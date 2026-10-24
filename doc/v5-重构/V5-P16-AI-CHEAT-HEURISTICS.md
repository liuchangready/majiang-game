# V5 P1.6 — AiCheatHeuristics 可见牌排序组件移植（ENG-021）

日期：2026-10-25
状态：**组件级交付与独立复核证据；不宣称 P1.6/G1/G5 完成。**
工作树：`D:/majiang-wt/ai-cheat-heuristics`（独占），分支 `wt/v5-ai-cheat-heuristics`，起点 HEAD `654aa54175fb27dc1761a0bed00de1978684fabd`（已验收 PR19 当前头）。
对应缺口：`doc/v5-重构/implementation-v1.json` 行 **ENG-021**（`gapSummary: 仅用本座位可见信息的启发式未移植`，responsibleTask P1.6，planTarget Mahjong.Ai）。**冻结的 implementation-v1.json 本轮未改动**——该行仍由 caller 侧统一结算。

---

## 1. 范围

仅三个 expectedFiles（本工作树 diff 全集，`git status --porcelain` 只有这三项）：

| 文件 | 性质 |
|---|---|
| `dotnet/src/Mahjong.Ai/AiCheatHeuristics.cs` | 生产移植（ENG-021） |
| `dotnet/tests/Mahjong.Ai.Tests/AiCheatHeuristicsTests.cs` | 新增测试（与既有 `AiDecisionTests.cs` 同工程分文件，沿用 P16 policy 测试分文件先例） |
| `doc/v5-重构/V5-P16-AI-CHEAT-HEURISTICS.md` | 本记录 |

未修改：`AiObservation.cs`、`AiCheatPolicy.cs`、`AiDecisionModels/AiDecisionService/CommandIntent`、`BasicAiContext`（若在后续任务中落位）、Domain 全部、Contracts 全部、actor/registry/实际千术能力或收费接线、`tests/v5-golden/ai-v1.json`、统一 verifier、`.gitignore`、`.git/info/exclude`。差分探针全部位于本任务独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/eng021-heuristics-probe/`，零仓库污染。

**边界声明**：本组件只是排序启发式。真实能力消费、capability 适配器、actor 接线、`selectableTileIds` 投影供给均**未实现**，属后续任务。启发式整数分值仅用于候选排序，**不得**进入 `CommandIntent` 规范哈希或权威收费事实；本类型不产生任何命令、回执或账本副作用。

---

## 2. V4 → V5 逐函数行为对照

V4 源：`engine/src/main/kotlin/com/majiang/ai/cheat/AiCheatHeuristics.kt`（internal object）+ `engine/src/main/kotlin/com/majiang/model/Tile.kt`。V4 调用方核对：`BeamSwapAiCapability.kt:92,97,101-102`、`SleeveBloomAiCapability.kt:130`、`TileMarkAiCapability.kt:55-56`（三者均不在本轮移植范围）。

| V4 实际行为（按源码而非注释） | V5 实现 | 证据 |
|---|---|---|
| `same = hand.count { it.sameType(tile) }` **含被评牌自身**与全部物理 copy；`>=3→+18`、`==2→+10`、否则 0 | 同构（`SameType` 计数，含自身） | 探针 TV 2,040 例 0 差异；测试 `SameTypeCountAwardsTenForAPairAndEighteenForThreeOrMore`、`SameTypeCountIncludesTheEvaluatedTileItself` |
| 数牌近邻：`it !== tile && it.suit == tile.suit && abs(Δrank)==1` 每张 +5；`==2` 每张 +2。**`!==` 引用排除可证为 no-op**：距离 1/2 必然 `Δrank≠0`，同引用/同 type 牌距离恒 0 本就不满足谓词——排除与否对任意输入计数相同，且不会错误排除另一物理 copy | 纯值谓词 `Suit 相同 && |Rank 差|∈{1,2}`，无任何排除 | 探针 136 候选 × 15 手牌逐点 0 差异（若 no-op 论断有误必现分叉）；测试 `SameTypeTilesNeverCountAsNeighbours` |
| 距离 ≥3 或跨花色：0（34 维相邻 type 如 9m→1s **不**相邻——花色不同即 0） | 同构 | 测试 `DistanceOneAddsFiveAndDistanceTwoAddsTwoPerNeighbour`、`NeighboursNeverCrossSuits` |
| 数牌 rank∈{1,9}：−2 | 同构 | 测试 `TerminalsPayTwoAndSimplesPayNothing` |
| 字牌：DRAGON +4、WIND +1；**无**自风/场风/宝牌/红五额外权重 | 同构 | 测试 `HonorsGetFourForDragonsAndOneForWinds`、`RedFiveHasNoExtraWeightOverAnOrdinaryFive`（19=红五 copy3 与 18 分值恒等） |
| `leastValuableHandTiles`：`sortedWith(compareBy{value}.thenBy{tileId})` 稳定升序 `.take(limit)`；`mostValuableHandTiles`：`compareByDescending{value}.thenBy{tileId}` 降序值+升序 id | LINQ `OrderBy/OrderByDescending + ThenBy(Value)`（文档化稳定排序，keySelector 每元素恰一次） | 探针 2,716 排序例 0 差异；测试 `LeastSortsAscendingAndMostDescendingByValue`、`TiesBreakByAscendingTileIdInBothDirections` |
| `take(limit)`：负数抛 `IllegalArgumentException`；0→空；>count→全部；**无 14 张上限、无去重、无 candidateLimit64 钳制** | 负数抛 `ArgumentOutOfRangeException`（C# 惯例映射，见 §4）；0→空；>count→全部 | 测试 `LimitZeroReturnsEmptyAndOversizedReturnsEverything`、`NegativeLimitThrowsLikeTheV4Take`、`NoHiddenCandidateLimitOrHandSizeCapIsApplied`（20 元素重复列表 limit 15→15、100→20） |
| `relativeValue(candidate, hand) ≡ tileValue(candidate, hand)`；候选**不要求在手** | `RelativeValue` 直委托 `TileValue` | 探针 REL 136 例；测试 `RelativeValueIsExactlyTileValueInsideAndOutsideTheHand`（0..135 全扫） |
| Kotlin 非空参数固有 null 检查（方法入口 NPE） | 入口显式 `ArgumentNullException`（映射声明，见 §4） | 探针 NULL 3 例双侧 NULL_THROWN；测试 `NullListIsRejectedAtEveryEntryPoint`（4 入口） |
| 输入列表不突变（`sortedWith` 返回新 List） | `OrderBy` 非突变；输出 `Array.AsReadOnly(新数组)`，无可写集合逃逸 | 探针每手牌 IN2 复读行；测试 `SortingNeverMutatesTheInputList`、`OutputIsADefensiveCopyIsolatedFromTheInput`（NotSame + Add/Clear 抛 NotSupported + 输入变异后输出冻结） |
| 输入域：任意长度 List、允许重复、V4 无验证 | 同域保持：`IReadOnlyList<TileId>` 任意 Count、允许重复、不造 14 张约束 | 测试 `NoHiddenCandidateLimitOrHandSizeCapIsApplied`；探针随机手 1..14 含物理重复 |

物理牌一律用既有 `Mahjong.Domain.TileId`（0..135，`TypeIndex=Val>>2`，copy3 五为红五），未造任何重复牌类型或隐藏牌视图。V5 `TileSuit` 枚举序（Man/Pin/Sou/Wind/Dragon）与 V4 命名序（MAN/PIN/SOU）数值不同，但组件只用相等比较，无序值依赖。

---

## 3. 独立 Kotlin/C# 差分探针（真实 V4 源码编译执行）

- 位置：独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/eng021-heuristics-probe/`（`run-probe.sh`、`Probe.kt`、`Program.cs`、`Probe.csproj`，均不入库）。
- Kotlin 侧：JBR `java.exe`（OpenJDK 25.0.2）+ Gradle 缓存 `kotlin-compiler-embeddable-2.4.10.jar`（与工程 Kotlin 版本一致，另需 `kotlin-reflect-2.1.0`、`kotlinx-coroutines-core-jvm-1.8.0` 在 classpath）+ `kotlin-stdlib-2.4.10.jar`，`-jvm-target 17`，**直接编译本工作树真实 V4 源**（`Tile.kt` + `AiCheatHeuristics.kt`，未复制未修改）与驱动 `Probe.kt` 同模块执行。
- C# 侧：`Probe.csproj`（net8.0，Release）以 ProjectReference 直接引用本工作树 `Mahjong.Ai.csproj`/`Mahjong.Domain.csproj`，驱动 `Program.cs` 与 Kotlin 逐行镜像。
- 语料（两侧同构、显式可复现）：
  - 显式手 9 组：空手、单张、同 type 对子/三张/四张（含红五 19）、4m/5m/6m 近邻链、三花色边界（9m/1s/1p）、幺九+风+三元、跨色不干扰手。
  - 随机手 96 组（大小 1..14，允许物理重复）+ 拟真手 48 组（13 张不重复 + 1 张摸牌），seed `20261025`，LCG `state*6364136223846793005+1442695040888963407`（64 位回绕两语言一致，取 `ushr 33` 高 31 位）。
  - `tileValue`：全部 136 候选 × 15 手牌；`relativeValue`：136 候选 × 1 手；排序：152 手 × 去重 limit 集 `{0,1,2,⌊n/2⌋,n−1,n,n+1,n+5,64}`（自然含 −1）× {ASC,DESC}；null 入口 3 例。
- 结果（终版复跑，两次运行 SHA256 一致 → deterministic）：

```text
cases=5054 tv=2040 rel=136 sorts=2716 sortIn=27812 sortOut=17600
hist=-2:308;0:804;1:252;2:132;3:20;4:232;5:116;6:12;7:52;8:8;9:28;10:52;11:4;
     12:40;13:4;14:12;15:28;16:4;17:4;18:16;20:28;22:4;25:4;34:8;38:4
DIFF: identical
dump-kotlin.txt == dump-csharp.txt == b294be5fce4f7c640e961caa4c1d259cbd64a7eb98fb0641959f01839a15996c
```

- “实际比较次数”口径：两侧被观察的统计量（用例数、tileValue/relativeValue 调用数、排序例数、排序输入张数 27,812、输出张数 17,600、分值全分布直方图）逐项相等并纳入 diff。内部排序比较次数为运行时私有（JVM TimSort 每次比较重算 value vs .NET LINQ 每元素求值一次），**不属于可观察行为契约**，以全量排序输出 0 差异作为等价证据；此为 §5 已知内部差异 D-2。
- 异常映射实测（stderr 证据，归一化为 THROWN/NULL_THROWN 后纳入 diff，原类型单独留档）：
  - Kotlin `SORT L=-1 → java.lang.IllegalArgumentException` ⇄ C# `System.ArgumentOutOfRangeException`
  - Kotlin `NULL → java.lang.NullPointerException`（固有检查）⇄ C# `System.ArgumentNullException`
- 探针初跑一轮因两侧空输出列表格式（`""` vs `-`）报 4 行差异，属**探针自身格式缺陷**，修正驱动格式后 0 差异；V4/C# 生产语义无涉。

---

## 4. 错误路径与 C# 惯例映射（显式声明）

| V4 Kotlin | V5 C# | 性质 |
|---|---|---|
| 参数非空固有检查 → `NullPointerException` | 入口 `ArgumentNullException`（4 个公共入口一致） | 有意惯例映射，非行为分歧；探针 NULL 3 例双侧归一一致 |
| `take(负数)` → `IllegalArgumentException` | `ArgumentOutOfRangeException` | 同上 |

V4 对合法输入无上限/去重约束，V5 保持同域，未新增任何拒绝路径。

---

## 5. 全部已知缺陷与限制（一次性列全）

| ID | 级别 | 描述 | 处置 |
|---|---|---|---|
| D-1 | MINOR | 异常类型按 C# 惯例映射（IllegalArgumentException→ArgumentOutOfRangeException、NPE→ArgumentNullException），类型名与 V4 不同，语义角色一致 | 已声明并经探针证实；接受 |
| D-2 | MINOR | 内部排序算法与求值次数不同（TimSort 逐比较重算 vs LINQ 单次求值）；可观察排序输出经 5,054 例 0 差异，性能未做基准比较 | 已声明；排序输出契约不受影响 |
| D-3 | MINOR | 组件为纯函数库，无自动化防误用护栏阻止未来调用方把分值写入 CommandIntent 哈希/收费事实（约束目前仅存在于类型文档与本记录） | 后续接线任务（capability/actor）须复核此约束；本轮不越界加机制 |
| D-4 | 信息 | 首轮测试运行 115 例中 6 例失败：全部为**测试侧手算期望值错误**（[16,0,108] 升序误写、[19,12,13] 分值误算、limit 100 超界期望误写、两组组合分值算错），非生产实现缺陷（实现当轮已被探针证实与 V4 一致）；同会话修正后 115/115。该轮失败日志被终版复跑覆盖（同路径 TestResults），此处如实记录，不另造伪造日志 | 已修正并终版全绿 |
| D-5 | 信息 | ENG-021 行在冻结 implementation-v1.json 中仍为 partial，由 caller 统一结算；本记录不改矩阵 | 移交 caller |

---

## 6. 命令、exit 与计数（终版，日志 SHA-256 见 §8）

| # | 命令 | exit | 结果 |
|---|---|---|---|
| 1 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal` | 0 | **0 警告 / 0 错误** |
| 2 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal` | 0 | **115 通过 / 0 失败 / 0 跳过**（94 已验收基线 = 50 基线/黄金 + 44 policy，加本轮 21） |
| 3 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release`（同参） | 0 | **322 通过 / 0 失败 / 0 跳过**（兼容面不变） |
| 4 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release`（同参） | 0 | **104 通过 / 0 失败 / 0 跳过** |
| 5 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release`（同参） | 0 | **132 通过 / 0 失败 / 0 跳过** |
| 6 | `bash <TEMP>/run-probe.sh`（kotlinc 2.4.10 编真实 V4 → 双侧探针 → diff + SHA256） | 0 | `DIFF: identical`，双侧 SHA256 `b294be5f…39a15996c` |
| 7 | `git diff --check` | 0 | 无空白错误（空输出） |
| 8 | `git status --porcelain` | 0 | 仅 §1 三文件，无越界 |

Ai 分母核对：115 = 94（既有）+ 21（本轮 `AiCheatHeuristicsTests`）。既有 94 未删改（`AiDecisionTests.cs`、`AiCheatPolicyTests.cs`、`BaselineAiStrategyTests.cs`、`AiReplayGoldenTests.cs` 原样）。

---

## 7. NOT_RUN（如实单列，不混写 PASS）

- PostgreSQL / WSS 端到端 / TableRuntime / LocalHost 集成：NOT_RUN（本轮不改这些层）。
- Unity / IL2CPP / Android 真机 / iOS：NOT_RUN（平台阻塞保持既有记录）。
- 跨物理网络实测：NOT_RUN。
- V4 Gradle 全量构建/测试：NOT_RUN（探针只编译了 V4 的 Tile.kt + AiCheatHeuristics.kt 两个真实源文件，不宣称 V4 全量状态）。
- Debug 配置测试：NOT_RUN（本轮交付口径为 Release；P16 policy 任务曾有 Debug 记录，本轮未重复）。
- 远端 CI / push / PR：NOT_RUN（由 caller 负责 push 与唯一 PR19 `feat/v5-1-integration→v5` 及 merge-back；本任务未 push、未建 PR、未触发 CI）。

## 8. 证据物清单与 SHA-256

全部位于独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/eng021-heuristics-probe/`：

```text
release-build-solution.log      6fe0da4f0c0b7886d676d2e201bab81c52cfb4d3f9cf22e9c10b5f42963c21f5
release-test-Mahjong.Ai.Tests.log      ce0ed8a6a7ac3acdd732da24eb91bc40ed9ad4acaf5fa0374d86dac154f48e86
release-test-Mahjong.Domain.Tests.log  3a643419d792e559afd1d457a8c742a7fffb942bee16e491460dfeebe2adf73b
release-test-Mahjong.Contracts.Tests.log bd487e699d17baf83776f211e9d8e26e8a1a2a61a30dc9fb83e6c86150c4d4d2
release-test-Mahjong.Client.Tests.log  75a61488e98a4acf7acf6adee6773135bc0a2d7da06b518fe4c687781df01c16
git-diff-check.log              e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855（空文件）
dump-kotlin.txt                 b294be5fce4f7c640e961caa4c1d259cbd64a7eb98fb0641959f01839a15996c
dump-csharp.txt                 b294be5fce4f7c640e961caa4c1d259cbd64a7eb98fb0641959f01839a15996c
kotlin-stderr.txt               7dc573d07c562c258c24bd77c683d0ca092cd46700d63afa9c0be9a6c33fac57
csharp-stderr.txt               e2c931598ca62bde4bb16104d546a7e2dc0d256cc528dd6d424ac9c6218dcf36
Probe.kt                        c1ce0bf758121bd8d4efdbe502e0df7e25ab0939f94a33589b2fffc68e07b968
Program.cs                      bf33a7e54561d862f516c8fe378fda92f40cdb1e3ee8e3158bb89abe47c9ce9c
Probe.csproj                    30fc3f2e15a35f087948dd7110c3b84748f04149ba099d5a364131327adefedf
run-probe.sh                    70227db2312c7ea680d471304392b4daf8e91b01d458daca02e7daeb1bcb1721
```

探针未加入任何 golden 分母，未手填任何哈希（dump 双侧由各运行时独立产出，SHA256 由工具计算）。

## 9. 结论口径

ENG-021 的**组件**（`AiCheatHeuristics` 四函数：TileValue / LeastValuableHandTiles / MostValuableHandTiles / RelativeValue）已按 V4 源码行为移植，真实 V4 源码编译执行的独立差分探针 5,054 例 0 差异，Ai 套件 115/115、Domain 322、Contracts 104、Client 132 全绿，Release 全解决方案 0 警告 0 错误。真实能力消费与 actor 接线仍待后续任务；**不宣告 P1.6、G1 或 G5 完成**；V4 仍是唯一生产基线。
