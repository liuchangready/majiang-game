# V5-P1.6 目标感知 AI 策略（ENG-016）交付记录

日期：2026-10-25

状态：**组件通过；不是完整 P1.6、G1、平台或端到端通过。V4 仍是唯一生产基线。**

基线：`78441944283ca111cc344f19d152514ed577574c`（已验收 PR19 历史头；远端后来的独立启发式提交不属于本任务依赖，本树未覆盖/更新冻结基线）。

## 1. 结论与范围

本任务实现 `implementation-v1` 记录的 ENG-016 真实未迁移缺口：把 V4
`engine/src/main/kotlin/com/majiang/ai/TargetAwareAIStrategies.kt` 中的
`TargetAwareAi.SuitGoalAi`、`HonorTripletGoalAi`、`KokushiGoalAi` 三个纯可见牌目标策略移植到
`Mahjong.Ai`。

本交付只声明**组件行为**：

- 输入只来自 `AiObservation` 已授权的本人手牌/摸牌、本人公开副露、公开最后弃牌、公开分数/场况与冻结合法候选；
- 输出是 `AiObservation.LegalActions` 中的一个候选，或 `null`；
- 不读取 `GameState`、完整牌山/死山、对手暗牌、种子、全 136 位置表、权威回调或 Unity；
- 评分只是排序启发式，不是规则、收费、合法性或 `CommandIntent.AuthorityHash` 的输入；
- 没有把策略装配到 actor/`AiDecisionService`；actor 投影产出与实际接线属后续任务。

本任务**没有**完成或声称完成：完整 P1.6、G1、平台端到端、TableRuntime/PG/WSS、Unity/IL2CPP、真机或远端 CI。

## 2. V4 源码对照

源码：`engine/src/main/kotlin/com/majiang/ai/TargetAwareAIStrategies.kt`（306 行，基线 HEAD）。
测试参照：`engine/src/test/kotlin/com/majiang/ai/TargetAwareAiStrategiesTest.kt`。
物理动作事实参照：`engine/src/main/kotlin/com/majiang/state/LegalActionProvider.kt`
（`DeclarePon(seat, matching.take(2))`；`DeclareChi` 携带完整 `Meld`）。

### 2.1 共同分支顺序

三个策略均按 V4 顺序：

1. 空合法列表返回 `null`；
2. 先取第一个 `Ron`，再取第一个 `Tsumo`；
3. 已立直且摸牌在合法候选内时，摸切**精确物理牌**；
4. 各自目标分支（Suits/Honor/Kokushi）；
5. V4 fallback：`BasicAIStrategy().decideAction(view, 完整 legalActions)`；若 Basic 结果被本目标禁止，再按**原始候选顺序**取第一个不禁止候选；无则 `null`。

关键点：fallback **不是**先过滤候选再交给 Basic。实现中 `TargetAwareAiMath.Fallback` 先调用
`BasicAiStrategy.TryChoose` 完整列表，再过滤其结果。

### 2.2 评分与 tie 语义

- `suitScore = 100*targetCount + 12*mentsu + 4*taatsu`（mentu/taatsu 按 V4 的 presence 计数，允许重叠）。
- `numberMentsuCount` 对万/索/筒分别计数刻子与顺子。
- `honorScore = 100*completeKinds + 15*sum(min(c,3)) + 3*numberMentsuCount`。
- `kokushiScore = 100*distinct + (hasPair ? 15 : 0) - 20*max(0, totalExtra-1)`。
- 全部为整数；没有浮点规则参与。

`maxWithOrNull` 的精确语义：比较键依次为 score、typeIndex、copyIndex（均为升序键），取最大；
**完全相等时保留先出现的候选**。不是取最小 tileId，也不是后出现者覆盖。`CandidateRank.IsBetterThan`
只在 score/typeIndex/copyIndex 严格更大时替换；测试覆盖最大 typeIndex、最大 copyIndex 与完全相等保留首个。

### 2.3 三个策略

**SuitGoalAi（万/索/筒清一色）**

- 仅碰目标花色的最后弃牌，且 `afterPon` 的 score 严格大于 `before`；
- 仅吃完整副露三张均为目标花色，且 `afterChi` 严格大于 `before`；
- 碰优先于吃，吃优先于打牌；
- 打牌按 `removeTile(base, tile)` 的 score、typeIndex、copyIndex 取最大；
- fallback 禁止：非目标吃、非目标/无改善碰、杠。

**HonorTripletGoalAi（大三元/大四喜）**

- 构造时目标字牌必须非空且全部在 `27..33`；输入集合按 Kotlin `Set` 语义去重；
- 仅当最后弃牌是目标字牌，且可见 base（本人暗手 + 本人公开副露）中该 type 少于 3 张时碰；
- 该碰分支没有“严格改善”条件（与 V4 一致）；
- 其余按 honor score 打牌；
- fallback 禁止吃、非目标字牌碰、杠。

**KokushiGoalAi（国士无双）**

- 13 类 typeIndex 为 `0,8,9,17,18,26,27,28,29,30,31,32,33`；
- 只按 `myAllClosedTiles` 评分（不含本人副露）；额外副本按 V4 罚分；
- 有打牌时先走国士评分，因此不会宣告九种九牌；
- 无打牌时 fallback 禁止明确分类为九种九牌的 `Other`、吃、碰、杠；非九种九牌的通用 `Other` 不误判；
- 无合法非禁止候选时返回 `null`。

### 2.4 物理参与牌

V4 的 `afterChi` 从 base 中移除完整副露里除 `calledTile` 外的每一张，再加入完整 `meld.tiles`；
`afterPon` 移除 `pon.tiles`（V4 provider 实际为 2 张手牌），再加入 `pon.tiles + called`。
本实现用 candidate-bound 的 `TargetAwareChiFact`（完整 3 张，含 called）与
`TargetAwarePonFact`（2 张手牌 + called）精确表达，不按 optionId 或类型猜测。

## 3. C# API 与不可变绑定

### 3.1 文件

- `dotnet/src/Mahjong.Ai/TargetAwareAiContext.cs`
- `dotnet/src/Mahjong.Ai/TargetAwareAiStrategies.cs`
- `dotnet/tests/Mahjong.Ai.Tests/TargetAwareAiStrategiesTests.cs`
- 本文件

### 3.2 `TargetAwareAiContext`

由 `AiObservation` 加候选绑定事实构造。保存：

- `SessionId`、`DecisionKey`、`ControlEpoch`、`StateVersion`；
- 冻结候选列表的防御副本（保持顺序与完整候选身份：kind/seat/tile/optionId）；
- 既有 `BasicAiContext`（本人立直、公开最后弃牌、Kan/Other 分类）；
- 每个 Chi 候选的完整 meld/called，每个 Pon 候选的两张参与手牌/called。

构造时校验：

- `BasicContext` 存在且属于同一座位；复用 `BasicAiContext.ValidateCandidateList`；
- 每个 Chi/Pon 候选必须有且只有一个事实；事实必须指向该列表中的候选；
- Chi 必须是同花色、三张不同 rank、连续、包含 called；Pon 必须是 called 同 type 的两张不同物理手牌；
- called 必须等于公开最后弃牌；called 不能已在本人手牌/副露中；
- 参与手牌必须在 `myAllClosedTiles` 中；
- 本人手牌与副露不能重复物理牌。

所有集合与事实列表构造时复制，公开为 `ReadOnlyCollection`/`IReadOnlyList`。

### 3.3 入口

三种策略各提供：

```csharp
AiStrategicChoice? TryChoose(
    AiObservation observation,
    TargetAwareAiContext context,
    CancellationToken cancellationToken = default);
```

这是推荐的安全入口：先验证 `context` 与 `observation` 的 session、DecisionKey、controlEpoch、
stateVersion、候选列表顺序/身份和 BasicContext 完全一致；跨决策窗口、旧 epoch/stateVersion 或
同 optionId 不同候选均抛 `TargetAwareAiContextException`。

同时保留 raw 入口：

```csharp
AiStrategicChoice? TryChoose(
    IReadOnlyList<LegalActionOption> legalActions,
    TargetAwareAiContext context,
    IReadOnlyList<TileId> myClosedTiles,
    TileId? myDrawnTile,
    IReadOnlyList<AiVisibleMeld> myMelds,
    int dealerSeat,
    int roundWind,
    IReadOnlyList<long> scores,
    CancellationToken cancellationToken = default);
```

raw 入口保留 V4 的**空列表立即返回 `null`**语义；非空时验证候选列表与 context 完全一致。
raw 入口是低层入口，调用方必须传入与 context 同一窗口的可见输入；安全接线应使用
`AiObservation` 入口。没有实现 `IAiStrategy` 固定上下文包装，避免在组件层捕获旧窗口；
`AiDecisionService` 的实际装配留给后续 actor 接线任务。

`AiObservation` 未修改；没有把元数据塞进 `OptionId`，也没有另造完整观察副本。

### 3.4 隐藏状态边界

新类型只引用 `AiObservation`、`AiVisibleMeld`、`LegalActionOption`、`TileId`、`BasicAiContext`、
`DecisionKey` 和 BCL 集合。没有 `GameState`、`TilePositionMap`、牌山/死山、对手手牌、种子、
时钟或权威回调字段。测试对公开属性名和 `Mahjong.Ai` 程序集引用做边界检查。

## 4. 验证结果

### 4.1 Release solution 构建

命令（工作树根）：

```powershell
& 'D:/Unity/dotnet/dotnet.exe' build dotnet/Mahjong.sln -c Release --no-incremental
```

结果：`exit 0`，**0 warning，0 error**。

日志：`C:\Users\sncdc\AppData\Local\Temp\majiang-target-aware-probe-20261025\solution-release-build.log`

SHA-256：`3dee5aa5c97eb760f7ced3ff7dd1fd3b13176b76cb1d0a94a377033124b24765`

### 4.2 测试套件

均先使用上面的 `--no-incremental` 构建，再 `--no-build` 运行；无 skip、无空测试、无旧 DLL。

| 套件 | 命令（工作树根） | 结果 | 日志 SHA-256 |
|---|---|---|---|
| Ai 全量 + 新测试 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-build` | 224 通过 / 0 失败 / 0 skip，exit 0 | `49e22dd33e77797e5a15a495d02105037f657fc8477334f6e83842508bb0b3ed` |
| Domain | 同上，`Mahjong.Domain.Tests` | 322 通过 / 0 失败 / 0 skip，exit 0 | `5b8b0e236a884efa16e31d70caee1a2534e50d01edde2ebe853ed0259eb539d9` |
| Contracts | 同上，`Mahjong.Contracts.Tests` | 104 通过 / 0 失败 / 0 skip，exit 0 | `a790ffc8887c42e4a1d0d21f6a4510e8c18d9911043672d05994079852b17ed6` |
| Client | 同上，`Mahjong.Client.Tests` | 132 通过 / 0 失败 / 0 skip，exit 0 | `a3014a8946860f8407f17cf7adee3fa671030bbcc94cf7e811db5a0c8c32e04d` |

Ai 基线保持 **172** 项，全部旧例保留；新增 **50 个测试方法**（49 `[Fact]` + 1 个 3 组
`[InlineData]` 的 `[Theory]`），执行 **52** 项，`172 + 52 = 224`。没有采用旧启发式树的 115 作为
当前 Ai 分母。

### 4.3 真实 V4 Kotlin 差分探针

**对照来源**：本树 `engine/build/libs/engine.jar`，由 Gradle `:engine:jar` 从 V4 真实
`TargetAwareAIStrategies.kt`/`BasicAIStrategy.kt` 等源码编译；Kotlin 探针直接调用
`TargetAwareAi.SuitGoalAi`、`HonorTripletGoalAi`、`KokushiGoalAi`，没有把算法手抄到探针里。

**方法**：Kotlin 探针用固定 seed `20261025` 生成 121 个可见、物理自洽的 case；
每个 case 分别跑 SuitGoal（三花色轮换）、HonorTriplet（四组目标集合轮换）和 Kokushi，共
**363 个策略结果**，写出 case/候选/真实 V4 决策索引。C# 探针重建同一
`AiObservation + TargetAwareAiContext`，调用目标策略并逐项比较索引；差异文件为空。

**结果**：

- cases：121；strategy results：363；matches：**363**；mismatches：**0**；errors：**0**；
- 非空结果：335；`null` 决策：28（4 个空候选 case × 3 + 16 个仅禁止动作/仅国士禁止 case）；
- 含 Chi 的 case：21；含 Pon：36；含 Kan：9；含 AbortiveDraw：8；
- 结果行中的候选类型计数：`A=24, C=63, D=2634, K=27, O=108, P=66, R=27, T=39`；
- 实际选择的类型计数：`A=16, C=10, D=195, NULL=28, O=19, P=56, R=27, T=12`；
- 三策略各自选择的分布：Suit `A/C/D/NULL/O/P/R/T = 8/10/55/8/11/16/9/4`，
  Honor `A/D/NULL/O/P/R/T = 8/66/8/8/18/9/4`，Kokushi `D/NULL/P/R/T = 74/12/22/9/4`。

**日志与产物**（独占 TEMP，未纳入冻结 golden 分母）：

- Kotlin cases（UTF-8，38558 bytes）：
  `C:\Users\sncdc\AppData\Local\Temp\majiang-target-aware-probe-20261025\kotlin-cases.txt`

  SHA-256：`2a9176fb7b15534f5b9b2f94dc4c3a9c2c60a0c1bce8c426d08fb701abecbbe8`
- Kotlin 运行日志（Gradle `BUILD SUCCESSFUL`，cases/results/seed 计数）：
  `...\kotlin-probe.log`，SHA-256：`acaf00d90799874d06dfcb8efa4807a1a7eab35545c4c23d8003e4b83e5dc5d2`
- C# 差分摘要：
  `...\csharp-probe.log`，SHA-256：`756d0c16692e8163474f3a0be204d41275e0f14e19aa5b7dad1b7dc0e1e8451b`
- 差异文件（0 bytes，SHA-256 为空文件摘要）：
  `...\csharp-diff.txt`，`e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`

Kotlin 探针源码与 C# 探针源码也在该 TEMP 目录（`src/main/kotlin/Probe.kt`、
`csharp/Program.cs`），未提交、未修改 V4 tracked 源码/测试。

### 4.4 PASS / FAIL / BLOCKED / NOT_RUN

| 项 | 状态 | 证据/说明 |
|---|---|---|
| ENG-016 三个策略组件 | **PASS** | 单测 + 真实 Kotlin 差分 363/363 |
| Release solution 构建 | **PASS** | 0 warning / 0 error |
| Ai 全量（172 旧 + 52 新） | **PASS** | 224/224 |
| Domain / Contracts / Client | **PASS** | 322 / 104 / 132 |
| 组件级失败/阻塞 | **无** | 无未修复 BLOCKER/MAJOR/MINOR |
| 完整 P1.6 / G1 | **NOT_RUN（阶段门槛）** | 本任务只交付组件，不宣布阶段通过 |
| actor 投影产出、目标策略实际装配 | **NOT_RUN（后续接线）** | 本任务未实现 IAiStrategy 固定上下文包装或 actor 接线 |
| TableRuntime / PG / WSS | **NOT_RUN** | 未跑 |
| Unity / IL2CPP / 真机 / 物理网络 | **NOT_RUN** | 未跑 |
| 远端 CI | **NOT_RUN** | 未触发 |

## 5. 已知边界与后续

1. 本交付是三维目标策略的**纯组件**；没有 actor 投影、没有 `AiDecisionService` 装配、没有
   端到端回放。`IAiStrategy` 包装未实现，避免在组件层固定旧窗口；后续接线必须用
   `AiObservation + TargetAwareAiContext` 的同一窗口并复核 `DecisionKey/controlEpoch/stateVersion`。
2. `TargetAwareAiContext` 要求 `AiObservation.BasicContext` 非空；这是 fallback 的立直/最后弃牌/
   Kan/Other 分类所需，缺失时明确抛 `TargetAwareAiContextException`，不猜。
3. Chi/Pon 事实必须由权威投影提供；当前 `LegalActionOption` 不带完整物理参与牌，本任务没有修改
   `AiObservation` 或 `LegalActionOption`，也没有解析 optionId 猜动作。
4. 评分是启发式，不能作为规则/收费/权威哈希；`HeuristicScore` 已在测试中证明不影响
   `CommandIntent.AuthorityHash`。
5. V4 仍是唯一生产基线；本交付不改变 V4 源码/测试/冻结 golden，也不把临时差分输入计入
   `tests/v5-golden/` 的分母。
6. 已知产品/阶段缺口：完整 P1.6、G1、平台、真实网络/设备均未完成；不得把本文件写成 G1/G5 通过。

## 6. 文件清单与提交

预期并实际修改（新增）仅：

1. `dotnet/src/Mahjong.Ai/TargetAwareAiContext.cs`（828 行）
2. `dotnet/src/Mahjong.Ai/TargetAwareAiStrategies.cs`（1203 行）
3. `dotnet/tests/Mahjong.Ai.Tests/TargetAwareAiStrategiesTests.cs`（1157 行）
4. `doc/v5-重构/V5-P16-TARGET-AWARE-STRATEGIES.md`（本文件）

没有修改 `AiObservation`、`BasicAiStrategy`、`BasicAiContext`、`AiDecision`、`CommandIntent`、
Domain/Contracts、既有测试/golden、Unity/Resources/Burst、V4 源码/测试或兄弟任务文件。
最终 commit 与 clean status 以交付回复中的 `git rev-parse HEAD`/`git status` 为准。
