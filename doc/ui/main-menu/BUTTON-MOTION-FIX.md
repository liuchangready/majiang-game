# 正式 APP 菜单点击反馈修正（按钮自身向右滑）

- 日期：2026-10-22
- 范围：正式主菜单（`presentation.menu`）与其单元测试 / GL 证据
- 基线：`3f5370c`（本任务开发期间另一个任务已提交 `e715b8c`，与本任务文件范围完全分离）

---

## 1. 用户新语义（本任务的唯一权威）

用户原话：

> 现在菜单点入的时候感觉像是**背景向左滑**，而不是**按钮向右滑**。
> 点击时按钮自身向右（正 X）滑，背景稳定。

展开为可验收条目：

| # | 语义 |
|---|------|
| S1 | 被点中的那一个菜单项，其**底板 / 图标 / 文字作为一个整体**向右（正 X）移动 |
| S2 | 背景、标题、副标题、章鱼吉祥物、其余五个按钮、全屏遮罩的**屏幕坐标保持不变** |
| S3 | 目标弹层（秘籍 / 设置 / 连接框）**固定位置**，只允许淡入，不得平移 |
| S4 | 相机位置与投影矩阵在点击前后**逐元素完全一致** |
| S5 | 点击后的业务回调**立即且只执行一次**，视觉反馈不得延迟请求/权威 tick |
| S6 | 首次入场的“从右入场序列”与点击反馈是**两个独立事件**，点击不得重播入场 |
| S7 | 位移期间该项**禁止再次激活**；落位后立即恢复可点 |
| S8 | `motionEnabled=false` 直接落到最终稳定位置，**无任何视觉等待** |
| S9 | 关闭 / 返回 / 暂停 / resize 后**没有残留位移或焦点错误** |

---

## 2. 旧实现的根因

旧实现（`3f5370c`）把“点击后出现的目标内容”做成从右滑入，具体是：

```kotlin
// MenuScreen.kt（已删除）
private inline fun withContentSlide(offsetX: Float, block: () -> Unit) {
    val priorX = camera.position.x
    camera.position.x = priorX - offsetX   // ← 相机整体左移
    camera.update()
    try { block() } finally { camera.position.x = priorX; camera.update() }
}
```

问题有三层：

1. **移动的主体错了**：被移动的是“目标弹层”，而且是通过平移**相机**实现的。
   由于相机是整个菜单的公共投影，被平移的是**该状态下所有由相机绘制的内容**，
   视觉上就表现为“背景/菜单整体向左滑”，而不是“按钮向右滑”。
2. **被点的按钮自身没有任何位移**：按钮只拿到 `MainMenuView.ACTIVATION_FLASH_MS = 140ms` 的高亮，
   底板/图标/文字完全没动，因此用户永远看不到“按钮向右”。
3. **位移与命中脱节**：位移由 `MenuContentMotion` 单独持有，`MenuScreen.touchDown`
   在 `isContentMotionActive` 时直接吞掉输入，靠“门控”避免落位前误触；
   旧相机平移壳也因此长期保留（`withContentSlide` / `slidingTargetStates` / `contentMotionOffsetX`）。

---

## 3. 代码修复

### 3.1 新增：被点按钮自身的位移模型

`presentation/src/main/kotlin/com/majiang/presentation/menu/MainMenuButtonMotion.kt`（新增，纯 Kotlin，无 GL）

| 参数 | 值 | 说明 |
|------|----|------|
| `MAX_OFFSET_X` | `56f` | 峰值正 X 位移（要求区间 48~64 逻辑像素） |
| `OUTBOUND_MS` | `160L` | 滑出时长（要求区间 140~180ms），easeOutCubic |
| `RETRACT_MS` | `140L` | 收回时长 |
| `TOTAL_MS` | `300L` | 单次点击反馈总时长（要求区间 260~320ms），easeInOutCubic（两端速度为 0，无跳变） |

- 位移只作用于**一个** `MainMenuItemId`，永远不会作用到背景或别的项。
- `motionEnabled=false`：`start()` 直接 `reset()`，无位移、不阻塞、无等待。
- `pause()/resume()` 冻结并整体平移时间轴，暂停期间 `update()` 不推进。

### 3.2 布局：绘制矩形与命中矩形同源

`MainMenuLayout.kt`

- 新增 `MainMenuButtonOffset(itemId, offsetX, blocking)`，作为“点击反馈位移”的唯一载体。
- `MainMenuLayout.frame(elapsedMs, motionEnabled, buttonOffset)`：位移**只叠加到被点项**，
  并按 `maxButtonOffsetX(id) = 1280 - (baseX + width)` 夹紧，右边界永不越过 1280 设计宽度。
- `MainMenuItemFrame` 新增 `motionOffsetX` / `motionActive`。
- `MainMenuFrame.hitTest` 排除 `motionActive` 的项：
  位移中的按钮**不接受命中**，起点空位也不可命中 → 不会出现“命中终点空白 / 重复触发”。

### 3.3 主菜单视图：鼠标 / 触控 / 键盘共用同一份状态

`MainMenuView.kt`

- 持有唯一 `MainMenuButtonMotion`；`update()` 推进它，`applyElapsed()` 把位移交给布局。
- `activate(id)`：
  1. `item.motionActive` → 直接拒绝（位移期间禁止重复激活）；
  2. 记录 140ms 高亮（只影响绘制）；
  3. `buttonMotion.start(id)`；
  4. **同步**执行原业务回调（不等待动画）。
- `beginEntry()` 会 `reset()` 位移（重新入场不允许残留）。
- `onPause()/onResume()` 冻结/续播位移；新增 `resetButtonMotion()` 供父层在关闭覆盖层、隐藏时清零。
- **按下（pointerDown）只给高亮与压暗，不做几何位移**：
  这样绘制矩形与命中矩形永远相等，释放 / 拖出 / 取消语义完全准确
  （拖出即取消，不会出现“按钮滑走导致松手落空”）。
- **删除 `MainMenuView.handleTouch(tx, ty)`**（旧“按下即激活”的兼容壳，全仓无调用者）：
  该语义与“释放 / 拖出 / 取消必须正确”直接冲突，保留只会留下误用入口。

### 3.4 菜单场景：删除相机平移，目标层固定位置

`MenuScreen.kt`

- **删除** `withContentSlide`、`slidingTargetStates`、`contentMotion`、`contentMotionState`、
  `contentMotionOffsetX`、`isContentMotionActive`、`syncContentMotion`；
  **删除文件** `MenuContentMotion.kt`（不留无用 camera 兼容壳）。
- 目标弹层改为**固定位置淡入**：新增 `MenuOverlayReveal`（140ms，仅改 alpha）。
  遮罩 alpha 0 → 0.75 平滑淡入，因此点击后被点按钮的右移在遮罩压暗之前是**肉眼可见**的。
- 覆盖层面板改为完全不透明（alpha 1.0），用于证明“目标层一个像素都没动”。
- `closeOverlay()` / `hide()` 会清零点击位移 → 无残留位移。
- 输入不再因视觉反馈被门控（`touchDown/touchUp/touchDragged/keyTyped` 中的
  `isContentMotionActive` 分支全部移除）。
- 新增诊断读口（仅供 GL 冒烟）：
  `cameraStateForDiagnostics()`（相机位置 + 投影矩阵 18 个 float）、
  `overlayRevealAlpha`。

### 3.5 测试与证据

- 删除 `MenuContentMotionTest.kt`（旧目标层位移模型已不存在）。
- 删除 `MenuTargetMotionContractTest.kt`，改写为 `MenuButtonMotionContractTest.kt`：
  保留旧用例中仍有价值的断言（重复提交只发一次请求、ESC 返回、resize 命中对齐、
  回调即时性），并按新语义补充（正 X 位移、未点项不动、位移中禁止重复激活、
  拖出取消、减少动画立即稳定、相机与投影不变、无残留位移与焦点错误）。
- 新增 `MainMenuButtonMotionTest.kt`（纯模型 9 项）。
- 更新既有断言：`MainMenuScreenContractTest`（重试前先推进时间轴）、
  `MenuScreenSubViewsContractTest`（再次打开弹窗前先结束位移）、
  `GameScreenSkinSmokeTest`（两次换肤之间先结束位移）。

---

## 4. 实证（真实命令与真实结果）

### 4.1 菜单专项单元测试

```
.\gradlew.bat :presentation:test --tests "com.majiang.presentation.menu.*"
```

结果：**61 项菜单用例全绿**（`MainMenuButtonMotionTest` 9、`MainMenuLayoutTest` 6、
`MainMenuScreenContractTest` 15、`MenuButtonMotionContractTest` 12、
`MenuScreenSubViewsContractTest` 5、`MenuScreenTest` 14）。

presentation 全量：

```
.\gradlew.bat :presentation:test
```

结果：**458 项，0 失败 0 错误**（含另一个任务新加入的准备阶段用例）。

### 4.2 桌面构建

```
.\gradlew.bat :lwjgl3:classes
```

结果：**BUILD SUCCESSFUL**。

### 4.3 真实 GL 冒烟（新增，证据目录 `snapshot/menu-button-motion-smoke`）

```
.\gradlew.bat -I tools/architecture/menu-button-motion-smoke.init.gradle :lwjgl3:menuButtonMotionSmoke
```

真实窗口 1280x720、真实纹理、真实输入路径（skin 项不切页面）。日志
`snapshot/menu-button-motion-smoke/smoke-results.txt` 原文：

```
MOTION skin plate/icon shiftPx 0ms=0 80ms=49 160ms=56 300ms=0
STATIC 80ms drift title=0.00 subtitle=0.00 mascot=0.00 neighbour=0.00 background=0.00
STATIC 80ms button region drift=193.93
CAMERA click: position+projection unchanged (18 floats)
CAMERA overlay: position+projection unchanged (18 floats)
OVERLAY panel maxAbsDiff 0ms=0.0 80ms=0.0
OVERLAY dimmer region meanAbsDiff=125.62 revealAlpha 0ms=0.0 80ms=0.606414
OVERLAY settings focus-ring shiftPx=49 (dimmerFactor=0.55, scale=1.83)
RESIZE 960x540: click aligned, motion offset=49.0
PASS: menu button motion verified (button slides right, background stays, camera never shifts)
```

逐条对应 S1~S9：

| 证据 | 含义 |
|------|------|
| `shiftPx 0ms=0 80ms=49 160ms=56 300ms=0` | **像素实测**：皮肤项底板+图标整体右移 49px（80ms）、56px（160ms），300ms 回到原位 |
| `drift title=0.00 subtitle=0.00 mascot=0.00 neighbour=0.00 background=0.00` | 标题 / 副标题 / 章鱼 / 邻近入口 / 背景与基准帧**逐像素完全相同** |
| `button region drift=193.93` | 对照组：按钮区域确实变了（不是整屏没动） |
| `CAMERA click / overlay: position+projection unchanged (18 floats)` | 点击与开弹层前后相机位置与投影矩阵**逐元素完全一致** |
| `OVERLAY panel maxAbsDiff 0ms=0.0 80ms=0.0` | 目标弹层面板**一个像素都没动**（固定位置） |
| `OVERLAY settings focus-ring shiftPx=49` | 覆盖层下被点的设置入口同样右移 49px（遮罩亮度归一化后模板匹配实测） |
| `RESIZE 960x540 ... motion offset=49.0` | resize 后命中与绘制仍对齐，位移照常 |

位移量是**用截图像素测出来的**（模板匹配 + 亮度归一化），不是读内部 offset 数值。
另做了一次独立复核：直接按原始像素找金色焦点框左缘，
`0ms→753px、80ms→802px(+49)、160ms→809px(+56)、300ms→753px`，与上面结论一致。

截图清单（`snapshot/menu-button-motion-smoke/`）：

| 文件 | 内容 |
|------|------|
| `01_skin_settled_reference.png` | 点击前基准帧（hover+focus 在皮肤项） |
| `02_skin_click_000ms.png` | 释放后 0ms：位移为 0 |
| `03_skin_click_080ms.png` | 释放后 80ms：按钮右移中 |
| `04_skin_click_160ms.png` | 释放后 160ms：位移峰值 |
| `05_skin_click_300ms.png` | 释放后 300ms：回到原位 |
| `06_settings_open_000ms.png` | 设置弹层刚打开（遮罩 alpha=0，面板固定位置） |
| `07_settings_open_080ms.png` | 80ms（遮罩淡入中，被点入口已右移） |
| `08_settings_settled.png` | 遮罩淡入完成 |
| `09_resized_960x540.png` / `10_resized_960x540_moved.png` | resize 后仍然对齐且位移照常 |

### 4.4 既有 GL 冒烟（同步改为新语义）

```
.\gradlew.bat -I tools/architecture/main-menu-smoke.init.gradle :lwjgl3:mainMenuRenderSmoke
.\gradlew.bat -I tools/architecture/menu-click-preparation-smoke.init.gradle :lwjgl3:menuClickPreparationSmoke
```

三个 GL 冒烟的进程退出码均为 **0**（`menuButtonMotionSmoke` / `mainMenuRenderSmoke` /
`menuClickPreparationSmoke`），原始控制台输出分别留在
`snapshot/<目录>/run.log`。

- `MainMenuRenderSmoke`：PASS（六项回调、960x540 / 19.5:9 输入对齐、减少动画立即可用）。
- `MenuClickPreparationSmoke`：**旧的目标层 +220px 位移断言已删除**，改为断言
  “相机位置/投影不变 + 目标面板三帧亮度一致”：

  ```
  FIXED tcp_connect cameraShift=false (position+projection identical)
  FIXED secrets     cameraShift=false (position+projection identical)
  FIXED settings    cameraShift=false (position+projection identical)
  PREP skip-to-table wallClockMs=180.62
  PASS: menu target stays fixed (no camera shift) and real single-player early deal verified
  ```

  真实 MahjongGame 单机路径仍然即时（180.62ms 跳过码牌进牌桌），说明视觉反馈没有拖慢权威流程。

---

## 5. 行为变更与取舍（需要知会）

1. **位移期间该项不可再次激活（最长 300ms）**：这是需求 S7 的直接结果，
   目的是杜绝重复提交与“命中终点空白”。副作用是**快速双击同一个入口只生效一次**
   （例如连点两次“牌面皮肤”只会切换一次）。相关既有测试已按新语义调整
   （`GameScreenSkinSmokeTest`、`MainMenuScreenContractTest`、`MenuScreenSubViewsContractTest`）。
2. **按下只有高亮、没有几何位移**：为了保证“绘制矩形 == 命中矩形”以及
   释放 / 拖出 / 取消语义绝对准确，按下反馈采用压暗 + 金色焦点框，不移动几何。
3. **覆盖层面板改为完全不透明（0.98 → 1.0）**：视觉差异不可察觉，
   但换来了“目标层零位移”的逐像素证据（`maxAbsDiff = 0.0`）。
4. **目标弹层不再有横向入场**：改为 140ms 淡入 + 遮罩淡入；
   秘籍 / 设置面板本身固定位置，连接框同样固定位置。

---

## 6. 未验证项 / 需 Codex 复核

- **未做**：Android 真机触控实测（本任务范围外）、引擎与协议模块的重复验证、
  全项目 `test` 全量（按分工由 Codex 最终统一跑）。
- **未做**：真机上的实际观感评审（60fps 真机帧率下 300ms 反馈是否合适），
  建议在桌面 `:lwjgl3:run` 里用真实鼠标点一次“牌面皮肤”确认手感。
- **需复核**：第 5 节第 1 条“位移期间禁止再次激活”对手感的实际影响，
  是否需要把锁定期缩短到仅滑出段（160ms）。
- **需复核**：覆盖层面板由 0.98 改为 1.0 是否影响美术验收。
- **并发说明**：本任务未触碰另一个任务的 `GameScreen` / `ShufflePreparation*` /
  `RoundPreparation*` 等文件（其改动已由对方提交为 `e715b8c`），未回滚任何内容。
- 本任务**未执行 git 提交 / 推送**。
