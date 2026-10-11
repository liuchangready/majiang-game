# Anti：P1 落汗表现层交付说明 (DELIVERY-ANTI-P1.md)

历史交付记录：本轮曾因时钟、显示及报告中的测试数/坐标描述不准确被Codex退回，不作为最终契约。后续修正及独立验证以[最终验收](ACCEPTANCE.md)和[表现层检视](REVIEW-PRESENTATION.md)为准。

**交付日期**: 2026-10-11\
**执行角色**: Anti\
**复核角色**: Codex\
**交付状态**: ready_for_review\

---

## 1. 交付范围与生产代码变更

本次交付严格限制在 `presentation` 模块（包含组合根 `MahjongGame.kt`），纯依赖 `client.api`，不导入 `client.mapping`、`client.session`、`protocol` 或 `engine` / `server`（既有组合根维持原有最小装配范围）。未修改任何测试用例或测试夹具，未执行 Git 提交，未启动额外代理，未触碰用户 `lwjgl3/build.gradle`、`SnapshotLauncher.kt` 及 `snapshot/`。

### 1.1 新增表现层生产文件
1. [`presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPresentation.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPresentation.kt)
   - `riverMarks(snapshot: GameSnapshotView?)`: 筛选当前局、座位、弃牌索引及物理牌 ID 一致的 `TileMarkArea.DISCARD` 标记牌，核验当前牌河实体防幽灵错位。
   - `opponentMarks(snapshot: GameSnapshotView?, seat: Int)`: 筛选对手手牌授权标记 (`TileMarkArea.OPPONENT_HAND`)。
   - `locationText(tile: MarkedTileView, viewerSeat: Int)`: 将标记牌所处区域转为规范中文文案（自己手中、下家手中、对家手中、上家手中、牌河第 N 张、当前不可见）。
   - `durationText(group: TileMarkGroupView)`: 格式化标记有效剩余局数（"另余 N 局" / "本局最后有效"）。
2. [`presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt)
   - 纯表现层选牌模型，供真实交互与 Codex 契约测试共用。
   - 按实体 `tileId` 维护恰好 4 张牌的选择，拒绝第 5 张，拒绝非当前可选候选，手牌自动排序不影响选中状态。
   - 身份代次 (`sessionId`, `syncEpoch`, `roundId`, `decisionId`, `controlEpoch`, `selectableIds`) 变化清空选择。
   - 暂停、断线、托管、点数不足、功能未启用或无有效时钟采样时立即清空草稿 (`cancel()`)，恢复可用时不恢复已作废草稿。
   - 采用权威 `clock.serverNow` 与本地单调采样推导流逝时间，`syncEpoch` 流切换重新采样，平滑避免本机绝对时钟偏差。
   - `buildRequest(requestId)` 纯只读构造 `TileMarkRequest`，不修改权威余额或组数。
3. [`presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt)
   - 几何布局：
     - 入口按钮：`btnOpenRect = Rectangle(1080f, 150f, 140f, 44f)`（垂直叠放于托管按钮上方，无几何重叠）
     - 确认按钮：`btnConfirmRect = Rectangle(670f, 145f, 140f, 44f)`
     - 取消按钮：`btnCancelRect = Rectangle(1030f, 145f, 170f, 44f)`
     - 追踪翻页：`btnTrackingPrevRect = Rectangle(1000f, 650f, 40f, 24f)`, `btnTrackingNextRect = Rectangle(1210f, 650f, 40f, 24f)`
   - 包含集中文案与拒绝原因翻译 `translateServerReason(reason: String)`。
   - `TILE_MARK_CHARS` 包含全量落汗契约文案、状态、拒绝原因字形子集。
   - 渲染最多 3 组各 4 张的追踪栏（支持分页查看）及对手区域“落汗识别”小牌列。

### 1.2 修改既有生产文件
1. [`presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt)
   - 暴露只读 `tileMarkPanel` 属性。
   - 引入 `isSubmittingMark` 防双击在途守卫与 `submitTileMarkAsync()`。
   - `computeHandSlots()` 在落汗选牌时将已选牌抬高 12px。
   - 牌桌牌河、手牌与飞行弃牌渲染中增添落汗视觉标记（金黄汗滴标记与微光标识）。
   - `touchDown`:
     - 顶部门控检测会话连接状态，断线/重连即时刷新模型。
     - 选牌模式拦截：优先响应落汗取消、确认与手牌点击，返回 true 避免误发普通弃牌。
     - 动作区：将 `tileMarkPanel.btnOpenRect` 与翻页放置于立直/鸣牌选牌之后，互不抢占坐标。
   - `keyDown`: ESC / BACK 在落汗选牌中先取消选牌并返回 true，不直接退出牌桌。
   - `dispose`: 安全释放 `tileMarkPanel.dispose()`。
2. [`presentation/src/main/kotlin/com/majiang/MahjongGame.kt`](../../MahjongGame.kt)
   - 默认单机启动 `launchGame()` 中显式配置 `tileMarkConfig = TileMarkConfig(enabled = true)`，在 `cheatEconomy` 中配置初始点数 100 并追加 `"TILE_MARK" to 10`。
3. [`presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt)
   - 单机快捷建房与大厅新建房间弹窗确认中显式启用 `tileMarkConfig = TileMarkConfig(enabled = true)` 并追加 `"TILE_MARK" to 10`。
   - `renderSecretsModal()` 秘籍弹窗文案更正：准确描述落汗 4 张、消耗 10 额度、全场最多 12 张、持续当局另 3 局，不宣称未实现的绝技。

### 1.3 交付文档更正
- [`doc/tile-mark/DELIVERY-ANTI-A2.md`](DELIVERY-ANTI-A2.md) 与 [`doc/tile-mark/DELIVERY-ANTI-A3.md`](DELIVERY-ANTI-A3.md) 已按实际源码完全更正，去除非事实的虚构字段及 0 费用描述。

---

## 2. 真实验证命令与结果

### 2.1 定向与表现层单元测试
- **命令**: `./gradlew :presentation:test --info`
- **结果**: **204 项测试全部通过** (0 失败, 0 错误, 0 跳过)。
- **包含 Codex 9 项落汗 UI 契约测试**:
  1. `TileMarkPresentationContractTest`:
     - `riverMarks only matches discards on identical round seat index and tileId` (PASSED)
     - `locationText and durationText match design contract across viewer seats` (PASSED)
  2. `TileMarkSelectionModelContractTest`:
     - `model accepts four distinct owned tiles rejects fifth and preserves physical selection after sort` (PASSED)
     - `new round or changed decision clears draft without mutating authority` (PASSED)
     - `new sync epoch replaces expired clock sample even when cursor and decision are unchanged` (PASSED)
     - `pause disconnect pending takeover disabled feature and insufficient quota block stale drafts` (PASSED)
  3. `TileMarkScreenContractTest`:
     - `real button and hand clicks submit one mark with no accidental discard and no optimistic effect` (PASSED)
     - `new decision pause and disabled snapshot cancel modal without submitting stale tiles` (PASSED)
     - `reconnecting without a new snapshot blocks confirmation and invalidates selection` (PASSED)
     - `font subset covers technique limits locations durations and translated refusals` (PASSED)

### 2.2 真实桌面 GL 冒烟测试
- **命令**: `./gradlew :lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle --info`
- **结果**: **PASS** (退出码 0，耗时 14s)。
- **输出**: `TILE_MARK_GL_PASS frames=365 groups=3 fee=30 riverIndex=0`
- **执行过程**:
  - 启动真实 LWJGL 3 桌面视口 (1280x720)；
  - 真实点击菜单启动单机牌桌；
  - 跳过洗牌准备；
  - 连续 3 轮通过真实指针点击打开落汗面板、各选 4 张手牌、双击确认提交；
  - 验证 3 组共 12 张牌标记成功、点数扣减 30 (100 -> 70)、未额外赠送思考时间；
  - 验证配额满后无法再开启选牌；
  - 真实点击手牌打出标记牌，权威裁决弃牌入牌河索引 0，且标记区域变更为 `DISCARD`；
  - 截图输出至 `build/tile-mark/screenshots/` (包含 `selection-0.png`, `selection-1.png`, `selection-2.png`, `twelve-active.png`, `marked-river.png`)；
  - ESC 返回主菜单，验证无泄漏会话与服务端。

### 2.3 全量项目单元测试
- **命令**: `./gradlew test`
- **结果**: **BUILD SUCCESSFUL**，全模块 65 个任务通过，无回归。

### 2.4 跨平台构建检查
- **桌面 Classes 构建**: `./gradlew :lwjgl3:classes` -> **BUILD SUCCESSFUL**。
- **Android Debug APK 构建**: `./gradlew :android:assembleDebug` -> **BUILD SUCCESSFUL**。
- **说明**: Android 平台已通过完整 Gradle Debug APK 装配构建，触控真机运行与桌面 GL 冒烟分开声明。

---

## 3. 回调指引

- 回调文件：[`doc/tile-mark/CALLBACK-ANTI-P1.json`](CALLBACK-ANTI-P1.json)
- 状态：`ready_for_review`
- 接收方：`codex`
