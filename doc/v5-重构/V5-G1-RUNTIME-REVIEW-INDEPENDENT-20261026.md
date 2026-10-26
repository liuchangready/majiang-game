# V5 G1 重审 — 运行时侧独立复审报告（八门：1/1b/3/4/5/6/7/8/9）

日期：2026-10-26
审查者：Claude Code（glm-5.3-flash），独立审查线程（G1 runtime 侧八门）
冻结基线：`08c4a75345484aadaf47b30f340992532f9c6ce2`（PR19 已发布头）
工作树：`D:\majiang-wt\g1-runtime-review-glm-20261026`（分支 `wt/v5-g1-runtime-review-glm-20261026`，独占）
协议：`V5-REVIEW-REWORK-PROTOCOL.md`（一次性全量缺陷清单；不挤牙膏；不放松验证器）

**范围声明**：本报告只覆盖 G1 九门中的 **门 1（本地 PG gate + CI 双 run 审计）、门 3、4、5、6、7、8、9** 共八门。**门 2（黄金目录/分类）属并行 Anti 独立任务，本报告标记 NOT_RUN（非本任务失败，不等待、不重做其普查/编目工作）**。本报告仅为 caller 判定 G1 的输入之一，**不构成对 G1 的批准**；V4 仍是唯一生产基线；G5 未完成；未创建 complete.flag。

---

## 0. 结论速览

| # | 门条件（任务书 :108） | 本报告判定 | 依据一句话 |
|---|---|---|---|
| 1 | `dotnet test` 独立全绿 | **PASS** | 本树未修改 wrapper 全程实测 exit 0（§1）；CI 双 run 1,808/0/0 逐文件亲验（§2） |
| 2 | 已导出黄金样本对齐率 100%，未导出单列 | **NOT_RUN（本任务）** | 属并行 Anti 门 2 任务，本线程不重做 |
| 3 | 依赖方向静态测试通过 | **PASS** | 8 处静态断言锚点逐个核实 + 新鲜构建定向探针 13+59+7 全绿（§3） |
| 4 | Domain 不含隐式随机/时钟/浮点权威 | **PASS** | 严格 grep **0 命中** + Domain 零包引用（§4） |
| 5 | 三绝技与 136 守恒通过 | **PASS** | N-1a/N-1b/N-2 三组件源码接线逐锚点核实 + 85 个权威测试 + §7.2 负例矩阵逐类映射（§5） |
| 6 | Wire 可重复生成 | **PASS** | SchemaCompatibility(13)+ProjectionWireMapper(16) 新鲜构建探针全绿，CI 149 全绿（§6） |
| 7 | 按座位/观战私有性通过 | **PASS** | VisibilityGolden 8 + 观战命令前置拒绝 5 探针全绿 + PrivateTypes 六类冻结（§7） |
| 8 | TableRuntime 并发、版本与恢复合同通过 | **PASS** | checkpoint 3 探针 + CI 397 全绿；回执重放/冲突、双栅栏、恢复不重复结算有具名测试（§8） |
| 9 | Unity/IL2CPP 可用平台的构建日志和缺口分列 | **PASS（按任务书原口径/P2.2 出口）** | 可用平台（Windows .NET）有原始构建日志 ×3；缺口按 §5.5/§8 分列含具体资源清单；在途 P22 不计入（§9） |

**计数核对：PASS=8；FAIL=0；BLOCKED=0；NOT_RUN=1（门 2，并行任务范围）；合计 9 = 行数。**

**本审查新发现缺陷 1 条（MINOR，见 §12 D-1-01）**；历史 INFO 挂账 I-1/I-2/I-3/I-4/I-5 原样沿用（§12.2），其中 I-2（历史 run 39970580589）保持 **UNRESOLVED**。

---

## 1. 门 1（A）：本树未修改 wrapper 全程实测 — PASS

### 1.1 执行身份与命令

- wrapper：`tools/ci/verify-v5-postgres.ps1` **未修改**，SHA-256 `38F7E1CDC887196F0198C17349809A379F6F901A2CB407CAA1DE3E1F5A7999BA`。
- 端口预检（本进程独立探测）：55463 空闲；55461/55465/55466/55467 亦空闲（probe 脚本在 `D:\mj-g1rt-20261026\port-probe.ps1`，不在仓库内）。55463 不在保留段 5432/5433/55450-52。
- 实际执行命令（原样）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File 'D:\majiang-wt\g1-runtime-review-glm-20261026\tools\ci\verify-v5-postgres.ps1' -Configuration Release -RunnerMode dotnetTest -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 55463 -Root D:/mj-g1rt-20261026/TEMP/mahjong-p53-pg-ci-g1rt-a1 -LogDir D:/mj-g1rt-20261026/logs/v5pg-g1rt-a1
```

- 未使用任何自测开关（VerifyScript/NoProjectDiscovery/SkipPluginCopyCheck 均未传）；无跳过组件。
- TEMP/TMP 仅本进程指向 D 盘独占根 `D:\mj-g1rt-20261026\TEMP`；LogDir 全新目录。

### 1.2 结果（原生数值退出码与逐阶段）

控制台：`D:\mj-g1rt-20261026\logs\gate1-console.log`（SHA-256 `208BD9B05147D593FC0E1BC10900D4AF548B95836A2F73E08F34CF50C66F0351`）。后台任务输出文件逐字记录 **`GATE1-EXIT=0`**。

| 阶段 | 结果 | 关键证据 |
|---|---|---|
| precheck | PASS (exit 0) | port 127.0.0.1:55463 free |
| start-postgres | PASS (exit 0) | 官方 EDB zip **本次全新下载** 322,530,154 B 至本次独占 `…\download\`；pinned SHA `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C` 校验在 start-postgres.ps1 内强制执行；`server.identity` 事件 + 只读 data_directory 校验指向 `D:/mj-g1rt-20261026/TEMP/mahjong-p53-pg-ci-g1rt-a1/data`（verified） |
| ownership-marker | PASS (exit 0) | `pg-owner.json`（ownership=owned） |
| environment | PASS (exit 0) | 4 变量（MAHJONG_LOBBY_TEST_POSTGRES / PG_ROOT / PG_DATA / PG_EVIDENCE）注入子进程 |
| migrate | PASS (exit 0) | `001_lobby.sql` **applied 2 time(s)，每次 exit 0**；`migration.repeated` 事件在案 |
| verify | PASS (exit 0) | 统一验证器 `tools/verify-v5.ps1`（SHA `FA8D539B…307`，与 run-info 记录一致）退出码逐字传播 |
| archive-cluster-evidence | PASS (exit 0) | 4 file(s) |
| cleanup | PASS (exit 0) | **rootDeleted=True**；事后 `Test-Path` 实测 False |
| env-restore | PASS (exit 0) | fresh-child block view 证实 **4× restored-absent**（provider-set-item-null） |
| write-evidence | PASS (exit 0) | `logs\v5pg-g1rt-a1\pg`（12/12 manifest 全对，0 缺失） |

`failedStages` 为空；overall exit **0**。

### 1.3 测试与构建数字（真实计数，逐 meta 亲读）

`summary.log` 9 行项目全 PASS：**7 测试项目 1,808 通过 / 0 失败 / 0 跳过**：

| 项目 | total | failed | skipped | exit |
|---|---|---|---|---|
| Mahjong.Ai.Tests | 444 | 0 | 0 | 0 |
| Mahjong.Client.Tests | 132 | 0 | 0 | 0 |
| Mahjong.Client.Wss.Tests | 80 | 0 | 0 | 0 |
| Mahjong.Contracts.Tests | 149 | 0 | 0 | 0 |
| Mahjong.Domain.Tests | 517 | 0 | 0 | 0 |
| Mahjong.LobbyService.Tests | 89 | 0 | 0 | 0 |
| Mahjong.TableRuntime.Tests | 397 | 0 | 0 | 0 |
| 合计 | **1,808** | **0** | **0** | — |

- 2 个 build-only helper（LobbyService.PostgresProbe、Outbox.CrashWorker）仅验证构建，不计用例数。
- solution build PASS（exit 0）；**12-DLL 插件闭包**实点 =12（Google.Protobuf、Ai、Client、Contracts、Contracts.Wire、Domain、LocalHost、TableRuntime、System.Buffers、System.Memory、System.Numerics.Vectors、System.Runtime.CompilerServices.Unsafe），其中 **Tests DLL=0**。
- 每项目 meta（`dotnetTest.meta.txt`）7/7 exit 0，框架日志 7/7 SHA 复核 **MATCH**（Ai `6473E27F…`、Client `BC7C7548…`、Wss `587D6876…`、Contracts `6E0F729A…`、Domain `FB1DB0B1…`、Lobby `DC08973C…`、TableRuntime `5B09DA7C…`）。
- **口径声明**：本次 PG 证据（`postgres-evidence.jsonl`：server.identity、migration.repeated、concurrency.roomFill/crossRoomJoin、receipt.replay/terminalReplay、terminal.projection 等）全部属 **Lobby 存储层 + 迁移 + PG 真库启动证据**；P5.2 事件存储未实现，本报告**不以**该证据宣称 P5.2 通过。
- 运行后 `git status --porcelain` 为空、`git diff --check` 干净——wrapper 未在仓库留下任何改动（bin/obj 为 ignored 产物）。

**门 1(A) 判定：PASS。** 失败后未盲目重跑（一次通过）。

## 2. 门 1（B）：GitHub CI 双 run 产物独立审计 — PASS

caller 指定 runs `40266679969` 与 `40266677478`；本线程以 gh 只读下载到自有根 `D:\mj-g1rt-20261026\ci\`（各 62 文件），逐文件亲验（脚本与日志：`ci-audit.ps1`/`ci-audit.log` SHA `6242D5F4…78FC`；`fwlog-check.ps1`/`fwlog-check.log` SHA `D35F1451…68B5`）。未触发、未重跑任何 CI。

| 项 | run 40266679969 | run 40266677478 |
|---|---|---|
| 事件 | PR event（checkout=merge ref） | push event |
| 采样头 | `9bfa01b014e34e70c58235f2b2817a9b75e8664a`（= 08c4a75 与 v5 tip 457274c 的合并提交，双亲已核实） | `08c4a75345484aadaf47b30f340992532f9c6ce2`（**冻结头精确一致**） |
| 汇总 | OVERALL PASS，9 行项目 9 PASS / 0 FAIL / 0 BLOCKED / 0 NOT_RUN | 同左 |
| 用例 | **1,808 / 0 / 0**（与 §1.3 同分布） | **1,808 / 0 / 0** |
| meta/框架日志 | 7 项目 meta exit 0；框架日志 14/14（两 run 合计）SHA **MATCH** | 同左 |
| wrapper 10 阶段 | 全 PASS；PG16.10 server.identity；rootDeleted=True；env 4× restored-absent | 同左 |
| manifest | 12/12 ok，1 uncovered=`evidence-sha256.txt` 自身（自排除，预期） | 同左 |
| ready-lines | =1（CI wrapper 无 PG 重启探针 → 已知 I-1，非新缺陷） | 同左 |
| 关键文件 SHA | summary.log `48AA9B70…FC98B`；run-info `921F8E88…A552`；wrapper-summary `08AA21E2…6636`；pg-evidence `4D468245…BB782` | summary.log `A974AD38…0126`；run-info `6BEEE17B…D451`；wrapper-summary `A5D998EB…7719`；pg-evidence `CE42EE3A…3B55C` |

- **口径**：PR run 的采样头是 merge ref，本报告如实记录为"08c4a75 已被合入 v5 tip 的合并提交上验证"，不冒充"08c4a75 本身"；push run 才是逐字节的 08c4a75。
- **历史 run 39970580589 维持 UNRESOLVED**（失败框架日志缺失；本双绿不构成对其根因结论，不称 flaky，不盲重试——沿用 I-2）。
- 本地 1,808 与 CI 1,808 用例分布一致（Ai 444/Client 132/Wss 80/Contracts 149/Domain 517/Lobby 89/TableRuntime 397），与历史 1,356（8ee22eb 时）之差由 N-1a/N-1b/N-2/N-3 新增测试解释。

**门 1(B) 判定：PASS。**

## 3. 门 3 依赖方向静态测试 — PASS

固定头 8 处静态断言锚点逐个核实存在（与上轮 V5-1-REVIEW §3.3 清单一致，行号未漂移）：

1. `SchemaCompatibilityTests.cs:376` Domain ✗ Contracts/Wire/Protobuf/UnityEngine/System.Data
2. `:389` Contracts ✗ Wire/Protobuf
3. `:399` Wire ✓ Contracts + Protobuf（正向）
4. `:409` 协议事件面冻结 19+2
5. `TilePositionMapTests.cs:1049` Domain ✗ Contracts/Client/Server/TableRuntime/LocalHost/UnityEngine/UnityEditor/System.Net/System.Data/Microsoft.Data
6. `AiDecisionTests.cs:437-452` Ai ✗ TableRuntime/Contracts/LocalHost/UnityEngine（含暗牌观察属性扫描）
7. `TableSessionTests.cs:749` Client ✗ Domain/TableRuntime/LocalHost；✓ Contracts+Wire
8. `ArchitectureAndPolicyTests.cs:13` Lobby ✗ Domain/TableRuntime

新鲜构建定向探针（§13 P 组）：SchemaCompatibilityTests **13/13**、TilePositionMapTests **59/59**（49 个 Fact/Theory 属性，Theory InlineData 展开后实跑 59）、ArchitectureAndPolicyTests **7/7**，exit 全 0，XML 计数确认。CI 1,808 内同批全绿。

**残余（沿用 I-5，建议非门）**：TableRuntime 正向白名单、Wire ✗ Domain、LocalHost ✗ 其余未显式断言。**判定 PASS**（门条款是"静态测试通过"，现有断言覆盖核心禁边且全绿；不把白名单精确化升格为门条件）。

## 4. 门 4 Domain 无隐式随机/时钟/浮点权威 — PASS

- 严格 grep（`dotnet/src/Mahjong.Domain` 全部 .cs）：`new Random(|DateTime.Now|DateTime.UtcNow|DateTimeOffset.Now|DateTimeOffset.UtcNow|Guid.NewGuid|Stopwatch.|Environment.TickCount|Thread.Sleep|Task.Delay|Math.Random` → **PURITY-HITS=0**（本次复审实测，日志 `domain-purity-grep.log` SHA `28015404…3AA7D`）。
- `Mahjong.Domain.csproj` PackageReference 数 = **0**（netstandard2.1 / C# 9，无任何包依赖）。
- 沿用上轮口径：确定性由"源代码事实 + 全量测试"保证；source-scan 守护测试仍是建议（I-4，非门条款）。**判定 PASS。**

## 5. 门 5 三绝技与 136 守恒（N-1a/N-1b/N-2 宿主实 closure + §7.2 负例矩阵）— PASS

上轮门 5 FAIL 的三组件在本头全部具备**源码接线 + 真实测试 + 已接受组件级验收记录**三重证据，本线程逐锚点重验源码（非转抄）：

### 5.1 N-1a（域侧决策动作）
- `RoundTypes.cs:31-47`：RoundActionKind 实有 **14 值**（含 DeclareChi=9/DeclarePon=10/DeclareOpenKan=11/DeclareClosedKan=12/DeclareRiichi=13）；AgariYame 由 MatchSession.ChooseDealerEnd 承接（已接受口径）。
- `RoundDecisionTests.cs`（Domain.Tests）**24 Fact**：资格类（ChiRejectsNonShimocha…、RiichiRejectsInsufficientPointsNonTenpaiAndOpenHands、InvalidPhysicalTilesAndMeldShapesNeverRecordAClaim、ClosedKanAfterRiichiRejectsAStructuralChange）、窗口类（DuplicateResponsesAndExpiredWindowsRejectWithoutMutation、LowPriorityFirstClaimCannotStealAnUnclosedWindow、ChiAndPonAreUnavailableInAChankanWindow、RonOutranksPon…、NewDecisionCandidatesRejectStaleForeignAndTerminalInstall）、原子性（PrepareFailureLeavesNoEventVersionOrTileChange、AssertRejectedWithoutMutation）、恢复（RecoveryRoundTrips…×4、CommandJournalRoundTripsN1aCommandsAndRejectsMissingMeldTiles）。
- 玩家 envelope 通道 `TableActor.cs:175→:1039→:1151` 直达域执行（上轮已正名的真实入口）。

### 5.2 N-1b（出千执行层接线）
- `TableActor.cs:257` `SubmitCheatAsync`：7 出千命令走**同一权威 mailbox**（与玩家命令同队列串行），跨 BootId offer journal、cheat fingerprint 绑定、旧 schema fail-closed 均在源码核实。
- `N1bCheatExecutionTests.cs` **40 测试**，§7.2 五类负例逐类有具名覆盖：资格（tile_mark_from_the_wrong_seat…zero_side_effects、frozen_missing_wall_candidate…、inner_and_outer_request_ids_must_match…）、窗口/期限（pending_decision_and_authoritative_deadline_bind_cheat_commands、expired_off_turn_choice…reports_rejection、authoritative_off_turn_sweep…late_manual_choice_is_rejected）、旧 epoch/旧代（stale_owner_control_version_round_and_recovery_generation…、old_owner_epoch_is_rejected_after_recovery、durable_recovery_fence_rejects_a_delayed_cheat_command_from_the_old_generation）、原子性（commit_failure_rolls_back_every_cheat_side_effect、sleeve_activation_commit_failure_publishes_nothing…、durable_commit_fault_leaves_no_cheat_state_version_or_charge）、回执幂等（same_request_id_with_a_different_payload_digest_is_a_conflict、business_rejection_replays_after_turn_changes…）；**136 守恒**（beam_swap_swap_moves_exactly_the_frozen_hand_and_wall_entities_and_keeps_136_unique）；恢复（durable_checkpoint_and_journal_cold_recovery…、cross_boot_offer 系列 ×5）。

### 5.3 N-2（结算权威链）
- `TableActor.cs:289` `SubmitSettlementAsync`；`TableSettlementCommands.cs`（typed intent 不携带 han/fu/payments；9 个 reason code；`ITableSettlementFactsPort` :346）。
- 权威计算：`TableActor.cs:1777-1786` 调 `RoundSettlementCalculator.CalculateTsumo/Ron/ExhaustiveDraw/AbortiveDraw`（Domain/Rules），链至 `WinningScoreEvaluator.Explain`（`RoundSettlementCalculator.cs:379`）——结算分值出自冻结域计算器，不信调用方。
- 旧/raw settlement 收口（**authority-mode 边界，双入口**）：玩家路径 `:1177-1188`（`IsSettlementAction && port!=null → RawEntryRejected`）、AI/超时路径 `:3259-3271` 同栅栏；port 注入时 raw envelope 在读取任何调用方分数/番/符/支付前被拒。**port 未注入时 raw envelope 落回既有 legacy 域执行——这是有意保留的 legacy 模式（port 为可选构造注入 :54/:191），且当前生产代码无任何 port 设置点（唯一注入者为测试 N2HostSettlementTests），即当前真实默认姿态是 fail-closed（typed 提交遇无 port → `:1670-1676` AuthorityUnavailable 拒绝）。** 该边界语义已如实记录；P4.1/P5.1 组合根未开工是阶段事实（SF-1），**不作为本头 G1 新阻断**（任务书门文本无宿主组合根条款）。
- `N2HostSettlementTests.cs` **21 测试**：RawSettlementEnvelopeIsRefused…、StaleOrForeignSourceFacts…、OldRoundVersionOwnerControlAndDecision…、ForgedHostFactsAreRejectedByTheFrozenCalculator（结算错误注入类）、TerminalRoundRejectsASecondSettlement…、CommitFailureDoesNotConsumeTheDecisionClockOrState、DurableSettlementReplaysIdenticallyAndConflictsOnDifferentPayload、RecoveryDoesNotRepeatTheSettlementPaymentOrOutbox、TrustedFuritenFactRejectsRonAndFailingPortFailsClosed、TypedTsumo 绑定/原子结算 sleeve/beam offer 等。

### 5.4 §7.2 统一负例矩阵映射（对真实新增入口，自动阶段权限保留）

| 负例类 | N-1a | N-1b | N-2 |
|---|---|---|---|
| 资格不满足（零状态变更） | RoundDecisionTests 资格组 | N1b wrong_seat / missing_wall_candidate / request_ids | TypedTsumo…RejectsForeignSeat、MissingSeatHostFactsFailClosed |
| 窗口关闭/过期 | DuplicateResponses…、ChankanWindow | deadline_bind / expired_off_turn / late_manual | RonUsesTheOpenResponseWindowAndRejectsAClaimOutsideIt |
| 旧 controlEpoch/roundId | NewDecisionCandidatesRejectStaleForeignAndTerminalInstall | stale_owner_control…、old_owner_epoch…、recovery_fence… | OldRoundVersionOwnerControlAndDecision…、StaleOrForeignSourceFacts… |
| 原子性/回执幂等 | PrepareFailure…、AssertRejectedWithoutMutation | commit_failure_rolls_back…、same_request_id…conflict、business_rejection_replays… | CommitFailureDoesNotConsume…、DurableSettlementReplaysIdenticallyAndConflicts… |
| 结算错误注入 | —（不适用） | —（不适用） | ForgedHostFactsAreRejectedByTheFrozenCalculator、TrustedFuritenFactRejectsRonAndFailingPortFailsClosed、TerminalRoundRejectsASecondSettlement |

矩阵五类 × 适用入口**全部有具名真实测试**，且全部包含在本头 Gate 1 实测 1,808/0/0 与 CI 双 run 1,808/0/0 内（TableRuntime.Tests 397、Domain.Tests 517 计数涵盖）。

**判定 PASS。** N-1c 为可选扩展未实施（明确可选，非缺陷）；发现的唯一新缺口是 AuthorityUnavailable 分支无专测（MINOR，§12 D-1-01，不影响本门判定——该分支方向为拒绝/fail-closed）。

## 6. 门 6 Wire 可重复生成 — PASS

- 事件面冻结断言 `SchemaCompatibilityTests.cs:409`（19+2 私有事件）在新构建探针中实跑通过（13/13 含 4 处依赖方向断言）。
- `ProjectionWireMapperTests`（Contracts.Tests）**16/16** 探针全绿；Contracts.Tests 全量 149 在本地 Gate 1 与 CI 双 run 均 0/0/0 全绿。
- 上轮 §3.6 的稳定字节/字段号/错误码 Fact 群仍在本头且全绿（包含于上述计数）。**判定 PASS。**

## 7. 门 7 按座位/观战私有性 — PASS

- `VisibilityGoldenTests`（TableRuntime.Tests）**8/8** 探针全绿（owner stream typed cheat events、spectator/其他座位不可见——含 N1b 的 owner_stream_receives_typed_cheat_events_while_spectator_and_other_seat_do_not）。
- `IndependentReviewTests`（Client.Tests，:66 观战命令前置拒绝）**5/5** 探针全绿。
- 私有事件面：`ProtocolSnapshot.cs:69-77` PrivateTypes 恰 6 值（TileDealt/TileDrawn/RinshanDraw/CheatPointsChanged/SleeveBloomResolved/BeamSwapResolved），事件冻结断言 :409 实跑通过。**判定 PASS。**

## 8. 门 8 TableRuntime 并发、版本与恢复合同 — PASS

- `RoundDecisionCheckpointTests`（TableRuntime.Tests）**3/3** 探针全绿。
- 回执/双栅栏/恢复具名测试（本头亲验存在并含于 397 全绿）：DurableSettlementReplaysIdenticallyAndConflictsOnDifferentPayload、RecoveryDoesNotRepeatTheSettlementPaymentOrOutbox、durable_checkpoint_and_journal_cold_recovery_restore_full_cheat_state_and_replay_the_same_receipt、durable_recovery_fence_rejects_a_delayed_cheat_command_from_the_old_generation、old_owner_epoch_is_rejected_after_recovery_and_concurrent_cheats_commit_at_most_once、cold_recovery_capacity_shrink_still_replays…、durable_cheat_binding_is_persisted_and_tamper_recovery_is_refused、stale_owner_control_version_round_and_recovery_generation_are_rejected…（ownerEpoch+stateVersion 双栅栏语义）。
- 并发类（receipt/concurrency 事件族）在本地 PG gate 与 CI 的 postgres-evidence.jsonl 中有真库并发/回执事件佐证。**判定 PASS**（I-1 无 PG 重启探针为已知残余）。

## 9. 门 9 Unity/IL2CPP 可用平台构建日志与缺口分列 — PASS（按任务书原口径/P2.2 出口）

**判据原文**（任务书 :108）："Unity/IL2CPP 可用平台的构建日志和缺口分列"。判读沿用上轮已成立口径：可用平台要有原始构建日志；不可用平台的缺口要分列并给出具体资源清单。

### 9.1 本冻结头已接受的平台证据（只计此处的）

1. **可用平台（Windows x64 .NET 8）原始构建日志充足 ×3**：① 本报告 §1 本树 wrapper 全程（sln-build PASS + 1,808 测试 + 12-DLL 插件闭包，全部原始日志+SHA 在案）；② CI run 40266677478（checkout=08c4a75 精确）；③ CI run 40266679969（merge ref 含 08c4a75）。
2. **缺口分列与具体资源清单**（本头已接受文档）：`V5-1-AOT-REVIEW.md` §5.1 工具链盘点（Unity 6000.3.27f1 + il2cpp.exe + Windows IL2CPP 模块 + Android SDK/adb/JDK 均在；NDK/MSVC/Windows SDK 当时缺）、§5.5 **需要补齐的资源表**（Editor 许可；Windows IL2CPP=VS C++ 工具+Windows SDK+许可；Android=NDK(匹配 Unity 6.3)+许可+设备；iOS=macOS+Xcode+设备/签名）、§5.2-5.4 各平台 BLOCKED/NOT_RUN 原始命令与退出码（Windows exit 2 缺 MSVC/SDK；Android exit 2 缺 NDK；iOS 无 macOS）、§8 状态表、§9 五条 integration gap、§10 六条"不得宣称"。`UNITY-ENVIRONMENT.md`（2026-10-22）与 `V5-0-MEASUREMENTS.md`（P0.3）为补充环境/实测口径。
3. **本报告不照抄 2026-10-22 的"没有 SDK/未激活"笼统描述**：以本头已接受的 `V5-1-AOT-REVIEW.md` §5.1 盘点为准（Android SDK 在位；NDK/MSVC/WinSDK 当时缺；许可以 batchmode seat 校验为唯一证明）。

### 9.2 在途 P22（明确不计入本基线，仅区别性记录）

以下内容**均不是 HEAD 祖先**（`git merge-base --is-ancestor` rc=1），属未接受的在途平台复验，本报告不将其作为本头证据，也未审查/合入：

- `wt/v5-p22-il2cpp` 线（101cdd7→25c006d→c98f52a）：§11 追补声称宿主补齐 MSVC v143 14.44.35207 + Windows SDK 10.0.26100 + NDK r27c + Unity Personal 许可激活，**Windows IL2CPP build+runtime 实测 PASS**（player exit=0）；c98f52a 记录 Android 四次尝试、JDK-17/CMake-3.22.1/cmdline-tools-16.0 补救与 **Unity→sdkmanager 0xC0000409 崩溃未解（Android 仍 FAIL/NOT_RUN，无 APK）**。→ 这是"历史 Windows IL2CPP build+player 实测"与"Android 残余"的所在，**待审**。
- `5c230e4`（platform AOT recheck：503 行 recheck 报告 + 1312 行探针脚本）**不在任何本地分支上**（parent=00c71cd）；其 cherry-pick/重放形态 `85efe52` 在 `wt/v5-p22-platform-recheck-20261026`。→ "当前 P22 待审平台复验"本体，**未交付/未接受，不构成本基线 PASS**。

### 9.3 明确残余清单（照录，不因在途分支而提前销账）

Unity 许可激活（本头接受证据=BLOCKED/exit 198，在途分支声称已激活待审）；Windows IL2CPP（本头 BLOCKED）；Android CLI 0xC0000409/Ninja（在途 c98f52a 记录，未解）；Android 真机/模拟器；iOS/macOS/Xcode；跨物理网络 T4 未测；TLS/WSS NOT_RUN（P2.2 不含传输实现端点）；AOT 裁剪实测（link.xml 解析≠裁剪通过）。

**判定 PASS（按任务书原口径）**：可用平台有原始构建日志 + 缺口已按 P2.2 出口分列含资源清单；Unity/IL2CPP 子项状态如实标 **BLOCKED**，无通过宣称，在途 P22 未计入。

## 10. 门 2 — NOT_RUN（本任务）

门 2（黄金目录/分类）由 caller 授予并行 Anti 任务执行（含 N-3 后续口径）。本线程**不重做其普查/编目、不等待其结论**；对 N-3 已收口事实（台账 592=411 bound+181 classified+0 pending、冻结 census 1516 路径）本报告不做第二次数数，仅如实引用既有已接受记录。此 NOT_RUN 非本任务失败。

## 11. 九门汇总

见 §0 表。我负责的八门 **8 PASS / 0 FAIL / 0 BLOCKED**；门 2 NOT_RUN（并行任务）。G1 是否通过的最终判定权在 caller；本报告不自行批准 G1，不 push、不建 PR、不触发 CI、不合 v5。

## 12. 缺陷/挂账清单（一次性全量；同类已合并）

### 12.1 本审查新发现

| ID | 严重级别 | 位置 | 复现 | 实际 vs 期望 | 证据 | 修复要求 | 验收断言 |
|---|---|---|---|---|---|---|---|
| D-1-01 | MINOR（测试覆盖缺口，非行为缺陷） | `dotnet/src/Mahjong.TableRuntime/TableActor.cs:1670-1676`（分支本体）；`dotnet/tests/Mahjong.TableRuntime.Tests/N2HostSettlementTests.cs`（缺负例） | `grep -rn "AuthorityUnavailable" dotnet/tests/` → 0 命中；N2HostSettlementTests 21 测试全部经 `SetFactsPort` 注入 port | 实际：`SubmitSettlementAsync` 在**未注入** `ITableSettlementFactsPort` 时的 fail-closed 拒绝分支（ReasonCode=AuthorityUnavailable，零状态变更）无任何行为测试；期望：该分支有专测 | 本报告 §5.3 源码锚点 + grep 输出（`CODEX-V5-N2-ACCEPTANCE.md` 亦未声称覆盖该分支，无验收声明冲突） | 新增一条负例测试：不注入 port 直接 `SubmitSettlementAsync`，断言 Rejected(AuthorityUnavailable) 且零事件/版本不变/无结算副作用 | 测试在 `dotnet test` 全绿；XML/框架日志计数含该用例；不修改生产代码 |

除 D-1-01 外，**我负责的八门范围内未发现其他 FAIL/BLOCKED 级缺陷**。门 5 判定不受 D-1-01 影响（分支方向为拒绝，fail-closed，不可能产生错误放行）。

### 12.2 沿用挂账（非本审查新发现，原样维持）

| ID | 内容 | 状态 |
|---|---|---|
| I-1 | CI/本地 wrapper 无 PG server-restart 探针（本次 ready-lines=1 再次确认） | INFO，维持 |
| I-2 | 历史 run 39970580589（Client 131/132 失败）框架日志缺失 | **UNRESOLVED**；本双绿不构成根因结论，不称 flaky |
| I-3 | MTP 框架日志 UTF-16 无 per-test 明细 | INFO，维持（组合证据口径） |
| I-4 | Domain 确定性 source-scan 守护测试 | 建议，非门条款 |
| I-5 | 依赖方向精确白名单增强 | 建议，非门条款 |
| SF-1 | AI 组合根/宿主组合根属 P4.1/P5.1（G2/V5-3 阶段产物） | 阶段事实，非 G1 阻断（任务书门文本无此条款） |

## 13. 证据与 SHA256 清单

**仓库内（本次唯一新增文件=本报告；其余零改动）**

- `git rev-parse HEAD` = `08c4a75345484aadaf47b30f340992532f9c6ce2`；branch `wt/v5-g1-runtime-review-glm-20261026`；提交前 `git status --porcelain` 空、`git diff --check` 干净。
- `tools/ci/verify-v5-postgres.ps1` SHA `38F7E1CDC887196F0198C17349809A379F6F901A2CB407CAA1DE3E1F5A7999BA`（未修改）；`tools/verify-v5.ps1` SHA `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`（与本地 run-info 记录一致）。

**本线程自有根 `D:\mj-g1rt-20261026\`（不在仓库内）**

| 文件 | SHA-256 |
|---|---|
| logs/gate1-console.log | `208BD9B05147D593FC0E1BC10900D4AF548B95836A2F73E08F34CF50C66F0351` |
| logs/gate1-verify.log | `83DD9D7EB7F323C4C8AF89D3EBCBF084717781D5EA8AEE2763693D3D4B129A44` |
| logs/v5pg-g1rt-a1/summary.log | `2F5CB24A4B3F2D8C906B09545939424FBF678F35BACCB2B4C48802146FAB83E6` |
| logs/v5pg-g1rt-a1/run-info.txt | `F06728D6530C5198D957F4F2C7D946C516C8330AB1578066A91C874C9F6BFDAA` |
| logs/v5pg-g1rt-a1/pg/wrapper-summary.json | `D147F1CA4F0DDF07DCFE47C29F28EEA771D067CEA168FD2E281599037ADB4CB4` |
| logs/v5pg-g1rt-a1/pg/postgres-evidence.jsonl | `C835773065277A0F38FB2EE86DCF2BFBC4C1F277642EAB00CD8204423F8B2C11` |
| logs/v5pg-g1rt-a1/pg/env-restore.jsonl | `DDECA8ED65A73429198593EBD2CF3CC2CBA1BB628A3D39B82A219DD9EF1914FE` |
| logs/ci-audit.log | `6242D5F4A9DC7C93FF9926A80DDCAD44704C8F8E06C1E8F26D7E414814D078FC` |
| logs/fwlog-check.log | `D35F1451961B5E52A84650FF7B2D6485FD1C73F9221DEA4A37184FEA0C1768B5` |
| ci/run-40266679969/summary.log | `48AA9B7064C3D25608EA099B648869D4AD11212C389B06BDBB94B6E81D8FC98B` |
| ci/run-40266679969/pg/wrapper-summary.json | `08AA21E27BCD4EC06BBA351306DAE8AE86D71FD1A87933F9AEFCA3C91ECC6636` |
| ci/run-40266677478/summary.log | `A974AD3809BAAB25820354B65793B8CD8EB9B76B09978EC011BC4DE320360126` |
| ci/run-40266677478/pg/wrapper-summary.json | `A5D998EB5D6FB061CCA6C683FCDA31F081502FE26A3ACACACF4BE2D226F27719` |
| probes/probes-console.log | `94070181953C4D8B45A50542921B194D85B099520B50C185E1EFCAA177EB4B2C` |
| probes/schema-compat.xml | `F8324D1CD3409D9446A0682E8D62EE4917CF48C04648F60D625942717ECE2E60` |
| probes/wire-projection.xml | `DC434DEC8758C623258B4EB1A261C9795F33B65ED518194AFB61BC4AC45BB0A0` |
| probes/domain-forbidden-edges.xml | `D7C35A1CC1BB5A4D52A0C8E860031D622D6DE7484980DD6DE47D196C591DCE09` |
| probes/lobby-arch.xml | `2EEEA0354915333D848CB4926CFD17179DA2F8513262F08E4A737BABE0D95A07` |
| probes/client-spectator.xml | `FF584D2796570C5068D21E1E1ACD11AB013C9DF9190A840C042BA7DEAE74115B` |
| probes/visibility-golden.xml | `FBFE8C195D3B2BE63B58817ED6F152CCD25AD467B4586A7F44AADE9C51593A73` |
| probes/checkpoint.xml | `6B1AB38224F796C7719FA3CAEB1E192A2F067158161395E9D62B94327655BAEB` |
| probes/domain-purity-grep.log | `28015404EF266FDA301CD66DE4C92398906CDA19A173AD0CF841A1D48F3EAA7D` |

探针运行器与调用形态：xUnit v3 直接运行器（`dotnet.exe <testdll> -noColor -noLogo -class <filter> -result-xml <path>`），7/7 原生数值 exit 0，XML `total/passed/failed/skipped` 计数确认（13/16/59/7/5/8/3，全部 0 失败 0 跳过）；运行对象为 §1 本地 Gate 1 run 产出的 **Release 新鲜构建**（先构建后运行，未运行陈旧 DLL）。

## 14. 每门未覆盖边界（如实单列）

- **门 1**：无 PG server-restart 探针（I-1）；两个 build-only helper 不产用例；PG 证据=Lobby 存储层口径，P5.2 事件存储未实现未测（本报告未宣称）；MTP 日志无 per-test 明细（I-3）。
- **门 3**：非完整白名单（I-5 三条建议边）；BCL 层面仅 Domain ✗ System.Net/System.Data 等已断言，其余程序集未逐一遍历。
- **门 4**：无 source-scan 守护测试（I-4）；确定性以现状 grep + 测试保证，未升格为门。
- **门 5**：N-1c 未实施（可选）；AuthorityUnavailable 分支无专测（D-1-01）；P4.1 LocalHost 空壳/P5.1 未开工（SF-1 阶段事实）——typed 结算/出千到"生产宿主"的最后一跳在 G2/V5-3 落地。
- **门 6**：探针为类级实跑；未在本线程重跑独立字节级再生成对比（依赖既有稳定字节 Fact 群 + 149 全绿）。
- **门 7**：覆盖实测的座位/观战向量（可见性黄金 8 + 观战拒绝 5 + 私有事件面冻结）；未穷举声明"所有可能泄漏路径"。
- **门 8**：恢复合同覆盖进程内 checkpoint/journal/cold-recovery/租约/回执重放冲突；PG server-restart 场景不在 CI/本地 gate（I-1）。
- **门 9**：本头无任何 Unity/IL2CPP 构建日志（全部 BLOCKED）；P22 三类在途证据未审未计；Android 0xC0000409、iOS、真机、跨物理网络、TLS/WSS 明确残余。
- **门 2**：本线程 NOT_RUN（并行 Anti 任务）。

## 15. 声明

1. 本报告是 caller 判定 G1 的输入，**不构成 G1 批准**；不 push、不建 PR、不触发 CI、不合并 v5、不部署、不转派/启动其他代理、不创建 complete.flag。
2. 本次对仓库的唯一改动 = 新增本报告文件；生产/测试/fixture/台账/catalog/verifier/既有报告零改动（提交后 `git status` 干净、`git diff --check` 干净可复核）。
3. 两个 DeepSeek 返修线程（thread_20261026_103011_054d / thread_20261026_103011_957d）的未提交产物未被读取或合入；其实现未被重复审查。
4. 所有退出码为原生数值退出码；所有计数为真实计数；无跳过、无旧产物冒绿、无零测试或 skip 顶替。
