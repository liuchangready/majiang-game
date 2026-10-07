# D3：出千点数与Round/Session结果的实际账本交接

所有权：state/GameState.kt、state/StateInvariants.kt、round/RoundStateMachine.kt、round/RoundResult.kt、round/RoundTransitionPlanner.kt、match/MatchSession.kt、application/ApplicationCoordinator.kt、application/RoundResultValidator.kt。D2独占Host，不要修改host/UI/测试/Gradle。A已经结束，不改它的配置和独立CheatLedger。

目标：配置初始出千点数实际进入局内权威账本，结果交接与跨局传递、最终结果包含点数。不要在Host另存平行可写资源余额。保持Round/Session不计算校验或AI的边界。

1. GameState增加`val cheatPoints:IntArray=IntArray(4)`与本局资源流水或快照字段，deepCopy/copyInto/restore完备隔离；StateInvariants审计非负与字段长度。
2. RoundStateMachine冻结startingCheatPoints，RoundResult添加startingCheatPoints/finalCheatPoints/cheatPointDeltas（默认四个0以兼容旧fixture），必要流水摘要；deepCopy隔离。planner在buildRoundResult记录起终点和差额。
3. MatchSession增加betweenRoundCheatPoints，初始参数initialCheatPoints=List(4){0}；SessionChange/SessionSnapshot/RoundRecord/SessionResult必要字段始终深复制。已批准结果交接覆盖finalCheatPoints一次，不再加delta。下一局起点来自Session，不重新赠送初始值。终场记录最终余额及收支统计（若引入流水）。
4. Coordinator初始Session从initialState.cheatPoints读取；createNextRound将Session账本传入新GameState。既有legacy初始化fixture镜像同步也要保持合理起点；不改已有计分守恒规则。
5. RoundResultValidator验证出千起始基线、最终候选余额与delta一致；不能把守恒要求错误套到允许奖励/消耗的资源点数。
6. 在应用层提供明确的可信账务命令，例如`applyCheatLedgerEntry(entryId:String,seat:Int,amount:Int,reason:String):ActionResult`。它不是Client接口，不允许普通玩家任意调用。单桌锁、幂等同id同载荷、冲突/负余额/溢出无副作用、候选审计后Round.install，更新所有投影视图订阅。Round/Session本身不负责校验。不要直接绕过Round版本号修改state数组。复用A的CheatLedger计算候选或独立计算服务，不重复写另一份校验业务。
7. 若要新增资源事件，在event/GameEvent.kt与快照器仅加必要的可见性明确事件（该文件本任务可修改），默认只本人可见的消费原因不能漏给对手；若可通过快照变更完整表达也可无需新事件。不要添加没接规则的UseCheat玩家动作，现有四种技能的真正费用接线可留独立技能服务调用可信入口，但如实说明能力，不声称UI技能已完成。
8. 与普通麻将动作相同的事务性，终局后旧round拒绝资源修改；跨局重试相同entryId不能再扣当前局余额。

产出可以通过测试：配置[10,20,30,40]初始→可信扣7→局末结果[3,20,30,40]→下一局仍3→重复entry不再扣；不影响麻将积分和牌张守恒。拒绝负余额时版本、快照与事件完全不变。交付delivery-D3.md。
