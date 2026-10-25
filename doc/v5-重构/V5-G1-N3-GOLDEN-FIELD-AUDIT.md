# V5-G1 N-3 前置独立审计报告 — 黄金样本字段级映射与覆盖口径核查（第 3 轮返修版）

- **审计执行**：Claude Code（本 agent），独占 worktree `D:/majiang-wt/n3-golden-field-audit`，分支 `wt/v5-n3-golden-field-audit`
- **冻结基线**：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（PR19 最新已发布头；工作树 status clean，HEAD 即该提交）
- **审计对象**：V5-1-REVIEW §7.1 N-3 [MAJOR]（G-028a 事件序列 + mv-swap-drawn-with-wall-backfill + 20 项 partial 的断言边界），以及"已导出黄金样本对齐率为 100%"这一 G1 门口径的字段级核实
- **交付路径**：`doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md`（任务 expectedFiles 指定路径；第 1 轮曾误用 `N3-GOLDEN-FIELD-AUDIT-REPORT.md`，第 2 轮已 `git mv` 统一，无第二拷贝）
- **版本史**：第 1 轮 = commit `5ba62bd`（被 caller 判 REWORK_REQUIRED，R1-1..R1-5）；第 2 轮 = commit `48660db`（被 caller 判 REWORK_REQUIRED，R2-1..R2-3）；第 3 轮 = 本文件，R2-1..R2-3 逐条映射见 §14
- **报告日期**：2026-10-25
- **性质声明**：本报告只产出审计证据与最小补齐包建议；**不实施任何 P0.1/已接受项开发，不改 catalog/manifest/fixtures/生产或测试代码，不改固定头已接受 G1 盘点报告**。本报告组件完成 ≠ N-3 关闭 ≠ G1 批准。

---

## 0. 前置核实（审计开工前实拍）

| 项 | 值 | 验证命令 |
|---|---|---|
| HEAD（开工时） | `8254c9b783740ddf519cebc15bb50a8f9974cf58` | `git rev-parse HEAD` |
| 分支 | `wt/v5-n3-golden-field-audit`（独占） | `git branch --show-current` |
| 工作树 | clean（唯一在途变更 = 本报告文件） | `git status --short` |
| V4 唯一生产 | Kotlin `engine/`；V5 `dotnet/` 生产性 false（catalog baseline `v5Production:false`） | catalog-v1.json baseline |
| G1 | 不批准（固定 8ee22eb 报告 7 PASS/2 FAIL） | CODEX-V5-G1-FIXED-HEAD-REPORT-ACCEPTANCE.md |
| 只读文件 | `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`（未复制未提交未修改） | — |
| 禁触文件 | 主 checkout 未提交的 CLAUDE.md、PROMPTS.md、任务书/协议、Unity 环境/Resources/Burst | 未复制、未提交、未修改 |

---

## 1. 审计方法与可复算口径（第 3 轮：两层覆盖口径）

### 1.1 第 1 轮方法局限的正式记录（R1-3，沿用）

第 1 轮对 scoring-v1 使用 `scoring-shape.ps1` 做"形状提取"：**对 7 组各取首/中/末样本、嵌套数组最多展开 3 个元素**。该证据**不充分**：顶层键相同 ≠ 递归形状齐性。全量 census（§1.2）实测 scoring-v1 归一化路径 419 条，第 1 轮抽样漏 14 条真实路径（`$.winSamples[*].input.melds[*]` 及其 5 个子叶、`input.doraIndicators[*]`、`input.uraDoraIndicators[*]`、`expected.dora` 三个 `[*]` 叶、`$.doraSamples[*]` 的 2 条 `[*]` 叶、`$.randomSamples[*].ura[*]`）。第 1 轮"scoring 全字段"类结论已作废；其余第 1 轮证据（45/45 定向运行、5 类消费者逐行深读、V4 源锚点、v4Commit/exporter SHA 链、mv-swap 五层链、AgariYame 双侧锚点）不受影响并继续引用。

### 1.2 全量 census（第 2 轮建立，第 3 轮沿用同一 census 产物）

- **工具**：`FullCensus`（C# net8.0 / System.Text.Json，独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/`，源码全文内嵌 §11.4），对 6 份 fixture **递归枚举全部 JSON 节点，无任何抽样**（scoring-v1 全部 10238 case 逐一遍历）。
- **口径定义**（本报告全文统一）：归一化逻辑路径 = 数组下标折叠为 `[*]`；distinctPaths = 不同归一化逻辑路径数（容器与叶都计）；distinctLeafPaths = 其中非容器路径数；leafInstances = 叶实例累计；groups = 按"首个 `[*]` 及其前缀"机械分组数。**census 路径书写约定：`[*]` 紧贴段名**（如 `$.winSamples[*].input`），模式匹配时 `X.#` 与 `X[*]`、`X[*].y` 为**字面段比较、互不匹配**——这是第 2 轮规则表曾误配 17 条、第 3 轮已显式修复的规则文法要点（§3.0.1）。
- **总量**：

| fixture | distinctPaths | distinctLeafPaths | leafInstances | groups（[*]-组） |
|---|---|---|---|---|
| physical-v1 | 296 | 233 | 29750 | 46 |
| scoring-v1 | 419 | 308 | 476153 | 78 |
| round-v1 | 200 | 150 | 1163 | 113 |
| cheat-v1 | 204 | 164 | 1163 | 96 |
| shuffle-v1 | 199 | 140 | 23016 | 172 |
| ai-v1 | 198 | 152 | 1199 | 81 |

### 1.3 两层覆盖口径（R2-1a：本节为第 3 轮方法核心，替代第 2 轮"单层 COVERAGE 即覆盖"的表述）

**"报告覆盖了全部字段"必须同时由两层独立机器校验证明，缺一不可：**

- **层 1 = 路径枚举覆盖（COVERAGE 块）**：`verify-coverage.ps1`（源码全文内嵌 §11.5）把每条 census 归一化路径归入 prefix-2 组，6 份 fixture 各产出一个**完全划分**的 `组 = distinctPaths` 清单内嵌 §3.0。它证明的仅是"**枚举出的每条路径都进了报告、计数一致**"——**对语义零断言**：把映射整段删除、把某组路径漏报、或把计数抄错，verify 都会 exit 非 0；但**所有路径全部塞进一行 `META` 也能通过层 1**。
- **层 2 = 字段消费语义映射（FIELD-RULES 块）**：`verify-mapping.ps1`（源码全文内嵌 §11.6）对 §3.0.1 内嵌的 **417 条 first-match-wins 规则**逐条复核：(a) 规则集必须**无泄漏地划分全部 1516 条路径**（每条路径恰被一条规则命中，任何路径无规则命中 = LEAK）；(b) 每条规则的声明路径数必须与 census 重算一致（COUNT）；(c) 每条规则必须带**合法判级类**与**满足该类文法的 V4 exporter 锚点 + C# 消费者锚点**（ANCHOR-V4/ANCHOR-C#）+ owner。把映射删除 → LEAK；计数抄错 → COUNT；锚点缺失/复制粘贴占位 → ANCHOR；只交一份"计数对了但没有语义映射"的 COVERAGE 副本 → 层 2 直接 ANCHOR-C#/LEAK 失败。**本报告两层均 exit 0（§11.5/§11.6），且三种变异负例全部被检出（§11.6 负例矩阵）**。

### 1.4 字段判级八类与锚点文法（R2-1a）

**判级类**（FIELD-RULES 第 4 列，first-match-wins 划分的终态）：

| 类 | 含义 | csAnchor 文法 | exporterAnchor 文法 |
|---|---|---|---|
| IN_BIND | 输入绑定：导出输入值作为 C# 真实入口构造载荷 | `文件.cs:行` | `文件.kt(:行(-行)?)` |
| OUT_DIFF | 输出差分：C# 重放实算值与导出 expected 逐值对照 | `文件.cs:行` | `文件.kt(:行(-行)?)` |
| INTERNAL | fixture 内部结构/自检（无生产差分语义，显式声明保留） | `文件.cs:行` | `文件.kt(:行(-行)?)` |
| SEM_MAP | 语义映射：同语义名称/表示映射（可绑定或已绑定，绑定须配映射注释） | `文件.cs:行` 或 `GAP:<包ID>` | `文件.kt(:行(-行)?)` |
| UNBOUND | 已导出可比较字段当前无任何 C# 断言消费 | `GAP:<包ID>` | `文件.kt(:行(-行)?)` |
| UNBOUND_HC | 硬编码未绑定：C# 以硬编码常量替代导出值（断言存在但不读 fixture） | `GAP:<包ID>` | `文件.kt(:行(-行)?)` |
| META | exporter 元数据/provenance，无生产行为语义，显式排除 | `meta:<说明>` | `meta:<说明>` |
| CONTAINER | 纯容器节点（object/array 自身，无独立语义） | `meta:<说明>` | `meta:<说明>` |

- UNBOUND/UNBOUND_HC 的 csAnchor 必须指向补齐包（`GAP:N3-*`，§9），exporterAnchor 必须是**真实存在的 `.kt` 文件**（checker 只校验文法；人工抽查在 §3 各节完成）；**禁止空串或省略号**。
- 判级是**保守**的：语义可比但当前未绑定的（如 hasKanDeclared）一律记 UNBOUND_HC 并指向补齐包，**不以"可语义映射"抵扣缺口**（§13 INFO-1）。
- 本表与 §3.0.1 FIELD-RULES 块、§11.6 校验器三方一致；§3 各节正文引用的计数全部可由 FIELD-RULES 块按 fixture+类筛选复算（§11.6 复算命令）。

---

## 2. 总判定（先给结论，第 3 轮修正版）

1. **"已导出黄金样本对齐率为 100%" 不成立（字段级）**。两层口径实测：层 1 = 1516 条归一化路径全覆盖（§3.0，verify exit 0）；层 2 = 417 条语义映射规则全划分（§3.0.1，check exit 0）。**已导出但当前无 C# 断言消费的路径实数 = 87**（UNBOUND 47 + UNBOUND_HC 40，全部带 `GAP:N3-*` 补齐包指针），分布：
   - **round-v1：20 条路径（16 个字段级条目）UNBOUND_HC**（roundTerminal 9 + chankanRon 11，§3.3 两表），其中事件序列面零护栏（G-028a 核心）；
   - **cheat-v1：33 条路径** = tileMovement 13（GAP:N3-B1）+ goldenInputs 6（GAP:N3-B3）+ tileMarkState 6（GAP:N3-B4 的 cheat 部分）+ ledger 8（GAP:N3-B5）；**75 个 handler case 分桶 = A16+B2+C6+E1+D50**（D-α46/D-β4，§3.4 逐条表），行为级黄金对照 **24/75**（A16+B2+C6=23 + E1 存在级 1）；
   - **scoring-v1：6 条路径**（winSamples `expected.status`+`input.extraHan`、noWinSamples `doraIndicators/uraDoraIndicators/honba/riichiSticks`，GAP:N3-E1，§3.2）；
   - **physical-v1：24 条路径**（GAP:N3-E2：boundaries[*] 11 + boundarySamples 6 + invalid[*] 3 + csharpExceptionMapping 4，§3.1）；
   - **shuffle-v1：1 条路径**（`cases[*].deadWallSegmentEqual`，GAP:N3-B2，§3.5）；**ai-v1：3 条路径**（mappingBasis/mappingLimits，GAP:N3-B6，§3.6）。
   - **第 2 轮错误结论修正**：(a) "winSamples 95 条中 94 条被消费"的算术**作废**——95 条路径按类拆分 = IN_BIND 32 + OUT_DIFF 50 + SEM_MAP 1 + UNBOUND 2 + META 10（容器与元数据计入分母，§3.2 逐类表）；(b) "tileMarkState 3/3 子键行为差分"**作废**——24 条路径 = SEM_MAP 1 + UNBOUND_HC 6 + OUT_DIFF 17，6 个未绑字段（versionAfterFirstRegister/sameIdDifferentContentException/seatGroupLimitIsThree/seatGroupLimitException/seatTileLimitIsTwelve/createdRoundOrdinal）变异后 16/16 PASS（探针 P3）；(c) "ledger 前 8 子键行为差分"**作废**——53 条路径 = SEM_MAP 4 + IN_BIND 26 + UNBOUND_HC 8 + OUT_DIFF 15，4 个子键（conflictDifferentPayload/insufficientFunds/overflow/invalidSeat）的 outcome/exception 8 条叶变异后 16/16 PASS（探针 P4）。
2. **G-028a 定性修正（含 R1-1 更正，沿用）**：C# Domain 以 `RoundSettlement/RoundResult` 字段承载 ScoreSettled 信息是合法表示映射（§6 四层链）；真实缺口 = round-v1 两个重放测试**没有对事件序列做精确断言** + `hasKanDeclared/hasRinshanDraw` 等导出值未绑定。两布尔可语义比较（exporter 以终局事件列表 `contains` 计算，与 C# `DoesNotContain` 同语义，名称映射 RinshanDraw→RinshanDrawn）；第 1 轮"不可强迫同值"表述已全部撤回（§3.3、§9 N3-A2、§13 INFO-1）。变异探针双改 true 后 6/6 PASS = **覆盖缺口**，非不可比较。
3. **mv-swap-drawn-with-wall-backfill（沿用，caller 已认可方向）**：非 V4-only，C# 等价入口已被行为重放；缺口收窄为 m1b 交叉绑定（N3-B1）+ 注释更正（N3-B2）+ 一处不可表示的地址级表示差（INFO-2）。无需用户规则裁决、无需刷新冻结记录（§7）。
4. **AgariYame（沿用）**：行为在 C# match 层完整存在；疑虑仅保留"round 层命令入口枚举"一点（§8）。
5. **ai-v1 / G-049（沿用 + 第 3 轮 B6 补充）**：第 6 份 fixture 存在且 Replay/Submit/拒绝码/动作绑定为真实 C# 行为，但 `DeepEquals(before,after)` 是同一 fixture 的 V4 前后树内部比对，不是"C# 重放后态 vs V4 全树"差分；**G-049 不可据此重算 full，建议 partial**（§3.6，caller 裁决）。第 3 轮补充：`v5Observation.mappingBasis`/`mappingLimits` 3 条路径当前未绑定（GAP:N3-B6）。
6. **45/45 定向 Golden Facts 全 PASS（native exit 0）维持**——失败不在断言结果，而在断言覆盖。**9 个独立变异探针**（第 2 轮 5 + 第 3 轮 4，§10/§11.8）+ **2 个正控制**（已绑定 totalPoints 错值 → 1 FAIL exit 1；sb-activate-already-used 的 reason→WRONG_PHASE → 1 FAIL exit 1，证明 `CheatGoldenTests.cs:486-490/:557` 的 ReasonMap 绑定真实存在）证明判级方法有效。
7. **case 总数口径（沿用）**：catalog `fixtureConcreteCases=10382` vs 本审计 census 案例口径 10353，Δ=29（round +23、cheat +6）——catalog 未声明案例分解口径，Δ29 如实上报、不采纳、不改 catalog（§5）；ai-v1 的 7 case 在 catalog 之外。
8. **一次性缺陷清单见 §13（DEF-1..DEF-5 + INFO-1..3）**。本审计按任务书**不实施修复**；修复经由 §9 补齐包由 caller 独立下发。§9 覆盖本报告所报**全部可比较缺口**（87 条 GAP 路径逐一映射到包）；确属不可机械比较的项逐条给出语义与批准责任（无沉默移出分母）。

---
## 3. 六份 fixture 字段级映射总表（全量 census + 语义映射双层版）

### 3.0 层 1：全量路径枚举覆盖（机器可复核）

prefix-2 组完全划分（组 = 该前缀下 distinctPaths 数；6 块合计覆盖 1516 条归一化路径；校验器源码内嵌 §11.5，verify exit 0 实测同节）。任何字段级结论均可由块内组 → `*-full-paths.txt` 逐条展开。**本块只证明枚举覆盖，语义判级见 §3.0.1 层 2。**

```text
COVERAGE:BEGIN(physical-v1)
$ = 1
$.clockUsed = 1
$.exporterSourceSha256 = 1
$.extractionStatus = 1
$.generatedByCommand = 1
$.generationResult = 7
$.generator = 1
$.generatorVersion = 1
$.gitStatusPorcelain = 1
$.gitStatusPorcelain[*] = 1
$.hashCrosswalk = 6
$.randomInputsNote = 1
$.rulesetVersion = 1
$.samples = 1
$.samples[*] = 249
$.schema = 1
$.scope = 8
$.sourceHashAlgorithm = 1
$.taskPaths = 1
$.taskPaths[*] = 1
$.trackedModifiedFiles = 1
$.trackedProductionModifiedFiles = 1
$.v4Commit = 1
$.v4ProductionTreeClean = 1
$.v4RulesSourceNote = 1
$.v4SourceHashes = 1
$.v4SourceHashes[*] = 3
$.worktreeDirty = 1
COVERAGE:END
```

```text
COVERAGE:BEGIN(scoring-v1)
$ = 1
$.doraSamples = 1
$.doraSamples[*] = 35
$.exportedAtNote = 1
$.extractionStatus = 1
$.fuSamples = 1
$.fuSamples[*] = 39
$.generatedByCommand = 1
$.generationResult = 10
$.generator = 1
$.generatorVersion = 1
$.noWinSamples = 1
$.noWinSamples[*] = 42
$.randomMeta = 43
$.randomSamples = 1
$.randomSamples[*] = 51
$.rulesetVersion = 1
$.schema = 1
$.scoreSamples = 1
$.scoreSamples[*] = 58
$.settlementSamples = 1
$.settlementSamples[*] = 28
$.unexportedSamples = 1
$.v4Commit = 1
$.v4RulesSourceNote = 1
$.winSamples = 1
$.winSamples[*] = 95
COVERAGE:END
```

```text
COVERAGE:BEGIN(round-v1)
$ = 1
$.chankanRon = 39
$.controlEpoch = 19
$.decisionClock = 1
$.decisionClock[*] = 26
$.exportedAtNote = 1
$.extractionStatus = 1
$.generatedByCommand = 1
$.generationResult = 6
$.generator = 1
$.matchProgression = 1
$.matchProgression[*] = 54
$.reproducibilityNote = 1
$.roundTerminal = 40
$.rulesetVersion = 1
$.schema = 1
$.unexportedSamples = 1
$.unexportedSamples[*] = 1
$.v4Commit = 1
$.v4DotnetRulesCommit = 1
$.v4EngineSourceCommit = 1
$.workingTreeDirty = 1
COVERAGE:END
```

```text
COVERAGE:BEGIN(cheat-v1)
$ = 1
$.beamSwapHandler = 1
$.beamSwapHandler[*] = 6
$.exportedAtNote = 1
$.extractionStatus = 1
$.generatedByCommand = 1
$.generator = 1
$.generatorVersion = 1
$.ledger = 53
$.rulesetVersion = 1
$.schema = 1
$.sleeveBloomHandler = 1
$.sleeveBloomHandler[*] = 32
$.slotConventionNote = 1
$.tileMarkHandler = 1
$.tileMarkHandler[*] = 5
$.tileMarkState = 24
$.tileMovement = 1
$.tileMovement[*] = 69
$.v4Commit = 1
$.v4RulesSourceNote = 1
COVERAGE:END
```

```text
COVERAGE:BEGIN(shuffle-v1)
$ = 1
$.cases = 1
$.cases[*] = 22
$.configSnapshot = 96
$.demandVectors = 13
$.exportedAtNote = 1
$.extractionStatus = 1
$.generatedByCommand = 1
$.generationResult = 5
$.generator = 1
$.generatorVersion = 1
$.overlapMatrix = 43
$.rngGapNote = 1
$.rulesetVersion = 1
$.schema = 1
$.slotMatrix = 1
$.slotMatrix[*] = 7
$.v4Commit = 1
$.v4RulesSourceNote = 1
COVERAGE:END
```

```text
COVERAGE:BEGIN(ai-v1)
$ = 1
$.cases = 1
$.cases[*] = 110
$.decisionClock = 8
$.extractionStatus = 1
$.generatedAtHead = 1
$.generatedByCommand = 1
$.generator = 1
$.generatorVersion = 1
$.recordedIntent = 12
$.reproducibility = 6
$.rulesetVersion = 1
$.schema = 1
$.sourceFileSha256 = 1
$.sourceFileSha256[*] = 4
$.testOnlyChanges = 1
$.unexportedOrMappedSamples = 1
$.unexportedOrMappedSamples[*] = 1
$.v4BehaviorSource = 1
$.v4Commit = 1
$.v4DotnetAiSourceCommit = 1
$.v4DotnetDomainSourceCommit = 1
$.v4EngineSourceCommit = 1
$.v4ProductionSourceDirty = 1
$.v4ProductionSourceDirtyPaths = 1
$.v4TestOnlyTrackedDirtyPaths = 1
$.v4TestOnlyTrackedDirtyPaths[*] = 1
$.v4TrackedDirtyPaths = 1
$.v4TrackedDirtyPaths[*] = 1
$.v4TrackedWorkingTreeDirty = 1
$.v5Observation = 33
COVERAGE:END
```

### 3.0.1 层 2：字段消费语义映射规则表（FIELD-RULES，R2-1a 核心交付）

**目的**：把 §3.0 的"每条路径都进了报告"升级为"每条路径都被判入一个语义类，且每个非排除类都有 V4 导出锚点与 C# 消费（或显式缺口/排除）锚点"。第 2 轮只交了层 1，把 `$.roundTerminal.input.#` 这类 catch-all 规则下的未读叶全部隐没——第 3 轮把 catch-all 全部展开为显式规则（含 17 条 `[*]` 紧贴段名的文法修正与 7 条 catch-all 隐没叶的显式 UNBOUND_HC 规则），417 条规则对 1516 条路径构成**无泄漏、无重叠（first-match-wins 顺序无关，规则间互不覆盖路径）**的划分。

**规则文法**（TSV 7 列：`fixture` `pattern` `paths` `class` `exporterAnchor` `csAnchor` `owner`；`#` 匹配零或多层后缀、`*` 匹配单层下标、其余为字面段比较——**`X.#` 不匹配 `X[*]`/`X[*].y`**，见 §1.2 口径）：pattern 按表内顺序 first-match-wins 命中 census 路径；`paths` 列 = 该规则实际命中的 distinctPaths 数（校验器重算强制一致）；8 个判级类与锚点文法见 §1.4。

**块内嵌如下（成对标记行之间的原始 TSV；校验器 `-ReportPath` 模式直接从本报告读取该块复算，§11.6）**：

FIELD-RULES:BEGIN
```text
fixture	pattern	paths	class	exporterAnchor	csAnchor	owner
physical-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META
physical-v1	$.clockUsed	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:41	BOUND
physical-v1	$.exporterSourceSha256	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:49	BOUND
physical-v1	$.extractionStatus	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:40	BOUND
physical-v1	$.generatedByCommand	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.generatorVersion	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:38	BOUND
physical-v1	$.gitStatusPorcelain	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.gitStatusPorcelain[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.generationResult.#	7	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:64	BOUND
physical-v1	$.hashCrosswalk.#	6	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:94	BOUND
physical-v1	$.randomInputsNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.rulesetVersion	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:39	BOUND
physical-v1	$.schema	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:37	BOUND
physical-v1	$.sourceHashAlgorithm	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:51	BOUND
physical-v1	$.taskPaths	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.taskPaths[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.trackedModifiedFiles	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.trackedProductionModifiedFiles	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:47	BOUND
physical-v1	$.v4Commit	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:43	BOUND
physical-v1	$.v4ProductionTreeClean	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:46	BOUND
physical-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.v4SourceHashes[*].#	3	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:58	BOUND
physical-v1	$.v4SourceHashes.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:58	BOUND
physical-v1	$.worktreeDirty	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
physical-v1	$.scope.#	8	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:72	BOUND
physical-v1	$.samples.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:80	BOUND
physical-v1	$.samples[*].command	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:89	BOUND
physical-v1	$.samples[*].extractionStatus	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:86	BOUND
physical-v1	$.samples[*].manifestStatusBefore	1	META	meta:exporter-run-bookkeeping	meta:exporter-run-bookkeeping	META
physical-v1	$.samples[*].source.#	5	META	meta:exporter-run-bookkeeping	meta:exporter-run-bookkeeping	META
physical-v1	$.samples[*].input.clockMillis	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:87	BOUND
physical-v1	$.samples[*].input.sessionId	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:88	BOUND
physical-v1	$.samples[*].input.randomInputs	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:324	BOUND
physical-v1	$.samples[*].input.#	13	META	meta:exporter-run-bookkeeping	meta:exporter-run-bookkeeping	META
physical-v1	$.samples[*].expected.tiles[*].#	12	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:110	BOUND
physical-v1	$.samples[*].expected.groups[*].#	7	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:171	BOUND
physical-v1	$.samples[*].expected.boundarySamples[*].isHonor	1	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.boundarySamples[*].isNumberTile	1	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.boundarySamples[*].isRedFive	1	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.boundarySamples[*].isSimple	1	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.boundarySamples[*].isTerminal	1	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.boundarySamples[*].isTerminalOrHonor	1	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.boundarySamples[*].#	6	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:157	BOUND
physical-v1	$.samples[*].expected.boundaries[*].tileId	1	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:224	BOUND
physical-v1	$.samples[*].expected.boundaries[*].#	11	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.validRoundTrips[*].#	6	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:201	BOUND
physical-v1	$.samples[*].expected.invalid[*].input	1	IN_BIND	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:236	BOUND
physical-v1	$.samples[*].expected.invalid[*].exceptionType	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:233	BOUND
physical-v1	$.samples[*].expected.invalid[*].semanticCategory	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:234	BOUND
physical-v1	$.samples[*].expected.invalid[*].#	3	UNBOUND	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.csharpExceptionMapping.#	4	UNBOUND_HC	V5PhysicalGoldenExporterTest.kt	GAP:N3-E2	GAP:N3-E2
physical-v1	$.samples[*].expected.duplicateKeyStaticLimitation.#	11	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:511	BOUND
physical-v1	$.samples[*].expected.cases[*].#	31	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:243	BOUND
physical-v1	$.samples[*].expected.probeA.firstAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:409	BOUND
physical-v1	$.samples[*].expected.probeA.secondAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:420	BOUND
physical-v1	$.samples[*].expected.probeA.after.#	6	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:415	BOUND
physical-v1	$.samples[*].expected.probeA.mutation.mapSizeAfter	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:414	BOUND
physical-v1	$.samples[*].expected.probeA.mutation.#	4	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeA.autoRepairObserved	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:419	BOUND
physical-v1	$.samples[*].expected.probeA.before.#	6	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeA.target.#	12	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeA.fixture	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeA.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:406	BOUND
physical-v1	$.samples[*].expected.probeB.before.auditOutcome	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:469	BOUND
physical-v1	$.samples[*].expected.probeB.before.#	9	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeB.firstAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:470	BOUND
physical-v1	$.samples[*].expected.probeB.secondAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:483	BOUND
physical-v1	$.samples[*].expected.probeB.after.actualTiles[*]	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeB.after.actualTiles.#	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeB.after.#	7	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:474	BOUND
physical-v1	$.samples[*].expected.probeB.csharpMapping.#	3	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:488	BOUND
physical-v1	$.samples[*].expected.probeB.autoRepairObserved	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:482	BOUND
physical-v1	$.samples[*].expected.probeB.target.#	12	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeB.fixture	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META
physical-v1	$.samples[*].expected.probeB.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:466	BOUND
physical-v1	$.samples[*].expected.#	33	OUT_DIFF	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:101	BOUND
physical-v1	$.samples[*].#	2	SEM_MAP	V5PhysicalGoldenExporterTest.kt	PhysicalGoldenTests.cs:83	BOUND
scoring-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META
scoring-v1	$.schema	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:25	BOUND
scoring-v1	$.rulesetVersion	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:26	BOUND
scoring-v1	$.v4Commit	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:29	BOUND
scoring-v1	$.extractionStatus	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:39	BOUND
scoring-v1	$.generatedByCommand	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.exportedAtNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.generatorVersion	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.unexportedSamples	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.generationResult.#	10	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:41	BOUND
scoring-v1	$.winSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:33	BOUND
scoring-v1	$.winSamples[*].expected.status	1	UNBOUND	V5ScoringGoldenExporterTest.kt	GAP:N3-E1	GAP:N3-E1
scoring-v1	$.winSamples[*].expected.#	50	OUT_DIFF	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:207	BOUND
scoring-v1	$.winSamples[*].input.extraHan	1	UNBOUND	V5ScoringGoldenExporterTest.kt	GAP:N3-E1	GAP:N3-E1
scoring-v1	$.winSamples[*].input.#	32	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:207	BOUND
scoring-v1	$.winSamples[*].tags.#	1	META	meta:authoring-labels	meta:authoring-labels	META
scoring-v1	$.winSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.winSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.winSamples[*].#	5	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
scoring-v1	$.noWinSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:38	BOUND
scoring-v1	$.noWinSamples[*].input.doraIndicators	1	UNBOUND	V5ScoringGoldenExporterTest.kt	GAP:N3-E1	GAP:N3-E1
scoring-v1	$.noWinSamples[*].input.uraDoraIndicators	1	UNBOUND	V5ScoringGoldenExporterTest.kt	GAP:N3-E1	GAP:N3-E1
scoring-v1	$.noWinSamples[*].input.honba	1	UNBOUND	V5ScoringGoldenExporterTest.kt	GAP:N3-E1	GAP:N3-E1
scoring-v1	$.noWinSamples[*].input.riichiSticks	1	UNBOUND	V5ScoringGoldenExporterTest.kt	GAP:N3-E1	GAP:N3-E1
scoring-v1	$.noWinSamples[*].input.#	27	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:244	BOUND
scoring-v1	$.noWinSamples[*].expected.status	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:247	BOUND
scoring-v1	$.noWinSamples[*].expected.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:247	BOUND
scoring-v1	$.noWinSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.noWinSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.noWinSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
scoring-v1	$.randomSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:32	BOUND
scoring-v1	$.randomSamples[*].expected.#	14	OUT_DIFF	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:372	BOUND
scoring-v1	$.randomSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.randomSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.randomSamples[*].#	32	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:372	BOUND
scoring-v1	$.randomMeta.deterministicNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.randomMeta.#	42	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:105	BOUND
scoring-v1	$.scoreSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:34	BOUND
scoring-v1	$.scoreSamples[*].expected.#	46	OUT_DIFF	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:259	BOUND
scoring-v1	$.scoreSamples[*].input.#	4	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:259	BOUND
scoring-v1	$.scoreSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.scoreSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.scoreSamples[*].#	3	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
scoring-v1	$.fuSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:35	BOUND
scoring-v1	$.fuSamples[*].expected.#	9	OUT_DIFF	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:289	BOUND
scoring-v1	$.fuSamples[*].input.#	21	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:289	BOUND
scoring-v1	$.fuSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.fuSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.fuSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
scoring-v1	$.doraSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:36	BOUND
scoring-v1	$.doraSamples[*].expected.#	11	OUT_DIFF	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:310	BOUND
scoring-v1	$.doraSamples[*].input.#	15	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:310	BOUND
scoring-v1	$.doraSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.doraSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.doraSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
scoring-v1	$.settlementSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:37	BOUND
scoring-v1	$.settlementSamples[*].expected.#	6	OUT_DIFF	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:326	BOUND
scoring-v1	$.settlementSamples[*].input.#	13	IN_BIND	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:326	BOUND
scoring-v1	$.settlementSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
scoring-v1	$.settlementSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt	ScoringGoldenTests.cs:199	BOUND
scoring-v1	$.settlementSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
round-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META
round-v1	$.schema	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:80	BOUND
round-v1	$.extractionStatus	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:81	BOUND
round-v1	$.rulesetVersion	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:82	BOUND
round-v1	$.v4Commit	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:83	BOUND
round-v1	$.generatedByCommand	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:84	BOUND
round-v1	$.exportedAtNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.reproducibilityNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.v4DotnetRulesCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.v4EngineSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.workingTreeDirty	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.unexportedSamples[*]	1	META	meta:list-items-not-consumed	meta:list-items-not-consumed	META
round-v1	$.unexportedSamples.#	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:85	BOUND
round-v1	$.generationResult.controllerEpochSamples	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.generationResult.exporterTestResult	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
round-v1	$.generationResult.#	4	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:86	BOUND
round-v1	$.roundTerminal.sampleId	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:90	BOUND
round-v1	$.roundTerminal.status	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:92	BOUND
round-v1	$.roundTerminal.expected.eventTypes[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.expected.eventTypes.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.expected.phaseAfter	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.expected.resultOutcome.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.expected.#	9	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:101	BOUND
round-v1	$.roundTerminal.input.phaseBefore	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.input.versionBefore	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.input.#	6	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:110	BOUND
round-v1	$.roundTerminal.settlement.outcome	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.settlement.totalDeltas[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.settlement.totalDeltas.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.roundTerminal.settlement.#	8	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:115	BOUND
round-v1	$.roundTerminal.#	6	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
round-v1	$.chankanRon.sampleId	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:91	BOUND
round-v1	$.chankanRon.status	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:93	BOUND
round-v1	$.chankanRon.expected.eventTypes[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.eventTypes.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.hasKanDeclared	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.hasRinshanDraw	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.legalRonSeat1	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.meldTypeAfterKan	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.phaseAfterKan	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.phaseAfterRon	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.resultOutcome.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.expected.#	13	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:151	BOUND
round-v1	$.chankanRon.input.meldTiles[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.input.meldTiles.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt	GAP:N3-B4	GAP:N3-B4
round-v1	$.chankanRon.input.#	7	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:158	BOUND
round-v1	$.chankanRon.#	6	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
round-v1	$.controlEpoch.operations[*].epochs[*]	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:429	BOUND
round-v1	$.controlEpoch.operations[*].epochs.#	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:429	BOUND
round-v1	$.controlEpoch.operations[*].kinds[*]	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:432	BOUND
round-v1	$.controlEpoch.operations[*].kinds.#	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:432	BOUND
round-v1	$.controlEpoch.operations[*].result	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:420	BOUND
round-v1	$.controlEpoch.operations[*].#	5	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:391	BOUND
round-v1	$.controlEpoch.#	9	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
round-v1	$.decisionClock[*].operations[*].deadline	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:339	BOUND
round-v1	$.decisionClock[*].operations[*].reserveAtOpen	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:340	BOUND
round-v1	$.decisionClock[*].operations[*].generation	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:341	BOUND
round-v1	$.decisionClock[*].operations[*].result	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:347	BOUND
round-v1	$.decisionClock[*].operations[*].count	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:351	BOUND
round-v1	$.decisionClock[*].operations[*].value	1	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:374	BOUND
round-v1	$.decisionClock[*].operations[*].#	7	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:311	BOUND
round-v1	$.decisionClock[*].config.#	4	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:320	BOUND
round-v1	$.decisionClock[*].status	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:96	BOUND
round-v1	$.decisionClock[*].#	8	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
round-v1	$.decisionClock.#	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:94	BOUND
round-v1	$.matchProgression[*].expected.type	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:273	BOUND
round-v1	$.matchProgression[*].expected.#	15	OUT_DIFF	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:236	BOUND
round-v1	$.matchProgression[*].config.#	14	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:245	BOUND
round-v1	$.matchProgression[*].input.#	16	IN_BIND	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:259	BOUND
round-v1	$.matchProgression[*].#	8	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META
round-v1	$.matchProgression.#	1	SEM_MAP	V5RoundGoldenExportTest.kt	RoundReplayGoldenTests.cs:87	BOUND
cheat-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META
cheat-v1	$.schema	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:138	BOUND
cheat-v1	$.extractionStatus	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:139	BOUND
cheat-v1	$.v4Commit	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:141	BOUND
cheat-v1	$.generatorVersion	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:145	BOUND
cheat-v1	$.rulesetVersion	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:146	BOUND
cheat-v1	$.generatedByCommand	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:147	BOUND
cheat-v1	$.exportedAtNote	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:148	BOUND
cheat-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
cheat-v1	$.slotConventionNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
cheat-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
cheat-v1	$.ledger	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:151	BOUND
cheat-v1	$.ledger.initialPoints	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:169	BOUND
cheat-v1	$.ledger.initialPoints[*]	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:169	BOUND
cheat-v1	$.ledger.initialPoints.#	0	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:169	BOUND
cheat-v1	$.ledger.chargeOnce.entryId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:175	BOUND
cheat-v1	$.ledger.chargeOnce.seat	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:175	BOUND
cheat-v1	$.ledger.chargeOnce.amount	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:175	BOUND
cheat-v1	$.ledger.chargeOnce.reason	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:175	BOUND
cheat-v1	$.ledger.chargeOnce.balanceAfter	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:178	BOUND
cheat-v1	$.ledger.chargeOnce.balancesAfter[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:179	BOUND
cheat-v1	$.ledger.chargeOnce.balancesAfter.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:179	BOUND
cheat-v1	$.ledger.chargeOnce.entryCount	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:185	BOUND
cheat-v1	$.ledger.chargeOnce.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:170	BOUND
cheat-v1	$.ledger.replaySamePayload.returnsOriginalEntry	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:191	BOUND
cheat-v1	$.ledger.replaySamePayload.balanceAfter	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:192	BOUND
cheat-v1	$.ledger.replaySamePayload.entryCount	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:193	BOUND
cheat-v1	$.ledger.replaySamePayload.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:188	BOUND
cheat-v1	$.ledger.conflictDifferentPayload.requested.#	3	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:198	BOUND
cheat-v1	$.ledger.conflictDifferentPayload.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.conflictDifferentPayload.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.conflictDifferentPayload.balancesAfter[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:205	BOUND
cheat-v1	$.ledger.conflictDifferentPayload.balancesAfter.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:205	BOUND
cheat-v1	$.ledger.conflictDifferentPayload.entryCount	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:207	BOUND
cheat-v1	$.ledger.conflictDifferentPayload.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:196	BOUND
cheat-v1	$.ledger.insufficientFunds.initialPoints	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:211	BOUND
cheat-v1	$.ledger.insufficientFunds.amount	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:212	BOUND
cheat-v1	$.ledger.insufficientFunds.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.insufficientFunds.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.insufficientFunds.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:210	BOUND
cheat-v1	$.ledger.overflow.amount	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:217	BOUND
cheat-v1	$.ledger.overflow.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.overflow.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.overflow.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:216	BOUND
cheat-v1	$.ledger.invalidSeat.seat	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:222	BOUND
cheat-v1	$.ledger.invalidSeat.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.invalidSeat.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B5	GAP:N3-B5
cheat-v1	$.ledger.invalidSeat.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:221	BOUND
cheat-v1	$.ledger.flowSummary.spend.#	3	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:235	BOUND
cheat-v1	$.ledger.flowSummary.gain.#	3	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:236	BOUND
cheat-v1	$.ledger.flowSummary.seat0TotalSpent	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:243	BOUND
cheat-v1	$.ledger.flowSummary.seat1TotalGained	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:244	BOUND
cheat-v1	$.ledger.flowSummary.seat1NetDelta	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:245	BOUND
cheat-v1	$.ledger.flowSummary.finalBalances[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:253	BOUND
cheat-v1	$.ledger.flowSummary.finalBalances.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:253	BOUND
cheat-v1	$.ledger.flowSummary.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:233	BOUND
cheat-v1	$.ledger.snapshotRoundTrip.#	3	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:260	BOUND
cheat-v1	$.tileMarkState	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:277	BOUND
cheat-v1	$.tileMarkState.registerDealtRound.versionAfterFirstRegister	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B4	GAP:N3-B4
cheat-v1	$.tileMarkState.registerDealtRound.#	3	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:280	BOUND
cheat-v1	$.tileMarkState.addGroup.sameIdDifferentContentException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B4	GAP:N3-B4
cheat-v1	$.tileMarkState.addGroup.seatGroupLimitIsThree	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B4	GAP:N3-B4
cheat-v1	$.tileMarkState.addGroup.seatGroupLimitException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B4	GAP:N3-B4
cheat-v1	$.tileMarkState.addGroup.seatTileLimitIsTwelve	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B4	GAP:N3-B4
cheat-v1	$.tileMarkState.addGroup.#	7	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:288	BOUND
cheat-v1	$.tileMarkState.expiry.createdRoundOrdinal	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B4	GAP:N3-B4
cheat-v1	$.tileMarkState.expiry.#	7	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:298	BOUND
cheat-v1	$.tileMarkHandler[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:118	BOUND
cheat-v1	$.tileMarkHandler[*].outcome	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:556	BOUND
cheat-v1	$.tileMarkHandler[*].reason	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:557	BOUND
cheat-v1	$.tileMarkHandler[*].note	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:350	BOUND
cheat-v1	$.tileMarkHandler[*].#	1	CONTAINER	meta:case-array-elements	meta:case-array-elements	META
cheat-v1	$.tileMarkHandler.#	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:153	BOUND
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.availableWallCount	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B3	GAP:N3-B3
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerId	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B3	GAP:N3-B3
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.rinshanPointer	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B3	GAP:N3-B3
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.wallEndExclusive	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B3	GAP:N3-B3
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.wallPointer	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B3	GAP:N3-B3
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.windowId	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B3	GAP:N3-B3
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.#	21	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:714	BOUND
cheat-v1	$.sleeveBloomHandler[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:118	BOUND
cheat-v1	$.sleeveBloomHandler[*].outcome	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:556	BOUND
cheat-v1	$.sleeveBloomHandler[*].reason	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:557	BOUND
cheat-v1	$.sleeveBloomHandler[*].note	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:350	BOUND
cheat-v1	$.sleeveBloomHandler[*].#	1	CONTAINER	meta:case-array-elements	meta:case-array-elements	META
cheat-v1	$.sleeveBloomHandler.#	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:154	BOUND
cheat-v1	$.beamSwapHandler[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:118	BOUND
cheat-v1	$.beamSwapHandler[*].outcome	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:556	BOUND
cheat-v1	$.beamSwapHandler[*].reason	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:557	BOUND
cheat-v1	$.beamSwapHandler[*].note	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:350	BOUND
cheat-v1	$.beamSwapHandler[*].#	2	CONTAINER	meta:case-array-elements	meta:case-array-elements	META
cheat-v1	$.beamSwapHandler.#	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:155	BOUND
cheat-v1	$.tileMovement	1	SEM_MAP	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:583	BOUND
cheat-v1	$.tileMovement[*].factsBefore[*].#	5	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].factsBefore.#	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].deliveredTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].doraRefusalException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].wallSlotRefusalException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].inputLastDiscardSeat	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].note	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].rinshanPointerAfterDraw	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].wallPointerBeforeDelivery	1	UNBOUND	V5CheatGoldenExporterTest.kt	GAP:N3-B1	GAP:N3-B1
cheat-v1	$.tileMovement[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:593	BOUND
cheat-v1	$.tileMovement[*].handTileId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:991	BOUND
cheat-v1	$.tileMovement[*].inputDoraIndicatorTileIds	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:935	BOUND
cheat-v1	$.tileMovement[*].inputDrawnTileTileId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:924	BOUND
cheat-v1	$.tileMovement[*].inputKanCount	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:936	BOUND
cheat-v1	$.tileMovement[*].inputLastDiscardTileId	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:931	BOUND
cheat-v1	$.tileMovement[*].inputPlacements[*].#	5	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:907	BOUND
cheat-v1	$.tileMovement[*].inputPlacements.#	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:907	BOUND
cheat-v1	$.tileMovement[*].inputRinshanPointer	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:929	BOUND
cheat-v1	$.tileMovement[*].inputSeat	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:950	BOUND
cheat-v1	$.tileMovement[*].inputWallEndExclusive	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:928	BOUND
cheat-v1	$.tileMovement[*].inputWallPointer	1	IN_BIND	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:927	BOUND
cheat-v1	$.tileMovement[*].factsAfter.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:1016	BOUND
cheat-v1	$.tileMovement[*].#	39	OUT_DIFF	V5CheatGoldenExporterTest.kt	CheatGoldenTests.cs:581	BOUND
shuffle-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META
shuffle-v1	$.schema	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:32	BOUND
shuffle-v1	$.generatorVersion	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:33	BOUND
shuffle-v1	$.rulesetVersion	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:34	BOUND
shuffle-v1	$.extractionStatus	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:35	BOUND
shuffle-v1	$.v4Commit	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:37	BOUND
shuffle-v1	$.generatedByCommand	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:41	BOUND
shuffle-v1	$.exportedAtNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
shuffle-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
shuffle-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
shuffle-v1	$.rngGapNote	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:46	BOUND
shuffle-v1	$.generationResult.#	5	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:50	BOUND
shuffle-v1	$.configSnapshot.#	96	META	meta:v4-config-not-consumed	meta:v4-config-not-consumed	META
shuffle-v1	$.slotMatrix[*].#	7	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:60	BOUND
shuffle-v1	$.slotMatrix.#	1	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:60	BOUND
shuffle-v1	$.demandVectors.#	13	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:102	BOUND
shuffle-v1	$.overlapMatrix.#	43	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:116	BOUND
shuffle-v1	$.cases.#	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:52	BOUND
shuffle-v1	$.cases[*].deadWallSegmentEqual	1	UNBOUND	V5ShuffleGoldenExporterTest.kt	GAP:N3-B2	GAP:N3-B2
shuffle-v1	$.cases[*].baseWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:156	BOUND
shuffle-v1	$.cases[*].baseWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:156	BOUND
shuffle-v1	$.cases[*].threeCasterWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:156	BOUND
shuffle-v1	$.cases[*].threeCasterWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:156	BOUND
shuffle-v1	$.cases[*].fourCasterWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:180	BOUND
shuffle-v1	$.cases[*].fourCasterWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:180	BOUND
shuffle-v1	$.cases[*].noTargetWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:181	BOUND
shuffle-v1	$.cases[*].noTargetWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:181	BOUND
shuffle-v1	$.cases[*].zeroBudgetWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:182	BOUND
shuffle-v1	$.cases[*].zeroBudgetWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:182	BOUND
shuffle-v1	$.cases[*].redFiveCount	1	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:173	BOUND
shuffle-v1	$.cases[*].fourCasterIdentity	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:183	BOUND
shuffle-v1	$.cases[*].reversedEqualsThree	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:184	BOUND
shuffle-v1	$.cases[*].threeCasterChanged	1	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:193	BOUND
shuffle-v1	$.cases[*].plans[*].#	3	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:147	BOUND
shuffle-v1	$.cases[*].plans.#	1	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:147	BOUND
shuffle-v1	$.cases[*].seed	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:142	BOUND
shuffle-v1	$.cases[*].dealerSeat	1	OUT_DIFF	V5ShuffleGoldenExporterTest.kt	ShuffleGoldenTests.cs:144	BOUND
shuffle-v1	$.cases[*].#	1	CONTAINER	meta:case-array-elements	meta:case-array-elements	META
ai-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META
ai-v1	$.schema	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:37	BOUND
ai-v1	$.generatorVersion	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:38	BOUND
ai-v1	$.rulesetVersion	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:39	BOUND
ai-v1	$.v4Commit	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:40	BOUND
ai-v1	$.extractionStatus	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.generatedByCommand	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.generatedAtHead	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:41	BOUND
ai-v1	$.v4ProductionSourceDirty	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:45	BOUND
ai-v1	$.v4ProductionSourceDirtyPaths	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:46	BOUND
ai-v1	$.v4TestOnlyTrackedDirtyPaths	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:47	BOUND
ai-v1	$.v4TestOnlyTrackedDirtyPaths[*]	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:47	BOUND
ai-v1	$.v4TestOnlyTrackedDirtyPaths.#	0	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:47	BOUND
ai-v1	$.sourceFileSha256[*].sha256	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:62	BOUND
ai-v1	$.sourceFileSha256[*].#	3	META	meta:hash-crosswalk-labels	meta:hash-crosswalk-labels	META
ai-v1	$.sourceFileSha256.#	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:58	BOUND
ai-v1	$.decisionClock.#	8	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:65	BOUND
ai-v1	$.recordedIntent.#	12	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:71	BOUND
ai-v1	$.v5Observation.mappingLimits[*]	1	UNBOUND	V5AiGoldenExporterTest.kt	GAP:N3-B6	GAP:N3-B6
ai-v1	$.v5Observation.mappingLimits.#	1	UNBOUND	V5AiGoldenExporterTest.kt	GAP:N3-B6	GAP:N3-B6
ai-v1	$.v5Observation.#	31	IN_BIND	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:271	BOUND
ai-v1	$.cases.#	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:80	BOUND
ai-v1	$.cases[*].id	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:81	BOUND
ai-v1	$.cases[*].kind	1	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:611	BOUND
ai-v1	$.cases[*].note	1	META	meta:authoring-labels	meta:authoring-labels	META
ai-v1	$.cases[*].v5.authority.#	10	OUT_DIFF	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:464	BOUND
ai-v1	$.cases[*].v5.authorityMapping.#	6	SEM_MAP	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:404	BOUND
ai-v1	$.cases[*].v5.expected.mappingBasis	1	UNBOUND	V5AiGoldenExporterTest.kt	GAP:N3-B6	GAP:N3-B6
ai-v1	$.cases[*].v5.expected.#	3	OUT_DIFF	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:141	BOUND
ai-v1	$.cases[*].v5.intentActionRef	1	IN_BIND	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:378	BOUND
ai-v1	$.cases[*].v5.submittedAtMillis	1	IN_BIND	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:172	BOUND
ai-v1	$.cases[*].v5.#	1	CONTAINER	meta:case-container	meta:case-container	META
ai-v1	$.cases[*].v4Call.entryPoint	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.cases[*].v4Call.#	17	IN_BIND	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:495	BOUND
ai-v1	$.cases[*].v4.#	65	INTERNAL	V5AiGoldenExporterTest.kt	AiReplayGoldenTests.cs:558	BOUND
ai-v1	$.cases[*].#	1	CONTAINER	meta:case-container	meta:case-container	META
ai-v1	$.testOnlyChanges	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.unexportedOrMappedSamples	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.unexportedOrMappedSamples[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.unexportedOrMappedSamples.#	0	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4BehaviorSource	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4DotnetAiSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4DotnetDomainSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4EngineSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4TrackedDirtyPaths	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4TrackedDirtyPaths[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4TrackedDirtyPaths.#	0	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.v4TrackedWorkingTreeDirty	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
ai-v1	$.reproducibility.#	6	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META
```
FIELD-RULES:END

**规则表统计（机器复算：§11.6 emit/check 命令 + 按列筛选命令）**：

| fixture | 规则数 | census 路径数 |
|---|---|---|
| physical-v1 | 78 | 296 |
| scoring-v1 | 63 | 419 |
| round-v1 | 72 | 200 |
| cheat-v1 | 116 | 204 |
| shuffle-v1 | 38 | 199 |
| ai-v1 | 50 | 198 |
| **合计** | **417** | **1516** |

**按判级类合计（1516 条路径的终态分布；由 FIELD-RULES 块第 3 列求和复算）**：

| 类 | 路径数 | 含义速记 |
|---|---|---|
| IN_BIND | 328 | 输入绑定 |
| OUT_DIFF | 439 | 输出差分 |
| SEM_MAP | 266 | 语义映射（均带真实 C# 锚点，无 GAP 抵扣） |
| INTERNAL | 75 | fixture 内部结构/自检（显式声明） |
| UNBOUND | 47 | 未绑定（GAP 补齐包） |
| UNBOUND_HC | 40 | 硬编码未绑定（GAP 补齐包） |
| META | 308 | exporter 元数据（显式排除） |
| CONTAINER | 13 | 纯容器节点 |
| **合计** | **1516** | |

- **GAP（已导出未消费）按补齐包分布 = 87 条**：GAP:N3-B1 13（tileMovement：UNBOUND 11 + UNBOUND_HC 2）、GAP:N3-B2 1（shuffle deadWallSegmentEqual）、GAP:N3-B3 6（handler goldenInputs 树：tileMark 0/20、beamSwap 0/23 未引用节的 goldenInputs 路径）、GAP:N3-B4 26（round 20 + tileMarkState 6，全 UNBOUND_HC）、GAP:N3-B5 8（ledger 4 子键 outcome/exception 叶，全 UNBOUND_HC）、GAP:N3-B6 3（ai mappingBasis/mappingLimits）、GAP:N3-E1 6（scoring win status/extraHan + noWin 四字段）、GAP:N3-E2 24（physical：boundaries[*] 11 + boundarySamples 6 + invalid[*] 3 = UNBOUND 20；csharpExceptionMapping 4 = UNBOUND_HC）。**owner 列合计：绑定/映射/结构 owner = 1108 条，META owner = 321 条，GAP owner = 87 条，三者之和 = 1516。**
- **第 2 轮规则表缺陷的修复对照**：第 2 轮表存在 17 处 catch-all 误配（census 的 `[*]` 紧贴段名文法下 `X.#` 不命中 `X[*]` 系列，导致 factsBefore[*].#、inputPlacements[*].#、balancesAfter[*]、finalBalances[*]、eventTypes[*]、epochs/kinds、5 组 wall [*]、plans[*].#、mappingLimits[*]、probeB actualTiles[*] 等被错误归类）与 7 处 catch-all 隐没（`$.roundTerminal.input.#`、`$.roundTerminal.settlement.#`、`$.chankanRon.input.#` 把未读叶 phaseBefore/versionBefore/outcome/totalDeltas/meldTiles 隐没为"已绑定"）。第 3 轮全部展开为显式规则并复算：类迁移对账 OUT_DIFF 443→439、IN_BIND 336→328、UNBOUND 41→47、UNBOUND_HC 31→40、CONTAINER 21→13、INTERNAL 70→75、META 307→308、SEM_MAP 267→266（迁移前后总和均 1516）。
- **checker 负例矩阵（R2-1a 验收：三种作弊路径都被检出；命令与日志全文见 §11.6）**：

| 负例 | 变异 | 检出结果 | 日志 SHA256 |
|---|---|---|---|
| strip-anchors | 清空全部 IN_BIND/OUT_DIFF/INTERNAL/SEM_MAP 规则的 csAnchor | 254 × ANCHOR-C#，RULES-CHECK-FAILED errors=254，exit 1 | `dfbd76d7d712b9b28b321f3bc1c87f605dea0ef2ee1d877ee8b1f70dfe4ae5cf` |
| drop-rule | 删除首条 paths>0 规则 | 1 × LEAK（physical-v1 根路径失覆盖），exit 1 | `5af2d3f4b07a9028191fa3fcaf231c0ea85cf72b2f3a11d909ce8078b4b2650e` |
| anchorless-rule | 追加一条无锚点 OUT_DIFF 规则 | 1 × ANCHOR-C#，exit 1 | `b92c9f721468cadc418b56396f4d4c76b9509bdec45a136af451a1eb0e2e8d94` |

- **正检实测（RULES-CHECK-OK）**：`RULES-CHECK-OK total-paths=1516 rules=417 leaks=0 mismatches=0 anchor-errors=0`（check-run.log SHA256 `34ffcf3ddb18d5c7d40eff16b34807034286fb94f9f8b3c390ca4470e7334638`）。

### 3.1 physical-v1（catalog 内）

- **SHA256** `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995`；v4Commit `32f645ec…`（真实）；census 296 distinctPaths（FIELD-RULES 78 条规则全划分，§3.0.1）；C# 消费 `PhysicalGoldenTests`（9 Facts 全 PASS）。
- **provenance 链（沿用第 2 轮修正表述）**：fixture 内嵌 `exporterSourceSha256` + `sourceHashAlgorithm` + `v4SourceHashes[3]`；C# 断言（`PhysicalGoldenTests.cs:56`、`:61`）调用 `Sha256SourceFile`（helper 在 `:663-666`）：`File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n")` 再求 SHA256——即 **CRLF→LF 行尾归一化后的 SHA256**。这与 catalog/manifest 记录的 **raw 字节 SHA256 是不同算法口径，两者不可互换**：provenance 自检证明"归一化源文本与 exporter 文件一致"，叠加 catalog 侧 raw hash 一致（§11.9）共同构成双链。
- **组级映射**（prefix-2 组 → 判级；精确路径级判级以 FIELD-RULES 78 条为准）：

| prefix-2 组（distinctPaths） | C# 断言范围 | 判级 |
|---|---|---|
| `$.samples[*]`（249）：G-001a/b/c 身份/赤五/136 张 + G-002 20 case + G-003a 32 墙 map + digest | 全字段逐值（TileId、136 tiles、redFiveIds 19/55/91、逐墙 map、136 守恒、4352 位置） | 行为差分（V4 产物 → C# 断言消费） |
| `$.scope`（8）/`$.generationResult`（7）/`$.hashCrosswalk`（6）/`$.v4SourceHashes[*]`（3）/`$.taskPaths[*]`（1）/`$.gitStatusPorcelain[*]`（1）/其余头部标量 | provenance/清单断言（:56、:61 及上下文） | 输入绑定/自检（归一化 SHA 口径如上） |
| G-003b probeA/probeB | 结构映射（探测路径结构一致） | fixture 内部结构（probe 语义无生产对应，已声明） |
| duplicateKey | — | 不可表示（V4 MutableMap 无法构造重复键 case，NOT_CONSTRUCTIBLE） |

- **第 3 轮新增未绑定缺口（GAP:N3-E2，24 条路径）**：`$.samples[*].expected.boundaries[*]`（11 条：容器 + 叶）、`$.samples[*].expected.boundarySamples.#`（6 条）、`$.samples[*].expected.invalid.#`（3 条）、`$.samples[*].expected.csharpExceptionMapping.#`（4 条，UNBOUND_HC）。PhysicalGoldenTests 的 digest/守恒断言不逐值消费这四组字段；变异级实证 = census 路径行 + FIELD-RULES 显式规则（`physical-v1-full-paths.txt` 可逐条展开），补齐包 N3-E2（§9）。

### 3.2 scoring-v1（catalog 内，第 3 轮分母拆分版）

- **SHA256** `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E`；v4Commit `97831c88…`（真实）；7 组 10238 case；C# 消费 `ScoringGoldenTests`（7 Facts 全 PASS）。
- **第 2 轮算术错误的正式撤回（R2-1d）**："95 条中 94 条被真实断言消费"的分母混入了容器与元数据路径，**该算术无效，作废**。正确口径 = 以 FIELD-RULES 类计数的**逐类分母**，每个分母分子都可由 §11.6 命令复算；"消费率"类聚合表述一律不再使用。
- **winSamples[*]（95 条路径）逐类分母**：

| 类 | 路径数 | 明细 |
|---|---|---|
| IN_BIND | 32 | input.* 构造（hand/context/doraIndicators/uraDoraIndicators/melds 树等）进 `CompareWinSample`（:207-242） |
| OUT_DIFF | 50 | score.*/fu.lines/paymentPlan.components/dora 六元组/yaku 逐项/honba/riichiSticks/totalHan/scoreHan/totalPoints/yakumanCount 逐值 |
| SEM_MAP | 1 | 与 noWinSamples 共享的 status 语义投影（C# 消费锚点在 CompareNoWinSample :247；winSamples 自身的 status 见下 UNBOUND 行） |
| UNBOUND | 2 | **`expected.status`（48 实例）**——`CompareWinSample` 不读 status（`:247` 的断言属 `CompareNoWinSample`）；**`input.extraHan`**——:207-242 未读（noWin 侧 :250 读 extraHan，win 侧无对应断言）。GAP:N3-E1 |
| META | 10 | tags/tags[*]（exporter 元数据，源码 0 次引用，显式排除声明 = N3-E1(b)，caller 裁决前维持"未绑定"计入 §9 分母）+ 容器节点 |

- **noWinSamples[*]（42 条路径）逐类分母**：IN_BIND 27 + OUT_DIFF 3 + SEM_MAP 4 + **UNBOUND 8**（`input.doraIndicators`/`input.uraDoraIndicators`/`input.honba`/`input.riichiSticks` 及其数组叶——`CompareNoWinSample` 只读 status/extraHan，:250；GAP:N3-E1）。
- **其余 5 组**（fuSamples 39 / doraSamples 35 / settlementSamples 28 / scoreSamples 58 / randomSamples 51 条路径）：除容器/META 外全部 OUT_DIFF（fu lines 逐行、dora 六元组、settlement 全字段、payments×5 调 C# 支付函数比对、randomSamples 分布 meta 逐项对账 + `ura[*]` 1208 叶实例）；已导出未绑定 = ∅。
- **结论（第 3 轮版）**：scoring 已导出未绑定 = GAP:N3-E1 的 **6 条路径**（上两表 UNBOUND 行之和），变异实证：win 首例 status win→noWin → 7/7 PASS exit 0（§11.8 第 2 轮探针表）。"尚未导出"类缺口（4han/30fu、28→30、单赤宝牌锚定 case）仍归 §4 表 G-013/014/015，与本节"已导出未绑定"不混计。

### 3.3 round-v1（catalog 内，R1-1 修正版 + 第 3 轮路径计数）— 字段级缺口最集中的一份

- **SHA256** `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`；v4Commit `21a7d2c4…`（真实）；`workingTreeDirty=true`（导出时 V4 工作树有测试侧脏文件——来源链弱于 physical，如实记录）；C# 消费 `RoundReplayGoldenTests`（6 Facts 全 PASS）。
- **exporter 关键源码（R1-1 事实基础）**：`V5RoundGoldenExportTest.kt:240` 起导出收尾——`:275` `"eventTypes" to arr(events.map { j(it) })`；**`:276` `"hasKanDeclared" to j(events.contains("KanDeclared"))`、`:277` `"hasRinshanDraw" to j(events.contains("RinshanDraw"))`**——两个导出布尔 = 终局事件列表是否包含对应事件，与 C# `:225-226` 的 `DoesNotContain` 同语义同方向，名称映射 RinshanDraw→RinshanDrawn。fixture 中两布尔均为 `false`。
- **roundTerminal（RG-001）逐字段绑定表**（C# = `RoundTerminalReplaysExactly` :101-148；**GAP:N3-B4 的 round 部分 = 9 条路径**）：

| fixture 字段（导出值） | C# 绑定 | 判级 |
|---|---|---|
| input.roundId/dealer/scores/riichiSticks | ✅ 作为 P13.Dealt 构造输入 | 黄金输入绑定 |
| input.phaseBefore（"DISCARD"）/ input.versionBefore（0） | ❌ C# 硬编码 `RoundPhase.Discard` 起手（:110-113） | **UNBOUND_HC**（GAP:N3-B4） |
| settlement.resultId/winner/finalScores/remainingSticks/completedAtUtc/completedAtMonotonicMillis | ✅ Tsumo 命令载荷 | 黄金输入绑定 |
| settlement.outcome（"Tsumo"） | ❌ C# 硬编码 `RoundOutcome.Tsumo(winner)`（:117），导出串未比对 | **UNBOUND_HC**（GAP:N3-B4） |
| settlement.totalDeltas | ❌ C# 用 `expected.resultDeltas` 替代（:131），`settlement.totalDeltas` 未读（容器+叶 2 条路径） | **UNBOUND_HC**（GAP:N3-B4） |
| expected.versionAfter/resultFinalScores/resultDeltas/rejectedActions[3]/versionAfterRejections | ✅ 逐值（:129 前后区块） | 输出差分 |
| expected.phaseAfter（"ROUND_END"） | ⚠️ 硬编码 `RoundPhase.RoundEnd`（:129），未读导出值 | UNBOUND_HC（GAP:N3-B4） |
| expected.resultOutcome（"Tsumo"） | ⚠️ 硬编码 `RoundOutcomeKind.Tsumo`（:130） | UNBOUND_HC（GAP:N3-B4） |
| expected.eventTypes[3] = **[TsumoWin, ScoreSettled, RoundEnded]** | ⚠️ 仅 `Contains(TsumoWin)`（:134）（容器+叶 2 条路径） | UNBOUND_HC（GAP:N3-B4，G-028a 核心） |

- **chankanRon（RG-002）逐字段绑定表**（C# = `ChankanRonReplaysExactly` :151-233；**GAP:N3-B4 的 round 部分 = 11 条路径**）：

| fixture 字段（导出值） | C# 绑定 | 判级 |
|---|---|---|
| input.roundId/kanserSeat/ronSeat/addedTileId/eligibleSeats | ✅ P13.ChankanDealt 构造输入 | 黄金输入绑定 |
| input.meldTiles[3]（面子预置） | ❌ 导出值未比对（容器+叶 2 条路径，:162 起未读） | **UNBOUND_HC**（GAP:N3-B4） |
| expected.rinshanPointerAfterKan/totalKanCountAfterKan/rinshanPointerAfterRon/totalKanCountAfterRon/winner/loser/resultId/finalScores/totalDeltas/versionAfterRon | ✅ 逐值（:231 等） | 输出差分 |
| expected.phaseAfterKan（"CHANKAN_WINDOW"）/ phaseAfterRon（"ROUND_END"）/ resultOutcome（"Ron"） | ⚠️ 全部硬编码（:221/:227），导出值未比对 | UNBOUND_HC（GAP:N3-B4） |
| expected.eventTypes[3] = **[RonWin, ScoreSettled, RoundEnded]** | ❌ 事件面仅 `:225-226` 两个 `DoesNotContain`（容器+叶 2 条路径） | UNBOUND_HC（GAP:N3-B4，G-028a 核心） |
| expected.meldTypeAfterKan（"PON"） | ❌ | UNBOUND_HC（GAP:N3-B4） |
| expected.legalRonSeat1（true） | ❌ 窗口资格由 eligibleSeats 间接走过，导出布尔未比对 | UNBOUND_HC（GAP:N3-B4） |
| expected.hasKanDeclared（false） | ❌ 未绑定。**可语义比较**：= fixture 终局事件含 KanDeclared 与否 ↔ C# 终局事件表 `Contains("KanDeclared")`；变异探针双改 true → 6/6 PASS exit 0（§11.8）。判级按 §1.4 保守原则记 **UNBOUND_HC**（GAP:N3-B4），语义可比性经 §13 INFO-1 记录，补齐 = N3-A2 等值绑定 | UNBOUND_HC·语义可比（GAP:N3-B4） |
| expected.hasRinshanDraw（false） | ❌ 同上，C# 侧名称 **RinshanDrawn** | UNBOUND_HC·语义可比（GAP:N3-B4） |
| 状态哈希守恒 | ✅ hashBefore 前后比对 | 输出差分（等效物理守恒） |

- **缺口计数（第 3 轮精确口径，双视图对账）**：**字段级条目视图 = 16**（roundTerminal 7 + chankanRon 9，第 1 轮"18"作废）；**census 路径视图 = 20 条 UNBOUND_HC 路径**（roundTerminal 9 + chankanRon 11——totalDeltas/eventTypes/meldTiles 各计容器+叶 2 条）。两视图是同一缺口集合的粒度差异，§9 N3-B4/A1/A2 以路径视图为机器分母、以条目视图为断言明细。
- **事件序列投影规则（R1-1，沿用）**：N3-A1/A2 的 C# 期望序列 = fixture `eventTypes` 按 §6 表示映射投影（ScoreSettled 信息由 `RoundSettlement/RoundResult` 字段承载 → 从事件序列折叠）→ roundTerminal 投影为 `[TsumoWin, RoundEnded]`、chankanRon 投影为 `[RonWin, RoundEnded]`；断言注释必须引用 fixture 原始序列与投影规则。
- 其余组：matchProgression 16 case 全字段消费（含 allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame/tieBreakSeats/tenpai/transferSeat）；decisionClock 41 ops 全走 + ≥35 断言；controlEpoch 6 ops 全走；unexportedSamples[3] 显式声明未导出项。本节未列"弱化/未绑定"的行均为逐值绑定，无沉默省略。

### 3.4 cheat-v1（catalog 内，第 3 轮精确分桶修正版）

- **SHA256** `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；v4Commit `a96c195c…`（真实）；C# 消费 `CheatGoldenTests`（16 Facts 全 PASS）。
- **第 2 轮分桶错误的正式更正（R2-3a）**：case **#23 sb-activate-already-used 实为 A 桶（已绑定）**——`CheatGoldenTests.cs:486-490` 经 `AssertGoldenRejection` → `:557 Assert.Equal(ReasonMap[Str(c,"reason")], expected)` 真实读取该 case 的导出 reason 并与 C# 实算错误码差分；**正控制探针**把该 case 的 reason 篡改为 WRONG_PHASE 后 → `Handler_SleeveBloom_RepresentativeCases_ReplayAgainstCheatSubmission [FAIL]`（Assert.Equal Failure，Total:16 Failed:1，exit 1，§11.8 探针 P1）——第 2 轮把 #23 记为 D-α（无黄金对照）与源码事实矛盾，**更正为 A**。
- **75 个 handler case 逐条分桶**（tileMark 20 + sleeveBloom 32 + beamSwap 23；桶定义：**A** 黄金输出行为重放（真 C# 入口 + outcome/reason 逐值对照）、**B** 部分绑定、**C** goldenInputs 驱动全链路重放、**D** 无黄金行为对照（仅 `Handler_Sections_CoverEveryRejectionReason` :339-365 的 reason 映射完备性覆盖）、**E** 纯 C# 存在断言但黄金条目未绑定。分桶依据 = 源码字面量差集初筛 + 逐断言深读判级；**机器复算命令与 75 行 TSV 见 §11.7（case-refs.ps1，-File 形式，exit 0）**）：

| # | 节 | caseId | outcome | reason | 桶 |
|---|---|---|---|---|---|
| 1 | tileMark | tm-allowed | Allowed | null | **D-α（Allowed 主路径）** |
| 2 | tileMark | tm-not-your-turn | Rejected | NOT_YOUR_TURN | A |
| 3 | tileMark | tm-wrong-phase-preparation | Rejected | WRONG_PHASE | D-α |
| 4 | tileMark | tm-wrong-phase-draw | Rejected | WRONG_PHASE | D-α |
| 5 | tileMark | tm-wrong-phase-round-end | Rejected | DECISION_MISMATCH | **D-β（InvalidRequest 合并语义）** |
| 6 | tileMark | tm-response-window | Rejected | WRONG_PHASE | **B**（reason 绑定 + C# 特化 ResponseWindowOpen 同族码，非同路径） |
| 7 | tileMark | tm-riichi-locked | Rejected | TILE_MARK_RIICHI_LOCKED | A |
| 8 | tileMark | tm-invalid-selection-three | Rejected | TILE_MARK_INVALID_SELECTION | A |
| 9 | tileMark | tm-invalid-selection-duplicate | Rejected | TILE_MARK_INVALID_SELECTION | D-α |
| 10 | tileMark | tm-tile-not-owned | Rejected | TILE_MARK_TILE_NOT_OWNED | A |
| 11 | tileMark | tm-already-marked | Rejected | TILE_MARK_ALREADY_MARKED | A |
| 12 | tileMark | tm-capacity-groups | Rejected | TILE_MARK_CAPACITY_EXCEEDED | D-α |
| 13 | tileMark | tm-capacity-tiles | Rejected | TILE_MARK_CAPACITY_EXCEEDED | D-α |
| 14 | tileMark | tm-insufficient-funds | Rejected | INSUFFICIENT_FUNDS | A |
| 15 | tileMark | tm-sleeve-pending | Rejected | SLEEVE_BLOOM_CHOICE_PENDING | D-α |
| 16 | tileMark | tm-offturn-sleeve-pending | Rejected | SLEEVE_BLOOM_CHOICE_PENDING | D-α |
| 17 | tileMark | tm-beam-pending | Rejected | BEAM_SWAP_CHOICE_PENDING | D-α |
| 18 | tileMark | tm-no-active-decision | Rejected | NO_ACTIVE_DECISION | **D-β（InvalidRequest 合并语义）** |
| 19 | tileMark | tm-deadline-expired | Rejected | DEADLINE_EXPIRED | **D-β（Domain 输入不可表示，见下）** |
| 20 | tileMark | tm-decision-mismatch | Rejected | DECISION_MISMATCH | **D-β（InvalidRequest 合并语义）** |
| 21 | sleeveBloom | sb-activate-allowed | Allowed | null | C |
| 22 | sleeveBloom | sb-activate-dealer-extra | Rejected | SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED | D-α |
| 23 | sleeveBloom | sb-activate-already-used | Rejected | SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW | **A（第 3 轮更正；:486-490 → :557 ReasonMap 差分绑定；正控制探针实证）** |
| 24 | sleeveBloom | sb-activate-beam-pending | Rejected | BEAM_SWAP_ALREADY_PENDING | D-α |
| 25 | sleeveBloom | sb-activate-sleeve-pending | Rejected | SLEEVE_BLOOM_ALREADY_PENDING | D-α |
| 26 | sleeveBloom | sb-activate-wall-empty | Rejected | SLEEVE_BLOOM_WALL_EMPTY | D-α |
| 27 | sleeveBloom | sb-activate-wall-empty-drift | Rejected | SLEEVE_BLOOM_WALL_EMPTY | D-α |
| 28 | sleeveBloom | sb-activate-no-drawn-tile | Rejected | SLEEVE_BLOOM_NO_DRAWN_TILE | D-α |
| 29 | sleeveBloom | sb-activate-not-your-turn | Rejected | NOT_YOUR_TURN | D-α |
| 30 | sleeveBloom | sb-activate-wrong-phase | Rejected | WRONG_PHASE | D-α |
| 31 | sleeveBloom | sb-activate-response-window | Rejected | WRONG_PHASE | D-α |
| 32 | sleeveBloom | sb-activate-disabled | Rejected | SLEEVE_BLOOM_DISABLED | A |
| 33 | sleeveBloom | sb-activate-insufficient | Rejected | INSUFFICIENT_FUNDS | A |
| 34 | sleeveBloom | sb-activate-rinshan-allowed | Allowed | null | C |
| 35 | sleeveBloom | sb-activate-rinshan-empty | Rejected | SLEEVE_BLOOM_WALL_EMPTY | D-α |
| 36 | sleeveBloom | sb-choose-first | Allowed | null | **E**（:480 仅注释 + 独立成功断言，无 `Case(...)` 读取该 golden case；变异探针 outcome→Rejected+reason→WRONG_PHASE 仍 16/16 PASS，探针 P2 复证） |
| 37 | sleeveBloom | sb-choose-second | Allowed | null | C |
| 38 | sleeveBloom | sb-choose-stale-turn | StaleOffer | SLEEVE_BLOOM_STALE_OFFER | A |
| 39 | sleeveBloom | sb-choose-stale-drawn | StaleOffer | SLEEVE_BLOOM_STALE_OFFER | D-α（Stale） |
| 40 | sleeveBloom | sb-choose-stale-index | StaleOffer | SLEEVE_BLOOM_STALE_OFFER | D-α（Stale） |
| 41 | sleeveBloom | sb-choose-offer-mismatch | Rejected | SLEEVE_BLOOM_OFFER_MISMATCH | D-α |
| 42 | sleeveBloom | sb-choose-no-offer | Rejected | SLEEVE_BLOOM_NO_PENDING_OFFER | D-α |
| 43 | sleeveBloom | sb-choose-rinshan | Allowed | null | C |
| 44 | sleeveBloom | sbo-activate-allowed | Allowed | null | C |
| 45 | sleeveBloom | sbo-activate-no-window | Rejected | SLEEVE_BLOOM_NO_WINDOW | A |
| 46 | sleeveBloom | sbo-activate-reserved | Rejected | SLEEVE_BLOOM_RESERVED_PENDING | D-α |
| 47 | sleeveBloom | sbo-activate-own-turn | Rejected | NOT_YOUR_TURN | D-α |
| 48 | sleeveBloom | sbo-activate-wall-empty | Rejected | SLEEVE_BLOOM_WALL_EMPTY | D-α |
| 49 | sleeveBloom | sbo-choose-allowed | Allowed | null | C |
| 50 | sleeveBloom | sbo-choose-expired | Rejected | DEADLINE_EXPIRED | D-α（C# off-turn 路径 :553-560/:849 可构造） |
| 51 | sleeveBloom | sbo-choose-stale-first | Rejected | SLEEVE_BLOOM_STALE_OFFER | D-α |
| 52 | sleeveBloom | sbo-choose-stale-second | Rejected | SLEEVE_BLOOM_STALE_OFFER | D-α |
| 53 | beamSwap | bm-activate-allowed | Allowed | null | **D-α（Allowed 主路径）** |
| 54 | beamSwap | bm-activate-no-candidates | Rejected | BEAM_SWAP_NO_CANDIDATES | D-α |
| 55 | beamSwap | bm-activate-dealer-extra | Rejected | BEAM_SWAP_DEALER_EXTRA_EXCLUDED | D-α |
| 56 | beamSwap | bm-activate-riichi | Rejected | BEAM_SWAP_RIICHI_LOCKED | D-α |
| 57 | beamSwap | bm-activate-no-drawn | Rejected | BEAM_SWAP_NO_DRAWN_TILE | D-α |
| 58 | beamSwap | bm-activate-beam-pending | Rejected | BEAM_SWAP_ALREADY_PENDING | D-α |
| 59 | beamSwap | bm-activate-sleeve-pending | Rejected | BEAM_SWAP_ALREADY_PENDING | D-α |
| 60 | beamSwap | bm-activate-already-used | Rejected | BEAM_SWAP_ALREADY_USED_THIS_DRAW | A |
| 61 | beamSwap | bm-activate-disabled | Rejected | BEAM_SWAP_DISABLED | A |
| 62 | beamSwap | bm-activate-insufficient | Rejected | INSUFFICIENT_FUNDS | A |
| 63 | beamSwap | bm-activate-deadwall-allowed | Allowed | null | **D-α（Allowed 主路径）** |
| 64 | beamSwap | bm-activate-deadwall-empty | Rejected | BEAM_SWAP_NO_CANDIDATES | D-α |
| 65 | beamSwap | bm-activate-deadwall-dora-only | Rejected | BEAM_SWAP_NO_CANDIDATES | D-α |
| 66 | beamSwap | bm-choose-swap | Allowed | null | **D-α（Allowed 主路径）** |
| 67 | beamSwap | bm-choose-keep | Allowed | null | **B**（仅 outcome 比对 :539-544） |
| 68 | beamSwap | bm-choose-invalid-slot | Rejected | BEAM_SWAP_INVALID_CHOICE | D-α |
| 69 | beamSwap | bm-choose-invalid-foreign-hand | Rejected | BEAM_SWAP_INVALID_CHOICE | A |
| 70 | beamSwap | bm-keep-original-args-forbidden-at-request | Rejected | BEAM_SWAP_INVALID_CHOICE（requestException=IllegalArgumentException） | D-α（reqEx） |
| 71 | beamSwap | bm-choose-stale-turn | StaleOffer | BEAM_SWAP_STALE_OFFER | A |
| 72 | beamSwap | bm-choose-stale-drawn | StaleOffer | BEAM_SWAP_STALE_OFFER | D-α（Stale） |
| 73 | beamSwap | bm-choose-deadwall-allowed | Allowed | null | **D-α（Allowed 主路径）** |
| 74 | beamSwap | bm-choose-deadwall-dora-stale | StaleOffer | BEAM_SWAP_STALE_OFFER | D-α（Stale） |
| 75 | beamSwap | bm-choose-replacement-allowed | Allowed | null | **D-α（Allowed 主路径）** |

- **分桶汇总（第 3 轮精确版，与 §4/§9/§13 一致；机器复算 = §11.7 case-refs-summary.txt）**：**A=16**（tm 6：#2/#7/#8/#10/#11/#14；sb 5：#23/#32/#33/#38/#45；bm 5：#60/#61/#62/#69/#71）；**B=2**（#6/#67）；**C=6**（#21/#34/#37/#43/#44/#49）；**E=1**（#36）；**D=50**（**D-α 可构造可绑定 46**：含 Allowed 主路径 6（#1/#53/#63/#66/#73/#75）、StaleOffer 6（#39/#40/#51/#52/#72/#74）、requestException 1（#70）、其余 Rejected 33；**D-β 需上层/合并语义映射 4**：#5/#18/#19/#20）。合计 16+2+6+1+50 = **75**。
- **引用侧口径（R2-3d 实测，机器复算 = §11.7）**：源码字面量引用 = tileMark **7**、sleeveBloom **11**、beamSwap **6**，合计 **24**；未引用 13/21/17 合计 51（其中 #23 在引用侧 11 内且为行为绑定）。**goldenInputs 真实覆盖率：tileMark 0/20、beamSwap 0/23、sleeveBloom 7/32**——C 桶全在 sleeveBloom；**"复用现有 goldenInputs 构造"对 D 桶不成立**：D50 的构造输入须从 V4 exporter 源码（`V5CheatGoldenExporterTest.kt` 对应 case 构造段）重建，§9 N3-B3 已按此改写（不再声称复用）。goldenInputs 树本身的 6 条 census 路径（未引用两节的容器/叶）= GAP:N3-B3。
- **D-β 4 例的真实 C# 对应层（沿用）**：#19 tm-deadline-expired——`CheatTurnContext`（`CheatSubmission.cs:12-25`）无 deadline/decision 字段，C# `DEADLINE_EXPIRED` 仅存在于 off-turn 选择路径 `ChooseOffTurnSleeveBloom`（:553-560，测试侧 :849 已走，对应 #50）；在岗 tileMark 的 deadline 到期在 Domain 输入层不可表示——绑定需"等价类断言 + 映射注释 + owner 批准"（§9 N3-B3(b)）。#5/#18/#20（DECISION_MISMATCH/NO_ACTIVE_DECISION）：对应层为请求校验的 **InvalidRequest 合并语义**——绑定 = 对实际合并 outcome 断言 + 映射注释。#5 特记：caseId 带 "wrong-phase" 但导出 reason 实为 DECISION_MISMATCH（分桶以导出 reason 为准）。
- **ledger 节（第 3 轮再修正，R2-1c）**：53 条路径逐类 = **SEM_MAP 4 + IN_BIND 26 + UNBOUND_HC 8 + OUT_DIFF 15**。第 2 轮"9 子键中 8 个为行为差分"**作废**：`conflictDifferentPayload/insufficientFunds/overflow/invalidSeat` 4 个子键的 `outcome`/`exception` 导出叶（8 条路径，GAP:N3-B5）当前**未被任何 C# 断言消费**——变异探针 P4 把 4 子键 outcome rejected→accepted、exception 具体类→NOT_AN_EXCEPTION 后 16/16 PASS exit 0（§11.8）；其余 5 子键（initialPoints/chargeOnce/replaySamePayload/insufficientFunds 主余额流/flowSummary 的差分面）维持 OUT_DIFF/IN_BIND。`snapshotRoundTrip` = 布尔信任（`Assert.True(Bool(...))` :260-261，SEM_MAP 4 条）+ `CheatLedger.FromSnapshot` 逐余额比对，**restored.Entries 无逐项黄金比较，fixture 中亦无 golden Entries 数组可对照（snapshot 子键仅含 balancesEqual/entriesEqual 两布尔）**——第 2 轮"绑定 golden Entries 数组"的建议对象不存在，N3-E1 注记改为：如需 Entries 级对照须先在 exporter 侧扩导出（属 catalog 语义变更，caller 裁决；本审计不改 fixture）。
- **tileMarkState（第 3 轮再修正，R2-1b）**：24 条路径逐类 = **SEM_MAP 1 + UNBOUND_HC 6 + OUT_DIFF 17**。第 2 轮"3/3 子键行为差分"**作废**：`versionAfterFirstRegister/sameIdDifferentContentException/seatGroupLimitIsThree/seatGroupLimitException/seatTileLimitIsTwelve/createdRoundOrdinal` 6 个导出字段（GAP:N3-B4 的 cheat 部分）未被消费——变异探针 P3 全部篡改后 16/16 PASS exit 0（§11.8）；其余 17 条 OUT_DIFF = registerDealtRound/addGroup/expiry 的版本链断言（:280-329）。
- **tileMovement**：70 条路径 = IN_BIND 16 + OUT_DIFF 40 + SEM_MAP 1 + UNBOUND 11 + UNBOUND_HC 2（GAP:N3-B1：m1b 交叉绑定 11 叶 + 2 条硬编码叶）。6 caseId 中 5 个行为重放；mv-swap-drawn-with-wall-backfill 结构（§7）。
- **侥幸通过判定（更新）**：ReasonMap :43-78 是完备性断言，但映射正确 ≠ 行为正确；D50 的真实 C# 行为若与 V4 相悖现有断言不设防；6 个 Allowed 主路径 case 风险最高，E（#36）已由探针实证（P2），#23 已证绑定（P1 正控制）。

### 3.5 shuffle-v1（catalog 内，R1-2(b) 修正版）

- **SHA256** `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`；v4Commit `37b4dd61…`（真实）；C# 消费 `ShuffleGoldenTests`（4 Facts 全 PASS）。
- **真实 C# 数学差分（生产算法被调用并逐值对照）**：
  - slotMatrix 16 组逐值 ↔ `ShuffleSlots`（:75，含 DeadWallStart 越界禁令）；
  - demandVectors 6×34 逐值（1e-12）↔ `ShuffleTargets.Demand`（:107）；
  - overlapMatrix 6×6 双精度逐值 ↔ `ShuffleTargets.Overlap`（:121）+ 测试内独立冻结硬值双锚（2/√117、3/√39、4/√52、0、1，:127-131）。
- **fixture 内部结构检查（非生产行为差分）**：`ExportedWallsSatisfyPhysicalLaws`（:160-175）对**导出的墙数组**做 136 唯一/置换/赤五恒 3/恒等律/死山 122..135 隔离检查——没有运行 C# shuffle 生产算法生成墙；`rngGapNote`/D-09 声明的 RNG 差异意味着 V4 种子与 C# 不可能同墙，该声明不等于"该组有生产行为对齐"。identity laws/booleans（:179-184）与 threeCasterChanged（:192）同为 fixture 内部/跨字段检查。
- **已导出未绑定（GAP:N3-B2，1 条路径）**：`$.cases[*].deadWallSegmentEqual`（32 布尔实例）——`deadWallSegmentEqual` 在 ShuffleGoldenTests.cs 中从未被读取；变异探针第 1 例 true→false → 4/4 PASS exit 0（§11.8）。

### 3.6 ai-v1（catalog 外第 6 份，R1-2(c) 修正版 + 第 3 轮 B6 补充）

- **SHA256** `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；v4Commit `205506f0…`（真实，恰为 catalog 冻结头）；7 case；C# 消费 `AiReplayGoldenTests`（3 Facts 全 PASS，经 csproj `<None Include>` 拷贝读取）。
- **真实 C# 行为（保留）**：7/7 case 经 `AiDecisionService.Replay + Submit`（`ThrowingStrategy` 保证绝不运行启发式）：录制意图（recordedIntent 绑定防换动作）、拒绝码（AI-002-old-round/AI-003-old-decision/AI-004-old-epoch/AI-005-wrong-seat/AI-006-candidate-not-legal/AI-007-exact-deadline/AI-001-legal-intent）、v5.authorityMapping 六种 mode 构造校验（输入/配置面绑定）。
- **fixture 内部证据（非 C# 后态差分）**：`AssertV4Evidence` 的 `DeepEquals(before,after)`（:571/:607）比较的是同一 fixture 的 V4 前后树（尤其拒绝 case 的 before==after 期望），不是"C# 重放后实际状态 vs V4 全树"差分；接受 case 仅核若干关系字段（receiptStatus/eventCount/stateChanged）；末 Fact 仅 DeadlineArbiter 接纳动作，不执行 RoundStateMachine 产生 C# 后态。
- **第 3 轮新增未绑定（GAP:N3-B6，3 条路径）**：`$.v5Observation.mappingBasis` 及 `$.v5Observation.mappingLimits[*]`/`[*].#`——观测元数据中声明"映射依据/边界"的字段当前无 C# 断言消费（FIELD-RULES 显式 UNBOUND 规则；含第 3 轮 `[*]` 文法修正前的误配修复）。属索引/文档语义面，补齐包 N3-B6 = 在 `AiReplayGoldenTests` 增加存在+取值断言或在 fixture 导出侧声明排除（caller 裁决，二选一，禁止沉默）。
- **索引缺口与 G-049**：catalog `fixtures[]=5`（notice 明示 five accepted V4-exported fixtures）、manifest 无 ai-v1 行、G-049 `coverage=none` 与仓库现状不符——ai-v1 恰为 recorded-intent + stale-AI submission fixture。但按本节消费边界，G-049 不可重算 full；**建议 partial**（真实行为面存在 + 后态差分缺位）。是否扩口径由 caller 裁决（§9 N3-C1）。

### 3.7 v4Commit 真实性核验（机器可复算，沿用第 1 轮）

| fixture | v4Commit | `git cat-file -t` | 提交日期/主题 |
|---|---|---|---|
| ai-v1 | 205506f07f6a… | commit | 2026-10-24 docs(v5): record independent physical golden acceptance |
| cheat-v1 | a96c195c852e… | commit | 2026-10-23 feat(v5): add AI intent boundary and stale-result checks |
| physical-v1 | 32f645ec9b13… | commit | 2026-10-24 docs(v5): record P3.2c independent and integrated Wire acceptance |
| round-v1 | 21a7d2c49539… | commit | 2026-10-23 feat(v5): port scoring rules and V4 golden differential tests |
| shuffle-v1 | 37b4dd610c5e… | commit | 2026-10-23 docs(v5): detail phases 2 through 5 tasks and gates |
| scoring-v1 | 97831c88f664… | commit | 2026-10-23 feat(v5): freeze Contracts and Wire v1 schema |

### 3.8 exporter 源链完整性（catalog 声明 vs 冻结头仓库实测，沿用第 1 轮）

| fixture | exporter 路径 | catalog `sha256Raw` | 仓库实测 SHA256（`sha256sum`） | 一致 |
|---|---|---|---|---|
| physical-v1 | engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt | `B86286AE5CAE67BCB1E395C48CA755E884439683367D76AABC6711D6A7775EA4` | `b86286ae5cae67bcb1e395c48ca755e884439683367d76aabc6711d6a7775ea4` | ✅ |
| scoring-v1 | engine/src/test/kotlin/com/majiang/rules/V5ScoringGoldenExporterTest.kt | `284E65279640AFD5AE9B06F7FF4ACAA5E2F2A2F0CC732B802110D594027913A6` | `284e65279640afd5ae9b06f7ff4acaa5e2f2a2f0cc732b802110d594027913a6` | ✅ |
| round-v1 | engine/src/test/kotlin/com/majiang/rules/V5RoundGoldenExportTest.kt | `D3CBD13FE7DE38155CD493192EBD1CCA052F643936426AEB519FB3C9AEA3A28B` | `d3cbd13fe7de38155cd493192ebd1cca052f643936426aeb519fb3c9aea3a28b` | ✅ |
| cheat-v1 | engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt | `402AA8EBF03B8E3F2AEB5712A94E7FC1C8508EE327981BC014F7A0A2A67902E4` | `402aa8ebf03b8e3f2aeb5712a94e7fc1c8508ee327981bc014f7a0a2a67902e4` | ✅ |
| shuffle-v1 | engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt | `EF6CC325110F640C372576B07FA4D2C0B5F7B84AA2D69F9E84307A81240A1B3E` | `ef6cc325110f640c372576b07fa4d2c0b5f7b84aa2d69f9e84307a81240a1b3e` | ✅ |
| ai-v1（catalog 外，仅实测存档） | engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt | — | `f70da6a4afbb06fa1bc80f2d0a6573bfb5a63dd302b2ca52122a551d623c76f8` | — |

**判定**：产出五份 catalog fixture 的 exporter 文件与冻结头仓库内现存文件 SHA256 逐一一致（5/5）——fixture 确为仓库内当前 exporter 所声称的代码产物。叠加 §3.1：physical-v1 的 C# 自检链是 CRLF→LF 归一化 SHA 口径，与 catalog raw hash 双链互补、不可互换表述。

---

## 4. 20 项 partial 严格拆分（第 3 轮精确计数版，无 ≈）

分母定义：**"已导出未断言"= fixture 内真实存在、且存在可行 C# 对应断言（或在 D-β 情形需显式映射批准）的字段/case**；无 V4 导出物归"尚未导出"；不可构造或不可机械比较归"不可表示/语义映射"。**D50 与下列 handler 行是同一 case 集的视图关系，不重复计数**（每 case 只有一个桶，见 §3.4 表）。

| ID | owner | 已导出但未断言（精确） | 尚未导出（精确） | 不可表示/语义映射 |
|---|---|---|---|---|
| G-003b | P1.1 | —；**另第 3 轮新增 GAP:N3-E2 24 条路径**（boundaries/boundarySamples/invalid/csharpExceptionMapping，§3.1，单列见 N-NEW-4） | — | probeA/probeB 探测语义无生产对应（结构映射已做）；duplicateKey NOT_CONSTRUCTIBLE |
| G-008 | P1.1 | —（cheat tileMovement 的 2 个 swap case 已重放）；**另 tileMovement 13 条路径 GAP:N3-B1**（N-NEW-5） | draw/discard/pon/kan 的 TileMovement case（五份均无） | — |
| G-013 | P1.2 | — | 恰好 4han/30fu/7700 锚定 case | — |
| G-014 | P1.2 | — | 恰好 28→30 锚定 case | — |
| G-015 | P1.2 | — | 单赤宝牌+指示牌 1/1/2 case | — |
| G-019 | P1.2 | —（42 yaku codes 全数断言） | — | 41/42 役裁决（D-01）未决——规则裁决项，非字段缺口 |
| **G-028a** | P1.3 | **round-v1 字段级 16 条 / census 路径 20 条 UNBOUND_HC**（§3.3 两表，GAP:N3-B4 的 round 部分；含 eventTypes 精确序列×2、语义可比布尔×2） | — | ScoreSettled 表示映射（§6，合法，协议生产者 = P3 TableRuntime 项 N3-D1） |
| G-031 | P1.3 | — | 无事件命令的 event-list/version 断言所需导出物 | — |
| G-032 | P1.4 | — | 单 case 完整 post-state/组牌/位置/deadline | — |
| G-033 | P1.4 | #12 tm-capacity-groups、#13 tm-capacity-tiles（D50 视图） | — | — |
| G-034 | P1.4 | tileMark 未绑定 **13**（#1,#3,#4,#5,#9,#12,#13,#15,#16,#17,#18,#19,#20） | — | #19 Domain 输入不可表示（D-β） |
| G-035 | P1.4 | sleeveBloom 未绑定 **21 = D20 + E1**（#22,#24-31,#35,#39,#40,#41,#42,#46,#47,#48,#50,#51,#52；#36 为 E 桶视图单列）——**第 3 轮移除已绑定 #23 后由 22 → 21** | — | — |
| G-036 | P1.4 | sbo 未重放 **6**（#46,#47,#48,#50,#51,#52） | — | — |
| G-037 | P1.4 | stale/wrong-window/no-offer 未重放视图（#39,#40,#41,#42,#51,#52,#72,#74） | — | — |
| G-038 | P1.4 | beamSwap 未绑定 **17**（#53-59,#63-66,#68,#70,#72,#73,#74,#75），含 Allowed 主路径 5（#53,#63,#66,#73,#75） | — | — |
| G-039 | P1.4 | bm invalid 未重放视图（#68,#70） | — | — |
| G-041 | P1.4 | ledger snapshotRoundTrip 的 restored.Entries 逐项对照缺位（§3.4：fixture 无 golden Entries 数组，需 exporter 扩导出，caller 裁决）；**另 ledger 8 条 outcome/exception 叶 GAP:N3-B5**（N-NEW-7） | round version / mahjong scores / 136-tile 守恒的账本对照导出物 | — |
| G-043 | P1.5 | — | 非法 seat/config 拒绝 case | — |
| G-046 | P1.3 | — | duplicate-open 拒绝、旧 roundId-key open 拒绝 case（现有 9 op 全为成功路径） | — |
| G-048 | P1.3 | — | server.ConnectionControl grace/reclaim（exporter 显式声明不导出，unexportedSamples[3] 在案） | — |

**本审计新增字段级缺口（不属于上述 20 个 catalog partial 行的现有表述，是否并入/新增 catalog 行由 caller 裁决，本审计不改 catalog）**：

| 新增行 | fixture | 精确路径 | 证据 |
|---|---|---|---|
| N-NEW-1 | scoring-v1 | `$.winSamples[*].expected.status` + `$.winSamples[*].input.extraHan`（GAP:N3-E1 之 2 条） | 变异探针 win→noWin → 7/7 PASS（§11.8）；FIELD-RULES UNBOUND 规则 |
| N-NEW-2 | scoring-v1 | `$.noWinSamples[*].input.doraIndicators/uraDoraIndicators/honba/riichiSticks`（GAP:N3-E1 之 4 条） | CompareNoWinSample 只读 status/extraHan（:250）；FIELD-RULES UNBOUND 规则 |
| N-NEW-3 | shuffle-v1 | `$.cases[*].deadWallSegmentEqual`（GAP:N3-B2） | 变异探针 true→false → 4/4 PASS（§11.8） |
| N-NEW-4 | physical-v1 | boundaries[*] 11 + boundarySamples 6 + invalid[*] 3 + csharpExceptionMapping 4（GAP:N3-E2 = 24 条） | FIELD-RULES 显式规则；`physical-v1-full-paths.txt` 逐条可展开 |
| N-NEW-5 | cheat-v1 | tileMovement 13 条（UNBOUND 11 + UNBOUND_HC 2，GAP:N3-B1）+ goldenInputs 6 条（GAP:N3-B3） | FIELD-RULES 显式规则；goldenInputs 引用 0/20·0/23（§11.7） |
| N-NEW-6 | cheat-v1 | tileMarkState 6 字段（GAP:N3-B4 cheat 部分） | 探针 P3 六字段全篡改 → 16/16 PASS（§11.8） |
| N-NEW-7 | cheat-v1 | ledger 4 子键 outcome/exception 8 条（GAP:N3-B5） | 探针 P4 全篡改 → 16/16 PASS（§11.8） |
| N-NEW-8 | ai-v1 | mappingBasis/mappingLimits 3 条（GAP:N3-B6） | FIELD-RULES 显式 UNBOUND 规则（§3.6） |

**20 项 partial 的分子合计（第 3 轮精确）**：已导出未断言 = round-v1 **16 字段级条目（20 条 census 路径）** + cheat-v1 **53 case 级（D50 + B2 + E1；其中 D-α46 可构造、D-β4 需语义映射批准）** + cheat-v1 路径级 33 条（GAP:N3-B1/B3/B4cheat/B5）+ G-041 Entries 对照缺位 1 项；cheat case 级与 handler 分桶行（G-033..039）为视图关系不叠加。新增行 N-NEW-1..8 与 20 行部分重叠处已注明 GAP 包归属，不重复计入分子。其余 partial 缺口全部为"尚未导出"。G-019 是规则裁决（D-01）；G-003b 一半不可表示。

---

## 5. 33 none + 3 v5-only（catalog 机器提取，逐 ID）

**none = 33**：G-004, G-005, G-006, G-007, G-009, G-010, G-011, G-012, G-024, G-028b, G-030, G-040a, G-040b, G-040c, G-044, G-045, G-047, G-049*, G-050a, G-050b, G-050c, G-051, G-052, G-053, G-054, G-056a, G-056b, G-056c, G-056d, G-057a, G-057b, G-057c, G-057d。
\*G-049 按五份口径为 none，但 ai-v1 已存在且 Replay/Submit/拒绝码/动作绑定被真实消费——按 §3.6 消费边界**建议重算 partial（非 full）**，由 caller 裁决（§9 N3-C1）。

**v5-only = 3**：G-055（outbox/RECEIPT_QUERY）、G-058（ownerEpoch/lease/Postgres）、G-059（Protobuf/WSS/TLS/Unity/IL2CPP）——设计上无 V4 fixture，不进 G1 分母。

**目录数字的正确读法**：73 = G 需求行数；10,382 = catalog 五份 fixture 声明 case 数（caseTotal 口径）；42 = 五个 C# 读者类 Fact 数；45 = 42 Domain + 3 Ai。三者互不为分子分母。catalog `fixtureConcreteCases=10382` 与本审计 census 案例口径 10353 的 **Δ=29**（round +23、cheat +6）：catalog fixture 条目只有 `id/path/sha256/bytes/caseTotal` 四字段、未声明案例分解口径，Δ29 作为口径差异如实上报，**不采纳、不改 catalog**（是否声明分解 = §9 N3-C1 的 caller 裁决项）。

---

## 6. 专查一：G-028a 完整事件序列四层追踪（含 R1-1 修正）

**结论：ScoreSettled 语义映射成立、C# 表示合法；真实缺口 = 事件序列精确断言缺失 + 导出值未绑定（含两语义可比布尔）。不强迫 Domain 同名枚举。**

| 层 | 锚点 | 事实 |
|---|---|---|
| ① V4 domain/round | `RoundTransitionPlanner.kt:408/:428/:508/:582/:600` | 和牌结算发射 `GameEvent.ScoreSettled(turnNumber, payments, newScores)`，夹在和牌事件与 RoundEnded 之间；exporter `V5RoundGoldenExportTest.kt:178` 记录类名 → 导出 `[TsumoWin, ScoreSettled, RoundEnded]` |
| ② V4 match | `MatchSession.kt:497-532` | match 层消费 round 结果另发 MatchEnded；ScoreSettled 是 round 层事件，match 层无对应物 |
| ③ 协议（C# 侧冻结契约） | `ProtocolSnapshot.cs:51-53` | `ProtocolEventType.ScoreSettled=17 / RoundEnded=18 / MatchEnded=19` 已冻结 |
| ④ C# Domain/投影 | `RoundTransition.cs:56-74`（RoundEventType 16 值，无 ScoreSettled）；`TableEventProjection.cs:253/:735`；`TableProjectionContractMapper.cs:521-532`；TableRuntime src 无 ScoreSettled 生产者 | C# Domain 把 ScoreSettled 携带的信息放进 `RoundSettlement/RoundResult` 字段（resultFinalScores/resultDeltas 已被断言）——信息不丢，表示不同 |

**判定**：
1. V4 `[TsumoWin, ScoreSettled, RoundEnded]` ↔ C# `[TsumoWin, RoundEnded]` + 结算字段随 Result —— 合法语义映射；强迫 Domain 出现同名枚举是错误要求。
2. 协议层 ScoreSettled=17 的生产者是 P3 TableRuntime 集成项（§9 N3-D1），不属 Domain 黄金补齐范围。
3. **真实缺口（P1.3，`RoundReplayGoldenTests.cs`，GAP:N3-B4 的 round 部分 = 20 条路径 / 16 条目）**：
   - `RoundTerminalReplaysExactly:134` 仅 `Contains(TsumoWin)`——多事件/少事件/错序/假事件全部 PASS；
   - `ChankanRonReplaysExactly` 事件面仅 `:225-226` 两个 `DoesNotContain`——不发任何事件的错误 C# 也能 PASS；
   - `hasKanDeclared/hasRinshanDraw` 导出布尔未绑定（语义可比、名称映射 RinshanDraw→RinshanDrawn；判级按 §1.4 保守原则记 UNBOUND_HC，绑定要求见 N3-A2）。
4. 补齐断言的期望序列**必须由 fixture 投影**（§3.3 投影规则），不得手写另一份预期。

---

## 7. 专查二：mv-swap-drawn-with-wall-backfill（沿用第 1 轮，caller 认可方向）

**结论：非 V4-only，非迁移缺口，无需用户规则裁决；C# 测试内注释与事实不符应更正；剩余为小型交叉绑定补充。**

五层证据链：

1. **V4 法律**：`TileMovement.kt:543-548` — 摸牌实体参与置换时旧墙槽回填。
2. **V4 生产可达性**：`ApplicationCoordinator.kt:1432-1437`（袖里藏花第二选以摸牌实体置换，回填分支生产可达）；`ApplicationCoordinator.kt:2350`（偷梁换柱拒绝摸牌实体）。
3. **V4 导出**：`V5CheatGoldenExporterTest.kt:1091-1124` — m1b 用真实 `drawNormal`+`swapHandWithWall(drawn,1)` 导出。
4. **C# 实现**：`CheatSubmission.cs:376` — `ChooseSleeveBloom(keep=Second, Normal)` 以 drawnTile 调 `map.SwapHandWithWall`；`TilePositionMap.cs:1145-1160` 原语原生支持 `replacesDrawn`。
5. **C# 拒绝门**：`CheatSubmission.cs:1003-1006` — ChooseBeamSwap 拒绝 drawn 实体，对齐 V4 偷梁换柱生产 :2350。

**新发现（维持）**：`CheatGoldenTests.cs:638-642` 注释称该法律"在 C# 侧无入口"——后半句错：入口是 `ChooseSleeveBloom:376`，且 `SleeveBloomSecondChoice_BindsToGoldenInputs:748-757` 已行为重放摸牌实体置换。

**剩余真实缺口**：N3-B1（tileMovement 13 条路径：m1b 三元组交叉绑定 + 2 条硬编码叶）、N3-B2（注释更正）、N3-B1 附带的 INFO-2 地址落点表示差（不可表示，记录即可）。

---

## 8. 专查三：AgariYame / ChooseDealerEnd（沿用第 1 轮）

| 侧 | 锚点 | 事实 |
|---|---|---|
| V4 | `MatchSession.kt:82,497-532`；`GameAction.DeclareAgariYame`（ApplicationCoordinator :3268） | `ApprovedSessionStep.ChooseDealerEnd(agariYame=true)` → `MatchFinishReason.AGARI_YAME` → SessionResult + MatchEnded |
| C# | `MatchSession.cs:498` `ChooseDealerEnd(bool agariYame, …)`；:212 `CheckAgariYameEligible`；:536 `MatchFinishReason.AgariYame` | 行为完整存在 |

matchProgression 16 case 全字段消费含 `allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame`。**判定**：G-027 行为 full 成立；round 层命令入口枚举差只是命令入口面 gap，不得据此断言"缺全部 agari-yame 行为"。

---

## 9. 最小补齐包（可独立下发）与依赖 DAG（第 3 轮：覆盖全部 87 条 GAP 路径）

### 补齐项（全部为测试/文档/索引文件，不触生产代码、不改 fixtures、不改 catalog 冻结数字以外的语义）

每包的机器分母 = §3.0.1 FIELD-RULES 块中 owner 列为对应 `GAP:N3-*` 的路径数；均可由 §11.6 复算命令按列筛选核对。

| ID | 文件 | 内容（断言要求逐项） | 依赖 | 对应缺口（GAP 路径数） |
|---|---|---|---|---|
| N3-A1 | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs` | RoundTerminal：(a) 事件序列 = fixture `eventTypes=[TsumoWin,ScoreSettled,RoundEnded]` 经 §3.3 投影规则 → `Assert.Equal(["TsumoWin","RoundEnded"])`，注释引用 fixture 原序列与投影规则；(b) 绑定 `input.phaseBefore/versionBefore`（替换硬编码 :110-113）；(c) 绑定 `settlement.outcome/totalDeltas`（:117/:131）；(d) 绑定 `expected.phaseAfter/resultOutcome`（:129/:130 改读导出值） | 无 | GAP:N3-B4 round 部分 9 条路径 |
| N3-A2 | 同上 | ChankanRon：(a) `Assert.Equal(["RonWin","RoundEnded"])`（投影注释同上）；(b) **绑定 `expected.hasKanDeclared/hasRinshanDraw`**：`Assert.Equal(导出布尔, 终局事件列表.Contains("KanDeclared"/"RinshanDrawn"))`，行注释仅作名称映射说明，不以注释豁免绑定；(c) 绑定 `meldTypeAfterKan/legalRonSeat1/input.meldTiles/phaseAfterKan/phaseAfterRon/resultOutcome`（替换 :221/:227 硬编码） | 无 | GAP:N3-B4 round 部分 11 条路径 |
| N3-B1 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | tileMovement 13 条路径绑定：m1b 黄金字段（backfilledIndex/resolvedIndex/pointer 三元）与 `SleeveBloomSecondChoice` 重放交叉绑定（11 条 UNBOUND）+ 2 条硬编码叶改读导出值（2 条 UNBOUND_HC） | 无 | GAP:N3-B1 = 13 |
| N3-B2 | 同上 | 更正 :638-642 注释为准确映射（ChooseSleeveBloom:376 为入口；ChooseBeamSwap 拒绝=V4 生产对齐；地址落点差按 INFO-2 记录为表示差） | 无 | §7（注释，非路径分母） |
| N3-B3 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`（仅测试） | **handler 全清单补齐包**，覆盖 §3.4 表全部非 A/C 桶：(a) **D-α 46 例**：真实入口行为重放 + outcome/reason/requestException 逐值绑定——**构造输入不得声称"复用现有 goldenInputs"**（引用实测 tileMark 0/20、beamSwap 0/23），须按 V4 exporter 源锚点（`V5CheatGoldenExporterTest.kt` 对应 case 构造段）重建输入并留锚点注释；Allowed 主路径 6 例（#1/#53/#63/#66/#73/#75）另绑副作用/事件形状（tileMarkState/tileMovement/offer 状态相关节）；(b) **D-β 4 例**（#5/#18/#19/#20）：#19 在岗 deadline 在 `CheatTurnContext`（:12-25）无字段、C# DEADLINE_EXPIRED 仅 off-turn（:553-560），以等价类断言 + 映射注释 + **owner 批准记录**落地；#5/#18/#20 绑定 InvalidRequest 合并语义实际 outcome + 注释；(c) **B 2 例升级**：#6 绑定 C# 特化 ResponseWindowOpen 错误码逐值、#67 升级全链路重放；(d) **E 1 例绑定**：#36 sb-choose-first 以 fixture goldenInputs 构造并绑定 outcome=Allowed/reason=null。同时绑定两未引用节（tileMark/beamSwap）的 goldenInputs 树 6 条路径（断言构造输入与 fixture 逐值一致）。验收：16 Facts 保持 PASS，新增断言后 Total 增加且 0 FAIL；负例矩阵见 §13 DEF-2 | 无 | GAP:N3-B3 = 6（goldenInputs 树）+ D50/B2/E1（case 级） |
| N3-B4 | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`（round 20 条）+ `CheatGoldenTests.cs`（tileMarkState 6 条） | round 20 条路径的断言明细 = N3-A1/A2；**tileMarkState 6 字段绑定**：`versionAfterFirstRegister`、`sameIdDifferentContentException`、`seatGroupLimitIsThree`、`seatGroupLimitException`、`seatTileLimitIsTwelve`、`createdRoundOrdinal` 改读导出值（替换/补足 :280-329 版本链断言未覆盖处） | 无（A1/A2 与本包 round 部分为同一工作的两个视图） | GAP:N3-B4 = 26（round 20 + tileMarkState 6） |
| N3-B5 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | ledger 4 子键（conflictDifferentPayload/insufficientFunds/overflow/invalidSeat）的 `outcome`/`exception` 导出叶逐值绑定：C# 实算结果（rejected/accepted + 具体异常类）与导出值对照（探针 P4 证明当前不设防） | 无 | GAP:N3-B5 = 8 |
| N3-B6 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` 或 fixture 导出侧（caller 裁决二选一） | `$.v5Observation.mappingBasis`/`mappingLimits` 3 条路径：绑定（存在+取值断言）或导出侧显式排除声明——禁止沉默 | 无 | GAP:N3-B6 = 3 |
| N3-C1 | `tests/v5-golden/catalog-v1.json` + `V5-0-GOLDEN-MANIFEST.md` | **caller 口径决定**：ai-v1 纳入 fixtures[]/manifest 并把 G-049 重算 partial（§3.6）；catalog caseTotal 口径 Δ29 是否声明分解（§5） | caller 裁决 | §3.6 / §5 / G-049 |
| N3-E1 | `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` | (a) 绑定 `winSamples[*].expected.status` = C# 重放实算 status；(b) 绑定 `winSamples[*].input.extraHan`（win 侧现无断言，noWin 侧 :250 已有）；(c) 绑定 `noWinSamples[*]` 的 `input.doraIndicators/uraDoraIndicators/honba/riichiSticks` 4 字段；(d) `winSamples[*].tags/tags[*]` 以**显式排除声明**处理（exporter 元数据：caller 认为须绑定则并入，否则测试内加显式注释使分母可见——禁止沉默） | 无 | GAP:N3-E1 = 6 + tags 显式排除（META 10 条不进 GAP 分母） |
| N3-E2 | `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` | 24 条路径绑定：`expected.boundaries[*]`（11 条：越界样本逐值对照）、`expected.boundarySamples`（6 条）、`expected.invalid`（3 条：非法输入判定对照）、`expected.csharpExceptionMapping`（4 条：异常类型映射断言） | 无 | GAP:N3-E2 = 24 |
| N3-D1 | （P3 范围，非本包） | TableRuntime ScoreSettled 协议事件生产者 | P3.x | §6 判定 2 |

### 依赖 DAG

```
N3-A1 ─┐
N3-A2 ─┴─→ G-028a 关闭路径（P1.3, RoundReplayGoldenTests）──┐
N3-B1 ─┐                                                    │
N3-B2 ─┴─→ mv-swap 定性修正落地（P1.4, CheatGoldenTests）──┤
N3-B3 ────→ cheat handler case 级 + goldenInputs（P1.4）───┤
N3-B4 ────→ round 路径视图 + tileMarkState 6（P1.3/P1.4）──┼─→ N-3 [MAJOR] 子项证据齐备
N3-B5 ────→ ledger 4 子键 outcome/exception（P1.4）────────┤
N3-B6 ────→ ai mappingBasis/Limits（P1.5）─────────────────┤
N3-E1 ────→ scoring 6 条路径 + tags 排除声明（P1.2）───────┤
N3-E2 ────→ physical 24 条路径（P1.1）─────────────────────┤
                                                            │
N3-C1 ←─ caller 口径裁决（G-049 / caseTotal 口径）─────────┤
                                                            │
N3-D1（P3 TableRuntime，独立轨）───────────────────────────┴─→ G1 门的字段级证据基础
```

所有 N3-A*/B*/E* 互不依赖、可并行；N3-C1 是索引口径决定非代码；N3-D1 与 G1 无前置关系。**本包覆盖本报告所报全部可比较缺口（GAP 87 条路径逐一映射如上）**；唯一"不直接绑定"项 = D-β #19 的在岗 deadline 表示（等价类 + 批准）、tags 元数据（显式排除声明）、ledger Entries 级对照（fixture 无 golden Entries 数组，需 exporter 扩导出——属 catalog 语义变更，caller 裁决）。三者均显式留痕，无沉默移出分母。

---

## 10. 侥幸通过（wrong-value-passes）判定汇总（9 探针 + 2 正控制实证）

| # | 位置 | 错值场景 | 现有断言是否设防 | 实证（轮次/探针） |
|---|---|---|---|---|
| 1 | RoundTerminalReplaysExactly :134 | 事件序列多/少/错序/假事件 | **不设防**（仅 Contains TsumoWin） | 静态读源 + §13 DEF-1 负例矩阵（第 2 轮） |
| 2 | ChankanRonReplaysExactly :225-226 | 和牌后完全不发事件 | **不设防** | 同上（第 2 轮） |
| 3 | chankanRon hasKanDeclared/hasRinshanDraw | 导出双布尔改 true | **不设防** | 探针 round-flags-mutated-true：6/6 PASS exit 0（第 2 轮） |
| 4 | winSamples[*].expected.status | 首例 win→noWin | **不设防** | 探针 scoring-win-status-mutated：7/7 PASS exit 0（第 2 轮） |
| 5 | winSamples[*].tags/tags[*] | 元数据注入 | **不设防**（未读） | 源码 0 次引用（第 2 轮） |
| 6 | cases[*].deadWallSegmentEqual | 首例 true→false | **不设防** | 探针 shuffle-deadwall-flag-mutated：4/4 PASS exit 0（第 2 轮） |
| 7 | sb-choose-first（E 桶） | outcome→Rejected + reason→WRONG_PHASE | **不设防** | 第 2 轮探针 cheat-choose-first-mutated-rejected：16/16 PASS；**第 3 轮探针 P2 独立复证** 16/16 PASS exit 0 |
| 8 | cheat D50（含 6 个 Allowed 主路径） | 真实 C# 行为与 V4 相悖（同 reason 不同副作用/事件形状） | **不设防** | §3.4 分桶 + §13 DEF-2 负例矩阵 |
| 9 | tileMarkState 6 字段（versionAfterFirstRegister 等） | 六字段全部篡改 | **不设防** | **第 3 轮探针 P3** tilemark-state-fields-unbound：16/16 PASS exit 0 |
| 10 | ledger 4 子键 outcome/exception | rejected→accepted + 异常类→NOT_AN_EXCEPTION | **不设防** | **第 3 轮探针 P4** ledger-markers-unbound：16/16 PASS exit 0 |
| 11 | **正控制①**：已绑定 totalPoints | 2000→123456789 | **设防（1 FAIL exit 1）** | 第 2 轮探针 positive-control：Total:7, Failed:1 |
| 12 | **正控制②**：sb-activate-already-used（A 桶 #23） | reason→WRONG_PHASE | **设防（1 FAIL exit 1）** | **第 3 轮探针 P1**：`Handler_SleeveBloom_RepresentativeCases_ReplayAgainstCheatSubmission [FAIL]`（Assert.Equal Failure @ CheatGoldenTests.cs:557）——证明 :486-490 ReasonMap 绑定真实存在，#23 = A 桶 |
| 13 | physical samples/scope/hashCrosswalk、scoring 其余绑定组、shuffle 三矩阵、round version/scores/deltas/rejected、ledger 差分子集、tileMarkState 版本链 17 条 | 数值/结构错值 | 设防（逐值断言 + 分布对账 + 冻结硬值双锚，§3 逐字段表） | 45/45 PASS 维持 |

---

## 11. 可复算命令与证据（完整命令、native exit、全 SHA256；无省略号）

> 约定：所有哈希除特别注明 **Git blob id**（`git hash-object`，SHA-1 算法，不可与 SHA256 互换）外均为 **SHA256**（`sha256sum`，小写十六进制）。第 3 轮全部新证据在独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/`（下称 R3）；第 2 轮 census 在 `C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/`（下称 R2）；**§11.10 的自抽取闭环在全新空 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-extract-loop/`（下称 LOOP）**——全部工具从本报告代码块重新提取后重建复算，证明"报告即工具源"。

### 11.1 构建（Release；第 3 轮返修开工时全量重建，日志在 R3）

```
cd D:/majiang-wt/n3-golden-field-audit
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/build-domain.log 2>&1
→ EXIT=0（build-domain.log 尾部 = 0 个警告 0 个错误；SHA256 8ee3b3a7c3bb2f85022c834c4cc6cfb1826dce55ad8df20d527589120ec5bebb）
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/build-ai.log 2>&1
→ EXIT=0（build-ai.log 尾部 = 0 个警告 0 个错误；SHA256 4704945405bd920ffb2f077f836e9552277b66dd2cbbeab54d97a14dbfee645f）
```

### 11.2 定向 Golden 运行（完整命令，native exit；沿用第 2 轮运行记录，日志在 R2）

```
cd D:/majiang-wt/n3-golden-field-audit
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.PhysicalGoldenTests -noColor -noLogo
→ EXIT=0；Total: 9, Failed: 0, Skipped: 0；日志 SHA256 93B00464631947058E76E76E217CC31E2211CBFA8DA6B717CB0B9DF36A1ACF4B
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.ScoringGoldenTests -noColor -noLogo
→ EXIT=0；Total: 7, Failed: 0；日志 SHA256 C1B7A66626F328DA1711D183D97562B1FDE63DDE1647D6AADA6EA67C6F44FCE5
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.RoundReplayGoldenTests -noColor -noLogo
→ EXIT=0；Total: 6, Failed: 0；日志 SHA256 B83631131381FDE2266E8C20733EA67521B1A9AD03F086B27487F07E06694D8E
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo
→ EXIT=0；Total: 16, Failed: 0；日志 SHA256 4783C3761FF0F2360B626A1B03B317DC43ACBD7BABAFCD8F3CDFE95B7D721AF4
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.ShuffleGoldenTests -noColor -noLogo
→ EXIT=0；Total: 4, Failed: 0；日志 SHA256 94238F9CD8919AC573F159BFC08A34E0FDD5F7CCB0D825C0FBECA636BF659F15
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiReplayGoldenTests -noColor -noLogo
→ EXIT=0；Total: 3, Failed: 0；日志 SHA256 91CC7A2918DB3E49119196F28979FBD52D23B115B9980576539FA89D420829B9
```

合计 45/45 PASS，无 skip、无空测试、无断言弱化操作。

### 11.3 第 1 轮枚举工件（历史证据，保留不覆盖；TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-field-audit-01edf75dbc3b/`；第 3 轮返修时对该 TEMP 逐文件 `sha256sum` 重测，与下表全部一致）

| 工件 | SHA256 |
|---|---|
| walk-fixtures.ps1 | `1F449B61454EE4F07DA699299B71AEA073581CE213EF771017982C5DFF41B4F7` |
| aggregate-leaves.ps1 | `E37049DD05E3248CB95779A672D8D8EDD3269AD5E6322909EC006EA77561ED91` |
| scoring-shape.ps1（首中末抽样——已被 §1.1 判不充分，仅存档） | `3C9C111741FEED7CF1EBC14BBCAF86A92A32C33959A17C8E2274B43B8B3F44AB` |
| physical-v1-leaves.txt / -agg | `C921C12D2BFC487B578DAB95A4969204F016EB7E4B1BE1D699379860631659CD` / `D0664CEC8A2378096EE80A27C3B1D73632660D1C3437D1B68440099D5EF17E6E` |
| scoring-shape.txt | `A9A2D122FC3422EE25A33A711426F9FD93CE2758AE4820BC829D417D3C3AAD35` |
| round-v1-leaves.txt / -agg | `7C3BC02D85194560455EDD74AE24F4A29DB9C88357929BBF70E125DCB9BD7351` / `0B9A9D5E4B4DA7AA44B9F62BCCFAF8DDF520074BBA65FCDF9DFDC07859368541` |
| cheat-v1-leaves.txt / -agg | `87E4FA363FA5F9F2C398D614BC8DA9CFA9E3BEBBF30606E9B192E2A8749413CE` / `6F84A0972CBC7ACFBE097AE709661CE38E1F0A7FD8AF2E8900BBE0368758AEA9` |
| shuffle-v1-leaves.txt / -agg | `3B5C916D3FB2BF7AF38D139EB2DBCB20A03D360032D278F0D7F3835317D6185B` / `BD39C67945CDB6D4DF536CD8738A1996B8AB05F687178AD18936122088237A18` |
| ai-v1-leaves.txt / -agg | `F10407306769BDC56C1E592CDD533D792B4BF15C3BD4BC786983BC20605D8530` / `9FAFD588C66441643408429309F83621789EAE708FA6705FFAE0D5D2326088` |

（第 1 轮工件仅作历史对照，本报告全部结论以 §11.4 census 与 §11.5-§11.8 第 3 轮复算件为准。）

### 11.4 全量 census（工具源内嵌 + 完整命令；census 在 R2 产出，LOOP 在 §11.10 复算一致）

工具源码全文内嵌如下两块（提取命令见 §11.10；R2 实测 SHA：csproj `8f946bc6c411cc89c659c88eeb89ddeaeb52cb54b0d480e58e09ff418e29168d`、Program.cs `fd49db41b5dbb6602b7ce1a2e9a52b679f78b700dc1d93606d8f12f8f530b4d0`——§11.10 从本报告提取后重测须逐一相等）：

```xml
<!--TOOL:fullcensus-csproj:BEGIN-->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>FullCensus</AssemblyName>
    <RootNamespace>FullCensus</RootNamespace>
  </PropertyGroup>
</Project>
<!--TOOL:fullcensus-csproj:END-->
```

`FullCensus/Program.cs`（递归枚举全部 JSON 节点；归一化 `[N]→[*]`；口径定义见 §1.2）：

```csharp
<!--TOOL:fullcensus-program:BEGIN-->
// FullCensus — N-3 golden field audit round-2 rework evidence tool.
// Exhaustively enumerates EVERY JSON node of every golden fixture (no sampling):
// normalized logical paths (arrays collapsed to [*]), observed kinds per path,
// instance counts, array element sums and length ranges. Emits per fixture:
//   <fixture>-full-paths.txt     one line per distinct normalized path
//   <fixture>-group-counts.txt   mechanical group (prefix through first "[*]",
//                                else the whole path) -> path/leaf counts
//   census-summary.txt           per-fixture totals
// Exit codes: 0 = all fixtures enumerated; 2 = usage; 3 = missing fixture file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FullCensus
{
    internal static class Program
    {
        sealed class PathInfo
        {
            public SortedSet<string> Kinds = new SortedSet<string>(StringComparer.Ordinal);
            public long InstanceCount;
            public long LeafInstances;
            public long ElementSum;
            public long LenMin = long.MaxValue, LenMax = long.MinValue;
        }

        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: FullCensus <repoRoot> <outDir>");
                return 2;
            }
            string repoRoot = args[0];
            string outDir = args[1];
            Directory.CreateDirectory(outDir);

            string[] fixtures = { "physical-v1", "scoring-v1", "round-v1", "cheat-v1", "shuffle-v1", "ai-v1" };
            var summary = new List<string>();
            int failures = 0;

            foreach (string name in fixtures)
            {
                string file = Path.Combine(repoRoot, "tests", "v5-golden", name + ".json");
                if (!File.Exists(file))
                {
                    summary.Add("MISSING " + name);
                    failures++;
                    continue;
                }

                var paths = new SortedDictionary<string, PathInfo>(StringComparer.Ordinal);
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    Walk(doc.RootElement, "$", paths);
                }

                var pathLines = new List<string>();
                foreach (var kv in paths)
                {
                    string kinds = string.Join("|", kv.Value.Kinds);
                    string extra = kv.Value.LenMin != long.MaxValue
                        ? " elements=" + kv.Value.ElementSum + " lenMin=" + kv.Value.LenMin + " lenMax=" + kv.Value.LenMax
                        : "";
                    pathLines.Add(kv.Key + " " + kinds + " instances=" + kv.Value.InstanceCount + extra);
                }
                File.WriteAllLines(Path.Combine(outDir, name + "-full-paths.txt"), pathLines, new UTF8Encoding(false));

                var groups = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
                foreach (var kv in paths)
                {
                    string g = GroupOf(kv.Key);
                    if (!groups.TryGetValue(g, out long[] a)) a = new long[4]; // distinct, leafPaths, leafInstances, instances
                    a[0]++;
                    if (!kv.Value.Kinds.Contains("object") && !kv.Value.Kinds.Contains("array")) a[1]++;
                    a[2] += kv.Value.LeafInstances;
                    a[3] += kv.Value.InstanceCount;
                    groups[g] = a;
                }
                var groupLines = new List<string> { "group\tdistinctPaths\tdistinctLeafPaths\tleafInstances\tinstances" };
                long totalPaths = 0, totalLeafPaths = 0, totalLeafInstances = 0;
                foreach (var kv in groups)
                {
                    groupLines.Add(kv.Key + "\t" + kv.Value[0] + "\t" + kv.Value[1] + "\t" + kv.Value[2] + "\t" + kv.Value[3]);
                    totalPaths += kv.Value[0];
                    totalLeafPaths += kv.Value[1];
                    totalLeafInstances += kv.Value[2];
                }
                groupLines.Add("TOTAL\t" + totalPaths + "\t" + totalLeafPaths + "\t" + totalLeafInstances + "\t-");
                File.WriteAllLines(Path.Combine(outDir, name + "-group-counts.txt"), groupLines, new UTF8Encoding(false));
                summary.Add(name + ": distinctPaths=" + totalPaths + " distinctLeafPaths=" + totalLeafPaths
                    + " leafInstances=" + totalLeafInstances + " groups=" + groups.Count);
            }

            File.WriteAllLines(Path.Combine(outDir, "census-summary.txt"), summary, new UTF8Encoding(false));
            foreach (string s in summary) Console.WriteLine(s);
            Console.WriteLine("FULL-CENSUS-DONE failures=" + failures);
            return failures == 0 ? 0 : 3;
        }

        static string GroupOf(string path)
        {
            int i = path.IndexOf("[*]", StringComparison.Ordinal);
            return i < 0 ? path : path.Substring(0, i + 3);
        }

        static void Walk(JsonElement e, string path, SortedDictionary<string, PathInfo> paths)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var o = Get(paths, path);
                    o.Kinds.Add("object");
                    o.InstanceCount++;
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, path + "." + p.Name, paths);
                    break;
                case JsonValueKind.Array:
                    var a = Get(paths, path);
                    a.Kinds.Add("array");
                    a.InstanceCount++;
                    long n = 0;
                    foreach (var item in e.EnumerateArray())
                    {
                        Walk(item, path + "[*]", paths);
                        n++;
                    }
                    a.ElementSum += n;
                    if (n < a.LenMin) a.LenMin = n;
                    if (n > a.LenMax) a.LenMax = n;
                    break;
                case JsonValueKind.String:
                    Leaf(paths, path, "string");
                    break;
                case JsonValueKind.Number:
                    Leaf(paths, path, "number");
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    Leaf(paths, path, "boolean");
                    break;
                case JsonValueKind.Null:
                    Leaf(paths, path, "null");
                    break;
            }
        }

        static void Leaf(SortedDictionary<string, PathInfo> paths, string path, string kind)
        {
            PathInfo info = Get(paths, path);
            info.Kinds.Add(kind);
            info.InstanceCount++;
            info.LeafInstances++;
        }

        static PathInfo Get(SortedDictionary<string, PathInfo> paths, string path)
        {
            if (!paths.TryGetValue(path, out PathInfo info))
            {
                info = new PathInfo();
                paths[path] = info;
            }
            return info;
        }
    }
}
<!--TOOL:fullcensus-program:END-->
```

**census 完整命令（R2 实测；LOOP 复算见 §11.10，产物 SHA 须与下表逐一相等）**：

```
D:/Unity/dotnet/dotnet.exe run --project C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/FullCensus/FullCensus.csproj -- D:/majiang-wt/n3-golden-field-audit C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out/census-run.log 2>&1
→ EXIT=0；日志尾部 = FULL-CENSUS-DONE failures=0；census-run.log SHA256 72c78ea2284e26f12d6828f693798b0f8b8b2d76c5fb285d7ff28ed8d7b86c7d
```

输出（`census-out/`，`census-summary.txt` SHA256 `7b8909f2a2a0c4dc4f4dff068fafb1e19762ed86d0af005d2eb739ea98041504`；全文 6 行 distinctPaths/distinctLeafPaths/leafInstances/groups 见 §1.2 表）：

| 文件 | SHA256 |
|---|---|
| physical-v1-full-paths.txt（296 路径） | `db9f3db65ee3278783ad132accf1993ef5fc4930ad2e214c690cd2b788f487be` |
| scoring-v1-full-paths.txt（419） | `d84b7e1fddf616d462d0226f5a44ad815e8f1b18b80e827a5c99084afc6ebe35` |
| round-v1-full-paths.txt（200） | `e646e083f04b9822fdf6557335ddf921bb98231e493a2a738f0fdaab701931ff` |
| cheat-v1-full-paths.txt（204） | `391966d9479bd29936f1ff842026e2a81c921d05850d3634726243c597ff3081` |
| shuffle-v1-full-paths.txt（199） | `5c0ada4449ec0b295e1541a38172502d17318c12254300b53e783824bea6f30c` |
| ai-v1-full-paths.txt（198） | `ff1b9f56d3ef842d66c442ace771e5fc77ad85acdcaf6a25f0b6fefc2c0b6138` |
| physical-v1-group-counts.txt | `404757d80217a5d9797b0505c900f4b5f5cd5ddafcef29646350326e50f64e76` |
| scoring-v1-group-counts.txt | `22eb15448368073d6649dccd830bbca38c17151d7bdbbe06a3fcb72bceee1cf5` |
| round-v1-group-counts.txt | `5ae03e742a1b507079f0ba86fcaeff5072700081a4726b9fbb3419f866c5587f` |
| cheat-v1-group-counts.txt | `e133b039c2f9f8e25cf4ccbe3cb99235b48d578db98427c05073cdc4bc93328b` |
| shuffle-v1-group-counts.txt | `5c93bd811b839da3913881f149e185b8a28afd82e37f9817395d18aaeefb6015` |
| ai-v1-group-counts.txt | `d29988b85ff35a8926b92cf91031edc5a8f568f66a337e59e0558e3f3239ae1c` |

**第 1 轮漏检 14 路径的复核命令（可在任意 census 产物上复算）**：

```
grep -F -e '$.winSamples[*].input.melds[*]' -e '$.winSamples[*].input.melds[*].calledTile' -e '$.winSamples[*].input.melds[*].fromPlayer' -e '$.winSamples[*].input.melds[*].tiles' -e '$.winSamples[*].input.melds[*].tiles[*]' -e '$.winSamples[*].input.melds[*].type' -e '$.winSamples[*].input.doraIndicators[*]' -e '$.winSamples[*].input.uraDoraIndicators[*]' -e '$.winSamples[*].expected.dora.indicatorTiles[*]' -e '$.winSamples[*].expected.dora.uraTiles[*]' -e '$.winSamples[*].expected.dora.redTiles[*]' -e '$.doraSamples[*].expected.uraTiles[*]' -e '$.doraSamples[*].input.uraDoraIndicators[*]' -e '$.randomSamples[*].ura[*]' C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out/scoring-v1-full-paths.txt
→ 输出恰为上述 14 行（含 $.randomSamples[*].ura[*] number instances=1208）
```

### 11.5 层 1 覆盖校验 verify-coverage（工具源内嵌；R2-2 验收：命令 exit 0 + 全日志路径/SHA）

工具源码全文内嵌如下（R3 实测 SHA `f932794ee00db2c8cea4a67b15e4ec640bdc933f890a0240c3091a59e22d3997`；提取命令见 §11.10）：

```powershell
<!--TOOL:verify-coverage:BEGIN-->
# Coverage mapping tool for the N-3 round-2 audit report.
# emit   : compute prefix-2 logical-path groups per fixture from the full census
#          and write <fixture>-prefix2.txt blocks (complete partition of all paths).
# verify : recompute the groups and diff them against the fenced blocks embedded
#          in the audit report between COVERAGE:BEGIN(<fixture>) / COVERAGE:END
#          markers. Exit 0 = report mapping covers 100% of enumerated paths.
param([string]$Mode, [string]$CensusDir, [string]$ReportPath, [string]$OutDir,
      [string]$Repo, [string]$Evidence)
# -Repo/-Evidence give every report-embedded tool the same invocation surface:
#   -Evidence <dir>  evidence root; CensusDir defaults to <dir>\census-out and
#                    OutDir defaults to <dir>\coverage-out
#   -Repo <path>     audit worktree root (accepted for uniformity; this checker
#                    consumes only census output, never the repo itself)
$ErrorActionPreference = 'Stop'
if (-not $CensusDir -and $Evidence) { $CensusDir = Join-Path $Evidence 'census-out' }
if (-not $OutDir -and $Evidence) { $OutDir = Join-Path $Evidence 'coverage-out' }
$fixtures = @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')

function Prefix2([string]$path) {
  if (-not $path.StartsWith('$.', [StringComparison]::Ordinal)) { return $path }
  $parts = $path.Substring(2) -split '\.'
  if ($parts.Count -le 1) { return $path }
  return '$.' + $parts[0]
}

function Compute([string]$fixture) {
  $file = Join-Path $CensusDir ($fixture + '-full-paths.txt')
  $acc = @{}
  foreach ($line in Get-Content $file) {
    $p = ($line -split ' ')[0]
    $g = Prefix2 $p
    if (-not $acc.ContainsKey($g)) { $acc[$g] = 0 }
    $acc[$g]++
  }
  return $acc
}

if ($Mode -eq 'emit') {
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  foreach ($f in $fixtures) {
    $acc = Compute $f
    $lines = @('COVERAGE:BEGIN(' + $f + ')')
    foreach ($k in ($acc.Keys | Sort-Object)) { $lines += ($k + ' = ' + $acc[$k]) }
    $lines += 'COVERAGE:END'
    $lines | Set-Content -Encoding UTF8 (Join-Path $OutDir ($f + '-prefix2.txt'))
    Write-Output ('EMIT ' + $f + ' groups=' + $acc.Count)
  }
  exit 0
}

if ($Mode -eq 'verify') {
  $report = Get-Content -Raw -Encoding UTF8 $ReportPath
  $fail = 0
  foreach ($f in $fixtures) {
    $begin = 'COVERAGE:BEGIN(' + $f + ')'
    $i = $report.IndexOf($begin, [StringComparison]::Ordinal)
    if ($i -lt 0) { Write-Output ('FAIL marker missing for ' + $f); $fail++; continue }
    $j = $report.IndexOf('COVERAGE:END', $i, [StringComparison]::Ordinal)
    if ($j -lt 0) { Write-Output ('FAIL end marker missing for ' + $f); $fail++; continue }
    $block = $report.Substring($i + $begin.Length, $j - $i - $begin.Length)
    $reportMap = @{}
    foreach ($line in ($block -split "`n")) {
      $t = $line.Trim()
      if ($t.StartsWith('$', [StringComparison]::Ordinal) -and $t.Contains(' = ')) {
        $kv = $t -split ' = '
        $reportMap[$kv[0]] = [int]$kv[1]
      }
    }
    $acc = Compute $f
    $missing = 0; $wrong = 0
    foreach ($k in $acc.Keys) {
      if (-not $reportMap.ContainsKey($k)) { Write-Output ('FAIL ' + $f + ' unmapped group: ' + $k); $missing++ }
      elseif ($reportMap[$k] -ne $acc[$k]) { Write-Output ('FAIL ' + $f + ' count mismatch ' + $k + ': report=' + $reportMap[$k] + ' census=' + $acc[$k]); $wrong++ }
    }
    $extra = 0
    foreach ($k in $reportMap.Keys) { if (-not $acc.ContainsKey($k)) { Write-Output ('FAIL ' + $f + ' extra group: ' + $k); $extra++ } }
    Write-Output ('VERIFY ' + $f + ' groups=' + $acc.Count + ' unmapped=' + $missing + ' mismatched=' + $wrong + ' extra=' + $extra)
    if ($missing + $wrong + $extra -gt 0) { $fail++ }
  }
  if ($fail -eq 0) { Write-Output 'COVERAGE-VERIFY-OK unmapped-total=0' } else { Write-Output ('COVERAGE-VERIFY-FAILED blocks=' + $fail) }
  if ($fail -eq 0) { exit 0 } else { exit 1 }
}
Write-Error 'usage: verify-coverage.ps1 -Mode emit|verify -CensusDir <dir> [-ReportPath <file>] [-OutDir <dir>]'
exit 2
<!--TOOL:verify-coverage:END-->
```

**verify-coverage 完整命令（-File 形式；emit 先行产出 prefix-2 划分，verify 对本报告 §3.0 逐组 diff）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-coverage.ps1 -Mode emit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3 -Repo D:/majiang-wt/n3-golden-field-audit 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/coverage-emit.log 2>&1
→ EXIT=0（EMIT physical-v1 groups=28 / scoring-v1 groups=27 / round-v1 groups=22 / cheat-v1 groups=21 / shuffle-v1 groups=19 / ai-v1 groups=31）
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-coverage.ps1 -Mode verify -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3 -ReportPath D:/majiang-wt/n3-golden-field-audit/doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/coverage-verify.log 2>&1
→ EXIT=0；实测输出：
VERIFY physical-v1 groups=28 unmapped=0 mismatched=0 extra=0
VERIFY scoring-v1 groups=27 unmapped=0 mismatched=0 extra=0
VERIFY round-v1 groups=22 unmapped=0 mismatched=0 extra=0
VERIFY cheat-v1 groups=21 unmapped=0 mismatched=0 extra=0
VERIFY shuffle-v1 groups=19 unmapped=0 mismatched=0 extra=0
VERIFY ai-v1 groups=31 unmapped=0 mismatched=0 extra=0
COVERAGE-VERIFY-OK unmapped-total=0
```

### 11.6 层 2 语义映射校验 verify-mapping（工具源内嵌；R2-1a/R2-2 验收：正检 exit 0 + 三负例全部检出）

工具源码全文内嵌如下（R3 实测 SHA `1ce17dac4f663714492315d30f85c173b0e4ef0506f4bdf0175a03bda5b793c3`；规则 TSV 以 §3.0.1 内嵌块为准，R3 独立副本 `field-rules.tsv` SHA `aab3f4cfbcb9a28b246f812056b6e2f7e77bde6c70c9f7df7e869a94365e959a`，两者内容逐行一致）：

```powershell
<!--TOOL:verify-mapping:BEGIN-->
# N-3 round-3: semantic field-mapping rule checker (R2-1).
# The report embeds a FIELD-RULES block (tab-separated) that must partition
# EVERY normalized census path (containers included) into semantic classes,
# each with a V4 exporter anchor and a C# consumer anchor (or an explicit
# GAP:/meta: reference for unbound/excluded units).
#
# Modes:
#   check    -RulesFile <tsv> | -ReportPath <md> -CensusDir <dir>
#   emit     -PatternsFile <tsv> -CensusDir <dir> -OutFile <tsv>   (fill paths column)
#   negative -Kind strip-anchors|drop-rule|anchorless-rule -RulesFile <tsv> -CensusDir <dir>
#            (expect the mutated check to FAIL; exit 0 when it fails = negative detected)
# Check fails on: leaked path (no rule), count mismatch, unknown class,
# missing/invalid C# anchor, missing/invalid exporter anchor, missing owner.
param([string]$Mode, [string]$RulesFile, [string]$PatternsFile, [string]$ReportPath,
      [string]$CensusDir, [string]$OutFile, [string]$Kind,
      [string]$Repo, [string]$Evidence)
# -Repo/-Evidence give every report-embedded tool the same invocation surface:
#   -Evidence <dir>  evidence root; CensusDir defaults to <dir>\census-out
#   -Repo <path>     audit worktree root (accepted for uniformity; this checker
#                    consumes only census output, never the repo itself)
$ErrorActionPreference = 'Stop'
if (-not $CensusDir -and $Evidence) { $CensusDir = Join-Path $Evidence 'census-out' }
$fixtures = @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')
$validClass = @('CONTAINER','IN_BIND','OUT_DIFF','INTERNAL','SEM_MAP','UNBOUND','UNBOUND_HC','META')

function Load-Census([string]$fx) {
  $list = New-Object 'System.Collections.Generic.List[string]'
  foreach ($line in Get-Content (Join-Path $CensusDir ($fx + '-full-paths.txt'))) {
    [void]$list.Add((($line -split ' ')[0]))
  }
  return $list
}

function Parse-Rules([string[]]$lines) {
  $rules = New-Object System.Collections.Generic.List[object]
  foreach ($line in $lines) {
    if ($line -notmatch '\S') { continue }
    if ($line.StartsWith('#')) { continue }
    $c = $line -split "`t"
    if ($c.Count -lt 7) { continue }   # skips marker lines (FIELD-RULES:BEGIN/END) and blanks
    if ($c[0].Length -gt 0 -and $c[0][0] -eq [char]0xFEFF) { $c[0] = $c[0].Substring(1) }  # UTF-8 BOM tolerant
    if ($c[0] -eq 'fixture') { continue }
    $rules.Add([pscustomobject]@{
      fixture = $c[0]; pattern = $c[1]; paths = if ($c[2] -match '^\d+$') { [int]$c[2] } else { -1 }
      cls = $c[3]; exporter = $c[4]; cs = $c[5]; owner = $c[6]
    })
  }
  return $rules
}

function Read-Rules-Block([string]$md) {
  $text = Get-Content -Raw -Encoding UTF8 $md
  $i = $text.IndexOf('FIELD-RULES:BEGIN', [StringComparison]::Ordinal)
  if ($i -lt 0) { throw 'FIELD-RULES:BEGIN not found in report' }
  $j = $text.IndexOf('FIELD-RULES:END', $i, [StringComparison]::Ordinal)
  if ($j -lt 0) { throw 'FIELD-RULES:END not found in report' }
  $block = $text.Substring($i, $j - $i)
  return ($block -split "`r?`n")
}

function Test-Match([string]$pattern, [string]$path) {
  if ($pattern -eq '$') { return ($path -eq '$') }
  if ($path -eq '$') { return $false }
  $p = $pattern.TrimStart('$').TrimStart('.').Split('.')
  $a = $path.TrimStart('$').TrimStart('.').Split('.')
  $ai = 0
  for ($pi = 0; $pi -lt $p.Count; $pi++) {
    if ($p[$pi] -eq '#') { return ($pi -eq $p.Count - 1) }
    if ($ai -ge $a.Count) { return $false }
    if ($p[$pi] -eq '*') { $ai++; continue }
    if ($p[$pi] -cne $a[$ai]) { return $false }
    $ai++
  }
  return ($ai -eq $a.Count)
}

function Invoke-Check($rules) {
  $errors = New-Object System.Collections.Generic.List[string]
  $totalPaths = 0
  foreach ($fx in $fixtures) {
    $fr = @($rules | Where-Object { $_.fixture -eq $fx })
    if ($fr.Count -eq 0) { $errors.Add('no rules for ' + $fx); continue }
    $paths = Load-Census $fx
    $totalPaths += $paths.Count
    $counts = New-Object 'int[]' $fr.Count
    $leak = 0
    foreach ($p in $paths) {
      $owned = -1
      for ($i = 0; $i -lt $fr.Count; $i++) { if (Test-Match $fr[$i].pattern $p) { $owned = $i; break } }
      if ($owned -lt 0) { $errors.Add('LEAK ' + $fx + ' ' + $p); $leak++; continue }
      $counts[$owned]++
    }
    for ($i = 0; $i -lt $fr.Count; $i++) {
      $r = $fr[$i]
      if ($r.paths -ne $counts[$i]) {
        $errors.Add('COUNT ' + $fx + ' rule#' + ($i + 1) + ' ' + $r.pattern + ' declared=' + $r.paths + ' actual=' + $counts[$i])
      }
      if ($validClass -notcontains $r.cls) { $errors.Add('CLASS ' + $fx + ' ' + $r.pattern + ' unknown class ' + $r.cls) }
      $csOk = $false
      $csHash = $r.cs -match '\S+\.cs:\d+'
      switch ($r.cls) {
        'IN_BIND'    { $csOk = $csHash }
        'OUT_DIFF'   { $csOk = $csHash }
        'INTERNAL'   { $csOk = $csHash }
        'SEM_MAP'    { $csOk = ($csHash -or $r.cs.StartsWith('GAP:')) }
        'UNBOUND'    { $csOk = $r.cs.StartsWith('GAP:') }
        'UNBOUND_HC' { $csOk = $r.cs.StartsWith('GAP:') }
        'META'       { $csOk = $r.cs.StartsWith('meta:') }
        'CONTAINER'  { $csOk = $r.cs.StartsWith('meta:') }
      }
      if (-not $csOk) { $errors.Add('ANCHOR-C# ' + $fx + ' ' + $r.pattern + ' class=' + $r.cls + ' cs=' + $r.cs) }
      $expOk = ($r.exporter -match '\S+\.kt(:\d+(-\d+)?)?$') -or $r.exporter.StartsWith('meta:')
      if (-not $expOk) { $errors.Add('ANCHOR-V4 ' + $fx + ' ' + $r.pattern + ' exporter=' + $r.exporter) }
      if (-not $r.owner) { $errors.Add('OWNER ' + $fx + ' ' + $r.pattern) }
    }
    Write-Host ('RULES ' + $fx + ' paths=' + $paths.Count + ' rules=' + $fr.Count + ' leaked=' + $leak)
  }
  if ($errors.Count -eq 0) {
    Write-Host ('RULES-CHECK-OK total-paths=' + $totalPaths + ' rules=' + $rules.Count + ' leaks=0 mismatches=0 anchor-errors=0')
    return 0
  }
  $errors | Select-Object -First 40 | ForEach-Object { Write-Host ('FAIL ' + $_) }
  Write-Host ('RULES-CHECK-FAILED errors=' + $errors.Count)
  return 1
}

switch ($Mode) {
  'emit' {
    $patterns = Parse-Rules (Get-Content -Encoding UTF8 $PatternsFile)
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add(("fixture`tpattern`tpaths`tclass`texporterAnchor`tcsAnchor`towner"))
    foreach ($fx in $fixtures) {
      $fr = @($patterns | Where-Object { $_.fixture -eq $fx })
      $paths = Load-Census $fx
      $counts = New-Object 'int[]' $fr.Count
      foreach ($p in $paths) {
        for ($i = 0; $i -lt $fr.Count; $i++) { if (Test-Match $fr[$i].pattern $p) { $counts[$i]++; break } }
      }
      $k = 0
      foreach ($r in $fr) {
        $out.Add(($r.fixture + "`t" + $r.pattern + "`t" + $counts[$k] + "`t" + $r.cls + "`t" + $r.exporter + "`t" + $r.cs + "`t" + $r.owner))
        $k++
      }
    }
    $out | Set-Content -Encoding UTF8 $OutFile
    Write-Output ('EMIT-RULES ' + $OutFile + ' rules=' + ($out.Count - 1))
    $leaks = 0
    foreach ($fx in $fixtures) {
      $fr = @($patterns | Where-Object { $_.fixture -eq $fx })
      foreach ($p in (Load-Census $fx)) {
        $hit = $false
        foreach ($r in $fr) { if (Test-Match $r.pattern $p) { $hit = $true; break } }
        if (-not $hit) { Write-Output ('LEAK ' + $fx + ' ' + $p); $leaks++ }
      }
    }
    if ($leaks -gt 0) { Write-Output ('EMIT-WITH-LEAKS=' + $leaks); exit 1 }
    exit 0
  }
  'check' {
    if ($ReportPath) { $rules = Parse-Rules (Read-Rules-Block $ReportPath) }
    else { $rules = Parse-Rules (Get-Content -Encoding UTF8 $RulesFile) }
    $rc = Invoke-Check $rules
    exit $rc
  }
  'negative' {
    $lines = if ($ReportPath) { Read-Rules-Block $ReportPath } else { Get-Content -Encoding UTF8 $RulesFile }
    switch ($Kind) {
      'strip-anchors' {
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) {
          $c = $l -split "`t"
          if ($c.Count -ge 7 -and $c[0] -ne 'fixture' -and ($c[3] -eq 'IN_BIND' -or $c[3] -eq 'OUT_DIFF' -or $c[3] -eq 'INTERNAL' -or $c[3] -eq 'SEM_MAP')) {
            [void]$mut.Add(($c[0] + "`t" + $c[1] + "`t" + $c[2] + "`t" + $c[3] + "`t" + $c[4] + "`t`t" + $c[6]))
          } else { [void]$mut.Add($l) }
        }
      }
      'drop-rule' {
        $mut = New-Object System.Collections.Generic.List[string]
        $dropped = $false
        foreach ($l in $lines) {
          $c = $l -split "`t"
          if (-not $dropped -and $c.Count -ge 7 -and $c[0] -ne 'fixture' -and $c[2] -match '^\d+$' -and [int]$c[2] -gt 0) { $dropped = $true; continue }
          [void]$mut.Add($l)
        }
      }
      'anchorless-rule' {
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { [void]$mut.Add($l) }
        [void]$mut.Add(("physical-v1`t" + '$.samples[*].expected.#' + "`t0`tOUT_DIFF`tV5PhysicalGoldenExporterTest.kt`t`tP1.1"))
      }
      default { Write-Error 'unknown -Kind'; exit 2 }
    }
    $rules = Parse-Rules $mut
    $rc = Invoke-Check $rules
    if ($rc -eq 1) { Write-Output ('NEGATIVE-DETECTED ' + $Kind + ' (mutated check FAILED as required)'); exit 0 }
    else { Write-Output ('NEGATIVE-NOT-DETECTED ' + $Kind + ' (mutated check PASSED = checker too weak)'); exit 1 }
  }
  default { Write-Error 'usage: -Mode emit|check|negative'; exit 2 }
}
<!--TOOL:verify-mapping:END-->
```

**verify-mapping 完整命令（R3 实测；emit 产出 417 规则 TSV → check 正检 → 三负例全部 NEGATIVE-DETECTED）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-mapping.ps1 -Mode emit -PatternsFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules-patterns.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/census-out -OutFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/emit-run.log 2>&1
→ EXIT=0；EMIT-RULES field-rules.tsv rules=417（emit-run.log SHA256 149159d5c894d02093833948d9296a9700d490b58654ddcd97762e524fc595f1；无 LEAK 行）
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-mapping.ps1 -Mode check -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/check-run.log 2>&1
→ EXIT=0；实测输出：
RULES physical-v1 paths=296 rules=78 leaked=0
RULES scoring-v1 paths=419 rules=63 leaked=0
RULES round-v1 paths=200 rules=72 leaked=0
RULES cheat-v1 paths=204 rules=116 leaked=0
RULES shuffle-v1 paths=199 rules=38 leaked=0
RULES ai-v1 paths=198 rules=50 leaked=0
RULES-CHECK-OK total-paths=1516 rules=417 leaks=0 mismatches=0 anchor-errors=0
```

**对本报告 §3.0.1 内嵌块的直接校验（-ReportPath 模式：不经外部 TSV，直接读报告）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-mapping.ps1 -Mode check -ReportPath D:/majiang-wt/n3-golden-field-audit/doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/check-report.log 2>&1
→ EXIT=0；RULES-CHECK-OK total-paths=1516 rules=417 leaks=0 mismatches=0 anchor-errors=0
```

**三个变异负例（R2-1a 验收：checker 必须对三类作弊全部 FAIL；NEGATIVE-DETECTED = 变异后检查如预期失败）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-mapping.ps1 -Mode negative -Kind strip-anchors -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/negative-strip-anchors.log 2>&1
→ EXIT=0；RULES-CHECK-FAILED errors=254（全部 ANCHOR-C#）；NEGATIVE-DETECTED strip-anchors（negative-strip-anchors.log SHA256 dfbd76d7d712b9b28b321f3bc1c87f605dea0ef2ee1d877ee8b1f70dfe4ae5cf）
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-mapping.ps1 -Mode negative -Kind drop-rule -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/negative-drop-rule.log 2>&1
→ EXIT=0；FAIL LEAK physical-v1 $；RULES-CHECK-FAILED errors=1；NEGATIVE-DETECTED drop-rule（negative-drop-rule.log SHA256 5af2d3f4b07a9028191fa3fcaf231c0ea85cf72b2f3a11d909ce8078b4b2650e）
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/verify-mapping.ps1 -Mode negative -Kind anchorless-rule -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/negative-anchorless-rule.log 2>&1
→ EXIT=0；FAIL ANCHOR-C# physical-v1 $.samples[*].expected.#；RULES-CHECK-FAILED errors=1；NEGATIVE-DETECTED anchorless-rule（negative-anchorless-rule.log SHA256 b92c9f721468cadc418b56396f4d4c76b9509bdec45a136af451a1eb0e2e8d94）
```

**FIELD-RULES 按类/按 owner 复算命令（§3.0.1 统计表与 §2.1 缺口分布的独立核对）**：

```
awk -F'\t' 'NR>1 && $4=="UNBOUND" || NR>1 && $4=="UNBOUND_HC" {s[$4]+=$3} END {print "UNBOUND="s["UNBOUND"], "UNBOUND_HC="s["UNBOUND_HC"]}' C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv
→ UNBOUND=47 UNBOUND_HC=40（GAP 合计 87）
awk -F'\t' 'NR>1 && $7 ~ /^GAP/ {t[$7]+=$3} END {for (k in t) print k, t[k]}' C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/field-rules.tsv | sort
→ GAP:N3-B1 13 / GAP:N3-B2 1 / GAP:N3-B3 6 / GAP:N3-B4 26 / GAP:N3-B5 8 / GAP:N3-B6 3 / GAP:N3-E1 6 / GAP:N3-E2 24
```

### 11.7 75-case 分桶机器复算 case-refs（工具源内嵌；R2-3a-c/R2-2 验收：-File 命令 exit 0）

工具源码全文内嵌如下（R3 实测 SHA `39aeccc450fcb3ddd63cfa4ed56b3527071b88934e293ce42e616cf499995492`；提取命令见 §11.10）：

```powershell
<!--TOOL:case-refs:BEGIN-->
# N-3 round-3: machine-recompute the 75-case reference/binding buckets.
# -Repo    : audit worktree root
# -Evidence: exclusive TEMP evidence dir (created if missing)
# Emits case-refs.tsv (75 rows) + case-refs-summary.txt and exits 0 only when
# every bucket rule holds and the section/total counts match the expected set.
param([string]$Repo = 'D:/majiang-wt/n3-golden-field-audit',
      [string]$Evidence = 'C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3')
$ErrorActionPreference = 'Stop'

$fixturePath = Join-Path $Repo 'tests/v5-golden/cheat-v1.json'
$srcPath     = Join-Path $Repo 'dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs'
$fixture = Get-Content -Raw -Encoding UTF8 $fixturePath | ConvertFrom-Json
$srcLines = Get-Content -Encoding UTF8 $srcPath

# Strip // comments per line (no block comments in this file), keep quotes intact.
function StripComment([string]$line) {
  $i = $line.IndexOf('//')
  if ($i -ge 0) { return $line.Substring(0, $i) }
  return $line
}
$codeLines = @($srcLines | ForEach-Object { StripComment $_ })

function Find-Quoted([string]$id) {
  $needle = '"' + $id + '"'
  for ($i = 0; $i -lt $codeLines.Count; $i++) {
    if ($codeLines[$i].Contains($needle)) { return $i }
  }
  return -1
}
function Find-Bare([string]$id) {           # any occurrence in raw source incl. comments
  foreach ($l in $srcLines) { if ($l.Contains($id)) { return $true } }
  return $false
}
# Call-site context: the quoted literal's line joined with next 2 lines.
function CallKind([int]$lineIdx) {
  if ($lineIdx -lt 0) { return 'NONE' }
  $ctx = $codeLines[$lineIdx]
  if ($lineIdx + 1 -lt $codeLines.Count) { $ctx += ' ' + $codeLines[$lineIdx + 1] }
  if ($lineIdx + 2 -lt $codeLines.Count) { $ctx += ' ' + $codeLines[$lineIdx + 2] }
  if ($ctx.Contains('AssertGoldenRejection') -or $ctx.Contains('AssertGoldenStaleOffer') -or $ctx.Contains('AssertGoldenAllowed')) { return 'ASSERT_GOLDEN' }
  if ($ctx.Contains('Case(')) { return 'CASE_READ' }
  return 'QUOTED_OTHER'
}

$rows = New-Object System.Collections.Generic.List[string]
$quoted = @{ tileMarkHandler = 0; sleeveBloomHandler = 0; beamSwapHandler = 0 }
$gi = @{ tileMarkHandler = 0; sleeveBloomHandler = 0; beamSwapHandler = 0 }
$bucketCount = @{}
foreach ($sec in @('tileMarkHandler','sleeveBloomHandler','beamSwapHandler')) {
  foreach ($c in $c2 = @($fixture.$sec)) {
    $id = $c.caseId
    $qi = Find-Quoted $id
    $kind = CallKind $qi
    $bare = Find-Bare $id
    $hasGI = $null -ne $c.goldenInputs
    if ($hasGI) { $gi[$sec]++ }
    # Mechanical bucket:
    #  A = ASSERT_GOLDEN (real-entry replay with golden outcome/reason compare)
    #  C = CASE_READ + goldenInputs non-empty (golden-input-driven full chain)
    #  B = CASE_READ without goldenInputs (partial binding)
    #  E = no quoted literal, bare mention only (comment-level existence)
    #  D = everything else (reason-map completeness only)
    if ($kind -eq 'ASSERT_GOLDEN') { $b = 'A' }
    elseif ($kind -eq 'CASE_READ' -and $hasGI) { $b = 'C' }
    elseif ($kind -eq 'CASE_READ') { $b = 'B' }
    elseif ($qi -ge 0) { $b = 'QUOTED_OTHER' }
    elseif ($bare) { $b = 'E' }
    else { $b = 'D' }
    if (-not $bucketCount.ContainsKey($b)) { $bucketCount[$b] = 0 }
    $bucketCount[$b]++
    if ($qi -ge 0) { $quoted[$sec]++ }
    $rows.Add(($sec + "`t" + $id + "`t" + $c.outcome + "`t" + $c.reason + "`t" + $hasGI + "`t" + $kind + "`t" + $b + "`tline=" + ($qi + 1)))
  }
}

$out = Join-Path $Evidence 'case-refs.tsv'
$rows | Set-Content -Encoding UTF8 $out
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add(('quoted tileMarkHandler=' + $quoted['tileMarkHandler'] + ' sleeveBloomHandler=' + $quoted['sleeveBloomHandler'] + ' beamSwapHandler=' + $quoted['beamSwapHandler'] + ' total=' + ($quoted['tileMarkHandler'] + $quoted['sleeveBloomHandler'] + $quoted['beamSwapHandler'])))
$summary.Add(('goldenInputs tileMark=' + $gi['tileMarkHandler'] + '/20 sleeveBloom=' + $gi['sleeveBloomHandler'] + '/32 beamSwap=' + $gi['beamSwapHandler'] + '/23'))
$bk = ($bucketCount.Keys | Sort-Object | ForEach-Object { $_ + '=' + $bucketCount[$_] }) -join ' '
$summary.Add('buckets ' + $bk)
$total = ($rows | Measure-Object).Count
$summary.Add('rows=' + $total)
# Acceptance: A=16 B=2 C=6 E=1 D=50, quoted 7/11/6=24, rows=75
$ok = $true
if ($bucketCount['A'] -ne 16) { $summary.Add('FAIL A!=16'); $ok = $false }
if ($bucketCount['B'] -ne 2)  { $summary.Add('FAIL B!=2');  $ok = $false }
if ($bucketCount['C'] -ne 6)  { $summary.Add('FAIL C!=6');  $ok = $false }
if ($bucketCount['E'] -ne 1)  { $summary.Add('FAIL E!=1');  $ok = $false }
if ($bucketCount['D'] -ne 50) { $summary.Add('FAIL D!=50'); $ok = $false }
if ($bucketCount['QUOTED_OTHER'] -gt 0) { $summary.Add('FAIL quoted-but-unclassified rows exist'); $ok = $false }
if ($total -ne 75) { $summary.Add('FAIL rows!=75'); $ok = $false }
$summary | Set-Content -Encoding UTF8 (Join-Path $Evidence 'case-refs-summary.txt')
$summary | ForEach-Object { Write-Output $_ }
if ($ok) { Write-Output 'CASE-REFS-OK'; exit 0 } else { exit 1 }
<!--TOOL:case-refs:END-->
```

**case-refs 完整命令（-File 形式，exit 0；对 fixture+源码重算 75 行分桶并校验桶规则）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/case-refs.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/case-refs-run.log 2>&1
→ EXIT=0；产出 case-refs.tsv（75 行，SHA256 af0aedd8b62fa05be5f95ddc04f3390321ec1711d41621a6737f367d3b0b72bb）与 case-refs-summary.txt（SHA256 e8303afc3ae73aa7d051c0d37a95ccc4dea7a27962f8e51d419998427ea2dce4），内容：
quoted tileMarkHandler=7 sleeveBloomHandler=11 beamSwapHandler=6 total=24
goldenInputs tileMark=0/20 sleeveBloom=7/32 beamSwap=0/23
buckets A=16 B=2 C=6 D=50 E=1
rows=75
```

75 行逐条桶归属 = §3.4 表；case-refs.tsv 为机器真值（`caseId,section,outcome,reason,quoted,bucket` 75 行），与 §3.4 表格、§4 拆分、§9 N3-B3 三处引用同源。

### 11.8 判级变异探针（第 3 轮 4 探针工具源内嵌 + 第 2 轮 5 探针存档引用）

第 3 轮探针工具源码全文内嵌如下（R3 实测 SHA `bd9a403d01da8260979918e2e1509d1fcae1844da98b85f5a7a4599a79c0ef9c`；变异全部发生在 R3\fake-repo 副本，真实工作树零改动；提取命令见 §11.10）：

```powershell
<!--TOOL:probes-state-fields:BEGIN-->
# N-3 round-3 rework: targeted mutation probes proving (R2-1b/R2-1c/R2-3a)
#   P1 sb-already-used-bound-positive : reason -> WRONG_PHASE  => 1 FAIL exit 1 (bucket A positive control)
#   P2 sb-choose-first-unbound        : outcome/reason mutated => 16/16 PASS exit 0 (bucket E)
#   P3 tilemark-state-fields-unbound  : 6 unbound tileMarkState fields => 16/16 PASS exit 0
#   P4 ledger-markers-unbound         : outcome/exception of 4 ledger subkeys => 16/16 PASS exit 0
# All mutations happen on a COPY inside -Evidence\fake-repo; the real tree is
# never modified. Mutations are POSITIONAL: each lands after its caseId/subkey
# anchor, never on the first textual occurrence in the file.
param([string]$Repo = 'D:/majiang-wt/n3-golden-field-audit',
      [string]$Evidence = 'C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3')
$ErrorActionPreference = 'Stop'
$fake   = Join-Path $Evidence 'fake-repo'
$probes = Join-Path $Evidence 'probes'
$dotnet = 'D:\Unity\dotnet\dotnet.exe'
$binSrc  = Join-Path $Repo 'dotnet\tests\Mahjong.Domain.Tests\bin\Release\net8.0'
$binDst  = Join-Path $fake 'dotnet\tests\Mahjong.Domain.Tests\bin\Release\net8.0'
$dllFake = Join-Path $binDst 'Mahjong.Domain.Tests.dll'

New-Item -ItemType Directory -Force -Path (Join-Path $fake 'tests\v5-golden') | Out-Null
New-Item -ItemType Directory -Force -Path $probes | Out-Null
Copy-Item (Join-Path $Repo 'AGENTS.md') (Join-Path $fake 'AGENTS.md') -Force
if (-not (Test-Path $dllFake)) { Copy-Item $binSrc $binDst -Recurse }

function Find-After([string]$text, [string]$needle, [int]$from) {
  $i = $text.IndexOf($needle, $from, [StringComparison]::Ordinal)
  if ($i -lt 0) { throw ('anchor missing after ' + $from + ': ' + $needle) }
  return $i
}

function Run-Probe([string]$name, [string[]]$anchorSeq) {
  # anchorSeq: alternating needle / replacement, applied strictly in order,
  # each search starting AFTER the previous replacement's position.
  $dir = Join-Path $probes $name
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $text = Get-Content -Raw -Encoding UTF8 (Join-Path $Repo 'tests\v5-golden\cheat-v1.json')
  $pos = 0
  for ($k = 0; $k -lt $anchorSeq.Count; $k += 2) {
    $i = Find-After $text $anchorSeq[$k] $pos
    $text = $text.Substring(0, $i) + $anchorSeq[$k + 1] + $text.Substring($i + $anchorSeq[$k].Length)
    $pos = $i + $anchorSeq[$k + 1].Length
  }
  $dst = Join-Path $fake 'tests\v5-golden\cheat-v1.json'
  [System.IO.File]::WriteAllText($dst, $text, (New-Object System.Text.UTF8Encoding($false)))
  Copy-Item $dst (Join-Path $dir 'cheat-v1.json') -Force
  $log = Join-Path $dir ($name + '.log')
  $old = $env:MAHJONG_REPO_ROOT
  $env:MAHJONG_REPO_ROOT = $fake
  try {
    & $dotnet $dllFake -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo 1>$log 2>&1
    $code = $LASTEXITCODE
  } finally { $env:MAHJONG_REPO_ROOT = $old }
  $sum = (Get-Content $log | Select-String -Pattern 'Total: \d+').Line | Select-Object -First 1
  $sha = (Get-FileHash -Algorithm SHA256 $dst).Hash
  Write-Host ($name + ' | exit=' + $code + ' | ' + $sum.Trim() + ' | mutatedFixtureSha256=' + $sha)
  Add-Content -Encoding UTF8 -Path (Join-Path $probes 'probe-summary.txt') -Value ($name + ' | exit=' + $code + ' | ' + $sum.Trim() + ' | mutatedFixtureSha256=' + $sha)
  return $code
}

$e1 = Run-Probe 'sb-already-used-bound-positive' @(
  '"caseId": "sb-activate-already-used"', '"caseId": "sb-activate-already-used"',
  '"reason": "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW"', '"reason": "WRONG_PHASE"')
$e2 = Run-Probe 'sb-choose-first-unbound' @(
  '"caseId": "sb-choose-first"', '"caseId": "sb-choose-first"',
  '"outcome": "Allowed"', '"outcome": "Rejected"',
  '"reason": null', '"reason": "WRONG_PHASE"')
$e3 = Run-Probe 'tilemark-state-fields-unbound' @(
  '"versionAfterFirstRegister": 1', '"versionAfterFirstRegister": 999',
  '"sameIdDifferentContentException": "IllegalArgumentException"', '"sameIdDifferentContentException": "NOT_AN_EXCEPTION"',
  '"seatGroupLimitIsThree": true', '"seatGroupLimitIsThree": false',
  '"seatGroupLimitException": "IllegalArgumentException"', '"seatGroupLimitException": "NOT_AN_EXCEPTION"',
  '"seatTileLimitIsTwelve": true', '"seatTileLimitIsTwelve": false',
  '"createdRoundOrdinal": 1', '"createdRoundOrdinal": 999')
$e4 = Run-Probe 'ledger-markers-unbound' @(
  '"conflictDifferentPayload": {', '"conflictDifferentPayload": {',
  '"outcome": "rejected"', '"outcome": "accepted"',
  '"exception": "IllegalArgumentException"', '"exception": "NOT_AN_EXCEPTION"',
  '"insufficientFunds": {', '"insufficientFunds": {',
  '"outcome": "rejected"', '"outcome": "accepted"',
  '"exception": "IllegalStateException"', '"exception": "NOT_AN_EXCEPTION"',
  '"overflow": {', '"overflow": {',
  '"outcome": "rejected"', '"outcome": "accepted"',
  '"exception": "ArithmeticException"', '"exception": "NOT_AN_EXCEPTION"',
  '"invalidSeat": {', '"invalidSeat": {',
  '"outcome": "rejected"', '"outcome": "accepted"',
  '"exception": "IllegalArgumentException"', '"exception": "NOT_AN_EXCEPTION"')

$fail = 0
if ($e1 -ne 1) { Write-Output 'UNEXPECTED sb-already-used-bound-positive: expected exit 1 (bound)'; $fail++ }
if ($e2 -ne 0) { Write-Output 'UNEXPECTED sb-choose-first-unbound: expected exit 0'; $fail++ }
if ($e3 -ne 0) { Write-Output 'UNEXPECTED tilemark-state-fields-unbound: expected exit 0'; $fail++ }
if ($e4 -ne 0) { Write-Output 'UNEXPECTED ledger-markers-unbound: expected exit 0'; $fail++ }
Write-Output ('PROBES-DONE alreadyUsed=' + $e1 + ' chooseFirst=' + $e2 + ' stateFields=' + $e3 + ' ledgerMarkers=' + $e4)
if ($fail -gt 0) { exit 1 } else { Write-Output 'ALL-AS-EXPECTED'; exit 0 }
<!--TOOL:probes-state-fields:END-->
```

**第 3 轮 4 探针完整命令（-File 形式，exit 0 = 四探针全部 AS-EXPECTED）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/probes-state-fields.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/probes-state-fields-run.log 2>&1
→ EXIT=0；尾行 PROBES-DONE alreadyUsed=1 chooseFirst=0 stateFields=0 ledgerMarkers=0
```

| 探针 | 变异（位置性：锚定 caseId/子键之后） | native exit（期望） | 实测结果 | 探针日志 SHA256 | 变异 fixture SHA256 |
|---|---|---|---|---|---|
| P1 sb-already-used-bound-positive（正控制） | #23 reason `SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW`→**WRONG_PHASE** | **1**（1 FAIL） | Total: 16, **Failed: 1**（FAIL @ CheatGoldenTests.cs:557 Assert.Equal，证明 :486-490 绑定真实存在） | `92ba5f470bc53c7b44daecfbe635cf6414f855f81afb8cc71662e360ea280d40` | `7516BD4C4380F60A3D18170AA0E85FD0EACE89BAC4340C994F5BCF417DDA62FD` |
| P2 sb-choose-first-unbound（E 桶） | #36 outcome Allowed→**Rejected**、reason null→**WRONG_PHASE** | 0（0 FAIL） | Total: 16, Failed: 0 | `c0644bed79fdc9fd000d273e2b45a22537a371c0ba99aadb4455ac7031a46f26` | `63E5B0560D6D5A3691AE17370B387DAFBEDA5B8E8E6673819168D80523FF89DC` |
| P3 tilemark-state-fields-unbound | tileMarkState 6 字段（versionAfterFirstRegister 1→999、sameIdDifferentContentException→NOT_AN_EXCEPTION、seatGroupLimitIsThree true→false、seatGroupLimitException→NOT_AN_EXCEPTION、seatTileLimitIsTwelve true→false、createdRoundOrdinal 1→999） | 0（0 FAIL） | Total: 16, Failed: 0 | `8e26a41c256925270e84ff54ac5b42932987b82762b189b98f57a2de139293b1` | `816872C58041528973DBB842C5AC8693A1D5A5E3688658C0DF01983173474568` |
| P4 ledger-markers-unbound | 4 子键 outcome rejected→**accepted** + exception 具体类→**NOT_AN_EXCEPTION**（conflictDifferentPayload/insufficientFunds/overflow/invalidSeat） | 0（0 FAIL） | Total: 16, Failed: 0 | `d173fed0c5b7c310ddef9d3a569e4b6871f402e05705dd7117262e971afb18e2` | `F74BECD5A999F6121DD4D9C3E28E98BDCD2FCC47A17CC4D53D06B96B9C8414F0` |

`probes/probe-summary.txt`（4 行 + 全 AS-EXPECTED）SHA256 `539ad92504871baf61fbbc6583b0813109961e88c59db87b2d56299db06d224f`；探针工具运行日志 `probes-state-fields-run.log` SHA256 `d9595cd9d717dfb497488f77eb4d1796160322aa40c820041fe6b93df1c9dfc6`（探针日志内 `Time:` 为实测耗时、随运行变化，判定行/exit/变异 fixture SHA 恒定；本表 SHA 为交付时在案实测值）。

**第 2 轮 5 探针（存档引用，证据在 R2；结论已在 §10 表 3-7/11 引用）**：round-flags-mutated-true（6/6 PASS）、scoring-win-status-mutated（7/7 PASS）、positive-control-bound-score-mutation（**7 跑 1 FAIL exit 1**）、shuffle-deadwall-flag-mutated（4/4 PASS）、cheat-choose-first-mutated-rejected（16/16 PASS）；脚本 `review-probes.ps1` SHA256 `4d3896114c9f93937a4f886bcc0341904250214f2a5d06a79f7ba33a64597854`、`probe-summary.txt` SHA256 `d2d693fd1b4aedbaf064ef318aa884380a31ec2dce65865102a1fab37a44e353`（5 行全 AS-EXPECTED）、逐探针日志/变异 fixture SHA 见 R2 目录与第 2 轮存档（本报告 §10 结论与 R2 日志一致，不重复罗列 35 个哈希）。

### 11.9 fixture SHA256（工作树实测，与 catalog 声明一致；沿用第 1 轮）

| fixture | 实测 SHA256 | catalog 声明 |
|---|---|---|
| physical-v1.json | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | 一致 |
| scoring-v1.json | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | 一致 |
| round-v1.json | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | 一致 |
| cheat-v1.json | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | 一致 |
| shuffle-v1.json | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | 一致 |
| ai-v1.json | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | catalog 外（§3.6） |
| catalog-v1.json | `16f7fd7f980398388e4e5da45a3d5fb755c19990915f403987094617e90e891b` | — |

### 11.10 自抽取复算闭环（R2-2 验收：空 TEMP 从本报告代码块重建全部工具并复算；实测证据表）

**提取命令（awk 按行内标记对切分；6 个工具全部来自本报告 §11 代码块）**：

```
EV=C:/Users/sncdc/AppData/Local/Temp/n3-extract-loop
mkdir -p "$EV/extracted"
R=D:/majiang-wt/n3-golden-field-audit/doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md
awk '/^<!--TOOL:fullcensus-csproj:BEGIN-->/{f=1;next} /^<!--TOOL:fullcensus-csproj:END-->/{f=0} f' "$R" > "$EV/extracted/FullCensus.csproj"
awk '/^<!--TOOL:fullcensus-program:BEGIN-->/{f=1;next} /^<!--TOOL:fullcensus-program:END-->/{f=0} f' "$R" > "$EV/extracted/Program.cs"
awk '/^<!--TOOL:verify-coverage:BEGIN-->/{f=1;next} /^<!--TOOL:verify-coverage:END-->/{f=0} f' "$R" > "$EV/extracted/verify-coverage.ps1"
awk '/^<!--TOOL:verify-mapping:BEGIN-->/{f=1;next} /^<!--TOOL:verify-mapping:END-->/{f=0} f' "$R" > "$EV/extracted/verify-mapping.ps1"
awk '/^<!--TOOL:case-refs:BEGIN-->/{f=1;next} /^<!--TOOL:case-refs:END-->/{f=0} f' "$R" > "$EV/extracted/case-refs.ps1"
awk '/^<!--TOOL:probes-state-fields:BEGIN-->/{f=1;next} /^<!--TOOL:probes-state-fields:END-->/{f=0} f' "$R" > "$EV/extracted/probes-state-fields.ps1"
```

**实测证据表（LOOP = `C:/Users/sncdc/AppData/Local/Temp/n3-extract-loop`，全新空 TEMP；下列步骤按上块命令原样执行，全部 SHA256 为交付时在案实测）**：

**① 工具重建**：上块 6 条 awk 提取全部成功，提取件与 §11 各内嵌源 `cmp` **逐一 byte-identical**：

| 提取件 | SHA256（= §11 对应内嵌源） |
|---|---|
| `extracted/FullCensus.csproj` | `8f946bc6c411cc89c659c88eeb89ddeaeb52cb54b0d480e58e09ff418e29168d` |
| `extracted/Program.cs` | `fd49db41b5dbb6602b7ce1a2e9a52b679f78b700dc1d93606d8f12f8f530b4d0` |
| `extracted/verify-coverage.ps1` | `f932794ee00db2c8cea4a67b15e4ec640bdc933f890a0240c3091a59e22d3997`（§11.5 头注同值） |
| `extracted/verify-mapping.ps1` | `1ce17dac4f663714492315d30f85c173b0e4ef0506f4bdf0175a03bda5b793c3`（§11.6 头注同值） |
| `extracted/case-refs.ps1` | `39aeccc450fcb3ddd63cfa4ed56b3527071b88934e293ce42e616cf499995492`（§11.7 头注同值） |
| `extracted/probes-state-fields.ps1` | `bd9a403d01da8260979918e2e1509d1fcae1844da98b85f5a7a4599a79c0ef9c`（§11.8 头注同值） |

另将本报告 `FIELD-RULES:BEGIN/END` 块提取为 `field-rules-from-report.tsv`（表头 + 417 规则 = 418 行）SHA256 `d932a03ff9a6b958576adf51a87deee684c88776f726f4546257d4ede51ee73d`，与 §11.6 独立副本 `field-rules.tsv`（BOM 剥除后）`cmp` 逐字节一致——"报告内嵌规则表 = 独立副本"由此闭环。

**② census 复算**：`D:/Unity/dotnet/dotnet.exe run --project LOOP/extracted/FullCensus.csproj -- D:/majiang-wt/n3-golden-field-audit LOOP/census-out` → EXIT=0，**14 件产物与 §11.4 表逐一相等**：

| 产物 | SHA256 | 与 §11.4 |
|---|---|---|
| `census-run.log` | `72c78ea2284e26f12d6828f693798b0f8b8b2d76c5fb285d7ff28ed8d7b86c7d` | 相等（尾部 `FULL-CENSUS-DONE failures=0`） |
| `census-summary.txt` | `7b8909f2a2a0c4dc4f4dff068fafb1e19762ed86d0af005d2eb739ea98041504` | 相等 |
| 6 × `<fixture>-full-paths.txt` | physical `db9f3db6…` / scoring `d84b7e1f…` / round `e646e083…` / cheat `391966d9…` / shuffle `5c0ada44…` / ai `ff1b9f56…` | 全部相等（296/419/200/204/199/198，合计 1516 路径复现） |
| 6 × `<fixture>-group-counts.txt` | `404757d8…` / `22eb1544…` / `5ae03e74…` / `e133b039…` / `5c93bd81…` / `d29988b8…` | 全部相等 |

scoring-v1 case 总数复现 = fixture `generationResult.totalSamples = 10238`（7 组：win 48 + noWin 4 + fu 9 + dora 6 + settlement 24 + score 147 + random 10000；fixture SHA 见 §11.9；census 对全部 case 逐节点遍历，scoring leafInstances=476153）。

**③ coverage 两层复算**（均对**本报告**执行；六组 EMIT 计数 28/27/22/21/19/31 与 §3.0 一致）：

| 步骤 | native exit | 实测输出 | 日志 SHA256 |
|---|---|---|---|
| emit（-Evidence LOOP） | 0 | 6 × prefix2 产物与 §11.5 版 `cmp` 相等 | LOOP `coverage-emit.log` `06efd6a773ef9a14a4d7b1c6869441ed8d18f58b370852b2df2796013eee8e51`（与 §11.5 命令日志逐字节相等） |
| verify（-ReportPath 本报告） | 0 | `COVERAGE-VERIFY-OK unmapped-total=0`（六组全 unmapped=0 mismatched=0 extra=0） | LOOP `coverage-verify.log` `e0dd66910112c9cd4c15eb3a18404d6114cb0cd296954e28deff5f5c2a4fce31`（与 §11.5 命令日志逐字节相等） |
| check `-ReportPath`（-CensusDir LOOP/census-out） | 0 | `RULES-CHECK-OK total-paths=1516 rules=417 leaks=0 mismatches=0 anchor-errors=0` | LOOP `check-report.log` `34ffcf3ddb18d5c7d40eff16b34807034286fb94f9f8b3c390ca4470e7334638`（= §11.6 `check-run.log` 逐字节） |
| check `-RulesFile`（① 的报告块提取 TSV） | 0 | 同上 | LOOP `check-tsv.log` 同 SHA `34ffcf3d…`（三次独立调用输出逐字节一致） |
| negative strip-anchors | 0（NEGATIVE-DETECTED） | errors=254 全 `ANCHOR-C#` | LOOP `negative-strip-anchors.log` `dfbd76d7…`（= §11.6 版逐字节） |
| negative drop-rule | 0（NEGATIVE-DETECTED） | `FAIL LEAK physical-v1 $`；errors=1 | LOOP `negative-drop-rule.log` `5af2d3f4…`（= §11.6 版逐字节） |
| negative anchorless-rule | 0（NEGATIVE-DETECTED） | `FAIL ANCHOR-C# physical-v1 $.samples[*].expected.#`；errors=1 | LOOP `negative-anchorless-rule.log` `b92c9f72…`（= §11.6 版逐字节） |

**④ case 分桶复算**：`extracted/case-refs.ps1 -Repo … -Evidence LOOP` → EXIT=0：`quoted tileMarkHandler=7 sleeveBloomHandler=11 beamSwapHandler=6 total=24`、`goldenInputs tileMark=0/20 sleeveBloom=7/32 beamSwap=0/23`、`buckets A=16 B=2 C=6 D=50 E=1`、`rows=75`、`CASE-REFS-OK`；`case-refs.tsv` `af0aedd8…` 与 `case-refs-summary.txt` `e8303afc…` 均与 §11.7 版逐字节相等；运行日志 LOOP `case-refs-run.log` SHA256 `a5e774b4d8bdc90c44df7164e1e9cdeb19350633aa7f98ea138241b99676c84c`。

**⑤ 探针复算（含"故意失败 → 非零"验收）**：`extracted/probes-state-fields.ps1 -Repo … -Evidence LOOP` → EXIT=0，尾行 `PROBES-DONE alreadyUsed=1 chooseFirst=0 stateFields=0 ledgerMarkers=0` + `ALL-AS-EXPECTED`；四个变异 fixture SHA 与 §11.8 表逐一相等（`7516BD4C…` / `63E5B056…` / `816872C5…` / `F74BECD5…`）。**故意失败非零**：P1（#23 reason→WRONG_PHASE 绑定正控制）测试进程实测 native **exit=1**、`Total: 16, Failed: 1`（FAIL @ CheatGoldenTests.cs:557）——探针工具捕获该非零并按预期判 AS-EXPECTED；LOOP 探针工具运行日志 `probes-state-fields-run.log` SHA256 `2f3940613af50168398fd25396773222a2a74f0fe53f78080f0d4e916943836c`，逐探针日志 4 份在 `LOOP/probes/<name>/<name>.log`（`Time:` 耗时随运行变化致 SHA 与 §11.8 R3 版不同，判定行/exit/变异 fixture SHA 一致）。

**⑥ 基线重放**：真实树上 `Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo` → **EXIT=0**、`Total: 16, Errors: 0, Failed: 0`（LOOP `cheat-golden-baseline.log` SHA256 `4d1371ac49906237dd7e3b3485b8e5b9e331dd68cef38220dd08979b036bf32b`）。

**闭环结论**：空 TEMP 从本报告代码块重建 6 工具 + 报告内嵌规则块，复算出与本审计 §11 各节**逐字节相等**的 census（1516 路径 / scoring 10238 case 全量遍历）、两层 coverage（`COVERAGE-VERIFY-OK` + `RULES-CHECK-OK` + 三负例全检出）、case 分桶（75 行 = A16+B2+C6+D50+E1）与探针结论（故意失败 exit 1 实证）；R2-2 验收四要素——空 TEMP 重建复现 1516 路径/10238 case、handler 命令 exit 0、故意失败非零、证据清单与实际日志 1:1——全部满足。

### 11.11 本报告自身哈希

- 完整性校验一律以 `sha256sum "doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md"` 实测 SHA256 为准，**提交后在最终交付说明中给出实测值**（正文不预填，杜绝手填哈希）。

### 11.12 caller 独立证据对照（E 目录，未复制未改）

第 3 轮 caller 审查证据 E = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r2-review-931bc685c4b34a738dcfdf4863b6cf99`。本审计仅只读引用其结论作交叉验证：audit-probes.ps1 `75203A8C…`/audit-probes.log `82634716…`、mutation-probes.ps1 `F6C73E7E…`/mutation-probes.log `ABA4FAE7…`、source-checks.log `A49162FD…`、sha256.json `3222812C…`、负例族（coverage-with-no-semantic-mapping / tilemark-claimed-full-fields-unbound / ledger-claimed-full-output-unbound / positive-control-sb-activate-already-used-bound / sb-choose-first-unbound）与 reported-repro.log `8BD48159…`（全量 SHA 见 E/sha256.json）。**关键交叉点**：caller 正控制 positive-control-sb-activate-already-used-bound（#23 绑定实证）与本方探针 P1 同结论——#23 = A 桶；caller 三个 claim-buster 负例指出的"tileMarkState/ledger 全差分"过度声明，本报告已按 UNBOUND_HC 实测改写（§3.4）。本报告所有数字以 §11 本方独占 TEMP 复算件为准，E 为独立第二来源，二者无矛盾。

---
## 12. 边界声明

1. 本报告**只读**了仓库与兄弟任务无关区域；未读取 N1a/N2 兄弟在途未提交内容，未假设其已修。
2. 未修改：生产代码、测试代码、fixtures、catalog、manifest、固定头已接受 G1 盘点报告、用户未提交文件（CLAUDE.md/PROMPTS.md/任务书/协议/Unity 环境/Resources/Burst）。
3. 全部写入限于：本报告一个文档（expectedFiles 指定路径）+ 独占 TEMP 三处（第 1 轮 `n3-field-audit-01edf75dbc3b`、第 2 轮 `n3-rework-round2`（含 fake-repo 变异副本）、第 3 轮 `n3-rework-round3` 与自抽取闭环 `n3-extract-loop`，均不触仓库）。
4. `V5-REVIEW-REWORK-PROTOCOL.md` 全程只读，未复制提交。
5. **审计报告组件完成 ≠ N-3 关闭 ≠ G1 批准**：N-3/G1 的关闭由 caller 依据本报告与补齐包（§9）独立复核后裁决。G1 门的全部口径由任务书既有规定与 caller 汇总复核决定——本报告与 §9 不缩小也不扩大该口径：所有已导出可比较未绑定内容全部保留为缺口（§3.0.1 GAP 87 条 + §9 覆盖清单），不增设任务书之外的批准前置条件。
6. 本报告不带任何 push/PR/CI 触发/远端操作。

---

## 13. 协议格式缺陷清单（第 3 轮一次性全量版；同类合并 + 统一负例矩阵）

> 本审计冻结在"只写一个新报告"边界内，**不实施任何修复**；每项"修复要求"指向 §9 补齐包 ID，由 caller 独立下发。严重级别口径：MAJOR = 现有断言防不住错误值/错误行为或阻断 G1 门口径核实；MINOR = 文档/索引失真，无行为风险。无 BLOCKER（未发现生产行为错误或守恒违例）。**本清单已覆盖当前全部已知可复现缺陷，无分批保留**；后续仅当出现新信息才追加，并须说明此前为何不可发现。

### DEF-1 [MAJOR] round-v1 已导出字段未绑定/硬编码（GAP:N3-B4 round 部分：20 条 census 路径 / 16 个字段级条目）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`——`:110-113`（phaseBefore/versionBefore 硬编码 Discard）、`:117`（outcome 硬编码）、`:129-131`（phaseAfter/resultOutcome 硬编码、totalDeltas 被 resultDeltas 替代）、`:134`（事件仅 Contains TsumoWin）；`:151-233`（ChankanRonReplaysExactly：eventTypes 仅 :225-226 两个 DoesNotContain、hasKanDeclared/hasRinshanDraw/legalRonSeat1/meldTypeAfterKan/phaseAfterKan/phaseAfterRon/resultOutcome/input.meldTiles 未绑定）。导出侧事实：`V5RoundGoldenExportTest.kt:275-277`。
- **原样复现（-File/-class 形式，native exit）**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.RoundReplayGoldenTests -noColor -noLogo
  → EXIT=0（Total: 6, Failed: 0）
  ```
  路径级缺口清单位移证明（层 2 规则表）：§11.6 check 命令 → `RULES round-v1 paths=200 rules=72 leaked=0` + `awk` 按 owner 筛选 → `GAP:N3-B4 26`（round 20 + tileMarkState 6，§11.6 复算命令）。
- **实际 vs 期望**：实际 = 20 条路径（16 条目）UNBOUND_HC——C# 断言存在但读硬编码常量或干脆不读导出值；期望 = N3-A1/A2 的逐值绑定（事件序列按 fixture 投影规则精确对账，两布尔经名称映射绑定到 C# Contains 结果——ScoreSettled 合法映射保留，不强造 Domain 同名枚举）。
- **证据**：fixture `tests/v5-golden/round-v1.json` SHA256 `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`；运行日志 SHA256 `B83631131381FDE2266E8C20733EA67521B1A9AD03F086B27487F07E06694D8E`；flags 变异探针（第 2 轮）6/6 PASS；§3.3 两表；FIELD-RULES round-v1 72 条规则。
- **修复要求**：§9 N3-A1 + N3-A2（= N3-B4 的 round 部分，仅测试文件）。
- **统一负例矩阵（变异验证仅在 caller 隔离环境做）**：(a) 双布尔 false→true → 应 FAIL；(b) eventTypes 插入 KanDeclared / 删去 RoundEnded / 错序 → 应 FAIL；(c) settlement.totalDeltas 篡改 → 应 FAIL；(d) meldTypeAfterKan "PON"→"CHOW"、legalRonSeat1 true→false → 应 FAIL；(e) phaseAfterKan/phaseAfterRon/resultOutcome 逐值错值 → 应 FAIL。
- **验收断言**：(a) 两测试对事件类型**精确序列**断言（投影注释在案）；(b) §3.3 两表全部 UNBOUND_HC 行变为逐值绑定或显式映射断言；(c) 负例矩阵 (a)-(e) 全部 FAIL；(d) 6 Facts 保持 PASS。

### DEF-2 [MAJOR] cheat-v1 case 级 53（D50+B2+E1）无/弱黄金行为对照 + 路径级 33（B1/B3/B4cheat/B5）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`——行为级黄金对照 **24/75**（A16+B2+C6=23 + E1 存在级 1：#36 :480 注释级）；其余 **50**（D 桶，§3.4 表标 D 者）仅经 `Handler_Sections_CoverEveryRejectionReason`（:339-365）做 reason 映射完备性；B2（#6 同族码、#67 仅 outcome :539-544）；路径级：tileMovement 13（GAP:N3-B1）、goldenInputs 树 6（GAP:N3-B3：tileMark 0/20、beamSwap 0/23）、tileMarkState 6 字段（GAP:N3-B4 cheat 部分）、ledger 4 子键 outcome/exception 8 叶（GAP:N3-B5）。
- **原样复现（全部 -File 形式，native exit；工具源均内嵌于本报告，提取见 §11.10）**：
  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/case-refs.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3
  → EXIT=0（quoted tileMarkHandler=7 sleeveBloomHandler=11 beamSwapHandler=6 total=24；goldenInputs tileMark=0/20 sleeveBloom=7/32 beamSwap=0/23；buckets A=16 B=2 C=6 D=50 E=1；rows=75）
  powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/probes-state-fields.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3
  → EXIT=0（P1 alreadyUsed=1 FAIL[正控制] / P2 chooseFirst=0 / P3 stateFields=0 / P4 ledgerMarkers=0）
  cd D:/majiang-wt/n3-golden-field-audit
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo
  → EXIT=0（Total: 16, Failed: 0）
  ```
- **实际 vs 期望**：实际 = D50 的 outcome/reason 仅被映射表消费、B2 部分绑定、E1 存在级、33 条路径未消费（P3/P4 变异全 PASS）；期望 = §9 N3-B3(a)-(d) + N3-B1/B3/B4/B5 全覆盖——**所有已导出可比较未绑定内容保留缺口直至绑定**；D-β 4 例以等价类断言 + 映射注释 + owner 批准落地（真正不可比处显式说明语义与批准责任，不沉默移出分母）。
- **证据**：fixture `tests/v5-golden/cheat-v1.json` SHA256 `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；运行日志 SHA256 `4783C3761FF0F2360B626A1B03B317DC43ACBD7BABAFCD8F3CDFE95B7D721AF4`；4 探针日志/变异 fixture SHA（§11.8 表）；case-refs.tsv/summary SHA（§11.7）；§3.4 逐条 75 行表。
- **修复要求**：§9 N3-B3（case 级）+ N3-B1/B3/B4(cheat 部分)/B5（路径级），仅测试文件。
- **统一负例矩阵**：(a) 6 个 Allowed 主路径 case 任一副作用/事件形状错值 → 应 FAIL；(b) sb-choose-first outcome→Rejected + reason→WRONG_PHASE → 应 FAIL；(c) 任一 stale case 的 stale 判定翻转 → 应 FAIL；(d) #70 requestException 缺失/错型 → 应 FAIL；(e) D-β #19/#5/#18/#20 的映射断言值错置 → 应 FAIL；(f) tileMarkState 6 字段/ledger 4 子键 outcome·exception 篡改 → 修复后应 FAIL（现 PASS = 探针 P3/P4 实证）。
- **验收断言**：(a) §3.4 表 D50+E1 全部有行为级断言或显式映射断言；(b) B2 升级后逐值绑定；(c) 负例矩阵 (a)-(f) 全部 FAIL；(d) 现有 16 Facts 保持 PASS；(e) **无门径收窄**：本缺陷的关闭标准 = §3.4 分桶清零（绑定或显式映射）+ 33 条 GAP 路径清零，不以"至少 N 个 Allowed + 代表拒绝"替代。

### DEF-3 [MINOR] CheatGoldenTests.cs:638-642 差距声明与源码事实不符（mv-swap 定级；第 3 轮全量内嵌，不再引用第 1 轮）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs:638-642`——注释声称 mv-swap-drawn-with-wall-backfill 对应的"摸牌实体置换 + 旧墙槽回填"法律**在 C# 侧无入口**。
- **原样复现**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  sed -n '638,642p' dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs
  → 注释文本与 §7 五层链第 4/5 条矛盾（ChooseSleeveBloom:376 即入口）
  grep -n "SwapHandWithWall" dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs | head
  → 命中 SleeveBloomSecondChoice_BindsToGoldenInputs（:748-757 一带）已行为重放摸牌实体置换
  ```
- **实际 vs 期望**：实际 = "无入口"表述与源码事实不符（入口存在且已被行为重放）；期望 = 注释更正为准确映射（N3-B2）：入口 `ChooseSleeveBloom:376`、`ChooseBeamSwap` 拒绝 drawn 实体 = V4 生产对齐（`ApplicationCoordinator.kt:2350`）、地址落点差按 INFO-2 记录为表示差。
- **证据**：§7 五层链（V4 法律/生产可达/导出/C# 实现/拒绝门，锚点逐行在案）；`SleeveBloomSecondChoice_BindsToGoldenInputs:748-757`。
- **修复要求**：§9 N3-B2（仅注释）。
- **验收断言**：注释与 §7 五层链一致；结构断言保持原样（不因注释更正改变断言强度）。

### DEF-4 [MINOR] catalog G-049 coverage=none 与仓库现状不符（ai-v1 索引过时；第 3 轮全量内嵌，不再引用第 1 轮）

- **准确位置**：`tests/v5-golden/catalog-v1.json`（SHA256 `16f7fd7f980398388e4e5da45a3d5fb755c19990915f403987094617e90e891b`）G-049 行 `coverage=none`；同源 `V5-0-GOLDEN-MANIFEST.md` 无 ai-v1 行；catalog `fixtures[]` 数 = 5。
- **原样复现**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  grep -o '"coverage": "none"' tests/v5-golden/catalog-v1.json | wc -l   → 33（含 G-049）
  grep -c 'ai-v1' tests/v5-golden/catalog-v1.json                        → 0（fixtures[] 五份，无 ai-v1）
  ls tests/v5-golden/ai-v1.json && sha256sum tests/v5-golden/ai-v1.json
  → E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9（存在且被消费）
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiReplayGoldenTests -noColor -noLogo
  → EXIT=0（Total: 3, Failed: 0；run-ai.log SHA256 91CC7A2918DB3E49119196F28979FBD52D23B115B9980576539FA89D420829B9）
  ```
- **实际 vs 期望**：实际 = G-049 记 none，但 ai-v1 存在且 Replay/Submit/拒绝码/动作绑定被真实 C# 消费（§3.6）；期望 = 按 §3.6 消费边界重算 **partial**（非 full：后态差分缺位 + mappingBasis/mappingLimits 3 条未绑定）。是否把 ai-v1 纳入 fixtures[]/manifest = §9 N3-C1 的 caller 裁决项（本审计不改 catalog）。
- **证据**：catalog/ai-v1 fixture SHA（§11.9）；run-ai.log SHA（§11.2）；§3.6。
- **修复要求**：§9 N3-C1（caller 裁决口径）。
- **验收断言**：catalog/manifest 与"仓库现存 fixture 清单"一致（含或显式排除 ai-v1 的书面理由）。

### DEF-5 [MAJOR] 已导出字段未绑定族（scoring/shuffle/physical/ai/tileMovement/goldenInputs）与"fixture 内部结构误判为生产行为差分"的结论性错误（第 3 轮扩展为全部 GAP 路径）

- **准确位置**：`ScoringGoldenTests.cs`（`CompareWinSample` :207-242 不读 `expected.status` 与 `input.extraHan`；`CompareNoWinSample` :247-250 不读 doraIndicators/uraDoraIndicators/honba/riichiSticks；`tags` 全文 0 次引用）；`ShuffleGoldenTests.cs`（`deadWallSegmentEqual` 全文 0 次引用；`ExportedWallsSatisfyPhysicalLaws` :160-175 为导出数组内部检查）；`CheatGoldenTests.cs:260-261`（snapshotRoundTrip 布尔信任）+ tileMovement 13 条 + goldenInputs 树 6 条；`PhysicalGoldenTests.cs`（boundaries/boundarySamples/invalid/csharpExceptionMapping 24 条；`:56/:61/:663-666` 归一化 SHA 口径）；`AiReplayGoldenTests`（mappingBasis/mappingLimits 3 条）。
- **原样复现**：§11.8 探针命令（P3/P4 + 第 2 轮 5 探针）；§11.6 check 命令（`RULES-CHECK-OK total-paths=1516 rules=417`）；`grep -c 'tags' dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` → `0`；`grep -c 'deadWallSegmentEqual' dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenTests.cs` → `0`。
- **实际 vs 期望**：实际 = GAP 87 条路径（§3.0.1 分布）当前变异后 PASS——探针实证 6 条面（win status/noWin 四字段/deadWallSegmentEqual/sb-choose-first/tileMarkState 6 字段/ledger 8 叶）；期望 = §9 N3-E1/E2/B1/B3/B5/B6 逐包绑定、tags/mappingLimits 类元数据显式排除声明、各 fixture 判级以 §3.0.1 FIELD-RULES 八类为准（行为差分/输入绑定/语义映射/内部结构/未绑定/硬编码未绑定/元数据/容器），**禁止"fixture 内部结构"与"生产行为差分"混称**。
- **证据**：探针日志/变异 fixture SHA（§11.8 表）；census 路径行（`$.winSamples[*].expected.status string instances=48`、`$.winSamples[*].tags array instances=48 elements=54`、`$.cases[*].deadWallSegmentEqual boolean instances=32` 等，`*-full-paths.txt` 逐条在案）；FIELD-RULES 417 条规则（§3.0.1）。
- **修复要求**：§9 N3-E1 + N3-E2 + N3-B1/B3/B5/B6（全部仅测试/文档文件）；分类表述以本报告 §3 为准同步至任何下游盘点。
- **统一负例矩阵**：(a) win 首例 status win→noWin → 修复后应 FAIL；(b) deadWallSegmentEqual 首例 true→false → 修复后应 FAIL；(c) tags 数组注入标记 → 绑定或显式排除后行为确定（FAIL 或明确不适用声明）；(d) 正控制 totalPoints 错值 → FAIL（已实证）；(e) tileMarkState 6 字段与 ledger 8 叶篡改 → 修复后应 FAIL（现 PASS = P3/P4）。
- **验收断言**：(a) 探针面在修复后全部 FAIL；(b) tags/mappingLimits 的分母可见性成立（绑定或显式声明）；(c) §3.0.1 FIELD-RULES 与 §11.5/§11.6 双层校验 exit 0 维持（修复后重跑本报告工具链即得）。

### 语义映射 INFO 记录（非缺陷，禁止后续轮次误报为缺陷）

- **INFO-1**：`chankanRon.expected.hasKanDeclared/hasRinshanDraw` ↔ C# 终局事件列表 `Contains("KanDeclared")`/`Contains("RinshanDrawn")`——同语义、可绑定，唯一差异是名称映射 RinshanDraw→RinshanDrawn。补齐 = §9 N3-A2 的等值绑定断言；映射注释仅说明名称映射，**不构成豁免**（判级按 §1.4 保守原则维持 UNBOUND_HC 直至绑定落地）。
- **INFO-2**：mv-swap-drawn-with-wall-backfill 被置换墙牌落点地址（V4：指针下方死区旧墙槽 ↔ C#：摸牌让出的手牌槽）——净归属与 136 守恒等价、地址无规则消费，**不可机械 1:1**；以映射注释记录（N3-B2 附属）。批准责任：P1.4 owner。
- **INFO-3**：役种 41/42 口径（D-01）——DEEPSEEK 计划 `:588` 已预登记"冻结黄金前必须确认"，属规则裁决轨，非字段缺口；G-019 的 partial 定性维持。
- **INFO-4（第 3 轮新增）**：ledger `snapshotRoundTrip` 的 Entries 级对照**当前无对象**——fixture snapshot 子键仅含 `balancesEqual/entriesEqual` 两布尔，无 golden Entries 数组；"绑定 golden Entries"需先在 exporter 侧扩导出（catalog 语义变更）。在扩导出落地前，缺口的可执行形态 = N3-B5 的 8 条 outcome/exception 叶绑定 + Entries 对照注记留痕（G-041），批准责任：caller。

---

## 14. 第 3 轮返修映射（R2-1..R2-3 → 章节 → 证据 → 状态）

| 项 | caller 缺陷摘要 | 修复落点 | 关键证据 | 状态 |
|---|---|---|---|---|
| R2-1a | COVERAGE 只验前缀计数；须拆分"路径枚举覆盖"与"字段消费语义映射"两层，内嵌机器可展开字段规则表（全 leaf、分类、锚点、owner、补齐文件、依赖），checker 须对"仅计数副本/删映射/无 C# 锚点"FAIL | **§1.3 两层口径、§1.4 八类+锚点文法、§3.0.1 FIELD-RULES 417 规则块**（全 1516 路径、7 列 TSV、first-match-wins、GAP:/meta: 文法）、**§11.6 verify-mapping**（emit/check/negative 三模式）+ 三负例全部 NEGATIVE-DETECTED（strip-anchors=254 ANCHOR-C# / drop-rule=1 LEAK / anchorless-rule=1 ANCHOR-C#） | RULES-CHECK-OK total-paths=1516 rules=417（check-run.log `34ffcf3d…`）；负例日志 3 份 SHA（§3.0.1/§11.6）；规则表独立 TSV SHA `7d28a049…`/`aab3f4cf…` | **已修**；§11.10 另从报告代码块重建复算（闭环） |
| R2-1b | "tileMarkState 3/3 子键行为差分"错误——6 个未绑定字段变异后 PASS | §3.4 tileMarkState 行改写（SEM_MAP 1 + UNBOUND_HC 6 + OUT_DIFF 17）；GAP:N3-B4 cheat 部分；§9 N3-B4 列 6 字段名；§10 行 9；§13 DEF-2 | 探针 P3 六字段全篡改 → 16/16 PASS exit 0（log `8e26a41c…`，变异 fixture `816872C5…`） | **已修** |
| R2-1c | "ledger 前 8 子键行为差分"掩盖 outcome/exception 未绑定叶 | §3.4 ledger 行改写（SEM_MAP 4 + IN_BIND 26 + UNBOUND_HC 8 + OUT_DIFF 15）；GAP:N3-B5；§9 N3-B5；§10 行 10；§13 DEF-2/DEF-5 | 探针 P4 四子键 outcome/exception 全篡改 → 16/16 PASS exit 0（log `d173fed0…`，变异 fixture `F74BECD5…`） | **已修** |
| R2-1d | §3.2 "95 条中 94 消费"算术无效（分母混入容器/元数据）；须逐类分母 + 审批责任 | §3.2 重写：winSamples 95 = IN_BIND 32/OUT_DIFF 50/SEM_MAP 1/UNBOUND 2/META 10 逐类表；noWinSamples 42 = 27/3/4/8；正式撤回该算术；tags 审批责任 = N3-E1(b) caller 裁决 | FIELD-RULES 按列筛选复算命令（§11.6）；§3.0.1 统计表 | **已修** |
| R2-1e | §9 缺 tileMarkState/ledger 未绑定字段包；N3-E1 无 ledger 文件；fixture 只有布尔无 golden Entries 数组 | §9 新增 N3-B4（tileMarkState 6）+ N3-B5（ledger 8，落地文件明确）；§3.4/§13 INFO-4 改写：snapshot 子键仅两布尔、无 golden Entries 数组可绑——Entries 级对照需 exporter 扩导出（catalog 语义变更，caller 裁决），当前可执行形态 = B5 八叶绑定 + G-041 注记 | §11.8 P3/P4；census cheat 路径行；§3.4 ledger/tileMarkState 两行 | **已修** |
| R2-2a | DEF-2 内嵌 -Command 长命令解析失败；须 -File 形式且 exit 0 | §13 DEF-2 复现命令全部改 -File（case-refs.ps1 / probes-state-fields.ps1 / CheatGoldenTests 三条，native exit 在案）；DEF-1/DEF-3/DEF-4 同步 -File/-class 化 | §11.7/§11.8 实测 exit 0 日志 | **已修** |
| R2-2b | §11 须内嵌全部参数化工具源（代码块 + 提取命令），工具收 -Repo/-Evidence 参数；空 TEMP 重建须复现 1516 路径/10238 case | **§11.4/§11.5/§11.6/§11.7/§11.8 六个工具源全文内嵌**（`<!--TOOL:…:BEGIN/END-->` 标记 + §11.10 awk 提取命令）；六工具统一 `-Repo/-Evidence` 参数面；**§11.10 自抽取闭环**在空 TEMP 重建并复算（census 1516/scoring 10238、coverage verify、mapping check、case-refs、探针、CheatGoldenTests），实测证据表见 §11.10 | 工具 SHA 六份（§11 各节头部）；§11.10 LOOP 实测表 | **已修** |
| R2-2c | build-domain/build-ai 日志 SHA 缺失；§11.5 verify 无全日志路径/哈希；§13 DEF-3/DEF-4 引用已删除的第 1 轮文件 | §11.1 补第 3 轮重建日志全 SHA（`8ee3b3a7…`/`47049454…`，0 警告 0 错误）；§11.5 补 emit/verify 全日志路径+SHA（§11.10 证据表）；DEF-3/DEF-4 全量内嵌（位置/复现/实际 vs 期望/证据/修复/验收自足，不再引用第 1 轮）；§11.3 第 1 轮存档 SHA 第 3 轮重测核对 | build 日志 R3 实测；§11.3 重测命令与结果；DEF-3/DEF-4 内嵌全文 | **已修** |
| R2-3a | case #23 sb-activate-already-used 实为已绑定（:486-490 → :557；reason→WRONG_PHASE 应 1 FAIL exit 1）→ 桶 A 非 D | §3.4 表 #23 改 **A**（含绑定链与正控制实证）；§2.1/§3.4 汇总改 A16；§10 行 12 正控制② | 探针 P1：`Handler_SleeveBloom_RepresentativeCases_ReplayAgainstCheatSubmission [FAIL]` @ :557，Total:16 Failed:1 exit 1（log `92ba5f47…`，变异 fixture `7516BD4C…`）；与 caller E 目录 positive-control 同结论 | **已修** |
| R2-3b | 分桶总数应为 quoted 7/11/6=24、**A16+B2+C6+E1+D50=75（D-α46/D-β4）** | §3.4 分桶汇总改为机器复算版（A16 清单含 #23；D-α46 = Allowed 6 + Stale 6 + reqEx 1 + 其余 Rejected 33；D-β4）；机器真值 = case-refs.ps1（§11.7，rows=75） | case-refs-summary.txt（`e8303afc…`）：buckets A=16 B=2 C=6 D=50 E=1；§4 G-034/G-035/G-038 同步 | **已修** |
| R2-3c | §4 G-035 应剔除 #23 → 21（=D20+E1）；§4 合计须含 B/E | §4 G-035 = 21（清单去 #23）；§4 合计 = cheat 53 case 级（D50+B2+E1）+ 33 路径级 + round 20 路径/16 条目 + G-041 | §3.4 表逐条；case-refs.tsv 75 行 | **已修** |
| R2-3d | "复用现有 goldenInputs 构造"不实（tileMark 0/20、beamSwap 0/23、sleeveBloom 7/32）；D 构造需 V4 源锚点 | §3.4 引用侧口径行改写（goldenInputs 实测覆盖率）；§9 N3-B3(a) 改为"按 V4 exporter 源锚点（V5CheatGoldenExporterTest.kt 对应 case 构造段）重建输入并留锚点注释"；goldenInputs 树 6 条路径 = GAP:N3-B3 | §11.7 case-refs 输出（goldenInputs 0/20·7/32·0/23） | **已修** |
| R2-3e | "G1 批准另需 G5 完成"（§9 :606）与 §12 同类表述系误引——任务书 :107-108 无 G1 前置 G5 条款 | §9 DAG 尾行删除；§12 第 5 条改写：G1 门口径完全由任务书既有规定与 caller 汇总复核决定，本报告不增设任何批准前置 | 任务书 :107-108（只读核对）；§9/§12 现文 | **已修** |

**未修事项与风险（显式清单）**：
1. **不实施** §9 任何补齐项（任务边界：只修报告）；N3-A*/B*/E* 全部为建议，风险 = 87 条 GAP 路径缺口继续存在直至 caller 下发。
2. **catalog 口径未采纳**（不改 catalog）：Δ29 与 G-049 重算均为 N3-C1 的 caller 裁决项。
3. **D-β #19 在岗 deadline**：Domain 输入不可表示，等价类断言 + owner 批准为建议方案，未获批准前该例维持"缺口（需语义映射）"状态。
4. **tags/mappingLimits 元数据**：绑定或显式排除由 caller 裁决（N3-E1(d)/N3-B6），裁决前 tags 维持"未绑定"注记计入 §9 分母可见性说明。
5. **ledger Entries 级对照**：fixture 无 golden Entries 数组（INFO-4），需 exporter 扩导出方可绑定；此前维持 G-041 注记 + N3-B5 八叶绑定形态。
6. 第 1/2 轮 caller 认可方向（mv-swap 五层、ChooseDealerEnd、45/45、哈希口径表）未改动，仅按本轮口径补充引用。
