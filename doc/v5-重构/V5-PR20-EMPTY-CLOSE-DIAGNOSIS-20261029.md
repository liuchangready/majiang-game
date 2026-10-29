# V5 PR20 Empty-Close Diagnosis (2026-10-29)

Status: **independent diagnosis only. Not an acceptance, not a fix, not a batch PASS.**
Accepted baseline: `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
Frozen unaccepted candidate: **`eeaa90dd579bc07e85641b0c32949ada90b55ae9`** (Draft PR20 head).
Inspection method: `git archive` of the candidate into ignored `build/pr20-candidate-eeaa90d`; candidate code was **not** merged, checked out into this worktree, or committed.
Worktree: `D:/majiang-wt/close-diagnosis-ds-20261029`, branch `wt/v5-close-diagnosis-20261029`.
V4 remains the only production baseline; G1 is approved; **G2–G5 remain unapproved**.

---

## 0. Verdict

| Question | Verdict |
|---|---|
| Did the frozen candidate/production server emit a `Close` with an empty `Code` in the preserved evidence? | **NOT_REPRODUCED / NOT_OBSERVED** |
| Is the client's empty-code-`Close` behavior real? | **PASS (synthetic probe)**: empty `Close.Code` deterministically becomes `PROTOCOL_VIOLATION` and `Faulted` |
| Does the preserved final2 `lastError=PROTOCOL_VIOLATION` prove an empty `Close`? | **NO.** The final2 inbound trace contains no `Close` frame; the exact symptom is evidence-consistent with a heartbeat-validation fault instead |
| Does the preserved final9 evidence contain the `lastError=PROTOCOL_VIOLATION` claimed by the B1 delivery doc? | **NO. FAIL (documentation/evidence contradiction)** |
| Is B1-MAJOR-01 ("empty Close code") closed by this report? | **NO. UNRESOLVED / NOT_REPRODUCED** |
| Was the original B1 HTTPS/WSS/PostgreSQL suite rerun here? | **NOT_RUN** (no shared PostgreSQL, no TLS trust changes) |
| Is the B1 batch gate now PASS? | **NO. It remains FAIL/BLOCKED on the already-recorded receipt/digest and LocalHost blockers** |

Short conclusion: the **client-side sink** for an empty `Close.Code` is real and fail-closed, but the **server-side source** of an empty code is not present in the frozen candidate, and the preserved "empty Close" attribution is not supported by the preserved frames. **Three preserved runs** (final2, rework12, smoke) show the identical tuple `state=Faulted, lastError=PROTOCOL_VIOLATION`; where an inbound trace exists it ends at `Heartbeat` and contains **no `Close` envelope**. That points to the client heartbeat decorator (`HeartbeatTableChannel`), not to `TableSessionHandler.SendCloseAndCloseAsync`, as the evidence-consistent cause.

---

## 1. Freeze, isolation and scope

| Item | Value |
|---|---|
| Accepted baseline | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| Frozen candidate | `eeaa90dd579bc07e85641b0c32949ada90b55ae9` (parents `59bee8e6...`, `565568ce...`; subject `docs(v5): integrate B1 lookup digest diagnosis`) |
| Candidate relative to baseline | ancestor check exit 0; **328 commits ahead**; `git diff --stat` = 585 files, 188,186 insertions, 1,622 deletions |
| Candidate extraction | `git archive --format=zip --output=build/pr20-candidate-eeaa90d.zip eeaa90d...` then `Expand-Archive` (native exits 0) |
| Candidate code committed here | **none** |
| Production/test/sln/verifier/Unity changes | **none** |
| PG / TLS / machine trust | **not used or changed** |
| Push / PR / delegation / release / gate approval | **none** |

The candidate tree was only read from `build/pr20-candidate-eeaa90d`; the probe built it into that ignored tree and into `build/pr20-close-probe`. No candidate source was copied into the report commit.

---

## 2. Preserved evidence: what actually exists

Preserved B1 run directories are in the other worktree
`D:/majiang-wt/p54b-b1-https-admission-ds-20261029-1250/artifacts/` and were read only.

### 2.1 final2 — one of three preserved exact `PROTOCOL_VIOLATION` runs

`artifacts/b1-final2-logs/suite.json`, last `Assertions` entry:

```text
FAILED: TimeoutException: No definitive receipt for request req-normal-1109e5f5e6ca4a5690e269d67e099dcf
(state=Faulted, lastError=PROTOCOL_VIOLATION, inbound=Heartbeat).
```

Key facts:

- `NormalActionSeat = -1`, `NormalActionStateVersion = 0`, `AllClientsSawNormalAction = false`.
- The client is `Faulted`; `LastError` is exactly `PROTOCOL_VIOLATION` (no `: message` suffix).
- `inbound=Heartbeat` is the **last 20** inbound envelopes; there is **no `Close` entry**.
- The inbound trace is enabled for this client: `SuiteRunner.cs` sets `p0Client.Loss.TargetRequestId = normalRequestId` before `SubmitAndWaitAsync` (`dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/SuiteRunner.cs:350-351`). `ReceiptLossChannel` records every decoded frame before returning it (`.../ReceiptLossChannel.cs:184-201`).
- Server-side `table.stdout.log` has one `close reserved SLOW_CONSUMER` at line 31 and four `close delivered code=LOCAL_CLOSED` lines (lines 36, 44, 49, 63). All delivered codes are non-empty. There is no `close delivered code=` with an empty value.
- The final2 run head was `aa7d81168ad175a4971514ac116cfff92e36434b`; it is **not** an ancestor of the candidate (different B1 worktree line). However, the client close/heartbeat sources relevant to this symptom are byte-identical between final2 and the candidate: `git diff --quiet aa7d811..eeaa90d -- dotnet/src/Mahjong.Client/{TableSession,HeartbeatTableChannel,WebSocketTableChannel}.cs` exits 0.

Why the absent `Close` matters: `TableSession.HandleInboundClose` is the **only** code path that sets `_lastError` to exactly `PROTOCOL_VIOLATION` from a protocol `Close` body (`TableSession.cs:1977-1984`). If an empty-code `Close` had arrived, the trace would contain `Close:` and `HandleInboundClose` would be the sink. It does not. Therefore final2's exact `PROTOCOL_VIOLATION` is **not** evidence of an empty `Close`.

### 2.1b rework12 and smoke — same tuple, same absence of `Close`

Two additional preserved runs reproduce the exact tuple:

- `artifacts/b1-rework12-3ea394a0cb7940a6bf2b2c7f621e1c56/logs/suite.json` (head `57d66ab1...`):
  `state=Faulted, lastError=PROTOCOL_VIOLATION, inbound=Heartbeat`; no `Close` in the trace. Its `table.stdout.log` has **4/4** delivered codes = `LOCAL_CLOSED` and **no** `close reserved SLOW_CONSUMER` at all. So the symptom does not depend on the final2 SLOW_CONSUMER reservation.
- `artifacts/b1-smoke/suite2.log`:
  `state=Faulted, lastError=PROTOCOL_VIOLATION` (the log line is truncated before `inbound`; `suite.json` in that directory is a later receipt-scope-blocked run).

All three preserved occurrences therefore share the code `PROTOCOL_VIOLATION`, and the two with full traces end at `Heartbeat` with no `Close`. This is a recurring pattern, not a one-off final2 artifact. It is still not a proof of the exact heartbeat sub-branch, because none of the preserved runs records Ping-vs-Pong or the nonce.

### 2.2 final9 — the delivery doc's claim is contradicted by its own evidence

`artifacts/b1-final9-logs/suite.json`:

```text
BlockerCode   = CLIENT_RECEIPT_SCOPE_MISSING
BlockerDetail = ... inbound=PlayerUpdate,Receipt,PlayerUpdate,PlayerUpdate,Heartbeat.
```

Key facts:

- There is **no `LastError` property** in `suite.json`; `suite-summary.json` also has no `lastError` field.
- The client is not reported Faulted; the blocker is the unresolved APPLIED receipt (`ClientPendingAfterServerReceipt=1`).
- `table.stdout.log` has 9 `close reserved`, 17 `close sending`, and 11 `close delivered` lines: 7 × `LOCAL_CLOSED`, 4 × `INVALID_TOKEN`; none empty.
- The delivery doc (`doc/v5-重构/V5-P5.4b-B1-HTTPS-ADMISSION-DELIVERY-20261029.md:155` and `:191`) says final9 had `TableSession.LastError=PROTOCOL_VIOLATION` and "inbound trace contains Receipt then still Faulted". The preserved final9 JSON does **not** support either statement. The more accurate companion report already says the opposite (`V5-REMOTE-RECEIPT-TICKET-DELIVERY-20261029.md:179-184`; `CODEX-V5-REMOTE-RECEIPT-TICKET-ACCEPTANCE-20261029.md:18`).

This is a documentation/evidence defect, not a production close-frame observation. It should be corrected before any caller relies on B1-MAJOR-01.

### 2.3 Candidate-preserved digest evidence

`doc/v5-重构/evidence/V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029/` contains a patched and an unpatched suite:

- `unpatched/suite.json`: digest-assertion failure; no `lastError`, no `Close`.
- `patched/suite-summary.json`: reaches `negative_matrix_complete`, full negative matrix, no `lastError`, no `Close` claim.

Neither file contains an empty `Close.Code` or a client fault caused by one. They are relevant only as proof that the later B1 receipt suite did not reproduce the close symptom.

### 2.4 Evidence contradiction table

| Claim | Source | Preserved reality |
|---|---|---|
| final9 `LastError=PROTOCOL_VIOLATION` | B1 delivery `:155`, `:191` | final9 `suite.json`/`suite-summary.json` have no `LastError`; blocker is receipt scope |
| final9 inbound contains `Receipt` then Faulted | B1 delivery `:191` | inbound contains `Receipt` but the client is not reported Faulted |
| all final9 server closes carry stable codes | remote delivery `:179-184` | true: 11/11 non-empty |
| empty Close not reproduced, UNRESOLVED/NOT_RUN | remote delivery `:46`; Codex acceptance `:18` | consistent with the preserved files |

---

## 3. Server close-frame trace in the frozen candidate

All paths below are in `dotnet/src/Mahjong.TableService/TableSessionHandler.cs` of `eeaa90d`.

### 3.1 The close decision type

```csharp
private readonly struct CloseDirective          // :165-179
{
    public CloseDirective(string code, bool permanent, int wsCloseCode) ...
    public string Code { get; }
    public bool Permanent { get; }
    public int WebSocketCloseCode { get; }
}
```

There are exactly **five** direct `new CloseDirective(...)` construction sites:

| Line | Code argument | Non-empty source |
|---:|---|---|
| 877 | `ProtocolErrorCode.LocalClosed` | client sends protocol `Close` |
| 1345 | `ProtocolErrorCode.LocalClosed` | post-admission client `Close` |
| 1937 | `ProtocolErrorCode.LocalClosed` | WebSocket close received |
| 2054 | `ProtocolErrorCode.LegacyV4Rejected` | V4 magic detected; WS status pinned to 4400 |
| 2740 | `code` parameter of `Directive(string)` | all callers pass stable constants |

`Directive(string)` (`:2738-2744`) only wraps one of the stable constants. `EnqueueClose` (`:2630-2655`) stores the directive and reserves the close slot. All other close paths go through `Directive(...)` with non-empty constants: stream termination (`:788`, mapping `:791-805`), displacement (`:812`, `:1144`), heartbeat timeout (`:825`), drain/shutdown (`:844`), handshake timeout (`:866`, `:1917`), protocol violation (`:885`, `:1946`, `:2045`, `:2099`), major/ruleset/projection mismatch (`:893`, `:898`, `:903`), invalid token (`:922`, `:988`, `:1101`), server busy (`:939`), lease lost (`:950`, `:979`), internal error (`:1067`), slow consumer (`:1122`, `:1394`, `:2601`, `:2611`), message too large (`:1954`, `:1959`, `:2072`), malformed (`:2068`).

### 3.2 Wire envelope

`SendCloseAndCloseAsync` (`:2678-2732`) builds the protocol `Close` body from the directive:

```csharp
var close = new Close
{
    Code = directive.Code,                    // :2699
    Permanent = directive.Permanent || ProtocolErrorCode.IsPermanentClose(directive.Code),
    WebsocketCloseCode = (uint)directive.WebSocketCloseCode
};
Envelope envelope = BuildEnvelope(MessageType.Close, close, correlationId: null); // :2703
...
await _ws.CloseOutputAsync(
    (WebSocketCloseStatus)directive.WebSocketCloseCode,
    directive.Code,                           // :2715
    grace.Token);
```

`BuildEnvelope` assigns the `Close` body unchanged (`:2416`). The envelope is sent only when `_identity != null` (`:2695`). If the failure occurs before admission identity exists, **no protocol `Close` envelope is sent at all**; only the transport close is attempted.

`CompleteTimeoutCloseAsync` (`:1976-2027`) also sends only a **transport** close (`:1987-1990`) with the mapped status and `code` as the reason; it does not enqueue a protocol `Close` body. This is the handshake-timeout path.

### 3.3 Close-code mapping

`ProtocolErrorCode.ToWebSocketCloseCode` (`ProtocolErrorCode.cs:234-271`) maps every one of the 13 stable close codes to a non-empty WebSocket status (1000/1009/4400/4401/4403/4409/4429/4503; unknown → 1011). The probe enumerated `ProtocolErrorCode.AllCloseCodes` and found **no** mapping to 0, 1005, or 1006.

### 3.4 Conclusion for the server

Within the frozen candidate, there is **no production code path that constructs an empty `Close.Code` for an admitted session**, and no path that maps a stable close code to an empty WebSocket status. The documented source location (`TableSessionHandler.SendCloseAndCloseAsync/Directive`) is therefore not supported as an empty-code producer by static tracing.

This does **not** prove that no historical or differently-built server ever did so. It proves the frozen candidate's current paths are non-empty.

---

## 4. Client close and lifecycle trace

All paths below are in `dotnet/src/Mahjong.Client/` of `eeaa90d`.

### 4.1 Protocol `Close` dispatch

`TableSession.ApplyFrameAsyncCore` dispatches `Envelope.BodyOneofCase.Close` to `HandleInboundClose` (`TableSession.cs:306-307`). `HandleInboundClose` (`:1977-1996`) does:

```csharp
_lastError = string.IsNullOrEmpty(close.Code)
    ? ProtocolErrorCode.ProtocolViolation          // :1984
    : close.Code;
...
_connection.Fire(close.Permanent ? PermanentFailure : TransientFailure);
```

Thus an empty `Close.Code` is a **deterministic fail-closed fault**, not a generic close. It is also why an empty code is indistinguishable, at the `LastError` level, from a heartbeat fault (both produce exactly `PROTOCOL_VIOLATION`).

### 4.2 Transport failure path

`HandleTransportFailure` copies the channel exception code verbatim (`:2019-2021`):

```csharp
_lastError = exception.Code;
```

The B1 worker's real WSS channel maps a WebSocket close to `WS_CLOSE_<n>` (`dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/RealWssTableChannel.cs:130-136`), not to `PROTOCOL_VIOLATION`. The production `WebSocketTableChannel` maps status 4400 to `PROTOCOL_VIOLATION` (`WebSocketTableChannel.cs:677-678`) and unknown statuses to `TransportLost` (`:691-692`). The B1 final2 worker used `RealWssTableChannel`, so a server WebSocket close would have produced `WS_CLOSE_...`, not `PROTOCOL_VIOLATION`.

### 4.3 Heartbeat path — the evidence-consistent alternative

`HeartbeatTableChannel` is a control decorator in front of the transport. It raises `TableChannelClosedException` with `ProtocolErrorCode.ProtocolViolation` in three places:

| Line | Condition | Effect |
|---:|---|---|
| 373-378 | heartbeat arrives before `WELCOME` (`!session.Bound`) | permanent fault |
| 381-383 | heartbeat session/room/seat/binding epoch mismatch | permanent fault |
| 412-414 | `PONG` nonce is not an outstanding client `PING` (`OnPong` false) | permanent fault |

`HeartbeatState.OnPong` (`:786-803`) removes the nonce from `_outstanding`; if the nonce is absent it returns `false`. `Fail` stores the terminal exception and closes the inner channel (`:591-629`). `TableSession.HandleTransportFailure` then copies `PROTOCOL_VIOLATION` into `LastError` (`TableSession.cs:2021`).

This path produces **exactly** the observed final2 tuple (`state=Faulted`, `lastError=PROTOCOL_VIOLATION`) **without any `Close` frame**. It is also the only exact-code path that remains after excluding a `Close` body and an `Error` body (the final2 trace has neither).

### 4.4 Plausible final2 lifecycle (evidence-consistent, not fully proven)

```text
p0 ready; heartbeat decorator active
  -> inbound Heartbeat (PING or PONG) fails binding/PONG validation
  -> HeartbeatTableChannel.Fail(PROTOCOL_VIOLATION, permanent)
  -> TableSession.HandleTransportFailure: LastError = PROTOCOL_VIOLATION
  -> TableSession Faulted; pending receipt lookup cannot complete
  -> heartbeat decorator closes the inner channel
  -> RealWssTableChannel.CloseAsync sends NormalClosure(1000), reason "b1-test-close"
  -> server logs LOCAL_CLOSED (matches final2 table lines 34-36)
  -> no protocol Close ever appears in the client inbound trace
```

The exact heartbeat sub-branch (unbound heartbeat vs binding mismatch vs unmatched PONG) is **not proven**: the preserved trace records only `Heartbeat`, not `Ping`/`Pong` or the nonce, and the server logs do not carry the session id on those lines. The lifecycle above is therefore a **code-path-based inference**, while the absence of `Close` in the final2 trace is an **observed** fact.

---

## 5. Focused isolated probes

Probe root (ignored, not committed):
`build/pr20-close-probe/`. It references the extracted candidate projects and uses only temporary outbox directories under `%TEMP%`; it does not open a socket, PostgreSQL, or TLS.

### 5.1 Build and run

```powershell
D:/Unity/dotnet/dotnet.exe build build/pr20-close-probe/Probe.csproj `
  -c Release --nologo -v:minimal -nr:false
# native exit 0, 0 warnings, 0 errors

D:/Unity/dotnet/dotnet.exe build/pr20-close-probe/bin/Release/net8.0/Pr20CloseProbe.dll
# native exit 0
```

Final probe output (`logs/probe-3.stdout.log`), **9/9 PASS, failures=0**:

```text
PASS wire_close_nonempty_roundtrip observed=ok=True,code=LOCAL_CLOSED
PASS wire_close_empty_roundtrip_is_accepted observed=ok=True,codeLength=0
PASS all_stable_close_codes_map_to_nonempty_ws_status observed=bad=<none>
PASS client_empty_close_code_becomes_protocol_violation observed=state=Faulted,lastError=PROTOCOL_VIOLATION
PASS client_nonempty_close_keeps_its_code observed=state=Faulted,lastError=LOCAL_CLOSED
PASS client_transport_protocol_violation_sets_lasterror_without_close observed=state=Faulted,lastError=PROTOCOL_VIOLATION
PASS heartbeat_pong_before_welcome_faults observed=code=PROTOCOL_VIOLATION,permanent=True
PASS heartbeat_bound_unmatched_pong_faults_without_close observed=code=PROTOCOL_VIOLATION,permanent=True
PASS heartbeat_bound_valid_ping_echoes_pong observed=sent=Heartbeat,passThroughBytes=71
failures=0
```

What each group proves:

1. **Wire is not the guard**: an empty `Close.Code` is a valid protobuf/round-trip value; the codec does not reject it. If a server ever sent one, the client would see it.
2. **Client sink is real**: an empty-code `Close` causes `Faulted` + exactly `PROTOCOL_VIOLATION`; a non-empty `LOCAL_CLOSED` is preserved.
3. **Same `LastError` without any Close**: an unmatched PONG after binding, and a PONG before binding, both raise `PROTOCOL_VIOLATION`; a transport fault with that code sets the same `LastError`. The valid PING positive control still receives a PONG, so the probe is not simply failing all heartbeats.
4. **No code-mapping hole**: all stable close codes map to non-empty WebSocket statuses.

### 5.2 Evidence assertions

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File build/pr20-close-probe/verify-evidence.ps1
# native exit 0; 25/25 PASS
```

It asserts, among other things: final2 and rework12 both have `lastError=PROTOCOL_VIOLATION` and `inbound=Heartbeat` with no `Close`; final2's 4/4 and rework12's 4/4 delivered server closes are non-empty; rework12 has no `SLOW_CONSUMER`; the smoke run has the same last error; final9 has no `LastError` and 11/11 non-empty delivered closes; the candidate has 5 `CloseDirective` constructors, copies `directive.Code`, has no empty literal, and has 3 heartbeat `ProtocolViolation` paths; final2→candidate client close/heartbeat sources are unchanged.

### 5.3 Preserved failed probe run

The first probe run (`logs/probe.stdout.log`, native exit **1**) failed its **positive control** because the fake channel had no post-PING pass-through frame, so `ReceiveAsync` blocked and hit the fake EOF. The product assertions were all already PASS. The control was fixed by appending a pass-through `Close` frame after the valid PING; the final run is `logs/probe-3.stdout.log` (exit 0). The failed log is retained; it is a harness defect, not a product result.

### 5.4 Existing candidate client tests

The candidate's own WSS test project was built and run on `eeaa90d` (no PG, no B1 runner, no machine trust change):

```powershell
dotnet test dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj `
  -c Release --nologo --filter "FullyQualifiedName~HeartbeatTableChannelTests"
# result: 80/80 passed, 0 failed, 0 skipped, native exit 0
```

The `--filter` was not honored by this xUnit v3/Microsoft.Testing.Platform project, so the count is the **whole WSS test assembly (80)**, not only the 17 `[Fact]` methods in `HeartbeatTableChannelTests.cs`. Those 17 already cover valid PING/PONG, heartbeat before WELCOME, different binding identity, unmatched PONG, invalid nonce, idle deadline, duplicate/stale WELCOME, and reopen. This means the heartbeat `PROTOCOL_VIOLATION` behavior is **intentional and unit-tested**, not an untested accident. The suite still does **not** cover an empty `Close.Code` at the `TableSession` level or the full `TableSession.LastError` mapping from a heartbeat exception.

---

## 6. Reproduction matrix

| Item | Result | Basis |
|---|---|---|
| Candidate source audit: server close directives | **PASS** | 5 constructors, all non-empty; `SendCloseAndCloseAsync` copies `directive.Code`; no empty literal |
| Candidate source audit: client empty-close sink | **PASS** | `TableSession.cs:1984`; synthetic probe |
| Candidate source audit: heartbeat `PROTOCOL_VIOLATION` paths | **PASS** | `HeartbeatTableChannel.cs:373-414`; synthetic probe |
| Wire round-trip of empty/non-empty `Close` | **PASS** | probe; empty is wire-valid |
| final2 / rework12 / smoke evidence parse | **PASS** | same `PROTOCOL_VIOLATION` tuple; traces end at `Heartbeat`; 4/4 + 4/4 delivered codes non-empty; rework12 has no `SLOW_CONSUMER` |
| Candidate `Mahjong.Client.Wss.Tests` | **PASS** | 80/80, 0 failed/skipped, native exit 0; includes 17 heartbeat facts |
| final9 evidence parse | **PASS** | blocker is receipt scope; no `LastError`; 11/11 delivered codes non-empty |
| Server-emitted empty `Close` on candidate | **NOT_REPRODUCED** | static trace + preserved logs; no observed empty frame |
| B1-MAJOR-01 as a confirmed production defect | **FAIL / UNRESOLVED** | causal claim unsupported; exact final2 sub-branch unproven |
| Original B1 suite (real Lobby HTTPS + WSS + PG + 5 clients) | **NOT_RUN** | no shared PostgreSQL; no TLS/trust changes; preserved evidence only |
| PG / TLS / machine-trust boundary | **NOT_RUN** | explicitly outside this probe |
| B1 batch gate | **FAIL/BLOCKED (existing)** | receipt scope/digest and LocalHost blockers remain; this report changes none |

**Do not read `NOT_REPRODUCED` as "fixed".** It means the preserved evidence does not contain the alleged server behavior on the frozen candidate, and the client symptom has a second, evidence-consistent producer.

---

## 7. Minimal next action

1. **Capture, do not change policy first.** Re-run the historical B1 5-client scenario (50 ms and 250 ms tick) with test-harness-only diagnostics that record, per inbound frame: envelope type; for `Heartbeat`, `Ping` vs `Pong` and a **hashed** nonce; for `Close`, `Code`, `Permanent`, `WebsocketCloseCode`; plus `TableSession.State`, `LastError`, and the `HeartbeatSnapshot` (bound/pending nonce/outstanding count). Do not log tickets, tokens, private hands, or raw nonces. This distinguishes empty `Close` from heartbeat mismatch without changing production behavior.
2. **Keep fail-closed behavior.** Do not default an empty `Close.Code` to a non-empty code, do not swallow `Close`, and do not turn `PROTOCOL_VIOLATION` into a success. If a product decision is desired for unmatched PONG (e.g. transient reconnect instead of permanent fault), it is a separate transport/liveness policy change requiring owner/security review and dedicated tests; it is not a reason to relax close validation.
3. **Close the remaining test gap** (not a production change): the 17 existing `HeartbeatTableChannelTests` already cover valid PING/PONG, pre-WELCOME heartbeat, binding mismatch, unmatched PONG, invalid nonce, idle timeout, duplicate/stale WELCOME, and reopen (80/80 WSS tests pass). Add only the missing cases: (a) empty-code `Close` → `Faulted`/`PROTOCOL_VIOLATION`; (b) non-empty `Close` preserved; (c) a full `TableSession` test proving a heartbeat exception becomes the exact `LastError`. Do not duplicate the existing heartbeat unit cases.
4. **Correct the B1 documentation before gate use.** Remove the final9 `LastError=PROTOCOL_VIOLATION` claim (`V5-P5.4b-B1-HTTPS-ADMISSION-DELIVERY-20261029.md:155,191`); cite final2/rework12/smoke as the preserved exact-code runs and state that their traces end at `Heartbeat` with no `Close`. Reclassify B1-MAJOR-01 as "unresolved / cause unestablished", not "empty Close confirmed".
5. **Keep B1-MAJOR-01 open** until a preserved run contains either a `Close` frame with `Code=""` (and its producer) or a heartbeat diagnostic proving the exact branch. Do not close PR20 or the B1 gate on this report.

---

## 8. Boundaries and non-claims

- No production code, tests, verifier, solution, Unity Resources/Burst, golden/pins, machine trust, or other worktree was modified.
- The candidate was inspected only through `git archive`; it was not merged, checked out, or committed. The report commit is based on `8fd7c6c`.
- The probe used a fake channel/outbox only; it did **not** exercise real WSS, TLS, PostgreSQL, Lobby signing, Android/iOS, or cross-physical-network behavior.
- The original B1 suite and its native exit are **not** claimed here; only preserved B1 artifacts were parsed. The candidate WSS unit suite was run separately (80/80) and is not the B1 gate.
- This report does not declare B1-MAJOR-01 fixed, does not approve G2–G5, and does not change the V4 production baseline.
- The final2 heartbeat explanation is **inference by code-path elimination**, not a frame-level proof of the exact heartbeat sub-branch.

---

## Appendix A — exact evidence paths and SHA-256

Preserved B1 evidence (read-only):

```text
5df3a2962b06060174ffd5f6067cfda4205eba4ba5b42349ffa9d117853707a1  b1-final2-logs/suite.json
8dea80cfd96fd0beb21b3cd284213c060ab435a9c11c8ba08aadbdb3411d9476  b1-final2-logs/table.stdout.log
6c16dc82512bec6f71cd289138cbe8783544f04dea484a94b3acaca6bc9ebb07  b1-final2-logs/suite.log
bab30d2be75cdfef5743082dad876762aa68eaf23d63047db19e0958eebc4f27  b1-final2-logs/runner.log
f19564c7042b06bb24e7efc1b4b57692cc39e85f8f12c3bb78a3cb6bd6b3946e  b1-final9-logs/suite.json
c060763ca1f40c4d7606a821b74742f99e6b074c9359462f29143a24d834e05b  b1-final9-logs/suite-summary.json
d621b10716098b221cecf20bfaaad66d00dee96cf50280cd8afe43bc43d0ae0d  b1-final9-logs/table.stdout.log
8ab6da85a5f7509f4de69dacf95f2a9638bd3f500cc9bbcdd1eca19f3266ef34  b1-final9-logs/runner.log
68d15225c1994b424c33834cd870417072229085a4cb763cd93460642144cbd8  b1-rework12-.../logs/suite.json
af02119b3e2a45a083cc212d227da712f242f18cb62a21f382fe1318f8783c2b  b1-rework12-.../logs/table.stdout.log
187fb9a2ef5e1cc6e3caf9d51323d884d788cda08744edc19dbe0ba2842a14c4  b1-rework12-.../logs/runner.log
c9dc4c0acca1f5b92f016613a8ec383696f2f7d56ba095ee1e102239b6fa4347  b1-smoke/suite2.log
47adb3ecc591b760111e3e22a12b3319f1b00eb1dcef0dd3d8b46aa80921f1cc  b1-smoke/suite.json
```

Candidate documents:

```text
971c29dd17dec94e2a817c9be71ecf51ba9c47c7759f78d7dde150df3a7bb10e  V5-P5.4b-B1-HTTPS-ADMISSION-DELIVERY-20261029.md
54070cb55668dfbae2ba24151c92576ad7806a79a8233363c50ed8e06bd48890  V5-REMOTE-RECEIPT-TICKET-DELIVERY-20261029.md
17077df071159444b89020d4ac98c0d60d6f78fdaef7937c5d8888ee43d3d1d3  CODEX-V5-REMOTE-RECEIPT-TICKET-ACCEPTANCE-20261029.md
47ad2f5a066bacdd6f032b043956e30bbb34570b6edd74b6f31afb07b3a62084  evidence/.../patched/suite-summary.json
103aebeb4c5406afa3dc833a31ec34877ec32a7c33452bfefce3b284622351e9  evidence/.../unpatched/suite.json
```

Probe logs (ignored):

```text
bd64f7dac24221b55a021a42d0c346e6a989c447669e775cb5c4ba30a135a269  probe-3.stdout.log (9/9 PASS, exit 0)
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  probe-3.stderr.log (0 bytes)
803e5b031d6eff3082c5ba30e5eb2665651cf1388cfd2fa461b7568cbaffd758  probe.stdout.log (first run, positive-control FAIL, exit 1)
9ecdec775d4d5b82a2806274652b977ebcf3da0c9666e46e4a6c083feba9608a  evidence.stdout.log (first evidence run, 18/18 PASS, exit 0)
1e7421e0c589ac9aed1054aa47dc3775b6a18103a6061ac57b0560359ece2865  evidence-2.stdout.log (25/25 PASS, exit 0)
041b7ea7c2d6838973bb0cfbf9eb22d1fd0b4ad05784a2dbb2f31287c00c8be6  heartbeat-tests.stdout.log (80/80 PASS, exit 0)
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  heartbeat-tests.stderr.log (0 bytes)
```

## Appendix B — candidate line references used

```text
TableSessionHandler.cs:165-179   CloseDirective
TableSessionHandler.cs:877,1345,1937,2054,2740   direct CloseDirective constructors
TableSessionHandler.cs:1976-2027 CompleteTimeoutCloseAsync (transport close only)
TableSessionHandler.cs:2416      BuildEnvelope Close body
TableSessionHandler.cs:2630-2655 EnqueueClose
TableSessionHandler.cs:2678-2732 SendCloseAndCloseAsync (Code = directive.Code at :2699)
TableSessionHandler.cs:2738-2744 Directive(string)
TableSession.cs:306-307          Close dispatch
TableSession.cs:1977-1996        HandleInboundClose (empty -> ProtocolViolation at :1984)
TableSession.cs:2019-2034        HandleTransportFailure (code copy at :2021)
HeartbeatTableChannel.cs:373-414 ProtocolViolation paths
HeartbeatTableChannel.cs:786-803 OnPong outstanding-nonce check
WebSocketTableChannel.cs:474-478,666-693 transport close mapping
RealWssTableChannel.cs:130-136   B1 test channel maps close to WS_CLOSE_<n>
TableServiceOptions.cs:55        HeartbeatIntervalMs = 10_000
TableServiceOptions.cs:97        RoomTickIntervalMs = 250
```
