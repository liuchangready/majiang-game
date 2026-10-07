# V3 迁移执行计划与契约

日期：2026-10-01。目标设计：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)。

用户已授权：迁移现有代码、清理无用业务与测试、开发新代码和测试；Codex 负责设计与验收。生产开发曾从 Antigravity 切换到 Claude Code GLM；2026-10-01 16:18 按用户最新要求切回 Antigravity，通过已安装的 MCP Bridge 接续现有工作。

## 基线与策略

- 当前 Git HEAD 为 f524d60；已存在的 V3 文档和 README 修改属于用户当前工作，不覆盖。
- 先执行原测试与桌面编译，保留基线日志 build/v3-baseline.log。
- 迁移保留麻将规则、实体牌守恒、优先级、立直/振听、副露与算分的行为测试，不以减少测试数替代修复。
- 仅删除已无生产调用且其约束已被新测试替代的兼容测试；删除记录逐项说明替代用例。测试失败不是删除理由。
- 生产代码由委派的开发代理实现。大型目录移动由 Anti 输出可审阅的 PowerShell 脚本，Codex 检视后执行；单一 PowerShell 路径操作，验证所有目标位于工作区。

## 目标代码结构

`engine`：纯 Kotlin/JVM 引擎模块，保留 com.majiang 下已有领域包名以降低无意义导入改动，包含 model/state/round/match/rules/settlement/event/ai/cheat/application、计时与控制服务。禁止依赖 libGDX/UI。

`client`：纯 Kotlin/JVM 客户端模块，包含 sync、GameClient、网络客户端适配；依赖 engine 的只读类型与公开入口。共享 access 接口及 DTO 与本地宿主实现放在 engine，避免 engine 中的本地宿主反向依赖 client。依赖不能反向流动。

`core`：libGDX 展示与组合根，包括 screen/render/MahjongGame；依赖 engine/client。客户端接口的展示桥接在此层。

`server`：纯 JVM 权威联网宿主与房间服务，可独立或由桌面宿主使用；不依赖 core/libGDX。

模块迁移在新组件稳定后执行；第一波仍在 core 源码树实现新包，避免并行移动造成冲突。不得仅复制源码到新目录而留下两份生产实现。

## 第一波任务所有权

| Task | 唯一写入范围 | 交付 |
|---|---|---|
| A | match/MatchConfig.kt、match/MatchProgression.kt、economy/** | 可配置四人赛程、击飞/排名政策、独立出千资源账本 |
| B | timing/**、control/** | 可注入时钟、20+3计时、超时默认动作、掉线政策状态 |
| C | access/**、sync/**、client/GameClient.kt | 统一异步接口、协议 DTO、有序快照/更新消费、客户端待确认状态 |

三者不修改 Coordinator、GameScreen、Gradle、测试或其他任务范围。任务报告写 doc/v3-migration/delivery-ID.md。

## 冻结的共享契约

异步接口使用 Java 8 CompletableFuture（项目 Android 已启用 coreLibraryDesugaring）；不要新增协程或网络框架依赖。所有返回 DTO 要隔离可变集合/事件数组。遵守 Android minSdk 21 可脱糖 API，不使用 Java ObjectInputStream 反序列化网络请求。

### A 对外契约

com.majiang.match.MatchConfig：`playerCount: Int=4, handsPerWind: Int=4, winds: List<Int>=listOf(1,2), startingScores: List<Int>=List(4){25000}, allowExtension: Boolean=true, allowAgariYame: Boolean=true, bankruptcyEnabled: Boolean=true, bankruptcyThreshold: Int=0, bankruptcyInclusive: Boolean=false, targetScore: Int=30000, tieBreakSeats: List<Int>=listOf(0,1,2,3)`。

`validateSupported()` 拒绝未实现三人规则、非法赛程、非法分数与重复座次。`toRuleConfig(): MatchRuleConfig` 桥接现有进度算法。现有 MatchRuleConfig 添加默认参数，不破坏旧测试。四人短赛允许 handsPerWind=1..4；winds 支持从东开始连续场风1..2，扩展最多再一场。现有默认行为保持。算法用配置替换 magic 3/4 的赛程判定（四人座位数量仍为4）。同分按 tieBreakSeats，击飞比较按配置。

com.majiang.economy `CheatEconomyConfig(enabled:Boolean=false, initialPoints:List<Int>=List(4){0}, costs:Map<String,Int>=emptyMap())`；`CheatLedger(config)`，`balances():List<Int>`，`apply(entryId:String, seat:Int, amount:Int, reason:String):CheatLedgerEntry`。同 id 同载荷重试返回原 entry；冲突、负余额、越界、溢出拒绝无副作用。正负流水与原因保留，不能影响麻将积分。提供 defensive snapshot 和结果交接所需结构。只有独立账本不算最终集成完成，后续 D 负责接线。

### B 对外契约

com.majiang.timing：`fun interface MonotonicClock { fun nowMillis():Long }`，`SystemMonotonicClock`。
`enum ReserveScope { ROUND, SESSION }`；`TimeControlConfig(baseMillis:Long=3000, reserveMillis:Long=20000, reserveScope:ReserveScope=ROUND)`。
`DecisionKey(roundId:String, seat:Int, decisionId:String)`；`DecisionTiming(key, generation, opensAtMillis, deadlineMillis, reserveAtOpenMillis, ...必要字段)`。
`DecisionClock(config:TimeControlConfig=..., clock:MonotonicClock=...)`：
- `beginRound(roundId:String)`：重复调用同局不刷新余额，ROUND作用域新局重置，SESSION不重置；关闭旧局计时。
- `open(key:DecisionKey, opensAtMillis:Long=clock.nowMillis()):DecisionTiming`：同决策重复不刷新，不允许同座位同时打开另一阻塞决策。
- `timing(seat:Int):DecisionTiming?`、`remainingReserve(seat:Int):Long`。
- `complete(key:DecisionKey, atMillis:Long=clock.nowMillis()):Boolean`：仅有效且截止前，恰好deadline拒绝，结算一次；未到opensAt可免费完成。
- `expired(nowMillis:Long=clock.nowMillis()):List<DecisionTiming>`：查询不消费。
- `expire(key:DecisionKey, generation:Long):Boolean`：到期且匹配才结算并关闭，旧定时器无副作用。
- `cancel(key)`：窗口因裁决不再需要该玩家时结算已耗时并关闭；不得白送新时间。
- pause/resume 保留免费时间与储备，不能重置。
`TimeoutPolicy.choose(view:PlayerView, options:List<GameAction>):GameAction?`：优先响应Pass，否则合法摸切，否则确定性合法弃牌；不自动自摸/荣和/杠。无合法必选动作返回null交给宿主报错。

com.majiang.control：配置化 `DisconnectMode { IMMEDIATE_AI, GRACE_THEN_AI, TIMEOUT_ONLY }`、`DisconnectPolicy(mode,graceMillis,aiProfileId)` 与独立 `ConnectionControl` 状态逻辑（断线、到期需接管、重连）；保留座位身份，不自行更改领域状态；后续协调器整合。

### C 对外契约

com.majiang.access：统一接口 `SessionAccess : AutoCloseable`，`MahjongGameAccess : AutoCloseable`。每个端点绑定不可伪造 viewerSeat，不暴露完整 GameState 或 coordinator。

`SessionAccess`：val viewerSeat:Int；`capabilities():CompletableFuture<AccessCapabilities>`；`snapshot():CompletableFuture<PlayerSnapshot>`；`decision():CompletableFuture<PlayerDecision>`；`submit(choice:ActionChoice):CompletableFuture<ActionReceipt>`；`start():CompletableFuture<ActionReceipt>`；`nextRound():CompletableFuture<ActionReceipt>`；`activeGame():CompletableFuture<MahjongGameAccess?>`；`subscribe(listener:(PlayerUpdate)->Unit):AutoCloseable`；`resume(cursor:Long):CompletableFuture<PlayerSnapshot>`；`requestControl(takeover:Boolean):CompletableFuture<ActionReceipt>`；close。全部共享单条逻辑更新流。

`MahjongGameAccess`：val roundId:String；snapshot/decision/submit/queryResult（未来结果可用只读事件列表表达全部结算），close；旧round绑定提交必须拒绝。

DTO 至少：`AccessCapabilities(protocolVersion:Int=3, supportedPlayerCounts:Set<Int>=setOf(4), remoteTransport:Boolean=false, persistence:Boolean=false, resumption:Boolean=true)`；
`PlayerSnapshot(sessionId:String, roundId:String?, syncEpoch:String, cursor:Long, sessionVersion:Long, roundVersion:Long?, view:PlayerView, decision:PlayerDecision, clocks:List<ClockView>=emptyList(), cheatPoints:List<Int>=emptyList())`；ClockView存seat/opensAt/deadline/reserve/权威时钟样本。
`PlayerUpdate(snapshot:PlayerSnapshot, fromCursor:Long, visibleEvents:List<GameEvent>)`；默认包含完整可见快照，先保证同步正确，后续可优化delta。fromCursor为前置游标，snapshot.cursor为新游标，按座位独立连续，私密事件不能造成可见游标缺口。
`ActionReceipt(requestId:String, status:ReceiptStatus, reason:String?=null, cursor:Long?=null)`，`ReceiptStatus { REJECTED, RECORDED, APPLIED }`。

com.majiang.sync `ClientSynchronizer`：`install(snapshot)`、`accept(update):SyncDisposition`，`snapshot:PlayerSnapshot?`，`SyncDisposition { APPLIED, DUPLICATE, GAP, STALE }`。旧epoch更新拒绝，只有显式install才能切换epoch；乱序缓冲有界，不能跳过gap；提供 drainEvents/或listener严格按实际apply顺序输出，去重。snapshot与events深复制。

com.majiang.client `GameClient(access:SessionAccess, dispatch:((()->Unit)->Unit)={it()})`：缓存只读 snapshot，异步 connect/start/nextRound/choose(action:GameAction)/requestControl；订阅Client更新；sync缺口自动resume；等待回执期间 pending=true禁止新选择；通过完整动作物理身份匹配当下PlayerDecision，不支持旧rawAction兜底。即便接口回调同步，也不得丢失初始化到订阅之间的更新。close只关端点。

## 后续集成与清理

D：统一 TableHost 接入A/B/C，Session/单局本地访问、计时与控制/AI、出千账本结果整合，更新默认本地入口。
E：模块移动脚本、测试归属迁移、兼容层移除与覆盖替代清单。
F：异步GameClient→Screen桥接与双展示状态，真实GL验收。
N：复用宿主的真实网络传输与房间服务、MallSession/RemoteSession，故障注入与端到端验证。

停止点不以预算或任务交付报告作为完成标准；生产接线、测试与构建均须实际通过。按阶段记录真实实现范围，三人完整玩法及无缝主机迁移保持V3既定独立阶段，不能假报支持。
