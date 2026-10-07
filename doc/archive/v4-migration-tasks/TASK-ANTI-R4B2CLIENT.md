# R4B2 CLIENT 实际观战接入（Codex明确授权执行）

16:10 Codex已接受WIRE：protocol模块136/136、独立协议13/13全部通过。PREVIEW/PUBLIC此前接受，现按真实签名核定并授权本批，bridge实际调用为执行凭据。Anti仅文件工具开发，禁止shell/build/提交/改设置/再次委派；Codex统一测试验收。用户最新边界为完成整个R4后暂停，绝不进入R5/R6。

实际WIRE交付见delivery-ANTI-R4B2WIREFIX.md和delivery-ANTI-R4B2WIREFINAL.md（原WIRE未写delivery，由这两批补完并验收）。ApiMessageCodec.classifyPush(String):PushType（PLAYER/ROOM/PUBLIC）、encode/decodePublicSnapshot、encode/decodePublicUpdatePush、encode/decodePublicResendRequest已落盘。ResumeSpectatorRequest字段为resumeToken/checkpoint；ApiRequest对应token/publicCheckpoint。SpectatorBinding含roomId/spectatorId/resumeToken/snapshot，不是玩家RoomBinding。精确字段/签名读取protocol实文件，保持protocol冻结，缺口报告Codex协调。

先读CONTRACT.md、已接受WIRE交付及`R4B2-CLIENT-REVIEW-NOTES.md`。该notes的所有权、独立PUBLIC缓存/身份/同步/生命周期与验收要求作为本任务具体要求；实际接口以已接受WIRE为准，缺口报告Codex协调，不跨写protocol。

## 独占修改范围

client/api下ServerConnectionSession、LobbySession、新SpectatorSession/PublicViewModels、ProtocolViewMapper（可拆新公共mapper），transport/udp/UdpClientBinding及必要UdpClient/调度器装配，对应client模块测试/fakes。server和engine归SERVER，protocol冻结；不改V3 network、EngineDtoMapper、presentation、平台入口、build、reviewer。

## 客户端门面签名

沿用现有LobbySession建房/加入返回初始快照的风格，新增：

```kotlin
fun joinSpectator(roomId: String, spectatorName: String? = null): CompletableFuture<PublicSnapshotView>
fun resumeSpectator(roomId: String, spectatorId: String, resumeToken: String,
    checkpoint: PublicCheckpoint? = null): CompletableFuture<PublicSnapshotView>
```

`PublicCheckpoint`是client.api自有不可变值类型(sessionId,syncEpoch,cursor)，不能typealias协议类型。`SpectatorSession : AutoCloseable`提供`currentPublicSnapshot: PublicSnapshotView?`、`publicSnapshot()`、`resumePublic(checkpoint: PublicCheckpoint)`、`queryPublicRoundResult(roundId)`、`onPublicUpdate(listener: (PublicUpdateView)->Unit)`，以及closed/disconnectReason/connectionState、onStateChange/onDisconnect、getRoomInfo/onRoomUpdate、leaveRoom/close的只读及生命周期能力；所有查询/离房返回CompletableFuture，订阅返回AutoCloseable。与GameSession同名且同语义的生命周期方法可由ServerConnectionSession共用实现，但SpectatorSession不继承GameSession/LobbySession的玩家写接口。客户端公开快照/更新分别命名PublicSnapshotView/PublicUpdateView，公开结算事件返回List<GameEventView>并保留PUBLIC白名单。其余嵌套公开类型参照WIRE实际字段、采用客户端自有名称。

## 完成标准

- 门面为客户端自有的异步观战加入/恢复及只读SpectatorSession。公开DTO逐字段保真、深层不可变，公开签名不泄漏protocol/engine，不能包裹私有GameSnapshotView或继承可写GameSession。
- 未绑定/玩家/观战互斥状态，独立PUBLIC缓存/listener/早期缓冲/重排/恢复；私有查询或写入提前失败，服务端仍须强制授权。错角色推送不污染另一缓存。
- 公共绑定基线与早期推送在同一session/epoch有序合流；普通update.fromCursor必须等于当前cursor，重复/缺口/乱序不跳过。不能复用旧PushSequencer允许current+1的宽松条件。
- 真实SpectatorApi、typed PLAYER/ROOM/PUBLIC分类、严格PUBLIC_RESEND codec及完整checkpoint接线。checkpoint.cursor为已应用基线，不手拼JSON、不用decode异常猜类型，保留ROOM重复有效消息ACK等现有正确行为。
- 完整生命周期自动恢复使用spectatorId/token/public checkpoint，不能调用玩家resume。新基线/旧Future/旧callback/旧epoch/同room重建隔离。恢复失败能继续重试；永久终结不自动抢占。
- PUBLIC listener、连接监听、timer、receiver与outstanding Future在leave/close/替换/失败时收尾；用户回调在锁外。正常离房可重新加入玩家或观战。
- module fakes补完新抽象方法并可控延迟/异常/推送；保留PLAYER/ROOM/preview全部有效断言。Codex自有reviewer proxy由Codex更新，你不改。
- 模块测试覆盖notes所列真实公开字段/隐私/顺序/生命周期风险。跨真实UDP最后更新丢包、双次恢复等由Codex在双方完成后验证，不能声称已通过。

交付`delivery-ANTI-R4B2CLIENT.md`，列具体文件、实际客户端API、对SERVER的注意点、未完成项，明确未运行测试。结束前读取FEEDBACK-R4B2CLIENT.md（若存在）。源码交付不等于验收通过。
