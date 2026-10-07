# R4B2 CLIENT 观战接入：Codex 只读预审笔记

本文依据当前 `ServerConnectionSession`、`UdpClientBinding`、`LobbySession` 和尚未派发的 `TASK-ANTI-R4B2WIRE.md` 编写，**不是开发授权，也不是完成证据**。WIRE 接受后按实际签名更新 CLIENT 任务。用户最新边界：完成 R4 验收后保存并暂停，不启动 R5/R6。本文未修改生产或测试、未运行构建。

## 建议独占文件范围

CLIENT 由同一个 Anti 任务持有下列文件；可与 SERVER 并行，不能再把同一个 runtime 或 mapper 分给其他写入进程。

| 文件 | 接入职责 |
| --- | --- |
| `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt` | 玩家/观战绑定的互斥生命周期、独立 PUBLIC 缓存、顺序消费、订阅、恢复与 Future 清理 |
| `client/src/main/kotlin/com/majiang/client/api/LobbySession.kt` | 面向调用者的异步观战加入/恢复入口；参数和结果均为客户端类型 |
| 新 `client/src/main/kotlin/com/majiang/client/api/SpectatorSession.kt` | 独立只读观战门面：当前公开快照、公开快照查询/恢复、公开局结果、公开更新订阅和生命周期；不得继承带 submit/decision 的 GameSession |
| 新 `client/src/main/kotlin/com/majiang/client/api/PublicViewModels.kt` | 独立公开 board/session/history/result/snapshot/update/checkpoint 客户端模型，名称按 WIRE 实际契约定稿 |
| `client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt` | 按 WIRE 已接受类型完整映射公开字段，可复用 tile/meld/公开事件及计分映射，不复用私有 GameSnapshotView 作为公开超集 |
| `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt` | 实现 SpectatorApi、类型化 PUSH 路由、独立 PUBLIC 顺序和补发、观战绑定/退出清理 |
| `client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt` | 补齐 FakeServerConnection 的新 abstract 方法；保留原玩家及 ROOM 用例 |
| `client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt` | 新 API 请求/响应、PUBLIC PUSH、数据报身份、重传与关闭清理 |
| `client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt` 及新增 PUBLIC 模块测试 | 完整映射、公开类型边界与不可变集合、观战运行时行为 |

`UdpClient.kt` 和 `ScheduledExecutorUdpScheduler.kt` 负责连接/资源装配，现有异步工厂可复用；只有实际发现资源归属问题才在 CLIENT 任务中明确扩入。`GameSession.kt` 原玩家门面原则上保留，不把公开快照塞入其玩家类型。`EngineDtoMapper.kt`、V3 network、presentation、启动器不属于本批，不为旧入口新增兼容壳。protocol 主源码和测试归 WIRE，server 接口实现归 SERVER；CLIENT 不跨写。所有 `tools/architecture/v4-review` 文件由 Codex 更新。

## 当前实现与需要补齐的路径

### 独立绑定、缓存和订阅

当前 runtime 仅有 `currentBinding: RoomBinding?`、`cachedSnapshot: GameSnapshotView?`、玩家 `reorderBuffer`、`earlyUpdateBuffer`、`appliedBatchCursors` 和 `updateListeners`。`bindConnectionListeners` 只注册玩家、ROOM 和断线三个回调。`onBindingEstablished` 一律读取 `binding.snapshot.view.viewerSeat`。

观战接入不能用假 seat 或清空玩家暗牌来满足这些分支。绑定必须明确区分未绑定、玩家、观战；玩家绑定与观战绑定不可同时有效。公开缓存/订阅要有各自类型、顺序状态和早期缓冲；`currentSnapshot` 与 `currentPublicSnapshot` 不能互相赋值或被另一角色的 PUSH 填充。可以共用调度与生命周期工具，不能共用游标值、玩家决策或缓存对象。

`onPublicUpdate` 应在发起观战 join/resume 前注册；绑定响应提供全量 PUBLIC 基线，之后只合流同一 sessionId/syncEpoch 的早期消息。与现有玩家策略一致，早期缓冲必须有界；溢出进入明确恢复路径，不能静默丢弃后一直停留在旧基线。PUBLIC listener 注册、取消、抛异常、延迟 dispatch 均需覆盖，不能持状态锁调用外部用户代码。

角色切换需明确完成离房再建立新绑定，或显式拒绝冲突操作。观战不能调用准备、更新房规、开局、提交动作/洗牌、收回/释放控制；客户端可提前返回失败 Future，服务端仍必须按真实绑定强制拒绝。不能只依赖界面不显示按钮。

### PUBLIC 更新顺序与恢复

WIRE 草案的普通更新为 `cursor == fromCursor + 1`，因此客户端只有 `fromCursor == 当前cursor` 才能连续应用；全量更新必须有明确 full 标志且 `fromCursor == cursor`。对普通消息的重复、乱序、缺口按独立 PUBLIC 缓存处理。

已有 `protocol/.../PushSequencer.offer` 使用 `fromCursor > currentCursor + 1` 才判缺口。直接复制给 PUBLIC 会把缺失恰好一条的更新误判 Applied。Codex 已决定 CLIENT 内部实现独立 PUBLIC 严格基线/重排，保留现有 PLAYER 行为，不修改共享 protocol 的玩家 sequencer。

恢复请求必须使用 `PublicCheckpoint(sessionId, syncEpoch, cursor)`；正常更新不能凭更大数字跨流覆盖。新流只能通过受当前连接/绑定代次保护的 join/resume/publicSnapshot 全量响应建立基线，同数值 cursor 也可能是不同流。旧恢复 Future 晚到时不能清除新绑定的恢复在途标记，也不能写回新缓存。恢复失败释放标记，允许后续重试；恢复成功清除过期缓冲并按顺序排水，事件批次不能重复发布。

当前 runtime `triggerGapRecovery` 固定调用玩家 `resume(cursor)`；`doReconnect` 固定构造 `ResumeRequest(roomId, token, cursor)` 并调用 `resumeSession`。观战分支必须使用 spectatorId/resumeToken 和 PUBLIC checkpoint 调用 `resumeSpectator`，得到新全量基线后恢复 PUBLIC；禁止走玩家恢复或把观众转换成座位。断线时可以保留旧公开视图用于展示，但不得继续接受旧连接数据。

显式离房在途、主动关闭、被替换、ROOM_CLOSED、SERVER_SHUTDOWN、token 无效等永久终结，不得开启观战抢占式重连。现有 `isLeavingRoom`、`currentEpoch`、`currentRoomEpoch`、outstanding Futures 与重连计时器行为需扩展到观战；相同 roomId 离开重入也必须隔离旧回调。`terminateSession` 和 `bindConnectionListeners` 新增 PUBLIC subscription 的关闭与解绑。

### typed PUSH 与 UDP 接线

当前 `handleInboundPush` 先调用 `isRoomUpdateEnvelope`，其对任意带 type 的对象进入 ROOM 解析。CLIENT 必须改为 WIRE 明确的 PLAYER/ROOM/PUBLIC 分类器，未知类型、错误类型字段和非法 JSON 直接拒绝；不能用先解码玩家、异常后试公开的回退猜类型。

保留 ROOM_UPDATE 已验收的单调版本、绑定检查和重复有效消息在 ReplayGuard 前 ACK；房间版本不是任何游戏流 cursor。观战绑定也允许公开大厅 getRoomInfo/onRoomUpdate，不能因 `currentBinding == null` 被现有玩家专用条件全部丢弃。

`UdpClientBinding` 当前只有 `boundSeat` 和玩家 token，create/join/resume 收到 RSP 后采用 connectionId、递增 bindingEpoch 并 reset ReplayGuard/玩家 sequencer。新增观战握手应以专属 spectatorBinding 响应建立绑定、原子清理另一角色状态，完整采用 PUBLIC 基线，不能复用 RoomBinding/seat。普通 publicSnapshot/resumePublic 响应回写必须捕获并验证发起请求的 bindingEpoch。

当前 `sendResendRequest` 发 `{"fromCursor":...}`，`triggerFullSnapshotSelfHeal` 调玩家 snapshot，并发布 ProtocolPlayerUpdate。PUBLIC 需自己的恢复在途标记、公开查询、ProtocolPublicUpdate 与 listener。Codex 已指定 PUBLIC RESEND 形状并将同步到 WIRE 任务：

```json
{"type":"PUBLIC_RESEND","checkpoint":{"sessionId":"session-id","syncEpoch":"public-epoch","cursor":7}}
```

`checkpoint.cursor` 是客户端**已应用基线**，server 从 `cursor + 1` 起补发。WIRE 提供严格 `encodePublicResendRequest(PublicCheckpoint)` / `decodePublicResendRequest(...)`；实际签名以 WIRE 接受后的交付为准。SERVER 必须校验当前观战绑定及 checkpoint 的 session/epoch，旧 epoch 不能查询新流窗口；CLIENT 不手拼 JSON。PLAYER 原 RESEND 形状和行为保留，两类消息不得靠解码异常回退辨别。

现有 `onInboundFrame` 在 connectionId 为 0 时丢弃 PUSH，已绑定时拒绝不匹配 connectionId（包括 0）。观战握手也要保持这一身份边界。模拟 leave 响应应仍带旧有效 connectionId，收到成功响应后才归零；不要重现 R4B1 测试用 connId=0 导致 Future 一直等待的问题。分片重组后的 PUBLIC 分类、断线后重复 KICK ACK 和接收线程关闭继续复用现有传输规范。

## fake 与独立验收装配

- CLIENT 模块 `ServerConnectionSessionTest.FakeServerConnection` 直接实现 ServerConnection，WIRE 增加 abstract 方法后必须补齐真实可控的观战 Future、公开 listener 注册/注销、发包、延迟/异常行为。不得用生产接口默认 unsupported/no-op 消除编译错误。
- protocol 模块 `FakeServerConnection.kt` 与 `FakeServerConnectionSmokeTest.kt` 归 WIRE 修改，不归 CLIENT；server `InProcessBinding.kt` 归 SERVER。
- Codex `V4RuntimeAcceptanceTest.FakeConnection` 是动态 Proxy；新增 runtime 初始化时注册 onPublicUpdate，会使未更新的 Proxy 默认返回 CompletableFuture 而非 AutoCloseable，发生运行期 cast 错误。Codex 要在下一次 runtime 验收前补独立 PUBLIC listener 返回值，不能交 CLIENT 改 reviewer。
- UDP 模块使用 fake clock/scheduler 和实际帧。新增测试要主动推进 fake 时间并使用有界等待；禁止依赖不推进调度器的裸 join。实际 loopback loss/resume 验收由 Codex 在 SERVER/CLIENT 都完成后串行运行。

## CLIENT 验收应观察的结果

1. 真实 InProcess 与 UDP 观战 join、publicSnapshot、公开局结果和 resume 路径返回独立客户端 PUBLIC 模型，公开结算/历史的全部字段与来源一致，含 outcome 和立直棒归属；事件保留合法公开内容，不能删除全部事件假装隐私通过。
2. 同一观察者不会收到玩家 hand/drawn/sleeve/preparation/preview/decision/controlEpoch 等字段；混合玩家负载由严格 codec 拒绝，runtime 也不消费错角色更新。客户端公开 DTO 不 import engine/protocol，不内部包裹 GameSnapshotView。
3. 更新乱序、重复、丢一条、超窗、首个/最后一个更新丢失、分片与全量标志均有实际收敛证据。仅测试有后一条触发 gap，不足以证明最后一条丢失可以恢复；这一点需要 SERVER 可靠推送策略配合。
4. 断线后保留旧公开视图并通过观战身份恢复；旧连接回调、同房间离开重入回调、旧恢复响应均不能污染新缓存。离房可再次加入玩家/观战，关闭后在途 Future 全部结束，计时器/订阅/receiver 不泄漏。
5. 房间公开订阅对观战仍正常；玩家 preview、ROOM_UPDATE、已验收玩家同步/计时/规则测试全部保留并回归。模块通过不替代独立跨进程/丢包验收。

以上是 CLIENT 实现和验收准备，不启动产品观战 UI、不切换默认产品入口、不删除 V3；R4 最终验收完成后暂停。
