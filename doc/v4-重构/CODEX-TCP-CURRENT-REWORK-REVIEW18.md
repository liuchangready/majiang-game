# TCP 返修第十八轮独立复验：R07 工作池满载时绕过有界预算新建线程

日期：2026-10-17。Anti 原会话返回 `CODEX_CALLBACK TCP_R07_FIX4 ready_for_review` 后，Codex 独立复验。

第 20 项“网络 EOF 的请求 Future 不在 Selector 线程完成”独立探针已转绿。原 21 项独立探针（客户端 20、服务端预算 1）强制重跑、标准全量测试、Android APK 与桌面 jar 强制重建通过，日志 `build/tcp-current-review18-independent-forced.log`、`build/tcp-current-review18-all-forced.log`、`build/tcp-current-review18-dual-build.log`。

新增工作池饱和独立探针 `saturated callback worker must not spawn unbounded fallback threads` 失败，日志 `build/tcp-current-review18-worker-saturation-probe.log`。探针占住单线程 worker，再填满其 256 项队列，模拟 Selector 线程提交完成回调；实际回调运行于 `TcpClient-WorkerFallback-*` 临时线程。

根因是 `TcpClientBinding.executeOnWorker` 在 `workerExecutor` 拒绝任务或关闭时，无条件 `Thread(...).start()`，每次失败均可产生新守护线程，没有线程数、排队字节、存活时间上限。该分支绕过设计 §2 的“有界工作池”与“不用无限 cached pool 代替阻塞治理”；并发连接和慢用户回调可不断制造线程。需要有界的高优先级完成/断线通道或其他可证上限机制；不能在 Selector 线程回退同步执行，也不能无声丢 Future 完成。覆盖队列满、关闭、重连与精确一次终态。

同时，Anti 第四轮交付称“彻底移除 `CompletableFuture.runAsync`”，源码 `connectInitial` 和 `reconnect` 仍使用该方法；这项文档声明需纠正或明确适用范围。

结论：R07 仍待修；R09/R10 的现有探针通过，整体及 T4/T5 仍待验收。新增独立探针不可修改以规避失败。
