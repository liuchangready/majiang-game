# V5 N3 有序 typed 随机输入 seam（D-R1-02）

状态：**组件级 PASS**；`threeCasterShapeBinding` 仍 **BLOCKED_RANDOM_INPUT**，`reversedEqualsThree` 仍
**BLOCKED_MISSING_REVERSED_WALL**，G1 仍 **BLOCKED**，G5 **NOT APPROVED**。V4 仍是唯一生产架构。

日期：2026-10-25
Worktree：`D:/majiang-wt/shuffle-typed-random`
分支：`wt/v5-shuffle-typed-random`
冻结基线：`c5485884eac21f04874db19c1d64619ffa58de60`（唯一 PR19 已发布已接受头）
交付：本文件所在本地最小 commit；最终 commit SHA 由调用者交付回执记录，避免在文件内写入自引用哈希。

## 0. 结论与边界

P1.5 的记录输入迁移已接受；本任务只补齐该迁移的前置输入 seam，不进入 P4/P5 后阶段。
本组件关闭 D-R1-02 定义的**前置 seam**：基础 136 墙形成之后，C# 现在可以按真实顺序记录并消费
`nextInt(bound, result)` 与 `nextDouble(value)`，而不是把前者压成 `floor(double * bound)`。

必须明确区分：

| 项目 | 状态 | 说明 |
|---|---|---|
| typed 记录 / 合成入口 / 错误失败 | **PASS** | 本报告第 2–5 节；真实公开入口与负例矩阵 |
| 旧 double-only 入口结果/失败语义 | **PASS** | 共享算法 + `DoubleTraceReader` 保持 floor；既有 11 项 shuffle 测试与全量 Domain 通过 |
| C# typed/legacy 兼容性 | **PASS（C# 自测）** | 同一 C# 算法、同输入逐张一致；**不是 V4 黄金** |
| 真实 V4 事件提取并跨运行时逐墙对齐 | **BLOCKED_RANDOM_INPUT** | 未授权/未实现 V4 记录 seam；本组件不宣称 |
| `threeCasterShapeBinding` | **BLOCKED_RANDOM_INPUT** | 仍缺 `cases[].threeCasterRandomEvents` 真实 V4 导出 |
| `reversedEqualsThree` | **BLOCKED_MISSING_REVERSED_WALL** | 仍缺 `cases[].reversedWall` |
| G1 / G5 | **BLOCKED / NOT APPROVED** | 本组件不是 G1/G5 决策 |
| PG / WSS / Unity / IL2CPP / Gradle / 设备 / 远端 CI | **NOT_RUN** | 无相关变更，未用本地结果冒充 |

仅 doubles 的记录**不足以**保真 V4：V4 在基础墙之后同时使用有界整数选择（`shuffled(rng)`）和
`nextDouble()`；整数选择无法从 `threeCasterWall`、seed 或 double 序列可靠反推。本组件没有手填
V4 随机样本，没有把自身可重放称为跨运行时黄金一致，也没有复刻 V4 seed/RNG 或改变
`rulesetVersion=1`。基础洗牌本身仍是 `BaseWall` 记录输入。

## 1. 变更文件（严格四个）

| 文件 | 类型 |
|---|---|
| `dotnet/src/Mahjong.Domain/Stacking/ShuffleTypedRandomTrace.cs` | 新增：不可变 typed 记录与事件类型 |
| `dotnet/src/Mahjong.Domain/Stacking/ShuffleWallSynthesizer.cs` | 修改：新增不同名 typed 入口；抽取共享随机消费面；旧入口不变 |
| `dotnet/tests/Mahjong.Domain.Tests/ShuffleTypedRandomTraceTests.cs` | 新增：9 个真实公开入口 Fact |
| `doc/v5-重构/V5-N3-SHUFFLE-TYPED-RANDOM-INPUT.md` | 新增：本报告 |

未修改：V4 Kotlin 生产源码、exporter、旧 `ShuffleRandomTrace.cs`、`tests/v5-golden/shuffle-v1.json`、
catalog/manifest、统一 verifier、旧报告、solution/project、Unity/`environment`/`Resources`/Burst。
没有 push、PR、远端 CI、默认分支或环境信任/部署操作。

## 2. 真实接口与字段语义

### 2.1 类型

`ShuffleTypedRandomDrawKind`（`ShuffleTypedRandomTrace.cs:12`）：

- `NextInt = 1`
- `NextDouble = 2`

值从 1 开始，使 `default`/0 不是合法 kind；导入时可明确拒绝未初始化值。

`ShuffleTypedRandomEvent`（`:32`）——**原始输入形态，只承载字段**：

| 字段 | 语义 |
|---|---|
| `Kind` | `NextInt` / `NextDouble`；其他值在记录创建时拒绝 |
| `Bound` | NextInt 的精确正上界；NextDouble 必须为 0 |
| `Result` | NextInt 结果，必须 `0 <= Result < Bound`；NextDouble 必须为 0 |
| `Value` | NextDouble 值，必须有限且 `[0,1)`；NextInt 必须为 0.0 |

规范化的“未使用字段固定为 0”是有意的：类型互换或半初始化事件不会因为残留值看起来合法而被接受。
`ForNextInt(bound, result)` / `ForNextDouble(value)` 只是便捷构造，语义校验统一在
`ShuffleTypedRandomTrace.TryCreate`。

`ShuffleTypedRandomDraw`（`:84`）——**已校验、不可变**的 draw：

- `Kind`；
- NextInt：`Bound > 0`、`0 <= Result < Bound`、`Value == 0.0`；
- NextDouble：`Bound == 0`、`Result == 0`、`Value` 有限且 `0.0 <= Value < 1.0`。

构造函数为 internal，只有记录创建路径能生成；外部只能通过 trace 取得。

`ShuffleTypedRandomTrace`（`:165`）：

| 成员 | 语义 |
|---|---|
| `RecordId` | 非空记录标识；Domain 不生成 ID |
| `BaseWall` | 基础洗牌结果 136 张唯一实体牌；创建时独立复制 |
| `Draws` | 基础墙之后的有序 typed 事件；`ReadOnlyCollection`，不可强转为可变数组 |
| `Count` | 事件数量（不是牌数） |
| `Create` / `TryCreate` | 严格校验并复制；失败不返回部分记录 |
| `WithoutDraws` | 合法空轨迹（无千术/禁用/四人恒等） |

创建校验（`TryValidateDraw`，`:284`）：

- `recordId` 非空；
- 基础墙恰为 136 张、值域 0..135、无重复（因此无缺失），空/重复基础墙拒绝；
- `draws` 非 null，元素非 null；
- 未知 kind（0、99 等）拒绝；
- NextInt：bound 必须 > 0，result 必须 `0..bound-1`，`Value` 必须 0.0；
- NextDouble：`Bound`/`Result` 必须 0，值必须有限、非 NaN/Infinity、`[0,1)`；
- 事件顺序原样保留，不排序、不去重、不重排。

创建时复制输入：修改创建后的源 `TileId[]` 或源事件 `List` 不影响记录；`Draws` 不可变。
Domain 新代码不创建随机、不读时钟、不生成 ID。

### 2.2 合成入口

新增不同名入口（`ShuffleWallSynthesizer.cs:316`）：

```csharp
ShuffleSynthesisResult ShapeWithTypedRandomTrace(
    ShuffleTypedRandomTrace trace,
    int dealerSeat,
    IReadOnlyList<SeatShufflePlan> plans,
    ShuffleConfig config)
```

以及原始事件列表入口（`:399`）：

```csharp
ShuffleSynthesisResult ShapeWithTypedRandomTrace(
    string recordId,
    IReadOnlyList<TileId> baseWallOrder,
    IReadOnlyList<ShuffleTypedRandomEvent> draws,
    int dealerSeat,
    IReadOnlyList<SeatShufflePlan> plans,
    ShuffleConfig config)
```

旧 `Shape(...)` 重载、名称、参数与反射可见性未变。旧 `Shape` 仍返回旧结果码/消息/计数语义。

### 2.3 错误码与诊断

保留 `0..8` 原值；新增：

| 值 | 名称 | 触发 |
|---:|---|---|
| 9 | `InvalidTypedTrace` | 原始事件入口的记录级非法输入（未知 kind、非法 bound/result、NaN/Infinity、空/重复基础墙、null 事件等） |
| 10 | `UnexpectedTypedDrawKind` | 算法要求 NextInt 但记录是 NextDouble，或反之 |
| 11 | `TypedDrawBoundMismatch` | 算法要求 `nextInt(bound)`，记录事件是 NextInt 但 bound 不同 |

typed 失败均：`IsSuccess=false`、`Wall=null`、不产生牌山、不修改输入记录。

`ShuffleSynthesisResult.RandomRequests`（`:94`）是本次实际接受请求的只读快照。对 legacy 入口，
它把每次 floor 选择记录为 `nextInt(bound, result)`；对 typed 入口，它逐项等于被消费的事件。
它只用于 C# 兼容性/诊断，**不是 V4 黄金样本**。

`ConsumedRandomCount` 的 typed 精确定义：

| 结果 | consumed |
|---|---|
| 成功 | 记录事件总数（恰好用尽） |
| `TraceExhausted` | 耗尽前已成功匹配并应用的事件数 |
| `UnexpectedTypedDrawKind` / `TypedDrawBoundMismatch` | 失败事件之前的已应用事件数；失败事件不计入 |
| `TraceNotFullyConsumed` | 算法实际消费的事件数（< 记录总数） |
| 无目标/禁用/四人恒等 | 0 |

旧 double-only 入口在 `TraceExhausted` 时仍报告 0（既有语义不变）；typed 入口报告真实前缀。
因此 legacy 失败结果上的 `RandomRequests` 只是诊断用部分前缀，**不**与旧的 `ConsumedRandomCount=0`
混用为同一个“已消费”定义。typed 在进入决策前的失败（dealer/plan 等）与 legacy 一样报告
`ActivePlanCount=0`。

### 2.4 旧 double-only 入口兼容

- `Shape(...)` 的名称、重载、参数与反射可见性未变；没有改名或替换旧 API。
- typed/legacy 共用 `ShapeValidated` 决策实现，只把随机读取抽象为 `IRandomDecisionSource`。
- legacy 的 `DoubleTraceReader.NextInt(bound)` 仍是 `(int)(pick * bound)`（并保留上界防御），
  因此旧 double 记录逐值结果不变。
- 旧错误码 0..8 的数值和语义不变；旧 `Success`/`Failure` 三/四参数重载保留，新增的是带
  `randomRequests` 的重载。
- legacy `TraceExhausted` 仍报告 `ConsumedRandomCount=0` 和原文案；无目标/禁用/四人分支的
  多余值仍返回 `TraceNotFullyConsumed` 且 `Wall=null`。
- 既有 11 项 `ShuffleWallSynthesisTests`、4 项 `ShuffleGoldenTests`、7 项
  `ShuffleGoldenBindingTests` 全部通过（22/0/0/0/0），见第 5 节。

## 3. 有序消费合同与 V4 源锚点

V4 基础墙之后只有以下随机调用；本 seam 的顺序与之逐项对应：

| # | V4 位置 | V4 调用 | typed 请求 | 条件/次数 |
|---:|---|---|---|---|
| 1 | `ShuffleWallGenerator.kt:134` | `activePlans.map { it.seat }.shuffled(rng)` | `nextInt(3)`、`nextInt(2)`（n=3；一般 m 个元素消耗 `nextInt(m)..nextInt(2)`） | 仅 `activePlans` 非空且 n<4；`shuffled` 为 Kotlin Fisher-Yates，逐元素 `nextInt(i+1)` |
| 2 | `:136` | `initialSlotsMap[seat]!!.shuffled(rng)` | 每个席位 `nextInt(L)..nextInt(2)`，L=13/14 | 每个随机顺序中的活跃席位一次 |
| 3 | `:142` | `rng.nextDouble() >= remaining` | `nextDouble()` | 仅 `remaining < 1.0`；否则不消费 |
| 4 | `:145` | `rng.nextDouble() >= strength` | `nextDouble()` | 每个未因额度 break 的目标槽一次 |
| 5 | `:149` → `:333` | `improveSlot(... rng)`；`pick = rng.nextDouble() * totalWeight` | `nextDouble()` | 仅候选非空且 `totalWeight > 0` |
| 6 | `:172` | `activePlans.map { it.seat }.shuffled(rng)` | `nextInt(m)..nextInt(2)` | 阶段二；n<4 且 active 非空 |
| 7 | `:174` | `drawSlotsMap[seat]!!.shuffled(rng)` | `nextInt(D)..nextInt(2)`，D=17/18 | 每个活跃席位一次 |
| 8 | `:179` | `rng.nextDouble() >= remaining` | `nextDouble()` | 仅 `remaining < 1.0` |
| 9 | `:181` | `rng.nextDouble() >= strength` | `nextDouble()` | 每个未 break 的摸牌目标槽一次 |
| 10 | `:190` → `:333` | `improveSlot` 供体抽样 | `nextDouble()` | 仅候选非空且 `totalWeight > 0` |

基础洗牌 `Random(seed)` / `wall.shuffle(rng)`（`:63-65`）**不进入** typed 事件流；其 136 张结果由
`BaseWall` 记录。无目标/禁用/四人恒等（`:78-87`）在基础墙后立即返回，typed 记录必须为空；多余事件
按 `TraceNotFullyConsumed` 拒绝。

零额度路径（`:139-142`、`:176-179`）在观察 `remaining <= 0.0` 之前已经完成席位与槽位洗牌，因此
它不是空 trace：单施术者、`tier(1.0,0,0)` 的合法 typed 轨迹是 28 或 29 个纯 `nextInt`（起手槽
13/14 与摸牌槽 17/18 的 Fisher-Yates），空 trace 必须 `TraceExhausted`。本次测试独立按
`ShuffleSlots.Initial/Draws` 长度核对了这一边界序列。

typed 读取器严格按位置消费：kind/bound 不匹配立即失败，不搜索、不跳过、不重排；不会把 int
转成 double 再 floor。共享算法通过 `IRandomDecisionSource`（`:1222`）只抽象“取下一个值”，
决策顺序、槽位/供体、预算和失败边界仍是一份实现。legacy 源的 `NextInt`（`:1245`）保持原
`floor(pick * bound)`；typed 源（`:1310`）直接返回 `Result`。

## 4. 测试覆盖

新增 `ShuffleTypedRandomTraceTests.cs` 的 9 个 Fact：

| Fact | 覆盖 |
|---|---|
| `TraceCopiesInputsAndExposesOnlyImmutableOrderedDraws` | 复制基础墙/事件、只读集合、不可强转数组、顺序、`WithoutDraws` |
| `TraceCreationRejectsInvalidKindsBoundsValuesAndBaseWalls` | null/空/长短/重复基础墙；null 事件；kind 0/99；bound 0/-1；result -1/==bound/>bound；NextInt 带 value；NaN/±Infinity/负数/1.0；NextDouble 带 bound/result；合法边界 `NextInt(1,0)`、`NextInt(int.MaxValue,int.MaxValue-1)`、`0.0`、`0.9999999999999999` |
| `IdentityPathsRequireExactEmptyTraceAndConsumeNothing` | 无目标/禁用/四人恒等：空 trace 成功且 consumed=0；多余 trace 拒绝且 consumed=0 |
| `KindAndBoundMismatchesFailAtTheExactOrderedPosition` | 首事件 kind/bound 错、第二事件 bound 错（consumed=1）、首个 double 换成 int（consumed=该位置）、失败后原记录仍可回放；独立核对席位洗牌 bound 3/2 |
| `TypedSynthesisIsDeterministicNonIdentityAndPreservesPhysicalLaws` | 16 seeds：同输入两次逐张一致、与 legacy 逐张一致、136 唯一、3 赤五、死山 122..135 不变、方案 reversed 正控制、真实非恒等 changed>0 |
| `ZeroBudgetIdentityStillConsumesShuffleDecisionsAndRejectsEmptyTrace` | 8 seeds：空 trace `TraceExhausted`；合法 trace consumed>0；全部 NextInt；按槽位长度独立核对 28/29 边界；牌山恒等 |
| `MissingExtraSkippedDuplicatedAndReorderedEventsAreRejected` | 截掉末事件 `TraceExhausted` consumed=N-1；追加 `TraceNotFullyConsumed` consumed=N；跳过首事件 consumed=0；重复首事件 consumed=1；交换前两事件 consumed=0 |
| `BatchSingleFieldMutationMatrixRejectsEveryCorruption` | 13 个单字段/单结构负例一次性批量执行；任何被接受项使 Fact 失败（native exit 非零）；矩阵后原记录仍成功 |
| `InvalidEntryArgumentsAndRawEventsReturnStructuredFailures` | trace/config null；dealer -1/4；plans null/null 元素/重复 seat；原始入口空 recordId/重复墙/NaN/null 事件 → `InvalidTypedTrace`；合法原始列表与 trace 入口一致 |

单字段矩阵包含：first-int→double、first-bound+1、first-bound-0、first-result-at-bound、
first-int-with-double-value、first-double→int、first-double-NaN、first-double-1.0、skip-first、
duplicate-first、drop-last、append-extra、swap-first-two。矩阵的 self-negative 语义是：若实现错误地
接受了任一损坏输入，`accepted` 非空，Fact 断言失败，测试进程 native exit 非零；测试/证据工具不向
生产源写负例。

测试明确标注：typed 事件来自同一 C# 合成器 legacy 执行的 `RandomRequests`，是 **C# 自测**，不是
V4 黄金；不证明 V4 逐墙一致。

## 5. 实测命令、exit 与计数

证据根 E：`D:/majiang-wt/shuffle-typed-random-evidence-20261025`
CLI：`D:/Unity/dotnet/dotnet.exe`
所有命令在 `D:/majiang-wt/shuffle-typed-random` 执行。计数顺序为 Total / Errors / Failed / Skipped / Not Run。

| 检查 | native exit | 结果 / 原始日志 |
|---|---:|---|
| solution Release 非增量构建 | 0 | 0 warning / 0 error；`solution-build.log` |
| TableRuntime.Tests Release 非增量构建 | 0 | 0 warning / 0 error；`tableruntime-build.log` |
| Domain 全量 | 0 | 492 / 0 / 0 / 0 / 0；`domain-full.log` |
| typed 定向 9 项 | 0 | 9 / 0 / 0 / 0 / 0；`typed-targeted.log` |
| 旧 shuffle 定向 22 项（4+7+11） | 0 | 22 / 0 / 0 / 0 / 0；`shuffle-legacy-targeted.log` |
| AI 全量 | 0 | 438 / 0 / 0 / 0 / 0；`ai-full.log` |
| Contracts 全量 | 0 | 149 / 0 / 0 / 0 / 0；`contracts-full.log` |
| Client 全量 | 0 | 132 / 0 / 0 / 0 / 0；`client-full.log` |
| TableRuntime 全量 | 0 | 365 / 0 / 0 / 0 / 0；`tableruntime-full.log` |

五个标准模块合计 **1576 passed / 0 failed / 0 skipped / 0 not-run**。typed 9 项已包含于 Domain 492，
旧 shuffle 22 项也已包含于 Domain 492，均不重复加总。无 skip、空测试、`Total: 0`、旧 DLL 或编译失败掩盖；
先完成非增量构建，再运行对应 Release DLL。

未运行：PG、WSS、Unity、IL2CPP、Gradle、Android/桌面实机、远端 CI。无相关变更，未盲跑重型平台，
也不把它们写成 PASS。

### 5.1 完整 SHA-256

源/测试/证据（`Get-FileHash -Algorithm SHA256`）：

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `dotnet/src/Mahjong.Domain/Stacking/ShuffleTypedRandomTrace.cs` | 15570 | `AE912C8DC2E06DB843614D5FA5CC2B99C058309FA5431AB5389CDC61795A5F10` |
| `dotnet/src/Mahjong.Domain/Stacking/ShuffleWallSynthesizer.cs` | 55617 | `7863B155AA0B8235860E39CBDEEADC4FD189E758858E9B25D9B8720A746D8170` |
| `dotnet/tests/Mahjong.Domain.Tests/ShuffleTypedRandomTraceTests.cs` | 42196 | `38263AAE5EC855F682862B020222ACCA7D474D5A14CB8DE3F9B5D56B92E63BD2` |
| `tests/v5-golden/shuffle-v1.json`（未改） | 99467 | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` |
| `Mahjong.Domain.dll` | 377344 | `C9FEC855AD2A4A9F5BD0F953E813995A3E2BD91950C6D45A5663690146C703F6` |
| `Mahjong.Domain.Tests.dll` | 570880 | `53C2CE85119F70727983B5E5BF7C5B254E9BE6FB1F8ECCC369AC05F8644F8904` |

证据日志：

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `solution-build.log` | 3528 | `60471A9C58AF230A7D103649DBB7E7F28BC123BC669B006F7B144CED83C7E2E2` |
| `tableruntime-build.log` | 2140 | `87939833A2D182929E7A1BDF47BC1756C8744717CDB4D0F4288F8BCC1506F0CC` |
| `domain-full.log` | 994 | `3E2328C8668D6E3C48B25CAB80663AC8668264B1978CF725C52F1ABC17C02D55` |
| `typed-targeted.log` | 736 | `73DE169B8811A8CB530F5033452B73A029DE820BC808A241A751A8FF4EE0BF1F` |
| `shuffle-legacy-targeted.log` | 738 | `D997BF11272DC0F34CC16D722B6830B56C485A1DD49C80CB349761A947797E61` |
| `ai-full.log` | 700 | `E7F23952FAE834E01EBE6B9CDAF196552E376C8C818E4D20B363F4D9A3C08856` |
| `contracts-full.log` | 770 | `A6224DCDE367EC8F222316E32EBCAFDADE3B0185F4E8BF42B2477EF0FCBF57E5` |
| `client-full.log` | 740 | `517965664DA0BA5BB665A5868995BD7071EC188C2DF8DE117437FBD2233D95A9` |
| `tableruntime-full.log` | 800 | `4B043928F501B1E89842113E72B2EA158DFEF9F12738BC5068B1D04EBA418846` |

旧 fixture 哈希与 shuffle-v1 验收记录一致，未因本任务变化。

## 6. 复跑命令

```powershell
$R = 'D:/majiang-wt/shuffle-typed-random'
$E = 'D:/majiang-wt/shuffle-typed-random-evidence-20261025'
$CLI = 'D:/Unity/dotnet/dotnet.exe'

& $CLI build "$R/dotnet/Mahjong.sln" -c Release --no-incremental -v minimal
& $CLI build "$R/dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj" -c Release --no-incremental -v minimal

foreach ($m in @('Domain','Ai','Contracts','Client','TableRuntime')) {
    & $CLI "$R/dotnet/tests/Mahjong.$m.Tests/bin/Release/net8.0/Mahjong.$m.Tests.dll" -noLogo
    if ($LASTEXITCODE -ne 0) { throw "$m native exit $LASTEXITCODE" }
}

& $CLI "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" -noLogo -class '*ShuffleTypedRandomTraceTests*'
& $CLI "$R/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll" -noLogo -class '*ShuffleWallSynthesisTests*' -class '*ShuffleGoldenTests*' -class '*ShuffleGoldenBindingTests*'

git -C $R diff --check
git -C $R status --porcelain=v1
```

`*` 过滤器在 PowerShell 中原样传递；如需日志，可对上述命令追加 `*> $E/<name>.log` 并记录
`$LASTEXITCODE`。矩阵负例的“失败返回非零”由 `BatchSingleFieldMutationMatrixRejectsEveryCorruption`
Fact 自身保证：任一损坏输入被接受即 Fact 失败，进程 native exit 非零。

## 7. 最小后续 V4 提取责任（未执行）

本组件只是消费者 seam，不产出 V4 事件样本。`threeCasterShapeBinding` 要解除
**BLOCKED_RANDOM_INPUT**，仍需一次单独授权的 V4 侧工作：

1. 在 `engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt:63` 引入测试专用
   `Random`/`RandomSource` 记录 seam（生产默认仍 `Random(seed)`，不改变规则/版本）。只记录
   **基础墙形成之后**的调用；基础 `wall.shuffle(rng)` 作为 `baseWall` 已有记录，不混入事件流。
2. 在 `:134,136,142,145,172,174,179,181,333` 的真实调用点记录
   `{kind:"nextInt",bound,result}` / `{kind:"nextDouble",value}`，严格保留顺序。不得手填、
   反推或从墙/seed 猜测结果。
3. exporter 为 32 个 case 增加 `cases[].threeCasterRandomEvents`（以及必要的
   `fourCaster`/`noTarget`/`zeroBudget` 事件或明确共享规则）。**不要改旧 fixture 字段语义**：
   新增字段或新 schema/version，保留旧 `shuffle-v1.json` 的既有消费者；旧四/七个 golden Facts
   不变。
4. C# 消费方用 `ShapeWithTypedRandomTrace` 读取真实 V4 事件，32 cases × 136 slots 逐张比较；
   截断/多余/kind/bound 错必须失败；同时复核 136 唯一、3 赤五、死山 122..135。
5. `reversedEqualsThree` 另需 `cases[].reversedWall: number[136]`，与本 seam 独立。

在上述实际导出完成前，不能声称 `threeCasterShapeBinding` PASS，也不能把本报告的 C# 自测当作
V4 跨运行时黄金一致。

## 8. 最终状态

```text
typed random input seam                PASS (component)
legacy double-only compatibility       PASS (C# self-test; all old tests green)
threeCasterShapeBinding                BLOCKED_RANDOM_INPUT
reversedEqualsThree                    BLOCKED_MISSING_REVERSED_WALL
G1                                     BLOCKED
G5                                     NOT APPROVED
PG / WSS / Unity / IL2CPP / Gradle / device / remote CI   NOT_RUN
```

worktree 交付后保持 clean；仅四个文件进入本地最小 commit。调用者独立复核后负责隔离集成树、
唯一 PR19 `feat/v5-1-integration -> v5` push 与 `record_merge_request`；本任务不自行宣布
N3/G1/G5 完成。
