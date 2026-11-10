# 疯狂动物麻将 · 正式 APP 主菜单交付记录

- 工作区：`D:\majiang`
- 基线 HEAD：`d8dc01dd56ee4f45dd372dc1f9085eff45aadaed`
- 模型：当前默认模型（未切换）
- Git：未提交、未推送
- 第 2 轮返工：修复真实 GL `GL_BLEND` 生命周期导致的 1280 内容区黑屏/不透明光晕/弹窗底图消失；冒烟新增像素级断言并重新生成全部截图。
- 第 3 轮返工：修复单机 `createRoom`/`start` 失败或拒绝后本地会话未 detach/close 的缺陷；新增 4 项失败回收/迟到回调测试，联机入口恢复 TCP 弹窗，重试成功保留新会话。
- 第 4 轮返工：修复 `openLocalLobby` 存活检查与所有权发布/attach 之间的 dispose 竞态；存活检查、发布、attach、完成回收与 dispose 统一在同一同步临界区，新增 3 项交错回归测试。
- 设计依据：[DESIGN.md](DESIGN.md)、[LAYOUT-1280.svg](LAYOUT-1280.svg)、[manifest.json](../../../assets/ui/main-menu/manifest.json)

## 1. 实施结果

正式 libGDX Kotlin 主菜单已接入真实 APP 运行链，不是 HTML Demo，也不依赖 `ui_demo`。

### 1.1 图层

| 层 | 实现 |
| --- | --- |
| L0 场景 | `club-background.png` 按真实窗口尺寸 cover，独立于内容 `FitViewport`；1280×720、960×540、1560×720（19.5:9）均铺满，超宽边缘由场景填充。 |
| L1 阅读遮罩 | ShapeRenderer 左右渐暗 + 上下边缘暗角，不全屏压黑。 |
| L2 环境光 | 左侧青绿背光、右侧金色柔光，低透明静态/极缓慢变化。 |
| L3 吉祥物 | `octopus-hero.png` 在 `(96,64,530,506)` 内等比 contain，完整透明通道；仅装饰，无角色能力/选角/货币信息。 |
| L4 标题 | “疯狂动物麻将”使用从 NotoSansSC-Regular 重新生成的 64px 增量字库，不是把 18px 字库放大；副标“三大千术 · 牌桌心战”使用 21px 专用字库。 |
| L5 六入口 | 朱红 `button-primary.png` / 翡翠 `button-secondary.png` 九宫格（18px 四边）、`button-focus.png` 焦点层、`menu-icons.png` 六格 64px 图标、24px/21px 动态中文。 |
| L6 状态 | 固定 `(752,58,440,38)` 提示条；成功/错误带“提示：/错误：”文字前缀，不只依赖颜色；皮肤项显示当前皮肤中文名。 |
| L7 弹窗 | 秘籍/设置打开时先画稳定 MAIN 底图，再画遮罩与弹窗；父层状态独占输入，关闭后恢复原焦点且不重播入场。 |

### 1.2 绘制状态与资源所有权

- ShapeRenderer、SpriteBatch 均成段 `begin/end`，透明图元启用 `GL_BLEND` 标准 alpha 混合。
- `MainMenuView` 绘制前后保存并恢复 batch color/projection、各字体 color/scale，避免污染大厅和弹窗。
- 新增 `presentation/menu/MainMenuAssets.kt`，由 `UiAssets` 根对象延迟创建并统一 `dispose`；View/Screen 只借用。
- `UiAssets` 新增 64/24/21/16px 主菜单字体并统一释放；同一字体实例按 identity 去重，不重复 dispose。
- 主菜单纹理首次真正渲染 MAIN 时加载一次；无头/无 GL 时返回 null，不强制创建 GL 资源；加载失败只记一次日志并走可见纯色降级。
- 未进入 MAIN 的测试路径不创建菜单纹理；菜单纹理不会每帧加载。

#### GL_BLEND 生命周期修正（第 2 轮阻断返工）

独立目视复验发现 1280 内容区背景几乎全黑、L1/L2 变成不透明绿/金圆盘、L7 弹窗后底图消失。根因是 libGDX `SpriteBatch.end()` 会关闭 `GL_BLEND`，而后续 ShapeRenderer 没有重新开启：

- L0 `renderBackdrop()` 的 `SpriteBatch.end()` 关闭混合；
- `MainMenuView` 的 L1/L2 ShapeRenderer、`renderModalBase` 的 0.75 黑色遮罩随后以不透明方式绘制；
- 超宽左右边栏没有走内容遮罩，所以仍能看到背景，进一步确认了状态泄漏。

修复方式：新增 `enableMenuAlphaBlending()`（`GL_BLEND` + `GL_FUNC_ADD` + `SRC_ALPHA/ONE_MINUS_SRC_ALPHA`），并自包含地在每个 ShapeRenderer 绘制段前调用：

- `MenuScreen.render` 帧首；
- `renderBackdrop()` 之后（SpriteBatch 刚关闭混合）；
- `renderBackground()`、`renderModalBase()`；
- `MainMenuView.render()` 的 L1/L2 ShapeRenderer 之前。

所有菜单子视图仍按“ShapeRenderer → SpriteBatch”顺序绘制；每个 Shape 段自行恢复混合，不依赖上一段 SpriteBatch，也不要求其他子视图改代码。像素断言会把该缺陷直接判失败，不能只靠 PASS 字样。

### 1.3 入场与命中

- 六项最终矩形严格来自 manifest：单机/联机 `440×80`，皮肤/秘籍/设置/退出 `440×58`。
- 每项从最终位置右侧 `96` 单位移入，延迟 `120/200/280/360/440/520ms`，各 `420ms` easeOutCubic，总计 `940ms`。
- 绘制与命中读取同一个 `MainMenuFrame`；`settled` 前不进入 hit 列表，因此移动中的最终矩形不会误触。
- 各入口独立落位：540ms 单机可点；620/700/780/860/940ms 依次启用联机/皮肤/秘籍/设置/退出。
- `motionEnabled=false` 时直接进入最终落位态，立即可操作；这是可注入表现层参数，未冒充已有系统设置。
- 公开的 `btnPlaySingle` 等矩形保留为最终落位矩形；当前帧矩形通过 `currentFrame` / `itemRect(id)` 读取。

### 1.4 输入、重入与门禁

- 鼠标 hover、触控 down/up、拖动取消、键盘 Tab/↑/↓/Enter/Space 共用同一入口模型与落位门禁。
- hover 只改变描边/底板亮度，pressed 只压暗底板，不平移几何；focus 使用金色九宫格焦点层。
- 首次 `show()` 播放入场；LOBBY 返回 MAIN 重播一次；对局结束回到新 `MenuScreen` 首次 show 播放；后台 `resume()`、皮肤/状态文字刷新、覆盖弹窗开关均不重播。
- 覆盖弹窗打开时记录焦点，关闭后恢复；点击弹窗关闭按钮或 Esc 都不会穿透到底图菜单。
- 六个原有回调保持不变；单机启动增加 `singleStartInFlight` 门禁，启动中禁用单机/联机入口，失败后在 UI 线程恢复入口并显示原因。
- 未把网络、会话或领域逻辑放入 `MainMenuView`；`MenuScreen` 继续独占 `LobbySession` 异步、requestGeneration 与代次守卫。

#### 单机启动失败回收（第 3 轮）

独立代码审查发现：`openLocalLobby()` 成功后立即 `attachLobby`，若 `createRoom()`/`start()` 失败或拒绝，原实现只清忙碌标记并显示错误，`activeLobby` 仍指向失败的本地会话。后续点击“联机对战”会误入本地 LOBBY，重试也会留下旧会话。

修复：

- 用 `AtomicReference` 保存本次 `targetLobby`，用 `singleStartLobby` 标记尚未成功交付的会话；
- `openLocalLobby` 完成时先校验 `isDisposed`/`requestGeneration`，迟到结果直接关闭，不 attach；
- `createRoom` 成功时 `attachLobby` 并记录 seat/role；`start()` 前再次校验代次；
- 失败/拒绝且本会话仍是 `activeLobby` 时，先 `detachLobby()` 再 `close()`，并恢复 `myRole=NONE`、`myViewerSeat=null`、`hasNavigatedToTable=false`、房间/准备/房主状态；
- 若会话已被新一代请求替换，只关闭旧目标，绝不按 `activeLobby` 盲关新会话；
- 成功路径清除 `singleStartLobby` 标记并保留 `activeLobby`；`dispose()` 只关闭尚未成功交付的待处理会话；
- 迟到回调不导航、不改新一代 UI，但会关闭自己创建且未被替换的会话。

#### dispose/异步完成竞态（第 4 轮）

独立复验指出：`thenCompose` 中先检查 `isDisposed`/`requestGeneration`，再发布 `targetLobbyRef`/`singleStartLobby` 并 `attachLobby`；若 `dispose()` 恰好在两者之间执行，`dispose()` 读不到 pending 会话并完成，随后回调仍可 attach 已废弃的 Screen，而 `finishSingleStart` 又会被 `runOnUi` 的 dispose 门禁丢弃，造成 lobby 永不关闭。

修复：

- 新增 `singleStartLock`，把 `isDisposed`/`requestGeneration` 存活检查、`targetLobbyRef`/`singleStartLobby` 发布、`attachLobby`、attach 后的二次失效检查放在同一同步临界区；
- `dispose()` 在同一临界区设置 disposed、递增代次、取出并清空 pending 会话、`detachLobby()`；退出临界区后关闭本地会话；
- `createRoom`/`start` 的结果回收、`finishSingleStart` 的失败清理/成功交付也在同一临界区；成功后清除 pending 标记并保留 activeLobby；
- 迟到结果若发现 disposed 或代次过期，直接关闭本轮创建的 lobby，不 attach、不导航；若 activeLobby 已被替换，只关闭旧对象；
- `dispose` 期间若 attach 回调重入触发，attach 返回后会再次检查 disposed 并撤销订阅。

新增 3 项交错测试：结果先到但 `createRoom` 未完成时 dispose；attach 回调内恰好 dispose；旧代次结果不关闭替换后的新会话。

### 1.5 视口、resize 与平台

- `MenuScreen` 使用统一 `FitViewport(1280×720)`，`resize()` 更新视口，输入统一 `viewport.unproject`。
- L0 独立按真实屏幕 cover；内容始终保持在 16:9 安全区。
- 960×540：内容填满，绘制/命中一致。
- 1560×720（19.5:9）：内容视口 1280×720，居中 `x=140`，两侧由场景填充；绘制/命中一致。
- 1280×720：内容视口 1280×720。
- Android 安全边距通过 `Gdx.graphics.safeInsetLeft/Top/Right/Bottom` 平移/收缩视口，不引入 Android SDK 到 presentation；真机/模拟器尚未实测。
- 桌面窗口名、Snapshot 窗口名、Android `android:label` 已统一为“疯狂动物麻将”。

## 2. 文件清单

新增：

- `presentation/src/main/kotlin/com/majiang/presentation/menu/MainMenuLayout.kt`
- `presentation/src/main/kotlin/com/majiang/presentation/menu/MainMenuAssets.kt`
- `presentation/src/test/kotlin/com/majiang/presentation/menu/MainMenuLayoutTest.kt`
- `presentation/src/test/kotlin/com/majiang/presentation/menu/MainMenuScreenContractTest.kt`
- `tools/architecture/MainMenuRenderSmoke.java`
- `tools/architecture/main-menu-smoke.init.gradle`
- `doc/ui/main-menu/DELIVERY.md`
- `snapshot/main-menu-smoke/`（GL 实测截图与 `smoke-results.txt`）

修改：

- `presentation/src/main/kotlin/com/majiang/presentation/menu/MainMenuView.kt`
- `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt`
- `presentation/src/main/kotlin/com/majiang/presentation/common/UiAssets.kt`
- `lwjgl3/src/main/kotlin/com/majiang/desktop/DesktopLauncher.kt`（仅窗口名）
- `lwjgl3/src/main/kotlin/com/majiang/desktop/SnapshotLauncher.kt`（仅窗口名；快照阶段顺序未改）
- `android/AndroidManifest.xml`（仅 label）
- 既有菜单/皮肤测试的点击坐标适配（未删除或放宽断言）

未修改：`ui_demo/`、引擎/协议/客户端/服务端规则、`AGENTS.md`、`assets/ui/` 图像、`doc/ui/` 设计图、`tools/ui-assets/` 生成脚本、原有快照 PNG（运行 SnapshotLauncher 验证后已恢复 HEAD 版本）。

## 3. 验证命令与真实结果

### 3.1 新增测试

```text
.\gradlew.bat :presentation:test --tests "com.majiang.presentation.menu.MainMenuLayoutTest" --tests "com.majiang.presentation.menu.MainMenuScreenContractTest" --console=plain
```

结果：21 项通过，0 失败。覆盖：

- manifest 六矩形/安全区/延迟/420ms/940ms/96 偏移；
- 0/240/540/620/700/780/860/940ms 落位与独立启用；
- 移动中最终矩形不命中、outside 不命中；
- 减少动画立即落位；
- 每帧布局与命中一致性；
- LOBBY→MAIN 重播一次、重复 show 不重播、resume 不重播、覆盖层开关不重播、焦点恢复；
- 大厅返回双击只执行一次 leave 且只重播一次；
- 覆盖层输入门禁；
- 960×540 / 2340×1080 视口输入与绘制一致；
- 单机启动重复提交门禁、失败恢复、错误提示；
- `openLocalLobby()` 成功 + `createRoom()` 失败：会话 close、activeLobby 清空、角色/席位/导航复位、入口恢复、联机回到 TCP 弹窗、成功重试；
- `createRoom()` 成功 + `start()` 异常：同一套回收断言与成功重试；
- `createRoom()` 成功 + `start()` 返回 REJECTED：同一套回收断言与成功重试；
- dispose 后迟到的 `openLocalLobby` 结果被关闭且不 attach；
- attach 已完成但 `createRoom` 未完成时 dispose：会话关闭，迟到结果不重开、不双关；
- attach 回调内恰好 dispose：会话关闭、activeLobby 不残留；
- 旧代次结果到达时只关闭自己的 lobby，不关闭替换后的新会话；
- hover/pressed/focus、Tab/↑/↓/Enter/Space。

### 3.2 全部 presentation 测试

```text
.\gradlew.bat :presentation:test --console=plain
```

结果：`374 tests, 0 failures`（`presentation/build/test-results/test/` 汇总）。

### 3.3 独立桌面 GL 主菜单冒烟

```text
.\gradlew.bat -I tools/architecture/main-menu-smoke.init.gradle :lwjgl3:mainMenuRenderSmoke --console=plain
```

结果：`SMOKE_EXIT=0`，`PASS: all MainMenuRenderSmoke checks completed`，真实 Lwjgl3 窗口、真实纹理与输入。日志：`snapshot/main-menu-smoke/smoke-results.txt`。本轮对 14 张截图逐张加入像素断言（背景亮度/细节色数、光晕原始色像素、覆盖层下底图可见性），失败会直接中止冒烟，不能只靠 PASS 字样通过。

截图（均为实际运行截图，不是概念图）：

| 文件 | 状态 |
| --- | --- |
| `snapshot/main-menu-smoke/01_entry_000ms.png` | 0ms，六项在 +96、alpha 0；右侧无面板 |
| `snapshot/main-menu-smoke/02_entry_240ms.png` | 240ms，单机/联机滑入中；点击最终单机矩形被拒绝 |
| `snapshot/main-menu-smoke/03_entry_540ms.png` | 540ms，单机已落位；联机仍禁用 |
| `snapshot/main-menu-smoke/04_entry_940ms.png` | 940ms，六项全部落位；1280 内容区背景可见，光晕低透明 |
| `snapshot/main-menu-smoke/05_focus_online.png` | 键盘焦点在联机 |
| `snapshot/main-menu-smoke/06_secrets_overlay.png` | 秘籍覆盖弹窗，0.75 遮罩下菜单底图仍可见 |
| `snapshot/main-menu-smoke/07_settings_overlay.png` | 设置覆盖弹窗，底图仍可见 |
| `snapshot/main-menu-smoke/08_skin_updated_1280.png` | 皮肤即点即变（1280×720） |
| `snapshot/main-menu-smoke/09_960x540.png` | 960×540 绘制 |
| `snapshot/main-menu-smoke/10_960x540_skin_updated.png` | 960×540 下点击皮肤后即时更新 |
| `snapshot/main-menu-smoke/11_ultrawide_1560x720.png` | 19.5:9；内容视口 1280×720 居中 |
| `snapshot/main-menu-smoke/12_ultrawide_settings.png` | 19.5:9 下点击设置并显示覆盖层 |
| `snapshot/main-menu-smoke/13_reentry_240ms.png` | 六入口验证后显式重播 MAIN 入场 240ms，且只重播一次 |
| `snapshot/main-menu-smoke/14_reduced_motion.png` | 减少动画：立即终态、立即可操作 |

补充像素核验（冒烟内自动断言，避免仅凭文件名判断）：

- 04 图 1280 内容区背景采样 `(690,500,55×120)`：`avgLuma=87.7`、`uniqueColors=1475`；旧缺陷时该区因不透明遮罩接近黑屏，现在能读到场景细节。
- 04 图光晕原始色像素：mascot 区 `rawGreen=0`，右侧暗部 `rawGold=0`；L2 不再输出不透明 `#267A5C` / `#F5BD4F` 圆盘。
- 06/07 覆盖层：左侧底图区 `avgLuma=28.3`、`uniqueColors=1481`；右侧菜单底板区 `avgLuma=18.6`、`uniqueColors=136`，确认 0.75 遮罩下仍非纯黑。
- 12 超宽覆盖层：内容左侧 `avgLuma=21.4`、`uniqueColors=1492`；内容菜单底板 `avgLuma=18.6`、`uniqueColors=80`，确认超宽下 L7 也没有丢底图。
- 960×540 背景区 `avgLuma=85.8`；19.5:9 内容区 `avgLuma=52.3`、两侧边栏 `avgLuma=40.0`，均能读到场景像素，不是黑边。
- 04 图标题区金色字形 bbox `(9,7)-(384,64)`，宽 375px，确认 64px 标题字库真实绘制；副标字形 bbox 197×18。
- 04 图单机/联机面板主色约 `#9E4A31`，皮肤/秘籍/设置/退出约 `#3B543E`，状态条约 `#2F2520`。
- 04 与 05 差异 bbox 覆盖单机/联机范围，确认焦点层真实重绘；04 与 06/07 差异覆盖全屏，确认弹窗真实重绘。
- 960×540 与 ultrawide 的点击落点由 `screenPoint` 实测命中。

### 3.4 既有业务快照回归

```text
.\gradlew.bat :lwjgl3:snapshot --console=plain
```

结果：BUILD SUCCESSFUL，主菜单→秘籍→设置→建房→码牌→牌桌顺序正常完成。验证后已将 `snapshot/01..06_*.png` 恢复为 HEAD 版本，避免改动画像文件。

### 3.5 用户指定总验证

```text
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
```

结果：`BUILD SUCCESSFUL`（exit 0，87 actionable tasks）。测试汇总：

- engine 587
- protocol 199
- client 235
- server 352
- presentation 374
- 合计 1747，0 失败

Android 调试 APK：`android/build/outputs/apk/debug/android-debug.apk`（44,828,592 bytes，最终复跑产物）。

Android 构建出现既有 D8/Kotlin metadata 警告（R8 8.9.35 无法完全解析 Kotlin 2.4.10 metadata），但 dex/打包成功；未将其写成无警告通过。

## 4. 未验证 / 明确缺口

1. **Android 真机/模拟器未实测**：没有可用的真机或模拟器运行记录，因此刘海/挖孔、系统导航栏安全边距、触控命中、APK 实际启动、GLES2 纹理/字体加载没有实测数据。代码已按 `safeInset*` 处理并成功产出 APK，但不能宣称真机通过。
2. **图像目视检查受限**：当前默认模型不支持图像输入，`read_image` 对 `MAIN-MENU-EXPECTED.png`、背景、章鱼、按钮素材返回“model does not declare image input”。本轮已对 1280/960/19.5:9/覆盖层截图加入背景亮度、细节色数、光晕原始色像素和遮罩下底图可见性的自动断言，能阻断本轮黑屏/不透明光晕/底图消失缺陷；仍无法用肉眼判断美术观感，留给 Codex 独立目视复验。
3. **性能未测量**：未记录菜单纹理加载耗时、首帧耗时、低端 Android 帧率。设计素材说明中的 12.2 MiB 只是保守估算，本交付不宣称实测通过。
4. **Android 安全区实机几何**：仅验证了代码路径与构建，无 19.5:9 真机/模拟器截图。
5. **真实桌面鼠标/触控硬件事件**：GL 冒烟通过 `MenuScreen.touchDown/touchUp` 与视口坐标注入，键盘通过 `keyDown` 注入；Lwjgl3 窗口真实创建并渲染，但未用物理鼠标逐像素人工点击。

## 5. 设计与素材状态

- `assets/ui/main-menu/manifest.json`：仅将 `status` 更新为运行接入状态，坐标/时序/路径约束未改。
- `assets/ui/main-menu/README.md`：仅更新“已接入运行链”状态与交付链接，素材清单未改。
- `DESIGN.md`：仅追加/更新实施状态，L0–L7、坐标、时序、输入与资源约束未删改。
- `tools/ui-assets/`：未修改。
- `tools/architecture/`：仅新增本轮独立冒烟脚本，与既有架构冒烟和素材生成脚本分离。
