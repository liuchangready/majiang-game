# R4B2 SERVER FENCE：InProcess 观战并发恢复返修

用户指定 Anti 开发、Codex 独立测试。SERVERCHECK2 已退出，服务端主源编译通过；Codex 于 `build/v4-migration/R4B2-SERVERCHECK2-server-module-fix.log` 实测 server 模块 110 项、1 项失败。只负责 `server/src/main/kotlin/com/majiang/server/{inprocess,room}/**`、必要的 `server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`，以及交付文档。不要修改 client、protocol、engine、presentation、UDP 源码/测试或 reviewer 测试。

失败为 `InProcessBindingTest.concurrent resume spectator deterministic race rejects superseded request()` 第 947 行：第一次 resume 的 `onSpectatorDisplaced` 回调被门闩挂起，第二次 resume 本应先完成并使第一次请求拒绝 `DISPLACED_BY_NEW_CONNECTION`，实际第一次成功。耗时 5.018 秒，说明同步通知可能阻塞 TableHost 队列，直到测试门闩超时。请查 `SharedRoom.resumeSpectator` 的 `enqueueOperation {...}.thenApply { notifySpectatorDisplaced(...) }` 与 `InProcessBinding.resumeSpectator/tryClaimSpectatorBinding` 的确切执行时序。修复权威队列被外部回调阻塞和旧恢复成功覆盖新恢复的问题，保持旧连接主动顶号、会话代次及其它公开流/单机合同；不得删减失败断言或改成 sleep/poll 逃避竞态。生产修复应有对应确定性测试。

Codex 已修复 SERVERCHECK2 模块测试遗漏的 `ResendResponse` import。当前独立 review 复测另行进行，若本任务反馈文件有追加须读取。仅用内置文件工具落盘，不运行 shell/Gradle/测试/其他 agent/提交。交付 `doc/v4-migration/delivery-ANTI-R4B2SERVERFENCE.md`，列实际文件、修复原因、未完成项及测试未运行声明。不得进入 R5。
