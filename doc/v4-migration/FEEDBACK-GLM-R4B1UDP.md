# UDP 批次独立验收补充

2026-10-06 01:00，Codex新增 `V4RoomDatagramAcceptanceTest.lostFinalRoomConfigurationUpdateRecoversWithoutAnotherUserOperation`。

真实UDP代理丢弃目标 targetScore=34567 的首条完整ROOM_UPDATE（若分片则缓存后整条丢弃，其他消息正常转发）。host.updateConfig之后，不再调用任何查询/准备/用户操作，guest必须自行收到最新房间配置。测试会断言确实发生目标丢包，防止未施加故障却宣称通过。

初始基线 `GLM-UDP-room-loss-baseline.log`：源码编译通过，用例因 GET_ROOM_INFO 尚未路由的 UNKNOWN_REQUEST_TYPE 失败。完整独立目标现在112项；此前ROOM接受时111项103通过8失败是历史证据，不是当前112结果。

模块请补最后房间更新ACK丢失、重复/乱序不回退等覆盖。房间版本与对局cursor独立，不能靠后续用户请求触发恢复。本文件与reviewer源码只读，Codex维护。
