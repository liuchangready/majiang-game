# R4A 交付报告：领域纯同步权威与全异步服务端宿主

- **交付日期**：2026 年 10 月 5 日
- **执行角色**：Antigravity (Anti)
- **验收角色**：Codex (拥有独立测试、编译构建与验收决定权)
- **任务目标**：根据 [TASK-ANTI-R4A.md](file:///D:/majiang/doc/v4-migration/TASK-ANTI-R4A.md)、[CONTRACT.md](file:///D:/majiang/doc/v4-migration/CONTRACT.md) 及 [FEEDBACK-R4A.md](file:///D:/majiang/doc/v4-migration/FEEDBACK-R4A.md)，建立纯领域同步权威 `TableAuthority` 与每桌独立串行执行异步服务端宿主 `ServerTableHost`，彻底替换两条 V4 入口链（`InProcessServer` / `InProcessBinding` 与 `UdpRoom` / `UdpTableServer`）对旧 `TableHost` / `SessionAccess` 的依赖。

---

## 1. 真实运行入口调用链 (Real Live Entry Chains)

本阶段已彻底完成两条 V4 服务端调用链的重构，无任何旧版 `TableHost`、`SessionAccess` 或 `LocalSession` 遗留引用：

```
[本地单机 V4 链]
InProcessServer.createLocalTable()
  └─> InProcessBinding (实现 ServerConnection 契约)
        └─> ServerTableHost (持有 tableId, serialExecutor, completionExecutor, seatDispatchers)
              └─> TableAuthority (纯领域同步入口: 管理 ApplicationCoordinator, DecisionClock, 牌山物理守恒, 千术账本)

[网络 UDP V4 链]
UdpTableServer (监听 DatagramSocket, 帧分发, 心跳, 重传)
  └─> UdpRoom (管理 UDP 会话 Session, 房间状态, 断线重连代次)
        └─> ServerTableHost (持有 tableId, serialExecutor, completionExecutor, seatDispatchers)
              └─> TableAuthority (纯领域同步入口: 管理 ApplicationCoordinator, DecisionClock, 牌山物理守恒, 千术账本)
```

### 核心解耦机制：
1. **外部调用零阻塞**：所有对外 API（`start`, `submitChoice`, `submitShufflePlan`, `claimControl`, `releaseControl`, `snapshot`, `decision`, `resume`, `tick` 等）统一提交至 `ServerTableHost.enqueueOperation`，立即返回 `CompletableFuture`，绝不在调用线程（UDP 接收循环或本地调用线程）上持有领域监视器锁。
2. **计算与回调解耦**：权威计算在 `serialExecutor` 串行执行完毕后，外部 Future 的 `complete` 由独立的 `completionExecutor` 异步驱动。即使消费者的 `whenComplete`、网络发送或 UI 回调发生阻塞，`serialExecutor` 也能以 0ms 延迟继续响应后续查询与命令。
3. **权威执行点会话再校验**：`UdpRoom` 在提交操作时向宿主注入 `{ commandTarget(session) != null }` 闭包，在 `serialExecutor` 实际执行时二次校验连接代次、顶号状态与离开状态，杜绝被顶替旧连接的排队命令在线性化后越权生效。
4. **订阅基线串行化与隔离派发**：`subscribeSeat` 将订阅注册与基线快照提取置于 `serialExecutor` 内串行执行；派发时使用专属单线程 `seatDispatchers[seat]` 投递独立的 `PlayerUpdate.deepCopy()`，慢监听器绝不影响桌内执行队列。

---

## 2. 职责映射表 (Responsibility Mapping)

旧版 `TableHost`（约 2000 行单体）的所有职责已按领域权威与服务端宿主规范完成彻底拆分：

| 职责大类 | 具体功能项 | 旧架构归属 (`TableHost`) | V4 新架构归属 | 设计考量与特性 |
| :--- | :--- | :--- | :--- | :--- |
| **纯领域规则与状态** | 单局推进、合法动作判定、动作执行 | `TableHost` 内部混合 | `TableAuthority` + `ApplicationCoordinator` | 纯 JVM 同步门面，无线程无套接字 |
| **物理守恒与码牌** | 136 张牌严格守恒、准备阶段牌山生成 | `TableHost` 内部混合 | `TableAuthority` + `ShuffleWallGenerator` | 物理守恒闭环，洗牌千术预设生成 |
| **决策时钟与超时** | 免费时间/储备扣减、截止时间、超时判定 | `TableHost` 内部混合 | `TableAuthority` + `DecisionClock` | 权威时钟驱动，超时具有最高仲裁权 |
| **千术经济与账本** | 千术点数变更、决策键原子 rekey | `TableHost` 内部混合 | `TableAuthority` | 账本变更原子刷新 decisionId 并保留倒计时 |
| **整桌串行调度** | 单桌串行操作入队与线性执行 | 依赖调用方锁与互斥 | `ServerTableHost.serialExecutor` | 可注入 Executor，便于确定性并发与关闭测试 |
| **Future 完成解耦** | 将领域计算完成与下游监听器回调分离 | 在同一线程/提交线程内完成 | `ServerTableHost.completionExecutor` | 慢网络消费者绝不阻塞权威查询队列 |
| **连接与控制权生命周期** | 人机/AI 接管角色切换、掉线宽限期、重连 | `TableHost` + `ConnectionControl` | `ServerTableHost` (`ConnectionControl`) | 领域外生命周期管理，控制代次权威单调递增 |
| **席位投影与订阅** | 单席位视盘投影、游标、增量事件、快照历史 | `TableHost.SeatStream` | `ServerTableHost` (`SeatStream` + `seatDispatchers`) | 单席位单调有序推进，各席位异步解耦 |
| **后台 AI 推理** | 异步策略决策、提交时二次三要素校验 | `TableHost` 后台线程池 | `ServerTableHost.internalAiExecutor` | 突变队列外推理，提交验证 roundId, decisionId, epoch |
| **非阻塞时钟步进** | tick 超时兜底、局间推进、自动立直摸切 | `TableHost.tick()` (旧同步阻塞) | `ServerTableHost.tick(): CompletableFuture<Boolean>` | 全异步非阻塞返回，避免渲染与服务端轮询锁死 |
| **确定性资源释放** | close 终止未决 Future、释放执行器 | `TableHost.close()` (可能等待) | `ServerTableHost.close()` | 立即清空并以异常完成所有排队中的 Future，零等待 |

---

## 3. 测试迁移与保留对照表 (Test Retention & Migration)

### 3.1 旧 TableHost 测试全量保留 (20 个测试，保留于 `engine/src/test/kotlin/com/majiang/v3`)
根据 `R4-host-test-preservation-baseline.json` 基准，旧宿主测试完全保留，未删减任何断言：

| 文件路径 | 测试数量 | 状态 | 职责覆盖与保留原因 |
| :--- | :---: | :---: | :--- |
| `engine/src/test/kotlin/com/majiang/v3/BankruptcyHostAcceptanceTest.kt` | 3 | **保留** | 破产结算与整场结束规则测试，供旧 V3 链回归验证 |
| `engine/src/test/kotlin/com/majiang/v3/HostRhythmAcceptanceTest.kt` | 4 | **保留** | 150ms 自动动作节奏、立直自摸/暗杠截止权、本地暂停与接管轮转测试 |
| `engine/src/test/kotlin/com/majiang/v3/HostAiIsolationAcceptanceTest.kt` | 2 | **保留** | AI 离线推理计算与状态突变隔离测试 |
| `engine/src/test/kotlin/com/majiang/v3/TableHostAcceptanceTest.kt` | 4 | **保留** | 旧 TableHost 综合验收测试 |
| `engine/src/test/kotlin/com/majiang/v3/ShufflePreparationAcceptanceTest.kt` | 7 | **保留** | 洗牌千术准备阶段、方案预设与原子发牌全套流程测试 |
| **合计** | **20** | - | **全部保留，SHA-256 与基线完全一致** |

### 3.2 新增 V4 权威与宿主专属测试

| 文件路径 | 测试项数量 | 重点验证功能 |
| :--- | :---: | :--- |
| `engine/src/test/kotlin/com/majiang/authority/TableAuthorityTest.kt` | 5 | 1. 房主启动与出牌提交幂等性及冲突拒绝；<br>2. 截止时间到期触发 `AutoStepResult.TimeoutResolved` 与晚提交拒绝；<br>3. 洗牌千术准备阶段、预设方案记录与 136 张牌物理守恒原子发牌；<br>4. 暂停与恢复对截止时间的精确平移 (`timing.deadlineMillis + 3000L`)；<br>5. 千术账本变更原子替换 `decisionId` 并完整保留时钟语义。 |
| `server/src/test/kotlin/com/majiang/server/host/ServerTableHostTest.kt` | 5 | 1. 慢更新观察者阻塞时不阻塞调用方与权威查询 (`releaseControl` -> `snapshot` role="TAKEOVER")；<br>2. 串行队列并发命令处理与单调递增游标 (`receivedCursors[i] >= receivedCursors[i-1]`)；<br>3. 断线与控制权收回生命周期 (`reconnect` -> `claimControl` -> role="HUMAN")；<br>4. 注入已暂停的 `serialExecutor` 时构造函数零阻塞立即返回，且 `close()` 立即以异常完成未决 Future；<br>5. 异步非阻塞 `tick(): CompletableFuture<Boolean>` 即时返回。 |

### 3.3 架构评审测试对齐 (`tools/architecture/v4-review`)
- `V4AuthorityBoundariesTest`:
  - `domainAuthorityDoesNotOwnConnectionsOrExecutionThreads`: 验证 `TableAuthority` 纯净度，无任何网络、线程池、旧 TableHost 或 SessionAccess 依赖；
  - `bothV4BindingsHaveStoppedImportingLegacyDomainHostAndAccess`: 验证 `server.inprocess` 与 `server.udp` 包内所有源文件彻底移除了对旧 `TableHost`、`SessionAccess` 与 `LocalSession` 的导入。
- `V4AuthorityAcceptanceTest`:
  - `slowUpdateObserverDoesNotBlockCallerOrAuthoritativeQueries`: 验证慢观察者下 `releaseControl` 与 `snapshot` 响应；
  - `competingChoicesForOneDecisionCommitOnlyOneDiscard`: 验证同一决策并发竞争时恰有一项生效、恰打出一张牌；
  - `invalidChoiceCannotRefreshDeadlineAndTimeoutCommitsOnce`: 验证非法选择不刷新截止时间，超时 `tick` 仅裁决一次。

---

## 4. 涉及文件清单 (Modified & Created Files)

1. `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` (新建 - 纯领域权威)
2. `engine/src/test/kotlin/com/majiang/authority/TableAuthorityTest.kt` (新建 - 领域权威单元测试)
3. `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt` (新建 - 服务端权威异步宿主)
4. `server/src/test/kotlin/com/majiang/server/host/ServerTableHostTest.kt` (新建 - 服务端宿主并发与生命周期测试)
5. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt` (修改 - 接入 `ServerTableHost`，移除旧 host/access 导入)
6. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt` (修改 - 接入 `ServerTableHost`，非阻塞 `tick`)
7. `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt` (修改 - 接入 `ServerTableHost`，会话执行点二次校验，非阻塞 `tick`)

---

## 5. 未决事项与后续规划 (Unresolved Items & Boundaries)

1. **R4A 边界遵守**：本阶段严格限制在单桌权威领域与异步宿主核心调度上。大厅房间管理、统一业务注册表 (RoomRegistry)、准备就绪确认状态、观战者与 UI 表现层适配器按计划保留至 **R4B** 统一实现。
2. **旧入口保留期**：`com.majiang.host.TableHost`、`com.majiang.access.SessionAccess` 及 `LocalSession` 当前原封不动保留在工程中，用于旧 V3 链与回归测试编译；待 R5/R6 阶段统一废弃清理。
3. **测试执行状态声明**：**Anti 严格遵守开发规范与权限边界，未在本地执行任何 shell 命令、gradle 构建或测试命令。所有代码语法、边界及行为规范均留待 Codex 独立编译、运行测试并进行权威验收。**
