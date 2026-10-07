# R4B2 SERVER CONT 运行中检视反馈（Codex）

18:09 静态检视；本批仍在开发，尚未运行构建，不是最终验收结论。已看到真实 PUBLIC 快照构造、完整 checkpoint 校验、initialState 生命周期和主动替换通知落盘。

请补齐新观战绑定的异步提交边界：当前 InProcessBinding.joinSpectator/resumeSpectator 捕获 bindingGeneration 只给队列内 preValidator，thenApply 直接 bindSpectator；bindSpectator 锁内只检查 !closed，并未验证请求开始的本地代次、当前未绑定状态、SharedRoom 当前 spectator generation。completionExecutor 是 cached pool，完成顺序不等于权威队列顺序，因此两个请求可在尚未绑定时同时通过，较早完成回调迟到覆盖较新绑定、并 detach 后者订阅。新主动 displaced 通知在完成链中，也存在通知发生在旧请求尚未订阅之前的窗口，不能仅依赖通知修复。

请让同一连接的观战 join/resume 具有明确互斥/请求代次认领，并在真正绑定和安装订阅前重新核对本地请求身份及共享最新代次；迟到结果不能覆盖新状态、不能泄漏加入成员或订阅。close/leave 与安装订阅也需同样保证，回调和资源 close 仍在锁外。不要为了这个修复重构已接受的旧 PLAYER 行为，但新观战不能悄悄抢占已有 PLAYER。

可加入确定性模块回归：利用 SharedRoom.onSpectatorDisplaced 的测试监听器阻塞某次恢复的 completion，先完成另一次请求再释放旧 completion，证明仅最新合法绑定生效或明确拒绝并发请求；不依赖随机 sleep。

另外 SpectatorMember.generation/resumeToken 为普通可变字段，写在权威线程、读在各传输/通知线程；ConcurrentHashMap 只发布成员引用，不保证后续成员字段写入可见。请使用明确的可见性方案（如 immutable member replacement / volatile / 统一同步），确保旧连接权限与主动通知判断能可靠看到最新代次。

原 FEEDBACK-R4B2SERVER.md 与完整 TASK 要求仍有效，尤其真实 UDP 接线、主动 ACK 重试和终结清理，不能只交上面局部修复。

18:12 新 UdpRoom 静态问题：lastPublishedPublicSnapshot 声明为 AtomicReference<ProtocolPublicSnapshot?>，但 updatePublishedPublicSnapshot 的 current、getPublishedPublicSnapshot 返回值、handlePublicResend 的 published 均直接使用 AtomicReference 本身，随后访问 cursor/sessionId/syncEpoch 或 compareAndSet 类型均不符。读取需要真实 .get()，不是删类型检查。handlePublicResend 还需拒绝 checkpoint.cursor 超前于权威 published.cursor，再执行 cursor + 1，避免 Long.MAX_VALUE 溢出成负数触发错误全量恢复。保持 protocol 冻结。

## 18:17 GLM额度退出后的实际编译与剩余接线

GLM两路均因429/1308五小时额度退出；本批未交付，禁止声称完成。R4B2-GLM-quota-compile.log 实测 client 主源码编译成功、server 失败：上述 UdpRoom AtomicReference 问题全部重现，另 UdpTableServer 缺 PUBLIC_UPDATE_RETRY_BASE_MILLIS / PUBLIC_UPDATE_MAX_ATTEMPTS / PUBLIC_UPDATE_REFRESH_MILLIS 常量。

更关键的未完成生产接线：当前 UdpTableServer 仅有 JOIN_SPECTATOR / RESUME_SPECTATOR 大厅分支，还没有 PUBLIC_SNAPSHOT / RESUME_PUBLIC / QUERY_PUBLIC_ROUND_RESULT 的会话路由和观战权限 gate；sendPublicUpdate 未传入 UdpRoom 创建/包装的 publicUpdateSender，refreshPendingPublicPushes 仅定义未接 ticker。先补完整真实行为，不能只让编译转绿。终结 paths 的 inFlightPublicPushes 清理和权限先于缓存命中需完整复核。PUBLIC_RESEND 当前发送临时新 seq，不进入可靠待确认记录，须对照完整重试/丢包要求验收。
