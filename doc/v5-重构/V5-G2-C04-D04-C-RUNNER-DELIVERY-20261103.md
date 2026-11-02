# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 3）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness，使用其配置默认模型。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；分支
`wt/v5-g2-c04-d04-runner-ds-20261103`。独立复核报告：
`build/caller-c04c-r2-9a46ba54aea14c11894ad9bb2531b8d8/REVIEW.md`
（SHA-256 `696C3F67ADC716413C69E4C6E52E85CC41192874F9E40E2AEA0E0FEB1878F751`），已全文读取；
probes.ps1/log/json 与 REVIEW-HASHES 已复核。

本轮只处理 C/R1-06 四组；不是 A/B 依赖，不把组件结果升格为 C04-G3/GAME 通过。旧 R8/R1/R2
证据、历史 errors=3 与 errors=6 原件只读保留，未删/未改计数。

## 1. 范围

tracked 修改仍限于六个授权路径；本轮实际改动：

| 文件 | 状态 |
|---|---|
| `tools/unity/run-p45-c04-player-cheats.ps1` | 返修 |
| `tools/tests/C04PlayerCheatRunner.Tests.ps1` | 返修 |
| `tools/tests/C04PlayerCheats.Tests.ps1` | 保留 |
| `unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs` | 严格门禁 + URP import 因果尝试 |
| `unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs.meta` | 未改 |
| `doc/v5-重构/V5-G2-C04-D04-C-RUNNER-DELIVERY-20261103.md` | 本报告 |

未改共享生产源码/asmdef/机器许可/旧证据；未 push/PR/merge/deploy/approve/另派。

## 2. 逐 ID 修复映射

### C-R1-06-R1-01 — 进程清理 peer 误杀与遍历（MAJOR）

- **移除** `Stop-C04OwnedCommandMatches` 及所有 CommandLine/path/time-window 所有权推断；不再因
  相同参数或相近启动时间终止任何兄弟进程。
- 根进程改为 **signal-gated wrapper**：先启动 wrapper，创建 Job 并把 wrapper 加入 Job，之后才
  写 signal 让 wrapper 启动目标；目标在 Job 内创建，消除“启动后才 assign”的逃逸窗口。
  wrapper 用 `ProcessStartInfo` 等待目标退出并传播 exit code。
- Job 不可用/创建失败/assign 失败一律 `OWNERSHIP_UNKNOWN` 失败关闭；assign 失败关闭旧 handle；
  `QueryInformationJobObject` 失败返回 -1，StopJob 将其视为 unknown 而非 0。
- 修正 `Get-C04OwnedProcessSet`：不再在入队前设置 `seen`，出队才标记，root→child→grandchild
  正确返回（probe 900000/1/2 => 2）。
- 所有调用方继续门控 drain/cleanup/control；foreign sentinel 不再被停止。

验证：review probes `peerSentinel.sentinelAlive=true`、`ownedStopped=0`；
`descendantTraversal actualCount=2`；真实 detached writer `job active_before=2→0`，drain true。
本任务测试 51 项包含 exact identity、timeout/cancel/native/foreign sentinel、big dual-stream、
quoted path。

### C-R1-06-R1-02 — 输入冻结/转换验证（MAJOR）

- 复制源闭包仍由 `Get-C04SourceInputRows` 枚举实际复制文件（含 tests/ignored Unity inputs）。
- 新增 **copy→warmup** 与 **warmup→build** 两个边界快照：
  `staged-project-pre-warmup.csv`、`staged-project-input-manifest.csv`（warmup 后冻结）、
  `staged-project-final-manifest.csv`（build 后）；并在 build 后校验整个 staged代码闭包
  `staged-code-manifest.csv`（dotnet/tests/tools/root，排除生成的 bin/obj 与 Unity 子树）。
- `Split-C04StagingTransitions` 不再只按路径豁免：
  - changed `ProjectSettings.asset` / Font SDF 必须满足 **before 非空行全部保留在 after** 的
    additive YAML 断言，记录 before/after SHA；任意替换/删除原行即 fatal。
  - removed `PerformanceTestRunInfo/Settings` 必须 before 存在、after 缺失。
  - extra 仅允许内容校验通过的 Unity 生成文件：`.csproj` 含 `<Project`+`<TargetFramework`、
    `.slnx` 为 Solution XML、`.vscode/*.json` 含对应键、`*.pdb.meta` 为 fileFormatVersion 2+guid。
  - 其他任意 asset/scene/config/package/importer/code 变化 fatal。
- 无 BeforeRoot/AfterRoot 的调用（review probe 的路径-only fixture）现在得到 fatal，不再产生
  空 fatal 的假豁免。
- 最终 final manifest、code manifest、source manifest 均作为 package receipt 的必存在文件并
  重算，不是闲置 hash 标签。

验证：review probe `inputMutation.fatal` 非空、`allowedCount=0`；本任务 fixture 的任意
ProjectSettings 替换被拒绝，additive warmup/build transitions 在真实 fresh build 通过。

### C-R1-06-R1-03 — SkipBuild/路径/失败封存（MAJOR）

- `Write-C04PackageSeal` 现在必须有 `BuildEvidenceRoot`（必须在 repo `build/` 下），且以下真实
  文件存在并参与 receipt/seal：
  `source-input-manifest.csv`、`staging/staged-project-input-manifest.csv`、
  `staging/staged-project-final-manifest.csv`、`staging/plugin-closure.csv`、
  `build/player/c04-build-report.json`、`staging/repo/.../provenance.json`。
- `Test-C04PackageSeal` 重算 source manifest 与当前 `Get-C04SourceInputRows` 的逐行
  path/bytes/SHA；重算 staged/final manifest、plugin closure set、report；要求 package player
  与 fresh evidence `build/player/<playerRelativePath>` 字节一致；要求 provenance 每个 input
  在 `staging/repo` 下存在且长度/SHA 匹配，每个 output 在 staged plugin 目录存在且 SHA 匹配；
  BuildReport plugins 非空且与 closure 精确一致；player 必须 PE/MZ 且 >=64KB。
- 自报 `buildEvidenceRelativePath`、receipt、seal 任一关系不一致即失败；合成 powershell.exe +
  虚构 `fixture.cs`/空 outputs 的包不能通过（provenance input/output 实际字节校验失败）。
- 路径：`Assert-C04PathSafe` 检查所有已存在祖先 reparse；package member 必须相对、无 `..`、
  不越界；file reparse 拒绝。
- 失败封存：`partial-artifacts.csv` 覆盖 `build/player` 与 `staging/repo`（排除
  Library/Temp/Logs/obj/bin/UserSettings），seal 绑定其 SHA；finalize 失败删除 PASS seal、
  重写 FAIL，仍失败则删除 seal 并写 `FINALIZE_FAILED.txt`。

验证：review probes `syntheticGate.ok=false`（evidence input/output missing）、
`syntheticMain.exit=2/status=FAIL`；`seal-path-probes` partial tamper 检测、junction exit2/
followedReparse=false；本任务正例 fixture 仍可 PASS（有完整 evidence）。

### C-R1-06-R1-04 — BuildReport 严格与 URP 原因（MAJOR，构建仍未通过）

- 保留 R2 已关闭的严格门禁：errors 非 0、step Error/Exception/Assert、summary-only 99、
  one-known+unattributed、missing bytes/hash、非空 exception 全部拒绝；allowlist 参数仅兼容、
  不参与放行。
- 真实 fresh build 仍 `Succeeded/errors=6/stepErrors=6/native1`；runner exit3、无 package seal。
- 六文件内已做因果尝试：`C04PlayerCheatBuild.RepairPackageImportOrder()` 在 BuildPlayer 前对
  `AutodeskInteractive.shadergraph` 与 `TraceVirtualOffset.urtshader` 执行
  `AssetDatabase.ImportAsset(...ForceUpdate|ForceSynchronousImport)` + `AssetDatabase.Refresh`；
  同一 6 条错误仍存在（evidence `r3-evidence-330...`），证明不是简单 import 顺序/缓存。
- 具体外部范围 proposal（未应用，仅新 ignored build）：
  `build/r3-proposal-cfe026ce686d4e2388640ba25590eba1/PROPOSAL.md`、
  `manifest.patch`、`packages-lock.notes.txt`。内容：把 URP/core 从 17.3.0 升到 17.4.0
  （manifest.patch 为精确 diff；UPM 重生成 lock），或对 URP
  `RenderPipelineGlobalSettingsUtils` 的 null-resource reload 路径打等价包补丁。原因：失败资源
  和 importer 位于 `Packages/com.unity.render-pipelines.*`，不在六个授权文件内；
  `ProjectSettings/GraphicsSettings`/URP asset 不含这两条路径。
- 未改 tracked 包/设置，未用计数过滤/放宽断言。真实 errors0 完整包、可信 SkipBuild 正例仍
  **BLOCKED**，等待上述具体外部授权。

## 3. 实际验证（本轮）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheatRunner.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheats.Tests.ps1
```

- Runner：**62/62, native 0**；log
  `build/r3-tests-3c619bc7d07a4673b0ff5f2cb3246e90/runner.log`，SHA
  `81D480E1F2E619F72C277971E28EB6662E7C0886994540C57A800ADAF193EF5E`。
- Compatibility：**20/20, native 0**；log 同目录 `cheats.log`，SHA
  `15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`。
- Review probes（新复制根，native0）：`peerSentinelAlive=true/ownedStopped=0`；
  `descendantTraversal=2/2`；`syntheticGate.ok=false`；`syntheticMain=exit2/FAIL`；
  `inputMutation.fatal非空/allowedCount=0`；`strictReport.ok=false`。

真实 fresh build（build code `a0c10b14ae70c75869e8f782bbd7ee3f69c720ea`; final test commit `a9bccdf3e416706f8d78478b376dc7fe8f641825`）：
`build/r3-evidence-330dfc18ad104ab29d4746dabc0137e8/`；`summary.json` SHA
`4BF739AF2396C90EA8A3B2BE26A3517AF2A8E0BD8A5AB51885884D009FB0A527`，`seal.json` SHA
`C74FEC9CB4AE9133620F5D3788EEDC8DFD06D9575A033018D86594966FEB7A59`，BuildReport SHA
`403F0ACC03BC688973F2567F387D12A86772EB684C11C12586BE1538F6028EB2`，partial manifest SHA
`427A71A10FC4DD327A78914AC1B84D1B3816F7C743ED2B8AA03AC411B3D92F5B`（1091 files）。
runner native **3**；status FAIL；sourceStable=true；无 package seal/manifest；真实 warmup/build
transition 仅 6 个精确允许项，`fatal=[]`，`staged-project-final-manifest.csv` 存在且入证据清单。

## 4. 结论

- R1-01 进程 peer 误杀/遍历/Job 逃逸：**已修并实测**。
- R1-02 输入冻结/转换/全闭包绑定：**已修并实测**；任意 ProjectSettings 替换 fatal。
- R1-03 可信 SkipBuild/路径/失败封存：**已实现并拒绝合成包**；真实干净包正例仍缺。
- R1-04 严格门禁：**保留**；真实构建 errors=6 根因未能在六文件内修复，已提交具体外部 proposal；
  **无组件构建 PASS、无 package/SkipBuild 正例、无 approve/MR**。

A/B 未实现、未运行 Player/PlayMode；全 D04 仍 0 BLOCKER / 3 原 MAJOR，未通过。

## 5. 提交与清洁

- 最终代码/测试提交：`a9bccdf3e416706f8d78478b376dc7fe8f641825`；fresh build 证据来自 `a0c10b14…`（最终提交仅增加 receipt↔seal source manifest 关系检查，已由 62/62 测试覆盖，不重跑昂贵构建）。报告随后 doc-only 更新，
  HEAD 以 `git log -1` 为准）。
- `git status --porcelain` clean；`git diff --check` exit 0。
- 未 push/PR/merge/deploy/approve；未改共享生产源码/asmdef/机器许可。
