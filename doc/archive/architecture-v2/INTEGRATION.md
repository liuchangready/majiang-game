# D 集成补充契约与验收接口

Codex 固定以下接口，避免测试与实现各自猜测：

1. 保留 `GameStateMachine(state, eventBus, ...)` 与 `initializeMatch(humanIndex, aiStrategies)`。构造器末尾新增具名参数 `autoPlayAi: Boolean = true`、`random: kotlin.random.Random = Random.Default`。`autoPlayAi=false` 仅关闭 AI 自动决策，系统必要的发牌/轮转摸牌仍正常执行，允许测试对任意座位手工提交合法命令。
2. `handleAction(action): ActionResult`、`legalActions(player): List<GameAction>` 按 DESIGN。传入的 state 实例在提交后仍代表最新权威状态，不能把内部引用换成新对象而留下外部夹具读取旧对象；可以候选深拷贝后 copyInto 原对象。
3. `LocalGameSession(viewerSeat: Int = 0, ...)` 保留默认构造；允许注入测试创建的引擎与事件总线，例如 `LocalGameSession(viewerSeat, engine, eventBus)`。对外 GameSession 不暴露它们。start 只能用于初始状态；注入已开始引擎时 view 立即有效。
4. A、B、C 初次交付及 review/A.md、review/C.md 已存在。D 开始时必须读取最终 delivery 与审查记录，核对剩余问题。A 初次交付 deepCopy 调用未定义 TilePool.copyFrom，是已发现的编译阻塞；若返修未覆盖，D 在所有权移交后修正。
5. 新建抢杠阶段时同步 StateInvariants 对该阶段的计数，不能放宽普通 RESPONSE_WINDOW。立直摸牌后的暗杠如果暂不支持合法待牌不变校验，应完全不提供该动作并在最终交付说明；加杠/暗杠对未立直玩家必须正常。
6. Rules/RiichiRuleEngine 可由 D 在 B 完成后适配，用 discardHistory、同巡/立直振听和同一 WinContext 来源校验，不保留原 isFirstUninterruptedGo 每次出牌后立即 false 的首巡错误。允许按每个玩家首摸与是否副露构造上下文。
7. C 初次界面只迁移了已有按钮。D 集成还必须提供合法吃/碰/杠/立直动作的最小可用按钮或选项，响应 Pass；不需要华丽布局，但不能产生一个人类有合法响应却无操作入口的死局。南四可选终了动作同样必须可操作，MATCH_END 展示终局并禁用 nextRound。
8. 命令完成后事件才发布；重入要求：监听器不能重入尚未完成的同一命令，拒绝或队列化有明确语义。监听器异常不能中止已提交操作链；最终事件监听消费者得到防御性快照。
9. 新增测试均由 Codex 编写。生产编译阶段因测试旧接口报错时写明错误，不要修改测试，不要通过恢复 UI.state/fsm 让旧测试编译。
10. GameSession.start/submit/nextRound 返回的 ActionResult.Accepted.events 也必须按 viewerSeat 脱敏，不能只过滤 subscribe 通道而从命令返回值泄露他家发牌/摸牌。GameEventBus 的数组/列表历史也必须与监听消费者隔离。

任务 A/C 返修完成后，D 接管必要跨文件修复与所有集成调用点；A/C 不再并行写这些文件。所有构建均由 Codex 执行，开发者不得调用 RunCommand（无头权限尚未授权）。
