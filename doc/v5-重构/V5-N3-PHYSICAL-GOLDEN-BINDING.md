# V5 N3 physical-v1 golden binding — field and probe report

Date: 2026-10-25
Task: P1.1/N3 existing physical-v1 consumer completion (not a re-implementation of P1.1 production).
Branch: `wt/v5-n3-physical-binding`
Frozen baseline: `0c6eff1e47e99396d6a9c49dd32293d625a8bb03`
Delivery HEAD: reported in the handoff for this round; the worktree is clean at that commit.

Authorized changes:

- `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` — 9 Facts, all fixture fields consumed.
- `tools/v5-golden/probe-physical-binding.ps1` — strict external-copy probe with policy self-tests.
- `doc/v5-重构/V5-N3-PHYSICAL-GOLDEN-BINDING.md` — this report.

Not changed: production C#, V4 exporter, `tests/v5-golden/physical-v1.json`, catalog, manifest, unified verifier, project/solution files, user files/environment.

Fixture SHA-256: `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995`.
Catalog SHA-256: `16F7FD7F980398388E4E5DA45A3D5FB755C19990915F403987094617E90E891B`.
`[Fact]` count remains 9, matching catalog `factCount=9`.

## 1. D-R1-01: accepted G-002 mapping branch is now behavior-bound

Location: `PhysicalGoldenTests.G002ZoneOwnerSemanticsMapToImmutablePositions`.

Before: `csharpMapping` was read, but in the `outcome=accepted` branch it was not
used. `samples[G-002].expected.cases[0].csharpMapping='unknown-mapping'`
(`loc-wall-default`) still produced 9/0/0/0/0, native 0.

After: every accepted case calls the real factory selected by
`TileZoneRules.RequiresSeat(zone)` and requires the exact mapping:

- seatless zone → `seatless-factory`;
- seat-required zone → `seat-factory`;
- unknown value and a legal value naming the wrong branch both fail;
- missing, null and wrong-type values fail through the normal strict string reader.

The 20 cases and 9 Facts are retained. Accepted counts are asserted as 3
seatless-factory and 7 seat-factory; rejected counts remain 4
`structural-owner-forbidden` and 6 `invalid-seat`.

Unified negative matrix (10 probe cases, wall=seatless and hand=seat):

| Case | Mutation | Expected |
|---|---|---|
| `g002-accepted-wall-mapping-unknown` | `loc-wall-default.csharpMapping='unknown-mapping'` | native≠0, Failed>0 |
| `g002-accepted-wall-mapping-wrong` | `loc-wall-default.csharpMapping='seat-factory'` | native≠0, Failed>0 |
| `g002-accepted-wall-mapping-missing` | remove `csharpMapping` | native≠0, Failed>0 |
| `g002-accepted-wall-mapping-null` | `csharpMapping=null` | native≠0, Failed>0 |
| `g002-accepted-wall-mapping-wrongtype` | `csharpMapping=7` | native≠0, Failed>0 |
| `g002-accepted-hand-mapping-unknown` | `loc-hand-0.csharpMapping='unknown-mapping'` | native≠0, Failed>0 |
| `g002-accepted-hand-mapping-wrong` | `loc-hand-0.csharpMapping='seatless-factory'` | native≠0, Failed>0 |
| `g002-accepted-hand-mapping-missing` | remove `csharpMapping` | native≠0, Failed>0 |
| `g002-accepted-hand-mapping-null` | `csharpMapping=null` | native≠0, Failed>0 |
| `g002-accepted-hand-mapping-wrongtype` | `csharpMapping=7` | native≠0, Failed>0 |

Caller's own 31-probe script (`independent-probes.ps1`) now reports 31 declared,
31 completed, 0 failures; the former `review-g002-accepted-mapping` unexpected
pass is a real native-1/Failed-1 negative.

## 2. D-R1-02: strict probe classification and safe cleanup

Location: `tools/v5-golden/probe-physical-binding.ps1` (`Get-ProbeClassification`,
`Get-FrameworkSummary`, `Assert-SafeProbeRoot`, `Invoke-Probe`).

The previous script classified an observed pass only as
`nativeExit==0 && errors==0 && failed==0`; a negative probe with native exit 2,
Errors 1, Failed 0 was therefore accepted as `FAIL_EXPECTED`. It also did not
require `Errors==0`/`Failed>0` for real negatives and had no classifier self-test.

Current policy, enforced for every probe:

- Positive: native 0, Total 9, Errors 0, Failed 0, Skipped 0, Not Run 0.
- Negative: native ≠ 0, Total 9, Errors 0, Failed > 0, Skipped 0, Not Run 0.
- Missing summary, multiple summaries, Total 0, tool/runner errors, skipped or
  not-run tests, and any exit/summary contradiction are hard failures, never
  expected negatives.
- `completed != declared` or any infrastructure failure → exit 2.
- An unexpected domain pass → exit 1.

Self-test matrix (12 classifier cases) includes the exact caller mock
(native 2, Total 9, Errors 1, Failed 0) and classifies it `INFRA_FAIL`; it also
covers native 0 + Failed 1, negative with Errors 1 + Failed 1, native ≠ 0 with
Failed 0, positive with Failed 1, Total 0, Skipped 1 and Not Run 1.
Parser self-tests reject a missing summary, zero total, exit 0 with Failed 1 and
native 1 with zero Failed. Cleanup self-tests reject wrong leaf shape, wrong
parent, wrong expected leaf and traversal; the valid created-shape path is
accepted.

Cleanup safety: the absolute path must have the process TEMP directory as its
exact parent and a leaf equal to the GUID leaf created by this invocation; the
path is validated again immediately before `Remove-Item -Recurse`. The
environment `MAHJONG_REPO_ROOT` is restored in every per-probe `finally`.

## 3. Field consumption matrix

Legend: IN/OUT = fixture drives a real entry point and result is compared;
INTERNAL = aggregate/construction consistency; META = provenance/description,
not behavioral equivalence; BLOCKED = unrepresentable cross-language structure.

| Sample | Consumed fields | Real entry point / assertion | Exporter source |
|---|---|---|---|
| G-001a | `size/distinctIdentities/redFiveCount/nonRedFiveCount/standardSetOrderIsTileIdOrder/redFiveTileIds/tiles[*].{tileId,typeIndex,copyIndex,suit,rank,is*}` | `new TileId(id)`, `TileId.All[i]`, recomputed aggregates; 136 tiles | `V5PhysicalGoldenExporterTest.kt:80-95` |
| G-001b | `boundaryTypeIndexes/boundarySamples[*].{all 11}/groups[*].{typeIndex,count,tileIds,suit,rank}/distinctTypeIndexes/totalTiles/allGroupsFour/tileIdFormula/suitOrder` | `FromTypeIndex`; 10 boundaries, 34×4 groups; suitOrder checked over contiguous 0..33 | `:100-147` |
| G-001c | `validCount/validRoundTrips[*]/allRoundTripEqual/boundaries[*].{all 11}/invalid[*].{input,outcome,exceptionType,message,semanticCategory}/csharpExceptionMapping.*` | `new TileId`, `FromTypeIndex`, `TryFromValue`, real exception `ParamName/ActualValue/FullName`; source anchor `Tile.kt:112-120` | `:152-191` |
| G-002 | `cases[*].{caseId,zone,player,outcome,constructed,zoneOut,playerOut,exceptionType,message,semanticCategory,csharpMapping}/acceptedCount/rejectedCount/csharpStructuralNote` | Real `TilePosition` factory or real exception; accepted mapping exact branch; 20 cases | `:196-282` |
| G-003a | `seedCount/cases[*].{seed,dealerSeat,wall,baseWall,redFiveTileIds,redFiveCount,conserved,positions[*].{tileId,zone,seat,slot},deadWallSegmentEqual,reversedEquals,wallCanonicalSha256}/all*/positionsRecorded` | `RecordedRandomDraw` + `TilePositionMap`; 32 walls × 136 = 4,352 positions; digest recomputed | `:284-388` |
| G-003b | `probeA.*`, `probeB.*`, `duplicateKeyStaticLimitation.*` | `TilePositionMap.FromPlacements`/read-only `AsPlacements`; target tile semantics; actualTiles permutation/count/SHA-256; audit messages | `:390-556` |
| `hashCrosswalk` | `v4CanonicalStateHash/v4Reason/csharpHash/wallCanonicalDigestAlgorithm/csharpHashVerification` | META + source anchor `StateInvariants.kt`; no 16-hex C# hash stored; same-map rebuild stable; G-003a case 0 digest recomputed | `:612-629` |

All sample metadata (`manifestStatusBefore`, `source.*`, `command`, `input.*`)
is consumed; absent aliases are asserted `not-applicable`. No `expected` leaf
field is read-only/unasserted.

## 4. Not-comparable / blocked

1. V4 canonical state hash is `NOT_AVAILABLE`; C# `TileStateHash` is a V5-only
   FNV-1a definition, not a V4 digest.
2. V4 mutable `TilePool` vs C# immutable `TilePositionMap`; duplicate-key branch
   is `NOT_CONSTRUCTIBLE`; ledger desynchronisation is structural.
3. `reversedEquals`/`allOrderIndependent`: V4 reversed-plan wall is not
   exported, so C# binds the boolean/aggregate but cannot recompute it.
4. G-003a does not claim C# reproduces the JVM RNG; exported walls are explicit
   inputs.
5. `actualTiles` order is V4 container traversal; only permutation/count/SHA is
   verified.
6. V4 `IllegalArgumentException` vs C# `ArgumentOutOfRangeException` is a
   semantic mapping, not class equality.
7. V4 absolute slots 122..135 map to C# `DeadWall` 0..13.
8. `manifestStatusBefore`, `gitStatusPorcelain`, `taskPaths` are historical
   provenance, not current-worktree or behavior claims.

## 5. Verification

Commands:

```powershell
$ev='D:/majiang-wt/n3-physical-binding-evidence-20261025-1540'
$dotnet='D:/Unity/dotnet/dotnet.exe'

& $dotnet build 'dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj' -c Release --no-incremental -v:minimal
& $dotnet 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' -class 'Mahjong.Domain.Tests.PhysicalGoldenTests' -noColor -noLogo
& $dotnet 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' -noColor -noLogo
& $dotnet build 'dotnet/Mahjong.sln' -c Release --no-incremental -v:minimal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'tools/verify-v5-golden-catalog.ps1' -RepoRoot 'D:/majiang-wt/n3-physical-binding'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'tools/v5-golden/probe-physical-binding.ps1' -EvidenceRoot $ev
```

Expected results:

- Domain.Tests build: native 0, 0 warnings / 0 errors.
- Targeted: Total 9, Errors 0, Failed 0, Skipped 0, Not Run 0, native 0.
- Domain full: Total 385, Errors 0, Failed 0, Skipped 0, Not Run 0, native 0.
- Solution build: native 0, 0 warnings / 0 errors.
- Catalog verifier: native 0, PASS (5 fixtures / 73 requirements / 10382 cases /
  10000 random / 42 facts).
- External probe: 38 declared / 38 completed / 1 PASS baseline / 37 expected
  negatives / 0 unexpected / 0 infrastructure failure / fixture preserved,
  native 0.
- Caller's `independent-probes.ps1`: 31/31 expected, native 0.
- Policy self-tests: 12 classifier + 4 parser + 5 cleanup cases all pass,
  `-SelfTestOnly` native 0.

## 6. Status

Component result: PASS for the physical-v1 consumer completion and probe policy.

Not approved / not claimed:

- G1 and G5 are not approved.
- V4 remains the only production architecture.
- PG/WSS/Unity/Gradle/device/remote CI were not run this round; no such pass is
  claimed.
- No production, exporter, fixture, catalog, manifest, unified verifier or
  project/solution change.
- Caller owns independent review, isolation integration, the unique PR19 line,
  push and `record_merge_request`; this worktree retains its merge-back
  obligation.
