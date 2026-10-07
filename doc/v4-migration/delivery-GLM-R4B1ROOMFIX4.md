# 交付报告：R4B1ROOMFIX4 本地房间绑定竞态修复

**执行人**：Claude Code (GLM)  
**架构与验收负责人**：Codex  
**日期**：2026-10-06  
**对应任务**：`doc/v4-migration/TASK-GLM-R4B1ROOMFIX4.md`（参照 `doc/v4-migration/RESUME.md`、`doc/v4-migration/FEEDBACK-R4B1ROOMFIX3.md`）

---

## 1. 交付文件清单与核心修复

### 生产代码（仅 server 模块，仅 InProcessBinding）

1. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)

   SharedRoom / RoomRegistry / InProcessServer 本次**零改动**（hostSeat != 0 装配核对结论见 §3.4）。全部竞态修复收敛在绑定层：

   - **修复离房竞态（独立失败 #1、server 失败 #1 的根因）**。原代码三处回调（`internalRoomSub`、`currentSeatSub`、`BindingRoomObserver`）存在两个致命分支：
     1. `else if (!isCurrentGen && !closed) handleDisplacedByNewConnection()` 位于捕获代次/房间校验**之外**——旧房间或旧绑定的迟到回调（含 `subscribeSeat` 迟到基线、终局通知迟到派发）能把已重建新房的连接误判为被顶号并关闭；
     2. `isExplicitlyLeaving` 在 `whenComplete` 中过早复位——终局通知在复位之后、解绑完成之前送达时，自身 `leave` 造成的 `bindingGenerations[seat]++` 被当成真实顶号/房间关闭。
   - **回调双重代次门**。三处回调统一改为严格顺序：
     1. 显式离房窗口内（`isExplicitlyLeaving`，锁内读取）直接静默丢弃；
     2. **第一重门**：捕获的连接本地 `bindingGeneration` 与 `currentRoom` 房间引用，不匹配即丢弃（来自旧房间/旧绑定的回调绝不关闭新绑定）；
     3. **第二重门**：通过后才比对共享席位代次 `bindingGenerations[seat]`，漂移且经 `isCurrentBinding` 锁内复核仍为当前绑定时，才判定被新连接顶替。
     原“校验之外的兜底 else-if”全部删除。
   - **离房窗口全程覆盖 + 确定性补裁**。`isExplicitlyLeaving` 在 `leaveRoom` 入队前置位，在回执链（`thenApply` 解绑完成）之后的 `whenComplete` 才复位，覆盖整个窗口。回执完成后补裁：若仍未解绑且原房间已关闭 → `handleRoomClosed`；席位代次已漂移 → `handleDisplacedByNewConnection`。由此“离房期间被真实顶号/房间被解散”不再依赖回调时序，确定性失效。
   - **绑定状态一致性**。新增 `stateLock`：`bindRoom`（代次递增 + `currentRoom`/`capturedSeatGeneration` 原子切换，**先切换后释放旧订阅**）、`leaveRoom` 解绑、`close()` 终态、`onRoomUpdate` 快照读取与全部回调门均在锁内读写 `bindingGeneration`/`currentRoom`/`isExplicitlyLeaving`。`bindingGeneration` 补 `@Volatile`。锁内只有字段读写，无 Future 等待、无跨执行器提交，不违反非阻塞契约。
   - **保留真实顶号失效行为**：新连接 `resumeSession` 抢占后，旧连接经回调门或回执补裁两条路径之一确定性 `DISPLACED_BY_NEW_CONNECTION`；旧连接退出时 `disconnectSeat` 的共享代次校验继续保护新连接（SharedRoom 原有逻辑，未动）。
   - `BindingRoomObserver.attach` 改为捕获订阅时的 `seat`（原实现在回调期读 `binding.viewerSeat`，可能与捕获代次错位）。

### 模块测试（server 模块）

2. [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)
   - **修正测试前提（server 失败 #2）**：`replacement resume prevents previous connection from changing readiness` 补上 `hostConn.setReady(true)`（经真实 API，未放宽生产就绪门槛）；保留旧连接 `REJECTED`、新连接身份 `resumedBound2.seat == guestBound1.seat`、真实服务端游标 `snapshot.cursor == 0L` 三类断言。
   - **新增可控屏障回归** `late terminal callback from retired room cannot close rebound connection`：直接在旧 `SharedRoom` 上挂起终局通知派发线程（CountDownLatch 屏障），期间完成 leave(APPLIED) → 同连接 createRoom → start，随后释放屏障；断言连接始终存活、`currentRoom` 指向新房、无任何 onDisconnect、新房快照可用。不依赖概率时序。
   - **新增确定性顶号回归** `replacement before explicit leave deterministically invalidates stale connection`：新连接先 `resumeSession` 顶替，旧连接再显式 `leaveRoom`——任意回调时序下均得到 `REJECTED` + `closed=true` + `DISPLACED_BY_NEW_CONNECTION`（回执补裁路径的确定性验证），且新连接可继续操作。
   - **新增循环回归** `repeated leave create and start cycles keep the same connection live`：同连接 10 轮 leave→createRoom→start→14 张起手，放大残余竞态窗口（与 reviewer 20 次探针互补）。
   - **新增非 0 真人座位装配覆盖（任务项 4）** `nonzero human seat assembles host admin viewer and ai seats consistently`：`createLocalTable(humanSeat = 2)` 下断言 `binding.viewerSeat == room.hostSeat == tableHost.adminSeat == 2`；房间成员仅该座位为 HUMAN/host 且单人装配自动就绪，其余三席 COMPUTER 自动就绪；快照 `viewerSeat == 2`；`start()` 由 2 号位 APPLIED、非庄家 13 张起手（初始庄家固定 0 号位）。
   - 既有测试（含 `local host can leave and create another room on the live connection` 原断言）全部保留，未删除、未放宽。

3. [`server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt)
   - **修正观察者契约（server 失败 #3）**：`room observer receives strictly monotonic versions and eventual latest full view`——不再要求 3 个中间快照必达（与有界最新完整视图合并契约冲突），改为：首送必须是初始基线 v1；观察到的版本**严格单调递增**（原为 `<=`，收紧为 `<`，合并跳过合法、回退/重复非法）；最终必达最新 `ready=true` 完整视图且版本等于权威 `getRoomInfo()` 版本。观察者隔离与非阻塞保证由既有 `blocking room observer does not block authoritative queries or operations` 继续覆盖（未改动）。

---

## 2. 测试执行声明

**特别声明**：根据任务指示，本次交付**未运行任何构建或测试**（未执行 `./gradlew`、未调用任何 shell/外部 Agent、未提交）。全部实测由 Codex 在交付后独立执行与验收。

---

## 3. 任务项逐条核对

### 3.1 离房竞态（任务项 1）
已修复，见 §1.1。关键不变量：leave 返回 `APPLIED` 当且仅当解绑完成（同一完成链内先解绑后完成回执），调用方观测到回执后任何旧回调必被第一重门丢弃；离房窗口内的旧回调被离房标志静默；真实顶号失效经回调门或补裁保底。

### 3.2 replacement resume 测试前提（任务项 2）
已补 `host.setReady(true)`，生产 `NOT_READY` 门槛未动（`SharedRoom.startMatch` 未改动）。

### 3.3 SharedRoomTest 观察者契约（任务项 3）
已按允许合并的契约修正，断言收紧（严格单调）而非放宽，未删除有效测试。

### 3.4 humanSeat != 0 装配核对（任务项 4）
核对结论：装配已在先前批次完整落盘，本次**无需生产改动**——`InProcessServer.createLocalTable(humanSeat)` → `RoomRegistry.createRoom(hostSeat)` → `SharedRoom(hostSeat)`（`require(0..3)`、单人模式 `LocalHuman` 仅在 hostSeat、其余 `Computer`、`adminSeat = hostSeat`、成员/就绪/房间信息 `isHost` 全部随 hostSeat），`InProcessBinding(viewerSeat = humanSeat)`，`startMatch` 以 `seat == hostSeat` 鉴权。新增模块测试锁定该一致性（§1.2）。未新增任何 LOCAL 固定占位房间/token 兼容路径；`InProcessBinding.createRoom` 大厅路径维持建房者 0 号房主位语义（`viewerSeat = 0` 与新建房间默认 `hostSeat = 0` 一致）。

---

## 4. 建议验收命令（Codex 执行）

```powershell
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewStage=R4B-review -PreviewOnly=true :server:test --tests com.majiang.review.* --tests com.majiang.v4models.* --tests com.majiang.v4runtime.* --tests com.majiang.v4authority.* --tests com.majiang.v4lobby.* --tests *V4FinalArchitectureTest.facadePublicTypesDoNotLeakDomainOrProtocolObjects --console=plain
.\gradlew.bat :server:test --console=plain
```

预期（供核对，非声明）：
- server 模块：86 项原有 + 4 项新增 = 90 项，此前 3 处失败（离房竞态、replacement-resume 前提、观察者契约）应全部转绿；
- 独立 109 项：#1（本地离房/重建竞态，含 20 次循环探针）应转绿；其余 8 项失败属 UDP（#2-#5）与客户端 room 运行时（#6-#9），不在本任务范围。

---

## 5. 尚未验证与遗留风险

1. **未编译、未实测**：本次仅静态修改，编译正确性与全部测试结果以 Codex 实测为准（尤其 Kotlin 细节如 `synchronized` 内 `val` 单次赋值、嵌套类访问私有成员——与原代码同风格，但未经编译器确认）。
2. **残余理论竞态**：回调门的三次读取（标志/代次/房间）中，代次与房间读取在锁外。已论证：正常路径下回调经执行器交接与变更建立 happens-before，跨整个离房窗口被挂起的回调由锁内复核（`isCurrentBinding`）与离房标志兜底；超出监视器/volatile 全序的理论撕裂读（JMM 形式语义允许、真实 JVM 不出现）未另行消锁覆盖。
3. **补裁行为新增**：`leaveRoom` 回执 `REJECTED` 且席位代次漂移/房间已关时，现在会确定性关闭连接（旧代码依赖概率性回调）。已加模块测试锁定；若 Codex 认为该语义需要调整，请反馈。
4. **换房瞬间的迟到 UI 事件**：已在回调门之前开始投递的旧房间 UI 通知（监听器已开始执行）理论上可能再送达一条旧视图给 `onRoomUpdate`/`onUpdate` 监听器，但绝不影响连接存活与权威查询（原行为一致，未收紧）。
5. **后续批次未动**：UDP 接入（独立 #2-#5）、客户端 roomInfo 排序/失效（独立 #6-#9）、R4B2/R5/R6 均未开始，等待 Codex 验收本批后派发。
