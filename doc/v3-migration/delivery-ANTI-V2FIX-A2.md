# 交付报告：ANTI-V2FIX-A2 (A2 编译与审查退回精准修复)

## 1. 任务概述

- **任务编号**：ANTI-V2FIX-A2
- **工作区**：`D:/majiang`
- **修改范围**（严格限定两处测试辅助文件）：
  - `core/src/test/kotlin/com/majiang/GdxHeadlessTestRunner.kt`
  - `core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt`
- **交付目标**：精准解决 Codex 在 A1 审查中指出的编译参数错误、非法枚举引用、异常吞噬以及单局与结算元数据同步逻辑缺陷，不改动任何其他业务或测试文件，不执行任何外部命令。

---

## 2. 变更详情与修复对照

### 2.1 PlayerDecision 构造签名与选项类型修复 (`FakeScreenClient.kt`)
- **问题根因**：`PlayerDecision` 定义在 `engine/.../application/ActionDecisionDto.kt`，其签名为主构造器 `PlayerDecision(roundId: String?, decisionId: String, controlEpoch: Long, options: List<ActionOption>)`，不存在 `cursor` 与 `legalActions` 参数，且无法直接传入 `List<GameAction>`。
- **修复方案**：
  - `init` 构造块中：`decision = PlayerDecision(roundId = currentRoundId, decisionId = "decision-$cursorSeq", controlEpoch = 0L, options = emptyList())`
  - `update()` 方法中：`decision = PlayerDecision(roundId = effectiveRoundId, decisionId = "decision-$snapCursor", controlEpoch = 0L, options = emptyList())`
  - `toSnapshot()` 扩展函数中：`decision = PlayerDecision(roundId, "fake-decision", 0L, emptyList())`
  - 三处均完成修复，完全符合只读测试 double 契约。

### 2.2 GamePhase 枚举名称修正 (`FakeScreenClient.kt`)
- **问题根因**：代码中误用了不存在的 `GamePhase.ROUND_OVER` 与 `GamePhase.GAME_OVER`。
- **修复方案**：修正为领域状态机的规范枚举 `GamePhase.ROUND_END` 与 `GamePhase.MATCH_END`。

### 2.3 杜绝异常吞噬与初始化假阳性 (`GdxHeadlessTestRunner.kt` & `FakeScreenClient.kt`)
- **问题根因**：
  - `GdxHeadlessTestRunner.initGdx()` 中的 `initLatch.await(3, TimeUnit.SECONDS)` 返回 `false` 时未校验，超时后继续执行并假装成功；
  - `FakeScreenClient.flushUi()` 中存在 `catch (_: Exception) {}` 吞噬超时或中断。
- **修复方案**：
  - 在 `GdxHeadlessTestRunner.initGdx()` 中加入显式校验：
    ```kotlin
    val acquired = initLatch.await(3, TimeUnit.SECONDS)
    check(acquired) { "Failed to initialize Gdx HeadlessApplication within 3 seconds" }
    ```
  - 在 `FakeScreenClient.flushUi()` 中移除 `try-catch` 块，直接调用 `done.get(3, TimeUnit.SECONDS)`，超时或中断由测试框架直接捕获失败。

### 2.4 新局识别与无事件刷新结算保全 (`FakeScreenClient.kt`)
- **问题根因**：此前在 `nextRound()` 请求触发后直接将 `pendingNewRound` 设为 `true`，导致后续同局结算刷新（无事件空列表更新）被误识别为新局，错误重置了局次并清空结算事件。
- **修复方案**：
  - 新局识别移除对孤立 `pendingNewRound` 的盲目信任，改由实际证据判定：
    1. 盘面局况参数变更：`view.roundWind != lastRoundWind || view.roundNumber != lastRoundNumber || view.honbaSticks != lastHonbaSticks`；
    2. 终态流转至非终态实际证据：`lastPhase in (ROUND_END, MATCH_END) && view.phase !in (ROUND_END, MATCH_END)`；
    3. 新局显式流程事件：`events.any { it is GameEvent.StackingStarted || it is GameEvent.DealingComplete }`。
  - 在同局内接收无事件刷新（`events = emptyList()`）或同局事件时，完整保留已记录的结算事件列表与当前结算局身份。

### 2.5 快照 Setter 元数据同步与旧局结算身份延续 (`FakeScreenClient.kt`)
- **问题根因**：
  - `snapshot` setter 外部直接赋值时仅更新 `_snapshot` 字段，未同步内部元数据游标与局状态；
  - `publish(snap)` 收到 `roundId == null` 且 `settlementRoundId != null` 的终局/局间快照时，未记录该结算身份，导致后续 `update()` 无法延续该局身份而错误产生新 ID。
- **修复方案**：
  - 抽取内部私有方法 `applySnapshot(snap: PlayerSnapshot)`，在 `snapshot` setter 中复用该逻辑进行完整元数据同步（不广播，不 flush UI）；
  - `publish(snap)` 统一复用 `applySnapshot(snap)` 并触发广播与 UI 刷新；
  - 在 `applySnapshot` 中，若 `snap.roundId == null` 但 `snap.settlementRoundId != null`，将 `currentRoundId` 和 `currentSettlementRoundId` 均保留为该旧局结算身份；后续 `update()` 在该局结算未开启新局时，延续保留该身份与 `settlementRoundId`，杜绝错误换 ID。

---

## 3. 交付清单

1. `core/src/test/kotlin/com/majiang/GdxHeadlessTestRunner.kt`（已修改）
2. `core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt`（已修改）
3. `doc/v3-migration/delivery-ANTI-V2FIX-A2.md`（本交付报告）

本次任务未执行任何终端或 Shell 命令，未修改任何其他业务代码及构建脚本，已交付供 Codex 独立编译与测试。
