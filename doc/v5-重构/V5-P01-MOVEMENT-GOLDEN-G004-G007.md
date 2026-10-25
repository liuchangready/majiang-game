# V5 P0.1 运动黄金样本 G-004..G-007 导出与 C# 行为绑定

- 日期：2026-10-25
- 工作树：`D:/majiang-wt/movement-golden-g004-g007`
- 分支：`wt/v5-movement-golden-g004-g007`
- 冻结 base：`d6bc69fa930d005b355a1273ec732fbdf82bbe2f`（生成时 `git rev-parse HEAD` 与唯一 ancestry base 一致）
- V4 生产架构：唯一生产基线；**G1 仍 BLOCKED，G5 未完成**。本报告只证明 G-004..G-007 的新 exporter 与 C# 定向绑定，不证明 73 例全量、G1、G5 或平台验收。

---

## 0. 结论先行

| 项目 | 结果 | 证据 |
|---|---|---|
| V4 新增 movement exporter | **PASS** | `V5TileMovementGoldenExporterTest` 1/0/0；输出 `tests/v5-golden/movement-v1.json`，8 cases / 22 steps |
| 导出确定性（同输入两次） | **PASS** | export-9 / export-10 均为 `C2D5469E…7C13`，逐字节相同 |
| V4 `TileMovementContractTest` | **PASS** | 10/0/0；XML SHA `4BE16568…C1D4` |
| V4 exporter inert 运行 | **PASS** | 1/0/0；XML SHA `9712B925…CDA4` |
| C# Release build | **PASS** | `Mahjong.Domain.Tests` exit 0，0 warnings / 0 errors |
| C# 新 golden 定向 | **PASS** | 10/0/0/0/0（Total/Errors/Failed/Skipped/Not Run） |
| C# Domain 全量 | **PASS** | 480/0/0，其中 10 为本任务新增 `[Fact]`；未单独重跑 470 基线 |
| 变异 probe | **PASS** | baseline 10/0/0/0/0；INPUT/OUTPUT/STRUCTURE/META 四类负例均 exit 1 + Failed 1；4 个正控制 PASS（含 CRLF-only 源行尾）；`-SelfNegative` exit 1 |
| 既有 catalog 校验 | **PASS（只证旧目录未改）** | `fixtures=5 requirements=73 cases=10382 facts=42`，exit 0 |
| 既有五个 fixture | **未改** | 五个 SHA-256 与 manifest 记录逐一致 |
| G1 / G5 / 平台 / PG / Unity / 远端 CI | **NOT_RUN** | 不在本任务范围；不得由本报告推断 |

本任务没有修改任何 V4 生产源码、C# `Mahjong.Domain` 生产代码、csproj/solution、manifest、catalog、既有五个 fixture、`CLAUDE.md`、`PROMPTS.md`、任务书/协议、Unity 环境/Resources/Burst。没有 push、创建 PR、触发远端 CI 或修改默认分支。

---

## 1. 授权文件与提交边界

本次只创建以下五个文件：

1. `engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt`
2. `tests/v5-golden/movement-v1.json`
3. `dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs`
4. `tools/v5-golden/probe-movement-binding.ps1`
5. `doc/v5-重构/V5-P01-MOVEMENT-GOLDEN-G004-G007.md`（本文件）

生成时 `git status --porcelain` 只有任务文件；`git diff --name-only HEAD` 为空（没有 tracked 生产文件改动）。既有 `physical-v1.json`、`scoring-v1.json`、`round-v1.json`、`cheat-v1.json`、`shuffle-v1.json` 的 SHA-256 仍分别为：

| fixture | SHA-256 |
|---|---|
| `physical-v1.json` | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` |
| `scoring-v1.json` | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` |
| `round-v1.json` | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` |
| `cheat-v1.json` | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |
| `shuffle-v1.json` | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` |

`catalog-v1.json` 校验器仍输出 `sha256=16F7FD7F980398388E4E5DA45A3D5FB755C19990915F403987094617E90E891B`，覆盖仍是 5 个旧 fixture；它**不能**作为新增 `movement-v1.json` 完整性的证明。

---

## 2. 新增 fixture 与 case crosswalk

- 文件：`tests/v5-golden/movement-v1.json`
- 大小：1,278,595 bytes
- SHA-256：`C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13`
- schema：`v5-movement-golden-v1`；`generatorVersion=1`；`rulesetVersion=1`；`v4Commit=d6bc69fa930d005b355a1273ec732fbdf82bbe2f`；`v4ProductionTreeClean=true`；`clockUsed=false`；`implicitRandomUsed=false`。
- case/step：**8 cases / 22 steps**；rejection steps：2。

| case ID | G 行 | 真实 V4 调用 | 实际结果 |
|---|---|---|---|
| `MV-G004-DRAW-DISCARD` | G-004 | `TestTableBuilder.create(phase=DRAW)`；`draw(0)`；`discard(0, top)` | 两 step；draw 返回 T66；discard 后 river/discardHistory 为 T66；136 守恒 |
| `MV-G005-PON` | G-005 | Pin5 c0/c1 在手、c3 在 seat2 river；`pon(0, T91, 2, [T88,T89])` | 三张进入 MELD，seat2 river 清空，`lastDiscard` 原始值变 stale |
| `MV-G005-CHI` | G-005 | Man2 c2(T6)/Man3 c2(T10) 在手、Man4 c2(T14) 在 seat3 river；`chi(0, T14, 3, [T6,T10])` | 三张进入 MELD，源 river 清空 |
| `MV-G006-CLOSED-KAN-RINSHAN` | G-006 | Sou8 c0..c3 在手；真实 `draw(0)` 得到无关 T62；`closedKan(0, four)`；`drawRinshan(0)` | 无关摸牌保留；`drawnTile=null`；rinshan T48；dead-wall replacement +1；dora=T52 未变 |
| `MV-G006-ADDED-KAN-RINSHAN` | G-006 | Pin6 c0..c2 在 PON；`wallPrefix=[c3]`；真实 `draw(0)` 得 T95；`addedKan(0,T95)`；`drawRinshan(0)` | 第四张只消耗一次；rinshan T49；replacement +1；dora=T53 未变 |
| `MV-G006-OPEN-KAN-RINSHAN` | G-006 | Sou7 c0..c2 在手、c3 在 seat2 river；`openKan(0,T63,2,three)`；`drawRinshan(0)` | 弃牌移出 river；四张入 MELD；rinshan T49；dora=T53 未变 |
| `MV-G007-DUPLICATE-PON-REJECTION` | G-007 | Man5 c0(T16) 在手、c3(T19) 在 seat2 river；`pon(..., [T16,T16])` | `java.lang.IllegalArgumentException: Pon tiles contain duplicate copies...`；前后 136 placements 完全相等 |
| `MV-G007-FIFTH-RINSHAN-REJECTION` | G-007 | 同一 state 连续 4 次 `drawRinshan + discard`，再第 5 次 `drawRinshan` | 4 次成功，replacement=4、rinshanPointer=4；第 5 次 `java.lang.IllegalArgumentException: Rinshan tiles exhausted: pointer=4...`；前后 136 placements 完全相等；dora=T56 未被吃掉 |

每个 case 的 `initialState` 与每一步的 `before/after` 都包含：

- 完整 136 `placements`（`tileId/zone/seat/index`），并断言 136 唯一身份、位置唯一、标准集一致；
- `wallPointer/wallEndExclusive/rinshanPointer`；
- 四家 `hands`（排序后的实体牌 ID 列表）；
- 四家 `rivers` 与 `discardHistory`；
- `melds`（座位、组号、类型、实体牌、fromPlayer、calledTile）；
- `deadWallBySlot`（14 槽，已摸走的槽为 null）、`deadWallList`（V4 原始 14 张列表）、`replacements`；
- `doraIndicators`；
- `lastDiscard` / `drawnTile`（含 source）/ `rinshanDraw`；
- V4 原始 `v4LastDiscard` 与 `v4TotalKanCount`（SOURCE_ONLY）。

dora 指示牌在 closed/added/open kan 与第五次岭上 case 中真实设为 `deadWall[4]`（分别为 T52/T53/T53/T56），并在每步后导出；没有只写空列表冒充“dora 不被吃掉”。

---

## 3. V4 mutable index 与 C# slots 的明确映射

`TilePositionMap` 是 136 槽不可变 map；V4 是多个可变容器 + `TilePool` 账本。本任务没有修改 Domain/fixture 规则来“凑过”，映射如下：

| V4 | C# canonical | 验证方式 |
|---|---|---|
| `wall[index]`（`index < wallEndExclusive`，`TilePool=WALL`） | `Wall(index)` | 槽位逐张精确比较 |
| `deadWall[index]`（`TilePool=DEAD_WALL`，`index >= rinshanPointer`） | `DeadWall(index)` | 槽位逐张精确比较 |
| `deadWallReplacements[index]`（V4 pool 仍记 DEAD_WALL） | `DeadWallReplacement(index)` | 替换槽逐张精确比较 |
| 手牌 `allClosedTiles`（V4 是排序列表，无槽位） | `Hand(seat, 0..n-1)`（按 tileId 排序的 dense 槽） | 比较排序后的手牌集合；不硬比 V4 不存在的 hand slot |
| V4 `melds[meldIndex].tiles` | `Meld(seat, meldIndex*4 + tileId-sorted offset)` | 比较组号 + 排序后的实体牌；组内顺序不作为语义 |
| V4 `discards[slot]` | `Discard(seat, slot)` | 槽位/顺序精确比较 |
| V4 `deadWall` 中已被摸走的旧槽位 | **不输出为 DeadWall**；按 `TilePool` 实际位置归入 HAND/DISCARD | C# 要求 `index < rinshanPointer` 的 DeadWall 槽为空；导出器按 pool 实际位置映射 |
| V4 `lastDiscard` 被鸣牌/摸牌后仍指向旧牌 | canonical `lastDiscard` 按 C# 语义在 draw/call 后为 null；原始值保留在 `v4RawLastDiscard` | C# `LastDiscard` 与 canonical 比较；probe 的 SOURCE_ONLY 正控制证明原始值不被误用 |
| V4 `totalKanCount`（TileMovement 不维护） | C# `KanCount` 在杠时 +1 | 不纳入物理 diff；显式列为已知差异 |
| V4 `MeldType/fromPlayer/calledTile`、`discardHistory` | C# `TilePositionMap` 无对应字段 | 作为 SOURCE_ONLY / UNREPRESENTABLE 明列；不伪造相等 |

计数口径：V4 手牌是列表，C# 手牌槽是分配细节；因此手牌槽按 tileId 稳定排序后比较，wall/discard/meld/dead-wall/replacement 槽保持精确。没有比较不等价的 V4/C# 枚举序号或 hash。

---

## 4. C# 真实行为绑定

新增 `dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs`，只读取新增 fixture，调用现有生产方法：

- `TilePositionMap.FromPlacements(...)` 构建初始 136 槽；
- `DrawFromWall`、`Discard`、`Pon`、`Chi`、`ClosedKan`、`AddedKan`、`OpenKan`、`DrawReplacement`；
- 每个成功 step 比较：136 位置（zone/seat；非 hand/meld 还比 index）、pointers、`LastDiscard`、`DrawnTile`（含 `DrawSource`）、`LastRinshanDraw`、dora、hands、meld 物理组、rivers、replacements、有效王牌 14；
- 每个失败 step 比较：`TransitionResult.IsSuccess=false`、`State=null`、typed `Error.Code`（duplicate pon -> `InvalidMeldShape`；fifth rinshan -> `DeadWallExhausted`）、source map 的 136 放置完全不变；
- 断言 `map.Revision` 等于成功 step 数（失败不推进）；每个 expected state 都验证 136 唯一身份和有效王牌 14。

新增 10 个 `[Fact]`：

1. `GoldenFileCarriesRealV4ProvenanceAndEightCases`
2. `G004DrawAndDiscardReplayExactly`
3. `G005PonReplayTransfersThreeTiles`
4. `G005ChiReplayTransfersThreeTiles`
5. `G006ClosedKanAndRinshanReplay`
6. `G006AddedKanAndRinshanReplay`
7. `G006OpenKanAndRinshanReplay`
8. `G007DuplicatePonRejectsWithoutMutation`
9. `G007FifthRinshanRejectsWithoutMutation`
10. `MappingCoverageRecordsSourceOnlyAndUnrepresentableFields`

字段分类（fixture `fieldClassification` 与 C# 测试共同锁定）：

- `INPUT`：placements、pointers、hands、rivers、melds、deadWallBySlot、replacements、dora、lastDiscard、每步 action；
- `OUTPUT`：outcome、resultTileId、after placements/pointers/evidence/hands/melds/rivers/deadwall/replacements/dora；
- `STRUCTURE`：136、唯一 ID、唯一位置、有效王牌 14、指针范围、136 守恒；
- `META`：schema/generator/version/commit/source hashes/command；
- `SOURCE_ONLY`：V4 `totalKanCount`、`discardHistory`、meld type/from/called、`v4RawLastDiscard`、raw `deadWallList`；
- `UNREPRESENTABLE`：当前 C# map 没有 discardHistory、meld metadata、V4 totalKanCount；V4 双账本 vs C# 单账本不可等价注入。

C# 侧没有修改生产 `TilePositionMap`/`Domain`，也没有把不可表示的字段写成 PASS。若后续 G1 需要 meld 类型或 discard history 的跨运行时证据，最小后续责任是扩展 Domain map 的显式 schema（仍不读 V4 规则），由 P1.1 owner 评审；本任务不擅自扩 Domain。

---

## 5. 命令、exit、真实计数与原始日志

### 5.1 版本

- Gradle wrapper 9.7.1；Gradle JVM：`C:\Program Files\Android\Android Studio\jbr`（`java.exe` 未在 PATH，wrapper 使用 `JAVA_HOME`）。
- 项目 Kotlin 2.4.10；JDK 17。
- .NET SDK 8.0.426；Host 8.0.32；win-x64；Domain `netstandard2.1`/C# 9；测试 `net8.0`/C# 9。
- Windows 10.0.19045。

### 5.2 V4 导出（PASS）

```powershell
$env:V5_MOVEMENT_GOLDEN_EXPORT='1'
.\gradlew.bat :engine:test --tests "com.majiang.model.V5TileMovementGoldenExporterTest" --rerun-tasks --console=plain
```

- export-9：`BUILD SUCCESSFUL`，**exit 0**；`V5TileMovementGoldenExporterTest > exportMovementGoldenSamplesOrValidateInert() PASSED`。
- export-10（同输入立即再导出）：**exit 0**；fixture SHA 前后均为 `C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13`，逐字节相同。
- export-9 原始日志：`build/movement-golden/export-9.log`，SHA-256 `F5CE8FF97911CE7D0A609AC03A766DB61BDB722E6198322914250A577882C7C3`。
- export-10 原始日志：`build/movement-golden/export-10.log`，SHA-256 `5E3EA84B5231E7550389578998E851E038AAA6EE92AC838B059A916CC0A3C83C`。
- 生成时 fixture 的 `v4Commit` 为 `d6bc69fa930d005b355a1273ec732fbdf82bbe2f`；没有写墙钟时间、随机 seed 或手填 hash。

默认（不设 `V5_MOVEMENT_GOLDEN_EXPORT`）运行同一 exporter 仍会执行全部 8 个 case 的真实 V4 调用、`TestTableBuilder.assertPhysical`、`StateInvariants.assertValid` 与 canonical 结构校验，但**不写** `movement-v1.json`。这满足“默认可验证行为但不擅自写原 fixture”。

### 5.3 V4 定向测试（PASS）

```powershell
.\gradlew.bat :engine:test `
  --tests "com.majiang.model.TileMovementContractTest" `
  --tests "com.majiang.model.V5TileMovementGoldenExporterTest" `
  --rerun-tasks --console=plain
```

- **exit 0**。
- `TileMovementContractTest`：**10 tests / 0 failures / 0 errors / 0 skipped**。
- `V5TileMovementGoldenExporterTest`（inert）：**1 / 0 / 0 / 0**。
- 原始日志：`build/movement-golden/engine-targeted-3.log`，SHA-256 `6E2EF180836557CF3375EBA750861CFDAE75BF40F3666A31B0692B038EE4EADD`。
- XML：
  - `engine/build/test-results/test/TEST-com.majiang.model.TileMovementContractTest.xml`，SHA-256 `4BE16568960887E02B0C2AA9FD46514FB3DE0EF6C1DE6DEC41F266608DC0C1D4`；
  - `...V5TileMovementGoldenExporterTest.xml`，SHA-256 `9712B9257CC366CB3CB12B7E36AA54A9236E474F7C00DAC6675045A9575FCDA4`。

### 5.4 C# Release 构建与测试（PASS）

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class "Mahjong.Domain.Tests.TileMovementGoldenTests" -noColor -noLogo
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
```

- build：**exit 0**，0 warnings / 0 errors。
- 新 golden 定向：**Total 10 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0**，exit 0；日志 `build/movement-golden/targeted-3.log`。
- Domain 全量：**Passed 480 / Failed 0 / Skipped 0 / Total 480**，exit 0；日志 `build/movement-golden/domain-full-4.log`，SHA-256 `F0EA4143904674D4BFC407B22FBC70A7CB1215102EF7AB36C469330D93292AB3`。
- 全量 direct runner（生成 XML）：`Total: 480, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，exit 0；日志 `build/movement-golden/evidence/domain-full-direct-3.log`，SHA-256 `13E212D3046C8E3056E89E3C1D9F301D265D22335791C049A9445B6AF9D589C3`。
  - `domain-junit-3.xml`：`959A6C1BCDEA7984D07DC97973CC5931AD1E4A55433CE5358EF943EDDDB94485`；
  - `domain-3.trx`：`55D26D98F68068FED86669AC2D522D2C1E62CD303D5531C64E14C9FC97C4F54B`；
  - `domain-xunit-3.xml`：`214343581AA69E448693E0F37A52D8A4F1756982EDE3BF08EB19D9450BFE0393`。
- 480 中包含本任务 10 个新 Fact；本任务**没有**单独重跑 470 基线，因此不把 470 当作本轮实测。

### 5.5 变异 probe（PASS）

脚本：`tools/v5-golden/probe-movement-binding.ps1`（`#requires -Version 5.1`，Windows PowerShell 5.1 实跑）。脚本用 `git archive HEAD` 创建独占 TEMP 副本，再把当前三个任务文件叠加进副本；只在副本内变异 `movement-v1.json`，原工作树 fixture 前后 SHA 均为 `C2D5469E…7C13`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-movement-binding.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-movement-binding.ps1 -SelfNegative
```

最终正常运行（日志 `build/movement-golden/probe-4.log`，SHA `53D7A9E880838E5121C790D1A10578E00723AF2CD13F180C629E59D8C213D280`）：

| mutation | 类别 | 期望 | native exit | Total | Failed | Errors | Skipped | Not Run |
|---|---|---|---:|---:|---:|---:|---:|---:|
| baseline | baseline | PASS | 0 | 10 | 0 | 0 | 0 | 0 |
| `negative-input-action-seat` | INPUT | FAIL | 1 | 10 | 1 | 0 | 0 | 0 |
| `negative-output-wall-pointer` | OUTPUT | FAIL | 1 | 10 | 1 | 0 | 0 | 0 |
| `negative-structure-duplicate-position` | STRUCTURE | FAIL | 1 | 10 | 1 | 0 | 0 | 0 |
| `negative-meta-schema` | META | FAIL | 1 | 10 | 1 | 0 | 0 | 0 |
| `positive-source-only-kan-count` | SOURCE_ONLY | PASS | 0 | 10 | 0 | 0 | 0 | 0 |
| `positive-order-reversed-placements` | ORDER | PASS | 0 | 10 | 0 | 0 | 0 | 0 |
| `positive-order-swapped-hand-slots` | ORDER | PASS | 0 | 10 | 0 | 0 | 0 | 0 |
| `positive-line-ending-crlf` | LINE_ENDING | PASS | 0 | 10 | 0 | 0 | 0 | 0 |

- 最终 probe 证据目录：`build/movement-binding-probe/20261025T070635Z-36208/`，含 `baseline.log`、8 个 mutation/positive 日志、`build.log`、`results.csv`、`summary.txt`；`summary.txt` 记录原 fixture 前后哈希相同。
- `positive-line-ending-crlf` 在 TEMP 副本把 exporter 源从 LF 转成 CRLF，C# 测试走“raw 字节随行尾改变、LF-normalized 内容不变”的显式分支并 PASS；它证明 §6 的行尾口径不是只写文档。
- `-SelfNegative`：baseline 10/0/0/0/0 后主动 exit **1**；日志 `build/movement-golden/probe-selfnegative-4.log`，SHA `8C17D424AA9AF90448C51BFA2C17E1B37929C618DF545C70194EDF4E340DE4BC`；证据目录 `build/movement-binding-probe/20261025T070718Z-39216/`。
- probe 的负例不是“脚本自己断言失败”：每个负例都真实启动 native xUnit runner，exit 1 且 summary `Failed=1`；正控制真实 exit 0 且 `Failed=0`。probe 没有 skip、空测试或 Total=0。

### 5.6 既有 catalog 校验（PASS，但只证旧目录）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
```

`PASS catalog=... sha256=16F7FD7F...891B fixtures=5 requirements=73 cases=10382 random=10000 facts=42 coverage=full:17/partial:20/none:33/v5-only:3`，exit 0。

日志：`build/movement-golden/catalog-verify-2.log`，SHA `C2ADE3D94EE5400B48C8A7705BD6CBD0309C257F4DF2C5629B2676B96C6C2250`。

该结果只证明旧 catalog + 五个旧 fixture 未被本任务修改；**不**证明新增 `movement-v1.json` 已被 catalog 收录，也不证明 73 例完成。冻结的旧 manifest/catalog 仍把 G-004..G-007 标为 `pending/none`，本任务没有静默改它。

---

## 6. 确定性、源哈希与原始字节口径

fixture 的 `sourceHashes` 与 `exporterSource` 同时记录：

- `sha256RawBytes`：文件原始字节（Windows checkout 下是 CRLF）；
- `sha256LfNormalized`：UTF-8 文本把 CRLF 规范化为 LF 后的 SHA-256；
- `crlfCount`、`lfOnlyCount`、`lineEndings`（如实标 CRLF/LF/MIXED/NONE）。

C# `TileMovementGoldenTests` 对每个源文件**同时重算 raw 与 LF-normalized** 两个哈希。内容权威始终是 LF-normalized 哈希；当 checkout 行尾 profile 与 fixture 记录一致时，再严格比对 raw 字节哈希、raw 字节数和 CRLF/LF-only 计数。如果 Git `autocrlf`/checkout 只改变了行尾，测试会显式断言 raw 哈希与 raw 字节数已随 profile 改变，而不是把 LF 哈希冒充 raw 哈希。本次新增的四个源/脚本文件本身为 LF，所以报告中的 raw 与 LF 值相同；V4 生产源码为 CRLF，两个值不同，fixture 中已分别记录。

本任务文件：

| 文件 | bytes | SHA-256（raw = LF） |
|---|---:|---|
| `tests/v5-golden/movement-v1.json` | 1,278,595 | `C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13` |
| `engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt` | 62,520 | `654662DC97BC3B193523DC13426B762A52C76699B6E6577C3217BAF63C496CDC` |
| `dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs` | 38,947 | `1154390513270EC66750F0FF72D72F88F72AB4A34CB87B0ECD17E6D1168243B9` |
| `tools/v5-golden/probe-movement-binding.ps1` | 16,431 | `70520D4C90C3D9C07AD824C7BF5DD67B2250EC0B245CF7025328DE5130B1AC4E` |

fixture 记录的关键 V4 源（完整列表见 JSON）：

| 文件 | raw SHA-256 | LF-normalized SHA-256 |
|---|---|---|
| `TileMovement.kt` | `f89cf112dad6404b679002438f1a3f6a1a222d7e4377dc247c2580c15336a4e0` | `51f085bd0ad124c16b6a279d699012aaf9a848373cc2c26310211f3a4d7565f8` |
| `TileMovementContractTest.kt` | `365bbbcf6c74069d0f3703b44ace451319f6f5d67afc258c9fc325feb1e1b207` | `996143953d668a81607eae00a5f9376e1048fdd113d436b87865fd7f1d519e4f` |
| `StateInvariants.kt` | `f89c71ec8aff185955e77f48c06f5f163861ebdef6635f1b46ec8fb68ef52be2` | `db410243f699c19b944f243346e7dd5c0d9a41f117ecf4e342eea54f5c0d762e` |
| `TestTableBuilder.kt` | `850aac265db93f3ede7b6b96f04d02b58c6274fe41919840171d8c5927d854d1` | `45fe486e596a38ad696f7900a277886b7f6cc7a103e8717d299e9658cac5da0a` |

fixture 还记录 `Tile.kt`、`TileLocation.kt`、`TilePool.kt`、`Meld.kt`、`PlayerHand.kt`、`GameState.kt` 的 raw/LF 两套哈希。没有手填 expected hash；所有 expected 都来自本 exporter 对 V4 的真实调用。

---

## 7. 已知差异、边界与剩余缺口

1. **范围只到 G-004..G-007**：不是 73 例全量，不是 6/6 或 73/73；catalog 仍是旧状态。G-008（wall swap）及 G-009..G-012 等未在本任务新增导出。
2. **`KanCount` 不可直接比**：C# `TilePositionMap` 在杠时 `KanCount+1`；V4 `TileMovement.closedKan/addedKan/openKan` 不更新 `GameState.totalKanCount`（由应用层维护）。fixture 将 V4 值记入 `v4TotalKanCount`（SOURCE_ONLY），C# 测试不把它当物理 diff；`MappingCoverage...` 实测 C# `KanCount=1`、V4 记录仍为 0。
3. **Meld 元数据不可表示**：当前 C# map 只有 Meld 槽位，没有 `MeldType/fromPlayer/calledTile`。fixture 导出这些字段，C# 只验证物理组/牌；不声称类型跨运行时已绑定。最小后续责任：P1.1 owner 决定是否扩展 Domain schema。
4. **`discardHistory` 不可表示**：V4 保留被鸣走的 river 历史；C# `TilePositionMap` 只有当前 river。fixture 导出历史，C# 不比较；不伪造对应字段。
5. **`lastDiscard` 语义差异**：V4 `TileMovement.draw/drawRinshan` 不清 `lastDiscard`，鸣牌后也可能留下 stale 指向；C# 在成功 draw/call 后清空 evidence。canonical 采用 C# 语义，原始值保留为 `v4RawLastDiscard`；probe 的 SOURCE_ONLY 正控制证明消费方不误读原始字段。
6. **手牌/副露槽位 canonical 化**：V4 手牌是无 slot 的排序列表；C# hand slot 是分配细节。两侧按 tileId dense 排序比较；wall/discard/dead-wall/replacement 槽仍逐张精确。没有把 hand slot 差异写成失败，也没有复制 V4 规则到 C#。
7. **V4 dead wall 列表保留已摸走实体**：V4 `deadWall` 14 张列表在 `rinshanPointer` 前进后仍含旧实体；C# 要求已消费槽为空。导出器按 `TilePool` 实际位置归入 HAND/DISCARD，并保留 `deadWallList` 原始值作为 SOURCE_ONLY。
8. **`LastRinshanDraw` 是 V5/C# 证据**：V4 没有对应字段；fixture 只从成功 `drawRinshan` 动作派生，不是 V4 原生字段。
9. **受保护尾牌/袖里藏花路径未覆盖**：本批 case 没有 sleeve reservation/off-turn offer。V4 `wall.removeAt` 在受保护尾牌存在时的 slot 压缩与 C# “稳定 slot + wallEnd” 语义未在本批证明；这是后续 P1.1 movement 扩展的明确缺口，不是已通过项。
10. **异常类不追求同名**：V4 `IllegalArgumentException` 与 C# typed `TransitionErrorCode` 通过语义映射对照；C# 断言 `InvalidMeldShape`/`DeadWallExhausted`，不声称异常类等价。
11. **dora/ura**：本批 kan/fifth case 设置了真实 dora 指示牌并验证不被岭上消耗；没有导出 ura dora（本批不涉及里宝）。
12. **平台/环境未测**：PG、WSS、Unity、IL2CPP、Android/iOS、真机、跨物理网络、远端 CI、G1/G5 全量均 **NOT_RUN**。本报告不能替代这些证据。

---

## 8. 历史失败与修复（保留）

以下失败发生在实现过程中，均已保留日志；最终结果以 §0/§5 为准：

| 阶段 | 实际 | 处理 |
|---|---|---|
| C# build attempt 1 | exit 1，47 errors：xunit v3 的 `Assert.Equal(expected, actual, message)` 被解释成 comparer 重载；另有 `out MeldGroup` nullable 警告 | 改为本地 `Eq/SeqEq` 显式比较器 + nullable out；build attempt 2 exit 0 |
| Gradle export-5 | exit 1；`During DRAW phase, no player can hold drawnTile, but players [0] do` | 在 test harness 按已接受 `TileMovementContractTest` 设置 phase/currentPlayer 后再 audit；未改生产规则 |
| Gradle export-6 | exit 1；`During DISCARD, player 0 expected equivalent 13 tiles, but got 14 (actor=2)` | 同上，PON/CHI 也把 currentPlayer 设为行动者；export-7 起通过 |
| 早期 fixture 版本 | `lastDiscard` 在 draw/call 后的 canonical 值与 C# 清空语义不一致 | 增加显式 canonical 映射 `v4RawLastDiscard`（SOURCE_ONLY）；export-4 起 C# 定向通过 |

没有把失败日志删除或改写成 PASS；最终 fixture 是 export-9/10 的稳定产物。

---

## 9. 给 caller 的集成提示

- 本分支在 `d6bc69f` 上只新增五个授权文件；合并前用 `git show --stat --name-only <delivery-sha>` 复核，应恰好是这五个路径。
- `movement-v1.json` 的 `exporterSource.sha256LfNormalized` 应与 exporter 文件 LF 哈希 `654662DC…6CDC` 一致；C# 测试会独立重算。
- 本任务不修改 manifest/catalog；是否把 G-004..G-007 登记为 `exported` 由 caller 在独立 catalog 任务中决定，且不得改写本报告或旧 fixture。
- 本任务未 push/PR/触发远端 CI；PR19 的 `feat/v5-1-integration -> v5` 集成、独立复核与 merge-back 由 caller 所有。
- G1 仍 BLOCKED，G5 未完成；本交付不改变该结论。
