# N3 shuffle-v1 字段绑定：调用者独立验收

日期：2026-10-25。Bridge：`thread_20261025_124651_4c90`，沿用原调用者/开发者 session；本组件已通过 `approve_task` 批准归档。

交付 HEAD：`1c43a9487f09ac80da804294daf215b63c08c614`，包含首轮 `aaa07af10b77006b9c653ecef63710e8d04b458b`。
唯一已发布基线：`0c6eff1e47e99396d6a9c49dd32293d625a8bb03`。交付是该基线的直接后继，当前独立工作树即隔离集成候选，无其他组件合并差异。

本验收仅覆盖已授权的 shuffle-v1 消费者补齐。[字段分类与源码锚点](V5-N3-SHUFFLE-GOLDEN-BINDING.md)区分数学逐值、真实 Shape 输出、导出墙内部关系、META、SOURCE_ONLY 和 BLOCKED；不把内部自洽称为跨运行时行为对齐。
本组件不批准完整 N3、G1 或 G5；V4 仍为唯一生产架构。

## 三项返修与审查结论

| 返修 | 独立复核 |
|---|---|
| D-R1-01，32 样本完整性 | 精确 seed 集合 0..31、去重及数量校验；复制单样本、全部复制 seed0、缺样本和越界均失败，合法 case 乱序仍通过。|
| D-R1-02，随机输入依赖规格 | 已明确基础墙形成后全部有序 typed `nextInt(bound,result)` / `nextDouble` 事件，以及 C# floor 选择的无损适配要求；仅 doubles 记录不足以闭合。保留 BLOCKED，未实现新 seam。|
| D-R1-03，META 与持久报告 | 报告已入 git；两个 META 字段严格读取字符串，校验真实 exporter 路径和 V4 来源声明。来源存在性检查有明确的 repo 可发现前提；commit 只验 40-hex，不声称 ancestry 验证。|

累计交付仅新增三份允许文件；旧四个 Shuffle Facts、生产 C#/Kotlin、exporter、fixture/catalog/manifest、统一 verifier、项目/solution 未改。
累计 `git diff --check 0c6eff1..1c43a94` 通过，交付工作树 clean。无 skip、空测试、旧 DLL、Total0 或编译失败掩盖。
对照 exporter 和真实生产入口，确认 96 次恒等 Shape 调用、32 个逻辑 case、136 张守恒与死山映射；同一 case 在多个 Facts 遍历不重复计为样本。
没有新增生产并发、恢复或公开投影路径；未发现新增领域规则、安全或隐私缺陷。

## 调用者实测与证据

证据根 E：`D:/majiang-wt/n3-shuffle-review-f826e90f12644b5291c7132fde0971ec`。
CLI：`D:/Unity/dotnet/dotnet.exe`。命令均在原 worktree `D:/majiang-wt/n3-shuffle-binding` 执行。
测试计数顺序为 Total / Errors / Failed / Skipped / Not Run。

| 检查 | native exit | 结果 / 原始日志 |
|---|---:|---|
| solution Release 非增量构建 | 0 | 0 warning / 0 error；`solution-build.log` |
| TableRuntime.Tests Release 非增量构建 | 0 | 0 warning / 0 error；`runtime-build.log`；该项目不依赖 solution 自动调度 |
| Domain 全量 | 0 | 392 / 0 / 0 / 0 / 0；`domain-full.log` |
| Shuffle 旧四项 + 新七项 | 0 | 11 / 0 / 0 / 0 / 0；`shuffle-targeted.log` |
| AI / Contracts / Client / TableRuntime 全量 | 各 0 | 438 / 149 / 132 / 362 项，均零 error/failed/skipped/not-run；`ai-full.log`、`contracts-full.log`、`client-full.log`、`tableruntime-full.log` |
| 外部副本矩阵 | 0 | 33 场景、0 violations；3 正控制通过，30 负例均 Total7 / Failed>0 / native1，零 errors/skips/not-run；`matrix/` |
| 前轮独立复现原样复跑 | 0（脚本） | 四例均 Total11 / Failed1 / native1，零 errors/skips/not-run；`reviewer-probes/` |
| 既有 catalog verifier | 0 | fixtures5 / requirements73 / cases10382 / random10000 / facts42；`catalog.log`；这是既有目录口径，不是新增七项行为绑定的完整性证明 |

五个标准模块合计 **1473 passed / 0 failed / 0 skipped**。定向 11 项已包含于 Domain392，不重复加总。
30 个矩阵负例及四个历史复现是预期测试失败，不能记为产品失败或额外通过用例。
fixture SHA-256 前后未变：`386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`。

可复制命令（E 替换为上面的证据根；矩阵 OutDir 必须不存在）：

```powershell
$cli = 'D:/Unity/dotnet/dotnet.exe'
& $cli build dotnet/Mahjong.sln -c Release --no-incremental -v minimal
& $cli build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v minimal
foreach ($module in @('Domain','Ai','Contracts','Client','TableRuntime')) {
    & $cli "dotnet/tests/Mahjong.$module.Tests/bin/Release/net8.0/Mahjong.$module.Tests.dll" -noLogo
    if ($LASTEXITCODE -ne 0) { throw "$module native exit $LASTEXITCODE" }
}
& $cli dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo -class '*ShuffleGolden*'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-shuffle-binding.ps1 -RepoRoot D:/majiang-wt/n3-shuffle-binding -OutDir "$E/matrix-rerun"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$E/additional-probes.ps1" -RepoRoot D:/majiang-wt/n3-shuffle-binding -Evidence "$E/reviewer-probes-rerun"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot D:/majiang-wt/n3-shuffle-binding
```

独立证据清单 `E/SHA256-MANIFEST.csv` 的 SHA-256：`537459AD36A9B8D504039CAA2E9E31EEBC53C63F472EB53F7B61E47BDE912AAA`。
`E/review-summary.json` 保存数值退出码及状态；`E/repo-sha256.csv` 保存被审文件和 DLL 哈希。
首轮曾出现的样本/META 假绿与错误随机输入规格由本轮修正取代，不删除历史失败记录。

## 统一发布与未闭合边界

复用唯一 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，`feat/v5-1-integration -> v5`。
两轮交付与本调用者验收文档统一一次正常快进推送；本验收提交仅新增文档，生产和测试树与已验证交付相同。
本批其他已验收组件已在发布基线；物理绑定任务仍返修、出千处理器任务尚未验收、字段审计任务 failed，均排除在本次提交之外。
主 checkout 四份用户未提交文档在同步前后逐一比较 SHA-256，并从提交范围排除。

`threeCasterShapeBinding` 保持 **BLOCKED_RANDOM_INPUT**；`reversedEqualsThree` 保持 **BLOCKED_MISSING_REVERSED_WALL**。
零额度路径的合法输入是 vacuous trace，空 trace 按真实契约 `TraceExhausted`；不是新的生产缺陷。
G1/G5 未批准。PG/WSS/Unity/IL2CPP/Gradle/设备/物理网络/本次发布远端 CI 为 NOT_RUN，不能以本地结果代替。
