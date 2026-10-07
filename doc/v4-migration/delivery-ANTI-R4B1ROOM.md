# 交付报告：R4B1ROOM 共享房间与进程内绑定服务

**执行人**：Antigravity (Anti)  
**日期**：2026-10-05  
**对应任务**：`doc/v4-migration/TASK-ANTI-R4B1ROOM.md` 与 `doc/v4-migration/FEEDBACK-R4B1ROOM.md`  

---

## 1. 交付文件清单

### 生产代码 (server 模块)
1. `server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`
   - 增加 `toProtocolMatchConfig(MatchConfig)` 协议映射；
   - 修复 `TimeControlConfig` 领域毫秒与协议秒单位换算（乘除 1000L，保留 20+3 默认值，使用 `Math.multiplyExact` 校验防溢出）。
2. `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`
   - 将 `enqueueOperation` 与 `completionExecutor` 可见性调整为 `internal`；
   - 增加权威状态提交钩子 `onStateCommitted: (() -> Unit)?`，在 `publishStateChanges` 末尾安全驱动生命周期推导；
   - 暴露队列内原子原语：`performStartOnQueue`、`performAssignComputerSeatOnQueue`、`performResetSeatControllerOnQueue`、`performUpdateConfigOnQueue`、`performDisconnectOnQueue`、`cursorForSeat`；
   - 暴露 `val isPaused: Boolean get() = authority.isPaused` 供开赛前置校验。
3. `server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`
   - 彻底移除调用者线程 `@Synchronized` 锁，全部房间业务严格共享 `ServerTableHost` 串行执行队列；
   - 引入独立通知派发线程池 `roomNotificationExecutor` 与 `RoomSubscriber` 队列，实现单观察者单调有序通知派发，初始基线以严格 FIFO 顺序入队，慢观察者不阻塞权威队列、返回 Future 或其他观察者；
   - 显式退出 (leave) 在所有生命周期阶段（大厅、对局、结算等）均轮转 `resumeTokens[seat]` 并递增 `bindingGenerations[seat]`；
   - 房主退出解散时，将成功回执 (`APPLIED`) 优先交付调用方 Future，延后在通知线程池异步释放 `tableHost` 资源，彻底消除关闭与成功回执之间的竞态；
   - 开赛 (startMatch) 在修改席位与补位 AI 前进行权威暂停与终局防御检查，对补位 AI 席位采用事务性暂存，若权威启动被拒则完整回滚空闲席位与准入空间；
   - 房间生命周期直接基于权威提交事件推导（LOBBY -> PREPARING / PLAYING -> SETTLED -> FINISHED -> CLOSED）；
   - 所有代理方法完整穿透 `sessionValidator` 校验。
4. `server/src/main/kotlin/com/majiang/server/room/RoomRegistry.kt`
   - 采用内部短临界区锁实现严格原子且有界的准入控制（`rooms.size >= maxRooms`，`isClosed`）；
   - 锁与业务完全解耦：持锁期间仅执行轻量级元数据检查与集合更新，房间构造、监听器派发与被替换旧房间关闭均在锁外执行。
5. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt`
   - 保留单人装配策略 (`isSingleplayer = true`)，支持大厅重构桌同样继承单人自动就绪行为；
   - 服务端活跃房间属性 (`currentRoom`、`currentHost`) 与步进驱动 (`tick()`) 自动跟踪大厅创建/绑定的活跃房间；
   - 暴露未绑定的连接端点 `createConnection(): ServerConnection`，供大厅/菜单场景调用。
6. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`
   - 彻底移除 `fallbackHost` 及直连宿主兼容旁路，全量操作统一收敛至 `SharedRoom`；
   - `joinRoom` 采用前置代次校验器，在加入成功前不强制要求 `currentRoom === targetRoom`，支持未绑定连接干净加入；
   - `leaveRoom` 严格条件化解绑：仅在回执状态为 `APPLIED` 且代次与当前房间匹配时才执行解绑；
   - 监听器通知派发严格捕获连接活跃状态、房间引用与 `bindingGeneration`，换房或离房后绝不派发旧房间或旧连接残留事件。

### 模块测试 (server 模块)
7. `server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`
   - 更新单人模式下 `leaveRoom()` 断言为 `APPLIED`；
   - 增加未绑定连接在同一通道上建房、离房并再次加入的端到端测试；
   - 增加本地房主离房后在同一活跃通道上创建第二个房间、继承单机策略并正常启动与时钟步进的回归测试。
8. `server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`
   - 覆盖单人创建与开赛流程；
   - 覆盖仅房主可配置规则与联机全员就绪门禁；
   - 覆盖大厅规则配置更新下的会话稳定性 (`sessionId` / `syncEpoch` 不漂移)；
   - 覆盖慢观察者阻塞下权威查询与操作零阻塞返回；
   - 覆盖权威队列串行化执行；
   - 覆盖过时会话校验器 (`sessionValidator`) 拒绝机制；
   - 覆盖宿主关闭取消挂起 Future；
   - 覆盖房主退出解散房间与调用方成功回执；
   - 覆盖显式退出在所有生命周期阶段轮转 Token 与递增代次；
   - 覆盖暂停状态下开赛被拒时 AI 补位暂存回滚与准入空间保留；
   - 覆盖观察者从初始基线开始单调有序接收房间版本演进。

---

## 2. 测试执行声明

**特别声明**：根据本次任务规范与多 Agent 角色边界约定（Anti 负责开发与模块代码交付，Codex 负责独立测试、构建验证与验收）：
- **所有构建指令与测试均未由 Anti 运行**。
- Anti 未执行任何 `./gradlew`、终端构建或命令行指令。

---

## 3. 未解决项与后续范围说明

- **UDP 绑定与网络通道**：当前批次聚焦于传输中立的权威共享房间 (`SharedRoom`)、注册表 (`RoomRegistry`) 与进程内绑定 (`InProcessBinding`)；UDP 报文传输层集成、分片重组与弱网重传机制属于后续阶段任务。
- **Client 运行时与视图层适配**：客户端会话状态机、断线自动退避重连与 LibGDX 视图层交互将在后续客户端批次中接入统一的 `ServerConnection` 契约。
- **观战席位与 B2 阶段**：观战投影接入与 B2 扩展保持未动，严格不越界提前实现。
