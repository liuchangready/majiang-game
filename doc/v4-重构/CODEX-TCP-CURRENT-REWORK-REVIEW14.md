# TCP 返修第十四轮独立复验：R09 功能探针转绿，串行门控竞态仍待修；R10 独立实证失败

日期：2026-10-17。Anti 原会话返回 `CODEX_CALLBACK TCP_R09_FIX6 ready_for_review` 后，Codex 独立复验。

## 已通过

- R09 的 17 项独立真实 TCP 探针强制重跑通过，含满队列重挂载和卸载后排队 PUSH 保全；日志 `build/tcp-current-review14-independent-forced.log`。
- 标准全量 1,256 项强制重跑通过，日志 `build/tcp-current-review14-all-forced.log`。Android APK 与桌面 jar 强制重建通过，日志 `build/tcp-current-review14-dual-build.log`。
- 源码确认三路 `drain*Queue` 在监听器为空时不弹出队首，不推进游标；重挂载可重新排水。

## 待修

1. **R09 串行派发门控残余竞态**：三路 `drain*Queue` 在内层末尾调用 `*Draining.set(false)` 并尝试重新获取，外层 `finally` 又无条件 `set(false)`。线程 A 在第一次释放门控后，线程 B 可获取门控并开始回调；A 的 finally 再将 B 的门控清掉，线程 C 因此可与 B 并发排水，破坏同流严格有序交付。应改成所有权明确的单 drainer 状态机，不能无条件释放别人的门控。
2. **R10 实证失败**：新增独立真实回环探针 `TcpBudgetReviewTest.partial inbound frame reserves global bytes before payload allocation`；服务端已接收并持有部分 TCP 帧载荷时，`globalBudget.currentAllocatedBytes` 未计入解码暂存。日志 `build/tcp-current-review14-server-budget-probe.log`。设计 §4 要求预留后分配并覆盖解码暂存。
3. **R07 静态阻断仍在**：客户端仍以阻塞 `Socket` / `OutputStream.write` 发帧，在读线程执行解码/监听器回调；不满足设计 §2 的可复用非阻塞 I/O 与有界工作池。

结论：R09 的已复现功能缺陷转绿，但串行门控尚不能独立关闭；R07/R10 待修。R04/R14 已关闭。R01–R16 整体与 T4/T5 仍待验收。
