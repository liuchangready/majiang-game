# R4B2 WIRE：独立观战协议（Codex已授权执行）

Codex已接受PREVIEW/PUBLIC：相关四模块704项全部通过，完整独立134项通过。现明确授权本批WIRE开发，实际bridge调用为执行凭据。Anti 仅使用文件工具开发，Codex 独立运行测试；禁止 shell/build/提交/改设置/再委派。遵守 CONTRACT.md，不保留 V3 入口兼容性。用户最新要求整个R4完成后暂停，本批不得自行启动SERVER/CLIENT或R5/R6。

## 所有权与阶段编译边界

仅 protocol 主源码与其模块测试：server/api 新接口和请求/绑定模型、ServerConnection 组合接口、transport/api 新 PUBLIC 模型与真实 ApiMessage.kt codec。保留 PREVIEW 已接受的玩家预览、严格验证与现有大厅字段。不得修改 engine/server/client/presentation/reviewer/build。

本批可新增 ServerConnection 抽象方法，后续 SERVER/CLIENT 接入批次各自补齐真实具体实现。只由 Codex 运行 protocol 测试验收；期间全工程缺新方法是明确待接线状态，不添加默认 unsupported/no-op/空快照维持表面可编译。protocol 自己的 FakeServerConnection 属于本批测试范围，必须按测试用途补完并验证。不能宣称实际观战已经可用。

## 冻结 API

`ServerConnection : LobbyApi, SessionApi, PushChannel, SpectatorApi, AutoCloseable`。新 `SpectatorApi` 所有方法为 abstract：

```kotlin
fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding>
fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding>
fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot>
fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot>
fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>>
fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable
```

- JoinSpectatorRequest(roomId, spectatorName: String? = null, protocolVersion 默认 ApiProtocol.PROTOCOL_VERSION)。验证非空 roomId 与版本，显示名遵循既有加入请求约定。
- PublicCheckpoint(sessionId, syncEpoch, cursor)，前两项非空、cursor >= 0。裸游标不能跨流使用。
- ResumeSpectatorRequest(roomId, spectatorId, resumeToken, checkpoint: PublicCheckpoint? = null, protocolVersion 默认当前版本)。身份字段非空，日志/toString 脱敏 token。
- SpectatorBinding(roomId, spectatorId, resumeToken, snapshot: ProtocolPublicSnapshot)，不含 seat、玩家快照、玩家决策，token 脱敏。
- 保留 PushChannel.onUpdate 的玩家类型；PUBLIC 使用 onPublicUpdate，统一 onDisconnect。既有 leaveRoom/getRoomInfo/onRoomUpdate 在后续绑定实现中支持两种角色。
- joinSpectator/resumeSpectator 的 binding.snapshot，以及 publicSnapshot/resumePublic 返回值，均是权威全量基线，不是普通连续更新。恢复时验证完整sessionId+syncEpoch+cursor；窗口内丢包补发走独立PUBLIC UDP RESEND，窗口外发送full PUBLIC_UPDATE。无需把256份快照塞进一个恢复响应。

## 独立 PUBLIC 模型

新增 ProtocolPublicSnapshot，包含 sessionId/sessionVersion/roundId/roundVersion、syncEpoch/cursor、board: ProtocolPublicBoardView?、session: ProtocolPublicSessionSummary、clocks、paused、settlementRoundId、settlementEvents。领域 PublicTableProjection 不包含网络游标；此处由 server 后续包装独立流。除字段语义参考外不得 import engine 类型。

公开 board、session、score record、match result 必须逐项映射已接受 engine/projection 实际输出的全部公开字段，保持公开牌河/副露/暗牌计数、场局庄家/分数/时钟、公开历史/最终排名与评分明细。包括PublicScoreRecord.outcome/remainingRiichiSticks、PublicMatchResult.remainingRiichiSticksTransferredTo，不丢弃字段或恒设null；outcome定义协议自己的类型，不能透传领域对象。

禁止字段：myClosedTiles、drawn 实体、sleeves、完整牌山或未公开里宝、准备选择/资源余额/私有流水、decision/options/presentationPreview、玩家控制/连接身份。不能复用含这些私有字段的 ProtocolPlayerSnapshot/ProtocolPlayerView/ProtocolSessionView 作为内部超集，再临时遮盖。

上述身份禁令指controlEpoch/token/generation等授权凭据；已有ROOM_UPDATE的成员姓名、座位、host/ready、HUMAN/COMPUTER是公开大厅信息，观战可读。现有roomConfig.initialPoints/costs是规则配置，不是运行中的余额/预留/选择，不必另造观战房间DTO。不要误删这些公开字段。

ProtocolPublicUpdate(snapshot, fromCursor, visibleEvents, isFullSnapshot)：嵌套不可变，fromCursor >= 0；普通更新 snapshot.cursor == fromCursor + 1；全量基线 fromCursor == snapshot.cursor 且 isFullSnapshot=true。当前 PUBLIC 流独立于四个玩家流。syncEpoch 是公开流代次，连接 generation 是后续服务端独立事实。

所有集合构造/copy/getter 防御复制且不可变，覆盖 board 四家嵌套集合、meld.tiles、结算评分嵌套集合与历史/outcome；equals/hashCode/copy 保持值语义。不得 Any/Map 夹带领域或玩家对象。

## 真实 V4 wire

- ApiTags 添加 JOIN_SPECTATOR、RESUME_SPECTATOR、PUBLIC_SNAPSHOT、RESUME_PUBLIC、QUERY_PUBLIC_ROUND_RESULT。
- ApiRequest 添加 spectatorId、spectatorName、publicCheckpoint；复用 roomId/token/roundId。无客户端可选 role 权限字段，服务端按绑定事实判断。
- ApiResponse 添加 spectatorBinding、publicSnapshot、publicEvents。观战响应不得同时带玩家 seat/snapshot/decision/capabilities/私有events 等混合负载；错误仍遵循现有统一响应。保留原玩家响应语义。
- 混合负载拒绝在出现PUBLIC专属字段时实施；单独的公共roomInfo、receipt以及原合法玩家capabilities响应仍须可用。权限校验由后续绑定server实现，WIRE不能以删除既有合法响应类型替代隔离。
- PUBLIC PUSH 明确 `{"type":"PUBLIC_UPDATE","update":{...}}`，完整 update 的 isFullSnapshot 必填严格 Boolean；提供 encode/decodePublicSnapshot、encode/decodePublicUpdatePush 及公共绑定/请求的实际响应往返支持。
- 当前 isRoomUpdateEnvelope 对任意 type 字段返回 true，会把 PUBLIC 错送房间解码。增加明确 PUSH 分类器（PLAYER/ROOM/PUBLIC），未知 type/非字符串/非法JSON拒绝；不依赖 decode 异常回退猜类型。玩家现有无 type envelope 可保留当前 V4形状，ROOM_UPDATE 保持严格定型。交付中列出 CLIENT 下一批需要改的路由位置。
- 未知字段、缺失必填字段、错误类型/枚举/座位/物理牌/数值边界都严格拒绝，Gson 必须经校验构造，不绕过不可变防御。
- 对 PUBLIC snapshot/update/result 的事件进行明确白名单校验：只允许已接受 PublicProjectionFactory 放行的公开事件，不能让通用 ProtocolGameEvent adapter 接受私密 TileDrawn/CheatPointsChanged 等出现在 PUBLIC 中。数组/深层评分必须保真且不可变；已公开获胜结算可含经服务端授权的里宝详情，不把普通 RoundEnded 视为发布权限。不要过滤全部事件来满足隐私。
- 复用现有 UDP 数据报与可靠性定义，本批不修改 UDP 传输实现。为后续PUBLIC重传冻结严格载荷：`{"type":"PUBLIC_RESEND","checkpoint":{"sessionId":...,"syncEpoch":...,"cursor":...}}`，增加 `encodePublicResendRequest(PublicCheckpoint): String` / `decodePublicResendRequest(String): PublicCheckpoint`。checkpoint.cursor为已应用基线，补发从cursor+1开始；沿用RESEND数据报类型。未知/缺失字段、错误类型和负cursor拒绝，保留既有PLAYER裸fromCursor路径由后续绑定按真实角色区分。不可让PUBLIC重传只携带裸游标。
- 后续CLIENT使用独立PUBLIC连续检查（普通更新fromCursor必须等于当前基线），不能直接沿用现有PushSequencer中fromCursor<=current+1的宽松判断。本批不修改该旧组件；在交付中明确提示SERVER/CLIENT对齐上述重传约定和独立代次。

## 模块证据和交付

protocol 测试覆盖公开准备/局中/结算/历史快照、请求/响应/绑定/typed PUSH与PUBLIC_RESEND往返；混合私有字段/私密事件拒绝；PUBLIC 普通连续更新与全量基线；元数据/未知字段/错误类型拒绝；嵌套源集合与getter修改不影响值；token日志脱敏；旧玩家preview/ROOM_UPDATE仍保真。

交付 `delivery-ANTI-R4B2WIRE.md`，给出最终精确签名/字段、SERVER/CLIENT需补的实现列表，以及未运行测试声明。结束前读取 FEEDBACK-R4B2WIRE.md（若有）。Codex只接受协议契约后再启动 SERVER 与 CLIENT，不自动跳R5/R6。
