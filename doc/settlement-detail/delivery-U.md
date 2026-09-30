# Task U 交付文档：结算明细可读性与可核对性呈现层实现

## 1. 任务概述与职责边界

本次交付为 **Task U（展示与呈现层）** 完整实现，遵循与 **Task R（领域与结算服务）** 的并行开发与解耦规范：
- **不修改 R 文件**：未改动 `WinningScoreEvaluator.kt`、`ScoreCalculator.kt`、`GameEventSnapshot.kt`、`GameStateMachine.kt` 与 `SettlementService.kt`。
- **不修改测试或构建配置**：未修改 `build.gradle`、Gradle 依赖或任何已有测试代码。
- **保留旧实现契约**：保留 `SettlementBreakdownFormatter` 及其对外 API 供既有测试与无 breakdown 数据使用；流局与无 breakdown 结算完全兼容原 `modalRect` 路径。
- **纯内置工具开发**：完全基于代码编辑与文件写入工具完成，无执行 Shell 或外部命令。

---

## 2. 交付文件清单

1. [`core/src/main/kotlin/com/majiang/screen/SettlementDetailPresenter.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/SettlementDetailPresenter.kt)
   - 纯函数 Presenter，无 libGDX 图形/GL 强依赖，完全支持在 headless 环境中执行契约单测。
   - 实现 `enum class SettlementSection { YAKU, DORA, CALC }` 与 `data class SettlementDetailRow(val text: String, val tiles: List<Tile> = emptyList())`。
   - 实现 `const val ROWS_PER_PAGE = 6`、`rows(b, section)`、`pageCount(b, section)`、`page(b, section, index)`。
   - 具备安全反射与优雅回退机制，无缝兼容 R 领域层新增的 `indicatorHits`、`uraHits`、`uraEligible`、`isDealer`、`isTsumo` 及 `rawBasePointsExact`。
2. [`core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt)
   - 从 `GameScreen` 中解耦抽离的专用渲染器，统一管理 FreeType incremental 中文字体生命周期。
   - 负责世界坐标 `Rect(180, 95, 920, 540)` 下的模态弹窗绘制：包含三标签栏、6行明细与麻将牌面（支持指示牌、目标牌、命中牌等最多6张小牌）、上一页/下一页按钮、底部四家实时得分变化与下一局按钮。
3. [`core/src/main/kotlin/com/majiang/screen/GameScreen.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/GameScreen.kt)
   - 接入只读测试契约属性：
     - `val settlementSection: SettlementSection` (初始 `YAKU`)
     - `val settlementPage: Int` (初始 `0`)
     - `val settlementPageCount: Int`
     - `fun visibleSettlementRows(): List<SettlementDetailRow>`
   - 接入标签矩形与分页矩形访问接口：`detailModalRect`、`tabYakuRect`、`tabDoraRect`、`tabCalcRect`、`btnPrevPageRect`、`btnNextPageRect`。
   - 交互控制：在模态弹窗开启时，拦截所有外部点击，杜绝误触出牌/副露/Pass；标签切换或重复点击重置为第 0 页；翻页按钮在越界时禁用；新结算及新一局自动重置。
   - 资源释放：在 `GameScreen.dispose()` 中协同释放 `settlementDetailRenderer`。

---

## 3. 三大板块推导行构建规范

### 3.1 役种板块 (YAKU)
- **全部拆解逐项**：普通役展示 `名称 N 番`；役满展示 `名称 役满`（多倍时注明倍数）。
- **拆解说明**：明确注明 `【拆解说明】已选最高得点之唯一合法拆解方案`。
- **普通结算小计与等式**：
  - `役种番数小计：N 番`
  - `宝牌番数分项：表宝牌 A 番 | 里宝牌 B 番 | 赤宝牌 C 番 (宝牌小计 D 番)`
  - `总番数等式：役种 N 番 + 宝牌 D 番 = 合计 M 番`
- **役满结算规则说明**：
  - 汇总 `N 倍役满 (固定基本点 N × 8000 点)`
  - 明确标注 `役满不叠加普通役种翻数与宝牌番数`
  - 若命中宝牌，明确提示 `命中但役满不计番`。

### 3.2 宝牌板块 (DORA)
- **零命中全显示**：表宝牌、里宝牌、赤宝牌即使 0 命中均独立显示对应行。
- **指示项格式**：
  - 每行文字：`表指示牌 N [X] → 宝牌 [Y]：命中 M 张 +M 番`（役满时标注 `(役满不计番)`）。
  - 行内牌面：依次附带 `indicator`（指示牌）、`target`（目标牌）、`tiles`（手牌物理命中牌），支持展示多达 6 张牌。
  - 行内图例说明首张为指示牌、第二张为宝牌目标。
- **里宝牌规则控制**：
  - 若未立直（`!uraEligible`）：输出 `里宝牌：未立直，里宝牌不适用 (不展示里指示牌与牌面)`，且 `tiles` 为空。
  - 若已立直：逐指示牌输出命中情况与牌面。
- **赤宝牌**：展示各红五真实物理牌面（`0m`/`0p`/`0s`），说明每张 1 番。
- **规则提示**：简短说明 `重复指示牌独立计次加算；赤宝牌可与表/里宝牌多重叠加加番`。

### 3.3 计算推导板块 (CALC)
- **总番与符数**：
  - 普通和牌：输出总番组成、每条符数明细贡献（如底符、刻子、面子、雀头、听牌、自摸）、进位切上规则与最终符数。
  - 役满和牌：输出 `役满跳过常规符数计算` 与固定 8000 × 倍数推导。
- **基本点与上限封顶**：
  - 公式独立成行：`基本点原始公式：fu 符 × 2^(2 + han 番) = rawBasePointsExact 点`（使用精确大数表示，不截断、不溢出）。
  - 封顶说明行：达到满贯/跳满/倍满/三倍满/累计役满时独立提示 `上限封顶规则：达到【capReason】封顶，基本点调整为 basePoints 点`。
- **支付倍率与取整**：
  - 基于结算快照的 `isDealer` 与 `isTsumo`（不依赖后置轮转状态）：
    - 庄家自摸：三家闲家各付 `基本点 × 2`（向上取整至百位）。
    - 庄家荣和：放铳者独付 `基本点 × 6`（向上取整至百位）。
    - 闲家自摸：庄家付 `基本点 × 2`，两闲家各付 `基本点 × 1`（逐笔向上取整至百位）。
    - 闲家荣和：放铳者独付 `基本点 × 4`（向上取整至百位）。
- **场棒与供托**：
  - 独立列出本场数、每人 100 点或放铳 300 点累计，以及供托立直棒根数 × 1000。
  - 最终总得点与 `totalPoints` 严格保持一致。

---

## 4. FreeType incremental 字体策略与无头隔离

1. **资源复用**：直接利用项目中现有的 `assets/fonts/NotoSansSC-Regular.ttf`。
2. **懒加载与无头安全**：
   - 不在任何构造器或静态块中触碰 OpenGL。
   - `SettlementDetailRenderer.getOrInitChineseFont()` 在初始化前严格校验 `Gdx.gl != null && Gdx.graphics != null && Gdx.files != null`。
   - 在 headless 单测运行环境下自动返回 `null` 并安全回退，绝不抛出任何 GL 异常或空指针。
3. **动态按需字形生成**：
   - 配置 `param.incremental = true` 与 `param.characters = FreeTypeFontGenerator.DEFAULT_CHARS`。
   - 在运行时绘制汉字时由 FreeType 自动按需栅格化字形并填充纹理，避免巨型预烘焙贴图开销。
4. **生命周期规范**：
   - `SettlementDetailRenderer` 实现 `Disposable`。
   - 在 `GameScreen.dispose()` 中调用 `settlementDetailRenderer.dispose()`，依次安全释放 `BitmapFont` 与 `FreeTypeFontGenerator`。

---

## 5. 交互几何与输入保护

| 控件 | 几何世界坐标 Rect | 说明 |
| :--- | :--- | :--- |
| 和牌详情弹窗底板 | `Rect(180, 95, 920, 540)` | 全局模态拦截 |
| 役种明细标签 (YAKU) | `Rect(220, 515, 150, 36)` | 点击切换并重置到第0页 |
| 宝牌明细标签 (DORA) | `Rect(390, 515, 150, 36)` | 点击切换并重置到第0页 |
| 计算推导标签 (CALC) | `Rect(560, 515, 150, 36)` | 点击切换并重置到第0页 |
| 上一页按钮 | `Rect(220, 195, 100, 34)` | `page <= 0` 时置灰且禁用 |
| 下一页按钮 | `Rect(940, 195, 100, 34)` | `page >= pageCount - 1` 时置灰且禁用 |
| 6 行明细内容区域 | 顶部起始 `y = 495`，行高 `42` | `y = 243..495`，右侧预留牌面展示 |
| 推进按钮 (下一局/续庄) | `y = 145..191` | 保持既有 `btnNextRoundRect` 等区域 |
| 四家结算得分汇总 | `y = 98..135` | 底部常驻，不被明细内容遮挡 |

所有在弹窗展示期间发生的触控点击均被完全拦截并返回 `true`，杜绝任何穿透至牌桌导致误出牌、误副露或误 Pass 的隐患。
