# 落汗千术设计

日期：2026-10-11。状态：已完成并通过 Codex 独立验收。Anti 分阶段实现 engine、protocol/server/client 及表现层，Codex 编写测试、检视并退回修正。最终强制执行1000项测试全部通过，桌面与Android构建成功，真实产品GL及两种视口的可视GL通过。玩法和验证边界见[最终验收](ACCEPTANCE.md)；过程见 [Engine 检视](REVIEW-ENGINE.md)、[API 检视](REVIEW-API.md) 与 [表现层检视](REVIEW-PRESENTATION.md)。

落汗是本轮新增的第二个千术功能：玩家在打牌期间，花费 10 个额度，从自己的手牌中选择 4 张实体牌作标记。每位玩家最多同时保留 12 张有效标记。每批标记当局立即生效，并继续覆盖后续 3 局；施术者能够识别这些牌，尤其是它们进入对手手牌时。实现顺序为 engine 规则与领域接口、protocol/server/client 契约及接线、presentation 展示。

本文为开发设计基线；各层实现与验收状态以对应检视记录为准。第一阶段见 [Anti Engine 任务单](TASK-ANTI-ENGINE.md)，接口落地说明见 [服务端与客户端 API](../SERVER_CLIENT_API.md)。

## 已确认的玩法

以下规则已由用户确认：

- 选牌范围是自己当前手牌中的 4 张实体牌。
- 使用当局立即生效，另持续后续 3 局。
- 在有效期内，无论标记牌位于牌河还是对手手牌，施术者都能看到。
- 标记牌位于牌河时，必须记录并展示所在座位及该座位牌河中的具体位置。
- 每位玩家最多同时标记 12 张牌，即最多三组，每组 4 张。
- 每次施术消耗 10 个额度。

“实体牌”按 `tileId` 区分。同种牌的 4 个副本是 4 张不同的牌；标记一张五万，不会自动标记另外三张五万。赤五与普通五也各自保留身份。4 张可以同种，但不能重复选中同一个 `tileId`。自己的手牌包括暗手牌和独立摸牌，不包括已打出的牌、副露或袖牌。

例如，玩家在本局标记了 4 张手牌，其中一张五万在下一局发到下家手里：施术者能看到下家持有这张五万，其余未标记的手牌仍隐藏。下家打出该牌后，标识随牌进入牌河；被碰走后只在当前副露处显示，不残留在原牌河。

## 待检视的玩法建议

以下属于设计者建议，不是用户逐项明确指定的规则。用户已授权开始开发，首版按这些建议实施；如需改变玩法，先在交付说明中提出并由 Codex 检视，不能默默改动已确认规则。

| 项目 | 建议 | 原因与影响 |
|---|---|---|
| 使用时机 | 本人可弃牌的回合，`CHEAT_WINDOW` 或 `DISCARD`；进入响应窗口、抢杠窗口或结算后不能使用 | 选择与出牌共用既有回合，避免插入新的阻塞响应窗口 |
| 立直后 | 不能新施术；已生效的标记继续保留 | 不干扰自动摸切；立直前可先落汗再宣言立直 |
| 重复标记 | 每组恰好 4 张，各组目标不重叠；已生效的牌不能重复标记、替换或续期；到期后可再次选择 | 在用户确认的 12 张总上限内独立管理各组寿命 |
| 连续使用 | 不另设每回合次数上限；余额、时间和剩余容量允许时，同一回合可提交多组 | 每次均需新的有效请求和最新决策，三次成功共消耗 30 个额度 |
| 扣费时点 | 每次成功一次性扣 10 个额度；选牌与取消免费，拒绝不扣费，后续局不再扣费 | 与标记写入原子提交，保证不会重复扣费 |
| 成功与抓包 | 首版确定性生效，不增加新的随机失败、怀疑度或抓包公式 | 先完成标记和跨局信息能力；视线风险若要加入，需单独确定概率、代价及失败语义 |
| AI | 首版 AI 不主动施术；已存在的标记在 AI 托管期间继续计时和保存 | 不把策略开发与首版规则交付绑定；AI 后续若使用此能力，也只能读取自己获准的投影 |
| 额外可见区域 | 自己手牌和原本公开的副露、宝牌指示牌也显示标识；牌山、未公开王牌、袖牌均不额外揭示 | 不把落汗扩展成牌山定位或袖牌透视 |
| 整场结束 | 剩余有效期不带到另一场，不退还剩余费用 | 效果绑定当前 `sessionId` |

额度在实现上沿用现有千术点账本，不新增另一种货币。费用采用现有 `CheatEconomyConfig.costs["TILE_MARK"]` 作为唯一配置来源，本版启用时必须显式为 10；缺失或其他数值应拒绝配置，避免沿用 `costFor()` 的缺省零费用误开免费技能。新增 `TileMarkConfig(enabled = false)` 只控制功能开关；每次 4 张、总上限 12 张、后续 3 局和每次 10 个额度均为本版固定规则。启用时必须同时启用千术经济。产品默认配置是否开启落汗，仍待检视。

## 持续时间和实体身份

### 按实际发牌计数

新增整场内部计数 `dealtRoundOrdinal`：同一 `roundId` 首次成功完成发牌才递增一次。构造状态机、进入 `STACKING`、打开准备面板、失败的下一局请求、重复事件、断线重连、暂停恢复都不递增。具有完整已发牌 `initialState` 的测试装配也必须登记一次。

连庄的新 `roundId` 算下一局；荒牌流局、途中流局只要已实际发牌，都算已经进行的一局。不按“东一局”等局名、本场数、玩家摸牌巡数或 `sessionVersion` 计算持续时间。

每次施术创建一组独立效果。设该次施术时的已发牌序号为 `k`：

- `createdRoundOrdinal = k`。
- `expiresAfterRoundOrdinal = k + 3`，是包含在有效期内的最后一局。
- 首次成功接受序号 `k + 3` 的单局结果时，到期清除该组。
- 整场提前结束或关闭时立即清除。所有清理都必须幂等。

新组不会延长旧组寿命。已有效标记数按本座位所有未到期组的不同 `tileId` 总数计算；新组提交前必须满足 `activeTileCount + 4 <= 12`。一组到期释放 4 个名额，其他组继续有效；牌暂时处于不可见区域也占用名额，不能按当前看到多少张计算容量。

| 阶段 | 标记是否有效 | 界面说明 |
|---|---|---|
| 施术当局 `k` | 是，立即生效 | 本局生效，另余 3 局 |
| `k` 结算及下一局准备 | 是；准备时不提供牌的位置 | 还可覆盖后续 3 局 |
| 后续第 1 局 `k + 1` | 是 | 本局生效，另余 2 局 |
| 后续第 2 局 `k + 2` | 是 | 本局生效，另余 1 局 |
| 后续第 3 局 `k + 3` | 是 | 本局为最后有效局 |
| 第 3 个后续局结算完成 | 否 | 落汗已失效 |

局间保留各有效组的 4 张牌及各自期限，但不把上一局的手牌位置当成新局位置。最终有效局的牌面在终局动作裁决前仍可识别；其结算快照仅移除已经到期的组，其余组保留。已经形成的旧快照保持自身内容不变，不能因到期而被反向修改。

多组示例：第 1 局施术 A 组、第 2 局施术 B 组、第 3 局施术 C 组，每次扣 10 个额度，合计 12 张。A、B、C 分别在第 4、5、6 局结束时到期。第 4 局 A 到期后容量降为 8 张，第 5 局可再选一组新的 4 张；不能把三组统一按最新一次施术日期续期。

### 同一副牌的跨局身份

现有 `Tile.tileId = typeIndex * 4 + copyIndex`，`TilePool.buildStandardSet()` 和洗牌生成器每局重新构建相同的 136 个身份。因此以 `(sessionId, tileId)` 表示本场持续跟踪的实体副本，不依赖 Kotlin 对象引用、手牌排序索引或牌山索引。

标记只增加独立的元数据，不改变 `Tile` 的相等性、`copyIndex`、赤牌属性，也不调用 `TileMovement` 移动牌。下一局照常洗牌、发牌和执行码牌千术，不为了使标记可见而改变发牌分布。

## 当前架构与实现入口

| 现有代码 | 对本功能的约束 |
|---|---|
| `TableAuthority` | 对 server 的领域门面；`coordinator` 已为 `internal`，不得重新公开 |
| `MatchSession`、`SessionChange` | 管理跨局状态并通过 prepare/install 安装，适合保存跨局标记 |
| `RoundRuleService` | 早期通用 `UseCheatTechnique` 已移除，落汗走专用强类型请求，不依赖通用千术枚举 |
| `ActionChoice`、`PlayerDecision` | 当前提交的是权威候选 `optionId`，没有四张自选牌的参数入口 |
| `DecisionClock.rekey()` | 已支持更换决策键且保留截止时间，可复用资源操作的计时处理 |
| `PlayerSnapshot` / `PlayerView` | 现有对手暗牌只提供张数，需新增施术者专属的有限可见信息 |
| `PublicProjectionFactory` | 观战采用独立白名单投影，不得添加落汗私有信息 |
| `GameScreen` | 当前 `renderPlayerHands()` 只绘制本人的牌，不能把对手标记显示当成已有的翻牌接口 |

落汗采用专用强类型请求，不通过 `Map<String, Any>` 携带参数，也不为 14 张牌的 `C(14,4) = 1001` 种组合生成候选。普通弃牌、鸣牌和立直继续使用原 `submit(choice)`。

## Engine 领域设计

### 状态归属

拟新增 `com.majiang.cheat.mark` 包，包含：

| 类型 | 职责 |
|---|---|
| `TileMarkRules` | 校验使用时机、手牌归属、四张去重、组数与费用；计算候选变更 |
| `TileMarkGroup` | 保存所属座位、组标识、4 个 `tileId`、创建局和到期局次 |
| `TileMarkState` | `MatchSession` 内部的四座位标记组列表，每座位最多三组，另含状态版本、已登记发牌局次 |
| `TileMarkProjectionFactory` | 从当前已提交领域状态构造单个观察者允许看到的信息 |
| `TileMarkRequest` | 领域请求值对象，只含强类型字段和防御复制后的集合 |

`TileMarkGroup` 最小字段为 `groupId`、`ownerSeat`、`tileIds`、`createdRoundId`、`createdRoundOrdinal`、`expiresAfterRoundOrdinal`。组标识在首次成功提交时产生，重试返回原结果；同一座位各有效组互斥，不同座位可以各自标记同一个实体牌，归属、容量和期限互不影响。整桌最多 48 条带所有者的有效标记记录，不要求四人的目标牌合计互不相同。

`TileMarkState` 由 `MatchSession` 持有，不存入 `GameState`、渲染器、客户端缓存或 `TilePool`。新增受控的 Session 变更类型处理施术、登记成功发牌、到期和关闭清理；补齐 `SessionChange` 的候选字段、快照和深复制，不暴露可变集合。已有 Session 变更必须默认保留标记状态，不能在暂停、结算或换庄时丢失。

由 `ApplicationCoordinator` 在现有写锁和提交保护内协调单局账本与整场标记的原子安装。server 只调用 `TableAuthority` 新入口，不穿透读写 `MatchSession`。

### 权威接口

拟增加：

```kotlin
// engine 自有类型；列表在实际实现中必须防御复制。
data class TileMarkRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    val tileIds: List<Int>
)

// TableAuthority 拟新增成员；seat 和 now 由可信宿主传入。
fun submitTileMark(
    seat: Int,
    request: TileMarkRequest,
    now: Long
): MutationResult
```

查询通过 `querySnapshot()` 的新增私有字段，不另开可绕过投影的全局标记查询接口。

新请求必须验证以下条件：

1. 玩家绑定有效、当前连接有提交权、非观战，`seat` 为服务端绑定的座位；真人已被托管时不能施术，需先显式收回控制权。
2. 功能及经济配置启用，当前整场、局、决策与控制代次匹配；未暂停、未结束且当前已发牌。
3. 本人为行动玩家，阶段允许，当前存在本人的合法弃牌候选；未立直、未处于任何响应窗口。
4. 对应权威计时存在且 `now < deadline`，等于截止时刻也拒绝。缺失计时不能当成不限时。
5. `tileIds.size == 4`，范围均为 `0..135`，去重后仍为 4；每张都在当时本人的 `closedTiles + drawnTile` 中。
6. 选中的牌不在本座位其他有效组中，新增后有效标记总数不超过 12，余额至少为 10。客户端提供的费用、到期局次或声称的牌位置均不作为依据。

### 原子性与计时

先校验并准备全部候选：费用流水、单局账本状态、整场标记状态及投影可用性。通过状态版本及守恒审计后，在一个领域提交中安装。禁止先单独调用会提交并广播的 `applyCheatLedgerEntry()`，再写标记；否则可能出现已经扣点却没有标记的半提交。

成功后返回 `APPLIED`、`stateChanged = true`，恰好扣除 10 个额度并生成完整快照。普通拒绝返回 `REJECTED`，不扣费、不创建半组、不改变寿命或牌位。两次并发施术也必须逐个在队列内重算剩余容量，不能先在网络线程各自判定后突破 12 张。首版不要求新增包含牌 ID 的广播事件；施术者通过回执和新的标记快照得到反馈，费用沿用现有账本事件及可见性规则。即使事件列表为空，私有快照变化也必须触发推送。

落汗是本次出牌前的补充操作，不完成弃牌决策、不摸牌、不切换行动玩家。费用导致 `roundVersion` 和 `decisionId` 改变时，沿用 `TableAuthority.applyCheatLedgerEntry()` 的计时迁移方式，对仍有效的原决策调用 `DecisionClock.rekey()`。保留 `opensAt`、`deadline`、储备基数和计时 generation；不能调用 complete/cancel 后重新 open，也不能赠送新的 3 秒。

成功回执到达后，客户端仍需等待相应新快照再提交弃牌，避免使用旧 `decisionId`。旧决策、旧控制代次及超时到达的标记请求都必须被拒绝；超时自动出牌依然使用原期限。打开选牌面板、播放标记动画、重连均不暂停该期限。

### 幂等和清理

领域去重按当前整场、座位和 `requestId` 生效，并保存操作类型及载荷指纹。与普通动作、洗牌方案共用统一请求标识约束：同一 ID 用于另一类写操作也返回冲突，不能通过换接口再次执行。

对 4 个合法 ID 排序后计算语义指纹，选择顺序不影响标记集合。合法重复请求返回原回执，即使已进入下一局或标记已经到期，也不能重新扣费或重新产生标记；返回旧回执不恢复旧效果。相同 ID 改动牌集合、整场/局/决策/控制代次等字段，返回 `REQUEST_ID_CONFLICT`。UDP 传输层已有的更严格原始载荷冲突规则保留；自动重试必须复用完全相同的载荷。

普通查询、重放和对账不能延长寿命。断线、AI 接管、同座位合法重连和显式收回控制均保留原有各组及各自到期序号，`controlEpoch` 只废止旧提交，不作为标记寿命。若房间规则允许永久离房后换人占座，宿主必须先通过可信领域操作清理旧座位标记和相关私有缓存，再绑定新玩家；换人不能继承信息或重置费用。整场关闭释放全部状态和请求记录。

## 私有可见信息

拟在 `PlayerSnapshot` 增加可空 `tileMark: TileMarkView?`。功能关闭时为 `null`；开启时返回当前观察者自己的施术能力和全部有效组，不返回其他人的组。`PlayerView` 对未标记暗牌仍仅提供张数。

`TileMarkView` 包含：

- `canApply`、明确的禁用原因、权威费用 10、`activeTileCount`、`maxActiveTiles = 12`、当前可选 `selectableTileIds`；候选排除本座位已经有效标记的牌，仅对有操作权的本回合提供。
- `decisionId`、`controlEpoch` 和视图状态版本，用于绑定选牌面板及阻止旧选择提交。
- `activeGroups`：0 至 3 个有效组。每组含组标识、创建局、最后有效局序号、`futureRoundsRemaining`、`isLastEffectiveRound`，以及恰好 4 项已标记牌的当前可见情况。各组独立展示期限，按创建局次和组标识稳定排序。

`futureRoundsRemaining` 表示“当前局之后还可覆盖的局数”，不是包含当前局的总数；最后有效局时为 0，但组仍有效。局间以最近已发牌的局次计算；准备阶段不要提前减一次。

每个标记牌条目包含 `tileId` / 强类型牌面、一个受限的可见区域、可空座位号，以及可空的 `riverPosition: RiverPosition?`：

| 领域真实位置 | 给施术者的投影 | 显示规则 |
|---|---|---|
| 自己暗手牌或摸牌 | `OWN_HAND`、本人座位 | 原牌面加小标识 |
| 对手暗手牌或独立摸牌 | `OPPONENT_HAND`、持有者座位 | 显示已标记的牌面，不下发对手其他牌或其排序 |
| 当前牌河 | `DISCARD`、对应座位及 `riverPosition` | 精确记录牌河位置，在对应槽位的实体牌上加标识，并在追踪栏显示位置 |
| 原本公开的副露牌 | `EXPOSED_MELD`、对应座位 | 在副露处加标识；被鸣走的弃牌不再在原牌河重复显示 |
| 原本公开的宝牌指示牌 | `EXPOSED_INDICATOR`、无座位 | 只标识本来已公开的牌 |
| 牌山、未公开王牌、袖牌或当前无已发牌牌桌 | `NOT_VISIBLE`、无座位 | 仅保留玩家已经知道的标记牌面，显示“当前不可见” |

### 牌河位置契约

`RiverPosition` 包含 `roundId: String`、`seat: Int`、`discardIndex: Int`。`discardIndex` 从 0 开始，表示同一快照中 `view.discards[seat]` 的当前索引；必须满足该位置的 `tileId` 与标记牌一致，且 `seat` 与条目的可见座位相同。它不是累计弃牌次数或 `discardHistory` 中的历史序号。

engine 在生成私有投影时，按实体 ID 在权威牌河列表中定位，记录到该版本的 `MarkedTileView`。单靠 `TilePool` 的区域与座位不足以确定牌河位置。server、protocol、client 完整传递这个位置，客户端不按牌型猜测同种牌的位置。

区域为 `DISCARD` 时 `riverPosition` 必须存在，其他区域必须为 `null`。标记牌被鸣走后，在同一次快照更新中清除牌河位置并改为副露位置；若牌河列表变化，其他仍在牌河中的标记牌按新列表重新定位。进入下一局、准备阶段或标记到期时，清除旧局位置；重连通过完整快照恢复当前有效位置。已经发出的旧快照仍保持原位置记录，不被后续变化改写。

UI 用当前牌河布局函数把座位与索引换算为画面坐标，追踪栏显示“下家牌河第 7 张”等从 1 开始的位置描述；切换观察视角、分辨率或换行布局不改变协议索引。标识跟随该实体牌的绘制和动画显示时机，不在动画隐藏的目标槽位提前显示。绘制前校验局、索引和实体 ID 一致；不一致时不标错牌，并沿用现有快照恢复机制重新同步。

### 隐私与快照一致性

`NOT_VISIBLE` 不携带隐藏区域的细分原因、所属座位、牌山下标、摸到该牌还需几巡等信息。不能把 `TilePool.tileLocations` 整体发给客户端，再由 UI 隐藏。

对手持牌不下发暗牌 `slotIndex`、真实排序位置或“刚摸到”标志，以免从排序位置推测未标记牌。首版在对应对手区域显示“落汗识别”小牌列，只表示对手持有这些牌；如果后续接入完整牌背行，已识别牌使用展示用位置排列，其余位置画牌背，展示位置不代表真实手牌顺序。

投影在提交后的同一状态版本上，由 engine 对本座位至多 12 个 ID 查询当前容器位置；同时校验位置与实体归属。跨局不沿用旧位置缓存。快照及嵌套集合都要深复制并只读包装，避免后续移动牌或修改来源集合改变已经发出的视图。

其他玩家及所有观战者不获得该施术者的牌列表、组标识和剩余期限。普通的公开牌面仍按原规则可见；他人自己拥有落汗时，只得到自己的效果。`PublicTableProjection`、`ProtocolPublicSnapshot`、`PublicSnapshotView` 和公开历史结果均不新增落汗字段；公开事件白名单也不放行任何带标记牌信息的事件。

## 协议与客户端 API

### 独立类型映射

领域、线路、客户端各自定义类型，不以 typealias 共用跨层 DTO：

| engine | protocol | client.api |
|---|---|---|
| `TileMarkRequest` | `ProtocolTileMarkRequest` | `TileMarkRequest` |
| `TileMarkView` | `ProtocolTileMarkView` | `TileMarkView` |
| `TileMarkGroupView` | `ProtocolTileMarkGroupView` | `TileMarkGroupView` |
| `MarkedTileView` | `ProtocolMarkedTileView` | `MarkedTileView` |
| `RiverPosition` | `ProtocolRiverPosition` | `RiverPositionView` |
| `TileMarkConfig` | `ProtocolTileMarkConfig` | `TileMarkConfig` |

线路请求和客户端请求与领域请求具有相同的业务字段：`requestId`、`sessionId`、`roundId`、`decisionId`、`controlEpoch`、`tileIds`。不能接受客户端指定 `ownerSeat`、费用、持续局数或效果结果。`EngineProtocolMapper` 和 `ProtocolViewMapper` 显式转换并校验所有新字段；后者继续位于 `client.mapping`。

新增 `SessionApi.submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt>`，以及 `client.api.GameSession.submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt>`。`ServerConnectionSession` 在 `client.session` 中实现，沿用在途请求、绑定代次、调度线程和关闭 Future 的既有规则。`SpectatorSession` 不提供该方法，底层伪造请求也必须拒绝。

### 本地与 UDP 接线

新增 `ApiTags.SUBMIT_TILE_MARK` 及 `ApiRequest.tileMark` 字段，更新已知 API 白名单、编解码器、请求载荷校验、UDP 客户端分发、服务端分发和进程内绑定。信封 `requestId` 必须与内部请求一致；一个请求不能混入 `choice`、`shufflePlan` 等其他写操作载荷。

所有路径最终在 `ServerTableHost` 的同桌串行队列里调用相同的 `TableAuthority.submitTileMark()`，包括超时处理前后的竞态。不得在网络线程修改标记或在权威队列中阻塞等待 Future。

保留 UDP V4 的帧头、分片阈值、窗口及完整快照机制。`ProtocolPlayerSnapshot` 和 `GameSnapshotView` 增加相应可空字段；每次座位快照都包含完整的当前效果，标记为 `null` 或 `activeGroups` 为空时必须清除旧缓存，单组消失时移除该组，不能与旧列表合并而复活过期组。加入、恢复、手动查询、PUSH 和缺口自愈均使用同一投影，不能只靠一次成功事件恢复效果。

能力集拟增加 `tileMarkSupported`，缺失时按 false 处理；房间配置决定是否实际启用。首版交付仍要求各模块同版本升级，不承诺任意旧客户端可以加入已启用落汗的房间。旧服务不支持时新 UI 禁用入口；未知字段缺省值、能力不匹配与错误码须在接口阶段用兼容夹具验证，不能静默当作施术成功。

### 拒绝原因

业务拒绝沿用 `Receipt.status = REJECTED`；结构错误与身份错误沿用协议错误路径。

| 原因 | 含义 |
|---|---|
| `TILE_MARK_DISABLED` | 功能未启用 |
| `WRONG_PHASE` / `NOT_YOUR_TURN` | 当前不是可施术的本人出牌阶段 |
| `TILE_MARK_RIICHI_LOCKED` | 本人已经立直 |
| `TILE_MARK_CAPACITY_EXCEEDED` | 新增 4 张后将超出本座位 12 张有效标记上限 |
| `TILE_MARK_ALREADY_MARKED` | 选择包含本座位其他有效组已经标记的牌 |
| `TILE_MARK_INVALID_SELECTION` | 数量不为 4、重复或 ID 越界 |
| `TILE_MARK_TILE_NOT_OWNED` | 选中的实体已经不在本人可选手牌中 |
| `INSUFFICIENT_FUNDS` | 千术点不足 |
| `DECISION_MISMATCH` / `STALE_CONTROL_EPOCH` | 局、决策或控制代次已变更 |
| `DEADLINE_EXPIRED` / `HOST_PAUSED` | 已到截止时间或宿主暂停 |
| `REQUEST_ID_CONFLICT` | 同一请求 ID 被用于不同操作或不同载荷 |

房间或 Session 不匹配、未绑定、已关闭和观战提交，采用现有绑定/会话错误体系，并在第二阶段冻结确切映射。回执中不能带回请求者无权获知的目标牌实际持有者或位置。

## 表现层设计

### 施术交互

在现有码牌千术之外增加“落汗”入口，但仅在对局期间按权威 `canApply` 开启。点击后进入本地选牌模式：显示当前手牌，点击勾选或取消勾选，显示 `本次已选择 n/4`、`已标记 m/12`、`消耗 10 额度` 和原出牌倒计时。选满 4 张后确认可用，继续点第五张不会自动替换此前选择。已有有效标记的牌显示占用状态且不可重复选；达到 12 张时入口禁用并说明“标记已满，等待到期”。取消退出选牌模式，不提交任何领域动作。

选牌模式与立直选牌、鸣牌选择互斥。选择点击不能同时触发弃牌；已有出牌动画输入锁继续生效。没有待确认操作时，退出选牌模式后恢复正常出牌。手牌排序只改变展示位置，选择集合始终按实体 ID 保存。

确认提交后使用现有 `isPending` 门控避免重复施术和同时出牌，不乐观扣点或先显示成功标记。回执和快照可乱序到达：只有权威快照出现相应组才显示已生效；先到的回执不能覆盖更新的快照。领域拒绝显示具体原因，并根据最新快照决定能否继续选择。

决策、局、控制代次、选牌候选或绑定变化后，旧选择立即失效；断线、自动托管、暂停和超时也关闭或禁用面板，不在恢复后补发旧选择。默认超时动作按原规则出牌，选牌面板不延长时间。

### 效果展示

- 自己手牌、牌河和公开副露中的标记牌使用小型汗滴图标及描边；不改变赤宝、立直横置牌或副露朝向的视觉含义。
- 在对手座位旁增加“落汗识别”小牌列，显示最新投影明确授权的牌面。首版不要求取得对手真实手牌槽位。
- 提供最多 12 张牌的追踪栏，按三组各 4 张分行或折叠展示，列出牌面及“自己手中 / 下家手中 / 对家牌河第 7 张 / 当前不可见”等状态；牌河状态必须包含具体位置，避免仅靠颜色表达信息；移动端也必须能查看全部有效组。
- 每组分别显示期限，区分“另余 3 局”和“本局最后有效”，不要在后续第 3 局显示成已经到期，也不能用最新组期限替代旧组期限。
- 标记牌一旦改变区域，旧区域标识随当前权威快照移除；正在播放的旧动画不得把旧局或已到期牌面重新显示为有效识别信息。
- `SpectatorScreen` 无入口、无追踪栏、无私有标记。玩家退出房间或改为观战后立即清除对应 UI 缓存。

表现层拟新增 `TileMarkSelectionModel`、`TileMarkPanel` 和小型标记绘制组件，全部只依赖 `client.api`。纯选择状态、上限、失效条件和快照恢复可用无头测试覆盖；桌面鼠标和 Android 触控共用同一选择模型，不采用仅悬停可见的交互。

## 分阶段交付

| 阶段 | 修改范围 | 完成条件 |
|---|---|---|
| 第一阶段 Engine | 规则、整场标记状态、原子账本提交、发牌局次、到期、私有投影、`TableAuthority` 入口和领域测试 | 无 UI/网络也能完整验证施术、后续三局、守恒、隐私和时钟；评审并冻结领域契约 |
| 第二阶段 接口与 API | protocol DTO/编解码、server 映射与本地/UDP 接线、client.api 门面与视图、client 实现及恢复缓存 | 真实本地/UDP 链路能施术、拒绝伪造、恢复完整效果，完成接口文档及集成测试；此阶段通过后才接展示 |
| 第三阶段 表现层 | 入口、四牌选择、追踪栏、对手识别显示、输入锁、字体文本及桌面/移动布局 | 真实牌桌完成选择到施术再跨局识别的闭环，恢复/到期/观战均正确 |

每阶段先完成自己的行为验证，再进入下一阶段；不能先在 UI 假翻牌、用客户端计数三局，或把未接线的 DTO 当成功能完成。现有麻将规则、算分、牌山、出千账本、UDP 可靠性与控制权测试均须保留。

## 验收清单

| 组别 | 必须覆盖的情况 |
|---|---|
| 选牌与守恒 | 4 张不同 ID 成功；3/5 张、重复、越界、同种不同副本、赤五/普通五；暗手牌与独立摸牌均可选；他人手牌、牌河、副露、袖牌不可选；所有 136 张的位置前后不变 |
| 容量与多组 | 依次 4、8、12 张成功且合计消耗 30；第四组拒绝；不可见牌仍占名额；重复标记既有实体拒绝；单组到期仅释放 4 个名额，其他组及期限不变；并发请求不突破上限 |
| 使用权限 | 他人回合、响应窗口、抢杠窗口、立直后、准备阶段、暂停、结算、托管及观战均拒绝；绕过 UI 发请求仍拒绝 |
| 跨局寿命 | 当局、三个后续局各自有效；同局创建多组和不同局创建多组分别计期，互不续期；各自最后一局结束到期；连庄和途中流局计数；等待发牌、失败请求、重复回调、暂停与重连不重复计数；整场提前结束清理 |
| 跨局身份 | 连续四局重新生成状态机与牌对象，标记仍按同一个 `tileId` 跟踪；不按牌型扩散或改变洗牌分布；码牌开启/关闭两条发牌路径一致 |
| 原子与费用 | 每次成功恰好扣 10；余额 9 拒绝、10 可用；重试只扣一次；余额不足、校验失败、版本冲突不留半组或半笔流水；缺失费用键或费用不为 10 的启用配置拒绝 |
| 请求与竞态 | 相同请求重放、改载荷冲突、跨 API 复用 ID、到期后重放；标记与弃牌/超时/接管并发依队列顺序生效；已经离手的牌不能被补标记 |
| 时钟 | 施术导致新 decisionId 后，原 deadline、opensAt 和储备不重置；恰好到期拒绝；重复操作与动画不能获得额外思考时间 |
| 投影 | 标记实体在对手手牌、摸牌、牌河和公开副露间转移；被鸣走只显示当前所在处；隐藏区域统一不可见，不含任何未标记暗牌 ID、排序下标或牌山位置 |
| 牌河位置 | 四个座位的首张、换行边界及末张定位；同种不同实体不混淆；座位、索引与实体 ID 一致；被鸣走立即清空位置、列表变化重新定位；跨局不保留旧位置；本地/UDP 编解码及重连保持位置；视角切换、动画和布局变化后仍标在正确实体上 |
| 信息隔离 | 四个座位各自拥有最多 12 张、部分目标跨座位重叠；逐座位 JSON 仅有本人效果；公开观战快照、事件、恢复包和历史接口不含私有标记信息 |
| 深复制 | 修改请求原列表、领域候选或投影来源集合不改变已安装组；旧快照不随跨局/到期漂移；公开 DTO 无新增可变内部引用 |
| 同步恢复 | 满 12 张视图下的 UDP 丢包/乱序/重复 PUSH/分片、全量替换、再次重连；本地与 UDP 语义一致；空效果及单组过期清除对应旧缓存；旧绑定回调不能把标记带到新房间或观战状态 |
| UI | 四牌上限、取消、排序、双击、在途锁、超时关闭、拒绝后重选、快照先于回执、有效期文案、跨区域/跨局动画清理、桌面与触摸操作 |

最终验证包括不筛选的全量单元/模块测试、独立架构与隐私审查、桌面构建、Android 调试 APK 构建及真实桌面 GL 冒烟。实现完成前不填写通过数量；Android 触控若未在设备验证，应与构建结果分开记录。

## 参考源码

- [权威门面 TableAuthority](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)
- [整场状态 MatchSession](../../engine/src/main/kotlin/com/majiang/match/MatchSession.kt) 与 [ApplicationCoordinator](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt)
- [单局规则入口 RoundRuleService](../../engine/src/main/kotlin/com/majiang/round/RoundRuleService.kt)
- [Tile 实体身份](../../engine/src/main/kotlin/com/majiang/model/Tile.kt) 与 [TilePool](../../engine/src/main/kotlin/com/majiang/model/TilePool.kt)
- [DecisionClock](../../engine/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 与 [CheatEconomyConfig](../../engine/src/main/kotlin/com/majiang/economy/CheatEconomyConfig.kt)
- [PlayerSnapshot](../../engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt) 与 [公开投影](../../engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt)
- [线路 SessionApi](../../protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt)、[请求信封](../../protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)、[客户端 GameSession](../../client/src/main/kotlin/com/majiang/client/api/GameSession.kt)
- [GameScreen](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 与 [V4 架构规范](../GAME_ARCHITECTURE_V4.md)
