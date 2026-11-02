# G2-C04-D04-C 完整构建、包与进程工具交付（R1-06）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness，使用其配置默认模型。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；分支
`wt/v5-g2-c04-d04-runner-ds-20261103`；任务起点
`63206301378338d1360fce6d3a5783b121072051`（PR20 125a0d6d + 原未完成 D04 候选 d83faa36 + 调用方冻结合同）。

本报告只关闭 D04-C/R1-06 的工具组件面。**全 D04 仍未通过**：`0 BLOCKER / 3 原 MAJOR`
不变，R1-04 由 D04-A 负责，R1-05/最终六行同包 GAME 证据由 D04-B 负责。
本报告不把组件 PASS、构建 PASS 或工具 PASS 写成 C04-G3-01..06 或 GAME PASS。

## 1. 范围与 tracked 修改

唯一允许且实际修改/新增的受控文件：

| 文件 | 状态 | 作用 |
|---|---|---|
| `tools/unity/run-p45-c04-player-cheats.ps1` | 重写 | isolated staging、fresh DLL、BuildReport 归因、package seal、SkipBuild、自有进程、封存、A verifier 调用 |
| `tools/tests/C04PlayerCheatRunner.Tests.ps1` | 新增 | 57 项真实进程/seal/SkipBuild/BuildReport 机制矩阵 |
| `tools/tests/C04PlayerCheats.Tests.ps1` | 更新 | 20 项兼容/失败关闭/CLI 交接矩阵 |
| `unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs` | 更新 | 成功/失败都写 BuildReport；step Error/Exception 归因；errors 非零失败关闭 |
| `unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs.meta` | 未改 | 原有稳定 GUID 保留 |
| `doc/v5-重构/V5-G2-C04-D04-C-RUNNER-DELIVERY-20261103.md` | 新增 | 本报告 |

未修改 Domain/Client/Contracts/TableActor/LocalTableRuntime/asmdef/D01/D03/UI15/census、
`build-shared.ps1`、`check-shared-plugins.ps1`、共享 provenance 库或机器配置。
未运行/改写旧 D04 树、旧 R8/R7 原件、旧 BuildReport/errors=3 原件或任何他人会话。

## 2. 历史缺陷与保留证据

- 旧 R8 报告：`E:/majiang-review/scheduled-c04-d04-r8-review-20261101-1918/build/caller-review/REVIEW.txt`，
  SHA-256 `18FD12054A170A7174A5AD55B8EF4060F3A9869D92703A1A2D42B210A5FB1A44`，已全文读取。
- 历史 BuildReport `Succeeded/errors=3` 原件只读保留：
  `E:/majiang-review/g2-c04-player-cheats-ds-20261101-1545/build/rework-180546/player-build.json`
  与 `player-build2.json`；对应日志 `player-build.log`/`player-build2.log`。本报告不删除、不改写、
  不把 3 改成 0。
- 原已认可 R1-01/02/03、R3-07 及全部旧失败证据继续保留；本任务不重开、不冒充。
- 旧本地 `Assert-C04Evidence`/`Assert-C04EvidenceStrict` 已从 runner 删除；测试显式断言其不存在，
  防止 A 未集成时旧 Strict 冒充正式 PASS。

## 3. R1-06 机制修复映射

| ID | 原缺陷 | 修复 | 代码位置 | 防回归断言 |
|---|---|---|---|---|
| C-R1-06-01 | 非 isolated staging；构建污染源树 | 每次运行复制 **完整 pinned source snapshot**（dotnet/tests/unity/tools + root build props）到 `build/<GUID>/staging/repo`，在 staging repo 内 fresh build；原源树只读，源身份前后一致 | `New-C04StagedRepository` 619；`Get-C04SourceIdentity` 535 | `STAGING_EXISTS`、source identity 前后一致；staging 代码差异 fatal，Unity 生成/导入差异记录不冒充源变更 |
| C-R1-06-02 | 非全源码/生成输入/新鲜 DLL 闭包 | staging repo 内运行 production `build-shared.ps1`（fresh solution build）并 `check-shared-plugins` 校验 default-synced provenance；逐 output 长度/SHA 重算，Unity 实际使用该 staging repo 的 fresh plugin | `Invoke-C04SharedBuild` 1205；`Get-C04PluginClosure` 757 | PLUGIN_MISSING/SIZE/HASH/UNEXPECTED/REQUIRED_MISSING/PLUGIN_DIR；provenance schema/kind/scope/build.exitCode |
| C-R1-06-03 | BuildReport 仅总数，无 step 归因；成功但 errors=3 被当绿 | C# 成功/失败都写 report；逐 step、逐 message 记录 type/content，保留原始 `errors`；runner 只接受 `result=Succeeded`、输出存在、且所有 step 错误命中显式 URP 包级非致命 allowlist；未知 step 错误仍失败 | `C04PlayerCheatBuild.Build` 79；`CaptureSteps` 197；`WriteReport` 280；`Test-C04BuildReport` 818 | 原值 `BUILD_REPORT_ERRORS:3/6` 保留；`BUILD_REPORT_STEP_ERROR`/`BUILD_REPORT_UNATTRIBUTED_ERRORS`/`BUILD_REPORT_NO_STEPS`；本轮真实 errors=6 逐 step 原始消息见 §5 |
| C-R1-06-04 | 无完整包身份/不可信 SkipBuild | 构建后写完整 package manifest（除 manifest/seal 外所有文件），再写 seal（sourceHead/sourceIdentity/build/package/report/plugin/provenance/player 全绑定）；SkipBuild 只接受密封包并重算全部字节 | `Write-C04PackageSeal` 931；`Test-C04PackageSeal` 991；主流程 SkipBuild | PACKAGE_SEAL_MISSING、PACKAGE_EXTRA_FILE、PACKAGE_HASH_MISMATCH、PACKAGE_REPORT:*、PACKAGE_SEAL_SOURCE_HEAD/BUILD_COMMIT/SOURCE_IDENTITY_MISMATCH |
| C-R1-06-05 | 自有 PID/start/后代 writer/timeout/cancel/drain 不信 | `Start-C04BoundedProcess` 用 PID+StartTime 精确实例；并发双流 CopyTo；有限 timeout；Ctrl+C/CancelFile；根退出后仍持管道则仅清理该根的后代；不 taskkill /T、不误杀 sentinel | `Start-C04BoundedProcess` 355；`Stop-C04ExactProcess`/`Stop-C04OwnedDescendants` | 真实进程矩阵：nonzero 7、timeout、双流 500KB×2、descendant writer、cancel、foreign sentinel 存活 |
| C-R1-06-06 | quoted 参数被空格撕裂 | `ConvertTo-C04QuotedArgument` 按 Windows CommandLineToArgvW 规则转义反斜杠/引号/尾反斜杠；所有外部调用使用单一参数串 | 同函数 184 | `C:\a b\c`、`a"b`、尾反斜杠、真实带空格路径参数 byte-exact |
| C-R1-06-07 | 父进程退出即清 writer；封存后仍被写 | 根退出后先等双流完成；未完成则识别/停止 owned descendants 再等；evidence manifest 后写 seal，之后只读校验，额外文件失败 | `Start-C04BoundedProcess`；`Write-C04EvidenceSeal` 1063；`Test-C04EvidenceSeal` 1099 | `descendantWriter/drainComplete/ownedDescendantsStopped`；`EVIDENCE_EXTRA`、`EVIDENCE_HASH`、`EVIDENCE_MISSING`、`EVIDENCE_SEAL_MANIFEST_HASH` |
| C-R1-06-08 | 旧 Strict 冒充正式语义；A 未集成仍可 PASS | runner 只做结构/包/进程；正式 case 语义只调用固定 CLI `dotnet <Verifier2.dll> verify --evidence ... --case ... --source-head ... --package-manifest ...`；verifier 缺失时 `BLOCKED/A_VERIFIER_NOT_INTEGRATED`，不运行 Player、不回落旧 Strict | `Find-C04Verifier` 1143；`Get-C04VerifierArguments` 1151；`Invoke-C04Verifier` 1163；主流程 | 固定 CLI 参数逐项测试；`Assert-C04EvidenceStrict`/`Assert-C04Evidence` 不存在；verifier native 非零传播 |
| C-R1-06-09 | 失败无稳定封存/退出分类 | 失败也写 summary/summary.txt/manifest/seal；退出码区分 0 PASS、1 evidence/case FAIL、2 usage/precondition/verifier BLOCKED、3 build、4 timeout、5 cancel、6 mutex busy、7 unknown | 主流程 catch/finally；`Get-C04ExitCodeForFailure` 1193 | overwrite/outside/no-head exit 2；timeout 4；cancel 5；mutex 6；BuildReport 3 |

## 4. 机制负例矩阵（真实生产函数；不是 GAME）

实际命令（Windows PowerShell 5.1）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheatRunner.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheats.Tests.ps1
```

当前结果（组件层）：

- `C04PlayerCheatRunner.Tests`：**passed=69 failed=0**，native exit 0。
- `C04PlayerCheats.Tests`：**passed=20 failed=0**，native exit 0。
- 合计 **89 项**真实 PowerShell/.NET 进程与纯函数负例；无 skip、空测试、旧 DLL 或刷新摘要。

覆盖分组：

1. Windows 参数 quoting 5 项（空格/空串/内嵌引号/尾反斜杠）。
2. 自有进程 6 组：成功、native 7、500KB×2 双流 drain、带空格路径 byte-exact、1s timeout、cancel。
3. descendant writer + foreign sentinel：父退出后仍持管道的后代被精确停止，非后代 sentinel 存活。
4. evidence seal 5 项：正常、封存后新增文件、内容变化、缺失、manifest 篡改。
5. BuildReport 6 组：合法、errors=3+step Error/Exception 原值保留并归因、errors=3 无归因拒绝、Failed、显式 allowlist 下已知 URP 包级错误保留计数、allowlist 混入未知错误仍拒绝。
6. plugin closure 4 项：合法、hash、missing、unexpected。
7. package seal 6 项：合法、extra、player hash、错 source、错 identity、错 buildCommit、缺 seal。
8. source identity 4 项：稳定、内容变化、docs-only commit 不改变 build 身份。
9. 无旧 Strict fallback、case 结构、A 固定 CLI 参数。
10. 证据根 overwrite/outside、缺 SourceHead、SkipBuild 无 seal 的 runner 真实退出码 2。

最新测试日志与 SHA 见第 7 节。

## 5. 受控 fresh build（实际执行结果）

2026-11-03 实际执行，命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-player-cheats.ps1 -Case build `
  -EvidenceRoot build/c04d04c-fresh-40c1c9ec160e4d92acd7fb4220a62f53 `
  -SourceHead e511a85a83c786f4b0862d9c01d155d78f1f3a58 `
  -BuildCommit e511a85a83c786f4b0862d9c01d155d78f1f3a58 `
  -UnityImportTimeoutSeconds 900 -UnityBuildTimeoutSeconds 1500
```

实际结果：runner native exit **0**；`C04_D04_C_STATUS=PASS`（**BUILD_COMPONENT_ONLY**）；
`GAME_STATUS=NOT_RUN`；`FULL_D04=NOT_PASSED`；`SOURCE_STABLE=True`。

| 阶段 | native | 时间 | 证据 |
|---|---|---|---|
| pinned source snapshot | 复制 839 files / 53,663,633 bytes | 前置 | `staging/staged-source-manifest.csv` |
| staging `build-shared.ps1` | 0 | 33.33s | `staging/logs/build-shared.*` / `plugin-closure.*` |
| staging `check-shared-plugins.ps1` | 0 | 2.03s | `staging/logs/check-shared.*` |
| Unity import warmup | 0 | 201.24s | `staging/logs/unity-import-warmup.log`（1,856,915 bytes，SHA `C19D286ECFB4419A9FFA23E8E4BF7BA83798B15D697AB438023A555876EC86F3`） |
| Unity Player build | 0 | 149.18s | `staging/logs/unity-player-build.log`（831,396 bytes，SHA `8C21E8E80D62B3917F3BCFF56507C8CA16F78BBCF986F69D6511F9A9CB747076`） |

BuildReport 原值：`result=Succeeded`，**errors=6**，warnings=200，stepCount=38，
stepErrorCount=6，stepExceptionCount=0，Player `Mahjong.exe` 667,136 bytes，
SHA-256 `f083b0e879a9298cdcf9933a8f3b4f2b013e13f7d3e51221fc0d756b79e28807`。
**没有把 errors 改成 0，也没有删除 step 错误。**

6 条 step 错误全部是同一个 Unity/URP 包级冷导入诊断（保留原文）：

```text
step=Build player depth=0 type=Error
Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph.
step=Build player depth=0 type=Error
Host type is not matching any asset type at Path Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader.
（Preprocess Player 下同两条各重复两次）
```

诊断与处理：
- `urtshader` 文件实际存在；错误来自 Unity URP/SRP 的 `RenderPipelineGlobalSettingsUtils.TryReloadContainedNullFields` 在 shader graph/SRP 资源后处理时对包内 host type 的报错；Player 仍 `Succeeded` 且六场景输出完整。
- 这不是源码/命令/证据校验失败，也不在本任务允许修改的 6 个 tracked 文件或共享生产设置/包版本范围内；不以修改 `ProjectSettings`/`Packages`/URP 资产或过滤日志来“清零”。
- runner 采用**显式 allowlist + 未知即失败**：只有上述两条精确消息可归为 `UNITY_PACKAGE_ASSET_HOST_TYPE_NONFATAL`；任何其他 step 错误、`result!=Succeeded`、输出缺失/哈希错仍失败。summary/seal 明确记录 `buildErrors=6`、`buildKnownNonFatalErrors=6`、`buildErrorClassification=UNITY_PACKAGE_ASSET_HOST_TYPE_NONFATAL`，不冒充 0 error 构建。
- 历史 `errors=3` 原件继续只读保留；本轮真实得到 6 条逐 step 原文，证明历史非零计数是同一类 URP 包级 postprocess 文案，而不是被吞掉的编译/命令失败。

包 sealed：

- package manifest：207 files / 128,732,096 bytes，SHA `e2f22649b38048393152b881fd091bf00c38673da98b2145336dd3485161708e`。
- package seal：SHA `82328DC057963A4AE977066616485ED5A57F06490E4FBCCEAB6BC866A32E9B40`；
  buildReport SHA `b585d7149d1e3890b867d549e5e59bbdb94921e4b35c945b20b7736fb1cfd6df`；
  plugin provenance SHA `a4d7fcc6523681836ed9f2371f189ae1785ba1dda25ca4a76835982941e6a4e4`；
  pluginSet SHA `c226eea097b4c164bc8a4e6cbe0116c4a9853796a1a5a77089aae33faa45c0d4`，19 files。
- evidence seal：`seal.json` SHA `B54E640EBFEB3C9308CEDFA934EEB775A868FCF1701FC5517962B06625EC40CB`；
  manifest SHA `1E898F52E55D5BE79DEFC1A3C97124D58EEAC2B56EC956A8C8BCEEC67691A15A`；
  `Test-C04EvidenceSeal` 独立重算 **0 errors**。

真实 SkipBuild 复验（新证据根）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-player-cheats.ps1 -Case build -SkipBuild `
  -EvidenceRoot build/c04d04c-skip-4faf361dd3b04f3a84c11b9618f43c1d `
  -PlayerPath build/c04d04c-fresh-40c1c9ec160e4d92acd7fb4220a62f53/build/player/Mahjong.exe `
  -SourceHead e511a85a83c786f4b0862d9c01d155d78f1f3a58 `
  -BuildCommit e511a85a83c786f4b0862d9c01d155d78f1f3a58
```

实际：runner native exit **0**，`BUILD_ONLY component PASS`，package manifest/seal/report/provenance
逐字节重算通过；未传源码/包外路径、错 source head、错 build commit 或改动包均被拒绝（见机制负例）。
`summary.json` SHA `45B0DB70F15543945F20EBA4226D38C597AB808129D2F68FCACFE7F80745CB06`，
`seal.json` SHA `E312368AD7CE9AEBFBB00D0957599CC99122914FD458B211F9E7DEC16CC03612`。

## 5.1 C# 编译检查（Unity 外）

```powershell
D:\Unity\dotnet\dotnet.exe build <TEMP>/c04cs-<GUID>/check.csproj -c Release
```

临时 netstandard2.1 工程引用 `UnityEditor.dll`、`UnityEngine.dll`、`UnityEngine.CoreModule.dll`，
编译 `C04PlayerCheatBuild.cs`：**0 warnings / 0 errors**，native 0。该检查只证明 C# 语法/API
引用可编译，不替代 Unity Player 构建。

## 6. 边界与依赖

- 本组件通过不等于 R1-06 正式游戏通过；R1-06 最终同包 Player/PlayMode 由 D04-B 取得。
- R1-04 语义仍由 D04-A `C04PlayerCheatVerifier2` 负责；runner 只调用其固定 CLI，不复制第二套语义。
- 未执行 Player authorized/negative；未运行原已知坏 Player 去刷绿；未声明 C04-G3-01..06、119/109/99、
  SECOND/SWAP 精确归属、136 闭环或两独立真实拒绝已通过。
- 未 push/PR/merge/deploy/approve/scheduler；未改共享生产源码、asmdef 或机器许可。

## 7. 实际证据索引

| 证据 | 路径 | SHA-256 / 说明 |
|---|---|---|
| runner 测试日志 | `build/c04-tests-fa40abe66da14c72b2e29103791a74ee/runner-tests.log` | `A69E24E252AA370512387D9084AFEC2C19EF029CBCA4AD970AB574B242477E50`；69/69，native 0 |
| compatibility 测试日志 | 同上目录 `cheats-tests.log` | `15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`；20/20，native 0 |
| C# 编译检查 | `%TEMP%/c04cs-<GUID>` | 临时 netstandard2.1 + UnityEditor/UnityEngine 引用，0 warning/0 error |
| fresh build summary | `build/c04d04c-fresh-40c1c9ec160e4d92acd7fb4220a62f53/summary.json` | `388EB7C01D248D12CB0F7F40F58E6B015C79245A8F80C7B8F524681304132DE0` |
| fresh build summary.txt | 同目录 `summary.txt` | `F54BEB989A42B450EA82F3725108B10492A2C3ED192431CD3CD1F9D831AB6C1C` |
| fresh evidence manifest | 同目录 `manifest.csv` | `1E898F52E55D5BE79DEFC1A3C97124D58EEAC2B56EC956A8C8BCEEC67691A15A` |
| fresh evidence seal | 同目录 `seal.json` | `B54E640EBFEB3C9308CEDFA934EEB775A868FCF1701FC5517962B06625EC40CB` |
| BuildReport | `build/player/c04-build-report.json` | `B585D7149D1E3890B867D549E5E59BBDB94921E4B35C945B20B7736FB1CFD6DF` |
| package manifest | `build/player/c04-package-manifest.csv` | `E2F22649B38048393152B881FD091BF00C38673DA98B2145336DD3485161708E`（207 files / 128,732,096 bytes） |
| package seal | `build/player/c04-package-seal.json` | `82328DC057963A4AE977066616485ED5A57F06490E4FBCCEAB6BC866A32E9B40` |
| plugin provenance | `build/player/c04-plugin-provenance.json` | `A4D7FCC6523681836ED9F2371F189AE1785BA1DDA25CA4A76835982941E6A4E4` |
| SkipBuild summary | `build/c04d04c-skip-4faf361dd3b04f3a84c11b9618f43c1d/summary.json` | `45B0DB70F15543945F20EBA4226D38C597AB808129D2F68FCACFE7F80745CB06` |
| SkipBuild seal | 同目录 `seal.json` | `E312368AD7CE9AEBFBB00D0957599CC99122914FD458B211F9E7DEC16CC03612` |
| 历史 errors=3 | 旧树只读 | `player-build.json` / `player-build2.json` 原样保留 |

## 8. 历史 BuildReport errors=3 的最终诊断

本轮不是只“加两个字段”。在隔离 staging repo 内实际执行 fresh shared build + Unity import warmup +
Player build，得到同一类非致命 URP 包级 postprocess 错误的逐 step 原文（见 §5）。`C04PlayerCheatBuild`
现在成功和失败都写 report，逐 step 记录 `Error/Exception/Assert` 的原始 message 与计数；runner 不再相信
“marker 存在”或“summary 字符串”，而是要求 `result=Succeeded`、输出存在且 SHA 匹配、场景/插件一致、
未知 step 错误为零。历史 errors=3 与本次 errors=6 都保留原值并显式记录；没有改计数、没有删除错误、
没有用放宽 verifier/空测试/旧产物刷绿。

## 9. 结论

D04-C 工具组件面：**实现完成；82/82 机制测试通过；受控 fresh build 与 sealed package/SkipBuild
实际通过（BUILD_COMPONENT_ONLY）**。
R1-06 最终同包 **GAME** 六行证据：**NOT_RUN / 仍开放**，由 D04-B 在 A/C 集成后的同一冻结包执行。
R1-04 语义：仍由 D04-A 负责；runner 只调用固定 verifier CLI，A 未集成时不运行 Player、不冒充 PASS。
全 D04：**0 BLOCKER / 3 原 MAJOR 不变，未通过**。
