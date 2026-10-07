# 任务 ANTI-SHUFFLE-D4 交付报告：Kotlin 跨模块 Smart Cast 编译修复

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-SHUFFLE-D4 生产代码修复)
- **调用链路**：codex -> antigravity
- **唯一修改代码文件**：
  - `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationModel.kt`
- **交付文档**：
  - `doc/v3-migration/delivery-ANTI-SHUFFLE-D4.md`

---

## 1. 修复明细

### 1.1 跨模块公共属性 Smart Cast 编译报错修复
- **问题原因**：
  在 `ShufflePreparationModel.update` 中，`prep` 为来自 `engine` 模块的 `ShufflePreparationView`。其 `prep.selection` 为跨模块公开属性，Kotlin 编译器无法跨模块保证其不可变性，导致在非空判断后对其直接调用 `prep.selection.initialTargets` 与 `prep.selection.drawTargets` 报出 `Smart cast to 'ShuffleSelection' is impossible, because 'prep.selection' is a public API property declared in different module` 编译错误（原行号 358、360）。
- **解决方案**：
  在空值检查前通过局部变量捕获 `val selected = prep.selection`，并在 `if (selected != null)` 块内使用 `selected`，配合 `selected.deepCopy()` 赋值给 `confirmedSelection`，消除了跨模块 smart cast 限制，保障编译与不可变防御性复制。

---

## 2. 变更清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationModel.kt` | 修改 | 在 `prep.confirmed` 分支中引入 `val selected = prep.selection` 局部变量捕获，修复 Kotlin 编译器跨模块 smart cast 限制，保持其它逻辑不变 |
| `doc/v3-migration/delivery-ANTI-SHUFFLE-D4.md` | 新增 | 本交付报告 |

---

## 3. 验收声明

- 仅修改 `ShufflePreparationModel.kt` 生产代码，未修改任何测试或构建配置；
- 未调用任何 shell/terminal 命令；
- 交付完毕，交由 Codex / Root 执行构建与测试验收。
