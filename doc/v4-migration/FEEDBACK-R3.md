# R3 当前审查提示

## 20:21 主体落盘后的编译与代码审查（先处理）

**最新编译 20:30 R3-interim-4.log**：之前 override/KICKED 已修；当前 ServerConnectionSession 未实现新抽象接口成员（详见日志），onBindingEstablished 的 finalSnapshot 在 :603/:612 可能未初始化。先修编译再看行为。真实 UDP 两次断网测试 V4RuntimeUdpAcceptanceTest 已由 Codex 加入，尚未运行；它会在第一次恢复后等待旧 transport 两秒清理，再断网第二次，以暴露 scheduler 所有权问题。

20:32 所有权修复又有一个确定性资源问题：terminateSession 先 currentConnection.close()，UdpClient.onCloseCallback 使用共享 scheduler 安排 2 秒后关闭 socket；随后 runtime 马上 scheduler.close()（shutdownNow），会取消刚排的 socket cleanup，导致 receiver/socket 永不释放。正常关闭与永久断线也必须释放 socket，同时保留 R1 终结 ACK 窗口；请让终结 transport 清理不依赖马上销毁的 runtime scheduler，或按正确关闭顺序实现。不要只解决“旧连接杀死新 scheduler”却造成最终关闭泄漏。

- 编译日志 R3-interim-1.log：ServerConnectionSession.kt:71 `currentBinding overrides nothing`，:367 ApiProtocol.KICKED 不存在。不要为 override 错误把 RoomBinding 泄漏进 GameSession/LobbySession；移除无效 override。
- 当前 full 逻辑仍猜 `fromCursor == cursor`，并据此接受任意不同 epoch。必须落实下面第 1 条的显式全量标记；旧 epoch PUSH 永远不能自行替换新绑定。不同 epoch 只能由有效绑定/恢复响应建立。
- pendingFutures 仅保存 Receipt Future，snapshot/create/join/resume/list/query/decision 等仍 close 后悬挂。统一追踪全部外部 Future；取消及同步抛错也必须释放 pending、转为 failed Future。关闭时的 Future 消费同样走 dispatcher，不能直接 fut.completeExceptionally 破坏线程契约。
- handleInboundDisconnect 永久原因只设 stateRef=CLOSED，isClosed 仍 false，也未清理在途请求/订阅/计时器；需要一致终态。重复掉线通知不得开启并发重连循环。
- connectUdp 复用同一个 scheduler 给每个 UdpClient，而 UdpClient 关闭会在 2 秒后关闭这个 scheduler。首次断网后旧连接将杀死新连接与后续退避所用调度器。请明确 transport 与 runtime scheduler 资源所有权，隔离/引用计数均可，但不能旧连接关闭整个会话调度器。
- 同连接换绑/自动恢复必须重置 appliedBatchCursors/earlyBuffer/reorder/in-flight；新 epoch 即使 cursor 降低也应接受（当前 doReconnect 用 cursor >= cur 错误拒绝）。自动恢复订阅到新连接后、绑定响应之前的推送必须缓冲，不能与旧缓存比较直接丢弃。切房同理。
- triggerGapRecovery 完成后若还存在后续缺口，需要继续恢复；同步抛错和恢复映射异常释放标记。与旧 AsyncClientAcceptanceTest 语义一致。
- onBindingEstablished、decision 在 synchronized(syncLock) 内调用 dispatch。默认 dispatcher 立即执行，等于锁内调用用户代码，必须移出锁。
- leaveRoom 在途时即应禁止断网重连抢占；当前直到回执 APPLIED 才清 binding，丢包时会重新占座。显式退出后按 R1 语义仍可在大厅使用当前连接，不应把“退出旧房”误当永久关闭整个大厅连接。

这是开发中反馈，请继续修复并补模块测试；Codex 将运行独立测试，不提前放行。

独立 runtime 测试已扩展为 19 项，加入假时钟指数退避、新 epoch 游标归零、恢复期间早包、永久关闭、UI 派发、取消/晚回执隔离、明确全量标记/旧 epoch 拒绝、leave 在途不重连及重复断线通知去重。当前仍等待上述两处编译错误修复，新增测试未声称已执行。协议 full 字段落盘后测试已接 isFullSnapshot。

Codex 在开发期间记录；不是最终验收。

1. UdpClientBinding.handlePushFrame 解码 decoded.isFullSnapshot，但 dispatchUpdate 只传 ProtocolPlayerUpdate；后者目前没有 full 标记。新的统一 runtime 若再按 fromCursor 检查，会无法区分窗口外全量替换和普通缺口。请把明确全量语义贯穿 ServerConnection 到 runtime（任务允许必要 protocol/server 接线），不能靠猜 fromCursor == cursor，也不能让所有携带快照的 PUSH 一律跳过缺口。
2. UdpClientBinding 目前自身维护 pushSequencer、earlyPushBuffer、isHealingSnapshot。请明确传输可靠投递与业务同步各自职责，避免增加第三套游标/恢复状态造成互相覆盖；旧 GameClient 仅阶段共存待 R6 删除。
3. 初始绑定响应晚于 PUSH、snapshot/resume 响应晚于新 PUSH 时必须单调；同 epoch 旧 snapshot 不能覆盖新 decision。恢复失败后解除 in-flight 以允许下一次恢复。
4. 任意 ServerConnection 可在 onUpdate 注册或 createRoom 调用内同步触发回调；构造/初始化不得依赖 UDP 异步时序。关闭和切房后的旧回调（包括已排进 UI dispatcher 的通知）应失效。

Codex 正准备独立假连接测试。请按任务完成 API 后继续模块测试，交付前读取最新反馈。

## 首批可执行基线（20:17）

tools/architecture/v4-review/V4RuntimeAcceptanceTest.kt 已创建，仅由 Codex 修改。5 项均在旧直通门面上失败，日志 build/v4-migration/R3-runtime-baseline.log：

- duplicateAndForeignPushesDoNotReachUi：重复游标、外 session、旧 epoch 都被转发。
- gapCannotExposeFutureDecisionAndPredecessorDrainsInOrder：缺前序直接暴露 cursor 2。
- earlyPushCannotBeRolledBackByBindingReply：createRoom 内先推 cursor 1、后返回 cursor 0，绑定完成视图仍为 0；必须返回对账后的最新视图。
- closingRuntimeTerminatesOutstandingRequests：底层 snapshot Future 未结束时 close，上层 Future 悬挂。
- staleCallbackAfterCloseCannotNotifyUi：关闭前捕获的回调在关闭后仍通知 UI。

测试使用动态代理假 ServerConnection，避免具体传输时间依赖。目前接入 ServerConnectionSession，若你改名/构造签名请在交付注明，Codex 只调整接线保留断言。

20:19 扩展基线 7 项均失败（R3-runtime-baseline-2.log）。新增恢复在途去重/失败重试，以及一个 UI 监听器抛异常不影响其他监听器。恢复测试接受 runtime 使用 resume 或 snapshot 获取恢复基线，不强制内部方法选择。旧 AsyncClientAcceptanceTest 中的同步抛错转换为 failed Future、失败后 pending 释放、恢复后尚有后续缺口继续恢复等有效行为亦须保留。
