# 交付报告：R4B1UDPSERVER UDP服务端接入与生命周期

**执行人**：Antigravity (Anti)  
**日期**：2026-10-06  
**对应任务**：`doc/v4-migration/TASK-ANTI-R4B1UDPSERVER.md`  

---

## 1. 交付文件清单

### 生产代码 (server 模块)
1. `server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`
   - 定义 `data class ResumeSessionResult(val seat: Int, val snapshot: PlayerSnapshot, val generation: Long)` 并提供 `.first`, `.second`, `.third` 访问器，既向后兼容既有解构与调用，又向调用端原子交付权威分配的连接代次；
   - 更新 `resumeSession`：在 `tableHost.enqueueOperation` 权威串行队列内原子自增 `++bindingGenerations[seat]` 并作为结果属性返回，确保会话恢复的代次递增与领域重连具备绝对权威偏序。

2. `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`
   - 彻底将房间业务（成员席位、准备就绪、规则配置、开赛准入、离房判定、会话恢复、生命周期与快照查询）委托至 `SharedRoom`，消除业务状态双写；
   - 引入席位细粒度锁 `seatLocks = Array(4) { Any() }` 与已安装代次追踪 `installedGenerations = LongArray(4)`；
   - 彻底解决并发乱序 `resume` 竞态：恢复完成时获取权威队列产生的精确 `res.generation`，在 `synchronized(seatLocks[seat])` 下比对 `targetGeneration < installedGenerations[seat]`。若检测到已被更新代次覆盖，直接抛出 `DISPLACED_BY_NEW_CONNECTION` 拒绝装回旧绑定，且不错误剔除较新会话；
   - 将被顶替旧连接的 `kickSender` 派发与 `close()` 移出席位锁外异步执行，彻底阻断慢 KICK 拖累或锁死新会话安装；
   - `removeBinding` 与 `closeTransport` 全面纳入席位锁保护，防止并发解绑覆盖；
   - 保留执行期绑定校验 `commandTarget` 与 `sessionValidator`，动态校验会话生命周期与代次有效性；
   - 保留房主显式离房的豁免标记 `leaveExclusion`，确保离房房主收到操作成功回执，同时其余席位可靠接收 `ROOM_CLOSED` 终端广播。

3. `server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`
   - 补齐伴生对象中缺失的重试与兜底常量：`ROOM_UPDATE_RETRY_BASE_MILLIS = 100L`, `ROOM_UPDATE_MAX_ATTEMPTS = 3`, `ROOM_UPDATE_REFRESH_MILLIS = 3000L`；
   - `InFlightRoomUpdate` 增加记录房间版本号 `version: Long`，并维护 `highestSentRoomVersion = ConcurrentHashMap<Int, Long>()` 单调版本追踪，严格丢弃并发旧版本回调，防止较旧版本覆盖新版本待发记录；
   - 为 `send(...)` 增加 `useStableSeq: Boolean = false` 控制参数：发送房间更新或重传时启用稳定消息 seq（即分片与重发均沿用原始帧 seq，而非每分片自增分配新 seq），根本消除客户端 ACK 回指原 seq 时服务端无法匹配并清除在途记录的缺陷；
   - 在 `sendRoomUpdate`、`scheduleRoomUpdateRetry`、`refreshPendingRoomUpdates` 以及 `sendKick` 中显式指定 `useStableSeq = true`；
   - 在大厅请求路由 `handleLobbyReq` 中补齐 `ApiTags.GET_ROOM_INFO`，允许未绑定客户端查询房间信息；
   - 在会话关闭、读超时检测、KICK 终结及服务端停服时完整清理 `highestSentRoomVersion` 与 `inFlightRoomUpdates`；
   - 增加内部测试观察入口 `getInFlightRoomUpdate` 与 `getHighestSentRoomVersion`。

### 模块测试 (server 模块)
4. `server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`
   - 适配工厂构造入口为 `UdpRoom.standalone(...)`；
   - 将已废弃的字段访问 `room.matchStarted.get()` 迁移至权威状态推导属性 `room.isMatchStarted`；
   - 适配多真人准入校验，在开赛测试中增加 `setReady` 前置条件；
   - 新增回归测试 `testConcurrentOutOfOrderResumePreservesLatestBinding`：验证并发乱序恢复时权威最新代次正确胜出，旧恢复无法覆盖最新绑定；
   - 新增回归测试 `testSlowKickDoesNotBlockOrCorruptNewerResume`：验证当旧连接 KICK 遭遇长时间网络延迟阻塞时，新连接恢复与绑定依然能即刻完成；
   - 新增回归测试 `testRoomUpdateRetransmitAndAckClearing`：验证稳定 seq 机制下客户端 ACK 精确清除在途更新记录、非匹配 ACK 安全保留记录、以及旧版本更新安全丢弃。

---

## 2. 测试执行声明

**特别声明**：根据多 Agent 协作规范与本次任务指示（Anti 负责开发生产代码与模块测试，Codex 独立负责构建、测试运行与最终验收）：
- **所有构建指令与测试均未由 Anti 运行**。
- Anti 未执行任何 `./gradlew`、终端构建或命令行指令。

---

## 3. 未解决项与边界说明

- **未解决项**：服务端 UDP 接入与生命周期（Parallel A）范围内的所有需求已全部闭环，无遗留未解决项。
- **边界说明**：
  - 客户端网络传输、断线重连协调器及视图适配（Parallel B / B2）归属独立任务，不在本任务修改范围内；
  - review-owned 测试目录 `tools/architecture/v4-review/**` 归 Codex 独占所有权，Anti 未进行任何修改；
  - 公共 protocol 契约及现有规则算分测试保持严格冻结，未作修改。
