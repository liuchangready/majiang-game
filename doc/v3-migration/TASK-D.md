# D：V3 权威宿主与本地访问整合

依赖 A/B/C 的冻结接口及当前实际实现。你的所有权：新增 host/**、access/local/**（如LocalSession），可修改 application/ApplicationCoordinator.kt 增加必要可信入口；不要修改 A/B/C 文件、UI、Gradle、测试。后续 E 负责模块迁移，F负责Screen接线，账本结果字段整合另交D2。

目标不是仅编译接口：实现真实可玩的V3本地服务，串联已有Round/Session、配置、计时、AI、接管、更新流。不要实现另一份麻将规则。

## 必须公开的可测试 API

`com.majiang.host.TableHost(config:MatchConfig=MatchConfig(), timeControl:TimeControlConfig=TimeControlConfig(), clock:MonotonicClock=SystemMonotonicClock, controllers:List<SeatController>=listOf(LocalHuman,Computer,Computer,Computer), initialState:GameState?=null, disconnectPolicy:DisconnectPolicy=..., ...) : AutoCloseable`

`fun connect(seat:Int):SessionAccess` 返回私有绑定座位的本地适配器，可名LocalSession。
`fun tick(presentationBusy:Boolean=false)` 由可信宿主驱动；每次最多一个自动麻将动作；普通玩家、可和/合法立直暗杠、响应窗口保持等待直到选择或超时。
`fun disconnect(seat:Int)`、`fun reconnect(seat:Int)` 可信连接服务调用。
`fun stateForTesting` 不要添加；通过端点快照与已有独立fixture构造验收即可。
`snapshot` DTO包含时间、状态与控制代次，不暴露完整GameState、Coordinator。

## 约束

1. 创建 Coordinator 使用配置起始积分和 MatchProgression(config.toRuleConfig)，默认关闭 Coordinator 的同步AI/自动立直循环，并启用deferAutomaticActions。宿主接管自动策略，保证异步AI不会卡住串行权威。可用内部handleAction提交已验证AI/超时选择；真人用submitChoice。
2. 单桌串行锁或执行队列统一包住所有start/nextRound/submit/tick/control/disconnect/snapshot/subscribe操作；监听器重入命令明确拒绝，不允许回调在一次提交尚未结束时插入动作。查询与订阅快照必须一致。
3. 每次状态变化后同步所有座位待决策。按roundId+decisionId创建或关闭DecisionClock。非操作座位不计时。控制代次变化只废止凭据，不重置同一语义决策用时。旧局结束取消所有时钟，新局依scope重置。
4. 提交先检查身份/幂等/当前决策，再按权威deadline裁决。非法提交和重复回执不能扣时或刷新时间。截止前有效操作完成计时一次；已到期则系统超时选择胜出。只在实际接受动作后结算计时，不能先扣储备再拒绝。保留每请求稳定回执；不能重试失败后以新ID偷偷提交另一动作。
5. 响应提交成功若窗口尚在等待，回执RECORDED；真正执行后APPLIED。闭合窗口中别人未提交的计时按cancel结算。queryDecision对已被AI控制的客户端返回无选项（权威内部仍可查询AI候选）。
6. 用B的TimeoutPolicy处理到期。荣和超时必须走Pass原规则以更新振听；吃碰后从合法牌选择；不能伪造drawnTile。每tick最多一动作，接下来的tick继续其他过期窗口。
7. AI：使用 bounded daemon executor 在锁外计算，只给PlayerView与合法动作；捕获异常。每任务绑定roundId、decisionId、controlEpoch，返回时重验证。AI卡住不得阻止查询、其他玩家输入或计时到期。超时策略是兜底而不是随意额外自动和牌。close取消等待任务且不会无限等待线程。
8. 自动立直摸切只有没有自摸或合法杠时，在展示门控允许且节奏间隔达到时执行，最多一步。门控影响自动动作节奏，不改变已打开的权威deadline；显式合法单机暂停另行接口。普通真人绝不被AI代打。
9. 接入B掉线政策：IMMEDIATE_AI/GRACE_THEN_AI/TIMEOUT_ONLY。以配置aiProfileId解析具体AI（至少basic、tsumogiri两种），未知profile开赛拒绝。接管保留人类席位身份；reconnect不自动撤销已提交AI动作；requestControl(false)重新认证的绑定端点才收回，递增epoch并保留用时。
10. 每座位持有自己的syncEpoch与连续cursor、有限更新记录。snapshot+subscribe没有竞态：subscribe在同一串行边界发送带基线的初始更新/或等价握手；resume返回当前一致快照并废止旧流代次。只在可见投影/计时/控制/可见事件实际变化时增加该座位cursor，空闲tick不得无限发布。调用SessionUpdate.filterVisibleEvents，但初始快照仍需PlayerView；不得发完整内部事件。
11. 深复制所有跨监听器数据。投影视图、decision、events的可变内部数组不能别名。close一个SessionAccess只移除其订阅与触发配置的连接语义，不关闭整场；host.close才停止整桌。
12. 权限：只有房主/绑定管理员（默认seat0）可start；nextRound采用每座位ready，所有真人准备后才开始，AI自动ready；WAITING_DEALER_CHOICE只有当前庄家可选继续/终了。多端点重复ready不得重复创建下一局。按Session阶段验证，不随意StartStacking。
13. activeGame()返回绑定当前roundId的访问对象；旧局即使已进入下一局也不能把submit转给新局。queryResult返回过滤后的原局结算，不可返回下一局结果。
14. 不新增伪远程适配器；网络由N任务实现。本地AccessCapabilities remoteTransport=false、persistence=false、支持4人，如实声明。

阅读实际C接口后实现一致方法。若C必须扩展字段，写清必要接口变更建议给Codex，不擅自编辑并行所有者文件。交付doc/v3-migration/delivery-D.md，说明缺口，不允许把尚未接线的模块称为完成。
