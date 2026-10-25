# V5 N3 Table Cheat Admission Golden Consumer Binding（第 2 轮返修）

日期：2026-10-25
工作树：`D:/majiang-wt/n3-table-cheat-admission`
分支：`wt/v5-n3-table-cheat-admission`
冻结基线：`285781db305980a2d5dfdb3e0eefcc9854296e04`
上一交付：`7d0bb5b72682b02d8881471726774be45a0538e5`
本轮返修 commit：见最终交付消息（只修订原三个文件，不新建任务/会话/转派）
未 push、未开 PR、未合并、未改默认分支、未触发远端 CI、未部署、未改全局环境。

## 0. 结论（PASS / FAIL / BLOCKED / NOT_RUN 分开）

| 范围 | 状态 | 依据 |
|---|---|---|
| 源输入行为绑定 | **4/5，1 BLOCKED** | `tm-allowed`、`tm-deadline-expired` 真实源输入；`tm-wrong-phase-round-end`、`tm-no-active-decision` 经清晰隔离 fault injection 复刻源输入；`tm-decision-mismatch` 的“authority 无 active round”不可表达，**BLOCKED** |
| 定向测试类 | **PASS（7/7）** | Total 7 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0，exit0 |
| TableRuntime 全量 | **PASS** | Total 362 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0，exit0（既有 355 + 新增 7） |
| mutation matrix | **PASS（独立）** | 28 例 = 5 positive + 23 negative；全部按预期；0 unexpected；matrix PASS 与 readiness 分开 |
| binding readiness | **BLOCKED 4/5，native exit 3** | `blockedCaseIds=["tm-decision-mismatch"]`，原因“authority has no active round”；matrix 仍完整运行并 PASS |
| `tm-allowed` 一次扣费/一次版本/重放不重复 | **PASS** | RemoteHuman 实际控制快照；version 0→1；余额 40→30；1 ledger entry；1 组 4 张标记；3 事件；1 commit；重放同一 receipt |
| 拒绝例零副作用 | **PASS** | 版本/事件序/指纹/phase/seat/scores/136 落位/round 事件/cheat 状态/余额/ledger/标记/pending/commit/publication 全部不变 |
| 136 张物理守恒 | **PASS** | 每例前后 136 placement、136 不同 tile id、0..135 |
| 原 fixture 不可变 | **PASS** | SHA256 `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |
| TableRuntime.Tests 非增量 Release 构建 | **PASS** | exit0，0 warning / 0 error |
| solution Release 构建（依赖确认） | **PASS** | 12 项目，0 warning / 0 error，exit0 |
| 精确 V4 输入结构复刻 | **BLOCKED / PARTIAL（见 §9）** | `tm-decision-mismatch` 的 no-active-round；`sessionId`/`autoRiichiDiscard` host/session 字段；exact V4 tile id（fixture 未导出） |
| N3 整体 | **FAIL / 未完成** | 只覆盖 tileMarkHandler 的 5 例（4 bound + 1 blocked）；其余 15 例、sleeveBloom/beamSwap/tileMovement/ledger 未绑定 |
| G1 | **FAIL / BLOCKED（维持不批准）** | 门 2 全字段口径、门 5 三绝技权威链未关闭 |
| G5 | **NOT_RUN** | 未运行 PG/WSS/Unity/IL2CPP/Android/iOS/设备/弱网/跨物理网络 |
| PG / WSS / Unity / IL2CPP / Gradle / 平台 | **NOT_RUN** | 无生产更改，按要求不盲重跑 |
| 远端 CI | **NOT_RUN（本任务）** | 用户提供的 40158293672 / 40158296451（均 1628 PASS）是 caller 已核对事实，不证明本任务 |
| 兄弟在途工作树 | **NOT_TOUCHED** | 未读取/修改/推测通过 |

本交付**不**使用 `complete.flag`，**不**把 4/5、matrix PASS 或 readiness BLOCKED 写成 N3/G1/G5 通过，**不**改 V4 唯一生产基线。

## 1. 范围、身份与文件

### 1.1 身份核对

```text
git status --porcelain=v1        -> 交付前空
git rev-parse HEAD               -> 7d0bb5b72682b02d8881471726774be45a0538e5（上一交付）
git rev-parse --abbrev-ref HEAD  -> wt/v5-n3-table-cheat-admission
git worktree list                -> D:/majiang-wt/n3-table-cheat-admission 7d0bb5b [wt/v5-n3-table-cheat-admission]
```

主 checkout `D:/majiang` 未触碰。

### 1.2 只修订三个文件

```text
M dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs
M tools/v5-golden/probe-table-cheat-golden.ps1
M doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md
```

未改生产、旧测试、fixture、exporter、catalog、manifest、verifier、项目、solution、Unity/Resources/Burst/环境。

## 2. 源 oracle 与逐例输入资格

### 2.1 源

- V4 exporter `engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt:359-427`；常量 `:260-275`。
- V4 判定 `engine/src/main/kotlin/com/majiang/authority/cheat/TileMarkSubmissionHandler.kt:60-114`。
- fixture `tests/v5-golden/cheat-v1.json` 的 `tileMarkHandler`（20 例，只有 `caseId/note/outcome/reason`，无 `goldenInputs`）。

### 2.2 逐例资格（本轮 readiness 的真实分母）

| case | V4 源输入 | V4 判定 | 本轮资格 | 精确说明 |
|---|---|---|---|---|
| `tm-allowed` | seat0、CHEAT_WINDOW、currentPlayer0、`RemoteHuman(false)`、decision d-1、timing(0..10000)、now=1000、4 owned unmarked tiles、points40/cost10 | Allowed | **BOUND** | C# 用 `InitialControllers=RemoteHuman` 表达 kind；控制快照断言 kind/Connected/epoch0。`sessionId` 与 `autoRiichiDiscard=false` 是 host 字段，见 §9.3 |
| `tm-wrong-phase-round-end` | 其余同 allowed，phase=ROUND_END，request 原身份（含 decision d-1），decision/timing 仍存在 | DECISION_MISMATCH | **BOUND（fault injection）** | 真实 Tsumo 终局后，用反射把原 pending+timing 重新装回，保留 decision d-1；命中 `RoundFinished`。非业务可达 |
| `tm-no-active-decision` | `timing=null`，`decision(seat)` 仍存在，其余同 allowed | NO_ACTIVE_DECISION | **BOUND（fault injection）** | 真实 OpenDecision 后反射清 clock timing、保留 pending；请求带 d-1；命中 `not open in the clock`。非业务可达 |
| `tm-deadline-expired` | now==deadline=10000，decision/timing 存在 | DEADLINE_EXPIRED | **BOUND** | 真实 pending+timing、now=10000；命中 `DEADLINE_EXPIRED` |
| `tm-decision-mismatch` | 上下文 `roundIdGiven=null`（authority 无 active round），request 原身份 | DECISION_MISMATCH | **BLOCKED** | 已接受 actor 永远绑定具体 `_round`，没有安全 null-round 端口；不伪造 null actor。见 §9.1 |

`denominator=5`，`bound=4`，`blocked=1`，`readiness=4/5`。这不是 5/5，也不是“本任务全完成”。

### 2.3 controller 与 session 分开记录（D-R1-02）

| 源字段 | C# 可表达性 | 本任务实际 | 是否阻断 |
|---|---|---|---|
| `controller = SeatController.RemoteHuman(false)` 的 kind | **可**：`TableActorOptions.InitialControllers=RemoteHuman` | 四座位均 `RemoteHuman`；每例执行前 `GetControlSnapshotAsync` 断言 `Kind=RemoteHuman`、`Status=Connected`、`ControlEpoch=0` | 不阻断 |
| `RemoteHuman(autoRiichiDiscard=false)` 的 flag | 不可：`TableSeatControlView`/`SeatControllerKind` 不携带该 flag | 记录为 host gap；V4 `isHumanOrAllowedAutomated` 只读 kind，不读该 flag | 不阻断该 verdict |
| `sessionId/currentSessionId = "s-golden"` | 不可：`TableCheatCommandEnvelope` 无 session 字段 | 记录为 host gap；V4 该检查在 equality 通过后才进入 timing/deadline；C# 没有 session gate | 不阻断该 verdict；不冒充已接线 |
| 连接/托管状态 | 可读：`Connected`、`RemoteHuman`、epoch0；无 takeover | 断言实际快照；未发明 controller 入口 | 不阻断 |

## 3. C# 真实执行路径

```text
TableActor.SubmitCheatAsync                  TableActor.cs:223
 -> ProcessCheatCommandAsync                 :1487（request binding / replay）
 -> ProcessCheatCommandInternalAsync         :1564
 -> ProcessCheatCommandCoreAsync             :1639
    -> ValidateAuthorityIdentity             :1893
       round :1922 / pending+decision :1938 / no-timing :1955 / deadline :1964
       state version :1980 / control :1988
    -> RoundCheatTransition.Prepare           RoundCheatTransition.cs:506
       -> CheatSubmission.ApplyTileMark       CheatSubmission.cs:129-...
       -> Materialize                         :1274（CHEAT_REPLAYED :1301）
    -> CommitCheatAndInstallAsync             TableActor.cs:1722
```

拒绝发生在 Domain 之前；`RoundCheatTransition.Guard`（`:1218-1272`）在 Domain 前再次检查 `round.IsFinished`（`:1232`）与 command round（`:1240`）。

## 4. V4 粗码 → C# typed reason 映射

| case | V4 reason | C# 实际分支 | C# typed reason | 资格 |
|---|---|---|---|---|
| `tm-allowed` | null | `ApplyTileMark` Accepted | Accepted | BOUND |
| `tm-wrong-phase-round-end` | DECISION_MISMATCH | `Guard :1232` `round.IsFinished` | `ROUND_FINISHED` | BOUND（fault injection，保留原 pending/decision） |
| `tm-no-active-decision` | NO_ACTIVE_DECISION | `ValidateAuthorityIdentity :1955-1961` | `DECISION_MISMATCH`（Detail `not open in the clock`） | BOUND（fault injection） |
| `tm-deadline-expired` | DEADLINE_EXPIRED | `:1964-1970` | `DEADLINE_EXPIRED` | BOUND |
| `tm-decision-mismatch` | DECISION_MISMATCH | 无 active-round 分支 | **不映射为完成**；stale-round 只作 supplementary | **BLOCKED** |

### 4.1 三个容易混同的分支（显式区分）

| 输入状态 | 位置 | Detail | typed reason |
|---|---|---|---|
| pending 存在、请求无 decisionId | `:1945-1953` | `does not carry the active decision id` | DECISION_MISMATCH |
| pending 存在、decisionId 匹配、clock 无 timing | `:1955-1961` | `not open in the clock` | DECISION_MISMATCH |
| 无 pending、请求带 decisionId | `:1972-1978` | `not open for this seat` | DECISION_MISMATCH |

`tm-no-active-decision` 断言第一种/第三种文本不出现；deadline tick-after 控制断言第三种文本。

## 5. 逐例执行与副作用

每例执行前捕获真实快照：`StateVersion / LastEventSeq / StateFingerprint / IsFinished / Phase / CurrentSeat / Scores / 136 placements / RoundEventCount / Round.Version / Cheat.Version / Cheat.Fingerprint / Balance / LedgerEntryCount / MarkGroupCount / MarkedTileCount / MarkedTiles / PendingTiming / CommitCount / PublicationCount`。拒绝后逐项比较，并断言 `Events=[]`、`CommandVersion=null`、136 守恒。每例执行前另断言 controller 快照。

### 5.1 `tm-allowed`（`:181`）

- RemoteHuman 四座，kind/Connected/epoch0 已断言。
- 执行前：version0；余额40；ledger0；标记0；pending `0:d-1:10000`；commit0；publication0；136。
- 执行：OpenDecision d-1，now1000<10000；4 张真实手牌；cost10。
- 结果：Accepted；CommandVersion1；3 事件（CheatApplied bridge + CheatPointsChanged(-10,newBalance30) + TileMarkApplied(g-golden,seat0)）。
- 执行后：version1；LastEventSeq+3；round事件+1；余额30；ledger1；1组4张；落位签名不变；cheat version前进/fingerprint改变；commit1；publication1；136。
- 重放同一 envelope：`Assert.Same`；version/余额/ledger/标记/commit/publication 不变。
- stale control guard（非 V4 case）：新 requestId + controlEpoch1 → `STALE_CONTROL_EPOCH`；零版本/零扣费/零额外 commit。

### 5.2 `tm-wrong-phase-round-end`（`:267`）

- 先真实 Tsumo 终局（带 decision d-1；终局消费 pending；version0→1；commit1；publication1）。
- **fault injection**：反射把同一 pending d-1 与 timing 重新装回；保留原 decisionId。快照：IsFinished=true、Phase=RoundEnd、`0:d-1:10000`、136。
- 执行：round/seat/request 原身份 + decision d-1。
- 结果：Rejected / `ROUND_FINISHED`；Detail 含 `already finished`；Events=[]；CommandVersion null。
- 执行后：所有快照字段不变；仍 IsFinished；version1；commit1；publication1；余额40；ledger0；标记0；pending 仍 `0:d-1:10000`；136。
- 终止不复活：`OpenDecisionAsync` → `ROUND_FINISHED`，版本不变。
- 明确：终局后仍有 pending 是异常态，仅 fault injection，不是业务可达。

### 5.3 `tm-no-active-decision`（`:346`）

- 真实 OpenDecision d-1（pending+timing 原子）；快照 `0:d-1:10000`。
- **fault injection**：反射 `ClearTimings()`，保留 `_pending`；快照 `0:d-1:-`；其余快照字段不变。
- 执行：请求带 d-1，now1000<10000（非 deadline）。
- 结果：Rejected / `DECISION_MISMATCH`；Detail 含 `not open in the clock`；不含 `does not carry` / `not open for this seat`；Events=[]；CommandVersion null。
- 执行后：version0、余额40、ledger0、标记0、commit0、publication0、pending 仍 `0:d-1:-`；136。
- 明确：pending-without-timing 非业务可达；公开 `OpenDecisionAsync` 总是同时安装两者。

### 5.4 `tm-deadline-expired`（`:400`）

- 真实 OpenDecision d-1（Discard candidate）；快照 `0:d-1:10000`；clock=10000。
- Phase A 直接准入：Rejected / `DEADLINE_EXPIRED`；Detail 含 `authoritative deadline`；Events=[]；CommandVersion null。
- 执行后：version0、余额40、ledger0、标记0、commit0、publication0、pending 仍 `0:d-1:10000`；136。拒绝不静默过期。
- Phase B 另一时序：same clock 调 Tick → Accepted、version1、commit1、publication1；真实 timeout discard 改变落位；pending 全空；136。随后同一 cheat（decision d-1、expectedVersion1）→ Rejected / `DECISION_MISMATCH`，Detail `not open for this seat`；零进一步变化。证明 due tick 先发生时不可能整桌零变。

### 5.5 `tm-decision-mismatch`（**BLOCKED**）与 supplementary guard（`:469`）

- 源 case 不执行，不计入 readiness。原因：V4 `roundIdGiven=null` 表示 authority 无 active round；C# actor 永远有具体 `_round`，无安全 null-round 端口。**不伪造 null actor。**
- 保留 `supplementary_stale_round_identity_is_rejected_with_round_mismatch`：真实 OpenDecision d-1，命令 round=`r-stale-golden` → `ROUND_MISMATCH`，Detail `does not match`；零副作用。该方法明确标为 supplementary，不读 source case 的 `LoadBoundCase`，不计入 4/5。
- fixture 的 `tm-decision-mismatch` 值仍由 20 例结构校验检查；这是 fixture 完整性，不是源 case 执行绑定。

## 6. fixture 严格性、manifest 与 mutation 抗性

- 顶层精确字段；`schema/generator/generatorVersion/extractionStatus` 固定；重复/未知属性失败。
- `tileMarkHandler` 必须 20 例；每例精确 4 字段；重复/未知/缺失 caseId 失败；0 例失败。
- `outcome` 只接受 Allowed/Rejected；`reason` 字符串或 null；Allowed 必须 null、Rejected 必须非空；错型失败。
- 20 例逐项与 exporter `:406-425` 的 V4 oracle 比较；包含 blocked case 的值校验，但不宣称其执行绑定。
- `V5_TABLE_CHEAT_GOLDEN_PATH` 只覆盖 fixture 路径；缺失直接失败，不静默回退。
- 4 个绑定 Fact 先断言 fixture == V4 oracle，再断言真实执行 == C# typed 映射；没有 fixture 自洽冒充执行。
- `binding_readiness_manifest_is_explicit`（`:509`）的 DisplayName 是机器可读 manifest：`N3-BINDING-MANIFEST|denominator=5|bound=4|blocked=1|boundCases=...|blockedCases=tm-decision-mismatch|faultInjection=...|reason=NO_ACTIVE_ROUND_NOT_REPRESENTABLE`。探针据此计算真实 readiness，而不是只数方法名。

## 7. 独立探针：matrix 与 readiness 分离

脚本：`tools/v5-golden/probe-table-cheat-golden.ps1`。

- 独立 `V5_TABLE_CHEAT_GOLDEN_PATH`；只写新 TEMP；内层每 run + 外层整脚本 try/finally 恢复环境；所有产物 UTF8 no BOM。
- 构建失败直接传播；assembly 缺失 exit2；未解析 summary、missing fixture、Total=0 均为 tool failure，不算预期 FAIL。
- readiness 先解析 `N3-BINDING-MANIFEST` 与 `-list 'full/json'`：
  - 必须 `denominator=5/bound=4/blocked=1`；
  - `blockedCases` 必须是 `tm-decision-mismatch`；
  - 4 个 bound case 的真实方法必须存在；
  - blocked case **不得**有 `golden_tm_decision_mismatch*` 方法（防止反向 pin 成 bound）；
  - `supplementary_stale_round...` 必须存在；
  - `faultInjection` 必须是 round-end/no-active-decision。
- readiness 状态为 `BLOCKED` 时**不提前退出**；完整 28 例 matrix 继续运行，独立产出 PASS/FAIL。
- 负例必须 native exit **1**、Total==baseline 7、Failed≥1、Errors=0、Skipped=0、NotRun=0、真实 `[FAIL]`、预期失败文本命中。
- 最终顺序：matrix 失败 → exit1/2；matrix PASS + readiness FAIL → exit2；matrix PASS + readiness BLOCKED → exit3；全 PASS → exit0。

实际结果：

```text
=== BINDING READINESS: BLOCKED ===
denominator=5 bound=4 blocked=1 readiness=4/5 blockedCaseIds=[tm-decision-mismatch]
READINESS-BLOCKED-CASE: tm-decision-mismatch: authority has no active round (V4 roundIdGiven=null);
  the accepted actor always owns a concrete _round and has no safe null-round port
bindingReadiness=BLOCKED readiness=4/5 discoveredMethods=7 mutationMatrix=PASS
cases=28 positive=5 negative=23 expectedTotal=7 unexpected=0 toolFailure=False
PROBE-BLOCKED: mutation matrix PASS, binding readiness BLOCKED 4/5
native exit 3
```

`matrix=PASS` 是独立事实；`readiness=BLOCKED` 不再被 matrix 绿灯覆盖。

### 7.1 本轮返修映射（D-R1-01 / D-R1-02）

| 缺陷 | 修复 | 验证 |
|---|---|---|
| **D-R1-01** readiness 5/5 与已记录缺口冲突；round-end 去 decisionId；decision-mismatch 用旧 round 替代 no-active-round | ① `tm-decision-mismatch` 明确 BLOCKED，不计入 bound；② round-end 用反射重装原 pending+timing 并保留 decision d-1；③ manifest 声明 5 分母/4 bound/1 blocked；④ 探针解析 manifest 并输出 blockedCaseIds/原因/nonzero exit3；⑤ matrix 在 readiness 之后仍完整运行；⑥ 删除“5/5/full/完成”口径 | 定向 7/7；matrix 28/28 PASS；readiness BLOCKED 4/5 exit3；`readiness.json` 的 blockedCaseIds/原因；`summary.json` matrix=PASS 独立 |
| **D-R1-02** 四座 Computer 与源 RemoteHuman 不符；报告误称 controller 无端口 | ① `InitialControllers=RemoteHuman`；② 每例执行前 `GetControlSnapshotAsync` 断言 Kind=RemoteHuman/Connected/epoch0；③ 报告把 controller kind（可表达）与 autoRiichiDiscard/sessionId（host gap）分开 | 定向 4 个 golden/1 supplementary 全部通过；控制断言真实读取快照；报告 §2.3/§9.3 |

## 8. 真实验证命令、native exit、计数与日志

CLI `D:/Unity/dotnet/dotnet.exe`（8.0.426）。证据根：

```text
E4 = C:/Users/sncdc/AppData/Local/Temp/n3-table-cheat-rework-accept-bb39f6c9e2d54e8e8cfa5cc2d05361e7
P4 = C:/Users/sncdc/AppData/Local/Temp/table-cheat-golden-probe-4f05319b36ec4f12af5eac3845502637
```

| 验证 | 命令 | native exit | 计数 | 日志 |
|---|---|---:|---|---|
| TableRuntime.Tests 非增量 Release 构建 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v:minimal` | 0 | 0 warning / 0 error | `E4/runtime-build.log` |
| 定向新类 | `...Mahjong.TableRuntime.Tests.dll -noLogo -class '*N3TableCheatGoldenAdmissionTests*'` | 0 | **7 / 0 / 0 / 0 / 0** | `E4/targeted.log` |
| TableRuntime 全量 | `...Mahjong.TableRuntime.Tests.dll -noLogo` | 0 | **362 / 0 / 0 / 0 / 0** | `E4/runtime-full.log` |
| solution Release 构建 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental -v:minimal` | 0 | 12 项目 / 0 warning / 0 error | `E4/solution-build.log` |
| mutation/readiness 探针 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-table-cheat-golden.ps1` | **3（预期 BLOCKED）** | matrix 28 / 5 positive / 23 negative / unexpected0；readiness 4/5 | `E4/probe.log`；`P4/matrix.csv`、`summary.json`、`readiness.json` |

未运行（NOT_RUN）：solution 测试全量、PG、WSS、Unity、IL2CPP、Android/iOS、设备、弱网、跨物理网络、Gradle/V4 生产、远端 CI。无生产更改，不盲重跑。

## 9. 缺口、BLOCKED 与准确最小后续范围

1. **`tm-decision-mismatch` 源输入 BLOCKED（no active round）**
   - V4 `roundIdGiven=null` 表示权威上下文没有 active round；C# `TableActor` 永远绑定 `_round`，没有安全 null-round 端口。
   - 不伪造 null actor；不把 `r-stale-golden` 旧 round 冒充该 case。supplementary stale-round guard 仅保留 C# guard 回归，不计入 4/5。
   - 若必须复刻该源输入：需要 actor 生命周期/端口级改动（允许无 round 的 actor 状态或权威 round 缺失上下文），涉及 `TableActor.cs` / `TableRuntimeContracts.cs` 生产改动；**本任务禁止扩大范围**。
2. **两个 bound 例是 fault injection，不是业务可达**
   - `tm-wrong-phase-round-end`：终局后重装 pending+timing；`tm-no-active-decision`：保留 pending、清 clock timing。
   - 生产终局清理/OpenDecision 原子性未改；报告和测试均明确异常态。
3. **host/session 字段（不阻断该 verdict，但必须记录）**
   - `sessionId/currentSessionId`：actor 无 session 字段；C# 没有 session gate；不冒充已接线。
   - `RemoteHuman(autoRiichiDiscard=false)`：C# control view 不携带该 flag；V4 cheat handler 只读 controller kind，不读该 flag。
   - controller kind/连接/托管：`RemoteHuman`、`Connected`、epoch0 已通过真实控制快照断言；不存在 controller 入口缺口。
4. **tileMark 段没有 `goldenInputs`，exact V4 tile id 不可导**
   - 本任务按 exporter 的“4 张 owned unmarked hand tiles”输入类，从真实 C# 回合取牌；没有发明 goldenInputs，并以 N20 负例拒绝 `goldenInputs` 注入。
   - 若必须 exact 实体 id：需要 V4 exporter 导出输入并升级 fixture schema（**本任务禁止**）。
5. **只覆盖 5/20 个 tileMark case；sleeveBloom/beamSwap/tileMovement/ledger 未绑定**
   - 最小后续：同一测试类扩展其余 15 例；另起 N3 任务绑定其他段；均不需生产改动。
6. **N3/G1/G5 边界**
   - N3 整体 FAIL/未完成；G1 未批准；G5 未完成；V4 仍唯一生产。

## 10. 证据与 SHA256

```text
E4 = C:/Users/sncdc/AppData/Local/Temp/n3-table-cheat-rework-accept-bb39f6c9e2d54e8e8cfa5cc2d05361e7
P4 = C:/Users/sncdc/AppData/Local/Temp/table-cheat-golden-probe-4f05319b36ec4f12af5eac3845502637
```

关键日志/产物：

| 文件 | SHA-256 |
|---|---|
| `E4/runtime-build.log` | `8B8B2AAF7CAE38B202068D8CB222A6004846720959FBBE9CA8F527C803EB8AD8` |
| `E4/targeted.log` | `72F2CE146CFB18B83CAB8E15124D522B7D1B27D1DA5326410969C521F0266F60` |
| `E4/runtime-full.log` | `54A05836377C6B2BF41D7C311C5026576FC594E60A2875C16589CBC42C2F3AE7` |
| `E4/solution-build.log` | `074D3DD823AD880300BEAB9E26A99707AC727C30281E9AA164505B4D6FB0745B` |
| `E4/probe.log` | `91C5A8B364DD7D2932CC66D8A5A86B25B976ECCC81511460460844691532CD21` |
| `P4/summary.json` | `D5D648507975BA1D092BB55CB469546190C66CAF4AE5AF4FC0FC897945DDEC97` |
| `P4/readiness.json` | `2B34E9C2D64FE59DF9E4CC9B3139FF2637CB1887BD7BDD69799178BDFC15DAF8` |
| `P4/matrix.csv` | `5AB51ED358251333237AA2EF8B123D87FB5B67B3D0BE0E39EAAFEFB0C5B45B56` |
| `P4/list-discovered.json` | `4E383CE7891A49FC293C8DA2DC6110D44D10591C9A07D75E7412EFD1591D1A7E` |

源/fixture：

| 文件 | SHA-256 |
|---|---|
| `N3TableCheatGoldenAdmissionTests.cs`（交付前） | `5D18A2BCD6FB642D1D16D21CBEBDB5F23687A421C6BEE32DF5D616E7B74A58AA` |
| `probe-table-cheat-golden.ps1` | `DAB8563E1C511FCB80BA08EAC638E02E314093C391C80625776F3AD56BA31ABA` |
| `tests/v5-golden/cheat-v1.json` | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |

`E4/sha256-key.txt` 保存完整路径/长度/哈希。

## 11. 提交、逐文件清单、clean 与 diff-check

本地返修 commit（不 push）：`fix(v5-n3): separate blocked readiness from mutation matrix`。

逐文件：

```text
M dotnet/tests/Mahjong.TableRuntime.Tests/N3TableCheatGoldenAdmissionTests.cs
M tools/v5-golden/probe-table-cheat-golden.ps1
M doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md
```

提交后：

```text
git status --porcelain=v1          -> 空
git diff --check                   -> exit0
git diff --check 285781d..HEAD     -> exit0
git diff-tree --check HEAD^ HEAD   -> exit0
git show --stat --oneline HEAD     -> 仅上述三文件
```

（最终 commit 哈希见交付消息。）

## 12. 结论边界

- 本报告 PASS 仅为“4/5 源输入行为绑定 + 1 明确 BLOCKED + 独立 28 例 mutation matrix + 本地回归”。
- `tm-decision-mismatch` 未完成，不把 supplementary stale-round guard 计为源 case。
- 不把 4/5、matrix PASS 或 readiness BLOCKED 写成 N3/G1/G5、远端 CI、Unity/IL2CPP、设备或跨物理网络通过。
- 用户提供的两次远端 CI（40158293672 / 40158296451，均 1628 PASS/0 FAIL/0 SKIP）是 caller 已核对的历史事实；不含本轮新增/返修测试，不能证明本任务。
- V4 仍是唯一生产架构；无 `complete.flag`。
