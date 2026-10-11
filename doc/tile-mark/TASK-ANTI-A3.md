# Anti：A3 客户端门面、映射与恢复

2026-10-11 22:13：A1/A1FIX1/A2 已通过 Codex 独立验收，现派发。生产范围 client。Codex 独占测试维护；禁止修改测试、Git 提交、新开代理或提前修改表现层。

## 独立 API

`client.api` 只定义独立不可变类型：`TileMarkConfig`、`TileMarkRequest`、`TileMarkArea`、`MarkedTileView`、`TileMarkGroupView`、`TileMarkView`、`RiverPositionView`，字段与已验收线路契约对应，牌面为 TileView，不引用 engine/server/protocol 类型。配置 MatchConfig 及 Capabilities 加入 tileMarkConfig/tileMarkSupported（默认关闭/false）。GameSnapshotView 增加可空 tileMark，所有手写 copy/equals/hashCode/deepCopy 保留并防御复制。

GameSession 增加 `submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt>`；接口默认实现明确失败，避免旧假实现误宣告支持。SpectatorSession 不增加写方法。

## 映射与运行时

- `client.mapping.ProtocolViewMapper` 显式转换全部配置、能力、请求、快照及位置；新增 `toProtocolTileMarkRequest`。实现在 mapping/session 包，不能搬回 client.api。
- `client.transport.udp.UdpClientBinding` 真正覆盖 SessionApi.submitTileMark，用 SUBMIT_TILE_MARK 与相同信封 requestId 发送完整载荷，沿用可靠请求、错误路径和关闭 Future 机制。
- `client.session.ServerConnectionSession` 实现 GameSession 方法，遵循现有 submit/submitShufflePlan 的 pendingState、actionSubmissionSeq、connection epoch、room epoch、trackFuture、dispatch 和 close 规则。其他写请求在落汗在途时也必须 ACTION_IN_FLIGHT；被拒/异常/取消/关闭/换绑时正确释放旧 Future。旧动作晚到回执不能解除新动作在途锁，不能覆盖较新快照。
- 标记始终来自完整快照。空组、单组消失、tileMark=null 都按完整替换，不能合并旧组；重复/乱序/缺口恢复沿用既有有界缓冲（64）和去重（256）。PUSH、查询、resume、重连和新绑定使用相同映射；退出房间或转观战清除私有缓存。
- 当前 API 暂不要求表现层动画；绝不能用客户端计数代替权威三局期限或主动乐观扣费。

## 验证与交付

Codex 已移入 TileMarkClientContractTest（client 测试 7 项，含新增自动重连及旧连接晚到回执）及 TileMarkClientUdpIntegrationTest（server 测试 1 项真实客户端 UDP）。既有 ServerConnectionSessionTest.FakeServerConnection/FakeUdpClock/FakeUdpScheduler 已由 Codex 改为 internal，并加入标记延迟/失败与 resumeSession 自定义响应以复用测试夹具。用例覆盖映射、请求透传、在途锁与异常、快照先于回执/乱序/空效果清理、换绑/关闭/观战隔离，以及真实客户端 UDP 到服务端的集成。运行新增定向及完整 :client:test、:server:test。失败改生产直至通过；测试自身问题准确报告 Codex，不得改测试。

写 DELIVERY-ANTI-A3.md、CALLBACK-ANTI-A3.json，更新 PROGRESS-ANTI.md，输出 `CODEX_CALLBACK A3 ready_for_review` 后退出。通过独立验收后才转表现层。

## A2 交付文档更正（随本阶段完成）

Codex 源码检视发现 DELIVERY-ANTI-A2.md 含不准确的示例名：实际 HostReceiptKey 是 Action/Shuffle/Mark；错误原因是 STALE_SESSION/SEAT_DISCONNECTED/SEAT_NOT_HUMAN；没有 ActionReceipt.staleSession、deadlineExpired、updateDecisionClock 等 helper。实际先调用 authority 校验/回放，再由 issuedReceipts 保持原 cursor；截止判定在 authority 内，UdpRoom 用真实 sessionValidator(session)，不是文档中虚构的 lambda。按当前源码简化并更正这些段落，不必修改已经通过的 server 生产实现。以后只在 doc/tile-mark 下写回调文件，不在仓库根目录重复写 CALLBACK。
