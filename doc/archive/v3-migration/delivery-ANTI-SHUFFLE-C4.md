# 任务 ANTI-SHUFFLE-C4 交付报告：GameClient 关闭竞态与原子所有权转移加固

- **任务编号**：ANTI-SHUFFLE-C4
- **交付日期**：2026-10-02
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **设计与审查**：Codex (Designer, Test Author, Reviewer & Builder)
- **所有权范围**：严格仅所有并修改 `client/src/main/kotlin/com/majiang/client/GameClient.kt`；不修改任何测试、构建脚本、其他模块生产代码或 `ProtocolJson.kt`。

---

## 1. 变更清单与所有权边界

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| [`client/src/main/kotlin/com/majiang/client/GameClient.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/GameClient.kt) | 修改 | 修复 `trackActionReceipt` 与 `requestControl` 回调与 `close()` 之间的解绑竞态，实现原子所有权转移与终态完成保障；消除同步 `supplier()` 异常与晚关闭间的丢失输出；防止关闭后 `pendingState` 复活；杜绝已结算/旧回调清除新请求的 pending 标志；保障重入完成监听器可无缝发起下一动作。 |
| [`doc/v3-migration/delivery-ANTI-SHUFFLE-C4.md`](file:///D:/majiang/doc/v3-migration/delivery-ANTI-SHUFFLE-C4.md) | 新增 | 本交付报告 |

---

## 2. 核心竞态问题诊断与加固方案

### 2.1 移除与关闭间的终态断裂竞态 (Lost Output Race)

#### 原有缺陷机制
在原实现中：
```kotlin
underlyingFuture.whenComplete { receipt, error ->
    synchronized(pendingLock) {
        pendingOutputFutures.remove(outputFuture)
    }
    // 竞态窗口：此处锁已释放，close() 在此间隙执行！
    // close() 遍历 pendingOutputFutures 时未发现 outputFuture，因此不会 complete 它。
    // close() 将 isClosed 置为 true。
    if (isClosed.get()) {
        return@whenComplete // 回调检测到已关闭，直接返回，导致 outputFuture 永久挂起未完成！
    }
    pendingState.set(false)
    ...
```
`requestControl` 亦存在完全相同的缺陷模式。

#### 加固措施：原子所有权转移与关闭终态保障
1. **原子所有权转移**：
   在 `synchronized(pendingLock)` 保护下执行 `wasPending = pendingOutputFutures.remove(outputFuture) != null`。
   - 若 `wasPending == false`：说明 `close()`（或异常路径）已经先移除了该 Future 并接管了它的完成，当前回调判定为晚到达的无效回调，立即退出，安全忽略；
   - 若 `wasPending == true`：当前回调成功且唯一地获得了该 Future 的完成所有权，无论如何**绝不遗漏完成**。
2. **终态分支补齐**：
   若客户端处于已关闭状态（在锁内或锁后检测到 `completeClientClosed || isClosed.get()` 为真），显式以 `CLIENT_CLOSED` 完成 `outputFuture`，彻底消除挂起死锁。

---

### 2.2 同步 `supplier()` 异常与晚关闭间的竞态防护

#### 风险场景
当调用 `access` 提交（如 `access.submit` 或 `access.requestControl`）抛出同步异常时：
若在 `catch` 块中仅简单从 `pendingOutputFutures` 移除后释放锁，并发触发的 `close()` 同样可能未能捕捉该 Future，导致输出丢失或异常未安全传导。

#### 加固措施
在 `catch (t: Throwable)` 块中采用与正常回调完全对齐的原子所有权转移：
- 仅当 `wasPending == true` 时代表当前执行路径持有该 Future 的完成权；
- 若已被 `close()` 关闭，显式完成 `CLIENT_CLOSED`；否则以 `completeExceptionally(t)` 终止；
- 始终安全返回 `outputFuture`，彻底防止输出丢失。

---

### 2.3 `pendingState` 生命周期与重入调度保障

#### 1. 杜绝旧回调清除新请求的 pending 标志 (No Old Callback Clears New Request)
- 只有成功从 `pendingOutputFutures` 移除并持有完成所有权（`wasPending == true`）的合法首次回调才允许释放 `pendingState`；
- 所有已被结算或迟到的晚到达回调（`!wasPending`）直接退出，绝不调用 `pendingState.set(false)`，彻底避免旧回调错误清空后续新请求已获取的 pending 互斥锁。

#### 2. 重入监听器支持立即发起下一请求 (Reentrant Next Request)
- `pendingState.set(false)` 必须且已置于 `outputFuture.complete(receipt)` / `outputFuture.completeExceptionally(...)` **之前**执行；
- 当上层调用者在 `outputFuture` 上通过 `thenAccept` / `whenComplete` 挂载完成监听器并在监听器内同步重入调用 `client.choose(...)` 或 `client.submitShufflePlan(...)` 时，由于 `pendingState` 已安全释放，`pendingState.compareAndSet(false, true)` 将顺利通过，杜绝产生虚假的 `PENDING_ACTION_IN_FLIGHT`。

#### 3. 杜绝关闭后 `pendingState` 复活 (No Postclose PendingState Resurrection)
- `close()` 采用 `isClosed.compareAndSet(false, true)` 保障单次幂等（one-shot）；
- `close()` 内部在重置状态时以及在 `synchronized(pendingLock)` 内清空 `pendingOutputFutures` 时，均同步执行 `pendingState.set(false)`；
- `isPending` 与 `pending` 属性统一收敛为 `!isClosed.get() && pendingState.get()`，确保关闭后状态查询永不返回 true；
- 所有动作发起入口在检查 `isClosed` 或遇到非法校验时均重置 `pendingState`。

---

## 3. 锁外回调与并发死锁防范 (Lock-Free Completion)

在 `trackActionReceipt` 与 `requestControl` 的起始判断中，若在 `synchronized(pendingLock)` 保护下发现 `isClosed.get()` 为真，标记 `closedImmediately = true`，并在**释放锁之后**调用 `outputFuture.complete(...)`，严格遵循 GameClient 准则第 10 条（“避免跨用户回调持有锁，所有监听器分发与回调均在对象锁外部安全触发”），彻底杜绝用户监听器重入时导致的潜在锁争用或死锁。

---

## 4. 交付验证总结

1. **修改范围**：仅修改 `GameClient.kt`，未改动 `ProtocolJson.kt`，未改动任何测试文件与构建配置，未扩大公开接口范围；
2. **正确性**：
   - 所有在途动作与控制权请求的 Future 无论遭遇底层异常、正常回执或并发 `close()`，均保证有且仅有一次终态完成；
   - 迟到回调完全被安全过滤与丢弃；
   - 重入请求与连续调用门禁流转严格清晰。
