# N-3 前置独立审计报告 — 黄金样本字段级映射与覆盖口径核查

- **审计执行**：Claude Code（本 agent），独占 worktree `D:/majiang-wt/n3-golden-field-audit`，分支 `wt/v5-n3-golden-field-audit`
- **冻结基线**：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（PR19 最新已发布头；工作树 status clean，HEAD 即该提交）
- **审计对象**：V5-1-REVIEW §7.1 N-3 [MAJOR]（G-028a 事件序列 + mv-swap-drawn-with-wall-backfill + 20 项 partial 的断言边界），以及"已导出黄金样本对齐率为 100%"这一 G1 门口径的字段级核实
- **报告日期**：2026-10-25
- **性质声明**：本报告只产出审计证据与最小补齐包建议；**不实施任何 P0.1/已接受项开发，不改 catalog/manifest/fixtures/生产或测试代码，不改固定头已接受 G1 盘点报告**。本报告组件完成 ≠ N-3 关闭 ≠ G1 批准。

---

## 0. 前置核实（审计开工前实拍）

| 项 | 值 | 验证命令 |
|---|---|---|
| HEAD | `8254c9b783740ddf519cebc15bb50a8f9974cf58` | `git rev-parse HEAD` |
| 分支 | `wt/v5-n3-golden-field-audit`（独占） | `git branch --show-current` |
| status | clean（开工时与提交前均为 clean） | `git status --porcelain` |
| V4 唯一生产 | Kotlin `engine/`；V5 `dotnet/` 生产性 false（catalog baseline `v5Production:false`） | catalog-v1.json baseline |
| G1 | 不批准（固定 8ee22eb 报告 7 PASS/2 FAIL） | CODEX-V5-G1-FIXED-HEAD-REPORT-ACCEPTANCE.md |
| G5 | 未完成 | 同上 |
| 只读文件 | `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`（未复制未提交未修改） | — |
| 禁触文件 | 主 checkout 未提交的 CLAUDE.md、PROMPTS.md、任务书/协议、Unity 环境/Resources/Burst | 未复制、未提交、未修改 |

---

## 1. 审计方法与可复算口径

1. **机器枚举**：对 6 份 fixture（5 份 catalog 内 + ai-v1）以 PowerShell `ConvertFrom-Json` 递归走查全部 JSON 叶路径，产出精确路径清单（`*-leaves.txt`），再归一化 `[N]→[*]` 聚合为逻辑路径清单（`*-agg.txt`）。
2. **scoring-v1 特例**：8.3MB/10,238 case 的全量逐叶走查在 PS 5.1 哈希表实现下不可行，改用**形状提取**（`scoring-shape.ps1`）：对 7 个组各取首/中/末 3 个样本做逻辑路径并集，并对**全部**样本做顶层键集齐性校验（7 组 `distinctTopLevelKeySets=1`，形状齐性成立）。组样本数 48+4+9+6+24+147+10000=10,238 与 catalog `caseTotal` 精确一致。
3. **C# 消费者深读**：5 个 catalog 内 C# 读者类（Physical 9 Facts / Scoring 7 / Round 6 / Cheat 16 / Shuffle 4 = 42 Facts）+ catalog 外 `AiReplayGoldenTests`（3 Facts）全文逐行读，对每个导出组列出"绑定字段集"。
4. **V4 源锚点**：fixture 内嵌 `source.file/testClass/testMethod` + exporter .kt 实读 + V4 生产调用链（ApplicationCoordinator / TileMovement / MatchSession / RoundTransitionPlanner）grep 锚定。
5. **v4Commit 真实性**：6 份 fixture 的 `v4Commit` 逐一 `git cat-file -t` 验证，全部为本仓库真实提交（见 §3 表）。
6. **exporter 源链完整性**：5 份 catalog fixture 声明的 exporter `sha256Raw` 与冻结头仓库内 exporter .kt 实测 SHA256 逐一比对（§3.8，5/5 精确一致）。
7. **DEEPSEEK 计划交叉核对**：实读 `doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`（2058 行）与本报告三处专项结论交叉——无矛盾，且三处直接佐证：`:560`（黄金样本定位为"语言中立抽取"，非全保真翻译——支持 §6 ScoreSettled 语义映射判定）；`:588`（41/42 役种口径"冻结黄金前必须确认"——支持 G-019 归入规则裁决而非字段缺口）；`:1736`（V5-1A 退出标准"黄金样本 100% 对齐"即 G1 门口径出处，本报告 §2.1 即是对该口径的字段级核实）；`R14`（"测试全绿假信心…明确未读/未跑"——即 §2.6 总判定）。计划书对 mv-swap-drawn-with-wall-backfill 与 ScoreSettled **无任何预授权/预排除表述**，§7 以 P1.4 任务文本 + 双侧生产源码为设计来源的定性不受影响。
8. **侥幸通过判定**：对每个数值/序列断言问一句"C# 若产出错误值/错误事件序列，现有断言是否仍会 PASS"。

---

## 2. 总判定（先给结论）

1. **"已导出黄金样本对齐率为 100%" 不成立（字段级）**：按"已导出 expected 字段被真实 C# 断言消费"的口径，5 份 catalog fixture 中 round-v1 与 cheat-v1 存在**成片的已导出但未断言字段**（§4、§5）；physical/scoring/shuffle 三份在自身导出范围内接近全消费（残留均为"不可机械比较/不可表示"并已有声明）。10,382 case 数与 17 full 都**不是**全字段行为对齐的分子——catalog 自己的 `status=evidence-index-not-acceptance` 与 `notice` 已声明这一点，本次审计以字段级证据坐实。
2. **G-028a 定性修正**：这不是"C# 缺 ScoreSettled 枚举"的迁移缺口，而是**语义映射 + 断言弱化**问题（§6）。C# Domain 以 `RoundSettlement/RoundResult` 字段承载结算信息而非独立事件是合法表示；真正的缺口是 round-v1 的两个重放测试都**没有对事件序列做任何精确断言**（roundTerminal 只 `Contains(TsumoWin)`，chankanRon 只有两个 `DoesNotContain`）——事件序列面的错误值今天可以全量侥幸通过。
3. **mv-swap-drawn-with-wall-backfill 定性修正（新发现）**：该路径**不是 V4-only**，C# 有等价生产入口且已被行为重放——`CheatGoldenTests.cs:638-642` 的差距声明（"该法律在 C# 侧无入口"）**不准确**（§7）。剩余缺口收窄为：m1b 黄金字段未与 C# 重放交叉绑定 + 一处不可表示的地址级表示差 + 一段需要更正的注释。**无需用户规则裁决，无需刷新冻结记录**。
4. **AgariYame**：行为在 C# match 层完整存在（`MatchSession.ChooseDealerEnd`），V5-1-REVIEW 相关疑虑应只保留"round 层命令入口枚举"一点，不构成缺全部行为（§8）。
5. **ai-v1.json 索引缺口**：第 6 份 fixture 存在、被 `AiReplayGoldenTests`（3 Facts 全 PASS）**全字段强消费**，但不在 catalog `fixtures[]`（catalog 明示只收"five accepted V4-exported fixtures"），catalog G-049 `coverage=none` 按"五份"口径自洽、按"仓库现存"口径已过时（§3.7）。
6. **45/45 定向 Golden Facts 全 PASS**（原生 exit 0，日志与 SHA256 见 §11）：现有断言无一失败——**失败不在断言结果，而在断言覆盖**。
7. **一次性协议格式缺陷清单**见 §13（同类合并后 4 项：2 MAJOR / 2 MINOR + 3 条语义映射 INFO 记录）。本审计按任务书**不实施修复**；修复经由 §9 补齐包由 caller 独立下发。

---

## 3. 六份 fixture 字段级映射总表

### 3.1 physical-v1（catalog 内）

- **SHA256** `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995`；v4Commit `32f645ec…`（真实）；逻辑路径 296；C# 消费 `PhysicalGoldenTests`（9 Facts 全 PASS）。
- **来源链（最强）**：fixture 内嵌 `exporterSourceSha256`，C# 逐字节对回仓库内 exporter 文件（`PhysicalGoldenTests` provenance Fact）。
- **组级映射**：

| 导出组/字段域 | C# 断言 | 判定 |
|---|---|---|
| G-001a/b/c 身份/赤五/136 张 | 全字段（TileId 字段、136 tiles、redFiveIds 19/55/91） | 行为差分 |
| G-002 20 cases | 全 case 全字段 | 行为差分 |
| G-003a 32 墙→map | 逐墙 map + 136 守恒 + 4352 位置 + digest | 行为差分 |
| G-003b probeA/probeB | 结构映射（探测路径结构一致） | 仅结构（probe 语义无生产对应，已声明） |
| duplicateKey | — | 不可表示（V4 MutableMap 无法构造重复键 case，NOT_CONSTRUCTIBLE） |

### 3.2 scoring-v1（catalog 内）

- **SHA256** `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E`；v4Commit `97831c88…`（真实）；7 组 10,238 case；C# 消费 `ScoringGoldenTests`（7 Facts 全 PASS）。
- **组级映射（含逐字段核验）**：

| 组 | 样本 | C# 断言范围 | 判定 |
|---|---|---|---|
| winSamples | 48 | 全字段：score.*（han/fu/basePoints/rawBasePoints/rawBasePointsExact/capCode/yakumanMultiplier/isYakuman）、fu.lines 逐行、paymentPlan.components 逐项、dora 六元组、yaku 逐项、honbaAddition/riichiAddition/totalHan/scoreHan/totalPoints/status/yakumanCount | 行为差分（最强） |
| noWinSamples | 4 | status=null 断言 | 行为差分 |
| fuSamples | 9 | fu lines/subtotal/roundedUp | 行为差分 |
| doraSamples | 6 | indicator/red/total 组合 | 行为差分 |
| settlementSamples | 24 | deltas/newScores/remainingRiichiSticks 全字段 | 行为差分 |
| scoreSamples | 147 | 全字段：basePoints/capCode/isYakuman/rawBasePoints(+Exact)/yakumanMultiplier/payments×5（逐个调 C# 支付函数比对）/paymentPlans×4（components 逐项+kind+total） | 行为差分 |
| randomSamples | 10,000 | `CompareRandomSample`（:372-439）消费 expected **全部**导出字段：status/yaku(code+han+count)/yakuHan/scoreHan/isYakuman/yakumanCount/fu/basePoints/capCode/totalPoints + 分布 meta（win/noWin/open/closed/tsumo/ron/removed/yakuman/yakuCounts）逐项对账 | 行为差分 |

- **结论**：randomSamples "比较字段少" 是 **exporter 导出集本身较小**（无 paymentPlan/honba/riichi/fu.lines），而非 C# 跳过已导出字段——**已导出未断言 = ∅**；与 curated scoreSamples 的差异全部归入"尚未导出"桶。
- 残留（对应 catalog partial G-013/014/015/019）见 §7：均为"特定锚定 case 尚未导出"，非已导出未断言。

### 3.3 round-v1（catalog 内）— 字段级缺口最集中的一份

- **SHA256** `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`；v4Commit `21a7d2c4…`（真实）；逻辑路径 200；`workingTreeDirty=true`（导出时 V4 工作树有测试侧脏文件——来源链弱于 physical，审计如实记录）；C# 消费 `RoundReplayGoldenTests`（6 Facts 全 PASS）。
- **roundTerminal（RG-001）逐字段绑定表**（C# = `RoundTerminalReplaysExactly` :101-148）：

| fixture 字段 | C# 绑定 | 判定 |
|---|---|---|
| input.roundId/dealer/scores/riichiSticks | ✅ 作为 P13.Dealt 构造输入 | 行为差分 |
| **input.phaseBefore / input.versionBefore** | ❌ 未绑定（C# 硬编码 `RoundPhase.Discard` 起手） | 已导出未断言 |
| settlement.resultId/winner/finalScores/remainingSticks/completedAtUtc/completedAtMonotonicMillis | ✅ 作为 Tsumo 命令载荷 | 行为差分 |
| **settlement.outcome** | ❌ C# 硬编码 `RoundOutcome.Tsumo(winner)`，导出串 "Tsumo" 未比对 | 已导出未断言 |
| **settlement.totalDeltas** | ❌ 未绑定（用 expected.resultDeltas 替代比对） | 已导出未断言 |
| expected.versionAfter / resultFinalScores / resultDeltas / rejectedActions[3] / versionAfterRejections | ✅ 逐值 | 行为差分 |
| **expected.phaseAfter** | ⚠️ C# 硬编码 `RoundPhase.RoundEnd`，未与导出值比对 | 断言弱化（硬编码） |
| **expected.resultOutcome** | ⚠️ 硬编码 `RoundOutcomeKind.Tsumo` | 断言弱化（硬编码） |
| **expected.eventTypes[3]** | ⚠️ 仅 `Contains(TsumoWin)`（:134） | **已导出未断言（G-028a 核心）** |

- **chankanRon（RG-002）逐字段绑定表**（C# = `ChankanRonReplaysExactly` :151-233）：

| fixture 字段 | C# 绑定 | 判定 |
|---|---|---|
| input.roundId/kanserSeat/ronSeat/addedTileId/eligibleSeats | ✅ | 行为差分 |
| **input.meldTiles[3]** | ❌（P13.ChankanDealt 预置面子，导出值未比对） | 已导出未断言 |
| expected.rinshanPointerAfterKan/totalKanCountAfterKan/rinshanPointerAfterRon/totalKanCountAfterRon/winner/loser/resultId/finalScores/totalDeltas/versionAfterRon | ✅ 逐值 | 行为差分 |
| expected.phaseAfterKan / phaseAfterRon / resultOutcome | ⚠️ 全部硬编码（ChankanWindow/RoundEnd/Ron），导出值未比对 | 断言弱化（硬编码） |
| **expected.eventTypes[3]** | ❌ 完全未断言（仅 DoesNotContain KanDeclared/RinshanDrawn） | **已导出未断言（G-028a 核心）** |
| **expected.meldTypeAfterKan** | ❌ | 已导出未断言 |
| **expected.legalRonSeat1** | ❌（窗口资格由 eligibleSeats 间接走过，导出布尔未比对） | 已导出未断言 |
| expected.hasKanDeclared / hasRinshanDraw | ⚠️ 语义映射非 1:1：V4 是"加杠/岭上摸牌**发生过**"的状态旗标，C# `DoesNotContain` 是"抢杠和原子回滚后事件表**不保留**该事件"——两者可同时为真，**不可强迫同值对照**，需映射注释 | 语义映射（不可机械 1:1） |
| 状态哈希守恒 | ✅（hashBefore 前后比对，等效物理守恒） | 行为差分 |

- 其余组：matchProgression 16 case **全字段消费**（config 10 字段 + input 9 + expected 10，含 allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame/tieBreakSeats/tenpai/transferSeat 等）；decisionClock 41 ops 全走 + ≥35 断言；controlEpoch 6 ops 全走；unexportedSamples[3] 显式声明未导出项（full 136-placement state digest、server.ConnectionControl grace/reclaim、加杠里宝揭示）。

### 3.4 cheat-v1（catalog 内）

- **SHA256** `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；v4Commit `a96c195c…`（真实）；逻辑路径 204；C# 消费 `CheatGoldenTests`（16 Facts 全 PASS）。
- **case 级消费矩阵**（75 handler cases = tileMark 20 + sleeveBloom 32 + beamSwap 23）：

| 节 | 导出 | 行为重放（真 C# 入口 + 黄金对照） | reason 映射覆盖（Handler_Sections_CoverEveryRejectionReason） | 未绑定 |
|---|---|---|---|---|
| tileMarkHandler | 20 | 6 全重放（not-your-turn/riichi-locked/invalid-selection-three/tile-not-owned/insufficient-funds/already-marked）+ 1 半重放（tm-response-window：V4 归并 WRONG_PHASE ↔ C# 特化 ResponseWindowOpen"同族"，reason 导出值有比对、错误码走同族映射） | 其余 13 仅 reason 完整性 | 13 case 的行为级重放 |
| sleeveBloomHandler | 32 | 6 goldenInputs 驱动全重放（sb-activate-allowed→sb-choose-second、sb-activate-rinshan-allowed→sb-choose-rinshan、sbo-activate-allowed→sbo-choose-allowed）+ 4 拒绝重放 + 1 stale 重放（sb-choose-stale-turn）+ 1 纯 C# 侧行为断言（sb-choose-first，无黄金对照） | 其余 20 仅 reason 完整性 | 20 case 的行为级重放 |
| beamSwapHandler | 23 | 4 拒绝重放 + 1 stale 重放（bm-choose-stale-turn）+ 1 仅 outcome 比对（bm-choose-keep :543-544） | 其余 17 仅 reason 完整性 | 17 case 的行为级重放（含 bm-activate-allowed、bm-choose-swap、deadwall 全系、replacement-allowed 等 **Allowed 主路径 case**） |

- **ledger 节 9/9 子键全消费**（initialPoints/chargeOnce/replaySamePayload/conflictDifferentPayload/insufficientFunds/overflow/invalidSeat/flowSummary/snapshotRoundTrip）——行为差分。
- **tileMarkState 节 3/3 子键全消费**（registerDealtRound/addGroup/expiry 全链版本断言 :280-329）——行为差分；partial 残留仅在 capacity 两 case（reason 覆盖、未行为重放，见 §7 G-033）。
- **tileMovement 6/6 caseId 全被引用**：mv-swap-hand-with-wall-direct 行为重放（同构 Wall(54)↔wall[1]）、mv-reservation-delivery 黄金结构 + C# 交付入口重放、mv-reservation-delivery-then-beam 136 摆位全等重建、mv-rinshan-and-drawn-swap/mv-swap-hand-with-dead-wall 重放、**mv-swap-drawn-with-wall-backfill 仅结构**（§7 专查）。
- **侥幸通过判定**：reason 映射表 31 V4 reasons→C# CheatErrorCode 是完备性断言，但**映射正确 ≠ 行为正确**；24/75 case 的行为重放之外的 51 case，其真实 C# 行为若与 V4 相悖，现有断言不设防（其中 beamSwap 的 5 个 Allowed 主路径 case 最为显著——成功路径的事件/副作用形状无黄金对照）。

### 3.5 shuffle-v1（catalog 内）

- **SHA256** `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`；v4Commit `37b4dd61…`（真实）；逻辑路径 199；C# 消费 `ShuffleGoldenTests`（4 Facts 全 PASS）。
- slotMatrix 16 组逐值对 `ShuffleSlots`（含 DeadWallStart 越界禁令）；demandVectors 6×34 逐值（1e-12）；overlapMatrix 6×6 双精度逐值 + **测试内独立冻结硬值双重锚**（2/√117、3/√39、4/√52、0、1）；32 墙 × 5 变体全部 136 唯一/赤五恒 3/恒等律/死山 122..135 隔离——**行为差分**。
- walls 不做跨运行时逐张复现（D-09 已在文件头/rngGapNote 双声明）——**不可表示（有声明），非缺口**。

### 3.6 ai-v1（**catalog 外第 6 份**）

- **SHA256** `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；v4Commit `205506f0…`（真实，恰为 catalog 冻结头）；逻辑路径 198；7 case；C# 消费 `AiReplayGoldenTests`（3 Facts 全 PASS，经 csproj `<None Include>` 拷贝到输出目录读取）。
- **消费强度全场最高**：7/7 case 经 `AiDecisionService.Replay + Submit` 行为重放（`ThrowingStrategy` 保证**绝不运行启发式**）；before/after 状态 `DeepEquals` 全树比对；v5.authorityMapping 六种 mode 逐一构造并校验；`recordedIntent` 绑定防换动作；最后一个 Fact 还接入 `DeadlineArbiter/ControlRegistry` 做 tick 级集成。
- **索引缺口**：catalog `fixtures[]=5`（notice 明示 "five accepted V4-exported fixtures"），manifest 无 ai-v1 行，catalog G-049 `coverage=none`（"No AiDecisionRecorded/intent or stale-AI submission fixture"）与仓库现状**不符**——ai-v1 恰好就是 recorded-intent + stale-AI submission fixture（AI-002-old-round / AI-003-old-decision / AI-004-old-epoch / AI-005-wrong-seat / AI-006-candidate-not-legal / AI-007-exact-deadline / AI-001-legal-intent）。
- **定性**：行为无缺口；**索引/盘点缺口**。G-049 的补齐不是写代码，而是把 ai-v1 纳入 catalog fixtures[] 与 manifest 行并重算 G-049 coverage（是否纳入"五份冻结口径"是 caller 的口径决定，本审计只报告事实）。

### 3.7 v4Commit 真实性核验（机器可复算）

| fixture | v4Commit | `git cat-file -t` | 提交日期/主题 |
|---|---|---|---|
| ai-v1 | 205506f07f6a… | commit | 2026-10-24 docs(v5): record independent physical golden acceptance |
| cheat-v1 | a96c195c852e… | commit | 2026-10-23 feat(v5): add AI intent boundary and stale-result checks |
| physical-v1 | 32f645ec9b13… | commit | 2026-10-24 docs(v5): record P3.2c independent and integrated Wire acceptance |
| round-v1 | 21a7d2c49539… | commit | 2026-10-23 feat(v5): port scoring rules and V4 golden differential tests |
| shuffle-v1 | 37b4dd610c5e… | commit | 2026-10-23 docs(v5): detail phases 2 through 5 tasks and gates |
| scoring-v1 | 97831c88f664… | commit | 2026-10-23 feat(v5): freeze Contracts and Wire v1 schema |

### 3.8 exporter 源链完整性（catalog 声明 vs 冻结头仓库实测）

| fixture | exporter 路径（catalog 声明 = 仓库实际） | catalog `sha256Raw` | 仓库实测 SHA256（`sha256sum`） | 一致 |
|---|---|---|---|---|
| physical-v1 | engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt | `B86286AE5CAE67BCB1E395C48CA755E884439683367D76AABC6711D6A7775EA4` | `b86286ae5cae67bcb1e395c48ca755e884439683367d76aabc6711d6a7775ea4` | ✅ |
| scoring-v1 | engine/src/test/kotlin/com/majiang/rules/V5ScoringGoldenExporterTest.kt | `284E65279640AFD5AE9B06F7FF4ACAA5E2F2A2F0CC732B802110D594027913A6` | `284e65279640afd5ae9b06f7ff4acaa5e2f2a2f0cc732b802110d594027913a6` | ✅ |
| round-v1 | engine/src/test/kotlin/com/majiang/rules/V5RoundGoldenExportTest.kt | `D3CBD13FE7DE38155CD493192EBD1CCA052F643936426AEB519FB3C9AEA3A28B` | `d3cbd13fe7de38155cd493192ebd1cca052f643936426aeb519fb3c9aea3a28b` | ✅ |
| cheat-v1 | engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt | `402AA8EBF03B8E3F2AEB5712A94E7FC1C8508EE327981BC014F7A0A2A67902E4` | `402aa8ebf03b8e3f2aeb5712a94e7fc1c8508ee327981bc014f7a0a2a67902e4` | ✅ |
| shuffle-v1 | engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt | `EF6CC325110F640C372576B07FA4D2C0B5F7B84AA2D69F9E84307A81240A1B3E` | `ef6cc325110f640c372576b07fa4d2c0b5f7b84aa2d69f9e84307a81240a1b3e` | ✅ |
| ai-v1（catalog 外，仅实测存档） | engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt | — | `f70da6a4afbb06fa1bc80f2d0a6573bfb5a63dd302b2ca52122a551d623c76f8` | — |

**判定**：产出五份 catalog fixture 的 exporter 文件与冻结头仓库内现存文件**字节级一致（5/5）**——导出源链完整，fixture 确为仓库内当前 exporter 所声称的代码产物；不存在"fixture 由已漂移的旧 exporter 生成"的风险。physical-v1 另有 fixture 内嵌 `exporterSourceSha256` 的 C# 自检（§3.1），构成双重链。

---

## 4. 20 项 partial 严格拆分（尚未导出 / 已导出未断言 / 不可表示）

分母定义：**"已导出但未断言"只数 fixture 内真实存在、且存在可行 C# 对应断言的字段/case**；无 V4 导出物的归"尚未导出"；不可构造或不可机械比较的归"不可表示"。

| ID | owner | 已导出但未断言（精确） | 尚未导出（精确） | 不可表示/语义映射 |
|---|---|---|---|---|
| G-003b | P1.1 | — | — | probeA/probeB 探测语义无生产对应（结构映射已做）；duplicateKey NOT_CONSTRUCTIBLE |
| G-008 | P1.1 | —（cheat-v1 tileMovement 的 2 个 swap case 已重放） | draw/discard/pon/kan 的 TileMovement case（五份均无） | — |
| G-013 | P1.2 | — | 恰好 4han/30fu/7700 锚定 case | — |
| G-014 | P1.2 | — | 恰好 28→30 锚定 case | — |
| G-015 | P1.2 | — | 单赤宝牌+指示牌 1/1/2 case | — |
| G-019 | P1.2 | —（42 yaku codes 全数断言） | — | 41/42 役裁决（D-01）未决——规则裁决项，非字段缺口 |
| **G-028a** | P1.3 | roundTerminal: eventTypes 精确序列、phaseAfter、resultOutcome、input.phaseBefore/versionBefore、settlement.outcome/totalDeltas；chankanRon: eventTypes、meldTypeAfterKan、legalRonSeat1、phaseAfterKan/Ron、resultOutcome、input.meldTiles（全表见 §3.3） | — | hasKanDeclared/hasRinshanDraw ↔ C# 事件表不保留 = 语义映射；ScoreSettled 协议事件生产者 = P3 TableRuntime 项（§6） |
| G-031 | P1.3 | — | 无事件命令的 event-list/version 断言所需导出物 | — |
| G-032 | P1.4 | — | 单 case 完整 post-state/组牌/位置/deadline | — |
| G-033 | P1.4 | tm-capacity-groups / tm-capacity-tiles 两 case 行为重放（tileMarkState 三子键本身已全消费） | — | — |
| G-034 | P1.4 | tileMark 13 case 行为重放（20−6 全重放−1 半重放） | — | — |
| G-035 | P1.4 | sleeveBloom 20 case 行为重放（32−6−4−1−1） | — | — |
| G-036 | P1.4 | sbo 其余 timing 分支 case 行为重放 | — | — |
| G-037 | P1.4 | stale/wrong-window/no-offer 未重放 case（各节 stale 之外） | — | — |
| G-038 | P1.4 | beamSwap 17 case 行为重放（23−4−1−1），**含 5 个 Allowed 主路径 case** | — | — |
| G-039 | P1.4 | bm invalid/stale/foreign 未重放 case | — | — |
| G-041 | P1.4 | —（ledger 9/9 子键已消费） | round version / mahjong scores / 136-tile 守恒的账本对照导出物 | — |
| G-043 | P1.5 | — | 非法 seat/config 拒绝 case | — |
| G-046 | P1.3 | — | duplicate-open 拒绝、旧 roundId-key open 拒绝 case（现有 9 op 全为成功路径） | — |
| G-048 | P1.3 | — | server.ConnectionControl grace/reclaim（exporter 显式声明不导出，unexportedSamples[3] 在案） | — |

**20 项 partial 的分子合计**：已导出未断言 ≈ 50 个 case 级条目 + round-v1 18 个字段级条目，全部集中在 **round-v1 与 cheat-v1 两份**；其余 partial 的缺口全部是"尚未导出"。G-019 是规则裁决（D-01），G-003b 的一半是不可表示。

---

## 5. 33 none + 3 v5-only（catalog 机器提取，逐 ID）

**none = 33**：G-004, G-005, G-006, G-007, G-009, G-010, G-011, G-012, G-024, G-028b, G-030, G-040a, G-040b, G-040c, G-044, G-045, G-047, G-049*, G-050a, G-050b, G-050c, G-051, G-052, G-053, G-054, G-056a, G-056b, G-056c, G-056d, G-057a, G-057b, G-057c, G-057d。
\*G-049 按五份口径为 none，但 ai-v1 已存在且被全消费（§3.6）——建议 caller 决断是否重算为 partial/full。

**v5-only = 3**：G-055（outbox/RECEIPT_QUERY）、G-058（ownerEpoch/lease/Postgres）、G-059（Protobuf/WSS/TLS/Unity/IL2CPP）——设计上无 V4 fixture，不进 G1 分母。

**目录数字的正确读法**：73=G 需求行数；10,382=五份 fixture 具体 case 数（scoring 10,000 为子集不二次累加）；42=五个 C# 读者类 Fact 数；三者**互不为分子分母**。本次审计新增第 45 个 Fact 口径：42 Domain + 3 Ai = 45。

---

## 6. 专查一：G-028a 完整事件序列四层追踪

**结论：语义映射成立、C# 表示合法；真实缺口是事件序列断言缺失，不是缺枚举。不强迫同名枚举 1:1。**

| 层 | 锚点 | 事实 |
|---|---|---|
| ① V4 domain/round | `RoundTransitionPlanner.kt:408/:428/:508/:582/:600` | 和牌结算发射 `GameEvent.ScoreSettled(turnNumber, payments, newScores)`，夹在和牌事件与 RoundEnded 之间；exporter `V5RoundGoldenExportTest.kt:178` 记录类名 → 导出 `[TsumoWin, ScoreSettled, RoundEnded]` |
| ② V4 match | `MatchSession.kt:497-532` | match 层消费 round 结果，另发 MatchEnded；ScoreSettled 是 round 层事件，match 层无对应物 |
| ③ 协议（C# 侧冻结契约） | `ProtocolSnapshot.cs:51-53` | `ProtocolEventType.ScoreSettled=17 / RoundEnded=18 / MatchEnded=19` 已冻结（schema 冻结，含 reserved 语义） |
| ④ C# Domain/投影 | `RoundTransition.cs:56-74`（RoundEventType 16 值，**无 ScoreSettled**）；`TableEventProjection.cs:253/:735`（消费侧 settlement 类型识别）、`TableProjectionContractMapper.cs:521-532`（投影侧 payload builder）；**TableRuntime src 无 ScoreSettled 生产者** | C# Domain 把 V4 ScoreSettled 携带的信息（payments/newScores）放进 `RoundSettlement`/`RoundResult` 字段（resultFinalScores/resultDeltas 已被 C# 断言），不以独立事件表示——**信息不丢，表示不同** |

**判定**：
1. V4 `[TsumoWin, ScoreSettled, RoundEnded]` ↔ C# Domain `[TsumoWin, RoundEnded]` + 结算字段随 Result —— **合法语义映射**，强迫 Domain 出现同名枚举是错误要求（任务书明令禁止）。
2. 协议层 ScoreSettled=17 的生产者是 **P3 TableRuntime 集成项**，不属于 Domain 黄金测试补齐范围；现有引用全部是消费/投影侧，不构成"冻结契约被违反"。
3. **真实缺口（P1.3，文件 `RoundReplayGoldenTests.cs`）**：
   - `RoundTerminalReplaysExactly:134` 只有 `Contains(TsumoWin)`——事件序列多事件/少事件/错序/假事件全部 PASS（数值面有 version/scores/deltas/rejected 护栏，**事件面零护栏**）。
   - `ChankanRonReplaysExactly` 对事件只有两个 `DoesNotContain`——一个**不发任何事件**的错误 C# 也能 PASS。
4. **侥幸通过 = 成立**（这是 20 partial 里唯一"现有断言能用错值侥幸通过"的实锤点，其余 partial 的已导出未断言属于"没测"，G-028a 属于"测了但防不住错事件序列"）。

---

## 7. 专查二：mv-swap-drawn-with-wall-backfill（含新发现）

**结论：非 V4-only，非迁移缺口，无需用户规则裁决；C# 测试内注释与事实不符，应更正；剩余为小型交叉绑定补充。**

五层证据链：

1. **V4 法律**：`TileMovement.kt:543-548` — 摸牌实体参与置换时旧墙槽回填（"该槽位不能继续以 WALL 身份出现，否则会与新槽位重复计数"）。
2. **V4 生产可达性**：
   - 袖里藏花第二选：`ApplicationCoordinator.kt:1432-1437` — `swapHandWithWall(state, seat, handTile = drawnTile, …)` — **生产路径以摸牌实体置换**，回填分支生产可达；
   - 偷梁换柱：`ApplicationCoordinator.kt:2350` — `if (handTile == drawnTile …) return Rejected` — **生产路径拒绝摸牌实体**。
3. **V4 导出**：`V5CheatGoldenExporterTest.kt:1091-1124` — m1b 用真实 `drawNormal`+`swapHandWithWall(drawn,1)` 导出原语级事实。
4. **C# 实现（行为存在）**：`CheatSubmission.cs:376` — `ChooseSleeveBloom(keep=Second, Normal)` 同样以 `drawnTile` 调 `map.SwapHandWithWall`；`TilePositionMap.cs:1145-1160` 原语原生支持 `replacesDrawn`（drawn 证据被墙牌替换，与 V4 `TileMovement.kt:550-551` 一致）。
5. **C# 拒绝门（与 V4 生产对齐）**：`CheatSubmission.cs:1003-1006` — ChooseBeamSwap 拒绝 drawn 实体，**恰好对齐 V4 偷梁换柱生产路径** :2350。

**新发现（本审计核心增量）**：`CheatGoldenTests.cs:638-642` 注释称 "C# P1.4 域层入口（ChooseBeamSwap）按设计拒绝摸牌实体，**该法律在 C# 侧无入口**，仅断言黄金内部结构一致" — **前半句对（且与 V4 生产对齐），后半句错**：该法律的 C# 入口是 `ChooseSleeveBloom:376`，且 `SleeveBloomSecondChoice_BindsToGoldenInputs:748-757` **已经行为重放了摸牌实体置换**（摸牌→候选槽、候选进手、指针不动、136 守恒、下一手摸回换出牌）。

**剩余真实缺口（收窄后）**：
- **N3-B1**：m1b 黄金字段（backfilledIndex/resolvedIndex/pointer 三元）未与 C# 重放**交叉绑定**——现有重放走 sb-choose-second 关系，未对回 m1b 导出值。
- **N3-B2**：:638-642 注释需更正（"V4-only path" 定级不成立）。注意：本审计不改 C# 代码，此项列入补齐包。
- **N3-B3（不可表示，记录即可）**：被置换出的墙牌**落点地址**表示差——V4 落在摸牌旧墙槽（指针下方死区 `wall[0]`，factsAfter container=wall）；C# 落在摸牌让出的手牌槽（container=hand）。两者 136 守恒等价、净归属等价（玩家同样以墙牌为新摸牌）、死区地址无任何规则消费。**不可强迫地址级 1:1**；建议以映射注释记录，不作断言。

**与任务书红线的关系**：不宣布永久排除、不改规则、不刷新冻结记录；P1.4 "等额物理移动"语义两边均满足。

---

## 8. 专查三：AgariYame / ChooseDealerEnd

| 侧 | 锚点 | 事实 |
|---|---|---|
| V4 | `MatchSession.kt:82,497-532`；`GameAction.DeclareAgariYame`（ApplicationCoordinator :3268） | `ApprovedSessionStep.ChooseDealerEnd(agariYame=true)` → `MatchFinishReason.AGARI_YAME` → SessionResult + MatchEnded |
| C# | `MatchSession.cs:498` `ChooseDealerEnd(bool agariYame, …)`；:212 `CheckAgariYameEligible`；:536 `MatchFinishReason.AgariYame` | 行为完整存在 |

matchProgression 16 case 全字段消费含 `allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame`（RG-003m..o 全走）。**判定**：G-027 行为 full 成立；若 C# round 层缺 `DeclareAgariYame` 命令入口枚举，那只是**命令入口面**的 gap（V4 的 round 层入口在 ApplicationCoordinator，C# 的对应入口在 match 层），**不得据此断言"缺全部 agari-yame 行为"**。

---

## 9. 最小补齐包（可独立下发）与依赖 DAG

### 补齐项（全部为测试/文档/索引文件，不触生产代码、不改 fixtures、不改 catalog 冻结数字以外的语义）

| ID | 文件 | 内容 | 依赖 | 对应缺口 |
|---|---|---|---|---|
| N3-A1 | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs` | RoundTerminal：`Assert.Equal([TsumoWin, RoundEnded], eventTypes 投影)` 精确序列 + 绑定 expected.phaseAfter/resultOutcome（替换硬编码）+ input.phaseBefore/versionBefore + settlement.outcome/totalDeltas | 无 | G-028a / §3.3 |
| N3-A2 | 同上 | ChankanRon：`Assert.Equal([RonWin, RoundEnded], eventTypes)` + 绑定 meldTypeAfterKan/legalRonSeat1/input.meldTiles/phaseAfterKan/Ron/resultOutcome + hasKanDeclared/hasRinshanDraw 语义映射注释（不强迫同值） | 无 | G-028a |
| N3-B1 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | m1b 黄金字段与 `SleeveBloomSecondChoice` 重放交叉绑定（backfilledIndex/resolvedIndex/pointer 不变量） | 无 | G-038 邻域 / mv-swap |
| N3-B2 | 同上 | 更正 :638-642 注释为准确映射（ChooseSleeveBloom:376 为入口；ChooseBeamSwap 拒绝=V4 生产对齐；地址落点差异记录为表示差） | 无 | §7 |
| N3-C1 | `tests/v5-golden/catalog-v1.json` + `V5-0-GOLDEN-MANIFEST.md` | **caller 口径决定**：是否把 ai-v1 纳入 fixtures[]/denominators 并重算 G-049（建议以"仓库现存 fixture"口径补一列，保留五份冻结口径为历史注记） | caller 裁决 | §3.6 / G-049 |
| N3-D1 | （P3 范围，非本包） | TableRuntime ScoreSettled 协议事件生产者 | P3.x | §6 判定 2 |

### 依赖 DAG

```
N3-A1 ─┐
N3-A2 ─┼─→ G-028a 关闭路径（P1.3, RoundReplayGoldenTests）──┐
N3-B1 ─┐                                                    │
N3-B2 ─┴─→ mv-swap 定性修正落地（P1.4, CheatGoldenTests）──┼─→ N-3 [MAJOR] 三子项证据齐备
                                                            │
N3-C1 ←─ caller 口径裁决（G-049 / catalog 索引）───────────┤
                                                            │
N3-D1（P3 TableRuntime，独立轨）───────────────────────────┴─→ G1 门的字段级证据基础
（G1 批准另需：未导出缺口单列清单口径、G5 完成、caller 汇总复核——均不在本包内）
```

所有 N3-A*/B* 互不依赖、可并行；N3-C1 是索引口径决定非代码；N3-D1 与 G1 无前置关系（协议生产者属 P3 集成轨）。

---

## 10. 侥幸通过（wrong-value-passes）判定汇总

| 位置 | 错值场景 | 现有断言是否设防 |
|---|---|---|
| RoundTerminalReplaysExactly :134 | 事件序列多/少/错序/假事件（如 [TsumoWin, KanDeclared, RoundEnded]） | **不设防**（仅 Contains TsumoWin） |
| ChankanRonReplaysExactly | 和牌后完全不发事件 | **不设防**（仅两个 DoesNotContain） |
| CheatGoldenTests 51 个 reason-only case | 真实 C# 拒绝行为与 V4 相悖（同 reason 不同副作用） | **不设防**（映射完备 ≠ 行为一致）；beamSwap 5 个 Allowed 主路径 case 风险最高 |
| physical/scoring/shuffle 全部已导出字段 | 数值错值 | 设防（全字段逐值断言 + 分布对账 + 冻结硬值双锚） |
| ledger/tileMarkState 9+3 子键 | 数值错值 | 设防 |

---

## 11. 可复算命令与证据（原生数值 exit + 完整 SHA256）

### 11.1 构建（Release）

```
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
→ EXIT=0（build-domain.log，0 警告 0 错误）
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal
→ EXIT=0（build-ai.log，0 警告 0 错误）
```

### 11.2 定向 Golden 运行（catalog `targetedCommand` 同款直跑器）

| 命令（共同前缀 `D:/Unity/dotnet/dotnet.exe <dll> -class <类> -noColor -noLogo`） | EXIT | 结果行 | 日志 SHA256 |
|---|---|---|---|
| `…Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.PhysicalGoldenTests` | **0** | Total: 9, Failed: 0, Skipped: 0 | `93B00464631947058E76E76E217CC31E2211CBFA8DA6B717CB0B9DF36A1ACF4B` |
| `…Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.ScoringGoldenTests` | **0** | Total: 7, Failed: 0, Skipped: 0 | `C1B7A66626F328DA1711D183D97562B1FDE63DDE1647D6AADA6EA67C6F44FCE5` |
| `…Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.RoundReplayGoldenTests` | **0** | Total: 6, Failed: 0, Skipped: 0 | `B83631131381FDE2266E8C20733EA67521B1A9AD03F086B27487F07E06694D8E` |
| `…Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests` | **0** | Total: 16, Failed: 0, Skipped: 0 | `4783C3761FF0F2360B626A1B03B317DC43ACBD7BABAFCD8F3CDFE95B7D721AF4` |
| `…Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.ShuffleGoldenTests` | **0** | Total: 4, Failed: 0, Skipped: 0 | `94238F9CD8919AC573F159BFC08A34E0FDD5F7CCB0D825C0FBECA636BF659F15` |
| `…Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiReplayGoldenTests` | **0** | Total: 3, Failed: 0, Skipped: 0 | `91CC7A2918DB3E49119196F28979FBD52D23B115B9980576539FA89D420829B9` |

合计 45/45 PASS，无 skip、无空测试、无断言弱化操作。原始日志在独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-field-audit-01edf75dbc3b/`。

### 11.3 机器枚举证据（TEMP，全部只读仓库 + 独占 TEMP 写入）

| 工件 | SHA256 |
|---|---|
| walk-fixtures.ps1 | `1F449B61454EE4F07DA699299B71AEA073581CE213EF771017982C5DFF41B4F7` |
| aggregate-leaves.ps1 | `E37049DD05E3248CB95779A672D8D8EDD3269AD5E6322909EC006EA77561ED91` |
| scoring-shape.ps1 | `3C9C111741FEED7CF1EBC14BBCAF86A92A32C33959A17C8E2274B43B8B3F44AB` |
| physical-v1-leaves.txt / -agg | `C921C12D2BFC487B578DAB95A4969204F016EB7E4B1BE1D699379860631659CD` / `D0664CEC8A2378096EE80A27C3B1D73632660D1C3437D1B68440099D5EF17E6E`（296 逻辑路径） |
| scoring-shape.txt（7 组齐性+路径并集） | `A9A2D122FC3422EE25A33A711426F9FD93CE2758AE4820BC829D417D3C3AAD35` |
| round-v1-leaves.txt / -agg | `7C3BC02D85194560455EDD74AE24F4A29DB9C88357929BBF70E125DCB9BD7351` / `0B9A9D5E4B4DA7AA44B9F62BCCFAF8DDF520074BBA65FCDF9DFDC07859368541`（200） |
| cheat-v1-leaves.txt / -agg | `87E4FA363FA5F9F2C398D614BC8DA9CFA9E3BEBBF30606E9B192E2A8749413CE` / `6F84A0972CBC7ACFBE097AE709661CE38E1F0A7FD8AF2E8900BBE0368758AEA9`（204） |
| shuffle-v1-leaves.txt / -agg | `3B5C916D3FB2BF7AF38D139EB2DBCB20A03D360032D278F0D7F3835317D6185B` / `BD39C67945CDB6D4DF536CD8738A1996B8AB05F687178AD18936122088237A18`（199） |
| ai-v1-leaves.txt / -agg | `F10407306769BDC56C1E592CDD533D792B4BF15C3BD4BC786983BC20605D8530` / `9FAFD588C66441643408429309F83621789EAE708FA6705FFAE0D5F8D2326088`（198） |

### 11.4 fixture SHA256（工作树实测，与 catalog 声明一致性）

| fixture | 实测 SHA256 | catalog 声明 |
|---|---|---|
| physical-v1.json | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | 一致 |
| scoring-v1.json | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | 一致 |
| round-v1.json | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | 一致 |
| cheat-v1.json | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | 一致 |
| shuffle-v1.json | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | 一致 |
| ai-v1.json | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | catalog 外（§3.6） |

本报告文件自身：`doc/v5-重构/N3-GOLDEN-FIELD-AUDIT-REPORT.md`（SHA256 以提交时 `git hash-object` 为准）。

---

## 12. 边界声明

1. 本报告**只读**了仓库与兄弟任务无关区域；未读取 N1a/N2 兄弟在途未提交内容，未假设其已修。
2. 未修改：生产代码、测试代码、fixtures、catalog、manifest、固定头已接受 G1 盘点报告、用户未提交文件（CLAUDE.md/PROMPTS.md/任务书/协议/Unity 环境）。
3. 全部写入限于：本报告一个文档 + 独占 TEMP 目录。
4. `V5-REVIEW-REWORK-PROTOCOL.md` 全程只读，未复制提交。
5. **审计报告组件完成 ≠ N-3 关闭 ≠ G1 批准**：N-3/G1 的关闭由 caller 依据本报告与补齐包（§9）独立复核后裁决；G1 门仍受 V5-0-1 任务书 :107-108 全口径约束（未导出样本单列、G5 未完成等）。
6. 本报告不带任何 push/PR/CI 触发/远端操作。

---

## 13. 协议格式缺陷清单（按 V5-REVIEW-REWORK-PROTOCOL 一次性全报，同类合并）

> 本审计冻结在"只写一个新报告"边界内，**不实施任何修复**；每项的"修复要求"指向 §9 补齐包对应 ID，由 caller 独立下发。严重级别口径：MAJOR = 现有断言防不住错误值/错误行为或阻断 G1 门口径核实；MINOR = 文档/索引失真，无行为风险。无 BLOCKER（未发现生产行为错误或守恒违例）。

### DEF-1 [MAJOR] round-v1 已导出字段的断言缺失与硬编码（含 G-028a 事件序列）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs:129-134`（phaseAfter/resultOutcome 硬编码、eventTypes 仅 Contains）、`:136-147`（rejectedActions/versionAfterRejections 已设防）、`:151-233`（ChankanRonReplaysExactly：eventTypes/meldTypeAfterKan/legalRonSeat1/phaseAfterKan/phaseAfterRon/resultOutcome/input.meldTiles 未绑定）。
- **原样复现**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class "Mahjong.Domain.Tests.RoundReplayGoldenTests" -noColor -noLogo
  → EXIT=0（Total: 6, Failed: 0）——在事件序列被篡改（如插入 KanDeclared 或删去 RoundEnded）的错误实现下同样 EXIT=0
  ```
- **实际 vs 期望**：实际 = roundTerminal 事件断言仅 `Contains(TsumoWin)`（:134），chankanRon 事件断言仅两个 `DoesNotContain`（:225-226）；期望 = 与导出 `eventTypes` 全序列精确对账（C# Domain 侧为 `[TsumoWin, RoundEnded]` / `[RonWin, RoundEnded]`，ScoreSettled 为语义映射非同名枚举，见 §6）+ §3.3 两表所列 15 个字段级条目逐值绑定。
- **证据**：fixture `tests/v5-golden/round-v1.json` SHA256 `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`；运行日志 `run-round.log` SHA256 `B83631131381FDE2266E8C20733EA67521B1A9AD03F086B27487F07E06694D8E`（EXIT=0）；字段级绑定全表本报告 §3.3。
- **修复要求**：§9 N3-A1 + N3-A2（P1.3，仅测试文件）。
- **验收断言**：(a) 对 roundTerminal/chankanRon 各断言 C# Domain 事件类型**精确序列**；(b) §3.3 两表"已导出未断言/断言弱化"行全部变为逐值绑定或带映射注释的显式声明；(c) 人为注入"多一事件/少一事件/错序"三类变异后测试 FAIL（变异验证仅在 caller 隔离环境做，不在本审计工作树）。

### DEF-2 [MAJOR] cheat-v1 51/75 case 无行为级黄金对照（含 beamSwap Allowed 主路径）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` — 行为重放仅覆盖 24/75 case（tileMark 6+1 半重放、sleeveBloom 6 goldenInputs+4+1 stale、beamSwap 4+1 stale+1 仅 outcome）；其余 51 case 仅经 `Handler_Sections_CoverEveryRejectionReason`（:339-365）做 reason 映射完备性。
- **原样复现**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  powershell -NoProfile -Command "…枚举 cheat-v1.json 各节 caseId，与 CheatGoldenTests.cs 中字符串字面量求差…"
  （本报告 §3.4 矩阵即该差集的机器核对结果；详细逐 case 清单见 §3.4 表）
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class "Mahjong.Domain.Tests.CheatGoldenTests" -noColor -noLogo
  → EXIT=0（Total: 16, Failed: 0）
  ```
- **实际 vs 期望**：实际 = 51 case 的 `outcome/reason` 被映射表消费但真实 C# 行为无对照——若 C# 对这些 case 产生与 V4 相悖的副作用/事件形状，现有断言不设防；期望 = 至少 beamSwap 5 个 Allowed 主路径 case（bm-activate-allowed、bm-choose-swap、bm-activate-deadwall-allowed、bm-choose-deadwall-allowed、bm-choose-replacement-allowed）与各节拒绝类代表 case 行为级重放，其余以显式"reason-only"声明入盘点（不要求 75/75 全重放，按已导出可比较范围定义分母）。
- **证据**：fixture `tests/v5-golden/cheat-v1.json` SHA256 `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；运行日志 `run-cheat.log` SHA256 `4783C3761FF0F2360B626A1B03B317DC43ACBD7BABAFCD8F3CDFE95B7D721AF4`（EXIT=0）；消费矩阵 §3.4。
- **修复要求**：§9 N3-B1 的扩展（P1.4，仅测试文件）；分母口径按 §4 表 G-034/035/038 行。
- **验收断言**：(a) beamSwap 5 个 Allowed case 有 goldenInputs/关系绑定级行为重放；(b) 未重放 case 在测试文件或盘点中以显式声明标注（禁止沉默）；(c) 现有 16 Facts 全 PASS 保持。

### DEF-3 [MINOR] CheatGoldenTests.cs:638-642 差距声明与源码事实不符（mv-swap 定级）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs:638-642`（`TileMovement_DrawnBackfill_IsAV4OnlyPath_RecordedAsStructure` 的 doc 注释）。
- **原样复现**：只读比对三处源码即可——注释声称"该法律在 C# 侧无入口"，而 `dotnet/src/Mahjong.Domain/Cheat/CheatSubmission.cs:376`（ChooseSleeveBloom 以 drawnTile 调 SwapHandWithWall）、`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs:748-757`（该路径已被行为重放）。
- **实际 vs 期望**：实际 = 注释把 m1b 定级为"V4-only、C# 无入口"；期望 = 按 §7 五层证据链更正（ChooseBeamSwap 拒绝 drawn 与 V4 偷梁换柱生产 `ApplicationCoordinator.kt:2350` 对齐；drawn 置换法律的入口是 ChooseSleeveBloom，与 V4 袖里藏花生产 `ApplicationCoordinator.kt:1432-1437` 对齐；地址落点差为不可表示的表示差）。
- **证据**：上述源码路径与行号（冻结头 `8254c9b` 实拍）；本报告 §7。
- **修复要求**：§9 N3-B2（仅注释，不改断言语义）。
- **验收断言**：注释内容与 §7 五层证据链一致；`TileMovement_DrawnBackfill_IsAV4OnlyPath_RecordedAsStructure` 的结构断言保持原样（不因更名/改注释弱化）。

### DEF-4 [MINOR] catalog G-049 coverage=none 与仓库现状不符（ai-v1 索引过时）

- **准确位置**：`tests/v5-golden/catalog-v1.json` `requirements[G-049]`（coverage=none，note "No AiDecisionRecorded/intent or stale-AI submission fixture"，csharpEvidence 空）；catalog `fixtures[]` 无 ai-v1 条目。
- **原样复现**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  ls tests/v5-golden/ai-v1.json   # 存在，schema=v5-ai-golden-v1，7 case 含 5 个 stale-AI submission
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class "Mahjong.Ai.Tests.AiReplayGoldenTests" -noColor -noLogo
  → EXIT=0（Total: 3, Failed: 0）
  ```
- **实际 vs 期望**：实际 = G-049 记为"无 fixture"；期望 = catalog 的五份口径（notice "five accepted V4-exported fixtures"）自洽，但按"仓库现存"口径 G-049 至少 partial——**是否扩口径由 caller 裁决（§9 N3-C1），本审计不自行改 catalog**。
- **证据**：`tests/v5-golden/catalog-v1.json` SHA256 `16F7FD7F980398388E4E5DA45A3D5FB755C19990915F403987094617E90E891B`；`tests/v5-golden/ai-v1.json` SHA256 `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；运行日志 `run-ai.log` SHA256 `91CC7A2918DB3E49119196F28979FBD52D23B115B9980576539FA89D420829B9`（EXIT=0）；§3.6。
- **修复要求**：§9 N3-C1（caller 口径裁决；若采纳"仓库现存"口径则同步 catalog fixtures[]/denominators/manifest 行并重算 G-049）。
- **验收断言**：catalog 与 manifest 对 ai-v1 的记载二者一致，且与"仓库现存 fixture 清单"这一机器可核对事实一致。

### 语义映射 INFO 记录（非缺陷，禁止后续轮次误报为缺陷）

- **INFO-1**：`chankanRon.expected.hasKanDeclared/hasRinshanDraw`（V4 状态旗标"发生过"）↔ C# `DoesNotContain(KanDeclared/RinshanDrawn)`（抢杠和原子回滚后事件表不保留）——两者可同时为真，**不可强迫同值对照**；补齐时以映射注释记录（N3-A2 附属）。
- **INFO-2**：mv-swap-drawn-with-wall-backfill 被置换墙牌落点地址（V4：指针下方死区旧墙槽 ↔ C#：摸牌让出的手牌槽）——净归属与 136 守恒等价、地址无规则消费，**不可机械 1:1**；以映射注释记录（N3-B2 附属）。
- **INFO-3**：役种 41/42 口径（D-01）——DEEPSEEK 计划 `:588` 已预登记"冻结黄金前必须确认"，属规则裁决轨，非字段缺口；G-019 的 partial 定性维持，**不凭本审计增删门**。
