# 交付报告：R4B1ROOMFIX1 共享房间与连接修复

**执行人**：Antigravity (Anti)  
**日期**：2026-10-05  
**对应任务**：`doc/v4-migration/TASK-ANTI-R4B1ROOMFIX1.md` 与 `doc/v4-migration/FEEDBACK-R4B1ROOMFIX1.md`  

---

## 1. 交付文件清单

### 生产代码 (server 模块)
1. `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`
   - 修复编译错误：将不存在的 `ControllerRole.LOCAL_HUMAN` 修正为领域枚举 `ControllerRole.HUMAN`；
   - 增加队列内重连原语 `performReconnectOnQueue(seat, reclaim)`；
   - 增加窄接口快照查询原语 `performQuerySnapshotOnQueue(seat, cursor)`，封装 `seatStreams[seat].syncEpoch` 与 `buildSeatControlViews()`，保留私有流数组的严格封装。
2. `server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`
   - 移除所有 `LOCAL` 硬编码特殊逻辑，席位 Token 统一采用独立安全 UUID；
   - 增加 `resumeSession(resumeToken, cursor, sessionValidator)`，校验目标房间、会话代次与席位 Token 有效性，重连席位并返回快照；错误或已注销 Token 抛出 `INVALID_RESUME_TOKEN`；
   - 观察者派发重构为有界合并派发器 (`RoomSubscriber`)：常数空间 $O(1)$ 内存，支持最新全量视图合并 (latest-full-view coalescing)，且保证 `CLOSED` 终结事件不被提前丢弃，慢观察者不阻塞权威队列、返回 Future 或其他观察者；
   - 确定性房主离开与关闭：调用方优先取得 `APPLIED` 成功回执，`deferredShutdown()` 异步释放宿主与通知线程池，杜绝资源竞态与 `roomNotificationExecutor` 泄漏；`close()` 基于不可变快照推导终结状态，不与队列内数组突变发生并发竞态。
3. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt`
   - 移除 `createLocalTable` 的 `LOCAL_ROOM_ID` 硬编码固定标识，生成真实共享房间；
   - `currentRoom` 动态追踪活跃房间或大厅注册表首个存活房间。
4. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`
   - 移除所有伪造成功的 `LOCAL` 兼容旁路；已绑定房间时调用 `createRoom`/`joinRoom` 严格拒绝 (`ALREADY_IN_ROOM`)；
   - `resumeSession` 完整校验目标房间存在性与 Token 合法性，认证失败时不破坏现有连接绑定；
   - 区分访客传输关闭与房间销毁：访客连接 `close()` 仅触发席位断线通知，绝不关闭房主房间或使有效 `resumeToken` 失效；新连接可持原 Token 成功恢复；
   - 观察者通知派发通过 `attach`/`detach` 直接接入 `room.onRoomUpdate`，消除独立执行器导致的初始通知乱序风险，换房或离房后绝无旧房间残留通知。

### 模块测试 (server 模块)
5. `server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`
   - 更新单人语义测试：断言真实房间标识、全员人数 (4)、已绑定时拒绝重复建房/加入、错误 Token 拒绝与正确 Token 恢复、显式离开后原 Token 永久失效；
   - 增加测试：不存在的房间号或错误 Token 恢复失败但保留原有连接绑定；
   - 增加测试：访客传输层意外关闭不影响房主房间与对局状态，新连接可使用原 Token 成功重连恢复；
   - 增加测试：大厅期显式离开使旧 Token 不可恢复。
6. `server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`
   - 增加测试：`resumeSession` 严格校验 Token，拒绝错误 Token，并在显式退出注销后拒绝原 Token；
   - 增加测试：房主离开幂等释放宿主与执行器资源，无泄漏。

---

## 2. 测试执行声明

**特别声明**：根据多 Agent 协作规范与本次任务指示（Anti 负责开发生产代码与模块测试，Codex 独立负责构建、测试运行与最终验收）：
- **所有构建指令与测试均未由 Anti 运行**。
- Anti 未执行任何 `./gradlew`、终端构建或命令行指令。

---

## 3. 未解决项与后续范围说明

- **UDP 绑定与网络重传**：属于后续网络批次范围，本批次保持传输中立与进程内调用。
- **Client 运行时与视图适配**：属于后续客户端批次。
- **B2 观战与扩展席位**：严格保留至后续阶段。
