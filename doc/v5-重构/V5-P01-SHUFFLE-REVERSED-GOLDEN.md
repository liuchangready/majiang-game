# V5 P0.1 反向牌墙伴随证据（actual reversed wall companion）

日期：2026-10-25
Worktree：`D:/majiang-wt/shuffle-reversed-golden`
分支：`wt/v5-shuffle-reversed-golden`
冻结 V4 base：`03084f08af6e2ddefa2ebc6e094de5dac2474fd4`（唯一 PR19 已发布已接受头）
规则：V4 仍是唯一生产架构；G1 **BLOCKED**；G5 **NOT APPROVED**。

## 0. 结论与边界

本任务只补齐已接受 shuffle 报告明确列出的独立 P0.1 证据缺口：旧 `shuffle-v1.json` 只有
`reversedEqualsThree` 布尔，没有实际 reversed wall，阻塞
`BLOCKED_MISSING_REVERSED_WALL`。本轮真实调用**未修改的** V4
`ShuffleWallGenerator.generate`，对同一 32 个 seed/dealer/threePlans/config 分别以正序和
逆序 plans 执行，导出实际 `baseWall` / `threeCasterWall` / `reversedWall`，并由 C# 从两份
真实墙重新计算 `three == reversed`，同时逐字段绑定旧样本。

| 项目 | 状态 | 说明 |
|---|---|---|
| 实际 reversed wall 伴伴随文件 | **PASS（组件）** | 32 个真实 V4 正/逆序执行；新 schema/version |
| 旧 `shuffle-v1.json` 逐字节不变 | **PASS** | SHA-256 仍为 `386061B4...DAC282`，未刷新 |
| 旧样本 seed/dealer/plans/config/threeCasterWall 绑定 | **PASS（C# 运行时）** | 任一旧样本不符即 FAIL，不回写旧 fixture |
| `reversedEqualsThree` 由真实正逆序墙重新计算 | **PASS（伴随绑定）** | C# 重算并与新、旧布尔核对 |
| 136 唯一 / 3 赤五 / 死山 122..135 / 确定性 | **PASS** | exporter 与 C# 双侧真实断言 |
| `threeCasterShapeBinding` | **BLOCKED_RANDOM_INPUT** | 仍缺真实 V4 有序 typed 随机事件 |
| C# 三人 Shape 逐张非恒等输出差分 | **NOT_RUN / 不得声称** | 本任务不是 Shape 输出对齐 |
| V4 有序 typed 随机提取 | **NOT_IMPLEMENTED** | 不在本任务范围；责任在后续独立授权 |
| G1 | **BLOCKED** | 本组件不改变阶段门槛 |
| G5 | **NOT APPROVED** | 本组件不是迁移完成决策 |

本报告**不**声称完整跨运行时排序/随机/`threeCasterShapeBinding` PASS，也不改写旧历史验证结论。
旧 `SOURCE_ONLY` 缺口只由新伴随证据补齐；`threeCasterShapeBinding` 与 `BLOCKED_RANDOM_INPUT`
保持原状。

## 1. 交付文件与范围

仅新增以下 5 个文件；无生产 C#/Kotlin、旧 exporter、旧 fixture、旧消费者、catalog/manifest、
统一 verifier、项目/solution、Unity/`environment`/`Resources`/Burst 改动。

| 文件 | 作用 |
|---|---|
| `engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt` | 真实 V4 正/逆序导出器；默认验证不写盘 |
| `tests/v5-golden/shuffle-reversed-v1.json` | 新独立 schema 的 32 样本伴随文件 |
| `dotnet/tests/Mahjong.Domain.Tests/ShuffleReversedGoldenTests.cs` | C# 读取新伴随 + 旧 fixture，逐样本/逐墙/逐输入绑定 |
| `tools/v5-golden/probe-shuffle-reversed-binding.ps1` | 独占 TEMP 单字段负例矩阵 + 行尾/顺序正控制 + self-negative |
| `doc/v5-重构/V5-P01-SHUFFLE-REVERSED-GOLDEN.md` | 本报告 |

旧 fixture：

- 路径：`tests/v5-golden/shuffle-v1.json`
- 字节数：`99467`
- 原始工作树 SHA-256：`386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`
- LF-normalized SHA-256：`EB1FCCE7A11330721AFF5042C73E7140E229A815B83F8202DE3C17312788B4B4`
- 新旧逐字段比较：32/32 的 `dealerSeat`、`plans`、`baseWall`、`threeCasterWall` 与旧布尔全部一致。

最终交付范围仍是上述 5 个文件。第 1 轮 commit
`8734494936aad0d451511156a454b989d508468b` 建立这 5 个文件；第 2 轮返修 commit 只修改其中
C# 测试、探针与本文档 3 个文件，exporter 与 fixture 字节不变。准确最终 commit SHA 由调用者
交付回执记录，避免在文件内写入自引用哈希。

## 2. 字段级分类

新伴随文件 `schema = v5-shuffle-reversed-golden-v1`，与旧
`schema = v5-shuffle-golden-v1` 独立；旧文件不改。

| 字段/路径 | 分类 | 实际绑定与断言 |
|---|---|---|
| `schema`, `generatorVersion`, `rulesetVersion`, `extractionStatus` | META | 精确值；schema 必须为新独立版本 |
| `generator` | META | 必须是新 exporter 真实路径；C# 校验文件存在 |
| `v4BaseCommit`, `exportWorktreeHead` | META | 精确等于冻结 base `03084f08...`；只做声明相等，不声称 ancestry |
| `generatedByCommand` | META | 严格读取字符串并校验 exporter 类名、`V5_SHUFFLE_REVERSED_GOLDEN_EXPORT=1`、`:engine:test`、`--tests`、`--console=plain`；不执行该命令 |
| `exportedAtNote` | META | 明确真实 `generate` 调用、正逆序分别执行、无手填 |
| `v4ProductionSourceNote` | META | 严格读取字符串；必须包含 `engine/src/main/kotlin/com/majiang/stacking`、`engine/src/main/kotlin/com/majiang/model`、`V4 production sources`、冻结树声明与 source-hash 声明；对应目录真实存在；数字/null/空/无关声明均 FAIL |
| `relationNote` | META | 明确 `threeCasterWall` 与 `reversedWall` 是两次独立执行，布尔由两墙重算 |
| `scopeNote` | META | 明确不是 C# Shape 输出差分，且不清除 `BLOCKED_RANDOM_INPUT` |
| `generationBaselineNote` | META | 明确在冻结 base 之上导出，交付 commit 只加 test-only 文件 |
| `sourceHashAlgorithm`, `sourceHashNote` | META | `SHA-256`；明确 raw 与 LF-normalized 是两个域 |
| `sourceTree[]` | META / SOURCE | 恰好 10 个必需 role；role/path 精确；空、缺项、重复、错 role、错 path、错 hash 均 FAIL |
| `bindsOldFixture` | META / SOURCE | 旧 fixture 的 path/schema/v4Commit/caseCount/raw/LF hash/行尾；C# 真实重算 |
| `config` | INPUT / META | 与旧 `configSnapshot` 深度逐值相等，并与 C# `new ShuffleConfig(enabled:true)` 默认值逐项相等 |
| `cases[].seed` | INPUT / STRUCTURE | 精确集合 `0..31`、无重复、无缺失、无越界 |
| `cases[].dealerSeat` | INPUT / STRUCTURE | 等于 `seed % 4`，并等于旧样本 dealer |
| `cases[].plans` | INPUT / STRUCTURE | 精确 3 项；每个对象恰好 `seat`/`target` 两个唯一字段，属性顺序无关；seat `0/1/2`，target `KOKUSHI/DAISANGEN/CHINITSU_MAN`；额外字段、大小写替代、重复键、错类型/数量/枚举均 FAIL |
| `cases[].reversedPlans` | INPUT / STRUCTURE | 必须是 `plans` 的精确逆序；每个对象同样严格 `seat`/`target` 两个唯一字段；缺失/非逆序/额外字段/重复键均 FAIL |
| `cases[].baseWall` | OUTPUT | 真实 `generate(seed, dealer, emptyList, cfg)` 输出；136 唯一/范围/3 赤五 |
| `cases[].threeCasterWall` | OUTPUT | 真实 `generate(seed, dealer, threePlans, cfg)` 输出；与旧样本逐张相等 |
| `cases[].reversedWall` | OUTPUT | 真实 `generate(seed, dealer, threePlans.reversed(), cfg)` 输出；136 唯一/范围/3 赤五 |
| `cases[].redFiveCount` | INTERNAL_RELATION | 由真实墙重新计数；必须为 3 |
| `cases[].deadWallSegmentEqual` | INTERNAL_RELATION | 由 `baseWall[122..135] == threeCasterWall[122..135]` 重算 |
| `cases[].threeCasterChanged` | INTERNAL_RELATION | 由 `baseWall != threeCasterWall` 重算 |
| `cases[].reversedEqualsThree` | INTERNAL_RELATION（本伴随） | 由 `threeCasterWall == reversedWall` 重算，并与旧布尔核对 |
| `generationResult` | META | `cases=32`、`reversedEqualsThreeSeeds=32`、`threeCasterChangedSeeds=32`、`deadWallSegmentEqualSeeds=32`、`exporterTestResult=PASSED` |

### 2.1 历史 SOURCE_ONLY 与本次补齐

旧报告将 `reversedEqualsThree` 标为 `SOURCE_ONLY`，因为旧 fixture 只有布尔、没有
`reversedWall`，C# 无法重算。本任务新增伴随文件后：

- 旧 fixture 本身仍不被改写，旧历史结论仍按原记录理解；
- 新伴随文件提供 `reversedWall`，C# 从**两份真实墙**重算 `three == reversed`；
- 这是一种**伴随证据补齐**，不是把旧报告改写成“当年已经完成 C# 重算”。

### 2.2 BLOCKED 边界

`threeCasterShapeBinding` 仍 **BLOCKED_RANDOM_INPUT**。原因不是墙数据，而是基础墙之后
V4 的决策阶段消费有序 typed 随机事件（`nextInt(bound,result)` 与 `nextDouble(value)`），
当前 fixture 没有这些真实事件。本任务**没有**：

- 实现随机插桩、RNG 复刻、trace 猜测或从 seed/墙反推事件；
- 把 `threeCasterWall` 或 `reversedWall` 当作 C# `Shape` 的输出；
- 声称三人 C# Shape 逐张黄金对齐；
- 改变 `rulesetVersion`、旧错误码或旧消费者语义。

## 3. 真实 V4 生成与来源

### 3.1 真实调用

`V5ShuffleReversedGoldenExporterTest` 对 `seed = 0L..31L`、`dealer = seed % 4` 执行：

```text
base     = ShuffleWallGenerator.generate(seed, dealer, emptyList(), cfg)
three    = ShuffleWallGenerator.generate(seed, dealer, threePlans, cfg)
reversed = ShuffleWallGenerator.generate(seed, dealer, threePlans.reversed(), cfg)
```

其中 `cfg = ShuffleConfig(enabled = true)`，`threePlans` 精确为：

```text
0 -> KOKUSHI
1 -> DAISANGEN
2 -> CHINITSU_MAN
```

每个样本对同一输入再执行一次，逐字段断言确定性。对三面墙逐一断言：

- 136 张；
- 136 个唯一 tileId；
- 等于 `TilePool.buildStandardSet().toSet()`；
- 3 张赤五；
- `baseWall[122..135] == wall[122..135]`（死山隔离）。

并断言：

- `three == reversed`（V4 排序无关律，实际逐张比较）；
- `base != three`（32/32 实际发生交换）；
- `base[122..135] == three[122..135]`（死山段相等）。

默认运行只验证、**不写盘**；仅
`V5_SHUFFLE_REVERSED_GOLDEN_EXPORT=1`（或系统属性
`v5.shuffle.reversed.export=true`）写新伴随文件。导出模式还要求
`git rev-parse HEAD == 03084f08...`，防止在交付 commit 上静默改变 provenance。

### 3.2 来源集合与 SHA

`sourceTree` 恰好包含以下 10 个 role；C# 要求 role/path 精确、无重复、无缺项，并真实重算
LF-normalized 与 raw 两个域的 SHA-256：

| role | path | `sha256LfNormalized` |
|---|---|---|
| `v4_production_wall_generator` | `engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt` | `ee98ace333cdbdc8f76c6b17b8c7134a6cf5a0e5168adf34b4c532afd34b1dd1` |
| `v4_production_shuffle_slots` | `engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt` | `9386871529939dad346639a311dd84cc7645d8a26af9fb566247f7d4267d95a8` |
| `v4_production_shuffle_config` | `engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt` | `cc4b1f4718e2268a130e967a9921841fdb6a676808188242fa0791042369c0d0` |
| `v4_production_shuffle_targets` | `engine/src/main/kotlin/com/majiang/stacking/ShuffleTargets.kt` | `7140cc9530083f260f1c606fd56fbd92a33a417875d3e4f38f868accfa3cfa45` |
| `v4_production_shuffle_target` | `engine/src/main/kotlin/com/majiang/stacking/ShuffleTarget.kt` | `b094c50b98f6228a810ac99c10b7c316868a9430bf1d3bc0bb2161ccee35b6cc` |
| `v4_production_shuffle_selection` | `engine/src/main/kotlin/com/majiang/stacking/ShuffleSelection.kt` | `f07c1bfd5bfafe1e8e121a0163baaedc97837e08e9ac3e7bf62a204d558c32a5` |
| `v4_production_seat_shuffle_plan` | `engine/src/main/kotlin/com/majiang/stacking/SeatShufflePlan.kt` | `24a197ed51467422b590a2ea0a19d7b3fa011c465c5e9f0e5ed06e70e158a237` |
| `v4_production_tile_pool` | `engine/src/main/kotlin/com/majiang/model/TilePool.kt` | `43b2562949c569122d81434960fb1cb838f20f6425d8fd4dd9c0552d22122f5c` |
| `v4_production_tile` | `engine/src/main/kotlin/com/majiang/model/Tile.kt` | `6657290cc42efa0d3c58893ffd00d92b78e3d871ebfb426194caccd6770072b0` |
| `v4_test_only_exporter` | `engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt` | `8e3e3821d3f6adfb56116875d22dd1c6c6384a44a9613813171fea6a93bd9420` |

前 9 个生产文件在本工作树为 CRLF：`sha256Raw != sha256LfNormalized`，例如
`ShuffleWallGenerator.kt` raw 为
`ddcc157b80eb11407782ba03df73445fe9ac4ce12091509e39c12b48f983d7d9`。新增 exporter
为 LF：`sha256Raw == sha256LfNormalized`。C# 不假设当前 checkout 行尾：

- 始终校验 LF-normalized 摘要；
- 按记录的 `lineEnding` 重建 raw 字节并校验 raw 摘要；
- 只有当前 checkout 的行尾与记录一致时才额外比较实际 raw 摘要。

因此 Windows CRLF、干净 LF checkout 都能复跑，而内容变化仍会失败。

### 3.3 旧 fixture 绑定

`bindsOldFixture` 记录并校验：

- `path = tests/v5-golden/shuffle-v1.json`；
- `schema = v5-shuffle-golden-v1`；
- `v4Commit = 37b4dd610c5e7908dc4938378278c1475e0eee73`（40-hex，与旧文件一致）；
- `caseCount = 32`；
- raw SHA-256：`386061B4...DAC282`（CRLF 工作树字节）；
- LF-normalized SHA-256：`EB1FCCE7...B4B4`；
- `lineEnding = CRLF`。

C# 另外把这两个旧 fixture 摘要作为**历史完整性 pin**：`386061B4...` 与
`EB1FCCE7...`。它们是旧 fixture 的来源摘要，不是新墙/布尔输出 oracle；用于防止在新增
伴随文件时静默刷新旧样本。

## 4. C# 重新计算绑定

`ShuffleReversedGoldenTests` 共 4 个 Fact：

| Fact | 证明 |
|---|---|
| `CompanionSchemaSourceTreeAndOldFixtureProvenanceAreReal` | schema/version/冻结 base/命令/note；10 个 source role/path/hash 真实重算；旧 fixture hash/schema/commit/caseCount 真实 |
| `CompanionBindsEveryOldCaseSeedDealerPlansBaseAndThreeCasterWall` | 32 个精确 seed；dealer/plans/reversedPlans 输入规范；新 base/three 与旧样本逐张相等 |
| `ReversedWallIsConservedAndRecomputesReversedEqualsThreeFromBothFixtures` | 三面墙 136 唯一/范围/3 赤五；死山隔离；`three == reversed` 从两墙重算；与新旧布尔核对 |
| `CompanionConfigAndCountersMatchTheAcceptedV4InputContract` | config 与旧 snapshot 及 C# 默认值逐值相等；旧 fixture 仍 32/0..31/全 true；generationResult 计数一致 |

明确标注（文件 XML 注释与本节）：

- 这是 **V4 导出关系在 C# 中重新计算** 的绑定；
- **不是** C# Shape 三人非恒等输出差分；
- 旧 `SOURCE_ONLY` 缺失依赖只由新伴随证据补齐，不改写旧历史验证结论；
- `threeCasterShapeBinding` 仍 **BLOCKED_RANDOM_INPUT**。

严格解析：缺字段、错类型、错长度、非法枚举、重复 seed、非法 role、重复 source、单字段交换、
缺失 `reversedWall` 等都会抛出/断言失败，不会回退到合法默认值。

## 5. 外部探针矩阵

脚本：`tools/v5-golden/probe-shuffle-reversed-binding.ps1`
每个场景：

1. 新建独占 TEMP case root；
2. 只复制/变异新伴随 fixture，旧 fixture 原样复制；
3. 设置 `MAHJONG_REPO_ROOT` 指向该副本；
4. 执行真实 `Mahjong.Domain.Tests.dll -class '*ShuffleReversedGoldenTests*'`；
5. 检查实际 native exit 与 xUnit summary。

固定检查：

- `Total` 等于 baseline `Total=4` 且 `>0`；
- `Errors=0`、`Skipped=0`、`Not Run=0`；
- 正控制：exit 0、Failed 0；
- 负例：exit 非 0、Failed > 0；
- 日志出现 abort/no-match/unhandled 等即 violation。

矩阵：**76 场景 = 5 正控制 + 71 负例**，0 violations。正控制：

- `baseline`；
- `legal-case-order`（case 顺序逆排仍绿）；
- `legal-crlf`（新 fixture 用 CRLF 仍绿）；
- `legal-plan-property-order`（plan 对象 target 在 seat 前仍绿）；
- `legal-reversed-plan-property-order`（reversed plan 对象属性顺序互换仍绿）。

重复键场景直接对真实 JSON 文本注入第二份 `seat`/`target`，不经过
`ConvertFrom-Json`/`ConvertTo-Json`；这样 `JsonDocument.EnumerateObject()` 能看到两个键，
`AssertExactProperties` 才能真实拒绝。证据中的 `plan-duplicate-seat.shuffle-reversed-v1.json`
确实含 `"seat": 9, "seat": 0`，`reversed-plan-duplicate-target...` 确实含
`"target": "INVALID", "target": "CHINITSU_MAN"`。

负例族：

| 族 | 代表场景 |
|---|---|
| INPUT | duplicate/out-of-range seed、missing case、wrong dealer、unknown/duplicate plan、非逆序 reversedPlans、config 错误/缺失 |
| PLAN_OBJECT | plan/reversed plan 额外字段、重复 seat/target（非法→合法、合法→非法、相同值重复）、缺字段、seat/target 错类型、plan 数量 2/4 |
| OUTPUT | base/three/reversed 单字段交换、长度 135/137、tile 999、字符串值 |
| STRUCTURE | 缺 `threeCasterWall`/`reversedWall`、extra case/top field、数量 31/33、bool/seed 错类型 |
| INTERNAL_RELATION | `reversedEqualsThree=false`、`threeCasterChanged=false`、`redFiveCount=2`、`deadWallSegmentEqual=false` |
| META | schema/generator/base commit、`v4ProductionSourceNote` 数字/null/空/无关、`generatedByCommand` 错误/缺失、generation count 错误 |
| SOURCE | sourceTree 空/缺项/重复/错 role/错 path/错 LF hash/错 raw hash/错 lineEnding、旧 fixture hash/caseCount/commit 错误 |

self-negative：`-SelfNegative` 注入一个合法 no-op 副本但标记为 `Fail`。同一 checker 实际观察到
`exit 0 / Failed 0` 并记为
`VIOLATION: expected non-zero native exit; expected Failed > 0`，脚本 exit **1**。这不是无条件
exit 1，也没有修改生产源或放宽 verifier。扩展矩阵的 self-negative 为 77 场景，仍只有该 1 个
预期 violation。

调用者独立脚本 `reviewer-strictness.ps1` 的 9 个场景（baseline + 8 个此前意外绿负例）在修复后：
baseline `Total4/Errors0/Failed0/Skipped0/NotRun0/native0`；8 个负例均
`Total4/Errors0/Failed>0/Skipped0/NotRun0/native1`。

## 6. 实测命令、native exit 与真实计数

CLI：`D:/Unity/dotnet/dotnet.exe`（SDK `8.0.426`）。Gradle launcher JVM 为
`25.0.2`（JetBrains JBR），Kotlin/Java **compile target 为 JVM 17**；两者不同，已按实际记录。
Shell：Windows PowerShell `5.1.19041.6456`；OS：Windows 10.0.19045。

证据根 E：`D:/majiang-wt/shuffle-reversed-golden-evidence-20261025`；本轮返修日志在
`E/rework-r1/`。计数顺序：Total / Errors / Failed / Skipped / Not Run。

| 检查 | native exit | 结果 / 原始日志 |
|---|---:|---|
| Kotlin 新 exporter + 既有 Shuffle 验收 | 0 | 17 项（新 1 + `ShuffleAlgorithmAcceptanceTest` 15 + `ShuffleAtomicDealAcceptanceTest` 1），0 fail；`rework-r1/kotlin-shuffle-verify.log` |
| 默认 exporter 不写盘 | 0 | fixture hash 前后均为 `5A5EA43F...B619`；`rework-r1/kotlin-shuffle-verify.log` |
| solution Release `--no-incremental` | 0 | 0 warning / 0 error；`rework-r1/solution-build.log` |
| TableRuntime.Tests Release `--no-incremental` | 0 | 0 warning / 0 error；`rework-r1/tableruntime-build.log` |
| Domain 全量 | 0 | 496 / 0 / 0 / 0 / 0；`rework-r1/domain-full.log` |
| AI 全量 | 0 | 438 / 0 / 0 / 0 / 0；`rework-r1/ai-full.log` |
| Contracts 全量 | 0 | 149 / 0 / 0 / 0 / 0；`rework-r1/contracts-full.log` |
| Client 全量 | 0 | 132 / 0 / 0 / 0 / 0；`rework-r1/client-full.log` |
| TableRuntime 全量 | 0 | 365 / 0 / 0 / 0 / 0；`rework-r1/tableruntime-full.log` |
| 旧 shuffle 定向 | 0 | 22 / 0 / 0 / 0 / 0；`rework-r1/shuffle-legacy-targeted.log` |
| typed shuffle 定向 | 0 | 9 / 0 / 0 / 0 / 0；`rework-r1/shuffle-typed-targeted.log` |
| 新 reversed 定向 | 0 | 4 / 0 / 0 / 0 / 0；`rework-r1/shuffle-reversed-targeted.log` |
| 外部矩阵正常模式 | 0 | 76 场景，baseline Total4，0 violations；`rework-r1/probe-final/` |
| 外部矩阵 self-negative | **1（预期）** | 77 场景，1 个预期 unexpected-green violation；`rework-r1/probe-final-self-negative/` |
| 调用者 reviewer 脚本 | 0（脚本自身） | baseline native0/Total4/Errors0/Failed0/Skipped0/NotRun0；8 负例均 native1/Total4/Errors0/Failed>0/Skipped0/NotRun0；`codex-.../rework-r1-final/strictness-results.json` |

五模块合计 **1580 passed / 0 failed / 0 skipped / 0 not-run**。定向 22/9/4 已包含在
Domain 496 内，不重复加总。无 skip、空测试、`Total=0`、旧 DLL 或编译失败掩盖；所有
C# 运行都使用本轮非增量构建产物。Kotlin 首次 daemon 连接失败后由 Gradle 非 daemon 回退编译，
同一命令最终 exit0、17 项全绿；不是测试跳过。

### 6.1 环境与启动器差异

| 项目 | 实际值 |
|---|---|
| Gradle | 9.7.1 |
| Gradle launcher JVM | 25.0.2（JetBrains JBR） |
| Kotlin/Java compile target | JVM 17 |
| .NET SDK | 8.0.426 |
| PowerShell | 5.1.19041.6456 |
| OS | Windows 10.0.19045 amd64 |

## 7. 哈希

### 7.1 交付与 fixture

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `tests/v5-golden/shuffle-reversed-v1.json` | 75847 | `5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619` |
| `tests/v5-golden/shuffle-v1.json`（未改） | 99467 | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` |
| `engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt` | 23341 | `8E3E3821D3F6ADFB56116875D22DD1C6C6384A44A9613813171FEA6A93BD9420` |
| `dotnet/tests/Mahjong.Domain.Tests/ShuffleReversedGoldenTests.cs` | 46804 | `0035BC592A89DED23FBEF3552C193EFD5318E9DC925F562E29F8E4DC635B5D74` |
| `tools/v5-golden/probe-shuffle-reversed-binding.ps1` | 37414 | `3E054BE213DA0824A05114F1B451A81F8FD2F92FA8841B92C6F9CE1D4BE2AEBD` |

### 7.2 关键日志

本轮返修日志位于 `E/rework-r1/`；上一轮日志保留在 E 根，不覆盖。

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `rework-r1/kotlin-shuffle-verify.log` | 42426 | `F6BF7CC10DD37CE5B77F87289035D921CA5FDE7527A72186CAD846A7703CB6E6` |
| `rework-r1/solution-build.log` | 3564 | `73A2F5E772FA6AC0DDBDCB0D6AABCBA62AB78759B1EEAA07B4C985FC44D8F75E` |
| `rework-r1/tableruntime-build.log` | 2146 | `B322D29CBE86EFB22E1711B5F71D1C61FEC8B8DF25E8D58A6727473FC50C7ABB` |
| `rework-r1/domain-full.log` | 958 | `C0EAEB01126C17F5F8B55B3D2E219E803E9DE957FBEC58777F0B35CB261E00C3` |
| `rework-r1/ai-full.log` | 664 | `31FB952621A3FC0DDCD2460E6F04B4B4BEF9E22C34A6A763E2753AA78973A40E` |
| `rework-r1/contracts-full.log` | 734 | `4E109830B93414A34A9E1634CE22FC2F58DE895E621F977E9FD417F696F22E4F` |
| `rework-r1/client-full.log` | 704 | `789635E5C506FF3A9C15B2CF15BE2C54651CD32FDC8481BB39176421EAF78916` |
| `rework-r1/tableruntime-full.log` | 764 | `88FC82F23B8DB263C395983E7170BE058BDE18D98CE40A7B6FCE2B53EA63F097` |
| `rework-r1/shuffle-legacy-targeted.log` | 702 | `4765FAB4F7C917AC36DECC2B226F27DA677D7F21908AF838A16DB079956CEE01` |
| `rework-r1/shuffle-typed-targeted.log` | 700 | `4FAB803DC37A03606A1129196AB0E26E59CF3057C40E19568155A738293DB29B` |
| `rework-r1/shuffle-reversed-targeted.log` | 700 | `3A27A26A73B9A9337D279C58C5A00D283F6939911B9AC344AA1191D0FA247A22` |
| `rework-r1/probe-final-console.log` | 17652 | `A79C11F9208773F7BFF4F9A7C0A9BA2A7E8B113C56C979FA938A5A770D05E923` |
| `rework-r1/probe-final-self-negative-console.log` | 22178 | `0339255D8EAC5EC954D5BA136163A4EF4DDEEB8B358BFEE9CFA82E88B7F0630B` |
| `codex-.../rework-r1-final/strictness-results.json` | — | `4CA5900D3EE44291B75A123BBBF2B2E4DE0FBADB55C70A705930B9263CE86CD7` |

`reviewer-strictness.ps1` 本身 SHA-256 仍为
`45A24083D4EDCE029B09FD5CA15F829B591EFA348FBA211880863DD6D4203E8D`，未被修改。

E/rework-r1 下另有 `probe-final/` 与 `probe-final-self-negative/` 的逐场景原始日志、变异
fixture 副本、CSV 和 `probe-summary.json`；完整 SHA-256 清单见 E 根 `SHA256-MANIFEST.csv`。

## 8. 限制、未运行项与后续责任

### 8.1 未运行 / 不冒充

| 项目 | 状态 |
|---|---|
| PG / PostgreSQL | **NOT_RUN** |
| WSS / TLS / 真实网络 | **NOT_RUN** |
| Unity Editor / IL2CPP / Android / iOS / 真机 | **NOT_RUN** |
| Gradle Android/桌面实机、物理网络 | **NOT_RUN** |
| 远端 CI（包括 caller 提供的 push/PR 运行） | **NOT_RUN（本新代码）** |
| 旧 Client `39970580589` 根因 | **UNRESOLVED**（不在本任务范围） |

调用者已下载审核的远端 push `40171976573` / PR `40171981239`（1745/0/0）以及 PR 实际
merge `a89352e0` 不是 sourcehead，这些是 caller 上下文，**不代替**本工作树对新代码的本地验证；
本报告所有数字均来自冻结 base `03084f0` + 本轮 5 个文件的实际本地执行。旧 Client
`39970580589` 根因仍未解决，不能据此声称 Client 全绿。

### 8.2 后续真实 V4 有序 typed 随机提取（未执行）

要解除 `threeCasterShapeBinding` 的 **BLOCKED_RANDOM_INPUT**，仍需单独授权的 V4 侧工作：

1. 在 `ShuffleWallGenerator.kt:63` 引入 test-only `Random`/`RandomSource` 记录 seam；生产默认
   仍 `Random(seed)`，不改规则/版本。
2. 只记录基础墙形成之后的真实调用：
   `{kind:"nextInt",bound,result}` 与 `{kind:"nextDouble",value}`，严格保序；基础
   `wall.shuffle(rng)` 已由 `baseWall` 记录，不混入事件流。
3. exporter 为 32 case 增加 `threeCasterRandomEvents`（或等价有序事件流 + 明确适配），
   不改旧 fixture 字段语义。
4. C# 用已接受的 `ShapeWithTypedRandomTrace` 读取真实事件，32 cases × 136 slots 逐张比较；
   截断/多余/kind/bound 错必须失败。
5. 同时复核 136 唯一、3 赤五、死山 122..135；在此之前不能声称 Shape 黄金一致。

本任务**没有**实现上述 seam，也没有手填/反推任何随机事件。

### 8.3 历史与目录

旧 `shuffle-v1.json` 未改；历史 fixture、catalog/manifest、统一 verifier 的统计与结论不因本任务
更新。本任务只把“缺少实际 reversed wall 证据”这一个组件按实测判定为补齐；不把组件 PASS
写成完整 N3、G1 或 G5 完成。

## 9. 复跑命令

```powershell
$R = 'D:/majiang-wt/shuffle-reversed-golden'
$E = 'D:/majiang-wt/shuffle-reversed-golden-evidence-20261025'
$CLI = 'D:/Unity/dotnet/dotnet.exe'

# 仅当需要重新导出时：必须在冻结 base 03084f0 之上，且显式设置环境变量。
$env:V5_SHUFFLE_REVERSED_GOLDEN_EXPORT = '1'
& "$R/gradlew.bat" :engine:test `
  --tests "com.majiang.v3.V5ShuffleReversedGoldenExporterTest" `
  --rerun-tasks --console=plain
Remove-Item Env:V5_SHUFFLE_REVERSED_GOLDEN_EXPORT

# 默认验证（不写盘）+ 既有 Kotlin shuffle 验收
& "$R/gradlew.bat" :engine:test `
  --tests "com.majiang.v3.V5ShuffleReversedGoldenExporterTest" `
  --tests "com.majiang.v3.ShuffleAlgorithmAcceptanceTest" `
  --tests "com.majiang.v3.ShuffleAtomicDealAcceptanceTest" `
  --rerun-tasks --console=plain

& $CLI build "$R/dotnet/Mahjong.sln" -c Release --no-incremental -v minimal
& $CLI build "$R/dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj" `
  -c Release --no-incremental -v minimal

foreach ($module in @('Domain','Ai','Contracts','Client','TableRuntime')) {
    & $CLI "$R/dotnet/tests/Mahjong.$module.Tests/bin/Release/net8.0/Mahjong.$module.Tests.dll" -noLogo
    if ($LASTEXITCODE -ne 0) { throw "$module native exit $LASTEXITCODE" }
}

& $CLI "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" `
  -noLogo -class '*ShuffleReversedGoldenTests*'

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$R/tools/v5-golden/probe-shuffle-reversed-binding.ps1" `
  -RepoRoot $R -Dotnet $CLI -OutDir "$E/probe-final-rerun"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$R/tools/v5-golden/probe-shuffle-reversed-binding.ps1" `
  -RepoRoot $R -Dotnet $CLI -OutDir "$E/probe-self-negative-rerun" -SelfNegative
# 上一条必须 native exit 1，且 self-negative-legal-control 为意外绿 violation。

# 调用者独立 strictness 脚本（Evidence 目录必须先创建；脚本自身 exit0，结果看 9 行 JSON）
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "D:/majiang-wt/codex-shuffle-reversed-review-1792915003211/reviewer-strictness.ps1" `
  -RepoRoot $R -Evidence "D:/majiang-wt/codex-shuffle-reversed-review-1792915003211/rework-rerun"
# 期望：baseline native0/Failed0；8 个负例 native1/Total4/Failed>0/Skipped0/NotRun0。
```

外部矩阵 OutDir 必须不存在；脚本会拒绝复用。探针只写独占 TEMP 与 OutDir，不改仓库 fixture
或生产源。

## 10. 状态 ledger

| 项目 | 状态 |
|---|---|
| 实际 reversed wall 32 样本伴随 | **PASS（组件）** |
| 旧 fixture 逐字节不变 | **PASS** |
| 旧样本 32 seed/dealer/plans/config/base/three 绑定 | **PASS（C#）** |
| `reversedEqualsThree` 从真实正逆序墙重算 | **PASS（伴随绑定）** |
| 136 唯一 / 3 赤五 / 死山隔离 / 确定性 | **PASS** |
| 单字段负例/结构/来源/行尾/顺序探针 | **PASS**（76 场景，0 violations） |
| self-negative checker | **PASS（实际 exit 1，预期 violation）** |
| 调用者 8 个意外绿复现 | **PASS（修复后全部 native1/Failed>0）** |
| `threeCasterShapeBinding` | **BLOCKED_RANDOM_INPUT** |
| 真实 V4 有序 typed 随机提取 | **NOT_IMPLEMENTED** |
| C# 三人 Shape 逐张输出差分 | **NOT_RUN / 不得声称** |
| G1 | **BLOCKED** |
| G5 | **NOT APPROVED** |
| PG / WSS / Unity / IL2CPP / 设备 / 远端 CI | **NOT_RUN** |
| 旧 Client `39970580589` | **UNRESOLVED** |

本任务不宣布 G1/G5 完成，不创建 `complete.flag`，不 push、不建 PR、不触发远端 CI、不改默认分支。

## 11. 第 2 轮返修记录（D-R1-01 / D-R1-02）

第 1 轮交付 commit 为 `8734494936aad0d451511156a454b989d508468b`。本轮在同一 thread、
同一 receiver/native session/model 内修复两条 MAJOR；返修 commit 是本文件所在的后续本地
commit，准确 SHA 由调用者回执记录。

### 11.1 D-R1-01：plan 对象字段清单与重复键

- 位置：`ShuffleReversedGoldenTests.cs` 的 `ParsePlans`；探针矩阵。
- 原缺陷：只 `GetProperty` 读取 `seat`/`target`，未校验每个 plan 对象的字段清单；
  `plan-extra-field`、`plan-duplicate-seat`、`reversed-plan-extra-field`、
  `reversed-plan-duplicate-target` 均意外 native0/Failed0。
- 修复：`ParsePlans` 对每个对象先断言 `ValueKind=Object`，再调用
  `AssertExactProperties(plan, ["seat","target"])`；`EnumerateObject()` 保留重复键，
  因此第二份 `seat`/`target` 不能藏在 `GetProperty` 的最后值后面。属性顺序无关。
- 探针：正控制共 5 个（本轮新增 plan/reversed-plan 属性顺序 2 个）和新增 17 个 plan 负例；重复键由
  `RawMutation` 直接写真实 JSON 文本，不经 `ConvertFrom-Json`/`ConvertTo-Json`。
- 验收：reviewer 的 4 个 plan 场景修复后均 native1/Total4/Errors0/Failed2/Skipped0/NotRun0；
  扩展矩阵 `plan-*`/`reversed-plan-*` 全部 FAIL；`legal-plan-property-order` 与
  `legal-reversed-plan-property-order` 保持 native0/Failed0。

### 11.2 D-R1-02：未读取的 V4 来源 META

- 位置：`ShuffleReversedGoldenTests.cs` 的 provenance Fact；报告 META 口径。
- 原缺陷：`v4ProductionSourceNote` 只出现在 `TopLevelFields`，从未 `Str` 读取；
  `meta-number`、`meta-null`、`meta-empty`、`meta-unrelated` 均意外 native0/Failed0。
- 修复：严格 `Str` 读取并断言：
  `engine/src/main/kotlin/com/majiang/stacking`、
  `engine/src/main/kotlin/com/majiang/model`、
  `V4 production sources`、`v4BaseCommit is the frozen tree at export time`、
  `source hashes below bind the exact working-tree bytes`；两个目录必须真实存在。
  同时补上 `generatedByCommand` 的真实命令片段校验（export 变量、`:engine:test`、
  `--tests`、exporter 类名、`--console=plain`），并新增 command 负例，使报告口径与实际
  场景一致。
- 验收：reviewer 的 4 个 META 场景修复后均 native1/Total4/Errors0/Failed1/Skipped0/NotRun0；
  扩展矩阵的 `meta-source-note-*`、`meta-command-wrong`、`meta-command-missing` 全部 FAIL；
  真实 fixture 与来源 SHA 校验继续绿。

### 11.3 复跑结果

- 调用者 `reviewer-strictness.ps1`（SHA-256 `45A24083...203E8D`）：
  baseline native0/Total4/Failed0；8 个负例全部 native1/Total4/Errors0/Failed>0/
  Skipped0/NotRun0。新 `strictness-results.json` SHA-256
  `4CA5900D3EE44291B75A123BBBF2B2E4DE0FBADB55C70A705930B9263CE86CD7`；
  原始 `C1EBBD0A...BD5E` 保留在 E 根，不覆盖。
- 扩展矩阵：76 场景 = 5 正控制 + 71 负例，0 violations；self-negative 77 场景，
  exit1，仅 `self-negative-legal-control` 一行异常，对应原始 JSON 中 2 条条件违反
  （合法控制实际 exit0 且 Failed0）。
- fresh solution/TableRuntime 构建均 0 warning/0 error；五模块 1580/0/0/0/0；
  Kotlin 17/0/0/0；新 reversed 定向 4/0/0/0/0。
- 仍未改变：旧 fixture `386061B4...`、新 fixture `5A5EA43F...`、exporter `8E3E3821...`；
  `BLOCKED_RANDOM_INPUT`、G1 BLOCKED、G5 NOT APPROVED 均保持。
