# D4: Host compile and remaining concrete defects

Own host/** and access/BoundMahjongGameAccess.kt/local/** only. D2 did not compile. Read build/v3-migration/integration-after-d2.log actual compiler errors and fix all, using existing actual declarations rather than assuming APIs. No tests/other domain edits. Paths may still core/src/main, will migrate engine after you finish. F owns UI, N owns network, D3 domain.

Specific compile errors: TableHost constructor nullable coordinator shadows member in property/init expressions: rename parameter or explicitly this.coordinator. MatchConfig.deepCopy does not exist: defensive copy lists plus cheatEconomy initialPoints/costs. ConnectionControl has seatState(seat).isAutomated, seatState(seat).isDisconnected, getRole(seat), not methods you called. Bound requires import application.deepCopy for PlayerDecision. Fix all errors from log.

Remove all getDeclaredField/reflection cheatPoints fallback. D3 is adding GameState.cheatPoints:IntArray, MatchSession.betweenRoundCheatPoints:List<Int>; directly reference these typed APIs even if parallel edit not finished yet. Initialize state from config; snapshots from activeRound.readState().cheatPoints or session.betweenRoundCheatPoints. Falling back to initial gifts masks integration bugs.

Other review findings:
- Initial broadcast cannot use cursor=fromCursor=0 after start if initial client snapshot was0: ClientSynchronizer will discard it! Establish initial stored projection at construction, every real changed update cursor grows strictly. subscribe initial baseline may be snapshot cursor0, but start must cursor1+.
- resumeSeat currently changes epoch for same seat, pushes new-epoch update which GameClient will reject as STALE. Prefer retaining stream epoch for ordinary snapshot resume (one host incarnation), return authoritative snapshot cursor. Epoch change only genuinely restarted stream, with explicit resync mechanism. Do not break other endpoint of same seat.
- AI session dealer choice is skipped by syncDecisionsAndClocks because roundId null. Provide host processing for automated session end/continue choice so all-AI games cannot stall permanently. Automatic next-round/ready policy must let local seat ready with AI others continue.
- Reentrant mutation rejection must cover tick/connect/disconnect/reconnect/close, not merely submit. Ensure callbacks querying snapshot safe while no nested state commit.
- lastActionTimeMillis initialization must allow first action after actual150ms, avoid Long.MIN_VALUE subtraction overflow. Manual clock0 test must work.

No script execution. Deliver D4 report; Codex builds and runs acceptance tests.
