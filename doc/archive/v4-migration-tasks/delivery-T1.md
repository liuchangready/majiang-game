# T1 交付记录：server 契约层

日期：2026-10-05。开发：Anti。检视/测试：主会话（Claude Code GLM）。

## 交付物

13 个新文件，全部位于独占路径 `server/src/**/com/majiang/server/api/`：
- 接口：`LobbyApi`、`SessionApi`、`PushChannel`、`ServerConnection`
- DTO：`CreateRoomRequest`、`JoinRoomRequest`、`ResumeRequest`、`RoomBinding`、`RoomSummary`
- 常量：`ApiErrors`（14 错误码）、`ApiProtocol`（版本 4 + KICK 原因码）
- 测试：`ApiDtoValidationTest`（21 项）、`FakeServerConnectionSmokeTest`（6 项）、`FakeServerConnection` 假实现

## 验收结论：通过

- 隔离检查：时间戳比对确认仅 13 个独占路径文件被创建/修改，无越界改动。
- 检视结论：接口签名与 SERVER_CLIENT_API.md §2/§3 完全一致；KDoc 写明幂等契约 `(seat, requestId)` 去重、REJECTED 合法回执模型、脱敏投影；DTO 校验与 engine 风格对齐；token 脱敏到位；零重复定义（复用 engine 载荷）。
- 测试：`:server:test` 31 项全过（27 新增 + 4 既有）；全量回归 engine 340 / client 34 / presentation 183 / server 31 = **588 项，0 失败**（基线 561 + 27 新增）。

## 主会话修正（1 处）

- `FakeServerConnection.kt`：缺 `import com.majiang.application.deepCopy`（`PlayerDecision.deepCopy()` 是该包扩展函数），补一行 import 后编译通过。

## 遗留观察（不阻塞，转后续任务）

1. `ApiProtocol` 同时定义 `DISPLACED_BY_NEW_CONNECTION` 与 `DISCONNECTED_BY_NEW_CONNECTION` 同义常量——T4 接入时统一使用 `DISPLACED_BY_NEW_CONNECTION`（与现网 TCP 栈及设计文档 §7.7 一致），多余常量 T7 清理。
2. `FakeServerConnection` 可在 T2/T5 测试复用，符合简报预期。
