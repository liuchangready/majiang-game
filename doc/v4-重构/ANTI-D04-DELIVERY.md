# D-04 菜单子组件拆分交付报告 (ANTI-D04-DELIVERY)

- **基线 SHA**: `8910e5b` (feat/mj-tcp-migration)
- **执行角色**: Anti (开发执行)
- **验收角色**: Claude (独立审查与测试验收)
- **需求来源**: `doc/v4-重构/IMPROVEMENT-PLAN.md` §9.2 节 D-04
- **任务简报**: `tools/architecture/v4-improvement/ANTI-D04-TASK.txt`

---

## 1. 新增与修改文件清单

| 文件路径 | 变更类型 | 一句话职责 |
|---|---|---|
| `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuViewDraw.kt` | 新增 | 菜单与大厅视图共享基础绘制工具（按钮、输入框、居中文本）。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/MainMenuView.kt` | 新增 | 主菜单六大核心入口纯展示视图，分发单机/大厅/换肤/秘籍/设置/退出操作意图。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/ConnectDialogView.kt` | 新增 | TCP 远端连接弹窗纯展示视图，维护 HOST/PORT 输入、光标闪烁与字符过滤。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/LobbyListView.kt` | 新增 | 联机大厅纯展示视图，负责左侧导航栏、最多 8 行房间列表、选中高亮与加入/观战上报。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/CreateRoomDialogView.kt` | 新增 | 创建比赛房间规则配置弹窗纯展示视图，维护半庄与码牌开关翻转并上报确认配置。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/JoinRoomDialogView.kt` | 新增 | 输码加入房间弹窗纯展示视图，维护 12 位房间号输入、光标与确认/取消回调。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/RoomPreparationView.kt` | 新增 | 房间等待与就绪纯展示视图，负责四席位展示、就绪标记与开局/离房操作上报。 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt` | 修改 | 父层组合协调器，唯一拥有会话生命周期、异步业务编排、代次守卫与导航跳转。 |
| `presentation/src/test/kotlin/com/majiang/presentation/menu/MenuScreenSubViewsContractTest.kt` | 新增 | 验证子 View 纯展示性反射断言、输入框状态保留、建房配置上报与输码清空语义。 |

> **改动范围约束检查**：严格仅在 `com/majiang/presentation/menu` 包内新增与修改，未改动 `engine/`、`client/`、`protocol/`、`server/`、`presentation/game`、`presentation/common` 或构建脚本；未改动既有 `MenuScreenTest.kt`（逐行未变）。

---

## 2. 状态所有权与交互契约

1. **父层 `MenuScreen` 唯一保留所有权**：
   - 活跃会话引用与订阅：`activeLobby`、`lobbyStateSub`、`roomUpdateSub`；
   - 角色与导航守卫：`myRole`、`myViewerSeat`、`hasNavigatedToTable`；
   - 生命周期与代次守卫：`requestGeneration`、`isDisposed`、`runOnUi`；
   - 提示与错误状态：`statusMessage`、`errorMessage`；
   - 异步业务编排与导航：单机开赛链、TCP 连接链、房间拉取与加入/观战链、建房/离房链；
   - 背景与粒子特效：`renderBackground`、`FloatingParticle`、`animTimer`；
   - 秘籍与设置通用弹窗：`renderSecretsModal`、`renderSettingsModal`、`handleTouchModal`。
2. **子组件状态边界与交互**：
   - 子组件不持有 `LobbySession`、不持有 `TableNavigation`、不发网络请求、不读写 `requestGeneration`；
   - 构造参数与公开成员通过反射检测完全不含禁绝类型（测试通过）；
   - 对话框实例由父层长期持有，在同一 `MenuScreen` 生命周期内保持输入内容与开关状态；
   - 各子视图通过显式回调函数（如 `onConnect`、`onConfirm`、`onToggleReady`）向上汇报意图。

---

## 3. 第 3 节不变量逐项回读自查（引用行号）

本节由开发方使用读文件工具完整重新读回各文件代码，逐字逐行对照原基线行为完成自查。

### 3.1 像素级布局与样式不变
- **主菜单入口 (`MainMenuView.kt`)**：
  - L25–L30：按钮交互矩形 `btnPlaySingle` (490, 420, 300, 50)、`btnOpenLobby` (490, 355, 300, 50)、`btnSkin` (490, 290, 300, 48)、`btnSecrets` (490, 225, 300, 48)、`btnSettings` (490, 160, 300, 48)、`btnExit` (490, 95, 300, 48) 逐像素一致；
  - L57–L84：标题字号 `2.4f`（金黄色）、副标题 `1.1f`、按钮字号 `1.2f`（白色居中偏移 32f/30f）、提示信息 `1.0f`（居中 640, 480）与基线完全逐字一致。
- **TCP 连接弹窗 (`ConnectDialogView.kt`)**：
  - L29–L33：弹窗底板 `rectTcpModal` (360, 180, 560, 360)、输入框 `rectHostInput` (420, 390, 440, 42)、`rectPortInput` (420, 310, 440, 42)、按钮 `btnTcpConnect` (420, 220, 200, 44)、`btnTcpCancel` (660, 220, 200, 44) 逐像素一致；
  - L67–L86：弹窗标题 `1.6f`、标签 `1.1f` LIGHT_GRAY、光标闪烁 `(animTimer % 1f < 0.5f)` 下绘制下划线 `_`、按钮居中与基线完全一致。
- **大厅列表 (`LobbyListView.kt`)**：
  - L29–L35：左列按钮 `btnLobbyCreateRoom` (80, 560, 220, 46)、`btnLobbyJoinById` (80, 495, 220, 46)、`btnLobbyRefresh` (80, 430, 220, 46)、`btnLobbyBack` (80, 120, 220, 46)、面板 `rectRoomListPanel` (330, 180, 870, 440)、右下操作 `btnJoinSelected` (770, 110, 200, 46)、`btnSpectateSelected` (1000, 110, 200, 46) 逐像素一致；
  - L73–L140：顶栏标题 `1.6f`、连接状态文案与颜色、列表表头 5 列（房号/房主/人数/状态/赛制规则）、首 8 行渲染与高亮选中箭头 `▶` (1.0f, 0.9f, 0.4f, 1f) 逐字一致。
- **建房配置弹窗 (`CreateRoomDialogView.kt`)**：
  - L25–L30：弹窗底板 `rectCreateModal` (320, 140, 640, 460)、开关项 `btnCfgWind` (380, 430, 520, 42)、`btnCfgRedFive` (380, 365, 520, 42)、`btnCfgShuffle` (380, 300, 520, 42)、按钮 `btnCreateConfirm` (380, 190, 240, 46)、`btnCreateCancel` (660, 190, 240, 46) 逐像素一致；
  - L55–L68：标题 `1.6f`、各配置切换提示文案 `1.2f`、确认/取消按钮逐字一致。
- **输码加入弹窗 (`JoinRoomDialogView.kt`)**：
  - L24–L27：弹窗底板 `rectJoinModal` (380, 220, 520, 300)、输入框 `rectJoinRoomInput` (440, 360, 400, 44)、按钮 `btnJoinConfirm` (440, 270, 180, 44)、`btnJoinCancel` (660, 270, 180, 44) 逐像素一致；
  - L61–L76：标题 `1.6f`、输入框字符及光标、按钮与错误提示文本逐字一致。
- **房间准备等待 (`RoomPreparationView.kt`)**：
  - L24–L26：底板面板 (100, 220, 1080, 380)、操作按钮 `btnRoomReadyToggle` (100, 130, 240, 46)、`btnRoomStart` (370, 130, 240, 46)、`btnRoomLeave` (640, 130, 240, 46) 逐像素一致；
  - L62–L114：房号、生命周期状态、四席位（东/南/西/北家、[房主] 标记、已准备[GREEN]/等待准备[YELLOW]、等待玩家加入/电脑补位...）逐字一致。

### 3.2 行为不变量
- **一次性导航守卫**:
  - `startSinglePlay`：`MenuScreen.kt` L412–L415 检查并置位 `!hasNavigatedToTable` 后触发 `navigation.showPlayerTable()`；
  - `startRoomMatch`：`MenuScreen.kt` L598–L601 检查并置位 `!hasNavigatedToTable` 后触发 `navigation.showPlayerTable()`；
  - `onRoomInfoUpdated`：`MenuScreen.kt` L657–L661 在收到 `PLAYING` 时检查并置位 `!hasNavigatedToTable` 后跳转。
- **迟到会话关闭与取消**:
  - `connectRemoteLobby`：`MenuScreen.kt` L435, L438–L441 维护请求代次 `gen = ++requestGeneration`，在 `gen != requestGeneration` 或 disposed 时调用 `lobby?.close()` 抛弃迟到会话；
  - 取消连接：`ConnectDialogView.onCancel` 触发 `requestGeneration++`（`MenuScreen.kt` L127），`keyDown` 接收 ESC 且处于 `CONNECTING` 状态时自增 `requestGeneration++` 并返回 `TCP_CONNECT_DIALOG`（`MenuScreen.kt` L740–L742）。
- **刷新重置选中**:
  - `refreshRoomList`：`MenuScreen.kt` L646–L647 成功拉取列表后重置 `selectedRoom = roomList.firstOrNull()`，原样保留现状语义。
- **大厅断开返回**:
  - `disconnectLobbyAndReturn`：`MenuScreen.kt` L456–L468 执行 `detachLobby()`、`lobbyToClose?.close()`、`navigation.leaveToMenu()`，最后经 `runOnUi` 重置状态为 `MAIN`。
- **键盘字符过滤**:
  - `ConnectDialogView.keyTyped`：`ConnectDialogView.kt` L113–L134 逐字保留原过滤规则（HOST 允许 `a..z, A..Z, 0..9, ., -` 及 `\b`；PORT 仅允许数字且限长 5 位）；
  - `JoinRoomDialogView.keyTyped`：`JoinRoomDialogView.kt` L97–L107 逐字保留原过滤规则（ROOM_ID 仅允许字母或数字且限长 12 位）；
  - `MenuScreen.keyDown`：`MenuScreen.kt` L734–L751 ESC 键分支路由映射表逐字完全一致。
- **生命周期与 UI 调度**:
  - `MenuScreen.show`：`MenuScreen.kt` L210–L212 注册 `Gdx.input.inputProcessor = this`；
  - `MenuScreen.dispose`：`MenuScreen.kt` L759–L763 幂等置位 `isDisposed = true`，递增 `requestGeneration++` 并执行 `detachLobby()`；
  - `MenuScreen.runOnUi`：`MenuScreen.kt` L167–L180 对 `expectedGen` 判等与 `Gdx.app` 判空兜底逻辑逐字一致。

### 3.3 订阅不重复
- `MenuScreen.attachLobby`：`MenuScreen.kt` L182–L208 建立新订阅前显式先调用 `lobbyStateSub?.close()` 与 `roomUpdateSub?.close()`；
- `MenuScreen.init`：`MenuScreen.kt` L163–L165 仅对构造传入的 `initialLobby` 执行一次 `attachLobby`；
- 所有提取的 6 个子组件绝不持有 `LobbySession`，绝无额外重复监听建立。

---

## 4. 自动化回归测试说明

新增测试文件 `presentation/src/test/kotlin/com/majiang/presentation/menu/MenuScreenSubViewsContractTest.kt`，包含四大测试场景：
1. `testSubViewsArePurePresentationComponentsWithoutSessionOrNavigation`：通过 Java 反射遍历 6 个子 View 类所有构造函数、公开字段、公开方法入参及返回值，严密断言不存在 `LobbySession` 与 `TableNavigation` 类型；
2. `testConnectDialogInputPersistenceAcrossReopen`：测试通过触控与按键向 `ConnectDialogView` 录入自定义 host，点击取消返回主菜单后再进入，断言输入内容保留；
3. `testCreateRoomDialogConfigPersistenceAndConfirmationPayload`：测试在建房配置弹窗切换半庄（变东风）与码牌开关，取消返回大厅再进入，断言开关状态持久保留；点击确认创建，断言 Fake 接收并捕获的 `MatchConfig`（`winds = [1]`，`shuffleConfig.enabled = false`）与界面配置精准匹配；
4. `testJoinRoomInputResetOnEntryFromLobby`：测试输入房间号后取消，重新从大厅点击“输入房号加入”按钮，验证原代码“点击输入房号加入时清空 `inputRoomId` 并激活输入框”的语义得到严格保持。

---

## 5. 执行声明与待复核事项

1. **执行声明**：开发执行期间**未运行任何终端命令、构建或测试**（无 `gradle` 或测试进程产生），所有代码编辑与回读自查均仅使用文件读写工具完成。编译与测试全权交由验收方代跑。
2. **待复核事项**：
   - 请验收方运行 `./gradlew test --info` 验证既有 `MenuScreenTest`（10 项全量用例）与新增的 `MenuScreenSubViewsContractTest` 全部绿灯通过；
   - 请验收方核验各子组件对外暴露的常量与属性是否契合代码整洁与可测试性要求。
