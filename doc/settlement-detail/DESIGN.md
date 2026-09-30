# 结算明细可读性与可核对性（2026-09-30，Codex 设计/测试/检视，Antigravity 开发）

## 现状与目标
本次基线 343 tests 全通过。已有 score-breakdown 初版领域数据与单行展示，本次在其上补齐，不覆盖其他人的改动。用户要求所有命中役种、表宝牌、里宝牌、赤宝牌和计算方法都可见。
审计发现：长行溢出；默认字体无中文；无逐个指示牌映射；役满时宝牌/符数表述误导；高番原始基本点 Int 溢出；事件 snapshot 未复制新增嵌套集合。

## 并行任务和所有权
- **R 领域任务**：WinningScoreEvaluator.kt、ScoreCalculator.kt、GameEventSnapshot.kt；确有必要可修改 GameStateMachine.kt/SettlementService.kt。不得改 UI、测试或构建配置。
- **U 展示任务**：新增 screen/SettlementDetailPresenter.kt 与渲染/字体帮助类，GameScreen.kt；可读所有文件，不改 R 文件、测试或构建配置。gdx-freetype 已依赖且 assets/fonts/NotoSansSC-Regular.ttf 已存在。
- **F**：仅按 followup 指定的修复范围。
- 不提交 git、不改规则配置、不删旧实现契约。保留现有构造默认参数和 343 项测试兼容。用内置文件读写搜索工具开发，禁止 shell/RunCommand/terminal/MCP/调用其他代理或修改权限。Codex 负责所有命令、测试、构建。

## R 必须遵守的新增 API（供 U 并行使用）
```kotlin
// com.majiang.rules，WinningScoreEvaluator.kt 顶层
data class DoraIndicatorHit(val indicator: Tile, val target: Tile, val tiles: List<Tile>)
// DoraBreakdown 末尾新增默认字段：
val indicatorHits: List<DoraIndicatorHit> = emptyList()
val uraHits: List<DoraIndicatorHit> = emptyList()
// ScoreBreakdown 末尾新增默认字段：
val uraEligible: Boolean = false
val isDealer: Boolean = false
val isTsumo: Boolean = false
// ScoreCalculator.ScoreResult 末尾新增默认字段：
val rawBasePointsExact: String = rawBasePoints.toString()
```
1. 每个表/里指示牌生成一项，含零命中；target 用 Tile.fromTypeIndex(目标) 非赤牌；tiles 保留物理 copyIndex。手牌含荣和牌恰好一次，并含全部副露牌、杠的四张。重复指示牌独立计次；红五可叠加表/里/赤。
2. explain 内 uraEligible=context.isRiichi；不立直绝不能在解释/事件中带入里指示牌或命中牌。其余两个布尔字段取结算时上下文，不能从下一局状态反推。
3. 实际役满不加宝牌番，不用符数公式；仍可展示已公开的命中牌并明确“命中但不计番”。现有每个役满项按一倍规则保留，不擅自增加双倍役满规则。普通役只列最高得点的同一合法拆解，不能把互斥拆解役种混加。
4. 正常 formulaText 使用 BigInteger 或其他安全算法的精确 rawBasePointsExact，至少支持 0..80 番，无移位折返/负数。旧 rawBasePoints:Int 保持类型和小数值语义，超界可饱和 Int.MAX_VALUE 并注释，不用于公式展示/支付计算。合法高番仍按既有累计役满封顶，不变更规则。修正 4番30符满贯的错误注释。
5. snapshot 深拷贝且不可修改所有 breakdown 中的 List（役种、宝牌命中、逐指示牌嵌套 tiles、Fu.lines、paymentLines），既不能被原列表修改，也不能被其他订阅者改变。不改变事件历史/会话隐私约束。

## U 展示契约
```kotlin
// com.majiang.screen，供纯函数与输入测试使用
enum class SettlementSection { YAKU, DORA, CALC }
data class SettlementDetailRow(val text: String, val tiles: List<Tile> = emptyList())
object SettlementDetailPresenter {
    const val ROWS_PER_PAGE = 6
    fun rows(b: ScoreBreakdown, section: SettlementSection): List<SettlementDetailRow>
    fun pageCount(b: ScoreBreakdown, section: SettlementSection): Int
    fun page(b: ScoreBreakdown, section: SettlementSection, index: Int): List<SettlementDetailRow>
}
// GameScreen 只读测试接口：
val settlementSection: SettlementSection // private set，初始 YAKU
val settlementPage: Int // private set，初始0
val settlementPageCount: Int
fun visibleSettlementRows(): List<SettlementDetailRow>
```
- 和牌弹窗改为世界坐标 Rect(180,95,920,540)。流局/无 breakdown 旧回放兼容原流程，可沿用原 modalRect 路径。不得影响 NEXT ROUND/CONTINUE/AGARI-YAME 原命中区域与合法行为。
- 三个标签按钮固定：役种 Rect(220,515,150,36)、宝牌 Rect(390,515,150,36)、计算 Rect(560,515,150,36)。上一页 Rect(220,195,100,34)、下一页 Rect(940,195,100,34)。页码及当前/总页数可见；越界按钮禁用。明细6行（顶部约 y=495、行高42），底部永久保留四家真实分数/增减和下一局按钮，不能被内容遮挡。必要时调整行内基线，不改变按钮区域。
- 中文使用现有 NotoSansSC TTF，FreeType incremental 按需字形，独立字体资源懒初始化并正确 dispose，不能在 headless 构造时访问 GL。无需全局字体替换。不要把中文画到默认 BitmapFont。文字真实像素宽度检查换行/分行；不靠无限缩字号挤满。每条役种独立一行、无省略号隐藏项目。长公式可拆为多行；牌行保证可显示6张小牌（指示牌、目标、最多4张命中）。
- YAKU：全部选中拆解役种逐项“名称 N番”或“名称 役满”；注明最高得点拆解；显示役种小计、表/里/赤番数各自小计，总番等式（普通）；役满合计N倍，明确不叠普通役/宝牌番。
- DORA：三个类别即使0也显示。每个指示项单独一行，文字说明“指示 → 宝牌：命中N张 +N番”，实际牌面依次显示 indicator,target,命中的物理牌；说明前两张含义，不只用数字符号。5个表+5个里都可翻页看全。赤宝牌展示各红五真实牌面、每张1番。重复指示计次及红五叠加的简短解释。非立直显示“未立直，里宝牌不适用”，且不出现里牌面。役满所有类别明确命中但不计番。
- CALC：总番组成、每条符数贡献、小计/进位/特殊规则、基本点精确公式及满贯等上限、庄闲荣和/自摸支付倍率、逐笔向上取整到100、本场每人100或放铳300、本场数、供托根数×1000、最终总得点。公式长时独立分行，不截断。役满跳过无意义符数，解释8000×倍数。支付用结算快照两个布尔值，不能用已轮转庄家。
- 分页纯函数必须保持原序无丢失/重复；切换标签重置页0；重复点击当前标签也重置；新结算/新一局重置；所有点击均不提交出牌/吃碰杠/Pass。表中所有总分必须与真实 ScoreSettled 一致。
- 可提取 SettlementDetailRenderer，避免继续在 GameScreen 堆积大量文本布局代码。旧 SettlementBreakdownFormatter 对外 API 保留供既有测试/旧数据使用。

## Codex 验收
领域：独立手算普通/满贯/役满，多指示含0命中、里门槛、红五叠加、荣和牌不重计、副露杠4张、零宝牌、无役不可凭宝牌和；大番公式精确与封顶；事件深度隔离；本场/供托总分与实际扣款一致。
表现：所有役条目和逐指示命中可翻页，中文/真实牌面可读，输入分页无动作提交；真实 OpenGL 截图核对边界，旧数据/流局/下一局回归。
运行完整 test、桌面编译、Android debug 构建。Android 仅声明构建验证，不虚称真机测试。

## 规则依据边界
宝牌/里宝牌、符与支付规则参考 EMA Riichi 2025 官方规则 https://mahjong-europe.org/portal/images/docs/Riichi-rules-2025-EN.pdf （浏览检索已查到，PDF 直接抓取受限）。保留本项目既定赤五、累计役满与多倍役满配置，不把 EMA 无赤牌的赛事差异带入游戏。
