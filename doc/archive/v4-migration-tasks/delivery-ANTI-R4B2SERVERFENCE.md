# R4B2 SERVERFENCE 交付报告 (Anti)

## 1. 任务背景与边界声明
- **任务代号**: R4B2SERVERFENCE
- **执行角色**: Antigravity (Anti) - 生产代码与单元测试编写
- **监督与测试验收角色**: OpenAI Codex
- **操作原则**: 纯内置文件读取与编辑落盘，未运行 shell/Gradle/终端/构建/测试/提交/其他 Agent。
- **阶段边界**: 严格限制在 R4B2 服务端范围内，绝不越界进入 R5。构建与测试是否通过由 Codex 全权独立验证并裁决，本文绝不声称“构建已过”。

---

## 2. 改动文件清单
1. [`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt)
2. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
3. [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)

---

## 3. 具体修改内容与修复原因

### 修复 1：`SharedRoom.resumeSpectator` 顶替通知移出 TableHost 单线程权威队列
- **原问题**:
  原实现通过 `enqueueOperation { ... }.thenApply { notifySpectatorDisplaced(res.spectatorId, res.generation); res }` 串联。由于 `.thenApply` 未指定独立执行器，回调 listener 在 TableHost 单线程权威 Future 完成时同步执行。
  在 `InProcessBindingTest.concurrent resume spectator deterministic race rejects superseded request()` 中，第 1 次恢复（gen 2）的 `onSpectatorDisplaced` 回调被测试门闩（`unblockFirstLatch.await(5, TimeUnit.SECONDS)`）挂起。因为该回调直接运行在 TableHost 单线程上，导致 TableHost 执行队列被锁死整整 5 秒，第 2 次恢复（gen 3）无法入队执行。5 秒超时放行后，gen 2 认领绑定时成员代次仍为 2，导致旧请求错误成功，测试在第 947 行 `assertThrows` 处失败。
- **修复措施**:
  在 `SharedRoom.resumeSpectator` 中，将完成阶段改为 `.thenApplyAsync({ res -> notifySpectatorDisplaced(res.spectatorId, res.generation); res }, roomNotificationExecutor)`：
  1. TableHost 内部的 `member.generation++` 与快照生成在权威队列内原子完成并立即返回，权威队列绝不被外部观察者/门闩阻塞。
  2. 顶替通知及下游绑定认领整体移至专用异步通知线程池 `roomNotificationExecutor`。
  3. 当 gen 2 通知被测试门闩挂起时，TableHost 队列完全畅通，gen 3 恢复请求在数毫秒内即时完成权威代次递增（变为 3）并成功认领；放行后 gen 2 认领检测到最新代次已漂移为 3，确定性抛出 `IllegalStateException("DISPLACED_BY_NEW_CONNECTION")` 被拒绝，保留了严谨的竞争排他性，且不破坏现有完成链。

### 修复 2：`InProcessBinding` 观战代次漂移严格过滤与防过时顶替
- **原问题**:
  在异步/并发派发顶替通知场景下，旧代次（如迟到的 gen 2）通知可能在 gen 3 已完成绑定后到达，若未保留代次身份校验，可能误将刚激活的 gen 3 绑定作为被顶替对象关闭。
- **修复措施**:
  1. 在 `InProcessBinding.installSpectatorSubscriptions` 中，`room.onSpectatorDisplaced` 监听器显式接收 `(displacedId, newGeneration)`，只有在 `newGeneration > ctx.spectatorGeneration` 时才触发 `handleDisplacedByNewConnection(ctx)`，防止迟到的旧代次或同代次通知误关新绑定。
  2. 在 `installSpectatorSubscriptions` 安装后防竞态检查中，将代次校验明确为 `currentMem.generation > ctx.spectatorGeneration`。
  3. 在 `dispatchGatedCallback` 门控中，观战代次漂移校验更新为 `mem == null || mem.generation > ctx.spectatorGeneration`。
  4. 在 `handleDisplacedByNewConnection` 中，观战关闭认领校验更新为 `ctx.room.spectators[ctx.spectatorId]?.let { it.generation > ctx.spectatorGeneration } ?: true`，确保只有真正存在更高代次覆盖时才认领关闭。
  5. `tryClaimSpectatorBinding` 继续维持对 `mem == null || mem.generation != specGen` 的严格校验，若代次不匹配则立即拒绝并不做任何状态覆盖或误解绑。

### 修复 3：新增确定性单元测试验证旧代次通知不影响新绑定
- 在 `InProcessBindingTest.kt` 中保留原 `concurrent resume spectator deterministic race rejects superseded request()` 的全部门闩和断言逻辑（不改短超时、不删断言）。
- 追加新增确定性单元测试 `stale spectator displaced notification does not displace newer binding`，端到端验证即使存在过时代次（gen 1L）的上下文投递，门控系统也会安全静默过滤，active 连接（gen 2L）保持活跃不被关闭，公共快照正常可用。

---

## 4. 未完成项
- **本任务无任何遗留或未完成项**。针对 TableHost 权威队列阻塞与并发恢复代次判定均已完全修复并落盘。

---

## 5. 未运行测试声明与后续验收
- **测试未运行声明**: Anti 在本次任务执行过程中严格遵守约束，**未运行任何终端、Shell、Gradle 命令，未启动构建或测试**。
- **后续流程**: 交付由 Codex 接手，由 Codex 执行独立构建、测试回归与验收工作。若验收通过，由 Codex 依序派发后续阶段；Anti 不自行推进至 R5。
