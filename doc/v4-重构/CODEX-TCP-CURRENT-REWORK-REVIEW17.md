# TCP 返修第十七轮独立复验：R07 网络断线仍在 Selector 线程完成请求 Future

日期：2026-10-17。Anti 原会话返回 `CODEX_CALLBACK TCP_R07_FIX3 ready_for_review` 后，Codex 独立复验。

第 19 项“慢写断线回调在写锁外运行”独立探针已转绿；源码确认慢写和写异常先出 `writeLock` 后进入 `handleFailure`，正在派发的入站帧也继续计入邮箱预算。Anti 报告其定向、全量和双端构建通过。

新增第 20 项独立真实 TCP 探针 `network EOF must not complete request future on selector thread` 失败，日志 `build/tcp-current-review17-future-thread-probe.log`。真实对端收取一个 `LIST_ROOMS` 请求后立即 EOF；客户端请求 Future 的 `whenComplete` 回调实际在 `TcpClient-IO-*` Selector 线程执行。`handleRead` 在 Selector 线程调用 `handleFailure`，而 `handleFailure` 直接 `req.future.completeExceptionally(ex)`；CompletableFuture 默认同步运行用户回调。慢回调可阻塞该 Session 的读写、心跳和慢写检查，违反设计 §2 的“I/O 循环仅做有界读写、帧校验、排队，不执行用户回调”及“完成 Future ... 保持在状态锁与权威队列之外”。

请让 I/O 线程只发布断线状态和有界失败事件，统一在有界串行工作资源中完成 Future 与通知监听器；不要依赖无界公共线程池。审计 `pendingConnectFuture`、`pendingWelcomeFuture` 及所有 selector 路径的 `complete/completeExceptionally`，保留精确一次失败、顺序和代次门控。第 20 项独立探针不可修改以规避失败。

结论：R07 仍待修；R09/R10 当前功能探针通过，整体及 T4/T5 仍待验收。
