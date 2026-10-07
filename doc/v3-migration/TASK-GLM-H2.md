# GLM-H2: acceptance failure, resource carry across rounds

Own engine production only, same scope as GLM-H. No tests/build/client/core/server edits. Read delivery-GLM-H.md first; H completed successfully.

Codex ran :engine:test: 316 tests, 315 passed. Failure CheatLedgerIntegrationAcceptanceTest `approved resource debit survives settlement next deal and cross-round retry without altering mahjong scores`, line 37: next deal cheatPoints expected [3,20,30,40], got [0,0,0,0]. Root cause evidence: ApplicationCoordinator.executeNextRoundInternal copies betweenRoundCheatPoints into fresh GameState, then RoundTransitionPlanner StartStacking calls GameState.resetForNewRound(), which calls cheatPoints.fill(0). Preserve match-owned resource balances through fresh deal reset, while round ledger entries reset correctly. Check first deal too, startingCheatPoints freezes correctly, cumulative history idempotency remains valid. Make a minimal coherent fix, not test-specific patch. Read all acceptance tests; do not claim tests run. Codex runs them.

Review any related score/resource copying inconsistency and report delivery-GLM-H2.md. Do not modify shared access DTO interfaces (protocol concurrently implementing).
