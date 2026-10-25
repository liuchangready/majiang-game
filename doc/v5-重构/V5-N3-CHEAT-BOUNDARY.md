# V5 N3 residual cheat-boundary evidence (B1 + B8 ×3)

- Date: 2026-10-26
- Worktree: `D:/majiang-wt/n3-cheat-boundary-20261026`
- Branch: `wt/v5-n3-cheat-boundary-20261026`
- Baseline HEAD: `85134e9ce4fd67556f6d2f355249c14f4d3b42d4`
- PR19 head at task start: `85134e9`
- Status: **evidence input only** — the shared ledger, verifier, fixtures, catalog, manifest and accepted reports are unchanged. This report does not approve gate 2, G1 or G5 and creates no `complete.flag`.

## 0. Result in one table

The four rows are the explicit residual behavior obligations from shared ledger §14.1, not a re-open of the 81-note metadata adjudication.

| Frozen instance | Current ledger | Test evidence | Exact V4 input representable? | Proposal |
|---|---|---|---|---|
| B1 `mv-swap-drawn-with-wall-backfill` note | pending | Domain PASS | No: V4 backfills the old slot as an alias; C# leaves the slot free and keeps the same physical entity in the hand | **pending**; classified only if caller accepts the V4-only alias boundary |
| B8 `tm-capacity-tiles` note | pending | Domain PASS | No: V4 groups=2 / 12 marked; C# groups are exactly four and at most three | **pending**; classified only if caller accepts the unrepresentable V4-only boundary |
| B8 `tm-decision-mismatch` note | pending | Domain + TableRuntime PASS | No: V4 authority has no active round; C# actor always owns a concrete round and typed commands require a nonblank round id | **pending**; classified only if caller accepts the no-active-round boundary |
| B8 `bm-activate-deadwall-empty` note | pending | Domain PASS | No: V4 passes an empty `deadWallTileIdsInDeadWall` filter while 14 dead-wall entities still exist; C# derives candidates from canonical positions and has no filter input | **pending**; classified only if caller accepts the non-canonical filter-state boundary |

No row is proposed `bound`: no existing production API reproduces the exact V4 input. No row is silently excluded. `pending` is intentional; the caller owns acceptance of a representation difference and the serial ledger/digest update.

## 1. Scope, ownership and frozen boundaries

Verified before any modification:

```text
git rev-parse HEAD
85134e9ce4fd67556f6d2f355249c14f4d3b42d4

git rev-parse --abbrev-ref HEAD
wt/v5-n3-cheat-boundary-20261026

git status --porcelain=v1
(empty)
```

This worktree is separate from the main checkout `D:/majiang`, the planning tree and every sibling worktree. No sibling in-flight artifact was read or depended on. No main-checkout file, planning-tree file, sibling file, user-uncommitted file, Unity environment/Resources/Burst path, machine-level trust/environment, deployment, PR, CI job or remote branch was touched.

Changed paths, exactly four, all new:

```text
doc/v5-重构/V5-N3-CHEAT-BOUNDARY.md
doc/v5-重构/V5-N3-CHEAT-BOUNDARY.tsv
dotnet/tests/Mahjong.Domain.Tests/N3CheatBoundaryTests.cs
dotnet/tests/Mahjong.TableRuntime.Tests/N3CheatBoundaryAdmissionTests.cs
```

Production code, existing tests, fixtures, `tests/v5-golden/cheat-v1.json`, catalog, manifest, shared ledger/TSV, unified verifier, accepted reports and the V4 exporter are byte-unchanged. No production bug was patched in this round. No reflection is used to claim production reachability; the tests call only existing public/domain APIs.

## 2. Frozen identity, note values and source pins

The subset TSV preserves `id`, `package`, `fixture`, `caseId`, `path`, `idx`, `kind`, `parent`, `histClass`, `noteText` and `noteSha256`. A mechanical join against `V5-G1-N3-CLOSEOUT-LEDGER.tsv` passes for all four identities, and the note SHA is recomputed from the preserved text.

| Case | histClass | noteText | noteSha256 |
|---|---|---|---|
| `mv-swap-drawn-with-wall-backfill` | UNBOUND | `after drawNormal the drawn tile keeps its old wall slot; swapping it backfills that slot with the displaced wall tile` | `475d2d8b01b35d71007625463811d377d30b94b17d4b5fc5aafba8d28e7b5144` |
| `tm-capacity-tiles` | UNBOUND_HC | `8 marked + 4 > 12` | `ed3ed67365865c2caefb557ff03758baac2cede53fd89c684bf57057cddd466a` |
| `tm-decision-mismatch` | UNBOUND_HC | `no active round` | `92c0327ef8fef41b33d320c5df48b935d052d22baf0a62360d93a03dd8e3606c` |
| `bm-activate-deadwall-empty` | UNBOUND_HC | `everything consumed` | `693c5650f1042740748570a4d9b635fba76f4bfbf07fca96d6146c2c5e85a946` |

The `8 marked + 4 > 12` text is an original exporter narrative typo: `8 + 4 = 12`. The text and SHA are retained verbatim as provenance and are not used as acceptance arithmetic. The real V4 input is `12 + 4 > 12`.

Relevant frozen source anchors:

- B1 exporter: `engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt:1091-1123`.
- Capacity exporter: `V5CheatGoldenExporterTest.kt:418`.
- Decision exporter: `V5CheatGoldenExporterTest.kt:425`.
- Dead-wall filter exporter: `V5CheatGoldenExporterTest.kt:932-935`.
- Fixture: `tests/v5-golden/cheat-v1.json` (`tileMovement`, `tileMarkHandler`, `beamSwapHandler`).

`N3CheatBoundaryTests.FrozenFixtureValuesAnchorTheFourCases` reads that untouched fixture (honoring `V5_CHEAT_GOLDEN_PATH` when set), asserts all four note/outcome/reason values, the B1 `drawnTileId=33`, `displacedTileId=29`, `backfilledTileId=29`, `resolvedIndex=1`, `backfilledIndex=0` and pointer fields, and recomputes the B1 note SHA from the fixture text. This prevents the hardcoded test inputs from drifting away from the frozen record.

## 3. B1 `mv-swap-drawn-with-wall-backfill`

### 3.1 V4 exporter input and materialisation

The exporter creates twelve closed seat-0 tiles, a wall prefix `[33, 29, 25]`, and a real `drawNormal` from V4 slot 0:

```text
wallPointerBeforeDraw = 0
drawnTileId           = 33   (old slot 0)
wallPointerAfterDraw  = 1
swap drawn 33 with wall slot 1
displacedTileId       = 29   (wall slot 1)
wall[0]               = 29   (V4 backfill)
wall[1]               = 33
players[0].drawnTile   = 29
resolvedIndex          = 1
backfilledIndex        = 0
wallPointerAfterSwap   = 1
factsAfter             = [33 @ wall index 1; 29 @ hand seat 0]
```

The V4 code sets both `s.wall[0] = wall8` and `s.players[0].drawnTile = wall8`, where `wall8` is tile 29 (`V5CheatGoldenExporterTest.kt:1107-1109`). The fixture `factsAfter` still lists only one 29 and places it in the hand. The backfilled slot is therefore a **view alias to the displaced entity**, not a second physical tile.

### 3.2 C# trace on the same V4 values

`N3CheatBoundaryTests.Backfill_V4AliasAndCanonicalPositionsAreBidirectionallyEquivalent` builds a canonical 136-placement map with C# wall offset 53 (`Wall(53)=33`, `Wall(54)=29`, `Wall(55)=25`) and executes the same sequence with existing APIs.

| Point | V4 | C# canonical map | Equal physical facts? |
|---|---|---|---|
| Before draw | pointer 0; slots 0/1/2 = 33/29/25 | pointer 53; Wall(53/54/55) = 33/29/25; 69 wall entities | yes |
| After draw | pointer 1; drawn 33; old slot 0 retained as the hole | pointer 54; drawn 33 in `Hand(0,12)`; Wall(53) free; 68 wall entities | yes: one 33, now in hand |
| After swap | 33 @ wall 1; 29 @ hand/drawn; Wall[0]=29 alias; pointer 1 | 33 @ Wall(54); 29 @ Hand(0,12) and `DrawnTile=29`; Wall(53) free; 29 occurs once; pointer 54; wall end 122 | yes: physical entity positions match; only the old-slot view differs |
| Next draw | V4 pointer 1 scans the next wall tile 33 | C# skips free Wall(53), draws 33 from Wall(54), pointer 55; tail 25 stays at Wall(55) | yes |

Assertions actually executed:

```text
initial: 136 placements, 136 distinct positions, 69 wall entities, pointer 53
draw:    DrawnTile=33/Wall, pointer 54, Wall(53) free, 136 distinct
swap:    ResultTile=29, DrawnTile=29, 33@Wall(54), 29@Hand(0,12),
         25@Wall(55), pointer 54, WallEnd=122, 68 wall entities,
         29 occurs exactly once, 136 distinct
next:    DrawnTile=33, pointer 55, 25@Wall(55), 136 distinct
```

The `29`-at-Wall(53) negative is causal: a real V4-style backfill would return 29 there; the canonical map throws `InvalidOperationException("No tile at position ...")`, proving no duplicate entity was fabricated.

The mutation negative changes only the resolved slot from 1 to 2. It yields displaced tile 25 at `Wall(55)`, not 29 at `Wall(54)`, so the fixture oracle rejects it. The old slot remains free.

### 3.3 Disposition

- No real rule deviation was found for physical identity, count, drawn identity or 136 conservation.
- The V4 backfill slot is an eager representation alias; C# preserves the same entity in the hand and re-enters the free hole lazily on the next draw.
- Because the exact backfill slot is not materialised in C#, the row stays **pending**. If the caller explicitly accepts the alias boundary it can become `classified`; otherwise the future production change must add an explicit backfill/view surface without duplicating the tile or relaxing conservation.

## 4. B8 `tm-capacity-tiles`

### 4.1 Exact V4 input

`V5CheatGoldenExporterTest.kt:418`:

```text
input(groups = 2, marked = (16..27).toSet())
default selected tileIds = handIds[0..3] = 0, 4, 8, 12
outcome = Rejected
reason  = TILE_MARK_CAPACITY_EXCEEDED
```

Thus the real state is **12 marked + 4 selected = 16 > 12**, with V4 `groups=2`. The note's `8 marked + 4 > 12` is false arithmetic (`8 + 4 = 12`) and is not asserted as a rejection.

### 4.2 Exact state is not representable in C#

A `TileMarkGroup` holds exactly four tiles (`TileMark.cs:69-72`). Two legal groups therefore carry eight marked tiles, never twelve. Three groups carry twelve; adding the four selected tiles would be a fourth group, and `AddGroup` rejects at `seatGroups >= 3` (`TileMark.cs:472-475`) before the twelve-tile limit (`:477-480`).

Test evidence:

- `new TileMarkGroup(..., 12 tiles, ...)` throws `ArgumentException("A tile mark group holds exactly four tiles.")` with `ParamName=tileIds`.
- Two groups produce 8 marked tiles; three groups produce 12.
- A direct fourth group fails with `TileMarkError.GroupLimitExceeded`.

So the exact V4 `groups=2` / 12-marked state cannot be constructed, and the selected four cannot be attached as a fourth group.

### 4.3 Reachable 12+4 capacity rejection, zero fee and zero state change

`N3CheatBoundaryTests.TileMark_TwelvePlusFourIsRejectedWithZeroChargeAndNoStateChange` starts from three legal groups (12 marked) and calls `CheatSubmission.ApplyTileMark` with the exact selected set `0/4/8/12`.

Actual:

```text
result.Success = false
result.Error   = CheatErrorCode.MarkCapacityExceeded
result.State   = null
result.Map     = null
result.Events  = empty
source balance = 40, ledger entries = 0
source version/fingerprint unchanged
source groups = 3, marked tiles = 12
```

The typed reason matches V4 `TILE_MARK_CAPACITY_EXCEEDED`, but the internal cause is `GroupLimitExceeded`, not `TileLimitExceeded`. This is disclosed rather than presented as the exact V4 state.

`N3CheatBoundaryTests.TileMark_EightPlusFourIsAllowedAndChargesOnce` is the arithmetic control: 8 marked + the same 4 selected is allowed, charges exactly 10 (balance 30), leaves one ledger entry, three groups and 12 marked tiles. Any old `8 + 4 > 12` rejection assertion fails this test. The positive control uses the same map and selected tiles, so the difference is the marked capacity, not a different fixture.

### 4.4 Disposition

- Exact V4 state: **not representable**; **pending**.
- Reachable C# analogue: 12+4 rejects with the right typed reason and zero fee/state change.
- If the caller accepts the exactly-four/three-group representability boundary, classify the row; do not relax the production group invariant and do not claim the reachable analogue is the same input.

## 5. B8 `tm-decision-mismatch`

### 5.1 Exact V4 input and boundary

`V5CheatGoldenExporterTest.kt:425`:

```text
input(roundIdGiven = null)   // authority has no active round
outcome = Rejected
reason  = DECISION_MISMATCH
```

### 5.2 C# construction rejection

There is no safe null-round entry:

- `CheatTurnContext` rejects null/blank `roundId` with `ArgumentException` and `ParamName=roundId` (`CheatSubmission.cs:43-46`; test `N3CheatBoundaryTests.cs:350-379`).
- `RoundCheatCommand` rejects null/blank `roundId` with `ArgumentException` and `ParamName=roundId` (`RoundCheatTransition.cs:276-279`; test `N3CheatBoundaryAdmissionTests.cs:24-62`).
- `TableActor` requires a non-null `RoundStateMachine` and throws `ArgumentNullException(initialRound)` at construction (`TableActor.cs:134`); the test passes null ports deliberately because the round guard fires before any port is touched.

No public API accepts a null round id. The exact V4 input is therefore contract-unrepresentable, not merely untested.

### 5.3 Reachable contrast: no open decision is not the null-round case

`N3CheatBoundaryAdmissionTests.NoOpenDecisionRejectionIsTypedAndSideEffectFree` builds a valid actor with a concrete round, cheat economy and registered mark round, but no pending decision. A tile-mark command with a non-open decision id is rejected:

```text
Status          = Rejected
ReasonCode      = DecisionMismatch
Detail          = "The command refers to a decision that is not open for this seat."
Events          = empty
CommandVersion  = null
snapshot        = StateVersion, LastEventSeq, StateFingerprint unchanged; RecentEvents empty
```

This is explicitly **not** the V4 source case. It exercises the reachable no-open-decision branch and proves the detail differs from a round mismatch. The existing supplementary stale-round test yields `RoundMismatch` and is also not this source case. The actor no-active-decision and round-ended paths therefore do not become null-round evidence.

### 5.4 Disposition

- Exact V4 null-round input: **not representable**; **pending**.
- Construction rejection is the value evidence; no fake actor state was created.
- If the caller accepts the no-active-round boundary, classify; otherwise a safe null-round port and a value-level assertion are required. Do not ask sibling N2 to change the actor for this task.

## 6. B8 `bm-activate-deadwall-empty`

### 6.1 Exact V4 input

`V5CheatGoldenExporterTest.kt:932-935`:

```text
activation(
  drawKind       = RINSHAN,
  rinshanPointer = 4,
  deadWallInWall = emptySet())
outcome = Rejected
reason  = BEAM_SWAP_NO_CANDIDATES
note    = everything consumed
```

The 14 dead-wall entities still exist elsewhere; only the handler's filter set is empty. This is not a physical empty dead wall.

### 6.2 C# has no filter surface and cannot form the same map

`CheatSubmission.ActivateBeamSwap` takes `CheatState`, `TilePositionMap`, `CheatTurnContext`, economy and config; it has no `deadWallTileIdsInDeadWall` parameter. Candidates are derived from canonical positions (`CheatSubmission.cs:879-923`). A canonical map requires exactly 14 effective dead-wall/replacement entities (`TilePositionMap.cs:1587-1593`).

At `RinshanPointer=4`, the canonical map has:

```text
10 undrawn back-half dead-wall entities + 4 replacement entities = 14 effective
```

Test evidence:

- `BeamSwap_PointerFourCanonicalMapHasCandidatesAndChargesOnce`: 14 effective entities; activation succeeds; 14 candidates (4 replacement + 10 dead-wall); exactly one 10-point charge; no map movement.
- `BeamSwap_FiveDoraIndicatorsStillLeaveNineCanonicalCandidates`: even with all five dora indicators, 10 back-half minus 5 dora + 4 replacements = 9 candidates; activation still succeeds. The existing dora-only case is not reused as this source input.
- `BeamSwap_ThirteenEffectiveDeadWallTilesAreRejectedAtMapConstruction`: a 4-replacement + 9-dead-wall map throws `ConservationException("Effective dead wall must contain 14 tiles ...")`; a `13`-entity physical map is not constructible.

### 6.3 Reachable empty-candidate control (different input, explicitly labelled)

`BeamSwap_WallExhaustedEmptyCandidatesHasZeroCostAndNoOffer` builds a normal-draw map with `WallPointer == WallEndExclusive == 122` and zero live-wall slots. `ActivateBeamSwap` rejects before charging:

```text
Error   = CheatErrorCode.NoWallCandidate
Message = "No live-wall slot remains in the beam window."
State   = null; Map = null; Events = empty
source  = 40 points, no ledger entries, no offer, version/fingerprint unchanged
```

This proves the reachable empty-candidate path is zero-fee/zero-offer, but it is **not** the deadWallTileIdsInDeadWall source case. It is not used to claim alignment.

### 6.4 Disposition

- Exact V4 filter state: **not representable**; **pending**.
- Canonical pointer-4 candidates cannot be empty; physical 13/14-entity shortcuts are not accepted as the same input.
- If the caller accepts the non-canonical filter-state boundary, classify; otherwise a handler-level candidate-suppression surface with an empty-candidate value assertion is required.

## 7. Test matrix, commands and evidence

All commands used `D:/Unity/dotnet/dotnet.exe`. Fresh builds were `--no-incremental --no-restore` after a one-time restore. Process `TEMP`/`TMP` were local to `artifacts/TEMP/n3-cheat-boundary-20261026` on the D drive. Tests ran only after successful builds, through the direct runner (not `dotnet test --filter`).

```powershell
$env:TEMP='D:/majiang-wt/n3-cheat-boundary-20261026/artifacts/TEMP/n3-cheat-boundary-20261026'
$env:TMP=$env:TEMP
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj --no-incremental --no-restore
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-incremental --no-restore
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*N3CheatBoundaryTests' -result-xml <E>/final-domain-n3.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -class '*N3CheatBoundaryAdmissionTests' -result-xml <E>/final-table-n3.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -method '*Backfill_V4AliasAndCanonicalPositionsAreBidirectionallyEquivalent' -result-xml <E>/final-domain-method.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -method '*NullRoundIsRejectedAtCommandConstructionAndActorConstruction' -result-xml <E>/final-table-method.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*NoSuchN3BoundaryTests' -result-xml <E>/final-domain-control.xml
```

`E = D:/majiang-wt/n3-cheat-boundary-20261026/artifacts/TEMP/n3-cheat-boundary-20261026`.

| Selection | Total | Passed | Failed | Skipped | Not Run | Errors | Native exit |
|---|---:|---:|---:|---:|---:|---:|---:|
| Domain fresh build | — | — | — | — | — | 0 warnings / 0 errors | 0 |
| TableRuntime fresh build | — | — | — | — | — | 0 warnings / 0 errors | 0 |
| Domain `-class '*N3CheatBoundaryTests'` | 11 | 11 | 0 | 0 | 0 | 0 | 0 |
| Actor `-class '*N3CheatBoundaryAdmissionTests'` | 2 | 2 | 0 | 0 | 0 | 0 | 0 |
| Domain `-method` backfill | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| Actor `-method` null round | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| Nonexistent-class selector control | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

XML was read to confirm the exact class/method selection. The 11 Domain tests are all in `Mahjong.Domain.Tests.N3CheatBoundaryTests`; the 2 actor tests are all in `Mahjong.TableRuntime.Tests.N3CheatBoundaryAdmissionTests`. No `Skip`, empty test, old DLL or `Total=0` result is used as coverage.

### 7.1 Final log/XML SHA-256

| Evidence under `E` | SHA-256 |
|---|---|
| `final-domain-build.log` | `0D7002CB82A590064EBB03C11FE8C845902C407BE2FC2EB2F57E279C34558D2B` |
| `final-table-build.log` | `DFB7274564D893A0E607509CF1FD30972FAC99A5646F7796CD8865FF9A3B5BA7` |
| `final-domain-n3.log` | `B8D06F9FED1A9A06B4219EE33C86B5F24D0813C9685FF2A39959061F7782C8AE` |
| `final-table-n3.log` | `361E1D6912FDB522AC62BB786F43A5DF73D76137A90538F3D5D80988F45CE261` |
| `final-domain-n3.xml` | `EBE2620186936EEB2104892304C876FD21904655A59E637FAE49DAB6C71B8700` |
| `final-table-n3.xml` | `CD3B31B3E3D4AFD9C4FE10492AED340126E6EB5302C2AEAADC3BDC03479EB6BD` |
| `final-domain-method.log` | `1FAA736625F72A486D92077D05CAC5E78EA9DAED91C78DACE404C33F699D2C25` |
| `final-table-method.log` | `310C4958A64D654982161C7EF67319CD5DEA2B94D11A6D53B6B26C1870203BD5` |
| `final-domain-method.xml` | `F7A57572C7E03F580FBEE8778D84050A483CF28A60EA7E420BC96E791EDC253D` |
| `final-table-method.xml` | `CB2453D1BECFCF19B8E97E88493E403B3867BBFB879F9A750C9E05356D8B3BC0` |
| `final-domain-control.log` | `C49BDCCF7FAB807E6F1C9CE245E5EA6F1404EF470EDC5ED4F8F8411BE32FBC5B` |
| `final-domain-control.xml` | `529C50688F780D7F54B2409DF55ADAE6D8F082431F05B3F7AC4D22A4E0500BCA` |

`artifacts/` is ignored by `.git/info/exclude` in this worktree; these evidence files are not part of the commit.

## 8. PASS / FAIL / BLOCKED / NOT_RUN

| Status | Items |
|---|---|
| **PASS** | Both fresh builds (0 warnings, 0 errors); Domain boundary class 11/11; actor boundary class 2/2; method selectors 1/1 each; nonexistent-class control 0; all four rows have value-level reachable assertions and a causal negative or construction rejection. |
| **FAIL** | None. No test failed in the final matrix; no product defect was found in the tested reachable APIs. |
| **BLOCKED** | Exact V4 inputs for the four rows are not representable in the accepted C# model: V4 backfill alias, V4 groups=2/12-marked state, V4 no-active-round authority, V4 empty dead-wall filter. Each is recorded as construction/contract evidence, not faked. Ledger disposition awaits caller acceptance. |
| **NOT_RUN** | Full solution suite, PostgreSQL, WSS, Unity/IL2CPP, Gradle, device/emulator, physical-network and remote CI. None is required for this boundary evidence; none is claimed. |

This round does **not** approve gate 2, G1 or G5, does not claim zero pending, does not create a `complete.flag`, and does not push, open a PR, create a component MR or trigger CI. V4 remains the sole production architecture.

## 9. Findings and residual obligations

No new product behavior defect was found in the exercised paths. The material findings are representational:

- **R-01 (MAJOR, coverage; B1).** V4's hole backfill is an eager alias; C# uses a lazy free hole. Physical positions, drawn identity, counts and 136 conservation are equivalent. The exact old-slot value is not materialised. Caller must accept the alias boundary or require an explicit production view surface.
- **R-02 (MAJOR, coverage; capacity).** The exact V4 `groups=2` / 12-marked state is unrepresentable under the exactly-four and max-three-group invariants. A reachable 12+4 analogue rejects with `MarkCapacityExceeded` and zero fee/state change, but the internal cause is the group cap, not the tile-limit branch. Do not relax the group invariant.
- **R-03 (MAJOR, coverage; decision).** The V4 no-active-round authority has no safe C# representation; command/context/actor construction rejects null or blank rounds. No-open-decision and stale-round are different cases and are not substituted.
- **R-04 (MAJOR, coverage; dead wall).** The V4 empty `deadWallTileIdsInDeadWall` filter is not a physical empty dead wall. Canonical C# requires 14 effective entities and has no filter input; the pointer-4 candidate set cannot be empty. The wall-exhausted zero-fee/zero-offer test is a different reachable control, not this source case.
- **P-01 (MINOR, provenance; capacity note).** `8 marked + 4 > 12` is an original arithmetic typo (`8 + 4 = 12`). The text and SHA remain frozen; the real input is 12+4. No false arithmetic assertion is required.

The four ledger rows remain `pending` until the caller explicitly accepts the representation boundaries. The subset TSV records `proposal=pending`, `proposedNextVerdict=classified`, `callerAcceptanceRequired=yes-representation-boundary`, concrete consumers, mutation negatives, representation reasons, owner/revisit dates and evidence references.

## 10. Deliverables

| File | Role |
|---|---|
| `doc/v5-重构/V5-N3-CHEAT-BOUNDARY.md` | This evidence report |
| `doc/v5-重构/V5-N3-CHEAT-BOUNDARY.tsv` | Four-row subset, mechanically joinable by frozen identity; preserves note text/SHA |
| `dotnet/tests/Mahjong.Domain.Tests/N3CheatBoundaryTests.cs` | 11 Domain tests: one frozen-fixture value anchor plus B1, capacity, domain no-round construction and dead-wall boundary |
| `dotnet/tests/Mahjong.TableRuntime.Tests/N3CheatBoundaryAdmissionTests.cs` | 2 actor tests for null-round construction rejection and the reachable no-open-decision contrast |

The delivery is one minimal local commit containing only these four new files. The commit hash and post-commit `git status`/`git diff --check` are reported in the final delivery message after the commit exists; this report does not embed a self-referential hash. No sibling delivery, production file, shared ledger, verifier or accepted report is modified.
