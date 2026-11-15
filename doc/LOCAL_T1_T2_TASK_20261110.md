# 单机稳定性：T1 / T2 补测任务

日期：2026-11-10。目标是单机本地游戏的规则正确性、持续游玩和可靠恢复；本任务不代替 Unity 动画、真实键鼠或设备验收。

## 身份与范围

- 用户授权：由主代理设计，Luna6 实现；拆为 T1、T2 两项；基于 main，复核和验证后合入 main。
- 实际起点：`ac167cb4c7115c64601d796b517516c8ca597c64`（main）。
- 工作树：`D:/majiang-wt/local-t1-t2-20261110`；分支：`task/local-t1-t2-20261110`。
- 设计依据：`doc/TEST_DESIGN.md` 的 T1/T2、覆盖映射和去重原则。
- 工具：`D:/game_dev_tools/dotnet/dotnet.exe`；独占输出：本工作树 `build/local-t1-t2/<GUID>/`。
- 无 Unity、ADB、远端服务、数据库或全局环境改动。保留现有规则、黄金数据、拒绝边界和全部既有断言。

## 两项任务与验收矩阵

### T1：纯代码与规则组合

实现范围为现有 Domain / Client 测试工程；无文件、网络、真实等待和进程。

| coverageId | 风险与设计断言 | 与既有测试的区别 |
|---|---|---|
| LOCAL-T1-01 | 有界固定种子的合法摸打/响应序列；逐步独立枚举 0..135、合法位置、版本推进与事件后缀；中途捕获恢复并与未恢复分支继续同一命令序列比较 | 既有黄金与单动作断言仍保留；新增持续操作及恢复组合不变量 |
| LOCAL-T1-02 | 在上述序列的多个阶段插入错误局号/座位/实体或失效准备结果；拒绝前后牌、分数、供托、版本、事件完全不变；终态不能复活 | 把拒绝边界放进真实前序操作产生的状态，避免只测试初始状态 |
| LOCAL-T1-03 | 时钟暂停/恢复与决策换身份、旧控制代次/旧回调的组合；不延长免费时间、不重置储备、旧操作零副作用 | 先查既有 DecisionClock/ControlRegistry 用例；只有缺失的交互才新增 |

固定种子只是有界探索；必须保留种子/步骤诊断。初态走合法牌山和领域入口，不能伪造成功、注入终态或用被测规则计算唯一预期。已覆盖的等价边界记录复用位置，不为增加数量复制测试。

### T2：真实本地客户端与双重持久化

实现范围为 LocalHost.Tests，以及必要的现有 LocalHost.CrashWorker 测试模式；复用 TableSession / ITableChannel / LocalHost / FileOutboxStore。进程内 LocalHost 不冒称远端服务。

| coverageId | 风险与设计断言 | 与既有测试的区别 |
|---|---|---|
| LOCAL-T2-01 | 真实 Client 的 FileOutboxStore 与真实本地权威存档一起经历回执丢失和冷重开；按原 account/scope/request/digest 查询，只应用一次，不自动重新提交 | 当前 LocalHost 对账矩阵主要用 InMemoryOutboxStore；Client 文件存储用例主要连接 FakeTableChannel |
| LOCAL-T2-02 | 独立 worker 中由 TableSession 先落盘 outbox，再实际提交；标记确认提交且回执未被客户端确认后强杀；新进程/新 store 读取原待决请求并对账，最终再重开也不复活 | 禁止父进程在强杀后用 marker 重新构造 outbox 冒充持久客户端记录 |
| LOCAL-T2-03 | 支付型三绝技在提交后丢回执/冷重开时，仅一次费用和账本记录；牌实体、候选可见性及原请求身份正确 | 组合费用幂等与真实文件 outbox；复用既有合法三绝技场景 |
| LOCAL-T2-04 | 本地多局推进或两个独立存档的隔离边界；冷重开后 roundId、比分/供托/记录和未决请求归属正确；旧局命令不能修改新局 | 先核 LocalMatch/Concurrency 既有用例，仅补尚未证明的存档/outbox 组合 |

既有 G6 八类恢复（正常、强杀、主档截断、checksum 错、缺主有备、两档缺、两档坏、不兼容）优先增强或复用，不另起同义矩阵。错误主档和旧请求不得被静默当成新游戏成功。进程只操作本任务启动的 PID，带有界标记等待和退出清理。

## 覆盖与去重交付

主代理复核每项新增断言与现有实现，补一份 `coverageId → 测试方法/既有复用 → T1/T2 → 实际结果` 清单。未新增不等于未覆盖；未实测也不写通过。所有单机适用 U/API 场景族应有已有覆盖或明确缺口，远端 TLS/PG 等单列为本任务范围外。

### 单机范围的既有复用映射

下表是源码覆盖入口，不表示每个场景族的全部组合已穷举。实际执行与新增组合的结果在交付时单独记录。

| 原设计范围 | 复用的主要测试文件（`dotnet/tests/` 下） | 本轮关系 |
|---|---|---|
| U-01 牌实体与位置 | Domain.Tests/TilePositionMapTests、TileMovementGoldenTests | 增补 LOCAL-T1-01/02 连续序列 |
| U-02 合法动作 | Domain.Tests/RoundDecisionTests、RoundStateMachineTests | 复用吃碰杠、立直、和牌等规则正负例 |
| U-03 和牌算分 | Domain.Tests/ScoringContractTests、ScoringGoldenTests、RoundSettlementCalculatorTests | 复用冻结算分与非法输入 |
| U-04 单局状态机 | Domain.Tests/RoundStateMachineTests、RoundReplayGoldenTests | 增补中途恢复和拒绝组合 |
| U-05 整场推进 | Domain.Tests/MatchSessionTests | 复用换局、重复结果、终态与供托 |
| U-06 洗牌码牌 | Domain.Tests/ShuffleGoldenTests、ShuffleStackingTests、ShuffleTypedRandomTraceTests | 复用合法牌山来源与确定性 |
| U-07 三绝技 | Domain.Tests/TileMarkTests、SleeveBloomTests、BeamSwapTests、N3CheatResidualBoundaryTests | 复用授权、冻结候选与交换边界 |
| U-08 费用与幂等 | Domain.Tests/CheatLedgerTests；Contracts.Tests 的幂等/规范载荷用例 | 复用纯模型；文件+权威交互归 T2 |
| U-09 时钟与控制 | Domain.Tests/DecisionClockTests、ControlRegistryTests；Ai.Tests/TargetAwareAiStrategiesTests | LOCAL-T1-03 仅补未覆盖的交互 |
| U-10 Client/协议 | Client.Tests/TableSessionTests、TableSessionOperationQueueTests；Contracts.Tests | 复用原键、游标、绑定、发送前持久化合同 |
| U-11 表现纯模型 | Unity.TableModel.Tests/TableCommandIntentFactoryTests、ConfirmedAnimationQueueTests、LocalFullMatchExecutionBoundaryTests | 仅纯模型；不计作动画/真实输入证明 |
| U-12 架构与工具 | Architecture.Tests；tools/verify-v5-source-provenance.ps1 | 复用当前冻结来源；无旧 Kotlin 运行依赖 |
| API-01/02 本地入桌/房间 | LocalHost.Tests/LocalTicketTests、LocalHostLifecycleTests、C04RoomTicketTests、LocalHostSessionSmokeTests | 实际 LocalHost 适配器；无独立远端 Lobby 声明 |
| API-03..06 摸打/吃碰/立直/杠 | LocalHost.Tests/C04PlayerActionChannelTests、LocalHostCandidateAcceptanceTests | 复用真实 TableSession 通道及各杠种/抢杠用例 |
| API-07/08 和牌/流局/整场 | LocalHost.Tests/LocalHostWinTests、LocalHostRoundTests、LocalMatchTests、LocalMatchChannelProgressionTests | 复用真实客户端与换局；补文件请求归属组合 |
| API-09/10 三绝技与费用 | LocalHost.Tests/C04PlayerCheatChannelTests、LocalHostCheatTests | LOCAL-T2-03 补支付与文件 outbox 的恢复组合 |
| API-11 私有信息 | LocalHost.Tests/C04PrivacyStreamMatrixTests、C04RoomTicketTests | 复用四席与观战原始通道断言 |
| API-12 回执丢失/重放 | LocalHost.Tests/C04RecoveryRequestReconciliationTests、LocalControlTests | LOCAL-T2-01/02 补普通动作的双持久化 |
| API-13 并发/隔离 | LocalHost.Tests/LocalHostConcurrencyTests、LocalTableStoreTests；TableRuntime.Tests/TableStreamActorTests | LOCAL-T2-04 补独立存档/outbox 归属 |
| API-14/15 deadline/控制 | LocalHost.Tests/LocalHostDecisionTests、LocalControlTests、C04RoomTicketTests | 本地真实通道与自有进程；不替代远端掉线证据 |
| API-16 持久化/冷恢复 | LocalHost.Tests/LocalSaveBackupRecoveryTests、C04RecoveryRequestReconciliationTests | 既有 G6 八类继续保留；增强真实客户端文件状态 |
| API-17 本地传输故障 | LocalHost.Tests/LocalHostConcurrencyTests、C04PrivacyStreamMatrixTests | 本地队列、取消与关闭；TLS/WS/真实网络不适用单机范围 |
| API-18 线上资产权威 | 原服务端专用验证保持原合同 | 线上资产链不属本轮本地单机实现，不以本地用例宣称已通过 |

## 验证阶段与运行边界

1. A：核对基线、文件范围、SDK 可用及当前自有进程；只读盘点现有用例。
2. B：Luna6 两项并行实现，文件范围隔离；主代理独占构建和测试，避免同时写共享输出。发现生产缺陷先汇报并设计最小修复，不放宽测试。
3. C：构建本候选 Domain / Client / LocalHost 相关测试及 worker；运行新增定向集合，核对实际发现数、XML 与原生 exit。编译失败不运行旧二进制。
4. D：定向通过后运行受影响测试工程的完整集合及冻结来源门禁；输入未变的通过项不重复。失败保留原件，修复原因后才复验。
5. E：一次性复核全部范围、假绿、规则/守恒、并发恢复、隐私、证据与文档；记录原生退出、计数和文件哈希。核对 main 未被他人推进后再集成。

构建有界 10 分钟、单个测试工程有界 10 分钟，30 秒心跳；沿用既有 runner 的额外约束。仅本地 .NET，不申请 Unity 共享锁。所有证据用本任务独占 GUID 目录，不覆盖历史输出；正常取消自有进程后再封存。

## 实现与实际结果

主代理设计和复核，两个 `gpt-6-luna` 任务分别实现 T1 / T2。生产源码未修改，没有新增测试框架、依赖或 skip。

| coverageId | 实际测试方法 | 增量与结果 |
|---|---|---|
| LOCAL-T1-01/02 | `LocalRoundSequenceTests.FixedSeedLegalTurnsConserveTilesAndRestoreToEquivalentReplay` | 新增 4 个固定种子行（17/29/43/71）；真实完整置换与响应窗口、逐命令守恒、拒绝零变化和恢复分支重放；4/4 PASS，不按两个 coverageId 重复计数 |
| LOCAL-T1-03 | `DecisionClockTests.PauseResumeAndControllerReplacementKeepOldCallbackAndDecisionStale` | 新增 1 案；先旧 epoch 拒绝，再到期旧 key 回调拒绝，最终新 key 正常到期；1/1 PASS |
| LOCAL-T2-01 | `LocalMatchChannelProgressionTests.LostReceiptIsReconciledThroughOriginalOutboxWithoutDuplicateSideEffect` | 增强既有 1 案；换为真实 FileOutboxStore，新 store 实例冷重连并核对原请求字节、身份和磁盘记录；PASS |
| LOCAL-T2-02 | `C04RecoveryRequestReconciliationTests.ForceKilledProcess_ReconcilesOriginalRequestWithoutDuplicateSideEffect` | 增强既有 G6-02；真实 worker 发送前写文件，提交后未确认时由父进程终止，父进程直接冷读原文件，不再重新造 pending entry；PASS |
| LOCAL-T2-03 | `C04PlayerCheatChannelTests.PaidTileMarkLostReceiptColdReopenChargesOnceFromFileOutbox` | 新增 1 案；120→119、账本一条、恢复标记效果与物理实体正确，冷重开仅查回执不发业务命令；PASS |
| LOCAL-T2-04 | `C04RecoveryRequestReconciliationTests.SharedFileOutboxKeepsPendingRequestOwnedByItsOriginalRoom` | 新增 1 案；错房查询不清旧 pending、不改错房状态，回原房对账；捕获实际 wire 发送证明没有盲目重发；PASS |

共新增 7 条展开用例，增强既有 2 案。每个新异步集成测试均有总时限和 finally 资源清理；worker 完整写入后原子发布 marker，并有 90 秒自限。

### 定向与完整回归

真实执行器为既有 `tools/verify-v5.ps1`，`RunnerMode=direct`；XML 是 xUnit 原生逐例结果，不能改称 `dotnet test` / MTP 证据。

| 测试工程 | 实际执行 / 通过 | 失败 / 错误 / 跳过 / 未跑 |
|---|---:|---:|
| Domain.Tests | 534 / 534 | 0 / 0 / 0 / 0 |
| Ai.Tests | 444 / 444 | 0 / 0 / 0 / 0 |
| Contracts.Tests | 152 / 152 | 0 / 0 / 0 / 0 |
| Architecture.Tests | 132 / 132 | 0 / 0 / 0 / 0 |
| Client.Tests | 182 / 182 | 0 / 0 / 0 / 0 |
| TableRuntime.Tests | 493 / 493 | 0 / 0 / 0 / 0 |
| LocalHost.Tests | 199 / 199 | 0 / 0 / 0 / 0 |
| Unity.TableModel.Tests | 236 / 236 | 0 / 0 / 0 / 0 |
| 合计 | 2,372 / 2,372 | 0 / 0 / 0 / 0 |

Release solution 构建 exit 0；不在 solution 内的 4 个测试工程由既有执行器单独构建。共享 DLL scratch 复制闭包 PASS（12 assemblies，未改 Unity Assets）、companion 4/4 fixtures PASS、冻结历史来源校验 PASS。当前本地字节与嵌入来源通过；未运行联网/历史 Git pin 获取。

证据根：`D:/majiang-wt/local-t1-t2-20261110/build/local-t1-t2/b2f40f07b3c942959e137b224e67576c1/`。

- `tested-source-inputs.json`：实际测试源码的 SHA-256，集成前再次核对。
- `t1-sequences.xml` / `t1-clock.xml`：4+1 案定向结果。
- `t2-targeted-1.xml`：2 个新增集成案与 2 个增强案，4/4 通过。
- `full-verify-1/summary.log`、`direct-xml/<project>/direct-results.xml`：8 工程逐例结果，XML、控制台和各原生退出相互核对。
- `full-verify-1.exit.txt`：完整执行器原生退出 0。
- `local-verify.manifest.json`：本次选定的 8 个测试工程。`required` 沿用现有 solution 成员语义；所有选定工程均实际执行且失败均使整体验证失败。

实际完整命令（在本任务工作树执行，重新运行应换新的 LogDir）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe `
  -RunnerMode direct -NoProjectDiscovery `
  -ManifestPath build/local-t1-t2/b2f40f07b3c942959e137b224e67576c1/local-verify.manifest.json `
  -LogDir build/local-t1-t2/b2f40f07b3c942959e137b224e67576c1/full-verify-1
```

### 首败与复核记录

首轮 Domain 构建暴露 helper 作用域错误；首轮 LocalHost 构建及独立编译诊断暴露 nullable、wire 字段类型与 xUnit analyzer 用法错误。原始 `*-build-1.log` 和 `localhost-compile-diagnostic.log` 原件保留；分别修复后重建均为 0 警告、0 错误。最初 T1 联合筛选产生 Total=0，明确排除为无有效执行，随后分别运行 4 行序列与 1 案时钟并核对实际计数；不把 exit 0 当用例通过。

复核还纠正了固定牌序假称种子、未进入响应窗口、时钟输入倒退、marker 部分写入和错房 lookup 等待条件等问题。最终测试验证了新增风险，没有改变生产规则或宽松化验证器。

本轮单机 T1/T2 补测已通过。远端独立服务、PG、TLS、Unity 实际动画、最终 Player 的真实输入/画面及设备证据未在本任务执行；T1/T2 结果不能写为单机游戏、G2、T3/T4 或设备整体已验收。
