# N3 residual cheat-boundary independent acceptance

Date: 2026-10-26. Bridge task: `thread_20261026_071845_72ec`.

The test and evidence component is accepted at delivery `ba74567a506b0277f8c94766c71b93c16806d2f2`, based on `85134e9ce4fd67556f6d2f355249c14f4d3b42d4`. The original `58758e5` commit and the round-two repair remain in history. Bridge `approve_task` returned `COMPLETED`.

## Scope and repair decisions

The net delivery adds only the four authorized paths: `N3CheatResidualBoundaryTests.cs`, `N3CheatResidualAdmissionTests.cs`, `V5-N3-CHEAT-RESIDUAL-BOUNDARIES.md` and its TSV. There are no production, existing-test, fixture, catalog, manifest, verifier or shared-ledger changes. Old file/class names have no surviving copies. Delivery status is clean and `git diff --check 85134e9 HEAD` exits 0.

- D-R1-01 is closed: paths, class names, selectors and references match the frozen expectedFiles.
- D-R1-02 is closed: B1 executes swap, successful discard of 29, cleared drawn evidence, 29 in seat-0 discards, then next-seat draw of 33 at Hand(1,13), pointer 55, tail 25 and 136 distinct placements. The resolved-index 1-to-2 negative remains. V4 follow-up is explicitly a static derivation, not a measured runtime result or complete bidirectional proof.
- Capacity evidence distinguishes the exact V4 two-group/12-marked state from the C# three-group/12-marked analogue. The real source arithmetic is 12+4>12. Original note text/SHA is preserved; 8+4 is a successful positive control. Rejection has no fee/state/events.
- Decision evidence distinguishes null-round construction rejection from the reachable actor no-open-decision rejection; neither is relabelled as the other.
- Dead-wall evidence distinguishes an empty V4 eligibility filter from physically missing dead-wall entities. Canonical pointer-four maps yield 14 candidates (nine with five dora indicators); invalid 13-effective maps reject construction. The wall-exhausted control separately proves zero fee/offer.

An independent TSV join found exactly one original ledger row for each identity. Package, fixture, caseId, path, idx, kind, parent and histClass match; SHA-256 recomputation matches all four preserved notes. Each source ledger verdict and each proposal is still pending.

Concurrency/recovery, public/private projection, transport and platform production code are unchanged. No new production claims are inferred from these tests.

## Independently executed verification

Working directory: `D:/majiang-wt/n3-cheat-boundary-20261026`.
Evidence directory E: `artifacts/TEMP/codex-n3-r2-review-20261026` (ignored). Process TEMP/TMP point to E on D:.

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj --no-incremental --no-restore
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-incremental --no-restore
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*N3CheatResidualBoundaryTests' -result-xml <E>/domain.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -class '*N3CheatResidualAdmissionTests' -result-xml <E>/table.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -method '*Backfill_V4AliasAndLegalDiscardDrawFollowUp' -result-xml <E>/backfill.xml
```

Both fresh builds exit 0 with zero warnings/errors. Direct runners exit 0. XML test identities and runner summaries confirm Domain 11/11, actor 2/2 and B1 method 1/1, each with Errors/Failed/Skipped/Not Run = 0. No zero-selection control is counted as coverage.

| Evidence under E | SHA-256 |
|---|---|
| domain-build.log | 7A016E429EF71ED77142B23C72D6FB3F39F5D04249379AA9642D8273C04DAA1E |
| table-build.log | ADCD4E35356F841686248715A57BA66E77177D460C265E969C6CE96377E21AC1 |
| domain.log | 82D0CD4A56B4F66D8FBBE5E7B768C3950ABF04DE18AF7DF540F75E639F070481 |
| table.log | F948AE236E4E8B1D1B0C589DF26F6EC0E8EC536A8BD446354413664B866043D1 |
| domain.xml | 024B1D7E27A636C7B83BB2369A8345F3D8F4DF4FFF97372C9E074E09199D07D9 |
| table.xml | 882FD9705391633BC8D5BEAC0ECE17DEEEA14304EF0956B6335A0E2069657064 |
| backfill.log | B9D9D343A1D171F387697DECDC4AC12C36846350C34206DFC3F49ABA5321A19A |
| backfill.xml | 244A65AE4C8E4907CEA04C9B079D7A103C82D3710EC8621796A87721BA90769C |

The caller merged the delivery in isolated `D:/majiang-wt/codex-n3-residual-final-20261026`, based on the existing remote integration head. `git diff --exit-code ba74567 HEAD -- dotnet tools tests` exits 0: integrated implementation/test/fixture trees equal the freshly tested delivery. Only this caller acceptance record is added afterward. No other unaccepted sibling implementation is included.

## Acceptance limits and publication

PASS: this task's authorized test/evidence delivery and both repair obligations. FAIL: none in the final relevant verification. Remaining coverage: all four representation differences await explicit caller adjudication; accepting this evidence does not accept their proposed classification. NOT_RUN: solution-wide suite, PG/WSS, Unity/IL2CPP, Gradle, devices, physical network and new remote CI.

The shared vector remains **592 = 411 bound + 175 classified + 6 pending**. Classified remains uncovered; historical 1516 paths / 655145 nodes / 592 GAP / 17 UNA are unchanged. Gate 2/N3/G1/G5 are not approved, V4 remains production, and no complete.flag is created.

The same-batch N-2 host and AI-discard tasks are still in rework at this acceptance snapshot. They do not veto this component and are not included before independent acceptance. Publication uses the existing single PR19, `feat/v5-1-integration` to `v5`, with no component PR and no merge of PR19. Remote publication SHA and URL are recorded by the caller in the same Bridge thread after push and remote read-back.
