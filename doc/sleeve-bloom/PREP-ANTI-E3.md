# 袖里藏花表现层 (SB-E3) 实施前准备与分析报告 (PREP-ANTI-E3.md)

> Codex 2026-10-12 实施期更正：本文件是准备历史，布局请以最新 TASK-ANTI-E3.md 和 FEEDBACK-E3-INITIAL.md 为准。旧 GameScreenTest 要求理牌高至少44，这是有效触控契约；Codex先前高36指令有误，已改理牌(1080,50,140,44)，上下仍留6/8像素。不要修改或削弱该测试，请Anti修生产布局并继续测试。

- **任务标识**：SB-E3-PREP
- **责任人**：Anti (agy)
- **日期**：2026-10-12
- **状态**：准备完毕，待 E2 验收通过后启动生产实现
- **模式**：只读分析，未修改任何生产/测试/现有文档代码，未运行 Gradle，未触发任何 Git 操作

---

## 1. 任务背景与边界约束

根据 `doc/sleeve-bloom/TASK-ANTI-E3.md`、`VISUAL-DESIGN.md`、`DESIGN.md` 以及 `AGENTS.md`：
- 当前 E2 阶段由独立 Anti 进程负责 protocol、server 与 client.api 生产接线并持有 Gradle 运行权；
- 本准备阶段（SB-E3-PREP）仅执行只读分析与详细方案制定，梳理表现层组件、输入分发、渲染、生命周期与组装根的全部代码落点；
- 梳理待启用的两项只读契约测试（`*.kt.pending`）与真实桌面 GL 冒烟测试（`SleeveBloomRenderSmoke.java`）；
- 识别潜在冲突点与测试夹具细节，为 Codex 及后续 E3 快速闭环提供确凿事实依据。

---

## 2. 核心架构与目标源码落点

表现层全部实现严格限定于 `presentation` 模块，仅依赖 `client.api`，不接触 `engine` 或 `protocol`。

```
presentation/src/main/kotlin/com/majiang/
  ├── presentation/game/
  │     ├── SleeveBloomChoiceModel.kt   [新增] 纯无头选牌与时钟模型
  │     ├── SleeveBloomPanel.kt         [新增] 真实 GL 渲染与几何包围盒组件
  │     └── GameScreen.kt               [修改] 挂入面板、输入分发、模态互斥、异步请求与销毁
  ├── presentation/menu/
  │     └── MenuScreen.kt               [修改] 秘籍弹窗文案扩展、建房配置补齐
  └── MahjongGame.kt                    [修改] 组合根 launchGame 默认启用袖里藏花配置

presentation/src/test/kotlin/com/majiang/screen/
  ├── SleeveBloomChoiceModelContractTest.kt  [Codex 下发后从 pending 重命名接入]
  └── SleeveBloomScreenContractTest.kt       [Codex 下发后从 pending 重命名接入]

tools/architecture/
  └── SleeveBloomRenderSmoke.java            [Codex 独立 GL 冒烟验证]
```

---

## 3. 详细设计与实现方案

### 3.1 `SleeveBloomChoiceModel.kt` (纯状态与请求构造模型)

- **包路径**：`com.majiang.presentation.game`
- **继承/依赖**：无 LibGDX 依赖，只依赖 `com.majiang.client.api.*`。
- **构造函数**：
  ```kotlin
  class SleeveBloomChoiceModel(
      val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
  )
  ```
- **核心属性**：
  - `val isChoosing: Boolean`：私有快照存在 `pendingOffer`、连接正常、非暂停、非托管时为 `true`。注意：**倒计时归零后仍为 `true`**（保持模态所有权，等待权威快照清理），但禁止确认与选择。
  - `val selected: SleeveBloomKeep?`：当前选中的候选卡（`FIRST` 或 `SECOND`），初始为 `null`。
  - `val canActivate: Boolean`：在无挂起 offer、权威 `sleeveBloom.canApply == true`、余额足够、真人控制、未暂停、未在途时为 `true`。
  - `val canConfirm: Boolean`：在 `isChoosing == true`、`selected != null`、`remainingMillis > 0`、未在途、未暂停、真人控制时为 `true`。
  - `val remainingMillis: Long`（动态计算 getter）：
    ```kotlin
    val remainingMillis: Long
        get() {
            val sample = sampledLocalMillis ?: return 0L
            if (currentSnapshot?.paused == true) return 0L
            val now = monotonicClock()
            val elapsed = maxOf(0L, now - sample)
            val estimatedServerNow = sampledServerNow + elapsed
            return maxOf(0L, cachedDeadline - estimatedServerNow)
        }
    ```
- **核心方法**：
  1. `update(snapshot: GameSnapshotView?, pending: Boolean = false, connected: Boolean = true, nowMillis: Long = monotonicClock())`：
     - 若 `snapshot == null` 或 `!connected`：调用 `cancel()`，清理时钟采样；
     - 若 `pending == true`：标记在途状态；
     - 检查身份代次与候选实体是否变更（`sessionId`、`syncEpoch`、`roundId`、`decisionId`、`controlEpoch`、`offerId`、`firstTile`、`secondTile` 任一变化）：
       若变更，立即清空选中 `selected = null`（不关闭有效 offer 的 `isChoosing`）；
     - 若处于暂停、托管（`TAKEOVER`）、断线或 offer 清除：重置 `isChoosing = false`，清空 `selected = null`；
     - 时钟采样：仅在快照代次变更或 `sampledLocalMillis == null` 时记录 `sampledLocalMillis = nowMillis`、`sampledServerNow = viewerClock.serverNow`、`cachedDeadline = offer.deadlineMillis`；同一快照重复刷新（如每帧渲染 tick）**绝不重置本地采样起点**。
  2. `select(keep: SleeveBloomKeep): Boolean`：
     - 校验 `if (!isChoosing || remainingMillis <= 0L || isPending) return false`；
     - 设置 `selected = keep` 并返回 `true`。
  3. `buildActivateRequest(requestId: String): SleeveBloomRequest?`：
     - 严格只读，不修改快照或模型状态；
     - 若 `!canActivate || isChoosing || isPending || !isConnected` 返回 `null`；
     - 返回 `SleeveBloomRequest(requestId, snap.sessionId, snap.roundId ?: "", bloom.decisionId, bloom.controlEpoch)`。
  4. `buildChoiceRequest(requestId: String, keep: SleeveBloomKeep): SleeveBloomChoiceRequest?`：
     - 若 `!isChoosing || isPending || !isConnected || remainingMillis <= 0L` 返回 `null`；
     - **取消/保留第一张语义**：`keep == SleeveBloomKeep.FIRST` 时允许 `selected == null`；
     - **确认/保留第二张语义**：`keep == SleeveBloomKeep.SECOND` 时必须 `selected == SleeveBloomKeep.SECOND`；
     - 返回 `SleeveBloomChoiceRequest(requestId, snap.sessionId, snap.roundId ?: "", snap.decision.decisionId, snap.decision.controlEpoch, offer.offerId, keep)`。
  5. `cancel()`：
     - `isChoosing = false`，`selected = null`。

---

### 3.2 `SleeveBloomPanel.kt` (表现层图形与几何包围盒)

- **包路径**：`com.majiang.presentation.game`
- **继承与接口**：实现 `com.badlogic.gdx.utils.Disposable`
- **公共几何常量与契约**（1280×720 UI 世界坐标系）：
  ```kotlin
  val btnOpenRect = Rectangle(1080f, 100f, 140f, 44f)      // 入口按钮
  val panelRect = Rectangle(390f, 210f, 500f, 300f)        // 二选一主模态面板
  val firstCardRect = Rectangle(460f, 290f, 90f, 126f)     // 第一候选牌卡 (已摸到)
  val secondCardRect = Rectangle(730f, 290f, 90f, 126f)    // 第二候选牌卡 (牌山次张/王牌替补)
  val btnConfirmRect = Rectangle(450f, 225f, 180f, 44f)    // 确认保留所选
  val btnCancelRect = Rectangle(650f, 225f, 180f, 44f)     // 取消/保留第一张
  ```
  *几何关系验证*：
  - `panelRect` 完整包含 `firstCardRect`、`secondCardRect`、`btnConfirmRect`、`btnCancelRect`；
  - `firstCardRect` (x=460..550, y=290..416) 与 `secondCardRect` (x=730..820, y=290..416) 水平互不重叠；
  - 两牌卡 (y=290..416) 与下方两按钮 (y=225..269) 垂直互不重叠；
  - `btnConfirmRect` (x=450..630) 与 `btnCancelRect` (x=650..830) 水平互不重叠。
- **渲染设计** (`render(...)`)：
  1. **入口按钮**（`!model.isChoosing` 时绘制）：
     - 可用时橙色高亮 `Color(0.85f, 0.45f, 0.15f, 1f)`，描边金色，文案「袖里藏花 · 10」；
     - 禁用时置灰 `Color(0.25f, 0.25f, 0.25f, 0.7f)`，文案淡灰。
  2. **二选一模态面板**（`model.isChoosing` 时绘制）：
     - 半透明深色底板 `Color(0.08f, 0.12f, 0.14f, 0.94f)`，外描金边；
     - 顶部居中标题「袖里藏花 · 二选一」（基线 y=490）；
     - 费用提示「已消耗 10 额度」（基线 y=455）；
     - 右上角权威倒计时（单位秒，最后 3 秒标红 `Color(0.95f, 0.35f, 0.35f, 1f)`）；
     - 两张候选牌使用 `TileSkin` / 纹理渲染牌面；
     - 牌面选中状态：选中的牌卡高亮青色描边 `Color(0.2f, 0.9f, 0.85f, 1f)`，未选无描边；
     - 来源标签（基线 y=274）：第一张标注「已摸到」；第二张依据 `drawKind == RINSHAN` 标注「王牌替补」，否则标注「牌山次张」；
     - 确认按钮「保留所选」：`canConfirm` 为真时橙色，未选时置灰禁用；
     - 取消按钮「保留第一张」：暗红色底板，无需选牌即可点击，在途期间禁用。
- **伴生对象**：
  1. `fun translateServerReason(reason: String): String`：
     涵盖全部新增原因码及复用通用原因码：
     - `SLEEVE_BLOOM_DISABLED` -> "袖里藏花未启用"
     - `SLEEVE_BLOOM_RIICHI_LOCKED` -> "立直后禁止施展"
     - `SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED` -> "庄家起手第 14 张不可施展"
     - `SLEEVE_BLOOM_NO_DRAWN_TILE` -> "当前无刚摸的牌"
     - `SLEEVE_BLOOM_ALREADY_PENDING` -> "二选一进行中"
     - `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW` -> "本次摸牌已施展过"
     - `SLEEVE_BLOOM_WALL_EMPTY` -> "牌山已摸完，无第二候选"
     - `SLEEVE_BLOOM_NO_PENDING_OFFER` -> "没有进行中的二选一"
     - `SLEEVE_BLOOM_OFFER_MISMATCH` -> "选择已过期"
     - `SLEEVE_BLOOM_STALE_OFFER` -> "局面已变化，选择失效"
     - `SLEEVE_BLOOM_CHOICE_PENDING` -> "请先完成袖里藏花二选一"
     - `INSUFFICIENT_FUNDS` -> "千术点数不足 (需10点)"
     - `DEADLINE_EXPIRED` -> "思考时间已超时"
     - `DECISION_MISMATCH` -> "决策已变更"
     - `NOT_YOUR_TURN` -> "非本人出牌回合"
     - `SEAT_NOT_HUMAN` -> "非真人玩家控制"
     - `ACTION_IN_FLIGHT` -> "有动作正在处理中"
     - `SESSION_CLOSED` -> "连接会话已关闭"
     - `STALE_CONTROL_EPOCH` -> "控制代次已过期"
     - `WRONG_PHASE` -> "当前阶段无法施术"
     - `NO_ACTIVE_DECISION` -> "当前无有效决策"
     - `HOST_PAUSED` -> "对局已暂停"
     - `REQUEST_ID_CONFLICT` -> "请求标识冲突"
  2. `val SLEEVE_BLOOM_CHARS: String`：
     集合包含所有 UI 文字、按钮文案、原因翻译以及秘籍说明中的中文字形全集（去重），保证字形覆盖率测试 100% 通过。

---

### 3.3 `GameScreen.kt` 深度接线

1. **字段与组件持有**：
   ```kotlin
   val sleeveBloomPanel = SleeveBloomPanel(session)
   private var isSubmittingBloom = false
   private var isSubmittingBloomChoice = false
   ```
2. **模态注册** (`isModalActive()`):
   ```kotlin
   fun isModalActive(): Boolean =
       showFinalResult || showSettlement || shufflePreparationPanel.isActive ||
       tileMarkPanel.model.isSelecting || riichiSelectionModel.isSelecting || meldSelectionModel.isSelecting ||
       sleeveBloomPanel.model.isChoosing
   ```
   二选一期间挂起手牌点击、出牌、落汗、立直及副露操作，牌河提示面板自动收起。
3. **输入拦截 (`touchDown`)**：
   - *模态最高优先级分支*：
     ```kotlin
     if (sleeveBloomPanel.model.isChoosing) {
         if (sleeveBloomPanel.btnConfirmRect.contains(uiTx, uiTy)) {
             sleeveBloomPanel.model.selected?.let { submitSleeveBloomChoiceAsync(it) }
             return true
         }
         if (sleeveBloomPanel.btnCancelRect.contains(uiTx, uiTy)) {
             submitSleeveBloomChoiceAsync(SleeveBloomKeep.FIRST)
             return true
         }
         if (sleeveBloomPanel.firstCardRect.contains(uiTx, uiTy)) {
             sleeveBloomPanel.model.select(SleeveBloomKeep.FIRST)
             return true
         }
         if (sleeveBloomPanel.secondCardRect.contains(uiTx, uiTy)) {
             sleeveBloomPanel.model.select(SleeveBloomKeep.SECOND)
             return true
         }
         // 吞掉面板外及面板空白处所有点击，防穿透
         return true
     }
     ```
   - *入口按钮点击分支*（**必须置于 `btnTakeoverRect` 之前**，详见第 4 节冲突分析）：
     ```kotlin
     val canOpenBloom = (currentSnapshot?.sleeveBloom?.canApply == true) &&
         !isDiscardPlaybackBusy && !isPaused && !session.isPending &&
         !isSubmitting && !isSubmittingMark && !isSubmittingBloom && !isSubmittingBloomChoice &&
         !riichiSelectionModel.isSelecting && !meldSelectionModel.isSelecting &&
         !tileMarkPanel.model.isSelecting && !sleeveBloomPanel.model.isChoosing

     if (canOpenBloom && sleeveBloomPanel.btnOpenRect.contains(tableTx, tableTy)) {
         submitSleeveBloomAsync()
         return true
     }
     ```
4. **按键拦截 (`keyDown`)**：
   ```kotlin
   if (sleeveBloomPanel.model.isChoosing) {
       submitSleeveBloomChoiceAsync(SleeveBloomKeep.FIRST)
       return true
   }
   ```
   按下 ESC 或 Android 返回键时，优先作为「保留第一张」提交二选一，**绝不退出游戏到菜单**。
5. **异步提交方法**：
   - `submitSleeveBloomAsync()`：校验 `canActivate`，UUID 构造 `SleeveBloomRequest`，`isSubmittingBloom` 防重入；成功不乐观弹窗，依赖权威快照驱动；失败或被拒通过 `showToast` 提示原因。
   - `submitSleeveBloomChoiceAsync(keep: SleeveBloomKeep)`：校验 `remainingMillis > 0`，UUID 构造 `SleeveBloomChoiceRequest`，`isSubmittingBloomChoice` 防重入；回执到达若失败通过 toast 提示。
6. **快照与每帧刷新**：
   在 `updateSnapshot()` 和 `render(delta)` 中同步驱动 `sleeveBloomPanel.update(...)`。
7. **销毁** (`dispose()`):
   调用 `sleeveBloomPanel.dispose()`，标记 `isDisposed = true`。

---

### 3.4 `MenuScreen.kt` 与 `MahjongGame.kt` 组合装配

1. **`MenuScreen.kt` 秘籍弹窗**：
   在 `renderSecretsModal` 中追加条目：
   ```
   5. 袖里藏花: 摸牌后出牌前消耗 10 额度，翻看第二候选牌（牌山次张或王牌替补）二选一；超时默认保留原摸牌并摸切，费用不退。
   ```
   同步微调排版间距，确保 6 条文本不与模态下边缘重合。
2. **`MahjongGame.launchGame` 默认配置**：
   ```kotlin
   val defaultCosts = mapOf(
       "TILE_MARK" to 10,
       "SLEEVE_BLOOM" to 10
   )
   lobby.createRoom(
       playerName = "玩家",
       config = MatchConfig(
           cheatEconomy = CheatEconomyConfig(
               enabled = true,
               initialPoints = List(4) { 100 },
               costs = defaultCosts
           ),
           shuffleConfig = ShuffleConfig(enabled = true),
           tileMarkConfig = TileMarkConfig(enabled = true),
           sleeveBloomConfig = SleeveBloomConfig(enabled = true)
       )
   )
   ```
   在 `MenuScreen.btnPlaySingle` 与 `btnCreateConfirm` 中保持同等配置更新。

---

## 4. 关键排查发现与潜在冲突提示 (针对 Codex)

### 4.1 几何与点击冲突：`btnTakeoverRect` 覆盖 `btnOpenRect`
- **事实证据**：
  在 `GameScreen.kt` 第 137 行：
  ```kotlin
  val btnTakeoverRect = Rectangle(1080f, 108f, 140f, 36f)
  ```
  其垂直范围为 `y = 108f .. 144f`。
  而根据视觉规范与契约测试，`sleeveBloomPanel.btnOpenRect` 位于：
  ```kotlin
  Rectangle(1080f, 100f, 140f, 44f)
  ```
  其垂直范围为 `y = 100f .. 144f`，水平范围同为 `x = 1080f .. 1220f`！
  两者的几何区域发生 80% 以上重叠。
- **触发场景**：
  在 `GameScreen.touchDown` 第 1927 行：
  ```kotlin
  if (gameStarted && !isLoading && !isMatchFinished && !showSettlement && btnTakeoverRect.contains(tableTx, tableTy)) { ... }
  ```
  该分支在普通按钮判定之前执行。当测试或玩家点击 `btnOpenRect` 中心点 `(1150f, 122f)` 时，将直接命中 `btnTakeoverRect`，导致触发 `session.claimControl() / releaseControl()`，而根本无法触发袖里藏花入口！
- **解决方案与实现决策**：
  1. 在 `touchDown` 中，将 `canOpenBloom && sleeveBloomPanel.btnOpenRect.contains(tableTx, tableTy)` 的检查前置到 `btnTakeoverRect` 之前；
  2. 审计发现 `btnTakeoverRect` 当前在表现层中并无可见渲染（历史遗留控制逻辑），将该热区下移至 `y = 50f` 避免物理污染。

### 4.2 倒计时计算：动态 Property 与无 update 刷新
- **事实证据**：
  在 `SleeveBloomChoiceModelContractTest.kt.pending` 第 54–58 行：
  ```kotlin
  time += 10_000
  model.update(received)
  assertEquals(13_000L, model.remainingMillis)
  time += 13_000
  assertEquals(0L, model.remainingMillis)
  ```
  在第 57 行将时间步进 13,000 毫秒后，**并未调用 `model.update(received)`**，而是直接断言 `model.remainingMillis == 0L`。
- **解决方案与实现决策**：
  `remainingMillis` 必须声明为带 getter 的属性，实时调用 `monotonicClock()` 结合上一次快照锁定的 `(sampledLocalMillis, sampledServerNow, cachedDeadline)` 进行外推计算，不能将其做成仅在 `update()` 中计算的静态值。

### 4.3 确认与取消按钮的请求构造条件差异
- **事实证据**：
  在 `SleeveBloomChoiceModelContractTest.kt.pending` 第 37 行：
  ```kotlin
  assertNotNull(model.buildChoiceRequest("cancel", SleeveBloomKeep.FIRST))
  ```
  此时 `model.selected` 仍为 `null`，但请求构造成功；而在未选择时点击确认按钮 `btnConfirmRect` 则必须被忽略且 `canConfirm == false`。
- **解决方案与实现决策**：
  - `buildChoiceRequest(id, keep)` 当 `keep == FIRST` 时，允许 `selected == null` 或 `selected == FIRST`；
  - 当 `keep == SECOND` 时，严格要求 `selected == SECOND`；
  - 确认按钮 `btnConfirmRect` 的点击处理绑定 `model.canConfirm` 与 `model.selected`，确保未选中牌卡时点击确认绝不向 Session 提交请求。

### 4.4 冒烟脚本与默认产品配置依赖
- **事实证据**：
  `tools/architecture/SleeveBloomRenderSmoke.java` 第 79–80 行：
  ```java
  var bloom = snapshot.getSleeveBloom();
  check(bloom != null, "Default product config must enable bloom like tile mark");
  ```
  以及第 88 行与 106 行：
  ```java
  check(offer.getDeadlineMillis() == originalDeadline, "Activation gifted time");
  ```
- **关键提醒**：
  `MahjongGame.launchGame` 必须在默认单机建房配置中启用 `SleeveBloomConfig(enabled = true)`，且千术账本费用必须显式配置 `"SLEEVE_BLOOM" to 10`。

---

## 5. 后续启动与验收路线图 (E2 验收后)

1. **测试激活**：
   将 `doc/sleeve-bloom/` 下的两个 `.kt.pending` 文件重命名/移动到 `presentation/src/test/kotlin/com/majiang/screen/`（去除 `.pending` 扩展名）。
2. **源码创建与修改**：
   - 创建 `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomChoiceModel.kt`；
   - 创建 `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt`；
   - 修改 `GameScreen.kt`（面板挂载、模态、输入拦截、提交方法与销毁）；
   - 修改 `MenuScreen.kt`（秘籍说明文本与建房配置）；
   - 修改 `MahjongGame.kt`（默认启动配置）。
3. **验证链路**：
   - `:presentation:test` 专项测试；
   - `./gradlew test --info` 全量测试；
   - `./gradlew :lwjgl3:classes :android:assembleDebug` 双端编译；
   - 执行 `SleeveBloomRenderSmoke.java` 真实 GL 冒烟并检查截图。
4. **交付物生成**：
   - `PROGRESS-ANTI-E3.md`
   - `DELIVERY-ANTI-E3.md`
   - `CALLBACK-ANTI-E3.json`
   - 标准输出 `CODEX_CALLBACK SB-E3 ready_for_review`。

---
*本准备文档仅作为技术方案与冲突排查报告，不作为 E3 实现授权或验收结果。*
