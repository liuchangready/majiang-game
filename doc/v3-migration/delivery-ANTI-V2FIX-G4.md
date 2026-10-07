# 任务 ANTI-V2FIX-G4 交付报告：立直与副露确认分支校验最新快照 (GameScreen)

- **交付日期**：2026-10-05
- **执行角色**：Antigravity (Task ANTI-V2FIX-G4 生产代码修复)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、构建与审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不使用命令行或外部终端执行）

---

## 1. 变更清单与所有权范围

严格遵循任务授权范围，仅修改产品 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` 中 `touchDown` 的 CONFIRM RIICHI 及 CONFIRM 副露两个相邻分支，不修改任何测试文件或构建脚本：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | `touchDown` 中 `btnRiichiRect` 与 `btnMeldConfirmRect` 确认分支读取 `confirmationAction` 后，通过 `val latestView = client.snapshot?.view ?: currentView` 获取最新快照视图并以 `latestView.legalActions.contains(action)` 校验合法性；失效时取消选择并调用 `refreshLocalSelectionModels()` 刷新本地模型，消费点击且不向服务端提交陈旧动作；候选合法时维持原异步提交与拒绝回执刷新流程；未选牌时依然仅消费点击不发动作。 |
| `doc/v3-migration/delivery-ANTI-V2FIX-G4.md` | 新增 | 本交付报告 |

---

## 2. 根因分析与代码实现

### 2.1 根因分析
在移除旧 legacy polling 后，若服务端或客户端底层 `client.snapshot` 已完成增量更新，但 UI 订阅派发尚未执行完成时，本地 `currentView` 仍暂存旧候选动作。在用户点击 CONFIRM（立直或副露确认）时，先前代码仅校验 `currentView.legalActions.contains(action)`，导致判定为合法并向 `client.choose(action)` 提交了已在底层快照中失效的陈旧动作（如在 `MeldChoiceScreenContractTest:127` 中导致 stale 确认产生 1 次 choose 请求，预期应为 0 次）。

### 2.2 修复方案
在 `touchDown` 的立直确认和副露确认两处分支：
1. 读取待确认动作后获取最新视图：`val latestView = client.snapshot?.view ?: currentView`。
2. 动作合法性判定统一基于 `latestView.legalActions.contains(action)`。
3. 若动作已失效（`action != null && !latestView.legalActions.contains(action)`）：
   - 调用对应模型的 `cancel()` 退出选牌模式；
   - 调用 `refreshLocalSelectionModels()` 立即拉齐 `currentView` 与界面选择模型；
   - 消费本次点击（返回 `true`），绝不调用 `submitActionAsync` 发起提交。
4. 候选动作仍合法时：正常取消选择并异步提交。
5. 未选择动作（`action == null`）时：不发起提交，消费本次点击。

仅针对本地已缓存快照进行轻量合法性预检，不同步查宿主、不阻塞线程、不破坏服务端最终校验权威。

---

## 3. 验收准备

生产代码已修改完成并持久化，请 Codex 进行下一步验证：
- 重新运行 `core` 模块测试，重点验证 `MeldChoiceScreenContractTest`（特别是第 127 行 `rejected and silently stale confirmations exit without committing another candidate` 用例）；
- 执行图形界面冒烟回归与完整构建检查。
