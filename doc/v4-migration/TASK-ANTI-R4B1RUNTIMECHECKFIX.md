# Runtime 并发测试装配修复

仅允许 client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt 与交付文档。Anti文件工具开发、Codex运行测试，不shell/build/提交/委派；不改生产/server/protocol/reviewer或构建。小任务直接修复交付。

R4B1-FINALFIX-full-checks.log：client111项110通过1失败。testRoomUpdateObserverInterleavedThreadExecutionNeverDeliversStaleAfterNew:1400 TimeoutException。dispatch只把callback加到dispatchedTasks，createRoom().get却没排水建房完成任务，尚未进入目标并发场景就超时。先保存create Future，排水初始化/建房任务再有界get，清空启动任务后测试v2/v3交错。保留原单调断言，不把dispatcher改回同步掩盖交错。等待latch和join必须检查是否真正完成，捕获子线程异常并回传主测试线程；清理session。

新加高并发测试不应仅Thread.sleep(200)后断言非空与升序，应有界等到最新v30确实收到，并保留升序与线程资源释放。交付 delivery-ANTI-R4B1RUNTIMECHECKFIX.md，说明未运行测试。不扩展任务或改正确生产代码。
