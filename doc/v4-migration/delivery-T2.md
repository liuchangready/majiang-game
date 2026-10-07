# T2 交付记录：进程内绑定

日期：2026-10-05。开发：Anti。检视/测试：主会话。

## 交付物（3 个新文件，均在独占路径 `server/**/inprocess/`）

- `InProcessServer`：`createLocalTable(config, humanSeat)` 创建 TableHost（LocalHuman + 3×Computer 控制者、adminSeat=humanSeat）、`connect(humanSeat)` 取 SessionAccess；`listRooms()` 单机语义（单桌摘要/空列表）；外置 `tick()` 驱动代理，无私起线程。
- `InProcessBinding`：实现 `ServerConnection`；SessionApi 直通 `SessionAccess`（返回前一律 `deepCopy()`），`queryRoundResult` 经 `host.queryRoundResultEvents(seat, roundId)` 脱敏；大厅单机语义（`leaveRoom` → REJECTED `LOCAL_TABLE_UNSUPPORTED`，token 占位 `"LOCAL"`）；`onUpdate` 直通订阅、`onDisconnect` 仅 close 触发一次、幂等关闭。
- `InProcessBindingTest`（5 项）：端到端（建桌→快照→开赛→决策→弃牌→游标推进）、requestId 幂等重放、关闭后拒绝/异常语义、大厅语义、洗牌千术提交。

## 验收结论：通过

- 隔离检查：本轮时间戳比对无越界改动。
- **engine API 逐一核实**（Anti 幻觉高危点）：`TableHost(config/controllers/adminSeat)` 构造、`queryRoundResultEvents(seat, roundId)`、`currentRoundId`、`tick(presentationBusy): Boolean`、`connect(seat)`、`SeatController.LocalHuman()/Computer()` 全部与 engine 源码一致。
- 测试：server 36 项全过；全量回归 **632 项 0 失败**。

## 检视备注（不阻塞）

1. `InProcessBinding.close()` 连带关闭 host——单机 1:1 桌场景合理（KDoc 已写明），多桌复用时需上提宿主所有权到 server。
2. `capabilities()` 覆写 `protocolVersion = 4` 与 V4 基线对齐，正确。
