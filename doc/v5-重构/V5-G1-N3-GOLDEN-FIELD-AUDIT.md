# V5-G1 N-3 前置独立审计报告 — 黄金样本字段级映射与覆盖口径核查（第 6 轮返修版）

- **审计执行**：Claude Code（本 agent），独占 worktree `D:/majiang-wt/n3-golden-field-audit`，分支 `wt/v5-n3-golden-field-audit`
- **冻结基线**：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（PR19 最新已发布头；工作树 status clean，HEAD 即该提交）
- **审计对象**：V5-1-REVIEW §7.1 N-3 [MAJOR]（G-028a 事件序列 + mv-swap-drawn-with-wall-backfill + 20 项 partial 的断言边界），以及"已导出黄金样本对齐率为 100%"这一 G1 门口径的字段级核实
- **交付路径**：`doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md`（任务 expectedFiles 指定路径；第 1 轮曾误用 `N3-GOLDEN-FIELD-AUDIT-REPORT.md`，第 2 轮已 `git mv` 统一，无第二拷贝）
- **版本史**：第 1 轮 = commit `5ba62bd`（REWORK_REQUIRED，R1-1..R1-5）；第 2 轮 = commit `48660db`（REWORK_REQUIRED，R2-1..R2-3）；第 3 轮 = commit `7ea62e6`（REWORK_REQUIRED，R3-1..R3-3，无 BLOCKER，3 组 MAJOR）；第 4 轮 = commit `4561431`（REWORK_REQUIRED，R4-1..R4-3，0 BLOCKER，3 MAJOR）；第 5 轮 = commit `da0155a`（REWORK_REQUIRED，R5-1/R5-2，0 BLOCKER，2 MAJOR）；第 6 轮 = 本文件，R3-1..R3-3 逐条映射见 §14、R4-1..R4-3 见 §15.3、R5-1/R5-2 逐条映射见 §15.4
- **报告日期**：2026-10-25
- **性质声明**：本报告只产出审计证据与最小补齐包建议；**不实施任何 P0.1/已接受项开发，不改 catalog/manifest/fixtures/生产或测试代码，不改固定头已接受 G1 盘点报告**。本报告组件完成 ≠ N-3 关闭 ≠ G1 批准。

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

### 1.3 两层覆盖口径（R2-1a 建立；第 4 轮加固为有序口径；第 5 轮按 R4-1 升级逐节点 case 归属 + 双口径统计；第 6 轮按 R5-1/R5-2 升级为全族逐实例单元真值 + where 实例判级 + UNA 类）

**"报告覆盖了全部字段"必须同时由两层独立机器校验证明，缺一不可；第 5 轮起路径级与单元级统计双口径并报（R4-1），第 6 轮起单元口径升为全族逐实例真值（R5-1）：**

- **层 1 = 路径枚举覆盖（COVERAGE 块）**：`verify-coverage.ps1`（源码全文内嵌 §11.5）把每条 census 归一化路径归入 prefix-2 组，6 份 fixture 各产出一个**完全划分**的 `组 = distinctPaths` 清单内嵌 §3.0。它证明的仅是"**枚举出的每条路径都进了报告、计数一致**"——**对语义零断言**：把映射整段删除、把某组路径漏报、或把计数抄错，verify 都会 exit 非 0；但**所有路径全部塞进一行 `META` 也能通过层 1**。
- **层 2 = 字段消费语义映射（FIELD-RULES 块）**：`verify-mapping.ps1` **v4**（源码全文内嵌 §11.6）对 §3.0.1 内嵌的 **542 条 8 列规则**逐条复核，在 v2 全部强制项（**有序 first-match 裁决**——第 3 轮"无重叠"表述已撤回，census 实测 935 条路径多命中、有序表裁决唯一；每条路径恰被一条规则**裁决**（无规则命中 = LEAK）；声明路径数 = census 重算 first-match 残差（COUNT）；判级类合法；**锚点真实性（-Repo 消费，`File.kt:L1-L2`/`File.cs:L(-L2)` 逐一打开核对行界）**；`(fixture,pattern,scope)` 无重复（DUP）；CONTAINER 不得持有标量（SCALAR-AS-CONTAINER））之上，新增 **R4-1 要求的逐节点 case 归属校验**：
  - **单位（unit）=（case，路径出现实例）——第 6 轮起全族逐实例（R5-1）**：unit 全集由独立工具 `CensusInstances`（§11.4b）枚举 = 6 份 fixture 的**每一个 JSON 节点出现实例共 655,145 个**（instances.tsv，含容器与 JSON `null` 叶），校验器逐 unit 从 manifest 判级并强制"每实例恰被认领一次"（v3 只对 scoped 家族逐实例展开、unscoped 家族仍按"1 路径 = 1 单元"进账——48 个 winSamples status 实例只计 1、32 个 deadWallSegmentEqual 实例只计 1，正是 R5-1 击穿的混合分母）；manifest↔census 双向路径集合等式由 `MANIFEST-CENSUS-MISMATCH` 机器强制。
  - **双向 scope 并集等式**：同 (fixture,pattern) 族内 scoped 行的 case 并集必须**等于**该路径实际出现 case 全集——并集多出/缺少任一 case 都报错；scope id 若指向**不存在的字段、跨 section 的案例、或该 pattern 下无任何 unit 的 id**，一律显式 SCOPE 错误（v2 的 presence 单向检查与"case 在 fixture 别处存在即放行"两个漏网形态全部堵死）。
  - **六类 SCOPE 错误 + where 谓词实例判级（R5-2）**：`SCOPE-ID-UNKNOWN` / `SCOPE-ID-ABSENT` / `SCOPE-UNIT-UNCOVERED` / `SCOPE-OVERLAP` / `SCOPE-MIXED` / `SCOPE-NO-CASE-ID`；scope 文法 `case=<ids>[;where=<field>==<v1>|<v2>]`——**第 6 轮起 where 是真实数据面**：谓词按 unit 的**实例父对象标量字段**（manifest `parent` 列，§11.4b）求值，同路径不同容器分区的 unit 由不同 where 行认领（factsAfter `index`/`seat` 的 6 条 where 拆分行由此机器可表、机器可证）；另增 `UNA-NOT-NULL` 校验（UNA 行认领的实例必须实为 JSON null；**第 7 轮起该守卫为 scoped 与 unscoped 全分支共享不变量**——第 6 轮 v4 仅装在 scoped 分支，unscoped 行认领非 null 实例不被拒绝，caller 复现在案，§15.5 R6-2）。
  - **双口径统计并排输出**：`DIST`（路径级 first-match 首行投影，历史口径保留）与 `CASE-DIST`/`CASE-TOTAL units=`（单元级真值：每 unit 按其案例实际认领行判类）并排；`CASE-MIXED` 行公布判类混合路径清单（**第 7 轮起以 (fixture,path) 为键、每行显式 `fx=`，27 条全部 cheat-v1——v4 仅以路径为键曾把跨文档同名路径误并出 7 条假混合，§15.5 R6-1**）。**"101 条 GAP 路径"是 DIST 口径的定义投影（保留，但不再声明它枚举全部未绑定单元）；未绑定单元完整分母 = CASE-DIST 口径 592 units（UNBOUND 350 + UNBOUND_HC 242；§2/§9），另有 UNA 17 units 为显式"不适用"（导出侧 null 结构占位、源码无对应读点）不计入缺口分母（§1.4）。**
- **验收现状**：两层正检均 exit 0（§11.5/§11.6）；**16 种 checker 变异负例全部 NEGATIVE-DETECTED + 1 项独立 mixed-namespace oracle 控制行**（§11.6 v5 负例矩阵：v3 的 12 种全保留 + 第 6 轮新增 3 种——where 谓词值破坏 → SCOPE-UNIT-UNCOVERED、UNA 行删除 → SCOPE-UNIT-UNCOVERED、UNA 行改认领非 null 实例 → SCOPE-OVERLAP+UNA-NOT-NULL + **第 7 轮新增 1 种——unscoped 行认领非 null 实例记 UNA → 48 × UNA-NOT-NULL（R6-2 修复面；v4 同输入 exit 0 假绿）**），含 caller R4-1 指定负例——给 `availableWallCount` UNBOUND 行的 scope 追加不存在的 `|sb-activate-disabled` → `FAIL SCOPE-ID-ABSENT cheat-v1|$.sleeveBloomHandler[*].goldenInputs.availableWallCount sb-activate-disabled`、exit 1（v2 同变异仍 `RULES-CHECK-OK-V2 scope-errors=0` exit 0，复现 log SHA `17EC626D…` 在案，§11.6）；**兄弟行换序不变性**实测升级（R5-1 验收、第 7 轮 fx 限定后复跑维持）：同族行轮换后**单元真值 CASE-DIST、逐实例 PATH-UNITS 与 fx 限定 CASE-MIXED/CASE-TOTAL 输出逐字节不变**，仅 DIST 首行投影有意随行序（历史语义保留，§11.6）；第 3/4/6 轮 caller 的全部 checker 反例先在旧器上复现（§3.0.1 反例复现表/§15.5），再逐代修复。

### 1.4 字段判级九类与锚点文法（R2-1a 建立；第 4 轮升级 8 列 TSV；第 5 轮起 scoped 行按单元级真值判类；第 6 轮按 R5-2 新增 UNA 类、where 谓词真实生效）

**判级类**（FIELD-RULES 第 4 列）：路径级统计（`DIST`）按有序表首条命中行归属；单元级统计（`CASE-DIST`）按**每 unit 所属案例的实际认领行**归属——scoped 兄弟行在两个口径下分别进账（§1.3）：

| 类 | 含义 | csAnchor 文法 | exporterAnchor 文法 |
|---|---|---|---|
| IN_BIND | 输入绑定：导出输入值作为 C# 真实入口构造载荷 | `文件.cs:行(-行)?`（真实文件+行，校验器 -Repo 复核存在性） | `文件.kt:行(-行)` |
| OUT_DIFF | 输出差分：C# 重放实算值与导出 expected 逐值对照 | `文件.cs:行(-行)?`（同上） | `文件.kt:行(-行)` |
| INTERNAL | fixture 内部结构/自检（无生产差分语义，显式声明保留；C# 侧读取处为内部一致性断言） | `文件.cs:行(-行)?`（同上） | `文件.kt:行(-行)` |
| SEM_MAP | 语义映射：同语义名称/表示映射（可绑定或已绑定，绑定须配映射注释） | `文件.cs:行(-行)?` 或 `GAP:<包ID>` | `文件.kt:行(-行)` |
| UNBOUND | 已导出可比较字段当前无任何 C# 断言消费 | `GAP:<包ID>` | `文件.kt:行(-行)` |
| UNBOUND_HC | 硬编码/存在级未绑定：C# 以硬编码常量或存在级读取替代导出值验证（断言存在但不构成值级对照；允许引用其存在级读点 `文件.cs:行(-行)` 或 GAP） | `GAP:<包ID>` 或 `文件.cs:行(-行)?` | `文件.kt:行(-行)` |
| UNA | **不适用（第 6 轮新增，R5-2）**：该实例为导出侧 JSON null 结构占位，且源码消费处对该容器分区**无对应读点**——显式"非缺口"标注，不计入缺口分母（校验器强制 UNA 行认领的实例 kind=null，`UNA-NOT-NULL`；≠ META：META 是无行为语义的 provenance 字段，UNA 是行为字段中不可读的 null 占位实例） | `UNA:<说明>` | `meta:<说明>` |
| META | exporter 元数据/provenance，无生产行为语义，显式排除 | `meta:<说明>` | `meta:<说明>` |
| CONTAINER | 纯容器节点（object/array 自身，无独立语义；校验器拒绝其持有标量） | `meta:<说明>` | `meta:<说明>` |

- **TSV 8 列**：`fixture / pattern / paths / class / exporterAnchor / csAnchor / owner / scope`。`scope` 列 = `*`（全量）或 `case=<caseId>|<caseId>|…[;where=<field>==<v1>|<v2>]`（case 级细化判类；**第 6 轮起 6 条 factsAfter `index`/`seat` 拆分行真实使用 where 谓词**，按实例父对象标量字段求值——§1.3/§11.4b）。**同一 pattern 存在多条 scoped 兄弟行时，`paths` 列按 pattern 残差计（同一 normalized 路径数在各行重复声明，校验器按 pattern 重算强制一致）；行与行之间的 case 归属由 scope 列单独机器校验**——**双向并集等式 + 逐 unit 认领**（caseId ∈ fixture 案例全集、scope 必须真实产出 unit、两两不相交、并集=该字段出现过的全部 case，§1.3 六类 SCOPE 错误）。路径级类分布按**表内首条命中行**归属（`DIST`）；单元级分布按每实例认领行归属（`CASE-DIST`/`CASE-MIXED`/`CASE-TOTAL units=655145 gap-units=592`——655,145 = CensusInstances manifest 全体实例，每实例恰认领一次）。
- UNBOUND/UNBOUND_HC 的 owner/csAnchor 必须指向补齐包（`GAP:N3-*`，§9）；exporterAnchor 必须**真实存在**（校验器 -Repo 逐一打开文件核对行界）；**禁止空串、省略号、仅文件名**（第 3 轮 322 条 filename-only 锚点已全部升级为 `文件.kt:L1-L2` 行段锚点并经 -Repo 校验）。
- 判级是**保守**的：语义可比但当前未绑定的（如 hasKanDeclared）一律记 UNBOUND_HC 并指向补齐包，**不以"可语义映射"抵扣缺口**（§13 INFO-1）。
- 本表与 §3.0.1 FIELD-RULES 块、§11.6 v4 校验器三方一致；§3 各节正文引用的计数全部可由 §11.6 的 `check` 命令 `DIST`/`CASE-DIST`/`PATH-UNITS` 输出复算（含按类、按 GAP owner 两种分布 × 两种口径）。


## 2. 总判定（先给结论，第 6 轮修正版）

1. **"已导出黄金样本对齐率为 100%" 不成立（字段级）**。两层口径实测：层 1 = 1516 条归一化路径全覆盖（§3.0，verify exit 0）；层 2 = 542 条语义映射规则、655,145 个实例逐 unit 认领校验全通过（§3.0.1，v4 check exit 0）。**已导出但当前无 C# 值级断言消费的缺口为双口径**：**路径口径 = 101**（UNBOUND 56 + UNBOUND_HC 45；§11.6 `DIST` 行机器输出——该口径是规则表 first-match 首行投影的**定义投影**，保留但不声明枚举全部未绑定单元）；**单元口径 = 592 units**（UNBOUND 350 + UNBOUND_HC 242；§11.6 `CASE-DIST` 行机器输出，CensusInstances 655,145 实例**全族逐实例**枚举、每实例恰认领一次——这是未绑定单元的完整分母）；另有 UNA 17 units（导出侧 null 结构占位且源码无对应读点）显式不计入缺口（§1.4）。全部带 `GAP:N3-*` 补齐包指针，分布：
   - **cheat-v1：路径口径 40 条 / 单元口径 292 units**（路径 = DIST 首行投影 B1 14 + B3 6 + B4 6 + B5 10 + B7 1 + B8 3——**B10 的 13 条与 B11 的 2 条路径不进路径口径**：其 DIST 首行是同路径的非缺口行，缺口只在单元口径可见，由 CASE-MIXED 行显式公布，§3.0.1）；单元分解 = tileMovement 24 units（GAP:N3-B1，14 路径，逐条清单见 §3.4）+ goldenInputs B3 21 units（6 路径）+ **B10 28 units（13 路径，第 5 轮新增——R4-2 逐单元重划后暴露：同一路径的其它案例此前被"整路径 IN_BIND"误记为已绑）** + factsAfter `index`/`seat` B11 11 units（2 路径；**第 6 轮 R5-2 按真实读点重算**：两路径共 34 实例 = wall index 已读 2（BOUND）+ m5 hand/discard seat 已读 4（OUT_DIFF）+ null 结构占位且无读点 17（UNA，不适用）+ 真缺口 11，原"26 单元"作废，§3.4）+ tileMarkState 6 units（GAP:N3-B4 的 cheat 部分，包分母 35 units = cheat 6 + round 29）+ ledger 10 units（GAP:N3-B5 = 4 子键 outcome/exception 8 叶 + snapshotRoundTrip 两布尔）+ **handler note 75 units + 六个 handler outcome/reason scoped 行 116 units = B8 共 191 units**（note 3 路径；第 4 轮曾误写"72 条路径"）+ beamSwap requestException 1 unit（GAP:N3-B7）；**75 个 handler case 分桶 = A16+B2+C6+D50+E1**（D-α46/D-β4，§3.4 逐条表），行为级黄金对照 **24/75**（A16+B2+C6=24），另有 E1（#36）1 例存在级对照（24+1=25/75 有某种形式的黄金对照，E 桶非行为绑定）；
   - **round-v1：20 条路径（16 个字段级条目）UNBOUND_HC**（roundTerminal 9 + chankanRon 11，§3.3 两表），其中事件序列面零护栏（G-028a 核心）；
   - **physical-v1：29 条路径** = GAP:N3-E2 24（boundaries[*] 11 + boundarySamples 6 + invalid[*] 3 + csharpExceptionMapping 4）+ GAP:N3-E3 5（hashCrosswalk.csharpHashVerification/v4Reason 2 + expected.suitOrder 1 + scope.targetIds 2，§3.1）；
   - **scoring-v1：6 条路径**（winSamples `expected.status`+`input.extraHan`、noWinSamples `doraIndicators/uraDoraIndicators/honba/riichiSticks`，GAP:N3-E1，§3.2）；
   - **shuffle-v1：1 条路径**（`cases[*].deadWallSegmentEqual`，GAP:N3-B2，§3.5）；**ai-v1：5 条路径**（GAP:N3-B6 3 = `$.cases[*].v5.expected.mappingBasis` + `$.v5Observation.mappingLimits`/`[*]`；GAP:N3-B9 2 = `$.cases[*].v4.events[*].text` + `$.cases[*].v4Call.action.text`，§3.6）。
   - **第 2/3 轮错误结论修正（沿用 + 第 4 轮新增）**：(a) "winSamples 95 条中 94 条被消费"作废——winSamples 组机器复算 = **96 条**：IN_BIND 32 + OUT_DIFF 50 + SEM_MAP 2 + UNBOUND 2 + META 10（第 3 轮 95 漏计 `$.winSamples` 数组容器本身，由 `$.winSamples.#` 规则以 SEM_MAP 持有）；(b) "noWinSamples 42 = IN_BIND27+SEM_MAP3+UNBOUND4+META8" 修正为 **43**：IN_BIND 27 + SEM_MAP 4 + UNBOUND 4 + META 8（第 4 条 SEM_MAP = `$.noWinSamples[*].expected` 容器，由 `expected.#` 规则持有；第 3 轮两处分母漏计容器路径，属容器归属口径而非消费语义变化，已按校验器 `DIST` 口径统一）；(c) "tileMarkState 3/3 子键行为差分"作废（R2-1b，沿用）；(d) "ledger 前 8 子键行为差分"作废（R2-1c，沿用）；(e) 第 3 轮"tileMovement 70 = IN_BIND 16 + OUT_DIFF 40 + SEM_MAP 1 + UNBOUND 11 + UNBOUND_HC 2"与"m1b 交叉绑定 11 叶"作废——逐 case 机器重划后 tileMovement 70 = **IN_BIND 16 + OUT_DIFF 18 + INTERNAL 20 + SEM_MAP 1 + UNBOUND 12 + UNBOUND_HC 2 + CONTAINER 1**（DIST 首行投影；第 6 轮按 §11.6 v4 `DIST-FX` 校准，与 §3.4 组内算术一致），m1b 交叉字段（resolvedIndex/backfilledIndex/wallPointerAfterDraw 等）实为 **INTERNAL**（fixture 内部结构自检 `TileMovement_DrawnBackfill_IsAV4OnlyPath_RecordedAsStructure` :644-666，连贯变异探针 16/16 PASS 证明其无绝对锚，§3.4 逐条表）；(f) 第 3 轮"scoring 无重叠/真实锚点已验证"的校验器声明作废——其校验器三项反例（不存在锚点通过、935 路径多重命中、322 条 filename-only 锚点）在第 4 轮全部复现并修复（§3.0.1 反例复现表 + §11.6 v2 负例矩阵）。
2. **G-028a 定性修正（含 R1-1 更正，沿用）**：C# Domain 以 `RoundSettlement/RoundResult` 字段承载 ScoreSettled 信息是合法表示映射（§6 四层链）；真实缺口 = round-v1 两个重放测试**没有对事件序列做精确断言** + `hasKanDeclared/hasRinshanDraw` 等导出值未绑定。两布尔可语义比较（exporter 以终局事件列表 `contains` 计算，与 C# `DoesNotContain` 同语义，名称映射 RinshanDraw→RinshanDrawn）；第 1 轮"不可强迫同值"表述已全部撤回（§3.3、§9 N3-A2、§13 INFO-1）。变异探针双改 true 后 6/6 PASS = **覆盖缺口**，非不可比较。
3. **mv-swap-drawn-with-wall-backfill（第 4 轮定稿）**：非 V4-only 断言撤回后的事实面 = C# 等价入口已被行为重放；m1b 交叉字段为 **INTERNAL 结构自检**（无生产差分语义，连贯变异不设防——但按 §1.4 这不是缺口类，是显式声明的结构保留）；真实缺口收窄为 §3.4 逐条表的 14 条 UNBOUND/UNBOUND_HC（N3-B1）+ 注释更正（N3-B2）+ 一处不可表示的地址级表示差（INFO-2）。无需用户规则裁决、无需刷新冻结记录（§7）。
4. **AgariYame（沿用）**：行为在 C# match 层完整存在；疑虑仅保留"round 层命令入口枚举"一点（§8）。
5. **ai-v1 / G-049（沿用 + 路径级修正）**：第 6 份 fixture 存在且 Replay/Submit/拒绝码/动作绑定/decisionClock 全绑定（8 条路径 IN_BIND 5 + OUT_DIFF 1 + SEM_MAP 1 + CONTAINER 1；正控制探针 baseMillis/reserveMillis→999 → 1 FAIL exit 1，§11.8）为真实 C# 行为，但 `DeepEquals(before,after)` 是同一 fixture 的 V4 前后树内部比对，不是"C# 重放后态 vs V4 全树"差分；**G-049 不可据此重算 full，建议 partial**（§3.6，caller 裁决）。第 4 轮修正：未绑定的 mappingBasis 实际路径是 `$.cases[*].v5.expected.mappingBasis`（第 3 轮误写为 `$.v5Observation.mappingBasis`），`mappingLimits` 2 条确在 `$.v5Observation` 下；另新增 2 条文本叶 `events[*].text`/`action.text`（GAP:N3-B9）。
6. **45/45 定向 Golden Facts 全 PASS（native exit 0）维持**——失败不在断言结果，而在断言覆盖。**累计 32 个独立变异探针**（不设防实证 23：第 2 轮 4 + 第 3 轮 3 + 第 4 轮 5 + 第 5 轮 11；设防正控制 9：第 2 轮 1 + 第 3 轮 1 + 第 4 轮 2 + 第 5 轮 5——physical tile.rank→99、sb-activate-allowed cost 绑定面、m5 hand/discard seat、sleeve 关系对单侧破坏、ai baseMillis/reserveMillis→999，均 1 FAIL exit 1，§10/§11.8）+ **3 个第 3 轮校验器反例复现**（§3.0.1）证明判级方法与校验器边界均经实证；第 5 轮探针驱动自带**逐探针期望校验 + 失败累计 + 负向自检**（变异被剥离 → 驱动自身 exit 1，§11.8）。
7. **case 总数口径（沿用）**：catalog `fixtureConcreteCases=10382` vs 本审计 census 案例口径 10353，Δ=29（round +23、cheat +6）——catalog 未声明案例分解口径，Δ29 如实上报、不采纳、不改 catalog（§5）；ai-v1 的 7 case 在 catalog 之外。
8. **一次性缺陷清单见 §13（DEF-1..DEF-5 + INFO-1..4）**。本审计按任务书**不实施修复**；修复经由 §9 补齐包由 caller 独立下发。§9 覆盖本报告所报**全部当前可比较缺口**（路径口径 101 / 单元口径 592 逐一映射到 14 个包）；确属不可机械比较的项逐条给出语义与批准责任（无沉默移出分母）。

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

### 3.0.1 层 2：字段消费语义映射规则表（FIELD-RULES，R2-1a 核心交付；第 6 轮 v4 全族逐实例版）

**目的**：把 §3.0 的"每条路径都进了报告"升级为"每条路径、每个（case，实例）单元都被判入一个语义类，且每个非排除类都有真实存在的 V4 导出行段锚点与 C# 消费（或显式缺口/排除）锚点"。第 2 轮只交了层 1；第 3 轮展开为 417 条显式规则但**校验器不消费仓库**，被 caller 三类反例击穿（不存在锚点通过、935 路径多重命中+1 标量挂 CONTAINER、322 条 filename-only 导出锚点）；第 4 轮升级 **497 条 8 列有序规则**（-Repo 锚点消费、DUP/SCALAR/CLASS 拒绝、scope presence 校验）；第 5 轮（R4-1/R4-2）升级为 540 条逐单元版：109 条 scoped 行、GAP 行 98 行、`goldenInputs` 家族按叶×案例拆为 34 行（79 个单元逐单元判类）、`factsAfter[*].index`/`[*].seat` 按案例拆分；**第 6 轮（R5-1/R5-2）升级为 542 条全族逐实例版**：新增独立实例清单工具 `CensusInstances`（§11.4b，655,145 实例）作为单元分母的枚举源，**所有家族（scoped 与 unscoped 一致）按实例判级**；`factsAfter[*].index`/`[*].seat` 的 4 条 v3 行按真实容器读点拆为 **6 条 where 谓词行**（wall index 2 实例 INTERNAL + hand/discard null index 11 实例 UNA + deadWall/replacement index 4 实例 GAP:N3-B11 + m5 hand/discard seat 4 实例 OUT_DIFF + 其余 hand/discard seat 7 实例 GAP:N3-B11 + wall|deadWall|replacement null seat 6 实例 UNA）；新增第 9 判级类 **UNA**（17 实例，显式"不适用"，§1.4）。

**机器统计（§11.6 正检 `DIST` + `CASE-DIST` 双口径，可复算）**：

| 口径 | CONTAINER | IN_BIND | INTERNAL | META | OUT_DIFF | SEM_MAP | UNBOUND | UNBOUND_HC | UNA | 合计 | GAP 合计 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 路径（DIST，first-match 首行投影） | 18 | 314 | 114 | 308 | 405 | 256 | 56 | 45 | 0 | **1516** | **101** |
| 单元（CASE-DIST，逐实例真值） | 144 | 430115 | 23088 | 42684 | 147837 | 10668 | 350 | 242 | 17 | **655145 units** | **592 units** |

- **GAP 单元级分解（CASE-DIST `gap=` 行）**：B1 24 / B2 32 / B3 21 / B4 35 / B5 10 / B6 11 / B7 1 / **B8 191（= 六 handler scoped 行 116 单元 + 三 handler note 叶 75 实例）** / B9 8 / **B10 28** / **B11 11（第 6 轮按真实读点重算，原 26 作废——见 §3.4 factsAfter 分解）** / E1 112 / E2 98 / E3 10 = **592**。路径级分解（DIST `gap=` 行）= B1 14 / B2 1 / B3 6 / B4 26 / B5 10 / B6 3 / B7 1 / B8 3 / B9 2 / E1 6 / E2 24 / E3 5 = **101**（**B10/B11 无路径级行**：其路径的 DIST 首行均为同路径非缺口行）。
- **判类混合路径 = 27 条（第 7 轮 fx 限定后真值）**，`CASE-MIXED` 行逐一公布且每行显式带 `fx=`（§11.6 v5 正检输出）：**全部 `fx=cheat-v1`**，构成 = goldenInputs 14 + factsAfter 族 7（`factsAfter`、`factsAfter[*]`、`.container`、`.index`、`.seat`、`.tileId`、`reservedTileId`）+ 六 handler outcome/reason 行 6。同一路径在同一 fixture 内于不同案例/容器分区下判类不同——这正是第 4 轮"整路径单一标签"掩盖按单元缺口的机制性原因；factsAfter `index`/`seat` 的三向混合（`INTERNAL,UNA,UNBOUND` / `OUT_DIFF,UNA,UNBOUND`）即 R5-2 分区拆分的机器呈现。【第 6 轮更正（R6-1）】v4 的 `$pathClasses` 仅以路径为键，unscoped 类写入后把**不同 JSON 文档的同名路径跨 fixture 合并**，曾报 34 条（多出的 7 条为假混合：`round-v1`/`ai-v1` 的 `$.decisionClock`、`round-v1` 的 `$.exportedAtNote`/`$.extractionStatus`/`$.generatedByCommand`/`$.generationResult.exporterTestResult`/`$.generatorVersion`/`$.unexportedSamples`——每条在其自身 fixture 内均不混合）；v5 全部混合汇总改 (fixture,path) 键，7 条见证路径在任一 fixture 下均不再混合（独立 oracle 复算 27/27 相符，§11.6 mixed-namespace-oracle）；34 只作为 R6 错误历史保留于 §15.5。
- **兄弟行换序不变性**：scoped 兄弟行的表内顺序不影响任何 unit 的判类（实测：同族行轮换后 `CASE-DIST`、逐实例 `PATH-UNITS`、fx 限定 `CASE-MIXED` 与 `CASE-TOTAL` 输出**逐字节不变**，§11.6 v5 实测）；`DIST` 口径则**有意**保留"表内文件顺序 first-match"的历史投影语义（裁决唯一但依赖行序），两口径的差异全部由 `CASE-MIXED` 行显式可见。

**规则文法与顺序语义**（TSV 8 列：`fixture` `pattern` `paths` `class` `exporterAnchor` `csAnchor` `owner` `scope`；`#` 匹配零或多层后缀且**只作末段**、`*` 匹配单层下标、其余为字面段比较——census 路径的 `[*]` 紧贴段名，**`X.#` 不匹配 `X[*]`/`X[*].y`**）：pattern 按表内顺序 **first-match-wins** 裁决 census 路径（一条路径可被多条 pattern 命中——第 3 轮"无重叠"表述已撤回，实测 935 条路径多命中——有序表保证**裁决唯一**）；`paths` 列 = 该 pattern 的 first-match 残差路径数（同 pattern 的 scoped 兄弟行重复声明同一残差，校验器按 pattern 重算强制一致）；**行间 case 归属由 `scope` 列机器校验（双向等式：scoped 并集 = 该字段实际出现 case 全集，逐实例认领唯一且恰一次，六类 SCOPE 错误见 §1.3）**；9 个判级类与锚点文法见 §1.4。

**块内嵌如下（成对标记行之间的原始 TSV；校验器 `-ReportPath` 模式直接从本报告读取该块复算，§11.6）**：

FIELD-RULES:BEGIN
```text
﻿fixture	pattern	paths	class	exporterAnchor	csAnchor	owner	scope
physical-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META	*
physical-v1	$.clockUsed	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:41	BOUND	*
physical-v1	$.exporterSourceSha256	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:49	BOUND	*
physical-v1	$.extractionStatus	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:40	BOUND	*
physical-v1	$.generatedByCommand	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.generatorVersion	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:38	BOUND	*
physical-v1	$.gitStatusPorcelain	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.gitStatusPorcelain[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.generationResult.#	7	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:64	BOUND	*
physical-v1	$.hashCrosswalk	1	CONTAINER	meta:container-exporter	meta:container-node	META	*
physical-v1	$.hashCrosswalk.v4CanonicalStateHash	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:95	BOUND	*
physical-v1	$.hashCrosswalk.csharpHash	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:96	BOUND	*
physical-v1	$.hashCrosswalk.wallCanonicalDigestAlgorithm	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:97	BOUND	*
physical-v1	$.hashCrosswalk.csharpHashVerification	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:39-668	GAP:N3-E3	GAP:N3-E3	*
physical-v1	$.hashCrosswalk.v4Reason	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:39-668	GAP:N3-E3	GAP:N3-E3	*
physical-v1	$.randomInputsNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.rulesetVersion	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:39	BOUND	*
physical-v1	$.schema	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:37	BOUND	*
physical-v1	$.sourceHashAlgorithm	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:51	BOUND	*
physical-v1	$.taskPaths	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.taskPaths[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.trackedModifiedFiles	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.trackedProductionModifiedFiles	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:47	BOUND	*
physical-v1	$.v4Commit	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:43	BOUND	*
physical-v1	$.v4ProductionTreeClean	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:46	BOUND	*
physical-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.v4SourceHashes[*].#	3	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:58	BOUND	*
physical-v1	$.v4SourceHashes.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:58	BOUND	*
physical-v1	$.worktreeDirty	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
physical-v1	$.scope	1	CONTAINER	meta:container-exporter	meta:container-node	META	*
physical-v1	$.scope.manifestEntriesTargeted	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:73	BOUND	*
physical-v1	$.scope.manifestEntriesExportedByThisRun	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:74	BOUND	*
physical-v1	$.scope.manifestEntriesNotFullAudit	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:75	BOUND	*
physical-v1	$.scope.fullManifestAudited	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:76	BOUND	*
physical-v1	$.scope.note	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:77	BOUND	*
physical-v1	$.scope.targetIds	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:39-668	GAP:N3-E3	GAP:N3-E3	*
physical-v1	$.scope.targetIds[*]	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:39-668	GAP:N3-E3	GAP:N3-E3	*
physical-v1	$.samples.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:39-668	PhysicalGoldenTests.cs:80	BOUND	*
physical-v1	$.samples[*].command	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:89	BOUND	*
physical-v1	$.samples[*].extractionStatus	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:86	BOUND	*
physical-v1	$.samples[*].manifestStatusBefore	1	META	meta:exporter-run-bookkeeping	meta:exporter-run-bookkeeping	META	*
physical-v1	$.samples[*].source.#	5	META	meta:exporter-run-bookkeeping	meta:exporter-run-bookkeeping	META	*
physical-v1	$.samples[*].input.clockMillis	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:692-702	PhysicalGoldenTests.cs:87	BOUND	*
physical-v1	$.samples[*].input.sessionId	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:692-702	PhysicalGoldenTests.cs:88	BOUND	*
physical-v1	$.samples[*].input.randomInputs	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:692-702	PhysicalGoldenTests.cs:324	BOUND	*
physical-v1	$.samples[*].input.#	13	META	meta:exporter-run-bookkeeping	meta:exporter-run-bookkeeping	META	*
physical-v1	$.samples[*].expected.tiles[*].#	12	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:703-716	PhysicalGoldenTests.cs:110	BOUND	*
physical-v1	$.samples[*].expected.groups[*].#	7	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:171	BOUND	*
physical-v1	$.samples[*].expected.boundarySamples[*].isHonor	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.boundarySamples[*].isNumberTile	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.boundarySamples[*].isRedFive	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.boundarySamples[*].isSimple	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.boundarySamples[*].isTerminal	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.boundarySamples[*].isTerminalOrHonor	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.boundarySamples[*].#	6	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:157	BOUND	*
physical-v1	$.samples[*].expected.boundaries[*].tileId	1	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:224	BOUND	*
physical-v1	$.samples[*].expected.boundaries[*].#	11	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.validRoundTrips[*].#	6	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:201	BOUND	*
physical-v1	$.samples[*].expected.invalid[*].input	1	IN_BIND	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:236	BOUND	*
physical-v1	$.samples[*].expected.invalid[*].exceptionType	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:233	BOUND	*
physical-v1	$.samples[*].expected.invalid[*].semanticCategory	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:234	BOUND	*
physical-v1	$.samples[*].expected.invalid[*].#	3	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.csharpExceptionMapping.#	4	UNBOUND_HC	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E2	GAP:N3-E2	*
physical-v1	$.samples[*].expected.duplicateKeyStaticLimitation.#	11	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:511	BOUND	*
physical-v1	$.samples[*].expected.cases[*].#	31	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:243	BOUND	*
physical-v1	$.samples[*].expected.probeA.firstAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:409	BOUND	*
physical-v1	$.samples[*].expected.probeA.secondAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:420	BOUND	*
physical-v1	$.samples[*].expected.probeA.after.#	6	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:415	BOUND	*
physical-v1	$.samples[*].expected.probeA.mutation.mapSizeAfter	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:414	BOUND	*
physical-v1	$.samples[*].expected.probeA.mutation.#	4	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeA.autoRepairObserved	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:419	BOUND	*
physical-v1	$.samples[*].expected.probeA.before.#	6	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeA.target.#	12	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeA.fixture	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeA.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:406	BOUND	*
physical-v1	$.samples[*].expected.probeB.before.auditOutcome	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:469	BOUND	*
physical-v1	$.samples[*].expected.probeB.before.#	9	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeB.firstAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:470	BOUND	*
physical-v1	$.samples[*].expected.probeB.secondAudit.#	5	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:483	BOUND	*
physical-v1	$.samples[*].expected.probeB.after.actualTiles[*]	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeB.after.actualTiles.#	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeB.after.#	7	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:474	BOUND	*
physical-v1	$.samples[*].expected.probeB.csharpMapping.#	3	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:488	BOUND	*
physical-v1	$.samples[*].expected.probeB.autoRepairObserved	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:482	BOUND	*
physical-v1	$.samples[*].expected.probeB.target.#	12	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeB.fixture	1	META	meta:v4-probe-setup	meta:v4-probe-setup	META	*
physical-v1	$.samples[*].expected.probeB.#	1	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:466	BOUND	*
physical-v1	$.samples[*].expected.suitOrder	1	UNBOUND	V5PhysicalGoldenExporterTest.kt:669-691	GAP:N3-E3	GAP:N3-E3	*
physical-v1	$.samples[*].expected.#	32	OUT_DIFF	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:101	BOUND	*
physical-v1	$.samples[*].#	2	SEM_MAP	V5PhysicalGoldenExporterTest.kt:669-691	PhysicalGoldenTests.cs:83	BOUND	*
scoring-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META	*
scoring-v1	$.schema	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:34-138	ScoringGoldenTests.cs:25	BOUND	*
scoring-v1	$.rulesetVersion	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:34-138	ScoringGoldenTests.cs:26	BOUND	*
scoring-v1	$.v4Commit	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:34-138	ScoringGoldenTests.cs:29	BOUND	*
scoring-v1	$.extractionStatus	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:34-138	ScoringGoldenTests.cs:39	BOUND	*
scoring-v1	$.generatedByCommand	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.exportedAtNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.generatorVersion	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.unexportedSamples	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.generationResult.#	10	SEM_MAP	V5ScoringGoldenExporterTest.kt:34-138	ScoringGoldenTests.cs:41	BOUND	*
scoring-v1	$.winSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:139-443	ScoringGoldenTests.cs:33	BOUND	*
scoring-v1	$.winSamples[*].expected.status	1	UNBOUND	V5ScoringGoldenExporterTest.kt:888-929	GAP:N3-E1	GAP:N3-E1	*
scoring-v1	$.winSamples[*].expected.#	50	OUT_DIFF	V5ScoringGoldenExporterTest.kt:888-929	ScoringGoldenTests.cs:207	BOUND	*
scoring-v1	$.winSamples[*].input.extraHan	1	UNBOUND	V5ScoringGoldenExporterTest.kt:139-443	GAP:N3-E1	GAP:N3-E1	*
scoring-v1	$.winSamples[*].input.#	32	IN_BIND	V5ScoringGoldenExporterTest.kt:139-443	ScoringGoldenTests.cs:207	BOUND	*
scoring-v1	$.winSamples[*].tags.#	1	META	meta:authoring-labels	meta:authoring-labels	META	*
scoring-v1	$.winSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.winSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:139-443	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.winSamples[*].#	5	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
scoring-v1	$.noWinSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:444-504	ScoringGoldenTests.cs:38	BOUND	*
scoring-v1	$.noWinSamples[*].input.doraIndicators	1	UNBOUND	V5ScoringGoldenExporterTest.kt:444-504	GAP:N3-E1	GAP:N3-E1	*
scoring-v1	$.noWinSamples[*].input.uraDoraIndicators	1	UNBOUND	V5ScoringGoldenExporterTest.kt:444-504	GAP:N3-E1	GAP:N3-E1	*
scoring-v1	$.noWinSamples[*].input.honba	1	UNBOUND	V5ScoringGoldenExporterTest.kt:444-504	GAP:N3-E1	GAP:N3-E1	*
scoring-v1	$.noWinSamples[*].input.riichiSticks	1	UNBOUND	V5ScoringGoldenExporterTest.kt:444-504	GAP:N3-E1	GAP:N3-E1	*
scoring-v1	$.noWinSamples[*].input.#	27	IN_BIND	V5ScoringGoldenExporterTest.kt:444-504	ScoringGoldenTests.cs:244	BOUND	*
scoring-v1	$.noWinSamples[*].expected.status	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:930-943	ScoringGoldenTests.cs:247	BOUND	*
scoring-v1	$.noWinSamples[*].expected.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:930-943	ScoringGoldenTests.cs:247	BOUND	*
scoring-v1	$.noWinSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.noWinSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:444-504	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.noWinSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
scoring-v1	$.randomSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:742-887	ScoringGoldenTests.cs:32	BOUND	*
scoring-v1	$.randomSamples[*].expected.#	14	OUT_DIFF	V5ScoringGoldenExporterTest.kt:1076-1116	ScoringGoldenTests.cs:372	BOUND	*
scoring-v1	$.randomSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.randomSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:742-887	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.randomSamples[*].#	32	IN_BIND	V5ScoringGoldenExporterTest.kt:742-887	ScoringGoldenTests.cs:372	BOUND	*
scoring-v1	$.randomMeta.deterministicNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.randomMeta.#	42	SEM_MAP	V5ScoringGoldenExporterTest.kt:1117-1163	ScoringGoldenTests.cs:105	BOUND	*
scoring-v1	$.scoreSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:715-741	ScoringGoldenTests.cs:34	BOUND	*
scoring-v1	$.scoreSamples[*].expected.#	46	OUT_DIFF	V5ScoringGoldenExporterTest.kt:1040-1075	ScoringGoldenTests.cs:259	BOUND	*
scoring-v1	$.scoreSamples[*].input.#	4	IN_BIND	V5ScoringGoldenExporterTest.kt:715-741	ScoringGoldenTests.cs:259	BOUND	*
scoring-v1	$.scoreSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.scoreSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:715-741	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.scoreSamples[*].#	3	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
scoring-v1	$.fuSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:505-627	ScoringGoldenTests.cs:35	BOUND	*
scoring-v1	$.fuSamples[*].expected.#	9	OUT_DIFF	V5ScoringGoldenExporterTest.kt:944-981	ScoringGoldenTests.cs:289	BOUND	*
scoring-v1	$.fuSamples[*].input.#	21	IN_BIND	V5ScoringGoldenExporterTest.kt:505-627	ScoringGoldenTests.cs:289	BOUND	*
scoring-v1	$.fuSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.fuSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:505-627	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.fuSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
scoring-v1	$.doraSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:628-679	ScoringGoldenTests.cs:36	BOUND	*
scoring-v1	$.doraSamples[*].expected.#	11	OUT_DIFF	V5ScoringGoldenExporterTest.kt:982-1000	ScoringGoldenTests.cs:310	BOUND	*
scoring-v1	$.doraSamples[*].input.#	15	IN_BIND	V5ScoringGoldenExporterTest.kt:628-679	ScoringGoldenTests.cs:310	BOUND	*
scoring-v1	$.doraSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.doraSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:628-679	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.doraSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
scoring-v1	$.settlementSamples.#	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:680-714	ScoringGoldenTests.cs:37	BOUND	*
scoring-v1	$.settlementSamples[*].expected.#	6	OUT_DIFF	V5ScoringGoldenExporterTest.kt:1001-1039	ScoringGoldenTests.cs:326	BOUND	*
scoring-v1	$.settlementSamples[*].input.#	13	IN_BIND	V5ScoringGoldenExporterTest.kt:680-714	ScoringGoldenTests.cs:326	BOUND	*
scoring-v1	$.settlementSamples[*].source.#	4	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
scoring-v1	$.settlementSamples[*].id	1	SEM_MAP	V5ScoringGoldenExporterTest.kt:680-714	ScoringGoldenTests.cs:199	BOUND	*
scoring-v1	$.settlementSamples[*].#	4	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
round-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META	*
round-v1	$.schema	1	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:80	BOUND	*
round-v1	$.extractionStatus	1	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:81	BOUND	*
round-v1	$.rulesetVersion	1	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:82	BOUND	*
round-v1	$.v4Commit	1	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:83	BOUND	*
round-v1	$.generatedByCommand	1	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:84	BOUND	*
round-v1	$.exportedAtNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.reproducibilityNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.v4DotnetRulesCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.v4EngineSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.workingTreeDirty	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.unexportedSamples[*]	1	META	meta:list-items-not-consumed	meta:list-items-not-consumed	META	*
round-v1	$.unexportedSamples.#	1	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:85	BOUND	*
round-v1	$.generationResult.controllerEpochSamples	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.generationResult.exporterTestResult	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
round-v1	$.generationResult.#	4	SEM_MAP	V5RoundGoldenExportTest.kt:55-114	RoundReplayGoldenTests.cs:86	BOUND	*
round-v1	$.roundTerminal.sampleId	1	SEM_MAP	V5RoundGoldenExportTest.kt:115-190	RoundReplayGoldenTests.cs:90	BOUND	*
round-v1	$.roundTerminal.status	1	SEM_MAP	V5RoundGoldenExportTest.kt:115-190	RoundReplayGoldenTests.cs:92	BOUND	*
round-v1	$.roundTerminal.expected.eventTypes[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.expected.eventTypes.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.expected.phaseAfter	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.expected.resultOutcome.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.expected.#	9	OUT_DIFF	V5RoundGoldenExportTest.kt:115-190	RoundReplayGoldenTests.cs:101	BOUND	*
round-v1	$.roundTerminal.input.phaseBefore	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.input.versionBefore	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.input.#	6	IN_BIND	V5RoundGoldenExportTest.kt:115-190	RoundReplayGoldenTests.cs:110	BOUND	*
round-v1	$.roundTerminal.settlement.outcome	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.settlement.totalDeltas[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.settlement.totalDeltas.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:115-190	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.roundTerminal.settlement.#	8	IN_BIND	V5RoundGoldenExportTest.kt:115-190	RoundReplayGoldenTests.cs:115	BOUND	*
round-v1	$.roundTerminal.#	6	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
round-v1	$.chankanRon.sampleId	1	SEM_MAP	V5RoundGoldenExportTest.kt:191-291	RoundReplayGoldenTests.cs:91	BOUND	*
round-v1	$.chankanRon.status	1	SEM_MAP	V5RoundGoldenExportTest.kt:191-291	RoundReplayGoldenTests.cs:93	BOUND	*
round-v1	$.chankanRon.expected.eventTypes[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.eventTypes.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.hasKanDeclared	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.hasRinshanDraw	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.legalRonSeat1	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.meldTypeAfterKan	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.phaseAfterKan	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.phaseAfterRon	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.resultOutcome.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.expected.#	13	OUT_DIFF	V5RoundGoldenExportTest.kt:191-291	RoundReplayGoldenTests.cs:151	BOUND	*
round-v1	$.chankanRon.input.meldTiles[*]	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.input.meldTiles.#	1	UNBOUND_HC	V5RoundGoldenExportTest.kt:191-291	GAP:N3-B4	GAP:N3-B4	*
round-v1	$.chankanRon.input.#	7	IN_BIND	V5RoundGoldenExportTest.kt:191-291	RoundReplayGoldenTests.cs:158	BOUND	*
round-v1	$.chankanRon.#	6	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
round-v1	$.controlEpoch.operations[*].epochs[*]	1	OUT_DIFF	V5RoundGoldenExportTest.kt:636-688	RoundReplayGoldenTests.cs:429	BOUND	*
round-v1	$.controlEpoch.operations[*].epochs.#	1	OUT_DIFF	V5RoundGoldenExportTest.kt:636-688	RoundReplayGoldenTests.cs:429	BOUND	*
round-v1	$.controlEpoch.operations[*].kinds[*]	1	OUT_DIFF	V5RoundGoldenExportTest.kt:636-688	RoundReplayGoldenTests.cs:432	BOUND	*
round-v1	$.controlEpoch.operations[*].kinds.#	1	OUT_DIFF	V5RoundGoldenExportTest.kt:636-688	RoundReplayGoldenTests.cs:432	BOUND	*
round-v1	$.controlEpoch.operations[*].result	1	OUT_DIFF	V5RoundGoldenExportTest.kt:636-688	RoundReplayGoldenTests.cs:420	BOUND	*
round-v1	$.controlEpoch.operations[*].#	5	IN_BIND	V5RoundGoldenExportTest.kt:636-688	RoundReplayGoldenTests.cs:391	BOUND	*
round-v1	$.controlEpoch.#	9	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
round-v1	$.decisionClock[*].operations[*].deadline	1	OUT_DIFF	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:339	BOUND	*
round-v1	$.decisionClock[*].operations[*].reserveAtOpen	1	OUT_DIFF	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:340	BOUND	*
round-v1	$.decisionClock[*].operations[*].generation	1	OUT_DIFF	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:341	BOUND	*
round-v1	$.decisionClock[*].operations[*].result	1	OUT_DIFF	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:347	BOUND	*
round-v1	$.decisionClock[*].operations[*].count	1	OUT_DIFF	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:351	BOUND	*
round-v1	$.decisionClock[*].operations[*].value	1	OUT_DIFF	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:374	BOUND	*
round-v1	$.decisionClock[*].operations[*].#	7	IN_BIND	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:311	BOUND	*
round-v1	$.decisionClock[*].config.#	4	IN_BIND	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:320	BOUND	*
round-v1	$.decisionClock[*].status	1	SEM_MAP	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:96	BOUND	*
round-v1	$.decisionClock[*].#	8	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
round-v1	$.decisionClock.#	1	SEM_MAP	V5RoundGoldenExportTest.kt:368-635	RoundReplayGoldenTests.cs:94	BOUND	*
round-v1	$.matchProgression[*].expected.type	1	SEM_MAP	V5RoundGoldenExportTest.kt:292-367	RoundReplayGoldenTests.cs:273	BOUND	*
round-v1	$.matchProgression[*].expected.#	15	OUT_DIFF	V5RoundGoldenExportTest.kt:292-367	RoundReplayGoldenTests.cs:236	BOUND	*
round-v1	$.matchProgression[*].config.#	14	IN_BIND	V5RoundGoldenExportTest.kt:292-367	RoundReplayGoldenTests.cs:245	BOUND	*
round-v1	$.matchProgression[*].input.#	16	IN_BIND	V5RoundGoldenExportTest.kt:292-367	RoundReplayGoldenTests.cs:259	BOUND	*
round-v1	$.matchProgression[*].#	8	META	meta:sample-bookkeeping	meta:sample-bookkeeping	META	*
round-v1	$.matchProgression.#	1	SEM_MAP	V5RoundGoldenExportTest.kt:292-367	RoundReplayGoldenTests.cs:87	BOUND	*
cheat-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META	*
cheat-v1	$.schema	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:138	BOUND	*
cheat-v1	$.extractionStatus	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:139	BOUND	*
cheat-v1	$.v4Commit	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:141	BOUND	*
cheat-v1	$.generatorVersion	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:145	BOUND	*
cheat-v1	$.rulesetVersion	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:146	BOUND	*
cheat-v1	$.generatedByCommand	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:147	BOUND	*
cheat-v1	$.exportedAtNote	1	SEM_MAP	V5CheatGoldenExporterTest.kt:68-110	CheatGoldenTests.cs:148	BOUND	*
cheat-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
cheat-v1	$.slotConventionNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
cheat-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
cheat-v1	$.ledger	1	SEM_MAP	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:151	BOUND	*
cheat-v1	$.ledger.initialPoints	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:169	BOUND	*
cheat-v1	$.ledger.initialPoints[*]	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:169	BOUND	*
cheat-v1	$.ledger.initialPoints.#	0	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:169	BOUND	*
cheat-v1	$.ledger.chargeOnce.entryId	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:175	BOUND	*
cheat-v1	$.ledger.chargeOnce.seat	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:175	BOUND	*
cheat-v1	$.ledger.chargeOnce.amount	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:175	BOUND	*
cheat-v1	$.ledger.chargeOnce.reason	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:175	BOUND	*
cheat-v1	$.ledger.chargeOnce.balanceAfter	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:178	BOUND	*
cheat-v1	$.ledger.chargeOnce.balancesAfter[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:179	BOUND	*
cheat-v1	$.ledger.chargeOnce.balancesAfter.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:179	BOUND	*
cheat-v1	$.ledger.chargeOnce.entryCount	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:185	BOUND	*
cheat-v1	$.ledger.chargeOnce.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:170	BOUND	*
cheat-v1	$.ledger.replaySamePayload.returnsOriginalEntry	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:191	BOUND	*
cheat-v1	$.ledger.replaySamePayload.balanceAfter	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:192	BOUND	*
cheat-v1	$.ledger.replaySamePayload.entryCount	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:193	BOUND	*
cheat-v1	$.ledger.replaySamePayload.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:188	BOUND	*
cheat-v1	$.ledger.conflictDifferentPayload.requested.#	3	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:198	BOUND	*
cheat-v1	$.ledger.conflictDifferentPayload.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.conflictDifferentPayload.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.conflictDifferentPayload.balancesAfter[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:205	BOUND	*
cheat-v1	$.ledger.conflictDifferentPayload.balancesAfter.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:205	BOUND	*
cheat-v1	$.ledger.conflictDifferentPayload.entryCount	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:207	BOUND	*
cheat-v1	$.ledger.conflictDifferentPayload.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:196	BOUND	*
cheat-v1	$.ledger.insufficientFunds.initialPoints	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:211	BOUND	*
cheat-v1	$.ledger.insufficientFunds.amount	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:212	BOUND	*
cheat-v1	$.ledger.insufficientFunds.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.insufficientFunds.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.insufficientFunds.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:210	BOUND	*
cheat-v1	$.ledger.overflow.amount	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:217	BOUND	*
cheat-v1	$.ledger.overflow.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.overflow.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.overflow.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:216	BOUND	*
cheat-v1	$.ledger.invalidSeat.seat	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:222	BOUND	*
cheat-v1	$.ledger.invalidSeat.outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.invalidSeat.exception	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	GAP:N3-B5	GAP:N3-B5	*
cheat-v1	$.ledger.invalidSeat.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:221	BOUND	*
cheat-v1	$.ledger.flowSummary.spend.#	3	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:235	BOUND	*
cheat-v1	$.ledger.flowSummary.gain.#	3	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:236	BOUND	*
cheat-v1	$.ledger.flowSummary.seat0TotalSpent	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:243	BOUND	*
cheat-v1	$.ledger.flowSummary.seat1TotalGained	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:244	BOUND	*
cheat-v1	$.ledger.flowSummary.seat1NetDelta	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:245	BOUND	*
cheat-v1	$.ledger.flowSummary.finalBalances[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:253	BOUND	*
cheat-v1	$.ledger.flowSummary.finalBalances.#	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:253	BOUND	*
cheat-v1	$.ledger.flowSummary.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:233	BOUND	*
cheat-v1	$.ledger.snapshotRoundTrip	1	CONTAINER	meta:container-exporter	meta:container-node	META	*
cheat-v1	$.ledger.snapshotRoundTrip.balancesEqual	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:261	GAP:N3-B5	*
cheat-v1	$.ledger.snapshotRoundTrip.entriesEqual	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:111-192	CheatGoldenTests.cs:262	GAP:N3-B5	*
cheat-v1	$.tileMarkState	1	SEM_MAP	V5CheatGoldenExporterTest.kt:193-266	CheatGoldenTests.cs:277	BOUND	*
cheat-v1	$.tileMarkState.registerDealtRound.versionAfterFirstRegister	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:193-266	GAP:N3-B4	GAP:N3-B4	*
cheat-v1	$.tileMarkState.registerDealtRound.#	3	OUT_DIFF	V5CheatGoldenExporterTest.kt:193-266	CheatGoldenTests.cs:280	BOUND	*
cheat-v1	$.tileMarkState.addGroup.sameIdDifferentContentException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:193-266	GAP:N3-B4	GAP:N3-B4	*
cheat-v1	$.tileMarkState.addGroup.seatGroupLimitIsThree	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:193-266	GAP:N3-B4	GAP:N3-B4	*
cheat-v1	$.tileMarkState.addGroup.seatGroupLimitException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:193-266	GAP:N3-B4	GAP:N3-B4	*
cheat-v1	$.tileMarkState.addGroup.seatTileLimitIsTwelve	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:193-266	GAP:N3-B4	GAP:N3-B4	*
cheat-v1	$.tileMarkState.addGroup.#	7	OUT_DIFF	V5CheatGoldenExporterTest.kt:193-266	CheatGoldenTests.cs:288	BOUND	*
cheat-v1	$.tileMarkState.expiry.createdRoundOrdinal	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:193-266	GAP:N3-B4	GAP:N3-B4	*
cheat-v1	$.tileMarkState.expiry.#	7	OUT_DIFF	V5CheatGoldenExporterTest.kt:193-266	CheatGoldenTests.cs:298	BOUND	*
cheat-v1	$.tileMarkHandler[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:118	BOUND	*
cheat-v1	$.tileMarkHandler[*].outcome	1	SEM_MAP	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:552-563	BOUND	case=tm-not-your-turn|tm-riichi-locked|tm-invalid-selection-three|tm-tile-not-owned|tm-insufficient-funds|tm-already-marked
cheat-v1	$.tileMarkHandler[*].outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:351-375	GAP:N3-B8	case=tm-allowed|tm-wrong-phase-preparation|tm-wrong-phase-draw|tm-wrong-phase-round-end|tm-response-window|tm-invalid-selection-duplicate|tm-capacity-groups|tm-capacity-tiles|tm-sleeve-pending|tm-offturn-sleeve-pending|tm-beam-pending|tm-no-active-decision|tm-deadline-expired|tm-decision-mismatch
cheat-v1	$.tileMarkHandler[*].reason	1	SEM_MAP	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:552-563	BOUND	case=tm-not-your-turn|tm-riichi-locked|tm-invalid-selection-three|tm-tile-not-owned|tm-insufficient-funds|tm-already-marked
cheat-v1	$.tileMarkHandler[*].reason	1	IN_BIND	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:413-418	BOUND	case=tm-response-window
cheat-v1	$.tileMarkHandler[*].reason	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:358-359	GAP:N3-B8	case=tm-allowed|tm-wrong-phase-preparation|tm-wrong-phase-draw|tm-wrong-phase-round-end|tm-invalid-selection-duplicate|tm-capacity-groups|tm-capacity-tiles|tm-sleeve-pending|tm-offturn-sleeve-pending|tm-beam-pending|tm-no-active-decision|tm-deadline-expired|tm-decision-mismatch
cheat-v1	$.tileMarkHandler[*].note	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:350	GAP:N3-B8	*
cheat-v1	$.tileMarkHandler[*].#	1	CONTAINER	meta:case-array-elements	meta:case-array-elements	META	*
cheat-v1	$.tileMarkHandler.#	1	SEM_MAP	V5CheatGoldenExporterTest.kt:359-428	CheatGoldenTests.cs:153	BOUND	*
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.seat	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:819	BOUND	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.currentPlayer	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:820	BOUND	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.phase	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:821	BOUND	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.keep	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:723	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.keep	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:773	BOUND	case=sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.keep	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:823	BOUND	case=sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.drawKind	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:722	BOUND	case=sb-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.drawKind	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:772	BOUND	case=sb-activate-rinshan-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cheatPoints	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:822	BOUND	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cost	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:822	BOUND	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerFirstInWall	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:825	BOUND	case=sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerSecondInWall	1	SEM_MAP	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:826	BOUND	case=sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cheatPoints	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:737	BOUND	case=sb-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cheatPoints	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:787	BOUND	case=sb-activate-rinshan-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cost	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:737	BOUND	case=sb-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cost	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:787	BOUND	case=sb-activate-rinshan-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.drawnTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:774	BOUND	case=sb-activate-rinshan-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.drawnTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:725	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.nextWallTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:727	BOUND	case=sb-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.nextDeadWallTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:775	BOUND	case=sb-activate-rinshan-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.turnNumber	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:724	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerCreatedTurnNumber	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:724	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerFirstTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:725	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerFirstTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:774	BOUND	case=sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerSecondTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:727	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerSecondTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:775	BOUND	case=sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerSecondWallIndex	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:726	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.nextAvailableWallIndex	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:726	BOUND	case=sb-choose-second
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.now	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:824	BOUND	case=sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.deadlineMillis	1	INTERNAL	V5CheatGoldenExporterTest.kt:308-357	CheatGoldenTests.cs:824	BOUND	case=sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.availableWallCount	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B3	GAP:N3-B3	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B3	GAP:N3-B3	case=sb-choose-first|sb-choose-second|sb-choose-rinshan|sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.rinshanPointer	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B3	GAP:N3-B3	case=sb-activate-allowed|sb-activate-rinshan-allowed|sb-choose-first|sb-choose-second|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.wallEndExclusive	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B3	GAP:N3-B3	case=sb-activate-allowed|sb-activate-rinshan-allowed|sb-choose-first|sb-choose-second|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.wallPointer	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B3	GAP:N3-B3	case=sb-activate-allowed|sb-activate-rinshan-allowed|sb-choose-first|sb-choose-second|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.windowId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B3	GAP:N3-B3	case=sbo-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.seat	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-activate-allowed|sb-activate-rinshan-allowed|sb-choose-first|sb-choose-second|sb-choose-rinshan|sbo-choose-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.keep	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.drawKind	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first|sb-choose-second|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.drawnTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-activate-allowed|sb-choose-first|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.nextWallTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-activate-rinshan-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.nextDeadWallTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-activate-allowed
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.cost	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first|sb-choose-second|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.turnNumber	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerCreatedTurnNumber	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerFirstTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerSecondTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.offerSecondWallIndex	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.nextAvailableWallIndex	1	UNBOUND	V5CheatGoldenExporterTest.kt:308-357	GAP:N3-B10	GAP:N3-B10	case=sb-choose-first|sb-choose-rinshan
cheat-v1	$.sleeveBloomHandler[*].goldenInputs.#	1	CONTAINER	meta:container-exporter	meta:container-node	META	*
cheat-v1	$.sleeveBloomHandler[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:118	BOUND	*
cheat-v1	$.sleeveBloomHandler[*].outcome	1	SEM_MAP	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:552-575	BOUND	case=sb-activate-insufficient|sb-activate-disabled|sb-choose-stale-turn|sb-activate-already-used|sbo-activate-no-window
cheat-v1	$.sleeveBloomHandler[*].outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:351-375	GAP:N3-B8	case=sb-activate-allowed|sb-activate-dealer-extra|sb-activate-beam-pending|sb-activate-sleeve-pending|sb-activate-wall-empty|sb-activate-wall-empty-drift|sb-activate-no-drawn-tile|sb-activate-not-your-turn|sb-activate-wrong-phase|sb-activate-response-window|sb-activate-rinshan-allowed|sb-activate-rinshan-empty|sb-choose-first|sb-choose-second|sb-choose-stale-drawn|sb-choose-stale-index|sb-choose-offer-mismatch|sb-choose-no-offer|sb-choose-rinshan|sbo-activate-allowed|sbo-activate-reserved|sbo-activate-own-turn|sbo-activate-wall-empty|sbo-choose-allowed|sbo-choose-expired|sbo-choose-stale-first|sbo-choose-stale-second
cheat-v1	$.sleeveBloomHandler[*].reason	1	SEM_MAP	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:552-575	BOUND	case=sb-activate-insufficient|sb-activate-disabled|sb-choose-stale-turn|sb-activate-already-used|sbo-activate-no-window
cheat-v1	$.sleeveBloomHandler[*].reason	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:358-359	GAP:N3-B8	case=sb-activate-allowed|sb-activate-dealer-extra|sb-activate-beam-pending|sb-activate-sleeve-pending|sb-activate-wall-empty|sb-activate-wall-empty-drift|sb-activate-no-drawn-tile|sb-activate-not-your-turn|sb-activate-wrong-phase|sb-activate-response-window|sb-activate-rinshan-allowed|sb-activate-rinshan-empty|sb-choose-first|sb-choose-second|sb-choose-stale-drawn|sb-choose-stale-index|sb-choose-offer-mismatch|sb-choose-no-offer|sb-choose-rinshan|sbo-activate-allowed|sbo-activate-reserved|sbo-activate-own-turn|sbo-activate-wall-empty|sbo-choose-allowed|sbo-choose-expired|sbo-choose-stale-first|sbo-choose-stale-second
cheat-v1	$.sleeveBloomHandler[*].note	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:350	GAP:N3-B8	*
cheat-v1	$.sleeveBloomHandler[*].#	1	CONTAINER	meta:case-array-elements	meta:case-array-elements	META	*
cheat-v1	$.sleeveBloomHandler.#	1	SEM_MAP	V5CheatGoldenExporterTest.kt:432-766	CheatGoldenTests.cs:154	BOUND	*
cheat-v1	$.beamSwapHandler[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:118	BOUND	*
cheat-v1	$.beamSwapHandler[*].outcome	1	SEM_MAP	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:540-575	BOUND	case=bm-activate-insufficient|bm-activate-disabled|bm-choose-stale-turn|bm-choose-invalid-foreign-hand|bm-activate-already-used
cheat-v1	$.beamSwapHandler[*].outcome	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:543-544	BOUND	case=bm-choose-keep
cheat-v1	$.beamSwapHandler[*].outcome	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:351-375	GAP:N3-B8	case=bm-activate-allowed|bm-activate-no-candidates|bm-activate-dealer-extra|bm-activate-riichi|bm-activate-no-drawn|bm-activate-beam-pending|bm-activate-sleeve-pending|bm-activate-deadwall-allowed|bm-activate-deadwall-empty|bm-activate-deadwall-dora-only|bm-choose-swap|bm-choose-invalid-slot|bm-keep-original-args-forbidden-at-request|bm-choose-stale-drawn|bm-choose-deadwall-allowed|bm-choose-deadwall-dora-stale|bm-choose-replacement-allowed
cheat-v1	$.beamSwapHandler[*].reason	1	SEM_MAP	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:552-563	BOUND	case=bm-activate-insufficient|bm-activate-disabled|bm-choose-stale-turn|bm-choose-invalid-foreign-hand|bm-activate-already-used
cheat-v1	$.beamSwapHandler[*].reason	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:358-359	GAP:N3-B8	case=bm-activate-allowed|bm-activate-no-candidates|bm-activate-dealer-extra|bm-activate-riichi|bm-activate-no-drawn|bm-activate-beam-pending|bm-activate-sleeve-pending|bm-activate-deadwall-allowed|bm-activate-deadwall-empty|bm-activate-deadwall-dora-only|bm-choose-swap|bm-choose-keep|bm-choose-invalid-slot|bm-keep-original-args-forbidden-at-request|bm-choose-stale-drawn|bm-choose-deadwall-allowed|bm-choose-deadwall-dora-stale|bm-choose-replacement-allowed
cheat-v1	$.beamSwapHandler[*].note	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:283-299	CheatGoldenTests.cs:350	GAP:N3-B8	*
cheat-v1	$.beamSwapHandler[*].requestException	1	UNBOUND	V5CheatGoldenExporterTest.kt:767-978	GAP:N3-B7	GAP:N3-B7	*
cheat-v1	$.beamSwapHandler[*]	1	CONTAINER	meta:container-exporter	meta:case-array-elements	META	*
cheat-v1	$.beamSwapHandler.#	1	SEM_MAP	V5CheatGoldenExporterTest.kt:767-978	CheatGoldenTests.cs:155	BOUND	*
cheat-v1	$.tileMovement	1	SEM_MAP	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:583	BOUND	*
cheat-v1	$.tileMovement[*].factsBefore[*].#	5	UNBOUND	V5CheatGoldenExporterTest.kt:1011-1042	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].factsBefore.#	1	UNBOUND	V5CheatGoldenExporterTest.kt:1011-1042	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].deliveredTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].doraRefusalException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].wallSlotRefusalException	1	UNBOUND_HC	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].inputLastDiscardSeat	1	UNBOUND	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].note	1	UNBOUND	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].rinshanPointerAfterDraw	1	UNBOUND	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].wallPointerBeforeDelivery	1	UNBOUND	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	*
cheat-v1	$.tileMovement[*].caseId	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:593	BOUND	*
cheat-v1	$.tileMovement[*].handTileId	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:991	BOUND	*
cheat-v1	$.tileMovement[*].inputDoraIndicatorTileIds	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:935	BOUND	*
cheat-v1	$.tileMovement[*].inputDrawnTileTileId	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:924	BOUND	*
cheat-v1	$.tileMovement[*].inputKanCount	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:936	BOUND	*
cheat-v1	$.tileMovement[*].inputLastDiscardTileId	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:931	BOUND	*
cheat-v1	$.tileMovement[*].inputPlacements[*].#	5	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:907	BOUND	*
cheat-v1	$.tileMovement[*].inputPlacements.#	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:907	BOUND	*
cheat-v1	$.tileMovement[*].inputRinshanPointer	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:929	BOUND	*
cheat-v1	$.tileMovement[*].inputSeat	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:950	BOUND	*
cheat-v1	$.tileMovement[*].inputWallEndExclusive	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:928	BOUND	*
cheat-v1	$.tileMovement[*].inputWallPointer	1	IN_BIND	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:927	BOUND	*
cheat-v1	$.tileMovement[*].factsAfter	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:1016-1044	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].factsAfter	1	INTERNAL	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:601-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-reservation-delivery|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].factsAfter[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:1016-1044	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].factsAfter[*]	1	INTERNAL	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:601-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-reservation-delivery|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].factsAfter[*].container	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:1016-1044	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].factsAfter[*].container	1	INTERNAL	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:601-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-reservation-delivery|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].factsAfter[*].index	1	INTERNAL	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:609-650	BOUND	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill;where=container==wall
cheat-v1	$.tileMovement[*].factsAfter[*].index	1	UNA	meta:null-structural-placeholder	UNA:NULL-UNREAD	UNA:NULL-UNREAD	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-reservation-delivery|mv-reservation-delivery-then-beam|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall;where=container==hand|discard
cheat-v1	$.tileMovement[*].factsAfter[*].index	1	UNBOUND	V5CheatGoldenExporterTest.kt:1011-1042	GAP:N3-B11	GAP:N3-B11	case=mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall;where=container==deadWall|replacement
cheat-v1	$.tileMovement[*].factsAfter[*].seat	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:1016-1044	BOUND	case=mv-reservation-delivery-then-beam;where=container==hand|discard
cheat-v1	$.tileMovement[*].factsAfter[*].seat	1	UNBOUND	V5CheatGoldenExporterTest.kt:1011-1042	GAP:N3-B11	GAP:N3-B11	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-reservation-delivery|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall;where=container==hand|discard
cheat-v1	$.tileMovement[*].factsAfter[*].seat	1	UNA	meta:null-structural-placeholder	UNA:NULL-UNREAD	UNA:NULL-UNREAD	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall;where=container==wall|deadWall|replacement
cheat-v1	$.tileMovement[*].factsAfter[*].tileId	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:1016-1044	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].factsAfter[*].tileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1011-1042	CheatGoldenTests.cs:601-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill|mv-reservation-delivery|mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].backfilledIndex	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].backfilledTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].beamGotTileId	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].candidateSlots	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].candidateSlots[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].candidateTileIds	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].candidateTileIds[*]	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].deadWallSlot1AfterSwap	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].displacedTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:602-666	BOUND	case=mv-swap-hand-with-wall-direct|mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].doraSlotIndex	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].drawnTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].firstUndrawnTileId	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].fromReservation	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:667-714	BOUND	case=mv-reservation-delivery
cheat-v1	$.tileMovement[*].gotTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].nextDrawTileId	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].nextRinshanTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-rinshan-and-drawn-swap
cheat-v1	$.tileMovement[*].replacementSlot0AfterSwap	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].replacementTileId	1	UNBOUND	V5CheatGoldenExporterTest.kt:1043-1383	GAP:N3-B1	GAP:N3-B1	case=mv-rinshan-and-drawn-swap
cheat-v1	$.tileMovement[*].replacementTileIdBefore	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].reservedTileId	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].reservedTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:667-714	BOUND	case=mv-reservation-delivery
cheat-v1	$.tileMovement[*].resolvedIndex	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].rinshanTileId	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:1052-1111	BOUND	case=mv-rinshan-and-drawn-swap|mv-swap-hand-with-dead-wall
cheat-v1	$.tileMovement[*].successorTileId	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].wallEndExclusiveAfter	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:667-1111	BOUND	case=mv-reservation-delivery|mv-rinshan-and-drawn-swap
cheat-v1	$.tileMovement[*].wallEndExclusiveAfterDelivery	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].wallEndExclusiveBefore	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:667-1111	BOUND	case=mv-reservation-delivery|mv-rinshan-and-drawn-swap
cheat-v1	$.tileMovement[*].wallIndex	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:602-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-reservation-delivery
cheat-v1	$.tileMovement[*].wallPointerAfter	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:602-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-reservation-delivery
cheat-v1	$.tileMovement[*].wallPointerAfterDelivery	1	OUT_DIFF	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:900-1016	BOUND	case=mv-reservation-delivery-then-beam
cheat-v1	$.tileMovement[*].wallPointerAfterDraw	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].wallPointerAfterSwap	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*].wallPointerBefore	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:602-714	BOUND	case=mv-swap-hand-with-wall-direct|mv-reservation-delivery
cheat-v1	$.tileMovement[*].wallPointerBeforeDraw	1	INTERNAL	V5CheatGoldenExporterTest.kt:1043-1383	CheatGoldenTests.cs:644-666	BOUND	case=mv-swap-drawn-with-wall-backfill
cheat-v1	$.tileMovement[*]	1	CONTAINER	meta:container-exporter	meta:case-array-elements	META	*
shuffle-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META	*
shuffle-v1	$.schema	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:32	BOUND	*
shuffle-v1	$.generatorVersion	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:33	BOUND	*
shuffle-v1	$.rulesetVersion	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:34	BOUND	*
shuffle-v1	$.extractionStatus	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:35	BOUND	*
shuffle-v1	$.v4Commit	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:37	BOUND	*
shuffle-v1	$.generatedByCommand	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:41	BOUND	*
shuffle-v1	$.exportedAtNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
shuffle-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
shuffle-v1	$.v4RulesSourceNote	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
shuffle-v1	$.rngGapNote	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:46	BOUND	*
shuffle-v1	$.generationResult.#	5	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:50	BOUND	*
shuffle-v1	$.configSnapshot.#	96	META	meta:v4-config-not-consumed	meta:v4-config-not-consumed	META	*
shuffle-v1	$.slotMatrix[*].#	7	OUT_DIFF	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:60	BOUND	*
shuffle-v1	$.slotMatrix.#	1	OUT_DIFF	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:60	BOUND	*
shuffle-v1	$.demandVectors.#	13	OUT_DIFF	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:102	BOUND	*
shuffle-v1	$.overlapMatrix.#	43	OUT_DIFF	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:116	BOUND	*
shuffle-v1	$.cases.#	1	SEM_MAP	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:52	BOUND	*
shuffle-v1	$.cases[*].deadWallSegmentEqual	1	UNBOUND	V5ShuffleGoldenExporterTest.kt:39-203	GAP:N3-B2	GAP:N3-B2	*
shuffle-v1	$.cases[*].baseWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:156	BOUND	*
shuffle-v1	$.cases[*].baseWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:156	BOUND	*
shuffle-v1	$.cases[*].threeCasterWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:156	BOUND	*
shuffle-v1	$.cases[*].threeCasterWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:156	BOUND	*
shuffle-v1	$.cases[*].fourCasterWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:180	BOUND	*
shuffle-v1	$.cases[*].fourCasterWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:180	BOUND	*
shuffle-v1	$.cases[*].noTargetWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:181	BOUND	*
shuffle-v1	$.cases[*].noTargetWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:181	BOUND	*
shuffle-v1	$.cases[*].zeroBudgetWall[*].#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:182	BOUND	*
shuffle-v1	$.cases[*].zeroBudgetWall.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:182	BOUND	*
shuffle-v1	$.cases[*].redFiveCount	1	IN_BIND	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:171-173	BOUND	*
shuffle-v1	$.cases[*].fourCasterIdentity	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:183	BOUND	*
shuffle-v1	$.cases[*].reversedEqualsThree	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:184	BOUND	*
shuffle-v1	$.cases[*].threeCasterChanged	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:193	BOUND	*
shuffle-v1	$.cases[*].plans[*].seat	1	IN_BIND	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:152	BOUND	*
shuffle-v1	$.cases[*].plans[*].target	1	IN_BIND	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:153	BOUND	*
shuffle-v1	$.cases[*].plans[*]	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:148	BOUND	*
shuffle-v1	$.cases[*].plans.#	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:148	BOUND	*
shuffle-v1	$.cases[*].seed	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:142-144	BOUND	*
shuffle-v1	$.cases[*].dealerSeat	1	INTERNAL	V5ShuffleGoldenExporterTest.kt:39-203	ShuffleGoldenTests.cs:143-144	BOUND	*
shuffle-v1	$.cases[*].#	1	CONTAINER	meta:case-array-elements	meta:case-array-elements	META	*
ai-v1	$	1	CONTAINER	meta:root-node	meta:container-node	META	*
ai-v1	$.schema	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:37	BOUND	*
ai-v1	$.generatorVersion	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:38	BOUND	*
ai-v1	$.rulesetVersion	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:39	BOUND	*
ai-v1	$.v4Commit	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:40	BOUND	*
ai-v1	$.extractionStatus	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.generatedByCommand	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.generator	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.generatedAtHead	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:41	BOUND	*
ai-v1	$.v4ProductionSourceDirty	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:45	BOUND	*
ai-v1	$.v4ProductionSourceDirtyPaths	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:46	BOUND	*
ai-v1	$.v4TestOnlyTrackedDirtyPaths	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:47	BOUND	*
ai-v1	$.v4TestOnlyTrackedDirtyPaths[*]	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:47	BOUND	*
ai-v1	$.v4TestOnlyTrackedDirtyPaths.#	0	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:47	BOUND	*
ai-v1	$.sourceFileSha256[*].sha256	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:62	BOUND	*
ai-v1	$.sourceFileSha256[*].#	3	META	meta:hash-crosswalk-labels	meta:hash-crosswalk-labels	META	*
ai-v1	$.sourceFileSha256.#	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:58	BOUND	*
ai-v1	$.decisionClock	1	CONTAINER	meta:container-exporter	meta:container-node	META	*
ai-v1	$.decisionClock.baseMillis	1	IN_BIND	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:228	BOUND	*
ai-v1	$.decisionClock.reserveMillis	1	IN_BIND	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:229	BOUND	*
ai-v1	$.decisionClock.opensAtMillis	1	IN_BIND	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:232	BOUND	*
ai-v1	$.decisionClock.deadlineMillis	1	OUT_DIFF	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:233	BOUND	*
ai-v1	$.decisionClock.reserveScope	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:221-227	BOUND	*
ai-v1	$.decisionClock.expiredAtDeadlineMinus1	1	IN_BIND	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:65-71	BOUND	*
ai-v1	$.decisionClock.expiredAtDeadline	1	IN_BIND	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:65-71	BOUND	*
ai-v1	$.recordedIntent.#	12	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:71	BOUND	*
ai-v1	$.v5Observation.mappingLimits[*]	1	UNBOUND	V5AiGoldenExporterTest.kt:88-547	GAP:N3-B6	GAP:N3-B6	*
ai-v1	$.v5Observation.mappingLimits.#	1	UNBOUND	V5AiGoldenExporterTest.kt:88-547	GAP:N3-B6	GAP:N3-B6	*
ai-v1	$.v5Observation.#	31	IN_BIND	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:271	BOUND	*
ai-v1	$.cases.#	1	SEM_MAP	V5AiGoldenExporterTest.kt:88-547	AiReplayGoldenTests.cs:80	BOUND	*
ai-v1	$.cases[*].id	1	SEM_MAP	V5AiGoldenExporterTest.kt:624-709	AiReplayGoldenTests.cs:81	BOUND	*
ai-v1	$.cases[*].kind	1	SEM_MAP	V5AiGoldenExporterTest.kt:624-709	AiReplayGoldenTests.cs:611	BOUND	*
ai-v1	$.cases[*].note	1	META	meta:authoring-labels	meta:authoring-labels	META	*
ai-v1	$.cases[*].v5.authority.#	10	OUT_DIFF	V5AiGoldenExporterTest.kt:720-739	AiReplayGoldenTests.cs:464	BOUND	*
ai-v1	$.cases[*].v5.authorityMapping.#	6	SEM_MAP	V5AiGoldenExporterTest.kt:710-719	AiReplayGoldenTests.cs:404	BOUND	*
ai-v1	$.cases[*].v5.expected.mappingBasis	1	UNBOUND	V5AiGoldenExporterTest.kt:740-742	GAP:N3-B6	GAP:N3-B6	*
ai-v1	$.cases[*].v5.expected.#	3	OUT_DIFF	V5AiGoldenExporterTest.kt:740-742	AiReplayGoldenTests.cs:141	BOUND	*
ai-v1	$.cases[*].v5.intentActionRef	1	IN_BIND	V5AiGoldenExporterTest.kt:624-709	AiReplayGoldenTests.cs:378	BOUND	*
ai-v1	$.cases[*].v5.submittedAtMillis	1	IN_BIND	V5AiGoldenExporterTest.kt:624-709	AiReplayGoldenTests.cs:172	BOUND	*
ai-v1	$.cases[*].v5.#	1	CONTAINER	meta:case-container	meta:case-container	META	*
ai-v1	$.cases[*].v4Call.entryPoint	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.cases[*].v4Call.action.text	1	UNBOUND	V5AiGoldenExporterTest.kt:624-709	GAP:N3-B9	GAP:N3-B9	*
ai-v1	$.cases[*].v4Call.#	16	IN_BIND	V5AiGoldenExporterTest.kt:624-709	AiReplayGoldenTests.cs:495	BOUND	*
ai-v1	$.cases[*].v4.events[*].text	1	UNBOUND	V5AiGoldenExporterTest.kt:624-709	GAP:N3-B9	GAP:N3-B9	*
ai-v1	$.cases[*].v4.#	64	INTERNAL	V5AiGoldenExporterTest.kt:624-709	AiReplayGoldenTests.cs:558	BOUND	*
ai-v1	$.cases[*].#	1	CONTAINER	meta:case-container	meta:case-container	META	*
ai-v1	$.testOnlyChanges	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.unexportedOrMappedSamples	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.unexportedOrMappedSamples[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.unexportedOrMappedSamples.#	0	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4BehaviorSource	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4DotnetAiSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4DotnetDomainSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4EngineSourceCommit	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4TrackedDirtyPaths	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4TrackedDirtyPaths[*]	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4TrackedDirtyPaths.#	0	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.v4TrackedWorkingTreeDirty	1	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
ai-v1	$.reproducibility.#	6	META	meta:provenance-not-consumed	meta:provenance-not-consumed	META	*
```
FIELD-RULES:END

**规则表统计（全部机器复算，第 4 轮 v2 校验器 `check` 输出 = §11.6 实测；`DIST` 行 = 路径级类分布按表内首条命中行归属）**：

| fixture | 规则数 | census 路径数 |
|---|---|---|
| physical-v1 | 91 | 296 |
| scoring-v1 | 63 | 419 |
| round-v1 | 72 | 200 |
| cheat-v1 | 172 | 204 |
| shuffle-v1 | 40 | 199 |
| ai-v1 | 59 | 198 |
| **合计** | **497** | **1516** |

- **路径级类分布（`DIST class=` 行，unique-path 按首条命中行归属）**：CONTAINER 17 / IN_BIND 335 / INTERNAL 102 / META 308 / OUT_DIFF 406 / SEM_MAP 247 / UNBOUND 56 / UNBOUND_HC 45（合计 1516）。第 3 轮→第 4 轮迁移对账：OUT_DIFF 439→406、IN_BIND 328→335、UNBOUND 47→56、UNBOUND_HC 40→45、CONTAINER 13→17、INTERNAL 75→102、META 308→308、SEM_MAP 266→247（迁移前后总和均 1516；迁移明细 = §3 各节逐字段表 + §15 R3-2 映射行）。
- **GAP 路径分布（`DIST gap=` 行，12 包 101 条）**：GAP:N3-B1 14（cheat tileMovement）、GAP:N3-B2 1（shuffle deadWallSegmentEqual）、GAP:N3-B3 6（sleeveBloom goldenInputs 六叶）、GAP:N3-B4 26（round 20 + tileMarkState 6）、GAP:N3-B5 10（ledger 4 子键 outcome/exception 8 + snapshotRoundTrip 两布尔 2）、GAP:N3-B6 3（ai mappingBasis/mappingLimits）、GAP:N3-B7 1（beamSwap requestException）、GAP:N3-B8 3（三 handler note 叶；scoped outcome/reason UNBOUND_HC 行的 case 集消解随 N3-B3 落地，见 §9）、GAP:N3-B9 2（ai 文本叶）、GAP:N3-E1 6（scoring win 2 + noWin 4）、GAP:N3-E2 24（physical boundaries/boundarySamples/invalid/csharpExceptionMapping）、GAP:N3-E3 5（physical hashCrosswalk 双标签 + suitOrder + scope.targetIds 2）。**owner 列合计（unique-path 口径）：绑定/映射/结构 owner = 1090 条，META/容器 owner = 325 条，GAP owner = 101 条，三者之和 = 1516。**
- **第 3 轮规则表缺陷的修复对照（R3-1/R3-2 逐项）**：(a) 322 条 filename-only 导出锚点全部升级为 `文件.kt:L1-L2` 行段锚点，校验器 -Repo 逐一打开核对（不存在/越界即 ANCHOR）；(b) `$.beamSwapHandler[*].requestException` 从 CONTAINER catch-all 显影为 UNBOUND GAP:N3-B7；(c) `$.tileMarkHandler[*].reason` 从整组 OUT_DIFF 拆为 SEM_MAP 6 case + IN_BIND 1 case（tm-response-window）+ UNBOUND_HC 13 case 三行 scoped 规则，outcome 同法拆分；(d) hashCrosswalk 6 路径从单一 SEM_MAP 拆为 SEM_MAP 3（:95/:96/:97 实读）+ UNBOUND 2（csharpHashVerification/v4Reason → E3）+ CONTAINER 1；(e) shuffle threeCasterChanged/plans/seed/dealerSeat/fourCasterIdentity/reversedEqualsThree 从正文"fixture 内部"含糊表述落为显式 INTERNAL 行段规则（:142-144/:148/:183/:184/:193）；(f) snapshotRoundTrip 两布尔从信任级消费改记 UNBOUND_HC GAP:N3-B5（读点 :261-262 在案）；(g) 全部 broad catch-all（8 条）删除并以显式规则替换——每条替换路径均可由 §11.6 `check` 复算。
- **第 3 轮 caller 三类 checker 反例的独立复现（对第 3 轮旧校验器执行，工具与日志在 R4 TEMP；脚本 `probes-round4.ps1` SHA256 `f64337f5cf175b062f17896487c74eae017493c1cb5198ca30bf3c7bf89b293c`）**：

| 反例 | 变异 | 第 3 轮校验器实测 | 日志 SHA256 |
|---|---|---|---|
| nonexistent-anchors | 全部 file:line 锚点换成 `NonexistentExporter.kt:999999`/`NonexistentConsumer.cs:999999` | **check 仍 OK（缺陷复现）**，nativeExit=0 | `afafefaaeadccdca2499016be57cf787d30bcde7b8620bf8761375366a0052b1` |
| overlap-duplicate | 追加与既有行同 fixture+pattern 的重复规则 `$.clockUsed` | **check 仍 OK（缺陷复现）**，nativeExit=0 | `83b70ebc45e209450e6c4a0deeba4b0ad1968d6bf686d7313108d8f243d92d4e` |
| overlap-and-kind | 独立匹配器对 6 fixture 全路径 census 多重命中与标量挂 CONTAINER | **OVERLAPPING_PATHS=935、SCALAR_AS_CONTAINER=1**（明细 `overlap-and-kind-details.log` 935+1 行） | `e309ddfad55dd7bf00fef12b56a6502ce719c2d4e0e8cc83def02b84e4e0af60` |

- **checker 负例矩阵（v2 校验器，R3-1 验收：六种作弊路径全部被检出；命令与日志全文见 §11.6）**：

| 负例 | 变异 | 检出结果 | 日志 SHA256 |
|---|---|---|---|
| strip-anchors | 清空全部值级规则的 csAnchor | 309 × ANCHOR-C#，RULES-CHECK-FAILED errors=309，exit 1 | `8d39fa10c8f0cb6a8337916227afe387ff64c90d9246431898dc79be22de4ac1` |
| drop-rule | 删除首条 paths>0 规则 | 1 × LEAK（physical-v1 根路径失裁决），exit 1 | `e40e23e41049742367dac35eeee19ec4aaac80027414e1947faf580706de97a5` |
| anchorless-rule | 追加一条无行段锚点 OUT_DIFF 规则 | 4 × ANCHOR（V4 lacks line range / C# 缺失），exit 1 | `0107553a008b36a9edb4b2361b87682a232908b965e868b9255a6f858e2b21a6` |
| nonexistent-anchors | 全部 file:line 锚点换成不存在文件+越界行号 | 717 × ANCHOR（file not found / bounds），exit 1 | `7ea6f6d5de74d0c50518893e80fc09a03857a1c803d3216a9543366fde27a060` |
| duplicate-rule | 重复追加 `$.clockUsed` 行 | 1 × DUP `physical-v1 $.clockUsed scope=*`，exit 1 | `dd0b33023bd1b551fff52d6cb7e4fb3eda95d26dc87441d6198fb5a194fc1d48` |
| scalar-container | 前置 `$.#` CONTAINER 规则吞标量 | 324 × SCALAR-AS-CONTAINER（首条 `…boundarySamples[*].copyIndex kind=number`），exit 1 | `d5091c0f682ef6600aad44a32affa60fb20f61478f933a5afabcc6587ee6556e` |

- **正检实测（v2，含 DIST 分布输出）**：`RULES-CHECK-OK-V2 total-paths=1516 rules=497 leaks=0 mismatches=0 anchor-errors=0 dup=0 scalarAsContainer=0 scope-errors=0` + `DIST-TOTAL unique-paths=1516 gap-paths=101`（check-v2-positive.log SHA256 `c52452dd934fd16fec3c0d4ecf1a5921562218bad0b7ea257e5cc1d058e1841a`）。

### 3.1 physical-v1（catalog 内，第 4 轮 91 规则机器重划版）

- **SHA256** `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995`；v4Commit `32f645ec…`（真实）；census 296 distinctPaths（FIELD-RULES 91 条规则全裁决，§3.0.1）；C# 消费 `PhysicalGoldenTests`（9 Facts 全 PASS）。
- **路径级类分布（§11.6 `DIST` 复算）**：CONTAINER 3 + IN_BIND 1 + META 76 + OUT_DIFF 95 + SEM_MAP 92 + UNBOUND 25 + UNBOUND_HC 4 = 296。
- **真实 C# 数学差分（保留）**：samples[*] 的 136 墙/输入/期望逐值对照（OUT_DIFF 95 条，`PhysicalGoldenTests.cs` 9 Facts）；provenance 链：fixture 内嵌 `exporterSourceSha256` + `sourceHashAlgorithm` + `v4SourceHashes[8]`；C# 断言（`:56`、`:61`）调用 `Sha256SourceFile`（helper `:663-666`）＝**CRLF→LF 归一化 SHA**，与 catalog raw 字节 SHA256 是不同口径、双链互补不可互换（§3.8 判定）。
- **第 4 轮拆分修正（R3-2(d)，hashCrosswalk 6 路径）**：第 3 轮整组贴单一 SEM_MAP 标签、实际 C# 只读 3 条——机器重划：`v4CanonicalStateHash` SEM_MAP `:95`、`csharpHash` SEM_MAP `:96`、`wallCanonicalDigestAlgorithm` SEM_MAP `:97`、`csharpHashVerification`/`v4Reason` **UNBOUND GAP:N3-E3**（变异探针 physical-hash-labels：双字段改 WRONG → 9/9 PASS exit 0，§11.8）、容器 CONTAINER。
- **第 4 轮拆分修正（scope 8 路径）**：`manifestEntriesTargeted/ExportedByThisRun/NotFullAudit/fullManifestAudited/note` 5 条 SEM_MAP（`:73`-`:77` 实读）+ `targetIds`/`targetIds[*]` **UNBOUND GAP:N3-E3** 2 条 + 容器 CONTAINER。
- **GAP:N3-E2 24 条（沿用，机器复核一致）**：`expected.boundaries[*]` 11 + `boundarySamples[*]` 6 + `invalid[*]` 3 + `csharpExceptionMapping` 4（含容器 1，全带行段导出锚点）。
- **GAP:N3-E3 5 条（第 4 轮新增）**：hashCrosswalk 双标签 2 + `expected.suitOrder` 1 + `scope.targetIds` 2。落地动作 = §9 N3-E3。
- **侥幸通过判定**：samples 数值面 95 条 OUT_DIFF 设防（正控制 physical-boundary-control：`samples[0].expected.tiles[0].rank→99` → 1 FAIL exit 1，§11.8）；boundaries/boundarySamples/invalid/csharpExceptionMapping/hashCrosswalk 双标签/suitOrder/targetIds 共 29 条不设防（探针 1 + FIELD-RULES 显式规则）。

### 3.2 scoring-v1（catalog 内，第 4 轮机器复算分母版）

- **SHA256** `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E`；v4Commit `97831c88…`（真实）；census 419 distinctPaths（FIELD-RULES 63 条规则全裁决）；C# 消费 `ScoringGoldenTests`（7 Facts 全 PASS，10238 case 全遍历）。
- **路径级类分布（§11.6 `DIST` 复算）**：CONTAINER 1 + IN_BIND 144 + META 60 + OUT_DIFF 136 + SEM_MAP 72 + UNBOUND 6 = 419。
- **winSamples 组 = 96 条（第 3 轮 95 的修正，R3-3(e)）**：IN_BIND 32（input 全树绑定 `CompareWinSample` :207-242）+ OUT_DIFF 50（expected 全树逐值对照，除 status 外）+ SEM_MAP 2（`$.winSamples` 容器经 `$.winSamples.#` 持有 + `id`）+ UNBOUND 2（`expected.status`、`input.extraHan` → GAP:N3-E1）+ META 10（tags/source/容器）。**第 3 轮分母 95 漏计 `$.winSamples` 数组容器本身**（机器真值以校验器 `DIST` 输出为准）；变异探针 win→noWin → 7/7 PASS（第 2 轮）证明 status 不设防。
- **noWinSamples 组 = 43 条（第 3 轮 42 的修正，R3-3(e)）**：IN_BIND 27（input 其余字段绑定，`CompareNoWinSample` :247-250）+ SEM_MAP 4（容器经 `expected.#` 持有的 `expected` 容器 + `expected.status` + `id` + `$.noWinSamples.#` 持有的数组容器）+ UNBOUND 4（`doraIndicators/uraDoraIndicators/honba/riichiSticks` → GAP:N3-E1）+ META 8（source/容器）。**第 3 轮分母 42 漏计 `$.noWinSamples[*].expected` 容器路径**；两处 +1 均为容器归属口径修正，非消费语义变化。
- **GAP:N3-E1 = 6 条（沿用）**：win 侧 2（status/extraHan）+ noWin 侧 4；`winSamples[*].tags/tags[*]` 以显式 META 排除处理（审批责任 §9 N3-E1(d)）。
- **侥幸通过判定（沿用）**：6 条 UNBOUND 变异不设防（第 2 轮探针）；正控制 totalPoints 错值 → 1 FAIL exit 1（第 2 轮）。

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

### 3.4 cheat-v1（catalog 内，第 6 轮逐实例 scoped 重划版）

- **SHA256** `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；v4Commit `a96c195c…`（真实）；census 204 distinctPaths（FIELD-RULES 215 条规则全裁决，其中 scoped case 级规则 109 行——全部 scoped 机制集中于本 fixture）；C# 消费 `CheatGoldenTests`（16 Facts 全 PASS）。
- **路径级类分布（§11.6 `DIST` 复算，first-match 首行投影）**：CONTAINER 7 + INTERNAL 33 + IN_BIND 45 + META 3 + OUT_DIFF 48 + SEM_MAP 28 + UNBOUND 19 + UNBOUND_HC 21 = 204（第 4 轮口径 66 IN_BIND 的下降 = goldenInputs 家族按单元重划后，"整路径已绑"的假象被拆散：见下两行）。
- **第 2 轮分桶错误的正式更正（R2-3a，沿用）**：case **#23 sb-activate-already-used 实为 A 桶（已绑定）**——`CheatGoldenTests.cs:486-490` 经 `AssertGoldenRejection` → `:557 Assert.Equal(ReasonMap[Str(c,"reason")], expected)` 真实读取该 case 的导出 reason 并与 C# 实算错误码差分；**正控制探针**把该 case 的 reason 篡改为 WRONG_PHASE 后 → `Handler_SleeveBloom_RepresentativeCases_ReplayAgainstCheatSubmission [FAIL]`（Assert.Equal Failure，Total:16 Failed:1，exit 1，§11.8 探针 P1）——第 2 轮把 #23 记为 D-α（无黄金对照）与源码事实矛盾，**更正为 A**。
- **75 个 handler case 逐条分桶**（tileMark 20 + sleeveBloom 32 + beamSwap 23；桶定义：**A** 黄金输出行为重放（真 C# 入口 + outcome/reason 逐值对照）、**B** 部分绑定、**C** goldenInputs 驱动全链路重放、**D** 无黄金行为对照（仅 `Handler_Sections_CoverEveryRejectionReason` :339-365 的 reason 映射完备性覆盖）、**E** 纯 C# 存在断言但黄金条目未绑定。分桶依据 = 源码字面量差集初筛 + 逐断言深读判级；**机器复算命令与 75 行 TSV 见 §11.7（case-refs.ps1，-File 形式，exit 0）**）：

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


- **分桶汇总（第 4 轮算术修正，R3-3(a)）**：**行为级黄金对照 = 24/75（A 16 + B 2 + C 6）**；E 1 条为纯 C# 存在断言（golden 条目本身未绑定，注释级对照，**不计入行为绑定**）；**合计某种形式对照 = 25/75**；D 50 条无行为对照。第 3 轮 "A16+B2+C6=24" 本身正确，但 §13 曾误写 "24/75 行为对照（注释级 E1 亦计入）" 与 "+E1=25 行为级"——E1 是存在级不是行为级，两种口径在第 4 轮明确分离。
- **goldenInputs 家族逐单元真值（第 5 轮，R4-2 核心）**：`$.sleeveBloomHandler[*].goldenInputs` = **26 个叶路径、79 个（case，实例）单元**，逐叶逐案例判类后 **INTERNAL 14 + SEM_MAP 12 + OUT_DIFF 4 = 30 单元有真实绑定/映射锚点；UNBOUND 49 单元（B3 21 + B10 28）；IN_BIND 0**。**第 4 轮"其余 22 条 IN_BIND 已绑 ：718-826"的错误**：该说法把"叶路径在部分案例下被消费"当成了"整路径 21 条已绑"——按单元拆开后，任一叶都不存在"全部案例已绑"；被消费的 30 个单元各有独立锚点（`act=sb-activate-allowed→choice=sb-choose-second` 绑定链 `:718-727`/`:737`、`act-rinshan→choose-rinshan` `:769-775`/`:787`、`sbo` 链 `:814-826`、exporter 锚 `V5CheatGoldenExporterTest.kt:308-357`）。逐叶表（units = 案例数；锚点 = C# 消费行）：

| goldenInputs 叶 | units（按类） | 缺口归属 |
|---|---|---|
| `availableWallCount` | UNBOUND 1 | B3 |
| `offerId` | UNBOUND 4 | B3 |
| `rinshanPointer` / `wallEndExclusive` / `wallPointer` | 各 UNBOUND 5 | B3 |
| `windowId` | UNBOUND 1 | B3 |
| `seat` | SEM_MAP 1（sbo `:819`）+ UNBOUND 6 | **B10** 6 |
| `keep` | SEM_MAP 3（`:723`/`:773`/`:823`）+ UNBOUND 1（choose-first） | **B10** 1 |
| `drawKind` | SEM_MAP 2（`:722`/`:772`）+ UNBOUND 3 | **B10** 3 |
| `drawnTileId` | INTERNAL 2（`:725`/`:774`）+ UNBOUND 3 | **B10** 3 |
| `nextWallTileId` | INTERNAL 1（`:727`）+ UNBOUND 1 | **B10** 1 |
| `nextDeadWallTileId` | INTERNAL 1（`:775`）+ UNBOUND 1 | **B10** 1 |
| `cost` | SEM_MAP 1（sbo `:822`）+ OUT_DIFF 2（`:737`/`:787`）+ UNBOUND 3 | **B10** 3 |
| `cheatPoints` | SEM_MAP 1（sbo `:822`）+ OUT_DIFF 2（`:737`/`:787`） | 已绑（3/3 单元） |
| `turnNumber` / `offerCreatedTurnNumber` | 各 INTERNAL 1（`:724`）+ UNBOUND 2 | **B10** 各 2 |
| `offerFirstTileId` | INTERNAL 2（`:725`/`:774`）+ UNBOUND 1 | **B10** 1 |
| `offerSecondTileId` | INTERNAL 2（`:727`/`:775`）+ UNBOUND 1 | **B10** 1 |
| `offerSecondWallIndex` | INTERNAL 1（`:726`）+ UNBOUND 2 | **B10** 2 |
| `nextAvailableWallIndex` | INTERNAL 1（`:726`）+ UNBOUND 2 | **B10** 2 |
| `currentPlayer` / `phase` | 各 SEM_MAP 1（`:820`/`:821`） | 已绑 |
| `offerFirstInWall` / `offerSecondInWall` | 各 SEM_MAP 1（`:825`/`:826`） | 已绑 |
| `now` / `deadlineMillis` | 各 INTERNAL 1（`:824`） | 已绑 |

  合计 26 叶 79 单元 = **B3 21 + B10 28 UNBOUND**（补齐包 §9）；tileMark/beamSwap 的 goldenInputs 全绑定无缺口（结论沿用）。**UNBOUND 判级的实证护栏**：B10 代表单元变异 3 例（choose-second 的 `cost`→999、choose-first 的 `seat`→999、choose-second 的 `drawKind`→WRONG）全部 16/16 PASS exit 0（§11.8 第 5 轮探针）；BOUND 面正控制 = `sb-activate-allowed` 的 `cost`→999 → 1 FAIL exit 1（`sleeve-input-bound-control`）。
- **factsAfter 家族逐实例真值（第 6 轮 R5-2 重算；第 5 轮"26 单元"分解作废）**：`$.tileMovement[*].factsAfter[*].index` / `[*].seat` 两路径共 **34 实例**，按**真实容器读点**逐实例判级（机器呈现 = §3.0.1 规则表 6 条 where 拆分行 + §11.6 `PATH-UNITS` 两行 owners）：
  - `index` 17 实例 = **wall 2 INTERNAL**（m1a/m1b 被 `:609-650` 实读；第 5 轮"wall 分区显式排除"表述**撤回**——m5 区消费器 `:1030-1031` 本就含 `case "wall"` 分支，wall index 语义是"源码比较、样本全被读"，非"被排除"）+ **hand/discard 11 UNA**（导出 null 结构占位、`Int(...)` 无读点，显式"不适用"非缺口，§1.4）+ **deadWall/replacement 4 GAP:N3-B11**（数值、无人读——m3/m4 的 `TileMovement_DeadWallLaws_ReplayThroughBeamSwapEntrypoint`（:1051-1105）只经 `FactContainer(...)` 读 `container` 串，不读坐标）；
  - `seat` 17 实例 = **m5 hand/discard 4 OUT_DIFF**（`:1016-1044` switch 实读 `pos.Seat == Int(fact,"seat")` 逐值对照，正控制 `m5-facts-hand-seat-control` 1 FAIL 实证设防）+ **其余 5 案例 hand/discard 7 GAP:N3-B11**（数值、无人读）+ **wall/deadWall/replacement 6 UNA**（导出 null、无读点）；
  - **诚实口径（R5-2 验收）**："源码分支存在但冻结样本 0 个"记 **NOT_EXERCISED**（m5 消费器 wall/deadWall/replacement 三分支即此形态：m5 fixture 仅含 hand/discard facts 各带 index/seat，wall 坐标零冻结样本）——与"该实例实际未被比较"（真缺口，B11）严格区分；`m5-facts-unchecked-coordinate` 探针的**实际生效变异 = m5 hand/discard index 2 实例 null→999**（16/16 PASS 证 null index 无读点），其 wall 分区 `seat`→999 半支在 m5 **零命中（NOT_EXERCISED）**——第 5 轮"wall seat→999 已实测不设防"的表述据此更正（不虚构样本）。B11 合计 = 4 + 7 = **11 units**（§9）。
- **D-β（50 条 D 桶非全部"侥幸"）**：其中 reason-mapped case 由 `Handler_Sections_CoverEveryRejectionReason`（:339-365）的 C# 实算错误码差分构成**存在级**覆盖（逐值 outcome/reason 重放仍缺，落在 B4/B5/B8 包）。
- **ledger（第 4 轮机器重划）**：SEM_MAP 1（`$.ledger` 容器经读点持有）+ IN_BIND 26（差分子集逐值对照）+ UNBOUND_HC 10 + OUT_DIFF 15 + CONTAINER 1 = 53。**GAP:N3-B5 = 10 条**（四子键 outcome/exception 8 + snapshotRoundTrip `balancesEqual`/`entriesEqual` 两布尔 2——读点 `CheatGoldenTests.cs:261-262` `Assert.True(Bool(...))` 为信任级存在断言，翻值即 FAIL 但无计算值差分，绑定任务记 N3-B5 逐值对照）。
- **tileMarkState**：6 条 UNBOUND_HC（`registerDealtRound.versionAfterFirstRegister`、`addGroup.sameIdDifferentContentException/seatGroupLimitIsThree/seatGroupLimitException/seatTileLimitIsTwelve`、`expiry.createdRoundOrdinal`）随 GAP:N3-B4 包（§9）；其余 tileMarkState 路径 META/容器。
- **tileMovement（第 4 轮机器重划 R3-2(c)；第 5 轮 factsAfter index/seat 拆分后组内算术更新）**：IN_BIND 16（m1a/m3 结构断言，:602-629/:412-418）+ OUT_DIFF 18 + **INTERNAL 20** + SEM_MAP 1 + UNBOUND 12 + UNBOUND_HC 2 + CONTAINER 1 = 70（`factsAfter[*].index` 首行投影仍 INTERNAL、`[*].seat` 首行投影改 OUT_DIFF——单元级拆分见下一段）。**m1b cross 字段（resolvedIndex/backfilledIndex/backfilledTileId/drawnTileId/wallPointerBeforeDraw/wallPointerAfterDraw/wallPointerAfterSwap/displacedTileId）= INTERNAL**：`TileMovement_DrawnBackfill_IsAV4OnlyPath_RecordedAsStructure`（:642-666）为纯结构自洽断言（resolvedIndex==factsAfter.index、backfilledIndex==resolvedIndex-1、wallPointerBeforeDraw+1==wallPointerAfterDraw），无生产差分语义、无绝对锚——第 3 轮把 m1b 记 "11 叶 golden 输出对照" 与源码不符，更正；coherent+100 变异 16/16 PASS（§11.8）与 INTERNAL 判级一致（非 GAP）。其余 UNBOUND 12 条入 **GAP:N3-B1（14 条，§9 逐路径列出）**。
- **handler scoped 拆分（第 4 轮新增 R3-2(b)；第 5 轮按 R4-1 公布单元数）**：`tileMarkHandler[*].outcome` 拆 SEM_MAP 6 case + UNBOUND_HC 14 case；`tileMarkHandler[*].reason` 拆 SEM_MAP 6 + IN_BIND 1（tm-response-window，:417-418 实读 ResponseWindowOpen/WRONG_PHASE）+ UNBOUND_HC 13；`sleeveBloomHandler[*].outcome` 拆 SEM_MAP 5 + UNBOUND_HC 27；`beamSwapHandler[*].outcome` 拆 SEM_MAP 5 + OUT_DIFF 1（bm-choose-keep，:553-556 黄金重放）+ UNBOUND_HC 17；`beamSwapHandler[*].reason` 拆 SEM_MAP 5 + UNBOUND_HC 18；`beamSwapHandler[*].requestException` 整组 UNBOUND → **GAP:N3-B7**（CONTAINER 吞并缺陷修复）；三 handler `note` 叶 → **GAP:N3-B8**（存在级 :350-351 读点）。**第 4 轮 §9 曾把六个 UNBOUND_HC scoped 行记为"72 条 UNBOUND_HC 路径"——实为 116 个实例**（14+13+27+27+17+18；每 case 恰 1 个 outcome/reason 实例，逐实例数与 case 数同值），三 handler `note` 叶另计 **75 实例**（每 handler case 1 个 note：20+23+32），**§11.6 v4 `CASE-DIST` gap=B8 units=191 = 116 + 75**（v3 曾记 119 = 116 + note 3 路径——R5-1 混合分母的又一形态，第 6 轮更正）；HC 单元的 case 集消解落 **GAP:N3-B8**（机器 owner 列同值，§9 N3-B8 承载 case 级绑定动作清单）。
- **侥幸通过判定（第 5 轮更新）**：UNBOUND/UNBOUND_HC 面（B1/B3/**B10**/B11/B4 部分/B5 部分/B7/B8）变异不设防——第 4 轮探针：cheat-request-exception（requestException 篡改 → 16/16 PASS exit 0）、cheat-unbound-reason（UNBOUND_HC case 的 reason 改 NOT_YOUR_TURN → 16/16 PASS exit 0）、cheat-six-goldeninputs（B3 六叶值改 WRONG/999 → 16/16 PASS exit 0）、cheat-backfill-coherent（m1b coherent 值 +100 → 16/16 PASS exit 0，证 INTERNAL 无生产语义）；**第 5 轮新增 7 个 cheat 探针**：sleeve-input-relative-turns（关系对双 +100 → 16/16 PASS）、sleeve-input-relative-entities（实体对双 +1 → 16/16 PASS）、m5-facts-unchecked-coordinate（实际生效变异 = m5 hand/discard 分区 `index` 2 实例 null→999；wall 分区 `seat`→999 半支在 m5 零命中 NOT_EXERCISED——m5 无 wall facts → 16/16 PASS，证 null index 坐标无读点，§3.4 factsAfter 段）、sleeve-unbound-cost/seat/drawkind 三例（B10 代表单元 → 16/16 PASS）、sleeve-input-relation-break（**正控制**：关系对单侧 +100 → 1 FAIL exit 1，证 INTERNAL 对是真绑定）；cheat 绑定面的正控制 = 第 3 轮 **#23 正控制**（reason→WRONG_PHASE → 1 FAIL exit 1，A 桶证据，§11.8 P1）+ 第 5 轮 `sleeve-input-bound-control`（cost→999 → 1 FAIL）与 `m5-facts-hand-seat-control`（hand/discard seat→999 → 1 FAIL）；snapshotRoundTrip 翻布尔 → FAIL（信任级，绑定任务 N3-B5）。

### 3.5 shuffle-v1（catalog 内，第 4 轮机器重划版）

- **SHA256** `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282`；v4Commit `37b4dd61…`（真实）；census 199 distinctPaths（FIELD-RULES 40 条规则全裁决，§3.0.1）；C# 消费 `ShuffleGoldenTests`（4 Facts 全 PASS）。
- **路径级类分布（§11.6 `DIST` 复算）**：CONTAINER 2 + INTERNAL 17 + IN_BIND 3 + META 99 + OUT_DIFF 64 + SEM_MAP 13 + UNBOUND 1 = 199。
- **真实 C# 数学差分（生产算法被调用并逐值对照，保留）**：
  - slotMatrix 16 组逐值 ↔ `ShuffleSlots`（:75，含 DeadWallStart 越界禁令）；
  - demandVectors 6×34 逐值（1e-12）↔ `ShuffleTargets.Demand`（:107）；
  - overlapMatrix 6×6 双精度逐值 ↔ `ShuffleTargets.Overlap`（:121）+ 测试内独立冻结硬值双锚（2/√117、3/√39、4/√52、0、1，:127-131）。
  - 以上 OUT_DIFF 64 + SEM_MAP 13 + IN_BIND 3（输入面）为消费面主体。
- **第 4 轮显式 INTERNAL 落规则（R3-2(e)，17 条）**：第 3 轮正文以"fixture 内部结构检查"含糊带过的字段全部落为显式 INTERNAL 行段规则（导出锚 `V5ShuffleGoldenExporterTest.kt:39-203`，机器 -Repo 核对通过）：五组墙数组叶（`baseWall/threeCasterWall/fourCasterWall/noTargetWall/zeroBudgetWall` 各 `[*].#`+`.#`，10 条——C# `ExportedWallsSatisfyPhysicalLaws` :160-175 仅对导出数组做 136 唯一/置换/赤五恒 3/恒等律/死山隔离检查，不运行 C# 生产 shuffle 生成墙）、`fourCasterIdentity`（:183）、`reversedEqualsThree`（:184）、`threeCasterChanged`（:193）、`plans[*]`/`plans.#`（:148/:152/:153 关系检查）、`seed`/`dealerSeat`（:142-144 导出，C# 侧无逐值断言）。`rngGapNote`/D-09 声明的 RNG 差异意味着 V4 种子与 C# 不可能同墙——INTERNAL 判级 = 结构自洽语义，无生产差分语义，与 §3.4 m1b 同口径。
- **已导出未绑定（GAP:N3-B2，1 条路径，沿用）**：`$.cases[*].deadWallSegmentEqual`（32 布尔实例）——`deadWallSegmentEqual` 在 ShuffleGoldenTests.cs 中从未被读取；变异探针第 1 例 true→false → 4/4 PASS exit 0（§11.8）。**第 4 轮补强（R3-3(c)）**：N3-B2 落地包升级为"在 `ShuffleGoldenTests.cs` 增加 `deadWallSegmentEqual` 绑定断言"（对 32 布尔逐值对照或导出侧显式排除，caller 裁决，§9）。
- **侥幸通过判定**：slotMatrix/demandVectors/overlapMatrix 数值面设防（第 1/2 轮变异探针）；INTERNAL 17 条 + UNBOUND 1 条不设防（探针 deadWallSegmentEqual + FIELD-RULES 显式规则）。

### 3.6 ai-v1（catalog 外第 6 份，第 4 轮机器重划版）

- **SHA256** `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；v4Commit `205506f0…`（真实，恰为 catalog 冻结头）；census 198 distinctPaths（FIELD-RULES 59 条规则全裁决，§3.0.1）；7 case；C# 消费 `AiReplayGoldenTests`（3 Facts 全 PASS，经 csproj `<None Include>` 拷贝读取）。
- **路径级类分布（§11.6 `DIST` 复算）**：CONTAINER 4 + INTERNAL 64 + IN_BIND 54 + META 24 + OUT_DIFF 14 + SEM_MAP 33 + UNBOUND 5 = 198。
- **真实 C# 行为（保留）**：7/7 case 经 `AiDecisionService.Replay + Submit`（`ThrowingStrategy` 保证绝不运行启发式）：录制意图（recordedIntent 绑定防换动作）、拒绝码（AI-002-old-round/AI-003-old-decision/AI-004-old-epoch/AI-005-wrong-seat/AI-006-candidate-not-legal/AI-007-exact-deadline/AI-001-legal-intent）、v5.authorityMapping 六种 mode 构造校验（输入/配置面绑定）。
- **fixture 内部证据（非 C# 后态差分，保留）**：`AssertV4Evidence` 的 `DeepEquals(before,after)`（:571/:607）比较的是同一 fixture 的 V4 前后树（尤其拒绝 case 的 before==after 期望），不是"C# 重放后实际状态 vs V4 全树"差分；接受 case 仅核若干关系字段（receiptStatus/eventCount/stateChanged）；末 Fact 仅 DeadlineArbiter 接纳动作，不执行 RoundStateMachine 产生 C# 后态。第 4 轮该面落为显式 INTERNAL 规则 `$.cases[*].v4.#`（64 条路径，导出锚 `V5AiGoldenExporterTest.kt:624-709`）。
- **decisionClock 全组绑定（第 4 轮机器复核，8 条路径无一 GAP）**：容器 CONTAINER + `baseMillis`/`reserveMillis`/`opensAtMillis` IN_BIND（:228/:229/:232）+ `deadlineMillis` OUT_DIFF（:233 实算对照）+ `reserveScope` SEM_MAP（:221-227）+ `expiredAtDeadline`/`expiredAtDeadlineMinus1` IN_BIND（:65-71 边界构造）。**正控制探针 ai-clock-metadata**（`baseMillis`/`reserveMillis`→999 → 1 FAIL exit 1，§11.8；第 3 轮草案曾误记为"deadline 改 ±1"，第 5 轮按工具实际变异行更正）证明该组确已设防——第 3 轮草案曾误记其为缺口，机器探针推翻。
- **GAP:N3-B6 = 3 条（第 4 轮路径修正，R3-3(d)）**：`$.cases[*].v5.expected.mappingBasis`（导出锚 `V5AiGoldenExporterTest.kt:740-742`；第 3 轮误写为 `$.v5Observation.mappingBasis`，机器 census 证实真实路径在 `cases[*].v5.expected` 之下）+ `$.v5Observation.mappingLimits[*]`/`.#`（:88-547）。观测元数据中声明"映射依据/边界"的字段无 C# 断言消费；补齐包 N3-B6 = 在 `AiReplayGoldenTests` 增加存在+取值断言或导出侧声明排除（caller 裁决，二选一，禁止沉默）。
- **GAP:N3-B9 = 2 条（第 4 轮新增）**：`$.cases[*].v4Call.action.text` 与 `$.cases[*].v4.events[*].text`（:624-709）——动作/事件的人类可读文本叶无 C# 断言消费（结构面 v4Call/v4 已由 INTERNAL/recordedIntent 覆盖，仅 text 叶悬空）；补齐包 N3-B9 = 存在断言或导出侧排除（§9）。
- **索引缺口与 G-049（保留）**：catalog `fixtures[]=5`（notice 明示 five accepted V4-exported fixtures）、manifest 无 ai-v1 行、G-049 `coverage=none` 与仓库现状不符——ai-v1 恰为 recorded-intent + stale-AI submission fixture。按本节消费边界，G-049 不可重算 full；**建议 partial**（真实行为面存在 + 后态差分缺位）。是否扩口径由 caller 裁决（§9 N3-C1）。
- **侥幸通过判定**：recordedIntent/拒绝码/authorityMapping/decisionClock 面设防（历史变异 + ai-clock-metadata 正控制）；B6 3 条 + B9 2 条不设防（探针覆盖缺口记录在案，§10）。

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
| G-008 | P1.1 | —（cheat tileMovement 的 2 个 swap case 已重放）；**另 tileMovement 14 条路径 GAP:N3-B1**（N-NEW-5；第 4 轮 m1b cross 字段改判 INTERNAL 后由 13 → 14） | draw/discard/pon/kan 的 TileMovement case（五份均无） | — |
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
| N-NEW-5 | cheat-v1 | tileMovement 14 条（UNBOUND 12 + UNBOUND_HC 2，GAP:N3-B1；第 4 轮 m1b 移出改判 INTERNAL）+ sleeveBloom goldenInputs 六叶 6 条（GAP:N3-B3，§3.4——第 4 轮更正：缺口属 sleeveBloom，tileMark/beamSwap 的 goldenInputs 均已绑定） | FIELD-RULES 显式规则；探针 Q5 六叶删 → 16/16 PASS（§11.8） |
| N-NEW-6 | cheat-v1 | tileMarkState 6 字段（GAP:N3-B4 cheat 部分） | 探针 P3 六字段全篡改 → 16/16 PASS（§11.8） |
| N-NEW-7 | cheat-v1 | ledger 四子键 outcome/exception 8 条 + snapshotRoundTrip 两布尔 2 条（GAP:N3-B5 = 10，第 4 轮扩展） | 探针 P4/Q4 全篡改 → 16/16 PASS（§11.8） |
| N-NEW-8 | ai-v1 | `$.cases[*].v5.expected.mappingBasis` + `$.v5Observation.mappingLimits`/`[*]` 3 条（GAP:N3-B6；第 4 轮路径修正）+ text 叶 2 条（GAP:N3-B9，第 4 轮新增） | FIELD-RULES 显式 UNBOUND 规则（§3.6） |

**20 项 partial 的分子合计（第 4 轮机器分母）**：已导出未断言 = round-v1 **16 字段级条目（20 条 census 路径）** + cheat-v1 **53 case 级（D50 + B2 + E1；其中 D-α46 可构造、D-β4 需语义映射批准）** + cheat-v1 路径级 40 条（GAP:N3-B1/B3/B4cheat/B5/B7/B8，§3.0.1 DIST）+ G-041 Entries 对照缺位 1 项；cheat case 级与 handler 分桶行（G-033..039）为视图关系不叠加。新增行 N-NEW-1..8 与 20 行部分重叠处已注明 GAP 包归属，不重复计入分子。其余 partial 缺口全部为"尚未导出"。G-019 是规则裁决（D-01）；G-003b 一半不可表示。

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

**剩余真实缺口**：N3-B1（tileMovement 14 条路径绑定；第 4 轮 m1b cross 字段改判 INTERNAL——`:642-666` 为纯结构自洽断言，不入 B1，§3.4）、N3-B2（注释更正 + ShuffleGoldenTests.cs deadWallSegmentEqual 绑定）、N3-B1 附带的 INFO-2 地址落点表示差（不可表示，记录即可）。

---

## 8. 专查三：AgariYame / ChooseDealerEnd（沿用第 1 轮）

| 侧 | 锚点 | 事实 |
|---|---|---|
| V4 | `MatchSession.kt:82,497-532`；`GameAction.DeclareAgariYame`（ApplicationCoordinator :3268） | `ApprovedSessionStep.ChooseDealerEnd(agariYame=true)` → `MatchFinishReason.AGARI_YAME` → SessionResult + MatchEnded |
| C# | `MatchSession.cs:498` `ChooseDealerEnd(bool agariYame, …)`；:212 `CheckAgariYameEligible`；:536 `MatchFinishReason.AgariYame` | 行为完整存在 |

matchProgression 16 case 全字段消费含 `allowAgariYame/dealerDeclaredAgariYame/canDealerDeclareAgariYame`。**判定**：G-027 行为 full 成立；round 层命令入口枚举差只是命令入口面 gap，不得据此断言"缺全部 agari-yame 行为"。

---

## 9. 最小补齐包（可独立下发）与依赖 DAG（第 6 轮：路径口径 101 / 单元口径 592 双分母全覆盖）

### 补齐项（全部为测试/文档/索引文件，不触生产代码、不改 fixtures、不改 catalog 冻结数字以外的语义）

每包的机器分母 = §3.0.1 FIELD-RULES 块中 owner 列为对应 `GAP:N3-*` 的路径数（DIST 口径）与实例数（CASE-DIST 口径）**双口径并报**，**逐路径清单由 §11.6 `DIST gap=` 行复算、逐实例数由 `CASE-DIST gap=` 行复算、逐路径实例数由 `PATH-UNITS` 行复算**（路径口径 101 条逐一核对：cheat 40 + round 20 + scoring 6 + physical 29 + shuffle 1 + ai 5 = 101；单元口径 592 = **全部家族逐实例展开**后 owner 列为 GAP 的实例数——CensusInstances 655,145 实例全集、每实例恰认领一次，分解见 §3.0.1 表）。**诚实口径（R4-1 建立、R5-1 收紧）**：完整分母是**实例**而非路径——"101 条 GAP 路径"是 DIST 口径的定义投影，**不声明它枚举全部未绑定单元**；未绑定实例完整分母 = **592 units**（v3 的 286 为"scoped 逐实例 + unscoped 1 路径 = 1 单元"混合分母，R5-1 作废）。分母为当前规则表机器值；判级表本身的完备性以本审计的复核过程为界（不可发现性说明见 §15）。

| ID | 文件 | 内容（断言要求逐项） | 依赖 | 对应缺口（路径 / 单元） |
|---|---|---|---|---|
| N3-A1 | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs` | RoundTerminal：(a) 事件序列 = fixture `eventTypes=[TsumoWin,ScoreSettled,RoundEnded]` 经 §3.3 投影规则 → `Assert.Equal(["TsumoWin","RoundEnded"])`，注释引用 fixture 原序列与投影规则；(b) 绑定 `input.phaseBefore/versionBefore`（替换硬编码 :110-113）；(c) 绑定 `settlement.outcome/totalDeltas`（:117/:131）；(d) 绑定 `expected.phaseAfter/resultOutcome`（:129/:130 改读导出值） | 无 | GAP:N3-B4 round 部分 9 路径 / 14 units（roundTerminal 逐路径实例，PATH-UNITS 复算） |
| N3-A2 | 同上 | ChankanRon：(a) `Assert.Equal(["RonWin","RoundEnded"])`（投影注释同上）；(b) **绑定 `expected.hasKanDeclared/hasRinshanDraw`**：`Assert.Equal(导出布尔, 终局事件列表.Contains("KanDeclared"/"RinshanDrawn"))`，行注释仅作名称映射说明，不以注释豁免绑定；(c) 绑定 `meldTypeAfterKan/legalRonSeat1/input.meldTiles/phaseAfterKan/phaseAfterRon/resultOutcome`（替换 :221/:227 硬编码） | 无 | GAP:N3-B4 round 部分 11 路径 / 15 units（chankanRon 逐路径实例，PATH-UNITS 复算） |
| N3-B1 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | tileMovement **14 条路径绑定（第 4 轮逐路径清单，机器 DIST 复算）**：`deliveredTileId`、`inputLastDiscardSeat`、`replacementTileId`、`rinshanPointerAfterDraw`、`wallPointerBeforeDelivery`、`note`、`factsBefore`、`factsBefore[*]`、`factsBefore[*].container/.index/.seat/.tileId` 共 12 条 UNBOUND + `doraRefusalException`、`wallSlotRefusalException` 2 条 UNBOUND_HC。**第 4 轮口径更正（R3-3(f)）**：m1b cross 字段（resolvedIndex/backfilledIndex/…）已改判 INTERNAL（§3.4），**不再入 B1 分母**——B1 = 14 非 13，且无 "11 叶 golden 输出对照" 之说 | 无 | GAP:N3-B1 = 14 路径 / 24 units |
| N3-B2 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` + `ShuffleGoldenTests.cs` | (a) 更正 CheatGoldenTests :638-642 注释为准确映射（ChooseSleeveBloom:376 为入口；ChooseBeamSwap 拒绝=V4 生产对齐；地址落点差按 INFO-2 记录为表示差）；(b) **第 4 轮新增（R3-3(c)）**：`ShuffleGoldenTests.cs` 增加 `deadWallSegmentEqual` 绑定断言（32 布尔逐值对照或导出侧显式排除，禁止沉默） | 无 | GAP:N3-B2 = 1 路径 / 32 units（32 布尔实例逐一，PATH-UNITS `deadWallSegmentEqual units=32`）（+(a) 注释项非路径分母） |
| N3-B3 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`（仅测试） | **sleeveBloom goldenInputs 未绑定单元绑定包（第 5 轮按 R4-2 重写）**：`$.sleeveBloomHandler[*].goldenInputs` 的 6 叶 × 出现案例共 **21 个 UNBOUND 单元**逐单元绑定或导出侧显式排除——`availableWallCount`（1：sbo-activate-allowed）、`offerId`（4：sb-choose-first/second/rinshan、sbo-choose-allowed）、`rinshanPointer`（5）、`wallEndExclusive`（5）、`wallPointer`（5）、`windowId`（1：sbo-activate-allowed）。**第 4 轮"其余 22 条 IN_BIND 已绑 ：718-826"撤回**（§3.4 逐叶表：真相 = 30 个单元有锚点、49 个 UNBOUND、IN_BIND 0）；tileMark/beamSwap goldenInputs 已绑无缺口（沿用）。handler case 级动作清单（D-α/D-β/B/E）随 §3.4 分桶表执行，其机器单元分母记在 N3-B8（scoped HC 行 owner 列） | 无 | GAP:N3-B3 = 6 路径 / 21 单元 |
| N3-B8 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | **handler 全清单包（第 6 轮扩为逐实例分母承载包）**：(a) 三 handler `note` 叶 3 条路径 **75 实例**绑定（存在+取值，读点 `:350-351` 现为存在级；每 handler case 1 个 note：tileMark 20 + sleeveBloom 23 + beamSwap 32）或导出侧排除；(b) **六个 handler outcome/reason scoped 行的 116 个实例**（tileMark outcome 14 + tileMark reason 13 + sleeveBloom outcome 27 + sleeveBloom reason 27 + beamSwap outcome 17 + beamSwap reason 18；每 case 恰 1 个 outcome/reason 实例，实例数 = case 数；**第 4 轮曾误写"72 条路径"，R4-1 更正**）逐 case 绑定或显式排除——动作清单 = §3.4 分桶表非 A/C 桶：**D-α 46 例** 真实入口行为重放 + outcome/reason/requestException 逐值绑定（构造输入按 V4 exporter 源锚点 `V5CheatGoldenExporterTest.kt` 对应 case 构造段重建并留锚点注释，**不得声称"复用现有 goldenInputs"**，实测 tileMark 0/20、beamSwap 0/23；Allowed 主路径 6 例 #1/#53/#63/#66/#73/#75 另绑副作用/事件形状）；**D-β 4 例**（#5/#18/#19/#20：#19 在岗 deadline 等价类断言 + 映射注释 + owner 批准记录，#5/#18/#20 绑定 InvalidRequest 合并语义实际 outcome）；**B 2 例升级**（#6 ResponseWindowOpen 错误码逐值、#67 全链路重放）；**E 1 例绑定**（#36 sb-choose-first 以 fixture goldenInputs 构造并绑定 outcome=Allowed/reason=null） | 无 | GAP:N3-B8 = 3 路径 + 191 units（= 116 HC 实例 + 75 note 实例；CASE-DIST gap=B8 units=191；v3 曾记 119，R5-1 混合分母更正） |
| N3-B10 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`（仅测试） | **goldenInputs 按单元细化新缺口（第 5 轮新增，R4-2）**：`goldenInputs` 家族逐叶逐案例重划后暴露的 **28 个 UNBOUND 单元（13 叶路径）**——`seat` 6、`drawKind` 3、`drawnTileId` 3、`cost` 3、`turnNumber` 2、`offerCreatedTurnNumber` 2、`offerSecondWallIndex` 2、`nextAvailableWallIndex` 2、`keep` 1（sb-choose-first）、`nextWallTileId` 1、`nextDeadWallTileId` 1、`offerFirstTileId` 1、`offerSecondTileId` 1。同叶的已绑单元（30 个）保持现状锚点；逐单元绑定或导出侧显式排除。实证：3 个代表单元变异 16/16 PASS（§11.8 第 5 轮）；BOUND 面正控制 `sleeve-input-bound-control` 1 FAIL | 无 | GAP:N3-B10 = 13 路径 / 28 单元 |
| N3-B11 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`（仅测试） | **factsAfter 坐标未读实例（第 6 轮按 R5-2 真实读点重算，原 26 单元作废）**：两路径 34 实例中 6 已读（wall index 2 INTERNAL `:609-650` + m5 hand/discard seat 4 OUT_DIFF `:1016-1044`）、17 为 null 结构占位且无读点（hand/discard index 11 + wall/deadWall/replacement seat 6 → **UNA 不适用**，§1.4）、真缺口 **11 units** = `index` deadWall/replacement **4 实例**（m3/m4；`TileMovement_DeadWallLaws_ReplayThroughBeamSwapEntrypoint` :1051-1105 仅读 `container` 串不读坐标）+ `seat` 非 m5 hand/discard **7 实例**（其余 5 案例）。绑定 = 逐实例与 C# 实际落位对照或导出侧显式排除。实证：`m5-facts-hand-seat-control` 1 FAIL（OUT_DIFF 分区确设防）；null index 面 `m5-facts-unchecked-coordinate` 16/16 PASS（生效半支 = m5 hand/discard index 2 实例 null→999；wall seat→999 半支零命中 NOT_EXERCISED，§3.4） | 无 | GAP:N3-B11 = 2 路径 / 11 units |
| N3-B4 | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`（round 20 条）+ `CheatGoldenTests.cs`（tileMarkState 6 条） | round 20 条路径的断言明细 = N3-A1/A2；**tileMarkState 6 字段绑定**：`versionAfterFirstRegister`、`sameIdDifferentContentException`、`seatGroupLimitIsThree`、`seatGroupLimitException`、`seatTileLimitIsTwelve`、`createdRoundOrdinal` 改读导出值（替换/补足 :280-329 版本链断言未覆盖处） | 无（A1/A2 与本包 round 部分为同一工作的两个视图） | GAP:N3-B4 = 26 路径 / 35 units（round 29 = roundTerminal 14 + chankanRon 15，PATH-UNITS 复算；tileMarkState 6×1） |
| N3-B5 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | ledger 4 子键（conflictDifferentPayload/insufficientFunds/overflow/invalidSeat）的 `outcome`/`exception` 导出叶逐值绑定（8 条；探针 P4 证明当前不设防）；**第 4 轮新增（R3-3(g)）**：`snapshotRoundTrip.balancesEqual`/`entriesEqual` 两布尔（读点 `CheatGoldenTests.cs:261-262` `Assert.True(Bool(...))` 为信任级存在断言——翻布尔即 FAIL 但无计算值差分）升级为逐值对照（C# 实算 balance/entry 集合 vs 导出布尔） | 无 | GAP:N3-B5 = 10 路径 / 10 units |
| N3-B6 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` 或 fixture 导出侧（caller 裁决二选一） | **第 4 轮路径修正（R3-3(d)）**：`$.cases[*].v5.expected.mappingBasis`（导出锚 `V5AiGoldenExporterTest.kt:740-742`）+ `$.v5Observation.mappingLimits`/`mappingLimits[*]` 共 3 条路径：绑定（存在+取值断言）或导出侧显式排除声明——禁止沉默 | 无 | GAP:N3-B6 = 3 路径 / 11 units（mappingBasis 7 + mappingLimits 1 + mappingLimits[*] 3，PATH-UNITS 复算） |
| N3-B7 | `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | **第 4 轮新增**：`$.beamSwapHandler[*].requestException` 1 条路径——第 3 轮被 CONTAINER catch-all 吞并（§3.0.1 缺陷 (b)），机器重划显影为 UNBOUND；绑定 = beamSwap 异常 case 的 requestException 逐值断言或导出侧排除 | 无 | GAP:N3-B7 = 1 路径 / 1 unit |
| N3-B9 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` | **第 4 轮新增**：`$.cases[*].v4Call.action.text` + `$.cases[*].v4.events[*].text` 2 条路径（结构面已由 INTERNAL/recordedIntent 覆盖，仅 text 叶悬空）：绑定或导出侧排除 | 无 | GAP:N3-B9 = 2 路径 / 8 units（events[*].text 1 + action.text 7，PATH-UNITS 复算） |
| N3-C1 | `tests/v5-golden/catalog-v1.json` + `V5-0-GOLDEN-MANIFEST.md` | **caller 口径决定**：ai-v1 纳入 fixtures[]/manifest 并把 G-049 重算 partial（§3.6）；catalog caseTotal 口径 Δ29 是否声明分解（§5） | caller 裁决 | §3.6 / §5 / G-049 |
| N3-E1 | `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` | (a) 绑定 `winSamples[*].expected.status` = C# 重放实算 status；(b) 绑定 `winSamples[*].input.extraHan`（win 侧现无断言，noWin 侧 :250 已有）；(c) 绑定 `noWinSamples[*]` 的 `input.doraIndicators/uraDoraIndicators/honba/riichiSticks` 4 字段；(d) `winSamples[*].tags/tags[*]` 以**显式排除声明**处理（exporter 元数据：caller 认为须绑定则并入，否则测试内加显式注释使分母可见——禁止沉默） | 无 | GAP:N3-E1 = 6 路径 / 112 units（status 48 + extraHan 48 + noWin 4 字段 ×4，PATH-UNITS 复算；v3 曾按 1 路径 = 1 单元把 status/extraHan 各记 1——R5-1 混合分母作废） + tags 显式排除（META 10 条不进 GAP 分母） |
| N3-E2 | `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` | 24 条路径绑定：`expected.boundaries[*]`（11 条：越界样本逐值对照）、`expected.boundarySamples`（6 条）、`expected.invalid`（3 条：非法输入判定对照）、`expected.csharpExceptionMapping`（4 条：异常类型映射断言） | 无 | GAP:N3-E2 = 24 路径 / 98 units |
| N3-E3 | `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` | **第 4 轮新增**：5 条路径——`hashCrosswalk.csharpHashVerification`/`v4Reason`（双标签叶，SEM_MAP 3 条已绑 :95-:97，此 2 条悬空；变异探针 physical-hash-labels 证不设防）、`expected.suitOrder`、`scope.targetIds`/`targetIds[*]`（:73-:77 读 scope 5 条已绑，targetIds 悬空）：绑定或导出侧排除 | 无 | GAP:N3-E3 = 5 路径 / 10 units |
| N3-D1 | （P3 范围，非本包） | TableRuntime ScoreSettled 协议事件生产者 | P3.x | §6 判定 2 |

### 依赖 DAG

```
N3-A1 ─┐
N3-A2 ─┴─→ G-028a 关闭路径（P1.3, RoundReplayGoldenTests）──┐
N3-B1 ─┐                                                    │
N3-B2 ─┴─→ mv-swap 定性修正 + deadWallSegmentEqual（P1.4）──┤
N3-B3 ────→ cheat handler case 级 + sleeveBloom 六叶（P1.4）┤
N3-B4 ────→ round 路径视图 + tileMarkState 6（P1.3/P1.4）──┼─→ N-3 [MAJOR] 子项证据齐备
N3-B5 ────→ ledger 8 + snapshot 布尔 2（P1.4）─────────────┤
N3-B6 ────→ ai mappingBasis/Limits（P1.5）─────────────────┤
N3-B7 ────→ beamSwap requestException（P1.4）──────────────┤
N3-B8 ────→ 三 handler note 叶（P1.4）─────────────────────┤
N3-B9 ────→ ai text 叶（P1.5）─────────────────────────────┤
N3-E1 ────→ scoring 6 条路径 + tags 排除声明（P1.2）───────┤
N3-E2 ────→ physical 24 条路径（P1.1）─────────────────────┤
N3-E3 ────→ physical 5 条路径（P1.1）──────────────────────┤
                                                            │
N3-C1 ←─ caller 口径裁决（G-049 / caseTotal 口径）─────────┤
                                                            │
N3-D1（P3 TableRuntime，独立轨）───────────────────────────┴─→ G1 门的字段级证据基础
```

所有 N3-A*/B*/E* 互不依赖、可并行；N3-C1 是索引口径决定非代码；N3-D1 与 G1 无前置关系。**本包覆盖本报告所报全部可比较缺口（GAP 101 条路径逐一映射如上，清单可由 §11.6 机器复算）**；唯一"不直接绑定"项 = D-β #19 的在岗 deadline 表示（等价类 + 批准）、tags 元数据（显式排除声明）、ledger Entries 级对照（fixture 无 golden Entries 数组，需 exporter 扩导出——属 catalog 语义变更，caller 裁决）。三者均显式留痕，无沉默移出分母。

---
## 10. 侥幸通过（wrong-value-passes）判定汇总（第 6 轮复核版：累计 32 探针 = 不设防实证 23 + 正控制 9；第 5 轮 16 探针由 R5 TEMP `probes-round5.ps1` 驱动——含重跑第 4 轮 7 探针、逐探针期望校验、失败累计与负向自检，§11.8；第 6 轮按 R5-2 更正 P9 的生效变异范围 = m5 hand/discard index 2 实例，wall seat→999 半支零命中 NOT_EXERCISED，表 23/29 与 §11.8 P9 行）

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
| 11 | physical hashCrosswalk `csharpHashVerification`/`v4Reason` 双标签叶（GAP:N3-E3 的 2 条） | 两标签值改 WRONG | **不设防** | **第 4 轮探针 physical-hash-labels（第 5 轮重跑同判）**：9/9 PASS exit 0（fixture SHA `7B605C10…`，§11.8）——注：变异仅覆盖两标签，`scope.targetIds`/`expected.suitOrder` 未在变异范围，其不设防由 §9 N3-E3 静态读点（`:73-:77` 读 scope 5 条、targetIds 零引用）证实 |
| 12 | `$.beamSwapHandler[*].requestException`（GAP:N3-B7） | requestException 篡改为 WRONG | **不设防** | **第 4 轮探针 cheat-request-exception**：16/16 PASS exit 0（cheat 绑定面正控制由 §10 表 18 #23 探针承担） |
| 13 | UNBOUND_HC scoped case 的 reason（tm-wrong-phase-preparation 拆分行） | 改 NOT_YOUR_TURN | **不设防** | **第 4 轮探针 cheat-unbound-reason**：16/16 PASS exit 0 |
| 14 | sleeveBloom goldenInputs 六叶（GAP:N3-B3） | 六叶逐 case 在场者改 WRONG（字符串叶）/999（数值叶），非整体删除 | **不设防** | **第 4 轮探针 cheat-six-goldeninputs（第 5 轮重跑同判）**：16/16 PASS exit 0 |
| 15 | m1b cross 字段（INTERNAL 判级复核） | `mv-swap-drawn-with-wall-backfill` 的 `resolvedIndex`/`backfilledIndex`/`factsAfter[index]` 三值 +100 | **不设防（=INTERNAL 无生产语义的实证）** | **第 4 轮探针 cheat-backfill-coherent（第 5 轮重跑同判）**：16/16 PASS exit 0——与 §3.4 INTERNAL 判级一致，非 GAP |
| 16 | `$.ledger.snapshotRoundTrip.balancesEqual`/`entriesEqual`（信任级） | 翻布尔 | **半设防（存在级）** | 读点 `:261-262` `Assert.True(Bool(...))` 翻值即 FAIL，但无计算值差分 → 绑定任务 N3-B5 逐值对照 |
| 17 | **正控制①**：已绑定 totalPoints | 2000→123456789 | **设防（1 FAIL exit 1）** | 第 2 轮探针 positive-control：Total:7, Failed:1 |
| 18 | **正控制②**：sb-activate-already-used（A 桶 #23） | reason→WRONG_PHASE | **设防（1 FAIL exit 1）** | **第 3 轮探针 P1**：`Handler_SleeveBloom_RepresentativeCases_ReplayAgainstCheatSubmission [FAIL]`（Assert.Equal Failure @ CheatGoldenTests.cs:557）——证明 :486-490 ReasonMap 绑定真实存在，#23 = A 桶 |
| 19 | **正控制③**：physical samples[0].expected.tiles[0].rank | rank→99 | **设防（1 FAIL exit 1）** | **第 4 轮探针 physical-boundary-control**（fixture SHA `99DA2673…`） |
| 20 | **正控制④**：ai decisionClock | `baseMillis`/`reserveMillis`→999 | **设防（1 FAIL exit 1）** | **第 4 轮探针 ai-clock-metadata（第 5 轮重跑同判，fixture SHA `62838FE1…`）**（原拟缺口假设被探针推翻 → decisionClock 8 路径全绑定，§3.6） |
| 21 | sb-choose-second goldenInputs 相对回合对（`offerCreatedTurnNumber`+`turnNumber`，INTERNAL 判级复核） | 关系对双侧 +100（保持自洽） | **不设防（=内部相对关系非真输入绑定的实证）** | **第 5 轮探针 sleeve-input-relative-turns**：16/16 PASS exit 0（§11.8） |
| 22 | sb-choose-second goldenInputs 相对实体对（`drawnTileId`+`offerFirstTileId`，INTERNAL 判级复核） | 关系对双侧 +1（保持自洽） | **不设防** | **第 5 轮探针 sleeve-input-relative-entities**：16/16 PASS exit 0（§11.8） |
| 23 | m5（`mv-reservation-delivery-then-beam`）factsAfter 坐标（GAP:N3-B11 邻接面；第 6 轮 R5-2 更正口径） | hand/discard 分区 `index`→999 + wall 分区 `seat`→999 | **不设防（= m5 hand/discard null index 无读点的实证；wall 半支 NOT_EXERCISED）** | **第 5 轮探针 m5-facts-unchecked-coordinate；第 6 轮更正**：实际生效变异 = m5 hand/discard `index` **2 实例 null→999** → 16/16 PASS exit 0（证 null index 坐标无读点，判 UNA 非"排除"）；**wall 分区 `seat`→999 半支零命中**——m5 fixture 仅含 hand/discard factsAfter（wall=0），该变异未触及任何冻结样本（NOT_EXERCISED），不构成 wall 面不设防证据；第 5 轮"wall seat→999 已实测不设防"表述作废（§11.8 P9 行逐项） |
| 24 | B10 代表单元①：sb-choose-second `cost` | cost→999 | **不设防** | **第 5 轮探针 sleeve-unbound-cost-choose-second**：16/16 PASS exit 0（§11.8） |
| 25 | B10 代表单元②：sb-choose-first `seat` | seat→999 | **不设防** | **第 5 轮探针 sleeve-unbound-seat-choose-first**：16/16 PASS exit 0（§11.8） |
| 26 | B10 代表单元③：sb-choose-second `drawKind` | drawKind→WRONG | **不设防** | **第 5 轮探针 sleeve-unbound-drawkind-choose-second**：16/16 PASS exit 0（§11.8） |
| 27 | **正控制⑤**：sb-choose-second 相对回合对单侧破坏 | 仅 `offerCreatedTurnNumber` +100（破坏自洽） | **设防（1 FAIL exit 1）** | **第 5 轮探针 sleeve-input-relation-break**：失败点 = fixture 自检断言 `SleeveBloomSecondChoice_BindsToGoldenInputs`（CheatGoldenTests.cs:724，Expected 112/Actual 12）——证该对存在 fixture 级自洽守卫，与 INTERNAL（无生产语义锚、双侧 +100 不设防，表 21）判级一致 |
| 28 | **正控制⑥**：sb-activate-allowed `cost`（已绑 OUT_DIFF/SEM_MAP 面） | cost→999 | **设防（1 FAIL exit 1）** | **第 5 轮探针 sleeve-input-bound-control**：失败点 `SleeveBloomSecondChoice_BindsToGoldenInputs`（fixture SHA `1E24FFEE…`，§11.8） |
| 29 | **正控制⑦**：m5 factsAfter hand/discard 分区 `seat`（已绑 OUT_DIFF 面） | seat→999 | **设防（1 FAIL exit 1）** | **第 5 轮探针 m5-facts-hand-seat-control**：失败点 `DeliveryThenBeam_BindsToGoldenCandidateEntities`（生效变异 = m5 hand/discard `seat` 2 实例）——证 hand/discard 分区 seat 已逐值对照；真缺口仅 `index` deadWall/replacement 4 实例 + 非 m5 `seat` 7 实例（GAP:N3-B11 = 11 units，§3.4/§9；v3"仅 index 与 wall 分区坐标悬空"中 wall 半句按 R5-2 更正——wall seat 无冻结样本，属 NOT_EXERCISED 非"悬空"） |
| 30 | physical samples/scope 已绑 5 条、scoring 其余绑定组、shuffle 三矩阵、round version/scores/deltas/rejected、ledger 差分子集、tileMarkState 版本链 17 条 | 数值/结构错值 | 设防（逐值断言 + 分布对账 + 冻结硬值双锚，§3 逐字段表） | 45/45 PASS 维持 |

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

### 11.4b 逐实例清单 CensusInstances（第 6 轮新增工具；R5-1 单元真值的独立枚举源）

**第 5 轮审查缺陷 R5-1 的机制性根源**：v3 校验器的 CASE-DIST 把 scoped 家族按（case，路径出现实例）展开、却把每个 unscoped 家族按"1 条归一化路径 = 1 单元"进账（`$.winSamples[*].expected.status` 的 48 个真实导出叶只计 1、`$.cases[*].deadWallSegmentEqual` 的 32 个实例只计 1）——单元分母由此成为"部分实例 + 部分路径"的混合量。修复 = **全族逐实例枚举**：新增 `CensusInstances`（与 FullCensus 同构的 C# net8.0 工具，System.Text.Json，无第三方包），对 6 份 fixture 的**每一个 JSON 节点出现实例**（非归一化路径、无抽样）产出行式清单，校验器 v4（§11.6）据其对**所有**族（scoped 与 unscoped 一致）做逐实例判级。

**清单格式**（`instances.tsv`，TAB 分隔，6 列）：`fixture / path / caseId / idx / kind / parent`——`path` 与 FullCensus 归一化完全一致（`$`、`.prop`、每层数组 `[*]`）；`caseId` = 该节点所属**根层数组对象段**元素的 `caseId`（或 `id`）属性，两属性皆缺时合成 `<section>[i]`，fixture 级节点（含根）记 `-`；`idx` = 同一 (fixture, path, caseId) 内 1 起出现序号；`kind` = object|array|string|number|boolean|null（与 census 同词表，JSON `null` 叶计实例同口径）；`parent` = **直接父对象的自身标量属性** `k=v;k2=v2`（名排序、null 记 `k=null`、父为根/数组/缺省记 `-`）——这是 `;where=` 谓词的真实求值输入（R5-2 修复面）。工具源全文内嵌如下（实测 SHA256：csproj `9bbd92e2df91ce7a14cf506306dc186ff2a4d3613173d66c65adcddeaf0dff6a`、Program.cs `10c03a4e8c172ed3122587995d4f6d2ed50dba4ea03c0b6d202223c479097ec6`；提取命令见 §11.10）：

```xml
<!--TOOL:censusinstances-csproj:BEGIN-->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>CensusInstances</AssemblyName>
    <RootNamespace>CensusInstances</RootNamespace>
  </PropertyGroup>
</Project>
<!--TOOL:censusinstances-csproj:END-->
```

```csharp
<!--TOOL:censusinstances-program:BEGIN-->
// CensusInstances — N-3 golden field audit round-6 rework evidence tool (R5-1/R5-2).
// Companion to FullCensus: while FullCensus aggregates per normalized path,
// CensusInstances emits the PER-INSTANCE manifest the unit-truth statistics
// require — one row for EVERY JSON node occurrence of every fixture, so the
// checker can expand unscoped families per instance too (a normalized path
// with N occurrences contributes N units, not 1):
//   instances.tsv   one line per node occurrence, TAB-separated:
//     fixture <TAB> path <TAB> caseId <TAB> idx <TAB> kind <TAB> parent
//     path    identical normalization to FullCensus ($, .prop, [*] per level)
//     caseId  caseId (or id) property of the enclosing ROOT-LEVEL array element
//             whose elements are objects; synthetic "<section>[i]" when the
//             element carries neither; "-" for fixture-level nodes (incl. root)
//     idx     1-based occurrence index among rows sharing (fixture,path,caseId)
//     kind    object|array|string|number|boolean|null (same vocabulary as census)
//     parent  immediate parent OBJECT's own scalar properties as k=v;k2=v2
//             (names sorted; null values as k=null; "-" when parent is the
//             root object, an array, or absent) — this is what where= preds read
//   manifest-summary.txt  per-fixture nodes/leaves/cases counts
// Exit codes: 0 = all fixtures enumerated; 2 = usage; 3 = missing fixture file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CensusInstances
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: CensusInstances <repoRoot> <outDir>");
                return 2;
            }
            string repoRoot = args[0];
            string outDir = args[1];
            Directory.CreateDirectory(outDir);

            string[] fixtures = { "physical-v1", "scoring-v1", "round-v1", "cheat-v1", "shuffle-v1", "ai-v1" };
            var summary = new List<string>();
            int failures = 0;

            using (var manifest = new StreamWriter(Path.Combine(outDir, "instances.tsv"), false, new UTF8Encoding(false)))
            {
                foreach (string name in fixtures)
                {
                    string file = Path.Combine(repoRoot, "tests", "v5-golden", name + ".json");
                    if (!File.Exists(file))
                    {
                        summary.Add("MISSING " + name);
                        failures++;
                        continue;
                    }
                    long nodes = 0, leaves = 0;
                    var cases = new SortedSet<string>(StringComparer.Ordinal);
                    var idx = new Dictionary<string, int>(StringComparer.Ordinal);
                    using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file)))
                    {
                        WalkRoot(doc.RootElement, name, manifest, idx, ref nodes, ref leaves, cases);
                    }
                    summary.Add(name + ": nodes=" + nodes + " leaves=" + leaves + " cases=" + cases.Count);
                }
            }

            File.WriteAllLines(Path.Combine(outDir, "manifest-summary.txt"), summary, new UTF8Encoding(false));
            foreach (string s in summary) Console.WriteLine(s);
            Console.WriteLine("INSTANCE-MANIFEST-DONE failures=" + failures);
            return failures == 0 ? 0 : 3;
        }

        // Root pass: direct-child arrays of the root object whose elements are
        // objects are case sections; every element gets a case key. All other
        // nodes are fixture-level ("-" case).
        static void WalkRoot(JsonElement root, string fx, StreamWriter w,
            Dictionary<string, int> idx, ref long nodes, ref long leaves, SortedSet<string> cases)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                Emit(root, fx, "$", "-", w, idx, null, ref nodes, ref leaves, cases);
                return;
            }
            Emit(root, fx, "$", "-", w, idx, null, ref nodes, ref leaves, cases);
            foreach (var p in root.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array && ArrayOfObjects(p.Value))
                {
                    Emit(p.Value, fx, "$." + p.Name, "-", w, idx, ScalarFields(root), ref nodes, ref leaves, cases);
                    int i = 0;
                    foreach (var el in p.Value.EnumerateArray())
                    {
                        string cid;
                        if (el.ValueKind == JsonValueKind.Object)
                        {
                            cid = FirstProp(el, "caseId");
                            if (cid == null) cid = FirstProp(el, "id");
                            if (cid == null) cid = p.Name + "[" + i + "]";
                        }
                        else cid = p.Name + "[" + i + "]";
                        cases.Add(cid);
                        Emit(el, fx, "$." + p.Name + "[*]", cid, w, idx, "-", ref nodes, ref leaves, cases);
                        if (el.ValueKind == JsonValueKind.Object) WalkObject(el, "$." + p.Name + "[*]", fx, cid, w, idx, ref nodes, ref leaves, cases);
                        else if (el.ValueKind == JsonValueKind.Array) WalkArray(el, "$." + p.Name + "[*]", fx, cid, w, idx, ref nodes, ref leaves, cases);
                        i++;
                    }
                }
                else
                {
                    Emit(p.Value, fx, "$." + p.Name, "-", w, idx, ScalarFields(root), ref nodes, ref leaves, cases);
                    if (p.Value.ValueKind == JsonValueKind.Object) WalkObject(p.Value, "$." + p.Name, fx, "-", w, idx, ref nodes, ref leaves, cases);
                    else if (p.Value.ValueKind == JsonValueKind.Array) WalkArray(p.Value, "$." + p.Name, fx, "-", w, idx, ref nodes, ref leaves, cases);
                }
            }
        }

        static bool ArrayOfObjects(JsonElement arr)
        {
            foreach (var el in arr.EnumerateArray()) return el.ValueKind == JsonValueKind.Object;
            return false;
        }

        static string FirstProp(JsonElement obj, string name)
        {
            foreach (var p in obj.EnumerateObject())
                if (p.Name == name && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            return null;
        }

        static void WalkObject(JsonElement obj, string path, string fx, string cid, StreamWriter w,
            Dictionary<string, int> idx, ref long nodes, ref long leaves, SortedSet<string> cases)
        {
            string myFields = ScalarFields(obj);
            foreach (var p in obj.EnumerateObject())
            {
                string childPath = path + "." + p.Name;
                Emit(p.Value, fx, childPath, cid, w, idx, myFields, ref nodes, ref leaves, cases);
                if (p.Value.ValueKind == JsonValueKind.Object) WalkObject(p.Value, childPath, fx, cid, w, idx, ref nodes, ref leaves, cases);
                else if (p.Value.ValueKind == JsonValueKind.Array) WalkArray(p.Value, childPath, fx, cid, w, idx, ref nodes, ref leaves, cases);
            }
        }

        static void WalkArray(JsonElement arr, string path, string fx, string cid, StreamWriter w,
            Dictionary<string, int> idx, ref long nodes, ref long leaves, SortedSet<string> cases)
        {
            string childPath = path + "[*]";
            foreach (var el in arr.EnumerateArray())
            {
                Emit(el, fx, childPath, cid, w, idx, "-", ref nodes, ref leaves, cases);
                if (el.ValueKind == JsonValueKind.Object) WalkObject(el, childPath, fx, cid, w, idx, ref nodes, ref leaves, cases);
                else if (el.ValueKind == JsonValueKind.Array) WalkArray(el, childPath, fx, cid, w, idx, ref nodes, ref leaves, cases);
            }
        }

        // The immediate parent OBJECT's own scalar properties, sorted by name.
        static string ScalarFields(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return "-";
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object && p.Value.ValueKind != JsonValueKind.Array) keys.Add(p.Name);
            }
            if (keys.Count == 0) return "-";
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                if (sb.Length > 0) sb.Append(';');
                foreach (var p in obj.EnumerateObject())
                {
                    if (p.Name != k) continue;
                    sb.Append(k).Append('=');
                    switch (p.Value.ValueKind)
                    {
                        case JsonValueKind.Null: sb.Append("null"); break;
                        case JsonValueKind.String: sb.Append(p.Value.GetString()); break;
                        case JsonValueKind.Number: sb.Append(p.Value.GetRawText()); break;
                        case JsonValueKind.True: sb.Append("true"); break;
                        case JsonValueKind.False: sb.Append("false"); break;
                    }
                    break;
                }
            }
            return sb.ToString();
        }

        static void Emit(JsonElement e, string fx, string path, string cid, StreamWriter w,
            Dictionary<string, int> idx, string parentFields, ref long nodes, ref long leaves, SortedSet<string> cases)
        {
            string kind;
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: kind = "object"; break;
                case JsonValueKind.Array: kind = "array"; break;
                case JsonValueKind.String: kind = "string"; break;
                case JsonValueKind.Number: kind = "number"; break;
                case JsonValueKind.True:
                case JsonValueKind.False: kind = "boolean"; break;
                default: kind = "null"; break;
            }
            nodes++;
            if (kind != "object" && kind != "array") leaves++;
            string key = fx + '\u0001' + path + '\u0001' + cid;
            int n;
            idx.TryGetValue(key, out n);
            idx[key] = n + 1;
            w.Write(fx); w.Write('\t');
            w.Write(path); w.Write('\t');
            w.Write(cid); w.Write('\t');
            w.Write(n + 1); w.Write('\t');
            w.Write(kind); w.Write('\t');
            w.Write(parentFields == null ? "-" : parentFields);
            w.Write('\n');
        }
    }
}
<!--TOOL:censusinstances-program:END-->
```

**实测运行（R6 TEMP `C:/Users/sncdc/AppData/Local/Temp/n3-rework-round6/CensusInstances/`）**：

```
D:/Unity/dotnet/dotnet.exe run --project CensusInstances.csproj -- D:/majiang-wt/n3-golden-field-audit ../inst-out 1> ../inst-run.log 2>&1
→ EXIT=0；六行 nodes/leaves/cases + 尾行 INSTANCE-MANIFEST-DONE failures=0（inst-run.log SHA256 f075dd6da4ecb5ef3e3d161f462fc34af58dc71ca8ef5ddb19b9118f6810914e）
```

**产物**：`instances.tsv` **655,145 行**（SHA256 `049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12`），逐 fixture 节点数 = physical **34,718** / scoring **592,640** / round **1,474** / cheat **1,443** / shuffle **23,432** / ai **1,438**（合计 655,145 = §3.0.1 单元总分的机器来源）；`manifest-summary.txt`（SHA256 `a7aa8f3dd13afbc5fb332e197356f74b3b204e8a10cc571a9cdad9664ea577a2`）。**交叉自洽**：manifest 逐 fixture 叶实例数（29,750 / 476,153 / 1,163 / 1,163 / 23,016 / 1,199）与 FullCensus `census-summary.txt` 的 `leafInstances` **逐一相等**；案例数（14 / 10,238 / 22 / 81 / 48 / 20）与 §5 census 案例口径一致。校验器 v4 另在运行中强制 manifest↔census 双向路径集合等式（`MANIFEST-CENSUS-MISMATCH`，§11.6）。

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

### 11.6 层 2 语义映射校验 verify-mapping v4（工具源内嵌；R5-1/R5-2 验收：正检 exit 0 + 15 负例全部检出 + 单元真值/逐实例换序不变 + 第 3/4/5 轮 caller 全部 checker 反例堵死）

工具源码全文内嵌如下（**v5 实测 SHA256 `b094d2a8540877d199ee1dc99753cf6c6675bc1534c60c533ca1385cf9f8d51f`，648 行**；规则 TSV 以 §3.0.1 内嵌块为准——本报告为 LF 行尾，提取副本 SHA256 `24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31`，与 R7 TEMP 原始产物 `rules-v5.tsv`（LF + UTF-8 BOM）**逐字节相等**。第 7 轮 v5 相对 v4 的两处硬化，见 §15.5：**① CASE-MIXED 以 (fixture,path) 为键并显式输出 `fx=`（R6-1）**——v4 的 `$pathClasses` 仅用路径作键，unscoped 类写入后不同 JSON 文档的同名路径被跨 fixture 合并（34 条混合中 7 条为假混合，如 round-v1 `$.decisionClock`=SEM_MAP 与 ai-v1 `$.decisionClock`=CONTAINER 各自 fixture 内均不混合）；v5 全部混合路径汇总改 (fixture,path) 键，真混合 **27 条全部 cheat-v1**，独立 oracle 重算逐一相符（§11.6 mixed-namespace-oracle）。**② UNA-NOT-NULL 成为全分支共享不变量（R6-2）**——v4 仅在 scoped 分支安装 kind=null 守卫，unscoped 行可把真实非空叶记为 UNA 并移出缺口分母（caller 复现：48 个 status 字符串实例 → una-errors=0、gap 592→544、exit 0）；v5 对 scoped 与 unscoped 的每一被认领实例施加同一守卫，且 UNA-NOT-NULL 错误**不设 40 条打印上限**逐实例全量公布。历史版本：v4 SHA256 `190de233…` 618 行见 git `49592ec`；v3 `414b042e…` 见 git `da0155a`）。

**v4 相对 v3 的四项加固（对应 R5-1/R5-2）**：(1) **全族逐实例单元真值（R5-1）**——unit 全集改由独立工具 `CensusInstances`（§11.4b）的实例清单（instances.tsv，655,145 行）驱动：**scoped 与 unscoped 家族一律逐实例判级**（v3 对 unscoped 家族按"1 路径 = 1 单元"进账——48 个 winSamples status 实例只计 1、32 个 deadWallSegmentEqual 实例只计 1，CASE-TOTAL 1821/286 为混合分母，全部作废）；校验器强制 manifest↔census 双向路径集合等式（`MANIFEST-CENSUS-MISMATCH`）与每实例恰认领一次，输出 `UNITS fx=`（逐 fixture 实例数）、`PATH-UNITS`（1516 行逐路径实例与 owners）、`CASE-TOTAL units=655145 gap-units=592 mixed-paths=34`【第 7 轮更正：v4 的 34 含 7 条跨 fixture 假混合，v5 fx 键后 27，§15.5 R6-1】；(2) **where 谓词成为数据面（R5-2）**——谓词按 unit 的**实例父对象标量字段**（manifest `parent` 列）求值，factsAfter `index`/`seat` 由 v3 的 4 条整分区行拆为 **6 条 where 行**（wall index INTERNAL 2 / hand·discard null index UNA 11 / deadWall·replacement index GAP:N3-B11 4 / m5 hand·discard seat OUT_DIFF 4 / 其余 hand·discard seat GAP:N3-B11 7 / wall·deadWall·replacement null seat UNA 6）；(3) **新增第 9 判级类 UNA + `UNA-NOT-NULL` 校验**——"导出 null 结构占位且源码无读点"的实例显式"不适用"（17 units，不计入缺口分母），UNA 行认领非 null 实例即报错【第 7 轮更正：v4 的该守卫仅装在 scoped 分支、unscoped 行绕过（R6-2），v5 升级为全分支共享不变量，§15.5】；(4) **NOT_EXERCISED 与"未比较"严格区分（R5-2 验收）**——"源码分支存在但冻结样本 0 个"（如 m5 消费器 wall/deadWall/replacement 分支）不再被表述为"显式排除"或"已实测"，正文（§3.4/§10/§11.8）逐处更正，探针证据按**实际生效变异**记账。

```powershell
<!--TOOL:verify-mapping:BEGIN-->
# N-3 round-7 rework: semantic field-mapping rule checker v5 (R6-1/R6-2).
# v5 hardening over v4, driven by the round-6 review:
#   1. CASE-MIXED is fixture-qualified (R6-1): v4 keyed $pathClasses by the
#      normalized path alone, so same-named paths in DIFFERENT JSON documents
#      (fixtures) were merged once unscoped families began writing there in
#      v4 — 34 reported mixes contained 7 false cross-fixture ones (e.g.
#      round-v1 $.decisionClock SEM_MAP vs ai-v1 $.decisionClock CONTAINER,
#      neither mixed within its own fixture). v5 keys the mixed-path
#      aggregation by (fixture, path) and prints
#      'CASE-MIXED fx=<fx> path=<path> classes=<sorted>' (true mixes: 27,
#      all cheat-v1). No cross-document merge may be reported as mixing.
#   2. UNA-NOT-NULL is a shared invariant (R6-2): v4 installed the
#      kind=null guard only on the scoped branch, so an unscoped row could
#      claim real non-null leaves as UNA and silently move them out of the
#      gap denominator (caller repro: 48 winSamples status string units ->
#      una-errors=0, gap-units 592->544, exit 0). v5 applies the same
#      kind=null guard to EVERY claimed instance, scoped or unscoped; the
#      error names fixture|pattern, case, path, idx and real kind.
# v4 hardening over v3 (round-6 delivery, historical):
#   1. TRUE per-instance unit denominator (R5-1): v3 expanded only scoped
#      families per (case,path) node and tallied every unscoped family as 1
#      unit per normalized census path — a mixed statistic that compressed
#      e.g. 48 winSamples status leaves or 32 deadWallSegmentEqual instances
#      into 1. v4 consumes the CensusInstances per-instance manifest
#      (instances.tsv: every JSON node occurrence of every fixture) and
#      tallies EVERY unit — scoped and unscoped alike — as one
#      (fixture, case, path, occurrence) unit classed by its actually
#      claiming row. CASE-TOTAL/CASE-DIST are now the complete instance
#      truth; the first-match DIST path projection is kept unchanged as the
#      independent historical path scope (DIST-FX per-fixture lines added).
#   2. where predicates are now DATA, not just grammar (R5-2): the
#      factsAfter[*].index/seat families are split per container with real
#      ;where= rows, and the per-instance adjudication reads each unit's
#      parent object fields from the manifest, so a predicate that matches
#      nothing surfaces as SCOPE-UNIT-UNCOVERED and a UNA row claiming a
#      non-null instance surfaces as UNA-NOT-NULL.
#   3. UNA class (9th): "not applicable" structural placeholders — exported
#      JSON-null leaves that no consumer reads. UNA units stay inside the
#      unit totals (nothing is silently dropped) but carry owner UNA:* and
#      are machine-verified to be null (kind=null); they are NOT gap units.
#   Retained from v3: -Repo real line-range anchors, DUP, SCALAR-AS-CONTAINER,
#   ordered first-match path projection, SCOPE-MIXED, six SCOPE errors,
#   sibling-reorder-invariant unit adjudication (scoped sibling rows must be
#   disjoint; overlap = error, so unit classes cannot depend on row order).
#   Modes:
#   check    -RulesFile <tsv> | -ReportPath <md> -CensusDir <dir> -Repo <path>
#            (CensusDir must contain <fx>-full-paths.txt and instances.tsv
#             produced by FullCensus + CensusInstances)
#   emit     -PatternsFile <tsv> -CensusDir <dir> -OutFile <tsv>
#   negative -Kind strip-anchors|drop-rule|anchorless-rule|nonexistent-anchors|
#                  duplicate-rule|scalar-container|scope-extra-case|scope-drop-case|
#                  scope-unknown-case|scope-dup-case|scope-cross-section|
#                  scope-where-broken|where-value-broken|una-drop|una-nonnull|
#                  una-unscoped-nonnull
#            -RulesFile <tsv> -CensusDir <dir> -Repo <path>
#            (expect the mutated check to FAIL; exit 0 when it fails = detected)
param([string]$Mode, [string]$RulesFile, [string]$PatternsFile, [string]$ReportPath,
      [string]$CensusDir, [string]$OutFile, [string]$Kind,
      [string]$Repo, [string]$Evidence)
$ErrorActionPreference = 'Stop'
if (-not $CensusDir -and $Evidence) { $CensusDir = Join-Path $Evidence 'census-out' }
$fixtures = @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')
$validClass = @('CONTAINER','IN_BIND','OUT_DIFF','INTERNAL','SEM_MAP','UNBOUND','UNBOUND_HC','META','UNA')
$classList = @('CONTAINER','IN_BIND','INTERNAL','META','OUT_DIFF','SEM_MAP','UNBOUND','UNBOUND_HC','UNA')

function Load-Census([string]$fx) {
  $list = New-Object 'System.Collections.Generic.List[object]'
  foreach ($line in Get-Content (Join-Path $CensusDir ($fx + '-full-paths.txt'))) {
    $sp = $line -split ' '
    [void]$list.Add([pscustomobject]@{ path = $sp[0]; kind = $sp[1] })
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
    $scope = '*'
    if ($c.Count -ge 8 -and $c[7]) { $scope = $c[7] }
    $rules.Add([pscustomobject]@{
      fixture = $c[0]; pattern = $c[1]; paths = if ($c[2] -match '^\d+$') { [int]$c[2] } else { -1 }
      cls = $c[3]; exporter = $c[4]; cs = $c[5]; owner = $c[6]; scope = $scope
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

# ---- repo-backed anchor validation ----
$fileCache = @{}
function Resolve-RepoFile([string]$name, [string]$kind) {
  if ($fileCache.ContainsKey($name)) { return $fileCache[$name] }
  $root = if ($kind -eq 'kt') { Join-Path $Repo 'engine/src/test' } else { Join-Path $Repo 'dotnet/tests' }
  $hit = $null
  if (Test-Path $root) {
    $hit = Get-ChildItem -Path $root -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -notmatch '\\(bin|obj|build)\\' } | Select-Object -First 1
  }
  $fileCache[$name] = $hit
  return $hit
}
function Test-FileLineAnchor([string]$anchor, [string]$kind, [string]$fixture, [string]$pattern, [string]$colName, $errors) {
  if ($anchor -notmatch '^\S+\.(kt|cs):(\d+)(-(\d+))?$') {
    [void]$errors.Add($colName + ' ' + $fixture + ' ' + $pattern + ' anchor lacks line range: ' + $anchor)
    return
  }
  $name = ($anchor -split ':')[0]
  $m = [regex]::Match($anchor, ':(\d+)(-(\d+))?$')
  $l1 = [int]$m.Groups[1].Value
  $l2 = if ($m.Groups[3].Success) { [int]$m.Groups[3].Value } else { $l1 }
  $f = Resolve-RepoFile $name $kind
  if ($null -eq $f) { [void]$errors.Add($colName + ' ' + $fixture + ' ' + $pattern + ' file not found under repo: ' + $name); return }
  $n = ([IO.File]::ReadAllLines($f.FullName)).Count
  if ($l1 -lt 1 -or $l2 -lt $l1 -or $l2 -gt $n) {
    [void]$errors.Add($colName + ' ' + $fixture + ' ' + $pattern + ' line range out of bounds (' + $l1 + '-' + $l2 + ', file=' + $n + '): ' + $name)
  }
}

# ---- scope grammar: case=<id>|...[;where=<field>==<v1>|<v2>] ----
function Parse-Scope([string]$scope) {
  if ($scope -eq '*') { return $null }
  if ($scope -notmatch '^case=[A-Za-z0-9._\[\]-]+(\|[A-Za-z0-9._\[\]-]+)*((;where=)[A-Za-z0-9_]+==[A-Za-z0-9._-]+(\|[A-Za-z0-9._-]+)*)?$') { return $null }
  $parts = $scope -split ';'
  $ids = $parts[0].Substring(5) -split '\|'
  $wf = ''; $wv = @()
  if ($parts.Count -gt 1) {
    $w = ($parts[1].Substring(6)) -split '=='
    $wf = $w[0]
    $wv = $w[1] -split '\|'
  }
  return [pscustomobject]@{ ids = $ids; whereField = $wf; whereVals = $wv }
}
# where test against the manifest parent string ("k=v;k2=v2" or "-")
function Test-WhereStr([string]$parent, [string]$field, [string[]]$vals) {
  if (-not $parent -or $parent -eq '-') { return $false }
  $prefix = $field + '='
  foreach ($seg in $parent.Split(';')) {
    if ($seg.StartsWith($prefix, [StringComparison]::Ordinal)) {
      return ($vals -ccontains $seg.Substring($prefix.Length))
    }
  }
  return $false
}

function Invoke-Check($rules) {
  $errors = New-Object System.Collections.Generic.List[string]
  $totalPaths = 0
  # duplicate (fixture,pattern,scope) rejection
  $seen = @{}
  foreach ($r in $rules) {
    $k = $r.fixture + '|' + $r.pattern + '|' + $r.scope
    if ($seen.ContainsKey($k)) { [void]$errors.Add('DUP ' + $r.fixture + ' ' + $r.pattern + ' scope=' + $r.scope) }
    else { $seen[$k] = $true }
  }
  # a family (fixture,pattern) must not mix scope='*' rows with scoped rows
  $famStyle = @{}
  foreach ($r in $rules) {
    $gk = $r.fixture + '|' + $r.pattern
    if (-not $famStyle.ContainsKey($gk)) { $famStyle[$gk] = '' }
    if ($r.scope -eq '*') { $famStyle[$gk] = $famStyle[$gk] + 'S' } else { $famStyle[$gk] = $famStyle[$gk] + 'C' }
  }
  foreach ($gk in $famStyle.Keys) {
    if ($famStyle[$gk].Contains('S') -and $famStyle[$gk].Contains('C')) {
      [void]$errors.Add('SCOPE-MIXED ' + $gk + ' family mixes unscoped and scoped rows')
    }
  }
  # ---- pass A: per census path, first-match owning family + path projection (unchanged) ----
  $classDist = @{}; $gapDist = @{}
  $classDistFx = @{}; $gapDistFx = @{}
  $totalUnits = 0
  $mixedPaths = New-Object System.Collections.Generic.List[string]
  $pathOwner = @{}; $famResidual = @{}
  $manifestPaths = @{}; $caseUniverse = @{}; $fxNodes = @{}
  foreach ($fx in $fixtures) {
    $pathOwner[$fx] = @{}; $manifestPaths[$fx] = @{}; $caseUniverse[$fx] = @{}; $fxNodes[$fx] = 0
    $classDistFx[$fx] = @{}; $gapDistFx[$fx] = @{}
  }
  $famLines = @{}   # fixture|pattern -> List of raw manifest lines
  foreach ($fx in $fixtures) {
    $fr = @($rules | Where-Object { $_.fixture -eq $fx })
    if ($fr.Count -eq 0) { $errors.Add('no rules for ' + $fx); continue }
    $paths = Load-Census $fx
    $totalPaths += $paths.Count
    $residual = @{}
    $leak = 0; $scalarAsContainer = 0
    foreach ($p in $paths) {
      $owned = $null
      for ($i = 0; $i -lt $fr.Count; $i++) { if (Test-Match $fr[$i].pattern $p.path) { $owned = $fr[$i]; break } }
      if ($null -eq $owned) { $errors.Add('LEAK ' + $fx + ' ' + $p.path); $leak++; continue }
      if (-not $residual.ContainsKey($owned.pattern)) { $residual[$owned.pattern] = 0 }
      $residual[$owned.pattern]++
      $pathOwner[$fx][$p.path] = $owned.pattern
      $ck = $owned.cls
      if (-not $classDist.ContainsKey($ck)) { $classDist[$ck] = 0 }
      $classDist[$ck]++
      if (-not $classDistFx[$fx].ContainsKey($ck)) { $classDistFx[$fx][$ck] = 0 }
      $classDistFx[$fx][$ck]++
      if ($owned.owner -match '^GAP:') {
        if (-not $gapDist.ContainsKey($owned.owner)) { $gapDist[$owned.owner] = 0 }
        $gapDist[$owned.owner]++
        if (-not $gapDistFx[$fx].ContainsKey($owned.owner)) { $gapDistFx[$fx][$owned.owner] = 0 }
        $gapDistFx[$fx][$owned.owner]++
      }
      if ($owned.cls -eq 'CONTAINER' -and $p.kind -notmatch '^(object|array)') {
        [void]$errors.Add('SCALAR-AS-CONTAINER ' + $fx + ' ' + $p.path + ' kind=' + $p.kind + ' rule=' + $owned.pattern)
        $scalarAsContainer++
      }
    }
    $famResidual[$fx] = $residual
    foreach ($r in $fr) {
      $actual = if ($residual.ContainsKey($r.pattern)) { $residual[$r.pattern] } else { 0 }
      if ($r.paths -ne $actual) {
        [void]$errors.Add('COUNT ' + $fx + ' ' + $r.pattern + ' scope=' + $r.scope + ' declared=' + $r.paths + ' actual=' + $actual)
      }
      if ($validClass -notcontains $r.cls) { [void]$errors.Add('CLASS ' + $fx + ' ' + $r.pattern + ' unknown class ' + $r.cls) }
      # consumer anchor policy per class
      $csFileLine = ($r.cs -match '^\S+\.cs:(\d+)(-(\d+))?$')
      $csOk = $false
      switch ($r.cls) {
        'IN_BIND'    { $csOk = $csFileLine }
        'OUT_DIFF'   { $csOk = $csFileLine }
        'INTERNAL'   { $csOk = $csFileLine }
        'SEM_MAP'    { $csOk = $csFileLine }
        'UNBOUND'    { $csOk = ($r.cs -match '^GAP:N3-(B|E)\d+$') }
        'UNBOUND_HC' { $csOk = ($csFileLine -or ($r.cs -match '^GAP:N3-(B|E)\d+$')) }  # existence-only rows may cite their read site
        'META'       { $csOk = $r.cs.StartsWith('meta:') }
        'CONTAINER'  { $csOk = $r.cs.StartsWith('meta:') }
        'UNA'        { $csOk = $r.cs.StartsWith('UNA:') }
      }
      if (-not $csOk) { [void]$errors.Add('ANCHOR-C# ' + $fx + ' ' + $r.pattern + ' scope=' + $r.scope + ' class=' + $r.cls + ' cs=' + $r.cs) }
      elseif ($csFileLine) { Test-FileLineAnchor $r.cs 'cs' $fx $r.pattern 'ANCHOR-C#' $errors }
      # exporter anchor policy: real classes need File.kt:L(-L); META/CONTAINER/UNA meta:
      if ($r.cls -in @('META','CONTAINER','UNA')) {
        if (-not $r.exporter.StartsWith('meta:')) { [void]$errors.Add('ANCHOR-V4 ' + $fx + ' ' + $r.pattern + ' exporter must be meta: for ' + $r.cls + ': ' + $r.exporter) }
      } else {
        if ($r.exporter -match '^\S+\.kt:(\d+)(-(\d+))?$') { Test-FileLineAnchor $r.exporter 'kt' $fx $r.pattern 'ANCHOR-V4' $errors }
        else { [void]$errors.Add('ANCHOR-V4 ' + $fx + ' ' + $r.pattern + ' exporter lacks line range: ' + $r.exporter) }
      }
      if (-not $r.owner) { [void]$errors.Add('OWNER ' + $fx + ' ' + $r.pattern) }
    }
    Write-Host ('RULES ' + $fx + ' paths=' + $paths.Count + ' rules=' + $fr.Count + ' leaked=' + $leak + ' scalarAsContainer=' + $scalarAsContainer)
  }
  # ---- pass B: stream the per-instance manifest, bucket by owning family ----
  $manifestFile = Join-Path $CensusDir 'instances.tsv'
  if (-not (Test-Path $manifestFile)) { Write-Host ('FAIL instances.tsv not found under ' + $CensusDir + ' (run CensusInstances first)'); Write-Host 'RULES-CHECK-FAILED errors=1'; return 1 }
  foreach ($line in [IO.File]::ReadLines($manifestFile)) {
    if (-not $line) { continue }
    $c = $line -split "`t"
    if ($c.Count -lt 6) { [void]$errors.Add('MANIFEST-BADLINE ' + $line); continue }
    $fx = $c[0]; $p = $c[1]; $cid = $c[2]; $kind = $c[4]
    if (-not $manifestPaths.ContainsKey($fx)) { [void]$errors.Add('MANIFEST-UNKNOWN-FIXTURE ' + $fx); continue }
    $fxNodes[$fx]++
    if ($cid -ne '-') { $caseUniverse[$fx][$cid] = $true }
    if (-not $manifestPaths[$fx].ContainsKey($p)) { $manifestPaths[$fx][$p] = $true }
    $pattern = $null
    if ($pathOwner[$fx].ContainsKey($p)) { $pattern = $pathOwner[$fx][$p] }
    if ($null -eq $pattern) { [void]$errors.Add('MANIFEST-CENSUS-MISMATCH ' + $fx + ' manifest path not in census: ' + $p); continue }
    $gk = $fx + '|' + $pattern
    if (-not $famLines.ContainsKey($gk)) { $famLines[$gk] = New-Object System.Collections.Generic.List[string] }
    $famLines[$gk].Add($line)
  }
  foreach ($fx in $fixtures) {
    foreach ($p in $pathOwner[$fx].Keys) {
      if (-not $manifestPaths[$fx].ContainsKey($p)) { [void]$errors.Add('MANIFEST-CENSUS-MISMATCH ' + $fx + ' census path with no manifest instance: ' + $p) }
    }
  }
  # ---- pass C: per-family per-instance adjudication + TRUE unit tally ----
  $classDistU = @{}; $gapDistU = @{}
  $pathClasses = @{}
  $pathUnits = @{}   # fx|path -> hashtable owner -> n (per-path unit detail)
  function Add-Unit([string]$fxp, [string]$cls, [string]$owner) {
    if (-not $classDistU.ContainsKey($cls)) { $classDistU[$cls] = 0 }
    $classDistU[$cls]++
    if ($owner -match '^GAP:') {
      if (-not $gapDistU.ContainsKey($owner)) { $gapDistU[$owner] = 0 }
      $gapDistU[$owner]++
    }
    if (-not $pathUnits.ContainsKey($fxp)) { $pathUnits[$fxp] = @{} }
    if (-not $pathUnits[$fxp].ContainsKey($owner)) { $pathUnits[$fxp][$owner] = 0 }
    $pathUnits[$fxp][$owner]++
  }
  foreach ($gk in $famLines.Keys) {
    $lines = $famLines[$gk]
    $fx = $gk.Substring(0, $gk.IndexOf('|'))
    $pattern = $gk.Substring($gk.IndexOf('|') + 1)
    $fr = @($rules | Where-Object { $_.fixture -eq $fx -and $_.pattern -eq $pattern })
    if ($fr.Count -eq 0) { continue }
    $isUnscoped = ($fr[0].scope -eq '*')
    if ($isUnscoped) {
      # unscoped family: the row claims EVERY instance of every matched path
      foreach ($line in $lines) {
        $c = $line -split "`t"
        $cls = $fr[0].cls
        if ($cls -eq 'UNA' -and $c[4] -ne 'null') { [void]$errors.Add('UNA-NOT-NULL ' + $gk + ' ' + $c[2] + ' ' + $c[1] + ' idx=' + $c[3] + ' kind=' + $c[4]) }
        Add-Unit ($fx + '|' + $c[1]) $cls $fr[0].owner
        $totalUnits++
        $pck = $fx + '|' + $c[1]
        if (-not $pathClasses.ContainsKey($pck)) { $pathClasses[$pck] = @{} }
        $pathClasses[$pck][$cls] = $true
      }
      continue
    }
    # scoped family: bidirectional per-instance equality
    $parsed = @()
    $bad = $false
    foreach ($r in $fr) {
      $sc = Parse-Scope $r.scope
      if ($null -eq $sc) { [void]$errors.Add('SCOPE-FORMAT ' + $gk + ' ' + $r.scope); $bad = $true; continue }
      foreach ($id in $sc.ids) {
        if (-not $caseUniverse[$fx].ContainsKey($id)) { [void]$errors.Add('SCOPE-ID-UNKNOWN ' + $gk + ' ' + $id) }
      }
      $parsed += [pscustomobject]@{ row = $r; sc = $sc }
    }
    if ($bad) { continue }
    # family must be case-attributable
    $noCase = $true
    foreach ($line in $lines) { $c = $line -split "`t"; if ($c[2] -ne '-') { $noCase = $false; break } }
    if ($lines.Count -gt 0 -and $noCase) { [void]$errors.Add('SCOPE-NO-CASE-ID ' + $gk + ' section elements carry no caseId/id'); continue }
    # unit coverage: exactly one owning row per instance; UNA rows must claim null
    $owner = New-Object 'System.Collections.Generic.List[object]'  # one entry per line: count + cls + owner
    for ($i = 0; $i -lt $lines.Count; $i++) { [void]$owner.Add([pscustomobject]@{ n = 0; cls = ''; owner = '' }) }
    $rowClaimed = @{}
    for ($ri = 0; $ri -lt $parsed.Count; $ri++) {
      $pr = $parsed[$ri]
      for ($i = 0; $i -lt $lines.Count; $i++) {
        $c = $lines[$i] -split "`t"
        if ($pr.sc.ids -cnotcontains $c[2]) { continue }
        if ($pr.sc.whereField -and -not (Test-WhereStr $c[5] $pr.sc.whereField $pr.sc.whereVals)) { continue }
        $owner[$i].n++
        if ($owner[$i].n -eq 1) { $owner[$i].cls = $pr.row.cls; $owner[$i].owner = $pr.row.owner }
        $rk = "${ri}|" + $c[2]
        if (-not $rowClaimed.ContainsKey($rk)) { $rowClaimed[$rk] = 0 }
        $rowClaimed[$rk]++
      }
    }
    for ($i = 0; $i -lt $lines.Count; $i++) {
      $c = $lines[$i] -split "`t"
      if ($owner[$i].n -eq 0) { [void]$errors.Add('SCOPE-UNIT-UNCOVERED ' + $gk + ' ' + $c[2] + ' ' + $c[1] + ' idx=' + $c[3] + ' kind=' + $c[4]) }
      elseif ($owner[$i].n -gt 1) { [void]$errors.Add('SCOPE-OVERLAP ' + $gk + ' ' + $c[2] + ' ' + $c[1] + ' owners=' + $owner[$i].n) }
      else {
        $cls = $owner[$i].cls
        if ($cls -eq 'UNA' -and $c[4] -ne 'null') { [void]$errors.Add('UNA-NOT-NULL ' + $gk + ' ' + $c[2] + ' ' + $c[1] + ' idx=' + $c[3] + ' kind=' + $c[4]) }
        Add-Unit ($fx + '|' + $c[1]) $cls $owner[$i].owner
        $totalUnits++
        $pck = $fx + '|' + $c[1]
        if (-not $pathClasses.ContainsKey($pck)) { $pathClasses[$pck] = @{} }
        $pathClasses[$pck][$cls] = $true
      }
    }
    # every scoped row must actually claim >=1 instance (absent-field / where-never-true)
    for ($ri2 = 0; $ri2 -lt $parsed.Count; $ri2++) {
      foreach ($id in $parsed[$ri2].sc.ids) {
        if (-not $rowClaimed.ContainsKey("${ri2}|" + $id)) { [void]$errors.Add('SCOPE-ID-ABSENT ' + $gk + ' row#' + $ri2 + ' ' + $id) }
      }
    }
    # census agreement: family census paths == family manifest paths
    $unitPaths = @{}
    foreach ($line in $lines) { $c = $line -split "`t"; $unitPaths[$c[1]] = $true }
    foreach ($p in (Load-Census $fx)) {
      if (Test-Match $pattern $p.path) {
        if (-not $unitPaths.ContainsKey($p.path)) { [void]$errors.Add('UNIT-CENSUS-MISMATCH ' + $gk + ' census path with no manifest instance: ' + $p.path) }
      }
    }
    foreach ($up in $unitPaths.Keys) {
      if (-not $pathOwner[$fx].ContainsKey($up) -or $pathOwner[$fx][$up] -ne $pattern) {
        [void]$errors.Add('UNIT-CENSUS-MISMATCH ' + $gk + ' manifest path not owned by this family: ' + $up)
      }
    }
  }
  foreach ($pp in $pathClasses.Keys) {
    if ($pathClasses[$pp].Keys.Count -gt 1) {
      # (fixture, path)-keyed: same-named paths in different fixtures are never merged (R6-1)
      $mxp = $pp.Substring($pp.IndexOf('|') + 1)
      [void]$mixedPaths.Add('fx=' + ($pp.Substring(0, $pp.IndexOf('|'))) + ' path=' + $mxp + ' classes=' + (($pathClasses[$pp].Keys | Sort-Object) -join ','))
    }
  }
  # ---- emit per-fixture path projections (for prose regeneration) ----
  foreach ($fx in $fixtures) {
    foreach ($k in $classList) {
      if ($classDistFx[$fx].ContainsKey($k)) { Write-Host ('DIST-FX fx=' + $fx + ' class=' + $k + ' paths=' + $classDistFx[$fx][$k]) }
    }
    foreach ($k in ($gapDistFx[$fx].Keys | Sort-Object)) { Write-Host ('DIST-FX fx=' + $fx + ' gap=' + $k + ' paths=' + $gapDistFx[$fx][$k]) }
    Write-Host ('UNITS fx=' + $fx + ' nodes=' + $fxNodes[$fx])
  }
  # emit unique-path class distribution + gap distribution (first-row projection, historical)
  $distSum = 0
  foreach ($k in $classList) {
    $n = if ($classDist.ContainsKey($k)) { $classDist[$k] } else { 0 }
    $distSum += $n
    Write-Host ('DIST class=' + $k + ' paths=' + $n)
  }
  $gapSum = 0
  foreach ($k in ($gapDist.Keys | Sort-Object)) {
    $gapSum += $gapDist[$k]
    Write-Host ('DIST gap=' + $k + ' paths=' + $gapDist[$k])
  }
  Write-Host ('DIST-TOTAL unique-paths=' + $distSum + ' gap-paths=' + $gapSum)
  # TRUE per-instance unit distribution (v4)
  $distSumU = 0
  foreach ($k in $classList) {
    $n = if ($classDistU.ContainsKey($k)) { $classDistU[$k] } else { 0 }
    $distSumU += $n
    Write-Host ('CASE-DIST class=' + $k + ' units=' + $n)
  }
  $gapSumU = 0
  foreach ($k in ($gapDistU.Keys | Sort-Object)) {
    $gapSumU += $gapDistU[$k]
    Write-Host ('CASE-DIST gap=' + $k + ' units=' + $gapDistU[$k])
  }
  foreach ($m in ($mixedPaths | Sort-Object)) { Write-Host ('CASE-MIXED ' + $m) }
  Write-Host ('CASE-TOTAL units=' + $distSumU + ' gap-units=' + $gapSumU + ' mixed-paths=' + $mixedPaths.Count)
  # per-path unit detail: every claimed number above is recomputable per path
  foreach ($key in ($pathUnits.Keys | Sort-Object)) {
    $h = $pathUnits[$key]
    $n = 0; foreach ($v in $h.Values) { $n += $v }
    $parts = @()
    foreach ($o in ($h.Keys | Sort-Object)) { $parts += ($o + ':' + $h[$o]) }
    Write-Host ('PATH-UNITS fx=' + ($key.Split('|')[0]) + ' path=' + ($key.Substring($key.IndexOf('|') + 1)) + ' units=' + $n + ' owners=' + ($parts -join ','))
  }
  if ($errors.Count -eq 0) {
    Write-Host ('RULES-CHECK-OK-V5 total-paths=' + $totalPaths + ' instances=' + ($fxNodes.Values | Measure-Object -Sum).Sum + ' units=' + $totalUnits + ' rules=' + $rules.Count + ' leaks=0 mismatches=0 anchor-errors=0 dup=0 scalarAsContainer=0 scope-errors=0 una-errors=0')
    return 0
  }
  # UNA-NOT-NULL is per-instance evidence: print every one uncapped (R6-2 —
  # the count of real non-null instances misclaimed as UNA IS the message);
  # all other error kinds stay capped at 40 with a remaining-count line.
  $unaErr = @($errors | Where-Object { $_.StartsWith('UNA-NOT-NULL ') })
  $othErr = @($errors | Where-Object { -not $_.StartsWith('UNA-NOT-NULL ') })
  $unaErr | ForEach-Object { Write-Host ('FAIL ' + $_) }
  $othErr | Select-Object -First 40 | ForEach-Object { Write-Host ('FAIL ' + $_) }
  $shown = $unaErr.Count + [Math]::Min(40, $othErr.Count)
  if ($errors.Count -gt $shown) { Write-Host ('FAIL ... (remaining ' + ($errors.Count - $shown) + ' errors not shown)') }
  Write-Host ('RULES-CHECK-FAILED errors=' + $errors.Count)
  return 1
}

switch ($Mode) {
  'emit' {
    $patterns = Parse-Rules (Get-Content -Encoding UTF8 $PatternsFile)
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add(("fixture`tpattern`tpaths`tclass`texporterAnchor`tcsAnchor`towner`tscope"))
    foreach ($fx in $fixtures) {
      $fr = @($patterns | Where-Object { $_.fixture -eq $fx })
      $paths = Load-Census $fx
      $residual = @{}
      foreach ($p in $paths) {
        for ($i = 0; $i -lt $fr.Count; $i++) {
          if (Test-Match $fr[$i].pattern $p.path) {
            if (-not $residual.ContainsKey($fr[$i].pattern)) { $residual[$fr[$i].pattern] = 0 }
            $residual[$fr[$i].pattern]++
            break
          }
        }
      }
      foreach ($r in $fr) {
        $cnt = if ($residual.ContainsKey($r.pattern)) { $residual[$r.pattern] } else { 0 }
        $out.Add(($r.fixture + "`t" + $r.pattern + "`t" + $cnt + "`t" + $r.cls + "`t" + $r.exporter + "`t" + $r.cs + "`t" + $r.owner + "`t" + $r.scope))
      }
    }
    $out | Set-Content -Encoding UTF8 $OutFile
    Write-Output ('EMIT-RULES ' + $OutFile + ' rules=' + ($out.Count - 1))
    $leaks = 0
    foreach ($fx in $fixtures) {
      $fr = @($patterns | Where-Object { $_.fixture -eq $fx })
      foreach ($p in (Load-Census $fx)) {
        $hit = $false
        foreach ($r in $fr) { if (Test-Match $r.pattern $p.path) { $hit = $true; break } }
        if (-not $hit) { Write-Output ('LEAK ' + $fx + ' ' + $p.path); $leaks++ }
      }
    }
    if ($leaks -gt 0) { Write-Output ('EMIT-WITH-LEAKS=' + $leaks); exit 1 }
    exit 0
  }
  'check' {
    if (-not $Repo) { Write-Error 'check mode requires -Repo (v4 validates anchors against the repo)'; exit 2 }
    if ($ReportPath) { $rules = Parse-Rules (Read-Rules-Block $ReportPath) }
    else { $rules = Parse-Rules (Get-Content -Encoding UTF8 $RulesFile) }
    $rc = Invoke-Check $rules
    exit $rc
  }
  'negative' {
    $lines = if ($ReportPath) { Read-Rules-Block $ReportPath } else { Get-Content -Encoding UTF8 $RulesFile }
    function Find-Row([string[]]$ls, [string]$fx, [string]$patSuffix, [string]$cls) {
      foreach ($l in $ls) {
        $c = $l -split "`t"
        if ($c.Count -ge 8 -and $c[0] -eq $fx -and $c[3] -eq $cls -and $c[1].EndsWith($patSuffix)) { return $l }
      }
      return $null
    }
    $mut = $null
    switch ($Kind) {
      'strip-anchors' {
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) {
          $c = $l -split "`t"
          if ($c.Count -ge 7 -and $c[0] -ne 'fixture' -and ($c[3] -eq 'IN_BIND' -or $c[3] -eq 'OUT_DIFF' -or $c[3] -eq 'INTERNAL' -or $c[3] -eq 'SEM_MAP')) {
            [void]$mut.Add(($c[0] + "`t" + $c[1] + "`t" + $c[2] + "`t" + $c[3] + "`t" + $c[4] + "`t`t" + $c[6] + "`t" + $c[7]))
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
        [void]$mut.Add(("physical-v1`t" + '$.samples[*].expected.#' + "`t0`tOUT_DIFF`tV5PhysicalGoldenExporterTest.kt`t`tP1.1`t*"))
      }
      'nonexistent-anchors' {
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) {
          $c = $l -split "`t"
          if ($c.Count -ge 8 -and $c[0] -ne 'fixture') {
            if ($c[4] -match '^\S+\.kt:\d+') { $c[4] = 'NonexistentExporter.kt:1-2' }
            if ($c[5] -match '^\S+\.cs:\d+') { $c[5] = 'NonexistentConsumer.cs:999999-1000000' }
            [void]$mut.Add($c -join "`t")
          } else { [void]$mut.Add($l) }
        }
      }
      'duplicate-rule' {
        $mut = New-Object System.Collections.Generic.List[string]
        $dupRow = $null
        foreach ($l in $lines) {
          [void]$mut.Add($l)
          if (-not $dupRow) {
            $c = $l -split "`t"
            if ($c.Count -ge 8 -and $c[0] -eq 'physical-v1' -and $c[1] -eq '$.clockUsed') { $dupRow = $l }
          }
        }
        if (-not $dupRow) { Write-Error 'duplicate-rule negative needs a $.clockUsed row'; exit 2 }
        [void]$mut.Add($dupRow)
      }
      'scalar-container' {
        $mut = New-Object System.Collections.Generic.List[string]
        $inserted = $false
        foreach ($l in $lines) {
          if (-not $inserted -and $l -match '^physical-v1\t' -and $l -notmatch '^fixture\t') {
            [void]$mut.Add(("physical-v1`t" + '$.#' + "`t0`tCONTAINER`tmeta:container-exporter`tmeta:container-node`tMETA`t*"))
            $inserted = $true
          }
          [void]$mut.Add($l)
        }
      }
      'scope-extra-case' {
        # caller R4-1 negative, exact shape: append an absent sibling case to a scoped leaf
        $row = Find-Row $lines 'cheat-v1' 'goldenInputs.availableWallCount' 'UNBOUND'
        if (-not $row) { Write-Error 'scope-extra-case negative needs the availableWallCount UNBOUND row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] + '|sb-activate-disabled'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'scope-drop-case' {
        $row = Find-Row $lines 'cheat-v1' 'goldenInputs.availableWallCount' 'UNBOUND'
        if (-not $row) { Write-Error 'scope-drop-case negative needs the availableWallCount UNBOUND row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = 'case=' + ($c[7].Substring(5).Split('|') | Select-Object -Skip 1) ; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'scope-unknown-case' {
        $row = Find-Row $lines 'cheat-v1' 'goldenInputs.availableWallCount' 'UNBOUND'
        if (-not $row) { Write-Error 'scope-unknown-case negative needs the availableWallCount UNBOUND row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] + '|no-such-case-anywhere'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'scope-cross-section' {
        $row = Find-Row $lines 'cheat-v1' 'goldenInputs.availableWallCount' 'UNBOUND'
        if (-not $row) { Write-Error 'scope-cross-section negative needs the availableWallCount UNBOUND row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] + '|tm-wrong-phase-preparation'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'scope-dup-case' {
        # sibling-row overlap: appending an already-owned case must double-cover
        $row = Find-Row $lines 'cheat-v1' 'goldenInputs.seat' 'UNBOUND'
        if (-not $row) { Write-Error 'scope-dup-case negative needs the goldenInputs.seat UNBOUND row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] + '|sbo-activate-allowed'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'scope-where-broken' {
        # append a second where clause -> grammar rejects (at most one ;where=)
        $row = Find-Row $lines 'cheat-v1' 'factsAfter[*].seat' 'OUT_DIFF'
        if (-not $row) { Write-Error 'scope-where-broken negative needs the factsAfter[*].seat OUT_DIFF row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] + ';where=container==wall'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'where-value-broken' {
        # where value that matches no instance -> its units fall uncovered
        $row = Find-Row $lines 'cheat-v1' 'factsAfter[*].seat' 'OUT_DIFF'
        if (-not $row) { Write-Error 'where-value-broken negative needs the factsAfter[*].seat OUT_DIFF row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] -replace 'container==hand\|discard', 'container==hnd|discard'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'una-drop' {
        # drop the UNA row -> its null instances fall uncovered
        $row = Find-Row $lines 'cheat-v1' 'factsAfter[*].index' 'UNA'
        if (-not $row) { Write-Error 'una-drop negative needs the factsAfter[*].index UNA row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -cne $row) { [void]$mut.Add($l) } }
      }
      'una-nonnull' {
        # retarget the UNA row onto a container whose instances are numbers
        $row = Find-Row $lines 'cheat-v1' 'factsAfter[*].index' 'UNA'
        if (-not $row) { Write-Error 'una-nonnull negative needs the factsAfter[*].index UNA row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[7] = $c[7] -replace 'container==hand\|discard', 'container==wall'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
      }
      'una-unscoped-nonnull' {
        # caller R6-2 negative, exact shape: unscoped row claims real string leaves as UNA
        $row = Find-Row $lines 'scoring-v1' '$.winSamples[*].expected.status' 'UNBOUND'
        if (-not $row) { Write-Error 'una-unscoped-nonnull negative needs the winSamples status UNBOUND row'; exit 2 }
        $mut = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ceq $row) { $c = $l -split "`t"; $c[3] = 'UNA'; $c[4] = 'meta:caller-negative-nonnull'; $c[5] = 'UNA:CALLER-NEGATIVE'; $c[6] = 'UNA:CALLER-NEGATIVE'; [void]$mut.Add($c -join "`t") } else { [void]$mut.Add($l) } }
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

**R6-1 独立 oracle 工具源码全文内嵌如下（`mixed-namespace-oracle.ps1`，实测 SHA256 `e54af103aaf6768128412dc4f8b40c50c1c1e301db80ff981ea4824571f6fd09`，127 行，与 R7 TEMP `vm5/mixed-namespace-oracle.ps1` 逐字节相等；不调用校验器、不消费仓库，只读 rules TSV + instances.tsv + checker 日志三件；提取见 §11.10）**：

```powershell
<!--TOOL:mixed-namespace-oracle:BEGIN-->
# Independent cross-fixture namespace isolation oracle (round-7, R6-1).
# Does NOT call the checker. Recomputes every (fixture, path) class set
# directly from the rules TSV + CensusInstances manifest (instances.tsv)
# and compares against the checker log's CASE-MIXED lines.
#
# Rationale: census normalized paths are literal (patterns match exactly),
# so ownership is the first distinct pattern per fixture; a scoped family's
# per-instance classes come from scope/where evaluation over manifest rows.
# v4's defect (same-named paths merged ACROSS fixtures) cannot survive this
# comparison: any reported mix without a fixture-qualified counterpart in
# the recomputation, or any recomputed mix missing from the report, fails.
#
# Usage: powershell -File mixed-namespace-oracle.ps1 -RulesFile rules-v5.tsv `
#        -Manifest census/instances.tsv -CheckLog v5-check.log
param([string]$RulesFile, [string]$Manifest, [string]$CheckLog)
$ErrorActionPreference = 'Stop'

# ---- load rules ----
$rules = @()
foreach ($l in (Get-Content -Encoding UTF8 $RulesFile)) {
  $c = $l -split "`t"
  if ($c.Count -ge 8 -and $c[0] -ne 'fixture') {
    $scope = $c[7]
    $ids = @(); $wField = ''; $wVals = @()
    if ($scope -eq '*') {
      # unscoped
    } else {
      foreach ($part in $scope -split ';') {
        if ($part.StartsWith('case=')) { $ids = ($part.Substring(5) -split '\|') }
        elseif ($part.StartsWith('where=')) {
          $w = $part.Substring(6); $eq = $w.IndexOf('==')
          $wField = $w.Substring(0, $eq); $wVals = ($w.Substring($eq + 2) -split '\|')
        }
      }
    }
    $rules += [pscustomobject]@{ fx = $c[0]; pat = $c[1]; cls = $c[3]; owner = $c[6]; unscoped = ($scope -eq '*'); ids = $ids; wField = $wField; wVals = $wVals }
  }
}

# ---- load manifest: (fx,path) -> instances (caseId, kind, parent) ----
$inst = @{}
foreach ($l in (Get-Content -Encoding UTF8 $Manifest)) {
  $c = $l -split "`t"
  if ($c.Count -lt 6) { continue }
  $k = $c[0] + '|' + $c[1]
  if (-not $inst.ContainsKey($k)) { $inst[$k] = New-Object System.Collections.Generic.List[object] }
  $inst[$k].Add([pscustomobject]@{ case = $c[2]; kind = $c[4]; parent = $c[5] })
}

# ---- per-fixture ordered distinct patterns (first-match ownership) ----
$pats = @{}
foreach ($r in $rules) { if (-not $pats.ContainsKey($r.fx)) { $pats[$r.fx] = New-Object System.Collections.Generic.List[string] }; if (-not $pats[$r.fx].Contains($r.pat)) { [void]$pats[$r.fx].Add($r.pat) } }
$rowsOf = @{}
foreach ($r in $rules) { $k = $r.fx + '|' + $r.pat; if (-not $rowsOf.ContainsKey($k)) { $rowsOf[$k] = New-Object System.Collections.Generic.List[object] }; [void]$rowsOf[$k].Add($r) }

function Test-Where([string]$parent, [string]$field, [string[]]$vals) {
  if ($parent -eq '-') { return $false }
  foreach ($kv in $parent -split ';') {
    $eq = $kv.IndexOf('=')
    if ($eq -gt 0 -and $kv.Substring(0, $eq) -eq $field) { return ($vals -ccontains $kv.Substring($eq + 1)) }
  }
  return $false
}

# ---- recompute class set per (fx,path) ----
$recomp = @{}
$mixed = @{}
foreach ($k in $inst.Keys) {
  $fx = $k.Substring(0, $k.IndexOf('|')); $p = $k.Substring($k.IndexOf('|') + 1)
  $ownerPat = $null
  foreach ($pt in $pats[$fx]) { if ($pt -ceq $p) { $ownerPat = $pt; break } }
  if ($null -eq $ownerPat) { continue }
  $fam = $rowsOf[$fx + '|' + $ownerPat]
  $cls = @{}
  if ($fam[0].unscoped) {
    $cls[$fam[0].cls] = $true
  } else {
    foreach ($it in $inst[$k]) {
      foreach ($r in $fam) {
        if ($r.unscoped) { continue }
        if (-not ($r.ids -ccontains $it.case)) { continue }
        if ($r.wField -and -not (Test-Where $it.parent $r.wField $r.wVals)) { continue }
        $cls[$r.cls] = $true
        break
      }
    }
  }
  $recomp[$k] = ($cls.Keys | Sort-Object) -join ','
  if ($cls.Keys.Count -gt 1) { $mixed[$k] = $recomp[$k] }
}

# ---- compare against checker log ----
$reported = @{}
foreach ($l in (Get-Content -Encoding UTF8 $CheckLog)) {
  if ($l.StartsWith('CASE-MIXED fx=')) {
    $body = $l.Substring('CASE-MIXED '.Length)
    $fx = ($body -split ' ')[0].Substring(3)
    $rest = $body.Substring($body.IndexOf(' ') + 1)
    $pt = ($rest -split ' ')[0].Substring('path='.Length)
    $cls = $rest.Substring($rest.IndexOf('classes=') + 8)
    $reported[$fx + '|' + $pt] = $cls
  }
}
$bad = 0
foreach ($k in $reported.Keys) {
  if (-not $recomp.ContainsKey($k)) { Write-Host ('REPORTED-NOT-RECOMPUTED ' + $k); $bad++ }
  elseif ($recomp[$k] -ne $reported[$k]) { Write-Host ('CLASS-SET-MISMATCH ' + $k + ' reported=' + $reported[$k] + ' recomputed=' + $recomp[$k]); $bad++ }
}
foreach ($k in $mixed.Keys) {
  if (-not $reported.ContainsKey($k)) { Write-Host ('RECOMPUTED-NOT-REPORTED ' + $k + ' classes=' + $mixed[$k]); $bad++ }
}
# cross-fixture namespace check: no path name may be mixed in one fixture
# while absent from another fixture's mixed set for a DIFFERENT reason —
# the definitive assertion: every reported mix key is fixture-qualified and
# equals the recomputation (above), and the 7 v4 false-mixed witness paths
# are NOT mixed in any fixture.
$witnesses = @('$.decisionClock', '$.exportedAtNote', '$.extractionStatus', '$.generatedByCommand', '$.generationResult.exporterTestResult', '$.generatorVersion', '$.unexportedSamples')
foreach ($w in $witnesses) {
  $hits = @($mixed.Keys | Where-Object { $_.Substring($_.IndexOf('|') + 1) -ceq $w })
  if ($hits.Count -gt 0) { Write-Host ('WITNESS-FALSELY-MIXED ' + $w + ' -> ' + ($hits -join ' ')); $bad++ } else { Write-Host ('WITNESS-CLEAN ' + $w) }
}
foreach ($k in ($mixed.Keys | Sort-Object)) { Write-Host ('ORACLE-MIXED fx=' + ($k.Substring(0, $k.IndexOf('|'))) + ' path=' + ($k.Substring($k.IndexOf('|') + 1)) + ' classes=' + $mixed[$k]) }
$fxs = @($mixed.Keys | ForEach-Object { $_.Substring(0, $_.IndexOf('|')) } | Sort-Object -Unique)
Write-Host ('MIXED-NS-ORACLE-' + $(if ($bad -eq 0) { 'OK' } else { 'FAILED' }) + ' recomputed-mixed=' + $mixed.Keys.Count + ' reported=' + $reported.Keys.Count + ' fixtures=' + ($fxs -join ',') + ' mismatches=' + $bad)
if ($bad -gt 0) { exit 1 }
exit 0
<!--TOOL:mixed-namespace-oracle:END-->
```

**正检实测（v5，-RulesFile 模式对 R7 TEMP 原始 TSV；-ReportPath 模式对报告内嵌块的复算在 §11.10 LOOP-R7；census = `vm5/census/`——6 份 `<fx>-full-paths.txt` + `instances.tsv`，与 R6 `vm4/census/` 逐字节同源（本 round 未改 census 产物），instances.tsv 即 §11.4b CensusInstances 清单，校验器同时消费两者并强制双向等式）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/verify-mapping-v5.ps1 -Mode check -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/rules-v5.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/census -Repo D:/majiang-wt/n3-golden-field-audit 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/v5-check.log 2>&1
→ EXIT=0；UNITS fx=×6（physical 34718 / scoring 592640 / round 1474 / cheat 1443 / shuffle 23432 / ai 1438，合计 655145 = §11.4b manifest）+ RULES ×6（paths 296/419/200/204/199/198，rules 91/63/72/217/40/59 = 542，leaked=0 scalarAsContainer=0）+ DIST class=×9（CONTAINER 18/IN_BIND 314/INTERNAL 114/META 308/OUT_DIFF 405/SEM_MAP 256/UNBOUND 56/UNBOUND_HC 45/UNA 0）+ DIST gap=×12 + DIST-TOTAL unique-paths=1516 gap-paths=101 + PATH-UNITS ×1516（逐路径实例与 owners）+ DIST-FX ×6 + CASE-DIST class=×9（144/430115/23088/42684/147837/10668/350/242/17）+ CASE-DIST gap=×14 + 27 行 CASE-MIXED（每行显式 `fx=`，全部 cheat-v1）+ CASE-TOTAL units=655145 gap-units=592 mixed-paths=27 + RULES-CHECK-OK-V5 total-paths=1516 instances=655145 units=655145 rules=542 leaks=0 mismatches=0 anchor-errors=0 dup=0 scalarAsContainer=0 scope-errors=0 una-errors=0（v5-check.log SHA256 fcae384805bf6a44b3e3e48d013f398ad829967ec0051d84e835569aea2b4735）
```

**R6-1 验收实测（fx 键 + 独立 oracle）**——v5 判定行 una-errors=0、mixed-paths=27；`CASE-MIXED` 27 行全部 `fx=cheat-v1`，摘录（机器输出原样）：

```
CASE-MIXED fx=cheat-v1 path=$.sleeveBloomHandler[*].goldenInputs.cost classes=OUT_DIFF,SEM_MAP,UNBOUND
CASE-MIXED fx=cheat-v1 path=$.tileMovement[*].factsAfter[*].index classes=INTERNAL,UNA,UNBOUND
CASE-MIXED fx=cheat-v1 path=$.tileMovement[*].factsAfter[*].seat classes=OUT_DIFF,UNA,UNBOUND
```

v4 曾报 34 行（无 `fx=` 前缀，跨 fixture 误并 7 条假混合）——该 34 为**已更正的 R6 错误历史**（v4-check.log SHA256 `bdc7ca11f7535b4f33798afd0cd2ec97de39744df7f3e3e5eb3711b986f4a25d`，保留于 R6 TEMP 与 §15.5 映射），不再作为现行值引用。7 条见证路径（round-v1/ai-v1 `$.decisionClock`、round-v1 `$.exportedAtNote`/`$.extractionStatus`/`$.generatedByCommand`/`$.generationResult.exporterTestResult`/`$.generatorVersion`/`$.unexportedSamples`）在 v5 下于**任一 fixture 内均不混合**。独立 oracle（`vm5/mixed-namespace-oracle.ps1`，SHA256 `e54af103aaf6768128412dc4f8b40c50c1c1e301db80ff981ea4824571f6fd09`，不调用校验器，直接从 rules TSV + instances.tsv 重算每 (fixture,path) 类集合并与 checker 日志逐行比对）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/mixed-namespace-oracle.ps1 -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/rules-v5.tsv -Manifest C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/census/instances.tsv -CheckLog C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/v5-check.log 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/mixed-namespace-oracle.log 2>&1
→ EXIT=0；WITNESS-CLEAN ×7 + ORACLE-MIXED fx=cheat-v1 ×27（与 checker 日志 27 行逐一相等）+ `MIXED-NS-ORACLE-OK recomputed-mixed=27 reported=27 fixtures=cheat-v1 mismatches=0`（log SHA256 9eaebd992adc42c55d956b780c19815e46849b7f46b38abd1236a45e4eb18f42）
```

**R6-2 验收实测（UNA 非空守卫全分支共享，caller 指定输入原样回放）**——caller 的负例 TSV（`vm5/una-unscoped-caller.tsv`，SHA256 `3671b3dab66b4c6a25d02b43f598f4289cbe5d450378a82277080f9eb44823ba` 与 E' 证据 `una-unscoped-nonnull.tsv` 逐字节相等；scoring-v1 `$.winSamples[*].expected.status` UNBOUND→UNA、exporterAnchor=`meta:caller-negative-nonnull`、csAnchor/owner=`UNA:CALLER-NEGATIVE`、scope=*）在 **v4** 上 exit 0 假绿（una-errors=0、`CASE-DIST class=UNA units=65`、`CASE-TOTAL … gap-units=544 mixed-paths=34`，复现 log `vm5/repro-una-v4.log` SHA256 `e1e6d837afa551638a7f1edeeff220bc41dd52eaf09f4200eb5b5cb324791e3c`）；在 **v5** 上：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/verify-mapping-v5.ps1 -Mode check -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/una-unscoped-caller.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/census -Repo D:/majiang-wt/n3-golden-field-audit 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/v5-una-caller.log 2>&1
→ EXIT=1；恰 48 行 `FAIL UNA-NOT-NULL scoring-v1|$.winSamples[*].expected.status <case> $.winSamples[*].expected.status idx=<i> kind=string`（48 个真实字符串实例逐一点名，无一条伪装成路径/锚点/scope 错误；UNA-NOT-NULL 不设 40 条打印上限，逐实例全量公布）+ `RULES-CHECK-FAILED errors=48`（log SHA256 5279c6454651173e3c1fef3581b46b40827ca6de0cf690b46f175d19de289b71）；基线（原始 TSV）不受影响：仍 UNA 17 units / gap-units=592 / units=655145 / mixed-paths=27 / EXIT=0

**R5-1 验收的实例级复算（caller 指定两例必须逐实例可复算，PATH-UNITS 机器输出摘录；v5 正检同位四行逐字节相同）**——48 个 winSamples status 实例逐实例判入 E1、32 个 shuffle deadWallSegmentEqual 实例逐实例判入 B2（v3 两个都只记 1）；factsAfter 坐标两路径的 34 实例按 where 分区拆分归属：

```
PATH-UNITS fx=scoring-v1 path=$.winSamples[*].expected.status units=48 owners=GAP:N3-E1:48
PATH-UNITS fx=shuffle-v1 path=$.cases[*].deadWallSegmentEqual units=32 owners=GAP:N3-B2:32
PATH-UNITS fx=cheat-v1 path=$.tileMovement[*].factsAfter[*].index units=17 owners=BOUND:2,GAP:N3-B11:4,UNA:NULL-UNREAD:11
PATH-UNITS fx=cheat-v1 path=$.tileMovement[*].factsAfter[*].seat units=17 owners=BOUND:4,GAP:N3-B11:7,UNA:NULL-UNREAD:6
```

**兄弟行换序不变性实测（R4-1 验收：scoped 兄弟行互换后逐单元判类不变；第 7 轮 v5 复跑 = fx 限定的单元真值与混合路径汇总逐字节不变）**——把 rules-v5.tsv 的数据行按 (fixture,pattern) 分组、组内轮换一位（末行→首行；行多重集合不变，仅 scoped 兄弟行次序变化），再跑 check：

```
# 派生脚本（reorder-v5.pl，与第 6 轮 reorder-v4.pl 同算法同 SHA，本节内嵌）
perl reorder-v5.pl rules-v5.tsv rules-v5-reordered.tsv
→ REORDER-BUILT groups=502 rotated=27 outLines=542（27 个多行组各轮换一位；header 不动）
# 派生产物 body 与原 TSV 逐行多重集合相同（tail -n +2 | sort 双向 cmp 逐字节一致），行序不同；因规则内容未动，rules-v5-reordered.tsv 与第 6 轮 v4 派生件同 SHA
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/verify-mapping-v5.ps1 -Mode check -RulesFile C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/rules-v5-reordered.tsv -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/census -Repo D:/majiang-wt/n3-golden-field-audit 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/v5-check-reordered.log 2>&1
→ EXIT=0；**CASE-DIST 23 行（class×9 + gap×14）与 v5 正检逐字节相同**（cmp 空）、**PATH-UNITS 1516 行与正检逐字节相同**（cmp 空，= 655145 单元逐实例归属不变）、**fx 限定 CASE-MIXED 27 行与 CASE-TOTAL units=655145 gap-units=592 mixed-paths=27 与正检逐字节相同**、RULES-CHECK-OK-V5 判定行相同——**R5-1 单元真值与 R6-1 fx 限定混合汇总对兄弟行次序严格不变**；`DIST-TOTAL gap-paths=113`（正检 101）——DIST 口径**有意**保留"表内 first-match 投影"语义，scoped 兄弟行换序会改变路径级投影归属（UNBOUND 兄弟行排前时该路径投影为 UNBOUND），该口径差异与 fx 限定 CASE-MIXED 的 27 条显式分列、互不混同（第 5 轮 v3 逆序变异同象：gap-paths 122 vs 101，第 6/7 轮轮换变异 113 vs 101）；因此 §3.0.1/§9 的 DIST 数字恒以锁 SHA 的原始行序表为准（rules-v5-reordered.tsv SHA256 7cbdfa5fc03bc00a5df4df5187d2b30ee20f994b460cbdff81f08d94b6069622【= 第 6 轮 v4 派生件同值，规则未动】、v5-check-reordered.log SHA256 278d1eb6e6067abb492e0a65efee24cad2b546d2e027591eb5f2d60633c3bd50）
```

**checker 负例矩阵（第 7 轮：16 种作弊变异全部 NEGATIVE-DETECTED + 1 项独立 oracle 控制行；v5 对每种变异 exit 1、负例包装器 exit 0；命令形如 `-Mode negative -Kind <k> -RulesFile <R7>/vm5/rules-v5.tsv -CensusDir <R7>/vm5/census -Repo D:/majiang-wt/n3-golden-field-audit`，日志 `vm5/neg-v5/<k>.log`）**——第 6 轮在 v3 的 12 种之上新增 3 种专打 R5-2 修复面的变异（`where-value-broken` 打 where 谓词真值、`una-drop` 打 UNA 行存在性、`una-nonnull` 打 UNA 行谓词错挂）；**第 7 轮（R6-2）新增 `una-unscoped-nonnull` 专打 v4 unscoped 分支绕过 UNA 守卫的面（与 caller 指定输入同构）**，并新增独立 mixed-namespace oracle 控制行（R6-1）；15 项既有负例全部在 v5 上原样重跑（v4 时代日志保留于 R6 TEMP/§15.5 历史，不作为现行证据）：

| Kind | 变异 | 首条机器错误 | errors（判定行真值总数） | 负例 log SHA256（v5，`vm5/neg-v5/`） |
|---|---|---|---|---|
| strip-anchors | 全部锚点置空 | `FAIL ANCHOR-C# physical-v1 $.clockUsed scope=* class=SEM_MAP cs=`（40 条打印上限 + remaining 行；**errors 列本 round 更正为判定行真值总数**：v4/v5 判定行同为此值——v4 表曾把该三种的打印行数 40 误记入本列，非 v5 行为差异） | 336 | `c6e8a26432a1a9f55018aa87f8dccf1961a3559a2d55f9d9e1e0563210b5f4c3` |
| drop-rule | 删 `$.clockUsed` 行 | `FAIL LEAK physical-v1 $`，随后 v4 新增第二错 `FAIL MANIFEST-CENSUS-MISMATCH physical-v1 manifest path not in census: $`（rules↔census 双向等式把漏报路径也钉住） | 2 | `88ab256e9351f83acbbe3b2c7b56d5fdc23b6199ee4eefe7a6f78630aff9150c` |
| anchorless-rule | `$.samples[*].expected.#` 置空锚点 | `FAIL DUP physical-v1 $.samples[*].expected.# scope=*` | 4 | `6119aa35f48446352632c7d63c186f2eb228501991ec22147e63398e5bbff5bd` |
| nonexistent-anchors | `$.clockUsed` 双锚点指向不存在文件 | `FAIL ANCHOR-C# physical-v1 $.clockUsed file not found under repo: NonexistentConsumer.cs`（同上：真值总数，v4 判定行同值） | 786 | `b534985c01f8d6aec338a2c204e4deb3c5d3d065726db32664379aae1a1243f8` |
| duplicate-rule | 复制 `$.clockUsed` 行 | `FAIL DUP physical-v1 $.clockUsed scope=*` | 1 | `eb830a9154d32fffc5d9abf311d2dc0b458848b2f5c010f7061280fff3e3a2b5` |
| scalar-container | `$.#` 置 CONTAINER | `FAIL SCALAR-AS-CONTAINER physical-v1 $.clockUsed kind=boolean rule=$.#`（同上：真值总数，v4 判定行同值） | 324 | `e7c55bee90a2a862a2387e264d8987ea914584bae7d7cff0121c21241d0b44ce` |
| scope-extra-case | **caller R4-1 指定负例**：`availableWallCount` scope 追加 `\|sb-activate-disabled` | `FAIL SCOPE-ID-ABSENT cheat-v1\|$.sleeveBloomHandler[*].goldenInputs.availableWallCount row#0 sb-activate-disabled` | 1 | `c1a8a7c1a83ea37f056d5d96ca536d3f42aeb36645e757bfd30e17cdec49a012` |
| scope-drop-case | scope 删去实际出现 case（单 case 行删后剩空 `case=`） | `FAIL SCOPE-FORMAT cheat-v1\|$.sleeveBloomHandler[*].goldenInputs.availableWallCount case=`（文法校验拒绝空 case 列表） | 1 | `8f71255943da4824b17f615331ebb40fe9a97f03ffb4a3e36563c1e16434379e` |
| scope-unknown-case | scope 追加全 fixture 不存在的 `no-such-case-anywhere` | `FAIL SCOPE-ID-UNKNOWN cheat-v1\|…availableWallCount no-such-case-anywhere` + `FAIL SCOPE-ID-ABSENT …` | 2 | `0a045e798167690e24106eb847898cc6f1cdb2d539e648cc3d672a7c9dc71ab0` |
| scope-dup-case | `goldenInputs.seat` UNBOUND 行并复制 case 造成双认领 | `FAIL SCOPE-OVERLAP cheat-v1\|…goldenInputs.seat sbo-activate-allowed $.sleeveBloomHandler[*].goldenInputs.seat owners=2` | 1 | `0e1fd68f327fd2a7d695ec162eddd548058b9022bf428150971cf8ee0b739bd9` |
| scope-cross-section | scope 混入跨分区 case（`tm-wrong-phase-preparation`） | `FAIL SCOPE-ID-ABSENT cheat-v1\|…goldenInputs.availableWallCount row#0 tm-wrong-phase-preparation` | 1 | `53eeecf62c046680e1989fd95bade32e1c9a2bd328963a6d6ae76757b92a1dcd` |
| scope-where-broken | `factsAfter[*].seat` OUT_DIFF 行追加第二个 `;where=` 子句 | `FAIL SCOPE-FORMAT cheat-v1\|$.tileMovement[*].factsAfter[*].seat case=mv-reservation-delivery-then-beam;where=container==hand\|discard;where=container==wall`（文法拒绝：至多一个 `;where=`） | 1 | `9d3fb4791848e4a2c1f84d3b5dc5094292137ef126b5c30e76dec3976b97addf` |
| where-value-broken（第 6 轮新） | seat OUT_DIFF 行 where 值 `hand\|discard` 改为 `hnd\|discard`（无实例匹配的容器名） | 2 × `FAIL SCOPE-UNIT-UNCOVERED cheat-v1\|$.tileMovement[*].factsAfter[*].seat mv-reservation-delivery-then-beam … idx=1 kind=number` / `idx=3`（仅 m5 手牌容器 2 个 number 实例失覆盖；恰与 §3.4 "生效实例=2" 的实例级判定互证） | 2 | `f39d3282098f41a3738182080f69347e45ce1e37319c8eb24cb3da043ff81c5b` |
| una-drop（第 6 轮新） | 整行删除 `factsAfter[*].index` 的 `;where=container==hand\|discard` UNA 行 | 11 × `FAIL SCOPE-UNIT-UNCOVERED …factsAfter[*].index mv-swap-hand-with-wall-direct … idx=2 kind=null`（11 个未读 null 坐标实例失去显式 UNA 认领即 uncovered） | 11 | `25a6f8f0503789108f2f8505bc41753a586d6f5b7c18673b4dbfc9a31d5f0578` |
| una-nonnull（第 6 轮新） | 同一 UNA 行 where 改挂 `container==wall`（wall 实例已被 INTERNAL 行认领、且 4 case 的 row# 无 wall 实例） | 17 = 2 × `FAIL SCOPE-OVERLAP …factsAfter[*].index mv-swap-hand-with-wall-direct … owners=2`（首条）+ 11 × `FAIL SCOPE-UNIT-UNCOVERED`（原 11 个 null 实例失覆盖）+ 4 × `FAIL SCOPE-ID-ABSENT … row#1 mv-reservation-delivery` 等（挂错容器后 4 个 case 的声明行号无 wall 实例可对） | 17 | `146c627798b72c0628eaf1b131be20ab44cd033088fca765307e7152b51dc33b` |
| **una-unscoped-nonnull（第 7 轮新，R6-2 专打面）** | scoring-v1 `$.winSamples[*].expected.status` UNBOUND→UNA、exporterAnchor=`meta:caller-negative-nonnull`、csAnchor/owner=`UNA:CALLER-NEGATIVE`、scope=*（与 caller 指定输入同构；48 个真实字符串实例被 unscoped 行记为 UNA） | 恰 48 × `FAIL UNA-NOT-NULL scoring-v1\|$.winSamples[*].expected.status <case> … kind=string`（v4 同输入 exit 0 假绿——守卫只在 scoped 分支；v5 全分支共享不变量逐实例拒绝，且 UNA 类错误不设 40 条打印上限，无任何其余类型错误混入） | 48 | `9414bffd4a76e825e0ae8e2d14f5c4c4d69ce691ee2761007e1538071a2e70b9` |
| **mixed-namespace oracle（第 7 轮新控制行，R6-1）** | 非 TSV 变异——独立 oracle（`vm5/mixed-namespace-oracle.ps1`）从 rules TSV + instances.tsv 重算每 (fixture,path) 类集合，与 checker 日志 CASE-MIXED 行逐行比对，并断言 7 条 v4 假混合见证路径在任一 fixture 内均不混合 | `MIXED-NS-ORACLE-OK recomputed-mixed=27 reported=27 fixtures=cheat-v1 mismatches=0`（7 × `WITNESS-CLEAN` + 27 行 `ORACLE-MIXED fx=cheat-v1 …` 与 checker 日志逐一相等；任何 REPORTED-NOT-RECOMPUTED / CLASS-SET-MISMATCH / RECOMPUTED-NOT-REPORTED / WITNESS-FALSELY-MIXED 即 exit 1） | 0（控制行： mismatches=0 为通过态） | `9eaebd992adc42c55d956b780c19815e46849b7f46b38abd1236a45e4eb18f42` |

**FIELD-RULES 按类/按 owner 复算命令（§3.0.1 统计表的独立核对，第 7 轮表 = rules-v5.tsv，内容与 v4 逐字节相同、本 round 未动；残差列 $3 对 scoped 兄弟行重复声明同一 pattern 残差，直接求和会双重计数，unique-path/逐单元口径以 §11.6 正检 DIST/CASE-DIST 行为准）**：

```
awk -F'\t' 'NR>1 && $4=="UNBOUND" || NR>1 && $4=="UNBOUND_HC" {s[$4]+=$3} END {print "UNBOUND="s["UNBOUND"], "UNBOUND_HC="s["UNBOUND_HC"]}' C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/rules-v5.tsv
→ 残差列口径 UNBOUND=71 UNBOUND_HC=51；比 unique-path DIST（UNBOUND 56/UNBOUND_HC 45）多出的 15/6 条 = scoped 兄弟行（goldenInputs UNBOUND 面、handler outcome/reason UNBOUND_HC 行）在自身 pattern 上重复声明的残差（该残差已由表序在前的绑定类行声明并裁决）；UNBOND 合计 71 与 v3 数值相同但 factsAfter 段行构成不同——v3 两条无 where 的 factsAfter UNBOUND 行（B11 残差 2）在 v4 变为 6 条带 `;where=` 的行：index 拆出 INTERNAL-wall 行 + UNA-hand/discard 行（B11 行限定 deadWall|replacement）、seat 拆出 m5 OUT_DIFF-where 行 + UNA-wall/deadWall/replacement 行（B11 行限定非 m5 手牌容器），factsAfter 段进 UNBOUND 列的仍只有两条 B11 行各残差 1
awk -F'\t' 'NR>1 && $7 ~ /^GAP/ {t[$7]+=$3} END {for (k in t) print k, t[k]}' C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/rules-v5.tsv | sort
→ 残差列口径：B1 14 / B10 13 / B11 2 / B2 1 / B3 6 / B4 26 / B5 10 / B6 3 / B7 1 / B8 9 / B9 2 / E1 6 / E2 24 / E3 5（合计 122）。与 unique-path DIST（gap 合计 101）的差异全部在 mixed 路径：B10 的 13 条残差全部住在同叶路径的绑定类行之下（`DIST gap=GAP:N3-B10` 不出现），B11 的 2 条残差 = 第 6 轮新增的 index-B11 行与 seat-B11 行各 1（unique-path 口径 B11 路径已被 m5 OUT_DIFF/INTERNAL 兄弟行持有，`DIST gap=GAP:N3-B11` 不出现）；B8 残差 9 = 3 条 note 路径 + 6 条 scoped outcome/reason UNBOUND_HC 兄弟行重复声明，unique-path 口径 B8 = 3（`DIST gap=GAP:N3-B8 paths=3`）、逐单元口径 B8 = 191（`CASE-DIST gap=GAP:N3-B8 units=191`）——三层口径的换算关系由本命令 + 正检 DIST/CASE-DIST 行共同机器可核
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

### 11.8 判级变异探针与 caller 反例独立复现（第 5 轮：16 探针自足驱动 + 负向自检内嵌；R4/R3/R2 历史运行存档引用）

第 5 轮探针驱动源码全文内嵌如下（交付实测 SHA256 `fab9222a5ebb103fc2ac04ed9a1dc0655bd87a5647bce2c103b15db172abd256`；变异全部发生在 R5 TEMP 副本（`fake-repo`），真实工作树零改动）。本驱动为 R4-3 返修件，对第 4 轮驱动的四项硬化：(1) **无旧 TEMP 依赖**——历史第 3 轮校验器 + 417 规则表改从本报告 `git 7ea62e6` 提取并锁 SHA（`1CE17DAC…`/`D932A03F…`），不再读 R3 TEMP；(2) **全新 census**——每轮重跑 1516 路径清点（六 fixture 296/419/200/204/199/198）而非引用旧产物；(3) **逐探针期望校验 + 失败累计**——16 个探针各带 `nativeExit/Total/Failed/Skipped/NotRun` 期望值，任何失配进 failures 累计、末尾统一判 ROUND5-PROBES-FAILED → exit 1（不再出现"期望失配仍 exit 0"）；(4) **负向自检**——`-SelfTest -SkipMutation <probe>` 跳过某变异后运行，若失败集恰好只含该探针自身的期望失配 → 判 SELFTEST-PASS（守卫生效），证明"变异被静默剥离 → 正常模式必 exit 1"。本驱动兼负两职：**(a) 对第 3 轮旧校验器复现 caller 三类 checker 反例**（nonexistent-anchors / overlap-duplicate / overlap-and-kind，§3.0.1 反例表），**(b) 16 探针 = 重跑第 4 轮 7 探针 + 9 个新探针**（6 不设防实证 + 3 正控制，§10 表 21-29）。提取命令见 §11.10。

```powershell
<!--TOOL:probes-round5:BEGIN-->
# N-3 round-5: self-contained verification driver (R4-3 rework).
# Hardening over the round-4 driver, driven by the round-4 review:
#   1. NO old-TEMP dependency: the historical round-3 checker + 417-rule table
#      are extracted from GIT COMMIT 7ea62e6 of THIS report (SHA-pinned below);
#      the census is regenerated fresh from the report-embedded FullCensus
#      tool blocks. -SourceDir is gone.
#   2. EXPLICIT PER-PROBE EXPECTATIONS: every probe declares expected
#      nativeExit/Total/Failed/Skipped/NotRun (and the A-probes pin the exact
#      historical blind-spot magnitudes). ANY mismatch is accumulated; the
#      driver exits non-zero if any expectation fails. Stripping a mutation
#      (e.g. the Q2 rank=99 positive control) flips that probe's actuals away
#      from its expectations and the driver FAILS - the round-4 silent-pass
#      hole is closed.
#   3. -SkipMutation <name> runs a probe with the ORIGINAL fixture (no
#      mutation applied); its expectation then fails and the driver exits 1.
#      -SelfTest is the documented negative self-test: it runs the whole
#      driver with the physical-boundary-control mutation skipped and REQUIRES
#      exactly that one failure (exit 0 = guard works, otherwise exit 1).
# Probe families:
#   A (historical R3 checker blind spots, checker+rules pinned from 7ea62e6):
#     A1 nonexistent-anchors   fake file:line anchors -> OLD checker exit 0 (blind)
#     A2 overlap-duplicate     duplicated $.clockUsed rule -> OLD checker exit 0 (blind)
#     A3 overlap-and-kind      driver's own matcher -> OVERLAPPING_PATHS=935
#                              SCALAR_AS_CONTAINER=1 (exact historical counts)
#   B (mutated fixture copies under a fake repo; real tree untouched):
#     physical-hash-labels             two hashCrosswalk labels -> WRONG     : gap  (9 PASS)
#     physical-boundary-control        bound tiles[0].rank -> 99             : control (1 FAIL)
#     cheat-request-exception          beamSwap requestException -> WRONG    : gap  (16 PASS)
#     cheat-unbound-reason             tm-wrong-phase reason -> NOT_YOUR_TURN: gap  (16 PASS)
#     cheat-six-goldeninputs           6 B3 goldenInputs leaves WRONG/999    : gap  (16 PASS)
#     cheat-backfill-coherent          m1b resolved/backfilled/fact.index+100: gap  (16 PASS)
#     sleeve-input-relative-turns      offerCreatedTurnNumber+turnNumber+100 : gap  (16 PASS)
#     sleeve-input-relative-entities   drawnTileId+offerFirstTileId+1        : gap  (16 PASS)
#     m5-facts-unchecked-coordinate    m5 hand/discard index=999, wall seat  : gap  (16 PASS)
#     sleeve-unbound-cost-choose-second   cost=999 on sb-choose-second       : gap  (16 PASS)
#     sleeve-unbound-seat-choose-first    seat=999 on sb-choose-first        : gap  (16 PASS)
#     sleeve-unbound-drawkind-choose-second drawKind=WRONG on sb-choose-second: gap (16 PASS)
#     sleeve-input-bound-control       bound act goldenInputs.cost -> 999    : control (1 FAIL)
#     m5-facts-hand-seat-control       m5 hand/discard seat -> 999           : control (1 FAIL)
#     sleeve-input-relation-break      offerCreatedTurnNumber+100 single side: control (1 FAIL)
#     ai-clock-metadata                decisionClock base/reserveMillis->999 : control (1 FAIL)
#                                      (R4 Q7 outcome: positive control proving the
#                                       ai decisionClock 8 paths ARE bound)
param([string]$Repo = 'D:/majiang-wt/n3-golden-field-audit',
      [Parameter(Mandatory=$true)][string]$Evidence,
      [string]$ReportPath = '',
      [string]$SkipMutation = '',
      [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$dotnet = 'D:/Unity/dotnet/dotnet.exe'
# CJK-safe: build the v5-重构 path segment from code points (PS 5.1 reads BOM-less
# .ps1 as ANSI, so literal CJK in this file would mangle)
$cjkDir = 'doc/v5-' + [string][char]0x91CD + [string][char]0x6784 + '/V5-G1-N3-GOLDEN-FIELD-AUDIT.md'
if (-not $ReportPath) { $ReportPath = Join-Path $Repo $cjkDir }
if ($SelfTest -and -not $SkipMutation) { $SkipMutation = 'physical-boundary-control' }
$failures = New-Object System.Collections.Generic.List[string]
function Add-Failure([string]$msg) { $failures.Add($msg); Write-Output ('EXPECTATION-FAILED ' + $msg) }

# ---------- step 0: preflight ----------
New-Item -ItemType Directory -Force -Path $Evidence | Out-Null
$Evidence = (Get-Item $Evidence).FullName
foreach ($d in @("$Repo/AGENTS.md",
                 "$Repo/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll",
                 "$Repo/dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll",
                 $ReportPath)) {
  if (-not (Test-Path $d)) { throw ('preflight missing: ' + $d) }
}

# ---------- step 1: historical round-3 checker + rules from git 7ea62e6 (SHA-pinned) ----------
$archive = Join-Path $Evidence 'archive-r3'
New-Item -ItemType Directory -Force -Path $archive | Out-Null
$rel = $cjkDir
& cmd /c ("git -C `"$Repo`" show 7ea62e6:`"$rel`" > `"" + (Join-Path $archive 'r3-report.md') + "`"") | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'git show 7ea62e6 failed' }
$r3md = Join-Path $archive 'r3-report.md'
$lines = [IO.File]::ReadAllLines($r3md)
$vm = New-Object System.Collections.Generic.List[string]
$inf = $false
foreach ($l in $lines) {
  if ($l -eq '<!--TOOL:verify-mapping:BEGIN-->') { $inf = $true; continue }
  if ($l -eq '<!--TOOL:verify-mapping:END-->') { $inf = $false; continue }
  if ($inf) { $vm.Add($l) }
}
# LF-only write: the awk-extracted pins are LF; WriteAllLines would emit CRLF and break the SHA
[IO.File]::WriteAllText((Join-Path $archive 'verify-mapping-r3.ps1'), ($vm -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
$fr = New-Object System.Collections.Generic.List[string]
$inf = $false
foreach ($l in $lines) {
  if ($l -eq 'FIELD-RULES:BEGIN') { $inf = $true; continue }
  if ($l -eq 'FIELD-RULES:END') { $inf = $false; continue }
  if ($inf -and -not $l.StartsWith('```')) { $fr.Add($l) }
}
[IO.File]::WriteAllText((Join-Path $archive 'field-rules-r3.tsv'), ($fr -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
# ASCII-only artifacts: PS line round-trip is byte-safe; SHA-pin against tampering
$pinVm = (Get-FileHash -Algorithm SHA256 (Join-Path $archive 'verify-mapping-r3.ps1')).Hash
$pinFr = (Get-FileHash -Algorithm SHA256 (Join-Path $archive 'field-rules-r3.tsv')).Hash
$expVm = '1CE17DAC4F663714492315D30F85C173B0E4EF0506F4BDF0175A03BDA5B793C3'
$expFr = 'D932A03FF9A6B958576ADF51A87DEEE684C88776F726F4546257D4EDE51EE73D'
Write-Output ('archive-r3/verify-mapping-r3.ps1 sha256=' + $pinVm)
Write-Output ('archive-r3/field-rules-r3.tsv sha256=' + $pinFr)
if ($pinVm -ne $expVm) { Add-Failure ('archive verify-mapping-r3.ps1 SHA mismatch: ' + $pinVm) }
if ($pinFr -ne $expFr) { Add-Failure ('archive field-rules-r3.tsv SHA mismatch: ' + $pinFr) }

# ---------- step 2: fresh census from the report-embedded FullCensus ----------
$censusOut = Join-Path $Evidence 'census-out'
$extracted = Join-Path $Evidence 'extracted'
New-Item -ItemType Directory -Force -Path $censusOut, $extracted | Out-Null
function Extract-Tool([string]$name, [string]$dest) {
  $out = New-Object System.Collections.Generic.List[string]
  $inf = $false
  foreach ($l in [IO.File]::ReadAllLines($ReportPath)) {
    if ($l -eq ('<!--TOOL:' + $name + ':BEGIN-->')) { $inf = $true; continue }
    if ($l -eq ('<!--TOOL:' + $name + ':END-->')) { $inf = $false; continue }
    if ($inf) { $out.Add($l) }
  }
  [IO.File]::WriteAllLines($dest, $out)
  if ($out.Count -eq 0) { throw ('tool block empty: ' + $name) }
}
Extract-Tool 'fullcensus-csproj' (Join-Path $extracted 'FullCensus.csproj')
Extract-Tool 'fullcensus-program' (Join-Path $extracted 'Program.cs')
Push-Location $Evidence
try {
  & $dotnet run --project (Join-Path $extracted 'FullCensus.csproj') -- $Repo $censusOut 1> (Join-Path $Evidence 'census-run.log') 2>&1
  $rc = $LASTEXITCODE
} finally { Pop-Location }
if ($rc -ne 0) { Add-Failure ('census regeneration nativeExit=' + $rc) }
$tail = (Get-Content (Join-Path $Evidence 'census-run.log') | Select-Object -Last 1)
Write-Output ('census tail: ' + $tail)
if ($tail -notmatch 'FULL-CENSUS-DONE failures=0') { Add-Failure ('census tail unexpected: ' + $tail) }
$expPaths = @{ 'physical-v1' = 296; 'scoring-v1' = 419; 'round-v1' = 200; 'cheat-v1' = 204; 'shuffle-v1' = 199; 'ai-v1' = 198 }
foreach ($fx in $expPaths.Keys) {
  $n = @((Get-Content (Join-Path $censusOut ($fx + '-full-paths.txt')))).Count
  if ($n -ne $expPaths[$fx]) { Add-Failure ('census ' + $fx + ' paths=' + $n + ' expected=' + $expPaths[$fx]) }
}
Write-Output ('census regenerated: ' + (($expPaths.Keys | ForEach-Object { $_ + '=' + $expPaths[$_] }) -join ' '))

# ---------- step 3: A-probes - historical R3 checker blind spots (expectations!) ----------
$rules3 = Join-Path $Evidence 'rules-r3.tsv'
Copy-Item (Join-Path $archive 'field-rules-r3.tsv') $rules3 -Force
$checker3 = Join-Path $Evidence 'verify-mapping-r3.ps1'
Copy-Item (Join-Path $archive 'verify-mapping-r3.ps1') $checker3 -Force
function Invoke-OldCheck([string]$Name, [string[]]$RuleLines) {
  $p = Join-Path $Evidence ($Name + '.tsv')
  [IO.File]::WriteAllText($p, ($RuleLines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
  & powershell -NoProfile -ExecutionPolicy Bypass -File $checker3 -Mode check -RulesFile $p -CensusDir $censusOut 1> (Join-Path $Evidence ($Name + '.log')) 2>&1
  return $LASTEXITCODE
}
$ruleLines = @(Get-Content -Encoding UTF8 $rules3 | Where-Object { $_ -match "`t" })
# A1 nonexistent-anchors: OLD checker must stay exit 0 (the round-3 blind spot)
$fake = @($ruleLines | ForEach-Object {
  $c = $_ -split "`t"
  if ($c.Count -ge 7 -and $c[5] -match '\.cs:\d+') { $c[4] = 'NonexistentExporter.kt:999999'; $c[5] = 'NonexistentConsumer.cs:999999' }
  $c -join "`t"
})
$a1 = Invoke-OldCheck 'nonexistent-anchors' $fake
Write-Output ('A1 nonexistent-anchors nativeExit=' + $a1 + ' (expected 0 = historical blind spot reproduced)')
if ($a1 -ne 0) { Add-Failure ('A1 nonexistent-anchors expected old-checker exit 0, got ' + $a1) }
# A2 overlap-duplicate: OLD checker must stay exit 0
$dup = $ruleLines + ('physical-v1' + "`t" + '$.clockUsed' + "`t0`tSEM_MAP`tV5PhysicalGoldenExporterTest.kt`tPhysicalGoldenTests.cs:41`tBOUND`t*")
$a2 = Invoke-OldCheck 'overlap-duplicate' $dup
Write-Output ('A2 overlap-duplicate nativeExit=' + $a2 + ' (expected 0 = historical blind spot reproduced)')
if ($a2 -ne 0) { Add-Failure ('A2 overlap-duplicate expected old-checker exit 0, got ' + $a2) }
# A3 exact historical blind-spot magnitudes via the driver's own matcher
function Test-Pattern([string]$pat, [string]$path) {
  if ($pat -eq '$') { return ($path -eq '$') }
  if ($path -eq '$') { return $false }
  $pp = $pat.Substring(2).Split('.'); $aa = $path.Substring(2).Split('.')
  for ($i = 0; $i -lt $pp.Count; $i++) {
    if ($pp[$i] -eq '#') { return ($i -eq $pp.Count - 1) }
    if ($i -ge $aa.Count) { return $false }
    if ($pp[$i] -ne '*' -and $pp[$i] -cne $aa[$i]) { return $false }
  }
  return ($pp.Count -eq $aa.Count)
}
$fixtures = @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')
$detail = New-Object System.Collections.Generic.List[string]
$overlapTotal = 0; $scalarAsContainer = 0
foreach ($fx in $fixtures) {
  $fr3 = @($ruleLines | Where-Object { ($_ -split "`t")[0] -eq $fx } | ForEach-Object { $c = $_ -split "`t"; [pscustomobject]@{ pat = $c[1]; cls = $c[3] } })
  foreach ($line in Get-Content (Join-Path $censusOut ($fx + '-full-paths.txt'))) {
    $sp = $line.Split(' '); $path = $sp[0]; $kind = $sp[1]
    $hits = @($fr3 | Where-Object { Test-Pattern $_.pat $path })
    if ($hits.Count -gt 1) { $overlapTotal++; $detail.Add('OVERLAP ' + $fx + ' ' + $path + ' rules=' + $hits.Count) }
    if ($hits.Count -gt 0 -and $hits[0].cls -eq 'CONTAINER' -and $kind -notmatch '^(object|array)$') { $scalarAsContainer++; $detail.Add('SCALAR-AS-CONTAINER ' + $fx + ' ' + $line) }
  }
}
$detail | Set-Content -Encoding UTF8 (Join-Path $Evidence 'overlap-and-kind-details.log')
Write-Output ('A3 OVERLAPPING_PATHS=' + $overlapTotal + ' SCALAR_AS_CONTAINER=' + $scalarAsContainer + ' (expected 935 / 1)')
if ($overlapTotal -ne 935) { Add-Failure ('A3 OVERLAPPING_PATHS=' + $overlapTotal + ' expected 935') }
if ($scalarAsContainer -ne 1) { Add-Failure ('A3 SCALAR_AS_CONTAINER=' + $scalarAsContainer + ' expected 1') }

# ---------- step 4: B-probes - mutated fixture copies with expectations ----------
$fake = Join-Path $Evidence 'fake-repo'
New-Item -ItemType Directory -Force -Path "$fake/tests/v5-golden" | Out-Null
Copy-Item "$Repo/AGENTS.md" "$fake/AGENTS.md" -Force
foreach ($m in @('Domain','Ai')) {
  $src = "$Repo/dotnet/tests/Mahjong.$m.Tests/bin/Release/net8.0"
  $dst = "$fake/dotnet/tests/Mahjong.$m.Tests/bin/Release/net8.0"
  if (-not (Test-Path $dst)) { Copy-Item $src $dst -Recurse -Force }
}
$phy = Get-Content -Raw -Encoding UTF8 "$Repo/tests/v5-golden/physical-v1.json" | ConvertFrom-Json
foreach ($src in @($phy.v4SourceHashes | ForEach-Object { $_.file }) + 'engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt') {
  $dest = Join-Path $fake $src
  New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
  Copy-Item (Join-Path $Repo $src) $dest -Force
}
# expectation rows: name | fixture | test class | exit | total | failed | skipped | notrun
$expect = @(
  @{n='physical-hash-labels';            f='physical-v1'; c='PhysicalGoldenTests';  e=0; t=9;  fl=0},
  @{n='physical-boundary-control';       f='physical-v1'; c='PhysicalGoldenTests';  e=1; t=9;  fl=1},
  @{n='cheat-request-exception';         f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='cheat-unbound-reason';            f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='cheat-six-goldeninputs';          f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='cheat-backfill-coherent';         f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='sleeve-input-relative-turns';     f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='sleeve-input-relative-entities';  f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='m5-facts-unchecked-coordinate';   f='cheat-v1';    c='CheatGoldenTests';     e=0; t=16; fl=0},
  @{n='sleeve-unbound-cost-choose-second';    f='cheat-v1'; c='CheatGoldenTests';   e=0; t=16; fl=0},
  @{n='sleeve-unbound-seat-choose-first';     f='cheat-v1'; c='CheatGoldenTests';   e=0; t=16; fl=0},
  @{n='sleeve-unbound-drawkind-choose-second'; f='cheat-v1'; c='CheatGoldenTests';  e=0; t=16; fl=0},
  @{n='sleeve-input-bound-control';      f='cheat-v1';    c='CheatGoldenTests';     e=1; t=16; fl=1},
  @{n='m5-facts-hand-seat-control';      f='cheat-v1';    c='CheatGoldenTests';     e=1; t=16; fl=1},
  @{n='sleeve-input-relation-break';     f='cheat-v1';    c='CheatGoldenTests';     e=1; t=16; fl=1},
  @{n='ai-clock-metadata';               f='ai-v1';       c='AiReplayGoldenTests';  e=1; t=3;  fl=1}  # positive control (R4 Q7: decisionClock bound)
)
$mutations = @{
  'physical-hash-labels'                  = { param($j) $j.hashCrosswalk.csharpHashVerification = 'WRONG'; $j.hashCrosswalk.v4Reason = 'WRONG' }
  'physical-boundary-control'             = { param($j) $j.samples[0].expected.tiles[0].rank = 99 }
  'cheat-request-exception'               = { param($j) ($j.beamSwapHandler | Where-Object requestException).requestException = 'WRONG' }
  'cheat-unbound-reason'                  = { param($j) ($j.tileMarkHandler | Where-Object caseId -eq 'tm-wrong-phase-preparation').reason = 'NOT_YOUR_TURN' }
  'cheat-six-goldeninputs'                = { param($j)
    foreach ($c in $j.sleeveBloomHandler) {
      if ($c.goldenInputs) {
        foreach ($p in @('availableWallCount','offerId','rinshanPointer','wallEndExclusive','wallPointer','windowId')) {
          if ($c.goldenInputs.PSObject.Properties.Name -contains $p) {
            if ($c.goldenInputs.$p -is [string]) { $c.goldenInputs.$p = 'WRONG' } else { $c.goldenInputs.$p = 999 }
          }
        }
      }
    } }
  'cheat-backfill-coherent'               = { param($j)
    $m = $j.tileMovement | Where-Object caseId -eq 'mv-swap-drawn-with-wall-backfill'
    $m.resolvedIndex += 100; $m.backfilledIndex += 100
    ($m.factsAfter | Where-Object tileId -eq $m.drawnTileId).index += 100 }
  'sleeve-input-relative-turns'           = { param($j) $g = ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-choose-second').goldenInputs; $g.offerCreatedTurnNumber += 100; $g.turnNumber += 100 }
  'sleeve-input-relative-entities'        = { param($j) $g = ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-choose-second').goldenInputs; $g.drawnTileId += 1; $g.offerFirstTileId += 1 }
  'm5-facts-unchecked-coordinate'         = { param($j)
    $g = $j.tileMovement | Where-Object caseId -eq 'mv-reservation-delivery-then-beam'
    foreach ($f in $g.factsAfter) {
      if ($f.container -eq 'hand' -or $f.container -eq 'discard') { $f.index = 999 }
      if ($f.container -eq 'wall') { $f.seat = 999 }
    } }
  'sleeve-unbound-cost-choose-second'     = { param($j) ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-choose-second').goldenInputs.cost = 999 }
  'sleeve-unbound-seat-choose-first'      = { param($j) ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-choose-first').goldenInputs.seat = 999 }
  'sleeve-unbound-drawkind-choose-second' = { param($j) ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-choose-second').goldenInputs.drawKind = 'WRONG' }
  'sleeve-input-bound-control'            = { param($j) ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-activate-allowed').goldenInputs.cost = 999 }
  'm5-facts-hand-seat-control'            = { param($j)
    $g = $j.tileMovement | Where-Object caseId -eq 'mv-reservation-delivery-then-beam'
    foreach ($f in $g.factsAfter) { if ($f.container -eq 'hand' -or $f.container -eq 'discard') { $f.seat = 999 } } }
  'sleeve-input-relation-break'           = { param($j) $g = ($j.sleeveBloomHandler | Where-Object caseId -eq 'sb-choose-second').goldenInputs; $g.offerCreatedTurnNumber += 100 }
  'ai-clock-metadata'                     = { param($j) $j.decisionClock.baseMillis = 999; $j.decisionClock.reserveMillis = 999 }
}
$summary = New-Object System.Collections.Generic.List[string]
foreach ($x in $expect) {
  $name = $x.n
  $j = Get-Content -Raw -Encoding UTF8 "$Repo/tests/v5-golden/$($x.f).json" | ConvertFrom-Json
  if ($name -ne $SkipMutation) { & $mutations[$name] $j }
  else { Write-Output ('SKIP-MUTATION ' + $name + ' (self-test: original fixture, expectation MUST fail)') }
  $dst = "$fake/tests/v5-golden/$($x.f).json"
  [IO.File]::WriteAllText($dst, ($j | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
  Copy-Item $dst (Join-Path $Evidence ($name + '.json')) -Force
  $module = if ($x.c -eq 'AiReplayGoldenTests') { 'Ai' } else { 'Domain' }
  if ($module -eq 'Ai') { Copy-Item $dst "$fake/dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/ai-v1.json" -Force }
  $dll = "$fake/dotnet/tests/Mahjong.$module.Tests/bin/Release/net8.0/Mahjong.$module.Tests.dll"
  $old = $env:MAHJONG_REPO_ROOT; $env:MAHJONG_REPO_ROOT = $fake
  try {
    & $dotnet $dll -class "Mahjong.$module.Tests.$($x.c)" -noColor -noLogo 1> (Join-Path $Evidence ($name + '.log')) 2>&1
    $rc = $LASTEXITCODE
  } finally { $env:MAHJONG_REPO_ROOT = $old }
  $sum = ((Get-Content (Join-Path $Evidence ($name + '.log')) | Select-String 'Total:') | Select-Object -First 1).Line.Trim()
  $m = [regex]::Match($sum, 'Total: (\d+), Errors: \d+, Failed: (\d+), Skipped: (\d+), Not Run: (\d+)')
  if (-not $m.Success) { Add-Failure ($name + ' cannot parse summary line: ' + $sum); continue }
  $total = [int]$m.Groups[1].Value; $failed = [int]$m.Groups[2].Value; $skipped = [int]$m.Groups[3].Value; $notrun = [int]$m.Groups[4].Value
  $fxSha = (Get-FileHash -Algorithm SHA256 $dst).Hash
  $logSha = (Get-FileHash -Algorithm SHA256 (Join-Path $Evidence ($name + '.log'))).Hash
  $line = $name + ' nativeExit=' + $rc + ' (exp ' + $x.e + ') | ' + $sum + ' (exp total=' + $x.t + ' failed=' + $x.fl + ' skipped=0 notrun=0)' + ' | mutatedFixtureSha256=' + $fxSha + ' | logSha256=' + $logSha
  Write-Output $line
  $summary.Add($line)
  if ($rc -ne $x.e) { Add-Failure ($name + ' nativeExit=' + $rc + ' expected ' + $x.e) }
  if ($total -ne $x.t) { Add-Failure ($name + ' Total=' + $total + ' expected ' + $x.t) }
  if ($failed -ne $x.fl) { Add-Failure ($name + ' Failed=' + $failed + ' expected ' + $x.fl) }
  if ($skipped -ne 0) { Add-Failure ($name + ' Skipped=' + $skipped + ' expected 0') }
  if ($notrun -ne 0) { Add-Failure ($name + ' NotRun=' + $notrun + ' expected 0') }
}
$summary | Set-Content -Encoding UTF8 (Join-Path $Evidence 'probe-summary.txt')

# ---------- verdict ----------
if ($SelfTest) {
  $foreign = @($failures | Where-Object { -not $_.StartsWith($SkipMutation + ' ') })
  if ($failures.Count -ge 1 -and $foreign.Count -eq 0) {
    Write-Output ('SELFTEST-PASS (skipped mutation produced only its own expectation failures: ' + $failures.Count + '; guard works)')
    exit 0
  }
  Write-Output ('SELFTEST-FAIL failures=' + $failures.Count + ' foreign=' + $foreign.Count + ' (expected failures ONLY for skipped probe ' + $SkipMutation + ')')
  $failures | ForEach-Object { Write-Output ('  ' + $_) }
  exit 1
}
if ($failures.Count -eq 0) {
  Write-Output 'ROUND5-PROBES-COMPLETE (all A/B expectations matched)'
  exit 0
}
Write-Output ('ROUND5-PROBES-FAILED failures=' + $failures.Count)
$failures | ForEach-Object { Write-Output ('  ' + $_) }
exit 1
<!--TOOL:probes-round5:END-->
```

**第 5 轮完整命令（-File 形式，exit 0 = 反例复现 + 16 探针期望全部匹配）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/probes-round5.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/probes-run2 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/probes-run2/driver-run.log 2>&1
→ TRUE_EXIT=0；关键行：archive-r3/verify-mapping-r3.ps1 sha256=1CE17DAC4F663714492315D30F85C173B0E4EF0506F4BDF0175A03BDA5B793C3、archive-r3/field-rules-r3.tsv sha256=D932A03FF9A6B958576ADF51A87DEEE684C88776F726F4546257D4EDE51EE73D（git 7ea62e6 提取锁 SHA 全中）、census regenerated: cheat-v1=204 shuffle-v1=199 ai-v1=198 physical-v1=296 scoring-v1=419 round-v1=200 + census tail: FULL-CENSUS-DONE failures=0、A3 OVERLAPPING_PATHS=935 SCALAR_AS_CONTAINER=1 (expected 935 / 1)、16 行探针结果（下表）+ 尾行 ROUND5-PROBES-COMPLETE (all A/B expectations matched)（driver-run.log SHA256 e622e20102207c6be9fb229c7497b50451fe99de1481f3003f46e50ba2cc352f，census-run.log SHA256 6b7c79e2de1e0dc7b170972ac0cd1a6bba2951b02bddd310805f49195d7d6a23；逐探针日志 16 份 + 变异 fixture 16 份 + archive-r3/ + census-out/ + extracted/ 同目录在案）
```

**16 探针逐行结果（R5 TEMP `probes-run2` 实测；变异列与工具 `$mutations` 哈希表逐字一致；期望列 = 工具 `$expect` 表）**：

| # | 探针 | 变异（逐字） | native exit（期望） | 实测结果 | 探针日志 SHA256 | 变异 fixture SHA256 |
|---|---|---|---|---|---|---|
| P1 | physical-hash-labels | `hashCrosswalk.csharpHashVerification`→'WRONG' + `v4Reason`→'WRONG'（仅两标签；targetIds/suitOrder 不在变异范围） | 0（exp 0） | Mahjong.Domain.Tests Total: 9, **Failed: 0**——E3 双标签确不设防 | `0D662BB585AA1997D68767EDE4191EC0E138A26C7E0D39377CEB138900991844` | `7B605C10BB15D9E250F1E3B09A78D8C8AB27B08C6F45175DFF00E6CAAAE87DA2` |
| P2 | physical-boundary-control（正控制①） | `samples[0].expected.tiles[0].rank`→99 | **1**（exp 1） | Total: 9, **Failed: 1**——physical 数值面设防实证 | `385D55037560E678774507B56169C5B802B20BECBA5B5C99E10613BDAECC2C8C` | `99DA2673A3D0AA76E69C68BE2B26A5AFB35EBB13753441EA65476AF355E85BFF` |
| P3 | cheat-request-exception | `beamSwapHandler` 含 requestException 案例的 `requestException`→'WRONG' | 0（exp 0） | Total: 16, **Failed: 0**——B7 确不设防 | `0D2816A16668E01D3BEF2698DBCF00DA7057E57C8B81B9FD3743BD760E08E6B0` | `756795243F50C318D7BE71A65D2580C6EB4CC998B0EF5E55AECE50252C796EAC` |
| P4 | cheat-unbound-reason | `tileMarkHandler` case `tm-wrong-phase-preparation` 的 `reason`→'NOT_YOUR_TURN' | 0（exp 0） | Total: 16, **Failed: 0**——UNBOUND_HC 拆分行确不设防 | `EE8787A3B0CC72C36814AA77001CBF839E3E2D64FF3F6C8779B48C7883EC0B7F` | `FEDD45B579EC11A5DCDAC064A1AC0351524D9587B1ADAEBA4D84B6DF059D0D86` |
| P5 | cheat-six-goldeninputs | sleeveBloom 全案例六叶（availableWallCount/offerId/rinshanPointer/wallEndExclusive/wallPointer/windowId）逐 case 在场者：字符串叶→'WRONG'、数值叶→999（非整体删除） | 0（exp 0） | Total: 16, **Failed: 0**——B3 六叶确不设防 | `4CC28600A5DE23F48A288403646FA5284205BA73B3F0566F5AC4B6AC15F22FCD` | `6656871D7823F5BC096D97CA153A6F74DEE88F6AA72F17E11307B5DE344CF016` |
| P6 | cheat-backfill-coherent | `mv-swap-drawn-with-wall-backfill` 的 `resolvedIndex`+100、`backfilledIndex`+100、`factsAfter[drawnTileId 匹配行].index`+100（INTERNAL 判级复核） | 0（exp 0） | Total: 16, **Failed: 0**——与 §3.4 INTERNAL 判级一致（结构自洽断言无生产差分语义，非 GAP） | `B6636D6A9CB7C1139BD1673D38EF24683ACF469EE40886BDD3CECBBE397B1E58` | `C734E77956673D8DF37ABE2C85FF9C7AE5ACC0B5B24819EF88293499D435B073` |
| P7 | sleeve-input-relative-turns | sb-choose-second goldenInputs `offerCreatedTurnNumber`+100 与 `turnNumber`+100（关系对双侧） | 0（exp 0） | Total: 16, **Failed: 0**——内部相对回合非真输入绑定（B10 面） | `C479E9A1E4756F540CFC3808E0D83AF0366DBDEFEACC06709B7AD5EA3E6D7576` | `A1EAB34E135D136F474154F7B51CA208AE6EA0A28630166357C5B67E447E9A13` |
| P8 | sleeve-input-relative-entities | sb-choose-second goldenInputs `drawnTileId`+1 与 `offerFirstTileId`+1（关系对双侧） | 0（exp 0） | Total: 16, **Failed: 0**——内部相对实体非真输入绑定（B10 面） | `73C315F361A7B082F18A73E2DEAC9D20952052576C4601F52EBBAA5AF6DB9A5F` | `ADADD797EB01086B68706C7DAC965692FADA88E2B6324938F459E590815C9664` |
| P9 | m5-facts-unchecked-coordinate | `mv-reservation-delivery-then-beam` factsAfter：container=hand/discard 行 `index`→999 + container=wall 行 `seat`→999 | 0（exp 0） | Total: 16, **Failed: 0**——**第 6 轮 R5-2 更正口径**：实际生效变异 = m5 hand/discard 分区 `index` **2 实例 null→999**（证 null index 无读点，判 UNA 非"排除"）；`seat`→999 半支针对 container=wall 行——m5 fixture wall facts = **0**（caller E 目录 `M5 wallFacts=0` 同测，§11.12），该半支**零命中 NOT_EXERCISED**，不构成 wall 面证据；第 5 轮"分区排除语义确不设防（B11 悬空坐标）"以偏概全表述作废 | `460EF7A9B915D6C9210404F0893AFAC7073230648044C379C366BE78324A08A3` | `A4964643F9B532654EBDA9F0771313B016210BF17C6FF7F0E5698499F9866916` |
| P10 | sleeve-unbound-cost-choose-second | sb-choose-second goldenInputs `cost`→999（B10 代表单元①） | 0（exp 0） | Total: 16, **Failed: 0** | `DEECC0C060D0D0D99AD3A392BC262951ED897CE8701C6809D09CD0180BED8856` | `6C920C72932143624E786FDD93927BD164CC06211B8F084D973763ACA8F87D8D` |
| P11 | sleeve-unbound-seat-choose-first | sb-choose-first goldenInputs `seat`→999（B10 代表单元②） | 0（exp 0） | Total: 16, **Failed: 0** | `202092C81934EC3AB284F820576CD4FF55747D2C74CC76ABA5B4005DA6DC1AD9` | `28004414BD1AA4FA06AABF34CC89849AB7BB83510B63D742C72A1BEEB81F81BD` |
| P12 | sleeve-unbound-drawkind-choose-second | sb-choose-second goldenInputs `drawKind`→'WRONG'（B10 代表单元③） | 0（exp 0） | Total: 16, **Failed: 0** | `803136B0C9914D7A4FA665FD075F698C123306590537BDDE802663597B25244E` | `2493E156A3843639503C82C3A90A71C3EF8FCD7AB7DAE84B6CA49D2D1FBDF50A` |
| P13 | sleeve-input-bound-control（正控制⑥） | sb-activate-allowed goldenInputs `cost`→999（已绑面） | **1**（exp 1） | Total: 16, **Failed: 1**（`SleeveBloomSecondChoice_BindsToGoldenInputs`） | `64968528589BFB626ADA6E3DED6D96B6E2E3B99CC14232E715EE06F6A5887C30` | `1E24FFEE3753146A58CA05E4214F545BD0E615902CE5DC6945CFB0CD28BEE5A6` |
| P14 | m5-facts-hand-seat-control（正控制⑦） | `mv-reservation-delivery-then-beam` factsAfter：container=hand/discard 行 `seat`→999（已绑面） | **1**（exp 1） | Total: 16, **Failed: 1**（`DeliveryThenBeam_BindsToGoldenCandidateEntities`） | `2633DB8D41C0E2D64B21384C526C1C392DA1FB0B0AFF554905FC610E4B66BFE2` | `6F8FF7F2FE0B5DBF403903BA5992A04D662A9ABDFF852935E6621BFE725A4058` |
| P15 | sleeve-input-relation-break（正控制⑤） | sb-choose-second goldenInputs 仅 `offerCreatedTurnNumber`+100（关系对单侧破坏） | **1**（exp 1） | Total: 16, **Failed: 1**（`SleeveBloomSecondChoice_BindsToGoldenInputs`，Expected 112/Actual 12，CheatGoldenTests.cs:724） | `124E794024ED22042EFCD04E605FFC443F46B89D3FF73523C86F548BB1F01A1E` | `A7A49933B0F13B9ED34A60A4C0CD6C1A9E963801ADD2F61EB26F5B98CF39CEB9` |
| P16 | ai-clock-metadata（正控制④） | `decisionClock.baseMillis`→999 + `reserveMillis`→999 | **1**（exp 1） | Mahjong.Ai.Tests Total: 3, **Failed: 1**——decisionClock 8 路径全绑定实证 | `2571D66DA32D0BDA0F1906A5D40DFC5C321973F215134926DC2D17337A22329C` | `62838FE1B8ABAEA05A822A5B598BF0CA47AA6D14C103090DC597F422F32EA886` |

`probe-summary.txt`（16 行，Evidence 根平铺）SHA256 `8ac4168f03c4a7e3d9a313ebf6db19b8f2d9db9226bc0dab6b2eddffe20cb6d5`，其内容 = 上表 P1-P16 判定列逐字（nativeExit/Total/Failed/变异 fixture SHA/日志 SHA）。探针日志内 `Time:` 为实测耗时、随运行变化，判定行/exit/变异 fixture SHA 恒定；本表 SHA 为交付时在案实测值。cheat 绑定面的正控制由第 3 轮 P1（§10 表 18）+ 第 5 轮 P13/P14/P15（§10 表 27-29）共同承担。

**负向自检（R4-3 验收：变异被剥离 → 驱动必报失败）**：

```
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/probes-round5.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/selftest-run2 -SkipMutation physical-boundary-control -SelfTest 1> C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/selftest-run2/driver-selftest.log 2>&1
→ TRUE_EXIT=0；日志含 SKIP-MUTATION physical-boundary-control (self-test: original fixture, expectation MUST fail) + 该探针 1 FAIL 期望失配 ×2（nativeExit=0 exp 1 / Failed=0 exp 1）+ 尾行 SELFTEST-PASS (skipped mutation produced only its own expectation failures: 2; guard works)（driver-selftest.log SHA256 e42aa98030c348529cb813754db506348c10255769e428b926bfa43f5306142d）
```

语义：`-SelfTest` 模式下"失败集只含被跳探针自身的期望失配" = 守卫正确识别了被剥离的变异（对驱动逻辑的验收，故 exit 0）；等价地在**正常模式**下任何期望失配（含变异被静默剥离产生的 0 FAIL）都会进 failures → `ROUND5-PROBES-FAILED` → exit 1——第 4 轮"counterfactual 仍 exit 0 + COMPLETE"的缺陷由此堵死。

**第 4 轮运行（历史存档，证据在 R4 TEMP；7 探针由已被第 5 轮驱动取代的 `probes-round4.ps1` 执行，SHA256 `f64337f5cf175b062f17896487c74eae017493c1cb5198ca30bf3c7bf89b293c`，现锚定于本报告 `git 7ea62e6`）**：命令 `powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round4/probes-round4.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round4` → EXIT=0，尾行 ROUND4-PROBES-COMPLETE（probes-round4-run.log SHA256 f8c68326ff126ec201c1ed92a2d3d38064047444d76fdf3f4eb1934827bc67f7，probe-summary.txt 7 行 SHA256 60e1c7b4d3a0116be7a5a054412cdce630d3ca909e7b88ffa604a40a94fc513a）。历史日志 SHA 不变；第 4 轮报告曾有 4 处变异描述与工具实际行为不符（Q1 误写"targetIds 全改 WRONG"、Q3 误写"全字段删除"、Q4 误写"改 WRONG_PHASE"、Q5 误写"六叶整体删除"），第 5 轮按工具 `$mutations` 逐字更正如本表 P1/P3/P4/P5 变异列所示——七探针的判定行/exit/变异 fixture SHA 与本表 P1-P7 一致（同一变异代码逐字沿用，第 5 轮重跑同判）。

**第 3 轮 4 探针（存档引用，证据在 R3；结论已在 §10 表 7/9/10/12/18 引用）**：P1 sb-already-used-bound-positive（**1 FAIL exit 1**，A 桶正控制）、P2 sb-choose-first-unbound（16/16 PASS）、P3 tilemark-state-fields-unbound（16/16 PASS）、P4 ledger-markers-unbound（16/16 PASS）；工具源全文内嵌于第 3 轮报告 §11.8（`<!--TOOL:probes-state-fields:BEGIN/END-->`，SHA `bd9a403d01da8260979918e2e1509d1fcae1844da98b85f5a7a4599a79c0ef9c`，提取命令同 §11.10 awk 模式），probe-summary.txt SHA `539ad92504871baf61fbbc6583b0813109961e88c59db87b2d56299db06d224f`、run log SHA `d9595cd9d717dfb497488f77eb4d1796160322aa40c820041fe6b93df1c9dfc6`。

**第 2 轮 5 探针（存档引用，证据在 R2；结论已在 §10 表 3-7/11/17 引用）**：round-flags-mutated-true（6/6 PASS）、scoring-win-status-mutated（7/7 PASS）、positive-control-bound-score-mutation（**7 跑 1 FAIL exit 1**）、shuffle-deadwall-flag-mutated（4/4 PASS）、cheat-choose-first-mutated-rejected（16/16 PASS）；脚本 `review-probes.ps1` SHA256 `4d3896114c9f93937a4f886bcc0341904250214f2a5d06a79f7ba33a64597854`、`probe-summary.txt` SHA256 `d2d693fd1b4aedbaf064ef318aa884380a31ec2dce65865102a1fab37a44e353`（本报告 §10 结论与 R2 日志一致，35 个哈希不重复罗列）。

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

### 11.10 自抽取复算闭环（R2-2 验收沿用 + 第 7 轮工具集复算；空 TEMP 从本报告代码块重建全部工具并复算）

**提取命令（awk 按行内标记对切分；10 个工具 + 报告内嵌 FIELD-RULES 规则块，全部来自本报告 §11 代码块；第 7 轮（R6-1）新增独立 oracle `mixed-namespace-oracle`（§11.6），`verify-mapping` 升至 v5（§11.6）；`CensusInstances` 双件（§11.4b）与第 5 轮探针工具 `probes-round5` 沿用不变（R4-3/R5 验收通过，不再重写；本 round 未改任何 census/coverage/case-refs/probes 工具，提取件 SHA 与第 6 轮 LOOP 逐一相同））**：

```
EV=C:/Users/sncdc/AppData/Local/Temp/n3-extract-loop-r7
mkdir -p "$EV/extracted"
R=D:/majiang-wt/n3-golden-field-audit/doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md
awk '/^<!--TOOL:fullcensus-csproj:BEGIN-->/{f=1;next} /^<!--TOOL:fullcensus-csproj:END-->/{f=0} f' "$R" > "$EV/extracted/FullCensus.csproj"
awk '/^<!--TOOL:fullcensus-program:BEGIN-->/{f=1;next} /^<!--TOOL:fullcensus-program:END-->/{f=0} f' "$R" > "$EV/extracted/Program.cs"
awk '/^<!--TOOL:censusinstances-csproj:BEGIN-->/{f=1;next} /^<!--TOOL:censusinstances-csproj:END-->/{f=0} f' "$R" > "$EV/extracted/CensusInstances.csproj"
awk '/^<!--TOOL:censusinstances-program:BEGIN-->/{f=1;next} /^<!--TOOL:censusinstances-program:END-->/{f=0} f' "$R" > "$EV/extracted/CensusInstances-Program.cs"
awk '/^<!--TOOL:verify-coverage:BEGIN-->/{f=1;next} /^<!--TOOL:verify-coverage:END-->/{f=0} f' "$R" > "$EV/extracted/verify-coverage.ps1"
awk '/^<!--TOOL:verify-mapping:BEGIN-->/{f=1;next} /^<!--TOOL:verify-mapping:END-->/{f=0} f' "$R" > "$EV/extracted/verify-mapping.ps1"
awk '/^<!--TOOL:mixed-namespace-oracle:BEGIN-->/{f=1;next} /^<!--TOOL:mixed-namespace-oracle:END-->/{f=0} f' "$R" > "$EV/extracted/mixed-namespace-oracle.ps1"
awk '/^<!--TOOL:case-refs:BEGIN-->/{f=1;next} /^<!--TOOL:case-refs:END-->/{f=0} f' "$R" > "$EV/extracted/case-refs.ps1"
awk '/^<!--TOOL:probes-round5:BEGIN-->/{f=1;next} /^<!--TOOL:probes-round5:END-->/{f=0} f' "$R" > "$EV/extracted/probes-round5.ps1"
awk '/^FIELD-RULES:BEGIN$/{f=1;next} /^FIELD-RULES:END$/{f=0} f' "$R" | sed '/^```/d' > "$EV/extracted/field-rules-from-report.tsv"
```

**实测证据表（LOOP-R7 = `C:/Users/sncdc/AppData/Local/Temp/n3-extract-loop-r7`，全新空 TEMP；下列步骤按上块命令原样执行，SHA256 为交付时在案实测；确定性产物与 §11 对应日志逐字节相等，随耗时变化的日志以实测为准并保持判定行一致）**：

**① 工具重建**：上块 10 条 awk + 1 条 FIELD-RULES 提取全部成功，SHA256：

| 提取件 | SHA256（= §11 对应内嵌源） |
|---|---|
| `extracted/FullCensus.csproj` | `8f946bc6c411cc89c659c88eeb89ddeaeb52cb54b0d480e58e09ff418e29168d` |
| `extracted/Program.cs` | `fd49db41b5dbb6602b7ce1a2e9a52b679f78b700dc1d93606d8f12f8f530b4d0` |
| `extracted/CensusInstances.csproj` | `9bbd92e2df91ce7a14cf506306dc186ff2a4d3613173d66c65adcddeaf0dff6a`（§11.4b 头注同值） |
| `extracted/CensusInstances-Program.cs` | `10c03a4e8c172ed3122587995d4f6d2ed50dba4ea03c0b6d202223c479097ec6`（§11.4b 头注同值） |
| `extracted/verify-coverage.ps1` | `f932794ee00db2c8cea4a67b15e4ec640bdc933f890a0240c3091a59e22d3997`（§11.5 头注同值） |
| `extracted/verify-mapping.ps1` | `b094d2a8540877d199ee1dc99753cf6c6675bc1534c60c533ca1385cf9f8d51f`（§11.6 **v5** 头注同值——v5 内嵌块经本提取环节 round-trip 复现） |
| `extracted/mixed-namespace-oracle.ps1` | `e54af103aaf6768128412dc4f8b40c50c1c1e301db80ff981ea4824571f6fd09`（§11.6 R6-1 oracle 头注同值） |
| `extracted/case-refs.ps1` | `39aeccc450fcb3ddd63cfa4ed56b3527071b88934e293ce42e616cf499995492`（§11.7 头注同值） |
| `extracted/probes-round5.ps1` | `fab9222a5ebb103fc2ac04ed9a1dc0655bd87a5647bce2c103b15db172abd256`（§11.8 头注同值） |
| `extracted/field-rules-from-report.tsv` | `24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31`（与 R7 TEMP 原始 `rules-v5.tsv` **逐字节相等**：UTF-8 BOM + LF 无 CR；**规则内容本 round 未动**，即 §11.6 两次 check 的 `-RulesFile` 输入） |

**② census 复算（FullCensus + CensusInstances 双工具）**：两 csproj 均为 SDK 默认 `*.cs` 通配，而提取目录同时含两个带 `Main` 的 Program（`Program.cs` 与 `CensusInstances-Program.cs`），任一程序若在该共享目录构建都会 CS0017 双 Main 报错（第 6 轮实测在案，§11.10 LOOP-R6 版注记）——故各从**只含自身双件的专职目录**构建（`fullcensus-run/`、`censusinstances-run/`，均为①提取件的字节拷贝，SHA 同①表；第 7 轮直接按此协议执行，一次通过）：`D:/Unity/dotnet/dotnet.exe run --project fullcensus-run/FullCensus.csproj -- <repo> census-out` → EXIT=0，**13 件产物与 §11.4/R5 驱动产物逐字节相等**（重跑前后 sha256 全等：physical `db9f3db6…`/scoring `d84b7e1f…`/round `e646e083…`/cheat `391966d9…`/shuffle `5c0ada44…`/ai `ff1b9f56…`/census-summary `7b8909f2…`），6 × `<fixture>-full-paths.txt` 合计 1516 行（296/419/200/204/199/198 复现）；`census-run.log` SHA256 `72c78ea2284e26f12d6828f693798b0f8b8b2d76c5fb285d7ff28ed8d7b86c7d`（与 §11.4 表逐字节相等，尾行 `FULL-CENSUS-DONE failures=0`）。`D:/Unity/dotnet/dotnet.exe run --project censusinstances-run/CensusInstances.csproj -- <repo> census-out` → EXIT=0，`instances.tsv` 655,145 行 SHA256 `049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12`（§11.4b pin 同值）、`inst-run.log` `f075dd6da4ecb5ef3e3d161f462fc34af58dc71ca8ef5ddb19b9118f6810914e`（§11.4b 同值，尾行 `INSTANCE-MANIFEST-DONE failures=0`）。scoring-v1 case 总数复现 = fixture `generationResult.totalSamples = 10238`（7 组：win 48 + noWin 4 + fu 9 + dora 6 + settlement 24 + score 147 + random 10000；census 对全部 case 逐节点遍历，scoring leafInstances=476153）。

**③ coverage/mapping 两层复算**（均对**本报告**执行；六组 EMIT 计数 28/27/22/21/19/31 与 §3.0 一致；v5 映射校验含逐实例单元真值输出；**调用注记（如实）**：本 round 驱动脚本首次运行曾对 emit/verify 漏传 `-CensusDir`（工具内 `Join-Path $CensusDir …` 收到 null → exit 1）——系调用参数笔误而非工具缺陷，**工具一字未改**，补参重跑即 exit 0 且与 §11.5 命令日志逐字节相等；失败尝试的日志被成功运行覆写、未单独存档，特此记注）：

| 步骤 | native exit | 实测输出 | 日志 SHA256 |
|---|---|---|---|
| emit（-CensusDir LOOP-R7/census-out -OutDir LOOP-R7/coverage-out） | 0 | 6 × prefix2 产物与 §11.5 版相等 | `coverage-emit.log` `06efd6a773ef9a14a4d7b1c6869441ed8d18f58b370852b2df2796013eee8e51`（与 §11.5 命令日志逐字节相等） |
| verify（-CensusDir LOOP-R7/census-out -ReportPath 本报告） | 0 | `COVERAGE-VERIFY-OK unmapped-total=0`（六组全 unmapped=0 mismatched=0 extra=0） | `coverage-verify.log` `e0dd66910112c9cd4c15eb3a18404d6114cb0cd296954e28deff5f5c2a4fce31`（与 §11.5 命令日志逐字节相等） |
| check `-ReportPath`（-CensusDir LOOP-R7/census-out，内含 `instances.tsv`） | 0 | `RULES-CHECK-OK-V5 total-paths=1516 instances=655145 units=655145 rules=542 leaks=0 mismatches=0 anchor-errors=0 dup=0 scalarAsContainer=0 scope-errors=0 una-errors=0` + PATH-UNITS ×1516（每路径真实单元数，非平凡 units>1 行 793 条）+ DIST class ×9 / gap ×12（合计 gap-paths=101）+ `DIST-TOTAL unique-paths=1516 gap-paths=101` + CASE-DIST class ×9 / gap ×14（合计 gap-units=592）+ CASE-MIXED ×27（每行显式 `fx=`，全部 cheat-v1）+ `CASE-TOTAL units=655145 gap-units=592 mixed-paths=27` | `check-report.log` `fcae384805bf6a44b3e3e48d013f398ad829967ec0051d84e835569aea2b4735`（= §11.6 v5 `-RulesFile` 模式日志逐字节） |
| check `-RulesFile`（① 的报告块提取 TSV） | 0 | 同上 | `check-tsv.log` 同 SHA `fcae3848…`（两次独立调用输出逐字节一致，亦 = `vm5/v5-check.log`） |
| **mixed-namespace oracle（R6-1 控制行）**（-RulesFile ① 提取 TSV -Manifest LOOP-R7/census-out/instances.tsv -CheckLog check-report.log） | 0 | `MIXED-NS-ORACLE-OK recomputed-mixed=27 reported=27 fixtures=cheat-v1 mismatches=0`（7 × WITNESS-CLEAN + 27 行 ORACLE-MIXED 与 checker 日志逐一相等） | `mixed-namespace-oracle.log` `9eaebd992adc42c55d956b780c19815e46849b7f46b38abd1236a45e4eb18f42`（与 §11.6 oracle 日志逐字节相等） |
| negative ×16（strip-anchors/drop-rule/anchorless-rule/nonexistent-anchors/duplicate-rule/scalar-container/scope-extra-case/scope-drop-case/scope-unknown-case/scope-dup-case/scope-cross-section/scope-where-broken/where-value-broken/una-drop/una-nonnull/**una-unscoped-nonnull**） | 0（全部 NEGATIVE-DETECTED） | 16 份日志与 §11.6 v5 负例矩阵表**逐字节相等**（`cmp` 全零；含第 7 轮新增的 48 × UNA-NOT-NULL 面） | `neg/<kind>.log` ×16，SHA 见 §11.6 表（同值） |

**④ case 分桶复算**：`extracted/case-refs.ps1 -Repo … -Evidence LOOP-R7` → EXIT=0：`quoted tileMarkHandler=7 sleeveBloomHandler=11 beamSwapHandler=6 total=24`、`goldenInputs tileMark=0/20 sleeveBloom=7/32 beamSwap=0/23`、`buckets A=16 B=2 C=6 D=50 E=1`、`rows=75`、`CASE-REFS-OK`；`case-refs.tsv` SHA256 `af0aedd8b62fa05be5f95ddc04f3390321ec1711d41621a6737f367d3b0b72bb`、`case-refs-summary.txt` `e8303afc3ae73aa7d051c0d37a95ccc4dea7a27962f8e51d419998427ea2dce4`、运行日志 `case-refs-run.log` `a5e774b4d8bdc90c44df7164e1e9cdeb19350633aa7f98ea138241b99676c84c`——三件均与 §11.7 版逐字节相等。

**⑤ 探针复算（含逐探针期望校验）**：`extracted/probes-round5.ps1 -Repo … -Evidence LOOP-R7/driver`（**caller 同款协议：驱动专用全新子目录**，驱动在其中自建 `extracted/`、`census-out/`、`fake-repo/`）→ EXIT=0，尾行 `ROUND5-PROBES-COMPLETE (all A/B expectations matched)`；16 探针 nativeExit/Total/Failed/Skipped/NotRun 期望全配（5 个故意失败探针实测 native exit=1，驱动捕获非零并按期望判行）；**16 个变异 fixture SHA 与 §11.8 表逐一相等**（如 ai-clock-metadata `62838FE1…`）；git `7ea62e6` 存档提取锁 SHA 全中（`1CE17DAC…`/`D932A03F…`）；全新 census 复现（physical 296/scoring 419/round 200/cheat 204/shuffle 199/ai 198）+ 反例行 `A3 OVERLAPPING_PATHS=935 SCALAR_AS_CONTAINER=1 (expected 935 / 1)`。运行日志 `probes-round5-run.log` SHA256 `79b8cbfd2196adebb3bdbcd5d67d4c1da656cae5f1e118a87924f52204cdd7e5`、`probe-summary.txt`（`LOOP-R7/driver/` 下平铺，16 行）SHA256 `27aa905e5e3609ba006da5a7deb3fc526ea7787c80efeb72ae62ba1b331c32c1`，逐探针日志 16 份在 `LOOP-R7/driver/<name>.log`（`Time:` 耗时随运行变化致 logSha 与 §11.8 版不同，判定行/exit/变异 fixture SHA 一致）。**环境注记（第 6 轮历史，工具未改）**：第 6 轮首次尝试曾将 `-Evidence` 指向共享提取目录根，驱动自抽取 FullCensus 双件至同目录时与已抽取的 `CensusInstances-Program.cs` 发生 CS0017 双 Main 冲突（PS 5.1 `$ErrorActionPreference='Stop'` 将首行原生 stderr 升级为 terminating NativeCommandError，exit 1）——系目录布局环境问题而非驱动缺陷，**驱动一字未改**，改按 caller 协议以全新子目录重跑即 COMPLETE（第 6 轮失败日志 `3e82e422…` 在 R6 TEMP 在案；第 7 轮直接按全新子目录协议一次通过）。

**⑥ 基线重放**：真实工作树 `D:/majiang-wt/n3-golden-field-audit` 下 `D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo` → **EXIT=0**、`Total: 16, Errors: 0, Failed: 0`（LOOP-R7 `baseline-cheatgolden.log` SHA256 `107236567307eab526df8a1ce4acd0645912a0676c0cf50464426a6ce5a3ce16`）。

**闭环结论**：空 TEMP 从本报告代码块重建 **10 工具 + 报告内嵌 FIELD-RULES 规则块**，复算出与本审计 §11 各节**逐字节相等**的 census（1516 路径 / 655,145 实例 / scoring 10238 case 全量遍历）、两层 coverage（`COVERAGE-VERIFY-OK` + v5 `RULES-CHECK-OK-V5` instances=units=655145 + fx 限定 mixed-paths=27 + **16 负例全检出 + 独立 oracle 27/27 相符**）、case 分桶（75 行 = A16+B2+C6+D50+E1）与探针结论（16 探针期望全配、5 个故意失败 exit 1 实证）；R2-2 验收四要素——空 TEMP 重建复现 1516 路径/10238 case、handler 命令 exit 0、故意失败非零、证据清单与实际日志 1:1——全部满足。**第 7 轮新增闭环面**：v5 内嵌块经提取环节 round-trip（SHA `b094d2a8…` 复现）后，其 `-ReportPath` 与 `-RulesFile` 两种模式对同一报告/同一 TSV 产生逐字节一致输出（`fcae3848…` = §11.6 实测），R6-1 的 (fixture,path) 键与 R6-2 的全分支 UNA 守卫均在自提取闭环内机器可复算。

### 11.11 本报告自身哈希

- 完整性校验一律以 `sha256sum "doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md"` 实测 SHA256 为准，**提交后在最终交付说明中给出实测值**（正文不预填，杜绝手填哈希）。

### 11.12 caller 独立证据对照（E 目录，未复制未改）

第 2 轮 caller 审查证据 E = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r2-review-931bc685c4b34a738dcfdf4863b6cf99`。本审计仅只读引用其结论作交叉验证：audit-probes.ps1 `75203A8C…`/audit-probes.log `82634716…`、mutation-probes.ps1 `F6C73E7E…`/mutation-probes.log `ABA4FAE7…`、source-checks.log `A49162FD…`、sha256.json `3222812C…`、负例族（coverage-with-no-semantic-mapping / tilemark-claimed-full-fields-unbound / ledger-claimed-full-output-unbound / positive-control-sb-activate-already-used-bound / sb-choose-first-unbound）与 reported-repro.log `8BD48159…`（全量 SHA 见 E/sha256.json）。**关键交叉点**：caller 正控制 positive-control-sb-activate-already-used-bound（#23 绑定实证）与本方探针 P1 同结论——#23 = A 桶；caller 三个 claim-buster 负例指出的"tileMarkState/ledger 全差分"过度声明，本报告已按 UNBOUND_HC 实测改写（§3.4）。

第 3 轮 caller 审查证据 E = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r3-review-d9701474e23244b880077239233f103e`（只读引用；全量 SHA 见其 sha256.json）。**逐字节相同的独立复现**：本方 R4 反例复现日志与 caller E 目录同名产物 SHA256 逐一相等——nonexistent-anchors.log = mapping-check.log = `AFAFEFAAEADCCDCA2499016BE57CF787D30BCDE7B8620BF8761375366A0052B1`、overlap-duplicate.log = `83B70EBC45E209450E6C4A0DEEBA4B0AD1968D6BF686D7313108D8F243D92D4E`、overlap-and-kind-details.log = `E309DDFAD55DD7BF00FEF12B56A6502CE719C2D4E0E8CC83DEF02B84E4E0AF60`（两个独立会话对同一第 3 轮校验器产生逐字节一致的反例输出，R3-1 三缺陷为客观事实）；caller 对本方第 3 轮报告的内嵌规则块提取件 rules.tsv `D932A03FF9A6B958576ADF51A87DEEE684C88776F726F4546257D4EDE51EE73D` 与本方 §11.10（第 3 轮版）提取件同值。caller 侧其余关键件：review-new-evidence.ps1 `FBFB921F…`/review-new-evidence.log `F3BCD75E…`、source-checks.log `047F4779…`、worktree.log `95882FAF…`、probes-run.log `80F2DAFB…`、六份 test-*.log（test-CheatGoldenTests `F29D9E99…` 等）、census-out/census-summary.txt `7B8909F2…`（与本方 §11.4 同值）、变异 fixture ai-clock-metadata.json `62838FE1…`（与本方 §11.8 Q7 同值——同一变异位置约定）、build-domain/build-ai log `B5C092F1…`/`EBD60389…`。**关键交叉点**：caller R3 三组 MAJOR（R3-1/R3-2/R3-3）的反例与本方 §3.0.1 反例表、§3 重分类、§13 算术修正一一对应，本报告第 4 轮已逐项修复（§15 映射表）。本报告所有数字以 §11 本方独占 TEMP 复算件为准，两个 E 目录均为独立第二来源，三方无矛盾。

第 4 轮 caller 审查证据 E = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r4-review-d4c6b79dc4ab4cfe8df00fa84a658525`（只读引用；全量 SHA 见其 sha256.json）。**关键交叉点**：R4-1 指定负例——caller 对本方第 4 轮 v2 校验器执行 `availableWallCount` scope 追加 `|sb-activate-disabled`，实测仍 `RULES-CHECK-OK-V2 scope-errors=0` exit 0（independent-probes.ps1 SHA256 `A040D239…`、log `2B37B13C…`、sha256.json `F7CE2A86…`）——该反例成为 v3 六种 SCOPE 错误的直接设计输入（本方 v3 复现与堵死见 §3.0.1 反例复现表与 §11.6 负例表 scope-extra-case 行，`FAIL SCOPE-ID-ABSENT … sb-activate-disabled` exit 1）。本方 R5 探针驱动重跑 16 探针所得变异 fixture SHA 与 caller R4 目录在案变异 fixture 逐一相符（P1 `7B605C10…`/P2 `99DA2673…`/P16 `62838FE1…` 等，§11.8 表），两轮独立运行对同一变异代码产生逐字节一致的 fixture，变异复现性由此第三方可验。

第 5 轮 caller 审查证据 E = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r5-review-a25083a7b9284ede969b89e4160c97e1`（只读引用，未复制未改；全量 SHA 见其 sha256.json `F6E785BC…`）。**关键交叉点**：(1) caller `independent-semantic-probes.ps1`（SHA `19464124…`）与 log（SHA `5c70c291…`）以独立脚本实测四条机器行——`M5 wallFacts=0 handDiscardFacts=4`（m5 fixture 仅含 hand/discard factsAfter 各带 index/seat、wall 零冻结样本；本报告据此**更正第 5 轮"m5 wall seat→999 已实测不设防"的不实表述**且不虚构样本，§3.4/§10 表 23）、`STATUS-UNBOUND instances=48 table-case-contribution=1` 与 `COORD-UNBOUND indexInstances=17`（R5-1 混合分母与 factsAfter 17 实例的独立复算，与本方 CensusInstances 逐实例清单的 48/11/17/6 拆分一致，§11.4b/§3.4）、`INDEX-UNIT … container=hand index=NULL tableClass=INTERNAL`（v3 表把未读 null 坐标判 INTERNAL 的直接反例——第 6 轮改判 UNA 不适用，§1.4）+ `M1-HAND-INDEX-MUTANT nativeExit=0`（16/0/0，null index 无读点实证，与本方 P9 生效半支结论同向）；(2) caller 以本方第 5 轮报告内嵌块重跑 probes-round5（其提取件 SHA `fab9222a…` 与本方 §11.8 锁值同）并归档本方第 3 轮校验器（`verify-mapping-r3.ps1` = `1CE17DAC…`，与本方 §15.3 R4-3(a) 行 git 存档锁同值——两轮独立会话对同一工具产物逐字节一致）。R5 两组 MAJOR 的反例与本方第 6 轮更正一一对应（§15.4 逐项映射）；caller 同时更正其第 4 轮"m5 含 wall 事实"的审查假设，本报告相应修正且未虚构任何样本/断言（§15.4 R5-2 行）。

第 6 轮 caller 审查证据 E' = `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r6-review-f5ab86b1c5f94aadb4b036367036be22`（只读引用，未复制未改；全量 SHA 见其 sha256.json `ff9ac0fb…`）。**关键交叉点**：(1) R6-1——caller `mixed-path-oracle.ps1`（SHA `2206f208…`）实测 `MIXED reported=34 fixture-qualified=27 false-cross-fixture=7` 并以 `$.decisionClock` 在 round-v1（SEM_MAP）与 ai-v1（CONTAINER）各自 fixture 内均不混合的Witness 断言打出 `CROSS-FIXTURE-COLLISION-REPRODUCED`（log `947adb64…`）；本方第 7 轮 v5 改 (fixture,path) 复合键后复算同位真值 27/27 相符、7/7 见证路径任一 fixture 内不混合（§11.6 mixed-namespace-oracle，独立实现不依赖 caller 脚本）；(2) R6-2——caller `una-unscoped-nonnull.tsv`（SHA `3671b3dab66b4c6a25d02b43f598f4289cbe5d450378a82277080f9eb44823ba`，`una-unscoped-negative.ps1` `bb61fa2a…` 生成）对 v4 实测 exit 0 假绿：`owners=UNA:CALLER-NEGATIVE:48`、`CASE-DIST class=UNA units=65`、`gap-units=544`（log `e33945a3…`、check.log `809ce514…`）；本方以**逐字节相等**的 TSV（SHA 同值）在 v4 上复现同象（log `e1e6d837…`）、在 v5 上被逐实例拒绝——恰 48 行 `UNA-NOT-NULL … kind=string`、`RULES-CHECK-FAILED errors=48` exit 1（log `5279c645…`，§11.6/§15.5）。caller 日志由 PowerShell 重定向保存（编码不同），不要求与本方原日志 raw SHA 相同——以内容逐行对读为准。

---
## 12. 边界声明

1. 本报告**只读**了仓库与兄弟任务无关区域；未读取 N1a/N2 兄弟在途未提交内容，未假设其已修。
2. 未修改：生产代码、测试代码、fixtures、catalog、manifest、固定头已接受 G1 盘点报告、用户未提交文件（CLAUDE.md/PROMPTS.md/任务书/协议/Unity 环境/Resources/Burst）。
3. 全部写入限于：本报告一个文档（expectedFiles 指定路径）+ 独占 TEMP 七处（第 1 轮 `n3-field-audit-01edf75dbc3b`、第 2 轮 `n3-rework-round2`（含 fake-repo 变异副本）、第 3 轮 `n3-rework-round3` 与自抽取闭环 `n3-extract-loop`、第 4 轮 `n3-rework-round4`（含 fake-repo 变异副本，第 4 轮版自抽取闭环 TEMP 在该轮内新建）、第 5 轮 `n3-rework-round5`（含 `probes-run2`/`selftest-run2`/`vm-run` 子目录与 fake-repo 变异副本）与自抽取闭环 `n3-extract-loop-r5`、第 7 轮 `n3-rework-round7`（`vm5/` 校验套件与负例/换序/oracle 证据）与自抽取闭环 `n3-extract-loop-r7`，均不触仓库）。另有 caller 六轮审查 E 目录（§11.12）只读未复制未改。
4. `V5-REVIEW-REWORK-PROTOCOL.md` 全程只读，未复制提交。
5. **审计报告组件完成 ≠ N-3 关闭 ≠ G1 批准**：N-3/G1 的关闭由 caller 依据本报告与补齐包（§9）独立复核后裁决。G1 门的全部口径由任务书既有规定与 caller 汇总复核决定——本报告与 §9 不缩小也不扩大该口径：所有已导出可比较未绑定内容全部保留为缺口（§3.0.1 GAP 101 条 + §9 覆盖清单），不增设任务书之外的批准前置条件。
6. 本报告不带任何 push/PR/CI 触发/远端操作。

---
## 13. 协议格式缺陷清单（第 7 轮一次性全量版；同类合并 + 统一负例矩阵；R3-3 算术修正 + R4-2 逐单元语义更正 + R5-1 单元真值/R5-2 真实读点更正 + R6-1 fx 命名空间/R6-2 UNA 全分支守卫更正）

> 本审计冻结在"只写一个新报告"边界内，**不实施任何修复**；每项"修复要求"指向 §9 补齐包 ID，由 caller 独立下发。严重级别口径：MAJOR = 现有断言防不住错误值/错误行为或阻断 G1 门口径核实；MINOR = 文档/索引失真，无行为风险。无 BLOCKER（未发现生产行为错误或守恒违例）。**本清单已覆盖当前全部已知可复现缺陷，无分批保留**；后续仅当出现新信息才追加，并须说明此前为何不可发现。**第 4 轮修正（R3-3）**：DEF-2 的行为级对照算术更正（A16+B2+C6=**24**，E1 为存在级不计入行为绑定，合计 25/75）、B1=14（m1b 移出）、B5=10（snapshot 两布尔移入）、B3 归属更正（sleeveBloom 六叶，非 tileMark/beamSwap）、新增 B7/B8/B9/E3 缺口包；DEF-5 的规则表分母更新 417→497、GAP 87→101。**第 6 轮修正（R5-1/R5-2）**：DEF-2/DEF-5 单元分母由 286 更正为 **592**（全族逐实例，CensusInstances）；DEF-2 混合路径单元 54→**39**（B11 26→11：6 实例已读 + 17 UNA 不适用）；P9/wall 面表述按实际生效变异与零冻结样本更正（NOT_EXERCISED），m5 wall=0 为 caller 与本方双侧实证。**第 7 轮修正（R6-1/R6-2，单元真值不变，仅呈现口径与校验面硬化）**：混合路径按 (fixture,path) 键重计 34→**27**（7 条假混合系跨 fixture 误并，§15.5）；UNA 非空守卫从 scoped 分支扩展为全分支共享不变量（unclaimed 分支虚报 UNA 的 48 实例复现现被逐实例拒绝）；负例矩阵 15→**17 项**（新增 `mixed-namespace` oracle 控制行与 `una-unscoped-nonnull`）。

### DEF-1 [MAJOR] round-v1 已导出字段未绑定/硬编码（GAP:N3-B4 round 部分：20 条 census 路径 / 16 个字段级条目）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`——`:110-113`（phaseBefore/versionBefore 硬编码 Discard）、`:117`（outcome 硬编码）、`:129-131`（phaseAfter/resultOutcome 硬编码、totalDeltas 被 resultDeltas 替代）、`:134`（事件仅 Contains TsumoWin）；`:151-233`（ChankanRonReplaysExactly：eventTypes 仅 :225-226 两个 DoesNotContain、hasKanDeclared/hasRinshanDraw/legalRonSeat1/meldTypeAfterKan/phaseAfterKan/phaseAfterRon/resultOutcome/input.meldTiles 未绑定）。导出侧事实：`V5RoundGoldenExportTest.kt:275-277`。
- **原样复现（-File/-class 形式，native exit）**：
  ```
  cd D:/majiang-wt/n3-golden-field-audit
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.RoundReplayGoldenTests -noColor -noLogo
  → EXIT=0（Total: 6, Failed: 0）
  ```
  路径级缺口清单位移证明（层 2 规则表）：§11.6 v4 check 命令 → `RULES round-v1 paths=200 rules=72 leaked=0 scalarAsContainer=0` + `DIST gap=GAP:N3-B4 paths=26`（round 20 + tileMarkState 6，§11.6 正检实测；逐实例口径 `CASE-DIST gap=GAP:N3-B4 units=35`）。
- **实际 vs 期望**：实际 = 20 条路径（16 条目）UNBOUND_HC——C# 断言存在但读硬编码常量或干脆不读导出值；期望 = N3-A1/A2 的逐值绑定（事件序列按 fixture 投影规则精确对账，两布尔经名称映射绑定到 C# Contains 结果——ScoreSettled 合法映射保留，不强造 Domain 同名枚举）。
- **证据**：fixture `tests/v5-golden/round-v1.json` SHA256 `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7`；运行日志 SHA256 `B83631131381FDE2266E8C20733EA67521B1A9AD03F086B27487F07E06694D8E`；flags 变异探针（第 2 轮）6/6 PASS；§3.3 两表；FIELD-RULES round-v1 72 条规则。
- **修复要求**：§9 N3-A1 + N3-A2（= N3-B4 的 round 部分，仅测试文件）。
- **统一负例矩阵（变异验证仅在 caller 隔离环境做）**：(a) 双布尔 false→true → 应 FAIL；(b) eventTypes 插入 KanDeclared / 删去 RoundEnded / 错序 → 应 FAIL；(c) settlement.totalDeltas 篡改 → 应 FAIL；(d) meldTypeAfterKan "PON"→"CHOW"、legalRonSeat1 true→false → 应 FAIL；(e) phaseAfterKan/phaseAfterRon/resultOutcome 逐值错值 → 应 FAIL。
- **验收断言**：(a) 两测试对事件类型**精确序列**断言（投影注释在案）；(b) §3.3 两表全部 UNBOUND_HC 行变为逐值绑定或显式映射断言；(c) 负例矩阵 (a)-(e) 全部 FAIL；(d) 6 Facts 保持 PASS。

### DEF-2 [MAJOR] cheat-v1 case 级 53（D50+B2+E1）无/弱黄金行为对照 + 路径级 40（B1/B3/B4cheat/B5/B7/B8）

- **准确位置**：`dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs`——行为级黄金对照 **24/75（A16+B2+C6=24）**；**E1 存在级 1**（#36 :480 注释级——纯 C# 存在断言、golden 条目本身未绑定，**不计入行为绑定**；两种口径合计 = 25/75 有某种形式对照）；其余 **50**（D 桶，§3.4 表标 D 者）仅经 `Handler_Sections_CoverEveryRejectionReason`（:339-365）做 reason 映射完备性；B2（#6 同族码、#67 仅 outcome :539-544）；**路径级 40 条 + 混合路径逐单元 39 个**（第 6 轮 R5-1 全族逐实例重划；第 5 轮"54"中 B11 26 系混合分母错算，更正为 11）：路径级 40 = tileMovement 14（GAP:N3-B1，m1b cross 字段已移出为 INTERNAL）+ goldenInputs UNBOUND 六叶 6 路径（GAP:N3-B3：`availableWallCount/offerId/rinshanPointer/wallEndExclusive/wallPointer/windowId`——**第 4 轮更正：缺口属 sleeveBloom，tileMark/beamSwap 的 goldenInputs 均已绑定**）+ tileMarkState 6 字段（GAP:N3-B4 cheat 部分）+ ledger 10（GAP:N3-B5）+ beamSwap requestException 1（GAP:N3-B7）+ 三 handler note 叶 3（GAP:N3-B8）；**混合路径逐单元**（路径被绑定类行裁决、单元级悬空，DIST 不显、CASE-DIST 显）= goldenInputs B10 28 单元 + factsAfter B11 **11 单元**（= deadWall/replacement index 4 实例 + 非 m5 hand/discard seat 7 实例；两路径 34 实例中 6 已读、17 为 null 无读点 UNA 不适用，§3.4/§9 N3-B11）。**goldenInputs 逐单元真值（第 5 轮 R4-2 更正，撤回第 4 轮"其余 22 条 IN_BIND 已绑 :718-826"）**：sleeveBloom goldenInputs 合计 79 单元 = 绑定类 30（INTERNAL 14 + SEM_MAP 12 + OUT_DIFF 4）+ UNBOUND 49（B3 21 + B10 28）+ **IN_BIND 0**——第 4 轮 TSV 的 `goldenInputs.#` 21 行 IN_BIND"已绑 :718-826"系容器捕获误作输入绑定，第 5 轮 FIELD-RULES v3 已按 26 叶 × case 逐单元重列（§3.4 表）。
- **原样复现（全部 -File 形式，native exit；工具源均内嵌于本报告，提取见 §11.10）**：
  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/verify-mapping-v5.ps1 -Mode check -ReportPath "D:/majiang-wt/n3-golden-field-audit/doc/v5-重构/V5-G1-N3-GOLDEN-FIELD-AUDIT.md" -CensusDir C:/Users/sncdc/AppData/Local/Temp/n3-rework-round7/vm5/census -Repo D:/majiang-wt/n3-golden-field-audit
  → EXIT=0（RULES cheat-v1 paths=204 rules=217 leaked=0 scalarAsContainer=0 + DIST-TOTAL unique-paths=1516 gap-paths=101 + CASE-TOTAL units=655145 gap-units=592 mixed-paths=27 + RULES-CHECK-OK-V5 total-paths=1516 instances=655145 units=655145 rules=542 … una-errors=0；-ReportPath 模式复算在 §11.10 LOOP-R7 实测）
  powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3/case-refs.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round3
  → EXIT=0（quoted tileMarkHandler=7 sleeveBloomHandler=11 beamSwapHandler=6 total=24；goldenInputs tileMark=0/20 sleeveBloom=7/32 beamSwap=0/23；buckets A=16 B=2 C=6 D=50 E=1；rows=75——case-refs 口径为"case 引用侧"，与字段级 goldenInputs 绑定（B3/B10）分属两层）
  powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/probes-round5.ps1 -Repo D:/majiang-wt/n3-golden-field-audit -Evidence C:/Users/sncdc/AppData/Local/Temp/n3-rework-round5/probes-run2
  → EXIT=0（P3 requestException / P4 unbound-reason / P5 six-goldeninputs 全 16/16 PASS；P9 m5 分区坐标 16/16 PASS；正控制 = 第 3 轮 P1 + 第 5 轮 P13/P14/P15 各 1 FAIL）
  cd D:/majiang-wt/n3-golden-field-audit
  D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class Mahjong.Domain.Tests.CheatGoldenTests -noColor -noLogo
  → EXIT=0（Total: 16, Failed: 0）
  ```
- **实际 vs 期望**：实际 = D50 的 outcome/reason 仅被映射表消费、B2 部分绑定、E1 存在级、40 条路径 + 39 个混合路径单元未消费（P3/P4/P5/P9/P10-P12 变异全 PASS；P9 生效面 = hand/discard index 2 实例，wall 半支零命中，§10 表 23）；期望 = §9 N3-B3(a)-(d) + N3-B1/B3/B10/B11/B4(cheat 部分)/B5/B7/B8 全覆盖——**所有已导出可比较未绑定内容保留缺口直至绑定**；D-β 4 例以等价类断言 + 映射注释 + owner 批准落地（真正不可比处显式说明语义与批准责任，不沉默移出分母）。
- **证据**：fixture `tests/v5-golden/cheat-v1.json` SHA256 `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；运行日志 SHA256 `4783C3761FF0F2360B626A1B03B317DC43ACBD7BABAFCD8F3CDFE95B7D721AF4`；R5 探针日志/变异 fixture SHA（§11.8 表 P3-P12）；第 3 轮 4 探针（§11.8 存档）；case-refs.tsv/summary SHA（§11.7）；§3.4 逐条 75 行表 + goldenInputs 26 叶逐单元表；snapshotRoundTrip 读点 `:261-262`。
- **修复要求**：§9 N3-B3（case 级 + sleeveBloom UNBOUND 面）+ N3-B1/B10/B11/B4(cheat 部分)/B5/B7/B8（路径级/单元级），仅测试文件。
- **统一负例矩阵**：(a) 6 个 Allowed 主路径 case 任一副作用/事件形状错值 → 应 FAIL；(b) sb-choose-first outcome→Rejected + reason→WRONG_PHASE → 应 FAIL；(c) 任一 stale case 的 stale 判定翻转 → 应 FAIL；(d) #70 requestException 缺失/错型 → 应 FAIL；(e) D-β #19/#5/#18/#20 的映射断言值错置 → 应 FAIL；(f) tileMarkState 6 字段/ledger 四子键 outcome·exception/snapshot 两布尔篡改 → 修复后应 FAIL（现 PASS = 探针 P3/P4/Q3/Q4 实证）。
- **验收断言**：(a) §3.4 表 D50+E1 全部有行为级断言或显式映射断言；(b) B2 升级后逐值绑定；(c) 负例矩阵 (a)-(f) 全部 FAIL；(d) 现有 16 Facts 保持 PASS；(e) **无门径收窄**：本缺陷的关闭标准 = §3.4 分桶清零（绑定或显式映射）+ 40 条 GAP 路径清零 + 混合路径 B10 28/B11 11 单元清零（以修复后 FIELD-RULES 重列 + CASE-DIST gap 归零为准），不以"至少 N 个 Allowed + 代表拒绝"替代。

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
- **修复要求**：§9 N3-B2（仅注释 + ShuffleGoldenTests.cs deadWallSegmentEqual 绑定，第 4 轮扩展）。
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
- **实际 vs 期望**：实际 = G-049 记 none，但 ai-v1 存在且 Replay/Submit/拒绝码/动作/decisionClock 绑定被真实 C# 消费（§3.6，第 4 轮探针 Q7 正控制实证 decisionClock 面设防）；期望 = 按 §3.6 消费边界重算 **partial**（非 full：后态差分缺位 + mappingBasis/mappingLimits/text 叶 5 条未绑定）。是否把 ai-v1 纳入 fixtures[]/manifest = §9 N3-C1 的 caller 裁决项（本审计不改 catalog）。
- **证据**：catalog/ai-v1 fixture SHA（§11.9）；run-ai.log SHA（§11.2）；§3.6；Q7（§11.8）。
- **修复要求**：§9 N3-C1（caller 裁决口径）。
- **验收断言**：catalog/manifest 与"仓库现存 fixture 清单"一致（含或显式排除 ai-v1 的书面理由）。

### DEF-5 [MAJOR] 已导出字段未绑定族（scoring/shuffle/physical/ai/tileMovement/goldenInputs/requestException/note/text/factsAfter 坐标）与"fixture 内部结构误判为生产行为差分"的结论性错误（第 6 轮双分母：GAP 101 条路径【定义投影】/ 592 单元【完整分母，全族逐实例】/ 14 包）

- **准确位置**：`ScoringGoldenTests.cs`（`CompareWinSample` :207-242 不读 `expected.status` 与 `input.extraHan`；`CompareNoWinSample` :247-250 不读 doraIndicators/uraDoraIndicators/honba/riichiSticks；`tags` 全文 0 次引用）；`ShuffleGoldenTests.cs`（`deadWallSegmentEqual` 全文 0 次引用；`ExportedWallsSatisfyPhysicalLaws` :160-175 为导出数组内部检查）；`CheatGoldenTests.cs:261-262`（snapshotRoundTrip 布尔信任级）+ tileMovement 14 条 + goldenInputs 六叶 + requestException 1 条 + note 3 条；`PhysicalGoldenTests.cs`（boundaries/boundarySamples/invalid/csharpExceptionMapping 24 条 + hashCrosswalk 双标签/suitOrder/scope.targetIds 5 条；`:56/:61/:663-666` 归一化 SHA 口径）；`AiReplayGoldenTests`（mappingBasis/mappingLimits 3 条 + v4Call/v4 text 叶 2 条）。
- **原样复现**：§11.8 探针命令（第 3 轮 P3/P4 + 第 5 轮 P1/P3/P4/P5/P9/P10-P12）；§11.6 v5 check 命令（`RULES-CHECK-OK-V5 total-paths=1516 instances=655145 units=655145 rules=542 leaks=0 mismatches=0 anchor-errors=0 dup=0 scalarAsContainer=0 scope-errors=0 una-errors=0` + `DIST-TOTAL unique-paths=1516 gap-paths=101` + `CASE-TOTAL units=655145 gap-units=592 mixed-paths=27`，§11.6 实测 log SHA `fcae3848…`）；`grep -c 'tags' dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` → `0`；`grep -c 'deadWallSegmentEqual' dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenTests.cs` → `0`。
- **实际 vs 期望**：实际 = GAP 101 条路径（§3.0.1 DIST 定义投影）/ **592 单元**（§3.0.1 CASE-DIST 完整分母，**全族逐实例展开**，14 包 = B1-B11 + E1-E3；第 5 轮 286 系混合分母作废）当前变异后 PASS——探针实证 16 条面（win status/noWin 四字段/deadWallSegmentEqual/sb-choose-first/tileMarkState 6 字段/ledger 8 叶/hashCrosswalk 双标签/requestException/UNBOUND_HC reason/六叶/相对回合对/相对实体对/m5 分区坐标【生效面 = hand/discard index 2 实例】/B10 三代表单元）；期望 = §9 N3-E1/E2/E3/B1/B2/B3/B10/B11/B4(cheat)/B5/B7/B8/B9 逐包绑定、tags/mappingLimits/text 类元数据显式排除声明、各 fixture 判级以 §3.0.1 FIELD-RULES **九类**为准（行为差分/输入绑定/语义映射/内部结构/未绑定/硬编码未绑定/元数据/容器/不适用 UNA），**禁止"fixture 内部结构"与"生产行为差分"混称**（m1b cross 字段与 shuffle 五组墙数组/identity 布尔在第 4 轮、goldenInputs 相对关系对与 factsAfter 分区坐标在第 5 轮、factsAfter 坐标按真实读点的 6 条 where 行拆分在第 6 轮均显式落 INTERNAL/分区谓词行段规则，§3.4/§3.5）。
- **证据**：探针日志/变异 fixture SHA（§11.8 表 + 存档）；census 路径行（`$.winSamples[*].expected.status string instances=48`、`$.winSamples[*].tags array instances=48 elements=54`、`$.cases[*].deadWallSegmentEqual boolean instances=32` 等，`*-full-paths.txt` 逐条在案）+ **逐实例清单 instances.tsv 655,145 行**（§11.4b，单元分母的独立枚举源）；FIELD-RULES 542 条规则（§3.0.1；单元分母 = 655,145 实例逐实例判级，非行数聚合）。
- **修复要求**：§9 N3-E1 + N3-E2 + N3-E3 + N3-B1/B2/B3/B10/B11/B4(cheat 部分)/B5/B7/B8/B9（全部仅测试/文档文件）；分类表述以本报告 §3 为准同步至任何下游盘点。
- **统一负例矩阵**：(a) win 首例 status win→noWin → 修复后应 FAIL；(b) deadWallSegmentEqual 首例 true→false → 修复后应 FAIL；(c) tags 数组注入标记 → 绑定或显式排除后行为确定（FAIL 或明确不适用声明）；(d) 正控制 totalPoints 错值 → FAIL（已实证）；(e) tileMarkState 6 字段与 ledger 8 叶篡改 → 修复后应 FAIL（现 PASS = P3/P4）；(f) hashCrosswalk 双标签/targetIds/requestException/note 篡改 → 修复后应 FAIL（现 PASS = Q1/Q3）。
- **验收断言**：(a) 探针面在修复后全部 FAIL；(b) tags/mappingLimits/text 的分母可见性成立（绑定或显式声明）；(c) §3.0.1 FIELD-RULES 与 §11.5/§11.6 双层校验 exit 0 维持（修复后重跑本报告工具链即得）。

### 语义映射 INFO 记录（非缺陷，禁止后续轮次误报为缺陷）

- **INFO-1**：`chankanRon.expected.hasKanDeclared/hasRinshanDraw` ↔ C# 终局事件列表 `Contains("KanDeclared")`/`Contains("RinshanDrawn")`——同语义、可绑定，唯一差异是名称映射 RinshanDraw→RinshanDrawn。补齐 = §9 N3-A2 的等值绑定断言；映射注释仅说明名称映射，**不构成豁免**（判级按 §1.4 保守原则维持 UNBOUND_HC 直至绑定落地）。
- **INFO-2**：mv-swap-drawn-with-wall-backfill 被置换墙牌落点地址（V4：指针下方死区旧墙槽 ↔ C#：摸牌让出的手牌槽）——净归属与 136 守恒等价、地址无规则消费，**不可机械 1:1**；以映射注释记录（N3-B2 附属）。批准责任：P1.4 owner。
- **INFO-3**：役种 41/42 口径（D-01）——DEEPSEEK 计划 `:588` 已预登记"冻结黄金前必须确认"，属规则裁决轨，非字段缺口；G-019 的 partial 定性维持。
- **INFO-4**：ledger `snapshotRoundTrip` 的 Entries 级对照**当前无对象**——fixture snapshot 子键仅含 `balancesEqual/entriesEqual` 两布尔，无 golden Entries 数组；"绑定 golden Entries"需先在 exporter 侧扩导出（catalog 语义变更）。在扩导出落地前，缺口的可执行形态 = N3-B5 的 10 条绑定（8 叶 outcome/exception + 两布尔逐值对照）+ Entries 对照注记留痕（G-041），批准责任：caller。

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
1. **不实施** §9 任何补齐项（任务边界：只修报告）；N3-A*/B*/E* 全部为建议，风险 = GAP 路径缺口继续存在直至 caller 下发（"87 条"系第 2 轮时点人工求和口径，第 4 轮机器分母 = 101 条，见 §3.0.1/§9）。
2. **catalog 口径未采纳**（不改 catalog）：Δ29 与 G-049 重算均为 N3-C1 的 caller 裁决项。
3. **D-β #19 在岗 deadline**：Domain 输入不可表示，等价类断言 + owner 批准为建议方案，未获批准前该例维持"缺口（需语义映射）"状态。
4. **tags/mappingLimits 元数据**：绑定或显式排除由 caller 裁决（N3-E1(d)/N3-B6），裁决前 tags 维持"未绑定"注记计入 §9 分母可见性说明。
5. **ledger Entries 级对照**：fixture 无 golden Entries 数组（INFO-4），需 exporter 扩导出方可绑定；此前维持 G-041 注记 + N3-B5 八叶绑定形态。
6. 第 1/2 轮 caller 认可方向（mv-swap 五层、ChooseDealerEnd、45/45、哈希口径表）未改动，仅按本轮口径补充引用。
---

## 15. 历轮审查意见 → 返修映射与不可发现性说明（R3 → 第 4 轮；R4 → 第 5 轮；R5 → 第 6 轮；R6 → 第 7 轮）

### 15.1 caller R3 审查意见逐项映射（REWORK_REQUIRED，commit 7ea62e6）

| caller 意见 | 内容摘要 | 第 4 轮修复落点 | 机器验收 |
|---|---|---|---|
| R3-1(a) | 校验器放行不存在的 file:line 锚点 | §11.6 verify-mapping **v2**：-Repo 真实消费，exporter `File.kt:L1-L2`/consumer `File.cs:L(-L2)` 逐一打开核对（存在性+行界） | 负例 nonexistent-anchors：717 × ANCHOR，exit 1（§11.6）；旧器缺陷复现 log 在案（§3.0.1 反例表） |
| R3-1(b) | 935 路径多重命中 + 重复规则放行 | §1.3/§3.0.1 改为**有序 first-match 口径**（"无重叠"表述撤回）；v2 增加 DUP 拒绝（同 fixture+pattern+scope） | 负例 duplicate-rule：1 × DUP，exit 1（§11.6）；OVERLAPPING_PATHS=935 复现在案 |
| R3-1(c) | 322 条 filename-only 导出锚点 | §3.0.1 规则表全部升级 `File.kt:L1-L2` 行段锚点，v2 -Repo 逐一核对 | 正检 anchor-errors=0（§11.6）；anchorless-rule 负例 4 × ANCHOR |
| R3-1(+) | 标量叶挂 CONTAINER 未检出 | v2 SCALAR-AS-CONTAINER 拒绝（census kind=number/string/bool vs CONTAINER 规则） | 负例 scalar-container：324 错误，exit 1（§11.6） |
| R3-2(a) | `$.beamSwapHandler[*].requestException` 被 CONTAINER 吞并 | §3.0.1 显式 UNBOUND 行 + GAP:N3-B7（§3.4/§9） | DIST gap=B7 paths=1；探针 Q3 16/16 PASS（§11.8） |
| R3-2(b) | `$.tileMarkHandler[*].reason` 整组 OUT_DIFF 错误 | scoped 拆分：SEM_MAP 6 case + IN_BIND 1 case（tm-response-window :417-418）+ UNBOUND_HC 13 case；outcome 同法（§3.4） | SCOPE 列机器校验 scope-errors=0；DIST gap=B8=3（note）；探针 Q4 |
| R3-2(c) | tileMovement m1b 误判 OUT_DIFF | m1b cross 字段显式 INTERNAL（结构自洽 `:644-666`，无生产差分语义，非 GAP）（§3.4） | 探针 Q6（coherent+100 → 16/16 PASS）与 INTERNAL 判级一致 |
| R3-2(d) | hashCrosswalk 6 路径单一 SEM_MAP 标签但 C# 只读 3 | 拆分：SEM_MAP 3（:95/:96/:97）+ UNBOUND 2（→E3）+ CONTAINER 1（§3.1） | DIST gap=E3 paths=5；探针 Q1 9/9 PASS |
| R3-2(e) | shuffle threeCasterChanged/plans/dealerSeat 表≠文 | 显式 INTERNAL 行段规则 17 条（:39-203 导出锚；C# 侧 :179-184/:193 关系检查）（§3.5） | DIST shuffle INTERNAL=17 |
| R3-2(f) | snapshotRoundTrip 两布尔信任级 | 记 UNBOUND_HC GAP:N3-B5（读点 :261-262 在案；翻值即 FAIL 但无计算值差分）（§3.4/§9） | B5 = 10（DIST gap 行） |
| R3-2(g) | broad catch-all 需全量复核 | 8 条 catch-all 全部删除、以显式规则替换；v2 CLASS 校验使 catch-all 不可能再通过 | 正检 mismatches=0；§3.0.1 修复对照 (g) |
| R3-3(a) | §13 算术：24 与 "+E1=25 行为级" 矛盾 | §13/§3.4：行为级 = A16+B2+C6=**24**；E1 存在级另计；合计 25/75（§13 DEF-2） | case-refs buckets 输出（§11.7） |
| R3-3(b) | 6 条 goldenInputs 缺口误归 tileMark/beamSwap | 更正为 sleeveBloom 六叶 `{availableWallCount,offerId,rinshanPointer,wallEndExclusive,wallPointer,windowId}`（§3.4/§9 N3-B3；tileMark/beamSwap goldenInputs 已绑定） | DIST gap=B3 paths=6；探针 Q5 |
| R3-3(c) | N3-B2 缺 ShuffleGoldenTests.cs 绑定任务 | §9 N3-B2 扩展 = 注释更正 + deadWallSegmentEqual 绑定断言 | DIST gap=B2 paths=1 |
| R3-3(d) | mappingBasis 真实路径 | `$.cases[*].v5.expected.mappingBasis`（census 证实；导出锚 :740-742）（§3.6/§9 N3-B6） | DIST gap=B6 paths=3 |
| R3-3(e) | win/noWin 分母 95/42 漏容器 | 96/43（容器路径归属口径修正，非消费语义变化）（§3.2） | DIST scoring：IN_BIND 144/OUT_DIFF 136/SEM_MAP 72/UNBOUND 6 = 419 |
| R3-3(f) | "m1b 11 叶 golden 输出对照" 不实 | §3.4/§9 N3-B1：B1 = 14 条显式清单，m1b 移出为 INTERNAL，无 "11 叶" 表述 | DIST gap=B1 paths=14 |
| R3-3(g) | snapshot 两布尔须可执行绑定 | §9 N3-B5 = 10（含两布尔逐值对照任务）（§3.4/§13 INFO-4） | DIST gap=B5 paths=10 |

### 15.2 不可发现性说明（后续轮次追加缺陷时适用）

本报告第 4 轮的以下机制**自第 3 轮审查后才存在**：v2 校验器的 -Repo 真实锚点核对、DUP/SCALAR-AS-CONTAINER/SCOPE 拒绝、有序 first-match 裁决口径、scope 列 case 集机器校验、DIST 分布输出、497 条 8 列规则表的 66 条 cheat scoped 行段规则。因此——凡属"第 3 轮规则表/校验器无法表达或无法检出"的判级误差（如 scoped case 拆分粒度、行段锚点行界、同 pattern 重复行），其在此前的不可发现性以第 3 轮工具能力为界；第 4 轮起这些机制在案，后续轮次不得再以"工具不支持"为由豁免同类核查。**第 5 轮新增机制**（自第 4 轮审查后才存在，第 4 轮及以前不可发现）：v3 逐节点 presence 枚举（v2 arr[0] 采样）、双向 scope 并集等式与六种 SCOPE 显式错误、`where` 分区谓词文法、CASE-DIST 逐单元分布（v2 只有路径级 DIST，跨分区/混合路径单元缺口不可见）、探针驱动逐探针期望校验 + 失败累计 + 负向自检（v2 时代 counterfactual 失配仍 exit 0）、历史工具 git 存档锁 SHA（v2 时代依赖旧 TEMP 在场）。凡属纯源码阅读可达的事实（如 `:261-262` 信任级断言、`deadWallSegmentEqual` 零引用、m1b 结构自洽断言性质、**goldenInputs 各叶的真实读点 :718-826 与逐 case 分布**），第 1-4 轮即应可发现，本报告在第 4/5 轮方才更正的（snapshot 归 B5、m1b 移出 B1、B2 文件扩展、**goldenInputs 容器行 IN_BIND 误作输入绑定、m5 factsAfter index/seat 分区坐标 OUT_DIFF 以偏概全**——后者属 R3-2"内部自洽误作输入绑定"同类残余），更正原因记录于 §15.1/§15.3 对应行与 §3.0.1 修复对照，不新增豁免。**第 6 轮新增机制**（自第 5 轮审查后才存在，第 5 轮及以前不可机器表达）：`CensusInstances` 逐实例清单（655,145 实例全集——v3 无独立实例枚举源，unscoped 家族的"1 路径 = 1 单元"是当时的机制上限，R5-1 的混合分母缺陷在该机制下不可见）；where 谓词按实例父对象标量字段求值的数据面（v3 谓词文法已实现但规则表未使用）；第 9 判级类 UNA 与 `UNA-NOT-NULL` 校验；`NOT_EXERCISED` 记账口径（源码分支存在但冻结样本 0 个 ≠ 未比较）。**同时按 caller 裁定明确**：R5-2 的 factsAfter 坐标真实读点错误（`:1016-1044` 消费器 switch、m5 wall=0、v3 TSV 无 where 行而正文声称"保留 where 谓词行"）属 R4-2 语义核查残余，纯源码阅读可达，**不作不可发现豁免**（§15.4 R5-2 行）。**第 7 轮新增机制**（自第 6 轮审查后才存在，第 6 轮及以前不可见——两缺陷均源自 v4 当轮新增的 scoped/unscoped 双分支逻辑，v3 及更早无此机制）：`$pathClasses` 的 (fixture,path) 复合键与 `CASE-MIXED fx=` 显式命名空间（v4 单路径键的跨文档合并是第 6 轮工具的机制内缺陷，独立 mixed-namespace oracle 自第 7 轮起在案为常设控制行）；UNA 非空守卫的全分支共享化与 UNA 类错误的逐实例无上限打印（第 6 轮 v4 的 scoped-only 守卫是其自身新逻辑的遗漏面，非历史工具可表达）。R6-1/R6-2 因此按不可发现处理、不改写 v3/v4 历史（§15.5）；自本报告起这些机制在案，后续轮次不得再以"工具不支持"为由豁免同类核查。

### 15.3 caller R4 审查意见逐项映射（REWORK_REQUIRED，commit 4561431，3 MAJOR / 0 BLOCKER）

| caller 意见 | 内容摘要 | 第 5 轮修复落点 | 机器验收 |
|---|---|---|---|
| R4-1(a)(b)(c) | v2 "并集=字段实际出现 case 全集"未实现：presence 采样 arr[0]、scope 校验仅 pres⊆union 单向、caseUniverse 取整 fixture 致跨分区洞（caller 指定负例 scope 追加 `\|sb-activate-disabled` → v2 仍 `RULES-CHECK-OK-V2 scope-errors=0` exit 0，log SHA `17EC626D…`） | §11.6 verify-mapping **v3**：逐节点 presence 枚举（遍历每案例每数组元素）+ **双向** scope 等式（并集=present 集 且每 (case,path) unit 恰一行覆盖）+ 六种显式 SCOPE 错误（UNKNOWN/ABSENT/UNCOVERED/OVERLAP/MIXED/NO-CASE-ID） | 负例 scope-extra-case：`FAIL SCOPE-ID-ABSENT cheat-v1\|$.sleeveBloomHandler[*].goldenInputs.availableWallCount sb-activate-disabled`、exit 1（§11.6 表）；12 负例全 DETECTED；兄弟行换序后 CASE-DIST 逐字节不变（§11.6 实测）；正检 scope-errors=0 |
| R4-1(d) | 六条 HC scoped 行 = 116 个 (case,path) 单元，报告冒称"72 条路径"且"其余 22 条全部 IN_BIND"不实；§9 N3-B3 数字须重算 | §1.3/§3.0.1 双口径声明：DIST（路径，定义投影 101）与 CASE-DIST（单元，第 5 轮时点记 286）并列公布；§9 N3-B3 重算为 **21 单元/6 路径**、N3-B8 重算为 **116 HC 单元 + 3 note 路径（第 5 轮 CASE-DIST 119，第 6 轮逐实例 191）**、新增 N3-B10 28 单元/N3-B11 第 5 轮记 26 单元；mixed-paths 第 5 轮记 27 **【第 6 轮更正】**：286 为"scoped 逐实例 + unscoped 1 路径 = 1 单元"混合分母（R5-1 作废），全族逐实例后 = **592**（mixed-paths=34、B11=11、B8=191），见 §15.4 | 第 5 轮实测 `CASE-TOTAL units=1821 gap-units=286 mixed-paths=27` + `CASE-DIST gap=GAP:N3-B8 units=119`（历史在案）；第 6 轮同位实测 `CASE-TOTAL units=655145 gap-units=592 mixed-paths=34` + `CASE-DIST gap=GAP:N3-B8 units=191`（§11.6 实测 log `bdc7ca11…`；**【第 7 轮更正】v4 的 34 含 7 条跨 fixture 假混合，fx 键后真混合 27，§15.5 R6-1**）；"101 = 定义投影、不宣称枚举全部未绑定单元"维持写入 §1.3/§3.0.1 |
| R4-2 | goldenInputs 容器行 21 条 IN_BIND"已绑 :718-826"不实（实为容器捕获）；m5 factsAfter index/seat 整组 OUT_DIFF 以偏概全（仅 mv-reservation-delivery-then-beam 的 hand/discard 分区 index 等被消费） | FIELD-RULES **v3** 逐单元重列：goldenInputs 26 叶 × case = 79 单元（绑定类 30 = INTERNAL 14 + SEM_MAP 12 + OUT_DIFF 4；UNBOUND 49 = B3 21 + B10 28；**IN_BIND 0**）；factsAfter `index` INTERNAL 2-case + UNBOUND 4-case（→B11）、`seat` UNBOUND 5-case（→B11）+ 落点栏原称"保留 m5 OUT_DIFF `;where=container==hand\|discard` 谓词行" **【第 6 轮更正：此系不实陈述——v3 TSV 实际 0 条 `;where=` 行（谓词文法已在 v3 校验器实现、规则表未使用），where 行真正落地在第 6 轮 v4 的 6 条，§3.4/§11.6；R5-2 不作豁免，§15.4】**；§3.4 新增 26 叶逐单元表 + B11 分区段（第 6 轮按真实读点重写）；§13 DEF-2 同步 | 探针 P7 相对回合对双侧 +100 → 16/16 PASS、P8 相对实体对 +1 → PASS、P9 m5 分区坐标 → PASS（第 5 轮结论"不设防"以偏概全，**第 6 轮更正：生效变异仅 hand/discard index 2 实例，wall seat→999 半支零命中 NOT_EXERCISED**，§10 表 23/§11.8 P9 行）；正控制 P13 sb-activate-allowed cost→999 → 1 FAIL、P14 m5 hand/discard seat→999 → 1 FAIL、P15 关系对单侧破坏 → 1 FAIL（§11.8 表）；CASE-DIST gap=B10 units=28（第 6 轮维持）/B11 第 5 轮记 26 → 第 6 轮 **11**（§15.4） |
| R4-3(a) | probes-round4 非自足：默认 SourceDir 指向 R3 TEMP 绝对路径、Invoke-RuleCheck 结果弃置、无期望校验、counterfactual 失配仍 exit 0 + COMPLETE | §11.8 **probes-round5** 驱动：R3 历史工具改从 git `7ea62e6` 提取并锁 SHA（`1CE17DAC…`/`D932A03F…`）、每轮全新 census、16 探针逐项 nativeExit/Total/Failed/Skipped/NotRun 期望 + 失败累计 → 任何失配 ROUND5-PROBES-FAILED exit 1、`-SelfTest -SkipMutation` 负向自检（守卫生效判 SELFTEST-PASS） | 全跑 TRUE_EXIT=0 ROUND5-PROBES-COMPLETE（§11.8 driver-run.log `e622e201…`）；负向自检 SELFTEST-PASS（仅被跳探针自身 2 条期望失配；driver-selftest.log `e42aa980…`）；LOOP-R5 提取件复算 16 fixture SHA 与 §11.8 全等（第 5 轮在案；第 6/7 轮 §11.10 ⑤ 同等复现） |
| R4-3(b) | 探针正文 5 处变异描述与工具实际行为不符（Q1 "targetIds 全改 WRONG" 实为仅双标签；Q3 "全字段删除" 实为置 WRONG；Q4 "WRONG_PHASE" 实为 NOT_YOUR_TURN；Q5 "六叶整体删除" 实为在场者置 WRONG/999；Q7 "deadline±1" 实为 base/reserveMillis→999） | §10 表 11/14/15/20 行与 §11.8 R4 存档段全部按工具 `$mutations` 哈希表逐字更正；§3.6 ai-clock 段同步；历史日志 SHA 不变、仅正文更正并注明撤回 | §11.8 P1-P16 变异列与工具源逐字一致（同一哈希表内嵌 §11.8）；R4/R5 两轮独立运行变异 fixture SHA 逐一相符（P1 `7B605C10…` 等 7 件，§11.12 caller E 对照） |

### 15.4 caller R5 审查意见逐项映射（REWORK_REQUIRED，commit da0155a，2 MAJOR / 0 BLOCKER）

| caller 意见 | 内容摘要 | 第 6 轮修复落点 | 机器验收 |
|---|---|---|---|
| R5-1 | v3 CASE-TOTAL/CASE-DIST 为**混合分母**：scoped 家族按 (case,path) 实例展开、unscoped 家族按"1 条归一化路径 = 1 单元"进账（48 个 winSamples status 实例只计 1、32 个 shuffle deadWallSegmentEqual 实例只计 1）；单元真值须全族逐实例展开、独立枚举来源、重算全部 UNBOUND/UNBOUND_HC 族；兄弟行换序不得改变单元分布 | §11.4b 新增独立工具 **CensusInstances**（与 FullCensus 同构的 net8.0 工具，对 6 fixture 每一个 JSON 节点出现实例产出行式清单 `instances.tsv` 655,145 行，path 与 census 归一化逐字一致、cross-check 叶实例数逐一相等）；§11.6 verify-mapping **v4** 以该清单驱动**全族逐实例判级**（scoped 与 unscoped 一致；每实例恰认领一次 + manifest↔census 双向路径集合等式 `MANIFEST-CENSUS-MISMATCH`）；§3.0.1/§2/§9/§13 全部单元分母重算（`CASE-TOTAL units=655145 gap-units=592 mixed-paths=34`；E1 112 = status 48 + extraHan 48 + noWin 4×4；B2 32；B8 191 = 116+75；B11 11；B10 28 维持）【第 7 轮更正：34 的呈现口径含 7 条跨 fixture 假混合，fx 键后 27，§15.5 R6-1】 | caller 两指定例**逐实例可复算**：`PATH-UNITS fx=scoring-v1 path=$.winSamples[*].expected.status units=48 owners=GAP:N3-E1:48`、`PATH-UNITS fx=shuffle-v1 path=$.cases[*].deadWallSegmentEqual units=32 owners=GAP:N3-B2:32`（§11.6 摘录，log `bdc7ca11…`；v5 同位四行逐字节相同，§11.6）；status 恰 48、deadWallSegmentEqual 恰 32 均由独立枚举直接得出；全局总量 655145 = 独立枚举按定义展开（UNITS fx= 六行合计）；兄弟行换序（组内轮换）后 **CASE-DIST/PATH-UNITS/CASE-TOTAL 逐字节不变**（cmp 全零，§11.6 实测；v5 复跑同结论），DIST-TOTAL gap-paths 101→113 为 first-match 投影有意语义 |
| R5-2 | v3 把 factsAfter **未读 null 坐标 extrapolate 成 INTERNAL**；m5 factsAfter 只有 4 个 hand/discard 条目（wall=0），此前"m5 wall seat→999 已实测不设防"系事实错误（且 caller 更正其第 4 轮"m5 含 wall 事实"假设——不得为迁就错误描述虚构样本）；正文称 wall index"显式排除"但消费器 `:1030-1031` **本就含 wall 分支**（0 冻结样本 ≠ 源码不比较）；v3 声称保留 `;where=container==hand\|discard` 行但 v3 TSV 实际 0 条 where 行；须按真实容器适用性 + 实际消费逐节点判级、未读 null 坐标显式 UNA、"源码分支存在但冻结样本 0 个"（NOT_EXERCISED）与"该单元未比较"严格区分、P9 公布真实生效变异 | 逐节点按**真实容器适用性 + 实际消费**判级：`factsAfter[*].index`/`[*].seat` 由 v3 4 条整分区行拆为 **6 条 where 谓词行**（wall index 2 INTERNAL + hand/discard null index 11 UNA + deadWall/replacement index 4 B11 + m5 hand/discard seat 4 OUT_DIFF + 其余 hand/discard seat 7 B11 + wall/deadWall/replacement null seat 6 UNA，§3.0.1 内嵌表 705-710）；**UNA 类**（§1.4 第 9 类，17 units 不进缺口分母）+ `UNA-NOT-NULL` 校验；"显式排除"表述撤回（§3.4）、"m5 wall→999 已实测"更正为**零命中 NOT_EXERCISED**（§3.4/§10 表 23/§11.8 P9 行，m5 wall=0 由 caller E `M5 wallFacts=0` 与本方 manifest 双侧实证，未虚构样本）；"保留 where 行"撤回并如实记账（§15.3 R4-2 行标记）；P9 结论按**实际生效变异 = m5 hand/discard index 2 实例**重写（§10 表 23）；B11 = **11 units**（§9 N3-B11 重算，26 作废）；历史日志 SHA 全部不变、仅正文更正 | 分区真值由 manifest 逐一验证：index 17 = wall 2 + hand/discard 11 + deadWall/replacement 4；seat 17 = m5 4 + 其余 hand/discard 7 + wall/deadWall/replacement 6；`UNITS/CASE-DIST class=UNA 17` 与实例清单直接相符（`una-errors=0`）；负例机器证明机制真实性——`una-drop` → 11 × SCOPE-UNIT-UNCOVERED、`una-nonnull` → 2 OVERLAP + 11 UNCOVERED + 4 SCOPE-ID-ABSENT、`where-value-broken` → 2 UNCOVERED（恰 = 生效实例 2，与 P9 结论互证），15 负例全 DETECTED exit 1（§11.6 表）；R5-2 属 R4-2 语义残余不作豁免（§15.2）。**【第 7 轮补注（R6-2）】v4 的 UNA-NOT-NULL 守卫仅装在 scoped 分支，unscoped 行认领非 null 实例不被拒绝（caller 复现：48 个 status 字符串实例记 UNA → exit 0 假绿、gap 592→544）；v5 将同一守卫升级为全分支共享不变量并取消 UNA 类错误的 40 条打印上限，§15.5 R6-2** |

### 15.5 caller R6 审查意见逐项映射（REWORK_REQUIRED，commit 49592ec，0 BLOCKER / 2 MAJOR）

> caller E' 证据目录 `C:/Users/sncdc/AppData/Local/Temp/codex-n3-r6-review-f5ab86b1c5f94aadb4b036367036be22`（只读）：`review-summary.json` SHA256 `6d9baaec…`、`sha256.json` `ff9ac0fb…`、`mixed-path-oracle.ps1` `2206f208…` / `mixed-path-oracle.log` `947adb64…`（MIXED reported=34 fixture-qualified=27 false-cross-fixture=7，`CROSS-FIXTURE-COLLISION-REPRODUCED`）、`una-unscoped-negative.ps1` `bb61fa2a…`、`una-unscoped-nonnull.tsv` `3671b3dab66b4c6a25d02b43f598f4289cbe5d450378a82277080f9eb44823ba` / `una-unscoped-nonnull.log` `e33945a3…`、`check.log` `809ce514…`。两缺陷均源自 v4 **新增逻辑**（scoped/unscoped 双分支 + unscoped 类写入），v3 无此机制、不可发现性成立（§15.2 不新增豁免、不改写历史）。两缺陷同轮批量修复、统一负例矩阵 17 项（§11.6）。

| caller 意见 | 内容摘要（实际 vs 期望） | 第 7 轮修复落点 | 机器验收 |
|---|---|---|---|
| R6-1 | **实际**：v4 `$pathClasses` 仅以归一化路径为键，unscoped 类写入后不同 JSON 文档的同名路径跨 fixture 合并——34 条 CASE-MIXED 中 7 条为假混合（round-v1 `$.decisionClock`=SEM_MAP 与 ai-v1 `$.decisionClock`=CONTAINER 各自 fixture 内均不混合；另有 `$.exportedAtNote`/`$.extractionStatus`/`$.generatedByCommand`/`$.generationResult.exporterTestResult`/`$.generatorVersion`/`$.unexportedSamples`）；真混合 27 条全部 cheat-v1。**期望**：混合汇总以 (fixture,path) 为键并显式输出 `fx=`；不得以"定义投影"等改名理由豁免跨文档合并；兄弟行换序不改变 fx 限定的 CASE-MIXED/CASE-DIST/PATH-UNITS | §11.6 verify-mapping **v5**（SHA `b094d2a8540877d199ee1dc99753cf6c6675bc1534c60c533ca1385cf9f8d51f`，648 行，规则 TSV 未动）：scoped 与 unscoped 两分支的 `$pathClasses` 均改 `$fx + '\|' + $path` 复合键；混合汇总逐行输出 `CASE-MIXED fx=<fx> path=<path> classes=<sorted>`；正文现行值 34→27（§1.3/§3.0.1/§13），34 只保留为明确标注的 R6 错误历史（§11.6/本表）；新增**独立 mixed-namespace oracle**（不调用校验器，从 rules TSV + instances.tsv 重算每 (fixture,path) 类集合）作第 17 项负例控制行 | v5 正检：`CASE-TOTAL units=655145 gap-units=592 mixed-paths=27`、27 行 CASE-MIXED 全部 `fx=cheat-v1`（14 goldenInputs + 7 factsAfter 族 + 6 handler outcome/reason）、`RULES-CHECK-OK-V5 … una-errors=0`（log `fcae3848…`）；oracle `MIXED-NS-ORACLE-OK recomputed-mixed=27 reported=27 fixtures=cheat-v1 mismatches=0`、7/7 `WITNESS-CLEAN`（log `9eaebd99…`）；兄弟行换序复跑：fx 限定 CASE-MIXED/CASE-DIST/PATH-UNITS/CASE-TOTAL 逐字节不变，`DIST-TOTAL gap-paths=113`（有意 first-match 投影语义，与 27 显式区分，§11.6 实测） |
| R6-2 | **实际**：v4 的 `UNA-NOT-NULL`（kind=null 校验）只装在 scoped 分支（~:2305），unscoped 分支 `Add-Unit`→`continue` 绕过它——caller 复现：scoring-v1 `$.winSamples[*].expected.status` UNBOUND→UNA（scope=*、paths=1 不变）→ v4 native **exit 0 假绿**（una-errors=0）、`PATH-UNITS … owners=UNA:CALLER-NEGATIVE:48`、`CASE-DIST class=UNA units=65`、`CASE-TOTAL units=655145 gap-units=544 mixed-paths=34`，48 个真实字符串实例被移出缺口分母。**期望**：UNA kind=null 校验覆盖**全部**被认领实例（scoped+unscoped 共享不变量）；不得用样本名黑名单/硬编码/manifest 置 null/只覆盖既有 2 条 scoped UNA 行实现；既有 `una-nonnull`（scoped retarget）负例不得替代本面 | §11.6 v5：UNA 守卫提为**两分支共享不变量**（unscoped 分支 `Add-Unit` 前同款 `UNA-NOT-NULL` 检查，按实例 kind 判定，无任何名单/硬编码）；错误打印改造：`UNA-NOT-NULL` 逐实例**不设 40 条上限**全量公布，其余错误保持 First 40 + 剩余计数行；§11.6 负例矩阵新增第 16 项 `una-unscoped-nonnull`（构造面与 caller 完全一致），既有 15 项负例全部在 v5 上重跑 | caller 指定输入原样回放（TSV 与 E' `una-unscoped-nonnull.tsv` 逐字节相等）：v5 **EXIT=1**、恰 **48 行 `FAIL UNA-NOT-NULL scoring-v1\|$.winSamples[*].expected.status <case> … kind=string`**（逐实例点名真实非 null 实例，无伪装成路径/锚点/scope 错误）+ `RULES-CHECK-FAILED errors=48`（log `5279c645…`）；v4 同输入 exit 0 假绿复现在案（log `e1e6d837…`）；基线不受影响：UNA 17 units / gap-units=592 / units=655145 / mixed-paths=27 / exit 0；新负例 `una-unscoped-nonnull` → NEGATIVE-DETECTED errors=48（§11.6 表）；既有 `una-nonnull`（scoped 面）负例保持 DETECTED 不受打印上限影响 |

---

---
