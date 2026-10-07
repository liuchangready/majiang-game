# 同连接重叠离房基线

2026-10-06 00:47，Codex新增 `V4LobbyAcceptanceTest.overlappingLeaveRequestsCannotInvalidateTheNextRoomBinding` 并实际运行，1项失败。

该用例用宿主队列屏障同时挂起两个 leave 请求，释放后第一条回执 APPLIED，但连接 closed=true（第71行），未能创建下一个房间。日志 `build/v4-migration/GLM-ROOM-overlap-baseline.log`。这确认了当前返修要求中“多个 leave 共用布尔标志/旧收尾污染当前操作”具有实际可复现失败，不只是静态风险。

允许同一绑定重复 leave 共用在途 Future 或明确 REJECTED；第一个成功离房后连接必须保持存活，随后新房不被旧回执/回调解绑。本用例只固定这些行为，不限定内部实现。独立完整目标现在111项；ROOM专项23项。不要编辑 reviewer 文件。
