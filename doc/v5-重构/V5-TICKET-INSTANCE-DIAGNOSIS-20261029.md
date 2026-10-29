# V5 Ticket Instance Diagnosis — In-Memory jti Consumption vs Cross-Instance/Restart

Date: 2026-10-29
Status: **DIAGNOSIS COMPLETE — single-instance behavior PASS; cross-instance/restart one-time guarantee FAIL at the component boundary; real multi-instance/PostgreSQL validation NOT_RUN.**
Candidate: `eeaa90dd579bc07e85641b0c32949ada90b55ae9` (frozen, unaccepted PR20 head).
Accepted baseline: `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (`origin/v5`).
Report commit: local report-only commit on `wt/v5-ticket-instance-diagnosis-20261029`; no candidate code, production code, tests, verifier, `Mahjong.sln`, Unity Resources/Burst, or machine trust was changed.

This report does not change the gate state: **V4 remains the production architecture; G2–G5 are not approved.** It does not merge, commit, or endorse candidate code, and it does not claim a fix.

---

## 1. Executive result

| Area | Result | Boundary |
|---|---|---|
| Same process, concurrent duplicate `jti` | **PASS** | 32 concurrent `Consume` calls: 1 `Accepted`, 31 `DuplicateJti`, 0 other |
| Same process, exact-jti replay after `Release` | **PASS** | exact replay remains `DuplicateJti`; a fresh jti for the same assignment tuple is accepted |
| Same process, cross-account seat fence | **PASS** | second account for the same `(room, seat)` gets `SeatOccupiedByOtherAccount` |
| Same process, spectator capacity | **PASS** | `MaxSpectatorsPerRoom=1` rejects the second spectator |
| Two independent registries, same `jti` | **FAIL** | both accept and both commit the same ticket |
| Restart (fresh registry, same ticket) | **FAIL** | fresh registry accepts and commits the already-consumed jti |
| Two instances, same seat, different accounts | **FAIL** | both accept/commit; each instance reports its own live owner |
| Two instances, `MaxSpectatorsPerRoom=1` | **FAIL** | each accepts one spectator; total 2 |
| Issuance supersession (`SupersededAt`) | **FAIL (related, single-instance)** | an older but still-unconsumed ticket is accepted and displaces the newer connection because the table gate never reads the Lobby issuance row |
| Real two-instance WSS + PostgreSQL | **BLOCKED / NOT_RUN** | no shared PostgreSQL used; no real two-process admission probe |
| Real Lobby HTTPS issuance + WSS consumption | **NOT_RUN** | candidate's own B1 run reports `lobby.table_ticket_consumption=0` |
| G3 / cross-instance restart guarantee | **BLOCKED** | no durable shared ticket-consumption authority wired into the WSS admission path |

**Bottom line:** the candidate's `ITicketConsumptionRegistry` is a correct **per-process** one-time fence. It is not a global one-time fence. The contract text says the table ticket is “one-time” and short-lived; the implementation enforces that only inside one process's memory. A ticket can be consumed again by another TableService instance or after a restart while the ticket is still within its TTL. Whether a given deployment exposes two *simultaneous* live seats also depends on the separate Postgres room-lease invariant; that lease is a mitigation, not a ticket one-time guarantee. No real multi-instance/PostgreSQL test was run here.

---

## 2. Frozen inputs and method

### 2.1 Inputs

| Item | Value |
|---|---|
| Candidate | `eeaa90dd579bc07e85641b0c32949ada90b55ae9` |
| Candidate tree | `8f2543f271ae9e2854dc16c927eb667bf80b6a82` |
| Candidate parents | `59bee8e6fd2c3d38799bed73634c37d6cceea91d` (first), `565568cec260453cd76b8dcf238109efe420bfeb` (second) |
| Candidate subject | `docs(v5): integrate B1 lookup digest diagnosis` |
| Accepted baseline | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| Baseline tree | `61f50856aedd78e0f8609e282baa8313ae8f2a14` |
| Baseline is ancestor of candidate | `git merge-base --is-ancestor 8fd7c6c eeaa90d` → native exit **0** |
| Commits in candidate not in baseline | 328 (`git rev-list --count 8fd7c6c..eeaa90d`) |
| Worktree | `D:\majiang-wt\ticket-instance-ds-20261029` |
| Branch | `wt/v5-ticket-instance-diagnosis-20261029` |
| Isolated archive | `D:\majiang-wt\ticket-instance-ds-20261029\build\ticket-instance-ds-20261029\candidate` (ignored by `build/`) |

The candidate was inspected only through `git archive` into the ignored directory above. It was not merged, checked out over the worktree, or committed. No candidate source was edited.

### 2.2 Commands used (reproducible)

From the worktree:

```powershell
$base = 'D:\majiang-wt\ticket-instance-ds-20261029\build\ticket-instance-ds-20261029'
New-Item -ItemType Directory -Force "$base\candidate" | Out-Null

git archive --format=tar -o "$base\candidate.tar" `
  eeaa90dd579bc07e85641b0c32949ada90b55ae9
tar -xf "$base\candidate.tar" -C "$base\candidate"

# Process-local temp/home; existing NuGet cache is only read.
$env:TEMP = "$base\temp"; $env:TMP = "$base\temp"
$env:DOTNET_CLI_HOME = "$base\dotnet-home"
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"

dotnet build "$base\candidate\dotnet\src\Mahjong.TableService\Mahjong.TableService.csproj" `
  -c Release --nologo -v:minimal

dotnet build "$base\probe\TicketInstanceProbe.csproj" `
  -c Release --nologo -v:minimal

$dll = "$base\probe\bin\Release\net8.0\TicketInstanceProbe.dll"
dotnet $dll "$base\probe-results-1.json"
dotnet $dll "$base\probe-results-2.json"
```

The probe is a separate console project that references the candidate's `Mahjong.TableService.csproj`. It creates the candidate's real public `InMemoryTicketConsumptionRegistry` and `ValidatedTableTicket` objects directly. It does not use PostgreSQL, a network listener, candidate tests, or production files.

### 2.3 Native exits and counts

| Command | Native exit | Counts / result |
|---|---:|---|
| `git archive` + `tar` | **0** | archive extracted; 34 top-level entries |
| `dotnet build Mahjong.TableService.csproj -c Release` | **0** | 0 warnings, 0 errors |
| First `dotnet build TicketInstanceProbe.csproj` | **1** | one nullable-annotation compile error (`CS8604`), fixed in probe only; no candidate change |
| Final `dotnet build TicketInstanceProbe.csproj` | **0** | 0 warnings, 0 errors |
| Probe run 1 | **0** | `failures=0`, `probe_exit=0`; 9 scenarios |
| Probe run 2 | **0** | byte-identical scenario output; `failures=0`, `probe_exit=0` |
| `dotnet --info` | **0** | SDK 8.0.426, runtime 8.0.32, Windows x64 |

The intermediate probe build failure is recorded, not hidden: `build-probe.log` SHA-256 `F3E385CA70363FF99DC5735B2D0D1B90687E549F4F226D38208E37C8498364FB`. The final build log is `build-probe-3.log` SHA-256 `5B3C9AFD7927CC7A84221F2A2DA910218DEEEDA861D995C38EA54A424F85B550`.

---

## 3. Source trace: issuance, verification, consumption

### 3.1 Issuance (Lobby)

`dotnet/src/Mahjong.LobbyService/LobbyApplication.Tickets.cs`

- `IssueTableTicketAsync` computes `payloadDigest` from `("table.ticket.issue.v1", assignmentId)` and performs receipt-first replay before mutable checks (`:22-31`).
- It requires an active assignment owned by the account and checks the account has no other active assignment (`:100-143`).
- It marks the previous active ticket for the same assignment `SupersededAt = now` (`:145-153`), then creates a fresh `jti` (`:155`), builds the frozen claim set (`:156-169`), signs it through `_tickets.Issue` (`:170`), and inserts a durable `lobby.table_ticket` row (`:171-183`).
- Default TTL is 60 seconds, maximum 300 seconds; clock skew is 5 seconds (`LobbyOptions.cs:26-30`, `:113-125`).

`dotnet/src/Mahjong.LobbyService/PostgresLobbyStore.cs:1151-1173` inserts the ticket row. The schema (`Migrations/001_lobby.sql:159-202`) has:

```sql
CREATE TABLE lobby.table_ticket (jti text PRIMARY KEY, ... consumed_at, superseded_at ...);
CREATE UNIQUE INDEX uq_table_ticket_active_assignment
  ON lobby.table_ticket (assignment_id)
  WHERE consumed_at IS NULL AND superseded_at IS NULL;
CREATE TABLE lobby.table_ticket_consumption (
  jti text NOT NULL REFERENCES lobby.table_ticket (jti),
  ...
  CONSTRAINT pk_table_ticket_consumption PRIMARY KEY (jti)
);
```

The issuance side therefore has a real durable `jti`, a durable supersession marker, and a durable unique consumption ledger. The missing link is that **the TableService admission path never calls the Lobby consumer**.

`dotnet/src/Mahjong.LobbyService/LobbyHttp.cs:255-268` exposes only:

```text
POST /v1/table-tickets   -> IssueTableTicketAsync
```

There is no `/consume` route. `ConsumeTableTicketAsync` exists only as the application port. `git grep -i consume .../LobbyHttp.cs` returns **exit 1** (no match). The README states this explicitly: “`TableTicketConsumer`（应用端口，当前无公网 HTTP 路由）” (`README.md:87`). The method's own comment calls it a “Reference one-time consumption boundary for the future TableService handshake. The real WSS handshake is P5.1 and is NOT integrated here.” (`LobbyApplication.Tickets.cs:204-209`).

### 3.2 Verification (TableService)

`dotnet/src/Mahjong.TableService/TableTicketValidator.cs`

- The validator checks compact JWS shape, strict canonical base64url, header/payload claim allowlists (no unknown/duplicate claims), `alg=ES256`, `typ=JWT`, active `kid`, ES256 signature, issuer/audience exact match, id charset, PLAYER seat 0..3, spectator no seat, protocol/ruleset/projection versions, `exp > iat`, TTL cap, future-`iat`/`nbf`, and expiry with skew (`:257-265`, `:292-457`).
- It is deliberately read-only: “It never signs, never calls the Lobby runtime and never holds a private key” (`:262-264`).
- It has **no stateful `jti` check**. It cannot see `lobby.table_ticket.consumed_at`, `superseded_at`, assignment status, or the Lobby consumption table.

`dotnet/src/Mahjong.TableService/TableSessionHandler.cs:906-923` calls `TableTicketValidator.Validate` with an expectation containing only the route room; role/seat/account are accepted from the signed ticket. `:925-951` acquires the room host; `:953-980` checks the host's actor lifecycle; only then does `:984` call `_consumption.Consume(ticket, sessionId, DateTimeOffset.UtcNow)`.

The host dependency is real: `GetOrAddAsync` may fail with `LEASE_UNAVAILABLE` before `Consume`. In Postgres store mode, `PostgresTableHostRegistry` acquires a per-room lease and fails closed if another live owner holds it (`PostgresTableHostRegistry.cs:22-51`, `:250-262`). This is a separate room-authority fence; it is not a ticket-consumption fence.

### 3.3 Consumption (TableService, in-memory)

`dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs`

- The interface documents the two-phase admission: provisional `Consume`, final `CommitAdmission`, `EvictOnWelcome`, `RollbackSeatHandover`, `Release` (`:137-206`).
- The only implementation is `InMemoryTicketConsumptionRegistry` (`:208-209`). The class comment says: “In-memory P5.1 implementation. Cross-instance uniqueness arrives with P5.2.” (`:208`). P5.2 exists, but this sentence was never converted into a durable ticket registry.
- State is a private per-instance set of dictionaries behind one `_gate` (`:211-230`):
  - `_consumedJtis: Dictionary<string, ConsumedTicket>`
  - `_assignments: Dictionary<string, AssignmentRecord>`
  - `_seatBindings: Dictionary<string, SeatLease>`
  - `_spectatorCounts: Dictionary<string, int>`
  - `_sessions: Dictionary<string, SessionRecord>`
- `Consume` takes `_gate`, calls `PurgeExpired(nowMs)`, rejects an already-retained `jti` (`:266-275`), checks the assignment tuple (`:277-300`), checks the live seat owner (`:302-328`), checks spectator capacity (`:303-310`), fails closed at `MaxRetainedConsumedTickets` (`:330-334`), records the jti until `ticket.ExpiresAt + ClockSkewSeconds` (`:336-371`), records the assignment (`:373-391`), and records the session (`:393-400`).
- `CommitAdmission` finalizes the spectator or seat handover and marks the jti committed (`:406-471`). `EvictOnWelcome` is the single displacement decision after WELCOME (`:485-565`). `RollbackSeatHandover` removes only that session's not-yet-welcomed commit entry (`:574-618`). `Release` frees the session/seat/spectator resources but **does not remove the jti or assignment evidence** (`:620-695`, especially `:689-693`). `PurgeExpired` removes records only after expiry + skew (`:740-780`).

`dotnet/src/Mahjong.TableService/TableServiceWebHost.cs:109-110` registers exactly one `InMemoryTicketConsumptionRegistry` as a singleton per built `WebApplication`:

```csharp
builder.Services.AddSingleton<ITicketConsumptionRegistry>(_ =>
    new InMemoryTicketConsumptionRegistry(options));
```

The same registration is used in both store modes. `TABLESERVICE_STORE=postgres` changes the **room host** to `PostgresTableHostRegistry` (`TableServiceWebHost.cs:122-161`), but not the ticket registry. The default store mode is `Memory` (`TableServiceOptions.cs:112`; `Program.cs:114-132`).

`git grep InMemoryTicketConsumptionRegistry eeaa90d -- dotnet/src` returns only:

- `TableServiceWebHost.cs:110` registration
- `TableTicketConsumptionRegistry.cs:209` class declaration
- `TableTicketConsumptionRegistry.cs:235` constructor

There is no other `ITicketConsumptionRegistry` implementation in the candidate. `git grep` for a TableService call to `ConsumeTableTicketAsync` finds only the Lobby declaration (`LobbyApplication.Tickets.cs:210`) and README references; the TableService does not reference the Lobby project or its consumer.

### 3.4 What the durable Lobby consumer would do (but is not called)

`LobbyApplication.ConsumeTableTicketAsync` (`LobbyApplication.Tickets.cs:210-417`) does, in one store transaction:

1. Validate the JWT via `_tickets.Validate` (`:221-236`).
2. Reconcile the terminal room projection and reject `ROOM_CLOSED` (`:238-246`).
3. Lock the room, assignment, ticket row, and match ticket in the documented order (`:252-273`).
4. Reject unknown, consumed, superseded, expired, or tuple-mismatched tickets (`:274-302`).
5. Require the assignment to be active and not expired (`:304-322`).
6. Reject a second active assignment for the account (`:324-333`).
7. Reject a seat conflict (`:335-350`).
8. Confirm the match reservation if needed (`:352-379`).
9. Confirm the assignment, insert `lobby.table_ticket_consumption(jti)` and mark `table_ticket.consumed_at` in the same transaction (`:381-404`).

`PostgresLobbyStore.cs:1189-1204` inserts the consumption row; the primary key on `jti` (`001_lobby.sql:197`) and the `FOR UPDATE` reads are the cross-connection/durable fence. The existing P5.3 PostgreSQL verification reports this working on a real isolated PG 16.10: `ConcurrentDuplicateTicketConsumptionCreatesOneConsumptionRow` → 1 success, 1 `TICKET_ALREADY_CONSUMED`, consumption=1 (`V5-P5.3-POSTGRES-VERIFICATION.md:159`), and a restart-write-probe with “persistent receipt + single consumption” (`:100`). **Those are pre-existing repository docs; this report did not re-run PostgreSQL.** The important boundary is that this code is not on the WSS admission path.

The existing P5.3 delivery records the one-time ticket path as PASS only for the in-memory transaction, with PostgreSQL **NOT_RUN** (`V5-P5.3-DELIVERY.md:178`), and explicitly says there is no real TableService linkage: consumption exists only as the application port (`:192`). The candidate's B1 delivery states the same: “TableService 仍用 `InMemoryTicketConsumptionRegistry`；Lobby `table_ticket_consumption` 未接线；跨实例票据消费风险仍在” (`V5-REMOTE-RECEIPT-TICKET-DELIVERY-20261029.md:48`, `:196`). The candidate's B1 gate review lists it as `OPEN` and says G3 requires a persistent authority if cross-instance/restart ticket consumption is required (`CODEX-V5-P54B-B1-GATE-REVIEW-451F4EC-20261029.md:255`). The B1 delivery records `lobbyTicketConsumptionRows=0` for its room (`V5-P5.4b-B1-HTTPS-ADMISSION-DELIVERY-20261029.md:207-210`).

### 3.5 Enforcement matrix

| Property | TableService admission | Lobby durable consumer | Notes |
|---|---|---|---|
| Signature / claims / `aud` / role / seat / room / assignment / versions | **Yes** (`TableTicketValidator.Validate`) | Yes (`_tickets.Validate`) | Cryptographically validated in both |
| Ticket expiry / TTL cap / skew | **Yes** | Yes | Default 60s, max 300s |
| Exact `jti` already consumed | **Yes, per process** (`_consumedJtis`) | **Yes, durable** (`pk_table_ticket_consumption`) | TableService memory only |
| `superseded_at` (newer ticket replaced old) | **No** | **Yes** (`Tickets.cs:284-287`) | Old unspent ticket stays usable at the table gate |
| Assignment active / not expired | **No** | **Yes** (`:304-322`) | Table gate only sees signed claims |
| Room open / terminal | Indirect via host/actor lifecycle | **Yes** (`:256-266`) | Different consistency points |
| Seat occupied by another account | **Yes, per process** (`_seatBindings`) | Yes (assignment/seat conflict) | Not shared across instances |
| Spectator capacity | **Yes, per process** (`_spectatorCounts`) | Yes (assignment/row checks) | Not shared across instances |
| Consumption survives restart | **No** | **Yes** (PG) | Table gate uses a fresh dictionary |
| Consumption shared across instances | **No** | **Yes** (PG PK + row locks) | Not called by TableService |

---

## 4. Deterministic independent probe

### 4.1 Probe design

The probe is at:

```text
build/ticket-instance-ds-20261029/probe/TicketInstanceProbe.csproj
build/ticket-instance-ds-20261029/probe/Program.cs
```

It references the frozen candidate's `Mahjong.TableService.csproj`, constructs real `InMemoryTicketConsumptionRegistry` instances, and uses a frozen `ValidatedTableTicket` with a fixed clock (`2026-10-29T12:00:00Z`). It does not touch PostgreSQL, the network, the repository tests, or production files. Each scenario runs with expected assertions; the process returns 0 only when all assertions hold.

Two independent runs were performed. The scenario output is identical; only the JSON `generated_at_utc` differs.

### 4.2 Results

| ID | Scenario | Expected | Actual | Verdict |
|---|---|---|---|---|
| S1 | One registry, 32 concurrent `Consume` calls for the same `jti`, distinct session ids | one winner | `s1_total=32`, `s1_accepted=1`, `s1_duplicate_jti=31`, `s1_other=0`; commit `Committed` | **PASS** |
| S2 | Two independent registries (two instances), same `jti`, concurrent | both see a fresh ticket | `s2_same_jti_accepted=2`; statuses `Accepted,Accepted`; both commits `Committed` | **FAIL** (cross-instance one-time) |
| S3 | Registry A consumes+commits; a fresh registry B (restart) consumes the same ticket | B must reject | `s3_first=Accepted`, `s3_first_commit=Committed`, `s3_after_restart=Accepted`, `s3_after_restart_commit=Committed` | **FAIL** (restart one-time) |
| S4 | Two instances; account A takes seat 3; account B takes the same seat with a different ticket | at most one owner | `s4_a=Accepted`, `s4_b=Accepted`, both commits `True`; `s4_owner_a=seat-session-a`, `s4_owner_b=seat-session-b` | **FAIL** (cross-instance seat) |
| S5 | Two instances, `MaxSpectatorsPerRoom=1`; one spectator each | global max 1 | `s5_a=Accepted`, `s5_b=Accepted`, `s5_total_accepted_across_instances=2` | **FAIL** (cross-instance capacity) |
| S6 | One registry; consume+commit+release; exact replay; then fresh jti same assignment | exact replay rejected; fresh jti accepted | `s6_exact_replay_after_release=DuplicateJti`, `s6_fresh_jti_same_assignment=Accepted`, `s6_fresh_commit=Committed` | **PASS** (single-instance) |
| S7 | One registry; newer ticket commits+welcomes; older but unconsumed ticket (same assignment/account/seat) arrives later | older ticket should be rejected as superseded | `s7_new=Accepted`, `s7_new_welcome=None`, `s7_old_after_new=Accepted`, `s7_old_commit=Committed`, `s7_old_welcome=DisplacedOther`, `s7_old_displaces=fresh-new-session` | **FAIL** (issuance freshness) |
| S8 | One registry; account A owns seat 0; account B tries same seat | reject B | `s8_a=Accepted`, `s8_a_commit=Committed`, `s8_b=SeatOccupiedByOtherAccount` | **PASS** (single-instance) |
| S9 | One registry, `MaxSpectatorsPerRoom=1`; second spectator | reject second | `s9_a=Accepted`, `s9_b=SpectatorCapacityReached` | **PASS** (single-instance) |

Raw run output is retained at:

```text
build/ticket-instance-ds-20261029/probe-run-1.log
build/ticket-instance-ds-20261029/probe-run-2.log
build/ticket-instance-ds-20261029/probe-results-1.json
build/ticket-instance-ds-20261029/probe-results-2.json
```

Both runs: `failures=0`, `probe_exit=0`, native exit **0**.

### 4.3 What the probe proves and does not prove

**Proves:**

- The candidate's in-memory registry is safe against concurrent duplicate `jti` **within one process**.
- Exact-jti replay remains blocked after session `Release`; a fresh jti for the same assignment is accepted (the intended reconnect semantics).
- Seat ownership and spectator capacity are enforced **within one process**.
- Two independent registry objects have no shared state: the same `jti` can be accepted by both; a restart loses the jti; the same seat can have two local owners; a per-instance spectator cap of 1 becomes 2 across instances.
- An older, still-unconsumed ticket can displace a newer connection within one registry because the registry does not consult Lobby `superseded_at` or ticket issuance time.

**Does not prove:**

- That two real TableService processes, behind a load balancer, both reach `Consume` and both issue WELCOME for the same room. In Postgres store mode, the room lease may reject the second host before `Consume`. That is a **deployment-dependent mitigation**, not a ticket one-time guarantee.
- That a real restart/failover within the ticket TTL produces duplicate live authoritative seats. It does produce a **second consumption of the same ticket**, which is the contract violation; the live-seat consequence depends on lease/authority timing.
- Any PostgreSQL behavior, WSS behavior, Lobby HTTPS behavior, network behavior, or production deployment behavior. Those are **NOT_RUN**.

---

## 5. Deployment assumptions and actual security implications

### 5.1 Current deployment assumptions in the code

1. **The ticket registry is a process singleton, not a shared store.** `TableServiceWebHost.cs:109-110` creates a new `InMemoryTicketConsumptionRegistry` for each built host. A restart or a second host gets an empty registry.
2. **The default store mode is memory.** `TableServiceOptions.StoreMode = TableHostStoreMode.Memory` (`:112`); `TABLESERVICE_STORE` defaults to memory when unset (`Program.cs:114-132`). In memory mode, each process also has an independent room host, so there is no shared room lease either.
3. **Postgres mode shares the room authority, not the ticket authority.** `TABLESERVICE_STORE=postgres` builds `PostgresTableHostRegistry` (`TableServiceWebHost.cs:122-161`), which acquires a per-room lease and fails closed if another live owner holds it (`PostgresTableHostRegistry.cs:22-51`, `:250-262`). It still registers `InMemoryTicketConsumptionRegistry` at `:109-110`.
4. **Lobby owns the durable ticket rows, but the WSS path does not call the Lobby consumer.** `LobbyHttp.cs:255-268` exposes issuance only. `ConsumeTableTicketAsync` is an application port with no route (`LobbyApplication.Tickets.cs:204-209`; `README.md:87`). The candidate's B1 run observed `lobby.table_ticket_consumption=0` (`V5-P5.4b-B1-HTTPS-ADMISSION-DELIVERY-20261029.md:207-210`).
5. **The TTL is short.** Default 60 seconds, max 300 seconds, skew 5 seconds (`LobbyOptions.cs:26-30`; `TableServiceOptions.cs:41-43`). This bounds the replay window but is not a substitute for a one-time fence.

### 5.2 Security implications

**A. Cross-instance/restart replay of a valid ticket (contract violation).**
An attacker or a buggy client holding a valid ticket can present it to a second TableService instance, or to the same service after a restart, and the fresh in-memory registry will accept it. The ticket remains valid for up to 60 seconds by default (up to 300 seconds if configured). The one-time property is therefore “one time per process lifetime”, not “one time per issued ticket”.

**B. Cross-instance seat/capacity duplication (deployment-dependent).**
At the registry boundary, two instances can accept two different accounts for the same `(room, seat)` and can each admit up to `MaxSpectatorsPerRoom` spectators. In the current Postgres room-lease mode, a second instance is normally refused for the same room before `Consume`, so this is not proven to produce two simultaneous authoritative seats. But:
- memory mode has no shared room lease at all;
- a failover/reclaim can give a new instance the room while the old registry state is gone;
- any deployment that routes the same logical room to two independent hosts loses the local fence entirely.

The room lease must not be treated as the ticket one-time guarantee. They are different invariants, with different failure modes.

**C. Superseded tickets are not rejected at the table gate.**
Lobby marks the previous ticket `superseded_at` when it issues a new one (`LobbyApplication.Tickets.cs:145-153`). The table gate only sees the JWT; it never reads that row. Within one process, an older but still-unconsumed ticket is accepted and can displace the newer connection (S7: `DisplacedOther`, displacing `fresh-new-session`). The old ticket's `exp` is still in the future, so the validator accepts it. This is a freshness/revocation gap independent of the cross-instance dictionary issue, though both come from the same missing shared state.

**D. Assignment/room terminal state is not checked at the table gate.**
The table gate validates room from the route and claims, and relies on the host/actor lifecycle (`TableSessionHandler.cs:953-980`). It does not verify the Lobby assignment is still `Reserved|Confirmed`, not expired, and not cancelled by terminal reconciliation. Lobby's durable consumer would (`LobbyApplication.Tickets.cs:304-322`). A ticket for an assignment that Lobby has cancelled can still be presented until its `exp`, subject to whether the room host/actor is still serving. The exact end-to-end consequence depends on room lifecycle timing; it is not proven by this probe.

**E. Spectator count and assignment tuple binding reset on restart.**
`_spectatorCounts`, `_assignments`, and `_seatBindings` are memory dictionaries. A restart resets the count and the tuple binding. This is another reason a fresh registry can accept a ticket the previous process would have rejected.

**F. What is not at risk in the tested single-process path.**
Within one process, the registry's lock and exact-jti check correctly serialize concurrent duplicate admissions (S1), keep the jti spent after `Release` (S6), reject a different account on the same seat (S8), and enforce the spectator cap (S9). Those are real, tested properties and should not be dismissed.

### 5.3 Severity wording

- **Contract/security gap: HIGH for a multi-instance or restart-within-TTL deployment.** The “one-time” ticket claim is not true there.
- **Production impact: deployment-dependent, not established end-to-end.** A correctly configured single-owner Postgres deployment may not expose two simultaneous live seats because of the room lease; this report does not claim it does. The durable one-time guarantee is still absent, and the candidate's own B1 gate lists it as OPEN.
- **No exploit was executed against a real Lobby/WSS/PG deployment.** The probe is a deterministic component-level proof of the state-sharing defect.

---

## 6. Contract requirements and minimal safe options

### 6.1 Requirements if “one-time” is a real contract

1. **A durable, shared consumption authority.** If the ticket is one-time across instances and restarts, the exact-`jti` fence must live in a store all admission paths share and that survives process restart. A process dictionary cannot satisfy this.
2. **Atomic admission coupling.** The consumption record must be written/checked in the same transaction or atomic boundary as the seat/handover reservation, before WELCOME. A crash after consuming but before WELCOME must not re-enable the ticket; a crash before consuming must not leave a phantom spent ticket. The current in-memory implementation's `Release` keeps the jti evidence, which is the right direction.
3. **Retention and cleanup.** Keep `jti` records at least until `ticket.exp + clockSkew`, then purge; bound the store and fail closed when full. The current registry does this in memory (`:330-334`, `:336-337`, `:740-780`); a durable version needs the same semantics and an explicit cleanup job/transaction.
4. **Identity and handover semantics.** Bind `(jti, account, role, room, seat, assignment)`; reject an exact-jti replay; allow a fresh jti for the same assignment only under an explicit reconnect/handover rule; never let an older ticket silently win over a newer one. The current in-memory assignment/seat/eviction chain covers much of this per process, but not the issuance freshness.
5. **Supersession/assignment visibility.** If Lobby's `superseded_at` and assignment state are part of the contract, the table gate must be able to observe them (shared durable ledger or an explicit signed issuance epoch). Without this, an old unspent ticket remains usable until `exp`.
6. **No synchronous Lobby runtime call unless the boundary changes.** The taskbook says `TableService` does not synchronously call the Lobby runtime (`V5-0-1-IMPLEMENTATION-TASKS.md:188`). A design that adds a network call to Lobby on every HELLO changes that contract and the failure/availability model; it must be an explicit decision, not an implementation detail.
7. **Data ownership.** Lobby owns `lobby.table_ticket` / `lobby.table_ticket_consumption`; TableService owns `table_store`. A solution should not have TableService write Lobby tables directly without an explicit ownership change.

### 6.2 Minimal safe options (analysis only; none implemented)

| Option | Shape | Single-instance | Cross-instance | Restart | Supersession | Contract impact | Assessment |
|---|---|---|---|---|---|---|---|
| **O1. Operational single-instance constraint** | Pin all admissions for a room to one process; no restart within ticket TTL; drain admissions before restart; do not run multiple memory-mode instances. | PASS | FAIL (not allowed by deployment) | FAIL (unless no restart within TTL) | FAIL | No code; requires explicit scope exclusion | Only honest if G3 explicitly declares cross-instance/restart out of scope. It is a deployment restriction, not a fix. |
| **O2. Durable jti ledger in `table_store`** | TableService-owned table keyed `jti`; check/insert in the admission transaction with the room lease; retain until exp+skew; bounded; fail closed. | PASS | PASS | PASS | Partial (exact jti only) | Schema/port/code; no Lobby call; respects current service boundary | Smallest change that closes the reported cross-instance/restart exact-jti gap. Does not by itself close `superseded_at`/older-ticket freshness. |
| **O3. Lobby as sole consumption authority** | TableService calls an explicit Lobby consume API (or a shared Lobby transaction) and uses the existing `table_ticket_consumption(jti)` primary key. | PASS | PASS | PASS | PASS (Lobby checks `consumed_at`, `superseded_at`, assignment, seat, match) | Cross-service synchronous dependency; changes the “no synchronous Lobby runtime call” boundary; availability/latency coupling | Reuses the already-PG-tested Lobby ledger and closes both exact-jti and supersession/assignment checks. Requires a product/contract decision. |
| **O4. Durable ledger + signed issuance epoch** | O2 plus a monotonic issuance epoch/nonce in the ticket and a durable “latest epoch” check. | PASS | PASS | PASS | PASS if epoch is authoritative | Additional claim/schema/contract; more moving parts | Only needed if the contract requires old-ticket revocation but O3 is not acceptable. Not minimal. |

**Minimal safe reading:** if the requirement is only “the same jti cannot be consumed twice across instances/restarts”, **O2** is the smallest change that does not violate the current no-synchronous-Lobby-call boundary. If the requirement includes “a newly issued ticket invalidates the old one” and “cancelled assignment/terminal room rejects”, **O3** is the smallest option that reuses the existing durable Lobby ledger, but it changes the service boundary and needs an explicit decision. **O1** is not a security fix; it is a scope restriction that must be written down and enforced. No option is implemented here.

### 6.3 What must not be concluded

- Do not write “the ticket is one-time” without the deployment scope. It is one-time **per process lifetime** in the candidate.
- Do not use the room lease as proof of ticket one-time. It is a separate authority fence.
- Do not use the candidate's in-memory tests, or this probe, as PostgreSQL evidence.
- Do not “fix” this by writing test rows into `lobby.table_ticket_consumption`, injecting fake scopes, or weakening validator/seat/role checks.
- Do not add a synchronous Lobby call without an explicit boundary decision.

---

## 7. PASS / FAIL / BLOCKED / NOT_RUN boundaries

| Item | Status | Evidence |
|---|---|---|
| Candidate frozen SHA and ancestry verified | **PASS** | `eeaa90d`, tree `8f2543f`; ancestor exit 0; 328 commits ahead |
| Isolated archive inspection; no candidate merge/commit | **PASS** | `build/ticket-instance-ds-20261029/candidate`; worktree tracked clean |
| Candidate TableService builds in isolation | **PASS** | 0 warnings, 0 errors, native exit 0 |
| Probe builds and runs twice | **PASS** | native exit 0 both; `failures=0` both |
| Same-process concurrent exact-jti replay | **PASS** | S1: 1/32 accepted, 31 duplicate |
| Same-process exact replay after Release | **PASS** | S6: `DuplicateJti` |
| Same-process seat fence | **PASS** | S8: `SeatOccupiedByOtherAccount` |
| Same-process spectator cap | **PASS** | S9: `SpectatorCapacityReached` |
| Cross-instance same jti one-time | **FAIL** | S2: 2 accepted, 2 committed |
| Restart same jti one-time | **FAIL** | S3: fresh registry accepted/committed |
| Cross-instance seat uniqueness | **FAIL** | S4: two accepted owners in two registries |
| Cross-instance spectator capacity | **FAIL** | S5: cap 1 each, total 2 |
| Issuance supersession at table gate | **FAIL** | S7: old ticket accepted and displaced newer session |
| Real two-instance WSS admission | **NOT_RUN** | no shared PG; no two-process Kestrel probe |
| Real PostgreSQL ticket consumption | **NOT_RUN** | no shared PG resource used; candidate docs' prior PG evidence not re-run |
| Real Lobby HTTPS issuance + WSS consumption | **NOT_RUN** | candidate B1 reported `lobby.table_ticket_consumption=0`; no re-run |
| G3 cross-instance/restart ticket guarantee | **BLOCKED** | no durable shared authority wired |
| G2–G5 gates | **NOT_RUN / not approved** | V4 remains production |

---

## 8. Artifacts and hashes

All retained artifacts are in the ignored directory:

```text
D:\majiang-wt\ticket-instance-ds-20261029\build\ticket-instance-ds-20261029
```

Candidate source is identified by commit path; the extracted copy and `candidate.tar` were removed after hashing to keep the inspection isolated. Regenerate them with the `git archive` command in §2.2.

| Artifact | SHA-256 |
|---|---|
| `eeaa90d:dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs` | `8395F0CEB3446C8E3E809BC6D162720DC9F7F325EEE14A217F030FEEF7415D4F` |
| `eeaa90d:dotnet/src/Mahjong.TableService/TableTicketValidator.cs` | `168338CDB2A8826E9304D3D85DDE3CD8A3D1D193DA690A9932E2E0A79C7B8AFC` |
| `eeaa90d:dotnet/src/Mahjong.TableService/TableServiceWebHost.cs` | `6AB7C60A58E051E8BD995B2E1CEF22BB986A10C976BB97B55A81FDFFFB1168E4` |
| `eeaa90d:dotnet/src/Mahjong.LobbyService/LobbyApplication.Tickets.cs` | `7E6E71294019A729DA0ED4021878B3CCD274482CCC5761A348897FFCCFD409B2` |
| `eeaa90d:dotnet/src/Mahjong.LobbyService/PostgresLobbyStore.cs` | `B1CCCCB29CA1CFB93F7C59D81BA1FEE91ABED4F57809270A505B8A191C6A0F31` |
| `eeaa90d:dotnet/src/Mahjong.LobbyService/Migrations/001_lobby.sql` | `205F16EF97671B1EFFA6A8615709538EB5504CECE58ED450AF1DEE4F38B975CC` |
| `probe\Program.cs` | `17608ED5844846F5DA6484295CAC2EBDC353B8AD66F0AE2C7FAE0116D4A37EBD` |
| `probe\TicketInstanceProbe.csproj` | `0E0F782A8397BAF379DEF06704F827834A123F9C48BF0B0F1D1DFA479B3A85D9` |
| `build-tableservice.log` | `58A7285447BF7738AAB65282D99D1A4E0EB394E2747263413CE0EDF3541B6838` |
| `build-probe.log` (first failed build) | `F3E385CA70363FF99DC5735B2D0D1B90687E549F4F226D38208E37C8498364FB` |
| `build-probe-2.log` | `B176A7CE4BA998D5F6840E041674F041CD10E96793680DE39AFDEED2165F228F` |
| `build-probe-3.log` (final) | `5B3C9AFD7927CC7A84221F2A2DA910218DEEEDA861D995C38EA54A424F85B550` |
| `probe-run-1.log` | `794EC37929118AF824C562EFF83E7EAA169CD53BD552424733B3072E38CC5B43` |
| `probe-run-2.log` | `794EC37929118AF824C562EFF83E7EAA169CD53BD552424733B3072E38CC5B43` |
| `probe-results-1.json` | `DEC7160FBA1948AD95DEABBC02135843966991492731D44D2C09D421DEDD7F8E` |
| `probe-results-2.json` | `ACD462C30451A6AE8DC56ABA4116A5B4C391F95885CFAD64CD1EC841E00D7E94` |
| `source-audit.txt` | `4C9A7296CC7AB09773F26934C9B7FB369AECEC9E2BBB41A38C17D09BE8F2E22A` |

The two `probe-run` logs are byte-identical. The two JSON files differ only in `generated_at_utc`; the scenario values are identical.

The candidate archive (`candidate.tar`) and extracted candidate tree are inspection inputs, not deliverables. They can be regenerated with the `git archive` command in §2.2; only the probe source, logs, JSON results, and source audit are needed to reproduce the evidence.

---

## 9. Scope, non-actions, and handoff

- **No production code, tests, verifier, `Mahjong.sln`, Unity `Resources`/Burst, or machine trust was changed.**
- **No push, PR, merge, release, gate approval, delegation, or scheduler operation.**
- **No candidate code committed.** The local report commit contains only this report.
- **No real PostgreSQL, WSS, Lobby HTTPS, multi-process, multi-instance, cross-physical-network, Android/iOS, or production deployment validation was performed.**
- The candidate remains frozen and unaccepted. The caller may integrate this evidence into the existing PR20 review; this report is not an acceptance of PR20 or of candidate code.
- V4 remains the production architecture. G2–G5 remain unapproved.

**Recommended next discrimination for the caller (not a product decision):** decide whether the contract requires global one-time semantics. If yes, choose an authority (TableService `table_store` vs Lobby's existing durable ledger) and require a real two-instance/restart WSS + PostgreSQL probe, including exact-jti replay, same-seat cross-account, spectator cap, superseded ticket, and terminal assignment. If no, write the single-instance/no-restart scope restriction explicitly and stop calling the ticket globally one-time. Until then, the cross-instance/restart guarantee is **not established**.
