# 正式 APP 响应式 UI 续做交付

2026-10-22 · DeepSeek 在现有未提交工作区继续开发，Codex 独立检视。

本文件只记录本人实际完成内容、可复跑证据和缺口，**不自行宣称最终验收通过**。最终以 Codex 独立读 diff、复跑与看图为准。

---

## 0. 结论与边界

- 正式生产链 `MahjongGame → 主菜单 SOLO → 真实 GameScreen → 权威准备 → 真实发牌` 已接入中央 16:9 stage；观战共用同一套 stage/HUD/牌桌渲染。
- 保留并继续使用 `TableVisualAssets / TableStageRenderer / TableStageLayout / TableHudView / UiAssets / GameScreen / SpectatorScreen / 准备栏 / 三个千术二级面板`，没有从 HEAD 重置，没有恢复已删除的 `PreparationTableRenderer.kt`。
- 本机最终验证：`presentation` 测试 **471 项，0 失败**；`:lwjgl3:classes`、`:android:compileDebugKotlin` 成功；设计参考测试 5936 断言通过；正式 APP GL 冒烟 **PASS, 29 张真实帧缓冲截图**；target/zero 两个准备冒烟均 PASS。
- 未改 engine / protocol / server / client 规则，未改 `ui_demo`，未提交 Git。
- **视觉复核方式说明**：当前 DeepSeek 模型不具备图像输入能力，不能声称“逐张目视”。本次使用 Windows OCR 文本框、帧缓冲像素采样、设计图缩略差异（meanAbs）、Alpha/几何边界和 29 张原图保留来核对；`responsive-app-runtime/index.html` 供 Codex 和用户直接逐张查看。最终视觉验收必须由 Codex 完成。

---

## 1. 本次实际实现

### 1.1 唯一 stage 变换

新增/接续：

- `presentation/.../common/render/TableStageViewport.kt`
  - `1280×720` 设计画布等比 fit 到安全窗口：`scale = min(availableW/1280, availableH/720)`。
  - `stageLeft/stageTop/stageWidth/stageHeight`、`framebufferStageX/Y/W/H`、`framebufferRatio`。
  - `toStage` / `toWindow` / `localRectToWindow`；stage 外返回 `null`。
  - 零宽高返回 `EMPTY`，不做除零。
- `GameScreen` 与 `SpectatorScreen`：
  - 相机固定 1280×720，不再扩大 `worldW/worldH`。
  - 每帧先重算 stage；GL viewport 与 scissor 使用帧缓冲像素。
  - Z0 环境以 window 空间 cover 整个窗口；stage 图层再切 viewport/scissor，环境侧不接牌桌输入。
  - `framebufferRatio` 只用于 GL viewport/scissor 与字体原生像素，输入不重复乘 DPI。
- `TableStageLayout.kt` 固定桌面 `(32,76,1216,620)`、准备/发牌罗盘、四席 HUD、手牌托盘、顶部提示、右下操作列；抽屉不再压窄桌面。
- `TableLocalZoom.kt`（新增）：牌河提示打开时，牌桌局部缩放到 stage 左侧 840px。绘制矩阵和输入逆变换共用该对象；回归测试验证 `camera.combined` 投影后能被正式输入路径还原。

### 1.2 输入、焦点与裁切

- `GameScreen.screenToUi` 统一走 `stageViewport.toStage`；审计后没有保留默认全窗口 `camera.unproject`。
- `touchDown/touchUp/touchDragged/mouseMoved` 全部使用同一反变换；`backButtonRect`、准备栏、牌河提示、结算/终局/技能面板均使用 stage 坐标。
- `TableLocalZoom` 的局部矩阵与逆变换在 `GameScreen.render`、`touchDown`、`mouseMoved` 三处共用，不再各写一份浮点常量。
- 准备面板新增键盘焦点：
  - 默认焦点「开始本局」；抽屉内按六张目标卡 → 看破 → 直接开局 → 返回 → 确认施术移动。
  - Tab/方向键移动，Enter/Space 激活焦点控件；Esc 从抽屉返回并清目标、保留看破。
  - 焦点框由 `focusedRect()` 生成，和绘制/命中矩形同源，使用 stage 投影。
- 抽屉/准备栏只在 stage 内滑入；stage scissor 裁掉越过右沿的入场部分。抽屉入场未完成时拒绝面板点击。
- 观战 `SpectatorScreen` 复用 `TableStageRenderer`/`TableHudView`/`TableBoardRenderer`，输入只有返回/退出语义，不产生牌桌动作。

### 1.3 HUD 与美术

- 四席 HUD 改为“头像精灵 + 独立名牌”：头像不再带白方框，名牌不再缩进 72px。
- 公开信息：`P0..P3 · 风位`、真实分数、庄家/立直标记、当前巡高亮、真实时钟；没有用动物名冒充玩家昵称（client.api 无昵称）。
- 发牌后 HUD 与手牌托盘、操作列分居 stage 内，四席名牌和按钮不重叠。
- 准备栏、抽屉、回执、目标卡、按钮全部使用 `assets/ui/table/` 的七张素材和 manifest 切片；没有用纯色矩形替代素材。
- 三个技能入口（落汗/袖里藏花/偷梁换柱）保留 `button-skins` 红底金边，标题改用当前 stage 的 `TableStageFonts.body` 原生字形（19px 设计字号 × stageScale × framebufferRatio），并在中央压一条 22px 半透明暗带提升白字对比度；实测 1280 下「偷梁换柱 10」「袖里藏花 10」及裁剪后的「落汗」可读，960 下「偷梁换柱」「袖里藏花」可读，金边与外圈红底仍保留。
- 本人名牌在头像下方独立排版，字号档位 17/21px，不再压到 0.68 倍。

### 1.4 资源、字体与释放

- `UiAssets` 根持有 `TableVisualAssets`、主菜单资源、共享字体与 `SpriteBatch/ShapeRenderer/SkinManager`；Screen 只借用，不 dispose。
- 牌桌字体 `StageFont` 按 `设计字号 × stageScale × framebufferRatio` 生成原生 FreeType 字形；绘制时只抵消帧缓冲像素比，不使用 17px 字库放大冒充标题。
- 生产字体缓存为 **有界 LRU，上限 32 个原生像素字号**；淘汰和根释放都会 `dispose`。最终 29 张截图跨 9 个尺寸后缓存为 **26/32**，未无限增长。
- 七张美术纹理在 GL3 环境生成 mipmap + `MipMapLinearLinear/Linear`，GLES2 安全回退 Linear；源 PNG 像素未改。
- `TableVisualAssets.load()` 失败会释放已创建纹理；`UiAssets.dispose()` 统一释放纹理、字体、生成器和共享 Skin。

### 1.5 resize 与状态保持

- `GameScreen.resize` / `SpectatorScreen.resize` 只重算 view/stage/scissor/字体档位，并 `pressCancel()` 取消准备面板正在按住的提交。
- 不重置：目标/看破/费用、权威截止、回执状态、弃牌动画、发牌后手牌。
- 零宽高切 `EMPTY`，恢复后先重算 view 再处理输入。
- 连续 resize 冒烟中，每步先按住「确认施术」再改窗口；释放后 `submitting=false`、`confirmed=false`，最后只在 1280×720 提交一次。
- 单机 0 点直接确认、电脑及时确认、回执与发牌并行淡出保持；没有新增 10/15 秒等待，未暂停权威 tick。

---

## 2. 实际源码

### 2.1 正式实现

| 文件 | 作用 |
| --- | --- |
| `presentation/src/main/kotlin/com/majiang/presentation/common/UiAssets.kt` | 根资源、牌桌九宫格按钮、原生字号 LRU 字体缓存、mipmap 纹理所有权、统一释放 |
| `.../common/render/TableStageViewport.kt` | 唯一 16:9 fit、输入正/逆变换、帧缓冲视口/scissor |
| `.../common/render/TableStageLayout.kt` | 桌面、罗盘、四席头像/名牌、托盘、操作列、准备提示的 stage 几何 |
| `.../common/render/TableStageRenderer.kt` | Z0 环境 cover、Z10 桌面、Z20 罗盘、Z30 HUD、托盘、准备提示、操作底板、兜底 |
| `.../common/render/TableHudView.kt` | 玩家/观战共用的公开 HUD 投影 |
| `.../common/render/StageFonts.kt` | 34/28/25/21/19/17/16/32 字号角色与绘制/测量 |
| `.../common/render/TableLocalZoom.kt` | 牌河提示局部缩放矩阵与逆变换 |
| `.../common/render/TableVisualAssets.kt` | 七张素材、manifest 切片、mipmap、九宫格目标边框 |
| `.../common/render/TableBoardRenderer.kt` | 发牌后中心文字、手牌、牌河、副露，共享给观战 |
| `.../game/GameScreen.kt` | 正式牌桌、stage 输入/GL viewport、HUD、准备栏、回执、技能面板、结算/终局 |
| `.../game/SpectatorScreen.kt` | 同一 stage/HUD/牌桌渲染的观战路径 |
| `.../game/ShufflePreparationPanel.kt` | 准备栏/抽屉/回执、键盘焦点、按压取消、失败恢复、回执淡出 |
| `.../game/RoundPreparationLayout.kt` | 准备栏/抽屉/回执/目标卡的 1280 设计矩形 |
| `.../game/RiverHintPanel.kt` | 牌河提示面板与阶段字体借用 |
| `.../game/TileMarkPanel.kt` | 落汗入口/二级选牌 |
| `.../game/SleeveBloomPanel.kt` | 袖里藏花入口/二选一 |
| `.../game/BeamSwapPanel.kt` | 偷梁换柱入口/二级选牌 |
| `.../game/RightActionColumnLayout.kt` | 右侧五按钮固定 stage 操作列 |

`presentation/.../common/render/PreparationTableRenderer.kt` 仍是删除状态，未恢复。

### 2.2 测试与冒烟

| 文件 | 作用 |
| --- | --- |
| `presentation/src/test/.../common/render/TableStageViewportTest.kt` | fit golden、4:3/超宽、DPI 比例、零尺寸 |
| `presentation/src/test/.../common/render/TableLocalZoomTest.kt` | 牌河局部矩阵投影 → 正式输入逆变换往返 |
| `presentation/src/test/.../game/RoundPreparationFlowTest.kt` | 0/10/30/40、失败恢复、resize 取消按压、键盘焦点/确认、旧回包隔离 |
| `presentation/src/test/.../game/RoundPreparationLayoutTest.kt` | 四席/抽屉/回执几何、交互区不重叠 |
| `presentation/src/test/.../game/SpectatorSharedTableRenderTest.kt` | 观战共享牌桌多帧渲染、四视角弃牌槽位 |
| `presentation/src/test/.../game/SpectatorScreenContractTest.kt` | 观战只消费公开快照、无玩家动作 |
| `tools/architecture/ResponsiveAppRenderSmoke.java` | 正式 APP 响应式 GL 冒烟（本轮重写） |
| `tools/architecture/responsive-app-smoke.init.gradle` | 该冒烟 Gradle 入口 |

正式冒烟只走 `MahjongGame → MenuScreen SOLO → GameScreen → 权威准备/发牌`，没有新建样例渲染器。权威准备窗口只有 10 秒，所以本轮把尺寸矩阵拆成“第一局做 1280 全状态 + 连续 resize，随后每个尺寸重新真实开局取得独立准备窗口”，没有暂停权威 tick，也没有改 `selectionMillis`。

---

## 3. 命令与结果

```powershell
# 1. 正式测试与双端编译
.\gradlew.bat :presentation:test :lwjgl3:classes :android:compileDebugKotlin --console=plain

# 2. 正式 APP 响应式 GL 冒烟（29 张真实帧缓冲）
.\gradlew.bat -I tools/architecture/responsive-app-smoke.init.gradle :lwjgl3:responsiveAppSmoke --console=plain

# 3. 既有 target / zero 正式准备 GL 冒烟
.\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmoke --console=plain
.\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmokeZero --console=plain

# 4. 保留的固定设计参考测试（不替代正式 APP 验收）
.\gradlew.bat -I tools/ui-layout/responsive-design.init.gradle :lwjgl3:responsiveDesign --console=plain
```

实际结果：

| 项目 | 结果 |
| --- | --- |
| `:presentation:test` | **471 tests, 0 failed, 0 skipped**（70 个结果文件） |
| `:lwjgl3:classes` | BUILD SUCCESSFUL |
| `:android:compileDebugKotlin` | BUILD SUCCESSFUL |
| `responsiveAppSmoke` | **PASS, captures=29**；连续 6 次 resize；最终只提交一次 |
| 字体缓存 | **26 / 32**（跨 9 个窗口尺寸后仍有界） |
| target 准备冒烟 | **PASS**；confirm→deal **474.40 ms**；回执发牌后 **148.66 ms** 消失 |
| zero 准备冒烟 | **PASS**；confirm→deal **469.81 ms**；回执发牌后 **153.90 ms** 消失 |
| 136 守恒 | 每局 `hands=53 + wall=69 + deadWall=14 = 136` |
| 设计参考测试 | **PASS**；26 场景、5936 断言、1352 GPU/CPU 点、7 次真实 resize、18 个 Alpha 边界；最大误差 **0.589844 px** |

说明：`gradlew.bat` 在本机经 `cmd` 包装时常见退出码 1 但末尾为 `BUILD SUCCESSFUL`；本文按 Gradle 输出和测试结果 XML 判定，不把包装退出码当作测试失败。

---

## 4. 正式 APP 截图

目录：`doc/ui/round-preparation/responsive-app-runtime/`

- 检视页：[index.html](responsive-app-runtime/index.html)
- 原始矩阵：[runtime-matrix.csv](responsive-app-runtime/runtime-matrix.csv)
- 冒烟日志：[smoke-results.txt](responsive-app-runtime/smoke-results.txt)
- OCR 文本盒证据：[ocr-evidence.txt](responsive-app-runtime/ocr-evidence.txt)

### 4.1 最低原图文件名

1280×720 全状态：

`app-01-entry-1280x720.png` · `app-02-drawer-mid-1280x720.png` · `app-03-drawer-unselected-1280x720.png` · `app-04-target-selected-1280x720.png` · `app-05-cost40-1280x720.png` · `app-10-receipt-1280x720.png` · `app-11-dealt-1280x720.png` · `app-12-playing-1280x720.png` · `app-13-animation-mid-1280x720.png` · `app-14-river-hint-1280x720.png`

连续窗口：

`app-resize-00-1280x720.png` · `app-resize-01-1160x700.png` · `app-resize-02-1024x768.png` · `app-resize-03-960x540.png` · `app-resize-04-1440x810.png` · `app-resize-05-1600x600.png` · `app-resize-06-1280x720.png`

各尺寸目标抽屉 + 关键发牌图：

`app-06-drawer-960x540.png` · `app-06-drawer-1024x768.png` · `app-06-drawer-1280x800.png` · `app-06-drawer-1600x600.png` · `app-06-drawer-1920x1080.png` · `app-06-drawer-2560x1080.png`
`app-12-playing-960x540.png` · `app-12-playing-1024x768.png` · `app-12-playing-1280x800.png` · `app-12-playing-1600x600.png` · `app-12-playing-1920x1080.png` · `app-12-playing-2560x1080.png`

### 4.2 尺寸/stage 矩阵

| 文件 | 窗口 | 帧缓冲 | stage left,top w×h | 状态 |
| --- | --- | --- | --- | --- |
| app-01-entry-1280x720.png | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | prep PANEL, target=null |
| app-02-drawer-mid-1280x720.png | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 抽屉入场中 |
| app-03-drawer-unselected-1280x720.png | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 未选目标 |
| app-04-target-selected-1280x720.png | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 国士无双, 30点 |
| app-05-cost40-1280x720.png | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 国士无双+看破, 40点 |
| app-resize-00 | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 40点草稿 |
| app-resize-01 | 1160×700 | 1160×700 | 0.00,23.75 1160.00×652.50 | 40点草稿 |
| app-resize-02 | 1024×768 | 1024×768 | 0.00,96.00 1024.00×576.00 | 4:3 环境上下 |
| app-resize-03 | 960×540 | 960×540 | 0.00,0.00 960.00×540.00 | 最小常规尺寸 |
| app-resize-04 | 1440×810 | 1440×810 | 0.00,0.00 1440.00×810.00 | 16:9 |
| app-resize-05 | 1600×600 | 1600×600 | 266.67,0.00 1066.67×600.00 | 超宽两侧环境 |
| app-resize-06 | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 回到基准 |
| app-10-receipt | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 只读回执 |
| app-11-dealt | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 真实发牌 |
| app-12-playing-1280x720 | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 真实牌局 |
| app-13-animation-mid | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 弃牌动画中段 |
| app-14-river-hint | 1280×720 | 1280×720 | 0.00,0.00 1280.00×720.00 | 牌河提示 |
| app-06-drawer-960x540 | 960×540 | 960×540 | 0.00,0.00 960.00×540.00 | 独立准备窗口, 40点 |
| app-06-drawer-1024x768 | 1024×768 | 1024×768 | 0.00,96.00 1024.00×576.00 | 独立准备窗口, 40点 |
| app-06-drawer-1280x800 | 1280×800 | 1280×800 | 0.00,40.00 1280.00×720.00 | 独立准备窗口, 40点 |
| app-06-drawer-1600x600 | 1600×600 | 1600×600 | 266.67,0.00 1066.67×600.00 | 独立准备窗口, 40点 |
| app-06-drawer-1920x1080 | 1920×1080 | 1920×1080 | 0.00,0.00 1920.00×1080.00 | 独立准备窗口, 40点 |
| app-06-drawer-2560x1080 | 2560×1080 | 2560×1080 | 320.00,0.00 1920.00×1080.00 | 独立准备窗口, 40点 |
| app-12-playing-960x540 | 960×540 | 960×540 | 0.00,0.00 960.00×540.00 | 真实发牌 |
| app-12-playing-1024x768 | 1024×768 | 1024×768 | 0.00,96.00 1024.00×576.00 | 真实发牌 |
| app-12-playing-1280x800 | 1280×800 | 1280×800 | 0.00,40.00 1280.00×720.00 | 真实发牌 |
| app-12-playing-1600x600 | 1600×600 | 1600×600 | 266.67,0.00 1066.67×600.00 | 真实发牌 |
| app-12-playing-1920x1080 | 1920×1080 | 1920×1080 | 0.00,0.00 1920.00×1080.00 | 真实发牌 |
| app-12-playing-2560x1080 | 2560×1080 | 2560×1080 | 320.00,0.00 1920.00×1080.00 | 真实发牌 |

全部 29 张截图的实际窗口/帧缓冲/stage 以 `runtime-matrix.csv` 为准；上表为可读摘要。

---

## 5. 输入、状态与像素证据

### 5.1 反投影与点击

冒烟对 1280、1160、1024、960、1440、1600、1920、2560、1280x800 都执行：

1. `stageToScreen` 把目标卡中心投到窗口；
2. `screenToStage` 反算回设计坐标；
3. 断言误差 ≤1.2 设计像素；
4. 用反算出的窗口坐标真实调用 `touchDown/touchUp`；
5. 目标卡第一次点击取消、第二次重新选中，费用保持 40。

日志示例（1600×600）：

```
target click window=1600x600 card=708.00,192.00,252.00,114.00
 screen=961.67,392.50 local=834.40,248.40 before=KOKUSHI
target click after first=null
target click after second=KOKUSHI
```

### 5.2 环境区拒绝

- 1024×768：`stageTop=96`，在 `(512,48)` 点击环境区返回 false。
- 1280×800：`stageTop=40`，在 `(640,20)` 点击环境区返回 false。
- 1600×600：`stageLeft=266.67`，在 `(133,300)` 点击环境区返回 false。
- 2560×1080：`stageLeft=320`，在 `(160,540)` 点击环境区返回 false。

### 5.3 resize 取消按压、草稿保持

连续序列 `1280×720 → 1160×700 → 1024×768 → 960×540 → 1440×810 → 1600×600 → 1280×720`：

- 每步先 `pressRect(确认施术)`，再 `setWindowedMode`；
- `GameScreen.resize` 调 `pressCancel()`；
- 释放后日志均为 `submitting=false confirmed=false`；
- 每步真实点击国士无双两次，`cost=40`、`see=true` 保持；
- 只在最后 1280×720 点击一次「确认施术」，日志 `final confirm submitted once`。

日志节选：

```
continuous after release index=0 ... target=KOKUSHI cost=40 submitting=false confirmed=false
continuous after release index=1 ... target=KOKUSHI cost=40 submitting=false confirmed=false
...
continuous after release index=5 ... target=KOKUSHI cost=40 submitting=false confirmed=false
final confirm submitted once; modelSubmitting=true page=DRAWER
```

### 5.4 选中/美术/回执/牌河像素

- 目标卡选中：`app-03` 相同坐标 `(939,430)` 为暗色 `31,52,71`，`app-04` 为金色勾选 `231,184,82`。
- 牌河提示像素 `(900,320)` 为 `7,52,44`（翡翠绿/金），冒烟断言 `G+16 >= B`，排除旧蓝矩形。
- 回执标题 OCR 为「方案已确认」，像素位置在 `(1018..1174,194..224)`；回执卡 `(944,160,304,260)`。
- 1280 准备栏标题 OCR「本局准备」约 31px 高；抽屉标题约 27px；目标卡名约 20px。

### 5.5 136 物理守恒

- 1280 主局：`hands=53 wall=69 deadWall=14 = 136`，cheat=`[60,100,100,100]`（本轮花 40 点）。
- 每个独立尺寸局：`hands=53 wall=69 deadWall=14 = 136`，cheat=`[100,100,100,100]`。

### 5.6 观战共享路径

`SpectatorSharedTableRenderTest` 在 headless 下对真实 `SpectatorScreen` 连续渲染 10 帧，覆盖赤宝牌、吃/碰副露、四家不同手牌数，并验证四视角弃牌槽位集合旋转等价。观战与玩家共用 `TableStageRenderer`、`TableHudView`、`TableBoardRenderer`。本轮没有建立真实观战房间做 GL 截图，这是残余缺口。

---

## 6. 与设计图的对照

用同一尺寸的正式 APP 截图与 `responsive-validation` 设计截图做 320×180 缩略图平均绝对差（meanAbs，0..255）：

| 场景 | meanAbs | ≥32 差异块占比 |
| --- | ---: | ---: |
| 1280 默认准备 | 8.44 | 6.1% |
| 1280 未选抽屉 | 13.98 | 11.3% |
| 1280 已选抽屉 | 14.43 | 11.9% |
| 1280 回执 | 4.91 | 3.4% |
| 1280 发牌后 | 7.37 | 5.9% |
| 960 抽屉 / 发牌 | 18.54 / 11.17 | 16.3% / 8.7% |
| 1024×768 抽屉 / 发牌 | 11.94 / 6.25 | 10.9% / 5.2% |
| 1280×800 抽屉 / 发牌 | 14.10 / 6.74 | 11.6% / 4.8% |
| 1600×600 抽屉 / 发牌 | 12.38 / 6.16 | 11.8% / 5.0% |
| 1920×1080 抽屉 / 发牌 | 13.86 / 5.96 | 11.8% / 5.1% |
| 2560×1080 抽屉 / 发牌 | 10.56 / 4.42 | 8.9% / 3.7% |

差异主要来自正式 APP 的真实数据（P0..P3、真实分数/风位/时钟、实发 53 张牌、真实回执）与设计样例固定数据；背景、桌面、罗盘、头像、抽屉、回执的构图和位置一致。960 抽屉差异最大，主要因为文字/样例卡名在 0.75 缩放下光栅化不同，未发现抽屉压窄桌面或按钮漂到环境区。

**本轮实际修复的明显视觉问题**：三个技能入口的白字与红底金饰对比不足。已加 22px 中央暗带（保留外圈红底与金边），OCR 从读不到变为可读到「落汗」「袖里藏花 10」等；未用纯色矩形替代素材。

**必须由 Codex 目视确认的点**：头像金饰锯齿、九宫格金线是否重复、本人名牌是否挤字、目标卡底部标签、技能按钮金饰、超宽环境是否有拉伸/漂移。当前代理无法读图，只能提供上述 OCR/像素/差分证据。

---

## 7. 参考文件完整性

- `RESPONSIVE-APP-BASELINE.json` **未覆盖**。
- 七张 `assets/ui/table/*.png`、四张 `ASSEMBLY-*`、`responsive-validation` 的 38 张原始设计截图 + `overview.png`、`positions.csv`、`alpha-bounds.csv`、`REPORT.md`、`responsive-spec.json`、`tools/ui-layout/*` 均保留；除下面一项外，基线中 61 个参考文件的 SHA256 全部一致。
- **例外**：按交接要求复跑保留的设计测试时，`responsive-validation/validation.json` 被该测试重写，从基线 168,277 bytes 变为 64,025 bytes（当前测试 schema 的紧凑输出）。所有设计 PNG 仍与基线逐字节一致，REPORT 的 5936/1352/0.589844 结论不变。若需要严格恢复原 JSON，需从 Codex 的原始参考副本取回；本次没有伪造旧文件。
- 未改 `ui_demo`，未处理 `snapshot/table-design-edge` 缓存，未提交/推送 Git。

---

## 8. 残余环境缺口

以下项**没有**被本次验证为通过：

1. 原生 3440×1440 窗口：本机 GLFW 请求被系统限制宽度（设计阶段记录为 3124），`responsive-validation` 的 3440 图是 FBO 离屏，不可称原生窗口通过。本轮正式 APP 矩阵只到 2560×1080。
2. 1.5×/2× 高 DPI：设计测试使用 FBO 模拟，未切换 Windows 系统缩放/跨屏拖动；正式 APP 未测 OS 缩放。
3. OS 最大化/最小化恢复：未做真实最小化/最大化；0×0 仅为合成视图测试。
4. Android 真机/模拟器：只验证 `:android:compileDebugKotlin`，未测真实安全区、48dp 触控、GLES2 无 mipmap 路径或竖屏（不在横屏设计范围）。
5. 小于 960×540：仅设计侧 800×450 不越界检查；正式 APP 不承诺可读性。
6. 真实观战 GL 截图：只有 headless 共享渲染/契约测试。
7. 最终视觉验收：见 §0/§6，需 Codex 看原图。

---

## 9. 交给 Codex 的复核清单

1. `git diff` 读 `GameScreen/SpectatorScreen/TableStageViewport/TableStageLayout/TableStageRenderer/UiAssets/ShufflePreparationPanel` 与三个技能面板。
2. 复跑 §3 五条命令；确认 `presentation` 471 项、冒烟 29 张、target/zero PASS。
3. 打开 `responsive-app-runtime/index.html` 与 `responsive-validation/index.html` 逐张比对；重点看 960 名牌/目标标签、1024 上下环境、1600/2560 左右环境、1280 技能按钮与牌河提示。
4. 检查 `runtime-matrix.csv` 的 stage 与截图实际位置是否一致；检查 `ocr-evidence.txt` 的文本盒。
5. 检查 `responsive-app-runtime/smoke-results.txt` 的连续 resize 与最终单次提交。
6. 确认 `RESPONSIVE-APP-BASELINE.json` 未变、七张源 PNG 未改；注意 §7 的 `validation.json` 重写说明。
7. 如发现视觉差异，请直接在当前未提交工作区修正；不要恢复 `PreparationTableRenderer.kt`，不要从 HEAD 重置。
