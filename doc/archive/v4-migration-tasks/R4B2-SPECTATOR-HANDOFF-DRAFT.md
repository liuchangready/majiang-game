# R4B2 观战接入分工草案（尚未派发）

本文件仅为当前PREVIEW/PUBLIC完成后的接入准备，不授权运行中的开发者跨范围修改。最终接口须在PUBLIC实际类型验证后由Codex冻结，不能把草案当已实现功能。

## 必须保持的边界

- 观战者拥有独立membership、spectatorId、resumeToken与generation；四座玩家数组/控制权/AI状态不受观战加入或退出影响，不以seat0或-1充当玩家。
- 同一传输连接只能处于未绑定、玩家、观战三种明确状态之一，恢复/替换时旧generation失效。角色是服务端绑定事实，不信任请求传入角色。
- 玩家快照及其私有presentationPreview永不经过观战传输。PUBLIC从新PublicProjectionFactory生成，包含公开桌面/分数/时钟/历史，严禁资源余额及私有事件。
- PUBLIC有独立syncEpoch/cursor、256条窗口、明确full-recovery baseline，完整快照PUSH与可见事件，不以任一座位游标代用。
- 单机与UDP共用SharedRoom和ServerTableHost业务序列；通知/序列化/外部Future完成不占权威状态修改临界区。

## 拟定公共边界

增加独立protocol SpectatorApi（不能用默认空实现掩盖未实现）。候选接口：joinSpectator(request)、resumeSpectator(request)、publicSnapshot()、resumePublic(cursor)、onPublicUpdate(listener)。加入/恢复返回独立SpectatorBinding(roomId,spectatorId,resumeToken,publicSnapshot)，不夹带玩家RoomBinding。

公开snapshot/update使用独立ProtocolPublicSnapshot/ProtocolPublicUpdate，映射域PublicTableProjection；客户端有独立PublicSnapshotView/PublicUpdateView，public facade不泄漏protocol/domain。沿用真实ApiMessageCodec，新增明确PUBLIC_PUSH类型，字段和私密事件类型严格验证。

既有ServerConnection的两种实现可实现SpectatorApi，client内部统一适配；是否将接口组合到ServerConnection须在协议批次统一处理所有消费者，不能遗留UnsupportedOperation默认壳。client对外必须提供实际可用的观战加入、缓存、订阅、恢复、离房生命周期，不能只公开DTO。

## 拟定后续批次

1. WIRE：明确SpectatorApi、独立binding/public协议DTO与严格codec；保留PREVIEW字段。该批只冻结公共契约，不声称观战接入完成。
2. SERVER 与 CLIENT 并行：SERVER独占共享观战成员/权威PUBLIC流/进程内和UDP角色验证及domain映射；CLIENT独占UDP公开消息、client视图映射/运行时/自动恢复。共享protocol冻结。跨端都完成后统一编译与真实测试，失败回指定所有者。
3. 集成验收：满四人房间仍可观战；伪造SUBMIT/SHUFFLE/READY/START/CONFIG/CONTROL/DECISION/私人SNAPSHOT/私人结果查询一律拒绝；PUBLIC只读查询/离房允许。玩家token不可冒充观战token，观战token不能恢复玩家座位。
4. 丢最后公开更新、丢ACK、重复/乱序/分片重试收敛；窗口内补发及窗口外full恢复；两次断线恢复；旧连接替换失效；观战离开令牌撤销；host离房/停服准确终结观战连接。

测试必须验证完整nested privacy（准备/局中/结算/历史），公开变更会推进PUBLIC cursor而私人事件不会泄漏内容；独立审核最后不以过滤所有事件/删除公开结算详情来满足隐私。

R4B2全部通过之前不进入R5产品入口切换。R5之后再做R6引用审计与V3删除。

## 14:07 接口接线审查补充（仍未派发）

建议 `ServerConnection` 直接组合独立 `SpectatorApi`，其方法全部 abstract：`joinSpectator(JoinSpectatorRequest)`、`resumeSpectator(ResumeSpectatorRequest)` 返回 `CompletableFuture<SpectatorBinding>`；`publicSnapshot()`、`resumePublic(PublicCheckpoint)` 返回 `CompletableFuture<ProtocolPublicSnapshot>`；`queryPublicRoundResult(roundId)` 返回公开事件列表；`onPublicUpdate(listener)` 返回注销句柄。`PublicCheckpoint` 明确 sessionId、syncEpoch、cursor，不能只发送裸游标并把不同房间/代次的数字当同一历史。

保留现有玩家 `PushChannel.onUpdate` 类型及统一 `onDisconnect`，观战使用独立 public 回调；不要把参数改成 Any/玩家快照超集。既有 `leaveRoom/getRoomInfo/onRoomUpdate` 同时适用于玩家和观战绑定。WIRE 只进行 protocol 模块验收；ServerConnection 新增 abstract 方法在下一 SERVER/CLIENT 批共同补齐，期间明确全工程尚不可编译，不能加空实现隐藏缺口。所有测试 fake 也由其所属任务补完。

真实 codec 的 `isRoomUpdateEnvelope` 当前对任何含 type 字段的 JSON 都返回 true；新 PUBLIC 推送会被误送房间解码。WIRE 应增加明确分类器（PLAYER/ROOM/PUBLIC，未知类型拒绝），CLIENT 路由同步接入，不靠异常回退识别。PUBLIC 信封建议 `type=PUBLIC_UPDATE`、update 包含 snapshot/fromCursor/visibleEvents/isFullSnapshot；严格 Boolean 全量标记。普通更新 cursor=fromCursor+1，绑定/恢复全量基线 fromCursor=cursor、isFullSnapshot=true。

复用既有 UDP PUSH/FRAG/ACK/RESEND 数据报，不新增第二套网络栈。只用 256 条被动重传窗口无法自愈“最后一条公开更新丢失且之后没有新动作”；SERVER 必须保留当前连接最新 PUBLIC 待 ACK 消息，按稳定 seq/分片重试，ACK/替换/离房/停服清理。该状态不进入领域投影，不能复用某个玩家的游标或重传空间。

权限矩阵：未绑定可列房/创建/加入玩家/加入观战/按角色令牌恢复；PLAYER 才能读取私有快照/决策/私有局结果及执行准备/配置/控制/动作；SPECTATOR 只能公开快照/公开历史/恢复、公共房间信息和离房。混用令牌、篡改客户端角色、旧 generation 调用必须拒绝且不影响权威状态。后续正式任务须按实际 PUBLIC DTO 冻结 wire 字段与严格隐私白名单，不让通用 ProtocolGameEvent 解码成为私密事件后门。
