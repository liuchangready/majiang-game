# 交付报告：R4B1RUNTIMEFINAL 客户端房间缓存提交竞态返修

**任务编号**：`R4B1RUNTIMEFINAL`  
**开发者**：Antigravity (Anti)  
**日期**：2026-10-06  

---

## 1. 修改文件清单

1. [`client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt)
2. [`client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt)

*(严格遵守独占文件范围，未修改 client transport/udp、protocol、server、reviewer/acceptance 测试目录、其他 client.api 文件或构建配置)*

---

## 2. 问题根因分析与修复落地

### 2.1 根因分析
在旧实现中，`handleInboundRoomUpdate` 仅在方法入口处对 `epoch`、`roomEpoch`、`currentBinding` 进行了前置检查。随后在获取 `roomLock` 写入缓存时，并未再次校验代次与绑定的有效性。
当旧回调通过前置检查后在 `roomLock` 前被调度挂起，而主线程完成了 `leaveRoom()` 与新房 `createRoom()`，旧回调随后获得 `roomLock`，将旧房间数据（如 v90）写入了新房间的缓存 `cachedRoomVersion`，从而污染了新房间的缓存视图，导致新房间版本被覆盖或后续低版本合法更新被丢弃。

### 2.2 核心修复实现

1. **缓存提交线性化点双重代次校验 (`handleInboundRoomUpdate`)**：
   - 在 `synchronized(roomLock)` 临界区内部，再次严格重验当前状态：
     - `closed || isLeavingRoom.get()`；
     - `epoch != currentEpoch.get()`（连接代次变更）；
     - `roomEpoch != currentRoomEpoch.get()`（房间代次变更）；
     - `currentBinding == null` 或 `protoRoomInfo.roomId != currentBinding.roomId`（绑定失效或房间不符）。
   - 任一条件失效则立即返回 `false`，绝不修改 `cachedRoomVersion` 与 `cachedRoomInfo`，彻底杜绝已在途的旧回调污染新绑定缓存。

2. **慢查询响应线性化点代次对账 (`getRoomInfo()`)**：
   - 在 `synchronized(roomLock)` 内部同样执行连接代次 `epoch`、房间代次 `roomEpoch` 以及当前绑定一致性校验；
   - 若代次已失效或房间不匹配，不触碰缓存，直接返回 `null` 并使 Future 以 `IOException("Session closed or generation changed")` 异常结束；
   - 仅当且仅当属于当前有效代次时，才与缓存进行最新版本对账。

3. **派发阶段防版本回退与延迟乱序抑制 (`notifyRoomUpdateListeners`)**：
   - 保持 `observer.listener(info)` 在锁外调用，避免锁内执行用户代码或导致死锁；
   - 在进入用户监听器前，在 `synchronized(roomLock)` 中比对 `info.version < cachedRoomVersion`：若权威缓存已先于当前更新前进到更高版本（如 v3），则立即丢弃落后的旧版本（如 v2），杜绝“更新缓存 v3 后把 v2 延迟发送给 UI”的乱序反转；
   - `RoomObserver` 内部记录每个观察者的 `lastDeliveredRoomEpoch` 与 `lastDeliveredVersion`，在 `synchronized(observer)` 下保证单监听器接收的单调递增性，杜绝重复与倒退。

4. **保留反射兼容与无死锁锁顺序**：
   - 保留字段 `private val roomLock = Any()`，供 review 测试通过反射精准控制锁并发断点；
   - `syncLock` 与 `roomLock` 相互独立，全流程无嵌套加锁，保持锁顺序清晰无死锁。

---

## 3. 模块测试覆盖

在 [`ServerConnectionSessionTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt) 中补充了 4 个严格的多线程竞态与代次隔离回归测试用例：
1. `testRetiredRoomCallbackCannotAttachToNewBindingWithSameRoomId`：验证离房后重建同名房间时，旧回调被新连接代次/房间代次拦截，不污染新房缓存。
2. `testRoomCallbackAlreadyInFlightCannotPoisonReplacementCache`：通过反射锁控制与工作线程状态 `BLOCKED` 屏障，精确模拟旧回调先通过检查后被挂起，另一线程完成 `leaveRoom()` + `createRoom()` 替换房间，释放锁后旧回调恢复执行绝不污染新房缓存。
3. `testRoomNotificationDispatchPreservesVersionMonotonicity`：通过自定义队列派发模拟乱序与延迟消费，验证权威缓存更新到 v3 后，滞后的 v2 派发被自动抑制，UI 不发生版本倒退。
4. `testDelayedRoomQueryCannotPoisonReplacementRoomCache`：验证旧房间挂起的 `getRoomInfo` 慢查询在离房重建后以异常结束，且不污染新房缓存。

---

## 4. 未解决项 (Unresolved Items)

- **无**。反馈中提及的在途旧回调提交污染新房缓存、以及锁外 dispatch 版本乱序抑制问题均已完整解决。

---

## 5. 验证与测试声明

- **测试未由 Anti 运行 (Tests NOT run by Anti)**。
- 依据任务指令与 `AGENTS.md` 规则，Anti 仅使用内置文件编辑/读写工具完成业务代码与测试用例落盘，未执行任何终端/Shell 命令、Gradle 构建或自动化测试。
- 最终代码构建与集成测试验收由 Codex 统一执行。
