# S2：首轮代码审查修复（只修改 S 归属文件）

你是 Task S 的后续修复开发者。原 S 已结束，R/C 可能在独立归属文件工作。读 DEVELOPMENT.md、现有 application/MatchSession/GameStateMachine 与 Codex 新 architecture 测试。禁止 shell、禁止改测试。写 delivery-S2.md。

必须修复以下实证问题，不要仅解释：

1. 编译失败：ApplicationCoordinator.kt 和 GameStateMachine.kt 导入不存在的 com.majiang.model.SeatController；正确位于 com.majiang.state。日志 build/round-session-split/compile-first.log。
2. 严重状态别名：init 中 firstRound 持有 initialState 本体，后续 change.state.copyInto(initialState) 会在 nextRound 时篡改旧 Round，终局时 initialState.phase=MATCH_END 又修改单局终态。移除这种别名。可让 firstRound 使用 initialState.deepCopy，把 initialState 仅作兼容镜像；兼容旧 fixture 命令前的外部修改也需合理支持，但只允许旧 GameStateMachine 适配路径导入，不能让默认新端点导入镜像覆盖权威状态。首选 legacy facade 显式同步，默认协调器不暴露镜像写入权力。确保新增 SessionBoundaryAcceptanceTest 旧局冻结断言通过。
3. queryView 在 activeRound=null 时返回空手牌/空副露，导致结算时牌桌消失。保留 endedRound 的只读快照用于局间/终局展示，叠加 Session 最终账本；不能把 endedRound 当活动局。结算展示需要本局 metadata，下一局 metadata 在真正开局后使用。
4. 下一局现在先 session.install(StartNextRound) 再发牌和审计，若准备失败已污染 Session。必须先准备新 Round 及其发牌候选、完成所有验证审计，再一起安装 Session 和新 Round。WAIT_DEALER_CHOICE 的继续选择也不能先写入再失败。
5. submitChoice 只检查 cachedDecision，没重新核对其是否仍有效，没有 choice.roundId == registeredDecision.roundId 严格匹配。必须在同一锁内计算当前决策资格，拒绝旧回合/窗口/已响应/控制代次；保留同窗口其他座位决策。不能提交 roundId=null 伪装局内选项。invalid 请求不得清掉合法决策缓存。
6. 决策和请求回执存在可变引用泄漏：直接 return existing / receipt.result、列表和事件数组浅拷贝。返回和存储都深拷贝或真正不可变，包括 GameAction 的 nested tile list 和 events/newScores/payments。
7. 加一致性读锁：CAS 仅保护写入，queryView/queryDecision/controllerFor/legalActions 目前可能观察到半提交。使用统一可重入读写串行边界保证查询与提交一致，同时保持并发/回调重入命令拒绝、不能死锁。事件发布时快照必须来自完整提交。C2 将移除端点对协调器调用的嵌套锁。
8. 必须实际抽出独立外部验证/注册组件（SessionCommandValidator、RoundResultValidator、DecisionRegistry/RequestRegistry、ControllerRegistry 等），不要把所有内容堆在 970 行 Coordinator。Session/结果验证至少包括活动 roundId、未消费 resultId、source versions、初始账本匹配、final=start+delta、积分+供托守恒。只检查 consumedResults 不够。MatchSession 仍只应用 Approved 指令，不调用验证器。
9. 共享广播和旧 eventBus 的事件/回执必须隔离，回调不能改动之后监听者或后续重放。通过 snapshotGameEvent 复制；在完整顶层提交后发布更安全。保持已有重入测试的拒绝语义。
10. MatchSession 的结果、record、pending plan 等嵌套集合做防御复制；ruleConfig 要传入当前 matchProgression.config，而不是一律默认。

Codex 新测试位于 core/src/test/kotlin/com/majiang/architecture，可只读理解验收意图。不要修改其他任务文件。
