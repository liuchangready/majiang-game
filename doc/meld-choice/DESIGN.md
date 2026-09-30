# 吃碰杠牌组选择设计

Codex 设计、测试、检视；Antigravity 生产开发。当前基线255测试已通过。当前 GameScreen 点击吃碰杠取 firstOrNull 直接提交，导致只能选第一种。本次仅改表现层与只读方案适配，不改麻将规则、领域动作、AI策略、振听、算分或自动摸切。

## 交互

1. 点击 CHI/PON/KAN 仅打开该类牌组面板，不提交动作、不移牌、不记过水/振听。单方案可自动选中但仍须确认；多方案初始不选中，确认禁用。点击方案选中；悬停可临时高亮参与的手牌，但不能改变确认方案。
2. 面板显示每种完整组合的实际牌面：吃/碰3张；明杠/暗杠/加杠4张。类型标签 CHI / PON / OPEN KAN / CLOSED KAN / ADDED KAN。外来牌用不同颜色边框和 CALLED 标记，标明 FROM Pn；加杠区分原碰组与 ADD 的第4张；暗杠预览面向本人可展示4张正面。不要拉伸伪装横牌，也不必扩展现有 TileSkin 旋转接口。
3. 底部高亮参与的精确手牌；加杠只高亮补入的1张。候选面板在桌面牌面 flush 后绘制，避免被牌河遮盖。所有卡片和文字均在视口内，不覆盖手牌或顶部振听 HUD。沿用英文字体，不引入字体/图片/依赖。
4. 统一布局契约（方便实际输入与无头测试）：面板 Rectangle(230,210,800,320)。每页3卡，卡片 i=0..2：Rectangle(250+i*255,280,240,190)。卡片内放类型/来源/3或4张牌/选中状态。PREV Rectangle(250,225,90,36)，NEXT Rectangle(920,225,90,36)，页数居中。CONFIRM Rectangle(670,145,140,44)，CANCEL Rectangle(1030,145,170,44)。选牌时原动作按钮不重叠绘制。
5. 选牌期间保留 WIN 快捷按钮 Rectangle(490,145,160,44)，按合法动作显示 RON 或 TSUMO；保留 PASS Rectangle(840,145,150,44)，仅合法 Pass 时出现，点击才提交 Pass。自摸和荣和不得因打开鸣牌选择而消失。取消/ESC/Android BACK 只退回普通操作，不提交 Pass，也不关游戏。点手牌或面板空白不得出牌。理牌可用且保留精确选中动作。
6. 确认只提交被选中合法动作的原始内容一次；提交前核对最新 session.view/合法动作，视图变化取消选择；被拒绝也退出并刷新。取消后可重新选别类。与立直选牌互斥，窗口更新、结算、换局、hide/dispose 清理状态。不能因为确认后假会话视图未更新而重复提交。

## 固定 API / 任务 M 所有权

新增 `session/MeldChoiceProvider.kt`：

```kotlin
enum class MeldChoiceKind { CHI, PON, KAN }
data class MeldChoice(
    val action: GameAction,
    val type: MeldType,
    val tiles: List<Tile>,
    val handTiles: List<Tile>,
    val calledTile: Tile? = null,
    val addedTile: Tile? = null,
    val fromPlayer: Int? = null
)
object MeldChoiceProvider {
    fun options(view: PlayerView, kind: MeldChoiceKind): List<MeldChoice>
}
```

只消费 view.legalActions 且 action.player == viewerSeat；不生成/猜测新合法动作。顺序保持合法动作顺序，去除完全重复动作，不丢失不同动作。所有列表深拷贝并不可变（含 action 内 tiles/meld.tiles），只用自己手牌和公开信息，无领域副作用。

- CHI：action.meld 提供完整3张、calledTile、fromPlayer；handTiles 为移除 calledTile 后2张，精确物理身份必须属于本人。
- PON：action.tiles 为本人2张；添加 view.lastDiscard 成完整3张；calledTile/来源用lastDiscard/lastDiscardPlayer，缺失或来源本人时不生成伪方案。
- OPEN_KAN：action.tiles 为本人3张；添加 view.lastDiscard 成4张，来源同上。
- CLOSED_KAN：action.tiles 为本人4张，包括 drawnTile（如有）；called/added/from 均null。
- ADDED_KAN：现有协议 action.tiles 已含原碰3张+补入1张。依据 view.melds[viewerSeat] 精确匹配原 PON 和本人补入牌，只高亮1张 handTiles；tiles 展示原碰3张+addedTile；calledTile/fromPlayer 来自原 PON。不得误把完整4张都当作新手牌，也不要再把原碰追加一次变7张。
- malformed/incomplete options不得虚构牌；用精确 Tile 身份验证必要牌数和归属，隐藏缺少必要公开目标的方案。不要做第二套麻将合法性规则。

新增 `screen/MeldSelectionModel.kt`，无libGDX依赖：

```kotlin
class MeldSelectionModel {
    val isSelecting: Boolean
    val kind: MeldChoiceKind?
    val options: List<MeldChoice>
    val selectedAction: GameAction?
    val pageIndex: Int
    val pageCount: Int
    val visibleOptions: List<MeldChoice> // 每页3个
    fun update(view: PlayerView)
    fun begin(kind: MeldChoiceKind): Boolean
    fun select(action: GameAction): Boolean
    fun setPage(index: Int): Boolean
    fun confirmationAction(): GameAction?
    fun cancel()
}
```

初次update不打开；相等view保留选择；实质变化退出/清空。begin有方案才进入；同kind重复begin不清空已选；换kind重置。单方案预选，多方案null；select仅当前页合法action；setPage有效且变化才翻页并清空选择，非法页不变。cancel清选择/kind/page，confirmationAction模式外null。不要自行submit。

M 仅拥有上述两个新文件和 delivery-M.md；不改GameScreen/领域/测试。

## 任务 U 所有权

仅改 `screen/GameScreen.kt`，必要时新增独立渲染助手（如 `screen/MeldChoicePanel.kt`），不改M文件。使用上述固定API，公开 `val meldSelectionModel = MeldSelectionModel()` 供验收。按布局契约接入渲染/输入/会话变化/生命周期/理牌；所有吃碰杠入口包括立直后杠走方案选择，不能留直接firstOrNull submit。按钮标签若有不同杠型用通用KAN，具体类型在面板显示。保持先前立直/振听功能。

U 独立交付 delivery-U.md；不得改测试、Gradle、依赖。旧测试直接点杠立即生效的断言由Codex适配新确认流程。

## 执行限制与验收

只使用原生文件 read/search/edit 工具。不要RunCommand、shell、终端、MCP工具或其他agent，不修改权限（headless无法授权）。开发无需执行测试，Codex独立执行。

Codex验证：三种吃法逐一可选、碰/明杠目标牌完整且精确、多个暗杠/加杠分列、只高亮补入牌、分页、不可变、非零座位隔离、取消不Pass/不振听、确认一次且移正确牌、改变选择/悬停不误提交、拒绝与过期快照、立直后杠到岭上自摸选择、真实OpenGL画面与触屏输入、全量测试与双平台构建。
