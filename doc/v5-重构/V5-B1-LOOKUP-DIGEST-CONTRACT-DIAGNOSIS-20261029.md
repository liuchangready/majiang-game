# V5 B1 Lookup Digest Contract Diagnosis: Rejected / Applied Receipt Canonical Digest Loss (2026-10-29)

Status: **independent diagnosis (DIAGNOSIS). Not an acceptance, not a batch PASS, and it does not close B1-BLOCKER-03.**
Accepted baseline: `origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
Frozen candidate: `5ccefd2fc5770a8bab03f3a1236e7df308661676` (PR20 head, **UNACCEPTED**).
Production state: **V4 remains the only production baseline; G1 approved; G2-G5 unapproved; B1-BLOCKER-03 remains OPEN/BLOCKED.**

---

## 0. Executive summary

1. **The immediate receipt is correct.** `TableSessionHandler.cs:1475-1482` computes the MJC1 domain-separated digest from the same canonical payload bytes and `MapReceipt` / `BuildReceipt` write it to the wire `CanonicalPayloadSha256`. An independent cleartext-loopback WebSocket (ws://) probe measured that both Applied and Rejected immediate receipts equal the corresponding canonical payload's `Digest.ToHex()` (the domain digest), not a bare SHA-256.

2. **The loss occurs at durable-receipt construction, not in the schema, retrieval, or DTO mapping.** `TableActor` constructs `Receipt.Applied` / `Receipt.Rejected` for accepted, rejected, and cheat commands without passing the optional canonical digest (`TableActor.cs:3766-3780`, `:4202-4213`, `:4482-4491`). `TableSessionHandler` already computed that digest at `:1481`, but only used it for the immediate receipt; it never passed it into the actor mailbox/envelope. The envelope's only digest field is the raw `TablePayloadSha256` (`TableRuntimeContracts.cs:193-269`). The persisted `request_receipt.canonical_payload_digest` is therefore NULL.

3. **Applied and Rejected are both empty; this is not Rejected-only.** The B1 gate measured a Rejected lookup. This report's cleartext-loopback WebSocket (ws://) probe measured an **Applied lookup as well, and it is also empty**. The code path is shared: both `Receipt.Applied` and `Receipt.Rejected` omit the digest.

4. **Persistence itself does not drop the field.** `request_receipt.canonical_payload_digest` is nullable; `InsertReceiptAsync` faithfully writes `receipt.Receipt.CanonicalPayloadDigest` or NULL; `TryGetAsync` / `LoadReceiptForUpdateAsync` / `RestoreReceipt` faithfully restore NULL. The existing `PostgresR1RegressionTests.ReceiptOptionalFields_ArePreservedForAppliedRecordedAndRejected` covers Applied/Recorded/Rejected null/non-null combinations (Rejected is a committedAt x digest four-cell matrix). That PG test was **NOT_RUN** in this report; only code review was performed.

5. **Lookup DTO mapping is conditional, not a place that fills the digest.** `MapLookupReceipt` writes the inner `Receipt.canonical_payload_sha256` only when `receipt.CanonicalPayloadDigest.HasValue`; `DispatchReceiptQueryAsync` never writes the top-level `ReceiptLookup.canonical_payload_sha256`. The independent probe measured both fields empty.

6. **The Client can reconcile from its outbox expected digest, but this is local binding, not server-verified canonical identity.** `TableSession.HandleReceiptLookupAsync` treats a lookup digest as a Conflict only when it is non-empty and unequal; `BuildResolution` substitutes the outbox expected digest when the server digest is empty (`TableSession.cs:1452-1503`, `:1759-1775`). Cold restart and pre-existing rows can therefore still reconcile, but `CanonicalReceiptIdentityVerified=false` is honest.

7. **Does an existing contract require a non-empty lookup digest? The answer is inconsistent, and cannot be decided unilaterally in code.**
   - The wire proto's `canonical_payload_sha256` is a plain proto3 string (no `optional` presence keyword); an empty string is wire-valid.
   - `Contracts.Receipt.CanonicalPayloadDigest` is `PayloadDigest?`; the `Receipt.Applied` / `Receipt.Rejected` factories accept an optional digest.
   - `V5-0-PROTOCOL-DECISIONS.md` §7.1/§7.4 says lookup returns the original definitive receipt's original fields, but does not explicitly require a non-empty digest.
   - The P5.2 schema is explicitly nullable and its tests cover null. The current `SessionFlowTests` explicitly asserts that the lookup digest is empty and calls this a frozen-contract boundary.
   - On the other hand, the B1 acceptance's `CanonicalReceiptIdentityVerified` requires `FirstReceiptDigestVerified && LookupDigestAvailable && LookupDigestMatchesExpected`; LocalHost's `HandleReceiptQueryAsync` returns both top-level and inner digest, and its test asserts the top-level digest equals the immediate receipt digest.
   This is a **contract/acceptance-criteria conflict**, not a single frozen-schema violation. It requires a contract-owner decision; it cannot be closed by relaxing the test or by injecting a digest.

8. **B1-BLOCKER-03 remains OPEN/BLOCKED.** This report fabricated no digest, relaxed no check, changed no production/test code, and did not classify the blocker as closed. PR20 remains an unaccepted candidate; G2-G5 are unapproved; V4 remains production.

---

## 1. Inputs, exact SHAs, and extraction

| Item | Exact value |
|---|---|
| accepted baseline | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| PR20 head candidate | `5ccefd2fc5770a8bab03f3a1236e7df308661676` |
| gate-review object | `451f4ec9803cb45c9b01a5724d58718ac6ad1728` |
| `451f4ec` vs `5ccefd2` | only the gate-review document is added (283 lines); production code is byte-identical |
| candidate tree | `c6affca6c415554d8d35750159aa961693f6dd90` (2,315 files) |
| archive | `build/review-candidate-5ccefd2.zip`, 328,430,951 bytes, SHA256 `B90D2FBCAC676DE1F56FB606EDE8E4ECA8DD3E333B0574B461423B058C26DB28` |
| isolated extraction | `build/review-candidate-5ccefd2/` (ignored) |
| scratch tree check | `build/candidate-tree-check/` (ignored scratch git; `git write-tree` equals candidate tree, `TREE_EQUAL=True`) |
| candidate code commit | **not merged, not checked out, not committed wholesale to this worktree** |

Extraction and tree-check commands (native exits listed):

```powershell
git archive --format=zip -o build\review-candidate-5ccefd2.zip 5ccefd2fc5770a8bab03f3a1236e7df308661676
# exit 0

Expand-Archive -Path build\review-candidate-5ccefd2.zip -DestinationPath build\review-candidate-5ccefd2 -Force
# PowerShell cmdlet succeeded; no native exit code; 2315 files extracted

git -C build\candidate-tree-check init -q
git -C build\candidate-tree-check config core.autocrlf false
git -C build\candidate-tree-check add -A -f
git -C build\candidate-tree-check write-tree
# c6affca6c415554d8d35750159aa961693f6dd90
# == git rev-parse 5ccefd2^{tree}; TREE_EQUAL=True
```

`Expand-Archive` is a PowerShell cmdlet and produces no native exit code; this report does not write its empty `$LASTEXITCODE` as 0. `git archive`, `git write-tree`, and the `dotnet` commands below have real native exits.

---

## 2. End-to-end digest trace

### 2.1 Two different SHA-256 domains

| Name | Calculation | Use |
|---|---|---|
| `CanonicalPayloadDigest` / `PayloadDigest` (MJC1) | `SHA256("MAHJONG-V5-CANONICAL-COMMAND\0" || u32(len) || canonicalPayload)` (`CanonicalPayloadDigest.cs:41,57-64`) | Client outbox identity, immediate wire receipt, B1 expected digest |
| `TablePayloadSha256` (raw) | `SHA256(canonicalPayload)`, no domain prefix (`TableDurableContracts.cs:110-121`) | actor durable idempotency / conflict decision / persisted `payload_sha256` |

Independent direct probe on the same canonical payload:

```text
canonicalDomainDigest = 4b9defeb19f5ef74a06e8f53a8952c40629ed4a109b66d4243ed0919a0f784a0
bareSha256            = 39259ceae4aaf27971b4d04f3eaac9d87879d4ecbdc131ab922cc9828ff7e23a
domainDiffersFromBare = true
```

### 2.2 Immediate receipt (correct)

```text
TableSessionHandler.cs:1475  byte[] payloadBytes = envelope.Command.CanonicalPayload.ToByteArray();
TableSessionHandler.cs:1481  string wireDigestHex = CanonicalPayloadDigest.Compute(payloadBytes).ToHex();
TableSessionHandler.cs:1529  result = await host.Actor.SubmitAsync(business.Envelope, ...);
TableSessionHandler.cs:1542  Enqueue(BuildReceipt(envelope, TableCommandMapper.MapReceipt(result, wireDigestHex)));
```

`MapReceipt` unconditionally writes `wireDigestHex` into the wire receipt (`TableCommandMapper.cs:732-775`), including the `Rejected` admission-fence branch with no durable receipt. `BuildReceipt` only fills the authoritative scope (`TableSessionHandler.cs:2503-2514`).

Independent cleartext-loopback WS measurements:

| Scenario | Immediate receipt | digest == domain |
|---|---|---|
| Applied discard | `Applied` | `true` (`6d0f4b074d605fda7b5b03ce093f1c0d9aee737afe87d0b26da9a0ce5a010cd8`) |
| Rejected `ROUND_MISMATCH` | `Rejected` | `true` (`b48935875130eea60a56110fa3dea7b346e8947d3f8af7817789880abb89f987`) |
| exact-byte replay (Applied) | `Applied`, `Replayed=true` | `true` |
| exact-byte replay (Rejected) | `Rejected`, `Replayed=true` | `true` |

### 2.3 Durable receipt construction: the exact loss point

**Accepted (ordinary business command):**

```text
TableActor.cs:3766  durableReceipt = new TableDurableReceipt(
TableActor.cs:3767      accountId,
TableActor.cs:3768      scopeId,
TableActor.cs:3769      envelope.RequestId,
TableActor.cs:3770      api,
TableActor.cs:3771      digest,                       // raw TablePayloadSha256
TableActor.cs:3772      Receipt.Applied(
TableActor.cs:3773          evaluated,
TableActor.cs:3774          commandVersion,
TableActor.cs:3775          lastEventSeq,
TableActor.cs:3776          null,                    // reasonCode
TableActor.cs:3777          createdUtc == 0 ? (ulong?)null : createdUtc),
                                                      // <-- optional canonicalPayloadDigest not passed
```

**Accepted/Rejected (cheat business command):**

```text
TableActor.cs:4209  change.Accepted
TableActor.cs:4209      ? Receipt.Applied(evaluated, commandVersion, lastEventSeq, null, committedAt)
TableActor.cs:4210      : Receipt.Rejected(evaluated, reasonCode),
                                                      // <-- neither passes canonical digest
```

**Rejected (ordinary business command):**

```text
TableActor.cs:4482  var receipt = new TableDurableReceipt(
TableActor.cs:4483      scope.AccountId,
TableActor.cs:4484      scope.ScopeId,
TableActor.cs:4485      requestId,
TableActor.cs:4486      api,
TableActor.cs:4487      payloadSha256,                 // raw
TableActor.cs:4488      Receipt.Rejected(rejection.EvaluatedStateVersion, rejection.ReasonCode),
                                                      // <-- no canonical digest
TableActor.cs:4489      rejection.Detail,
TableActor.cs:4490      createdUtc,
TableActor.cs:4491      ReceiptExpiry(createdUtc, durable));
```

`Receipt.Applied` / `Receipt.Rejected` already accept a trailing `PayloadDigest? digest = null` (`ProtocolReceipt.cs:121-172`). This is a caller omission, not an API limitation.

Why the actor never receives the domain digest:

- `TableCommandMapper.Map` computes and passes only the raw `TablePayloadSha256` (`TableCommandMapper.cs:287`); `TableCommandEnvelope` / `TableCheatCommandEnvelope` have `PayloadSha256` but no canonical digest (`TableRuntimeContracts.cs:193-269`).
- `DispatchCommandAsync` computes `wireDigestHex` at `:1481`, but `SubmitAsync` / `SubmitCheatAsync` receive only the envelope (`TableSessionHandler.cs:1529-1534`); that value flows only to `MapReceipt`, never into the actor commit path.
- `CommitRejectedReceiptAsync` also passes `commandJournal: null` (`TableActor.cs:4501`), so a rejected row has no canonical command bytes in the DB for offline recomputation.

**Secondary structural point:** `TableDurableReceipt.ToCommandReceipt` constructs `TableCommandReceipt.Rejected(...)`, which has no digest field (`TableDurableContracts.cs:353-366`, `TableRuntimeContracts.cs:596-712`). This is not the direct cause of the empty lookup (lookup reads `receipt.Receipt.AsReplay()` directly), but fixing only `ToCommandReceipt` would still not make lookup return a digest.

### 2.4 Schema, write transaction, and retrieval

Schema (`001_table_store.sql:102-139`):

```sql
CREATE TABLE IF NOT EXISTS table_store.request_receipt (
    ...
    canonical_payload_digest bytea NULL,
    ...
    CONSTRAINT ck_receipt_canonical CHECK (
        canonical_payload_digest IS NULL OR octet_length(canonical_payload_digest) = 32),
    ...
);

ALTER TABLE table_store.request_receipt
    ADD COLUMN IF NOT EXISTS committed_at_utc_ms bigint NULL,
    ADD COLUMN IF NOT EXISTS canonical_payload_digest bytea NULL;
```

The write is one transaction (`PostgresTableStore.Commit.cs:33-36`; accepted events/journal/state plus receipt at `:293-379`; receipt insertion at `:375-379`). `InsertReceiptAsync` writes:

```csharp
// PostgresTableStore.Commit.cs:775-780
AddBytea(command, "canonical_digest",
    receipt.Receipt.CanonicalPayloadDigest.HasValue
        ? receipt.Receipt.CanonicalPayloadDigest.Value.AsSpan().ToArray()
        : null);
```

The persistence layer therefore loses nothing: it faithfully writes NULL. On retrieval, `TryGetAsync` reads the nullable column (`PostgresTableStore.Ports.cs:352-410`), and `RestoreReceipt` preserves null or non-null for Applied/Recorded/Rejected (`PostgresTableStore.cs:181-225`); `LoadReceiptForUpdateAsync` does the same (`PostgresTableStore.Commit.cs:604-655`). `PostgresR1RegressionTests.ReceiptOptionalFields_ArePreservedForAppliedRecordedAndRejected` covers Applied/Recorded/Rejected null/non-null combinations (`PostgresR1RegressionTests.cs:261-297`); that test was **PG NOT_RUN** here.

### 2.5 Retrieval and DTO mapping

`QueryReceiptAsync` reads the durable receipt and returns `ReceiptLookupResult.Found(receipt.Receipt.AsReplay())` (`TableActor.cs:382-411`). `ReceiptLookupResult.Found(Receipt)` uses `receipt.CanonicalPayloadDigest` as its `Digest` (`ProtocolReceipt.cs:260-263`); a null field yields a null lookup digest.

`DispatchReceiptQueryAsync` (`TableSessionHandler.cs:1732-1802`) only does:

```csharp
case ReceiptLookupStatus.Found:
    wire.Status = LookupStatus.Found;
    wire.Receipt = MapLookupReceipt(lookup.Receipt!);
    break;
```

`MapLookupReceipt` (`:1849-1876`):

```csharp
if (receipt.CanonicalPayloadDigest.HasValue)
{
    wire.CanonicalPayloadSha256 = receipt.CanonicalPayloadDigest.Value.ToHex();
}
```

It never sets the top-level `ReceiptLookup.canonical_payload_sha256`. Therefore:
- a null durable digest produces an empty inner `Receipt.canonical_payload_sha256`;
- the top-level `ReceiptLookup.canonical_payload_sha256` is always empty, even if the inner field is present;
- the independent probe measured both Applied and Rejected as empty, and the top level empty.

For contrast, LocalHost's `HandleReceiptQueryAsync` sets both the top-level and inner digest (`LocalTableRuntime.cs:1808-1863`), and its test asserts the top-level value equals the immediate receipt digest (`LocalControlTests.cs:2642-2650`). That is evidence of an intended LocalHost behavior, but LocalHost is a G2 candidate, not the remote production contract.

### 2.6 Client outbox and reconciliation

- `OutboxEntry.Digest = command.Payload.Digest` (`Outbox.cs:54`), `DigestSha256 => Digest.ToHex()` (`:83`); the outbox identity is the MJC1 domain digest.
- Immediate receipt: Conflict only when the wire digest is non-empty and unequal (`TableSession.cs:1373-1381`).
- Lookup: top-level and inner digests are both checked only when non-empty and unequal (`TableSession.cs:1452-1503`).
- `BuildResolution`: `string digest = string.IsNullOrEmpty(receipt.CanonicalPayloadSha256) ? expectedDigest : receipt.CanonicalPayloadSha256;` (`:1759-1775`). An empty server digest is replaced by the outbox expected digest.
- `FileOutboxFormat` persists the resolution's `CanonicalPayloadSha256` (`:260`, `:799`), so the fallback survives a cold restart.

Independent direct probe:

```text
clientLookupFallbackDisposition        = Rejected
clientLookupFallbackDigest             = 4b9def... (equals outbox expected)
clientLookupFallbackUsesExpectedDigest = true
clientLookupFallbackFromLookup         = true
```

**Semantic conclusion:** the Client can reconcile, but the server does not provide a verifiable canonical identity. This is "local expected-digest binding plus server receipt fields/version", not "first receipt, lookup, and outbox all equal". B1's `CanonicalReceiptIdentityVerified=false` is accurate.

### 2.7 Request-id conflict semantics

- The actor's receipt-first step looks up `(accountId, scopeId, requestId)`; `existing.Matches(api, rawPayloadSha256)` true means replay, while a different API or raw hash means `TableOperationResult.Conflict` (`TableActor.cs:1141-1160`). The conflict occurs before scope/state validation and before new business evaluation, and never overwrites the original receipt.
- The store's `CommitAsync` performs the same receipt-first + `Matches` inside the transaction (`PostgresTableStore.Commit.cs:53-72`), so concurrent retries cannot overwrite it either.
- `ReceiptIdempotency.Classify` uses the canonical `PayloadDigest?` semantics, but it is **not called** on the remote production path; remote idempotency actually uses raw `TablePayloadSha256`. Adding or backfilling canonical digest neither changes nor should change the conflict key.
- Independent cleartext-loopback WS measurements:
  - Applied same key, different bytes -> `REQUEST_ID_CONFLICT` / `REQUEST_KEY_CONFLICT`; the original Applied receipt is not overwritten.
  - Rejected same key, different kind -> `REQUEST_ID_CONFLICT` / `REQUEST_KEY_CONFLICT`; the original Rejected receipt is not overwritten.
  - Both exact-byte replays -> original status/reason/version, `Replayed=true`, and the immediate digest still equals the domain digest.

### 2.8 Scope isolation

`DispatchReceiptQueryAsync` first calls `IsAuthorizedReceiptScopeAsync` (`TableSessionHandler.cs:1764-1768`, `:1815-1847`):

- The connection's own scope is allowed.
- Any other scope must be found and must have the same `accountId`, `roomId`, and role; otherwise `SCOPE_MISMATCH`.
- Expired/revoked scopes do **not** block lookup (historical scopes remain reconcilable by design, D-1-09); new commands are still rejected by `ValidateScope`.
- The actor's `QueryReceiptAsync` additionally checks that the scope's account equals the queried account (`TableActor.cs:398-411`), and the store key includes `accountId + scopeId + requestId`, providing defense in depth.

Independent cleartext-loopback WS measurements:

| Probe | Result |
|---|---|
| Account B uses A's scope + A's requestId | `Error` / `MALFORMED_MESSAGE` / detail `SCOPE_MISMATCH` |
| Account B uses **B's own scope** + A's requestId | `ReceiptLookup` / `NotFound` (key isolation; no leak) |
| Nonexistent scope | `Error` / `MALFORMED_MESSAGE` / detail `SCOPE_MISMATCH` |
| Same-account, different historical scope | **NOT_TESTED** — this probe did not exercise it |
| A's own scope + A's requestId | `Found`, original status/reason/version preserved |

### 2.9 Cold restart and pre-existing rows

- **PG server cold restart:** `TryGetAsync` reads `request_receipt` directly by primary key, independent of whether the actor just recovered; a NULL digest returns NULL. Code paths: `PostgresTableStore.Ports.cs:352-410`, `PostgresTableStore.cs:181-225`. No PostgreSQL was started in this report; **PG cold-restart validation is NOT_RUN**; this is code evidence.
- **Client cold restart:** the outbox persists the canonical payload and expected digest; the resolution digest is persisted too (`FileOutboxFormat.cs:260,799`). The `BuildResolution` fallback remains available after reconnect. The direct probe verifies the fallback branch; no real process kill/restart was performed here (the B1 gate did a crash-worker run within its own scope, but this report does not reuse its conclusions).
- **Pre-existing rows:** the schema's `ADD COLUMN IF NOT EXISTS ... NULL` gives old databases/rows NULL; `RestoreReceipt` preserves NULL. Existing rows (whether from the older schema or the current producer) have no canonical digest. **There is no universal backfill path:**
  - `request_receipt` stores only `payload_sha256` (raw SHA-256) and an encrypted detail string; it does not store the canonical payload bytes, and a raw SHA-256 cannot be converted into the domain digest (different domain prefix).
  - Rejected receipts have no `table_command_journal` (`CommitRejectedReceiptAsync` passes `commandJournal: null`; the B1 negative fact also asserts journal=0), so rejected old rows have no command bytes that could be re-hashed.
  - Accepted receipts have a journal, but it stores `RoundCommandCodec` / `TableCheatCommandCodec` encodings, not the original wire canonical payload bytes. A theoretical "re-encode the journal into canonical bytes and verify against `payload_sha256`" path would require a new deterministic encoder and byte-for-byte proof; it is not a minimal migration default.
  - Any `NOT NULL` migration or "fill old rows" proposal must first solve the rejected-row problem. Using the raw hash as a substitute domain digest would fabricate identity; fabricating a digest is forbidden.

---

## 3. What do existing contracts actually require?

| Source | Fact | Implication for "lookup must return a non-empty digest" |
|---|---|---|
| `Contracts.Wire/mahjong.proto:278-321` | `Receipt.canonical_payload_sha256` and `ReceiptLookup.canonical_payload_sha256` are plain strings; no `optional` presence keyword | Empty string is wire-valid; proto itself does not force non-empty |
| `Contracts/ProtocolReceipt.cs:111-172` | `PayloadDigest? CanonicalPayloadDigest`; `Receipt.Applied/Rejected` take an optional `digest` defaulting to null | Contract type permits Applied/Rejected without a digest |
| `V5-0-PROTOCOL-DECISIONS.md:398-401` | Same key/same payload returns the original definitive receipt's original fields (`status/reason/evaluated/command_version/event_seq`, etc.), not recomputed and not emptied | "Original fields" can be read as including digest; but the listed fields do not include digest, and "etc." is not a hard list |
| `V5-0-PROTOCOL-DECISIONS.md:410-413, 425-427` | Lookup returns original definitive fields; REJECTED writes a receipt but no events; queries create no business batch | Does not explicitly require a non-empty lookup digest, nor explicitly permit empty |
| `V5-0-1-IMPLEMENTATION-TASKS.md:103-106` (P3.4) | Same key/same payload returns the original receipt; same key/different payload conflicts; REJECTED writes a receipt | If the original delivered receipt had a digest, lookup should preserve it; but it does not define whether the producer must put the wire digest into the durable receipt |
| `V5-0-1-IMPLEMENTATION-TASKS.md:177-182` (P5.2) | Same key/same payload returns the original result; events/receipt/outbox in one transaction | Same as above; no digest mandate |
| `001_table_store.sql:102-139` | `canonical_payload_digest bytea NULL`; CHECK only validates length | Schema explicitly optional |
| `PostgresR1RegressionTests.cs:261-297` | Applied/Recorded/Rejected null/non-null round-trip assertions | P5.2 accepts null; the persistence contract does not require the producer to write non-null |
| `SessionFlowTests.cs:149-180` | Explicitly asserts the lookup receipt digest is empty and comments "frozen-contract boundary / Client binds its own retained expected digest" | **The current TableService test contract explicitly accepts an empty lookup digest** |
| `NegativeProbes.cs:465-496` + `B1Models.cs:236` | `CanonicalReceiptIdentityVerified = FirstReceiptDigestVerified && LookupDigestAvailable && LookupDigestMatchesExpected`; `preserved` allows `!digestAvailable` but records `DigestDomainMismatch=true` | The B1 acceptance criterion requires non-empty; the suite itself does not fail on empty, so B1-BLOCKER-03 is a criterion-level BLOCKED, not a suite failure |
| `LocalTableRuntime.cs:1808-1863` + `LocalControlTests.cs:2642-2650` | LocalHost lookup writes both top-level and inner digest; the test asserts the top-level value equals the immediate digest | LocalHost implementation/tests expect non-empty, but LocalHost is a G2 candidate, not the remote production contract |
| `V5-REMOTE-RECEIPT-TICKET-DELIVERY-20261029.md:100, 338` | Explicitly does not forward the actor raw digest; the Client uses its retained expected digest for an empty lookup digest | This is a deliberate compatibility trade-off for the current state; it does not prove "lookup must be non-empty", nor does it prove the empty digest can be labeled verified |

**Conclusion (without making the owner's product decision):** the frozen wire/schema contracts **permit** an empty lookup digest; the B1 acceptance and LocalHost behavior **expect** non-empty; the protocol-decision wording sits between the two. No single contract simultaneously supports "B1-BLOCKER-03 must be fixed in code" and "the current test explicitly accepts empty". Therefore:
- the current empty digest cannot be written as PASS;
- "B1-BLOCKER-03 is closed" cannot be written as fact;
- the contract owner must choose either "lookup digest must be non-empty" or "lookup digest is optional, the Client binds the outbox expected digest, and the gap is classified as BLOCKED/compatibility".

---

## 4. Independent executable probes and evidence

### 4.1 Probe project

- Path: `build/b1-probe/` (ignored, not committed).
- Contents: `B1DigestProbe.csproj` + `Program.cs`; references the candidate's `Mahjong.Contracts`, `Contracts.Wire`, `TableRuntime`, `Client`, `TableService`, and the public test support from candidate `Mahjong.TableService.Tests` (real Kestrel over cleartext loopback HTTP/1.1 WebSocket (ws://) / memory-mode store).
- No PostgreSQL. `TableServiceTestHost` defaults to `TableHostStoreMode.Memory` (`TableServiceOptions.cs:112`), so this is a real socket + real TableService + in-memory durable store, not a mock transport.
- The probe modifies no candidate source, asserts nothing about candidate tests, and writes nothing into the candidate directory; it builds/runs only against the ignored copy.
- Hashes:
  - `Program.cs` SHA256 `6226BDEBDCF3285BBAB0E3027AD2EAB3EB7476C5717ED40C8274ACA462092BCC`
  - `B1DigestProbe.csproj` SHA256 `D83ADE8EC92F633F13F884A985DFCE88DF265B4FB79A4D3BAE3088F4A2BA1196`
  - `probe-result.json` SHA256 `FC6C49227A8B939293115E03375FF6223173782749D1A9A554645C1442DC2AFF`
  - `probe-console.log` SHA256 `C68DA2918F2AFD950ABB779610612A8AA8FB2D8139ADC147EEFFB266A814F0E9`

### 4.2 Direct probe (no socket; production types + reflection)

| Observation | Measurement |
|---|---|
| MJC1 domain digest vs bare SHA-256 | different (`4b9def...` vs `39259c...`) |
| `TableDurableReceipt` (Rejected, no digest) inner digest | `HasValue=false` |
| Same, derived lookup digest | `""` |
| `TableDurableReceipt` (Applied, no digest) inner digest | `HasValue=false` |
| Same, derived lookup digest | `""` |
| `Matches(same api, same raw)` | `true` |
| `Matches(different api, same raw)` | `false` |
| `Matches(same api, different raw)` | `false` |
| `ReceiptIdempotency.Classify(null, incoming)` / equal / different | `New` / `Replay` / `Conflict` |
| `MapReceipt(rejected, domain)` | status `Rejected`; digest equals domain (`true`) |
| `MapLookupReceipt` (inner digest null) | `""` |
| `MapLookupReceipt` (inner digest non-null; only to show the mapping itself works) | equals domain (`true`) |
| Client `BuildResolution` (wire digest empty) | `Rejected`; digest = outbox expected; `FromReceiptLookup=true` |
| Client `BuildResolution` (wire digest non-empty, different) | returns the server digest; the actual Conflict gate is the non-empty comparison in `HandleReceiptLookupAsync` |

### 4.3 Cleartext loopback WS probe (memory-mode TableService)

Transport boundary: `TableServiceTestHost.cs:29` defines `WsUrl = "ws://127.0.0.1:" + Port + TableServiceWebHost.TablePath`, and `:31` defines `BaseUrl = "http://127.0.0.1:" + Port`. The default memory-mode host configures Kestrel on cleartext HTTP/1.1 without `UseHttps` (`TableServiceWebHost.cs:91-103`), and the captured console shows `Now listening on: http://127.0.0.1:8703` plus `HTTP/1.1 GET ... - 101`. This is a **plaintext WebSocket upgrade over loopback HTTP, not WSS/TLS**; HTTP 101 is the WebSocket upgrade response, not TLS evidence. The probe therefore does **not** validate TLS, certificates, HTTPS admission, or WSS. Those remain **NOT_RUN**.

Commands and native exits:

```powershell
dotnet build build\b1-probe\B1DigestProbe.csproj -c Release --nologo -v:minimal
# exit 0 (only SYSLIB0050 obsolete warning)

dotnet build\b1-probe\bin\Release\net8.0\B1DigestProbe.dll
# exit 0; result=PROBE_COMPLETED
```

Key output (full JSON: `build/b1-probe/out/probe-result.json`):

```text
hostMode = TableHostStoreMode.Memory (no PostgreSQL)
appliedImmediateStatus = Applied
appliedImmediateDigest = 6d0f4b074d605fda7b5b03ce093f1c0d9aee737afe87d0b26da9a0ce5a010cd8
appliedImmediateDigestEqualsDomain = true
appliedLookupStatus = Found
appliedLookupReceiptStatus = Applied
appliedLookupDigest = ""
appliedLookupDigestAvailable = false
appliedLookupTopLevelDigest = ""
appliedSameKeyDifferentBytesErrorCode = REQUEST_ID_CONFLICT
appliedSameKeyDifferentBytesDetail = REQUEST_KEY_CONFLICT
appliedExactReplayStatus = Applied
appliedExactReplayReplayed = true
appliedExactReplayDigestEqualsDomain = true

rejectedImmediateStatus = Rejected
rejectedImmediateReason = ROUND_MISMATCH
rejectedImmediateDigest = b48935875130eea60a56110fa3dea7b346e8947d3f8af7817789880abb89f987
rejectedImmediateDigestEqualsDomain = true
rejectedSameKeyDifferentKindErrorCode = REQUEST_ID_CONFLICT
rejectedSameKeyDifferentKindDetail = REQUEST_KEY_CONFLICT
rejectedExactReplayStatus = Rejected
rejectedExactReplayReplayed = true
rejectedExactReplayDigestEqualsDomain = true
rejectedLookupStatus = Found
rejectedLookupReceiptStatus = Rejected
rejectedLookupReason = ROUND_MISMATCH
rejectedLookupDigest = ""
rejectedLookupDigestAvailable = false
rejectedLookupTopLevelDigest = ""
rejectedLookupPreservedStatus/Reason/Version = true/true/true

crossAccountForeignScopeBody = Error
crossAccountForeignScopeErrorCode = MALFORMED_MESSAGE
crossAccountForeignScopeDetail = SCOPE_MISMATCH
otherAccountOwnScopeLookupStatus = NotFound
nonexistentScopeBody = Error
nonexistentScopeErrorCode = MALFORMED_MESSAGE
nonexistentScopeDetail = SCOPE_MISMATCH
```

Interpretation:
- Both Applied and Rejected reproduce the empty lookup digest; the B1 gate covered only Rejected, so this adds the Applied case.
- Immediate receipt, exact replay, and same-key conflict all work; the tested scope-isolation cases are cross-account `SCOPE_MISMATCH`, account B's own scope + A's requestId `NotFound`, and nonexistent scope `SCOPE_MISMATCH`. **Same-account, different historical scope was NOT_TESTED.** The problem is specifically the durable receipt -> lookup canonical digest field.
- The top-level lookup digest is also empty; this is a structural omission in `DispatchReceiptQueryAsync`, not a schema/DB problem.

Caller-side independent rerun (reported in this thread): build exit `0`, probe exit `0`, with the same Applied/Rejected empty lookup digest, same-key conflict, and tested cross-account/nonexistent-scope isolation results. This is consistent with the local run above and does not change the transport boundary: the probe is cleartext `ws://`, not WSS/TLS.

### 4.4 Full existing TableService test suite

```powershell
dotnet test build\review-candidate-5ccefd2\dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj -c Release --no-build --nologo -v:minimal
# exit 0
# failed: 0, passed: 167, skipped: 0, total: 167
```

Log: `build/b1-probe/out/tableservice-tests.log`, SHA256 `B7F1000C26618FB9AF82962DDBEBDCBF9DFFF875CDA766AD2B2BB32B5C00554A`.

The 167 tests include `SessionFlowTests.CommandReceipt_ReplayAndDurableLookup`, which explicitly asserts an empty lookup digest and passes. Thus the current TableService test contract accepts an empty lookup digest; the B1 three-way identity is not a current test-contract requirement. The 167 tests are memory-mode, not PostgreSQL, and cannot replace the B1 44-test HTTPS/WSS/PG suite.

### 4.5 Not run (must stay separate)

| Item | Status | Reason |
|---|---|---|
| B1 HTTPS/WSS admission suite on PostgreSQL 16.10 | **NOT_RUN** | avoid shared PG; this report does no PG resource work |
| B1 44 HttpsAdmission tests | **NOT_RUN** | same; the gate is independently run elsewhere |
| LocalHost gate / LocalHost cold restart | **NOT_RUN** | explicitly independent of the running LocalHost gate |
| PG cold restart / pre-existing-row backfill measurement | **NOT_RUN** | no PG; code evidence only |
| B2 full match / G3 / real public network / devices | **NOT_RUN** | outside this report |
| `PostgresR1RegressionTests.ReceiptOptionalFields...` | **NOT_RUN** | PG test; code review only |

---

## 5. Minimal safe remedy assessment

> These are options, not implementations. This report changes no production code. Any option first requires the contract owner to resolve §3's "must the lookup digest be non-empty?" question.

### 5.1 Option A: persist and return the MJC1 digest for new receipts (if the owner says non-empty is required)

Minimal change surface:

1. Add an optional `PayloadDigest? CanonicalPayloadDigest` (default null, source-compatible) to `TableCommandEnvelope` / `TableCheatCommandEnvelope`. It must be computed by the trusted host from the same canonical bytes, never supplied by a client field.
2. In `TableCommandMapper.Map`, compute `CanonicalPayloadDigest.Compute(payload.AsSpan())` near `:287` and pass it into `MapSubmitAction` / `MapCheat` so the envelopes carry it.
3. Use it at the three constructors:
   - accepted ordinary command: `:3772` `Receipt.Applied(..., digest: envelope.CanonicalPayloadDigest)`;
   - cheat accepted/rejected: `:4209-4210`;
   - ordinary rejected: `:4488` `Receipt.Rejected(..., digest: command.CanonicalPayloadDigest)`.
4. Do not change `TablePayloadSha256`, `Matches`, or `ProcessAdmittedCommandAsync` ordering; the raw hash remains the idempotency/conflict key, and the canonical digest is only wire/audit identity.
5. Persistence, retrieval, and `MapLookupReceipt` already support non-null values; no DDL is required. Optionally set the top-level `ReceiptLookup.CanonicalPayloadSha256` to the same value to match LocalHost.
6. Tests: add "Applied lookup digest == immediate digest == outbox expected" and "Rejected same"; change `SessionFlowTests`' empty-digest assertion only as part of an explicit contract change, never as a test-side relaxation; keep same-key/scope negatives; add a legacy-null-row compatibility test.

Pros: no wire schema change; no conflict semantics change; new rows satisfy B1 three-way identity; old rows still use the Client fallback.
Limits: requires a TableRuntime envelope contract change and review; old rows remain null; rejected old rows cannot be backfilled offline.

### 5.2 Option B: explicitly accept that lookup does not return a digest (if the owner says optional)

- Keep the current code.
- Document in the proto/docs that `canonical_payload_sha256` is optional and empty means "the producer did not persist a wire identity".
- The Client continues to bind with the outbox expected digest.
- B1 records `CanonicalReceiptIdentityVerified=false` as a contract-permitted digest-unavailable result, not as PASS.
- Do not write B1-BLOCKER-03 as closed/verified; it is an adjudicated compatibility gap, not a proven identity.
- This changes the B1 acceptance criteria/documentation, which is a contract/product-owner decision, not a code fix.

### 5.3 Option C: hybrid (recommended for migration, still owner approval required)

- New receipts persist the digest (Option A).
- Old rows remain NULL and use the Client fallback.
- Add an explicit `digest_available` / `digest_state` semantic or documentation distinguishing "not captured" from "verified".
- B1 accepts classified results for old rows and requires three-way equality only for new rows.
- Do not add `NOT NULL` and do not perform unreliable backfill.

### 5.4 Explicitly unsafe alternatives

| Alternative | Why it is unsafe |
|---|---|
| Fill `canonical_payload_digest` with `payload_sha256` (raw) | Different domain; creates a fake identity that does not match the Client outbox, so B1 still conflicts |
| Forward the actor raw digest directly to lookup | The Client resolves every lookup as Conflict (already measured as B1-BLOCKER-01b) |
| Mark `CanonicalReceiptIdentityVerified=true` while the lookup digest is empty | Relaxes/fabricates the check; forbidden by the task |
| Remove the digest comparison from the Client | Weakens identity/reconciliation; not a minimal safe fix |
| Post-commit UPDATE to fill the digest | Breaks the receipt/event/outbox same-transaction guarantee; a crash window leaves "committed without digest" |
| Add a `NOT NULL` constraint | Old rows, especially rejected, cannot be reliably backfilled; would block migration/startup |
| Reconstruct a rejected old row's digest from DB | Rejected rows have no command journal and no canonical payload bytes; cannot be reliably reconstructed |

---

## 6. Migration, backward compatibility, and security

### 6.1 Migration and compatibility

- **Wire:** no new field; `canonical_payload_sha256` already exists. Old Clients ignore a non-empty value; new Clients still use the fallback when it is empty. A non-empty value only adds one identity check.
- **Schema:** `canonical_payload_digest` already exists and is nullable; writing non-null values needs no DDL. Do **not** add `NOT NULL` without a universal backfill.
- **Pre-existing rows:** they stay NULL; `RestoreReceipt` already handles this. Their lookup remains empty and the Client fallback still works; B1 three-way identity still fails for them. If the owner requires every row to be non-null, a separate migration/replay/manual reconciliation is required; rejected old rows cannot be reconstructed from the DB.
- **Cold restart:** PG reads NULL unchanged; the Client outbox's expected digest is persisted, so the fallback works. After the new write path, new rows still return a digest after restart; old rows remain NULL.
- **Mixed state:** new and old rows coexist. Tests must cover both null and non-null. A new-row pass cannot be used to claim old-row identity.

### 6.2 Security implications

- The canonical digest is a **non-secret** domain-separated hash. Returning it does not reveal the canonical payload contents (only a comparable identity, already exposed to the same degree by the immediate receipt/outbox). Do not log raw payloads, tickets, or keys; the digest itself may be logged as needed.
- It must be computed by the server from the canonical bytes. **Never trust a client-supplied digest**, and never let it override scope/account/room/role.
- Keep raw `TablePayloadSha256` as the idempotency/conflict key. Do not replace it with, or mix it with, the canonical digest (the B1-BLOCKER-01b root cause was exactly domain mixing).
- Preserve receipt-first conflict ordering and zero-side-effect semantics; adding a digest must not change "same key/different payload -> REQUEST_ID_CONFLICT, original receipt unchanged".
- Preserve scope isolation: lookup still requires the same account/room/role or the current scope; adding a digest must not bypass `SCOPE_MISMATCH` / NotFound zero-leak semantics.
- The current "empty digest + Client fallback" is not an immediate authentication bypass in the production WSS/TLS deployment (trusted server and request-key/scope checks remain), but it prevents the server from proving that a lookup result is bound to the payload. This is a defense-in-depth/auditability gap and the direct reason the B1 three-way identity cannot pass. It must not be described as "no security impact". This statement does not imply the local probe used TLS; the probe is cleartext `ws://` only.

---

## 7. Genuine decisions and dependencies

| ID | Type | Content | Owner |
|---|---|---|---|
| D1 | **Product/contract decision (blocking)** | Must the lookup canonical digest be non-empty? If yes, use Option A/C; if optional, use Option B and classify it explicitly, never as verified/closed | contract owner / B1 owner |
| D2 | **Migration decision** | How to treat old rows (especially rejected rows with no journal): keep NULL + Client fallback, or require replay/manual reconciliation/accept that they cannot be reconstructed? Using the raw hash as a fake domain digest is forbidden | contract owner + storage owner |
| D3 | Technical dependency | If Option A is chosen, adding optional canonical digest to `TableCommandEnvelope` / `TableCheatCommandEnvelope` is a TableRuntime contract change requiring its owner's review; `TableCommandMapper` is used only by TableService, so that surface is contained | TableRuntime/TableService owner |
| D4 | Verification dependency | PG cold restart, old-row reads, and the B1 44-test HTTPS/WSS/PG suite must be rerun in an isolated PG environment; they are **NOT_RUN** here and cannot be replaced by memory-mode/167 tests | caller / B1 gate owner |

This report does not choose D1 or D2; it does not turn a recommendation into a decision.

---

## 8. Judgment matrix

| Dimension | Judgment |
|---|---|
| Exact candidate extraction / tree equality | **PASS** (`c6affca...`, 2315 files, TREE_EQUAL=True) |
| 451f4ec vs 5ccefd2 production code identity | **PASS** (only the gate-review document added, 283 lines) |
| Immediate Applied/Rejected canonical digest | **PASS** (cleartext loopback WS equals domain digest) |
| Probe transport | **PASS only as cleartext loopback WS (`ws://`, HTTP 101)**; not WSS/TLS; WSS/TLS validation **NOT_RUN** |
| Exact-byte replay digest | **PASS** (Applied/Rejected both equal domain digest, `Replayed=true`) |
| Durable receipt / lookup Applied digest | **BLOCKED/FAIL** (probe: `Found/Applied`, digest empty) |
| Durable receipt / lookup Rejected digest | **BLOCKED/FAIL** (probe: `Found/Rejected/ROUND_MISMATCH`, digest empty) |
| Top-level `ReceiptLookup.canonical_payload_sha256` | **BLOCKED/FAIL** (probe: always empty) |
| Same-key different bytes/kind conflict | **PASS** (`REQUEST_ID_CONFLICT`/`REQUEST_KEY_CONFLICT`, original receipt preserved) |
| Scope isolation | **PASS for tested cases** (cross-account `SCOPE_MISMATCH`; account B's own scope + A's requestId `NotFound`; nonexistent scope `SCOPE_MISMATCH`); **same-account other historical scope NOT_TESTED** |
| Client outbox fallback | **PASS (compatibility behavior)**, but not server-verified identity |
| PG schema/write/read consistency | **Code-evidence PASS; PG measurement NOT_RUN** |
| PG cold restart / pre-existing-row backfill | **NOT_RUN** (code evidence: NULL preserved; no universal backfill) |
| Existing TableService full suite | **PASS** (167/167, 0 failed/skipped, memory-mode, exit 0) |
| B1-BLOCKER-03 | **OPEN/BLOCKED** (not closed) |
| B1 HTTPS/WSS/PG suite | **NOT_RUN** (this report is independent of that gate) |
| PR20 / B1 batch acceptance | **NOT ACCEPTED** (this report is diagnosis only) |
| G2-G5 / real network / devices | **NOT_RUN / unapproved** |
| V4 production baseline | **unchanged; still the only production** |

---

## 9. Evidence index, commit boundary, and limitations

### 9.1 Key source-file SHA256 (candidate `5ccefd2` extraction)

```text
7E05DC5FF0D26341A75B554CF303BC7686BCA8450A2B31855558219433394BFB  dotnet/src/Mahjong.Contracts/ProtocolReceipt.cs
2CE9E027F7C06849B1BBA09AAC42DF1CBD6582CAE6A829D59AC2B8346983B73C  dotnet/src/Mahjong.Contracts/CanonicalPayloadDigest.cs
AF0CB6C57326E74C42C01535688E19C6BCD95FAAFD81EE854E1FFC6A09CEFC84  dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs
B352CC6E84ADFB4A7B962ED68C82CE726CFF2C2DAAD932E073D5406C74E9728E  dotnet/src/Mahjong.TableRuntime/TableActor.cs
D54D3241096F0AB0A2133CD7645B559D234DD9BB228941BF90A2D25AB03C4E60  dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs
37180DE6D542F7DFE2927400F258F6F3F264AA9B765C5B4DE848A0BE46C41F72  dotnet/src/Mahjong.TableService/TableSessionHandler.cs
6B027B74D0CC721EDE022154ECAB27F6883AF4CB5799C43360A5E7CEBC39869C  dotnet/src/Mahjong.TableService/TableCommandMapper.cs
07C23EEE1541E3EA2D879D904FAB1212A54A90B7118175A9328773588CFC1B02  dotnet/src/Mahjong.Client/TableSession.cs
2347EFB16F2A79CF57021D8B9D89FA5070F4CE2BB494CDA07377BC6844B8B5D2  dotnet/src/Mahjong.Client/Outbox.cs
A94989C39D56B28EF29448EAEFCCE9B7E0A6DA3BCA9923A20E1A37244050E51B  dotnet/src/Mahjong.Client/FileOutboxFormat.cs
0A370F4FEA8FC836165FB2040F3E762148322C4E58DC5ECC898B3AE8B9928FB4  dotnet/src/Mahjong.Infrastructure/Migrations/001_table_store.sql
4988DBFABC2436F32A5ECEF110094ECCE2A505AC11461CDE81D2A7EDB23F7060  dotnet/src/Mahjong.Infrastructure/PostgresTableStore.Commit.cs
C89C176BD5CBA378CEDBE050AED40159159929242D578ED52E4A5F8AA00FA0D7  dotnet/src/Mahjong.Infrastructure/PostgresTableStore.Ports.cs
892F50796408C7E212430EC1B7182E91F0FC0B4A39427D989EB38727BF8CA6D1  dotnet/src/Mahjong.Infrastructure/PostgresTableStore.cs
5A03DDB27D483BDB4D335455A31CBBCCB25400A92C82322700172D594BAF52E4  dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs
C9C7406ED35C629A42628D97A583C844365A37CECA77C435AC809F74E42786A3  dotnet/tests/Mahjong.TableService.Tests/SessionFlowTests.cs
5B3F71027E334887CCC192D90ABD098404454574E0C9DBBE5461B59031445496  dotnet/tests/Mahjong.Infrastructure.Tests/PostgresR1RegressionTests.cs
386A70034E98B96181503F3F2C5936773FC58D680402A534F2F6F80959F5CD1C  dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs
805661F328D544A9D6E0825449547F5CB15C5F22E520185C71970A57630FCDBA  dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/NegativeProbes.cs
```

### 9.2 Probe evidence (ignored, not committed)

```text
build/review-candidate-5ccefd2.zip
  SHA256 B90D2FBCAC676DE1F56FB606EDE8E4ECA8DD3E333B0574B461423B058C26DB28
build/b1-probe/Program.cs
  SHA256 6226BDEBDCF3285BBAB0E3027AD2EAB3EB7476C5717ED40C8274ACA462092BCC
build/b1-probe/out/probe-result.json
  SHA256 FC6C49227A8B939293115E03375FF6223173782749D1A9A554645C1442DC2AFF
build/b1-probe/out/probe-console.log
  SHA256 C68DA2918F2AFD950ABB779610612A8AA8FB2D8139ADC147EEFFB266A814F0E9
build/b1-probe/out/tableservice-tests.log
  SHA256 B7F1000C26618FB9AF82962DDBEBDCBF9DFFF875CDA766AD2B2BB32B5C00554A
```

Limitations:
- The probe transport is **plaintext loopback WebSocket (`ws://`, HTTP 101)**, not WSS/TLS. It does not validate TLS, certificates, HTTPS admission, or WSS; those are **NOT_RUN**.
- `probe-result.json` has `hostMode=Memory`; it proves the production TableService command/receipt/actor/mapping paths, not PostgreSQL persistence or restart.
- 167/167 is the full memory-mode TableService test suite, not the B1 44-test suite, and not a PG 16.10 gate.
- The B1 gate's 44/44, real TLS/PG, and Run A/Run B results are prior gate-review evidence; this report did not rerun them and does not rewrite their conclusions.
- This report did not perform a real process kill/restart; cold-restart conclusions are code evidence plus Client outbox persistence evidence.
- This report did not run the LocalHost gate, by instruction.

### 9.3 Commit boundary

- A local commit based on `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
- **The only committed file:** `doc/v5-重构/V5-B1-LOOKUP-DIGEST-CONTRACT-DIAGNOSIS-20261029.md`.
- Do not commit `build/`, candidate code, probe source, logs, certificates, keys, or outbox data.
- No production code, test assertions, unified verifier, `dotnet/Mahjong.sln`, Unity `Resources`/Burst, other worktrees, or machine certificate/global trust changes.
- No push, PR, merge, deployment, delegation/subagent, or `complete.flag`.
- Do not present this report as B1/PR20/G2-G5 acceptance; do not present B1-BLOCKER-03 as closed.
- V4 remains the only production baseline; G2-G5 remain unapproved.

**Final conclusion:** the direct technical cause of B1-BLOCKER-03 is that `TableActor` constructs durable `Receipt.Applied/Rejected` without the MJC1 canonical digest the service has already computed; the omission affects both Applied and Rejected, the nullable schema faithfully persists NULL, and the lookup DTO therefore emits an empty digest. The frozen wire/schema permits empty, but B1 acceptance and LocalHost behavior expect non-empty. This is therefore a contract/acceptance conflict requiring a contract-owner decision, not a defect that can be closed unilaterally in code or by relaxing tests. B1-BLOCKER-03 remains **OPEN/BLOCKED**; this report provides independent evidence, minimal safe remedy assessment, and migration/security boundaries only.
