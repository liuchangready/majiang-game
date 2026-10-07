# 收尾审查证据与必须完成项

前批模块95 server/101 client、独立112通过，完整 test/桌面编译/APK也成功。以下为审查发现的未覆盖边界，不是已跑失败的旧用例；补确定性模块回归并修复，保留原断言。

1. UdpTableServer 退出/KICK/timeout路径先 inFlightRoomUpdates.remove(id)，再 purgeRoomUpdate(entry)；purge 又要求 remove(id,entry) 成功才取消 retry/完成 future。条目已移除，取消与完成都不会发生。统一清理所有权，一次移除后仍终结对应旧条目；旧 ACK 不能清新条目。用假 scheduler 检查 cancellation、future 完成及无孤儿重试，覆盖 leave/kick/timeout/close。
2. sendRoomUpdate 仅入口检查 session.isClosed/isDisplaced。并发关闭/清理后，已经进入但尚未拿到 roomUpdateLocks 的回调可重新创建 watermark/entry/定时器。锁对象从 map 删除也可能让并发旧回调拿不同锁。把连接生命周期和待发替换联动，关闭后不再注册条目；不引入无界 tombstone。用屏障构造发送与清理交错。保持 socket发送、取消及外部完成回调在状态锁外，稳定seq/重复ACK/最新完整视图不变。
3. LEAVE_ROOM回调在 receipt.status=REJECTED 时仍移除session并记录terminated，err分支也无条件session.close()。仅权威APPLIED撤销绑定并关闭对应服务端成员session；REJECTED或失败不冒充成功离房，保持原有效绑定，真正房间已关闭/旧generation则依实际状态处理。补模块证据，不靠吞异常使回执成功。

若某项判断有误，提供实际路径与可执行反例，不能只按反馈机械改代码。
