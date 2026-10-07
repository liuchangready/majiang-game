# R4B2CLIENTRESUME 交付报告（Antigravity）

- 开发者：Antigravity (Anti)
- 任务：R4B2CLIENTRESUME（接续客户端观战流同步、恢复标志归属、全量去重与生命周期隔离）
- 日期：2026-10-07

---

## 1. 修改文件清单

1. `client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt`
   - 修复测试编译错误：正确导入 `com.majiang.server.api.PublicCheckpoint as ProtocolPublicCheckpoint`；
   - 修复 `TileView` 构造参数：使用 3 参数签名 `TileView(tileId, suit, rank)`（`isRed` 属性由 `tileId` 内部派生），不污染生产代码。

2. `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`
   - **全量基线去重与 ACK 保持**：`handleInboundPush(PushType.PUBLIC)` 在单一 `stateLock` 下进行游标判定，`if (snap.cursor > curCursor)` 推进游标并派发更新；重复或落后的 full 消息（`snap.cursor <= curCursor`）正常回复 ACK，但不重复派发至监听器，解决 `repeatedFullBaselineNotifiesOnlyOnce`。
   - **自愈恢复代次隔离**：新增 `PublicHealToken(epoch, id)` 及 `activePublicHealToken: AtomicReference<PublicHealToken?>`；请求捕获 `epochAtRequest`，响应返回时仅在代次匹配且有效时采纳；`finally` 块通过 `activePublicHealToken.compareAndSet(healToken, null)` 释放标志，杜绝旧代次或失效请求清除新代次恢复标记。
   - **自愈响应采纳与单次归属（响应 Codex 10:45 & 10:56 反馈）**：
     - `adoptAuthoritativePublicSnapshot` 调整为支持同流有效基线采纳（仅在 `!isNewStream && snap.cursor < lastPublicCursor` 时拒绝回滚，`snap.cursor >= lastPublicCursor` 均合法采纳），允许 early buffer 溢出后在相同游标下建立基线；
     - `triggerPublicSnapshotSelfHeal` 的 `whenComplete` 显式判定响应有效性：检查 `isStale = !isNewStream && snap.cursor < lastPublicCursor`，过期旧响应不派发，避免在有更新 full 到达后被旧自愈响应回滚；非 stale 的合法全量基线在锁内构建 `ProtocolPublicUpdate(isFullSnapshot = true)` 并排入待派发列表，同时排水缓冲；
   - **监听器回调前逐次校验有效性（响应 Codex 10:54 反馈）**：
     - `dispatchPublicUpdate` 在每次回调用户监听器前重新校验 `!closed && bindingEpoch == item.epoch && boundRoomId == item.roomId && boundSpectatorId != null`，若首个监听器同步执行 `close()` 或 `leaveRoom()`，立即中止派发，后续监听器不再收到旧数据；
     - `onPublicUpdate` 在早期缓冲回放循环中，每次回调用户代码前同样重新校验当前绑定代次与未关闭状态，避免首个回放同步关闭后后续回放继续交付；
   - **生命周期彻底清理**：在 `leaveRoom()`、`joinSpectator()`、`resumeSpectator()` 及 `close()` 中重置 `activePublicHealToken.set(null)` 并清空各队列与重排缓冲。

3. `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`
   - **全量基线去重**：`handleInboundPublicUpdate` 对 full 消息采用 `if (updateSnap.cursor > current.cursor)` 严格递增才派发，同游标重复仅清理重排缓冲不派发；
   - **恢复标志代次归属**：新增 `PublicRecoveryToken(connectionEpoch, roomEpoch, id)` 及 `activePublicRecoveryToken: AtomicReference<PublicRecoveryToken?>`，彻底替换原 `isPublicResuming: AtomicBoolean`；`triggerPublicGapRecovery` 与各生命周期操作均通过代次令牌隔离，旧代次响应无法清除新代次标记或阻塞新恢复；
   - **派发来源身份全链路贯穿与逐次校验（响应 Codex 10:54 反馈）**：
     - `notifyPublicUpdateListeners` 接收 `(update, epoch, roomEpoch, roomId)`；
     - 在 `dispatch` 派发循环中，针对每个监听器均重新校验 `!closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get() && currentSpectatorBinding?.roomId == roomId && cachedPublicSnapshot != null`；若首个监听器执行 `leaveRoom().await()`，房间代次递增且绑定置空，后续监听器立即中止，彻底解决 `queuedPublicCallbackCannotRunAfterSuccessfulLeave` 与 `successfulLeaveInsideFirstListenerInvalidatesTheRemainingCallbacks`；
   - **生命周期与重连清理**：在 `onBindingEstablished`、`onSpectatorBindingEstablished`、`joinSpectator`、`resumeSpectator`、`leaveRoom`、`terminateSession` 中全量重置 `activePublicRecoveryToken.set(null)`。

4. `client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`
   - 新增 `testSpectatorJoinAndRepeatedFullBaselineNotifiesOnlyOnce`：验证观战加入、连接 ID 采纳、全量基线派发，以及同游标重复全量消息仅 ACK 不重复通知监听器；
   - 新增 `testSpectatorGapTriggersResendCheckpoint`：验证观战流缺口正确触发 `RESEND` 报文，且携带与当前基线严格一致的 `PublicCheckpoint`；
   - 新增 `testSpectatorLeaveRoomCleansBindingsAndResetsTokens`：验证显式离房正确清理席位与观战绑定，重置连接 ID 为 0 并释放恢复标志。

---

## 2. 未完成项 (Unresolved Items)

- 无未完成项。所有 R4B2CLIENTRESUME 契约要求、Codex 反馈要点（10:45、10:54、10:56）均已完成并落盘。

---

## 3. 测试运行声明

- 按照项目工作流协议及任务明确约束：**本交付未运行 Gradle 构建、未运行单元测试、未启动 Shell 终端或后台命令**。
- 代码完全遵循契约与评审测试断言设计，请 Codex 按照串行验收流程执行独立构建与全量测试验证。
