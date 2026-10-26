# G1 门2（黄金样本）独立审查与伴随 Fixture 增量核对报告

**报告路径**：`doc/v5-重构/V5-G1-GOLDEN-REVIEW-ANTI-20261026.md`<br/>
**审查者**：Antigravity (Anti)<br/>
**审查基线**：`08c4a75345484aadaf47b30f340992532f9c6ce2`（已发布 PR19 冻结头）<br/>
**独占分支 / 工作树**：`wt/v5-g1-golden-review-anti-20261026`（`D:\majiang-wt\g1-golden-review-anti-20261026`）<br/>
**任务依据**：`CODEX-V5-N3-FINAL-ADJUDICATION-ACCEPTANCE.md` 第 49 行明确保留之“伴随 fixture catalog 增量核对”，依据《V5 模块重构计划》§17.4 判定门 2 是否具备关闭条件。<br/>
**生产与架构基线声明**：**V4 仍为唯一生产架构基线**；G1 未批准，G5 未完成；本仓库严禁创建 `complete.flag`；门 2 评估仅为调用方综合判定的输入，不构成系统级批准。

---

## 1. 审查范围与四层溯源链

### 1.1 冻结目录（六组基准 Fixture）
调用方与本审查者均在冻结头上执行 `tools/verify-v5-golden-catalog.ps1`，原样 Exit 0：
- **Catalog 文件真实路径**：`tests/v5-golden/catalog-v1.json`（文件存在，`git cat-file -e HEAD:tests/v5-golden/catalog-v1.json` Exit 0）
- **Catalog SHA-256**：`236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D`
- **基准 Fixtures（6 个）**：`tests/v5-golden/{physical-v1,scoring-v1,round-v1,cheat-v1,shuffle-v1,ai-v1}.json`
- **规约统计**：requirements = 73，cases = 10,389，random = 10,000，facts = 45
- **覆盖率分类统计**：full = 17，partial = 21，none = 32，v5-only = 3
- **性质认定**：此验证 Exit 0 仅证明冻结目录自身内容与哈希一致，**不覆盖任何伴随样本**。

### 1.2 四个伴随 Fixture 的四阶段完整溯源链
伴随 fixture 是为了对齐 V4 真实运行行为所补充的物理与逻辑黄金样本。依据 `V5-0-1-IMPLEMENTATION-TASKS.md` §17.1，伴随样本**有意不进入冻结六组分母**，以防止破坏冻结的 1,516 path / 655,145 node / 542 field 基准分母。

对四个伴随样本进行端到端四阶段溯源、真实字段 Pin、C# 消费者与基线祖先核验：

| 伴随 Fixture | 实际 Tracked 路径 | 来源 Pin 原名字段与语义 | 首次导出提交 SHA | 返修/交付提交 SHA | 最终集成接受完整 SHA（经祖先核验） | 调用方独立验收记录 | 实际 C# 消费类 |
|---|---|---|---|---|---|---|---|
| `movement-v1` | `tests/v5-golden/movement-v1.json` | `v4Commit` = `d6bc69fa930d005b355a1273ec732fbdf82bbe2f`（真实 V4 导出生成测试提交） | `fdd0287` | `d39be07` (修复 raw seat=-1 与 selector) | `1432d775526de921f2cde3aba283236fc3b8e8c1` | `CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md` | `Mahjong.Domain.Tests.TileMovementGoldenTests` |
| `shuffle-reversed-v1` | `tests/v5-golden/shuffle-reversed-v1.json` | `v4BaseCommit` = `03084f08af6e2ddefa2ebc6e094de5dac2474fd4`（导出时点冻结树；另含 `v4Commit` = `37b4dd610c5e`） | `8734494` | `0b5cbdf` (修复 plan 字段与 META) + `67d54b5` (行尾修复) | `8254c9b783740ddf519cebc15bb50a8f9974cf58` | `CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md` | `Mahjong.Domain.Tests.ShuffleReversedGoldenTests` |
| `visibility-v1` | `tests/v5-golden/visibility-v1.json` | `v4SourceCommit` = `e8a9e9adc401becef6d05d4cf5de972e998df976`（真实 V4 源 Pin）；`v5BaselineCommit` = `1432d775526d`（V5 任务基线） | `544f9c1` | `4086bf3` (修复 mapped-event 比较与 raw ura) | `f9fd8e8ded4e86d301ccc8d2694b1e7f3e24f38b` | `CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md` | `Mahjong.TableRuntime.Tests.VisibilityGoldenTests` |
| `ai-discard-flags-v1` | `tests/v5-golden/ai-discard-flags-v1.json` | `generatedAtHead` = `85134e9ce4fd67556f6d2f355249c14f4d3b42d4`（导出 HEAD）；`v4EngineLastCommit` = `3f5370c30983`（V4 对局逻辑末次提交） | `daa834a` | `29e11c1` (修复 Visit 算法计数与 2 行 TSV) | `7606c5c1daf2523f1bdb653e439fcc4f3f278cca` | `CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md` | `Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests` |

**基线祖先与路径有效性核验**：
- 每一 Tracked 路径执行 `Test-Path` 均为 `True`，且 `git cat-file -e HEAD:<path>` 原生 Exit 0；
- 每一最终完整接受提交 SHA 执行 `git rev-parse --verify '<sha>^{commit}'` 均准确解析为单一提交（Exit 0）；
- 对冻结基线 `08c4a75345484aadaf47b30f340992532f9c6ce2` 执行 `git merge-base --is-ancestor <sha> 08c4a75`：
  - `1432d775526de921f2cde3aba283236fc3b8e8c1`：**Exit 0**
  - `8254c9b783740ddf519cebc15bb50a8f9974cf58`：**Exit 0**
  - `f9fd8e8ded4e86d301ccc8d2694b1e7f3e24f38b`：**Exit 0**
  - `7606c5c1daf2523f1bdb653e439fcc4f3f278cca`：**Exit 0**<br/>
四项伴随交付的最终接受提交全部为冻结基线的有效祖先，无悬空或未集成产物。

### 1.3 伴随 Fixture 的实际登记位置与结构分母对照

伴随 fixture 依据各自任务书要求，在既有已接受文档中拥有明确的独立登记位置与结构分母记录：

1. **共享台账登记（`movement-v1`、`shuffle-reversed-v1`、`visibility-v1`）**：
   - 登记位置：`doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.md` §8（表列 L173–175）与 §8a（结构分母 L184–186）；
   - **`movement-v1`**：8 案例，SHA-256 `5927F44A650DB963813C352B9786147A5953772862CA33722FDD9CE3E17FA3C5`；结构分母：326 归一化路径 / 229 标量叶路径 / 47,973 节点实例 / 38,685 标量叶实例；C# 读点 `TileMovementGoldenTests.cs:104-111`，配套探针 `tools/v5-golden/probe-movement-binding.ps1`；
   - **`shuffle-reversed-v1`**：32 案例，SHA-256 `81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902`；结构分母：160 归一化路径 / 116 标量叶路径 / 14,236 节点实例 / 13,807 标量叶实例；C# 读点 `ShuffleReversedGoldenTests.cs:213-218`；
   - **`visibility-v1`**：4 案例（12 ID 面），SHA-256 `6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94`；结构分母：770 归一化路径 / 528 标量叶路径 / 5,142 节点实例 / 3,999 标量叶实例；C# 读点 `VisibilityGoldenTests.cs:50,79,87,90-98`，配套探针 `tools/v5-golden/probe-visibility-binding.ps1`（67 runs）；
   - **分母防膨胀纪律**：上述三项在台账 §8 明确声明“不入六 fixture census、不入 592 分母、不重算历史分母”。
2. **独立技术规约登记（`ai-discard-flags-v1`）**：
   - 登记位置：`doc/v5-重构/V5-N3-AI-DISCARD-FLAGS.md` §8 与 `doc/v5-重构/CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md`；
   - 真实文件：`tests/v5-golden/ai-discard-flags-v1.json`，SHA-256 `DB1A872F324A8FB994020FB90700E5F64E4E90ECAA63E1CB3FC3A660CAE10AEE`；
   - 结构分母：采用 Visit 遍历算法，确切普查为 **89 归一化路径 / 63 标量叶路径 / 401 节点实例 / 292 标量叶实例**；
   - C# 消费读点：`dotnet/tests/Mahjong.Ai.Tests/AiDiscardFlagsGoldenTests.cs:130-145`；
   - 状态说明：该独立增量登记已在 Caller 验收中被明确接受，未列入共享台账 §8 仅属文档索引未同步，不影响其作为独立增量黄金的技术有效性。

### 1.4 冻结审计分母与收口状态
- **路径分母**：1,516 paths
- **节点分母**：655,145 node instances
- **字段分母**：542 fields
- **未分析项**：17 UNA（Unanalyzed）
- **缺口总行数**：592 GAP closeout rows（在 `doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv` 中完全闭合：`bound = 411`，`classified = 181`，`pending = 0`）
- **收口原则**：181 项 classified 保持未覆盖（严禁标为 bound/full），按 §17.4 规定冻结缺口分类并移交后续规划，不重开已接受六项分类。

---

## 2. 真实构建、正例执行与负例实验

### 2.1 路径占位符绑定声明
为消除路径歧义，本文档所有命令占位符与实际环境绑定如下：
- `<mirror>`：`artifacts/TEMP/anti-g1-gate2-review-20261026/mirror`（包含 EOL 规范化后的当前头 `tools/verify-v5-n3-closeout-ledger.ps1` 与 `doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv` 镜像，源文本与校验逻辑与仓库完全一致）
- `<tsv>`：`doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.tsv`
- `<E-Caller-R1>`：`artifacts/TEMP/caller-gate2-review-9bce181c5fb84a92a454fc372e2972ba`（Caller 首轮复核证据根）
- `<E-Caller-R2>`：`artifacts/TEMP/caller-gate2-r2-ba0af32870c2461ab0aa30ca4884181f`（Caller 第二轮复核与反证证据根）
- `<E-Anti>`：`artifacts/TEMP/anti-g1-gate2-review-20261026`（Anti 本审查任务独占临时产物根）

### 2.2 正例运行与摘要校验（全部原生数值 Exit 0）

| 验证项 | 命令行（原样） | 原生 Exit | 测试统计 (Total/Pass/Fail/Error/Skip/NotRun) | 执行主体与轮次 | 产物实际路径及 SHA-256 |
|---|---|---:|---|---|---|
| **原黄金目录验证** | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1` | 0 | 6 fixtures / 73 reqs / 10389 cases / 10000 rand / 45 facts | Caller R1 / R2 独立实跑 | `<E-Caller-R2>/catalog.log`<br/>`47B1DFE44E6DCDBE219BED47DA0FB5BB2D1813930DDDA560FDDCB0288D5B7C6E` |
| **收口台账校验** | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot <mirror> -LedgerPath <tsv>` | 0 | 24 CHECK-OK / 44 pins / 14 digests / 411 bound / 181 classified / 0 pending | Caller R1 / R2 独立实跑 | `<E-Caller-R2>/ledger.log`<br/>`B0189E0E94DBC6A96C36A6F349AA7E3B0CEFE5B4197A4308B221ACA28707F72E` |
| **AI 测试工程新鲜构建** | `D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-incremental --nologo` | 0 | 0 warnings, 0 errors | Caller R2 实跑 (`runtime.ps1`) | `<E-Caller-R2>/build.log`<br/>`76F2AA10F991A5BB21A14D06E4C68260B136A9C85AFE8C882B5DB60BAEDE53C3` |
| **AI 伴随黄金定向测试** | `D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests -result-xml <E-Caller-R2>/original.xml` | 0 | Total: 6, Pass: 6, Fail: 0, Errors: 0, Skipped: 0, NotRun: 0 | Caller R2 实跑 (`runtime.ps1`) | `<E-Caller-R2>/original.log`<br/>`3E21C057522F9E6958D9C406FF294B638C0EB5142ED61533A3658FE650C52FA8`<br/>`<E-Caller-R2>/original.xml`<br/>`D66BB8390534F88D1AE7989943F489BDEA6FF731A343138EA18CD388706D1031` |
| **AI 定向测试历史对照** | 同上直接 runner 命令（输出至 `<E-Caller-R1>`） | 0 | Total: 6, Pass: 6, Fail: 0, Errors: 0, Skipped: 0, NotRun: 0 | Caller R1 实跑 | `<E-Caller-R1>/original.log`<br/>`7B1A84B1E73340338E00A82622F793ADFFDB54599927518F411F2B99F8F91CC6`<br/>`<E-Caller-R1>/original.xml`<br/>`E6A4F2DA3BDB6408EED435F23FDC194D3D0343505B04711837BDD8C18952994D` |

### 2.3 负例实验矩阵（预期拒绝与未覆盖反证）

| 实验编号 | 目标防御层 | 变异操作与命令行 | 预期结果与性质 | 实际执行结果 / 失败代号 | 执行主体与轮次 | 产物实际路径及 SHA-256 |
|---|---|---|---|---|---|---|
| **Neg-Ai-Ledger** | 台账白名单防御 | 在收口台账首行 `fixture` 列注入 `ai-discard-flags-v1`，运行 `verify-v5-n3-closeout-ledger.ps1` | 原生 Exit 1，被第 408 行白名单语义拒绝 | **Exit 1**，报 4 个 FAIL：`fixture-unknown`、`id-shape`、`ledger-leak`、`ledger-extra` | Caller R1 实跑 | `<E-Caller-R1>/negative-ai-fixture.log`<br/>`0BB8C97C2D4F6FAFF5604911F4A58C8EB1867258FE0C8024C0546F2E233DA0E9` |
| **Neg-Cat-01** | Catalog 校验边界 | 变异 `tests/v5-golden/ai-discard-flags-v1.json` 根字段 `"fixture": "corrupted-id"`，运行 `verify-v5-golden-catalog.ps1` | 原生 Exit 0，证明原校验器未覆盖伴随样本（未覆盖反证） | **Exit 0**（确认伴随样本在统一 catalog 校验器层属于 `NOT_COVERED`） | Anti R1 实跑 | `<E-Anti>/neg1-catalog-companion-corrupt.log`<br/>`A8C0E25D7356C2F4191D4EA451E94A78AD6B844A7BDD428D1A2D3D1687ACE011` |
| **Neg-Ai-01** | C# 字段值级消费 | 设置 `MAHJONG_AI_DISCARD_FLAGS_GOLDEN` 指向上述变异文件，运行 C# direct runner | 原生 Exit 1，C# 断言对 `fixture` 字段值比较失败 | **Exit 1**，Total: 6, Failed: 1 (`Assert.Equal Failure: Expected "ai-discard-flags-v1", Actual "corrupted-id"`) | Anti R1 实跑 | `<E-Anti>/ai-discard-mut-id.xml`<br/>`DA741435C9F10A1EFAD7C737A2CBA90783071906DFFC09747DA5A888E6B71590` |
| **Neg-Ai-Prov** (D-R2-02 反证) | C# 覆盖边界反证 | 变异 `ai-discard-flags-v1.json` 之 `provenance.generatedAtHead` 为 40 个 0，设置环境变量指向变异副本并运行 C# direct runner | 原生 Exit 0，证明 C# 仅断言长度而未比对真实提交 SHA（未覆盖反证） | **Exit 0**，Total: 6, Pass: 6, Fail: 0（证明 C# 不等价于哈希/来源防篡改） | Caller R2 实跑 (`runtime.ps1`) | `<E-Caller-R2>/mutated.xml`<br/>`00AE33E81D3C116A393CB085295D0D94120FA8A3314ADD76A9BFD9AC98036B70`<br/>`<E-Caller-R2>/mutated-provenance.json`<br/>`7465EF3F98FD9F8D53020024A165556A73B75C1671EDC2E26E17044F9E0F28BC` |
| **Neg-Ledger-A** | 台账 classified 理由防御 | 移除 B1 包某 classified 行的强制分类理由字段，运行台账校验器 | 原生 Exit 1，报 `classified-reason` | **Exit 1**，Failures: 1 (`classified-reason`) | Anti R1 实跑 | `<E-Anti>/negA-run.log`<br/>`DD8ABAC1425D48DD1CF9F8E30C54B71F1487A3F21151EF1D230EAB8FE21CD305` |
| **Neg-Ledger-B** | 台账伪造 bound 防御 | 将 B6 包 mappingLimits 行伪升级为 `bound`，运行台账校验器 | 原生 Exit 1，多重违规阻断 | **Exit 1**，Failures: 11 (`bound-anchor` x4, `bound-commit` x2, `verdict-vector` x2, `verdict-total` x2, `adjudication-pin` x1) | Anti R1 实跑 | `<E-Anti>/negB-run.log`<br/>`80A5BB8F41F25B6B3F6C052F3F877A444F949176398CC1317C535BF1520D218B` |
| **Neg-Ledger-C** | 台账增量黑名单防御 | 在 TSV 某行注入 `movement-v1`，运行台账校验器 | 原生 Exit 1，报增量与白名单双重违规 | **Exit 1**，Failures: 5 (`incremental-fixture`, `fixture-unknown`, `id-shape`, `ledger-leak`, `ledger-extra`) | Anti R1 实跑 | `<E-Anti>/negC-run.log`<br/>`EF04A00CA7DF78C77F147DB9366AE2B6F1258329EF6D2F2BE5BB9D39B8474468` |

---

## 3. 伴随 Fixture 与台账机制深度核对与定性

### 3.1 伴随 Fixture 的独立性与分母隔离
- **设计初衷**：`movement-v1`、`shuffle-reversed-v1`、`visibility-v1` 与 `ai-discard-flags-v1` 均是为支持真实 V4 运行时复杂逻辑（物理移动守恒、正逆序码牌重算、快照深拷贝可见性、摸打弃牌立直鸣牌标记）而生成的黄金事实。
- **分母隔离规范**：依据计划 §17.1，这些伴随样本严禁混入六组基准黄金的分母中。若将其路径或节点计入基准分母，会导致 1,516 路径与 655,145 节点基线膨胀变形。

### 3.2 台账通用白名单防御与“分母泄漏风险”澄清
- **机制核查**：在 `tools/verify-v5-n3-closeout-ledger.ps1` 第 405–409 行：
  ```powershell
  $fxSeen = @{}
  foreach ($r in $rows) { $fxSeen[$r.fixture] = $true }
  foreach ($fx in $fxSeen.Keys) {
    if ($censusFixtures -cnotcontains $fx) { Fail 'fixture-unknown' $fx }
  }
  ```
- **澄清结论**：
  1. 台账校验器采用的是**硬性白名单机制**（`$censusFixtures = @('physical-v1','scoring-v1','round-v1','cheat-v1','shuffle-v1','ai-v1')`）。任何不在白名单内的 fixture（无论是 `ai-discard-flags-v1` 还是任意非法字符串）在解析台账行时均直接触发 `Fail 'fixture-unknown'` 并退出 1。
  2. 第 410–412 行的 `$incrementalFixtures` 仅是针对更早前三个增量 fixture 的补充黑名单断言，并非唯一防护防线。
  3. Caller 实跑之 `Neg-Ai-Ledger`（日志 `0BB8C97C...`）实测证实，若将 `ai-discard-flags-v1` 注入 TSV，脚本立即以 Exit 1 阻断并报 4 个 FAIL，**绝不存在伴随样本无阻断混入 592 分母的系统性漏洞**。首轮报告推断的“分母漏检风险”在语义与实测上均不成立。

### 3.3 已接受的独立增量登记（Independent Registry）
- **依据与范围**：原任务明确授权认可“被明确接受的独立 registry”。
- **事实认定**：`ai-discard-flags-v1` 的增量规约与节点普查已在 `doc/v5-重构/V5-N3-AI-DISCARD-FLAGS.md` §8 完成完整登记：
  - 采用 Visit 遍历算法：**89 paths / 63 leaf paths / 401 node instances / 292 leaf instances**；
  - 该独立登记已在 `CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md` 中被调用方独立验收并批准接受；
  - 最终六项 caller 验收明确采纳其分类证据。
- **状态定性**：`ai-discard-flags-v1` 在领域规约与验收链上已具备权威独立登记，并非无主增量。其未在共享台账 `V5-G1-N3-CLOSEOUT-LEDGER.md` §8/§8a 汇总表中同步列名，属于文档索引未刷新的管理项，不构成架构或规约缺口。

### 3.4 仓库级伴随校验器缺口（NOT_COVERED）、C# 断言覆盖边界与反证
- **自动化缺口定性**：
  - 现有的 `tools/verify-v5-golden-catalog.ps1` 仅校验六组基准 catalog，未包含对四个伴随 fixture 的哈希与规约检查；
  - 仓库目前尚未构建统一的 `tools/verify-v5-companion-fixtures.ps1` 脚本；
  - **明确声明**：伴随 fixture 在仓库级脚本静态哈希核查上属于 **`NOT_COVERED`**。
- **C# 单元测试覆盖边界与反证（D-R2-02）**：
  - 四个伴随样本在 C# 测试中均有对应测试类（`TileMovementGoldenTests`、`ShuffleReversedGoldenTests`、`VisibilityGoldenTests`、`AiDiscardFlagsGoldenTests`）；
  - **覆盖范围严格受限于已有具体值级断言**：测试仅对已实现的字段进行值级比较（例如 `AiDiscardFlagsGoldenTests` 比对 `fixture` 标识、弃牌动作、`isRiichi` 与 `isClaimed` 等业务值，字段变异报真实 Exit 1 / Failed，见 Neg-Ai-01）；
  - **明确 C# 测试不等价于固定哈希或全字节防篡改**：Caller 第二轮实测反证（Neg-Ai-Prov）表明，在 `ai-discard-flags-v1.json` 中将 `provenance.generatedAtHead` 变异为 40 个 0 后，由于 `AiDiscardFlagsGoldenTests.cs:140` 仅断言 `Length == 40` 而未校验实际提交 SHA，测试依然原生 Exit 0 / 6 项全 PASS（产物 `<E-Caller-R2>/mutated.xml` SHA-256 `00AE33E8...`）；
  - **剩余风险确认**：若伴随 fixture 的来源元数据、META 描述、注释或未显式断言的字段被意外篡改或发生漂移，C# 测试无法拦截该类漂移。因此，**不能做出“任何篡改 100% 拦截”或“已被 C# 完全缓解”的绝对保证**；
  - **边界界定**：该 counterexample 用于纠正文档夸称与界定边界，既有契约从未要求为所有来源元数据建立固定哈希校验，不将历史接受的 META 分类重开为新系统缺陷，也不作为推翻门 2 硬收口的理由。

---

## 4. 后续跟踪与维护项清单（非阻断）

本轮完整核对确认，无 BLOCKER 或阻断性 MAJOR 缺陷。以下两项列为非阻断的后续建议与跟踪债务：

| 项编号 | 类型 / 级别 | 位置 | 现状说明 | 修复建议与后续规划 | 建议责任方 |
|---|---|---|---|---|---|
| **REC-G2-01** | 文档维护 / MINOR | `doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.md` §8 & `tools/verify-v5-n3-closeout-ledger.ps1` L181 | `V5-N3-AI-DISCARD-FLAGS.md` §8 已有独立增量登记，但共享台账 §8 未补齐该项说明，且校验器黑名单 `$incrementalFixtures` 未列入 `ai-discard-flags-v1`（已被第 408 行白名单防御） | 在后续台账维护或统一发布时，将 `ai-discard-flags-v1` 补充登记入共享台账 §8 增量清单，并在 verifier 黑名单中补齐该 token，保持台账全集整齐一致。 | 台账维护方 (Caller / PR19 集成方) |
| **REC-G2-02** | 自动化增强 / SUGGESTION | `tools/` 目录 | 伴随 fixture 静态哈希与结构在脚本级属于 `NOT_COVERED`，C# 仅覆盖部分业务断言 | 在后续工程阶段（如 G5 自动化收尾），可按需编写独立的 `tools/verify-v5-companion-fixtures.ps1`，将四个伴随 fixture 的固定哈希纳入静态命令行检查。 | 工具链维护方 |

---

## 5. G1 门2（黄金样本）最终审查结论

依据《V5 模块重构计划》§17.4 硬收口规则：
> “审计 r6 若未发现新的系统性类别，即冻结缺口分类并关闭门 2”。

**判定结论**：**PASS（门 2 达到可宣布对齐关闭口径）**

**判定理由**：
1. **冻结目录真实完备**：六组基准目录通过原 `verify-v5-golden-catalog.ps1` 校验，Exit 0，哈希全同；
2. **伴随样本全链可溯**：四个伴随 fixture 全部来自真实 V4 导出，具有经确认的真实字段源 Pin、交付/接受完整 SHA 与 Caller 验收记录，且接受提交全部为冻结基线的有效祖先；
3. **分母严格封闭**：台账校验器硬性白名单（L408）实测拒绝任何非基准 fixture（Exit 1），592 收口分母无泄漏、无膨胀；
4. **独立登记已被接受**：`ai-discard-flags-v1` 在 `V5-N3-AI-DISCARD-FLAGS.md` §8 中具备经调用的完整普查登记与验收；前三组伴随样本在共享台账 §8/§8a 中均有明确的位置与分母记录；
5. **消费与变异有效防御**：全部伴随样本均在 C# 端实现端到端业务值级消费，业务语义变异均报真实失败；
6. **未覆盖项与反证如实记录**：仓库级脚本未覆盖伴随样本标定为 `NOT_COVERED`，C# 测试未校验来源 SHA 的反证如实记录，门 2 判定严格依据硬收口规则，不依赖全字节防篡改的绝对假设；181 项 classified 保持未覆盖（严禁标为 bound）。

---

## 6. 协议维度自检与架构边界声明

1. **唯一生产架构与完整性状态**：
   - 本结论仅为 G1 门 2 单一门禁的独立复核判定。
   - **V4 仍是全系统唯一生产架构与权威实现**。
   - G1 总体尚未被最终批准，G5 未完成，严禁创建 `complete.flag`。
   - 组件级或单一门禁的 PASS 严禁被等同或宣传为 G1 或 G5 整体通过。
2. **工作树与文件修改边界**：
   - 本审查严格在独占分支 `wt/v5-g1-golden-review-anti-20261026` 与工作树 `D:\majiang-wt\g1-golden-review-anti-20261026` 中进行；
   - 相对冻结基线 `08c4a75345484aadaf47b30f340992532f9c6ce2`，仅新增唯一授权文件 `doc/v5-重构/V5-G1-GOLDEN-REVIEW-ANTI-20261026.md`；
   - 未修改任何生产代码、测试代码、黄金 fixture、catalog、manifest、台账 TSV、既有校验器或历史验收报告；
   - 未执行 `git push`、未创建 PR、未触发 CI、未尝试合并 v5，无转派行为。
