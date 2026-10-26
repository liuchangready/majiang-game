# P1.5 有序 typed 随机输入：调用者独立验收

日期：2026-10-25。Bridge：`thread_20261025_144837_800b`，沿用原任务与 caller session；已实际调用 `approve_task`，返回 `COMPLETED`。

交付：`99a7a76723b5e009bb5756e05a0c460082893c0f`，直接父提交为冻结基线 `c5485884eac21f04874db19c1d64619ffa58de60`。
调用者在交付工作树 `D:/majiang-wt/shuffle-typed-random` 独立检查与实跑，随后由隔离集成树统一发布到既有唯一 PR19。

## 验收范围与结论

接受 D-R1-02 的 C# typed 输入 seam：`ShuffleTypedRandomTrace` 复制并校验基础墙与有序事件，`ShapeWithTypedRandomTrace` 严格消费 kind/bound/value。
Fisher-Yates 直接使用整数结果；概率与供体选择消费 double。类型、bound、轨迹不足和多余均结构化失败，`Wall=null`，失败事件不计入已消费前缀。
无目标、禁用和四人恒等要求空记录；零额度仍消费席位/槽位洗牌，不能当作空记录成功。

核对了真实 Kotlin 调用及 C# 共享算法，旧入口名称、错误码数值、double 选择与失败语义保持兼容。
交付精确四个 expectedFiles，工作树 clean，`git diff --check c548588 HEAD` 通过，开发者 SHA 清单逐项匹配。
新增输入与结果集合不可变；物理交换保留 136 张唯一、3 赤五和死山隔离。无新增并发、恢复、联网或公开投影路径，未发现新增安全/隐私缺陷。
本轮没有待返修缺陷。

## 调用者实测

CLI：`D:/Unity/dotnet/dotnet.exe`。证据根 E：`D:/majiang-wt/codex-shuffle-typed-review-20261025`。
以下全部由调用者执行，测试使用本轮非增量构建产物；没有 skip、Total0 或编译失败后使用旧 DLL。

| 检查 | native exit | 结果 / E 下日志 |
|---|---:|---|
| solution Release `--no-incremental` | 0 | 0 warning / 0 error；`solution-build.log` |
| TableRuntime.Tests Release `--no-incremental` | 0 | 0 warning / 0 error；`tableruntime-build.log` |
| Domain 全量 | 0 | Total492 / Errors0 / Failed0 / Skipped0 / NotRun0；`Domain-full.log` |
| AI 全量 | 0 | Total438 / Errors0 / Failed0 / Skipped0 / NotRun0；`Ai-full.log` |
| Contracts 全量 | 0 | Total149 / Errors0 / Failed0 / Skipped0 / NotRun0；`Contracts-full.log` |
| Client 全量 | 0 | Total132 / Errors0 / Failed0 / Skipped0 / NotRun0；`Client-full.log` |
| TableRuntime 全量 | 0 | Total365 / Errors0 / Failed0 / Skipped0 / NotRun0；`TableRuntime-full.log` |
| typed 定向 | 0 | Total9 / Errors0 / Failed0 / Skipped0 / NotRun0；`typed-targeted.log` |
| 旧 shuffle 定向 | 0 | Total22 / Errors0 / Failed0 / Skipped0 / NotRun0；`legacy-targeted.log` |
| 独立边界与逐位置探针 | 0 | Total10 / Errors0 / Failed0 / Skipped0 / NotRun0；`probes/positive.log` |
| 负例矩阵 self-negative | **1（预期）** | Total1 / Errors0 / Failed1 / Skipped0 / NotRun0；`probes/self-negative.log` |

标准五模块合计 **1576 passed / 0 failed / 0 skipped / 0 not-run**。定向用例已在 Domain492 中，不重复加总。
外部副本的 10 Facts 包含复制的 9 项及一个调用者新增 Fact，不计为新增标准测试数。

调用者新增 `probes/ReviewerBoundaryTests.cs`，运行 24 个场景（含 double 0 与最大可表示的小于 1 的数），与 legacy 中间区间选择逐张比较。
对 **2923 个有序事件位置**逐项替换 kind，并对每个 int 位置另改 bound；均验证精确前缀计数、`Wall=null`，以及结果请求集合不可写。
这是 C# 消费合同的独立探针，**不是 V4 黄金一致证明**。

self-negative 仅修改 E 下测试副本：向负例矩阵加入完整合法事件作为 `self-negative-valid-control`。
检测器实际报告该控制被接受，唯一 Fact 失败并返回 native1；调用者同时检查失败名称、Total1/Failed1 与退出码。
没有向生产源写负例，也没有把这一预期失败算作产品失败或通过项。

`E/review-summary.json` 保存结论和计数；`E/SHA256-MANIFEST.csv` 保存日志与外部探针源码哈希，其 SHA-256 为：
`B64CCFC9C6409CC9997686319E9DD47C9DE0E6BC725BDF1675A9FBC68E7CD098`。
旧 fixture SHA-256 不变：`386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`。

## 标准复跑命令

在交付工作树或包含本提交的干净仓库执行：

```powershell
$cli = 'D:/Unity/dotnet/dotnet.exe'
& $cli build dotnet/Mahjong.sln -c Release --no-incremental -v minimal
if ($LASTEXITCODE -ne 0) { throw 'solution build failed' }
& $cli build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v minimal
if ($LASTEXITCODE -ne 0) { throw 'runtime test build failed' }
foreach ($moduleName in @('Domain','Ai','Contracts','Client','TableRuntime')) {
    & $cli "dotnet/tests/Mahjong.$moduleName.Tests/bin/Release/net8.0/Mahjong.$moduleName.Tests.dll" -noLogo
    if ($LASTEXITCODE -ne 0) { throw "$moduleName tests failed" }
}
& $cli dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo -class '*ShuffleTypedRandomTraceTests*'
& $cli dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo -class '*ShuffleWallSynthesisTests*' -class '*ShuffleGoldenTests*' -class '*ShuffleGoldenBindingTests*'
```

外部 self-negative 副本保留在 E，其 `probes/Reviewer.Typed.Probes.csproj` 直接引用已验收 Domain。
重新构建后运行 `probes/bin/Release/net8.0/Reviewer.Typed.Probes.dll -noLogo -method '*BatchSingleFieldMutationMatrixRejectsEveryCorruption*'` 应为 native1 / Total1 / Failed1。
重新执行正控制前，从该测试副本删除唯一 `cases.Add(("self-negative-valid-control", ...))` 注入行；外部全量应为 Total10 / Failed0。

## 统一发布与边界

复用唯一 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，`feat/v5-1-integration -> v5`。
远端发布前头为冻结基线；本地同名集成分支是其祖先，零独有未推送提交。因此本批只增加本任务交付和本独立验收记录，统一一次正常快进推送。
开发者不推送，调用者在 `D:/majiang-wt/codex-shuffle-typed-integration-20261025` 集成；用户主 checkout 的四份未提交文档以 SHA 核对并排除。

`threeCasterShapeBinding` 仍 **BLOCKED_RANDOM_INPUT**（真实 V4 有序事件尚未提取）；`reversedEqualsThree` 仍 **BLOCKED_MISSING_REVERSED_WALL**。
本组件验收不批准完整 N3、G1 或 G5，V4 仍是唯一生产架构。PG/WSS/Unity/IL2CPP/Gradle/设备/物理网络及本次发布远端 CI 均 **NOT_RUN**。
