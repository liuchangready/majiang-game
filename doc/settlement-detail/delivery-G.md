# 🀄 结算明细可读性与可核对性交付报告 (Task G - 第二轮精确修复)

- **交付日期**：2026-09-30
- **开发角色**：Antigravity (生产开发)
- **检视/构建/测试角色**：Codex
- **代码库**：`D:/majiang`
- **范围限制**：严格限定于 U 展示层生产文件，不修改测试用例、不修改 Gradle 构建、不修改 R 领域层文件、不执行外部命令行。

---

## 1. 修复背景与缺陷定位

在 Task F 交付基础上，Codex 在 GL 测试套件与验收核对中发现两个关键问题：

1. **`SettlementDetailRenderer.wrapRowText` 边界越界与超宽问题**：
   - 原代码使用 `for (i in 1 until text.length)` 探测前缀长度，当整个字符串恰好仅最后一个字超出 `maxWidth` 时（例如 Codex GL harness 构造的重复“测”字符串），该循环在 `i = text.length - 1` 处结束且未能触发 `width > maxWidth` 的 `break` 分支。
   - 这导致 `bestSplit` 保持初始值 `text.length`。随后的标点回退扫描从 `j = bestSplit` 开始访问 `text[j]`，直接引发 `StringIndexOutOfBoundsException`。
   - 此外，回退扫描若在 `bestSplit` 处命中分隔符，赋值 `splitIndex = j + 1` 会得到 `bestSplit + 1`，导致切出的第一行再次超出 `maxWidth`。
2. **CALC 符数进位数学逻辑错误（open ron minimum）**：
   - 原展示逻辑中，进位行使用 `finalFu` 展示取整结果。在副露荣和保底手牌中（`subtotal=20, roundedUpTo=20, finalFu=30, specialNote="荣和 30符保底"`），由于 `subtotal != finalFu`，进位行被渲染为 `小计 20 符 → 切上向上取整至十位：30 符`，出现“20 向上取整到十位是 30”的数学荒谬表述。

---

## 2. 详细修改内容

### 2.1 `SettlementDetailRenderer.kt` (文本排版与测量)

1. **重构 `wrapRowText` 算法**：
   - 使用二分查找在 `1..remaining.length` 范围内精确测算最大可容纳前缀字符数 `bestSplit`，确保能覆盖仅最后一个字符超出 `maxWidth` 的边界情形。
   - 标点回退扫描严格限制在已容纳前缀内部：`for (j in (bestSplit - 1) downTo lookback)`，保证扫描索引完全落在 `0 until remaining.length` (`0..length-1`) 内，绝不发生越界。
   - 设定 `splitIndex = maxOf(1, minOf(splitIndex, bestSplit))`，确保切分点绝不超过 `bestSplit`，杜绝首行超宽。
   - 增加逐字符收缩校验循环：`while (splitIndex > 1 && testLayout.width > maxWidth) { splitIndex-- }`，硬性保证每一行的 `GlyphLayout.width <= maxWidth`。
   - 采用循环推进截取多行，文字内容保留完整，不截断、不丢失字符。
2. **兼顾多调用契约**：
   - 提供 `fun wrapRowText(font, text, maxWidth)` 实例方法，同时在 `companion object` 中提供等价的静态纯函数实现，确保测试 harness 无论通过实例还是伴生对象调用均可直接执行。
3. **安全渲染任意行数**：
   - `renderDetailModal` 中更新行绘制逻辑，安全兼容 0 行（跳过）、1 行、2 行（标准行距）及多行（自适应等分步长），避免任何索引越界。

### 2.2 `SettlementDetailPresenter.kt` (CALC 符数推导展示)

1. **进位行严格使用 `roundedUpTo`**：
   - 判断条件调整为 `b.fu.subtotal == b.fu.roundedUpTo`。
   - 若相等且无保底差值（`roundedUpTo == finalFu`），输出 `符数进位：小计 ${b.fu.subtotal} 符已整十，最终计 ${b.fu.roundedUpTo} 符`（兼容既有基线测试）；
   - 若相等但存在保底差值（如 open ron 20/30），输出 `符数进位：小计 ${b.fu.subtotal} 符已整十，向上取整至十位：${b.fu.roundedUpTo} 符`，数值为 20，彻底消除数学错误；
   - 若不相等（如 22 符），输出 `符数进位：小计 ${b.fu.subtotal} 符 → 切上向上取整至十位：${b.fu.roundedUpTo} 符`。
2. **保底说明与最终计符独立呈现**：
   - 当 `b.fu.finalFu != b.fu.roundedUpTo` 时，独立新增保底行：
     `符数保底说明：${b.fu.specialNote ?: "荣和 30符保底"}，最终计 ${b.fu.finalFu} 符`；
   - 既有七对子（25 符）与门前平和自摸（20 符）分支完全保留；
   - 完美支持 open ron minimum 的验收核对。

---

## 3. 文件修改清单与所有权审查

| 文件路径 | 所属 | 修改类型 | 说明 |
|:---|:---:|:---:|:---|
| [`SettlementDetailRenderer.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt) | Task U | 修改 | 修复 `wrapRowText` 二分前缀与安全回退，增加 companion object，保证每行 width <= maxWidth |
| [`SettlementDetailPresenter.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/SettlementDetailPresenter.kt) | Task U | 修改 | 进位行使用 `roundedUpTo`，保底行独立说明最终计符，保留七对25与平和20分支 |
| [`delivery-G.md`](file:///D:/majiang/doc/settlement-detail/delivery-G.md) | Task G | 新增 | 本次交付报告文档 |

- **R 领域文件未修改**（`WinningScoreEvaluator.kt`、`ScoreCalculator.kt`、`GameEventSnapshot.kt` 均未触碰）。
- **未改动任何测试文件或构建脚本**。
- **未执行任何命令或改变系统权限**。
