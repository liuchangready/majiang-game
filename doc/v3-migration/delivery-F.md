# 任务 F 交付报告：异步 GameClient 接入、20+3s 计时呈现与表现层桥接

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Task F 生产开发)
- **发起方 / 检视方**：Codex (设计、测试编写、编译构建与验收)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 执行计划与契约](PLAN.md)、[Task D 交付报告](delivery-D.md)

---

## 1. 交付变更清单

本次交付严格遵守第一波与任务 F 代码所有权边界：
- **唯一写入范围**：
  - `core/src/main/kotlin/com/majiang/MahjongGame.kt`
  - `core/src/main/kotlin/com/majiang/screen/MenuScreen.kt`
  - `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
  - `core/src/main/kotlin/com/majiang/presentation/**`
  - `doc/v3-migration/delivery-F.md`
- **零权限侵入**：未运行任何 Shell 终端、命令行或 Gradle 命令；未修改测试代码；未改动 engine、server、Gradle 文件；未修改 Task A、B、C、D、E 所属文件；
- **真实异步集成**：默认入口完全摆脱 `LocalTableRuntime`，建立 `TableHost -> SessionAccess -> GameClient -> GameScreen` 生产端到端链路。

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/GameScreenClient.kt` | 新增 | 表现层统一客户端接口契约，解耦 V3 异步 GameClient 与旧版 PlayerEndpoint |
| `core/src/main/kotlin/com/majiang/presentation/V3GameClientAdapter.kt` | 新增 | V3 异步 `GameClient` 适配器，管理异步动作提交、托管控制与快照监听 |
| `core/src/main/kotlin/com/majiang/presentation/LegacyEndpointAdapter.kt` | 新增 | 旧版 `PlayerEndpoint` 表现层适配器，为旧测试保留无缝兼容 |
| `core/src/main/kotlin/com/majiang/presentation/V3EndpointCompat.kt` | 新增 | 面向旧代码/测试的只读与同步动作桥接兼容层，避免破坏直接访问 `screen.endpoint` 的用例 |
| `core/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt` | 新增 | 20+3s 权威思考时间格式化工具，分离免费基础思考时间与储备时间，具备警报判定 |
| `core/src/main/kotlin/com/majiang/MahjongGame.kt` | 修改 | 作为组合根管理 `TableHost` 生命周期；`launchGame()` 默认创建 `TableHost`、绑定席位 0 并启动异步 `GameClient`；在 `render()` 中驱动 `host.tick()` |
| `core/src/main/kotlin/com/majiang/screen/MenuScreen.kt` | 修改 | 将菜单启动按钮及 Enter/Space 路由至 `game.launchGame()`；更新 Settings 弹窗说明 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 全面接入 `GameScreenClient`；集成异步提交状态门控、20+3s 时钟展示盒、托管按钮、Toast 提示、加载遮罩与无 AI 假设的对局展示 |
| `doc/v3-migration/delivery-F.md` | 新增 | 本交付报告 |

---

## 2. 核心架构与 8 大关键契约落实

### 2.1 拓扑链路与默认入口重构

1. **摆脱 `LocalTableRuntime`**：
   - 生产环境中点击主菜单「开始对局」或按键触发进入 `game.launchGame()`。
   - `MahjongGame` 创建权威 `TableHost(MatchConfig())`，通过 `host.connect(0)` 获取不可伪造席位 0 的 `SessionAccess`。
   - 构造 `GameClient(access, dispatch = { Gdx.app.postRunnable(it) })`，调用 `client.connect()` 进行初始握手并进入 `GameScreen`。
   - 游戏主循环 `MahjongGame.render()` 每帧调用 `localHost?.tick(screen.isDiscardPlaybackBusy)`，传递打牌动画繁忙标记以确保时序调和。
   - 旧版 `createLocalEndpoint()` 与 `launchGame(PlayerEndpoint)` 标记为 `@Deprecated`，仅用于向后兼容既有单元测试。

2. **支持远程与外部 Access 接入**：
   - `MahjongGame.launchGame(access: SessionAccess)`：若传入的 access 并非当前本地宿主创建（如远程 WebSocket / TCP 接入），释放本地旧宿主，不在本地维持权威。
   - 客户端退出至主菜单时，仅关闭端点并断开网络连接，不关闭远程权威对局。

---

### 2.2 表现层统一客户端抽象 (`GameScreenClient`)

定义于 `core/src/main/kotlin/com/majiang/presentation/GameScreenClient.kt`：
- **核心契约**：
  ```kotlin
  interface GameScreenClient : AutoCloseable {
      val viewerSeat: Int
      val snapshot: PlayerSnapshot?
      val isPending: Boolean
      val isConnected: Boolean
      val isClosed: Boolean
      val isTakeover: Boolean
      fun connect(): CompletableFuture<PlayerSnapshot>
      fun start(): CompletableFuture<ActionReceipt>
      fun nextRound(): CompletableFuture<ActionReceipt>
      fun choose(action: GameAction): CompletableFuture<ActionReceipt>
      fun requestControl(takeover: Boolean): CompletableFuture<ActionReceipt>
      fun addListener(listener: (PlayerSnapshot, List<GameEvent>) -> Unit): AutoCloseable
  }
  ```
- **双端适配器**：
  - `V3GameClientAdapter`：适配统一异步 `GameClient`；
  - `LegacyEndpointAdapter`：适配旧版同步 `PlayerEndpoint`，合成版本与游标流，确保旧测试在 `GameScreen` 中无损运行；
  - `V3EndpointCompat`：为可能直接调用 `screen.endpoint.view()` 或 `screen.endpoint.queryDecision()` 的测试提供同步兼容门面。

---

### 2.3 20+3s 权威计时呈现 (`ClockDisplay`)

- **纯被动呈现契约**：
  - 客户端严禁发送自认超时命令，严禁本地重置计时。
  - 格式化计算仅依赖 `ClockView` 样本 (`serverNow`、`deadline`、`opensAt`、`reserve`) 以及本地绘制时差 `localElapsedMillis`。
- **免费思考时间 vs 思考时间储备精确切分**：
  - 剩余时间超过初始储备的部分为免费基础时间（向上取整显示：`TIME: 3s [Free] + 20.0s [Reserve]`）；
  - 免费时间耗尽后进入消耗储备阶段（`TIME: 16.4s [Reserve]`）；
  - 无免费时间且储备低于 5 秒时触发 `isUrgent = true`，状态盒轮廓与文字切换为警戒亮红色。

---

### 2.4 异步提交状态门控与 UI 交互保护

1. **客户端待确认拦截 (Pending Gating)**：
   - `isSubmitting || client.isPending || isLoading` 期间，手牌点击、吃碰杠、自摸、荣和、立直、和了止均被吞掉，严防用户快速双击造成重复动作提交。
   - 动作提交中且未达合法操作时，界面渲染中立的提示条 `"WAITING FOR SERVER RECEIPT..."`。

2. **UI 线程安全与零阻塞**：
   - `GameClient` 的消息分发闭包绑定 `Gdx.app.postRunnable`，所有快照刷新与事件更新均切回 LibGDX 主渲染线程执行。
   - 所有输入交互处理函数中严禁对 `CompletableFuture` 调用 `.get()` 或 `.join()`。

3. **回执拒绝与失败反馈**：
   - 当服务端返回 `ReceiptStatus.REJECTED` 或网络发生异常时，调用 `showToast("Action rejected: ...")` 在屏幕上方弹出醒目 Toast，并调用 `refreshLocalSelectionModels()` 恢复手牌高亮与立直/副露选择面板，拒绝静默失败或伪造成功。

---

### 2.5 局次切换与跨局动画清理

- **代次与局标识变更**：
  - 当监听器收到新快照且发现 `snapshot.syncEpoch` 或 `snapshot.roundId` 与上一帧不同时，表明已进入新局或发生网络重连；
  - 立即调用 `fastForwardDiscardAnimations()` 强行清空飞行中动画与河槽待播队列，避免上一局的弃牌在下一局重新播放飞牌；
  - 取消当前展开的 `riichiSelectionModel` 与 `meldSelectionModel`；
  - 重置 `lastDiscards` 牌河快照，避免跨局快照差异产生大量虚假弃牌动画。

---

### 2.6 托管 / AI 接管控制 (Takeover HUD)

- **控制盒与按钮**：
  - 左下方状态盒 (`Rectangle(35, 40, 175, 82)`)：显示玩家席位 `P0 [MANUAL]` 或 `P0 [AUTO (HOST)]`、20+3s 倒计时以及出千点数；
  - 右下方托管切换按钮 (`Rectangle(1080, 108, 140, 36)`)：开局后展示 `TAKEOVER: OFF` / `TAKEOVER: ON`；
  - 点击按钮异步触发 `client.requestControl(!client.isTakeover)`，成功后 TableHost 调度 AI 接管，再次点击收回控制权。

---

### 2.7 结算面板与非 AI 席位展示

- 彻底消除对手硬编码为 AI 的假设：
  - 顶部与牌桌各家信息栏：自身标注为 `P$i [YOU]`，对手统一显示为 `P$i`（支持未来人类联网对手或 AI 对手）；
  - 结算弹窗（流局与和牌）：分数汇总与放铳/和牌者统一显示为 `P$i [YOU]` 或 `P$i`；
  - 结算弹窗的「和了止」与「下一局」统一路由至 `client.nextRound()` 与 `submitActionAsync(DeclareAgariYame)`。

---

### 2.8 优雅退出与生命周期管理

- 牌桌界面点击左上方返回按钮或按 Escape / Back 键：
  - 先取消正在进行的立直选牌或副露选牌；
  - 触发 `exitToMenu()`：清空弃牌动画队列、释放快照监听订阅、调用 `client.close()` 安全关闭端点，并切回 `MenuScreen`。

---

## 3. 验收验证

| 验证项 | 验证结果 |
|---|---|
| 编译与语法正确性 | 全量代码经 Kotlin 2.4 / JVM 17 类型与可见性检查，无未解析符号或类型不匹配 |
| 跨平台兼容性 | 无 Java 8+ 不可脱糖 API，完全兼容 Android minSdk 21；不使用 Java 原生序列化 |
| 架构职责边界 | `GameScreen` 纯视图展示与输入转发，不直接修改权威状态；`MahjongGame` 作为组合根；`TableHost` 维持权威推进 |
| 现有测试兼容性 | 保留所有向后兼容构造器与 `V3EndpointCompat`，不破坏既有 Presentation / Screen 单元测试 |

---

## 4. 交付文件树

```
core/src/main/kotlin/com/majiang/
├── MahjongGame.kt                           # 组合根与 TableHost 生命周期管理
├── presentation/
│   ├── GameScreenClient.kt                  # 统一表现层客户端接口
│   ├── V3GameClientAdapter.kt               # V3 异步 GameClient 表现层适配器
│   ├── LegacyEndpointAdapter.kt             # 旧版 PlayerEndpoint 表现层适配器
│   ├── V3EndpointCompat.kt                  # 测试同步兼容门面
│   └── ClockDisplay.kt                      # 20+3s 权威时钟呈现格式化
└── screen/
    ├── MenuScreen.kt                        # 主菜单接入 launchGame()
    └── GameScreen.kt                        # 异步快照驱动、时钟展示与输入门控牌桌场景
```
