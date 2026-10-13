# 偷梁换柱测试与开发接口约定

2026-10-13，Codex与Anti协作使用。行为以[设计](DESIGN.md)为准；本文件固定命名，避免测试与实现并行时漂移。请求和值对象须有值相等、copy及必要的防御性集合复制。不得因接口约定放宽领域和线路校验。

## Engine（`com.majiang.cheat.beam`）

```kotlin
data class BeamSwapConfig(val enabled: Boolean = false)
enum class BeamSwapWindow { WALL, DEAD_WALL }
enum class BeamSwapAction { SWAP, KEEP_ORIGINAL }
sealed class BeamSwapSlot {
    data class WallSlot(val index: Int) : BeamSwapSlot()
    data class DeadWallSlot(val index: Int) : BeamSwapSlot()
    data class ReplacementSlot(val index: Int) : BeamSwapSlot()
}
// 标准身份五元组字段及类型与SleeveBloomRequest一致。
BeamSwapRequest(requestId: String, sessionId: String, roundId: String, decisionId: String, controlEpoch: Long)
BeamSwapChoiceRequest(requestId: String, sessionId: String, roundId: String, decisionId: String,
    controlEpoch: Long, offerId: String, action: BeamSwapAction,
    handTileId: Int? = null, targetSlot: BeamSwapSlot? = null)
BeamSwapCandidate(tileId: Int, slot: BeamSwapSlot)
BeamSwapOffer(offerId: String, seat: Int, window: BeamSwapWindow,
    candidates: List<BeamSwapCandidate>, eligibleHandTileIds: List<Int>, drawnTileId: Int, createdTurnNumber: Int)
BeamSwapCandidateView(slot: BeamSwapSlot, marked: Boolean, tile: Tile? = null)
BeamSwapOfferView(offerId: String, window: BeamSwapWindow, candidates: List<BeamSwapCandidateView>,
    selectableHandTileIds: List<Int>, deadlineMillis: Long, decisionId: String, controlEpoch: Long)
BeamSwapView(canApply: Boolean, disabledReason: String?, cost: Int = 10, window: BeamSwapWindow?,
    decisionId: String, controlEpoch: Long, stateVersion: Long, pendingOffer: BeamSwapOfferView? = null)
```

槽位index为对应容器绝对索引（非UI相对序号），非负；领域请求可携带非允许的非负索引以便权威拒绝。未知/非允许实体及槽位在权威拒绝且不得清除offer；分支结构非法可在构造/线路层拒绝。未标记候选 `marked=false,tile=null`，标记候选 `marked=true,tile!=null`，tile含本人已知实体牌面与ID。不得另加隐藏实体ID或全局候选表到私有视图。

`GameState.beamSwapOffer` / `beamSwapUsedOnTurn`、`MatchConfig.beamSwapConfig`、`PlayerSnapshot.beamSwap`及 `TableAuthority.submitBeamSwap/submitBeamSwapChoice`、协调器接口、`TileMovement.swapHandWithDeadWall` 均按DESIGN实现。测试使用上述入口，不依赖内部辅助算法命名。

## 后续模块命名

Protocol使用同构`ProtocolBeamSwap*`，客户端`client.api`使用`BeamSwap*`（候选tile为`TileView?`），不得引用engine模型。专用API、标签、能力开关与快照字段按DESIGN接线。若字段确需变更先写出原因并告知Codex，不要自行修改测试。

线路槽位固定JSON `{ "kind": "WALL" | "DEAD_WALL" | "REPLACEMENT", "index": 非负整数 }`。候选视图字段为`slot,marked,tile`，非标记不携带tile字段；标记才含ProtocolTile。SWAP必含handTileId、targetSlot；KEEP_ORIGINAL省略二者，显式null也拒绝。严格拒绝目标实体ID等未知请求字段，防止额外载荷绕过指纹与隐私契约。
