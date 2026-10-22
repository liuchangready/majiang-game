# 正式 APP 牌桌视觉接入交付

开发：DeepSeek Flash（用户指定替代 MiniMax）。设计与七张生产素材：Codex。
日期：2026-10-22。范围：正式 libGDX APP 的准备阶段、目标抽屉、本人回执与真实发牌后的同场景衔接；未改引擎、协议、服务端规则，未做 ui_demo，未 commit/push。

## 1. 接入结果

本轮不再使用纯色大矩形模拟桌面，正式 GL 场景直接使用 `assets/ui/table/` 的七张独立素材：

| 层 | 正式实现 |
| --- | --- |
| Z0 环境 | `club-environment.png`，按可见世界 cover 居中填满，准备、抽屉、回执、发牌后都不消失 |
| Z10 桌面 | `jade-table-board.png`，固定 `(32,76,1216,620)` 设计矩形（libGDX `y=24`），不随安全区/抽屉压缩 |
| Z20 罗盘 | `round-compass.png`，准备 `220×220`，发牌后 `188×188`；局次/风位/宝牌由代码绘制 |
| Z30 公开席 | `animal-medallions.png` 绝对席位 0/1/2/3 → 章鱼/喜鹊/熊猫/狐狸；名字、分数、风位、庄、立直、时钟、当前巡来自公开快照 |
| Z40 真实牌 | 沿用既有牌皮肤、手牌/暗手牌/牌河/副露渲染；新增本人手牌托盘与对家手牌托盘，均在真实牌层之下 |
| Z50 面板 | `jade-panel-skin.png` 九宫格；`split340`，主面板目标角 44，名牌 12，提示 18，卡 20 |
| 按钮 | `button-skins.png` 先取红 `[40,96,1456,384]` / 绿 `[40,544,1456,384]`，再按 248/248/120/120 split，目标边 32×18 |
| 目标卡 | `target-card-scenes.png` 六张 manifest 切片，禁止整图集；每卡下方 21px 名称，上方用当前 `TileSkin.appendRotatedTileDraw` 画 3～5 张独立装饰示例牌 |
| 回执 | 独立 304×260 只读卡，34px 金色标题、方案、费用、等待文案、勾选和四点；完整主面板金框；150ms 淡出与权威发牌并行 |

### 主要源码

- `presentation/src/main/kotlin/com/majiang/presentation/common/render/TableVisualAssets.kt`：七张纹理、源切片、九宫格目标边框的唯一根资源包。
- `.../TableStageLayout.kt`：持续场景、准备/发牌 HUD、托盘的 manifest 几何。
- `.../TableStageRenderer.kt`：Z0/Z10/Z20/Z30、托盘、底部提示、右下操作底板。
- `.../TableHudView.kt`：玩家/观战共用的最小公开 HUD 投影，观战源码不引用 `PlayerView`/私有手牌/千术字段。
- `.../TableBoardRenderer.kt`：发牌后中心文字与宝牌移入/围绕新罗盘；共享路径同时用于观战。
- `presentation/.../game/RoundPreparationLayout.kt`：重建为 manifest 坐标（准备栏 312×624、抽屉 560×640、卡 252×114、按钮 272×76 等）。
- `.../game/ShufflePreparationPanel.kt`：新九宫格面板、插画卡、当前 TileSkin 示例牌、金框/勾选/轻抬 4、新回执。
- `.../game/GameScreen.kt`：持续场景、公开 HUD、宽屏舞台相机、准备/发牌衔接、回执/抽屉局部动画。
- `.../game/SpectatorScreen.kt`：观战共享持续场景与公开 HUD，保留纯公开流和只读边界。
- `.../game/TileMarkPanel.kt`、`SleeveBloomPanel.kt`、`BeamSwapPanel.kt`：发牌后技能入口底部接入新九宫格底板，保留原权限/禁用/时序；选中态和二级弹窗的旧语义未改。
- 删除 `PreparationTableRenderer.kt`（旧纯色桌面/绿色占位文案来源）。

### 坐标与资源细节

- 目标矩形全部是 1280×720 **左上角**设计坐标；绘制时统一转 `y = 720 - top - height`。
- 图集源切片按 manifest 左上角行列直接取 `TextureRegion`。实现中用运行帧验证过：若再对源切片做一次 Y 翻转，会把按钮红/绿、六张目标场景和四张头像整体上下颠倒；因此源切片没有重复翻转。目标矩形转换与源码切片是两件事。
- 九宫格从源区取角/边时，`TextureRegion` 顶行必须取 local `y=0`、底行取 `y=h-split`；早期版本把 Top/Bottom row 取反，导致主面板上下浮出独立金条、抽屉顶边悬空、按钮重复金条。该映射已修正，并保留 `split 340` / 按钮 `248/120` 的源切分，只显式覆盖目标边 44/12/18/20 与 32/18。
- 七张素材 SHA256 与 `manifest.json` 全部一致（见第 4 节）。
- 所有纹理 Linear + ClampToEdge、无 mipmap、straight alpha；真实 Alpha 未按黑底抠图。
- 纹理、九宫格、34/28/25/19/34px 字体由 `UiAssets` 根持有并只加载一次；Screen/面板只借用，退出时不释放共享资源。

### 行为保持

未修改 `ShufflePreparationModel`。保留：0 点立即提交开始、看破 10/目标 30/合计 40、无默认目标、Esc 清目标保留看破、失败恢复原页与草稿、旧 round 回包隔离、回执真实渲染后按 150ms 淡出且不等待发牌、所有座位确认即开局、136 张实体守恒、权威 deadline/tick 不可变、主菜单按钮向右滑且相机固定的既有修复。

## 2. 实际运行截图

全部来自正式 `MahjongGame → 主菜单 SOLO → 真实 GameScreen → 真实本地权威准备` 的 GL 帧缓冲，不是 ASSEMBLY 或 mock。

- `doc/ui/round-preparation/visual-runtime/visual-runtime-01-entry-1280.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-02-drawer-mid-1280.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-03-targets-unselected-1280.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-04-target-selected-1280.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-05-cost40-1280.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-06-960x540.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-07-1600x600.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-08-receipt-1280.png`
- `doc/ui/round-preparation/visual-runtime/visual-runtime-09-dealt-1280.png`
- 运行日志：`doc/ui/round-preparation/visual-runtime/smoke-results.txt`

`02` 为 280ms easeOutCubic 抽屉的真实中段帧（等待 `drawerOffsetX <= 100` 才截）；`04` 为 30 点选中；`05` 为 40 点。`06/07` 是缩放后的实际窗口帧，宽屏相机扩展世界到 1920×720 进行等比居中，不再横向拉伸头像/文字，并已在同一次 smoke 中真实点击 1600×600 下的目标卡验证命中一致。

## 3. 验证命令与结果

```powershell
.\gradlew.bat :presentation:compileKotlin --console=plain
# BUILD SUCCESSFUL

.\gradlew.bat :presentation:test --rerun-tasks --console=plain
# 459 tests, 0 failures, 0 errors（68 个 XML suite）

.\gradlew.bat :lwjgl3:compileKotlin --console=plain
# BUILD SUCCESSFUL

.\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmoke --console=plain
# PASS: round preparation GL smoke mode=target
# confirmToDealMs≈461–515ms（多次运行，本机；返修末次 463ms）
# receipt-linger-after-deal≈131–154ms（返修末次 149ms）
# after deal: handTiles=53 wallRemaining=69 deadWall=14 → 136 守恒

.\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmokeZero --console=plain
# PASS: round preparation GL smoke mode=zero
# confirmToDealMs≈463ms（返修末次）；receipt-linger≈153ms；136 守恒

.\gradlew.bat :android:compileDebugKotlin --rerun-tasks --console=plain
# BUILD SUCCESSFUL（返修末次 1m18s；仅 SDK XML 版本提示）
```

Smoke 的旧回执检测已改为新金色标题区域，并继续检测：回执首帧不透明可读、回执不阻塞发牌、发牌后不宣称仍在等待、回执 150ms 左右淡出、136 守恒。`1600×600` 目标卡真实点击也加入了 smoke 断言。

素材校验：

```text
club-environment.png   32f05e…983f match
jade-table-board.png   610878…b42d match
jade-panel-skin.png    4dda0d…57b  match
button-skins.png       ce4932…653f match
animal-medallions.png  c457e0…9149 match
target-card-scenes.png 0000aa…2c37 match
round-compass.png      b34c8d…ea7f match
```

（完整哈希见 `assets/ui/table/manifest.json`；逐项实际值一致。）

## 4. 视觉对照记录

已按四张 ASSEMBLY 与三张原先认可图逐层检查，并用 Codex 只读截图检查复核（仅作开发自检，不替代最终独立验收）：

- 环境/木沿/锦纹/金云纹：来自 `club-environment` + `jade-table-board`，准备和发牌后同一张桌面持续存在；发牌截图不再回到纯绿背景。
- 头像与名牌：四张动物头像按绝对席位固定在桌面四周；抽屉展开只移动右家。主体按 manifest，为避免头像金框/手牌托盘/罗盘互相压边，本人准备头像上移 12px、本人发牌名牌收为 `(132,58,72,48)`、抽屉右家名牌右移到 `(564,378,124,50)`；公开数据不变。分数、风位、庄/立直/当前巡、时钟保留。公开 `client.api` 没有玩家昵称字段，因此按规范使用 `P0..P3 + 风位`，未用动物名冒充玩家身份。
- 罗盘：准备态显示局次/准备开局/本场供托，发牌后缩到 188 并显示局次、本场供托、牌山、宝牌；底部风位让给宝牌，避免文字撞牌。
- 面板/按钮：主面板 44 目标角，小名牌 12、短提示 18、卡 20；按钮先取红/绿源区再 split 248/248/120/120，目标 32×18。未直接把 340/248 源像素当逻辑边框。
- 目标卡：六张场景与 manifest 顺序一致；插画上叠加当前牌皮 3～5 张示例牌；无默认选择；选中卡金框、右上勾选、120ms 上抬 4；示例牌只进入局部 `TileDrawBuffer`。
- 回执：金色标题、方案/费用、等待文案、勾选、四点；真实渲染后开始 150ms 淡出，发牌不等回执。
- 发牌后：手牌托盘、对家托盘、右下操作底板和技能入口九宫格已接入；理牌/托管与三绝技入口都改为九宫格底板 + 明确金边 + 19/21px 高对比标签，左侧色条保留原 RON/TSUMO/PASS/托管状态语义；牌河提示、权限/禁用态/时钟/立直/庄家/当前巡语义保留。
- 1600×600：舞台相机扩展到 1920×720 世界并以 (640,360) 居中，桌面/头像/文字等比，环境 cover，准备栏按真实右沿锚定。该方案不引入 FitViewport，也不改牌河/鸣牌/手牌逻辑坐标。

自查后修复的实际差异：源图集切片 Top/Bottom row 取反导致的主面板浮动金条、抽屉悬空顶边、按钮重复金条；源图集整体上下颠倒导致的红/绿按钮、目标场景、头像错位；抽屉与准备栏同帧叠字；回执底行被边框截断；回执残影按钮；目标卡选中黄块盖住插画；小按钮文字压金线；技能/托管纯色矩形；P0 窄名牌时钟/庄标/分数挤压；观战 HUD 误引用私有 `PlayerView`。返修后的 1280/960/1600 帧按逐张对比再检查，未发现高/中等级可见缺陷。

## 5. 已知缺口与未虚报项

1. `client.api` 当前没有玩家昵称/头像身份字段；名牌使用公开席位 `P0..P3` 与风位，动物头像只按绝对席位装饰，不是新选角系统。
2. 发牌后本人名牌只有 72px 宽：姓名/时钟在第一行，分数/风位（庄/立直）在第二行，字号做了 0.68～0.82 缩放；其他三席仍用标准 21px 分数与 0.9 时钟。未改变数据或权威时钟。
3. 为避免金框相碰，准备阶段本人头像上移 12px、发牌本人名牌由 80 缩为 72、抽屉右家名牌从 538 移到 564；这三处是 4～12px 的可读性避让，不是原始 manifest 数值。
4. 本轮只重绘准备/抽屉/回执/持续桌面/公开 HUD/发牌后入口与托盘。落汗、袖里藏花、偷梁换柱的二级选牌弹窗仍沿用既有布局和语义，只替换了入口按钮底板；这是刻意控制行为回归范围，不声称二级弹窗也完成全量皮肤化。
5. 1600×600 采用扩展世界相机而非 FitViewport；目标卡命中已实测。没有重写牌河、鸣牌、手牌的世界坐标。
6. 本文件是开发交付；最终视觉与行为结论仍由 Codex 独立审查。未 commit/push，未动 `ui_demo`、七模块边界、引擎/协议/服务端规则及 `snapshot/table-design-edge`。

## 6. 未处理文件声明

Codex 本次交付的 `assets/ui/table/`、四张 ASSEMBLY PNG/SVG、`ASSET-HANDOFF.md`、`VISUAL-COMPOSITION.md`、`tools/ui-assets/build-table-design.cjs` 与 `snapshot/table-design-edge/` 均保留原样，未清理、覆盖、提交或撤销。`visual-runtime/` 是本轮开发生成的真实 GL 截图目录。
