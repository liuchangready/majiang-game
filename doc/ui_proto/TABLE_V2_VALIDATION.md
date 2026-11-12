# Mah-jong wall V2 prototype validation

## Task identity and scope

- Objective: add a separately launchable V2 view with connected, perspective-aware opponent walls; retain V1 direct launch and C screenshot behavior.
- Worktree: `E:\agent-space\thread_20261112_101636_dd1e\worktree`.
- Branch: `mesh/task/thread_20261112_101636_dd1e`.
- Accepted base: delivered `ui_prot` commit `d922b24fec412e6a9f1c9fe0d4a8a4d6c1cb441e`.
- Source identity: this task's isolated worktree only. The paused `thread_20261112_092013_114a` worktree and all other task worktrees are out of scope.
- Allowed files: `V5ArtPrototype.cs`, new `MahjongWallRowGraphic.cs` and metadata, `tools/unity/run-ui-proto-table.ps1`, `ui_proto/README.md`, this card and one V2 visual comparison note.
- Excluded: old V1 page geometry, art assets, game rules/authority, unrelated tests, other worktrees, commits, pushes, merges and deployments.

## Known gaps and acceptance

- Known gap: existing V1 uses rotated sprite tiles for opponent walls; this V2 task addresses only the wall chassis and seat projection.
- Fixed scenarios: V1 direct switch `--prototype-mahjong-table`; V2 direct switch `--prototype-mahjong-table-v2`; focused C capture in either view. V2 window is 1200×690 to match the supplied 600×345 reference ratio.
- Acceptance: Unity Windows Player build succeeds with zero compiler errors and successful BuildReport; V1 and V2 switches remain distinct; a fresh V2 Player opens directly; its focused C key creates a valid PNG under that build root's `screenshots`; screenshot is visually inspected and remaining visual gaps documented. This is prototype evidence only.
- No test suites are in scope.

## Validation plan and boundaries

- Short source review: inspect both launch branches, screenshot gate and changed files before build.
- Build: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-ui-proto-windows.ps1`, fresh GUID output root, runner's existing 1800-second build timeout.
- Launch/capture: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-ui-proto-table.ps1 -RunRoot <fresh-root> -PrototypeVersion v2`; focus the Player and press C once. Then launch V1 with the same artifact and default version to confirm old direct launch is retained.
- Shared resource: `Local\MahjongV5P45UnityValidation`, wait at most 600 seconds with the runner's 30-second heartbeat. Use the existing runner's normal cancellation behavior. Do not close or terminate processes not owned by this task.
- Output: one fresh `build/ui_proto/<GUID>/` root; no preexisting root is reused or overwritten. Preserve logs and screenshot. Initial failure is kept; retry only after correcting its cause and use another fresh root.
- Final evidence: branch/base/head, build report, compiler diagnostics, artifact path, PNG size/dimensions/hash, runtime logs, visual inspection and final working-tree status.

## Results

- Base correction: bridge initialized this isolated task on `f7271b2e`, which predates the delivered table-only build. Before editing, this task's own branch was aligned to delivered `origin/ui_prot` commit `d922b24fec412e6a9f1c9fe0d4a8a4d6c1cb441e`; the paused task worktree was not opened or changed.
- Short source checks: `git diff --check` and PowerShell parser checks passed. `check-ui-proto-assets.ps1 -ProjectRoot E:/agent-space/thread_20261112_101636_dd1e/worktree` exited 0 (`UI_PROTO_ASSET_PREFLIGHT_PASS`).
- Preserved first build failure: root `build/ui_proto/08fc02e325294af4bd4663451f268b5b/`; native Unity exit 1; 0 C# compiler errors; BuildReport `Failed`, 3 errors. The pre-build guard reported missing ignored `Library/SharedPluginProvenance/provenance.json`. No screenshot or Player came from this attempt.
- Prerequisite recovery: `build-shared.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe` exited 0 (0 warnings/errors) and synchronized 19 outputs from this worktree. `check-shared-plugins.ps1 -Configuration Release -Json` returned `Ok=true`, 156 inputs, 19 outputs, 0 failures; provenance SHA-256 `A486E1FEE67B0DD90EBBFD4E64C12AC9084BCBFFE99C3F25D4003E8D9679B357`. Exact provenance JSON is copied to the successful build root.
- Successful Player: fresh root `build/ui_proto/a899b75ca4d9483195a38ef7466edbc1/`; Unity `D:\game_dev_tools\unity\Editor\Unity.exe`; native exit 0; compiler errors 0; BuildReport `Succeeded`, 0 errors, 224 warnings, 174,632,926 bytes, 85.44 seconds; 181 Player files. Independent manifest check found 181 entries and 0 missing/hash/length mismatches. Executable: `player/ui_proto.exe`.
- V2 direct launch: `run-ui-proto-table.ps1 -RunRoot build/ui_proto/a899b75ca4d9483195a38ef7466edbc1 -PrototypeVersion v2` launched PID 19728 with `--prototype-mahjong-table-v2`, 1200×690. A focused Windows C key-down/up created `screenshots/table-20261112-103226-491-0.png` (PNG decodes, 1200×690, 864,950 bytes, SHA-256 `42D14C31109C2A1D9FE20DEFA98988A86FE4362AF3402F43FD6F48B3D8A1FE4D`). Image was visually inspected; details and remaining visual gaps are in `TABLE_V2_COMPARISON.md`.
- V1 compatibility: the same launcher with its default version started PID 25688 using `--prototype-mahjong-table`, 1280×720. A focused C key-down/up created `screenshots/table-20261112-103323-469-0.png` (PNG decodes, 1280×720, 981,436 bytes, SHA-256 `2DEC37E0B6B5973A4C403F53EC320DD0C4B93DD03CB3F83D3029D7689AF6B0CE`). Visual inspection confirms the original orange-card V1 composition is separate and unchanged.
- Input note: the first generic `WScript.Shell.SendKeys` attempt created no screenshot; a confirmed foreground-window Win32 key event then created each image. The unsuccessful attempt did not alter the Player or evidence.
- Unity-generated tracked edits: Unity import/build changed 70 tracked paths from the initially clean base, including generated art import settings, global settings/performance files and plugin DLL/PDB outputs. These task-generated edits were archived in `unity-generated-changes.patch` with path list `unity-generated-paths.txt` in the successful build root, then restored path-by-path to the accepted base. No other worktree was touched.
- No unrelated test suite ran. No commit, push, merge, deployment, device or G2 acceptance is claimed.
- Final checks: `git diff --check` passed; only the four intended edits and four new source/documentation files remain dirty. Both task-owned Players were closed normally; the screenshot and package roots remain intact.
