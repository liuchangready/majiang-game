# V5 远端 CI 验证与第 1 轮返修报告

> **远端历史 run：FAIL（已观测）。** `v5-verify` run `39966787401` 在 `Run unified V5 verification` 步骤失败；461 个用例由回退 direct runner 执行且 0 失败，但实际生效的是 .NET 10 SDK，4/4 `dotnet test` 失败，Unity 插件复制未执行。
>
> **本地返修：PASS（已观测）。** 将 `dotnet/global.json` 移到仓库根、补充 SDK 诊断并 pin workflow 到 `8.0.426` 后，从仓库根和 `dotnet/` 子目录分别运行完整 Release 统一入口：SDK 均为 `8.0.426`，461/0/0，插件 12 DLL、禁止物 0，`OVERALL: PASS` exit 0。
>
> **远端新 SHA：NOT_RUN。** 本轮未触发新 run；未经新的远端 run 确认，**不得把远端标为 PASS**。

- 工作树：`D:/majiang-wt/remote-ci`
- 本地分支：`wt/v5-remote-ci`
- 代码基线：`a056b4508e14325001f7cd086d51f3af0a61da88`
- 上轮本地报告提交：`3e06a0a270d8374cdbeb0ad28950229a83b5b0c7`（仅本地，未推送）
- 远端 `feat/v5-1-integration` / PR #19 head：`a056b45`
- 仓库 / PR：`liuchangready/majiang-game`，PR #19（OPEN，base `v5`；未合并、未关闭）
- 原远端验证窗口：**2026-10-23 14:06:16–14:08:30 UTC / 22:06:16–22:08:30（UTC+8）**
- 本轮返修状态：**工作区修改，未提交、未推送、未触发旧 SHA 的新 CI**

## 1. 范围与约束

- 初始范围：验证 GitHub 托管 `windows-latest` 上的 `v5-verify` 干净 checkout、.NET restore/build/test、Unity 共享依赖复制、日志上传。
- 第 1 轮返修范围（经复核明确扩展）：`dotnet/global.json` 移动到根、`tools/verify-v5.ps1` 的 SDK 诊断、`.github/workflows/v5-verify.yml` 的最小配套 pin、以及本报告。
- 未改业务实现、测试源码、`tools/unity/build-shared.ps1`、manifest 或凭据；未放宽 TFM、未跳过插件复制、未忽略失败、未用 direct 回退掩盖问题。
- 未纳入 P1.4、WSS 适配或其他任务未提交修改；未切分支、未触碰其他工作树。
- 本轮按复核要求**不提交、不推送、不建/改/合并/关闭 PR、不触发旧 SHA 的重复 CI**。

## 2. 认证与权限

| 项 | 结果 |
|---|---|
| GitHub CLI | `gh 2.102.0` |
| 账号 | `liuchangready`（keyring 登录，token 未输出） |
| token scopes | `gist`, `read:org`, `repo`, `workflow` |
| 仓库 | `liuchangready/majiang-game`（private，default branch `main`） |
| 账号对仓库权限 | `admin=true`, `maintain=true`, `push=true`, `pull=true`, `triage=true` |
| 对 GitHub 的写操作 | 仅第 1 轮一次 `workflow_dispatch`；本轮未再触发任何 run |

权限足够读取 Actions run/artifact。未改权限、计费、secrets、分支保护。

## 3. 原远端 run 清单（exact SHA）

| Run ID | Trigger | Attempt | Head ref / SHA | 结论 | 窗口（UTC） | 说明 |
|---|---|---|---|---|---|---|
| [39966787401](https://github.com/liuchangready/majiang-game/actions/runs/39966787401) | `workflow_dispatch` | 1 | `feat/v5-1-integration` / `a056b45` | **failure** | 14:06:16–14:08:30 | 第 1 轮权威 run |
| [39959230176](https://github.com/liuchangready/majiang-game/actions/runs/39959230176) | `push` | 1 | `feat/v5-1-integration` / `a056b45` | failure | 13:12:00–13:14:07 | 既有 push run；同根因可复现 |
| [39959231262](https://github.com/liuchangready/majiang-game/actions/runs/39959231262) | `pull_request` | 1 | `feat/v5-1-integration` / `a056b45` | failure | 13:12:00–13:13:22 | PR #19 触发；同一失败步骤 |

没有成功 run 可复用；3 个 run 全部 failure。既有 push run 的 artifact 与 dispatch run 的 `summary.log`、`plugin-copy.log` 内容一致。

## 4. 原远端 run 元数据

| 项 | 值 |
|---|---|
| Run URL | https://github.com/liuchangready/majiang-game/actions/runs/39966787401 |
| Run ID / attempt | `39966787401` / `1` |
| Workflow | `v5-verify`，ID `394054164`，path `.github/workflows/v5-verify.yml`，state `active` |
| Workflow 文件版本（原 SHA） | blob `f0a0bed008f26d3b57d69824883f72312208c0bc` |
| 触发来源 | `workflow_dispatch`（gh CLI） |
| Head branch / SHA | `feat/v5-1-integration` / `a056b4508e14325001f7cd086d51f3af0a61da88` |
| 创建 / 完成（UTC） | `2026-10-23T14:06:16Z` / `2026-10-23T14:08:30Z` |
| 创建 / 完成（UTC+8） | `2026-10-23 22:06:16` / `2026-10-23 22:08:30` |
| 最终结论 | **failure** |

`v5-verify.yml` 不在 `main`（contents API 404），但 `workflow_dispatch` 在已审查 ref 上直接可用；第 1 轮未创建 `ci/v5-verify-a056b45` 分支。

## 5. 原远端 Job / Step

Job：`verify`，ID `120458612473`，runner `GitHub Actions 1000000002`，labels `windows-latest`，job 窗口 `14:06:20–14:08:30 UTC`（约 2m10s）。

| # | Step | 结果 | 窗口（UTC） | 备注 |
|---|---|---|---|---|
| 1 | Set up job | success | 14:06:21–14:06:22 | runner 2.338.0 |
| 2 | Checkout | success | 14:06:22–14:06:39 | 干净 checkout，exact SHA |
| 3 | Setup .NET 8 SDK | success | 14:06:39–14:06:42 | 安装了 8.0.426，但未真正选中 |
| 4 | Run unified V5 verification | **failure** | 14:06:42–14:08:23 | `OVERALL: FAIL`，exit 1 |
| 5 | Upload verification logs | success | 14:08:23–14:08:25 | 11 文件，6162 bytes |
| 9 | Post Setup .NET 8 SDK | skipped | 14:08:25 | 无 post 动作 |
| 10 | Post Checkout | success | 14:08:25–14:08:28 | 清理凭据 |
| 11 | Complete job | success | 14:08:28 | 清理 orphan dotnet/VBCSCompiler |

## 6. 原远端 Runner / SDK / OS

| 项 | 值 | 证据 |
|---|---|---|
| Runner | GitHub Actions hosted，`windows-latest` | job labels / runner_name |
| OS | Microsoft Windows Server 2025，10.0.26100，Datacenter | run log |
| Runner image | `windows-2025-vs2026`，version `20261021.278.1` | run log |
| Runner version | `2.338.0` | run log |
| Region | `northcentralus` | run log |
| 安装的 .NET SDK | `8.0.426`（`setup-dotnet` 报告 already installed） | run log |
| 实际 `dotnet` 路径 | `C:\Program Files\dotnet\dotnet.exe` | `verify-v5` 首行 |
| **实际生效 SDK** | **.NET 10**（`dotnet test` 明确报 “on .NET 10 SDK and later”） | 4 个 `dotnetTest-*.log` |
| Shell | `C:\Program Files\PowerShell\7\pwsh.EXE` | run log |

`DOTNET_ROOT=C:\Program Files\dotnet`；该目录同时存在 8.0.426 与 10.x。`setup-dotnet` 只保证 8.0.426 安装，不会把默认 SDK 降到 8；仓库根当时没有 `global.json`。

## 7. 原远端干净 checkout 与构建

- checkout `clean: true`，先删除 `D:\a\majiang-game\majiang-game` 内容，再 `git init`。
- `fetch --depth=1 origin +a056b4508e14325001f7cd086d51f3af0a61da88...`。
- `git log -1 --format=%H` 输出 exact SHA；共 1404 个文件。
- `sln-build.log`：12 工程 restore/build；`Build succeeded`，`0 Warning(s)`，`0 Error(s)`，`00:00:59.55`；产物覆盖 7 个 netstandard2.1 共享库、4 个 net8.0 测试工程和 CrashWorker。
- 干净 checkout + 构建先于测试，未使用历史 `bin/obj`；构建确实覆盖 `a056b45` 当前源码。
- 但构建由 .NET 10 SDK 执行，不是 workflow 名义上的 .NET 8。

## 8. 原远端测试执行

| 项目 | 实际 runner | Total | Failed | Skipped | Not Run | 结果 |
|---|---|---:|---:|---:|---:|---|
| Mahjong.Domain.Tests | in-process (direct) | 233 | 0 | 0 | 0 | PASS |
| Mahjong.Contracts.Tests | in-process (direct) | 82 | 0 | 0 | 0 | PASS |
| Mahjong.Ai.Tests | in-process (direct) | 14 | 0 | 0 | 0 | PASS |
| Mahjong.Client.Tests | in-process (direct) | 132 | 0 | 0 | 0 | PASS |
| **合计** |  | **461** | **0** | **0** | **0** |  |
| Mahjong.Outbox.CrashWorker | 仅构建（helper） | — | — | — | — | PASS |

- 461 来自本次远端日志逐项相加（233+82+14+132），与基线 461 一致；不是硬填。
- 无失败、无跳过、无 zero-test 工程；helper 不计用例。
- **`dotnet test` 路径没有通过**。4 个 `dotnetTest-*.log` 均为同一原始错误：

```text
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet test experience.
```

- 461 用例是 direct runner 执行的结果；不能据此说 `dotnet test` 或整个远端 workflow 通过。

## 9. 原远端 Unity 插件复制失败

`plugin-copy.log` 显示 `build-shared.ps1` 先成功 build 整个 sln（0 warning / 0 error），随后在收集 NuGet 闭包时异常：

```text
Exception: ...\tools\unity\build-shared.ps1:202
Mahjong.Domain was not restored for netstandard2.1
(no '.NETStandard,Version=v2.1' target in project.assets.json)
```

后果：未生成 `plugins` 目录；artifact 无插件 DLL 清单；闭包/禁止物检查未运行；已跟踪 Unity 资产未被写入（scratch 目录且未到复制阶段）。因此插件复制状态 **FAIL**，闭包证据 **BLOCKED**。

## 10. 原远端 Artifact

| 项 | 值 |
|---|---|
| Artifact 名称 | `v5-verify-logs` |
| Artifact ID | `12507955228` |
| 大小 | `6162` bytes |
| 文件数 | `11` |
| 上传时间（UTC） | `2026-10-23T14:08:25Z` |
| 过期时间（UTC） | `2027-01-21T14:06:18Z` |
| 上传 zip SHA-256（run log） | `47d2ea81700841d4893c49f1e3d854a1e2ebdb5393e3ef661ecc4ed13df65acb` |
| 下载根目录 | `D:/majiang-wt/remote-ci/artifacts/remote-ci/39966787401/artifact/20261023-140643` |
| 完整 run log | `D:/majiang-wt/remote-ci/artifacts/remote-ci/39966787401/run.log` |

11 个文件：`summary.log`、`sln-build.log`、`plugin-copy.log`、4 个 `dotnetTest-*.log`、4 个 `direct-*.log`。summary 为 `OVERALL: FAIL (project rows: 5 PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN)`；测试日志齐全；构建证据为 solution 级；**缺插件清单与禁止物扫描结果**。

## 11. 根因：已观测事实与推断

### 11.1 已观测事实

1. `dotnet/global.json` 存在（tracked，`8.0.426` + `latestPatch`），仓库根没有 `global.json`。
2. workflow / `verify-v5.ps1` 从仓库根目录调用 `dotnet`；runner 默认选中的是 .NET 10（4 个 `dotnet test` 原始错误明确写 “.NET 10 SDK and later”）。
3. `build-shared.ps1:202` 的精确键查找失败，原始错误明确说 `project.assets.json` 中无 `.NETStandard,Version=v2.1` target。
4. 461 个 direct 用例确实执行且全绿；失败发生在 SDK/工具链与插件复制层，不是测试断言失败。

### 11.2 推断（未由远端日志直接证明）

- 远端日志**没有打印** SDK 10 restore 后 `targets` 的实际键名。因此“SDK 10 把目标键改成了短名（如 `netstandard2.1`）”只是对“精确键查找失败”的机制推断，不是已观测事实；具体键名需在 SDK 10 环境打印 `targets` 才能确认。
- 已确认的因果链只到：root 无 `global.json` → 实际选中 .NET 10 → `dotnet test` 报 MTP/VSTest 不兼容，且 assets 中不存在脚本要求的精确键。修复选择是让 SDK pin 真正生效，而不是放宽 TFM 检查。

## 12. 第 1 轮返修实施

### 12.1 修改清单（工作区，未提交）

| # | 文件 | 变更 |
|---|---|---|
| 1 | `dotnet/global.json` → `global.json` | 移动既有 pin 到仓库根；内容不变：`8.0.426` + `latestPatch` |
| 2 | `tools/verify-v5.ps1` | +40/−1：记录实际 `DotnetPath`、cwd、命中的 `global.json`、`dotnet --version`/`--info`；诊断失败 exit 2；summary 增加 SDK 行 |
| 3 | `.github/workflows/v5-verify.yml` | `dotnet-version: 8.0.x` → `8.0.426`，与 root `global.json` 精确对齐 |
| 4 | `V5-REMOTE-CI-VERIFICATION.md` | 保留原远端 FAIL 证据，新增本轮修复、复验结果与边界 |

关键 diff：

```diff
diff --git a/dotnet/global.json b/global.json
similarity index 100%
rename from dotnet/global.json
rename to global.json

-          dotnet-version: 8.0.x
+          dotnet-version: 8.0.426

+# tools/verify-v5.ps1 (SDK diagnostics, before build)
+$versionOutput = @(& $DotnetPath --version 2>&1)
+$versionExit = $LASTEXITCODE
+$infoOutput = @(& $DotnetPath --info 2>&1)
+$infoExit = $LASTEXITCODE
+if ($versionExit -ne 0 -or $infoExit -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
+    Write-Host "BLOCKED: dotnet SDK diagnostic failed ..."
+    exit 2
+}
```

设计要点：

- 从 cwd 向上查找 `global.json`，把实际命中的路径写入 `sdk-diagnostic.log`；root pin 对根目录和 `dotnet/` 子目录调用都生效。
- 诊断使用实际解析出的 `$DotnetPath`，不是另起 `dotnet` 命令；`--version` 或 `--info` 失败即 exit 2，绝不继续报绿。
- 既有构建失败、零用例、插件复制缺失/禁止物门禁保持不变；没有放宽 TFM，也没有跳过复制或忽略失败。

### 12.2 本轮命令

```powershell
# 1) 仓库根
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -LogDir artifacts\remote-ci-fix\round1-root

# 2) dotnet 子目录（验证向上查找 root global.json）
cd dotnet
powershell -NoProfile -ExecutionPolicy Bypass -File ..\tools\verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -LogDir ..\artifacts\remote-ci-fix\round1-dotnet

# 3) 诊断失败门禁探针（临时 fake dotnet，exit 1）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-v5.ps1 `
  -Configuration Release -DotnetPath <temp>\fake-dotnet.cmd `
  -LogDir artifacts\remote-ci-fix\diag-fail
```

### 12.3 本轮实际结果

| 运行位置 | cwd | 命中 global.json | SDK | build | 测试 | 插件 | 总结果 |
|---|---|---|---|---|---|---|---|
| 仓库根 | `D:\majiang-wt\remote-ci` | `D:\majiang-wt\remote-ci\global.json` | `8.0.426` | 0 warning / 0 error | 461 / 0 / 0 | 12 DLL，禁止物 0 | **PASS exit 0** |
| `dotnet/` | `D:\majiang-wt\remote-ci\dotnet` | `D:\majiang-wt\remote-ci\global.json` | `8.0.426` | 0 warning / 0 error | 461 / 0 / 0 | 12 DLL，禁止物 0 | **PASS exit 0** |
| 诊断探针 | 根 | root `global.json` | fake exit 1 | 未执行 | 未执行 | 未执行 | **BLOCKED exit 2** |

两次运行的 summary 均显示：

```text
dotnet SDK     : 8.0.426  path: D:\Unity\dotnet\dotnet.exe  cwd: ...
solution build : PASS (exit 0)
plugin copy    : PASS  (12 assemblies in scratch dir; tracked Unity assets untouched)
OVERALL: PASS  (project rows: 5 PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN)
```

`dotnet --info` 在两次运行中都显示 `global.json file: D:\majiang-wt\remote-ci\global.json`，SDK 8.0.426，host 8.0.32；本地 OS 为 Windows 10.0.19045（不是 CI 的 Windows Server 2025）。

## 13. 本地复验证据

### 13.1 每项目计数（两次运行一致）

| 项目 | 根目录 Total / Failed / Skipped | dotnet 子目录 Total / Failed / Skipped | runner |
|---|---|---|---|
| Mahjong.Domain.Tests | 233 / 0 / 0 | 233 / 0 / 0 | `dotnet test` |
| Mahjong.Contracts.Tests | 82 / 0 / 0 | 82 / 0 / 0 | `dotnet test` |
| Mahjong.Ai.Tests | 14 / 0 / 0 | 14 / 0 / 0 | `dotnet test` |
| Mahjong.Client.Tests | 132 / 0 / 0 | 132 / 0 / 0 | `dotnet test` |
| **合计** | **461 / 0 / 0** | **461 / 0 / 0** |  |
| Mahjong.Outbox.CrashWorker | 仅构建 | 仅构建 | helper |

### 13.2 插件闭包清单（12 DLL）

`Google.Protobuf.dll`、`Mahjong.Ai.dll`、`Mahjong.Client.dll`、`Mahjong.Contracts.dll`、`Mahjong.Contracts.Wire.dll`、`Mahjong.Domain.dll`、`Mahjong.LocalHost.dll`、`Mahjong.TableRuntime.dll`、`System.Buffers.dll`、`System.Memory.dll`、`System.Numerics.Vectors.dll`、`System.Runtime.CompilerServices.Unsafe.dll`。

包依赖版本：Google.Protobuf `3.28.3`、System.Buffers `4.4.0`、System.Memory `4.5.3`、System.Numerics.Vectors `4.4.0`、System.Runtime.CompilerServices.Unsafe `4.5.2`。

- 必需集合缺失：**0**。
- 禁止物匹配（`Mahjong.*.Tests.*`、`Mahjong.Outbox.CrashWorker.*`、`xunit.*`、`Microsoft.Testing.*`、`Microsoft.ApplicationInsights.*`、`Grpc.*`、`Google.Protobuf.Tools.*`）：**0**。
- 两次运行的 DLL 名称差异：**0**；SHA-256 差异：**0**。
- 复制目录另有 7 个 PDB；总复制 19 个文件。
- `git diff -- unity/` 为空，未覆盖 tracked Unity 资源。

### 13.3 证据路径（ignored）

| 证据 | 路径 |
|---|---|
| 根运行 summary | `artifacts/remote-ci-fix/round1-root/summary.log` |
| 根运行 SDK | `artifacts/remote-ci-fix/round1-root/sdk-diagnostic.log` |
| 根运行插件复制 | `artifacts/remote-ci-fix/round1-root/plugin-copy.log` |
| 根运行插件审计 | `artifacts/remote-ci-fix/round1-root/plugin-audit.log` |
| dotnet 运行 summary | `artifacts/remote-ci-fix/round1-dotnet/summary.log` |
| dotnet 运行 SDK | `artifacts/remote-ci-fix/round1-dotnet/sdk-diagnostic.log` |
| dotnet 运行插件审计 | `artifacts/remote-ci-fix/round1-dotnet/plugin-audit.log` |
| 诊断失败探针 | `artifacts/remote-ci-fix/diag-fail/sdk-diagnostic.log` |

## 14. 状态口径

| 项 | 状态 | 说明 |
|---|---|---|
| 原远端 `v5-verify` run | **FAIL** | run 39966787401，conclusion failure；已观测 |
| 原远端 clean checkout | PASS | exact SHA，1404 文件 |
| 原远端 .NET 8 实际生效 | **FAIL** | 实际 .NET 10；root 无 global.json |
| 原远端 `dotnet test` | **FAIL** | 4/4 MTP/.NET 10 错误 |
| 原远端 direct 测试 | PASS | 461 / 0 / 0；不能替代完整远端通过 |
| 原远端插件复制 | **FAIL** | `build-shared.ps1:202`；无 plugins 目录 |
| 本地 root 统一入口 | **PASS** | SDK 8.0.426，461，12 DLL，禁止物 0，exit 0 |
| 本地 dotnet 子目录统一入口 | **PASS** | 同一 root global.json，SDK 8.0.426，结果一致 |
| 诊断失败门禁 | **PASS** | fake dotnet exit 1 → BLOCKED exit 2，无 build |
| 远端新 SHA / 新 run | **NOT_RUN** | 本轮未触发；需 caller 在新 SHA 上验证 |
| Unity 加载 / IL2CPP / Android / iOS | **NOT_RUN** | 不在本 workflow 范围 |
| 真实 DB / G1 / G3 | **NOT_RUN** | 本 workflow 不覆盖 |

## 15. 缺口与边界

1. **远端新 SHA 尚未验证**：本地 PASS 只能证明修复在本地 .NET 8 路径生效；新 SHA 必须跑一次新的远端 `v5-verify` 并核对 artifact，才能标远端 PASS。
2. **原远端 artifact 缺插件清单**：原 run 失败在复制前，无法从原 artifact 核对闭包；本轮本地补齐了闭包/禁止物证据，但不改变原 run 的 FAIL。
3. **SDK 10 assets 键名仍是推断**：远端日志只证明精确键不存在，未打印实际键名；报告不把“短键名”写成已观测事实。
4. **本地构建为增量构建**：两次统一入口均 `0 Warning / 0 Error` 并通过门禁；未额外做 `bin/obj` 全清重建（原远端已是 clean checkout 构建）。
5. **远端 .NET CI 不代表 Unity 加载、IL2CPP、Android/iOS、真实 DB、G1/G3 通过**；这些仍需独立环境验收。
6. 本轮未提交、未推送、未建/改/合并/关闭 PR、未触发旧 SHA 的重复 CI；P1.4/WSS 未纳入。

## 16. 复现 / 补验命令

```powershell
# 本地 root 复验
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -LogDir artifacts\remote-ci-fix\round1-root

# 本地 dotnet 子目录复验
cd dotnet
powershell -NoProfile -ExecutionPolicy Bypass -File ..\tools\verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -LogDir ..\artifacts\remote-ci-fix\round1-dotnet

# 查看 SDK 诊断 / 插件审计
Get-Content artifacts\remote-ci-fix\round1-root\sdk-diagnostic.log
Get-Content artifacts\remote-ci-fix\round1-root\plugin-audit.log
```

远端补验（由 caller 在新 SHA 上执行；本轮未执行）：

```powershell
gh workflow run v5-verify -R liuchangready/majiang-game --ref <new-reviewed-ref>
gh run view <new-run-id> -R liuchangready/majiang-game --log
gh run download <new-run-id> -R liuchangready/majiang-game -n v5-verify-logs -D <ignored-dir>
```

## 17. 交付说明

- 本轮工作区修改：`global.json`（由 `dotnet/global.json` 移动）、`tools/verify-v5.ps1`、`.github/workflows/v5-verify.yml`、本报告。
- 按复核要求，本轮**未 commit、未 push、未新建/修改 MR、未合并/关闭 PR、未触发旧 SHA 的重复 CI**。
- 原远端 run `39966787401` 仍为 **FAIL**；远端新 SHA 为 **NOT_RUN**；本地返修为 **PASS**。
- 交回 caller 独立审查；远端新 SHA 的验证、PR #19 的推送/收口由 caller 统一处理。

## 18. Codex 第 2 轮独立复核（caller）

- 在当前工作树运行完整 Release 统一入口，实际 SDK 为 `8.0.426`，根目录与 `dotnet/` 的 `--version` 一致。构建 0 警告 / 0 错误；Domain 233、Contracts 82、Ai 14、Client 132，共 461 项，全部通过，0 失败 / 0 跳过，均为 `dotnet test`。
- 插件复制 12 DLL，必需集合缺失 0、禁止物匹配 0；已记录 SHA-256 清单后清理本次 scratch DLL/PDB，未修改 tracked Unity 资产。证据：`artifacts/codex-remote-ci-review/round2/`。
- 独立 SDK 诊断失败探针返回预期 exit 2，未进入构建。证据：`artifacts/codex-remote-ci-review/round2-diag-fail/sdk-diagnostic.log`。
- 复核时远端集成分支已推进到已验收 P1.4 提交 `d833348d0bbad23a140602d56859e8a33480ba0f`。上文的 `a056b45` 是原始 CI 运行基线，并非当前 PR head。caller 沿用 PR #19，将此远端已提交基线合入当前工作树后再次运行统一验证；其他任务未提交内容不纳入。
- 本节记录本地独立证据；新 SHA 的远端结果仍待后续记录，不能据本地 PASS 推定远端 PASS。
