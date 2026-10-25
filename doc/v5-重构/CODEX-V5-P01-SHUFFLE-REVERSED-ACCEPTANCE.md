# P0.1 实际 reversedWall 伴随证据：调用者独立验收

日期：2026-10-25。Bridge：`thread_20261025_152559_a4ef`，沿用原任务、caller session 和 receiver，未新建任务或转派。

接受范围：真实 V4 正逆序三人码牌墙的伴随导出、旧样本绑定与关系重算。**仅组件 PASS**；真实 V4 有序随机输入仍为 `BLOCKED_RANDOM_INPUT`，G1 BLOCKED，G5 NOT APPROVED，V4 仍为唯一生产架构。

交付提交为 `8734494936aad0d451511156a454b989d508468b` 和 `0b5cbdf536d6a17cb2576f3e8c205739cb512599`，冻结父基线为 `03084f08af6e2ddefa2ebc6e094de5dac2474fd4`。交付恰好五个新增文件，工作树 clean，`git diff --check` 通过；旧 fixture、生产 Kotlin/C#、项目、目录及统一 verifier 未改。两条原 MAJOR 均已修复：plan 对象使用唯一字段清单，来源 META 严格读取并验证。

## 独立实测

CLI：`D:/Unity/dotnet/dotnet.exe`。调用者证据根 E：`D:/majiang-wt/codex-shuffle-reversed-r2-review-20261025-1640`。
Gradle launcher JVM 25.0.2，编译 target JVM 17。Kotlin 既有编译/弃用警告保留，未称其零警告。

| 检查 | native exit | 真实结果 / E 下证据 |
|---|---:|---|
| 交付 solution、TableRuntime.Tests Release `--no-incremental` | 各 0 | 各 0 warning / 0 error；`solution-build.log`、`tableruntime-build.log` |
| 交付五模块标准全量 | 各 0 | Domain496 + Ai438 + Contracts149 + Client132 + Runtime365 = **1580**，Errors/Failed/Skipped/NotRun 均 0；`*-tests.log` |
| Kotlin `--rerun-tasks` | 0 | exporter1 + ShuffleAlgorithm15 + ShuffleAtomicDeal1 = **17**，fail/error/skip 均 0；`kotlin-shuffle.log`、三份 `TEST-*.xml` |
| 原 reviewer strictness | 0（驱动脚本） | baseline Total4/Failed0/native0；8 个负例各 Total4/Errors0/Failed>0/Skipped0/NotRun0/native1；`strictness/strictness-results.json` |
| 交付 LF 矩阵 / self-negative | 0 / 1（预期） | 76 场景符合预期；77 场景仅注入合法控制一行异常；`matrix/`、`self-negative/` |
| 开发者完整证据 SHA 校验 | 0 | **1634/1634** 匹配；`delivery-manifest-check.json` |
| 隔离集成 solution、Runtime fresh 双构建 | 各 0 | 各 0 warning / 0 error；`integrated-solution-build.log`、`integrated-runtime-build.log` |
| 隔离集成五模块标准全量 | 各 0 | Domain506 + Ai438 + Contracts149 + Client132 + Runtime365 = **1590**，Errors/Failed/Skipped/NotRun 均 0；`integrated-*-tests.log` |
| 隔离集成 strictness | 0（驱动脚本） | baseline 与 8 负例再次全部符合相同精确门槛；`integrated-strictness/` |
| 修复后干净 CRLF 正常 / self-negative | 0 / 1（预期） | **76 / 77** 场景全部符合预期；`crlf-fixed-matrix/`、`crlf-fixed-self-negative/` |
| 修复后干净 LF fresh Domain 构建 | 0 | 0 warning / 0 error；`lf-clean-build.log` |
| 修复后干净 LF 正常 / self-negative | 0 / 1（预期） | **76 / 77** 场景全部符合预期；`lf-fixed-matrix/`、`lf-fixed-self-negative/` |

标准计数各自独立，不叠加定向或矩阵反复执行的四项 Facts。所有矩阵行检查 Total4、Errors0、Skipped0、NotRun0；71 个负例必须 native 非零且 Failed>0，5 个正控制必须 native0/Failed0。self-negative 的唯一异常场景实际 native0/Failed0，因此产生 **2 条条件违反信息、1 个异常场景**；驱动脚本据此退出 1，未硬编码失败。原始 JSON 的 `violations=2` 不改写成 1。

三面实际墙均有 136 唯一实体、三赤五、死山隔离及重复运行确定性断言。C# 逐样本关联旧 fixture 的 seed/dealer/plans/config/base/three，并由实际三人墙与逆序墙重算关系；没有以布尔常量替代逐槽比较。来源集合和内容 SHA 实际校验，raw 与 LF-normalized 域区分。无新增生产领域、并发、deadline、控制代次、恢复或公开投影路径，相关运行时维度不受本变更影响。

## 发布前发现与调用者修复 D-R2-01

交付树复验后调用者已实际执行 `approve_task`，Bridge 返回 `COMPLETED`。随后全新集成 checkout 揭示本轮新增 RawMutation 的 CRLF 依赖：`New-PlanObjectText` 构造 LF 锚点，而检出的 fixture 有 1512 个 CRLF。前三正控通过，但第四场景在执行测试前抛出 `raw mutation did not match`，矩阵 native1，不能视为组件证据通过。

失败保留在 `E/integrated-matrix.log`，SHA-256：`DDB217B0A998A3E71EAA1C210F561262C745BA0D639C90895A8FA6041E417757`；`clean-checkout-crlf-failure.json` 保存 clean status、HEAD、行尾及 SHA。调用者在干净检出验证前归档得过早。

调用者立即向同一线程调用 `request_rework`，提交 D-R2-01 的复现、证据和修复断言；Bridge 拒绝：`no active reviewer role is held`，原因是归档已释放 reviewer 角色。未新建任务、会话或转派。调用者在当前会话直接修复并提交 `67d54b576fe4d787e36beb6bf322d74b16354517`：只规范化内存匹配副本的 CRLF/孤立 CR，磁盘 fixture 和 provenance SHA 保持真实；重复键直接以文本写入，没有经 JSON 反序列化吞掉。

修复后的两个检出树均为同一提交、clean：

| 工作树 | 来源行尾 | fixture 原始 SHA-256 |
|---|---|---|
| `D:/majiang-wt/codex-shuffle-reversed-integration-20261025` | 1512 CRLF / 0 lone LF | `81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902` |
| `D:/majiang-wt/codex-reversed-lf-20261025` | 1512 LF / 0 CRLF | `5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619` |

第二树通过命令局部 `git -c core.autocrlf=false worktree add --detach` 创建，未改全局设置。两个来源各运行完整正常矩阵与 self-negative，真实重复键内容和全部精确门槛由 `E/verify-review.ps1` 复核，exit0。后续调用者修复仅涉及探针和文档，C#、Kotlin 与已全量验证的构建输入相同。

## 复跑与证据

在仓库内运行：

```powershell
$cli = 'D:/Unity/dotnet/dotnet.exe'
& $cli build dotnet/Mahjong.sln -c Release --no-incremental -v minimal
& $cli build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v minimal
foreach ($module in @('Domain','Ai','Contracts','Client','TableRuntime')) {
    & $cli "dotnet/tests/Mahjong.$module.Tests/bin/Release/net8.0/Mahjong.$module.Tests.dll" -noLogo
    if ($LASTEXITCODE -ne 0) { throw "$module failed" }
}
./gradlew.bat :engine:test --tests com.majiang.v3.V5ShuffleReversedGoldenExporterTest --tests com.majiang.v3.ShuffleAlgorithmAcceptanceTest --tests com.majiang.v3.ShuffleAtomicDealAcceptanceTest --rerun-tasks --console=plain
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-shuffle-reversed-binding.ps1 -RepoRoot (Get-Location).Path -OutDir '<new evidence directory>'
# 用另一个全新 OutDir 加 -SelfNegative：预期 exit1，且仅合法注入控制一行异常。
```

`E/SHA256-MANIFEST.csv` 包含 **1018** 个文件的 SHA-256，包含原始失败、完整日志、每个变异 fixture、JSON、XML 和调用者复核脚本；清单 SHA-256 为 `CB6ACEEF3F4D821F12B61211E2F5CA94C1CD10A2EE302F1D56DDE77FBA67F7E2`。
`E/review-summary.json` SHA-256 为 `AC8D1D409490E43FC9635D8ECD81B3751B71132BF63FCEB148BBBA7B0E7BA6C1`。
用户主 checkout 的四份未提交文档经 SHA 比对不变；未复制、提交或覆盖。

## 统一发布与保留边界

复用既有唯一 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，`feat/v5-1-integration -> v5`。发布前远端头为 `1432d775526de921f2cde3aba283236fc3b8e8c1`，已含同批 movement 任务；本地旧集成分支是其祖先，无独有未推送提交。隔离 merge `23609b1a069619561832797ccc30dee89b616211` 保留该已接受批次。本次统一正常推送两条交付提交、merge、调用者行尾修复和验收记录；未纳入未验收 audit 工作，未创建额外 MR。

此结论只补齐实际 reversed-wall 证据，不构成 C# Shape 三人逐张输出差分。真实 V4 有序 typed 随机事件尚缺，`BLOCKED_RANDOM_INPUT` 保留。完整 N3/G1/G5 未批准。PG/WSS/Unity/IL2CPP/Android/iOS/设备/物理网络与本次发布远端 CI 均 **NOT_RUN**；旧 Client CI `39970580589` 根因仍 **UNRESOLVED**。未创建 `complete.flag`。
