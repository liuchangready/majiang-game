# 交付报告：R4B1ROOMFIX2 共享房间与连接修复

**执行人**：Antigravity (Anti)  
**架构与验收负责人**：Codex  
**日期**：2026-10-05  
**对应任务**：`doc/v4-migration/TASK-ANTI-R4B1ROOMFIX2.md` 与 `doc/v4-migration/FEEDBACK-R4B1ROOMFIX2.md`  

---

## 1. 交付文件清单与核心修复

### 生产代码 (server 模块)
1. [`server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt)
   - **修复枚举引用与重连原语**：将不存在的 `ControllerRole.LOCAL_HUMAN` 纠正为 `ControllerRole.HUMAN`；提供队列内重连原语 `performReconnectOnQueue(seat, reclaim)`。
   - **收敛窄接口快照查询**：`performQuerySnapshotOnQueue(seat)` 强制校验 `seat in 0..3`，直接使用该席位权威流的 `syncEpoch: String` 与单调权威游标 `stream.cursor`，彻底移除调用方传入的游标参数（调用端 cursor 仅代表客户端基线，绝不能作为权威状态标识）。

2. [`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt)
   - **队列内断线席位调度**：新增 `disconnectSeat(seat: Int, expectedSeatGeneration: Long): CompletableFuture<Unit>`，将席位断开操作入队至 `tableHost` 串行执行队列。在执行期比对全局席位代次 `bindingGenerations[seat] == expectedSeatGeneration`：已被顶号替换的旧连接断开请求在权威队列上被安全忽略，绝不误断开新连接；有效代次断开时在单人模式由房主触发房间终结，在联机模式下将席位转交 AI 托管并保留有效 `resumeToken`。
   - **观察者版本防乱序与严格终结隔离**：重构 `RoomSubscriber`：
     - 引入 `highestVersion` 版本单调递增看门狗：在并发注册或竞态入队场景下，严格拒绝旧版本/同版本覆盖更新的视图，终结状态 `CLOSED` 保持为最终态；
     - 分离终结事件与注销生命周期：显式 `unregister()` 立即清理 `pending` 与 `terminal` 并置位 `unregistered = true`，彻底抑制已注销 UI 观察者的任何后续回调；
     - `onRoomUpdate` 在房间已处于 `CLOSED` 终结态时仍入队终结快照，确保新注册观察者能够正常消费到终结事件。

3. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
   - **对齐协议字段与接口契约**：`resumeSession` 统一使用 `req.token`（而非 `resumeToken`）；`queryRoundResult` 签名与返回值纠正为 `CompletableFuture<List<ProtocolGameEvent>>`。
   - **保证内部终端房间订阅**：新增 `internalRoomSub` 内部房间监听句柄，独立于可选的 UI `roomObservers`。只要绑定房间即建立内部订阅；当房间权威终结 (`ProtocolRoomLifecycle.CLOSED`) 时，自动将连接标记为 `closed = true` 并派发 `onDisconnect(ApiProtocol.ROOM_CLOSED)`，后续业务写操作一律返回 `REJECTED(SESSION_CLOSED)`。
   - **房主主动离开状态保护**：通过 `isExplicitlyLeaving` 标志保护 `leaveRoom()` 流程，房主主动解散离开并收到 `APPLIED` 时，自身连接保持活跃 (`closed = false`, `currentRoom = null`)，不被自身的 `ROOM_CLOSED` 广播误踢，支持在同一通道上继续建房或加入。
   - **共享席位代次感知与顶号隔离**：在 `bindRoom` 时捕获 `capturedSeatGeneration = room.bindingGenerations[viewerSeat]`；`makeValidator` 与推送/房间更新回调中严格比对全局代次。被顶号后旧连接立即置位 `_disconnectReason = ApiProtocol.DISPLACED_BY_NEW_CONNECTION` 并关闭；旧连接退出调用 `room.disconnectSeat(seat, seatGen)`，队列校验因代次递增而安全跳过，绝不断开新连接。
   - **UI 观察者生命周期守卫**：`BindingRoomObserver` 增加 `isDetached` 生命周期标志，在 `detach()` 后即刻丢弃并拦截任何排队中的残留回调。

4. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt)
   - 移除伪造成功的单机固定兼容标识，全面对接 `SharedRoom` 与 `RoomRegistry` 真实实例。

### 模块测试 (server 模块)
5. [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)
   - 补齐 `import com.majiang.transport.api.ProtocolRoomLifecycle` 缺失引用；
   - 新增 `host leave succeeds while guest receives room closed without UI subscription`：验证在两连接真实网络策略下，无 UI 房间更新订阅时，房主离开返回 `APPLIED` 且保持活跃未绑定，访客通过内部订阅收到 `onDisconnect(ROOM_CLOSED)`，`closed = true` 且后续动作被拒绝；
   - 新增 `replacement resume prevents previous connection from changing readiness`：验证新连接携带虚假未来游标 (999999) 恢复席位，权威快照返回真实服务端当前游标，旧连接尝试更改就绪状态被拒绝，席位就绪保持有效且房主开赛成功。

6. [`server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt)
   - 适配 `resumeSession` 最新签名，保留 Token 认证、轮转作废与幂等资源清理验证。

---

## 2. 测试执行声明

**特别声明**：根据多 Agent 协作规范与本次任务指示（Anti 负责生产代码与模块测试开发，Codex 独立负责构建、测试运行与最终验收）：
- **所有构建指令与测试均未由 Anti 运行**。
- Anti 未执行任何 `./gradlew`、终端构建、命令行或进程指令。

---

## 3. 未解决项与后续范围说明

- **UDP 绑定与网络重传**：属于后续网络批次范围，本批次保持传输中立与进程内调用。
- **Client 运行时与视图适配**：属于后续客户端批次。
- **B2 观战与扩展席位**：严格保留至后续阶段。
