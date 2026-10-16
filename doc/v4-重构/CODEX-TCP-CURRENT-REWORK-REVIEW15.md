# TCP 返修第十五轮独立复验：R07 新 NIO 路径仍漏算当前写帧并越过单轮限额

日期：2026-10-17。Anti 原会话返回 `CODEX_CALLBACK TCP_R07_R09_R10 ready_for_review` 后，Codex 独立复验。

## 已通过

- 独立真实 TCP 探针 18/18（客户端 17、服务端部分帧预算 1）强制重跑通过，日志 `build/tcp-current-review15-independent-forced.log`。
- 标准全量强制重跑、Android APK 与桌面 jar 强制重建通过，日志分别为 `build/tcp-current-review15-all-forced.log`、`build/tcp-current-review15-dual-build.log`。
- R09 旧 drainer 的 `finally` 现有所有权门控；R10 的 decoder 现接入全局预算，部分帧探针转绿。两项仍需结合最终 R07 改动复验。

## R07 新阻断

1. `TcpClientBinding.handleWrite` 将队首 `ByteBuffer` 移入 `currentWriteBuffer` 时，立即把其 **全部剩余字节** 从 `queuedOutboundBytes` 扣除；但该缓冲仍由连接持有，可能长时间部分写或零写进展。`writeRawFrame` 只按 `outboundQueue.size` 和 `queuedOutboundBytes` 判断 256 帧 / 4 MiB。实际可同时持有当前最多 1 MiB 帧与排队 4 MiB，超过设计 §4 明确要求的“含部分写出的当前帧”。应以当前剩余字节和当前帧数纳入容量准入，随实际写出进展扣账，并在关闭/重连时归零。
2. `handleWrite` 虽在循环前检查 `bytesWrittenThisTurn < 64 KiB`，但对 `sc.write(buf)` 没有限制 `buf.limit`，单个近 1 MiB 帧可一次写出远超 64 KiB，违反设计 §4 每连接单轮最多写 64 KiB 的公平配额。应临时限制 ByteBuffer 的可写窗口并恢复 limit，单次写出不能越过剩余配额。
3. 入站 `workerExecutor` 只限制 **256 个 Runnable**，每个 Runnable 捕获完整 `decodedFrames` 列表；没有入站字节预算，也没有 64 帧 / 4 MiB 的接收邮箱约束。慢监听器下，已解码的大快照可积压到远超设计 §4 的连接预算。应按每帧实际持有字节与帧数准入、出队/拒绝/关闭精确归还；心跳不能因慢监听器饿死。

请补可观测的定向回归，不能只验证排队时的 `queuedOutboundBytes` 或某个单帧通过。R07 未关闭；R09/R10 的已修路径需在下一轮保持。T4/T5 真实环境仍待验收。
