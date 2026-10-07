# R4B2 SERVER 接入审查准备

2026-10-06。只读源码审查所得；不是开发派发，也不表示 WIRE/SERVER 已验收。必须等 PREVIEW/PUBLIC 接受、WIRE 契约接受后再执行 SERVER。用户最新终点：完成整个 R4 后保存并暂停，不进入 R5/R6。

## 文件所有权与依赖

SERVER 单一开发者独占以下范围，与 CLIENT 并行时不得相互修改：

- `server/.../room/SharedRoom.kt`：共享观战 membership/token/generation、授权验证、加入/恢复/离开/断开；必要时同包新增明确身份类型。
- `server/.../host/ServerTableHost.kt`：独立 PUBLIC 流、队列内读事务、完整快照基线、历史及有序通知。
- `server/.../mapping/EngineProtocolMapper.kt` 或同包新增 `PublicProtocolMapper.kt`：PUBLIC 到独立协议类型的逐项映射。
- `server/.../inprocess/InProcessBinding.kt`、必要的 `InProcessServer.kt`：真实 SpectatorApi、互斥绑定状态、代次门与资源收尾。
- `server/.../udp/UdpRoom.kt`、`UdpClientSession.kt`、`UdpTableServer.kt`：UDP 角色、路由、PUBLIC 编码/重试/ACK/RESEND 和终端流程。
- 对应 server 模块测试。`RoomRegistry.kt` 仅在共享房间准入/停服接线确实需要时修改，不能另建观战房间注册表。
- 经主审明确同意，最小扩展 `engine/.../authority/TableAuthority.kt` 的 PUBLIC 只读入口；不得修改 PREVIEW 已接受区域、既有规则/计时/算分算法。纯投影 `engine/.../projection/**` 已接受，默认只读。

protocol 由 WIRE 冻结；client 全部归 CLIENT；presentation、组合根、V3 删除均不在本批。若真实接线发现协议缺口，回报主审单独协调，不跨范围改契约。

## 必要的纯权威查询入口

实际完整历史结算存于 TableAuthority 私有 `roundResultEvents`、`lastCompletedSettlementEvents`。`MatchSession.roundRecords` 没有完整 ScoreBreakdown；现有 `queryRoundResultEvents(seat, roundId)` 强制玩家过滤。禁止传 seat0、反射字段、或新建第二套结算账本。

建议新增同步纯读入口：

```kotlin
fun queryPublicProjection(): PublicTableProjection
fun queryPublicRoundResultEvents(roundId: String): List<GameEvent>
```

前者在一次权威操作中取得 `coordinator.session.readState()`、`activeRound?.readState()`、局/场版本、`buildClockViews()`、暂停状态，以及与现有玩家 snapshot 相同的真实终局判定和已完成局标识；调用已接受 `PublicProjectionFactory.project(...)`。后者读取既有结算缓存或当前真实 RoundResult，再调用 PUBLIC 事件白名单。不返回 raw GameState、准备记录或玩家快照。

里宝许可由实际和牌结算事实确定，不能由客户端参数、一般 RoundEnded、私人透视事件或单纯 phase 名称授权。可抽取私有只读辅助函数，保证 projection、历史查询与本次公开事件使用同一授权判断。若 ServerTableHost 发布可见事件需要跨调用复用，则可增加 `filterPublicEvents(events: List<GameEvent>): List<GameEvent>`，内部复用相同授权和 factory；不能让 wire 传 revealSettlementUra。

上述方法仅在 ServerTableHost 单桌 serialExecutor 内调用，包括初始化安全基线、publicSnapshot/resumePublic、公开结果查询和 `publishStateChanges`。UDP 接收线程不直接调用 authority；序列化、socket 发送、监听器与外部 Future 完成在队列外。权威查询不持有 transport/membership 锁。

## 宿主与共享成员接入

`ServerTableHost` 当前 `seatStreams[4]`、`seatDispatchers[4]` 和 `publishStateChanges` 全按玩家运行。新增一个 PUBLIC stream，不能借用其中任一项；有独立 syncEpoch/cursor/最近完整快照/最多 256 条历史。每次真实提交统一准备 PUBLIC 候选，发布时使用独立前后 cursor。初次订阅注册与基线提取同队列完成，派发走独立有序通知路径，保证慢观战者不阻塞权威与玩家。

版本改变可使公开 snapshot 元数据更新，但私密事件不能作为 PUBLIC visibleEvents 发送。只刷新 serverNow 不应制造无界时钟推送；复用现有有效时钟变更判断思想。PUBLIC 输出应保留公开计分与历史字段，不能为了隐私把公开事件全部清空。

SharedRoom 新建独立 spectatorId 索引和 token/generation 事实；加入满四座房间正常，观战 join/leave/disconnect 均不改 `seatOccupied/isHuman/isReady/resumeTokens/bindingGenerations[seat]` 或控制权。实际加入/恢复结果必须在同一队列返回分配的 generation 与公开基线，不能异步完成后再读可变 generation。

恢复先验证 roomId+spectatorId+token 与当前有效 membership，再递增 generation；失败不破坏现有绑定。客户端给出的未来 cursor 不决定权威输出。并发两次恢复只允许最高有效 generation 安装；旧完成、旧关闭与旧 leave 不得撤销新连接。显式 leave 撤销 membership/token，意外掉线仅解绑连接、保留恢复事实。

## 两种绑定与授权

连接状态须明确区分未绑定、玩家、观战；不要把当前 `viewerSeat=0` 或 UDP 必填 seat 字段作为观战默认值。具体方法按绑定角色分派，玩家私有函数入口本身也要拒绝观战，不能只在 UDP 路由拒绝。

观战允许 PUBLIC 只读接口、roomInfo/roomUpdate、capabilities、leave。拒绝 submit/shuffle/control/ready/start/nextRound/config/decision/玩家 snapshot/玩家 resumeCursor/玩家 queryRoundResult。玩家 token 与观战 token 的索引/恢复路径分离；一个有效 token 不能替代另一种身份。未绑定/已绑定的大厅 API 分支要包含 JOIN_SPECTATOR 与 RESUME_SPECTATOR。

InProcessBinding 当前回调门捕获 local generation、room、seat、seatGeneration。替换为对应角色的明确身份上下文，保留 stateLock 下原子认领、锁外通知，以及 leave Future 在解绑完成后才完成的语义。观战订阅只调用 PUBLIC 路径，绝不临时接收玩家 projection 再过滤。

UdpRoom 当前 onSharedRoomUpdate/closeTransport 遍历四座 activeBindings；需同时处理独立观战绑定集合。`commandTarget/sessionValidator/getConnectionGeneration` 必须按角色验证。UDP 路由 `handleSessionReq` 当前直接读取 session.seat 做前置代次校验，必须先改为统一成员身份校验，再进入幂等缓存/实际 API。旧 session 缓存不能绕过代次失效。

观战 leave 成功返回大厅；复用现有 terminatedSessions 的有界 LEAVE 回执重放，不因重试再次触发成员变更。房主 leave 的自身豁免只属于离开的房主，所有观战者收到 ROOM_CLOSED。SERVER_SHUTDOWN、顶号和关闭要统一终结公开订阅、挂起 Future、重试和窗口；旧断开不能触发任何玩家 AI 接管。

## PUBLIC UDP 可靠性具体落点

`UdpClientSession.snapshotSupplier` 和 `handleResend` 当前固定 PlayerSnapshot/玩家编码，这是必须真实替换的接入点。使用角色明确的快照供应路径；供应者读取已发布不可变 PUBLIC 缓存，不能在 receiveLoop 里同步等待权威队列。错误时不能发一个玩家 snapshot 兜底。

复用 PUSH/FRAG/ACK/RESEND 数据报类型和 256 条窗口；PUBLIC 不复用玩家的裸 fromCursor 载荷。WIRE 已决定提供 strict `encode/decodePublicResendRequest(PublicCheckpoint)`，格式为 `{"type":"PUBLIC_RESEND","checkpoint":{"sessionId":"...","syncEpoch":"...","cursor":0}}`；cursor 是客户端已应用基线，服务端从 cursor+1 起补发。先验证当前观战绑定角色及 sessionId/syncEpoch；非法或旧流请求不得查询新流窗口。玩家既有 RESEND 载荷保留。

PUBLIC 普通更新 `cursor=fromCursor+1`，全量更新 `fromCursor=cursor/full=true`；join/resume/query 响应都是权威完整基线。ROOM_UPDATE 使用 room version，PUBLIC 使用自己 cursor，二者不可共享 watermark。CLIENT 的 PUBLIC 重排必须以 `fromCursor == 已应用 cursor` 为连续条件；既有 PushSequencer 会放行 `fromCursor == current+1`，不能原样复用。

现有 player `sendPushUpdate` 只有发送和 recordPush，被动 RESEND 不能恢复丢失的最后一条。PUBLIC 必须有最新待 ACK 消息的主动重试/有限调度与低频继续恢复，复用已验证 ROOM_UPDATE 策略。稳定消息 seq 用于全部分片及重试，ACK 对应消息 seq。

新增独立 PUBLIC pending/watermark；同一同步协议原子比较版本并安装 pending，避免旧回调覆盖新待发条目。cancel/send/Future 完成在临界区外。ACK 只清理匹配条目，不能误清 ROOM_UPDATE/KICK；任务回调先验证当前 entry/session/generation。close/resume/leave/timeout 与 late scheduler registration 必须确定性取消所有 PUBLIC 任务，不能仅依赖 timer 以后自行醒来。

先完成 session 注册再安排初始可靠消息，或在注册之后明确重发可信基线；当前 room sender 会拒绝未注册 session，不能假定 bind 时调用一次就必达。真实网络映射不能把 PUBLIC 通过 player mapper/encoder。

## 独立验收建议

1. 两种绑定：四名玩家已占满仍可观战；加入和退出前后四座控制、时钟、牌状态不变。LOBBY/PREPARING/PLAYING/SETTLED/FINISHED 均测试允许的只读行为。
2. 每个伪造私有/写请求逐项拒绝；观战 token 恢复玩家、玩家 token 恢复观战、错误 spectatorId、旧 generation 均失败。拒绝前后权威版本/状态无非法变化。
3. 准备/摸牌/千术/结算/历史完整递归隐私；真公开和牌 ScoreBreakdown、供托归属、历史 outcome 保留。改变私密输入不能把对应负载带到 PUBLIC。
4. 初始订阅与动作竞争、有序 cursor、同 epoch 不回滚、旧 epoch 不覆盖、窗口内补发和窗口外 full baseline；同房重复观战和两次自动恢复。
5. 真实 UDP 代理丢最后 PUBLIC 消息、丢 ACK、重复、乱序、分片丢失；没有后续用户动作仍恢复。收到合法重复消息仍 ACK。
6. 慢/抛异常观战 listener 不阻塞玩家或权威；暂停串行执行器时 API 快速返回未完成 Future；截止时间与 AI 仍受原权威调度约束。
7. 丢离房回执重放、并发离房、旧连接关闭对新连接无效、房主离房正确终结所有观战者但房主回大厅；停服先完成可靠终端通知，再关 socket。
8. 虚拟调度器验证所有 PUBLIC retry 在 leave/close/timeout/替换后取消，含任务注册与关闭竞态；保留已接受 ROOM/player 重试与原有效规则测试。

SERVER 和 CLIENT 全部接线后，由 Codex 串行完整构建/模块回归/独立真实双绑定验收；不在开发进程并行构建。整个 R4 通过后保存验收证据并按用户要求暂停，不能续派 R5/R6。
