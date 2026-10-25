# V5 G1 当前冻结头九门证据汇编（供 Codex 最终重审）

日期：2026-10-26
工作树：`D:\majiang-wt\g1-current-evidence`（分支 `wt/v5-g1-current-evidence`）
冻结基线：`6c3573620f61e8cbb6a13c8c4b81abef0b173fd9`
性质：**只汇编证据，不做门禁批准**。本报告是给 Codex 做最终一次 G1 九门判定的输入，不是判定本身。
关系声明：本报告**不重复、不覆盖**历史审计 `8254c9b` 时点的报告，也不重做全字段 census；全部证据针对当前冻结头 `6c357362` 重新取证。

---

## 0. 冻结头身份（本轮亲验）

| 项 | 值 | 验证方式 |
|---|---|---|
| HEAD | `6c3573620f61e8cbb6a13c8c4b81abef0b173fd9` | `git rev-parse HEAD` |
| 分支 | `wt/v5-g1-current-evidence` | `git branch --show-current` |
| 状态 | 干净（开工与收工两次 `git status --porcelain` 均为空） | 本轮 |
| 与 8ee22eb 关系 | `8ee22ebf5164f74db75e5a33e87900f5b87cd8bd` 是当前头祖先（`git merge-base --is-ancestor` → yes） | 本轮 |
| 与 PR 合成头树等价 | `git diff --quiet 6c357362 4f008bb617b74e5bfd6a575506aca8abadad821d` → exit 0（整树逐字节等价） | 本轮本地自证 |
| HEAD 内容 | 仅比集成头 `0eb27a8` 多一个 docs 文件 `CODEX-V5-N3-CLOSEOUT-ACCEPTANCE.md`（54 行新增，0 代码改动） | `git show --stat 6c357362` |
| 已发布状态 | `6c357362` = `origin/feat/v5-1-integration` tip；`origin/v5` tip = `457274c`；CI PR run 的合成头 `4f008bb` parents = `457274c` + `6c357362` | 本轮 |
| 97831c8 | ProtocGenerationIsRepeatable Fact 引入 commit，是冻结头祖先 | `git merge-base --is-ancestor` |

探针记录：`artifacts/TEMP/nine-gate-probes.sh`（SHA256 `909897c493ec52cef5a58ec53129e297102b8aee04ef701a68f64d7505e657e7`）、`artifacts/TEMP/nine-gate-probes.log`（SHA256 `e134cb4aabcba1dca47ea5c02c2c67fa946e5fdeca4f165539b672d3591fade5`，PROBE-EXIT=0）。

---

## 1. CI 证据来源声明（必须准确）

**所有 CI 结果均来自"远端实跑、本轮只读读取"，不是本地重新运行。** 本任务未触发任何 CI、未 push、未建 PR。读取目录 `E=D:/majiang-wt/codex-g1-progress-20261026/artifacts`（caller 采集的 GitHub Actions 官方产物）。

- push run 40237578435：https://github.com/liuchangready/majiang-game/actions/runs/40237578435 ，HEAD=`6c357362`，`refs/heads/feat/v5-1-integration`，attempt=1，Windows runner，.NET SDK 8.0.426。
  - `run-info.txt` SHA256 `6F20D9C3BAF2729DF54F71404DF8A847183C1844E5DE5FAC52B2D594E3C9A38B`
  - `summary.log` SHA256 `80BD705F8A874ECFD3BC03478CF8449AFB667306AE5FBE23B7CA5137712D6680`
  - `pg/wrapper-summary.json` SHA256 `0B24A772B7D88A48CA0FEC9AAC2A734C98C52F97CC71EBA2B746A2C2F7A30EEA`
  - `pg/postgres-evidence.jsonl` SHA256 `E6C1EA7CF15941C526C778ADFFB1180D05A2587FF75B26C62BBDE76B02C53017`
  - `pg/migrate.log` SHA256 `CF06CB95B91C12E5F05F3E5480B087DE7DF00C9CDF6BA491C60EF501F3E9256B`
  - `pg/env-restore.jsonl` SHA256 `1FAD70863EA7254E871C59E68D96A05C50514873B28103E40AA1199DE20793E3`
- PR run 40237582686：https://github.com/liuchangready/majiang-game/actions/runs/40237582686 ，HEAD=`4f008bb`（merge commit，parents `457274c`+`6c357362`），`refs/pull/19/merge`，Windows runner，.NET SDK 8.0.426。
  - `run-info.txt` SHA256 `F0A1E777D2A78ACDE479DD856CD83315309E163359A014B543A6F69832A32460`
  - `summary.log` SHA256 `616504D99C163336DD2AB2B109F6A509B2B02351C6EE79A265D7391459694F3B`
  - `pg/wrapper-summary.json` SHA256 `C2631AF03EEEFB17D2CAB725F39897ABA3D71B3FADAFB94A28F5156123F175EE`
- 完整性核对：`evidence-sha256.csv`（SHA256 `36D3C6A01554A09303F2155F5FB6936EE277681AED8F87622B3AE919902CCD03`）登记 127 项，本轮以自写脚本（`artifacts/TEMP/verify-evidence-csv.ps1`，SHA256 `0dba830e812aea7dd35e885f125da718c7412f9cb21b600cdb3f348f6c4cb044`）逐文件重算比对，输出 `CSV-VERIFY OK=127 BAD=0 TOTAL=127`（native exit 0）。
- reviewer 脚本与日志（caller 侧）：`review-ci.ps1` SHA256 `55E766E961710FEDFA781696D068D5AE264256B34FF9330FAFBEEEE44ACD1C62`；`ci-review.log` SHA256 `0b9cc2606d4b8d7a…`（完整值见 CSV），含 2×`CI-REVIEW-PASS` + `TREE-EQUALITY-PASS`；`REVIEW.txt` SHA256 `702985D7FDB8CD10EDF4AE82A55E488F2AA000ADB1605B2BFADAC8E538BE64BF`。

**两个 run 的共同实测结果（两份 summary.log 均亲读）：**

| 项目 | total | failed | skipped |
|---|---|---|---|
| Mahjong.Ai.Tests | 438 | 0 | 0 |
| Mahjong.Client.Tests | 132 | 0 | 0 |
| Mahjong.Client.Wss.Tests | 80 | 0 | 0 |
| Mahjong.Contracts.Tests | 149 | 0 | 0 |
| Mahjong.Domain.Tests | 506 | 0 | 0 |
| Mahjong.LobbyService.Tests | 89 | 0 | 0 |
| Mahjong.TableRuntime.Tests | 373 | 0 | 0 |
| **合计** | **1767** | **0** | **0** |

另有 solution build PASS exit 0、plugin copy 12 个 DLL（7 个 Mahjong 生产程序集 Ai/Client/Contracts/Contracts.Wire/Domain/LocalHost/TableRuntime + Google.Protobuf 3.28.3 + System.Buffers/System.Memory/System.Numerics.Vectors/System.Runtime.CompilerServices.Unsafe 4 个兼容程序集；**零 Tests DLL**；官方 copy 清单见 caller 复核 `findings.log`，SHA256 `f6279cd252429242aefabef945314370489644828d382fa0e29f22d451498268`）；7 个测试项目计数只归属测试统计，不与插件清单混算。OVERALL PASS 共 9 行判定。两 run 的 PG wrapper 均为 10 阶段（precheck/start-postgres/ownership-marker/environment/migrate/verify/archive-cluster-evidence/cleanup/env-restore/write-evidence）全 PASS、统一 verifier exit 0、failedStages=0、集群端口 55460、rootDeleted=True、restoredToPreCall=True。

**Domain 独立复核**：`runs/Mahjong.Domain.Tests/dotnetTest.meta.txt` 内嵌 exit code 0、total 506 / failed 0 / skipped 0 及 HEAD；其引用的框架日志 `Mahjong.Domain.Tests_net8.0_x64.log` SHA256 `50D3C659EA72E6F075813EB2599BB8D5684CFCC7DFD396F1EBAC9B64A35D0299`，与 meta 内嵌哈希一致，本轮重算吻合。

---

## 2. 九门逐门证据

每门按：当前头源码锚点 → 当前有效测试/已接受证据 → 命令与时点 → 计数与日志 → 适用范围 → 残余 → 建议判定。

### 门1 dotnet test 独立全绿 — 建议 PASS

- 证据：§1 两 run 1767/0/0（2026-10-25 21:53 UTC 起，远端实跑，本轮读取）；solution build PASS exit 0；无 skip、无空跑（每项目 total 与历史增量一致：Domain 322→506、Table 313→373、Contracts 104→149、Ai 316→438）。
- 独立复核：Domain meta + 框架日志哈希亲验（§1）；全部 127 项 SHA256 亲验（§1）。
- 口径说明：计数采用 D-R2-01 已接受改判口径——历史上 `--filter` 实跑为全项目直接 xUnit 有效选择，124+7 两次定向选择不构成假绿（该结论属已接受台账，本轮不重开）。
- 残余：见 I-CUR-4（旧 run 39970580589 Client 失败框架日志仍未找回，与本门当前绿无关，不可称 flaky）；I-CUR-7（MTP 控制台 UTF-16 混合编码，无法给出 per-test 明细行级提取，但 meta+框架日志哈希链完整）。

### 门2 黄金样本：已导出对齐率 100% / 未导出单列 — PENDING（明确待 caller）

- 冻结 census 是唯一分母：实例真值分母 **592**（v3 混合分母 286 已作废），592 = 411 bound + 82 classified + 99 pending，覆盖 14 包；17 UNA 旁置；pending 分布 B1:6 / B6:11 / B8:75 / B9:7。出处：`V5-G1-N3-CLOSEOUT-LEDGER.md`（已接受，校验器 digest 防篡改）。
- 本轮机械层复验：当前头重跑黄金目录校验器（只读，`-RepoRoot` 指本工作树），输出 fixtures=6 / requirements=73 / cases=10389 / random=10000 / facts=45 / 覆盖 full:17 partial:21 none:32 v5-only:3，catalog sha256 `236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D`，exit 0。日志：`artifacts/TEMP/catalog-validator.log`（SHA256 `95c46b46e1397e80f3a64290234f99d138221a03188de4a049b32d162b3eb73e`）。
- **门2 的 99 个 pending 元数据裁决不归本任务**：两个 DeepSeek 证据子任务分别举证 81 notes（B1/B8）与 18 AI（B6/B9）。本任务不读其在途产物、不裁其 identity/分类、不改共享台账。门2 是否达到"可宣布对齐口径"由 caller 验收两份证据并串行改判原共享 N3 台账后才能关闭。
- 本任务已锚定的门2 相关测试事实（仅陈述存在性，不代裁）：`N3TableCheatGoldenAdmissionTests.cs` 6 Facts，`:63` 断言 denominator=5 / bound=4 / blocked=1；`ShuffleGoldenBindingTests.cs:415` DeadWallSegmentEqual。
- 建议判定：**BLOCKED（等 caller）**，不因机械层 exit 0 而宣布 PASS。

### 门3 依赖方向静态测试 — 建议 PASS

当前头 8 处依赖断言锚点全部在位（本轮逐点 grep 命中）：

| # | 断言 | 锚点 |
|---|---|---|
| 1 | DomainDoesNotReferenceContractsWireProtobufUnityOrDatabase | `dotnet/tests/Mahjong.Contracts.Tests/SchemaCompatibilityTests.cs:376` |
| 2 | ContractsDoesNotReferenceWireOrProtobuf | 同文件 `:389` |
| 3 | WireReferencesContractsAndProtobuf | 同文件 `:399` |
| 4 | EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents | 同文件 `:409` |
| 5 | DomainAssemblyDoesNotReferenceContractsUnityNetworkOrDatabase | `dotnet/tests/Mahjong.Domain.Tests/TilePositionMapTests.cs:1049` |
| 6 | ObservationSurfaceExposesNoHiddenDomainState（含 Ai 程序集禁引用 TableRuntime/Contracts/LocalHost/UnityEngine 守卫） | `dotnet/tests/Mahjong.Ai.Tests/AiDecisionTests.cs:394`（程序集守卫块 `:441-452`） |
| 7 | ClientAssemblyDoesNotReferenceDomainOrTableRuntime | `dotnet/tests/Mahjong.Client.Tests/TableSessionTests.cs:749` |
| 8 | LobbyAssemblyDoesNotReferenceDomainOrTableRuntime | `dotnet/tests/Mahjong.LobbyService.Tests/ArchitectureAndPolicyTests.cs:13` |

- 运行证据：Contracts 149 / Domain 506 / Ai 438 / Client 132 / Lobby 89 两 run 全绿（含上述全部 Fact 实跑）。
- 残余：无。

### 门4 Domain 无隐式随机/时钟/浮点权威 — 建议 PASS

- 本轮对当前头 `dotnet/src/Mahjong.Domain` 全目录 grep（含 N3 期新增 Domain 文件）：
  `new Random( |DateTime.Now|DateTime.UtcNow|DateTimeOffset.Now|DateTimeOffset.UtcNow|Guid.NewGuid|Stopwatch.|Environment.TickCount|Thread.Sleep|Task.Delay(` → **零命中，grep exit 1**（探针 C 段）。
- 运行证据：Domain 506 两 run 全绿；确定性 golden（洗牌/发牌/守恒）全绿。
- 残余：无（该门原文条件未加码；不以更细 census 无限重开）。

### 门5 三绝技与 136 守恒 — 建议：核心已销项，**门 PASS 尚未达成**（硬前置残余 = N-2 宿主接线 D-CUR-1，按原关闭公式属 G1 判定前须处置项，见 §4；§7.2 负例矩阵随 N-1 验收覆盖，见下）

**N-1a 域动作扩展（已销项，证据在当前头）：**
- `dotnet/src/Mahjong.Domain/RoundTypes.cs:31-47`：`RoundActionKind` 14 值（0..13，含 DeclareChi=9/DeclarePon=10/DeclareOpenKan=11/DeclareClosedKan=12/DeclareRiichi=13）。注意末值 `DeclareRiichi = 13` 无尾逗号，机械 `grep -c " = [0-9]*,"` 会得 13，实际 14 值。
- `RoundDecisionTests` 24 Facts 全绿；`RoundDecisionCheckpointTests` 3 Facts 全绿。
- 验收记录：`CODEX-V5-N1A-ACCEPTANCE.md`（PASS；AgariYame 归 MatchSession.ChooseDealerEnd）。

**N-1b 三绝技权威执行链（已销项，证据在当前头）：**
- 入口：`TableRuntimeContracts.cs:271` `TableCheatCommandEnvelope`（`:320` CheatCommand 属性）→ `TableActor.cs:223` `SubmitCheatAsync`（同一权威队列）→ `:1487` `ProcessCheatCommandAsync` → `:1639` Core → 域迁移 `RoundCheatTransition.cs` 内 20 处 `CheatSubmission.` 调用（`:531`–`:864`，覆盖 7 个公共入口 + 过期/送达分支）。
- 玩家路径与域谓词一致：`TableActor.cs:1449`/`:2993` `_round.Execute(command.Command, CheatProtectionPredicate())`。
- 投影：`TableProjectionContractMapper.cs:520` CheatPointsChanged / `:551` SleeveBloomResolved / `:557` BeamSwapResolved。
- `N1bCheatExecutionTests` 40 Facts 全绿；mixed-boot 探针 2/2（N-1b 验收记录 `CODEX-V5-N1B-ACCEPTANCE.md`）。

**136 物理守恒（充分）：**
- `dotnet/src/Mahjong.Domain/TilePositionMap.cs:193` `TotalTileCount = 136`；`PhysicalGoldenTests` 21 处 `Assert.Equal(136`；洗牌/配牌/宝牌/王牌段 golden 全绿。

**N-2 接线残余（门5 尚不能宣布 PASS 的唯一原因）：**
- `RoundStateMachine.cs` `RequireSettlement` 5 处（`:621`/`:653`/`:1201`/`:1217` 及 Ron 路径 `:1263`）——结算仍为**调用方供给**。
- `RoundSettlementCalculator`/`SettlementCalculation` 在 `dotnet/src` 内 Rules 目录之外**零生产调用者**（grep exit 1）。
- 与 N-2 组件验收自述一致：`CODEX-V5-N2-ACCEPTANCE.md` 明示残余"N-2 整体的宿主接线、旧自带 settlement 入口收口、裸命令 source 绑定及可信历史认证仍未关闭"。本报告不与该已接受验收矛盾，仅将其残余如实列为门5 未闭项。

**SF-1 严格分离（阶段事实，不是当前头缺陷）：**
- `TableActor.cs` `private RoundCommand? MapAction(LegalActionOption action)`（`:6484`）仍仅 Pass/Discard（调用点 `:2253` AI / `:2531` 超时）；TableActor 内 `IAiStrategy`/`AiDecisionService` 零引用（grep exit 1）；`dotnet/src/Mahjong.LocalHost` 0 个 `.cs`。这三点是 SF-1 记录的历史阶段事实，其收口属 P4.1/P5.1 准备与实现，**不计为 G1 门5 的失败条件，也不因修复后的 N-1a/N-1b/N-2 而改写**。

### 门6 Wire 可重复生成 — 建议 PASS（有实际生成证据，非推断）

- 单一事实源：`dotnet/src/Mahjong.Contracts.Wire/Mahjong.Contracts.Wire.csproj:28` `<Protobuf Include="mahjong.proto" GrpcServices="None" />`（Grpc.Tools 2.68.1 + Google.Protobuf 3.28.3）；生成文件不入库、每次构建重新生成（csproj 注释行明示）。
- **实际重复生成证据**：`SchemaCompatibilityTests.cs:338` `ProtocGenerationIsRepeatable`——同一 protoc 跑两遍，断言 `Mahjong.cs` 与 descriptor **字节相等**；该 Fact 在 Contracts 149 两 run 中实跑全绿。引入 commit `97831c8` 是冻结头祖先，即该断言对当前头生效。
- caller 第 1 轮复核新增独立本地实跑（本轮只读读取其记录，非本任务执行）：本树 Contracts Release `--no-incremental` 构建 0 警告 0 错误；direct runner `-class '*SchemaCompatibilityTests'` → **13 PASS / 0 FAIL / 0 SKIP / 0 NOT_RUN**（含 ProtocGenerationIsRepeatable）。记录：`findings.log`（SHA256 `f6279cd252429242aefabef945314370489644828d382fa0e29f22d451498268`）、`schema.xml`（SHA256 `D8CCDA50F94377A7911E9D188A3132B9878904FAAA85914338E204A5C4D976B1`）。
- 附加冻结护栏（同文件）：`:318` `WholeSchemaFingerprintIsFrozen`、`:279-291` proto↔descriptor 同步、`:235-245` reserved 守卫、`:325-335` 禁 map/Any/Timestamp/float。
- 本任务未自行把"稳定编码=生成 diff"当作证据；门6 的判定材料就是上述实跑 Fact。
- 残余：无。

### 门7 按座位/观战私有性 — 建议 PASS

- `TableVisibilityProjectionTests` 18 Facts + `VisibilityGoldenTests` 8 Facts（Table 373 内实跑全绿）。
- 观战前置拒绝：`dotnet/tests/Mahjong.Client.Tests/IndependentReviewTests.cs:66` `SpectatorMustRejectPlayerCommandBeforePersistenceAndSend`（Client 132 内全绿）。
- 私有类型白名单：`dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs:69` `ProtocolEventPolicy.PrivateTypes`；门3 锚点 #4 冻结 V4 19+2 与 V5 私有事件；门3 锚点 #6 保证 Ai 观测面不泄漏 Domain 隐藏状态。
- 残余：无。

### 门8 TableRuntime 并发/版本/恢复合同 — 建议 PASS

- Table 373 两 run 全绿；合同文件在位：`TableDurableContracts.cs`、`TableRecoveryService.cs`、`TableDurableLeaseRecoveryTests.cs`（本轮 ls 存在性探针 K 通过）。
- PG（Lobby 组件）证据事件本轮在 push run `postgres-evidence.jsonl` 亲验：crossRoomJoin×3、receipt.replay、receipt.terminalReplay、recovery.expiredReservation、transaction.rollback、ticketConsumption、roomFill、crossConnectionRowLock、migration.repeated（migrate.log "run 1 of 2"/"run 2 of 2" 两遍幂等）、terminal.projection×2、server.identity；env-restore 4 变量 restored-absent；postgres.log ready-count=1。
- **范围声明**：PG 是 Lobby 组件验收，**不代表尚未实现的 P5.2 事件存储 PASS**；两进程重启探针不在 CI wrapper 中（见 I-CUR-5）。
- 残余：I-CUR-5。

### 门9 Unity/IL2CPP 可用平台构建日志与缺口分列 — 建议 PASS（按 P2.2 出口口径：日志✓ + 缺口如实分列）

- 已有原始构建日志：Windows .NET（两 run sln-build PASS exit 0 + 12 个 DLL plugin copy——生产+互操作程序集，无 Tests DLL，明细见 §1）。
- 缺口分列（全部 NOT_RUN/BLOCKED，出处 `doc/v5-重构/UNITY-ENVIRONMENT.md`，2026-10-22）：
  - Unity 许可未激活：Editor 批量退出码 198（`:5` 状态、`:133`），连 Editor 层都未通过；
  - IL2CPP 未构建（`:139-142`）；Android 无 SDK/NDK（`:47`）；iOS 需 macOS+Xcode（`:143`）；真机、跨物理网未测；CI 未配置 Unity job（`:148`）。
- 本报告不借 Editor 通过冒充 IL2CPP/设备通过，也未放宽 P2.2 出口条件。
- 残余：D-CUR-3（该文档 `:151` 一句过时表述，文档级 MINOR，不影响门9 判定）。

---

## 3. 缺陷与未决项清单（一轮列全；同类合并；无新增凑数项）

### 缺陷（需修复/需裁决）

**D-CUR-1 [MAJOR] N-2 宿主接线未完成（门5 唯一未闭残余）**
- 位置：`dotnet/src/Mahjong.Domain/RoundStateMachine.cs:621/653/1201/1217`（RequireSettlement 调用方供给）；`RoundSettlementCalculator` Rules 外零生产调用者。
- 复现：`grep -c "RequireSettlement" dotnet/src/Mahjong.Domain/RoundStateMachine.cs` → 5；`grep -rln "RoundSettlementCalculator\|SettlementCalculation" dotnet/src --include=*.cs | grep -v "/Rules/"` → 空（exit 1）。
- 实际 vs 期望：实际=结算由调用方供给、宿主未接计算器；期望（原门条件）=N-2 组件接线完成且宿主链闭合。与 N-2 验收残余自述一致，非新发现。
- 证据：本轮探针 F 段（nine-gate-probes.log）；`CODEX-V5-N2-ACCEPTANCE.md`。
- 修复要求：宿主接线按原关闭公式（`V5-1-REVIEW.md:462`）是门5/G1 **判定前的硬前置**，由 caller 在 G1 判定前处置（派单完成接线，或对门5 关闭口径另行裁决）；N-2 验收残余四项中其余三项（旧自带 settlement 入口收口、裸命令 source 绑定、可信历史认证）是否同为 G1 前置属歧义，交 caller 裁决。本报告只呈报事实，不执行 N-2/P4/P5 代码。
- 验收断言：接线落地后宿主生产路径存在计算器调用、RequireSettlement 由宿主满足、残余按 caller 裁决范围各有测试；返修时逐条给命令/exit/计数。

**D-CUR-2 [门2依赖，非缺陷] 99 个 pending 元数据裁决未决（门2 BLOCKED 根因）**
- 位置：原共享 `V5-G1-N3-CLOSEOUT-LEDGER.md` pending 集合（592=411+82+99；B1:6/B6:11/B8:75/B9:7）。
- 实际 vs 期望：实际=99 项待判；期望=caller 验收两份 DS 证据（81 notes + 18 AI）后串行改判台账，门2 达到可宣布对齐口径。
- 证据：`REVIEW.txt`（caller 简报）；台账校验器 digest。本任务不处理其 identity/分类。
- 验收断言：caller 侧完成台账串行改判并给出验收记录后，门2 才能改判。

**D-CUR-3 [MINOR] UNITY-ENVIRONMENT.md 过时句子**
- 位置：`doc/v5-重构/UNITY-ENVIRONMENT.md:151`（"V5-0…与 V5-1…尚未开始"与当前事实不符）。
- 复现：读该行。实际：与当前头状态矛盾。期望：更新为当前状态或删除。
- 修复要求：文档一行修订，随下一批次 docs 提交即可，不阻塞任何门。

### 未决事项（明确 NOT_RUN / 遗留，不凑缺陷数）

**I-CUR-4 旧 run 39970580589 Client 失败原因 UNRESOLVED**：框架日志始终未找回，既不能归因也不能称 flaky；不因当前两 run 新绿而解释掉。保持 UNRESOLVED 状态记录。

**I-CUR-5 PG 两进程重启探针不在 CI wrapper**：当前 wrapper 10 阶段不含该探针；ready-count=1 已证单进程 ready，但"重启后恢复"属 P5.2 范畴义务，本报告如实标注。

**I-CUR-6 P4.1 准备线程 FAILED / worktree `b43435d` 未接受未集成**：`thread_20261025_194659_8d8a` 因缺 native session FAILED；`b43435df0ab674f9a1f09e60766680216be4a81f`（"docs(v5): prepare P4.1 local host and save contract"）对象在库但未接受未集成。这是**后续准备义务**，不是 G1 失败条件，禁止当完成、禁止重复派生。

**I-CUR-7 MTP 控制台日志 UTF-16LE 混合编码**：无法从 `dotnetTest-*.log` 直接提取 per-test 明细行；完整性由 meta+框架日志 SHA256 链保证（Domain 已亲验）。列为已知观测限制。

**I-CUR-8 本地严格 verifier 对当前头 NOT_RUN**：本任务不盲跑全量套件/PG；本地严格验证以两 run 远端实跑记录为准，本地重跑不在本任务范围。

---

## 4. 给 Codex 最终一次九门判定的精确输入

判定对象：冻结头 `6c357362`（= origin/feat/v5-1-integration tip，整树等于 PR19 合成头 4f008bb）。

| 门 | 本报告建议 | Codex 判定所需输入 |
|---|---|---|
| 1 | PASS | §1 两 run summary.log + 127 项 SHA256 CSV 复验记录 |
| 2 | BLOCKED→待改判 | caller 对 99 pending 两份 DS 证据的验收记录 + 原共享台账串行改判结果 |
| 3 | PASS | §2 门3 8 锚点 + 五测试项目两 run 全绿 |
| 4 | PASS | nine-gate-probes.log C 段（grep exit 1 零命中）+ Domain 506 绿 |
| 5 | 未闭（核心销项；硬前置 = N-2 接线，G1 判定前处置） | §2 门5 全部锚点 + N1A/N1B/N2 三份验收 + 原关闭公式（`V5-1-REVIEW.md:462`）逐字对照 + D-CUR-1 处置（权在 caller） |
| 6 | PASS | SchemaCompatibilityTests 149 绿 + ProtocGenerationIsRepeatable 字节相等 Fact |
| 7 | PASS | 门7 锚点 + 18+8 Facts + 观战拒绝 Fact |
| 8 | PASS | Table 373 绿 + wrapper-summary.json 10 阶段 + PG 证据事件清单 |
| 9 | PASS（缺口分列口径） | UNITY-ENVIRONMENT.md 缺口清单 + Windows .NET 构建日志 |

剩余依赖（**原关闭公式逐字出处 `V5-1-REVIEW.md:462`**："G1 关闭 = 门 2（N-3 推进至可宣布的对齐口径）+ 门 5（N-1a/N-1b 完成 + N-2 接线 + §7.2 负例矩阵过）；N-1c 可选；SF-1 不在关闭公式（§7.4）；门 1/3/4/6/7/8/9 维持 PASS"）：

- 门2：等 caller（99 pending 两份 DS 证据验收 + 原共享台账串行改判）。
- 门5：N-1a/N-1b 已完成 ✓；§7.2 负例矩阵属 N-1 验收断言统一条款（`V5-1-REVIEW.md:387`），随 N-1a/N-1b 已接受验收覆盖，本报告不重开；**N-2 接线未完成（D-CUR-1）——按原公式这是 G1 判定前的硬前置**，其处置（派单完成接线，或 caller 对门5 关闭口径另行裁决）须在 G1 判定前进行，不得列为 G1 批准后才执行。N-2 验收残余四项中其余三项是否同为 G1 前置属歧义，交 caller 裁决。
- 汇总：本轮**恰 9 门 = 7 PASS（门 1/3/4/6/7/8/9）+ 门2 BLOCKED + 门5 未闭**。历史 8ee22eb 报告为 7 PASS/2 FAIL，差异逐门见 §2 当前头证据，Codex 可直接对照判定。

## 5. G1 批准后 P4/P5 可并行清单（仅列清单，不派单、不执行）

以下并行前提：各任务**文件集互不重叠（互斥占有）**、共享台账仍由 caller 串行更新。

1. **P4.1 本地宿主**（依赖 I-CUR-6 的重新准备：b43435d 未接受，需按 FAILED 原因重开准备线程，禁止复用 FAILED session）：LocalHost 组合根、AI 组合根接线、SF-1 中 MapAction 扩展前置。
2. **P4.2 壳层**（其 docs-only 准备已接受：V5-P4.2-SHELL-PREPARATION，冻结 f9fd8e8d）：可先行，与 P4.1 文件集互不重叠。
3. **P5.x 服务端/存储线**：P5.2 事件存储（I-CUR-5 的重启探针归入其验收）、HTTPS/WSS 宿主链路（`V5-ARCHITECTURE-DESIGN.md:16/:146`：V5 公网入口为 HTTPS + WSS，**不沿用 MJ-TCP/1 的 `MJTC` 魔数与 12 字节长度帧头**；MJ-TCP/1 仍为 V4 唯一生产传输边界，两者不混淆）。
4. **门2 台账改判**（D-CUR-2，caller 串行）：不与代码任务并行冲突。
5. **Unity 平台线**（门9 缺口清偿：许可、IL2CPP、SDK/NDK、iOS、真机）——按 UNITY-ENVIRONMENT.md 依赖顺序，不借 Editor 冒充。

注：N-2 接线**不在上列**——按原关闭公式（`V5-1-REVIEW.md:462`）它是 G1 判定前的硬前置（§4），须由 caller 在 G1 判定前处置；其余三项 N-2 残余的时点交 caller 裁决。

约束不变：V4 是唯一生产基线；G1 未批准前不创建 complete.flag；组件验收不得写成阶段验收；push/PR/合并由 caller 独占。

---

## REVIEW_INCOMPLETE 声明

本报告是**证据汇编**，复核完整性按 `V5-REVIEW-REWORK-PROTOCOL.md` §1 执行：缺陷与未决项已一轮列全（D-CUR-1..3、I-CUR-4..8），无追加计划；门2 因依赖 caller 对两份 DS 证据的验收，本轮无法给出最终判定，故整份报告以 REVIEW_INCOMPLETE 性质交付——这不是复核方"无法一次给全"的免责标记，而是任务边界（门2 等 caller）的如实表达。
