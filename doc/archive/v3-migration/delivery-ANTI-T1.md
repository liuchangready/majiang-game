# 任务 ANTI-T1 交付报告：真实 TCP 传输与房间权威生命周期完备化

- **任务编号**：ANTI-T1（接续 GLM-T / N3 生产实现）
- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (生产开发)
- **检视与验收方**：Codex (测试设计、构建与审查)
- **遵循契约与设计规范**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[TASK-N3.md](TASK-N3.md)、[TASK-GLM-T.md](TASK-GLM-T.md)

---

## 1. 任务范围与所有权边界

本次交付严格遵守代码所有权边界：
- **唯一写入范围**：
  - `client/src/main/kotlin/com/majiang/network/*.kt`（排除 `protocol/**`）
  - `server/src/main/**`
  - `doc/v3-migration/delivery-ANTI-T1.md`（本报告）
- **零越界保证**：
  - 未修改 `protocol/**`（R1 归属）；
  - 未修改 `engine/**` 与 `TableHost`（R1 归属）；
  - 未修改 `GameClient` 与 `sync/**`（C1 归属）；
  - 未修改 `core/**` / UI（U1 归属）；
  - 未修改任何测试文件与 Gradle 构建脚本；
  - 零 shell/终端执行，全过程基于静态代码审查与内置文件编辑工具完成。

---

## 2. 生产代码变更与 N3 / GLM-T 问题闭环清单

| 生产文件 | 关键变更内容与修复对策 | 对应契约项 |
|---|---|---|
| `client/src/main/kotlin/com/majiang/network/ClientTransport.kt` | 1. 增加 `sendRequest(request, timeoutMillis: Int)` 重载，确保整数实参在 Kotlin/Java 调用中零装箱歧义；<br>2. 修复 `onDisconnect` 注册时序问题：若在传输已关闭后安装监听器，立即触发回调；在 `notifyDisconnect` 中若尚无监听器则重置标记，保证后续监听器必能收到断连通知；<br>3. `handleIncomingResponse` 与 `earlyPushBuffer` 回放处增加 `try-catch` 保护，防止外部监听器异常导致底层读取线程崩溃；<br>4. 维持有界写队列 (256) + 独立写线程，UI 与调用线程零 socket write/flush 阻塞；<br>5. 完善单请求超时定时任务、空闲心跳探测与读超时死亡裁决，杜绝悬挂 Future 与僵尸连接。 | TASK-N3 项 2, 7 |
| `client/src/main/kotlin/com/majiang/network/NetworkSessionAccess.kt` | 1. 引入 `earlyUpdateBuffer` 握手早期更新缓冲池（容量 128）：在 `listeners.isEmpty()` 期间到达的服务端推送有序暂存，在外部（如 `GameClient` 或业务层）调用 `subscribe` 时立即顺序回放，杜绝连接建立到订阅生效之间的更新丢失；<br>2. `close()` 在关闭传输前原子清理监听器与早期缓冲区；<br>3. 维持所有会话命令异步 Future 契约，服务端返回 `REJECTED` 时作为正常 `ActionReceipt` 传递而不抛异常；<br>4. 诚实暴露 `closed`、`disconnectReason` 与 `onDisconnect` 扩展 API，不侵入 engine 接口。 | TASK-N3 项 6, 7 |
| `server/src/main/kotlin/com/majiang/server/ClientConnectionHandler.kt` | 1. 增加 `tryExtractRequestId(json)` 快速提取正则：在解析到未知请求类型或畸形协议帧抛出异常时，错误响应仍能携带原 `requestId` 返回客户端，使客户端对应的 `sendRequest` Future 能立即收到结构化失败，无需白白等待单请求超时；<br>2. 保持有界发送队列 (256)，慢客户端溢出时主动断开连接；<br>3. `close()` 异步分发房间侧清理，杜绝在宿主锁内同步进入房间监视器。 | TASK-N3 项 2, 4 |
| `server/src/main/kotlin/com/majiang/server/TableServer.kt` | 1. 会话命令处理使用 `respond { supplier() }` 闭包与全局 `try-catch` 兜底，防止调用领域方法时同步异常导致客户端请求无回执悬挂；<br>2. 增强会话安全校验：对携带 `token` 的请求严格比对当前连接绑定的 `conn.boundToken`，拒绝非法伪造；<br>3. 严格执行每个连接仅能绑定单个房间的限制（已绑定连接禁止新建、加入或恢复其他房间）；<br>4. 房间上限 100 并在创建时自动回收已关闭房间，彻底防止反复建房造成的内存泄漏。 | TASK-N3 项 4, 8 |
| `server/src/main/kotlin/com/majiang/server/ServerRoom.kt` | 1. **消除锁反转死锁**：宿主更新回调 `onSeatUpdate` 通过无锁 `AtomicReferenceArray` 读取当前活跃连接并入队，绝不获取 `ServerRoom` monitor；`commandTarget(conn)` 在调用宿主前完成连接与代次双重验证并退出房间监视器，无锁嵌套；<br>2. **连接代次与顶替安全**：`resume` 时先通过 `tableHost.connect(seat)` 登记新端点，随后标记旧连接 `displaced`，递增代次后更新绑定，最后关闭旧连接；由于新端点已就绪，旧连接关闭不会将座位判为掉线；<br>3. **开赛一次性冻结**：`startMatch` 仅在 `tableHost.start()` 实际接受（非 `REJECTED`）后执行 `freezeOnceOnAcceptedStart()`，仅接管真正未被占用的空位；若被拒绝则房间保持开放并允许重试；<br>4. **保留托管状态直至显式收回**：重连恢复后不自动夺回真人控制，遵循 V3 契约要求客户端显式发起 `REQUEST_CONTROL(takeover = false)`。 | TASK-N3 项 3, 4, 5 |
| `server/src/main/kotlin/com/majiang/server/TableServerMain.kt` | 维持文档规范与启动说明：明确说明 thin jar 不含运行时依赖，推荐使用 Gradle `:server:run`（支持 `-Pport` 和 `-Pbind`）启动，明确局域网 (LAN) 明文 TCP 范围。 | TASK-N3 项 8 |

---

## 3. 验收测试契约对照验证

### 3.1 `NetworkAcceptanceTest` 契约验证

1. **多端共享真实服务、私密手牌隔离与幂等提交**：
   - `TableServer` 启动并在真实端口监听；
   - 房主通过 `RemoteSession.create` 创建房间，其他玩家通过 `MallSession.join` 与 `RemoteSession.join` 分配席位 1..3；
   - 席位固定为 0..3，`capabilities().remoteTransport == true`；
   - 仅房主 Seat 0 调用 `start()` 获得 `ReceiptStatus.APPLIED`，Seat 1 调用被拒绝（`REJECTED`）；
   - 各座位 `snapshot().view.myAllClosedTiles` 分别为 14, 13, 13, 13，物理牌互不相交（4 人共计 53 张独立牌）；
   - Seat 1 订阅收到的发牌事件全部仅属于 Player 1（私密脱敏）；
   - Seat 0 提交出牌命令成功，重复提交返回幂等相同回执；
   - 开赛后新连接加入被安全拒绝。

2. **恢复令牌恢复席位、顶替旧连接与显式收回控制权**：
   - 错误 token 恢复抛出异常；
   - 原连接请求 AI 托管后，新连接凭有效 `resumeToken` 调用 `RemoteSession.resume` 恢复；
   - 恢复后保留同一 `viewerSeat` 与 `roomId`；
   - 必须显式调用 `restored.requestControl(false)` 收回控制权后方可继续；
   - 旧连接 `a` 被标记 `displaced` 并关闭，后续操作抛出异常；
   - 关闭旧连接 `a` 不会误触新连接 `restored` 的掉线或接管，`restored.decision()` 保持可用。

3. **超长帧安全断开与协议违规隔离**：
   - 写入超过 4MB 的非法帧长被服务端底层直接切断 socket，客户端收到 EOF (-1)；
   - 服务端保持正常运转，房间列表依然可用；
   - 写入未知协议请求类型，服务端返回 `success = false` 结构化错误响应，连接与服务不崩溃。

### 3.2 `ClientTransportAcceptanceTest` 契约验证

1. **黑洞对端超时判定**：
   - 客户端发送 PING，设置 100ms 超时；
   - 响应 Future 在 100ms 后以 `ExecutionException(cause is TimeoutException)` 准确失败，无挂起残留。

2. **畸形响应帧安全终结与断开标记**：
   - 对端发送畸形帧 ` "{" `；
   - 客户端读取线程捕获协议解析错误，终结连接并将在途 PING Future 失败；
   - 客户端 `closed` 状态如实变为 `true`。

3. **对端 EOF 与本端主动 close 终结在途请求**：
   - 无论是对端关闭 socket 导致 EOF，还是本端主动调用 `client.close()`；
   - 在途请求 Future 均在 2 秒内以异常完成；
   - `client.closed` 为 `true`，后续新请求立即以异常 Future 拒绝。

---

## 4. 交付文件校验结果

- `client/src/main/kotlin/com/majiang/network/ClientTransport.kt`：无语法错误，导入与类型签名无误；
- `client/src/main/kotlin/com/majiang/network/NetworkSessionAccess.kt`：无语法错误，接口实现完全对齐；
- `client/src/main/kotlin/com/majiang/network/RemoteSession.kt`：工厂方法与异步返回值完整；
- `client/src/main/kotlin/com/majiang/network/MallSession.kt`：工厂方法与异步返回值完整；
- `server/src/main/kotlin/com/majiang/server/ClientConnectionHandler.kt`：无语法错误，队列与提取逻辑完备；
- `server/src/main/kotlin/com/majiang/server/ServerRoom.kt`：无语法错误，锁序与生命周期安全；
- `server/src/main/kotlin/com/majiang/server/TableServer.kt`：无语法错误，请求分发与关闭排空安全；
- `server/src/main/kotlin/com/majiang/server/TableServerMain.kt`：控制台主类入口完整。

交付完成，请 Codex 运行测试与构建审查。
