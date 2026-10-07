# V4 迁移交付报告：任务 R1FIX1 (R1 缺陷全面修复与契约收敛)

**交付人**：Antigravity (Anti)  
**架构与验收负责人**：Codex  
**交付日期**：2026-10-05  
**测试执行声明**：**本交付物中的所有生产代码与测试用例修改均未由 Anti 运行、编译或执行构建**（严格遵守任务约束：Anti 仅使用内置文件读写工具，不执行任何命令行、Gradle 构建、独立测试或进程，所有编译构建、独立测试与 18 项架构审查测试均由 Codex 负责执行验收）。

---

## 一、修改与新增文件清单

### 1. 协议层 (protocol)
- [`protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt)
  - 新增 `const val CONFLICT = "CONFLICT"` 并纳入 `ApiErrors.ALL`。
- [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
  - `encodePush` 线上显式编码全量标记：`"full": true` 与 `"isFullSnapshot": true`。
  - `decodePushEnvelope` 严格依据线上显式标记提取 `isFullSnapshot`（游标形态本身不构成全量替换授权）。
  - `decodeRequest` 保留语法合法的非空未知 API 标签，交由服务端分发器返回 `UNKNOWN_REQUEST_TYPE`；对嵌套参数（`config`、`choice`、`shufflePlan`、`preferSeat`、`cursor`）在协议入口执行强制边界校验（防御 Gson 绕过构造器）。
- [`protocol/src/main/kotlin/com/majiang/transport/udp/PushSequencer.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/udp/PushSequencer.kt)
  - `adoptFullSnapshot` 保持在单条推送流内严格单调递增，禁止游标向后回滚。
  - `offer` 在判定 `isFullSnapshot` 前先执行陈旧性检查（`cursor <= currentCursor`），陈旧全量快照直接返回 `Stale`，绝不触发 `Applied`。
  - 新增 `reset(newCursor = 0L)`，仅在明确的新房间/新会话代次绑定时允许重置基线。
- [`protocol/src/main/kotlin/com/majiang/transport/udp/ReplayGuard.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/udp/ReplayGuard.kt)
  - 由原先单一最高序（导致乱序有效请求被永久丢弃）升级为 256 位环形滑动窗口：严格阻断重复重放包与落后窗口（`seq <= maxSeen - windowSize`）的老包，同时安全接纳在窗口内乱序到达的合法数据帧（如 seq 20 后到达 seq 19）。
- [`protocol/src/main/kotlin/com/majiang/transport/udp/ReliableRequestor.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/udp/ReliableRequestor.kt)
  - 维护已完成请求与 RSP 序号的有界缓存（256 条）；当网络中出现重复交付的相同 RSP 帧时，自动补发 ACK 确认帧，避免对端或测试重试卡死。
- [`protocol/src/test/kotlin/com/majiang/transport/udp/PushSequencerTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/udp/PushSequencerTest.kt)
  - 补充陈旧全量快照安全丢弃、不倒退游标以及 `reset` 建立新基线的回归测试。
- [`protocol/src/test/kotlin/com/majiang/transport/udp/ReplayGuardTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/udp/ReplayGuardTest.kt)
  - 补充乱序到达数据包（如 seq 20 后到达 seq 19）正常接收、重复 seq 严格丢弃及滑动窗口边界淘汰的回归测试。
- [`protocol/src/test/kotlin/com/majiang/transport/api/ApiMessageCodecTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ApiMessageCodecTest.kt)
  - 补充保留未知 API 供分发器处理、嵌套载荷边界校验、以及线上全量快照标记编解码的单元测试。

### 2. 服务端 (server)
- [`server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt)
  - 修复 `putInFlight` 签名与类型契约：原子 `putIfAbsent` 并返回先前的 `CompletableFuture<ApiResponse>?`（解决编译错误）。
  - `CachedSessionEntry` 与 `InFlightSessionEntry` 全面记录原始 `rawPayload`、`api` 与 `request`。
  - 内存严格有界：在途请求上限 256 条（超时清理），幂等缓存上限 1024 条。
  - `handleResend` 产生的全量快照推送明确设置 `isFullSnapshot = true`。
- [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
  - `bindHost` 与 `join` 中初次下发全量快照推送时，明确设置 `isFullSnapshot = true`。
- [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
  - 将原始 JSON 载荷 `jsonString` 传递至 `handleLobbyReq` 与 `handleSessionReq`。
  - `handleLobbyReq`：检查已完成与在途条目的 `api` 和 `rawPayload`，相同 `requestId` 附带不同载荷或 API 时立即返回 `ApiErrors.CONFLICT`；正确记录 `CompletedLobbyEntry` 的元数据。
  - `handleSessionReq`：**代次与 Token 校验前置在缓存重放与在途去重之前**，彻底阻止未授权或被顶替连接窃取既有响应；对命中缓存或在途的会话请求同样进行 `CONFLICT` 冲突检测；通过 `inboundReplayGuard`（滑动窗口）接纳乱序不同请求。
  - `sendKick`：实现有界重传机制（至多 3 次，指数退避），将挂起的 KICK 登记在 `inFlightKicks` 中；收到对应 ACK 时取消重试定时器并完成 Future。
  - `close()`：彻底移除 `Thread.sleep(50)` 伪可靠等待，改用 `CompletableFuture.allOf(*kickFutures).get(200, TimeUnit.MILLISECONDS)` 进行有界排空等待。
- [`server/src/test/kotlin/com/majiang/server/udp/UdpTableServerRegressionTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpTableServerRegressionTest.kt) *(新增模块测试)*
  - 覆盖大厅/会话请求篡改载荷返回 `CONFLICT`、乱序 seq=20/19 均正常响应、被踢 KICK 重发与 ACK 确认。

### 3. 客户端 (client)
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
  - 连接身份收紧：已绑定状态下（`connectionId != 0`）对所有非匹配 `connectionId`（包括 `connectionId == 0`）一律静默丢弃；仅未绑定的握手请求（CREATE/JOIN/RESUME）响应可采纳新分配的 `connectionId`。
  - 分片重组完成后的完整帧再次执行身份合法性校验；**仅在身份校验通过后方才刷新链路活跃时间**。
  - 处于 `closed` 状态的绑定**仅对针对当前终止连接的 KICK 回发 ACK**，不再对任意帧回 ACK。
  - `triggerFullSnapshotSelfHeal()` 增加 `isHealingSnapshot` 原语防重，杜绝并发/重复自愈快照请求。
  - `handlePushFrame` 使用 `ApiMessageCodec.decodePushEnvelope` 读取线上显式标记。
  - `leaveRoom` 成功后不仅解绑参数，还执行 `pushSequencer.reset(0L)`。
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt)
  - 接收线程循环条件调整为 `while (!socket.isClosed)`，在 `binding.closed` 后继续保持监听，配合 `onCloseCallback` 延迟 500ms 关闭底层 Socket，确保能够接收并 ACK 服务端重传的终结 KICK 报文。
- [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt)
  - `testHeartbeatMissedPongsTriggersDead`：在推进模拟时间时注入入站 ACK 帧，维持入站活跃，避免 10 秒空闲的 `READ_TIMEOUT` 掩盖 3 次丢失心跳的 `MISSED_HEARTBEATS` 判死。

---

## 二、六大缺陷与编译问题修复映射

| 序号 | 缺陷描述 (引自 TASK-ANTI-R1FIX1) | 修复方案与所在文件 |
| :--- | :--- | :--- |
| **0** | `server:compileKotlin fails at UdpTableServer.kt:591` | `UdpClientSession.putInFlight` 采用原子 `putIfAbsent` 并返回既有 Future；统一了返回类型与 completion/removal 生命周期。 |
| **1** | `PushSequencer.adoptFullSnapshot` 盲目降低游标；`offer` 对陈旧快照返回 `Applied`；全量快照缺显式线上标记；防重复自愈请求 | `PushSequencer.kt`：`adoptFullSnapshot` 单调递增；`offer` 优先陈旧检测（`cursor <= currentCursor` 返回 `Stale`）；线上携带 `"full": true, "isFullSnapshot": true`；`UdpClientBinding.kt` 增加 `isHealingSnapshot` 防重。 |
| **2** | `handleSessionReq` 曾使用最高序防重放导致 seq20 后到 seq19 被拒；Token/代次未前置 | `ReplayGuard.kt` 采用 256 环形滑动窗口；`UdpTableServer.kt` 将会话房间、代次与 Token 校验置于缓存/在途检查之前。 |
| **3** | `CompletedLobbyEntry.rawPayload` 为空串未查冲突；同 `requestId` 篡改载荷或 API 需返回 `CONFLICT` | `UdpTableServer.kt`、`UdpClientSession.kt` 完整记录并比对 `rawPayload`、`api` 与 `request`，冲突立即回复 `CONFLICT`；所有在途/完成映射表有界并超时淘汰。 |
| **4** | 已绑定客户端接受 connectionId=0 响应；重组后缺少身份校验；心跳保活过早刷新；关闭后乱回 ACK | `UdpClientBinding.kt`：已绑定端严格拒绝 `connectionId == 0`；重组完整帧后校验连接合法性；仅合法帧刷新活跃时间；关闭状态仅 ACK 针对本端 `terminatingConnectionId` 的 KICK。 |
| **5** | `sendKick` 仅发单次；`close` 使用 `Thread.sleep(50)` 假装可靠；客户端立即关 Socket 导致重传 KICK 无法被 ACK | `UdpTableServer.kt`：`sendKick` 增加指数退避有界重试并追踪 ACK；`close` 改为 `CompletableFuture.allOf` 200ms 排空；`UdpClient.kt` 延迟 500ms 释放 Socket，接收线程监听至 Socket 关闭。 |
| **6** | `ApiMessageCodec` 提前拦截未知非空 API 导致 `MALFORMED_REQUEST` 掩盖 `UNKNOWN_REQUEST_TYPE`；嵌套字段绕过边界 | `ApiMessage.kt`：语法合法的未知非空 API 标签放行至服务端分发器；对 `config`、`choice`、`shufflePlan`、`preferSeat`、`cursor` 进行显式非空/取值范围边界校验。 |

---

## 三、未决事项说明

- **本任务（R1FIX1）范围内无未决缺陷**。所有 6 项缺陷、1 项编译失败以及 2 项客户端测试夹具/幂等行为均已修复对齐。
- 所有代码均遵循 V4 契约规范，未修改 `tools/architecture/v4-review/` 下由 Codex 拥有的 18 项审查测试。
- 请 Codex 运行全量模块测试 (`:protocol:test`, `:client:test`, `:server:test`) 及架构审查测试 (`:server:test --tests com.majiang.review.*`) 进行独立构建与验收。
