# G2 Final CI delivery — corrected timing oracle (2026-11-05)

**State: NOT ACCEPTED.** This same-receiver follow-up corrects the prior round's mistaken interpretation of recovery grace. It makes no production/verifier changes, performs no remote operation, and does not claim the historical full Unified matrix is green. G2-REVIEW-01 remains the accepted scope gap; G2-REVIEW-03 delivery is present.

## Candidate and evidence identity

- Worktree/branch: `E:/majiang-review/g2-final-ci-luna6-20261104`, `wt/v5-g2-final-ci-luna6-20261104`.
- Current base: `e2accaa0b9647bcb64e0fc3639c4c5bf1eb5a0d8`. This follow-up is uncommitted and unpushed; there is no candidate commit SHA. The C04 content hash and Git blob are in the companion binding JSON.
- Historical source `4a855c3c49e65ec97708784d84a784df9f11c199` and PR synthetic merge `ebc98716b8588d5ff6af774b04674bc4f3dd0e3f` are distinct. Source push run `41729487041` was historical SUCCESS; PR run `41729493387` was historical FAILURE on the merge SHA. The original direct-results XML remains unchanged at `build/caller-original-ci-41729493387`, SHA-256 `3483424346A8DB5F57065A12C4A3A2D0EB9700C61E75DDB12E54AE37738BF8F3`; original Unified summary was 20 PASS / 1 FAIL.
- The prior complete candidate verification remains **19 PASS / 2 FAIL**, as stored in `artifacts/541f2f42-fd58-49e6-b7ac-526c178c9abc/unified/summary.log` (SHA-256 `8EE94AF39ED6525612711940D2C8AA6C1BB8D1177D36BE477444AF946AF0A6F2`). This follow-up did not rerun or alter that matrix. The prior Lobby PostgreSQL and LocalControl failures remain open/out of scope.

## Corrected review findings

### G2-REVIEW-01 — MAJOR — OPEN_SCOPE_GAP (accepted; no expansion requested here)

`PrepareRecoveryCaptureOnNextOpen` opens the recovered runtime with `deferDriver=true` so it can capture the first recovered save. The current C04 probe explicitly confirms `DriverRunning == false` and manually calls `actor.TickAsync`; it therefore tests actor deadline handling only, not the real driver's post-retirement unbound-human gate. Existing `DriverForTest` APIs have no deterministic completion barrier after that gate; polling/sleep and treating `StopAsync` as proof would be invalid. The previously accepted exact scope request remains an internal deterministic driver-iteration barrier in `LocalTableDriver.cs` (plus accessor plumbing only if needed). This round does not retry the driver seam or claim driver-level acceptance.

### G2-REVIEW-02 — CORRECTED: prior review requirement was mistaken; no production defect

The reviewer withdrew the demand that cold recovery preserve the *source* checkpoint grace value unchanged. That ignored the documented, legal first recovery grace. `TableRecoveryOptions` defaults `GraceMillis` to `5000`; the accepted recovery lifecycle delivery specifies a recovered deadline of `29000` with grace `5000`; `TableDurableTimingRecoveryTests` verifies first grace application and that two consecutive recoveries do not grant it twice.

C04 now computes expected grace/base independently of recovered output:

- If source `RecoveryGraceApplied` is false, expected grace is the configured first-recovery grace `5000ms`, and exactly that grace is added to the source remaining base.
- If source `RecoveryGraceApplied` is true, expected grace is its persisted remaining grace and no grace is added again to source base.
- Expected absolute deadline is derived from recovery monotonic time plus this independently expected base, source reserve, and source overdue. The checkpoint and running `DecisionTiming` are checked directly, as are the before/after boundary conditions.

This directly rejects an extra `+5000ms`, a repeated grace on an already-applied checkpoint, or a deadline reset. The previous round's erroneous-oracle red result is preserved, not overwritten: `build/541f2f42-fd58-49e6-b7ac-526c178c9abc/review-round2/targeted/final-assertion-target.xml` and `.native.log`. It reported source grace/base `0/20000`, recovered `5000/25000`, expected deadline `24000`, actual `29000`, and a pending decision at the incorrectly asserted 24-second boundary. The error was using source grace `0` as if it were the configured first-recovery grace; the additional 5 seconds was valid policy behavior, not a production violation.

The prior green TableRuntime XML (`build/541f2f42-fd58-49e6-b7ac-526c178c9abc/table-runtime/direct-results.xml`, SHA-256 `BE56F32AD2AABBA2A831902D2769B2151F73E1F52C8DC5C3A0F945AC7C71C9B8`) contains and passed both `LongCrossBootDowntimeIsExcludedButGraceIsAppliedOnce` and `TwoConsecutiveRecoveriesDoNotGrantGraceTwice`; it was not rerun.

### G2-REVIEW-03 — delivery files corrected

Both requested files exist. The Markdown and JSON now bind the corrected C04 blob/hash, preserve the prior 19/2 result and out-of-scope failures, identify the prior wrong oracle and its raw failed run, and state NOT_ACCEPTED / remote CI NOT_RUN. No old artifact or summary was refreshed or overwritten.

## Bounded verification for this correction

Build command: `D:/game_dev_tools/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --configuration Release --no-restore` — native exit 0, zero warnings/errors. Raw log and hash are bound in the JSON.

The exact native target command is recorded per invocation in `build/541f2f42-fd58-49e6-b7ac-526c178c9abc/review-round3/targeted/`. Five fixed invocations each reported total=1, passed=1, failed=0, errors=0, skipped=0, not-run=0, native exit 0. Their XML and native log SHA-256 values are listed in the companion binding JSON. The previous round's failed XML/log remain intact under `review-round2/`.

No 184/483 full suite or expensive PostgreSQL/Unified matrix was rerun. LocalHostRecoveryTests.cs remains unchanged. Remote CI on this content is **NOT_RUN**; independent review is **NOT_RUN**. No acceptance, approval, push, merge, or deployment is claimed. V4 remains production and G2-G5 remain unapproved.

## Other retained out-of-scope findings

- `LocalControlTests.DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect`, `LocalControlTests.cs:575-690` (especially line 690): existing Unified XML records expected `Ready`, actual `Synchronizing`, LocalHost 183/184. Its original XML/log are preserved; no dependency on a hung thread is claimed and no change was authorized.
- Lobby PG installation/cleanup: existing Stage D diagnostics show missing `psql.exe`, `pg_ctl.exe`, and timezone resources and 12 LobbyService connection failures. The verifier/scripts were not changed. Diagnostics and the normal shutdown evidence for the owned PG process remain preserved.
