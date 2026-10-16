# TCP 返修第十三轮独立复验：R09 卸载监听器后排队 PUSH 仍会消失

日期：2026-10-17。Anti 原会话返回 `CODEX_CALLBACK TCP_R09_FIX5 ready_for_review` 后，Codex 独立复验。

第五轮改动修复了第 16 项“满队列重挂载静默丢队首”探针；原 16 项强制独立测试通过，日志 `build/tcp-current-review13-independent-forced.log`。源码确认重挂载时不再 `poll()` 丢队首，派发队列增加条数/字节预算，恢复基线入队失败时保留待办并重试。

新增第 17 项独立探针 `queued public update must survive listener unmount before remount` 仍失败，日志 `build/tcp-current-review13-unmount-probe.log`。真实 TCP 观战绑定后，公开流监听器在处理游标 1 时阻塞；游标 2 已排队。卸载监听器并释放回调，派发器从队列取出游标 2，在没有监听器时仍推进 `lastDispatchedPublicCursor`，随后无回调地丢弃。稍后重挂载没有早到消息可排，也没有 `publicRecoveryPending`；无下一条 PUSH 时 UI 永久停留在游标 1。

根因见 `TcpClientBinding.drainPublicQueue`：先 `poll()` 并更新最后派发游标，之后才复制可能为空的监听器集合。`drainPlayerQueue` 与 `drainRoomQueue` 同构。修复应确保无监听器期间的 UPDATE 被保留到有界早期队列，或明确标记恢复待办并在重新挂载后取得全量快照；游标不得在未交付时冒充已派发。需覆盖并发卸载/重挂载、队列上限、close/rebind 代次、玩家/公开/房间三路。

结论：R09 仍退回。R04/R14 已关闭，R07/R10 待修，整体与 T4/T5 仍待验收。第 17 项独立探针不可修改以规避失败。
