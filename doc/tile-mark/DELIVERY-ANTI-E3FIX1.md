# E3FIX1 交付文档：关闭通知回调死锁与权威清理顺序修复

- **任务编号**: E3FIX1 (TASK-ANTI-E3FIX1)
- **执行角色**: Anti (代码实现)
- **审查角色**: Codex (独立检视与验收)
- **修复模块**: `engine`
- **交付日期**: 2026-10-11

---

## 1. 缺陷根因与分析

### 1.1 读锁升级写锁死锁
- 在 E3 实现中，`ApplicationCoordinator.close()` 依赖 `rwLock.writeLock().withLock` 阻塞等待写锁。
- 动作（如 `submitTileMark`、`submitChoice` 等）提交成功后，在 `finally` 块中执行降级操作：先持有 `readLock` 再释放 `writeLock`，并在持有 `readLock` 的状态下调用 `broadcastEvents(allEvents)` 发布快照隔离通知。
- 在 `broadcastEvents` 执行期间，`currentExecutingThread` 已在降级前置为空。
- 当事件订阅者在通知回调中触发 `TableAuthority.close() -> ApplicationCoordinator.close()` 时：
  1. 调用线程即为持有 `readLock` 的执行线程；
  2. 原 `currentExecutingThread.get() == Thread.currentThread()` 无法捕获该状态；
  3. 线程尝试在持有自身 `readLock` 的情况下请求 `writeLock`。JVM `ReentrantReadWriteLock` 严禁锁升级，导致线程无限期阻塞等待自身释放读锁，产生真实死锁。

### 1.2 关闭意图与清理顺序
- 无法通过简单的 `if (...) return` 忽略关闭调用，否则会破坏关闭意图，导致 `session.lifecycle != CLOSED` 且标记组无法按规清空。
- 不能采用后台异步线程写入 `session`，否则将破坏单桌串行写守卫。
- 在回调中执行关闭后，`TableAuthority.submitTileMark` 等外层方法从协调器返回时，原有逻辑会无条件执行 `decisionClock.rekey`、`storedReceipts[receiptKey] = ...`，导致在 `authority.close()` 已清空回执与时钟后，又重新向已重置的时钟与回执表插入残留数据。

---

## 2. 修复方案实现

### 2.1 ApplicationCoordinator：事务兼容的延迟安全关闭机制
1. **状态标识与锁检测**:
   - 增加 `private val pendingClose = AtomicBoolean(false)`。
   - `close()` 入口通过原生的 `rwLock.readHoldCount > 0` 识别当前线程是否正持有读锁（广播执行中）。
   - 若持有读锁，原子设置 `pendingClose.set(true)` 并立即安全返回，杜绝锁升级死锁。
   - 若当前线程已持有写锁（`rwLock.isWriteLockedByCurrentThread`），直接重入执行 `performCloseInternal()`。
   - 若未持锁（外部普通调用），则在 `rwLock.writeLock().withLock` 下执行 `performCloseInternal()`。
2. **延迟原子执行与锁收尾**:
   - 新增 `finalizeActionExecution()` 与 `finalizeActionExecutionWithoutReadLock()` 统一生命周期管理函数。
   - 在 `broadcastEvents` 退出后，首先释放 `rwLock.readLock().unlock()`，确保当前线程读锁计数归零。
   - 在释放读锁后、清除 `isProcessingAction` 之前，原子检查 `pendingClose.compareAndSet(true, false)`。若命中延迟关闭，立即在 `rwLock.writeLock().withLock` 下执行 `session.prepare(ApprovedSessionStep.Close)` 与 `session.install(change)`。
   - `isProcessingAction` 原子守卫保持为 `true` 直至关闭完成，确保没有任何外部并发动作可在此期间插队。
   - 关闭完成后释放守卫，施术动作正常返回 `ActionResult.Accepted`，确保原子提交与关闭清空全部生效。
3. **全发布入口覆盖**:
   - 统一修补所有 9 个发布事件的动作入口：
     - `setSeatController`
     - `submitChoice`
     - `submitPlayerAction`
     - `handleAction`
     - `nextRound`
     - `advanceAutomaticActions`
     - `applyCheatLedgerEntry`
     - `submitTileMark`
     - `installPreparedWall`
   - 以及 `initializeMatch` 与 `updateLobbyConfig` 的无读锁退出收尾，避免遗留相同的锁升级死锁缺陷。

### 2.2 TableAuthority：关闭状态追踪与回执/时钟清理防回写
1. **权威级关闭标识**:
   - 增加 `@Volatile private var isClosed: Boolean = false`，并在 `@Synchronized override fun close()` 中原子置位。
2. **成功分支安全清理顺序**:
   - 在 `submitTileMark`、`submitChoice`、`submitAiChoice`、`applyCheatLedgerEntry`、`start`、`nextRound` 中：
     - 动作执行成功后，检查 `if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED)`。
     - 若已关闭，跳过 `decisionClock.rekey`、跳过 `decisionClock.complete`、跳过写入 `storedReceipts`、跳过写入结算事件；
     - 依然向调用者返回已提交的合法 `MutationResult`（包含 `APPLIED` 回执）。
3. **入口与自动推进防死灰复燃**:
   - 在所有提交入口 (`submitTileMark`、`submitChoice`、`submitShufflePlan`、`submitAiChoice`、`applyCheatLedgerEntry`) 的起始位置，校验 `if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED)` 并直接返回 `REJECTED`，不污染 `storedReceipts`。
   - 在 `checkTimeout`、`checkWaitingDealerChoice`、`checkAutoNextRound`、`syncDecisionsAndClocks`、`isPreparationActive` 中增加关闭跳过，确保关闭后不重新创建时钟或调度动作。

---

## 3. 修改文件清单

| 文件路径 | 修改内容概要 |
|---|---|
| [`engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt) | 引入 `pendingClose`、原生读写锁状态检测安全 `close()`、`finalizeActionExecution` 延迟写锁关闭机制，统一所有 9 个广播入口的收尾释放与关闭逻辑 |
| [`engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt) | 增加 `isClosed` 标记与 `@Synchronized close()`，成功分支校验关闭状态防止 rekey/回执回写，提交与推进入口增加关闭防御 |

---

## 4. 验证与测试结果

### 4.1 定向落汗测试 (37 项全通过)
执行指令：
```powershell
.\gradlew :engine:test --tests "com.majiang.cheat.mark.*" --console=plain
```
结果：
- `TileMarkCloseCallbackTest`: **1/1 PASSED** (原死锁回归测试通过，施术提交返回 `APPLIED`，会话置为 `CLOSED`，标记组清空，再次 close 安全)
- `TileMarkAuthorityContractTest`: **16/16 PASSED**
- `TileMarkModelContractTest`: **9/9 PASSED**
- `TileMarkStateContractTest`: **10/10 PASSED**
- `TileMarkRequestIsolationTest`: **1/1 PASSED**
- **落汗测试总计**: 5 个套件，**37 项测试全数通过**，0 失败，0 错误，0 跳过。

### 4.2 全量 Engine 测试 (377 项全通过)
执行指令：
```powershell
.\gradlew :engine:test --console=plain
```
结果：
- **测试套件总数**: 53
- **测试总数**: 377
- **失败 (Failures)**: 0
- **错误 (Errors)**: 0
- **跳过 (Skipped)**: 0
- 既有 376 项历史用例 + 1 项新增 `TileMarkCloseCallbackTest` 全部通过。

---

## 5. 红线与交付规范检查

- [x] 未修改任何测试代码（测试全由 Codex 维护）。
- [x] 未跳过或删除任何测试。
- [x] 未修改 engine 之外的其他生产模块。
- [x] 未执行 Git 提交（保持工作区干净未提交）。
- [x] 保留原 E3 交付历史记录，新增独立 E3FIX1 交付文件。
- [x] 完成 `DELIVERY-ANTI-E3FIX1.md` 与 `CALLBACK-ANTI-E3FIX1.json`。
