# 立直选牌与待牌预览

Codex 设计、测试、审查；Antigravity 开发。延续现有立直后自动摸切/自摸暂停/严格暗杠，不改变那些规则。

## 产品流程

点击 RIICHI 仅进入选牌，不提交命令、不自动选择第一项、不扣点。可立直的手牌加金色轮廓，其他牌暗化。点击合法弃牌即选中（抬高/明确选中标记），可反复改选；点击非法弃牌不出牌、不改变选择。手牌上方显示待牌面板：左侧所选弃牌，右侧所有对应和牌牌面，使用当前 TileSkin，不只用文字牌名。无选中时显示选择提示。悬停合法弃牌可临时预览，移开恢复已选方案；悬停不能修改选中项或提交。移动端点击同样完整可用。

选牌模式仅展示 CONFIRM RIICHI 和 CANCEL；未选择时确认禁用。确认才提交精确的 DeclareRiichi(viewerSeat, chosenPhysicalTile)。取消/ESC/Android BACK 返回普通手动回合，不打牌不扣点。点击选中牌第二次仍只是选择，避免误宣言。排序开关不清除选择，选择始终以 Tile 物理身份绑定，不用显示索引。

待牌列表不显示未知牌山剩余张数、概率或番数；本轮只展示正确牌面。支持两面、多面、七对、国士以及已有暗杠的听牌。最多 13 种待牌不裁剪。可用两行布局，面板不能遮住自己的手牌、确认/取消按钮、排序开关。暂时覆盖中央牌河可以接受，退出模式后消失。已有英文 UI 继续使用英文短标签，避免默认 BitmapFont 中文缺字；牌面使用现有皮肤。

## 固定接口与并行边界

任务 P 只新增以下文件：

1. `session/RiichiPreviewProvider.kt`：
   - `data class RiichiDiscardOption(val action: GameAction.DeclareRiichi, val waits: List<Tile>)`。
   - `object RiichiPreviewProvider { fun options(view: PlayerView): List<RiichiDiscardOption> }`。
   - 仅从当前观察者合法 DeclareRiichi 动作生成，必须是自己的回合、DISCARD/CHEAT_WINDOW、尚未立直。按精确 Tile 身份从 myAllClosedTiles 移除一张；移除失败则不生成。用既有 HandDecomposer 求待牌，排除剩余暗牌 + 自己已有副露中已占满四张的第五张候选。不要按他家暗牌、牌山顺序或公开河里已见数量过滤理论待牌，不显示可见剩余数。
   - 待牌按 typeIndex 去重排序，用 Tile.fromTypeIndex 作纯展示牌；输出集合及内部 waits 不可修改，输入不变。相同牌种不同副本保留不同 action，不能丢失真正选中的物理牌。
2. `screen/RiichiSelectionModel.kt`：纯表现状态，可单独测试，不依赖 Gdx。
   - 无参构造；只读属性 `isSelecting: Boolean`, `selectedTile: Tile?`, `options: List<RiichiDiscardOption>`。
   - `fun update(view: PlayerView)`：刷新方案。首次初始化不进入选择；值相等视图更新保留选择。视图实质变化时退出并清空选择，防止旧动作确认（比较视图值即可）；排序不修改视图。
   - `fun begin(): Boolean`：有方案才进入；不自动选中，已在模式中再次 begin 不清空已有选择。
   - `fun select(tile: Tile): Boolean`：仅模式内合法物理弃牌可选，非法选择保留已选项。
   - `fun previewFor(tile: Tile): RiichiDiscardOption?`：纯查询，无状态变化。
   - `fun confirmationAction(): GameAction.DeclareRiichi?`：仅模式内已选择时返回精确合法动作，不自行提交。
   - `fun cancel()`：退出并清空选择。

任务 U 只修改 `screen/GameScreen.kt`：依赖 P 接口，不修改 P 文件。实现上述真实手牌点击/预览/高亮/当前皮肤牌面面板。初始化与 session.subscribe 更新均调用模型 update；确认结果 Accepted/Rejected 都退出选择（Rejected 重新读取会话视图），不能重复提交旧动作。hide/dispose/back 清除模型。不能直接读取 GameState 或重新在 screen 计算麻将规则。

UI 固定坐标（1280x720 世界坐标，供验收）：沿用 RIICHI/确认区域 x670..810,y145..189；CANCEL 区域 x1030..1200,y145..189。选择态其他行动按钮不激活，Sort 与 Back 仍可用。手牌布局沿用当前 OFF：x220 + index*58，摸牌加24；ON 使用 TileArranger 的身份映射。点击事件需在渲染前也能正确构建手牌命中区域，不能依赖上一帧过期的 handTileRects；渲染与触摸共用布局。面板建议 x230..1030,y210..400，宽度足够13面牌。必须先 flush 桌面牌面，再绘制面板与它的牌面，否则牌河会覆盖面板。选牌态手牌框线/暗化应在牌面 flush 之后绘制。

任务 L（补充，独立并行）仅修改 `rules/RiichiRuleEngine.kt` 的 canRiichi：现有实现按牌种去重，只允许打出第一个物理副本，导致玩家无法选择同种的摸牌副本。改为返回所有符合条件的精确物理弃牌（可以按牌种缓存待牌判断），保留现有门清/未立直/点数/牌山条件。听牌判断排除剩余暗牌+自己的副露已占满4张的第五张候选，但不因牌河已见而拒绝空听。不要改其他规则行为。L 写 delivery-L.md。P/U 不要修改 L 文件。

不修改测试、Gradle/依赖或所有权之外的生产文件。只用内置文件读写/编辑工具；禁止 RunCommand/终端/shell（无头权限未授权），不要绕过权限。Codex 执行所有测试与构建。各自写 `doc/riichi-choice/delivery-P.md` 或 `delivery-U.md` 后返回，不要为测试调用终端。

## 验收

首次点击立直无提交；未选择确认无提交；两种弃牌对应不同待牌；改选、取消与非法牌点击零领域副作用；准确物理副本/非零座位/排序；手牌视图更新关闭旧选择；拒绝确认可恢复；七对/国士13面/已有暗杠；预览不可变且不泄露私有牌；真实 GameScreen→Session→Engine 确认才扣点并舍牌；保留此前213项回归，替换其中旧的一键立直UI断言。实际OpenGL截图检视面板、高亮与按钮。
