# 正式 APP 菜单点击滑入与单机准备早开局修复

日期：2026-10-22
范围：仅正式 APP（`presentation` / `engine` / `server`）。未修改 `ui_demo`，未提交或推送 Git，未删除/放宽既有测试。

## 1. 用户反馈与结论

1. **菜单点击缺乏滑入感**：点击“联机对战”“千术秘籍”“游戏设置”等入口后，目标内容直接出现在终态，只有主菜单六项首次/返回入场有动画。
2. **单机准备阶段卡界面**：单机 1 真人 + 3 默认电脑，玩家点“不使用千术”后必须等满选择倒计时（默认 10 秒，部分配置 15 秒）才能进入牌桌。

两项均已按权威/表现层边界修复，并有真实 GL 冒烟与真实单机路径耗时记录。

## 2. 根因

### A. 菜单点击

- `MainMenuLayout` 只负责 MAIN 六项首次/返回入场的 96px / 420ms 位移。
- `MenuScreen.openOverlay`、`openLobbyFlow` 以及各子视图打开路径直接修改 `currentState`；`MainMenuView.activate` 同步执行回调，目标内容没有“从右滑入”的中间帧。
- `TCP_CONNECT_DIALOG`、`CREATE_ROOM_DIALOG`、`JOIN_ROOM_DIALOG`、`SECRETS_MODAL`、`SETTINGS_MODAL` 直接绘制终态；秘籍/设置的遮罩与面板也在同一次绘制中立即出现。

### B. 单机准备阶段

- `ShufflePreparationHandler.step` 在 `now < deadlineMillis` 时无条件 `Step.Waiting`，即使四个座位都已确认。
- 默认单机电脑没有启用统一 AI 千术预算，`ServerTableHost.maybeScheduleAi` 在准备阶段发现 `enabledCapabilityIds` 为空时直接 `return`，不提交任何决定。
- `CheatAiRegistry` 又会按 `enabledCapabilityIds` 过滤能力，连“skip”安全候选也不会进入异步 AI 结果，因此电脑永远不确认，只能等 deadline 兜底。

## 3. 修复

### A. 目标内容轻滑入

新增 `MenuContentMotion`（`presentation/menu/MenuContentMotion.kt`）：

```text
offset = 220 * (1 - easeOutCubic((now - start) / 280ms))
```

- 点击后第一次渲染从 **+220 逻辑像素**开始，**280ms** easeOutCubic 落位。
- 只平移目标内容：联机连接框、秘籍、设置、建房/输码加入子面板。
- 主菜单底图、章鱼、标题、全屏遮罩不移动；完整大厅/房间页仍保持既有整屏切换，不把主场景整体拖动。
- 秘籍/设置的全屏 0.75 遮罩单独绘制，只有弹窗面板滑入。
- **输入门控**：`offset != 0` 时目标指针/键盘输入被明确吞掉；落位后 `offset == 0`，绘制与命中重新使用同一终态坐标，不存在按钮/点击区错位或穿透。
- `motionEnabled = false` 时直接 `snap()` 到终态，立即可操作。
- 动画只在渲染帧启动/推进；状态切换后到首帧之间 offset 仍为 0，绘制与输入一致。
- 点击/键盘确认增加 140ms 激活高亮，只影响绘制；业务回调仍在 `activate` 内同步触发，不被动画延迟。
- 不重播主菜单六项入场；覆盖层关闭恢复焦点且 `entryPlayCount` 不变。
- ESC/返回在滑入期间仍然立即关闭目标内容，不被 280ms 动画拖延。

### B. 提前发牌

`ShufflePreparationHandler.step`：

- 仍在 deadline 前时，若 **四个座位全部已确认**，立即进入 `GENERATING` 并生成牌山，不再空等。
- 任一真人未确认时保持等待；原 deadline 仍是安全兜底。
- 早结束时在同一刻冻结原始方案与看破席位集合 K；费用仍按原始选择计算，有效方案仍按看破整效抵消生成。
- 只有一次 `executeDealWithPreparation`；重复 tick、迟到请求、超时 tick 不会二次扣费或二次发牌。

`ServerTableHost.maybeScheduleAi`：

- 准备阶段且权威投影 `enabledCapabilityIds` 为空（统一策略关闭、预算 0、托管禁止花费或能力全关）时，不再直接返回，而是为该自动化座位构造确定性 `AiRequestId` 的**零费用 skip**，交给既有 `TableAuthority.submitShufflePlan` 串行入队确认。
- 边界：只处理 `automatedSeats`；一个真人的 skip 绝不会代替其他真人确认。只要仍启用任一能力（包括统一策略开启且有预算），仍走异步 AI 策略，电脑可以合法施术。`aiMaySpend = false` 不会被简单解释为“所有 AI 都 skip”。
- 预算 0/禁用不再无意义等待；若能力启用了但当前可花点数不足，异步 AI 仍可合法选择 skip 或零费用能力，deadline 继续兜底。
- 请求沿用 stamp/epoch/round/回执幂等；重复 tick 安全。

### C. 准备 UI

- 本地受理后不再显示笼统“已确认”，而是：
  - 已提交未回执：`已提交，等待权威确认…`
  - 已确认但仍选择中：`已确认，等待其他玩家…`
  - 权威进入 GENERATING：`正在发牌，请稍候…`
- 失败回执恢复 `canConfirm` / `canSkip`，显示拒绝原因。
- 不本地关闭面板或假设已经发牌；遮罩只在权威 `preparation == null` 快照到达后解除。

## 4. 新增/调整的回归测试

### 先观察失败（修复前）

新增：

- `engine/src/test/kotlin/com/majiang/authority/ShufflePreparationEarlyFinishTest.kt`（4 项）
- `server/src/test/kotlin/com/majiang/server/host/PreparationEarlyDealContractTest.kt`（4 项）

修复前运行：

```powershell
.\gradlew.bat :engine:test --tests "com.majiang.authority.ShufflePreparationEarlyFinishTest" :server:test --tests "com.majiang.server.host.PreparationEarlyDealContractTest" --console=plain
```

结果：

- engine 2/3 失败（当时回归文件 3 项），典型信息：`all confirmed seats must deal early ==> expected: <true> but was: <false>`；最后一个真人确认后仍不能提前。
- server 4/4 失败，典型信息：`all seats confirmed must deal before the deadline ==> expected: <null> but was: <ShufflePreparationView(... status=SELECTING, deadlineMillis=10000, serverNowMillis=60, confirmed=true ...)>`。

### 修复后覆盖

engine：

1. 四个真人全部确认，deadline 前立即发牌；原方案 40/30/10/0 费用与看破集合冻结；136 张物理守恒；重复 tick 与迟到请求不重发/不重扣。
2. 三个真人确认、第四个未确认时不得提前；原 deadline 正常兜底。
3. 最后一个真人在 deadline 前确认后仍可提前。
4. 直接 `ShufflePreparationHandler.step` 早结束：原始方案（40/30/30/0）与看破集合 K 冻结，有效方案按看破抵消，牌山与同种子生成结果逐张一致且 136 张。

server（真实 `ServerTableHost` + 固定测试时钟 + 直接 AI executor）：

1. 1 真人 + 3 默认电脑，点 skip 后少量 tick 即 `preparation=null`、已发 14/13/13/13 张、四人 0 费用。
2. 一个真人 skip 不会确认其他真人；全真人确认后在 deadline 前提前结束。
3. AI 策略禁用 / 预算 0：电脑由权威零费用 skip，玩家 skip 后提前发牌。
4. 统一 AI 策略启用且预算 60：电脑仍异步选择 30 点方案，玩家 skip 后提前发牌，电脑各扣 30 点且只扣一次。

presentation：

- `MenuContentMotionTest`（3 项）：+220 起始、80ms 中间帧、280ms 落位、减少动画直接终态。
- `MenuTargetMotionContractTest`（6 项）：秘籍/设置/连接框滑入中间帧；动画期间点击终态按钮被吞；ESC 立即返回；`motionEnabled=false` 可直接操作；resize 后命中一致；重复点击只发一次连接请求；激活反馈不延迟业务回调。
- `ShufflePreparationUiAcceptanceTest` 新增 2 项：等待其他玩家/正在发牌反馈、失败回执恢复操作。

既有测试调整（未放宽断言）：

- `ServerTableHostCoverageTest` 的“ten second selection...”原先假定“四人已确认也要等 deadline”；按新契约改为：三人确认时 deadline 仍固定且不发牌，第四人确认后 deadline 前提前发牌，仍校验 14/13/13/13、53 张唯一手牌、20 秒储备 + 3 秒首决策。
- `MainMenuRenderSmoke` 在既有覆盖弹窗截图前先让 280ms 滑入落位，保留原“稳定底图 + 弹窗”像素断言语义；滑入本身由新冒烟单独覆盖。

## 5. 真实 GL 冒烟

新增：

- `tools/architecture/MenuClickPreparationSmoke.java`
- `tools/architecture/menu-click-preparation-smoke.init.gradle`

运行：

```powershell
.\gradlew.bat -I tools/architecture/menu-click-preparation-smoke.init.gradle :lwjgl3:menuClickPreparationSmoke --console=plain
```

输出：`snapshot/menu-click-preparation-smoke/`

截图：

- `tcp_connect_00ms.png` / `tcp_connect_80ms.png` / `tcp_connect_280ms.png`
- `secrets_00ms.png` / `secrets_80ms.png` / `secrets_280ms.png`
- `settings_00ms.png` / `settings_80ms.png` / `settings_280ms.png`
- `prep_before_skip.png` / `prep_after_skip.png`
- `smoke-results.txt`

真实结果（最终一次运行）：

```text
SLIDE tcp_connect targetOffsetsPx 0ms=220 80ms=80 280ms=0 backgroundDrift=0.00
SLIDE secrets    targetOffsetsPx 0ms=220 80ms=80 280ms=0 backgroundDrift=0.00
SLIDE settings   targetOffsetsPx 0ms=220 80ms=80 280ms=0 backgroundDrift=0.00
PREP skip-to-table wallClockMs=159.65
PREP preparation=null, panelActive=false, cheatPoints[0]=100, handTiles=14
PASS: menu slide displacement and real single-player early deal verified
```

- 目标位移由截图模板匹配/弹窗差异带测量，不是只读状态值；背景区域 0/280ms 漂移为 0.00，确认主场景没有随目标整屏移动。
- 单机路径是真实 `MahjongGame`：点击正式主菜单“单机对局” → 进入真实准备面板 → 点击“不使用千术” → 墙钟记录到权威 `preparation=null`、遮罩消失、牌桌可见。最终一次 **159.65ms**；连续三次完整冒烟分别为 **179.18ms / 161.72ms / 159.65ms**，均远小于 10 秒 deadline。
- `prep_after_skip.png` 为遮罩解除后的牌桌；`cheatPoints[0]=100` 证明 0 费用，`handTiles=14` 证明真实发牌。

既有 `MainMenuRenderSmoke` 也重新运行通过：

```powershell
.\gradlew.bat -I tools/architecture/main-menu-smoke.init.gradle :lwjgl3:mainMenuRenderSmoke --console=plain
```

## 6. 命令与结果

专项：

```powershell
.\gradlew.bat :engine:test --tests "com.majiang.authority.ShufflePreparationEarlyFinishTest" :server:test --tests "com.majiang.server.host.PreparationEarlyDealContractTest" --console=plain
.\gradlew.bat :presentation:test --tests "com.majiang.presentation.menu.*" --console=plain
.\gradlew.bat :presentation:test --tests "com.majiang.presentation.game.ShufflePreparationUiAcceptanceTest" --console=plain
```

结果：上述专项全部通过。

全量 engine + server：

```powershell
.\gradlew.bat :engine:test :server:test --console=plain
```

结果：engine 591 项、server 356 项，0 失败。

最终要求命令：

```powershell
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
```

结果：`BUILD SUCCESSFUL`；全仓 1766 项测试 0 失败；`:lwjgl3:classes` 成功；`:android:assembleDebug` 成功，APK 为 `android/build/outputs/apk/debug/android-debug.apk`（44,831,591 bytes）。构建过程中 Kotlin 编译 daemon 连接告警后回退到进程内编译，任务仍成功；不是测试或装配失败。

## 7. 未验证/边界

- 未在 Android 真机/模拟器上实测触控滑入、刘海安全区与低端设备帧率；本次真实 GL 验证为 Windows LWJGL3 1280×720。
- 本次实测正式单机默认 10 秒选择配置；15 秒配置走同一提前结束代码路径，但未单独计时。
- 未对截图做人工目视检查（当前模型不支持图像输入）；位移与背景稳定性由像素测量、状态和真实流程断言验证。
- 完整大厅/房间页未加入滑入，保持既有整屏切换；这是有意避免“主场景整屏移动”，不是遗漏。
- 准备面板本身的进入仍即时显示，未额外加滑入；避免对准备选择和权威推进增加任何门控。菜单目标内容的滑入与输入门控已按上述契约覆盖。
- 未提交、未推送 Git；未修改 `ui_demo`；未删除或放宽既有测试断言。
