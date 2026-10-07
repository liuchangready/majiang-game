# 交付报告：任务 R4B1ROOMTESTFINAL (InProcessBindingTest Disconnect Listener Timing Fix)

## 1. 任务背景与执行模式声明

- **任务代号**：R4B1ROOMTESTFINAL
- **开发角色**：Antigravity (Anti)
- **验收角色**：Codex（拥有架构终审、独立测试运行、构建与验收权）
- **测试执行状态声明**：**Anti 本人严格遵守契约与指示，未执行任何 `./gradlew`、shell、终端、构建或测试命令**。所有编译检查、测试执行与最终验收由 Codex 独立运行。

---

## 2. 变更文件清单

- `server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`

> 注：本任务严格遵守文件所有权限制，未修改任何生产代码、其他模块代码、构建脚本或 `tools/architecture/v4-review` 目录下的用例。

---

## 3. 核心修复说明

### 3.1 缺陷分析
在用例 `host leave succeeds while guest receives room closed without UI subscription` 中：
- `guestConn.close()` / `claimClosure()` 在核心锁内原子将 `isClosed` 置为 `true`，并设置 `disconnectReason = ApiProtocol.ROOM_CLOSED`。
- 而向 `onDisconnect` 注册的监听器回调派发 `runCloseTeardown` 遵循架构契约在锁外异步/后续执行。
- 测试原先通过轮询 `guestConn.closed` 作为退出等待的条件。当 `guestConn.closed` 刚变为 `true` 时，锁外的 `onDisconnect` 监听器尚未完成调用，导致紧随其后的断言 `assertTrue(guestDisconnectReasons.contains(ApiProtocol.ROOM_CLOSED))` 偶发/必发失败。

### 3.2 修复措施
- 在 `InProcessBindingTest.kt` 的该用例中，引入 `val guestDisconnectLatch = CountDownLatch(1)`：
  ```kotlin
  val guestDisconnectLatch = CountDownLatch(1)
  val guestDisconnectReasons = CopyOnWriteArrayList<String>()
  guestConn.onDisconnect {
      guestDisconnectReasons.add(it)
      guestDisconnectLatch.countDown()
  }
  ```
- 将对 `guestConn.closed` 的无保障轮询替换为有界信号等待：
  ```kotlin
  assertTrue(guestDisconnectLatch.await(3, TimeUnit.SECONDS), "Guest must receive onDisconnect callback")
  assertTrue(guestConn.closed, "Guest connection must be closed after host left")
  assertEquals(ApiProtocol.ROOM_CLOSED, guestConn.disconnectReason)
  assertTrue(guestDisconnectReasons.contains(ApiProtocol.ROOM_CLOSED))
  ```
- **完整保留断言**：完全保留对 `guestConn.closed`、`guestConn.disconnectReason` 以及 `guestDisconnectReasons.contains(ApiProtocol.ROOM_CLOSED)` 的断言，无任何削弱或删除。
- **锁外执行原则不变**：没有将监听器回调移回核心锁内，维持生产端锁外安全派发架构。
- **后续有效断言保持原样**：保留访客后续操作被拒（`SESSION_CLOSED`）、房主连接依然活跃可继续建房等所有断言与资源清理。

---

## 4. 未解决项 (Unresolved Items)

- **无 (None)**：任务范围内针对 `InProcessBindingTest.kt` 第 516 行的时序竞态问题已完成针对性修复，未遗留任何阻塞项。

---

## 5. 验收交接

- 本任务修复已落盘，交付事件就绪。
- 请 Codex 接续进行测试编译、全量回归与验收。
