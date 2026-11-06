# V5 Durable Shared Table-Ticket Consumption — Implementable Design

Date: 2026-10-30 (Asia/Shanghai)

Status: **DESIGN ONLY — NOT IMPLEMENTED, NOT FIXED, NOT ACCEPTED**

Base: `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (`origin/v5`, accepted G1 baseline)

Frozen candidate: `c0dd5f895bf584d38b832102b3f1d81cf1c3e079` (unaccepted PR20 head)

V4 remains the only production architecture. **G2–G5 are not approved.** No gate is approved by this report.

This report follows the frozen candidate's accepted input
`doc/v5-重构/V5-TICKET-INSTANCE-DIAGNOSIS-20261029.md` (SHA-256
`3DD0602C32E6803E7F06EB556A11F02581C31DDA01C5423C5696FBEF9245080D`). It distinguishes
source facts from proposed choices, gives an implementable target design, exact file scopes and
tests, and records what was actually run.

The security gap is **not fixed** by this report. The candidate's in-process fence remains a
per-process fence; the candidate code is neither merged nor committed here.

**Revision 2 (2026-10-30).** This revision answers five review findings: (1) the SQL CHECK NULL
gap in `ck_admission_seat`; (2) retention cleanup deleting non-terminal live rows; (3) missing
persistent ordering for same-account handover; (4) WELCOME/DB/close authority windows; (5)
`request_id` and unknown-commit-timeout semantics. It adds targeted isolated-PostgreSQL probes
and distinguishes simplified probe DDL from the full recommended DDL. Only this report changed.

**Revision 3 (2026-10-30).** Revision 2 still had a freshness flaw: `admission_seq` was assigned
in TableService Reserve B, after Lobby consume A, so a delayed old B could receive a larger local
sequence than a newer ticket. This revision binds freshness to Lobby issuance/consumption
linearization with a durable `ticket_generation`, adds a Lobby `ConfirmAdmission` step before the
actor authority switch, makes local `admission_seq` a subordinate control token, and specifies
token-conditional overlapping `HANDOVER_PENDING`. Round-3 probes reproduce the old A/B inversion
and show the corrected decision, concurrent interleavings, existing-LIVE/lease/restart fencing,
transaction rollback, and a Node actor-pending state model.

**Revision 4 (2026-10-30).** Revision 3 still lost a committed or unknown Lobby D0 when a newer
pending replaced its token: B could fail and restore old even though Lobby had committed A. This
revision adds a durable `lobbyLive` floor and D0-attempt records independent of the actor pending
token, conditional recovery/compensation that never overwrites a newer live generation, explicit
D0 unknown/timeout reconciliation and late-response rules, and restart recovery from Lobby live.
The extended Node model covers the reported interleaving, A/B D0 outcomes, compensation
concurrency, unknown/late D0, and restart. This is a model probe only; no production code changed.

**Revision 5 (2026-10-30).** Revision 4 still allowed old restoration when A's D0 had been sent but
its response was not yet delivered: the actor saw `NOT_SENT`/`PENDING` and B's failure restored
old even though Lobby had committed A. This revision adds a durable pre-send
`PENDING_SEND/SENDING/IN_FLIGHT` outbox, treats every possibly-committed-but-unanswered request
(including queued responses and restart) as unresolved, and forbids old restoration until a
definitive `REJECTED`/proved-`NOT_SENT` result with no other unresolved/committed attempt. The
extended Node model splits send/Lobby-commit/response and reproduces the caller's failing
interleaving. No production code changed.

**Revision 6 (2026-10-30).** Revision 5 still allowed `resolveNoCommit` to treat an `IN_FLIGHT`
attempt as locally rejected and restore old; a delayed original request could then commit at
Lobby. This revision adds a Lobby-side atomic **Cancel tombstone** keyed by `request_id`, sharing
the Confirm assignment lock, with idempotent `COMMITTED|CANCELLED|REJECTED` results and
retention/retry rules. Absence is not a terminal rejection; only a durable cancel/confirm result
can restore/deny old. It also fixes the outbox CHECK to include persistent `NOT_SENT` (and
`CANCELLED`), verifies those states in PostgreSQL, and extends the Node model to split send,
query/cancel, and late delivery. No production code changed.

**Revision 7 (2026-10-30).** Revision 6 allowed tombstones to be cleaned after retention while a
delayed Confirm could still arrive and be re-committed because the request row was absent. This
revision adds an authoritative, caller-unextendable `d0_deadline` and `cancelled_at` on the
consumption record, checked by Confirm under the same lock/transaction as Cancel/cleanup using DB
time. A late Confirm after tombstone deletion is `CANCELLED`/`DEADLINE_EXPIRED`; a missing
authoritative record is `AUTHORITATIVE_RECORD_MISSING` and never commits. It also extends the
model/real-PG probes for cleanup, boundary, restart, and missing-record cases. No production code
changed.

**Revision 8 (2026-10-30).** Revision 7 checked `d0_deadline` before an existing terminal
`COMMITTED` result, so a response-lost/restarted retry after the deadline returned
`DEADLINE_EXPIRED` instead of the original committed result; Cancel after commit similarly
diverged from the Node model. This revision gives an exact binding-matched terminal result
(`COMMITTED|CANCELLED|REJECTED`) priority over the deadline, applies the deadline/cancel fence only
when no prior terminal result exists, fixes the `d0_deadline > created_at` DDL conflict for late
terminal rows, and adds Node/PostgreSQL probes for retry, boundary, cleanup, restart, missing
evidence, and binding conflicts. No production code changed.

**Revision 9 (2026-10-30).** Revision 8 allowed the result row to be cleaned while the consumption row remained without a terminal marker, so a committed request could be misread as a new DEADLINE_EXPIRED request and written as REJECTED. This revision defines a full-binding terminal marker on the consumption row, an atomic request/result/consumption cleanup invariant, and RESULT_EXPIRED/TERMINAL_EVIDENCE_INCONSISTENT fail-closed outcomes. It adds Node and PostgreSQL probes for request/result cleanup, consumption cleanup, restart, concurrent cleanup/Confirm, binding conflict, and inconsistent evidence. No production code changed.

**Revision 10 (2026-10-30).** Revision 9 left the generic Confirm/Cancel steps returning a
result/request terminal before checking the consumption marker, so a cleaned committed request with
a marker could still reach `DEADLINE_EXPIRED` in the generic path. This revision makes one
evidence-collection/consistency/marker/terminal/deadline order authoritative for Confirm, Cancel,
and lookup, removes the duplicate conflicting steps, defines cleanup when the result is already
absent, and adds a Node order-model plus PostgreSQL marker/conflict/cleanup probes. No production
code changed.

**Revision 11 (2026-10-30).** The probe9 SQL returned a result/marker replay before reading the request row, so a request/result/marker state or binding conflict was missed. This revision adds a strict probe11 schema that reads all three witnesses under the same locks, validates full binding and original result fields, and asserts every expected outcome with SQL exceptions; it also adds a three-witness Node model and concurrent retry probe. No production code changed.

**Revision 12 (2026-10-30).** The probe11 `probe_evidence` compared only `admission_id` and `ticket_generation`, ignoring `jti` and the rest of the required binding, so a result with `jti=jtiB` and request/consumption `jti=jtiA` could still replay. This revision adds a probe12 schema with every binding field (`jti`, account, room, assignment, session, owner_epoch), asserts each row's binding conflict through Confirm/Cancel/Lookup, adds generation conflicts, and records the exact SQL failures. P51 is retained only as the earlier, narrower probe; P53 supersedes it for full binding. No production code changed.

**Revision 13 (2026-10-30).** The probe12 binding comparisons used SQL `<>`; a NULL argument made the predicate UNKNOWN, so `probe_confirm(...,NULL,...)` returned `REPLAYED` instead of failing closed. This revision rejects every NULL required binding argument with `INVALID_BINDING`, uses `IS DISTINCT FROM`, and adds 27 NULL-argument assertions (9 fields × Confirm/Cancel/Lookup), plus result-code/live-id/generation checks. No production code changed.

**Revision 14 (2026-10-30).** The acceptance summary cited P51 C5/C6 for result_code/live-id/generation even though P51 C5 tests only result_code and C6 only live id; P53 C10/C17 are the generation evidence. It also made binding conflicts TERMINAL_EVIDENCE_INCONSISTENT in decision 42, contradicting decision 43 and the probes. This revision corrects the evidence mapping and fixes the error precedence (INVALID_BINDING → REQUEST_ID_CONFLICT → TERMINAL_EVIDENCE_INCONSISTENT). No production code changed.

---

## 1. Frozen inputs and method

### 1.1 Commits

| Item | Value |
|---|---|
| Accepted base | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`, tree `61f50856aedd78e0f8609e282baa8313ae8f2a14` |
| Frozen candidate | `c0dd5f895bf584d38b832102b3f1d81cf1c3e079`, tree `802fa5fc543f3275532b39a8a69c668775f77fd4` |
| Candidate parent | `0420848a0cb3c820536f26f583be280bdee714ee` |
| Candidate subject | `docs(v5): diagnose PR20 empty-close evidence (unresolved)` |
| Candidate is descendant of base | `git merge-base --is-ancestor 8fd7c6c c0dd5f8` → native exit **0** |
| Commits in candidate not in base | **330** (`git rev-list --count 8fd7c6c..c0dd5f8`) |

### 1.2 Isolation

The candidate was inspected only through `git archive` into the ignored directory:

```text
build/ticket-pr20-c0dd5f8/
```

No candidate tree was checked out over this worktree, merged, or committed. The tracked worktree
was clean before and after the inspection. The archive and PostgreSQL cluster were removed after
the probes; retained evidence is listed in §8.

### 1.3 Method and limits

- Read `AGENTS.md`, `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md`, and
  `doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md` from the accepted base.
- Read the frozen candidate's ticket diagnosis and the Lobby/TableService ticket source at the
  exact candidate SHA.
- Ran isolated PostgreSQL 16.10 transactional probes against the candidate's **schema**, and
  against a throw-away proposed-schema probe. The probes did not compile or execute candidate
  C# code.
- Revision 2 reran targeted probes on the **full final proposed DDL** (seat NULL/range,
  terminal-only cleanup, long-lived row, cleanup/handover, `admission_seq` reverse-WELCOME,
  `request_id` uniqueness) in a second owned cluster. SQL predicates and constraints were tested;
  actor/command fencing and WSS behavior were not.
- Revision 3 ran a **simplified Lobby freshness model** in a third owned cluster (issuance,
  consumption, conditional confirm, existing live, owner-epoch lease, rollback, restart) and a
  Node **actor pending-token model**. The freshness model is not the candidate schema or the full
  admission DDL; it isolates the issuance/consume/confirm linearization under test.
- Revision 4 ran an extended Node **actor + D0/Lobby-live model** (`probe4-actor-d0-model.mjs`):
  committed/unknown/late D0, newer pending replacement/failure, conditional compensation,
  `lobbyLive` recovery, and restart. This is a state-machine model, not C#/RPC/DB.
- Revision 5 split **D0 send intent, Lobby commit, and response delivery** in
  `probe5-d0-inflight-model.mjs`, and reran the caller's `caller-inflight-probe.cjs` as the
  failing baseline. This is a state-machine model; outbox durability and C# mailbox/transport are
  not implemented.
- Revision 6 added a Node **Confirm/Cancel tombstone model** and reran the caller's
  `caller-query-race.cjs` as the failing baseline, plus a real PostgreSQL probe of the outbox
  `NOT_SENT` DDL, invalid-state rejection, restart persistence, and startup recovery. The Lobby
  Cancel SQL transaction and C# adapter remain unimplemented.
- Revision 7 added a Node **deadline/tombstone-cleanup model** and reran the caller's
  `caller-tombstone-cleanup.cjs`, plus a real PostgreSQL function probe of `d0_deadline`,
  `cancelled_at`, cleanup, boundary, and missing-record behavior. The production Lobby SQL/RPC and
  C# adapter remain unimplemented.
- Revision 8 added a Node **terminal-result/idempotency model** and reran the caller's
  `caller-review-deadline.mjs`, plus real PostgreSQL probes for committed-result replay before
  deadline, cancellation after commit, late `REJECTED` DDL, cleanup, binding conflict, and missing
  evidence. The production Lobby SQL/RPC and C# adapter remain unimplemented.
- No real Lobby HTTPS, WSS, two-process, two-instance, TLS/mTLS, or shared-production database
  run was performed. Those remain `NOT_RUN`/`BLOCKED` as stated in §9.

### 1.4 Source hashes at the frozen candidate

SHA-256 of the extracted candidate file; Git blob shown for reproducibility.

| File | Git blob | SHA-256 |
|---|---|---|
| `TableTicketConsumptionRegistry.cs` | `f4b74bf5df08370c568911c914a3d11c44148c7a` | `8395F0CEB3446C8E3E809BC6D162720DC9F7F325EEE14A217F030FEEF7415D4F` |
| `TableTicketValidator.cs` | `04e930bbd5b579231aeb80343ae92f3141663645` | `168338CDB2A8826E9304D3D85DDE3CD8A3D1D193DA690A9932E2E0A79C7B8AFC` |
| `TableServiceWebHost.cs` | `047f0d3ccebf4b11481ef283b68b584160363eca` | `6AB7C60A58E051E8BD995B2E1CEF22BB986A10C976BB97B55A81FDFFFB1168E4` |
| `TableSessionHandler.cs` | `a97655eda5107550de3032303211d579ab2609f8` | `37180DE6D542F7DFE2927400F258F6F3F264AA9B765C5B4DE848A0BE46C41F72` |
| `TableServiceOptions.cs` | `bd2e0c8a19bc160507fc4166b71fe3db2a9f77fa` | `2F6DE78D4114C72CFE173F75C924B33CD4AA328B141796C5562B38A3206C2AA9` |
| `LobbyApplication.Tickets.cs` | `2afa39487d0832928fb5d05675c3e39c74d85e55` | `7E6E71294019A729DA0ED4021878B3CCD274482CCC5761A348897FFCCFD409B2` |
| `PostgresLobbyStore.cs` | `8cc47e426a9e8ff476fcb90921452cdcd8c11e88` | `B1CCCCB29CA1CFB93F7C59D81BA1FEE91ABED4F57809270A505B8A191C6A0F31` |
| `001_lobby.sql` | `876f5e9e5293e6f064dd2992e829a36bf821b14d` | `205F16EF97671B1EFFA6A8615709538EB5504CECE58ED450AF1DEE4F38B975CC` |
| `001_table_store.sql` | `80e0571f6a8981e6ed8beb63ecbc7f5f6ea57edc` | `0A370F4FEA8FC836165FB2040F3E762148322C4E58DC5ECC898B3AE8B9928FB4` |
| `PostgresTableStore.Ports.cs` | `a12d971d36320966ba32a1c6658bec506b9bad11` | `C89C176BD5CBA378CEDBE050AED40159159929242D578ED52E4A5F8AA00FA0D7` |

The registry, validator, host, Lobby application, Lobby store and Lobby migration hashes above
match the frozen diagnosis's recorded inputs exactly; this confirms the diagnosis and this report
inspected the same ticket code.

---

## 2. Source facts (not proposals)

### 2.1 Issuance and supersession in Lobby

`dotnet/src/Mahjong.LobbyService/LobbyApplication.Tickets.cs`

- `IssueTableTicketAsync` is receipt-first (`:22-31`), locks the room and assignment in
  `ExecuteIdempotentAsync` (`:67-200`), requires an active assignment owned by the account
  (`:100-127`), rejects an account with another active assignment (`:134-143`), marks the previous
  active ticket `SupersededAt = now` (`:145-153`), creates a fresh `jti`, signs a frozen claim
  set, and inserts `lobby.table_ticket` (`:155-183`).
- The frozen ticket claim set has no `ticket_generation`/issuance sequence; `iat` is second-
  resolution and `jti` is random. The candidate therefore has no signed, durable total order
  between two tickets for the same assignment. `lobby.assignment` also has no
  `latest_ticket_generation`/`admission_generation`; only `table_ticket.superseded_at` records
  the newest issuance at that moment.
- Default ticket TTL is **60 s**, maximum **300 s**, clock skew **5 s**, reservation TTL **15 s**
  (`LobbyOptions.cs:26-32`). The Lobby spectator cap is currently a “temporary safety cap” of
  **32** (`LobbyOptions.cs:42-43`); it is not a product decision.
- `dotnet/src/Mahjong.LobbyService/Migrations/001_lobby.sql`:
  - `lobby.table_ticket` has `jti` primary key, `assignment_id`, `account_id`, `room_id`, role,
    seat, `issued_at`, `expires_at`, `kid`, `consumed_at`, `superseded_at` (`:159-177`).
  - `uq_table_ticket_active_assignment` is a partial unique index on `assignment_id` where
    `consumed_at IS NULL AND superseded_at IS NULL` (`:179-181`). At most one unconsumed,
    unsuperseded ticket exists per assignment.
  - `lobby.table_ticket_consumption` has `jti` primary key and an FK to `lobby.table_ticket`
    (`:190-199`). This is a durable exact-`jti` replay fence.
- `dotnet/src/Mahjong.LobbyService/LobbyHttp.cs:255-268` exposes only
  `POST /v1/table-tickets` (issuance). There is no public `/consume` route.
- `ConsumeTableTicketAsync` (`LobbyApplication.Tickets.cs:210-417`) is an application port, not a
  WSS path. In one store transaction it locks room → assignment → table ticket (`:248-273`) and
  later the match ticket (`:352-379`), rejects unknown/consumed/superseded/expired/tuple-
  mismatched tickets (`:274-302`), checks assignment status/expiry (`:304-322`), the one-active-
  assignment rule (`:324-333`), seat conflict (`:335-350`), match state (`:352-379`), then writes
  consumption and marks the assignment/table ticket consumed (`:381-404`).
- `PostgresLobbyStore.cs:302-383` provides the transaction wrapper and rollback;
  `:1108-1126` reads the ticket `FOR UPDATE`; `:1151-1204` inserts the ticket/consumption rows.

**Fact:** the durable Lobby ledger already satisfies exact-`jti` one-time, supersession,
assignment state and assignment-seat uniqueness **when it is called**. It is not called by the
TableService WSS admission path.

### 2.2 Read-only ticket verification in TableService

`dotnet/src/Mahjong.TableService/TableTicketValidator.cs`

- The validator is deliberately read-only: it never signs, never calls the Lobby runtime, and
  never holds a private key (`:257-265`).
- It enforces compact JWS shape, strict canonical base64url, header/payload claim allowlists,
  no unknown/duplicate claims, `alg=ES256`, `typ=JWT`, an active `kid`, ES256 signature,
  issuer/audience exact match, id charset, PLAYER seat 0..3, spectator without seat, frozen
  protocol/ruleset/projection versions, `exp > iat`, TTL cap, future `iat`/`nbf`, and expiry with
  skew (`:292-457`).
- It has **no stateful `jti` check**, no `superseded_at` check, no assignment status check, and no
  live seat/spectator knowledge.
- The route room is supplied by the server through `TableTicketExpectation` (`:399-406`); the
  role, seat and account come from the signed ticket.

### 2.3 In-process consumption and live admission in TableService

`dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs`

- `ITicketConsumptionRegistry` documents a two-phase admission: provisional `Consume`, final
  `CommitAdmission`, post-WELCOME `EvictOnWelcome`, `RollbackSeatHandover`, and `Release`
  (`:137-206`).
- The only implementation is `InMemoryTicketConsumptionRegistry` (`:208-209`). Its comment says
  “In-memory P5.1 implementation. Cross-instance uniqueness arrives with P5.2.” P5.2 exists, but
  no durable ticket registry was added.
- State is five per-instance dictionaries behind one `_gate` (`:211-230`):
  `_consumedJtis`, `_assignments`, `_seatBindings`, `_spectatorCounts`, `_sessions`.
- `Consume` (`:252-404`) purges expired records, rejects an exact retained `jti`, checks the
  assignment tuple, checks the live seat owner, checks spectator capacity, fails closed at
  `MaxRetainedConsumedTickets`, records the jti/assignment/session, and returns `Accepted`.
- `CommitAdmission` (`:406-471`) is the handover linearization point.
- `EvictOnWelcome` (`:485-565`) is the only displacement decision; it runs **after** WELCOME.
  The in-memory implementation assigns `_nextSeatSeq` at `Consume` and keeps a per-seat commit
  chain; `EvictOnWelcome` scans that chain for newer welcomed entries. That ordering is **not
  persisted** and is lost on restart/cross-instance; the durable design must bind ownership to
  Lobby `ticket_generation`/`admission_generation`, with local `admission_seq` only a subordinate
  control token.
- `RollbackSeatHandover` (`:574-618`) removes only this session's not-yet-welcomed commit.
- `Release` (`:620-695`) frees live seat/spectator resources but **does not remove the jti or
  assignment evidence**; the jti stays spent until expiry + skew.
- `PurgeExpired` (`:740-780`) removes retained records only after ticket expiry + skew.
- `TableServiceWebHost.cs:109-110` registers exactly one `InMemoryTicketConsumptionRegistry` as a
  singleton per built `WebApplication`; `:214-216` injects it into every session handler.
- `TableServiceOptions.cs:112` defaults `StoreMode` to `Memory`. `TABLESERVICE_STORE=postgres`
  changes the room host to `PostgresTableHostRegistry` (`TableServiceWebHost.cs:122-161`) but
  **not** the ticket registry.
- The default spectator cap is **32** (`TableServiceOptions.cs:82`), retained consumed-ticket cap
  is **65,536** (`:93`), lease TTL is **90 s** (`:105`).

### 2.4 Admission and teardown sequence in TableService

`dotnet/src/Mahjong.TableService/TableSessionHandler.cs`

1. Validate the HELLO ticket with the route room (`:917`).
2. Create a session id and acquire the room host (`:927-940`); a capacity/lease failure consumes
   nothing (`:941-951`).
3. Read the actor lifecycle and require `Serving` (`:958-980`).
4. Call `_consumption.Consume(ticket, sessionId, now)` (`:984`). Every refusal becomes close
   `4401` (`:985-989`).
5. Register the session, issue a server scope, subscribe the stream, then call
   `_consumption.CommitAdmission(sessionId)` (`:994-1102`).
6. Send WELCOME (`:1104-1127`), then `EvictOnWelcome` (`:1129-1161`). A `SelfSuperseded` welcome
   closes with 4403; `DisplacedOther` asks the session directory to displace the old session.
7. Any failure before WELCOME calls `RollbackSeatHandover` and `Release` (`:1179-1212`).
8. Teardown (`:488-573`) revokes an unused scope, unsubscribes, unregisters, checks
   `TryGetSeatOwner` before notifying a seat disconnect, and calls `Release`. The jti evidence is
   retained by `Release`.

### 2.5 Enforcement matrix at the frozen candidate

| Property | TableService WSS admission | Lobby durable consumer | Notes |
|---|---|---|---|
| Signature, claims, `aud`, role, seat, room, assignment, versions | Yes | Yes | Cryptographic validation in both |
| Ticket expiry / TTL cap / skew | Yes | Yes | 60 s default, 300 s max |
| Exact `jti` already consumed | **Per process only** | **Durable** (`pk_table_ticket_consumption`) | TableService never calls the Lobby consumer |
| `superseded_at` | **No** | **Yes** | Old unspent ticket remains usable at the table gate |
| Assignment active / not expired | **No** | **Yes** | Table gate only sees signed claims |
| Room open / terminal | Indirect via host/actor | **Yes** | Different consistency points |
| Seat occupied by another account | **Per process only** | Yes (assignment/seat checks) | Not shared across instances |
| Spectator capacity | **Per process only** | Lobby assignment flow (not consume) | Different notions: assignment slots vs live sockets |
| Consumption survives restart | **No** | **Yes** (PG) | Fresh process has an empty registry |
| Consumption shared across instances | **No** | **Yes** (PG PK + row locks) | Not wired to WSS |

### 2.6 Prior deterministic evidence (cited, not re-run here)

The frozen diagnosis records nine component scenarios against real candidate registry objects:

| ID | Scenario | Result |
|---|---|---|
| S1 | 32 concurrent same-`jti` calls in one registry | **PASS**: 1 accepted, 31 `DuplicateJti`, 0 other |
| S2 | Two independent registries, same `jti` | **FAIL**: both accepted and committed |
| S3 | Fresh registry after restart, same ticket | **FAIL**: accepted and committed |
| S4 | Two registries, same seat, different accounts | **FAIL**: both accepted |
| S5 | Two registries, spectator cap 1 | **FAIL**: 2 accepted |
| S6 | Release then exact replay; fresh jti same assignment | **PASS**: replay `DuplicateJti`, fresh jti accepted |
| S7 | Newer ticket commits, older unconsumed ticket arrives later | **FAIL**: older accepted and displaced newer |
| S8 | Same process, cross-account seat | **PASS**: `SeatOccupiedByOtherAccount` |
| S9 | Same process spectator cap | **PASS**: `SpectatorCapacityReached` |

These prove a correct per-process fence and a missing global fence. They do **not** prove real
two-instance WSS behavior; that was `NOT_RUN` in the diagnosis and remains so here.

---

## 3. Invariants the implementation must make true

These are proposed normative invariants, derived from the diagnosis and the V5 taskbook. They
are not claims that the candidate currently satisfies them.

- **I1 — Exact `jti` one-time.** A ticket `jti` can be consumed at most once across every
  TableService instance and every restart, until `exp + skew`. Release, rollback, displacement,
  and process death must not re-enable it.
- **I2 — Freshness/supersession.** A ticket whose Lobby row is `consumed_at IS NOT NULL` or
  `superseded_at IS NOT NULL`, whose assignment is no longer `RESERVED|CONFIRMED`, or whose room
  is terminal must be rejected even if its JWS `exp` is still in the future.
- **I3 — Player seat uniqueness.** At most one **live** player per `(room, seat)` across all
  instances. A different account can never take a live seat. A same-account re-ticket may hand
  over only after the new admission has delivered WELCOME; a failed new admission must leave the
  old owner working.
- **I4 — Spectator capacity.** The number of live spectator sockets per room is at most the
  configured cap across all instances. A failed/refused/disconnected admission must not leak a
  slot.
- **I5 — Fail closed.** Any uncertainty (Lobby timeout, DB failure, lost lease, unknown commit
  outcome) must refuse WELCOME. There is no fallback to the in-memory registry in a durable
  deployment.
- **I6 — Durability.** A committed consumption and a live admission survive process restart and
  instance failover; a committed success is never silently lost. A crash before commit may leave
  the ticket spent (fail closed) but must not leave a phantom live seat.
- **I7 — Release semantics.** Disconnect frees live seat/spectator capacity, but never un-spends
  the jti. Reconnect uses a fresh ticket for the same active assignment.
- **I8 — Authentication boundary.** The user is authenticated only by the Lobby-signed JWS. The
  internal consume call uses a separate service identity. TableService never trusts an
  unsigned request field for account/role/seat/room.
- **I9 — Data ownership.** Lobby owns `lobby.*`; TableService owns `table_store.*`. A service
  does not write the other's schema without an explicit, reviewed ownership change.
- **I10 — Auditability.** Stable refusal reasons, redacted logs, and a correlation id connect
  the Lobby consumption to the TableService admission without logging the compact ticket.
- **I11 — Durable freshness ordering.** Every ticket has a durable, signed
  `ticket_generation` assigned at issuance; every live admission has a Lobby
  `admission_generation` assigned at `ConfirmAdmission`. An older ticket can never commit or
  WELCOME over a newer ticket for the same seat/spectator identity, regardless of local
  `admission_seq` order, scheduling, or restart.
- **I12 — Control generation.** `owner_epoch`, `ticket_generation`,
  `lobby_admission_generation`, and local `admission_seq` are checked on every command and
  delivery. A `LIVE` row alone is not proof of exclusive control; the old connection's queued
  commands are rejected by generation, not by a best-effort close.

---

## 4. Options and recommendation

| Option | Shape | One-time global | Freshness | Seat/spectator global | Contract impact |
|---|---|---|---|---|---|
| **O1** | Pin each room to one process; no restart within TTL | No | No | No | Not a fix; must be an explicit scope exclusion |
| **O2** | TableService-owned durable `jti` ledger in `table_store` | Yes | Partial (only after the newer ticket was seen) | Yes | No Lobby call; smallest change; old-first supersession gap remains |
| **O3** | Lobby internal consume + ConfirmAdmission (issuance generation/live lease) + TableService durable live ledger | Yes | Yes | Yes | Adds synchronous consume/confirm dependency on Lobby and a Lobby live lease; changes the P5.3 “TableService does not synchronously call Lobby runtime” boundary |
| **O4** | Shared-PostgreSQL `SECURITY DEFINER` consume function + TableService local admission in one transaction | Yes | Yes | Yes | Strongest atomicity; requires same database/cluster, cross-schema function, privilege and ownership change |
| **O5** | Lobby owns live admission too (consume/release/heartbeat) | Yes | Yes | Yes | Largest coupling; Lobby must track live sockets and lease expiry |

**Recommendation: O3.** It reuses the already-durable, already-transactional Lobby ledger for
I1/I2, binds freshness to Lobby issuance/`ConfirmAdmission`, keeps live seat/spectator ownership
in `table_store` where the room lease and actor already live, and avoids cross-schema writes. Its
cost is two explicit product/architecture decisions: Lobby is a synchronous dependency for
consume/confirm, and Lobby holds a live admission lease. If availability must be fully decoupled,
O2 is the only smaller option, but it does **not** close old-ticket-after-new-issuance; that must
then be written down as an accepted limitation. If single-transaction atomicity across both
authorities is mandatory, choose O4 and budget for the schema/privilege change.

This report does not choose O3 as an accepted product decision. It is the recommended
implementable design; §10 lists the decisions the caller must make.

---

## 5. Recommended design (O3)

### 5.1 Component responsibilities

| Component | Owns | Does not own |
|---|---|---|
| LobbyService | Ticket issuance/JWKS, `lobby.table_ticket`, durable `ticket_generation` and assignment freshness, `lobby.table_ticket_consumption` + attempt state, **`ConfirmAdmission`**, assignment/match state, supersession, one-time consume transaction | Live sockets, seat occupancy after admission, table snapshots |
| TableService | JWS validation, room host/lease, live seat/spectator capacity, local `admission_seq` control token, actor authority generation, stream/session, WELCOME/eviction, `table_store.table_ticket_admission` | Ticket freshness ordering, ticket issuance, signing keys, Lobby assignment lifecycle |
| PostgreSQL | Two schemas: `lobby.*` and `table_store.*`; separate runtime roles; durable ticket/admission generations and terminal-only cleanup | Cross-schema writes in O3 |

### 5.2 Happy-path admission sequence

All steps are fail-closed. “Commit” means the durable transaction returned success.

1. **HELLO + JWS validation (read-only).** The TableService validates the ticket against the
   route room; no state changes.
2. **Room host/lease.** Acquire the room host and verify the actor is `Serving`. A capacity,
   lease or actor failure consumes nothing.
3. **Lobby consume (durable transaction A).** TableService calls the internal Lobby consume
   endpoint with the compact ticket, route room, `request_id`, `table_instance_id`, and
   `session_id`. Lobby:
   - validates the JWS again (defense in depth) and derives account/role/seat/assignment from
     the signed claims;
   - locks room → assignment → table ticket → match in that order;
   - checks `consumed_at IS NULL`, `superseded_at IS NULL`, `expires_at > DB now`, the persisted
     record equals the claims, the assignment is `RESERVED|CONFIRMED` and unexpired, the account
     has no other active assignment, the player seat is not held by another active assignment,
     and the match ticket is active/confirmable;
   - inserts `lobby.table_ticket_consumption(jti)` (PK), marks `table_ticket.consumed_at`, updates
     the assignment/match in the **same transaction**;
   - returns the consumption result, `consumed_at`, `ticket_generation`, and
     `lobby_consumption_id`.
   An error rolls back all of it: the jti is not spent. A timeout is **not** “no side effect”:
   the transaction may have committed (see §5.8); the same `request_id` must be used to resolve
   the unknown result.
4. **TableService durable admission reserve (transaction B).** In one `table_store` transaction:
   - lock the room's `table_store.table_lease` row `FOR UPDATE`; require
     `owner_epoch = host.OwnerEpoch` and an unexpired lease;
   - insert `table_store.table_ticket_admission` with `state='RESERVED'`, unique `jti`, unique
     `request_id`, `lobby_consumed_at`, `ticket_generation`, `lobby_consumption_id`,
     `session_id`, `owner_epoch`, `admission_seq` (a **local control token**, not freshness),
     `retain_until`, `reservation_expires_at`;
   - for a player, reject if another account has a `RESERVED|COMMITTED|LIVE` row on that seat,
     or if an active row for the same identity has a higher `ticket_generation` (fast path only;
     Lobby `ConfirmAdmission` is the final freshness authority); for a spectator, count
     `RESERVED|COMMITTED|LIVE` spectators for the room and enforce the cap under the same lock;
   - commit.
   If this fails, the ticket is already spent in Lobby; do not WELCOME. This is the accepted
   fail-closed cost of O3 (see §10 item 4). `Reserve` is idempotent only for the exact same
   `jti` + `session_id` + `request_id`; it returns the existing row’s state. A different session
   with the same `jti` or a different `jti` with the same `request_id` is a conflict.
   `admission_seq` is assigned here for actor/stream/idempotency ordering; it does **not** order
   ticket freshness. Two delayed admissions can receive it out of ticket order.
5. **Bind session/scope/stream**, then `CommitAdmission` (transaction C): transition the row
   `RESERVED → COMMITTED`; verify the lease epoch and that the stored `ticket_generation` still
   matches the consumption record. Do not use local `admission_seq` as a freshness comparison.
6. **WELCOME** on the wire. WELCOME is **not** the authority grant (§5.5A); it carries
   `admission_id`/`admission_seq`/`ticket_generation`/`owner_epoch` and the connection remains
   command-pending until Lobby Confirm and local D.
7. **Lobby `ConfirmAdmission` (freshness linearization, transaction D0).** Before sending,
   TableService persists a durable D0 outbox/attempt row (`PENDING_SEND`/`SENDING`) keyed by
   `(admission_id, request_id)`; only then does it call Lobby with `jti`, `ticket_generation`,
   `lobby_consumption_id`, `admission_id`, `session_id`, `owner_epoch`, and `request_id`. The
   response may be delayed/queued; `SENDING`/`IN_FLIGHT` is unresolved and blocks old authority.
   Lobby locks the assignment and its live row and:
   - verifies the consumption belongs to this `jti`/session and the ticket is not superseded;
   - requires `ticket_generation == assignment.latest_ticket_generation`; an older ticket is
     `STALE_GENERATION` even if its local `admission_seq` is larger;
   - rejects if an existing live admission has a newer `ticket_generation` or a newer
     `admission_generation`;
   - rejects a different `owner_epoch` while the existing live lease is unexpired (`LEASE_HELD`);
   - increments `assignment.admission_generation`, upserts the live row
     (`live_admission_id`, `live_ticket_generation`, `table_owner_epoch`, `session_id`,
     `lease_expires_at`), and returns `lobby_admission_generation`.
   A `STALE_GENERATION`/`NEWER_LIVE`/`LEASE_HELD` result does **not** automatically restore old.
   The actor records the result/current `lobbyLive` and calls `recoverToLobbyLive()`; it restores
   old only if Lobby still reports old as live and no D0 is
   `PENDING_SEND|SENDING|IN_FLIGHT|COMMITTED|UNKNOWN`. Otherwise it reconciles to the durable live or enters
   `RECONCILING` with no command authority. A newer ticket issued but not yet consumed also makes
   the old ticket stale (fail-closed; see §10).
8. **Local `EvictOnWelcome`/authority switch (transaction D).** Conditional on the returned
   `lobby_admission_generation` and `ticket_generation`: if the local row is no longer the newest
   confirmed Lobby generation, mark it `SUPERSEDED` and close; otherwise demote the previous
   `LIVE` row first, promote this row to `LIVE`, and switch the actor `SeatAuthority` to this
   `(ticket_generation, lobby_admission_generation, admission_seq)` tuple. Local `admission_seq`
   is only the final tie-breaker/stream token; it can never resurrect a stale ticket.
9. **Failure before/at D0 or D.** If WELCOME fails before D0, or D0 is definitively rejected/
   `NOT_SENT` and `lobbyLive` still equals old and no D0 is in flight/unknown/committed,
   `RollbackSeatHandover`/`Release` the local row and restore old. If D0 is
   `PENDING_SEND|SENDING|IN_FLIGHT|COMMITTED|UNKNOWN`, **never restore old**: keep the outbox/attempt, close
   the new connection, and enter `RECONCILING`/recover to the committed candidate. If D0 committed
   but local D fails, call conditional `ReleaseAdmission(admission_id,
   lobby_admission_generation)`;
   if a newer live exists, the release is a no-op and the actor reconciles to it; if it succeeds,
   no old restore (seat unowned); if compensation fails, fault the seat closed and recover from
   the durable live row/lease. If the actor abandons an in-flight D0, it calls
   `CancelAdmission`; only the resulting durable `CANCELLED` tombstone (or a definitive
   `REJECTED`/`NOT_SENT`) can restore old. An empty lookup is not terminal. Both jtis remain spent
   in Lobby.
10. **Teardown/disconnect.** `Release` marks this session's row `RELEASED` (fenced by
    `session_id` + `owner_epoch` + `ticket_generation`); it frees the live seat/spectator slot. It
    never deletes the `jti` row before `retain_until` and never calls Lobby to un-consume.
11. **Crash/restart.** Durable rows survive; a new owner epoch releases all older-epoch live
    rows; Lobby `live_admission` rows are fenced by `table_owner_epoch`/lease. See §5.6.

### 5.3 Lobby consume transaction (A) in detail

**Lock order.** `lobby.room` → `lobby.assignment` → `lobby.table_ticket` → `lobby.match_ticket`,
the same order already documented in `ConsumeTableTicketAsync` (`LobbyApplication.Tickets.cs:253-
273`). Issuance locks room/assignment/active ticket in the same order, so consume/issue cannot
deadlock and cannot both accept a superseded/consumed pair.

**Required checks (all inside the transaction):**

1. Room exists and `state='OPEN'`.
2. `table_ticket` row exists for the signed `jti`; persisted account, room, assignment, role,
   seat, and expiry equal the claims.
3. `consumed_at IS NULL` and `superseded_at IS NULL`; `expires_at > now`.
4. Assignment exists, matches account/room/role/seat, is `RESERVED|CONFIRMED`, and is unexpired.
5. The account has no other active assignment (`uq_assignment_active_account` is the last
   resort).
6. For a player, no other active assignment holds `(room, seat)` (`uq_assignment_active_seat`).
7. Match ticket, if present, is not `CANCELLED|EXPIRED|QUEUED`; a `RESERVED` match may be
   confirmed in the same transaction.
8. Terminal room reconciliation has already run (`EnsureTerminalRoomReconciledAsync`), or the
   transaction observes `state='CLOSED'` and rejects.

**Writes (same transaction):**

```text
INSERT INTO lobby.table_ticket_consumption(jti, ticket_generation, ...)  -- PK jti
UPDATE lobby.table_ticket SET consumed_at = now WHERE jti=...
UPDATE lobby.assignment SET status='CONFIRMED', table_confirmed_at=..., expires_at=NULL ...
UPDATE lobby.match_ticket SET status='CONFIRMED' ...  -- only when it was RESERVED
```

**Idempotent retry.** Add `request_id`, `table_instance`, `session_id` to the consumption row
(or a separate audit table). The idempotency lookup must run **before** the `consumed_at`
rejection; otherwise a retry after a lost response cannot recover. On a retry:

- same `jti` + same `request_id` + same instance/session → return the original result
  (`Replayed=true`), no second insert;
- same `jti` + different request/session → `TICKET_ALREADY_CONSUMED`;
- same `request_id` + different `jti` → `REQUEST_ID_CONFLICT` (unique index);
- inconsistent row (`consumed_at` set but no consumption row, or vice versa) → fail closed
  `TICKET_INCONSISTENT`, never repair by guessing.

To resolve an **unknown commit**, Lobby must persist an attempt row keyed by the service
`request_id` with `IN_PROGRESS | COMPLETED | FAILED` and bind it to `(jti, table_instance,
session_id)`. A retry then returns `IN_PROGRESS` (retry later), the original `COMPLETED` result,
or `FAILED`; a request id with a different tuple is `REQUEST_ID_CONFLICT`. If the attempt row is
absent, the retry may execute; if the attempt lookup is unavailable or times out, TableService
must fail closed and **not** report “no side effect.” The idempotency lookup must run before the
`consumed_at` rejection.

This lets TableService recover from a lost response after a committed Lobby transaction without
weakening I1. A retry after a crash that lost the request id cannot recover and must use a fresh
ticket.

**Retention/cleanup.** Keep consumption rows at least until `ticket.expires_at + skew`. A
maintenance worker may delete older rows; the JWS `exp` check is then the remaining fence.
`lobby.table_ticket` rows may be retained longer for audit; a consumed/superseded row no longer
occupies the active unique index. Because `table_ticket_consumption.jti` has an FK to
`table_ticket.jti`, delete consumption rows before their ticket rows in any cleanup batch.

### 5.3A Lobby freshness generation and `ConfirmAdmission`

`ticket_generation` is the durable freshness order. It is assigned at **issuance**, while the
assignment row is locked, from a monotonic sequence:

```text
lobby.assignment.latest_ticket_generation
lobby.assignment.admission_generation
lobby.table_ticket.ticket_generation
lobby.table_ticket_consumption.ticket_generation
lobby.live_admission (
  assignment_id, live_admission_id, live_ticket_generation,
  admission_generation, table_owner_epoch, session_id, lease_expires_at,
  confirm_request_id
)
```

Proposed issuance transaction (same room → assignment lock order as consume):

```text
BEGIN;
SELECT ... FROM lobby.room WHERE room_id=... FOR UPDATE;
SELECT ... FROM lobby.assignment WHERE assignment_id=... FOR UPDATE;
ticket_generation := nextval('lobby.ticket_generation_seq');
UPDATE lobby.table_ticket SET superseded_at=now()
 WHERE assignment_id=... AND consumed_at IS NULL AND superseded_at IS NULL;
INSERT INTO lobby.table_ticket(..., ticket_generation, ...);
UPDATE lobby.assignment
   SET latest_ticket_generation=ticket_generation
 WHERE assignment_id=...;
COMMIT;
```

`ticket_generation` must be signed in the ticket (proposed new claim `ticket_generation`) or
returned as an authenticated Lobby consume result and stored by TableService. A signed claim is
preferable because it survives a lost response and is auditable; adding it changes the frozen
JWT claim allowlist and is a product/contract decision. Existing rows need a one-time migration:
backfill each ticket’s generation in issuance order (or assign a deterministic new sequence),
set `assignment.latest_ticket_generation` to the maximum, and set the sequence above the maximum;
never leave an existing ticket at generation 0.

**`ConfirmAdmission` is the freshness linearization point.** It is called after WELCOME and
before the local D/actor switch:

```text
BEGIN;
SELECT ... FROM lobby.room WHERE room_id=... FOR UPDATE;
SELECT ... FROM lobby.assignment WHERE assignment_id=... FOR UPDATE;
SELECT ... FROM lobby.live_admission WHERE assignment_id=... FOR UPDATE;
-- verify consumption row, session, jti, ticket_generation, owner_epoch
IF ticket.ticket_generation <> assignment.latest_ticket_generation THEN
    -> STALE_GENERATION
IF live.live_ticket_generation > ticket.ticket_generation THEN
    -> NEWER_LIVE
IF live.live_ticket_generation = ticket.ticket_generation
   AND live.live_admission_id <> p_admission_id THEN
    -> DUPLICATE_ADMISSION
IF live.live_ticket_generation = ticket.ticket_generation
   AND live.live_admission_id = p_admission_id THEN
    -> REPLAYED (return existing admission_generation, do not increment)
IF live.table_owner_epoch <> request.owner_epoch
   AND live.lease_expires_at > now() THEN
    -> LEASE_HELD
admission_generation := assignment.admission_generation + 1;
UPSERT lobby.live_admission(...);
UPDATE lobby.assignment SET admission_generation=...;
COMMIT;
```

The lock order is room → assignment → live admission; consume uses room → assignment → ticket →
match, and issuance uses room → assignment → active ticket. All start room/assignment, so
issuance/consume/confirm serialize on the assignment and cannot produce a stale live row by
lock inversion. `ConfirmAdmission` is idempotent by `(admission_id, request_id)`: the same
confirmed admission returns `REPLAYED` with its existing `admission_generation`; a different
admission reusing the request id conflicts; a different jti/session is a binding mismatch.
`ReleaseAdmission` is a **conditional** compensation: it may clear only the exact
`(live_admission_id, admission_generation, ticket_generation)` that failed local D. If a newer
live row exists, it is a no-op; it never restores an older admission and never clears a newer one.

**A/B adjudication.** The caller's interleaving is:

| Step | Old ticket A | New ticket B |
|---|---|---|
| 1 | A consume succeeds (gen 1) | — |
| 2 | — | issue B (gen 2), consume B |
| 3 | B Reserve gets local `admission_seq=1` | B Confirm → LIVE gen 2 |
| 4 | A Reserve delayed gets local `admission_seq=2` | — |
| 5 | A Confirm checks `gen 1 != latest gen 2` → `STALE_GENERATION`; A local row `SUPERSEDED` | B remains LIVE |

A larger local `admission_seq` therefore cannot make A newer. If A's Confirm happens **before**
B is issued, A may become LIVE; B's later issuance/confirm has a higher ticket generation and
supersedes A. If B is issued but never consumed, A's Confirm still fails closed because
`latest_ticket_generation` is B; the seat may stay idle until B is used or expires (product
decision).

**Relationship to local `admission_seq`.** The local sequence is a TableService **control token**
for stream/command/receipt ordering and idempotency. It is not a freshness order and must never be
the primary comparison for seat ownership. The authoritative tuple is
`(ticket_generation, lobby_admission_generation)`; `admission_seq` is compared only when the two
authoritative generations are equal (for example, two attempts on the same confirmed ticket),
and then only as a tie-breaker. A local row whose ticket/lobby generation is older is stale even
if its `admission_seq` is larger.

**Existing LIVE and restart fencing.** `lobby.live_admission` carries `table_owner_epoch` and
`lease_expires_at`. A different owner epoch is refused while the old live lease is unexpired
(`LEASE_HELD`); after expiry, a newer ticket generation may take over. A new TableService process
must rebuild its local authority from the durable live row and release older-epoch local rows; it
must not assume its new local `admission_seq` is fresher. A Lobby restart retains
`latest_ticket_generation`, `admission_generation`, and the live row, so the same decisions
reapply.

### 5.3B Lobby Confirm/Cancel terminal request fence

A single unsatisfied `request_id` row is the terminal fence shared by Confirm and Cancel:

```sql
CREATE TABLE lobby.table_ticket_d0_request (
    request_id text PRIMARY KEY,
    admission_id text NOT NULL,
    jti text NOT NULL,
    ticket_generation bigint NOT NULL,
    account_id text NOT NULL,
    room_id text NOT NULL,
    assignment_id text NOT NULL,
    session_id text NOT NULL,
    owner_epoch bigint NOT NULL,
    state text NOT NULL,              -- COMMITTED | CANCELLED | REJECTED
    result_code text NULL,
    d0_deadline timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL,
    CONSTRAINT ck_d0_request_state CHECK
        (state IN ('COMMITTED','CANCELLED','REJECTED')),
    CONSTRAINT ck_d0_request_time CHECK (retain_until > created_at)
);

-- Immutable terminal/idempotency evidence. It survives request-row cleanup and
-- retains the full binding plus the original result.
CREATE TABLE lobby.table_ticket_d0_result (
    request_id text PRIMARY KEY,
    admission_id text NOT NULL,
    jti text NOT NULL,
    ticket_generation bigint NOT NULL,
    account_id text NOT NULL,
    room_id text NOT NULL,
    assignment_id text NOT NULL,
    session_id text NOT NULL,
    owner_epoch bigint NOT NULL,
    state text NOT NULL,              -- COMMITTED | CANCELLED | REJECTED
    result_code text NULL,
    live_admission_id text NULL,
    lobby_admission_generation bigint NULL,
    d0_deadline timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL,
    CONSTRAINT ck_d0_result_state CHECK
        (state IN ('COMMITTED','CANCELLED','REJECTED')),
    CONSTRAINT ck_d0_result_time CHECK (retain_until > created_at)
);

ALTER TABLE lobby.table_ticket_consumption
    ADD COLUMN IF NOT EXISTS request_id text NULL,
    ADD COLUMN IF NOT EXISTS admission_id text NULL,
    ADD COLUMN IF NOT EXISTS ticket_generation bigint NULL,
    ADD COLUMN IF NOT EXISTS session_id text NULL,
    ADD COLUMN IF NOT EXISTS owner_epoch bigint NULL,
    ADD COLUMN IF NOT EXISTS d0_deadline timestamptz NULL,
    ADD COLUMN IF NOT EXISTS cancelled_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS d0_retain_until timestamptz NULL,
    ADD COLUMN IF NOT EXISTS terminal_state text NULL,
    ADD COLUMN IF NOT EXISTS terminal_result_code text NULL,
    ADD COLUMN IF NOT EXISTS terminal_live_admission_id text NULL,
    ADD COLUMN IF NOT EXISTS terminal_lobby_admission_generation bigint NULL,
    ADD COLUMN IF NOT EXISTS terminal_at timestamptz NULL;

ALTER TABLE lobby.table_ticket_consumption
    ADD CONSTRAINT ck_consumption_terminal_state CHECK
        (terminal_state IS NULL OR terminal_state IN ('COMMITTED','CANCELLED','REJECTED')),
    ADD CONSTRAINT ck_consumption_terminal_pair CHECK
        ((terminal_state IS NULL AND terminal_at IS NULL)
         OR (terminal_state IS NOT NULL AND terminal_at IS NOT NULL)),
    ADD CONSTRAINT uq_consumption_request UNIQUE (request_id);

-- Backfill existing rows, then SET NOT NULL for request_id/admission_id/
-- ticket_generation/session_id/owner_epoch/d0_deadline/d0_retain_until.

-- d0_deadline is the original consume-time deadline. A terminal row created after
-- the deadline intentionally may have d0_deadline <= created_at; do NOT add a
-- CHECK requiring d0_deadline > created_at. The row records the late rejection.
```

The authoritative consumption row also carries `d0_deadline` and `cancelled_at`:

```text
d0_deadline = LEAST(ticket.expires_at, DB clock_timestamp() + max_admission_window)
cancelled_at = first successful Cancel time (NULL until cancelled)
```

`d0_deadline` is written once at consume, from DB time; no client field can extend it. The one
execution order for Confirm/Cancel/lookup is:

1. Lock `room → assignment → live_admission → request/result/consumption` (or an
   advisory/unique-key lock for an absent request).
2. Validate the full binding `(request_id, admission_id, jti, ticket_generation, account, room,
   assignment, session, owner_epoch)` against **every** existing request/result/consumption row. A
   mismatch is `REQUEST_ID_CONFLICT`; never fall through to a new commit. Every required binding
   argument must be non-NULL; a NULL/missing argument is `INVALID_BINDING`, never a wildcard.
3. Collect **all** terminal evidence before returning anything: result state, request state, and
   consumption `terminal_state`. If more than one exists, their states and bindings must agree,
   including original `result_code` and `live_admission_id`/`lobby_admission_generation`. If a
   terminal row/marker exists but the consumption marker is missing/NULL, or any witness/field
   disagrees, return `TERMINAL_EVIDENCE_INCONSISTENT` and fail closed.
4. If the collected terminal evidence is exact-binding and consistent, return it **before** any
   deadline check: `COMMITTED` → original result/`REPLAYED`; `CANCELLED` → `CANCELLED`; `REJECTED`
   → original `result_code`. This covers request/result cleanup while the consumption marker is
   retained.
5. If there is **no** terminal evidence at all, require the authoritative consumption row; missing
   → `RESULT_EXPIRED` (unknown/fail-closed). Only then check `cancelled_at` and
   `DB now >= d0_deadline`; a genuinely new request may be `CANCELLED`/`DEADLINE_EXPIRED`.
6. A brand-new Confirm before the deadline writes request+result+consumption marker+live in one
   transaction. A deadline-expired new Confirm writes a durable `REJECTED` result **and** the
   consumption marker with `created_at=DB now` and `retain_until=GREATEST(DB now+result_retention,
   deadline+grace)`. Deadline must never mask an already committed result.

The request row is only the short-lived lock/working row; the result row and consumption marker are
the retained witnesses. Deleting the request row cannot reopen a committed/cancelled decision.

Both operations lock in the same order: `lobby.room` → `lobby.assignment` →
`lobby.live_admission` → the request row (or an advisory/unique-key lock for an absent row).
`ConfirmAdmission` and `CancelAdmission` are idempotent by `request_id` and must verify the full
binding `(admission_id, jti, ticket_generation, account, room, assignment, session, owner_epoch)`.
A mismatch is `REQUEST_ID_CONFLICT`; it never creates or overwrites a terminal row.

**Confirm semantics (terminal result wins).** In the lock transaction:

1. Lock/read `table_ticket_d0_result`, `table_ticket_d0_request`, and the consumption row.
   Validate every existing row's full binding. A mismatch → `REQUEST_ID_CONFLICT`; do not commit.
2. Collect all terminal evidence before returning: result state, request state, consumption
   `terminal_state`. If any terminal row exists but the consumption marker is missing/NULL, or
   result/request/marker states disagree, return `TERMINAL_EVIDENCE_INCONSISTENT`; do not infer a
   new commit, deadline rejection, or replay.
3. If the consistent terminal evidence is `COMMITTED`/`CANCELLED`/`REJECTED`, return it **before**
   any deadline check: `COMMITTED` → `REPLAYED` with the original
   `live_admission_id`/`lobby_admission_generation`; `CANCELLED` → `CANCELLED`; `REJECTED` → the
   original `result_code` (including `DEADLINE_EXPIRED`). This covers request/result cleanup while
   the consumption marker survives.
4. If there is no terminal evidence at all, require the authoritative consumption row; missing →
   `RESULT_EXPIRED` (unknown/fail-closed). Only then check `cancelled_at` and
   `DB now >= d0_deadline`: `CANCELLED` / `DEADLINE_EXPIRED`. For a deadline-expired new Confirm,
   write result **and** consumption marker with `created_at=DB now` and
   `retain_until=GREATEST(DB now+result_retention, deadline+grace)`.
5. A brand-new Confirm before the deadline performs freshness/live/lease checks and writes the
   `COMMITTED` request+result+consumption marker+live in one transaction. Deadline must never mask
   an already committed result. A concurrent Confirm serializes on the assignment/request lock; it
   cannot both commit.
**Cancel semantics (terminal result wins).** Cancel uses the same lock/binding order and the same
“collect/validate all evidence first” rule as Confirm. If terminal evidence exists and is
consistent: `COMMITTED` → `ALREADY_COMMITTED` **before** the deadline check and without touching
the live row; `CANCELLED` → `REPLAYED_CANCELLED`; `REJECTED` → the original refusal. A binding
mismatch → `REQUEST_ID_CONFLICT`; a terminal row with missing/NULL consumption marker or
disagreeing states → `TERMINAL_EVIDENCE_INCONSISTENT`. Only when no terminal evidence exists does
Cancel require the consumption row (missing → `RESULT_EXPIRED`), check
`DB now >= d0_deadline` (return `DEADLINE_EXPIRED`), else set `consumption.cancelled_at=DB now` and
write a durable `CANCELLED` request+result+consumption marker in the same assignment-lock
transaction; do not modify `live_admission`. A later original Confirm sees the marker and returns
`CANCELLED` even if the request was delayed. If cancel cannot persist the tombstone/cancelled_at,
return `UNKNOWN` and old remains blocked.

### 5.3C Result/consumption/request cleanup invariant

The terminal evidence is **at least one** of:

- the immutable `lobby.table_ticket_d0_result` row (full binding + original result), or
- the consumption row's terminal marker (`terminal_state`, `terminal_result_code`,
  `terminal_live_admission_id`, `terminal_lobby_admission_generation`, `terminal_at`), with the
  full binding.

`COMMITTED`/`CANCELLED`/`REJECTED` is written to the result row **and** the consumption terminal
marker in the same transaction as the live update. Once set, the marker is never reset to NULL.
Therefore a committed request whose response was lost cannot become a “new” request merely because
the request/result rows were cleaned.

Cleanup runs in one transaction with the same `room → assignment → live → request/result/
consumption` lock order as Confirm/Cancel:

| Row | Earliest deletion | Required witness |
|---|---|---|
| `d0_request` | after `request.retain_until` and deadline+grace | result or consumption terminal marker still present |
| `d0_result` | after `result.retain_until` | consumption terminal marker present, or consumption is deleted in the same transaction |
| `table_ticket_consumption` | after `d0_retain_until` and result retention | result deleted in the same transaction; no earlier marker deletion |

`d0_retain_until` is set to at least the result's `retain_until` and the marker/request horizon. If
the result row already disappeared in an earlier cleanup, the consumption marker is the last
witness: it may be deleted only after `d0_retain_until` (which already covers the result retention)
and only by the same locked cleanup transaction. If the result still exists, both rows are deleted
in that one transaction. There is no state where the result is deleted before its retention while
the consumption row remains with a NULL marker.

Consequences:

- Request row gone, result/consumption marker present → replay the original terminal result, even
  after restart and arbitrarily late.
- Result row gone, consumption marker present → replay from the marker.
- Both result and consumption gone → `RESULT_EXPIRED`; **never** `DEADLINE_EXPIRED` and never a
  new `REJECTED`.
- Consumption exists with `terminal_state IS NULL` but a result/request row or outbox says a D0
  was sent/terminal → `TERMINAL_EVIDENCE_INCONSISTENT`; do not treat it as a new request.
  `DEADLINE_EXPIRED` is allowed only for a genuinely never-terminal request.
- Cleanup that finds an inconsistency aborts and retains rows; it must not “repair” by deleting
  the last witness.

If an actor sees `RESULT_EXPIRED`, `AUTHORITATIVE_RECORD_MISSING`, or
`TERMINAL_EVIDENCE_INCONSISTENT`, it must not restore old; it stays `RECONCILING`/faulted.
Only a definitive terminal `CANCELLED`/`REJECTED`/proved-unsent, with no unresolved attempt,
permits old restoration.

`RESULT_EXPIRED` means terminal evidence once existed but all result/marker retention has expired.
`AUTHORITATIVE_RECORD_MISSING` means the consumption row is absent before any terminal evidence can
be established. They are distinct causes but the same fail-closed family: neither is
`DEADLINE_EXPIRED`, neither may write a new `REJECTED`, and neither may restore old. The API must
document which code it exposes; it must not use either as a synonym for “no side effect”.

### 5.4 TableService durable admission schema (B–D)

New migration `table_store` version 2 (proposed name
`dotnet/src/Mahjong.Infrastructure/Migrations/002_ticket_admission.sql`):

```sql
CREATE SEQUENCE table_store.ticket_admission_seq;

CREATE TABLE table_store.table_ticket_admission (
    admission_id text PRIMARY KEY,
    jti text NOT NULL UNIQUE,
    request_id text NOT NULL,
    lobby_consumed_at timestamptz NOT NULL,
    lobby_consumption_id text NOT NULL,
    ticket_generation bigint NOT NULL,
    room_id text NOT NULL,
    assignment_id text NOT NULL,
    account_id text NOT NULL,
    role text NOT NULL,
    seat_id integer NULL,
    session_id text NOT NULL,
    owner_instance text NOT NULL,
    owner_epoch bigint NOT NULL,
    admission_seq bigint NOT NULL DEFAULT nextval('table_store.ticket_admission_seq'),
    lobby_admission_generation bigint NULL,
    state text NOT NULL,
    reserved_at timestamptz NOT NULL,
    committed_at timestamptz NULL,
    welcomed_at timestamptz NULL,
    released_at timestamptz NULL,
    reservation_expires_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL,
    CONSTRAINT uq_admission_request UNIQUE (request_id),
    CONSTRAINT ck_admission_request CHECK (char_length(request_id) BETWEEN 1 AND 128),
    CONSTRAINT ck_admission_role CHECK (role IN ('PLAYER','SPECTATOR')),
    CONSTRAINT ck_admission_ticket_generation CHECK (ticket_generation > 0),
    CONSTRAINT ck_admission_state CHECK
        (state IN ('RESERVED','COMMITTED','LIVE','RELEASED','SUPERSEDED')),
    CONSTRAINT ck_admission_seat CHECK (
        (role='PLAYER' AND seat_id IS NOT NULL AND seat_id BETWEEN 0 AND 3) OR
        (role='SPECTATOR' AND seat_id IS NULL)),
    CONSTRAINT ck_admission_live CHECK (
        (state IN ('RESERVED','COMMITTED') AND welcomed_at IS NULL) OR
        (state='LIVE' AND welcomed_at IS NOT NULL) OR
        state IN ('RELEASED','SUPERSEDED')),
    CONSTRAINT ck_admission_released CHECK (
        (state IN ('RELEASED','SUPERSEDED') AND released_at IS NOT NULL) OR
        (state IN ('RESERVED','COMMITTED','LIVE') AND released_at IS NULL)),
    CONSTRAINT ck_admission_seq CHECK (admission_seq > 0),
    CONSTRAINT ck_admission_time CHECK (
        reservation_expires_at > reserved_at AND retain_until >= reservation_expires_at)
);

CREATE UNIQUE INDEX uq_admission_live_seat
    ON table_store.table_ticket_admission (room_id, seat_id)
    WHERE role='PLAYER' AND state='LIVE';

CREATE UNIQUE INDEX uq_admission_live_spectator_account
    ON table_store.table_ticket_admission (room_id, account_id)
    WHERE role='SPECTATOR' AND state='LIVE';

CREATE INDEX ix_admission_live_spectator
    ON table_store.table_ticket_admission (room_id)
    WHERE role='SPECTATOR' AND state IN ('RESERVED','COMMITTED','LIVE');

CREATE INDEX ix_admission_active_seat_seq
    ON table_store.table_ticket_admission (room_id, seat_id, admission_seq)
    WHERE state IN ('RESERVED','COMMITTED','LIVE');

CREATE INDEX ix_admission_active_seat_ticket
    ON table_store.table_ticket_admission (room_id, seat_id, ticket_generation)
    WHERE state IN ('RESERVED','COMMITTED','LIVE');

CREATE INDEX ix_admission_lobby_generation
    ON table_store.table_ticket_admission (lobby_admission_generation)
    WHERE lobby_admission_generation IS NOT NULL;

CREATE INDEX ix_admission_reservation_expiry
    ON table_store.table_ticket_admission (reservation_expires_at)
    WHERE state IN ('RESERVED','COMMITTED');

CREATE INDEX ix_admission_terminal_cleanup
    ON table_store.table_ticket_admission (released_at)
    WHERE state IN ('RELEASED','SUPERSEDED');

-- D0 send intent: persisted before the network call; UNKNOWN/SENDING/IN_FLIGHT
-- blocks old command authority until resolved.
CREATE TABLE table_store.table_ticket_d0_outbox (
    admission_id text PRIMARY KEY,
    request_id text NOT NULL UNIQUE,
    ticket_generation bigint NOT NULL,
    owner_epoch bigint NOT NULL,
    state text NOT NULL,
    response_blob bytea NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_d0_state CHECK
        (state IN ('PENDING_SEND','NOT_SENT','SENDING','IN_FLIGHT',
                   'COMMITTED','REJECTED','CANCELLED','UNKNOWN'))
);
```

**Outbox transitions.** `PENDING_SEND → NOT_SENT` only with proven no-write;
`PENDING_SEND → SENDING → IN_FLIGHT`; `IN_FLIGHT → COMMITTED|REJECTED|CANCELLED|UNKNOWN`;
`UNKNOWN → COMMITTED|REJECTED|CANCELLED`; `NOT_SENT/COMMITTED/REJECTED/CANCELLED` are terminal.
A terminal `CANCELLED` requires the Lobby tombstone; `NOT_SENT` requires transport proof. No
transition may infer `REJECTED` merely from a query absence. Startup maps
`PENDING_SEND|SENDING|IN_FLIGHT` to `UNKNOWN` and loads them as unresolved; `NOT_SENT` remains a
terminal record and may restore old only under the no-unresolved conditions.

**Seat CHECK is explicit about NULL.** `seat_id BETWEEN 0 AND 3` evaluates to `UNKNOWN` when
`seat_id IS NULL`; a SQL `CHECK` accepts `UNKNOWN` (only `FALSE` rejects). The final predicate
must therefore say `role='PLAYER' AND seat_id IS NOT NULL AND seat_id BETWEEN 0 AND 3`, and the
spectator branch must say `seat_id IS NULL`. The isolated probe reproduced the old form accepting
`('PLAYER', NULL)` and confirmed the final form rejects `PLAYER NULL`, `PLAYER -1`, `PLAYER 4`,
and `SPECTATOR 0` (§7.2). SQLite has the same three-valued logic; the caller's Node/SQLite
observation is consistent with this PostgreSQL result.

**`ck_admission_live` truth table.** `RESERVED`/`COMMITTED` have no `welcomed_at`; `LIVE`
requires it; `RELEASED`/`SUPERSEDED` may have either, because a reservation can be superseded
before WELCOME and a live row can be released after WELCOME. `RELEASED`/`SUPERSEDED` must have `released_at`; active states must have it null. The probe initially made
`SUPERSEDED` require `welcomed_at`; that rejected a legitimately superseded pre-WELCOME
reservation and was corrected before the final DDL.

**Why `LIVE` only is unique.** A same-account re-ticket must be able to reserve and commit while
the old socket still owns the seat. The old row remains `LIVE` until the new WELCOME, Lobby
Confirm, and local D succeed; then both rows change atomically (old `SUPERSEDED`, new `LIVE`). A
cross-account reservation is rejected earlier by the live-owner account check.
`RESERVED`/`COMMITTED` rows can coexist; they do not own a live seat. The spectator index
additionally prevents two live spectator sockets for the same `(room, account)`; this is a
proposed product rule (§10 items 5 and 18).

**Why unique `jti` and `request_id`.** `jti` is the TableService-side defense-in-depth exact-
replay fence. `request_id` is the idempotency/binding key: `Reserve` first looks up the exact
`(request_id, jti, session_id)`; a match returns the existing row and state, while a different
`jti` with the same `request_id` conflicts (`uq_admission_request`) and a different session with
the same `jti` is `DuplicateJti`. The unique constraints are the database backstop; the
application lookup is still required for a clean idempotent response.

**`ticket_generation` and `lobby_admission_generation`.** These are the authoritative freshness
fields. `ticket_generation` comes from Lobby issuance (signed claim or authenticated consume
result); `lobby_admission_generation` comes from Lobby `ConfirmAdmission`. Comparisons for seat /
spectator ownership use `(ticket_generation, lobby_admission_generation)` descending. A local row
with a larger `admission_seq` but an older ticket generation is stale and must be rejected.

**`admission_seq` (subordinate control token).** `admission_seq` is only the TableService local
control/stream token. It is not a freshness
order; it is the per-admission control generation used by `Commit`, `EvictOnWelcome` only after
the Lobby generation matches, commands, and deliveries (§5.5A). A rolled-back transaction burns a
sequence value; that is fine. Do not derive it from a client value or from `now()`.

**D0 outbox.** `table_ticket_d0_outbox` is written **before** the D0 network send and updated only
when the response is delivered. `PENDING_SEND`/`SENDING`/`IN_FLIGHT`/`UNKNOWN` are unresolved and
block old command authority. On startup, unresolved rows are loaded into `d0Attempts`; no actor
may default to old. A terminal `REJECTED`/`NOT_SENT`/`CANCELLED` row is required before any old restoration.

**Retention is terminal-only.** `retain_until` alone is **not** a delete predicate. A live or
pending row can outlive the ticket TTL; deleting it would free a seat/spectator slot while the
socket still exists and allow an over-capacity replacement. Cleanup may delete only terminal
rows (`RELEASED`/`SUPERSEDED`) that have a non-null `released_at`, whose release is older than the
cleanup grace, **and** whose `retain_until` has passed. The exact `jti` fence is kept until then;
after deletion, the Lobby consumption row and the JWS `exp` remain. See §5.6 for the room-lock
race.

### 5.5 Capacity, ordering, handover and release rules

- **Reserve (B):** lock the room lease row; verify epoch; store the ticket's `ticket_generation`
  and `lobby_consumption_id`; assign the subordinate local `admission_seq`; check player live-owner
  account and spectator count; insert `RESERVED`. A local row with a newer `ticket_generation` is
  a fast-path conflict, but it is not the freshness authority.
- **Commit (C):** verify epoch; require this row still `RESERVED`; verify the stored
  `ticket_generation` still matches the consumption record. Do not decide freshness from local
  `admission_seq`.
- **Lobby Confirm (D0):** call Lobby `ConfirmAdmission` (§5.3A); it checks
  `ticket_generation == latest_ticket_generation`, existing live generation, owner epoch/lease,
  and returns `lobby_admission_generation`. A `STALE_GENERATION`/`NEWER_LIVE`/`LEASE_HELD` result
  closes this connection; local row becomes `SUPERSEDED`.
- **Local welcome/evict (D):** lock the room lease row, this row, and the identity's other active
  rows; require `COMMITTED` and the D0 token/generation.
  - Re-evaluate the authoritative tuple. If **any newer `(ticket_generation,
    lobby_admission_generation)` row** exists, this WELCOME lost: mark this row `SUPERSEDED`,
    return `SelfSuperseded`, close this connection 4403, and **never demote the newer row**.
    This is the reverse-WELCOME guard: an older delayed admission can never evict a newer
    connection, even if its local `admission_seq` is larger.
  - No newer authoritative row and no `LIVE` owner → promote this row to `LIVE`.
  - No newer authoritative row and a same-account `LIVE` owner → demote the old row to
    `SUPERSEDED` **first**, then promote this row to `LIVE`, and return the old session for 4403.
    The order matters because the partial unique index is not deferrable; one transaction, and
    rollback restores the old `LIVE` row.
  - No newer authoritative row and a different-account `LIVE` owner → do **not** touch the old
    row; mark this row `SUPERSEDED`, return `SelfSuperseded`, and close this connection 4403.
    This is a fail-closed inconsistency path; cross-account reservation should already have been
    refused in (B).
- **Why `RESERVED` can coexist.** A newer reservation may be in flight while the older socket
  still works. The newer row's `ticket_generation` (and Lobby's `latest_ticket_generation`) is the
  fence; local `admission_seq` is not. A newer reservation that later fails is released; the older
  admission has already been superseded and is closed, so the seat needs a fresh ticket. This
  trades a small availability loss for “an older request can never overwrite a newer one.”
- **Rollback:** `COMMITTED|RESERVED` for this session → `RELEASED`; a `LIVE` row is never rolled
  back by a post-hoc failure. A definitively rejected or transport-proved `NOT_SENT` D0 releases
  the local row and may
  restore old only if `lobbyLive` still equals old. A `COMMITTED`/`UNKNOWN` D0 does **not**
  authorize old restoration: mark the local row `RELEASED` or `FAULTED`, reconcile to
  `lobbyLive`, and never leave a local reservation `RESERVED` after abandonment.
- **Release:** `RESERVED|COMMITTED|LIVE` for this session and epoch → `RELEASED`,
  `released_at=now`; idempotent; no-op if the session/epoch/ticket generation does not match. A
  displaced old session's release must not touch the new owner's row.
- **Seat disconnect fence:** `TryGetSeatOwner(room, seat)` reads the single `LIVE` row, including
  its `ticket_generation`/`lobby_admission_generation`. Only that session may notify
  `DisconnectAsync`; a displaced old connection must not put the new seat into grace. The actor
  also checks the generation on every command (§5.5A).
- **Spectator cap:** configured in TableService; count all `RESERVED|COMMITTED|LIVE` spectator
  rows for the room under the room lease lock. Release every failure path. The Lobby assignment
  cap is a separate pre-admission quota; see §10 items 6 and 18. A same-account spectator refresh
  uses the same `(room, account)` freshness fence; `uq_admission_live_spectator_account` prevents
  two live spectator sockets for the same account.

### 5.5A Authority fencing: WELCOME is not the control grant

A database row with `state='LIVE'` is necessary but not sufficient to prove that exactly one
connection can issue authoritative commands. The old socket may still be open, in-flight frames
may still be queued, and the close is best-effort. The authority must be a **generation token**
checked on every command and delivery, not a post-hoc close.

**Durable tokens.** `owner_epoch` is the room-lease generation. `ticket_generation` is Lobby
issuance order. `lobby_admission_generation` is Lobby `ConfirmAdmission` order.
`admission_seq` is only the TableService local control/stream token. All four may be carried, but
freshness comparisons use `(ticket_generation, lobby_admission_generation)`; `owner_epoch` fences
the room lease; `admission_seq` is the final local tie-breaker. Two connections can share one
`owner_epoch`, so `owner_epoch` alone cannot distinguish old and new same-account sockets.

**Connection identity.** Add `ticket_generation`, `lobby_admission_generation`,
`admission_id`, `admission_seq`, and `owner_epoch` to `TableConnectionIdentity` (the existing
candidate identity already carries `sessionId`, room, role, seat, account, scope, jti; see
`TableSessionHandler.cs:1018-1027`). Every command, control command, receipt lookup, and stream
subscription is bound to that identity.

**Actor authority state, durable `lobbyLive`, and overlapping pending.** For each seat/spectator
identity the room actor holds:

```text
SeatAuthority {
  current: { ticketGeneration, lobbyAdmissionGeneration, admissionSeq,
             admissionId, sessionId } | null,
  lobbyLive: { ticketGeneration, lobbyAdmissionGeneration,
               admissionId } | null | UNKNOWN,
  d0Attempts: map admissionId -> {
      token, ticketGeneration,
      state: PENDING_SEND|NOT_SENT|SENDING|IN_FLIGHT|COMMITTED|REJECTED|CANCELLED|UNKNOWN,
      outboxId, lobbyGeneration, localDReady },
  pending: { token, ticketGeneration, admissionSeq, admissionId } | null,
  pendingTokenEpoch,
  phase: NORMAL | HANDOVER_PENDING | RECONCILING | FAULTED
}
```

`lobbyLive` is the **durable floor**. A D0 result is recorded in `d0Attempts`/`lobbyLive` even when
its actor pending token has been replaced; an old token may be ignored for the actor switch but
its result must never disappear. `PENDING_SEND`, `SENDING`, `IN_FLIGHT`, and `UNKNOWN` are **unresolved**: a
request that may have reached Lobby but whose result is not delivered. `current` may still hold
the old admission in memory, but `canCommand` requires `phase=NORMAL`, no unresolved D0 at or
above the current ticket generation, and `current` exactly equal to a known `lobbyLive`;
otherwise the old connection has no command authority.

**Send fence (before D0).** The actor mailbox performs, in order:

1. Create a durable D0 outbox row keyed by `(admission_id, request_id)` with state `PENDING_SEND`.
2. Mark the attempt `SENDING` and persist it (same outbox transaction or a durable attempt row).
3. Only then write the D0 request to the transport; on a successful write, mark `IN_FLIGHT`.
4. Deliver the response through the mailbox; record `COMMITTED`/`REJECTED`/`UNKNOWN`.

If step 1/2 cannot be persisted, do **not** send the request. If the transport proves no request
bytes were written (for example connect failure before send), mark `NOT_SENT`; if that proof is
unavailable, mark `UNKNOWN`. A process crash after step 2 leaves a durable `SENDING`/`IN_FLIGHT`
row; on restart it becomes `UNKNOWN` and blocks old until reconciled. The outbox row is the
recoverable evidence that an in-flight D0 may have committed; an in-memory `pending` token alone
is not sufficient.

The actor mailbox serializes all `BeginHandover`, `MarkSending`, `SendD0`, `D0Result`,
`CompleteHandover`, `FailHandover`, command, and delivery messages. `BeginHandover` replaces the
pending token only if the new ticket generation is newer; it never erases `d0Attempts`/outbox or
regresses `lobbyLive`.

**D0 result handling (independent of token).**

- `D0` **terminal-result check before deadline**: exact-binding `COMMITTED` (request row or
  consumption terminal marker) → `REPLAYED` even after deadline/restart/cleanup; `CANCELLED` →
  `CANCELLED`; `REJECTED` → original refusal. A missing consumption row without terminal evidence
  → `RESULT_EXPIRED`; a binding mismatch → `REQUEST_ID_CONFLICT`. Only when no terminal
  evidence exists do `cancelled_at`/`DB now >= d0_deadline` apply. If the result is gone but the
  consumption terminal marker says `COMMITTED`, replay from the marker. If all terminal evidence
  is gone, return `RESULT_EXPIRED` (not `DEADLINE_EXPIRED`); if the marker/result disagree, return
  `TERMINAL_EVIDENCE_INCONSISTENT`. Neither may restore old. Deadline must never mask a
  committed result; a post-deadline retry must not be treated as a new no-side-effect rejection.
  All checks are under the same assignment/request lock as Cancel/cleanup.
- `D0 COMMITTED`/`REPLAYED`: record the attempt and set `lobbyLive` if its generation is at least
  the current floor. If the token is the current pending, mark it committed; if the token is stale,
  keep the result as a durable candidate and enter `RECONCILING`; never drop it.
- `D0 PENDING_SEND`/`SENDING`/`IN_FLIGHT` (including response queued/not delivered):
  **unresolved**. It may have
  committed, so old restoration is forbidden. The actor stays `HANDOVER_PENDING`/`RECONCILING`;
  commands are rejected until the response/query resolves it.
- `D0 UNKNOWN` (timeout/reset): record the attempt as `UNKNOWN`, set `lobbyLive=UNKNOWN`, and enter
  `RECONCILING`. No old connection may command while the result is unknown. Retry/query with the
  same `request_id`; a late response is applied conditionally (below).
- `D0 REJECTED`: record the rejection; Lobby did not change. Only this case may restore `current`,
  and only if `lobbyLive` still equals `current` and **no other attempt is
  PENDING_SEND|SENDING|IN_FLIGHT|COMMITTED|UNKNOWN**.
- `NOT_SENT`: only a transport-proved no-write state; it may restore old under the same conditions.
- `CANCELLED`/`REPLAYED_CANCELLED`: a durable Lobby cancel tombstone. It may restore old only if
  no other attempt is unresolved/committed and `lobbyLive` still equals `current`; the tombstone
  guarantees the delayed original Confirm can never commit.
- `ALREADY_COMMITTED`: cancel lost the race. Record the committed live, do not restore old, and
  recover to that admission (or fail closed if its local D is unavailable).
- A query/lookup that returns **ABSENT** is not a terminal rejection. The actor must not restore
  old from absence; it must Cancel (which creates the tombstone) or remain `RECONCILING`.
- `D0 COMMITTED` for an older token after a newer live exists: record/audit it but never regress
  `lobbyLive` or overwrite the newer admission; the older local D must be rejected.
- `D0 UNKNOWN` followed by a newer `D0 COMMITTED`: the newer live generation wins; the older
  unknown result must be resolved/rejected but cannot overwrite the newer live. An older unresolved
  attempt does not block a newer confirmed current, but it remains audited until resolved.

**`FailHandover(token)` rules.**

- A stale token is ignored for pending-state changes, but its D0 result/outbox remains in
  `d0Attempts`/`lobbyLive`.
- If **any attempt is `PENDING_SEND`, `SENDING`, `IN_FLIGHT`, or `UNKNOWN`** (including an older token whose
  response has not been delivered), **never restore old**. Call `recoverToLobbyLive()`: switch to
  a committed attempt with a completed local D, or enter `RECONCILING` with no command authority.
- If the current pending's D0 is `COMMITTED`, never restore old; recover to the committed
  candidate/fail closed.
- Only when no attempt is unresolved/committed may a definitive
  `REJECTED`/`NOT_SENT`/`CANCELLED` restore `current`, and only if `lobbyLive` still equals
  `current`. A query returning `ABSENT` is not one of these terminal results.
- A newer pending's failure therefore cannot restore an old connection that Lobby may already have
  replaced; it recovers to a committed D0 candidate or fails closed.

**`CompleteHandover`/local D.** A successful local D may switch to the attempt only if its
`ticket_generation`/`lobby_admission_generation` equals `lobbyLive`; a stale attempt may be kept
as a local-D-ready candidate but cannot overwrite a newer current. A failed local D calls
`ReleaseAdmission(admissionId, lobbyGeneration)`:

```sql
UPDATE lobby.live_admission
   SET released_at=now(), live_admission_id=NULL
 WHERE live_admission_id=@admission_id
   AND admission_generation=@lobby_generation
   AND latest_ticket_generation=@ticket_generation;
```

Zero rows means a newer live exists: do not clear it, do not restore old; reconcile to the newer
live or `FAULTED`. If the release succeeds, there is no live admission; enter `RECONCILING` and
require a fresh ticket. Never compensate by restoring `current` after a committed D0.

**D0 timeout/unknown and late responses.** The D0 request is idempotent by
`(admission_id, request_id)`. On timeout, `lobbyLive=UNKNOWN`; the actor rejects commands. A query
or retry returns `COMMITTED`/`REPLAYED`/`REJECTED`/`IN_PROGRESS`; only a definitive result moves
`lobbyLive`. A late `COMMITTED` for an older token is applied only if no newer live exists; a late
`COMMITTED` after a newer live is recorded but rejected (`NEWER_LIVE`) and cannot displace it.
The same rule applies after restart.

**Local D/actor switch atomicity.** There is no cross-system atomic commit. The actor holds
`HANDOVER_PENDING`/`RECONCILING` (no commands) across D0 and local D. After local D commits, the
actor switches current; if that switch fails, enter `FAULTED`, close both connections, and recover
from `lobbyLive`/the local row. If the process crashes between local D and actor switch, restart
reads Lobby live and local state: it restores only a matching confirmed local admission; otherwise
it has no command authority. The old local generation is never the default.

**Linearization with WELCOME before Lobby Confirm and local D.** Keep WELCOME before the final
commit, but make WELCOME readiness, not authority:

1. `BeginHandover` for the new generation; old and new commands are rejected; the old stream is
   marked stale.
2. WELCOME is sent with `admission_id`, `admission_seq`, `ticket_generation`,
   `lobby_admission_generation` (if known), and `owner_epoch`, plus a pending/ready indication. A
   new client must not treat WELCOME alone as permission to command.
3. Lobby `ConfirmAdmission` (D0) returns the result; `lobbyLive` is updated even if the pending
   token is stale.
4. Local D is conditional on the D0 generation; it demotes old `LIVE`, promotes this row, and
   `CompleteHandover(token)` switches the actor authority. Only now are new commands accepted.
5. The old socket is closed best-effort; its commands are already stale by
   `(ticket_generation, lobby_admission_generation, admission_seq)`, so delayed close is harmless.

If D0 has not committed (definitive rejection/no request), `FailHandover` may restore old only
under the conditions above. If D0 is committed or unknown, old restoration is forbidden; recover
to Lobby live or fail closed.

**Reported in-flight interleaving.** Initial old is `current` and Lobby LIVE. A `Begin`;
A's D0 is **sent** and Lobby commits A, but the response is not yet delivered to the actor (the
actor sees `IN_FLIGHT`, not `COMMITTED`). B `Begin` replaces A pending. A `Complete`/`Fail` is
stale and ignored for pending state, but A's in-flight outbox remains. B fails before its own D0;
`FailHandover(B)` must not restore old because A may have committed. The correct result is
`lobbyLive=A`, `current` still old only as stale memory, `phase=RECONCILING`, and
`oldCanCommand=false`. If A's response arrives/local D is ready, recover to A; otherwise no
command authority until A/reconnect. A model that only compares pending tokens restores old and
creates a Lobby=old split brain; the caller probe reproduces `{"lobbyLive":"A","actorFloor":"old",
"outcome":"RESTORED_CURRENT","oldCanCommand":true}` with exit 1. The send fence/outbox in §5.5A
prevents that.

**Per-command/delivery rules.**

- Reject stale `(ticket_generation, lobby_admission_generation, admission_seq)` with a stable
  `STALE_ADMISSION`/`STALE_CONTROL` reason and zero side effects. Do not apply, and do not write an
  `APPLIED` receipt.
- In `HANDOVER_PENDING`, return retryable `ADMISSION_PENDING` (or close) for both generations;
  never accept a command because “the old socket is still connected.”
- Seat-private pushes carry the full tuple. The actor stops routing to the old generation at the
  authority switch; any old in-flight frame is tagged stale and must be ignored by the client.
  Public spectator streams are not seat-private and remain independent.
- Receipt/scope identity includes `session_id`/`ticket_generation`/`admission_seq`; an old
  connection cannot read or reuse a new admission's receipt/scope, and a new connection cannot
  claim an old receipt.
- The room lease `owner_epoch` is checked independently; a host that lost the lease rejects all
  commands even if its actor has not yet observed the loss.

**Why this is not “single `LIVE` + close”.** The old socket may be open and may have queued
commands when D commits. The command fence rejects those by generation; the close is only cleanup.
Conversely, the new socket may receive WELCOME before D; it is not authoritative until step 4, so
it cannot issue a command that races the old authority. The database `LIVE` row and `lobbyLive`
floor prove the durable ordering; the actor generation proves the live control path. If
`lobbyLive` is newer or unknown, an actor `current` that does not match it is **fenced**: it may
exist in memory for audit but cannot command.

### 5.6 Crash, restart and failover

**Lobby.** A committed consumption row survives restart and blocks the same `jti`; a transaction
that did not commit leaves no row and the ticket may be retried. This is the existing PG behavior
and was re-probed in §7 (P1/P2/P6).

**TableService.** A committed `RESERVED`/`COMMITTED`/`LIVE` row survives restart with its
`owner_epoch`. When a host acquires a room lease with a newer `owner_epoch`, it must atomically:

```text
UPDATE table_store.table_ticket_admission
   SET state='RELEASED', released_at=clock_timestamp()
 WHERE room_id=@room AND owner_epoch < @new_epoch
   AND state IN ('RESERVED','COMMITTED','LIVE');
```

This is the recovery fence: a new owner never inherits phantom live seats or spectator slots from
a dead owner. It does **not** delete the `jti` rows; they remain until `retain_until`, so the
spent-ticket fence is preserved. A crashed owner's in-flight `Reserve`/`Commit` that reaches the
DB after the new epoch will fail the `owner_epoch` check and roll back.

**Crash windows:**

| Crash point | Durable state | Result |
|---|---|---|
| Before Lobby commit | No consumption row | Ticket may be retried; no WELCOME |
| Lobby request sent but commit outcome unknown | Attempt/consumption may or may not exist | Resolve by same `request_id`; do not assume no side effect (§5.8) |
| After Lobby commit, before local Reserve | Lobby row spent; no local row | No WELCOME; fresh ticket required |
| After local `RESERVED`, before `COMMITTED` | Local reservation, Lobby spent | Old epoch cleanup releases; no WELCOME; fresh ticket |
| After `COMMITTED`, before WELCOME | Local committed, Lobby spent, actor pending | No command authority; old epoch cleanup releases; fresh ticket |
| After WELCOME, before Lobby Confirm | Actor `HANDOVER_PENDING`; local `COMMITTED`; Lobby not live | New has no authority; Confirm failure/restart restores old or epoch cleanup; no new live row |
| After Lobby Confirm, before local D | Lobby live row new, old stale; local `COMMITTED`; actor pending | No connection commands; local D failure must conditionally compensate `ReleaseAdmission`; crash leaves Lobby live row fenced by owner epoch/lease and local recovery |
| Lobby D0 result unknown | Lobby may or may not have committed; actor `RECONCILING` | No old commands; retry/query same request id; a late commit is recorded; do not restore old |
| D0 intent persisted but request not sent (crash) | Outbox `SENDING`/`PENDING_SEND`; Lobby unchanged | On restart it is `UNKNOWN`/needs proof; old blocked until definitively unsent/rejected |
| D0 sent, response queued or lost | Outbox `IN_FLIGHT`; Lobby may be live | No old commands; response/query resolves; restart maps to `UNKNOWN` |
| D0 committed, response not delivered | Lobby live new; actor `IN_FLIGHT`/unknown | No old commands; late response recovered conditionally |
| Committed before deadline, retry after | Terminal `COMMITTED` row/marker | `REPLAYED`; not `DEADLINE_EXPIRED`; no old restore, no duplicate effect |
| Cancel tombstone committed before delayed D0 | Lobby request `CANCELLED`; live unchanged | Late Confirm `CANCELLED`; old may restore only after the definitive tombstone result |
| Tombstone cleaned after deadline | Request row gone; consumption has `cancelled_at`/`d0_deadline` | Late Confirm `CANCELLED`/`DEADLINE_EXPIRED`; never commit; old remains safe |
| Result cleaned, consumption marker remains | Request/result gone; marker COMMITTED/CANCELLED | Replay original; Cancel ALREADY_COMMITTED/REPLAYED_CANCELLED; no old |
| All terminal evidence cleaned | Result/request/consumption gone | RESULT_EXPIRED; no commit, no old; never DEADLINE_EXPIRED |
| Consumption record missing | No authoritative deadline/cancel evidence | `AUTHORITATIVE_RECORD_MISSING`; fail closed; no commit |
| D0 Confirm committed before Cancel | Lobby request `COMMITTED`; live new | Cancel `ALREADY_COMMITTED`; old must not restore; recover confirmed live |
| Newer pending B fails while earlier D0 A committed | Lobby live=A; B had no D0 commit | Do not restore old; recover to A if local D ready, else `RECONCILING`; a newer B success later supersedes A |
| Compensation after D0 commit | Conditional release only if live still this admission/generation | If a newer live exists, release is a no-op; never clear newer live; no old restore |
| After local D/actor switch, before old close | DB/Lobby new live; actor new generation | Old commands rejected by tuple; delayed close harmless |
| After old close / steady state | Both durable | New owner survives; old epoch cleanup only on failover |

The “after WELCOME, before Lobby Confirm” window is why WELCOME cannot be the authority grant:
the client may have WELCOME but the actor is `HANDOVER_PENDING`, so it cannot command. The
“after Lobby Confirm, before local D” window is the compensation window; the durable Lobby live
row is the authority, and a local failure must conditionally release it or fault the seat closed.
A committed or unknown D0 never restores old; a newer pending's failure recovers to the durable
`lobbyLive` candidate or has no command authority. Only after D does the new tuple command; the
old tuple is stale from the pending phase onward. A post-hoc socket close is never the fence.

**Existing LIVE and cross-process fencing.** `lobby.live_admission` stores `table_owner_epoch`
and `lease_expires_at`. A new TableService process with a different owner epoch is `LEASE_HELD`
while the old live lease is unexpired; after expiry it may `ConfirmAdmission` a higher
`ticket_generation`. An old ticket cannot reclaim the seat because `ticket_generation` is stale.
The new process also releases local `RESERVED|COMMITTED|LIVE` rows from older owner epochs as
described above. A Lobby restart preserves `latest_ticket_generation`, `admission_generation`, and
the live row; the same fencing decisions reapply.

**Retention cleanup.** The only safe delete predicate is:

```sql
DELETE FROM table_store.table_ticket_admission
 WHERE state IN ('RELEASED','SUPERSEDED')
   AND released_at IS NOT NULL
   AND released_at <= clock_timestamp() - @cleanup_grace
   AND retain_until <= clock_timestamp();
```

`RESERVED`, `COMMITTED`, and `LIVE` rows are **never** deleted by age, even when the ticket TTL
has passed: the socket can live longer than the ticket. Their capacity is released only by an
explicit terminal transition (`Release`, `SUPERSEDED`, or epoch cleanup). A live row that crosses
`retain_until` stays in place; a new admission for the same seat/spectator identity must be
refused, not enabled by deleting the live row. Once the row becomes terminal, cleanup waits for
the grace and `retain_until`; the jti fence is then backed by the Lobby consumption row and the
JWS `exp`.

Cleanup must not race a handover. Run it per room while holding the same `table_store.table_lease`
row `FOR UPDATE` used by `Reserve`/`EvictOnWelcome`; do **not** delete while the seat is
`HANDOVER_PENDING`. A global cleaner may use `FOR UPDATE SKIP LOCKED` only if it re-checks state,
`released_at`, `retain_until`, and the current room epoch after locking. Terminal rows are never a
handover target, but the room lock keeps the observation and delete in one serial order. See
§7.2 for the live-crossing-retain and cleanup/handover probes.

**Reservation sweeper.** A same-epoch `RESERVED`/`COMMITTED` row whose
`reservation_expires_at <= now` and whose session is gone is a leaked slot. The room owner sweeps
it to `RELEASED` under the lease row lock. If the sweeper cannot run, the slot stays unavailable
(fail closed), not over-allocated. `reservation_expires_at` should be short (for example 15 s,
matching the Lobby reservation TTL) and is not a substitute for the lease fence.

### 5.7 Authentication and schema boundaries

**User authentication (facts).** The only user credential is the Lobby-issued ES256 JWS. The
TableService validator holds public keys only; `Program.cs:15` and `:152-200` refuse to start a
table host with no validation keys; issuer/audience/version/TTL/claim checks are strict. The
Lobby signer holds the private key and publishes JWKS with rotation overlap.

**Internal service authentication (proposed).** The consume endpoint is not a user endpoint:

- mTLS client certificate from TableService to Lobby, with a dedicated service identity; or an
  HMAC over method + path + body hash + timestamp + nonce with a rotated service secret.
- Separate listener/port or network policy; no public ingress. A public consume route would let
  anyone who holds a ticket burn it and would add a denial-of-service surface.
- The consume request carries only `ticket`, `roomId`, `requestId`, `tableInstanceId`,
  `sessionId`. Account, role, seat, and assignment are derived from the signed ticket; the service
  must not accept them as authoritative request fields. `ConfirmAdmission` carries only the
  already-authenticated `jti`, `ticket_generation`, `lobby_consumption_id`, `admission_id`,
  `session_id`, `owner_epoch`, and `request_id`.
- Service-request replay protection: timestamp window + request-id idempotency. The response is
  bound to the request; TableService compares `jti`, account, room, assignment, role, seat,
  `ticket_generation`, and expiry against the locally validated JWS and rejects any mismatch.

**Database roles (proposed).** Keep the existing separation:

- Lobby runtime role: DML on `lobby.*` only.
- Table runtime role: DML on `table_store.*` only.
- O3 has no cross-schema grants. O4 would need a narrowly granted `SECURITY DEFINER` function and
  is a separate ownership decision.

**Logging.** Log a hashed/truncated `jti`, `request_id`, room, role, seat, and a stable status.
Never log the compact ticket, private key, session token, or full account identifier in
operational logs. Current `LogRedacted` usage is a good pattern; extend it with the correlation
id.

### 5.8 Refusal mapping (proposed)

| Condition | Internal result | Client-visible default |
|---|---|---|
| Bad signature, unknown `kid`, version/TTL/claim failure | `TICKET_INVALID` | 4401 `INVALID_TOKEN` |
| Already consumed | `TICKET_ALREADY_CONSUMED` | 4401 `INVALID_TOKEN` |
| Superseded | `TICKET_SUPERSEDED` | 4401 `INVALID_TOKEN` |
| Expired | `TICKET_EXPIRED` | 4401 `INVALID_TOKEN` |
| Assignment not active / expired / account already seated | `ASSIGNMENT_NOT_ACTIVE` / `ACCOUNT_ALREADY_SEATED` | 4401 or a reviewed stable code |
| Player seat held by another account | `SEAT_ALREADY_CONFIRMED` | 4401 or `ROOM_FULL` (product/protocol decision) |
| Spectator cap | `SPECTATOR_CAPACITY_REACHED` | 4401 or `ROOM_FULL` (product/protocol decision) |
| Lobby unreachable before the request is sent | `LOBBY_UNAVAILABLE` | 4503 `SERVER_BUSY`; retryable, no request-side effect |
| Lobby 4xx definitive rejection | `TICKET_*` / `ASSIGNMENT_*` | 4401; transaction rolled back, no side effect |
| Lobby timeout / reset / 5xx after send | `LOBBY_COMMIT_UNKNOWN` | **Unknown**, not “no side effect”: no WELCOME; retry the same `request_id`; if still unknown/unavailable, fail closed and require a fresh ticket |
| Lobby committed but response lost | `TICKET_CONSUMED_REPLAYED` | Same `request_id`/tuple returns the original result; proceed to local Reserve |
| Lobby attempt still `IN_PROGRESS` | `TICKET_CONSUME_IN_PROGRESS` | 4503/retryable; do not WELCOME; retry the same request id |
| `ConfirmAdmission`: older ticket generation | `STALE_GENERATION` | 4401/4403; local row `SUPERSEDED`; never demote the newer live row |
| `ConfirmAdmission`: newer live generation | `NEWER_LIVE` | 4403; close this connection |
| `ConfirmAdmission`: different live owner epoch, lease unexpired | `LEASE_HELD` | 4503; fail closed until lease expiry/recovery |
| `ConfirmAdmission`: no consumption record | `NOT_CONSUMED` | internal inconsistency; release local reservation, no WELCOME |
| `ConfirmAdmission` timeout/unknown | `ADMISSION_UNKNOWN` | `RECONCILING`; no old commands; retry/query same `request_id`; late result conditional |
| Table store unavailable after Lobby commit | `ADMISSION_STORE_UNAVAILABLE` | 4503; no WELCOME; ticket spent |
| Lost room lease / actor not serving | `LEASE_LOST` | 4503 `LEASE_LOST` |
| New WELCOME lost to a newer handover | `SELF_SUPERSEDED` | 4403 `DISPLACED_BY_NEW_CONNECTION` |
| Old owner displaced by new WELCOME | `DISPLACED_OTHER` | 4403 to the old session |

Defaulting ticket refusals to `4401` avoids leaking whether a `jti` exists or is superseded;
capacity may need a distinct code for client UX. This is a protocol/product decision, not a
unilateral change to the frozen error set.

**Unknown commit is not “no side effect.”** Only a request that provably never reached Lobby
(connection failed before send) or a definitive 4xx rollback can be described as no side effect.
A timeout, reset, or 5xx after the request was sent is `LOBBY_COMMIT_UNKNOWN`. TableService must
not send WELCOME and must resolve it with the same `request_id`: `COMPLETED` returns the original
result and may proceed; `IN_PROGRESS` retries later; `FAILED`/absent may retry; an unresolved or
unavailable lookup fails closed and requires a fresh ticket. The same rule applies to a
TableService local-store timeout after Lobby commit: the ticket may be spent, so the outcome is
unknown until the local row/lease is re-read.

---

## 6. Exact implementation file scopes (proposed; nothing below was changed)

### 6.1 LobbyService

| File | Required change |
|---|---|
| `dotnet/src/Mahjong.LobbyService/Migrations/002_ticket_consumption_authority.sql` **(new)** | Add `ticket_generation`/`latest_ticket_generation`/`admission_generation`, `lobby.live_admission` (with `table_owner_epoch`/`lease_expires_at`), **`lobby.table_ticket_d0_request` tombstone + `lobby.table_ticket_d0_result` immutable result table** (`request_id` PK, binding, `COMMITTED|CANCELLED|REJECTED`, `d0_deadline`, retention), **`d0_deadline`/`cancelled_at`/`d0_retain_until` plus full-binding `terminal_state`/result columns on the consumption row**, `request_id`, `table_instance`, `session_id` (and optional `consumption_id`) to the consume table; unique `request_id`; retention indexes; backfill existing ticket/assignment generations and sequence above max. Add attempt table for `IN_PROGRESS|COMPLETED|FAILED`. Keep `jti` PK. |
| `dotnet/src/Mahjong.LobbyService/LobbyApplication.Tickets.cs` | Idempotent `ConsumeTableTicketAsync` overload carrying `requestId`/instance/session; exact-replay return; attempt-state lookup **before** `consumed_at` rejection; **`ConfirmAdmissionAsync`** and **`CancelAdmissionAsync`** with shared room/assignment/live/request lock, binding validation, atomic terminal tombstone, `RELEASE`/`ALREADY_COMMITTED` handling; inconsistent-row fail-closed. |
| `dotnet/src/Mahjong.LobbyService/LobbyHttp.cs` | Internal `POST /internal/v1/table-tickets/consume`, `confirm`, `cancel`, and request lookup/retry; same `request_id`; service authentication; no public route; request-size limit; redacted errors. |
| `dotnet/src/Mahjong.LobbyService/LobbyModels.cs` | `TableTicketClaims`/`IssuedTableTicket` gain `ticket_generation`; consume/confirm/cancel request/response and terminal-result records; no user-controlled account/role/seat. |
| `dotnet/src/Mahjong.LobbyService/TableTicketSigner.cs` | Sign/validate the proposed `ticket_generation` claim (issuance sequence); keep the claim allowlist strict. |
| `dotnet/src/Mahjong.LobbyService/PostgresLobbyStore.cs` | Insert/read generation/live/request/attempt columns; lock assignment/live/request; find by exact request id; terminal tombstone insert/read; constraint-name translation; retention cleanup. |
| `dotnet/src/Mahjong.LobbyService/LobbyOptions.cs` | Internal endpoint/service-auth settings; consumption retention; timeout/limits; cleanup grace. |
| `dotnet/src/Mahjong.LobbyService/LobbyMaintenanceWorker.cs` | Bounded expired-consumption/attempt cleanup; never delete an attempt before its completed result is no longer needed. |
| `dotnet/src/Mahjong.LobbyService/README.md` | Replace “no public route / not integrated” with the explicit internal boundary, or keep it if O3 is not approved. |

### 6.2 TableService

| File | Required change |
|---|---|
| `dotnet/src/Mahjong.TableService/ITicketAdmissionStore.cs` **(new)** | Async durable port: `ReserveAsync`, `CommitAsync`, `ConfirmAdmissionAsync`, `EvictOnWelcomeAsync`, `RollbackAsync`, `ReleaseAsync`, `TryGetSeatOwnerAsync`; D0 outbox `MarkSendingAsync`, `MarkInFlightAsync`, `RecordD0ResultAsync`, `LoadUnresolvedD0Async`; stores `ticket_generation`/`lobby_admission_generation`; local `admission_seq` is control-only. |
| `dotnet/src/Mahjong.TableService/PostgresTicketAdmissionStore.cs` **(new)** | Adapter from the port to `Mahjong.Infrastructure` methods; maps DB outcomes to the stable outcomes. |
| `dotnet/src/Mahjong.TableService/LobbyTicketConsumer.cs` **(new)** | Internal HTTP client: mTLS/HMAC, timeout, bounded retry with the same `request_id`, response binding, `consume`/`confirm`/`release`, circuit breaker, fail-closed mapping. |
| `dotnet/src/Mahjong.TableService/TableSessionHandler.cs` | Replace sync `Consume`/`CommitAdmission`/`EvictOnWelcome`/`Release` calls with the async port; preserve the order (validate → host → consume → subscribe → commit → WELCOME → Lobby confirm → local D/authority switch → teardown); add `ticket_generation`, `request_id`, correlation id, durable `lobbyLive`/`d0Attempts`, `HANDOVER_PENDING`/`RECONCILING`, conditional compensation and restart recovery. |
| `dotnet/src/Mahjong.TableService/TableCommandMapper.cs` | Add `ticket_generation`, `lobby_admission_generation`, `admission_id`, `admission_seq`, `owner_epoch` to `TableConnectionIdentity`; reject stale tuple on command mapping; keep scope/control epoch checks. |
| `dotnet/src/Mahjong.TableService/TableTicketValidator.cs` | Add the signed `ticket_generation` claim to the strict allowlist and `ValidatedTableTicket`; reject missing/duplicate/invalid generation. |
| `dotnet/src/Mahjong.TableService/TableSeatAuthority.cs` **(new)** | Per-identity authority state with `current`, durable `lobbyLive` floor, `d0Attempts`, `pending`, `RECONCILING`/`FAULTED`; `Begin/D0Result/Complete/Fail`, conditional recovery, unknown/late D0 handling; old failure cannot overwrite newer pending/current or committed/unknown D0. |
| `dotnet/src/Mahjong.TableService/TableSessionDirectory.cs`, `TableRoomHost.cs`, `TableWireStreaming.cs` | Route seat-private streams by authority generation; stop old-generation routing at the switch; never rely on socket close for authority. |
| `dotnet/src/Mahjong.TableService/TableServiceWebHost.cs` | Register the durable port and Lobby consumer in Postgres mode; refuse to start a durable/remote deployment without them; keep memory mode explicitly test/dev-only. |
| `dotnet/src/Mahjong.TableService/TableServiceOptions.cs` | Lobby internal endpoint, service-auth reference, connect/response timeouts, retry budget, admission TTL, cleanup grace, require-durable-admission flag. |
| `dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs` | Either (a) keep the in-memory implementation only for tests/dev and document it as non-durable, or (b) retire it behind the new port. Do not leave it as the Postgres-mode registration. |

### 6.3 Infrastructure / schema

| File | Required change |
|---|---|
| `dotnet/src/Mahjong.Infrastructure/Migrations/002_ticket_admission.sql` **(new)** | `table_store.table_ticket_admission` with `request_id`, `ticket_generation`, `lobby_consumption_id`, `lobby_admission_generation`, local `admission_seq`, explicit seat NULL check, terminal-only cleanup index; create `ticket_admission_seq`; add `table_store.table_ticket_d0_outbox` with state `PENDING_SEND|NOT_SENT|SENDING|IN_FLIGHT|COMMITTED|REJECTED|CANCELLED|UNKNOWN`; constraints/indexes from §5.4. |
| `dotnet/src/Mahjong.Infrastructure/PostgresTableMigrator.cs` | Register migration version 2; extend `IsSchemaReady` to require the admission table/sequence/indexes; fail startup if missing. |
| `dotnet/src/Mahjong.Infrastructure/PostgresTableStore.TicketAdmissions.cs` **(new partial)** | Reserve/commit/evict/rollback/release/owner queries in one `NpgsqlTransaction`; room lease `FOR UPDATE`; owner/ticket/lobby generation checks; D0 outbox insert-before-send, `NOT_SENT`/`CANCELLED` terminal transitions, result update, unresolved recovery; unique-violation mapping; terminal-only cleanup. |
| `dotnet/src/Mahjong.Infrastructure/PostgresTableStore.Ports.cs` | `AcquireAsync` must acquire/refresh the lease and release older-epoch admissions in one transaction (or a data-modifying CTE); `RenewAsync` must keep the same epoch. |
| `dotnet/src/Mahjong.Infrastructure/TableTicketAdmissionContracts.cs` **(new)** | Records/enums shared by the store and TableService adapter; no Npgsql types in the public contract. |
| `dotnet/src/Mahjong.Infrastructure/Mahjong.Infrastructure.csproj` | Embed the new migration resource; no new package. |

### 6.4 Tests (must be added; none were added here)

| File | Coverage |
|---|---|
| `dotnet/tests/Mahjong.TableService.Tests/PostgresTicketAdmissionTests.cs` **(new)** | Real isolated PG: seat NULL/out-of-range/spectator rejection; concurrent same-`jti`; same seat cross-account; same-account handover + reverse WELCOME; A/B delayed-B freshness; Lobby Confirm/Release; spectator cap; release/reinsert; epoch cleanup; live row crossing `retain_until`; cleanup/handover concurrency; crash/restart; rollback; stale lease. |
| `dotnet/tests/Mahjong.Infrastructure.Tests/PostgresTicketAdmissionStoreTests.cs` **(new or equivalent existing project)** | SQL transaction boundaries, unique indexes, `request_id`/`jti` binding, state transitions, terminal-only retention cleanup, epoch fence, `admission_seq` ordering. |
| `dotnet/tests/Mahjong.LobbyService.Tests/PostgresLobbyStoreTests.cs` | Extend existing PG tests for consume/supersede/retry/idempotent request id/attempt state/unknown commit/rollback; `ConfirmAdmission` generation/live/owner-lease checks; `ReleaseAdmission` compensation. |
| `dotnet/tests/Mahjong.LobbyService.Tests/TableTicketTests.cs` | In-memory/Lobby semantics and the new request binding. |
| `dotnet/tests/Mahjong.TableService.Tests/TableTicketRegistryLifecycleTests.cs` | Keep the existing single-process matrix as the in-memory fake contract; add async port cases; do not weaken existing assertions. |
| `dotnet/tests/Mahjong.TableService.Tests/AdmissionTicketTests.cs` | Real Kestrel admission with a fake Lobby consumer; duplicate/superseded/capacity/lease failures; no side effects on refusal. |
| `dotnet/tests/Mahjong.TableService.Tests/SessionFlowTests.cs`, `RuntimeDecisionRuntimeTests.cs` | WELCOME failure, reverse WELCOME, `HANDOVER_PENDING`, stale-generation command rejection, disconnect fence, post-WELCOME DB failure. |
| `dotnet/tests/Mahjong.LobbyService.Tests/InternalTicketConsumptionApiTests.cs` **(new)** | Service auth required; public call rejected; wrong room/claim; replay; `IN_PROGRESS`/unknown commit; `ConfirmAdmission` committed/rejected/unknown/late; conditional `ReleaseAdmission`; body limits; no ticket in logs. |
| `dotnet/tests/Mahjong.Architecture.Tests/...` | TableService still has no project reference to Lobby; no `lobby.*` DML from the table runtime role; no new public wire contract. |

**Do not modify:** `Mahjong.sln`, the unified verifier, Unity `Resources`/Burst, machine trust,
V4 production code/tests, or other worktrees. Do not add the migration to the solution by hand
outside the caller's integration process.

### 6.5 Test matrix

| ID | Assertion | Required setup |
|---|---|---|
| T1 | 32 concurrent same-`jti` consume calls → exactly one row | Isolated PG, two connections |
| T2 | Rollback before commit → no consumption row, `consumed_at` null | Isolated PG |
| T3 | `FOR UPDATE` second consumer blocks then sees `consumed_at` | Isolated PG, timed |
| T4 | Issue-first then consume-old → `TICKET_SUPERSEDED` | Lobby store |
| T5 | Consume-first then issue-new → old spent, new active | Lobby store |
| T6 | Crash after commit → new process rejects same `jti` | Real postmaster restart |
| T7 | Two TableService hosts, same seat different accounts → one live | Real PG, two hosts |
| T8 | Two TableService hosts, spectator cap 1 → one live | Real PG, two hosts |
| T9 | Same account fresh ticket handover → old 4403 only after new WELCOME + D commit | Real Kestrel |
| T10 | WELCOME failure → old owner preserved, both jtis spent | Gated WebSocket |
| T11 | Disconnect → capacity freed, old `jti` still rejected, fresh ticket accepted | Real Kestrel |
| T12 | Lobby pre-send failure/4xx → no local row, no WELCOME; timeout after send follows T22, not “no side effect” | Fake Lobby |
| T13 | Local DB failure after Lobby consume → no WELCOME, ticket spent, redacted log | Fault injection |
| T14 | Response-binding mismatch → reject | Unit |
| T15 | Internal consume endpoint without service auth → 401/404, no consumption | Real Lobby Kestrel |
| T16 | Epoch change releases old live rows and frees seat/spectator count | Real PG |
| T17 | Final DDL rejects `PLAYER NULL`, `PLAYER -1`, `PLAYER 4`, `SPECTATOR 0`; accepts valid seats | Real PG, final DDL |
| T18 | Live row crossing `retain_until` is not deleted; same seat remains blocked; after release+grace it may be deleted | Real PG, long-lived row |
| T19 | Cleanup does not delete `RESERVED`/`COMMITTED`/`LIVE`; cleanup vs handover serializes under room lock | Real PG, concurrent |
| T20 | Older `COMMITTED` cannot commit/WELCOME over newer active row; reverse/concurrent WELCOME leaves newest live | Real PG, `admission_seq` |
| T21 | WELCOME before D does not grant command authority; stale `admission_seq` commands are rejected after D; failed D restores old | Real Kestrel/actor |
| T22 | Lobby timeout/5xx after send resolves by same request id; `IN_PROGRESS`/`COMPLETED`/`FAILED`/unknown behavior | Fake Lobby + real store |
| T23 | Same `request_id` different `jti` conflicts; same `jti` different session is duplicate; exact same tuple replays | Real PG |
| T24 | A/B inversion: old consumed first, new issue/consume/confirm live, old delayed local seq larger → `STALE_GENERATION`, old `SUPERSEDED`, new `LIVE` | Real freshness model/real Lobby PG |
| T25 | Concurrent old/new Confirm: old-first and new-first both leave only new `LIVE`; old `SUPERSEDED` | Real PG, concurrent |
| T26 | Old consume vs new issuance both orders: old either `CONSUMED` then stale or `TICKET_SUPERSEDED`; only new `LIVE` | Real PG, concurrent |
| T27 | Existing old `LIVE`, new ticket confirms: old `SUPERSEDED`, new `LIVE`; old re-confirm `STALE_GENERATION` | Real Lobby PG |
| T28 | Existing live lease: different owner epoch `LEASE_HELD`; after expiry takeover; old ticket still stale | Real Lobby PG |
| T29 | Confirm transaction rollback leaves prior live generation; local abandoned row released | Real PG |
| T30 | Overlapping `HANDOVER_PENDING`: A fail/B complete/B complete/A fail/B fail/C complete; old failure cannot overwrite newer pending/current | Actor model/executable test |
| T31 | Lobby restart retains latest/ ticket/live generations; old ticket rejected after restart | Real PG restart |
| T32 | A D0 committed, B replaces pending/fails before D0 → old cannot command; recover A when local D ready | Actor+D0 model |
| T33 | A D0 unknown, B replaces/fails → old cannot command; late A commit is retained; late A after newer B rejected | Actor+D0 model |
| T34 | A D0 committed, B D0 committed, A local D fails/compensates → newer B live not cleared | Actor+D0 model |
| T35 | Restart with Lobby live A but local A missing → no old authority; with local A present → A | Actor+D0 model |
| T36 | D0 committed/unknown then failure → no old restore; only conditional recovery/compensation | Actor+D0 model |
| T37 | D0 outbox written before send; crash after write/ before response loads as unresolved; old cannot command | TableService crash/restart |
| T38 | Sent but Lobby not committed, response lost; B fails → old blocked until a durable cancel tombstone (not a query absence) | Actor+D0 model |
| T39 | Sent, Lobby committed, response queued; B fails → old blocked; late A response recovers A | Actor+D0 model |
| T40 | Newer B confirmed while older A in-flight → B current; late A cannot overwrite | Actor+D0 model |
| T41 | Provably unsent vs uncertain send: only provably unsent/definitive reject can restore old | Actor+D0 model |
| T42 | Restart with unresolved D0 and Lobby live A/local missing → no old; local A present → A | Actor+D0 model |
| T43 | Confirm commits first; Cancel returns `ALREADY_COMMITTED`; old not restored | Lobby PG/model |
| T44 | Cancel tombstone commits first; delayed Confirm returns `CANCELLED`; old only after tombstone | Lobby PG/model |
| T45 | Query/lookup `ABSENT` is non-terminal; Cancel creates tombstone; old not restored on absence | Lobby PG/model |
| T46 | Duplicate Confirm/Cancel and restart preserve terminal result; binding mismatch conflicts | Lobby PG/model |
| T47 | Outbox DDL accepts persistent `NOT_SENT`; old CHECK without it rejects; invalid state rejected | Real PG |
| T48 | Outbox `PENDING_SEND/SENDING/IN_FLIGHT` survives restart then maps to `UNKNOWN`; `NOT_SENT`/`COMMITTED` persist | Real PG crash/restart |
| T49 | Cancel response lost; retry returns `REPLAYED_CANCELLED`; old remains blocked until definitive reply | Lobby model |
| T50 | Cancel tombstone must not clear a newer B live; no old restore | Lobby PG/model |
| T51 | Tombstone cleanup after deadline; late Confirm is `CANCELLED`/`DEADLINE_EXPIRED`, never `COMMITTED` | Lobby PG/model |
| T52 | Cleanup before deadline deletes nothing; Confirm sees tombstone | Lobby PG/model |
| T53 | Deadline boundary: before commits; exactly at/after rejects; DB time is authoritative | Lobby PG/model |
| T54 | Confirm/cleanup interleaving after deadline; missing request row cannot commit | Lobby PG/model |
| T55 | Consumption record missing → `AUTHORITATIVE_RECORD_MISSING`; no commit; restart retains deadline/cancel marker | Lobby PG/model |
| T56 | Confirm before deadline then retry at/after → `REPLAYED`, not `DEADLINE_EXPIRED`; no duplicate effect | Lobby PG/model |
| T57 | Cancel after committed, even after deadline → `ALREADY_COMMITTED`; old not restored | Lobby PG/model |
| T58 | First Confirm at/after deadline with no terminal → `DEADLINE_EXPIRED`; late REJECTED row allowed with past `d0_deadline` | Lobby PG/model |
| T59 | Committed request row cleaned/restart → consumption terminal marker replays `COMMITTED` | Lobby PG/model |
| T60 | Binding mismatch on committed request → `REQUEST_ID_CONFLICT`; no duplicate/live change | Lobby PG/model |
| T61 | COMMITTED then request/result cleanup, consumption marker remains → `REPLAYED`/`ALREADY_COMMITTED`; old false | Lobby PG/model |
| T62 | Result row deleted but consumption marker remains → replay original result; no deadline rejection | Lobby PG/model |
| T63 | Result+request+consumption all cleaned → `RESULT_EXPIRED`, never `DEADLINE_EXPIRED`/new commit; actor no old | Lobby PG/model |
| T64 | Cleanup/Confirm/Cancel concurrency: same lock; no delete/reopen window; restart preserves marker | Lobby PG/model |
| T65 | Consumption marker missing/NULL while terminal evidence exists → `TERMINAL_EVIDENCE_INCONSISTENT`; fail closed | Lobby PG/model |
| T66 | Generic path after result/request cleanup must not differ from unified path; marker replays `REPLAYED` | Node order model |
| T67 | Result exists but marker NULL, or result/marker states conflict → `TERMINAL_EVIDENCE_INCONSISTENT`; no deadline/commit | Lobby PG/model |

Suggested commands once implemented (not run here):

```powershell
dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --no-incremental -nr:false --nologo
dotnet dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll `
  -noLogo -reporter default -result-xml <evidence>\tableservice.xml

# Isolated PG only; never point this at a shared/production database.
$env:MAHJONG_TICKET_TEST_POSTGRES = 'Host=127.0.0.1;Port=<owned>;Database=<owned>;Username=<owned>'
dotnet dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll `
  -noLogo -reporter default -result-xml <evidence>\ticket-pg.xml
```

The tests must report real `Total/Passed/Failed/Skipped/NotRun` and native exit. Do not count
this design report or the SQL probes as a test pass.

---

## 7. Isolated probes actually run (PostgreSQL + Node models)

### 7.1 Environment

| Item | Value |
|---|---|
| PostgreSQL | **16.10**, Windows x64, official EDB binaries |
| Archive SHA-256 | `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C` |
| Cluster | `build/ticket-pr20-c0dd5f8/pgdata` (ignored, owned by this task), `127.0.0.1:55471` |
| Database | `ticket_probe` |
| Auth | one-time loopback `trust`; no existing/user/shared database contacted |
| Migrations | candidate `001_lobby.sql` and `001_table_store.sql`; native exits **0**, **0**; 21 tables |
| Candidate runtime | not built or executed |
| Cleanup | cluster stopped and deleted; archive and binaries deleted; logs retained in the ignored directory |
| Round-2 cluster | `build/ticket-pr20-c0dd5f8/pgdata2`, `127.0.0.1:55472`, DB `ticket_probe2`, same official PostgreSQL 16.10 binaries; candidate `001_table_store.sql` applied (13 tables); stopped and deleted after probes |
| Round-3 cluster | `build/ticket-pr20-c0dd5f8/pgdata3`, `127.0.0.1:55473`, DB `ticket_probe3`, same official PostgreSQL 16.10 binaries; **simplified Lobby freshness model**, not candidate schema/full DDL; stopped and deleted after probes |
| Round-4 model | Node `v24.14.1`; `probe4-actor-d0-model.mjs`; no database, no production code |
| Round-5 model | Node `v24.14.1`; `probe5-d0-inflight-model.mjs` + caller `caller-inflight-probe.cjs`; no database, no production code |
| Round-6 model/DDL | Node `v24.14.1`; `probe6-cancel-race-model.mjs` + caller `caller-query-race.cjs`; PostgreSQL 16.10 in `pgdata7`/port 55474 for outbox DDL persistence; clusters stopped/deleted |
| Round-7 model/DDL | Node `v24.14.1`; `probe7-deadline-model.mjs` + caller `caller-tombstone-cleanup.cjs`; PostgreSQL 16.10 in `pgdata8`/port 55475 for deadline/cancel/cleanup functions; clusters stopped/deleted |
| Round-8 model/DDL | Node `v24.14.1`; `probe8-deadline-idempotency-model.mjs` + caller `caller-review-deadline.mjs`; PostgreSQL 16.10 in `pgdata9`/`pgdata10` ports 55476/55477 for terminal-result/deadline DDL; clusters stopped/deleted |
| Round-9 model/DDL | Node `v24.14.1`; `probe9-cleanup-evidence-model.mjs`; PostgreSQL 16.10 in `pgdata11`/port 55478 for result/consumption cleanup and lock probes; cluster stopped/deleted |
| Round-10 order model/DDL | Node `v24.14.1`; `probe10-order-model.mjs`; PostgreSQL 16.10 in `pgdata12`/port 55479 for final marker/conflict/cleanup/lock probes; cluster stopped/deleted |
| Round-11 strict evidence | Node `v24.14.1`; `probe11-three-evidence-model.mjs`; PostgreSQL 16.10 in `pgdata13`/port 55480 for strict three-witness SQL assertions and lock probe; cluster stopped/deleted |
| Round-12 full binding | PostgreSQL 16.10 in `pgdata14`/port 55481 for `jti`/account/room/assignment/session/owner_epoch assertions and concurrent retry; cluster stopped/deleted |
| Round-13 NULL binding | PostgreSQL 16.10 in `pgdata15`/port 55482 for 27 NULL/missing binding assertions and concurrent retry; cluster stopped/deleted |

**Probe fidelity (important).** The first round and the second round do not prove the same thing:

- **P1–P6** exercised the candidate's actual `lobby`/`table_store` schema. **P7–P11** used a
  simplified throw-away admission table with only the constraints then under discussion; those
  rows did **not** have `request_id`, `admission_seq`, or the terminal-only cleanup predicate.
- **P13–P22** use the **full final recommended DDL** in `probe2-final-ddl.sql`: sequence,
  `request_id` unique, explicit seat NULL check, `ck_admission_live`, `ck_admission_released`,
  partial live-seat/spectator indexes, and cleanup index. The probe table is named
  `table_store.probe_admission`; the production proposal is `table_store.table_ticket_admission`
  with the same columns/constraints. They prove SQL constraint/predicate behavior on that DDL, not
  candidate C# code, the actor fence, HTTP/Lobby wiring, or WSS.
- **P23–P33** use a **simplified Lobby freshness model** (`probe3-schema.sql`): issuance,
  consumption, conditional confirm, live row, owner epoch/lease, and local admissions. It is
  neither the candidate schema nor the full production DDL; it isolates the
  issuance/consume/confirm linearization and the A/B reversal. The actor pending model is a
  separate Node executable model, not C#.
- **P34** is a Node executable model of the actor + Lobby D0 coordination, not C# and not a
  network/DB test. It covers committed/unknown/late D0, newer pending failure, compensation, and
  restart; it cannot prove the actual actor integration or Lobby persistence.
- **P35/P36** are Node models split into send/Lobby-commit/response-delivery. They prove the
  in-flight fence and reproduce the caller's failing interleaving; they do not prove outbox
  durability in PostgreSQL, C# mailbox ordering, or network send semantics.
- **P37/P38** are a Node cancel/confirm race model and the caller's query-race reproducer. They
  prove the tombstone ordering logic, not the actual Lobby SQL transaction.
- **P39** is a real PostgreSQL probe of the outbox CHECK and restart states. It proves DDL
  persistence/recovery only, not the Lobby cancel transaction or C# code.
- **P40/P41** are the caller tombstone-cleanup reproducer and a Node deadline/cleanup model. They
  prove the logic of `d0_deadline`/`cancelled_at` ordering, not the real SQL transaction.
- **P42** is a real PostgreSQL function probe of deadline/cancel/cleanup, including DB-time and
  missing-record cases. It proves SQL predicate/lock behavior, not RPC/C# integration.
- **P43/P44** are the caller deadline-review reproducer and a Node terminal-result/idempotency
  model. They prove the result-precedence logic, not the real SQL transaction.
- **P45** is a real PostgreSQL probe of terminal-result precedence, late-REJECTED DDL, cleanup,
  binding conflict, and missing evidence. It proves SQL behavior only.
- **P12** is the old-predicate control table, included only to reproduce the NULL acceptance.
- The simplified probes are still valid for the specific predicate they tested, but must not be
  cited as proof of the full recommended DDL. The full-DDL probes must not be cited as proof of
  the C# implementation.

The proposed-admission probes used throw-away tables (`probe_room_lock`,
`probe_ticket_admission`/`probe_admission`) created only in the owned probe databases. They are
not candidate code and were not committed. The caller's Node/SQLite reproducer was not re-run here; the same old-predicate acceptance was reproduced in PostgreSQL, and the final DDL was tested directly.

**Probe commands (exact shape used).** The values in angle brackets were the owned paths/ports
for this run; no command was pointed at a shared database.

```powershell
$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg   = "$base\pg\pgsql\bin"
$port = 55471

# Extract only the official PG 16.10 Windows zip; SHA-256 checked first.
New-Item -ItemType Directory -Force "$base\pg" | Out-Null
tar -xf 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip' -C "$base\pg"
& "$pg\initdb.exe" -D "$base\pgdata" -U mahjong_pg -A trust -E UTF8 --locale=C
$p = Start-Process -FilePath "$pg\pg_ctl.exe" -ArgumentList @('-D',"$base\pgdata",'-l',"$base\pg.log",'-o',"`"-p $port -h 127.0.0.1`"",'start') `
  -RedirectStandardOutput "$base\pg-start.out" -RedirectStandardError "$base\pg-start.err" -PassThru -WindowStyle Hidden
$p.WaitForExit()
& "$pg\createdb.exe" -h 127.0.0.1 -p $port -U mahjong_pg ticket_probe

# Candidate schemas, then the probe seed.
& "$pg\psql.exe" -h 127.0.0.1 -p $port -U mahjong_pg -d ticket_probe -v ON_ERROR_STOP=1 `
  -f "$base\dotnet\src\Mahjong.LobbyService\Migrations\001_lobby.sql"
& "$pg\psql.exe" -h 127.0.0.1 -p $port -U mahjong_pg -d ticket_probe -v ON_ERROR_STOP=1 `
  -f "$base\dotnet\src\Mahjong.Infrastructure\Migrations\001_table_store.sql"
& "$pg\psql.exe" -h 127.0.0.1 -p $port -U mahjong_pg -d ticket_probe -f "$base\seed.sql"

# Concurrency pair: same jti; the runner captures both native exits and both streams.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg\psql.exe" -Port $port -Database ticket_probe `
  -ScriptA "$base\probe-pk-a.sql" -ScriptB "$base\probe-pk-b.sql" -OutPrefix "$base\pk" -DelayMs 300

# Same pattern for lock/rollback/supersession and for the proposed admission probes.
# One controlled crash cycle:
& "$pg\psql.exe" -h 127.0.0.1 -p $port -U mahjong_pg -d ticket_probe -f "$base\probe-crash-commit.sql"
& "$pg\pg_ctl.exe" -D "$base\pgdata" -m immediate stop
$p = Start-Process -FilePath "$pg\pg_ctl.exe" -ArgumentList @('-D',"$base\pgdata",'-l',"$base\pg.log",'-o',"`"-p $port -h 127.0.0.1`"",'start') `
  -RedirectStandardOutput "$base\crash-start.out" -RedirectStandardError "$base\crash-start.err" -PassThru -WindowStyle Hidden
$p.WaitForExit()
& "$pg\psql.exe" -h 127.0.0.1 -p $port -U mahjong_pg -d ticket_probe -Atc `
  "select (select count(*) from lobby.table_ticket_consumption where jti='jti_c')||','||(select (consumed_at is not null)::text from lobby.table_ticket where jti='jti_c');"
```

The `run-concurrent.ps1` harness starts the two `psql` processes with a 300 ms stagger, reads
both streams, and returns `a_exit=...` / `b_exit=...`. The proposed schema probe used
`probe-schema.sql`, then `probe-adm-*.sql` through the same harness. The controlled restart at
00:25:21–00:25:24 is the one quoted below; later lines in `pg.log` are additional owned-cluster
restart cycles from the harness.

`psql` native exit convention observed here: **0** success, **3** script error
(`ON_ERROR_STOP`), **1** command error for the one `-c` duplicate insert.

For the two crash cycles (P6/P11), the `psql`/`pg_ctl` commit/stop exits were not separately
printed because the PowerShell job was moved to the background after starting `postgres`; the
retained stdout records `COMMIT` / `server stopped`, the restart log records `server started`, and
the decisive post-restart query native exits are recorded below. The duplicate-replay command's
native exit is recorded as **1** (psql `-c` error path).

**Round-2 commands (full final DDL).** A second owned cluster was used so the round-1 candidate
schema evidence stayed untouched:

```powershell
$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg2  = "$base\pg2\pgsql\bin"
$port2 = 55472

& "$pg2\initdb.exe" -D "$base\pgdata2" -U mahjong_pg -A trust -E UTF8 --locale=C
$p = Start-Process -FilePath "$pg2\pg_ctl.exe" -ArgumentList @('-D',"$base\pgdata2",'-l',"$base\pg2.log",'-o',"`"-p $port2 -h 127.0.0.1`"",'start') `
  -RedirectStandardOutput "$base\pg2-start.out" -RedirectStandardError "$base\pg2-start.err" -PassThru -WindowStyle Hidden
$p.WaitForExit()
& "$pg2\createdb.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg ticket_probe2
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -v ON_ERROR_STOP=1 `
  -f "$base\dotnet\src\Mahjong.Infrastructure\Migrations\001_table_store.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-final-ddl.sql"

# Seat CHECK: old predicate accepts PLAYER NULL; final DDL rejects NULL/-1/4/SPECTATOR seat.
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-old-seat.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-bad-player-null.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-bad-player-neg.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-bad-player-high.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-bad-spectator-seat.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-valid.sql"

# Cleanup: old retain-only vs corrected terminal-only; long-lived row; release cleanup.
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-cleanup-setup.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-cleanup-old.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-cleanup-setup.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-cleanup-corrected.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-live-overcommit.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-release-cleanup.sql"

# Sequence/freshness and request-id uniqueness.
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-seq-setup.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-old-welcome.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-commit-setup.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-old-commit.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-new-commit.sql"
& "$pg2\psql.exe" -h 127.0.0.1 -p $port2 -U mahjong_pg -d ticket_probe2 -f "$base\probe2-request-dup.sql"

# Cleanup vs handover, and reverse/concurrent WELCOME, use the same run-concurrent harness.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg2\psql.exe" -Port $port2 -Database ticket_probe2 `
  -ScriptA "$base\probe2-handover-cleanup.sql" -ScriptB "$base\probe2-handover-d.sql" `
  -OutPrefix "$base\pg2-handover-cleanupfirst" -DelayMs 300
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg2\psql.exe" -Port $port2 -Database ticket_probe2 `
  -ScriptA "$base\probe2-handover-d.sql" -ScriptB "$base\probe2-handover-cleanup.sql" `
  -OutPrefix "$base\pg2-handover-dfirst" -DelayMs 300
# Repeat the setup between the two handover runs.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg2\psql.exe" -Port $port2 -Database ticket_probe2 `
  -ScriptA "$base\probe2-welcome-a.sql" -ScriptB "$base\probe2-welcome-b.sql" `
  -OutPrefix "$base\pg2-welcome-oldfirst" -DelayMs 300
# Repeat welcome-setup, then swap A/B for the reverse ordering.
```

The round-2 cluster was stopped and deleted after the probes; logs remain. The `run-concurrent`
harness's own exit is **0** when it successfully launches and reports both children; the relevant
native exit is `a_exit`/`b_exit` for the `psql` children.

**Round-3 commands (simplified freshness model).** A third owned cluster isolated the
issuance/consume/confirm model from the round-2 DDL:

```powershell
$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg3  = "$base\pg3\pgsql\bin"
$port3 = 55473

& "$pg3\initdb.exe" -D "$base\pgdata3" -U mahjong_pg -A trust -E UTF8 --locale=C
$p = Start-Process -FilePath "$pg3\pg_ctl.exe" -ArgumentList @('-D',"$base\pgdata3",'-l',"$base\pg3.log",'-o',"`"-p $port3 -h 127.0.0.1`"",'start') `
  -RedirectStandardOutput "$base\pg3-start.out" -RedirectStandardError "$base\pg3-start.err" -PassThru -WindowStyle Hidden
$p.WaitForExit()
& "$pg3\createdb.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg ticket_probe3
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-schema.sql"

# A/B inversion: old consumed first, new issued/consumed/live, old delayed with larger local seq.
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-ab.sql"

# Concurrent old/new confirm, both orders.
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-confirm-setup.sql"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg3\psql.exe" -Port $port3 -Database ticket_probe3 `
  -ScriptA "$base\probe3-confirm-old.sql" -ScriptB "$base\probe3-confirm-new.sql" `
  -OutPrefix "$base\probe3-confirm-oldfirst" -DelayMs 300
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-confirm-setup.sql"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg3\psql.exe" -Port $port3 -Database ticket_probe3 `
  -ScriptA "$base\probe3-confirm-new.sql" -ScriptB "$base\probe3-confirm-old.sql" `
  -OutPrefix "$base\probe3-confirm-newfirst" -DelayMs 300

# Old consume vs new issuance, both orders.
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-consume-issue-setup.sql"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg3\psql.exe" -Port $port3 -Database ticket_probe3 `
  -ScriptA "$base\probe3-consume-old-slow.sql" -ScriptB "$base\probe3-issue-new-slow.sql" `
  -OutPrefix "$base\probe3-ci-oldfirst" -DelayMs 300
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-consume-issue-finish.sql"
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-consume-issue-setup.sql"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base\run-concurrent.ps1" `
  -Psql "$pg3\psql.exe" -Port $port3 -Database ticket_probe3 `
  -ScriptA "$base\probe3-issue-new-slow.sql" -ScriptB "$base\probe3-consume-old-slow.sql" `
  -OutPrefix "$base\probe3-ci-newfirst" -DelayMs 300
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-consume-issue-finish.sql"

# Existing LIVE, owner-epoch/lease fencing, rollback, restart, actor pending model.
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-old-live-new.sql"
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-lease.sql"
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-rollback.sql"
# Stop -m immediate, restart, then query/probe old ticket:
& "$pg3\pg_ctl.exe" -D "$base\pgdata3" -m immediate stop
$p = Start-Process -FilePath "$pg3\pg_ctl.exe" -ArgumentList @('-D',"$base\pgdata3",'-l',"$base\pg3.log",'-o',"`"-p $port3 -h 127.0.0.1`"",'start') `
  -RedirectStandardOutput "$base\pg3-restart-start.out" -RedirectStandardError "$base\pg3-restart-start.err" -PassThru -WindowStyle Hidden
$p.WaitForExit()
& "$pg3\psql.exe" -h 127.0.0.1 -p $port3 -U mahjong_pg -d ticket_probe3 -f "$base\probe3-restart-old.sql"
node "$base\probe3-handover-model.mjs"
node "$base\probe3-freshness-compare.mjs"
```

The round-3 `probe3-restart-old.out` inserts a new local old-ticket reservation and calls confirm;
the query is recorded as exit **0**. The two Node models write PASS/FAIL lines and exit **0** when
all scenarios pass.

**Round-4 model command.** No database:

```powershell
node build/ticket-pr20-c0dd5f8/probe4-actor-d0-model.mjs
# exit 0; S1_reported_interleaving lobbyLive=A:10 actorCurrent=old oldCanCommand=false
# scenarios=49 failures=0
```

**Round-5 in-flight model commands.** No database:

```powershell
node build/ticket-pr20-c0dd5f8/caller-inflight-probe.cjs
# exit 1 (reproduces the reported defect):
# {"lobbyLive":"A","actorFloor":"old","outcome":"RESTORED_CURRENT","oldCanCommand":true}

node build/ticket-pr20-c0dd5f8/probe5-d0-inflight-model.mjs
# exit 0:
# {"id":"F1_reported_inflight","lobbyLive":"A","actorFloor":"old",
#  "phase":"RECONCILING","outcome":"NO_RESTORE_INFLIGHT","oldCanCommand":false}
# scenarios=36 failures=0
```

**Round-6 cancel/outbox commands.**

```powershell
node build/ticket-pr20-c0dd5f8/caller-query-race.cjs
# exit 1 (reproduces reported defect):
# {"lobbyLive":"A","oldCanCommand":true,"lateResult":"COMMITTED"}

node build/ticket-pr20-c0dd5f8/probe6-cancel-race-model.mjs
# exit 0:
# {"id":"Q1_reported_query_race","lobbyLive":"old","oldCanCommand":true,"lateResult":"CANCELLED"}
# scenarios=29 failures=0

$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg6 = "$base\pg7\pgsql\bin"
$port6 = 55474
& "$pg6\psql.exe" -h 127.0.0.1 -p $port6 -U mahjong_pg -d ticket_probe6 -f "$base\probe6-outbox-ddl.sql"
# old CHECK without NOT_SENT:
& "$pg6\psql.exe" -h 127.0.0.1 -p $port6 -U mahjong_pg -d ticket_probe6 -v ON_ERROR_STOP=1 `
  -c "INSERT INTO table_store.old_d0_outbox(...state) VALUES (...,'NOT_SENT');"   # exit 1
& "$pg6\psql.exe" -h 127.0.0.1 -p $port6 -U mahjong_pg -d ticket_probe6 -f "$base\probe6-outbox-states.sql"
& "$pg6\psql.exe" -h 127.0.0.1 -p $port6 -U mahjong_pg -d ticket_probe6 -f "$base\probe6-outbox-bad.sql"
# stop -m immediate; restart; query persistence; then simulate startup recovery:
& "$pg6\psql.exe" -h 127.0.0.1 -p $port6 -U mahjong_pg -d ticket_probe6 -f "$base\probe6-outbox-recovery.sql"
```

**Round-7 deadline/cleanup commands.**

```powershell
node build/ticket-pr20-c0dd5f8/caller-tombstone-cleanup.cjs
# exit 1 (reproduces reported defect):
# {"cancelled":"CANCELLED","late":"COMMITTED","lobbyLive":"A"}

node build/ticket-pr20-c0dd5f8/probe7-deadline-model.mjs
# exit 0:
# {"id":"D1_reported_cleanup_race","cancelled":"CANCELLED","late":"CANCELLED",
#  "lobbyLive":"old","oldCanCommand":true}
# scenarios=24 failures=0

$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg7 = "$base\pg8\pgsql\bin"
$port7 = 55475
& "$pg7\psql.exe" -h 127.0.0.1 -p $port7 -U mahjong_pg -d ticket_probe7 -f "$base\probe7-deadline-schema.sql"
& "$pg7\psql.exe" -h 127.0.0.1 -p $port7 -U mahjong_pg -d ticket_probe7 -f "$base\probe7-deadline-tests.sql"
& "$pg7\psql.exe" -h 127.0.0.1 -p $port7 -U mahjong_pg -d ticket_probe7 -f "$base\probe7-deadline-dbnow.sql"
```

**Round-8 terminal-result/idempotency commands.**

```powershell
node build/ticket-pr20-c0dd5f8/caller-review-deadline.mjs
# exit 0; prints:
# review-confirm-first {"status":"COMMITTED",...}
# review-confirm-retry-after-deadline {"status":"DEADLINE_EXPIRED"}   [old model]
# review-cancel-after-deadline-model {"status":"ALREADY_COMMITTED"}

node build/ticket-pr20-c0dd5f8/probe8-deadline-idempotency-model.mjs
# exit 0:
# {"id":"E1_committed_retry","first":"COMMITTED","atDeadline":"REPLAYED","after":"REPLAYED"}
# scenarios=21 failures=0

$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg8 = "$base\pg9\pgsql\bin"
$port8 = 55476
& "$pg8\psql.exe" -h 127.0.0.1 -p $port8 -U mahjong_pg -d ticket_probe8 -f "$base\probe8-deadline-idempotency-schema.sql"
& "$pg8\psql.exe" -h 127.0.0.1 -p $port8 -U mahjong_pg -d ticket_probe8 -f "$base\probe8-deadline-idempotency-tests.sql"
# old DDL must reject a late terminal row; final DDL must accept it:
& "$pg8\psql.exe" -h 127.0.0.1 -p $port8 -U mahjong_pg -d ticket_probe8 -f "$base\probe8-old-deadline-check.sql"   # exit 3
& "$pg8\psql.exe" -h 127.0.0.1 -p $port8 -U mahjong_pg -d ticket_probe8 -f "$base\probe8-deadline-final-late.sql"  # exit 0
& "$pg8\psql.exe" -h 127.0.0.1 -p $port8 -U mahjong_pg -d ticket_probe8 -f "$base\probe8-deadline-cancel-tests.sql"
```

**Round-9 cleanup/evidence commands.**

```powershell
node build/ticket-pr20-c0dd5f8/probe9-cleanup-evidence-model.mjs
# exit 0:
# {"id":"C1_committed_result_cleanup","first":"COMMITTED",
#  "after_result_and_request_cleanup":"REPLAYED"}
# scenarios=14 failures=0

$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg9 = "$base\pg11\pgsql\bin"
$port9 = 55478
& "$pg9\psql.exe" -h 127.0.0.1 -p $port9 -U mahjong_pg -d ticket_probe10 -f "$base\probe9-cleanup-schema.sql"
& "$pg9\psql.exe" -h 127.0.0.1 -p $port9 -U mahjong_pg -d ticket_probe10 -f "$base\probe9-cleanup-tests.sql"
# T1 commit -> cleanup request/result -> REPLAYED; T3 all evidence gone -> RESULT_EXPIRED.
& "$pg9\psql.exe" -h 127.0.0.1 -p $port9 -U mahjong_pg -d ticket_probe10 -f "$base\probe9-lock-setup.sql"
# Two processes: probe9-lock-a.sql holds/locks/deletes result; probe9-lock-b.sql waits.
# b output: LOCK_B_confirm=REPLAYED, elapsed ~1.65 s.
```

**Round-10 unified-order commands.**

```powershell
node build/ticket-pr20-c0dd5f8/probe10-order-model.mjs
# exit 0:
# {"general":"DEADLINE_EXPIRED","detail":"REPLAYED"}
# scenarios=8 failures=0 (O1_agree=false is the asserted old-vs-unified divergence)

$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg10 = "$base\pg12\pgsql\bin"
$port10 = 55479
& "$pg10\psql.exe" -h 127.0.0.1 -p $port10 -U mahjong_pg -d ticket_probe11 -f "$base\probe9-cleanup-schema.sql"
& "$pg10\psql.exe" -h 127.0.0.1 -p $port10 -U mahjong_pg -d ticket_probe11 -f "$base\probe9-cleanup-tests.sql"
# T1/T2 replay, T3 RESULT_EXPIRED, T5b/T6 TERMINAL_EVIDENCE_INCONSISTENT.
& "$pg10\psql.exe" -h 127.0.0.1 -p $port10 -U mahjong_pg -d ticket_probe11 -f "$base\probe9-lock-setup.sql"
# A/B concurrent: b output LOCK_B_confirm=REPLAYED, elapsed ~1.69 s.
```

**Round-11 strict three-witness commands.**

```powershell
node build/ticket-pr20-c0dd5f8/probe11-three-evidence-model.mjs
# exit 0:
# {"actual":"TERMINAL_EVIDENCE_INCONSISTENT","expected":"TERMINAL_EVIDENCE_INCONSISTENT","pass":true}
# scenarios=8 failures=0

$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg11 = "$base\pg13\pgsql\bin"
$port11 = 55480
& "$pg11\psql.exe" -h 127.0.0.1 -p $port11 -U mahjong_pg -d ticket_probe12 -f "$base\probe11-evidence-schema.sql"
& "$pg11\psql.exe" -h 127.0.0.1 -p $port11 -U mahjong_pg -d ticket_probe12 -f "$base\probe11-evidence-tests.sql"
# SQL assertions; any mismatch raises ASSERT_FAIL and psql exits 3.
& "$pg11\psql.exe" -h 127.0.0.1 -p $port11 -U mahjong_pg -d ticket_probe12 -f "$base\probe9-lock-setup.sql"
# A/B concurrent retry: b output LOCK_B_confirm=REPLAYED, elapsed ~1.71 s.
```

**Round-12 full-binding commands.**

```powershell
$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg12 = "$base\pg14\pgsql\bin"
$port12 = 55481
& "$pg12\psql.exe" -h 127.0.0.1 -p $port12 -U mahjong_pg -d ticket_probe13 -f "$base\probe12-binding-schema.sql"
& "$pg12\psql.exe" -h 127.0.0.1 -p $port12 -U mahjong_pg -d ticket_probe13 -f "$base\probe12-binding-tests.sql"
# exit 0; any ASSERT_FAIL would exit 3.
& "$pg12\psql.exe" -h 127.0.0.1 -p $port12 -U mahjong_pg -d ticket_probe13 -f "$base\probe12-lock-setup.sql"
# A/B concurrent: b output LOCK_B_confirm=REPLAYED, elapsed ~1.57 s.
```

**Round-13 NULL-binding commands.**

```powershell
$base = 'D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8'
$pg13 = "$base\pg15\pgsql\bin"
$port13 = 55482
& "$pg13\psql.exe" -h 127.0.0.1 -p $port13 -U mahjong_pg -d ticket_probe14 -f "$base\probe12-binding-schema.sql"
& "$pg13\psql.exe" -h 127.0.0.1 -p $port13 -U mahjong_pg -d ticket_probe14 -f "$base\probe12-null-tests.sql"
# 27 NULL/missing binding assertions; any mismatch raises ASSERT_FAIL and exits 3.
& "$pg13\psql.exe" -h 127.0.0.1 -p $port13 -U mahjong_pg -d ticket_probe14 -f "$base\probe12-lock-setup.sql"
# A/B concurrent: b output LOCK_B_confirm=REPLAYED, elapsed ~1.70 s.
```

### 7.2 Results

| ID | Probe | Native exit | Measured result |
|---|---|---|---|
| P1 | Two concurrent inserts of the same `jti` into `lobby.table_ticket_consumption` | A **0**, B **3** | B: `duplicate key value violates unique constraint "pk_table_ticket_consumption"`; final count **1** |
| P2 | Insert + mark `consumed_at`, then `ROLLBACK`; fresh commit afterwards | A **0**, B **0** | After rollback: `0,true` (count 0, `consumed_at` null); after B: `1,true` |
| P3 | A locks ticket `FOR UPDATE`, updates `consumed_at`, sleeps; B waits on the same row | A **0**, B **0** | B blocked **1548.562 ms**, then saw `2026-10-30 00:24:16.306513+08`, `was_consumed=t` |
| P4 | Consume old ticket first, then issue a new ticket for the same assignment | A **0**, B **0** | Old `jti_b` consumed; new `jti_b2` active; issuance waited **688.834 ms** on the assignment lock |
| P5 | Issue new ticket first, then consume old | Issue **0**, consume **3** | Consume blocked **707.796 ms**, then `ERROR: TICKET_SUPERSEDED`; old `jti_b` `superseded_at` set, new active |
| P6 | Commit a consumption, `pg_ctl stop -m immediate`, restart, query, replay | commit output `COMMIT`; stop output `server stopped`; restart log “server started”; query **0**; replay **1** | After restart: count **1**, `consumed_at` true; replay: `duplicate key ... pk_table_ticket_consumption` |
| P7 | Proposed admission table: two concurrent inserts, same `jti` | A **0**, B **3** | `duplicate key ... probe_ticket_admission_jti_key`; count **1** |
| P8 | Proposed admission table: same seat, different `jti`/account | A **0**, B **3** | `duplicate key ... probe_uq_live_seat` on `(room_1,0)`; live count **1** |
| P9 | Release then reinsert; new owner epoch releases old rows then admits | **0** | Live seats **1**; **2** released rows retained; epoch-2 row live; old/new released |
| P10 | Spectator cap 1 under room lock; two concurrent admissions | A **0**, B **3** | B blocked **1199.079 ms**, then `SPECTATOR_CAPACITY_REACHED`; live spectators **1** |
| P11 | Proposed admission row survives immediate restart | insert output `INSERT 0 1`; stop output `server stopped`; restart log “server started”; query **0** | `jti_persist,true` after restart |
| P12 | Old `ck_admission_seat` predicate, `PLAYER NULL` | **0** | Old CHECK accepted the row (`old_check_player_null_rows = 1`); same three-valued-logic defect the caller saw in Node/SQLite |
| P13 | Final DDL seat checks: `PLAYER NULL`, `PLAYER -1`, `PLAYER 4`, `SPECTATOR 0` | Each **3** | Each rejects with `ck_probe_seat`; valid `PLAYER 0` + `SPECTATOR NULL` insert exit **0** |
| P14 | Old cleanup predicate `retain_until <= now()` on a `LIVE` row | **0** | `DELETE 1`; row disappeared; a replacement `LIVE` for the same seat then inserted (`live_seat_3 = 1`), proving overcommit |
| P15 | Corrected terminal-only cleanup on the same long-lived `LIVE` row | **0** | `DELETE 0`; row stayed `LIVE`; replacement insert exit **3** (`uq_probe_live_seat`); after explicit release + grace, cleanup `DELETE 1` and replacement insert **0** |
| P16 | Cleanup vs handover, cleanup first, then D | A **0**, B **0** | Cleanup `DELETE 0`; D then `hand_old:SUPERSEDED`, `hand_new:LIVE` |
| P17 | Cleanup vs handover, D first, then cleanup | A **0**, B **0** | D `hand_old:SUPERSEDED`, `hand_new:LIVE`; cleanup `DELETE 1` of the terminal old row; final only `hand_new:LIVE` |
| P18 | Older `RESERVED` commit while newer `RESERVED` exists | **0** | `OLD_COMMIT_SUPERSEDED`; old `SUPERSEDED`, newer `RESERVED`; newer commit then `NEW_COMMIT_ACCEPTED` → `COMMITTED` |
| P19 | Older delayed WELCOME after newer `LIVE` | **0** | `OLD_WELCOME_SELF_SUPERSEDED`; final old `SUPERSEDED`, new `LIVE`; newer connection not demoted |
| P20 | Concurrent reverse WELCOME, old-first and new-first | A **0**, B **0** (both runs) | Both final states `seq_old:SUPERSEDED,seq_new:LIVE`; older never overwrote newer |
| P21 | Concurrent `admission_seq` assignment | A **0**, B **0** | Distinct sequence values `seq_a:1`, `seq_b:2` |
| P22 | Duplicate `request_id` with different `jti` | **3** | `duplicate key ... uq_probe_admission_request`; first row retained (count 1) |
| P23 | A/B inversion: old gen1 consumed, new gen2 issued/consumed, new local seq1 `LIVE`, old local seq2 delayed | **0** | The caller's Node model (exit 0) reproduced the old failure `newer=SUPERSEDED, older=LIVE` under local-seq-only ordering. With Lobby `ticket_generation`: new `adm_new:LIVE` seq1; old `adm_old:SUPERSEDED` seq2; `confirm_old=STALE_GENERATION`; live gen2. The larger local seq did not win |
| P24 | Concurrent old/new Confirm, old-first | A **0**, B **0** | Old `STALE_GENERATION`; new `LIVE`; final `adm_new:LIVE,adm_old:SUPERSEDED` |
| P25 | Concurrent old/new Confirm, new-first | A **0**, B **0** | New `LIVE`; old `STALE_GENERATION`; final `adm_new:LIVE,adm_old:SUPERSEDED` |
| P26 | Old consume vs new issuance, old-first | A **0**, B **0** | `old_consume=CONSUMED`, `new_issue_gen=2`; new `LIVE`; old `STALE_GENERATION` |
| P27 | Old consume vs new issuance, new-first | A **0**, B **0** | `new_issue_gen=2`; `old_consume=TICKET_SUPERSEDED`; new `LIVE`; old local `SUPERSEDED` (`old_confirm=NOT_CONSUMED`) |
| P28 | Existing old `LIVE`, then new ticket confirms | **0** | `old_confirm=LIVE`, `new_confirm=LIVE`, `old_after_new=STALE_GENERATION`; final old `SUPERSEDED`, new `LIVE` |
| P29 | Existing live owner epoch 1; new process owner 2 | **0** | Unexpired: `new_owner2_unexpired=LEASE_HELD`; after lease expiry: `new_owner2_expired=LIVE`; old after takeover: `STALE_GENERATION`; local old seq3 `SUPERSEDED` |
| P30 | Confirm transaction rollback | **0** | Confirm gen3 returned `LIVE` then `ROLLBACK`; durable live returned to `adm_new:2`; `adm_fail` local row remained `RESERVED` (application must release it; no live leak) |
| P31 | PostgreSQL immediate restart then old ticket | query **0** | Live `adm_new:2` and local `adm_new:LIVE,adm_old:SUPERSEDED` persisted; `old_after_restart=STALE_GENERATION` |
| P32 | Node actor `HANDOVER_PENDING` model | **0** | `scenarios=22 failures=0`; old fail/complete tokens ignored after newer pending/current; B/C wins; A restored only when no newer pending |
| P33 | Node compare: local `admission_seq` only vs `(ticket_generation, local seq)` | **0** | `local_seq_only_winner=old_ticket` (reproduces the caller's A/B inversion); `freshness_first_winner=new_ticket` (corrected). No SQL, only the ordering rule |
| P34 | Node actor+D0 model: reported interleaving, B success/failure, A D0 late/unknown, compensation vs newer live, restart | **0** | `scenarios=49 failures=0`; reported S1 snapshot `lobbyLive=A:10 actorCurrent=old oldCanCommand=false`; B failure did not restore old; late A local D switched to A; unknown A prevented old; compensation did not clear newer B; restart with missing local A had no old authority |
| P35 | Node in-flight model: send intent, Lobby commit, delayed/no response, B fail/success, restart, provably unsent vs uncertain | **0** | `scenarios=36 failures=0`; F1 `lobbyLive=A, actorFloor=old, phase=RECONCILING, outcome=NO_RESTORE_INFLIGHT, oldCanCommand=false`; F2 old only after definitive no-commit; F3 B wins; F4 late A wins; F5/F6 restart; F7 rejected restore; F8 unsent restore/uncertain no |
| P36 | Caller `caller-inflight-probe.cjs` against round-4 model | **1** | Exact reported failure: `{"lobbyLive":"A","actorFloor":"old","outcome":"RESTORED_CURRENT","oldCanCommand":true}`; retained as the defect reproducer, not a design pass |
| P37 | Caller `caller-query-race.cjs` against round-5 model | **1** | Exact reported failure: `{"lobbyLive":"A","oldCanCommand":true,"lateResult":"COMMITTED"}`; query no-commit lacked a terminal fence |
| P38 | Node cancel/confirm race model: tombstone, absent query, confirm-first, cancel-first, duplicate, restart, lost response, newer B | **0** | `scenarios=29 failures=0`; corrected Q1 `{"lobbyLive":"old","oldCanCommand":true,"lateResult":"CANCELLED"}`; old true only after durable tombstone; confirm-first returns `ALREADY_COMMITTED`, old false |
| P39 | PostgreSQL outbox DDL: old CHECK lacking `NOT_SENT`, final CHECK, invalid state, immediate restart, startup recovery | old NOT_SENT **1**; final states **0**; bad state **3**; restart query **0**; recovery **0** | Old table rejected `NOT_SENT`; final accepted `PENDING_SEND/SENDING/IN_FLIGHT/NOT_SENT/UNKNOWN/COMMITTED`; invalid `FOO` rejected; all states persisted across immediate restart; recovery mapped pending/sending/in-flight to `UNKNOWN`, kept `NOT_SENT` and `COMMITTED` |
| P40 | Caller `caller-tombstone-cleanup.cjs` against round-6 model | **1** | Exact reported failure: `{"cancelled":"CANCELLED","late":"COMMITTED","lobbyLive":"A"}`; tombstone deletion reopened the request |
| P41 | Node deadline/cleanup model: tombstone cleanup after deadline, before deadline, confirm-first, boundary, restart, missing record, old-command window | **0** | `scenarios=24 failures=0`; D1 `{"cancelled":"CANCELLED","late":"CANCELLED","lobbyLive":"old","oldCanCommand":true}`; pre-deadline cleanup 0; at-deadline `DEADLINE_EXPIRED`; missing record `AUTHORITATIVE_RECORD_MISSING` |
| P42 | PostgreSQL deadline/cancel/cleanup functions: before/after cleanup, confirm-first, boundary, DB time, missing record | **0** | T1 cleanup before=0, late confirm CANCELLED, cleanup after=1, late confirm CANCELLED; T2 confirm COMMITTED, cleanup 0, cancel ALREADY_COMMITTED; T3 boundary DEADLINE_EXPIRED; T4 missing AUTHORITATIVE_RECORD_MISSING; DB-time expired/cleanup/confirm as expected |
| P43 | Caller `caller-review-deadline.mjs` against round-7 model | **0** | `review-confirm-first=COMMITTED`; retry after deadline `DEADLINE_EXPIRED` (old model defect); `review-cancel-after-deadline-model=ALREADY_COMMITTED` (model/doc divergence) |
| P44 | Node terminal-result/idempotency model: commit retry, cancel after commit, new at deadline, cleanup, restart, missing/conflict | **0** | `scenarios=21 failures=0`; E1 `first=COMMITTED, atDeadline=REPLAYED, after=REPLAYED`; E2 `ALREADY_COMMITTED`; E3 new at deadline `DEADLINE_EXPIRED`; E5 replay after row delete; E6 missing/conflict |
| P45 | PostgreSQL terminal-result/deadline DDL: commit retry, cancel after, late REJECTED row, cleanup, missing/conflict | core **0**; old-check **3**; final-late **0**; cancel **0**; late **0** | T1 commit/retry at/after `COMMITTED`/`REPLAYED`/`REPLAYED`; cancel `ALREADY_COMMITTED`; T2 new at deadline `DEADLINE_EXPIRED`; old CHECK rejects late row, final accepts; cancel/cleanup/late `CANCELLED`; missing `AUTHORITATIVE_RECORD_MISSING`; row-delete replay; conflict; one-hour-late `DEADLINE_EXPIRED`, cleanup 1, late retry still expires, retention > created_at |
| P46 | Node cleanup-evidence model: committed, request+result cleanup, all evidence gone, restart, conflict, inconsistent marker | **0** | `scenarios=14 failures=0`; C1 `after_result_and_request_cleanup=REPLAYED`; C3 `RESULT_EXPIRED` (not deadline); C5 cancelled marker; C8 `TERMINAL_EVIDENCE_INCONSISTENT` |
| P47 | PostgreSQL cleanup/evidence: request/result cleanup, consumption marker, all gone, late new, conflict, inconsistent | **0** | T1 `COMMITTED` → cleanup request/result 1/1 → `REPLAYED`, Cancel `ALREADY_COMMITTED`; T2 result gone → `REPLAYED`; T3 consumption+result gone → `RESULT_EXPIRED` (not deadline); T4 new `DEADLINE_EXPIRED`; T5b terminal conflict / T6 marker cleared → `TERMINAL_EVIDENCE_INCONSISTENT` |
| P48 | PostgreSQL concurrent cleanup/Confirm lock probe | output **REPLAYED**, elapsed **~1.69 s** | A held the row lock/deleted result; B waited then replayed from consumption marker; no delete/reopen window |
| P49 | Node unified-order model: general vs detail after request/result cleanup, marker NULL/conflict, all gone, new deadline | **0** | `scenarios=8 failures=0`; exact divergence `general=DEADLINE_EXPIRED`, `detail=REPLAYED`; marker/result conflict `TERMINAL_EVIDENCE_INCONSISTENT`; all gone `RESULT_EXPIRED` |
| P50 | Node three-witness model: result/request/marker state, binding, live id, result-code conflicts | **0** | `scenarios=8 failures=0`; reported three-way case `actual=TERMINAL_EVIDENCE_INCONSISTENT`, `pass=true`; consistent marker-only `REPLAYED` |
| P51 | PostgreSQL strict three-witness schema/tests with SQL assertions | **0** | C1 three witnesses replay; C2 three-way state conflict / C3 result-absent request conflict / C5 result-code / C6 live-id → `TERMINAL_EVIDENCE_INCONSISTENT`; C4 binding → `REQUEST_ID_CONFLICT`; C7/C9 all gone → `RESULT_EXPIRED`; C8 cleanup replay; C10 new deadline |
| P52 | PostgreSQL concurrent retry with strict schema | output **REPLAYED**, elapsed **~1.71 s** | A holds/deletes result; B waits then replays marker; no hidden request conflict |
| P53 | PostgreSQL full-binding schema/tests: jti on each row × Confirm/Cancel/Lookup, account/room/assignment/session/owner_epoch, generation, result fields | **0** | C1 baseline; C2 result jti / C3 request jti / C4 consumption jti → `REQUEST_ID_CONFLICT` for all three ops; C5–C9 fields → conflict; C10 generation → `TERMINAL_EVIDENCE_INCONSISTENT`; C11–C12 terminal conflicts; C13 all gone; C14 cleanup replay; C15 deadline; C16 result-code / C17 live-id+generation in `probe12-null-tests.sql`; P55 NULL matrix; `probe_assert` raises `ASSERT_FAIL`, so any mismatch exits 3 |
| P54 | PostgreSQL full-binding concurrent retry | output **REPLAYED**, elapsed **~1.70 s** | A holds/deletes result; B waits then replays marker with full binding; native exit not captured |
| P55 | PostgreSQL NULL/missing binding arguments: 9 fields × Confirm/Cancel/Lookup; exact binding control | **0** | 27 `INVALID_BINDING` assertions; `request_id`/admission/jti/ticket/account/room/assignment/session/owner all fail closed; exact binding still REPLAYED/ALREADY/COMMITTED |

P51 is the earlier probe11 schema; it compared only admission_id/ticket_generation, omitted jti and the rest of the binding, and its C5/C6 did not test generation equality. P53 is the authoritative full-binding probe; P51 is retained only as a narrower historical check.

P44 was a simplified model using an in-memory c.terminal; the final schema now defines the full-binding consumption terminal columns used by P46. P45 cleaned only the request row; P47/P48 additionally clean result/consumption and cover the window.

Controlled crash recovery lines retained in `pg-recovery-lines.txt`:

```text
2026-10-30 00:25:21.622 CST [61588] LOG:  received immediate shutdown request
2026-10-30 00:25:23.562 CST [60184] LOG:  database system was not properly shut down; automatic recovery in progress
2026-10-30 00:25:23.567 CST [60184] LOG:  redo starts at 0/156D828
2026-10-30 00:25:23.896 CST [60184] LOG:  redo done at 0/1ACC230 ...
2026-10-30 00:25:24.648 CST [60172] LOG:  database system is ready to accept connections
```

### 7.3 What the probes prove and do not prove

**Prove:**

- The candidate `lobby` schema's `jti` primary key is a real cross-connection exact-replay
  fence; one of two concurrent inserts wins.
- The existing `FOR UPDATE` pattern makes a second consumer wait and then observe
  `consumed_at`; rollback leaves no consumption row.
- The documented room → assignment → ticket lock order serializes consume vs issuance: issue-
  first makes old consume fail `TICKET_SUPERSEDED`; consume-first lets issuance create a new
  active ticket while the old jti stays consumed.
- The proposed `table_store` constraints (unique `jti`, partial unique live seat, room-lock
  capacity count) behave as intended under concurrency.
- Committed Lobby consumption and proposed admission rows survive an immediate postmaster
  restart and WAL recovery.
- On the **full final DDL**, the explicit seat NULL check rejects `PLAYER NULL`, `PLAYER -1`,
  `PLAYER 4`, and `SPECTATOR 0`; a retain-only cleanup deletes a `LIVE` row and permits a second
  live seat, while terminal-only cleanup does neither; a live row crossing `retain_until` stays
  live and blocks replacement; cleanup and handover serialize safely under the room lock.
- On the round-2 full DDL, local `admission_seq`/reverse-WELCOME ordering prevents an older local
  row from committing/WELCOMING over a newer active row **when the ticket generations are
  already ordered**. This does not prove A/B freshness; round 3 shows local seq alone is
  insufficient when B is delayed after A.
- A duplicate `request_id` with a different `jti` is rejected by the final DDL.
- In the simplified Lobby freshness model, a delayed old ticket with a **larger local
  `admission_seq`** loses to a newer `ticket_generation`: old `STALE_GENERATION`/`SUPERSEDED`,
  new `LIVE` (A/B inversion corrected). Both old/new Confirm and old-consume/new-issue concurrent
  interleavings end with only the new generation live.
- Existing old `LIVE` is displaced by a newer ticket; a different owner epoch is `LEASE_HELD`
  until the old live lease expires; after takeover the old ticket remains `STALE_GENERATION`;
  the live generation and local states survive an immediate PostgreSQL restart.
- A rolled-back Confirm does not leave a durable new live generation; local compensation remains
  the application's responsibility.
- The Node actor model shows stale `FailHandover`/`CompleteHandover` tokens are ignored when a
  newer pending or current authority exists.
- The separate Node comparison model reproduces the caller's A/B inversion under local-seq-only
  ordering (`old_ticket` wins with seq 2) and selects `new_ticket` when `ticket_generation` is
  compared first.
- The extended Node actor+D0 model shows the reported interleaving does **not** give old command
  authority: `lobbyLive=A:10`, `actorCurrent=old`, `oldCanCommand=false`; B failure recovers to A
  only when A's local D is ready, otherwise no authority. It also covers unknown/late D0,
  compensation against a newer live, and restart with missing local A.
- The in-flight model shows a **sent but not yet answered** D0 is enough to block old: the
  caller's exact failure is reproduced with exit **1**, while the send-fence model yields
  `NO_RESTORE_INFLIGHT`/`oldCanCommand=false` with exit **0**. It also covers no-commit plus a
  durable cancel tombstone, B success, delayed A response, restart, and proved-unsent vs
  uncertain send.
- The cancel/confirm model shows a durable `CANCELLED` tombstone is the only safe way to restore
  old after a no-commit query: late Confirm returns `CANCELLED`, while confirm-first returns
  `ALREADY_COMMITTED` and old stays false. Absence alone never restores old.
- The deadline/cleanup model shows a tombstone may be deleted only after the authoritative
  deadline, and a late Confirm after deletion is `CANCELLED`/`DEADLINE_EXPIRED`, never
  `COMMITTED`; a missing consumption record is `AUTHORITATIVE_RECORD_MISSING`.
- The real PG function probe verifies the deadline/cancel/cleanup predicates, the at-deadline
  rejection, DB-time checks, and missing-record fail-closed behavior.
- The terminal-result model/PG probe shows an existing exact-binding `COMMITTED` result is
  replayed after the deadline/restart, Cancel after commit returns `ALREADY_COMMITTED`, and only a
  new Confirm with no terminal result is `DEADLINE_EXPIRED`; a late `REJECTED` row with
  `d0_deadline` before `created_at` is valid.
- The full-binding PG probe reads result/request/consumption under one lock and compares
  `jti` plus account/room/assignment/session/owner_epoch on every row; any per-row conflict is
  `REQUEST_ID_CONFLICT`, generation/result-field conflict is
  `TERMINAL_EVIDENCE_INCONSISTENT`. A partial `admission_id`/`ticket_generation` check is not
  sufficient and is not claimed as full binding.
- The NULL-binding probe rejects every NULL/missing required argument (`request_id` included)
  with `INVALID_BINDING` before any lookup; 9 fields × Confirm/Cancel/Lookup are asserted, while
  the exact binding still replays. This is a simplified SQL probe, not production RPC validation.
- The real PG outbox probe shows the final CHECK accepts persistent `NOT_SENT`, the old CHECK
  rejects it, invalid states are rejected, and unresolved/terminal states survive restart and
  startup recovery.

**Do not prove:**

- That candidate C# `InMemoryTicketConsumptionRegistry` was replaced or wired to this schema.
- That the Lobby internal consume endpoint, TableService client, or response binding exists.
- That two real TableService processes behind WSS share the durable ledger, or that real
  PostgreSQL WSS admission works.
- That TLS/mTLS, service authentication, Lobby availability behavior, or production clock skew
  works.
- That the actor-level `HANDOVER_PENDING`/generation fence, reverse-WELCOME command rejection, or
  per-command/delivery fencing exists; those are design-only and `NOT_RUN` in C#. The Node models
  prove only the token/`lobbyLive` state-machine logic, not the actor integration, actual D0 RPC,
  Lobby DB idempotency, or WSS.
- That the round-3 SQL freshness model is the candidate Lobby schema or the full production
  admission DDL; it is a simplified isolated model of issuance/consume/confirm.
- That the Lobby `ConfirmAdmission`/`CancelAdmission` SQL transaction, tombstone retention/cleanup,
  or C# actor integration exists. The Node cancel model proves only the ordering logic; the PG
  deadline functions are probe SQL, not the production service transaction.
- That network delay is bounded or that a missing request row proves no commit. The design uses
  the durable deadline/cancel marker, not absence.
- That the D0 outbox is durable/transactional in PostgreSQL or that a C# mailbox marks
  `SENDING` before the network write. The Node model encodes the required order; the PG probe
  verifies only the outbox CHECK/state persistence, not the production adapter.
- That the design's implementation compiles, passes tests, or fixes the security gap.
- That the full DDL is the final migration; it is a probe DDL and still needs review/migration
  packaging.

---

## 8. Preserved evidence and hashes

All paths are under the ignored:

```text
D:\majiang-wt\ticket-shared-design-20261030\build\ticket-pr20-c0dd5f8
```

| Artifact | SHA-256 |
|---|---|
| `probe-manifest.csv` (66 files, round 1) | `0B61160CD1BC0E27A456ABAAC17F7659F329250678F5684E74BE82811D69E85C` |
| `probe2-manifest.csv` (83 files, round 2 full-DDL probes) | `6AEC70BE552D94E3E4591F9504244C043A5B2E689F96681A4D63A3E9E1751ED7` |
| `probe2-final-ddl.sql` (full final probe DDL) | `F635FB5761A175E1BB1ED59A2AE8A60B536C94110AFF0774E99C511A1D629A12` |
| `pg2-ddl-final.out` (DDL created) | `0D2198AD439E5BBDD4080C49C2E3883CD621DAEB1105BF0B9901BCB64A6A9F94` |
| `probe2-old-seat-final.out` (old CHECK accepts PLAYER NULL) | `CD47BC3FA3BEB85275CE835FF440AA69C5F99E270779826FDDDBFEED6032BE6C` |
| `probe2-bad-player-null-final.out` | `293EF1DC8A47FEB45E56C8A091696E9B56989AE1C8790BCE1C53D14F8E0B162B` |
| `probe2-bad-player-neg-final.out` | `E548A8485F20A00F053F8FD3F4D2F86ADCB6C92E16439819076C2B9FD206DE81` |
| `probe2-bad-player-high-final.out` | `6FAFAAEE4E14AD2628B73D3E8086ECA386964E38FD1B5B88200097BDDBAAD70F` |
| `probe2-bad-spectator-seat-final.out` | `9A9CB7083D6B63D1998DCB3F96B46F79730AE635008DB415A346716BF0C3B6A9` |
| `pg2-cleanup-old.out` (retain-only deletes LIVE) | `8C2AE4A34C03947449204E3B693EA1EA60B6FD0EAE36C1FB2CCE35812661EEA8` |
| `pg2-cleanup-corrected.out` (terminal-only) | `D56A1CAE8AC4B9BCB210F0A93AC5EEA6BDC5A0AF96879E3256CD35B9A907316D` |
| `pg2-live-overcommit.out` (replacement blocked) | `8BD3226E4F0724A38D0596BE660DE38D0867915B91661F5060F61B6C2ABDABAE` |
| `pg2-release-cleanup.out` | `6589DE39142B09A882647A7BAF2295978EE31F5A6A0E5FF11A01C9A07BE2C67F` |
| `pg2-old-welcome-final.out` (old WELCOME self-superseded) | `B7DB85DD2BEAB96A837A9F15A4EFCDE4F25B240E867DA05D58DD89FA809EDD6C` |
| `pg2-old-commit.out` | `A483459B156CCC0E486479C7B7781421BABB4466CF46C7B4C11D24181A03CFBA` |
| `pg2-request-dup.out` (duplicate request id) | `E72E22CC3A10A19D7AFAB4B135744864AA4EBC26041336D0313A2503561C89B3` |
| `pg2-valid.out` (valid seat/spectator) | `34CB9DE268951F69A5A0D10CE04F1C3FE34B1734A84D929FD8A426333AD866BD` |
| `probe3-manifest.csv` (53 files, round-3 freshness/actor model) | `89E9A87EC2AE7F773E858CBCFE0C4B47C5992BEFC04D3C6630E47503497AE2FE` |
| `probe3-schema.sql` (simplified Lobby freshness model) | `DEB79F4C7E16F0D857C1A3337D522A446A5D9EFDBFDA9FCCC4ED7BBF02767D7A` |
| `probe3-ab.out` (A/B inversion corrected) | `DF690A90B338B31E2404397209EF561C75E2463616D685900CC298857A95150E` |
| `probe3-confirm-oldfirst-a.out` | `D889F9F54AE5514F8437E585C4AFA93CBCE168AC1A81F06263E06331646AFF8F` |
| `probe3-confirm-newfirst-b.out` | `62DA7E4AEB90288CA5EA38853F04CF7445ED2A90111FAE9EAACE2C9E3F740B70` |
| `probe3-lease.out` (owner-epoch/lease fencing) | `88961C757C2EEC9340447D17617EC33CD2DE9DE21ACF6FB8B250DA0DEFD8871B` |
| `probe3-rollback.out` | `D7F75E4DF7E8DD45E9A51339B63485AEC3B8CF1F5E371E44A245503A93BD7402` |
| `probe3-restart-old.out` (post-restart stale old ticket) | `3844B7BADBD08D44A81944A2BCB3D9C5D2A7142A43ABE2FC1F0334F7551D63D3` |
| `probe3-handover-model.out` (22 scenarios, 0 failures) | `417AEDBBC1E1574694D25C75B29AD9CA6E5B846D77DBA407D626624E463F9837` |
| `probe3-freshness-compare.mjs` | `0D123C89D7B2ADE6F05221442AB7BE3B119F77D62B5C05A44609D033D77A4A2C` |
| `probe3-freshness-compare.out` (local seq vs freshness) | `A7A99C0EB5E453A51C6908A5567D83C750A14A8E4BCBCA15C81496DA4727FCA4` |
| `probe4-manifest.csv` (2 files, round-4 actor+D0 model) | `EDBE16145C3F67814AB11DDA5F4702C11A21D993C26C85ACB049746CE494420F` |
| `probe4-actor-d0-model.mjs` | `DA8F4A6549BA5C5E883728A54E1C8076EFDEE2DB1CD83FC9E0089917C90ADD59` |
| `probe4-actor-d0-model.out` (49 scenarios, 0 failures) | `3C326EB2EA770484154E31A40493A5E3948341149EFFCED3B5D7AE7A6FF3BA3E` |
| `probe5-manifest.csv` (4 files, round-5 in-flight D0 model) | `EB36F4B70C5C5C79D6B41E7E007DF10E36974B2CEA1CD58901638F042B9D1057` |
| `probe5-d0-inflight-model.mjs` | `8D67774330B323692B7D3446DAE26FB91A04D07534339C5F0E92DB57353A811F` |
| `probe5-d0-inflight-model.out` (36 scenarios, 0 failures) | `4ED038930D6B16321D6BEB5DDCE088F8EF23F2DB89AEA986E3FB770EDED7FA94` |
| `caller-inflight-probe.cjs` | `D23C6FFE8170198261B5D6D10C642138E2051895ACDA2B44F63EFDE2F7B1F3EF` |
| `probe5-caller-inflight.out` (reported defect, exit 1) | `69058B92DE5E7BA4F73A444CB8510CA4A7F5B58676E921FA9EFD46470FCD425C` |
| `probe6-manifest.csv` (17 files, round-6 cancel/outbox probes) | `FCF525DCFB4AA19F880D21F576256BE9377B7D0BD79EEAFC31324B7D49187D8A` |
| `probe6-cancel-race-model.mjs` | `79A5459601C651EA45105C1D59F8CB7BC2AF13BF6FF5ED19ACF755B6587F3E4B` |
| `probe6-cancel-race-model.out` (29 scenarios, 0 failures) | `39AAD17593FD0A09F644795B05E9EC5A8C76EB6CAA990DC608D8C5093459C573` |
| `caller-query-race.cjs` | `8F5EB01E624D81FE0CA2A21515E7E3FCED9C3BA26B4FDC8C8007471D30744B00` |
| `probe6-caller-query-race.out` (reported defect, exit 1) | `79E06D499D7B2C512ACB30F5C2130E1A6B60541BF8996BC53BD17F3AA2A6C5D3` |
| `probe6-outbox-ddl.sql` | `761D04E5B33C69B92E51003F75D55BAB94466A2E9A81D895E66A559D037DF8A7` |
| `probe6-outbox-states.out` | `270822C699EBF6C915E8D9812EFEBC8A996B2649FE32D4B80F8472BD8B170F80` |
| `probe6-outbox-bad.out` (invalid state, exit 3) | `FB9E93B5D31A794A5C7254C64A86467C970F98E233DAAF607D02DB5A5B183ADE` |
| `probe6-outbox-restart.out` | `2D4A51A32A34698D3CEE6AE1BFAE48B389AC1604F554FF09A2A3E3950B1D0D9D` |
| `probe6-outbox-recovery.out` | `06014AF3FDBF2CF878465398805AE1E392917F10521821A11A76233C62D251A3` |
| `probe7-manifest.csv` (10 files, round-7 deadline/cleanup probes) | `B8A4B22CB7041D7EEAD059576F445BAEB012D1297B4253AB7BBCF6D7B0E845C0` |
| `probe7-deadline-model.mjs` | `D68C11CBB743E2B85EAA91DD3950200579685565BC26E83CAD0F54FE58A2E0FE` |
| `probe7-deadline-model.out` (24 scenarios, 0 failures) | `D38EEF9232600078C7592D321F29E234C73BAB23F0771FFB06D324D07DE4891D` |
| `caller-tombstone-cleanup.cjs` | `24249C5FA359DFBB0272A5285BA9F8689D77E328E645537C26A735755B4C87B0` |
| `probe7-caller-cleanup.out` (reported defect, exit 1) | `F25D84B4EB5E1A99D68544BCE94807E85D5F16BBE3DD569646CAE2306E12D6CC` |
| `probe7-deadline-schema.sql` | `F8D97EB1C359C66B57E2FBD6E93F73205050AC0548DDF4535FB3B6B9CEC65325` |
| `probe7-deadline-tests.out` | `45A76F4AF23FC40AB367C961A71FB8ED292974D66086F373CB4A2D56480D931C` |
| `probe7-deadline-dbnow.out` | `4EF37B094F0D263345F622EF9BDE650DC7AF534F7D4A6979E0FC23AC55BE0245` |
| `probe8-manifest.csv` (17 files, round-8 terminal-result probes) | `313E028131B87719707D3E7BFA862EE651704678A6F456BD11C8F0591BD61CAE` |
| `probe8-deadline-idempotency-model.mjs` | `D14D615EBDC80184F4C6B322FA4AC51E6FA48355E19B5C1BC5A6B0E722619F5E` |
| `probe8-deadline-idempotency-model.out` (21 scenarios, 0 failures) | `811ADF1F548365A9072B9C638656F58560691C9E09F944BCC1181CD33CE2F62C` |
| `caller-review-deadline.mjs` | `672B25E1A862C22A8224A17CC0175E86CD837D42EBFFD8F4B16932DF777B10EC` |
| `probe8-caller-review.out` (reported divergence, exit 0) | `BE8A4F05E9C47949614F130AE20A552A7BA9412BCF48213DF725D61FEC9D49D9` |
| `probe8-deadline-idempotency-schema.sql` | `7396F43378FCE20421B952534B0B80CC92D00A701738D4EB76142C0F25CBF101` |
| `probe8-deadline-idempotency-tests.out` | `C323F9A7D02786F0CFA1048306E9F68198F2195A49A3ECB5EF228E267B8B001B` |
| `probe8-deadline-cancel-tests.out` | `3410DF0F46B1634677C9C4A592D0235FF7BFF33FACFD3AFD6CEDC29FF0F188AF` |
| `probe8-old-deadline-check.out` (old DDL rejects late row, exit 3) | `33804BCB55CD1889A7B0BA8F9F1490D310B55AE9C8F51B231924696DC650CA31` |
| `probe8-deadline-final-late.out` (final DDL accepts, exit 0) | `D3C0F2A869CAE51F6C454B60568E7E67C6087A61C85F63A2EE40E44CFF030208` |
| probe8-deadline-late-result.out (late after deadline+grace, cleanup) | 9FF99A43855FB7511074C76AF04BC0E4A66505189881EC5D840C8F8A06C30E8D |
| `probe10-manifest.csv` (16 files, round-10 unified-order probes) | `CD8DD1EEB712E961741909459481D32EB602F6DCA241138067C359A69B9D325D` |
| `probe9-cleanup-evidence-model.mjs` | `92C0CCEB6FE1B0BAF6644C53F3DED429886717C7B2CBF3EA68A1676213153A28` |
| `probe9-cleanup-evidence-model.out` (14 scenarios, 0 failures) | `9D8CC2122EBE0775141DDA7D1A0BF9BF4C0820FAD3DACA6EC85008122434F7FB` |
| `probe9-cleanup-schema.sql` (final unified schema) | `7CECA8EFB057E8744BC8675D900D6390DD779B87E3870E74E43CFFB87C85DD4E` |
| `probe9-cleanup-tests.out` (final T1/T2/T3/T5b/T6) | `71F03C5D52293E6433A54FD3272EA4B50AAB7463BE3604B7FE84E667768212D9` |
| `probe9-lock-b.out` (concurrent cleanup/Confirm) | `BADAC8D892ADF79F181A09541D094663F57D02247796C49291C35E3298B6C7F8` |
| `probe10-order-model.mjs` | `33CD7B4DA12F4EE67C46639D1E1994DAD040EBA2623985D91510647685826D6E` |
| `probe10-order-model.out` (8 scenarios, 0 failures) | `119AA2EBF50FD6A879A15CD2D15E6F53E7C494B3F1926664E1E846630EFE8919` |
| `probe11-manifest.csv` (11 files, round-11 strict evidence) | `59B46AE5382CDB0D1C5695D067CE3BD0BAC8AFD86E72CDC94ACD75062DC4FFBE` |
| `probe12-manifest.csv` (13 files, round-12/13 full binding + NULL) | `300E763ADC425A02DBDD6C8E7F827AD8DD9C0CC4EB73E0F9C722A93BBDDDAA1B` |
| `probe12-binding-schema.sql` (full binding/NULL guard) | `81B7BB19D8F835A5B753E2EAC0042A34F81C045ABC747778A5B578D02864BEBA` |
| `probe12-binding-tests.out` (SQL assertions, exit 0) | `BEE1132343432C62EBE27A5130B8B354D326FCF7BC1D168ADD9F0AD91713B0D0` |
| `probe12-null-tests.out` (27 NULL assertions, exit 0) | `6191C431DFC8176391EEEEB0E7B928A0FCE3981516D1B8114AC0DE206AB5C89E` |
| `probe12-lock-b.out` (concurrent retry) | `F93ECF089D88231D00D4E2FF51503592B4E136669FC37C2EABA9AFEAB3E4E54A` |
| `probe11-three-evidence-model.mjs` | `7F3F2B6695E263DCC26E75B20849D0398F5FAF22A824BEABFEF0B5EBD222100E` |
| `probe11-three-evidence-model.out` (8 scenarios, 0 failures) | `FEC3F5946C2763766DD8C498148BB13C83163ECFF1EF4D1EEF86F874C7F46730` |
| `probe11-evidence-schema.sql` (strict three-witness schema) | `6B2FF1CCFD7B0B9B3376CE10BF60074B3EC7C9AC139B0CC38EB0080A6401FF76` |
| `probe11-evidence-tests.out` (SQL assertions, exit 0) | `313FBCE6A995F51445A7FA938EE3E8265418BBBA122C163647E021580B718883` |
| `probe11-lock-b.out` (concurrent retry) | `B584EAFA960D15F8CA3AD746F23EA2F2CDD74C2E60B74111C3319C31DDF7DCE3` |
| `run-concurrent.ps1` | `43298FB46367638012FBB6DB5BECAE8382A193FBDB1C6E3038B102E2D7F13430` |
| `probe-schema.sql` (proposed schema) | `CEBA1BA08B6EE13A72E907C22A6A9F94310A542379214C28DB2D8E30481A4D6E` |
| `pk-b.err` (same-jti unique violation) | `79BEEA5C897AE272A537EE7CE4E78946216914D8F22410935925424578202755` |
| `lock-b.out` (FOR UPDATE wait) | `2299DC9E4237E73FB58E17B503CF5898716D5C6AAFDA119B408A868C827E5B6E` |
| `super2-b.err` (`TICKET_SUPERSEDED`) | `F693C791D322771FED10AD2E04BB3AB774477B80A80CA5841FB556476F4C40EA` |
| `crash-commit.out` | `8E575DFCC77A94A10E85CC1C1CEA5D7E3A4E6E316D4504F7282FA60123E94630` |
| `pg.log` (final, after shutdown) | `C21D65B58D37C4934D2EC4E1250871E47733430EF4DE982C4F773FE3EF5CBBA3` |
| `pg-recovery-lines.txt` | `AE335A13E40D0F0B8691ED00631B077138295084E5322F6A45B83469CABDDEF9` |
| `adm-jti-b.err` (proposed same-jti) | `6861FB3848C58741DAA7D03AC95832F9C3CB5E7962A004A24F3C32D366B148CF` |
| `adm-seat-b.err` (proposed live seat) | `B65761966A1A98DF18ED76D96B6C2D20125F4C427CF89C38637BD2B09E98DE91` |
| `adm-spec-b.err` (proposed spectator cap) | `0988FE60BB0AB9CFB7AD07725F85A1FD2F11D86F8E3A964953A209AF18CD4452` |
| `adm-release-epoch.out` | `35CB6A9D3428CED301F7882387DE8FD88E6BBC983844CEE97813B74F5391EFB0` |

The thirteen manifests are the authoritative lists; the table above is a convenience subset. The
candidate source hashes are in §1.4. `pg.log` includes additional crash/recovery cycles caused by
the round-1 probe harness restarting the owned cluster; the controlled cycle at 00:25:21–00:25:24
is the one quoted in §7.2. The round-2 cluster (`pgdata2`, port 55472), round-3 cluster
(`pgdata3`, port 55473), round-6 outbox cluster (`pgdata7`, port 55474), round-7 deadline
cluster (`pgdata8`, port 55475), round-8 idempotency clusters (`pgdata9`/`pgdata10`, ports
55476/55477), round-9 cleanup cluster (`pgdata11`, port 55478), and round-10 unified-order cluster
(`pgdata12`, port 55479), and round-11 strict-evidence cluster (`pgdata13`, port 55480), and round-12 full-binding cluster (`pgdata14`, port 55481), and round-13 NULL-binding cluster (`pgdata15`, port 55482) were stopped and deleted; their logs and outputs are retained in the
ignored directory. Round 4 and round 5 are Node models only (no database). No shared database or
another worktree was touched.

---

## 9. Status: PASS / FAIL / BLOCKED / NOT_RUN

| Item | Status | Evidence |
|---|---|---|
| Candidate/base SHA and ancestry verified | **PASS** | `c0dd5f8`, tree `802fa5f`; ancestor exit **0**; 330 commits ahead |
| Candidate inspected only via archive; no merge/commit | **PASS** | ignored `build/ticket-pr20-c0dd5f8`; tracked worktree clean |
| Ticket source audit (issuance, validation, consumption, handler) | **PASS** | exact code locations in §2; source hashes in §1.4 |
| Same-process concurrent exact-`jti` fence | **PASS** (prior diagnosis, cited) | S1: 1/32 accepted, 31 duplicate |
| Same-process seat/capacity fence | **PASS** (prior diagnosis, cited) | S8/S9 |
| Cross-instance same-`jti` one-time | **FAIL** (prior diagnosis; current candidate) | S2: both accepted |
| Restart same-`jti` one-time | **FAIL** (prior diagnosis; current candidate) | S3: fresh registry accepted |
| Cross-instance seat/capacity | **FAIL** (prior diagnosis; current candidate) | S4/S5 |
| Supersession at table gate | **FAIL** (prior diagnosis; current candidate) | S7: old ticket displaced newer |
| Isolated PG schema/concurrency/rollback/restart probes | **PASS** | §7; native exits and counts |
| Proposed durable admission constraints | **PASS** (schema probe only) | P7–P11 simplified; P13–P22 full final DDL; P12 old-predicate control |
| Final DDL seat NULL/range/spectator rejection | **PASS** (full-DDL SQL probe) | P12/P13; old predicate accepted `PLAYER NULL`, final rejected all four invalid cases |
| Terminal-only retention cleanup | **PASS** (full-DDL SQL probe) | P14/P15; old predicate deleted `LIVE`, corrected predicate did not; release+grace allowed delete |
| Long-lived `LIVE` crossing `retain_until` | **PASS** (full-DDL SQL probe) | P15; live row retained and replacement blocked |
| Cleanup vs handover under room lock | **PASS** (full-DDL SQL probe) | P16/P17; both orders safe |
| Durable `admission_seq` ordering | **PASS** (full-DDL SQL probe) | P18–P21; older commit/WELCOME never overwrote newer |
| `request_id` uniqueness/binding | **PASS** (full-DDL SQL probe) | P22; duplicate request id rejected |
| Lobby `ticket_generation` A/B freshness model | **PASS** (simplified SQL model) | P23–P27; new `LIVE`, old `STALE_GENERATION` even with larger local seq |
| Existing LIVE / owner-epoch lease fencing | **PASS** (simplified SQL model) | P28/P29; `LEASE_HELD` until expiry; old stale after takeover |
| Confirm transaction rollback | **PASS** (simplified SQL model) | P30; live gen2 restored, no durable gen3; abandoned local `RESERVED` must be released by the adapter |
| Lobby/live state across PG restart | **PASS** (simplified SQL model) | P31; old ticket stale after restart |
| Actor overlapping pending-token model | **PASS** (Node model) | P32; 22/22 scenarios, stale fail/complete ignored |
| Local-seq-only A/B inversion vs freshness ordering | **PASS** (Node model) | P33; old wins by seq, new wins by ticket generation; caller's failure reproduced and corrected |
| Actor+D0 `lobbyLive` coordination | **PASS** (Node model) | P34; 49/49 scenarios, 0 failures; reported interleaving oldCanCommand=false |
| D0 committed/unknown not lost by stale token | **PASS** (Node model) | P34 S1/S4/S9; B failure recovers committed A or no authority |
| Compensation vs newer live | **PASS** (Node model) | P34 S5; `ReleaseAdmission` no-op clears nothing newer |
| Restart with Lobby live but missing local admission | **PASS** (Node model) | P34 S6; no old command authority |
| D0 sent/in-flight blocks old before response | **PASS** (Node model) | P35 F1/F4/F5; `NO_RESTORE_INFLIGHT`, oldCanCommand=false; only a durable cancel tombstone can later restore old |
| Reported caller in-flight reproducer | **FAIL (defect reproduced)** | P36 exit **1**, `oldCanCommand=true`; retained as failure baseline |
| Cancel tombstone vs Confirm race | **PASS** (Node model) | P38 29/29; confirm-first `ALREADY_COMMITTED`/old false; cancel-first late Confirm `CANCELLED`; absence not terminal; restart persists |
| Tombstone cleanup vs late Confirm | **PASS** (Node model) | P41 24/24; after deletion late Confirm `CANCELLED`/`DEADLINE_EXPIRED`; old safe |
| Deadline/cancel/cleanup SQL predicates | **PASS** (real PG) | P42; cleanup before=0, after=1; boundary `DEADLINE_EXPIRED`; missing record failed closed; DB-time checks |
| Committed result precedence over deadline | **PASS** (Node + real PG) | P44/P45; commit before deadline, retry at/after → `REPLAYED`; Cancel → `ALREADY_COMMITTED`; no duplicate effect |
| Late `REJECTED` row with past deadline | **PASS** (real PG) | P45; old `d0_deadline > created_at` CHECK rejects (exit 3); final allows (exit 0) |
| Caller review-deadline divergence | **FAIL (defect reproduced/observed)** | P43 exit 0 but prints retry `DEADLINE_EXPIRED`; retained as divergence baseline |
| C# Lobby result-precedence adapter | **NOT_RUN** | model/DDL only |
| Result/request cleanup with consumption marker | **PASS** (Node + real PG) | P46/P47; REPLAYED/ALREADY_COMMITTED after request/result cleanup |
| All terminal evidence cleaned | **PASS** (Node + real PG) | P46/P47; RESULT_EXPIRED, never DEADLINE_EXPIRED/new commit; actor no old |
| Concurrent cleanup/Confirm | **PASS** (real PG) | P48; B waited ~1.69 s, then REPLAYED from marker |
| Generic vs unified execution order | **PASS** (Node + PG) | P49 exact `general=DEADLINE_EXPIRED`, `detail=REPLAYED`; only unified order used |
| Marker/result conflict | **PASS** (Node + PG) | P47 T5b/T6 `TERMINAL_EVIDENCE_INCONSISTENT`; no deadline/commit |
| Three-witness request/result/marker conflict | **PASS** (Node + strict PG) | P50/P51; request CANCELLED + result/marker COMMITTED → `TERMINAL_EVIDENCE_INCONSISTENT`; SQL assertions |
| Result original fields (result_code/live id/generation) | **PASS** (Node + strict PG) | result_code: P50 W5, P53 C16; live id: P50 W4, P51 C6, P53 C17; generation: P53 C10/C17. P51 C5 is result_code only and C6 is live id only; neither tests generation. Mismatch fails closed. |
| Strict schema concurrent retry | **PASS** (real PG output) | P52; B waited ~1.71 s then `REPLAYED`; native exit not captured |
| Full binding incl. jti/account/room/assignment/session/owner | **PASS** (real PG) | P53; each row's jti conflict × Confirm/Cancel/Lookup → `REQUEST_ID_CONFLICT`; fields C5–C9 |
| lobby_admission_generation conflict | **PASS** (real PG) | P53 C10; Confirm/Cancel/Lookup `TERMINAL_EVIDENCE_INCONSISTENT` |
| Full-binding concurrent retry | **PASS** (real PG output) | P54; B waited ~1.70 s then `REPLAYED`; native exit not captured |
| NULL/missing binding arguments | **PASS** (real PG) | P55; 27 INVALID_BINDING assertions across 9 fields × Confirm/Cancel/Lookup; exact binding control still succeeds |
| Reported caller tombstone-cleanup reproducer | **FAIL (defect reproduced)** | P40 exit **1**, `late=COMMITTED`; retained as failure baseline |
| C# deadline/cancel/cleanup integration | **NOT_RUN** | model/DDL only |
| Reported caller query-race reproducer | **FAIL (defect reproduced)** | P37 exit **1**, `lateResult=COMMITTED`; retained as failure baseline |
| Outbox DDL persistent `NOT_SENT` | **PASS** (real PG) | P39; final CHECK accepts it, old CHECK rejects; invalid state rejects |
| Outbox unresolved/crash recovery | **PASS** (real PG) | P39; `PENDING_SEND/SENDING/IN_FLIGHT` survive restart then map to `UNKNOWN`; `NOT_SENT`/`COMMITTED` persist |
| C# Lobby Confirm/Cancel transaction / outbox adapter | **NOT_RUN** | model/DDL only; no production code |
| C# actor+D0 integration / actual RPC | **NOT_RUN** | model only; no production code |
| Actor `HANDOVER_PENDING`/per-command generation fence | **NOT_RUN** | design only; no C#/WSS implementation |
| WELCOME/DB/failed-D crash windows on real sockets | **NOT_RUN** | design only |
| Real two-instance WSS + shared PG | **NOT_RUN** | no candidate/design implementation |
| Real Lobby HTTPS issuance + WSS consumption | **NOT_RUN** | no internal endpoint implemented |
| Real candidate C# build/tests for the new design | **NOT_RUN** | design only |
| Implementation of durable shared consumption | **NOT_RUN** | no production code changed |
| Security gap fixed | **NOT CLAIMED / NOT_RUN** | per-process fence remains |
| G3 cross-instance/restart ticket guarantee | **BLOCKED** | no durable shared authority wired |
| G2–G5 gates | **NOT_RUN / not approved** | V4 remains production |

No `PASS` above means “ticket is globally one-time in the candidate.” Only the schema-level
probes passed.

---

## 10. Remaining product / architecture decisions

These must be decided by the caller/product owner before implementation. Each changes the design
or its deployment claims.

1. **Is global one-time semantics actually required?** If not, write an explicit single-instance
   / no-restart-within-TTL scope restriction and stop calling the ticket globally one-time. O1 is
   not a security fix.
2. **Authority choice.** O3 (Lobby consume API) is recommended. O2 (TableService-only ledger)
   leaves old-ticket-after-new-issuance open. O4 (shared-PG function) gives one transaction but
   changes schema ownership/privileges. Decide before file scopes are frozen.
3. **Lobby availability coupling.** O3 makes Lobby a synchronous dependency for new admissions.
   Is it acceptable that a Lobby outage blocks new tables (existing games continue)? If not,
   choose O2 and explicitly accept the freshness gap.
4. **Burned-ticket UX.** In O3, a Lobby commit followed by a TableService local failure spends
   the ticket without a WELCOME. Is “obtain a fresh ticket” acceptable, or is a two-phase local
   capacity reservation with TTL required? The latter is more code and more edge cases.
5. **Same-account multi-device takeover and ordering.** Is a fresh ticket for the same
   assignment allowed to displace the previous live socket only after WELCOME? Confirm that a
   newer `RESERVED` blocks an older `Commit`/WELCOME (the fail-closed choice in §5.5), or define a
   different tie-break. Define whether one account may have more than one live spectator socket
   per room; the proposed DDL says no.
6. **Seat vs spectator capacity authority.** Lobby has an assignment quota; TableService has a
   live-socket cap. Should they be the same number, and which one rejects first? Align them or
   document the two-stage behavior.
7. **Close/error mapping.** Keep all ticket refusals at 4401 (anti-enumeration), or expose
   `ROOM_FULL`/`SPECTATOR_LIMIT`/`ROOM_CLOSED` for UX? This touches the frozen protocol error
   set and needs review.
8. **Internal service authentication.** mTLS vs HMAC, key rotation, listener/network isolation,
   request replay window. This is security-critical; a public consume route is not acceptable.
9. **Retention and quotas.** Ticket TTL (60 s default / 300 s max), consumption retention,
   admission retention, **terminal-only cleanup grace**, per-room/global admission quota, and what
   to do when the quota is reached (fail closed). Do not let an operator shorten retention below
   `ticket.exp + skew`; live rows are never age-deleted.
10. **Clock authority.** DB `clock_timestamp()` vs service UTC for `exp`, `superseded_at`, and
    admission TTL. Pick one policy and bound the skew; do not mix without a written rule.
11. **DB topology.** Are Lobby and TableService databases allowed to share a PostgreSQL
    cluster/database? O3 does not require it; O4 does. If separate, O4 is unavailable.
12. **Room lease single-owner assumption.** The design assumes `PostgresTableHostRegistry`
    enforces one live owner per room and a new `owner_epoch` fences old admissions. Confirm this
    is the deployment invariant; if multiple owners per room are allowed, the epoch cleanup is
    insufficient.
13. **Audit/privacy.** How long to retain jti/request/session correlation, and who can query it.
    Never put the compact ticket or private key in logs.
14. **Authority-fencing protocol.** Does the client accept that WELCOME is not command authority
    until the actor switch, and that commands during `HANDOVER_PENDING` get a retryable
    `ADMISSION_PENDING`? This needs a protocol/client decision and new stable reason codes.
15. **Unknown-commit UX.** On Lobby timeout, is the client willing to wait for the same
    `request_id` resolution, or should the admission fail fast and require a fresh ticket? The
    attempt table and retry budget must match.
16. **Cleanup execution model.** Per-room owner sweep under the lease row (`FOR UPDATE`) vs a
    global `SKIP LOCKED` cleaner with re-check. The latter needs an explicit proof that it cannot
    delete a row a concurrent handover is about to use.
17. **Request-id generation/binding.** Who generates `request_id` (TableService admission
    attempt), its lifetime, and whether it is reused across process restarts. The unique
    `(request_id)` plus `(jti)` binding is only safe if the generator never reuses a value for a
    different tuple.
18. **Spectator live uniqueness.** Is one live spectator socket per `(room, account)` the product
    rule? If not, remove `uq_admission_live_spectator_account` and define capacity/eviction for
    multiple devices.
19. **`ticket_generation` transport.** Add a signed `ticket_generation` claim (changes the frozen
    JWT allowlist) or return it only as an authenticated Lobby consume result. A claim survives a
    lost response and is auditable; a response-only field is less contractual.
20. **Strict old-ticket rejection.** If a newer ticket is issued but never consumed, the older
    pending admission is rejected and the seat may stay idle until the new ticket is used or
    expires. Is that acceptable, or should issuance alone not invalidate pending old admissions
    (which would reopen the A/B inversion)?
21. **Second Lobby call.** `ConfirmAdmission` makes Lobby a required synchronous dependency after
    WELCOME. Is that acceptable, and what is the timeout/compensation budget? If not, freshness
    cannot be closed with O3; revisit O4/O5.
22. **Lobby live lease.** `lobby.live_admission` needs `table_owner_epoch`/`lease_expires_at`
    renewal tied to the TableService room lease. Define the heartbeat, expiry, and takeover
    window; do not let a stale live row block forever.
23. **Actor pending token policy.** Confirm that a newer pending replaces an older one, that
    `FailHandover` restores only the last confirmed authority, and that an older token is ignored.
    Define the client retry/close behavior for `ADMISSION_PENDING`.
24. **D0 unknown/late resolution.** Define the query/retry API, timeout budget, and how a late
    `COMMITTED` for a stale token is recorded/audited. A late result must never overwrite a newer
    `lobbyLive`; an unknown D0 must block old commands.
25. **Compensation policy.** Confirm that `ReleaseAdmission` is conditional and never restores
    old or clears a newer live; if it succeeds, the seat is unowned until a fresh ticket. If it
    fails, fault the seat closed rather than resuming old.
26. **Restart recovery source of truth.** On TableService restart, Lobby `live_admission` is the
    floor. If the corresponding local admission is missing/unconfirmed, the actor has no command
    authority; old local state must never be the default.
27. **D0 outbox durability.** Which transaction/queue persists `PENDING_SEND`/`SENDING` before the
    network write, how it is updated to `IN_FLIGHT`, and how startup loads unresolved rows. The
    outbox must be at least as durable as the room lease; an in-memory token is insufficient.
28. **Send proof.** Define exactly when the transport can mark `NOT_SENT` (no request bytes
    written). Any ambiguous TLS/HTTP write is `UNKNOWN` and blocks old; do not infer from a generic
    connect exception.
29. **Reconciliation.** Define the query/retry budget for `SENDING/IN_FLIGHT/UNKNOWN`, who may
    answer (Lobby by `request_id`), and how a late result is audited when a newer live exists.
30. **Cancel tombstone retention.** A tombstone must outlive every delayed request (ticket TTL +
    skew + network/retry/restart horizon). Define the cleanup job and forbid deletion before
    `retain_until`; a later absent row is not a rejection.
31. **Cancel API semantics.** Confirm that `CancelAdmission` shares the assignment/live/request
    lock with Confirm, does not modify a committed live row, and returns
    `CANCELLED|ALREADY_COMMITTED|REPLAYED_CANCELLED|REQUEST_ID_CONFLICT`. Clients must not infer a
    rejection from an empty lookup.
32. **Binding and retry.** Define the full `(request_id, admission_id, jti, ticket_generation,
    account, room, assignment, session, owner_epoch)` binding and the retry/response-lost path;
    a mismatch must never create a tombstone or clear a live row.
33. **D0 deadline source.** Confirm `d0_deadline = LEAST(ticket.exp, consume DB time + max
    admission window)` and that no caller field can extend it. Define the maximum window, the
    exact boundary (`now >= deadline` rejects), and the DB clock authority.
34. **Consumption/deadline retention.** Keep `d0_deadline`/`cancelled_at` at least through the
    tombstone/ticket/request horizon. If the authoritative consumption row is missing, Confirm
    must fail closed (`AUTHORITATIVE_RECORD_MISSING`); absence is never success.
35. **Cleanup locking/DB time.** Cleanup must take the same assignment/request lock as
    Confirm/Cancel, use DB `clock_timestamp()`, and delete tombstones only after deadline+grace.
    Define how a delayed Confirm that races cleanup is rejected by the deadline/cancel marker.
36. **Terminal-result precedence.** An exact-binding `COMMITTED`/`CANCELLED`/`REJECTED` result must
    be replayed before deadline checks; deadline applies only to a request with no prior terminal
    result. Define the idempotency/result-retention horizon and the response for an out-of-horizon
    retry (must not be mistaken for “no side effect”).
37. **Terminal-result persistence.** The immutable result is `lobby.table_ticket_d0_result`,
    keyed by `request_id` and containing the full binding plus the original result. It must survive
    request-row cleanup/restart long enough to replay; if both result and any equivalent retained
    terminal evidence are missing, return `AUTHORITATIVE_RECORD_MISSING`, never commit.
38. **Late rejection DDL.** A terminal `REJECTED` row created after the deadline has
    `d0_deadline <= created_at`; the schema must not impose `d0_deadline > created_at`. Separate
    “original deadline” from “row creation time” in the migration and tests.
39. **Atomic cleanup invariant.** Request/result/consumption cleanup must share the request lock
    and preserve at least one full-binding terminal witness until all three are deleted together.
    Never delete a result while the consumption marker is unset; never reset a terminal marker to
    NULL. If evidence is inconsistent/missing, fail closed with
    `RESULT_EXPIRED`/`TERMINAL_EVIDENCE_INCONSISTENT`; do not restore old.
40. **Unknown terminal evidence.** RESULT_EXPIRED, TERMINAL_EVIDENCE_INCONSISTENT, and AUTHORITATIVE_RECORD_MISSING are an unknown/fail-closed family: never treat them as DEADLINE_EXPIRED, never write a new REJECTED, and never restore old. Only a positive CANCELLED/REJECTED/proved-unsent with no unresolved attempt allows old restoration.
41. **Single execution order.** Confirm, Cancel, and lookup must use the §5.3B/§5.3C order exactly: lock → full binding → collect all terminal evidence/consistency → marker/result replay → missing/deadline → commit. No alternate generic path may skip the consumption marker; the Node order model must compare both paths and assert the old path is rejected.
42. **Original-result field equality and error precedence.** If result and marker both exist, `result_code` (for REJECTED) and `live_admission_id`/`lobby_admission_generation` (for COMMITTED) must be equal before replay. Error order: NULL/missing binding argument → `INVALID_BINDING`; any full-binding mismatch → `REQUEST_ID_CONFLICT`; only after binding passes, state/result-field mismatch → `TERMINAL_EVIDENCE_INCONSISTENT`; no side effect.
43. **Full binding equality.** Every existing result/request/consumption row must match the complete `(request_id, admission_id, jti, ticket_generation, account, room, assignment, session, owner_epoch)` binding. `jti` is mandatory; a partial check is insufficient and must not be treated as satisfying this invariant.
44. **NULL binding arguments.** All required binding arguments, including `request_id`, must be non-NULL. Confirm/Cancel/Lookup return `INVALID_BINDING` for any NULL/missing argument; SQL must use explicit non-null checks or `IS DISTINCT FROM`, never `<>`/UNKNOWN as a wildcard.

---

## 11. Non-actions and handoff

- **No production code, tests, verifier, `Mahjong.sln`, Unity `Resources`/Burst, machine trust,
  or other worktrees were changed.**
- **No candidate code was merged, checked out over this worktree, or committed.** The archive is
  an inspection input only.
- **No push, PR, delegation, release, gate approval, or scheduler operation.**
- **No shared database was touched.** All three PostgreSQL clusters were created, used, stopped,
  and deleted by this task; logs remain in the ignored build directory.
- **No claim that the security gap is fixed.** The candidate's registry is still a per-process
  fence; the durable design is unimplemented.
- **V4 remains the production architecture; G2–G5 remain unapproved.**

Recommended next step for the caller: decide §10 items 1–5, 14–17, and 19–44, then dispatch
implementation against the file scopes in §6, with the §6.5 test matrix. Acceptance must include a
real two-instance WSS + Lobby + shared PostgreSQL probe (exact-`jti` replay, restart, cross-account
seat, spectator cap, superseded ticket, terminal assignment), plus a real reverse-WELCOME /
`HANDOVER_PENDING` test, the A/B delayed-B freshness interleaving, and a D0
sent/in-flight/unknown/late-response test with a durable outbox, Cancel tombstone, deadline/cleanup
fence, and committed-result replay. Until that exists, the cross-instance/restart one-time
guarantee is **not established**.
