# R3FIX1 live review

## 20:51 FINAL CHECKS GREEN - finish delivery, no more production edits needed

R3FIX1-final-review.log: **78/78 independent checks passed**, zero failures/skips, including real UDP repeated outages and receiver cleanup. R3FIX1-final-checks.log: full Gradle test + desktop compile + Android APK **BUILD SUCCESSFUL**. Latest client module 83/83 passed, explicit leave binding cleanup verified. Codex has archived source hashes and counts. Please finalize delivery-ANTI-R3FIX1.md accurately, no speculative extra changes and no R4 work. Earlier failures below are historical and resolved by this final run.

**20:48 R3FIX1-interim-4.log:** fake runtime now **24/24 PASS**, client **82/83**. Remaining module failure testLeaveRoomInFlightPreventsReconnectOnDisconnect expects currentBinding null after terminal disconnection during explicit leave, but old RoomBinding retained. Explicit leave intention must discard resumable binding when transport becomes terminal; retain UI snapshot as appropriate. Do not weaken the no-reconnect assertion. Check exact XML stack and fix, then ready for full rerun.

**20:46 update, R3FIX1-interim-3.log:** same-epoch delayed decision and queuedActiveState now PASS. Runtime **22/24**, remaining failures are direct close completion violating dispatch and no-progress recursive recovery (new independent reproduction). Client **76/80**, same four module failures from 20:44. Source latest terminateSession still directly fut.completeExceptionally at ~1402. Please apply all 20:44 items below; do not finalize early.

## 20:44 latest executable results: R3FIX1-interim-2.log

Production and module tests now COMPILE. Fake runtime 20/23; client module 76/80.

- **Regression in latest terminateSession**: changing all failures to direct fut.completeExceptionally made queued-success test pass by violating dispatcher contract; uiDispatchControlsReceiptsAndDropsQueuedUpdatesAfterClose now fails. Restore dispatch for completion, and check closed/generation *inside queued success callback*. Both assertions must pass together; do not trade one for the other.
- Delayed same-controlEpoch decision and queuedActiveState tests still fail as described below.
- **Real recovery bug found by module test**: testReorderBufferAndGapHealing expects no future update but receives ~689 duplicate baseline notifications. Fake resume returns completed current cursor=10; triggerGapRecovery recursively retries the same unresolved gap without progress until StackOverflow is caught. Only continue immediately if recovery makes real progress. No-progress recovery must release in-flight and wait for new input or bounded scheduled retry; do not repeatedly publish identical baseline. Keep recoveryContinuesWhenAnotherGapRemains passing when progress *does* occur. Do not merely change fake fixture to hide this loop.
- Module fixture issues you may correct with clear rationale: testLeaveRoomInFlight... never installs customLeaveRoomFuture, so its fake returns immediate APPLIED yet test expects pending. Install an uncompleted future. testGameSessionFacadeOperations emits duplicate cursor 10 after installing cursor 10 then expects UI update; use fresh cursor 11/from10 to test observer plumbing. Its later KICKED assertion must reflect R3 permanent closed state and preserved KICKED reason, not expect open then LOCAL_CLOSED. testAutoReconnect... expects only RECONNECTING, but onStateChange now intentionally replays CONNECTED; clear initial replay before testing transition or include it. Keep actual recovery assertions.

Please fix these together before delivery. Latest XML paths are client/build/test-results/test and build/v4-migration/review-R3-probes/server/test-results/test.

## 20:41 confirmed remaining lifecycle failures (read first)

R3FIX1-late-response-check.log: expanded fake runtime **20/23 pass, 3 fail**. These are original R3 nonrollback/queued callback requirements, not new scope:

1. delayedDecisionCannotReturnOldOptionsAfterNewPushInSameControlEpoch: decision request pending, PUSH installs newer decisionId with SAME controlEpoch, old decision reply then arrives. Current decision() compares only controlEpoch, returns old options. Compare request's snapshot version/cursor/generation and choose confirmed current decision if updates advanced; don't assume epoch changes for every decision.
2. queuedQueryCompletionIsInvalidatedByCloseBeforeUiConsumesIt: snapshot transport has completed but result completion is queued in injected dispatcher, runtime closes, drain dispatcher. Existing queued success wins over queued close failure. Check closed/generation again INSIDE every dispatched successful completion (snapshot, resume, binding, cached decision etc). Once close happened while outer Future pending, it must not succeed later.
3. queuedActiveStateCannotBePublishedAfterClose: onStateChange immediately enqueues CONNECTED; close before drain; callback still emits CONNECTED after closed then CLOSED. Initial onStateChange replay lacks generation/closed guard. Permit terminal CLOSED notification but suppress queued old active states, including notified states in same epoch. Also honor listener unregister handles when queued notifications run.

Module test compile still has RoundStarted unless you have now corrected it. Latest full Gradle attempt R3FIX1-module-interim.log: other modules and platform builds proceeded, overall failed only at client test compilation. Fix all then Codex will rerun focused/full checks.

**20:38 latest run: R3FIX1-interim-1.log** Production compiles. Client module test compile fails at ServerConnectionSessionTest.kt:718: nonexistent ProtocolGameEvent.RoundStarted. Use an actual equivalent event (e.g. StackingStarted, as old cross-round dedup test does) preserving two distinct rounds with same event payload assertions. Do not weaken that test. Independent server review ran separately via --continue; Codex checking XML now.

Confirmed independent fake runtime **20/20 PASS, zero skipped** on this source. Now running two real UDP probes plus R1/R2 regressions (R3FIX1-full-review.log). Client module compile still needs above fix; no stage acceptance yet.

20:40 R3FIX1-full-review.log **74/74 PASS**, including real UDP two outages, final receiver cleanup, all R1/R2 regressions. Module compile remains blocked by ProtocolGameEvent.RoundStarted at test line 718 (still present in latest source). Please fix that module fixture then Codex can execute all module tests and report any real failures. Do not treat delivery as accepted until that full check passes.

Codex currently has 20 fake-connection runtime tests and 2 real UDP tests (two outages plus final receiver cleanup). Last fake test added: rebindingEndsOldRequestsBeforeTheirTransportResponds, which requires new binding to terminate all old-generation futures immediately, without waiting for old transport to respond. Old action completion cannot release the new generation's pending gate.

Review tests are Codex-owned. Compilation of production must be fixed first; no behavior result for these expanded cases yet. Please reread this file before final delivery.

20:37 protocol:test PASS after adding isFullSnapshot (R3-protocol-interim.log). Runtime code review: handleInboundUpdate full branch currently does plain `return` inside inline synchronized after adding updatesToDispatch. This exits the whole function, so cache changes but UI is never notified. Make accepted full update reach the outside-lock dispatch. Independent full replacement test now checks observer delivery and marker, not only cache.
