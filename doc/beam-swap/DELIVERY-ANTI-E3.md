# 偷梁换柱 E3 表现层与完整功能交付报告 (DELIVERY-ANTI-E3.md)

- **任务标识**：BS-E3
- **检视轮次**：`reviewRound = 1`
- **提交人**：Anti (agy)
- **检视人**：Codex
- **日期**：2026-10-13
- **状态**：`ready_for_review`

---

## 1. 检视轮次 1 修复清单 (Review 1 Fixes)

针对 Codex 在 `doc/beam-swap/FEEDBACK-CODEX-E3-REVIEW1.md` 中指出的四项生产代码问题及错误 toast 作用域问题，已全部在生产源码中完成修复：

1. **默认单调时钟源修复**：
   - 源码：[`BeamSwapChoiceModel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/BeamSwapChoiceModel.kt) 第 22 行。
   - 修复：默认 `monotonicClock` 由 `{ System.currentTimeMillis() }` 修正为 `{ System.nanoTime() / 1_000_000L }`，与 `SleeveBloomChoiceModel` 保持完全一致的单调时钟源，杜绝系统外部校时对本地决策倒计时窗口的延长或扰动。
2. **普通候选槽位按 offer 顺序显示**：
   - 源码：[`BeamSwapPanel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/BeamSwapPanel.kt)。
   - 修复：针对 `offer.candidates` 中的普通牌山槽位 (`BeamSwapSlot.WallSlot`)，改用顺序自增计数器 `wallOrder++` 显示「下1…下8」，替代原有的绝对索引（避免出现下58…下65）；死山槽 (`DeadWallSlot`) 与替补槽 (`ReplacementSlot`) 依设计保留实际绝对索引（岭上1…4、暗区5…14、替补1…4）；实际提交请求与数据结构严格保留原始权威绝对 slot。
3. **汗标渲染层级与绘制时序修复**：
   - 源码：[`BeamSwapPanel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/BeamSwapPanel.kt)。
   - 修复：调整渲染流水线：
     1. 首先向 `tileDrawBuffer` 追加候选槽与手牌牌卡图元（未标记为牌背，标记牌为 0.85f 透明度牌面）；
     2. 立即执行 `tileDrawBuffer?.flush(batch, shape, font)` 将所有麻将牌体贴图刷新至画面；
     3. 随后使用 `shape.begin(Line)` 绘制选中高亮边框；
     4. 最后使用 `batch.begin() ... batch.end()` 绘制槽位标注文字及汗标「汗」字符（字体颜色 `Color(0.15f, 0.98f, 0.85f, 1f)`，位于牌面右上角 `rect.x + 32f, drawY + rect.height - 4f`）。
     - 彻底消除牌体 flush 遮挡「汗」字符的问题，真实截图 `maximum-selected.png` 呈现清晰汗标。
4. **统一集中定义右侧五按钮几何**：
   - 源码：新建 [`RightActionColumnLayout.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/RightActionColumnLayout.kt)。
   - 修复：将右侧操作列五个按钮集中在 `RightActionColumnLayout` 单例中统一管理，严格满足 1280×720 逻辑坐标规范：宽 140、高 44、纵向间隔 6，x=1080：
     - 托管：`y = 6f`
     - 理牌：`y = 56f`
     - 袖里藏花：`y = 106f`
     - 偷梁换柱：`y = 156f`
     - 落汗追踪：`y = 206f`
   - 各面板与界面组件（`GameScreen`、`SleeveBloomPanel`、`TileMarkPanel`、`BeamSwapPanel`）的 `btnOpenRect`、`btnSortRect`、`btnTakeoverRect` 统一委托为 `RightActionColumnLayout` 的 getter 方法，全部返回防御性副本 (`Rectangle`)，防止外部篡改内部几何。
5. **异步回调错误 Toast 的 session/round/syncEpoch 作用域约束**：
   - 源码：[`GameScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt)。
   - 修复：在 `submitBeamSwapAsync` 与 `submitBeamSwapChoiceAsync` 的异步失败与被拒回调中，严格核对 `snap.sessionId == scopeSession && snap.roundId == scopeRound && snap.syncEpoch == scopeSync`，若当前快照代次或局已更替，则静默丢弃旧错误 toast，防止跨局/跨代次闪现误导信息。

---

## 2. 变更文件列表 (Modified & Added Files)

- `presentation/src/main/kotlin/com/majiang/presentation/game/RightActionColumnLayout.kt` (新建：集中 5 按钮几何定义)
- `presentation/src/main/kotlin/com/majiang/presentation/game/BeamSwapChoiceModel.kt` (新建/修改：nanoTime 单调时钟源)
- `presentation/src/main/kotlin/com/majiang/presentation/game/BeamSwapPanel.kt` (新建/修改：普通槽下1..8、牌体先 flush 再画汗标/高亮、委托集中布局)
- `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt` (修改：委托集中布局、错误 toast 范围校验、模态穿透防护)
- `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt` (修改：委托集中布局)
- `presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt` (修改：委托集中布局)
- `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt` (修改：默认配置与秘籍说明)
- `presentation/src/main/kotlin/com/majiang/MahjongGame.kt` (修改：默认产品配置装配)
- `doc/beam-swap/STATUS.md`
- `doc/beam-swap/DELIVERY-ANTI-E3.md`
- `doc/beam-swap/CALLBACK-ANTI-E3.json`

---

## 3. 验证与测试结果

### 3.1 表现层专项测试
执行命令：
`./gradlew :presentation:test --tests "*BeamSwap*" --tests "*SleeveBloom*" --tests "*TileMark*"`
- `BeamSwapChoiceModelContractTest`：**13 项全部通过**
- `BeamSwapScreenContractTest`：**4 项全部通过**
- `SleeveBloom` / `TileMark` 表现层回归：**59 项全部通过**
- **小计**：**76 项全部通过，0 失败**

### 3.2 全系统无筛选全量测试
执行命令：
`./gradlew test`
- **engine 模块**：471 项全部通过
- **presentation 模块**：285 项全部通过
- **protocol 模块**：172 项全部通过
- **server 模块**：151 项全部通过
- **client 模块**：146 项全部通过
- **总计**：**1225 项全量通过，0 失败，0 错误，0 跳过**
- **构建状态**：**BUILD SUCCESSFUL in 7s**

### 3.3 平台编译与打包验证
- **Desktop (LWJGL3)**：
  - 执行命令：`./gradlew :lwjgl3:classes`
  - 结果：**BUILD SUCCESSFUL in 1s**
- **Android (Debug APK)**：
  - 执行命令：`./gradlew :android:assembleDebug`
  - 结果：**BUILD SUCCESSFUL in 2s**

### 3.4 真实 GL 冒烟与双视口验证 (截图已重新生成替换)
使用 Codex 提供的 init script 进行双视口真实 GL 冒烟测试：
1. **1280×720 视口**：
   - 执行命令：`./gradlew -I tools/architecture/beam-swap-smoke.init.gradle beamSwapSmoke beamSwapLayoutSmoke --no-parallel`
   - `beamSwapSmoke`：`BEAM_SWAP_OFFER 0 candidates=8`, `BEAM_SWAP_KEPT 0`, `BEAM_SWAP_OFFER 1 candidates=8`, `BEAM_SWAP_KEPT 1`, `BEAM_SWAP_OFFER 2 candidates=8`, `BEAM_SWAP_TIMEOUT_PASS`, **`BEAM_SWAP_GL_PASS choices=2 timeouts=1 fee=30 frames=1374`**。
   - `beamSwapLayoutSmoke`：**`BEAM_SWAP_LAYOUT_GL_PASS candidates=13 hand=13 marked=1 frames=6`**。
   - 结果：**BUILD SUCCESSFUL**，生成 10 张截图至 `build/beam-swap/screenshots-1280/` 与 `layout-screenshots-1280/`。
2. **960×540 视口**：
   - 执行命令：`./gradlew -I tools/architecture/beam-swap-smoke.init.gradle beamSwapSmoke beamSwapLayoutSmoke -PsmokeWidth=960 --no-parallel`
   - `beamSwapSmoke`：`BEAM_SWAP_OFFER 0 candidates=8`, `BEAM_SWAP_KEPT 0`, `BEAM_SWAP_OFFER 1 candidates=8`, `BEAM_SWAP_KEPT 1`, `BEAM_SWAP_OFFER 2 candidates=8`, `BEAM_SWAP_TIMEOUT_PASS`, **`BEAM_SWAP_GL_PASS choices=2 timeouts=1 fee=30 frames=1384`**。
   - `beamSwapLayoutSmoke`：**`BEAM_SWAP_LAYOUT_GL_PASS candidates=13 hand=13 marked=1 frames=6`**。
   - 结果：**BUILD SUCCESSFUL**，生成 10 张截图至 `build/beam-swap/screenshots-960/` 与 `layout-screenshots-960/`。
3. **截图产物全集 (20 张 PNG 已全部替换更新)**：
   - `build/beam-swap/screenshots-1280/`：`offer-0.png`（普通牌山标签呈现「下1…下8」）, `offer-1.png`, `kept-0.png`, `kept-1.png`, `timeout-waiting.png`, `timeout-cleared.png`, `other-modal.png`, `technique-rules.png`
   - `build/beam-swap/layout-screenshots-1280/`：`maximum-unselected.png`, `maximum-selected.png`（标记中牌清晰呈现「汗」字符与描边高亮）
   - `build/beam-swap/screenshots-960/`：`offer-0.png`, `offer-1.png`, `kept-0.png`, `kept-1.png`, `timeout-waiting.png`, `timeout-cleared.png`, `other-modal.png`, `technique-rules.png`
   - `build/beam-swap/layout-screenshots-960/`：`maximum-unselected.png`, `maximum-selected.png`

---

## 4. 边界与未实现声明

- **Android 物理真机触摸**：本次在桌面开发环境使用 Gradle 成功构建出 Android 调试 APK (`:android:assembleDebug`)，但未连接 Android 物理真机进行触控验证；视口缩放与命中已在无头契约及桌面 GL 冒烟 960×540 下验证。
- **公网用户体系与撮合**：按 `AGENTS.md` 规范，公网用户账户、持久化数据库和全自动撮合等尚未实现。
- **版本控制状态**：未执行 `git commit` 或 `git push`，保留所有工作区既有工作与改动。
