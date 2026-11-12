# Mahjong wall V2 validation and review rework

## Task identity and scope

- Worktree: `E:\\agent-space\\thread_20261112_101636_dd1e\\worktree`
- Branch: `mesh/task/thread_20261112_101636_dd1e`
- Rework base / current HEAD: `d6a5a755be18eee42861b68dd7fb7abd72ad68cd` (uncommitted changes)
- Reviewed source range: `d922b24f..d6a5a755`; this round addresses only the three findings below.
- V1 direct launch and C screenshot behavior remain available. No game rules, authority, device, G2 or release acceptance is claimed.
- The paused task `thread_20261112_092013_114a` and every other worktree are out of scope.

## Complete review findings and repair mapping

### D-1-1 — MAJOR — replaced screen-space seat patches with shared table projection

- Location: `unity/Mahjong.Unity/Assets/Scripts/UI/Prototype/MahjongWallRowGraphic.cs` (`OnPopulateMesh`) and new `MahjongWallGeometry.cs` (shared chassis and projection).
- Reproduction: inspect `MahjongWallRowGraphic.OnPopulateMesh` and view the prior screenshot `build/ui_proto/a899b75ca4d9483195a38ef7466edbc1/screenshots/table-20261112-103226-491-0.png`.
- Actual before repair: north, west and east walls each used separate screen-space offsets, depths, strides and size taper. Side-row spacing did not follow a shared tile width, creating stepped edges and inconsistent row orientation.
- Expected: all seats use canonical tile dimensions and one table-local projection; each seat provides only its table location and orientation, with common-corner joins and projected near/far scale.
- Evidence: prior screenshot SHA-256 `42D14C31109C2A1D9FE20DEFA98988A86FE4362AF3402F43FD6F48B3D8A1FE4D`; repaired geometry output `build/ui_proto/geometry-check/e364c8ee9b1942faabda0e4b1e61b6a9/geometry-result.txt`; fresh V2 image listed in [comparison](TABLE_V2_COMPARISON.md).
- Repair: use the shared W/H/D/gap chassis and table-local north/west/east bases in `MahjongWallGeometry`; generate all surfaces from projected shared corners.
- Acceptance assertion: checker validates the shared tile size, all three seat bases, two exact joins, seam clearance/non-overlap, perspective scale and left/right symmetry. Output: `assertions=648`, `positiveRows=3`, `sharedCorners=2`, `negativeCases=3`.

### D-1-2 — MAJOR — corrected visible faces and eliminated zero-area triangles

- Location: `MahjongWallGeometry.cs` face construction and `MahjongWallRowGraphic.cs` mesh emission (`AddQuad` / `AddTriangle`).
- Reproduction: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -Path 'E:/agent-space/thread_20261112_101636_dd1e/review-r1-006240f2ed9f45c6b68b3dab42df5b04/mesh-review.cs'; [MeshReview]::Run()"` (review shim only; it does not establish Unity runtime validation). Run the real geometry check with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-mahjong-wall-geometry.ps1`.
- Actual before repair: shim reported north 252 triangles/56 zero-area; west and east each 198/44. Side panels could be covered by the back face, and the border vertices were collinear, so those quads had no area.
- Expected: visible top, side and back surfaces form one body; no emitted triangle has zero area; mirrored sides have correct face winding.
- Evidence: review fixture `E:/agent-space/thread_20261112_101636_dd1e/review-r1-006240f2ed9f45c6b68b3dab42df5b04/mesh-review.cs` (SHA-256 `BF6D48E4E638115B9D5AE1E96B721E38CF590CEF37AE7A2D12D8DA792A2CDC88`) and its `geometry-result.txt`; repaired checker result above; final Player screenshot and hash in the comparison note.
- Repair: surfaces now share projected chassis corners and draw in explicit shadow/side/top/back order. Remove the invalid border quads. Mesh insertion rejects every triangle with area <= 0.0001.
- Acceptance assertion: actual geometry checker reports `triangles=360 zeroArea=0 North=140:0 West=110:0 East=110:0`; it also rejects a collapsed north end-side plane. Fresh Player visual inspection confirms visible side thickness at both north ends and aligned, mirrored side walls.

### D-1-3 — MINOR — corrected screenshot ratio statement

- Location: `TABLE_V2_COMPARISON.md` and the V2 launch description in `ui_proto/README.md`.
- Reproduction: compare PNG IHDR sizes (`1200x690`, `1280x720`) with reference `600x345`.
- Actual before repair: report said both images matched the reference ratio.
- Expected: V2 matches 40:23; retained V1 is 16:9 and is not ratio-matched.
- Evidence: final PNGs and SHA-256 values in `TABLE_V2_COMPARISON.md`.
- Repair: state each version’s actual dimensions and ratio separately.
- Acceptance assertion: docs match captured dimensions; V1’s retained 16:9 size is documented as compatibility behavior, not a functional defect.

## Unified negative-case matrix

The real geometry source is compiled and its `RunSelfCheck()` is executed by the command below. The three negative cases are rejected by the checker; none is skipped or weakened.

| Negative mutation | Expected rejection | Actual result |
| --- | --- | --- |
| Perturb west seat row origin independently | Reject non-shared seat transform / asymmetric join | Rejected; counted in `negativeCases=3` |
| Change east tile width independently | Reject non-canonical chassis dimensions | Rejected; counted in `negativeCases=3` |
| Collapse the north end side plane | Reject zero-area rendered triangle | Rejected; counted in `negativeCases=3` |

Command: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-mahjong-wall-geometry.ps1`

Exit code: `0`. Exact result: `GEOMETRY_SELF_CHECK_PASS assertions=648 triangles=360 zeroArea=0 seats=3 positiveRows=3 sharedCorners=2 negativeCases=3 North=140:0 West=110:0 East=110:0`.

## Build, package, launch and screenshot evidence

- Preflight: `tools/unity/check-ui-proto-assets.ps1 -ProjectRoot E:/agent-space/thread_20261112_101636_dd1e/worktree` — PASS.
- Shared plugin prerequisite: `build-shared.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe` — exit 0; plugin guard reports `Ok=true`, 156 inputs, 19 outputs, 0 failures. It compiled projects but no test suite was run.
- First build failure retained at `build/ui_proto/40f73173d44a47a69296710e724df69c/`: missing ignored `Library/SharedPluginProvenance/provenance.json`; Unity exit 1. It was not reused or overwritten.
- Final fresh build: `build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6/summary.json` — status PASS, Unity exit 0, compiler errors 0, BuildReport `Succeeded`, 0 errors, 32 warnings, 181 files. An independent manifest pass found 181 entries, 0 missing and 0 length/hash mismatches. Executable: `player/ui_proto.exe`. Package manifest: `player.sha256.json`.
- V2 direct launch: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-ui-proto-table.ps1 -RunRoot build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6 -PrototypeVersion v2`; C capture: `screenshots/table-20261112-105853-213-0.png`, 1200x690, 866,253 bytes, SHA-256 `B2ED220FDED553F1EF5AAD325DBAD86607A864D8D04A0232100956DDE3133563`.
- V1 compatibility launch: same command without `-PrototypeVersion v2`; C capture: `screenshots/table-20261112-110031-372-0.png`, 1280x720, 981,436 bytes, SHA-256 `2DEC37E0B6B5973A4C403F53EC320DD0C4B93DD03CB3F83D3029D7689AF6B0CE`.
- Both direct views were closed normally. Final visual inspection and remaining gaps are recorded in `TABLE_V2_COMPARISON.md`.
- V2 runtime log: `build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6/v2-player.log`, SHA-256 `8A3B40610E9A0B23D3AD8DA2E03D4ABAF059949B00094EE308301AF1A4D54162`; it contains the same 648-assertion geometry self-check result.
- The earlier round-1 build and screenshots remain intact. Unity-generated tracked changes from this round are archived in the final build root under `unity-generated-changes.patch` and `unity-generated-paths.txt`, then restored path-by-path; authored changes remain in the worktree.
- No unrelated suite, commit, push, merge, deployment, device run, or G2 acceptance was performed.
