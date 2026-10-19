# D-05 玩家与观战共用的公开桌面静态渲染交付说明 (第1步)

- **实施角色**: Anti (开发执行)
- **验收角色**: Claude (独立检视与验收)
- **任务编号**: IMPROVEMENT-PLAN.md §9.2 D-05 第1步（提取桌面布局与静态绘制）
- **依据任务简报**: `tools/architecture/v4-improvement/ANTI-D05-TASK.txt`
- **基线 commit**: `8910e5b` (feat/mj-tcp-migration)
- **命令执行声明**: **严格未运行任何构建、测试或 CLI 命令**（包括 `./gradlew`、`git` 等），全量自动化编译、测试与源扫描留待验收方独立执行。
- **改动范围隔离**: 仅限 `presentation` 模块中 `game` 与 `common.render` 包及其对应测试目录；**严禁且完全未触碰 `presentation/menu/` 及其测试（D-04 并行区）**；未触碰 `engine/`、`client/`、`server/`、`protocol/`、`android/`、`lwjgl3/` 等模块。

---

## 1. 新增与修改文件清单

| 文件路径 | 类型 | 职责一句话说明 |
|---|---|---|
| `presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderModel.kt` | 新增 | 纯公开牌桌渲染数据模型，全字段防御性不可变，绝不引用会话或快照私有对象。 |
| `presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderMapper.kt` | 新增 | 负责将玩家私有快照与观战公开快照映射为统一模型，同时扩展 Companion 工厂方法。 |
| `presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderer.kt` | 新增 | 纯无状态静态桌面绘制器，统一实现桌面中心、席位信息、手牌区、牌河与副露五大渲染段。 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/OpponentHandPresentation.kt` | 修改 | 新增 `rowsFromCounts` 工厂方法，为观战与无授权场景生成全 null 暗牌槽对手手牌行。 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt` | 修改 | 委托静态桌面五段绘制至 `TableBoardRenderer`，保留原有 public 签名与全部交互/动画链路。 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/SpectatorScreen.kt` | 修改 | 切换至统一渲染模型与 `TableBoardRenderer`，清理重复绘制私有方法，保持订阅与生命周期不变。 |
| `presentation/src/test/kotlin/com/majiang/presentation/common/render/TableBoardRenderModelContractTest.kt` | 新增 | 验证玩家/观战映射契约、落汗标记集正确性、观战隐私隔离及源文本禁止引用类型扫描。 |
| `presentation/src/test/kotlin/com/majiang/presentation/game/SpectatorSharedTableRenderTest.kt` | 新增 | 验证观战多帧渲染稳定性、赤宝牌/副露兼容性及四个相对视点牌河槽位旋转等价性。 |

---

## 2. 第 1 节差异项统一处理结果矩阵

根据 `ANTI-D05-TASK.txt` 第 1 节列出的两屏原有实现差异，统一处理结果如下：

| 差异项 | 原 GameScreen (玩家) | 原 SpectatorScreen (观战) | 统一后决议与像素/布局处理结果 |
|---|---|---|---|
| **牌河槽位公式** | `computeDiscardSlotPosition` (相对视角) | 私有公式 (绝对视角 viewerSeat=0) | 两套公式在基线中本不相同；按简报'玩家屏数值优先'规则统一至 GameScreen 数值，观战屏 rel1 牌河整体平移（x -30、行基准 280+col*40 → 440-col*40），rel2 牌河换向（700-col*40 → 460+col*40、480+row*56 → 530-row*56），属有意统一。 |
| **席位信息顶部标签位置** | 相对 seat 2: `(320f, 675f)` | 绝对 seat 2: `(640f, 670f)` | **有意统一至 `(320f, 675f)`**。观战屏顶部标签 X 坐标移动 -320 像素、Y 坐标移动 +5 像素，统一了四家相对布局体验，且为顶部中央的宝牌指示牌预留充裕视觉空间。 |
| **中心文案起点** | `(540f, 400f)` | `(510f, 400f)` 且带前缀 | **统一文案起点至 `(540f, 400f)`**。观战屏起点 X 坐标移动 +30 像素；若 `isSpectator == true` 则在字符串前追加 `"【观战】 "` 前缀，两屏格式与字色/缩放保护逻辑完全对齐。 |
| **落汗标记与半透明** | dora/牌河/副露有效标记 alpha 0.55，牌河提示框 | 无任何标记与提示框 | **通过参数/模型自然隔离**。`fromPlayerSnapshot` 提取标记集合；`fromPublicSnapshot` 标记集合恒为空集，`highlightTileId` 传入 `null`。观战屏零标记，杜绝隐私泄漏。 |
| **对手手牌行绘制** | `OpponentHandPresentation.build`（支持落汗牌面翻面） | 四家纯牌背（含底部 seat 0） | **统一至 `renderHandAreas`**。对手三家由 `OpponentHandRow` 驱动（观战为全 null 暗牌槽）；观战屏通过 `viewerHandBackCount > 0` 绘制底部 seat 0 牌背行（`startX=260f, y=40f, 44x62, step=48f`），玩家模式下 `viewerHandBackCount=0` 自动跳过。 |
| **投影矩阵与摄像机** | `tableCamera.combined` (带缩放平移) | `camera.combined` | **由调用方按参数传入**。`TableBoardRenderer` 绝不 new Camera、不持有摄像机，完全由调用方传入各自的 `projectionMatrix`。 |

---

## 3. 不变量逐项回读自查与代码行号索引

依据任务简报中的各项不变量要求，逐一回读源文件核验并给出代码行号引用：

### 3.1 TableBoardRenderModel 纯净性与不可变性
- **禁止引用类型与隐私隔离**:
  - 回读文件: [`TableBoardRenderModel.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderModel.kt)
  - 该文件全量 134 行中，**完全不包含** `"GameSnapshotView"`、`"PlayerView"`、`"PublicSnapshotView"`、`"GameSession"`、`"SpectatorSession"` 字样（已在 `TableBoardRenderModelContractTest` L173-200 中通过文本扫描自动化断言保护）。
  - 仅引入 client.api 的基础公开只读类型：`ClockInfo`、`MeldView`、`TileView`（L3-5）及 `OpponentHandRow`（L6）。
- **防御性拷贝与不可变集合**:
  - `TableBoardRenderModel.kt` L54-67: 对 `doraIndicators`、`markedDoraIndicatorIds`、`scores`、`riichiStatus`、`discards` (二维遍历拷贝)、`riverMarkedTileKeys`、`melds` (二维遍历拷贝)、`markedMeldTileIds`、`opponentHandRows`、`clocks` 全部执行 `ArrayList`/`HashSet` 拷贝后包裹 `Collections.unmodifiableList`/`Collections.unmodifiableSet`。
- **前置参数校验与标准方法**:
  - `TableBoardRenderModel.kt` L69-76: `init` 校验 `viewerSeat in 0..3`、`scores.size == 4`、`riichiStatus.size == 4`、`discards.size == 4`、`melds.size == 4`、`viewerHandBackCount >= 0`。
  - `TableBoardRenderModel.kt` L78-130: 完整实现 `equals`、`hashCode` 与安全脱敏 `toString`。

### 3.2 TableBoardRenderMapper 映射工厂与隐私隔离
- **解耦设计与 Companion 扩展**:
  - 回读文件: [`TableBoardRenderMapper.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderMapper.kt)
  - 映射逻辑置于独立的 `TableBoardRenderMapper.kt`（L18-96），既隔离了 `TableBoardRenderModel.kt` 的源文本依赖，又通过 L98-102 的 Companion 扩展函数 `TableBoardRenderModel.Companion.fromPlayerSnapshot` / `fromPublicSnapshot` 提供了自然的调用语法。
- **玩家私有映射 (`fromPlayerSnapshot`)**:
  - `TableBoardRenderMapper.kt` L20-66:
    - `viewerSeat = snapshot.viewerSeat`、`isSpectator = false`；
    - `markedDoraIndicatorIds`: 取 `EXPOSED_INDICATOR` 的 tileId（L24-26）；
    - `riverMarkedTileKeys`: 通过 `TileMarkPresentation.riverMarks(snapshot)` 解析对应 `(seat, discardIndex, tileId)`（L28-37）；
    - `markedMeldTileIds`: 取 `EXPOSED_MELD` 的 tileId（L39-41）；
    - `opponentHandRows`: 委托 `OpponentHandPresentation.build(snapshot)`（L43）；
    - `viewerHandBackCount = 0`（L63）。
- **观战公开映射 (`fromPublicSnapshot`)**:
  - `TableBoardRenderMapper.kt` L68-95:
    - `viewerSeat = 0`、`isSpectator = true`；
    - 全部落汗标记集合恒传 `emptySet()`（L83, L88, L90）；
    - `opponentHandRows`: 委托 `OpponentHandPresentation.rowsFromCounts(board.handTileCounts, board.phase)`（L71）；
    - `viewerHandBackCount = board.handTileCounts.getOrElse(0) { 0 }`（L72）；
    - 严禁任何私有字段进入模型。

### 3.3 OpponentHandPresentation 纯计数构造
- **纯计数工厂方法**:
  - 回读文件: [`OpponentHandPresentation.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/game/OpponentHandPresentation.kt) L75-88:
  - `rowsFromCounts(handTileCounts: List<Int>, phase: GamePhaseView): List<OpponentHandRow>` 针对席位 1..3 构建 3 行；
  - `STACKING` 阶段张数置 0，其余阶段取 `handTileCounts[seat]`；
  - 槽位全部填充为 `HandDisplaySlot(null)`（牌面严格为 null，牌背展示）；
  - 既有 `build` 与 `buildSlotsForSeat`（L40-70, L90-136）语义与实现保持不变。

### 3.4 TableBoardRenderer 无状态桌面绘制器
- **架构纯净性与禁止引用**:
  - 回读文件: [`TableBoardRenderer.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderer.kt) L36: 声明为纯 `object`，无实例字段，不持有任何摄像机或快照状态；
  - 文本扫描验证不包含 `GameSnapshotView`、`PlayerView`、`GameSession`、`SpectatorSession`。
- **公共坐标函数**:
  - `computeDiscardStart`（L43-50）与 `computeDiscardSlotPosition`（L57-70）严格与原 `GameScreen` 的数值与相对旋转规则完全一致。
- **五大绘制段实现**:
  1. `renderTableCenter`（L75-133）: 起点 `(540f, 400f)`，观战前缀 `"【观战】 "`，宝牌槽位 `(640f + i*32f, 315f)` 28x38，落汗指示牌 alpha 0.55，并在 `try-finally` 块中严格恢复字色与缩放。
  2. `renderSeatsInfo`（L138-192）: 相对位置 `0=(40,80)`, `1=(1150,400)`, `2=(320,675)`, `3=(40,400)`；仅非观战且当前视角追加 `" [YOU]"`；时钟紧迫度红色/立直标记/当前巡泛黄；`try-finally` 恢复字体状态。
  3. `renderHandAreas`（L197-251）: 相对席位 1/2/3 对应 Right/Top/Left 对手暗手牌行；落汗牌面翻面支持 alpha 0.55 与选中青色边框；观战底部席位牌背 `viewerHandBackCount > 0` 在 `(260f + i*48f, 40f)` 44x62 绘制；最后统一 flush 缓冲。
  4. `renderDiscards`（L378-434）: 基于 `computeDiscardSlotPosition`，支持 `isTileVisibleInRiver` 过滤未落地的飞行牌；落汗弃牌 alpha 0.55；选中牌绘制青色边框 `(pos.first - 2f, pos.second - 2f, 42f, 56f)`。
  5. `renderMelds`（L440-472）: 使用 `MeldDisplayLayout.layoutAll(model.melds, model.viewerSeat)` 两参数重载，`MeldDisplayRenderer.append` 传入 `markedTileIds = model.markedMeldTileIds`，支持选中组高亮描边。

### 3.5 GameScreen 接线与 API 兼容性
- 回读文件: [`GameScreen.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt)
- **Public 签名与数值不变**:
  - L675-676: `fun getDiscardStart(seat: Int): Pair<Float, Float> = TableBoardRenderer.computeDiscardStart(seat, currentView.viewerSeat)`
  - L678-679: `fun getDiscardSlotPosition(seat: Int, slotIndex: Int): Pair<Float, Float> = TableBoardRenderer.computeDiscardSlotPosition(seat, slotIndex, currentView.viewerSeat)`
  - 签名与返回数值完全不受影响，保留对 `DiscardAnimationController` 及外部测试的 100% 兼容。
- **渲染循环调度**:
  - L1014-1022: 每帧在进入绘制前，由 `currentSnapshot` 构造一次 `tableModel` 与 `tableOptions`；
  - L1025-1048: 中心与四家基本信息绘制；
  - L1051-1063: 对手手牌行保留原 `gameStarted` 门控（`if (tableModel != null && gameStarted)`）；
  - L1066-1093: 牌河与副露绘制；
  - L1096-1100: 明手牌 `renderPlayerHands` 与飞行牌动画 `renderFlyingDiscard` 及其余 UI 面板、按钮完全保留原有逻辑与顺序。
- **清理冗余代码**:
  - 彻底删除了原 `GameScreen` 内冗余的私有渲染函数（原 L1198-1556 的 `renderTableCenter`、`renderSeatsInfo`、`renderOpponentHands`、`renderTopOpponentHand`、`renderLeftOpponentHand`、`renderRightOpponentHand`、`renderDiscards`、`renderMelds`）。

### 3.6 SpectatorScreen 接线与生命周期守恒
- 回读文件: [`SpectatorScreen.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/game/SpectatorScreen.kt)
- **生命周期与网络订阅 100% 不动**:
  - L99-126: `init` 订阅 `session.onPublicUpdate` 与 `session.onDisconnect`，异步拉取 `session.publicSnapshot()` 逻辑完全保持原样。
  - L134-153: `show()`、`hide()` 键盘 back 键拦截与释放逻辑完全保持原样。
- **静态桌面五段接线**:
  - L170-174: 从 `currentSnapshot` 构造 `TableBoardRenderModel.fromPublicSnapshot(snap)` 与 `TableBoardRenderOptions`；
  - L177-240: 依次调用 `TableBoardRenderer` 的 `renderTableCenter`、`renderSeatsInfo`、`renderHandAreas`、`renderDiscards`、`renderMelds`；
  - 顺序严格保持为：返回按钮（L166）→ 桌面中心 → 席位信息 → 手牌背 → 牌河 → 副露 → 终局总成绩弹窗（L242-263）。
- **清理重复实现**:
  - 删除了观战屏原私有的 `getDiscardSlotPosition`、`renderTableCenter`、`renderSeatsInfo`、`renderHandTileBacks`、`renderDiscards`、`renderMelds`。

### 3.7 新增测试覆盖与隐私边界断言
1. [`TableBoardRenderModelContractTest.kt`](file:///d:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/TableBoardRenderModelContractTest.kt):
   - L29-120: `fromPlayerSnapshot` 字段全量映射与宝牌/牌河/副露落汗标记提取断言；
   - L123-170: `fromPublicSnapshot` 观战默认值、`viewerSeat=0`、标记集全空、对手槽位全 null、`viewerHandBackCount=14` 隐私零泄漏断言；
   - L172-200: 源文本扫描断言，确保 `TableBoardRenderModel.kt` 与 `TableBoardRenderer.kt` 不包含任何禁止类型字符串。
2. [`SpectatorSharedTableRenderTest.kt`](file:///d:/majiang/presentation/src/test/kotlin/com/majiang/presentation/game/SpectatorSharedTableRenderTest.kt):
   - L21-82: 配合 `FakeSpectatorSession` 与 `TestUiAssets` 进行多帧无头渲染，验证赤宝牌、吃碰副露及各家不同手牌张数下无任何渲染异常，且快照数据未被篡改；
   - L85-129: 对 4 个不同视点（`viewerSeat=0..3`）的 `GameScreen` 进行弃牌起点 `getDiscardStart` 与全部 24 个槽位 `getDiscardSlotPosition` 的全量集合比对，验证旋转等价性。

---

## 4. 零命令执行声明

在此郑重声明：
- 本次交付过程中，**未执行任何命令行指令**（包括但不限于 Gradle 构建、单元测试运行、Git 操作等）。
- 所有文件的新增与修改均由文件读写工具完成，完全满足 D-05 交付约束。
- 编译通过性、全部既有测试回归及新增契约/集成测试的验证完全委托验收方执行。

---

## 5. 不确定点与说明

1. **Companion 扩展函数拆分设计**:
   - 任务简报中说明模型与渲染器源文本不得包含 `GameSnapshotView` / `PlayerView`。
   - 为兼顾这一隐私边界与简报第 5 节指定的调用语法 `TableBoardRenderModel.fromPlayerSnapshot(currentSnapshot)`，将这两个工厂函数实现在 `TableBoardRenderMapper.kt` 中作为 `TableBoardRenderModel.Companion` 的扩展函数提供。
   - 如此既保证了 `TableBoardRenderModel.kt` 与 `TableBoardRenderer.kt` 的 100% 纯净（通过文本扫描测试），又完全满足了调用方的易用性。
2. **观战屏顶部标签位置调整**:
   - 观战屏顶部席位标签由原绝对坐标 `(640f, 670f)` 统一为相对坐标 `(320f, 675f)`，已在第 2 节详细记录。此调整使得观战屏与玩家对局屏的视觉元素分布一致，且避免与顶部中央的宝牌指示牌重叠。

---

## 6. 第 1 轮返修记录

- **返修 1（测试编译修复）**:
  - 文件: [`TableBoardRenderModelContractTest.kt`](file:///d:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/TableBoardRenderModelContractTest.kt) L74
  - 将 `selectableTileIds` 的初始值由 `emptySet()` 修复为 `emptyList()`，严格匹配 `TileMarkView.selectableTileIds: List<Int>` 的字段类型定义，消除了测试编译推断错误。经读文件工具逐字回读确认 L74 为 `selectableTileIds = emptyList(),`。
- **返修 2（交付说明事实更正）**:
  - 文件: [`ANTI-D05-DELIVERY.md`](file:///d:/majiang/doc/v4-重构/ANTI-D05-DELIVERY.md) 第 2 节差异矩阵
  - 更正原简报转述的"两套公式数值完全一致"表述，准确记录基线 8910e5b 下两屏槽位公式的实际差异（rel1 整体平移 x -30、行基准 `280+col*40` → `440-col*40`；rel2 换向 `700-col*40` → `460+col*40`、`480+row*56` → `530-row*56`），并明确注明依简报"玩家屏数值优先"原则统一至 GameScreen 数值，属于有意统一。
- **零命令执行声明**:
  - 本轮返修全程**严格未运行任何构建、测试或 CLI 命令**，仅通过文件读写工具修改与核验。

---

## 7. 第 2 轮返修记录

- **修复 1（TileMarkGroupView 局序契约对齐）**:
  - 文件: [`TableBoardRenderModelContractTest.kt`](file:///d:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/TableBoardRenderModelContractTest.kt) L62
  - 将 `expiresAfterRoundOrdinal` 由 3 修复为 4，严格对齐 `TileMarkModels.kt` L187 的契约约束 `expiresAfterRoundOrdinal == createdRoundOrdinal + 3`（夹具中 `createdRoundOrdinal = 1` 时必须为 4）。
  - 经读文件工具逐字回读确认 L62: `            expiresAfterRoundOrdinal = 4,`。
- **修复 2（TileMarkGroupView 四张牌契约对齐）**:
  - 文件: [`TableBoardRenderModelContractTest.kt`](file:///d:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/TableBoardRenderModelContractTest.kt) L65
  - 将 `tiles` 列表由 3 张补齐为 4 张互异牌，追加 `MarkedTileView(32, meldTile3, TileMarkArea.NOT_VISIBLE)`，满足 `TileMarkGroupView` 对牌数为 4 的严格契约。`NOT_VISIBLE` 区域天然被 `TableBoardRenderMapper` 与 `TileMarkPresentation.riverMarks` 区域过滤器排除，对后续 L96-119 的既有测试断言完全无影响。
  - 经读文件工具逐字回读确认 L65: `            tiles = listOf(markedDora, markedRiver, markedMeld, MarkedTileView(32, meldTile3, TileMarkArea.NOT_VISIBLE))`。
- **修复 3（TableBoardRenderer KDoc 消除源扫描命中）**:
  - 文件: [`TableBoardRenderer.kt`](file:///d:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderer.kt) L31
  - 将 KDoc 中点名禁用类型的注释文字替换为泛化表述：` * 2. 严禁引用私有玩家快照、公开快照或会话等上层视图类型（禁用清单以 TableBoardRenderModelContractTest 源扫描为准）；`，彻底消除源文本扫描测试中的禁用字符串字面量匹配。
  - 经读文件工具逐字回读确认 L31，并已自查全文再无任何 `GameSnapshotView`、`PlayerView`、`GameSession`、`SpectatorSession` 字样。
- **零命令执行声明**:
  - 本轮返修全程**严格未运行任何构建、测试或 CLI 命令**，仅通过文件读写工具修改与核验。


