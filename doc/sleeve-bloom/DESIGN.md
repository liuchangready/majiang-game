# 袖里藏花千术设计

日期：2026-10-12。状态：设计修订及实现已完成，[Codex最终独立验收通过](ACCEPTANCE.md)：1110项测试、双端构建和两种尺寸/断线真实GL通过。Anti分阶段实现engine、protocol/server/client及表现层，Codex编写60项新增测试并独立检视返修。本文规定当前契约；阶段历史见[执行状态](STATUS.md)，图形展示见[袖里藏花图形设计](VISUAL-DESIGN.md)。Android真机触控未验证。

袖里藏花是继开局码牌（偷天换日、瞒天过海）与落汗之后第三个接入出千经济的运行时千术：玩家在**本人摸牌之后、出牌之前**的窗口消耗 10 个额度，同时获得两张候选牌——原摸到（或杠后补到）的第一张，以及一张按方位规则确定的第二张——二选一保留；未选中的那张留在或退回指定位置，除候选交换及规定的岭上指针回退外，其余牌序不变，其余流程照常继续。

命名说明：本千术与既有四大绝技概念中的「袖里乾坤」（`SLEEVE_SWAP`，手牌与藏牌互换）与「移花接木」（`WALL_STEAL`，牌山偷摸换牌）仅名称相近，互不依赖。四大绝技仍走休眠的 `UseCheatTechnique` 通用链路；袖里藏花与落汗一样走专用强类型请求，不复活通用链路。正常摸牌路径复用 `TileMovement.swapHandWithWall` 物理原语（该原语最初为移花接木准备），但入口、校验与记账完全独立。

## 已确认的玩法

以下规则已由用户逐条确认：

- 使用时机：本人回合，摸牌之后、出牌之前。正常摸牌与杠后补牌（暗杠、大明杠、加杠三类）都适用。
- 使用后可多拿一张牌：即一次获取两张候选牌，玩家先选择保留哪一张。
- 第二张候选牌按方位确定：
  - 正常摸牌：拿到的是**后面一张**——本次摸牌位置（活牌山头端）的下一张。
  - 杠后补牌：拿到的是**前面一张**——杠补牌发生在牌山末端的王牌区，第二候选是紧邻王牌区靠牌山一侧的那张，即本次杠补牌事务中被从活牌山末端拉入王牌区的**替补牌**。
- 剩下的一张**退回牌中相应的位置**：具体槽位与指针语义见下文；除这次候选交换及规定的岭上指针回退外，其余牌序不变，牌山剩余数与王牌有效张数均不变。
- 其余流程照常继续：二选一结束后照常出牌、杠、自摸或立直，不额外占用或跳过任何回合环节。
- 每次施术消耗 10 个出千额度。
- 二选一弹出后若玩家超时未选（10 点已扣、不退），默认保留第一张（原摸到或原补到的那张），对局面零扰动。

例如，南家正常摸牌后点击「袖里藏花」：扣 10 个额度，翻开牌山头端下一张作为第二候选。南家若保留第二张，则原摸到的那张退回牌山头端，成为下一次从头端摸到的牌；若保留原摸牌，第二候选留在牌山原位。无吃碰杠等干预且对局继续时，由下家摸到该头端牌。杠后例：北家暗杠补到岭上张后施术，第二候选是本次杠补牌刚从牌山末端拉入王牌的替补牌；北家若保留替补牌，则岭上张退回岭上槽，成为下一次合法杠补到的那张；若保留岭上张，替补牌留在王牌区，与未施术的牌局物理状态一致。

「退回相应位置」指上述例子中的指定槽位：正常摸牌保留第二张时，原摸牌成为活牌山头端下一张；杠后保留第二张时，原岭上张成为下一次合法杠补的牌。保留第一张时第二候选原位不动，杠后的替补牌仍在替补列表中，不会成为下一张岭上牌。**除这两张候选的交换与明确规定的岭上指针回退外，其余牌序不变**，不能声称交换后的后续抽取序列与未施术时完全一致。若本局不再允许杠，退回的岭上张留在王牌，不保证再次被摸出；正常摸牌的实际领取者也由后续吃碰杠与回合推进决定，不保证一定是下家。

## 首版设计约束与检视口径

以下是基于已确认玩法的首版设计约束，包含本次检视修订，不代表用户逐项明确指定。实现时按这些契约交付；如需改动，应在交付说明中提出并由 Codex 检视，不能默默改动已确认规则。

| 项目 | 建议 | 原因与影响 |
|---|---|---|
| 每次摸牌限一次 | 同一次摸牌（含杠补牌）只能成功激活一次袖里藏花，激活即标记 | 两张候选已穷尽本次摸牌的全部方位信息，重复激活无新候选；防止反复试探扣费 |
| 立直后 | 不能施术 | 立直后自动摸切，玩家无选择权；与落汗的立直锁定一致 |
| 庄家起手第 14 张 | 不可施术 | 该张由发牌产生而非摸牌；且首巡即翻看牌山次张会过度放大先手信息 |
| 与落汗同窗并用 | 允许同一窗口先落汗再袖里藏花（或反之），各自扣费、各自重键计时 | 两千术互不排斥；换牌后落汗按实体牌 `tileId` 追踪，语义自洽无需特判 |
| AI | 首版 AI 不主动施术；托管期间挂起的二选一由超时结算保留第一张 | 不把策略开发与规则交付绑定；AI 只读自己获准的投影 |
| 二选一截止时长 | 不设独立软窗口，截止时间即该座当次决策计时截止；超时先结算「保留第一张」再执行默认摸切 | 零新增计时状态，暂停语义随 DecisionClock 天然冻结；若评审要求「10 秒二选一」手感，需另加 `choiceWindowMillis` 软窗口与暂停补偿，列入后续检视 |
| 旧协议线兼容 | `sleeveBloomConfig` 默认关闭；能力集新增 `sleeveBloomSupported`，缺失按 false 处理 | 与 `tileMarkSupported` 协商模式一致；产品组装处是否默认开启与落汗保持同策略 |
| 换牌后的动作与预览 | 保留第二张后，全部合法动作与立直待牌、听牌等预览按新手牌重算 | 保留回合与原计时，不保留旧动作选项；不能预先承诺预览代码无需修改 |
| 千术事件 | 首版不新增 `SleeveBloomOffered` / `SleeveBloomResolved` 等私有事件，私有快照驱动反馈 | 与落汗先例一致；若后续需要事件回放精细补偿，再按 GameEvent、快照函数、脱敏、公开投影、协议映射五处联动补做 |
| 扣费与退款 | 激活成功即扣 10 个额度，超时保留第一张或主动取消均不退款 | 用户已确认「超时 10 点已扣」；激活即获得第二候选信息 |
| 四杠与流局 | 第 4 次杠补完成且仍在合法窗口时可以施术；回退岭上指针不减少实际杠次数、不开放第五杠 | 杠入口同时检查 `totalKanCount < 4`、岭上槽位与活牌山；终局事务清除 offer，新局复位全部袖里藏花字段 |
| 断线与托管 | offer 保留到选择完成、原决策超时或终局；截止前恢复真人控制可按当前身份继续选择 | 断线不等于清除 offer；托管后须显式收回控制权，旧代次未执行请求拒绝，恢复不补发旧选择 |

额度沿用现有千术点账本，不新增货币。费用以 `CheatEconomyConfig.costs["SLEEVE_BLOOM"]` 为唯一配置来源，启用时必须显式为 10；缺失或其他数值拒绝配置，避免缺省零费用误开免费技能。新增 `SleeveBloomConfig(enabled = false)` 只控制功能开关；每次 10 个额度、候选方位规则、超时保留第一张均为本版固定规则。启用时必须同时启用千术经济（`MatchConfig.validateSupported` 与 `ProtocolMatchConfig` 双重校验）。

首版平衡边界：本功能提供进张选择与额外牌面信息，不接入休眠的视线、怀疑度或抓包判定，AI 首版不主动使用。因此真人与 AI 的技能使用能力不对称；10 点是固定玩法参数，不代表已经完成平衡验证，后续通过对局数据评估。

## 二选一流程与退回语义

### 两步交互

袖里藏花是「激活 → 选择」两步交互，不占用也不替换回合决策：

1. **激活**：玩家在摸牌后的窗口提交激活请求。权威校验通过后扣 10 个额度，创建挂起的二选一（offer），并通过私有快照向本人揭示第二候选牌。**此步没有任何牌的物理移动**：第二候选仍留在牌山或王牌区原位，仅对本人可见。
2. **选择**：玩家从两张候选牌中选定一张。保留第一张时零物理变化；保留第二张时执行一次交换（见下）。选择完成、超时结算或局终清理后，挂起状态清除，玩家继续正常的出牌决策。

挂起期间，该玩家的其他一切动作（出牌、杠、自摸、立直、落汗施术）都被权威拒绝，必须先完成二选一；同一时刻至多存在一个挂起二选一，且只属于当前行动玩家。

### 退回语义与守恒

物理牌库遵守既有两条王牌不变量（`StateInvariants`）：王牌列表恒 14 张（岭上张取走后列表保留历史引用，仅指针推进，已取部分不再算作王牌物理归属）；王牌有效张数 = 王牌列表未取部分 + 替补列表张数，恒为 14。

| 情形 | 第一候选 | 第二候选 | 保留第一张 | 保留第二张 |
|---|---|---|---|---|
| 正常摸牌后 | `drawnTile`（刚从头端摸到的） | `wall[wallPointer]`（头端下一张） | 零物理变化，第二候选留在牌山成为下一摸 | 两张内容对调：第二候选入手，原摸牌写入牌山头端槽位，摸牌指针不动，牌山剩余数不变 |
| 杠后补牌后 | `drawnTile`（刚补到的岭上张） | 本次 `drawRinshan` 从 `wall[wallEndExclusive-1]` 拉入王牌的替补牌 | 零物理变化，替补牌留在王牌区 | 岭上张退回岭上槽（岭上指针回退一位），替补牌从替补列表移出入手；王牌列表仍 14 张、有效张数仍 14，活牌山两端指针与内容不动 |

正常摸牌路径复用既有原语 `TileMovement.swapHandWithWall(state, player, handTile = 当前 drawnTile, wallIndex = wallPointer)`（该原语已特判摸牌槽：交换后 `drawnTile` 直接变为牌山取出的那张，双向记账）；杠后路径新增一个专用原语（见权威接口）。两条路径都须经过 `TilePool` 双向转移与守恒审计，136 张实体牌的位置账本前后一致。保留现有物理不变量，不以审计器零改动为目标；新增 offer 与杠次数语义应补充断言和测试。

**岭上位置与杠次数必须分离**：`rinshanPointer` 只表示下一张岭上牌的位置，袖里藏花可令其回退；`totalKanCount` 与 `playerKanCounts` 仍记录已完成的杠，施术不得减少它们，也不撤回已翻开的宝牌指示牌。暗杠、加杠、大明杠的候选生成与执行校验均须同时满足 `totalKanCount < 4`、`rinshanPointer < 4`、`wallRemaining > 0` 及原有规则。包括响应窗口中的大明杠与抢杠结束后的加杠提交，均不能绕过次数门禁。物理原语保持槽位检查，业务门禁在声明或提交新杠之前执行，不能在已递增为 4 的第四次补牌中错误拒绝。即使四杠由同一人完成、对局未流局且岭上指针已回退，也不得生成或接受第五杠。

一个必须保证的时序：权威超时裁决（默认摸切）在应用默认动作**之前**先结算挂起的二选一（保留第一张），否则「挂起期间其他动作被拒」会让默认动作也被拒，对局卡死。结算保留第一张后紧接摸切第一张，恰好等于未施术时的超时行为，零扰动。

### 换牌后的和牌与役种

按「其余流程照常继续」确定首版口径：施术不视为一次新的摸牌或鸣牌，不增加 `turnNumber`，不改变已有摸牌来源、首巡未中断标志或一发状态；保留第二张后，以新 `drawnTile` 和当前手牌重新检查和牌形、役种、符番与得点。杠后换入替补牌仍沿用 `isRinshanDraw = true`，合法自摸可计岭上开花；非庄家首次摸牌后换牌，仍满足既有首巡条件时可计地和。正常来源不会因换牌获得岭上标志；海底、河底仍按既有摸牌来源和活牌山余量判断。庄家起手第 14 张禁止激活，不通过本功能增加天和机会。上述为明确的设计口径，必须以实际算分测试验证，不能只测预览。

## 当前架构与实现入口

| 现有代码 | 对本功能的约束 |
|---|---|
| `TableAuthority` | 对 server 的领域门面；`coordinator` 为 `internal`，不得重新公开 |
| `RoundRuleService.validatePreconditions()` | 永久拒绝通用 `UseCheatTechnique`，袖里藏花必须走专用请求，不能只加一个枚举 |
| `TileMovement.swapHandWithWall()` | 已支持手牌与活牌山任意槽位交换并特判摸牌槽，正常摸牌路径直接复用 |
| `TileMovement.drawRinshan()` | 杠后补牌同时拉替补入王牌；第二候选（替补牌）与岭上张的来源均在此事务内 |
| `LegalActionProvider` / `RoundRuleService` / `RoundTransitionPlanner` | 杠次数门禁与岭上指针分离；候选生成、响应及最终提交都禁止第五杠 |
| `StateInvariants` | 王牌恒 14 的两条不变量与活牌山审计区间 `[wallPointer, wallEndExclusive)` 是交换设计的硬边界 |
| `GameState` | 挂起二选一是单局内状态，归属单局状态机，不进跨局的 `MatchSession`/`SessionChange` |
| `TableAuthority.checkTimeout()` | 权威超时默认摸切；必须先结算挂起二选一再执行默认动作 |
| `DecisionClock.rekey()` | 已支持换决策键并完整保留 `opensAt`、`deadline` 与储备，施术与选择各触发一次 |
| `SessionUpdate.redactForViewer()` / `PublicProjectionFactory` | 他家摸牌事件已脱敏、公开投影白名单默认丢弃未知事件——袖里藏花零新增事件即可保密 |
| `GameScreen` | 入口按钮、模态互斥、专用请求提交与 toast 拒绝原因均有落汗先例可循 |

袖里藏花采用专用强类型请求（激活与选择各一个），不通过 `Map<String, Any>` 携带参数，也不为二选一生成 `optionId` 候选。普通弃牌、鸣牌和立直继续使用原 `submit(choice)`。

## Engine 领域设计

### 状态归属

拟新增 `com.majiang.cheat.bloom` 包，包含：

| 类型 | 职责 |
|---|---|
| `SleeveBloomConfig` | 功能开关（`enabled = false` 默认关闭） |
| `SleeveBloomDrawKind` | 枚举 `DEAL_EXTRA` / `NORMAL` / `RINSHAN`，标记最近一次摸牌来源 |
| `SleeveBloomKeep` | 枚举 `FIRST` / `SECOND` |
| `SleeveBloomRequest` | 激活请求值对象：`requestId`、`sessionId`、`roundId`、`decisionId`、`controlEpoch`，防御性样式仿 `TileMarkRequest` |
| `SleeveBloomChoiceRequest` | 选择请求值对象：上述五元组再加 `offerId`、`keep` |
| `SleeveBloomOffer` | 挂起的二选一：`offerId`（`bloom-$seat-$requestId`）、`seat`、`drawKind`、`firstTile`、`secondTile`、`createdTurnNumber`；归属所在单局，不保存可过期的决策键或控制代次，第二候选物理上不动，仅逻辑引用 |
| `SleeveBloomView` / `SleeveBloomOfferView` | 观察者私有视图（见「私有可见信息」） |
| `SleeveBloomProjectionFactory` | 从已提交领域状态构造单观察者可见信息 |

与落汗不同，挂起二选一**不进 `MatchSession`**：落汗标记跨局持续才需要整场状态与专用 `SessionChange`；袖里藏花的 offer 生命周期严格在单局、单次摸牌之内，存入 `GameState` 三个新字段，随 `RoundChange` 原子安装，随深拷贝复制、随 `resetForNewRound` 复位，不触碰整场状态机：

```kotlin
// GameState 拟新增字段
var sleeveBloomOffer: SleeveBloomOffer? = null   // 当前挂起的二选一；null 表示无
var sleeveBloomUsedOnTurn: Int = -1              // 已使用的摸牌序号（turnNumber）；每次摸牌限一次
var lastDrawKind: SleeveBloomDrawKind = SleeveBloomDrawKind.DEAL_EXTRA
```

`lastDrawKind` 覆盖全部七处写入：两条发牌路径 `dealExtra` 之后置 `DEAL_EXTRA`（庄家起手第 14 张）；`DrawTile` 与自动摸牌 `advanceToNextDraw` 置 `NORMAL`；三处 `drawRinshan`（暗杠、大明杠、加杠）置 `RINSHAN`。既有 `GameState.isRinshanDraw` 保留并与来源一致。当前所有正常摸牌及三种杠补牌路径均递增 `turnNumber`，故它可作为本局摸牌序号；激活与选择不递增它，连续杠每次新补牌仍可各施术一次。新增摸牌路径也须遵守此约束。

### 权威接口

拟增加（`seat` 与 `now` 由可信宿主传入）：

```kotlin
// TableAuthority 拟新增成员
fun submitSleeveBloom(seat: Int, request: SleeveBloomRequest, now: Long): MutationResult
fun submitSleeveBloomChoice(seat: Int, request: SleeveBloomChoiceRequest, now: Long): MutationResult

// ApplicationCoordinator 拟新增成员（internal 语义不变）
fun submitSleeveBloom(seat: Int, request: SleeveBloomRequest, now: Long): ActionResult
fun submitSleeveBloomChoice(seat: Int, request: SleeveBloomChoiceRequest): ActionResult
internal fun resolveSleeveBloomKeepFirst(seat: Int): List<GameEvent>  // 超时/清理兜底：保留第一张并清除 offer

// TileMovement 拟新增原语（仅杠后路径需要）
fun swapDrawnWithRinshanReplacement(state: GameState, player: Int): Tile
// 前置：drawnTile 非空且池位置为该玩家手牌；岭上指针 ≥ 1；
//       deadWall[rinshanPointer - 1] 恰为当前 drawnTile；替补列表非空且末位池位置为王牌
// 效果：替补列表移除末位；岭上指针回退；drawnTile 变为替补牌；
//       岭上张转回王牌、替补牌转入该玩家手牌
```

正常摸牌路径不新增原语，直接调用既有 `swapHandWithWall(state, seat, handTile = drawnTile, wallIndex = wallPointer)`。

查询通过 `querySnapshot()` 新增私有字段 `PlayerSnapshot.sleeveBloom`，不另开可绕过投影的全局查询接口。

**激活请求校验链**（幂等查询必须早于当前决策、代次与 deadline 校验）：

1. 校验可信宿主绑定、有效座位与整场请求作用域；不允许观战者、他人或旧连接借回执查询绕过身份校验。沿用宿主既有 `STALE_SESSION` / `SEAT_DISCONNECTED` / `SEAT_NOT_HUMAN` 入口门禁。
2. 在该整场与座位作用域查幂等账：同 `requestId`、操作类型与完整载荷指纹一致即返回原回执；不同则返回 `REQUEST_ID_CONFLICT`。命中不执行后续变更，即使当前决策已更新、offer 已结束或 deadline 已过。宿主入口拒绝与关闭后不可访问的会话不承诺回执回放。
3. 仅首次请求校验功能开关、经济开启与费用显式为 10；否则 `SLEEVE_BLOOM_DISABLED`。会话未关闭、未暂停，`roundId`、`decisionId`、`controlEpoch` 匹配当前权威值；不匹配返回既有原因码。
4. 权威计时存在、计时键匹配当前决策且 `now < deadline`；到期返回 `DEADLINE_EXPIRED`。
5. 控制者为真人（本地或远端），非托管非观战；否则 `ACTION_NOT_AVAILABLE`。
6. 本人为当前行动玩家、`phase == CHEAT_WINDOW` 且无响应窗口；否则 `NOT_YOUR_TURN` / `WRONG_PHASE`。注意窗口比落汗更严：吃碰后的 `DISCARD` 阶段没有摸牌语义，不可施术。
7. `drawnTile != null`；否则 `SLEEVE_BLOOM_NO_DRAWN_TILE`。
8. 未立直；否则 `SLEEVE_BLOOM_RIICHI_LOCKED`。
9. `lastDrawKind != DEAL_EXTRA`；否则 `SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED`。
10. 无挂起二选一；否则 `SLEEVE_BLOOM_ALREADY_PENDING`。
11. `sleeveBloomUsedOnTurn != turnNumber`；否则 `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW`。
12. 正常摸牌来源时 `wallRemaining >= 1`；否则 `SLEEVE_BLOOM_WALL_EMPTY`。杠后来源须验证原岭上槽与本次替补实体的归属，不能仅因来源枚举为 `RINSHAN` 就跳过物理前置校验。
13. 余额 ≥ 10；否则 `INSUFFICIENT_FUNDS`。
14. 委托协调器在锁内复核业务状态与物理前置条件后执行。首次领域处理产生的成功或拒绝回执写入幂等账；宿主绑定或作用域失败不污染合法座位的请求账。

**选择请求校验链**：先执行同样的可信作用域与幂等查询，再校验首次请求的功能、当前身份、计时与真人控制；不重复扣费，也不重复要求激活前的余额或“尚未使用”。另要求：

1. 存在挂起二选一且属于本人；否则 `SLEEVE_BLOOM_NO_PENDING_OFFER`。
2. `offerId` 匹配；否则 `SLEEVE_BLOOM_OFFER_MISMATCH`。
3. 局面一致性：仍处于本人 `CHEAT_WINDOW`，摸牌序号与来源匹配，`drawnTile` 恰为 `offer.firstTile`，第二候选仍在指定槽位且实体归属一致。否则 `SLEEVE_BLOOM_STALE_OFFER`，原子清除失效 offer 并推送新快照，不交换、不退款；此清理不代表决策已经超时，不得提前执行默认摸切或把第一张重新写回已变化的手牌。
4. 选择请求的 `decisionId` / `controlEpoch` 必须来自当前权威快照；offer 本身只绑定本局、座位与这次摸牌。激活后的决策重键或截止前显式收回控制权，不会使仍有效的 offer 永久绑定旧身份。

### 原子性与计时

激活与选择各在协调器现有写锁与提交保护内完成，复用 `submitTileMark` 的原子提交模式：在候选深拷贝上执行全部变更，通过物理及袖里藏花语义审计后随一次 `RoundChange` 原子安装并使决策注册表失效。激活包含扣费流水、使用标记与 offer，且仅此步发布一次 `GameEvent.CheatPointsChanged`；选择包含必要的交换与清除 offer，不再发布扣费事件。禁止先单独调用会提交并广播的 `applyCheatLedgerEntry()` 再改状态，避免「已扣点却没有 offer」的半提交。选择或清理即使没有新事件，也须按状态变更推送完整快照。

费用流水 `entryId = "bloom-$seat-$requestId"`，幂等重试只扣一次。费用统一从 `config.cheatEconomy.costs["SLEEVE_BLOOM"]` 读取（落汗现状是门面读配置、协调器硬编码 10 的不一致，袖里藏花不沿用，两侧都从配置读，并由 `MatchConfig` / `ProtocolMatchConfig` 的显式等于 10 校验兜底）。

袖里藏花是出牌前的补充操作，不完成弃牌决策、不切换行动玩家。激活与选择各使 `roundVersion` 递增、`decisionId` 变化，须对仍有效的原决策各调用一次 `DecisionClock.rekey()`，完整保留 `opensAt`、`deadline` 与储备基数，不重置也不赠送思考时间。先重建决策、重键原时钟，再同步宿主时钟与发布快照，不能让通用同步路径将旧时钟取消后重新开表。

激活未改手牌，可保留该手牌对应的规则候选供计时及超时策略使用，但挂起期间普通动作统一由 offer 门禁拒绝。选择完成后必须从实际保留牌重算全部合法动作：弃牌实体、自摸、暗杠/加杠、立直与九种九牌等均以新状态为准；重新生成决策与选项，旧 `decisionId` / `optionId` 不得继续使用。保留第一张时规则候选内容可以相同，决策身份仍更新。立直待牌、听牌预览与最终算分同步采用新手牌。

### 幂等和清理

回执层次：下述幂等账保存领域回执，重复请求的status/reason及业务结果不变。宿主为线路回执附加私有流cursor：成功回执保留首次发出的cursor；拒绝回执标注返回时的当前cursor，该同步元数据不作为动作已生效的凭据，客户端状态始终以有序完整快照为准。

领域去重按整场、座位与 `requestId` 生效，保存操作类型（激活/选择）与载荷指纹；同一 ID 用于另一类写操作（普通动作、洗牌方案、落汗）返回 `REQUEST_ID_CONFLICT`。合法重复请求返回原回执，即使 offer 已被结算，也不重新扣费或重新激活；相同 ID 改动任何字段同样冲突。UDP 传输层更严格的原始载荷冲突规则保留，自动重试必须复用完全相同的载荷。

清理路径全部幂等：超时先结算保留第一张，再基于结算后状态选择并执行默认动作（`checkTimeout` 与立直自动摸切两处同序，后者为纯防御），在同一次权威队列处理内完成。若清理更新决策身份，后续取最新决策并正确关闭对应时钟，不得沿用失效键导致超时不生效。和牌、四杠散了、荒牌等终局事务即清除 offer，不等待下一局；新状态机与 `resetForNewRound` 均复位三字段。换庄属于局间变化，不能继承上一局 offer。

暂停保留 offer，并随权威时钟冻结；恢复后按剩余预算继续。断线与 AI 托管本身不清除 offer，客户端只隐藏面板；托管 AI 不提交普通动作或选择，原 deadline 到期后权威保留第一张并默认摸切。宽限期内重连且仍为真人控制时可继续选择；已托管者必须先显式 `claimControl()`，在截止前收回后用完整快照中的当前决策、控制代次和同一 `offerId` 发起新选择，不重新扣费。未执行的旧代次请求拒绝，已执行请求重放按上述幂等规则仅返回旧回执。已超时则恢复结算后的快照。控制权切换若引起决策键变化，仍需重键保留原 deadline 与储备，不能借反复断线、托管或收回来延长选择时间。

`handleAction` 顶部增加兜底护栏：该座位存在挂起二选一时，直接拒绝其任何 `GameAction` 提交（`SLEEVE_BLOOM_CHOICE_PENDING`），覆盖权威超时裁决之外的直接调用路径。AI 决策入口同样防御（AI 不激活，纯兜底）。

## 私有可见信息

拟在 `PlayerSnapshot` 增加可空 `sleeveBloom: SleeveBloomView?`。功能关闭时为 `null`；开启时仅返回**观察者本人**的能力与挂起信息：

- `canApply`、明确的禁用原因、权威费用 10、当前摸牌来源 `drawKind`（无摸牌时为 `null`）。
- `decisionId`、`controlEpoch` 与状态版本，用于绑定入口按钮与阻止旧选择提交。
- `pendingOffer: SleeveBloomOfferView?`：`offerId`、`firstTile`、`secondTile`、`drawKind`、`deadlineMillis`（投影时从该座 `DecisionClock` 查询）、`decisionId`、`controlEpoch`。后两者从当前权威决策与控制者投影，必须与外层身份一致，不从激活请求复制。offer 不自带时钟；客户端结合快照的权威时间采样与暂停状态显示剩余时间，不能把服务端单调 deadline 直接与客户端时钟相减，也不能仅凭绝对 deadline 推断暂停已冻结。本人托管期间仍可保留该私有字段，能否显示或提交另由连接、暂停及控制状态门控。

保密规则：

- 本功能揭示的第二候选及其来源关系**只存在于 `pendingOffer` 之内**。激活前不得新增 `wall[wallPointer]` 或替补槽位的牌面揭示；既有落汗标记可以保留玩家已知的实体牌面，但不暴露它是否恰为第二候选。投影工厂的 `canApply` 分支只判断来源、数量、权限与费用，不读取或输出候选牌面。
- 他家本就看不到施术者的摸牌牌面（`TileDrawn` / `RinshanDraw` 已按座位脱敏），袖里藏花不新增私有候选事件；仍须审查快照、事件、回执和公开历史脱敏。其他观察者可见既有的 `CheatPointsChanged` 余额变化，以及各自原本获准的落汗投影变化，不承诺完全无感知，不额外揭示候选或交换去向。
- `PublicTableProjection`、`ProtocolPublicSnapshot`、公开历史结果均不新增字段。
- `PlayerSnapshot.init` 增加交叉校验（仿 `presentationPreview` 先例）：`pendingOffer` 非空时要求当前阶段为本人 `CHEAT_WINDOW` 且 `view.myDrawnTile` 的实体 ID 等于 `firstTile` 的实体 ID。
- 快照与嵌套集合深复制并只读包装；已发出的旧快照不随后续结算被反向改写。

与落汗并用时的可见性：保留第二张会替换 `drawnTile` 实体，落汗按 `(sessionId, tileId)` 跟踪实体，被换入牌山的已标记牌在追踪区自然转为「当前不可见」，被换入手牌的照常显示——语义自洽，无需特判。

## 协议与客户端 API

### 独立类型映射

领域、线路、客户端各自定义类型，不以 typealias 共用跨层 DTO：

| engine | protocol | client.api |
|---|---|---|
| `SleeveBloomConfig` | `ProtocolSleeveBloomConfig` | `SleeveBloomConfig` |
| `SleeveBloomDrawKind` | `ProtocolSleeveBloomDrawKind` | `SleeveBloomDrawKind` |
| `SleeveBloomKeep` | `ProtocolSleeveBloomKeep` | `SleeveBloomKeep` |
| `SleeveBloomRequest` | `ProtocolSleeveBloomRequest` | `SleeveBloomRequest` |
| `SleeveBloomChoiceRequest` | `ProtocolSleeveBloomChoiceRequest` | `SleeveBloomChoiceRequest` |
| `SleeveBloomOffer` | `ProtocolSleeveBloomOfferView` | `SleeveBloomOfferView` |
| `SleeveBloomView` | `ProtocolSleeveBloomView` | `SleeveBloomView` |

线路请求与客户端请求和领域请求业务字段一致；不能接受客户端指定座位、费用或效果结果。`EngineProtocolMapper` 与 `ProtocolViewMapper` 显式转换并校验所有新字段（含 `DrawKind` / `Keep` 枚举映射与 `Tile` 转换）。

新增 `SessionApi.submitSleeveBloom(request): CompletableFuture<ProtocolActionReceipt>` 与 `SessionApi.submitSleeveBloomChoice(request): CompletableFuture<ProtocolActionReceipt>`（接口默认抛 `UnsupportedOperationException`，与 `submitTileMark` 同式），以及 `client.api.GameSession.submitSleeveBloom / submitSleeveBloomChoice`。`ServerConnectionSession` 在 `client.session` 中实现：激活与选择**共用同一把在途请求锁**（`ACTION_IN_FLIGHT` 防双发，与出牌共享锁语义正确——同一时刻至多一个在途请求），沿用绑定代次校验、调度线程与关闭 Future 的既有规则。观战不提供该方法，底层伪造请求必须拒绝。

### 本地与 UDP 接线

新增 `ApiTags.SUBMIT_SLEEVE_BLOOM` 与 `ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE`，`ApiRequest` 增加 `sleeveBloom` / `sleeveBloomChoice` 两个载荷字段，更新已知 API 白名单。编解码互斥矩阵与落汗同式：两个新标签各自必含专属载荷、禁止携带 `choice` / `shufflePlan` / `tileMark` 及彼此载荷；两类新载荷只允许出现在各自标签下。各自新增专用 `TypeAdapter`（严格类型校验、枚举白名单、解析异常包装）并注册。

所有路径最终在 `ServerTableHost` 同桌串行队列里调用相同的 `TableAuthority` 入口：`EngineProtocolMapper` 双向映射（含快照尾部 `sleeveBloom` 视图转换）、`SharedRoom` 直通、`ServerTableHost.submitSleeveBloom / submitSleeveBloomChoice`（入队串行、`STALE_SESSION` / `SEAT_DISCONNECTED` / `SEAT_NOT_HUMAN` 三连拒、回执缓存键新增 `HostReceiptKey.Bloom`、状态变化时同步决策时钟并推送）、`InProcessBinding` 进程内同接口实现、`UdpRoom` 与 `UdpTableServer` 分发（后者 `spectatorForbiddenApis` 集合必须显式加入两个新标签，在入口拒绝观战伪造）。

能力集拟增加 `sleeveBloomSupported`，缺失按 false 处理；房间配置决定是否实际启用，启用校验（经济开启且费用显式为 10）在 `ProtocolMatchConfig` 与 `MatchConfig.validateSupported` 双侧执行。`ProtocolPlayerSnapshot` 增加可空 `sleeveBloom` 字段并做交叉校验（同引擎侧）。旧服务不支持时新 UI 禁用入口；首版仍要求各模块同版本升级，不承诺旧客户端加入已启用袖里藏花的房间。

### 拒绝原因

业务拒绝沿用 `Receipt.status = REJECTED`；结构错误与身份错误沿用协议错误路径。

| 原因 | 含义 |
|---|---|
| `SLEEVE_BLOOM_DISABLED` | 功能未启用 |
| `WRONG_PHASE` / `NOT_YOUR_TURN` | 不是本人摸牌后的千术窗口 |
| `SLEEVE_BLOOM_NO_DRAWN_TILE` | 当前没有刚摸到的牌 |
| `SLEEVE_BLOOM_RIICHI_LOCKED` | 本人已经立直 |
| `SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED` | 庄家起手第 14 张不可施术 |
| `SLEEVE_BLOOM_ALREADY_PENDING` | 二选一已在进行中 |
| `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW` | 本次摸牌已施术过 |
| `SLEEVE_BLOOM_WALL_EMPTY` | 牌山已摸完，没有第二候选 |
| `SLEEVE_BLOOM_NO_PENDING_OFFER` | 没有进行中的二选一 |
| `SLEEVE_BLOOM_OFFER_MISMATCH` | `offerId` 不匹配，选择已过期 |
| `SLEEVE_BLOOM_STALE_OFFER` | 局面已变化，选择失效 |
| `SLEEVE_BLOOM_CHOICE_PENDING` | 请先完成袖里藏花二选一 |
| `INSUFFICIENT_FUNDS` | 千术点不足 |
| `DECISION_MISMATCH` / `STALE_CONTROL_EPOCH` / `NO_ACTIVE_DECISION` | 局、决策或控制代次已变更 |
| `DEADLINE_EXPIRED` / `HOST_PAUSED` / `SESSION_CLOSED` | 已到截止、暂停或会话关闭 |
| `REQUEST_ID_CONFLICT` | 同一请求 ID 被用于不同操作或不同载荷 |

回执中不能带回请求者无权获知的任何牌山或王牌信息。

## 表现层设计

入口常驻按钮在落汗按钮下方，仅当权威 `canApply` 为真时可用；点击即提交激活请求，不乐观扣点。激活成功后由私有快照的 `pendingOffer` 驱动弹出二选一面板：两张候选牌并排展示，点击选中，确认「保留所选」或取消「保留第一张」，面板右上显示权威倒计时。选择提交后，`pendingOffer` 消失、`myDrawnTile` 更新为保留牌（沿用既有摸牌槽与金色高亮），面板自动关闭，继续正常出牌。

二选一面板与立直选牌、落汗选牌、鸣牌选择互斥，注册进 `isModalActive()`；期间手牌点击与其他行动按钮全部挂起（引擎侧同时拒绝，双保险）。决策、局、控制代次或候选变化后本地旧选中状态立即失效；断线、托管、暂停时隐藏面板，但不据此清除领域 offer。恢复真人控制与有效快照后，尚未结束的 offer 重新展示，玩家重新选择，不补发旧选择。未保留的牌不做退回去向展示（牌墙无图形、牌山剩余数不变）。提交使用现有 `isPending` 门控防重复；回执与快照乱序到达时以权威快照为准；领域拒绝经原因码中文表以 toast 提示。

具体几何、文案与验收口径见[袖里藏花图形设计](VISUAL-DESIGN.md)。

## 分阶段交付

| 阶段 | 修改范围 | 完成条件 |
|---|---|---|
| 第一阶段 Engine | 状态字段、原语、权威入口、原子扣费、私有投影、超时联动与领域测试 | 无 UI/网络也能完整验证激活、二选一、退回语义、守恒、隐私、时钟与超时结算；评审并冻结领域契约 |
| 第二阶段 接口与 API | protocol DTO/编解码、server 映射与本地/UDP 接线、client.api 门面与视图、client 实现及恢复缓存 | 真实本地/UDP 链路能施术、拒绝伪造、恢复完整效果，完成接口文档及集成测试；通过后才接展示 |
| 第三阶段 表现层 | 入口、二选一面板、输入锁、字体文本及桌面/移动布局 | 真实牌桌完成激活到选择再出牌的闭环，超时/断线/观战均正确 |

每阶段先完成自己的行为验证再进入下一阶段；不能先在 UI 假翻牌，或把未接线的 DTO 当成功能完成。现有麻将规则、算分、牌山、出千账本、UDP 可靠性与控制权测试均须保留。

## 验收清单

| 组别 | 必须覆盖的情况 |
|---|---|
| 候选与退回 | 正常摸牌后激活：第二候选恰为头端下一张；保留第一张零扰动；保留第二张后原摸牌成为下一次头端摸牌（无干预时下家实际摸到）；三种杠各自激活：第二候选恰为本次替补牌，保留替补后岭上张成为下一次合法杠补牌，保留第一张则替补不成为岭上候选；其余牌序保持 |
| 守恒 | 每次变更后 136 张位置账本一致；王牌列表恒 14、有效张数恒 14；活牌山区间与两端指针、牌山剩余数前后一致；既有物理审计与新增 offer、杠次数语义断言全过 |
| 使用权限 | 他人回合、吃碰后的 DISCARD 阶段、响应窗口、抢杠窗口、无摸牌、立直后、庄家起手第 14 张、牌山摸完（正常摸牌来源）、准备阶段、暂停、结算、托管及观战均拒绝；绕过 UI 发请求仍拒绝 |
| 原子与费用 | 每次成功激活恰好扣 10，选择不再扣费；余额 9 拒绝、10 可激活且扣至 0 后仍可选择；重试只扣一次；激活校验失败、版本冲突不留 offer 或半笔流水；选择失败不交换，失效 offer 按规定清理；缺失费用键或费用不为 10 的启用配置拒绝；激活后超时或取消不退款 |
| 二选一挂起 | 挂起期间出牌、杠、自摸、立直、落汗提交均被 `SLEEVE_BLOOM_CHOICE_PENDING` 拒绝；同一摸牌重复激活拒绝；`offerId` 不匹配与局面漂移（`drawnTile` 已变化）拒绝 |
| 超时与时钟 | 决策超时先结算保留第一张再摸切，行为与未施术的超时完全一致；激活与选择各触发一次 rekey，`opensAt`/`deadline`/储备不重置；恰好到期拒绝；暂停冻结、恢复继续 |
| 杠次数与流局清理 | 连续杠每次新补牌可独立施术；第四次补牌后合法窗口可选第二张，指针回退但总杠数、各家杠数、宝牌不回退；同一人四杠继续对局时，他家第五杠的候选与伪造提交均拒绝，覆盖暗杠、加杠、大明杠及实际存在的牌型；终局事务立即清 offer，新局/连庄复位全部字段 |
| 投影与隐私 | 激活前不额外揭示第二候选；`pendingOffer` 仅本人快照出现且 `firstTile` 与 `myDrawnTile` 实体一致；嵌套身份与当前决策一致；他家、观战、公开投影、历史接口无候选信息泄漏；既有余额与各自获准的落汗信息照常投影 |
| 幂等与竞态 | 激活/选择成功回执丢失后，原载荷在决策更新、offer 清除或超时后重放仍返回原回执；首次拒绝重放一致；改载荷与跨 API 撞 ID 冲突；入口身份拒绝不污染他人账；激活与弃牌/超时/托管并发按队列顺序生效；UDP 丢包、乱序、重复与分片恢复 |
| 恢复与控制权 | 宽限期内重连继续选择；托管后未收回不允许选择，截止前显式收回按新代次继续；未执行旧请求拒绝，已执行重放无副作用；截止后恢复结算局面；暂停恢复保留 offer；反复控制权变化不重置 deadline、储备或扣费 |
| 动作、预览与并用 | 换牌使自摸、杠、立直等选项新增或消失；新弃牌选项包含新摸牌实体且不含已换出实体；旧决策选项拒绝；听牌预览随新快照重算；与落汗同窗先后施术互不干扰，换牌后落汗追踪区状态正确迁移 |
| 役种与算分 | 杠后保留替补牌自摸仍按岭上来源算分，正常来源不获岭上役；非庄首摸换牌满足条件可计地和；不满足首巡条件不得计地和；海底/河底沿用原规则；庄家起手激活拒绝；核对最终役种、符番、得点与预览一致 |
| UI | 入口按钮可用性、二选一面板选中/确认/取消/倒计时、模态互斥、超时自动关闭、拒绝 toast、桌面与触摸操作 |

最终验证包括不筛选的全量单元/模块测试、独立架构与隐私审查、桌面构建、Android 调试 APK 构建及真实桌面 GL 冒烟。实现完成前不填写通过数量；Android 触控若未在设备验证，应与构建结果分开记录。

## 参考源码

- [权威门面 TableAuthority](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)
- [应用协调 ApplicationCoordinator](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt) 与[单局状态机 RoundStateMachine](../../engine/src/main/kotlin/com/majiang/round/RoundStateMachine.kt)
- [物理移动原语 TileMovement](../../engine/src/main/kotlin/com/majiang/model/TileMovement.kt)、[Tile 实体身份](../../engine/src/main/kotlin/com/majiang/model/Tile.kt) 与 [TilePool](../../engine/src/main/kotlin/com/majiang/model/TilePool.kt)
- [单局状态 GameState](../../engine/src/main/kotlin/com/majiang/state/GameState.kt) 与[守恒审计 StateInvariants](../../engine/src/main/kotlin/com/majiang/state/StateInvariants.kt)
- [DecisionClock](../../engine/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 与 [CheatEconomyConfig](../../engine/src/main/kotlin/com/majiang/economy/CheatEconomyConfig.kt)
- [落汗领域包 cheat.mark（接线模板）](../../engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkModels.kt)
- [PlayerSnapshot](../../engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt) 与[公开投影](../../engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt)
- [线路 SessionApi](../../protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt)、[请求信封](../../protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)、[落汗协议契约（模板）](../../protocol/src/main/kotlin/com/majiang/transport/api/ProtocolTileMarkModels.kt)
- [客户端 GameSession](../../client/src/main/kotlin/com/majiang/client/api/GameSession.kt) 与[落汗客户端契约（模板）](../../client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt)
- [GameScreen](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 与 [V4 架构规范](../GAME_ARCHITECTURE_V4.md)
