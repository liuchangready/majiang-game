# 交付报告：R4B1CLIENTFIX1 客户端 UDP 首轮验收返修

## 1. 任务概述与交付文件范围

本任务根据 [`doc/v4-migration/TASK-ANTI-R4B1CLIENTFIX1.md`](file:///D:/majiang/doc/v4-migration/TASK-ANTI-R4B1CLIENTFIX1.md) 与 [`doc/v4-migration/FEEDBACK-R4B1CLIENTFIX1.md`](file:///D:/majiang/doc/v4-migration/FEEDBACK-R4B1CLIENTFIX1.md) 的反馈指引，针对客户端 UDP 可靠房间更新、模拟响应身份匹配、锁解耦与重入安全、测试有界等待进行了全面修复与加固。

### 修改文件列表（严格限制在独占权限内）
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
- [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt)

### 未修改文件与边界承诺
- 未修改 `ServerConnectionSession`、`client.api` 契约或 mapper。
- 未修改 `protocol` 冻结契约。
- 未修改 `server` 模块实现或任何 reviewer 测试用例（如 `tools/architecture/v4-review/**`）。
- 未修改项目构建配置文件。

---

## 2. 核心问题定位与修复详情

### 2.1 修复测试模拟离房响应中的 connectionId 错误（严禁放宽生产校验）
- **现象**：在 `testDelayedGetRoomInfoQueryDoesNotContaminateNewRoom` 与 `testRoomSwitchResetsVersionAndBuffersAndLowerVersionAccepted` 中，客户端在已绑定房间（`connectionId = 12345`）的状态下调用 `binding.leaveRoom()`。测试注入的模拟离房 RSP 帧却错误使用了 `connectionId = 0`。
- **机制与影响**：生产代码 `UdpClientBinding.onInboundFrame` 正确维护连接身份安全校验，当 `currentConnId != 0` 时，非当前连接 ID 的帧会被立即拦截丢弃。由于注入帧 connectionId 为 0，该 RSP 被正确丢弃，导致 `reliableRequestor.onResponse` 未被触发，`leaveFuture` 无法完成。
- **修复**：
  - 修正测试中注入的 leave RSP 帧：使用绑定的有效 `connectionId = 12345`，使得 leave 请求正常收到响应并在响应处理完成后由客户端重置连接身份（`this.connectionId = 0`）。
  - 在 `testDelayedGetRoomInfoQueryDoesNotContaminateNewRoom` 中，修正换入新房 ROOM2 后的延迟 query RSP 帧 connectionId 为新房当前生效连接 ID `99999`，以模拟该延迟帧通过网络层送达，并精确验证内部基于 `bindingEpoch` 与 `boundRoomId` 的新旧房间状态隔离与监听器抑制机制。
  - 严格保留 `UdpClientBinding.onInboundFrame` 的生产连接身份校验逻辑，绝不放宽。

### 2.2 全面替换测试裸 `join()` 为有界等待 `await(seconds = 5)`
- **修改**：在 `UdpClientBindingTest` 中定义拓展函数：
  ```kotlin
  private fun <T> CompletableFuture<T>.await(seconds: Long = 5): T = get(seconds, TimeUnit.SECONDS)
  ```
- **覆盖**：将测试类中所有测试用例中的裸 `.join()` 替换为 `.await()`。在遇到假时钟未推进或预期外的网络丢包阻断时，快速抛出 `TimeoutException`，杜绝 Gradle Test Worker 出现无限挂起现象。

### 2.3 解耦 `synchronized(this)` 锁与外部监听器回调，加固代次与重入安全
- **原问题**：在 `handleInboundPush` 与 `getRoomInfo` 中，若在 `synchronized(this)` 临界区内部直接调用 `dispatchRoomUpdate` 并同步触发外部监听器回调，若监听器内部存在慢执行或重入操作（例如在回调中同步调用 `leaveRoom()` 或 `close()`），容易导致锁争用或死锁风险。
- **重构与解耦**：
  1. `handleInboundPush`：仅在 `synchronized(this)` 内原子计算版本递增及更新缓存，产出待分发的 `toDispatch: ProtocolRoomInfo?`；外部回调 `dispatchRoomUpdate(toDispatch)` 严格移至锁外部调用。
  2. `getRoomInfo`：同样仅在 `synchronized(this)` 内原子验证代次与更新缓存，产出待分发对象后，在锁外部执行 `dispatchRoomUpdate`。
  3. `dispatchRoomUpdate` & `onRoomUpdate`：在遍历 listener 列表前快照当前 `capturedEpoch` 与 `capturedRoomId`；在每个 listener 调用前增加断路检查：
     ```kotlin
     if (closed || bindingEpoch != capturedEpoch || boundRoomId != capturedRoomId) {
         break
     }
     ```
     若前序 listener 重入执行了 `leaveRoom()` 或 `close()`，后续监听器分发立即安全中断，保证代次隔离和生命周期一致性。

### 2.4 新增重入回归测试用例
- `testReentrantListenerCallingLeaveRoomDoesNotDeadlock`：验证监听器在 `onRoomUpdate` 回调中重入调用 `binding.leaveRoom()`，请求能够无死锁立即发出并由后续 mock 响应成功收敛，连接重置为 0。
- `testReentrantListenerCallingCloseDoesNotDeadlock`：验证监听器在 `onRoomUpdate` 回调中重入调用 `binding.close(...)`，连接能够无死锁安全关闭，且后续监听器被守卫断路阻断。

---

## 3. 测试与验证状态声明

- **测试执行状态**：根据用户指引及架构规范，测试套件与 Gradle 构建完全由 Codex 独立执行与验收，**本开发者（Anti）未直接运行 Gradle 构建或测试命令**。
- **代码完整性**：所有修改均经过本地严格语法与逻辑审查，无未完成的 TODO，保持严格物理守恒与协议兼容。
