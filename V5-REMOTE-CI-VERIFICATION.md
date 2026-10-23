# V5 远端 CI 验证报告（GitHub Actions · v5-verify）

> **结论：FAIL。** 远端 `v5-verify` 在 `Run unified V5 verification` 步骤失败；461 个测试用例由回退的 direct runner 实际执行且 0 失败、0 跳过，但 **.NET 8 未被真正选中**、4/4 `dotnet test` 失败、**Unity 插件复制未执行**。因此本次远端 CI 不能记 PASS，也不能作为 Unity/平台验收证据。

- 工作树：`D:/majiang-wt/remote-ci`
- 本地分支 / HEAD：`wt/v5-remote-ci` / `a056b4508e14325001f7cd086d51f3af0a61da88`
- 基线：`a056b4508e14325001f7cd086d51f3af0a61da88`（与本地 HEAD、远端 `refs/heads/feat/v5-1-integration`、`refs/pull/19/head` 一致）
- 仓库 / PR：`liuchangready/majiang-game`，PR #19（OPEN，head `a056b45`，base `v5`；未合并、未关闭）
- 验证窗口：**2026-10-23 14:06:16–14:08:30 UTC / 2026-10-23 22:06:16–22:08:30（UTC+8）**
- 报告性质：仅本地提交；不推送报告、不建 MR、不改 workflow/脚本/实现。

## 1. 范围与约束

- 只验证 GitHub 托管 `windows-latest` runner 上的 `v5-verify`：干净 checkout、.NET restore/build、测试执行、Unity 共享依赖复制、日志上传。
- 未纳入 P1.4、WSS 适配或其他任务未提交修改；未切分支、未触碰其他工作树。
- 未修改实现、`tools/verify-v5.ps1`、`tools/unity/build-shared.ps1`、`.github/workflows/v5-verify.yml`、凭据或仓库权限。
- 未合并/关闭 PR #19；未推送报告或任何新代码。
- 原始下载日志仅放在 `artifacts/remote-ci/`（`*.log` 已被 `.gitignore` 忽略），未纳入提交。

## 2. 认证与权限

| 项 | 结果 |
|---|---|
| GitHub CLI | `gh 2.102.0` |
| 账号 | `liuchangready`（keyring 登录，token 未输出） |
| token scopes | `gist`, `read:org`, `repo`, `workflow` |
| 仓库 | `liuchangready/majiang-game`（private，default branch `main`） |
| 账号对仓库权限 | `admin=true`, `maintain=true`, `push=true`, `pull=true`, `triage=true` |
| 本轮实际写操作 | 仅一次 `workflow_dispatch` 触发 CI；未改权限、计费、secrets、分支保护 |

权限足够读取 Actions run/artifact 并触发 workflow。本轮没有创建 `ci/v5-verify-a056b45` 分支：`workflow_dispatch` 在已审查 ref 上直接可用，无需 fallback 分支。

## 3. Run 清单（exact SHA）

| Run ID | Trigger | Attempt | Head ref / SHA | 结论 | 窗口（UTC） | 说明 |
|---|---|---|---|---|---|---|
| [39966787401](https://github.com/liuchangready/majiang-game/actions/runs/39966787401) | `workflow_dispatch` | 1 | `feat/v5-1-integration` / `a056b45` | **failure** | 14:06:16–14:08:30 | **本次权威 run** |
| [39959230176](https://github.com/liuchangready/majiang-game/actions/runs/39959230176) | `push` | 1 | `feat/v5-1-integration` / `a056b45` | failure | 13:12:00–13:14:07 | 既有 push run；同根因，可复现 |
| [39959231262](https://github.com/liuchangready/majiang-game/actions/runs/39959231262) | `pull_request` | 1 | `feat/v5-1-integration` / `a056b45` | failure | 13:12:00–13:13:22 | PR #19 触发；同一失败步骤 |

没有成功 run 可复用；3 个 run 全部 failure。既有 push run 的 artifact 与本次 dispatch run 的 `summary.log`、`plugin-copy.log` 内容一致，说明失败不是单次 runner 抖动。

## 4. 本次权威 run 元数据

| 项 | 值 |
|---|---|
| Run URL | https://github.com/liuchangready/majiang-game/actions/runs/39966787401 |
| Run ID / attempt | `39966787401` / `1` |
| Workflow | `v5-verify`，ID `394054164`，path `.github/workflows/v5-verify.yml`，state `active` |
| Workflow 文件版本 | blob `f0a0bed008f26d3b57d69824883f72312208c0bc`（在 `a056b45` 上） |
| 触发来源 | `workflow_dispatch`（gh CLI；无 input） |
| Head branch / SHA | `feat/v5-1-integration` / `a056b4508e14325001f7cd086d51f3af0a61da88` |
| 创建 / 完成（UTC） | `2026-10-23T14:06:16Z` / `2026-10-23T14:08:30Z` |
| 创建 / 完成（UTC+8） | `2026-10-23 22:06:16` / `2026-10-23 22:08:30` |
| 最终结论 | **failure** |

说明：`v5-verify.yml` 不在 `main`（对 `main` 的 contents API 返回 404），但 `v5-verify` 已注册，`workflow_dispatch` 在 `feat/v5-1-integration`（PR #19 head，exact SHA）上成功创建 run；未改默认分支、未加实现 commit。

## 5. Job / Step 结果

Job：`verify`，ID `120458612473`，runner `GitHub Actions 1000000002`，labels `windows-latest`，job 窗口 `14:06:20–14:08:30 UTC`（约 2m10s）。

| # | Step | 结果 | 窗口（UTC） | 备注 |
|---|---|---|---|---|
| 1 | Set up job | success | 14:06:21–14:06:22 | runner 2.338.0 |
| 2 | Checkout | success | 14:06:22–14:06:39 | 干净 checkout，exact SHA |
| 3 | Setup .NET 8 SDK | success | 14:06:39–14:06:42 | 仅并排安装 8.0.426；未真正选中 |
| 4 | Run unified V5 verification | **failure** | 14:06:42–14:08:23 | `OVERALL: FAIL`，exit 1 |
| 5 | Upload verification logs | success | 14:08:23–14:08:25 | 11 个文件，6162 bytes |
| 9 | Post Setup .NET 8 SDK | skipped | 14:08:25 | 无 post 动作 |
| 10 | Post Checkout | success | 14:08:25–14:08:28 | 清理凭据 |
| 11 | Complete job | success | 14:08:28 | 清理 orphan dotnet/VBCSCompiler |

## 6. Runner / SDK / OS

| 项 | 值 | 证据 |
|---|---|---|
| Runner | GitHub Actions hosted，`windows-latest` | job labels / runner_name |
| OS | Microsoft Windows Server 2025，10.0.26100，Datacenter | run log `Set up job` |
| Runner image | `windows-2025-vs2026`，version `20261021.278.1` | run log |
| Runner version | `2.338.0` | run log |
| Region | `northcentralus` | run log |
| 安装的 .NET SDK | `8.0.426`（`setup-dotnet` 报告 already installed） | run log |
| 实际 `dotnet` 路径 | `C:\Program Files\dotnet\dotnet.exe` | `verify-v5` 首行 |
| **实际生效 SDK** | **.NET 10**（`dotnet test` 明确报 “on .NET 10 SDK and later”） | 4 个 `dotnetTest-*.log` |
| Shell | `C:\Program Files\PowerShell\7\pwsh.EXE` | run log |

`DOTNET_ROOT=C:\Program Files\dotnet`；该目录同时存在 8.0.426 与 10.x。`setup-dotnet` 只保证 8.0.426 安装，不会把默认 SDK 降到 8。

## 7. 干净 checkout 与构建

### 7.1 Checkout

- checkout `clean: true`，先 `Deleting the contents of 'D:\a\majiang-game\majiang-game'`，再 `git init`。
- `fetch --depth=1 origin +a056b4508e14325001f7cd086d51f3af0a61da88:refs/remotes/origin/feat/v5-1-integration`。
- `git log -1 --format=%H` 输出：`a056b4508e14325001f7cd086d51f3af0a61da88`。
- 共 checkout 1404 个文件（`Updating files: 100% (1404/1404), done`）。

### 7.2 构建

`sln-build.log`（`artifacts/remote-ci/39966787401/artifact/20261023-140643/sln-build.log`）：

- 12 个项目 restore；12 个项目实际编译输出；`Build succeeded`，`0 Warning(s)`，`0 Error(s)`，`Time Elapsed 00:00:59.55`。
- 产物覆盖当前源码：`Mahjong.Domain/Contracts/Contracts.Wire/TableRuntime/Client/LocalHost/Ai` 输出到 `bin/Release/netstandard2.1`；4 个测试工程与 `Mahjong.Outbox.CrashWorker` 输出到 `bin/Release/net8.0`。
- 干净 checkout 且构建发生在测试之前，未使用历史 `bin/obj`；构建确实覆盖 `a056b45` 的当前源码。
- **但构建由实际生效的 .NET 10 SDK 执行**，不是 workflow 名义上的 .NET 8；这是下文失败的环境根因，不能记 “.NET 8 build PASS”。

## 8. 测试执行结果

4 个 `dotnet test` 全部失败，`verify-v5.ps1` 按设计回退到同一程序集的 in-process direct runner；回退日志与计数均被记录。

| 项目 | 实际 runner | Total | Failed | Skipped | Not Run | 结果 | 日志 |
|---|---|---:|---:|---:|---:|---|---|
| Mahjong.Domain.Tests | in-process (direct) | 233 | 0 | 0 | 0 | PASS | `direct-Mahjong.Domain.Tests.log` |
| Mahjong.Contracts.Tests | in-process (direct) | 82 | 0 | 0 | 0 | PASS | `direct-Mahjong.Contracts.Tests.log` |
| Mahjong.Ai.Tests | in-process (direct) | 14 | 0 | 0 | 0 | PASS | `direct-Mahjong.Ai.Tests.log` |
| Mahjong.Client.Tests | in-process (direct) | 132 | 0 | 0 | 0 | PASS | `direct-Mahjong.Client.Tests.log` |
| **合计** |  | **461** | **0** | **0** | **0** |  |  |
| Mahjong.Outbox.CrashWorker | 仅构建（helper） | — | — | — | — | PASS | `sln-build.log` |

关键事实：

- 461 是本次远端日志逐项相加的结果（233+82+14+132），不是照抄基线的数字；与基线 461 一致。
- 无失败、无跳过、无 zero-test 工程；helper 不计用例（设计如此）。
- **`dotnet test` 路径没有通过**。4 个 `dotnetTest-*.log` 都只有同一条原始错误：

```text
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet test experience.
```

- direct runner 执行的程序集来自本次 solution build，日志显示 `Total: 233/82/14/132, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`；因此“测试本身通过”成立，但“workflow 的 `dotnet test` 路径通过”不成立。

## 9. Unity 共享依赖复制（失败）

`plugin-copy.log` 显示：`build-shared.ps1` 先成功 `dotnet build` 整个 sln（0 warning / 0 error），随后在收集 NuGet 闭包时异常：

```text
Exception: ...\tools\unity\build-shared.ps1:202
Mahjong.Domain was not restored for netstandard2.1
(no '.NETStandard,Version=v2.1' target in project.assets.json)
```

后果：

- `build-shared.ps1` 在 `New-Item $pluginDir` 之前抛出，**没有生成 `plugins` 目录**。
- artifact 中没有插件 DLL 清单，无法核对 7 个 Mahjong 程序集 + Google.Protobuf/传递依赖闭包。
- 禁止物检查（xunit/MTP/AppInsights/Grpc/CrashWorker/测试程序集）**未运行**。
- 已跟踪的 Unity `Assets/Plugins/Mahjong` 未被触碰（脚本使用 scratch 目录，且未执行到复制阶段）。

因此，“Unity 共享依赖复制”状态是 **FAIL**；“插件闭包与禁止物证据”状态是 **BLOCKED/未验证**，不能因为 job 里 build 成功就记 PASS。

## 10. Artifact 核对

| 项 | 值 |
|---|---|
| Artifact 名称 | `v5-verify-logs` |
| Artifact ID | `12507955228` |
| 大小 | `6162` bytes |
| 文件数 | `11` |
| 上传时间（UTC） | `2026-10-23T14:08:25Z` |
| 过期时间（UTC） | `2027-01-21T14:06:18Z` |
| 上传 zip SHA-256（run log 记录） | `47d2ea81700841d4893c49f1e3d854a1e2ebdb5393e3ef661ecc4ed13df65acb` |
| 下载根目录 | `D:/majiang-wt/remote-ci/artifacts/remote-ci/39966787401/artifact/20261023-140643` |
| 完整 run log | `D:/majiang-wt/remote-ci/artifacts/remote-ci/39966787401/run.log` |

Artifact 内 11 个文件：

- `summary.log`
- `sln-build.log`
- `plugin-copy.log`
- `dotnetTest-Mahjong.{Domain,Contracts,Ai,Client}.Tests.log`（4 个，均为 .NET 10/MTP 错误）
- `direct-Mahjong.{Domain,Contracts,Ai,Client}.Tests.log`（4 个，均为 461 计数来源）

核对结论：

- **summary 存在**，且 `OVERALL: FAIL (project rows: 5 PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN)`。
- **每工程测试日志存在**，4 个测试工程计数与 summary 一致。
- **构建证据是 solution 级**：`sln-build.log` 逐行列出 12 个工程的 restore/compile；没有单独的 `build-<project>.log`。这符合脚本设计（全部工程在 sln 中时复用 sln build 日志），但如果审查要求“每工程独立构建日志”，这是证据粒度缺口，不是本次失败原因。
- **plugin-copy 证据只有异常日志**，没有插件文件清单/禁止物扫描结果；这是最重要的 artifact 缺口。
- 日志上传步骤本身 PASS（11 文件、artifact 可下载、内容可读）。

下载与查看命令：

```powershell
gh run view 39966787401 -R liuchangready/majiang-game --log
gh run download 39966787401 -R liuchangready/majiang-game -n v5-verify-logs -D artifacts/remote-ci/39966787401/artifact
```

## 11. 首个根因、原始错误与最小修复建议

### 11.1 根因

仓库已有 SDK pin，但放错了层级：

- `dotnet/global.json` 存在（tracked，内容为 `8.0.426` + `rollForward: latestPatch`）；
- 仓库根目录**没有** `global.json`；
- `verify-v5.ps1` 和 workflow 从**仓库根目录**调用 `dotnet`，SDK 解析从 cwd 向上查找，因此看不到 `dotnet/global.json`；
- runner 的 `C:\Program Files\dotnet` 同时有 8.0.426 和 10.x，未命中 global.json 时默认选最高 SDK → **.NET 10**。

由此产生两个表现，根因相同：

1. **`dotnet test` 4/4 失败**（首个非致命表现）：MTP 在 .NET 10 SDK 上不再支持 VSTest target；脚本回退 direct runner，测试仍然执行并全绿。
2. **Unity 插件复制失败**（首个致命表现）：.NET 10 的 restore 产出的 `project.assets.json` 目标键不再是脚本硬编码的 `.NETStandard,Version=v2.1`，`build-shared.ps1:199-202` 直接 throw；`plugins` 目录未生成，闭包/禁止物检查未执行。

原始错误（逐字）：

```text
Mahjong.Domain was not restored for netstandard2.1
(no '.NETStandard,Version=v2.1' target in project.assets.json)
```

```text
Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
on .NET 10 SDK and later.
```

```text
OVERALL: FAIL  (project rows: 5 PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN)
Process completed with exit code 1.
```

### 11.2 最小修复建议（本轮未实施）

**首选（一行文件移动）**：把 `dotnet/global.json` 移到仓库根目录 `global.json`。

```powershell
git mv dotnet/global.json global.json
```

理由：root `global.json` 对从仓库根目录及 `dotnet/` 子目录发起的 `dotnet` 调用都生效；8.0.426 已在 runner 上安装；这同时修复 `dotnet test` 和 assets 目标键两个问题。移动后重新 `workflow_dispatch`，预期：

- `dotnet --version` = `8.0.426`；
- 4 个 `dotnet test` 不再报 .NET 10/MTP 错误；
- `build-shared.ps1` 能找到 `.NETStandard,Version=v2.1`；
- artifact 出现 `plugins` 清单与禁止物检查结果。

**可选加固（非替代）**：让 `build-shared.ps1` 同时接受短 TFM 键（如 `netstandard2.1`）和长键，避免未来 SDK 再次漂移时直接 throw；但这不能修复 `dotnet test` 路径，首选仍是 SDK pin 生效。

**不要**用“direct runner 461 全绿”覆盖本次 FAIL；也不要只改 workflow 的 `setup-dotnet` 版本而不放 root global.json，因为并排安装不会改变默认 SDK 选择。

## 12. 状态口径

| 项 | 状态 | 说明 |
|---|---|---|
| 远端 `v5-verify` run | **FAIL** | run 39966787401，conclusion failure |
| 认证 / 权限 | PASS | 账号、scopes、repo 权限足够；未改权限 |
| workflow 注册 / 触发 | PASS | `workflow_dispatch` 成功；exact SHA |
| 干净 checkout | PASS | 删除旧内容、depth=1、1404 文件、`git log -1` = exact SHA |
| .NET 8 实际生效 | **FAIL** | setup 安装了 8.0.426，但实际选 .NET 10；root 无 global.json |
| restore / solution build | PASS（构建本身） | 12 工程，0 warning / 0 error；但由 .NET 10 SDK 执行 |
| `dotnet test` 路径 | **FAIL** | 4/4 报 MTP/.NET 10 错误 |
| 测试执行（direct runner） | PASS | 461 / 0 failed / 0 skipped / 0 not-run |
| Unity 插件复制 | **FAIL** | `build-shared.ps1:202` throw；未生成 plugins 目录 |
| 插件闭包 / 禁止物 | **BLOCKED** | 复制未执行，无 DLL 清单可核 |
| 日志上传 | PASS | artifact 11 文件，6162 bytes，可下载 |
| artifact 完整性 | PARTIAL | summary/测试/构建/sln 日志齐全；缺插件清单与逐工程 build 日志 |
| Unity 加载 / IL2CPP / Android / iOS | **NOT_RUN** | 不在本 workflow 范围 |
| 真实 DB / G1 / G3 | **NOT_RUN** | 本 workflow 不覆盖 |

## 13. 缺口与边界

1. **`dotnet test` 未验证通过**：461 用例走 direct runner；不能把 direct runner 结果说成 `dotnet test` 通过。
2. **Unity 插件复制未验证**：没有 `plugins` 目录、没有 DLL 闭包、没有禁止物扫描；不能声称 Unity 共享依赖已可加载。
3. **SDK 口径不符**：名义 .NET 8，实际 .NET 10；`.NET 8 restore/build/test` 这一项未真正完成。
4. **逐工程构建日志粒度**：只有 solution 级 `sln-build.log`；脚本设计如此，但若审查要求逐工程独立日志，需要调整脚本/artifact。
5. **远端 .NET CI 不代表 Unity 加载、IL2CPP、Android/iOS、真实 DB、G1/G3 通过**；这些仍是 NOT_RUN/BLOCKED，需独立环境验收。
6. 本轮未纳入 P1.4、WSS 适配或其他任务未提交修改；未改实现/脚本/workflow/凭据/权限；未合并或关闭 PR #19。

## 14. 复现与补验命令

```powershell
# 查看本次 run 的 job/step 与原始日志
gh run view 39966787401 -R liuchangready/majiang-game
gh run view 39966787401 -R liuchangready/majiang-game --log

# 下载并核对 artifact
gh run download 39966787401 -R liuchangready/majiang-game -n v5-verify-logs `
  -D artifacts/remote-ci/39966787401/artifact

# 修复后重跑（在 root global.json 生效、审查通过后）
gh workflow run v5-verify -R liuchangready/majiang-game --ref feat/v5-1-integration

# 修复后在 runner 上应确认
dotnet --version
(Get-Content dotnet/src/Mahjong.Domain/obj/project.assets.json -Raw |
  ConvertFrom-Json).targets.PSObject.Properties.Name
```

## 15. 交付说明

- 本报告：`V5-REMOTE-CI-VERIFICATION.md`（仅本地提交，不推送）。
- 原始日志：`artifacts/remote-ci/39966787401/` 与 `artifacts/remote-ci/39959230176/`（ignored，未纳入提交）。
- 实际 run 结论：**FAIL**（不是 pending，不是 PASS）。
- 等待 Codex 独立复核；复核前不合并 PR、不推送报告、不创建 MR。
