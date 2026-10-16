# TCP 迁移设计检视报告与冻结决策

日期：2026-10-15。检视对象：[TCP-MIGRATION-DESIGN.md](TCP-MIGRATION-DESIGN.md)。核对基线：`3a382c6` 源码。结论：**设计总体可实施**，发现 3 处需修订文本、9 项实施前必须冻结的欠定契约，均以 D 系列决策处置；与设计正文冲突处以本文 D 系列为准。用例见 [TCP-MIGRATION-USECASES.md](TCP-MIGRATION-USECASES.md)。

## 一、冻结决策（D1–D13，实施必须遵守）

| # | 决策 | 依据与说明 |
|---|---|---|
| D1 | 同键不同 API/载荷沿用现有 `ApiErrors.CONFLICT`，**不新增** `REQUEST_ID_CONFLICT` | 设计 §5 的 `REQUEST_ID_CONFLICT` 与现有 `CONFLICT`（[ApiErrors.kt:54](protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt#L54) "请求标识相同但载荷或接口冲突"）语义完全一致，且 MR-03 服务端已按 `CONFLICT` 回包。新增会造成双码并存、分类器与文档分裂 |
| D2 | `SERVER_BUSY` **已存在**（[ApiErrors.kt:57](protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt#L57)），设计 §4 "拟新增" 表述作废，直接沿用 | 核对结果，无动作 |
| D3 | 新增 `ApiErrors.REQUEST_SCOPE_EXPIRED` 并入 `ALL` 集合；T1 落地 | 现无对应码（§5 §93 确为新增）。若现有测试断言 `ALL` 全集，按契约扩展断言（属新增，不属削弱） |
| D4 | CLOSE reason 码表冻结：复用 `ApiProtocol` 现有 `SERVER_SHUTDOWN`、`ROOM_CLOSED`、`DISPLACED_BY_NEW_CONNECTION`、`LOCAL_CLOSED`、`HEARTBEAT_TIMEOUT`；新增 `VERSION_MISMATCH`、`PROTOCOL_VIOLATION`、`HANDSHAKE_TIMEOUT`、`SERVER_OVERLOADED`、`SLOW_CONSUMER`，集中定义于 protocol（TCP 稳定定义对象，不散写字面量）。分类：`VERSION_MISMATCH`/`PROTOCOL_VIOLATION` 为永久（客户端缺陷需升级）；`HANDSHAKE_TIMEOUT`/`SERVER_OVERLOADED`/`SLOW_CONSUMER` 为临时（有界退避后重试） | 设计 §3 type10 只说"稳定 reason code"未枚举；不冻结则 T2/T3 各写一套 |
| D5 | PING/PONG nonce 冻结：8 字节随机数，JSON 中编为 16 个 hex 字符；PONG 必须逐字节回显；nonce 长度/格式非法按 `PROTOCOL_VIOLATION` 关闭 | §3 type8/9 仅写"有界 nonce" |
| D6 | WELCOME DTO 字段冻结：`wireVersion`、`businessVersion`、`connectionEpoch`、`lobbyResumeToken`、`lobbyScope{scope, expiresAtMillis}`、`limits{maxInFlightRequests=64, requestTimeoutMillis=10000, heartbeatIntervalMillis=3000, idleDeadMillis=10000}`。客户端以服务器回包/关闭为准，不得用 `expiresAtMillis`（两机墙钟不可比）自行提前判死停发 | §3 type2 "预算与期限"未列字段 |
| D7 | TCP REQ/RSP 外层信封：REQ=`{requestScope, requestId, request: ApiRequest}`（外层 `requestId` 必须等于 `request.requestId`，不一致按 `PROTOCOL_VIOLATION` 断开，不是业务错误）；RSP=`{requestScope, requestId, response: ApiResponse, grantedScope?: {scope, expiresAtMillis}}`。`grantedScope` 仅出现在成功的 JOIN_ROOM/RESUME_SESSION/JOIN_SPECTATOR/RESUME_SPECTATOR 响应；业务 DTO（RoomBinding/SpectatorBinding）不塞传输字段 | §3 type3/4、§5 "绑定 RSP 外层另携带角色 scope"未定载体 |
| D8 | `lobbyResumeToken`/`scope` 格式：服务端生成的不透明字符串，内含 ≥128 bit 随机（URL-safe 编码）；同进程不重复；不写日志；进程重启后失效且明确告知（INVALID_TOKEN 语义），不承诺跨重启恢复 | §5 只写"随机恢复 token" |
| D9 | `GET_REQUEST_SCOPE` 冻结：作为普通 REQ（`ApiRequest(api="GET_REQUEST_SCOPE")`，无额外业务载荷）发送；仅凭已完成握手+当前绑定身份准入；成功时 RSP 信封必带 `grantedScope`，`ApiResponse.success=true`；并发同身份返回同一 scope；scope 仍有效时返回现有值，过期按需新建；不创建房间、不执行游戏动作；速率限制初值每身份 1 次/秒（T0 复核）；InProcess 提供等价内部入口（见 F14） | §5 末段"载荷/错误码在 T0 冻结"——本决策即冻结值 |
| D10 | 时钟/调度迁移拆两步：T1 新建 `transport/common`（`TransportClock{currentTimeMillis()+nanoTime(), SYSTEM}`、`TransportScheduler`、`Cancellable`），**TCP 新代码只依赖中性接口**；`UdpClock.kt` 及其 273 处引用原地不动；T5 删 UDP 时一并收编改名。设计 §7 "迁至 transport.common"目标不变，路径拆两步 | 全仓 `UdpClock/UdpScheduler` 273 处/54 文件（含大量测试），T1 一次全量改名风险大且会污染 UDP 删除前 diff |
| D11 | 单调时钟入契约：`TransportClock.nanoTime()` 为 RTT 测量唯一时基（§3 "本地单调时间"落地）；心跳/超时/截止全部走注入时钟，禁散落 `System.*` | 现 `UdpClock` 只有墙钟毫秒 |
| D12 | 心跳方向冻结：客户端固定每 3 秒 PING（空闲与繁忙均发）；服务端不强制周期 PING，仅在需判活时可发；双方对 PING 必回 PONG；双方独立执行"10 秒无完整合法入站帧判死" | §4 "双方均执行"欠精确 |
| D13 | typed 异常承载：新增 `TcpConnectionException`（网络/超时类，临时）与 `TcpProtocolException`（含 D4 reasonCode）；`ReconnectErrorClassifier` 平移 MR-02 规则（逐层解包、全字匹配、禁 contains），T3 切换后移除 `UdpFrameException` 分支 | §7 末行"统一 typed 网络/协议错误"的落点；现分类器见 [ReconnectErrorClassifier.kt](client/src/main/kotlin/com/majiang/client/session/ReconnectErrorClassifier.kt) |

## 二、核对通过的引用与声称

- 设计 §3 REQ "外层与现有 id 必须一致"：`ApiRequest.requestId` 已存在（[ApiMessage.kt:86](protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt#L86)），D7 信封可实现。
- §3 推送类型载荷：`ProtocolPlayerUpdate`/`ProtocolPublicUpdate` 均已存在且有严格 Gson 适配器（ApiMessage.kt 内 `checkUnknownFields` 风格），TCP 消息解析沿用该风格，不用运行时类名反射分发。
- §4 超时初值与现状一致：`UdpProtocol.REQUEST_TIMEOUT_MILLIS=10s`，迁移无冲突。
- §7 删除清单与实际文件一一对应（`UdpFrame/UdpPacketType/UdpProtocol/Fragmenter/ReplayGuard/PushRetransmitWindow/ReliableRequestor/PushSequencer/HeartbeatPacer/UdpClock` 在 protocol `transport/udp`；`UdpClient/UdpClientBinding/ScheduledExecutorUdpScheduler` 在 client；`UdpTableServer/UdpClientSession/UdpRoom` 在 server；`connectUdp` 在 [ServerConnectionSession.kt:3025](client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt#L3025) 并被 presentation 测试与 `TableNavigation` 引用）。
- MR-01/02/03 语义（换绑检查、锁外回调、typed 分类、FIFO 回执、准入预留、比较删除）在设计中均有对应迁移条款；T2/T3 任务以此为等价验收基线。

## 三、问题清单（按处置归类）

**A. 设计文本需修订（已由 D 系列覆盖，改文档在下一版设计更新时并入）**

| # | 问题 | 处置 |
|---|---|---|
| F1 | §5 `REQUEST_ID_CONFLICT` 与现有 `CONFLICT` 重复 | D1 |
| F2 | §4 `SERVER_BUSY` "拟新增"不实 | D2 |
| F3 | §3 CLOSE 码表未枚举 | D4 |

**B. 实施前必须冻结的欠定契约（本文已冻结）**

F4 WELCOME 字段（D6）；F5 nonce（D5）；F6 绑定 RSP scope 载体（D7）；F7 token 格式（D8）；F8 GET_REQUEST_SCOPE 载荷（D9）；F9 PING 方向（D12）；F10 单调钟接口（D11）。

**C. 任务落点提示（无需改设计）**

| # | 提示 | 落点 |
|---|---|---|
| F11 | `UdpClock` 引用面 273 处/54 文件，时钟迁移拆两步 | T1+T5（D10） |
| F12 | InProcess 等价回执/scope 入口：回执与 scope 存储实现放 server 模块共享层，`TcpTableServer` 与 `InProcessServer` 复用同一实现，避免单机/联机两套幂等规则 | T2 |
| F13 | Android/GL 约束：`TcpClient.connect` 返回 Future，DNS/建连在 I/O 循环执行；presentation 只组装不感知帧 | T3 |
| F14 | 1 MiB 帧上限是 UDP 1200B 限制解除后的新能力：T0 必须实测四人+三绝技+观战+结算最大 JSON 载荷并留 ≥25% 余量；超限时在 T0 决策"提高上限或拆分快照"，禁止静默截断 | T0（主会话） |
| F15 | UDP 纯传输测试→TCP 对应测试的映射表由 T1 交付初版、T5 验收核对（义务来自 §7 末行） | T1+T5 |
| F16 | MR-06/MR-07 未在本轮范围：TCP 任务不得实现其预算/回收改动，也不得与其语义冲突（设计 §8 已声明重定范围） | 全批次 |

## 四、T0 剩余项（已全部关闭，2026-10-15）

1. 最大载荷实测（F14）：✅ 已完成——真实四人+三绝技+观战+结算整局，最大 PLAYER_UPDATE 10,128 B ≈ 1 MiB 的 0.97%，余量约 100 倍。实测记录与预算复核见 [CLAUDE-TCP-T0-MEASUREMENT.md](CLAUDE-TCP-T0-MEASUREMENT.md)（工具：独立验收套件 TcpT0PayloadMeasurementTest，Anti 开发、主会话运行）。
2. 预算初值复核：✅ 每座位整局副作用 19-23 次，512/64 初值充裕；设计 §4 预算表全部初值冻结为实施值。
3. GET_REQUEST_SCOPE 速率限制初值（D9，1 次/秒）：✅ 维持不变（余量 10 倍以上）。

**T2 派发门禁已解除。**
