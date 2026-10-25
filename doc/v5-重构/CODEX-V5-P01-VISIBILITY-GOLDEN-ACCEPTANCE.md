# G-057 可见性与快照深拷贝黄金：调用者独立验收

日期：2026-10-25。Bridge：`thread_20261025_162257_7df6`，沿用原 caller/receiver 会话。

本组件的可比字段验收通过。真实 V4 黄金覆盖 G-057a..d，C# 从输入重建 136 个实体的状态，调用既有生产投影与 `MapEvent`、`MapPublicSnapshot`、`MapPlayerSnapshot`，比较公开、逐席、owner 扩展和授权/未授权安全结算明细。三轮交付的最终提交为 `4086bf3db898d65b9e2119df3247a9beb89ba121`，包含 `544f9c1`、`e8a9e9a`；相对冻结基线 `1432d775526de921f2cde3aba283236fc3b8e8c1` 恰好五个约定文件，交付树 clean，累计 diff-check 通过。

**G1/G5 未批准，V4 仍是唯一生产架构。** 这里不把 SOURCE_ONLY、UNREPRESENTABLE 或已披露的 V4 缺陷计为跨运行时行为等价。

## 独立复核结论

本轮确认合并遗留 `D-G057-R2-01` 修复：b/c 共享逐值 mapped-event 比较器；C 的事件分数、番符、役、回合、副露来源/鸣牌、立直标记及 round 字段均消费黄金输出；两处重复 breakdown 表示均比较，未授权安全非 null breakdown 的数值/役/空私有身份也被消费；显式授权适配不再恒为 true。`MapPublicSnapshot` 逐条保留已经完整比较的 typed records。

raw 私有 ura 身份配合未授权标志会被 `ScoreBreakdownView` 拒绝。安全分支由 caller 从原始 INPUT 构造不携带私有身份的 facts；**这证明 caller 授权契约，不证明 V5 mapper 自动脱敏**。独立复跑上一轮的原始 23 个单字段变异，全部从漏检变成真实 `Total8/Failed1/native1`；没有依赖摘要 hash、schema 或非法事件次序制造这些失败。

本轮没有修改生产实现、旧 fixtures、冻结 catalog/manifest 或公共 verifier。快照边界包含真实 discard 后的新旧投影差异和真实 clock 输入变化；只读集合与 producer 输入复制另有执行断言。

## 当前交付工作区实跑

工作区：`D:/majiang-wt/visibility-golden-g057`。

| 项目 | native exit | Total | Errors / Failed / Skipped / NotRun |
|---|---:|---:|---|
| Release solution 非增量构建 | 0 | — | 0 warning / 0 error |
| 独立 TableRuntime.Tests 非增量构建 | 0 | — | 0 warning / 0 error |
| VisibilityGoldenTests | 0 | 8 | 0 / 0 / 0 / 0 |
| TableRuntime 全量 | 0 | 373 | 0 / 0 / 0 / 0 |
| Domain TilePositionMap | 0 | 59 | 0 / 0 / 0 / 0 |
| Domain TileMovementGolden | 0 | 10 | 0 / 0 / 0 / 0 |
| Domain CheatVisibility | 0 | 10 | 0 / 0 / 0 / 0 |
| Contracts ProjectionWireMapper | 0 | 16 | 0 / 0 / 0 / 0 |
| Contracts ProtocolContract | 0 | 30 | 0 / 0 / 0 / 0 |
| Gradle exporter | 0 | 2 | failures/errors/skipped 全 0 |
| Gradle SessionSnapshot | 0 | 8 | failures/errors/skipped 全 0 |
| Gradle PublicProjection | 0 | 12 | failures/errors/skipped 全 0 |

Contracts 类真实命名空间为 `Mahjong.Protocol.Tests`；首次使用错误筛选产生的两个 Total0 日志保留，**不计入通过统计**。修正后的 16/30 项如上，未把 native0 当成非零测试发现的替代。

提交内 probe exit0，67 个 native runs：baseline + 5 正控制全部 `Total8/native0/Failed0`，61 负例全部 `Total8/native1/Failed>=1`；每行 Errors/Skipped/NotRun 均 0。计数门控自身拒绝 Total0、short count、skip、notRun、error 和 exit 漂移。SelfNegative 先通过 8 项 baseline，再故意 exit1，调用者核对该 exit。

独立原始复核矩阵另有 baseline + 23 变异；逐行校验 baseline exit0/Failed0、每个变异 exit1/Failed1，均 Total8 且其余错误/跳过计数为 0。

交付 fixture raw/LF SHA-256：`30291ec270efb6f2a7d8c6984d53e7ae2dbd5ae45a41a7114ee69288749f346e`；render hash：`642cfdda1c11587c63ae9f8587c24c4959f7ed3e8bea5bd51d70a36a7b4cb4b8`。默认 Gradle 与探针没有改写原 fixture。

## 证据标签与环境失败

probe 原始 `results.csv` 的 `expected` 列由 helper 固定写为 PASS，不能拿这列推断场景预期；实际 driver 另按 `mutation.Expected` 执行严格 gate，因此并未把负例误判为绿色。调用者按 scenario 名、native exit 和完整摘要独立校验 61 FAIL + 6 PASS，并保存 `probe-independent-counts.csv`。这是保留的非阻断报告标签瑕疵，不声称其已修复。

集成树上的首次 probe 因深层临时 NuGet 包目录触发 MSB3106/MSB3954，build native1、driver2；该失败保留为环境阻塞，不算领域负例或测试通过。调用者立即对同一 thread 调用 `request_rework`，Bridge 拒绝：`no active reviewer role is held`，因为前面的组件验收已归档。没有另建任务、会话或转派。

调用者在同一会话以子进程 `NUGET_PACKAGES` 指向该集成树自己的较短 `build/nupkg` 重跑原 probe；没有改全局环境、项目、脚本、断言或计数门控。最终结果见下一节。

## 隔离集成与唯一 MR

隔离树：`D:/majiang-wt/codex-visibility-final-integration-20261025`。在已发布的 `0e7afad0ec2b7bf740d3776c26ed6ff4aae74858` 上合并三个交付提交，得到 `182d98979ec9ca4d4a46ec71e731dc5b8a14538b`。保留已接受 movement、typed shuffle、reversed-wall 等批次。主集成分支与远端无额外未推送提交；未验收字段审计候选排除。

新鲜集成 solution 与独立 Runtime 构建均 exit0，0 warning/error。标准全量 Domain506 + AI438 + Contracts149 + Client132 + TableRuntime373 = **1598 passed / 0 errors / 0 failed / 0 skipped / 0 not-run**，五个 native exit 均 0。这不包含独立的 PG/WSS/Unity 等项目。

使用独占短包缓存后的原 probe 再次 exit0，67/67 runs 符合预期（61 真实失败负例 + baseline/5 正控制），所有运行 Total8、Errors/Skipped/NotRun=0。调用者再次逐行核对 native exit 和 Failed。成功 evidence：`build/visibility-binding-probe/20261025T104358Z-41156`；原环境失败 evidence：`20261025T104223Z-20300`。该干净 checkout 的源码与 fixture 使用 CRLF；fixture raw SHA-256 为 `6506c0729a36e4625e157a5e6ea2ccb79b9c2ff2e4f3f57988bbb9535cc52b94`，LF-normalized 内容与交付一致，运行前后未改变。

本记录作为调用者文档提交附于上述实测 merge；不修改测试实现。统一更新已有 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`、目标 `v5`，普通 fast-forward push，不另建子任务 MR。用户主工作区已有 `CLAUDE.md` 和 `doc/ui/round-preparation/PROMPTS.md` 修改不暂存、不复制，merge-back 前后核对文件 SHA。

## 可复制命令

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental --nologo -v:minimal
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -class Mahjong.TableRuntime.Tests.VisibilityGoldenTests -noColor -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noColor -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Contracts.Tests/bin/Release/net8.0/Mahjong.Contracts.Tests.dll -class Mahjong.Protocol.Tests.ProjectionWireMapperTests -noColor -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Contracts.Tests/bin/Release/net8.0/Mahjong.Contracts.Tests.dll -class Mahjong.Protocol.Tests.ProtocolContractTests -noColor -noLogo
.\gradlew.bat :engine:test --tests com.majiang.projection.V5VisibilityGoldenExporterTest --tests com.majiang.session.SessionSnapshotContractTest --tests com.majiang.projection.PublicProjectionTest --rerun-tasks --console=plain
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-visibility-binding.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-visibility-binding.ps1 -SelfNegative
# 较长的隔离树：仅当前子进程设置独占缓存，避免深层包路径。
New-Item -ItemType Directory -Force build/nupkg | Out-Null
$env:NUGET_PACKAGES=(Resolve-Path build/nupkg).Path
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-visibility-binding.ps1
```

集成全量执行分别直接运行 Domain、Ai、Contracts、Client、TableRuntime 的 Release/net8.0 测试 DLL，每个运行均检查非零 Total、exit0 及所有错误/失败/跳过计数为 0。

## 原始证据及边界

交付调用者证据位于 `build/review-g057-r3`；标准日志、原始复核脚本/24 个运行日志、XML、计数复核 CSV 和两组 probe 的日志列入 121 文件的 `SHA256SUMS.csv`，该 manifest SHA-256 为 `b33aed41485ccdfa8c956dc8a10a04f4f04d7f1dff396c2bb6609b58038f68cf`。probe 目录分别为 `build/visibility-binding-probe/20261025T103817Z-14960`（67 runs）与 `20261025T104011Z-31512`（SelfNegative）。临时 DLL/缓存不作为证据清单内容。

集成证据位于 `build/review-visibility`，合并交付清单与集成标准日志、失败/成功 probe 原始日志后，`SHA256SUMS.csv` 共 **205 文件**，SHA-256 为 `00a050206edad224a16ef4a45e8f5ead20f1898c06f65e6a5cb2ad2924bf1cca`。交付 121 个原文件再次逐一核对 hash 通过；清单中的初次筛选 Total0 与 MSBuild 失败日志仅作保留的失败/未运行证据。

保留的差异：V4 `CheatPointsChanged` 对四席泄漏（生产冻结），V5 仅 owner；V4 暗杠身份与 V5 隐藏占位不同；legalActions/DecisionView、session summary、未过滤 SessionUpdate、wall 指针等仅按交付分类记录。raw ura 的授权事实必须由可信 caller 提供。没有用这批测试修复这些既有生产差异。

PG/WSS/Unity/IL2CPP/Android/iOS/设备/物理网络和本次远端 CI 在本轮均未验收；不把 .NET 或 V4 定向成功写成这些平台通过。完整 N3/G1/G5 仍未批准。
