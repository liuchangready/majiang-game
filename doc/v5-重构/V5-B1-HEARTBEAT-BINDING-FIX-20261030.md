# V5 B1 Server Heartbeat Binding Identity Fix (2026-10-30)

Status: **candidate fix plus reproducibility evidence for caller review. This is not an
acceptance, not a B1 batch PASS, and not a G2–G5 approval. PR20 remains unaccepted until
the caller reviews and integrates the patch. V4 remains the only production baseline.**

- Accepted baseline: `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
  (also this worktree's `HEAD`).
- Frozen unaccepted candidate: `928379f769e6c356d2fa8a9688026ab53b972278`
  (tree `a963b8adb5a1bda0626a9d47ae53e2653267caa6`; parent
  `c0dd5f895bf584d38b832102b3f1d81cf1c3e079`).
- Base worktree / branch: `D:/majiang-wt/b1-heartbeat-fix-20261030`,
  `wt/v5-b1-heartbeat-fix-20261030`.
- Patch: `doc/v5-重构/patches/V5-B1-HEARTBEAT-BINDING-FIX-20261030.patch`.
- This report is the only other committed file; the frozen candidate tree was extracted
  and modified only inside the ignored `build/b1-heartbeat-private/` directory.
- No push, PR, delegation, release, gate approval, or shared-worktree change.

---

## 0. Verdict

| Question | Result | Evidence |
|---|---|---|
| Is the server PING binding-identity defect fixed on the frozen candidate? | **PASS (candidate patch)** | `BuildHeartbeatPing()` now uses `BuildEnvelope(...)`, so the PING carries the current admitted session/room/seat/binding epoch and a fresh 16-hex nonce. |
| Did the strict frozen Client validation change? | **NO (PASS)** | No production Client file changed. The Client still rejects any bound heartbeat that does not match its WELCOME (`MatchesBinding`). |
| Player PING identity? | **PASS** | `ServerHeartbeatBindingTests.PlayerServerPing_CarriesAdmittedBinding_AndFreshNonce` (seat 2). |
| Spectator PING identity? | **PASS** | `ServerHeartbeatBindingTests.SpectatorServerPing_CarriesAdmittedBindingAndNoSeat` (seat `-1`). |
| Rebound/current connection identity and stale-session rejection? | **PASS** | `ReboundConnection_PingsUseTheNewBinding_AndOldSessionIsRejected` (new session ID, old session closed 4403). |
| Bindingless/stale PING negatives still rejected? | **PASS** | Two new `HeartbeatTableChannelTests` facts; 82/82 WSS tests pass. |
| Do the new server tests fail on the unpatched candidate for the right reason? | **PASS (expected failure)** | Unpatched TableService suite: **3 failed / 167 passed / 170 total**, exit **1**; actual envelope session ID is `""` where the admitted ID is non-empty. |
| Real isolated five-client HTTPS Lobby + WSS TableService + PostgreSQL scenario through multiple actual server heartbeat periods? | **PASS, twice** | Final runs 6/7: 13/13 wrapper stages PASS, **54/54** B1 tests PASS. Early all-five phase required **3 NEW PING/PONG pairs over ≥15 s**; post-recovery phase required **6 NEW PING/PONG pairs over ≥30 s**. Configured interval 10,000 ms; actual server send period `max(250, 10000/2)=5,000 ms`. Nonce-level phase correlation; binding mismatches 0, unexpected closes 0. |
| Is the B1 batch gate / PR20 accepted? | **NOT PASS / NOT RUN** | The wrapper exits 0, but the suite still reports `CanonicalReceiptIdentityVerified=false` and `same_key_lookup_digest=UNAVAILABLE_B1_BLOCKER_03`. That pre-existing B1 blocker is outside this narrow fix. |
| Were the B1 wrapper, verifier, TLS/certificate validation, or machine trust relaxed? | **NO** | Wrapper and all trust/verifier paths are byte-unchanged; the B1 audit tap is test-only and observes frames without rewriting them. |
| Were production checks or unrelated code changed? | **NO** | One production function changed (+9/−10 lines in `TableSessionHandler.cs`); everything else is test/harness code. |

Short conclusion: the observed first-PING fault was caused by the server emitting a bare
heartbeat envelope (`SessionId=""`, `RoomId=""`, `SeatId=0`, `BindingEpoch=0`) while the
strict Client required the admitted binding. The patch makes the PING use the same
authoritative envelope builder as every other outbound control frame. The heartbeat
liveness gate was also corrected: it captures per-client baselines at each phase start and
counts only in-phase frames, so an earlier phase can never satisfy a later one. Both final
isolated five-client runs now survive multiple **actual 5 s** send periods with exact
ping/pong correlation, and the unpatched candidate still fails the new server tests on the
exact missing identity.

### Rework record (round 2)

The first delivery's liveness helper compared **cumulative** audit counters from
`OpenAsync`. Because the first five-client phase already required ≥3, the later
post-recovery call with `minimumPings=2` could pass without any new heartbeat. That was a
real test/evidence defect, and the conclusion that the recovery phase spanned two periods
was not valid.

This rework replaces the helper with phase-local baselines and also fixes the interval
language: `LivenessLoopAsync` sends every `max(250, HeartbeatIntervalMs / 2)`, so the
10,000 ms configuration produces a **5,000 ms actual send period**, not 10,000 ms.

A first rework run (run 3) proved the new gate with 6/6 new PING/PONG at 30,008 ms, but
placing that 30 s phase **before the first business command** exceeded the 20 s action
reserve; the suite then timed out waiting for the normal discard. That run is retained as a
harness-placement failure, not a product failure. Runs 6/7 use a 3-new/15 s early
all-five phase (which still catches the original first-PING defect) and a 6-new/30 s
post-recovery phase where no action deadline is pending.

### Rework record (round 3)

The round-2 gate still had an off-by-one tail predicate: `newPings - phasePongs > 1`
treated **one** phase PING without its PONG as Ready, contradicting the "no unanswered
tail" comment and the 9/7 test. Round 3 replaces count subtraction with nonce-level phase
correlation: phase PINGs are the append-only nonce suffix after the baseline; a PONG is
credited only when its nonce equals a phase PING nonce; **any** unmatched phase PING
(`unansweredPings > 0`) keeps the phase Waiting. The audit snapshot copies counts and
ordered nonce ledgers under one lock, and the gate rejects a ledger that regressed or
changed prefix. The exact 7/6 boundary is now a regression test (`NewPingsMin=7`,
`NewPongsMin=6`, `Waiting`). Final runs 6/7 exercise this corrected gate.

---

## 1. Scope and boundaries

Changed inside the extracted candidate (patch):

- production: `dotnet/src/Mahjong.TableService/TableSessionHandler.cs` only;
- tests/harness: TableService regression tests, Client negative tests, B1 worker audit
  tap and liveness gates.

Not changed:

- `tools/ci/verify-v5-https-admission.ps1` (the original B1 wrapper is preserved
  byte-for-byte);
- the unified verifier, CI workflows, solution files, machine/user trust, OS certificate
  stores, `StrictRootTrust`, ticket signing/validation, or TLS policy;
- Unity `Resources`/Burst/ProjectSettings, Android/desktop shells, Kotlin production
  modules, shared worktrees, or other tasks' files;
- no candidate tree was merged, checked out into the base worktree, or committed as a
  whole.

The only base-worktree commit contains the patch and this report.

---

## 2. Candidate freeze and extraction

```powershell
git rev-parse HEAD
# 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
git rev-parse origin/v5
# 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
git merge-base --is-ancestor 8fd7c6c 928379f
# native exit 0
git rev-parse '928379f^{tree}'
# a963b8adb5a1bda0626a9d47ae53e2653267caa6
```

Extraction used `git archive --format=zip` into the ignored
`build/b1-heartbeat-private/` directory so UTF-8 repository paths were preserved
(the zip route, not a checkout or merge). Archive facts:

```text
build/b1-candidate-928379f.zip
bytes   328,497,194
sha256  9DB94D5024A2616A55F119FB5F8241239CD872C37F97885E0B14B0E21D73ACDB
```

A private git index was then initialized inside that ignored tree, pointed at the same
commit only to produce diffs and let the unmodified wrapper report the exact head. No
candidate file entered the base worktree's index except through the generated patch.

Candidate distance from accepted baseline:

```text
git diff --shortstat 8fd7c6c 928379f
# 588 files changed, 189447 insertions(+), 1622 deletions(-)
```

---

## 3. Root cause and fix

### 3.1 Confirmed producer

Accepted diagnostic report:
`doc/v5-重构/V5-PR20-B1-HEARTBEAT-PROBE-20261030.md` (committed at `928379f`).
It captured the first server PING 5.0 s after WELCOME with empty session/room,
`SeatId=0`, `BindingEpoch=0`; the bound identity was non-empty with epoch 1. The
Client faulted with `PROTOCOL_VIOLATION/permanent` within 0.2–3.4 ms. No Client PING,
no PONG, no protocol `Close`, and no empty `Close.Code` were observed.

Server code before the fix (`TableSessionHandler.cs`):

```csharp
private static Envelope BuildHeartbeatPing()
{
    string nonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
    var heartbeat = new Heartbeat { Ping = new Ping { Nonce = nonce } };
    return new Envelope { /* protocol fields + Heartbeat only */ };
}
```

The frozen Client's only failing branch was `MatchesBinding`, which requires all of:
bound, non-empty bound session/room, equal binding epoch, equal session/room, equal seat.
The server PONG already used `BuildEnvelope`, so PING and PONG disagreed.

### 3.2 Fix

`BuildHeartbeatPing()` is now an instance method and delegates to the one authoritative
envelope builder:

```csharp
private Envelope BuildHeartbeatPing()
{
    string nonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
    var heartbeat = new Heartbeat { Ping = new Ping { Nonce = nonce } };
    return BuildEnvelope(MessageType.Heartbeat, heartbeat, correlationId: null);
}
```

`BuildEnvelope` resolves the connection's `_identity` and writes `SessionId`, `RoomId`,
`SeatId`, and `BindingEpoch=1`; the nonce remains a fresh random 16-character lowercase
hex string. The liveness loop already calls this builder only when `_identity != null`.
No Client production code, binding check, nonce validator, TLS path, or trust decision
was touched.

---

## 4. Patch contents

Patch path:
`doc/v5-重构/patches/V5-B1-HEARTBEAT-BINDING-FIX-20261030.patch`

```text
bytes   71,072
sha256  16AE6536E406444407DEC9BB71EB34C5C2B44B2B3B3CE15B46A5F89F38EB1643
```

`git diff --numstat 928379f` (extracted tree):

| File | +/− | Role |
|---|---:|---|
| `dotnet/src/Mahjong.TableService/TableSessionHandler.cs` | +9/−10 | **only production change**: PING uses the authoritative envelope builder |
| `dotnet/tests/Mahjong.TableService.Tests/ServerHeartbeatBindingTests.cs` | +217/−0 | player, spectator, rebound/stale server regressions |
| `dotnet/tests/Mahjong.TableService.Tests/TestSupport/TableTestClient.cs` | +19/−0 | binding-preserving PONG helper |
| `dotnet/tests/Mahjong.Client.Wss.Tests/HeartbeatTableChannelTests.cs` | +36/−0 | bindingless and stale-session negative tests |
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Tests/HeartbeatLivenessGateTests.cs` | +253/−0 | phase-gate negatives, including exact 7/6 and zero-new-heartbeat |
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/HeartbeatBindingAuditChannel.cs` | +330/−0 | test-only frame tap with atomic nonce ledgers |
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/HeartbeatLivenessGate.cs` | +433/−0 | phase-local baseline, nonce correlation, exact tail rejection |
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/ClientHandle.cs` | +8/−1 | wires the test-only tap and negotiated interval |
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/SuiteRunner.cs` | +149/−0 | phase-local liveness gates, preserving existing assertions |
| `dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/B1Models.cs` | +36/−0 | phase start/end/delta evidence fields |

Totals: **10 files, +1,490/−11**.

Apply verification against a fresh extraction of exact `928379f`:

```powershell
git apply --check --verbose <patch>
# native exit 0; all 10 files checked
git apply <patch>
# native exit 0
```

After applying, all six modified existing files are byte-identical to the tested tree.
The four new files differ from the tested tree only by `core.autocrlf=true` CRLF
normalization; after normalizing line endings their contents compare equal. No fuzz or
offset was used.

---

## 5. Regression tests and native results

### 5.1 New regression coverage

TableService (server-side, real Kestrel + real WebSocket host):

1. `PlayerServerPing_CarriesAdmittedBinding_AndFreshNonce`
   - seat 2 player; PING must equal WELCOME session/room/seat/epoch;
   - nonce must match `^[0-9a-f]{16}$`;
   - binding-preserving PONG must not close the session;
   - next PING must have the same binding and a fresh nonce;
   - a Client PING after the PONG must be answered with its exact nonce, proving the
     receive path is still live.
2. `SpectatorServerPing_CarriesAdmittedBindingAndNoSeat`
   - spectator PING must carry the admitted session/room and `SeatId=-1`, epoch 1;
   - same PONG/fresh-PING/live-receive assertions.
3. `ReboundConnection_PingsUseTheNewBinding_AndOldSessionIsRejected`
   - same account/room/seat rebound with a fresh `jti`;
   - old session closed with `4403 DISPLACED_BY_NEW_CONNECTION`;
   - old displaced session never receives a heartbeat for the new session ID;
   - new session's PING carries the new session ID with the same room/seat/epoch and a
     fresh usable nonce.

Client (strictness preserved):

4. `BindinglessServerPingIsRejected` — exact defect shape (empty session/room, seat 0,
   epoch 0) still produces `PROTOCOL_VIOLATION / permanent`.
5. `PingWithStaleSessionIdentityIsRejected` — a PING carrying a previous/session-foreign
   ID is rejected with `PROTOCOL_VIOLATION / permanent`.

B1 worker (test-only):

6. `HeartbeatBindingAuditChannel` decodes a copy of each passing frame and records:
   server PING count, matched PONG count, unmatched PONG, PING/PONG binding mismatch,
   empty identity, inbound/outbound close, transport close, and ordered PING/PONG nonce
   ledgers. Counts and ledgers are copied under one lock (`CaptureCounters`) so a phase
   sample cannot mix a count from one instant with a nonce list from another. It never
   rewrites, drops, reorders, or answers a frame. The production `HeartbeatTableChannel`
   still performs the strict accept/reject.
7. `HeartbeatLivenessPhase` captures a per-client counter/nonce baseline at **phase
   start** and evaluates only in-phase frames, tracked with `Stopwatch` monotonic time.
   Phase PINGs are the append-only nonce suffix after the baseline; a PONG is credited
   only when its nonce equals a **phase PING nonce**. A PONG for a pre-phase in-flight
   PING is therefore not credited to a phase PING, and **any** phase PING without its own
   PONG makes the phase NotReady (`unansweredPings > 0`, not `> 1`). The early all-five
   phase requires **3 new PING/PONG pairs over ≥15 s** (three actual 5 s send periods);
   the post-recovery phase requires **6 new PING/PONG pairs over ≥30 s** (six actual 5 s
   send periods). Each phase also requires every client Ready, every PING binding correct,
   no binding/close counter change, and no close.
8. `HeartbeatLivenessGateTests` pins the gate itself (10 facts), including the required
   negative: a phase that starts with 6/6 cumulative counters and observes **zero new
   PINGs/PONGs** remains `Waiting` even after 60 s and can never pass. It also pins the
   exact off-by-one boundary: 7 phase PINGs with 6 matched PONGs and a satisfied 30 s span
   is `Waiting`, with `NewPingsMin=7`, `NewPongsMin=6`, and an `!1` unanswered marker.
   Other facts cover short spans, PONG deficits, binding mismatch, close, leaving Ready,
   and a pre-phase in-flight PONG whose nonce is not credited to a phase PING.

### 5.2 Native commands, exits, counts

| Command | Native result |
|---|---|
| `dotnet build` B1 Worker (`-c Release -nr:false`) | exit **0**, 0 warnings, 0 errors |
| `dotnet test` TableService.Tests (patched) | exit **0**, **170/170 passed**, 0 failed, 0 skipped |
| `dotnet test` Client.Wss.Tests (patched) | exit **0**, **82/82 passed**, 0 failed, 0 skipped |
| `dotnet test` TableService.Tests (unpatched candidate + new tests) | exit **1**, **167/170 passed, 3 failed**, 0 skipped |
| `dotnet test` B1 HTTPS admission tests | exit **0**, **54/54 passed**, 0 failed, 0 skipped (44 existing + 10 gate facts) |
| `git diff --check` | exit **0** |
| `git apply --check` on clean `928379f` | exit **0** |
| B1 wrapper final run 6 (ports 59971/59972/59973) | exit **0**, `P54B-B1-HTTPS-ADMISSION-PASS`, 13/13 stages |
| B1 wrapper final run 7 (ports 60071/60072/60073) | exit **0**, `P54B-B1-HTTPS-ADMISSION-PASS`, 13/13 stages |
| B1 wrapper intermediate run 3 (ports 59671/59672/59673) | exit **1** — harness timing failure only; see §6.4 |

The unpatched failure is the intended regression proof:

```text
ServerHeartbeatBindingTests.SpectatorServerPing_CarriesAdmittedBindingAndNoSeat
  Assert.Equal() Failure: Strings differ
  Expected: "<admitted session id>"
  Actual:   ""
ServerHeartbeatBindingTests.PlayerServerPing_CarriesAdmittedBinding_AndFreshNonce
  Expected: "<admitted session id>"
  Actual:   ""
ServerHeartbeatBindingTests.ReboundConnection_PingsUseTheNewBinding_AndOldSessionIsRejected
  Expected: "<admitted session id>"
  Actual:   ""
```

B1 unit tests inside both final wrapper runs: **54/54 passed, 0 failed, 0 skipped, 0 not-run**
(44 pre-existing B1 facts plus 10 phase-gate facts; XML SHA-256 differs between runs only
because the test result document includes run-specific timing).

---

## 6. Real isolated five-client B1 scenario

Topology per run (no in-memory service substitute):

- Windows, Windows PowerShell 5.1, .NET SDK **8.0.426** (`D:/Unity/dotnet/dotnet.exe`);
- PostgreSQL **16.10**, per-run disposable loopback cluster under the ignored tree,
  `initdb -U mahjong_pg --auth-local=trust --auth-host=trust`;
- real **LobbyService over HTTPS** with PostgreSQL;
- real **TableService over WSS** with PostgreSQL host registry;
- five real guest sessions (p0–p3 players, s0 spectator), five distinct admitted
  bindings, Lobby-signed tickets, real commands/receipts, crash/recover in a new OS
  process, and the negative matrix;
- `TABLESERVICE_HEARTBEAT_INTERVAL_MS=10000`, `IDLE_DEAD_MS=120000`,
  `ROOM_TICK_INTERVAL_MS=500`;
- run-local TLS root/leaf per connection strict-root trust; no OS trust change.

| Item | Final run 6 | Final run 7 |
|---|---|---|
| Ports (PG/Lobby/Table) | 59971 / 59972 / 59973 | 60071 / 60072 / 60073 |
| Room ID | `room_01a188e430ae723e95987063b6a421cc` | `room_01a188e8980f7e0da12a7a8156166134` |
| configured heartbeat interval | 10,000 ms | 10,000 ms |
| actual server PING send period | **5,000 ms** (`max(250, 10000/2)`) | **5,000 ms** |
| early phase UTC start → end | `18:20:25.021Z` → `18:20:40.125Z` | `18:25:13.764Z` → `18:25:28.894Z` |
| early phase monotonic elapsed | **15,102 ms** | **15,129 ms** |
| early phase NEW PINGs/PONGs per client (min) | **3 / 3** | **3 / 3** |
| early phase summary (all five) | `0:3/3,1:3/3,2:3/3,3:3/3,-1:3/3` | `0:3/3,1:3/3,2:3/3,3:3/3,-1:3/3` |
| post-recovery phase UTC start → end | `18:20:44.489Z` → `18:21:14.498Z` | `18:25:33.349Z` → `18:26:03.428Z` |
| post-recovery monotonic elapsed | **30,009 ms** | **30,079 ms** |
| post-recovery NEW PINGs/PONGs per client (min) | **6 / 6** | **6 / 6** |
| post-recovery summary (four live) | `0:6/6,2:6/6,3:6/6,-1:6/6` | `0:6/6,2:6/6,3:6/6,-1:6/6` |
| heartbeat binding mismatches | 0 | 0 |
| unexpected closes during phases | 0 | 0 |
| normal APPLIED version | 1 | 1 |
| crash APPLIED version | 3 | 3 |
| recovery found / pending after | true / 0 | true / 0 |
| all clients saw normal / crash action | true / true | true / true |
| `Blocked` | false | false |
| wrapper exit / stages | 0 / 13 PASS | 0 / 13 PASS |

Observed heartbeat liveness lines (final runs, exact values):

```text
# run 6
B1-HEARTBEAT-OK 0:3/3,1:3/3,2:3/3,3:3/3,-1:3/3 newPingsMin=3 newPongsMin=3 elapsedMs=15102 configuredMs=10000 actualPingMs=5000
B1-HEARTBEAT-POST-RECOVERY-OK 0:6/6,2:6/6,3:6/6,-1:6/6 newPingsMin=6 newPongsMin=6 elapsedMs=30009

# run 7
B1-HEARTBEAT-OK 0:3/3,1:3/3,2:3/3,3:3/3,-1:3/3 newPingsMin=3 newPongsMin=3 elapsedMs=15129 configuredMs=10000 actualPingMs=5000
B1-HEARTBEAT-POST-RECOVERY-OK 0:6/6,2:6/6,3:6/6,-1:6/6 newPingsMin=6 newPongsMin=6 elapsedMs=30079
```

The first number is the seat, then **phase-local** `newPINGs/newPONGs` derived from the
ordered nonce ledgers after the baseline captured at that phase's start. These are not
cumulative counters, and a PONG is credited only when its nonce equals a phase PING nonce.
The early phase is three actual 5 s send periods over at least 15 s; it is deliberately
**not** described as three configured 10 s periods. The post-recovery phase is six actual
5 s send periods over at least 30 s. The 15 s early span stays below the 20 s first-action
reserve; the 30 s span is placed after recovery, where no business action deadline is
pending.

An intermediate rework run (run 3, ports 59671/59672/59673) proved the 30 s phase with
`0:6/6,... elapsedMs=30008`, but placing that 30 s gate before the first business command
made the suite time out waiting for the normal discard; the wrapper reported
`P54B-B1-HTTPS-ADMISSION-FAIL` with exit 1. That is a harness-placement failure, not a
product failure, and it is not counted as a successful scenario. Final runs 6/7 use the
split placement above.

Unexpected-close checks:

- no `PROTOCOL_VIOLATION`, `HEARTBEAT_TIMEOUT`, or heartbeat-fault line in run 6 or run 7;
- server-delivered `Close` codes were non-empty: both final runs `2 × LOCAL_CLOSED` and
  `3 × INVALID_TOKEN`;
- no empty `Close.Code` was observed. The intentional p1 crash/recover close is outside
  the early five-session phase, and the post-recovery phase excludes p1.

Negative matrix still passes unchanged (both runs): untrusted root, wrong SAN, expired
TLS rejected for HTTPS and WSS; cross-account receipt query `SCOPE_MISMATCH`; spectator
player command `SCOPE_ROLE_NOT_AUTHORIZED`; tampered/foreign-room/expired tickets
rejected; same-key different-bytes preserved the original receipt.

---

## 7. Cleanup and final base-worktree state

Both wrapper runs report:

```text
stage cleanup: PASS (exit 0) ownership-verified cluster removed; ports free; no postmaster
```

Verified after the final runs (and the intermediate run 3):

- all nine owned ports from final runs 6/7 and intermediate run 3 are free;
- all per-run root directories from runs 3/6/7 are removed;
- no `postgres.exe` owned by any run remains;
- run-local TLS/keys and credentials are removed with the root;
- unrelated `postgres.exe` processes from other worktrees were not touched.

Base worktree after committing the patch and this report:

- exactly two committed files: the patch and this report;
- no production/test/verifier/Unity/shared-worktree change;
- `git status --porcelain` empty (apart from ignored `build/`);
- no push, PR, delegation, release, or gate approval.

---

## 8. Limitations and explicit non-claims

1. **Candidate result, not acceptance.** All PASS statuses above describe the frozen
   candidate plus this patch. PR20 and the B1 batch gate are not accepted by this work.
2. **B1 gate is still not PASS.** The wrapper exits 0 and the heartbeat defect is fixed,
   but the suite preserves `CanonicalReceiptIdentityVerified=false` and
   `same_key_lookup_digest=UNAVAILABLE_B1_BLOCKER_03`. That is a pre-existing receipt /
   digest blocker, outside this narrow heartbeat change. This report does not close
   B1-MAJOR-01 or any other gate item.
3. **Loopback isolation only.** HTTPS/WSS/PostgreSQL ran on loopback with disposable
   per-run resources. No cross-physical-network, public-internet, Android, Unity,
   IL2CPP, or device test was performed or claimed.
4. **Room tick fixed by the preserved wrapper.** Final runs used the wrapper's existing
   `ROOM_TICK_INTERVAL_MS=500`; the prior probe's harness-only 50 ms/250 ms parameter is
   deliberately not part of this patch. The heartbeat gate is tick-independent and uses
   phase-local frames: three actual 5 s send periods (~15 s) for the early all-five phase
   and six actual 5 s send periods (~30 s) for the post-recovery phase. It does **not**
   claim three configured 10 s periods.
5. **Rebound scope.** The rebound/current-connection test drives the real TableService
   admission and displacement path directly (same account/room/seat, fresh `jti`); it
   does not re-run the Lobby HTTP ticket flow for a second admission.
6. **Test-only observability.** `HeartbeatBindingAuditChannel` and the suite liveness
   gates live in the B1 worker test project. They observe the production Client and
   transport but do not ship as production code and do not replace the Client's strict
   decision.
7. **No new server inbound binding validation.** The patch corrects outbound PING
   identity; it does not add a new server-side rule for inbound PONG binding. The
   frozen Client remains strict, and server displacement closes the stale session with
   4403. No production check was relaxed.
8. **No empty-Close claim.** No empty `Close.Code` was observed in either run; the
   accepted diagnostic's empty-Close question remains as previously recorded.
9. **G2–G5 remain unapproved. V4 remains production.**

---

## 9. Exact commands

Extraction and patch checks:

```powershell
git archive --format=zip --output=build/b1-candidate-928379f.zip 928379f769e6c356d2fa8a9688026ab53b972278
Expand-Archive -Path build/b1-candidate-928379f.zip -DestinationPath build/b1-heartbeat-private -Force
git apply --check --verbose doc/v5-重构/patches/V5-B1-HEARTBEAT-BINDING-FIX-20261030.patch
git apply doc/v5-重构/patches/V5-B1-HEARTBEAT-BINDING-FIX-20261030.patch
```

Focused builds/tests (from the extracted ignored tree):

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/Mahjong.P5.4b.B1.HttpsAdmission.Worker.csproj -c Release --nologo -v:minimal -nr:false
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Tests/Mahjong.P5.4b.B1.HttpsAdmission.Tests.csproj -c Release --nologo
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --nologo
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj -c Release --nologo
```

Unpatched control used a separate clean extraction of the same archive with only the new
test files copied in (production file untouched), then the same `dotnet test` command.

Real B1 final runs (original wrapper, unmodified):

```powershell
C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-https-admission.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -PgPort 59971 -LobbyPort 59972 -TablePort 59973 `
  -Root artifacts/b1-run6-root -LogDir artifacts/b1-run6-logs `
  -SuiteTimeoutSeconds 480

C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-https-admission.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -PgPort 60071 -LobbyPort 60072 -TablePort 60073 `
  -Root artifacts/b1-run7-root -LogDir artifacts/b1-run7-logs `
  -SuiteTimeoutSeconds 480
```

Both produced `P54B-B1-HTTPS-ADMISSION-PASS` with native exit 0. The intermediate run 3
(`-PgPort 59671 ... -Root artifacts/b1-run3-root ...`) produced
`P54B-B1-HTTPS-ADMISSION-FAIL` with exit 1 for the harness-placement reason documented in
§6; its cleanup still passed.

---

## 10. Evidence paths and SHA-256

All paths below are under
`D:/majiang-wt/b1-heartbeat-fix-20261030/build/b1-heartbeat-private/artifacts/`
(ignored; not committed). Logs are candidate evidence, not acceptance evidence.

### Final run 6

| File | SHA-256 |
|---|---|
| `b1-run6-logs/suite.json` | `1E5C10EF718E0CDE89B2CD2FA21F9FB4F0BC680BBE8943AFE29E31C2010C3A65` |
| `b1-run6-logs/suite.log` | `54BCEFE90F55E4564206227FF201149E217A23E31B04B87E42965408DE3B7B7F` |
| `b1-run6-logs/wrapper-summary.json` | `49C785B305652375D635891C923A91414FB1C94BD76BD0A70A147BB1A06B683C` |
| `b1-run6-logs/tests-b1.xml` | `9F68532675D1FFE4CB9E25E4389BBB8985E5AD2F1C5B77699BEED93448D0A868` |
| `b1-run6-logs/table.stdout.log` | `C4649AB628AB18890397E573E0BC0309BAC9669D539F65F542032E160C2F83D3` |
| `b1-run6-logs/lobby.stdout.log` | `8F810C09CE19D3E93C7D8359AD2996B6503555E9C43466F34ACEC941D0BADFD7` |
| `b1-run6-logs/manifest-audit.json` | `F415E2BE07508EDE79E22DB632B4D1420E377D45B56C2B87DB5889D18251850F` |
| `b1-run6-logs/runner.log` | `E000D363D1626ACB3A0884D51C3ECE3301623F91A07509A4B35779C4135EAC57` |
| `build/b1-run6-console.log` | `1AA0A95958DEB4A0A9BEC35E5A29FF5B56E8AB3CA8CEEC8EB83395010F381FF6` |

Manifest: **374 entries**, hash `0C0785C10EB51CCDB2200C7FB9F099D885A443D3B03F6929A079F39D0CAB0632`,
**0 missing, 0 mismatched**.

### Final run 7

| File | SHA-256 |
|---|---|
| `b1-run7-logs/suite.json` | `82F820A85D171DA03A3AF211369F7D9447480C766BE4675AB885E238DDFF901A` |
| `b1-run7-logs/suite.log` | `0BDA888ED5BC559272B32C93445E1361D2127B5F1FFE8FB5A954A10A59937FB9` |
| `b1-run7-logs/wrapper-summary.json` | `4749108FCD6B3341968BEBD23E88B656D02F6CCE7FBF4BE3288F63E4C0B05262` |
| `b1-run7-logs/tests-b1.xml` | `89A23A20454AC3C13056AEEE2EACDB0F1E37B3CC03B8DA5EFAAA24E363ADCF28` |
| `b1-run7-logs/table.stdout.log` | `A2721059169F939323E316CFC0D1E472061CD87BA164348D23D85358642EC914` |
| `b1-run7-logs/lobby.stdout.log` | `91086E8F67FCE0ED075BF4DD7F939EF5850345BF0AE293609F16741B7D39C5B9` |
| `b1-run7-logs/manifest-audit.json` | `504833D41E302BD049F925BD1951845A84336785A6B7BF75C4FAAA729B3B0591` |
| `b1-run7-logs/runner.log` | `F1A49B0AE0EF1058DE2876618AAEF421B3D9D22B6644F1B2EEC6CC0B1D5B2DB6` |
| `build/b1-run7-console.log` | `3794FFD486FAB83E9615C84FDEF7779EAEB112D9D3D039462C5029CE7F5058A8` |

Manifest: **374 entries**, hash `E72845D388A7BB270A0F4D26BD18A07917F2707CD9A46BD8E8954DFF08FF92B3`,
**0 missing, 0 mismatched**.

### Intermediate harness-placement failure (run 3, not a product result)

| File | SHA-256 |
|---|---|
| `b1-run3-logs/suite.json` | `EB304D5D297709CAC5F9D6246419BE5F03CD4223233606752405046A52CA3C38` |
| `b1-run3-logs/suite.log` | `FE84CDB4321A7B04F6663BC047727E7992FF6DB73EAC4F71184EF6B87E53483B` |
| `b1-run3-logs/wrapper-summary.json` | `0606FA09E488291D8A97F07559F8B3EFFA8055830EF396D98DEBB0F3A691ADDF` |
| `b1-run3-logs/manifest-audit.json` | `80FEE488F3A866ABACADF345433A398B7B803C31ACF4668851425873DEA333C3` |
| `build/b1-run3-console.log` | `4A8A4BB6C5A18FBF2D74029ACBB8F1082AD6C833BE295248891A39BA75C21A2A` |

Run 3 passed the 6/6 `elapsedMs=30008` phase but timed out later because that 30 s phase
preceded the first business command; see §6. Its cleanup still passed. It is not counted
as a successful scenario.

### Focused test logs and patch

| Item | SHA-256 |
|---|---|
| `build/server-tests-1.log` (patched 170/170) | `DF6F555504871D83E0843051561378B6D4772B868E3C30CB318F910B234DD1B5` |
| `build/wss-tests-1.log` (82/82) | `3931E0145950E4667153E5497FA81637FD23DC0FCF8BBF58677F0C81B9B51972` |
| `build/unpatched-server-tests.log` (3 failed) | `FBBC44361BC438E0E8BA42941218AC0E73957E7F676AF885167AAD621654DF74` |
| `build/b1-gate-tests-final.log` (54/54) | `430B2A688FAFF39CED4EB1F27BE49F3C61F43E2DD34179598A2A74BBA0D7C6DB` |
| patch file | `16AE6536E406444407DEC9BB71EB34C5C2B44B2B3B3CE15B46A5F89F38EB1643` |
| candidate archive zip | `9DB94D5024A2616A55F119FB5F8241239CD872C37F97885E0B14B0E21D73ACDB` |

---

## 11. Final statement

This work fixes only the server-generated heartbeat PING identity and adds the tests and
test-only observation needed to prove the fix at unit and real multi-client levels. The
strict Client, B1 wrapper, verifier, certificate validation, and machine trust are
unchanged. The five-client scenario now survives phase-local heartbeat gates with matching
ping/pong and no unexpected close: 3 new PING/PONG pairs over ≥15 s for all five sessions
(three actual 5 s send periods), and 6 new pairs over ≥30 s after the real process
crash/recover (six actual 5 s send periods).

The result is **candidate evidence**, not acceptance. PR20 remains unaccepted, the B1
batch gate remains blocked by its pre-existing receipt-identity item, G2–G5 remain
unapproved, and V4 remains the only production baseline. The caller reviews the patch,
integrates it, and decides the gate.
