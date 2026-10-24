# V5 P1.6 基线策略交付：TrivialAiStrategy 与 TsumogiriAiStrategy

日期：2026-10-24
任务：追踪矩阵 [implementation-v1.json](implementation-v1.json) 中 `ENG-014`（`ai/TrivialAIStrategy.kt`）与 `ENG-015`（`ai/TsumogiriAIStrategy.kt`）两条 `partial` 缺口的补齐实现。
范围声明：**本任务只交付这两个真实缺失的基线策略**，不宣告 P1.6 完整，不宣告 G1/G5 通过；V4 仍是唯一生产基线。
基线：工作树 `D:/majiang-wt/ai-baseline-strategies`，分支 `wt/v5-ai-baseline-strategies`，起始 HEAD `6f1fbee7d8fe94bf98307800168acf56b05d19c4`（已审 PR19 冻结点）。

---

## 1. 交付文件（expectedFiles，共四个）

| 文件 | 内容 | SHA-256 |
|---|---|---|
| `dotnet/src/Mahjong.Ai/TrivialAiStrategy.cs` | ENG-014 移植 | `299866bd3c65431f1677fdd45203a2cdadde6e3c2b782f919a60d2bfbbd7ebde` |
| `dotnet/src/Mahjong.Ai/TsumogiriAiStrategy.cs` | ENG-015 移植 | `a3cebb9f0b04db2ced2548caef1073a4b45c250af676737dbf02c7f0629bd63e` |
| `dotnet/tests/Mahjong.Ai.Tests/BaselineAiStrategyTests.cs` | 行为矩阵测试（33 项） | `2452dd367c1bca8980fca8fd162c5cfaf33a7048d89c67f50d86cb4cd9f93ed1` |
| `doc/v5-重构/V5-P16-BASELINE-STRATEGIES.md` | 本交付文档 | 提交后见 commit |

无其他生产/测试/文档文件被修改或删除。构建产物与原始日志在 ignored 目录 `artifacts/p16-logs/`，不入库。

## 2. V4 来源与逐分支映射（以 V4 实际源码为准，非类注释）

V4 来源文件（基线 HEAD 处读取）：
- `engine/src/main/kotlin/com/majiang/ai/TrivialAIStrategy.kt`
- `engine/src/main/kotlin/com/majiang/ai/TsumogiriAIStrategy.kt`

两文件 V4 `name` 字段同为显示用字符串 `"摸切AI"`；这不构成算法身份。V5 中算法身份进入 `CommandIntent` 规范字节与 `AuthorityHash`，因此两个策略必须且已经持有不同的稳定 `AlgorithmId`。

### 2.1 TrivialAIStrategy → TrivialAiStrategy（`AlgorithmId="baseline-trivial"`，version 1）

| V4 源码分支（`decideAction`） | V5 实现（`TryChoose`/`Decide`） |
|---|---|
| `legalActions.isEmpty() → null` | `TryChoose`：空列表返回 null（与 V4 完全一致） |
| `firstOrNull { it is DeclareRon }` | 第一个 `Kind==Ron` 候选（列表序） |
| `firstOrNull { it is DeclareTsumo }` | 第一个 `Kind==Tsumo` 候选（列表序）；仅当无 Ron |
| `discardActions = filterIsInstance<DiscardTile>()`（保序） | 按 `Kind==Discard` 保序扫描 |
| `drawn = view.myDrawnTile; firstOrNull { it.tile == drawn }` | `drawnTileId.HasValue` 时第一个 `Tile==drawnTileId` 的 Discard（无座位过滤，忠实 V4） |
| `view.myClosedTiles.lastOrNull()` 匹配 | `MyHandTileIds` 末位与 Discard.Tile 匹配；drawn 存在但不合法时同样进入该兜底（V4 代码实际如此，类注释“无 drawnTile 时”不准确，本移植以代码为准并在测试 `TrivialDoesNotMatchDrawnTypeOnADifferentCopyAndFallsToHandLast` 固化） |
| `discardActions.last()` | 列表序最后一个 Discard（非按牌值） |
| `firstOrNull { it is Pass }` | 第一个 `Kind==Pass` |
| `legalActions.first()` | `legalActions[0]`（任意 Kind 兜底） |

V4 类注释称“永不副露/立直”，实际代码并无该否决——副露/立直候选只因更高优先级分支（和牌→弃牌→Pass→首个合法）先行而被跳过。本移植如实保留代码行为：响应窗口只有鸣牌+Pass 时选 Pass（`TrivialPassesWhenOnlyMeldCallsAndPassExist`），只有立直+杠等无弃牌候选时兜底选**首个合法动作（含立直）**（`TrivialTakesFirstLegalActionAsFinalFallback`）。不自立新麻将规则。

### 2.2 TsumogiriAIStrategy → TsumogiriAiStrategy（`AlgorithmId="baseline-tsumogiri"`，version 1）

| V4 源码分支（`decideAction`） | V5 实现 |
|---|---|
| `isEmpty() → null` | `TryChoose`：空列表返回 null |
| `firstOrNull { Pass && player==viewerSeat } ?: firstOrNull { Pass }` | 先本座 Pass，再任意座 Pass（双层保序） |
| `drawn != null → firstOrNull { Discard && player==viewer && tile==drawn } ?: firstOrNull { Discard && tile==drawn }` | 先本座摸切，再任意座摸切 |
| `firstOrNull { Discard && player==viewerSeat } ?: firstOrNull { Discard }` | 先本座首个 Discard，再任意座首个 Discard |
| `legalActions.firstOrNull()` | `legalActions[0]` |

座位过滤与兜底逐层保持 V4 原样；无 Ron/Tsumo 扫描（永不自和），与 Trivial 的“能和就和”构成本质差异，两策略未合并。

### 2.3 牌实体相等映射（红五/同 type 不同 copy）

V4 `Tile` 是 `data class`（`suit, rank, copyIndex` 结构相等）：红五为“每种数牌 5 的 copyIndex==3 物理副本”（`Tile.kt:49-52`），`it.tile == drawn` 因此**含副本号**——红五（copy 3）不等于普通五（copy 0）。V5 `TileId` 值相等（`id=typeIndex*4+copy`，0..135）语义一一对应：typeIndex 同而 copy 不同即不同实体。`BaselineAiStrategyTests.TrivialDistinguishesSameTypePhysicalCopiesIncludingRedFive` 用筒五 88（copy 0）与 91（红五）双向固化：drawn=91 选 91 的候选、drawn=88 选 88 的候选，绝不互混；`IsDiscardOfTile` 匹配同时要求 `Kind==Discard && Tile.HasValue && Tile.Value==tile`，不取 typeIndex 代替实体。差异边界：V4 还比较 `suit/rank` 字段本身，V5 由 typeIndex 承载；两者在 0..135 值域内等价，无行为分叉。

## 3. 现有接口适配差异（V4 语义 → V5 合同）

1. **非空返回签名**：V4 `decideAction` 返回可空 `GameAction?`；V5 `IAiStrategy.Decide` 非空。适配：每个新策略类提供 **`TryChoose` 可空入口**（忠实镜像 V4 签名语义：空候选 → null），`Decide` 委托 `TryChoose` 并在 null 时抛 `InvalidOperationException`。**`AiObservation` 构造器拒绝空候选列表**（`"An AI observation must contain at least one legal candidate."`），故该 null 分支经 `Decide` 不可达；保留为显式“绝不凭空造动作”的防御，不放宽既有构造验证，不修改既有接口。
2. **viewerSeat 过滤塌缩**：V4 Tsumogiri 的“本座优先/任意座兜底”双层扫描，在 V5 `IAiStrategy` 路径上第二层**结构性不可达**——`AiObservation.CopyActions` 强制每个候选 `Seat == viewing seat`，跨座候选无法被构造进 observation，因此**不能也不需要伪造跨座候选走 Decide 测试**。兜底层保留在 `TryChoose`（V4 移植保真），并由 `TsumogiriSeatFallbackArmsMirrorV4SourceButAreUnreachableViaObservation` 直接以裸列表覆盖，同时文档与测试注释声明该边界。Trivial 无座位过滤（V4 即如此），照搬。
3. **算法身份**：V4 `name="摸切AI"` 为显示字符串且两策略同名；V5 `AlgorithmId`（`baseline-trivial` / `baseline-tsumogiri`，均 version 1）为常量、互异、进入 `CommandIntent` 规范字节——同一候选被两策略选中时 `AuthorityHash` 必不同（测试 `StrategiesCarryDistinctStableAlgorithmIdentity`）。
4. **heuristic 分数**：两策略为确定性单选、不排名，`AiStrategicChoice` 恒报 `0d`；分数不进入 `CommandIntent`（既有 `CommandIntentAuthorityHashIsIndependentOfFloatingHeuristicScores` 继续守护），未改变 `CommandIntent` 现有编码/录制回放。
5. **取消语义**：V4 无取消；V5 在入口与每个扫描循环检查 `CancellationToken`，取消时抛 `OperationCanceledException` 而不是造一个选择（`StrategiesHonorCancellationInsteadOfFabricatingAChoice`、`ReasonAsyncPropagatesCancellationBeforeAnyStrategyWork`）。
6. **脱敏**：两策略 `ToString()` 为常量（类名+算法身份+版本），异常消息不含 observation/手牌/牌山内容；`StrategyToStringIsDecisionIndependentAndCarriesNoTileData` 固化。策略只读 `AiObservation`（其集合为防御复制的只读视图），不读全局状态/时钟/RNG/墙/他家暗牌——`Mahjong.Ai` 程序集引用面由既有 `ObservationSurfaceExposesNoHiddenDomainState` 继续约束，本任务未引入新依赖。
7. **返回原候选**：选择结果即输入列表中的原实例（`Assert.Same` 固化），`OptionId` 与物理 `TileId` 原样保留；`TryChoose` 不修改输入列表（`TryChooseDoesNotModifyItsInputLists`）。

## 4. 非覆盖范围与 actor 接线边界

- **不做**：`BasicAIStrategy`/`TargetAwareAIStrategies`（浮点启发式）、`AiCheatPolicy`/`cheat/*` 能力策略、`AIPlayer` 组合——它们是 ENG-014/015 之外的同任务族条目，本任务不扩 scope。
- **不做**：`ServerTableHost`/actor 的 AI 调度接线（V5 属 TableRuntime/P3.1/P3.3 既有合同）；本任务只验证“真实 `AiDecisionService.ReasonAsync` 产生 `CommandIntent` → `Submit` → `DeadlineArbiter`”这条既有公开链路（`ReasonAsyncTrivialIntentIsAdmittedAndAppliedByTheDomainArbiter`），stale round / stale epoch / deadline 到点拒绝沿用既有 `AiSubmissionResult` 行为（`SubmitRejectsStaleRoundEpochAndLateStrategyIntents`），未复制服务内部实现写同构测试。
- **不动**：actor/Domain/Contracts/AI 既有四文件（`AiDecision.cs`、`AiObservation.cs`、`AiDecisionService.cs`、`CommandIntent.cs`）、黄金 fixture（`tests/v5-golden/ai-v1.json`）、追踪矩阵、统一 verifier、CI、既有 `IAiStrategy` 接口。
- V4 侧：本任务**零改动**（无新增 V4 导出器、无既有 V4 文件修改；V4 已有 `TrivialAIStrategyTest.kt` 四例的行为由本套件以 V5 合同重新表达，不声称“V4 自动导出”）。
- 既有 `ai-v1` 记录意图回放（`AiReplayGoldenTests`）保持全部通过。

## 5. 测试矩阵与运行证据

### 5.1 新增测试（`BaselineAiStrategyTests`，33 项，全为有意义的非同构断言）

- **Trivial 优先级**：Ron>Tsumo>弃牌>Pass>首合法；Ron 与 Tsumo 同现按 Ron（与列表序无关）；弃牌分支先于 Pass；仅鸣牌+Pass 选 Pass；无弃牌候选兜底选首合法（含立直不被注释性否决）。
- **Tsumogiri 优先级**：有 Pass 永不选择和牌/弃牌/鸣牌；无 Pass 摸切 drawn；drawn 缺席/不合法取首个本座弃牌；无 Pass 无弃牌取首合法。
- **两策略差异（同输入异输出）**：响应窗口 [Pass, Discard] Trivial 弃牌 / Tsumogiri Pass；[Pass, Ron] 与 [Discard, Tsumo] 的胜负差异；无 drawn 多弃牌按“闭手末位 vs 列表首位”分歧；闭手末位无匹配时“列表末位 vs 列表首位”分歧；选择由列表位置而非牌值决定。
- **实体身份**：同 type 双 copy（普通 88 / 红五 91）双向不互混；drawn 非法而同 type 另一 copy 合法时不以 type 代替实体、落入闭手末位兜底。
- **顺序/身份/完整性**：同级多候选取首个实例；返回原候选实例且 `OptionId`/`Tile` 保真；输入列表零修改。
- **合同**：空候选 `TryChoose` 返回 null（V4 null 语义）；`AiObservation` 拒绝空候选（不可达边界固化）；同步/异步取消均不造选择；null 实参抛 `ArgumentNullException`；算法身份互异且同选择异哈希；`ToString` 恒定无牌信息；座位兜底层裸列表覆盖并声明 Decide 路径不可达。
- **真实服务接线**：`ReasonAsync`（真实 `AiDecisionService`）→ `CommandIntent`（真实算法身份、heuristic 0）→ `Submit` 接受 → Domain `DeadlineArbiter` 同 tick 接受同动作；同一 observation 上两策略产生互异意图；stale round / stale epoch / deadline 到点拒绝且无 `Submission`、deadline-1ms 接受；录制意图 `Replay` 注入不重跑策略且哈希一致可再接受。

### 5.2 运行命令、退出码与计数（全部真实执行，dotnet 8.0.426 @ `D:/Unity/dotnet/dotnet.exe`）

| # | 命令（cwd = 工作树根） | exit | 结果 |
|---|---|---|---|
| 1 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj`（Debug 全量） | 0 | **50 通过 / 0 失败 / 0 跳过**（既有 17 + 新增 33） |
| 2 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal -- --filter-class "Mahjong.Ai.Tests.BaselineAiStrategyTests"` | 0 | **33 通过 / 0 失败 / 0 跳过**（仅新增类，MTP `--filter-class`，仓库既有模式） |
| 3 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release` | 0 | **50 通过 / 0 失败 / 0 跳过** |
| 4 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release` | 0 | **322 通过 / 0 失败 / 0 跳过** |
| 5 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release` | 0 | **104 通过 / 0 失败 / 0 跳过** |
| 6 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release` | 0 | **132 通过 / 0 失败 / 0 跳过** |
| 7 | `dotnet build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 0 | 已成功生成，**0 警告 / 0 错误** |
| 8 | `git diff --check` | 0 | 无空白错误（提交前再对暂存区 `git diff --cached --check` 复核） |

原始日志（ignored 目录，含 SHA-256）：
`artifacts/p16-logs/release-test-Mahjong.Ai.Tests.log` `a69678a0e90a0c2e6d3ec9047dc6b34bffef28f89b4b2c37ae00d60bf9f4fa4d`、
`.../release-test-Mahjong.Domain.Tests.log` `0cf32da2df29b4e63db5f18a8881515cae143f75ed533986451d462e4c05dc9e`、
`.../release-test-Mahjong.Contracts.Tests.log` `53074d94ab6d418d7416b751acc5c7a0d7ef139367380947af9504205a587863`、
`.../release-test-Mahjong.Client.Tests.log` `a4227175a6bb9e77ddc5978b5a247eee60d9fa22cffc786e8c63f9fe2ed8286a`、
`.../release-build-solution.log` `93c0790689da608d617a9eeb5a3e30793d2a2176321e76f5a1db7b17187fbb0c`。

口径说明：Total 均非 0、无 skip、无旧 DLL 凑绿（2/3 号命令当场重建）；未修改任何 verifier、未弱化任何既有断言；黄金回放 `AiReplayGoldenTests` 在同一工程内随全量运行通过。

### 5.3 未运行项（如实声明 NOT_RUN）

| 套件/平台 | 状态 | 理由 |
|---|---|---|
| `Mahjong.TableRuntime.Tests`、`Mahjong.Client.Wss.Tests`、`Mahjong.LobbyService.Tests`、LobbyService PostgresProbe、真实 PG / WSS 大套件 | **NOT_RUN** | 与纯新增 AI 策略无编译/行为耦合（`Mahjong.Ai` 无新依赖、Solution Release 构建已覆盖全部工程的编译面），按任务指示不重跑 |
| Unity / IL2CPP / Android / iOS、V4 Gradle 全量、跨物理网络 | **NOT_RUN** | 不在本任务范围；V4 T4 等历史缺口维持原状 |

## 6. 结论与边界

- ENG-014 / ENG-015 的“确定性基线策略未移植”缺口已按 V4 实际源码行为补齐，测试矩阵 33 项全绿，既有 17 项与黄金回放不受影响。
- 追踪矩阵两行仍是 `partial→待复核` 的收敛判断：本任务不自行改写 `implementation-v1.json` 的 status（矩阵是已验收产物，改写属 caller/独立复核职权）。
- **P1.6 不因本交付而宣告完整**（同任务族还有 Basic/TargetAware/cheat 能力策略等条目），**G1 未过、G5 未过**；V4 仍是唯一生产基线。
- 交付方式：本地 commit，工作树 clean 后停止，等待 caller 独立复核；不 push、不触发 CI、不创建新 PR。
