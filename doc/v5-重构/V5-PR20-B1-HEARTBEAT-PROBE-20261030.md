# V5 PR20 B1 Heartbeat Probe (2026-10-30)

Status: **diagnostic only. Not an acceptance, not a fix, not a batch PASS, and not a gate approval.**
Accepted baseline: **`origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`**.
Frozen unaccepted candidate: **`c0dd5f895bf584d38b832102b3f1d81cf1c3e079`** (PR20 head).
Worktree / branch: `D:/majiang-wt/heartbeat-b1-probe-20261030`, `wt/v5-heartbeat-b1-probe-20261030`.
V4 remains the only production baseline; G2–G5 remain unapproved.

---

## 0. Verdict

| Question | Status | Observation |
|---|---|---|
| Candidate frozen and extracted without merge/checkout/commit? | **PASS** | `git archive` into ignored `.pr20-probe/`; no candidate source staged or committed |
| Isolated PostgreSQL 16.10 + real Lobby HTTPS + real TableService WSS prerequisites? | **PASS** | Two independent disposable clusters; per-run TLS root/leaf; no global trust change |
| B1 five-client scenario runner at 250 ms and 50 ms tick? | **PASS** | Both wrapper runs exit 0; 13/13 stages PASS; 44/44 B1 tests PASS |
| Did all five admitted sessions remain healthy? | **FAIL** | Four active clients (p0/p2/p3/s0) and the spectator faulted at the first server heartbeat; p1 was intentionally closed by the crash test |
| Did the historical `state=Faulted, lastError=PROTOCOL_VIOLATION` tuple recur? | **PASS** | Reproduced in both runs; the last inbound frame is `Heartbeat`; no `Close` frame |
| Is the old empty-`Close.Code` cause observed? | **FAIL** | No protocol `Close` frame at all (inbound or outbound); 0 empty codes; 9/9 server-delivered codes non-empty |
| Is the actual producer of the recurring fault observed? | **PASS** | Server `BuildHeartbeatPing()` sends an envelope with empty `SessionId`/`RoomId`, `SeatId=0`, `BindingEpoch=0`; the client rejects it in `MatchesBinding()` |
| B1-MAJOR-01 as "empty Close"? | **FAIL / UNSUPPORTED** | The observed recurrence has a different, now-captured producer; empty Close remains unproven |
| B1 batch gate / B1-MAJOR-01 closure? | **NOT_RUN / NOT PASS** | This report does not close the gate, declare a fix, or approve G2–G5 |
| Production code / tests / verifier / sln / Unity / machine trust / other worktrees changed? | **PASS** | None changed; only a report is committed |

Short conclusion: the historical client-fault symptom **does recur**, but not because of an empty `Close`. The frozen candidate's server liveness loop emits a binding-less `Heartbeat` PING 5.0 s after `WELCOME`; the frozen client requires the heartbeat envelope to carry the admitted session/room/seat/binding epoch and faults with `PROTOCOL_VIOLATION` when it does not. The B1 wrapper still exits 0 because its assertions complete before that first PING; all four active clients are `Faulted` by the worker's final capture. The wrapper PASS is therefore **not** evidence of five-client liveness or of a B1 gate pass.

No prerequisite was `BLOCKED`: PostgreSQL 16.10, .NET 8, the real Lobby/Table services, TLS, and both tick variants were available. The only `NOT_RUN` items are the fix, the gate decision, and any release/deployment action. The two scenario runs are preserved as failed *liveness* runs even though the B1 wrapper itself returned 0.

---

## 1. Scope and boundaries

- Inspected only the frozen candidate through `git archive`; candidate code was **not** merged, checked out into the base worktree, or committed.
- Committed only this report. No production, test, verifier, solution, Unity Resources/Burst, golden/pins, machine-trust, or other-worktree change.
- No push, PR, delegation, release, or gate approval.
- No global certificate store or machine trust change. TLS is trusted per connection against a run-local root (`StrictRootTrust` / `RealWssTableChannel.ValidatePeer`).
- PostgreSQL, Lobby, TableService, credentials, TLS material, outboxes, and ports were per-run and disposable. Cleanup was limited to those resources.
- Preserved final2/rework12/smoke artifacts in the other worktree were read only; they were not modified.
- The candidate's production B1 worker and CI runner were instrumented **only inside the ignored extracted tree**. The instrumentation is test-harness-only and is not part of this commit.

---

## 2. Candidate freeze and extraction

```text
git merge-base --is-ancestor 8fd7c6c c0dd5f8
# native exit 0

git cat-file -t c0dd5f895bf584d38b832102b3f1d81cf1c3e079
# commit

git rev-parse 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7 \
              c0dd5f895bf584d38b832102b3f1d81cf1c3e079 \
              'c0dd5f895bf584d38b832102b3f1d81cf1c3e079^{tree}'
# base    8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
# head    c0dd5f895bf584d38b832102b3f1d81cf1c3e079
# tree    0420848a0cb3c820536f26f583be280bdee714ee

git rev-list --count 8fd7c6c..c0dd5f8
# 330

git diff --shortstat 8fd7c6c c0dd5f8
# 587 files changed, 189015 insertions(+), 1622 deletions(-)
```

Frozen candidate = `eeaa90d` + two documentation commits:

```text
git diff --stat eeaa90dd579bc07e85641b0c32949ada90b55ae9 c0dd5f895bf584d38b832102b3f1d81cf1c3e079
# 2 files changed, 829 insertions(+)
#   V5-PR20-EMPTY-CLOSE-DIAGNOSIS-20261029.md
#   V5-TICKET-INSTANCE-DIAGNOSIS-20261029.md

git diff --quiet eeaa90d c0dd5f8 -- dotnet/src/Mahjong.TableService/TableSessionHandler.cs
# native exit 0 (production handler identical to the diagnosis candidate)
```

Extraction:

```text
git archive --format=tar -o .pr20-probe/archive/pr20-c0dd5f8.tar c0dd5f895bf584d38b832102b3f1d81cf1c3e079
# native exit 0; tar = 387,328,000 bytes
# SHA-256 264D062E4BE5D37A15DB96BC0A03257129605011ECF146A4E6F85F96A2029F8A

tar -xf .pr20-probe/archive/pr20-c0dd5f8.tar -C .pr20-probe
# native exit 0; 34 top-level entries
```

`.pr20-probe/` is excluded through the local `$GIT_COMMON_DIR/info/exclude` (not a tracked file). The only tracked change in this worktree is this report. No candidate file was copied into the report.

One launcher attempt was aborted before any service started because `pwsh` is not installed on this host (`CommandNotFoundException`); the run was relaunched with `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`. It produced no product evidence.

---

## 3. Environment and isolation

| Item | Value |
|---|---|
| Host | Windows, Windows PowerShell `5.1.19041.6456` |
| .NET SDK | `8.0.426` at `D:/Unity/dotnet/dotnet.exe` |
| PostgreSQL | `16.10` from the pinned cached zip, per-run `initdb -U mahjong_pg --auth-local=trust --auth-host=trust` |
| Cluster roots | `.pr20-probe/artifacts/b1-250ms-root`, `.pr20-probe/artifacts/b1-50ms-root` (both removed by the runner) |
| Log roots | `.pr20-probe/artifacts/b1-250ms-logs`, `.pr20-probe/artifacts/b1-50ms-logs` (preserved) |
| TLS | Fresh test root/leaf per run from the accepted certgen helper; per-connection strict root trust; no OS trust change |
| Databases | Two independent `mahjong_b1_lobby` / `mahjong_b1_table` clusters, one per run; no shared database server |
| Credentials | Disposable loopback trust-auth cluster; app roles `b1_lobby_app` / `mahjong_table_runtime`; all removed with the cluster |
| Ports | 250 ms: PG `59471`, Lobby `59472`, Table `59473`; 50 ms: PG `59571`, Lobby `59572`, Table `59573` |
| PIDs (transient) | 250 ms: Lobby `29968`, Table `52144`; 50 ms: Lobby `50360`, Table `42088` |
| Cleanup | Both runs: `stage cleanup PASS (exit 0)`, root removed, secret material removed, all six ports free, no postmaster |
| Other worktrees | Only read; not modified. Unrelated `postgres.exe` processes under `D:/majiang-pg-tmp/codex-integrated-r3-20261024` were observed and left untouched |

The scenario is the historical B1 topology: five real guest sessions (players p0–p3, spectator s0), real Lobby HTTPS over PostgreSQL, real TableService WSS over PostgreSQL, five distinct admitted session bindings, real commands/receipts, crash/recover in a new OS process, and the negative matrix. It is not an in-memory substitute.

---

## 4. Harness-only diagnostics

Production chain:

```text
TableSession
  -> HeartbeatTableChannel
  -> HeartbeatDiagnosticChannel   (new, test harness only)
  -> ReceiptLossChannel
  -> RealWssTableChannel
```

The tap sits **inside** `HeartbeatTableChannel`, so it sees both client-generated heartbeat envelopes and server Heartbeat/Close envelopes before the heartbeat decorator consumes them. Per frame it records:

- direction (in/out), byte count, envelope `MessageType`/`BodyCase`, decode result;
- for `Heartbeat`: `Ping`/`Pong`, nonce length, **SHA-256 prefix of the nonce** (never the raw nonce), pending-nonce match, bound flag;
- for `Close`: `Code`, `Code` length, `Permanent`, `WebsocketCloseCode`;
- for `Error`: `Code`, `DetailCode`;
- envelope `SessionId`/`RoomId` hashes, `SeatId`, `BindingEpoch`;
- the current bound identity hashes/seat/epoch and four match booleans;
- `TableSession` state, redacted `LastError`, and `HeartbeatSnapshot`;
- capture markers `after_five_client_baseline`, `suite_catch`, `suite_finally_before_close`, `crash_hold_armed`, `recover_before_dispose`.

`HeartbeatSnapshot` fields captured: `Bound`, `NegotiatedIntervalMs`, `NegotiatedIdleDeadMs`, pending nonce length/hash, `LastRoundTripMs`, `PingSentCount`, `PongReceivedCount`, `LastInboundAgeMs`, `LastPingAgeMs`, `TimedOut`. Note: the shipped `Snapshot` sets `TimedOut` from `session.Terminal != null`, not from the idle deadline; it is a terminal flag in this build.

Raw tickets, tokens, private hands, and raw nonces are not logged. The only change to the runner script is a `-RoomTickMs` parameter (default 500) so the same scenario can run at 250 ms and 50 ms.

Harness-only files changed in `.pr20-probe/` (ignored, not committed):

| File | SHA-256 |
|---|---|
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/HeartbeatDiagnosticChannel.cs` | `F0237ED5A9E5E1A843DF8D879019C80E691B63B3FBB0351CFE6EFB84D1C568FC` |
| `.../ClientHandle.cs` | `5DCCC57438F878E3562983E750F14C19B86083D89F9B7B2F8BAACFAC0FBF0AC3` |
| `.../Program.cs` | `434AD1824A97BC5E187053854EAEC3D16FA16704C31711E4DA2CDF16AADC8BDE` |
| `.../SuiteRunner.cs` | `86E09ACAD2F92A7048B3F282C8022FD7CB9CA4F785913952EBF00F53FC4DD4B5` |
| `.../CrashClient.cs` | `68B13B309CF29C6F3CC5FC76086B3611ADCFC5541CB74A4CFA6600D635852D9A` |
| `.../RecoverClient.cs` | `BFF5A54A8CB8D4D5D35C59FDD3CED654F0934BD35A817A11522F0792822A91EE` |
| `tools/ci/verify-v5-https-admission.ps1` | `8AB03A175BD16AE40C5F8F65A951B3DA75F3CFBCC62FF19F3F8FA7B1FAF80CEC` |

---

## 5. Commands and native exits

### 5.1 Harness build

```powershell
D:/Unity/dotnet/dotnet.exe build `
  .pr20-probe/dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/Mahjong.P5.4b.B1.HttpsAdmission.Worker.csproj `
  -c Release --nologo -v:minimal -nr:false
# native exit 0, 0 warnings, 0 errors
```

An earlier harness build failed with native exit **1** (`CS8604`, nullable `CloseAsync` argument) before any scenario ran; the harness was corrected and the build above is the one used. This is a harness defect, not a product result.

### 5.2 250 ms tick

```powershell
C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .pr20-probe/tools/ci/verify-v5-https-admission.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -PgPort 59471 -LobbyPort 59472 -TablePort 59473 `
  -Root   .pr20-probe/artifacts/b1-250ms-root `
  -LogDir .pr20-probe/artifacts/b1-250ms-logs `
  -RoomTickMs 250 -SuiteTimeoutSeconds 420
# wrapper native exit 0; output: P54B-B1-HTTPS-ADMISSION-PASS
```

### 5.3 50 ms tick

```powershell
C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .pr20-probe/tools/ci/verify-v5-https-admission.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -PgPort 59571 -LobbyPort 59572 -TablePort 59573 `
  -Root   .pr20-probe/artifacts/b1-50ms-root `
  -LogDir .pr20-probe/artifacts/b1-50ms-logs `
  -RoomTickMs 50 -SuiteTimeoutSeconds 420
# wrapper native exit 0; output: P54B-B1-HTTPS-ADMISSION-PASS
```

Both runs: 13/13 stages PASS, `tests-b1` 44/44 passed, 0 failed, 0 skipped, 0 not-run, native exit 0; `suite` stage PASS (worker exit 0); `pg-facts` PASS; `cleanup` PASS (exit 0). Rooms:

- 250 ms: `room_01a1887ab805788da72f2ded7c773e5f`
- 50 ms: `room_01a1887f7d2e77039b82f101f55aab87`

### 5.4 Focused client control

```powershell
D:/Unity/dotnet/dotnet.exe test `
  .pr20-probe/dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj `
  -c Release --nologo
# native exit 0; 80/80 passed, 0 failed, 0 skipped
```

`HeartbeatTableChannelTests.cs` contains 17 `[Fact]` methods, including `ValidPingIsEchoedAndNotReturnedToTheSession` (`:15`) and `HeartbeatWithDifferentBindingIdentityIsRejected` (`:73`). The whole WSS assembly ran (no filter), so the 80/80 result is the assembly count, not only those 17 facts. This control shows the client heartbeat contract is intentional; it does not exercise the tap.

---

## 6. What was observed

### 6.1 Five-client admission and the B1 assertions

Both runs:

- `AdmittedPlayers=4`, `SpectatorAdmitted=true`; the suite asserts five distinct session bindings (`p0` seat 0, `p1` seat 1, `p2` seat 2, `p3` seat 3, `s0` no seat).
- `BaselineFullForAll=true`, `PrivateStreamsIsolated=true`.
- Normal action: `NormalActionSeat=0`, `NormalActionStateVersion=1`, exact-byte replay version 1, `AllClientsSawNormalAction=true`.
- Crash/recover: `CrashCommandVersion=2`, `RecoveryFound=true`, `RecoveryPendingAfter=0`, `AllClientsSawCrashAction=true`.
- Negative matrix: 4 DB negative facts; the suite's negative results include `same_key_lookup_digest=UNAVAILABLE_B1_BLOCKER_03` and `canonicalReceiptIdentityVerified=false`.
- PG facts (both): `stateVersion=6`, `events=12`, normal `1/APPLIED` journal 1 event rows 2, crash `1/APPLIED` journal 1 event rows 2, settlement outbox 0, lobby consumption 0, ledger `NOT_RUN`.
- Suite assertions: 11/11 listed; no blocker code; `Blocked=false`.

So the B1 scenario itself ran end to end and its configured assertions passed.

### 6.2 The first server heartbeat faults four active clients

Both runs show the same pattern. The 50 ms run has the binding fields; the 250 ms run was captured with the first tap revision and has the same frame/fault pattern without the binding fields.

At 50 ms tick, `WELCOME` → first server PING:

| Client | PING after WELCOME | Server PING nonce (SHA-256 prefix) | Envelope session/room | Envelope seat / epoch | Bound seat / epoch | Match session/room/seat/epoch | Fault after PING |
|---|---:|---|---|---|---|---|---:|
| p0 | 5.023 s | `24D8A1A4669757A9` | empty / empty | 0 / 0 | 0 / 1 | F / F / T / F | 3.4 ms |
| p2 | 5.016 s | `608146C1A596E07C` | empty / empty | 0 / 0 | 2 / 1 | F / F / F / F | 0.3 ms |
| p3 | 5.010 s | `9EC75AC4E74C5877` | empty / empty | 0 / 0 | 3 / 1 | F / F / F / F | 0.2 ms |
| s0 | 5.009 s | `F4A72A7CCF51C032` | empty / empty | 0 / 0 | -1 / 1 | F / F / F / F | 0.2 ms |

The bound session/room hashes are non-empty and distinct; the room hash is `7AF2C8B9439D6B34` for all four, the session hashes are `77650B931037198B` (p0), `0DB76DECAC4BF16C` (p2), `4F6CC7E69155DC95` (p3), `A6D1C78AAF635654` (s0). The client records `PROTOCOL_VIOLATION/permanent` as the transport close reason immediately after each PING.

`HeartbeatSnapshot` for p0 at the PING:

```json
{"bound":true,"intervalMs":10000,"idleDeadMs":120000,"pendingNonceLen":null,
 "pendingNonceSha256_16":null,"lastRoundTripMs":null,"pingSent":0,"pongReceived":0,
 "lastInboundAgeMs":452,"lastPingAgeMs":5019,"timedOut":false}
```

At the failure close call the same snapshot has `timedOut:true` because the heartbeat session is terminal. At the final capture (73 s later) p0 is `Faulted`, `lastError=PROTOCOL_VIOLATION`, `pingSent=0`, `pongReceived=0`, `pendingNonce=null`, `lastInboundAgeMs=73012`.

This is decisive for attribution:

- **No client PING was ever sent** (`pingSent=0`) and **no PONG was received or sent** (`pongReceived=0`, all directions). The fault cannot be the "unmatched PONG" branch.
- The server PING arrived while `bound=true`; the fault cannot be the "heartbeat before WELCOME" branch.
- The only failing condition is the binding comparison: empty session/room and epoch 0 versus the admitted session/room and epoch 1 (p0's seat happens to equal 0, but the other three fields fail).

### 6.3 Server close frames and empty `Close`

Both runs:

- Inbound protocol `Close` frames: **0**; outbound protocol `Close` frames: **0**.
- `emptyCloseFrames=0` in the diagnostic summary.
- Server `close delivered` lines: **9/9 non-empty** — 6 × `LOCAL_CLOSED`, 3 × `INVALID_TOKEN`; no empty code, no `SLOW_CONSUMER` reservation in either run.
- The only client close calls are the four heartbeat-failure `PROTOCOL_VIOLATION/permanent` calls, the intentional p1 crash close (`b1-test-close/permanent`), and the final teardown calls.

Thus there is **no observation of an empty `Close.Code`**. The preserved final2 trace had no `Close` either; that conclusion in the accepted diagnosis remains correct. The new evidence adds a different producer of the same `LastError`.

### 6.4 The wrapper PASS is timing-dependent, not a liveness pass

Both `wrapper-summary.json` files report `exitCode=0`, `status=PASS`, `cleanupOk=true`, `secretMaterialRemoved=true`, 13 stages, `suite.Blocked=false`. But the worker's final captures in both runs are:

| Client | Final state | Final `LastError` |
|---|---|---|
| p0 | `Faulted` | `PROTOCOL_VIOLATION` |
| p1 | `Closed` | null (intentional crash/recover close) |
| p2 | `Faulted` | `PROTOCOL_VIOLATION` |
| p3 | `Faulted` | `PROTOCOL_VIOLATION` |
| s0 | `Faulted` | `PROTOCOL_VIOLATION` |

At suite end, **0/5 sessions are `Ready`**. The suite exit 0 implies its client assertions completed before the first 5 s PING; the suite continued for ~73 s after the first fault and ended with four `Faulted` active sessions and p1 intentionally `Closed`. The suite never asserts post-assertion liveness. The wrapper exit 0 therefore does not prove a healthy five-client session, and the suite's own `canonicalReceiptIdentityVerified=false` / `UNAVAILABLE_B1_BLOCKER_03` result is another reason not to read it as a gate pass.

### 6.5 Source trace: the producer is the server PING, not a Close

Server (`dotnet/src/Mahjong.TableService/TableSessionHandler.cs` in the frozen candidate):

- `LivenessLoopAsync` at `:815-834`; the timer is `HeartbeatIntervalMs / 2` at `:819` (10,000 ms configured → 5,000 ms), and the PING is enqueued at `:831` only when `_identity != null`.
- `BuildHeartbeatPing()` at `:2544-2557` returns a bare `Envelope` with protocol/version fields, `MessageType=Heartbeat`, and the PING body; it sets **no** `SessionId`, `RoomId`, `SeatId`, or `BindingEpoch`.
- `BuildEnvelope()` at `:2385-2403` sets `SessionId`, `RoomId`, `SeatId`, and `BindingEpoch=1` for ordinary envelopes, including the PONG at `:2574`.
- The PING/PONG asymmetry is therefore in the server source: PONG carries the admitted identity, PING does not.

Client (`dotnet/src/Mahjong.Client/HeartbeatTableChannel.cs`):

- `HandleHeartbeatAsync` at `:360-423`; before dispatching PING/PONG it calls `MatchesBinding` at `:381`.
- `MatchesBinding` at `:718-727` requires `bound`, non-empty `SessionId` and `RoomId`, equal `BindingEpoch`, equal `SessionId`/`RoomId`, and equal `SeatId`.
- A false result calls `FailLocked(PROTOCOL_VIOLATION, permanent:true)` (`:599-610`), which closes the inner channel (`CloseInnerAfterFailureAsync`, `:612-629`).
- `TableSession.HandleTransportFailure` at `:2019-2021` copies the code into `LastError`, producing the exact observed tuple.

For the empty-Close question, `TableSession.HandleInboundClose` at `:1977-1984` still maps an empty `Close.Code` to `PROTOCOL_VIOLATION`, but that path was not reached: no `Close` body arrived.

The final2 server handler contains the same `BuildHeartbeatPing()` body (`aa7d811...:2509-2522`), and the candidate's body at `:2544-2557` compares equal; the handler diff `aa7d811..c0dd5f8` has no hunk touching this function. The client triplet `TableSession.cs`, `HeartbeatTableChannel.cs`, `WebSocketTableChannel.cs` is byte-identical (`git diff --quiet ...` exit 0). Therefore final2's `state=Faulted, lastError=PROTOCOL_VIOLATION, inbound=Heartbeat` is now most plausibly explained by the same binding-less server PING — but the final2 frame itself only records `Heartbeat`, not its fields, so that last step is an inference, not a preserved frame-level observation.

---

## 7. Observation versus inference

### Observed

1. Both isolated B1 runs completed all configured assertions with wrapper exit 0.
2. The first server `Heartbeat` PING arrived 5.0 s after each `WELCOME`, matching `HeartbeatIntervalMs / 2`.
3. The PING envelope had empty `SessionId`/`RoomId`, `SeatId=0`, `BindingEpoch=0`; the bound identity was non-empty with epoch 1 and the admitted seat.
4. The client faulted with `PROTOCOL_VIOLATION/permanent` within 0.2–3.4 ms of each PING; final state `Faulted`, `LastError=PROTOCOL_VIOLATION`.
5. No client PING, no PONG, no pending nonce, no protocol `Close`, and no empty `Close.Code` were observed.
6. Server close logs were 9/9 non-empty and had no `SLOW_CONSUMER`.
7. At suite end, four active clients were `Faulted` and p1 was intentionally `Closed`; the wrapper still said PASS.
8. The focused WSS control passed 80/80, including the explicit "different binding identity is rejected" and "valid PING is echoed" facts.

### Inferred

1. `BuildHeartbeatPing()`'s missing identity is the direct cause of the observed client fault. This is a source-and-frame inference: the client's only matching branch that fails is `MatchesBinding`, and the captured fields show exactly which comparisons fail.
2. The same source defect most likely produced final2's identical tuple; the final2 handler and client sources are identical in the relevant functions, but final2's preserved trace lacks the PING fields.
3. The wrapper PASS is a timing-dependent false assurance for liveness: any action or wait that spans the first 5 s PING will encounter the fault. This follows from the deterministic timer and the `Faulted` terminal state; it was not separately run as a "hold then act" scenario.
4. The original B1-MAJOR-01 "empty Close" attribution is not supported. The client empty-Close sink is real, but no empty Close was produced or received in these runs or in the preserved traces.

### Not claimed

- No fix, no product decision on whether the server PING should carry identity or the client should accept a pre-binding/no-identity PING. That is a protocol/security decision for the owner.
- No declaration that B1-MAJOR-01 is closed, that PR20 is accepted, or that the B1 batch gate passes.
- No claim about G2–G5, V5 production, release, or deployment.
- No claim that the 5 s fault is the only heartbeat issue; only that it is the one observed and deterministically reproduced here.

---

## 8. Minimal next action

1. **Keep fail-closed behavior.** Do not default an empty `Close.Code`, do not ignore `Close`, and do not relax `MatchesBinding` just to make the suite green.
2. **Owner decision on heartbeat identity.** Either make `BuildHeartbeatPing()` use the same authoritative envelope builder as `BuildHeartbeatPong()` (session/room/seat/epoch), or explicitly define and test a pre-binding/no-identity heartbeat contract. This report does not choose or implement either.
3. **Add a liveness assertion after the existing B1 assertions** (a harness change, not a production change): require all five admitted sessions to remain `Ready` across at least one full server heartbeat period, so a late fault cannot be hidden by an early PASS. Do not weaken existing assertions.
4. **Re-run the B1 scenario at 50 ms and 250 ms** after the owner decision, with the same frame tap, and require 0 binding-mismatch heartbeat frames, 0 empty-close frames, and 5/5 healthy sessions at the end.
5. **Keep B1-MAJOR-01 open** until a preserved run contains either an empty-code `Close` with its producer or a heartbeat capture proving the exact branch. This report supplies the latter for the observed branch; it does not close the gate.

---

## Appendix A — preserved evidence and SHA-256

All paths are under `D:/majiang-wt/heartbeat-b1-probe-20261030/.pr20-probe/artifacts/` (ignored; not committed). `SHA256SUMS.txt` and `evidence-index.json` in the same directory contain the machine-readable index.

### 250 ms run

| File | SHA-256 |
|---|---|
| `b1-250ms-logs/suite.json` | `CEBF5945DEF984FC4D50886130B744EB3B7B60BBCE347567519893355D4D18D6` |
| `b1-250ms-logs/suite.json.heartbeat.jsonl` | `8D85D49070B3FEBDD9E790157176197EEC35022BDDDF6167AEE77543AF5F34E8` |
| `b1-250ms-logs/suite.json.heartbeat-summary.json` | `0936AC033A56B0534ECD63A73FC06501377B0D4CA80D3D12C9E9F901779BE566` |
| `b1-250ms-logs/table.stdout.log` | `B7E283D01618FEC3732D0AED8291089A02B846D2B09673266EB381158C7951A7` |
| `b1-250ms-logs/lobby.stdout.log` | `34B58712A01B5209BC2D2B3BF7A605661375ACD9B26E62D6BF304ECC9C4035B7` |
| `b1-250ms-logs/wrapper-summary.json` | `DBB5B4EC7AC7E3AD4374CC8E96DA628E92424A343BAD0308B9400773B69731C8` |
| `b1-250ms-logs/tests-b1.xml` | `6E7F35D53599ED4B75DFBE4C85382FF5CC7B56E30C555CFB67C0BBADA9BA83BC` |
| `b1-250ms-logs/manifest.sha256.txt` | `FDED0F914B3EC3042F1CAF8579692031454866920544D49F3C4B10A98763D992` |

Manifest: `32B5BE44556A800FD75B3668D5AD0C7B818556AF9684AEA6123A39960D6D359B`, 378 entries, 0 missing, 0 mismatched.

### 50 ms run

| File | SHA-256 |
|---|---|
| `b1-50ms-logs/suite.json` | `F7B165821DBA17812C34B27B97AC84722F3B6F26BA28E09003EB5C9C1525FBC7` |
| `b1-50ms-logs/suite.json.heartbeat.jsonl` | `648DE29D8DF32CFCA1FAFB89BFC78B2B6D716C3444BECBC6E9F8B4AADDC7A4C3` |
| `b1-50ms-logs/suite.json.heartbeat-summary.json` | `3CCC5FB6AAE1E4241970969172F697EEC54FB1A7DF6DB5B3106FDC81A674BA66` |
| `b1-50ms-logs/table.stdout.log` | `B1B8FF2715124A9F517ACA57F892541C89CF8DC7E3B9869F94D0A1B3E86AE887` |
| `b1-50ms-logs/lobby.stdout.log` | `99B71BA8CD5F0F4A324A5E4040A74C637EE3F95EBEA2516965880C3561107D5D` |
| `b1-50ms-logs/wrapper-summary.json` | `D0BC4BCAD3CC656AD19EEF2C802869154C7D222ECAC4B8A2D1C5056419D6A103` |
| `b1-50ms-logs/tests-b1.xml` | `101A3D7E6FF7B924D7E99137ACBC5F57D5F8CAB6A7318E64410EF4732E1B5FAA` |
| `b1-50ms-logs/manifest.sha256.txt` | `BD1BB5743C8DDC2190ADF0E182ED47F682FA771E6BCBA7A58D0F66B3D74B0F8E` |

Manifest: `BB225925E7AAE5E3D642F0454654DC02E89429C4A7F09EADEA6CE6D432645E52`, 378 entries, 0 missing, 0 mismatched.

### Shared control and archive

| Item | SHA-256 |
|---|---|
| `heartbeat-tests.stdout.log` (80/80, exit 0) | `6B4F54A6D70BCB99A32FE022B38AB58211C2EC731D9CC96069B86F543151F21F` |
| `b1-250ms-console.log` | `F24F9BF0B731FFB280C2BF1E58D44793BD47852EA434A28DA27B5A2B4AAE2095` |
| `b1-50ms-console.log` | `B208C303BB62C5D2E5D4DB5064738405F304D3ED569969456475CA838776E883` |
| `evidence-index.json` | `17C5CDBE8E5DA153083E8A31295F33816C291837D840A1271E15CA6335826FAF` |
| `SHA256SUMS.txt` | `37D9F6BE1E593E44A886544FEEFA27EC94DD29AF3E5A2810040ADE70D2B70560` |
| `.pr20-probe/archive/pr20-c0dd5f8.tar` (387,328,000 bytes) | `264D062E4BE5D37A15DB96BC0A03257129605011ECF146A4E6F85F96A2029F8A` |

---

## Appendix B — candidate line references used

```text
TableSessionHandler.cs:815-834   server liveness loop; /2 timer at :819; PING at :831
TableSessionHandler.cs:2385-2403 BuildEnvelope (SessionId/RoomId/SeatId/BindingEpoch=1)
TableSessionHandler.cs:2544-2557 BuildHeartbeatPing (bare envelope; no binding fields)
TableSessionHandler.cs:2559-2575 BuildHeartbeatPong (uses BuildEnvelope at :2574)
HeartbeatTableChannel.cs:360-423 heartbeat dispatch; binding check at :381
HeartbeatTableChannel.cs:718-727 MatchesBinding
HeartbeatTableChannel.cs:599-629 FailLocked / close inner
TableSession.cs:2019-2021        HandleTransportFailure copies the code to LastError
TableSession.cs:1977-1984        HandleInboundClose maps empty Close.Code to PROTOCOL_VIOLATION
HeartbeatTableChannelTests.cs:15 ValidPingIsEchoedAndNotReturnedToTheSession
HeartbeatTableChannelTests.cs:73 HeartbeatWithDifferentBindingIdentityIsRejected
```

Candidate comparison used for final2 attribution:

```text
aa7d81168ad175a4971514ac116cfff92e36434b  final2 run head (preserved evidence)
BuildHeartbeatPing bodies: aa7d811:2509-2522 == c0dd5f8:2544-2557 (string comparison true)
git diff --quiet aa7d811..c0dd5f8 -- dotnet/src/Mahjong.Client/{TableSession,HeartbeatTableChannel,WebSocketTableChannel}.cs
# native exit 0
```

---

## Appendix C — candidate freeze facts

```text
base      8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
candidate c0dd5f895bf584d38b832102b3f1d81cf1c3e079
parent    802fa5fc543f3275532b39a8a69c668775f77fd4
tree      0420848a0cb3c820536f26f583be280bdee714ee
ahead     330 commits
diff      587 files, +189,015 / -1,622
frozen    candidate = eeaa90d + 2 docs; production handler identical to eeaa90d
```
