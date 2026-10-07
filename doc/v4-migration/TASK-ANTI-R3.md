# R3 客户端同步与自动重连

仅在 R2 验收后执行。Anti 使用文件工具开发，Codex 执行测试。先读 CONTRACT、R2 交付、旧 GameClient/ClientSynchronizer 的实际行为和已有并发/恢复测试。

前置条件已满足：2026-10-05 20:13 R2 验收通过，52 项独立检查、721 项模块测试与双端构建成功。参见 ACCEPTANCE-R2.md。请同时读取 ACCEPTANCE-R3.md 的行为矩阵。既有 ServerConnectionSession 是直通门面，应该演进或替换为唯一 V4 runtime；不要只加一个未接入的新类。保持旧展示可编译直到 R5，不提前清理旧产品链。

## 授权范围

client 新 V4 api、sync、transport 及其模块测试；确需补通用契约字段时可修改 protocol V4 契约和相应 server 映射调用点，但须交付完整可编译接线。禁止修改规则、展示、review-owned 测试与设置。

## 交付

- 统一 V4 客户端运行时，封装任意 ServerConnection。LobbySession/GameSession 共享连接与生命周期，缓存不可变最新视图，具备 isPending、连接状态、可观察更新、只读错误/断线原因。
- 先订阅再初始化，早期推送有界缓存，snapshot/RSP/PUSH 按 sessionId+syncEpoch+cursor 对账，不回滚新视图；新绑定重置序列。缺口补发/全量请求在途去重，恢复失败释放标记并可重试。
- 请求在途、回执拒绝、传输错误、同步异常、关闭、取消和晚回包都正确完成 Future；玩家意图以最新 decisionId/optionId/controlEpoch 提交。禁止恢复 raw GameAction 兜底。
- 可注入 dispatch 统一监听器与 UI 消费回执线程；锁内不调用用户代码，关闭后不通知，旧代次回调无效。原 V3GameClientAdapter 的线程安全不能丢失。
- 连接工厂+时钟/调度器实现指数退避自动重连，保留 UI 快照与 roomId/token/cursor。只有意外可恢复掉线重试；主动退出/关闭、ROOM_CLOSED、SERVER_SHUTDOWN、被顶号不自动抢占。恢复绑定后对账、恢复订阅；显式 claimControl 才收回，AI 已做动作和权威时钟不回滚。
- 提供 connected/connecting/reconnecting/closed 等明确状态，表现层不识别具体 Local/Udp 类型。进程内绑定不创建心跳/网络重连线程。
- 假时钟、假连接覆盖乱序、缺口、切房、顶号、延迟回执、订阅早包和反复断网；真实 UDP 端到端由 Codex 验证。可复用旧同步算法但不要保留两套产品同步状态。

交付 delivery-ANTI-R3.md，说明最终产品使用的唯一 runtime、所有生命周期状态/转换和测试范围。旧 GameClient 在 R5 接线后由 R6 删除，不能提前删仍被展示引用的实现。
