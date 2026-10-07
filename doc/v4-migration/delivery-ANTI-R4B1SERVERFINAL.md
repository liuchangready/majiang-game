# 交付报告：R4B1SERVERFINAL (Server UDP 生命周期收尾与边界清理)

## 1. 任务概述

本任务为针对 `doc/v4-migration/FEEDBACK-R4B1SERVERFINAL.md` 提出的全部 3 项收尾审查证据与边界完善项的专门交付。
严格遵循文件所有权范围：仅修改 Server UDP 模块及其对应单元测试。未修改 client、protocol、reviewer 测试或构建脚本。不包含任何旧 V3 兼容别名。

---

## 2. 审查项逐项修复说明

### Item 1：统一在途房间更新所有权清理与定时器取消，杜绝孤儿重发
- **根因分析**：
  原实现中 `LEAVE_ROOM`、`sendKick`、`checkSessionTimeouts` 等路径先执行了 `inFlightRoomUpdates.remove(id)`，再将返回的条目传入 `purgeRoomUpdate(entry)`。而 `purgeRoomUpdate` 内部再次执行 `inFlightRoomUpdates.remove(entry.connectionId, entry)`，由于条目此前已被无条件移除，此处的条件移除必然返回 `false`，导致 `entry.retryCancellable?.cancel()` 与 `entry.future.complete(Unit)` 均未被触发，且可能导致调度器中遗留未取消的孤儿重试任务。
- **修复方案**：
  在 [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) 中：
  1. 拆分并规范化清理函数职责：
     - `terminateRoomUpdate(entry)`：原子取消 `retryCancellable` 并触发 `future.complete(Unit)`。
     - `purgeRoomUpdate(entry)`：仅在 `inFlightRoomUpdates[connId] === entry` 匹配时安全 CAS 移除并调用 `terminateRoomUpdate`，用于 `handleAck` 及条目自确认场景，确保旧 ACK 绝不会误清理更新的在途条目。
     - `cancelAndPurgeRoomUpdate(connectionId)`：原子移除指定连接当前在途的最新条目，并在移除成功时直接调用 `terminateRoomUpdate`，杜绝二次移除判断失效的问题。
  2. 调度器防护：`scheduleRoomUpdateRetry` 在注册到 `effectiveScheduler` 后增加瞬态防御，若注册前/注册期间条目已被取消或替换，立即调用 `cancellable.cancel()`。
  3. 全生命周期覆盖：在 `LEAVE_ROOM`、`sendKick`、`checkSessionTimeouts`、`close` 全部路径中统一调用 `cancelAndPurgeRoomUpdate`。
- **测试覆盖**：
  在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中使用 `FakeUdpClock` 与 `FakeUdpScheduler` 补充 4 个确定性单测：
  - `testRoomUpdateCancellationAndNoOrphanRetryOnLeave`
  - `testRoomUpdateCancellationAndNoOrphanRetryOnKick`
  - `testRoomUpdateCancellationAndNoOrphanRetryOnTimeout`
  - `testRoomUpdateCancellationAndNoOrphanRetryOnClose`
  均断言 `entry.future.isDone` 为 `true`、在途与 watermark 记录为 `null`、调度器中待发任务数为 0，且推进时间后绝无孤儿重试触发。

---

### Item 2：会话生命周期与待发状态原子联动，消除并发重注册与锁分裂
- **根因分析**：
  原 `roomUpdateLocks` 采用全局 `ConcurrentHashMap`，在连接退出/踢出时直接执行 `roomUpdateLocks.remove(connId)`。若并发房间更新回调在移除前后分别执行 `computeIfAbsent`，将获取到不同的锁对象，丧失临界区互斥保证；且原 `sendRoomUpdate` 仅在入口检查 `session.isClosed`，进入临界区后未复核，导致处于销毁边缘的连接被旧回调重新创建 watermark、在途条目与定时器。
- **修复方案**：
  在 [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) 中：
  1. 废弃易导致锁分裂的全局 `roomUpdateLocks` 映射，统一直接使用 `session: UdpClientSession` 本身作为会话锁监视器（`synchronized(session)`）。锁随 `session` 实例天然生命周期收敛，会话 GC 时自动回收，无需引入无界 tombstone。
  2. 在 `sendRoomUpdate` 的 `synchronized(session)` 临界区内部，严格复核连接存活状态：
     `if (session.isClosed || session.isDisplaced || isClosed.get() || sessionsByConnectionId[connId] !== session || inFlightKicks.containsKey(connId)) return`
     保证已关闭、已顶号、被踢除或已从活跃映射移除的连接绝无法重新注册新条目或重发定时器。
  3. 保持外部网络 Socket 发送、`cancellable.cancel()` 及 `future.complete()` 严格在锁外执行，稳定 seq 分配与最新完整视图推送机制保持不变。
- **测试覆盖**：
  在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中增加多线程屏障交错测试：
  - `testSendRoomUpdateInterleavedWithSessionTeardownDoesNotRecreateEntry`：使用 `CyclicBarrier` 构造高并发下 `sendRoomUpdate` 与超时销毁 `checkSessionTimeouts` 的交错竞态，验证销毁后在途条目、watermark 及调度器定时器严格清零，绝无任何残留重注册。

---

### Item 3：LEAVE_ROOM 拒绝状态保持会话有效绑定，仅权威 APPLIED 撤销会话
- **根因分析**：
  原 `UdpTableServer` 在处理 `ApiTags.LEAVE_ROOM` 时，无论 `receipt.status` 是否为 `REJECTED` 均执行了 `sessionsByConnectionId.remove` 并关闭会话，且在 `err` 分支无条件关闭会话。导致合法真人成员在收到拒绝回执后，其服务端传输连接却被错误销毁。
- **修复方案**：
  1. 在 [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt) 中：
     `leave` 改用 `whenComplete` 确保在异常或非 `APPLIED` 时原子回滚 `leavingConnectionIds` 与 `leaveExclusion`。
  2. 在 [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) 中：
     - 当 `receipt.status == ProtocolReceiptStatus.APPLIED` 时：先记录终止会话并回发 `APPLIED` 响应包，再执行 `terminateSession(session)` 撤销传输绑定。
     - 当 `receipt.status == ProtocolReceiptStatus.REJECTED` 时：仅通过 `replySession` 返回携带具体拒绝原因的响应回执，**绝不移除会话，绝不关闭 session**，保持原席位有效绑定。
     - 当 `err != null` 时：仅在房间已关闭（`ROOM_NOT_FOUND` 或 `room.closed`）时执行清理，其余异常保持会话可用。
- **测试覆盖**：
  在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中增加测试：
  - `testLeaveRoomRejectedPreservesActiveBindingAndSession`：验证因过时代次被权威拒绝离房时，真人会话未被关闭（`isClosed == false`），服务端仍完整保留会话，且该会话可继续正常执行后续业务请求（如 `GET_ROOM_INFO`）；直至提交合法离房后方才注销关闭。

---

## 3. 修改文件清单

| 文件路径 | 修改性质 | 主要说明 |
|---|---|---|
| `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt` | 生产代码 | `leave` 采用 `whenComplete` 保证异常/拒绝时回滚 `leavingConnectionIds` 与豁免状态 |
| `server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt` | 生产代码 | 联动 session 监视器锁防并发重注册；拆分 `cancelAndPurgeRoomUpdate` / `terminateSession`；`LEAVE_ROOM` 仅 APPLIED 关闭会话；添加 `registerSession` 辅助测试 |
| `server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt` | 测试代码 | 增加 6 个确定性单元回归测试，全覆盖 leave/kick/timeout/close 路径的取消完成、屏障交错与拒绝离房会话保持 |

---

## 4. 未解决项与遗留风险

- 审查报告 `FEEDBACK-R4B1SERVERFINAL.md` 中列出的全部 3 项收尾要求均已彻底修复并补充模块回归测试。
- 本任务未引入任何破坏性重构，保留了既有正负 connectionId 离房、异步顶号、最新版本原子替换全部能力。
- 未解决项：无。

---

## 5. 测试执行声明

> **声明**：按照本次协同工作流与授权约定，开发端（Anti）仅使用内置文件读写/编辑工具进行源码修改，**未在本地执行 Gradle 构建与自动化测试**。
> 所有编译、构建、静态检查与自动化验收测试统一由监管端（Codex）独立串行执行与验证。
