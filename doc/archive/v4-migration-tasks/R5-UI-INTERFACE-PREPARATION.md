# R5 并行接口契约（R4验收后冻结）

2026-10-07 12:08：R4最终全量943项、独立183项及双端构建已通过。Codex复核下列门面、资源和文件所有权后，将此文作为R5三路并行初始接口契约。任务中发现必要差异须向唯一拥有者反馈，由该拥有者改签名并同步另外两路，不能各自复制API。

## 已核实的现有连接事实

- `ServerConnectionSession` 同时实现 `LobbySession`、`GameSession`、`SpectatorSession`；三种画面分别接收对应门面，画面不强制把大厅门面 cast 为玩家门面。组合根持有同一运行时并负责选取正确门面。
- `InProcessServer(true).createConnection()` 返回未绑定大厅端点；单机使用它创建运行时，再由 `LobbySession.createRoom()` 真实建房。根拥有并 tick 宿主，动画忙只作为节奏提示，不能推迟权威截止。
- `ServerConnectionSession.connectUdp` 在返回 Future 前同步运行 `UdpClient.connect`，会创建 socket/解析地址。首次连接必须在根的后台线程上调用；成功/失败后的画面回调用 `Gdx.app.postRunnable` 回到 GL 线程；失败时回收已创建 socket 与自有 scheduler。
- `LobbySession` 目前没有开赛方法；`GameSession.start(): CompletableFuture<Receipt>` 已有。R5 菜单需要开赛按钮，指定**menu/root 路**独占 `client.api/LobbySession.kt`，新增同名 `start(): CompletableFuture<Receipt>`，由同一个 `ServerConnectionSession` 实现并保持异步、角色权限；GameScreen 路不得同时改门面。菜单将配置/准备/开赛交给大厅端点，不能只显示按钮。

## 拟冻结的画面边界

`com.majiang.presentation.common.UiAssets` 由组合根持有和释放，至少提供 `SpriteBatch`、`ShapeRenderer`、可显示中文的 `BitmapFont` 与 `SkinManager`。游戏/观战/菜单画面借用资源，仅释放各自拥有的订阅、临时缓冲与私有字体。共享 `TileSkin` 仅由 `SkinManager` 释放，进退桌不能重复释放；全局缓存的 `GhostHandTexture` 在根最终收尾，或由 common 路定义统一缓存清理时机。中文字体使用已有 NotoSansSC/freetype 路径，不能以默认 `BitmapFont` 展示新菜单文案。

`com.majiang.presentation.common.TableNavigation` 由 `MahjongGame` 实现，拟提供：

```kotlin
fun openLocalLobby(): CompletableFuture<LobbySession>
fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession>
fun showPlayerTable()
fun showSpectatorTable()
fun leaveToMenu(): CompletableFuture<Receipt?>
fun exitGame()
```

所有方法只暴露 `client.api` 类型和标准类型。根保存真实运行时，`showPlayerTable/showSpectatorTable` 把它作为 `GameSession/SpectatorSession` 交给画面，画面不依赖 `MahjongGame`、`InProcessServer`、协议或 engine。菜单初次连接拿 `LobbySession`；建房/加房/观战成功后通知根切屏。`leaveToMenu()` 对已绑定会话先完成 `leaveRoom()`，再切回仍可复用的大厅；对本地需要结束的宿主由根释放。连接断开只呈现状态并保留可信视图，不自动收回控制权。

拟固定构造器：`MenuScreen(assets: UiAssets, navigation: TableNavigation, lobby: LobbySession? = null)`，`GameScreen(assets: UiAssets, session: GameSession, navigation: TableNavigation, skinOverride: TileSkin? = null)`，`SpectatorScreen(assets: UiAssets, session: SpectatorSession, navigation: TableNavigation)`。具体形状须在 R5 派发前以现有测试/渲染需求复核；允许同义命名调整，但三个任务不能各自发明不同导航或资源所有者。菜单/游戏/观战代码只接触 `client.api`、本模块展示类型和 libGDX。

## 三路文件所有权（R4 接受后使用）

1. **common/render**：`presentation/src/main/.../render/**`、`presentation/ClockDisplay.kt`、`screen/{SettlementDetailPresenter,SettlementDetailRenderer,FinalResultRenderer,TenpaiPanelRenderer}.kt` 与对应测试。迁物理牌/副露/完整结算/待牌/共享资源；不写 MenuScreen、GameScreen 或组合根。
2. **game/spectator**：`screen/GameScreen.kt`、`screen/{MeldSelectionModel,RiichiSelectionModel}.kt`、`presentation/{ShufflePreparationModel,ShufflePreparationPanel,GameScreenClient,V3GameClientAdapter}.kt`、新增 SpectatorScreen 与对应画面测试。删除旧适配壳归此路；不写共用渲染器、MenuScreen、client.api 或平台。
3. **menu/root**：`screen/MenuScreen.kt`、`MahjongGame.kt`、新增 `UiAssets/TableNavigation`、lwjgl3/android 启动器/Manifest/Gradle、`server/TableServerMain.kt`/server Gradle、唯一授权的 `client.api/LobbySession.kt` 与其必要实现及对应测试。实现后台首次 UDP 连接、菜单大厅/观战流程、根资源/生命周期；不写 GameScreen、render 或 reviewer。

Codex 持有 `tools/architecture` 旧 GL smoke 与最终架构门槛，随稳定 API 迁移。其他跨路缺口先报告，再由唯一所有者落盘。R5 仍须验证真实菜单单机、双端 UDP、自动重连、断开/离房、中文字体、连续进退桌/换肤、码牌/立直/副露/结算和安卓网络权限。
