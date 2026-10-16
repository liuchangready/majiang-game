# TCP 返修第十二轮独立复验：R09 队列满时监听器重挂载仍会静默丢 PUSH

日期：2026-10-16。Anti 原会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R09_FIX4 ready_for_review` 后，Codex 独立复验。

## 已通过

- 第四轮修复将房间、公开、玩家更新放入各自串行派发队列，原 15 项独立探针强制重跑通过，含早到与在线更新的顺序用例；日志 `build/tcp-current-review12-independent-forced.log`。
- R09 标准定向测试强制重跑通过，日志 `build/tcp-current-review12-r09-target.log`。
- 标准全量 `./gradlew.bat test --rerun-tasks --console=plain` 通过，日志 `build/tcp-current-review12-all-forced.log`。

## 新阻断

`TcpClientBinding.onPublicUpdate` 在重新挂载监听器、排水早到消息时，如 `publicDispatchQueue` 已有 128 项，就直接 `poll()` 丢弃队首，再追加早到 PUSH；未保留全量恢复待办，也未显式报告缺口。`onUpdate` 与 `onRoomUpdate` 有同类路径。恢复查询成功时，如果玩家/公开派发队列已满，代码还会先清除 `playerRecoveryPending`/`publicRecoveryPending`，随后跳过完整恢复快照入队，同样可能静默丢失恢复结果。

新增独立探针 `remounting a public listener must not silently drop a full dispatch queue head`：真实 TCP 观战绑定后，用慢监听器阻塞游标 1，排入游标 2–129，卸载监听器，早到游标 130，再挂载。实际游标 2 被丢，且 `isPublicRecoveryPending` 为 false。该探针单独失败，其他 15 项通过；日志 `build/tcp-current-review12-remount-probe.log`。设计 §4 明确写队列满不得任意删事件，§6 要求早到溢出全量恢复及缺口停派。请修复所有同构路径，保留代次和锁外回调，并补回归测试。独立探针不可修改以规避失败。

另外，新增串行派发队列仅有每流 128 项的数量上限，没有字节预算；慢监听器占住一个派发器时，玩家/公开全量快照可积压大量内存。此项应与 R07 客户端有界工作池、慢监听器隔离一起核算。

结论：R09 暂不关闭。R04/R14 已关闭；R07/R10 待修。R01–R16 整体与 T4/T5 仍待验收。
