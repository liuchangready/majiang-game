# C2 客户端同步与异步生命周期返修

仅修改 sync/**、client/GameClient.kt、access DTO中必要方法。不要修改D正在实现的host/**/access/local/**或其他生产文件。Codex测试已放 core/src/test/kotlin/com/majiang/v3/ClientSynchronizationAcceptanceTest.kt、AsyncClientAcceptanceTest.kt。不要修改测试。

1. ClientSynchronizer.accept仅核验epoch，缺少sessionId与viewerSeat身份核验。foreign session必须STALE；游标连续不能使跨比赛更新进入当前视图。
2. eventSignature使用内容去重会把不同round内相同StackingStarted(0)等合法事件吞掉。按流批次cursor去重即可；一个合法新批次的事件不能仅因为内容相同而丢失。
3. 每监听器独立复制GameEvent含newScores数组，不能让第一监听器修改后污染其他监听器。GameClient.notifyListeners同样修正。
4. connect缓冲早期更新时忽略GAP，导致永久停在旧快照。必须处理缺口恢复，且并发/重复connect不得泄漏订阅。快照失败后恢复连接状态并释放本次订阅。
5. close后延迟connect/resume完成不得重新install或通知。使用连接代次/关闭守卫保护异步回调。
6. resume在途期间收到的新更新不能被较旧resume快照回滚。缓冲/合并恢复期间更新，以同一epoch的cursor防回退；新epoch正确切换后只应用该流更新。处于gap/恢复时不能choose旧候选。恢复失败维持不同步状态，不假装可操作。
7. dispatch可能异步排队，监听器交付时再次检查生命周期，不能在close后回调UI；并发更新通知顺序一致。
8. ClockView不应该强制单调时钟源非负，System.nanoTime允许负原点；只要求deadline>=opensAt，reserve>=0，时差合理。
9. 架构说明：你提供的LocalSessionAccess/BoundMahjongGameAccess属于过渡直接适配，D会提供真正TableHost受控实现，暂不继续扩展它们；后续清理时会删除未使用的重复路径。

保留完整物理牌匹配、pending、所有DTO隔离。交付delivery-C2.md列出修复，不声明网络已经实现。
