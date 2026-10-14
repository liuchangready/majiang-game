# 偷梁换柱千术设计

日期：2026-10-13。本文为**已实现并独立验收的设计基线**（见[最终验收](ACCEPTANCE.md)），已按用户补充要求与Codex检视修订。偷梁换柱是继开局码牌、落汗与袖里藏花之后第四个接入出千经济的运行时千术：玩家在**本人摸牌之后、出牌之前**消耗10个额度，把**原有暗手牌中的一张**与合法候选槽位原地对调。当前刚摸到／刚杠补到的 `drawnTile` 和任何副露牌（包括暗杠、明杠、加杠）均不得参与交换。正常摸牌候选为活牌山后续八张，杠后候选为死山（王牌区）全部未展示牌；候选默认牌背，仅本人有效落汗标记命中时亮面，其余可盲选。交换不改变取牌指针、剩余数、当前摸牌槽或翻牌记录。

**命名决策（2026-10-13 用户确认）**：用户原始提案名为「偷天换日」，与已上线的开局码牌千术「偷天换日」（影响起手配牌，40 点，见 `ShuffleSelection` 与洗牌准备面板）重名；经确认本千术定名**「偷梁换柱」**，技术标识 `BEAM_SWAP`，开局码牌两项命名不动。本千术与已移除的早期四大绝技（移花接木、袖里乾坤）概念仅语义相近、互不依赖；与落汗、袖里藏花一样走专用强类型请求，不复活旧通用链路。正常摸牌路径复用 `TileMovement.swapHandWithWall` 物理原语（该原语最初为移花接木准备、袖里藏花已复用），杠后路径新增一个专用原语。

## 已确认的玩法

以下规则已由用户于 2026-10-13 逐条确认：

- 一个千术、两个施术窗口，窗口由最近一次摸牌来源决定：
  1. **正常摸牌后**：可以将**原有暗手牌中的一张**与**活牌山后续八张牌**中的任意一张互换；
  2. **杠后补牌后**（暗杠、大明杠、加杠三类）：可以选择**死山上没有展示的任意牌**进行替换。
- **参与交换的牌（2026-10-13 用户补充确认）**：手牌侧只允许本次摸牌前已有、当前仍在本人 `closedTiles` 中的暗牌；本次刚摸到或刚杠补到的 `drawnTile` 禁止换出，任何吃、碰、暗杠、明杠、加杠中的牌禁止换出。牌山／死山侧不得把已取出的手牌、副露牌、牌河牌或已取岭上槽的历史引用当成候选。标记亮面不豁免这些限制。这里按当前区域和本次摸牌槽判定，不是永久禁止一张曾在以前回合被摸过、现已合法回到牌山的实体。
- **候选可见性（2026-10-13 用户补充确认）**：换牌时候选牌一律背面展示，**除了此前已经落汗标记的牌，其他的牌看不到牌面**；本人标记命中的候选亮面，未标记候选仍可选择（盲换）。
- **与袖里藏花互斥（2026-10-13 用户补充确认）**：同一次取牌中偷梁换柱与袖里藏花**不可以同时使用**，二选一；跨取牌不受影响。
- 每次激活消耗 **10 个出千额度**（用户确认与落汗、袖里藏花拉平）。
- 激活成功即扣费；超时未选或主动取消不退款。

例如：南家正常摸牌后施术，面板给出活牌山接下来 8 张的牌背；其中「下 3」因南家此前落汗标记亮出五万，南家把原有暗手牌中的北风与它对调——北风躺在牌山该槽位等待后续某次摸牌取走，五万入手成为新手牌的一部分；其余 7 张始终未见牌面。北家大明杠后补到岭上牌 A，施术面板给出死山全部暗牌的牌背（按岭上槽/宝牌暗区/替补区分组）；若某里宝槽经标记亮面，可把原有暗手牌中的一张孤立字牌与之对调，刚补到的A和大明杠副露保持不动——被换走的暗牌进入暗手牌，该字牌只在该里宝槽需要于立直和牌结算时展示的情况下作为里宝指示牌生效；未标记槽位只能盲换。

## 首版设计约束与检视口径

以下是基于已确认玩法的首版设计约束，不代表用户逐项明确指定。实现按这些契约交付；如需改动，应在交付说明中提出并由 Codex 检视，不能默默改动已确认规则。已确认规则与实现前检查见文末。

| 项目 | 建议 | 原因与影响 |
|---|---|---|
| 每次摸牌限一次 | 同一次取牌（正常摸牌或杠补牌）只能成功激活一次偷梁换柱，激活即标记 | 防止反复试探扣费；连续杠每次新补牌是新的取牌事件，可各施术一次 |
| 手牌侧范围 | 两个窗口只允许本人原有暗手牌：`handTile in closedTiles` 且 `handTile != drawnTile` | 用户确认：刚摸／刚补的牌与杠牌均不能参与；`allClosedTiles` 含摸牌，不可直接作为允许集合；任何副露或袖中牌也不属于可换暗手牌 |
| 候选可见性 | 激活**不揭示任何未标记候选**的牌面或实体身份；仅**本人有效落汗标记**命中的候选亮面（含其在窗口内的槽位）；候选槽位清单在**各自激活时**从当前牌面生成 | 本千术自身零透视，视觉信息全部来自落汗，两术构成组合经济；标记是各座私有信息，他人标记不亮面；面板全背也隐含「我的标记均不在本窗口内」的负向信息，属合法范围 |
| 立直后 | 不能施术 | 立直后自动摸切，玩家无选择权；与落汗、袖里藏花的立直锁定一致 |
| 庄家起手第 14 张 | 不可施术 | 该张由发牌产生而非摸牌（`DEAL_EXTRA`）；与袖里藏花口径一致 |
| 吃碰后的 DISCARD 阶段 | 不可施术 | 吃碰后没有摸牌语义，窗口比落汗更严，与袖里藏花一致 |
| 候选不足 | 正常摸牌窗口要求 `wallRemaining >= 1`（不足 8 张按剩余截断）；杠补窗口候选恒非空（岭上取尽后仍有宝牌暗区与替补） | 摸到活牌山最后一张（海底摸牌）后无候选，拒绝且不扣费；不足 8 张仍收全额 10 点 |
| 第四杠后 | 若仍处于合法 `CHEAT_WINDOW`，有可换暗手牌和死山候选，则可施术 | 候选不依赖下一张岭上牌；若已四杠散了或进入终局则不可施术。不得换出第四杠副露或本次岭上补牌。此为当前设计口径，区别于袖里藏花 |
| 替补列表计入死山候选 | 当前设计计入：替补物理上属于王牌区且未展示 | 换入替补区的牌没有正常摸取路径，但后续合法偷梁换柱可再次将其换出；不能称为永久离场。它不是刚摸入手的岭上补牌，二者必须区分 |
| 与袖里藏花同取牌 | **互斥、二选一**：任一术激活成功即占用本次取牌，另一术在同一取牌内的激活被拒且**不扣费**；跨取牌（含连续杠各次补牌）互不影响；挂起仍全局唯一 | 2026-10-13 用户确认；避免同一次取牌叠加「多拿一张」与「八张/死山换牌」的信息与操作双重优势；取牌事件按 `turnNumber` 判定（见「与袖里藏花的互斥与并用」） |
| 与落汗同窗并用 | 允许先后施术，各自扣费；任一选择挂起期间禁止新施术 | 落汗按实体追踪；偷梁换柱挂起时落汗专用权威、协调器和投影门禁均拒绝，不能只锁普通动作 |
| AI | 首版 AI 不主动施术；托管期间挂起选择由超时结算「保持原样」 | 不把策略开发与规则交付绑定 |
| 选择截止时长 | 不设独立软窗口，截止时间即该座当次决策计时截止；超时先结算「保持原样」再执行默认摸切 | 零新增计时状态；若评审要求软窗口另列后续检视 |
| 换牌后的动作与预览 | 选择完成后全部合法动作与立直待牌、听牌等预览按新手牌重算 | 不保留旧动作选项；不能预先承诺预览代码无需修改 |
| 千术事件 | 首版不新增 `BeamSwapOffered` / `BeamSwapResolved` 等私有事件，私有快照驱动反馈 | 与落汗、袖里藏花先例一致 |
| 旧协议线兼容 | `beamSwapConfig` 默认关闭；能力集新增 `beamSwapSupported`，缺失按 false | 与 `tileMarkSupported` / `sleeveBloomSupported` 协商模式一致 |
| 断线与托管 | offer 保留到选择完成、原决策超时或终局；截止前恢复真人控制可继续选择 | 断线不等于清除 offer；与袖里藏花口径一致 |

额度沿用现有千术点账本，不新增货币。费用以 `CheatEconomyConfig.costs["BEAM_SWAP"]` 为唯一配置来源，启用时必须显式为 10；缺失或其他数值拒绝配置。新增 `BeamSwapConfig(enabled = false)` 只控制功能开关；每次 10 点、候选规则、超时保持原样均为本版固定规则。启用时必须同时启用千术经济（`MatchConfig.validateSupported` 与 `ProtocolMatchConfig` 双重校验）。

首版平衡边界：本千术**自身不提供任何透视**——候选可见性完全由落汗标记供给（落汗 10 点 4 张、跨 3 局；偷梁换柱 10 点一次），盲换只能把废牌沉出换取未知牌，期望价值有限；精确狙击必须先付出落汗成本，两术构成组合经济。不接入休眠的视线、怀疑度或抓包判定，AI 首版不主动使用。10 点是用户指定的固定玩法参数，不代表完成平衡验证，尤其需结合替补区交换与盲换收益通过对局数据评估。

## 交互流程：激活 → 选择

偷梁换柱是两步交互，不占用也不替换回合决策：

1. **激活**：玩家在摸牌后的窗口提交激活请求。权威校验通过后扣 10 个额度，创建挂起选择（offer），并在私有快照中给出**候选槽位清单**（正常摸牌为后续八张；杠补为死山全部暗牌）：未标记候选只有槽位与牌背，不携带实体身份；本人落汗标记命中的候选附牌面。**此步没有任何牌的物理移动**：候选仍留在牌山或王牌区原位。
2. **选择**：玩家指定 `(原有暗手牌一张, 目标槽位)` 二元组——目标是**槽位而非实体**，未标记候选盲选、标记候选按亮面挑选均可。权威校验通过后执行一次原地对调（见「候选集与物理语义」），清除挂起状态，玩家继续正常的出牌决策。也可提交 `action = KEEP_ORIGINAL` 主动「保持原样」结束，零物理变化；该请求不携带手牌或目标槽位。取消同样不退款、不释放本次取牌的使用标记。

挂起期间，该玩家的其他一切动作（出牌、杠、自摸、立直、落汗与袖里藏花施术）都被权威拒绝，必须先完成选择；**同一时刻全局至多存在一个挂起千术选择**（袖里藏花或偷梁换柱任一），且只属于当前行动玩家。超时结算「保持原样」：零物理扰动，但 10 点不退、窗口内已亮面的标记候选信息不回收。

一个必须保证的时序（与袖里藏花同款）：权威超时裁决（默认摸切）在应用默认动作**之前**先结算挂起选择（保持原样），否则「挂起期间其他动作被拒」会让默认动作也被拒，对局卡死。结算后紧接摸切，恰好等于未施术时的超时行为。

## 候选集与物理语义

### 正常摸牌窗口（`lastDrawKind == NORMAL`）

候选集：`wall[i]`，`i ∈ [wallPointer, min(wallPointer + 8, wallEndExclusive))`，按摸牌先后排序展示（下 1 … 下 8）；为空（`wallRemaining == 0`）时拒绝激活且不扣费。

| 换出的手牌 | 语义 |
|---|---|
| 当前 `drawnTile`（刚摸／刚补的） | 禁止参与；不出现在允许选择集合中，伪造提交也拒绝，摸牌槽始终保持原牌 |
| 原有暗手牌 | 校验 `handTile in closedTiles` 且不等于 `drawnTile` 后，调用 `swapHandWithWall(state, seat, handTile, wallIndex = i)`；只替换暗手牌并重排暗手，`drawnTile` 不动 |

不变量：`wallPointer`、`wallEndExclusive`、牌山剩余数、王牌区、`drawnTile` 和全部副露完全不动。既有 `swapHandWithWall` 仍保留给袖里藏花使用的摸牌槽特判；偷梁换柱必须在自己的领域入口限制手牌范围，不能通过修改共享原语去禁止其他千术的合法路径。从暗手牌换出的牌停留在牌山目标槽位，**实际领取者由后续吃碰杠与回合推进决定**——延续 STACKING_ALGORITHM 的「牌山绝对冻结」法则：牌山永不重排，只允许原地对调，碰杠破千的时序反转照常上演。

### 杠后补牌窗口（`lastDrawKind == RINSHAN`）

**死山暗牌集合** = `{ t ∈ 全部 136 张实体 : tilePool.getLocation(t) == DEAD_WALL } \ doraIndicators`。按容器等价枚举为：

- `deadWall[rinshanPointer .. 3]`：未取岭上槽（已取槽 `deadWall[0 .. rinshanPointer-1]` 仅为历史引用，禁止按这些槽位生成候选；其引用的实体若已合法返回未取槽，只能按当前有效槽位枚举）；
- `deadWall[4 .. 13]` 中不在 `doraIndicators` 的槽：里宝槽与尚未翻开的指示槽（指示牌翻牌位置 `4 + k*2`、里宝 `4 + k*2 + 1`，翻牌时按**当时槽内容**生效）；
- `deadWallReplacements` 全部：从活牌山末端补入王牌的替补，未展示。

「没有展示」的边界：已翻开的表宝指示牌（`doraIndicators` 成员）不可选；里宝槽虽被终局翻牌引用，但在翻开前属于未展示，可选。

杠宝牌时机采用 SB-KAN-DORA 规则：暗杠成立即翻本次指示牌；大明杠／加杠在补牌后弃牌时才翻，连续杠则在下一次杠成立、补牌前翻前一次待翻指示牌。因此明杠／加杠后的本次待翻指示槽仍未展示，按本设计可作为候选；交换按翻开时的槽内容生效。激活、交换或保持原样均不得触发翻宝，不改变 `pendingKanDoraCount`。候选数量为有效王牌14张减去已翻表宝张数，首次明杠／加杠后最多13张；不能按实际杠数推算已翻数量。

交换语义：新增原语 `TileMovement.swapHandWithDeadWall(state, player, handTile, targetSlot, expectedTileId)`。目标以经过校验的有效槽位定位；实体ID由领域offer取出，仅用于核对该槽内容，客户端不得提交目标实体ID。必须先验证 `handTile in closedTiles`、`handTile != drawnTile`、手牌池归属以及目标仍在有效死山暗牌集合内，才交换暗手牌与该槽内容、重排暗手并做 `TilePool` 双向转移。禁止修改当前摸牌或任何副露。岭上指针、活牌山两端指针、替补列表张数和 `deadWall` 长度均不动。禁止对完整 `deadWall` 使用按实体查找的第一个命中：已取前缀可保留同一实体的历史引用，必须以有效槽位为准。

以下连锁后果均为**合法设计后果**，必须以测试固化：

| 换入的目标槽 | 后果 |
|---|---|
| 未取岭上槽（`deadWall[rinshanPointer..3]`） | 对应槽位在后续合法杠补实际取到该槽时生效；仅下一可取槽会影响下一次杠补，更深槽位不保证下一次或本局一定取到 |
| 里宝槽（`4 + k*2 + 1`，k < 已翻指示数） | 终局翻里宝按当时槽内容计算：立直和牌者的里宝可被本千术改写（自我加持）或被破坏 |
| 未翻指示槽（`4 + k*2`，k ≥ 已翻指示数） | 该槽在实际翻开时改变全场宝牌；本次明杠／加杠的待翻槽可在接下来的弃牌时生效，无须再杠一次；更深槽位取决于后续杠数及翻牌时机 |
| 替补列表 | 换入牌不会被正常摸取，但可在后续合法偷梁换柱中再次换出；当前候选换入本人的暗手牌 |
| 死山任一暗牌 → 暗手牌 | 当前 `drawnTile` 与杠副露不动；以交换后的完整手牌重算和牌形，保留 `isRinshanDraw` 等既有来源，合法自摸仍可计岭上开花 |

### 守恒与审计

既有两条王牌不变量（`StateInvariants`：`deadWall` 列表恒 14；有效王牌 = 未取部分 + 替补张数恒 14）与活牌山审计区间 `[wallPointer, wallEndExclusive)` 是本设计的硬边界。两条路径均只做原地对调 + `TilePool` 双向转移，136 张实体牌位置账本前后一致；新增 offer 与使用标记语义应补充断言和测试。

### 与袖里藏花的互斥与并用

| 维度 | 袖里藏花 | 偷梁换柱 |
|---|---|---|
| 候选规模 | 固定 1 张（下一张牌山 / 下一张岭上） | 8 张牌山 / 死山全部暗牌 |
| 候选可见性 | 第二候选牌面即商品（付费揭示） | 零透视，仅本人落汗标记命中亮面，其余盲选 |
| 同取牌并用 | 与偷梁换柱二选一（同取牌不可并用） | 与袖里藏花二选一（同取牌不可并用） |
| 手牌侧 | 仅 `drawnTile` | 仅原有暗手牌，排除 `drawnTile` 和全部副露 |
| 交换方向 | 二选一保留，未选退回指定槽 | 任意对调，换出牌沉入所选槽 |
| 第四杠后 | 禁止（无下一岭上） | 合法窗口且有可换暗手牌和候选时允许 |
| 费用 / 限次 | 10 点 / 每次摸牌一次 | 10 点 / 每次摸牌一次 |

互斥规则分两层：

1. **同取牌二选一（2026-10-13 用户确认）**：同一次取牌（同一 `turnNumber` 的摸牌或杠补牌事件）中，袖里藏花与偷梁换柱只能成功激活其一。任一术激活成功即占用本次取牌：另一术在同一取牌内的激活被 `*_ALREADY_USED_THIS_DRAW` 拒绝且**不扣费**。实现上两条激活链都须同时检查 `sleeveBloomUsedOnTurn` 与 `beamSwapUsedOnTurn`（任一等于当前 `turnNumber` 即拒绝），各自激活只写自己的字段。跨取牌互不影响：下一次取牌（含连续杠的每次补牌，均为新取牌事件）可重新二选一。
2. **挂起互斥（全局唯一）**：任一 offer 挂起期间，另一千术的激活与该玩家一切普通动作均被拒绝——同取牌二选一已在源头排除两者同时挂起，此层为防御性兜底。

跨取牌的候选读取：候选在各自**激活时**从当前牌面读取——上一次取牌用偷梁换柱换入 `wall[wallPointer + 2]` 的废牌，可能被后续某次取牌摸走；再下一次取牌激活袖里藏花时，其第二候选按当时的 `wall[wallPointer]` 读取，可能恰为先前施术换入的牌——语义自洽，无需特判。

## Engine 领域设计

### 状态归属

拟新增 `com.majiang.cheat.beam` 包：

| 类型 | 职责 |
|---|---|
| `BeamSwapConfig` | 功能开关（`enabled = false` 默认关闭） |
| `BeamSwapWindow` | 枚举 `WALL`（正常摸牌）/ `DEAD_WALL`（杠后补牌），由激活时的 `lastDrawKind` 推导 |
| `BeamSwapSlot` | 密封类型：`WallSlot(index)` / `DeadWallSlot(index)` / `ReplacementSlot(index)`，槽位描述用于选择时的漂移校验 |
| `BeamSwapCandidate` | 领域侧候选值对象：实体牌 `tileId` + `slot`；实体身份只进引擎内部校验与执行，**不下发**给未标记观察者 |
| `BeamSwapRequest` | 激活请求值对象：`requestId`、`sessionId`、`roundId`、`decisionId`、`controlEpoch`，防御性样式仿 `TileMarkRequest` |
| `BeamSwapChoiceRequest` | 身份五元组加 `offerId`、`action`（`SWAP` / `KEEP_ORIGINAL`）；仅SWAP包含 `handTileId` 与 `targetSlot`，目标实体ID不得来自请求。保持原样时不携带二者 |
| `BeamSwapOffer` | `offerId`、`seat`、`window`、`candidates`、`eligibleHandTileIds`（激活时原有暗手牌集合）、`drawnTileId`（内部一致性校验）、`createdTurnNumber`；归属单局，候选和手牌仅冻结引用、不移动实体 |
| `BeamSwapView` / `BeamSwapOfferView` | 观察者私有视图（见「私有可见信息」） |
| `BeamSwapProjectionFactory` | 从已提交领域状态构造单观察者可见信息 |

挂起选择不进 `MatchSession`，存入 `GameState` 两个新字段，随 `RoundChange` 原子安装、随深拷贝复制、随 `resetForNewRound` 复位：

```kotlin
// GameState 拟新增字段
var beamSwapOffer: BeamSwapOffer? = null   // 当前挂起的选择；null 表示无
var beamSwapUsedOnTurn: Int = -1           // 已使用的取牌序号（turnNumber）；每次取牌与袖里藏花二选一
```

窗口判定复用既有 `GameState.lastDrawKind`（`SleeveBloomDrawKind` 的 `NORMAL` / `RINSHAN` 取值），不新增重复的来源字段；该字段已覆盖发牌、正常摸牌与三类杠补牌全部写入点。`beamSwapUsedOnTurn` 与 `sleeveBloomUsedOnTurn` 各自记录本术的已使用取牌，但校验语义为**联合判定**：任一激活链都须同时检查两字段，任一等于当前 `turnNumber` 即视为本次取牌已被占用（同一次取牌至多激活袖里藏花或偷梁换柱中的一种，用户确认二选一；不限制落汗先后施术）；各自激活只写自己的字段，不复位对方的标记。

### 权威接口

拟增加（`seat` 与 `now` 由可信宿主传入）：

```kotlin
// TableAuthority 拟新增成员
fun submitBeamSwap(seat: Int, request: BeamSwapRequest, now: Long): MutationResult
fun submitBeamSwapChoice(seat: Int, request: BeamSwapChoiceRequest, now: Long): MutationResult

// ApplicationCoordinator 拟新增成员（internal 语义不变）
fun submitBeamSwap(seat: Int, request: BeamSwapRequest, now: Long): ActionResult
fun submitBeamSwapChoice(seat: Int, request: BeamSwapChoiceRequest): ActionResult
internal fun resolveBeamSwapKeepOriginal(seat: Int): List<GameEvent>  // 超时/清理兜底：保持原样并清除 offer

// TileMovement 原语（仅杠补窗口需要）
fun swapHandWithDeadWall(
    state: GameState, player: Int, handTile: Tile,
    targetSlot: BeamSwapSlot, expectedTileId: Int
): Tile
// 前置：handTile 在 closedTiles 中且不等于 drawnTile，池位置为本人手牌；
//       targetSlot 为有效死山暗牌槽，内容等于领域offer中的 expectedTileId；
//       目标池归属 DEAD_WALL，不是已翻指示牌、已取前缀、手牌或副露。
// 效果：只交换暗手牌与目标槽内容及池归属，重排暗手牌；
//       drawnTile、所有副露、取牌指针、替补张数和 deadWall 长度完全不动。
```

正常摸牌窗口不新增原语，先校验原有暗手牌范围，再调用既有 `swapHandWithWall(state, seat, handTile, wallIndex)`。查询通过 `querySnapshot()` 新增私有字段 `PlayerSnapshot.beamSwap`，不另开可绕过投影的全局查询接口。

**激活请求校验链**（幂等查询必须早于当前决策、代次与 deadline 校验）：

1. 校验可信宿主绑定、有效座位与整场请求作用域；沿用宿主既有 `STALE_SESSION` / `SEAT_DISCONNECTED` / `SEAT_NOT_HUMAN` 入口门禁。
2. 在该整场与座位作用域查幂等账：同 `requestId`、操作类型与完整载荷指纹一致即返回原回执；不同则 `REQUEST_ID_CONFLICT`。命中不执行后续变更。
3. 仅首次请求校验功能开关、经济开启与费用显式为 10；否则 `BEAM_SWAP_DISABLED`。会话未关闭、未暂停，`roundId`、`decisionId`、`controlEpoch` 匹配当前权威值；不匹配返回既有原因码。
4. 权威计时存在、计时键匹配当前决策且 `now < deadline`；到期返回 `DEADLINE_EXPIRED`。
5. 控制者为真人（本地或远端），非托管非观战；否则 `ACTION_NOT_AVAILABLE`。
6. 本人为当前行动玩家、`phase == CHEAT_WINDOW` 且无响应窗口；否则 `NOT_YOUR_TURN` / `WRONG_PHASE`。吃碰后的 `DISCARD` 阶段无摸牌语义，不可施术。
7. `drawnTile != null` 且归属本人手牌；否则 `BEAM_SWAP_NO_DRAWN_TILE`。从 `closedTiles` 生成可换集合，显式排除 `drawnTile` 并验证池归属；为空时 `BEAM_SWAP_NO_HAND_TILES`，不扣费。不得从包含摸牌的 `allClosedTiles` 或 `Zone.HAND` 全集直接生成可换集合。
8. 未立直；否则 `BEAM_SWAP_RIICHI_LOCKED`。
9. `lastDrawKind != DEAL_EXTRA`；否则 `BEAM_SWAP_DEALER_EXTRA_EXCLUDED`。
10. **全局**无挂起千术选择（袖里藏花或偷梁换柱任一）；否则 `BEAM_SWAP_ALREADY_PENDING`。袖里藏花的对称检查须同步泛化（见「幂等与清理」）。
11. 本取牌未使用袖里藏花或偷梁换柱：`beamSwapUsedOnTurn != turnNumber` 且 `sleeveBloomUsedOnTurn != turnNumber`；否则 `BEAM_SWAP_ALREADY_USED_THIS_DRAW`（本次取牌已用过袖里藏花或偷梁换柱，二选一）。
12. 窗口候选非空：`WALL` 窗口要求 `wallRemaining >= 1`；`DEAD_WALL` 窗口要求死山暗牌集合非空（第四杠后仍满足）；否则 `BEAM_SWAP_NO_CANDIDATES`，不扣费。
13. 余额 ≥ 10；否则 `INSUFFICIENT_FUNDS`。
14. 委托协调器在锁内复核业务状态与物理前置条件后执行。首次领域处理产生的成功或拒绝回执写入幂等账。

**选择请求校验链**：先执行同样的可信作用域与幂等查询，再校验首次请求的功能、当前身份、计时与真人控制；不重复扣费。另要求：

1. 存在挂起选择且属于本人；否则 `BEAM_SWAP_NO_PENDING_OFFER`。
2. `offerId` 匹配；否则 `BEAM_SWAP_OFFER_MISMATCH`，不得清理当前offer。非法请求与真实状态漂移不可混为一类。
3. 先校验请求分支：`SWAP` 必须指定offer中的 `eligibleHandTileIds` 和候选 `targetSlot`；伪造非允许手牌（含刚摸／刚补、杠牌及其他副露）或非候选槽位，返回 `BEAM_SWAP_INVALID_CHOICE`，不清除当前offer。`KEEP_ORIGINAL` 不需要选牌，禁止携带手牌或槽位；它与SWAP使用不同完整载荷指纹。
4. 再复核当前offer对应的真实局面：仍为本人 `CHEAT_WINDOW`，取牌序号、来源、原 `drawnTileId` 及可换暗手牌集合与激活时一致；每个候选槽仍合法，内容和池位置与offer一致。若真实局面漂移，返回 `BEAM_SWAP_STALE_OFFER` 并原子清理当前失效offer、保留deadline及使用标记，推送新快照，不交换、不退款、不提前摸切。
5. 选择请求的 `decisionId` / `controlEpoch` 必须来自当前权威快照；offer只绑定本局、座位与本次取牌。有效 `KEEP_ORIGINAL` 清除offer、更新决策身份并延续原时钟，不移动任何牌、不退款、不释放取牌限次。

### 原子性与计时

激活与选择各在协调器现有写锁与提交保护内完成，复用 `submitTileMark` 的原子提交模式：在候选深拷贝上执行全部变更，通过物理及偷梁换柱语义审计后随一次 `RoundChange` 原子安装并使决策注册表失效。激活包含扣费流水、使用标记与 offer，且仅此步发布一次 `GameEvent.CheatPointsChanged`；选择包含必要的交换与清除 offer，不再发布扣费事件。禁止先单独调用会提交并广播的 `applyCheatLedgerEntry()` 再改状态，避免半提交。选择或清理即使没有新事件，也须按状态变更推送完整快照。

费用流水 `entryId = "beam-$seat-$requestId"`，幂等重试只扣一次。费用统一从 `config.cheatEconomy.costs["BEAM_SWAP"]` 读取，门面与协调器两侧都从配置读，并由 `MatchConfig` / `ProtocolMatchConfig` 的显式等于 10 校验兜底。

偷梁换柱是出牌前的补充操作，不完成弃牌决策、不切换行动玩家。激活与选择各使 `roundVersion` 递增、`decisionId` 变化，须对仍有效的原决策各调用一次 `DecisionClock.rekey()`，完整保留 `opensAt`、`deadline` 与储备基数，不重置也不赠送思考时间；先重建决策、重键原时钟，再同步宿主时钟与发布快照。

激活未改手牌，可保留该手牌对应的规则候选供计时及超时策略使用，但挂起期间普通动作统一由 offer 门禁拒绝。选择完成后必须从实际交换后手牌重算全部合法动作：弃牌实体、自摸、暗杠/加杠、立直与九种九牌等均以新状态为准；重新生成决策与选项，旧 `decisionId` / `optionId` 不得继续使用。主动「保持原样」时规则候选内容可以相同，决策身份仍更新。立直待牌、听牌预览与最终算分同步采用新手牌。交换不视为新的摸牌或鸣牌：不改变 `turnNumber`、摸牌来源、`isRinshanDraw`、首巡未中断标志、一发状态与海底/河底判定；岭上来源换牌后岭上开花照常可计，正常来源不获岭上役。上述口径必须以实际算分测试验证，不能只测预览。

### 幂等与清理

回执层次与袖里藏花一致：幂等账保存领域回执，重复请求的 status/reason 及业务结果不变；宿主为线路回执附加的私有流 cursor 不作为动作已生效的凭据，客户端状态始终以有序完整快照为准。

领域去重按整场、座位与 `requestId` 生效，保存操作类型（激活/选择）与载荷指纹；同一 ID 用于另一类写操作（普通动作、洗牌方案、落汗、袖里藏花）返回 `REQUEST_ID_CONFLICT`。UDP 传输层更严格的原始载荷冲突规则保留，自动重试必须复用完全相同的载荷。

清理路径全部幂等：超时先结算「保持原样」，再基于结算后状态选择并执行默认动作（`checkTimeout` 与立直自动摸切两处同序）；若清理更新决策身份，后续取最新决策并正确关闭对应时钟。和牌、四杠散了、荒牌等终局事务即清除 offer；`resetForNewRound` 复位两字段，换庄不继承上一局 offer。暂停保留 offer 并随权威时钟冻结；断线与 AI 托管不清除 offer，托管 AI 不提交选择，原 deadline 到期后权威保持原样并默认摸切；宽限期内重连且仍为真人控制可继续选择，已托管者须先显式 `claimControl()`；控制权切换引起决策键变化仍需重键保留原 deadline 与储备，不能借反复断线、托管或收回来延长选择时间。

`handleAction` 顶部护栏**泛化**：该座位存在任一挂起千术选择（袖里藏花或偷梁换柱）时，直接拒绝其任何 `GameAction` 提交（`SLEEVE_BLOOM_CHOICE_PENDING` / `BEAM_SWAP_CHOICE_PENDING` 对应返回），覆盖权威超时裁决之外的直接调用路径。同理，袖里藏花激活链的第 10 步「无挂起二选一」须从「无袖里藏花 offer」泛化为「无任一挂起千术选择」——这是对既有护栏的**显式加严**（新增「偷梁换柱挂起时激活袖里藏花被拒」这一拒绝分支），交付说明中必须列明，Codex 据此核对既有袖里藏花测试预期是否需要同步该加严口径，不得借此削弱任何现有测试。AI 决策入口同样防御（AI 不激活，纯兜底）。

落汗接线必须同步加严：`TableAuthority.submitTileMark`、`ApplicationCoordinator.submitTileMark` 和 `TileMarkProjectionFactory.canApply` 均检查任一挂起千术offer。前两者是专用入口，不经过普通 `handleAction`，必须拒绝偷梁换柱挂起时的新落汗且不扣费；私有投影返回对应 `BEAM_SWAP_CHOICE_PENDING`，表现层同时禁用入口。两个千术的激活投影与实际入口还必须同时检查两种offer、两种取牌使用标记，避免按钮与权威结果分歧。

第二处加严：袖里藏花激活链的「本次摸牌已施术」检查（只查 `sleeveBloomUsedOnTurn`）须泛化为「本取牌已用袖里藏花或偷梁换柱」（同时检查 `beamSwapUsedOnTurn`）——新增「本取牌已用偷梁换柱时激活袖里藏花被拒且不扣费」的拒绝分支，同样属**显式加严**而非削弱，交付说明须列明；对应地，袖里藏花的 `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW` 原因含义扩展为「本次取牌已施术过运行时千术（本术或偷梁换柱）」。

## 私有可见信息

拟在 `PlayerSnapshot` 增加可空 `beamSwap: BeamSwapView?`。功能关闭时为 `null`；开启时仅返回**观察者本人**的信息：

- `canApply`、明确的禁用原因、权威费用 10、当前窗口（无取牌时为 `null`）。
- `decisionId`、`controlEpoch` 与状态版本，用于绑定入口按钮与阻止旧选择提交。
- `pendingOffer: BeamSwapOfferView?`：`offerId`、`window`、按序候选槽位列表、`selectableHandTileIds`（本人可换原有暗手牌，不含摸牌或副露）、`deadlineMillis`（投影时从该座 `DecisionClock` 查询）、`decisionId`、`controlEpoch`。后两者从当前权威决策与控制者投影，不从激活请求复制。每个候选条目含槽位标签与 `marked: Boolean`：仅当该实体被**观察者本人**的有效落汗标记命中时附 `tileId` 与牌面，否则只有槽位——`tileId` 不得出现在未标记条目中。

保密规则：

- 本功能**不揭示任何未标记候选**：未标记条目不携带 `tileId`、牌面或任何牌山深层/死山内容；实体身份只存在于引擎内部校验，不出现在任何观察者的投影、回执或事件中。
- 本人落汗标记命中的候选亮面并按窗口顺序展示槽位（如「下 3」「里宝槽」），等于在候选窗口内**局部定位**了该标记牌——这是对落汗「牌山、未公开王牌不揭示位置（`NOT_VISIBLE`）」口径的**经用户确认的定向放宽**，边界为：仅在本人挂起的偷梁换柱候选窗口内、仅对本人标记命中的候选生效；窗口结束或 offer 清除后，标记追踪栏按落汗原规则回到 `NOT_VISIBLE`，不保留窗口内获得的位置。Codex 须验证放宽不越过该窗口（例如不得把非候选区间的标记牌位置一并带出）。
- 既有落汗标记与袖里藏花候选照常投影，但不额外标注它们是否属于本功能候选。
- 他家本就看不到施术者的摸牌牌面（`TileDrawn` / `RinshanDraw` 已按座位脱敏），偷梁换柱零新增事件；仍须审查快照、事件、回执和公开历史脱敏。其他观察者可见既有的 `CheatPointsChanged` 余额变化，不额外揭示候选或交换去向。
- `PublicTableProjection`、`ProtocolPublicSnapshot`、公开历史结果均不新增字段。
- `PlayerSnapshot.init` 增加交叉校验：挂起offer须属于本人 `CHEAT_WINDOW` 且 `myDrawnTile` 存在；`selectableHandTileIds` 为本人暗手牌的允许集合，不含 `myDrawnTile`、任何副露或他家牌。当前摸牌仅作为状态前提，不是可换手牌。线路DTO做相同校验，集合深拷贝。
- 快照与嵌套集合深复制并只读包装；已发出的旧快照不随后续结算被反向改写。

与落汗并用时的可见性：换牌会替换手牌实体，落汗按 `(sessionId, tileId)` 跟踪实体，被换入牌山或死山的已标记牌在追踪区自然转为「当前不可见」，被换入手牌的照常显示——语义自洽，无需特判。

## 协议与客户端 API

领域、线路、客户端各自定义类型，不以 typealias 共用跨层 DTO：

| engine | protocol | client.api |
|---|---|---|
| `BeamSwapConfig` | `ProtocolBeamSwapConfig` | `BeamSwapConfig` |
| `BeamSwapWindow` | `ProtocolBeamSwapWindow` | `BeamSwapWindow` |
| `BeamSwapSlot` | `ProtocolBeamSwapSlot` | `BeamSwapSlot` |
| `BeamSwapCandidateView` | `ProtocolBeamSwapCandidateView` | `BeamSwapCandidateView` |
| `BeamSwapRequest` | `ProtocolBeamSwapRequest` | `BeamSwapRequest` |
| `BeamSwapChoiceRequest` | `ProtocolBeamSwapChoiceRequest` | `BeamSwapChoiceRequest` |
| `BeamSwapOffer` | `ProtocolBeamSwapOfferView` | `BeamSwapOfferView` |
| `BeamSwapView` | `ProtocolBeamSwapView` | `BeamSwapView` |

选择请求的线路判别字段固定为 `action`：`SWAP` 必须同时携带合法 `handTileId` 和 `targetSlot`；`KEEP_ORIGINAL` 必须省略二者（领域/客户端对象对应null）。拒绝未知action、缺少动作、SWAP缺项、KEEP_ORIGINAL携带选牌字段及任何客户端目标实体ID。槽位使用明确的种类与非负索引，入站由权威offer再次限制范围；取消、超时后重试、取消与SWAP复用同一请求ID的冲突均须测试。

线路请求与客户端请求和领域请求业务字段一致；不能接受客户端指定座位、费用或效果结果。`EngineProtocolMapper` 与 `ProtocolViewMapper` 显式转换并校验所有新字段（含 `Window` 枚举映射与候选实体转换）。

新增 `SessionApi.submitBeamSwap(request): CompletableFuture<ProtocolActionReceipt>` 与 `SessionApi.submitBeamSwapChoice(request): CompletableFuture<ProtocolActionReceipt>`（接口默认抛 `UnsupportedOperationException`，与 `submitTileMark` 同式），以及 `client.api.GameSession.submitBeamSwap / submitBeamSwapChoice`。`ServerConnectionSession` 在 `client.session` 中实现：激活与选择**共用同一把在途请求锁**（`ACTION_IN_FLIGHT` 防双发，与出牌共享锁语义正确——同一时刻至多一个在途请求），沿用绑定代次校验、调度线程与关闭 Future 的既有规则。观战不提供该方法，底层伪造请求必须拒绝。

新增 `ApiTags.SUBMIT_BEAM_SWAP` 与 `ApiTags.SUBMIT_BEAM_SWAP_CHOICE`，`ApiRequest` 增加 `beamSwap` / `beamSwapChoice` 两个载荷字段，更新已知 API 白名单。编解码互斥矩阵与落汗同式：两个新标签各自必含专属载荷、禁止携带 `choice` / `shufflePlan` / `tileMark` / `sleeveBloom` 系载荷及彼此载荷；两类新载荷只允许出现在各自标签下。各自新增专用 `TypeAdapter`（严格类型校验、枚举白名单、解析异常包装）并注册。

所有路径最终在 `ServerTableHost` 同桌串行队列里调用相同的 `TableAuthority` 入口：`EngineProtocolMapper` 双向映射（含快照尾部 `beamSwap` 视图转换）、`SharedRoom` 直通、`ServerTableHost.submitBeamSwap / submitBeamSwapChoice`（入队串行、`STALE_SESSION` / `SEAT_DISCONNECTED` / `SEAT_NOT_HUMAN` 三连拒、回执缓存键新增 `HostReceiptKey.BeamSwap`、状态变化时同步决策时钟并推送）、`InProcessBinding` 进程内同接口实现、`UdpRoom` 与 `UdpTableServer` 分发（后者 `spectatorForbiddenApis` 集合必须显式加入两个新标签）。

能力集拟增加 `beamSwapSupported`，缺失按 false 处理；房间配置决定是否实际启用，启用校验（经济开启且费用显式为 10）在 `ProtocolMatchConfig` 与 `MatchConfig.validateSupported` 双侧执行。`ProtocolPlayerSnapshot` 增加可空 `beamSwap` 字段并做交叉校验（同引擎侧）。旧服务不支持时新 UI 禁用入口；首版仍要求各模块同版本升级。

### 拒绝原因

业务拒绝沿用 `Receipt.status = REJECTED`；结构错误与身份错误沿用协议错误路径。

| 原因 | 含义 |
|---|---|
| `BEAM_SWAP_DISABLED` | 功能未启用 |
| `WRONG_PHASE` / `NOT_YOUR_TURN` | 不是本人摸牌后的千术窗口 |
| `BEAM_SWAP_NO_DRAWN_TILE` | 当前没有归属本人的刚摸牌 |
| `BEAM_SWAP_NO_HAND_TILES` | 没有可用于交换的原有暗手牌 |
| `BEAM_SWAP_RIICHI_LOCKED` | 本人已经立直 |
| `BEAM_SWAP_DEALER_EXTRA_EXCLUDED` | 庄家起手第 14 张不可施术 |
| `BEAM_SWAP_ALREADY_PENDING` | 已有挂起千术选择（袖里藏花或偷梁换柱） |
| `BEAM_SWAP_ALREADY_USED_THIS_DRAW` | 本次取牌已施术过运行时千术（偷梁换柱或袖里藏花任一，二选一） |
| `BEAM_SWAP_NO_CANDIDATES` | 候选不足（活牌山摸完或死山无暗牌） |
| `BEAM_SWAP_NO_PENDING_OFFER` | 没有进行中的选择 |
| `BEAM_SWAP_OFFER_MISMATCH` | offerId不匹配；仅拒绝，不清理当前offer |
| `BEAM_SWAP_STALE_OFFER` | 局面已变化，选择失效并原子清理 |
| `BEAM_SWAP_INVALID_CHOICE` | 请求选择不在允许暗手牌／候选槽位中，包含刚摸牌或杠牌等；仅拒绝，不清理offer |
| `BEAM_SWAP_CHOICE_PENDING` | 请先完成偷梁换柱选择 |
| `INSUFFICIENT_FUNDS` | 千术点不足 |
| `DECISION_MISMATCH` / `STALE_CONTROL_EPOCH` / `NO_ACTIVE_DECISION` | 局、决策或控制代次已变更 |
| `DEADLINE_EXPIRED` / `HOST_PAUSED` / `SESSION_CLOSED` | 已到截止、暂停或会话关闭 |
| `REQUEST_ID_CONFLICT` | 同一请求 ID 被用于不同操作或不同载荷 |

回执中不能带回请求者无权获知的任何牌山、死山信息。

## 表现层设计

入口须纳入右侧按钮整体布局，不能直接追加在袖里藏花下方：当前袖里藏花为 `(1080,100,140,44)`，下方已有理牌 `(1080,50,140,44)` 和托管 `(1080,6,140,36)`。实现前图形设计须重新安排所有按钮，保持无重叠及有效触控尺寸。入口仅在权威 `canApply` 为真且本地连接、控制状态允许时可用；点击提交激活，不乐观扣点，私有快照的offer驱动面板。

- **手牌选择行**：只允许私有offer的 `selectableHandTileIds` 对应原有暗手牌单选；当前刚摸／刚补的牌不得选择，任何副露（包括暗杠）不得选择。若为展示完整手牌而绘制摸牌，必须明确禁用，不能被点击、拖拽、触摸或快捷键选中。
- **候选行**：全部候选以**牌背**展示；`WALL` 窗口按摸牌先后排布并标注「下 1 … 下 8」，`DEAD_WALL` 窗口按槽位分组标注「岭上槽 / 宝牌暗区 / 替补区」。本人落汗标记命中的候选以半透明牌面 + 汗滴标识亮出（沿用落汗视觉语言），其余只能按牌背盲选；单选。
- 确认「互换」提交 `action=SWAP`；「保持原样」提交 `action=KEEP_ORIGINAL`（无需先选牌），零物理变化且不退款；面板显示权威倒计时。

选择提交后 `pendingOffer` 消失、暗手牌更新，`myDrawnTile` 与全部副露保持不变，面板自动关闭，继续正常出牌。换牌面板与立直选牌、落汗选牌、鸣牌选择、袖里藏花二选一面板互斥，注册进 `isModalActive()`；期间手牌点击与其他行动按钮全部挂起（引擎侧同时拒绝，双保险）。决策、局、控制代次或候选变化后本地旧选中状态立即失效；断线、托管、暂停时隐藏面板，但不据此清除领域 offer；恢复真人控制与有效快照后尚未结束的 offer 重新展示，不补发旧选择。被换出的牌做去向提示（槽位标签高亮一瞬），不承诺牌墙图形化。提交使用现有 `isPending` 门控防重复；回执与快照乱序到达时以权威快照为准；领域拒绝经原因码中文表以 toast 提示。

具体几何、文案与验收口径在实现前另立 [VISUAL-DESIGN.md](VISUAL-DESIGN.md)（已固定实施稿，待实现验收）。需覆盖最多13张死山候选、原有暗手牌选择区、1280×720及960×540、触控选牌与右侧按钮互不遮挡。

## 分阶段交付

| 阶段 | 修改范围 | 完成条件 |
|---|---|---|
| 第一阶段 Engine | 状态字段、`swapHandWithDeadWall` 原语、权威入口、原子扣费、私有投影、超时联动、护栏泛化与领域测试 | 无 UI/网络也能完整验证激活、选择、两窗口物理语义、守恒、隐私、时钟与超时结算；评审并冻结领域契约 |
| 第二阶段 接口与 API | protocol DTO/编解码、server 映射与本地/UDP 接线、client.api 门面与视图、client 实现及恢复缓存 | 真实本地/UDP 链路能施术、拒绝伪造、恢复完整效果，完成接口文档及集成测试；通过后才接展示 |
| 第三阶段 表现层 | 入口、换牌面板、输入锁、字体文本及桌面/移动布局 | 真实牌桌完成激活到选择再出牌的闭环，超时/断线/观战均正确 |

每阶段先完成自己的行为验证再进入下一阶段；不能先在 UI 假翻牌，或把未接线的 DTO 当成功能完成。现有麻将规则、算分、牌山、出千账本、UDP 可靠性与控制权测试均须保留。

## 验收清单

| 组别 | 必须覆盖的情况 |
|---|---|
| 候选与交换 | 正常候选为后续八张截断；死山候选按有效槽位枚举，不含已翻指示牌、已取历史槽、当前手牌、牌河或任何副露。两窗口 × 原有暗手牌 × 标记亮面／盲选全覆盖；刚摸／刚补牌、暗杠／明杠／加杠及其他副露即使带标记也拒绝。换牌只更新暗手牌与所选槽，drawnTile、副露、指针、余量不动；按对应槽位的后续实际取牌验证，不承诺下一次必取 |
| 守恒 | 每次变更后 136 张位置账本一致；`deadWall` 恒 14、有效王牌恒 14；活牌山区间与两端指针前后一致；既有物理审计与新增 offer、使用标记断言全过 |
| 使用权限 | 他人回合、吃碰后 DISCARD、响应窗口、抢杠窗口、无摸牌、无可换暗手牌、立直后、庄家起手第 14 张、活牌山摸完（WALL 窗口）、准备阶段、暂停、结算、托管及观战均拒绝；绕过 UI 发请求仍拒绝 |
| 原子与费用 | 每次成功激活恰好扣 10，选择不再扣费；余额 9 拒绝、10 可激活且扣至 0 后仍可选择；重试只扣一次；校验失败不留 offer 或半笔流水；选择失败不交换；缺失费用键或费用不为 10 的启用配置拒绝；超时或取消不退款 |
| 挂起与同取牌互斥 | 挂起期间出牌、杠、自摸、立直、落汗、袖里藏花提交均被对应 `*_CHOICE_PENDING` 拒绝；袖里藏花 offer 挂起时激活偷梁换柱拒绝，反之亦然；**同一次取牌先用袖里藏花后激活偷梁换柱被拒且不扣费，反序同理**；连续杠各次补牌为新取牌事件、可重新二选一；同术重复激活拒绝；错误offerId和非法选牌仅拒绝且保留当前offer；真实槽位／手牌基线漂移才拒绝并原子清理；覆盖KEEP_ORIGINAL取消及其与SWAP请求ID冲突，取消后同次取牌仍不可施术 |
| 超时与时钟 | 决策超时先结算保持原样再摸切，行为与未施术的超时完全一致；激活与选择各触发一次 rekey，`opensAt`/`deadline`/储备不重置；恰好到期拒绝；暂停冻结、恢复继续 |
| 杠与流局 | 三类杠补窗口仅能换原有暗手牌；第四杠补后若未流局且候选/可换手牌存在则可施术。分别验证浅层/深层岭上槽和指示槽在实际消费到该槽时生效；替补无正常摸取路径，但后续可再被千术换出；明杠／加杠待翻指示槽可选，暗杠已翻指示槽不可选，交换不触发翻宝或改变待翻数量；终局清offer，新局复位 |
| 投影与隐私 | 激活前不额外揭示任何候选；未标记候选条目不含 `tileId`/牌面；仅本人标记命中的候选亮面且槽位正确，他人标记不亮面；标记定位放宽严格限定在挂起窗口内，offer 清除后追踪栏回 `NOT_VISIBLE` 且不残留窗口内位置；`pendingOffer` 仅本人快照出现；他家、观战、公开投影、历史接口无候选信息泄漏；既有余额与各自获准的落汗、袖里藏花信息照常投影 |
| 幂等与竞态 | 激活/选择成功回执丢失后原载荷重放仍返回原回执；首次拒绝重放一致；改载荷与跨 API 撞 ID 冲突；激活与弃牌/超时/托管并发按队列顺序生效；UDP 丢包、乱序、重复与分片恢复 |
| 恢复与控制权 | 宽限期内重连继续选择；托管后未收回不允许选择，截止前显式收回按新代次继续；未执行旧请求拒绝，已执行重放无副作用；暂停恢复保留 offer；反复控制权变化不重置 deadline、储备或扣费 |
| 动作、预览与并用 | 换牌使自摸、杠、立直等选项新增或消失；新弃牌选项含换入实体且不含换出实体；旧决策选项拒绝；听牌预览随新快照重算；与落汗同窗可并用互不干扰；与袖里藏花同取牌互斥（拒绝且不扣费）、跨取牌先后施术互不影响，袖里藏花候选按其激活时牌面读取 |
| 役种与算分 | 杠补窗口换牌后自摸仍按岭上来源计岭上开花，正常来源不获岭上役；非庄首摸换牌满足条件可计地和；海底/河底沿用原规则；换入里宝/杠宝槽后终局宝牌按换后内容计算并与预览一致；核对最终役种、符番、得点 |
| UI | 原有暗手牌可选、当前摸牌与全部杠牌/副露不可选，右侧按钮无重叠，两种视口最多13候选可操作；入口按钮可用性、面板手牌/候选选择与确认/保持原样/倒计时、标记候选半透明亮面与汗滴标识、未标记牌背盲选、模态互斥、超时自动关闭、拒绝 toast、桌面与触摸操作 |

最终验证包括不筛选的全量单元/模块测试、独立架构与隐私审查、桌面构建、Android 调试 APK 构建及真实桌面 GL 冒烟。实现完成前不填写通过数量；Android 触控若未在设备验证，应与构建结果分开记录。

## 决策与实现前检查

- 用户已确认：命名偷梁换柱、每次10点、默认盲选且仅本人落汗标记命中亮面、与袖里藏花同取牌互斥；手牌侧仅原有暗手牌，刚摸／刚补和全部杠牌均不得参与交换。盲选与手牌范围不再列作开放决策。
- 当前设计保留：死山替补区计入候选；第四杠补牌后如仍处于合法窗口可施术。替补牌是在死山里的实体，不是刚摸入手的岭上补牌。后续平衡若需收窄须另行修改规则，不把“替补无正常摸取路径”写成永久离场。
- 实现前完成图形设计，明确按钮整体布局、候选上限与禁用牌交互；按本稿统一SWAP/KEEP_ORIGINAL载荷、错误请求不清理/真实漂移才清理的边界及落汗专用入口互斥。
- 2026-10-13用户已授权Anti开发、Codex编写测试与独立检视。按E1领域、E2网络客户端、E3表现层逐段回调验收；阶段交付不等于完整功能验收，具体结果以交付与验收文档为准。

## 参考源码

- [权威门面 TableAuthority](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)
- [应用协调 ApplicationCoordinator](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt) 与[单局状态机 RoundStateMachine](../../engine/src/main/kotlin/com/majiang/round/RoundStateMachine.kt)
- [物理移动原语 TileMovement](../../engine/src/main/kotlin/com/majiang/model/TileMovement.kt)（`swapHandWithWall` / `drawRinshan` / `swapDrawnWithNextRinshan`）、[Tile 实体身份](../../engine/src/main/kotlin/com/majiang/model/Tile.kt) 与 [TilePool](../../engine/src/main/kotlin/com/majiang/model/TilePool.kt)
- [单局状态 GameState](../../engine/src/main/kotlin/com/majiang/state/GameState.kt)（`lastDrawKind` 复用点）与[守恒审计 StateInvariants](../../engine/src/main/kotlin/com/majiang/state/StateInvariants.kt)
- [回合过渡 RoundTransitionPlanner](../../engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt)（指示牌翻牌位 `4+k*2`、里宝 `4+k*2+1`、岭上补牌路径）
- [DecisionClock](../../engine/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 与 [CheatEconomyConfig](../../engine/src/main/kotlin/com/majiang/economy/CheatEconomyConfig.kt)
- [袖里藏花领域包 cheat.bloom（互斥与接线模板）](../../engine/src/main/kotlin/com/majiang/cheat/bloom/SleeveBloomModels.kt) 与[袖里藏花设计](../sleeve-bloom/DESIGN.md)
- [开局码牌 ShuffleSelection（命名决策对照）](../../engine/src/main/kotlin/com/majiang/stacking/ShuffleSelection.kt) 与[开局码牌算法说明书](../../STACKING_ALGORITHM.md)
- [PlayerSnapshot](../../engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt) 与[公开投影](../../engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt)
- [线路 SessionApi](../../protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt)、[请求信封](../../protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)、[落汗协议契约（模板）](../../protocol/src/main/kotlin/com/majiang/transport/api/ProtocolTileMarkModels.kt)
- [客户端 GameSession](../../client/src/main/kotlin/com/majiang/client/api/GameSession.kt) 与[落汗客户端契约（模板）](../../client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt)
- [GameScreen](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 与 [V4 架构规范](../GAME_ARCHITECTURE_V4.md)
