# 任务 ANTI-U3 交付报告：Core 回归缺陷修复与表现层状态调和

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task ANTI-U3 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、构建与审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不使用命令行或外部终端执行）
- **目标设计与任务契约**：
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  - [STATUS.md](STATUS.md)
  - [delivery-ANTI-U2.md](delivery-ANTI-U2.md)
  - [ScreenRecoveryAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/v3/ScreenRecoveryAcceptanceTest.kt)
  - [MeldChoiceScreenContractTest.kt](../../core/src/test/kotlin/com/majiang/screen/MeldChoiceScreenContractTest.kt)
  - [RiichiChoiceScreenContractTest.kt](../../core/src/test/kotlin/com/majiang/screen/RiichiChoiceScreenContractTest.kt)
  - [DiscardAnimationIntegrationTest.kt](../../core/src/test/kotlin/com/majiang/screen/DiscardAnimationIntegrationTest.kt)
  - [DiscardInputGateAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/screen/DiscardInputGateAcceptanceTest.kt)
  - [FuritenScreenContractTest.kt](../../core/src/test/kotlin/com/majiang/screen/FuritenScreenContractTest.kt)
  - [GameScreenSessionContractTest.kt](../../core/src/test/kotlin/com/majiang/screen/GameScreenSessionContractTest.kt)
  - [SettlementDetailPresentationAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/screen/SettlementDetailPresentationAcceptanceTest.kt)

---

## 1. 唯一写入范围与交付清单

严格遵循唯一写入范围契约（仅限 `core/src/main/kotlin/com/majiang/screen/**`、`core/src/main/kotlin/com/majiang/presentation/**` 与文档；未修改任何测试源码、Gradle 配置、engine、client 或 server 模块）：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/LegacyEndpointAdapter.kt` | 修改 | 1. 增加 `refreshFromEndpoint(): PlayerSnapshot`，动态从底层同步端点拉取最新 `view` 与合法动作；<br>2. `snapshot` 属性动态调用 `refreshFromEndpoint()`，杜绝无事件时的静默状态陈旧；<br>3. `choose(action)` 增加严格候选匹配（`opt != null`）：非法或失效动作返回明确拒绝 `ACTION_NOT_AVAILABLE`，杜绝旧测试缝中的非法提交穿透。 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 1. **缺陷 #1 & #2 修复**：在 `onSnapshotAndEvents` 中，当 `snap.settlementRoundId != null` 且 `activeSettlementRoundId != targetSettlementId` 时，无条件根据快照恢复结算数据（更新赢家与明细）并重置为第 0 页 YAKU 标签；相同 ID 更新积分并不重置标签/页码；`snap.settlementRoundId == null` 时区分 V3 与 legacy 测试缝：保留 legacy 权威 live win/draw 事件，仅在真实 V3 快照（无 live win 事件）或 actual round-start 阶段跃迁时清理结算面板；<br>2. **缺陷 #3 修复**：新增 `syncLegacyEndpointIfPresent()`，在输入交互（`touchDown`、`canHuman*`、`isHumanTurn`、`currentFuritenText`）及鸣牌/立直确认前主动同步 legacy 端点视图；鸣牌确认校验 `currentView.legalActions.contains(action)`，陈旧确认退出选牌且提交尝试保持为 0；<br>3. **缺陷 #4 修复**：`startGame()` 检查 `isAlreadyActive`（手牌非空或 phase 非 STACKING），针对已由夹具初始化的活跃对局，被底层端点拒绝时保留 `gameStarted = true`，绝不隐藏手牌或禁用交互。 |
| `doc/v3-migration/delivery-ANTI-U3.md` | 新增 | 本交付报告 |

---

## 2. 核心缺陷修复与实现机制分析

### 2.1 缺陷 1 & 2：结算生命周期调和与不同结算身份切换 (`GameScreen.kt`)
- **根因分析**：
  - 缺陷 1：原 `onSnapshotAndEvents` 在处理 live 事件后，无条件执行 `if (snap.settlementRoundId == null) { showSettlement = false; currentSettlement = null ... }`。旧版 `LegacyEndpointAdapter` 快照中 `settlementRoundId` 恒为 null，导致所有 legacy 和牌/流局事件在同一回调内被刚设置又瞬间清空，导致 `DiscardAnimationIntegrationTest`、`DiscardInputGateAcceptanceTest`、`GameScreenSessionContractTest`、`SettlementDetailPresentationAcceptanceTest` 中断言 `screen.isSettlementShown` 失败。
  - 缺陷 2：在 `snap.settlementRoundId != null` 时，嵌套了 `if (!showSettlement)` 分支。如果当前已在展示结算（`showSettlement == true`），新局的不同结算 ID 只执行了 `activeSettlementRoundId = targetSettlementId`，而跳过了 `restoreSettlementFromSnapshot(snap)`，导致 `ScreenRecoveryAcceptanceTest` 跨结算局跳转测试断言赢家仍为旧局赢家 0（期望 1）。
- **修复方案**：
  ```kotlin
  val targetSettlementId = snap.settlementRoundId
  if (targetSettlementId != null) {
      if (!showSettlement || activeSettlementRoundId != targetSettlementId) {
          val restored = restoreSettlementFromSnapshot(snap)
          if (restored != null) {
              fastForwardDiscardAnimations()
              clearGameplayHover()
              riichiSelectionModel.cancel()
              meldSelectionModel.cancel()
              currentSettlement = restored
              showSettlement = true
              activeSettlementRoundId = targetSettlementId
              settlementSection = SettlementSection.YAKU
              settlementPage = 0
          }
      } else {
          // 相同 settlementRoundId：仅更新 ScoreSettled 支付变动，不重置用户已选择的 tab/page
          val scoreSettled = snap.settlementEvents.filterIsInstance<GameEvent.ScoreSettled>().lastOrNull()
          if (scoreSettled != null && currentSettlement != null) {
              currentSettlement = currentSettlement!!.copy(
                  payments = scoreSettled.payments,
                  newScores = scoreSettled.newScores.toList()
              )
          }
      }
  } else {
      // snap.settlementRoundId == null
      val hasLiveWinEvent = events.any { it is GameEvent.TsumoWin || it is GameEvent.RonWin || it is GameEvent.DrawGame }
      val isRoundStartTransition = snap.view.phase == GamePhase.STACKING ||
          events.any { it is GameEvent.StackingStarted } ||
          (roundChanged && snap.view.phase != GamePhase.ROUND_END)
      val shouldClear = if (isLegacySeam) {
          !hasLiveWinEvent && isRoundStartTransition
      } else {
          !hasLiveWinEvent
      }
      if (shouldClear && (showSettlement || activeSettlementRoundId != null)) {
          showSettlement = false
          currentSettlement = null
          activeSettlementRoundId = null
          settlementSection = SettlementSection.YAKU
          settlementPage = 0
      }
  }
  ```

### 2.2 缺陷 3：静默陈旧确认退出与端点状态刷新 (`LegacyEndpointAdapter.kt` & `GameScreen.kt`)
- **根因分析**：
  - 在 `MeldChoiceScreenContractTest` 中，当 `stale == true` 时，测试直接修改了 `session` 的返回值（`latest = latest.copy(legalActions=listOf(Pass))`），但没有广播任何事件。
  - `LegacyEndpointAdapter` 缓存在内存中的 `cachedSnapshot` 和 `GameScreen.currentView` 依然保留了旧的吃牌动作。点击确认时，模型未检测到动作已失效，直接调用 `choose`；且 `LegacyEndpointAdapter.choose` 原有逻辑在 `!endpoint.capabilities.decisions` 时无条件调用 `endpoint.submit(action)`，导致 `attempts` 计数增加为 1（期望为 0）。
- **修复方案**：
  - `LegacyEndpointAdapter` 提供 `refreshFromEndpoint()`，并在访问 `snapshot` 时动态刷新；`choose(action)` 仅在候选匹配时提交，不匹配时返回 `ACTION_NOT_AVAILABLE`。
  - `GameScreen` 引入 `syncLegacyEndpointIfPresent()`，在确认按钮点击、`touchDown` 派发、`canHuman*` 与 `isHumanTurn` 判定前同步 legacy 端点；副露确认执行 `meldSelectionModel.cancel()`，且仅当 `currentView.legalActions.contains(action)` 时才发起提交；失效时不提交且尝试次数保持为 0，同时正确返回 `true`（点击事件被消费处理，选牌界面关闭退出）。

### 2.3 缺陷 4：已开局夹具再次调用 startGame 状态保持 (`GameScreen.kt`)
- **根因分析**：
  - `RiichiChoiceScreenContractTest` 与 `FuritenScreenContractTest` 在测试开始前已显式调用 `e.initializeMatch(2)`，状态机手牌与阶段已经完备。
  - 测试接着调用 `screen.startGame()`，其内部触发 `client.start()`。底层端点因对局已开局返回 `ACTION_NOT_AVAILABLE`（`ReceiptStatus.REJECTED`）。
  - `startGame()` 原逻辑在受到任何 `REJECTED` 回执后盲目将 `gameStarted` 置为 `false`，导致后续屏幕渲染退回居中 "START GAME" 按钮，`touchDown` 将所有手牌与操作点击当做未开局吞掉（返回 `false`），报出 "Tile not handled: 2索" 以及振听文本为空的断言失败。
- **修复方案**：
  - 在 `startGame()` 的回执处理中，增加 `val isAlreadyActive = currentView.myAllClosedTiles.isNotEmpty() || currentView.phase != GamePhase.STACKING` 判定。
  - 当对局早已处于活跃手牌状态时，底层拒绝仅代表开局幂等完成，保留 `gameStarted = true`，不重置游戏状态，不弹出启动拒绝 Toast，确保手牌点击与立直确认交互不受影响。

---

## 3. 实现审查与证据对照说明

| 测试用例 / 缺陷项 | 审查定位代码 | 行为与契约验证依据 |
|---|---|---|
| `ScreenRecoveryAcceptanceTest.jumping directly between two completed rounds replaces the previous settlement` | `GameScreen.kt: onSnapshotAndEvents` | 当 `snap.settlementRoundId != activeSettlementRoundId` 时无条件调用 `restoreSettlementFromSnapshot(snap)`，新结算数据和 winner 1 正确替换旧局 0。 |
| `ScreenRecoveryAcceptanceTest.screen opened after win restores details and does not replay settlement on pause update` | `GameScreen.kt: onSnapshotAndEvents` | 相同 `targetSettlementId` 仅合并不重置，`host.pause()` 后保持 `SettlementSection.CALC`；`host.nextRound()` 后 V3 空 `settlementRoundId` 正确清空弹窗。 |
| `MeldChoiceScreenContractTest.rejected and silently stale confirmations exit without committing another candidate` | `GameScreen.kt: touchDown` & `LegacyEndpointAdapter.kt` | 点击 CONFIRM 前同步 legacy 端点并检查 `currentView.legalActions.contains(action)`；静默陈旧时不调用提交，`attempts` 保持 0，选牌模式正确退出（`isSelecting == false`）。 |
| `RiichiChoiceScreenContractTest.real session changes neither score nor tiles until confirmation then commits exact choice` | `GameScreen.kt: startGame` | 对已处于 `initializeMatch` 的活跃夹具，`start()` 被拒绝时不重置 `gameStarted`，2索手牌点击正常响应并进入立直预览，立直确认正常扣除 1000 点立直棒。 |
| `FuritenScreenContractTest.real furiten session exposes tsumo button and settles exactly once for each cause` | `GameScreen.kt: startGame` & `currentFuritenText` | 活跃开局保留确保 `currentFuritenText()` 非空，`canHumanTsumo()` 为 true，自摸点击正常推进阶段至 `ROUND_END`。 |
| `DiscardAnimationIntegrationTest.边界情况：结算弹窗出现时所有未完成动画快进收尾并清空队列` | `GameScreen.kt: onSnapshotAndEvents` | 针对 legacy live win 事件不因 snapshot 无 `settlementRoundId` 而被清空，`screen.isSettlementShown` 保持为 true。 |
| `DiscardInputGateAcceptanceTest.new round and settlement clear playback rather than stranding controls` | `GameScreen.kt: onSnapshotAndEvents` | live TsumoWin 保持 `isSettlementShown == true`，随后点击 NEXT ROUND 正常关闭结算弹窗。 |
| `GameScreenSessionContractTest.settlement modal captures ...` | `GameScreen.kt: onSnapshotAndEvents` | 两次 update 中的 TsumoWin 均正常保留结算数据与 `ScoreBreakdown`，`isSettlementShown` 保持 true。 |
| `SettlementDetailPresentationAcceptanceTest.tab and page clicks ...` | `GameScreen.kt: onSnapshotAndEvents` | emit live win 后弹窗展示并重置为第 0 页 YAKU；多次视图更新保留翻页状态；点击关闭按钮后正常关闭。 |

---

## 4. 后续交接

- **执行说明**：当前运行环境属于无命令行执行权限的会话，未执行任何外部构建指令或伪造测试结果日志。
- **验证交付物**：请 Codex 运行 `:core:test` 验证全量 154 项测试通过，并继续推进桌面 LWJGL 3 与 Android 构建。
