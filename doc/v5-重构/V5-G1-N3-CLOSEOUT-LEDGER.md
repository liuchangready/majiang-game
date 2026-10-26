# V5-G1-N3 销账台账（N3 closeout ledger：592 个 GAP 实例的 bound / classified / pending 三选一）

**当前候选裁决（caller 串行准备，2026-10-26）：592 = 411 bound / 181 classified / 0 pending。** 在已接受集成头 7606c5c 上，仅对剩余六个冻结 identity 应用已接受证据及 caller 明文分类规则；586 条非目标 raw 行、全部 411 bound、历史身份和 census 不变。此补丁尚待独立复核与集成，不把候选状态写作已批准门禁。classified 仍是未覆盖分类，不计 full；不宣告门2、G1 或 G5 通过，V4 继续是唯一生产架构。当前裁决见 **§15**；§14 的 411/175/6 及之前章节均为保留的历史记录。

**历史记录边界：下列原日期至 §13 保留原验收时点。** 其中 411/82/99、§5–6 的分类和 pending 清单、§10 的两个待办组合均为串行裁决前的历史事实，已由 §14 的当前裁决取代；历史实测、原 census、原 accepted 报告与日志不改写。

日期：2026-10-25（R1 独立复审后返修版；复审全文 SHA `e520991daa3b2c253053431a9bef3db032a802344c9ddea1a8c6002b5087f33e` = `artifacts/TEMP/codex-review-20261025/findings.md`）
工作树：`D:/majiang-wt/n3-closeout-ledger`（独占），分支 `wt/v5-n3-closeout-ledger`，冻结基线 `cae94de6c6f18a56134c6b0beeaec0e61ad4bc8e`（PR19 当前已发布头）。
交付物（仅三个文件）：本报告、`V5-G1-N3-CLOSEOUT-LEDGER.tsv`（592 行实例级台账）、`tools/verify-v5-n3-closeout-ledger.ps1`（只读台账校验器）。

**本任务是台账与分类，不是门禁裁决：本报告不宣告 G1、不宣告完整移植、不宣告 G5，也不重定义任务书的门 2。V4 仍是唯一生产架构。** N-3 收口规则（冻结 census 唯一分母、按包批量、三选一销账、硬收口点 r6、G1 重审九门）以 `V5-0-1-IMPLEMENTATION-TASKS.md` 为准，本台账只提供其输入。

**R1 复审结论与本版变化（详见 §12 逐条映射）**：复审确认 592 分母与账本结构成立，但指出 5 项缺陷——E3 十行中 9 行是 META/结构导出误标 bound（D-R1-01）、5 处读点不准（D-R1-02）、evidence 列为散文不可运行时追溯（D-R1-03）、§8 增量登记无路径/SHA/消费者锚点（D-R1-04）、全文 6 处自相矛盾（D-R1-05）。本版全部修复：**向量为 411 bound / 82 classified / 99 pending**（E3 重判 1 bound + 9 classified；592 实例集合与 14 包分母不变，不要求新增行为实现）。校验器同步强化：证据注册表运行时重哈希、逐包裁决 digest、census 保真、bound 义务列、锚点内容与 commit 祖先检查、17 列 schema 强制（负例矩阵 20/20，§7）。

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
| verify-mapping v5 | `D:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7/verify-mapping.ps1` | 656 行，本台账提取器逐语义忠实移植（Test-Match 有序首 match、Parse-Scope 文法、Test-WhereStr 父串读取、unscoped 全认领 / scoped 逐实例恰一行认领、first-claiming-row-wins） |
| R1 复审证据根 | `artifacts/TEMP/codex-review-20261025/`（只读保留） | findings.md、anchors.tsv（969 行）、5 个账本负例日志与 2 个探针日志，SHA 见 §1a 注册表 |

## 1a. 证据注册表（EV-ID → 路径 / SHA-256）

台账 TSV 的 evidence 列**只引下列 EV-ID**（分号分隔），不内嵌散文路径；校验器每次运行对全部 26 项**重算 SHA-256**（`evidence-catalog` 检查），任何被引文件漂移即 exit 1（D-R1-03 修复）。路径相对仓库根，`<E>` = `D:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7`。

| EV-ID | 路径 | SHA-256 |
|---|---|---|
| EV-AUDIT | `doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md` | `5a440c58902c260d607016546a37b243daaa75dcfe99401d3f5de7b202cb80e9` |
| EV-ACCEPT | `doc/v5-重构/CODEX-V5-G1-N3-FIELD-AUDIT-ACCEPTANCE.md` | `8d72f1dfb071e6cec56046b65abe78217d1f877f930e9ac1cb87ac88d7e68d71` |
| EV-CENSUS | `<E>/census/instances.tsv` | `049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12` |
| EV-RULES | `<E>/rules.tsv` | `24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31` |
| EV-CHEAT-DOC | `doc/v5-重构/V5-N3-CHEAT-GOLDEN-BINDING.md` | `a9daf5f5ccd705f031af98b7aa7825452a263e97ba3df55df0a01e4e9c9d9abe` |
| EV-CHEAT-ACC | `doc/v5-重构/CODEX-V5-N3-CHEAT-BINDING-ACCEPTANCE.md` | `a0660e19dc6777ce41fdbd734bec9cf61e5a3cf5c5275658f4a97c025a6574e5` |
| EV-ROUND-DOC | `doc/v5-重构/V5-N3-ROUND-GOLDEN-BINDING.md` | `9fa4953c787ef2bb5c924d5bdcabfc2ad7208209fba59b3a6ba2405a33d115db` |
| EV-ROUND-ACC | `doc/v5-重构/CODEX-V5-N3-ROUND-ACCEPTANCE.md` | `92c26f45e5e8a8bcda9d1c55f673400baf754038e7c02a6fa1b16c00582cda1e` |
| EV-SCORE-DOC | `doc/v5-重构/V5-N3-SCORING-GOLDEN-BINDING.md` | `f6e7f4abf5c90f753fea5e338fb4ad8ed10346458b945483ae6c059e1cd7fe3a` |
| EV-PHYS-DOC | `doc/v5-重构/V5-N3-PHYSICAL-GOLDEN-BINDING.md` | `641fab2bbf81d55825fc42bed73e9d5def6a301afad561847e9c82f7eb5541ab` |
| EV-PHYS-ACC | `doc/v5-重构/CODEX-V5-N3-PHYSICAL-ACCEPTANCE.md` | `e4621aee129d86a31c51e82e147022b1633f6ccd33184b1410dea2affd2d9b87` |
| EV-SHUF-DOC | `doc/v5-重构/V5-N3-SHUFFLE-GOLDEN-BINDING.md` | `2179ad99ee3139f28785d24ae2a3e9299b0caff17f9987f6807468785cce2842` |
| EV-SHUF-ACC | `doc/v5-重构/CODEX-V5-N3-SHUFFLE-ACCEPTANCE.md` | `9343395dab91501e751dad6934ad87faa1a6c474d85e827cdf9871f2a33f47db` |
| EV-AI-DOC | `doc/v5-重构/V5-N3-AI-REPLAY-BINDING.md` | `dd94ef8f32d9759fd1fbe2b6974d728cbdc27414c7f3f31842e692a3b74ecb1a` |
| EV-AI-ACC | `doc/v5-重构/CODEX-V5-N3-AI-R2-ACCEPTANCE.md` | `0b48be12d87c1a4e43ef1e5b575b64c3ae85fd8711530014a152c365533f24b1` |
| EV-TABLE-DOC | `doc/v5-重构/V5-N3-TABLE-CHEAT-GOLDEN-ADMISSION.md` | `add5cb6dea7747fd7e781ee97e9d7414469baa0df10cd2834e676387c97f4d08` |
| EV-TABLE-ACC | `doc/v5-重构/CODEX-V5-N3-TABLE-CHEAT-ACCEPTANCE.md` | `e401de2d63fb65e6abab99136edd4e42c3c97fdb1e9bb969b264de6b025fe701` |
| EV-SLEEVE-DOC | `doc/v5-重构/V5-N3-SLEEVE-FROZEN-INPUT.md` | `181bc19e84aa83d08334e5419d63402b4f20c61ac0f651a393f1cad93a742b38` |
| EV-SLEEVE-ACC | `doc/v5-重构/CODEX-V5-N3-SLEEVE-FROZEN-INPUT-ACCEPTANCE.md` | `5c648e9824201a005743dd8d96912b8be19a188ad8553347e6fded7b99d61d91` |
| EV-R1-REVIEW | `artifacts/TEMP/codex-review-20261025/findings.md` | `e520991daa3b2c253053431a9bef3db032a802344c9ddea1a8c6002b5087f33e` |
| EV-R1-ANCHORS | `artifacts/TEMP/codex-review-20261025/anchors.tsv` | `3f881f63060a15e2a1404cb5e39956e2e6e13956a945941a3a87495be74b5539` |
| EV-R1-BASE | `artifacts/TEMP/codex-review-20261025/baseline.log` | `a1c919861c9c3065777703262826f055532e73f71682621f2d03bcf90d643718` |
| EV-R1-DOMAIN | `artifacts/TEMP/codex-review-20261025/domain.log` | `d3ce821392555fce4ea056049d30306c12a60882c1718a06100a43263ff81aad` |
| EV-R1-TABLE | `artifacts/TEMP/codex-review-20261025/table.log` | `574549978cf1ba28fbab03713140f6240c8f22685ed4f299773ffbdb2fd7e2df` |
| EV-R1-PHYSHASH | `artifacts/TEMP/codex-review-20261025/physical-hash-verification-value.log` | `1692254931aecf482485f68c150d771c390f621942f47d5c71fefd7f64cb327e` |
| EV-R1-PHYSV4 | `artifacts/TEMP/codex-review-20261025/physical-v4-reason-value.log` | `98497fcee6ce6707b33d3325eb9e9c1c0ccb2e019426138d013c0acb163bcf42` |

## 2. 分母：286 → 592 的确定映射（不改任务书、不改旧报告）

任务书旧数字 **286** 是 v3 时代的**混合分母**：scoped 家族按 (case, path) 实例展开、unscoped 家族却按"1 条归一化路径 = 1 单元"进账（48 个 winSamples status 实例只计 1、32 个 shuffle deadWallSegmentEqual 实例只计 1）。已接受的 R5-1 将其作废并建立实例真值分母；冻结审计报告原文（`V5-G1-N3-GOLDEN-FIELD-AUDIT.md`）：

> L1227：**诚实口径（R4-1 建立、R5-1 收紧）**：完整分母是**实例**而非路径——"101 条 GAP 路径"是 DIST 口径的定义投影，**不声明它枚举全部未绑定单元**；未绑定实例完整分母 = **592 units**（v3 的 286 为"scoped 逐实例 + unscoped 1 路径 = 1 单元"混合分母，R5-1 作废）。

> L3571（R5-1 行）：§11.4b 新增独立工具 **CensusInstances**……§11.6 verify-mapping **v4** 以该清单驱动**全族逐实例判级**……（`CASE-TOTAL units=655145 gap-units=592 mixed-paths=34`；E1 112 = status 48 + extraHan 48 + noWin 4×4；B2 32；B8 191 = 116+75；B11 11；B10 28 维持）【第 7 轮更正：34 的呈现口径含 7 条跨 fixture 假混合，fx 键后 27，§15.5 R6-1】

本台账保留两个历史口径的**原样出处**，并采用唯一分母 **592**（本机用移植 v5 语义的独立提取器逐行重推导，与 pins 全等，见 §7）。路径口径 101（DIST 首行投影）仍成立但只作投影参考，不作为本台账任何小节的小计。原 B4 round 子包 A1/A2 与 B4 同属 29 units（round-v1 同一 fixture 同批绑定交付），本台账不重复计数。

**14 包互斥闭合（本轮逐行复算 = 冻结 CASE-DIST 向量；判级为 R1 返修后向量）：**

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
| N3-E3 physical suitOrder/hashCrosswalk/scope | physical-v1 | 10 | 1 | 9 | 0 |
| **合计** | | **592** | **411** | **82** | **99** |

分母纪律：六 census fixture 之外**不进台账**。已集成的增量 fixture（movement-v1、shuffle-reversed-v1、visibility-v1、typed shuffle）只做 §8 增量登记，不重算历史分母、不刷新黄金哈希/分子、不把未导出当失败。`histClass` 列保留每实例在 rules.tsv 的历史类（UNBOUND 350 + UNBOUND_HC 242 = 592），且校验器对全部 592 行做 kind/parent/histClass 逐实例保真比对（`history-fidelity` 检查，R1 后新增）。node/leaf/case/path 四层不混用同一分母：node 全集 655,145、路径投影 101、UNA 17、缺口 592 各自独立成列。

**同数换判防护（R1 后新增）**：TSV 每行按 `id|verdict|fixCommit|csRead|assertion|evidence` 六列进入逐包 canonical digest，14 包 SHA-256 全部钉进校验器（`adjudication-pin` 检查）。在总向量不变的前提下互换任意两行判级（verdict-swap）必然改变该包 multiset → digest 失配 → exit 1（负例 `verdict-swap` 实证，§7#4）。**校验器钉 digest 是防篡改机制，不替代本报告对每个判级的实质举证**——E3 重判的实质依据是 §3 的 META/内部结构边界与 EV-R1-PHYSHASH/EV-R1-PHYSV4 两个 FALSE-CLAIM 探针。

## 3. 判级标准（硬规则，逐实例执行）

- **bound**：存在当前头可解析的 C# 读点（文件:行，逐行核验存在、在文件行界内、且锚点 token 至少含 1 条内容代码行——空行/注释/using/裸花括号锚被校验器拒绝），断言对 fixture 导出值做**值级**比对（数值/字符串/布尔/枚举/null-kind），且该包至少有一条**已接受的变异反证**（改 fixture 字段必须导致测试 FAIL、native exit 非 0）；fixCommit 必须是当前 HEAD 的**祖先**（`git merge-base --is-ancestor`，仅存在于对象库不算）。历史证据可复用，但必须核对当前头源码仍然匹配并如实标注历史实测时点，不冒充本轮实跑。
- **classified**：有**已接受的**不绑定理由——不可表示（无 C# 载体）、依赖阻塞（已声明依赖 + 探针纪律）、导出边界（exporter 恒定值且 API 无消费参数）、**META 溯源/内部结构边界**（导出物是溯源元数据或 fixture/manifest 结构清单，不是行为值——E3 九条即此类，判级依据见下）、元数据且 caller 已受理裁决路径。必须给全：理由 + 责任 owner + 复访条件。**classified 一律不计 full**，仍留在未覆盖分类清单（§5）。
- **pending**：语义可比较但既未绑定也未获得已接受排除的一切情形。**存在级检查（如 note 仅 `IsNullOrWhiteSpace`）不等于值级绑定**（任务书明示），目录 validator、测试数通过、note 存在、硬编码都不替代绑定。必须给全：责任 owner + 期限 + 门槛。该包若无任何已接受变异反证，一律不得标 bound（B6 即此情形）。

**E3 META/内部结构边界（R1 复核重判依据，D-R1-01）**：`V5-N3-PHYSICAL-GOLDEN-BINDING.md:97,108` 的既有边界本就把 hashCrosswalk 归类为 META 溯源。R1 探针实证：向导出描述字符串追加 `FALSE-CLAIM-CODEX-REVIEW` 字样后全部 nine 测试仍 PASS（EV-R1-PHYSHASH / EV-R1-PHYSV4）——导出描述与行为**无值级耦合**；`scope.targetIds` 是对硬编码常量 id 清单的结构比较（fixture/manifest 结构检查，closeout 规则排除其为行为绑定证据）。据此 9 条（hashCrosswalk.csharpHashVerification、hashCrosswalk.v4Reason、scope.targetIds×7）由 bound 重判 classified；suitOrder 保持 bound（`:317` 值级断言 + 38 探针生效变异反证）。592 集合与 14 包分母不变，**不要求新增行为实现**。

每包"改字段必须失败"的已验收变异反证映射（§4 逐包表列出处）：B1/B3/B4(cheat)/B5/B7/B8/B10/B11 → cheat 绑定探针 46/46 FAIL_AS_EXPECTED + table admission 28 矩阵（5 正 + 23 负）；B4(round) → round 60 负例 + 6 保持；B2 → shuffle 33 场景 76 预期失败；E1 → scoring 8 注入矩阵 + caller 12 注入各 1 FAIL；E2/E3 → physical 38 探针（1 正 + 37 负，锁 suitOrder 等行为值）；B9 → AI 48/48 变异矩阵（覆盖 events 非 text 字段；action.text 维度无反证，见 §6）。**B6 全包无任何变异反证 → 全包 pending，无伪造。**

## 4. 逐包判定（bound 411）

台账 TSV 每行 17 列：id / package / fixture / caseId / path / idx / kind / parent / histClass / verdict / csRead / assertion / fixCommit / evidence / reason / owner / revisit。`id = <包>|<fixture>|<caseId>|<path>|<idx>`（竖线分隔，与 TSV 实际值一致），唯一且可重构。以下按包给出判定要点；**锚点行号均为当前头核验读点**（R1 指出的 5 处漂移已修正 + 全量锚点结构审计 1272 组后 100 处逐案失准引用已精修，见 §12），校验器逐行做内容校验（空行/注释/using/裸括号锚即拒）与 commit 祖先校验，测试通过为本轮实跑（§7），变异反证为历史已接受轮的实测（如实标注）。bound 行 evidence 列全部引 §1a 注册表 EV-ID。

| 包 | 判定 | 关键 C# 锚点（当前头核验） | 值级断言要点 | 绑定交付（commit，均经祖先校验） | 变异反证（历史已接受） |
|---|---|---|---|---|---|
| B1（18 bound / 6 pending） | 非 note 18 条 bound；note 6 条 pending | `CheatGoldenTests.cs:1026,1029-1031`（wallPointerBeforeDelivery、deliveredTileId==reservedTileId+落位）；`CheatGoldenBindingTests.cs:557`（inputLastDiscardSeat）、`:2472`（AssertFacts factsBefore 逐 container/index/seat/tileId）、`:2585,2587-2598`（rinshanPointerAfterDraw、replacementTileId）、`:2641,2656`（两 refusal exception 字面量） | 指针/座位/牌 id 值相等；异常类型字面量相等 | a24b491（合并 db684dc），V5-N3-CHEAT-GOLDEN-BINDING.md | cheat 探针 46/46 FAIL_AS_EXPECTED（已接受验收轮） |
| B2（32 bound） | 全 bound | `ShuffleGoldenBindingTests.cs:415`：`Assert.Equal(fixture.DeadWallSegmentEqual, segmentEqual)` | 布尔由真实洗牌段重推导后逐实例相等 | 1c43a94，V5-N3-SHUFFLE-GOLDEN-BINDING.md | 33 场景 / 76 预期失败断言 |
| B3（21 bound） | 全 bound | `CheatGoldenBindingTests.cs:2141,2156,2195,2234,2275`（wallPointer）、`:2143,2158,2197,2236,2277`（rinshanPointer）、`:2131,2144,2150,2183,2198,2237,2278`（wallEndExclusive，逐案真读点：sb-activate-allowed=`:2131`、sb-activate-rinshan-allowed=`:2150`、sb-choose-first/second=`:2183`、sb-choose-rinshan=`:2237`，共享断言行 `:2144,:2198,:2278`）、`:2217,2270,2394`（offerId）、`:2367`（windowId）、`:2364`（availableWallCount，R1 修正：原 :2370 为 else 花括号） | GoldenInt/GoldenString 对活体 map/offer 值相等 | a24b491 | cheat 探针 46/46 |
| B4（35 bound = cheat 6 + round 29） | 全 bound | cheat：`CheatGoldenBindingTests.cs:1247,1274,1286,1287,1299,1328`（R1 修正四行：sameIdDifferentContent=1274、seatGroupLimitIsThree=1286、seatGroupLimit=1287、seatTileLimitIsTwelve=1299，原行号是尾花括号）；round：`RoundReplayGoldenTests.cs:208,211,229-230,233,243,339,368,416,435-436,454,505,520,521,532,552,833` | tileMarkState 异常字面量/容量布尔/版本值；chankan meldType/eligibility/双 phase/双布尔/resultOutcome/eventTypes 全序列（AssertTerminalLifecycle）、roundTerminal settlement outcome/totalDeltas 全数组、input 消费后回读 | a24b491 + 6c8597b（V5-N3-ROUND-GOLDEN-BINDING.md） | cheat 46/46；round 60 负例 + 6 保持 |
| B5（10 bound） | 全 bound | `CheatGoldenBindingTests.cs:1122-1123,1136-1137,1145-1146,1152-1153`（四拒绝组 outcome+exception）、`:1197-1198`（balancesEqual/entriesEqual 由真实 FromSnapshot 重推导） | 值相等 + 类型化错误枚举断言 | a24b491 | cheat 46/46（含 ledger 注入） |
| B7（1 bound） | bound | `CheatGoldenBindingTests.cs:2874` | requestException 字面量相等 | a24b491 | cheat 46/46 |
| B8（108 bound / 8 classified / 75 pending） | outcome/reason 中 55 案 108 条 bound；4 案 8 条 classified（§5）；note 75 条 pending | bound 两条传递链：**fixture→CaseSpec 值比较** `CheatGoldenBindingTests.cs:1652,1654-1660`（outcome 值级、reason null-kind/值级，note 检查 `:1663`）；**CaseSpec→真实行为调用** tileMark `:1875,1947`（`TileMark_BoundCase_ReplaysThroughApplyTileMark` + `CheatSubmission.ApplyTileMark`）、sleeve `:2007,2110,2113`（`SleeveBloom_BoundCase_ReplaysThroughDomainEntry` + `ActivateSleeveBloom[WithFrozenInput]`）、beam `:2679,2735`（`BeamSwap_BoundCase_ReplaysThroughDomainEntry` + `CheatSubmission.ActivateBeamSwap`）（R1 补全：原锚止于 CaseSpec，未达行为调用）；其中 actor 层再绑定的 4 案加锚 `N3TableCheatGoldenAdmissionTests.cs:63`（`denominator=5\|bound=4\|blocked=1`） | 每案 outcome 必等、reason 空类型或值必等（对 fixture） | a24b491 + 92b2ec8（N1b actor）+ 7d0bb5b/8d2709e（table admission） | cheat 46/46；table 28 矩阵（5 正 + 23 负）+ readiness 4/5 exit 3 |
| B10（28 bound） | 全 bound | `CheatGoldenBindingTests.cs` cost `:2148,2203,2242,2283,2405`、drawKind `:2125,2151,2230,2273`、drawnTileId `:2126,2152,2228,2272`、keep `:2171,2264`（逐案真读点：sb-choose-first=`:2171`）、nextAvailableWallIndex `:2239,2279`、nextDeadWallTileId `:2133,2154`、nextWallTileId `:2127-2128,2153`、offerCreatedTurnNumber `:2219,2271`、offerFirstTileId `:2229,2274`、offerSecondTileId `:2231,2276`、offerSecondWallIndex `:2180,2232,2234`（逐案真读点：sb-choose-first=`:2180`、sb-choose-rinshan=`:2234`；含 null-kind）、seat `:2123,2149,2212,2262,2358,2391`、turnNumber `:2218,2269` | Golden* 对活体对象值相等 | a24b491 | cheat 46/46 |
| B11（11 bound） | 全 bound | `CheatGoldenBindingTests.cs:2485,2529,2559,2610,2672`（AssertFacts factsAfter） | index/seat 逐元素值相等（UNA 17 旁置不混算） | a24b491 | cheat 探针 P9（m5 hand/discard index 2 实例生效变异）+ 46/46 |
| E1（48 bound / 64 classified） | winSamples expected.status 48 bound；extraHan 48 + noWin 四输入 16 classified（§5） | `ScoringGoldenTests.cs:262`（status 强制读为 win）+ `:269`（Explain 返回 null 即失败）+ 同测试内 CompareYaku/totalHan/… 全值链；classified 读点 `:294-300` | 状态双向强制 + 实际和形/符/役/点全值链 | 3332d11，V5-N3-SCORING-GOLDEN-BINDING.md §9（caller 验收内嵌） | 8 注入矩阵 + caller 12 注入各 1 FAIL exit 1 |
| E2（98 bound） | 全 bound | `PhysicalGoldenTests.cs:356-375` + `:260-281`（AssertTileSemantics 双入口）、`:402-411`（csharpExceptionMapping 三字段对真实异常类型）、`:923-958`（invalid[*] 全字段，message `:947-950` 值级 `tileId must be 0..135, got N`、outcome `:928`）、helper `:930-943` | 11 语义字段 × 双入口值相等；异常映射对真实 thrown type | cabde78 + c4c02c5，V5-N3-PHYSICAL-GOLDEN-BINDING.md | 38 探针（1 正 + 37 负）FAIL_AS_EXPECTED |
| E3（1 bound / 9 classified） | suitOrder 1 条 bound；hashCrosswalk（csharpHashVerification、v4Reason）+ scope.targetIds×7 共 9 条 classified（R1 重判，§5） | bound：`PhysicalGoldenTests.cs:317`（AssertSuitOrder 对 34 组实映射）；classified 读点 `:165-189`（csharpHashVerification：选段断言 + 无存储 C# 哈希的结构缺失 + V5 同墙重建性质，`:181` 为注释行）、`:150-156`（v4Reason：选段子串 + 源文件清单）、`:110,115-117`（targetIds 对常量清单的结构比较） | suitOrder 值级；9 条 classified 为 META/结构导出，非行为值绑定（FALSE-CLAIM 探针 EV-R1-PHYSHASH/EV-R1-PHYSV4：向导出串追加假声明仍全绿） | cabde78 + c4c02c5 | 38 探针（锁 suitOrder 行为值）；META 三导出无值级反证（如实：探针不覆盖 META 串） |

## 5. classified 82（不偷 full；未覆盖分类清单继续保留）

| 组 | 实例 | 不绑定理由（已接受） | 责任 / 复访 |
|---|---:|---|---|
| B8 4 案 outcome/reason | 8 | `tm-capacity-tiles`、`tm-decision-mismatch`、`bm-activate-deadwall-empty`、`bm-activate-deadwall-dora-only`：Domain manifest 显式 blocked（`:1677-1684` 钉死 7 案清单），其中 3 案已由 actor 层（92b2ec8 + 7d0bb5b）再绑定，余 4 案为"无活跃局/死墙前提在该层不可表达"的**依赖阻塞**：accepted manifest 纪律要求 blocked 行不得计入 behaviour-bound 并必须声明依赖 + 探针纪律（23 条负例锁死 REJECTED 语义）。值级比对存在但绑定对象是占位语义而非最终生产行为 | P1.4（依赖：actor 层可表达性）；复访 = actor 层能表达该前提之日重开，否则持续排除在 full 之外 |
| B9 events[*].text | 1 | 表示差：C# `RoundEvent` 无 text 载体，exporter 该字段为 V4 展示文本；`V5-N3-AI-REPLAY-BINDING.md` §3/§5.4 已声明不可表示，48/48 变异矩阵锁定其余字段 | P1.6；复访 = RoundEvent 增设 text 载体或 exporter 收缩字段（caller 已在 AI 验收签收该边界） |
| E1 winSamples input.extraHan | 48 | 导出边界：`WinningScoreEvaluator.Explain` 无 extraHan 参数，读点不消费；exporter 对 win 样本恒导 0。`V5-N3-SCORING-GOLDEN-BINDING.md` 交付边界（:223,244-246）已接受 | P1.2 + caller（API 扩展裁决）；复访 = Explain 增参或导出侧收缩 |
| E1 noWinSamples doraIndicators/uraDoraIndicators/honba/riichiSticks | 16 | 同上边界：noWin 路径 `Evaluate(hand,context,isDealer,extraHan)` 签名无此四参（`:294-300`），exporter 恒空数组/0 | P1.2 + caller；复访 = Evaluate 扩签名 |
| E3 hashCrosswalk.csharpHashVerification + hashCrosswalk.v4Reason | 2 | META 溯源边界（`V5-N3-PHYSICAL-GOLDEN-BINDING.md:97,108` 既有定义）：二者是对导出描述的选段子串/结构断言，R1 探针证明向描述追加 FALSE-CLAIM 字样全部测试仍 PASS（EV-R1-PHYSHASH、EV-R1-PHYSV4）——无值级行为绑定，实为来源元数据 | P1.1 + caller（META/internal 裁决）；复访 = caller 若裁决这些 META 导出为行为绑定目标则重开并建值级读点，期限 2026-11-30 |
| E3 scope.targetIds | 7 | 内部结构边界：`PhysicalGoldenTests.cs:110,115-117` 将导出 scope.targetIds 数组与计数同硬编码常量 id 清单比较，属 fixture/manifest 结构检查（closeout 规则排除为行为绑定证据）；同批 tile 语义已由 E2 逐实例值级绑定（`:356-411`） | 同上：P1.1 + caller；复访 = caller 裁决（绑定 → 建值级读点 + 反证；排除 → 显式规则），期限 2026-11-30 |

## 6. pending 99（责任 + 期限 + 门槛，全部不藏）

| 组 | 实例 | 现状读点 | 为何不能 classified 成"不用做" | owner | 期限 / 门槛 |
|---|---:|---|---|---|---|
| B8 note（tileMark 20 + sleeve 32 + beam 23） | 75 | `CheatGoldenBindingTests.cs:1663`：`Assert.False(IsNullOrWhiteSpace(spec.Note))`（R1 修正：原 ：1672 为收尾花括号） | BindingManifest 对 75 案 outcome/reason 做值级比对，**note 仅存在级**，从不与 fixture note 内容比对（`:1652,1654-1660` 对照）。V4 note 叙述可语义比较，存在级不能替代值级（任务书硬规则）；该包有变异反证、note 维度无 | P1.4 | 2026-11-30；门槛 = 逐案 note 值级绑定（或 caller 明文规则"note 为 V4 叙述元数据"并留裁决记录） |
| B1 tileMovement note | 6 | `CheatGoldenTests.cs:369`：`Assert.False(note.Length==0)` | 同上，同包其余 18 条已 bound 证明绑定模式可行，note 是残余未绑维度 | P1.4 | 2026-11-30；同上 |
| B6 mappingBasis（7）+ mappingLimits（1+3） | 11 | 无 C# 读点（csRead='-'） | 审计 L3523 明文："tags/mappingLimits 元数据：绑定或显式排除由 caller 裁决（N3-E1(d)/N3-B6）"，裁决未落地；且 **B6 全包无任何已接受变异反证**，按硬规则不得标 bound，也不得未经裁决偷计 classified | caller 裁决 + P1.6 | 2026-11-30；门槛 = caller 二选一裁决（绑定 → 建读点 + 变异反证；排除 → 显式规则）后改账 |
| B9 v4Call.action.text | 7 | 无 C# 读点 | 与 B9 events text 不同：action.text **既无绑定也无已接受的不可表示声明**（DEF-5(b) 不满足）；不能借同类"表示差"名义搭车 classified。B9 包的 48/48 变异矩阵覆盖 events 其余字段，**不覆盖 action.text 维度** | P1.6 + caller 裁决 | 2026-11-30；门槛 = 绑定（值级 + 变异反证）或显式不可表示规则，二取一 |

## 7. 本轮实测（PASS/FAIL/BLOCKED/NOT_RUN 逐一列证）

| # | 检查 | 命令（原样） | native exit | 计数 | 证据 |
|---|---|---|---:|---|---|
| 1 | census/rules pins + v5 语义逐行重推导 | `powershell -File artifacts/TEMP/extract-gap-instances.ps1 -Evidence <E> -OutDir artifacts/TEMP/extract` | 0 | units=655145 gap=592 una=17 mixed=27；逐包向量 24/32/21/35/10/11/1/191/8/28/11/112/98/10 全等 | 临时提取器（不入库），PIN-OK 输出 |
| 2 | R1 返修批量变换（同断言重放）+ 100 处逐案锚点精修 | `powershell -File artifacts/TEMP/rework-r1.ps1 -RepoRoot <repo>`；`powershell -File artifacts/TEMP/r2/correct-anchors.ps1 -RepoRoot <repo> -OutTsv <staging>`（staging 经结构审计 1272 组锚点 0 flag 后原子落位） | 0 | REWORK-OK rows=592 bound=411 classified=82 pending=99；STATS b3-anchor=1 e3-meta=9 b8-enrich=108 b4-anchor=4；CORRECT fixes=100 rows=592（B8 62 处 :1654 对 + B3 5 处 wallEndExclusive 逐案 + B10 3 处 keep/offerSecondWallIndex 逐案 + E2 30 处 Physical 属性行）；写后字节断言（BOM+`id` 表头、593 行） | `artifacts/TEMP/rework-final.log`、`artifacts/TEMP/r2/correct-run.log`（不入库；逐包 digest 见校验器 `$adjudicationPin`） |
| 3 | 台账校验器基线（**本轮最终版 v2**） | `powershell -File tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot <repo>` | 0 | **24 项 CHECK-OK**（证据注册表 26 项重哈希、pins/1516/655145/592/17/27/542、唯一集合、集合等式、17 列 schema、census 保真 592 行、fixture 纪律、闭合 411/82/99、锚点内容+祖先、bound 义务列、证据 ID 解析、classified/pending 纪律、14 包 digest、blocked 集） | `artifacts/TEMP/verify-r2-final.log` SHA-256 见 §12 后交付记录 |
| 4 | 负例矩阵 v2（20 例） | `powershell -File artifacts/TEMP/run-negatives.ps1 -RepoRoot <repo>` | 0 | **20/20 变异全部拒绝（exit 1 且 FAIL-TOKEN 命中）**：原 14 例（漏行、重复ID、id-内容漂移、错fixture、缺锚点、锚点越界、锚点文件不存在、pending 伪装 bound、未知 commit、classified 删理由、pending 无期限、pending 带假 commit、包错挂、表头破坏）+ R1 新 6 例（census 保真破坏→history-fidelity、bound 义务/证据抹除→bound-obligation+bound-evidence、非祖先 commit→bound-commit-ancestor、空锚点→bound-anchor-content、同数换判→adjudication-pin、列数破坏→ledger-schema） | `artifacts/TEMP/neg-run-final.log` SHA-256 见 §12 后交付记录 |
| 5 | Domain 当前头实跑。**D-R2-01 更正标注**：原 `--filter` 未生效，原命令实际执行的是 Domain 测试项目**全项目**（非定向）；有效定向以直接 runner 为准。历史日志/数值原样保留 | 原命令（保留）：`dotnet test tests/Mahjong.Domain.Tests --filter "FullyQualifiedName~CheatGoldenBindingTests|~CheatGoldenTests|~RoundReplayGoldenTests|~ScoringGoldenTests|~ShuffleGoldenBindingTests|~PhysicalGoldenTests"`（dotnet = D:/Unity/dotnet/dotnet.exe 8.0.426，Debug）。filter 未生效对照实证：注入 `--filter CODEX_NO_SUCH_TEST` 仍执行 506 项（`artifacts/TEMP/codex-review-r2-20261026/ignored-filter.log`，SHA-256 见 §13）。有效定向命令：`D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Debug/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGoldenBindingTests' -class '*CheatGoldenTests' -class '*RoundReplayGoldenTests' -class '*ScoringGoldenTests' -class '*ShuffleGoldenBindingTests' -class '*PhysicalGoldenTests' -result-xml <TEMP>/six-classes.xml` | 0 | 原命令实际 = **Domain 测试项目全项目 通过 506 / 失败 0 / 跳过 0**（R1 复审独立实跑同数：EV-R1-DOMAIN）。有效直接 runner 六类定向 = 选中 6 类共 **124：通过 124 / 失败 0 / 跳过 0 / 未运行 0** | 全项目：`artifacts/TEMP/dotnet-domain.log`（历史原样保留，SHA 见 §13）；定向：只读证据根 `artifacts/TEMP/codex-closeout-validation-3ce8cd8e3e8849cbb29cd2a3b32ea29a/six-classes.log`（SHA `6df002bf…`）+ `six-classes.xml`（SHA `dfc1b3f9…`） |
| 6 | TableRuntime actor 准入当前头实跑。同 #5 口径更正（D-R2-01）：原命令实为 TableRuntime 测试项目**全项目**执行 | 原命令（保留）：`dotnet test tests/Mahjong.TableRuntime.Tests --filter "FullyQualifiedName~N3TableCheatGoldenAdmissionTests"`。有效定向命令：`D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Debug/net8.0/Mahjong.TableRuntime.Tests.dll -class '*N3TableCheatGoldenAdmissionTests' -result-xml <TEMP>/table-class.xml` | 0 | 原命令实际 = **TableRuntime 测试项目全项目 通过 373 / 失败 0 / 跳过 0**（R1 复审独立实跑同数：EV-R1-TABLE）。有效直接 runner actor 类定向 = 选中 1 类共 **7：通过 7 / 失败 0 / 跳过 0 / 未运行 0** | 全项目：`artifacts/TEMP/dotnet-table.log`（历史原样保留，SHA 见 §13）；定向：同上证据根 `table-class.log`（SHA `9789ac85…`）+ `table-class.xml`（SHA `28499ae5…`） |
| 7 | 锚点逐条核验 | sed/grep 逐条（§4 全部行号）+ 校验器内容/祖先检查 | 0 | 每条在当前头源码中逐字命中；5 处 R1 漂移修正（§12） | 会话记录 + verify-baseline-v2.log 的 bound-anchors/bound-anchor-content/bound-commit-ancestor 检查 |

**FAIL：无。BLOCKED：无（本轮交付面内）。NOT_RUN（如实单列，不冒充通过）：**
- 历史变异探针（cheat 46、round 60+6、scoring 8+12、physical 38、shuffle 33/76、AI 48、table 28、readiness 4/5）本轮**未重跑**，按已接受验收轮证据引用（注册表 EV-*-DOC/EV-*-ACC；各轮 native exit 已记录于各自验收文档）。本轮以 §7#5/#6 的当前头实跑 + §7#7 的锚点逐条核验确认"当前源码匹配"。**R1 新增探针（EV-R1-PHYSHASH/EV-R1-PHYSV4，FALSE-CLAIM 注入）为复审方本轮已接受实测**，本台账如实引用不重跑。
- PG/WSS/Unity IL2CPP/Gradle/Android 设备/物理网络/远端 CI：NOT_RUN，与本任务无关，不claim。
- 解决方案级全量 `dotnet test` 与 Gradle `build`：未跑（任务书禁止盲跑重型全量）。澄清（D-R2-01）：§7#5/#6 的 `--filter` 未生效，两命令实际为各自测试项目的**全项目执行**（506/373），仍非解决方案级全量；有效定向计数为直接 runner 的 124/7（§7#5/#6）。
- 校验器负例矩阵构造物（TEMP 副本）与提取器、返修 harness 均不入库；`artifacts/TEMP` 不进 commit（复审证据根 `codex-review-20261025/` 同样不入库、只读保留）。

环境说明：C 盘历史性紧张——本轮 process-local `TEMP/TMP` 全部指向 `D:/majiang-wt/n3-closeout-ledger/artifacts/TEMP`，未向 C 写任何大产物；未改机器级环境/信任。控制台为 GBK 代码页，PowerShell 重定向日志存在 UTF-16/宽度折行现象，本报告引用的日志 SHA 均为实际文件字节（折行不影响 grep FAIL-TOKEN 哨兵行，校验器为此专设恒短哨兵行）。

## 8. 增量 fixture 登记（不进 catalog、不改 592 分母）

四项均为**各自已验收交付的承接登记**：本轮只登记归属与追溯锚，不重复计数、不重算历史分子、不把未导出项当失败。V4 随机源/洗牌实现未因本台账改变。以下路径/SHA/锚点均为当前头逐项核实（D-R1-04 补全）。

| fixture | 交付 commit（祖先校验通过） | fixture 文件 / SHA-256 | 规模 | 消费者与锚点（当前头核验） | 边界 |
|---|---|---|---|---|---|
| movement-v1（G-004..G-007） | fdd0287 `test(v5): export movement golden G-004..G-007 and bind Domain` | `tests/v5-golden/movement-v1.json` / `5927f44a650db963813c352b9786147a5953772862ca33722fdd9ce3e17fa3c5` | 8 案例（caseId×8，caseCount=8） | `dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs:104-111`（schema/generatorVersion/rulesetVersion/caseCount/extractionStatus/clockUsed/implicitRandomUsed 表头值级链）；配套 probe `tools/v5-golden/probe-movement-binding.ps1` | E2/E3 同族物理移动增量；不入六 fixture census、不入 592 分母 |
| shuffle-reversed-v1 | 8734494 `test(v5-p01): add actual V4 reversed shuffle wall companion` | `tests/v5-golden/shuffle-reversed-v1.json` / `81d21f0a0c0c6fda6effb029364d5bc8f4ee30c84f1431246fdd9db5309f5902` | 32 案例（caseCount=32，逆序墙伴生） | `dotnet/tests/Mahjong.Domain.Tests/ShuffleReversedGoldenTests.cs:213-218`（CompanionSchema/ExporterPath/generatorVersion/rulesetVersion/extractionStatus/v4BaseCommit 表头值级链） | 逆序洗牌伴随 fixture；不重算 B2 分母、不刷新黄金哈希 |
| visibility-v1（G-057） | 544f9c1 绑定 + e8a9e9a 复核返修（f9fd8e8 记录验收） | `tests/v5-golden/visibility-v1.json` / `6506c0729a36e4625e157a5e6ea2ccb79b9c2ff2e4f3f57988bbb9535cc52b94` | 4 案例 / 12 id 面（caseCount=4） | `dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs:50,79,87,90-98`（冻结输入清单 + 复现命令串，8 Facts；probe 67 runs） | G-057 可见性增量；已接受独立验收；不入分母 |
| typed shuffle 随机接缝（D-R1-02） | 99a7a76 `N3: add ordered typed shuffle random input seam` | 无 fixture 文件（纯接缝交付） | 9 Facts | `dotnet/tests/Mahjong.Domain.Tests/ShuffleTypedRandomTraceTests.cs:104-121`（TraceExhausted 码 + ConsumedRandomCount 严格序断言）；生产侧 `dotnet/src/Mahjong.Domain/Stacking/ShuffleTypedRandomTrace.cs`（两者 SHA-256 见 §8a） | 有序类型化随机源接缝；无分母影响 |

### 8a. 结构分母登记（paths / leafPaths / nodeInstances / leafInstances，R3 补全 D-R1-04）

计数口径（与复核方同口径脚本逐字节一致，见下方复算证据）：根、对象、数组容器均计 node；每个 scalar（含 null）计 leaf；数组路径归一为 `[*]`、object key 保留为归一化路径；Paths/LeafPaths 为归一化路径**去重数**，NodeInstances/LeafInstances 为**出现总次数**。**以下数值是登记，不计行为覆盖、不计 full、不改 1516/655145/592/17、不重做六个旧 census fixture 审计。**

| fixture | normalized paths | scalar leaf paths | node instances | scalar leaf instances | v4 来源 pin（fixture 内字段 → 本仓 commit，`merge-base --is-ancestor` 祖先校验通过） |
|---|---:|---:|---:|---:|---|
| movement-v1 | 326 | 229 | 47973 | 38685 | `v4Commit` = `d6bc69fa930d005b355a1273ec732fbdf82bbe2f`（2026-10-25 13:47:01 +0800，`docs(v5-n3): record bounded cheat binding acceptance`） |
| shuffle-reversed-v1 | 160 | 116 | 14236 | 13807 | `v4BaseCommit` = `03084f08af6e2ddefa2ebc6e094de5dac2474fd4`（2026-10-25 15:12:47，导出时点冻结树）；fixture 另含 `v4Commit` = `37b4dd610c5e7908dc4938378278c1475e0eee73`（2026-10-23）；两者均祖先 OK |
| visibility-v1 | 770 | 528 | 5142 | 3999 | `v4SourceCommit` = `e8a9e9adc401becef6d05d4cf5de972e998df976`（2026-10-25 18:10:00，`fix(v5): rework G-057 visibility golden review findings`） |
| typed shuffle 随机接缝 | 无 fixture（不捏造 JSON 分母） | — | — | — | 生产源 `dotnet/src/Mahjong.Domain/Stacking/ShuffleTypedRandomTrace.cs` SHA-256 `8d428acb32c498b2bdfde7c223f84167423454b66ddf729d80d228e9de9ad682`；消费者 `dotnet/tests/Mahjong.Domain.Tests/ShuffleTypedRandomTraceTests.cs` SHA-256 `e2ab68c4e8a9be8a32353073458d7f46e13a7edfd53de16a0980d6d910a75630` |

同口径复算证据：复核方只读证据根 `artifacts/TEMP/codex-closeout-validation-3ce8cd8e3e8849cbb29cd2a3b32ea29a/` 内 `incremental-census.ps1`（SHA-256 `9efa7c2f4cdf9e47fceb199dd0769ed803832ebae561736a3bdb4194995d27dc`）与其输出 `incremental-census.json`（SHA-256 `b30b98958c0039acc9c5919099cb4c61443f86ee6ab56c268813fe35c801a961`）。本侧复算：把该脚本仅改输出路径（产物落 `artifacts/TEMP/r3/`，其余逐字节同脚本）后运行 exit=0，产出 `artifacts/TEMP/r3/incremental-census-repro.json` SHA-256 = `b30b98958c0039acc9c5919099cb4c61443f86ee6ab56c268813fe35c801a961`——**与复核方 JSON 逐字节同哈希**，上表四组数值即其内容（脚本逐字节副本与运行日志同存 `artifacts/TEMP/r3/`，不入库）。三项 fixture 交付 commit 时点（commit 全串见 §8 表）：movement fdd0287（15:12:15）、shuffle-reversed 8734494（15:53:09）、visibility 544f9c1（17:18:37）+ e8a9e9a（18:10:00 复核返修）、typed seam 99a7a76（15:06:36），均为 2026-10-25 +0800。

## 9. 与 G-049 / N3-C1 / Δ29 的关系（不越权）

G-049 维持未裁决（catalog 口径 none、审计建议 partial、N3-C1 caller 裁决未执行）；Δ29 与 catalog 重算均属 N3-C1 caller 裁决项，**本台账不改 catalog、不自行重定义门 2、不替 caller 裁决**。审计 §15.5 与 E' 证据 SHA 原样保留，未触碰任何已接受报告。

## 10. 下一批非重叠 pending 组合（≥2 包且 ≥50 单元；不预派任务，仅给 caller 输入）

| 组合 | 包 | 单元 | fixture | 拟定 owner | 依赖 / 门槛 | 与其他组合重叠 |
|---|---|---:|---|---|---|---|
| **P-NOTE-A** | N3-B8(note) + N3-B1(note) | 75 + 6 = **81** | cheat-v1 | P1.4 | 先决 = caller 对"note 为 V4 叙述元数据"或"须逐案值级绑定"二选一明文裁决；若选绑定，须复用 cheat 绑定模式（值级断言 + 每案至少 1 变异反证），验收时重跑本台账校验器（592 向量按裁决改判） | 源码/测试文件集与 P-ALIGN-A 不重叠（note 维度只触 `CheatGoldenTests.cs`/`CheatGoldenBindingTests.cs`；P-ALIGN-A 只触 ai 绑定测试与 ai fixture 读点）；同包 B8 其余 116 条已闭合，不重开 |
| **P-ALIGN-A** | N3-B6 + N3-B9(action.text) | 11 + 7 = **18** | ai-v1 | caller 裁决 + P1.6 | 先决 = caller 裁决（B6：绑定 vs 排除；action.text：绑定 vs 不可表示规则）；变异反证现状 = B6 全包无、B9 仅 events 非 text 字段有 48/48（action.text 维度无），裁决为"绑定"时须先建读点再补变异矩阵（AI 48/48 模式可扩） | 源码/测试文件集与 P-NOTE-A 不重叠；B9 events text 1 条已 classified 不在组合内 |

**共享触点（如实声明，修正 R1 前版本"文件集不相离、可并行委派"的不准确表述）**：两个组合的**源码/测试文件集**互不重叠，但都终结于**同一台账交付物**——改判都必须更新同一 `V5-G1-N3-CLOSEOUT-LEDGER.tsv`、重算同一批逐包 digest、重跑同一校验器。因此**交付层（TSV+校验器+报告）必须串行提交**；仅源码/测试实现层可并行委派。合计 99 units 全覆盖后，99 条 pending 按 caller 裁决逐条改判计入 bound/classified（在 411/82 现值上相应增加，总数恒为 592），向量闭合为 592 = bound + classified + 0 pending，届时 N3 台账进入"无 pending"状态，**是否构成 G1 重审输入由 caller 依九门单独裁决，本台账不宣告**。若 caller 走"零 pending 后单次当前头 G1 九门重审"路线，所需输入 = 本台账 + 校验器基线 + 负例矩阵记录 + 裁决后改判差异。

## 11. 红线复核（逐条自检）

- 仅改动三个 expectedFiles（本报告、TSV、tools/verify-v5-n3-closeout-ledger.ps1）；生产/测试/fixtures/catalog/manifest/已接受报告/统一 verifier/任务书/Unity 资源零改动（`git status` 干净提交前核对）。
- 未读兄弟未提交工作；未 push、未建 PR、未触发 CI；未转派；未改机器级环境。
- bound 411 全部有当前头可解析值级锚点（内容校验 + 祖先 commit）+ 已接受变异反证映射；note 存在级一律 pending；无目录 validator/测试数/硬编码冒充绑定；**E3 九条 META/结构导出不再冒充 bound**。
- classified 82 条全部有完整理由 + 责任 + 复访 + 注册表证据 ID，且**不计 full**；未覆盖分类清单（§5）继续保留。
- pending 99 条全部有 owner + 期限 + 门槛，无隐藏。
- B6 因无变异反证全包 pending，无伪造；每包变异反证映射齐备且表述一致（B6 全包无；B9 仅 events 非 text 字段有，action.text 维度无——§3/§6/§10 同一口径）。
- 未把环境阻塞标完成；PG/Unity/Gradle/设备全部 NOT_RUN 单列；**不宣告 G1 / G5 / 完整移植**；V4 唯一生产。

## 12. R1 复审返修映射（逐条）

复审全文与证据：`artifacts/TEMP/codex-review-20261025/`（findings.md SHA `e520991d…`，只读保留）。逐条修复映射：

| 发现 | 修复 | 落点与验证 |
|---|---|---|
| D-R1-01（MAJOR）E3 十行中 9 行 META/结构导出误标 bound | 按既有 META 边界（V5-N3-PHYSICAL-GOLDEN-BINDING.md:97,108）与 FALSE-CLAIM 探针实证重判：hashCrosswalk.csharpHashVerification、hashCrosswalk.v4Reason → classified（META 溯源）；scope.targetIds×7 → classified（内部结构检查）；suitOrder 保持 bound。592 集合/14 包分母不变，向量 420/73/99 → **411/82/99** | TSV 9 行 verdict/csRead/断言/owner/revisit 列；§2 向量表、§4 E3 行、§5 新两行；校验器 `closure` 向量断言 + `adjudication-pin` E3=`6754375c…`（同数换判即拒） |
| D-R1-02（MAJOR）读点不准：B3/B4 四行锚到花括号、B8 note 75 行锚到收尾花括号、B8 bound 行缺行为调用链 | 锚点修正：B3 availableWallCount `:2370`→`:2364`；B4 sameIdDifferentContent `:1283`→`:1274`、seatGroupLimitIsThree `:1294`→`:1286`、seatGroupLimit `:1295`→`:1287`、seatTileLimitIsTwelve `:1300`→`:1299`；B8 note 75 行 `:1672`→`:1663`；B8 108 条 bound 行补传递行为调用锚（tileMark `:1875,1947`、sleeve `:2007,2110,2113`、beam `:2679,2735`——fixture→CaseSpec 值比较→真实 Apply/Activate 调用→断言）。**追加减量精修（内容检查放行但逐案失准的 100 处引用，1272 组全量结构审计后逐条对源码修正）**：B8 62 处 v1 时代 `:1654` 行（`if` 结构行，与真比较行成对出现故内容检查放行）→ 删除并保留实际 reason/outcome 比较行 `:1659`/`:1652`；B3 5 处 goldenInputs wallEndExclusive 方法级引用 → 逐案真读点（sb-activate-allowed `:2131`、sb-activate-rinshan-allowed `:2150`、sb-choose-first/second `:2183`、sb-choose-rinshan `:2237`）；B10 3 处（keep `:2213`→`:2171`、offerSecondWallIndex `:2280`→`:2180`/`:2234`）；E2 30 处 Physical 空行/错位引用（`:367`→实际属性行 `:926-938` 逐字段、`:923`→`:389`、`:950`→`:947`）。校验器防御：`bound-anchor-content`（锚点 token 必须含 ≥1 内容代码行，全空行/注释/裸括号即拒）+ `bound-commit-ancestor`（merge-base 祖先校验，仅对象库存在不算） | 修正后每条锚点当前头逐字命中（§7#7）；staging 结构审计 1272 组 0 flag；负例 `anchor-line-one`、`unrelated-commit` 实证拒绝（§7#4）；精修前后逐包 digest 变化仅限 B3/B8/B10/E2（读点修正，组内 token 同时规范化为升序）与 B4（仅 4 行 token 升序规范化：`:368,339`→`:339,368`，读点集不变），校验器 `$adjudicationPin` 同步重钉（其余 9 包 digest 逐字节不变，证未触碰） |
| D-R1-03（MAJOR）evidence 列散文路径不可运行时追溯；旧 B6/B9 行引用错误审计哈希（ecae055e，实际 5a440c58） | 全 592 行 evidence 列改为 §1a 注册表 EV-ID 引用（26 项：9 份绑定文档 + 9 份验收 + census/rules + 复审 7 件）；校验器每次运行重算全部 26 项 SHA-256（`evidence-catalog`），bound/classified 行的 ID 逐一解析校验（`bound-evidence`/`classified-evidence`）；错误哈希实例随 EV-ID 化自然消除 | §1a 全表；校验器基线 evidence-catalog/resolution 双 CHECK-OK；负例 `bound-obligations-erased` 实证（抹除 asrt+evidence 即拒） |
| D-R1-04（MAJOR）§8 增量登记只有一句话，无路径/SHA/规模/消费者锚点 | §8 扩为六列表：每项补 fixture 文件实际路径 + SHA-256、规模（案例/Facts）、消费者测试与当前头锚点行、边界声明；交付 commit 均经祖先校验。**R3 补全（消除复审遗留）**：新增 §8a 结构分母登记——三增量 fixture 的 normalized paths / scalar leaf paths / node instances / scalar leaf instances（同口径复算与复核方 JSON 逐字节同哈希）、fixture 内 v4 来源 pin 字段与祖先校验、typed seam 生产源/消费者 SHA-256 | §8 新表（本轮逐项 sed/sha256sum 核实，见该表"消费者与锚点"列）；§8a 四行登记与复算证据（hashes 见 §13） |
| D-R1-05（MINOR）全文 6 处自相矛盾 | ①§10 算术 592=420+73+0（RHS=493）→ **99 条 pending 改判计入两向量后 592 = bound + classified + 0 pending**（总数恒为 592，§10）；②"文件集不相离可并行" → 改为"源码/测试不重叠、但 TSV+校验器+报告是共享触点、交付层须串行"；③B6/B9 反证表述矛盾 → 统一为"B6 全包无；B9 仅 events 非 text 字段有 48/48，action.text 维度无"（§3/§6/§10 同口径）；④§1 verify-mapping 路径 `E:/…`（盘不存在）→ `D:/…`；⑤§4 ID 分隔符 `#` → 竖线 `|`（与 TSV 实际一致）；⑥§7 命令/计数/SHA 全部刷新为本轮 v2 实测 | 本版全文；第 ①②③ 处见 §2/§3/§6/§10 对应段落；②的依据 = 两组改判都终结于同一 TSV/digest/校验器 |

**校验器 v2 防御矩阵与负例的对应**（负例全部 exit 1 且 FAIL-TOKEN 命中，§7#4）：history-corrupt→history-fidelity；bound-obligations-erased→bound-obligation+bound-evidence；unrelated-commit→bound-commit-ancestor；anchor-line-one→bound-anchor-content；verdict-swap→adjudication-pin；column-drop→ledger-schema；原 14 例照旧全拒。

## 13. 交付记录（R2 最终产物与日志 SHA-256）

向量 **592 = 411 bound + 82 classified + 99 pending**，14 包闭合；基线校验器 exit 0（24 项 CHECK-OK）；负例矩阵 20/20 全拒（exit 1 且 FAIL-TOKEN 命中）；Domain 506/0/0、TableRuntime 373/0/0 exit 0。**D-R2-01 更正标注**：506/373 为原 `--filter` 命令实际执行的各测试项目**全项目**结果（filter 未生效，对照实证 `artifacts/TEMP/codex-review-r2-20261026/ignored-filter.log`）；有效定向为直接 runner 的六类 124/0/0/0/0 与 actor 类 7/0/0/0/0（§7#5/#6，证据根只读保留）。以上为本轮实际 native 实测。

| 产物 / 日志 | SHA-256 |
|---|---|
| `V5-G1-N3-CLOSEOUT-LEDGER.tsv`（592 行，本版最终） | `edb9b158902779631683325fb060a3e7755df75fb28a3688ffe5cf7f50faf6a5` |
| `tools/verify-v5-n3-closeout-ledger.ps1`（v2，含 26 项证据注册表 + 14 包 adjudication-pin） | `f227ca4e28c5898ec457ba44b0959b7d7b343c4e8dac784507db514341a38ad8` |
| `artifacts/TEMP/verify-r2-final.log`（基线 24 CHECK-OK，exit 0） | `5e2e163daf5d24dd57e9cc0af3ec5737d5de8cdc8baab8e152268cfbc2a84f8f` |
| `artifacts/TEMP/neg-run-final.log`（20/20 拒绝，escaped=0） | `fefbff91d610edba07f842abe26f57782e3c61248d1f9ad5ee00f4dd991cb2af` |
| `artifacts/TEMP/rework-final.log`（REWORK-OK 411/82/99，STATS b3-anchor=1 e3-meta=9 b8-enrich=108 b4-anchor=4） | `ebeb28f2fd32ef466b092b445b2b2d910f70a35e90a6ec3a3eb4e9c84cfc1271` |
| `artifacts/TEMP/r2/correct-run.log`（锚点精修幂等重放：fixes=0 且输出与所采 staging 逐字节相等） | `9cc49119783c528a92c01ed47ca68c40ce91917bfc0e4ec49c7370788ffff830` |
| `artifacts/TEMP/r2/audit-staging2-out.txt`（1272 组锚点结构审计 0 flag） | `eddf889e965b591eb3a78587a1d4b9664cbc99525d1f39981b5168ab77a6eb70` |
| `artifacts/TEMP/dotnet-domain.log`（原 --filter 命令实际执行 = Domain 测试项目全项目 506/0/0，exit 0；D-R2-01 更正标注，日志原样保留） | `990eea1c8c58f165ac4380605c9b0b6fade6b5459c739bdf241819cdd0d92ade` |
| `artifacts/TEMP/dotnet-table.log`（原 --filter 命令实际执行 = TableRuntime 测试项目全项目 373/0/0，exit 0；D-R2-01 更正标注，日志原样保留） | `96bed376fcd582385d419c383ae1d8c31234c59b3cecb016710eabe816a53940` |
| 复核方只读证据根 `artifacts/TEMP/codex-closeout-validation-3ce8cd8e3e8849cbb29cd2a3b32ea29a/six-classes.log`（直接 runner 六类定向 124/0/0/0/0，exit 0） | `6df002bfef52e97890dcf16d4155d92e8a5fc6e59de8a42602aec12c5b9f0efc` |
| 同根 `six-classes.xml`（xUnit 结果 XML） | `dfc1b3f952de20c178a9cdea3acdcf5f026c474916dc8534f8659e810eada94e` |
| 同根 `table-class.log`（直接 runner actor 类定向 7/0/0/0/0，exit 0） | `9789ac8519726492a137923a71d42d25ca6ac968bbefe95a6a345614d8bc43ae` |
| 同根 `table-class.xml`（xUnit 结果 XML） | `28499ae51819cbda8b9c2670856c69c6b3b155ed2c0ca3f7eb7ab01148f26e2a` |
| `artifacts/TEMP/codex-review-r2-20261026/ignored-filter.log`（`--filter` 未生效对照实证：注入 `CODEX_NO_SUCH_TEST` 仍执行 506 项） | `056838f07896a6a8a5ae2a8d2403b2fa4562623bfc3093be1370c337fcc2265f` |
| §8a 同口径复算：同根 `incremental-census.ps1` + 其输出 `incremental-census.json`；本侧复算副本 `artifacts/TEMP/r3/incremental-census-repro.json` 与之**逐字节同哈希**（SHA 见 §8a） | `9efa7c2f4cdf9e47fceb199dd0769ed803832ebae561736a3bdb4194995d27dc` / `b30b98958c0039acc9c5919099cb4c61443f86ee6ab56c268813fe35c801a961` |

`artifacts/TEMP/` 不入库（含复审证据根 `codex-review-20261025/`，只读保留）；上表 TEMP 路径的 SHA 供复核方在本机复核对照。

## 14. Caller 串行裁决（2026-10-26，当前有效）

已接受线程 `thread_20261026_061703_b390`（note，交付 `d9159a3550b6a03931e0b576dce216d194d2ad5a`）与 `thread_20261026_061703_8ad8`（AI，交付 `bb34338e3b20d562c029204f42c1ca612f943e58`）的逐实例证据现统一入账。两份交付报告/TSV 保留原文，旧 census、历史 accepted 报告不改写。本次由 caller 在隔离集成树执行，不是 receiver 修改共享文件。

规则 N3-META-01：exporter 人工叙述、未进入领域返回值/输入/事件/回执/状态哈希/协议的 note，在其描述行为已有值级锚点时 classified；否则 pending。N3-META-02 适用于 AI exporter 的说明元数据/容器/隐私边界，行为义务不得借文字被豁免。N3-REPR-01 将 Kotlin action.toString 调试表示与 C# 结构化动作区分；不伪造跨语言文字等价。全部 classified 仍是未覆盖，不升为 bound/full。

| 变更包 | bound | classified | pending | 本次改判 |
|---|---:|---:|---:|---|
| B1 | 18 | 5 | 1 | note 5 classified，1 pending |
| B6 | 0 | 9 | 2 | mapping metadata 9 classified，2 pending |
| B8 | 108 | 80 | 3 | note 72 classified，3 pending；原 outcome/reason classified 8 不变 |
| B9 | 0 | 8 | 0 | action.text 7 classified；原 events.text 1 不变 |
| 其余 10 包 | 285 | 73 | 0 | 全部原行不变 |
| 总计 | **411** | **175** | **6** | **93 pending→classified；6 pending 保留具体义务** |

1516 路径、655145 节点、592 GAP、17 UNA、14 包分母均不变。独立串行审计核对 592 个 identity/kind/parent/histClass，恰好应用 99 个原 pending identity；493 条非目标 raw 行和 411 条 bound 全列不变。五个 note 的 census parent 历史截断继续冻结；完整 note 以 subset 的 noteText/noteSha256 追溯，不伪造修复 parent。

### 14.1 剩余六条（均不得计 full，期限 2026-11-30）

| 包 / 冻结实例 | 未覆盖行为 | owner / 复访条件 |
|---|---|---|
| B1 `mv-swap-drawn-with-wall-backfill` note | V4 backfill 槽位物化 | P1.4 + caller；补精确值级 backfill 断言，或另行显式接受 V4-only 物化边界 |
| B8 `tm-capacity-tiles` note | 真实 V4 groups=2、marked16..27 共12张，加 selected0/4/8/12 共4张；C# exactly-four group 无法表达此状态 | P1.4 + caller；补可达的 12+4>12 值级锚点，或另行显式裁决该边界。原 note `8 marked + 4 > 12` 是叙述笔误，8+4=12；原文/SHA 保留，绝不要求错误算术断言 |
| B8 `tm-decision-mismatch` note | 无活跃局的 V4 输入 | P1.4 + caller；安全 null-round 入口的精确值级断言，或另行显式裁决该边界 |
| B8 `bm-activate-deadwall-empty` note | V4 deadWallTileIdsInDeadWall 为空的过滤状态 | P1.4 + caller；可表达的候选抑制入口及空候选值级断言，或另行显式裁决非规范过滤状态边界 |
| B6 `$.v5Observation.mappingLimits[*]` idx1 | 每张弃牌 isClaimed；冻结 fixture 无弃牌，现 C# Assert.Empty | P1.6 + caller；有弃牌实例及 isClaimed 值级消费断言，或 exporter/PlayerView 实际暴露此字段 |
| B6 `$.v5Observation.mappingLimits[*]` idx2 | 每张弃牌 isRiichi；同上空集合边界 | P1.6 + caller；有弃牌实例及 isRiichi 值级消费断言，或 exporter/PlayerView 实际暴露此字段 |

### 14.2 同步 pins 与验证边界

校验器保留原 26 个 evidence pins，新增 11 个（共 37 个，运行时全部重哈希）：`EV-N3M-EXP/FIX/DOM/BIND/ADM/TMK` 为 note 报告 Appendix A 的六个源码 pins；`EV-NOTE-DOC/SUB` 为两份 note 交付；`EV-AIT-DOC/SUB/ACC` 为 AI 报告、subset 与 caller acceptance。精确路径/SHA 以 `tools/verify-v5-n3-closeout-ledger.ps1` 注册表为准。原行继续使用原 evidence；新行的 E-* 子证据键由 AI 报告与已接受 manifest 追溯。

仅四个包的裁决 digest 改变，其他十个原 digest 不变：

| 包 | 当前 SHA256（原 canonical 算法不变） |
|---|---|
| B1 | `6214892ea3e78443aacf151b517a481502bf9fd8b3644ea6c2a32609399e4e13` |
| B6 | `6a6a9e9fd5303a576537200a5b729344b63c9ecb708c018634c92c2d70e672a9` |
| B8 | `9b83fc2736b91e0334aefd92599225c73c7b4fbe43bbb8e13e80b94fff306d10` |
| B9 | `294d6eb3928f309ca1d50bcb11437832a87856f6e6bbc5bcf808a4b65a7f81f0` |

原 B8 blocked-set 检查明确限定原 outcome/reason 路径，仍精确要求四个原 blocked case；新增 note classifications 由逐包向量与逐身份 digest 约束，不混入该 outcome/reason 集合。没有删除身份、锚点、祖先、证据、义务、schema 或 census 检查，也未修改统一 verifier。

Caller 本轮在 note 工作区 fresh Domain/TableRuntime build 均 exit0、0 warnings/errors；direct runner/XML 精确选择 Domain 95/95、actor 7/7；81 note 全部替换仍 95/95、7/7；B1 结构化负例 3 FAIL，B8 Domain/actor 各2 FAIL，native exit1 均符合预期；无类控制0test，8/8矩阵通过。subset14项和容量纠错10项通过。集成树源码/测试/fixture 与实测交付树一致；当前台账校验24项、37 evidence hashes、14 digests 通过，catalog 仍6 fixtures/73 requirements/10389 cases/45 Facts，full17/partial21/none32/V5-only3。具体命令、重拒绝探针与日志 hashes 见独立 caller 记录 `CODEX-V5-N3-NOTE-ACCEPTANCE.md`。

这次裁决不重跑全量、PG/WSS、Unity/IL2CPP、Gradle、设备、物理网络或远端CI；不把历史同批 G1 证据报告的九门建议当作批准。六条 pending、N-2 宿主接线与最终 G1 重审仍按既有规则处理，不在本次自动闭环中宣告完成。复用唯一 PR19，不创建组件 MR。

## 15. 最后六条的 caller 分类候选（2026-10-26，待独立复核）

本补丁基于已发布且已接受的 `7606c5c1daf2523f1bdb653e439fcc4f3f278cca`，独占树 `D:/majiang-wt/codex-n3-final-adjudication-20261026-1045`。仅修改本文件、TSV 和台账专属校验器；不修改统一 verifier、源码、fixture、manifest/catalog 或历史已接受报告。此前 §14 的六项义务已分别收到证据，本节显式应用 caller 裁决，不用存在级断言或空集合冒充值级行为。

| 冻结实例（均为原 note/mappingLimits） | 本次明确裁决及证据 | 保留的未覆盖边界 |
|---|---|---|
| B1 mv-swap-drawn-with-wall-backfill | 依据已接受 residual 报告及 `CODEX-V5-PROGRESS-20261026-0955.md` 四项裁决，接受 V4 alias/旧槽物化表示差异；合法 discard29 → seat1 draw33 的行为证据保持 | 精确双运行时 backfill 物化未复现，完整双向运行 NOT_RUN；不免除可达规则和136守恒 |
| B8 tm-capacity-tiles | 接受 V4 groups=2 且 marked12 与 C# exactly-four-group 的表示边界；12+4拒绝和8+4成功证据保留 | 原 note 算术笔误及 SHA 不改，绝不引入错误算术规则 |
| B8 tm-decision-mismatch | 接受无活跃局输入不能构造 concrete-round actor 的边界；null-round 构造拒绝与现局无 decision 分别举证 | 两者均不称为原 V4 case 的等价回放 |
| B8 bm-activate-deadwall-empty | 接受 V4 空 eligibility filter 的非规范表示边界；pointer4下候选14/9及13实体构造拒绝已有证据 | 不把空过滤说成物理死山为空；可达空候选仍须拒绝、零费用/零 offer |
| B6 mappingLimits idx1 isClaimed | caller 明确接受 `29e11c1` 伴随 fixture 增量：实际非空 river/claimed discards、C# flag值消费及翻旗负例满足 §14.1 的行为复访条件，原说明串归为分类元数据 | 原 ai-v1 仍为空且冻结；PlayerView 没有 per-discard flag；原行不计 bound/full |
| B6 mappingLimits idx2 isRiichi | 同上，实际立直弃牌及 claimed riichi 组合、public事件推导、C#值消费和三类翻旗负例已有独立验收 | 同上；伴随样本证据不伪称原 ai-v1 有非空实例 |

前四条引用 `V5-N3-CHEAT-RESIDUAL-BOUNDARIES.md/.tsv`、其 caller acceptance 和0955明文裁决。后两条引用 `V5-N3-AI-DISCARD-FLAGS.md/.tsv` 及其 caller acceptance（含独立6正例和五组翻旗失败计数2/2/1/2/2）。这些是已接受的历史实跑，本补丁不宣称重新执行行为测试。每条仍保留 P1.4/P1.6 + caller、2026-11-30复访日期与精确触发条件；分类不免除未来新增生产适配入口的行为验证。

| 包 | bound | classified | pending | 本次变化 |
|---|---:|---:|---:|---|
| B1 | 18 | 6 | 0 | 1 pending→classified |
| B6 | 0 | 11 | 0 | 2 pending→classified |
| B8 | 108 | 83 | 0 | 3 pending→classified |
| 其余11包 | 285 | 81 | 0 | 全部raw行不变 |
| 总计 | **411** | **181** | **0** | **592冻结实例不变** |

校验器的原37个 evidence pins 完全保留，新增7个（44个）：EV-RES-DOC/SUB/ACC/DEC 与 EV-B6F-DOC/SUB/ACC。新文件逐个从已接受Git内容取SHA；没有为漂移旧文件刷新pin。仅B1/B6/B8三个canonical digest随六条合法裁决重算，其他11个digest不变；schema、census、history、真实锚点、祖先、义务、证据、blocked-set检查保持。`patch-summary.json` 记录六个精确identity、586 raw行保留与digest。校验输入所需七份历史ignored日志从已接受1010集成树复制，原始hash须由原校验器重验；只作历史来源，不充当本次实跑。

本补丁的验证范围是台账与分类，不是生产恢复、PG/WSS、Unity/IL2CPP或设备运行。独立复核仍须核对六项裁决的证据、完整identity保真、586行不变、原411条bound不变、11个digest及37个旧pins不变，并执行台账校验及拒绝负例。候选review通过后caller才可经唯一PR19普通push集成。新伴随fixture的目录增量登记仍须按既有规则检查，不借零pending跳过；随后才对同一最终集成头进行一次G1九门重审。G5未完成，V4仍唯一生产，不创建complete.flag。

本轮原始证据根 `artifacts/TEMP/caller-final-adjudication/`：`prepare.ps1` numeric exit0，`patch-summary.json` 实测六行改判及586行/历史身份不变（SHA256 `1ACADE348647EEFA25D317480E5EA312AE75BD96C3D60F797C7806455A154BF4`）；`provisioning.json` 记录18份旧pin对应文件的纯换行恢复，另三份先行恢复，均无内容差异或pin刷新。首次完整校验 `ledger.log` exit1、1911条级联报错，原因为旧文件raw换行不匹配，14个裁决digest本身一致。恢复原raw证据后第二次 `ledger-final.log` 已确认44/44文件SHA匹配、census/rules/路径/节点pins通过；完整重推导结果以原始日志终态为准，在未读到numeric exit0前不声称24项全通过。临时换行恢复文件不纳入补丁。拒绝负例脚本已准备，独立审查需实际执行并报告结果，不预报PASS。

本轮另只读下载、验证当前发布头CI：push [40260010463](https://github.com/liuchangready/majiang-game/actions/runs/40260010463) 实际checkout `7606c5c1daf2523f1bdb653e439fcc4f3f278cca`；PR [40260015059](https://github.com/liuchangready/majiang-game/actions/runs/40260015059) 实际checkout `6dfc93a2fd1562c430968daa8bde7bd1f559d9ff`，不混同API headSha。两者每份7个项目合计1808 passed/0failed/0skipped，solution build exit0、12 DLL scratch copy PASS、7份框架日志SHA逐一吻合各自meta，真实PG rollback/concurrency事件存在、owned root已删除、wrapper plannedExit0/failedStages空。`review-ci.ps1` numeric exit0，`ci-review.json` SHA256 `38B330FEF2EB239760E8B05CBC2A28B468BBEFE9A9FBADAF6D321549AB004B21`；原bundle及完整文件SHA表保留。此为远端实跑、本轮读取；未重跑CI，且不替本地未发布分类候选提供行为PASS。历史39970580589框架日志缺失仍未解决，不称flaky或已修复。

最终原始终态现已读取：第二次完整校验 numeric exit0、24 CHECK-OK / 0 CHECK-FAIL、44 evidence SHA及14 digests全部通过；ledger-final.log SHA256 `0A67C4838C9F6075708954A2BC36BB819836634E993F59DECD9B83A2B47E87CB`。校验完整重推导655145节点/592 GAP/17 UNA，闭合411/181/0。首次失败保留。校验完成后，21份临时换行恢复文件回到HEAD原字节，最终diff仅三份候选文件；没有提交任何旧文档或生产文件的换行变化。此项机械PASS仍不替代独立分类复核、负例和G1九门验收。
