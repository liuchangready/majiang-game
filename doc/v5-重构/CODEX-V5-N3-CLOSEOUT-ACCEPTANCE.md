# N3 closeout ledger: caller independent acceptance

Date: 2026-10-26. Bridge thread: `thread_20261025_202301_2dc3`.
Accepted delivery: `c749cf8a41da4c49c6caf1c7e2c9b5a272d74105` on `wt/v5-n3-closeout-ledger`; frozen source baseline: `cae94de6c6f18a56134c6b0beeaec0e61ad4bc8e`.

The ledger component is accepted. All 592 frozen GAP instances have stable, unique identities and exactly one adjudication: **411 bound, 82 classified, 99 pending**, across 14 packages. Classified records remain uncovered; pending records retain owners and dated revisit conditions. This acceptance does not adjudicate those 99 records, approve G1/G5, or change the V4 production architecture.

Delivery changes exactly the three authorized files: `V5-G1-N3-CLOSEOUT-LEDGER.md`, its TSV, and `tools/verify-v5-n3-closeout-ledger.ps1`. Round three changes only the report (+27/-8). No production, test, fixture, catalog, manifest, accepted historical report, or unified verifier changes belong to this task. The caller adds this acceptance record separately.

## Closed findings

- D-R1-01/02/03/05 retain their previously reviewed fixes: nine E3 metadata/internal records are classified, current consumer anchors and evidence IDs are corrected, the verifier enforces provenance/obligations/identity digests, and contradictory arithmetic and evidence statements are repaired.
- D-R1-04 is now complete. Independent same-definition traversal reproduces movement **326/229/47973/38685**, shuffle-reversed **160/116/14236/13807**, and visibility **770/528/5142/3999** (normalized paths / scalar leaf paths / node instances / scalar leaf instances). Output JSON SHA256 is `b30b98958c0039acc9c5919099cb4c61443f86ee6ab56c268813fe35c801a961`, identical to the prior caller evidence. Embedded source pins and their dates match Git and are ancestors of delivery. Typed shuffle has no fabricated fixture denominator; production/consumer SHAs match the report.
- D-R2-01 is complete. Historical outer `dotnet test --filter` commands actually ran entire projects (506 Domain and 373 TableRuntime tests). The report retains those logs and labels them accurately. Valid direct xUnit selection is 124 tests across six Domain classes and seven actor admission tests. All five cited historical logs/XML files were rehashed successfully.

The historical 1516 paths, 655145 node instances, 592 GAP instances and 17 UNA instances remain unchanged. Incremental registration is not behavior coverage and does not count as full.

## Fresh caller validation

Evidence root E: `D:/majiang-wt/n3-closeout-ledger/artifacts/TEMP/codex-review-r3-362bbf2c1c804b65a0faa4bb20b0f60f`. No earlier review evidence was overwritten. Process-local TEMP/TMP points to E.

Commands run from the delivery worktree, with `dotnet` meaning `D:/Unity/dotnet/dotnet.exe`:

```powershell
dotnet build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj --no-incremental --no-restore -v minimal
dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-incremental --no-restore -v minimal
dotnet dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGoldenBindingTests' -class '*CheatGoldenTests' -class '*RoundReplayGoldenTests' -class '*ScoringGoldenTests' -class '*ShuffleGoldenBindingTests' -class '*PhysicalGoldenTests' -result-xml <E>/six-classes.xml
dotnet dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -class '*N3TableCheatGoldenAdmissionTests' -result-xml <E>/table-class.xml
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot <delivery-or-integration-worktree>
```

Both nonincremental builds exit 0 with zero errors. TableRuntime has zero warnings. The overlapping builds briefly contended for the shared Domain DLL; Domain reported one MSB3026 copy retry warning and then succeeded. Tests ran only after both builds succeeded: **124 + 7 passed**, Errors/Failed/Skipped/NotRun all zero, both native exits 0. Delivery verifier exits 0 with **24 CHECK-OK**, including all 26 evidence hashes and all 14 adjudication digests. The 20 historical rejection controls and older behavior mutation matrices were not rerun for this report-only round; their previously reviewed evidence remains historical.

| Current evidence | SHA256 |
|---|---|
| E/baseline.log | `4c5b0c7ce04d94f3d01e30b06c1d6a0bcb59f4f338e0cf32f2472747a71c80e3` |
| E/integrated-baseline.log | `e63ed5f2865762b6ae3cf235a3926451eaf545870040ed8864356121f3620166` |
| E/domain-build.log | `b5c2d5d6420ca7c469a98f46ed62490b3c5804b26fcad4560b8f6729256ab122` |
| E/table-build.log | `35115ef3000a2a453fd7085f2f46c43c2de30daab6aca4be326cfaf7d4e7a056` |
| E/six-classes.log | `b1f1f4a7f734a597221e5e6bc474cf31b718afde1b5806b13e37f1bdf9266918` |
| E/six-classes.xml | `5b267893a731fcddd0619886d2cc865a8f0216bd20882ce771819b91e2eb8296` |
| E/table-class.log | `2b63c620fe5d3c304a7cb8b36fbce3211cd96a2248cb1f9a1a394212370196f5` |
| E/table-class.xml | `8e3788762225045cfb8fe4f00388fe87d7b36646b545450416c66c9656c74fda` |
| E/integrated-catalog.log | `f567cda98c90489b7dff09ee5e8705347ebeee5da23cabea95751ba58dd60711` |

## Unified integration

Caller integration tree `0eb27a8f364cd8b5978216cfb1f3c076c667262e` merges the delivery without conflict onto published PR19 head `cc416c08512dbd18ed2cbf97b03b648bed780339`. Its net change is exactly the three delivery files; the acceptance record is the sole additional caller file. Delivery file bytes are retained, including restoring LF after checkout where needed. The entire integrated `dotnet` tree equals the freshly tested delivery tree. The integrated ledger verifier independently exits 0 with 24 checks; the current golden catalog verifier exits 0 with six fixtures, 73 requirements, 10389 cases, 10000 random inputs, 45 catalog Facts and full17/partial21/none32/v5-only3.

Already accepted N3-C1 and P4.2 commits are ancestors of this integration head and are already published in the same PR. Failed/unaccepted P4.1 is excluded; its status does not reject this ledger component. No additional accepted, unpublished changes from this batch were found. The existing PR19 (`feat/v5-1-integration` -> `v5`) remains the single MR; no per-component MR is created. Main-checkout changes to `CLAUDE.md` and `doc/ui/round-preparation/PROMPTS.md` are preserved.

The verifier deliberately requires the pinned historical census root and seven local review evidence files; integration stages copies only under ignored TEMP. It is an evidence/structure check, not a portable behavior oracle or a unified migration gate.

Bridge `approve_task` returned COMPLETED for the same thread before publication; no receiver, task, or caller session was replaced. Publication and MR synchronization are recorded through `record_merge_request`. No solution-wide suite, PG/WSS, Unity/IL2CPP, Gradle, device, physical-network, or new remote CI pass is claimed by this local acceptance. G1 remains unapproved and G5 incomplete.
