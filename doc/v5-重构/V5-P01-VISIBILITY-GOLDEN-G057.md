# V5 P0.1-a/P0.1-b 可见性/快照深拷贝黄金 —— G-057a..G-057d

日期：2026-10-25
任务：G-057a 旧 `PlayerView` 快照深拷贝、G-057b 事件列表深拷贝与逐席可见性、G-057c 公开投影白名单/授权里宝/不可变、G-057d 真实 136 实体状态的 owner/他座/观战隔离。
性质：**严格 test-only**。不修改 V4 生产代码、V5 生产代码、旧五组 fixture、manifest/catalog、既有测试或已接受审计报告。
结论：本组件 G-057a..d 的可比字段 **PASS**；不可比/语义不兼容字段单列 **BLOCKED/UNREPRESENTABLE**。**G1 仍未批准，G5 未完成，V4 仍是唯一生产架构**；本任务没有平台（PG/WSS/Unity/IL2CPP/Android）通过声明。

---

## 1. 范围、基线、归属

| 项 | 实测值 |
|---|---|
| worktree | `D:/majiang-wt/visibility-golden-g057` |
| branch | `wt/v5-visibility-golden-g057` |
| 冻结 base | `1432d775526de921f2cde3aba283236fc3b8e8c1` |
| 起始 HEAD | `1432d775526de921f2cde3aba283236fc3b8e8c1`（与 base 相同） |
| base 是否为 HEAD 祖先 | 是（`git merge-base --is-ancestor` exit 0） |
| 起始 `git status --porcelain` | 0 行（干净） |
| worktree 归属 | 本任务独占；未被其他 worktree 占用；用户未提交 `CLAUDE.md`/`PROMPTS.md`/任务书/协议未触碰 |
| 生产基线 | V4 仍是唯一生产；本轮没有改 V4 或 C# 生产文件 |

交付文件恰为 5 个（逐文件见 §10）：

```text
engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt
tests/v5-golden/visibility-v1.json
dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs
tools/v5-golden/probe-visibility-binding.ps1
doc/v5-重构/V5-P01-VISIBILITY-GOLDEN-G057.md
```

没有新增/修改第 6 个跟踪文件；没有刷新旧 5 组 fixture、`catalog-v1.json`、`implementation-v1.json`、manifest 或审计报告。

---

## 2. 来源：真实 V4 公开入口

Kotlin 导出器直接调用：

| 用例 | 真实入口 | 已有证据 |
|---|---|---|
| G-057a | `PlayerView.fromGameState(state, seat, legalActions, myFuriten, myDiscardHistory)` | `SessionSnapshotContractTest` 的深拷贝方法 |
| G-057b | `SessionUpdate(view, events)`、`SessionUpdate.filterVisibleEvents(events, seat)` | `SessionSnapshotContractTest` 的 5 个事件方法 |
| G-057c | `PublicProjectionFactory.project(...)`、`PublicProjectionFactory.publicEvents(...)`、`PublicBoardView.fromGameState` | `PublicProjectionTest` 的 12 个方法 |
| G-057d | `PlayerView.fromGameState`、`PublicProjectionFactory.project` | 旧测试未直接断言对手暗牌/牌山/里宝，本任务新增实际探针 |

G-057d 不是静态字段推断：导出器先由 `TestTableBuilder` 构造一个真实的、136 张实体唯一、`StateInvariants` 通过的 `GameState`，再调用生产 `PlayerView.fromGameState` 并逐字段访问；C# 再从导出的 placement 重建 V5 领域状态并调用生产投影。两侧都用实际值断言，而不是“反射看不到字段”或“源码里没有字段”。

---

## 3. 四个用例

| case ID | G 行 | 输入/动作（显式记录） | 实际 V4 预期（fixture 中） |
|---|---|---|---|
| `VIS-G057A-SNAPSHOT-COPY` | G-057a | 真实 136 状态；seat0 为观察者；1 个 CHI 副露、2 张弃牌、1 张袖牌、庄家摸牌；`DeclarePon(0,[60,64])`；快照后清空源手牌/源副露 tiles/源动作 tiles/源弃牌/源 dora/源袖牌并把 score[0] 改 99999 | 旧 view 仍保留 `myClosedTiles` 13 张、`myAllClosedTiles` 14 张、`scores[0]=25000`、副露 3 张、动作 2 张、弃牌/宝牌不变 |
| `VIS-G057B-EVENT-VISIBILITY` | G-057b | 显式事件：`TileDealt(0)`、`TileDrawn(1)`、`RinshanDraw(2)`、表宝 `DoraRevealed`、里宝 `DoraRevealed(isUraDora=true)`、`TileDiscarded(2)`、`ChiDeclared(1)`；生产列表在 `SessionUpdate` 后 `clear()`；授权列表追加 `RoundEnded` | 生产列表清空后 8 条仍在；seat0 未授权可见 4 条、seat1 可见 4 条；未授权里宝对所有 seat 为空；公开弃牌四席均可见；授权列表里宝对四席可见 |
| `VIS-G057C-PUBLIC-PROJECTION` | G-057c | 真实 136 状态：seat1 PON、两家弃牌、seat0 立直且供托 1、表宝/里宝各 1；session `ROUND_ACTIVE`；事件含私发/私摸/岭上/千术流水/公开弃牌/CHI/表宝/里宝/`TsumoWin+ScoreBreakdown`/`RoundEnded`；`revealSettlementUra=false/true` | 公开 board 只含公开字段；私发/私摸/岭上/千术流水/未授权里宝被丢弃；未授权 `breakdown.dora.uraTiles/uraHits` 为空，授权后恢复；输入 scores/clocks/events 事后修改不影响投影；集合不可变 |
| `VIS-G057D-SEAT-VIEW-ISOLATION` | G-057d | 显式 136 实体：seat0 13 暗+摸 1；seat1 9 暗+CHI 3+袖 1；seat2 13 暗+3 弃牌；seat3 13 暗；王牌 14（表宝 index4=57、里宝 index5=58）；seat1 cheatPoints=990、一条 `vis-g057d-cheat-seat1` 流水；seat0/1/2 view + 观战 board | seat0 view 只看到他家 `handTileCounts`；seat1 暗牌/摸牌/袖牌/里宝/王牌顺序/千术流水不进入 seat0/观战；seat1 自己的 view 能看到自己暗牌，袖牌进入 owner extension；观战 board 有 `handTileCounts=[14,9,13,13]`、公开 dora、弃牌/副露 |

所有用例显式记录 `clockUsed=false`、`implicitRandomUsed=false`；没有取当前时间、没有隐式随机、没有生成 ID。`roundId`、事件序号 `eventSeq`、`commandVersion`、V5 cursor/state_version 均为显式输入（STRUCTURE），不与 V4 值混比。

---

## 4. Fixture 与来源哈希

- 生成命令（唯一支持形式）：

```powershell
$env:V5_VISIBILITY_GOLDEN_EXPORT='1'
.\gradlew.bat :engine:test --tests "com.majiang.projection.V5VisibilityGoldenExporterTest" --rerun-tasks --console=plain
```

- 默认（不设环境变量）仍真实执行全部四个用例并做深拷贝/可见性断言，但不写文件；同时校验已提交 fixture 的 schema、`caseCount=4` 和四个 case ID。
- 双次独立构造 + 双次渲染必须字节一致；实测两次导出 fixture SHA-256 相同（`26FDD766...`，见 §8）。
- 导出模式若发现旧 fixture 与本次 base 不同，会在日志逐项打印 `changed top-level keys`、旧/新 SHA-256，**不静默忽略**；实测提示为 `[cases, exporterSource]`（本轮新增实测字段）。
- `renderSha256LfNormalized` 绑定 fixture 自身内容：把该字段值替换为 `null` 后对 LF 文本做 SHA-256；C# 用同一规则重算并比对。fixture 以 LF 写出、末尾 LF。
- `sourceHashes[]` 对 17 个 V4/V5 源码/测试文件分别记录：
  - `sha256RawBytes`：checkout 原始字节；
  - `sha256LfNormalized`：CRLF→LF 后的 UTF-8 文本；
  - `bytesRaw`、`crlfCount`、`lfOnlyCount`、`lineEndings`。

  两者从不混用；**没有任何手填的 golden 结果哈希**。
- fixture 实际值：

| 项 | 值 |
|---|---|
| path | `tests/v5-golden/visibility-v1.json` |
| bytes | 130637 |
| raw SHA-256 | `26fdd766df42ac071637ae951e0912745f2ffc678f8f5adb05556551e552132a` |
| LF-normalized SHA-256 | 同上（文件本身为 LF） |
| CRLF count / LF-only count | 0 / 5923 |
| `renderSha256LfNormalized` | `816917a057eeb4a69cb939e14d06e44917fee67b95ff4fa7ab2fc4c9c1ff0379` |
| `v4SourceCommit` | `1432d775526de921f2cde3aba283236fc3b8e8c1` |
| `v5BaselineCommit` | `1432d775526de921f2cde3aba283236fc3b8e8c1` |

---

## 5. V5 绑定：真实调用与表示映射

C# 新类 `VisibilityGoldenTests` 从 placement 重建 `TilePositionMap`/`RoundStateMachine`，再调用：

- `TableVisibilityProjection.ProjectPlayerSnapshot`
- `TableVisibilityProjection.ProjectPublicSnapshot`
- `TableVisibilityProjection.ProjectOwnerPrivate`
- `TableEventProjection.Project` / `ProjectMany`

没有把 V4 对象传进 V5 投影，也没有把 V5 输出再与 V4 输出“自证相等”。

<details>
<summary><b>逐字段映射（V4 → V5 / 断言方式）</b></summary>

| V4 字段/行为 | V5 目标 | 映射/依据 | C# 是否逐值比较 |
|---|---|---|---|
| `phase` | `PublicBoardView.Phase` | `MapPhase(RoundPhase)` | 是 |
| `roundWind/roundNumber/honbaSticks/riichiSticks/dealerSeat/currentTurnPlayer` | 同名 board 字段 | 数值保持；`int`/`uint` 边界 | 是 |
| `wallRemaining` | `WallRemaining` | V4 活牌山后缀 `[wallPointer, wallEndExclusive)` 规范化为 V5 稠密 `Wall(0..N-1)`，V5 `WallPointer=0`；V4 原 pointer 为 SOURCE_ONLY | 是（规范化后） |
| `doraIndicators` | `DoraIndicators` | 牌 ID 保持；V5 要求指示牌在王牌 slot≥4 | 是 |
| `scores` (Int) | `ScoreView.Score` (long) | 数值保持 | 是 |
| `riichiStatus` | `TableSeatPublicFacts.Riichi` → `PlayerPublicView.Riichi` | 布尔保持 | 是 |
| `discards` | `DiscardView.TileId` / 顺序 | river 列表 index → `Discard(seat,index)`；`turnNumber=index`，两个 flag=false（V4 状态未持久化，SOURCE_ONLY） | 牌 ID/顺序是 |
| `melds` | `TableMeldFact` → `MeldView` | V4 `MeldType/fromPlayer/calledTile` 作为 V5 事实输入；V5 Domain 只存物理槽 | 类型/牌 ID/顺序是 |
| `handTileCounts` | `PlayerPublicView.HandCount` | `closedTiles+drawn` 张数 | 是 |
| `myClosedTiles` | `PrivatePlayerView.ClosedTiles` | `Hand` 物理槽减 drawn | 是 |
| `myDrawnTile` | `PrivatePlayerView.DrawnTileId` | `DrawnTileEvidence` | 是 |
| `myAllClosedTiles` | closed + drawn | 顺序保持（closed 后 drawn） | 是 |
| `mySleeveTiles` | `TableOwnerPrivateView.SleeveTiles` | V5 把袖牌放 owner-only 扩展，不放 `PlayerSnapshotView.Private` | 是（经 owner 扩展） |
| `myFuriten` | `PrivatePlayerView.Furiten` | V4 三布尔 → V5 单布尔；本 fixture 全 false | 是（isFuriten） |
| `myDiscardHistory` | `PrivatePlayerView.DiscardHistory` | 列表保持 | 是 |
| `lastDiscard` | `PublicBoardView.LastDiscard.TileId` | 牌 ID | 是 |
| `lastDiscardPlayer` | V5 board 无显式字段 | 从“哪一席 river 含该唯一物理牌”推导 | 是（推导后） |
| `legalActions` | `DecisionView`（P3.3 输入） | 形状不同；不做伪造映射 | 否，UNREPRESENTABLE |
| 私发/私摸/岭上 | `ProtocolEventType.TileDealt/TileDrawn/RinshanDraw` private | owner seat 才能投影 | 是（逐席） |
| 表宝/里宝 | `DoraRevealed` + `isUraDora` | 里宝额外受 `SettlementBatch && RevealSettlementUra` 约束 | 是（逐席/观战） |
| 公开弃牌/CHI | `TileDiscarded/ChiDeclared` public | 牌 ID 保持 | 是 |
| `ScoreBreakdown.uraTiles/uraHits` | 无 V5 字段 | `TableProjectedEvent` 仅 metadata | 否，BLOCKED（D-01-02） |
| V4 `PublicSessionSummary` | V5 `PublicSessionSummary` | 只有 `finished` 有同义字段 | 仅 finished=false |
| V4 王牌列表/里宝指示牌 | V5 不进入 board/player view | 结构上不可达 | 负例：逐值断言不出现 |

</details>

<details>
<summary><b>事件映射（V4 class → V5 ProtocolEventType）</b></summary>

`TileDealt→TileDealt`、`TileDrawn→TileDrawn`、`RinshanDraw→RinshanDraw`、`DoraRevealed(isUraDora)→DoraRevealed`、`TileDiscarded→TileDiscarded`、`ChiDeclared→ChiDeclared`、`PonDeclared→PonDeclared`、`KanDeclared→KanDeclared`、`RiichiDeclared→RiichiDeclared`、`DrawGame→DrawGame`、`ScoreSettled→ScoreSettled`、`RoundEnded→RoundEnded`、`MatchEnded→MatchEnded`、`CheatPointsChanged→CheatPointsChanged`（V5 private）。
V4 的 `settlementEvents` 语义映射为 V5 `TableProjectionPolicy(settlementBatch=true, revealSettlementUra=...)`；V4 未传 settlement 批时对应 V5 `settlementBatch=false`。

</details>

---

## 6. 字段分类（完整）

| 类别 | 字段 |
|---|---|
| INPUT | `state.roundId/phase/roundWind/roundNumber/honbaSticks/riichiSticks/dealerSeat/currentPlayer`、`scores[]`、`riichiDeclared[]`、`doraIndicators[]`、`uraDoraIndicators[]`、`placements[136]`、`melds[]`、`discards[][]`、`hands[]`、`lastDiscard`、`privateCheat`、`events[]`、`viewerSeat`、`legalActions`、`settlementEvents`、`revealSettlementUra`、policy flags |
| OUTPUT | `expected.snapshot.*`、`expected.seatViews.*`、`updateVisibleEventsAfterProducerClear`、`visibility.*`、`board.*`、`session.lifecycle/scores/roundRecords/finalResult`、`settlement.unauthorized/authorized`、`tsumoBreakdown.*`、`spectatorBoard.*`、`hiddenTileIds.*` |
| STRUCTURE | 136 唯一实体/0..135 全实体集、唯一位置、王牌 14、dora 在 slot≥4、wall index<WallEndExclusive、四 scores/四座位、case ID 唯一、G 行映射、V5 state_version/event_seq/cursor |
| META | schema、generatorVersion、rulesetVersion、generator、generatedByCommand(+Posix)、v4SourceCommit、v5BaselineCommit、v4ProductionTreeClean、trackedModifiedFiles、taskPaths、targetIds、caseCount、clockUsed、implicitRandomUsed、sourceHashes、exporterSource、renderSha256LfNormalized |
| SOURCE_ONLY | `v4WallPointer`、`v4WallEndExclusive`、`v4RinshanPointer`、`kanCount`、`privateCheat`（V4 PlayerView 无字段）、`v4DeadWall`、`drawSource`、`discardFlags`、`legalActions` 说明、`sessionUpdateWholesaleCopy`、`v4CheatPointsChangedVisibilityCounts`、`tsumoBreakdown` |
| UNREPRESENTABLE | V4 `legalActions`→V5 Decision；V4 `mySleeveTiles`→owner extension；V4 未过滤 `SessionUpdate.visibleEvents`→V5 无未过滤客户端列表；V4 session summary 全形状；V4 `ScoreBreakdown`；V4 暗杠公开身份 |

---

## 7. C# 测试与断言

`VisibilityGoldenTests` 共 8 个 `[Fact]`：

| 测试 | 真实调用 | 断言 |
|---|---|---|
| `GoldenMetaProvenanceAndCaseIdentityAreReal` | 读取 fixture + 重算 SHA | schema/版本/4 case/唯一 ID/G 行/taskPaths/命令/来源哈希/raw+LF 分离/render hash/8 条 knownDivergences 完整 |
| `G057aSnapshotDeepCopyMatchesV4` | `ProjectPlayerSnapshot` | V4 快照逐值；清空源 placement/scores/dora 后投影不变 |
| `G057bEventVisibilityMatchesV4` | `TableEventProjection.ProjectMany` | 四席未授权/授权逐事件类型/seat/tileIds；V5 千术流水仅 owner（V5-only，D-01-01） |
| `G057cPublicProjectionBoardAndWhitelistMatchV4` | `ProjectPublicSnapshot` + `ProjectMany` | 公开 board 逐值；私发/私摸/岭上/千术/未授权里宝被丢弃；授权里宝保留；metadata-only settlement 记录无 payload |
| `G057cPublicProjectionIsDefensivelyCopiedAndReadOnly` | 同上 | 源 placement/scores/dora 变更不影响投影；board/clocks/event tiles 只读；投影事件复制 producer 数组 |
| `G057dSeatViewsAndOwnerExtensionMatchV4` | `ProjectPlayerSnapshot` ×3 + `ProjectOwnerPrivate` | 三席逐值；owner1 袖牌/余 990/流水/CHI；owner0 无袖牌/余 1000/空流水 |
| `G057dNeighbourAndSpectatorNeverSeeHiddenIdentities` | 同上 + `ProjectPublicSnapshot` | seat0/观战不含 seat1 暗牌/袖牌/王牌顺序/里宝；seat1 自己可见；千术 entry/余额不泄漏 |
| `V5ProjectionCollectionsAreReadOnlyAndCopied` | 同上 | 集合 mutation 抛 `NotSupportedException`；源清空后不变；暗杠事件观战无 4 张身份、owner 有 |

负例不是 fixture 内部自洽或“导出 true”：每条都先由 V4 真实调用写入 expected，再由 V5 生产投影计算 actual，逐字段比较。

---

## 8. 测试、计数、exit

命令均在 worktree 根目录、`D:/Unity/dotnet/dotnet.exe`、临时目录 `build/tmp-vis` 下实跑。

| 类别 | 命令（摘要） | exit | 准确计数 |
|---|---|---|---|
| 新鲜 Release solution | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental` | 0 | 0 warning / 0 error |
| TableRuntime.Tests 依赖构建 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/... -c Release` | 0 | 0 warning / 0 error |
| C# 新 golden 定向 | `...Mahjong.TableRuntime.Tests.dll -class ...VisibilityGoldenTests` | 0 | Total=8, Errors=0, Failed=0, Skipped=0, NotRun=0 |
| TableRuntime 全量 | 同一 DLL 无 class 过滤 | 0 | Total=373, Errors=0, Failed=0, Skipped=0, NotRun=0 |
| Domain 定向 | `-class Mahjong.Domain.Tests.TilePositionMapTests` | 0 | Total=59, Failed=0, Skipped=0 |
| Domain 定向 | `-class Mahjong.Domain.Tests.TileMovementGoldenTests` | 0 | Total=10, Failed=0, Skipped=0 |
| Domain 定向 | `-class Mahjong.Domain.Tests.CheatVisibilityTests` | 0 | Total=10, Failed=0, Skipped=0 |
| Contracts 定向 | `-class Mahjong.Protocol.Tests.ProjectionWireMapperTests` | 0 | Total=16, Failed=0, Skipped=0 |
| Contracts 定向 | `-class Mahjong.Protocol.Tests.ProtocolContractTests` | 0 | Total=30, Failed=0, Skipped=0 |
| Gradle exporter | `:engine:test --tests ...V5VisibilityGoldenExporterTest` | 0 | tests=1, failures=0, errors=0, skipped=0 |
| Gradle 会话契约 | `--tests ...SessionSnapshotContractTest` | 0 | tests=8, failures=0, errors=0, skipped=0 |
| Gradle 公开投影 | `--tests ...PublicProjectionTest` | 0 | tests=12, failures=0, errors=0, skipped=0 |
| Probe 全量 | `powershell -File tools/v5-golden/probe-visibility-binding.ps1` | 0 | 26 native runs；baseline 8/0/0/0/0；20 FAIL 负例均 exit1/Failed≥1；5 PASS 正例；fixture 前后 SHA 相同 |
| Probe self-negative | 同脚本 `-SelfNegative` | 1（基线绿后故意） | baseline Total=8/Failed=0，随后 exit1 |

注意：最初一次 Contracts 命令用了错误 namespace，得到 `Total=0`（exit 0）；已改为 `Mahjong.Protocol.Tests` 重跑，上表为修正后的真实计数。`Total=0` 不作为通过。

---

## 9. Probe 负例矩阵（26 native runs）

每个 native run 统一校验 `exit`、`Total=8`、`Errors=0`、`Skipped=0`、`Not Run=0`；PASS 要求 Failed=0/exit0，FAIL 要求 Failed≥1/exit1。计数门自身有 12 个坏样例 + 2 个好样例自检；self-negative 在绿色 baseline 后让 driver 自身 exit1。

| # | 名称 | 类别 | 期望 | 实际 |
|---|---|---|---|---|
| 1 | `negative-input-placement-tile` | INPUT | FAIL | exit1 Failed=1 |
| 2 | `negative-input-event-owner-seat` | INPUT | FAIL | exit1 Failed=1 |
| 3 | `negative-output-snapshot-score` | OUTPUT | FAIL | exit1 Failed=1 |
| 4 | `negative-output-hand-count` | OUTPUT | FAIL | exit1 Failed=1 |
| 5 | `negative-output-board-dora` | OUTPUT | FAIL | exit1 Failed=1 |
| 6 | `negative-output-event-tile` | OUTPUT | FAIL | exit1 Failed=1 |
| 7 | `negative-output-event-dropped` | OUTPUT | FAIL | exit1 Failed=1 |
| 8 | `negative-output-wall-remaining` | OUTPUT | FAIL | exit1 Failed=1 |
| 9 | `negative-output-last-discard` | OUTPUT | FAIL | exit1 Failed=1 |
| 10 | `negative-privacy-opponent-id-public` | PRIVACY | FAIL | exit1 Failed=1 |
| 11 | `negative-privacy-public-dora-injection` | PRIVACY | FAIL | exit1 Failed=1 |
| 12 | `negative-privacy-ura-injection` | PRIVACY | FAIL | exit1 Failed=1 |
| 13 | `negative-privacy-cheat-balance` | PRIVACY | FAIL | exit1 Failed=1 |
| 14 | `negative-structure-duplicate-case` | STRUCTURE | FAIL | exit1 Failed=1 |
| 15 | `negative-structure-remove-case` | STRUCTURE | FAIL | exit1 Failed=4 |
| 16 | `negative-structure-extra-task-path` | STRUCTURE | FAIL | exit1 Failed=1 |
| 17 | `negative-meta-schema` | META | FAIL | exit1 Failed=1 |
| 18 | `negative-meta-source-hash` | META | FAIL | exit1 Failed=1 |
| 19 | `negative-meta-render-hash` | META | FAIL | exit1 Failed=1 |
| 20 | `negative-meta-baseline-commit` | META | FAIL | exit1 Failed=1 |
| 21 | `positive-source-only-legal-actions` | SOURCE_ONLY | PASS | exit0 Failed=0 |
| 22 | `positive-source-only-dead-wall` | SOURCE_ONLY | PASS | exit0 Failed=0 |
| 23 | `positive-placement-order` | STRUCTURE/表示 | PASS | exit0 Failed=0 |
| 24 | `positive-scope-note` | META/非比较 | PASS | exit0 Failed=0 |
| 25 | `positive-crlf-fixture` | 表示/行尾 | PASS | exit0 Failed=0 |

第 26 个是 baseline。未授权公开/他座暗牌/里宝注入/千术余额均为**真实 FAIL**；表示顺序/SOURCE_ONLY/CRLF 正控制保持 PASS。probe 只在 `build/visibility-binding-probe/<runId>/tmp` 的独占副本做单字段变异；原始 fixture 前后 SHA 相同；递归删除前校验解析路径等于本 run 的 `.../<runId>/tmp`。

---

## 10. 缺陷与差异（一次性完整清单）

严重级别分布：**BLOCKER 0；MAJOR 2；MINOR 4**。
其中 D-01-01 是 V4 既有隐私差异（本任务 test-only，不能改 V4）；D-01-02 是本 V5 组件的字段缺口，标 BLOCKED。

```text
[D-01-01]
严重级别：MAJOR
位置：engine/src/main/kotlin/com/majiang/session/SessionUpdate.kt:85-90；
      dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs:594-614
复现：$env:V5_VISIBILITY_GOLDEN_EXPORT='1'; .\gradlew.bat :engine:test --tests "com.majiang.projection.V5VisibilityGoldenExporterTest" --rerun-tasks --console=plain
      D:\Unity\dotnet\dotnet.exe dotnet\tests\Mahjong.TableRuntime.Tests\bin\Release\net8.0\Mahjong.TableRuntime.Tests.dll -class Mahjong.TableRuntime.Tests.VisibilityGoldenTests -noColor -noLogo
实际：V4 filterVisibleEvents 对 CheatPointsChanged 走 else 分支，seat0..3 实测各收到 1 条
      （fixture sourceOnly.v4CheatPointsChangedVisibilityCounts=[1,1,1,1]）；
      V5 实测仅 owner seat 收到，其他 3 席与观战为 null。
期望：千术点数流水只对 owner（观战不得见）；不能把 V4 的 else 分支移植为 V5 公开事件。
证据：tests/v5-golden/visibility-v1.json（sourceOnly 字段）；
      build/visibility-exporter-export5.log SHA-256 6b13ce8f202c7376c805e8443f1dfd85bf1bf07d9635832361a9dc9787e609c9；
      build/visibility-csharp-targeted-final3.log SHA-256 daa49afd90804014a7bb7a6d345b51c8f923bc280e3507e031843f7d92f1e37a
修复要求：V4 冻结不修；V5 保持 ProtocolEventPolicy 的 private 分类，P3.2 后续不得放宽。
验收断言：上述 C# 定向仍 8/8 通过，且 G057b 中 owner-only/其他席 null 断言保持；若 V4 后续修复，
      fixture 的 [1,1,1,1] 必须作为新信息显式更新，不能静默改 expected。
```

```text
[D-01-02]
严重级别：MAJOR
位置：dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs:314-551；
      dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs 的 ProtocolEventRecord/PublicMatchView
复现：同上 C# 定向；查看 fixture expected.tsumoBreakdown 与 G057cPublicProjectionBoardAndWhitelistMatchV4。
实际：V4 未授权时 breakdown.dora.uraTiles 为空、uraHits=0，授权后各 1；
      V5 TableProjectedEvent 是 metadata-only，没有 ScoreBreakdown 字段，无法逐值比较。
期望：V5 事件 payload 至少能表达公开计分/里宝剥除结果（由 P2.1 冻结字段），不得用同名假枚举替代。
证据：fixture expected.tsumoBreakdown；build/visibility-csharp-targeted-final3.log（同上 SHA）。
修复要求：P2.1 Contracts/Wire owner 增加显式 score breakdown payload（字段号另行冻结）；
      本组件不新增 V5 生产架构、不伪造字段。
验收断言：未来实现后，对同一 fixture 的 uraTiles/uraHits/indicatorTiles/totalPoints 逐值比较；
      在实现前该项保持 BLOCKED，不计入本组件 PASS 分母。
```

```text
[D-01-03]
严重级别：MINOR
位置：engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:222-244；
      dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs:717-757
复现：G057c 的 expected.session 与 V5 PublicSessionSummary 对比。
实际：V4 有 lifecycle/scores/roundRecords/finalResult；V5 只有 finished/finishReason/finalScores/rankings。
期望：活跃局只比较 finished=false；roundRecords 等由后续 P2.1/P3.2 明确定义后再绑定。
证据：fixture expected.session；G057c 测试。
修复要求：P2.1/P3.2 owner 决定是否补 roundRecords；在此之前不得把 V4 summary 整体塞进 V5。
验收断言：补字段后新增逐值断言；当前只允许 finished=false 计为对齐。
```

```text
[D-01-04]
严重级别：MINOR
位置：engine/src/main/kotlin/com/majiang/session/PlayerView.kt:124-142；
      dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs:887-911
复现：G057a fixture expected.snapshot.legalActions 有 DECLARE_PON[60,64]；V5 Private.Decision 由 P3.3 输入。
实际：V4 深拷贝 legalActions 通过；V5 没有从合法动作快照到 DecisionView 的同形映射。
期望：不伪造 GameAction→LegalActionView；V4 深拷贝只作为 V4 证据。
证据：fixture VIS-G057A-SNAPSHOT-COPY；sourceOnly.legalActions。
修复要求：P3.3 owner；本组件不实现 session/legal-action 迁移。
验收断言：P3.3 提供 DecisionView 后，另行建立动作映射样本；当前不计入 C# 对齐。
```

```text
[D-01-05]
严重级别：MINOR
位置：engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:482-506；
      dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs:1009-1017
复现：对比 V4 CLOSED_KAN 公开 meld 与 V5 公开 board 的 concealed placeholder。
实际：V4 PublicBoardView 发布暗杠四张身份；V5 公开 board 跳过四张身份（仅隐藏占位/数量）。
期望：V5 的更严格隐私策略；本 fixture 不含暗杠，避免把差异伪装成 PASS。
证据：knownDivergences VG-D03；V5 暗杠事件测试（观战空身份、owner 四张）。
修复要求：P3.2 owner 确认 V5 策略；若要 V4 对齐必须由产品裁决，不得放宽 V5。
验收断言：新增暗杠 board 样本时，明确期望 V5 隐藏，不能要求 V4/V5 身份相等。
```

```text
[D-01-06]
严重级别：MINOR
位置：engine/src/main/kotlin/com/majiang/session/PlayerView.kt:145；
      dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs:1183-1190
复现：G057d seat1 expected.mySleeveTiles 与 V5 PlayerSnapshotView.Private/TableOwnerPrivateView 对比。
实际：V4 把袖牌放在 PlayerView；V5 放在 owner-only 扩展，PlayerSnapshotView.Private.SleeveTiles 为空。
期望：owner 经 ProjectOwnerPrivate 可见；他座/观战不可见（本测试逐值验证）。
证据：fixture VIS-G057D-SEAT-VIEW-ISOLATION；G057d owner 测试。
修复要求：P3.2 owner 保持 owner 扩展，不回填到公共/私有大快照。
验收断言：新增 owner 扩展相关测试保持 case0 空、case1 有、观战无。
```

**差异（非缺陷，已映射/规范化，不计入缺陷数）**：

- V4 活牌山 pointer 语义 → V5 稠密 wall：只比较 `wallRemaining`，原 pointer 为 SOURCE_ONLY（VG-D07）。
- V4 弃牌 turn/flags 未持久化 → V5 `turnNumber=index`、两 flag=false；只比较牌 ID/顺序。
- V4 `Int` 分数 → V5 `long`；数值保持。
- V5 `lastDiscardPlayer` 无显式字段 → 用“唯一物理牌所在 river”推导。
- 暗杠事件：V5 观战 tileIds 为空、owner 四张（与 D-01-05 同源）。

---

## 11. 证据日志（实际文件 SHA-256）

| 日志 | SHA-256 |
|---|---|
| `build/visibility-solution-release-build.log` | `0c6a5e8bd714613994197fc4b813052151496a7f40fd71b1c3de76fa378bb432` |
| `build/visibility-tableruntime-build2.log` | `85d0714be424d47759cf7c716c284514163b4c9650aec5e7e6833ac47815fa13` |
| `build/visibility-csharp-targeted-final3.log` | `daa49afd90804014a7bb7a6d345b51c8f923bc280e3507e031843f7d92f1e37a` |
| `build/visibility-tableruntime-full-final3.log` | `5c574b8b95850d27e21d0bcf48f27a198093947ddd967317ca6851cbbab2b836` |
| `build/visibility-domain-TilePositionMapTests.log` | `e89aa309251b9d8fb2eb7fc2a221379262f9add6dd228c201d2d2c8a3695baaf` |
| `build/visibility-domain-TileMovementGoldenTests.log` | `db9a55ecfccf69d67a33c30921fb2799d9339ea07055dc763440d7c0e766831a` |
| `build/visibility-domain-CheatVisibilityTests.log` | `74f3c9bd930c252c488ef75064dc56136cbc6eb90a56e8fc8a29f31c9deef68d` |
| `build/visibility-contracts-ProjectionWireMapperTests.log` | `61a9a3e204e9dca93bdbbff3ac46141052c34b68ee178a52c56f1d102c97f6ed` |
| `build/visibility-contracts-ProtocolContractTests.log` | `ed3e8a25f046acf3fc889dd91a792b2048148a66396d70dfaef63ae2b1f469f0` |
| `build/visibility-gradle-targeted-final2.log` | `b30320b0bad62e7c5e17e51fabcf179a0a52cfd01af58ffc765957018e93f6ba` |
| `build/visibility-probe-final3.log` | `e2688e206e08763765adc53c3a9f4215845fe0fca444c8a9fd7614ff4fdba3b8` |
| `build/visibility-probe-selfneg2.log` | `b14841890a8b03ead03161ca624af7914ca1a6e90c8958b935fbf86b92e7ea18` |
| `build/visibility-binding-probe/20261025T091255Z-46476/results.csv` | `771145385cb743602114e4b6fff36fcd051d9d6b3916f06015cf1164a716645b` |
| 同上 `baseline.log` | `f29b096ac28cd2b9812a63a71efac44918caf0731706b79892c747a3abe8a43e` |
| 同上 `negative-privacy-cheat-balance.log` | `5209ea34118869d7fdf69cb888fd03a34d2658220259f840e806dc74352e0a7d` |
| 同上 `negative-meta-render-hash.log` | `8b490858fd4eed22fedb9aad8cd8367118f104e5b057570a8091e51d464e7907` |

probe evidence 目录：`build/visibility-binding-probe/20261025T091255Z-46476/`（26 个 native log + `results.csv`；`tmp` 已清理）。

---

## 12. 状态与边界

- 本组件 **PASS 仅限已实际对齐的可比字段**；D-01-02 的 ScoreBreakdown 与 D-01-03/04/05/06 的模型差异不计入 PASS 分母。
- **G1 仍未批准**：本任务不导出全部 73 例，不以 G-057a..d 作为 G1 新门槛，也不修改冻结 catalog/manifest。
- **G5 未完成**：没有平台源码变更，没有重跑 PG/WSS/Unity/IL2CPP/Android；本报告不声称平台通过。
- **V4 仍是唯一生产架构**；本轮没有 push、没有创建 PR、没有触发 CI、没有改默认分支、没有全局环境/部署变更。
- caller 复核/隔离集成后使用唯一 PR19 `feat/v5-1-integration → v5` / merge-back；本任务不自行合并、不推送。
