# UDP 客户端锁外通知还需保序

前批101 client测试已通过；新增回归必须验证真正线程交错，不是同线程可重入 monitor（Java monitor本来就可重入）。

1. handleInboundPush 把 v2 提交缓存后移到锁外 dispatchRoomUpdate；另一线程 query/v3 在其前提交并通知，旧线程随后通知v2，listener仍会看到[3,2]。dispatchRoomUpdate未检查该通知是否已被更新版本替代，多个listener/早期buffer也可能乱序。允许完整视图合并，但每个有效观察者版本单调、最后视图可达；实现有界的串行排水或等效方案，外部回调不在状态锁内，不让慢listener阻塞leave/close。不应每条通知创建线程。
2. dispatchRoomUpdate 到投递时才读取 bindingEpoch，而不是接受更新时捕获。handleInboundPush 在锁外校验 roomId，在锁内只比较version；leave/new bind恰好插入可让旧包写入新缓存。create/join/resume/leave 的身份修改与缓存reset目前也不在同一状态临界区。原子校验身份代次与缓存提交，通知携带接受时的绑定标识，不能在投递时收养新epoch。GET_ROOM_INFO锁外投递及earlybuffer按同一契约处理。
3. 有意义模块回归：v2回调第一listener阻塞、并发v3提交，第二listener不得收到[3,2]；正在提交的旧房间更新跨leave/new bind不得污染缓存；慢listener时另一线程close/leave须及时完成。所有等待有上限。保留重复ACK、丢最后更新、查询不回退、低版本新房、身份拒绝断言。

若现有接收线程使某种交错不可能，请同时检查公开getRoomInfo完成和onRoomUpdate注册的并发路径，提供实际证明，不要只假设单线程。
