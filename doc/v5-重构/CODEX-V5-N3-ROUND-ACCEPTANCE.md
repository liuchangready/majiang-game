# N3-A 轮次黄金字段绑定：第 2 轮独立验收与 PR19 集成

日期：2026-10-25。Bridge：`thread_20261025_094642_e558`，沿用原任务与 receiverSession；调用者已执行 `approve_task` 归档。

接受交付 `4167a5f025ff6ff3cfdc33a41bc52213b77431d6`，包含第一轮 `6c8597b5caa8392c32674783f1de565bb134331b`。相对冻结基线 `8ef0f49`，交付仅修改 `RoundReplayGoldenTests.cs` 与 `V5-N3-ROUND-GOLDEN-BINDING.md`，工作树干净，累计 `git diff --check` exit0。生产代码、fixture、catalog/manifest、verifier、项目文件均未修改。

## 接受范围

RG-001 / RG-002 的输入、结果和终局生命周期字段与真实 Domain 调用绑定。终局事件检查精确序列；C# 没有原生 `ScoreSettled` 枚举，使用真实非空结算结果及分数、差额、立直棒作为该语义位置的证据，并严格核对实际提交事件与历史前缀。抢杠布尔取真实结果事件 Contains；物理副露槽位、136 张守恒、响应资格、终态和版本均有断言。AgariYame 调用真实 `MatchSession.ChooseDealerEnd`，没有继续使用 Tsumo 别名。

第 2 轮 D-R1-01 的 match length、clock scope、progression result type 均严格解析；未知、缺失、错型不会回退到有效分支，合法分支计数也被检查。D-R1-02 的脚本用 `try/finally` 恢复环境变量，报告任务 ID 已纠正。审阅最终代码、两轮差异、报告及原始返修要求，没有发现新增阻断；原有六个 Fact 与 match/clock/control 行为断言保留。

本次没有新增运行代码；并发、恢复和私有投影的集成边界由既有 TableRuntime 全量回归覆盖，不把这次测试变更描述为新的生产功能。

## 调用者独立验证

CLI：`D:/Unity/dotnet/dotnet.exe`。交付目录：`D:/majiang-wt/n3-round-binding`。

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -showLiveOutput -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class '*RoundReplayGoldenTests*' -showLiveOutput -noLogo
```

| 验证 | 结果 | native exit |
|---|---|---:|
| 交付 Release solution 非增量构建 | 0 warning / 0 error | 0 |
| 交付 Domain 全量 | 385 passed / 0 failed / 0 skipped / 0 Not Run | 0 |
| 交付 Round 定向 | 6 passed / 0 failed / 0 skipped / 0 Not Run | 0 |
| 独立外部 fixture 负例 | 60/60 真实框架失败；每例 Total 6，Errors 0、Failed 1、Skipped 0、Not Run 0 | 每例 1 |
| 身份/时间保留控制 | 6/6 通过；每例六个 Fact | 每例 0 |
| 原 fixture 正控制 | 6 passed / 0 failed / 0 skipped | 0 |
| 矩阵脚本整体 | 0 unexpected pass / 0 unexpected failure | 0 |
| 环境恢复四路径 | existing/unset × normal/forced-failure 全部 ENV-RESTORE-PASS | 0 |

证据根为 `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r2-review-84edf0bfbdb84691b5c6f1caeff1750e`（以下记为 E）。`run-matrix.ps1` 从交付报告附录 A 原样提取，仅以 UTF-8 BOM 写入 TEMP。`env-restore-probes.ps1` 复制交付的同进程四路径验证脚本，仅将脚本及输出目录改为 E。正常路径执行完整矩阵；异常路径在设置变量后强制抛错。两种原始环境状态都在同一进程中验证恢复。首次直接执行 TEMP 脚本被该 PowerShell 进程的执行策略阻止，未运行测试；随后使用下列进程级参数完成验证，未改全局策略。

```powershell
$e = 'C:/Users/sncdc/AppData/Local/Temp/codex-n3-r2-review-84edf0bfbdb84691b5c6f1caeff1750e'
powershell -NoProfile -ExecutionPolicy Bypass -File "$e/run-matrix.ps1" -Repo D:/majiang-wt/n3-round-binding -EvidenceRoot "$e/reproduced-matrix"
$matrixExit = $LASTEXITCODE
powershell -NoProfile -ExecutionPolicy Bypass -File "$e/env-restore-probes.ps1"
$environmentExit = $LASTEXITCODE
```

原样本 SHA-256 前后均为 `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`。负例单独修改 TEMP 副本；60 项包含原 46 项及新增 14 项，未用同时修改多个字段掩盖未绑定字段。`matrix/matrix.csv` 保存逐例计数、exit、失败位置与日志路径，`matrix/summary.json` 保存汇总；调用者另逐行确认负例 exit 恰为 1 且有真实非零框架失败。

## 隔离集成与统一 MR

远端现有唯一 PR 为 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`，目标 `v5`。从已发布头 `0d253686f217c06ef85f9ee9983cec149594e98f` 创建隔离目录 `D:/majiang-wt/codex-n3-r2-integration-20261025`，正常合并交付，得到 `4c37b60c3f52e7f80e334d47b0e5325b4c57f794`，无冲突。后续出版提交仅添加本文，已测代码不变。

```powershell
# 在 D:/majiang-wt/codex-n3-r2-integration-20261025 执行
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release
```

集成构建 0 warning / 0 error、exit0。solution 四项目 Domain385 + AI435 + Contracts149 + Client132 = 1101；TableRuntime 不在 solution，已单独重新编译并执行 355 项。合计 **1456 passed / 0 failed / 0 skipped**，两个测试命令均 exit0。

同批已接受 N1a、N2、N3 scoring 和 N1b 均在该远端基线上，全部保留。本批新增未推送的两笔 N3-A 开发/返修提交、隔离合并与调用者验收记录统一推送并更新 PR19。仍处于 reworking 的 N3 审计未纳入；未另建子任务 MR。主 checkout 的四项用户未提交内容保留，临时工作树保留供追溯与 merge-back。

## 日志 SHA-256

下列路径相对 E；`sha256.json` 保存命令与矩阵汇总等主要证据哈希，`evidence-sha256.csv` 另包含逐例日志及环境路径复验的完整文件清单。

| 文件 | SHA-256 |
|---|---|
| build.log | `F81D41012AB829E137C2815D59B82E2F7C71AEEADF8ECBCEB33B43E0C2EA1914` |
| domain.log | `573B60D03FD9FCB736216375ECA95790D32E79062F018F9FF3991721DA5C9A03` |
| round.log | `FACFB911B4E7ED4B21017B79429B720393C77A5D72FA16A8286220F9AD5212A6` |
| matrix-console.log | `7D526226EB6142E4B5CFA3B6CE6B25545FCAC55CBBB982383A1647AC8EEBCADC` |
| matrix/matrix.csv | `1B48413E2606118AE293311CA5175B7BAA0236490AAA1AAF8E2317B0A3DA1D66` |
| matrix/summary.json | `FB094517B8F5DBAB63DE2C64A62301967D43DABB0C34CF0256E05673212828B9` |
| run-matrix.ps1 | `BC54F100733865D27374289F43629B3BAF1FF6E0EE93BFCD9085DA790C107AFD` |
| env-restore-probes.ps1 | `94A7994883D31C58A02156AFFC0916FBF64EF13B451AC1ABFC3EF1ECA9146C42` |
| env-restore.log | `2398A599C2EEBEBE5958BD510DC426B8BAEC81DE99ECC06B91989954AB66BBEB` |
| integration-build.log | `6D8286B286CF492B2F6762A4A8948CBFB22C31A74CAF14B645772B65E16DDBD7` |
| integration-solution-tests.log | `48BF53BA062296B6230C27579A5AA15F30A1F300A2DF23E1096A1D6AED8CEE70` |
| integration-runtime-tests.log | `514703ACA580EFB6A1A717D5EE319D743D6CE792C4787B18F6783839B3022FCB` |

## 验收边界

PASS 仅为 N3-A 轮次黄金消费者绑定及上述集成回归。完整 N3、其他 fixture 与 20 项 partial 未由本任务关闭；G1 仍未批准，G5 未完成，V4 仍为唯一生产架构，无 `complete.flag`。本轮 PG/WSS、Unity/IL2CPP、真机、真实网络与 V4 Gradle 全量 NOT_RUN；本地通过不代表本次出版后的远端 CI 已通过。
