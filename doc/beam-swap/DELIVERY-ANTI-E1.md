# 偷梁换柱 E1 领域实现交付报告 (DELIVERY-ANTI-E1.md)

- **任务标识**：BS-E1
- **提交人**：Anti (agy)
- **检视人**：Codex
- **日期**：2026-10-13
- **状态**：`ready_for_review`

---

## 1. 变更列表 (Modified & Added Files)

本次开发严格遵守单向依赖与模块边界规范，仅实现 `engine` 模块生产代码与交付文档，未修改任何测试代码，未执行 Git commit/push，完整保留未提交的 `SB-KAN-DORA` 与工作区既有改动：

### 1.1 新增模型与投影实现 (`engine/src/main/kotlin/com/majiang/cheat/beam/`)
1. `BeamSwapModels.kt`：
   - 核心契约值对象：`BeamSwapConfig(enabled = false)`、`BeamSwapWindow` (`WALL`, `DEAD_WALL`)、`BeamSwapAction` (`SWAP`, `KEEP_ORIGINAL`)、`BeamSwapSlot` (`WallSlot`, `DeadWallSlot`, `ReplacementSlot`，均校验 `index >= 0`)。
   - 请求与回执对象：`BeamSwapRequest`、`BeamSwapChoiceRequest`（校验身份五元组，`SWAP` 强制校验 `handTileId != null && targetSlot != null`，`KEEP_ORIGINAL` 强制校验两者为 `null`，禁止携带选牌字段）。
   - 领域状态与候选：`BeamSwapCandidate(tileId, slot)`、`BeamSwapOffer(offerId, seat, window, candidates, eligibleHandTileIds, drawnTileId, createdTurnNumber)`。
   - 私有视图对象：`BeamSwapCandidateView(slot, marked, tile)`、`BeamSwapOfferView(offerId, window, candidates, selectableHandTileIds, deadlineMillis, decisionId, controlEpoch)`、`BeamSwapView(canApply, disabledReason, cost = 10, window, decisionId, controlEpoch, stateVersion, pendingOffer)`。
2. `BeamSwapProjectionFactory.kt`：
   - 私有单观察者可见性投影工厂。
   - 绝不泄露未标记候选牌面与实体标识（`marked = false, tile = null`）；仅观察者本人有效落汗标记亮面（`marked = true, tile = Tile.fromTileId(c.tileId)`）。
   - 完整门禁原因推导：`HOST_PAUSED`、`BEAM_SWAP_ALREADY_PENDING`、`SLEEVE_BLOOM_ALREADY_PENDING`、`ACTION_NOT_AVAILABLE`、`NOT_YOUR_TURN`、`WRONG_PHASE`、`BEAM_SWAP_NO_DRAWN_TILE`、`BEAM_SWAP_NO_HAND_TILES`、`BEAM_SWAP_RIICHI_LOCKED`、`BEAM_SWAP_DEALER_EXTRA_EXCLUDED`、`BEAM_SWAP_ALREADY_USED_THIS_DRAW`、`BEAM_SWAP_NO_CANDIDATES`、`INSUFFICIENT_FUNDS`、`NO_ACTIVE_DECISION`、`DEADLINE_EXPIRED`。
   - 容器安全访问：在 STACKING 或王牌未发阶段安全边界校验，避免未发牌时越界。

### 1.2 领域与状态扩展
3. `engine/src/main/kotlin/com/majiang/model/TileMovement.kt`：
   - 实现物理原语 `swapHandWithDeadWall(state, player, handTile, targetSlot, expectedTileId): Tile`。
   - 严格校验 136 张守恒、手牌归属、有效死山暗牌区间，执行手牌与死山槽位原地对调及 `TilePool` 双向转移，自动重排暗手牌，保持 `drawnTile`、指针与替补列表张数不动。
4. `engine/src/main/kotlin/com/majiang/state/GameState.kt`：
   - 新增 `var beamSwapOffer: BeamSwapOffer? = null` 与 `var beamSwapUsedOnTurn: Int = -1`。
   - 接入 `resetForNewRound`（复位为 null 与 -1）、`copyInto` 与 `copyFrom` 深拷贝。
5. `engine/src/main/kotlin/com/majiang/state/StateInvariants.kt`：
   - 1.4 状态不变量强化：审计 `beamSwapOffer` 内部一致性（`seat in 0..3`、候选非空、有效暗手牌非空且不含摸牌）。
   - 互斥断言：`check(state.sleeveBloomOffer == null || state.beamSwapOffer == null)`，保证全局至多存在 1 个挂起千术 offer。
6. `engine/src/main/kotlin/com/majiang/match/MatchConfig.kt`：
   - 新增 `beamSwapConfig: BeamSwapConfig = BeamSwapConfig()`。
   - 在 `validateSupported()` 中强化校验：若 `beamSwapConfig.enabled == true`，必须同时开启千术经济且显式配置 `costs["BEAM_SWAP"] == 10`。
7. `engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt`：
   - 新增私有视图字段 `val beamSwap: BeamSwapView? = null`。
   - `init` 交叉安全校验：挂起 offer 必须属于本人 `CHEAT_WINDOW`、`view.myDrawnTile != null`、`decisionId` 与 `controlEpoch` 匹配、`selectableHandTileIds` 必须全在 `myClosedTiles` 且不包含 `myDrawnTile`。
   - `deepCopy()` 嵌套深复制支持。
8. `engine/src/main/kotlin/com/majiang/state/ActionResult.kt`：
   - 新增拒绝原因 `RejectionReason.BEAM_SWAP_CHOICE_PENDING`。
9. `engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt`：
   - `buildRoundResult` 中清理 `candidate.beamSwapOffer = null`。
10. `engine/src/main/kotlin/com/majiang/cheat/bloom/SleeveBloomModels.kt`：
    - `SleeveBloomProjectionFactory` 加严互斥检查：`state.beamSwapOffer != null` 拒绝并给出 `BEAM_SWAP_ALREADY_PENDING`；`beamSwapUsedOnTurn == state.turnNumber` 拒绝并给出 `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW`。
11. `engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkProjectionFactory.kt`：
    - `canApply` 增加门禁检查：若 `state.beamSwapOffer != null && seat == observerSeat` 返回 `BEAM_SWAP_CHOICE_PENDING`。

### 1.3 权威宿主与协调器接线
12. `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`：
    - 构造参数及属性接入 `beamSwapConfig: BeamSwapConfig`，并在 `updateLobbyConfig` 中同步。
    - `executeActionInternal` 顶部加严：若当前玩家存在挂起 `beamSwapOffer`，直接拒绝并返回 `BEAM_SWAP_CHOICE_PENDING`。
    - `submitTileMark` 门禁加严：若存在挂起 `beamSwapOffer`，拒绝并返回 `BEAM_SWAP_CHOICE_PENDING`。
    - `submitSleeveBloom` 门禁加严：检查 `beamSwapOffer != null` 与 `beamSwapUsedOnTurn == turnNumber`。
    - 新增 `submitBeamSwap(seat, request, now): ActionResult`：写锁内原子复核、扣费流水 `beam-$seat-$requestId`、安装 offer 并发布 `GameEvent.CheatPointsChanged`。
    - 新增 `submitBeamSwapChoice(seat, request): ActionResult`：写锁内全基线漂移校验（`isDrawKindConsistent`, `handConsistent`, `candidatesConsistent`），失效时原子清 offer；正常分支执行物理对调或 `KEEP_ORIGINAL`，清除 offer 并刷新决策。
    - 新增 `resolveBeamSwapKeepOriginal(seat): List<GameEvent>`：超时/自动摸切安全兜底，清除 offer 零物理变动。
    - 新增 `cleanStaleBeamSwapOffer(seat)`：局面漂移安全清理。
13. `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`：
    - 新增 `submitBeamSwap(seat, request, now): MutationResult`：涵盖 14 步完整校验链、幂等账（`SubmissionKind.BEAM_SWAP`）、冲突校验与时钟 `decisionClock.rekey()`。
    - 新增 `submitBeamSwapChoice(seat, request, now): MutationResult`：涵盖幂等、选择结构校验、全局基线漂移检测（若 phase 变为 DISCARD、来源改变、暗手牌/候选牌移动则以 `BEAM_SWAP_STALE_OFFER` 清理 offer 并延续 deadline）。
    - 加严 `submitSleeveBloom`：互斥拒绝 `BEAM_SWAP_ALREADY_PENDING` 与 `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW`。
    - 加严 `submitTileMark`：挂起检查包含 `beamSwapOffer`。
    - 更新 `checkTimeout`：在默认出牌决策前优先调用 `resolveBeamSwapKeepOriginal(seat)` 并 rekey。
    - 更新 `checkRiichiAutoDiscard`：在自动摸切前优先调用 `resolveBeamSwapKeepOriginal(currPlayer)` 并 rekey。
    - 更新 `syncDecisionsAndClocks`：若某座位有挂起 `beamSwapOffer`，AI 不调度决策。
    - 更新 `querySnapshot`：调用 `BeamSwapProjectionFactory.create(...)` 填充私有 `PlayerSnapshot.beamSwap`。
    - 更新 `sanitizeConfig`：安全复制 `beamSwapConfig`。

---

## 2. 接口与契约说明

遵循 `INTERFACES.md` 与 `DESIGN.md`：
- **物理守恒**：严格 136 张物理守恒。王牌区恒 14 张（未取岭上 + dora + replacements = 14）。第四杠后若处于 CHEAT_WINDOW 仍可使用 BeamSwap（暗死山候选依然存在）。
- **同摸牌二选一**：`beamSwapUsedOnTurn` 与 `sleeveBloomUsedOnTurn` 在 TableAuthority、Coordinator 与 ProjectionFactory 中全面联合判定，任一已使用则互斥拒绝且不扣费。
- **全局唯一 Offer**：任一千术 offer 挂起期间，另一千术激活、普通动作、落汗均被拒绝（返回对应 `*_CHOICE_PENDING` 或 `*_ALREADY_PENDING`）。
- **私有投影安全**：未标记候选仅暴露 slot，`tile = null`，不泄露实体 ID；仅本人有效落汗标记的候选亮面；他家快照无施术者的pendingOffer；功能开启时仍有各观察者自己的beamSwap权限视图。

---

## 3. 真实测试数量与结果

### 3.1 专项测试 (`*BeamSwap*`, `*SleeveBloom*`, `*TileMark*`)
- **执行命令**：`./gradlew :engine:test --tests "*BeamSwap*" --tests "*SleeveBloom*" --tests "*TileMark*"`
- **测试用例总数**：**131** 项
- **失败数 (failures)**：**0**
- **错误数 (errors)**：**0**
- **跳过数 (skipped)**：**0**
- **状态**：**BUILD SUCCESSFUL**

### 3.2 完整 engine 模块全量测试
- **执行命令**：`./gradlew :engine:test`
- **测试用例总数**：**471** 项
- **失败数 (failures)**：**0**
- **错误数 (errors)**：**0**
- **跳过数 (skipped)**：**0**
- **状态**：**BUILD SUCCESSFUL**

### 3.3 全项目编译测试
- **执行命令**：`./gradlew classes`
- **状态**：**BUILD SUCCESSFUL**（classes任务；不等同于Android APK构建，完整双端构建留至最终验收）

---

## 4. 未验证边界 (Unverified Boundaries)

按阶段划分，本阶段仅交付 engine 领域层，以下内容保留至后续阶段：
1. **BS-E2 线路与传输契约**：`protocol` 模块编解码（`ProtocolBeamSwap*` DTO、`ApiTags`）、`client` 模块门面（`GameSession.submitBeamSwap`、请求在途锁）、`server` 权威桌分发映射未在此阶段修改。
2. **BS-E3 表现层与 UI 呈现**：`presentation` 画面弹窗、候选暗牌牌背/亮面渲染、交互事件未在此阶段接入。
3. **公网与跨网络边界**：网络账户、持久化存储恢复不在系统范围内（符合 `AGENTS.md` 边界）。

---

## 5. 结论

BS-E1 领域实现已全部完成并通过全部 471 项 engine 测试，现将结果提交 Codex 进行独立测试与检视。
