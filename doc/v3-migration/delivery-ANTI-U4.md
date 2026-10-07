# 任务 ANTI-U4 交付报告：模态输入回归缺陷修复 (GameScreen)

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task ANTI-U4 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、构建与审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不使用命令行或外部终端执行）
- **目标设计与任务契约**：
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  - [STATUS.md](STATUS.md)
  - [delivery-ANTI-U3.md](delivery-ANTI-U3.md)
  - [MeldChoiceScreenContractTest.kt](../../core/src/test/kotlin/com/majiang/screen/MeldChoiceScreenContractTest.kt)

---

## 1. 唯一写入范围与交付清单

严格遵循任务所有权契约（仅限 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` 与本交付报告；未修改任何测试源码、Gradle 构建脚本或其他生产模块）：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 1. 修复回归 1：在 `btnMeldConfirmRect` 点击处理中，当 `confirmationAction() == null`（未选择候选方案）时，仅消费点击并保持模态开启，不无条件执行 `cancel()`，避免多方案预览期间确认导致弹窗意外关闭及后续卡片点击失效；仅在实际选中提交或陈旧状态时才执行 `cancel()`。<br>2. 修复回归 2：在 `touchDown` 起始调用 `syncLegacyEndpointIfPresent()` 前捕获模态开启上下文（`wasMeldSelecting`、`wasRiichiSelecting`）；当端点刷新使先前已开启的模态弹窗失效关闭时，立即吞掉该次输入（返回 `true`）并保持关闭，不提交任何动作，防止点击穿透到底层桌面或其它操作按钮；保留返回主菜单（Back）的优先处理。 |
| `doc/v3-migration/delivery-ANTI-U4.md` | 新增 | 本交付报告 |

---

## 2. 缺陷根因分析与代码变更

### 2.1 回归 1：未选择方案点击确认误关闭模态弹窗 (`each of three chi combinations can be independently previewed then committed`)

- **根因分析**：
  在 `btnMeldConfirmRect` 的点击处理中，代码在获取 `confirmationAction()` 后无条件调用了 `meldSelectionModel.cancel()`。在三吃等多方案场景中，初始进入选牌并未自动选中任何项（`confirmationAction() == null`）。当用户在未选择具体卡片前点击 CONFIRM 按钮时，弹窗被错误关闭，导致随后的候选卡片点击无法命中选牌逻辑而穿透并返回 `false`。
- **修复方案**：
  对齐立直选择模式（`btnRiichiRect`）的设计规范：
  - 若 `action != null && currentView.legalActions.contains(action)`：正常关闭模态（`cancel()`）并异步提交动作；
  - 若 `action != null && !currentView.legalActions.contains(action)`：属于陈旧状态，关闭模态（`cancel()`），不发起提交；
  - 若 `action == null`：不调用 `cancel()`，保持模态弹窗开启，消费点击并返回 `true`（不自动代选，不提交 Pass）。
- **变更代码行** (`core/src/main/kotlin/com/majiang/screen/GameScreen.kt` 约 3167-3185 行)：
  ```kotlin
  // 点击 CONFIRM 按钮（确认只提交被选中合法动作的原始内容一次）
  if (btnMeldConfirmRect.contains(tx, ty)) {
      syncLegacyEndpointIfPresent()
      val action = meldSelectionModel.confirmationAction()
      if (action != null && currentView.legalActions.contains(action)) {
          meldSelectionModel.cancel()
          submitActionAsync(action).whenComplete { receipt, _ ->
              runOnUi {
                  if (receipt == null || receipt.status == ReceiptStatus.REJECTED) {
                      refreshLocalSelectionModels()
                  }
              }
          }
          return true
      }
      if (action != null && !currentView.legalActions.contains(action)) {
          meldSelectionModel.cancel()
      }
      return true
  }
  ```

### 2.2 回归 2：静默陈旧确认输入未吞掉导致穿透与返回 false (`rejected and silently stale confirmations exit without committing another candidate`)

- **根因分析**：
  在 `touchDown` 开始处，`syncLegacyEndpointIfPresent()` 会在用户交互开始时主动拉取端点最新状态。当会话合法动作静默变更（如 `stale == true`，合法动作变为仅 `Pass`）时，`syncLegacyEndpointIfPresent()` 发现视图变化并调用了模型的 `update(currentView)`，使得先前打开的模态选择直接被清空关闭（`isSelecting` 变为 `false`）。
  随后后续代码因为 `isSelecting == false` 而完全跳过了副露模态处理分支，穿透到底层普通牌桌，最终因未命中任何可用按钮返回了 `false`，不仅导致测试中的 `confirm(s)` 断言失败，在实际游戏中还存在误触底层同坐标按钮（如立直按钮）的穿透风险。
- **修复方案**：
  在 `syncLegacyEndpointIfPresent()` 执行前捕获当前模态开启状态（`wasMeldSelecting` 与 `wasRiichiSelecting`）。
  在处理完顶部返回主菜单按钮（`< MENU`）后，检查是否发生“刷新导致先前打开的模态失效关闭”（`modalInvalidatedByRefresh`）：
  - 若发生，立即消费吞掉当前输入并返回 `true`；
  - 保持模态关闭状态，不向底层端点提交任何动作（`attempts == 0`）；
  - 杜绝点击穿透到底层桌面其它组件。
  - 对于未失效但端点显式拒绝（`Rejected`）的情况，原有逻辑已成功记录 1 次尝试（`attempts == 1`）并在回调中保持关闭。
- **变更代码行** (`core/src/main/kotlin/com/majiang/screen/GameScreen.kt` 约 2944-2962 行)：
  ```kotlin
  val wasMeldSelecting = meldSelectionModel.isSelecting
  val wasRiichiSelecting = riichiSelectionModel.isSelecting

  syncLegacyEndpointIfPresent()

  // 1. 返回主菜单（释放会话与订阅）
  if (backButtonRect.contains(tx, ty)) {
      exitToMenu()
      return true
  }

  // 若端点刷新使先前已打开的模态选择弹窗失效关闭，吞掉本次输入并保持关闭，
  // 既不提交任何候选动作，也防止点击穿透到下层桌面或其它按钮
  val modalInvalidatedByRefresh = (wasMeldSelecting && !meldSelectionModel.isSelecting) ||
      (wasRiichiSelecting && !riichiSelectionModel.isSelecting)
  if (modalInvalidatedByRefresh) {
      return true
  }
  ```

---

## 3. 验收对照与交付确认

| 检查项 | 契约要求 | 交付状态 |
|---|---|---|
| 回归 1: 未选择确认 | 消费点击并保持模态开启，不关闭，不自动代选，不提交 Pass，允许后续继续点选卡片后确认 | 已完成 |
| 回归 1: 实际选择与陈旧 | 仅在实际合法选择提交或陈旧状态时关闭模态 | 已完成 |
| 回归 2: 静默陈旧确认 | 捕获 pre-refresh 上下文，刷新失效时吞掉输入（返回 true），不提交动作（attempts=0），防止穿透 | 已完成 |
| 回归 2: 显式拒绝 | 显式拒绝时保持 1 次尝试（attempts=1），消费点击并关闭模态 | 已保持 |
| 回归 2: 特殊返回行为 | 保留顶部返回主菜单（Back）的优先响应逻辑，无穿透风险 | 已保持 |
| 范围控制 | 仅修改 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` 与本交付文档，不修改任何测试或构建配置 | 严格遵循 |
