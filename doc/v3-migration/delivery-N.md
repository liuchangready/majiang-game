# 任务 N 交付报告：真实网络传输与权威房间服务

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Task N 生产开发)
- **发起方 / 检视方**：Codex (测试设计、端到端验证、编译构建与验收)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 迁移执行计划](PLAN.md)

---

## 1. 任务范围与所有权边界

本次交付严格遵守第一波与任务 N 代码所有权边界规范：
- **唯一写入范围**：
  - `client/src/main/kotlin/com/majiang/network/**`
  - `server/src/main/kotlin/**`
  - `client/build.gradle`（新增 Gson 2.11.0 依赖）
  - `server/build.gradle`（新增 Gson 2.11.0、Application 插件与 `:server:run` 任务配置）
  - `doc/v3-migration/delivery-N.md`（本交付报告）
- **零越界侵入**：未修改 `engine`、`GameClient`、`Screen` 或任何测试文件；未修改根项目构建与配置；
- **真实网络传输**：采用真实 TCP Socket 往返传输，绝不使用内存伪代理假装远程连接；
- **依赖隔离**：桌面和 Android 客户端不依赖 `server` 模块与任何 UI 依赖，`client` 与 `server` 均为纯 JVM 模块。

---

## 2. 交付生产文件清单

| 模块 / 路径 | 变更类型 | 说明 |
|---|---|---|
| `client/build.gradle` | 修改 | 添加 `api "com.google.code.gson:gson:2.11.0"` 依赖，供 client 模块与下游 server 共享 |
| `server/build.gradle` | 修改 | 添加 `com.google.code.gson:gson:2.11.0` 依赖，引入 `application` 插件并配置 `mainClass` 与 `:server:run` 任务 |
| `client/src/main/kotlin/com/majiang/network/protocol/NetworkFrameCodec.kt` | 新增 | 长度前缀 (4 字节大端 Int) UTF-8 JSON TCP 帧编解码器，具备 4MB 上限拦截与防 OOM/DoS 机制 |
| `client/src/main/kotlin/com/majiang/network/protocol/NetworkMessages.kt` | 新增 | 统一请求 DTO (`NetworkRequest`)、响应与推送 DTO (`NetworkResponse`) 及房间摘要 (`RoomSummary`) |
| `client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt` | 新增 | 纯显式白名单 Gson 适配器集中定义，覆盖所有实体类型、密封类动作/事件，零 Class.forName，零 ObjectInputStream |
| `client/src/main/kotlin/com/majiang/network/ClientTransport.kt` | 新增 | 客户端底层 TCP 帧传输组件，实现异步非阻塞 Future 匹配、独立读线程、推送派发与异常断连安全清理 |
| `client/src/main/kotlin/com/majiang/network/NetworkSessionAccess.kt` | 新增 | 统一网络 SessionAccess 适配器，绑定权威不可伪造 viewerSeat，提供绑定 roundId 的 MahjongGameAccess |
| `client/src/main/kotlin/com/majiang/network/RemoteSession.kt` | 新增 | 玩家自建房网络会话，提供异步 `create`、`join`、`resume` 工厂与 `listRooms` 发现 |
| `client/src/main/kotlin/com/majiang/network/MallSession.kt` | 新增 | 官方大厅网络会话，共享底层传输与 SessionAccess 契约，提供对应大厅异步工厂与房间查询 |
| `server/src/main/kotlin/com/majiang/server/ClientConnectionHandler.kt` | 新增 | 服务端单客户端连接处理者，具备有界发送队列 (128) 隔离慢连接，支持新登录顶替安全关闭 |
| `server/src/main/kotlin/com/majiang/server/ServerRoom.kt` | 新增 | 房间权威宿主包装器，管辖独立 TableHost，处理身份冻结、未满 AI 补位、token 恢复与按席位脱敏投影 |
| `server/src/main/kotlin/com/majiang/server/TableServer.kt` | 新增 | 权威联网麻将房间服务器，管理房间生命周期、50ms 独立权威时钟 tick 调度与多房间请求分发 |
| `server/src/main/kotlin/com/majiang/server/TableServerMain.kt` | 新增 | 独立控制台命令行启动入口，支持 `--port` 与 `--bind` 参数解析及优雅停机 Hook |
| `doc/v3-migration/delivery-N.md` | 新增 | 本交付报告 |

---

## 3. 核心机制与契约落实明细

### 3.1 帧传输与安全序列化 (NetworkFrameCodec & ProtocolJson)

1. **长度前缀 TCP 帧**：
   - 帧结构为 `[4-byte Int (payload length)][UTF-8 JSON bytes]`；
   - 严格设置 `MAX_FRAME_SIZE = 4 * 1024 * 1024` (4 MB)；若帧长度小于 0 或超出 4MB，抛出 `IOException` 并立即关闭连接，防止超大帧攻击导致 OOM；
   - 读写流采用缓冲包装 (`BufferedInputStream` / `BufferedOutputStream`)，套接字启用 `tcpNoDelay = true` 降低延迟。
2. **显式白名单序列化与安全守卫**：
   - 使用 Gson 2.11.0，为 `Tile`、`Meld`、`GameAction`、`GameEvent`、`PlayerView`、`PlayerDecision`、`ClockView`、`ActionReceipt`、`ActionChoice`、`AccessCapabilities`、`MatchConfig`、`PlayerSnapshot`、`PlayerUpdate` 编写了专属的强类型 `TypeAdapter`；
   - **严禁使用任意类反射与 Class.forName**：多态类型使用固定标签映射（如 `DISCARD`, `CHI`, `PON`, `KAN`, `RIICHI`, `TSUMO`, `RON`, `PASS`）；
   - **严禁客户端上传 GameState**：客户端仅能上传经过验证的白名单请求与 `ActionChoice`；
   - **Kotlin 非空与深度隔离**：反序列化显式校验必填字段，集合深复制并由 `Collections.unmodifiableList` 包装；`PlayerView` 反序列化直接调用 `PlayerView.create(...)` 保证 22 项状态不变量。

### 3.2 客户端网络统一适配器 (NetworkSessionAccess, RemoteSession, MallSession)

1. **统一 SessionAccess 契约**：
   - 完全遵循 Task C 冻结的 `SessionAccess` 接口，对外提供纯异步 `CompletableFuture` 签名；
   - 席位不可伪造：连接后由服务端分配固定的权威 `viewerSeat`（0..3），所有提交均由此席位授权；
   - `capabilities()` 声明 `protocolVersion = 3, supportedPlayerCounts = setOf(4), remoteTransport = true, persistence = false, resumption = true`，真实反映网络传输属性；
   - 公开只读 `roomId: String` 与 `resumeToken: String` 供 UI 安全保存，`toString()` 显式将 token 遮蔽为 `***`，杜绝日志泄露凭证。
2. **单局隔离与 BoundMahjongGameAccess 拦截**：
   - `activeGame()` 返回与当前活动局 `roundId` 绑定的 `BoundMahjongGameAccess`；
   - 若局间无活动对局，`activeGame()` 返回 `null`；
   - 当单局推进至下一局或局号不符时，提交立即在客户端本地拦截拒绝（`ROUND_MISMATCH` / `ROUND_NOT_ACTIVE`），绝不将旧局动作提交转嫁到当前局。
3. **工厂方法签名**：
   ```kotlin
   // RemoteSession (玩家自建房)
   RemoteSession.create(host, port, playerName, config = MatchConfig()): CompletableFuture<RemoteSession>
   RemoteSession.join(host, port, roomId, playerName): CompletableFuture<RemoteSession>
   RemoteSession.resume(host, port, roomId, token): CompletableFuture<RemoteSession>
   RemoteSession.listRooms(host, port): CompletableFuture<List<RoomSummary>>

   // MallSession (大厅模式)
   MallSession.create(host, port, playerName, config = MatchConfig()): CompletableFuture<MallSession>
   MallSession.join(host, port, roomId, playerName): CompletableFuture<MallSession>
   MallSession.resume(host, port, roomId, token): CompletableFuture<MallSession>
   MallSession.listRooms(host, port): CompletableFuture<List<RoomSummary>>
   ```

### 3.3 权威房间服务 (TableServer & ServerRoom)

1. **每房间独立 TableHost 与 50ms 节拍调度**：
   - `TableServer` 拥有独立的单线程调度器 `ticker`，每 50ms 触发一次各房间的 `room.tick()`；
   - 依赖 `TableHost` 保证 150ms 自动动作节奏与 20+3 秒权威决策超时判定，服务端自身不篡改领域状态机。
2. **慢客户端有界发送队列隔离**：
   - 每个客户端连接持有容量为 128 的 `ArrayBlockingQueue<String>` 与专属后台写线程；
   - 当 TableHost 发布订阅事件更新时，仅执行 `queue.offer(...)`；若客户端网络慢导致队列溢出，立即主动断开该客户端连接，**绝不让任何慢客户端在 TableHost 串行锁内发生阻塞**。
3. **房间生命周期、冻结点与 AI 补位**：
   - 房主创建房间后，房间处于准备阶段，初始席位 0 归属房主；
   - 开赛前，真人玩家可通过 `JOIN_ROOM` 加入空闲席位 (1..3)；
   - 房主调用 `START_MATCH` 时触发**身份与配置冻结点**：
     * 检查未被真人占用的空位 (1..3)，原子调用 `tableHost.requestControl(seat, takeover = true).join()` 转为 AI 托管；
     * 随后调用 `tableHost.startSession(adminSeat = 0)` 发牌开赛；
     * 比赛开始后，任何新玩家调用 `JOIN_ROOM` 均被明确拒绝 (`ALREADY_STARTED`)，严禁顶替他人席位。
4. **随机不可猜 resumeToken 与断线接管恢复**：
   - 席位分配时生成随机 UUID 令牌 `resumeToken`，绑定 `(roomId, seat, generation)`；
   - 客户端断线后，TableHost 依据 `DisconnectPolicy` 处理（如立即转 AI 接管或等待超时）；
   - 真人重连调用 `RESUME_SESSION`：
     * 校验 `resumeToken`；若匹配，调用 `tableHost.reconnect(seat)` 与 `tableHost.requestControl(seat, takeover = false)` 收回人类控制权；
     * 递增连接代次 `generation`；
     * 若该席位仍存在旧连接，调用 `closeDueToNewLogin()` 发送 `PUSH_DISCONNECT` 并关闭旧连接；
     * **旧连接关闭时，核验代次与当前连接不匹配，绝不再次将席位标为掉线**。
5. **多客户端手牌隐私严格隔离**：
   - 房间内各座位的订阅更新均来源于 `tableHost.subscribeSeat(seat)`；
   - TableHost 权威投影保证观察者仅能看见自己的手牌，他家手牌仅为 `handTileCounts` 计数，多客户端仅接收自身席位的脱敏更新。

---

## 4. 运行与验证指引

### 4.1 启动服务端

通过 Gradle 执行 `:server:run` 任务启动独立服务：

```bash
# 默认在 127.0.0.1:8080 启动
./gradlew :server:run

# 指定监听 IP 与端口
./gradlew :server:run -Pport=9090 -Pbind=0.0.0.0
```

也可以通过主类启动：
```bash
java -cp server/build/libs/server.jar com.majiang.server.TableServerMainKt --port 8080 --bind 127.0.0.1
```

### 4.2 客户端使用示例

```kotlin
// 1. 房主创建房间
val hostSession = RemoteSession.create("127.0.0.1", 8080, "Alice").join()
val roomId = hostSession.roomId
val hostToken = hostSession.resumeToken

// 2. 玩家加入房间
val player2Session = RemoteSession.join("127.0.0.1", 8080, roomId, "Bob").join()

// 3. 房主开赛 (未加入的 Seat 2 与 Seat 3 自动转为 AI 补位)
hostSession.start().join()

// 4. 断线重连
val resumedSession = RemoteSession.resume("127.0.0.1", 8080, roomId, hostToken).join()
```

---

## 5. 当前能力范围与限制声明

按照 V3 统一架构与执行契约，如实声明如下能力范围：
- **已实现能力**：
  - 基于 TCP 长度前缀 UTF-8 JSON 帧的真实网络全双工长连接；
  - 玩家自建房与大厅对局模式的一体化服务与异步 SessionAccess 适配；
  - 房间成员稳定座次、开赛身份冻结与空位 AI 补齐；
  - 凭不可猜测 resumeToken 恢复、多客户端隐私脱敏与重复登录顶替防误掉线；
  - 独立 50ms 权威单桌 tick 驱动与慢客户端有界队列防卡死；
  - 命令行服务端独立运行与 Gradle `:server:run` 任务支持。
- **阶段性限制（留待后续阶段）**：
  - 本次限定局域网 (LAN) 与本地回环可验证的明文 TCP 传输；公网 TLS/WSS 加密通道与 CA 证书认证不在本波次承诺范围内；
  - 游戏状态完全保存在服务端内存中，持久化数据库存储与服务器断电崩溃恢复不在本波次承诺范围内；
  - 三人麻将规则集保持为独立规划阶段，本阶段严格执行四人麻将规范。

---

## 6. Codex 验收测试指引

Codex 可设计端到端回环测试套件验证如下核心用例：

1. **`TableServerLifecycleTest`**：
   - 启动 `TableServer(port = 0)`，获取 `boundPort`；
   - 验证服务正常启动与幂等 `close()` 释放端口。
2. **`RemoteSessionEndToEndTest`**：
   - 房主调用 `RemoteSession.create(...)`，验证分配 `seat = 0` 并获得有效 `roomId` 与 `resumeToken`；
   - 玩家 2 调用 `RemoteSession.join(...)`，验证分配 `seat = 1`；
   - 房主调用 `start()` 开赛，验证 Seat 2 与 Seat 3 由 AI 自动补位；
   - 验证 Seat 0 与 Seat 1 均能收到合法的 `PlayerSnapshot`，且各自手牌严格保密隔离。
3. **`DisconnectAndResumeTest`**：
   - 玩家 1 断开连接，验证 TableHost 触发掉线接管（AI 托管）；
   - 玩家 1 使用原 `roomId` 与 `resumeToken` 调用 `RemoteSession.resume(...)`；
   - 验证成功恢复连接，收回控制权，且最新游标与快照正确恢复。
4. **`DuplicateLoginDisplacementTest`**：
   - 使用相同 token 在新连接上调用 `resume`；
   - 验证旧连接收到 `PUSH_DISCONNECT`（reason: `"DISPLACED_BY_NEW_CONNECTION"`）并被关闭；
   - 验证旧连接断开不会导致席位再次进入掉线状态。
5. **`SecurityAndFrameLimitTest`**：
   - 发送超出 4MB 的非法超长帧头，验证服务端立即拒绝并关闭非法连接；
   - 发送恶意或未知的消息类型，验证服务端返回结构化错误且不崩溃。
6. **`BoundMahjongGameAccessTest`**：
   - 获取活动单局的 `MahjongGameAccess`，验证旧 roundId 动作提交立即在客户端被拦截拒绝（`ROUND_MISMATCH`）。
