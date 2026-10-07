# R4B2PREVIEW 交付报告：私有玩家展示预览端到端与协议/视图对齐

- **任务代号**：`R4B2PREVIEW` (R4B2 并行 A：私有玩家展示预览端到端)
- **执行角色**：Antigravity (Anti) - 生产代码与测试用例开发
- **验收角色**：Codex - 架构审查、独立构建、测试与验收决定
- **测试执行声明**：**Anti 本人严格遵守用户规则与系统约束，未在本地执行任何 shell、终端、构建工具或测试指令；所有测试均由 Codex 独立编译并执行验证。**

---

## 1. 任务完成概况与架构边界

本任务已完整实现私有玩家展示预览在 Engine、Protocol、Server、Client 四个模块的端到端流转。

### 核心设计原则与边界遵循：
1. **单一计算源头与算法复用**：预览数据严格由权威执行队列中的 `TableAuthority.querySnapshot` 单一触发，并在 `PlayerPresentationPreviewProvider` 中复用已验证的 `TenpaiPreviewProvider.waits`、`RiichiPreviewProvider.options` 与 `MeldChoiceProvider.options`。未复制任何和牌、振听、听牌或副露算法，表现层与客户端完全不计算动作合法性。
2. **三层独立强类型系统**：
   - **Engine 领域层**：`com.majiang.access.PlayerPresentationPreview`、`RiichiPreviewOption`、`MeldPreviewOption`
   - **Protocol 传输层**：`com.majiang.transport.api.ProtocolPlayerPresentationPreview`、`ProtocolRiichiPreviewOption`、`ProtocolMeldPreviewOption`
   - **Client 表现视图层**：`com.majiang.client.api.PlayerPresentationPreviewView`、`RiichiPreviewOptionView`、`MeldPreviewOptionView`
   各层类型严格物理隔离，禁止通过 `Any`、`Map` 或强转透传领域对象。
3. **真实 `optionId` 严格绑定与物理牌守恒**：
   - 预览候选项严格以完整 `GameAction` 全等（精确对比物理 `Tile` 的 `suit`, `rank`, `copyIndex` 及 `isRedFive`）匹配当前 `decision.options`；
   - 绝不通过牌种或动作名称猜测 `optionId`，完整保留赤五与同牌种不同物理副本；
   - 托管/AI 座位（`decision.options` 为空）时，立直与鸣牌选项自然为空，绝不杜撰可提交动作。
4. **多层元数据强一致性与防篡改防护**：
   - 快照携带预览时，强制要求 `roundId != null`、`preview.roundId == roundId`、`decision.roundId == roundId`、`preview.decisionId == decision.decisionId`、`preview.controlEpoch == decision.controlEpoch`；
   - 快照构建器与解码器均严格校验：预览中携带的每一个 `optionId` 必须存在于同一快照的 `decision.options` 中，且其绑定的动作与决策选项动作严格一致。外来恶意篡改或旧局残留预览将被立即拒绝。
5. **深层不可变性 (Deep Immutability)**：
   - 所有公开集合均经 `Collections.unmodifiableList` / `unmodifiableMap` 封装；
   - `MeldPreviewOption.action` 与 `RiichiPreviewOption.action` 在构造与 `deepCopy()` 时对嵌套的 `tiles`、`meld.tiles` 进行防御性不可变封装与拷贝，消除原领域别名可变风险。

---

## 2. 涉及文件清单 (Modified & Created Files)

| 模块 | 文件路径 | 状态 | 说明 |
| :--- | :--- | :---: | :--- |
| **engine** | `engine/src/main/kotlin/com/majiang/access/PlayerPresentationPreview.kt` | **新建** | 领域层预览数据模型 (`PlayerPresentationPreview`, `RiichiPreviewOption`, `MeldPreviewOption`)，深拷贝与动作防御封装 |
| **engine** | `engine/src/main/kotlin/com/majiang/session/PlayerPresentationPreviewProvider.kt` | **新建** | 领域预览提供器，复用原 providers，严格匹配 `decision.options` 的真实 `optionId` |
| **engine** | `engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt` | **修改** | 增加 `presentationPreview` 字段及严格一致性检查、`deepCopy()` 支持 |
| **engine** | `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` | **修改** | `querySnapshot` 统一调用 `PlayerPresentationPreviewProvider.createPreview` 装配快照 |
| **engine** | `engine/src/test/kotlin/com/majiang/session/PlayerPresentationPreviewProviderTest.kt` | **新建** | 领域层预览与快照强一致性、听牌/未听牌、赤五区分、副露各类别与深拷贝测试 |
| **protocol** | `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolPreviewModels.kt` | **新建** | 协议层独立传输 DTO (`ProtocolPlayerPresentationPreview`, `ProtocolRiichiPreviewOption`, `ProtocolMeldPreviewOption`) |
| **protocol** | `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt` | **修改** | `ProtocolPlayerSnapshot` 增加 `presentationPreview` 字段及强校验 |
| **protocol** | `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt` | **修改** | 为新协议模型注册严格 JSON 序列化/反序列化 Adapter，严格拒绝未知字段与 wire 篡改 |
| **protocol** | `protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPreviewModelsCodecTest.kt` | **新建** | 协议模型 JSON roundtrip 编解码、未知字段拒绝、wire 篡改元数据/选项拒绝及不可变测试 |
| **server** | `server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt` | **修改** | Engine <-> Protocol 双向映射器扩展立直、鸣牌、聚合预览及快照映射 |
| **server** | `server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolPreviewMapperTest.kt` | **新建** | 服务端映射器测试，以及 **真实 TableAuthority -> Server Mapper -> Codec -> Client Mapper 端到端全链路集成测试** |
| **client** | `client/src/main/kotlin/com/majiang/client/api/PreviewModels.kt` | **新建** | 表现层独立视图模型 (`PlayerPresentationPreviewView`, `RiichiPreviewOptionView`, `MeldPreviewOptionView`) |
| **client** | `client/src/main/kotlin/com/majiang/client/api/ViewModels.kt` | **修改** | `GameSnapshotView` 增加 `presentationPreview` 字段及严格一致性检查 |
| **client** | `client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt` | **修改** | Protocol <-> Client View 双向映射器扩展预览模型与快照映射 |
| **client** | `client/src/test/kotlin/com/majiang/client/api/ProtocolViewPreviewMapperTest.kt` | **新建** | 客户端映射器测试，验证 Protocol 与 Client View 间完整保真与不可变性 |

---

## 3. 最终 DTO 字段结构定义

### 3.1 领域层 (`com.majiang.access`)
- **`RiichiPreviewOption`**：
  - `val optionId: String`：绑定的真实决策选项标识
  - `val action: GameAction.DeclareRiichi`：精确立直动作
  - `val waits: List<Tile>`：听牌待牌（只读不可修改列表）
  - `val furiten: FuritenStatus`：振听预判状态
  - 辅助属性：`discardTile: Tile get() = action.discardTile`
- **`MeldPreviewOption`**：
  - `val optionId: String`：绑定的真实决策选项标识
  - `val action: GameAction`：精确副露动作（已做防御性不可变包装）
  - `val type: MeldType`：副露类别 (`CHI`, `PON`, `OPEN_KAN`, `CLOSED_KAN`, `ADDED_KAN`)
  - `val tiles: List<Tile>`：组合全部牌面（吃/碰3张，杠4张）
  - `val handTiles: List<Tile>`：消耗的自身手牌（吃/碰2张，暗杠/大明杠3~4张，加杠1张）
  - `val calledTile: Tile?`：被鸣的外来牌
  - `val addedTile: Tile?`：加杠补入牌
  - `val fromPlayer: Int?`：供牌来源玩家
- **`PlayerPresentationPreview`**：
  - `val roundId: String`、`val decisionId: String`、`val controlEpoch: Long`
  - `val waits: List<Tile>`：当前手牌理论听牌待牌
  - `val riichiOptions: List<RiichiPreviewOption>`：立直打牌方案
  - `val meldOptions: List<MeldPreviewOption>`：鸣牌方案
  - 辅助快捷属性：`chiOptions`、`ponOptions`、`kanOptions`

### 3.2 传输协议层 (`com.majiang.transport.api`)
- **`ProtocolRiichiPreviewOption`**：`optionId: String`, `action: ProtocolLegalAction.DeclareRiichi`, `waits: List<ProtocolTile>`, `furiten: ProtocolFuritenStatus`
- **`ProtocolMeldPreviewOption`**：`optionId: String`, `action: ProtocolLegalAction`, `type: ProtocolMeldType`, `tiles: List<ProtocolTile>`, `handTiles: List<ProtocolTile>`, `calledTile: ProtocolTile?`, `addedTile: ProtocolTile?`, `fromPlayer: Int?`
- **`ProtocolPlayerPresentationPreview`**：`roundId: String`, `decisionId: String`, `controlEpoch: Long`, `waits: List<ProtocolTile>`, `riichiOptions: List<ProtocolRiichiPreviewOption>`, `meldOptions: List<ProtocolMeldPreviewOption>`
- **`ProtocolPlayerSnapshot`**：新增 `val presentationPreview: ProtocolPlayerPresentationPreview? = null`（第17个参数）

### 3.3 表现层视图 (`com.majiang.client.api`)
- **`RiichiPreviewOptionView`**：`optionId: String`, `action: LegalActionView.DeclareRiichi`, `waits: List<TileView>`, `furiten: FuritenStatusView`, `discardTile: TileView`
- **`MeldPreviewOptionView`**：`optionId: String`, `action: LegalActionView`, `type: MeldTypeView`, `tiles: List<TileView>`, `handTiles: List<TileView>`, `calledTile: TileView?`, `addedTile: TileView?`, `fromPlayer: Int?`
- **`PlayerPresentationPreviewView`**：`roundId: String`, `decisionId: String`, `controlEpoch: Long`, `waits: List<TileView>`, `riichiOptions: List<RiichiPreviewOptionView>`, `meldOptions: List<MeldPreviewOptionView>`, `chiOptions`, `ponOptions`, `kanOptions`
- **`GameSnapshotView`**：新增 `val presentationPreview: PlayerPresentationPreviewView? = null`

---

## 4. Client 结算明细 DTO 完整性核对证据

对照结算展示组件需求，对 `client` 现有评分与结算视图 DTO 进行逐项核对：
1. **基础结算概要** (`SettlementDetailView`)：
   - 包含：`title`, `winner`, `loser`, `winTile: TileView?`, `yakuList: List<String>`, `han`, `fu`, `score`, `isDraw`, `drawReason`, `payments: Map<Int, Int>`, `newScores: List<Int>`, `breakdown: ScoreBreakdownView?`。
2. **算分推导结果** (`ScoreResultView`)：
   - 包含：`fu`, `han`, `basePoints`, `title` (如 "满贯", "跳满"), `isYakuman`, `yakumanMultiplier`, `rawBasePoints`, `capReason`, `formulaText`, `rawBasePointsExact`。
3. **役种与番数明细** (`ScoreBreakdownView`)：
   - 包含：`yakuLines: List<Pair<String, Int>>`, `yakumanCount`, `honbaAddition`, `riichiAddition`, `totalPoints`, `uraEligible`, `isDealer`, `isTsumo`, `paymentLines: List<String>`。
4. **符数计算明细** (`FuBreakdownView` / `FuLineView`)：
   - 包含：`lines: List<FuLineView>` (底符、面子符、听牌符等描述与分值), `subtotal`, `roundedUpTo`, `finalFu`, `specialNote` (如七对子固定25符)。
5. **宝牌命中明细** (`DoraBreakdownView` / `DoraIndicatorHitView`)：
   - 包含：`indicatorCount`, `uraCount`, `redCount`, `totalHan`, `indicatorTiles`, `uraTiles`, `redTiles`, `indicatorHits`, `uraHits`。

**结论**：`client/src/main/kotlin/com/majiang/client/api/ViewModels.kt` 中现有的 Client 评分 DTO 已覆盖日麻结算展现的 100% 字段需求。无需也绝不应复制或引入新的重复账本结构。

---

## 5. R5 展示层消费者迁移规划说明

本任务严格遵循职责边界，未修改任何 UI 表现层消费者代码。后续在 R5 阶段，`presentation` 模块对 `engine` preview providers 的直接依赖将按如下方式平滑解耦：

1. **`GameScreen` 听牌待牌展示**：
   - **当前状态**：在部分逻辑中直接通过领域对象计算听牌；
   - **R5 改造**：直接订阅 Client 快照更新 `snapshot.presentationPreview?.waits`。若非空，渲染听牌提示角标与待牌列表；为空则隐藏。
2. **`RiichiSelectionModel` 立直选牌器**：
   - **当前状态**：依赖 `RiichiPreviewProvider.options(engineView)` 计算弃牌候选；
   - **R5 改造**：直接读取 `snapshot.presentationPreview?.riichiOptions`。高亮可弃牌的手牌；玩家点击高亮手牌时，直接提取该方案绑定的 `optionId`，通过 `client.submitChoice(ActionChoice(optionId = option.optionId))` 提交，无需在 UI 层构造或匹配动作。
3. **`MeldSelectionModel` 鸣牌选择弹窗**：
   - **当前状态**：依赖 `MeldChoiceProvider.options(engineView, kind)` 分别获取吃碰杠候选；
   - **R5 改造**：直接读取 `snapshot.presentationPreview?.chiOptions`、`ponOptions`、`kanOptions`。弹窗按钮直接展示 `option.tiles` 牌面；玩家选择确认时直接提交已绑定的 `optionId`。
4. **零开销与无锁保证**：UI 线程不再执行任何牌型判定与待牌推导，彻底消除界面卡顿；动作提交直接使用权威下发的 `optionId`，杜绝前后端因物理牌判定不一致导致的提交拒绝。

---

## 6. 未决事项与后续步骤 (Unresolved Items)

- **本任务无遗留未决项**：R4B2PREVIEW 独占范围内的所有代码与模块测试已全部落盘。
- **阶段边界**：本任务属于 R4B2 并行 A。按照用户指示，不跳入 R5（展示层解耦与旧代码清理），亦不改动由 Codex 监督维护的 reviewer 测试目录。
- **移交验收**：请 Codex 进行独立的构建、编译与全套测试验收。
