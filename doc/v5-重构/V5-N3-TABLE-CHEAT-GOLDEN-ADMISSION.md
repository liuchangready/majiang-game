# V5 N3 Table Cheat Admission Golden Consumer Binding

日期：2026-10-25
工作树：`D:/majiang-wt/n3-table-cheat-admission`
分支：`wt/v5-n3-table-cheat-admission`
冻结/交付前基线：`285781db305980a2d5dfdb3e0eefcc9854296e04`（PR19 已发布已接受头）
任务性质：现有已接受 N1b actor 入口的黄金消费者绑定；不重复开发 N1b/actor、不重复 Domain handler、不依赖任何在途 cheat 绑定源码。
未 push、未开 PR、未合并、未改默认分支、未触发远端 CI、未部署、未转派、未改全局环境。

## 0. 结论（PASS / FAIL / BLOCKED / NOT_RUN 分开）

| 范围 | 状态 | 依据 |
|---|---|---|
| 本任务 5 例行为绑定（4 拒绝 + `tm-allowed` 正控制） | **PASS（仅本范围）** | 5 个 Fact 经真实 `TableActor.SubmitCheatAsync -> ValidateAuthorityIdentity -> RoundCheatTransition.Prepare -> Domain` 执行；定向 6/0/0/0，exit0 |
| `tm-allowed` 一次扣费/一次版本/重放不重复 | **PASS** | version 0→1；余额 40→30；1 条 ledger entry；1 组 4 张标记；3 个事件；1 次 commit；重放返回同一 receipt 实例 |
| 4 个拒绝例的零副作用 | **PASS** | 版本/事件序/状态指纹/phase/seat/scores/136 落位/round 事件/cheat 版本与指纹/余额/ledger entry/标记/pending/commit/publication 全部不变；`result.Events` 空；`CommandVersion` null |
| 136 张物理守恒 | **PASS** | 每例执行前后断言 136 个 placement、136 个不同 tile id、值域 0..135 |
| V4 粗错误码 → C# typed reason 语义映射 | **PASS（按表 4/表 5）** | `DECISION_MISMATCH(round end)`→`ROUND_FINISHED`；`NO_ACTIVE_DECISION`→`DECISION_MISMATCH`（clock 无 timing 分支）；`DECISION_MISMATCH(no active round)`→`ROUND_MISMATCH`；`DEADLINE_EXPIRED`→`DEADLINE_EXPIRED` |
| mutation matrix | **PASS（预期失败）** | 28 例：5 positive exit0、23 negative 全部 native exit1 / Total 6 / Failed≥1 / Errors0 / Skipped0 / NotRun0 / 预期失败文本命中；0 unexpected |
| binding readiness | **PASS 5/5** | `-list methods` 发现 6 个方法；5 个绑定 case 方法全部存在；baseline Total=6、Failed=0、Skipped=0、Errors=0、NotRun=0 |
| 原 fixture 不可变 | **PASS** | `tests/v5-golden/cheat-v1.json` 前后 SHA256 均为 `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |
| TableRuntime.Tests 非增量 Release 构建 | **PASS** | exit0，0 warning / 0 error |
| TableRuntime 全量 | **PASS** | 361 / 0 failed / 0 skipped / 0 errors / 0 not-run，exit0 |
| solution Release 构建（依赖确认） | **PASS** | 12 项目，0 warning / 0 error，exit0 |
| 精确 V4 输入结构复刻（非行为分支） | **BLOCKED / PARTIAL（见 §9）** | `sessionId`、`controller`、`roundIdGiven=null` 的“无 active round”状态、终局仍保留 pending decision、pending-without-timing 的正常业务路径，均无现有 actor/端口可表达 |
| N3 整体 | **FAIL / 未完成** | 本任务只绑定 `tileMarkHandler` 的 5/20 例；sleeveBloom/beamSwap/tileMovement/ledger 段与其他 15 例仍未关闭 |
| G1 | **FAIL / BLOCKED（维持不批准）** | 门 2 黄金全字段口径、门 5 三绝技权威链未关闭；N1b/N2 整体接线不在本任务 |
| G5 | **NOT_RUN** | 未运行 PG/WSS/Unity/IL2CPP/Android/iOS/设备/弱网/跨物理网络 |
| PG / WSS / Unity / IL2CPP / Gradle / 平台 | **NOT_RUN** | 本任务无生产更改，按要求不盲重跑；不宣称通过 |
| 远端 CI | **NOT_RUN（本任务）** | 用户提供的 40158293672（push 285781d）与 40158296451（PR merge c905edef, parents 457274c+285781d）均 1628 PASS/0 FAIL/0 SKIP，是 caller 已核对的事实；不证明本新任务、Unity/IL2CPP 或设备门槛 |
| 兄弟在途工作树 | **NOT_TOUCHED** | 未读取/修改 `thread_20261025_104846_b58a` 的 Domain.Tests 四文件、`thread_20261025_051932_5eeb` 的 8254c9b 文档审计、`n3-cheat-binding` 或其产物；未推测其通过 |

本交付**不**使用 `complete.flag`，**不**把本范围通过写成 N3/G1/G5 通过，**不**改动 V4 唯一生产基线。

## 1. 范围与约束遵守

### 1.1 允许与禁止

- 唯一新增代码：`dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs`。
- 唯一新增脚本：`tools/v5-golden/probe-table-cheat-golden.ps1`。
- 唯一新增报告：`doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md`（本文件）。
- 未修改：`Mahjong.TableRuntime` / `Mahjong.Domain` / `Mahjong.Ai` / `Mahjong.Contracts*` / `Mahjong.Client` 生产代码，`N1bCheatExecutionTests.cs` 及其他既有测试，`tests/v5-golden/*` 全部 fixture，exporter，catalog，manifest，verifier，`*.csproj`，`Mahjong.sln`，Unity/Resources/Burst/环境，主 checkout 未提交的 `CLAUDE.md`、`PROMPTS.md`、任务书/协议。
- 未读取或复制兄弟线程未提交产物；未合并其分支；未提前实现 N1b；未修改主 checkout。
- 只读参考：`AGENTS.md`、`CLAUDE.md`、迁移方案、`V5-0-1-IMPLEMENTATION-TASKS.md`、`CODEX-V5-N1B-ACCEPTANCE.md`、`V5-G1-N1B-CHEAT-EXECUTION.md`、V4 exporter、Domain/actor 源码、`D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`。
- 共享生产 C# 仍为 netstandard2.1/C# 9；新测试为 net8/C# 9；CLI 为 `D:/Unity/dotnet/dotnet.exe`（8.0.426）。

### 1.2 交付前身份核对

```text
$ git -C D:/majiang-wt/n3-table-cheat-admission status --porcelain=v1
（空）
$ git -C D:/majiang-wt/n3-table-cheat-admission rev-parse HEAD
285781db305980a2d5dfdb3e0eefcc9854296e04
$ git -C D:/majiang-wt/n3-table-cheat-admission rev-parse --abbrev-ref HEAD
wt/v5-n3-table-cheat-admission
```

`git worktree list` 确认 `D:/majiang-wt/n3-table-cheat-admission 285781d [wt/v5-n3-table-cheat-admission]`；主 checkout `D:/majiang 900a043 [feat/v5-1-integration]` 未被本任务触碰。

### 1.3 交付文件清单

```text
A dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs
A tools/v5-golden/probe-table-cheat-golden.ps1
A doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md
```

最终 `git status --porcelain=v1` clean；`git diff --check` 与 `git diff --check 285781d..HEAD` 均 exit0（见 §11）。

## 2. 源 oracle 与 V4 输入重建

### 2.1 源

- V4 exporter：`engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt:359-427`（`tileMarkHandlerSection`），输入 helper 在 `:363-403`，常量在 `:260-275`。
- V4 真实判定：`engine/src/main/kotlin/com/majiang/authority/cheat/TileMarkSubmissionHandler.kt:60-114`。
- 导出 fixture：`tests/v5-golden/cheat-v1.json` 的 `tileMarkHandler`（20 例，只有 `caseId/note/outcome/reason`，没有 `goldenInputs`）。
- fixture 元数据：`schema=v5-cheat-golden-v1`、`generator=engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt`、`generatorVersion=1`、`v4Commit=a96c195c852e035ff6434584135718bbb8a05d29`、`extractionStatus=exported`。

**本任务没有发明 `goldenInputs`。** 因为 tileMark 段没有导出输入，消费者从 exporter 的 `input(...)` 分支重建“输入类别”，再用真实 C# 回合满足它；exact V4 实体 tile id/session/control 值不是该段的导出字段，见 §9。

### 2.2 逐例源输入与 V4 判定

| case | V4 源输入（exporter + handler 分支） | V4 判定 | 说明 |
|---|---|---|---|
| `tm-allowed` | seat0、`CHEAT_WINDOW`、currentPlayer0、无 preparation/response/riichi/offer、`decision(0)`、`timing(0)`（opens 0 / deadline 10000）、now 1000、4 张 owned unmarked hand tiles、points 40、cost 10 | `Allowed` / null | exporter `:406`，handler 全部分支通过 |
| `tm-wrong-phase-round-end` | 其余同 allowed，`phase=ROUND_END`；request 身份不变（`request.roundId` 仍是 `r-golden`，decision/timing 仍传入） | `Rejected` / `DECISION_MISMATCH` | exporter `:410`；handler `:63-71` 把 `ROUND_END` 与“round 缺失”合并为同一粗码 |
| `tm-no-active-decision` | `timingGiven=false`（`timing=null`），`decision(seat)` 仍存在，其余同 allowed | `Rejected` / `NO_ACTIVE_DECISION` | exporter `:423`；handler `:82-84` |
| `tm-deadline-expired` | `nowOverride=deadline=10000`，decision/timing 仍存在 | `Rejected` / `DEADLINE_EXPIRED` | exporter `:424`；handler `:85`（`now >= deadline`） |
| `tm-decision-mismatch` | `roundIdGiven=null`（上下文无 active round），`request.roundId` 仍为 `r-golden`，decision 仍存在 | `Rejected` / `DECISION_MISMATCH` | exporter `:425`；handler `:64-71` |

### 2.3 现有 actor 是否可表达（逐项）

| V4 输入项 | C# actor 可表达性 | 本任务处理 |
|---|---|---|
| seat0 / `CHEAT_WINDOW` / currentPlayer0 | 可：`P31Fixtures.Dealt` + `RoundPhase.CheatWindow` | 真实 round |
| 4 张 owned unmarked hand tiles | 可：真实 hand 前 4 张 | 语义等价；exact V4 tile id 见 §9 |
| decision `d-1` + timing（0..10000） | 可：`OpenDecisionAsync` 原子安装 pending + clock timing；`DecisionClockConfig(9000,0)` 在 now=1000 时 deadline=10000 | 真实公开 API |
| now==deadline | 可：`ManualTableClock.Now=10000` | 真实时钟 |
| `phase=ROUND_END` | 可：真实终局 `Tsumo` 后 `IsFinished`/`RoundPhase.RoundEnd` | 真实终局；pending 已被终局消费（见 §9） |
| `timing=null` 但 decision 存在 | 仅 fault injection：公开 API 原子安装，二者不可分离 | 反射清 `_decisionClock` timing，保留 `_pending`；报告明确非业务可达 |
| 上下文 `roundId=null`（无 active round） | 不可：actor 永远绑定一个 `_round` | 用“命令 round 与 active round 不符”表达同一 identity 类，typed `ROUND_MISMATCH`；exact 无 round 状态见 §9 |
| `sessionId` / `controller` | 不可：`TableCheatCommandEnvelope` 没有这两个入口 | 该分支的拒绝原因不依赖它们；exact parity 见 §9 |
| controlEpoch | 可，但 C# seat control epoch 从 0 起，V4 用例是 1 | 本任务用 0；另加 stale control=1 的独立 guard，证明代次不可复用 |
| points 40 / cost 10 | 可：自建 `CheatEconomyConfig(true,40,{TileMark=10,...})` | 与 V4 数值一致 |

## 3. C# 真实执行路径

固定路径（均为已接受 N1b 入口，不是“未来接线”）：

```text
TableActor.SubmitCheatAsync                        TableActor.cs:223
  -> ProcessCheatCommandAsync                      TableActor.cs:1487（request binding / replay）
  -> ProcessCheatCommandInternalAsync              TableActor.cs:1564
  -> ProcessCheatCommandCoreAsync                  TableActor.cs:1639
     -> ValidateAuthorityIdentity                  TableActor.cs:1893
        recovery fence :1905 / owner :1914 / round :1922 / pending+decision :1938-1953
        timing missing :1955-1961 / deadline :1964 / state version :1980 / control :1988
     -> BuildCheatContext                          TableActor.cs:1735
     -> RoundCheatTransition.Prepare                RoundCheatTransition.cs:506
        -> CheatSubmission.ApplyTileMark            CheatSubmission.cs:129-...
        -> Materialize                              RoundCheatTransition.cs:1274
     -> CommitCheatAndInstallAsync                 TableActor.cs:1722
```

- `TableActor.cs:1662` 在 `ProcessCheatCommandCoreAsync` 内调用 identity；拒绝发生在 Domain 之前，故拒绝例不会进入 `CheatSubmission`、不会产生版本/扣费/标记/物理移动。
- `RoundCheatTransition.Guard`（`:1218-1272`）在 Domain 前再次检查 `round.IsFinished`（`:1232`）与 command round（`:1240`）。
- `Materialize` 的 `CHEAT_REPLAYED`（`:1301-1305`）与 actor 的 `_cheatRequestBindings`（`:1500-1522`）共同保证重放不重复。
- `TableRuntimeReasonCode.FromCheat`（`TableRuntimeReasonCode.cs:97-204`）是 bridge/Domain 码到 typed reason 的唯一映射；本任务没有改它。

## 4. V4 粗码 → C# typed reason 映射账本

| V4 case | V4 reason | V4 判定点 | C# 实际分支 | C# typed reason | 是否同码 |
|---|---|---|---|---|---|
| `tm-allowed` | null | 全通过 | `ApplyTileMark` Accepted | `Accepted`（无 reason） | — |
| `tm-wrong-phase-round-end` | `DECISION_MISMATCH` | handler `:63-71`（ROUND_END） | `RoundCheatTransition.Guard :1232` `round.IsFinished` | `ROUND_FINISHED` | 否（V4 粗码合并，C# 有独立 typed 码） |
| `tm-no-active-decision` | `NO_ACTIVE_DECISION` | handler `:82-84` | `ValidateAuthorityIdentity :1955-1961` “active decision is not open in the clock” | `DECISION_MISMATCH` | 否（C# 把 pending-without-timing 归入 DecisionMismatch） |
| `tm-deadline-expired` | `DEADLINE_EXPIRED` | handler `:85` | `ValidateAuthorityIdentity :1964-1970` `now >= timing.DeadlineMillis` | `DEADLINE_EXPIRED` | 是 |
| `tm-decision-mismatch` | `DECISION_MISMATCH` | handler `:64-71`（round null） | `ValidateAuthorityIdentity :1922-1928` command round != active round | `ROUND_MISMATCH` | 否（同一 identity 类，C# 独立 typed 码） |

### 4.1 三个容易混同的分支（本任务显式区分）

| 输入状态 | `ValidateAuthorityIdentity` 位置 | `Detail` 文本 | typed reason | 本任务 |
|---|---|---|---|---|
| pending 存在，请求**没有** decisionId | `:1945-1953` | `The command does not carry the active decision id.` | `DECISION_MISMATCH` | 未用作 `tm-no-active-decision` |
| pending 存在，decisionId 匹配，**clock 无 timing** | `:1955-1961` | `The active decision is not open in the clock.` | `DECISION_MISMATCH` | `tm-no-active-decision`（fault injection） |
| **无 pending**，请求带 decisionId | `:1972-1978` | `The command refers to a decision that is not open for this seat.` | `DECISION_MISMATCH` | deadline 例的 tick-after 控制 |

测试对 `tm-no-active-decision` 明确断言 `Detail` 含 `not open in the clock`，且不含 `does not carry` / `not open for this seat`；deadline 例的 tick-after 控制断言 `not open for this seat`。因此不是用相同错误码替代别的输入分支。

## 5. 逐例真实执行与副作用

每例都在执行前捕获真实快照：`StateVersion / LastEventSeq / StateFingerprint / IsFinished / Phase / CurrentSeat / Scores / 136 placements / RoundEventCount / Round.Version / Cheat.Version / Cheat.Fingerprint / Balance / LedgerEntryCount / MarkGroupCount / MarkedTileCount / MarkedTiles / PendingTiming / CommitCount / PublicationCount`。拒绝后逐项比较；并断言 `result.Events` 为空、`result.CommandVersion` null、136 守恒。

### 5.1 `tm-allowed`（正控制，`:118`）

- 执行前：version 0；余额 40；ledger 0；标记 0；pending `0:d-1:10000`；commit 0；publication 0；136 张。
- 执行：`OpenDecision` d-1，now=1000 < 10000；`Cheat(... SubmitTileMark, 4 张真实手牌, decision d-1)`。
- 结果：`Accepted`；`CommandVersion=1`；3 个事件（`CheatApplied` bridge + `CheatPointsChanged(-10, newBalance=30)` + `TileMarkApplied(groupId=g-golden, seat0)`）。
- 执行后：version 1；LastEventSeq +3；round 事件 +1；余额 30；ledger entry 1；1 组 4 张；落位签名不变；cheat version 前进、fingerprint 改变；commit 1；publication 1；136 张。
- 重放同一 envelope：`Assert.Same(result, replay)`；version/余额/ledger/标记/commit/publication 全部不变。
- stale control guard（非 V4 case）：新 requestId + controlEpoch=1 → `STALE_CONTROL_EPOCH`，零事件/零版本/零扣费/零额外 commit。

### 5.2 `tm-wrong-phase-round-end`（`:202`）

- 先真实 `Tsumo` 终局（带 decision d-1；终局消费 pending，version 0→1，commit 1，publication 1）。
- 执行前快照：`IsFinished=true`、`Phase=RoundEnd`、version 1、pending 全空、136 张。
- 执行：round/seat/request 身份保持 `r-golden/0/tm-round-end`，不带 decisionId（见 §9 的 exact parity 说明）。
- 结果：`Rejected` / `ROUND_FINISHED`，`Detail` 含 `already finished`；`Events=[]`；`CommandVersion=null`。
- 执行后：version/事件序/指纹/phase/seat/scores/落位/round 事件/cheat 状态/余额/ledger/标记/pending/commit/publication 全部不变；仍 `IsFinished=true`；136 张。
- 终局不复活：`OpenDecisionAsync` 返回 `ROUND_FINISHED`，版本不变。

### 5.3 `tm-no-active-decision`（`:276`）

- 先真实 `OpenDecision` d-1（pending+timing 原子安装）。快照 `0:d-1:10000`。
- fault injection：反射取 `_decisionClock`，调用 Domain `ClearTimings()`，保留 `_pending`。注入后快照 `0:d-1:-`；其余快照字段不变。
- 执行：请求**带** decisionId d-1，now=1000 < 10000（不是 deadline 分支）。
- 结果：`Rejected` / `DECISION_MISMATCH`；`Detail` 含 `not open in the clock`；不含 `does not carry` / `not open for this seat`；`Events=[]`；`CommandVersion=null`。
- 执行后：version 0、余额 40、ledger 0、标记 0、commit 0、publication 0、pending 仍 `0:d-1:-`；136 张。pending 未被消费。
- 明确声明：这是 fault injection，不是正常业务可达状态；正常 `OpenDecisionAsync` 总是同时安装 pending 与 timing。

### 5.4 `tm-deadline-expired`（`:328`）

- 先真实 `OpenDecision` d-1（Discard candidate，便于 tick），快照 `0:d-1:10000`；now=10000。
- Phase A 直接准入：`Rejected` / `DEADLINE_EXPIRED`；`Detail` 含 `authoritative deadline`；`Events=[]`；`CommandVersion=null`。
- 执行后：version 0、余额 40、ledger 0、标记 0、commit 0、publication 0、pending 仍 `0:d-1:10000`（拒绝不静默过期；过期是 tick 的职责）；136 张。
- Phase B 另一时序：same clock=10000 调 `TickAsync` → `Accepted`、version 1、commit 1、publication 1；真实 timeout discard 改变落位；pending 全空；136 张。随后同一 cheat（decisionId d-1、expectedVersion 1）→ `Rejected` / `DECISION_MISMATCH`，`Detail` 含 `not open for this seat`；此后零变化。证明“先 due tick”时整桌不可能零变，且零变只属于“准入先于 tick”的明确时序。

### 5.5 `tm-decision-mismatch`（`:395`）

- 先真实 `OpenDecision` d-1（pending+timing），快照 `0:d-1:10000`。
- 执行：command roundId=`r-stale-golden`（active 为 `r-golden`），decisionId d-1，expectedVersion 0。
- 结果：`Rejected` / `ROUND_MISMATCH`；`Detail` 含 `does not match`，且不含 `decision`；`Events=[]`；`CommandVersion=null`。
- 执行后：version 0、余额 40、ledger 0、标记 0、commit 0、publication 0、pending 仍 `0:d-1:10000`；136 张。旧 round 不复用。

## 6. fixture 严格性与 mutation 抗性（测试内）

`LoadFixture`（`:478`）对 TEMP 覆盖文件做：

- 顶层精确字段集合；`schema`、`generator`、`generatorVersion`、`extractionStatus` 固定校验；重复/未知属性失败。
- `tileMarkHandler` 必须是数组；每例精确 4 字段 `caseId/note/outcome/reason`；重复 caseId 失败；未知 caseId 失败；缺失 V4 case 失败；0 例失败。
- `outcome` 只接受 `Allowed`/`Rejected`；`reason` 只接受字符串或 null；`Allowed` 必须 null、`Rejected` 必须非空；错型（数字/布尔/对象/数组）失败。
- 20 例的 `outcome/reason` 与 exporter `:406-425` 的硬编码 V4 oracle 逐项比较；`Oracle` 表包含全部 20 例，不只 5 例。
- `V5_TABLE_CHEAT_GOLDEN_PATH` 只用于覆盖 fixture 路径；路径缺失直接失败，不会静默回退。
- 5 个绑定 Fact 在 `LoadBoundCase` 中先断言 fixture == V4 oracle，再断言真实执行 == C# typed 映射；没有“fixture 自洽即通过”的路径。

## 7. 独立探针：mutation matrix 与 binding readiness

脚本：`tools/v5-golden/probe-table-cheat-golden.ps1`。

- 只使用独立环境变量 `V5_TABLE_CHEAT_GOLDEN_PATH`（不借兄弟脚本的变量）。
- 构建一次 `Mahjong.TableRuntime.Tests -c Release --no-incremental`；构建失败直接传播 native exit；assembly 缺失 exit2。
- 只写新的 GUID TEMP 目录；仓库 fixture 与 build output 不写；每次运行前后 try/finally 恢复环境（内层每 run + 外层整脚本）。
- 所有写文件（meta/summary/readiness/matrix/log）使用 `UTF8Encoding(false)`，无 BOM。
- 正控制 5 例：原 TEMP 拷贝、whole-file reserialize、`v4Commit/exportedAtNote` provenance 改变、note-only、非绑定 case note-only → 全部 exit0/6 passed。
- 负例 23 例：5 个 outcome 单字段、5 个 reason 单字段、unknown case rename、missing case、missing reason field、wrong-type reason/outcome/caseId/note、null reason on Rejected、duplicate case、unknown property（`goldenInputs`）、unknown extra case、schema sentinel、empty handler（structural）。
- 每个负例必须是：native exit **1**、Total == baseline 6、Failed≥1、Errors=0、Skipped=0、NotRun=0、真实 `[FAIL]` 行、预期失败文本命中。构建失败、missing fixture、未解析 summary、Total=0 都归为 tool failure，不算预期 FAIL。
- 语义负例失败文本示例：reason swap 命中 `Assert.Equal() Failure: Strings differ`（Expected/Actual 为两个不同 V4 reason）；unknown property 命中 `unknown property 'goldenInputs'`；empty handler 命中 `actual 0`。不是只靠 header/hash。
- readiness 独立于 matrix：`-list methods` 必须发现 6 个方法、5 个绑定 case 方法全部存在、baseline Total==6、Failed/Errors/Skipped/NotRun 全 0；否则 `BINDING-READINESS-FAIL` exit3，不允许用 matrix 绿灯掩盖未绑定 case。

实际结果：

```text
bindingReadiness=PASS readiness=5/5 discoveredMethods=6 mutationMatrix=PASS
cases=28 positive=5 negative=23 expectedTotal=6 unexpected=0 toolFailure=False
source raw SHA256: A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E
source LF  SHA256: 3B33824F53E6E5808D2475CC212FCF3A914BBB5DF9A1A367BF2078A252F1F68D
source git  SHA1  : 61307f255c8d1b02f1b64084768b6a500f3307b4
PROBE-PASS; native exit 0
```

## 8. 真实验证命令、native exit、计数与日志

CLI：`D:/Unity/dotnet/dotnet.exe`（`8.0.426`）。证据根：

```text
E = C:/Users/sncdc/AppData/Local/Temp/n3-table-cheat-final-2ce56f1a176640f093dfccabe2b519a3
P = C:/Users/sncdc/AppData/Local/Temp/table-cheat-golden-probe-bf19ebfcfc4643188a436b8e641588e2
```

| 验证 | 原始命令 | native exit | 真实计数 | 日志 |
|---|---|---:|---|---|
| TableRuntime.Tests 非增量 Release 构建 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v:minimal` | 0 | 0 warning / 0 error | `E/build-runtime-tests.log` |
| 定向新类 | `Mahjong.TableRuntime.Tests.dll -noLogo -class '*N3TableCheatGoldenAdmissionTests*'` | 0 | Total 6 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0 | `E/targeted-new-class.log` |
| TableRuntime 全量 | `Mahjong.TableRuntime.Tests.dll -noLogo` | 0 | Total 361 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0 | `E/runtime-full.log` |
| solution Release 构建 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental -v:minimal` | 0 | 12 项目 / 0 warning / 0 error | `E/solution-build.log` |
| mutation/readiness 探针 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-table-cheat-golden.ps1` | 0 | 28 cases / 5 positive / 23 negative / unexpected 0 / readiness 5/5 | `E/probe.log`；`P/matrix.csv`、`P/summary.json`、`P/readiness.json` |

定向/全量均未使用 `--no-build` 掩盖编译失败：先做非增量构建，再用同一 assembly 运行；全量 361 = 既有 355 + 新增 6。

未运行（NOT_RUN）：solution 的测试全量（本任务只要求 solution 构建确认依赖）、PG、WSS、Unity、IL2CPP、Android/iOS、真实设备、弱网、跨物理网络、Gradle/V4 生产、远端 CI。原因：本任务只新增测试/脚本/报告，无生产更改；按要求不盲重跑。

## 9. 缺口、BLOCKED 与准确最小后续范围

以下不是“行为绑定失败”，而是**精确 V4 输入结构**的不可表达项；行为分支和 typed reason 已在 §4/§5 绑定。

1. **`tm-decision-mismatch` 的“无 active round”状态不可表达**
   - 现状：V4 `roundIdGiven=null` 表示权威上下文没有 round；C# `TableActor` 永远绑定一个 `_round`，没有公开端口把它置空。
   - 本任务：用“命令 roundId 与 active round 不符”表达同一 identity 类，typed `ROUND_MISMATCH`，零副作用。
   - 若必须复刻“无 round”状态：需要 actor/端口级改动（例如允许无 round 的 actor 生命周期，或在 `TableCheatCommandEnvelope` 之外提供权威 round 缺失上下文），涉及 `TableActor.cs` / `TableRuntimeContracts.cs` 生产改动。**本任务不扩大范围。**
2. **`tm-wrong-phase-round-end` 的“ROUND_END 且 decision 仍存在”不可表达**
   - 现状：C# 终局命令会消费 `_pending`；终局后 `OpenDecisionAsync` 被 `ROUND_FINISHED` 拒绝。终局没有 pending 是业务正确状态。
   - 本任务：用真实终局 + 原 round/seat/request 身份（不带 decisionId）命中 `RoundCheatTransition.Guard` 的 `RoundFinished`。
   - 若必须复刻“终局仍保留 pending”：需要反射注入终局 pending（额外 fault injection），或改终局清理策略（生产规则，禁止为测试改）。**不做。**
3. **`tm-no-active-decision` 的 pending-without-timing 只经 fault injection**
   - 现状：`OpenDecisionAsync` 原子安装 pending + clock timing；`BeginRecoveryAsync` 同时清两者。没有公开 API 只清 timing。
   - 本任务：反射 `_decisionClock.ClearTimings()`，保留 `_pending`；测试明确标注非业务可达。
   - 若需要正常业务可达：需要 actor 提供显式的 timing-scope fault/recovery 端口，涉及 `TableActor.cs` 生产改动。**不做。**
4. **`sessionId` / `controller` 没有进入 C# cheat envelope**
   - 现状：`TableCheatCommandEnvelope` 只携带 round/seat/action/requestId/payload/decision/owner/control/scope/api/payloadSha；没有 V4 的 `sessionId`、`SeatController`。
   - 本任务：该 5 例的拒绝分支不读取这两个字段；`tm-allowed` 用 actor 初始 controller 表达。
   - 若必须 full parity：需要 host 入口/协议到 actor 的 session/controller 绑定，属 P4.1/P5.1 宿主入站映射，**不在本任务**。
5. **tileMark 段没有 `goldenInputs`，exact V4 tile id 不可导**
   - 本任务按 exporter 的“4 张 owned unmarked hand tiles”输入类，从真实 C# 回合取牌；没有伪造 `goldenInputs`，并新增 N20 负例拒绝 `goldenInputs` 注入。
   - 若必须 exact 实体 id：需要 V4 exporter 为该段导出输入并升级 fixture schema（exporter/fixture 变更，**本任务禁止**）。
6. **本任务只绑定 5/20 个 tileMark case**
   - 其余 15 例（not-your-turn、preparation、draw、response、riichi、selection、ownership、already-marked、capacity、funds、sleeve/beam pending）未在本任务执行。
   - 最小后续：在同一测试类按同一 oracle/快照模式扩展；仍只需 TableRuntime.Tests，不需要生产改动。
7. **sleeveBloom / beamSwap / tileMovement / ledger 段未绑定**
   - 最小后续：新增对应 TableRuntime.Tests 消费者或独立 N3 任务；不改生产。
8. **N3/G1/G5 边界**
   - N3 整体仍 FAIL/未完成；G1 未批准；G5 未完成；V4 仍是唯一生产架构。

## 10. 证据与 SHA256

证据根：

```text
E = C:/Users/sncdc/AppData/Local/Temp/n3-table-cheat-final-2ce56f1a176640f093dfccabe2b519a3
P = C:/Users/sncdc/AppData/Local/Temp/table-cheat-golden-probe-bf19ebfcfc4643188a436b8e641588e2
```

关键日志/产物（完整 SHA256）：

| 文件 | SHA-256 |
|---|---|
| `E/build-runtime-tests.log` | `374927A742C0A109C7274460B08F07351AA23AB216CA3C735BDDC7644AA13C83` |
| `E/targeted-new-class.log` | `B47DCD145CB63DF8BE749523A33F3E126352B8BF2B0E6C4D7FD18E34838F9BED` |
| `E/runtime-full.log` | `842F933D114604F76639F4009DFAB58DFA608CF79E378010684A3DBA8B91C892` |
| `E/solution-build.log` | `B9FCA92453816E6265F5035BDD6B7F08A091B61CA11E41C70ACC7EFD1C2BDD0C` |
| `E/probe.log` | `FA256790995A3139E8D0A3302432614A2C4CD9B387AB6FA4B5CCEFCAAB251F20` |
| `P/build.log` | `CCF3EB208E7692C0BD9B85FE23F5CA2BE31D2911DEB773697BBFF9414239EF3B` |
| `P/matrix.csv` | `F5D9A49A51F70887A11C441581C068451780AB2F3663B026AA92AECE710BC116` |
| `P/summary.json` | `78B72513A7A35302ADDC2BF69DFEF14A6961AC586B96B600069759F2EFC34B90` |
| `P/readiness.json` | `989E4A5873CEC14D64EBEAD22ECD9BDF530CC796D6AC1A88079387DBD6ED818B` |
| `P/meta.json` | `F12F22605C39B9C4BFC8D996D7D824B26CAC647BA4116AA84D3E59E9C62BC78F` |
| `P/list-methods.log` | `580FE19E9B61CD88B6D05B2474FDE4436B1850C4557FC47A1541878E81CE9095` |

源/ fixture：

| 文件 | SHA-256 |
|---|---|
| `dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs`（交付前） | `C87FCF50118F029E91F5449D7B16DB6FE723D086929E1B91346ACEC046679EAB` |
| `tools/v5-golden/probe-table-cheat-golden.ps1` | `6DE887A0C741243A5EFD4C13A34AE010F6593F4E551F19BF20C334EDCFA6C486` |
| `tests/v5-golden/cheat-v1.json` | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |

`E/sha256-final.txt` 与 `E/sha256-probe-and-source.txt` 保存了完整路径/长度/哈希清单；`P` 下 28 个 mutation JSON/LOG 的逐文件 SHA256 在 `E/sha256-probe-and-source.txt`。

## 11. 提交、逐文件清单、clean 与 diff-check

本地最小 commit（不 push）：`test(v5-n3): bind table cheat admission golden cases`。

逐文件：

```text
A dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs
A tools/v5-golden/probe-table-cheat-golden.ps1
A doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md
```

提交后验证：

```text
git status --porcelain=v1                 -> 空
git diff --check                          -> exit0
git diff --check 285781d..HEAD            -> exit0
git diff-tree --check HEAD^ HEAD          -> exit0
git show --stat --oneline HEAD            -> 仅上述三文件
```

（最终提交哈希与上述命令的实际输出见交付消息；本报告不预填未发生的哈希。）

## 12. 结论边界

- 本报告 PASS 仅为“5 个 tileMark 行为分支的黄金消费者绑定 + 其 mutation/readiness 探针 + 本地回归”。
- 不把本范围通过写成完整 N3、G1、G5、远端 CI、Unity/IL2CPP、设备或跨物理网络通过。
- 用户提供的两次远端 CI（40158293672 / 40158296451，均 1628 PASS/0 FAIL/0 SKIP）是 caller 已下载核对的历史事实；它们不含本次新增测试，不能证明本任务。
- V4 仍是唯一生产架构；无 `complete.flag`。
