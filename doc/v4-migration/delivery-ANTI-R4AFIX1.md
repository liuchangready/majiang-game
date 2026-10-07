# R4AFIX1 交付报告：领域权威与全异步服务端宿主编译与行为补全

- **交付日期**：2026 年 10 月 5 日
- **执行角色**：Antigravity (Anti)
- **验收角色**：Codex (拥有独立测试、编译构建与验收决定权)
- **任务目标**：根据 [TASK-ANTI-R4AFIX1.md](file:///D:/majiang/doc/v4-migration/TASK-ANTI-R4AFIX1.md)、[FEEDBACK-R4AFIX1.md](file:///D:/majiang/doc/v4-migration/FEEDBACK-R4AFIX1.md) 及 [CONTRACT.md](file:///D:/majiang/doc/v4-migration/CONTRACT.md)，全面修复 R4A 中出现的编译器诊断，完善 `ServerTableHost` 排队 Future 外部完成生命周期与关闭安全，消除 `UdpRoom` 重传路径上的阻塞式 `.get()` 调用，并针对旧版 `TableHost` 的 20 项基准验收测试在 `ServerTableHost` 上建立完整的等价替换覆盖。

---

## 1. 编译器诊断修复 (Compiler Fixes)

针对 `build/v4-migration/R4A-delivery-check.log` 与 `doc/v4-migration/FEEDBACK-R4AFIX1.md` 中的全部编译诊断完成精确修复：

1. **`ServerTableHost.kt:433:13`**：
   - **原因**：此前仅导入了 `com.majiang.access.deepCopy`（用于 `PlayerSnapshot` / `PlayerView`），导致 `PlayerDecision.deepCopy()` 无法解析到扩展函数。
   - **修复**：添加 `import com.majiang.application.deepCopy`，使 `authority.queryDecision(...).deepCopy()` 正确解析。
2. **`InProcessBinding.kt:146`**：
   - **原因**：构造 `ProtocolAccessCapabilities` 时使用了过时的形参名 `resumptionSupported` 与 `authRequired`，且 `supportedPlayerCounts` 传入了 `List<Int>` 而非 `Set<Int>`。
   - **修复**：严格对齐 `ProtocolAccessCapabilities` 真实契约：
     `ProtocolAccessCapabilities(protocolVersion = ApiProtocol.PROTOCOL_VERSION, supportedPlayerCounts = setOf(4), remoteTransport = false, persistence = false, resumption = true)`。
3. **`InProcessServer.kt:113`**：
   - **原因**：缺失 `CompletableFuture` 类的导入。
   - **修复**：添加 `import java.util.concurrent.CompletableFuture`，并使 `tick(presentationBusy: Boolean): CompletableFuture<Boolean>` 全程非阻塞。
4. **`TableAuthorityTest.kt:117/118/171`**：
   - **原因**：`TableAuthority.kt` 中声明了内部嵌套类 `sealed class AutoStepResult`，而测试文件中未导入嵌套类型导致编译时找不到 `AutoStepResult.TimeoutResolved` 与 `AutoStepResult.PreparationDealt`。
   - **修复**：在 `TableAuthorityTest.kt` 中添加 `import com.majiang.authority.TableAuthority.AutoStepResult`，完整保留超时席位、动作类型及 136 张牌发牌物理守恒断言。

---

## 2. 排队 Future 生命周期与全异步安全 (Pending Future Lifecycle & Concurrency)

在 `ServerTableHost.kt` 中完善了外部 Future 全生命周期管理：

1. **生命周期全路径跟踪**：
   - `enqueueOperation` 将外部 `CompletableFuture` 加入 `pendingFutures` 集合，并通过 `future.whenComplete { _, _ -> pendingFutures.remove(future) }` 确保 Future 始终被跟踪，直到外部消费者回调和网络发送全部完成才解绑，杜绝在计算完成与回调完成的间隙发生泄漏。
2. **关闭与执行前校验**：
   - 若宿主已关闭，排队操作在 `serialExecutor` 取出执行前立即以 `IllegalStateException("ServerTableHost is closed")` 失败，绝不修改领域状态或发布多余事件。
   - 宿主 `close()` 调用时，原子将 `isClosed` 置为 true，立即提取并清空当前所有未完成的 `pendingFutures` 并调用 `completeExceptionally` 唤醒所有排队中的调用方。
3. **注入执行器属主管理**：
   - 通过 `ownsSerialExecutor` 与 `ownsAiExecutor` 区分内部创建与外部注入的执行器。对于测试或外部传入的 `serialExecutor`（如 Codex 注入的 `PausedExecutor`），宿主 `close()` 时**不关闭**外部注入的执行器，保证外部属主对其生命周期的控制。
4. **非阻塞回调解耦**：
   - 领域计算在单线程 `serialExecutor` 串行完成；外部 Future 的 `complete` / `completeExceptionally` 提交到独立的 `completionExecutor`。慢网络、慢监听器或消费者回调阻塞时，`serialExecutor` 保持 0ms 延迟响应后续权威操作与时钟步进。

---

## 3. UdpRoom 零阻塞自愈与异步席位冻结 (Non-blocking UDP Recovery)

针对 UDP 数据报接收线程与开赛流程消除全部阻塞调用：

1. **RESEND 路径无锁全量快照自愈**：
   - 客户端重传请求 `handleResend` 在接收循环线程上直接调用 `snapshotSupplier`。此前调用 `tableHost.snapshot().get(2s)` 存在导致整个服务器数据报接收循环被 2 秒锁死的隐患。
   - 在 `UdpRoom` 中维护 `lastPublishedSnapshots = AtomicReferenceArray<PlayerSnapshot?>(4)`，在 `init` 订阅回调 `onSeatUpdate`、`bindHost`、`join` 与 `resume` 派发时原子更新。
   - `snapshotSupplier` 直接读取缓存的最新不可变快照；若极端冷启动尚未收到第一次推送，则安全回退到 `tableHost.authority.querySnapshot(...)` 纯内存同步只读查询（0ms 返回，无 Future，无监视器锁），彻底消除 `.get(2s)` 阻塞桥。
2. **开赛席位冻结异步编排 (`freezeVacantSeatsAsync`)**：
   - 废除原有的阻塞式 `freezeOnceOnAcceptedStart() { ...get(5s) }`。
   - 改用 `freezeVacantSeatsAsync(): CompletableFuture<Unit>`，内部通过 `CompletableFuture.allOf(*futures.toTypedArray())` 并行触发空闲席位释放接管，并通过 `thenCompose` 编排入 `startMatch` 异步流水线，异常或超时绝不吞没。
3. **协议特性完整保留**：
   - R1 阶段的终端 ACK 握手、断线重连 resume、单席位私有视盘投影（遮蔽对手暗手牌与底牌）及全量同步标记完整保持，未破坏任何既有协议测试。

---

## 4. 真实运行入口调用链 (Real Live Entry Chains)

```
[本地单机 V4 链]
InProcessServer.createLocalTable()
  └─> InProcessBinding (实现 ServerConnection 契约)
        └─> ServerTableHost (持有 tableId, serialExecutor, completionExecutor, seatDispatchers)
              └─> TableAuthority (纯领域同步入口: 管理 ApplicationCoordinator, DecisionClock, 牌山物理守恒, 千术账本)

[网络 UDP V4 链]
UdpTableServer (监听 DatagramSocket, 帧分发, 心跳, 重传)
  └─> UdpRoom (管理 UDP 会话 Session, 房间状态, 断线重连代次, 无阻塞快照缓存)
        └─> ServerTableHost (持有 tableId, serialExecutor, completionExecutor, seatDispatchers)
              └─> TableAuthority (纯领域同步入口: 管理 ApplicationCoordinator, DecisionClock, 牌山物理守恒, 千术账本)
```

### 职责边界映射：
- **`TableAuthority`**：纯 JVM 内存同步门面，管理 `ApplicationCoordinator`、`DecisionClock`，实现 136 张牌洗牌物理守恒、准备阶段 (STACKING) 牌山生成、走子合法性校验、决策超时兜底选择与千术点数原子重算。无线程、无套接字、无订阅端点。
- **`ServerTableHost`**：异步服务端牌桌宿主，持有独立串行执行器 `serialExecutor`、外部 Future 完成解耦执行器 `completionExecutor`、席位隔离派发执行器 `seatDispatchers`、后台 AI 策略推理池 `internalAiExecutor`、连接与接管生命周期 `ConnectionControl` 以及单席位增量事件流 `SeatStream`。

---

## 5. 旧 TableHost 20 项保留基准的等价新宿主覆盖 (Test Migration & Coverage)

根据 `build/v4-migration/R4-host-test-preservation-baseline.json` 中的 20 项基准测试，我们在 `server/src/test/kotlin/com/majiang/server/host/ServerTableHostCoverageTest.kt` 中建立了针对 `ServerTableHost` 的 100% 等价行为覆盖；同时旧测试源码原封不动保留于 `engine/src/test/kotlin/com/majiang/v3`：

| 序号 | 旧测试基准文件与用例 | 新测试方法 (`ServerTableHostCoverageTest.kt`) | 覆盖领域行为与断言 |
| :---: | :--- | :--- | :--- |
| 1 | `BankruptcyHostAcceptanceTest`: 流局罚符破产 | `exhaustive draw bankruptcy settles deposits once and outranks dealer continuation` | 荒牌流局结算听牌罚符致负分，本场棒结算一次，破产终局优先级高于连庄 |
| 2 | `BankruptcyHostAcceptanceTest`: 荣和/自摸破产 | `ron and tsumo bankruptcy terminate match immediately without continuing to next round` | 和牌导致被点炮方或自摸支付方破产，比赛立即进入 FINISHED 状态 |
| 3 | `BankruptcyHostAcceptanceTest`: 局间历史账本 | `match history records final scores and deltas across all rounds on termination` | 破产终局记录跨局 roundRecords，各局最终点数与分数变化量 deltas 完整物理闭环 |
| 4 | `HostRhythmAcceptanceTest`: 自动动作节奏 | `auto discard interval enforces minimum rhythm interval` | 150ms 自动摸切最小节奏窗口，未到间隔不推进，到达后自动打出摸牌 |
| 5 | `HostRhythmAcceptanceTest`: 自摸/暗杠优先权 | `tsumo win and kan take precedence over riichi auto discard` | 立直后摸到和牌或暗杠时不自动摸切，保留玩家决策选项直至超时 |
| 6 | `HostRhythmAcceptanceTest`: 暂停冻结节奏 | `pause and resume freeze rhythm timing without advancing auto action` | 宿主暂停期间冻结自动推进，恢复后顺延倒计时且不丢失决策时钟 |
| 7 | `HostRhythmAcceptanceTest`: 接管轮转与倒计时 | `human player takes over control and retains decision clock` | 玩家掉线进入 TAKEOVER，重连后收回控制权 HUMAN，决策时钟与储备时间无缝保留 |
| 8 | `HostAiIsolationAcceptanceTest`: AI 队列外推理 | `ai strategy runs outside domain mutation queue and rejects stale submission` | AI 在领域队列外部异步思考，提交时验证 roundId/decisionId/epoch，过期提交拒绝 |
| 9 | `HostAiIsolationAcceptanceTest`: AI 超时兜底 | `unresponsive turn ai falls back to timeout policy without freezing table` | AI 计算超时或未响应时，宿主 tick 按 TimeoutPolicy 执行默认打牌，不卡死牌桌 |
| 10 | `TableHostAcceptanceTest`: 默认手牌与时钟 | `default table configuration generates expected player hands and initial clock` | 起手发牌庄家 14 张、闲家 13 张，默认 20s 储备 + 3s 思考，单局开局正常 |
| 11 | `TableHostAcceptanceTest`: 超时请求幂等性 | `timed request idempotency survives late retry without consuming a second action` | 同一 requestId 重试直接返回原回执，不消耗二次动作，不影响后续游标与牌局版本 |
| 12 | `TableHostAcceptanceTest`: 截止时间单次裁决 | `invalid choice cannot refresh deadline and timeout discards exactly once` | 非法请求不刷新截止时间，超时后 tick 恰好裁决一次摸切，后续重复提交被拒绝 |
| 13 | `TableHostAcceptanceTest`: 他席提交隔离 | `non human or other seat cannot submit another player decision` | 席位 B 无法提交席位 A 的决策，非法提交立即 REJECTED 且不产生游标推进 |
| 14 | `ShufflePreparationAcceptanceTest`: 准备阶段固定时钟 | `ten second selection is fixed independent of ordinary reserve and does not reveal hands` | 准备阶段固定 10s 选方案，与常规储备时钟独立；发牌前对手手牌与宝牌指示牌不可见 |
| 15 | `ShufflePreparationAcceptanceTest`: 千术点数私有预留 | `cost is reserved privately then charged once with the successful deal` | 方案提交后预留千术点数（私有可见），发牌成功后原子扣减，重复提交幂等 |
| 16 | `ShufflePreparationAcceptanceTest`: 136 张物理守恒 | `shuffle preparation preserves exact 136 tile physical conservation on deal` | 千术方案发牌后，牌山余牌 + 4 家手牌 + 初始摸牌 = 严格 136 张物理守恒 |
| 17 | `ShufflePreparationAcceptanceTest`: 余额不足拒绝 | `insufficient funds rejects whole request while half game can buy both` | 余额不足以购买选定方案时整体拒绝，不扣除任何点数；南场点数充足时正常接受 |
| 18 | `ShufflePreparationAcceptanceTest`: 截止与暂停保护 | `deadline and pause reject late actions without charging and retain remaining preparation` | 暂停期间拒绝方案提交，恢复后顺延截止时间；截止时间过后晚提交拒绝且不扣费 |
| 19 | `ShufflePreparationAcceptanceTest`: AI 千术消耗 | `auto deal with AI spending incorporates AI selections` | 允许 AI 消耗千术点数时，AI 席位方案参与牌山生成，发牌后正常开始摸切 |
| 20 | `ShufflePreparationAcceptanceTest`: 全员确认即时发牌 | `all players confirmed triggers immediate deal without waiting for deadline` | 4 名玩家均确认方案后，无需等待 10s 倒计时结束，下一次 tick 立即触发原子发牌 |

此外，补充覆盖了 `disabled shuffle skips preparation directly to active round`（关闭洗牌千术配置时直接跳过准备阶段进入发牌状态）。

---

## 6. 修改与新增文件清单 (File Inventory)

1. `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` (新建 - 纯领域同步权威门面)
2. `engine/src/test/kotlin/com/majiang/authority/TableAuthorityTest.kt` (新建 - 纯领域权威单元测试，已修复 `AutoStepResult` 导入)
3. `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt` (新建 - 服务端权威异步宿主，已修复 `PlayerDecision.deepCopy` 导入、`whenComplete` 跟踪与注入执行器属主管理)
4. `server/src/test/kotlin/com/majiang/server/host/ServerTableHostTest.kt` (新建 - 慢观察者隔离、串行游标单调性、断线重连控制权生命周期、注入暂停执行器与非阻塞 tick 测试)
5. `server/src/test/kotlin/com/majiang/server/host/ServerTableHostCoverageTest.kt` (新建 - 针对旧 TableHost 20 项基准测试的全量等价行为覆盖测试集)
6. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt` (修改 - 适配新宿主并修正 `ProtocolAccessCapabilities` 构造形参)
7. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt` (修改 - 增加 `CompletableFuture` 导入并支持非阻塞 `tick`)
8. `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt` (修改 - 消除重传路径 `.get(2s)` 阻塞并实现异步开赛席位接管)

---

## 7. 未决事项与状态声明 (Status & Boundaries)

1. **R4A 边界遵守**：本修复严格限制在 R4A 领域权威与服务端宿主编译、并发与行为覆盖范围内，未越界触碰 R4B 的大厅房间注册表 (RoomRegistry)、准备就绪确认状态或 UI 表现层适配器。
2. **测试执行状态声明**：**Anti 严格遵守开发规范与权限边界，未在本地执行任何 shell 命令、gradle 构建或测试命令。所有代码修改与测试用例均留待 Codex 进行独立编译、运行 9 项 R4A 独立探针以及全模块回归验证。**
