# V5-1-REVIEW — 首次当前集成头阶段审查报告（G1 证据盘点，第 2 轮返修版）

> 本报告由实现侧审查代理产出，**不构成 G1 门槛批准**。caller 需独立复核全部证据锚点后另行判定。
> 依据 `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md:108`（下称"任务书"）："生成 `V5-1-REVIEW.md` 记录实际测试数、失败、设备与未覆盖范围。"

## 0. 审查元信息与修订记录

| 项 | 值 |
|---|---|
| 审查对象（唯一固定头） | `8ee22ebf5164f74db75e5a33e87900f5b87cd8bd`（PR19 head，分支 `feat/v5-1-integration`） |
| 审查工作树 | `D:\majiang-wt\g1-readiness`，分支 `wt/v5-g1-readiness`；当前 HEAD `98d784624fb5071dd7c24260a42bf621777dfce4` = 审查头 + 仅本报告文件（`git diff --stat 8ee22eb..98d7846`：1 file changed, 339 insertions，源码面与审查头逐字节一致；本轮探针 A 段复核） |
| 审查日期 | 2026-10-25（第 1 轮报告 commit `98d7846`；本版为第 2 轮返修） |
| 第 2 轮独立探针证据 | `C:/Users/sncdc/AppData/Local/Temp/g1-review-r2-evidence/run-probes.ps1`（脚本）与 `probes.log`（SHA256 `3138e0b27e61e95a549018bc92ccf5ea360e4d33f154c69ead730bdba209c09a`，183 行，EXIT=0） |
| caller 复核证据（只读引用） | `C:/Users/sncdc/AppData/Local/Temp/g1-caller-review-20261025/`：source-audit.log SHA `49DA45BB…AC48E`、audit.log SHA `0BA76DF4…60D63`、catalog.log SHA `E3F631D9…8F6D2`；caller 本树 5 次定向测试 1187/0/0（Domain 322/TableRuntime 313/Contracts 104/Ai 316/Client 132，日志 SHA 见 §10 引用） |
| 结论 | **G1 不批准**。九门逐项：**7 PASS / 2 FAIL / 0 BLOCKED / 0 NOT_RUN**（§2，与九行一一对应） |
| 历史冻结审计 | 依赖矩阵 `437bd53`、黄金目录 `205506f`：引用其结论，不改写历史、不把已完成组件重列为未做 |

**范围声明**：本报告只读源码/既存 CI 产物 + 独占 TEMP 探针；唯一被修改的受管文件是本报告。不读取/不合入两个在途兄弟任务的未提交产物；不重复下发；V4 保持唯一生产基线。

### 0.1 第 2 轮返修逐组映射（R1-1..R1-5 → 本版落点）

| 组 | 第 1 轮缺陷 | 本版处理 | 落点 |
|---|---|---|---|
| R1-1 | 假发现"仅 Lobby 有装配级静态测试" | **撤回**。固定头实有 8 处依赖方向/冻结契约断言（§3.3 保护边清单）；门 3 改判 **PASS（现有守护）**，残余为精确白名单增强建议（I-5，非门）；S-1 重写（不拒绝 BCL/Protobuf/Npgsql，列统一发现纳管） | §3.3、§7 I-5、§9 S-1 |
| R1-2 | "已导出 10,382 用例经 validator 对齐"、门 2 PASS | **撤回**。validator 自述 never executes production code（机械校验）；catalog status=`evidence-index-not-acceptance`；G-028a 全序列未断言、G-003b structural-only、mv-swap-drawn-backfill 仅结构。门 2 改判 **FAIL**，五层拆分 + 残余责任单列（新缺陷 N-3） | §3.2、§7 N-3 |
| R1-3 | 动作差集漏 DealComplete/DeclareExhaustiveDraw；错标 P3.2/P3.3 职责；D-3/D-4 无出千/计分实际任务；DAG 缺入边 | **重写**。完整 13 动作映射表（§4.2）；事件按 协议 21=19+2（schema 冻结）/域内 16 内部事件/出千私有两类 分层（§4.3）；残余按任务书正名（P1.1/P1.3/P1.2/P1.4/P3.1 范围延伸）标"建议 G1 补齐子任务，尚未分配"（§7 N-1/N-2、§8）；已完成 P3.2=按流投影和快照恢复、P3.3=计时托管控制竞态 不改名（§4.5） | §4、§7、§8 |
| R1-4 | 门 4 计数矛盾（5/2/2 vs 实际行）；D-2 把建议变强制门；平台门自扩张 | **改正**。每门一个状态，总计 7/2/0/0 与九行一致（§2）；D-2 降级为 I-4（G1 原文只要求状态不含隐式权威，未要求特定 source-scan 守护）；门 9 按 P2.2 出口口径判（可用平台原始日志 + 缺口分列），Unity/IL2CPP 子项 BLOCKED 明示且无通过宣称 | §2、§3.4、§3.9、§7 I-4 |
| R1-5 | 锚点/格式错误（守恒混用、spectator 测试位置、meta 文件名、§6 断链、§12 占位符、§1 表述、缺陷缺复现/SHA） | **全部改正**。物理 136 守恒（TilePositionMap/PhysicalGoldenTests）与点棒/点数守恒（RoundStateMachine:742-750）分列（§3.5）；spectator 测试=`Mahjong.Client.Tests/IndependentReviewTests.cs:66`（§3.7）；meta=`dotnetTest.meta.txt`（§5.2）；探针统一为 §6 编号并给真实日志+SHA；§12 全部可复制命令无占位符；缺陷表每条含复现/证据 SHA | 全文 |

## 1. 环境前置核对（实际执行）

- `git rev-parse HEAD` → `98d7846…ce4`；分支 `wt/v5-g1-readiness`；`git status --porcelain` 空（探针 A 段）。
- 未触碰 `D:/majiang` 主 checkout 及其用户未提交文件；未 reset/覆盖任何兄弟工作树；未 push/建 PR/触发远端 CI。
- 文档阅读：`AGENTS.md`、`DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`V5-0-1-IMPLEMENTATION-TASKS.md`、`V5-0-GOLDEN-MANIFEST.md`、`UNITY-ENVIRONMENT.md` 全文通读用于口径；此外**大量源码文件与 CI 产物按证据需要阅读**（第 1 轮"读过且仅读过四文档"表述有误，特此更正；主 checkout 用户文件始终未动）。
- 本报告引用的 CI 产物位于 caller 下载基 `D:/majiang-wt/codex-progress-20261025/artifacts/progress-20261025/`（只读）。

## 2. G1 总结论

九门逐项状态（每门恰好一个状态；总数=行数）：

| # | 门条件（任务书 :108） | 状态 | 一句话依据 |
|---|---|---|---|
| 1 | `dotnet test` 独立全绿 | **PASS** | 双 CI run 1,356/0/0 逐 meta 亲读（§5.2）；caller 本树 5 项目 1,187/0/0 复证（§0 表） |
| 2 | 已导出黄金样本对齐率 100%，未导出样本单列 | **FAIL** | 机械目录校验 PASS ≠ 行为全字段对齐：G-028a 全序列未断言、G-003b/mv-swap-drawn-backfill 仅结构、20 partial 未对齐（§3.2 五层拆分，N-3） |
| 3 | 依赖方向静态测试通过 | **PASS** | 固定头 8 处静态断言存在且双 run 全绿（§3.3 保护边清单）；残余精确白名单为建议（I-5，非门） |
| 4 | Domain 不含隐式随机/时钟/浮点权威 | **PASS** | 严格 grep 零命中（探针 C 段 NO-MATCH）；守护测试为建议非门条款（I-4） |
| 5 | 三绝技与 136 守恒通过 | **FAIL** | 136 守恒组件级证据充分；三绝技域层组件在、权威命令链未通（13 动作 actor 仅通 2，§3.5/§4.2，N-1/N-2） |
| 6 | Wire 可重复生成 | **PASS** | 稳定字节/字段号/错误码稳定 Fact 群 + Contracts 104 全绿（§3.6） |
| 7 | 按座位/观战私有性通过 | **PASS** | 可见性 6 Fact + 观战命令前置拒绝（IndependentReviewTests.cs:66）+ 座位级 AI 观察（§3.7） |
| 8 | TableRuntime 并发、版本与恢复合同通过 | **PASS** | TableRuntime 313 全绿 + CI 恢复/租约/回执证据事件（§3.8） |
| 9 | Unity/IL2CPP 可用平台的构建日志和缺口分列 | **PASS**（按 P2.2 出口口径） | 可用平台（Windows .NET）有原始构建日志；不可用平台（Unity Editor/IL2CPP/NDK/iOS）缺口分列、标阻塞、无通过宣称（§3.9）；Unity/IL2CPP 子项状态=**BLOCKED** 明示 |

**计数核对：PASS=门 1,3,4,6,7,8,9（7 项）；FAIL=门 2,5（2 项）；BLOCKED=0；NOT_RUN=0；合计 9 = 行数。**

**G1 不批准**：门 2（黄金行为全字段对齐未证实）与门 5（三绝技权威链未通）未满足。在 N-1/N-2/N-3（§7）完成前保持 V4 唯一生产基线，不进入 V5-2/3 集成验收。本报告与第 1 轮"BLOCKED"结论在"不批准"上一致，但门级判定与计数以本版为准。

## 3. G1 门条件逐项证据

### 3.1 门 1 `dotnet test` 独立全绿 — PASS

两 CI run（§5.1）7 测试项目 meta（`dotnetTest.meta.txt`，每项目一份，共 14 份）全部 exit 0，合计 **1,356 passed / 0 failed / 0 skipped**：Ai 316、Client 132、Client.Wss 80、Contracts 104、Domain 322、Lobby 89、TableRuntime 313。解构建 exit 0、12-DLL 闭包 PASS、summary 9 行 OVERALL PASS（7 测试项目 + 2 个仅构建 helper，helper 未计入用例数）。caller 于本树（同源码面）5 次定向 `dotnet test` 复证 1,187/0/0 全 exit 0（含一次非标准输出路径失败后按受支持路径复验，详见 §10，不归因历史 39970580589、不称 flaky）。

### 3.2 门 2 黄金对齐（已导出 100% / 未导出单列）— FAIL

**五层拆分（不得混计）**：

| 层 | 事实 | 证据 |
|---|---|---|
| L1 机械目录校验 | PASS：validator 只查存在性/SHA/schema/计数/ID 去重/锚点/manifest 覆盖，**自述"never executes production code"**；本树复跑 PASS exit 0，catalog SHA `16F7FD7F…891B`，fixtures=5/requirements=73/cases=10,382/random=10,000/facts=42/coverage=full:17/partial:20/none:33/v5-only:3 | 探针 H/O 段；`tools/verify-v5-golden-catalog.ps1:9-11`；`tests/v5-golden/catalog-v1.json:4-5` |
| L2 目录自我定位 | catalog `status=evidence-index-not-acceptance`，notice 明示"does not assert G1/G5, V5 production readiness, or full 73-item coverage"——**10,382 是目录 case 数，不构成行为对齐分子** | catalog-v1.json:4-5（探针 H 段） |
| L3 真实 C# 行为差分（已存在） | 17 项 full 覆盖需求中含真实调用断言，如 PhysicalGoldenTests 逐项对照 C# 公开 API 且"所有 expected 都来自 V4 真实调用"、"不把 C# 实现当 oracle"；scoring/cheat/round 各黄金测试为真实 C# 执行 | `PhysicalGoldenTests.cs:15-27`（探针 H 段 :20-22） |
| L4 结构-only 与未断言字段边界 | ① G-003b V4 mutable 损坏注入探针在 C# 侧记 **structural**（C# 无可变容器，"没有把不可复现的 mutable 损坏注入写成 PASS"）；② `TileMovement_DrawnBackfill_IsAV4OnlyPath_RecordedAsStructure`（CheatGoldenTests.cs:644）：mv-swap-drawn-with-wall-backfill 为 V4-only 路径，C# `ChooseBeamSwap` 按设计拒绝摸牌实体，**仅断言黄金内部结构**；③ G-028a（manifest :343，`pending/partial`）：`RoundTerminalReplaysExactly`（RoundReplayGoldenTests.cs:101）断言 version/phase/result/scores/rejected+`TsumoWin` 在场（:134），**不断言 `MatchEnded` 缺席与完整 eventTypes 序列（ScoreSettled/RoundEnded）**；④ 20 项 partial 覆盖需求未对齐，33 项 none 单列 | manifest :343；三处测试源码（探针 H 段行号） |
| L5 未导出/无覆盖 | none:33 单列于目录 coverage 字段；v5-only:3 另列 | validator 输出（探针 O 段） |

**判定**：门 2 的"已导出黄金样本对齐率为 100%"必须指行为级全字段对齐；L4 所列边界（G-028a 序列、V4-only 路径、partial×20）使其当前**不可宣布**。**不批准该门**；残余逐项落 N-3（§7）。历史 P0.1 冻结审计（437bd53/205506f）的已接受组件结论不被本判定改写——本门判定针对"当前集成头可宣布的 G1 对齐口径"。

### 3.3 门 3 依赖方向静态测试 — PASS（现有守护；残余增强为建议）

第 1 轮"仅 Lobby 有装配级测试"为**假发现，撤回**。固定头实有 8 处（全部双 run 全绿，行号经探针 B 段复核）：

| 断言 | 位置 | 实际保护边 |
|---|---|---|
| `DomainDoesNotReferenceContractsWireProtobufUnityOrDatabase` | `dotnet/tests/Mahjong.Contracts.Tests/SchemaCompatibilityTests.cs:376` | Domain ✗ Contracts/Wire/Google.Protobuf/UnityEngine/System.Data |
| `ContractsDoesNotReferenceWireOrProtobuf` | 同文件 :389 | Contracts ✗ Wire/Protobuf |
| `WireReferencesContractsAndProtobuf` | 同文件 :399 | Wire ✓ Contracts + Google.Protobuf（正向） |
| `EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents` | 同文件 :409 | 协议事件面冻结 19+2（门 2/事件口径相关） |
| `DomainAssemblyDoesNotReferenceContractsUnityNetworkOrDatabase` | `dotnet/tests/Mahjong.Domain.Tests/TilePositionMapTests.cs:1049` | Domain ✗ Contracts/Client/Server/TableRuntime/LocalHost/UnityEngine/UnityEditor/System.Net/System.Data/Microsoft.Data（子串匹配，覆盖 Microsoft.Data.SqlClient 类） |
| Ai 程序集禁边（含观察暗牌属性扫描） | `dotnet/tests/Mahjong.Ai.Tests/AiDecisionTests.cs:437-452`（断言消息 :449） | Ai ✗ TableRuntime/Contracts/LocalHost/UnityEngine |
| `ClientAssemblyDoesNotReferenceDomainOrTableRuntime` | `dotnet/tests/Mahjong.Client.Tests/TableSessionTests.cs:749` | Client ✗ Domain/TableRuntime/LocalHost；✓ Contracts + Wire（正向） |
| `LobbyAssemblyDoesNotReferenceDomainOrTableRuntime` | `dotnet/tests/Mahjong.LobbyService.Tests/ArchitectureAndPolicyTests.cs:13` | Lobby ✗ Domain/TableRuntime |

**未保护边（残余，均为建议非门）**：TableRuntime 允许集（Domain/Ai/Contracts）无正向白名单断言；Wire ✗ Domain/Client 未显式断言；LocalHost ✗ 其余未断言；BCL 层面（如 Domain ✗ System.Net 已在 :1049 覆盖，其余程序集未逐一遍历）。→ I-5（§7）。

**门 3 判定**：G1 条款是"依赖方向静态测试通过"。现有 8 处断言覆盖核心禁边（Domain=∅依赖、Contracts✛Wire、Client✛Domain/TableRuntime、Ai✛TableRuntime/Contracts、Lobby✛Domain/TableRuntime）且全绿 → **PASS**。不能把"非完整白名单"偷换为"无测试"（第 1 轮错误），也不把白名单精确化升格为门条件。

### 3.4 门 4 Domain 无隐式随机/时钟/浮点权威 — PASS（现状；守护为建议）

严格 grep（`dotnet/src/Mahjong.Domain` 全部 .cs）：`new Random(|DateTime.Now|DateTime.UtcNow|DateTimeOffset.Now|DateTimeOffset.UtcNow|Guid.NewGuid|Stopwatch.|Environment.TickCount|Thread.Sleep|Task.Delay|Math.Random` → **NO-MATCH**（探针 C 段，可复现命令见 §12）。

边界情况（均非"隐式权威"，如实记录）：
1. `RoundRecovery.cs:735/790/876/893/1184/1242`、`RoundStateMachine.cs:1039/1063`：读取**调用方供给**的 `settlement.CompletedAtUtc`（`RoundTypes.cs:61-71` `PersistedUtcTime`）并哈希——时间为记录输入，非墙上时钟。
2. `Stacking/ShuffleConfig.cs`：`double` 仅作码牌调参，`"R"` 不变文化格式化（:97）；shuffle 确定性由 shuffle-v1（32 用例）与 P0.1 冻结跨 JVM/.NET LCG 差分覆盖。

**守护缺口定位**：任务书 G1 原文为状态要求（"Domain 不含隐式随机/时钟/浮点权威"），**未明文要求特定 source-scan 守护测试**；第 1 轮把"补 source-scan"当作 D-2 级 MAJOR 强制阻断属于自加门，**降级为建议 I-4**（§7）。当前状态 PASS 以本轮探针为证。

### 3.5 门 5 三绝技与 136 守恒 — FAIL（守恒证据充分；三绝技权威链未通）

**136 物理守恒（组件级证据充分，与点棒/点数守恒分列）**：
- 域层：`TilePositionMap` 为"exactly 136 physical tiles"的不可变位置表（`TilePositionMap.cs:184`），`TotalTileCount = 136`（:193）、构造期 136 槽强校验（:224）、`ConservationReport`/`ConservationException` 守恒审计（:125-180）；`PhysicalGoldenTests` 断言 `Assert.Equal(136, size)` 与 `Assert.Equal(136, distinctIdentities)`（`PhysicalGoldenTests.cs:104-105`）。
- 码牌/随机：shuffle-v1（32 用例）黄金差分；非法双占/重复拒绝在 P1.1 契约测试。
- （分列）**点棒/点数守恒**（非 136 物理守恒）：`RoundStateMachine.cs:742-750` 验证调用方结算的 score/stick 起终恒等，`:802-822` 验证结果一致性——此为**结算数值守恒**，不是 136 物理守恒的锚点（第 1 轮混用，更正）。

**三绝技（域层组件在、权威命令链未通）**：
- 域层（P1.4 已交付组件）：`dotnet/src/Mahjong.Domain/Cheat/` 8 文件——`SleeveBloom`（袖里藏花）、`BeamSwap`（偷梁换柱，含 `ChooseBeamSwap` 域层入口）、`TileMark`（落汗标记面）、`CheatSubmission`、`CheatLedger`、`CheatState`、`CheatEconomy`、`CheatProjection`；CheatGoldenTests 覆盖资格/费用/物理移动黄金（含 V4-only 路径如实记录，§3.2 L4）。
- 契约层：`CommandKind` 27 种含出千命令；协议私有两事件 `SleeveBloomResolved=20`/`BeamSwapResolved=21`（schema 冻结测试 SchemaCompatibilityTests.cs:409-417）；座位流要求受信 cheat 投影上下文（`TableActor.cs:5955-5969`）。
- **未通**：协议玩家/出千命令到域权威入口的 actor 适配仅覆盖 13 动作中的 2 个（`MapAction` `TableActor.cs:4819-4834`：`Pass`→:4824、`Discard`→:4827，其余 `default`→null→`UNSUPPORTED_ACTION` :1436/:1623）。域权威入口本身支持 9 种动作（`RoundStateMachine.Execute` :294，dispatch :207-225），但 Chi/Pon/Kan(claim)/Riichi/AgariYame **域动作也不存在**（§4.2 表）。→ N-1。

**判定**：136 守恒半项证据成立；三绝技半项"通过"要求资格/费用/窗口/等额物理移动在权威链端到端成立（任务书 P1.4 退出标准），当前命令面不通 → 门 5 **FAIL**。

### 3.6 门 6 Wire 可重复生成 — PASS

锚点（`dotnet/tests/Mahjong.Contracts.Tests/`）：`identical_projection_maps_to_stable_bytes`（ProjectionWireMapperTests.cs:12 起）、`EnvelopeFieldNumbersAreStable`、`CommandReceiptCursorAndSyncFieldNumbersAreStable`、`ErrorVocabularyIsStableWellFormedAndHasNoDuplicates`、`ApplicationCloseCodesMapToStableWebSocketCodes`、`EveryV4ApiErrorMapsToADistinctStableV5Code`、`RulesetAndProjectionMismatchesHaveDistinctStableCodes`、`StreamVersionMayRepeatWhileProjectionSeqAdvances`；`EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents`（SchemaCompatibilityTests.cs:409）冻结字段号 1..21。Contracts.Tests 104 双 run 全绿。

### 3.7 门 7 按座位/观战私有性 — PASS

- 投影层：`dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilityProjectionTests.cs:16-181` 六 Fact——`public_snapshot_uses_explicit_public_fields_only`、`player_snapshot_keeps_own_hand_and_draw_private`、`decision_is_visible_only_to_its_owner`、`private_events_reach_only_owner`、`public_events_reach_every_seat_and_spectator`、`settlement_events_gate_only_spectator`；另有 `different_hidden_states_produce_identical_public_snapshot`、`public_snapshot_does_not_expose_hidden_wall_or_hand_tiles`、`event_bodies_preserve_source_versions_and_redact_hidden_payload`。
- 会话层：`SpectatorMustRejectPlayerCommandBeforePersistenceAndSend` 位于 **`dotnet/tests/Mahjong.Client.Tests/IndependentReviewTests.cs:66`**（第 1 轮误标 TableStream 两文件，更正）：断言 `CountSent(Command)==0` 且 `store.SaveCount==0`——持久化与发送前双重拒绝。
- 协议策略层：`ProtocolEventPolicy.PrivateTypes`（ProtocolSnapshot.cs:69-78）显式 6 类私有（TileDealt/TileDrawn/RinshanDraw/CheatPointsChanged/SleeveBloomResolved/BeamSwapResolved），未知事件不静默当公有。
- AI 观察层：actor 座位级 `AiObservation`（`TableActor.cs:390-404, 4105-4200`）+ Ai 模型守卫（`AiObservation.cs:73-83` per-seat 契约、:318-321 上下文属主、:349 副露属主、:421 候选属主）。

### 3.8 门 8 TableRuntime 并发、版本与恢复合同 — PASS

TableRuntime.Tests 313 双 run 全绿；P3.4 回合探针在树（`CodexP34Round3..6ReviewProbes.cs`）；`TableDurableContracts.cs` state_version=命令版本 CAS、event_seq 逐事件、ownerEpoch+stateVersion 双栅栏、checkpoint 后重放校验；租约/恢复 `TableDurableLeaseRecoveryTests.cs`、`TableRecoveryService.cs`。CI 证据事件：`recovery.expiredReservation`、`receipt.replay`、`receipt.terminalReplay`、`concurrency.crossRoomJoin ×3`、`crossConnectionRowLock`、`transaction.rollback`、`ticketConsumption`（§5.4）。

### 3.9 门 9 Unity/IL2CPP 可用平台的构建日志和缺口分列 — PASS（按 P2.2 出口口径）

任务书 P2.2 出口原文："可用平台有原始构建日志，缺口有具体资源清单"；并明文授权"若缺 MSVC/SDK/NDK/iOS 设备，标阻塞并提供补验脚本，**不宣称通过**"。按此口径：

- **可用平台（Windows .NET，实际可用且已测）**：两 CI run 在 Windows runner 产出解构建 exit 0、12-DLL 闭包 PASS、9 项目行 PASS 与原始 job 日志（§5）；caller 本树 dotnet test 复证。原始日志存在 ✓。
- **不可用平台（Unity Editor / IL2CPP / Android NDK / macOS / iOS / 真机）**：双 run 无相关 job；环境缺口**分列**于 `doc/v5-重构/UNITY-ENVIRONMENT.md` 与 V5-0 T4 缺口清单（历史事实口径，本轮未重探查、不将历史证据表述为当前实测）；无任何通过宣称 ✓。
- Unity/IL2CPP 子项状态 = **BLOCKED**（工具链缺失，维持历史口径）；`Mahjong.LocalHost` 工程空壳（0 .cs）属 P4.1 未开工（G2 门内容，非本门缺陷）。

第 1 轮把该门自扩张为"所有平台必须全通过"，更正为上述 P2.2 口径；此为唯一一次口径校正，未降低 Windows 侧证据要求。

## 4. 组件状态分类与完整映射（P1.1–P3.4）

分类口径：完整功能 / 仅独立组件 / 接口先行 / 未接线 / 平台资源阻塞。

### 4.1 组件状态总表

| 组件 | 状态 | 关键证据 |
|---|---|---|
| Domain 牌实体/位置表/守恒（P1.1） | 完整功能（组件级，黄金差分） | TilePositionMap 136 槽+守恒审计；PhysicalGoldenTests 真实 API 对照 |
| Domain 计分 42 役（P1.2） | 仅独立组件，**未接线**（N-2） | `Rules/Yaku.cs` 42 条；`YakuEvaluator/WinningScoreEvaluator` 在 src/Rules 外零调用（探针 M 段） |
| Domain 单局/整场/计时/控制代次（P1.3） | 完整功能（组件级）；**命令面缺 5 类玩家决策动作**（N-1 域侧） | `RoundStateMachine.Execute` :294 覆盖 9 kinds；缺 Chi/Pon/Kan(claim)/Riichi/AgariYame |
| Domain 三绝技/账本/私有状态（P1.4） | 仅独立组件+黄金（含结构-only 边界），**权威链未接**（N-1） | Cheat/ 8 文件；ChooseBeamSwap 入口；CheatGoldenTests |
| Domain 码牌/记录随机（P1.5） | 完整功能（组件级） | ShuffleConfig+shuffle-v1 黄金 |
| AI 能力/意图记录（P1.6） | 仅独立组件，**无组合根**（N-4） | AiDecisionService 零消费者（探针 N 段）；BasicAiContextException :61/:502 |
| Contracts/Wire（P2.1/P2.2） | 完整功能（契约面冻结 19+2 事件、27 CommandKind、13 动作） | SchemaCompatibilityTests;ProtocolSnapshot |
| TableRuntime actor（P3.1） | 完整功能（组件合同）；**协议动作适配仅 2/13**（N-1 actor 侧） | MapAction :4819-4834；UNSUPPORTED_ACTION :1436/:1623 |
| 按流投影和快照恢复（P3.2，**正名**） | 完整功能（组件级，6 可见性 Fact+红action） | TableVisibilityProjectionTests；TableProjectionContractMapper |
| 计时、托管与控制竞态（P3.3，**正名**） | 完整功能（组件级） | actor 时钟/deadline/controlEpoch 守卫；TableRuntime 313 |
| 回执/租约/恢复端口（P3.4） | 接口先行（**按任务书明文**：仅接口+内存契约测试，真实 PG 属 V5-3A——阶段事实，非缺陷） | TableDurableContracts.cs:1427-1526；InMemoryTableStore.cs:147-152 唯一实现 |
| LobbyService/PG（P5.3 范围） | 完整功能（组件级） | 89 用例；PostgresLobbyStore；应用级重启回执重放 PostgresLobbyStoreTests.cs:619-671 |
| Client/Wss | 组件完整（132+80；TLS 正向链无条件 Fact） | WssEndpointTests.cs:96 + 4 负控 |
| LocalHost | 空壳工程（0 .cs） | P4.1 未开工（G2 门） |
| Unity/IL2CPP/Android/macOS/真机 | 平台资源阻塞（分列，无通过宣称） | UNITY-ENVIRONMENT.md；T4 清单 |

### 4.2 完整 13 个 ProtocolActionType 映射表（R1-3 要求，逐项可复算）

枚举源：`dotnet/src/Mahjong.Contracts/ProtocolCommand.cs:40-56`；域动作源：`dotnet/src/Mahjong.Domain/RoundTypes.cs:31-42`；域权威入口：`RoundStateMachine.Execute(RoundCommand)`（:294，dispatch :207-225，覆盖全部 9 kinds）；actor 命令适配：`TableActor.MapAction`（:4819-4834，仅 Pass/Discard）。

| # | ProtocolActionType | 域层入口 | actor 协议命令入口 | 分类 |
|---|---|---|---|---|
| 0 | Unspecified=0 | — | — | 占位 |
| 1 | StartStacking=1 | ✓ `StartStacking=0`（工厂含发牌，产 `TilesDealt` 事件） | ✗ 无分支（对局建立走 actor 回合装配路径，非玩家命令） | 域有 / actor 无玩家入口 |
| 2 | DealComplete=2 | **无同名值**；语义并入 `StartStacking`（发牌完成即 `TilesDealt`，协议侧映射 `TileDealt=3`/`DealingComplete=4` 事件） | ✗ | 无同名值 / 语义入口存在 |
| 3 | DrawTile=3 | ✓ `DrawTile=1` | ✗ 无分支（摸牌由权威回合推进驱动） | 域有 / actor 无 |
| 4 | DiscardTile=4 | ✓ `DiscardTile=2` | ✓ :4827 | **已接线** |
| 5 | DeclareChi=5 | ✗ 无域动作 | ✗ | **缺行为**（域+actor 均缺） |
| 6 | DeclarePon=6 | ✗ | ✗ | **缺行为** |
| 7 | DeclareKan=7 | **部分**：加杠有 `DeclareAddedKan=6`；明杠/暗杠 claim 无独立动作（域以 `ResponseClaimed`/`ChankanWindowOpened` 事件面存在，命令入口缺） | ✗ | **部分缺行为** |
| 8 | DeclareRiichi=8 | ✗ 无域动作（`RiichiSticks` 仅为状态/结算量，RoundTypes.cs:113-191） | ✗（决策**视图**已映射候选：`LegalActionKind.Riichi`→`DeclareRiichi`，TableActor.cs:5829-5830） | **缺行为**（视图有/执行无） |
| 9 | DeclareTsumo=9 | ✓ `DeclareTsumo=3` | ✗ 无分支 | 域有 / actor 无 |
| 10 | DeclareRon=10 | ✓ `DeclareRon=4` | ✗ | 域有 / actor 无 |
| 11 | Pass=11 | ✓ `Pass=5` | ✓ :4824 | **已接线** |
| 12 | DeclareAbortiveDraw=12 | ✓ `DeclareAbortiveDraw=8` | ✗ | 域有 / actor 无 |
| 13 | DeclareAgariYame=13 | ✗ 无域动作 | ✗ | **缺行为** |

域独有 kinds（协议无同名动作，承载方式注明）：`DeclareAddedKan=6`（协议经 `DeclareKan`+`MeldKind.AddedKan=5` 承载）；`DeclareExhaustiveDraw=7`（协议无同名动作；荒牌流局由 `WallExhausted=14`/`ExhaustiveDraw=12` 域事件与 `DrawGame=16` 协议事件承载，属权威自动判定面）。第 1 轮"缺 5/独有 AddedKan"遗漏了 DealComplete 的语义入口与 DeclareExhaustiveDraw 的承载差异，本表更正。

### 4.3 事件面分层（R1-3 要求：RoundEvent/CheatEvent/Match 与协议映射分开）

1. **协议冻结面（完整）**：`ProtocolEventType` 21 值 = V4 19 类（字段号 1-19 逐一冻结）+ V5 私有 `SleeveBloomResolved=20`、`BeamSwapResolved=21`；schema 冻结测试 `EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents`（SchemaCompatibilityTests.cs:409-417）；私有策略 `ProtocolEventPolicy.PrivateTypes` 6 类；未知事件拒绝（:79 IsKnownEventType，:256/:295 双闸）。**第 1 轮以"域内 16 vs V4 19"推断协议缺失是错误口径，撤回。**
2. **域内生命周期事件（内部）**：`RoundEventType` 16 值（RoundTransition.cs:56-74）含 V4 无的窗口/终局事件（`ResponseWindowOpened`/`ChankanWindowOpened`/`WallExhausted`），与协议 21 类**非一对一**，映射在投影层 `TableProjectionContractMapper`（WallBuilt :424、DealingComplete :433、RiichiDeclared :471、CheatPointsChanged :505、ScoreSettled :521、MatchEnded :531 等 payload 构造器在位）。
3. **出千私有事件**：协议 20/21 两类为 Private；域侧经 Cheat/Ledger/Projection 携带（checkpoint `TableActor.cs:2618-2641`；恢复 `:3371-3389`）。
4. **整场事件**：`MatchEnded=19` 协议在册；域侧 `MatchProgression.cs`（连庄/本场/终局归属）为另线模块。

### 4.4 持久化端口（P3.4，阶段事实）

接口 `ITableStore/IReceiptStore/ITableLease/ISettlementOutbox`（TableDurableContracts.cs:1427-1526），单原子边界 `CommitAsync`（:1469-1475 注释）；唯一实现 `InMemoryTableStore`（InMemoryTableStore.cs:147-152，四接口同实现，按 PG 同形事务约束）。任务书 P3.4 明文："这里仅完成接口与内存契约测试，**真实 PG 实现属于 V5-3A**"——内存-only 是**按计划的阶段边界，不是缺陷**（第 1 轮 D-6 撤回）。真实 PG 现存于 Lobby 线（PostgresLobbyStore/PostgresRoomProjectionReader）；跨进程重启探针见 §5.4。

### 4.5 AI 观察与决策（P1.6 组件 + P3.1 观察）

actor 座位级观察（:390-404/:4105-4200，含 Serving/时钟/决策窗/控制纪元守卫）；Ai 模型层本座位强制（§3.7）；`IAiStrategy.Decide(AiObservation, CancellationToken)` 双参签名冻结（AiDecisionTests.cs:431-436）；**组合根缺失**：`IAiStrategy/AiDecisionService` 在 src/Mahjong.Ai 之外零引用（探针 N 段），`BasicAiContext` 无生产构造点（喂真实 actor 观察将抛 `BasicAiContextException`，BasicAiStrategy.cs:61/:502）→ N-4。

## 5. CI 远端产物亲自核验记录

### 5.1 run 身份（自 run-info.txt 亲读）

| | ci-push | ci-pr |
|---|---|---|
| URL | https://github.com/liuchangready/majiang-game/actions/runs/40125990495 | https://github.com/liuchangready/majiang-game/actions/runs/40125992805 |
| GITHUB_RUN_ID / attempt | 40125990495 / 1 | 40125992805 / 1 |
| 实测 checkout | `8ee22ebf…8bd`（GITHUB_SHA） | `e1e0e88eae5518b0d4e6f4bb9532432ed11a6fc5`（PR19 merge，对 origin/v5 `457274c`） |
| ref / runner | refs/heads/feat/v5-1-integration / Windows | refs/pull/19/merge / Windows |
| 验证脚本 SHA256 | `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`（== 本地 `tools/verify-v5.ps1`） | 同左 |

产物本地基（caller 下载，本代理只读）：`D:/majiang-wt/codex-progress-20261025/artifacts/progress-20261025/{ci-push,ci-pr}/`。

### 5.2 每项目 meta 与框架日志

两 bundle 各 7 个测试项目 run 目录；per-project meta 文件名为 **`dotnetTest.meta.txt`**（第 1 轮误写 JSON，更正；探针 K 段列出 7 条路径），14 份全部 exit 0、头一致、携带框架日志 SHA256（逐份读取核验）。汇总 `summary.log` 双份均 7 行项目计数 + OVERALL PASS 9 行。

关键 SHA256（第 1 轮重算，本轮复核一致）：

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

（注：探针 H 段日志中源自 CJK 注释的行在 PS 5.1 GBK 控制台显示为乱码，系控制台代码页显示问题；底层文件为 UTF-8，行号与 ASCII 片段可定位，内容以本报告引用的 UTF-8 读取为准。）

### 5.3 统一用例 vs wrapper 额外探针

统一 1,356 用例 = 7 测试项目 `dotnet test`；wrapper 十阶段（precheck/start-postgres/ownership-marker/environment/migrate/verify/archive-cluster-evidence/cleanup/env-restore/write-evidence）为额外 PG 包装层，不增减用例数；两 run wrapper-summary 均 10 阶段 PASS、verifier exit 0。env-restore：两 run 各 4 变量 `restored-absent`、mechanism=`provider-set-item-null`、`rootDeleted: true`。

### 5.4 PG 证据逐条（亲自读 evidence jsonl + root 日志）

两 run `postgres-evidence.jsonl` 均含：server identity（PG 16.10）、`terminal.projection`、`receipt.terminalReplay`、`recovery.expiredReservation`、`migration.repeated`（migrationRuns:3）、`concurrency.crossRoomJoin ×3`、`crossConnectionRowLock`、`receipt.replay`、`transaction.rollback`、`ticketConsumption`、`roomFill`。

**真实 PG 两进程重启写读：本轮两个 CI run 中不存在。** `root-logs/postgres.log` 仅 `ready-count=1`（单次 postmaster 启动），无 `restart.json`；evidence 无 `restart.write|restart.read` 事件。CI 内的"重启"证据是**应用级**回执重放（PostgresLobbyStoreTests.cs:619-671）。真正两进程探针（`dotnet/tests/Mahjong.LobbyService.PostgresProbe/Program.cs`：write→真重启（postmaster 启动时间必须变化 :198-202、读写必须异进程 :203-206）→read）由本地 `dotnet/scripts/pg-lobby-verify/verify-all.ps1` 编排，**不在 CI wrapper 内**（I-1）。caller 独立确认 ready-count=1。

### 5.5 正向 TLS 链归因

MTP/xUnit v3 框架日志为 UTF-16LE（`iconv` 转码后仅见 per-assembly total 汇总，无 per-test 明细，I-3）。归因为组合证据：(a) 正向用例为无条件 `[Fact]`（`WssEndpointTests.cs:96` `PositiveLocalWssTrustChainCompletesTlsHandshakeAndWireRoundTrip`，每测试独立根 CA + socket factory，机器存储零改动，:110-113 注释）；(b) Wss 80/0/0 全绿 meta；(c) 4 负控同为无条件 Fact（自签 :76、异主机名 :150、不受信根 :174、过期叶 :199，均断言 `TlsHandshakeFailed+Permanent`）。

### 5.6 历史 PR 39970580589 Client131/132 — UNRESOLVED（维持）

失败框架日志在历史 bundle 缺失，无法补齐根因；当前双 run 全绿不构成原因已查明；不盲重试、不标 flaky（I-2）。caller 本轮两次非标准输出路径的本地测试失败已定位为路径/发现机制问题并按受支持路径复验通过，**不归因于该历史失败**。

## 6. 本轮探针记录（第 1 轮 + 第 2 轮，全部可复现）

统一编号（第 1 轮 §3.2/§3.3/§3.4 的"§6.0/§6.1/§6.2"断链已废弃，改指本节）：

| 编号 | 探针 | 轮次 | 结果 | 证据 |
|---|---|---|---|---|
| P-A | 黄金目录 validator 本树复跑 | R1+R2 | PASS exit 0；catalog SHA `16F7FD7F…891B`；73 需求 full17/partial20/none33/v5only3 | probes.log O 段 |
| P-B | 依赖方向：8 处静态断言定位 | R2 | 全部在位（:376/:389/:399/:409/:1049/:449/:749/:13） | probes.log B 段 |
| P-C | 依赖方向 csproj/using 图 | R1 | Domain=∅、Contracts=∅、Wire→Contracts、Ai→Domain、Client→{Contracts,Wire}、Lobby→Contracts、TableRuntime→{Domain,Ai,Contracts}、LocalHost→TableRuntime | 第 1 轮 Bash 记录；结论并入 §3.3 |
| P-D | Domain 确定性严格 grep | R1+R2 | NO-MATCH | probes.log C 段 |
| P-E | 动作/事件四枚举逐值提取 | R2 | RoundActionKind 9 / ProtocolActionType 13 / ProtocolEventType 21 / RoundEventType 16 | probes.log D 段 |
| P-F | 域权威入口 dispatch | R2 | `Execute` :294 覆盖 9 kinds（:207-225） | probes.log E 段 |
| P-G | actor MapAction 面积 | R2 | 仅 Pass(:4824)/Discard(:4827)；P3.1 边界注释 :4830 | probes.log F 段 |
| P-H | 协议 19+2 冻结 + 私有策略 | R2 | :54-55/:69/:79/:91/:256/:295 | probes.log G 段 |
| P-I | Golden 结构-only 边界 | R2 | validator 头(:9-11)/catalog status(:4-5)/G-028a(manifest:343)/Physical(:20-22,:104-105)/Cheat(:644)/Round(:101,:134) | probes.log H 段 |
| P-J | 136 物理守恒锚点 | R2 | TilePositionMap :125/:172/:184/:193/:224 + PhysicalGoldenTests :104-105 | probes.log I 段 |
| P-K | spectator 前置拒绝锚点 | R2 | IndependentReviewTests.cs:66 | probes.log J 段 |
| P-L | CI meta 文件名 | R2 | 7× `dotnetTest.meta.txt` | probes.log K 段 |
| P-M | 兄弟任务符号 | R1+R2 | AccessCapabilities 0；Ai 三符号仅 AiCandidateModels.cs:21 注释 | probes.log L 段 |
| P-N | 计分生产调用者 | R1+R2 | src/Rules 外 NO-MATCH（未接线） | probes.log M 段 |
| P-O | AI 组合根 | R1+R2 | src/Mahjong.Ai 外 NO-MATCH；BasicAiContextException :61/:502 | probes.log N 段 |
| P-P | Ai 本座位观察（源读） | R1 | AiObservation.cs:73-83/:318-321/:349/:421 守卫 | 第 1 轮源读，锚点见 §3.7 |
| P-Q | 按流隐私测试锚点 | R1 | TableVisibilityProjectionTests 6+3 Fact | §3.7 |

第 2 轮探针脚本与日志：`C:/Users/sncdc/AppData/Local/Temp/g1-review-r2-evidence/run-probes.ps1`、`probes.log`（SHA256 `3138e0b27e61e95a549018bc92ccf5ea360e4d33f154c69ead730bdba209c09a`）。**历史 CI 证据（§5）为既存产物只读核验，与本轮新执行探针分开列示。** 未重跑项：本地 `verify-all.ps1` 全套（含 PG 重启探针）、77/91 串行 PS 自测、Unity/设备链路——无代码变化不盲重跑重型自测。

## 7. 缺陷/缺口清单（第 2 轮一次性全量；每条含复现/证据/修复/验收）

### 7.0 第 1 轮清单更正记录

| 原编号 | 处置 | 理由 |
|---|---|---|
| D-1"仅 Lobby 有静态测试" | **撤回（假发现）** | 固定头实有 8 处断言（§3.3）；"非完整白名单"≠"无测试" |
| D-2 source-scan 守护缺失为 MAJOR | **降级 I-4** | G1 原文为状态要求，未明文要求该守护；不得自加门 |
| D-3/D-4 | **重构为 N-1/N-2** | 原责任归属错标 P3.2/P3.3，且无出千/计分实际任务；按任务书正名 |
| D-5 | **重构为 N-4** | 内容不变，编号规整 |
| D-6"表事件端口仅 InMemory" | **撤回（非缺陷）** | 任务书 P3.4 明文真实 PG 属 V5-3A，阶段事实 |

### 7.1 现行缺陷/缺口（同类已合并）

**N-1 [MAJOR] 权威链动作适配面：13 协议动作仅通 2，含三绝技/鸣牌/立直/和了止全族**
- **位置**：`dotnet/src/Mahjong.TableRuntime/TableActor.cs:4819-4834`（MapAction）、`:1436`/`:1623`（UNSUPPORTED_ACTION）；`dotnet/src/Mahjong.Domain/RoundTypes.cs:31-42`（域 9 kinds，缺 5 类决策动作）；`RoundStateMachine.cs:207-225,294`。
- **复现**：探针 F/E/D 段（§12 命令 4-6）；或对 `wss://` 会话发 `DeclareTsumo` 类命令观察 `UNSUPPORTED_ACTION`（不要求执行，源码可复算）。
- **实际 vs 期望**：实际=玩家决策命令仅 Pass/Discard 到达域；期望=任务书 P1.3/P1.4 退出口径下，吃/碰/杠/立直/和了止与三绝技命令端到端（协议命令→回执→域事件→投影）成立。
- **证据路径**：probes.log（SHA 见 §0）；TableActor.cs/RoundTypes.cs 固定头源码。
- **修复要求**（**建议 G1 补齐子任务，尚未分配**；不得改名已完成 P3.2/P3.3）：
  - 域侧（P1.1/P1.3 范围延伸）：`RoundActionKind` 增 `DeclareChi/DeclarePon/DeclareKan(claim)/DeclareRiichi/DeclareAgariYame`，`Execute` dispatch 补 case，产对应域事件（claim 可沿用 `ResponseClaimed` 泛化或新增，须与协议 21 类映射表对齐）。
  - actor 侧（P3.1 范围延伸）：`MapAction` 补全 13 动作映射；三绝技命令接 `Cheat/CheatSubmission`/`ChooseBeamSwap` 等既有域入口。
  - 依赖：计分接线（N-2）依赖本项 Tsumo/Ron/Riichi 通道；AI 组合根（N-4）依赖本项候选面。
- **验收断言**：§4.2 表所有"缺行为/域有 actor 无"行转"已接线"；统一负例矩阵（7.2）全过；黄金对齐（round/cheat 目录）新增用例一致。

**N-2 [MAJOR] 结算计算未接线（计分评估器零生产调用者）**
- **位置**：`dotnet/src/Mahjong.Domain/Rules/YakuEvaluator.cs`/`WinningScoreEvaluator.cs`（src/Rules 外零调用，探针 M 段）；`RoundStateMachine.cs:21`（头注释"computed by the frozen rules layer; this class verifies"）、`:742-750`（结算守恒验证）、`:802-822`。
- **复现**：§12 命令 7（Get-ChildItem 排除 Rules 扫引用）。
- **实际 vs 期望**：实际=`RoundSettlement` 由调用方供给、状态机只验证；期望=权威链内由规则层计算结算（P1.2 组件已黄金验收，差在接线）。
- **证据路径**：probes.log M 段；RoundStateMachine.cs 源码。
- **修复要求**（建议 G1 补齐子任务，尚未分配；依赖 N-1）：结算由 YakuEvaluator/WinningScoreEvaluator 在权威链计算，保留守恒验证为第二道闸。
- **验收断言**：权威链产生的 settlement 与 scoring-v1（10,238 用例）一致；守恒断言对错误计算拒绝。

**N-3 [MAJOR] 黄金全字段对齐残余（门 2 关闭条件）**
- **位置**：manifest G-028a（`doc/v5-重构/V5-0-GOLDEN-MANIFEST.md:343`，pending/partial）；`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs:101,134`；`CheatGoldenTests.cs:644`；`PhysicalGoldenTests.cs:20-26`；manifest 内 20 项 partial 覆盖需求。
- **复现**：§12 命令 8-9；或读 manifest partial/pending 列。
- **实际 vs 期望**：实际=终局重放不断言完整 eventTypes 序列（ScoreSettled/RoundEnded 序列、MatchEnded 缺席）、V4-only 物理移动路径仅结构、20 partial 未对齐；期望=门 2"已导出黄金样本对齐率 100%"可依行为级证据宣布。
- **证据路径**：probes.log H 段；manifest；E/catalog.log（SHA `E3F631D9…8F6D2`）。
- **修复要求**（按 manifest 既定 pending 项推进，不新造口径）：① RoundTerminalReplaysExactly 增补全事件序列断言（含 MatchEnded 缺席/ScoreSettled/RoundEnded 次序）；② V4-only 路径（mv-swap-drawn-with-wall-backfill）明确产品决策（C# 入口或永久结构记录）；③ 20 partial 逐项转 full 或记录不可导出理由。
- **验收断言**：manifest 对应行转 accepted/full 或留有显式决策记录；validator 机械层与行为断言层分开报告，不合并成"100%"。

**N-4 [MAJOR] AI 组合根缺失（P1.6 组件未被权威宿主消费）**
- **位置**：`dotnet/src/Mahjong.Ai/AiDecisionService.cs`（src/Mahjong.Ai 外零引用，探针 N 段）；`BasicAiStrategy.cs:61,502`（BasicAiContextException）；`Mahjong.LocalHost/`（空壳）。
- **复现**：§12 命令 10。
- **实际 vs 期望**：实际=actor 产座位级观察但无 strategy→actor 装配，Basic 策略对真实观察必抛异常；期望=P1.6 退出口径（迟到/过期拒绝、意图回放一致、无暗牌读取）在权威链内成立或显式声明阶段不支持自动 AI（口径由 caller 定）。
- **证据路径**：probes.log N 段；AiDecisionTests.cs:431-452（观察纯度+禁边已测）。
- **修复要求**（建议 G1 补齐子任务，尚未分配；依赖在途兄弟任务 T-A 的纯上下文与 N-1 候选面）：组合根把 actor 观察装配为策略可消费上下文并回投 `CommandIntent`。
- **验收断言**：同记录意图回放一致；迟到/过期结果拒绝；AI 无暗牌路径（既有 AiDecisionTests 断言保持全绿）。

### 7.2 统一负例矩阵（N-1/N-2 共用）

对每个新接线动作（Chi/Pon/Kan(claim)/Riichi/AgariYame/Tsumo/Ron/AddedKan + 三绝技三命令）：

| 负例类 | 断言 |
|---|---|
| 资格不满足 | 拒绝且零状态变更（无事件、版本不变） |
| 窗口关闭/过期 | 拒绝；决策窗外提交不入账 |
| 旧 controlEpoch/旧 roundId | 拒绝，不可复活/不可跨局生效 |
| 原子性（扣费/物理移动失败） | 回滚零副作用；出千类 entryId 同载荷重放返回原回执、异载荷冲突（P1.4 契约） |
| 结算错误注入（N-2） | 守恒/结果一致性验证拒绝错误 settlement（RoundStateMachine.cs:742-822 既有闸保持） |

### 7.3 INFO 挂账（非门槛缺陷）

| ID | 内容 | 证据 |
|---|---|---|
| I-1 | CI wrapper 不含 PG server-restart 探针（仅本地 verify-all.ps1）；G2/G3 前决定是否纳入（需评估一次性集群生命周期与安全） | §5.4 |
| I-2 | 历史 PR 39970580589 Client131/132 UNRESOLVED：失败框架日志缺失；当前绿不构成根因结论；不盲重试不称 flaky | §5.6 |
| I-3 | MTP 框架日志 UTF-16 无 per-test 明细；归因为组合证据；可选改进=wrapper 增 per-test 明文清单（caller 决定，非门） | §5.5 |
| I-4 | Domain 确定性 source-scan 守护测试（建议增强，非 G1 门条款；当前状态 PASS 以 P-D 探针为证） | §3.4 |
| I-5 | 依赖方向精确白名单增强（建议，非门）：补 TableRuntime 正向允许集、Wire✛Domain、LocalHost✛其余断言 | §3.3 |

## 8. 残余责任任务与依赖 DAG（按任务书正名）

```
N-1 域侧命令面扩展（P1.1/P1.3 延伸）→ N-1 actor 适配（P3.1 延伸）
        │                                    │
        ├──→ N-2 结算计算接线（P1.2 组件→权威链）  │
        │                                    │
T-A(在途) Ai纯上下文 ──→ N-4 AI 组合根 ←── N-1 候选面
                                 │
N-3 黄金对齐残余推进（manifest pending/partial 逐项）
                                 │
I-1/I-3 wrapper 探针与清单（可选，caller 决策）
                                 │
                          G1 关闭判定（caller）
                                 │
   ┌─────────────────────────────┼─────────────────────────────┐
   ▼                             ▼                             ▼
V5-2/G2（P4.1 LocalHost 组合根；  V5-3A 表事件 PG ITableStore 族   V5-3/G3（P5.x 远端）
依赖 P5.4a Client 接口基线）      （P3.4 明文后阶段，独立可先行）    → G4（P6.x）→ G5（P7.x）
```

- **G1 关闭 = 门 2（N-3 推进至可宣布的对齐口径）+ 门 5（N-1/N-2 完成 + 负例矩阵过）**；门 1/3/4/6/7/8/9 维持 PASS 即可（门 9 的 Unity/IL2CPP 子项按 P2.2 保持"阻塞分列"口径）。
- 阶段事实不构成 DAG 阻塞：LocalHost 组合根=P4.1（G2）；表事件 PG 存储=V5-3A（P3.4 明文）；两者不需为 G1 提前。
- N-1/N-2/N-4 均为"建议 G1 补齐子任务，**尚未分配**"——下发与否、粒度与 expectedFiles 由 caller 决定；两个在途 DS 任务（§10）不重复下发。

## 9. 可独立下发的文件范围建议（均非 G1 门槛项；本轮不实施）

| ID | 内容 | 文件范围 | 纳管方式 |
|---|---|---|---|
| S-1 | 依赖方向**精确白名单**增强（补 §3.3 未保护边，I-5 的落地形） | 新增 `dotnet/tests/Mahjong.Architecture.Tests/`（csproj + DependencyDirectionTests.cs）：对 8 个 src 程序集断言**正/负双向白名单**；断言目标只限 Mahjong 号段程序集与显式第三方（Wire→Google.Protobuf、Lobby→Npgsql 合法），**不拒绝 BCL 引用** | 置于 `dotnet/tests/` 即被 `tools/verify-v5.ps1` 自动发现（其头部注释 ：6-8：auto-discovers `*.Tests` under dotnet/tests|src）；可选同时登记 `dotnet/Mahjong.sln` |
| S-2 | Domain 确定性 source-scan 守护（I-4 的落地形） | 新增 `dotnet/tests/Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs`（不改现有测试）：禁用符号组=§3.4 regex；豁免表显式列 `PersistedUtcTime` 消费路径与 ShuffleConfig 数值调参并附理由；内嵌违例自检样本 | 同上自动发现 |
| S-3 | wrapper per-test 明文清单（I-3 的落地形，可选） | `tools/ci/verify-v5-postgres.ps1` 输出增强 | 属 CI 脚本改动，须 caller 批准 |

## 10. 在途兄弟任务（in-flight，未接受）

| 任务 | 固定头处事实（探针 P-M） | 状态 |
|---|---|---|
| Ai `AiCheatView`/`AiSeatSnapshot`/`AiDecisionContext` 纯不可变上下文 + 测试 | 全仓仅 `dotnet/src/Mahjong.Ai/AiCandidateModels.cs:21` 注释引用 V4 类名；无类型/测试落地 | **in-flight 未合入**；不读取未提交产物；不重复下发；N-4 依赖其接受 |
| Contracts `AccessCapabilities` 纯契约 + 测试 | 全仓 0 命中 | **in-flight 未合入**；同上 |

caller 本树复证日志（只读引用，SHA 由 caller 记录）：domain-in-tree-test.log `6C58EA84…AB642`、runtime-test.log `8F19FAA4…4D06`、contracts-test.log `C7EE456C…54AB0`、Mahjong.Ai.Tests-test.log `DC404918…B4425`、client-standard-test.log `9AF080E9…67192`。

## 11. REVIEW_INCOMPLETE 范围声明

以下事项本轮无法完全查明，明示范围：

1. **Unity/IL2CPP/macOS/Android/真机/跨物理网络构建**：无当前实测证据（环境缺口未重探查）；按 P2.2 口径以"缺口分列+不宣称通过"呈现（§3.9），不出任何当前 PASS。
2. **真实 PG 两进程重启写读的当前证据**：CI 不含该探针（§5.4）；本地历史记录为 P5.3 历史事实，本轮未重跑（不盲跑重型自测）。
3. **历史 PR 39970580589 根因**：失败框架日志缺失，无法补齐（I-2）。
4. **per-test 名级归因**：受 UTF-16 框架日志限制，采用组合归因（§5.5）。
5. **G-028a 之外 manifest 全部 partial 项的逐项深审**：本轮确认 20 partial 存在并单列（§3.2 L4/L5），未逐项复验每条 partial 的断言边界——逐项推进即 N-3 的执行体，属后续任务而非本轮可闭项。

除上述五项，本报告其余结论均有本轮可复现命令与可复算 SHA256 支撑。

## 12. 证据与命令附录（全部可复制，无占位符）

第 2 轮统一证据包（一次执行产出全部下列段）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/g1-review-r2-evidence/run-probes.ps1
# → probes.log，SHA256 3138e0b27e61e95a549018bc92ccf5ea360e4d33f154c69ead730bdba209c09a
```

分段命令（与 probes.log 段号对应，均只读）：

```
# 1. 头与范围（A 段）
git rev-parse HEAD; git status --porcelain; git diff --stat 8ee22ebf5164f74db75e5a33e87900f5b87cd8bd..HEAD; git diff --check HEAD^ HEAD
# 2. 依赖方向断言定位（B 段）
Select-String -Path dotnet\tests\Mahjong.Contracts.Tests\SchemaCompatibilityTests.cs -Pattern 'DomainDoesNotReference|ContractsDoesNotReference|WireReferencesContracts|EventSchemaFreezes'
Select-String -Path dotnet\tests\Mahjong.Domain.Tests\TilePositionMapTests.cs -Pattern 'DomainAssemblyDoesNotReference'
Select-String -Path dotnet\tests\Mahjong.Ai.Tests\AiDecisionTests.cs -Pattern 'Mahjong.Ai must not reference'
Select-String -Path dotnet\tests\Mahjong.Client.Tests\TableSessionTests.cs -Pattern 'ClientAssemblyDoesNotReference'
Select-String -Path dotnet\tests\Mahjong.LobbyService.Tests\ArchitectureAndPolicyTests.cs -Pattern 'LobbyAssemblyDoesNotReference'
# 3. Domain 确定性（C 段）
Get-ChildItem dotnet\src\Mahjong.Domain -Recurse -Filter *.cs | Select-String -Pattern 'new Random\(|DateTime\.Now|DateTime\.UtcNow|DateTimeOffset\.Now|DateTimeOffset\.UtcNow|Guid\.NewGuid|Stopwatch\.|Environment\.TickCount|Thread\.Sleep|Task\.Delay|Math\.Random'
# 4. 四枚举逐值（D 段）+ 域 dispatch（E 段）+ actor MapAction（F 段）
Select-String -Path dotnet\src\Mahjong.Domain\RoundTypes.cs -Pattern 'enum RoundActionKind' -Context 0,11
Select-String -Path dotnet\src\Mahjong.Contracts\ProtocolCommand.cs -Pattern 'enum ProtocolActionType' -Context 0,15
Select-String -Path dotnet\src\Mahjong.Contracts\ProtocolSnapshot.cs -Pattern 'enum ProtocolEventType' -Context 0,23
Select-String -Path dotnet\src\Mahjong.Domain\RoundTransition.cs -Pattern 'enum RoundEventType' -Context 0,17
Select-String -Path dotnet\src\Mahjong.Domain\RoundStateMachine.cs -Pattern 'Execute\(RoundCommand|case RoundActionKind\.'
Select-String -Path dotnet\src\Mahjong.TableRuntime\TableActor.cs -Pattern 'MapAction|RoundCommand\.(Pass|Discard)|UNSUPPORTED_ACTION'
# 5. Golden 边界（H 段）+ 136（I 段）+ spectator（J 段）
Get-Content tools\verify-v5-golden-catalog.ps1 -TotalCount 12
Select-String -Path tests\v5-golden\catalog-v1.json -Pattern '"status"|"notice"'
Select-String -Path doc\v5-重构\V5-0-GOLDEN-MANIFEST.md -Pattern 'G-028a'
Select-String -Path dotnet\tests\Mahjong.Domain.Tests\PhysicalGoldenTests.cs -Pattern 'oracle|structural|136'
Select-String -Path dotnet\tests\Mahjong.Domain.Tests\CheatGoldenTests.cs -Pattern 'IsAV4OnlyPath_RecordedAsStructure'
Select-String -Path dotnet\tests\Mahjong.Domain.Tests\RoundReplayGoldenTests.cs -Pattern 'RoundTerminalReplaysExactly|TsumoWin'
Select-String -Path dotnet\src\Mahjong.Domain\TilePositionMap.cs -Pattern 'TotalTileCount = 136|exactly 136|Conservation'
Select-String -Path dotnet\tests\Mahjong.Client.Tests\IndependentReviewTests.cs -Pattern 'SpectatorMustReject'
# 6. CI meta 文件名（K 段）
Get-ChildItem D:\majiang-wt\codex-progress-20261025\artifacts\progress-20261025\ci-push -Recurse -Filter dotnetTest.meta.txt
# 7. 计分接线（M 段）
Get-ChildItem dotnet\src -Recurse -Filter *.cs | Where-Object FullName -notmatch 'Rules' | Select-String -Pattern 'YakuEvaluator|WinningScoreEvaluator'
# 8. AI 组合根（N 段）
Get-ChildItem dotnet\src -Recurse -Filter *.cs | Where-Object FullName -notmatch 'Mahjong.Ai' | Select-String -Pattern 'IAiStrategy|AiDecisionService|new BasicAiContext'
# 9. validator 机械层复跑（O 段）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-v5-golden-catalog.ps1 -RepoRoot .
```

CI 产物 SHA256 复算命令：

```
sha256sum D:/majiang-wt/codex-progress-20261025/artifacts/progress-20261025/ci-push/…/summary.log   # 等 §5.2 表 10 项
```

冻结引用：依赖矩阵 `437bd53`；黄金目录 `205506f`（caller 第 13 轮验收集成于 PR19，见 `doc/v5-重构/` 验收文档与 `V5-0-GOLDEN-MANIFEST.md`）。复核协议（只读）：`D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`。

—— 审查代理（实现侧），2026-10-25，第 2 轮返修版。本报告不批准 G1。
