# V5 B1 Lookup Digest Fix: Candidate Patch and Evidence (2026-10-30)

Status: **candidate patch plus reproducibility evidence for caller review. This is not an
acceptance, not a B1 batch PASS, and not a G2–G5 approval. PR20 remains unaccepted until the
caller reviews and integrates the patch. B1-BLOCKER-03 is not declared closed here. V4 remains
the only production baseline.**

- Accepted baseline: `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
  (tree `61f50856aedd78e0f8609e282baa8313ae8f2a14`; also this worktree's `HEAD`).
- Unaccepted PR20 tip inspected: `00cd02d80172ebd13a2945e4e8bacfe486ae0c21`
  (tree `72502a19a808738f9e2c801f83e47d46dc07761d`; parent `2bb6e681...`).
- Accepted B1 diagnosis consulted: bridge thread `thread_20261029_211248_b71a`, integrated
  in PR20 as `doc/v5-重构/V5-B1-LOOKUP-DIGEST-CONTRACT-DIAGNOSIS-20261029.md`
  (commit `565568c`, merged by `eeaa90d`).
- Patch: `doc/v5-重构/patches/V5-B1-LOOKUP-DIGEST-FIX-20261030.patch`.
- This report is the only other committed file. The PR20 tree was extracted with
  `git archive` into the ignored `build/` directory and modified only there.
- No PR, push, merge, delegation, release, verifier relaxation, Unity asset, machine-trust,
  or other worktree change.

---

## 0. Verdict

| Question | Result | Evidence |
|---|---|---|
| Is the durable receipt producer omission reproduced on PR20 `00cd02d`? | **YES** | Immediate Applied/Rejected receipts carry the MJC1 digest; durable lookup inner and top-level digests are both empty. Unpatched new B1 class: **3 failed / 0 passed / 3**, exit **1**. |
| Does the patch persist the original MJC1 digest for new Applied receipts? | **YES** | `TableActor` passes the host-computed canonical digest into `Receipt.Applied`; patched Applied lookup inner and top-level digests equal the immediate receipt digest. |
| Does the patch persist it for new Rejected receipts? | **YES** | `CommitRejectedReceiptAsync` receives the same canonical digest and passes it to `Receipt.Rejected`; patched Rejected lookup inner and top-level digests equal the immediate receipt digest. |
| Is the digest computed from original business bytes, not protobuf reserialization? | **YES** | `TableCommandMapper.Map` computes `CanonicalPayloadDigest.Compute(payload.AsSpan())` over the retained wire `Command.canonical_payload` bytes. No typed command is re-encoded for the digest. |
| Is the raw `TablePayloadSha256` still the conflict key? | **YES** | `Matches`, `TablePayloadSha256`, receipt-first ordering, and `REQUEST_ID_CONFLICT` are untouched. Same-key/different-bytes remains a conflict; the original receipt and digest are preserved. |
| Is scope/account/room/role isolation unchanged? | **YES** | No authorization code changed. Tested cross-account `SCOPE_MISMATCH`, other-account-own-scope `NotFound`, and same-account historical scope `Found` with the original digest. |
| Are `UNKNOWN`/`NOT_FOUND` semantics preserved? | **YES** | No digest is invented for `NotFound`; the lookup-level field stays empty and no receipt is attached. Existing `ReceiptQuery_ForeignScopeRefusedAndUnknownIdNotFound` still passes. |
| Does the digest survive cold actor recovery from durable storage? | **YES (in-memory store)** | A new actor instance reads both Applied and Rejected digests after `RecoverFromStoreAsync`; targeted runtime test **1/1**, full runtime suite **454/454**. |
| Was PostgreSQL / real WSS/TLS / B1 44-test gate rerun? | **NO — NOT_RUN** | This patch was tested over loopback Kestrel `ws://` with the memory-mode durable store. No PG, TLS, WSS admission, or B1 gate claim is made. |
| Is B1-BLOCKER-03 closed? | **NO — caller decision required** | The patch makes new-row three-way identity achievable and strengthens the existing test, but the caller must review the contract/test change, integrate, and rerun B1 before any closure statement. |

Short conclusion: the B1-BLOCKER-03 defect is a producer-side omission, not a schema or
retrieval defect. The trusted host already computes the MJC1 digest for the immediate receipt,
but the actor's durable `Receipt.Applied`/`Receipt.Rejected` constructors never received it,
and `TableSessionHandler` never populated the lookup-level field. The patch carries the exact
host-computed digest through the optional envelope identity, persists it in the existing
nullable receipt column, and returns it on both lookup fields for new rows. Legacy rows remain
NULL and are not backfilled with a raw hash.

---

## 1. Inputs and extraction boundary

| Item | Exact value |
|---|---|
| accepted baseline | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| PR20 tip | `00cd02d80172ebd13a2945e4e8bacfe486ae0c21` |
| PR20 tree | `72502a19a808738f9e2c801f83e47d46dc07761d` |
| clean extraction | `build/review-input-pr20/` (ignored) |
| scratch patch source | `build/b1-digest-work/` (ignored; own scratch Git repository) |
| applied-patch verification | `build/b1-digest-patchcheck/` (ignored; fresh `git archive` plus `git apply`) |
| unpatched regression run | `build/b1-digest-unpatched/` (ignored; fresh `git archive` plus new tests only) |
| patch SHA-256 | `58AAC2B41DE1FB67FA91193C93FA396FDB8238072287D5BD744138025063D757` |
| patch bytes | 37,146 |

Extraction used only `git archive`; no candidate commit was checked out or merged into the
base worktree. `build/b1-digest-patchcheck` is a private scratch repository inside the ignored
`build/` directory, not the base repository.

Key commands and native exits:

```powershell
git archive --format=tar -o build\b1-digest-work.tar 00cd02d dotnet global.json
tar -xf build\b1-digest-work.tar -C build\b1-digest-work
# archive exit 0; tar exit 0

git -C build\b1-digest-patchcheck apply --check --whitespace=nowarn `
  build\V5-B1-LOOKUP-DIGEST-FIX-20261030.patch
# exit 0 against a fresh 00cd02d extraction

git -C build\b1-digest-patchcheck apply --whitespace=nowarn `
  build\V5-B1-LOOKUP-DIGEST-FIX-20261030.patch
# exit 0
```

Three of the four PR20 production files relevant to this defect are byte-identical to the
diagnosis candidate; `TableSessionHandler.cs` differs only by the later heartbeat integration,
with the digest computation and lookup mapping unchanged:

```text
B352CC6E84ADFB4A7B962ED68C82CE726CFF2C2DAAD932E073D5406C74E9728E  TableActor.cs
D54D3241096F0AB0A2133CD7645B559D234DD9BB228941BF90A2D25AB03C4E60  TableRuntimeContracts.cs
6B027B74D0CC721EDE022154ECAB27F6883AF4CB5799C43360A5E7CEBC39869C  TableCommandMapper.cs
51AA8908E9CB4717D8DC52DC3A65CC90EF9219D59CA3FDA064EDADC6E8CA7546  TableSessionHandler.cs
```

`TableSessionHandler.cs` differs from the diagnosis snapshot only because PR20 later
integrated the heartbeat fix; the receipt-digest computation at the wire boundary and the
lookup mapping are unchanged.

---

## 2. Actual source and root cause

### 2.1 Two different digest domains

The frozen design has two deliberately separate identities:

| Identity | Calculation | Role |
|---|---|---|
| MJC1 `CanonicalPayloadDigest` | `SHA-256("MAHJONG-V5-CANONICAL-COMMAND\0" || u32(len) || canonicalPayload)` | Client outbox identity, immediate wire receipt, B1 expected digest |
| `TablePayloadSha256` | `SHA-256(canonicalPayload)`, no domain prefix | Actor durable idempotency and same-key conflict key |

They cannot be substituted for each other. The diagnosis measured this directly; the patch
keeps both, and never forwards the raw hash under the Client-facing digest field.

### 2.2 Immediate receipt is correct

`TableSessionHandler` computes the wire digest from the exact original payload bytes:

```text
payloadBytes = envelope.Command.CanonicalPayload.ToByteArray()
wireDigestHex = CanonicalPayloadDigest.Compute(payloadBytes).ToHex()
```

and uses it in `TableCommandMapper.MapReceipt` for the immediate Applied/Rejected answer.
The B1 diagnosis and this run both confirm the immediate receipt is non-empty and equals the
MJC1 digest.

### 2.3 The durable producer omitted the digest

On unpatched PR20, the three durable receipt construction points are:

```text
TableActor.CommitDurableAcceptedAsync
    Receipt.Applied(evaluated, commandVersion, lastEventSeq, null, committedAt)
    // no canonicalPayloadDigest

TableActor.CommitCheat... (accepted/rejected branch)
    Receipt.Applied(...) / Receipt.Rejected(...)
    // no canonicalPayloadDigest

TableActor.CommitRejectedReceiptAsync
    Receipt.Rejected(evaluation, reasonCode)
    // no canonicalPayloadDigest
```

`TableCommandEnvelope` / `TableCheatCommandEnvelope` carry only `PayloadSha256`; the
`CanonicalPayloadDigest` computed by the service is not passed into the actor. The persisted
`request_receipt.canonical_payload_digest` is therefore NULL for new remote receipts.

`TableDurableReceipt.ToCommandReceipt` also has no digest field, but the immediate replay path
is not the lookup defect: the handler recomputes the wire digest from the retried request and
maps it onto the immediate receipt. The lookup path reads `receipt.Receipt.AsReplay()` directly.

### 2.4 Lookup omitted the top-level field

`TableActor.QueryReceiptAsync` returns `ReceiptLookupResult.Found(receipt.Receipt.AsReplay())`.
`ReceiptLookupResult.Found` copies `receipt.CanonicalPayloadDigest` into `Digest`, so the inner
wire field can be populated when the durable receipt has it. However, the prior
`DispatchReceiptQueryAsync` only called `MapLookupReceipt`, which writes the inner
`Receipt.canonical_payload_sha256` when present; it never wrote the lookup-level
`ReceiptLookup.canonical_payload_sha256`. Both fields were empty because the durable value was
NULL and the top-level field had no writer.

### 2.5 Schema and storage did not lose a value

`request_receipt.canonical_payload_digest` already exists and is nullable. The PostgreSQL and
in-memory stores faithfully persist and restore the `Receipt` object; no DDL, migration, or
store change is needed. The diagnosis explicitly rejected a `NOT NULL` migration and any
backfill from a raw hash. This patch follows that boundary.

---

## 3. Contract basis and the change the patch makes

### 3.1 Frozen contract

The relevant frozen wording is in `doc/v5-重构/V5-0-PROTOCOL-DECISIONS.md`:

- §7.1 (line 399): same key/same payload returns **the original definitive receipt's original
  fields** (`status/reasonCode/evaluated_state_version/command_version/event_seq` etc.),
  **not recomputed and not emptied**.
- §7.2 (line 411): after restart/reconnect, `RECEIPT_QUERY` for Applied/Recorded/Rejected
  returns the **original definitive receipt's original fields**, including the original
  `evaluated_state_version/command_version/event_seq`; no recompute, no clearing, no resend.
- §7.4 (line 427): idempotent replay and `RECEIPT_QUERY` return the stored definitive
  receipt's **original fields**; they must not be recomputed or cleared.
- The wire proto already has `Receipt.canonical_payload_sha256` and
  `ReceiptLookup.canonical_payload_sha256`; the schema column is nullable. Empty is wire-valid
  for legacy rows, but the contract does not authorize the producer to discard a value it
  already computed.

The diagnosis correctly identified an acceptance/test conflict: the old
`SessionFlowTests.CommandReceipt_ReplayAndDurableLookup` asserted that the lookup digest is
empty and called that a frozen boundary. That assertion encoded the producer omission rather
than a protocol requirement. The patch changes it to assert equality with the immediate
receipt digest **and** the lookup-level digest. This strengthens the check; it does not
delete, skip, or relax any assertion.

### 3.2 Chosen compatibility level (Option C hybrid)

The patch chooses the safe hybrid:

1. **New rows**: the trusted host persists the MJC1 digest for Applied, Recorded, and
   Rejected receipts.
2. **Legacy rows**: remain NULL. There is no `NOT NULL` migration, no post-commit UPDATE, and
   no raw-hash backfill. Old rows still use the Client's retained outbox digest.
3. **Schema/wire**: unchanged. The existing optional fields carry the value when available.
4. **Conflict identity**: unchanged. The raw `TablePayloadSha256` remains the durable
   idempotency/conflict key.

This does not claim that old rows can prove canonical identity. It makes new-row three-way
identity available, which is the part a producer fix can safely establish. Whether the B1
acceptance should require non-empty for every historical row remains a separate
contract/migration decision; this patch does not fabricate a backfill.

---

## 4. Patch contents

The patch is 8 files, **459 insertions / 27 deletions**:

| File | Change |
|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs` | Adds an optional `PayloadDigest? CanonicalPayloadDigest` to `TableCommandEnvelope` and `TableCheatCommandEnvelope`; default null preserves source compatibility and internal/system commands without a client identity. |
| `dotnet/src/Mahjong.TableService/TableCommandMapper.cs` | Computes `CanonicalPayloadDigest.Compute(payload.AsSpan())` from the exact retained wire canonical-payload bytes and passes it into both envelope types. |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | Passes the envelope digest into all durable receipt constructors: ordinary accepted, cheat accepted, cheat rejected, and the shared ordinary/cheat rejected-receipt port. Internal commands keep null. |
| `dotnet/src/Mahjong.TableService/TableSessionHandler.cs` | Sets the lookup-level `ReceiptLookup.canonical_payload_sha256` from `ReceiptLookupResult.Digest` when a definitive receipt has one; NotFound/Unknown remain empty. |
| `dotnet/tests/Mahjong.TableService.Tests/B1ReceiptDigestTests.cs` | New targeted matrix: Applied lookup, Rejected lookup, same-key conflict, unknown/not-found, cross-account scope, same-account historical scope. |
| `dotnet/tests/Mahjong.TableRuntime.Tests/B1ReceiptDigestPersistenceTests.cs` | New durable-store test: Applied and Rejected digests survive a new actor's cold recovery. |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableDurableTestSupport.cs` | Optional canonical-digest argument for the existing test envelope helper. |
| `dotnet/tests/Mahjong.TableService.Tests/SessionFlowTests.cs` | Replaces the old “lookup must be empty” assertion with inner + top-level equality to the immediate digest. |

Not changed: wire proto, SQL migrations, `TablePayloadSha256`, `Matches`, receipt-first
ordering, scope/account checks, ticket/admission paths, Client outbox resolution, B1 verifier,
CI workflow, Unity assets, Kotlin modules, or machine trust.

### 4.1 Why no envelope schema/DTO change was needed

`TableCommandEnvelope` is a TableRuntime constructor surface, not a wire message. Adding an
optional in-process field lets the trusted host carry a value it already computes to the
durable receipt constructor without changing `mahjong.proto`, the wire codec, or the
client-visible command. The value is not accepted from a client field; it is derived by the
service from the original wire payload bytes.

### 4.2 Rejected receipts

Both rejection paths are covered:

- ordinary/cheat business rejection after the actor evaluates the command
  (`CommitRejectedReceiptAsync`);
- cheat path's accepted/rejected branch in the cheat commit routine.

The receipt still has `command_version=null`, no event, and no state version change. Only the
optional canonical digest is added. `ShouldPersistRejection` fence exclusions are unchanged.

---

## 5. Test matrix and evidence

### 5.1 Targeted matrix

All tests run against the real loopback Kestrel TableService and the in-memory durable store,
using production command/receipt/actor/mapping paths. No mock transport was substituted.

| Scenario | Assertion | Patched result | Unpatched result |
|---|---|---|---|
| Applied ordinary command | Immediate digest = MJC1(payload); lookup inner = immediate; lookup top-level = immediate; `Replayed=true` | PASS | FAIL at lookup inner, expected non-empty, actual `""` |
| Applied same-key different bytes | `REQUEST_ID_CONFLICT`; original Applied receipt and digest unchanged | PASS | Not reached: the earlier lookup-digest assertion fails; conflict behavior itself is unchanged and is verified on the patched run |
| Rejected command (`ROUND_MISMATCH`) | Rejected immediate digest = MJC1(payload); lookup inner and top-level = immediate; status/reason preserved | PASS | FAIL at lookup inner, expected non-empty, actual `""` |
| Rejected same-key different bytes | `REQUEST_ID_CONFLICT`; original Rejected receipt and digest unchanged | PASS | Not reached: the earlier lookup-digest assertion fails; conflict behavior itself is unchanged and is verified on the patched run |
| Unknown request in own scope | `NotFound`, no receipt, lookup-level digest empty | PASS | Existing suite covers status; new test fails earlier at the digest assertion |
| Cross-account foreign scope | `MALFORMED_MESSAGE` / detail `SCOPE_MISMATCH` | PASS | Same (authorization path unchanged) |
| Other account's own scope + foreign request id | `NotFound`, no receipt, no digest | PASS | Same (key isolation unchanged) |
| Same account, historical scope after reconnect | `Found`; original Rejected status and digest returned | PASS | FAIL at digest, expected non-empty, actual `""` |
| Applied + Rejected durable persistence | New actor recovers from the same store; both digests and statuses survive | PASS | Test does not compile without the new envelope field; producer omission already proven by service tests |

### 5.2 Commands and exact results

Run from the ignored `build/b1-digest-patchcheck` extraction after applying the patch:

```powershell
dotnet build dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj -c Release --nologo -v:minimal
# exit 0; 0 warnings, 0 errors

dotnet test dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj -c Release --no-build --nologo -v:minimal
# exit 0; Failed: 0, Passed: 173, Skipped: 0, Total: 173

dotnet test dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj -c Release --no-build --nologo -v:minimal -- --filter-class "Mahjong.TableService.Tests.B1ReceiptDigestTests"
# exit 0; Failed: 0, Passed: 3, Skipped: 0, Total: 3

dotnet build dotnet\tests\Mahjong.TableRuntime.Tests\Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal
# exit 0; 0 warnings, 0 errors

dotnet test dotnet\tests\Mahjong.TableRuntime.Tests\Mahjong.TableRuntime.Tests.csproj -c Release --no-build --nologo -v:minimal
# exit 0; Failed: 0, Passed: 454, Skipped: 0, Total: 454

dotnet test dotnet\tests\Mahjong.TableRuntime.Tests\Mahjong.TableRuntime.Tests.csproj -c Release --no-build --nologo -v:minimal -- --filter-class "Mahjong.TableRuntime.Tests.B1ReceiptDigestPersistenceTests"
# exit 0; Failed: 0, Passed: 1, Skipped: 0, Total: 1

dotnet build dotnet\Mahjong.sln -c Release --nologo -v:minimal
# exit 0; 0 warnings, 0 errors
```

Additional unaffected suites from the same applied extraction:

```powershell
dotnet test dotnet\tests\Mahjong.Contracts.Tests\Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo -v:minimal
# exit 0; Failed: 0, Passed: 150, Skipped: 0, Total: 150

dotnet test dotnet\tests\Mahjong.Client.Tests\Mahjong.Client.Tests.csproj -c Release --no-build --nologo -v:minimal
# exit 0; Failed: 0, Passed: 182, Skipped: 0, Total: 182
```

The xunit v3 runner uses Microsoft.Testing.Platform syntax. VSTest-style `--filter` was
ignored in an early run (it executed the full assembly), so the final targeted runs use
`-- --filter-class`. The full-suite numbers above are the real full-project totals, not
filtered counts.

### 5.3 Negative-before run on unpatched PR20

The three new service tests were copied onto a fresh `00cd02d` extraction with no production
change. This proves the tests fail for the exact defect, not merely because the patch was
applied:

```powershell
dotnet test build\b1-digest-unpatched\dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj `
  -c Release --no-build --nologo -v:minimal `
  -- --filter-class "Mahjong.TableService.Tests.B1ReceiptDigestTests"
# exit 1; Failed: 3, Passed: 0, Skipped: 0, Total: 3
```

Details (`build/b1-digest-evidence/unpatched-b1-targeted-details.log`):

```text
AppliedLookup_PreservesOriginalDigest_AndSameKeyConflictKeepsIt
  Expected: "b67dceae9b8b01331e126f1a9164cb0a0209233e4e02cfe4e2"...
  Actual:   ""

LookupScopeIsolation_AndSameAccountHistoricalScopePreservesDigest
  Expected: "6a768d510bc2f6a461f6b5c8a9344c05c0ba4db8906af82c52"...
  Actual:   ""

RejectedLookup_PreservesOriginalDigest_AndUnknownResultStaysDigestFree
  Expected: "afa4e36f7f8fcd82586037f72e3cc5ae9da5c228cfba874efb"...
  Actual:   ""
```

The unpatched full service suite, with the new tests and the strengthened
`SessionFlowTests` assertion, is:

```text
Failed: 4, Passed: 169, Skipped: 0, Total: 173; native exit 1
```

The fourth failure is `SessionFlowTests.CommandReceipt_ReplayAndDurableLookup` at its lookup
digest equality assertion. All four failures are the empty durable lookup digest; the
immediate receipt, conflict, and scope assertions before those points pass.

---

## 6. Evidence index

All logs are ignored under `build/b1-digest-evidence/`; none is committed.

| File | SHA-256 | Purpose |
|---|---|---|
| `service-build-applied.log` | `61847800F6AA0B2CE354BE2A6B6AD562E1D8B65F2DCE1831178DAA2618D9A0F8` | Applied service build, exit 0 |
| `service-test-applied.log` | `BE03DD7E8C9E4F866CA24CBA08C3813A8D15A7687BABEEAB9CF449F8CBB066F0` | Full service suite 173/173, exit 0 |
| `service-b1-targeted-test.log` | `29FFAD0EF3D2E48CB2C7BC111F35117E9E15F3CD49AAB4ECC8F78390C3570C9E` | B1 service class 3/3, exit 0 |
| `runtime-build-applied.log` | `E936A51EAFC322109DE18021020E710ED7570515EB273473EA2092F15A5E501C` | Applied runtime build, exit 0 |
| `runtime-test-applied.log` | `6EB8C07B175310AD6201B6E48B6DDA7ADE827EB630450547E1EBBF8772FFAAAC` | Full runtime suite 454/454, exit 0 |
| `runtime-b1-targeted-test.log` | `D38117FEF2A10675964E06268CF4A2501DB1E19718B9397CB91FD1EDDD3A7F29` | Persistence test 1/1, exit 0 |
| `solution-build-applied.log` | `CE0E0D007219F06DC87A63872B26F5F36B74A24AF0C8E3AA1607BF9992ADDBAF` | Full solution build, 0 warnings/0 errors, exit 0 |
| `contracts-test-applied.log` | `4493011FD7978B3135E680CC577C0501688352E84AA03C259F3D742EB2A70767` | Contracts 150/150, exit 0 |
| `client-test-applied.log` | `FB19C3A229DC9EBCDB0D1007F88970B73520443D97D962863260A3C85902FBCA` | Client 182/182, exit 0 |
| `unpatched-b1-targeted-test.log` | `46E6FF3C1BBA33256E8BC3EF77C6A19DA2A363BC1406C26424D8F7706EE10CAC` | Unpatched B1 class 3 failed/3, exit 1 |
| `unpatched-b1-targeted-details.log` | `60073275B035B6F2599D1765817EDAD1B87A820F684E849AEDC0A756567ED41A` | Exact unpatched failure assertions |
| `unpatched-service-test.log` | `58FEF2C554DF4C5D9EF7F4D3B90FF039E9EDCC7FB9624067462A02E4BA3B46DB` | Unpatched full suite 4 failed/169 passed/173, exit 1 |

Patched source SHA-256 values (applied extraction):

```text
7D7BF03E2F9E2BCD74DEF8476508A8DA2B2F7795D61235CD766A6211DA59BE3A  TableActor.cs
A06ADE07C8F4A262DC20A25A32B5CC1DA5A5C75CAE731975E90FB488E9324FAA  TableRuntimeContracts.cs
3E516389DE76B1AAEEAB277C395876EF3E4BB7A4309143D4D56CE2C6D40A974B  TableCommandMapper.cs
7E8DF6F4C99C9C134967485EA2647652212B9E190EAE6DC2E887A4D1123FC36E  TableSessionHandler.cs
E4945C1C405303C72BA40D54885106011945E1D2B199B8BE76A36CFBA2B27055  B1ReceiptDigestTests.cs
FEEB8B4E1BA6AEE3323FF6FF5E7F885DC0E10A63EC0519C7731FE311CE3839D1  B1ReceiptDigestPersistenceTests.cs
```

---

## 7. Limitations and explicit NOT_RUN

- **Transport:** tests use a real Kestrel listener on `127.0.0.1` with cleartext `ws://`.
  They do not exercise WSS/TLS, certificate validation, or HTTPS admission.
- **Storage:** tests use `TableServiceTestHost`'s in-memory durable store. The runtime
  persistence test uses the same in-memory store with a new actor and cold recovery; it is
  not a PostgreSQL restart and not a process kill/restart.
- **PostgreSQL:** no PG instance was started. The existing nullable column and store code
  are unchanged; PG write/read/restart evidence is **NOT_RUN**.
- **B1 gate:** the B1 44-test HTTPS/WSS/PG suite, Run A/Run B, real tickets/TLS, and the
  `CanonicalReceiptIdentityVerified` gate were **NOT_RUN** here. This patch does not claim
  those pass.
- **Legacy rows:** old receipts keep NULL. No backfill, no `NOT NULL`, no raw-hash
  substitution. For those rows the Client still uses its retained outbox digest.
- **Rejected legacy rows:** cannot be reconstructed from the DB because they have no command
  journal. This patch does not attempt that.
- **Other platforms:** Unity, Android, iOS, IL2CPP, devices, and real public networks are
  **NOT_RUN** and untouched.
- **Test-count caveat:** the full service suite total is 173 including the three new B1 tests;
  it is not a B1 acceptance count. The full runtime suite is 454 including the new persistence
  test.

---

## 8. Security and compatibility review

- **No secret logging:** no log statement was added. The MJC1 digest is a non-secret
  domain-separated hash; no payload bytes, ticket, token, key, or outbox content is logged.
- **Trusted computation:** the digest is computed by the service from the original retained
  wire canonical-payload bytes. It is never read from a client-provided field and is not
  derived by reserializing a typed command/protobuf.
- **Conflict identity preserved:** `TablePayloadSha256` remains the receipt-first key and
  `Matches` comparison. Same-key/different API or bytes still returns
  `REQUEST_ID_CONFLICT` before domain evaluation and does not overwrite the original receipt.
- **Scope isolation preserved:** the patch does not touch `IsAuthorizedReceiptScopeAsync`,
  `ValidateScope`, account/room/role checks, or the actor's account binding. Foreign scope
  remains `SCOPE_MISMATCH`; another account's own scope remains `NotFound`; same-account
  historical scope remains queryable by design.
- **Unknown-result semantics preserved:** NotFound/Unknown still carry no definitive receipt
  and no invented digest; they remain unable to prove non-commit and do not authorize a new
  request id.
- **Schema compatibility:** optional nullable field, no DDL. Old Clients ignore a non-empty
  value; new Clients still have the outbox fallback for legacy NULL rows.
- **Test change:** the old empty-lookup assertion is the only removed assertion; it is
  replaced by inner + top-level equality assertions, which are stricter. No verifier, probe,
  or skip was removed, and no check was relaxed to obtain green.

---

## 9. Commit boundary and caller action

The only committed files are:

1. `doc/v5-重构/patches/V5-B1-LOOKUP-DIGEST-FIX-20261030.patch`
2. `doc/v5-重构/V5-B1-LOOKUP-DIGEST-FIX-20261030.md` (this report)

No production source, test source, candidate tree, binary, log, certificate, key, or outbox
data is committed. No PR, push, merge, deployment, verifier relaxation, Unity change, machine
trust change, or other worktree operation was performed.

Caller review should decide the contract/test change in §3: whether “new rows must return a
non-empty MJC1 digest, legacy NULL remains compatible” is the intended B1 rule. If accepted,
the caller can apply the patch to PR20, rerun the B1 HTTPS/WSS/PG gate, and only then decide
whether `CanonicalReceiptIdentityVerified` and B1-BLOCKER-03 can be closed. Until that happens,
this report’s status remains **candidate patch / B1-BLOCKER-03 OPEN / PR20 unaccepted**.
