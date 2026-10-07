# B2 review corrections

Continue B. Own engine/src/main/** EXCEPT stacking/** (A owns shared types). No tests/build/shell. Read SHUFFLE-CONTRACT and root tests Shuffle*AcceptanceTest.kt READ only. Report delivery-ANTI-SHUFFLE-B2.md.

Review found:
1. executeDealWithPreparation applies ledger entries THEN calls resetForNewRound(), which clears cheatEntries. Reset candidate before applying entries (preserve baseline), audit complete ledger and hand atomically. Root ShuffleAtomicDealAcceptanceTest catches malformed136 wall / insufficient second seat partial charge / erased ledger / doubledeal. Keep same-round trusted pending entries consistent if any.
2. prepRoundSeed = roundId.hashCode().toLong() is PUBLIC-predictable; anyone knowing roundId can reconstruct whole wall with guessed plans. Use fresh private SecureRandom.nextLong for each fresh preparation, retain on retry, never expose.
3. Remove production public forcePreparationExpire / setPreparationDeadline, unnecessary mutating test backdoors (tests inject MonotonicClock). isPreparationActive must not provide unsynchronized mutable access; internal/private with locking if exposed.
4. Ensure pause/resume really shifts prepOpensAtMillis AND prepDeadlineMillis by paused duration before clearing pausedAuthorityTime; frozen prep.serverNow whilepaused. Fail closed at exactdeadline, no normal clocks/options untildeal. Existing tests have now0->2000 pause->52000resume->60000deadline.
5. isInitialStateAlreadyDealt only applies to initial supplied round, NOT all future rounds. Current checks in next-round paths skip startPreparation forever if initial fixture was already dealt. Every later actualdeal inclrenchan opensprep.
6. preparation choices/reservations are perRound state percontract: do not keep sole truth in reusable Host arrays. Use immutable defensive Round-owned preparation record with approved state installation; rule/cost/generation outsideRound. Host may own timing/orchestration/private random seed. Preserve existing pure state boundary. Don't silently lose accepted selection on authority updates.
7. aiMaySpend=true currently does exactlysame asfalse (bothskip). Honor option for native Computer seats with deterministic budgeted targetchoice, or explicitly reject unsupported enabled config; never silently accept no-op option. Defaultfalse remains zeroautomaticspend. Takeover humans must never be charged automatically.
8. Failure remains no charge/nodeal/retry same seed, preferably statusGENERATING whilefailed retry (not pretendnew10seconds). No accidental repeated expensive tightretry or leaking internal errors. Keep draft confirmed immutable.
9. Ensure ordinary gameplay cheat actions remain unavailable on new enabled feature (摸打阶段暂不实现); don't activate legacy runtime cheat buttons because budgetnow50/100.

First root compile during your in-progress edits only failed missing buildPreparationViewForSeat; finish projection. Root will run tests afteryourdelivery.

Root engine:test 331 tests completed 329 passed / 2 failed: ShuffleAtomicDealAcceptanceTest line28 missing ledger. The other failure WAS A ROOT TEST FIXTURE ERROR: fourhumanseats needALLready; Codex fixed test to signal eachseat and disable bankruptcy to isolate crossround lifecycle. DO NOT weaken allReady gate. Pause test nowPASS in newerBfiles. All6 algorithmtests PASS. Logs build/v3-migration/shuffle-engine-second.log, XML engine/build/test-results/test/.
