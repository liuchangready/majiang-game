# MR-06 房间回收、观战上限与建房准入 — 验收记录 (CLAUDE-MR06-REVIEW.md)

- **验收方**: Claude Code 主会话（独立检视、代跑构建与测试）
- **开发方**: Anti（Antigravity，经 agent-mesh-bridge 派发）
- **基线 SHA**: 07d6aae（feat/mj-tcp-migration，TCP 迁移 T0–T5 已关闭）
- **需求依据**: doc/v4-重构/IMPROVEMENT-PLAN.md 第 4 节 MR-06；简报 tools/architecture/v4-improvement/ANTI-MR06-TASK.txt
- **交付说明**: doc/v4-重构/ANTI-MR06-DELIVERY.md（含第 5–11 轮返修记录）
- **验收日期**: 2026-10-17
- **结论**: **通过**（模块测试全绿；两条已知事项见第 4 节，均不阻塞）

---

## 1. 交付范围核对

| 文件 | 类型 | 核对结果 |
|---|---|---|
| server/room/RoomPolicies.kt | 新建 | RoomRetentionPolicy（LOBBY 5min/进行中 30min/FINISHED 10min/未参与 60s/巡检 5s）+ RoomCapacityPolicy（观战 32/IP 3/身份 3/速率 10s⁻¹）+ 顶层纯函数 ttlFor；全部拟定默认值、init 校验、KDoc 标注可配置 |
| server/tcp/CreateRoomAdmission.kt | 新建 | 单锁内原子四检查（全局容量/单 IP 未开局/单身份未开局/滑动窗口速率）；commit/release 恰一次（CAS）；对账清账 reconcile + 显式 onRoomClosed |
| server/room/SharedRoom.kt | 修改 | 连接状态跟踪（seatConnected/SpectatorMember.isConnected）、idleSinceMillis/idleEpoch、markSeatConnected/markSpectatorConnected、reconcileConnectivityOnQueue、closeIfIdleOnQueue（权威队列终审）、观战上限（占位口径）、close 时旋转 token；everConnectedHuman 仅在传输确认/单人 init 置位 |
| server/room/RoomRegistry.kt | 修改 | 仅注入 capacityPolicy 透传，行为不变 |
| server/tcp/TcpServerConfig.kt | 修改 | retentionPolicy/capacityPolicy 两个可注入配置 |
| server/tcp/TcpTableServer.kt | 修改 | RoomPatrol 独立调度器（close 500ms 纪律）、handleCreateRoom 原子预留（RateLimited→SERVER_BUSY / QuotaExceeded→SERVER_FULL）、Replay 回执新鲜度联动（保留回执、改发 ROOM_NOT_FOUND）、bindSpectatorRole 传真实代次、巡检对账兜底、onRoomRecycled 清理链 |
| protocol ApiErrors | 未动 | 复用 ROOM_FULL/SERVER_FULL/SERVER_BUSY/ROOM_NOT_FOUND，无新错误码（符合简报优先复用要求） |
| server/inprocess | 未动 | InProcess 行为不变（测试证实） |
| 测试 | 新建 RoomRecyclingTest(10 用例)/CreateRoomAdmissionTest(6 用例)；追加 TcpTableServerCapacityAndLifecycleTest(2 用例)；SharedRoomTest 追加用例在返修中按建议移除（与 RoomRecyclingTest T5 重复） | 未删除/削弱任何既有测试 |

## 2. R06-T1～T14 逐条验收结论

| 编号 | 验收项 | 结果 | 载荷 |
|---|---|---|---|
| T1 | TTL 边界（TTL-1 不回收/TTL 到回收；LOBBY/进行中/FINISHED/unengaged） | ✅ | RoomRecyclingTest T1：LOBBY/PLAYING 集成驱动；FINISHED/unengaged 经 ttlFor 纯函数直测（GameState 无法直接构造 FINISHED，集成面弱于其他档，已如记录） |
| T2 | resume/join 与回收竞争在权威队列串行裁决 | ✅ | T2 Case A（resume 先入队→不回收+空闲清除）/Case B（回收先入队→resume 得 ROOM_NOT_FOUND） |
| T3 | 仍有观战者（含断线占位）不回收 | ✅ | T3 全链路 |
| T4 | AI tick 不延长无人房间寿命 | ✅ | T4：50 次 tick 断言 idleSince 不变，TTL 到照常回收 |
| T5 | 第 33 个观战者拒绝/断线占位/resume 不受限/leave 释放 | ✅ | RoomRecyclingTest T5 |
| T6 | 单 IP 配额 3、开局/关闭释放 | ✅ | CreateRoomAdmissionTest T6 |
| T7 | 多 IP 耗尽全局预算、注册表数量精确 | ✅ | T7 |
| T8 | 共享 NAT：IP 与身份配额取更严者 | ✅ | T8 |
| T9 | 并发预留不超额、release 后名额可复用 | ✅ | T9（20 线程并发恰 10 成功） |
| T10 | 断线重试 Replay 原结果；回收后 Replay 得 ROOM_NOT_FOUND 且不新建房 | ✅ | CapacityAndLifecycleTest R06-T10（真实 TCP e2e） |
| T11 | 已回收房旧 token 不可复活、无效恢复身份拒绝 | ✅ | T11 + R5 e2e 第 6 步 |
| T12 | 循环回收后注册表/记账/配额归零 | ✅ | T12（5 轮建房-回收） |
| T13 | InProcess 不因无网络连接被回收 | ✅ | T13 smoke + InProcessBindingTest/InProcessDualTransportTest 全部既有用例通过 |
| T14 | 速率预算超频 SERVER_BUSY、窗口滑动恢复 | ✅ | T14（注入时钟） |
| 回归 R2 | 时钟基准统一（registry.clock） | ✅ | R2 回归：无参 performRoomPatrol() 钉住默认时钟源（config.clock 保持真实墙钟时不得误回收） |
| 回归 R3 | 观战 resume 后断开正确清 isConnected | ✅ | R3 回归（真实 TCP join→断→resume→再断） |
| 回归 R4 | join→bind 窗口死亡经巡检对账回收 | ✅ | R4 回归 |
| 回归 R5 | 巡检端到端（筛选→队列终审→注销→ROOM_CLOSED→admission 清账） | ✅ | R5 集成（真实 TcpTestClient） |

## 3. 验证执行记录（绑定当前工作区）

1. `./gradlew :protocol:test :server:test --console=plain` → **BUILD SUCCESSFUL**，server 188 项全部通过（含既有 SharedRoomTest/TcpTableServer*/InProcess*/契约测试零回归 + 上述 18 项新验收/回归用例）。
2. 中途处置：一次 Gradle 增量缓存损坏（TcpTableServerHandshakeTest.class MD5 失败、多 daemon 竞争），按 IMPROVEMENT-PLAN §6.1 以 `--stop` + 清理损坏输出目录解决，未改任何业务代码。
3. 独立审查套件（v4-review init）：**无法编译**——基线既有问题，见第 4 节事项 2。

## 4. 已知事项与后续跟进（不阻塞本 MR）

1. **域错误映射仅覆盖 resumeSession**：宿主拆除后迟到请求的 IllegalStateException→ROOM_NOT_FOUND 映射（exceptionally）只加在 resumeSession，未按指令同步到 join/joinSpectator/resumeSpectator。评估：TCP 层四个入口均先查 registry（getRoom 为 null/closed → ROOM_NOT_FOUND），生产路径不可达该缺口；队列内 isClosed 域检查仍覆盖"入队后关闭"窗口。建议在 MR-07 或 D-08 中统一为 enqueueRoomOperation 包装。
2. **独立审查套件自 T5 起失效**：tools/architecture/v4-review/*.kt 引用已删除的 UDP 类（UdpTableServer/UdpRoom/UdpClient），最后一次修改停在 8a4b59f（MR-02），T5 的"零 UDP 引用"审计只覆盖 src/main+src/test。本 MR 期间 tools/ 零改动（git diff 证实）。需单独立项把审查用例移植到 TCP，恢复阶段出口的独立验证能力——在移植完成前，涉及网络层的阶段出口验证只能依赖标准模块测试。
3. **FINISHED 档集成覆盖弱**：仅 ttlFor 纯函数直测；若后续 GameState 提供可构造的终态入口（或 D-06 拆分时），补一条真实驱动的集成用例。

## 5. 过程记录（供后续 MR 参考）

共 8 轮验收迭代。发现并修复的主要缺陷：生产时钟基准混用（巡检墙钟 vs 房间单调钟，会导致所有空闲房间 5 秒内被误回收）、观战 resume 后代次断裂致房间永不回收、join→bind 窗口死亡卡死、MR-03 回执 Replay 泄漏已回收 roomId、everConnectedHuman 乐观置位语义错误、连续 4 轮编译失败（幻觉 API/缺 import/作用域）、迟到 resume 域错误映射。返修指令从描述级逐步收紧到逐字级 + 强制回读自查后交付质量才稳定。无头约束下 Anti 无法自证编译，验收方"先跑编译再深审"应作为固定流程。
