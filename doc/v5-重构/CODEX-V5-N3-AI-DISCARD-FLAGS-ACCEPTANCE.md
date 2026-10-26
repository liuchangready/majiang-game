# N3 B6 AI discard flags: independent acceptance and unified batch

Date: 2026-10-26. Bridge thread: `thread_20261026_071845_5a2b`.
Accepted delivery: `29e11c1` (including `daa834a`), frozen baseline `85134e9ce4fd67556f6d2f355249c14f4d3b42d4`.
Bridge approval returned COMPLETED in the existing thread and caller context.

The component passes its scope: a companion golden fixture contains real V4 nonempty ordinary, riichi and claimed discards, and C# consumes their flag values through the existing public API. V4 production is unchanged. Both original ai-v1 mapping-limit rows remain pending in the shared ledger until explicit serial adjudication; accepting this evidence does not apply their classified proposals or declare bound/full, gate 2, G1 or G5.

## Rework verification

- D-B6-01: the replacement counter follows the accepted Visit algorithm: count every value before type dispatch, scalar as both node and leaf, root $, arrays [*]. Only the new fixture was counted: **89 paths / 63 leaf paths / 401 node instances / 292 leaf instances**. The old 26/109 container-only counts remain labelled historical errors. Historical denominators 1516/655145/592/17 are unchanged.
- D-B6-02: the new TSV has exactly two records and 32 columns. Thirteen identity/history fields per row match the frozen AI-text TSV byte-for-value, 26/26 matches, zero mismatches. Package is GAP:N3-B6; fixture is ai-v1.
- D-B6-03: baseline-to-delivery diff contains exactly six expected files. Both former B6-named document paths are absent. Behavior source and fixture bytes are unchanged from daa834a. Delivery status is clean and diff-check passes.

The exporter drives legal V4 actions, derives riichi by physical tile/seat from TileDiscarded, and identifies claimed tiles through public meld metadata and their absence from active rivers. The three cases include both flags true on the same claimed riichi discard. The fixture exports public discards/events/melds, with no private wall, seed or concealed hand. PlayerView still has no per-discard flag fields; this limitation is preserved.

## Fresh caller validation in the delivery worktree

Evidence root E: `D:/majiang-wt/n3-ai-discard-20261026/artifacts/TEMP/codex-b6-r2-1792981825345`. Process-local TEMP/TMP points to E.

Commands from the repository root:

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-incremental --nologo
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests -result-xml <E>/original.xml
powershell -NoProfile -ExecutionPolicy Bypass -File <E>/review.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File <E>/structural.ps1
./gradlew.bat :engine:test --tests com.majiang.state.V5AiDiscardFlagsGoldenExporterTest --rerun-tasks --console=plain
```

- Fresh build: native exit 0, zero warnings/errors.
- Direct runner/XML: total 6, passed 6, errors/failed/skipped/not-run all 0.
- Five isolated fixture mutations: each native exit 1, total 6, failed **2/2/1/2/2**, errors/skipped/not-run 0. Claim-river, riichi-river-on, riichi-river-off, claim-claimed and riichi-claimed reach value assertions; no missing-fixture failure is accepted.
- All 11 source SHA pins match the delivery source bytes.
- Gradle: native exit 0, XML tests 2, failures/errors/skipped 0. In default mode the validate method actually re-derives all three cases and verifies the render digest; the export method returns by design. This is not a claim of two active derivations or a new export this round.
- Structural verification: native exit 0, expected counts/identities/paths and clean state. The first caller structural attempt used a BOM-less script under Windows PowerShell and failed on its Chinese path; the corrected UTF-8 BOM script passes. The first mutation-script invocation was blocked by process execution policy; the explicit per-process Bypass invocation passes. Both are caller invocation issues, with no delivery changes.

| Evidence | SHA256 |
|---|---|
| E/build.log | C9CC6A0116BD701FBF3872F5FBAE345D0AB4C33AC055759E4F8E844E8D58480D |
| E/original.xml | 4D9EA2399A8BA656AFBCA1D3159159F0E7FA8AB60A0EDB1518ED0566DC31C512 |
| E/mutations.csv | A4D12CDF5024F01A28FB0158C9C617A95C904853ADD87C60144FFE1FEA63DBB0 |
| E/structural.log | E22766718ED54FCAF7851EC1C99F3557E9A9A22A21CC1FBFF831D8D05B104A97 |
| E/gradle.log | E9C8C0CD641F55D9C0554A167F4C73B8B7CFC9CDE05AAD47C69A0E6CEB9B9365 |
| E/gradle.xml | DAB89FA727DA204DC6D196F69646092B69D54F6FD336D1C55C990E21C9236A3E |

## Unified PR19 integration

The caller uses an isolated worktree `D:/majiang-wt/codex-b6-batch-final-20261026`, starting from published PR19 head `00c71cd72b3786c1d41c5d48951824f2150867ee`. Accepted unpublished N-2 host work `2760c6e` (including 316fa56) is included through existing integration commit `6512d4935b3dad7e67ced53d1e51ab4b4f8fa9a5`; its original bridge thread `thread_20261026_071845_d58f` is completed, with caller fresh 22-case, Runtime395, Domain506 and original behavioral-probe acceptance recorded there. Same-batch residual cheat work `ba74567` is already published in PR19 and retained. Unaccepted P4.1 addendum work is excluded.

Integration merge `379be5f` combines those changes with accepted AI delivery without conflict. All six AI delivery files equal 29e11c1 blobs; all five N-2 files equal 2760c6e blobs. Only this caller acceptance record is added beyond the accepted code/evidence. The fixture's TableActor source pin remains a historical generation pin at 85134e9; N-2 changes TableActor later. No fixture regeneration or claim that all historical pins equal the final integration head is made.

Evidence root I: `D:/majiang-wt/codex-b6-batch-final-20261026/artifacts/TEMP/codex-batch-review-1792982027213`. Process-local TEMP/TMP points to I.

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental --nologo
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental --nologo
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -result-xml <I>/domain.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -result-xml <I>/runtime.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests -result-xml <I>/ai-discard.xml
```

Both builds exit 0 with zero warnings/errors. Actual runner/XML results: **Domain 517/517, TableRuntime 397/397 (including all 22 N2HostSettlementTests), AI-discard 6/6**; native exits 0, errors/failed/skipped/not-run all 0. The initial Runtime invocation found no DLL because that project is outside Mahjong.sln; its log is preserved as runtime-before-project-build.log. The explicit project build and valid nonempty run above replace that invocation; no old DLL or zero-test result is used.

| Evidence | SHA256 |
|---|---|
| I/build.log | 81A843AE78117C0CC9ABD452F2EECB28534F5A2567773996B09FCD220A989E14 |
| I/runtime-build.log | C85082E978E0718369137EACAF98B41BF0F4CA32D76CD199DD43426DAE08FCFD |
| I/domain.xml | C60892CBC59983C77CA820BDD843E12E53B08D503A5125F166C91C89D71B00D7 |
| I/runtime.xml | F6B2BFF8932DE8077F45A8A3C8AE6C946E4B2E838241C03D9BF37DB3A7BF7EF3 |
| I/ai-discard.xml | E3EBEF5CE0A5FDA29784A52392AA56288310CE0FD4D078D9C97050C4428E4951 |

The shared ledger remains **411 bound / 175 classified / 6 pending**, classified still uncovered. N-2 legacy raw entry without a settlement port, ScoreSettled ownership, authenticated ippatsu/furiten host adapters and P4/P5 composition remain within their recorded boundaries. No full solution test suite, new integrated Gradle run, PG/WSS, Unity/IL2CPP, devices, physical-network or new remote-CI result is claimed. V4 remains the sole production architecture, G1 unapproved, G5 incomplete, complete.flag absent.

Publication reuses the existing single [PR19](https://github.com/liuchangready/majiang-game/pull/19), source `feat/v5-1-integration`, base `v5`. The caller pushes once with a normal fast-forward push and records the actual final SHA through record_merge_request. No component MR or PR merge is performed.

