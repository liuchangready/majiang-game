# TCP 返修第十六轮独立复验：R07 慢写断线回调仍在写锁内

日期：2026-10-17。Anti 原会话返回 `CODEX_CALLBACK TCP_R07_FIX2 ready_for_review` 后，Codex 独立复验。

本轮已修复当前部分写帧的字节/帧准入、单次写出 64 KiB 限额，并加入 64 帧 / 4 MiB 入站邮箱。原 18 项独立探针强制重跑通过，日志 `build/tcp-current-review16-independent-forced.log`；标准全量强制重跑与 Android APK、桌面 jar 构建均通过，日志 `build/tcp-current-review16-all-forced.log`、`build/tcp-current-review16-dual-build.log`。

新增第 19 项独立真实 TCP 探针 `stalled write disconnect callback runs outside write lock` 失败，日志 `build/tcp-current-review16-write-lock-probe.log`。探针令当前写帧停滞，注册断线回调；该回调启动另一线程尝试取 `writeLock` 并在 500 ms 内等待。实际第二线程直到回调返回才取得锁。

根因：`TcpClientBinding.checkStalledWrite` 在 `synchronized(writeLock)` 内调用 `handleFailure`；`handleWrite` 写异常 catch 路径同样在该锁内调用 `handleFailure`。后者同步完成在途 Future、调用 `notifyDisconnect`，使任意用户回调在写锁内运行；慢回调可拖住 Selector 与其他发送，违背设计 §2 的“完成 Future 和调用监听器保持在状态锁之外”及 I/O 循环职责边界。应先在锁内确定失败，再出锁处理失败与通知；避免关闭路径锁序反转，保留一次断线语义。

另请核对入站邮箱：`drainInboundMailbox` 在交付前已从 `inboundMailboxBytes` 扣除当前帧，但回调慢时该帧仍由工作线程持有；实际占用应与设计 §4 的接收预算一致，不能只统计尚未取出的帧。

结论：R07 未关闭；R09/R10 当前探针转绿，整体与 T4/T5 仍待验收。第 19 项独立探针不可修改以规避失败。
