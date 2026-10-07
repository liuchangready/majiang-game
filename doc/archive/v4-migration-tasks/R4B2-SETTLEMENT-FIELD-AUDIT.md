# R4B2 结算展示字段审计

2026-10-06，Codex 独立只读审查。审计范围为当前工作树 `SettlementDetailRenderer`、`SettlementDetailPresenter`、其结算/行模型，以及 V4 domain → protocol → client 的类型、映射和编解码。PREVIEWFIX 正在运行，本次没有构建、运行测试或修改生产代码。

**结论：现有结算展示读取的字段已完整覆盖，未发现需要在 R4 新增结算 DTO 字段的数据缺口。** 当前消费者仍依赖 engine 类型，替换消费者属于 R5。本报告只记录将来的接线要求；按用户最新范围，R4 完成后暂停，不启动 R5/R6。

## 核对依据

- 原任务要求核对现有 DTO，不复制账本：[TASK-ANTI-R4B2.md](TASK-ANTI-R4B2.md) 的 Presentation data supplied by server，以及 [TASK-ANTI-R4B2PREVIEW.md](TASK-ANTI-R4B2PREVIEW.md) 第 6 项。
- 当前结算容器：`presentation/src/main/kotlin/com/majiang/screen/GameScreen.kt:148` 的 `SettlementData`。
- 消费端：`presentation/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt:95` 的 `renderDetailModal`；`SettlementDetailPresenter.kt:23` 的 `SettlementDetailRow(text, tiles)` 和 `:37` 的 `rows`。
- 领域原始评分：`engine/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt:59` 的 `ScoreBreakdown`，同文件 `:14` 的 `DoraIndicatorHit`、`:29` 的 `DoraBreakdown`；`FuCalculator.kt:23` 的 `FuLine`、`:28` 的 `FuBreakdown`；`ScoreCalculator.kt:31` 的 `ScoreResult`。
- 服务端映射：`server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt:583` 至 `:636`。
- 协议类型：`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt:751` 的 `ProtocolFuLine`、`:759` 的 `ProtocolFuBreakdown`、`:805` 的 `ProtocolDoraIndicatorHit`、`:842` 的 `ProtocolDoraBreakdown`、`:918` 的 `ProtocolScoreResult`、`:934` 的 `ProtocolScoreBreakdown`。
- 客户端映射：`client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt:304` 至 `:360`；客户端独立类型在 `ViewModels.kt:540` 至 `:802`。

以上行号对应本次读取状态；PREVIEW 的其他代码修改可能使后续行号移动，方法名/类名是稳定定位依据。

## 评分与明细行逐项核对

下表字段均为 domain → protocol → client 同名逐值映射；宝牌实体转换为独立 `ProtocolTile` / `TileView`，并非直接携带 engine 对象。

| 展示用途与消费位置 | 实际读取字段 | 服务端 → 客户端映射位置 | 结论 |
| --- | --- | --- | --- |
| 和牌概览、役满标记；Renderer:298；Presenter:68 | `yakumanCount`、`score.isYakuman`、`score.yakumanMultiplier` | EngineProtocolMapper:611、624 → ProtocolViewMapper:335、348 | 完整；役满倍数未丢失 |
| 普通和牌概览；Renderer:306 | `score.han`、`score.fu`、`totalPoints` | EngineProtocolMapper:611、624 → ProtocolViewMapper:335、348 | 完整 |
| 役名/番数、役种小计；Presenter:72、87 | `yakuLines: List<Pair<String, Int>>` | EngineProtocolMapper:625 → ProtocolViewMapper:349 | 完整；逐役条目保留，未降为一个总番数 |
| 役满标题、封顶名；Presenter:78、196、269 | `score.title`、`score.capReason` | EngineProtocolMapper:615、619 → ProtocolViewMapper:339、343 | 完整，nullable 封顶原因保留 |
| 宝牌分类与合计；Presenter:95、119 | `dora.indicatorCount`、`uraCount`、`redCount`、`totalHan` | EngineProtocolMapper:599 → ProtocolViewMapper:323 | 完整 |
| 表宝指示牌 → 目标 → 命中物理牌；Presenter:125 | `dora.indicatorHits[*].indicator/target/tiles` | EngineProtocolMapper:593、607 → ProtocolViewMapper:317、331 | 完整；逐个指示牌和重复命中项保留 |
| 表宝旧式牌列回退；Presenter:134 | `dora.indicatorTiles` | EngineProtocolMapper:604 → ProtocolViewMapper:328 | 完整 |
| 里宝展示资格；Presenter:142 | `uraEligible` | EngineProtocolMapper:634 → ProtocolViewMapper:358 | 完整；它是计分资格，不是观战公开授权 |
| 里宝明细及牌列回退；Presenter:146、154 | `dora.uraHits[*].indicator/target/tiles`、`uraTiles` | EngineProtocolMapper:605、608 → ProtocolViewMapper:329、332 | 字段完整；公开投影的授权过滤见后文 |
| 赤宝牌列；Presenter:163 | `dora.redTiles`、`redCount` | EngineProtocolMapper:602、606 → ProtocolViewMapper:326、330 | 完整 |
| 符数逐项；Presenter:231、236、241 | `fu.lines[*].description/fu` | EngineProtocolMapper:583、585 → ProtocolViewMapper:304、309 | 完整 |
| 符数进位、保底、七对子/平和说明；Presenter:227 至 262 | `fu.subtotal`、`roundedUpTo`、`finalFu`、`specialNote` | EngineProtocolMapper:587 至 590 → ProtocolViewMapper:311 至 314 | 完整；特殊说明未丢失 |
| 原始基本点大整数展示；Presenter:191、267 | `score.rawBasePointsExact: String` | EngineProtocolMapper:621 → ProtocolViewMapper:345 | 完整；未缩减到 Int 或 Double |
| 基本点及支付倍率说明；Presenter:189、190、275 | `score.basePoints`、`isDealer`、`isTsumo` | EngineProtocolMapper:614、635、636 → ProtocolViewMapper:338、359、360 | 完整 |
| 权威支付说明行；Presenter:323 | `paymentLines: List<String>` | EngineProtocolMapper:630 → ProtocolViewMapper:354 | 完整 |
| 本场、供托、最终相加式；Presenter:328 至 350 | `honbaAddition`、`riichiAddition`、`totalPoints` | EngineProtocolMapper:631 至 633 → ProtocolViewMapper:355 至 357 | 完整 |
| 行内牌面与颜色；Renderer:231、333、529；Presenter:361 | 行 `text/tiles`；牌的花色/数字/赤五身份 | EngineProtocolMapper:123 → ProtocolViewMapper:138；ViewModels:91、134 | `text` 由 presenter 格式化；物理 `tileId`、`suit`、`rank` 全部传递，`TileView.isRedFive` 已提供赤五标识 |

`score.rawBasePoints`、`score.formulaText` 当前 Presenter 没有直接读取，但协议与 client 仍保留这两个字段（EngineProtocolMapper:618、620 → ProtocolViewMapper:342、344）。没有为了迁移删除领域评分数据。

Presenter 当前的乘法、进位和说明文案用于解释已经确定的权威结果；不调用和牌判定或再选择评分方案。R5 调整它接收 `ScoreBreakdownView` 时，不应在客户端重算合法性或替代服务端结算结果。

## 结算外层、恢复和按钮状态

`SettlementData` 的 13 个字段在客户端 `SettlementDetailView` 中全部有对应字段：

| 当前 SettlementData 字段 | V4 客户端来源 | 消费说明 |
| --- | --- | --- |
| `title` | `ProtocolViewMapper.projectSettlementDetail:458/473/488/503` 按事件类型生成 | Renderer:295 标题；属于展示文本，不需要新增领域字段 |
| `winner`、`loser` | `TsumoWin.player` 或 `RonWin.winner/loser` → `SettlementDetailView` | Renderer:284、289 玩家名称与放铳方 |
| `winTile` | 和牌事件实体经 `mapTile` | Renderer:216 和了牌；保留物理副本 |
| `yakuList`、`han`、`fu`、`score` | 和牌事件逐字段映射 | 当前旧 UI 无 breakdown 时的简版回退仍可支持 |
| `isDraw`、`drawReason` | `DrawGame` → `isDraw=true`、`reason.name` | 流局分支完整；不生成假的评分明细 |
| `payments`、`newScores` | `ScoreSettled` → immutable map/list | Renderer:386 至 391 底部净变化与最新点数 |
| `breakdown` | 和牌事件 → `mapScoreBreakdown` | 三标签页全部明细的来源 |

证据链：EngineProtocolMapper:736/746 映射和牌事件，`:784` 映射 `ScoreSettled`；ProtocolViewMapper:381/385/396 映射独立事件视图，`:447` 将结算事件组装为 `SettlementDetailView`；`:605` 的 `mapSnapshot` 同时保留 `settlementRoundId`、完整 `settlementEvents` 及其派生 `settlementDetail`。因此后续 UI 无需只依赖一次性动画回调才能恢复结算。

Renderer 的其余输入不是评分字段缺口：

- `currentScores` 有 `GameSnapshotView.view.scores` / `session.scores`；当前 Renderer 只在 `settlement.newScores` 不完整时回退使用。
- `viewerSeat` 在玩家 `PlayerView` 中；公开观战不应伪造玩家座位，之后观战 UI 需要明确“无本人座位”的展示分支。
- `isMatchEnd` 可来自公开 `session.lifecycle/finalResult`，与当前 GameScreen:180 的权威终局判定来源一致。
- `canAgariYame` 的权威候选已由 `LegalActionView.DeclareAgariYame` 及 decision/options 提供。当前 GameScreen:2654 按合法候选判断按钮；R5 替换类型与提交 optionId 即可，不需自行用比分推断权限。
- `section/page`、按钮矩形、字体和摄像机都是本地表现状态，不需要加入网络快照。

`projectSettlementDetail` 当前只提取首个 Tsumo/Ron/Draw/ScoreSettled 作为单个摘要（ProtocolViewMapper:448 至 451）。完整 `settlementEvents` 仍逐项保留；本次不把单摘要误报为支持多赢家详情切换，也不以此新增第二套账本。若未来展示多赢家列表，应消费现有完整事件并制定明确的视图契约。

## 编解码与公开权限核对

- `ApiMessage.kt:977`、`:999`、`:1019`、`:1071` 分别覆盖符数、宝牌命中项、宝牌分解和完整评分；严格字段集合与上述 DTO 对齐。
- `ApiMessage.kt:1263` 的 `ProtocolScoreResultAdapter` 保留 `rawBasePointsExact` 字符串、封顶原因和公式文本；`:1295` 保留 `FuLine.description/fu`。
- `ApiMessage.kt:597/612` 对 Tsumo/Ron 读取 optional `breakdown`，没有将复杂明细缩减成 `han/fu/score` 三个数值。
- PUBLIC 工厂的 `revealSettlementUra=false` 会去除里宝事件及 `breakdown.dora.uraTiles/uraHits`。只有可信公开结算授权为 true 时保留实体。这是隐私过滤，不是协议漏字段；R4 观战接线必须沿用此授权边界，不能把 `uraEligible` 当成公开授权。保留数值而隐藏实体时，展示端也不能把“未公开实体”误写成“命中 0”。

本次是源码完整性审计，不等同于运行验证。已有可用于下一轮统一回归的测试源码包括：

- `server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolMapperTest.kt:282`：领域评分/计分结果映射保真。
- `client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt:223`：协议评分到客户端映射保真。
- `protocol/src/test/kotlin/com/majiang/transport/api/ProtocolSerializationTest.kt:128`：完整评分 JSON 往返。
- `tools/architecture/v4-review/V4PublicProjectionAcceptanceTest.kt`：授权/未授权里宝、评分嵌套集合及数组隔离的独立探针。

这些测试的当前通过状态应以主监督串行验收记录为准，本报告没有重新执行。

## R4 结论与后续边界

R4 结算数据侧无需新增字段或账本。R4 尚需其余预览/观战任务及整体验收，本报告不能替代那些门禁。

仅记录将来的 R5 消费改动：`SettlementDetailRenderer` 接收 `SettlementDetailView/ScoreBreakdownView`；`SettlementDetailPresenter` 和 `SettlementDetailRow` 使用 client 的评分/牌视图；`TileSkin` 等表现接口接收 client 牌值。删除 engine 导入应在 R5 正式启动后落实，不能通过反向映射回领域对象或增加兼容壳绕开边界。**当前不执行这些改动；按最新用户要求，完成 R4 后暂停。**
