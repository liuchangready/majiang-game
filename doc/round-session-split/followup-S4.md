# S4：全量回归修复（限定 S 文件）

只修改 application/**、state/GameStateMachine.kt，必要时 match/MatchSession.kt。其他任务仍在修 client 与 GameScreen，禁止修改那些文件和任何测试。禁止 shell/构建。写 delivery-S4.md。参考 DEVELOPMENT.md，但本次聚焦以下真实失败。

本轮新30测试27通过，全量431测试19失败。日志 build/round-session-split/regression-first.log；XML 在 core/build/test-results/test。两个 ClientSelection 失败由独立 UI 任务处理，不要碰屏幕。其余多数是旧兼容镜像不同步，而非规则算法变化。

1. **空事件也要广播更新**：Coordinator 多个 finally 只在 acceptedResult!=null && allEvents.isNotEmpty() 时广播。玩家记录 Pass 但窗口未结束，事件为空仍有状态变化；必须每个新 accepted 顶层命令都广播（含控制权改变），幂等回放不重复广播。ClientBoundaryAcceptanceTest 对应失败期望1实际0。
2. **只读兼容镜像实时同步**：旧 GameStateMachine.state 及原构造 state 必须在每次已提交 Round / Session 步骤后、外部事件回调和下一次 AI 策略读取前同步最新状态。LocalGameSession 直接通过 engine.coordinator 执行，因此仅在 GameStateMachine 方法返回后 syncLegacyFacade 不够。可以由兼容适配器注册一个内部镜像目标/安全的提交后同步机制，Coordinator 默认新路径不注册；绝不能重新让镜像等于 Round.state。同步镜像不能改旧 Round 终态。本轮失败 LocalGameSession、MeldChoice、RiichiChoice、FuritenScreen、EngineProtocol、MatchIntegration、UnifiedAction 多数因此导致。旧 AI 测试闭包读取旧 state 也应看到刚摸的牌与推进后的牌山指针。
3. **兼容调用不能等到并发命令完成才进入**：现在 facade 在 command 前调用 importLegacyStateIfUnstarted，它会阻塞 writeLock；拒绝后又 syncLegacyFacade 可能阻塞 readState。SeatControllerAcceptanceTest 的 concurrent/reentry 用例要求立即 Rejected(ACTION_NOT_AVAILABLE)。不能因为等待对方完成就顺便执行或配置新控制者。导入必须先检查处理中/tryLock 或与命令入口一体化；拒绝路径不能等待再同步。回调在降级 readLock 内触发 legacy e.handleAction 也不能升级锁死锁。保持完整查询一致性。
4. **旧 fixture 显式修改的兼容读取**：RiichiFlowContractTest `exhausted rinshan capacity...` 在第一次 draw 后（version>0）构造合法用尽岭上区再 e.legalActions，目前只允许 version0 导入导致测试失效。仅针对 trusted legacy facade，可以把显式修改镜像在非执行期间同步到当前仍活动的 Round，并保持同一 Round 的起始积分基线不重置。不要给新客户端此能力，不得导入旧已结束局。不要改测试或删断言。也可以提供兼容 state getter 使 trusted engine.state 真实指向当前活动数据，但不能又让旧传入引用在跨局时与旧Round别名；请保持设计。
5. **AI 候选不可变嵌套列表**：decideAiResponseAction 调用 strategy.decideAction(view, eligible)，turn 传 legal；这些列表虽然复制但可变。应传 `view.legalActions`（PlayerView 已做递归不可修改封装），与原实现一致。UnifiedActionAcceptanceTest `AI response action nested tile lists...` 期望 UnsupportedOperationException。
6. DecisionRegistry 在 queryDecision 的读锁中仍写缓存，多读线程会同时写。为注册表内部提供适当同步，避免读锁升级死锁；保持同窗口其他座位 decisionId 稳定。

回归失败列表可查看 XML，但不允许改测试。所有新单局/Session隔离、请求幂等/代次、并发一致性、结果守恒测试目前已经通过，必须保持。R2 的 deepCopy 等不要回退。
