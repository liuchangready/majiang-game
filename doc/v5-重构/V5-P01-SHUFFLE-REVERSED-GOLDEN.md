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

本文件所在的本地最小 commit 只包含上述 5 个文件；准确 commit SHA 由调用者交付回执记录，
避免在文件内写入自引用哈希。

## 2. 字段级分类

新伴随文件 `schema = v5-shuffle-reversed-golden-v1`，与旧
`schema = v5-shuffle-golden-v1` 独立；旧文件不改。

| 字段/路径 | 分类 | 实际绑定与断言 |
|---|---|---|
| `schema`, `generatorVersion`, `rulesetVersion`, `extractionStatus` | META | 精确值；schema 必须为新独立版本 |
| `generator` | META | 必须是新 exporter 真实路径；C# 校验文件存在 |
| `v4BaseCommit`, `exportWorktreeHead` | META | 精确等于冻结 base `03084f08...`；只做声明相等，不声称 ancestry |
| `generatedByCommand` | META | 包含真实 exporter 类名；C# 不执行该命令 |
| `exportedAtNote` | META | 明确真实 `generate` 调用、正逆序分别执行、无手填 |
| `v4ProductionSourceNote` | META | 明确 V4 stacking/model 生产源与冻结树 |
| `relationNote` | META | 明确 `threeCasterWall` 与 `reversedWall` 是两次独立执行，布尔由两墙重算 |
| `scopeNote` | META | 明确不是 C# Shape 输出差分，且不清除 `BLOCKED_RANDOM_INPUT` |
| `generationBaselineNote` | META | 明确在冻结 base 之上导出，交付 commit 只加 test-only 文件 |
| `sourceHashAlgorithm`, `sourceHashNote` | META | `SHA-256`；明确 raw 与 LF-normalized 是两个域 |
| `sourceTree[]` | META / SOURCE | 恰好 10 个必需 role；role/path 精确；空、缺项、重复、错 role、错 path、错 hash 均 FAIL |
| `bindsOldFixture` | META / SOURCE | 旧 fixture 的 path/schema/v4Commit/caseCount/raw/LF hash/行尾；C# 真实重算 |
| `config` | INPUT / META | 与旧 `configSnapshot` 深度逐值相等，并与 C# `new ShuffleConfig(enabled:true)` 默认值逐项相等 |
| `cases[].seed` | INPUT / STRUCTURE | 精确集合 `0..31`、无重复、无缺失、无越界 |
| `cases[].dealerSeat` | INPUT / STRUCTURE | 等于 `seed % 4`，并等于旧样本 dealer |
| `cases[].plans` | INPUT / STRUCTURE | 精确 3 项，seat `0/1/2`，target `KOKUSHI/DAISANGEN/CHINITSU_MAN` |
| `cases[].reversedPlans` | INPUT / STRUCTURE | 必须是 `plans` 的精确逆序；缺失/非逆序 FAIL |
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

矩阵：**52 场景 = 3 正控制 + 49 负例**，0 violations。正控制：

- `baseline`；
- `legal-case-order`（case 顺序逆排仍绿）；
- `legal-crlf`（新 fixture 用 CRLF 仍绿）。

负例族：

| 族 | 代表场景 |
|---|---|
| INPUT | duplicate/out-of-range seed、missing case、wrong dealer、unknown/duplicate plan、非逆序 reversedPlans、config 错误/缺失 |
| OUTPUT | base/three/reversed 单字段交换、长度 135/137、tile 999、字符串值 |
| STRUCTURE | 缺 `threeCasterWall`/`reversedWall`、extra case/top field、数量 31/33、bool/seed 错类型 |
| INTERNAL_RELATION | `reversedEqualsThree=false`、`threeCasterChanged=false`、`redFiveCount=2`、`deadWallSegmentEqual=false` |
| META | schema/generator/base commit/command 错误、generation count 错误 |
| SOURCE | sourceTree 空/缺项/重复/错 role/错 path/错 LF hash/错 raw hash/错 lineEnding、旧 fixture hash/caseCount/commit 错误 |

self-negative：`-SelfNegative` 注入一个合法 no-op 副本但标记为 `Fail`。同一 checker 实际观察到
`exit 0 / Failed 0` 并记为
`VIOLATION: expected non-zero native exit; expected Failed > 0`，脚本 exit **1**。这不是无条件
exit 1，也没有修改生产源或放宽 verifier。

## 6. 实测命令、native exit 与真实计数

CLI：`D:/Unity/dotnet/dotnet.exe`（SDK `8.0.426`）。Gradle launcher JVM 为
`25.0.2`（JetBrains JBR），Kotlin/Java **compile target 为 JVM 17**；两者不同，已按实际记录。
Shell：Windows PowerShell `5.1.19041.6456`；OS：Windows 10.0.19045。

证据根 E：`D:/majiang-wt/shuffle-reversed-golden-evidence-20261025`。
计数顺序：Total / Errors / Failed / Skipped / Not Run。

| 检查 | native exit | 结果 / 原始日志 |
|---|---:|---|
| Kotlin 新 exporter + 既有 Shuffle 验收 | 0 | 17 项（新 1 + `ShuffleAlgorithmAcceptanceTest` 15 + `ShuffleAtomicDealAcceptanceTest` 1），0 fail；`kotlin-shuffle-verify.log` |
| 默认 exporter 不写盘 | 0 | fixture hash 前后均为 `5A5EA43F...B619`；`kotlin-shuffle-verify.log` |
| solution Release `--no-incremental` | 0 | 0 warning / 0 error；`solution-build.log` |
| TableRuntime.Tests Release `--no-incremental` | 0 | 0 warning / 0 error；`tableruntime-build.log` |
| Domain 全量 | 0 | 496 / 0 / 0 / 0 / 0；`domain-full.log` |
| AI 全量 | 0 | 438 / 0 / 0 / 0 / 0；`ai-full.log` |
| Contracts 全量 | 0 | 149 / 0 / 0 / 0 / 0；`contracts-full.log` |
| Client 全量 | 0 | 132 / 0 / 0 / 0 / 0；`client-full.log` |
| TableRuntime 全量 | 0 | 365 / 0 / 0 / 0 / 0；`tableruntime-full.log` |
| 旧 shuffle 定向 | 0 | 22 / 0 / 0 / 0 / 0；`shuffle-legacy-targeted.log` |
| typed shuffle 定向 | 0 | 9 / 0 / 0 / 0 / 0；`shuffle-typed-targeted.log` |
| 新 reversed 定向 | 0 | 4 / 0 / 0 / 0 / 0；`shuffle-reversed-targeted.log` |
| 外部矩阵正常模式 | 0 | 52 场景，baseline Total4，0 violations；`probe-final/` |
| 外部矩阵 self-negative | **1（预期）** | 53 场景，1 个预期 unexpected-green violation；`probe-self-negative-final/` |

五模块合计 **1580 passed / 0 failed / 0 skipped / 0 not-run**。定向 22/9/4 已包含在
Domain 496 内，不重复加总。无 skip、空测试、`Total=0`、旧 DLL 或编译失败掩盖；所有
C# 运行都使用本轮非增量构建产物。

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
| `dotnet/tests/Mahjong.Domain.Tests/ShuffleReversedGoldenTests.cs` | 44817 | `699FC6A1B48FF525E405E709A3BD6BF091D19D22A1CA523CEE976D70045A8ECB` |
| `tools/v5-golden/probe-shuffle-reversed-binding.ps1` | 28371 | `F0B93E76D8C3DB667D1347832B8A4A4CFE6970C5DC6717BA5611D9424DD6AB33` |

### 7.2 关键日志

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `kotlin-shuffle-verify.log` | 29614 | `8BE15B81E9BD1A1BE7375E03707038C11E0B8F28077E5D100AA55EF02E7755B6` |
| `solution-build.log` | 3564 | `24C3C178F55AEAF5856D25D9D11E9F48AD8DD562AC8E2FE7C9BCECFFF06F2E45` |
| `tableruntime-build.log` | 2146 | `76CD7F6ECA3DA067DCBCFBC6F776E142252D6874E800B6BFD793E58F321C24D9` |
| `domain-full.log` | 958 | `404AA7740D33ACF31262EB15AE6C17FB70FD8DE520C1A59D53256CE0BFCFB462` |
| `ai-full.log` | 664 | `51538FBE76F1A0A7A3691B5A290F50C46B88A139C59A184522BAFC0CDE76D724` |
| `contracts-full.log` | 734 | `907765E6B16495AB6C7D042F558679B0D5D127EFFE368607E9764038D2658867` |
| `client-full.log` | 704 | `E230EE52D8081E705F502F15B2292AD6C2F12A7CB262659A233BA0A8EAD666A5` |
| `tableruntime-full.log` | 764 | `B12943ECBE84DB10C63A4C123A25B816FAC63FCB9849AA3E6FADB2F39A5060AE` |
| `shuffle-legacy-targeted.log` | 702 | `EB712613069E86E4D43F577788D323CFFF102101472D27684CF214B01ACED721` |
| `shuffle-typed-targeted.log` | 700 | `266B6432C652C6D4D11850C1B3ECD5F0FA3C82D81BD8D4511FA590C50600B8BB` |
| `shuffle-reversed-targeted.log` | 700 | `C28267D0DF931380CB04CA48B1968B938D6B310DB792B15E144921681CFB6487` |
| `probe-final-console.log` | 10980 | `E6927C62AA33A2C717EF2297F7E7372132993FF24C305319DA43DE580FAFAC22` |
| `probe-self-negative-final-console.log` | 16370 | `5F2B32874440C5550FF0A91872D4AB77D438ECD3FE94752D4AA6807BE1512B9B` |

E 下另有 `probe-final/` 与 `probe-self-negative-final/` 的逐场景原始日志、变异 fixture 副本、
CSV 和 `probe-summary.json`；完整 SHA-256 清单见 E 根 `SHA256-MANIFEST.csv`。

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
| 单字段负例/结构/来源/行尾/顺序探针 | **PASS**（52 场景，0 violations） |
| self-negative checker | **PASS（实际 exit 1，预期 violation）** |
| `threeCasterShapeBinding` | **BLOCKED_RANDOM_INPUT** |
| 真实 V4 有序 typed 随机提取 | **NOT_IMPLEMENTED** |
| C# 三人 Shape 逐张输出差分 | **NOT_RUN / 不得声称** |
| G1 | **BLOCKED** |
| G5 | **NOT APPROVED** |
| PG / WSS / Unity / IL2CPP / 设备 / 远端 CI | **NOT_RUN** |
| 旧 Client `39970580589` | **UNRESOLVED** |

本任务不宣布 G1/G5 完成，不创建 `complete.flag`，不 push、不建 PR、不触发远端 CI、不改默认分支。
