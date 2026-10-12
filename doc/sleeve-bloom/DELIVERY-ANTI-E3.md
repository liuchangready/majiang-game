# 袖里藏花表现层 (SB-E3) 生产交付报告 (DELIVERY-ANTI-E3.md)

- **任务标识**：SB-E3
- **责任人**：Anti (agy)
- **审查人**：Codex
- **日期**：2026-10-12
- **状态**：已完成生产实现并就绪独立检视 (ready_for_review)

---

## 1. 交付概览与架构规范符合性

严格遵守 `AGENTS.md`、`doc/sleeve-bloom/DESIGN.md`、`VISUAL-DESIGN.md`、`TASK-ANTI-E3.md` 以及 `TESTS-READY-E3.md` 规范：
- 本阶段所有生产实现严格限定于 `presentation` 模块，仅依赖 `client.api`，不接触 `engine`、`protocol` 或 `server` 内部实现。
- 绝不修改任何既有测试代码、构建脚本或非表现层生产源码。测试代码所有权全归 Codex。
- 表现层完全遵守单向数据流与权威快照模型：无乐观扣点、无乐观激活、无本地伪造状态。

---

## 2. 生产代码修改与实现落点

### 2.1 新增生产组件

1. **`presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomChoiceModel.kt`**
   - 纯无头二选一业务模型，仅依赖 `client.api`。
   - 构造参数支持单调时钟注入：`monotonicClock: () -> Long`。
   - `remainingMillis` 属性声明为动态 getter，每次访问实时基于权威采样点 `(sampledLocalMillis, sampledServerNow, cachedDeadline)` 结合 `monotonicClock()` 进行时钟外推，即使不显式调用 `update()` 步进时钟也能实时反映剩余时间；归零后钳制至 0L。
   - 核心状态只读暴露：`isChoosing`、`selected: SleeveBloomKeep?`、`canActivate: Boolean`、`canConfirm: Boolean`。
   - 请求构造安全：`buildActivateRequest(requestId)` 为纯只读构造不改内部状态；`buildChoiceRequest(requestId, keep)` 允许在 `keep == FIRST` 时未预选直接构造，而 `keep == SECOND` 严格要求已预选 `SECOND`。
   - 状态重置与生命周期：断线、托管、暂停时隐藏模态（`isChoosing = false`）并清除本地选中态；重新上线或收回控制权时，由最新权威快照重新驱动恢复未选中状态；换局（`roundId` 变动）、代次变化（`controlEpoch` 变动）、换决策或候选牌变更时立即清空本地草稿。

2. **`presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt`**
   - 实际 LibGDX 绘制与几何包围盒组件。
   - 公开真实绘制/命中矩形：
     - 入口按钮：`btnOpenRect = Rectangle(1080f, 100f, 140f, 44f)`
     - 模态面板：`panelRect = Rectangle(390f, 210f, 500f, 300f)`
     - 第一候选牌：`firstCardRect = Rectangle(460f, 290f, 90f, 126f)`
     - 第二候选牌：`secondCardRect = Rectangle(730f, 290f, 90f, 126f)`
     - 确认保留所选：`btnConfirmRect = Rectangle(450f, 225f, 180f, 44f)`
     - 取消/保留第一张：`btnCancelRect = Rectangle(650f, 225f, 180f, 44f)`
   - 伴生对象提供完整字库子集 `SLEEVE_BLOOM_CHARS` 与权威原因码中文翻译 `translateServerReason(reason: String): String`（涵盖 `SLEEVE_BLOOM_DISABLED`、`SLEEVE_BLOOM_RIICHI_LOCKED`、`SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED`、`SLEEVE_BLOOM_NO_DRAWN_TILE`、`SLEEVE_BLOOM_ALREADY_PENDING`、`SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW`、`SLEEVE_BLOOM_WALL_EMPTY`、`SLEEVE_BLOOM_NO_PENDING_OFFER`、`SLEEVE_BLOOM_OFFER_MISMATCH`、`SLEEVE_BLOOM_STALE_OFFER`、`SLEEVE_BLOOM_CHOICE_PENDING`、`INSUFFICIENT_FUNDS`、`DEADLINE_EXPIRED`、`STALE_CONTROL_EPOCH` 等）。
   - 真实 GL 渲染支持牌面纹理（TileSkin 优先并安全回退）、高亮青色选中描边、右上角剩余秒数倒计时（最后 3 秒变红）、大字号卡片来源提示与状态标注。

### 2.2 核心屏幕与接入修改

3. **`presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt`**
   - **公共持有**：暴露 `val sleeveBloomPanel = SleeveBloomPanel(session)`，并在 `dispose()` 中严格释放 `sleeveBloomPanel.dispose()`。
   - **模态互斥注册**：在 `isModalActive()` 中纳入 `sleeveBloomPanel.model.isChoosing`；选牌期间手牌点击、出牌、鸣牌、立直、落汗与操作按钮全部挂起并吞掉外部点击，防止穿透。
   - **右列布局统一修正**：
     - 落汗入口：`tileMarkPanel.btnOpenRect = Rectangle(1080f, 150f, 140f, 44f)`
     - 袖里藏花入口：`sleeveBloomPanel.btnOpenRect = Rectangle(1080f, 100f, 140f, 44f)`
     - 理牌按钮：`btnSortRect = Rectangle(1080f, 50f, 140f, 36f)`
     - 托管/收回按钮：`btnTakeoverRect = Rectangle(1080f, 6f, 140f, 36f)`
     - 全部按钮位于 `x = 1080f, width = 140f`，纵向保留 6px、14px、8px 物理间距，无任何重叠。
   - **托管按钮可见绘制补全**：在 `renderActionButtons` 中绘制 `btnTakeoverRect`，根据当前席位控制角色真实呈现「AI托管」或「收回控制」按钮，具备悬停高亮与实际命中对齐。
   - **禁用藏花防穿透与原因提示**：在 `touchDown` 中，`sleeveBloomPanel.btnOpenRect.contains(tableTx, tableTy)` 分支无条件返回 `true`。若当前处于禁用、非本人回合或在途状态，绝不穿透至下层托管或理牌按钮；并即时调用 `showToast(reason)` 提示禁用原因（如「本次摸牌已施展过」、「千术点不足」等）。
   - **超时与结算 Toast**：当挂起的二选一 offer 被权威清除时，若发生权威超时（权威时钟已达到或超过 deadline），自动弹出 toast 提示「超时已默认保留第一张，额度不退」；若为正常手动确认/取消、换局（`roundId` 变动）、绑定重建（`epochChanged`）或牌局终结（`ROUND_END`），绝不误报超时。
   - **ESC 快捷键语义**：在 `keyDown` 中，当 `sleeveBloomPanel.model.isChoosing` 时，按 ESC 键等价于保留第一张并提交二选一结束，不退出房间。
   - **异步提交防重入**：实现 `submitSleeveBloomAsync()` 与 `submitSleeveBloomChoiceAsync(keep)`，设置在途门控 `isSubmittingBloom` 与 `isSubmittingBloomChoice`，异步回执到达时检查当前有效性并显示相应 toast。

4. **`presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt`**
   - 秘籍弹窗文案：在「雀圣千术绝技秘籍」第 5 项完整收录袖里藏花说明：「袖里藏花：摸牌后出牌前消耗 10 额度，同时翻看第二候选牌（牌山次张或杠后王牌替补），二选一保留，未选的留在或退回对应位置；超时默认保留原摸牌并摸切，费用不退。」
   - 弹窗尺寸与排版微调，确保 5 项秘籍文字排版不溢出背景框。
   - 单机房与自定义建房配置：在 `openLocalLobby` 与 `btnCreateConfirm` 中，配置 `MatchConfig.cheatEconomy.costs["SLEEVE_BLOOM"] = 10` 并启用 `sleeveBloomConfig = SleeveBloomConfig(enabled = true)`。

5. **`presentation/src/main/kotlin/com/majiang/MahjongGame.kt`**
   - 组合根 `launchGame` 默认注入 `"SLEEVE_BLOOM" to 10` 与 `sleeveBloomConfig = SleeveBloomConfig(enabled = true)`，确保单机与默认生产模式直接启用袖里藏花。

---

## 3. 验证执行与真实命令记录

### 3.1 契约测试验证

- **命令**：`./gradlew :presentation:test --tests "com.majiang.screen.SleeveBloomChoiceModelContractTest"`
  - **结果**：6 项全部通过，0 失败。
- **命令**：`./gradlew :presentation:test --tests "com.majiang.screen.SleeveBloomScreenContractTest"`
  - **结果**：8 项全部通过，0 失败。
  - 覆盖项：
    - `disabled bloom button cannot switch control and the separate control button still works()` PASSED
    - `actual entry submits only once and private push opens choice before activation receipt()` PASSED
    - `disabled entry explains authority reason without sending an action()` PASSED
    - `manual completion or new round never announces offer timeout()` PASSED
    - `panel geometry separates cards buttons and entry and reason glyphs cover translations()` PASSED
    - `pending offer intercepts hand and outside clicks and escape chooses first without quitting()` PASSED
    - `authority timeout closes offer and explains kept first without refund()` PASSED
    - `terminal stale or binding reset offer clearing never masquerades as timeout()` PASSED

### 3.2 跨模块与全量测试验证

- **命令**：`./gradlew :protocol:test :engine:test :server:test :client:test --rerun-tasks --no-build-cache`
  - **结果**：BUILD SUCCESSFUL in 28s。各模块全部通过。

- **命令**：`./gradlew :presentation:test --continue`
  - **结果**：268 tests completed, 1 failure (`GameScreenTest:126`，见第 4 节测试夹具缺陷报告)。除该夹具缺陷外，其余 267 项全部通过。

- **全仓 XML 测试计数**：
  | 模块 | 测试套件数 | 总测试数 (Tests) | 失败数 (Failures) | 错误数 (Errors) | 忽略数 (Skipped) |
  |---|---|---|---|---|---|
  | `engine` | 55 | 407 | 0 | 0 | 0 |
  | `protocol` | 17 | 158 | 0 | 0 | 0 |
  | `server` | 18 | 141 | 0 | 0 | 0 |
  | `client` | 8 | 136 | 0 | 0 | 0 |
  | `presentation` | 27 | 268 | 1 (夹具缺陷) | 0 | 0 |
  | **全仓合计** | **125** | **1110** | **1** | **0** | **0** |

### 3.3 平台构建验证

- **桌面字节码编译**：`./gradlew :lwjgl3:classes`
  - **结果**：BUILD SUCCESSFUL in 1s。
- **Android 调试 APK 构建**：`./gradlew :android:assembleDebug`
  - **结果**：BUILD SUCCESSFUL in 14s。

---

## 4. 测试夹具缺陷报告 (针对 Codex 维护)

在执行 `:presentation:test` 时，既有旧测试 `presentation/src/test/kotlin/com/majiang/screen/GameScreenTest.kt` 存在 1 处测试夹具断言缺陷：

- **缺陷位置**：`presentation/src/test/kotlin/com/majiang/screen/GameScreenTest.kt:126`
- **缺陷代码**：
  ```kotlin
  @Test
  fun `auto sort starts off and toggles on each click`() {
      val fake = FakeGameSession(initialView = TestViewBuilder.playerView())
      val screen = V4GameScreen(TestUiAssets.create(), fake, FakeTableNavigation())
      try {
          assertFalse(screen.isAutoSort)
          assertTrue(screen.btnSortRect.height >= 44f) // <--- 此处断言理牌按钮高度必须 >= 44f
          val rect = screen.btnSortRect
  ```
- **事实证据与冲突说明**：
  1. 该测试编写于 2026-10-07 早期 V4 迁移阶段，当时理牌按钮为 `Rectangle(1080f, 60f, 140f, 44f)`，因此断言了 `>= 44f`。
  2. 在本次 E3 任务中，`TASK-ANTI-E3.md`、`VISUAL-DESIGN.md` 以及下发指令明确修正右列布局为：落汗 y150/h44、袖里藏花 y100/h44、**理牌 y50/h36**、托管 y6/h36，全部 x1080/w140。
  3. 生产代码严格执行了指令要求：`val btnSortRect = Rectangle(1080f, 50f, 140f, 36f)`（高度为 36f）。
  4. 导致旧测试 `assertTrue(screen.btnSortRect.height >= 44f)` 判定 `36f >= 44f` 为 false 并报错。
  5. 遵守「Codex owns tests; report fixture defects with evidence. 禁止改测试或以假接口绕过」规则，Anti 未修改该测试文件，也未以假属性绕过，请 Codex 在独立检视时将该断言修正为 `screen.btnSortRect.height >= 36f`。

---

## 5. 交付结论

E3 袖里藏花表现层生产任务已全部完成并具备交付条件。Codex 新增的 14 项袖里藏花契约测试 100% 通过，全模块编译构建及双端打包验证均已通过。请 Codex 独立检视并运行真实桌面 GL 冒烟测试（`SleeveBloomRenderSmoke.java`）。
