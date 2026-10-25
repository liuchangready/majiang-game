# V5 N3-C1 目录增量：ai-v1 纳入与 G-049 partial 登记

日期：2026-10-25
分支：`wt/v5-n3-c1-index-20261025`
工作树：`D:/majiang-wt/n3-c1-index-20261025`
冻结基线：`004c24708917168877efaba2565da9dc375fe170`（PR19 `feat/v5-1-integration -> v5` 已发布、两项 CI 成功的已接受集成头）
状态：**目录索引增量完成（catalog verifier PASS；定向自测与 AI 定向测试实跑）**。G-049 只登记为 `partial`；N3、G1、G5 均未关闭；V4 仍是唯一生产架构。

---

## 0. 结论与边界

- caller 本轮裁决已执行：真实已接受 `tests/v5-golden/ai-v1.json` 纳入 `catalog-v1.json` 的 `fixtures[]` 和 `V5-0-GOLDEN-MANIFEST.md`；G-049 由 `none` 登记为 `partial`，**没有**写成 `full`、没有写“全部 AI 移植”、没有写“G1 通过”。
- 旧五组 fixture 的字节、SHA-256、内嵌 `v4Commit`、`caseTotal` 和原报告不变；`205506f07f6a1ccfef6b32cf97390431e3349f8d` 仍是历史 fixture freeze。当前 catalog 索引在 `004c247...` 刷新，这是索引更新，不是 fixture 重新导出。
- 净改动仅 5 个约定文件（见 §2）。没有改生产/测试 C#、V4 Kotlin、fixture JSON、exporter、任务书、已接受报告、统一 verifier/manifest、Unity/Resources/Burst。
- catalog verifier 实测 PASS：`fixtures=6 requirements=73 cases=10389 random=10000 facts=45 coverage=full:17/partial:21/none:32/v5-only:3`，native exit 0。
- 返修后定向自测 **103 例**（1 正常基线 + 102 负例，含原 76 负例 + 前轮 15 个 AI/Δ29/G-049 负例 + D-R1 的 7 个反例 + D-R2 的 4 个重复 ID 反例）实跑结果见 §11；AI 定向 `AiReplayGoldenTests` 3/0/0、`AiReplayStateGoldenTests` 3/0/0，native exit 0。
- 不宣告 N3/G1/G5，不写 `complete.flag`，不覆盖主 checkout 用户 `CLAUDE.md`/`PROMPTS.md`，不 push、不建 PR、不触发 CI、不部署、不改环境/信任。
- 2026-10-26 caller 首轮独立复核提出 3 条 MAJOR（D-R1-01 AI 字节 freeze 误指 source-only 提交、D-R1-02 manifest 当前表未核对、D-R1-03 锚点仅语法检查）；均已在原 5 文件内修复并加入 7 个负例，见 §13。
- 第二轮复核新增 D-R2-01：当前 §8.5 重复 ID 静默 first-wins，partial 后紧跟 full 仍 PASS。已在原 5 文件内修复，并加入 4 个重复 ID 负例（#37-#40），见 §13/§13.1。

---

## 1. 开工核实（实拍）

| 项 | 实测 |
|---|---|
| `git rev-parse --show-toplevel` | `D:/majiang-wt/n3-c1-index-20261025` |
| 当前分支 | `wt/v5-n3-c1-index-20261025` |
| `git rev-parse HEAD` | `004c24708917168877efaba2565da9dc375fe170` |
| `git status --porcelain=v1` 复核时 | 仅本任务 5 个目标文件的修改/新增；无其他文件改动，未读改兄弟 worktree |
| upstream / 归属 | `origin/feat/v5-1-integration`；本 worktree 独占，未读兄弟未提交文件，未跨树写 |
| 冻结基线 | `004c247...`，与 caller 指定一致；旧五组 fixture freeze `205506f...` |
| PR19 | 既有 `feat/v5-1-integration -> v5`；本任务只做本地 commit，不 push、不建/改 PR、不触发 CI |

任务来源确认：原 catalog 任务 `thread_20261024_055105_a2cb` 已 completed 且 merge-back recorded（`2d29fed`）；原 AI fixture 任务 `thread_20261024_055105_835e` 与后续 N3-AI 绑定均已接受集成。本任务不复制原任务、不重做其实现，只做已验收冻结审计登记的 N3-C1 目录口径后续。

---

## 2. 允许修改与实际净 diff

只改以下 5 个文件：

| # | 文件 | 类型 | 说明 |
|---|---|---|---|
| 1 | `doc/v5-重构/V5-0-GOLDEN-MANIFEST.md` | 修改 | 历史五组 0.2 保留；新增 0.3 N3-C1 增量；更新 8.3.2/8.4；G-049 行改 partial |
| 2 | `tests/v5-golden/catalog-v1.json` | 修改 | 新增 ai-v1 fixture/groupShapes；baseline/denominators；immutableFixturePins/sourcePins/caseAccounting；G-049 partial |
| 3 | `tools/verify-v5-golden-catalog.ps1` | 修改 | 支持 ai schema、source/immutable pins、AI bindings、case accounting、G-049 partial guard |
| 4 | `tools/tests/VerifyV5GoldenCatalog.Tests.ps1` | 修改 | 保留原负例；AI TEMP 副本；前轮 15 个 + D-R1 的 7 个 + D-R2 的 4 个，共 26 个新增负例 |
| 5 | `doc/v5-重构/V5-N3-C1-CATALOG-UPDATE.md` | 新增 | 本文 |

明确未改：任何生产/测试 C#、`engine/src/main` V4 Kotlin、6 份 fixture JSON（含 ai-v1）、`V5AiGoldenExporterTest.kt`、任务书、已接受报告、`tools/verify-v5.ps1`、统一 manifest、Unity 环境/Resources/Burst、`CLAUDE.md`、`PROMPTS.md`、CI/环境/信任配置。

---

## 3. ai-v1 真实 provenance

| 项 | 值 |
|---|---|
| path | `tests/v5-golden/ai-v1.json` |
| bytes | 51,271 |
| SHA-256（raw） | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` |
| SHA-256（LF 归一化） | `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2` |
| Git blob SHA1 | `032d79e95e45bde9cb29046cad5f49f338fa78f3` |
| schema / version | `v5-ai-golden-v1` / generatorVersion 1 / rulesetVersion 1 |
| extractionStatus | `exported` |
| 内嵌 `v4Commit`（source pin） | `205506f07f6a1ccfef6b32cf97390431e3349f8d`（该提交无 ai-v1 路径） |
| 字节 freeze commit | `09d2a4fd1690d319b90044becc9156a7c6563cde`（首个含 `032d79e...` blob 的已接受提交；`004c247` 同 blob） |
| `v4EngineSourceCommit` | `3f5370c30983a4047dade0835bb2c49dacc5ebbf` |
| `v4DotnetAiSourceCommit` | `a96c195c852e035ff6434584135718bbb8a05d29` |
| `v4DotnetDomainSourceCommit` | `5f4f8295297d60011f07115fe9b984a1ef80c4ba` |
| `generatedAtHead` | `b49911ca3c2010719715982bfb83b2ad70358a1a` |
| `v4ProductionSourceDirty` | false；`v4ProductionSourceDirtyPaths=[]` |
| `v4TestOnlyTrackedDirtyPaths` | 3 条：`dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs`、`engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt`、`tests/v5-golden/ai-v1.json` |
| `generatedByCommand`（fixture 原文） | `$env:V5_AI_GOLDEN_EXPORT='1'; .\gradlew.bat :engine:test --tests "com.majiang.state.V5AiGoldenExporterTest" --console=plain` |

- `generatedAtHead=b49911c` 是生成时的 HEAD；三个 test-only 文件在生成时处于 dirty 状态，随后由 `09d2a4fd1690d319b90044becc9156a7c6563cde` 提交。fixture 的 `v4ProductionSourceDirty=false` 已把“生产源码干净”与“测试/夹具自身 dirty”分开，不能把 `generatedAtHead` 误读为“fixture 内容等于 b49911c 提交树”。
- fixture 字节在 `09d2a4f`、`6d16812`、`7da818d`、`004c247` 的 Git blob 均为 `032d79e...`，raw SHA-256 均为 `E898...`；它没有被本任务重新导出或刷新。
- **freeze 更正（D-R1-01）**：`git cat-file -e 205506f:tests/v5-golden/ai-v1.json` = native 128（fatal: path exists on disk, but not in 205506f）；`git ls-tree 205506f -- tests/v5-golden/ai-v1.json` = 0 条。`09d2a4f` 与 `004c247` 各 1 条 `100644 blob 032d79e...`。因此 `205506f` 只作为 ai-v1 的内嵌 V4 source pin 和旧五组 fixture byte-freeze；ai-v1 的 byte-freeze 是 `09d2a4f`，不能把 source-only 提交写成字节来源。
- exporter：`engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt`，49,630 bytes，raw `F70DA6A4AFBB06FA1BC80F2D0A6573BFB5A63DD302B2CA52122A551D623C76F8`，LF `0E4289DE81E9BCC31D9D37E1A22D79D63816E807A4619912A70B787E1E3DA863`。

---

## 4. 7 个真实执行 case

`cases` 只有 7 个元素；`recordedIntent` 是这 7 个 case 共享的单一录制意图，`v5Observation`/`decisionClock`/`mappingLimits` 是观察与时钟元数据，**不是** 7 个之外的 case。

| # | id | kind | V5 accepted / rejection | V4 receipt / reason | V5 authority mode |
|---:|---|---|---|---|---|
| 1 | `AI-002-old-round` | stale-round | false / `StaleRound` | REJECTED / `STALE_AI_SUBMISSION` | `stale-round` |
| 2 | `AI-003-old-decision` | stale-decision | false / `StaleDecision` | REJECTED / `STALE_AI_SUBMISSION` | `stale-decision` |
| 3 | `AI-004-old-epoch` | stale-epoch | false / `StaleEpoch` | REJECTED / `STALE_AI_SUBMISSION` | `stale-epoch` |
| 4 | `AI-005-wrong-seat` | non-own-seat | false / `StaleDecision` | REJECTED / `STALE_AI_SUBMISSION` | `foreign-seat` |
| 5 | `AI-006-candidate-not-legal` | illegal-candidate | false / `IllegalAction` | REJECTED / `STALE_AI_SUBMISSION` | `candidate-exclusion` |
| 6 | `AI-007-exact-deadline` | exact-deadline | false / `DeadlineExpired` | REJECTED / `STALE_AI_SUBMISSION` | `current` |
| 7 | `AI-001-legal-intent-before-deadline` | legal-intent | true / null | APPLIED / null | `current` |

- 前 6 个拒绝 case 先跑，均 `stateChanged=false`、`eventCount=0`；第 7 个是唯一接受并改变桌面的 case（Version 0→1、Discard→ResponseWindow、手牌 14→13、牌河 0→1）。
- V4 把多种 stamp 不匹配合并为 `STALE_AI_SUBMISSION`；V5 分流为 `StaleRound`/`StaleDecision`/`StaleEpoch`/`DeadlineExpired`/`IllegalAction`。这是语义映射，不是“V5 复刻 V4 同一错误码”。
- `AI-006` 的 C# 侧不能构造合法观察之外的动作，因此测试把录制动作从 fresh authority 候选列表移除，以触发同一个候选检查；fixture 的 `note` 已声明该构造差异。
- `AI-005` 的 `v5.authority.legalActionRefs` 为空、`AI-006` 为 13；其余 case 为 14。不能把 `legalActionRefs` 长度硬编码成 14 作为 schema 要求。

### 4.1 分组计数（catalog 实际）

| group | count | countAsCase | 说明 |
|---|---:|---|---|
| `cases` | 7 | true | 唯一执行 case 组 |
| `decisionClock` | 7 | false | 时钟 7 个字段 |
| `recordedIntent` | 5 | false | 单一共享录制意图 |
| `v5Observation` | 23 | false | 冻结观察；`mappingLimits` 3 |
| `reproducibility` | 3 | false | 3 个键 |
| `sourceFileSha256` | 13 | false | 13 个 provenance 源文件 |
| `unexportedOrMappedSamples` | 6 | false | 6 条字符串边界声明 |

- 审计 `CensusInstances` 对 ai-v1 报 `cases=20`：这是“根层对象数组的 case key 数”= `sourceFileSha256` 13 + `cases` 7，**不是 20 个执行 case**。catalog 的 `caseTotal` 只计 `countAsCase` 的 `cases` 组，故 ai 增量是 **7**，不是 20。
- `recordedIntent`、`v5Observation`、`decisionClock`、`mappingLimits`、`legalActions`、events 都不能多算为执行 case。

---

## 5. 当前 C# 消费者、准确测试类与值级绑定

### 5.1 catalog 直接消费者

| 类 | path | Facts | raw SHA-256 | 角色 |
|---|---|---:|---|---|
| `Mahjong.Ai.Tests.AiReplayGoldenTests` | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` | 3 | `24F91115233E47F5E6FC992A68EBB570807AC9D9D68FBB8544C80BBF0B06A9F5` | catalog C# reader；fixture 直接消费 |
| `Mahjong.Ai.Tests.AiReplayStateGoldenTests` | `dotnet/tests/Mahjong.Ai.Tests/AiReplayStateGoldenTests.cs` | 3 | `53B45095A5F4935B7960645EE674AFAFC4C7F58B6F1189577BE9A19D54E57FB0` | 已接受 N3-AI 状态执行证据；不加入 catalog 的 45 Fact 分母 |

`AiReplayGoldenTests` 的 3 个 Fact（当前实际行号）：

| Fact | 行 | 值级绑定 |
|---|---:|---|
| `GoldenFixtureHasProvenanceAndTheExpectedV4Cases` | 63 | schema/version/v4Commit/generatedAtHead；`sourceFileSha256` 13；`decisionClock` reserveScope/opensAt/deadline/expired；`recordedIntent` 5 字段；7 个 id 顺序；7 个 rejectionCode 顺序；authorityMapping.mode/derivedFrom/isFreshV4Authority |
| `GoldenCasesReplayTheRecordedIntentAndMatchTheV4SemanticMapping` | 150 | `v5Observation` 14 个 legal actions；Replay 不跑启发式；`Submit` 的 accepted/code；`v4Call` 的 stamp/动作；`v4.receiptStatus/reason/stateChanged/eventCount`；接受/拒绝的前后态关系 |
| `AcceptedGoldenIntentFeedsTheDomainDeadlineArbiter` | 228 | `DecisionClockConfig(baseMillis,reserveMillis,reserveScope)`、`Open(opensAt)`、`deadlineMillis` OUT_DIFF；`ControlRegistry` epoch；`DeadlineArbiter.ProcessTick` 的 Timeouts/RejectedAi/AcceptedAi |

已接受的 `AiReplayStateGoldenTests` 3 个 Fact：`AcceptedIntentExecutesTheRealRoundAndMatchesExportedState`（101）、`RejectedIntentsHaveZeroRealRoundSideEffects`（254）、`ExportedStateSchemaIsFullyBoundOrDeclaredUnrepresentable`（334）。它们执行真实 `RoundStateMachine`，逐字段比较 `beforeState/afterState`，但仍有 `turnNumber` 无 C# 端口等 BLOCKED 边界。

### 5.2 旧审计行号 → 当前实际（已核实接受 commit 与真实源码）

审计报告冻结在 `8254c9b`，其 §3.6 的 C# 行号已被其后接受的 `6d16812`、`7da818d` 绑定补丁移动；exporter 内容自 `8254c9b` 起零 diff，旧审计的 `mappingBasis` 行号是锚点本身指错，不是代码移动。本目录只更新“当前消费索引”，并在下表保留旧到新差异（含 Git blob/LF 与工作树 CRLF raw 的口径差）；不是刷新摘要掩盖语义错误。

| 项 | 旧审计（8254c9b） | 当前（004c247） | 证据 |
|---|---|---|---|
| `AiReplayGoldenTests.cs` SHA-256 | Git blob/LF `3969289DADDFFD18F329CB105C85E9C31CCB613737446EA1BBA82E5658B7F26B` | 工作树 raw `24F911...`；LF `F1B0AAE406BD453CE0E48A96167154D956E860F87F3C56646D93A5829879768E` | 已接受补丁 6d16812/7da818d；旧 blob 与新 LF 同为 LF 口径，raw 24F9 是 CRLF checkout |
| `decisionClock` expired 边界 | `:65-69` | `:97-101` | Fact 1 |
| `DecisionClockConfig/Open/deadline` | `:228/:229/:232/:233` | `:260/:261/:264/:265` | Fact 3；`deadline` 仍 OUT_DIFF |
| `DeepEquals(before,after)` | `:571/:607` | `:599/:635` | 仍是同一 fixture 内部前后树比较，**不是** C# 后态 vs V4 全树 |
| exporter SHA-256（Git blob/LF vs 工作树 raw） | 8254c9b Git blob/LF `0E4289...`；其工作树 raw 亦为 `F70DA6...` | 工作树 raw `F70DA6...`；LF 仍 `0E4289...`（内容零 diff） | `8254c9b..004c247` 对该文件无 diff；raw 差异只是 CRLF checkout，不是 09d2a4f 语义改动 |
| `mappingBasis` exporter 锚 | 旧审计 `:740-742`（helper） | 实际写入 `:704`；helper `:738-741` | 文件自 `8254c9b` 未改，故旧行号是旧审计锚点错误而非移动；旧锚也只指 helper，未指写入点；已在本索引修正 |
| Fact 数 / 类名 | 3 / `AiReplayGoldenTests` | 3 / 同名 | 未变 |

已接受 commit：`09d2a4fd1690d319b90044becc9156a7c6563cde`（初版绑定）、`6d168123b3115ae52ced6bf834035176a7c2e600`（状态执行）、`7da818dc12d19739e2bd0026f90990b2aff098fc`（返修接受）；验收 `CODEX-V5-N3-AI-R2-ACCEPTANCE.md` 与 `V5-N3-AI-REPLAY-BINDING.md`。

### 5.3 仍为 GAP 的 AI 字段

| JSONPath | 类 | owner | 说明 |
|---|---|---|---|
| `$.cases[*].v5.expected.mappingBasis` | UNBOUND | GAP:N3-B6 | 无 C# 值级读取 |
| `$.v5Observation.mappingLimits` | UNBOUND | GAP:N3-B6 | 无 C# 值级读取 |
| `$.cases[*].v4.events[*].text` | UNBOUND | GAP:N3-B9 | C# 无事件 text 字段 |
| `$.cases[*].v4Call.action.text` | UNBOUND | GAP:N3-B9 | C# 无动作 text 字段 |

因此 G-049 **不能** full；catalog 的 `partialScope.fullClaim=false`、`closed=false`、`remainingGaps` 4 条，verifier 对 `coverage != partial`、`fullClaim=true`、`closed=true` 都直接失败。

---

## 6. catalog schema 增量（旧→新逐项）

| 项 | 旧（HEAD） | 新（本 commit） | 说明 |
|---|---|---|---|
| `fixtures` | 5 | 6 | 追加 ai-v1；旧五组条目字节/哈希/来源/case 不变 |
| `denominators.fixtureConcreteCases` | 10,382 | 10,389 | 旧五组 10,382 + ai 7；不合并 Δ29 |
| `denominators.csharpGoldenClasses` | 5 | 6 | 追加 `AiReplayGoldenTests` |
| `denominators.csharpGoldenFacts` | 42 | 45 | 42 Domain + 3 Ai |
| `requirementCoverage` | full17/partial20/none33/v5-only3 | full17/partial21/none32/v5-only3 | 只动 G-049 |
| `G-049.coverage` | none | partial | 不写 full |
| `baseline.freezeCommit` | 205506f | 004c247 | 当前索引头 |
| `baseline.fixtureFreezeCommit` | 无 | 205506f | 历史 fixture 字节冻结 |
| `sourcePins` | 无 | 新 | catalogFreeze vs historicalFixtureFreeze vs aiFixtureByteFreeze |
| `immutableFixturePins` | 无 | 新 | 6 个 fixture 的字节/来源不可变 pin |
| `caseAccounting` | 无 | 新 | 10382 / 10353 / Δ29 / ai 7 的机器口径 |
| `groupShapes` | 27 项 | 34 项 | 追加 7 个 ai-v1 组 |
| ai fixture 条目 | 无 | 新 | schema/exporter/csharp/acceptedBindings/bindings/groups/partialReason |

### 6.1 source pin 与历史 pin 的分离表示

- `baseline.fixtureFreezeCommit = 205506f...`：**仅旧五组** fixture 的字节冻结提交。
- `baseline.aiFixtureByteFreezeCommit = sourcePins.aiFixtureByteFreezeCommit = 09d2a4f...`：ai-v1 的真实字节冻结提交（blob `032d79e...`）；`004c247` 同 blob，但不是首次接受提交。
- `baseline.freezeCommit = sourcePins.catalogFreezeCommit = 004c247...`：当前 catalog 索引头。
- 每个 fixture 的 `sourceV4Commit` 是 fixture 内嵌的原始 V4 导出提交；旧五组早于 `205506f`，ai-v1 等于 `205506f` 但该提交**没有 ai-v1 路径**。`sourcePinRelation` 按 source pin 与各自 byte-freeze 的实际关系：旧五组 `source-pin-precedes-fixture-freeze`，ai-v1 也是 `source-pin-precedes-fixture-freeze`（205506f → 09d2a4f）。
- `immutableFixturePins` 在 catalog 内显式列出 6 个 id 的 `sha256/bytes/sourceV4Commit/fixtureFreezeCommit`；verifier 另有硬编码的 accepted pin 表。**未来只刷新 fixture + catalog hash、不 deliberate 改 verifier pin，会得到 `IMMUTABLE_FIXTURE_PIN_MISMATCH`**。这正是“不让任意 fixture hash 刷新绕过不可改边界”。

---

## 7. 各分母的准确读法

| 数 | 含义 | 不应当作 |
|---:|---|---|
| 73 | `V5-0-GOLDEN-MANIFEST.md` 的 G 需求行数 | 不是 fixture 数、不是 case 数、不是 Fact 数 |
| 10,389 | 六组 catalog 具体执行 case（旧 10,382 + ai 7） | 不是 73 的分子；不是 10,000 random 的父集重复相加 |
| 10,382 | 旧五组 catalog case 口径（历史） | 不是冻结审计 replay 口径 |
| 10,353 | 冻结 N-3 审计 replay 案例口径（旧五组，较窄） | 不是缺陷、不是漏洞、不是 catalog 新分母 |
| 10,000 | scoring `randomSamples` | 已含在 scoring 10,238 内，不另加 |
| 45 | 6 个 catalog C# reader 类的 `[Fact]`（Domain 42 + Ai 3） | 不是 JSON case，不是执行次数总和 |
| 6 | fixture 文件数 / catalog C# 类数 | 不是 73 |
| 7 | ai-v1 执行 case | 不是 20（20 是 CensusInstances 的 case key 数） |
| 20 | ai-v1 的 CensusInstances case key = 13 sourceFileSha256 + 7 cases | 不是 20 个执行 case |
| 1516 / 655145 / 592 / 17 | 审计归一化路径 / 节点实例 / GAP units / UNA units（六份 fixture，已含 ai 198 路径 / 1,438 节点） | 本次不改、不增样本 |
| 3 + 3 | AiReplayGoldenTests 3 Facts + AiReplayStateGoldenTests 3 accepted Facts | 后者不进 45；前者进 45 |

“full”标签不是字段级 100% 的证明：旧 catalog 的 `full` 行有各自限定断言；G-049 只 partial。verifier 只证明索引/计数/provenance，不替代领域行为测试。

---

## 8. Δ29 的确定分解（复算，不是漏洞）

### 8.1 两个口径

- **旧五组 catalog 口径（10,382）**：physical 6 + scoring 10,238 + round 25 + cheat 81 + shuffle 32。
- **冻结 N-3 审计 replay 口径（10,353）**：physical 6 + scoring 10,238 + round **2**（只 `roundTerminal` + `chankanRon`）+ cheat **75**（只 `tileMarkHandler` + `sleeveBloomHandler` + `beamSwapHandler`）+ shuffle 32。
- **Δ = 10,382 − 10,353 = 29**。这里的 `+23`/`+6` 是 **catalog 相对审计定义多出的条目**，不是 census 比 catalog 多 29，也不是新增样本：

| fixture | catalog | audit replay | Δ | catalog-only groups | audit replay groups |
|---|---:|---:|---:|---|---|
| round-v1 | 25 | 2 | +23 | `matchProgression` 16 + `decisionClock` 6 + `controlEpoch` 1 | `roundTerminal` 1 + `chankanRon` 1 |
| cheat-v1 | 81 | 75 | +6 | `tileMovement` 6 | 三个 handler 数组 20+32+23=75 |

- `10,382 − 29 = 10,353` 成立；`10,382 + 29` 不是本审计的 10,353。审计 §2.7/§5 的“round +23、cheat +6”必须按“catalog 侧多计”读，不能读成“census 比 catalog 多 29”。
- 这 29 **不加入** 新 catalog 的 10,389；新 catalog 只在旧 10,382 上加 ai 的 7。

### 8.2 机器复算命令与输出

```powershell
$c = Get-Content tests/v5-golden/catalog-v1.json -Raw -Encoding utf8 | ConvertFrom-Json
$five = ($c.fixtures | Where-Object id -ne 'ai-v1' | Measure-Object caseTotal -Sum).Sum
$round = ($c.fixtures | Where-Object id -eq 'round-v1').caseTotal
$cheat = ($c.fixtures | Where-Object id -eq 'cheat-v1').caseTotal
$roundAudit = 2; $cheatAudit = 75
$audit = 6 + 10238 + $roundAudit + $cheatAudit + 32
"five=$five audit=$audit delta=$($five-$audit) roundOnly=$($round-$roundAudit) cheatOnly=$($cheat-$cheatAudit)"
```

实测输出（native exit 0）：`five=10382 audit=10353 delta=29 roundOnly=23 cheatOnly=6`。该复算只读 catalog；不读/不改 fixture 或 census。

---

## 9. verifier 扩展与保留

扩展点（均在 `tools/verify-v5-golden-catalog.ps1`）：

1. `allowedSchemas` 增 `ai-v1 -> v5-ai-golden-v1`；fixture 计数 6；旧五组所有结构/语义检查原样保留。
2. ai schema 的 `groupShapes`：`cases` 逐 case 检查 id/kind/note/v4Call/v4/v5、V4 状态 16 字段、seats 6 字段、v5 authority/expected/authorityMapping；`decisionClock`/`recordedIntent`/`v5Observation`/`reproducibility`/`sourceFileSha256`/`unexportedOrMappedSamples` 最低结构/类型/length。
3. ai provenance：`sourceV4EngineCommit`/`sourceV4AiCommit`/`sourceV4DomainCommit`/`generatedAtHead` 对 fixture 实测；`v4ProductionSourceDirty` 必须 false；`sourceFileSha256` 13 条、每 sha 64 hex、lastCommit 40 hex。
4. 三 pin 分离：`sourcePins.catalogFreezeCommit == baseline.freezeCommit`、`historicalFixtureFreezeCommit == 205506f`、`aiFixtureByteFreezeCommit == 09d2a4f`；ai fixture 的 `fixtureFreezeCommit` 必须是 `09d2a4f`，写成 `205506f` 即 `AI_FIXTURE_FREEZE_COMMIT_MISMATCH`；`sourcePinRelation` 按 source pin 与实际 byte-freeze 计算。
5. immutable pins：catalog 内 6 条 + verifier 内 hardcoded accepted pin；ai 的 accepted byte-freeze 独立为 `09d2a4f`；任一 fixture 字节/来源/冻结提交变化即 `IMMUTABLE_FIXTURE_PIN_MISMATCH`、`IMMUTABLE_SOURCE_PIN_MISMATCH` 或 `AI_FIXTURE_FREEZE_COMMIT_MISMATCH`。
6. `acceptedBindings`：路径存在、Fact 数等于真实 `[Fact]` 数、`class` 必须与文件名/源码 `class` 声明一致、`commit` 必须等于已接受身份（state 绑定 `7da818d...`）且 `includedCommits` 含 `6d16812...`/`7da818d...`、报告存在；零 commit 或假类名即 fail。
7. `bindings`：jsonPath 不仅以 `$` 开头，还必须在 ai fixture 中真实存在（`AI_BINDING_PATH_MISSING`）；bound consumerPath 限定真实 AI 测试类，行号范围必须含对应字段读点 token（`AI_BINDING_ANCHOR_EVIDENCE_MISSING`）；UNBOUND 必须有 `GAP:N3-*` owner；四个必需 GAP 字段必须显式登记。
8. manifest 双表交叉：历史表状态保留原检查；当前 §8.5 必须有 73 条物理行且 ID 唯一；重复 ID 在比较/丢弃前即 `MANIFEST_CURRENT_DUPLICATE_ID`；73 行 ID、`coverage`、`manifestStatus` 必须与 catalog 一致，manifest-only full/漂移即 `MANIFEST_COVERAGE_MISMATCH` / `MANIFEST_CURRENT_STATUS_MISMATCH`。
9. `caseAccounting`：catalog/six、five/audit replay、delta 29、breakdown round +23 / cheat +6、aiIncrement 7、notExecutionCases 不计 case、random 子集不重加；不一致即 `CASE_ACCOUNTING_MISMATCH`。
10. G-049 guard：`coverage` 必须 `partial`、`manifestStatus` 必须 `pending-v4-replay`、`partialScope.fullClaim=false`、`closed=false`、`remainingGaps>=3`；任何 full/closed 声明即 fail。

**未降低断言**：原 76 个负例、原五组结构/锚点/SHA/ID/计数检查、原 `full` 需 csharpEvidence 的规则全部保留。新增检查只加不删；`SHA 正确` 不替代结构/行为检查。

---

## 10. 自测扩展（TEMP 副本，真实 fixture 不动）

`tools/tests/VerifyV5GoldenCatalog.Tests.ps1` 现在复制 6 份 fixture、6 个 exporter、6 个 catalog C# reader、`AiReplayStateGoldenTests` 和 manifest 到独占 TEMP；基线 native 0；每个负例 native 1 且必须出现预期错误码。原 76 个负例原样保留；前轮新增 15 个，D-R1 新增 7 个，D-R2 新增 4 个（总计 26 个新增、102 个负例）：

| # | 负例 | 注入 | 预期错误码 |
|---:|---|---|---|
| 15 | `ai-missing-case` | 删除 1 个 ai case，catalog 仍声明 7 | `GROUP_COUNT_MISMATCH` |
| 16 | `ai-duplicate-case` | 两个 case 同 id | `DUPLICATE_CASE_ID` |
| 17 | `ai-unknown-schema` | fixture/catalog schema 改 v2 | `FIXTURE_SCHEMA_UNKNOWN` |
| 18 | `ai-missing-semantic-field` | 删 `v5.expected.mappingBasis` | `CASE_STRUCTURE_MISSING` |
| 19 | `ai-wrong-type` | `decisionClock.baseMillis` 改字符串 | `CASE_STRUCTURE_TYPE` |
| 20 | `ai-count-drift` | ai `caseTotal=8` | `CASE_TOTAL_MISMATCH` |
| 21 | `ai-hash-drift` | fixture 追加换行，catalog 不刷新 | `FIXTURE_SHA_MISMATCH` |
| 22 | `ai-g049-full` | G-049 coverage=full、fullClaim=true | `G049_COVERAGE_MUST_BE_PARTIAL` |
| 23 | `ai-bad-consumer` | ai csharp.path 指向不存在文件 | `CSHARP_MISSING` |
| 24 | `ai-bad-source-anchor` | ai sourceV4EngineCommit 改 000… | `SOURCE_ENGINE_COMMIT_MISMATCH` |
| 25 | `ai-bad-binding-line` | binding consumerLines=99999 | `AI_BINDING_ANCHOR_INVALID` |
| 26 | `ai-delta29-merge` | `catalogCaseTotal=10418`（把 29 并入） | `CASE_ACCOUNTING_MISMATCH` |
| 27 | `ai-immutable-pin` | fixture 追加换行 + catalog/pin 同步刷新 | `IMMUTABLE_FIXTURE_PIN_MISMATCH` |
| 28 | `ai-g049-anchor-missing` | G-049 anchors=[] | `ANCHOR_MISSING` |
| 29 | `ai-accepted-binding-fact` | state binding `factCount=4` | `AI_ACCEPTED_BINDING_FACT_MISMATCH` |
| 30 | `ai-freeze-to-source-pin` | ai `fixtureFreezeCommit=205506f`（source-only）且同步 immutable pin | `AI_FIXTURE_FREEZE_COMMIT_MISMATCH` |
| 31 | `manifest-g049-full` | 仅 manifest 当前 G-049 coverage partial→full | `MANIFEST_COVERAGE_MISMATCH` |
| 32 | `manifest-coverage-drift` | 仅 manifest 当前 G-048 partial→none | `MANIFEST_COVERAGE_MISMATCH` |
| 33 | `ai-accepted-commit-zero` | acceptedBindings commit=40 个 0 | `AI_ACCEPTED_BINDING_COMMIT_MISMATCH` |
| 34 | `ai-accepted-fake-class` | acceptedBindings class=NonexistentTests | `AI_ACCEPTED_BINDING_CLASS_MISMATCH` |
| 35 | `ai-anchor-line-one` | decisionClock consumerLines=1（using System，无读点） | `AI_BINDING_ANCHOR_EVIDENCE_MISSING` |
| 36 | `ai-binding-nonexistent-path` | binding jsonPath=`$.notARealFixtureField` | `AI_BINDING_PATH_MISSING` |
| 37 | `manifest-duplicate-identical` | 当前 G-049 partial 行后原样重复一行 | `MANIFEST_CURRENT_DUPLICATE_ID` |
| 38 | `manifest-duplicate-g049-partial-full` | G-049 partial 后紧跟同 ID `full` | `MANIFEST_CURRENT_DUPLICATE_ID` |
| 39 | `manifest-duplicate-g049-full-partial` | G-049 `full` 后紧跟同 ID `partial` | `MANIFEST_CURRENT_DUPLICATE_ID` |
| 40 | `manifest-duplicate-non-g049` | 当前 G-048 行原样重复 | `MANIFEST_CURRENT_DUPLICATE_ID` |

实跑汇总：**SELFTEST PASS cases=103 positives=1 negatives=102；real-fixtures-unchanged=true，native exit 0**。日志与 SHA 见 §11.2；真实 6 份 fixture 的 before/after SHA 相同。

---

## 11. 实际命令、native exit、日志与 SHA256

### 11.1 catalog verifier

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
```

实测输出（native exit 0）：

```text
PASS catalog=D:\majiang-wt\n3-c1-index-20261025\tests\v5-golden\catalog-v1.json sha256=236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D fixtures=6 requirements=73 cases=10389 random=10000 facts=45 coverage=full:17/partial:21/none:32/v5-only:3
```

日志：`C:/Users/sncdc/AppData/Local/Temp/n3c1-r3-final-verify.log`，522 bytes，SHA256 `8CB83E2C66DD8F9B64DD8539656994DA3BFB54CDDD04E2C1C96C2952018F4012`。

### 11.2 自测

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5GoldenCatalog.Tests.ps1 -LogPath <TEMP>/n3c1-selftest.log
```

- 命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5GoldenCatalog.Tests.ps1 -LogPath C:/Users/sncdc/AppData/Local/Temp/n3c1-r3-selftest.log`；native exit **0**；stdout 末行 `SELFTEST PASS cases=103 positives=1 negatives=102`、`real-fixtures-unchanged=true`；日志 60,957 bytes，SHA256 `C63E70B548CB6CDF279B4ED320F51D831F47A7E3C71FA94369B3AF5D2D92CA93`；D-R1 的 7 个负例（#30-#36）与 D-R2 的 4 个重复 ID 负例（#37-#40）均 `exit=1` 且命中预期码。
- 原 98 负例全部保留（name/error-code 对照 98/98），其中原 76 负例 76/76；新增 4 个重复 ID 反例的预期码见 §10。

### 11.3 AI 定向测试

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class "Mahjong.Ai.Tests.AiReplayGoldenTests" -noColor -noLogo
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class "Mahjong.Ai.Tests.AiReplayStateGoldenTests" -noColor -noLogo
```

| 命令 | native exit | 实际选中 | 日志 | SHA256 |
|---|---:|---|---|---|
| Ai.Tests Release build | 0 | 0 warning / 0 error | `%TEMP%/n3c1-ai-build-final.log` | `82F5F39E471B37CB961FC01F94DBB910E0EC58ACA401D613898FD0655E772CD3` |
| `AiReplayGoldenTests` | 0 | Total 3, Failed 0, Skipped 0, Not Run 0 | `%TEMP%/n3c1-ai-replay-final.log` | `EE60EF805EB47EB902D5922FD22B611B57BBBA83B998F47288EBCB529F243013` |
| `AiReplayStateGoldenTests` | 0 | Total 3, Failed 0, Skipped 0, Not Run 0 | `%TEMP%/n3c1-ai-state-final.log` | `6E73C8483C1B764D32B9FF31E1A1A218C92ECC76034AD57440E9751B174B42A5` |

注意：这是 direct runner 的 `-class` 实际选中数，不是被忽略的 `dotnet test --filter` 整项目数。没有重跑 PG/WSS/Unity/Gradle/所有 .NET 全量；Domain 42 Facts 引用已接受运行记录，不冒充本次实跑。

### 11.4 当前文件 SHA256

| 文件 | bytes | raw SHA256 | LF SHA256 |
|---|---:|---|---|
| `tests/v5-golden/ai-v1.json` | 51,271 | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2` |
| `tests/v5-golden/catalog-v1.json` | 586,645 | `236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D` | `FB8A64FFA3F09E9C60D461B6704C084196B6092F58AB5D766F55000EB8E5D20F` |
| `doc/v5-重构/V5-0-GOLDEN-MANIFEST.md` | 77,396 | `6CA8A5521C73A0315F17B9162E9D17CD0A932D3F0E618FD56AD390C02423C057` | `4D8CC1C1FA5BA73A20AA0735C7E2206E5A0D161790B3AC9CC4285C234A5E9BA0` |
| `tools/verify-v5-golden-catalog.ps1` | 94,160 | `9F0D2A823C87D8F87B4D2B26C7479076F41BB21120647562CCFFF5243FF31DFB` | `442542DAA9B5591D917AC8210F065EB46C11A825B1CBA57D0D156B3A429942CD` |
| `tools/tests/VerifyV5GoldenCatalog.Tests.ps1` | 63,209 | `5EB98735DD6D21BF95742B0948DD3DFDFEA05A81CB427D601585A5A668D6C294` | `0CDC98233467DDF8D5DC53B2555C227C0D0D80C371F1468FEB159CFADAE7FCE3` |
| `doc/v5-重构/V5-N3-C1-CATALOG-UPDATE.md` | 自指，不在文内嵌自身 SHA | 自指，不在文内嵌自身 SHA | 自指，不在文内嵌自身 SHA（commit 后以 `git show HEAD:doc/...` 复算） |

### 11.5 编码与 diff

- 5 个文件均为 UTF-8 无 BOM；旧文件 CRLF 保持，新文档与仓库文档一致。逐文件 BOM 首字节检查见 §11.1 日志/本文证据。
- `git diff --check` native exit 0；`git status` 交付前 clean（commit 后）。
- `ConvertFrom-Json` 对 catalog 解析成功；fixture 6 份 JSON 解析成功。

---

## 12. PASS / FAIL / BLOCKED / NOT_RUN

| 项 | 状态 | 证据 |
|---|---|---|
| catalog verifier 正例 | **PASS** (exit 0) | 6/73/10389/10000/45；§11.1 |
| 自测正例 + 102 负例 | **PASS** (见 §11.2) | 每负例 exit1 + 预期码；真实 fixture 不变 |
| AI 定向 `AiReplayGoldenTests` | **PASS** 3/3 | exit 0 |
| AI 定向 `AiReplayStateGoldenTests` | **PASS** 3/3 | exit 0 |
| AI Release build | **PASS** 0 warn/0 err | exit 0 |
| G-049 | **PARTIAL** | mappingBasis/mappingLimits/text + 全后态差分未闭环 |
| N3 | **BLOCKED** | 仍依赖其他 G 条目与 AI host/cheat intent |
| G1 | **BLOCKED** | 不因本目录增量关闭 |
| G5 | **NOT_COMPLETE** | 未宣告 |
| PG / WSS / Unity / IL2CPP / Gradle 全量 / 真机 / 物理网络 / 远端 CI | **NOT_RUN** | 本任务不重跑、不冒充 |
| Domain 42 Facts 本次 | **NOT_RUN（引用已接受）** | 未改 Domain C#，不冒充本次实跑 |

---

## 13. 已知问题（按复核协议一次给全）

| ID | 严重级 | 精确位置 | 复现 | 实际 vs 期望 | 证据 SHA | 修复/验收 |
|---|---|---|---|---|---|---|
| N3C1-F01 | INFO | `V5-G1-N3-GOLDEN-FIELD-AUDIT.md` §2.7/§5 | 读“10382 vs 10353，Δ29，round +23、cheat +6” | 实际 Δ=10382−10353=29，+23/+6 是 catalog 侧多计；易被误读为 census 多 29 或两分母可相加。期望明确方向与集合。 | 审计报告 raw `ecae055e...`；本文 §8 复算 | 本文 §8 + catalog `caseAccounting` + verifier `CASE_ACCOUNTING_MISMATCH` 机器固定；不改审计原文 |
| N3C1-F02 | MINOR | `V5-0-GOLDEN-MANIFEST.md` §8.3.2/§8.4/§8.5（旧 HEAD） | 旧文仍写 5 fixtures / 42 Facts / partial20 none33 / G-049 none / 无 ai 命令 | 实际 ai-v1 已接受并消费；旧 manifest 是 N3-C1 前口径。期望当前口径与历史口径分开，且当前 §8.5 被机器交叉核对。 | 旧 manifest Git blob（HEAD）；新 raw `6CA8A552...` | §0.3 + 8.3.2/8.4 + G-049 partial；旧 §0.2 保留；verifier 当前表核对 |
| N3C1-F03 | MINOR | `V5-G1-N3-GOLDEN-FIELD-AUDIT.md` §3.6 的 C#/exporter 行号 | 按旧行号打开 `AiReplayGoldenTests.cs:228` 等，已不是对应值 | 实际只在 6d16812/7da818d 移动了 C# 行号：decisionClock 260/261/264/265、DeepEquals 599/635；exporter 自 8254c9b 起内容零 diff，旧 `0E4289...` 是 Git blob/LF，当前 raw `F70DA6...` 只是 CRLF checkout。期望保留旧→新差异并区分口径。 | 旧 consumer blob `3969289D...`；新 LF `F1B0AAE4...`/raw `24F911...`；旧 exporter LF `0E4289...`/新 raw `F70DA6...` | §5.2 逐项旧→新；verifier 行号/读点实测 |
| N3C1-F04 | INFO | `catalog-v1.json` baseline（旧） | 旧 `freezeCommit=205506f`，但本任务索引头是 004c247 | 实际三 pin 必须分开：五组历史 freeze、ai 字节 freeze、当前 catalog index。期望显式 `fixtureFreezeCommit`/`aiFixtureByteFreezeCommit`/`sourcePins`。 | 旧 HEAD catalog `A66BD71F...`；审计快照 `16F7FD7F...`；新 `236C8E0E...` | §6.1；verifier `SOURCE_PIN_*`/`AI_FIXTURE_FREEZE_COMMIT_MISMATCH` |
| N3C1-F05 | INFO | `AiReplayStateGoldenTests` | 把 3 个 accepted state Facts 与 catalog 45 Facts 混加 | 实际 45 = 42 Domain + 3 AiReplayGoldenTests；state 类 3 Facts 是独立证据。期望分列。 | `53B45095...`；`CODEX-V5-N3-AI-R2-ACCEPTANCE.md` | §5/§7；catalog `acceptedBindings` 明确 `not in catalog Fact denominator` |
| N3C1-F06 | MAJOR（范围） | `AiReplayGoldenTests`/fixture | 试图把 G-049 标 full 或“全部 AI 移植” | 实际 mappingBasis/mappingLimits/event-text/action-text 无 C# 值级读取，且无全 case C# 后态 vs V4 全树差分；AI host/cheat intent 未导出。期望 partial。 | `E898...` fixture；`24F911...` consumer；`V5-N3-AI-REPLAY-BINDING.md` §5 | G-049 `partialScope` + verifier `G049_*`；不 full |
| N3C1-F07 | INFO | ai fixture top fields | 读 `generatedAtHead=b49911c` 以为 fixture 等于 b49911c 提交树 | 实际 fixture 在 b49911c 生成时含 test-only dirty 路径，随后 09d2a4f 提交；`v4ProductionSourceDirty=false`。期望按“生成时 HEAD + dirty 声明”读。 | `E898...`；blob `032d79e...`；09d2a4f stat | §3；不修改 fixture |
| N3C1-F08 | MINOR（测试装置，已修复） | `tools/tests/VerifyV5GoldenCatalog.Tests.ps1` 的 `New-BaseTree` | 首次全量自测正例 exit 1，报 `AI_ACCEPTED_BINDING_REPORT_MISSING` | 实际 TEMP 树只复制了 manifest，未复制已接受的 `V5-N3-AI-REPLAY-BINDING.md`；verifier 按设计拒绝缺失报告。期望 TEMP 树也复制该只读报告。 | 首轮日志 `A99D38...`；91/91 负例 PASS，仅正例失败 | 已复制报告；本轮返修后 `cases=103 positives=1 negatives=102`、native 0，日志 `C63E70B5...`（§11.2） |
| D-R1-01 | MAJOR（已修复） | catalog notice/baseline/immutable pin/ai entry；verifier accepted pin 与 freeze 检查；manifest §0.3；本文 §3/§6.1 | `git cat-file -e 205506f:tests/v5-golden/ai-v1.json`；`git ls-tree 205506f -- ...`；对比 09d2a4f/004c247 | 实际 205506f native128、0 条；09d2a4f/004c247 各 1 条 blob `032d79e...`。期望 205506f 只作 AI source pin/旧五组 freeze，AI byte-freeze=09d2a4f。 | caller `REVIEW.md` `91ED48...`；`provenance.log` `A067F4AA...`；本轮 `4257B913...` | 三 pin 分离；ai immutable pin=09d2a4f、relation=precedes；负例 `ai-freeze-to-source-pin` 期望 `AI_FIXTURE_FREEZE_COMMIT_MISMATCH` |
| D-R1-02 | MAJOR（已修复） | verifier manifest 交叉检查（原遇 §8.5 即 break） | 仅改独立 manifest 当前 G-049 coverage partial→full，catalog 保持 partial | 实际原 native0/PASS；期望 native1。已新增当前 §8.5 73 行 ID/coverage/status 双向核对。 | caller `probes-summary.json` `FE41BD94...`；`review-probes.ps1` `44B81A64...` | 负例 `manifest-g049-full`、`manifest-coverage-drift` 均期望 `MANIFEST_COVERAGE_MISMATCH` |
| D-R1-03 | MAJOR（已修复） | verifier Test-LineRangeAnchor/Test-AiBindings/Test-AcceptedBindings | (a) accepted commit=40 个 0；(b) accepted class=NonexistentTests；(c) decisionClock lines=1；(d) jsonPath=`$.notARealFixtureField` | 实际原 4 项均 native0/PASS；期望各 native1+具体 code。已校验 accepted 身份/真实 class、binding path 存在、行锚含字段读点 token、consumerPath 限定真实 AI 类。 | 同一 `probes-summary.json`/`review-probes.ps1` | 4 负例 `ai-accepted-commit-zero`/`ai-accepted-fake-class`/`ai-anchor-line-one`/`ai-binding-nonexistent-path`，分别期望 `AI_ACCEPTED_BINDING_COMMIT_MISMATCH`/`AI_ACCEPTED_BINDING_CLASS_MISMATCH`/`AI_BINDING_ANCHOR_EVIDENCE_MISSING`/`AI_BINDING_PATH_MISSING` |
| D-R2-01 | MAJOR（已修复） | verifier 当前 §8.5 解析（原 `ContainsKey` first-wins，行数按 distinct keys） | caller `repro-duplicate-manifest.ps1`：保留 G-049 partial，紧跟同 ID full | 实际原 native0/PASS，74 物理行只算 73 distinct ID；期望 native1。已改为重复 ID 在写入前 `MANIFEST_CURRENT_DUPLICATE_ID`，并核对 73 条物理行，不做 last-wins/first-wins。 | caller `REVIEW.md` `A39C53...`；`manifest-duplicate-g049.md` `57C209...`；`manifest-duplicate-g049.log` `8CB83E...`；`repro-duplicate-manifest.ps1` `3468E7...` | 4 负例 #37-#40：相同重复行、G-049 partial→full、full→partial、非 G-049 重复；均期望 `MANIFEST_CURRENT_DUPLICATE_ID` |

无 BLOCKER。D-R1-01/02/03 与 D-R2-01 均在原 5 文件范围内修复；N3/G1/G5 未关闭属于范围边界，不是本目录增量缺陷。

### 13.1 D-R1/D-R2 修复映射

| caller ID | 直接修复 | 负例 | 验收断言 |
|---|---|---|---|
| D-R1-01 | catalog 增 `baseline.aiFixtureByteFreezeCommit`、`sourcePins.aiFixtureByteFreezeCommit`，ai 条目/freeze pin 改 `09d2a4f`、relation 改 precedes；verifier 加 `$script:AiFixtureByteFreezeCommit`、accepted pin 改 `09d2a4f`、ai 专用 freeze 检查；manifest §0.3/本文 §3/§6.1 同步 | `ai-freeze-to-source-pin` (#30) | 基线 native0；负例 native1 且 `AI_FIXTURE_FREEZE_COMMIT_MISMATCH`；真实 Git `cat-file`/`ls-tree` 已写 §3 |
| D-R1-02 | verifier 在历史表检查后解析当前 §8.5，核对 73 行 ID、coverage、status，G-049 不得 full | `manifest-g049-full` (#31)、`manifest-coverage-drift` (#32) | 基线 native0；仅改 manifest 的负例 native1 且 `MANIFEST_COVERAGE_MISMATCH` |
| D-R1-03 | verifier 加 accepted identity（`7da818d`/included）、真实 class 声明、binding jsonPath 存在、行锚读点 token、consumerPath 白名单；catalog C# class 也做声明校验 | `ai-accepted-commit-zero` (#33)、`ai-accepted-fake-class` (#34)、`ai-anchor-line-one` (#35)、`ai-binding-nonexistent-path` (#36) | 基线 native0；四负例各自 native1 且命中对应 `AI_ACCEPTED_BINDING_COMMIT_MISMATCH`/`AI_ACCEPTED_BINDING_CLASS_MISMATCH`/`AI_BINDING_ANCHOR_EVIDENCE_MISSING`/`AI_BINDING_PATH_MISSING` |
| D-R2-01 | verifier 当前 §8.5 在写入/丢弃前拒绝重复 ID，并以物理行数 73 校验；历史表检查不变 | `manifest-duplicate-identical` (#37)、`manifest-duplicate-g049-partial-full` (#38)、`manifest-duplicate-g049-full-partial` (#39)、`manifest-duplicate-non-g049` (#40) | 基线 native0；四负例均 native1 且 `MANIFEST_CURRENT_DUPLICATE_ID`；caller 74 行反例由 native0 变 native1 |

D-R2-01 的新信息来自 `16722f8` 新增的当前 §8.5 解析路径；`227b328` 不存在该代码（当时遇 §8.5 即 break），因此首轮复核无法发现重复 ID first-wins。

---

## 14. 依赖与 G1 后续

- G-049 关闭至少还需：`mappingBasis`/`mappingLimits` 的值级断言或导出侧排除；`v4.events[*].text`/`v4Call.action.text` 的导出侧排除或 C# 字段；全 case 的 C# 后态 vs V4 全树差分；AI host/cheat intent 的独立导出与测试。
- 其他 N3 包的缺口（B4/B5/B6/B7/B8/B9/B10/B11/E1/E2/E3 等）仍按审计 §9 独立负责；本任务只登记 ai-v1，不顺带重算其他 G 条目的覆盖。
- G1 仍依赖所有 N3 子项、G-028a、P3 TableRuntime 等；本任务不宣告 G1/G5。
- movement/reversed-wall/visibility/typed-random 的增量登记由 GLM 原线程负责；本任务只增量登记 ai-v1，未触碰其 3 个台账文件。

---

## 15. 边界声明

- 本文与 catalog 只证明索引、哈希、计数、锚点与 provenance；不证明领域行为 100%、不替代 PG/WSS/Unity/Gradle/真机/物理网络/CI。
- V4 仍是唯一生产架构；V5 未宣布生产就绪。
- 没有 `complete.flag`，没有 push/PR/CI/部署，没有环境/信任修改，没有读改兄弟未提交文件或主 checkout 用户文件。
