# 已完成副露在本人牌区持续展示

Codex设计、测试、检视；Antigravity开发生产代码。基线273测试通过。当前GameScreen显示本人暗牌和临时候选面板，但未绘制已提交的 `currentView.melds[viewerSeat]`。此次只补本人已完成副露展示，不改规则、动作协议、AI或他家布局。

## 视觉与交互

- 已吃/碰/杠的组固定在本人底部牌区右侧，与可打手牌分开；本人手牌仍从x220开始，原出牌/排序/摸牌坐标不变。
- 副露整体右对齐到x1048；每牌width36 height52，横间距38；组间空隙12。所有牌y40；标签最高y114，最低y25，避开y145的按钮和x1080的理牌键。
- 每组按实际meld列表顺序展示，组内保留实际物理Tile身份。吃/碰3张；每类杠4张；加杠替换原碰组后显示4张，不再画旧碰组副本。
- CHI/PON/OPEN KAN/ADDED KAN显示正面，标明类型和FROM Pn；calledTile橙色描边和CALLED小标签。加杠补入tile通常是meld.tiles.last()（需检查GameStateMachine加杠的真实构造顺序），蓝色描边和ADD标签。
- CLOSED KAN显示首尾牌背、中间两张正面，组标签CLOSED KAN；不标from/called。
- 以紧凑平铺形式呈现四张加杠，不扩展TileSkin旋转/堆叠能力。当前默认英文字体避免汉字缺字。
- 副露不可当作手牌点选/出牌，不加入handTileRects。牌区被点击时不提交任何动作。已完成副露在普通出牌、响应、立直/鸣牌候选预览时均保留，直到换局或会话更新移除；结算遮罩可自然遮挡。
- 非零viewerSeat必须只显示本人的公开副露；不读其他玩家暗牌。排序不会移动或改变副露顺序。

## 固定API（便于Codex独立测试）

新增 `render/MeldDisplayLayout.kt`，无图形上下文依赖，data不可变：

```kotlin
data class MeldDisplayTile(
 val tile: Tile, val x: Float, val y: Float, val width: Float, val height: Float,
 val faceDown: Boolean, val called: Boolean, val added: Boolean
)
data class MeldDisplayGroup(
 val type: MeldType, val fromPlayer: Int?, val tiles: List<MeldDisplayTile>,
 val x: Float, val width: Float
)
object MeldDisplayLayout {
 fun layout(view: PlayerView): List<MeldDisplayGroup>
}
```

按viewerSeat取副露，只读返回深拷贝不可修改列表；输出不改视图，不漏牌/复制牌。四个杠的最宽合法状态也必须与剩余手牌不重叠。合法输入足够，不需要另一套规则验证器。

新增 `render/MeldDisplayRenderer.kt`（保持GameScreen短小）：

```kotlin
object MeldDisplayRenderer {
 fun append(buffer: TileDrawBuffer, skin: TileSkin, groups: List<MeldDisplayGroup>)
}
```

只追加到现有buffer，不begin/end、不直接GL调用。用appendTileDraw/appendTileBackDraw和buffer边框/文字指令绘制。文字字号测量或保守尺寸确保组内不重叠，CALLED/ADD标签不挤压相邻牌。不要新增依赖或图片。

GameScreen仅增加 `fun displayedMelds(): List<MeldDisplayGroup>`（调用layout(currentView)）并在renderPlayerHands旁追加渲染到table缓冲，一起flush。不要增加冗长渲染分支。生命周期无需单独维护缓存，由currentView驱动。

## 所有权与工作限制

Task D拥有上述两个新生产文件、GameScreen最小接入及delivery-D.md。其他文件只读，尤其不要改Test/Gradle/TileDrawBuffer/TileSkin/领域引擎/选牌模型。

仅用内置原生read/search/edit文件工具；禁用RunCommand、shell、终端、MCP工具或其他agent。不要更改/绕过权限。Codex执行全部测试、Gradle构建与截图。

## 验收

真实吃碰明杠暗杠加杠操作确认后，screen.displayedMelds与权威melds一致，精确物理牌不丢不重；吃碰3张杠4张；四副露/四杠最大宽度与手牌/按钮不重叠；非零座位隔离、排序稳定、只读；副露点击不提交；视图更新清空；暗杠牌背/加杠补入颜色；真实OpenGL检查本人手牌旁持续可见，既有273项回归和桌面/Android构建。
