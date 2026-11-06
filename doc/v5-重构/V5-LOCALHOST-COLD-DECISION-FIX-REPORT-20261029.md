# V5 LocalHost Cold-Decision Fix — Candidate Patch Report (Revision 2)

Date: 2026-10-29 (Asia/Shanghai)
Status: **candidate patch delivered; BATCH-LH-COLD-01 is NOT closed; unified gate NOT_RUN; G2–G5 are NOT claimed; V4 remains production.**
Worktree: `D:/majiang-wt/cold-fix-ds-20261029`
Branch: `wt/v5-cold-fix-20261029`
Local base: `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (accepted `origin/v5`)
Frozen integration candidate: `a2a1bf9099ce6b607008cb017d7be60577166118` (PR20; **not** an accepted baseline)
Diagnosis input: `ab8d45f27f9f9d277127eba8872abb7456292557`
Deliverable: `doc/v5-重构/V5-LOCALHOST-COLD-DECISION-FIX-20261029.patch`
Patch SHA-256: `20989A95EAEDEEB7017C7B1C86D69F3698DDFBBC837F9587A175796CF01D6E08` (29,984 bytes)

This is a cumulative patch against the frozen `a2a1bf9` tree. It supersedes the Revision 1 patch that passed the cold test but still retained a stale turn decision when a new response window made the same seat eligible. The patch is not a wholesale candidate-tree commit.

---

## 1. Scope and constraints

- Work was performed in this dedicated worktree and in ignored `build/` copies created with `git archive`.
- No delegation, push, PR update, merge, deployment, global trust change, Unity/Resources/Burst change, or other worktree was used.
- The reviewer regression and log under `build/caller-verify` were read for reproduction only; that tree was not modified or committed.
- No secret, credential, token, private seed, or private ledger value was added to the patch or report.

---

## 2. Revision history

| Revision | Change | Result |
|---|---|---|
| Rev 1 (`ed4fbd3`) | Transition filter used seat/window eligibility only; LocalHost barrier required `CurrentSeat == 0` plus seat-0 pending | Cold regression passed; reviewer then showed a stale turn at seat 1 survived a new window because seat 1 was eligible |
| Rev 2 | Transition ownership now uses the prior state, window identity, and turn-vs-response candidate classification; recovery shares the same predicate | Reviewer regression and window-identity overlap regression pass; TableRuntime 453/453 |

---

## 3. Implemented change

### 3.1 Transition ownership is no longer seat-only

`dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs`

`FilterAfterTransition` now receives the prior `RoundStateMachine` as well as the next one. For each pending decision:

- **No prior window, next window opened:** every pre-window pending is stale and is dropped. A turn decision cannot become a response merely because its seat is eligible in the new window.
- **Prior window exists, next window is the same identity:** only a genuine response decision is retained, and only if the seat is eligible and has not responded. A stale turn opened at an eligible seat is classified as a turn decision and dropped.
- **Prior window exists, next window has a different identity:** the old response is dropped even when the same seat is eligible in the new window. Identity is `FromSeat + TargetTile + IsChankan`.
- **Prior window exists, next window closed:** every decision captured before the window is stale; all are dropped.
- **No prior window, no next window:** only a current-seat turn decision with a turn anchor or legal turn-kan shape survives; a response candidate set cannot become a turn.

Turn-vs-response classification:

- turn anchors are `Discard`, `Tsumo`, `Riichi`, `Cheat`, `Other`;
- in an open window, a Kan on the window target is an OpenKan response, not a turn kan;
- a Kan-only set on a non-window tile is classified as a turn kan only when the physical hand shape supports it;
- `[Kan, Pass]` is never treated as a turn kan.

Rejected decisions are cancelled on the **candidate** `DecisionClock` in the same transition, preserving atomic pending/timing updates. `TableActor` passes `_round` (the still-prior state) at all three commit paths: non-durable, durable, and cheat.

`FilterRecovered` now uses the same `IsStillOwned` predicate with the recovered round as both prior and next state, so recovery and transition cannot drift.

### 3.2 LocalHost barrier (unchanged from Rev 1)

`dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`

`WaitUntilFollowUpsSettleAsync` requires `(CurrentSeat == 0 && seat-0 pending non-empty) || round.IsFinished`. The cold test also asserts parent `CurrentSeat == 0`, exact parent pending id `local-human-<stateVersion>-0-<phase>`, disk/parent state-version equality, and cold pending id consistency with the cold state version.

### 3.3 Stale-open contract

`TableActor.ProcessOpenDecisionAsync` is unchanged. A stale host open is still accepted and is invalidated by the next owning transition. The reviewer regression itself opens the stale seat-1 turn through this existing contract and verifies that the subsequent response-window commit removes it. `ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit` remains unchanged and passes.

### 3.4 Touched candidate paths

| Path | Blob before | Blob after | Numstat |
|---|---|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `de2142bf830138a61cf903e46de73418887453b4` | `2b819e789484ffa0bf73b4adc8cbda342825f896` | `3/6` |
| `dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs` | `2818057d94af3bac86c3c9836693a25cc32733e6` | `9fd9060cb4d35c351abc7c36b682e6c7538cc799` | `109/48` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs` | `4703960f04896fe8c8570db12c9de3eb6bd6b379` | `4cb069bd85bdc7ec5c90cb5c92d8b6d0b43d0b30` | `237/0` |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs` | `a4a06206779a583d55d6c337b6c7306255175cb7` | `761a51ed6faf4f80a9592136bcedd447ed495cc2` | `25/2` |

Patch total: **4 files changed, 374 insertions(+), 56 deletions(-)**.

### 3.5 Focused tests

Existing Rev 1 tests remain:

- `TurnDecisionIsRemovedAtomicallyWhenCommitAdvancesCurrentSeat`
- `FailedTurnCommitLeavesPendingAndTimingUntouchedThenRetries`
- `FinishedRoundClearsEveryPendingAndTimingInTheSameCommit`

Reviewer-driven additions:

- `ReviewerStaleTurnMustNotSurviveNewResponseWindow` — exact reported scenario: seat 1 has a stale `Discard` decision, seat 0 discards `Dragon(1,3)` and opens a Pon window for seat 1; the stale pending and timing must both be gone.
- `ResponseDecisionFromDifferentWindowIdentityIsDroppedDespiteSeatOverlap` — direct transition-filter test: a seat-1 response from window A is retained for the same window and dropped when window B lists seat 1 but has a different target.

---

## 4. Evidence

All commands used `D:/Unity/dotnet/dotnet.exe` (SDK `8.0.426`) and `git 2.51.0.windows.2`. Logs/XML are in ignored `build/evidence2/`; they are local evidence, not committed deliverables.

### 4.1 Reviewer regression reproduction before Rev 2

Applying the Revision 1 patch to fresh `a2a1bf9`, adding the reviewer test, and running it:

```powershell
dotnet dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll `
  -noLogo -reporter default -result-xml ".../rework-baseline-results.xml" `
  -method 'Mahjong.TableRuntime.Tests.TableResponseDecisionInvalidationTests.ReviewerStaleTurnMustNotSurviveNewResponseWindow'
```

- **FAIL**: Total 1 / Failed 1 / Errors 0; native exit `1`.
- Actual: `review-stale/1/stale-turn`; expected null.
- XML SHA-256 `B3CD73D33A38247F2EE2ABB740547006417EB542D3E5AE87DDBC938757B9200A`.
- Focused log SHA-256 `AD207EC54A1D62141DE666B3ADB50A38BFA79C681515C4EF1469FFD90327E6F5`.
- The caller’s full run at `build/caller-verify/dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/TestResults/Mahjong.TableRuntime.Tests_net8.0_x64.log` records **452 total / 451 passed / 1 failed**, exit `1`; our focused reproduction independently confirms the same failing assertion.

### 4.2 Rev 2 on the candidate copy

Builds: TableRuntime.Tests and LocalHost.Tests + CrashWorker each native exit `0`, 0 warnings, 0 errors.

- Reviewer regression alone: **1/1**, native exit `0`; XML SHA-256 `FC4B689EF43B1295C2A0A10CDC183C32E0C9F36F0494E337B92C64009AEC905A`.
- Focused invalidation class: **22/22**, native exit `0`; XML SHA-256 `7A25F9D370820C392A13756CBF8876E5EF1FACF25A0934089EA45046803B69DB`.
- Complete TableRuntime suite: **453/453**, Errors 0, Skipped 0, Not Run 0, native exit `0`; XML SHA-256 `498DCA6D718341293B0D8A62073CDAAFCEB85529C78E94D4B190DCCCCC55F322`.
- Complete LocalHost suite, first run: **115 total / 114 passed / 1 failed**, native exit `1`. Failure: `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw`, expected `SETTLEMENT_FURITENRON`, actual `STALE_STATE_VERSION`. The test passed in isolation (1/1, exit 0) and the immediate full rerun was **115/115, exit 0**.
  - first-run XML SHA-256 `F7FB29DF5C7AD792A3D8A966A76F8676274603C7BB4E5A406F726844EADE8B0E`;
  - rerun XML SHA-256 `9F309C93DA79CFEE29057E7C73A9B8B0E86900DCABD40A56C015AA3DD9B757F7`.
- Cold test, three consecutive runs: **1/1 each**, native exits `0,0,0`; each marker has `state_version=4`, `last_event_seq=8`, `cold_pending_decision=local-human-4-0-CheatWindow`, `final_state_version=4`, `final_last_event_seq=8`, digest `f4b46cf7…5d5ff`.
  - marker SHA-256 `E0BD2A0EC517DB1FD7A949E7B5E7790E13CA8F71A8881E72BA9111DB7BFF92E8`.

The furiten failure is recorded as a timing-sensitive pre-existing test interaction, not as fixed by this patch. It is unchanged, passes in isolation, and both full patch-applied runs below are green.

### 4.3 Patch-applied fresh `a2a1bf9` tree

Exact apply commands:

```powershell
git -C <fresh-a2a1bf9-tree> -c core.autocrlf=false apply --check <patch>
git -C <fresh-a2a1bf9-tree> -c core.autocrlf=false apply <patch>
```

- Both native exit `0`; all four resulting files matched the tested patched blobs exactly.
- Build: native exit `0`, 0 warnings, 0 errors.
- Complete TableRuntime: **453/453**, exit `0`; XML SHA-256 `FEF18EF504E446F78618FA179B8AD2DE30CBD1104018CCD16825115C9263AA95`.
- Complete LocalHost run 1: **115/115**, exit `0`; XML SHA-256 `229CBFF155FF2F7B347D31F46682AD2CA8AC872AD66F0B730AF6653EBFD89D56`.
- Complete LocalHost run 2: **115/115**, exit `0`; XML SHA-256 `F2C920773B611F03F70DB09727C20F4A4C477C12A4BE5E8A397EEE05B4DD8C2A`.
- Cold test, three consecutive runs: **1/1 each**, exits `0,0,0`; markers identical to §4.2 (`v4/e8`, `local-human-4-0-CheatWindow`).
  - first marker SHA-256 `44ADC787E444762B51247C660F5B0CE3A6EDCA1327B48CF68C756E8D92051A42`.

### 4.4 Status ledger

| Item | Result |
|---|---|
| Reviewer regression reproduced on Rev 1 | **PASS (reproduction)**; test **FAIL**, exit 1 |
| Reviewer regression after Rev 2 | **PASS** |
| Different-window identity overlap | **PASS** |
| Legal same-window unresponded response retained | **PASS** |
| Failed commit rollback / terminal cleanup | **PASS** |
| Recovery and legal turn-kan retention | **PASS** |
| Complete TableRuntime | **PASS** (453/453) |
| Complete LocalHost | **PASS** on patch-applied runs and candidate rerun; one candidate full run hit the unrelated furiten timing flake |
| Cold process parent/cold equivalence | **PASS** (3/3 on candidate and 3/3 on patch-applied) |
| Default unified gate | **NOT_RUN** |
| Unity / Android / IL2CPP / real network | **NOT_RUN** |
| G2–G5 | **NOT claimed / NOT_RUN** |
| Push / PR / merge / deployment | **NOT_RUN (by design)** |

---

## 5. Residual risks and non-claims

1. The unified gate was not run. Green TableRuntime/LocalHost results do not close BATCH-LH-COLD-01 and are not a unified-gate PASS.
2. The `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` timing failure was observed once in the candidate full run, passed in isolation, and both patch-applied full runs were green. It is not modified or claimed fixed.
3. The earlier `PartialCancellationOfConcurrentInitialOpenLetsTheOtherWaiterSucceed` timing flake remains a pre-existing, unchanged test.
4. No Unity, Android, IL2CPP, PostgreSQL, WSS, companion, multiplayer, or real-network verification was performed.
5. G2–G5 remain unapproved. V4 remains the production baseline.
6. The patch applies to `a2a1bf9` and descendants containing the TableRuntime/LocalHost files; it does not apply to `8fd7c6c`, where those files are absent.

---

## 6. Cleanup

- Builds/tests ran in ignored `build/` copies or the ignored evidence directory.
- No child test process was left running; owned temporary source copies and archives were removed after patch verification.
- The worktree contains only this report and the patch as tracked additions; no other tracked file was modified.
