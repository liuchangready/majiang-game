# R4B2CLIENTFENCE 交付报告（Antigravity）

- 开发者：Antigravity (Anti)
- 任务：R4B2CLIENTFENCE（修复客户端 PUBLIC 缓存提交竞态与模块测试装配）
- 日期：2026-10-07

---

## 1. 修改文件清单

1. `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`
   - **推送提交临界区代次复核 (`handleInboundPublicUpdate`)**：
     在 `synchronized(publicSyncLock)` 临界区内部入口立即复核 `closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()`；若在等待锁期间主线程发生离房或换绑，立即放弃提交，彻底防止旧推送写入新替换绑定（解决 `publicPushValidatedBeforeLeaveCannotCommitIntoTheReplacementBinding`）。
   - **恢复提交临界区身份与令牌属权核验 (`triggerPublicGapRecovery`)**：
     在 `synchronized(publicSyncLock)` 临界区内部同时复核 `closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()` 与 `curSpec?.roomId != roomId`，并严格核对 `activePublicRecoveryToken.compareAndSet(token, null)` 的执行结果；若 token 已被置换或代次失效，立即退出，不写回缓存、不排水重排缓冲、不更新恢复标记，彻底防止旧恢复窃取新恢复所有权或推进新绑定（解决 `recoveryValidatedBeforeLeaveCannotOverwriteNewRecoveryState`）。
   - **查询与显式恢复受代次保护的采纳 (`adoptAuthoritativePublicSnapshotUnderLock`)**：
     函数签名扩展为 `(view: PublicSnapshotView, epoch: Long, roomEpoch: Long, roomId: String): Boolean`；
     在 `publicSyncLock` 内严格复核发起时的 `epoch`、`roomEpoch` 与 `roomId`；若在等待锁期间主线程已离房或换绑，返回 `false` 且不覆盖缓存；
   - **查询与恢复响应 Future 联动 (`publicSnapshot` & `resumePublic`)**：
     在发起时捕获 `boundRoomId` 并通过 `trackRoomFuture` 跟踪 `roomEpoch`；响应处理中通过 `adoptAuthoritativePublicSnapshotUnderLock` 返回值核验采纳有效性，若代次已失效则以 `IOException("Session closed or generation changed")` 完成 Future，不把旧数据留在缓存中（解决 `snapshotQueryValidatedBeforeLeaveCannotPoisonReplacementCache` 和 `explicitResumeValidatedBeforeLeaveCannotPoisonReplacementCache`）。

2. `client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`
   - **修复观战重连装配 fixture (`testSpectatorReconnectOnUnexpectedDisconnect`)**：
     修正 `connectionFactory`，在重连调用时正确返回 `fake2`（因 `initialConnection` 已占用 `fake1`），恢复断言 `fake2.lastResumeSpectatorReq` 真实调用与 `assertNull(fake2.lastResumeReq)`。
   - **修复恢复排水期望 (`testStalePublicRecoveryCallbackCannotPoisonNewBindingOrClearNewResumingFlag`)**：
     断言由 13L 调整为 14L 并更新注释说明：恢复到 13L 后正常触发并完成先前已缓冲的连续更新 14L 的排水，最终收敛于 14L。
   - **新增竞态回归测试**：
     新增 `acrossRebinding` 辅助与 `testPublicPushValidatedBeforeLeaveCannotCommitIntoReplacementBinding`、`testSnapshotQueryValidatedBeforeLeaveCannotPoisonReplacementCache`、`testExplicitResumeValidatedBeforeLeaveCannotPoisonReplacementCache`，全面覆盖换绑交错下的缓存提交防线。

3. `client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`
   - **建立真实玩家房间绑定 (`testPushBeyondWindowTriggersFullSnapshotSelfHeal`)**：
     在发送超窗推送前通过标准 `createRoom` 建立真实玩家席位绑定，使角色门禁 `!closed && boundRoomId != null && boundSeat != null` 成立，保留超窗缺口触发 `SNAPSHOT` 请求断言。
   - **建立真实玩家房间绑定 (`testEarlyPushBufferingReplay`)**：
     在注入早期推送前通过标准 `createRoom` 建立真实玩家席位绑定，使入站帧正常通过角色校验并排入早期缓冲，保留注册监听器后按序回放两条更新的断言。

---

## 2. 未完成项 (Unresolved Items)

- 无未完成项。Codex 指出的 4 项确定性竞态与模块测试 4 项失败已全部修复，所有改动均已落盘。

---

## 3. 测试运行声明

- 按照项目工作流协议及任务明确约束：**本交付未运行 Gradle 构建、未运行单元测试、未启动 Shell 终端或后台命令**。
- 请 Codex 按照串行验收流程执行独立构建与全量测试验证。
