# 交付报告：R4B1ROOMFIX5 离房收尾与关闭认领返修

**执行人**：Claude Code (GLM)  
**架构与验收负责人**：Codex  
**日期**：2026-10-06  
**对应任务**：`doc/v4-migration/TASK-GLM-R4B1ROOMFIX5.md`（参照 `doc/v4-migration/FEEDBACK-GLM-R4B1ROOMFIX4.md`）

---

## 1. 实际改动

### 生产：`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`（唯一生产文件）

1. **关闭条件原子化（反馈 #2）**
   - 新增 `claimClosure(reason, validate?)`：在单一 `stateLock` 临界区内完成「校验 + 认领」——校验通过即同时置位 `_disconnectReason`、`closed=true`、递增 `bindingGeneration` 作废全部回调、清空 `currentRoom`，并返回锁外收尾所需的旧绑定快照 `CloseClaim`；校验失败或已关闭返回 null 直接丢弃。`validate` 仅做字段读取，不含用户回调。
   - 新增 `runCloseTeardown(claim)`：断连通知、订阅释放、`disconnectSeat` 全部在锁外执行，至多一次。
   - `handleRoomClosed(ctx)` / `handleDisplacedByNewConnection(ctx)` 改为「锁内原子校验认领 + 锁外收尾」。校验包含：非显式离房、捕获本地代次、房间引用一致、（关房）房间确已 CLOSED /（顶号）共享席位代次漂移。反馈指出的「A 复核 true → 暂停 → B leave+rebind → A 无条件 close」窗口被消除：A 的复核与关闭状态认领是同一临界区；若 B 先完成 leave 解绑（锁内 `bindingGeneration++`），A 的认领必然失败。
   - `close()` 改为走同一 `claimClosure(LOCAL_CLOSED)` 协议；`bindRoom` 在锁内 `check(!isClosed.get())` 拒绝绑定已关闭连接（`createRoom` 回收孤儿房间、`joinRoom` 释放刚占用席位后异常完成、`resumeSession` 异常完成且 token 保持有效）。绑定切换（bindRoom/解绑）与关闭认领自此共用同一同步协议，无 check/use 窗口。

2. **离房返回完成收尾的 stage（反馈 #3）**
   - `leaveRoom` 改为返回占位 `leaveFuture`，其完成时刻保证解绑、订阅释放、标志复位、终局补裁全部结束（此前返回的 `pending` 不含 `whenComplete` 收尾 stage）。
   - 离房标志归属具体在途操作：新增 `inFlightLeave`（锁保护）。并发重复 leave 直接复用在途 Future（与 reviewer `overlappingLeaveRequestsCannotInvalidateTheNextRoomBinding` 的「可复用或明确拒绝」语义一致）；收尾仅在 `inFlightLeave === leaveFuture` 时复位标志并释放占位，旧完成绝不清除新离房。
   - 执行期 validator 改为完全基于锁内快照（leaveGen/leaveSeat/leaveSeatGen），不再经 `makeValidator` 二次读取。
   - 保留确定性终局补裁：回执完成后若仍绑定原房且房间已关闭/席位代次已漂移，走原子认领关闭（APPLIED 解绑路径因本地代次已递增而自动跳过，不会误裁自身）。

3. **最小 internal 回调处理入口（反馈 #4 的测试前提）**
   - 新增 `BindingCallbackContext`（bindRoom 锁内捕获的 generation/room/seat/seatGeneration）、`dispatchGatedCallback(ctx, deliver)`、`captureCurrentCallbackContext()`，全部 Kotlin `internal`（非 public API，无产品测试后门）。生产三处订阅（内部房间订阅、UI 房间观察者、席位更新）与测试共用同一条判定路径；迟到的旧绑定回调（代次/房号不匹配）直接丢弃，绝不投递或关闭新绑定。

### 测试

4. `server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`（反馈 #1）
   - observer 用例：目标版本改为**订阅与变更之前**确定（`baselineVersion = room.version`，两次就绪翻转各 +1），删除事后回填的捕获 var（数据竞争/错过已送达通知）；新增基线 latch——先确定性等待初始基线送达再触发 setReady 变更。保留断言：首送为基线版本、严格单调递增、最终必达最新 `ready=true` 视图且版本等于权威查询。

5. `server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`（反馈 #4）
   - 原 retired-room barrier 用例**诚实重命名**为 `suspended retired-room observer does not block leave or rebind`：仅覆盖慢观察者隔离下 leave 回执不被阻塞、同连接重建开局、释放后无影响；不再声称模拟旧绑定回调。
   - 新增 `stale binding callback after rebind is dropped but real replacement still claims closure`：换房前 `captureCurrentCallbackContext()` 捕获旧绑定身份，换房后经真实内部入口执行迟到回调（`dispatchGatedCallback`/`handleRoomClosed`/`handleDisplacedByNewConnection`），断言全部丢弃、不投递、不误关新房；随后以当前上下文 + 真实 `resumeSession` 顶替触发顶号裁决，断言确定性认领关闭（`DISPLACED_BY_NEW_CONNECTION`）且新连接不受影响。全程无 `Thread.sleep`。
   - 既有用例（含 10 轮循环、replacement-before-leave、非 0 真人座位 13 张）未改动。

---

## 2. 建议验收

```powershell
.\gradlew.bat :server:test --console=plain
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewStage=R4B-review -PreviewOnly=true :server:test --tests com.majiang.review.* --tests com.majiang.v4models.* --tests com.majiang.v4runtime.* --tests com.majiang.v4authority.* --tests com.majiang.v4lobby.* --tests *V4FinalArchitectureTest.facadePublicTypesDoNotLeakDomainOrProtocolObjects --console=plain
```

预期：server 模块 90 项全绿（此前 2 失败：observer 等待已按契约重写；非 0 座位 13 张已在上一版修正）；独立 110 项中 ROOM 相关（含 20 次循环探针、guest leave 复用、overlapping leave）应全绿，UDP 与客户端 8 项仍属后续批次。

---

## 3. 未验证项与遗留风险

1. **未编译、未运行任何测试**（按约定由 Codex 实测）。Kotlin 细节风险点：`synchronized` 内 `val` 单次赋值与 inline lambda 非局部 return（上一版同款写法已通过 Codex 实测编译运行）。
2. `leaveRoom` 回执为 APPLIED 但连接在离房期间被用户/终局认领并发关闭时，回执仍为 APPLIED（操作在房间层面确实生效）；连接状态以 `closed` 为准。此为边缘语义，如 Codex 认为需改为 REJECTED 请反馈。
3. 回调门的「是否投递」判定（`dispatchGatedCallback` 外层快读）仍允许极端情况下向 UI 监听器多投递一条已在途的旧房间事件（不影响连接存活与权威状态，与上一版一致）；反馈仅要求关闭判定原子化，投递路径未收紧。
4. 并发重复 leave 采用「复用在途 Future」分支：两个调用者拿到同一回执。reviewer 用例允许该语义；如需「明确拒绝」分支请反馈。
