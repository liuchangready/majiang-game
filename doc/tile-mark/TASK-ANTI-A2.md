# Anti：A2 服务端映射、本地和 UDP 接线

2026-10-11：A1 已独立验收，现派发 A2。生产范围 server，测试由 Codex 编写。遵循 DESIGN.md，保留现有 engine 权威和 protocol 严格线路契约，不修改测试、不提交 Git、不新开代理。

## 固定入口及接线

- `EngineProtocolMapper.toEngineTileMarkRequest(ProtocolTileMarkRequest): TileMarkRequest`；配置双向映射保留 tileMarkConfig；私有 PlayerSnapshot -> ProtocolPlayerSnapshot 映射完整 tileMark 及嵌套牌河位置。所有复制、恢复及事件快照路径都保持该字段，公开映射不增加私有字段。
- `ServerTableHost.submitTileMark(seat, request: TileMarkRequest, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt>`。只在既有每桌串行队列中读取身份、连接、控制代次、deadline 并调用 authority.submitTileMark，不能在网络线程预先认定请求有效。保留异步 Future 完成和监听器派发，慢消费者不阻塞队列。
- 成功后沿用 syncDecisionsAndClocks/publishStateChanges，私有状态变化即推送完整快照；不得只依赖公开事件触发。重复返回原有回执和原 cursor，不新增费用或游标。HostReceiptKey 必须区分 mark/action/shuffle，不能互相覆盖。
- SharedRoom、InProcessBinding、UdpRoom 与 UdpTableServer 的真实 SUBMIT_TILE_MARK 分发统一接到上面入口，沿用 sessionValidator 防旧绑定并拒绝未绑定/关闭/观战/断线/托管提交。内部 seat 只取绑定身份，不从请求载荷读取。
- 新服务器 capabilities.tileMarkSupported=true，包括进程内和 UDP 能力查询；配置关闭时业务仍拒绝 TILE_MARK_DISABLED。旧默认房间配置继续关闭。
- 房间配置更新、建房/恢复绑定快照、查询、PUSH、缺口恢复保留完整效果。核对永久离房后是否允许换人占座：若允许，按设计清理旧玩家的私有标记；若不允许，沿用现有规则并在交付中说明。不要借此扩展账户或持久化能力。

## 验证及交付

Codex 已提供 TileMarkServerContractTest（4 项）和 TileMarkUdpContractTest（1 项），覆盖真实本地施术/费用/推送/重试/牌河位置、其他席位与观战隔离、恢复完整快照、权威队列里的身份与截止校验，以及原生 UDP 回环请求/分片快照/重试/观战拒绝。不要用 stub 取代真实绑定。

运行新增定向测试及完整 `:server:test`，失败先修复生产代码，若测试夹具问题明确报告 Codex。完成后写 DELIVERY-ANTI-A2.md、CALLBACK-ANTI-A2.json（recipient codex, status ready_for_review），更新进度并输出 `CODEX_CALLBACK A2 ready_for_review` 后退出。客户端和 UI 尚属后续阶段，不在本任务内修改或宣称可用。
