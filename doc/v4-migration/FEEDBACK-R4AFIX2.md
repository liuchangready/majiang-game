# R4AFIX2 live feedback

Starting from failed R4AFIX1 delivery. All known findings are in TASK-ANTI-R4AFIX2.md and FEEDBACK-R4AFIX1.md. Current independent reviewer suite has 10 R4 probes, plus accepted R1-R3 baseline of 78.

Latest UdpRoom source DOES now contain direct fallback `tableHost.authority.querySnapshot(seat, "sync-0", 0L, emptyList())` in resume supplier (late edit after earlier read). Remove this unqueued domain access and invented epoch as task requires; it must use correctly published immutable cache/async authoritative mechanism. Earlier note that fallback was absent applied to previous read only.

21:27 R4AFIX2-engine-interim.log PASS: all 5 TableAuthorityTest cases now pass, including real TestTableBuilder.assertPhysical. Full 345 engine rerun remains for final validation.

21:28 R4AFIX2-review-interim.log PASS: all 10 independent R4 authority/host/binding probes pass. This does not accept unfinished 20-test equivalence or cache fix. Starting normal engine/server module test build next; will report actual diagnostics.

R4AFIX2-modules-interim.log: full engine 345 PASS. Server test compile fails only on known missing CountDownLatch/AIStrategy/PlayerView imports in ServerTableHostCoverageTest (and cascading inferred types). Production compiles. Await revised actual-equivalent coverage source for next run.

R4AFIX2-regressions-interim.log PASS: cumulative 88 independent checks, 0 failures/errors/skips, including real UDP double outage + cleanup. New coverage source now compiling/running in R4AFIX2-server-interim2.log, results pending.

21:31 R4AFIX2-server-interim2.log: server 66 tests, 7 FAILURES. Inspect XML and old original tests; do not weaken them. New equivalent coverage found real issues:
- ServerTableHostCoverageTest local pause :274 expected deadline 73000 got 0; riichi automatic interval :234 expected true got false; riichi win/kan :256 expected one timeout discard got zero. Check initialState initialization of clocks/decisions in new host (and preserve old LocalHuman controller fixture where needed).
- old control epoch/old round :600 expected REJECTED got RECORDED. Preserve actual old release/reclaim epoch fixture and reject old authorization.
- exhaustive draw bankruptcy :156 expected nextRound REJECTED got RECORDED. New host adds ready before validating terminal domain lifecycle; FINISHED cannot return accepted ready.
- cost reserved/charged :531 duplicate request must reproduce original ActionReceipt including cursor; got original cursor 2 then retry cursor 3. Domain idempotency caches cursorless receipt while host overwrites current cursor on every call: preserve original externally returned receipt for duplicate ID/payload, without double charge/mutation.
- InProcessBindingTest :89 expected delivered update count 2 got 1. Notifications now async: inspect whether legitimate eventual delivery requires latch/barrier in test versus real lost update, never drop observer/idempotency assertion.

All 66 server tests compiled; old 340 + new5 engine tests pass. Reviewer probes passing does not override these valid failures. Please address within current run before delivery.

Clarification after line inspection: coverage :600 failure is specifically `a.nextRound()` during PREPARING (expected REJECTED), not epoch check; epoch/old-round assertions preceding it passed. Both :600 and bankruptcy :156 need lifecycle validation before adding ready. Riichi fixture uses humans() all RemoteHuman(false), unlike original HostRhythmAcceptanceTest whose seat0 is LocalHuman: preserve that original setup for automatic riichi policy. Missing initial clocks remains a production issue. The new ServerTableHostTest serial queue case also asserts APPLIED on random discard; use correct successful status and retain state/cursor assertions just as fixed domain test.
