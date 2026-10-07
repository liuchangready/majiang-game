# 任务 ANTI-C1 交付报告：客户端生命周期、缺口链式自愈与无锁回调守护

- **任务编号**：ANTI-C1 (接续 TASK-GLM-C)
- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **协作与验收角色**：Codex (Designer, Test Author, Reviewer & Builder)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[TASK-GLM-C.md](TASK-GLM-C.md)、[plan.md](plan.md)

---

## 1. 交付变更清单

本次交付严格遵守代码所有权边界，仅修改了分配给本任务的文件，未修改任何测试代码、构建脚本、网络传输、引擎核心或 UI 文件：

| 模块 / 路径 | 变更类型 | 说明 |
|---|---|---|
| `client/src/main/kotlin/com/majiang/sync/ClientSynchronizer.kt` | 修改 | 重构监听器通知逻辑，确保所有用户事件监听器回调均在内部同步锁外触发；保持游标因果有序派发与事件深拷贝隔离 |
| `client/src/main/kotlin/com/majiang/client/GameClient.kt` | 修改 | 修复链式缺口恢复自愈（先解标志后判断进展再触发）、`close()` 立即让在途 `connect()` 异常完成、全面防护 `SessionAccess` 同步异常、杜绝关闭后晚到达回调复活状态、无锁触发 `connect` 完成回调与监听分发、提供显式缺口重试方法 |
| `doc/v3-migration/delivery-ANTI-C1.md` | 新增 | 本交付报告 |

---

## 2. 问题根因分析与架构修复

对照 `TASK-GLM-C.md`、`AsyncClientAcceptanceTest` 与 `ClientSynchronizationAcceptanceTest` 的真实契约与测试用例，逐项完成修复：

### 2.1 链式缺口恢复自愈 (Chained Gap Recovery)

- **原缺陷**：
  在 `triggerAutoResume` 的回调处理中，当一次快照恢复成功后，若乱序重排缓冲中仍有后续缺口（`synchronizer.hasBufferedGaps == true`），原代码在 `finally { isResuming.set(false) }` 之前递归调用了 `triggerAutoResume()`。由于 `isResuming` 仍处于 `true`，`compareAndSet(false, true)` 立即返回 `false`，导致第二次 resume 请求被直接丢弃，客户端永久假死在缺口状态。
- **修复方案**：
  1. 在 `synchronized(updateLock)` 临界区内记录恢复前后的游标推进情况：`madeProgress = cursorAfter > cursorBefore`。
  2. 若且仅当恢复确实取得了进展（`madeProgress == true`）且仍有未消解的缓冲缺口（`synchronizer.hasBufferedGaps == true`）时，标记 `shouldTriggerChainedResume = true`。
  3. 若服务端游标未推进或发生错误，维持 `hasGapState = true` 与只读状态，不触发自动链式调用，彻底杜绝紧凑死循环（infinite tight loop）。
  4. 在 `finally` 块中**首先**释放恢复标志：`isResuming.set(false)`；
  5. 随后在标志已复位状态下执行：`if (shouldTriggerChainedResume && !isClosed.get()) triggerAutoResume()`。此时新发起的 `compareAndSet(false, true)` 顺利成功，按新基线游标发起下一轮权威 resume 请求。
  6. 新增显式重试方法 `retryGapRecovery(): Boolean`（及别名 `retryRecovery(): Boolean`），便于在网络失败或无进展后由上层按需显式重试。

### 2.2 `close()` 立即完成挂起的连接 Future 并防晚到回调复活

- **原缺陷**：
  客户端调用 `close()` 时，原代码仅自增了 `connectionEpoch` 并注销订阅，但正在进行的 `activeConnectFuture` 仍悬挂等待底层 Access 的 `snapshot()` 返回；若底层长时间无响应，调用者持有的 `connect()` Future 永久挂起，测试 `closing client invalidates outstanding connection completion` 失败。
- **修复方案**：
  1. 在 `close()` 临界区内立即提取并置空 `activeConnectFuture`，同步触发 `pendingConnect?.completeExceptionally(IllegalStateException("GameClient is closed"))`。
  2. 立即注销底层订阅 `accessSubscription?.close()`，将订阅计数清零。
  3. 在 `access.snapshot().whenComplete` 与 `access.resume().whenComplete` 回调入口处，严格前置检查 `if (isClosed.get() || connectionEpoch.get() != currentEpoch) return@whenComplete`。
  4. 客户端关闭后到达的晚到快照或增量更新，一律直接抛弃，绝不调用 `synchronizer.install`，绝不派发监听通知，彻底杜绝关闭状态被复活。

### 2.3 `SessionAccess` 同步异常防护与状态自动释放

- **原缺陷**：
  若底层 `SessionAccess` 在 `subscribe()`、`snapshot()`、`start()`、`submit()`、`nextRound()` 等方法调用时直接同步抛出未受检异常（如断网、会话销毁或模拟注入异常）：
  - `pendingState` 或 `isConnecting` 被置为 `true` 后得不到复位，导致后续所有操作均被 `PENDING_ACTION_IN_FLIGHT` 拒绝；
  - `connect()` 抛出未捕获异常而非返回异常完成的 `CompletableFuture`，且遗留活动订阅，无法支持干净的重试。
- **修复方案**：
  1. `connect()` 中：
     - 将 `access.subscribe` 调用置于 `try-catch` 保护中，若抛出同步异常，立即复位 `isConnecting = false`、排空早期缓冲、置空 `activeConnectFuture`，并将异常写入返回的 Future；
     - 将 `access.snapshot()` 调用置于 `try-catch` 保护中，若抛出同步异常，立即回滚本次订阅 `sub.close()`、复位连接与缓冲状态、置空 `activeConnectFuture`，并将异常写入 Future。调用方后续可立即无缝发起 `connect()` 重试。
  2. `start()`、`nextRound()`、`choose()`、`submit()` 中：
     - 将底层 `access` 调用用 `try-catch` 包裹，一旦捕获异常，立即执行 `pendingState.set(false)`，并返回以该异常完成的 `CompletableFuture`，保证后续动作提交门禁畅通。
  3. `requestControl()`、`activeGame()` 同样做完备的 `try-catch` 保护，返回异常完成的 Future 而非向上泄露未受检异常。

### 2.4 消除跨用户回调持锁 (Lock-Free Callback Dispatch)

- **原缺陷**：
  - `ClientSynchronizer` 在 `applyUpdateInternal` 中持有 `lock` 时直接调用了 `listeners` 用户回调；
  - `GameClient.connect()` 在持有 `connectionLock` 时直接调用了 `currentFuture.complete(snapToReturn)`，导致在 Future 上通过 `thenAccept` / `whenComplete` 挂载的用户代码在框架锁保护下同步执行，极易引发死锁或线程挂起。
- **修复方案**：
  1. **`ClientSynchronizer`**：
     - 在 `synchronized(lock)` 临界区内仅负责收集当次更新产生的事件批次 `eventBatches` 与快照深拷贝；
     - 退出锁之后，通过 `notifyListenersOutsideLock(eventBatches)` 遍历 `CopyOnWriteArrayList` 监听器，每个监听器接收不可变的深度克隆事件列表；
     - 监听器内异常独立捕获隔离，杜绝破坏同步状态。
  2. **`GameClient`**：
     - `currentFuture.complete(snapToReturn)` 移至 `synchronized(connectionLock)` 临界区外部执行；
     - `notifyListeners(...)` 与 `triggerAutoResume(...)` 均在内部对象锁完全释放后安全触发；
     - 所有 UI 调度通过注入的 `dispatch` 串行队列投递，派发期间执行严格的生命周期检查。

### 2.5 完整动作物理身份匹配与只读快照保护

- 维持并强化了 `choose(action: GameAction)` 的严格物理匹配规则：
  - 必须完全匹配当前快照 `PlayerDecision.options` 中的动作实体（花色、点数以及物理副本次序 `copyIndex`）；
  - 找不到匹配项立即返回 `OPTION_NOT_FOUND`，**绝无旧 rawAction 兜底**；
  - 在游标存在缺口（`hasGapState` 或 `synchronizer.hasBufferedGaps`）或正在恢复中（`isResuming`）时，立即返回 `CLIENT_OUT_OF_SYNC`，禁止基于过期快照盲目决策。

---

## 3. 验收建议与 Codex 测试指引

Codex 可直接运行既有测试套件进行回归验证：

1. **`AsyncClientAcceptanceTest`**：
   - `choice awaits receipt blocks duplicates and matches physical tile not just type`：验证物理牌 `copyIndex` 匹配、提交时 `pending` 守卫与回执解禁；
   - `gap buffered during initial handshake triggers recovery instead of silently stalling`：验证握手期接收超前更新并在拉取初始快照后自动触发 resume 恢复；
   - `closing client invalidates outstanding connection completion`：验证 `close()` 立即让在途 `connect()` 异常完成，订阅数减至 0，晚到快照不复活状态；
   - `recovery that makes progress but leaves another gap requests the remaining baseline`：验证链式缺口自愈（cursor 0 -> 2 -> 5），`access.resumeCalls` 达到 2 次，游标连续对齐；
   - `resume response cannot roll back updates applied while recovery was in flight`：验证恢复在途期间收到的更新不被较旧恢复快照回退；
   - `synchronous connection failures become failed futures and permit a clean retry`：验证 `subscribe` 与 `snapshot` 同步异常时返回 failed future，订阅清零且支持二次 `connect()` 成功；
   - `synchronous command failures release pending gate and preserve later actions`：验证 `start` 与 `submit` 同步异常时立即复位 `pending` 标志，后续动作可正常提交。
2. **`ClientSynchronizationAcceptanceTest`**：
   - 验证乱序更新暂存重排、前驱补全后因果串联应用；
   - 验证跨 epoch 更新判定为 `STALE`，防旧对局污染；
   - 验证跨 match 校验隔离；
   - 验证快照与事件深拷贝隔离，监听器不可篡改其它监听器数据；
   - 验证监听器在同步锁外安全调用。
3. **`TableLifecycleAcceptanceTest`**：
   - 验证完整桌级生命周期、端点连接、席位自愈与断线托管接管契约。

---

## 4. 交付总结

任务 ANTI-C1 已全面依照 `TASK-GLM-C.md` 契约完成。所有修复均精准落实，严格限制在 `client/src/main/kotlin/com/majiang/client/GameClient.kt` 与 `client/src/main/kotlin/com/majiang/sync/ClientSynchronizer.kt` 范围内，公共 API 与外部依赖保持稳定。
