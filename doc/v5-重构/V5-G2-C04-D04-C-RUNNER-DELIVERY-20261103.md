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
| C-R1-06-01 | 非 isolated staging；构建污染源树 | 每次运行独占 `build/<GUID>/staging/unity-project`，复制 Assets/Packages/ProjectSettings 后替换为 fresh plugin 闭包；Unity 只读该 staging | `New-C04StagedUnityProject` 566；主流程 staging | `STAGING_EXISTS`、staging manifest 前后一致、source identity 前后一致 |
| C-R1-06-02 | 非全源码/生成输入/新鲜 DLL 闭包 | `build-shared -PluginOutputDir staging/plugins` fresh 构建，`check-shared-plugins` 验证 scratch provenance，再按 outputs 逐文件长度/SHA 校验；staging 只保留该闭包 | `Invoke-C04SharedBuild` 1072；`Get-C04PluginClosure` 644 | PLUGIN_MISSING/SIZE/HASH/UNEXPECTED/REQUIRED_MISSING；provenance schema/kind/scope/build.exitCode |
| C-R1-06-03 | BuildReport 仅总数，无 step 归因；成功但 errors=3 被当绿 | C# 成功/失败都写 report；逐 step、逐 message 记录 type/content；Error/Exception/Assert 计数；`errors != 0` 或 step error 失败关闭，原值不改 | `C04PlayerCheatBuild.Build` 79；`CaptureSteps` 197；`WriteReport` 280 | `BUILD_REPORT_ERRORS:3`、`BUILD_REPORT_STEP_ERROR`、`BUILD_REPORT_UNATTRIBUTED_ERRORS`、`BUILD_REPORT_NO_STEPS` |
| C-R1-06-04 | 无完整包身份/不可信 SkipBuild | 构建后写完整 package manifest（除 manifest/seal 外所有文件），再写 seal（sourceHead/sourceIdentity/build/package/report/plugin/provenance/player 全绑定）；SkipBuild 只接受密封包并重算全部字节 | `Write-C04PackageSeal` 801；`Test-C04PackageSeal` 859；主流程 SkipBuild | PACKAGE_SEAL_MISSING、PACKAGE_EXTRA_FILE、PACKAGE_HASH_MISMATCH、PACKAGE_REPORT:*、PACKAGE_SEAL_SOURCE_HEAD/BUILD_COMMIT/SOURCE_IDENTITY_MISMATCH |
| C-R1-06-05 | 自有 PID/start/后代 writer/timeout/cancel/drain 不信 | `Start-C04BoundedProcess` 用 PID+StartTime 精确实例；并发双流 CopyTo；有限 timeout；Ctrl+C/CancelFile；根退出后仍持管道则仅清理该根的后代；不 taskkill /T、不误杀 sentinel | `Start-C04BoundedProcess` 317；`Stop-C04ExactProcess`/`Stop-C04OwnedDescendants` | 真实进程矩阵：nonzero 7、timeout、双流 500KB×2、descendant writer、cancel、foreign sentinel 存活 |
| C-R1-06-06 | quoted 参数被空格撕裂 | `ConvertTo-C04QuotedArgument` 按 Windows CommandLineToArgvW 规则转义反斜杠/引号/尾反斜杠；所有外部调用使用单一参数串 | 同函数 184 | `C:\a b\c`、`a"b`、尾反斜杠、真实带空格路径参数 byte-exact |
| C-R1-06-07 | 父进程退出即清 writer；封存后仍被写 | 根退出后先等双流完成；未完成则识别/停止 owned descendants 再等；evidence manifest 后写 seal，之后只读校验，额外文件失败 | `Start-C04BoundedProcess`；`Write-C04EvidenceSeal` 931；`Test-C04EvidenceSeal` 969 | `descendantWriter/drainComplete/ownedDescendantsStopped`；`EVIDENCE_EXTRA`、`EVIDENCE_HASH`、`EVIDENCE_MISSING`、`EVIDENCE_SEAL_MANIFEST_HASH` |
| C-R1-06-08 | 旧 Strict 冒充正式语义；A 未集成仍可 PASS | runner 只做结构/包/进程；正式 case 语义只调用固定 CLI `dotnet <Verifier2.dll> verify --evidence ... --case ... --source-head ... --package-manifest ...`；verifier 缺失时 `BLOCKED/A_VERIFIER_NOT_INTEGRATED`，不运行 Player、不回落旧 Strict | `Find-C04Verifier` 1010；`Get-C04VerifierArguments` 1018；`Invoke-C04Verifier` 1030；主流程 | 固定 CLI 参数逐项测试；`Assert-C04EvidenceStrict`/`Assert-C04Evidence` 不存在；verifier native 非零传播 |
| C-R1-06-09 | 失败无稳定封存/退出分类 | 失败也写 summary/summary.txt/manifest/seal；退出码区分 0 PASS、1 evidence/case FAIL、2 usage/precondition/verifier BLOCKED、3 build、4 timeout、5 cancel、6 mutex busy、7 unknown | 主流程 catch/finally；`Get-C04ExitCodeForFailure` 1060 | overwrite/outside/no-head exit 2；timeout 4；cancel 5；mutex 6；BuildReport 3 |

## 4. 机制负例矩阵（真实生产函数；不是 GAME）

实际命令（Windows PowerShell 5.1）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheatRunner.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheats.Tests.ps1
```

当前结果（组件层）：

- `C04PlayerCheatRunner.Tests`：**passed=57 failed=0**，native exit 0。
- `C04PlayerCheats.Tests`：**passed=20 failed=0**，native exit 0。
- 合计 **77 项**真实 PowerShell/.NET 进程与纯函数负例；无 skip、空测试、旧 DLL 或刷新摘要。

覆盖分组：

1. Windows 参数 quoting 5 项（空格/空串/内嵌引号/尾反斜杠）。
2. 自有进程 6 组：成功、native 7、500KB×2 双流 drain、带空格路径 byte-exact、1s timeout、cancel。
3. descendant writer + foreign sentinel：父退出后仍持管道的后代被精确停止，非后代 sentinel 存活。
4. evidence seal 5 项：正常、封存后新增文件、内容变化、缺失、manifest 篡改。
5. BuildReport 4 组：合法、errors=3+step Error/Exception 原值保留并归因、errors=3 无归因拒绝、Failed。
6. plugin closure 4 项：合法、hash、missing、unexpected。
7. package seal 6 项：合法、extra、player hash、错 source、错 identity、错 buildCommit、缺 seal。
8. source identity 4 项：稳定、内容变化、docs-only commit 不改变 build 身份。
9. 无旧 Strict fallback、case 结构、A 固定 CLI 参数。
10. 证据根 overwrite/outside、缺 SourceHead、SkipBuild 无 seal 的 runner 真实退出码 2。

最新测试日志与 SHA 见第 7 节。

## 5. 受控 fresh build（实际执行结果）

本节在 fresh build 完成后由同一交付报告追加；未执行前明确为 `NOT_RUN`，不得用历史 Player 冒充。

- 构建命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-cheats.ps1 -Case build -EvidenceRoot build/<GUID> -SourceHead <HEAD> -BuildCommit <HEAD>`
- 共享锁：`Local\MahjongV5P45UnityValidation`，最多 600s，30s 心跳；超时为 `MUTEX_TIMEOUT`/exit 6，不绕锁。
- staging：`build/<GUID>/staging/unity-project`；fresh plugin：`build/<GUID>/staging/plugins`。
- 历史 errors=3：只读保留；新 report 必须逐 step 归因，errors 非零即失败关闭，禁止改计数或过滤错误。
- 状态：`NOT_RUN`（等待共享 Unity 锁/执行）。

## 6. 边界与依赖

- 本组件通过不等于 R1-06 正式游戏通过；R1-06 最终同包 Player/PlayMode 由 D04-B 取得。
- R1-04 语义仍由 D04-A `C04PlayerCheatVerifier2` 负责；runner 只调用其固定 CLI，不复制第二套语义。
- 未执行 Player authorized/negative；未运行原已知坏 Player 去刷绿；未声明 C04-G3-01..06、119/109/99、
  SECOND/SWAP 精确归属、136 闭环或两独立真实拒绝已通过。
- 未 push/PR/merge/deploy/approve/scheduler；未改共享生产源码、asmdef 或机器许可。

## 7. 实际证据索引

| 证据 | 路径 | 说明 |
|---|---|---|
| runner 测试日志 | `build/c04-tests-<GUID>/runner-tests.log` | 57/57，native 0 |
| compatibility 测试日志 | `build/c04-tests-<GUID>/cheats-tests.log` | 20/20，native 0 |
| C# 编译检查 | `%TEMP%/c04cs-<GUID>` | 临时 netstandard2.1 + UnityEditor/UnityEngine 引用，0 warning/0 error |
| fresh build | 待执行 | `build/<GUID>/summary.json`、`summary.txt`、`manifest.csv`、`seal.json` |
| 历史 errors=3 | 旧树只读 | `player-build.json` / `player-build2.json` |

## 8. 结论

D04-C 工具组件面：**实现完成，机制测试 77/77，等待受控 fresh build 与独立审查**。
R1-06 最终同包游戏证据：**NOT_RUN / 仍开放**。
全 D04：**0 BLOCKER / 3 原 MAJOR 不变，未通过**。
