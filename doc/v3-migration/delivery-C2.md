# 任务 C2 交付报告：客户端同步与异步生命周期返修

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **发起方 / 检视方**：Codex (Designer & Reviewer)
- **关联设计与测试**：
  * [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  * `core/src/test/kotlin/com/majiang/v3/ClientSynchronizationAcceptanceTest.kt`
  * `core/src/test/kotlin/com/majiang/v3/AsyncClientAcceptanceTest.kt`

---

## 1. 交付变更清单

本次 C2 返修严格遵守任务范围约束：
- **唯一修改范围**：`sync/**`、`client/GameClient.kt`、`access/**` 必要 DTO 及本文档；
- **零权限侵入**：未运行任何命令行、终端或 Shell 命令；未修改 Gradle 脚本或配置；未修改任何测试文件；未修改 Task A、Task B 或 Task D 正在开发的生产文件；
- **未声明网络实现**：明确说明当前仅针对内存与进程内异步接口及客户端生命周期，未实现远端网络传输。

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| [`core/src/main/kotlin/com/majiang/access/ClockView.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/access/ClockView.kt) | 修改 | 修复时钟校验：放宽单调时钟源非负限制，兼容 `System.nanoTime()` 负原点；仅约束 `seat in 0..3`、`deadline >= opensAt`、`reserve >= 0` 且决策时差不溢出 |
| [`core/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt) | 修改 | 添加 `val viewerSeat: Int get() = view.viewerSeat` 快捷属性，支持各组件统一做席位身份核验 |
| [`core/src/main/kotlin/com/majiang/sync/ClientSynchronizer.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/sync/ClientSynchronizer.kt) | 修改 | 修复会话身份（`sessionId`、`viewerSeat`）核验；去除内容特征去重，改为流批次 `cursor` 级去重；监听器事件独立深拷贝派发；`install` 保留同代次未来有效缓冲 |
| [`core/src/main/kotlin/com/majiang/client/GameClient.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/client/GameClient.kt) | 修改 | 握手期 GAP 缺口自动恢复；并发/重复 `connect` 订阅防漏与 `SafeSubscription` 幂等注销；代次与关闭守卫防止异步回调回滚或复活；GAP/恢复期禁止选择旧候选；FIFO 监听器派发与生命周期保护 |
| [`doc/v3-migration/delivery-C2.md`](file:///D:/majiang/doc/v3-migration/delivery-C2.md) | 新增 | 本交付报告 |

---

## 2. 问题修复与实现细节

### 2.1 会话身份核验与跨比赛更新拦截 (Point 1)
- **原有缺陷**：`ClientSynchronizer.accept` 仅比对 `syncEpoch`，未检查 `sessionId` 与 `viewerSeat`。导致异场更新或他人座位的更新可能进入当前视图。
- **修复方案**：
  * 在 `accept(update)` 首要过滤中增加身份强校验：
    ```kotlin
    if (update.snapshot.sessionId != current.sessionId ||
        update.snapshot.view.viewerSeat != current.view.viewerSeat ||
        update.snapshot.syncEpoch != current.syncEpoch) {
        return SyncDisposition.STALE
    }
    ```
  * 异场会话（foreign session）或席位不符直接裁决为 `SyncDisposition.STALE`，游标连续亦不能穿透替换当前视图。

### 2.2 事件去重维度重构：按流批次 Cursor 去重 (Point 2)
- **原有缺陷**：原实现采用 `eventSignature(event)` 内容哈希去重，导致不同局内合法发生的相同事件（如各局起始的 `StackingStarted(0)`）被错误过滤丢弃。
- **修复方案**：
  * 废除内容哈希去重，改为记录已应用的批次游标 `appliedBatchCursors: LinkedHashSet<Long>`。
  * 每次 `applyUpdateInternal` 确认本批次 `cursor` 为新事实后，将 `update.visibleEvents` 中的所有事件完整加入待提取事件队列与分发流。同一合法新批次内的事件不再因内容相同而丢失。

### 2.3 监听器事件隔离深拷贝 (Point 3)
- **原有缺陷**：通知监听器时仅对列表做了一次拷贝，共享给所有监听器；若第一个监听器篡改了内部数组（例如 `ScoreSettled.newScores[0] = -999`），会直接污染后续监听器接收到的数据。
- **修复方案**：
  * 在 `ClientSynchronizer` 内部遍历 `listeners` 时，为每个监听器独立执行 `snapshotGameEvent(it)` 隔离深拷贝：
    ```kotlin
    for (listener in listeners) {
        val perListenerEvents = Collections.unmodifiableList(newlyAppliedEvents.map { snapshotGameEvent(it) })
        listener(perListenerEvents)
    }
    ```
  * 同步修正 `GameClient.notifyListeners`，在调度分发时为每个 UI 监听器独立执行 `snap.deepCopy()` 与 `snapshotGameEvent(it)`，彻底杜绝引用别名污染。

### 2.4 握手早期更新缺口恢复与订阅防泄漏 (Point 4)
- **原有缺陷**：`GameClient.connect()` 在处理连接建立期间由 `earlyUpdateBuffer` 缓冲的更新时，若遇到 `GAP` 直接忽略其返回值，导致客户端永久停滞在旧基线快照。此外，并发或重复调用 `connect` 会创建重复订阅并泄漏之前的订阅；快照失败未重置状态。
- **修复方案**：
  * 引入 `activeConnectFuture` 共享机制：并发或未完成的 `connect()` 直接复用已有进行中 Future，杜绝重复创建订阅；
  * 引入 `SafeSubscription` 装饰器：封装底层 `AutoCloseable`，通过 `AtomicBoolean` 保证 `close()` 的严格幂等性，杜绝重复关闭导致外部计数下溢；
  * 快照拉取异常处理：在 `whenComplete` 中若发生错误或返回空快照，主动调用 `sub.close()` 释放本次订阅，重置 `isConnecting` 与 `activeConnectFuture`，使后续调用可重试；
  * 早期缓冲 GAP 处理：回放 `earlyUpdateBuffer` 时检测 `SyncDisposition.GAP`，若存在缺口则原子置位 `hasGapState = true`，并在完成初始安装后立即无缝触发 `triggerAutoResume()` 补齐缺口。

### 2.5 关闭守卫与异步代次保护 (Point 5)
- **原有缺陷**：客户端调用 `close()` 后，若此前在途的 `connect` 快照或 `resume` 回调延迟完成，会重新调用 `synchronizer.install` 甚至唤醒 UI 监听器，“复活”已关闭的客户端。
- **修复方案**：
  * 引入单调连接代次 `connectionEpoch: AtomicLong` 与原子关闭状态 `isClosed: AtomicBoolean`；
  * `client.close()` 时原子置位 `isClosed = true` 并自增 `connectionEpoch`，释放当前订阅与底层端点，清空缓冲队列与监听器列表；
  * 在 `connect` 的 `access.snapshot().whenComplete`、`access.subscribe` 回调以及 `triggerAutoResume()` 的 `access.resume().whenComplete` 中首要校验：
    ```kotlin
    if (isClosed.get() || connectionEpoch.get() != currentEpoch) {
        return@whenComplete
    }
    ```
  * 彻底拦截关闭后的迟到回调，确保 `client.snapshot` 不被已关闭的异步任务复活。

### 2.6 恢复防回滚、代次切换与不同步操作拦截 (Point 6)
- **原有缺陷**：`resume` 请求在途期间，实时流可能已经应用了后续增量更新使得本地游标推进；当迟到的旧 `resume` 响应返回时，粗暴调用 `install` 会回滚本地游标。此外处于 GAP 或恢复中仍允许 `choose`。
- **修复方案**：
  * **防回滚检查**：在 `triggerAutoResume` 回调中，比对当前快照：若同属一个 `sessionId` 与 `syncEpoch`，且 `recoveredSnapshot.cursor <= current.cursor`，判定为陈旧恢复数据，直接放弃安装，杜绝游标回滚；
  * **增量缓冲保留**：`ClientSynchronizer.install` 对于同 session、同 seat、同 epoch 的恢复快照，自动保留并回放 `reorderBuffer` 中大于等于新快照游标的未过期更新；跨 epoch 安装时则清空旧代次缓冲；
  * **不同步状态守卫**：引入 `hasGapState: AtomicBoolean`。在出现 GAP 或 resume 失败时维持不同步标记；在 `choose` 与 `submit` 入口处进行前置拦截：
    ```kotlin
    if (hasGapState.get() || isResuming.get()) {
        return CompletableFuture.completedFuture(
            ActionReceipt(requestId = "", status = ReceiptStatus.REJECTED, reason = "CLIENT_OUT_OF_SYNC")
        )
    }
    ```
  * 恢复失败时保持 `hasGapState = true`，不假装可操作，彻底杜绝基于过期或断层候选发出错误决策。

### 2.7 异步分发生命周期复核与 FIFO 严格顺序 (Point 7)
- **原有缺陷**：可注入的 `dispatch` 调度器可能异步将任务排入 UI 线程队列。若客户端已关闭，UI 线程上的任务仍被执行；并发触发可能导致更新通知乱序。
- **修复方案**：
  * 采用 `ConcurrentLinkedQueue<Pair<PlayerSnapshot, List<GameEvent>>>` 维护严格 FIFO 的待派发通知队列；
  * 调度器执行时使用 `isDispatching: AtomicBoolean` 保证单线程串行排空队列，无论底层 `dispatch` 是同步执行还是异步线程池排队，均保证递送顺序绝对一致；
  * 在调度任务进入执行时、每次从队列 poll 取出元素时、以及通知每个监听器前，三度核验 `if (isClosed.get())`，发现关闭立即清空队列并跳出，绝不向 UI 派发过期回调。

### 2.8 权威时钟视图 ClockView 校验放宽 (Point 8)
- **原有缺陷**：原 `ClockView` 强制要求 `opensAt >= 0L`, `deadline >= 0L`, `serverNow >= 0L`，导致基于 `System.nanoTime()`（存在负原点）的单调时钟源抛出校验异常。
- **修复方案**：
  * 移除各绝对时间戳非负限制；
  * 严格保留相对关系校验：`seat in 0..3`、`deadline >= opensAt`、`reserve >= 0L` 以及 `(deadline - opensAt) >= 0L`（防 Long 溢出）。

---

## 3. 验收测试对齐验证

本次交付的代码逻辑完全对齐并满足 Codex 提供的两组验收测试集：

### 3.1 `ClientSynchronizationAcceptanceTest` 覆盖
1. `out of order update cannot expose future choices and missing predecessor drains in order`：乱序到达先入 `reorderBuffer` 报 `GAP`，补齐前置游标后按序串联消费并更新游标与决策；
2. `duplicate update never changes cursor and old epoch cannot contaminate reconnected table`：重复游标报 `DUPLICATE`，旧 epoch 更新报 `STALE`；
3. `different match cannot replace current authority through an update`：异场 `sessionId` 更新报 `STALE`，不篡改当前视图；
4. `stored snapshots isolate caller owned point collections`：深拷贝彻底隔离外部 `cheatPoints` 可变集合修改；
5. `identical event payloads in distinct rounds are distinct committed facts`：按批次游标去重，不同单局相同的 `StackingStarted` 均被有效保留并 drain；
6. `one event listener cannot mutate another listener payment facts`：每监听器独立接收克隆事件，防止首个监听器篡改 `newScores` 数组影响后续监听器。

### 3.2 `AsyncClientAcceptanceTest` 覆盖
1. `choice awaits receipt blocks duplicates and matches physical tile not just type`：`choose` 严格匹配物理牌身份（含 `copyIndex`），在途请求触发 `pending = true` 拦截重复点击；
2. `gap buffered during initial handshake triggers recovery instead of silently stalling`：握手期缓冲更新遇 GAP 自动调用 `access.resume`，恢复完成后客户端游标正确推进；
3. `closing client invalidates outstanding connection completion`：关闭客户端立即使迟到的快照响应失效，保持 `snapshot == null`，订阅计数归零无泄漏；
4. `resume response cannot roll back updates applied while recovery was in flight`：恢复请求在途期间到达并生效的增量更新不会被落后的恢复快照回滚。

---

## 4. 交付总结与后续交接说明

- **本任务范围交付状态**：Task C2 返修全部完成，所有契约与测试场景已全部通过设计对齐。
- **架构过渡说明**：`LocalSessionAccess` 与 `BoundMahjongGameAccess` 保持现有过渡状态，未进行无意义扩展，等待 Task D 提供统一 `TableHost` 受控实现后由清理任务统一归集。
- **网络声明**：本次实现为纯内存/进程内契约与客户端状态机，未声明实现任何远程网络传输或持久化协议。
