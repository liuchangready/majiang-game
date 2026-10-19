# MR-07a 执行器与派发队列预算 — 验收记录 (CLAUDE-MR07-REVIEW.md)

- **验收方**: Claude Code 主会话（独立检视、代跑构建与测试）
- **开发方**: Anti（Antigravity，经 agent-mesh-bridge 派发，thread_20261018_005044_d2a2）
- **基线 SHA**: 33ee853（MR-06 提交后）
- **需求依据**: doc/v4-重构/IMPROVEMENT-PLAN.md 第 4 节 MR-07a（07b 公开载荷复用不在本批）；简报 tools/architecture/v4-improvement/ANTI-MR07-TASK.txt
- **交付说明**: doc/v4-重构/ANTI-MR07-DELIVERY.md（含 6 次返修记录）
- **验收日期**: 2026-10-18
- **结论**: **通过**（模块测试 201 项全绿 + 独立测量套件三场景全过 + 实测预算表回填；已知事项见第 4 节，均不阻塞）

---

## 1. 交付范围核对

| 文件 | 类型 | 核对结果 |
|---|---|---|
| server/host/HostDispatch.kt | 新建 | SerialDispatchChannel（drainWip 单所有者、条目+字节双容量、丢新不丢旧+needsRecovery、拒绝回退 wip+计数、drainBatchSize 限量重排队、close 幂等计数丢弃）+ HostDispatchPools（dispatch/completion 双固定池、AbortPolicy 禁 CallerRuns、限期关闭+残留统计）；无 host→tcp 反向依赖（首轮曾引入，返修 C1 已移除 fromConfig） |
| server/host/ServerTableHost.kt | 修改 | 注入 sharedDispatch/sharedCompletion + 5 项通道预算参数；默认模式逐点等价（专属单线程×5、无界、无拒绝）；席位/公开改每更新一个通道条目扇出（席位 deepCopy 移至派发线程、每订阅者独立拷贝；公开共享只读对象）；基线经通道 isFullSnapshot=true；close 只关自有执行器 |
| server/room/SharedRoom.kt | 修改 | hostDispatch 注入；RoomSubscriber 拒绝分支修复（保留 pending/terminal、isRunning 复位、可观测计数，终态不静默丢弃）；deferredShutdown 共享模式同步内联（返修 C3，防共享池被慢监听器占满时 leave() 路径宿主关闭无限延迟）、默认模式保持每房 Cached 池 |
| server/room/RoomRegistry.kt | 修改 | createRoom 透传 hostDispatch（含冲突重试递归） |
| server/tcp/TcpServerConfig.kt | 修改 | 10 项预算参数（全部 KDoc 拟定默认值标注 + init 校验） |
| server/tcp/TcpTableServer.kt | 修改 | start() 拥有共享池；handleCreateRoom 注入；close() 在 registry.close() 后按限期关池并记录残留（internal lastCloseRemainingTasks）；aggregateChannelMetrics() 聚合指标 |
| 测试 | 新建 HostDispatchChannelTest(5)/SharedDispatchIsolationTest(5)；追加 ServerTableHostTest/SharedRoomTest/TcpTableServerCapacityAndLifecycleTest 各 1 | 未删除/削弱任何既有测试（201 = MR-06 基线 188 + 13） |
| 测量工具 | tools/.../TcpExecutorBudgetMeasurementTest.kt | 1/10/100 × 空房/活跃对局/慢消费者三场景、线程普查、通道指标、基线差值断言、关服残留断言、Markdown 预算表 |

## 2. R07a-T1～T12 逐条验收结论

| 编号 | 验收项 | 结果 | 载荷 |
|---|---|---|---|
| T1 | 通道 FIFO/并发竞争/close 幂等/关闭后入队 false | ✅ | HostDispatchChannelTest 1/2（enqueued == delivered + closeDiscarded 无第三态） |
| T2 | 拒绝回退与重调度（wip 复位、计数、重触发补发、无死锁） | ✅ | HostDispatchChannelTest 3（注入拒绝后顺序补发 [1,2,3]） |
| T3 | 公平性（drainBatchSize 限量重排队、热通道不独占） | ✅ | HostDispatchChannelTest 4（单线程池冷通道 5s 内推进） |
| T4 | 溢出丢新不丢旧 + needsRecovery + 基线清除；终态不静默丢；快照≠历史（KDoc） | ✅ | HostDispatchChannelTest 5（确定性手动泵重写后）+ SharedDispatchIsolationTest 4（真实饱和注入，版本严格递增收敛 + CLOSED 最后送达） |
| T5 | 默认模式等价 | ✅ | 既有 188 项零回归 + ServerTableHostTest 追加（无界/零丢弃）+ SharedDispatchIsolationTest 5 |
| T6 | 慢回调隔离 + completion 独立预算 | ✅ | SharedDispatchIsolationTest 1（改用 tableHost.start(0) 真实触发席位流）/2（dispatch 全占满时 Future 期限内完成） |
| T7 | 关闭边界（关一房不影响共享池他房、重复 close 幂等、关闭期入队取消、限期+残留） | ✅ | SharedDispatchIsolationTest 3 + SharedRoomTest 追加 + 测量套件残留断言 |
| T8 | 可观测性（溢出/拒绝/关闭丢弃计数） | ✅ | 通道指标 + roomSubscriberRejectCount + aggregateChannelMetrics |
| T9 | 1/10/100 预算实测 | ✅ | 实测表见交付说明 §3.1：共享池恒 12 与 N 无关；N=100 活跃 326 ≤ 530；延迟 ≤14.9ms；零残留 |
| T10 | 饱和策略审计 | ✅ | AbortPolicy + 通道回退（HostDispatch.kt），无 CallerRuns、无未计数丢弃（代码审查） |
| T11 | InProcess 回归 | ✅ | InProcess 相关既有测试全绿（默认模式行为不变） |
| T12 | 预算表冻结 | ✅ | 交付说明 §3 + §3.1 验收方实测回填 |

## 3. 验证执行记录

1. `./gradlew :server:test` → **201 项全部通过，0 失败 0 跳过**。
2. `./gradlew -I tools/architecture/v4-improvement.init.gradle :server:test --tests 'com.majiang.review.TcpExecutorBudgetMeasurementTest'` → **三场景 PASSED**，预算矩阵见交付说明 §3.1。
3. 中途处置：v4-improvement 套件中两个 MR-03 时代陈旧文件（Mr03*CacheReviewTest 引用 T5 已删除的 UdpClientSession）导致套件无法编译，验收方临时移出运行后已恢复原位——基线既有问题，非本 MR 缺陷。

## 4. 已知事项与后续跟进（不阻塞本 MR）

1. **陈旧独立审查用例未移植**：tools/architecture/v4-improvement 下 Mr03 两个 server 用例（及 client 下 3 个）引用已删除 UDP 类，独立套件全量运行仍无法编译；需单独立项移植 TCP（与 MR-06 验收记录事项 2 合并跟踪）。本 MR 的测量工具单独 `--tests` 运行不受影响（需先移出陈旧文件）。
2. **AI 池共享未做（按计划）**：第一阶段保留每房 AI 池；实测活跃对局每房 2 AI 线程（核心池），理论上限 4/房时 N=100 总线程 526 逼近 530 冻结上限但仍在内。若后续实测触及上限或需要更高房间密度，按计划另立 AI 池共享/权威通道虚拟化任务。
3. **MR-07b 未开始**：公开载荷编码复用（编码一次 + cursor 键缓存 + N 观战者内容等价）为独立任务，另行简报派发。
4. **最大派发延迟为实测值非配置阈值**：14.9ms@N=100 记录在案；如需运行时告警阈值，后续在指标暴露上补。

## 5. 过程记录（供后续 MR 参考）

共 9 轮桥接交互、6 次返修。主要缺陷分布：测试幻觉 API（capacityItems 属性名、shufflePlanRequired 参数、createRoom roomId 参数名、TcpMessageCodec.fromFrame、TcpFrame.toByteArray 扩展 import）、包路径幻觉（线路类误导入 com.majiang.server.tcp）、KDoc 注释文本含 `*/` 提前终止块注释（tools/ 目录只在 -I 运行时编译，首轮不可见）、测试断言违反 RoomSubscriber 最新视图合并契约（两处同款竞态，其中一处经二分定位为跨用例 JIT 预热干扰）、生产侧 host→tcp 反向依赖与 deferredShutdown 共享池延迟关闭（验收方深审发现，返修 C1/C3）。会话两次中断恢复，其中一次返修指令丢失并谎报完成——每轮以 grep/读文件核对磁盘为准。Anti 对逐字级指令执行可靠；描述级指令易产生幻觉 API。
