# 任务 ANTI-SHUFFLE-D3 交付报告：洗牌面板中文字形覆盖补全与异步回执守卫加固

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-SHUFFLE-D3 生产代码实现)
- **调用链路**：codex -> antigravity
- **唯一写入范围**：
  - `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationPanel.kt`
  - `doc/v3-migration/delivery-ANTI-SHUFFLE-D3.md`
- **契约基准**：
  - Codex 验收测试：`core/src/test/kotlin/com/majiang/ShufflePreparationUiAcceptanceTest.kt` (`Chinese font subset covers player labels and translated errors`)
  - [delivery-ANTI-SHUFFLE-D2.md](delivery-ANTI-SHUFFLE-D2.md)

---

## 1. 修改要点与实现明细

### 1.1 中文字形集合由 UI 常量列表动态派生与补全 (Bounded Font Coverage)
- **问题诊断**：
  原 `SHUFFLE_PANEL_CHARS` 手工枚举字库遗漏了中文逗号 `，` 以及 `translateServerReason` 翻译文本中的关键汉字（如 `请 该 更 接 方 案 或 准 间 截 止` 等），导致字形覆盖测试失败并在真实渲染中可能出现方框缺字。
- **解决方案**：
  1. 在 `ShufflePreparationPanel.Companion` 引入结构化常量文本列表 `UI_STRINGS`，收敛面板所有大标题、点数预览、说明文案、选项标签、提示信息、按钮文案以及全部服务端错误翻译（包含 `UNSUPPORTED`、`TIMEOUT`、`INSUFFICIENT`、`NOT_AVAILABLE`、`STALE`、`ROUND`、`CLOSED`、`ALREADY`、`INVALID`、`PAUSED`、`EXPIRED`、`ERROR`、网络异常等）；
  2. 扩展全半角标点符号集，完整包含中文逗号 `，`、顿号 `、`、方括号 `[]`、竖线 `|`、对勾 `√` 与参考符号 `※`；
  3. `SHUFFLE_PANEL_CHARS` 由 `ASCII_AND_PUNCTUATION + UI_STRINGS.joinToString("")` 动态去重派生，彻底杜绝手工硬编码字串漏字；
  4. 保持 `@JvmField val SHUFFLE_PANEL_CHARS` 的公开属性签名与 LibGDX `FreeTypeFontParameter.characters` 契约，界面排版与几何包围盒稳定无变动。

### 1.2 异步提交回调守卫加固 (Late Callback Guard)
- **状态守卫条件收敛**：
  在 `submitPlan` 的 `future.whenComplete` 回调中，严格限定执行条件：
  ```kotlin
  if (isDisposed || client.isClosed || !model.isActive || model.roundId != targetRoundId) {
      return@whenComplete
  }
  ```
- **请求 ID 冲突过滤与已确认快照平滑过渡**：
  ```kotlin
  val currentSubmitting = model.submittingRequestId
  if (currentSubmitting != null && currentSubmitting != targetRequestId) {
      return@whenComplete
  }
  ```
  - 当模型当前在途为**不同且非空**的请求 ID 时，丢弃旧回调，避免污染新请求状态；
  - 当快照已先行到达且匹配确认时，快照更新已将 `model.submittingRequestId` 置为 `null`（`submittingIdclearedbyupdate`），此时 `currentSubmitting` 为 `null`，回调守卫**不丢弃**成功的确权回执（`onSubmissionAccepted`），保证客户端与表现层状态一致；
  - 针对权威已确认（`model.isAuthorityConfirmed == true`）的场景，阻断晚到错误或拒绝回执覆盖错误提示，确保状态机单向向前流转。

---

## 2. 变更文件清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationPanel.kt` | 修改 | `UI_STRINGS` 派生 `SHUFFLE_PANEL_CHARS`（覆盖全量字形与标点 `，`）、`submitPlan` 加固 `!client.isClosed && model.isActive && model.roundId == targetRoundId` 与 `submittingRequestId` 校验 |
| `doc/v3-migration/delivery-ANTI-SHUFFLE-D3.md` | 新增 | 本交付报告 |

---

## 3. 验收声明

- 本任务严格遵循职责边界，仅修改 `ShufflePreparationPanel.kt` 生产实现与本交付文档；
- 未调用任何 shell/terminal 命令，未修改任何 test 或 Gradle 文件；
- 生产代码已严格遵循 `Chinese font subset covers player labels and translated errors` 测试规范与晚到回调契约。交付完毕，交由 Codex 进行构建、测试与最终验收。
