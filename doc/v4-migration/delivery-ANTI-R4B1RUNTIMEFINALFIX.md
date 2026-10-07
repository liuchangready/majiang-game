# 交付物: R4B1RUNTIMEFINALFIX (Runtime 收尾测试返修与多线程保序)

- 开发者: Antigravity (Anti)
- 任务代码: `R4B1RUNTIMEFINALFIX`
- 日期: 2026-10-06

---

## 1. 修改文件清单

1. `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`
   - 为 `RoomObserver` 引入独立的 `listenerLock` 对象锁。
   - 重构 `notifyRoomUpdateListeners`：在 `observer.listenerLock` 保护下严格完成最新代次校验、过时版本丢弃、已交付版本递增并执行用户回调；`roomLock` 仅在检测过期（`isStale`）时做纳秒级读取，绝不在持有 `roomLock` 的情况下执行用户回调，杜绝死锁与锁嵌套。
   - 保证多线程并发 `dispatch` 场景下，对同一 observer 的回调严格串行化且单调保序，杜绝线程交错暂停导致的先交付 v3 后交付 v2 的乱序问题。

2. `client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`
   - 修复 `testDelayedRoomQueryCannotPoisonReplacementRoomCache`：为旧房间在途请求与新房间查询提供独立的 mock 响应（设置 v1 基线），避免复用已完成的 v99 Future 造成测试夹具误判；将 `.join()` 替换为有界 `.get(2, TimeUnit.SECONDS)`；保留旧 Future 异常完成断言和新房间缓存不被旧回复污染的断言。
   - 新增真实多线程交错回归测试 `testRoomUpdateObserverInterleavedThreadExecutionNeverDeliversStaleAfterNew`：使用独立线程与 `CountDownLatch` 明确模拟 v3 派发先于 v2 派发完成回调的竞态场景，验证过期的 v2 被严格丢弃，绝不在 v3 之后交付。
   - 新增高并发多线程回归测试 `testRoomUpdateObserverHighConcurrencyDispatchNeverDeliversBackwards`：在 4 线程池并发执行 `dispatch` 且快速连续推送多个版本更新的情况下，验证 observer 收到的版本序列严格单调递增，无乱序与倒退。

---

## 2. 针对 FEEDBACK-R4B1RUNTIMEFINALFIX.md 的解决说明

1. **测试夹具复用导致 `expected 2 actual 99` 失败**：
   - 原因：原测试中 `fake.customRoomInfoFuture` 一直指向 `delayedQueryReply`。旧请求完成 v99 后，对新房间调用 `session.getRoomInfo()` 时，`fake` 返回了同一个已完成 v99 的 Future，导致合法的新房间查询直接读到了夹具中预设的 99。
   - 修复：在新房间查询前配置独立的 mock 响应（v1 基线），验证延迟完成的旧请求（v99）被正确隔离，不污染新房间缓存，且旧 Future 因房间代次变更以异常结束。

2. **并发 `dispatch` 导致 observer 回调顺序 3, 2 的源码审查问题**：
   - 原因：原实现虽在锁内更新了 `lastDeliveredVersion`，但用户回调在锁外执行；若多线程并发派发，先更新 v2 的线程在执行回调前被暂停，后更新 v3 的线程先执行了回调，导致实际回调时序变为 3 然后 2。
   - 修复：在 `RoomObserver` 上引入串行派发锁 `listenerLock`，在锁内原子性地完成版本递增与用户回调执行，且全程不持有 `roomLock`；不仅彻底杜绝了 3, 2 乱序，还避免了在 session 核心锁内执行用户代码，不产生线程无界增长。

---

## 3. 未解决事项 (Unresolved Items)

- 无。本任务范围内的所有问题均已修复。

---

## 4. 测试执行声明 (Test Execution Statement)

- **严格遵循指令约束**：Anti 本次**未运行**任何终端命令、gradle 构建、单元测试或评审测试套件（严格遵从无 shell / run_command 约束）。
- 所有编译与构建测试由 Codex 独立执行并验收。
