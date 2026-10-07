# 首轮验收未通过

实际日志 build/v4-migration/PARALLEL-R4B-first-modules.log、PARALLEL-R4B-first-review.log。独立 112 项中 111 通过、1 失败；server 94 项中 92 通过、2 失败。client 卡住另行返修。不得改 reviewer 测试。

1. V4ReviewAcceptanceTest.hostLeaveMustCompleteReceiptAndKeepLobbyConnectionOpen 第 106 行 leaveRoom().await() 抛 ReliableRequestor has been closed；栈经 UdpClientBinding.onInboundFrame:291 的 KICK 关闭路径。房主主动离房须完整返回 APPLIED，保留大厅连接，其他成员正确终止。检查 UdpRoom leaveExclusion/SharedRoom 异步关闭通知竞态；不得让客户端忽略所有 KICK。补可重复真实生命周期回归，覆盖回执及后续 list/create。
2. UdpRoomTest.testResumeSessionDisplacesOldConnection:188 立即断言 oldSession.isClosed 失败。kick/close 已移到异步执行器。保留失效/关闭/kick 全部断言，采用线程安全信号、有界等待验证实际完成；不能把慢 kick 放回权威/席位锁。新连接生效时旧 generation 必须已失效。
3. UdpTableServerSmokeTest.testFullLoopbackLifecycle:210 START_MATCH 失败。原始客户端未做 SET_READY，迁移 host/guest 真实准备前提及唯一 requestId/递增 seq，保留全部生命周期/隐私/恢复断言。
4. 静态发现 sendRoomUpdate 的 watermark CAS 与 inFlightRoomUpdates.put 分离：A(v2) CAS 后暂停，B(v3) CAS/put，A 随后 put(v2) 覆盖 v3 并取消其重试。若 v3 首包丢失，最后更新无法恢复。请原子化每连接版本比较和待发替换，外部发送/回调不在状态锁内；ACK/清理不能误删新条目。补真正并发交错回归，不能只测顺序 v3 再 v2。
5. SharedRoom.ResumeSessionResult 的 first/second/third 兼容访问器违反要求。保留 seat/snapshot/generation，迁移实际消费者并删除不必要别名。

交付前重读本反馈；Codex 统一测试。
