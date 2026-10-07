# 任务 ANTI-T2 交付报告：服务端编译故障修复与全量符号引用排查

- **任务编号**：ANTI-T2（接续 T1 交付与 R1 构建反馈）
- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (生产开发)
- **检视与验收方**：Codex (测试设计、构建执行与审查)
- **遵循契约与设计规范**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[TASK-N3.md](TASK-N3.md)、[TASK-GLM-T.md](TASK-GLM-T.md)

---

## 1. 任务范围与所有权边界

本次修复严格遵守代码所有权边界：
- **唯一写入范围**：
  - `server/src/main/**`
  - `client/src/main/kotlin/com/majiang/network/*.kt`（排除 `protocol/**`）
  - `doc/v3-migration/delivery-ANTI-T2.md`（本报告）
- **零越界保证**：
  - 未修改 `protocol/**`（R1 归属）；
  - 未修改 `engine/**` 与 `TableHost`（R1 归属）；
  - 未修改 `GameClient` 与 `sync/**`（C1 归属）；
  - 未修改 `core/**` / UI（U1 归属）；
  - 未修改任何测试源码与 Gradle 构建文件；
  - 零终端/命令行执行（严格遵守 headless 模式约束），全过程通过内置文件读写与精确替换工具完成。

---

## 2. R1 编译故障根因分析与修复

### 2.1 编译故障分析 (build/v3-migration/anti-first-review.log)

在 Codex 执行 Gradle `:server:compileKotlin` 时报告以下编译错误：
- **故障位置**：`server/src/main/kotlin/com/majiang/server/TableServer.kt`（原约 281 行，当前 289 行）
- **错误原因**：
  在 `val respond: (() -> CompletableFuture<NetworkResponse>) -> Unit = { supplier -> ... }` 中显式声明了返回类型 `CompletableFuture<NetworkResponse>`，但文件头部未导入 `java.util.concurrent.CompletableFuture`。
- **级联反应**：
  由于 `CompletableFuture` 符号未决，Kotlin 编译器无法解析 `supplier()` 返回值类型，导致随后的 `.whenComplete { resp, err -> ... }` 方法调用以及闭包参数 `resp`、`err` 的类型推导全部失败报错。

### 2.2 修复措施

在 `server/src/main/kotlin/com/majiang/server/TableServer.kt` 头部显式添加：
```kotlin
import java.util.concurrent.CompletableFuture
```
使得 `val respond` 闭包类型声明以及第 220 行 `room.resume(...).whenComplete` 拥有完全已知的强类型签名，彻底闭环该编译器故障。

---

## 3. 全量同类缺失引用与空安全防御性排查

在修复 `TableServer.kt` 的同时，对归属范围内的全部生产代码进行了系统的类引用、包导入与平台类型空安全排查：

| 生产文件 | 排查项与修复明细 | 状态 |
|---|---|---|
| `server/src/main/kotlin/com/majiang/server/TableServer.kt` | 1. 补全缺失的 `import java.util.concurrent.CompletableFuture`；<br>2. 逐一比对 `AccessCapabilities`, `MatchConfig`, `NetworkRequest`, `NetworkResponse`, `RoomSummary`, `ServerSocket`, `SocketException`, `UUID`, `ConcurrentHashMap`, `ExecutorService`, `Executors`, `RejectedExecutionException`, `ScheduledExecutorService`, `ThreadFactory`, `TimeUnit`, `AtomicBoolean` 全部符号，均已就绪且与 engine/client 导出类型完全吻合。 | 已修复 |
| `server/src/main/kotlin/com/majiang/server/ServerRoom.kt` | 1. 确认已包含 `import java.util.concurrent.CompletableFuture`；<br>2. 排查并强化 Java 异步契约 `.handle { receipt/snap/decision/events, err -> }` 的空安全处理：Java `CompletableFuture.handle` 回调参数为 platform types (`T!` 与 `Throwable!`)，在 `startMatch`, `submitChoice`, `nextRound`, `querySnapshot`, `queryDecision`, `resumeCursor`, `requestControl`, `queryRoundResult` 中补齐 `if (err != null || result == null) commandFailed(...)` 防御性校验，避免异常分支时解引用平台类型引发潜在 NPE。 | 已强化 |
| `server/src/main/kotlin/com/majiang/server/ClientConnectionHandler.kt` | 1. 显式添加 `import java.net.SocketTimeoutException`，消除此前 catch 块中的长全限定类名写法，风格统一；<br>2. 核验 `NetworkFrameCodec`, `NetworkRequest`, `NetworkResponse`, `ProtocolJson`, `ArrayBlockingQueue`, `TimeUnit`, `AtomicBoolean` 均完全导入；<br>3. 核验调用 `server.handleRequest` 与 `server.onConnectionClosed` 签名完全匹配。 | 已优化 |
| `server/src/main/kotlin/com/majiang/server/TableServerMain.kt` | 核验 `exitProcess`, `TableServer`, `Runtime`, `Thread`, `InterruptedException` 符号，无任何缺失。 | 验证通过 |
| `client/src/main/kotlin/com/majiang/network/ClientTransport.kt` | 核验全部 24 个导入项与并发集合、调度器类引用，确认 `sendRequest` 双重载（Int 与 Long）及 `connectAsync` 均有完整类型支持。 | 验证通过 |
| `client/src/main/kotlin/com/majiang/network/NetworkSessionAccess.kt` | 核验 `SessionAccess`, `MahjongGameAccess`, `BoundMahjongGameAccess`, `ActionReceipt`, `PlayerSnapshot`, `PlayerUpdate`, `ReceiptStatus`, `ActionChoice`, `PlayerDecision`, `GameEvent` 符号，完全对齐 engine 最新接口。 | 验证通过 |
| `client/src/main/kotlin/com/majiang/network/RemoteSession.kt` | 核验伴生工厂方法 `create`, `join`, `resume`, `listRooms` 签名及 `CompletableFuture` 链式调用。 | 验证通过 |
| `client/src/main/kotlin/com/majiang/network/MallSession.kt` | 核验伴生工厂方法与 `NetworkSessionAccess` 继承关系。 | 验证通过 |

---

## 4. T1 核心机制完整保留验证

本次修复严格属于符号引用与类型安全性修复，**未改动传输模型、未重写协议、未新增/删除任何对外 API**，T1 实现的所有机制均完整保留：
1. **零调用线程 socket 阻塞**：`ClientTransport` 与 `ClientConnectionHandler` 均维持 `ArrayBlockingQueue(256)` 有界队列与独立后台写线程；
2. **连接关闭无死锁**：连接关闭回调 `onConnectionClosed` 依然通过 `TableServer.teardownExecutor` 异步清理，绝不从 TableHost 串行锁同步进入房间监视器锁；
3. **顶替保护与代次安全**：重连恢复时维持 `tableHost.connect(seat)` 先行接入，随后标记 `displaced`，递增 `connectionGeneration`，最后关闭旧连接，旧连接断开绝不误判座位掉线；
4. **一次性开赛冻结与显式收回**：`startMatch` 仅在成功开赛后执行一次未占用空位托管补齐，重连后保持托管状态等待客户端显式调用 `requestControl(takeover = false)` 收回控制权；
5. **早期更新缓冲**：`NetworkSessionAccess.earlyUpdateBuffer` 维持 128 条容量，保障客户端连接建立到 `subscribe` 安装之间的推送零丢失。

---

## 5. 验证状态与说明（诚实边界说明）

- **静态代码审查与符号交叉验证**：
  基于当前工作区 `engine`、`client`、`server` 源码树完成全量静态阅读与比对，确认所有类名、包名、方法签名、构造参数均匹配，无任何未解析的类型引用。
- **构建与测试状态说明**：
  根据指令要求与权限策略，本会话在 Headless 环境下未调用任何 Gradle 任务、Shell 脚本或本地进程构建。实际编译器通过状态（`:server:compileKotlin`）与回环测试执行（`NetworkAcceptanceTest`、`ClientTransportAcceptanceTest`）由检视方 Codex 统一执行并最终验收。

交付完成，请 Codex 执行实际编译与测试验收。
