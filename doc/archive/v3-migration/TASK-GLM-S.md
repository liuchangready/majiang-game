# GLM-S: complete authoritative recovery projection

Start only after H3 and P2 finish. Own engine/access/**, engine/host/TableHost.kt, client/network/protocol/**. Do not edit GameClient/sync, transport/server, core, tests/build. Other workers completed their files before this starts. Read actual sources and GAME_ARCHITECTURE_V3.md §9–10.

Review found a V3 correctness gap: PlayerSnapshot has only hand/decision/clocks, so a reconnect during settlement cannot reconstruct results, and UI cannot know an externally activated takeover. Session history/final result also inaccessible through common access. Complete a small coherent projection using the same snapshot wire path (no new server request kinds).

## Contract to implement

New public immutable/copy-isolated DTOs in engine/access:
- PublicRoundRecord(roundId:String, resultId:String, finalScores:List<Int>, deltas:List<Int>, finalCheatPoints:List<Int>, cheatDeltas:List<Int>). No hidden hands or detailed cheat ledger reasons/entries.
- PublicSessionResult(finalScores:List<Int>, rankedSeats:List<Int>, reason:String, finalCheatPoints:List<Int>). rankedSeats ordered best to worst using domain's actual ranking, not a client recalculation.
- SessionView(lifecycle:String, scores:List<Int>, roundWind:Int, roundNumber:Int, dealerSeat:Int, honba:Int, roundRecords:List<PublicRoundRecord>, finalResult:PublicSessionResult?). Public immutable defensive copies.
- SeatControlView(seat:Int, connected:Boolean, role:String, controlEpoch:Long). role exactly HUMAN/COMPUTER/TAKEOVER. controlEpoch from authoritative Coordinator registry, same as decision tokens.
- Append PlayerSnapshot fields with defaults for compatibility: session:SessionView?=null, controls:List<SeatControlView>=emptyList(), paused:Boolean=false, settlementRoundId:String?=null, settlementEvents:List<GameEvent>=emptyList(). deepCopy must copy all nested collections/events. Keep snapshot identity/cursor contracts.
- Add SessionAccess default querySession():CompletableFuture<SessionView> derived from snapshot (exception if absent), queryRoundRecords(offset:Int=0,limit:Int=50):CompletableFuture<List<PublicRoundRecord>> with nonnegative offset and limit1..100; queryFinalResult():CompletableFuture<PublicSessionResult?>. Default implementations ensure Local/Mall/Remote identical behavior without transport changes.

## Host projection

TableHost builds session from MatchSession read-only state and authoritative current visible scores; final result/history from domain MatchSession. controls from ConnectionControl plus Coordinator epoch. paused from DecisionClock. Terminal settlementEvents only when no current active gameplay / ROUND_END or MATCH_END, from cached completed round public result events projected through the actual existing seat visibility filter; never include unrelated private dealt/draw events. settlementRoundId is actual completed round ID. Clear settlement projection when next round begins. Queries/resume return this complete projection even if the event happened before subscribing. All changes to control connectivity, pause or session projection must count as meaningful update and advance seat stream once; avoid increasing cursor solely for fresh serverNow.

## Protocol

Extend explicit JSON adapters for new fields/DTOs; don't use unrestricted reflection on polymorphic events. New fields emitted by current writer; default absence acceptable only for existing in-process fixtures, wire endpoints use same version implementation. Full settlement breakdown must remain lossless. Existing strict request parsing unchanged. Unknown field/type rejection retained.

Codex owns tests and UI follow-up. Report delivery-GLM-S.md, list exact methods and any limits. Never claim build/test success without execution.
