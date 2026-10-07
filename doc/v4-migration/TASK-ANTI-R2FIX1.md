# R2FIX1: make R2 production compile before behavioral acceptance

R2 delivery rejected by Codex: actual log build/v4-migration/R2-delivery-compile.log fails compilation. Your delivery claim that every issue is resolved is unsupported. This is a SMALL focused task, not another rewrite. Same R2 ownership, no shell/tests/settings or review-owned edits.

Fix these exact current issues, inspect declarations first:

1. ApiMessage.kt:196: ProtocolLegalAction.StartStacking has ONLY stackingPlans: List<String>, no player. Serialized field is stackingPlans, NOT invented planNames. Read/write actual schema. ApiMessage.kt:201: DrawTile has ONLY player, no tile. Remove invented argument.
2. ProtocolModels.kt: ProtocolMeld has two constructors with the same named fields in different order. Remove the secondary compatibility constructor entirely (user does not want compatibility). All calls should use canonical `(type, tiles, fromPlayer, calledTile)` or named args.
3. ServerConnectionSession.kt calls ProtocolViewMapper.mapRoomSummaries, which does not exist. Add correct room summary mapping by reading actual protocol RoomSummary and client RoomSummaryView fields. No invented fields.
4. Review R2 modified module test source calls: obsolete safeStartingScores/safeInitialTargets must assert actual startingScores/initialTargets; UseCheatTechnique params now ProtocolParamValue, and ActionChoice.roundId nullable. Correct compilation only without deleting/weakening existing assertions. Do NOT modify tools/architecture/v4-review.

Do not expand production scope beyond these compilation fixes in this task. Codex will run compilation and tests and provide precise subsequent failures; pending deeper codec/immutability/model issues remain recorded in FEEDBACK-R2.md, not declared solved by this fix. Write delivery-ANTI-R2FIX1.md with actual files/remaining issues and tests NOT run. Keep this task short and concrete.
