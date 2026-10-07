# 任务 ANTI-U2 交付报告：UI 验收反馈、终端恢复投影与暂停态治理

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task ANTI-U2 生产代码实现)
- **协作模式**：Codex 负责测试设计、构建与检视；Antigravity 负责生产代码实现（严格使用内置文件读写工具，不执行外部 Shell/终端命令）
- **目标设计与任务契约**：
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  - [TASK-GLM-S.md](TASK-GLM-S.md)
  - [delivery-ANTI-U1.md](delivery-ANTI-U1.md)
  - [ScreenRecoveryAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/v3/ScreenRecoveryAcceptanceTest.kt)
  - [AsyncScreenAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/v3/AsyncScreenAcceptanceTest.kt)
  - [V3RenderSmoke.java](../../tools/architecture/V3RenderSmoke.java)

---

## 1. 交付变更清单

本次 Task ANTI-U2 交付严格遵守唯一写入范围契约（`core/src/main/kotlin/com/majiang/presentation/**`、`core/src/main/kotlin/com/majiang/screen/**` 与文档），未改动任何测试代码、Gradle 构建配置、engine、client 或 server 模块代码：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt` | 修改 | 修复决策超时后储备时间回退显示为初始储备的缺陷；以 `deadline <= opensAt` 区分闲置与活跃；活跃决策到期后呈现 0.0s 储备与警戒状态；计算采用非负本地耗时 |
| `core/src/main/kotlin/com/majiang/presentation/V3GameClientAdapter.kt` | 修改 | 席位托管状态 `isTakeover` 接入权威快照 `PlayerSnapshot.controls`（本席位 role == "TAKEOVER"）；仅在无控制投影（如旧测试夹具）时回退至本地请求标记；权威快照优先于本地标记 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 1. 修复 `defaultEmptyPlayerView` 中不存在的 `GamePhase.ROUND_START` 回退为 `GamePhase.STACKING`；<br>2. `runOnUi` 保留旧版测试缝 `LegacyEndpointAdapter` 同步内联执行路径，真实 V3 客户端保持跨线程派发；<br>3. 实现从 `snapshot.settlementEvents` 与 `settlementRoundId` 恢复结算面板（保留和牌拆解 ScoreBreakdown 与 ScoreSettled 支付及积分），后续时钟/暂停更新不重置用户选中的标签与页码，新一局自动清理过时结算；<br>4. 游标跳跃/新代次/局切换时清空旧打牌动画并同步基准牌河，杜绝重播历史快照打牌动画；<br>5. 接入权威暂停态 `snapshot.paused`：冻结倒计时（耗时计算置 0）、禁用全部玩法操作按钮与交互输入、状态盒呈现醒目 `PAUSED` 提示与黄色框线、操作区渲染暂停提示横幅 |
| `doc/v3-migration/delivery-ANTI-U2.md` | 新增 | 本交付报告 |

---

## 2. 核心缺陷修复与契约落地细节

### 2.1 修复编译缺失与枚举回退 (`GameScreen.kt`)
- **遗留问题**：
  - `GameScreen.defaultEmptyPlayerView(seat)` 中回退视图的初始 phase 引用了不存在的 `GamePhase.ROUND_START`。
  - `LegacyEndpointAdapter` 已在 U1 中补齐 `safeQueryDecision()`，其在 `capabilities.decisions` 为 false 时支持旧测试端点的合法动作映射。
- **修复实现**：
  - 将 `GameScreen.defaultEmptyPlayerView` 中的 `phase` 修改为权威初始阶段 `GamePhase.STACKING`。
  - 保留旧显式测试端点在缺乏决策候选支持时的原始提交能力（`submit(action)`），同时维持 V3 异步客户端严格候选选择路径。

### 2.2 权威托管状态投影 (`V3GameClientAdapter.kt`)
- **契约要求 (ANTI-U2 #2 & ScreenRecoveryAcceptanceTest)**：
  - 使用 `PlayerSnapshot.controls` 判定 `isTakeover`（自身席位 role == "TAKEOVER"）；
  - 仅在旧测试夹具无控制投影时回退至 `localTakeoverFlag`；
  - 权威快照优先于本地请求标记，保证新连入的客户端以及重连客户端能如实恢复并呈现真实控制权。
- **修复实现**：
  ```kotlin
  override val isTakeover: Boolean
      get() {
          val controls = snapshot?.controls
          val ownControl = controls?.firstOrNull { it.seat == viewerSeat }
          if (ownControl != null) {
              return ownControl.role == "TAKEOVER"
          }
          return localTakeoverFlag.get()
      }
  ```

### 2.3 终端快照恢复与过时结算清理 (`GameScreen.kt`)
- **契约要求 (ANTI-U2 #3 & ScreenRecoveryAcceptanceTest)**：
  - 在初始终端快照或 resume（甚至增量事件为空）时，根据 `snapshot.settlementEvents` 与 `settlementRoundId` 恢复结算面板；
  - 完整保留和牌拆解 `ScoreBreakdown` 与 `ScoreSettled` 支付转移和终局积分；
  - 不得在后续时钟或控制更新快照中反复重播结算结果，亦不得覆盖用户已选择的结算 Tab/页码（如 `SettlementSection.CALC`）；
  - 局内和牌事件仅处理一次；终端快照是数据恢复源，不触发二次打牌/动画重演；
  - 新一局快照（`snap.settlementRoundId == null`）必须自动清理过时结算面板，即便利息错过了 `StackingStarted` 事件；
  - 断线重连或游标跳跃（`snap.cursor > lastCursor + 1`）时，清空旧打牌动画并不对历史快照差异触发动画补播。
- **修复实现**：
  - 引入 `activeSettlementRoundId` 跟踪当前已呈现/恢复的结算单局 ID；
  - 实现 `restoreSettlementFromSnapshot(snap: PlayerSnapshot): SettlementData?`，按顺序解析 `TsumoWin`、`RonWin`、`DrawGame` 与 `ScoreSettled`，保留完整 breakdown，缺失 newScores 时安全回退至快照积分；
  - `onSnapshotAndEvents` 调度逻辑：
    - 若 `snap.settlementRoundId != null`：
      - 若 `!showSettlement`，执行 `restoreSettlementFromSnapshot`，初始化 `showSettlement = true`、`activeSettlementRoundId = snap.settlementRoundId`、`settlementSection = SettlementSection.YAKU`；
      - 若已处于该结算局中，仅合并更新可能到达的 `ScoreSettled`，绝不重设 `showSettlement`、`settlementSection` 或 `settlementPage`；
    - 若 `snap.settlementRoundId == null`，检测到进入新局或活跃对局，立即重置 `showSettlement = false`、`currentSettlement = null`、`activeSettlementRoundId = null`；
  - 动画防护：当检测到 `epochChanged || roundChanged || cursorJumped` 时，调用 `fastForwardDiscardAnimations()`，将 `lastDiscards` 直接同步为快照当前牌河，并跳过本次 `checkDiscardsDiff(events)`，彻底杜绝历史牌河差异补播。

### 2.4 暂停状态输入熔断与时钟冻结 (`ClockDisplay.kt` & `GameScreen.kt`)
- **契约要求 (ANTI-U2 #4 & ScreenRecoveryAcceptanceTest)**：
  - `ClockDisplay` 超时显示修复：原逻辑将 `remainingTotal <= 0` 判定为 `!hasActiveTiming`，导致到期后错误恢复初始储备显示；修改为只要 `deadline > opensAt` 即为活跃计时，到期后储备显示为 `0.0s` 且 `isUrgent = true`；闲置 0/0 保持初始储备显示；计算引入非负本地耗时；
  - 权威暂停态熔断：读取 `snapshot.paused`；倒计时显示本地增量耗时冻结为 `0L`（直接采用 S1 冻结的 `serverNow`）；
  - 禁用全部玩法操作输入与手牌交互，`canHuman*` 方法在暂停时恒定返回 `false`；状态盒标注醒目黄色暂停标识，操作区渲染居中暂停提示。
- **修复实现**：
  - `ClockDisplay.kt`：
    ```kotlin
    val isIdle = clockView.deadline <= clockView.opensAt
    if (isIdle) {
        val reserveSec = reserveAtStart / 1000f
        return FormattedClock(hasActiveTiming = false, freeSeconds = 0L, reserveSeconds = reserveSec, displayText = "RESERVE: ...", isUrgent = false)
    }
    val elapsed = maxOf(0L, localElapsedMillis)
    val estServerNow = clockView.serverNow + elapsed
    val remainingTotal = maxOf(0L, clockView.deadline - estServerNow)
    // 免费时间与储备时间按实际消耗递减，耗尽保持 0.0s 且 isUrgent = true
    ```
  - `GameScreen.kt`：
    - 增加 `val isPaused: Boolean get() = currentSnapshot?.paused == true`；
    - `canHumanTsumo()`, `canHumanRon()`, `canHumanPass()`, `canHumanPon()`, `canHumanChi()`, `canHumanKan()`, `canHumanRiichi()`, `canHumanAgariYame()`, `canHumanAbortiveDraw()`, `isHumanTurn()` 全面增加 `!isPaused` 门禁；
    - `submitActionAsync` 若处于 `isPaused` 立即返回 `ReceiptStatus.REJECTED("HOST_PAUSED")`；
    - `touchDown` 拦截暂停状态下的所有桌面手牌与鸣牌/立直交互；
    - `renderPlayerStatusBox` 在暂停时将本地耗时置为 `0L`，边框使用琥珀黄色，文字标注 `PAUSED - ${formatted.displayText}`；
    - `renderActionButtons` 在暂停时绘制专用提示 `MATCH PAUSED - INPUT DISABLED`。

### 2.5 旧版测试缝与实时 UI 线程派发区分 (`GameScreen.kt`)
- **契约要求 (ANTI-U2 #6)**：
  - U1 早期实现对所有存在 `Gdx.app` 的场景均使用 `app.postRunnable`；
  - 原有显式测试缝（如 `GameScreenTest` 使用 `LegacyEndpointAdapter`）依赖确定性同步内联回调；
  - 保留旧版直接路径，同时确保真实 V3 客户端将跨线程回调派发至展示线程。
- **修复实现**：
  - 引入 `isLegacySeam`:
    ```kotlin
    private val isLegacySeam: Boolean
        get() = client is LegacyEndpointAdapter
    ```
  - `runOnUi(action)` 当且仅当 `app != null && !isLegacySeam` 时使用 `app.postRunnable`；对于 `LegacyEndpointAdapter` 保持直接同步调用，使现有无头测试保持确定性。

---

## 3. 验收标准覆盖说明

| 契约要求 | 对应代码与实现 | 验收覆盖机制 |
|---|---|---|
| 枚举与决策引用检查 | `GameScreen.kt: defaultEmptyPlayerView` | 修复不存在的 `ROUND_START` 为 `STACKING`，消除潜在编译/运行时异常 |
| 席位控制优先权威快照 | `V3GameClientAdapter.kt: isTakeover` | `snapshot.controls` 判定自身角色为 `TAKEOVER`，满足 `ScreenRecoveryAcceptanceTest` 夺权/收回控制权用例 |
| 终端快照和牌结果恢复 | `GameScreen.kt: restoreSettlementFromSnapshot` | 连入即呈现和牌弹窗，保留 `breakdown` 与积分；满足 `ScreenRecoveryAcceptanceTest` 初始终态恢复断言 |
| 暂停时不重播结算/不重置Tab | `GameScreen.kt: onSnapshotAndEvents` | 匹配 `activeSettlementRoundId`，不覆盖用户已选的 `SettlementSection.CALC` |
| 新局快照清理过时结算 | `GameScreen.kt: onSnapshotAndEvents` | `snap.settlementRoundId == null` 自动重置结算状态与数据为 null |
| 游标跳跃清空并抑制动画 | `GameScreen.kt: onSnapshotAndEvents` | `cursorJumped` 立即快进动画并对齐牌河，跳过快照差异检测 |
| 倒计时超时显示 0.0s 警戒 | `ClockDisplay.kt: format` | 超时后显示 0 储备与紧急警戒状态；满足 `ScreenRecoveryAcceptanceTest` 时钟用例 |
| 暂停状态冻结倒计时与输入 | `GameScreen.kt: renderPlayerStatusBox & canHuman*` | 暂停时本地流逝时间置 0，输入熔断；满足 `ScreenRecoveryAcceptanceTest` 禁用自摸断言 |
| 旧测试缝保留同步路径 | `GameScreen.kt: runOnUi` | `LegacyEndpointAdapter` 内联同步执行，维持 `GameScreenTest` 确定性 |

---

## 4. 交付总结与后续交接

- **完成状态**：Task ANTI-U2 生产代码已全部修改完毕，相关逻辑与接口签名完全匹配 V3 规范及 `ScreenRecoveryAcceptanceTest` 契约。
- **执行纪律**：在当前无命令行执行权限的会话中，未运行任何 shell、构建或测试指令，亦未伪造构建日志。
- **后续交接**：请 Codex 进行后续构建与回归验收测试（运行 `./gradlew test --info` 验证包括 `ScreenRecoveryAcceptanceTest`、`AsyncScreenAcceptanceTest` 与 `GameScreenTest`，以及 `V3RenderSmoke` 图形冒烟验证）。
