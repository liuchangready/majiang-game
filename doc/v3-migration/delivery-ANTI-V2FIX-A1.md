# 交付报告：ANTI-V2FIX-A1 小批修复

## 1. 任务概述

- **任务编号**：ANTI-V2FIX-A1
- **工作区**：`D:/majiang`
- **修改范围**：
  - `core/src/test/kotlin/com/majiang/GdxHeadlessTestRunner.kt`
  - `core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt`
- **任务目标**：修复 FakeScreenClient 的 V3 快照游标、单局 ID 演进、终局结算恢复事件保留、以及 UI 线程调用 flushUi 自锁问题。保持全部公开 API 兼容。

---

## 2. 变更详情

### 2.1 `GdxHeadlessTestRunner.kt`
- **UI 线程确定性记录**：在 `HeadlessApplication` 创建的 `ApplicationAdapter.create()` 回调中捕获 `Thread.currentThread()` 并存入 `@Volatile var uiThread: Thread?`。
- **免猜测判定**：通过 `CountDownLatch(1)` 确保 `initGdx()` 返回前 UI 线程对象已就绪；提供 `isUiThread(): Boolean`，直接比对 `Thread.currentThread() === uiThread`，杜绝通过线程名猜测引发的误判。

### 2.2 `FakeScreenClient.kt`
- **游标与单局 ID 动态演进**：
  - `cursorSeq` 在每次 `update()` 时单调递增，`PlayerSnapshot.cursor`、`sessionVersion`、`roundVersion` 与 `decision.cursor` 同步更新。
  - 追踪单局状态变更（`nextRound()` 调用、风/局/本场变化、结算后进入新局等），进入新局时自增局次序号生成新 `roundId` 并清空旧局结算事件。
- **终局恢复事件（Settlement Events）保全**：
  - 接入 `GameEvent.isSettlementEvent()`，识别 `TsumoWin`、`RonWin`、`DrawGame`、`ScoreSettled`、`RoundEnded`、`MatchEnded` 等终局结算事件。
  - 在同一局内累加并保留终局事件，后续接收空事件列表的更新不会清空已存的结算事件；有结算事件时填充 `settlementRoundId = currentRoundId`，进入新局时方重置清空。
- **自锁消除与两层 FIFO Barrier**：
  - 在 `flushUi()` 中通过 `GdxHeadlessTestRunner.isUiThread()` 判断当前线程：若在 UI 线程调用则立即返回，彻底避免 UI 线程等待自身导致的死锁。
  - 在非 UI 测试线程中，通过两层嵌套 `app.postRunnable` 确保排空当前帧及本帧中派发的下一帧嵌套任务，并在 3 秒内完成等待。
  - 移除 `start()`、`choose()`、`nextRound()` 内部的 `flushUi()` 调用（避免在调用方尚未注册 `whenComplete` 处理器前过早 flush，并由测试调用方在输入后按需 flush）。
- **快照身份保护与防御性复制**：
  - `publish(snap)` 完整保留传入快照的身份元数据（`sessionId`、`roundId`、`cursor`、`settlementRoundId` 等），不进行默认值覆盖，并同步内部单局状态。
  - `snapshot` getter 与内部快照存储均使用 `.deepCopy()` 隔离，防止外部状态篡改。
- **公开 API 保持**：保留并扩展 `PlayerView.toSnapshot(events)`，支持携带终局结算事件。

---

## 3. 验收与交付物

- `core/src/test/kotlin/com/majiang/GdxHeadlessTestRunner.kt`：已更新。
- `core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt`：已更新。
- 本任务未修改任何其他业务或测试文件，未执行 shell 命令，代码已准备就绪，移交给 Codex 进行编译与测试验证。
