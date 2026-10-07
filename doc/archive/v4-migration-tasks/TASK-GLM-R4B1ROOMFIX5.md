# ROOMFIX4 返修，完成后交付等待验收

沿用同一会话与已有上下文，避免重新通读全仓。先阅读 FEEDBACK-GLM-R4B1ROOMFIX4.md。Codex 实测22项专项通过，完整 server90项88通过2失败，其中 humanSeat=2 牌数已由你修为13，observer等待仍需修。你的交付尚未接受。

仅修改 InProcessBinding 及这两份相关 server 测试，保留所有有效断言。不要执行构建/命令、改 reviewer、改配置或扩大到 UDP。

1. 修 observer 测试：两次 setReady 完成后才设置 targetVersion 会错过已经送达的最终通知，且普通捕获 var 有数据竞争。用事先确定目标和安全通知/回看机制；要求基线先收到时，应先通过 latch 等待基线再变更。严格单调及最终最新版断言保留。
2. 关闭条件必须原子化。`if (isCurrentBinding(...)) handleDisplacedByNewConnection()` 仍会发生：A 回调复核 true，线程暂停；B 完成 leave 再 create 新房；A 恢复执行无条件 close，关闭新房。这是正常 JVM、顺序一致内存下也成立的线程调度，不是“真实 JVM 不出现”的撕裂读。把捕获 room/localGen/seatGen 与 leaving 状态验证、关闭状态认领放在一个小临界区。外部回调/资源释放不在锁内。绑定切换与关闭认领需使用一致的同步协议，避免 check/use 窗口。
3. 离房必须返回完成收尾的 stage，而不是丢弃 pending.whenComplete 的新 stage 后返回 pending。离房标志应绑定具体操作，旧完成不得清除新离房；同连接并发重复 leave 可以复用在途 Future 或明确拒绝，不能互相清标志。
4. 修准确测试：旧房间独立 observer 的屏障不阻塞绑定订阅，不能声称已确定性模拟旧绑定回调。通过可控通知执行器或最小 internal 回调处理入口捕获旧绑定、换房后执行旧回调，并验证不误关；对真实替换仍必须认领关闭。不要加入产品 public 测试 API，不要用 Thread.sleep 假装顺序保证。

交付 delivery-GLM-R4B1ROOMFIX5.md，简述实际改动和未验证项；无需再复述整体架构或生成长论证。Codex 负责最终实测。
