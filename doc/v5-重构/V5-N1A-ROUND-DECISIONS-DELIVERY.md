# V5 N-1a 交付：Domain 玩家决策补齐（Chi/Pon/明杠 claim/暗杠/立直）

日期：2026-10-25。任务：既有 P1.3 的 G1 补齐子任务 **N-1a**，不得改名为已接受的 P3.2/P3.3。
工作树：`D:/majiang-wt/n1a-round-decisions`，分支 `wt/v5-n1a-round-decisions`。
冻结基线：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（PR19 唯一最新已发布头）。
代码提交：`5f377c8a62d432b1bd6f474f8f08ca8f609bbabe`（本文档单独提交；文档提交见 `git log -1 --format=%H`）。
本批次未执行：push、建 PR、远端 CI、合并、改默认分支、全局信任/环境/部署。V4 仍是唯一生产架构。

> **结论边界（必读）**：本交付只完成 **N-1a 的 Domain 组件面**。它不宣布 G1 通过，不宣布 V5 迁移完成，不关闭 N-1b（出千执行层）、N-2（结算计算接线）、N-3（黄金全字段对齐）、N-1c（自动座位适配）或 SF-1。冻结头 `8ee22eb` 的 G1 结论仍是 **7 PASS / 2 FAIL**；G5 仍未完成。

---

## 1. 先更正报告中的“5 个缺失动作”

`V5-1-REVIEW.md` §7.1 N-1a 写“`RoundActionKind` 无 DeclareChi/DeclarePon/DeclareKan(claim)/DeclareRiichi/DeclareAgariYame”。本基线核实结果：

| 项 | 核实结果 | 证据 |
|---|---|---|
| `DeclareAgariYame` | **已存在，且不属于单局 Round 动作**：`MatchSession.ChooseDealerEnd(bool agariYame, ...)` 在 `SessionLifecycle.WaitingDealerChoice` 下处理整场局间选择；V4 `RoundStateMachine.prepare` 也明确拒绝把它送进单局 | `dotnet/src/Mahjong.Domain/MatchSession.cs:498`；`V5-0-RULE-CONTRACT.md` §3.1 第 13 行；V4 `LegalActionProvider.kt:35-41` |
| 真实 Domain 缺口 | **Chi、Pon、明杠 claim、暗杠、立直**，即 5 个 `RoundActionKind` 值、4 个决策族（吃 / 碰 / 杠 / 立直）。报告把“杠 claim”合并成一项，又漏掉 V4 支持的暗杠；把 AgariYame 误算进单局 | 本交付新增值 `DeclareChi=9`、`DeclarePon=10`、`DeclareOpenKan=11`、`DeclareClosedKan=12`、`DeclareRiichi=13`；`DeclareAddedKan=6` 原路径保留 |

因此本任务**没有**为凑数在终态 round 增加可复活的假 AgariYame 动作，也没有改动 `MatchSession` 的局间入口。`DealComplete`/`DrawTile`/`StartStacking` 仍是权威阶段自动权限，不新增玩家命令、不要求玩家负例。

---

## 2. 实际实现

### 2.1 枚举与值稳定性

- `RoundActionKind` 只追加 9..13，0..8 原值不变；持久化不得重排。
- `RoundEventType` 只追加 `RiichiDeclared=16`、`ChiDeclared=17`、`PonDeclared=18`，0..15 不变。
- 新增持久枚举：`RoundMeldKind`（Chi=1/Pon=2/OpenKan=3/ClosedKan=4/AddedKan=5）、`RoundClaimKind`（Pass=0/Ron=1/Chi=2/Pon=3/OpenKan=4）。
- `RoundResponseClaim` 从“Pass/Ron 二态”扩展为五态；Ron 仍携带 scoring-layer settlement，Chi/Pon/OpenKan 携带精确物理手牌。

### 2.2 合法性与状态转移

| 决策 | 实现的合法性（不只加 enum case） | 证据入口 |
|---|---|---|
| 吃 Chi | 仅 `(fromSeat+1)%4` 下家；仅数牌；窗口内、非抢杠；未立直；窗口目标牌必须等于 called tile；2 张手牌必须为同一玩家持有的不同物理实体，且与目标牌成连续顺子；物理移动、副露记录、事件、座次推进一次完成 | `RoundStateMachine.PrepareChi`（`:629`）、`TilePositionMap.Chi`、`RoundMeld` |
| 碰 Pon | 任意非打牌者；窗口内、非抢杠；未立直；2 张同种且不同实体；窗口目标牌精确匹配；完成后座次归鸣牌者并进入 DISCARD | `PreparePon`（`:679`）、`TilePositionMap.Pon` |
| 明杠 claim | 任意非打牌者；窗口内、非抢杠；未立直；3 张同种且不同实体；`KanCount<4`、`RinshanPointer<4`、活牌山>0；完成后开花杠、摸岭上、翻杠宝时序 | `PrepareOpenKan`（`:721`）、`CommitOpenKan`（`:1320`） |
| 暗杠 | 当前玩家、CHEAT_WINDOW/DISCARD、无窗口；4 张同种不同实体且全在手中；全局杠上限；立直后必须过 `RiichiKanValidator` 等价校验（摸牌参与、待牌集合不变、每张和牌分解结构不变） | `PrepareClosedKan`（`:792`）、`CanClosedKanAfterRiichi`（`:1682`） |
| 立直 | 门前（无明副露；暗杠不破门清）、未立直、点数≥1000、活牌山≥4、所打牌在手中且打后仍有未占满第 5 张的听牌；扣 1000 点、供托 +1、立直标志置位、打牌并开响应窗 | `PrepareRiichi`（`:870`）、`CanRiichiWithTile`（`:1649`） |
| 立直锁 | 立直后只能摸切 `Tiles.DrawnTile`；非摸切拒绝 `RiichiDiscardLocked`；立直后吃/碰/明杠拒绝 | `PrepareDiscard`、`PrepareChi/Pon/OpenKan` |

补充：

- **吃替/食替**：对 V4 源码执行 `git grep -ni "kuikae|食替|食い替"` 无业务命中（仅两份历史交付文档提到同类模糊匹配风险）。V4 没有“同种牌食替”实现，因此本任务**没有发明类型级禁食替**；实际执行的 V4 有效约束是“被鸣的物理牌已进入副露，不在手中，不能再打出”，由牌实体归属与 `TilePositionMap` 强制。若产品要类型级禁食替，应作为新 ruleset 显式决策，而不是在 N-1a 静默加规则。
- **多响**：按冻结规则只取一个赢家（无双响/三响）；优先级 `Ron=3 > Pon/OpenKan=2 > Chi=1 > Pass=0`；同级按 `(seat-from+4)%4` 近者胜（头跳）。实现见 `ResolveCompletedWindow`（`:1149`）、`SelectWinningClaim`（`:1202`）。
- **窗口不提前关闭**：每个合格座位只记录一次；低优先级先入队不会关闭窗口，只有全部合格座位提交后才裁决。`RoundResponseWindow.IsComplete` + `WithClaim` 保持。
- **无候选/全员 Pass**：普通窗口推进到下一摸；抢杠窗口全员 Pass 才补杠/摸岭上；被抢则本次杠不成立。
- **观战/座次**：响应窗只允许 `EligibleSeats` 内且未回应的座位；打牌者/杠者不能响应自己的牌。

### 2.3 杠后补牌与宝牌时序（按 V4 实测路径）

- 暗杠：先 `revealPendingKanDora`（清此前欠翻），再翻当前杠宝，发 `KanDeclared`，再摸岭上、发 `RinshanDrawn`。
- 明杠：先清此前欠翻，`pendingKanDoraCount++`，发 `KanDeclared`，摸岭上；当前杠宝在下一次打牌后翻（V4 `executeOpenKanCommit` 路径）。
- 加杠：先 CHANKAN_WINDOW；无人抢杠才 `pending++`、发 `KanDeclared`、摸岭上；被抢则杠不成立、不翻宝、不摸岭上。
- 打牌（含立直打牌）：先发 `TileDiscarded`，再清欠翻宝（V4 `revealPendingKanDora` 顺序）。
- 宝牌指示牌使用死墙固定槽 4/6/8/10/12，最多 5 张；`TilePositionMap.RevealDoraIndicator`（`:760`）拒绝重复、越界、第 5 张以后。

### 2.4 立直点数与供托守恒

- 立直：`Scores[seat] -= 1000`、`RiichiSticks += 1`；当前 `Scores + RiichiSticks*1000` 恒等于本局起始 `Spec.Scores + Spec.RiichiSticks*1000`。
- 终局：`RoundResult.StartingScores` 取 `Spec.Scores`（不是立直后的当前分），`StartingRiichiSticks` 取 `Spec.RiichiSticks`，`RemainingRiichiSticks` 取 settlement；`TotalDeltas` 从本局起始分计算，含立直扣点与供托转移。
- `RoundSettlement` 仍由冻结计分层/调用方供给；N-2 未接线。终局守恒检查保持为第二道闸。

### 2.5 不可变状态、fingerprint 与恢复

- 新权威字段：当前 `Scores`、当前 `RiichiSticks`、`RiichiDeclared[4]`、`Melds`（类型/座次/鸣牌来源/被鸣牌/四张实体）、`PendingKanDoraCount`。
- `SourceFingerprint` 覆盖上述字段、spec 起分/起供托、窗口 claim 的 **kind + MeldTiles + settlement**、事件、结果与 136 牌 hash；窗口 claim 不再只混 `IsRon`。
- `Prepare` 失败返回拒绝值，源状态零事件、零版本、零物理变化；`Install` 只接受直接子状态，校验 roundId/version/fingerprint/`ReferenceEquals(ParentState)`，成功恰好 +1 版本。
- `RoundRecoveryState` 扩展为 v2 编码；`RoundRecoveryCodec.FormatVersion=2`，v1 明确拒绝（不静默默认新字段）。旧值不会丢字段：拒绝而非补默认。
- `RoundCommandCodec.FormatVersion` 保持 1，新 meld-tile 块追加在原字段之后；旧 journal 解码为 `MeldTiles=null`，新 meld 动作缺块时显式 `InvalidDataException`，不静默当作空副露。
- `RoundRecovery.Restore` 校验：分数/供托守恒、melds 实体落在本座 Meld 区、不同 meld 不共享实体、每个 meld 有声明事件、每个立直标志有 `RiichiDeclared` 事件、窗口 claim 的形状（吃顺子/碰杠同种）与重复实体。

---

## 3. 文件清单（逐文件）

### 3.1 生产代码

| 文件 | 变更 | 原因 |
|---|---|---|
| `dotnet/src/Mahjong.Domain/RoundTypes.cs` | 追加 5 个 action、3 个事件值；新增 `RoundMeldKind`/`RoundMeld`/`RoundClaimKind`；扩展 `RoundResponseClaim` | N-1a 类型面；持久值只追加 |
| `dotnet/src/Mahjong.Domain/RoundTransition.cs` | 追加 9 个错误码、3 个事件类型 | 新决策/窗口/立直/杠的可区分拒绝与事件 |
| `dotnet/src/Mahjong.Domain/RoundStateMachine.cs` | 新状态字段、5 个 Prepare/Commit 路径、优先级裁决、立直锁、暗杠校验、宝牌时序、fingerprint、`RoundCommand` 工厂 | N-1a 主体 |
| `dotnet/src/Mahjong.Domain/RoundRecovery.cs` | Recovery v2、新字段/claim/meld 编解码、命令 meld-tile 追加、恢复校验 | “新状态可冷恢复、旧值不静默丢字段” |
| `dotnet/src/Mahjong.Domain/TilePositionMap.cs` | 新增 `RevealDoraIndicator` | 杠后宝牌必须由牌实体层追加并进入 136 hash；否则只能在 Round 层伪造指示牌 |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryPayloads.cs` | `TableCheckpointFactory.SchemaVersion=2` | 明确拒绝旧 schema，不静默丢新字段 |
| `dotnet/src/Mahjong.TableRuntime/TableCheckpointValidator.cs` | 新增 `ValidateRoundDecisionState` | 保存/验证本任务新增 Domain 状态（守恒、melds、立直事件、pending dora） |

### 3.2 测试

| 文件 | 内容 |
|---|---|
| `dotnet/tests/Mahjong.Domain.Tests/RoundDecisionTests.cs`（新） | 吃/碰/明杠/暗杠/立直接受与拒绝、136 守恒、优先级、多席窗口、立直锁、立直供托终局转移、Prepare 失败零副作用、stale/foreign/终态拒绝、恢复等价、codec 兼容 |
| `dotnet/tests/Mahjong.TableRuntime.Tests/RoundDecisionCheckpointTests.cs`（新） | chi/riichi 状态经 `TableCheckpointFactory` 保存、`TableCheckpointValidator` 验证、`RoundRecovery` 冷恢复、re-encode hash 相等；损坏新字段/schema 1 拒绝 |

**范围扩大报告（准确路径/原因）**：`V5-1-REVIEW.md` §9.1 的 S-N1a 名义列为 `RoundTypes.cs`/`RoundStateMachine.cs`/`RoundTransition.cs` + Domain tests。本交付额外改了：
- `TilePositionMap.cs`：杠宝指示牌必须由 136 物理层追加，否则新指示牌无法进入 `TileStateHash` 与恢复；不接三绝技、不重构 actor。
- `RoundRecovery.cs`：新权威字段/claim/meld 必须进入现有 `RoundRecovery` + checkpoint 合同；不改该文件就无法满足“冷恢复/旧值不静默丢字段”。
- `TableRecoveryPayloads.cs`、`TableCheckpointValidator.cs`：仅这两个既有 TableRuntime checkpoint 文件，用于保存/验证新 Domain 状态；未改 `TableActor`/投影/出千执行链。

未改：`AGENTS.md`、`CLAUDE.md`、`PROMPTS.md`、`doc/v5-重构` 任务书/协议、`unity/`、`Engine/Resources`、Burst、`tests/v5-golden/*`、任何用户未提交文件。

---

## 4. 验证（真实命令、退出码、计数、原始日志、SHA256）

证据目录（独占，不覆盖历史）：
`C:/Users/sncdc/AppData/Local/Temp/n1a-round-decisions-a058127a4ecb4d258485292feac06c97/`

| 验证 | 真实命令 | 实际结果 | 退出码 |
|---|---|---|---|
| solution Release build | `& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental` | 12/12 项目，**0 警告 / 0 错误** | 0 |
| Domain 全量 | `& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build` | **346 passed / 0 failed / 0 skipped / 346 total** | 0 |
| TableRuntime 全量 | `& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build` | **316 passed / 0 failed / 0 skipped / 316 total** | 0 |
| diff-check | `git diff --check` | 无输出 | 0 |

基线对照：Domain 322 → 346（**+24** 新用例）；TableRuntime 313 → 316（**+3** 新用例）。两套均 0 skip；未使用 `--filter` 跳过、未删/弱化断言、未改 verifier。

日志 SHA256（完整小写）：

| 文件 | SHA256 | 字节 |
|---|---|---|
| `solution-release-build.log` | `9e899b96b0259b1d5790d92db2226ed1dc7aa454765cd4a135f45edb8c52dc65` | 4646 |
| `domain-tests.log` | `24e10b0351af3017493e6a8e757a1c6592b9988c380162ba3a12fac991eaf6bb` | 728 |
| `table-runtime-tests.log` | `0b21fddc6a0ca1805b7f49324755f310c8e4605c44b02aee88c82b40818a4996` | 788 |
| `domain-framework.log` | `5d8cf220d2c148c5db6b853f0dd9b3a6ac938d46fcc5abc08e8fdd48e2ace233` | 1026 |
| `table-runtime-framework.log` | `5712803db10c8f36fa8a30e160dd91abfbb84c1f17db6ab90c9d1c0bbbe5e4ff` | 1074 |
| `n1a-source.patch` | `4615c7e7811f0be8bf4c033eaf431fa4728aeb8f8eaee5e519eb5fa6aa0b6429` | 267788 |
| `identity.txt` | `11a4ae9219d8c2dab77b47d2d5e217211ce4e7decdde648179b74e7baa16fb63` | 128 |

源码 SHA256（提交前）：

| 文件 | SHA256 |
|---|---|
| `RoundTypes.cs` | `133c5caedf1582379c36468c05d223422015c0b1eb62b6e57d6d50bda343dcdd` |
| `RoundTransition.cs` | `fdf4856e7f220362489b74e5f5e7c9c85dfaab63e161b46a955dcc5495d85d42` |
| `RoundStateMachine.cs` | `4502cd6f2470f5a71de2ebd3f9a444bb30ce46fa0f7a0da03a2d824ca689e565` |
| `RoundRecovery.cs` | `55eae65fd9531213591d70b81cf0c858fe7c16292ec416b83d67315e206a5031` |
| `TilePositionMap.cs` | `b5b707d5762770f82d4cdcf4c36a703e85f02c58c020ff81dbe2333582a7fb4a` |
| `TableRecoveryPayloads.cs` | `c3a6ebce12bba5be461deb70917f222ef30f277a6ba1e46653e8f894fe7bddac` |
| `TableCheckpointValidator.cs` | `4190bfee3603b6f2e1bd2e0d7a6e46296a48cbb487a98282ca187fdff00d7418` |
| `RoundDecisionTests.cs` | `8dca8404c35ced0d2fa16449dfed0a94a6f2a83101d4c3adf11d92cc2bca4cdb` |
| `RoundDecisionCheckpointTests.cs` | `f50534ddbe3f9cfeb2c1e1f1868e643a6e1dd56309bf7dabfa1aefa24dcc5bf1` |

**NOT_RUN（有理由，不冒充通过）**：PG/Postgres、WSS、Unity/IL2CPP、Android/macOS 真机、T4 物理网络、远端 CI。理由：本批只改 Domain/两处 TableRuntime checkpoint 与测试，无对应环境；且用户明确禁止触发远端 CI、部署与全局环境改动。V4 全量 Kotlin 测试未重跑（无 Kotlin 代码变化）；黄金 fixtures 未改、未重导出。

### 4.1 提交后复核（post-commit）

- 代码提交 `5f377c8a62d432b1bd6f474f8f08ca8f609bbabe`；本文档单独提交（`git log -1 --format=%H`）。
- `git status --porcelain=v1`：**输出为空**（clean）。
- `git diff --check`：**exit 0**；`git diff-tree --check 5f377c8^ 5f377c8`：**exit 0**。
- 逐文件 `git hash-object` 与 `git rev-parse 5f377c8:<path>` **全部 MATCH**（提交内容与已测工作树内容一致）。
- 提交后重跑：Domain **346 passed / 0 failed / 0 skipped / exit 0**；TableRuntime **316 passed / 0 failed / 0 skipped / exit 0**。日志：
  - `final-domain-tests.log` SHA256 `3dccffa20d98ace02a8c44906c12c69fd28bcc2ed39c6fbd31b9876230824bfb`（728 B）
  - `final-table-runtime-tests.log` SHA256 `468800efd00ad940355b7c7af0edcda515ba3e48cf1370a368fee1a4b2e92409`（788 B）
  - `final-domain-tests.exit.txt` / `final-table-runtime-tests.exit.txt` 均为 `9585fbece5c402088f2d6a2def7e60d1c7ffecc24b16d32d5823a064b008935b`（内容 `0`）

---

## 5. 已知缺陷/边界（一次性全报；同类合并）

### D-N1a-01 [MAJOR，后续 N-2/投影] facts-backed checkpoint 在杠后冷恢复会被拒绝

- **位置**：`dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs:490-496`（`Kan/replacement-draw wall evidence is not modeled; refusing to guess.`）。
- **原样复现/实际 vs 期望**：任何 committed history 含 `KanDeclared` 或 `RinshanDrawn` 的 checkpoint，在 `ValidateRecoveredEventFacts(history, facts, policy, finalRound)` 路径直接抛 `TableProjectionException`；实际=杠后（含既有 AddedKan 和本任务 open/closed kan）不能带 facts 冷恢复；期望=N-2/投影补齐后从 checkpoint 恢复 136 牌/杠/岭上墙证据，或明确“杠后 checkpoint 延后到下一命令”的产品决策。
- **证据**：源码行 + 本交付 `RoundRecovery` 无 facts 的 checkpoint/恢复测试通过；未执行带 facts 的杠后恢复（该路径不在本任务文件范围）。
- **修复要求**：由 N-2/投影任务建模 `KanDeclared`/`RinshanDrawn` 的逆向墙证据，禁止用猜测填充。
- **验收断言**：构造含 closed/open/added kan 的 facts checkpoint，冷恢复成功且 136 hash/fingerprint/事件事实一致；或在合同层显式拒绝并给出可恢复的下一 checkpoint 策略。

### D-N1a-02 [MAJOR，后续投影/actor] 新 Domain 事件未进入 `TableEventProjection`

- **位置**：`dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs:568-666`（`FromRoundEvent` 的 switch 没有 `RiichiDeclared`/`ChiDeclared`/`PonDeclared`，默认返回 `null`）。
- **实际 vs 期望**：实际=玩家 envelope 结构上可把 `RoundCommand` 送到 `_round.Execute`，但新事件不会被投影为协议事件（`ProtocolEventType.ChiDeclared/PonDeclared/RiichiDeclared` 在 `TableProjectionContractMapper` 已有 payload 分支，缺的是 Domain→Protocol 入口）；期望=新事件按冻结 21 类映射并带副露/立直事实。
- **证据**：`TableEventProjection.cs` 行 + `TableProjectionContractMapper.cs:448/454/471` 已有协议分支。
- **修复要求**：补 `FromRoundEvent` 分支与事件事实/私有性、副露 tiles/from 的投影来源；不得把 `ResponseClaimed` 直接改名。
- **验收断言**：玩家 chi/pon/riichi 事件端到端出现在按座位/观战流，字段与协议 schema 一致；恢复事件事实校验通过。

### D-N1a-03 [MINOR，N-1c 可选] AI/超时适配仍只覆盖 Pass/Discard

- **位置**：`dotnet/src/Mahjong.TableRuntime/TableActor.cs:4819`（`MapAction` 仅 `Pass`/`Discard`；调用点 `:1422`/`:1611`）。
- **实际 vs 期望**：实际=自动化座位不能响应新吃/碰/杠/立直窗口；期望（若产品要求）=按 `LegalActionKind` 扩映射并保留 null→pending 语义。G1 九门无 AI 条款，本项不阻断 N-1a。
- **修复/验收**：N-1c 决定是否实施；实施后补 AI/超时判别式测试，禁止自动和牌/自动鸣牌。

### D-N1a-04 [INFO] 类型级食替未实现（V4 本身没有）

- **位置**：V4 `engine/.../TileMovement.kt` 的 `discard` 只校验“手中确实持有该物理牌”；全仓 `kuikae|食替|食い替` 无业务命中。
- **实际 vs 期望**：实际=被鸣的物理牌已入副露，不能再次打出；同种另一张仍可按 V4 打出。期望（若产品要日麻食替）=新 ruleset 明确类型级禁打并影响听牌/合法动作。本任务选择不发明规则。
- **验收**：若产品裁决要食替，新增 ruleset 版本 + 类型级测试；否则在规则文档明确 V4 无此约束。

### D-N1a-05 [INFO] Domain 仍不建模双立直/一发/振听/役种

- **位置**：`RoundStateMachine` 的立直只建模“门前/1000 点/听牌/立直锁/供托”；Ron/Tsumo 的 settlement 仍由调用方/scoring layer 供给（`RoundStateMachine.cs:24` 头注释、`BuildTerminal` `:1506`、`ValidateSettlement` `:1571`）。
- **实际 vs 期望**：实际=立直合法性正确，但双立直/一发/振听/役种和终局算分仍不在 Domain 权威链；期望=N-2 接 `YakuEvaluator`/`WinningScoreEvaluator`，并由 scoring context 建模这些状态。属既有边界，本任务未改变。
- **验收**：N-2 后权威链 settlement 与 scoring-v1 对齐；振听/双立直/一发用例通过。

### D-N1a-06 [INFO] 恢复/命令 schema 版本策略

- 恢复 `RoundRecoveryCodec` **2.0**：v1 明确拒绝（`Unsupported round recovery schema 1.`），不静默补默认；checkpoint `SchemaVersion=2`，schema 1 由 `TableCheckpointValidator`/`TableRecoveryService` 拒绝。
- 命令 journal 保持 **1**，新 meld-tile 块为尾部追加：旧 payload 解码为 `MeldTiles=null`，新 meld 动作缺块时 `InvalidDataException`，不会当作“空副露”执行。
- 这是**有意的兼容/拒绝边界**，不是静默迁移；升级旧 checkpoint 需要显式迁移工具（本批未做）。

---

## 6. 后续责任（准确列出，不假称关闭）

| 项 | 状态 | 责任/落点 |
|---|---|---|
| N-2 结算计算接线 | **未做** | `YakuEvaluator`/`WinningScoreEvaluator` 进入权威链；保留 `RoundStateMachine.BuildTerminal/ValidateSettlement`（`:1506`/`:1571`）第二道守恒闸 |
| N-1b 出千 7 命令执行层 | **未做**（本批零改动） | `TableCommandEnvelope` 出千载荷、actor 分发、`CheatSubmission` 调用、私有事件投影 |
| N-3 黄金全字段对齐 | **未做** | manifest pending/partial 逐项；本批未改/未重导出 `tests/v5-golden/*` |
| N-1c 自动座位适配 | **可选、未做** | `MapAction` 按新产品决策扩展；G1 九门无 AI 项 |
| 投影/actor 消费新事件 | **后续** | `TableEventProjection.FromRoundEvent` 补 Chi/Pon/Riichi；`TableVisibilityProjection` 杠/岭上 facts；详见 D-N1a-01/02 |
| 整场 AgariYame | **已有入口，不改** | `MatchSession.ChooseDealerEnd`（`:498`）；本任务不把它塞回 `RoundStateMachine` |
| G1 | **仍不批准** | 冻结头 8ee22eb：7 PASS/2 FAIL；V4 唯一生产；G5 未完成 |

---

## 7. 复现命令（UTF-8 / PowerShell）

```powershell
Set-Location D:/majiang-wt/n1a-round-decisions
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build
git diff --check
git status --porcelain=v1
```

实际结果见 §4；所有计数为 xUnit v3 Microsoft.Testing.Platform 的真实 summary，未手填。日志与源码 SHA256 见 §4；证据目录为独占临时目录，历史 CI/旧探针日志未被覆盖。

---

## 8. 结论

本交付完成 **N-1a 的 Domain 组件面**：Chi/Pon/明杠 claim/暗杠/立直、多席窗口与冻结优先级、立直扣点/供托守恒、杠后补牌/宝牌时序、不可变状态与 fingerprint、Recovery v2 + 两处 TableRuntime checkpoint 保存/验证，以及定向接受/拒绝/136/恢复测试。

它**不**表示：
- G1 通过或迁移完成；
- N-1b/N-2/N-3/N-1c/SF-1 完成；
- 投影/actor/自动座位端到端已通；
- V4 不再是唯一生产架构。

V4 仍是唯一生产基线；G1 仍为 7 PASS/2 FAIL；G5 仍未完成。
