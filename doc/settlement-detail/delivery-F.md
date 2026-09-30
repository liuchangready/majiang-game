# Task F 交付文档：结算明细可读性与可核对性完善与预检修复

## 1. 任务概述与职责说明

本任务为 **Task F（UI 与呈现层 Followup 修正任务）**，依据 Codex 预检要求，在 Task U 的基础上彻底解决 9 项可读性、几何重叠、字体健壮性与反射清理问题。

严格遵守开发边界与工作契约：
- **修改范围仅限 UI 文件**：仅修改了 [`core/src/main/kotlin/com/majiang/screen/SettlementDetailPresenter.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/SettlementDetailPresenter.kt)、[`core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt) 与 [`core/src/main/kotlin/com/majiang/screen/GameScreen.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/GameScreen.kt)。
- **不改动 R 领域层**：未修改 `WinningScoreEvaluator.kt`、`ScoreCalculator.kt`、`GameEventSnapshot.kt`、`GameStateMachine.kt`、`SettlementService.kt`。
- **不改动测试与构建配置**：未修改任何测试用例，未修改 Gradle 配置或依赖。
- **全流程无终端命令与 MCP 调用**：仅使用内置文件读写和替换工具完成所有代码重构与排版实现。

---

## 2. 预检 9 项修复逐条落实对照

### 2.1 删除全部反射与猜测回退（Codex 预检要求 1）
- **变更说明**：全面清理了 `SettlementDetailPresenter.kt` 中的 `IndicatorHitItem` 临时类以及 `extractHits`、`getUraEligible`、`getIsDealer`、`getIsTsumo`、`getRawBasePointsExact` 等所有反射方法与启发式猜测。
- **直接使用 R 领域固定 API**：
  - 表宝牌指示逐项：`b.dora.indicatorHits`（项内为 `hit.indicator`、`hit.target`、`hit.tiles`）
  - 里宝牌指示逐项：`b.dora.uraHits`
  - 里宝牌资格：`b.uraEligible`（未立直绝不展示里指示牌与命中牌面）
  - 庄闲与自摸上下文：`b.isDealer`、`b.isTsumo`
  - 精确原始基本点字符串：`b.score.rawBasePointsExact`（原生支持高达 80 番的大数精确呈现）

### 2.2 实际行内排版测量与至多2行换行（Codex 预检要求 2）
- **像素测量与换行机制**：在 `SettlementDetailRenderer.wrapRowText` 中，引入 `GlyphLayout` 进行真实文本像素宽度测量。
  - 正文横向可用区间：`x = 220f .. 1060f`（宽度 840px）。
  - 带麻将牌明细行：牌面靠右排列，动态预留 `tiles.size × 27f + gap + 16f` 安全边距，文字在剩余有效宽度内自适应断句（优先在标点符号、空格与箭头处折行）。
  - 行内基线分布：在 42px 行高内，单行文字基线居中偏下（`rowBottom + 26f`）；折为 2 行时，第 1 行基线为 `rowTop - 13f`，第 2 行基线为 `rowTop - 31f`，行距精确为 18px，绝不发生纵向重叠或截断。
- **条目拆分策略**：在 `SettlementDetailPresenter` 中主动将超长信息拆分成独立行：
  - 基本点公式与等号结果拆分为 2 条：`基本点计算：${fu} 符 × 2^(2 + ${han} 番)` 与 `原始基本点：${rawBasePointsExact} 点`，即便是 80 番（约 27 位十进制大数）也能完美单行展示，不挤占版面。
  - 闲家自摸支付按庄家与闲家分列 2 行。
  - 本场棒、供托立直棒与最终总得点独立单行。

### 2.3 Fu 符数逐项独立分行（Codex 预检要求 3）
- **逐项明细行**：废除了原有将全部符数拼成单行的格式，改为每个 `FuLine` 占据一条独立的 `SettlementDetailRow`（如 `符数明细：底符 +20 符`、`符数明细：暗刻1m +8 符`）。
- **小计与进位行**：末尾独立生成 `符数小计：${subtotal} 符` 与 `符数进位：小计 ${subtotal} 符 → 切上向上取整至十位：${finalFu} 符`。
- **特例直接定符**：针对七对子（25 符）与门前平和自摸（20 符），明确标注为特例直接定符（如 `符数计算规则：七对子固定 25 符 (特例直接定符，不向十位取整进位)` 与 `门前平和自摸固定 20 符 (特例直接定符，舍弃2符自摸，不向十位进位)`），彻底消除“声称都向十位进位”的表述歧义。

### 2.4 本场自摸公式纠正与取整过程推导（Codex 预检要求 4）
- **本场自摸公式纠正**：纠正了“每人加付 100，合计 600”等不严谨表述，自摸时严格按照 `每人加付 100 × 本场数`，三人合计 `300 × 本场数` 计算（例如 2 本场：`本场棒加算：2 本场 (每人加付 100×2 = 200 点，三人合计 +600 点)`）；放铳时显示 `放铳者独付 300×本场数`。
- **支付取整明细推导**：展示 `未取整值 → 向上取整至百位 → 最终付款`，例如闲家自摸时清晰列出：
  - 庄家支付：`基本点×2 = 640 点 → 取整至百位：700 点`
  - 闲家支付：`基本点×1 = 320 点 → 取整至百位：400 点`
- **总得点具体数字加法**：最终总得点明确给出具体算式：
  `最终结算总得点：${baseTotal} 点 (基础) + ${honbaAddition} 点 (本场) + ${riichiAddition} 点 (供托) = ${totalPoints} 点`。

### 2.5 底部几何遮挡彻底消除（Codex 预检要求 5）
- **分数区域重构**：将四家实时得分与点数增减迁移至弹窗底部两行两列：
  - 第 1 行（P0、P1）：`posY = 132f`
  - 第 2 行（P2、P3）：`posY = 110f`
  - 列坐标：Col 0 为 `posX = 220f`，Col 1 为 `posX = 680f`
- **安全边距保证**：
  - 弹窗底板 Y 为 95f，分数文本（110f / 132f）完全落在弹窗内部。
  - NEXT ROUND / CONTINUE / AGARI-YAME 按钮 Y 坐标为 145f .. 191f，与分数（132f）保持了 13px 的清晰纵向间隔。
  - 上一页 / 下一页按钮 Y 坐标固定为 195f .. 229f，完全脱离了分数区域，再无重合重叠风险。

### 2.6 役满概览修正与和了牌定位（Codex 预检要求 6）
- **概览文案准确性**：役满时头部标题概览自动切换为 `${mult}倍役满 → ${totalPoints}点`（多倍或累计时准确呈现倍数），不再显示错误的 `0番 30符`；普通和牌正常显示 `${han}番 ${fu}符 → ${totalPoints}点`。
- **和了牌右置避让**：和了牌小牌面位置移动至弹窗最右侧（`x = 1030f, y = modalRect.y + 468f`），并在左侧标注 `和了牌` 字样（`x = 975f`）。
- 左侧胜者与得点概览文本限制在 `x = 220f .. 960f` 范围内，拥有超过 240px 的安全横向缓冲，彻底消除长概览文本与和牌牌面的重叠。

### 2.7 旧数据与流局中文字体复用与换行（Codex 预检要求 7）
- **中文字体全局复用**：`GameScreen.renderSettlementModal` 在处理无 breakdown 的旧数据及流局弹窗时，直接复用 `settlementDetailRenderer.getOrInitChineseFont()`，避免回退到无中文 glyph 的默认 BitmapFont。
- **流局原因本地化**：对 `EXHAUSTIVE_WALL`、`FOUR_RIICHI`、`FOUR_KANS`、`FOUR_WINDS`、`NINE_TERMINALS`、`TRIPLE_RON` 等各种流局状态提供清晰的中文说明与副标题。
- **旧役种换行防溢出**：对旧 win 记录中的役种列表采用宽度测量自动换行，并明确注明 `（旧记录无完整推导明细）`，既不越出 `modalRect` 边界，也不假造缺失的公式与指示牌。保留 NEXT ROUND、和了止与续庄的原有交互路径。

### 2.8 文本表述规范与役满规则严谨化（Codex 预检要求 8）
- **拆解说明**：修改为统一规范的 `【拆解说明】已选最高得点拆解方案`（移除可能存在误导的“唯一合法拆解”）。
- **宝牌计番说明**：Dora 表述明确说明 `总番按累计命中加番`，说明重复指示牌独立计次规则。
- **役满排他规则**：役满时明确输出 `计分规则：常规役种番数与宝牌加番规则在役满中不适用`，若有命中宝牌则标注 `(役满达成，常规加番规则不适用，命中但不计番)`。

### 2.9 字体加载安全与资源释放（Codex 预检要求 9）
- **防止重复加载与内存泄漏**：在 `SettlementDetailRenderer` 中设置 `fontInitAttempted` 守卫标志。初次加载一旦发生异常或文件不存在，立即记录日志到 `Gdx.app?.error` 并将错误信息存储在 `fontLoadError` 中，同时安全 dispose 临时 generator，后续渲染帧不再盲目重试，杜绝每帧重复创建 native 生成器引起的内存泄漏。
- **无头与生命周期安全**：在无 GL 环境中安全返回 null；在 `GameScreen.dispose()` 中协同调用 `settlementDetailRenderer.dispose()`，保证 generator 与 font 均被完全释放。

---

## 3. 修改文件清单

| 文件路径 | 修改性质 | 主要改动内容 |
| :--- | :--- | :--- |
| `core/src/main/kotlin/com/majiang/screen/SettlementDetailPresenter.kt` | 纯函数呈现逻辑重构 | 删除全部反射与猜测；直接使用 R API；Fu 逐项分行；七对/平和自摸特例定符；纠正本场自摸公式；大数公式拆行；严谨役满文案 |
| `core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt` | UI 渲染器重构 | GlyphLayout 像素测量换行（最多2行）；底部得分移至 y132/y110 消除遮挡；役满概览修正；和了牌右置；字体加载防泄漏与异常保护 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 场景接入与旧数据适配 | 回退弹窗接入中文字体；流局原因本地化；旧役种换行与无明细标注；协同释放渲染器 |

---

## 4. 验证与后续建议

- **契约与 API 兼容性**：所有公共只读属性 `settlementSection`、`settlementPage`、`settlementPageCount`、`visibleSettlementRows()` 及几何矩形常量完全保留且保持一致。
- **下一步**：请 Codex 进行 Gradle 完整测试套件执行、桌面版运行、实际 OpenGL 截图核对与 Android debug 构建验证。
