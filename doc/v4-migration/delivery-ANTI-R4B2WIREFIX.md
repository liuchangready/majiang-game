# R4B2 WIREFIX 交付报告（Anti）

- **交付日期**：2026-10-06
- **责任主体**：Anti（生产代码实现）
- **测试状态声明**：**根据用户规则与任务要求，Anti 本人未执行任何构建（./gradlew）或测试命令；全部测试与验收由 Codex 独立执行。**

---

## 1. 任务背景与修复要点

在 Codex 第一轮独立验收探针（`tools/architecture/v4-review/V4PublicWireAcceptanceTest.kt`）中暴露的缺口已全面完成修复，所有冻结入口均在 `protocol/src/main` 落地：

1. **补齐 `ApiMessageCodec` 独立公开编解码入口**：
   - `encodePublicSnapshot(ProtocolPublicSnapshot): String` / `decodePublicSnapshot(String): ProtocolPublicSnapshot`
   - `encodePublicUpdatePush(ProtocolPublicUpdate): String` / `decodePublicUpdatePush(String): ProtocolPublicUpdate`
   - `encodePublicResendRequest(PublicCheckpoint): String` / `decodePublicResendRequest(String): PublicCheckpoint`
   - 严格异常控制：统一抛出 `JsonParseException`，复用 registered TypeAdapter。

2. **冻结 PUSH 分类器与修复信封路由**：
   - 新增 `ApiMessageCodec.classifyPush(json: String): PushType`，将推送信封精准定型为 `PLAYER`、`ROOM`、`PUBLIC`。
   - 修正 `isRoomUpdateEnvelope(json: String)` 与 `isRoomUpdatePush(json: String)`：仅当 `type == "ROOM_UPDATE"` 时返回 `true`，杜绝 `PUBLIC_UPDATE` 被误判为房间信封。
   - 保留旧无 `type` 玩家推送信封默认归类为 `PushType.PLAYER`。

3. **收紧观战 `ApiRequest` 校验与隔离玩家授权载荷**：
   - 在 `ApiMessageCodec.decodeRequest` 与 `ApiRequestAdapter` 两处对 5 类观战接口（`JOIN_SPECTATOR`, `RESUME_SPECTATOR`, `PUBLIC_SNAPSHOT`, `RESUME_PUBLIC`, `QUERY_PUBLIC_ROUND_RESULT`）实施格式隔离：
     - 严禁携带玩家授权载荷：出现 `preferSeat`、`choice` 或 `shufflePlan` 立即拒绝。
     - `JOIN_SPECTATOR`：必填非空 `roomId`，`spectatorName` 若存在必须为非空白字符串。
     - `RESUME_SPECTATOR`：必填非空 `roomId`、`spectatorId`、`token`，严禁裸 `cursor`。
     - `RESUME_PUBLIC`：必填 `publicCheckpoint` 结构体，严禁裸 `cursor`。
     - `QUERY_PUBLIC_ROUND_RESULT`：必填非空 `roundId`。
     - 拒绝客户端自断角色字段（`"role"`）。

4. **补全 `ApiResponse` 混合负载拒绝边界**：
   - 在 `ApiResponse.init` 与 `ApiResponseAdapter` 中将顶层 `resumeToken != null` 纳入 `hasPlayer` 判定。
   - 观战仅合规在 `SpectatorBinding.resumeToken` 内承载恢复令牌；公共响应若混入顶层旧玩家 `resumeToken` 则直接拒绝。
   - 保持单独公共响应（`roomInfo`, `receipt`）与合法旧玩家响应不受干扰。

5. **强化集合防御与不变性**：
   - `ProtocolPublicModels.kt` 中 `KNOWN_PUBLIC_LIFECYCLES` 与 `KNOWN_PUBLIC_FINISH_REASONS` 均经 `Collections.unmodifiableSet` 包装，防止外部修改。

---

## 2. 变更文件清单

| 文件路径 | 变更说明 |
|---|---|
| `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt` | 落地 `classifyPush`、公开 Snapshot/Update/Resend 编解码函数；修复 `isRoomUpdateEnvelope`；收紧 `ApiResponse.hasPlayer`；强化观战 `ApiRequest` 格式校验。 |
| `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolPublicModels.kt` | `KNOWN_PUBLIC_LIFECYCLES` 与 `KNOWN_PUBLIC_FINISH_REASONS` 使用不可变集合包装。 |

*注：未修改任何 `protocol/src/test`、`tools/architecture/v4-review` 或其他模块源码。*

---

## 3. 后续批次（SERVER / CLIENT）接线位置指引

1. **CLIENT 路由与观战接入（下一批次待办）**：
   - 在客户端 UDP 接收处使用 `ApiMessageCodec.classifyPush(payload)` 替代原有基于 `has("type")` 的旧逻辑。
   - `PushType.PUBLIC` 投递给 `SpectatorApi.onPublicUpdate`。
   - 观战恢复与丢包处理必须遵循连续性约定：普通更新要求 `update.fromCursor == currentCursor`；遇空洞发送 `encodePublicResendRequest(PublicCheckpoint)`。

2. **SERVER 观战端点实现（下一批次待办）**：
   - 派发并实现 `SpectatorApi` 的 6 个抽象方法。
   - 针对 `PUBLIC_RESEND` 载荷反序列化 `PublicCheckpoint` 并实施代次与游标补发。
   - 保持公共流事件发布与各玩家独立流相互隔离。
