# R5 ROOT 任务交付报告 (Anti)

**交付任务**：R5 ROOT（菜单、大厅、组合根与双端启动入口）  
**交付时间**：2026-10-07  
**开发人员**：Antigravity (Anti)  
**验收负责人**：Codex（负责串行编译、全量与独立回归、多端构建验收）  

---

## 1. 交付文件清单

### 1.1 组合根与表现层导航/资源
- [`presentation/src/main/kotlin/com/majiang/MahjongGame.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/MahjongGame.kt)
  - V4 纯净组合根，实现 `TableNavigation` 契约；
  - 装配 `InProcessServer(isSingleplayer = true).createConnection()` + `ServerConnectionSession`，单机经真实 `LobbySession.createRoom()`；
  - 远端 `openRemoteLobby(host, port)` 通过后台守护线程异步建立，成功/异常回切 GL 线程，具备 generation token / dispose 守卫，避免迟到成功复活旧画面或泄漏 session；
  - 直接调用并编译期检查已冻结的 `com.majiang.presentation.game.GameScreen` 与 `SpectatorScreen` 构造函数，零反射与零 `Class.forName`；
  - 直接在 `render()` 中类型安全读取 `(screen as? GameScreen)?.isDiscardPlaybackBusy` 驱动权威宿主节拍；
  - 实现生命周期 `pause`/`resume`/`dispose`，统筹收尾 `uiAssets`、`inProcessServer`、`currentSession` 与当前场景。
- [`presentation/src/main/kotlin/com/majiang/screen/MenuScreen.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/screen/MenuScreen.kt)
  - 主菜单与大厅交互场景，完全解耦自 `MahjongGame`，仅依赖 `UiAssets`、`TableNavigation` 与 `LobbySession` 门面；
  - 完整覆盖单机快速开始、UDP 远程连接对话框、大厅房间列表拉取与刷新、赛制规则配置与建房、房间详情与就绪、直接调用 `LobbySession.start()` 启动对局、观战进入、千术秘籍弹窗与设置弹窗；
  - 多端交互支持：鼠标点击、高亮悬停、键盘快捷键 (ESC) 与文本输入框打字/退格。
- [`presentation/src/main/kotlin/com/majiang/presentation/common/UiAssets.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/UiAssets.kt)
  - 表现层共享资源容器，持有 `SpriteBatch`、`ShapeRenderer`、`BitmapFont`、支持中文的 NotoSansSC 字体、`SkinManager` 与 `GhostHandTexture`；
  - 对齐 COMMON 路包名 `com.majiang.presentation.common.render`；
  - 收束 `FreeTypeFontGenerator` 生命周期：发生异常立即局部释放，成功生成则赋给成员由组合根在退出时统一一次性 `dispose()`，杜绝底层原生字体泄漏。
- [`presentation/src/main/kotlin/com/majiang/presentation/common/TableNavigation.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/TableNavigation.kt)
  - 画面跳转与生命周期流转统一契约 (`openLocalLobby`, `openRemoteLobby`, `showPlayerTable`, `showSpectatorTable`, `leaveToMenu`, `exitGame`)。
- [`presentation/build.gradle`](file:///D:/majiang/presentation/build.gradle)
  - 新增 `implementation project(':server')`，支持组合根装配 `InProcessServer`。

### 1.2 客户端 API 与传输层修复
- [`client/src/main/kotlin/com/majiang/client/api/LobbySession.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/LobbySession.kt)
  - 新增 `fun start(): CompletableFuture<Receipt>` 门面方法，房主在菜单直接开赛，无需向玩家画面借动作。
- [`client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt)
  - 满足 `LobbySession.start()` 实现；在 `connectUdp` 失败时清理自有 scheduler。
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt)
  - 在 `connect` 创建 `DatagramSocket()` 后，若域名解析、`socket.connect` 或初始化过程抛出异常，在 `catch` 中立即关闭未完成绑定的 `socket`，并在 `closeSchedulerOnShutdown` 为 true 时关闭注入的 scheduler，杜绝 socket 文件描述符泄漏。

### 1.3 服务端与双端启动器
- [`server/src/main/kotlin/com/majiang/server/TableServerMain.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/TableServerMain.kt)
  - 权威服务端入口默认切换为 `UdpTableServer`；
  - 严格支持 `--bind, -b` 与 `--port, -p` 命令行参数；
  - 启动完成后打印真实监听端口 `server.boundPort`。
- [`android/AndroidManifest.xml`](file:///D:/majiang/android/AndroidManifest.xml)
  - 声明 `android.permission.INTERNET` 与 `android.permission.ACCESS_NETWORK_STATE` 网络权限。
- [`lwjgl3/src/main/kotlin/com/majiang/desktop/DesktopLauncher.kt`](file:///D:/majiang/desktop/DesktopLauncher.kt) & [`android/src/main/kotlin/com/majiang/android/AndroidLauncher.kt`](file:///D:/majiang/android/AndroidLauncher.kt)
  - 确认双端启动器直接实例化 `MahjongGame()`，无旧 SessionAccess 重载或 LocalHost 直连。

### 1.4 自动化测试
- [`presentation/src/test/kotlin/com/majiang/MenuScreenTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/MenuScreenTest.kt)
  - 覆盖组合根生命周期、UiAssets 共享资源管理与回退；
  - 覆盖单机大厅开房建房与 `LobbySession.start()` 流程；
  - 覆盖离房退回大厅 (`leaveToMenu`) 及宿主资源释放；
  - 覆盖连续 3 次进退桌与换肤持久化验证（`SkinManager` 共享不被多次提前释放）；
  - 覆盖菜单所有二级弹窗状态流转（秘籍、设置、UDP连接、大厅、建房对话框）；
  - 覆盖键盘打字、退格与输入框交互；
  - 覆盖 `FakeLobbySession` 模拟端到端流程。
- [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientFailureTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientFailureTest.kt)
  - 验证非法端口（port = -1）连接失败时立即回收 scheduler；
  - 验证域名不可解析时连接失败、返回 `UnknownHostException` 并回收 scheduler；
  - 验证 `ServerConnectionSession.connectUdp` 异常路径安全清理自身调度器。

---

## 2. 跨路接口与反馈核验情况 (FEEDBACK-R5ROOT)

1. **COMMON 路包名对齐**：
   `UiAssets.kt`、`MahjongGame.kt` 与 `MenuScreenTest.kt` 已全面从 `com.majiang.render.*` 切换至 `com.majiang.presentation.common.render.SkinManager` 与 `GhostHandTexture`，未修改 COMMON 路持有的任何源码文件。
2. **FreeTypeFontGenerator 资源安全**：
   `UiAssets.createDefault()` 中收束了生成器生命周期，若 `generateFont` 抛错则通过局部句柄立即 `dispose()`，杜绝底层 C++ 字体泄漏。
3. **消除反射与旧画面包名依赖**：
   `MahjongGame.kt` 直接 import 并调用已冻结签名的 `com.majiang.presentation.game.GameScreen` 与 `SpectatorScreen`，反射转发代码与旧 `com.majiang.screen.*` 字符串已被彻底移除。
4. **初次 UDP 连接失败清理与防复活**：
   - `UdpClient.connect` 强化异常时 `socket?.close()`；
   - `MahjongGame.openRemoteLobby` 引入 `connectionSequence` 与 `isDisposed` 状态守卫，晚到的连接结果若发现已被更新的连接取代或应用已销毁，将立即关闭会话，绝不复活旧画面。

---

## 3. 未完成项与风险说明

- **未完成项**：无。R5 ROOT 范畴的所有文件、接口契约与异常防护已全部落盘完成。
- **跨路对齐提示**：`MahjongGame` 直接消费 `com.majiang.presentation.game.GameScreen(assets: UiAssets, session: GameSession, navigation: TableNavigation, skinOverride: TileSkin? = null)` 与 `SpectatorScreen(assets: UiAssets, session: SpectatorSession, navigation: TableNavigation)`。此签名与 `R5-UI-INTERFACE-PREPARATION.md` 冻结契约完全一致，请 GAME 路确保相同包名与主构造器签名。

---

## 4. 未运行测试声明

**根据用户指令与任务约束，本轮开发 Anti 仅使用内置文件读写/编辑工具，未运行任何 Gradle 任务、Shell 命令、终端构建或自动化测试。全部单元测试与构建验证工作将由 Codex 串行执行并进行最终验收。**
