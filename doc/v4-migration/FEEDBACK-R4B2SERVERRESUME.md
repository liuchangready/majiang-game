# R4B2 SERVER RESUME 开发中检视（Codex）

本批仍在开发，未运行新构建；SERVERCONT 全部真实失败反馈继续有效。10:52 对当前落盘 UDP 路径追加两项竞态，请交付前处理并保留回归。

1. 观战绑定注册窗口会漏掉最后一条 PUBLIC 更新。UdpRoom.joinSpectator/resumeSpectator 取快照 N 后才装 spectatorSessions，UdpTableServer 完成回调再注册 sessionsByConnectionId。若 N+1 在这两个窗口发布，会因尚无 spectator 或 sendPublicUpdate 的未注册检查被丢弃。目前 sendSpectatorBaseline 只补 ROOM_UPDATE；如果此后无动作，就没有 N+1 的可靠待发记录，客户端一直停留 N。请在全局注册完成后取得并可靠发送最新 PUBLIC 全量基线，同时协调并发更新的顺序，不能让旧基线回退流或覆盖较新 pending。建议用可控 completion 阻塞 join/resume，期间推进最后动作，释放后不再动作，验证客户端最终收到最新状态。

2. UDP 观战迟到完成可在房间终结后复活。installSpectatorBinding 和 resumeSpectator 的 spectatorLock 块只检查 installed generation，不检查 closed 和 SharedRoom 真实成员/当前代次；closeTransport 清空会话后，先前权威请求仍可能迟到装回。UdpTableServer.whenComplete 又无房间/服务端终结及当前代次复核，直接注册并返回成功。这条连接错过 ROOM_CLOSED/KICK。请在安装及全局注册处完成有效性复核并协调关闭窗口，失败清理对应新会话/成员（不能误撤销较新恢复）。建议构造 join/resume 权威成功但 completion 暂停，房主关闭房间后释放，断言不返回可用成功会话且无残留。

以上是根与只读 reviewer 对当前源码的检查，不是已经运行通过的测试。不要修改 Codex reviewer，不要自行启动构建或其它 writer。

独立 reviewer 已新增 V4SpectatorUdpBindingRaceTest.kt 两项（未运行），用 SharedRoom.onSpectatorDisplaced 的真实锁外同步通知门闩卡住 resume 成功至安装之前的窗口，分别发布最后一次 pause 和关闭房间，再释放完成；验证真实 UDP 无额外查询恢复最新状态，以及关房后收到失败响应而不是超时/复活。不反射改内部字段，测试已被现有 reviewer 目录接线包含。
