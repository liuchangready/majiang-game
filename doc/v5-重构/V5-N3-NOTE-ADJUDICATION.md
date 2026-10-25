# CODEX V5 N3 note-meta adjudication (P-NOTE-A: 81 note instances)

Date: 2026-10-26. Worktree: `D:/majiang-wt/n3-note-adjudication` (exclusive), branch `wt/v5-n3-note-adjudication`, frozen baseline/HEAD `6c3573620f61e8cbb6a13c8c4b81abef0b173fd9`. Status at start and at delivery: tracked tree clean; only the two authorized deliverables are added locally.

**This is an independent adjudication of the 81 note instances named by closeout-ledger §10 `P-NOTE-A` (N3-B1 note 6 + N3-B8 note 75). It is not a re-audit, not a re-count, and not a stage acceptance. It does not declare N3 closed, does not approve G1/G5, does not change the shared ledger/TSV/digests/verifier, does not create `complete.flag`, and does not change V4 as the only production architecture.** The caller owns the serial ledger update and the single PR19 integration.

## 0. Result in one table

| Item | Result |
|---|---|
| Frozen identities | **81/81** exact: 6 `GAP:N3-B1` + 75 `GAP:N3-B8`, 81 unique ids, 81 unique caseIds |
| Proposed verdict | **77 classified + 4 pending** |
| Classified split | B1 5, B8 72 (68 domain-anchored, 3 actor-anchored, 1 domain+actor) |
| Pending split | B1 1 (`mv-swap-drawn-with-wall-backfill`), B8 3 (`tm-capacity-tiles`, `tm-decision-mismatch`, `bm-activate-deadwall-empty`) |
| Note text controlled behavior? | **No.** All 81 notes replaced with sentinels: Domain 95/95 PASS, TableRuntime admission 7/7 PASS |
| Same-case structured-field negative | B1 `wallEndExclusiveAfter` 68->69: Domain 3 FAIL; B8 `tm-allowed` outcome/reason: Domain 2 FAIL, TableRuntime 2 FAIL |
| Denominator effect | none: 1516 paths / 655145 nodes / 592 units / 17 UNA untouched; only the 81 note rows are proposed for re-judgement |
| Post-merge vector if caller accepts all 77 | `592 = 411 bound + 159 classified + 22 pending` (B1 18/5/1, B8 108/80/3) |
| Shared files changed by this task | **none** (report + subset TSV only) |

The 4 pending rows are not a failure of the mutation run; they are the honest residue where the note describes a V4-only or unrepresentable behavior and no current C# value-level anchor exists. They are listed in §8 with full obligations.

## 1. Scope and non-goals

**In scope (only these 81 identities):**

- `GAP:N3-B1|cheat-v1|<caseId>|$.tileMovement[*].note|1` × 6.
- `GAP:N3-B8|cheat-v1|<caseId>|$.($.tileMarkHandler|$.sleeveBloomHandler|$.beamSwapHandler)[*].note|1` × 75.
- The fixture is only `tests/v5-golden/cheat-v1.json`; `kind=string`, `idx=1`, `histClass` is `UNBOUND` for B1 and `UNBOUND_HC` for B8.

**Out of scope / deliberately untouched:**

- The 18 AI pending units (`GAP:N3-B6` 11 + `GAP:N3-B9` 7) are not opened.
- Already bound/classified rows of B1/B8 (`outcome`, `reason`, movement scalar fields, `requestException`, etc.) are not re-opened; only the `note` path rows are proposed for re-judgement.
- The 1516/655145/592/17 denominators, the shared `V5-G1-N3-CLOSEOUT-LEDGER.tsv`, its digests, `tools/verify-v5-n3-closeout-ledger.ps1`, the unified verifier, the golden catalog/manifest, production/tests/fixtures, Unity/Resources/Burst, and all previously accepted reports are unchanged.
- No push, no PR, no CI, no machine-level environment/trust change, no sub-delegation.

## 2. Applicable rule N3-META-01 and decision procedure

The caller's rule is applied literally:

> A note that is **only** scenario-explanation text manually passed/concatenated by the test exporter, **and** does not come from a V4 domain return value, **and** does not enter input/event/receipt/state-hash/external protocol, is **classified** (narrative metadata; still uncovered, never bound/full). The rule does **not** exempt the comparable behavior the note describes: that behavior must already have a value-level anchor; if it is missing, the note stays **pending** and is listed in full. A literal-equality assertion on note text cannot be used to fake behavior verification. Field names are not mechanically excluded.

Per-row procedure actually executed:

1. **Origin.** Locate the case call and the note literal in `engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt`; record the case line and the exact note-literal line.
2. **No V4/domain flow.** Confirm the note is a Kotlin string literal passed separately from the V4 `CheatSubmissionCheck`/movement result, and that `case()`/`caseWithInputs()` only append it as a JSON field. Confirm C# production does not read `note`; the only fixture consumer is a non-empty check.
3. **Describe the behavior.** Reduce the note to its observable claim(s): scenario, outcome/reason, and physical/ledger consequence.
4. **Require a current-head value-level anchor.** A classified proposal is allowed only if the described behavior is actually executed and asserted at value level (numeric/string/bool/enum/null-kind/position/container/pointer/charge/event), not by note equality and not by mere existence.
5. **Verdict.** `classified` if 1–4 hold; otherwise `pending` with the missing anchor and the exact obligation. No row is proposed as `bound`.

This is a source/provenance adjudication first; the mutation runs in §6 are corroboration that the text is inert, not the sole basis of the verdicts.

## 3. Frozen identity verification

**Set and uniqueness.** The frozen filter `verdict=pending AND package IN (GAP:N3-B1, GAP:N3-B8) AND path LIKE '*.note'` yields exactly 81 ledger rows; 81 unique ids; 81 unique caseIds; 6 B1 + 75 B8; `kind=string`, `idx=1` for all. The subset is a pure projection of these rows: the first nine columns (id/package/fixture/caseId/path/idx/kind/parent/histClass) are copied byte-for-byte.

**Census.** All 81 six-column rows (fixture, path, caseId, idx, kind, parent) match `D:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7/census/instances.tsv` exactly: **81/81**. The census file SHA is `049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12` (655,145 rows).

**Rules/histClass.** `rules.tsv` (`24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31`) declares:

- `cheat-v1 $.tileMovement[*].note 1 UNBOUND ... GAP:N3-B1`
- `cheat-v1 $.tileMarkHandler[*].note 1 UNBOUND_HC ... GAP:N3-B8`
- `cheat-v1 $.sleeveBloomHandler[*].note 1 UNBOUND_HC ... GAP:N3-B8`
- `cheat-v1 $.beamSwapHandler[*].note 1 UNBOUND_HC ... GAP:N3-B8`

This matches the ledger histClass for all 81 rows.

**Parent provenance caveat (new finding F-02).** The census `parent` cell is a `;`-joined scalar bag. For the 5 B1 notes whose text contains a semicolon, the parent cell stops at the first semicolon, while the fixture note continues. The ledger faithfully reproduces the census truncation, so this is **not** an identity mismatch, but the truncated parent cannot be used as the full note text. The full note is taken from the fixture and hashed in the subset (`noteText`, `noteSha256`); `parentNoteComplete=false` marks the 5 rows.

| Truncated B1 row (5) | Full note SHA-256 |
|---|---|
| `mv-swap-hand-with-wall-direct` | `b1545212259500cfe8b84b601e2a7392361e05b3ee6949f380a44b3addb4499c` |
| `mv-swap-drawn-with-wall-backfill` | `475d2d8b01b35d71007625463811d377d30b94b17d4b5fc5aafba8d28e7b5144` |
| `mv-reservation-delivery-then-beam` | `e6d9835881a01b57f5489e63b13f589fde353ba726aa0c2f464bc43eae9a9787` |
| `mv-rinshan-and-drawn-swap` | `1892d2e62fbf30e304832399e09cad3808092af49a470f24f39e0a85023d8479` |
| `mv-swap-hand-with-dead-wall` | `082f9460099b5af0113a4f8fc4a2a57883b3bb9db6ab29eb83b3e1b5d450fa41` |

The exact full SHA for every row is authoritative in the TSV's `noteSha256`. All 81 `noteSha256` values were recomputed from `noteText` and compared to the fixture during `verify-subset.ps1` (0 mismatches).

## 4. note origin and consumer boundary

### 4.1 Exporter origin

`V5CheatGoldenExporterTest.kt` (SHA `402aa8ebf03b8e3f2aeb5712a94e7fc1c8508ee327981bc014f7a0a2a67902e4`) builds notes in two ways only:

- Handler notes: `case(id, check, expected, note)` / `caseWithInputs(id, check, expected, note, inputs)` at lines 283–304. `checkOf(check)` serializes only the V4 verdict (`outcome`/`reason`); `note` is a separate Kotlin `String` parameter appended at line 294 as `"note" to j(note)`. Every call site passes a literal.
- Movement notes: explicit `"note" to j("<literal>")` inside the movement `obj(...)` blocks (six sites).

No note value is read from a V4 return object, request, event, receipt, hash, or protocol DTO. No `note` variable is computed, formatted from a V4 result, or concatenated at runtime. The special `bm-keep-original-args-forbidden-at-request` row is an explicit `obj(...)` with its own literal at line 947 (caseId line 946).

### 4.2 C# consumer

- `CheatGoldenTests.cs:369`: `Assert.False(Str(c, "note").Length == 0)` — **existence only**; it never compares note content to a value.
- `CheatGoldenBindingTests.cs:1663`: `Assert.False(string.IsNullOrWhiteSpace(spec.Note))` — this checks the **C# manifest's own independently authored literal**, not the fixture note.
- All other `note` occurrences in the two files are comments/prose.
- Production `dotnet/src`: the only `Note` member is `Mahjong.Ai.AiEffect.Note` (an unrelated AI diagnostic field). `Mahjong.Domain` and `Mahjong.Contracts` have no note member; no event, receipt, state hash, or wire DTO carries this note.

Therefore the fixture note is **not** an input, event, receipt, state-hash, or protocol field. It is exporter narrative. That establishes the first half of N3-META-01 for all 81; the second half (behavior anchor) is decided per row in §5/§8.

## 5. Adjudication result

### 5.1 By package and anchor kind

| Package | Rows | classified | pending | Anchor kinds |
|---|---:|---:|---:|---|
| `GAP:N3-B1` (tileMovement note) | 6 | 5 | 1 | 5 domain-behavior; 1 none |
| `GAP:N3-B8` (handler note) | 75 | 72 | 3 | 68 domain-behavior; 3 actor-behavior; 1 domain+actor; 3 none |
| **Total** | **81** | **77** | **4** | |

The 3 actor-anchored notes are `tm-wrong-phase-round-end`, `tm-no-active-decision`, `tm-deadline-expired`; `tm-allowed` is the one domain+actor note. The other 68 B8 notes are Domain-value-anchored. The exact anchor for each row is in the TSV's `behaviorAnchor`/`csRead`/`assertion` columns and in Appendix B.

### 5.2 Why 77 are classified (not bound)

Each of the 77 has: (a) an exporter literal, (b) no V4/domain/protocol flow, and (c) a current-head value-level anchor for the behavior the note describes. Representative mappings:

- **B1 movement.** `mv-reservation-delivery` is anchored by `Movement_ReservationDelivery_BindsFactsAndRemoveAt` (`CheatGoldenBindingTests.cs:2534-2564`: reserved position, wallEndExclusive 69->68, pointer unmoved, reservation cleared, delivery event, factsAfter, conservation). `mv-reservation-delivery-then-beam` is anchored by `DeliveryThenBeam_BindsToGoldenCandidateEntities` (`CheatGoldenTests.cs:963-1124`: 8 candidate ids+slots, delivered tile in hand, absent from candidates, hand tile lands on the first candidate slot, next draw equals `nextDrawTileId`, all `factsAfter`). `mv-rinshan-and-drawn-swap`/`mv-swap-hand-with-dead-wall` are anchored at `CheatGoldenBindingTests.cs:2567-2675` and `CheatGoldenTests.cs:1133-1180`.
- **B8 tile mark.** `tm-allowed` is anchored at `CheatGoldenBindingTests.cs:1888-1889,1950-1968` (Allowed, charge, group id/seat/tile ids/round/ordinal, unchanged before-state) and at `N3TableCheatGoldenAdmissionTests.cs:181-247` (accepted once, 3 events, one ledger entry, one group/4 marked tiles, same receipt on replay). The other bound tile-mark notes are the switch branches at `:1888-1941` plus `AssertRejected` (`:1972`).
- **B8 sleeve.** Each note is the scenario branch of `SleeveBloom_BoundCase_ReplaysThroughDomainEntry` (`CheatGoldenBindingTests.cs:2007-2453`); the `goldenInputs`-bearing rows (`sb-activate-allowed`, `sb-activate-rinshan-allowed`, `sb-choose-first/second/rinshan`, `sbo-activate-allowed`, `sbo-choose-allowed`) compare every input field to the live map/economy at `:2116-2185`, `:2215-2249`, `:2349-2368`, `:2382-2424`.
- **B8 beam.** Each note is the scenario branch of `BeamSwap_BoundCase_ReplaysThroughDomainEntry` (`CheatGoldenBindingTests.cs:2679-2944`); the dora note is anchored by `BeamSwap_DoraIndicator_IsNeverACandidate` (`:2985-2996`, `DoesNotContain DeadWall(4)`) plus the dead-wall activation assertions (`:2759-2772`).
- **Actor notes.** `tm-wrong-phase-round-end` is anchored by `golden_tm_wrong_phase_round_end_rejects_with_the_terminated_round_reason` (`N3TableCheatGoldenAdmissionTests.cs:267-343`, typed `ROUND_FINISHED` at `:320`); `tm-no-active-decision` by `:346-397` (cleared clock timing, `DecisionMismatch`, no side effects); `tm-deadline-expired` by `:400-466` (clock exactly at deadline, `DeadlineExpired`, no side effects). These are **fault-injection** paths (documented in the test), not business-reachable states; the note's scenario is still value-asserted, which is why they are classified, not pending.

### 5.3 The 4 pending rows (full obligations)

| ID | Severity | Location / described behavior | Actual vs expected | Evidence SHA | Fix requirement | Acceptance assertion |
|---|---|---|---|---|---|---|
| `N3-B1\|cheat-v1\|mv-swap-drawn-with-wall-backfill\|$.tileMovement[*].note\|1` | MAJOR (coverage) | exporter line 1113; "after drawNormal ... swapping it backfills that slot with the displaced wall tile" | C# `Movement_DrawnBackfill...` (`CheatGoldenBindingTests.cs:2490-2531`) explicitly asserts the old wall slot is **free** (`:2526-2527`): C# does not materialise V4 hole backfill. No C# value-level assertion reproduces "backfills" | note `475d2d8b01b35d71007625463811d377d30b94b17d4b5fc5aafba8d28e7b5144`; source/run SHAs in Appendix A | Either add/point to a C# value-level assertion that reproduces the V4 backfill slot, or the caller records an explicit acceptance of the V4-only materialisation boundary at `:2526-2527` | A named test/probe must assert the backfilled slot value (not note equality), or the exclusion must be written into the caller's adjudication record; until then the row stays pending |
| `N3-B8\|cheat-v1\|tm-capacity-tiles\|$.tileMarkHandler[*].note\|1` | MAJOR (coverage) | exporter line 418; fixture note text is "8 marked + 4 > 12" but this is an **original narrative typo (8+4=12)**. The actual V4 input at `:418` is `groups=2`, `marked=(16..27)=12` tiles, `selected=0/4/8/12=4` tiles, i.e. **12+4 > 12** | The accepted manifest declares the actual 12-marked/4-selected input `UnrepresentableBlocked` (`CheatGoldenBindingTests.cs:1441-1444`): C#'s exactly-four group state cannot represent `groups=2` with 12 marked tiles. `TileMarkTests.cs:343-366` only anchors the general 3-group/12-tile cap, not the real 12+4 state. No value-level anchor exists; the note text is retained verbatim and is not a behavior spec | note `ed3ed67365865c2caefb557ff03758baac2cede53fd89c684bf57057cddd466a` | Add a reachable C# state/probe that asserts **12 marked + 4 selected exceeds 12** (the real V4 input; do **not** require the false 8+4>12), or caller explicitly accepts the V4-only input boundary | A named test must assert `MarkCapacityExceeded` from the exact 12+4 state, or the exclusion is recorded; note text/SHA unchanged and the typo documented; until then pending |
| `N3-B8\|cheat-v1\|tm-decision-mismatch\|$.tileMarkHandler[*].note\|1` | MAJOR (coverage) | exporter line 425; "no active round" | Accepted manifest declares `ActorLayerBlocked` (`CheatGoldenBindingTests.cs:1461-1464`); the accepted actor always owns a concrete `_round` and has no null-round port. `N3TableCheatGoldenAdmissionTests.cs:468-506` explicitly says its stale-round test is **not** this source case. No value-level anchor | note `92c0327ef8fef41b33d320c5df48b935d052d22baf0a62360d93a03dd8e3606c` | Add a safe null-round port and a value-level assertion for this exact input, or caller explicitly accepts the no-active-round boundary | A named test must execute the null-round input and assert the typed rejection, or the exclusion is recorded; until then pending |
| `N3-B8\|cheat-v1\|bm-activate-deadwall-empty\|$.beamSwapHandler[*].note\|1` | MAJOR (coverage) | exporter line 932; "everything consumed" | Accepted manifest declares `UnrepresentableBlocked` (`CheatGoldenBindingTests.cs:1559-1562`): V4 allows `deadWallTileIdsInDeadWall=empty` while 14 dead-wall entities still exist elsewhere; canonical C# `TilePositionMap` requires exactly 14 effective dead-wall/replacement tiles. No current test executes the empty-filter input | note `693c5650f1042740748570a4d9b635fba76f4bfbf07fca96d6146c2c5e85a946` | Add a handler-level `deadWallTileIdsInDeadWall`/candidate-suppression surface with a value-level empty-candidate assertion, or caller explicitly accepts the non-canonical filter-state boundary | A named test must execute the empty-filter state and assert `NoWallCandidate`, or the exclusion is recorded; until then pending |

These four are exactly the rows where the note's own text points at a behavior that is **not** a current C# behavior. They are not "note text differs"; they are genuine behavior-anchor gaps.

## 6. Empirical evidence: text is inert, behavior fields are live

### 6.1 Build and selection discipline

`dotnet` below is `D:/Unity/dotnet/dotnet.exe` 8.0.426, Debug, net8.0. We did **not** use `dotnet test --filter`; the historical warning that the outer filter is ignored is respected. Selection is by the direct runner `-class` and is confirmed from the result XML.

| # | Command (verbatim, from the worktree) | Native exit | Observed |
|---:|---|---:|---|
| 1 | `dotnet build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj --no-incremental --no-restore -v minimal` | **1** | `NETSDK1004` missing `obj/project.assets.json`; the old DLL path did not exist, so no stale DLL could be run. This is an environment precondition, not a code failure |
| 2 | `dotnet build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj --no-incremental -v minimal` | **0** | restore + build; **0 warnings / 0 errors**; DLL `c6db5af958c021c4a08eb594124c79a2f41fe7dd290aa8588da8a77c475e6d2e`, 659,456 bytes, 2026-10-25T22:22:30Z |
| 3 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-incremental -v minimal` | **0** | **0 warnings / 0 errors**; DLL `d48dcb57e49f764cce4073aec4e41ebb6cd6137fce34d8484e7faa0d56b82f86`, 1,176,576 bytes, 2026-10-25T22:30:23Z |
| 4 | `dotnet <Domain.Tests.dll> -class '*CheatGoldenTests' -class '*CheatGoldenBindingTests' -result-xml <baseline.xml>` | **0** | XML: Total 95, Passed 95, Failed 0, Skipped 0, NotRun 0; XML has exactly 2 collections (the two named classes) |
| 5 | `dotnet <Domain.Tests.dll> -class '*N3NoSuchClassControl' -result-xml <control.xml>` | **0** | XML: Total **0** — proves `-class` actually filters; selection is not "all tests" |
| 6 | `dotnet <TableRuntime.Tests.dll> -class '*N3TableCheatGoldenAdmissionTests' -result-xml <table-baseline.xml>` | **0** | XML: Total 7, Passed 7, Failed 0, Skipped 0, NotRun 0; 1 collection |

Only after build #2 succeeded did any runner execute. The failed #1 build had no DLL at the runner path (`Get-Item` failed), so the "compile failure must not run the old DLL" rule is satisfied.

Exact expansions used for runs #4–#6 (all from the worktree):

- `<Domain.Tests.dll>` = `dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll`
- `<TableRuntime.Tests.dll>` = `dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll`
- `<baseline.xml>` = `artifacts/TEMP/logs/baseline-two-classes.xml`
- `<control.xml>` = `artifacts/TEMP/logs/no-such-class.xml`
- `<table-baseline.xml>` = `artifacts/TEMP/logs/table-baseline.xml`

Variant runs (each is the same runner command with one process-local environment variable set):

```powershell
$env:V5_CHEAT_GOLDEN_PATH = '<worktree>/artifacts/TEMP/variants/cheat-v1.all-notes.json'
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGoldenTests' -class '*CheatGoldenBindingTests' -result-xml artifacts/TEMP/logs/all-notes.xml
# neg-b1 / neg-b8: same, with cheat-v1.neg-b1.json / cheat-v1.neg-b8.json
$env:V5_TABLE_CHEAT_GOLDEN_PATH = '<worktree>/artifacts/TEMP/variants/cheat-v1.all-notes.json'
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -class '*N3TableCheatGoldenAdmissionTests' -result-xml artifacts/TEMP/logs/table-all-notes.xml
# table neg-b8: same, with cheat-v1.neg-b8.json
```

The variants were generated with `powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/make-variants.ps1 -RepoRoot <worktree> -OutDir artifacts/TEMP/variants -Mode <roundtrip|all-notes|neg-b1|neg-b8>`; the semantic diff and the subset checks are in `artifacts/TEMP/logs/variant-semantic-diff.log` and `artifacts/TEMP/logs/verify-subset.log`.

### 6.2 Fixture variants (isolated TEMP copies; the repository fixture is untouched)

`make-variants.ps1` writes UTF-8/no-BOM copies under `artifacts/TEMP/variants/`. A semantic diff proved the generator changes only the intended field:

| Variant | Change | Semantic diff vs `tests/v5-golden/cheat-v1.json` |
|---|---|---|
| `all-notes` | all 81 notes replaced by `N3-META-01-MUTATED-NOTE::<section>::<caseId>`; 81 changed, 0 same, 0 empty, 81 distinct | **0 diffs ignoring `note`** |
| `neg-b1` | `tileMovement[mv-reservation-delivery].wallEndExclusiveAfter` 68 -> 69; note unchanged | **1 diff**, exactly that field |
| `neg-b8` | `tileMarkHandler[tm-allowed].outcome` Allowed -> Rejected, `reason` null -> `NOT_YOUR_TURN`; note unchanged | **2 diffs**, exactly those fields |
| `roundtrip` | no change | **0 diffs** (proves JSON round-trip fidelity) |

### 6.3 Mutation results

| Fixture | Runner class(es) | Total | Passed | Failed | Native exit | Interpretation |
|---|---|---:|---:|---:|---:|---|
| original | Domain `CheatGoldenTests` + `CheatGoldenBindingTests` | 95 | 95 | 0 | 0 | baseline |
| **all-notes** | same two classes | **95** | **95** | **0** | **0** | **all 81 note texts are inert in Domain** |
| `neg-b1` | same two classes | 95 | 92 | 3 | 1 | behavior field is live: `OffTurnReservationPath...`, `TileMovement_ReservationDelivery...`, `Movement_ReservationDelivery...` fail |
| `neg-b8` | same two classes | 95 | 93 | 2 | 1 | behavior fields are live: `BindingManifest_PreservesTheFullDenominator`, `TileMark_BoundCase...("tm-allowed")` fail |
| original | TableRuntime `N3TableCheatGoldenAdmissionTests` | 7 | 7 | 0 | 0 | actor baseline |
| **all-notes** | same class | **7** | **7** | **0** | **0** | **all 81 note texts are inert at the actor-admission layer too** |
| `neg-b8` | same class | 7 | 5 | 2 | 1 | `golden_tm_allowed...` and `golden_tile_mark_handler_cases_are_strictly_typed_and_complete` fail |

The all-notes runs are the direct empirical answer to "does the text control behavior?": no, in both the Domain path and the actor-admission path. The negative controls show the same cases' structured fields **do** control behavior, so the tests are not vacuous. This does not by itself prove the rule; the per-row source anchors in §5 are the primary basis.

### 6.4 Evidence files and SHA-256

All paths are relative to the worktree; `<E>` = `artifacts/TEMP/logs`.

| Evidence | SHA-256 |
|---|---|
| `E/domain-build.log` (failed no-restore) | `665f022796477e9adf0e0ba1b3b9940dfc73245efa0ec2e4e5b5725bf1be2f57` |
| `E/domain-build-restore.log` | `39fe90bbb112bedf53b798937d12e9800219dfc87e1bcbdca10fc752bcccc7a1` |
| `E/table-build.log` | `2b28df1ffde920043dedf2b09e9455a22bf03e150e5978115d47549d67e4b750` |
| `E/baseline-two-classes.log` | `bb31640e82cdfb846a6d9ce42d1530a059e123d873d0bccee6efba8ec18d7bc6` |
| `E/baseline-two-classes.xml` | `c77ea35fbb26a2b765bda2376fa28bfd98d2a9f51d095deaf64bdb8ce09a9526` |
| `E/all-notes.log` | `3fdad72a591e86a30096134938903fc90e3c3ca0f4cff654762dd7c3f09fe6a3` |
| `E/all-notes.xml` | `82dfd35cdba119ecba23ecddc97890fa89e602e2d51ac585d539548ff0b27bad` |
| `E/neg-b1.log` | `b2e701f4bba58013379c4858516bb4ba0ac17df3c10ef0717d87e8fb1cb9a30e` |
| `E/neg-b1.xml` | `f78fb8d421cd567372e11de82df959f6a6ec1a73a30c007d0a3ea1d86e0b1b58` |
| `E/neg-b8.log` | `188bc73496abb2b0dad57586b1e66dea14bebfd0a6230c3dde72d8247b8e7929` |
| `E/neg-b8.xml` | `c2f0da39c1802e64264257c6864391b5a63e91ab103597b2acd5e95168f98f44` |
| `E/table-baseline.log` | `3de97dcfc652c4296f48cc1e6e1764458a0aeb834622c91b57a7e35fdf6eb911` |
| `E/table-baseline.xml` | `6649e1db9947a134c102edcbfa72b7e699ed6ee44bffa6d9995e2d4ffeac2afc` |
| `E/table-all-notes.log` | `7e316752fd64bd33d585085f36b7516417b00389a4b7cc382adc33ca71fe26b5` |
| `E/table-all-notes.xml` | `1735d10f5adf755a968f776e69df6fd4b274708850e4b5f132bf2c2e834daca0` |
| `E/table-neg-b8.log` | `3790960723dae3b384cb1b1fc04df78d740315f6a3bbd65e725df4dbb4771e24` |
| `E/table-neg-b8.xml` | `3d39c6215d990eac05101c415bfa83797845f4d09d1b27a125a69b21fa1fda60` |
| `E/no-such-class.log` | `73270a71a335affdf39b4e80b5802bfc0319176152280063517cb258f12e014f` |
| `E/no-such-class.xml` | `e52924f1f6d2229125aa133e7f8856d91aa7ee392fc84a2227b8bd03526839af` |
| `E/variant-semantic-diff.log` | `357629a986b039e6e1e6a43bf34dea48ca38516296d397ace6ea45e462d5281c` |
| `E/verify-subset.log` | `81441cdafb10d1bd03bedd606d7098e5f5e5634a5516b97417de7a55a6a10ee4` |
| `artifacts/TEMP/variants/cheat-v1.roundtrip.json` | `ab650dcc2d627179952db972271092c04a5913a7df52c6983c3e919bae365a3a` |
| `artifacts/TEMP/variants/cheat-v1.all-notes.json` | `c1c8d662c276d9239592374bad62eb7227015eb0ed93757c224f08a55ceb237b` |
| `artifacts/TEMP/variants/cheat-v1.neg-b1.json` | `7b6ad8968323db7df34aeacd10e91730d9194d232a849620af71199463fd9d70` |
| `artifacts/TEMP/variants/cheat-v1.neg-b8.json` | `48102e812ab50c7f338d2830eac16ecb92886b3d1ebc78614c94474c81cbfc27` |

The subset verifier (`artifacts/TEMP/verify-subset.ps1`, run over the delivered TSV) exits 0 with 14 `CHECK-OK`: row count 81, unique id/case, package 6/75, verdict 77/4, first-nine-column identity match 0 mismatches, note SHA 0 mismatches, fixture text 0 mismatches, exporter case/note lines 0 mismatches, verdict obligations 0, no "counted as full" hits, 5 parent-truncation flags, 29 columns, no BOM. Full log SHA `81441cda...`.

## 7. Reused evidence and this round's NOT_RUN

The accepted cheat binding/mutation evidence is historical. We did not rerun the 46/46 cheat matrix, the 28-case table matrix, round/scoring/physical/shuffle matrices, or any full suite. We did verify that the cited accepted artifacts still match their pinned hashes and that the current source lines exist:

| Pin | Current check |
|---|---|
| `EV-CHEAT-DOC` `a9daf5f5...` (`V5-N3-CHEAT-GOLDEN-BINDING.md`) | hash matches; its 46/46 `FAIL_AS_EXPECTED` matrix is the accepted historical probe; **NOT_RUN this round** |
| `EV-CHEAT-ACC` `a0660e19...` | hash matches; accepted |
| `EV-TABLE-DOC` `add5cb6d...` / `EV-TABLE-ACC` `e401de2d...` | hash matches; accepted |
| `EV-ACCEPT` `8d72f1df...` | hash matches; accepted |
| `EV-R1-DOMAIN` `d3ce8213...` / `EV-R1-TABLE` `57454997...` | historical review logs were absent from this worktree's `artifacts/TEMP`; byte-identical copies from `D:/majiang-wt/n3-closeout-ledger/artifacts/TEMP/codex-review-20261025/{domain,table}.log` hash-match the pins exactly. They are reused read-only; **NOT_RUN this round** |
| `a24b491` (cheat binding), `7d0bb5b` (table admission) | both ancestors of HEAD; commits dated 2026-10-25; current source lines at HEAD match the cited tests |
| `6c35736` | frozen baseline/HEAD; 2026-10-26 |

The 46/46 matrix includes `outcome`/`reason`/ledger/mark mutations, **not** note-text mutations. That is why it cannot be cited as evidence for or against N3-META-01; it is cited only for the behavior fields that underpin the anchors.

## 8. Findings and residual obligations (complete list for this scope)

### F-01 (MAJOR, coverage obligation): four note identities have no current value-level anchor

**Location:** the four pending rows in §5.3. **Repro:** `powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/build-subset.ps1 -RepoRoot <worktree> -OutTsv <temp>` then read the four `verdict=pending` rows; inspect the cited C# methods. **Actual:** the fixture-note existence assertions pass, but the described behavior is either explicitly contradicted (`backfill`) or declared unrepresentable with no executing test (`capacity-tiles`, `decision-mismatch`, `deadwall-empty`). **Expected:** a named current-head test/probe that executes the described state and asserts a value, or a caller-recorded explicit exclusion. **Evidence:** the four note SHAs in §5.3 plus the run evidence in §6.4. **Fix requirement:** do not add a note-equality test; add the behavior anchor or record the exclusion. **Acceptance assertion:** the named behavior test fails when the relevant structured field is mutated and passes on the original fixture; the exclusion, if chosen, is written into the caller's adjudication record and keeps the row out of `full`.

### F-02 (MINOR, provenance): five B1 `parent` cells are truncated at the first semicolon

**Location:** ledger/census `parent` for `mv-swap-hand-with-wall-direct`, `mv-swap-drawn-with-wall-backfill`, `mv-reservation-delivery-then-beam`, `mv-rinshan-and-drawn-swap`, `mv-swap-hand-with-dead-wall`. **Repro:** `$r.parent -match 'note=([^;]*)'` returns only the first clause; compare with the fixture `note`. **Actual:** parent note is a prefix (census scalar-bag encoding). **Expected:** full note text or a full note SHA for audit. **Evidence:** fixture SHA `a196cbbd...`; subset `noteSha256`; `parentNoteComplete=false` on exactly 5 rows. **Fix requirement:** the caller's merged ledger should keep the full note text/SHA in a dedicated field (or the subset row), not rely on `parent`. **Acceptance assertion:** all 81 `noteText` equal the fixture; the 5 truncations are documented, and no identity or histClass changes.

### F-03 (MAJOR, integration): the existing ledger verifier is not compatible with 77 new classified note rows

**Location:** `tools/verify-v5-n3-closeout-ledger.ps1:66-82` (`$pkgVerdictWant`, `$verdictTotalWant`), `:137-147` (`$adjudicationPin`), `:522-530` (`blocked-set`). **Repro:** apply the 81 proposed verdicts to the ledger and evaluate the blocked-set predicate; the final B8 classified case set becomes **75** unique caseIds, of which **71** are outside the current four-item allow-list (`tm-capacity-tiles`, `tm-decision-mismatch`, `bm-activate-deadwall-empty`, `bm-activate-deadwall-dora-only`). **Actual:** `blocked-set` would fail with 71 "unexpected classified blocked case" errors; `closure` would see 411/159/22 vs pinned 411/82/99; `adjudication-pin` would fail for B1/B8; `evidence-catalog` would fail for the new `EV-N3M-*` IDs unless registered. **Expected:** the caller updates the verifier pins together with the ledger, or exempts note-path rows from `blocked-set`. **Fix requirement:** caller-only, serial; do not edit the shared verifier in a receiver task. **Acceptance assertion:** after the caller's merge, the verifier exits 0 with the new vector and the updated registry/digests, and the 81 note rows remain `classified`/`pending` (never `bound`).

The exact post-merge package vector is:

| Package | bound | classified | pending | total |
|---|---:|---:|---:|---:|
| B1 | 18 | 5 | 1 | 24 |
| B8 | 108 | 80 | 3 | 191 |
| all other 12 packages | 285 | 74 | 18 | 377 |
| **total** | **411** | **159** | **22** | **592** |

(`all other 12` includes the untouched B6 11 pending and B9 7 pending; B2/B3/B4/B5/B7/B10/B11/E1/E2/E3 keep their accepted vectors.)

### F-04 (MINOR, residual risk): the C# binding manifest's note is not the fixture note

**Location:** `CheatGoldenBindingTests.cs:1663`; `CheatGoldenTests.cs:369`. **Repro:** replace a fixture note with any non-empty string; both checks still pass (proved by the all-notes run). **Actual:** the fixture check is `Length == 0` only; the manifest check is `IsNullOrWhiteSpace(spec.Note)` on an independently authored C# literal. **Expected under N3-META-01:** this is acceptable because notes are metadata, not behavior. **Fix requirement:** none now; if the caller ever makes notes behavior-bearing, add a value comparison to the fixture and a mutation probe. **Acceptance assertion:** no claim of note value binding; the all-notes run must keep passing while behavior-field negatives fail.

### Non-excludable behavior obligations (summary)

| # | Obligation | Status |
|---|---|---|
| OB-01 | V4 m1b hole backfill is not materialised; C# asserts the old slot free | open (pending row 1) |
| OB-02 | exact V4 **12-marked + 4-selected > 12** capacity state; the note's `8 marked + 4 > 12` is an original narrative typo (8+4=12; actual exporter input `groups=2, marked 16..27=12, selected 0/4/8/12=4`) | open (pending row 2) |
| OB-03 | V4 no-active-round / DECISION_MISMATCH input | open (pending row 3) |
| OB-04 | V4 `deadWallTileIdsInDeadWall=empty` filter state | open (pending row 4) |
| OB-05 | 77 classified notes remain text-uncovered by design; note-text drift is not detected | accepted metadata boundary, not full |
| OB-06 | actor notes `round-end`/`no-active-decision` rely on documented fault-injection states | anchored, but not business-reachable; caller should keep the caveat |
| OB-07 | V4 reason labels not reproduced (`ROUND_FINISHED`, `WindowMismatch`, `CostMustBeTen`, etc.) | typed refinements documented in the binding tests; not a note-text claim |
| OB-08 | 5 truncated `parent` cells cannot serve as full note provenance | open provenance fix (F-02) |

## 9. Status vocabulary

- **PASS:** 81/81 identity/census/rules verification; exporter-origin and C#-consumer analysis; subset schema/note-SHA verification (14/14); fresh builds #2/#3; Domain baseline 95/95; actor baseline 7/7; all-notes Domain 95/95; all-notes actor 7/7; `-class` filter control 0 tests.
- **FAIL:** none in the deliverable. The intentional behavior-negative runs FAIL as expected (`neg-b1` 3, `neg-b8` 2, `table-neg-b8` 2); they are evidence, not delivery failures.
- **BLOCKED:** none current. Build #1 was a transient environment precondition (missing restore assets); it was resolved by build #2. No old DLL existed at the runner path, so nothing stale was run.
- **NOT_RUN:** the historical 46/46 cheat matrix, table 28 matrix, round/scoring/physical/shuffle matrices; PG/WSS; Unity/IL2CPP; Gradle; Android/device; physical network; remote CI; solution-wide/full suite; post-merge ledger verifier (caller action). The 4 pending behaviors are NOT_RUN by design because no anchor exists.

**No N3/G1/G5 pass is claimed. V4 remains the only production architecture. `complete.flag` is not created.**

## 10. Deliverables and change scope

Authorized files added by this task (only these two):

1. `doc/v5-重构/V5-N3-NOTE-ADJUDICATION.md` — this report.
2. `doc/v5-重构/V5-N3-NOTE-ADJUDICATION.tsv` — 81 data rows + header, 29 columns, UTF-8/no BOM/LF, SHA-256 `4dee4f0452370d1f61db73128d2f690c13903e8d7a868b6a523b0417464a241b`.

The TSV's first 17 columns follow the shared ledger schema (`id,package,fixture,caseId,path,idx,kind,parent,histClass,verdict,csRead,assertion,fixCommit,evidence,reason,owner,revisit`); the 12 audit columns are `exporterCaseLine,exporterNoteLine,exporterFunc,noteSha256,noteText,describedBehavior,behaviorAnchor,anchorKind,rule,noteOrigin,parentNoteComplete,censusIdentity`. For all 81 rows `fixCommit='-'` because this task changes no code; `classified` rows are still uncovered and `pending` rows carry a dated revisit.

No production, test, fixture, catalog, manifest, previously accepted report, shared ledger/digest/verifier, Unity/Resources/Burst, or sibling/main-checkout file was modified. The local commit contains only the two files above.

## Appendix A - evidence registry used by the subset

The subset cites only the following IDs. The six `EV-N3M-*` source pins are new; the caller must add them to the shared verifier registry if it preserves the IDs verbatim. The accepted IDs already exist in the shared registry.

| EV-ID | Path | SHA-256 |
|---|---|---|
| `EV-N3M-EXP` | `engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt` | `402aa8ebf03b8e3f2aeb5712a94e7fc1c8508ee327981bc014f7a0a2a67902e4` |
| `EV-N3M-FIX` | `tests/v5-golden/cheat-v1.json` | `a196cbbd97ab8f61be8679b74c8f17df2713cb37aef0922e5f3c9f725b28ab4e` |
| `EV-N3M-DOM` | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | `3b869a5491fcb029640b95364a61a3739eb0d6b45b80b90273151d00e68f88e5` |
| `EV-N3M-BIND` | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenBindingTests.cs` | `fbccbe81277dd02b53f2cece4a2f17e0570eb0d87e66c47de9930c50a8a69ac0` |
| `EV-N3M-ADM` | `dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs` | `51de26646bb921af0bece51dff8a94393d7b6ab6b40d0618bb8ec1b492ffcca3` |
| `EV-N3M-TMK` | `dotnet/tests/Mahjong.Domain.Tests/TileMarkTests.cs` | `496bb948072d0df951a882eb0e714d3bb4f3efcef989b0f7a038758f0334c479` |
| `EV-CHEAT-DOC` | `doc/v5-重构/V5-N3-CHEAT-GOLDEN-BINDING.md` | `a9daf5f5ccd705f031af98b7aa7825452a263e97ba3df55df0a01e4e9c9d9abe` |
| `EV-CHEAT-ACC` | `doc/v5-重构/CODEX-V5-N3-CHEAT-BINDING-ACCEPTANCE.md` | `a0660e19dc6777ce41fdbd734bec9cf61e5a3cf5c5275658f4a97c025a6574e5` |
| `EV-TABLE-DOC` | `doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md` | `add5cb6dea7747fd7e781ee97e9d7414469baa0df10cd2834e676387c97f4d08` |
| `EV-R1-DOMAIN` | historical `artifacts/TEMP/codex-review-20261025/domain.log` (verified byte-identical copy in this task's TEMP) | `d3ce821392555fce4ea056049d30306c12a60882c1718a06100a43263ff81aad` |
| `EV-R1-TABLE` | historical `artifacts/TEMP/codex-review-20261025/table.log` (verified byte-identical copy in this task's TEMP) | `574549978cf1ba28fbab03713140f6240c8f22685ed4f299773ffbdb2fd7e2df` |

The subset's `evidence` column also uses `EV-R1-DOMAIN`/`EV-R1-TABLE` for Domain/actor anchors. If the caller prefers fresh-run IDs, the corresponding this-round logs are in §6.4 and can be registered instead; the verdicts do not depend on the label choice.

## Appendix B - 81/81 identity-level verdict table

Generated from the 81-row TSV. Every id is the frozen ledger id (pipes escaped for Markdown only; the TSV keeps the raw id). `anchor` is the value-level anchor for classified rows and `none` for pending rows. Full note text, full SHA-256, exporter line, reason, owner/revisit and evidence IDs are in the TSV.

| # | id | package | verdict | anchor | note SHA-256 (12) |
|---:|---|---|---|---|---|
| 1 | `N3-B1\|cheat-v1\|mv-reservation-delivery\|$.tileMovement[*].note\|1` | GAP:N3-B1 | **classified** | CheatGoldenBindingTests.cs:2534-2564 (2545-2563) | `d22afadb213b` |
| 2 | `N3-B1\|cheat-v1\|mv-reservation-delivery-then-beam\|$.tileMovement[*].note\|1` | GAP:N3-B1 | **classified** | CheatGoldenTests.cs:963-1124 (1003-1123) | `e6d9835881a0` |
| 3 | `N3-B1\|cheat-v1\|mv-rinshan-and-drawn-swap\|$.tileMovement[*].note\|1` | GAP:N3-B1 | **classified** | CheatGoldenBindingTests.cs:2567-2612 (2583-2611) | `1892d2e62fbf` |
| 4 | `N3-B1\|cheat-v1\|mv-swap-drawn-with-wall-backfill\|$.tileMovement[*].note\|1` | GAP:N3-B1 | **pending** | none | `475d2d8b01b3` |
| 5 | `N3-B1\|cheat-v1\|mv-swap-hand-with-dead-wall\|$.tileMovement[*].note\|1` | GAP:N3-B1 | **classified** | CheatGoldenBindingTests.cs:2615-2675 (2634-2674) | `082f9460099b` |
| 6 | `N3-B1\|cheat-v1\|mv-swap-hand-with-wall-direct\|$.tileMovement[*].note\|1` | GAP:N3-B1 | **classified** | CheatGoldenBindingTests.cs:2461-2487 (2477-2485) | `b15452122595` |
| 7 | `N3-B8\|cheat-v1\|bm-activate-allowed\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2738-2758 | `06370d8b778d` |
| 8 | `N3-B8\|cheat-v1\|bm-activate-already-used\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2713-2716 | `c2e1d8057bac` |
| 9 | `N3-B8\|cheat-v1\|bm-activate-beam-pending\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2704-2708 | `9b7c992471f1` |
| 10 | `N3-B8\|cheat-v1\|bm-activate-deadwall-allowed\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2726-2730,2759-2772 | `3ae98ab35fc5` |
| 11 | `N3-B8\|cheat-v1\|bm-activate-deadwall-dora-only\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2985-2996 (DoesNotContain DeadWall(4)) and :2770 | `12fa10b39bbf` |
| 12 | `N3-B8\|cheat-v1\|bm-activate-deadwall-empty\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **pending** | none | `693c5650f104` |
| 13 | `N3-B8\|cheat-v1\|bm-activate-dealer-extra\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2692-2695 | `ffa56b15d720` |
| 14 | `N3-B8\|cheat-v1\|bm-activate-disabled\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2717-2721 | `521d3f3767ac` |
| 15 | `N3-B8\|cheat-v1\|bm-activate-insufficient\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2722-2725 | `bab92217db56` |
| 16 | `N3-B8\|cheat-v1\|bm-activate-no-candidates\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2688-2691,2773-2776 | `611c64632373` |
| 17 | `N3-B8\|cheat-v1\|bm-activate-no-drawn\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2700-2703 | `bd9d2c425b4f` |
| 18 | `N3-B8\|cheat-v1\|bm-activate-riichi\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2696-2699 | `3495740f8282` |
| 19 | `N3-B8\|cheat-v1\|bm-activate-sleeve-pending\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2709-2712 | `99b9a1498bca` |
| 20 | `N3-B8\|cheat-v1\|bm-choose-deadwall-allowed\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2919-2928 | `574317450ed7` |
| 21 | `N3-B8\|cheat-v1\|bm-choose-deadwall-dora-stale\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2887-2895,2912-2918,2985-2996 | `9d9e1a653fdd` |
| 22 | `N3-B8\|cheat-v1\|bm-choose-invalid-foreign-hand\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2804-2807,2869-2878 | `f844c001a593` |
| 23 | `N3-B8\|cheat-v1\|bm-choose-invalid-slot\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2800-2803,2869-2878 | `a0a3e465f916` |
| 24 | `N3-B8\|cheat-v1\|bm-choose-keep\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2851-2861 | `8cb7ebc1bea0` |
| 25 | `N3-B8\|cheat-v1\|bm-choose-replacement-allowed\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2929-2938 | `4571db47b093` |
| 26 | `N3-B8\|cheat-v1\|bm-choose-stale-drawn\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2816-2819,2862-2868 | `07359fedeb6f` |
| 27 | `N3-B8\|cheat-v1\|bm-choose-stale-turn\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2812-2815,2862-2868 | `f4db841d149e` |
| 28 | `N3-B8\|cheat-v1\|bm-choose-swap\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2837-2850 | `3d4097656bd8` |
| 29 | `N3-B8\|cheat-v1\|bm-keep-original-args-forbidden-at-request\|$.beamSwapHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2808-2811,2871-2877 (fixture requestException compared) | `5d1894f5c5eb` |
| 30 | `N3-B8\|cheat-v1\|sb-activate-allowed\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2116-2136 (goldenInputs all fields) | `b158dcc901a8` |
| 31 | `N3-B8\|cheat-v1\|sb-activate-already-used\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2042-2045,2156-2159 | `c2e1d8057bac` |
| 32 | `N3-B8\|cheat-v1\|sb-activate-beam-pending\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2046-2050,2156-2159 | `99b9a1498bca` |
| 33 | `N3-B8\|cheat-v1\|sb-activate-dealer-extra\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2038-2041,2156-2159 | `ffa56b15d720` |
| 34 | `N3-B8\|cheat-v1\|sb-activate-disabled\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2086-2090,2156-2159 | `521d3f3767ac` |
| 35 | `N3-B8\|cheat-v1\|sb-activate-insufficient\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2091-2094,2156-2159 | `bab92217db56` |
| 36 | `N3-B8\|cheat-v1\|sb-activate-no-drawn-tile\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2070-2073,2156-2159 | `bd9d2c425b4f` |
| 37 | `N3-B8\|cheat-v1\|sb-activate-not-your-turn\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2074-2077,2156-2159 | `a1130d6eb1bf` |
| 38 | `N3-B8\|cheat-v1\|sb-activate-response-window\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2082-2085,2156-2159 | `b6c50eceea89` |
| 39 | `N3-B8\|cheat-v1\|sb-activate-rinshan-allowed\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2095-2099,2137-2155 (goldenInputs all fields) | `9f4801c99550` |
| 40 | `N3-B8\|cheat-v1\|sb-activate-rinshan-empty\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2100-2104,2156-2159 | `153c86e31c41` |
| 41 | `N3-B8\|cheat-v1\|sb-activate-sleeve-pending\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2051-2054,2156-2159 | `9b7c992471f1` |
| 42 | `N3-B8\|cheat-v1\|sb-activate-wall-empty\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2055-2058,2156-2159 | `9dcfdc46d413` |
| 43 | `N3-B8\|cheat-v1\|sb-activate-wall-empty-drift\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2059-2069,2109-2114 (Frozen(32,false)) | `c2f181a19701` |
| 44 | `N3-B8\|cheat-v1\|sb-activate-wrong-phase\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2078-2081,2156-2159 | `f5d5b5a25216` |
| 45 | `N3-B8\|cheat-v1\|sb-choose-first\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2161-2214 (2168-2213) | `136ffc4f2caf` |
| 46 | `N3-B8\|cheat-v1\|sb-choose-no-offer\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2313-2319 | `b976efca36bf` |
| 47 | `N3-B8\|cheat-v1\|sb-choose-offer-mismatch\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2303-2312 | `795d1b4ac698` |
| 48 | `N3-B8\|cheat-v1\|sb-choose-rinshan\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2215-2250 | `38e04198155a` |
| 49 | `N3-B8\|cheat-v1\|sb-choose-second\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2161-2214 (2168-2213) | `136ffc4f2caf` |
| 50 | `N3-B8\|cheat-v1\|sb-choose-stale-drawn\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2266-2280 | `07359fedeb6f` |
| 51 | `N3-B8\|cheat-v1\|sb-choose-stale-index\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2281-2302 | `9295f87e872e` |
| 52 | `N3-B8\|cheat-v1\|sb-choose-stale-turn\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2251-2265 | `f4db841d149e` |
| 53 | `N3-B8\|cheat-v1\|sbo-activate-allowed\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2349-2368 (goldenInputs fields) | `cae089468b16` |
| 54 | `N3-B8\|cheat-v1\|sbo-activate-no-window\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2327-2330 | `983480f87dcc` |
| 55 | `N3-B8\|cheat-v1\|sbo-activate-own-turn\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2337-2340 | `681e63ff8ef8` |
| 56 | `N3-B8\|cheat-v1\|sbo-activate-reserved\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2331-2336 | `2562d00032ee` |
| 57 | `N3-B8\|cheat-v1\|sbo-activate-wall-empty\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2341-2344 | `02ccc297ac9b` |
| 58 | `N3-B8\|cheat-v1\|sbo-choose-allowed\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2382-2395,2411-2424 | `16f7320ac4e5` |
| 59 | `N3-B8\|cheat-v1\|sbo-choose-expired\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2396-2402,2425-2434 | `0808d30aadc5` |
| 60 | `N3-B8\|cheat-v1\|sbo-choose-stale-first\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2436-2448 | `214d179f8c28` |
| 61 | `N3-B8\|cheat-v1\|sbo-choose-stale-second\|$.sleeveBloomHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:2436-2448 | `6949c9ce592e` |
| 62 | `N3-B8\|cheat-v1\|tm-allowed\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1888-1889,1950-1968 | `bb12ca395aff` |
| 63 | `N3-B8\|cheat-v1\|tm-already-marked\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1914-1919,1972 | `749d4836eed1` |
| 64 | `N3-B8\|cheat-v1\|tm-beam-pending\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1938-1941,1972 | `02c169997677` |
| 65 | `N3-B8\|cheat-v1\|tm-capacity-groups\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1920-1928,1972 | `5b9e56a67b91` |
| 66 | `N3-B8\|cheat-v1\|tm-capacity-tiles\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **pending** | none | `ed3ed6736586` |
| 67 | `N3-B8\|cheat-v1\|tm-deadline-expired\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | N3TableCheatGoldenAdmissionTests.cs:400-466 (exact deadline at :417, DeadlineExpired at :425) | `0808d30aadc5` |
| 68 | `N3-B8\|cheat-v1\|tm-decision-mismatch\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **pending** | none | `92c0327ef8fe` |
| 69 | `N3-B8\|cheat-v1\|tm-insufficient-funds\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1929-1931,1972 | `ede6f7ed8ab9` |
| 70 | `N3-B8\|cheat-v1\|tm-invalid-selection-duplicate\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1908-1910,1972 | `a0b8ee84283d` |
| 71 | `N3-B8\|cheat-v1\|tm-invalid-selection-three\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1905-1907,1972 | `18e09841a122` |
| 72 | `N3-B8\|cheat-v1\|tm-no-active-decision\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | N3TableCheatGoldenAdmissionTests.cs:346-397 (DecisionMismatch, "not open in the clock") | `31833f029fc0` |
| 73 | `N3-B8\|cheat-v1\|tm-not-your-turn\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1890-1892,1972 | `d366e501ea6e` |
| 74 | `N3-B8\|cheat-v1\|tm-offturn-sleeve-pending\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1935-1937,1972 | `bf4817baafd8` |
| 75 | `N3-B8\|cheat-v1\|tm-response-window\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1899-1901,1972 | `13a4d60f9a2e` |
| 76 | `N3-B8\|cheat-v1\|tm-riichi-locked\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1902-1904,1972 | `476a259bc6a7` |
| 77 | `N3-B8\|cheat-v1\|tm-sleeve-pending\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1932-1934,1972 | `822754faa985` |
| 78 | `N3-B8\|cheat-v1\|tm-tile-not-owned\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1911-1913,1972 | `bcc36792344f` |
| 79 | `N3-B8\|cheat-v1\|tm-wrong-phase-draw\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1896-1898,1972 | `cf7de29f1675` |
| 80 | `N3-B8\|cheat-v1\|tm-wrong-phase-preparation\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | CheatGoldenBindingTests.cs:1893-1895,1972 | `30b8e5f17f9a` |
| 81 | `N3-B8\|cheat-v1\|tm-wrong-phase-round-end\|$.tileMarkHandler[*].note\|1` | GAP:N3-B8 | **classified** | N3TableCheatGoldenAdmissionTests.cs:267-343 (ROUND_FINISHED at :320) | `daac50dd36ab` |

## Appendix C - pending-row details (copy of the TSV fields)

| id | note | noteSha256 | csRead | assertion |
|---|---|---|---|---|
| `N3-B1\|cheat-v1\|mv-swap-drawn-with-wall-backfill\|$.tileMovement[*].note\|1` | after drawNormal the drawn tile keeps its old wall slot; swapping it backfills that slot with the displaced wall tile | `475d2d8b01b35d71007625463811d377d30b94b17d4b5fc5aafba8d28e7b5144` | `-` | old slot is asserted FREE at `:2526-2527`; C# does not materialise V4 backfill |
| `N3-B8\|cheat-v1\|bm-activate-deadwall-empty\|$.beamSwapHandler[*].note\|1` | everything consumed | `693c5650f1042740748570a4d9b635fba76f4bfbf07fca96d6146c2c5e85a946` | `-` | unrepresentable `deadWallTileIdsInDeadWall=empty`; no executing test |
| `N3-B8\|cheat-v1\|tm-capacity-tiles\|$.tileMarkHandler[*].note\|1` | 8 marked + 4 > 12 | `ed3ed67365865c2caefb557ff03758baac2cede53fd89c684bf57057cddd466a` | `-` | original note is a narrative typo (8+4=12); actual V4 input is 12 marked + 4 selected; unrepresentable groups=2/12-marked state; no executing test; no 8+4>12 requirement |
| `N3-B8\|cheat-v1\|tm-decision-mismatch\|$.tileMarkHandler[*].note\|1` | no active round | `92c0327ef8fef41b33d320c5df48b935d052d22baf0a62360d93a03dd8e3606c` | `-` | no null-round port; supplementary stale-round test is explicitly not this case |
