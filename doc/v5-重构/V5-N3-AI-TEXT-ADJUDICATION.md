# V5 N3 P-ALIGN-A：B6 mappingBasis/mappingLimits（11）+ B9 v4Call.action.text（7）逐条裁决证据

日期：2026-10-26

工作树：`D:/majiang-wt/n3-ai-adjudication`（独占），分支 `wt/v5-n3-ai-adjudication`，冻结基线 `6c3573620f61e8cbb6a13c8c4b81abef0b173fd9`（PR19 `feat/v5-1-integration` 当前头）

交付文件：本报告 + `V5-N3-AI-TEXT-ADJUDICATION.tsv`（18 行，25 列；TSV SHA-256 见 §7）

任务性质：已接受台账 §10 **P-ALIGN-A** 的明确后续；只做 18 条 pending 的逐条举证与 proposedVerdict，供 caller 复核后统一改共享台账。**本报告不宣布门 2、不宣布 G1/G5、不宣布完整移植；V4 仍是唯一生产架构；不创建 `complete.flag`。**

---

## 0. 结论摘要

| 口径 | 数量 |
|---|---:|
| 本批冻结单元 | **18**（B6 = 11，B9 action.text = 7） |
| proposedVerdict = classified | **16** |
| proposedVerdict = pending | **2**（B6 `mappingLimits[*]` 的 claimed-discard 与 per-discard riichi 两条） |
| proposedVerdict = bound | **0** |
| 复用已接受证据但本轮未重跑 | 历史 AI 48/48 变异矩阵（events 非 text 字段） |
| 第 1 轮 fresh 构建 / 直接 runner / TEMP 探针 | 全部实际执行，见 §6；第 2 轮仅交付修订，行为 NOT_RUN（§0.1） |

- **B6**：`mappingBasis` 7 条全部 classified（映射元数据；其描述的结构化行为 `accepted`/`rejectionCode` 已有 C# 值级消费者）；`mappingLimits` 容器 1 条 classified（纯容器）、sleeve 隐私限制 1 条 classified（有意排除、非缺失行为）、claimed-discard 与 per-discard riichi 2 条 **pending**（说明文字存在不能替代值级绑定；冻结 fixture 的 `visibleDiscards=[]`，没有非空实例，也没有 C# 值级断言）。**B6 全包仍无行为变异反证，故不设 bound。**
- **B9**：7 条 `action.text` 全部 classified（表示差异）。逐条确认其为 Kotlin data class 默认 `toString()`，不进入权威状态/哈希、无稳定跨语言协议合同；同一 case 的 `kind/player/tileId/optionId` 已由 C# 真实 `AiDecisionService.Submit` 与真实回放断言，且本报告用 TEMP 副本证明改 text 通过、改结构化字段失败。**没有借已 classified 的 `events[*].text` 结论搭车。**
- 当前台账向量仍为 **411 bound / 82 classified / 99 pending**；本批只提议其中 18 条的去向，不改共享 TSV、不改校验器、不改已接受报告。

### 0.1 第 1 轮调用者复核返修（D-AI-01 / D-AI-02）

- **D-AI-01（MINOR，已修）**：按 caller 精确 expectedFiles 将两个交付 `git mv` 为 `V5-N3-AI-TEXT-ADJUDICATION.md` / `.tsv`；报告内文件名、复现命令与引用同步更新；`P-ALIGN-A` 只保留为任务语义名。`base 6c35736..HEAD` 净 diff 仅新增这两个 expectedFiles，旧 `P-ALIGN-A` 交付路径不存在；TSV 18 行 × 25 列，字节内容不变（hash 见 §7）。
- **D-AI-02（MINOR，已修）**：旧 `evidence-sha256.txt` 原样保留供审计（其中含 1 条自引用行：旧摘要 `0a2cb429…`，该文件实际 SHA-256 为 `097F15FA…`）；新增 `evidence-manifest-v2.txt`，明确排除自身、旧 manifest 与摘要文件，共 **56** 条，逐项校验 **56 OK / 0 missing / 0 mismatch / 0 self / 0 old**（`verify-manifest-v2.log`）。manifest 摘要外置登记见 §7，不再尝试自引用固定点，不覆盖任何原测试日志。
- **本轮行为 NOT_RUN**：D-AI-01/D-AI-02 只改交付文件名与 manifest，不改源码/测试/fixture；引用 caller 第 1 轮已实跑证据：build.log `5D52156B77DD54063298969D40D57DBB113653049BF434E4A4CC704D48402D94`、baseline.xml `41F359FC526C24C1BA855DDC5A155135719E05F19710BAD8464BEE3A4E417DDB`、P13.log `0E7A11AB05916A7069E085E7817FCBEB8C61FBA31335B1DCC44AEE861C6113BD`、matrix.csv `6C3E4512AEF178611BD5C0CA96C52CB5B6A73BED22F95BD415094C919200FC87`，不重复全套测试。

---

## 1. 授权、开工核实与边界

### 1.1 开工核实（实际命令与结果）

```text
git rev-parse --show-toplevel  -> D:/majiang-wt/n3-ai-adjudication
git rev-parse HEAD             -> 6c3573620f61e8cbb6a13c8c4b81abef0b173fd9
git branch --show-current      -> wt/v5-n3-ai-adjudication
git status --porcelain=v1      -> (空)
git worktree list              -> 本路径为 [wt/v5-n3-ai-adjudication]，与 n3-note-adjudication 等兄弟树并列、互不重叠
```

- 开工时 HEAD 与用户给定冻结基线逐字一致；工作树干净；本任务只在上述独占 worktree 内读写。
- 未读取/修改兄弟 `D:/majiang-wt/n3-note-adjudication` 的未提交工作；未改主 checkout `D:/majiang`、规划树 `D:/majiang-v5-planning`。
- 未 push、未建 PR、未触发 CI、未转派、未改机器环境/信任/部署；无子任务、无子 MR。

### 1.2 expectedFiles 与实际改动

本任务只新增两个文件（不修改任何既有文件）：

| # | 文件 | 类型 | 说明 |
|---|---|---|---|
| 1 | `doc/v5-重构/V5-N3-AI-TEXT-ADJUDICATION.md` | 新增 | 本报告 |
| 2 | `doc/v5-重构/V5-N3-AI-TEXT-ADJUDICATION.tsv` | 新增 | 18 行机器可读裁决记录（原台账 10 个 identity 字段 + 原 fixCommit/evidence/reason + 本轮证据/规则/裁决/owner/revisit） |

上述两个路径是 caller `thread.expectedFiles` 的精确文件名（D-AI-01 返修后）；`P-ALIGN-A` 只保留为任务语义名。

明确未改：`dotnet/src/**`、`dotnet/tests/**`、`tests/v5-golden/**`、`catalog-v1.json`、manifest、`doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.md`、`doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv`、`tools/verify-v5-n3-closeout-ledger.ps1`、任何已接受报告、`V5-0-1-IMPLEMENTATION-TASKS.md`、`DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、Unity 环境/Resources/Burst。

### 1.3 只读输入

实际 UTF-8 读取：`AGENTS.md`、`doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md`（尤其 N-3 收口规则）、`doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`、`doc/v5-重构/CODEX-V5-N3-CLOSEOUT-ACCEPTANCE.md`、`V5-G1-N3-CLOSEOUT-LEDGER.md/tsv`、`V5-G1-N3-GOLDEN-FIELD-AUDIT.md`、`V5-N3-AI-REPLAY-BINDING.md`、`CODEX-V5-N3-AI-R2-ACCEPTANCE.md`，以及所需 Kotlin/C# 源码与测试。

`TEMP/TMP` 在本任务所有构建、runner、探针进程内均指向 D 盘证据根：

```text
D:/majiang-wt/n3-ai-adjudication/artifacts/TEMP/p-align-a-6ecd555edcd347a48dfeeb792966a4dd
```

该目录被仓库 `.git/info/exclude` 的 `artifacts/` 规则忽略，不进 commit。

---

## 2. 冻结输入与身份

### 2.1 输入哈希（本轮实测）

| 输入 | raw SHA-256 | LF SHA-256 | Git blob SHA-1 |
|---|---|---|---|
| `tests/v5-golden/ai-v1.json` | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2` | `032d79e95e45bde9cb29046cad5f49f338fa78f3` |
| `engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt` | `F70DA6A4AFBB06FA1BC80F2D0A6573BFB5A63DD302B2CA52122A551D623C76F8` | `0E4289DE81E9BCC31D9D37E1A22D79D63816E807A4619912A70B787E1E3DA863` | `d380b9610d42a6bae2f43e3e73f7c1a804729f60` |
| `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` | `24F91115233E47F5E6FC992A68EBB570807AC9D9D68FBB8544C80BBF0B06A9F5` | `F1B0AAE406BD453CE0E48A96167154D956E860F87F3C56646D93A5829879768E` | `da2c279d628d8537c6bf9e46a562b971f2e18d26` |
| `dotnet/tests/Mahjong.Ai.Tests/AiReplayStateGoldenTests.cs` | `53B45095A5F4935B7960645EE674AFAFC4C7F58B6F1189577BE9A19D54E57FB0` | `0032D47C87EFA1791F28ADC884A21FCE5837A35CDA4004E9CAA6CB83F504411A` | `a4841175e8dcf88852fb47dcdcb5a0292cad4f0e` |
| `doc/v5-重构/V5-N3-AI-REPLAY-BINDING.md` | `DD94EF8F32D9759FD1FBE2B6974D728CBDC27414C7F3F31842E692A3B74ECB1A` | — | `06107ee4329047f8b1d22448e092c4315fc5e779` |
| `doc/v5-重构/CODEX-V5-N3-AI-R2-ACCEPTANCE.md` | `0B48BE12D87C1A4E43EF1E5B575B64C3AE85FD8711530014A152C365533F24B1` | — | `106b683658c6feba2e4925070627bee9ddc7a378` |
| `doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md` | `5A440C58902C260D607016546A37B243DAAA75DCFE99401D3F5DE7B202CB80E9` | — | `ba1bec71c8d06b459057ad8c7c399e9aeb83cefd` |
| `doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv` | `EDB9B158902779631683325FB060A3E7755DF75FB28A3688FFE5CF7F50FAF6A5` | `01AA01DDD699C630325BCF61A076E7DF32D5A5F59F29E0D2E63376FEE1147795` | `bfd93818169e0afde6a61cb320e02767636f876e` |
| `doc/v5-重构/CODEX-V5-N3-CLOSEOUT-ACCEPTANCE.md` | `2F1C922A0DD0FCE513BF883A25E8445305E30088B054752F115BDBCB250E6113` | — | `635da796e2f248eafa11a93430974a028152869a` |

**与已接受证据的比对**：

- `ai-v1.json` 的 raw / LF / blob 与 `V5-N3-AI-REPLAY-BINDING.md §7` 和已接受 AI 验收逐字一致；fixture **未改**。
- `AiReplayGoldenTests.cs` raw 与已接受 AI 文档的 raw `24F91115…` 一致；`AiReplayStateGoldenTests.cs` 当前 raw 为 CRLF 形态 `53B45095…`，LF 归一化 `0032D47C…` 与已接受文档的 `0032D47C…` 一致，Git blob `a484117…` 可复核；这是 checkout 行尾形态差异，不是内容变更，本报告不声称 raw 字节相等。
- `V5-N3-AI-REPLAY-BINDING.md`、`CODEX-V5-N3-AI-R2-ACCEPTANCE.md`、`V5-G1-N3-GOLDEN-FIELD-AUDIT.md` 的 raw 分别等于台账登记 `EV-AI-DOC`、`EV-AI-ACC`、`EV-AUDIT` 的 SHA；未改已接受报告。
- 台账 TSV raw 等于其 §13 交付记录 `EDB9B158…`；本任务只读。
- `V5AiGoldenExporterTest.kt` 的最近两次提交为 `b49911c`（2026-10-24 06:08:00 +0800，add V4 AI decision golden replay evidence）与 `09d2a4f`（2026-10-24 06:21:47 +0800，bind AI golden replay to recorded V4 intent and state）；fixture 也由同一提交序列产生。当前文件与 fixture 均为冻结输入，本任务未改。

### 2.2 18 条身份与原文

台账 TSV 的 `id = <包>|<fixture>|<caseId>|<path>|<idx>`，17 列。按 `package` 精确筛选得到 **19** 行：B6 11 行 + B9 8 行；其中 B9 的 `$.cases[*].v4.events[*].text` 1 行早在台账中为 `classified`（`V5-N3-AI-REPLAY-BINDING.md`/AI 验收已签收），**不在本批 18 条内、未重开**。本批 18 = B6 11 + B9 `$.cases[*].v4Call.action.text` 7。

| # | 台账 id（简） | histClass | kind | parent（原文摘要） | 原 verdict |
|---|---|---|---|---:|---|
| 1 | `N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|1` | UNBOUND | string | `-` | pending |
| 2 | `N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|2` | UNBOUND | string | `-` | pending |
| 3 | `N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|3` | UNBOUND | string | `-` | pending |
| 4 | `N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits\|1` | UNBOUND | array | `controlEpoch=1;deadlineMillis=24000;…;sessionId=…;stateVersion=0` | pending |
| 5 | `N3-B6\|ai-v1\|AI-001-legal-intent-before-deadline\|$.cases[*].v5.expected.mappingBasis\|1` | UNBOUND | string | `accepted=true;mappingBasis=…;rejectionCode=None` | pending |
| 6 | `N3-B6\|ai-v1\|AI-002-old-round\|…mappingBasis\|1` | UNBOUND | string | `accepted=false;…;rejectionCode=StaleRound` | pending |
| 7 | `N3-B6\|ai-v1\|AI-003-old-decision\|…mappingBasis\|1` | UNBOUND | string | `accepted=false;…;rejectionCode=StaleDecision` | pending |
| 8 | `N3-B6\|ai-v1\|AI-004-old-epoch\|…mappingBasis\|1` | UNBOUND | string | `accepted=false;…;rejectionCode=StaleEpoch` | pending |
| 9 | `N3-B6\|ai-v1\|AI-005-wrong-seat\|…mappingBasis\|1` | UNBOUND | string | `accepted=false;…;rejectionCode=StaleDecision` | pending |
| 10 | `N3-B6\|ai-v1\|AI-006-candidate-not-legal\|…mappingBasis\|1` | UNBOUND | string | `accepted=false;…;rejectionCode=IllegalAction` | pending |
| 11 | `N3-B6\|ai-v1\|AI-007-exact-deadline\|…mappingBasis\|1` | UNBOUND | string | `accepted=false;…;rejectionCode=DeadlineExpired` | pending |
| 12 | `N3-B9\|ai-v1\|AI-001…\|$.cases[*].v4Call.action.text\|1` | UNBOUND | string | `kind=Discard;optionId=opt-0;player=1;text=DiscardTile(…);tileId=0;type=DiscardTile` | pending |
| 13 | `N3-B9\|ai-v1\|AI-002-old-round\|…action.text\|1` | UNBOUND | string | 同上（DiscardTile / MAN1 copy0） | pending |
| 14 | `N3-B9\|ai-v1\|AI-003-old-decision\|…action.text\|1` | UNBOUND | string | 同上 | pending |
| 15 | `N3-B9\|ai-v1\|AI-004-old-epoch\|…action.text\|1` | UNBOUND | string | 同上 | pending |
| 16 | `N3-B9\|ai-v1\|AI-005-wrong-seat\|…action.text\|1` | UNBOUND | string | 同上 | pending |
| 17 | `N3-B9\|ai-v1\|AI-006-candidate-not-legal\|…action.text\|1` | UNBOUND | string | `kind=Pass;optionId=null;player=1;text=Pass(player=1);tileId=null;type=Pass` | pending |
| 18 | `N3-B9\|ai-v1\|AI-007-exact-deadline\|…action.text\|1` | UNBOUND | string | 同 #12 | pending |

**校验**：交付 TSV 的前 10 列（id/package/fixture/caseId/path/idx/kind/parent/histClass/verdict）及原 `fixCommit`/`evidence`/`reason` 三列与冻结台账逐行逐字节比对，`bad=0`、18 行。完整原文（含长 parent、长 reason）在 TSV 中，不在本报告里二次转写。

`parent` 在本台账中不是 JSON 结构父路径，而是 census 记录的值上下文（例如 `accepted=…;mappingBasis=…;rejectionCode=…` 或 `kind=…;optionId=…;text=…`）；本报告原样保留，不按字段名改写。

---

## 3. 规则应用口径

### 3.1 N3-META-02（本报告逐行应用，不按字段名通排）

> exporter 测试手工生成的映射解释/限制说明，如不是领域输入输出且实际可比较结构化行为已有值级消费者，则 classified 为映射元数据；其描述的缺失行为不能因说明存在而豁免。

本报告把它拆成三个可检验条件：

1. **不是领域输入输出**：字符串由 `V5AiGoldenExporterTest.kt` 手写/拼接，C# 不读取它作为命令/状态输入或输出。
2. **实际可比较结构化行为已有值级消费者**：同一个 case 的 `accepted`/`rejectionCode`（B6）或 `kind/player/tileId/optionId`（B9）必须由当前 C# 真实 `AiDecisionService.Submit`/`RoundStateMachine` 断言，而不是只“存在”或只被解析。
3. **描述中的缺失行为不被说明本身豁免**：如果说明声称某行为缺失/为假，而该行为没有非空实例、没有值级断言，则不能因为“有说明”就把该行为算作 covered；说明本身最多进入 classified（未覆盖但已分类），不能进入 bound/full。

### 3.2 N3-REPR-01（B9 逐行应用）

> Kotlin data class/单例对象 `toString` 仅为调试表示，无稳定跨语言协议合同、不进入权威状态/哈希，且 `kind`/`tileId`/`optionId` 等语义已由当前 C# 实际回放断言时，原 `toString` 字节 classified 为表示差异，不要求 C# 伪造 JVM 文本。

本报告逐条确认：`action.toString()` 是 Kotlin data class 默认实现；`GameAction`/`Tile` 没有自定义 `toString`；该文本不在 `stateHash`/权威合同/线协议中；C# 对同一 case 的 `kind/player/tileId/optionId` 有真实回放值级断言。任何一条不满足（例如某个 text 是唯一行为载体、或结构化字段没有绑定），就保留 pending。

### 3.3 硬规则

- **B6 全包没有已接受的行为变异反证**（台账 §3/§6/§10 明确），因此本报告**不把任何 B6 行改成 bound**；只提议 classified/pending。
- **classified 不计 bound/full**；本报告不把 classified 写成“已覆盖”。
- **原 99 pending 保留**：本报告只提议 18 条去向；共享台账与校验器由 caller 串行更新，本任务不触碰。

---

## 4. B6：mappingBasis 7 + mappingLimits 容器/元素 4

### 4.1 来源调用链（先查全，避免把 `authorityMapping.basis` 混进来）

`V5AiGoldenExporterTest.kt` 中与本批相关的入口：

| 位置 | 作用 | 是否本批 18 条 |
|---|---|---|
| `:606-611` | `observationJson()` 写入 `v5Observation.mappingLimits` 三条手写字符串 | **是**（容器 + 三条元素） |
| `:704` | `caseJson()` 写 `"mappingBasis" to aj(expected.basis)` | **是**（7 条） |
| `:740-741` | `expected(accepted, code, basis)` 原样构造 `Expected` | **是**（传递函数，不是独立字段） |
| `:710-719` | `authorityMappingBasis(mode)` 生成 `v5.authorityMapping.basis` | **否**；这是另一个路径，台账审计 `$.cases[*].v5.authorityMapping.#` 已按 `SEM_MAP` 处理，不在本批 18 条。本报告只查调用链、不把它当本批反证。 |
| `:807-814`（`:813`） | `actionJson()` 写 `"text" to aj(action.toString())` | **是**（B9 7 条） |

`expected.mappingBasis` 的 7 个字面量分别在 `exportAiGoldenSamples()` 的 `caseJson(...)` 调用参数里：AI-002 `:186-190`、AI-003 `:222-226`、AI-004 `:260-264`、AI-005 `:296-300`、AI-006 `:332-336`、AI-007 `:373-377`、AI-001 `:419-423`。它们经 `expected()`（`:740-741`）传给 `caseJson()`，在 `:704` 写入 fixture。**本批只针对这个路径，不把 `:710ff` 的 `authorityMapping.basis` 算入 18 条。**

### 4.2 mappingBasis 7 条：classified（映射元数据）

| # | case | 冻结值（fixture，完整值见 TSV） | exporter 行 | C# 结构化消费者 | 裁决 |
|---|---|---|---|---|---|
| 5 | AI-001 | `V4 returned APPLIED at deadline-1; the stricter boundary (now >= deadline) is rejected, so the accepted boundary is exactly V5 Accept.` | `:419-423` + `:704,:740-741` | `accepted`/`rejectionCode`；AI-001 真实 `RoundStateMachine` 执行；AI-007 边界 | classified |
| 6 | AI-002 | `V4 compares submitted roundId ... V5 reports the round mismatch as StaleRound.` | `:186-190` + 同上 | `Assert.Equal(code, result.Code.ToString())`；`StaleRound` | classified |
| 7 | AI-003 | `V4 checks decisionId/timing-key equality ... StaleDecision.` | `:222-226` + 同上 | 同上；`StaleDecision` | classified |
| 8 | AI-004 | `V4 compares controlEpoch ... StaleEpoch.` | `:260-264` + 同上 | 同上；`StaleEpoch`；真实旧 epoch 0 | classified |
| 9 | AI-005 | `V4 resolves the decision for the submitted seat ... StaleDecision.` | `:296-300` + 同上 | 同上；seat 0 / 决策身份不匹配 → `StaleDecision` | classified |
| 10 | AI-006 | `V4 rejects an action absent ... IllegalAction. The C# test keeps ... removes it from the fresh authority candidate list ...` | `:332-336` + 同上 | 同上；`IllegalAction`；authority 候选排除由 `:490-520` 构造，记录动作仍在冻结观察中（`:411-415`） | classified |
| 11 | AI-007 | `V4 requires now < deadline ... DeadlineExpired at or after the deadline.` | `:373-377` + 同上 | 同上；`DeadlineExpired`；`DecisionClock.expired(deadline)` 实测 | classified |

**共同 C# 链（当前头）：**

1. `AiReplayGoldenTests.cs:126-131` 对 7 个 case 的 `expected.rejectionCode` 做精确序列断言：`StaleRound, StaleDecision, StaleEpoch, StaleDecision, IllegalAction, DeadlineExpired, None`。
2. `AiReplayGoldenTests.cs:173-177` 读取 `accepted` 与 `rejectionCode`；`:203-208` 走真实 `AiDecisionService(new ThrowingStrategy()).Submit(...)`，然后 `Assert.Equal(code, result.Code.ToString())` 与 `Assert.Equal(accepted, result.IsAccepted)`。
3. `AiReplayGoldenTests.cs:632` 对拒绝 case 断言 V4 `reason == STALE_AI_SUBMISSION`；对应说明里的“V4 合并原因 vs V5 分裂码”是映射叙述，真实 V5 码由 `:207` 消费。
4. AI-001 在 `AiReplayStateGoldenTests.cs:118-252` 走 `Replay → Submit → DeadlineArbiter.ProcessTick → RoundCommand.Discard → RoundStateMachine.Execute`，并对 before/after 投影逐字段断言；不是只读 fixture。
5. AI-006 的候选排除由 `caseJson` 的 `illegal-candidate` 分支（`:644-648`）与 `BuildAuthority`（`:490-520`）构造；`BuildIntent` 仍断言记录动作在冻结观察合法（`:411-415`），与说明中的“冻结观察合法 / authority 候选移除”一致。

**探针（§6 矩阵）**：P07 只改 AI-002 的 `mappingBasis`，P08 改全部 7 条 `mappingBasis`，均 exit 0 / 6 passed / 0 failed；P09 把 AI-002 的 `rejectionCode` 从 `StaleRound` 改为 `StaleDecision`，exit 1 / 3 failed；P10 把 AI-001 的 `accepted` 从 true 改为 false，exit 1 / 4 failed。**这证明本批消费者是结构化 `rejectionCode`/`accepted`，不是说明文字。** 因而 7 条 mappingBasis 按 N3-META-02 进入 classified；由于 B6 没有行为变异反证，**不得**改 bound。

**例外/边界**：`mappingBasis` 文字中的 V4 行为（“V4 collapses into STALE_AI_SUBMISSION”）由 `v4.reason` 的值级断言（`:632`）覆盖；V5 分裂码由 `:207` 覆盖。没有发现某条 `mappingBasis` 是唯一行为载体或缺少结构化绑定的情形。若 caller 认为 `mappingBasis` 的说明本身仍需 C# 值级消费，则这 7 条应保持 pending；本报告按 N3-META-02 提议 classified，理由与反证如上。

### 4.3 mappingLimits 容器/元素 4 条：1 容器 + 1 隐私 classified，2 条 pending

fixture 原文（三条，按 `[*]` 下标 0/1/2）：

```text
[0] PlayerView has no public claimed-discard flag; isClaimed is false.
[1] riichi status is available as a per-seat flag but is not attached to individual discards by PlayerView; per-discard isRiichi is false.
[2] sleeve tiles are private and are not part of AiObservation; they are intentionally absent.
```

| # | 单元 | 冻结值（摘要） | C# 读点/证据 | 裁决 | 理由 |
|---|---|---|---|---|---|
| 4 | `$.v5Observation.mappingLimits`（array） | 三条字符串的容器 | `grep mappingLimits` 在 `dotnet/**` = 0；`BuildObservation`（`AiReplayGoldenTests.cs:338-361`）不读该路径 | **classified** | 纯容器/元数据，不是领域输入输出，也没有行为值；P12 改整个数组仍 6 passed |
| 1 | `[*]` idx 1 | `PlayerView has no public claimed-discard flag; isClaimed is false.` | `AiReplayGoldenTests.cs:381-394` 解析 `isClaimed`；fixture `visibleDiscards=[]`；`AiReplayStateGoldenTests.cs:606` `Assert.Empty` | **pending** | 说明声称 `isClaimed=false`，但冻结 fixture 没有弃牌实例，C# 没有对非空 `isClaimed` 的值级断言；P13 加一条 discard 在 `:606` 因“数组必须为空”失败，而不是在 flag 值上失败。说明存在不能替代值级绑定（N3-META-02 末句）。 |
| 2 | `[*]` idx 2 | `riichi status ... per-discard isRiichi is false.` | 同上；解析 `isRiichi`；无实例 | **pending** | 同 #1：逐弃牌 `isRiichi` 缺失/恒 false 是 V4 `PlayerView` 导出限制；没有非空实例，也没有 C# 值级断言。 |
| 3 | `[*]` idx 3 | `sleeve tiles are private ... intentionally absent.` | `AiObservation.cs:86-100`（类型不携带 sleeve）；`AiObservation` 无 `Sleeve` 成员 | **classified** | 这是有意的隐私排除，不是“缺失的 V5 行为”；C# 类型面就是结构化证明，没有需要伪造的值。 |

**为什么 #1/#2 不是 classified 而是 pending（逐条应用，不按字段名通排）**：

- 它们不是“解释某条已值级绑定的结构化行为”，而是“解释某行为没有值/为假”。`visibleDiscards` 在冻结 fixture 中是空数组（`@($j.v5Observation.visibleDiscards).Count = 0`）；C# 虽然解析 `isRiichi`/`isClaimed`（`:389-390`），但没有任何测试对非空值做值级断言；唯一相关断言是 `Assert.Empty(observation.VisibleDiscards)`（`AiReplayStateGoldenTests.cs:606`）。
- P13 把 `visibleDiscards` 改为一条 `isRiichi=true,isClaimed=true`，测试 exit 1 / 2 failed，失败栈在 `AiReplayStateGoldenTests.cs(606)` 的 `Assert.Empty`；这说明在冻结 fixture 形状下，无法通过“加一条 discard”来绑定 flag 值，必须换 fixture 或改测试形状（超出本任务授权）。
- 因此按 N3-META-02 的第二个条件（实际可比较结构化行为已有值级消费者）不成立，不能把说明本身 classified 成“已覆盖”；保留 pending，真实缺口就是：**V4 `PlayerView` 不公开逐弃牌 claimed/riichi，冻结 ai-v1 没有非空弃牌，C# 没有非空 flag 值级断言。**

**若 caller 采更宽口径**：如果 caller 明确裁决“V4-only 导出限制本身可作为 classified 的导出边界，而不是缺失的 V5 行为”，则 #1/#2 可改 classified（理由写 V4-only export boundary，仍不计 bound/full）；本报告不替 caller 做这个裁决，因此 proposedVerdict 保持 pending。这样既保留 18 条的可复核性，也不把“说明存在”当成豁免。

**B6 小计**：9 classified（mappingBasis 7 + mappingLimits 容器 1 + sleeve 1）+ 2 pending（claimed / per-discard riichi）；**0 bound**；B6 仍无已接受行为变异反证。

---

## 5. B9：`v4Call.action.text` 7 条

### 5.1 来源与表示性质

- `V5AiGoldenExporterTest.kt:807-814` 的 `actionJson(action, optionId)` 写入 `type/kind/player/tileId/optionId/text`；其中 `:813` 是 `"text" to aj(action.toString())`。
- `caseJson` 在 `:670` 调用 `actionJson(submittedAction, submittedOptionId)`；`recordedIntent.selectedAction` 也在 `:518` 使用同一函数（该路径不在本批 18 条，仅说明共享函数）。
- `GameAction.kt:25` 是 `data class DiscardTile(val player: Int, val tile: Tile)`，`:49` 是 `data class Pass(val player: Int)`；`GameAction.kt` 没有 `override fun toString()`。`Tile.kt:20-26` 是 `data class Tile(suit, rank, copyIndex)`，默认 `toString()`。因此 `action.text` 是 Kotlin data class 默认调试表示，不是协议字段。
- 文本不进入 `stateHash`/权威状态/线协议；fixture 也没有跨语言 canonical hash（AI 文档 §5.6 明确无 V4 规范哈希）。

### 5.2 逐条

| # | case | 冻结 text | 同 case 已绑定的结构化字段 | C# 断言链 | 裁决 |
|---|---|---|---|---|---|
| 12 | AI-001 | `DiscardTile(player=1, tile=Tile(suit=MAN, rank=1, copyIndex=0))` | kind=Discard, player=1, tileId=0, optionId=opt-0 | `AssertActionFields` `:651-660`；真实 Submit `:203-208`；AI-001 真实 round `:118-252` | classified |
| 13 | AI-002 | 同上 | 同上 | 同上；`StaleRound` 真实拒绝 | classified |
| 14 | AI-003 | 同上 | 同上 | 同上；`StaleDecision` | classified |
| 15 | AI-004 | 同上 | 同上 | 同上；`StaleEpoch` | classified |
| 16 | AI-005 | 同上 | 同上 | 同上；`StaleDecision` | classified |
| 17 | AI-006 | `Pass(player=1)` | kind=Pass, player=1, tileId=null, optionId=null | AI-006 专用 `:567-576`；`AssertActionFields` 通用链；真实 `IllegalAction` `:207` | classified |
| 18 | AI-007 | `DiscardTile(player=1, tile=Tile(suit=MAN, rank=1, copyIndex=0))` | kind=Discard, player=1, tileId=0, optionId=opt-0 | 同 #12；`DeadlineExpired` | classified |

**逐条一致性说明**：

- 6 条 `DiscardTile` 的 text 组件 `player=1`、`Tile(suit=MAN, rank=1, copyIndex=0)` 分别对应已绑定的 `player=1`、`tileId=0`。`tileId=0` 的编码由 `Tile.kt:113-120`（`fromTileId` 与 `tileId = typeIndex*4+copyIndex`；suit 顺序见 `:68-75`）确定；`TileId.cs:59-95` 对同一 id 给出 Man/rank1/copy0。文本只是同一物理牌的另一表示，不是额外语义。
- AI-006 的 `Pass(player=1)` 对应 `kind=Pass`、`player=1`、`tileId=null`、`optionId=null`；这四项由 `:571-575` 明确断言。
- `text` 本身在 C# 全仓 **0 次**读取（`grep '"text"'` 仅命中 `AiReplayStateGoldenTests.cs:98` 的 `EventFields` 与 `:237` 的 **events** text 存在级断言；`action.text` 没有对应读取）。本报告没有用 events text 的结论替代 action.text 的举证。

### 5.3 探针（§6）

- P01 只改 AI-001 `action.text`、P02 只改 AI-006 `action.text`：均 exit 0 / 6 passed / 0 failed → text 未被消费。
- P03 改 AI-001 `action.kind`（Discard→Pass）：exit 1 / 1 failed，`AssertActionFields` `:653`；
- P04 改 AI-001 `action.tileId`（0→1）：exit 1 / 1 failed，`:657`；
- P05 改 AI-001 `action.optionId`（opt-0→opt-9）：exit 1 / 1 failed，`:659`；
- P06 改 AI-006 `action.kind`（Pass→Discard）：exit 1 / 1 failed，`:571`（`Expected: "Pass" Actual: "Discard"`）。
- 这组正/负对照直接证明：**text 是表示差异，结构化字段才是当前 C# 实际回放的值级消费者**；符合 N3-REPR-01，7 条全部 classified。**不要求 C# 伪造 JVM 文本。**
- 因为 B9 包的历史 48/48 变异矩阵只覆盖 events 非 text 字段（台账 §3/§6/§10），本报告不引用它来证明 action.text；action.text 的结论完全由本轮 P01-P06 支撑。

---

## 6. 第 1 轮实际命令、exit、计数与日志

> 以下为第 1 轮实跑证据。第 2 轮 D-AI-01/D-AI-02 只改交付文件名与 manifest，不改源码/测试/fixture，未重跑；行为结论沿用调用者第 1 轮独立证据（§0.1），本轮行为项记 NOT_RUN。

### 6.1 restore / fresh build

```powershell
$env:TEMP=$env:TMP='D:/majiang-wt/n3-ai-adjudication/artifacts/TEMP/p-align-a-6ecd555edcd347a48dfeeb792966a4dd'
D:/Unity/dotnet/dotnet.exe restore dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -v minimal
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj --no-incremental --no-restore -v minimal
```

| 命令 | native exit | 计数/结果 | 日志 |
|---|---:|---|---|
| restore | **0** | 还原 Domain / Ai / Ai.Tests 三个项目 | `ai-restore.log` |
| build `--no-incremental --no-restore` | **0** | **0 warning / 0 error**；生成 Debug net8.0 | `ai-build.log` |

首次带 `--no-restore` 的构建在干净 worktree 上因缺 `project.assets.json` exit 1（`NETSDK1004`）；随后 restore exit 0，再 fresh build exit 0。如实记录，不把首次失败掩盖成“一次通过”。

### 6.2 直接 runner（不是 `dotnet test --filter`）

```powershell
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Debug/net8.0/Mahjong.Ai.Tests.dll `
  -class '*AiReplayGoldenTests' -class '*AiReplayStateGoldenTests' `
  -result-xml <E>/ai-replay.xml
```

| native exit | XML 选择 | 结果 |
|---:|---|---|
| **0** | 恰好 2 个类：`AiReplayGoldenTests`（3 Facts）+ `AiReplayStateGoldenTests`（3 Facts） | **Total 6 / Passed 6 / Failed 0 / Errors 0 / Skipped 0 / Not Run 0** |

XML 逐 test 的类名/结果已核对（6 条全部 `Pass`，无其他类混入）。这证明“直接 runner + `-class`”实际选中了目标类，而不是外层 filter 被忽略的全项目执行。

### 6.3 TEMP 副本探针矩阵

脚本：`<E>/probe.ps1`；每个探针把真实 `ai-v1.json` 读入、只改一个逻辑字段、写到新的 TEMP 副本，`MAHJONG_AI_GOLDEN` 指向该副本，跑同一 direct runner；`try/finally` 清理环境变量。P00 是“同一序列化器、无逻辑改动”的控制。

| 探针 | 变异 | exit | Total | Failed | Errors | Skipped | Not Run | 结论 |
|---|---|---:|---:|---:|---:|---:|---:|---|
| P00 | 控制（无逻辑改动） | 0 | 6 | 0 | 0 | 0 | 0 | 序列化/加载正常 |
| P01 | AI-001 `action.text` → `FALSE-REPR action text` | 0 | 6 | 0 | 0 | 0 | 0 | text 未被消费 |
| P02 | AI-006 `action.text` → `FALSE-REPR pass text` | 0 | 6 | 0 | 0 | 0 | 0 | text 未被消费 |
| P03 | AI-001 `action.kind` Discard→Pass | 1 | 6 | 1 | 0 | 0 | 0 | 结构化字段被消费（`:653`） |
| P04 | AI-001 `action.tileId` 0→1 | 1 | 6 | 1 | 0 | 0 | 0 | 结构化字段被消费（`:657`） |
| P05 | AI-001 `action.optionId` opt-0→opt-9 | 1 | 6 | 1 | 0 | 0 | 0 | 结构化字段被消费（`:659`） |
| P06 | AI-006 `action.kind` Pass→Discard | 1 | 6 | 1 | 0 | 0 | 0 | 结构化字段被消费（`:571`） |
| P07 | AI-002 `mappingBasis` → FALSE-CLAIM | 0 | 6 | 0 | 0 | 0 | 0 | 说明未被消费 |
| P08 | 全部 7 条 `mappingBasis` → FALSE-CLAIM | 0 | 6 | 0 | 0 | 0 | 0 | 说明未被消费 |
| P09 | AI-002 `rejectionCode` StaleRound→StaleDecision | 1 | 6 | 3 | 0 | 0 | 0 | 结构化码被消费（`:129,:207` 等） |
| P10 | AI-001 `accepted` true→false | 1 | 6 | 4 | 0 | 0 | 0 | 结构化结果被消费（`:188,:238,:597` 等） |
| P11 | `mappingLimits[0]` → FALSE-CLAIM | 0 | 6 | 0 | 0 | 0 | 0 | 说明未被消费 |
| P12 | 整个 `mappingLimits` → 两条 FALSE-CLAIM | 0 | 6 | 0 | 0 | 0 | 0 | 容器/说明未被消费 |

**负例失败信息（原样摘录）**：

- P03：`AiReplayGoldenTests.cs(653,0): at ...AssertActionFields` → `:582`；
- P04：`AiReplayGoldenTests.cs(657,0): ...tileId`；
- P05：`AiReplayGoldenTests.cs(658,0): ...optionId`；
- P06：`Assert.Equal() Failure: Strings differ; Expected: "Pass"; Actual: "Discard"`，`:571`；
- P09：`GoldenFixtureHasProvenance...` 在 `:129`；`RejectedIntents...` 在 `:301`；`GoldenCases...` 在 `:207`；
- P10：`:238`（accepted case 选择）、`:597`（V4 receipt 期望）、`:188`、`:433`、`:112` 等。

**结构性检查 P13（不属于单字段矩阵）**：把 `visibleDiscards` 改为一条 `isRiichi=true,isClaimed=true`，exit 1 / 2 failed；两条失败栈均指向 `AiReplayStateGoldenTests.cs(606)` 的 `Assert.Empty(observation.VisibleDiscards)`，不是 flag 值断言。这说明冻结 fixture 的 `visibleDiscards=[]` 形状本身阻止了非空 flag 值级绑定；要绑定 #1/#2 必须换 fixture 或改测试形状（超出本任务），因此 #1/#2 保持 pending。

### 6.4 计数口径

- 本报告只跑 **2 个类 / 6 个 Fact**；未跑 Ai.Tests 全量（历史 438），未跑 Domain/TableRuntime/解决方案全量，未跑 Gradle/Unity/PG/WSS/Android/iOS/CI/设备；这些一律 NOT_RUN，不冒充通过。
- 历史 AI 48/48 变异矩阵本轮**未重跑**；它只覆盖 events 非 text 字段，本报告不拿它证明 action.text 或 B6。
- B6 没有历史行为变异反证；本报告也没有为 B6 制造“改字段必须 FAIL”的行为反证（只有说明文字改动的 PASS 与结构化 rejectionCode/accepted 改动的 FAIL 对照），因此 **B6 不设 bound**。

---

## 7. 证据注册表

证据根（ignored，不进 commit）：

```text
E = D:/majiang-wt/n3-ai-adjudication/artifacts/TEMP/p-align-a-6ecd555edcd347a48dfeeb792966a4dd
```

| EV-ID | 文件/内容 | SHA-256 |
|---|---|---|
| E-AI-FIXTURE | `tests/v5-golden/ai-v1.json`（raw） | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` |
| E-AI-FIXTURE-LF | 同上（LF 归一化） | `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2` |
| E-EXPORTER | `engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt`（raw） | `F70DA6A4AFBB06FA1BC80F2D0A6573BFB5A63DD302B2CA52122A551D623C76F8` |
| E-CS-REPLAY | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs`（raw） | `24F91115233E47F5E6FC992A68EBB570807AC9D9D68FBB8544C80BBF0B06A9F5` |
| E-CS-STATE | `dotnet/tests/Mahjong.Ai.Tests/AiReplayStateGoldenTests.cs`（LF） | `0032D47C87EFA1791F28ADC884A21FCE5837A35CDA4004E9CAA6CB83F504411A` |
| E-AIDOC | `doc/v5-重构/V5-N3-AI-REPLAY-BINDING.md`（raw） | `DD94EF8F32D9759FD1FBE2B6974D728CBDC27414C7F3F31842E692A3B74ECB1A` |
| E-AIACC | `doc/v5-重构/CODEX-V5-N3-AI-R2-ACCEPTANCE.md`（raw） | `0B48BE12D87C1A4E43EF1E5B575B64C3AE85FD8711530014A152C365533F24B1` |
| E-AUDIT | `doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md`（raw） | `5A440C58902C260D607016546A37B243DAAA75DCFE99401D3F5DE7B202CB80E9` |
| E-LEDGER | `doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv`（raw） | `EDB9B158902779631683325FB060A3E7755DF75FB28A3688FFE5CF7F50FAF6A5` |
| E-BUILD-01 | `ai-build.log`（fresh build，exit 0） | `3AD48BD76AE1232F68C8BC90A1AB4617B1C699F88C51E75E0D170EB2512468F2` |
| E-RESTORE-01 | `ai-restore.log`（restore，exit 0） | `9D12365A261858C10EFB40201525C2A063EC046384CFF449721438E498B3C94F` |
| E-RUN-01 | `ai-replay.log`（direct runner，6 passed） | `1501C60DB0980B5E2103A7D170D97F526CF1661A48425349C03DB05D45DF77CF` |
| E-RUN-XML-01 | `ai-replay.xml`（2 类 / 6 Facts） | `155299B5BC5FAEC59A7FE56C1680A4D5362C2F439703D2570A9C932E86C265BB` |
| E-PROBE-SCRIPT | `probe.ps1` | `D76E4E6E5FF709FEB788DB09EC9AE661A7BCB061AF3C9E2660C3F3CFB440B2F8` |
| E-PROBE-MATRIX | `matrix.csv`（13 例） | `6C3E4512AEF178611BD5C0CA96C52CB5B6A73BED22F95BD415094C919200FC87` |
| E-PROBE-CONSOLE | `probe-console.log` | `73A04000654909219C0673B36B1278359A99C559585C4C13D1571186471ED365` |
| E-FROZEN-18 | `frozen-18.json`（18 条 identity + fixture 值） | 由 `evidence-manifest-v2.txt` 逐文件登记 |
| E-MANIFEST-V2 | `evidence-manifest-v2.txt`（56 个非摘要证据文件的逐文件 SHA-256；排除自身、旧 manifest 与摘要文件） | `3CDF4EBBFCB1505B67CFA7E70E70122C227A4EE31ED3671EFAFA0E6EDBC0C1EE` |
| E-MANIFEST-V1 | `evidence-sha256.txt`（旧 manifest，原样保留供审计；含 1 条自引用行，已由 v2 取代） | `097F15FA49BE7BA010E87807954DC7F056E826C55406D942235355C45828DB35` |
| E-MANIFEST-V2-VERIFY | `verify-manifest-v2.log`（56 OK / 0 missing / 0 mismatch / 0 self / 0 old） | `CA19F236071AFBCA8773D31D781CB1182E45F0D17DB24B18118C1DAA21EB2A26` |

说明：E-MANIFEST-V2 覆盖 56 个非摘要证据文件（构建/恢复/runner 日志与 XML、13 个探针的副本/日志/XML、P13、矩阵、脚本、frozen-18、build-delivery、提交日志等），逐项校验 **56/56 PASS，0 missing / 0 mismatch / 0 self / 0 old**；本报告只列关键项，完整清单以 v2 manifest 为准。旧 E-MANIFEST-V1 原样保留供审计，其中 1 条自引用行（该行写旧摘要 `0a2cb429…`，而文件实际 SHA-256 为 `097F15FA…`）已被 v2 取代，不再作为可核验项；本报告不再宣称“57 个文件全部可核验”。每个探针的 `run-P*.log` 与 `result-P*.xml` 都在 v2 manifest 中，可逐项复核 exit/Total/Failed/Errors/Skipped/Not Run。

交付 TSV：

| 文件 | raw SHA-256 | LF SHA-256 | 行数 |
|---|---|---|---|
| `doc/v5-重构/V5-N3-AI-TEXT-ADJUDICATION.tsv` | `E73A4DC2B2E68E6F4712854706D8B5A5543C6672593D4198584C7230733497C5` | `9916F87FF2DABA65C2B28C246CEE747E6DC40AEC0B9AA28C9F0D39D791F44D12` | 19（表头 + 18） |

---

## 8. 18 + 81 的组合与 caller 统一改账顺序

### 8.1 组合

- 本批：**18** = B6 11 + B9 action.text 7。
- 兄弟批（P-NOTE-A）：**81** = B1 note 6 + B8 note 75；源码/测试文件集与本批不重叠（本批只读 ai 绑定测试与 ai fixture；兄弟只触 `CheatGoldenTests.cs`/`CheatGoldenBindingTests.cs` 的 note 维度）。
- 18 + 81 = **99**，正好是台账 §6 的全部 pending。两边都终结于**同一** `V5-G1-N3-CLOSEOUT-LEDGER.tsv`、同一批逐包 digest、同一校验器；因此**源码/测试实现层可并行，交付层（TSV + 校验器 + 报告）必须串行**。本任务不触碰共享台账/校验器，只在独占 worktree 提交本报告与交付 TSV。

### 8.2 本批单独应用的向量（供 caller 核对算术）

当前：`bound 411 / classified 82 / pending 99`。

本批 18 条提议：16 classified + 2 pending + 0 bound。只应用本批时：

```text
bound      = 411 + 0  = 411
classified = 82  + 16 = 98
pending    = 99  - 16 = 83
合计       = 411 + 98 + 83 = 592
```

若兄弟批 81 条最终为 `C_sib` classified、`P_sib` pending（`C_sib + P_sib = 81`），则合并后：

```text
bound      = 411
classified = 98 + C_sib
pending    = 2  + P_sib
合计       = 411 + (98 + C_sib) + (2 + P_sib) = 592
```

**不自称“合并后一定零 pending”**：这取决于 caller 对兄弟 81 条与对本批 #1/#2 的最终裁决；本报告只给本批 18 条的 proposedVerdict。

### 8.3 caller 建议的串行改账顺序

1. **冻结两批证据**：本报告 + TSV；兄弟的 note 证据。先复核两批的源码/测试文件集确无重叠。
2. **一次或两次串行改共享 TSV**：先 B6/B9 18 行（16 行 verdict→classified，补齐 reason/owner/revisit/evidence；2 行保持 pending 并更新 reason/owner/revisit），再 B1/B8 81 行；或反过来。**不要**在只改一半时运行校验器。
3. **重算逐包 canonical digest**：B1/B6/B8/B9 四个包的 `id|verdict|fixCommit|csRead|assertion|evidence` multiset 都变了；按校验器 `$adjudicationPin` 的口径重钉。B6 的 11 行中 2 行仍 pending、9 行 classified；B9 的 8 行中 7 行改 classified、1 行 events text 保持原 classified 不动。
4. **更新向量与报告**：把台账 §2 的 411/82/99、§5 classified 清单、§6 pending 清单、§10 组合表、§13 交付记录同步；**不要**改写已接受的历史报告原文。
5. **最后跑校验器**：`powershell -File tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot <caller-tree>`；预期 24 CHECK-OK 只有在第 2-4 步全部完成后才成立。负例矩阵（20/20）与历史证据哈希按台账既有方式复核。
6. **G1 重审另行裁决**：即使 99 条全部有去向、台账零 pending，也只说明台账组件一致；是否构成 G1 重审输入、门 2 是否通过，由 caller 依九门单独裁决。**本报告不宣布门 2、不宣布 G1。**

### 8.4 不得做的事

- 不得用本批的 classified 把原 99 pending 直接写成 0；本批只覆盖 18 条，且其中 2 条仍 pending。
- 不得刷新 fixture 哈希、改 catalog/manifest、改统一 verifier 来“制造对齐”。
- 不得把 B6 的说明文字改动 PASS 当成“B6 行为已绑定”；B6 仍无行为变异反证。
- 不得把本批的 6 个 Fact 当成 Ai.Tests 全量或阶段验收。

---

## 9. PASS / FAIL / BLOCKED / NOT_RUN

### PASS

- 开工 HEAD/branch/status/ownership 与冻结基线一致；工作树干净；独占 worktree 已核实。
- 18 条 identity（id/package/fixture/caseId/path/idx/kind/parent/histClass/原 verdict + 原 fixCommit/evidence/reason）与冻结台账逐行一致（`bad=0`）。
- restore exit 0；fresh `--no-incremental --no-restore` build exit 0，**0 warning / 0 error**（第 1 轮实跑）。
- 直接 runner 选中恰好 2 类，**6/6 passed，Errors/Failed/Skipped/Not Run 全 0，native exit 0**；XML 逐 test 核对（第 1 轮实跑）。
- TEMP 副本 13 例矩阵 + P13 结构检查全部按预期：text/mappingBasis/mappingLimits 改动 PASS；kind/tileId/optionId/rejectionCode/accepted 改动 FAIL；无 Errors/Skipped/Not Run（第 1 轮实跑）。
- 16 条 proposed classified 均有逐行证据链；2 条 proposed pending 均有真实缺口、owner、复访条件。
- 只新增两个文件；未改生产/测试/fixture/catalog/manifest/已接受报告/共享台账/统一 verifier/Unity 环境。
- 第 2 轮 D-AI-01/D-AI-02：精确 expectedFiles 重命名完成；v2 manifest 56/56 逐项 PASS；本轮未改代码、未重跑行为。

### FAIL

- 本任务范围内未发现需要报 FAIL 的实现缺陷；P03-P06/P09/P10/P13 的失败是**预期负例**，不是产品失败。
- 2 条 pending 是**未关闭缺口**，不是 FAIL；本报告不把它们写成通过。

### BLOCKED

- 无（本批证据面内）。#1/#2 的 pending 是“冻结 fixture 无法提供非空逐弃牌值”的未覆盖项，不是环境阻塞，也不冒充 BLOCKED。

### NOT_RUN（如实单列，不冒充通过）

- **第 2 轮未重跑**任何构建/runner/probe；行为测试沿用第 1 轮及 caller 独立证据：build.log `5D52156B77DD54063298969D40D57DBB113653049BF434E4A4CC704D48402D94`、baseline.xml `41F359FC526C24C1BA855DDC5A155135719E05F19710BAD8464BEE3A4E417DDB`、P13.log `0E7A11AB05916A7069E085E7817FCBEB8C61FBA31335B1DCC44AEE861C6113BD`、matrix.csv `6C3E4512AEF178611BD5C0CA96C52CB5B6A73BED22F95BD415094C919200FC87`。
- **历史 AI 48/48 变异矩阵未重跑**；它只覆盖 events 非 text 字段，本报告不引用它证明 action.text/B6。
- **B6 行为变异反证未建立**；本报告只做了说明文字 PASS + 结构化码 FAIL 的对照，故 B6 不设 bound。
- Ai.Tests 全量（历史 438）、Domain/TableRuntime/解决方案全量、Gradle/Unity/IL2CPP/PG/WSS/Android/iOS/真机/真实网络/远端 CI：**NOT_RUN**。
- `events[*].text` 已 classified 的 1 条：**未重开、未重跑、未改判**。
- 兄弟 P-NOTE-A 81 条：**未读、未改、未跑**；本报告只给组合与改账顺序。
- 共享台账 TSV/校验器/已接受报告：**未改、未运行**（本任务不触碰）。
- G1/G5/门 2：**未裁决、未宣布**；V4 仍是唯一生产架构；未创建 `complete.flag`。

---

## 10. 红线复核

- 仅新增 `doc/v5-重构/V5-N3-AI-TEXT-ADJUDICATION.md` 与 `doc/v5-重构/V5-N3-AI-TEXT-ADJUDICATION.tsv`；无生产/测试/fixture/catalog/manifest/已接受报告/共享台账/统一 verifier/Unity 资源改动。
- 未读改兄弟未提交工作；未 push、未建 PR、未触发 CI、未转派、未改机器级环境/信任/部署。
- 18 条没有一条被改成 bound；B6 全包无变异反证的口径保持；classified 不计 full；2 条 pending 有真实缺口与复访条件。
- 未用“同类 events text 已 classified”替代 action.text 的逐条举证；action.text 有独立 P01-P06 对照。
- 未把组件证据写成 G1/G5/门 2；未把 6 个 Fact 写成全量；未把历史未跑写成通过。
- 未刷新 fixture 哈希；未改 `stateHash`/权威合同；未要求 C# 伪造 JVM 文本。

---

## 11. 复现命令（从仓库根）

```powershell
$E = 'D:/majiang-wt/n3-ai-adjudication/artifacts/TEMP/p-align-a-6ecd555edcd347a48dfeeb792966a4dd'
$env:TEMP = $E; $env:TMP = $E

# 1) restore + fresh build
D:/Unity/dotnet/dotnet.exe restore dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -v minimal
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj --no-incremental --no-restore -v minimal

# 2) direct runner（2 类 / 6 Facts）
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Debug/net8.0/Mahjong.Ai.Tests.dll `
  -class '*AiReplayGoldenTests' -class '*AiReplayStateGoldenTests' `
  -result-xml "$E/ai-replay.xml"

# 3) 13 例 TEMP 副本矩阵
powershell -NoProfile -ExecutionPolicy Bypass -File "$E/probe.ps1" `
  -RepoRoot 'D:/majiang-wt/n3-ai-adjudication' -EvidenceRoot $E `
  *> "$E/probe-console.log"

# 4) 生成交付 TSV（脚本在证据根，输出到 doc）
powershell -NoProfile -ExecutionPolicy Bypass -File "$E/build-delivery.ps1" `
  -RepoRoot 'D:/majiang-wt/n3-ai-adjudication' `
  -OutTsv 'D:/majiang-wt/n3-ai-adjudication/doc/v5-重构/V5-N3-AI-TEXT-ADJUDICATION.tsv'
```

---

## 12. 交付记录

- 交付文件：本报告 + `V5-N3-AI-TEXT-ADJUDICATION.tsv`（18 行，25 列，原台账 identity 原样保留）。
- 第 1 轮提交 `e494b14` 使用旧命名；第 2 轮提交按 D-AI-01 `git mv` 为精确 expectedFiles，并按 D-AI-02 生成 v2 manifest。`base 6c35736..HEAD` 净 diff 仅两个 expectedFiles；第 2 轮不重跑行为测试，提交 hash 见交付回执。
- 本轮实际改动仅为上述两个交付文件（第 1 轮新增、第 2 轮重命名）；提交前 `git status --porcelain` 只应出现这两个交付文件；提交后 clean。
- 本地最小提交、commit hash、`git diff --check`、clean status 的最终值见本任务交付回执（本文件不自我引用提交哈希；提交信息记录本报告与 TSV 为唯一改动）。
- 未 push、未建 PR、未触发 CI；caller 负责独立复核、共享台账串行更新与唯一 PR19 集成；本收件人不创建子 MR。
- V4 仍是唯一生产架构；G1 未批准；G5 未完成；未创建 `complete.flag`。
