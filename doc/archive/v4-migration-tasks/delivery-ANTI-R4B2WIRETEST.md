# R4B2 WIRETEST 交付报告

## 1. 任务概述与分工边界

本任务独占范围为 `protocol/src/test`。开发人员 Anti 仅使用内置文件读写工具完成测试代码与测试假实现开发，未执行任何 shell、终端、构建或测试命令，未触碰 `protocol/src/main`、`engine`、`server`、`client`、`presentation`、`tools/architecture/v4-review` 或 `build` 目录。
根据分工约定，构建、测试与验收由 Codex 独立执行。

## 2. 修改与新增文件列表

| 文件路径 | 操作 | 内容与目的 |
|---|---|---|
| `protocol/src/test/kotlin/com/majiang/server/api/FakeServerConnection.kt` | 修改 | 补齐 `SpectatorApi` 全部 6 个抽象方法、请求记录器、可配置结果、PUBLIC 订阅及移除、`emitPublicUpdate`、`close` 清理及公开快照构造 helper |
| `protocol/src/test/kotlin/com/majiang/server/api/FakeServerConnectionSmokeTest.kt` | 修改 | 新增对 `FakeServerConnection` 观战期 6 个方法、请求记录与 PUBLIC 推送注销的烟测覆盖 |
| `protocol/src/test/kotlin/com/majiang/server/api/ApiDtoValidationTest.kt` | 修改 | 新增 `JoinSpectatorRequest`、`ResumeSpectatorRequest`、`PublicCheckpoint`、`SpectatorBinding` 结构验证、非法边界拒绝及 `toString` 令牌脱敏测试 |
| `protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPublicModelsCodecTest.kt` | 新增 | 全面覆盖独立公开快照、结算历史六种 outcome、完整计分、合法负分、物理红牌、嵌套源与 getter 不变性、5 个观战请求、观战响应/token脱敏、私密事件白名单防御、strict `PUBLIC_UPDATE` 连续与 full 标记、`PUBLIC_RESEND` 完整检查点边界及 `PushType` 分类器 |
| `protocol/src/test/kotlin/com/majiang/transport/api/ApiMessageCodecTest.kt` | 修改 | 补充 5 个观战 `ApiRequest` 标签的往返编解码测试 |

## 3. 具体实现与行为验证点

1. **`FakeServerConnection` 契约完整性**：
   - 实现了 `joinSpectator`、`resumeSpectator`、`publicSnapshot`、`resumePublic`、`queryPublicRoundResult`、`onPublicUpdate`。
   - 记录请求：`submittedJoinSpectatorRequests`、`submittedResumeSpectatorRequests`、`submittedPublicCheckpoints`、`queriedPublicRoundIds`。
   - 结果可配置：`joinSpectatorResult`、`resumeSpectatorResult`、`publicSnapshotSupplier`、`publicRoundResultSupplier`。
   - 订阅与推送：`onPublicUpdate` 返回有效 `AutoCloseable`，句柄关闭后不再接收更新；`emitPublicUpdate` 向所有活跃监听器分发；`close()` 时清理全部 `publicUpdateListeners`。
   - Companion 包含合法的 `createDefaultPublicBoardView`、`createDefaultPublicSessionSummary` 与 `createDefaultPublicSnapshot`。

2. **公开模型与线协议测试 (`ProtocolPublicModelsCodecTest.kt`)**：
   - **快照与结算保真**：覆盖 `DealerWin`、`NonDealerWin`、`Ron`、`Tsumo`、`ExhaustiveDraw`、`AbortiveDraw` 6 类 outcome；包含完整的 `ProtocolScoreBreakdown`（役种、符分解、宝牌命中、`rawBasePointsExact`）；保留合法负分（如 `-500`）；保留物理赤五（`tileId = 19`）。
   - **深层源与 Getter 不变性**：构造前篡改外部入参集合，构造后不改变快照数据；对 getter 返回的集合（如 `board.scores`、`board.discards`、`board.melds`、`meld.tiles`、`roundRecords`、`tenpaiSeats`、`settlementEvents` 等）进行可变转换并修改均抛出 `UnsupportedOperationException` 或 `ClassCastException`。
   - **观战请求与响应脱敏**：覆盖 `JOIN_SPECTATOR`、`RESUME_SPECTATOR`、`PUBLIC_SNAPSHOT`、`RESUME_PUBLIC`、`QUERY_PUBLIC_ROUND_RESULT`；`ResumeSpectatorRequest` 与 `SpectatorBinding` 在 `toString()` 中将令牌脱敏为 `***`，在线路 JSON 中完整保留；拒绝客户端自选 `role: SPECTATOR` 或混入 `preferSeat` / `choice` 等玩家控制字段。
   - **混合负载与事件白名单防御**：`ApiResponse` 严格拒绝同时携带 PUBLIC 字段与 PLAYER 专属字段；白名单放行公开事件，严格拒绝 `TileDealt`、`TileDrawn`、`RinshanDraw`、`CheatAttempted`、`SuspicionChanged`、`GazeShifted`、`CheatPointsChanged` 等私密/千术事件。
   - **strict `PUBLIC_UPDATE`**：验证连续普通更新（`snapshot.cursor == fromCursor + 1` 且 `isFullSnapshot == false`）与全量基线（`fromCursor == snapshot.cursor` 且 `isFullSnapshot == true`）；线路必须为严格布尔值（拒绝缺失、字符串 `"false"`、数字 `0` 等）。
   - **strict `PUBLIC_RESEND`**：验证携带完整 `PublicCheckpoint(sessionId, syncEpoch, cursor)`，支持 `Long.MAX_VALUE`；拒绝裸游标、缺失 checkpoint、未知字段、负游标、小数游标或额外席位字段。
   - **`PushType` 分类器**：验证旧玩家推送分类为 `PushType.PLAYER`、房间推送为 `PushType.ROOM`、公开推送为 `PushType.PUBLIC`；拒绝非字符串、未知类型及非合法 JSON 对象。

## 4. 未运行测试明确声明

**按照任务要求，Anti 仅使用文件工具落盘代码，未执行 Gradle 构建，未运行单元测试或集成测试。** 所有测试与构建均待 Codex 独立验收运行。

## 5. 未完成项与下游依赖说明

- `protocol/src/main` 的 codec 生产实现由并行任务 `R4B2WIREFIX` 完成。
- 真实的 Server 端和 Client 端观战接入（`ServerConnection` 具体绑定、独立公开流调度、UDP 数据报路由等）属于后续阶段任务（`R4B2SERVER` 与 `R4B2CLIENT`）。
