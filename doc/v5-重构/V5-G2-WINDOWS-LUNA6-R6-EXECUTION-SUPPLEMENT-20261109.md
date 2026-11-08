# G2 Windows Luna6 — Round 6 execution supplement

This is a continuation record for the existing task, not a new review. The clean bridge start was `33173f613ac88a4b74d11cdc5126368b17f7bcf4` (parent `7f232051d6643c9a6694b437fb30032eb9de3c32`, merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). No commit, push, merge, new finding ID, or acceptance occurred. The original 32-row Binding and Census remain unchanged.

## Implemented this round

- `unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs`: before each real G2 accepted action, submit a separate invalid action through production `SessionCoordinator`; capture canonical payload/digest, request/scope, typed/raw rejection, reason, and persisted authority image. Require no receipt version/event, unchanged state/event, and unchanged 136-entity hash/count. G2-03 also requires `DoubleRiichi=true` in the projected and raw accepted event.
- `dotnet/tools/C04PlayerActionVerifier/Program.cs`: fail closed on missing or inconsistent rejection evidence for GAME-sourced Player cases. The tool fixture path is not treated as a Player pass.
- `unity/Mahjong.Unity/Assets/Scripts/App/LocalControlPlayerProbe.cs`: capture a decision created while seat 0 is AI-controlled, prove same-round re-entry state does not regress, explicitly claim control and then submit the old AI result; require `StaleControlEpoch`. The prior stale-human-decision negative remains separate.
- `tools/unity/test-p45-control-ui.ps1`: require old AI decision/epoch facts and stale-epoch rejection in the owned Player probe log.
- `dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs`: add a deterministic, legal `dealer-tsumo` fixture with all 136 physical entities assigned.
- `dotnet/tests/Mahjong.TableService.Tests/B2TerminalSettlementTests.cs` and its test project reference: create a real local `ITableChannel`/`TableSession`, confirm dealer tsumo and capture the persisted LocalHost settlement asset, then send it through authenticated WebSocket/Kestrel ingress. The service rejects the unbacked match settlement before authority mutation and then accepts a server-derived tsumo on the same channel.
- Rebuilt the complete Unity shared plugin set from 156 current inputs, 19 copied outputs. This was required because old provenance described a nonexistent `R:\unity` target and older LocalHost source hashes. The fresh provenance checker now passes. All refreshed plugin DLL/PDB changes are present in the current worktree and must be included in the later candidate/package closure.

## Actual commands and results

1. **Verifier Release build**

   `D:/game_dev_tools/dotnet/dotnet.exe build dotnet/tools/C04PlayerActionVerifier/C04PlayerActionVerifier.csproj -c Release --artifacts-path E:/g2-r6-verify2/artifacts --disable-build-servers -nodeReuse:false`

   Native exit 0, 0 warnings/errors. Follow-up `dotnet exec E:/g2-r6-verify2/artifacts/bin/C04PlayerActionVerifier/release/Mahjong.C04PlayerActionVerifier.dll selftest` exit 0; output `C04_PROTOBUF_FIXTURE_PASS valid=1 invalid=2 canonicalDigest=PASS wrongDigestRejected=1`.

2. **Unity script compile**

   `D:/game_dev_tools/unity/Editor/Unity.exe -batchmode -nographics -quit -projectPath <current worktree>/unity/Mahjong.Unity -logFile E:/g2-r6-unity-compile3-33173f61/unity-compile.log`

   Native exit 0. Log SHA-256: `DC537A61A412EC10DAF020E13BA099E2400463043FCC1E573DAB09E37AEFBFB4`. Two earlier failed compile logs are retained: `E:/g2-r6-unity-compile-33173f61/unity-compile.log` (enum mismatch and RequestId/string mismatch), and `E:/g2-r6-unity-compile2-33173f61/unity-compile.log` (corrected `RequestId.Value` access).

3. **Shared plugins**

   `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe`

   Forced clean outputs, exit 0, 34.301 seconds, 156 inputs, 19 copied files. Build log SHA-256 `5E71B5424E98D572732FB6EBFECD17D6B0912D84FDFC11AA197684C99D1C850F`; current provenance SHA-256 `D9F7D3C2550FA31BE380216D359D174E8B67C135D3B40F148EB0E7FFE571737C`. Read-only `check-shared-plugins.ps1 -ProjectRoot <worktree> -Quiet` native exit 0.

4. **Existing C04 action PlayMode tests**

   `D:/game_dev_tools/unity/Editor/Unity.exe -batchmode -projectPath <current worktree>/unity/Mahjong.Unity -runTests -testPlatform PlayMode -testFilter Mahjong.Unity.Tests.PlayMode.C04PlayerActionScenarioTests -testResults E:/g2-r6-playmode2-33173f61/C04PlayerActionScenarioTests.xml -logFile E:/g2-r6-playmode2-33173f61/playmode.unity.log`

   Native exit 0; XML reports 4 total, 4 passed, 0 failed, 0 skipped, 0 not-run. XML SHA-256 `68A07C76F57FE8674EDEDDBD0854CD923FD8DA29E297238B0A316F3D89AFB5B2`; Unity log SHA-256 `0A6D3E67FCD21AF30D71B982E60C178EF11A9764B3B55F536100682F3E1AD186`. This is the existing four legal-action PlayMode suite. It did **not** execute the newly edited ordinary Player probes or the full Census matrix.

The first PlayMode attempt (`E:/g2-r6-playmode-33173f61/playmode.unity.log`, SHA-256 `66C749CF1FC42612CCD19B276B1FEB97083D419CE60BDD41040CF742E2171D36`) was **NOT_RUN**: the shared-plugin provenance guard rejected a stale `R:\unity` target/input closure before tests and no XML was produced. After capturing our own Editor PID/process tree and the stopped log reason, a normal close request did not succeed, so `WM_QUIT` was posted to that owned Editor thread. The process exited `-1073741819`; this is neither a product test failure nor a pass. The retry after rebuilding plugins produced the valid 4/4 run above.

5. **G7-02 LocalHost asset rejection and authoritative settlement**

   `D:/game_dev_tools/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --artifacts-path E:/g2-r6-table-service/artifacts --disable-build-servers -nodeReuse:false`

   Native exit 0, 0 warnings/errors. Targeted `WireIngress_RejectsUnbackedMatchSettlement_ThenComputesTsumoFromAuthority`: 1/1 passed, 0 failed/skipped/not-run; XML SHA-256 `F86218AFCEE7B6809486EB74637D7564F9629EFCEC437713351BFF0A7D9F6788`. Full TableService suite: 204/204 passed, 0 failed/skipped/not-run; XML SHA-256 `69F401C9E840F9E965A6B3DFAFFE2712DA8D8AA191A676B6CF9E13ECD94AD49C`; test assembly SHA-256 `03DCC47B4F38894E4E48FDEB44E212D143D6A8EC73755CECA5F889CCE8C572BF`.

   The negative obtains a real 136-tile LocalHost deal, submits and confirms tsumo over `ITableChannel`, captures the persisted save, and sends it through the real service ingress. The service rejects it with `MATCH_AUTHORITY_UNAVAILABLE`; assertions verify unchanged actor/commit versions, event sequence, physical tiles, scores, ledger, cheat points, settlement events, and outbox. The paired legal tsumo contains no client score and is computed by server authority with one terminal outbox. This is service-boundary support evidence; it does not alone make the ordinary Player census row PASS or satisfy final package binding.

6. **LocalHost full suite**

   `D:/game_dev_tools/dotnet/dotnet.exe exec E:/g2-r6-localhost/artifacts/bin/Mahjong.LocalHost.Tests/release/Mahjong.LocalHost.Tests.dll -noLogo -automated -failSkips -result-xml E:/g2-r6-localhost/LocalHost-final2.xml`

   Native exit 0, 196/196 passed, 0 failed/skipped/not-run. XML SHA-256 `6312A6AC4BADA9289A5F3389AA395B63F1DEA8CAE59B3F461E9F1AF22FB26014`.

7. **Shared plugin rebuild after adding LocalHost fixture**

   `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe`

   Native exit 0, 34.38 seconds, 156 inputs and 19 outputs. Build log SHA-256 `CD4A48911028549875E82D7E17AF3C01DE872FE7307FD24DC68432840353CD75`; provenance SHA-256 `38884F8D5ADECBD0FE89C2FDB8831E5CE16860A2DAB71A7CA4D0759B0B747644`. Read-only provenance check exited 0.

8. **Unity compile attempt after plugin refresh**

   `D:/game_dev_tools/unity/Editor/Unity.exe -batchmode -nographics -quit -projectPath <current worktree>/unity/Mahjong.Unity -logFile E:/g2-r6-unity-compile4-33173f61/unity-compile.log`

   Native exit 1 before compilation completed. The log records Win32 error 112 / “No space left on device” while Unity wrote `Library/Bee` and `Library/Bee/fullprofile.json`; it contains no C# compiler diagnostic. Preserved at `E:/g2-r6-unity-compile4-33173f61/unity-compile.log`, SHA-256 `F88A15B83BDF4D1F3C7138891B2DCBC9ACFA5A539951C730596D1C1BEE73BEBA`. No cache or user data was deleted to force a green result. The earlier successful Unity compile applies to Unity probe edits; the later source change was confined to the .NET LocalHost scenario and was compiled/exercised by the passing .NET suites.

## Complete known findings (no new IDs)

### D-1-02 — MAJOR — G1/G2/G4/G7-02 real Player and service coverage

- **Location:** Census/Binding G1-01/02/03, G2-01..04, G4-01..03, G7-02; `C04PlayerActionProbe`, `LocalControlPlayerProbe`, `LocalFullMatchPlayerDriver`, and service ingress tests.
- **Reproduction:** execute `tools/unity/run-p45-c04-player-actions.ps1` on a clean bridge candidate and `tools/unity/test-p45-control-ui.ps1` with its fresh Player; inspect original case evidence. For G7-02 inspect real service request/receipt and before/after actor/store state.
- **Actual vs expected:** new probes compile and existing action PlayMode is 4/4, but the new ordinary Player negatives/control chain have not been executed. G1 full Player win/draw/dealer continuation/new-round binding; G2 chankan/rinshan/full negative matrix; and G4 old AI result in Player are still missing. G7-02 now has the real LocalHost asset rejection and server-derived positive through service ingress, but remains supporting service evidence until bound to the final candidate/package and Census row. Expected: all original Census assertions with real Player/service evidence.
- **Evidence:** `E:/g2-r6-table-service/G7LocalAsset.xml`, `E:/g2-r6-table-service/TableService.xml`, and `E:/g2-r6-localhost/LocalHost-final2.xml`; these are service/LocalHost support, not ordinary Player proof. Original Binding remains unchanged.
- **Repair:** implement remaining deterministic real-channel scenarios, including actual LOCAL settlement provenance, then run on the next legally frozen candidate. No mock, direct terminal mutation, or component PASS substitutes.
- **Acceptance:** real Player/PlayMode/service positives and negatives for every listed case, non-zero counts and zero skips, request/state/receipt originals, and preserved 136-tile, epoch, deadline, terminal, privacy, and idempotency assertions.

### D-1-01 — MAJOR — G8 full layouts and physical input

- **Location:** C04-G8-01/02 ordinary Windows Player display/input.
- **Reproduction:** use the verified DPI-aware R2 capture against the final common Player at base/wide/tall supported client sizes and physically drive menu -> room -> game -> settlement/dialogs.
- **Actual vs expected:** only the prior complete base Menu is verified; no full table/settlement frames or physical input evidence exists. Expected: original layout and input assertions.
- **Evidence:** `build/review-r2-a198409d/ui2/client-dpi-aware.png` and `ui2/player.log`. This round's compile and PlayMode XML are not G8 evidence. The withdrawn crop diagnosis remains withdrawn.
- **Repair:** one DPI-aware context for measurement, screen conversion, capture and input; absolute Player log, exclusive smoke data, and input-to-request-to-authoritative-receipt binding.
- **Acceptance:** visually complete base/wide/tall menu/table/settlement frames and real physical input through the flow; at most one valid request per input and zero invalid commits from cancel/focus/old input, all bound to the final package.

### D-1-05 — MAJOR — final candidate/common package closure

- **Location:** candidate/packages/runs, all 32 Windows rows, eight shared DLLs, complete resources/plugins and BuildReport.
- **Reproduction:** inspect current worktree diff and existing suite package manifests; no common ordinary Player was built in this round.
- **Actual vs expected:** plugin closure was refreshed, but there is no new bridge-frozen candidate, common final Player or 32-row package binding. Expected: one candidate and one package closure for all required rows.
- **Evidence:** current shared-plugin provenance and this supplement; old suite packages are per-suite support only.
- **Repair:** complete feasible scenarios/UI probes, then obtain a clean candidate through bridge lifecycle and use the original fresh/clean guards with one common Player collection path. Bind all eight DLLs, package files, assets/plugins, BuildReport and row originals.
- **Acceptance:** every Windows row binds the same candidate and identical whole-package hashes plus request/state/receipt/screenshot artifacts; no package splicing, old binaries, skips, or refreshed summaries. Android 9 rows remain NOT_RUN.

### D-1-04 — MAJOR — G7-01 real offline match

- **Location:** Census/Binding C04-G7-01.
- **Reproduction:** retain the unchanged `E:/g2-r4-offline-preflight.txt`; do not repeat absent an environment change.
- **Actual vs expected:** a safe approved offline isolation/egress observer is unavailable; no offline Player match ran. Expected: same-candidate full local match with no remote channel/egress attempts.
- **Evidence:** preflight SHA-256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`; G7-01 remains NOT_RUN.
- **Repair:** use an existing approved isolated environment only; do not alter host-wide network/firewall/security. Finish other feasible rows meanwhile.
- **Acceptance:** one run binds environment, process/package, local transport/no-egress observation, final scores/ranking/ledger and terminal state.

The Binding remains 18 PASS / 11 PARTIAL / 3 NOT_RUN (32 Windows rows). Android 9 rows remain excluded and NOT_RUN. G7-02's service-boundary assertion now has a real positive/negative integration test, but its final candidate/package binding and other census assertions remain open. The task is not accepted; there are no new review IDs this round.
