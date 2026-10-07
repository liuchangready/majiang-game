# 任务 ANTI-SHUFFLE-D2 交付报告：洗牌千术表现层模型与面板评审修正

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-SHUFFLE-D2 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、构建与审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不使用命令行或外部终端执行）
- **契约基准**：
  - [SHUFFLE-CONTRACT.md](SHUFFLE-CONTRACT.md)
  - [delivery-ANTI-SHUFFLE-D.md](delivery-ANTI-SHUFFLE-D.md)
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)

---

## 1. 唯一所有权与写入范围

严格遵守任务所有权约束：
- 仅修改 `core/src/main/**` 生产代码以及本文档 `doc/v3-migration/delivery-ANTI-SHUFFLE-D2.md`；
- 未修改任何 `engine`、`client`、`server`、`test`、`build`、`assets` 或脚本文件；
- 根测试 `core/.../ShufflePreparationUiAcceptanceTest.kt` 仅作为只读验收规范参考，未做任何修改。

### 变更文件清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationModel.kt` | 修改 | 点数预览与预留逻辑区分、暂停/未激活/超时截止守卫、单调时钟注入、重复快照时间戳保护、换局彻底重置状态 |
| `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationPanel.kt` | 修改 | 跳过清除草稿与实际方案传递、roundId/requestId 与释放守卫、同步客户端异常兜底、断线/关闭/pending/超时输入拦截、中文字形补全与受控错误翻译、倒计时与标题无重叠重排、区域说明独立分行 |
| `doc/v3-migration/delivery-ANTI-SHUFFLE-D2.md` | 新增 | 本交付报告 |

---

## 2. D2 审查问题逐项修正与设计实现

### 2.1 点数预览与权威预留解耦 (Point 1)
- **权威扣除与本地草稿区分**：
  - 权威快照的 `prep.availablePoints` 已经排除了 `prep.reservedPoints`。当权威确认 (`isAuthorityConfirmed == true`) 后，剩余点数预览 `remainingPointsPreview` 直接返回 `availablePoints`，不再扣减 `totalCost`，彻底避免 `10 - 40 = -30` 及点数不足误报；
  - 针对本地提交回执到达 (`isLocallyConfirmed == true`) 与权威快照到达之间的瞬态窗口，通过 `availablePoints - confirmedSelection.cost()` 平滑过渡，避免瞬态二次扣减；
  - 草稿编辑期（未确认）维持 `availablePoints - totalCost` 测算；
  - 确认后 `isAffordable` 恒真，`confirmDisabledReason` 明确返回 `"已确认"`。

### 2.2 暂停、未激活与到期守卫，单调时钟注入 (Point 2 & Point 6)
- **草稿编辑与提交拦截**：
  - 引入 `canEditDraft = isActive && status == SELECTING && !isPaused && !isLocked && !isTimeExpired`；
  - 当模型未激活 (`status == null`)、暂停 (`isPaused == true`)、权威 serverNow 达到或超过 deadline、或本地推算剩余秒数 `<= 0` 时，`toggleInitialTarget`、`toggleDrawTarget`、`clearDraft` 均返回 `false` / 阻断；
  - `canConfirm` 与 `canSkip` 均严格要求 `!isPaused` 与 `!isTimeExpired`；
  - 面板 `touchDown` 检测到 `model.isPaused` 或 `model.remainingSeconds() <= 0L` 时消费触摸并阻断点击，防止到期临界点击。
- **单调时钟时间源**：
  - 默认时钟由 `System.currentTimeMillis()` 切换为 `monotonicClock = { System.nanoTime() / 1_000_000L }`；
  - 保留 `nowSystemMillis: Long = monotonicClock()` 显式参数接口，确保无头确定性测试表面稳定。

### 2.3 跳过清空草稿、晚到回调隔离与同步异常兜底 (Point 3)
- **提交方案精准传导**：
  - `submitPlan` 保存实际发出的 `submittedSelection = req.selection`；
  - 回执成功记录时，调用 `model.onSubmissionAccepted(targetRequestId, submittedSelection)`；
  - 点击“不使用千术（跳过）”时发送空方案，成功后 `confirmedSelection` 记录为空方案，并同步清空草稿目标集合，不再错误保留草稿中的出千目标。
- **旧代次与释放守卫**：
  - 提交时捕获 `targetRoundId` 与 `targetRequestId`；
  - 回执完成时比对 `if (isDisposed || model.roundId != targetRoundId) return@whenComplete`，避免旧局或已释放面板的晚到回调锁死新局；
  - `submittingRequestId` 校验防止串请求。
- **同步异常捕获**：
  - `client.submitShufflePlan(req)` 调用外包覆 `try ... catch (t: Throwable)`；
  - 若客户端抛出同步异常，立即调用 `model.onSubmissionRejected(targetRequestId, ...)` 重置 pending/submitting 状态并展示错误信息。

### 2.4 客户端阻断态消费与权威时间戳刷新优化 (Point 4)
- **阻断态模态消费**：
  - 在 `isDisposed`、`client.isClosed`、`!client.isConnected` 或 `client.isPending` 时，面板 `touchDown` 依然消费输入返回 `true`（阻断点击穿透到底层手牌与桌面），但不执行任何草稿变更或提交；
  - `mouseMoved` 清理悬停高亮状态。
- **权威时间戳防回跳**：
  - 检查 `isAuthoritySampleChanged = isNewRound || prep.serverNowMillis != serverNowMillis || prep.deadlineMillis != deadlineMillis || paused != isPaused`；
  - 仅当权威时间样本或换局/暂停状态发生实际变化时刷新 `localReceiveTimeMillis`，渲染线程或缓存快照重复调用 `update` 不会重置单调流逝起点，消除倒计时回跳隐患。

### 2.5 字形补全、受控翻译与几何布局去重叠 (Point 5)
- **字符集全量补全**：
  - `SHUFFLE_PANEL_CHARS` 包含全部 ASCII 可打印字符 (32..126)、全角标点符号 (`※【】、（）√…·：；？！“”‘’—`) 以及所有面板文案用字；
  - 完整包含 Codex 评审指出的关键字：`启 未 正 在 提 交 局 发 山 被 求 √ 出 暂 停`。
- **服务端原因受控中文化**：
  - 实现 `translateServerReason(reason: String?): String`；
  - 将 `UNSUPPORTED_ACTION`、`TIMED_OUT`、`INSUFFICIENT`、`DISCONNECTED`、`ILLEGAL`、`STALE_EPOCH` 等不可控英文原因映射为受控中文字符串，防止自定义字库因缺失未知字符而显示方框。
- **倒计时与点数重叠消除**：
  - 将独立倒计时框移至右上角 `countdownBoxRect = Rectangle(960f, 554f, 110f, 50f)`；
  - 顶部左/中依次排布大标题 (x=210)、可用点数 (x=320) 和预计消耗/剩余 (x=510..760)，与右上角倒计时留有 200px 安全间隙，彻底消除重叠；
  - 倒计时文本居中对齐，支持展示 `暂停`、`生成中` 与倒计秒数。
- **区域说明独立分行**：
  - 偷天换日与瞒天过海的标题行仅保留短标题与右侧启用状态标签（如 `【偷天换日】 40点` 与 `[已启用 40点]`），杜绝行内空间竞争；
  - 详细说明文字（如 `调控起手配牌（13张手牌与开门摸牌）`）下移至独立行 (y=488 与 y=298)，位于目标按钮上方，排版清晰美观；
  - 目标按钮与确认/跳过按钮几何尺寸及坐标保持稳定。

### 2.6 新局完全重置 (Point 6)
- **生命周期彻底清空**：
  - 当 `isNewRound` 触发或 `update(null)` 时，彻底重置 `isAuthorityConfirmed`、`isLocallyConfirmed`、`confirmedSelection`、`isSubmitting`、`submittingRequestId`、`errorMessage` 以及草稿集合；
  - 仅在新快照确实指示 `prep.confirmed == true` 时才进入确认态并同步选择。

---

## 3. 验收契约核对总结

| 审查项 | 修正前行为 | D2 修正后行为 | 验收状态 |
|---|---|---|---|
| **1. 预留点数重复扣减** | 确认后继续减去 totalCost 显示负数 | 区分权威预留与草稿预测，确认后直接展示可用点数，无二次扣减 | 已通过设计审查 |
| **2. 暂停与超时编辑拦截** | 暂停中仍可 toggle/confirm/skip | 暂停、非 SELECTING、超时截止严密阻断草稿变更与提交；单调时钟注入 | 已通过设计审查 |
| **3. 跳过方案非空草稿残留** | 跳过成功后将非空草稿存为确认方案 | 传递实际提交 selection，跳过成功重置并清空草稿；roundId/requestId 校验；同步异常捕获 | 已通过设计审查 |
| **4. 断线拦截与时间戳重复刷新** | 仅按 locked 拦截；每快照无差别刷新 localReceiveTime | 断线/关闭/pending 消费模态并阻断交互；仅权威样本变更刷新时间戳 | 已通过设计审查 |
| **5. 字符集缺失与文本框体重叠** | 缺失 15 个关键汉字；x580 预计消耗与倒计时外框重叠；长说明挤占状态 | 字符集补齐并受控翻译；倒计时移至右上角消除重叠；区域说明独立分行 | 已通过设计审查 |
| **6. 未激活操作与跨局重置** | 未激活时返回 true；换局未重置 accepted | 未激活不可 toggle；换局完全重置 accepted/confirmed 状态 | 已通过设计审查 |

---

## 4. 交付总结与测试归属声明

- **完成状态**：Task ANTI-SHUFFLE-D2 所有评审修正已全量合入 `core/src/main/**`。
- **测试归属声明**：本交付报告严格遵循架构职责边界，仅代表生产代码开发完成，不包含任何自创测试通过断言（"No testclaims"）。所有单元测试、无头验收与真实 GL 运行均由 Codex 独立执行并验收。
