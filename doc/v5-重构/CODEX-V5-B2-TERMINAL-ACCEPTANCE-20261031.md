# B2 terminal service: independent component acceptance

2026-10-31. Thread `thread_20261031_124526_0eef`, round 2.
Delivery `5718fe4e369901e3d2d068548f18e62bb9abd917`;
implementation/test head `b95f79e030dc44ebdc0be0c9af64bc063ada3ff2`;
frozen delivery base `a68c157c8fe56ca51c8fa37ded54f2acd773ffff`.
Integrated and tested candidate `3696d9028b1601fe37fc43683e324d22100587f2`
combines the delivery with PR20 head `b926775ff69a3aeef0763d8aff82f0e98dd150fc`.

**Accept the service-side implementation and all five round-1 rework groups.**
The original task explicitly permits a scoped implementation plus executable
failure evidence and an exact dependency when the frozen actor port blocks the
production path. This acceptance uses that boundary. It does not accept real
PostgreSQL natural termination, cold-win recovery, the whole B2 product, G3 or
any other phase. Their outstanding dependencies remain below.

## Changes and independent evidence

| Finding | Verified result |
|---|---|
| B2-R1-01 | Legal concealed/open meld shapes use the frozen evaluator; a real closed-kan/rinshan win is advertised and accepted by the actor. |
| B2-R1-02 | Complete discard history, including called-away discards, intersects all theoretical waits; discard/temporary/riichi furiten blocks Ron without removing other legal claims. |
| B2-R1-03 | A committed abortive draw replays its original receipt after RoundEnd; payload conflicts still fail. On a new binding/scope, the same account can query the original scope/request; a foreign spectator is refused. |
| B2-R1-04 | Transient failure retries the same identity, concurrent ticks are fenced and persistent failures stop after bounded attempts. A real unknown-but-committed result is reconciled against its original durable receipt. |
| B2-R1-05 | The actual worker natural/verify success gates reject privacy violations, repeat publication and missing evidence; the runner also rejects those flags. Actual worker gate-selftest: 14/14. |

Caller source-worktree evidence: `build/caller-b2-r2-d612da/` in
`D:/majiang-wt/b2-terminal-ds-20261031`. Integration evidence:
`build/caller-b2-r2/` in this report's integration worktree.
Both preserve exact build/native exits, raw logs and per-case XML; each has an
`evidence-sha256.json` inventory. Caller probes are ignored artifacts and did not
modify submitted production or test sources.

The caller actually executed:

- Fresh Release builds of TableService.Tests and the B2 worker: zero warnings/errors.
- Delivery TableService: final 194/194, including all 23 B2 cases; zero failures/errors/skips/not-run.
- Original independent business probes against the repaired assemblies: all four original failures now pass. The old extracted worker predicate was not reused as a new gate test.
- Actual worker gate-selftest: 14/14, native exit 0.
- Fresh integrated TableService: **203/203**, including the 23 B2 cases and the previously accepted timeout-client regressions; zero failures/errors/skips/not-run, native exit 0.
- Integrated worker build/gate and independently rebuilt probes: native exit 0, no failed probe assertions.

The integration retains all existing PR20 changes. The B2 production files,
focused tests, worker and runner are byte-identical to the reviewed delivery;
the added timeout-helper tests explain the 194-to-203 test-count difference.
Delta diff-check is clean. No frozen Domain/Runtime/Contracts/Client source,
workflow, Unity asset, scheduler or complete flag was changed by this task.

## Additional caller verification of previously untested paths

The new-binding probe uses real Kestrel admission and a new issued scope for the
same account, then RECEIPT_QUERY on the original scope/request. It returns Found.
A different spectator account querying that scope gets SCOPE_MISMATCH. This is
receipt reconciliation under the supported protocol; commands do not gain a
general right to present another scope.

The unknown-outcome probe starts a real in-memory durable room at version 0,
executes 71 actual actor actions through natural wall exhaustion to version 71,
then arms the accepted store fault `AfterApplyBeforeReturn`. The service receives
COMMIT_OUTCOME_UNKNOWN and records UNKNOWN_REPLAYED_ORIGINAL_RECEIPT. Independent
query finds that original receipt: one committed transaction, two terminal events,
one outbox entry. With the standard recovery sink installed in the test host,
verified durable recovery reaches RoundEnd; four more drives leave event, receipt,
commit and outbox counts unchanged. No receipt or database result is fabricated.
The sink only installs the actor-verified auxiliary aggregate through the public
host port. Reflection observes the reconciliation marker and calls the same
trusted response-eligibility resolver; it does not alter actor state or receipts.

These are controlled in-memory tests using an explicit frozen UTC fixture. They
are not evidence of real-PG natural termination or production-clock recovery.

## First failures retained

The source-worktree full run first returned 193/194: the pre-existing
`OpenHand_ClosedKan_CandidateAdvertised_AndAppliesThroughTheHost` timed out.
The exact class reran 6/6, the unchanged full suite reran 194/194, and the fresh
integrated suite passed 203/203. The initial XML/log/exit remain preserved; this
review does not claim a deterministic root cause or erase that transient failure.

The first integration harness was interrupted because PowerShell 5.1 treated
normal native stderr as a terminating error under ErrorActionPreference=Stop;
it produced no complete native XML and is not counted as a product test result.
After correcting capture while preserving native exit checks, the complete run
passed. Its interrupted log is retained. An initial extra recovery exploration
also demonstrated RECOVERY_STATE_SINK_REQUIRED in the in-memory test fixture;
the final probe wires the standard verified-state sink and passes. That original
exploration log is preserved separately.

## Recorded PG evidence and remaining boundary

The caller checked all **222** size/SHA-256 entries of the receiver's
`artifacts/p54b-durable-host-20261031-125158/` bundle: zero mismatches.
Its 29 stages are PASS, with native XML reporting TableService 194/194,
Runtime 476/476, Infrastructure 42/42 and DurableHost 59/59, all zero
fail/errors/skip/not-run. This is verification of recorded receiver PG execution,
not another caller PG run.

The receiver's original real HTTPS Lobby/WSS/PG natural run reaches wall=0 but
fails the terminal checkpoint with CHECKPOINT_INVALID because the frozen Runtime
uses two different UTC reads for timing and checkpoint metadata. The deterministic
advancing-clock negative remains tested and fail-closed. The real PG RoundEnd,
terminal outbox and post-terminal restart uniqueness remain BLOCKED/NOT_RUN.
Cold-win scoring calculation persistence is another recorded frozen-port gap.
Neither was repaired by changing forbidden layers or hidden behind a frozen-clock
claim. The original formal failures and exact SQL facts remain in the delivery
report and artifacts.

Publish this accepted service increment and this caller report together through
the existing Draft PR20; do not open another MR. Match/NextRound, settlement
consumption, wallet and cross-round orchestration are not delivered here.
P4.5 remains excluded pending its own acceptance. V4 remains production; G1 is
approved and G2-G5 are unapproved. Android/iOS, classified 181 and physical-device/
network gaps retain their recorded status. Current-head CI requires its own review;
historical direct CI and local tests do not establish remote dotnetTest acceptance.
