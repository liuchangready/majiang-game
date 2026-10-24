# V5-1-REVIEW — 首次当前集成头阶段审查报告（G1 证据盘点）

> 本报告由实现侧审查代理产出，**不构成 G1 门槛批准**。caller 需独立复核本报告全部证据锚点后另行判定。
> 依据 `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md:108`："生成 `V5-1-REVIEW.md` 记录实际测试数、失败、设备与未覆盖范围。"

## 0. 审查元信息

| 项 | 值 |
|---|---|
| 审查对象（唯一固定头） | `8ee22ebf5164f74db75e5a33e87900f5b87cd8bd`（已复核并发布的 PR19 head，分支 `feat/v5-1-integration`） |
| 审查工作树 | `D:\majiang-wt\g1-readiness`，分支 `wt/v5-g1-readiness`，审查起止 `git status --porcelain` 为空（clean），HEAD 与固定头一致 |
| 审查日期 | 2026-10-25 |
| 审查性质 | 只读源码/证据审计 + 独占探针；唯一文档写 scope 即本文件 |
| 结论 | **G1 = BLOCKED（维持）**；9 项门条件中 5 项 PASS、2 项 FAIL、2 项 BLOCKED/受限（§3） |
| 历史冻结审计 | 依赖矩阵冻结于 `437bd53`、黄金目录冻结于 `205506f`：本报告**引用其结论，不改写历史、不把已完成组件重列为未做** |

**范围声明**：本报告不重跑 P0.1 冻结矩阵/黄金 validator 的历史工作；不把组件测试全绿推断为"有效 V4 规则已全迁移"；不泛化审查 V5-2..V5-5 未开始功能（仅列门控）；不读取/不合入两个在途兄弟任务的未提交产物。

## 1. 环境前置核对（实际执行）

- `git rev-parse HEAD` → `8ee22ebf5164f74db75e5a33e87900f5b87cd8bd`；`git branch --show-current` → `wt/v5-g1-readiness`；`git status --porcelain` → 空。
- 未触碰 `D:/majiang` 主 checkout 及其用户未提交文件；未 reset/覆盖任何兄弟工作树。
- 读过且仅读过：`AGENTS.md`、`doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md`、复核协议（只读）。

## 2. 总结论

G1 九项门条件（门文本原文见 `V5-0-1-IMPLEMENTATION-TASKS.md:106-108`）逐项状态：

| # | 门条件 | 状态 | 摘要 |
|---|---|---|---|
| 1 | `dotnet test` 独立全绿 | **PASS**（事实层） | 双 run 亲自核验 1,356/0/0（§5.2）；"组件绿"不等于"功能全迁移" |
| 2 | 已导出黄金样本对齐率 100%，未导出单列 | **PASS** | 本审查树亲跑 validator PASS（§6.0）；未导出=none:33 单列 |
| 3 | 依赖方向静态测试通过 | **FAIL** | 仅 Lobby 有装配级静态测试；核心 5 模块图无守护（D-1）；现状图经本轮探针实测干净（§6.1） |
| 4 | Domain 不含隐式随机/时钟/浮点权威 | **PASS（现状）+ 守护缺口** | 严格 grep 干净（§6.2）；无强制静态测试（D-2） |
| 5 | 三绝技与 136 守恒通过 | **FAIL** | 136 守恒=组件级证据；三绝技=仅独立组件，权威链未接线（D-3/D-4，§4.2/§4.3） |
| 6 | Wire 可重复生成 | **PASS** | 稳定字节/字段号/错误码稳定 Fact 群（§3.6） |
| 7 | 按座位/观战私有性通过 | **PASS** | 6 个可见性 Fact + 观战者命令拒绝 + 座位级 AI 观察（§3.7） |
| 8 | TableRuntime 并发、版本与恢复合同通过 | **PASS**（组件级） | 313 用例 + CI 恢复/租约/回执证据事件（§3.8、§5.4） |
| 9 | Unity/IL2CPP 可用平台的构建日志和缺口分列 | **BLOCKED** | 双 run 无 Unity/IL2CPP job；平台环境缺口未重探查，维持历史事实（§3.9） |

**G1 不能关闭**：按门文本严格读，门条件 3、5 未满足，条件 9 无法出示当前构建日志。在 P3.2/P3.3（命令/事件面补齐与计分接线）完成、D-1/D-2 守护测试落地之前，保持 V4 生产基线，不进入 V5-2/3 集成验收。

## 3. G1 门条件逐项证据

### 3.1 `dotnet test` 独立全绿 — PASS

两个 CI run 的每项目 meta JSON 均亲自读取（非转抄 caller 计数），7 个测试项目两 run 均 exit 0，合计 **1,356 passed / 0 failed / 0 skipped**：

Ai 316、Client 132、Client.Wss 80、Contracts 104、Domain 322、Lobby 89、TableRuntime 313。解构建 exit 0、12-DLL 闭包 PASS、summary 9 行 OVERALL PASS（7 测试项目 + 2 个仅构建 helper，helper 未计入用例数）。run 头与产物 SHA 见 §5。

### 3.2 黄金对齐（已导出 100% / 未导出单列）— PASS

本审查树在固定头亲自运行（非引用 caller 结果）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot .
→ PASS, EXIT=0
catalog=.\tests\v5-golden\catalog-v1.json
sha256=16F7FD7F980398388E4E5DA45A3D5FB755C19990915F403987094617E90E891B
fixtures=5 requirements=73 cases=10382 random=10000 facts=42
coverage=full:17/partial:20/none:33/v5-only:3
```

- 已导出样本（10,382 用例）经冻结 validator 全部对齐（历史冻结于 `205506f`，本 head 复跑同 SHA 通过）。
- 未导出/无覆盖需求 33 项单列于目录 coverage 字段；**不声明任何"整体对齐百分比"**。
- 42 役种口径在源码锚定：`dotnet/src/Mahjong.Domain/Rules/Yaku.cs` 恰含 42 个 `new Yaku(` 条目。

### 3.3 依赖方向静态测试 — FAIL（守护缺失，现状干净）

现有静态测试仅一处：`dotnet/tests/Mahjong.LobbyService.Tests/ArchitectureAndPolicyTests.cs:13` `LobbyAssemblyDoesNotReferenceDomainOrTableRuntime`（反射检查 Lobby 程序集引用）。全仓 grep `DependencyDirection|ProjectReference|layering|architecture` 于 tests/scripts/tools 无其他静态依赖测试。

本轮探针实测当前图**是干净的**（csproj `ProjectReference` 全量 + `using` 级交叉验证，§6.1）：

```
Mahjong.Domain         => （无）
Mahjong.Contracts      => （无）          # Contracts ↛ Domain ✓
Mahjong.Contracts.Wire => Contracts
Mahjong.Ai             => Domain          # Ai ↛ TableRuntime（using 级 grep_rc=1）✓
Mahjong.Client         => Contracts, Contracts.Wire   # Client ↛ Domain ✓
Mahjong.LobbyService   => Contracts
Mahjong.TableRuntime   => Domain, Ai, Contracts（组合点）
Mahjong.LocalHost      => TableRuntime（工程空壳，0 个 .cs）
```

但"当前干净"只是点值事实，G1 要求的是**静态测试**。核心 5 模块图无自动守护 → 门条件未满足，缺陷 **D-1**。

### 3.4 Domain 无隐式随机/时钟/浮点权威 — PASS（现状）+ 守护缺口

严格 grep（`dotnet/src/Mahjong.Domain` 全部 .cs）：`new Random(|DateTime.Now|DateTime.UtcNow|DateTimeOffset.Now|DateTimeOffset.UtcNow|Guid.NewGuid|Stopwatch.|Environment.TickCount|Thread.Sleep|Task.Delay|Math.Random|nanotime` → **零命中（grep exit 1）**。

存在的两类边界情况（均非"隐式权威"，记录在案）：

1. `RoundRecovery.cs:735/790/876/893/1184/1242`、`RoundStateMachine.cs:1039/1063`：读取**调用方供给**的 `settlement.CompletedAtUtc`（`RoundTypes.cs:61-71` `PersistedUtcTime`）并哈希——时间作为**记录输入**进入，非墙上时钟。
2. `Stacking/ShuffleConfig.cs`：`double` 仅作码牌调参（Strength/Interference 等），以 `"R"` 不变文化格式化（`ShuffleConfig.cs:97`）；shuffle 确定性由黄金 shuffle-v1（32 用例）与跨 JVM/.NET LCG 差分（P0.1 冻结）覆盖。

**缺口**：无 source-scan 守护测试，上述干净状态无回归防护 → 缺陷 **D-2**。

### 3.5 三绝技与 136 守恒 — FAIL（三绝技未接线；守恒为组件级）

**136 守恒（组件级证据存在）**：
- 结算守恒验证：`RoundStateMachine.cs:742-750`（score/stick 起终恒等）、`:802-822`（结果一致性），`:482` 处 `checkConservation:false` 分支显式限定于非结算路径。
- 码牌/物理：黄金 shuffle-v1（32 用例）、physical-v1（P0.1 冻结差分）；V4 对应锚 `engine/.../GameEvent.kt:23` `WallBuilt(wallSize=136)`。

**三绝技（仅独立组件 + 契约面，权威链未接线）**：
- 组件：`dotnet/src/Mahjong.Domain/Cheat/` 8 文件 —— `SleeveBloom`（袖里藏花）、`BeamSwap`（偷梁换柱）、`TileMark`（落汗标记面）、`CheatSubmission`、`CheatLedger`、`CheatState`、`CheatEconomy`、`CheatProjection`。
- 契约：`ProtocolCommand.cs` `CommandKind` 27 种含出千命令面。
- 携带：actor 在 checkpoint/恢复/投影携带 cheat 状态（`TableActor.cs:10` using；`TableCheckpointCodecs.cs:8`；`TableCheckpointValidator.cs:5`；`TableEventProjection.cs:7`；`TableProjectionContractMapper.cs:809,854` SleeveBloom 视图映射；座位流要求受信 cheat 投影上下文 `TableActor.cs:5955-5969`）。
- **未接线**：权威动作入口 `TableActor.MapAction`（`TableActor.cs:4819-4834`）仅映射 `Pass`/`Discard`，`default:` 返回 null，注释明示 "Scoring/call/cheat actions need the P3.2/P3.3 adapters. P3.1 must not invent a settlement or a meld."；调用方得 `UNSUPPORTED_ACTION`（`:1436`、`:1623`）。grep `BeamSwap|CheatLedger\.|CheatEconomy\.|CheatProjection\.` 于 TableActor 无裁决调用命中。

→ 门条件 5 的"三绝技通过"当前不成立（缺陷 **D-3/D-4**），必须待 P3.2/P3.3。

### 3.6 Wire 可重复生成 — PASS

锚点（`dotnet/tests/Mahjong.Contracts.Tests/`）：`identical_projection_maps_to_stable_bytes`（ProjectionWireMapperTests.cs:12 起）、`EnvelopeFieldNumbersAreStable`、`CommandReceiptCursorAndSyncFieldNumbersAreStable`、`ErrorVocabularyIsStableWellFormedAndHasNoDuplicates`、`ApplicationCloseCodesMapToStableWebSocketCodes`、`EveryV4ApiErrorMapsToADistinctStableV5Code`、`RulesetAndProjectionMismatchesHaveDistinctStableCodes`、`StreamVersionMayRepeatWhileProjectionSeqAdvances`。Contracts.Tests 104 用例双 run 全绿。

### 3.7 按座位/观战私有性 — PASS

- 投影层：`dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilityProjectionTests.cs:16-181` 六个 Fact——`public_snapshot_uses_explicit_public_fields_only`、`player_snapshot_keeps_own_hand_and_draw_private`、`decision_is_visible_only_to_its_owner`、`private_events_reach_only_owner`、`public_events_reach_every_seat_and_spectator`、`settlement_events_gate_only_spectator`；另有 `different_hidden_states_produce_identical_public_snapshot`、`public_snapshot_does_not_expose_hidden_wall_or_hand_tiles`、`event_bodies_preserve_source_versions_and_redact_hidden_payload`。
- 流层：`TableStreamWireIntegrationTests.cs`/`TableStreamActorTests.cs` 含 `SpectatorMustRejectPlayerCommandBeforePersistenceAndSend`（持久化与发送前拒绝观战者命令）。
- AI 观察层：`TableActor.CaptureAiObservationAsync`（`TableActor.cs:390-404`）→ `ProcessAiObservation`（`:4105-4200`）构建座位级 `AiObservation`（本座位手牌计数/摸牌/明副露/牌河/宝牌/分数/候选），带 Serving/时钟/决策窗/控制纪元守卫。

### 3.8 TableRuntime 并发、版本与恢复合同 — PASS（组件级）

- TableRuntime.Tests 313 用例双 run 全绿；P3.4 回合 R3-R6 探针文件在树（`CodexP34Round3..6ReviewProbes.cs`）。
- 持久合同：`TableDurableContracts.cs`（state_version=命令版本 CAS、event_seq 逐事件、ownerEpoch+stateVersion 双栅栏、checkpoint 后重放校验）；租约/恢复 `TableDurableLeaseRecoveryTests.cs`、`TableRecoveryService.cs`。
- CI 证据事件（两 run 相同）：`recovery.expiredReservation`、`receipt.replay`、`receipt.terminalReplay`、`concurrency.crossRoomJoin ×3`、`crossConnectionRowLock`、`transaction.rollback`、`ticketConsumption`（§5.4）。

### 3.9 Unity/IL2CPP 可用平台构建日志与缺口分列 — BLOCKED

- 双 run（Windows runner）均无 Unity/IL2CPP/macOS/Android 构建 job；本轮未重探查平台环境（无 Unity 许可/MSVC/macOS/Xcode/真机证据产出）。
- 维持历史事实口径：引用 `doc/v5-重构/UNITY-ENVIRONMENT.md` 与 V5-0 测量记录中的缺口清单，**不将历史证据表述为当前实测**。
- `Mahjong.LocalHost` 工程仅含 csproj（0 个 .cs），P4.1 未开工，G2 门控不受影响地成立。

## 4. 组件状态分类盘点（P1.1–P3.4）

分类口径：完整功能 / 仅独立组件 / 接口先行 / 未接线 / 平台资源阻塞。

### 4.1 Domain 规则核心 — 仅独立组件（受限命令/事件面），未全接线

- 事件面：`RoundTransition.cs:56-74` `RoundEventType` **16 值** vs 冻结 19 类（`engine/src/main/kotlin/com/majiang/event/GameEvent.kt:20-108`）。
- 命令面：`RoundTypes.cs:31-45` `RoundActionKind` **9 种** vs 契约冻结 `ProtocolActionType` **13 种**（`ProtocolCommand.cs:40-56`）。
- 精确差集（本轮逐类比对）：
  - 动作缺 5：`DeclareChi(5)`、`DeclarePon(6)`、`DeclareKan(7)`、`DeclareRiichi(8)`、`DeclareAgariYame(13)`；V5 独有 `DeclareAddedKan`（加杠），V4 侧归入 DeclareKan 变体。
  - 事件侧：V4 19 类中 `ChiDeclared`、`PonDeclared`、`RiichiDeclared`、`CheatPointsChanged`、`ScoreSettled`、`WallBuilt`、`MatchEnded` 在 V5 `RoundEventType` 无同名独立事件（V5 以 `ResponseClaimed` 泛化吃/碰/杠明牌、结算并入 `RoundEnded`、MatchEnded 归 `MatchProgression.cs` 另线）；V5 新增 `ResponseWindowOpened`/`ChankanWindowOpened`/`WallExhausted` 为 V4 无的窗口/终局事件。**该映射差集必须在 P3.2 产出一对一的冻结映射表**，不得静默改名。
- 结算：状态机**只验证不计算**调用方供给的 `RoundSettlement`（头注释 `RoundStateMachine.cs:21`；守恒 `:742-750`；结果一致 `:802-822`）。
- 工厂齐备：StartStacking/Draw/Discard/Tsumo/Ron/Pass/AddedKan/ExhaustiveDraw/AbortiveDraw（`RoundStateMachine.cs:1136-1175`）。

### 4.2 Domain 计分（42 役） — 仅独立组件，未接线

`Rules/Yaku.cs`（42 役）+ `Rules/YakuEvaluator.cs`/`WinningScoreEvaluator.cs` 已黄金差分；`grep -rln "YakuEvaluator|WinningScoreEvaluator" dotnet/src` 在 Rules 目录外**零命中**——生产权威链无调用者。结算数值来源仍是调用方（§4.1）。

### 4.3 TableRuntime 权威执行 — 组件合同完整，权威动作面受限（P3.1 边界）

- 串行权威队列、有界邮箱、租约围栏、回执先行、checkpoint/恢复合同齐备（§3.8）。
- 决策视图候选面：`MapProtocolAction`（`TableActor.cs:5781-5832`）把 8 种 LegalActionKind 映射为 `ProtocolActionType` 供候选展示。
- 执行面仅 `Pass`/`Discard`（`MapAction :4819-4834` → `UNSUPPORTED_ACTION :1436/:1623`）；吃/碰/杠/立直/和了止/出千命令在权威链返回不支持。**候选能看、执行未通**是 P3.1 交付的显式边界，非新缺陷，但构成门条件 5 未满足的事实。

### 4.4 持久化端口 — 接口先行（P3.4 接口 + InMemory 同形实现）

- 接口：`ITableStore`/`IReceiptStore`/`ITableLease`/`ISettlementOutbox`（`TableDurableContracts.cs:1427-1526`），含单原子边界 `CommitAsync`（租约围栏+状态 CAS+scope 复查+事件+命令日志+回执+终局 outbox+checkpoint 同生共死，`:1469-1475` 注释）。
- 实现：**仅** `InMemoryTableStore`（`InMemoryTableStore.cs:147-152`，同时实现四接口），按 PG 同形事务约束。
- 真实 PG：全 `dotnet/src` 中 `Npgsql` 仅 `Mahjong.LobbyService`（PostgresLobbyStore/PostgresRoomProjectionReader）。**表事件 PG 存储不存在**（V5-3A/P5.2 前置，缺陷 **D-6**）。
- Lobby PG 线：应用级重启回执重放测试 `PostgresLobbyStoreTests.cs:619-671`；跨进程写→真重启→读探针 `Mahjong.LobbyService.PostgresProbe/Program.cs`（write/read 两相，postmaster 启动时间必须变化、读写必须异进程，`:198-206`）由本地 `dotnet/scripts/pg-lobby-verify/verify-all.ps1` 编排——**不在 CI wrapper 内**（§5.4）。

### 4.5 AI — 接口先行 + 未接线

- 本座位观察（已接线、隐私合规）：actor 座位级 `AiObservation`（§3.7）。
- 策略/决策组件：`AiDecisionService`（`IAiAuthorityPort` + ReasonAsync + 过期提交合同）等 13 文件；`grep` 全 src **零生产消费者**——无 strategy→actor 组合根。
- `BasicAiContext` 无生产构造点（`BasicAiStrategy.cs:61,502` 抛 `BasicAiContextException`，即真实 actor 观察喂给 Basic 策略将抛异常）；Ai 模块 `using Mahjong.TableRuntime` 零命中（分层干净）。
- 在途 ENG 后续（AiCheatView/AiSeatSnapshot/AiDecisionContext 纯上下文）在固定头**未合入**（§10）。

### 4.6 Client / Wss — 组件完整（本地 TLS 链已验）

- Client 132 + Wss 80 双 run 全绿；有界缓冲有序同步、`ProtocolViewMapper` 单向投递。
- TLS 正向链：`WssEndpointTests.cs:96` `PositiveLocalWssTrustChainCompletesTlsHandshakeAndWireRoundTrip` 为**无条件 `[Fact]`**（每测试独立根 CA + socket factory，机器存储零改动，`:110-113` 注释明示）；负控 4 件——自签默认拒绝 `:76`、异主机名 `:150`、不受信根 `:174`、过期叶 `:199`，全部断言 `TlsHandshakeFailed+Permanent`。
- `LocalHost` 空壳（0 .cs）→ 单机全链为 G2 门内容。

### 4.7 LobbyService / PG — 组件完整（P5.3 范围内）

89 用例双 run 全绿；生产 PG 存储/迁移/回执/票据/恢复证据齐（§5.4）；匹配/排队/会话策略测试在 `ArchitectureAndPolicyTests.cs`。

### 4.8 Unity / IL2CPP / Android / macOS / 真机 / 跨物理网络 — 平台资源阻塞

无任何当前实测证据；维持 T4 缺口清单历史口径；G2/G3 门控。

## 5. CI 远端产物亲自核验记录

### 5.1 run 身份（自 run-info.txt 亲读）

| | ci-push | ci-pr |
|---|---|---|
| URL | https://github.com/liuchangready/majiang-game/actions/runs/40125990495 | https://github.com/liuchangready/majiang-game/actions/runs/40125992805 |
| GITHUB_RUN_ID / attempt | 40125990495 / 1 | 40125992805 / 1 |
| 实测 checkout | `8ee22ebf…8bd`（GITHUB_SHA） | `e1e0e88eae5518b0d4e6f4bb9532432ed11a6fc5`（PR19 merge，对 origin/v5 `457274c`） |
| ref | refs/heads/feat/v5-1-integration | refs/pull/19/merge |
| runner | Windows | Windows |
| 验证脚本 SHA256 | `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`（== 本地 `tools/verify-v5.ps1`） | 同左 |

产物本地基（caller 下载，本代理只读）：`D:/majiang-wt/codex-progress-20261025/artifacts/progress-20261025/{ci-push,ci-pr}/`。

### 5.2 每项目 meta 与框架日志

两 bundle 各含 7 个测试项目 run 目录（`runs/Mahjong.{Ai,Client,Client.Wss,Contracts,Domain,LobbyService,TableRuntime}.Tests`），14 份 per-project meta JSON 全部 exit 0、头一致、并携带框架日志 SHA256（逐份读取核验）。汇总 `summary.log` 双份均 7 行项目计数 + `OVERALL ... PASS` 9 行。

关键 SHA256（本轮重算）：

| 文件 | SHA256 |
|---|---|
| push summary.log | `b8bdaf8a32b37d22ebc9502f908d28c63af685c7f83094a8a3e3973a08d1fd12` |
| push run-info.txt | `bf7c583459f26ae1befc1fda148457ecb55640ee8bda160276b5475d0e2d22ea` |
| push wrapper-summary.json | `04f498dbcf49bf388f6f286c3734139961f317ba379aacd54492d3de973246a5` |
| push postgres-evidence.jsonl | `d2afb1a976144e780c804a336752d0de2aefdcd5441fd9f5771154ad3ec3bc77` |
| PR summary.log | `017f61c24b382116aa667b46819e0c3a563ad38d07332d2c8c4335d31ecaa277` |
| PR run-info.txt | `9910ae738ca5ed0329740853f4a775cff7321edeee32f393d38482b077d88c1f` |
| PR wrapper-summary.json | `779846c009c4e5cb9171ec16bc2a24e18e8afd24e4dfb1e1e7a34a15badd3915` |
| PR postgres-evidence.jsonl | `e31cb92f8781518b120e3f6a9a06603a7b358991be756ee286d02c25525a4c33` |
| ci-push-40125990495.log（job log） | `419df7c2f2ef197e41a08d046d4c9d12d7e035987ee5f4d19a58d0addd7a8936` |
| ci-pr-40125992805.log（job log） | `89a771ce96996998442d631e386c33d13ae32dda14a4d1662e0450f50c305da4` |

### 5.3 统一用例 vs wrapper 额外探针（区分口径）

- 统一 1,356 用例 = 7 测试项目 `dotnet test`；wrapper 十阶段（precheck/start-postgres/ownership-marker/environment/migrate/verify/archive-cluster-evidence/cleanup/env-restore/write-evidence）是**额外 PG 包装层**，不增减用例数；两 run wrapper-summary 均 10 阶段 PASS、verifier exit 0。
- env-restore 证据（生产 wrapper 中可见 V5-CI-ENV-RESTORE-FOLLOWUP 修复生效）：两 run 各 4 变量 `restored-absent`、mechanism=`provider-set-item-null`、`rootDeleted: true`。

### 5.4 PG 证据逐条（亲自读 evidence jsonl + root 日志）

两 run `postgres-evidence.jsonl` 均含：server identity（PG 16.10）、`terminal.projection`、`receipt.terminalReplay`、`recovery.expiredReservation`、`migration.repeated`（migrationRuns:3）、`concurrency.crossRoomJoin ×3`、`crossConnectionRowLock`、`receipt.replay`、`transaction.rollback`、`ticketConsumption`、`roomFill`。

**真实 PG 两进程重启写读：本轮两个 CI run 中不存在。** 依据：`root-logs/postgres.log` 仅 `ready-count=1`（单次 postmaster 启动），无 `restart.json`；evidence 无 `restart.write|restart.read` 事件。CI 内的"重启"证据是**应用级**回执重放（`PostgresLobbyStoreTests.cs:619-671`，同一 PG 库上第二个 LobbyFixture 验证 `Replayed=true`）。真正的两进程重启探针（`PostgresProbe` write→pg restart→read）仅存在于本地编排 `dotnet/scripts/pg-lobby-verify/verify-all.ps1`，历史证据为 P5.3 本地验证记录——**当前 CI 无法出示 server-restart 原始日志**（缺陷 D-7，口径如实）。

### 5.5 正向 TLS 链归因

归因方式如实声明：MTP/xUnit v3 框架日志为 UTF-16LE（`iconv` 转码后仅见 per-assembly total:80/0/0 汇总，无 per-test 明细），故正向用例归因依据为——(a) 该用例在源码中为无条件 `[Fact]`（`WssEndpointTests.cs:96`，无 skip/条件特性）；(b) Wss 80/0/0 全绿 meta；(c) 4 个负控同为无条件 Fact 且断言精确。三者合并可归因"正向链在 CI 实际执行并通过"。per-test 明细缺失作为流程缺口记录（D-9）。

### 5.6 历史 PR 39970580589 Client131/132 — UNRESOLVED（维持）

失败时框架日志在该历史 bundle 中缺失，无法补齐根因；当前双 run 全绿**不构成**原因已查明，不盲重试、不标记 flaky。保持 UNRESOLVED 原状并继续挂账。

## 6. 本轮独立探针（新执行记录）

| 探针 | 命令/方法 | 结果 |
|---|---|---|
| P-0 黄金目录 validator（本树固定头） | `tools/verify-v5-golden-catalog.ps1 -RepoRoot .` | PASS，exit 0，catalog SHA `16F7FD7F…891B`，73 需求 full17/partial20/none33/v5only3（§3.2） |
| P-1 依赖方向 | 全 8 src + 9 tests csproj `ProjectReference` 全量提取 + `using Mahjong.Domain`（Contracts/Wire）grep + `Mahjong.TableRuntime`（Ai）grep | 图干净（§3.3）；Contracts/Wire↛Domain rc=1；Ai↛TableRuntime rc=1 |
| P-2 Domain 确定性 | 严格 regex grep（§3.4 清单） | 零命中 rc=1；两处边界（PersistedUtcTime 消费型、ShuffleConfig double）判定非隐式权威 |
| P-3 Ai 本座位 | `AiObservation.cs` 契约+守卫源读（`:73-83` per-seat projection 契约；`:318-321` context 属于观察座位校验；`:349` 副露属主校验；`:421` 候选属主校验；`AiGuard.CheckSeat`） | 观察模型层本座位强制成立；生产组合根缺失（§4.5） |
| P-4 按流隐私 | 可见性/流测试锚点提取（§3.7） | 6+3 投影 Fact + 观战命令前置拒绝，锚点齐备 |
| P-5 兄弟任务符号 | `AccessCapabilities` 全仓 0 命中；`AiCheatView/AiSeatSnapshot/AiDecisionContext` 仅 `AiCandidateModels.cs:21` 注释提及 V4 类名 | 两在途任务在固定头均未合入（§10） |

未重跑项（诚实声明）：本地 `verify-all.ps1` 全套（含 PG 重启探针）——无代码变化不盲重跑重型自测；77/91 串行 PS 自测；Unity/设备任何链路。

## 7. 新缺陷/缺口清单（一轮全报，无跨轮隐藏）

严重级：MAJOR=阻断对应 G1 门条件或后续阶段前置；INFO=证据/流程挂账。以下均为**缺口/接线缺陷**，非已接受组件的内部错误。

| ID | 级别 | 缺陷 | 精确位置 | 实际 vs 期望 | 修复要求与归属 | 验收断言 |
|---|---|---|---|---|---|---|
| D-1 | MAJOR | 核心模块依赖方向静态测试缺失（仅 Lobby 有） | 缺失于 `dotnet/tests`（现仅 `Mahjong.LobbyService.Tests/ArchitectureAndPolicyTests.cs:13`） | G1 要求"依赖方向静态测试通过"；实际无测试守护 Domain=∅/Contracts=∅/Wire→Contracts/Ai→Domain/Client→{Contracts,Wire} | 新增静态测试工程断言精确白名单引用边（§9 S-1）；归属 G1 收尾 | 枚举 8 src 程序集，对每条非白名单 ProjectReference/AssemblyReference 失败；图与 §3.3 白名单一致 |
| D-2 | MAJOR | Domain 确定性守护静态测试缺失 | 缺失于 `dotnet/tests` | 现状干净仅点值探针；无回归防护 | 新增 source-scan 测试（§9 S-2），豁免表显式列 `PersistedUtcTime` 消费与 ShuffleConfig double 并给理由；归属 G1 收尾 | 对 `src/Mahjong.Domain/**/*.cs` 扫描禁用符号零命中（除显式豁免）；注入违例样本时失败 |
| D-3 | MAJOR | 权威链命令/事件面与冻结口径差集未闭合 | `RoundTypes.cs:31-45`（9/13 动作）；`RoundTransition.cs:56-74`（16 vs 19 事件）；差集明细 §4.1 | 期望 P3.2/P3.3 后权威链覆盖 13 动作、19 事件并有逐类冻结映射表 | P3.2 补 Chi/Pon/Kan/Riichi/AgariYame 动作与对应事件；产出 V4↔V5 事件一对一映射表（含 ResponseClaimed 泛化、MatchEnded 归属、窗口事件归类） | 映射表冻结后，目录事件覆盖 none→partial/full 迁移可验证；actor 对 13 动作不再返回 UNSUPPORTED_ACTION（除显式不支持项） |
| D-4 | MAJOR | 计分层零生产调用者（settlement 只验证不计算） | `Rules/YakuEvaluator.cs`/`WinningScoreEvaluator.cs`（src 内无调用者）；`RoundStateMachine.cs:21,742-822` | 期望权威链内计算结算；实际调用方供给 | P3.3 把评估器接入结算路径，保留守恒验证为第二道闸 | 权威链产生的 RoundSettlement 与黄金 scoring-v1（10,238 用例）对齐 |
| D-5 | MAJOR | AI 组合根缺失 | `AiDecisionService.cs`（零消费者）；`BasicAiStrategy.cs:61,502`；`Mahjong.LocalHost/`（空壳） | 期望策略可被权威宿主消费；实际无装配 | P4.1 前置 AI 组合根任务（依赖在途 T-A 上下文与 P3.2 候选面）；LocalHost 组合根按 G2 门 | 组合根测试：actor 观察→策略→意图回投全链在本座位合规下走通 |
| D-6 | MAJOR | 表事件持久端口仅 InMemory | `TableDurableContracts.cs:1427-1526`（接口）；`InMemoryTableStore.cs:147`（唯一实现）；src Npgsql 仅 Lobby | P5.2 需真实表事件 PG 存储；实际不存在 | V5-3A 交付 PG `ITableStore` 族实现（同形事务约束）；P3.4 内存合同为规格源 | 崩溃重启后 checkpoint 重放 136/账本/哈希校验过；同键同载荷回执幂等、异载荷冲突 |
| D-7 | INFO | CI wrapper 不含 PG server-restart 探针 | `tools/ci/verify-v5-postgres.ps1`（无 probe/restart 调用）；探针在 `dotnet/scripts/pg-lobby-verify/verify-all.ps1` | G3 远端验收前需在 CI 可出示两进程重启写读原始日志 | G2/G3 间决定是否纳入 wrapper（须评估一次性集群生命周期与安全） | 纳入后 CI evidence 出现 `restart.write/read` 事件且 postmaster 启动时间变化 |
| D-8 | INFO | 历史 PR 39970580589 Client131/132 根因未明 | 历史 bundle 框架日志缺失 | — | 保持 UNRESOLVED 挂账；如需重查须先补框架日志留存机制 | 不适用（不盲重试、不称 flaky） |
| D-9 | INFO | MTP 框架日志无 per-test 明细（UTF-16） | 各 run framework log（仅 total 汇总） | 期望可按测试名归因 | 可选改进：wrapper 增 per-test 明文清单（需 caller 决定，非门槛项） | 清单含每测试名与结果 |

## 8. 残余责任任务与依赖 DAG

```
D-1 依赖方向静态测试 ─────────────┐
D-2 确定性守护静态测试 ───────────┤
                                  ▼
                          G1 收尾判定（caller）
                                  │
P3.2 命令/事件面补齐(含冻结映射表) │  P3.4-ext PG TableStore ──→ P5.2（G3）
        │                         │          （独立可先行）
        ▼                         │
P3.3 计分接线(YakuEvaluator→结算) │
        │                         │
T-A(在途) Ai纯上下文 ──┐          │
P3.2 候选面 ───────────┼→ AI 组合根(D-5) ──→ P4.1 LocalHost（G2 入口）
                       │                          │
T-B(在途) AccessCapabilities ──（独立）            ▼
                                          P4.2..P4.5 / G2
G3（P5.x 远端）→ G4（P6.x）→ G5（P7.x）
```

- **G1 关闭 = D-1 + D-2 落地 + 门条件 5 经 P3.2/P3.3 满足（或 caller 明确改口径并留痕）+ 门条件 9 缺口分列维持**。
- P3.4-ext 与 D-1/D-2 无相互依赖，可并行；AI 组合根被 T-A 与 P3.2 双阻塞；P4.1 另依赖 P5.4a Client 接口基线（`V5-0-1-IMPLEMENTATION-TASKS.md:112-117`）。
- V4 保持唯一生产基线，直至 G1 关闭且后续门槛按阶梯推进。

## 9. 可独立下发的具体文件范围建议

本轮 scope 仅本报告；以下为**建议下发**（均新增文件，不改现有测试/验证器/冻结 fixtures）：

| ID | 建议范围 | 文件 | 要点 |
|---|---|---|---|
| S-1 | 新建架构静态测试工程 | `dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj` + `DependencyDirectionTests.cs` | 引用全部 8 个 src 工程；反射断言每程序集 `GetReferencedAssemblies()` ⊆ 白名单（§3.3 图）；覆盖 Mahjong 号段以外的程序集引用必须失败 |
| S-2 | Domain 确定性 source-scan 测试 | `dotnet/tests/Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs` | 从测试程序集定位 `src/Mahjong.Domain` 递归扫描禁用符号（§3.4 regex）；显式豁免表（PersistedUtcTime 消费路径、ShuffleConfig 数值调参）各带理由注释；内嵌一个自测试违例样本证明扫描器自身有效 |
| S-3（可选） | wrapper per-test 明文清单 | `tools/ci/verify-v5-postgres.ps1`（MTP 输出增强） | 解决 D-9 归因限制；是否做由 caller 决定，非 G1 门槛修复 |

下发时 expectedFiles 与兄弟工作树不相交；两个在途 DS 任务（§10）不得重复下发。

## 10. 在途兄弟任务（in-flight，未接受）

| 任务 | 固定头处事实（本轮探针 P-5） | 状态 |
|---|---|---|
| Ai `AiCheatView`/`AiSeatSnapshot`/`AiDecisionContext` 纯不可变上下文 + 测试 | 全仓仅 `dotnet/src/Mahjong.Ai/AiCandidateModels.cs:21` 注释引用 V4 类名；无类型/测试落地 | **in-flight，未合入**；不读取其未提交产物；不重复下发；AI 组合根（D-5）依赖其接受 |
| Contracts `AccessCapabilities` 纯契约 + 测试 | 全仓 0 命中 | **in-flight，未合入**；同上 |

## 11. REVIEW_INCOMPLETE 范围声明

以下事项本轮无法完全查明，明示范围：

1. **Unity/IL2CPP/macOS/Android/真机/跨物理网络构建日志**：无当前实测证据（环境缺口未重探查）。本报告对门条件 9 记 BLOCKED 并引用历史缺口清单，未出任何当前 PASS。
2. **真实 PG 两进程重启写读的当前证据**：本轮未重跑本地 `verify-all.ps1`（避免无代码变化重跑重型自测）；CI 中不存在该探针（§5.4）。引用 P5.3 本地历史记录为历史事实。
3. **历史 PR 39970580589 Client131/132 根因**：失败框架日志缺失，本轮产物无法补齐，保持 UNRESOLVED。
4. **per-test 名级归因**：受 MTP UTF-16 框架日志限制，采用"无条件 Fact + 汇 total 全绿 + 源码断言"组合归因（§5.5），非日志逐条归因。

除上述四项，本报告其余结论均有本轮亲自执行的命令与可复算 SHA256 支撑。

## 12. 证据与命令附录

关键命令（全部在本审查树执行）：

```
git rev-parse HEAD / git status --porcelain / git branch --show-current
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot .   → PASS, EXIT=0
grep -rnE "<确定性禁用符号组>" dotnet/src/Mahjong.Domain --include="*.cs"                            → 零命中
for f in dotnet/{src,tests}/*/*.csproj; do …提取 ProjectReference…; done
grep -n "interface" dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs 等（§3–§4 全部锚点）
sha256sum <CI 产物清单>（§5.2 表）
iconv -f UTF-16LE -t UTF-8 <framework log>（§5.5）
```

冻结引用：依赖矩阵 `437bd53`；黄金目录 `205506f`（caller 第 13 轮独立验收集成于 PR19 `8ee22eb`，见 `doc/v5-重构/` 各验收文档与 `V5-0-GOLDEN-MANIFEST.md`）。

—— 审查代理（实现侧），2026-10-25。本报告不批准 G1。
