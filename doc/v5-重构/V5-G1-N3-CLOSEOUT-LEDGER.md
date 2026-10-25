# V5-G1-N3 销账台账（N3 closeout ledger：592 个 GAP 实例的 bound / classified / pending 三选一）

日期：2026-10-25
工作树：`D:/majiang-wt/n3-closeout-ledger`（独占），分支 `wt/v5-n3-closeout-ledger`，冻结基线 `cae94de6c6f18a56134c6b0beeaec0e61ad4bc8e`（PR19 当前已发布头）。
交付物（仅三个文件）：本报告、`V5-G1-N3-CLOSEOUT-LEDGER.tsv`（592 行实例级台账）、`tools/verify-v5-n3-closeout-ledger.ps1`（新只读台账校验器）。

**本任务是台账与分类，不是门禁裁决：本报告不宣告 G1、不宣告完整移植、不宣告 G5，也不重定义任务书的门 2。V4 仍是唯一生产架构。** N-3 收口规则（冻结 census 唯一分母、按包批量、三选一销账、硬收口点 r6、G1 重审九门）以 `V5-0-1-IMPLEMENTATION-TASKS.md` 为准，本台账只提供其输入。

---

## 1. 冻结输入与 pins

| 输入 | SHA-256 / 值 | 说明 |
|---|---|---|
| census `instances.tsv` | `049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12` | 655,145 行节点实例（6 列：fixture/path/caseId/idx/kind/parent），已接受 R7 证据 |
| census `rules.tsv` | `24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31` | 542 条映射规则（含 BOM 表头），verify-mapping v5 基线同源 |
| 归一化路径全集 | 1516 条 | 六 fixture `-full-paths.txt` 逐条计数（本机复核） |
| 节点实例全集 | 655,145 | 本轮独立重推导逐行复算一致 |
| GAP 缺口分母 | 592 units | CASE-DIST 全族逐实例真值（R5-1 建立、v5 收紧） |
| UNA（null 结构占位） | 17 units | 不进缺口分母，`UNA-NOT-NULL` 共享不变量（R6-2） |
| fixture 限定混合路径 | 27 条 | 全部在 cheat-v1（R6-1 修正后的 fx 键口径） |
| verify-mapping v5 | `E:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7/verify-mapping.ps1` | 656 行，本台账提取器逐语义忠实移植（Test-Match 有序首 match、Parse-Scope 文法、Test-WhereStr 父串读取、unscoped 全认领 / scoped 逐实例恰一行认领、first-claiming-row-wins） |

## 2. 分母：286 → 592 的确定映射（不改任务书、不改旧报告）

任务书旧数字 **286** 是 v3 时代的**混合分母**：scoped 家族按 (case, path) 实例展开、unscoped 家族却按"1 条归一化路径 = 1 单元"进账（48 个 winSamples status 实例只计 1、32 个 shuffle deadWallSegmentEqual 实例只计 1）。已接受的 R5-1 将其作废并建立实例真值分母；冻结审计报告原文（`V5-G1-N3-GOLDEN-FIELD-AUDIT.md`）：

> L1227：**诚实口径（R4-1 建立、R5-1 收紧）**：完整分母是**实例**而非路径——"101 条 GAP 路径"是 DIST 口径的定义投影，**不声明它枚举全部未绑定单元**；未绑定实例完整分母 = **592 units**（v3 的 286 为"scoped 逐实例 + unscoped 1 路径 = 1 单元"混合分母，R5-1 作废）。

> L3571（R5-1 行）：§11.4b 新增独立工具 **CensusInstances**……§11.6 verify-mapping **v4** 以该清单驱动**全族逐实例判级**……（`CASE-TOTAL units=655145 gap-units=592 mixed-paths=34`；E1 112 = status 48 + extraHan 48 + noWin 4×4；B2 32；B8 191 = 116+75；B11 11；B10 28 维持）【第 7 轮更正：34 的呈现口径含 7 条跨 fixture 假混合，fx 键后 27，§15.5 R6-1】

本台账保留两个历史口径的**原样出处**，并采用唯一分母 **592**（本机用移植 v5 语义的独立提取器逐行重推导，与 pins 全等，见 §7）。路径口径 101（DIST 首行投影）仍成立但只作投影参考，不作为本台账任何小节的小计。原 B4 round 子包 A1/A2 与 B4 同属 29 units（round-v1 同一 fixture 同批绑定交付），本台账不重复计数。

**14 包互斥闭合（本轮逐行复算 = 冻结 CASE-DIST 向量）：**

| 包 | fixture | 单元数 | bound | classified | pending |
|---|---|---:|---:|---:|---:|
| N3-B1 tileMovement | cheat-v1 | 24 | 18 | 0 | 6 |
| N3-B2 deadWallSegmentEqual | shuffle-v1 | 32 | 32 | 0 | 0 |
| N3-B3 goldenInputs 指针族 | cheat-v1 | 21 | 21 | 0 | 0 |
| N3-B4 tileMarkState + chankan/roundTerminal | cheat-v1 + round-v1 | 35 | 35 | 0 | 0 |
| N3-B5 ledger | cheat-v1 | 10 | 10 | 0 | 0 |
| N3-B6 mappingBasis/mappingLimits | ai-v1 | 11 | 0 | 0 | 11 |
| N3-B7 beam requestException | cheat-v1 | 1 | 1 | 0 | 0 |
| N3-B8 handler outcome/reason/note | cheat-v1 | 191 | 108 | 8 | 75 |
| N3-B9 AI events/action text | ai-v1 | 8 | 0 | 1 | 7 |
| N3-B10 goldenInputs 标量族 | cheat-v1 | 28 | 28 | 0 | 0 |
| N3-B11 factsAfter index/seat | cheat-v1 | 11 | 11 | 0 | 0 |
| N3-E1 scoring status/extraHan/noWin 输入 | scoring-v1 | 112 | 48 | 64 | 0 |
| N3-E2 physical boundaries/invalid | physical-v1 | 98 | 98 | 0 | 0 |
| N3-E3 physical suitOrder/hashCrosswalk/scope | physical-v1 | 10 | 10 | 0 | 0 |
| **合计** | | **592** | **420** | **73** | **99** |

分母纪律：六 census fixture 之外**不进台账**。已集成的增量 fixture（movement-v1、shuffle-reversed-v1、visibility-v1、typed shuffle）只做 §6 增量登记，不重算历史分母、不刷新黄金哈希/分子、不把未导出当失败。`histClass` 列保留每实例在 rules.tsv 的历史类（UNBOUND 350 + UNBOUND_HC 242 = 592），node/leaf/case/path 四层不混用同一分母：node 全集 655,145、路径投影 101、UNA 17、缺口 592 各自独立成列。

## 3. 判级标准（硬规则，逐实例执行）

- **bound**：存在当前头可解析的 C# 读点（文件:行，逐行核验存在且在文件行界内），断言对 fixture 导出值做**值级**比对（数值/字符串/布尔/枚举/null-kind），且该包至少有一条**已接受的变异反证**（改 fixture 字段必须导致测试 FAIL、native exit 非 0）。历史证据可复用，但必须核对当前头源码仍然匹配并如实标注历史实测时点，不冒充本轮实跑。
- **classified**：有**已接受的**不绑定理由——不可表示（无 C# 载体）、依赖阻塞（已声明依赖 + 探针纪律）、导出边界（exporter 恒定值且 API 无消费参数）、元数据且 caller 已受理裁决路径。必须给全：理由 + 责任 owner + 复访条件。**classified 一律不计 full**，仍留在未覆盖分类清单（§5）。
- **pending**：语义可比较但既未绑定也未获得已接受排除的一切情形。**存在级检查（如 note 仅 `IsNullOrWhiteSpace`）不等于值级绑定**（任务书明示），目录 validator、测试数通过、note 存在、硬编码都不替代绑定。必须给全：责任 owner + 期限 + 门槛。该包若无任何已接受变异反证，一律不得标 bound（B6 即此情形）。

每包"改字段必须失败"的已验收变异反证映射（§4 逐包表列出处）：B1/B3/B4(cheat)/B5/B7/B8/B10/B11 → cheat 绑定探针 46/46 FAIL_AS_EXPECTED + table admission 28 矩阵（5 正 + 23 负）；B4(round) → round 60 负例 + 6 保持；B2 → shuffle 33 场景 76 预期失败；E1 → scoring 8 注入矩阵 + caller 12 注入各 1 FAIL；E2/E3 → physical 38 探针（1 正 + 37 负）；B9 → AI 48/48 变异矩阵。**B6 无任何变异反证 → 全包 pending，无伪造。**

## 4. 逐包判定（bound 420）

台账 TSV 每行 17 列：id / package / fixture / caseId / path / idx / kind / parent / histClass / verdict / csRead / assertion / fixCommit / evidence / reason / owner / revisit。`id = <包>#<fixture>#<caseId>#<path>#<idx>`，唯一且可重构。以下按包给出判定要点；**锚点行号均为本轮在冻结头逐条 sed/grep 核验过的当前读点**，测试通过为本轮实跑（§7），变异反证为历史已接受轮的实测（如实标注）。

| 包 | 判定 | 关键 C# 锚点（当前头核验） | 值级断言要点 | 绑定交付（commit，均在本头祖先） | 变异反证（历史已接受） |
|---|---|---|---|---|---|
| B1（18 bound / 6 pending） | 非 note 18 条 bound；note 6 条 pending | `CheatGoldenTests.cs:1026,1029-1031`（wallPointerBeforeDelivery、deliveredTileId==reservedTileId+落位）；`CheatGoldenBindingTests.cs:557`（inputLastDiscardSeat）、`:2472`（AssertFacts factsBefore 逐 container/index/seat/tileId）、`:2585,2587-2598`（rinshanPointerAfterDraw、replacementTileId）、`:2641,2656`（两 refusal exception 字面量） | 指针/座位/牌 id 值相等；异常类型字面量相等 | a24b491（合并 db684dc），V5-N3-CHEAT-GOLDEN-BINDING.md | cheat 探针 46/46 FAIL_AS_EXPECTED（已接受验收轮） |
| B2（32 bound） | 全 bound | `ShuffleGoldenBindingTests.cs:415`：`Assert.Equal(fixture.DeadWallSegmentEqual, segmentEqual)` | 布尔由真实洗牌段重推导后逐实例相等 | 1c43a94，V5-N3-SHUFFLE-GOLDEN-BINDING.md | 33 场景 / 76 预期失败断言 |
| B3（21 bound） | 全 bound | `CheatGoldenBindingTests.cs:2141,2156,2195,2234,2275`（wallPointer）、`:2143,2158,2197,2236,2277`（rinshanPointer）、`:2144,2159,2198,2237,2278`（wallEndExclusive）、`:2217,2270,2394`（offerId）、`:2367`（windowId）、`:2370`（availableWallCount） | GoldenInt/GoldenString 对活体 map/offer 值相等 | a24b491 | cheat 探针 46/46 |
| B4（35 bound = cheat 6 + round 29） | 全 bound | cheat：`CheatGoldenBindingTests.cs:1247,1283,1294,1295,1300,1328`；round：`RoundReplayGoldenTests.cs:208,211,229-230,233,243,339,368,416,435-436,454,505,520,521,532,552,833` | tileMarkState 异常字面量/容量布尔/版本值；chankan meldType/eligibility/双 phase/双布尔/resultOutcome/eventTypes 全序列（AssertTerminalLifecycle）、roundTerminal settlement outcome/totalDeltas 全数组、input 消费后回读 | a24b491 + 6c8597b（V5-N3-ROUND-GOLDEN-BINDING.md） | cheat 46/46；round 60 负例 + 6 保持 |
| B5（10 bound） | 全 bound | `CheatGoldenBindingTests.cs:1122-1123,1136-1137,1145-1146,1152-1153`（四拒绝组 outcome+exception）、`:1197-1198`（balancesEqual/entriesEqual 由真实 FromSnapshot 重推导） | 值相等 + 类型化错误枚举断言 | a24b491 | cheat 46/46（含 ledger 注入） |
| B7（1 bound） | bound | `CheatGoldenBindingTests.cs:2874` | requestException 字面量相等 | a24b491 | cheat 46/46 |
| B8（108 bound / 8 classified / 75 pending） | outcome/reason 中 55 案 108 条 bound；4 案 8 条 classified（§5）；note 75 条 pending | bound：`CheatGoldenBindingTests.cs:1652`（outcome 值级）、`:1654-1660`（reason null-kind/值级）；其中 actor 层再绑定的 tm-allowed / tm-wrong-phase-round-end / tm-no-active-decision / tm-deadline-expired 加锚 `N3TableCheatGoldenAdmissionTests.cs:63`（`denominator=5\|bound=4\|blocked=1`） | 每案 outcome 必等、reason 空类型或值必等（对 fixture） | a24b491 + 92b2ec8（N1b actor）+ 7d0bb5b/8d2709e（table admission） | cheat 46/46；table 28 矩阵（5 正 + 23 负）+ readiness 4/5 exit 3 |
| B10（28 bound） | 全 bound | `CheatGoldenBindingTests.cs` cost `:2148,2203,2242,2283,2405`、drawKind `:2125,2151,2230,2273`、drawnTileId `:2126,2152,2228,2272`、keep `:2213,2264`、nextAvailableWallIndex `:2239,2279`、nextDeadWallTileId `:2133,2154`、nextWallTileId `:2127-2128,2153`、offerCreatedTurnNumber `:2219,2271`、offerFirstTileId `:2229,2274`、offerSecondTileId `:2231,2276`、offerSecondWallIndex `:2232,2280`（含 null-kind）、seat `:2123,2149,2212,2262,2358,2391`、turnNumber `:2218,2269` | Golden* 对活体对象值相等 | a24b491 | cheat 46/46 |
| B11（11 bound） | 全 bound | `CheatGoldenBindingTests.cs:2485,2529,2559,2610,2672`（AssertFacts factsAfter） | index/seat 逐元素值相等（UNA 17 旁置不混算） | a24b491 | cheat 探针 P9（m5 hand/discard index 2 实例生效变异）+ 46/46 |
| E1（48 bound / 64 classified） | winSamples expected.status 48 bound；extraHan 48 + noWin 四输入 16 classified（§5） | `ScoringGoldenTests.cs:262`（status 强制读为 win）+ `:269`（Explain 返回 null 即失败）+ 同测试内 CompareYaku/totalHan/… 全值链；classified 读点 `:294-300` | 状态双向强制 + 实际和形/符/役/点全值链 | 3332d11，V5-N3-SCORING-GOLDEN-BINDING.md §9（caller 验收内嵌） | 8 注入矩阵 + caller 12 注入各 1 FAIL exit 1 |
| E2（98 bound） | 全 bound | `PhysicalGoldenTests.cs:356-375` + `:260-281`（AssertTileSemantics 双入口）、`:402-411`（csharpExceptionMapping 三字段对真实异常类型）、`:923-958`（invalid[*] 全字段，message `:947-950` 值级 `tileId must be 0..135, got N`、outcome `:928`）、helper `:930-943` | 11 语义字段 × 双入口值相等；异常映射对真实 thrown type | cabde78 + c4c02c5，V5-N3-PHYSICAL-GOLDEN-BINDING.md | 38 探针（1 正 + 37 负）FAIL_AS_EXPECTED |
| E3（10 bound） | 全 bound | `PhysicalGoldenTests.cs:317`（suitOrder 对 34 组实映射）、`:150-153`（v4Reason 三子串 + 源文件清单）、`:165-181`（csharpHashVerification 行为锚：同墙重建哈希必等）、`:110-117`（targetIds 集合相等） | 元数据串逐一可检验且被检验 | cabde78 + c4c02c5 | 38 探针 |

## 5. classified 73（不偷 full；未覆盖分类清单继续保留）

| 组 | 实例 | 不绑定理由（已接受） | 责任 / 复访 |
|---|---:|---|---|
| B8 4 案 outcome/reason | 8 | `tm-capacity-tiles`、`tm-decision-mismatch`、`bm-activate-deadwall-empty`、`bm-activate-deadwall-dora-only`：Domain manifest 显式 blocked（`:1677-1684` 钉死 7 案清单），其中 3 案已由 actor 层（92b2ec8 + 7d0bb5b）再绑定，余 4 案为"无活跃局/死墙前提在该层不可表达"的**依赖阻塞**：accepted manifest 纪律要求 blocked 行不得计入 behaviour-bound 并必须声明依赖 + 探针纪律（23 条负例锁死 REJECTED 语义）。值级比对存在但绑定对象是占位语义而非最终生产行为 | P1.4（依赖：actor 层可表达性）；复访 = actor 层能表达该前提之日重开，否则持续排除在 full 之外 |
| B9 events[*].text | 1 | 表示差：C# `RoundEvent` 无 text 载体，exporter 该字段为 V4 展示文本；`V5-N3-AI-REPLAY-BINDING.md` §3/§5.4 已声明不可表示，48/48 变异矩阵锁定其余字段 | P1.6；复访 = RoundEvent 增设 text 载体或 exporter 收缩字段（caller 已在 AI 验收签收该边界） |
| E1 winSamples input.extraHan | 48 | 导出边界：`WinningScoreEvaluator.Explain` 无 extraHan 参数，读点不消费；exporter 对 win 样本恒导 0。`V5-N3-SCORING-GOLDEN-BINDING.md` 交付边界（:223,244-246）已接受 | P1.2 + caller（API 扩展裁决）；复访 = Explain 增参或导出侧收缩 |
| E1 noWinSamples doraIndicators/uraDoraIndicators/honba/riichiSticks | 16 | 同上边界：noWin 路径 `Evaluate(hand,context,isDealer,extraHan)` 签名无此四参（`:294-300`），exporter 恒空数组/0 | P1.2 + caller；复访 = Evaluate 扩签名 |

## 6. pending 99（责任 + 期限 + 门槛，全部不藏）

| 组 | 实例 | 现状读点 | 为何不能 classified 成"不用做" | owner | 期限 / 门槛 |
|---|---:|---|---|---|---|
| B8 note（tileMark 20 + sleeve 32 + beam 23） | 75 | `CheatGoldenBindingTests.cs:1672`：`Assert.False(IsNullOrWhiteSpace(spec.Note))` | BindingManifest 对 75 案 outcome/reason 做值级比对，**note 仅存在级**，从不与 fixture note 内容比对（`:1652,1654-1660` 对照）。V4 note 叙述可语义比较，存在级不能替代值级（任务书硬规则）；该包有变异反证、note 维度无 | P1.4 | 2026-11-30；门槛 = 逐案 note 值级绑定（或 caller 明文规则"note 为 V4 叙述元数据"并留裁决记录） |
| B1 tileMovement note | 6 | `CheatGoldenTests.cs:369`：`Assert.False(note.Length==0)` | 同上，同包其余 18 条已 bound 证明绑定模式可行，note 是残余未绑维度 | P1.4 | 2026-11-30；同上 |
| B6 mappingBasis（7）+ mappingLimits（1+3） | 11 | 无 C# 读点（csRead='-'） | 审计 L3523 明文："tags/mappingLimits 元数据：绑定或显式排除由 caller 裁决（N3-E1(d)/N3-B6）"，裁决未落地；且 **B6 全包无任何已接受变异反证**，按硬规则不得标 bound，也不得未经裁决偷计 classified | caller 裁决 + P1.6 | 2026-11-30；门槛 = caller 二选一裁决（绑定 → 建读点 + 变异反证；排除 → 显式规则）后改账 |
| B9 v4Call.action.text | 7 | 无 C# 读点 | 与 B9 events text 不同：action.text **既无绑定也无已接受的不可表示声明**（DEF-5(b) 不满足）；不能借同类"表示差"名义搭车 classified | P1.6 + caller 裁决 | 2026-11-30；门槛 = 绑定（值级 + 变异反证）或显式不可表示规则，二取一 |

## 7. 本轮实测（PASS/FAIL/BLOCKED/NOT_RUN 逐一列证）

| # | 检查 | 命令（原样） | native exit | 计数 | 证据 |
|---|---|---|---:|---|---|
| 1 | census/rules pins + v5 语义逐行重推导 | `powershell -File artifacts/TEMP/extract-gap-instances.ps1 -Evidence <E> -OutDir artifacts/TEMP/extract` | 0 | units=655145 gap=592 una=17 mixed=27；逐包向量 24/32/21/35/10/11/1/191/8/28/11/112/98/10 全等 | 临时提取器（不入库），PIN-OK 输出 |
| 2 | 台账构建闭合断言 | `powershell -File artifacts/TEMP/build-ledger.ps1 -RepoRoot <repo> -OutTsv doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv` | 0 | rows=592 bound=420 classified=73 pending=99；id 唯一；verdict 向量逐包断言 | LEDGER-OK 输出 |
| 3 | 台账校验器基线（**本轮最终版**） | `powershell -File tools/verify-v5-n3-closeout-ledger.ps1` | 0 | 18 项 CHECK-OK（pins/1516/655145/592/17/27/542/唯一集合/集合等式/fixture 纪律/闭合/锚点解析+commit 在史/classified/pending/blocked 集）| `artifacts/TEMP/verify-final.log` SHA-256 `28b092514b22b61d81a00fec82fd063059e2f83e703fc21e4a175ae65f9ed267` |
| 4 | 负例矩阵 | `powershell -File artifacts/TEMP/run-negatives.ps1 -RepoRoot <repo>` | 0 | **14/14 变异全部拒绝（exit 1 且 FAIL-TOKEN 命中）**：漏行、重复ID、id-内容漂移、错fixture（movement-v1 侵入）、缺锚点、锚点行越界、锚点文件不存在、pending 伪装 bound、未知 commit、classified 删理由、pending 无期限、pending 带假 commit、包错挂、表头破坏 | `artifacts/TEMP/neg-run3.log` SHA-256 `c3b2f134ca585b5e84e57cb6941d818eb13626c0f9c760e278243652d50814fa` |
| 5 | 六 golden 绑定类当前头实跑 | `dotnet test tests/Mahjong.Domain.Tests --filter "FullyQualifiedName~CheatGoldenBindingTests|~CheatGoldenTests|~RoundReplayGoldenTests|~ScoringGoldenTests|~ShuffleGoldenBindingTests|~PhysicalGoldenTests"`（dotnet = D:/Unity/dotnet/dotnet.exe 8.0.426，Debug） | 0 | **通过 506 / 失败 0 / 跳过 0** | `artifacts/TEMP/dotnet-domain.log` SHA-256 `990eea1c8c58f165ac4380605c9b0b6fade6b5459c739bdf241819cdd0d92ade` |
| 6 | actor 层准入当前头实跑 | `dotnet test tests/Mahjong.TableRuntime.Tests --filter "FullyQualifiedName~N3TableCheatGoldenAdmissionTests"` | 0 | 通过 373 / 失败 0 / 跳过 0 | `artifacts/TEMP/dotnet-table.log` SHA-256 `96bed376fcd582385d419c383ae1d8c31234c59b3cecb016710eabe816a53940` |
| 7 | 锚点逐条核验 | sed/grep 逐条（§4 全部行号） | 0 | 每条在冻结头源码中逐字命中 | 会话记录 + verify-final.log 的 bound-anchor 检查 |

**FAIL：无。BLOCKED：无（本轮交付面内）。NOT_RUN（如实单列，不冒充通过）：**
- 历史变异探针（cheat 46、round 60+6、scoring 8+12、physical 38、shuffle 33/76、AI 48、table 28、readiness 4/5）本轮**未重跑**，按已接受验收轮证据引用（出处 §4 表；各轮 native exit 已记录于各自验收文档）。本轮以 §7#5/#6 的当前头实跑 + §7#7 的锚点逐条核验确认"当前源码匹配"。
- PG/WSS/Unity IL2CPP/Gradle/Android 设备/物理网络/远端 CI：NOT_RUN，与本任务无关，不claim。
- 完整 `dotnet test` 全量与 Gradle `build`：未跑（任务书禁止盲跑重型全量）。
- 校验器负例矩阵构造物（TEMP 副本）与提取器均不入库；`artifacts/TEMP` 不进 commit。

环境说明：C 盘历史性紧张——本轮 process-local `TEMP/TMP` 全部指向 `D:/majiang-wt/n3-closeout-ledger/artifacts/TEMP`，未向 C 写任何大产物；未改机器级环境/信任。控制台为 GBK 代码页，PowerShell 重定向日志存在 UTF-16/宽度折行现象，本报告引用的日志 SHA 均为实际文件字节（折行不影响 grep FAIL-TOKEN 哨兵行，校验器为此专设恒短哨兵行）。

过程披露（负例矩阵一轮抖动，如实记录）：21:59 一轮负例（`neg-run2.log`）为 13/14——`wrong-fixture` 子进程仍 exit 1（**校验器照常拒绝，无逃逸**），但其 FAIL-TOKEN 输出未被 harness 捕获；经 mtime 取证 + 进程清单定位为**另一并发会话实例清扫 `artifacts/TEMP/neg` 目录导致的 harness 临时目录竞争**，与校验器逻辑无关。清理残留进程后单线程重跑两轮（22:11 `neg-run3.log` = 本表引用证据、22:22 `neg-run.log` 复确认）均 14/14 稳定全绿。

## 8. 增量 fixture 登记（不进 catalog、不改 592 分母）

| fixture | 当前头交付 commit | 登记说明 |
|---|---|---|
| movement-v1（G-004..G-007） | fdd0287 `test(v5): export movement golden G-004..G-007 and bind Domain` | 物理移动增量绑定已随交付完成（Domain 绑定 + probe-movement-binding.ps1），属 E2/E3 同族增量，不入六 fixture census、不入本台账分母 |
| shuffle-reversed-v1 | 8734494 `test(v5-p01): add actual V4 reversed shuffle wall companion` | 逆序洗牌伴随 fixture；`ShuffleReversedGoldenTests.cs` 已绑定；不重算 B2 分母、不刷新黄金哈希 |
| visibility-v1（G-057） | 544f9c1 绑定 + e8a9e9a 复核返修 | `VisibilityGoldenTests.cs` 8 Facts + probe 67 runs；已接受独立验收（f9fd8e8 记录）；不入分母 |
| typed shuffle 随机接缝（D-R1-02） | 99a7a76 `N3: add ordered typed shuffle random input seam` | `ShuffleTypedRandomTraceTests.cs` 9 Facts；有序类型化随机源接缝，无 fixture 文件、无分母影响 |

以上四项均为**各自已验收交付的承接登记**：本轮只登记归属，不重复计数、不重算历史分子、不把未导出项当失败。V4 随机源/洗牌实现未因本台账改变。

## 9. 与 G-049 / N3-C1 / Δ29 的关系（不越权）

G-049 维持未裁决（catalog 口径 none、审计建议 partial、N3-C1 caller 裁决未执行）；Δ29 与 catalog 重算均属 N3-C1 caller 裁决项，**本台账不改 catalog、不自行重定义门 2、不替 caller 裁决**。审计 §15.5 与 E' 证据 SHA 原样保留，未触碰任何已接受报告。

## 10. 下一批非重叠 pending 组合（≥2 包且 ≥50 单元；不预派任务，仅给 caller 输入）

| 组合 | 包 | 单元 | fixture | 拟定 owner | 依赖 / 门槛 | 与其他组合重叠 |
|---|---|---:|---|---|---|---|
| **P-NOTE-A** | N3-B8(note) + N3-B1(note) | 75 + 6 = **81** | cheat-v1 | P1.4 | 先决 = caller 对"note 为 V4 叙述元数据"或"须逐案值级绑定"二选一明文裁决；若选绑定，须复用 cheat 绑定模式（值级断言 + 每案至少 1 变异反证），验收时重跑本台账校验器（592 向量按裁决改判） | 与 P-ALIGN-A 无文件重叠（不同包不同维度）；同包 B8 其余 116 条已闭合，不重开 |
| **P-ALIGN-A** | N3-B6 + N3-B9(action.text) | 11 + 7 = **18** | ai-v1 | caller 裁决 + P1.6 | 先决 = caller 裁决（B6：绑定 vs 排除；action.text：绑定 vs 不可表示规则）；该两包当前**无任何变异反证**，裁决为"绑定"时须先建读点再补变异矩阵（AI 48/48 模式可扩） | 与 P-NOTE-A 无重叠；B9 events text 1 条已 classified 不在组合内 |

两组合并行约束：文件集不相离（P-NOTE-A 只触 `CheatGoldenTests.cs`/`CheatGoldenBindingTests.cs` 的 note 维度，P-ALIGN-A 只触 ai 绑定测试与 ai fixture 读点），可并行委派；合计 99 units 全覆盖后，592 = 420 bound + 73 classified + 0 pending，届时 N3 台账进入"无 pending"状态，**是否构成 G1 重审输入由 caller 依九门单独裁决，本台账不宣告**。若 caller 走"零 pending 后单次当前头 G1 九门重审"路线，所需输入 = 本台账 + 校验器基线 + 负例矩阵记录 + 裁决后改判差异。

## 11. 红线复核（逐条自检）

- 仅改动三个 expectedFiles（本报告、TSV、tools/verify-v5-n3-closeout-ledger.ps1）；生产/测试/fixtures/catalog/manifest/已接受报告/统一 verifier/任务书/Unity 资源零改动（`git status` 干净提交前核对）。
- 未读兄弟未提交工作；未 push、未建 PR、未触发 CI；未转派；未改机器级环境。
- bound 全部有当前头可解析值级锚点 + 已接受变异反证映射；note 存在级一律 pending；无目录 validator/测试数/硬编码冒充绑定。
- classified 73 条全部有完整理由 + 责任 + 复访，且**不计 full**；未覆盖分类清单（§5）继续保留。
- pending 99 条全部有 owner + 期限 + 门槛，无隐藏。
- B6 因无变异反证全包 pending，无伪造；每包变异反证映射齐备（B6 除外，如实待补）。
- 未把环境阻塞标完成；PG/Unity/Gradle/设备全部 NOT_RUN 单列；**不宣告 G1 / G5 / 完整移植**；V4 唯一生产。
