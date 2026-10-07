# SERVERCHECK2 开发中复核（Codex）

11:24源码进度：UdpRoom.kt 的 `isFullSnapshot = true` 已落盘；UdpRoomTest 的负游标改为构造器边界断言、checkpoint跟随实际cursor也已落盘，感谢。当前开发仍在进行，未运行本批构建。

请交付前再次检查 `testUdpTableServerSpectatorEndToEnd` 后半段。目前源码仍将非法 `SET_READY` 和玩家 `PUBLIC_SNAPSHOT` 的拒绝回执从 `waitForResponse(session.getCachedResponse)` 读取，而生产 `handleSessionReq` 的角色拒绝分支直接 `sendRsp`，在缓存命中前返回，**不会缓存该拒绝响应**。如果仍按缓存查询，会等待3秒后失败；请通过真实UDP数据报读取对应requestId的RSP或可控发送出口观察，保留拒绝码、无私有字段断言。入口异步`room.joinSpectator`后立即读取`spectators.keys.first()`也应稳定等待真实完成，而不是sleep猜时序。原任务已写这些要求，本文确保最后交付前检视当前剩余路径。

11:25复核：最新测试现用 `outboundFrameListener` 捕获RSP，角色拒绝和异步加入接线已改。该测试仍是直接 `handleFrame`＋发送出口捕获、没有真实socket网络往返；请在交付说明中准确表述为帧处理/角色路由测试。真正的UDP socket验收仍由 Codex 独立 V4SpectatorUdpAcceptanceTest 与 V4SpectatorUdpBindingRaceTest 负责。生产新 `outboundFrameListener` 若仅供模块测试，建议至少限制为 `internal`，避免扩展公开服务API；回调异常不能使正常网络发送中断，避免测试钩子成为产品发送路径的可抛失败点。不要为了这条建议弱化捕获到的真实响应断言。
