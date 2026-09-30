# Task D 交付文档：已完成副露在本人牌区持续展示

- **角色**：Antigravity（生产代码开发）
- **协同与验收**：Codex（架构设计、独立验收测试、Gradle构建与检视）
- **基线状态**：273 项测试全量通过，双端（LWJGL3 / Android）构建规范保全。

---

## 1. 交付目标与职责边界

本次交付为本人牌桌区域增加已提交公开副露（CHI / PON / OPEN KAN / CLOSED KAN / ADDED KAN）的持久展示能力。

### 1.1 独占所有权文件
1. `core/src/main/kotlin/com/majiang/render/MeldDisplayLayout.kt`（新增，纯计算无图形上下文依赖）
2. `core/src/main/kotlin/com/majiang/render/MeldDisplayRenderer.kt`（新增，TileDrawBuffer 指令生产者）
3. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`（最小接入：`displayedMelds()` 与渲染缓冲追加）
4. `doc/meld-display/delivery-D.md`（本文档）

### 1.2 严格遵守的限制
- 未修改任何测试文件、Gradle 配置文件、TileDrawBuffer、TileSkin、领域状态机或选牌状态模型。
- 未使用任何外部 shell、终端、MCP 工具或子 agent。
- 零额外依赖与零新贴图资源，完全复用现有 TileSkin 与 BitmapFont。

---

## 2. 固定接口与数据模型契约

### 2.1 数据模型与布局计算器 (`MeldDisplayLayout.kt`)

```kotlin
data class MeldDisplayTile(
    val tile: Tile,
    val x: Float,
    val y: Float,
    val width: Float,
    val height: Float,
    val faceDown: Boolean,
    val called: Boolean,
    val added: Boolean
)

data class MeldDisplayGroup(
    val type: MeldType,
    val fromPlayer: Int?,
    val tiles: List<MeldDisplayTile>,
    val x: Float,
    val width: Float
)

object MeldDisplayLayout {
    fun layout(view: PlayerView): List<MeldDisplayGroup>
}
```

- **纯无状态函数**：零 libGDX 引用，支持在任何脱机环境（含纯 JVM 单测）执行。
- **深拷贝只读隔离**：严格按 `view.viewerSeat` 读取公开副露 `view.melds[viewerSeat]`，对外返回 `Collections.unmodifiableList`，绝不泄露内部集合引用，绝不修改视图，不读其他玩家暗牌。
- **物理牌保全**：保留组内每个 `Tile` 的精确物理身份（含 `copyIndex`），不漏牌、不伪造牌、不复制牌。

### 2.2 渲染指令追加器 (`MeldDisplayRenderer.kt`)

```kotlin
object MeldDisplayRenderer {
    fun append(buffer: TileDrawBuffer, skin: TileSkin, groups: List<MeldDisplayGroup>)
}
```

- **指令生产者模式**：仅调用 `skin.appendTileDraw`、`skin.appendTileBackDraw`、`buffer.addLineRect` 与 `buffer.addText` 向现存 `TileDrawBuffer` 追加图元，不直接执行任何 OpenGL 调用，不调用 `begin()` / `end()`。
- **双皮肤兼容**：天然无缝兼容 `WikipediaSkin` 与 `ClassicSkin`。

### 2.3 场景接入 (`GameScreen.kt`)

- 公开只读查询入口：
  ```kotlin
  fun displayedMelds(): List<MeldDisplayGroup> = MeldDisplayLayout.layout(currentView)
  ```
- 牌桌渲染流程（与手牌紧邻追加，合并 flush）：
  ```kotlin
  // 3. 收集观察者手牌与已完成副露展示
  renderPlayerHands(batch, font)
  val melds = displayedMelds()
  if (melds.isNotEmpty()) {
      MeldDisplayRenderer.append(tileDrawBuffer, tileSkin, melds)
  }
  ```

---

## 3. 几何布局与视觉呈现规范

### 3.1 坐标与尺寸常量
- **整体右对齐基准**：`x = 1048f`
- **单牌尺寸**：`width = 36f`, `height = 52f`
- **组内单牌横向步长**：`step = 38f`（留 2f 间隙）
- **副露组间距**：`gap = 12f`
- **牌面基准高度**：所有牌 `y = 40f`（牌面覆盖区间 `y ∈ [40, 92]`）
- **标签安全区间**：最高 `y = 110f <= 114f`，最低 `y = 27f >= 25f`
  - 向上避开 `y = 145f..189f` 的操作按钮；
  - 向右避开 `x = 1080f..1220f` 的理牌按钮 (`btnSortRect`)。

### 3.2 极限宽度与手牌绝对不重叠证明
在日麻四开杠（4 个杠）的最宽合法极端状态下：
- 每组宽度：`(4 - 1) * 38f + 36f = 150f`
- 4 杠总宽度：`4 * 150f + 3 * 12f = 636f`
- 副露整体占据区间：`[1048 - 636, 1048] = [412f, 1048f]`
- 此时手牌仅剩 1 张暗牌（+1 张摸牌）：
  - 暗牌槽位：`x = 220f`，右边界 `220 + 54 = 274f`
  - 摸牌槽位：`drawnX = 220 + 1 * 58 + 24 = 302f`，右边界 `302 + 54 = 356f`
- **间距余量**：`412f - 356f = 56px`。即使在极限四杠状态下，副露与手牌仍有至少 56px 物理间隔，两者绝对不会发生重叠。

### 3.3 各副露类型的视觉规范

| 副露类型 | 牌数 | 牌面展示 | 来源标示 | 特殊高亮与边框 |
| :--- | :---: | :--- | :--- | :--- |
| **CHI (吃)** | 3 | 3 张正面 | `FROM Pn` (y=99f) + `CHI` (y=110f) | `calledTile` 橙色双层线框 + `CALLED` 标签 (y=35f) |
| **PON (碰)** | 3 | 3 张正面 | `FROM Pn` (y=99f) + `PON` (y=110f) | `calledTile` 橙色双层线框 + `CALLED` 标签 (y=35f) |
| **OPEN_KAN (大明杠)** | 4 | 4 张正面 | `FROM Pn` (y=99f) + `OPEN KAN` (y=110f) | `calledTile` 橙色双层线框 + `CALLED` 标签 (y=35f) |
| **CLOSED_KAN (暗杠)** | 4 | 首尾 2 张牌背 (`faceDown=true`)，中间 2 张正面 | 无来源，组标签 `CLOSED KAN` (y=108f) | 无 called/added 标记 |
| **ADDED_KAN (加杠)** | 4 | 4 张正面（替换原碰组展示） | `FROM Pn` (y=99f) + `ADDED KAN` (y=110f) | 原 `calledTile` 橙色框+`CALLED`；第 4 张（`tiles.last()`）蓝色双层线框 + `ADD` 标签 (y=35f) |

---

## 4. 交互与状态生命周期安全

1. **不可出牌 / 不入 handTileRects**：副露牌绝对不加入 `handTileRects`，不可被悬停、不可被选为手牌，点击副露区域零副作用，绝不向 `session` 提交任何出牌或动作请求。
2. **状态持续可见性**：副露在普通摸打、他家响应等待、立直选牌预览 (`riichiSelectionModel`)、鸣牌方案候选 (`meldSelectionModel`) 时均持续可见，换局或开局重置时随 `currentView.melds` 刷新自动同步。
3. **结算遮罩自然层级**：结算弹窗与暗色遮罩置于桌面牌面提交之后绘制，结算时副露被遮罩自然半透明覆盖。
4. **理牌稳定性**：点击理牌按钮 (`isAutoSort`) 仅对手牌暗牌执行位置重排，`displayedMelds()` 严格保持副露在领域层生成时的原始历史顺序。
