# 落汗图形展示开发任务 V1 交付文档 (DELIVERY-ANTI-VISUAL.md)

**任务编号**：V1
**开发角色**：Anti (Antigravity)
**验收角色**：Codex
**交付时间**：2026-10-12
**状态**：ready_for_review

---

## 1. 交付概览与架构边界

根据 `doc/tile-mark/TASK-ANTI-VISUAL.md` 与 `doc/tile-mark/VISUAL-DESIGN.md`，本次开发完整实现了日本立直麻将落汗千术的全新统一半透明图形展示体系、四方牌河提示交互面板与纯模型呈现投影。

- **模块约束**：严格遵守架构分层约束，改动限定于 `presentation/src/main` 与交付文档。未修改 `engine`、`protocol`、`server`、`client` 任何代码。
- **测试与用户文件约束**：未修改任何 `src/test`、`tools/architecture`、`Gradle` 配置文件，未触碰 `lwjgl3/build.gradle`、`SnapshotLauncher.kt` 及 `snapshot/`。未执行任何 `git commit/add/reset`，未调用外部代理。

---

## 2. 核心交付内容

### 2.1 冻结纯模型接口实现
在 `com.majiang.presentation.game` 包下实现了供 Codex 独立无头测试的冻结接口（防御复制只读集合）：
1. `HandDisplaySlot(val tile: TileView?)`：`null` 为暗手牌普通牌背，非 `null` 为本人已授权且有效的落汗牌面。
2. `OpponentHandRow(val seat: Int, slots: List<HandDisplaySlot>)`：提供只读 `slots`。
3. `OpponentHandPresentation.build(snapshot: GameSnapshotView?): List<OpponentHandRow>`：
   - 按 `viewerSeat` 后下家、对家、上家顺序固定返回三行。
   - 每行槽位数严格等于 `handTileCounts[seat]`。
   - 仅该座位于 `OPPONENT_HAND` 区域的有效标记可翻面；已知牌按 `typeIndex`、`tileId` 严格升序排列在一端，牌背无实体身份。
   - 重复或超出暗手牌数量的安全降级为牌背，不伪造额外暗手牌。
4. `RiverHintView(val wallRemaining: Int?, hands: List<OpponentHandRow>)`：提供只读 `hands`。
5. `RiverHintPresentation.build(snapshot: GameSnapshotView?): RiverHintView`：
   - 按当前局门风「东、南、西、北」固定返回四行。
   - 包含本人所在门风行（允许 `OWN_HAND` 区域标记翻面）。
   - `STACKING` 码牌准备阶段 `wallRemaining = null`，无旧暗手牌位置；合法阶段如实读取 `wallRemaining`（含 0）。

### 2.2 三家对手暗手牌图形渲染
- 在 `GameScreen` 中新增 `renderOpponentHands`：完整绘制三家暗手牌，未落汗牌显示标准牌背，落汗牌以 $\alpha = 0.55$ 半透明显示牌面，放置于手牌固定端，绝不泄露手牌真实物理排序，手牌总张数严格守恒。

### 2.3 统一牌面半透明视觉样式与汗滴图标移除
- 本人暗手牌、四家牌河弃牌、副露面子、宝牌指示牌、飞行打牌动画中，有效落汗牌统一使用 $\alpha = 0.55$ 半透明牌面。
- 在 `MeldDisplayRenderer` 中支持 `markedTileIds`，保持吃/碰/明杠/加杠/暗杠的横置、赤五、摸牌高亮完整语义的同时应用 $\alpha = 0.55$。
- 彻底移除汗滴角标与红点绘制（`drawTileMarkBadge`），牌面边缘使用淡青辉光线框保证对比度。

### 2.4 落汗千术面板精简与四格寿命点
- 移除旧版常驻文字追踪栏、文字位置说明与独立识别牌列。
- 保留落汗绝技选牌横幅（选择费用/数量/必要服务端拒绝原因）。
- 已生效标记组以 4 张紧凑小牌图标 + 4 格寿命点呈现：
  - 已消耗局：灰色空心圆圈。
  - 本局格：当局对局阶段为金色实心圆圈 + 外描边辉光；准备/码牌阶段显示为空心点。
  - 后续有效局：青绿色实心圆点。

### 2.5 牌河提示交互面板 (RiverHintPanel)
- 顶部展示“牌河提示”标题与权威 `wallRemaining` 余牌总数。
- 东南西北四行暗手牌行，仅落汗翻面。
- 下部提供四方牌河网格与滚轮浏览 (`scrolled`)，点击手牌落汗实体或牌河弃牌实体可进行跨面板与主牌桌的青蓝高亮联动定位。
- 实体焦点对齐权威生命周期：换局 (`roundId`)、换场 (`sessionId`) 或实体脱离可见区域时自动解焦；普通河牌在后续快照中持续保留焦点。
- 点击自闭环消费，完全防穿透；默认关闭，支持 Esc 键与切换按钮开关；进入选牌或结算模态时自动收起；不暂停游戏 deadline。

### 2.6 牌桌布局自适应重排与绘制状态恢复
- 面板展开时，本人手牌压缩间距 (`computeHandSlots`)，回合操作按钮上移 (`getActionButtonRect`)，东南西北座位信息平移避让，确保手牌、倒计时与响应操作无遮挡。
- `TileDrawBuffer.flush` 严格捕获并恢复调用方的 `batch.color`、`font.color`、`font.data.scaleX/scaleY`，杜绝半透明污染后续 UI 绘制。
- `ClassicSkin.appendTileBackDraw` 牌背统一使用不透明 $\alpha = 1.0$ 底色。

---

## 3. 修改与新增文件清单

### 新增文件
1. `presentation/src/main/kotlin/com/majiang/presentation/game/OpponentHandPresentation.kt`
2. `presentation/src/main/kotlin/com/majiang/presentation/game/RiverHintPresentation.kt`
3. `presentation/src/main/kotlin/com/majiang/presentation/game/RiverHintPanel.kt`

### 修改文件
1. `presentation/src/main/kotlin/com/majiang/presentation/common/render/TileSkin.kt`
2. `presentation/src/main/kotlin/com/majiang/presentation/common/render/TileDrawBuffer.kt`
3. `presentation/src/main/kotlin/com/majiang/presentation/common/render/MeldDisplayRenderer.kt`
4. `presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt`
5. `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt`

---

## 4. 测试与验证结果

执行全量构建与测试命令：
```powershell
$env:JAVA_HOME="C:\Program Files\Android\Android Studio\jbr"; .\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
```
- **退出码**：0 (BUILD SUCCESSFUL)
- **单元与契约测试**：全部测试通过（包括 `TileMarkVisualModelsContractTest` 31 项、`TileMarkAlphaContractTest` 8 项、`RiverHintPanelSelectionContractTest` 7 项、`TileMarkScreenContractTest` 等）。
- **桌面构建**：`:lwjgl3:classes` 通过。
- **Android APK 构建**：`:android:assembleDebug` 通过。
- **剩余问题**：无。
