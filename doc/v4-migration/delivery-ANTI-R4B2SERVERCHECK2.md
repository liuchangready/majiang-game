# R4B2 SERVERCHECK2 交付报告 (Anti)

## 1. 任务背景与边界声明
- **任务代号**: R4B2SERVERCHECK2
- **执行角色**: Antigravity (Anti) - 生产代码与单元测试编写
- **监督与测试验收角色**: OpenAI Codex
- **操作原则**: 纯内置文件读取与编辑落盘，未运行 shell/Gradle/终端/构建/测试/提交/其他 Agent。
- **阶段边界**: 严格限制在 R4B2 服务端范围内，绝不越界进入 R5。构建与测试是否通过由 Codex 全权独立验证并裁决，本文绝不声称“构建已过”。

---

## 2. 改动文件清单
1. [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
2. [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
3. [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt)

---

## 3. 具体修改内容与对应任务项

### 任务项 1：`UdpRoom.kt:541` 协议参数名编译修复
- 在 `UdpRoom.kt` 第 541 行，将构造 `ProtocolPublicUpdate` 时的字段名由非法的 `full = true` 改正为冻结协议定义的命名参数 `isFullSnapshot = true`。
- 在 `joinSpectator` 与 `resumeSpectator` 的返回绑定中，统一调用 `getPublishedPublicSnapshot() ?: res.snapshot`，确保即使在绑定准备窗口内权威状态有推进（如暂停），观战者也能立即获得最新已发布的权威全量快照。

### 任务项 2：`UdpRoomTest.kt` PublicCheckpoint 边界与对账修复
- 在 `testSpectatorJoinResumeAndCheckpointResend` 中：
  - 合法断言使用 `validCheckpoint = PublicCheckpoint(..., cursor = publishedSnap.cursor)`，动态跟随当前已发布基线游标，消除硬编码 `0L` 导致的状态不同步。
  - 负数游标（`cursor = -1L`）在 `PublicCheckpoint` 构造函数即由 `require(cursor >= 0L)` 防御拦截，改为在构造器边界通过 `assertFailsWith<IllegalArgumentException>` 断言，杜绝先抛出未捕获异常导致后续服务端边界断言不可达。
  - 保留并修复针对超前游标（`cursor + 10L`）、`Long.MAX_VALUE` 溢出游标、错误 `syncEpoch`、错误 `sessionId` 在服务端边界 `handlePublicResend` 返回 `null` 的拒绝断言。
  - 保留并修复凭错误 token 以及错误 checkpoint 恢复观战时服务端抛出 `INVALID_TOKEN` 的断言。

### 任务项 3：真实 UDP 异步加入与角色越权请求断言修复
- 在 `UdpTableServer.kt` 中：
  - 增加 `@Volatile var outboundFrameListener: ((frame: UdpFrame, address: SocketAddress) -> Unit)? = null` 观测点，在 `send` 时可同步通知外部监听者，便于单测捕获服务端实际发出的全部 UDP 响应（RSP）与推送（PUSH）数据帧。
- 在 `UdpRoomTest.kt` 的 `testUdpTableServerSpectatorEndToEnd` 中：
  - 挂载 `server.outboundFrameListener`，并实现带超时轮询的 `waitForOutboundResponse(requestId)`。
  - `joinSpecReq` 异步处理后，等待服务端发出的真实加入响应帧，再安全获取已安装的 `actualSpecSession`，彻底解决未等异步完成便直接读取 `spectators` 导致的竞态与空指针异常。
  - 针对角色越权请求（观战者调用 `SET_READY`，玩家调用 `PUBLIC_SNAPSHOT`）：
    - 响应未缓存属于生产代码设计的正确行为（`handleSessionReq` 在角色门禁处直接 `sendRsp` 返回，不污染会话幂等缓存）。
    - 单元测试通过 `waitForOutboundResponse` 观察实际网络帧中的 `ApiResponse`，断言返回 `UNAUTHENTICATED_SESSION` 错误码。
    - 显式断言 `actualSpecSession.getCachedResponse("req-spec-ready") == null` 与 `hostSession.getCachedResponse("req-player-pub") == null`，验证越权请求绝不进入缓存。
  - 保留对公开快照字段私密性（无私有 `snapshot` 或 `events`）、观战显式 `LEAVE_ROOM` 回执 `APPLIED`、会话终止与注销的完整断言。

### 任务项 4：保留 SERVERRESUME 基线与防复活排他机制
- 保留 `UdpRoom.sendSpectatorBaseline`：在会话完成全局注册后立即补发最新 `roomInfo` 与权威 `ProtocolPublicUpdate` 全量基线。
- 保留关房防复活：`installSpectatorBinding` 与 `resumeSpectator` 内对 `closed` 状态进行双重排他检查，并在服务端终结/代次不符时清理残余连接。
- 保留对 `tools/architecture/v4-review` 中 `V4SpectatorUdpBindingRaceTest.kt` 两个真实 UDP 竞态场景的完整兼容支持。

---

## 4. 未完成项
- **本任务无任何遗留或未完成项**。所有 4 项要求与 Codex 开发中复核意见均已在目标源码与测试中完成直接落盘。

---

## 5. 未运行测试与后续验收
- **未运行测试**: 本次交付中 Anti 严格遵守约束，**未运行任何 Gradle 命令、未启动任何测试套件、亦未运行任何构建脚本**。
- **后续流程**: 交付通知 Codex 接手，由 Codex 执行独立构建、测试回归与验收工作。若验收通过，由 Codex 依序派发后续阶段；Anti 不自行推进至 R5。
