# V5-0 迁移追踪矩阵（P0.1）

日期：2026-10-23
分支/基线：`v5`，任务基线提交 `48125ff`（迁移方案审计时记录的 `f8f4543` 与之相比，`src/main`、`src/test` 无改动）
状态：**P0.1 文档产物；不是 V5 实现记录，也不是 V5 验收报告。未自行通过 G0。**

本文件只做两件事：把 V4 已验证行为 / 仍需回放提取的行为 / V5 新能力分开，并给每条行为指定 V5 目标模块、P1–P3 任务、黄金样本和证据状态。V5 当前只有空工程骨架，没有任何 C# 规则实现；本文件不把 V4 的测试通过写成 V5 通过。

---

## 0. 结论边界

1. 本次只新增/修改 `doc/v5-重构/V5-0-TRACEABILITY.md` 与 `doc/v5-重构/V5-0-GOLDEN-MANIFEST.md`。未修改 V4 生产代码、测试、Unity 文件或其他文档，未提交、未推送。
2. 本次没有导出任何语言中立黄金样本到 `tests/v5-golden/`；清单中所有 V4 样本均为 `pending` 或 `pending-v4-replay`，因此**已导出数为 0，对齐率暂无分母，也不存在“V5 已对齐”结论**。
3. 本次没有运行全量 Gradle 测试、Android 构建、Unity/IL2CPP、T4 跨物理网络或真机测试。V4 的 1,275 项历史运行数、1,826 个静态测试方法、本次会话 470 个定向用例（首轮 366 + 复核补跑 67 + Riichi 37）均只是 V4 证据，不是 V5 验收结果；审查侧报告的 1,852 是独立 V4 回归，也不是 V5 验收。
4. 41/42 役种、23/19 事件、`GamePhase` 9/10 等口径差异没有被静默抹平；需要 P0.2/G0 裁决的项目在 §1.4 与 §5 单列，并指定责任任务。
5. G0 门槛仍由 Codex 复核 P0.1–P0.3 后记录 `V5-0-REVIEW.md`；本文件不能替代 `V5-0-RULE-CONTRACT.md`、`V5-0-VISIBILITY-MATRIX.md`、`V5-0-PROTOCOL-DECISIONS.md` 与 `V5-0-MEASUREMENTS.md`。

---

## 1. 审计口径与实测结果

### 1.1 静态文件数与测试数（只标审计口径）

统计范围：七个模块 `src/main/**/*.kt`、`src/test/**/*.kt`；行数含空行；测试方法按 `@Test`、`@ParameterizedTest`、`@RepeatedTest`、`@TestFactory` 注解计数，不展开参数化运行数。

| 模块 | 主文件 | 主代码行 | 测试文件 | 测试代码行 | 静态测试方法 |
|---|---:|---:|---:|---:|---:|
| engine | 114 | 26,256 | 74 | 16,186 | 578 |
| protocol | 39 | 13,040 | 21 | 6,366 | 199 |
| server | 27 | 13,857 | 43 | 17,444 | 343 |
| client | 20 | 14,061 | 21 | 9,807 | 235 |
| presentation | 63 | 18,846 | 76 | 14,569 | 471 |
| lwjgl3 | 2 | 162 | 0 | 0 | 0 |
| android | 1 | 22 | 0 | 0 | 0 |
| **合计** | **266** | **86,244** | **235** | **64,372** | **1,826** |

口径说明：

- 1,826 是**静态测试方法数**；1,275 是 V4 TCP 验收文档记录的**历史标准模块运行数**。两者不是同一口径，不能互相替代，也不能写进 V5 验收。
- 首轮定向运行了 27 个 engine 测试类、3 个 protocol 类、4 个 server 类、2 个 client 类，共 **366 个用例通过、0 失败、0 跳过**（命令与结果见 §1.3）；复核补跑另加 104 例（client 59 + SessionSnapshot 8 + Riichi 37，见下）。这些都是 V4 行为抽样验证，不是全量回归，不是 V5 测试。

### 1.2 迁移方案 266 个生产文件的归属核对

核对方法：从迁移方案 §1.3 的表格行提取 266 个 `.kt` 路径，与当前 `src/main` 实际路径逐项比较；用模块名消解 `server/`、`client/`、`presentation/` 的包名前缀差异。

结果：

- 方案列出的 **266** 个路径与当前实际 **266** 个文件**逐项完全一致**：缺失 0、多余 0、重复 0。
- 266 行均有 V5 去向；含“待定/TBD/未定”的行 0。**48 个文件是跨目标归属**（例如 Domain + Contracts、TableRuntime + Contracts、Unity 纯模型 + Client），其余为单一主目标。
- 去向按“文件-目标提及”统计（一个文件多目标会多次计数，故合计大于 266）：Domain 71、Unity 68、Contracts 58、TableRuntime 36、Client 20、Mahjong.Ai 14、TableService 13、Delete 12、Contracts.Wire 6、Infrastructure 6、LobbyService 4、LocalHost 2。该统计只用于核对覆盖面，不是文件数。
- `f8f4543..48125ff` 的差异只有 `.editorconfig`、`.gitattributes`、`.gitignore`、`doc/v5-重构/*`、`dotnet/`、`unity/`、`tools/unity/` 等脚手架/文档；`*/src/main/*` 与 `*/src/test/*` 的 diff 为空。故迁移方案的 266 文件审计在源码层面**没有漂移**；工作树中他人未提交的 `UNITY-ENVIRONMENT.md`、Unity `Resources` 删除、`BurstAotSettings_StandaloneWindows.json` 保持原样，未纳入本任务。

按模块/包的静态分布（用于复核 1.3 表的分组数）：

| 模块 | 包分布（文件数） | 合计 |
|---|---|---:|
| engine | access 11、ai 14（含 cheat 6）、application 10（含 cheat 2）、authority 6（含 cheat 5）、cheat 8、economy 4、event 3、match 3、model 6、projection 2、round 6、rules 10、session 6、settlement 1、stacking 10、state 9、timing 5 | 114 |
| protocol | server/api 16、transport/api 15、transport/common 1、transport/tcp 7 | 39 |
| server | 根 1、control 2、host 2、idempotency 2、inprocess 2、mapping 2、room 3、tcp 13 | 27 |
| client | api 11、mapping 1、session 5、transport/tcp 3 | 20 |
| presentation | 根 1、common 7、common/render 20、game 23、menu 12 | 63 |
| lwjgl3 | desktop 2 | 2 |
| android | 根 1 | 1 |

**漂移/差异说明**：文件“归属”本身未漂移，但方案中的少量**口径描述**与当前源码不一致，见 §1.4；这些不是 266 文件清单的增删，而是文档/代码口径差异，必须由 P0.2/G0 裁决，不能由实现者顺手改规则。

### 1.3 实际运行命令与结果

在 `D:\majiang` 执行（PowerShell；Gradle 使用仓库 wrapper，JVM 由 wrapper 找到 Android Studio JBR）：

```powershell
.\gradlew.bat :engine:test --tests "com.majiang.model.RedFiveTest" --tests "com.majiang.settlement.SettlementServiceContractTest" --tests "com.majiang.match.MatchProgressionContractTest" --tests "com.majiang.model.TileMovementContractTest" --tests "com.majiang.v3.ShuffleAtomicDealAcceptanceTest" --console=plain --no-daemon
.\gradlew.bat :engine:test --tests "com.majiang.rules.WinningScoreContractTest" --tests "com.majiang.rules.FuBreakdownContractTest" --tests "com.majiang.rules.DoraBreakdownContractTest" --tests "com.majiang.rules.ScoreCodesAndPaymentPlanTest" --tests "com.majiang.cheat.mark.TileMarkAuthorityContractTest" --tests "com.majiang.cheat.mark.TileMarkStateContractTest" --tests "com.majiang.cheat.bloom.SleeveBloomContractTest" --tests "com.majiang.cheat.bloom.SleeveBloomOffTurnTest" --tests "com.majiang.cheat.beam.BeamSwapContractTest" --tests "com.majiang.cheat.beam.BeamSwapWallAndRulesTest" --tests "com.majiang.seethrough.SeeThroughEngineAcceptanceTest" --console=plain --no-daemon
.\gradlew.bat :protocol:test --tests "com.majiang.transport.tcp.TcpWireTest" --tests "com.majiang.transport.tcp.TcpFrameCodecTest" --tests "com.majiang.transport.tcp.TcpMessagesTest" :server:test --tests "com.majiang.server.idempotency.RequestScopeReceiptStoreTest" --tests "com.majiang.server.idempotency.ReceiptStoreRegressionTest" --tests "com.majiang.server.control.ConnectionControlTest" --tests "com.majiang.server.host.ServerTableHostTest" :client:test --tests "com.majiang.client.session.PlayerStreamEngineTest" --tests "com.majiang.client.session.SpectatorStreamEngineTest" --console=plain --no-daemon
.\gradlew.bat :engine:test --tests "com.majiang.v3.TimeControlAcceptanceTest" --tests "com.majiang.state.SeatControllerAcceptanceTest" --tests "com.majiang.projection.PublicProjectionTest" --tests "com.majiang.state.ResponseResolverContractTest" --tests "com.majiang.rules.RiichiKanValidatorContractTest" --tests "com.majiang.cheat.bloom.SleeveBloomKanDoraTest" --tests "com.majiang.v3.ShuffleAlgorithmAcceptanceTest" --console=plain --no-daemon
.\gradlew.bat :engine:test --tests "com.majiang.v3.CheatLedgerIntegrationAcceptanceTest" --tests "com.majiang.state.UnifiedActionAcceptanceTest" --tests "com.majiang.architecture.RoundBoundaryAcceptanceTest" --tests "com.majiang.architecture.SessionBoundaryAcceptanceTest" --console=plain --no-daemon
```

结果：

| 批次 | 覆盖 | 用例数 | 结果 |
|---|---|---:|---|
| 1 | engine 规则/移动/结算/原子发牌 | 39 | 通过 |
| 2 | engine 算分/三绝技/看破 | 142 | 通过 |
| 3 | protocol 帧/消息 + server 回执/控制/宿主 + client 流 | 32 + 35 + 14 = 81 | 通过 |
| 4 | engine 计时/控制/投影/响应/立直杠/岭上宝牌/码牌 | 78 | 通过 |
| 5 | engine 账本/CHANKAN/局与整场边界 | 26 | 通过 |
| **合计** | 27 engine + 3 protocol + 4 server + 2 client 个测试类 | **366** | **0 失败、0 跳过** |

首轮上述 5 次 Gradle 调用均输出 `BUILD SUCCESSFUL`、退出码 0；测试结果为 `0 failures / 0 skipped`。未运行的命令/环境项不适用此退出码，单列为未覆盖。

审查侧另行报告：`.\gradlew.bat test --console=plain --no-daemon` 为 `BUILD SUCCESSFUL`，测试 XML 合计 **1,852 / 0 failures / 0 skipped**。这是 **V4 回归通过**，不是本次会话执行的结果，也不消除本文件指出的证据链缺口；不能写成 V5 通过。

复核补跑（第 2 轮，为 G-056a..d/G-057a..d 的证据闭环）：

```powershell
.\gradlew.bat :client:test --tests "com.majiang.client.transport.tcp.TcpClientBindingRegressionTest" --tests "com.majiang.client.session.PlayerStreamEngineTest" --tests "com.majiang.client.session.SpectatorStreamEngineTest" --console=plain --no-daemon
.\gradlew.bat :engine:test --tests "com.majiang.session.SessionSnapshotContractTest" --console=plain --no-daemon
.\gradlew.bat :engine:test --tests "com.majiang.rules.RiichiRulesVerificationTest" --console=plain --no-daemon
```

结果为 `BUILD SUCCESSFUL`、退出码 0：client 3 类 **59** 例（Player 6 + Spectator 8 + Binding 45）、SessionSnapshot **8** 例、RiichiRulesVerification **37** 例，合计 **104 / 0 failures / 0 skipped**。加上首轮 366 例，本次会话定向执行共 **470** 例通过。仍未运行全量套件；审查侧的 1,852 是独立 V4 回归，不是本会话结果。

未运行/未验证：全量 `gradlew test`、Android APK、Unity Editor/IL2CPP、T4 跨物理网络/弱网、Android 真机/模拟器、PostgreSQL/租约/冷恢复（V5 尚未实现）。这些都是未覆盖项，不得写成通过。

### 1.4 文档/代码差异与裁决责任

| 编号 | 文档/方案称 | 源码实际 | 证据 | 影响/责任 |
|---|---|---|---|---|
| D-01 | 41 种役种（`AGENTS.md:49`） | `Yaku` 枚举 **42 项**：1 翻 15、2 翻 11、3 翻 3、6 翻 1、役满 12 | `engine/.../rules/YakuEvaluator.kt:15-68` | 无法从源码证明哪一项“不计/不可产出”。冻结役种清单前由 **P0.2 + G0** 裁决；不得删枚举凑 41。 |
| D-02 | 23 种游戏事件（`ProtocolViewMapper` 旧注释，迁移方案 §1.5） | `GameEvent` **19 个子类** | `engine/.../event/GameEvent.kt:13-110`；`Select-String` 统计 19 个 `data class` | V5 事件 schema 以 19 类 + 新增字段为基线；**P2.1** 冻结，旧注释不再作为数量依据。 |
| D-03 | 迁移方案 §2.1.8 称 `GamePhase` **9 态** | 实际 **10 态**（含 `CHANKAN_WINDOW`） | `engine/.../state/GamePhase.kt:15-61` | 方案口径少计一个状态；V5 按 10 态建 reducer/合法转移，**P1.3**。 |
| D-04 | `AGENTS.md:96` 称默认 `GRACE_THEN_AI`（10s） | `DisconnectPolicy` 默认 `IMMEDIATE_AI`，`graceMillis=10000` 仅在显式选择 `GRACE_THEN_AI` 时生效 | `server/.../control/DisconnectPolicy.kt:24-27`；`ConnectionControl.kt:69` | 产品默认策略未定；V5 **P3.3 + P0.2** 必须以配置项记录，不得把 10s 宽限写成默认行为。 |
| D-05 | 迁移方案审计 HEAD `f8f4543` | 任务基线 `48125ff` | `git diff --name-only f8f4543..48125ff -- engine/src/main engine/src/test protocol/src/main protocol/src/test server/src/main server/src/test client/src/main client/src/test presentation/src/main presentation/src/test lwjgl3/src android/src` 输出为空（exit 0） | 源码/测试无漂移；文件数 266 结论仍成立。 |
| D-06 | 方案将 `RequestScopeReceiptStore` 作为回执能力 | V4 没有 `RECEIPT_QUERY` 消息/API；重连后的未知结果靠内存重放/查询回执 | `protocol/.../ApiErrors.kt`、迁移方案 §D7；全仓 grep `RECEIPT_QUERY`、`receiptQuery`、`queryReceipt` 均 0 命中 | V5 **P3.4/P2.1** 新增持久 outbox 与 `RECEIPT_QUERY`；V4 的“安全重放”不能写成“未知结果可对账”。 |
| D-07 | 方案称可记录随机源/seed 回放 | `ShufflePreparationHandler` 用 `SecureRandom.nextLong()` 生成局 seed，且无 getter/事件导出 | `engine/.../authority/cheat/ShufflePreparationHandler.kt:54,64-69` | V4 准备阶段无法仅凭现有公开输出复现牌山；黄金样本必须记录实际牌山/抽牌，**P1.5 + P0.1 提取**。 |
| D-08 | V4 TCP 验收“自动化完成” | 同文档明确 T4 跨物理网络/Android 真机/后台恢复**仍未验证** | `doc/v4-重构/CODEX-TCP-MIGRATION-ACCEPTANCE.md:5,16` | 不得用 V4 自动化/桌面快照替代 V5 Android/弱网验收；**P0.3/P5.4/P6.2**。 |

### 1.5 本次实际阅读/核对的源码与测试（不代表逐行审阅全部 266 文件）

- 迁移约束：`AGENTS.md`、`doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md`、`doc/v4-重构/CODEX-TCP-MIGRATION-ACCEPTANCE.md`。
- 构建/依赖：`settings.gradle`、`gradle.properties`、`engine/build.gradle`、`protocol/build.gradle`、`server/build.gradle`、`client/build.gradle`、`presentation/build.gradle`。
- engine 核心：`Tile`、`TileLocation`、`TilePool`、`TileMovement`、`GamePhase`、`GameState`（字段/重置入口）、`StateInvariants`、`RoundStateMachine`、`RoundTransitionPlanner`、`MatchSession`、`MatchProgression`、`MatchConfig`、`SettlementService`、`ScoreCalculator`、`YakuEvaluator`、`FuCalculator`、`WinningScoreEvaluator`、`HandDecomposer`、`ResponseResolver`、`FuritenRules`、`RiichiKanValidator`、`LegalActionProvider`、`DecisionClock`、`TimeControlConfig`、`TimeoutPolicy`、`SeatController`、`ControllerRegistry`、`ConnectionControl` / `DisconnectPolicy`（server 包，按源码位置核对）、`TileMarkSubmissionHandler`、`TileMarkCost`、`TileMarkModels`、`SleeveBloomSubmissionHandler`、`SleeveBloomModels`、`BeamSwapSubmissionHandler`、`BeamSwapModels`、`CheatLedger`、`CheatLedgerCharge`、`CheatEconomyConfig`、`ShuffleSlots`、`ShuffleWallGenerator`、`ShuffleConfig`、`ShufflePreparationHandler`、`PublicProjectionFactory`、`PlayerView`、`SessionUpdate`、`TableAuthority`（关键入口/回执/快照/超时/AI 段）、`ApplicationCoordinator`（发牌/账本关键段）。
- protocol/server/client：`TcpWire`、`ApiErrors` / `ApiProtocol`、`TcpCloseReasons`、`TcpServerConfig`、`RequestScopeReceiptStore`、`IdempotencyTypes`、`ServerTableHost`（tick/AI 段）、`PlayerStreamEngine`、`TcpClientBinding`（缓冲/未确认副作用段）。
- 测试：`RedFiveTest`、`TileMovementContractTest`、`ShuffleAtomicDealAcceptanceTest`、`ShuffleAlgorithmAcceptanceTest`、`WinningScoreContractTest`、`FuBreakdownContractTest`、`DoraBreakdownContractTest`、`ScoreCodesAndPaymentPlanTest`、`SettlementServiceContractTest`、`MatchProgressionContractTest`、`TimeControlAcceptanceTest`、`SeatControllerAcceptanceTest`、`ResponseResolverContractTest`、`RiichiKanValidatorContractTest`、`PublicProjectionTest`、`SessionSnapshotContractTest`、`TileMarkAuthorityContractTest`、`TileMarkStateContractTest`、`SleeveBloomContractTest`、`SleeveBloomOffTurnTest`、`SleeveBloomKanDoraTest`、`BeamSwapContractTest`、`BeamSwapWallAndRulesTest`、`SeeThroughEngineAcceptanceTest`、`CheatLedgerIntegrationAcceptanceTest`、`UnifiedActionAcceptanceTest`、`RoundBoundaryAcceptanceTest`、`SessionBoundaryAcceptanceTest`、`RequestScopeReceiptStoreTest`、`ReceiptStoreRegressionTest`、`ConnectionControlTest`、`ServerTableHostTest`、`TcpWireTest`、`TcpFrameCodecTest`、`TcpMessagesTest`、`PlayerStreamEngineTest`、`SpectatorStreamEngineTest`、`TcpClientBindingRegressionTest`。
- 测试夹具：`engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt`（固定 `turnNumber=12`、显式墙/手牌/王牌，自动补齐剩余 136 张；用于三绝技和移动样本）。

---

## 2. 追踪矩阵

图例：

- **证据状态**：`A/已跑` = 源码已核对且本次定向测试通过；`A/静态` = 源码/测试已核对但本次未运行；`B/待导出` = 行为存在、语言中立样本尚需从 V4 夹具/回放提取；`C/V5新增` = V4 无对应行为，不能计入 V4 对齐率；`D/待裁决` = 文档/代码口径或产品选择需要 G0 前裁决。
- **样本类型**：`直接` = 现有测试已断言输入/输出，可直接导出；`回放` = 需要录制随机/时间/ID/完整状态；`V5` = 只属 V5 新能力。
- 所有 `pending` 样本的提取状态见 `V5-0-GOLDEN-MANIFEST.md`；本矩阵不把“有测试”写成“已导出”。

### 2.1 规则、牌实体、算分

| ID | V4 有效行为与证据 | V5 归属 | P1–P3 任务 | 样本/类型 | 状态 |
|---|---|---|---|---|---|
| TR-A01 | `TileId=typeIndex*4+copy`，34 类顺序为万 0–8 / 索 9–17 / 筒 18–26 / 风 27–30 / 三元 31–33；数牌 5 的 copy=3 为赤五，恰好 3 张。证据：`model/Tile.kt:49-52,62-68,112-120`；`RedFiveTest` 4 例；`RiichiRulesVerificationTest` 的 34 维/136 张用例。 | Mahjong.Domain | P1.1 | G-001a/G-001b / 直接；G-001c / 探针 | A/已跑（赤五）；A/静态（34 维）；B/待导出（tileId 映射探针） |
| TR-A02 | 物理位置 `Zone` 7 种；WALL/DEAD_WALL/UNASSIGNED 的 seat=-1；HAND/DISCARD/MELD/SLEEVE 必须 0..3。证据：`model/TileLocation.kt:14-22`；`model/TilePool.kt:56-100`；`TileMovementContractTest` 的合法移动用例；非法构造由 G-002 探针覆盖。 | Mahjong.Domain | P1.1 | G-002 / 探针 | B/待导出（非法构造探针）；合法移动见 G-004..G-008 |
| TR-A03 | 136 张唯一身份、标准 136 张无遗漏、重复物理身份即失败；活牌山 `[wallPointer, wallEndExclusive)` 与跳过洞均计入；有效王牌 = `[rinshanPointer, deadWall.size)` + replacements，`deadWall.size==14` 且有效数 14。证据：`state/StateInvariants.kt:239-328`；`TileMovementContractTest`；`ShuffleAtomicDealAcceptanceTest`；`ShuffleAlgorithmAcceptanceTest`（标准集/136/3 赤五）；缺失/不一致由 G-003b 探针，重复物理墙由 G-010/G-007 覆盖；`TilePool.tileMap` 以 `Tile` 为键，duplicate-key 分支无法直接构造（静态限制）。 | Mahjong.Domain | P1.1 | G-003a / 直接；G-003b / 探针；G-009 / 回放 | A/已跑（标准集/重复墙）；B/待导出（缺失/不一致探针 + 完整状态摘要） |
| TR-A04 | 发牌/摸牌/打牌/吃/碰/明杠/暗杠/加杠/岭上/牌山交换均经 `TileMovement`；每次先校验源/目标/池位置，失败零副作用；暗杠保留无关摸牌、岭上补牌不重复消耗宝牌指示。证据：`model/TileMovement.kt`；`TileMovementContractTest` 10 例。 | Mahjong.Domain | P1.1 | G-004..G-008 / 直接 | A/已跑 |
| TR-A05 | 发牌前强校验 136 张；费用预检全桌通过后才原子扣费、重置容器、发牌、王牌翻开、审计；失败不扣费不发牌。证据：`application/ApplicationCoordinator.kt:2883-2996`；`ShuffleAtomicDealAcceptanceTest`。 | Mahjong.TableRuntime + Domain | P1.1/P3.1/P1.4 | G-010 / 直接；G-011 / 回放 | A/已跑（状态）；B/待导出（事件序列） |
| TR-A06 | 标准形 4 面子+雀头、七对、国士分支由 `HandDecomposer` 枚举；`YakuEvaluator.evaluate` 取最优分解；和牌判定与算分分离。证据：`rules/HandDecomposer.kt`、`rules/YakuEvaluator.kt:112-218`；`RiichiRulesVerificationTest`。 | Mahjong.Domain | P1.2 | G-013/G-018 / 直接 | A/静态（本次未跑该类） |
| TR-A07 | 役种 42 枚举（15/11/3/1/12 分档），stable `code` 与展示名分离；文档写 41。证据：`rules/YakuEvaluator.kt:15-68`；`ScoreCodesAndPaymentPlanTest`（code 唯一/旧名不变，未直接断言 42 计数）。 | Mahjong.Domain + Contracts | P1.2；P0.2 裁决 | G-019 / 回放 | D-01；A/已跑（code/旧名）；B/待导出（42 计数探针） |
| TR-A08 | 符：门清/副露、雀头、等待、自摸、杠、平和/七对固定符；行项目加总后向上取整。证据：`rules/FuCalculator.kt`；`FuBreakdownContractTest` 6 例。 | Mahjong.Domain | P1.2 | G-014 / 直接 | A/已跑 |
| TR-A09 | 基本点 `fu*2^(2+han)`；封顶 3 翻 70 符/4 翻 40 符/5 翻=满贯 2000，6–7 跳满 3000，8–10 倍满 4000，11–12 三倍满 6000，13+ 累计役满 8000；无切上满贯；支付按庄/闲、自摸/荣和乘数并向上取整百。证据：`rules/ScoreCalculator.kt:129-205`；`RiichiRulesVerificationTest`；`ScoreCodesAndPaymentPlanTest`。 | Mahjong.Domain | P1.2 | G-017/G-018 / 直接；G-019 / 回放 | A/已跑（capCode）；A/静态（支付/封顶表）；B/待导出（42 计数探针） |
| TR-A10 | 表宝牌/里宝/赤五叠加；里宝仅在立直和牌计入；同张红五可同时命中指示牌与赤宝（2 翻）；副露红五计入；宝牌本身不构成起和役。证据：`rules/WinningScoreEvaluator.kt`；`WinningScoreContractTest`、`DoraBreakdownContractTest`。 | Mahjong.Domain | P1.2 | G-015/G-016 / 直接 | A/已跑 |
| TR-A11 | 荣和：放铳者付基础+300×本场，和牌者收全部供托；自摸：每家基础+100×本场，和牌者收全部供托；和牌后供托清零；守恒断言 `sum(newScores)+sticks*1000`；本局立直棒跨局/整场只转移一次。证据：`settlement/SettlementService.kt:55-134,229-245`；`SettlementServiceContractTest`、`SessionBoundaryAcceptanceTest`。 | Mahjong.Domain | P1.2/P1.3 | G-020/G-021/G-024 / 直接 | A/已跑 |
| TR-A12 | 荒牌流局 1/2/3 听：3000/1500/1000 罚符（0/4 听无收支），供托保留；途中流局无罚符、供托保留；16 种听牌组合均守恒。证据：`SettlementService.kt:142-200`；`SettlementServiceContractTest`。 | Mahjong.Domain | P1.2/P1.3 | G-022/G-023 / 直接 | A/已跑 |
| TR-A13 | 立直/振听/杠合法性：舍牌振听、同巡/立直振听；立直后暗杠需保持待牌/结构，引用 WRC 2022 §8.9.1；杠后岭上/宝牌顺序。证据：`rules/FuritenRules.kt`、`rules/RiichiRuleEngine.kt`、`rules/RiichiKanValidator.kt`；`RiichiKanValidatorContractTest`、`FuritenFlowContractTest`、`SleeveBloomKanDoraTest`。 | Mahjong.Domain | P1.2/P1.3 | G-029/G-030 / 直接 | A/已跑（杠/岭上）；A/静态（振听全量） |

### 2.2 单局、整场、计时、控制代次

| ID | V4 有效行为与证据 | V5 归属 | P1–P3 任务 | 样本/类型 | 状态 |
|---|---|---|---|---|---|
| TR-B01 | `GamePhase` 实际 10 态：STACKING→DEALING→DRAW→CHEAT_WINDOW→DISCARD→RESPONSE_WINDOW→CHANKAN_WINDOW→KAN_DRAW→ROUND_END→MATCH_END；`allowedTransitions()` 白名单。证据：`state/GamePhase.kt:15-61`。 | Mahjong.Domain | P1.3 | G-028a/G-029 / 直接 | A/已跑（CHANKAN）；A/静态（全转移） |
| TR-B02 | `RoundStateMachine`：终态/`result != null` 拒绝再 prepare；每次 install 成功 version+1；prepare 不改状态；install 校验 roundId/sourceVersion；`nextRound()` 创建全新 `RoundStateMachine` 与新 `roundId`，旧局对象保持不变。证据：`round/RoundStateMachine.kt:47-201`；`RoundBoundaryAcceptanceTest:62-79`（单局终态）；`SessionBoundaryAcceptanceTest` 的 `dealer repeat creates a new machine and leaves the old round immutable`；`CheatLedgerIntegrationAcceptanceTest:21-42` 的 `next.roundId != old.roundId`。 | Mahjong.Domain | P1.3 | G-028a/G-028b / 直接 | A/已跑（单局终态 + 新 roundId 用例）；UUID 实际值待录制 |
| TR-B03 | `SessionLifecycle` 9 态；`RoundRecord` 保存每局结果/分数/供托/千术余额；`SessionResult` 保存终局排名/原因/供托归属；换庄/连庄均有显式记录。证据：`match/MatchSession.kt:14-55,127-140`。 | Mahjong.Domain | P1.3 | G-025/G-027 / 直接 | A/静态（整场记录）；A/已跑（边界） |
| TR-B04 | 连庄：庄家和/荒牌庄家听→同局连庄、本场+1；闲家和/庄家未听→换庄、本场清零（荒牌仍+1）；东风/半庄/西入延长；尾庄 `AGARI_YAME` 需达返点且独占第一；同分按 `tieBreakSeats`；终局剩余供托归第一并守恒。证据：`match/MatchProgression.kt:166-300`；`MatchProgressionContractTest` 13 例。 | Mahjong.Domain | P1.3 | G-025/G-026/G-027 / 直接 | A/已跑 |
| TR-B05 | 加杠先进入 `CHANKAN_WINDOW` 等抢杠；未宣告荣和前不改副露/岭上；抢杠荣和后不产生 `KanDeclared`/`RinshanDraw`；无人抢杠才结算加杠+岭上。证据：`round/RoundTransitionPlanner.kt:254-259,293-350`；`state/UnifiedActionAcceptanceTest` 的 `valid AI added kan waits for remote chankan...`；`SleeveBloomKanDoraTest`。 | Mahjong.Domain + TableRuntime | P1.3 | G-029 / 直接 | A/已跑 |
| TR-B06 | 每决策 3s 免费 + 每局 20s 储备（`ROUND`）或整场共享（`SESSION`）；deadline=open+base+reserveAtOpen；恰好 deadline 拒绝；同决策重复 open 不刷新；暂停/恢复不重置免费时间；旧局计时不能透支新局。证据：`timing/DecisionClock.kt:105-153,182-208`、`timing/TimeControlConfig.kt:14-36`；`TimeControlAcceptanceTest` 10 例。 | Mahjong.Domain + TableRuntime | P1.3/P3.3 | G-046 / 直接 | A/已跑 |
| TR-B07 | 超时策略优先级：Pass > 摸切（当前摸牌且合法）> 最小合法弃牌；绝不自动自摸/荣和/吃/碰/杠/立直/千术；无合法默认动作返回 null。`ServerTableHost.tick` 先宽限接管，再 `checkTimeout`，且超时不受 `presentationBusy` 推迟，之后才门控动画/AI。证据：`timing/TimeoutPolicy.kt:12-66`；`ServerTableHost.kt:1630-1705`；`TimeControlAcceptanceTest` 的 response/own-turn timeout 用例。 | Mahjong.Domain + TableRuntime | P1.3/P3.3 | G-047 / 直接 | A/已跑 |
| TR-B08 | `SeatController` 四类（LocalHuman/RemoteHuman/Computer/Takeover）；`ControllerRegistry.setController/initialize` 每次自增 `controlEpoch`；真人重连不自动收回，`reclaimControl()` 才自增；旧 epoch/decision/round 的提交拒绝。证据：`state/SeatController.kt`、`application/ControllerRegistry.kt:30-44`；`SeatControllerAcceptanceTest`；`ServerTableHostCoverageTest` 的 takeover/reclaim 用例。 | Mahjong.Domain + TableRuntime | P1.3/P3.3 | G-048 / 直接 | A/已跑（SeatController）；A/静态（宿主 epoch 边界） |
| TR-B09 | `ConnectionControl`：`IMMEDIATE_AI` 断线即 TAKEOVER+epoch+1；`GRACE_THEN_AI` 记录 deadline、期内保持 HUMAN、到期才接管+epoch+1；`TIMEOUT_ONLY` 永不接管；重复断线不延长宽限；reconnect(reclaim=false) 保留 TAKEOVER，显式 `reclaimControl` 才 HUMAN+epoch+1；过期 `expectedEpoch` 无副作用。证据：`server/.../control/ConnectionControl.kt:145-231,282-339`；`ConnectionControlTest` 9 例。注意默认策略口径见 D-04。 | Mahjong.TableRuntime | P3.3 | G-048 / 直接 + D/待裁决 | A/已跑；默认策略待 P0.2/P3.3 裁决 |
| TR-B10 | AI 只接收本座位 `PlayerView`+合法动作；推理在权威队列外；结果提交前校验 `roundId/decisionId/controlEpoch/deadline/候选`，任一不匹配丢弃；AI 浮点启发式不进权威哈希。证据：`authority/TableAuthority.kt:708-751`；`server/.../host/ServerTableHost.kt:1857-1900`；`SeatControllerAcceptanceTest`、`UnifiedActionAcceptanceTest`。 | Mahjong.Ai + TableRuntime | P1.6/P3.3 | G-049 / 直接+回放 | A/已跑（视图/拒绝）；B/待导出（recorded intent） |

### 2.3 三绝技、码牌、经济

| ID | V4 有效行为与证据 | V5 归属 | P1–P3 任务 | 样本/类型 | 状态 |
|---|---|---|---|---|---|
| TR-C01 | 落汗：只能标本人手牌 4 张、不能立直、最多 3 组/12 张、不能重复；费用默认 10 且经 `TileMarkCost.of` 唯一入口；组跨局保存、`createdRoundOrdinal+3` 过期；看破清标是整场变更；只 owner 可见。证据：`authority/cheat/TileMarkSubmissionHandler.kt:97-112`、`cheat/mark/TileMarkCost.kt:10-14`、`cheat/mark/TileMarkModels.kt:95-125`；`TileMarkAuthorityContractTest`、`TileMarkStateContractTest`。 | Mahjong.Domain + TableRuntime | P1.4 | G-032/G-033/G-034 / 直接 | A/已跑 |
| TR-C02 | 袖里藏花：即时路径在本人摸牌后 `CHEAT_WINDOW`，`DEAL_EXTRA` 禁止；第二候选=牌山下一个可用槽/王牌替补；他家回合路径在本人弃牌后签 `windowId`，`offTurnChoiceMillis=10s`；每座最多一个 reservation/off-turn offer；下一次普通摸牌/局终失效；看破投影只给 owner。证据：`authority/cheat/SleeveBloomSubmissionHandler.kt:248-283,286-350,352-407`、`cheat/bloom/SleeveBloomModels.kt`；`SleeveBloomContractTest`、`SleeveBloomOffTurnTest`。 | Mahjong.Domain + TableRuntime | P1.4 | G-035/G-036/G-037 / 直接 | A/已跑 |
| TR-C03 | 偷梁换柱：摸牌后选手牌+槽位（WALL/DEAD_WALL/REPLACEMENT）等额互换；offer 冻结候选/eligibleHandTileIds/drawnTileId；二选一 SWAP/KEEP_ORIGINAL；二选一校验整份 offer 基线未漂移；拒绝零副作用。证据：`authority/cheat/BeamSwapSubmissionHandler.kt:165-211,237-302`、`cheat/beam/BeamSwapModels.kt`；`BeamSwapContractTest`、`BeamSwapWallAndRulesTest`。 | Mahjong.Domain + TableRuntime | P1.4 | G-038/G-039 / 直接 | A/已跑 |
| TR-C04 | 三绝技后 136 张位置仍逐张守恒；费用事件与 offer 同一次发布可见；失败不扣费/不建 offer/不移动牌；三绝技 offer 私有投影不出现在他席/公开流。证据：三绝技测试中的 `physical()`/`locations()`/`snapshot(seat)` 断言；`TileMarkAuthorityContractTest:64-89,345-373`、`SleeveBloomContractTest:76-158`、`BeamSwapContractTest:78-150,457-477`。 | Mahjong.Domain + TableRuntime | P1.4/P3.2 | G-040a/G-040b/G-040c / 直接 | A/已跑（三类分项）；B/待导出（字段级 JSON） |
| TR-C05 | `CheatLedger`：同 `entryId` 同载荷返回原 entry；同 id 不同载荷冲突；负余额、座位越界、Int 溢出全部拒绝且零副作用；余额/流水防御复制；独立于麻将点数。证据：`economy/CheatLedger.kt:70-124`；`CheatLedgerIntegrationAcceptanceTest`。 | Mahjong.Domain | P1.4 | G-041 / 直接 | A/已跑 |
| TR-C06 | 码牌槽位：0..52 起手（庄 14/子 13）、53..121 后续摸牌、122..135 王牌；王牌绝不做目标/供体；起手后 0..52 锁定；四人有效时逐张等于同 seed 基础洗牌。证据：`stacking/ShuffleSlots.kt:14-79`、`stacking/ShuffleWallGenerator.kt:84-87`；`ShuffleAlgorithmAcceptanceTest`。 | Mahjong.Domain | P1.5 | G-042/G-043 / 直接 | A/已跑 |
| TR-C07 | `ShuffleWallGenerator.generate(seed, dealerSeat, plans, config)` 同 seed/同计划可复现；输入 plans 顺序无关；无目标/关闭/strength=0 与基础洗牌逐张相等；136 唯一、3 赤五、王牌段不变；多人目标冲突按档位/预算/衰减。证据：`stacking/ShuffleWallGenerator.kt:40-207`；`ShuffleAlgorithmAcceptanceTest`（seed 0..127）。 | Mahjong.Domain | P1.5 | G-043 / 直接 | A/已跑（生成器）；B/待导出（整局记录） |
| TR-C08 | 看破：费用 10；与统一目标组合上限 40；看破席位冻结目标、抵消对手统一目标；多人看破对称；看破不改变无人被抵消时的牌山；发牌时保留本人标记、清对手标记。证据：`stacking/ShuffleSelection.kt`、`authority/cheat/ShufflePreparationHandler.kt`；`SeeThroughEngineAcceptanceTest` 12 例。 | Mahjong.Domain + TableRuntime | P1.5/P1.4 | G-044 / 直接 | A/已跑 |
| TR-C09 | 准备阶段 seed：`SecureRandom.nextLong()` 生成，未导出到事件/视图；V4 不能仅凭公开输出复现整局牌山；V4 生成器本身 seed 可复现。证据：`ShufflePreparationHandler.kt:54,64-69`；对照 `ShuffleWallGenerator.kt:62-65`。 | Mahjong.Domain + Infrastructure 端口 | P1.5/P0.1 提取 | G-045 / 回放 | B/待导出；D-07 |

### 2.4 协议、回执、宿主串行、客户端同步

| ID | V4 有效行为与证据 | V5 归属 | P1–P3 任务 | 样本/类型 | 状态 |
|---|---|---|---|---|---|
| TR-D01 | MJ-TCP/1：12 字节大端头（"MJTC"+wireVersion 1+type+flags 0+uint32 payloadLength），payload 严格 UTF-8 JSON，1 B..1,048,576 B；帧类型 1..10；业务版本 8；`PROTOCOL_VERSION=8`，HELLO/WELCOME 硬相等。证据：`protocol/.../TcpWire.kt:9-45,83-92`、`server/api/ApiErrors.kt:92-100`；`TcpWireTest`、`TcpFrameCodecTest`、`TcpMessagesTest`。 | Mahjong.Contracts.Wire（重写）；旧 TCP 删除 | P2.1 | G-050a/G-050b/G-050c / 分解 | A/已跑（V4 三类分项）；C/V5 新 wire 未实现 |
| TR-D02 | 心跳 3s PING、10s 无完整合法入站判死；PING/PONG 16 hex nonce 原样回显；出站 256 帧/4 MiB、入站 64 帧/4 MiB、全局 256 MiB、连接 512；关闭原因永久/临时分类。证据：`server/.../TcpServerConfig.kt:21-64`、`protocol/.../TcpCloseReasons.kt:17-94`；`TcpMessagesTest`（nonce/消息族）、`TcpWireTest`（关闭原因）。 | Mahjong.TableService（WebSocket）+ Contracts | P2.1/P5.1 | G-050a/G-050c / 分解 | A/已跑（V4 分项）；C/V5 WSS 未实现 |
| TR-D03 | 错误码与业务拒绝分离：`ApiErrors` 稳定码（INVALID_PROTOCOL_VERSION、MALFORMED_REQUEST、CONFLICT、SERVER_BUSY、REQUEST_SCOPE_EXPIRED 等）；业务拒绝走 `ActionReceipt(status=REJECTED, reason=...)`；关闭原因有永久/临时集合。证据：`protocol/.../ApiErrors.kt:13-85`、`access/ReceiptStatus.kt:3-14`、`TcpCloseReasons.kt`；`TcpWireTest`。 | Mahjong.Contracts + TableRuntime | P2.1/P3.4 | G-051 / 回放 | A/已跑（V4）；C/V5 需重冻结 |
| TR-D04 | `ActionReceipt` 状态：REJECTED 不改状态、RECORDED 已记录待裁决、APPLIED 已提交；`TableAuthority.submitChoice` 查重顺序：会话/暂停/座位→回执→配额→准备→epoch→decision/round→option→deadline→执行；同载荷重放原回执，不同载荷 `REQUEST_ID_CONFLICT`，超配额 `RECEIPT_QUOTA_EXCEEDED`，每座 8,192。证据：`access/ReceiptStatus.kt`、`authority/TableAuthority.kt:181-299,541-646`；`Mr09ReceiptQuotaTest`、`TileMarkAuthorityContractTest`。 | Mahjong.Domain（CommandResult）+ TableRuntime | P3.4/P3.1 | G-052 / 直接 | A/静态（回执顺序/配额）；A/已跑（三绝技重放） |
| TR-D05 | `RequestScopeReceiptStore`：scope 默认 10 分钟；同键同载荷返回原回执/同 future；同键不同 API/载荷 CONFLICT；过期 scope 有旧回执则重放、无则 `REQUEST_SCOPE_EXPIRED` 且绝不重做；每成员在途 ≤64、副作用 ≤512、大厅副作用 1024、查询缓存 256；完成回执锁外回调。证据：`server/.../idempotency/RequestScopeReceiptStore.kt:16-38,210-295,300-379,448-490`；`RequestScopeReceiptStoreTest`、`ReceiptStoreRegressionTest`。 | Mahjong.TableRuntime + Infrastructure（持久化） | P3.4/P5.2 | G-053/G-054 / 直接 | A/已跑（内存版）；C/V5 持久化未实现 |
| TR-D06 | V4 **没有** `RECEIPT_QUERY` 消息/API；重连后的未知结果依赖客户端 `unconfirmedSideEffects` 内存重放（最多 128）与 scope 回执查询；应用被杀/进程重启后无持久对账。证据：`client/.../TcpClientBinding.kt:355-370,417,2205-2280`；迁移方案 §D7。 | Mahjong.TableRuntime + Client + Contracts | P3.4/P2.1/P5.4a | G-055 / V5 | C/V5 新能力；不计入 V4 对齐率 |
| TR-D07 | `ServerTableHost` 每桌 `serialExecutor` 串行；tick 顺序：准备→宽限接管→`checkTimeout`（优先，不受 `presentationBusy`）→庄家/自动下一局→动画门控→AI→立直摸切；AI 结果一次提交成功一个；慢回调不阻塞权威队列。证据：`server/.../host/ServerTableHost.kt:1598-1705,1857-1900`；`ServerTableHostTest` 8 例。 | Mahjong.TableRuntime | P3.1/P3.3 | G-047 / 直接 | A/已跑（串行/慢观察者）；A/静态（完整 tick 顺序） |
| TR-D08 | 客户端玩家流重排/已应用游标；早期入站 64 条/4 MiB；缺口全量 resume、全量基线清空重排；`bindingEpoch` 旧代次丢弃；观战独立游标/独立流；未确认副作用最多 128 条按原 scope 重放。证据：`client/.../PlayerStreamEngine.kt:60-84`、`TcpClientBinding.kt:319-370,417,2940-2995`；`PlayerStreamEngineTest`、`SpectatorStreamEngineTest`、`TcpClientBindingRegressionTest`（R09 role switch/early 64/4 MiB、R04 128/replay）。**`PlayerStreamEngine` 的 64 重排溢出、256 已应用游标淘汰、旧 epoch 派发丢弃没有现成测试，见 G-056d。** | Mahjong.Client | P3.2/P5.4a | G-056a/G-056b/G-056c/G-056d / 分解 | A/已跑（Player/Spectator/Binding 分项）；B/待导出（G-056d 探针） |

### 2.5 投影、可见性、观战

| ID | V4 有效行为与证据 | V5 归属 | P1–P3 任务 | 样本/类型 | 状态 |
|---|---|---|---|---|---|
| TR-E01 | `PlayerView` 只给本人暗牌/摸牌，对手仅 `handTileCounts`；牌河/副露/宝牌/分数公开；不暴露牌山顺序/未公开里宝/他席合法动作；集合只读深拷贝。证据：`session/PlayerView.kt:12-70`；`SessionSnapshotContractTest`（本人手牌/分数冻结、副露/动作列表解耦）；对手仅张数/牌山顺序由源码声明，需 G-057d 探针。 | Mahjong.TableRuntime + Contracts | P3.2 | G-057a/G-057d / 直接+探针 | A/已跑（本人视图/不可变）；B/待导出（对手张数探针） |
| TR-E02 | `SessionUpdate.filterVisibleEvents`：他家发牌/摸牌/岭上隐藏；里宝仅在和牌/结算/局终/终局授权；打牌/副露/立直/和牌/流局/表宝牌公开。证据：`session/SessionUpdate.kt:56-93`；`SessionSnapshotContractTest` 的私有发牌/摸牌/岭上、未公开里宝、公开弃牌用例；`PublicProjectionTest` 的授权里宝用例。 | Mahjong.TableRuntime | P3.2 | G-057b/G-057c / 直接 | A/已跑（分项） |
| TR-E03 | `PublicProjectionFactory` 白名单：允许流程/弃牌/副露/立直/和牌/流局/结算/局终/表宝牌；默认丢弃发牌/摸牌/岭上/千术流水/里宝（除非显式 `revealSettlementUra=true`）；观战不能伪造 seat。证据：`projection/PublicProjectionFactory.kt:96-153`；`PublicProjectionTest` 12 例。 | Mahjong.TableRuntime | P3.2 | G-057c / 直接 | A/已跑 |
| TR-E04 | 观战公开流独立身份/独立游标/独立缓冲；观战连接不能升级为玩家；玩家 API 拒绝观战身份。证据：`client/.../SpectatorStreamEngine.kt`、`server/.../TcpRequestRouterAndReceiptTest` 的 identity boundary 用例；`SpectatorStreamEngineTest`。 | Mahjong.Client + TableRuntime | P3.2/P5.4b | G-056b / 直接 | A/已跑（流引擎）；A/静态（路由身份） |
| TR-E05 | 三绝技私有视图只由 owner 查询；他人/公开流看不到 activeGroups/pendingOffer/候选；看破只影响有效目标，不泄露对手方案。证据：`TileMarkProjectionFactory`、`SleeveBloomProjectionFactory`、`BeamSwapProjectionFactory`；`TileMarkAuthorityContractTest:345-373`、`BeamSwapContractTest:457-477`。 | Mahjong.TableRuntime + Contracts | P3.2/P1.4 | G-040a/G-040b/G-040c / 直接 | A/已跑（三类分项）；B/待导出（字段级 JSON） |

### 2.6 V5 新能力与待裁决项（不计入 V4 对齐率）

| ID | 事项 | V5 归属 | 任务 | 样本/类型 | 状态 |
|---|---|---|---|---|---|
| TR-F01 | `ownerEpoch` + 表租约 + 每命令双 fence；V4 只有进程内 `@Synchronized`，无 DB 租约/ownerEpoch。 | Mahjong.TableService + Infrastructure | P3.4/P5.2 | G-058 / V5 | C/V5新增 |
| TR-F02 | 持久事件/回执/outbox/checkpoint/冷恢复；V4 回执全内存、scope 10 分钟清理、无 JDBC/Postgres。 | Infrastructure + TableRuntime | P3.4/P5.2 | G-058 / V5 | C/V5新增 |
| TR-F03 | Protobuf/Wire 冻结、字段号/reserved/oneof、AOT/IL2CPP；V4 是 Gson JSON + 手写 adapter。 | Contracts.Wire | P2.1/P2.2 | G-059 / V5 | C/V5新增 |
| TR-F04 | WSS/TLS/票据/账号/匹配；V4 无 TLS、无账户/匹配，仅自建 TCP 明文。 | TableService + LobbyService | P5.1/P5.3/P5.4b | G-059 / V5 | C/V5新增 |
| TR-F05 | 41/42 役种口径 | Domain + Docs | P0.2/G0 | G-019 / 回放 | D/待裁决（P0.2 负责；G0 记录） |
| TR-F06 | 是否精确复刻 V4 `Random(seed)`/浮点洗牌；现有生成器 seed 可复现，但准备阶段 seed 未导出、shuffle 算法跨运行时未冻结。 | Domain | P1.5 + P0.2 | G-045 / 回放 | D/待裁决（P1.5 负责；G0 记录） |
| TR-F07 | AI 是否要求同输入同结果；V4 浮点启发式只用于排序，AI intent 未作为独立事件导出。 | Ai + TableRuntime | P1.6/P0.2 | G-049 / 回放 | D/待裁决（P1.6 负责） |
| TR-F08 | 掉线默认策略：AGENTS 写 `GRACE_THEN_AI` 10s，代码默认 `IMMEDIATE_AI`；排位/私人房是否不同。 | TableRuntime + 产品配置 | P3.3/P0.2 | G-048 / 回放 | D/待裁决（P3.3 负责） |
| TR-F09 | 规则变体：uma/oka、切上满贯、流局满贯、双倍役满、三麻、拔北；V4 当前未实现，V5 不得顺手补。 | Domain + 产品配置 | P0.2 | 无 V4 样本 / V5 | C/V5新增（产品未定） |
| TR-F10 | 进程/线程/容量数字：V4 每桌约 7 线程、无界队列、1 MiB 帧上限；V5 目标是有界 mailbox/共享调度器/实测容量，不能把 V4 数字当 V5 结果。 | TableRuntime + TableService | P3.1/P5.2/P6.1 | G-058 / V5 | C/V5新增；P0.3 测量 |

---

## 3. 覆盖核对（按 P0.1 要求）

| P0.1 要求 | 覆盖行 | 样本 | 状态 |
|---|---|---|---|
| 规则、和牌/役/符/算分 | TR-A06..A13 | G-013..G-024 | 已映射；部分样本 pending |
| 136 实体/位置转移/赤五/34 类 | TR-A01..A05 | G-001a..G-012 | 已映射 |
| 局/整场/连庄/终局/流局 | TR-B01..B04 | G-025..G-028b | 已映射；G-028b 覆盖新 roundId |
| CHANKAN_WINDOW | TR-B05 | G-029 | 已映射；已跑测 |
| 三绝技成功/拒绝/回退 | TR-C01..C04 | G-032..G-040c | 已映射；三绝技拆为 G-040a/b/c |
| 码牌/看破 | TR-C06..C09 | G-042..G-045 | 已映射；准备 seed pending |
| 经济/账本 | TR-C05 | G-041 | 已映射 |
| 计时/超时裁决 | TR-B06..B07 | G-046..G-047 | 已映射 |
| 控制代次/托管/收回 | TR-B08..B09 | G-048 | 已映射；默认策略待裁决 |
| 回执/幂等 | TR-D04..D06 | G-052..G-055 | 已映射；V5 持久化/QUERY 为新增 |
| 协议/错误/帧 | TR-D01..D03 | G-050a..G-050c、G-051 | 已映射；V4 值需重冻结 |
| 投影/可见性/观战 | TR-E01..E05 | G-056a..G-056d、G-057a..G-057d | 已映射；G-056d/G-057d 待探针 |
| 多事件命令 | TR-A05（发牌） / TR-B05 与 TR-A13（杠+宝牌+岭上） | G-011/G-030 | 已映射；事件顺序待导出完整摘要 |
| 无事件拒绝 | TR-C05 / TR-D04 | G-031/G-052 | 已映射；已有断言 |
| AI 异步/stale | TR-B10 | G-049 | 已映射；intent 样本 pending |
| V5 新能力 | TR-F01..F10 | G-055/G-058/G-059 | 已单列，不计入对齐率 |

覆盖结论：**行为类别无遗漏；样本导出为 0，不能声称“黄金样本已建立”或“V5 对齐率 100%”。**

---

## 4. 266 文件归属的逐项可复核口径

1. 迁移方案 §1.3 的表格行 = 266 个唯一路径；实际 `src/main` = 266 个唯一文件；逐项比较差异 0。
2. 归属不是“按目录猜”：以方案每行第二列的目标文本为准；48 个多目标行保留双归属，未强行压成单模块。
3. 文件清单本身无漂移；`f8f4543..48125ff` 只有文档/脚手架差异，生产与测试源码 diff 为空。
4. 归属的**语义**仍需 P0.2/P2.1 细化：例如 `access/*` 的 Contracts/TableRuntime 拆分、`protocol/transport/api` 的 Wire/Views 拆分、`server/room` 的 Lobby/TableRuntime 拆分、`presentation` 的 Unity 重写。本文件只确认“有归属”，不声称目标项目已存在。
5. `dotnet/src` 当前无手写 `.cs` 规则实现（仅有 `obj/` 下生成的 `AssemblyInfo.cs`）；`unity` 仍无规则/UI `.cs`。因此 266 文件是**迁移来源清单**，不是已完成迁移的清单。

---

## 5. 未决项与 G0 前必须动作

| 未决项 | 证据 | 责任任务 | 进入 V5-1 前的要求 |
|---|---|---|---|
| 41/42 役种 | 42 枚举；文档 41；无法证明某项不可产出 | P0.2 + G0 | 冻结 `Yaku.code` 清单和“是否可产出”列；不得删枚举凑数 |
| 23/19 事件 | 实际 19 子类；旧注释 23 | P2.1 + G0 | 以 19 类为基线写 schema/可见性；新增事件另编号 |
| `GamePhase` 9/10 | 实际 10 态 | P0.2/P1.3 | 按 10 态冻结转移表；说明方案少计的是 `CHANKAN_WINDOW` |
| 掉线默认策略 | 代码默认 `IMMEDIATE_AI`，文档称 `GRACE_THEN_AI` 10s | P3.3 + P0.2 | 配置化并记录默认值；排位/私人房分别裁决 |
| 准备阶段 seed 不可导出 | `SecureRandom.nextLong()` 无 getter/事件 | P1.5 + P0.1 提取 | 记录实际牌山/抽牌序列；不得只记 seed 声称可回放 |
| 整局 UUID/随机 | `RoundStateMachine.roundId`、`MatchSession.sessionId`、`ResultId`、`Random.Default` 未注入 | P1.1/P1.3 | 回放样本记录实际 ID/随机结果；Domain 不自行生成 |
| V4 无 RECEIPT_QUERY/持久 outbox | 内存回执/128 条未确认副作用 | P3.4/P5.4a | 作为 V5 新能力单列，不能计 V4 对齐 |
| 样本导出 0 | `tests/v5-golden/` 不存在；清单 73 例/导出 0 | P0.1 后续 / P1 各任务 | 先导出 V4 样本 G-001a/b、G-003a、G-004..G-008、G-010、G-013..G-018、G-020..G-023、G-024、G-025..G-039、G-040a..c、G-041..G-044、G-046..G-048、G-050a..c、G-051..G-054、G-056a..c、G-057a..c；G-055/G-058/G-059 为 V5 新能力，G-001c/G-002/G-003b/G-009/G-011/G-012/G-019/G-045/G-049/G-056d/G-057d 为待探针/回放，均不计入；未导出保持 pending |
| T4/Android/Unity/IL2CPP 缺口 | TCP 验收文档明确未验证 | P0.3/P6.2 | 单列设备/网络/构建日志；不得用 V4 桌面测试替代 |

**G0 声明**：本文件只完成 P0.1 的追踪矩阵与清单设计，不构成 G0 通过。G0 仍需 P0.2（规则/事件/可见性/协议）、P0.3（实测基线/环境缺口）与 Codex 复核；未满足项不得以空 C# 类或“已有 V4 测试”替代。

---

## 6. 本次交付边界与工作树状态

- 新增：`doc/v5-重构/V5-0-TRACEABILITY.md`、`doc/v5-重构/V5-0-GOLDEN-MANIFEST.md`。
- 未改：V4 七模块 `src/main`/`src/test`、`AGENTS.md`、既有 V4/V5 文档、`dotnet/`、`unity/`、`tools/unity/`。
- 工作树原有他人未提交项（完整保留，未还原/覆盖）：`doc/v5-重构/UNITY-ENVIRONMENT.md`、`unity/Mahjong.Unity/Assets/Resources/PerformanceTestRunInfo.json` 及其 `.meta`、`PerformanceTestRunSettings.json` 及其 `.meta` 删除、`unity/Mahjong.Unity/ProjectSettings/BurstAotSettings_StandaloneWindows.json` 新增。
- 未提交、未推送；未生成 `tests/v5-golden/` 文件。

---

## 7. 2026-10-24 实现追踪核对（P0.1 现状复核，基线 437bd53；同日八轮独立复核返修加固）

**性质与边界**：本章为证据/追踪复核，不构成实现，不判定 G1，不修改任务书与架构文档。§1–§6 的历史日期、口径与结论全部保留，本章只在其上增加当前实现状态边界。核对基线为已审 PR19 实际头 `437bd53ca8771764d62ef75226b73efded2baabd`（独占工作树 `wt/v5-traceability-current`）；accepted 依据仅取自该基线内已复核验收文档、引入提交与实际 C# 源码，飞行中接收树与未推送候选一律不计入。返修沿革：初版（f724262）5 处放行漏洞 + 3 处数据错误；二轮（5b7e7d1）类型强转/当前索引取证/隐藏绿分支；三轮（21981cc）严格类型 schema + 冻结基线取证 + 自依赖拒绝；四轮（261eb85）注释/字符串剥离 + 声明紧匹配 + 2 行伪 v4Symbol 修正（ENG-031→`deepCopy`、SRV-027→`toByteArray`）；五轮（8975ece）词法状态扫描器替换统一宽松正则；六轮（3f5415f）按语言分洞（C# 非插值逐字串 `{` 惰性、Kotlin 仅未转义 `${` 开洞）+ 属性链容空白点取真实末段；七轮（adca621）结构化声明链解析器（可空 `?`、多参数/嵌套泛型 receiver 完整消费）；八轮（本轮）修复**函数类型箭头截断**缺陷：七轮的平衡尖括号计数把 `->` 箭头 token 的 `>` 当泛型闭括号，`val Map<(String) -> Int, List<Int>>.functionProperty: Int get() = 1`（复核方以本机缓存 Kotlin 2.4.10 K2JVMCompiler 真编译 exit0 的合法源码）的 receiver 在 `<(String) ->` 处提前截断，`Map` 被误判为声明（伪造证据）、真实末段 `functionProperty` 被误拒；属性分支此前也未核验完整合法声明边界。本轮修复：**`Skip-BalancedTypeArgs` 箭头感知平衡扫描**（`->` 的 `>` 惰性、括号与嵌套角括号深度计数，`suspend`/receiver 函数类型/`(List<Int>) -> List<String>`/`List<List<Int?>>` 均整体消费，未闭合整体放弃），属性 receiver 与 `fun` 类型参数表两个解析点共用同一扫描器（泛型约束界 `fun <F : (String) -> Int> higherFun` 同样不可截断）；**属性声明边界核验**：链后必须紧跟合法续元（`:` 类型 / `=` 初始化 / `by` 委托 / `get`/`set` 访问器），任何未来解析缺陷导致的提前截断都不可能再被判定为声明。本章为第八轮返修后版本：四轮 probe（6+6+7+7=26 例）全部符合预期，266 行正例零误伤，实现 JSON 连续四轮零改动。

### 7.1 新增产物

- `doc/v5-重构/implementation-v1.json`（schema `v5-implementation-trace-v1`）：266 个 V4 生产源码文件逐一唯一行。每行含：V4 符号（校验器强制为该行真实源文件在**冻结基线 blob** 内的真实**声明**——词法剥离后按声明语法匹配，属性/函数 receiver——含可空 `?`、多参数/嵌套泛型、**函数类型实参（`(A) -> B`）**及合法空白书写形式——、import、泛型参数、超类型、注释、字符串一律不构成声明）；计划归属 `planTarget`（逐字引用 `DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md` §1.3 逐文件表"V5 去向"列原文）；V5 目标模块 `v5Targets`（受控词表）；状态、责任任务与依赖（对照任务书真实任务 ID；依赖语义与门槛反循环见文件头 `dependenciesSemantics` 与 `gatePrerequisites`）；缺口、V5/测试/验收锚点、黄金样本引用。SHA256 `2c51d3fc2b536084dc8de3f90464b2953fabca1bf0db14a85861aaa76725cc3d`（五/六/七/八轮零改动）。行级修正史：四轮修正 2 处伪 v4Symbol——ENG-031 `EventSnapshot.kt` 的 `GameAction`（仅 import+扩展 receiver）→ `deepCopy`、SRV-027 `TcpExtensions.kt` 的 `TcpFrame`（同性质）→ `toByteArray`。
- `tools/traceability/validate-implementation-trace.ps1`：只读机器校验器（PowerShell 5.1 兼容，无安装、无全局改动）。第八轮强制项：**`Skip-BalancedTypeArgs` 箭头感知平衡类型实参扫描器**——`<` 增深、`>` 减深，但**前邻字符为 `-` 的 `>`（即 `->` 箭头 token 的一部分）惰性**，括号/嵌套角括号深度计数，未闭合返回未终结并整体放弃该候选；`Get-KotlinPropertyChain`（属性/函数 receiver 链）与 `Test-KotlinFunDeclaration`（`fun` 可选类型参数表）**共用该扫描器**，消除复制实现中相同的尖括号计数隐患。**属性声明边界核验**：链 `End` 后跳过空白必须命中 `:`（类型）/`=`（初始化）/词 `by`（委托）/词 `get`/`set`（访问器，词界校验）之一，否则该候选不构成声明——防御纵深，任何截断都不能再被判定为声明。沿用既有强制项：词法状态扫描器剥离（Kotlin 仅未转义 `${` 开模板洞、C# 仅插值前缀 `{`、非插值逐字串 `{` 普通内容、嵌套块注释深度计数、三引号/逐字串/插值洞全隔离）、结构化声明链解析（标识符段 + 平衡类型实参 + 可空后缀 + 点两侧合法空白，声明名取真实末段、非末段与类型实参内容不构成声明）、`fun` 名后紧跟 `(`、类型名紧跟关键字、typealias 名紧跟关键字、锚点与 v4Symbol 强制合法单标识符（专属标记）、严格 JSON 原始类型 schema、直接自依赖拒绝、冻结基线取证（行集合对照 `git ls-tree -r <baselineCommit>`，符号在 `git cat-file blob <baselineCommit>:<path>` 真实 blob 内核验）、验收提交存在性/祖先性/文档在提交与基线存在四重核验、任务书真实 ID 与门槛反循环、统计与分母自洽。SHA256 `5e91385f1c294f2d3e0a0b346bd652b9df034735544bb4699ba0e3fbd5ad8968`。
- `tools/tests/VerifyV5Traceability.Tests.ps1`：入 git 的可复现自测，**75 案例 = 15 正例（真实仓库 + 沙箱好例 s1 + 防误伤正例 s11 普通属性/s12 扩展属性名/s13 typealias/s14 扩展函数名/s21 逐字串后真实声明/s22 Kotlin 花括号串后真实声明/s24 空白点属性名/s28 可空属性名/s29 泛型属性名/s30 函数类型实参属性名/s31 嵌套可空泛型+空白点属性名/s33 函数类型实参 receiver 扩展函数名/s34 泛型约束界函数名）+ 60 负例（22 真实仓库负例 n01–n22 + 17 独立类型负例 t01–t17 + 21 隔离沙箱负例 s2–s10、s15–s20、s23、s25–s27、s32、s35）**。负例要求 exit=1 且命中预期 FAIL 标记。**无 skip 规则**：不存在任何跳过、默认通过或隐藏绿分支；真实仓库 n19 解析失败计为失败；**沙箱构造失败把 s1–s35 全部 35 例计为失败**。**沙箱源新增**：`Demo.kt` 追加四条合法声明 `val Map<(String) -> Int, List<Int>>.functionProperty: Int get() = 1`（函数类型实参 receiver，与本轮 review 沙箱同款）、`val List<List<Int?>> . nestedProperty: Int get() = 1`（嵌套可空泛型 + 空白点）、`fun Map<(String) -> Int, List<Int>>.functionFun(): Int = 1`（函数类型实参 receiver 扩展函数）、`fun <F : (String) -> Int> higherFun(f: F): Int = 1`（泛型约束界含箭头），并新增 6 例：s30（`functionProperty` 真实末段 → exit=0）、s31（`nestedProperty` → exit=0）、s32（嵌套泛型 receiver `List` → `v4-symbol-not-declared-at-baseline`；同例覆盖 `Map` 在函数类型实参形态下仍非声明）、s33（`functionFun` → exit=0）、s34（`higherFun` → exit=0）、s35（箭头界类型参数 `F` → 同标记拒绝）。SHA256 `38c9a958f726a4cb0f3fa77ce774555f8632c44ef2046c5877fe49b5fb960be1`。
- **沙箱确定性负例**：自测在系统 TEMP 下构造隔离 git 仓库（提交 B = 基线：生产源 `Demo.kt` 含普通属性、扩展属性（紧凑/空白点/可空 `String?`/泛型 `Map<String, Int>`/**函数类型实参 `Map<(String) -> Int, List<Int>>`**/嵌套可空泛型 `List<List<Int?>>` 五种 receiver 书写）、typealias、扩展函数（普通与函数类型实参 receiver）、泛型约束界函数、**Kotlin 嵌套块注释内伪类**、**真实三引号原始串内伪类**、**普通 `{` 花括号串后真实类**、**`${` 模板洞内嵌串伪类**；`DemoV5.cs` 含**注释内伪词**、**字符串内伪类**、**插值串洞内嵌字符串伪类**、**非插值逐字串内伪类（含 `{`）**、**逐字串后真实类**、泛型类；测试源 `DemoTest.kt`；真实任务书副本。提交 C = B 的子提交，追加基线后文件与符号）。负例：基线后文件行/符号/锚点（s2–s6）、注释-only/字符串-only/泛型参数/函数 receiver（s7–s10）、属性 receiver 紧凑/空白点/可空/泛型/嵌套泛型（s15/s23/s26/s27/s32）、嵌套注释（s16）、插值洞内嵌串（s17）、非单标识符（s18）、三引号原始串（s19）、非插值逐字串花括号（s20）、Kotlin `${` 模板洞内嵌串（s25）、箭头界类型参数（s35）。防误伤正例：s11–s14、s21、s22、s24、s28–s31、s33、s34。沙箱不改真实仓库任何生产代码与索引。

### 7.2 机械核对结果

- 分母核清：§1.3 计划清单逐文件表 266 条 = 冻结基线树（`git ls-tree -r 437bd53`）七模块 `src/main/*.kt` 实际 266 条，对射核验无缺失、无重复（逐模块 engine 114 / protocol 39 / server 27 / client 20 / presentation 63 / lwjgl3 2 / android 1，与计划书各节标注数完全一致）。§1.1 的"266 主源码"口径在当前基线仍然成立。
- 六个 V5 导出器测试（`engine/src/test` 下 `V5*GoldenExporterTest`）为黄金样本提取工具，属测试不属生产，不计入 266 分母——回应"新导出器测试是否在 266 之内"的疑问：不在。
- 锚点核验（八轮口径，全部对冻结基线，**词法剥离 + 箭头感知结构化声明解析 + 声明边界核验**）：V5 C# 锚点逐条核验路径规范性、`dotnet/src/` 类别、存在于基线树、符号为合法单标识符且在该提交 blob 内以真实类型声明存在；V4 测试锚点同理；每行 `v4Symbol` 为合法单标识符且在该行源文件的基线 blob 内真实声明（属性/函数 receiver——含可空、泛型、函数类型实参书写、import、注释、字符串、泛型参数、超类型位置不构成声明；属性声明取链真实末段且链后为合法声明续元）；266 行 0 失败（四轮紧匹配下修正 2 行伪 v4Symbol，五/六/七/八轮扫描器与解析器历次修正对 266 行真实代码持续全绿，正例日志逐轮字节一致证明零误伤）。验收锚点 9 个不同 (文档,提交) 对逐条通过核验：提交真实存在、为冻结基线 437bd53 祖先、文档在该提交中存在、文档也在基线树中。
- 数据修正记录（行级修正，不放宽校验）：二轮——SRV-019 `TcpOutboundQueue` 改写为实际 V4 行为与正确 V5 责任（pending P5.1 `Mahjong.TableService`），清除 24 行依赖 G1 门槛循环；四轮——ENG-031 v4Symbol `GameAction`→`deepCopy`、SRV-027 `TcpFrame`→`toByteArray`；五/六/七/八轮——实现 JSON 零改动（均为校验器词法/解析层修复）。

### 7.3 现状统计（由行实算，校验器强制与声明一致）

| 状态 | 行数 | 说明 |
|---|---|---|
| accepted-component | 21 | 由行实算：engine 20（P1.1 物理黄金 4、P1.4 三绝技域/账本/经济 9、P3.3 计时/控制 5、P2.1 SeatControlView 1、P3.4 ControllerRegistry 1）+ server 1（P3.1 HostDispatch）。P5.3 大厅、P5.4a 客户端通道组件虽有验收记录，但其 V4 行级映射未全量对齐，全部计为 partial，不在本行 |
| partial | 126 | 有 C# 对应物/组件承接但无独立验收或映射未完成 |
| pending | 99 | C# 未建，全部带责任任务与依赖（含二轮改写的 SRV-019 服务端出站队列） |
| replaced-by-v5-contract | 19 | MJ-TCP/1 链路与进程内回执由 Wire v1/持久回执替换，不迁移（protocol 8、server 8、client 2、engine 1）；服务端出站队列语义不在其列（见 7.2 SRV-019 修正） |
| platform-blocked | 1 | Android 真机/IL2CPP（P6.2） |

逐模块：engine 114 / protocol 39 / server 27 / client 20 / presentation 63 / lwjgl3 2 / android 1。

### 7.4 关键诚实边界（逐条）

1. **accepted-component ≠ 等价验收**：仅指对应 V5 组件在冻结基线内有明确验收记录；G1 未验收，不宣称与 V4 全量等价。
2. **算分/轮次/码牌**：C#（Rules、RoundStateMachine、Stacking）已存在，但无独立验收记录，全部 partial；`tests/v5-golden/` 中 round/scoring/shuffle 样本是导出样本，不构成验收。物理（physical-v1）与三绝技（cheat-v1）、AI（ai-v1）分别对应 P11/P1.4/P1.6 的组件验收。
3. **AI**：ai-v1 为记录意图回放（`V5-P1.6-AI-GOLDEN.md@b49911c` + 独立复核@437bd53）；不宣称全量策略覆盖。策略集、能力注册、启发式未移植（partial，责任 P1.6）。
4. **ApplicationCoordinator（3,336 行）与 TableAuthority（2,828 行）**：不做 1:1 搬运；串行化与部分语义由 P3.1/P3.4 组件承接，拆分未完成（partial）。
5. **规则特况**：立直/副露/振听/双响三响裁决 C# 未建（`RoundActionKind` 经 P0.2 裁决明确排除这些动作，责任归规则层 P1.3）；42 役逐役差分未验收（D-01 口径以 V4 代码 42 项为准）。
6. **传输**：MJ-TCP/1 是 V4 唯一生产传输；V5 以 WebSocket + Wire v1 替换，replaced-by-v5-contract 共 19 行。服务端出站队列、慢消费者背压与字节预算语义**不是**"被契约替换"——`TcpOutboundQueue` 行为 pending P5.1（服务端 WSS 发送侧未建；客户端持久 Outbox 不承接服务端发送队列）。服务端 WSS 宿主源码不在本基线树内，宿主装配责任 P5.1，G1 仍 BLOCKED（`V5-P31-WSS-INTEGRATION-20261024.md`）。无任何"删除 V4"或"回退旧协议"语义。
7. **回执/恢复**：V5 单一持久回执替换 V4 进程内回执与在飞表；`ITableStore`/`IReceiptStore` 端口未交付（P3.4/P5.2）。
8. **本地宿主**：`Mahjong.LocalHost` 为空 csproj（预构建 DLL 与空源码工程不一致、已过期）；进程内宿主与 TableService 装配分别待 P4.1/P5.1。
9. **平台阻塞保持记录**：Unity Editor 许可证（exit=198）、Windows/Android IL2CPP 缺 MSVC/Windows SDK/NDK、iOS/macOS 与真机弱网未实测（`V5-1-AOT-REVIEW.md@e41a998`）；Unity 表现层 63 行全部 pending（P4.2–P4.4，`unity/` 无 .cs 源，仅预构建 DLL）。
10. **并行任务边界**：黄金清单 `V5-0-GOLDEN-MANIFEST.md`/`catalog-v1.json` 及其 fixtures/校验器由并行任务冻结，本章未改动；P3.2 联调、CI 环境修复均只作为责任引用，不依赖其未合并产物。

### 7.5 机器校验证据

- 正例（真实仓库，只读）：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/traceability/validate-implementation-trace.ps1 -RepoRoot .` → `tracked-main-kt=266 rows=266`（枚举自冻结基线树），`RESULT=PASS`，**exit=0**（2026-10-24，日志 `artifacts/traceability-20261024/work/r8-positive-run1.log`，SHA256 `2ff14b020dbeb2450da9dbe2cb0de650517f484d38b311acdf0022912c7e2b8a`；与三/四/五/六/七轮正例日志字节一致——统计口径未变，八轮箭头感知解析与声明边界核验对 266 文件真实代码零误伤）。
- **八轮独立复核输入复跑（reviewer 原始证据不覆盖，全部自隔离副本）**：本轮 7 例 probe 复制到 `artifacts/r8-probe-round7-extra/`（probe.ps1 与 reviewer 原件 SHA256 一致 `3bca1c6a…23ac`）后执行。第八轮校验器 **7/7 全部符合预期**：`positive`（DemoEngine/DemoV5）actual=0、`alternative-verbatim-string-only` actual=1、`positive-after-alternative-verbatim` actual=0、`function-type-receiver`（`val Map<(String) -> Int, List<Int>>.functionProperty` 的 receiver `Map`）actual=1（`v4-symbol-not-declared-at-baseline`）、`positive-function-type-property`（`functionProperty` 真实末段）actual=0、`nested-generic-receiver`（`List<List<Int?>> . nestedProperty` 的 receiver `List`）actual=1（同标记）、`positive-nested-generic-property`（`nestedProperty`）actual=0；`PROBE_FAILED=0 TOTAL=7`（日志 `work/r8-probe-round7-extra.log`，SHA256 `e24ef42c604e7c2786d7572ac7fccf32ff6531a4fb47bfafcd2db41aaf2aec3d`；reviewer 在七轮校验器 adca621 下的原始判定为其原档 `artifacts/codex-traceability-round7-extra/` 内 probe-console.log 与 `artifacts/codex-traceability-round7-review/REVIEW.md`：2 例判定错误，其余 5 例对照正确）。
- **七轮 7 例 probe 回归复跑**：`artifacts/codex-traceability-round6-review/probe.ps1` 自隔离副本 → **7/7 仍全部符合预期**（nullable/generic 四例 + 三对照；日志 `work/r8-probe-round6-regression.log`，SHA256 `623a77bd…0893`，与七轮终跑字节一致）。
- **六轮 6 例 probe 回归复跑**：`artifacts/codex-traceability-round5-review/probe.ps1` 自隔离副本 → **6/6 仍全部符合预期**（verbatim-string-only/spaced-property-receiver=1、四正例=0；日志 `work/r8-probe-round5-regression.log`，SHA256 `88737f48…cf30`；判定与七轮一致，字节差异仅为七轮在原目录复跑时沙箱不洁产生的 git "nothing to commit" 两行噪声——八轮副本为全新沙箱首次运行的干净输出）。
- **五轮 6 例 probe 回归复跑**：`artifacts/codex-traceability-round4-review/probe.ps1` 自隔离副本 → **6/6 仍全部符合预期**（四负例=1、双正例=0；日志 `work/r8-probe-round4-regression.log`，SHA256 `d14598dd…a6fa`；字节差异来源同上）。
- **历史轮次（保留备查）**：一轮 5 个注入负例初版错误 PASS，二轮转 exit=1；三轮类型伪造 5 处与自依赖注入转 exit=1；四轮注释词伪符号与 2 行 receiver 伪 v4Symbol 转正确拒绝；五轮 Kotlin 嵌套注释/插值洞/紧凑属性 receiver 转正确拒绝；六轮逐字串花括号/Kotlin 任意花括号开洞/空白点属性链转正确判定；七轮可空/泛型 receiver 截断转正确判定（`artifacts/codex-traceability-review-20261024/`、`artifacts/codex-traceability-round3-review/` 至 `round6-review/`、`work/r3-*.log` 至 `work/r7-*.log`）。历史自测（46/50/58/65/69 案例）系我方自证，复核方未回传完整结果，不作为独立全绿援引。
- 负例自测（入 git，可从干净检出复跑）：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5Traceability.Tests.ps1` → **75/75 案例通过（15 正例 PASS + 60 负例全部 exit=1 且命中预期标记），SELFTEST=PASS，exit=0**（2026-10-24，日志 `artifacts/traceability-20261024/work/r8-selftest-raw.log`，SHA256 `decbe123ed33ddae968ed879759b703859af6a45e358fbc4887506b8d87abfda`；s30/s31/s33/s34 exit=0、s32/s35 exit=1 逐例可见）。负例覆盖：缺行、重复源行、未知状态、基线树外锚点路径、伪造锚点符号、pending 无 gap/责任、坏 JSON、统计造假、基线树外多余行、accepted 无验收锚点、伪造 v4Symbol、伪造验收提交、缺锚点 symbol、缺 V5 归属、伪造任务/依赖 ID、门槛依赖循环、路径逃逸、锚点类别错配、非祖先验收提交、文档缺在于提交、未知 V5 目标模块、直接自依赖（n22）、类型负例 t01–t17（string 字段收数字/布尔、数组字段收标量/null/对象、数组内 null/数字元素、锚点 path 收数字、denominator 收字符串、stats 值收字符串、stats 未知键、状态词表偏差、gatePrerequisites 收标量、golden 头字段收标量、rows 收标量）、沙箱 s2–s10/s15–s20/s23/s25–s27/s32/s35（基线后文件行、基线后 v4Symbol、基线后锚点符号、基线后锚点文件、基线后代验收提交、注释-only、字符串-only、泛型参数、函数 receiver、**属性 receiver 紧凑/空白点/可空/泛型/嵌套泛型/函数类型实参全形态、箭头界类型参数、嵌套块注释、插值洞内嵌串、非单标识符、三引号原始串、非插值逐字串花括号、Kotlin `${` 模板洞内嵌串**）。
- 校验器核验项（八轮）：冻结基线树枚举对射、仅 main+仅 .kt 卫生、严格 JSON 原始类型 schema（行字段与 header 全量，数组元素与锚点子字段逐级，null/false 元素不跳过）、模块与状态受控枚举、statusVocabulary 全等、v5Targets 受控词表、责任任务/依赖对照任务书真实标题 ID + 直接自依赖拒绝 + 门槛反循环、锚点路径规范性 + 类别 + 基线树成员 + 符号为合法单标识符且在词法剥离后的基线 blob 内按声明语法真实声明（词法扫描器 + 结构化声明链解析器 + **箭头感知平衡类型实参扫描** + **属性声明边界核验**：属性/函数 receiver——含可空、多参数/嵌套泛型、函数类型实参 `(A) -> B` 及合法空白——、类型实参内容、import、注释、字符串、超类型一律不构成声明；类型名紧跟关键字；`fun` 名后紧跟 `(`；属性声明取链真实末段且链后必须为 `:`/`=`/`by`/`get`/`set` 合法续元）、v4Symbol 同口径同标识符强制、验收提交存在性/祖先性/文档在提交与基线存在四重核验、golden 受控目录且在基线树、非 accepted 状态必须有 gap+责任、分母与逐模块/逐状态统计与行实算一致。证据明细：`artifacts/traceability-20261024/EVIDENCE.md`。

### 7.6 结论

P0.1 的"矩阵每行有源码/测试证据、V5 归属、状态；无待定而缺责任任务"在当前基线以 `implementation-v1.json` + 校验器 + 入 git 自测的方式达成机械可复核形态：**全字段严格 JSON 类型 schema**、**全部锚点与符号证据锚定在冻结基线 437bd53 的树与 blob，且经词法状态扫描器 + 箭头感知结构化声明链解析器（嵌套注释/原始串/逐字串/插值与模板洞按语言正确隔离；receiver 链——可空、多参数/嵌套泛型、函数类型实参、空白点、限定名——完整解析取真实末段，属性链后必须命中合法声明续元）剥离后的真实声明语法核验**（当前索引/工作树不构成证据；注释、字符串、receiver、类型实参、泛型参数、超类型中的词与非单标识符一律不构成声明）、**无 skip 无隐藏绿分支且沙箱失败按真实案例数计数**、验收证据链、责任任务、依赖语义（含直接自依赖与门槛循环双向拒绝）、统计口径全部受机器强制，八轮独立注入的伪造输入（类型伪造、基线后文件/符号冒充、自依赖、注释/字符串/泛型/各类 receiver——紧凑、空白点、可空、泛型、函数类型实参——/箭头界类型参数/嵌套注释/插值洞/逐字串花括号/模板洞伪声明）不再被放行，防误伤正例（普通/扩展属性含空白点/可空/泛型/函数类型实参/嵌套可空泛型形态、typealias、扩展函数名、泛型约束界函数名、逐字串与花括号串后真实声明）同时保证不误拒真实声明。同时为 G1 提供不漏项证据：266 个 V4 生产行为面全部有显式状态与未完成责任，无批量"已完成"宣称。本章不改变 §6 的交付边界，也不解除 §5 表中任何未决项。
