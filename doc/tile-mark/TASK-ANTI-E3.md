# Anti：E3 权威接线、原子费用与私有投影

前置：E2 不可变状态通过 Codex 检视。此批次完成 engine 的落汗调用闭环；不修改 protocol/server/client/presentation，不写或修改任何测试。测试由 Codex 写入 `TileMarkAuthorityContractTest`，你负责运行、修复生产代码及回调。以 `DESIGN.md` 为完整行为契约，以下固定本批次公开接口。

## 公开契约

1. `MatchConfig` 末尾追加 `tileMarkConfig: TileMarkConfig = TileMarkConfig()`。`validateSupported()` 在落汗启用时要求经济启用且 `costs["TILE_MARK"] == 10`；关闭时不新增费用约束。配置复制/大厅更新必须保留该字段。
2. `TableAuthority.submitTileMark(seat: Int, request: TileMarkRequest, now: Long): MutationResult`。与其他写方法同样串行。`coordinator` 仍 internal。
3. `MatchSession.tileMarkState: TileMarkState` 只读属性，通过 SessionChange 原子候选安装；外部不能直接替换。`SessionChange` 可追加可空候选字段，null 表示保留当前值，旧变更不能清空状态。
4. `PlayerSnapshot` 末尾追加 `tileMark: TileMarkView? = null`，关闭时 null，开启时仅当前观察者私有信息。任何公开投影/公开结果不加落汗字段。
5. `com.majiang.cheat.mark` 定义不可变且防御复制的视图：
   - `enum class TileMarkArea { OWN_HAND, OPPONENT_HAND, DISCARD, EXPOSED_MELD, EXPOSED_INDICATOR, NOT_VISIBLE }`
   - `MarkedTileView` 提供 `tileId: Int`、`tile: Tile`、`area: TileMarkArea`、`seat: Int?`、`riverPosition: RiverPosition?`。
   - `TileMarkGroupView` 提供 `groupId`、`createdRoundId`、`createdRoundOrdinal`、`expiresAfterRoundOrdinal`、`futureRoundsRemaining`、`isLastEffectiveRound`、`tiles: List<MarkedTileView>`。
   - `TileMarkView` 提供 `canApply: Boolean`、`disabledReason: String?`、`cost: Int`（10）、`activeTileCount: Int`、`maxActiveTiles: Int`（12）、`selectableTileIds: List<Int>`、`decisionId: String`、`controlEpoch: Long`、`stateVersion: Long`、`activeGroups: List<TileMarkGroupView>`。

## 原子写入和计时

严格验证配置、sessionId/roundId/decisionId/controlEpoch、真人控制权、本人可弃牌阶段、未立直、未暂停、有效对应计时且 `now < deadline`、4 张本人当前手牌不同实体、未重复有效标记、12 张容量及余额至少 10。

所有实际变化在 ApplicationCoordinator 现有锁及处理守卫内完成。先准备并审计单局扣费候选和 Session 标记候选，再安装、发布事件；不能先调用会独立提交的 `applyCheatLedgerEntry` 再创建组。保留 136 张位置和麻将积分，不推进出牌动作。费用事件沿用 CheatPointsChanged，不能在公开事件中包含标记实体列表。

沿用 ledger 操作的 `DecisionClock.rekey`，保留 opensAt/deadline/generation/储备。不能先 complete/cancel 再重新 open，缺少有效计时必须拒绝。每次施术生成新决策，后续弃牌必须使用新决策。

错误码遵循设计：TILE_MARK_DISABLED、WRONG_PHASE/NOT_YOUR_TURN、TILE_MARK_RIICHI_LOCKED、TILE_MARK_INVALID_SELECTION、TILE_MARK_TILE_NOT_OWNED、TILE_MARK_ALREADY_MARKED、TILE_MARK_CAPACITY_EXCEEDED、INSUFFICIENT_FUNDS、DECISION_MISMATCH、STALE_CONTROL_EPOCH、DEADLINE_EXPIRED、HOST_PAUSED、REQUEST_ID_CONFLICT。请求身份不匹配不能错误地施术。

权威回执按座位+requestId 记录操作类型与载荷指纹，合法选牌按排序规范化；重试相同请求返回原回执、stateChanged=false。到期后重试不能复活；同一 ID 换载荷或换为普通 choice/shuffle 写操作必须冲突。检查既有 storedReceipts/storedShufflePlans 与 RequestRegistry，不新增互相绕过的孤立缓存，保持既有有效用例。

## 生命周期接入

E2 状态由 MatchSession 持有。初始已发牌 GameState 登记一次；构造 STACKING 状态不登记。普通实际发牌和码牌 `applyPreparedWallAndDeal` 成功提交时，在同一提交里登记 roundId；StartNextRound 提前创建 STACKING 时不消耗期限。准备失败不计数。

接受真实单局结果时调用 completeRound，重复结算幂等；FINISHED/CLOSED 清空所有组。Pause/Resume/普通账本/座位控制变化保留组状态。同座位重连与接管不重置期限。不能公开任意外部替换状态的 setter 来绕过原子路径。

## 投影

从同一已提交版本生成观察者投影；查当前实体所属容器。牌河必须在该 seat 当前 discards 列表按 tileId 定位，riverPosition 的 roundId、seat、discardIndex 与同一快照完全匹配。鸣牌后清除牌河位置，变为公开副露；不使用 discardHistory 索引。不在对手手牌发送真实排序或摸牌标志。牌山/未公开王牌/袖牌/未实际发牌统一 NOT_VISIBLE，不携带隐藏座位或索引。局间保留组但不沿用上一局位置。

只有当前可施术真人座位提供 selectableTileIds，并排除本人已标记牌。其他观察者只得到自己的组；公开投影没有私有标记字段。对手暗杠若原来未公开具体实体，不借落汗扩大授权区域。深复制快照、组列表及可选牌，旧快照不随移动或到期变化。

## 交付

运行落汗测试及完整 engine 测试，失败修改生产实现后复测，不能删测或改测试来通过。如果 Codex 测试本身有编译或夹具错误，准确报告并等待 Codex 修正，不自行改测试。

更新 `doc/tile-mark/PROGRESS-ANTI.md`、`DELIVERY-ANTI-E3.md`、`CALLBACK-ANTI-E3.json`（recipient codex，status ready_for_review 或 blocked），列实际文件、接口和测试报告计数。最后输出 `CODEX_CALLBACK E3 ready_for_review` 并结束。允许构建/测试，agy 使用 `--dangerously-skip-permissions`。不要 Git 提交、另开代理或修改后续模块。
