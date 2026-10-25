# N3 physical-v1 binding independent acceptance

Date: 2026-10-25. Bridge: `thread_20261025_124651_2ad6`, round 2.

Component **PASS**; Bridge approved and archived by the caller. G1/G5 remain
unapproved and V4 remains the only production architecture.

## Reviewed scope and repairs

Frozen baseline: `0c6eff1e47e99396d6a9c49dd32293d625a8bb03`.
Delivery: `3e9cd1a0986397f263cb82409ba054b6329eb78f`, including `7467e5b`.
The delivery worktree is clean; only PhysicalGoldenTests.cs, the physical
probe script and its field report changed. Production, exporter, fixture,
catalog, manifest, unified verifier and project/solution files are unchanged.

- D-R1-01: all accepted G-002 cases compare csharpMapping with the exact real
  factory branch selected by TileZoneRules.RequiresSeat. Both seatless and
  seat-required cases reject unknown/wrong/missing/null/wrong-type mappings.
- D-R1-02: positives require native0/Total9/Errors0/Failed0/Skipped0/NotRun0;
  negatives require native nonzero/Total9/Errors0/Failed>0/Skipped0/NotRun0.
  Infrastructure failures cannot count as expected negatives. Each probe
  restores MAHJONG_REPO_ROOT; recursive cleanup checks the absolute TEMP
  parent and the exact created GUID leaf before removal.

Independent review found no new known defect. Existing nine Facts remain.
Source-backed exception mappings and tile semantics use actual C# entry
points. V4 mutable-pool differences, unavailable V4 canonical hash,
unexported reversed wall and JVM RNG remain explicit comparison limits.

## Caller validation

All commands below returned native exit 0. Test totals are framework totals;
all test runs have Errors0/Failed0/Skipped0/NotRun0 unless they are deliberately
mutated negative controls.

| Check | Delivery | Isolated integration |
|---|---:|---:|
| Release solution build, nonincremental | 0 warnings/errors | 0 warnings/errors |
| Domain full | 385 passed | 392 passed |
| Physical external-copy matrix | 38/38 expected | 38/38 expected |
| Catalog verifier | PASS | PASS |
| Previous caller's independent matrix | 31/31 expected | not repeated |
| Diff check | exit0 | exit0 |

Each 38-probe matrix contains one baseline (9 passed) and 37 real negatives
(native nonzero and Failed>0), zero infrastructure failures and no unexpected
pass. Each also passes 12 classifier, 4 parser and 5 cleanup self-tests.
The independent 31-probe CSV was additionally checked against the same strict
exit/count rules: 31 rows, zero invalid rows.
Catalog: 5 fixtures / 73 requirements / 10382 cases / 10000 random / 42 facts;
the historical catalog fact count is not proof of new behavior coverage.

Evidence: `D:/majiang-wt/codex-physical-r2-review-b070591a8ce24a0599b911adc2d8fe80`.
Native exits and totals are in `verification-exits.json`; raw logs, mutated
fixture copies, CSV results and self-test results are retained there.
The evidence SHA-256 manifest is `evidence-sha256.csv`; repository artifact
hashes are in `reviewed-artifacts-sha256.csv`.
The manifest covers 243 files (excluding itself); its SHA-256 is
`C0E7CA97E739EE9C03DBC4D00E015FE72F165CDDEC01206208FA3D3966110AEE`.

Fixture SHA-256: `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995`.
Catalog SHA-256: `16F7FD7F980398388E4E5DA45A3D5FB755C19990915F403987094617E90E891B`.
Both remain unchanged.

## Reproduction and unified publication

Run from the delivery or integration worktree (use a fresh EvidenceRoot):

```powershell
$dotnet='D:/Unity/dotnet/dotnet.exe'
& $dotnet build dotnet/Mahjong.sln -c Release --no-incremental -v:minimal
& $dotnet dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noColor -noLogo
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot $PWD.Path
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-physical-binding.ps1 -RepoRoot $PWD.Path -EvidenceRoot ('D:/majiang-wt/physical-repro-'+[guid]::NewGuid().ToString('N'))
```

The earlier caller script remains at
`D:/majiang-wt/codex-physical-review-9c1f83bcb8404a1897ad96fab38e42f8/independent-probes.ps1`;
pass `-Repo <delivery worktree> -EvidenceDir <newly created exclusive directory>`.

Isolated integration starts at published PR19 head `f023df5` and cherry-picks
both delivery commits as `cabde78` and `c4c02c5`. Tested integrated code:
`c4c02c515a5c856d2d9e92b7bcbe1aec59fe4a9a`. The publication adds only this
acceptance record, and uses the existing single PR19
`feat/v5-1-integration -> v5`; no additional MR is created.
Other unaccepted cheat/field-audit candidates are excluded. User-owned
uncommitted files and other worktrees are not modified.

PG/WSS/Unity/IL2CPP/Gradle/device/physical-network tests and remote CI were
NOT_RUN for this review. Push-triggered CI is assessed separately. No broader
N3/G1/G5 approval or platform result is inferred from this component.
