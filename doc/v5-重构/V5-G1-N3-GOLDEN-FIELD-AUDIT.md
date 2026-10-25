# V5-G1 N-3 前置独立审计报告 — 黄金样本字段级映射与覆盖口径核查（第 2 轮返修版）

- **审计执行**：Claude Code（本 agent），独占 worktree `D:/majiang-wt/n3-golden-field-audit`，分支 `wt/v5-n3-golden-field-audit`
- **冻结基线**：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（PR19 最新已发布头；工作树 status clean，HEAD 即该提交）
- **审计对象**：V5-1-REVIEW §7.1 N-3 [MAJOR]（G-028a 事件序列 + mv-swap-drawn-with-wall-backfill + 20 项 partial 的断言边界），以及"已导出黄金样本对齐率为 100%"这一 G1 门口径的字段级核实
- **交付路径**：`doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md`（任务 expectedFiles 指定路径；第 1 轮曾误用 `N3-GOLDEN-FIELD-AUDIT-REPORT.md`，本轮已 `git mv` 统一，无第二拷贝）
- **版本史**：第 1 轮 = commit `5ba62bd`（被 caller 判 REWORK_REQUIRED，R1-1..R1-5）；第 2 轮 = 本文件，R1-1..R1-5 逐条映射见 §14
- **报告日期**：2026-10-25
- **性质声明**：本报告只产出审计证据与最小补齐包建议；**不实施任何 P0.1/已接受项开发，不改 catalog/manifest/fixtures/生产或测试代码，不改固定头已接受 G1 盘点报告**。本报告组件完成 ≠ N-3 关闭 ≠ G1 批准。

---

## 0. 前置核实（审计开工前实拍）

| 项 | 值 | 验证命令 |
|---|---|---|
| HEAD（开工时） | `8254c9b783740ddf519cebc15bb50a8f9974cf58` | `git rev-parse HEAD` |
| 分支 | `wt/v5-n3-golden-field-audit`（独占） | `git branch --show-current` |
| V4 唯一生产 | Kotlin `engine/`；V5 `dotnet/` 生产性 false（catalog baseline `v5Production:false`） | catalog-v1.json baseline |
| G1 | 不批准（固定 8ee22eb 报告 7 PASS/2 FAIL） | CODEX-V5-G1-FIXED-HEAD-REPORT-ACCEPTANCE.md |
| G5 | 未完成 | 同上 |
| 只读文件 | `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`（未复制未提交未修改） | — |
| 禁触文件 | 主 checkout 未提交的 CLAUDE.md、PROMPTS.md、任务书/协议、Unity 环境/Resources/Burst | 未复制、未提交、未修改 |

---

## 1. 审计方法与可复算口径（第 2 轮：全量 census）

### 1.1 第 1 轮方法局限的正式记录（R1-3）

第 1 轮对 scoring-v1 使用 `scoring-shape.ps1` 做"形状提取"：**对 7 组各取首/中/末样本、嵌套数组最多展开 3 个元素**，并以"7 组顶层键集 `distinctTopLevelKeySets=1`"佐证形状齐性。该证据**不充分**：

- 顶层键相同 **≠** 递归形状齐性。全量 census（§1.2）实测 scoring-v1 归一化路径 419 条，其中第 1 轮抽样只覆盖 405 条，**实漏 14 条真实路径**：
  - `$.winSamples[*]` 组 84→**95**，缺 11：`$.winSamples[*].input.melds[*]`、`$.winSamples[*].input.melds[*].calledTile`、`$.winSamples[*].input.melds[*].fromPlayer`、`$.winSamples[*].input.melds[*].tiles`、`$.winSamples[*].input.melds[*].tiles[*]`、`$.winSamples[*].input.melds[*].type`、`$.winSamples[*].input.doraIndicators[*]`、`$.winSamples[*].input.uraDoraIndicators[*]`、`$.winSamples[*].expected.dora.indicatorTiles[*]`、`$.winSamples[*].expected.dora.uraTiles[*]`、`$.winSamples[*].expected.dora.redTiles[*]`
  - `$.doraSamples[*]` 组 33→**35**，缺 2：`$.doraSamples[*].expected.uraTiles[*]`、`$.doraSamples[*].input.uraDoraIndicators[*]`
  - `$.randomSamples[*]` 组 50→**51**，缺 1：`$.randomSamples[*].ura[*]`（1208 叶实例）
  - 其余 4 组（noWin/fu/settlement/score）本次全量路径无漏。
- 第 1 轮报告中凡以"scoring 全字段"为前提的结论（§3.2 winSamples 行、§10"physical/scoring/shuffle 全部已导出字段错值设防"）**作废重判**（§3.2、§10）。
- 第 1 轮其余证据**不受影响并继续引用**：45/45 定向运行、5 类 C# 消费者逐行深读、V4 源锚点、v4Commit/exporter SHA 链、mv-swap 五层链、AgariYame 双侧锚点、DEEPSEEK 计划交叉核对（§1 第 1 轮方法 3-8 条）。

### 1.2 第 2 轮全量 census（R1-3 修复）

- **工具**：`FullCensus`（C# net8.0 / System.Text.Json，独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/`），对 6 份 fixture **递归枚举全部 JSON 节点，无任何抽样**（scoring-v1 全部 10238 case 逐一遍历）。
- **口径定义**（本报告全文统一）：
  - **归一化逻辑路径**：数组下标折叠为 `[*]`（如 `$.winSamples[3].score.han` → `$.winSamples[*].score.han`）；
  - **distinctPaths**：不同归一化逻辑路径数（容器与叶都计）；
  - **distinctLeafPaths**：其中 kind 不含 object/array 的路径数；
  - **leafInstances**：叶实例出现次数（同一路径多 case 累计）；
  - **groups**：路径按"首个 `[*]` 及其前缀"机械分组后的组数。
- **产出**：每 fixture `*-full-paths.txt`（每条不同路径一行：路径 + kinds + instances + 数组 elements/lenMin/lenMax）、`*-group-counts.txt`、`census-summary.txt`；全部 SHA256 见 §11.4。总量：

| fixture | distinctPaths | distinctLeafPaths | leafInstances | groups（[*]-组） |
|---|---|---|---|---|
| physical-v1 | 296 | 233 | 29750 | 46 |
| scoring-v1 | 419 | 308 | 476153 | 78 |
| round-v1 | 200 | 150 | 1163 | 113 |
| cheat-v1 | 204 | 164 | 1163 | 96 |
| shuffle-v1 | 199 | 140 | 23016 | 172 |
| ai-v1 | 198 | 152 | 1199 | 81 |

- **机器可复核的覆盖率校验**（R1-2 验收口径"机器 leaf 集合与文档归组映射覆盖率 100%"）：`verify-coverage.ps1`（emit/verify 双模式）把每条归一化路径归入其 **prefix-2 组**（`$.` + 首个组件；无组件的容器自身一组），6 份 fixture 各产出一个**完全划分**的 `组 = distinctPaths` 清单；本报告 §3.0 内嵌这 6 个 COVERAGE 块，verify 模式对报告重新计数并与块 diff，**unmapped=mismatched=extra=0 才 exit 0**（本次实测结果见 §11.5）。
- **C# 消费强度判级方法**（R1-4"字符串引用≠行为绑定"）：(a) fixture caseId/path 与测试源码字面量求差集（初筛）；(b) 对每个初筛引用**深读断言上下文**判级（黄金输入绑定 / 黄金输出对照 / 同构关系 / 仅 reason 映射 / Allowed 计数 / 纯 C# 存在未绑定 / 需其他层语义映射）；(c) 对判级结论抽**变异探针负例**独立复现（§11.6，全部 AS-EXPECTED）。
- v4Commit 真实性、exporter 源链、DEEPSEEK 计划交叉核对：沿用第 1 轮（§3.7/§3.8/§6/§7/§8）。

---

## 2. 总判定（先给结论，第 2 轮修正版）

1. **"已导出黄金样本对齐率为 100%" 不成立（字段级）**。按"已导出 expected 字段被真实 C# 断言消费"的精确口径，本轮全量 census + 逐行判级的实数：
   - **round-v1：16 个字段级条目**未绑定/弱化（roundTerminal 7 + chankanRon 9，§3.3 两表），其中**事件序列面零护栏**（G-028a 核心）；
   - **cheat-v1：75 个 handler case 中 51 个无黄金行为对照**（47 个可在 Domain 层构造绑定 + 4 个需上层/合并语义映射），另有 2 个部分绑定、1 个纯 C# 存在级未绑定黄金条目；行为级黄金对照实数 **23/75 + 1 存在级 = 24/75**（§3.4）；
   - **scoring-v1：3 条已导出路径未绑定**——`$.winSamples[*].expected.status`（变异探针 win→noWin 后 7/7 PASS）、`$.winSamples[*].tags`（48 数组/54 叶）、`$.winSamples[*].tags[*]`；randomSamples/noWinSamples 的 status 有绑定（:247/:394-407），**winSamples 的没有**（§3.2）；
   - **shuffle-v1：1 条已导出路径未绑定**——`$.cases[*].deadWallSegmentEqual`（32 实例；变异探针 false 后 4/4 PASS）；且 **32 墙×5 变体整组不是生产行为差分**，是对导出数组的 fixture 内部结构检查（无 C# shuffle 生产算法运行）；真实 C# 数学差分仅为 slotMatrix/demandVectors/overlapMatrix 三组（§3.5）；
   - **分类修正**（fixture 内部结构 ≠ 生产行为差分）：shuffle walls、ai 前后树 DeepEquals、ledger snapshotRoundTrip（布尔信任 + balances-only）、physical probeA/B 结构映射、physical provenance（**CRLF→LF 归一化 SHA**，非逐字节 raw hash，§3.1）。
   - 上述合计**不含任何 ≈**；所有分母分子可由 §11 命令与 census 文件机器复算。
2. **G-028a 定性修正（含 R1-1 更正）**：C# Domain 以 `RoundSettlement/RoundResult` 字段承载 ScoreSettled 信息是**合法表示映射**（§6 四层链）；真实缺口是 round-v1 两个重放测试**没有对事件序列做精确断言**，且 `hasKanDeclared/hasRinshanDraw` 导出布尔**当前未绑定**——这两个布尔**可语义比较**（exporter 以终局事件列表 `contains` 计算，与 C# `DoesNotContain` 同语义，仅名称映射 RinshanDraw→RinshanDrawn），第 1 轮"不可强迫同值对照"的表述**错误，已全部撤回**（§3.3、§9 N3-A2、§13 INFO-1）。变异探针双改 true 后 6/6 PASS = **覆盖缺口**，非不可比较。
3. **mv-swap-drawn-with-wall-backfill**：非 V4-only，C# 等价入口已被行为重放；缺口收窄为 m1b 交叉绑定（N3-B1）+ 注释更正（N3-B2）+ 一处不可表示的地址级表示差（INFO-2）。无需用户规则裁决、无需刷新冻结记录（§7，caller 已认可方向）。
4. **AgariYame**：行为在 C# match 层完整存在；疑虑仅保留"round 层命令入口枚举"一点（§8）。
5. **ai-v1 / G-049**：第 6 份 fixture 存在且 Replay/Submit/拒绝码/动作绑定为真实 C# 行为，但 `DeepEquals(before,after)` 是**同一 fixture 的 V4 前后树内部比对**（尤其拒绝 case），不是"C# 重放后态 vs V4 全树"差分；末 Fact 仅 DeadlineArbiter 接纳动作，不执行 RoundStateMachine。**G-049 不可据此重算 full；建议 partial**（§3.6，caller 裁决）。
6. **45/45 定向 Golden Facts 全 PASS**（native exit 0）维持：现有断言无一失败——失败不在断言结果，而在断言覆盖。本轮 5 个独立变异探针（§11.6）+ 正控制（改已绑定 totalPoints → 1 FAIL exit 1）证明"侥幸通过"判定方法有效。
7. **case 总数口径**（R1-2/R1-4 附带核实）：catalog 声明 `fixtureConcreteCases=10382`（per-fixture caseTotal：physical 6 + scoring 10238 + round 25 + cheat 81 + shuffle 32）；本审计 census 案例口径为 **10353**（scoring 10238 + cheat handler case 75 + shuffle cases 32 + round 终局重放 2 + physical 6）；**Δ=29**（round +23、cheat +6）——catalog fixture 条目只有 `id/path/sha256/bytes/caseTotal` 四字段、**未声明案例分解口径**，Δ29 作为口径差异如实上报，**不采纳、不改 catalog**；ai-v1 的 7 case 在 catalog 之外。
8. 一次性缺陷清单见 §13（DEF-1..DEF-5：3 MAJOR 面向 round/cheat/其余 fixture 的绑定缺口 + 2 MINOR 文档/索引；INFO-1..3 语义映射记录）。本审计按任务书**不实施修复**；修复经由 §9 补齐包由 caller 独立下发。§9 覆盖本报告所报**全部可比较缺口**；确属不可机械比较的项逐条给出语义与批准责任（无沉默移出分母）。

---

## 3. 六份 fixture 字段级映射总表（全量 census 版）

### 3.0 全量覆盖映射（机器可复核）

prefix-2 组完全划分（组 = 该前缀下 distinctPaths 数；6 块合计覆盖 1516 条归一化路径，verify exit 0 见 §11.5）。任何字段级结论均可由块内组 → `*-full-paths.txt` 逐条展开。

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

**catalog case 总数口径**（R1-2 附带项）：上表 6 块即全部路径的机械划分；catalog `fixtureConcreteCases=10382` 与本审计 census 案例口径 10353 的 Δ29 分解与处理见 §2.7——**catalog 口径差异按事实上报，未被本报告采纳为任何分母**。

### 3.1 physical-v1（catalog 内）

- **SHA256** `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995`；v4Commit `32f645ec…`（真实）；census 296 distinctPaths；C# 消费 `PhysicalGoldenTests`（9 Facts 全 PASS）。
- **provenance 链（R1-2(d) 修正表述）**：fixture 内嵌 `exporterSourceSha256` + `sourceHashAlgorithm` + `v4SourceHashes[3]`；C# 断言（`PhysicalGoldenTests.cs:56`、`:61`）调用 `Sha256SourceFile`（helper 在 `:663-666`）：`File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n")` 再求 SHA256——即 **CRLF→LF 行尾归一化后的 SHA256**。这与 catalog/manifest 记录的 **raw 字节 SHA256 是不同算法口径，两者不可互换**：provenance 自检证明的是"归一化源文本与 exporter 文件一致"，叠加 catalog 侧 raw hash 一致（§11.7）共同构成双链；**任何一侧都不应被表述为"逐字节比对另一侧"**。
- **组级映射**（prefix-2 组 → 判级）：

| prefix-2 组（distinctPaths） | C# 断言范围 | 判级 |
|---|---|---|
| `$.samples[*]`（249）：G-001a/b/c 身份/赤五/136 张 + G-002 20 case + G-003a 32 墙 map + digest | 全字段逐值（TileId、136 tiles、redFiveIds 19/55/91、逐墙 map、136 守恒、4352 位置） | 行为差分（V4 产物 → C# 断言消费） |
| `$.scope`（8）/`$.generationResult`（7）/`$.hashCrosswalk`（6）/`$.v4SourceHashes[*]`（3）/`$.taskPaths[*]`（1）/`$.gitStatusPorcelain[*]`（1）/其余头部标量 | provenance/清单断言（:56、:61 及上下文） | 输入绑定/自检（归一化 SHA 口径如上） |
| G-003b probeA/probeB | 结构映射（探测路径结构一致） | fixture 内部结构（probe 语义无生产对应，已声明） |
| duplicateKey | — | 不可表示（V4 MutableMap 无法构造重复键 case，NOT_CONSTRUCTIBLE） |

### 3.2 scoring-v1（catalog 内，R1-2(a)/R1-3 修正版）

- **SHA256** `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E`；v4Commit `97831c88…`（真实）；7 组 10238 case；C# 消费 `ScoringGoldenTests`（7 Facts 全 PASS）。
- **组级映射（逐字段判级，95/35/51 等计数来自全量 census）**：

| 组（case 数） | C# 断言范围（函数:行） | 已导出未绑定 | 判级 |
|---|---|---|---|
| winSamples（48） | `CompareWinSample`（:207-242）：score.*（han/fu/basePoints/rawBasePoints/rawBasePointsExact/capCode/yakumanMultiplier/isYakuman）、fu.lines 逐行、paymentPlan.components 逐项、dora 六元组（indicatorTiles/uraTiles/redTiles）、yaku 逐项（code+han+count）、honbaAddition/riichiAddition/totalHan/scoreHan/totalPoints/yakumanCount | **`expected.status`（48 实例）**——CompareWinSample 不读 status（`:247` 的 status 断言属 CompareNoWinSample）；**`tags`（48 数组）/`tags[*]`（54 叶）**——`tags` 在 ScoringGoldenTests.cs 出现次数 = 0 | 输出差分 + **2 项未绑定**（status 变异探针 win→noWin → 7/7 PASS exit 0，§11.6） |
| noWinSamples（4） | `CompareNoWinSample`（:247）：`Assert.Equal("noWin", expected.status)` | — | 输出差分 |
| fuSamples（9） | fu lines/subtotal/roundedUp 逐行 | — | 输出差分 |
| doraSamples（6） | indicator/red/total 组合 + `expected.uraTiles[*]`/`input.uraDoraIndicators[*]`（第 1 轮漏、本轮入表） | — | 输出差分 |
| settlementSamples（24） | deltas/newScores/remainingRiichiSticks 全字段 | — | 输出差分 |
| scoreSamples（147） | basePoints/capCode/isYakuman/rawBasePoints(+Exact)/yakumanMultiplier/payments×5（逐个调 C# 支付函数比对）/paymentPlans×4（components 逐项+kind+total） | — | 输出差分（最强） |
| randomSamples（10000） | `CompareRandomSample`（:372-439，status 读于 :394/:401/:407）：status/yaku 逐项/yakuHan/scoreHan/isYakuman/yakumanCount/fu/basePoints/capCode/totalPoints + 分布 meta（win/noWin/open/closed/tsumo/ron/removed/yakuman/yakuCounts）逐项对账；`ura[*]`（1208 叶实例，第 1 轮漏、本轮入表） | — | 输出差分 |

- **结论修正**（替代第 1 轮"全字段消费"）：winSamples 的 95 条路径中 94 条被真实断言消费，`expected.status`、`tags`、`tags[*]` 3 条未绑定（status 为行为面缺口，tags 为 exporter 元数据面缺口——处理建议见 §9 N3-E1）；其余 6 组**已导出未绑定 = ∅**。
- "尚未导出"类缺口（4han/30fu、28→30、单赤宝牌锚定 case）仍归 §4 表 G-013/014/015，与本节"已导出未绑定"**不混计**。

### 3.3 round-v1（catalog 内，R1-1 修正版）— 字段级缺口最集中的一份

- **SHA256** `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`；v4Commit `21a7d2c4…`（真实）；`workingTreeDirty=true`（导出时 V4 工作树有测试侧脏文件——来源链弱于 physical，如实记录）；C# 消费 `RoundReplayGoldenTests`（6 Facts 全 PASS）。
- **exporter 关键源码（R1-1 事实基础）**：`V5RoundGoldenExportTest.kt:240` 起导出收尾——`:275` `"eventTypes" to arr(events.map { j(it) })`（终局事件类名序列）；**`:276` `"hasKanDeclared" to j(events.contains("KanDeclared"))`、`:277` `"hasRinshanDraw" to j(events.contains("RinshanDraw"))`**——即两个导出布尔 = **终局事件列表是否包含对应事件**，与 C# `:225-226` 对 C# 终局事件列表 `DoesNotContain(KanDeclared/RinshanDrawn)` **同语义、同方向**，名称仅需映射 **RinshanDraw→RinshanDrawn**。fixture 中两布尔均为 `false`。第 1 轮"V4 是发生过状态旗标、不可强迫同值"的表述与 exporter 源码矛盾，**作废**。
- **roundTerminal（RG-001）逐字段绑定表**（C# = `RoundTerminalReplaysExactly` :101-148；导出值实拍自 fixture）：

| fixture 字段（导出值） | C# 绑定 | 判级 |
|---|---|---|
| input.roundId/dealer/scores/riichiSticks | ✅ 作为 P13.Dealt 构造输入 | 黄金输入绑定 |
| input.phaseBefore（"DISCARD"）/ input.versionBefore（0） | ❌ C# 硬编码 `RoundPhase.Discard` 起手（:115） | 已导出未断言 |
| settlement.resultId/winner/finalScores/remainingSticks/completedAtUtc/completedAtMonotonicMillis | ✅ Tsumo 命令载荷 | 黄金输入绑定 |
| settlement.outcome（"Tsumo"） | ❌ C# 硬编码 `RoundOutcome.Tsumo(winner)`（:119），导出串未比对 | 已导出未断言 |
| settlement.totalDeltas | ❌ C# 用 `expected.resultDeltas` 替代（:132），`settlement.totalDeltas` 未读 | 已导出未断言 |
| expected.versionAfter/resultFinalScores/resultDeltas/rejectedActions[3]/versionAfterRejections | ✅ 逐值（:129 前后区块） | 输出差分 |
| expected.phaseAfter（"ROUND_END"） | ⚠️ 硬编码 `RoundPhase.RoundEnd`（:129），未读导出值 | 断言弱化（硬编码） |
| expected.resultOutcome（"Tsumo"） | ⚠️ 硬编码 `RoundOutcomeKind.Tsumo`（:130） | 断言弱化（硬编码） |
| expected.eventTypes[3] = **[TsumoWin, ScoreSettled, RoundEnded]** | ⚠️ 仅 `Contains(TsumoWin)`（:134） | 已导出未断言（G-028a 核心） |

- **chankanRon（RG-002）逐字段绑定表**（C# = `ChankanRonReplaysExactly` :151-233；导出值实拍自 fixture）：

| fixture 字段（导出值） | C# 绑定 | 判级 |
|---|---|---|
| input.roundId/kanserSeat/ronSeat/addedTileId/eligibleSeats | ✅ P13.ChankanDealt 构造输入 | 黄金输入绑定 |
| input.meldTiles[3]（面子预置） | ❌ 导出值未比对 | 已导出未断言 |
| expected.rinshanPointerAfterKan/totalKanCountAfterKan/rinshanPointerAfterRon/totalKanCountAfterRon/winner/loser/resultId/finalScores/totalDeltas/versionAfterRon | ✅ 逐值（:231 等） | 输出差分 |
| expected.phaseAfterKan（"CHANKAN_WINDOW"）/ phaseAfterRon（"ROUND_END"）/ resultOutcome（"Ron"） | ⚠️ 全部硬编码（:221/:227），导出值未比对 | 断言弱化（硬编码） |
| expected.eventTypes[3] = **[RonWin, ScoreSettled, RoundEnded]** | ❌ 事件面仅 `:225-226` 两个 `DoesNotContain` | 已导出未断言（G-028a 核心） |
| expected.meldTypeAfterKan（"PON"） | ❌ | 已导出未断言 |
| expected.legalRonSeat1（true） | ❌ 窗口资格由 eligibleSeats 间接走过，导出布尔未比对 | 已导出未断言 |
| expected.hasKanDeclared（false） | ❌ 未绑定。**可语义比较**：= fixture 终局事件含 KanDeclared 与否 ↔ C# 终局事件表 `Contains("KanDeclared")`；变异探针双改 true → 6/6 PASS exit 0（§11.6） | **语义可比·名称映射·当前未绑定** |
| expected.hasRinshanDraw（false） | ❌ 未绑定。同上，C# 侧名称为 **RinshanDrawn** | **语义可比·名称映射·当前未绑定** |
| 状态哈希守恒 | ✅ hashBefore 前后比对 | 输出差分（等效物理守恒） |

- **字段级缺口合计 = 16**：roundTerminal 7（phaseBefore/versionBefore/outcome/totalDeltas + phaseAfter/resultOutcome/eventTypes）+ chankanRon 9（meldTiles/eventTypes/hasKanDeclared/hasRinshanDraw/legalRonSeat1/meldTypeAfterKan/phaseAfterKan/phaseAfterRon/resultOutcome）。第 1 轮"18"的计数作废（彼时把 2 个语义可比布尔按"需注释"另立、且跨表重复计 eventTypes）。
- **事件序列投影规则（R1-1：精确序列建议必须由 fixture 投影，不得手写另一份预期）**：N3-A1/A2 的 C# 期望序列 = fixture `eventTypes` 按 §6 已文档化表示映射投影（`ScoreSettled` 信息由 `RoundSettlement/RoundResult` 字段承载 → 从事件序列折叠；其余类名同名保留）→ roundTerminal 投影为 `[TsumoWin, RoundEnded]`、chankanRon 投影为 `[RonWin, RoundEnded]`；断言注释必须引用 fixture 原始序列与投影规则，不引入外部手写预期。
- 其余组：matchProgression 16 case 全字段消费（config 10 + input 9 + expected 10，含 allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame/tieBreakSeats/tenpai/transferSeat）；decisionClock 41 ops 全走 + ≥35 断言；controlEpoch 6 ops 全走；unexportedSamples[3] 显式声明未导出项。**本节未列"弱化/未绑定"的行均为逐值绑定，无沉默省略。**

### 3.4 cheat-v1（catalog 内，R1-4 精确分桶版）

- **SHA256** `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；v4Commit `a96c195c…`（真实）；C# 消费 `CheatGoldenTests`（16 Facts 全 PASS）。
- **75 个 handler case 逐条分桶**（tileMark 20 + sleeveBloom 32 + beamSwap 23；桶定义：**A** 黄金输出行为重放（真 C# 入口 + outcome/reason 逐值对照）、**B** 部分绑定、**C** goldenInputs 驱动全链路重放、**D** 无黄金行为对照（仅 reason 映射完备性覆盖，`Handler_Sections_CoverEveryRejectionReason` :339-365）、**E** 纯 C# 存在断言但黄金条目未绑定。分桶依据 = 源码字面量差集初筛 + 逐断言深读判级；机器复算命令见 §13 DEF-2）：

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
| 23 | sleeveBloom | sb-activate-already-used | Rejected | SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW | D-α |
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
| 36 | sleeveBloom | sb-choose-first | Allowed | null | **E**（:480 仅注释 + 独立成功断言，无 `Case(...)` 读取该 golden case；变异探针 outcome→Rejected+reason→WRONG_PHASE 仍 16/16 PASS） |
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

- **分桶汇总（精确，与 §4/§9/§13 一致）**：**A=15**（tm6 + sb4 + bm5）；**B=2**；**C=6**；**E=1**；**D=51**（D-α 可构造可绑定 **47**：含 **Allowed 主路径 6**（#1/#53/#63/#66/#73/#75）、StaleOffer 6（#39/#40/#51/#52/#72/#74）、requestException 1（#70）、其余 Rejected 34；**D-β 需上层/合并语义映射 4**：#5/#18/#19/#20）。合计 15+2+6+1+51 = **75**。
- **引用侧口径对齐 caller 数字**：源码字面量引用（含 :480 对 sb-choose-first 的注释级引用）= tileMark **7**、sleeveBloom **11**、beamSwap **6**，合计 **24**；未引用 **13/21/17** 合计 **51**。字面量引用 ≠ 行为绑定：24 中行为级黄金对照实为 **A15+B2+C6=23**，sb-choose-first 属 E（存在级）。**重叠不重复计数**：D51 与 G-033/034/035/036/037/038/039 各行是"同一 case 集的不同视图"，§4 表只作视图引用，不再产生新计数。
- **D-β 4 例的真实 C# 对应层（R1-4 要求分层）**：
  - #19 tm-deadline-expired：`CheatTurnContext`（`CheatSubmission.cs:12-25`）**无 deadline/decision 字段**；C# Domain 的 `DEADLINE_EXPIRED` 仅存在于 off-turn 选择路径 `ChooseOffTurnSleeveBloom`（:553-560，测试侧 :849 已走，对应 #50 sbo-choose-expired 可构造）。**在岗 tileMark 的 deadline 到期在 Domain 输入层不可表示**——绑定需"等价类断言 + 映射注释 + owner 批准"（§9 N3-B3(b)）。
  - #5/#18/#20（DECISION_MISMATCH/NO_ACTIVE_DECISION）：C# 对应层为请求校验的 **InvalidRequest 合并语义**，非独立拒绝路径——绑定 = 对实际合并 outcome 断言 + 映射注释。
  - #5 特记：caseId 带 "wrong-phase" 但导出 reason 实为 **DECISION_MISMATCH**（分桶以导出 reason 为准）。
- **ledger 节（R1-2(d) 修正）**：9 子键中 8 个为行为差分（initialPoints/chargeOnce/replaySamePayload/conflictDifferentPayload/insufficientFunds/overflow/invalidSeat/flowSummary）；**snapshotRoundTrip = 对 fixture 布尔 `balancesEqual/entriesEqual` 的 `Assert.True`（:260-262）+ C# restored 仅逐余额比对，`restored.Entries` 无逐项黄金比较** → 消费强度 = 布尔信任 + balances 差分，**不是全叶差分**（处理建议 §9 N3-E1 注记；不与前 8 子键混称"9/9 行为差分"）。
- **tileMarkState 3/3 子键**（registerDealtRound/addGroup/expiry 全链版本断言 :280-329）——行为差分（capacity 两 case 的 handler 层缺口已按 #12/#13 计入 D，不重复计）。
- **tileMovement 6/6 caseId**：mv-swap-hand-with-wall-direct、mv-reservation-delivery、mv-reservation-delivery-then-beam、mv-rinshan-and-drawn-swap、mv-swap-hand-with-dead-wall 行为重放；**mv-swap-drawn-with-wall-backfill 仅结构**（§7 专查）。
- **侥幸通过判定**：reason 映射表 31 V4 reasons→C# CheatErrorCode（`ReasonMap` :43-78）是完备性断言，但**映射正确 ≠ 行为正确**：D51 的真实 C# 行为若与 V4 相悖，现有断言不设防；6 个 Allowed 主路径 case（含事件/副作用形状）风险最高，E（sb-choose-first）已由探针实证。

### 3.5 shuffle-v1（catalog 内，R1-2(b) 修正版）

- **SHA256** `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`；v4Commit `37b4dd61…`（真实）；C# 消费 `ShuffleGoldenTests`（4 Facts 全 PASS）。
- **真实 C# 数学差分（生产算法被调用并逐值对照）**：
  - slotMatrix 16 组逐值 ↔ `ShuffleSlots`（:75，含 DeadWallStart 越界禁令）；
  - demandVectors 6×34 逐值（1e-12）↔ `ShuffleTargets.Demand`（:107）；
  - overlapMatrix 6×6 双精度逐值 ↔ `ShuffleTargets.Overlap`（:121）+ **测试内独立冻结硬值双锚**（2/√117、3/√39、4/√52、0、1，:127-131）。
- **fixture 内部结构检查（非生产行为差分，R1-2(b) 修正）**：`ExportedWallsSatisfyPhysicalLaws`（:160-175）对**导出的墙数组**做 136 唯一/置换/赤五恒 3/恒等律/死山 122..135 隔离检查——**没有运行 C# shuffle 生产算法生成墙**；`rngGapNote`/D-09 声明的 RNG 差异意味着 V4 种子与 C# 不可能同墙，这类声明**不等于**"该组有生产行为对齐"。identity laws/booleans（:179-184）与 threeCasterChanged（:192）同为 fixture 内部/跨字段检查。
- **已导出未绑定**：`$.cases[*].deadWallSegmentEqual`（32 布尔实例）——`deadWallSegmentEqual` 在 ShuffleGoldenTests.cs 中**从未被读取**；变异探针第 1 例 true→false → 4/4 PASS exit 0（§11.6）。

### 3.6 ai-v1（catalog 外第 6 份，R1-2(c) 修正版）

- **SHA256** `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；v4Commit `205506f0…`（真实，恰为 catalog 冻结头）；7 case；C# 消费 `AiReplayGoldenTests`（3 Facts 全 PASS，经 csproj `<None Include>` 拷贝读取）。
- **真实 C# 行为（保留）**：7/7 case 经 `AiDecisionService.Replay + Submit`（`ThrowingStrategy` 保证绝不运行启发式）：录制意图（recordedIntent 绑定防换动作）、拒绝码（AI-002-old-round/AI-003-old-decision/AI-004-old-epoch/AI-005-wrong-seat/AI-006-candidate-not-legal/AI-007-exact-deadline/AI-001-legal-intent）、v5.authorityMapping 六种 mode 构造校验（输入/配置面绑定）。
- **fixture 内部证据（非 C# 后态差分，R1-2(c) 修正）**：`AssertV4Evidence` 的 `DeepEquals(before,after)`（:571/:607）比较的是**同一 fixture 的 V4 前后树**（尤其拒绝 case 的 before==after 期望），**不是**"C# 重放后实际状态 vs V4 全树"差分；接受 case 仅核若干关系字段（receiptStatus/eventCount/stateChanged）；末 Fact 仅 DeadlineArbiter 接纳动作，**不执行 RoundStateMachine 产生 C# 后态**。
- **索引缺口与 G-049**：catalog `fixtures[]=5`（notice 明示 five accepted V4-exported fixtures）、manifest 无 ai-v1 行、G-049 `coverage=none` 与仓库现状不符——ai-v1 恰为 recorded-intent + stale-AI submission fixture。**但按 §3.6 消费边界，G-049 不可重算 full；建议 partial**（真实行为面存在 + 后态差分缺位）。是否扩口径由 caller 裁决（§9 N3-C1）。

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

**判定**：产出五份 catalog fixture 的 exporter 文件与冻结头仓库内现存文件**SHA256 逐一一致（5/5）**——fixture 确为仓库内当前 exporter 所声称的代码产物。叠加 §3.1：physical-v1 的 C# 自检链是 **CRLF→LF 归一化 SHA** 口径，与 catalog raw hash 双链互补、不可互换表述。

---

## 4. 20 项 partial 严格拆分（第 2 轮精确计数版，无 ≈）

分母定义：**"已导出未断言"= fixture 内真实存在、且存在可行 C# 对应断言（或在 D-β 情形需显式映射批准）的字段/case**；无 V4 导出物归"尚未导出"；不可构造或不可机械比较归"不可表示/语义映射"。**D51 与下列 handler 行是同一 case 集的视图关系，不重复计数**（每 case 只有一个桶，见 §3.4 表）。

| ID | owner | 已导出但未断言（精确） | 尚未导出（精确） | 不可表示/语义映射 |
|---|---|---|---|---|
| G-003b | P1.1 | — | — | probeA/probeB 探测语义无生产对应（结构映射已做）；duplicateKey NOT_CONSTRUCTIBLE |
| G-008 | P1.1 | —（cheat tileMovement 的 2 个 swap case 已重放） | draw/discard/pon/kan 的 TileMovement case（五份均无） | — |
| G-013 | P1.2 | — | 恰好 4han/30fu/7700 锚定 case | — |
| G-014 | P1.2 | — | 恰好 28→30 锚定 case | — |
| G-015 | P1.2 | — | 单赤宝牌+指示牌 1/1/2 case | — |
| G-019 | P1.2 | —（42 yaku codes 全数断言） | — | 41/42 役裁决（D-01）未决——规则裁决项，非字段缺口 |
| **G-028a** | P1.3 | **round-v1 字段级 16 条**（§3.3 两表：roundTerminal 7 + chankanRon 9，含 eventTypes 精确序列×2、语义可比布尔×2） | — | ScoreSettled 表示映射（§6，合法，协议生产者 = P3 TableRuntime 项 N3-D1） |
| G-031 | P1.3 | — | 无事件命令的 event-list/version 断言所需导出物 | — |
| G-032 | P1.4 | — | 单 case 完整 post-state/组牌/位置/deadline | — |
| G-033 | P1.4 | #12 tm-capacity-groups、#13 tm-capacity-tiles（D51 视图） | — | — |
| G-034 | P1.4 | tileMark 未绑定 **13**（#1,#3,#4,#5,#9,#12,#13,#15,#16,#17,#18,#19,#20） | — | #19 Domain 输入不可表示（D-β） |
| G-035 | P1.4 | sleeveBloom 未绑定 **21**（#22-31,#35,#36(E),#39,#40,#41,#42,#46,#47,#48,#50,#51,#52——其中 E 视图 1） | — | — |
| G-036 | P1.4 | sbo 未重放 **6**（#46,#47,#48,#50,#51,#52） | — | — |
| G-037 | P1.4 | stale/wrong-window/no-offer 未重放视图（#39,#40,#41,#42,#51,#52,#72,#74） | — | — |
| G-038 | P1.4 | beamSwap 未绑定 **17**（#53-59,#63-66,#68,#70,#72,#73,#74,#75），**含 Allowed 主路径 5**（#53,#63,#66,#73,#75） | — | — |
| G-039 | P1.4 | bm invalid 未重放视图（#68,#70） | — | — |
| G-041 | P1.4 | ledger snapshotRoundTrip 的 `restored.Entries` 逐项对照缺位（§3.4 ledger 修正行） | round version / mahjong scores / 136-tile 守恒的账本对照导出物 | — |
| G-043 | P1.5 | — | 非法 seat/config 拒绝 case | — |
| G-046 | P1.3 | — | duplicate-open 拒绝、旧 roundId-key open 拒绝 case（现有 9 op 全为成功路径） | — |
| G-048 | P1.3 | — | server.ConnectionControl grace/reclaim（exporter 显式声明不导出，unexportedSamples[3] 在案） | — |

**本审计新增字段级缺口（不属于上述 20 个 catalog partial 行的现有表述，是否并入/新增 catalog 行由 caller 裁决，本审计不改 catalog）**：

| 新增行 | fixture | 精确路径 | 证据 |
|---|---|---|---|
| N-NEW-1 | scoring-v1 | `$.winSamples[*].expected.status`（48 实例） | 变异探针 win→noWin → 7/7 PASS（§11.6） |
| N-NEW-2 | scoring-v1 | `$.winSamples[*].tags`（48）/`$.winSamples[*].tags[*]`（54） | 源码 `tags` 出现 0 次（§3.2） |
| N-NEW-3 | shuffle-v1 | `$.cases[*].deadWallSegmentEqual`（32 实例） | 变异探针 true→false → 4/4 PASS（§11.6） |

**20 项 partial 的分子合计（精确）**：已导出未断言 = round-v1 **16 字段级** + cheat-v1 **51 case 级**（D51；其中 47 可构造、4 需语义映射批准）+ G-041 Entries 对照缺位 1 项；cheat 51 与 handler 分桶行（G-033..039）为视图关系不叠加。新增行 3 条路径另列（不分摊进 20 行）。其余 partial 缺口全部为"尚未导出"。G-019 是规则裁决（D-01）；G-003b 一半不可表示。

---

## 5. 33 none + 3 v5-only（catalog 机器提取，逐 ID）

**none = 33**：G-004, G-005, G-006, G-007, G-009, G-010, G-011, G-012, G-024, G-028b, G-030, G-040a, G-040b, G-040c, G-044, G-045, G-047, G-049*, G-050a, G-050b, G-050c, G-051, G-052, G-053, G-054, G-056a, G-056b, G-056c, G-056d, G-057a, G-057b, G-057c, G-057d。
\*G-049 按五份口径为 none，但 ai-v1 已存在且 Replay/Submit/拒绝码/动作绑定被真实消费——按 §3.6 消费边界**建议重算 partial（非 full）**，由 caller 裁决（§9 N3-C1）。

**v5-only = 3**：G-055（outbox/RECEIPT_QUERY）、G-058（ownerEpoch/lease/Postgres）、G-059（Protobuf/WSS/TLS/Unity/IL2CPP）——设计上无 V4 fixture，不进 G1 分母。

**目录数字的正确读法**：73 = G 需求行数；10,382 = catalog 五份 fixture 声明 case 数（caseTotal 口径，§2.7 的 Δ29 差异在案）；42 = 五个 C# 读者类 Fact 数；45 = 42 Domain + 3 Ai。三者互不为分子分母。

---

## 6. 专查一：G-028a 完整事件序列四层追踪（含 R1-1 修正）

**结论：ScoreSettled 语义映射成立、C# 表示合法；真实缺口 = 事件序列精确断言缺失 + 导出布尔未绑定（可比较）。不强迫 Domain 同名枚举。**

| 层 | 锚点 | 事实 |
|---|---|---|
| ① V4 domain/round | `RoundTransitionPlanner.kt:408/:428/:508/:582/:600` | 和牌结算发射 `GameEvent.ScoreSettled(turnNumber, payments, newScores)`，夹在和牌事件与 RoundEnded 之间；exporter `V5RoundGoldenExportTest.kt:178` 记录类名 → 导出 `[TsumoWin, ScoreSettled, RoundEnded]` |
| ② V4 match | `MatchSession.kt:497-532` | match 层消费 round 结果另发 MatchEnded；ScoreSettled 是 round 层事件，match 层无对应物 |
| ③ 协议（C# 侧冻结契约） | `ProtocolSnapshot.cs:51-53` | `ProtocolEventType.ScoreSettled=17 / RoundEnded=18 / MatchEnded=19` 已冻结 |
| ④ C# Domain/投影 | `RoundTransition.cs:56-74`（RoundEventType 16 值，无 ScoreSettled）；`TableEventProjection.cs:253/:735`；`TableProjectionContractMapper.cs:521-532`；TableRuntime src 无 ScoreSettled 生产者 | C# Domain 把 ScoreSettled 携带的信息放进 `RoundSettlement/RoundResult` 字段（resultFinalScores/resultDeltas 已被断言）——**信息不丢，表示不同** |

**判定**：
1. V4 `[TsumoWin, ScoreSettled, RoundEnded]` ↔ C# `[TsumoWin, RoundEnded]` + 结算字段随 Result —— 合法语义映射；强迫 Domain 出现同名枚举是错误要求。
2. 协议层 ScoreSettled=17 的生产者是 P3 TableRuntime 集成项（§9 N3-D1），不属 Domain 黄金补齐范围。
3. **真实缺口（P1.3，`RoundReplayGoldenTests.cs`）**：
   - `RoundTerminalReplaysExactly:134` 仅 `Contains(TsumoWin)`——多事件/少事件/错序/假事件全部 PASS；
   - `ChankanRonReplaysExactly` 事件面仅 `:225-226` 两个 `DoesNotContain`——不发任何事件的错误 C# 也能 PASS；
   - `hasKanDeclared/hasRinshanDraw` 导出布尔未绑定（§3.3，可比较，名称映射 RinshanDraw→RinshanDrawn）。
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

**剩余真实缺口**：N3-B1（m1b 三元组交叉绑定）、N3-B2（注释更正）、N3-B3 的 INFO-2 地址落点表示差（不可表示，记录即可）。

---

## 8. 专查三：AgariYame / ChooseDealerEnd（沿用第 1 轮）

| 侧 | 锚点 | 事实 |
|---|---|---|
| V4 | `MatchSession.kt:82,497-532`；`GameAction.DeclareAgariYame`（ApplicationCoordinator :3268） | `ApprovedSessionStep.ChooseDealerEnd(agariYame=true)` → `MatchFinishReason.AGARI_YAME` → SessionResult + MatchEnded |
| C# | `MatchSession.cs:498` `ChooseDealerEnd(bool agariYame, …)`；:212 `CheckAgariYameEligible`；:536 `MatchFinishReason.AgariYame` | 行为完整存在 |

matchProgression 16 case 全字段消费含 `allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame`。**判定**：G-027 行为 full 成立；round 层命令入口枚举差只是命令入口面 gap，不得据此断言"缺全部 agari-yame 行为"。

---

## 9. 最小补齐包（可独立下发）与依赖 DAG（第 2 轮：覆盖全部可比较缺口）

### 补齐项（全部为测试/文档/索引文件，不触生产代码、不改 fixtures、不改 catalog 冻结数字以外的语义）

| ID | 文件 | 内容（断言要求逐项） | 依赖 | 对应缺口 |
|---|---|---|---|---|
| N3-A1 | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs` | RoundTerminal：(a) 事件序列 = fixture `eventTypes=[TsumoWin,ScoreSettled,RoundEnded]` 经 §3.3 投影规则 → `Assert.Equal(["TsumoWin","RoundEnded"])`，注释引用 fixture 原序列与投影规则（不手写外部预期）；(b) 绑定 `input.phaseBefore/versionBefore`（替换硬编码 :115）；(c) 绑定 `settlement.outcome/totalDeltas`（:119/:132）；(d) 绑定 `expected.phaseAfter/resultOutcome`（:129/:130 改读导出值） | 无 | G-028a / §3.3 |
| N3-A2 | 同上 | ChankanRon：(a) `Assert.Equal(["RonWin","RoundEnded"])`（投影注释同上）；(b) **绑定 `expected.hasKanDeclared/hasRinshanDraw`**：`Assert.Equal(导出布尔, 终局事件列表.Contains("KanDeclared"/"RinshanDrawn"))`，行注释仅作**名称映射说明**（RinshanDraw→RinshanDrawn），**不以注释豁免绑定**；(c) 绑定 `meldTypeAfterKan/legalRonSeat1/input.meldTiles/phaseAfterKan/phaseAfterRon/resultOutcome`（替换 :221/:227 硬编码） | 无 | G-028a |
| N3-B1 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | m1b 黄金字段（backfilledIndex/resolvedIndex/pointer 三元）与 `SleeveBloomSecondChoice` 重放交叉绑定 | 无 | G-038 邻域 / mv-swap |
| N3-B2 | 同上 | 更正 :638-642 注释为准确映射（ChooseSleeveBloom:376 为入口；ChooseBeamSwap 拒绝=V4 生产对齐；地址落点差按 INFO-2 记录为表示差） | 无 | §7 |
| **N3-B3（新）** | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`（仅测试） | **handler 全清单补齐包**，覆盖 §3.4 表全部非 A/C 桶：(a) **D-α 47 例**：goldenInputs 驱动真实入口行为重放 + outcome/reason/requestException 逐值绑定；其中 **Allowed 主路径 6 例**（#1/#53/#63/#66/#73/#75）另绑副作用/事件形状（tileMarkState/tileMovement/offer 状态相关节）；(b) **D-β 4 例**（#5/#18/#19/#20）：按真实 C# 对应层绑定——#19 在岗 deadline 在 `CheatTurnContext`（:12-25）无字段、C# DEADLINE_EXPIRED 仅 off-turn（:553-560），以等价类断言 + 映射注释 + **owner 批准记录**落地；#5/#18/#20 绑定 InvalidRequest 合并语义实际 outcome + 注释；(c) **B 2 例升级**：#6 绑定 C# 特化 ResponseWindowOpen 错误码逐值（不再同族）、#67 升级全链路重放（不再仅 outcome）；(d) **E 1 例绑定**：#36 sb-choose-first 以 fixture goldenInputs 构造并绑定 outcome=Allowed/reason=null。验收：16 Facts 保持 PASS，新增断言后 Total 增加且 0 FAIL；负例矩阵见 §13 DEF-2 | 无（复用现有 goldenInputs 构造） | G-033/034/035/036/037/038/039 + §3.4 |
| N3-C1 | `tests/v5-golden/catalog-v1.json` + `V5-0-GOLDEN-MANIFEST.md` | **caller 口径决定**：ai-v1 纳入 fixtures[]/manifest 并把 G-049 重算 **partial**（§3.6）；catalog caseTotal 口径 Δ29 是否声明分解（§2.7） | caller 裁决 | §3.6 / §2.7 / G-049 |
| **N3-E1（新）** | `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` + `ShuffleGoldenTests.cs` | (a) scoring：绑定 `winSamples[*].expected.status` = C# 重放实算 status（探针证明当前不设防）；`tags/tags[*]` 以**显式排除声明**处理（exporter 元数据：若 caller 认为须绑定则并入，否则在测试内加一行显式 `// tags: exporter metadata, intentionally not asserted` 使分母可见——禁止沉默）；(b) shuffle：绑定 `cases[*].deadWallSegmentEqual` = C# 重算死山段等价性的实际结果 | 无 | N-NEW-1/2/3 / §3.2 / §3.5 |
| N3-D1 | （P3 范围，非本包） | TableRuntime ScoreSettled 协议事件生产者 | P3.x | §6 判定 2 |

### 依赖 DAG

```
N3-A1 ─┐
N3-A2 ─┼─→ G-028a 关闭路径（P1.3, RoundReplayGoldenTests）──┐
N3-B1 ─┐                                                    │
N3-B2 ─┴─→ mv-swap 定性修正落地（P1.4, CheatGoldenTests）──┤
N3-B3 ────→ cheat handler 51+2+1 全覆盖（P1.4）────────────┼─→ N-3 [MAJOR] 子项证据齐备
N3-E1 ────→ scoring/shuffle 3 条路径绑定（P1.2/P1.1）──────┤
                                                            │
N3-C1 ←─ caller 口径裁决（G-049 / caseTotal 口径）─────────┤
                                                            │
N3-D1（P3 TableRuntime，独立轨）───────────────────────────┴─→ G1 门的字段级证据基础
（G1 批准另需：未导出缺口单列清单口径、G5 完成、caller 汇总复核——均不在本包内）
```

所有 N3-A*/B*/E* 互不依赖、可并行；N3-C1 是索引口径决定非代码；N3-D1 与 G1 无前置关系。**本包覆盖本报告所报全部可比较缺口**（round 16 + cheat 51+2+1 + scoring/shuffle 3 路径 + G-041 Entries 对照注记）；唯一"不直接绑定"项 = D-β #19 的在岗 deadline 表示（等价类 + 批准）与 tags 元数据（显式排除声明）——两者均显式留痕，无沉默移出分母。

---

## 10. 侥幸通过（wrong-value-passes）判定汇总（含第 2 轮探针实证）

| 位置 | 错值场景 | 现有断言是否设防 | 实证 |
|---|---|---|---|
| RoundTerminalReplaysExactly :134 | 事件序列多/少/错序/假事件 | **不设防**（仅 Contains TsumoWin） | 静态读源 + §13 DEF-1 负例矩阵 |
| ChankanRonReplaysExactly :225-226 | 和牌后完全不发事件 | **不设防** | 同上 |
| chankanRon hasKanDeclared/hasRinshanDraw | 导出双布尔改 true | **不设防** | 探针 round-flags-mutated-true：6/6 PASS exit 0 |
| winSamples[*].expected.status | 首例 win→noWin | **不设防** | 探针 scoring-win-status-mutated：7/7 PASS exit 0 |
| winSamples[*].tags/tags[*] | 元数据注入 | **不设防**（未读） | 源码 0 次引用 |
| cases[*].deadWallSegmentEqual | 首例 true→false | **不设防** | 探针 shuffle-deadwall-flag-mutated：4/4 PASS exit 0 |
| sb-choose-first（E 桶） | outcome→Rejected + reason→WRONG_PHASE | **不设防** | 探针 cheat-choose-first-mutated-rejected：16/16 PASS exit 0 |
| cheat D51（含 6 个 Allowed 主路径） | 真实 C# 行为与 V4 相悖（同 reason 不同副作用/事件形状） | **不设防** | §3.4 分桶 + §13 DEF-2 负例矩阵 |
| **正控制**：已绑定 totalPoints | 2000→123456789 | **设防（1 FAIL exit 1）** | 探针 positive-control：Total:7, Failed:1 —— 证明探针方法/副本/DLL 实读有效 |
| physical samples/scope/hashCrosswalk、scoring 其余 6 组、shuffle 三矩阵、ledger 前 8 子键、tileMarkState、round version/scores/deltas/rejected | 数值/结构错值 | 设防（逐值断言 + 分布对账 + 冻结硬值双锚，§3 逐字段表） | 45/45 PASS 维持 |

---

## 11. 可复算命令与证据（完整命令、native exit、全 SHA256；无省略号）

> 约定：所有哈希除特别注明 **Git blob id**（`git hash-object`，SHA-1 算法，**不是 SHA256，与本表全部哈希不可互换**）外均为 **SHA256**（`sha256sum`，小写十六进制）。

### 11.1 构建（Release，沿用第 1 轮，日志在独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-field-audit-01edf75dbc3b/`）

```
cd D:/majiang-wt/n3-golden-field-audit
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
→ EXIT=0（build-domain.log，0 警告 0 错误）
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal
→ EXIT=0（build-ai.log，0 警告 0 错误）
```

### 11.2 定向 Golden 运行（完整命令，native exit）

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

### 11.3 第 1 轮枚举工件（历史证据，保留不覆盖；TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-field-audit-01edf75dbc3b/`）

| 工件 | SHA256 |
|---|---|
| walk-fixtures.ps1 | `1F449B61454EE4F07DA699299B71AEA073581CE213EF771017982C5DFF41B4F7` |
| aggregate-leaves.ps1 | `E37049DD05E3248CB95779A672D8D8EDD3269AD5E6322909EC006EA77561ED91` |
| scoring-shape.ps1（首中末抽样——**已被 §1.1 判不充分，仅存档**） | `3C9C111741FEED7CF1EBC14BBCAF86A92A32C33959A17C8E2274B43B8B3F44AB` |
| physical-v1-leaves.txt / -agg | `C921C12D2BFC487B578DAB95A4969204F016EB7E4B1BE1D699379860631659CD` / `D0664CEC8A2378096EE80A27C3B1D73632660D1C3437D1B68440099D5EF17E6E` |
| scoring-shape.txt | `A9A2D122FC3422EE25A33A711426F9FD93CE2758AE4820BC829D417D3C3AAD35` |
| round-v1-leaves.txt / -agg | `7C3BC02D85194560455EDD74AE24F4A29DB9C88357929BBF70E125DCB9BD7351` / `0B9A9D5E4B4DA7AA44B9F62BCCFAF8DDF520074BBA65FCDF9DFDC07859368541` |
| cheat-v1-leaves.txt / -agg | `87E4FA363FA5F9F2C398D614BC8DA9CFA9E3BEBBF30606E9B192E2A8749413CE` / `6F84A0972CBC7ACFBE097AE709661CE38E1F0A7FD8AF2E8900BBE0368758AEA9` |
| shuffle-v1-leaves.txt / -agg | `3B5C916D3FB2BF7AF38D139EB2DBCB20A03D360032D278F0D7F3835317D6185B` / `BD39C67945CDB6D4DF536CD8738A1996B8AB05F687178AD18936122088237A18` |
| ai-v1-leaves.txt / -agg | `F10407306769BDC56C1E592CDD533D79B4BF15C3BD4BC786983BC20605D8530` / `9FAFD588C66441643408429309F83621789EAE708FA6705FFAE0D5F8D2326088` |

### 11.4 第 2 轮全量 census（新独占 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/`；R1-3 修复）

工具源码 = `FullCensus/FullCensus.csproj`（net8.0 类库工程，AssemblyName FullCensus）+ `FullCensus/Program.cs`（递归枚举全部 JSON 节点；归一化 `[N]→[*]`；口径见 §1.2）。**完整可复制命令（无占位）**：

```
D:/Unity/dotnet/dotnet.exe run --project C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/FullCensus/FullCensus.csproj -- D:/majiang-wt/n3-golden-field-audit C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out/census-run.log 2>&1
→ EXIT=0；日志尾部 = FULL-CENSUS-DONE failures=0；census-run.log SHA256 72c78ea2284e26f12d6828f693798b0f8b8b2d76c5fb285d7ff28ed8d7b86c7d
```

输出（`census-out/`，`census-summary.txt` SHA256 `7b8909f2a2a0c4dc4f4dff068fafb1e19762ed86d0af005d2eb739ea98041504`；全文 6 行 distinctPaths/distinctLeafPaths/leafInstances/groups 见 §1.2 表）：

| 文件 | SHA256 |
|---|---|
| FullCensus/FullCensus.csproj | `8f946bc6c411cc89c659c88eeb89ddeaeb52cb54b0d480e58e09ff418e29168d` |
| FullCensus/Program.cs | `fd49db41b5dbb6602b7ce1a2e9a52b679f78b700dc1d93606d8f12f8f530b4d0` |
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
| physical-v1-prefix2.txt / scoring-v1-prefix2.txt | `3c7b46aec99940ca22077bbd2e7cd8f86389440831a9e74782d3588511894c6b` / `781ec54d0c23f75aefe522d6e4def27666994744ae380911f7e2ed0ea9b03e56` |
| round-v1-prefix2.txt / cheat-v1-prefix2.txt | `c431051b50b458848b35d375f8a6959a76cff347aad85fe0b4c168a0ce7e09af` / `6ac4a3d7f6664993fd501597ff26403d7b9af03ab004f1007e6e9be2a6c85923` |
| shuffle-v1-prefix2.txt / ai-v1-prefix2.txt | `3c4376000fe8f15d9e84b3ecea36fa933287518bbb876bfaab45f86a779d277c` / `885145ef36e4556ac6ed06e2d8f54505f1ef4d0d05e2896d8d9a4478232e0d3a` |

**第 1 轮漏检 14 路径的复核命令（可在新 TEMP 复算）**：

```
grep -F -e '$.winSamples[*].input.melds[*]' -e '$.winSamples[*].input.melds[*].calledTile' -e '$.winSamples[*].input.melds[*].fromPlayer' -e '$.winSamples[*].input.melds[*].tiles' -e '$.winSamples[*].input.melds[*].tiles[*]' -e '$.winSamples[*].input.melds[*].type' -e '$.winSamples[*].input.doraIndicators[*]' -e '$.winSamples[*].input.uraDoraIndicators[*]' -e '$.winSamples[*].expected.dora.indicatorTiles[*]' -e '$.winSamples[*].expected.dora.uraTiles[*]' -e '$.winSamples[*].expected.dora.redTiles[*]' -e '$.doraSamples[*].expected.uraTiles[*]' -e '$.doraSamples[*].input.uraDoraIndicators[*]' -e '$.randomSamples[*].ura[*]' C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out/scoring-v1-full-paths.txt
→ 输出恰为上述 14 行（含 $.randomSamples[*].ura[*] number instances=1208）
```

### 11.5 第 2 轮 prefix-2 覆盖率校验（emit/verify 双模式）

脚本 `verify-coverage.ps1`（SHA256 `2960545628a4037662bef80c89f885cc957ce1b0364a5583f7db5cdd975c2220`；初版 `be8ae22db036d62971ab7a6a61e22e9f165f27f352d99a5503207efca4304830` 的 verify 解析器漏接受根容器行 `$ = 1`，仅认 `$.` 前缀，导致 6 块各报 1 条 unmapped——修正为接受 `$` 前缀后全绿；分组/计数逻辑未变。逻辑：emit = 对 full-paths 按 prefix-2 分组写 `*-prefix2.txt` 完全划分；verify = 重算并与报告 §3.0 的 COVERAGE 块逐组 diff）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/verify-coverage.ps1 -Mode emit -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out -OutDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out
→ EMIT physical-v1 groups=28 / scoring-v1 groups=27 / round-v1 groups=22 / cheat-v1 groups=21 / shuffle-v1 groups=19 / ai-v1 groups=31；EXIT=0
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/verify-coverage.ps1 -Mode verify -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/census-out -ReportPath D:/majiang-wt/n3-golden-field-audit/doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md
→ 实测输出（提交前）：
VERIFY physical-v1 groups=28 unmapped=0 mismatched=0 extra=0
VERIFY scoring-v1 groups=27 unmapped=0 mismatched=0 extra=0
VERIFY round-v1 groups=22 unmapped=0 mismatched=0 extra=0
VERIFY cheat-v1 groups=21 unmapped=0 mismatched=0 extra=0
VERIFY shuffle-v1 groups=19 unmapped=0 mismatched=0 extra=0
VERIFY ai-v1 groups=31 unmapped=0 mismatched=0 extra=0
COVERAGE-VERIFY-OK unmapped-total=0
→ EXIT=0
```

### 11.6 第 2 轮变异探针（独立复现，全部 AS-EXPECTED）

脚本 `review-probes.ps1`（SHA256 `4d3896114c9f93937a4f886bcc0341904250214f2a5d06a79f7ba33a64597854`）：全部变异发生在独占 TEMP `fake-repo`（AGENTS.md + 变异 fixture 副本 + DLL 副本树），经 `MAHJONG_REPO_ROOT`/`AppContext` 双通道生效；真实工作树零改动。**完整可复制命令**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round2/review-probes.ps1
→ 逐行 AS-EXPECTED + 尾行 PROBES-DONE round=0 winStatus=0 posCtrl=1 shuffle=0 cheat=0；EXIT=0
```

| 探针 | 变异 | native exit（期望） | 结果行 | 日志 SHA256 | 变异 fixture SHA256 |
|---|---|---|---|---|---|
| round-flags-mutated-true | chankanRon `hasKanDeclared/hasRinshanDraw` false→**true** | 0 (0) | Total: 6, Failed: 0 | `202eca8b2765fb9f4111de7f77ff82c9fb8d812e43113f44647a7574a80c2ade` | `b52009a545a8c0e85e7440e638099df0c696fc19f71b47522f1a061447e65566` |
| scoring-win-status-mutated | winSamples 首例 `status` "win"→**"noWin"** | 0 (0) | Total: 7, Failed: 0 | `93635d2a39106a0c636e7a170377a97a1f941cf158621a7d5456d1d90297914e` | `87551c331f0dae9b1496c74327da8dbc7afecad347fc98bb60dc1bc33e03dbb4` |
| positive-control-bound-score-mutation | 已绑定 `totalPoints` 2000→**123456789** | **1** (1) | Total: 7, **Failed: 1** | `fdfd7035b02ddb828e5b4a2a080d9e92b1c4381c454ec43d4fd0bbe9514b199f` | `c75aff76beaf01a06440369a518844217b5f267ee99a8db8d8012a6b632224f6` |
| shuffle-deadwall-flag-mutated | `cases[*].deadWallSegmentEqual` 首例 true→**false** | 0 (0) | Total: 4, Failed: 0 | `dc5656190aff247d646a79b934a123c60605acde35189e1636165547e019bda3` | `4065f005e21fdb25944aabd427548230bef6db0aa949e7eeb7db43535c078c85` |
| cheat-choose-first-mutated-rejected | sb-choose-first `outcome` Allowed→**Rejected**、`reason` null→**"WRONG_PHASE"** | 0 (0) | Total: 16, Failed: 0 | `503741d9f7b71f87972176aaf83514d1837e4d60b6bcda40c4e1761614d00428` | `63e5b0560d6d5a3691ae17370b387dafbeda5b8e8e6673819168d80523ff89dc` |

`probes/probe-summary.txt` SHA256 `d2d693fd1b4aedbaf064ef318aa884380a31ec2dce65865102a1fab37a44e353`（5 行全 AS-EXPECTED）。caller 独立同型证据：E 目录 `round-flags-mutated-true.log` SHA `CDA814AA…673124A`、`scoring-win-status-mutated.log` SHA `2C479275…9E349`、`positive-control…log` SHA `2D4F14B8…60D61`、`shuffle-deadwall-flag-mutated.log` SHA `05028BF7…C5451`、`cheat-choose-first…log` SHA `F72B0404…CF77B`——与上表结论一致。

### 11.7 fixture SHA256（工作树实测，与 catalog 声明一致；沿用第 1 轮）

| fixture | 实测 SHA256 | catalog 声明 |
|---|---|---|
| physical-v1.json | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | 一致 |
| scoring-v1.json | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | 一致 |
| round-v1.json | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | 一致 |
| cheat-v1.json | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | 一致 |
| shuffle-v1.json | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | 一致 |
| ai-v1.json | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | catalog 外（§3.6） |

### 11.8 本报告自身哈希（R1-3 修正）

- 第 1 轮报告 364 行把 `git hash-object` 当作报告 SHA256——**算法错误**：`git hash-object` 产出 Git blob **SHA-1**，与 SHA256 不可互换。本报告的完整性校验一律以 `sha256sum "doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md"` 实测 SHA256 为准，**在提交后于最终交付说明中给出实测值**（正文不预填，杜绝手填哈希）。

### 11.9 caller 独立证据对照（E 目录，未复制未改）

E = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-review-f6f28203cb29469f8fe0505902cdcec0`。本审计仅只读引用其结论作交叉验证：review-probes.ps1 `AEFDCC6C…983C8`、review-probes.log `E07142FB…D7C`、full-census.log `B5E8C09C…5A87D`、source-checks.log `EA50E28A…B5D7`、build/test 各日志 SHA 见 `E/sha256.json`。本报告所有数字以 §11.3-11.7 本方独占 TEMP 复算件为准，E 为独立第二来源，二者无矛盾。

---

## 12. 边界声明

1. 本报告**只读**了仓库与兄弟任务无关区域；未读取 N1a/N2 兄弟在途未提交内容，未假设其已修。
2. 未修改：生产代码、测试代码、fixtures、catalog、manifest、固定头已接受 G1 盘点报告、用户未提交文件（CLAUDE.md/PROMPTS.md/任务书/协议/Unity 环境/Resources/Burst）。
3. 全部写入限于：本报告一个文档（expectedFiles 指定路径）+ 独占 TEMP 目录两处（第 1 轮 `n3-field-audit-01edf75dbc3b` 与第 2 轮 `n3-rework-round2`，后者含 fake-repo 变异副本，均不触仓库）。
4. `V5-REVIEW-REWORK-PROTOCOL.md` 全程只读，未复制提交。
5. **审计报告组件完成 ≠ N-3 关闭 ≠ G1 批准**：N-3/G1 的关闭由 caller 依据本报告与补齐包（§9）独立复核后裁决；G1 门仍受 V5-0-1 任务书 :107-108 全口径约束（未导出样本单列、G5 未完成等）。本报告与 §9 不缩小也不扩大该口径：**所有已导出可比较未绑定内容全部保留为缺口**（§9 覆盖清单）；不要求未导出 73 行全齐才允许 G1（该口径属任务书既有规定，非本审计增设）。
6. 本报告不带任何 push/PR/CI 触发/远端操作。

---

## 13. 协议格式缺陷清单（第 2 轮一次性全量版；同类合并 + 统一负例矩阵）

> 本审计冻结在"只写一个新报告"边界内，**不实施任何修复**；每项"修复要求"指向 §9 补齐包 ID，由 caller 独立下发。严重级别口径：MAJOR = 现有断言防不住错误值/错误行为或阻断 G1 门口径核实；MINOR = 文档/索引失真，无行为风险。无 BLOCKER（未发现生产行为错误或守恒违例）。**本清单已覆盖当前全部已知可复现缺陷，无分批保留**；后续仅当出现新信息才追加，并须说明此前为何不可发现。

### DEF-1 [MAJOR] round-v1 已导出字段的断言缺失与硬编码（含 G-028a 事件序列与语义可比布尔未绑定）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs:115`（phaseBefore/versionBefore 硬编码 Discard）、`:119`（outcome 硬编码）、`:129-130`（phaseAfter/resultOutcome 硬编码）、`:132`（用 resultDeltas 替代 settlement.totalDeltas）、`:134`（事件仅 Contains TsumoWin）、`:151-233`（ChankanRonReplaysExactly：eventTypes 仅 :225-226 两个 DoesNotContain、hasKanDeclared/hasRinshanDraw/legalRonSeat1/meldTypeAfterKan/phaseAfterKan/phaseAfterRon/resultOutcome/input.meldTiles 未绑定）。导出侧事实：`V5RoundGoldenExportTest.kt:275-277`。
- **原样复现**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.RoundReplayGoldenTests -noColor -noLogo
  → EXIT=0（Total: 6, Failed: 0）
  ```
- **实际 vs 期望**：实际 = 16 个字段级条目未绑定/弱化（§3.3 两表精确清单）；期望 = 每条逐值绑定，事件序列按 fixture 投影规则（§3.3）精确对账，两布尔经名称映射绑定到 C# Contains 结果（ScoreSettled 表示映射保留，不强造 Domain 同名枚举）。
- **证据**：fixture `tests/v5-golden/round-v1.json` SHA256 `DF5FC5F8…452B7`；运行日志 SHA256 `B8363113…94D8E`；flags 变异探针日志 SHA256 `202eca8b…c2ade`（6/6 PASS）；§3.3。
- **修复要求**：§9 N3-A1 + N3-A2（P1.3，仅测试文件）。
- **统一负例矩阵（变异验证仅在 caller 隔离环境做）**：(a) 双布尔 false→true → 应 FAIL；(b) eventTypes 插入 KanDeclared / 删去 RoundEnded / 错序 → 应 FAIL；(c) settlement.totalDeltas 篡改 → 应 FAIL；(d) meldTypeAfterKan "PON"→"CHOW"、legalRonSeat1 true→false → 应 FAIL；(e) phaseAfterKan/phaseAfterRon/resultOutcome 逐值错值 → 应 FAIL。
- **验收断言**：(a) 两测试对事件类型**精确序列**断言（投影注释在案）；(b) §3.3 两表全部"已导出未断言/断言弱化/未绑定"行变为逐值绑定或 D-β 型显式映射断言；(c) 负例矩阵 (a)-(e) 全部 FAIL；(d) 6 Facts 保持 PASS。

### DEF-2 [MAJOR] cheat-v1 51/75 case 无黄金行为对照（含 6 个 Allowed 主路径）+ 2 部分绑定 + 1 存在级未绑定

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` — 行为级黄金对照 23/75（A15+B2+C6，§3.4 逐条表）+ E1（sb-choose-first，:480 注释级）；其余 **51/75**（D 桶，逐条见 §3.4 表 #1-#75 中标 D 者）仅经 `Handler_Sections_CoverEveryRejectionReason`（:339-365）做 reason 映射完备性。
- **原样复现（完整可执行，无省略号）**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  powershell -NoProfile -Command "$f = Get-Content -Raw 'tests/v5-golden/cheat-v1.json' | ConvertFrom-Json; $src = Get-Content -Raw 'dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs'; foreach ($sec in @('tileMarkHandler','sleeveBloomHandler','beamSwapHandler')) { $ref=0; $un=0; foreach ($c in @($f.$sec)) { if ($src.Contains('\"' + $c.caseId + '\"')) { $ref++ } else { $un++ } }; Write-Output ($sec + ' ref=' + $ref + ' unref=' + $un) }"
  → tileMarkHandler ref=7 unref=13 / sleeveBloomHandler ref=10 unref=22（其中 sb-choose-first 为 :480 注释级引用 → 判级后 sleeve 引用=11/未引用=21）/ beamSwapHandler ref=6 unref=17；再按 §3.4 深读判级得 A15+B2+C6+E1+D51
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo
  → EXIT=0（Total: 16, Failed: 0）
  ```
- **实际 vs 期望**：实际 = 51 case 的 outcome/reason 仅被映射表消费，真实 C# 行为无对照（sb-choose-first 变异探针实证 16/16 PASS）；期望 = §9 N3-B3 的 (a)(b)(c)(d) 全覆盖——**所有已导出可比较未绑定 case 保留缺口直至绑定**；D-β 4 例以等价类断言 + 映射注释 + owner 批准落地（真正不可比处显式说明语义与批准责任，**不沉默移出分母**）。
- **证据**：fixture `tests/v5-golden/cheat-v1.json` SHA256 `A196CBBD…28AB4E`；运行日志 SHA256 `4783C376…1AF4`；sb-choose-first 变异探针日志 SHA256 `503741d9…00428`；§3.4 逐条 75 行表。
- **修复要求**：§9 N3-B3（P1.4，仅测试文件）；B 桶 2 例升级与 E 桶绑定同包。
- **统一负例矩阵**：(a) 6 个 Allowed 主路径 case 任一副作用/事件形状错值 → 应 FAIL；(b) sb-choose-first outcome→Rejected + reason→WRONG_PHASE → 应 FAIL；(c) 任一 stale case 的 stale 判定翻转 → 应 FAIL；(d) #70 requestException 缺失/错型 → 应 FAIL；(e) D-β #19/#5/#18/#20 的映射断言值错置 → 应 FAIL。
- **验收断言**：(a) §3.4 表 D51+E1 全部有行为级断言或显式映射断言；(b) B2 升级后逐值绑定；(c) 负例矩阵 (a)-(e) 全部 FAIL；(d) 现有 16 Facts 保持 PASS；(e) **无门径收窄**：本缺陷的关闭标准 = §3.4 分桶清零（绑定或显式映射），不以"至少 N 个 Allowed + 代表拒绝"替代。

### DEF-3 [MINOR] CheatGoldenTests.cs:638-642 差距声明与源码事实不符（mv-swap 定级）

- 沿用第 1 轮全部内容（位置/复现/实际 vs 期望/证据）；修复要求 §9 N3-B2；验收断言不变（注释与 §7 五层链一致，结构断言保持原样）。

### DEF-4 [MINOR] catalog G-049 coverage=none 与仓库现状不符（ai-v1 索引过时）

- 沿用第 1 轮位置与复现（`catalog-v1.json` SHA256 `16F7FD7F…E891B`、`ai-v1.json` SHA256 `E898CDB6…375C9`、run-ai.log SHA256 `91CC7A29…29B9`）。
- **第 2 轮修正**：G-049 重算建议为 **partial**（非 full）——真实行为面 = Replay/Submit 录制意图/拒绝码/动作绑定；后态差分缺位 = §3.6（DeepEquals 为 fixture 内部树）。修复要求 §9 N3-C1（caller 裁决口径）；验收断言不变（catalog/manifest 与"仓库现存 fixture 清单"一致）。

### DEF-5 [MAJOR] scoring-v1/shuffle-v1 已导出字段未绑定与"fixture 内部结构误判为生产行为差分"的结论性错误

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs`（`CompareWinSample` :207-242 不读 `expected.status`；`tags` 全文 0 次引用）；`dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenTests.cs`（`deadWallSegmentEqual` 全文 0 次引用；`ExportedWallsSatisfyPhysicalLaws` :160-175 为导出数组内部检查）；`CheatGoldenTests.cs:260-262`（snapshotRoundTrip 布尔信任）；`PhysicalGoldenTests.cs:56/:61/:663-666`（归一化 SHA 口径）。
- **原样复现**：§11.6 四条变异探针命令 + §11.4 census 命令（路径计数）；`grep -c 'tags' dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` → `0`；`grep -c 'deadWallSegmentEqual' dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenTests.cs` → `0`。
- **实际 vs 期望**：实际 = `winSamples[*].expected.status`（48 实例）、`winSamples[*].tags`（48）/`tags[*]`（54）、`cases[*].deadWallSegmentEqual`（32）未绑定且变异后 PASS；walls 组、ai 前后树、ledger snapshotRoundTrip、physical probeA/B 为 fixture 内部结构检查，provenance 为归一化 SHA 口径——**第 1 轮 §10"physical/scoring/shuffle 全部已导出字段错值均设防"与 §3.5"32 墙×5 变体行为差分"作废**；期望 = N3-E1 绑定 status/deadWallSegmentEqual、tags 显式排除声明、各 fixture 判级表（§3.1-3.6）按"行为差分/输入绑定/fixture 内部结构/未绑定/语义映射"五类准确标注。
- **证据**：探针日志/变异 fixture SHA（§11.6 表）；census 路径行（`$.winSamples[*].expected.status string instances=48`、`$.winSamples[*].tags array instances=48 elements=54`、`$.cases[*].deadWallSegmentEqual boolean instances=32`）。
- **修复要求**：§9 N3-E1；分类表述以本报告 §3 为准同步至任何下游盘点。
- **统一负例矩阵**：(a) win 首例 status win→noWin → 应 FAIL；(b) deadWallSegmentEqual 首例 true→false → 应 FAIL；(c) tags 数组注入标记 → 绑定或显式排除后行为确定（FAIL 或明确不适用声明）；(d) 正控制 totalPoints 错值 → FAIL（已实证）。
- **验收断言**：(a) 探针 (a)(b) 在修复后 FAIL；(b) `tags` 的分母可见性成立（绑定或显式声明）；(c) §3 判级表与机器 leaf 集合映射覆盖率 100%（§11.5 verify exit 0 维持）。

### 语义映射 INFO 记录（非缺陷，禁止后续轮次误报为缺陷）

- **INFO-1（第 2 轮重写，替代第 1 轮"不可强迫同值"表述）**：`chankanRon.expected.hasKanDeclared/hasRinshanDraw` ↔ C# 终局事件列表 `Contains("KanDeclared")`/`Contains("RinshanDrawn")`——**同语义、可绑定**（两侧都是"终局事件列表是否包含对应事件"），唯一差异是名称映射 RinshanDraw→RinshanDrawn。第 1 轮将其定性为"发生过状态旗标、不可强迫同值"与 exporter `V5RoundGoldenExportTest.kt:276-277`（`events.contains(...)`）矛盾，作废。补齐 = §9 N3-A2 的等值绑定断言；映射注释仅说明名称映射，**不构成豁免**。
- **INFO-2**：mv-swap-drawn-with-wall-backfill 被置换墙牌落点地址（V4：指针下方死区旧墙槽 ↔ C#：摸牌让出的手牌槽）——净归属与 136 守恒等价、地址无规则消费，**不可机械 1:1**；以映射注释记录（N3-B2 附属）。批准责任：P1.4 owner。
- **INFO-3**：役种 41/42 口径（D-01）——DEEPSEEK 计划 `:588` 已预登记"冻结黄金前必须确认"，属规则裁决轨，非字段缺口；G-019 的 partial 定性维持。

---

## 14. 第 2 轮返修映射（R1-1..R1-5 → 章节 → 证据 → 状态）

| 项 | 缺陷摘要 | 修复落点 | 关键证据 | 状态 |
|---|---|---|---|---|
| R1-1 | 抢杠导出布尔语义写反、错误宣告不可比较 | §3.3 两表、§2.2、§6 判定 3/4、§9 N3-A2（等值绑定，注释仅名称映射）、§13 INFO-1 重写、§10 探针行 | exporter :276-277 实拍（events.contains）；fixture false/false；本方探针 round-flags-mutated-true（6/6 PASS，log `202eca8b…`）与 caller E/round-flags-mutated-true.log 结论一致 | **已修**；ScoreSettled 合法映射保留；事件序列建议全部由 fixture 投影（§3.3 投影规则） |
| R1-2 | 覆盖表漏项 + fixture 内部结构与生产行为差分混淆 | §3.0（COVERAGE 全量映射块）、§3.1（provenance 归一化 SHA）、§3.2（status/tags）、§3.4（ledger snapshotRoundTrip）、§3.5（walls 拆分 + deadWallSegmentEqual）、§3.6（ai 边界 + G-049 partial）、§4（新增行 N-NEW-1..3）、§10、§13 DEF-5 | 五项 caller 探针全部本方独立复现（§11.6）；census 路径行实拍；源码 0 次引用 grep 实拍 | **已修**；判级五类法（§1.2）贯穿；"全字段设防"类结论全部改为逐字段证据 |
| R1-3 | scoring 证据实为抽样、漏 14 路径；命令链不完整；git hash-object 误当 SHA256 | §1.1（局限正式记录 + 14 路径清单）、§1.2/§3.0（全量 census 口径与总量）、§11.4-11.6（完整无省略命令 + native exit + 全 SHA）、§11.8（哈希算法修正） | census-run.log `72c78ea2…`（FULL-CENSUS-DONE failures=0）；14 路径 grep 命令与输出；全量路径/组计数/前缀块全 SHA 表 | **已修**；第 1 轮历史证据保留（§11.3 存档标注"不充分"） |
| R1-4 | 75 case 分桶/分母/补齐包不自洽；未经授权缩小关闭标准 | §3.4（逐条 75 行表 + A15/B2/C6/E1/D51 精确分桶 + D-α47/D-β4 分层 + caller 24/51 口径对齐）、§4（视图关系不重复计数 + 精确分子）、§9 N3-B3（可独立下发的 handler 全清单包：文件/依赖/分桶断言）、§13 DEF-2（完整复现命令 + 统一负例矩阵 + 无门径收窄声明） | 75 case 机械差集实拍（本方命令与 caller 脚本同结果）；sb-choose-first 探针 `503741d9…`；§2.7 caseTotal 口径差异上报 | **已修**；D-β 4 例与 tags 的语义/批准责任显式留痕；第 1 轮"至少5个Allowed其余reason-only、不要求75/75"表述删除，替换为"全部分母保留缺口" |
| R1-5 | 交付路径偏离 expectedFiles | `git mv` 至 `doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md`；文内路径同步（§11.8 等）；无第二拷贝 | `git status`（R 重命名）；`git diff 8254c9b..HEAD --name-only` = 单文件（提交后核验，见交付说明） | **已修** |

**未修事项与风险（显式清单）**：
1. **不实施** §9 任何补齐项（任务边界：只修报告）；N3-A*/B*/E* 全部为建议，风险 = 缺口继续存在直至 caller 下发。
2. **catalog 10382 口径 Δ29 未采纳**（不改 catalog）：风险 = 下游若混用 catalog caseTotal 与 census 案例口径会差 29；已在 §2.7 留痕待 caller 裁决（N3-C1）。
3. **D-β #19 在岗 deadline**：Domain 输入不可表示，等价类断言 + owner 批准为建议方案，未获批准前该例维持"缺口（需语义映射）"状态。
4. **tags 元数据**：绑定或显式排除由 caller 裁决（N3-E1(b)），裁决前维持"未绑定"计入分母。
5. 第 1 轮已发现的 caller 认可方向（mv-swap 五层、ChooseDealerEnd、45/45、哈希表）未改动，仅按本轮口径补充引用。
