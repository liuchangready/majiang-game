# 最后一项进程内房间通知测试时序返修

独占 server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt；只文件工具，不shell/build/提交/再委派，不改生产、其他模块、reviewer或构建。Codex已完成最新server/client回归，client111项全部通过，server101项100通过1失败（R4B1-CHECKFIX-modules.log）。

失败 host leave succeeds while guest receives room closed without UI subscription 第516行：guestConn.closed与disconnectReason已满足，但立即断言guestDisconnectReasons包含ROOM_CLOSED为false。closed可见与锁外listener实际执行不是同一时刻。用CountDownLatch或同类线程安全信号有界等待真实onDisconnect回调，再断言全部closed/reason/listener收到原因；不能删除断言，不能把用户回调移回核心锁。

保留guest后续被拒、host继续创建新房的有效断言；所有等待有上限，资源清理不变。只做本项必要修复。交付delivery-ANTI-R4B1ROOMTESTFINAL.md，说明未运行测试。桥接将自动通知Codex继续验收，无需呼叫另一个Codex CLI。
