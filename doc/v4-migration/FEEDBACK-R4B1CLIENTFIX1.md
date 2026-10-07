# client 首轮验收未完成

日志 build/v4-migration/PARALLEL-R4B-first-modules.log。测试无限等待，Codex 确认线程栈后只终止本轮 Gradle Test Executor 3 PID32384；栈保存在 PARALLEL-R4B-client-thread-dump.log。不能声称 client 通过。

1. UdpClientBindingTest.testDelayedGetRoomInfoQueryDoesNotContaminateNewRoom:886 leaveFuture.join() 卡住。已绑定 connectionId=12345，注入 leave RSP 却用 connectionId=0；生产正确拒绝此身份。修模拟响应，leave 返回后才重置身份；检查另一换房测试同类错误。不得放宽生产连接身份校验。
2. 新增测试裸 join 改为有界等待，失败快速报告，不依赖未推进的假 scheduler 自动超时。保留延迟查询/换房/乱序/ACK 断言。
3. 检查 handleInboundPush 在 synchronized(this) 内直接 dispatchRoomUpdate 调外部 listener 的路径。缓存/version 原子更新，外部回调移到锁外，保持有序和绑定代次隔离；慢/重入 listener 不应卡住离房/关闭。补有意义的有界重入/并发回归。

独立 hostLeaveMustCompleteReceiptAndKeepLobbyConnectionOpen 收到 KICK 后关闭请求链，已交 server 处理。不要掩盖合法 KICK 或改 reviewer。协议冻结。
