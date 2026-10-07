# 四家副露展示：设计与开发契约

用户：别人吃、碰、杠的牌看不到；Codex 设计和验收，Anti 实现。根因已确定：PlayerView.melds 有全部四家，MeldDisplayLayout.layout 和 GameScreen.render 只取 viewerSeat。

## 唯一开发范围

core/.../render/MeldDisplayLayout.kt、MeldDisplayRenderer.kt、screen/GameScreen.kt；可在 render 新增纯布局 helper。不改 engine/client/server/测试/构建。不调用 shell 或其他代理。读取相关代码片段即可，不需要重读全部历史。Codex 写验收与运行命令。完成后写简短 delivery-ANTI-OPPONENT-MELDS.md。

## 展示设计

- 所有位置以观察者为准：relative=(seat-viewerSeat+4)%4，0 自己底部、1 下家右侧、2 对家顶部、3 上家左侧。姓名/分数、牌河、弃牌动画起点一并用该相对座位映射，避免 viewer 非0时错位。牌面统一直立朝向用户，不引入旋转皮肤或改变鸣牌规则。
- 本人布局完全保留：36x52，step38/gap12，右对齐1048，y40；现有 layout(view) 和 GameScreen.displayedMelds() 保持“本人副露”语义和现有测试。新增 layoutAll(view) 与 GameScreen.displayedTableMelds()，真正的 render 使用后者，不能仅暴露查询而忘接绘制。
- MeldDisplayGroup 添加 ownerSeat:Int（兼容末尾默认参数允许）和 y:Float=40f 等需要的几何元数据；每组保留实际 owner/from/type 和 Tile 物理身份；返回列表及内部列表仍不可变。
- 对家最多四组横排：同36x52/38/12宽度，右对齐958，y600；四杠占 x322..958，不越界。组标签用一行如 `P2 PON FROM P1`，y666，scale约0.60（暗杠不标来源）；tile子标签相对自身y-5，不能沿用固定35。顶部玩家名/分数放到 y590 左右，避免副露与顶部HUD(y680)遮挡。
- 左侧副露 x80，右侧 x1050，每组一行，依次 y480、390、300、210，每行最多4张，宽150；类型/owner/from标签在y+68，scale0.60；子标签y-5。左右玩家名与分数移到各自 x80/1050、y595/572，避免副露覆盖原430位置文字。最下组位于操作按钮上方，边框含±1也不能进入手牌或控件。
- 吃/碰三张，明/暗/加杠四张；暗杠首尾背面、中间正面，不标called/source，不从他家暗牌读取。加杠替换旧碰组、仅最后补入牌标ADD。被鸣牌仍橙框，补入牌蓝框；赤牌/宝牌效果复用当前皮肤和Renderer。
- Renderer 区分自己/对手组的标签布局（可用group.y等明确元数据），本人标签与尺寸不变；对手标签不再固定y35/99/110，不能跑到本人牌区。不给副露增加弃牌交互。
- 牌河使用相对座位坐标；顶部牌河改为 y530 开始往下 step=-38，避免旧向上增长覆盖顶部副露/HUD。其余现有牌河坐标不变。顶部弃牌动画可从(626,575)开始，避免从副露内部飞出；左右起点与座位对应。全部渲染、飞行动画必须用相同映射（检查 renderDiscardRivers 的座位判断，而不是仅改 getter）。
- 直接从最新只读视图渲染，无累计本地副露缓存：吃碰杠后立即可见，重新连接/中途打开画面也可见，换局清空，加杠不重复旧碰。维持现有被取走弃牌的动画调和逻辑。

## 验收重点

Codex 会新增必要回归：原bug全桌漏画、任意viewer映射、每家四组边界/互不遮挡、真实其他座位吃碰各杠的更新、加杠替换与新局清理、皮肤收到所有公开牌且暗杠使用背面、点击副露不弃牌。保持原本人副露与手牌交互测试。

执行 core:test 与真实GL定向场景截图（四家含副露），验收前不要声称测试通过。
