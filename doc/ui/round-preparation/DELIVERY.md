# 正式 APP · 开局准备界面交付记录

本文记录「先入牌桌、再选千术」正式 APP 开局界面的实际实现、真实命令与结果、运行截图、耗时与未验证项。
设计约束见 [DESIGN.md](DESIGN.md)；概念图（`01-table-entry.png` / `02-target-picker-final.png` / `03-confirmed-wait-final.png`）**只是设计稿**，
本轮交付的运行截图全部来自真实生产链的 LWJGL3 帧缓冲，见下文「运行截图」。

> 状态：**已实现；第 2、3、4、5 轮均已按 Codex 独立验收的缺陷返修完成，等待复验**。
> 本轮未做 Git 提交 / 推送，未修改 `ui_demo`、图片、`AGENTS.md`
> 与业务引擎（`engine/ShufflePreparationHandler`、`server/ServerTableHost` 保持上一轮继承状态）。

---

## 0.0 第 5 轮返修记录（Codex 第 4 轮复验缺陷）

| # | 缺陷 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | **「至少一帧完全不透明回执」的时序缺陷**：`syncReceiptState()` 在 `syncGeometry()` 内写 `receiptDrawnAtMs=nowMs()`，而 `syncGeometry()` 会从 `update(snapshot/prep)`、`setVisibleBounds` 与绘制前调用——此时 GL **尚未真正绘制**回执。若权威准备结束快照与下一次真实渲染相隔 ≥150ms（窗口暂停、掉帧、后台恢复），淡出时钟在 update 时就已启动，用户在看到「方案已确认」之前回执就过期了 | 把「已绘制」误记在「准备画」的时机上 | ① 「首次绘制」改由 [ShufflePreparationPanel.markReceiptRendered] 在 **render 真正画出回执之后**记录；② 回执淡出起点 = `max(权威准备结束时刻, 回执首次真实绘制时刻)`，且**必须两者都满足**才启动——保证首个真实渲染帧 alpha=1；③ 面板时钟改为**可注入**（`clock` 参数），回执相关状态全部走注入时钟，便于确定性断言 |
| 2 | 需要回归覆盖「提交成功与 preparation=null 快照都在首次 render 之前到达、且等待 >150ms」 | — | 新增 2 项确定性回归（`RoundPreparationFlowTest`，31 项）：`a receipt that arrives before the first render stays fully opaque on the first real frame`、`a receipt drawn while the preparation is still active fades only after the authority ends` |

> 权威发牌**没有**因此被延迟：发牌时刻仍完全由权威决定；回执只是在其后按 DESIGN §7 约 150ms 淡出，
> 实测发牌后 66.53 ms（target）/ 71.31 ms（zero）内消失。

---

## 0.1 第 4 轮返修记录（Codex 第 3 轮复验缺陷）

| # | 缺陷 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | **占位文案横压左家席位铭牌**：`runtime-02-target-picker.png`（1280×720 抽屉展开）与 `runtime-06-small.png`（960×540）里「本局手牌、牌河与牌山将在准备完成后由权威生成」伸进左家 P3 铭牌（x≈177–222, y≈380） | `PreparationTableRenderer.drawTexts()` 对整句长文案只按 `safeArea` 整宽居中；抽屉展开时安全桌区只有 664 宽，整宽居中的 24 字长句必然压到左家铭牌上 | 新增 `centralTextRect(safeArea)`：横向避开左右两侧铭牌列；占位文案改为**两行短句**（「手牌、牌河与牌山」/「准备完成后由权威生成」）并只在中央文字区内居中；标题同步移到中央文字区。**不缩小触控目标、不遮席位、不改领域规则** |
| 2 | `runtime-03-confirmed.png` 取在发牌后 150ms 淡出中段，标题与方案字几乎不可读 | 冒烟在回执出现后**多等一帧**再截图，这期间权威已发牌、回执开始淡出 | ① 面板保证**至少存在一帧完全不透明的回执**：淡出起点取「回执首次绘制」与「权威准备结束」两者中较晚者（`receiptDrawnAtMs` / `receiptFadeStartMs`）；② 冒烟改为只在 `receiptAlpha >= 0.999` 时取帧，并用**截图像素回读**校验标题可读（亮绿色像素 ≥ 40），不可读就换下一帧重试 |

新增回归断言（`RoundPreparationLayoutTest`，3 项）：

- `placeholder text never overlaps the seat plates at 1280 with the drawer open`
- `placeholder text never overlaps the seat plates at 1280 with the panel only`
- `placeholder text stays inside the band and readable at 960 by 540`
  （同时断言：中央文字区不与任何铭牌相交、标题与两行说明的估算宽度都放得下、
  960×540 缩放 0.75 后占位标题/说明仍 ≥ 12 像素高）

真实像素复核（`03-drawer.png`，世界坐标）：

| 行 | 亮像素 x 范围 | 归属 |
| --- | --- | --- |
| y=364（占位说明第 1 行） | 288~390（另 14~48 为铭牌自身） | 全部落在中央文字区 238~442，**不再进入铭牌 26~222** |
| y=340（占位说明第 2 行） | 274~418（另 84~92 为铭牌自身） | 同上 |
| y=368 / 346（铭牌自身两行） | 14~72 / 14~88 | 只有铭牌自己的文字 |

---


## 0. 第 3 轮返修记录（Codex 第 2 轮复验缺陷）

| # | 缺陷 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | **迟到回执污染新局**：面板在 `model.onSubmissionAccepted()` 校验**之前**就写 `localReceiptAccepted=true` / `receiptSelectionSnapshot=旧方案` 并 `collapseToReceiptPage()`；模型虽拒绝旧 requestId，面板却把**新局**锁存成旧局「方案已确认」；失败分支又无条件 `restoreDraft()`，可把旧局 target/看破写进新局 | 面板副作用没有自己的有效性校验；`restoreDraft` 只查确认/提交态，不查 roundId | 新增 `ShufflePreparationModel.isCurrentSubmissionFor(requestId, roundId)`（同局 + 未被别的请求取代）与 `restoreDraft(snapshot, roundId)`（追加 roundId 校验）。`ShufflePreparationPanel.submit()` 的**所有**异步副作用（锁存回执、收回抽屉、回滚草稿、显示错误）都先过这道校验，跨局/过期回执**零副作用**直接丢弃 |
| 2 | **发牌后仍显示「等待其他玩家完成准备」**：回执在 `preparation=null` 后仍保持 900ms，前 750ms 完全不透明；且 `confirmedFeedbackText==null` 时回退成「等待」 | 回执寿命与文案都按「准备阶段内」设计，没区分权威是否还在准备 | 回执改为：权威准备阶段内常驻；**准备阶段结束（或回执在结束后才到达）时立刻启动 150ms 淡出**（DESIGN §7），并新增 `receiptFeedbackText`：只在权威仍处准备且确有等待时显示「等待其他玩家完成准备」，否则显示「本局准备已完成」。确认回执时抽屉改为**立即收起**（`snapDrawerClosed`），保证回执帧是稳定的已收起只读态 |
| 3 | `drawDrawerBorders()` 无条件画看破勾选框边框，房间禁用时仍有像可交互的框 | 边框绘制没有跟随「入口可见性」 | 抽出 `isSeeThroughEntryVisible`（= 房间是否启用看破），准备栏与抽屉的**底板、勾选框、边框**统一按它绘制：禁用时不画任何勾选装饰与边框，只留一行「看破未启用」说明 |

新增测试（第 3 轮，4 项）：

- `a late success receipt from the previous round leaves no trace in the new round`
  （真实 `CompletableFuture` 跨局迟到成功回执：断言 `panel.isReceiptVisible=false`、`model.page=PANEL`、
  `model.target=null`、`isSeeThroughSelected=false`、`errorMessage=null`、`confirmedSelection=null`）
- `a late rejected direct start from the previous round cannot roll a draft into the new round`
  （真实 `CompletableFuture` 跨局迟到失败回执：断言旧局草稿没有被回滚进新局，且新局不显示旧错误）
- `the see-through entry is hidden entirely when the room disables it`
- `the receipt never claims to wait once the authority ended the preparation`

冒烟新增断言：发牌后 `receiptFeedbackText` 不含「等待」；发牌后回执必须在 **400ms 内**消失（实测 ~67ms）。

---

## 0. 第 2 轮返修记录（Codex 独立验收缺陷）

Codex 目视 `runtime-*.png` 后指出 5 项缺陷，逐项定位与修复如下（截图已全部重跑并替换）：

| # | 缺陷 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | 成功回执态仍画着空倒计时框、空看破勾选框、空红色主按钮；「等待其他玩家完成准备」压在旧偷天换日按钮框内，「本局方案已锁定…」与其边框重叠 | `drawPanelBackground` / `drawPanelBorders` / `drawNinePatchButtons` 没有回执分支，而 `drawPanelContent` 在回执时早退 | 回执改为**独立只读层**：新增 `drawReceiptBackground/Border/Content`，绘制一张独立小回执卡（准备栏顶部 `312×200`），回执态**完全不绘制**倒计时框、看破勾选框、主按钮、偷天换日入口与整个抽屉；五行文案使用固定基线（世界 y 608/572/544/510/478），互不重叠 |
| 2 | 默认栏「点击后立即提交，不做二次确认」画在主按钮内部下沿，与「开始本局 · 0点」重叠 | 说明文字基线用 `panelRect.y+130f`，而主按钮范围是 `panelRect.y+114..178` | 说明/禁用理由基线改到 `panelRect.y+92f`（世界 y 140），完全落在主按钮下沿（162）之外；已用像素扫描确认（见 3.2） |
| 3 | `runtime-03-confirmed.png` 实际仍是整个六目标抽屉（可见「返回」「直接开局」），不是只读收起回执 | 抽屉动画只在收到权威快照时同步，而本地提交回执是异步到达的，权威可能先送来 `preparation=null` 快照，导致模型本地确认标记被清掉、抽屉一直留在屏上 | ① 抽屉与页面改为**每帧**同步（`syncMotionWithModel` 在 `syncGeometry` 内）；② 回执状态由面板自己锁存（`localReceiptAccepted` + `receiptSelectionSnapshot`），不再依赖模型标记；③ 提交成功时立即 `collapseToReceiptPage()`；④ 截图前强制等待 `drawerOffsetX == 160`（抽屉完全收起）且 `isReceiptVisible`，只采**稳定帧** |
| 4 | `activate(Control.DrawerSkip)` 在检查 `canSkip` / 构造请求**之前**就 `model.clearAllDraft()`：暂停、截止、提交中或已确认期间点该控件仍会清空草稿，即使请求被拒绝 | 顺序颠倒 | 改为**先校验再变更**：`canSkip` 与 `buildSkipRequest()` 成功后才清空草稿并提交；提交失败时用 `captureDraft()/restoreDraft()` 恢复原草稿并回到发起抽屉。新增 5 项边界测试（暂停 / 到期 / 在途 / 已确认 / 失败恢复） |
| 5 | 需要复跑专项、全量构建与两个真实 GL 冒烟并更新截图与文档 | — | 已全部执行，见下文第 2、3、5 节 |

附带修掉的两处相邻问题：

- 抽屉里的看破勾选在房间禁用时仍会被画成可交互勾选框 → 与准备栏一致，禁用时只显示「看破未启用」。
- 抽屉底部提示「先选择一个目标牌型」原本落在看破行内 → 移到看破行右侧（世界 x 976 / y 198），不再与任何控件重叠。
- 左家铭牌原本压在牌桌左上角既有的「返回主菜单」隐形热区上 → 改放左侧中部，避免点铭牌意外退桌。

---

## 1. 实际改动

### 1.1 新增文件

| 文件 | 作用 |
| --- | --- |
| `presentation/src/main/kotlin/com/majiang/presentation/game/RoundPreparationLayout.kt` | 纯几何布局：DESIGN §6 全部建议坐标 + 超宽/窄屏可见区推导 + 剩余安全桌区 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/RoundPreparationMotion.kt` | 局部滑入时序：准备栏 72px/220ms、抽屉 160px/280ms easeOutCubic、减少动画直达终态 |
| `presentation/src/main/kotlin/com/majiang/presentation/common/render/PreparationTableRenderer.kt` | 准备阶段桌面场景：L0 麻将馆环境 + L1 翡翠桌布/朱红木边/暖金描线 + 公开四席 + 局次，发牌前不画任何实体牌；**第 4 轮**：新增 `centralTextRect()` 中央文字区（横向避开两侧席位铭牌），占位文案改为两行短句并只在中央文字区内居中 |
| `presentation/src/test/kotlin/com/majiang/presentation/game/RoundPreparationFlowTest.kt` | 端到端交互流程测试（31 项：第 2 轮 +5 项「直接开局」边界用例，第 3 轮 +4 项跨局迟到回执 / 看破可见性 / 回执文案用例，第 5 轮 +2 项回执淡出时序确定性回归） |
| `presentation/src/test/kotlin/com/majiang/presentation/game/RoundPreparationLayoutTest.kt` | 布局契约测试（12 项：第 4 轮 +3 项占位文案与席位铭牌碰撞 / 960×540 可读性回归） |
| `presentation/src/test/kotlin/com/majiang/presentation/game/ShufflePreparationModelTest.kt` | 纯状态模型测试（18 项） |
| `tools/architecture/RoundPreparationRenderSmoke.java` | 真实 GL 冒烟：默认栏 / 抽屉中间帧 / 抽屉终态 / 40 点组合 / 960 / 超宽 / **已收起且完全不透明的可读回执** / 发牌后；并断言发牌后不再显示等待、回执按时消失 |
| `tools/architecture/round-preparation-smoke.init.gradle` | 冒烟任务：`roundPreparationSmoke`（目标确认）与 `roundPreparationSmokeZero`（零费开局） |
| `doc/ui/round-preparation/DELIVERY.md` | 本文件 |

### 1.2 修改文件

| 文件 | 实际改动 |
| --- | --- |
| `presentation/.../game/ShufflePreparationModel.kt` | 新增 `PreparationPage`（PANEL/DRAWER）与 `PreparationPrimaryAction`；主要动作费用动态文案；抽屉开合只编辑不提交；`buildPrimaryRequest` / `buildDrawerConfirmRequest`；失败恢复发起页（`submittingOriginPage`）；迟到回执守卫（请求标识 + 在途请求）；`onReconnect()` 复位草稿与页面；新局复位；**第 2 轮**：`captureDraft()` / `restoreDraft()`、`isPlanConfirmed`、`collapseToReceiptPage()`；**第 3 轮**：`isCurrentSubmissionFor(requestId, roundId)` 跨局回执校验、`restoreDraft` 追加 roundId 校验 |
| `presentation/.../game/ShufflePreparationPanel.kt` | 由「全屏六选一弹窗」重写为「右侧紧凑准备栏 + 按需六选一抽屉」；去掉全屏遮黑；原生绘制 + 九宫格按钮（复用 `assets/ui/main-menu` 既有底板）；真实绘制/命中矩形（`pressDown`/`pressUp`/`pressDragged`）；Esc/Enter/Space 键盘通路；**第 2 轮**：独立只读回执层、回执锁存与淡出、抽屉与页面每帧同步、「直接开局」先校验后清草稿、说明文字移出主按钮、抽屉看破禁用态、抽屉提示移位；**第 3 轮**：所有异步副作用先过跨局校验、回执 150ms 淡出 + `receiptFeedbackText`（发牌后不再宣称等待）、抽屉确认时立即收起（`snapDrawerClosed`）、`isSeeThroughEntryVisible` 统一控制看破装饰；**第 4 轮**：回执淡出起点改为「首次绘制」与「准备结束」的较晚者；**第 5 轮**：「首次绘制」只在 `render` 真正画出回执后记录（`markReceiptRendered`），淡出起点必须「已绘制」且「准备已结束」才启动；回执时钟改为可注入（`clock`） |
| `presentation/.../game/GameScreen.kt` | 准备阶段改渲染 `PreparationTableRenderer`（桌面始终可见，手牌/牌河/牌山留空）；准备栏/抽屉可见区随窗口自适应；触控 down/up/drag 与键盘全部接到新面板；准备阶段屏蔽牌局输入但保留「返回主菜单」；换同步代次/换会话按重连处理；**第 2 轮**：回执淡出期间仍绘制面板（`isVisible`），但模态守卫仍只认准备阶段 |
| `presentation/.../common/UiAssets.kt` | 显式声明桌边场景资源的生命周期归属：`obtainTableBackdrop()` 与 `obtainPreparationButtonPatches()` 复用已持有的主菜单纹理/九宫格，不新建纹理、不每帧加载 |
| `presentation/src/test/.../ShufflePreparationUiAcceptanceTest.kt` | 按新 UI 契约更新（默认进准备栏而不是进场六选一），**费用 / 失败 / 过期 / 模态守卫断言全部保留并强化** |

> `presentation/.../common/UiAssets.kt` 未新增纹理：准备栏与抽屉复用的九宫格按钮底板与麻将馆背景都来自
> `UiAssets` 已持有的 `MainMenuAssets`（延迟创建、统一释放），因此**没有每帧创建纹理**，也没有重复加载同一张 PNG。

### 1.3 关键行为对照

| 需求 | 实现位置 | 实际行为 |
| --- | --- | --- |
| 默认只显示紧凑准备栏 | `ShufflePreparationModel.page` 初始 `PANEL` | 进场不弹六选一；六张目标卡只在抽屉内可命中 |
| 一次点击零费开局 | `buildPrimaryRequest()` | `target=null, seeThrough=false`，费用 0，无二次确认 |
| 点「偷天换日」才展开 | `Control.OpenTarget` → `openTargetDrawer()` | 只切页面，不扣点、不提交、不预选牌型（默认不是清一色） |
| 六目标与现有枚举一致 | `ShuffleTarget.entries` | `CHINITSU_MAN/SOU/PIN, DAISANGEN, KOKUSHI, DAISUUSHII` |
| 规则短句 | `ShufflePreparationPanel.TARGET_RULE_HINT` | 「影响起手及后续摸牌，不保证成型」 |
| 看破独立勾选 | `btnSeeThroughRect` | 房间禁用时不渲染可交互勾选框，只显示「看破未启用（房间不支持）」 |
| 费用 0/10/30/40 | `primaryActionLabel` / `drawerConfirmLabel` | 动态随草稿变化；资金不足只禁用提交，不禁用编辑 |
| 抽屉返回 / Esc | `closeTargetDrawer()` | 清 target、保留看破、不提交 |
| 抽屉「直接开局 · 0点」 | `Control.DrawerSkip` | 清全部草稿并立刻提交 skip |
| 提交中阻止重复 | `isSubmitting` + `isLocked` | 重复点击不再发第二个请求 |
| 失败恢复原页 | `onSubmissionRejected` 恢复 `submittingOriginPage` | 抽屉提交失败仍停在抽屉，合法草稿保留并显示错误 |
| 成功回执 | `confirmedReceiptTitle = "方案已确认"` | 只读本人方案与预留费用 |
| 等待其他玩家 | `confirmedFeedbackText` | 「等待其他玩家完成准备」，不发明他人准备计数/私有选择 |
| 权威结束才解除准备态 | `status == null` 才 `isActive=false` | 本地受理不本地关闭遮罩 |
| 新局复位 / 旧回执不污染 | `update()` 的 `isNewRound` + 请求标识守卫 | 旧局回执不会确认新局 |
| 同局刷新 / 返回抽屉 / 暂停 / resize 不重置 deadline | `isAuthoritySampleChanged` 只在权威样本变化时更新 | 抽屉开关与窗口尺寸变化都不动 deadline |
| 重连 | `onReconnect()` + `epochChanged/sessionChanged` | 已确认显示锁定回执，未确认回到默认栏 |
| 动画 | `RoundPreparationMotion` | 准备栏 +72px/220ms、抽屉 +160px/280ms easeOutCubic；**命中矩形与绘制使用同一偏移**；`motionEnabled=false` 直接终态 |
| 输入 | `pressDown`/`pressUp`/`pressDragged` + 键盘 | 按下与松开位置一致才确认，拖出取消；Enter/Space 确认，Esc 从抽屉回栏 |
| 桌面可见 | 准备阶段渲染 `PreparationTableRenderer`，面板只做右侧局部压暗 | 不全屏遮黑；发牌前手牌/牌河/牌山留空 |

---

## 2. 真实命令与结果

> Gradle 全部串行执行，未并发构建。

### 2.1 专项测试

```bash
.\gradlew.bat :presentation:test --tests "com.majiang.presentation.game.RoundPreparation*" --tests "com.majiang.presentation.game.ShufflePreparation*" --console=plain
```

结果：`BUILD SUCCESSFUL`，**64 项 0 失败**（含既有 `ShufflePreparationUiAcceptanceTest` 13 项 + 新增 44 项 + 布局 8 项等）。

```bash
.\gradlew.bat :presentation:test --console=plain
```

结果：`BUILD SUCCESSFUL`（presentation 模块全量，无回归）。

### 2.2 全量构建

```bash
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
```

结果：`BUILD SUCCESSFUL`。详见第 5 节的实测数字。

### 2.3 真实 GL 冒烟

```bash
.\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmoke --console=plain
.\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmokeZero --console=plain
```

两次都走**真实生产链**：真实 `MahjongGame` → 主菜单「单机对局」→ 真实 `GameScreen` → 真实权威准备阶段
→ 真实 `GameSession.submitShufflePlan` → 真实权威发牌。截图全部由 `ScreenUtils.getFrameBufferPixmap` 从真实帧缓冲导出。

### 2.4 上一轮回归未被破坏

```bash
.\gradlew.bat -I tools/architecture/menu-click-preparation-smoke.init.gradle :lwjgl3:menuClickPreparationSmoke --console=plain
```

结果：**`PASS: menu slide displacement and real single-player early deal verified`**
（目标滑入位移 220/80/0px、背景漂移 0.00；真实单机点「不使用千术」→ `preparation=null`、遮罩消失、
`cheatPoints[0]=100`、`handTiles=14`，墙钟 **142~174 ms**）。
本轮新准备栏没有改变上一轮「准备齐立即发牌」的修复；该冒烟用旧的
`snapshot/menu-click-preparation-smoke/` 目录属于本轮 scope 外产物，已在验证后恢复为仓库原状（不纳入本轮改动）。

---

## 3. 运行截图

以下 PNG 全部是**真实运行截图**（不是设计概念图）：

| 文件 | 内容 | 来源 |
| --- | --- | --- |
| `runtime-01-entry.png` | 默认开局：麻将桌 + 公开四席 + 局次，右侧紧凑「本局准备」栏，无六选一；说明文字位于主按钮**下方** | `roundPreparationSmoke` |
| `runtime-02-target-picker.png` | 点「偷天换日」后展开的六选一抽屉（终态） | `roundPreparationSmoke` |
| `runtime-03-confirmed.png` | 确认后的**已收起只读回执**（「方案已确认」小卡；抽屉已完全收起，看不到「返回」「直接开局」；取帧时**完全不透明**） | `roundPreparationSmoke` |
| `runtime-04-drawer-mid.png` | 抽屉滑入**中间帧**（准备栏与抽屉交叠） | `roundPreparationSmoke` |
| `runtime-05-cost40.png` | 目标 + 看破 = 40 点组合（国士无双已选，金色勾选标识） | `roundPreparationSmoke` |
| `runtime-06-small.png` | 960×540 下的准备界面 | `roundPreparationSmoke` |
| `runtime-07-wide.png` | 1600×600 超宽下的准备界面（准备栏随可见区自动贴右缘） | `roundPreparationSmoke` |
| `runtime-08-dealt.png` | 权威发牌后的真实牌桌（回执已淡出，桌面干净） | `roundPreparationSmoke` |
| `runtime-09-zero-receipt.png` | 零费用开局的确认回执（权威准备阶段仍在进行时的只读回执） | `roundPreparationSmokeZero` |

原始输出（含 `smoke-results.txt`）在 `build/round-preparation/screenshots-target/` 与 `build/round-preparation/screenshots-zero/`。

### 3.1 冒烟内的像素断言（非人工目视）

| 断言 | 实测 |
| --- | --- |
| 桌面在准备阶段可见（不是全屏遮黑） | 桌区像素 `rgb=12,56,38`（翡翠桌布内圈），绿分量最高 |
| 右侧准备栏确实被绘制 | 面板像素 `rgb=12,44,39`（深翡翠底板） |
| 选中的目标卡确实被高亮 | 国士无双卡标记像素 `前 rgb=19,42,29`（卡底）→ `后 rgb=234,204,102`（暖金选中标识） |
| 六张目标卡数量 | `panel.getBtnTargets().size() == 6` |
| 默认不预选目标 | `model.getTarget() == null` |
| 回执帧是**已收起的只读回执** | 采样时 `page=PANEL`、`drawerOffsetX=160.0`（抽屉完全收起）、`receiptVisible=true` |
| 回执帧**可读**（不是淡出中段） | 取帧时 `alpha=1.0`，截图回读标题亮绿色像素 **297**（阈值 40）；不可读会自动换下一帧重试 |
| 回执在**首个真实渲染帧**仍完全不透明 | 专项确定性回归：提交成功与 `preparation=null` 快照都在首次 render 前到达、时钟推进 5000ms 后，`isReceiptVisible=true` 且 `alpha=1.0`；`markReceiptRendered()` 之后仍为 1.0，再过 190ms 才消失 |
| 占位文案不压席位铭牌 | `03-drawer.png`：占位两行亮像素 x 288~390 / 274~418，全部在中央文字区 238~442；左家铭牌列（26~222）只剩铭牌自身两行 |
| 抽屉在回执帧确实消失 | 抽屉「返回」按钮位置像素 `03-drawer rgb=20,59,50`（翡翠按钮）→ `07-receipt rgb=11,30,22`（桌面底色）；「直接开局」位置 `03-drawer rgb=13,45,39` → `07-receipt rgb=224,238,216`（真实牌桌） |
| 回执五行文案不重叠 | 文字行分别落在世界 y 608 / 572 / 544 / 510 / 478（基线间隔 26~36 单位） |
| 主按钮与说明文字不重叠 | 准备栏列文字行扫描：按钮标签在世界 y 192~198，按钮下沿 162；说明文字在世界 y 132~140，**在按钮之外** |
| 发牌后截图不含残留回执 | `08-dealt` 与 `03-confirmed` 像素不同（同点 `03-confirmed rgb=11,30,22`=回执卡底色 → `08-dealt rgb=20,56,36`=桌面绒面）；`08-dealt` 在回执区域扫描不到任何回执文字行 |
| 发牌后不再宣称等待 | 冒烟断言 `!panel.getReceiptFeedbackText().contains("等待")`（准备阶段结束后文案为「本局准备已完成」）✔ |
| 回执按时消失 | 发牌后回执消失耗时 **15.35 ms**（target）/ **67.36 ms**（zero），断言上限 400ms（DESIGN §7 约 150ms 淡出）✔ |

---

## 4. 真实耗时与费用

| 场景 | 指标 | 实测 |
| --- | --- | --- |
| 目标确认 → 权威发牌 | 墙钟 | **246.57 ms**（多次复跑 181~273 ms） |
| 零费开局 → 权威发牌 | 墙钟 | **272.66 ms**（多次复跑 197~273 ms） |
| 40 点组合后的出千点 | `cheatPoints[0]` | 100 → **60**（只扣一次，无二次扣减） |
| 零费开局后的出千点 | `cheatPoints[0]` | 100 → **100**（零费用） |
| 发牌后实体守恒 | `hands + wallRemaining + deadWall` | 53 + 69 + 14 = **136** ✔ |
| 上一轮提前发牌回归 | 跳过 → 牌桌可见 | **PASS**（`preparation=null`、`cheatPoints[0]=100`、`handTiles=14`）✔ |
| 发牌后回执消失 | 墙钟 | **66.53 ms**（target）/ **71.31 ms**（zero），断言上限 400ms ✔ |
| 回执首个真实渲染帧 | 不透明度 | **1.0**（`alpha=1.0`，标题亮绿像素 297，阈值 40）✔ |

两次都远小于 5 秒上限，说明**没有 10/15 秒固定等待、没有动画最短停留、没有第二次确认**；
单机电脑齐备后由权威立即发牌（继承上一轮 `ShufflePreparationHandler` / `ServerTableHost` 的提前发牌修复，本轮未改引擎）。
**回执的取帧与淡出都没有给权威发牌加任何等待**：发牌时刻完全由权威决定，
回执只是在其后按 DESIGN §7 约 150ms 淡出（实测 66~71 ms 内消失）；
即便权威快照与下一次真实渲染相隔很久（窗口暂停 / 掉帧 / 后台恢复），
回执也保证在**首个真实渲染帧**完全不透明可读，之后才启动淡出。

---

## 5. 全量构建结果

```bash
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
```

- 结果：**`BUILD SUCCESSFUL`**（Gradle 串行执行）。
- 第 5 轮返修后强制全量重跑：
  `.\gradlew.bat :engine:test :protocol:test :client:test :server:test :presentation:test --rerun-tasks --no-build-cache --console=plain`
  → **`BUILD SUCCESSFUL in 2m 50s`**；五个模块的 XML 报告合计 **1827 项，0 failures / 0 errors / 0 skipped**
  （比第 4 轮多 2 项：回执「首个真实渲染帧完全不透明」的确定性时序回归）。
- `:lwjgl3:classes`：成功。
- `:android:assembleDebug`：成功，产物 `android/build/outputs/apk/debug/android-debug.apk`。
  **这只代表 APK 构建通过，不等于真机/模拟器实测通过**（见第 6 节）。
- 备注 1：根 `test --rerun-tasks` 会在 `:android:javaPreCompileReleaseUnitTest` 阶段因本机 Gradle build-cache
  目录写权限失败（`AccessDeniedException`），与本轮代码无关；改为对五个 JVM 模块单独 `--rerun-tasks --no-build-cache` 后全绿。
  本机 Kotlin daemon 偶发连不上时会自动回退到进程内编译，结果不受影响。
- 备注 2：强制重跑时 `server` 模块的 `BeamSwapClientTcpIntegrationTest` 出现过一次
  `ACTION_IN_FLIGHT` 的时序性失败（偷梁换柱 TCP 集成用例，与本轮准备界面无关，
  本轮**未改动 server / engine / client / protocol 任何一行**）。单独重跑该用例与整个 server 模块均通过，
  随后全量重跑 **1825 项 0 失败**；判定为该既有用例的偶发时序抖动，已如实记录。

---

## 6. 未验证项 / 已知边界

1. **当前模型不支持图像输入**，因此本轮无法对设计概念图与运行截图做人工目视比对；
   布局、层级与文案全部以 `DESIGN.md` 的坐标与文字为准，视觉验证由像素断言与 Codex 独立目视检视承担。
2. **Android 真机 / 模拟器未实测**：只完成了 `:android:assembleDebug` 构建，未做真机触控、刘海安全区、
   低端设备帧率与纹理内存实测。**Android 构建通过 ≠ 真机通过。**
3. **跨物理网络未实测**：本轮只在单机（进程内权威）路径上跑通，联机多人准备阶段只由
   `client` / `server` / `engine` 既有测试覆盖，未做真实 TCP 房间的准备界面互验。
4. **「多人有未确认真人不得擅自开局」**由引擎 / 服务端契约测试覆盖
   （`engine/.../ShufflePreparationEarlyFinishTest.kt`、`server/.../PreparationEarlyDealContractTest.kt`，
   上一轮新增、本轮未改动），GL 冒烟只能在单机下运行，无法在真实会话里造出未确认真人。
5. **回执未显示其他玩家准备进度**（人数、谁已确认、谁选了看破）——按需求明确禁止发明未公开信息；
   「等待其他玩家完成准备」**只在权威仍处准备阶段时**出现，权威准备结束后回执按 §7 约 150ms 淡出并改为
   「本局准备已完成」，发牌后不再宣称等待。
6. **准备阶段背景与牌局阶段背景不是同一张**：准备阶段使用麻将馆底图 + 绘制桌布，
   权威发牌后回到既有牌局渲染（整屏翡翠色）。为避免影响既有像素断言与牌局观感，本轮没有改动牌局渲染路径。
7. `roundPreparationSmoke` 在切换窗口尺寸时需要一层重入保护（LWJGL3 的窗口模式切换会重入渲染回调），
   该保护只存在于冒烟脚本内，不影响产品代码。
8. 准备阶段左家铭牌放在**左侧中部**而不是左上角：牌桌左上角是既有的「返回主菜单」隐形热区，
   把信息铭牌压在热区上会让「点铭牌」意外退桌。准备阶段仍保留该热区作为退出出口。
9. 本轮未改 `engine` / `server` / `protocol` / `client` 任何一行代码：七模块边界、136 实体守恒、
   控制代次、requestId 幂等与上一轮的提前发牌修复全部原样继承。
10. **看破禁用态没有 GL 截图**：真实单机房间默认开启看破，本轮只补了单元测试
    （`the see-through entry is hidden entirely when the room disables it`）证明禁用时不画勾选框与边框；
    若要截图需要另建一个关闭 `seeThroughEnabled` 的房间配置（超出本轮 scope）。
