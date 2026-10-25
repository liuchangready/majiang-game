# V5 N3 shuffle-v1 golden field binding

Status: N3 binding + independent-review rework, 2026-10-25.
Worktree: `D:/majiang-wt/n3-shuffle-binding`
Branch: `wt/v5-n3-shuffle-binding`
Parent commit: `aaa07af10b77006b9c653ecef63710e8d04b458b`
Frozen PR19 base: `0c6eff1e47e99396d6a9c49dd32293d625a8bb03`

This document is the persistent report for the shuffle-v1 binding. It is **not** a G1/G5
decision. V4 remains the only production baseline. No production C#/Kotlin, V4 exporter,
fixture, catalog/manifest, unified verifier, project/solution or user-environment file is
changed by this work.

## 1. Files and scope

- `dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenBindingTests.cs` — 7 binding Facts.
- `tools/v5-golden/probe-shuffle-binding.ps1` — external single-field mutation matrix.
- `doc/v5-重构/V5-N3-SHUFFLE-GOLDEN-BINDING.md` — this report.

The four pre-existing `ShuffleGoldenTests` Facts are unchanged. They remain the structural
baseline; the new class adds the real `ShuffleWallSynthesizer.Shape` entry-point binding and
the exact-case-set completeness checks that the structural Facts do not provide.

## 2. Field classification (32 logical cases)

`tests/v5-golden/shuffle-v1.json` has 16 top-level fields and 32 logical cases with 13
case fields. All 32 seeds are bound as the exact set `0..31`, not merely array length 32;
case order is non-semantic.

| Path | Binding class and actual consumption |
|---|---|
| `schema`, `generatorVersion`, `rulesetVersion`, `extractionStatus` | exact scalar values |
| `generator` | exact real exporter path `engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt`; the file must exist when the repository root is discoverable |
| `v4Commit` | 40 lowercase/uppercase hex; no ancestry re-verification |
| `generatedByCommand` | contains the exporter test class name; the command is not executed by C# |
| `exportedAtNote` | contains `actual V4 ShuffleWallGenerator`; not a behavioral proof |
| `v4RulesSourceNote` | string; must name `engine/src/main/kotlin/com/majiang/stacking`, contain `V4 production source` and `v4Commit above is the exact tree used`; directory must exist when the repo root is discoverable |
| `rngGapNote` | contains `D-09`, `behavior samples`, `never wall bytes` |
| `generationResult` | 4 exact fields; counts checked; `threeCasterChangedSeeds` recomputed from the 32 case flags |
| `slotMatrix` | 16 entries; `ShuffleSlots.Initial` / `Draws(...,18)` exact int lists |
| `demandVectors` | 6 × 34; `ShuffleTargets.Demand` at 12 decimals |
| `overlapMatrix` | 6 × 6; `ShuffleTargets.Overlap`, symmetry, 12 decimals |
| `configSnapshot` | `new ShuffleConfig(enabled:true)` defaults; 6 scalars, 3 tiers, 2 mixed tiers, 15 target tiers |
| `cases[].seed` | exact set `0..31`, no duplicate/missing/out-of-range; `dealerSeat == seed % 4` |
| `cases[].dealerSeat` | Shape input and `TilePositionMap.CreateDealt` input |
| `cases[].plans` | Shape input; 32 × 3 parsed plans; unknown target, invalid seat, duplicate seat fail |
| `cases[].baseWall` | real Shape input; 136 unique/range/3 red fives |
| `cases[].threeCasterWall` | `INTERNAL_EXPORTED_WALL_RELATION`, **not Shape output**: conservation, red-five, `DeadWall(0..13)` mapping, dead-wall segment, `threeCasterChanged` |
| `cases[].fourCasterWall` | `SHAPE_OUTPUT`: real n=4 Shape with empty trace, tile-for-tile |
| `cases[].noTargetWall` | `SHAPE_OUTPUT`: real n=0 Shape with empty trace, tile-for-tile |
| `cases[].zeroBudgetWall` | `SHAPE_OUTPUT`: real zero-budget Shape with legal vacuous trace, tile-for-tile |
| `cases[].redFiveCount` | physical identity count across all five exported walls and the three Shape outputs |
| `cases[].deadWallSegmentEqual` | `INTERNAL_EXPORTED_WALL_RELATION`: `baseWall[122..135] == threeCasterWall[122..135]`, independently mapped through `TilePositionMap.DeadWall(0..13)`; not a Shape output |
| `cases[].threeCasterChanged` | recomputed `!baseWall.SequenceEqual(threeCasterWall)` |
| `cases[].fourCasterIdentity` | recomputed `base == four`, plus n=4 Shape output equality |
| `cases[].reversedEqualsThree` | `SOURCE_ONLY`: no `reversedWall` in the fixture; C# cannot recompute it |

### 2.1 32-case completeness

The V4 exporter loop is `for (seed in 0L..31L)`. The binding therefore requires:

- exactly 32 array entries;
- `HashSet<int>` insertion succeeds for every `seed`;
- the final set equals `Enumerable.Range(0, 32)`.

This rejects 32 copies of seed 0, one duplicated sample replacing another, a missing sample,
and seed 32 with `dealerSeat = 0`. The set assertion is order-independent, so the legal
case-reordering mutation still passes. No fixture hash is pinned.

The fixture has **32 logical cases**. The test class traverses them in several Facts (identity
Shape, dead wall, relation flags, conservation), so the number of test iterations is larger
than the number of logical cases; the report does not treat repeated traversal as extra
sample coverage.

### 2.2 Instance counts

- 32 cases × 5 exported walls = 160 wall arrays checked for 136 uniqueness, full 0..135 and 3 red fives.
- 96 real identity Shape calls: 32 no-target + 32 four-caster + 32 zero-budget.
- 1 additional zero-budget Shape call with empty trace (negative assertion).
- 64 `TilePositionMap.CreateWall` calls (32 base + 32 three-caster) and 32 `CreateDealt` calls.
- 256 wall conservation checks (160 exported + 96 Shape outputs).
- 16 slot entries, 204 demand values, 36 overlap values, 20 tier entries, 96 parsed plans.

## 3. Source anchors

| Anchor | Location |
|---|---|
| V4 exporter loop / plans / config | `engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt:46,48-55,61-68` |
| V4 export flags | `.../V5ShuffleGoldenExporterTest.kt:98-101` |
| V4 slot/demand/overlap/config export | `.../V5ShuffleGoldenExporterTest.kt:107-152` |
| V4 JVM RNG origin | `engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt:63-65` |
| V4 n=0 / n=4 identity | `ShuffleWallGenerator.kt:78-86` |
| V4 bounded shuffles / doubles | `ShuffleWallGenerator.kt:134-136,142,145,172-181,333` |
| C# Shape entry point | `dotnet/src/Mahjong.Domain/Stacking/ShuffleWallSynthesizer.cs:128-235` |
| C# n=0 / n=4 / exact-trace branches | `ShuffleWallSynthesizer.cs:271-305,499-511` |
| C# Fisher-Yates double consumption | `ShuffleWallSynthesizer.cs:949-965` |
| C# wall slot → dead wall | `dotnet/src/Mahjong.Domain/TilePositionMap.cs:1778-1790` |
| C# initial deal → hand/dead wall | `TilePositionMap.cs:1805-1832` |
| C# slot definitions | `dotnet/src/Mahjong.Domain/Stacking/ShuffleSlots.cs:18-91` |
| C# target demand/overlap | `dotnet/src/Mahjong.Domain/Stacking/ShuffleTargets.cs:39-90,136-182` |

## 4. Remaining BLOCKED items and the corrected minimal dependency

### 4.1 threeCaster Shape — BLOCKED_RANDOM_INPUT

`Shape(baseWall, empty trace, dealer, threePlans, enabledConfig)` returns
`TraceExhausted`, `Wall=null`, `ConsumedRandomCount=0`. The fixture has no post-base-wall
random record, so `threeCasterWall` cannot be claimed as C# Shape output.

The earlier wording "record only the exact `rng.nextDouble()` sequence" was **incorrect**.
V4's decision phase consumes two different kinds of random decisions after the base wall is
formed:

1. `activePlans.map { it.seat }.shuffled(rng)` and `initialSlotsMap[seat]!!.shuffled(rng)`
   (also the draw-phase equivalents) use bounded Fisher-Yates selections, i.e. an integer
   index decision per element, not a `nextDouble()`.
2. `rng.nextDouble()` is then used for the last-chance sub-unit budget, the proficiency
   check, and the donor Δ² weighted pick.

C# `ShuffleWallSynthesizer.ShuffledInPlace` consumes one `[0,1)` double per Fisher-Yates
index and computes `floor(pick * (i + 1))`. Therefore a faithful export must capture the
**ordered typed events**, for example:

```text
after base wall formation, for each case:
  { kind: "nextInt",   bound: n, result: r }   // every bounded shuffle choice
  { kind: "nextDouble", value: v }             // last chance / proficiency / donor
```

An adapter can then encode each captured bounded integer result as a `[0,1)` double that
satisfies the C# floor selection, preserving the exact event order. Recording only
`nextDouble` values is **not sufficient**, and the integer choices cannot be guessed from
`threeCasterWall` or the seed. The recording seam and the per-wall verification remain
BLOCKED pending authorization; no RNG simulation, trace guessing, reverse engineering or
fixture change is performed here.

Minimal future export field: `cases[].threeCasterRandomEvents` (ordered typed events), or an
equivalent ordered event stream plus an explicitly documented adapter. A test-only
recording `Random`/`RandomSource` seam at `ShuffleWallGenerator.kt:63` is required. The
acceptance must compare all 136 slots for 32 cases, reject truncated/extra events, and must
not claim success until that authorized seam is implemented.

### 4.2 reversedEqualsThree — BLOCKED_MISSING_REVERSED_WALL

The exporter computes `reversed` at `V5ShuffleGoldenExporterTest.kt:65` and writes only the
boolean at line 101. The fixture has no `reversedWall`; C# can consume the source-only flag
but cannot recompute it. Minimal extension: `cases[].reversedWall: number[136]`.

### 4.3 zeroBudget empty trace — premise correction

V4 still shuffles the active seat list and the initial/draw slot arrays before observing
`remaining <= 0.0`; with `tier(1.0, initial=0, draw=0)` a legal trace is therefore 28 or 29
vacuous zeros, not empty. Empty trace is `TraceExhausted`. The zero values are a legal
equivalence-class representative: no random branch can alter the wall when both budgets are
zero. This is recorded as a premise correction, not a production defect.

`reversedEqualsThree` is **SOURCE_ONLY**, not a C# recomputation; the report does not count it
as behavior binding.

## 5. Verification

| Check | Result |
|---|---|
| Domain.Tests non-incremental Release | 0 warning / 0 error / native 0 |
| solution non-incremental Release | 0 warning / 0 error / native 0 |
| new 7 binding Facts | Total 7, Errors 0, Failed 0, Skipped 0, Not Run 0, native 0 |
| old 4 Facts | Total 4, Errors 0, Failed 0, Skipped 0, Not Run 0, native 0 |
| Physical positive control | Total 9, Errors 0, Failed 0, Skipped 0, Not Run 0, native 0 |
| Domain full | Total 392, Errors 0, Failed 0, Skipped 0, Not Run 0, native 0 |
| external matrix | 33 scenarios, 0 violations, baseline 7/0/0/0/0, native 0 |
| matrix negative scenarios | 30 scenarios, native 1 each, Total 7, Failed > 0, 0 skipped/errors; 76 expected test failures |

The matrix includes three green scenarios (baseline plus legal plan-order and case-order
mutations) and 30 negative scenarios. New negative scenarios include
`duplicate-all-samples`, `duplicate-one-sample`, `missing-one-sample`, `out-of-range-seed`,
`generator-wrong-type`, `generator-wrong-path`, `rules-source-wrong-type` and
`rules-source-wrong-path`. All 30 must have a non-zero native exit and `Failed > 0`; missing
summaries, discovery drift, skips and tool aborts are violations. No old artifact is used for
green.

Readiness remains:

```text
G1                                      NOT_RUN
threeCasterShapeBinding                 BLOCKED_RANDOM_INPUT
reversedEqualsThreeIndependentRecompute BLOCKED_MISSING_REVERSED_WALL
zeroBudgetEmptyTrace                    NOT_APPLICABLE (vacuous trace contract)
PG / WSS / Unity / Gradle / device / remote CI  NOT_RUN
G5                                      NOT APPROVED
```

The matrix PASS is not a G1 pass and does not unblock the missing-input items.

## 6. Reproduction commands

```powershell
$R = 'D:/majiang-wt/n3-shuffle-binding'
$DOTNET = 'D:/Unity/dotnet/dotnet.exe'

& $DOTNET build "$R/dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj" -c Release --no-incremental -v minimal
& $DOTNET build "$R/dotnet/Mahjong.sln" -c Release --no-incremental -v minimal

& $DOTNET "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" -noLogo -class '*ShuffleGoldenBindingTests*'
& $DOTNET "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" -noLogo -class '*ShuffleGoldenTests'
& $DOTNET "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" -noLogo -class '*PhysicalGoldenTests*'
& $DOTNET "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" -noLogo

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$R/tools/v5-golden/probe-shuffle-binding.ps1" `
  -RepoRoot $R -Dotnet $DOTNET -OutDir "$R/../n3-shuffle-binding-evidence-20261025-r2/probe"
```

The probe requires a new non-existing `OutDir`, a new exclusive TEMP root, restores
`MAHJONG_REPO_ROOT` in `finally`, checks the dotnet executable/version, fixture schema/count,
binary freshness, discovery stability and tool errors. It never writes the repository
fixture.

## 7. Status ledger

| Item | Status |
|---|---|
| 32-case exact seed set | PASS (new assertion) |
| META `generator` / `v4RulesSourceNote` values | PASS (string + real path/source anchor; wrong type/path negatives fail) |
| Persistent report in git | this file |
| threeCaster Shape | BLOCKED_RANDOM_INPUT (typed event export + adapter required) |
| reversedEqualsThree | BLOCKED_MISSING_REVERSED_WALL |
| zero-budget empty trace | NOT_APPLICABLE; vacuous trace is the legal representative |
| G1 / G5 | NOT_RUN / NOT APPROVED |
| PG / WSS / Unity / Gradle / device / remote CI | NOT_RUN |

No push, PR, CI, verifier change or default-branch operation is performed. The worktree
retains the merge-back obligation; the caller owns independent review, isolated integration,
the single PR19 `feat/v5-1-integration -> v5` push and `record_merge_request`.
