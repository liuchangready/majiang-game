# 🀄 交付记录：任务 F（审查修正：手牌等效张数守恒、选牌面板排版与 HUD 视口自适应）

- **任务名称**：振听统一判定、拒绝荣和与可见提示 — 审查修正 (Task F)
- **负责 Agent**：Antigravity（生产开发）
- **审查与验证**：Codex（架构设计、测试验证、构建与渲染审查）
- **交付日期**：2026-09-29

---

## 1. 交付概览与所有权边界

根据任务契约与审查反馈，Task F 仅针对已交付的生产代码进行缺陷修正与排版优化，严格限定修改以下两个生产文件：
1. `core/src/main/kotlin/com/majiang/rules/FuritenRules.kt`
2. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`

未触碰测试套件、构建配置（Gradle/依赖）或任务边界外的其他生产文件。所有测试运行与集成构建均由 Codex 独立执行。

---

## 2. 变更详情与契约实现

### 2.1 待牌计算手牌 13 张等效张数守恒 (`FuritenRules.kt`)
- **问题**：原 `FuritenRules.waits` 仅校验 `closedTiles.isEmpty()`，由于底层 `HandDecomposer.calculateWaits` 支持任意 `1 mod 3` 牌数，导致单张孤牌（未构成 4 副露）也被拆解并返回非法听牌。
- **修正**：
  - 在 `FuritenRules.waits` 入口添加手牌等效张数严格前置守恒判定：
    ```kotlin
    if (hand.melds.size !in 0..4 || closedTiles.size + 3 * hand.melds.size != 13) {
        return emptySet()
    }
    ```
  - 暗杠/明杠物理上为 4 张牌，但作为 1 个副露元素（`hand.melds.size`），在等效张数公式中计为 1 组（3 张等效牌）；
  - 保留可选参数 `closedTiles` 及其默认值 `hand.closedTiles`；
  - 保留原有副露与手牌占满 4 张后的第 5 张牌候选过滤逻辑；
  - `completesHand(hand, tile)` 内部调用 `waits(hand)`，自动继承该等效张数守恒防护，无需改动通用 `HandDecomposer`。

### 2.2 立直选牌面板排版与文本语义优化 (`GameScreen.kt`)
- **问题**：
  - 原选牌面板振听提示文本以 `"WARNING: THIS DISCARD CAUSES..."` 开头，在继承同巡振听或立直振听时存在语义误导；
  - 原警告文本从 x580 起始单行绘制，多项原因并存时严重溢出面板右边界（x1030）；
  - 绘制逻辑与无头测试辅助方法 `previewFuritenText()` 维护了重复的字符串拼接与原因推导代码。
- **修正**：
  - **标题行精简状态**：在面板标题行 x580 处呈现紧凑且准确的结果状态 `AFTER DISCARD: FURITEN - TSUMO ONLY`，并测量宽度自适应缩放，确保不超出面板；
  - **右侧留白区域详情排版**：13 种待牌牌面在两行自适应布局下最右侧止于约 x708，x740..1010 区域完全留白。将振听原因详情移至该留白区域（x740 起始），通过文本测量 `measureTextWidth` 与折行算法 `wrapText` 约束在 270px 宽度内分行渲染，绝对不覆盖待牌牌面；
  - **统一共享文本格式化**：
    - 在伴生对象中提取共享纯函数 `formatFuritenReasons(furiten)`、`previewFuritenCausesText(furiten)` 与 `previewFuritenFullText(furiten)`；
    - `currentFuritenText()`、`previewFuritenText()` 与面板/HUD 绘制全面共享统一的格式化逻辑，杜绝重复代码。

### 2.3 HUD 振听警示边界自适应与字体状态守恒 (`GameScreen.kt`)
- **HUD 视口自适应**：
  - 顶部 HUD 振听警示条区域为世界坐标 `x 380..900`（宽度 520px，左右内边距 10px，有效文本区域 500px）；
  - 在三项原因并存（`DISCARD, TEMPORARY, RIICHI`，49 字符）时，通过 `GlyphLayout` 预测量文本宽度，当超出 500px 时计算自适应缩放因子（`scale = maxTextWidth / textWidth`）并居中对齐，保证文字完整容纳在徽章框内。
- **字体渲染状态严格守恒**：
  - 在 `renderTopHud`、`renderWaitsPreviewPanel` 及 `[FURITEN]` 标签绘制代码块中，均在修改 `font.data.setScale` 与 `font.color` 前记录原状态（`prevScaleX`, `prevScaleY`, `prevColor`），并在 `finally` 块中精准恢复，防止字体状态泄漏影响全局后续渲染；
  - 完整保留所有 13 种待牌渲染与选牌交互语义。

---

## 3. 变更文件清单

| 文件路径 | 变更类型 | 核心修改点 |
|:---|:---|:---|
| [`core/src/main/kotlin/com/majiang/rules/FuritenRules.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/FuritenRules.kt) | 修改 | `waits` 增加 `hand.melds.size in 0..4 && closedTiles.size + 3 * hand.melds.size == 13` 等效张数校验 |
| [`core/src/main/kotlin/com/majiang/screen/GameScreen.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/GameScreen.kt) | 修改 | HUD 文本测量自适应、立直预览面板精简状态与右侧留白折行排版、共享文本格式化、字体状态恢复 |
| [`doc/furiten/delivery-F.md`](file:///D:/majiang/doc/furiten/delivery-F.md) | 新建 | 审查修正交付说明文档 |

---

## 4. 验证与交付状态

本交付已完成所有代码修改与自检。根据 headless 协作协议，不在此执行终端测试或 Gradle 构建命令，全量回归与测试验证由 Codex 统一执行。
