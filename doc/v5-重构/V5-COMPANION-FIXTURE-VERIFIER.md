# V5 伴随黄金样本独立校验器实施报告 (REC-G2-02)

**日期**：2026-10-26
**执行角色**：Anti 独立任务（2026-10-26 授权之最多 4 个独立任务之一）
**冻结工作树基线**：`978a15667f80094ed63c86f69c53195439c95a2c`（分支 `wt/v5-companion-verifier-anti-20261026`）
**任务依据**：落实已接受文档 `doc/v5-重构/V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` §4 中 **REC-G2-02** 独立自动化维护建议，并在本轮完成对第 1 轮独立检视意见（D-R1-01 至 D-R1-06）的全面闭环批量修复。

---

## 1. 任务背景、前置边界与防扩口声明

1. **REC-G2-02 建议实施定性**：
   - 本任务仅针对已接受报告 `V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` §4 REC-G2-02 之建议，新增独立、只读的伴随黄金样本静态哈希与结构校验器及自测套件。
   - **严格不重审门 2**：门 2（黄金样本）已由前序审查判定为 PASS（可宣布对齐关闭口径），本建议实施**严禁升级为 G1 或 G5 的新前置门槛**，亦不改动既有 181 项 classified 的缺口分类或 100% 对齐口径。
   - **分母隔离规范绝对守恒**：四个伴随黄金样本属于特定领域复杂运行时的补充黄金事实，**严格不并入六基准目录（592 行台账 / 1,516 路径 / 655,145 节点分母）**。本次不修改 `tests/v5-golden/catalog-v1.json`、`doc/v5-重构/V5-0-GOLDEN-MANIFEST.md` 或 `tools/verify-v5-n3-closeout-ledger.ps1`。
2. **唯一生产架构与完整性状态**：
   - **V4（Kotlin/JVM + libGDX + MJ-TCP/1）仍是全系统唯一生产架构与权威基线**。
   - V5 整体门禁 G1 尚未最终批准，G5 未完成，**严禁创建 `complete.flag`**。
   - 本任务不启动任何 P4/P5 后续集成阶段，不向远端 push、不建组件 PR、不合入 v5。
3. **独立性与兄弟任务隔离**：
   - 本任务独立于 Unity wrapper / IL2CPP 等 P2.2 线程（`thread_20261026_103011_957d` 等），不读取其未提交产物，不因其未交付反复返修。
   - 本新增校验器作为 standalone 工具独立存在，尚未接入统一 CI 脚本 `tools/verify-v5.ps1`（标定为 **`NOT_RUN`**，且非本任务授权范围）。

---

## 2. 交付与修改文件范围（ExpectedFiles 审计）

本次实施严格遵守最小修改原则，相对冻结基线 `978a15667f80094ed63c86f69c53195439c95a2c`，恰好变更 3 个授权文件，未修改任何既有生产代码、测试代码、黄金 fixture 或公共校验器：

| 动作 | 文件路径 | 类型 / 职责 |
|---|---|---|
| **新增** | `tools/verify-v5-companion-fixtures.ps1` | 独立只读伴随黄金样本校验器 (PowerShell 5.1 兼容) |
| **新增** | `tools/tests/VerifyV5CompanionFixtures.Tests.ps1` | 伴随校验器回归测试套件 (包含 47 项正负例) |
| **新增** | `doc/v5-重构/V5-COMPANION-FIXTURE-VERIFIER.md` | 本实施与验收报告（修正原命名不匹配缺陷 [D-R1-06]） |

**绝对零修改确认**：
- `tools/verify-v5.ps1`：**未修改**（保持既有统一 CI 入口不变）
- `tools/verify-v5-golden-catalog.ps1`：**未修改**（已复测原生 Exit 0，SHA 不变）
- `tools/verify-v5-n3-closeout-ledger.ps1`：**未修改**
- `tests/v5-golden/catalog-v1.json`：**未修改**（6 目录分母严格隔离）
- `tests/v5-golden/movement-v1.json` 等全部 11 个黄金文件：**未修改（全字节 SHA-256 全程一致）**
- `dotnet/tests/` 下 C# 消费类：**未修改**
- 用户主目录 `CLAUDE.md`、`doc/ui/round-preparation/PROMPTS.md`：**未修改、未暂存**

---

## 3. 第 1 轮检视缺陷闭环修复映射 (D-R1-01 至 D-R1-06)

在第 1 轮独立检视中，调用方指出了 6 组已知缺陷（BLOCKER=0、MAJOR=5、MINOR=1），本次返修在同一会话内进行了批量闭环修复与实测验证：

| 缺陷编号 | 级别 | 缺陷简述 | 根因与修复措施 | 验证结果 / 状态 |
|---|---|---|---|---|
| **[D-R1-01]** | MAJOR | 全字节/UTF-8 校验被自动解码与规范化绕过 | 根因：`ReadAllText` 会自动识别并剥除 BOM，随后重写 UTF-8 导致 UTF-16LE 及 BOM 变异被掩盖；混合行尾未做原始字节与 SHA 强匹配。<br/>修复：固化各 fixture 在 Git LF 与 Windows CRLF 两种已接受形态下的精确 `raw SHA-256` 与 `byte count`。增加 `Test-HasBom`（显式拦截 UTF-8 BOM `0xEF, 0xBB, 0xBF` 及 UTF-16 BOM）和 `Get-StrictUtf8String`（使用 `UTF8Encoding($false, $true)` 严禁非法字节）；全量 CLI 入口严格校验原始字节数与原始哈希，非接受字节变异一律报 `INVALID_ENCODING` 或 `HASH_MISMATCH` 退出 1。 | **CLOSED**<br/>`utf16-default` 退出 1 (`INVALID_ENCODING`)；`mixed-eol-default` 退出 1 (`HASH_MISMATCH`) |
| **[D-R1-02]** | MAJOR | 公开 `-SkipHashCheck` 可将损坏业务数据认证为黄金样本 PASS | 根因：CLI 允许 `-SkipHashCheck` 跳过哈希信任根后仍输出 PASS 总结并打印固定静态哈希，导致业务损坏（如牌山重复牌）被虚假放行。<br/>修复：彻底移除公共 `-SkipHashCheck` 参数；仅提供内部专用结构测试接口 `-InternalStructureTestMode`，明确输出 `[STRUCTURE_TEST_MODE]` 标记且绝不输出黄金认证 PASS 与伪造哈希。在领域结构层为 `shuffle-reversed-v1` 增加 136 张物理守恒不变量检验（`WALL_CONSERVATION_INVALID`，0..135 牌号完全互斥不重复）。所有完整 CLI 调用对业务值变异均报 Exit 1，消除假绿。 | **CLOSED**<br/>`duplicate-tile-skiphash` 退出 1 (`WALL_CONSERVATION_INVALID` & `HASH_MISMATCH`)；结构模式独立生效 |
| **[D-R1-03]** | MAJOR | 只读 validator 允许 `-LogPath` 覆盖仓库 fixture | 根因：validator 曾包含 `-LogPath` 参数与文件写入逻辑，若参数被指向 fixture 会覆盖原始黄金样本。<br/>修复：彻底删除 validator 内部所有 `-LogPath` 参数、`Out-File`、`Set-Content` 及任何文件写入逻辑，全部结果写 stdout/stderr，成为 100% 纯只读工具。自测脚本 `VerifyV5CompanionFixtures.Tests.ps1` 的 `-LogPath` 增加白名单保护：仅允许指向 `build/` 或 `artifacts/` 目录，若指向 fixture 或 tracked 源码立即退出 2 `BLOCKED`。 | **CLOSED**<br/>`log-overwrites-fixture` 产物 `FIXTURE_OVERWRITTEN=False`；尝试 LogPath 指向 fixture 退出 2 `BLOCKED` |
| **[D-R1-04]** | MAJOR | RepoRoot 边界仅词法 StartsWith，目录联接可逃逸根外 | 根因：仅检查字符串 `StartsWith` 无法阻断 NTFS Directory Junction 或符号链接跨根访问。<br/>修复：实现 `Test-HasReparsePoint` 逐级向上递归检测各层目录的 `ReparsePoint` 属性，并在 `Test-IsInsideRepo` 中结合真实物理绝对路径强校验；任何通过 junction 或词法逃逸的请求一律报 `PATH_ESCAPE_DETECTED` 并退出 1。在测试脚本清理阶段实现 `Assert-WithinGuidRoot` 严格断言删除路径位于本次 GUID 根内，且递归清理前优先解构 junction 点，杜绝跟随外部链接误删。 | **CLOSED**<br/>`junction-outside-reporoot` 退出 1 (`PATH_ESCAPE_DETECTED`)；测试清理安全边界 100% 受控 |
| **[D-R1-05]** | MAJOR | 显式要求 Git 但缺 Git 时将环境阻塞记为 FAIL 1 | 根因：当传入 `-VerifyGitPins` 时若子进程环境中无 git，原逻辑抛异常被作为 fixture 验证失败记录为 Exit 1。<br/>修复：在请求 `-VerifyGitPins` 时增加 `git` 可执行命令前置检查，若缺失则显式输出 `BLOCKED: missing required tool 'git' for -VerifyGitPins` 并以原生退出码 2 退出；同时完善 git stderr 捕获，区分工具缺失（Exit 2）与 Pin 不符（Exit 1）。 | **CLOSED**<br/>`missing-git-dependency` 在无 git PATH 下原生退出 2 (`BLOCKED`) |
| **[D-R1-06]** | MINOR | expectedFiles 与报告证据不一致 | 根因：报告路径原写为 `V5-COMPANION-VERIFIER-REPORT-ANTI-20261026.md`，与任务授权的 `V5-COMPANION-FIXTURE-VERIFIER.md` 不一致；自测日志 SHA 在多轮运行后发生变化未同步更新。<br/>修复：通过 `git rm` 移除旧报告路径，将本报告迁移至唯一规范路径 `doc/v5-重构/V5-COMPANION-FIXTURE-VERIFIER.md`；更新所有测试结果计数（47 项）与各产物完整真实 SHA-256。 | **CLOSED**<br/>diff 恰好 3 个 expectedFiles，diff-check 为 0，SHA 真实复核一致 |

---

## 4. 四个伴随 Fixture 的固化 Pin 表与来源溯源

依据各原交付报告与调用者独立验收文档（`CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md`、`CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md`、`CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md`、`CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md`），逐一核实并固化真实物理字节、Git LF 与 Windows CRLF 原始 SHA-256、Git Blob 与来源提交：

| 伴随样本 ID | 相对路径 | 标识字段与值 | Git LF 规范形态 (Bytes / SHA-256) | Windows CRLF 检出形态 (Bytes / SHA-256) | 冻结提交 (FreezeCommit) / Git Blob | 嵌入来源提交 / 字段名 | 真实用例结构与标识字段 | 独立验收文档依据 |
|---|---|---|---|---|---|---|---|---|
| **movement-v1** | `tests/v5-golden/movement-v1.json` | `schema = "v5-movement-golden-v1"` | 1,278,595 B<br/>`C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13` | 1,331,161 B<br/>`5927F44A650DB963813C352B9786147A5953772862CA33722FDD9CE3E17FA3C5` | `fdd0287b415db3438b23694bb35e4b72d76d50aa`<br/>`25331005f30edfb2870cba8ee4a1b582400f346b` | `v4Commit = "d6bc69fa930d005b355a1273ec732fbdf82bbe2f"` | 8 cases (ID 字段 `caseId`，如 `MV-G004-DRAW-DISCARD` 等)，各含 `initialState` 与 `steps` | `CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md` |
| **shuffle-reversed-v1** | `tests/v5-golden/shuffle-reversed-v1.json` | `schema = "v5-shuffle-reversed-golden-v1"` | 75,847 B<br/>`5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619` | 77,359 B<br/>`81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902` | `8734494936aad0d451511156a454b989d508468b`<br/>`16ae21dafdf487707bf8f4064a879de22779654f` | `v4BaseCommit = "03084f08af6e2ddefa2ebc6e094de5dac2474fd4"` | 32 cases (ID 字段 `seed`，值为 0..31)，各含 `dealerSeat`、`threeCasterWall` (136 唯一牌)、`reversedWall` (136 唯一牌) | `CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md` |
| **visibility-v1** | `tests/v5-golden/visibility-v1.json` | `schema = "v5-visibility-golden-v1"` | 141,007 B<br/>`30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E` | 147,185 B<br/>`6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94` | `4086bf3db898d65b9e2119df3247a9beb89ba121`<br/>`eddcc00035978be64bca27efc27ec352ce686bf5` | `v4SourceCommit = "e8a9e9adc401becef6d05d4cf5de972e998df976"` | 4 cases (ID 字段 `id`，如 `VIS-G057A-SNAPSHOT-COPY` 等)，各含 `input`、`expected`、`sourceOnly` | `CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md` |
| **ai-discard-flags-v1** | `tests/v5-golden/ai-discard-flags-v1.json` | `fixture = "ai-discard-flags-v1"` | 14,480 B<br/>`448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8` | 14,984 B<br/>`DB1A872F324A8FB994020FB90700E5F64E4E90ECAA63E1CB3FC3A660CAE10AEE` | `daa834a45fb651b786fc4f13a3d1edfc4d8d4a39`<br/>`ef329f5562b47e6e1f9304f247d7a3bb36cf7125` | `provenance.generatedAtHead = "85134e9ce4fd67556f6d2f355249c14f4d3b42d4"` | 3 cases (ID 字段 `caseId`，如 `riichi-river` 等)，根含 `flagSemantics` (`isRiichi`, `isClaimed`) | `CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md` |

> [!IMPORTANT]
> **真实数据结构差异核实**：
> 1. `ai-discard-flags-v1` 根标识字段为 `fixture`（无 `schema` 字段），其用例由 `caseId` 标识；
> 2. `shuffle-reversed-v1` 的 32 项用例**无 `caseId` 字段**，其唯一主键为 `seed`（0..31 的整数）；
> 3. `visibility-v1` 的 4 项用例 ID 字段为 `id`（而非 `caseId`）；
> 4. `movement-v1` 的 8 项用例 ID 字段为 `caseId`；
> 5. 校验器严格按每份 fixture 的真实结构做最小必要断言，绝不假设统一 schema，绝不自造不存在的同名字段；
> 6. 嵌入的来源提交（如 `v4Commit`、`generatedAtHead`）与 PR19 接受提交（`FreezeCommit`）严格区分，绝不混淆或伪造。

---

## 5. 校验器与测试套件实现说明

### 5.1 校验器设计 (`tools/verify-v5-companion-fixtures.ps1`)
- **100% 绝对纯只读**：彻底移除文件写操作与 `-LogPath` 参数，全部结果仅向 stdout/stderr 输出，保障仓库与测试副本绝不被修改。
- **全字节与严格 UTF-8 双模匹配**：
  - 强制探测并拦截 BOM 标识；
  - 严格以非宽松 UTF-8 解码；
  - 仅接受固化的 Git LF 与 Windows CRLF 两种合法字节形态，拒绝任何非授权编码、BOM 或混合换行漂移。
- **物理守恒与业务反欺诈**：
  - 在全量 CLI 模式下任何单字节变异直接报 `HASH_MISMATCH` 退出 1；
  - 增加 136 张麻将实体牌物理闭环守恒检验（0..135 互斥无重），阻断重复实体或缺牌造假。
- **物理路径与重解析点防御**：
  - 递归检测所有祖先路径是否存在 `ReparsePoint`，拒绝 NTFS Junction 与 Symlink 逃逸；
  - 校验目标路径必须严格在规范化物理仓库根内。
- **严密的三态原生退出码**：
  - `Exit 0`：PASS（4/4 伴随样本完全通过）
  - `Exit 1`：FAIL（哈希不符、结构违规、物理守恒破缺、来源哈希为 0 或缺失等）
  - `Exit 2`：BLOCKED（缺少系统必需工具，或请求 `-VerifyGitPins` 时子进程环境中缺失 `git`）

### 5.2 回归测试套件设计 (`tools/tests/VerifyV5CompanionFixtures.Tests.ps1`)
- **独占 TEMP 隔离与安全析构**：
  - 每次自测在 `$env:TEMP` 中建立独占 GUID 目录；
  - 递归清理前使用 `Assert-WithinGuidRoot` 强校验路径在本次 GUID 树内，并优先解构 reparse point，杜绝递归误伤外部目录。
- **仓库不可变性验证**：自测前后对比真实仓库 4 个 fixture 及校验器源码本身的 SHA-256，确保真实文件 100% 未动。
- **测试用例分层**：
  - **正例基线 (2 项)**：TEMP 隔离沙箱基线与真实仓库 Git Pin 校验，原生 Exit 0。
  - **负例变异 (45 项)**：涵盖缺失文件、空文件、词法与 Junction 逃逸、UTF-16/BOM/混合换行/单字节漂移、牌山物理守恒违背、JSON 损坏、Schema 错位、提交全 0 拦截、用例缺失、身份碰撞、缺少 Git 环境（Exit 2）及测试日志路径越界保护（Exit 2）。

---

## 6. 独立执行验证与实验结果

### 6.1 校验器直接运行验证

```powershell
# 1. 默认全量基线校验
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1
# 原生 Exit 0，输出：PASS: 4/4 companion golden fixtures verified successfully

# 2. 包含 Git 历史冻结 Commit Blob 验证
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins
# 原生 Exit 0，输出：All 4 historical freeze commit git blobs verified equal to pinned hashes.
```

### 6.2 调用方第 1 轮复现探针脚本复跑验证 (`build/companion-caller-review/probes.ps1`)

针对调用方独立复核脚本 `probes.ps1` 中的全部 6 组反例进行直接复跑：

| 探针用例 | 注入操作 | 实际 Exit Code | 实际输出判定 | 结论 |
|---|---|---|---|---|
| `utf16-default` | 重写为 UTF-16LE + BOM | **1** | `INVALID_ENCODING: File contains Byte Order Mark` & `HASH_MISMATCH` | **PASS (阻断成功)** |
| `mixed-eol-default` | 混入单个 CRLF 换行 | **1** | `HASH_MISMATCH: raw bytes SHA-256 or length does not match` | **PASS (阻断成功)** |
| `duplicate-tile-skiphash` | 牌山首两张牌相同 | **1** | `HASH_MISMATCH` & `WALL_CONSERVATION_INVALID` | **PASS (消除假绿)** |
| `junction-outside-reporoot` | 创建指向根外的 Junction 目录 | **1** | `FAIL: PATH_ESCAPE_DETECTED` (reparse points forbidden) | **PASS (阻断逃逸)** |
| `log-overwrites-fixture` | 传入覆盖 fixture 路径 | **0** | `FIXTURE_OVERWRITTEN=False` (校验器无写盘逻辑) | **PASS (完全只读)** |
| `missing-git-dependency` | 仅保留 System32 无 git 环境 | **2** | `BLOCKED: missing required tool 'git' for -VerifyGitPins` | **PASS (正确归类 BLOCKED)** |

### 6.3 回归自测套件执行

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5CompanionFixtures.Tests.ps1
```

- **实际运行总结**：
  - **原生退出码**：`0`
  - **用例总数**：**47 项**
  - **正例基线 (Positives)**：**2 项**（均 Exit 0）
  - **负例变异 (Negatives)**：**45 项**（43 项 Exit 1，2 项 Exit 2 BLOCKED，全部符合预期）
  - **失败用例 (Failures)**：`0`
  - **真实仓库未篡改确认**：`real_fixtures_unchanged=true`
  - **自测原始日志**：`build/companion-fixtures/selftest.log`
  - **日志 SHA-256**：`570000CFBE40D920EF3776945F8662F7631AC38C8D4DB756FCFB57FBA3DEDAD3`

### 6.4 负例测试完整矩阵 (45 项变异)

| 序号 | 用例编号 / 名称 | 目标防御层 | 注入变异操作 | 预期失败代号 | 实际结果 / Exit Code | 判定 |
|---|---|---|---|---|---|---|
| 1 | `neg-missing-movement` | 文件存在性 | 删除 `movement-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 2 | `neg-missing-shuffle-reversed` | 文件存在性 | 删除 `shuffle-reversed-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 3 | `neg-missing-visibility` | 文件存在性 | 删除 `visibility-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 4 | `neg-missing-ai-discard-flags` | 文件存在性 | 删除 `ai-discard-flags-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 5 | `neg-empty-fixture` | 文件体积 | 将 `movement-v1.json` 清空为 0 字节 | `FIXTURE_EMPTY` | Exit 1 / `FIXTURE_EMPTY` | PASS |
| 6 | `neg-path-escape-lexical` | 路径词法安全 | 指定逃逸出仓库根之 `-FixtureDir ..\..\escaped` | `PATH_ESCAPE_DETECTED` | Exit 1 / `PATH_ESCAPE_DETECTED` | PASS |
| 7 | `neg-path-escape-junction` | 重解析点安全 | 创建逃逸出根的 Junction 目录并在参数中引用 | `PATH_ESCAPE_DETECTED` | Exit 1 / `PATH_ESCAPE_DETECTED` | PASS |
| 8 | `neg-encoding-utf16-le` | 编码防绕过 | 重写为 UTF-16LE 编码并带 BOM | `INVALID_ENCODING` | Exit 1 / `INVALID_ENCODING` | PASS |
| 9 | `neg-encoding-utf8-bom` | 编码防绕过 | 前置 UTF-8 BOM 字节 (`0xEF, 0xBB, 0xBF`) | `INVALID_ENCODING` | Exit 1 / `INVALID_ENCODING` | PASS |
| 10 | `neg-encoding-mixed-eol` | 换行防漂移 | 将单个换行符从 LF 篡改为 CRLF | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 11 | `neg-single-byte-drift-movement` | 哈希全字节防御 | 变异 `movement-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 12 | `neg-single-byte-drift-shuffle-reversed` | 哈希全字节防御 | 变异 `shuffle-reversed-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 13 | `neg-single-byte-drift-visibility` | 哈希全字节防御 | 变异 `visibility-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 14 | `neg-single-byte-drift-ai-discard-flags` | 哈希全字节防御 | 变异 `ai-discard-flags-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 15 | `neg-duplicate-tile-cli-full` | 完整 CLI 业务防假绿 | 牌山首两张牌相同，全量 CLI 校验 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` & `WALL_CONSERVATION_INVALID` | PASS |
| 16 | `neg-duplicate-tile-structure-mode` | 物理守恒检验 | 牌山首两张牌相同，结构测试模式 | `WALL_CONSERVATION_INVALID` | Exit 1 / `WALL_CONSERVATION_INVALID` | PASS |
| 17 | `neg-truncated-json` | JSON 语法容错 | 将 `movement-v1.json` 截断至 150 字节 | `INVALID_JSON` | Exit 1 / `INVALID_JSON` | PASS |
| 18 | `neg-corrupted-json-syntax` | JSON 语法容错 | 破坏 `ai-discard-flags-v1.json` JSON 结构 | `INVALID_JSON` | Exit 1 / `INVALID_JSON` | PASS |
| 19 | `neg-movement-schema-mismatch` | Schema 规约 | 篡改 `movement-v1` 之 `schema` | `SCHEMA_MISMATCH` | Exit 1 / `SCHEMA_MISMATCH` | PASS |
| 20 | `neg-movement-root-case-count-mismatch` | 用例计数规约 | 将 `movement-v1` 根 `caseCount` 改为 9 (数组实为 8) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 21 | `neg-movement-cases-array-mismatch` | 用例计数规约 | 移除 `movement-v1` 中第 8 个用例 (数组减至 7) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 22 | `neg-movement-duplicate-case-id` | 用例主键防重 | 将用例 2 之 `caseId` 赋给用例 1 | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 23 | `neg-movement-source-commit-zero` | 来源 Pin 防伪造 | 将 `v4Commit` 置为 40 个 0 | `SOURCE_COMMIT_INVALID` | Exit 1 / `SOURCE_COMMIT_INVALID` | PASS |
| 24 | `neg-movement-generator-mismatch` | 生成器溯源 | 篡改 `movement-v1` 之 `generator` 路径 | `GENERATOR_MISMATCH` | Exit 1 / `GENERATOR_MISMATCH` | PASS |
| 25 | `neg-shuffle-schema-mismatch` | Schema 规约 | 篡改 `shuffle-reversed-v1` 之 `schema` | `SCHEMA_MISMATCH` | Exit 1 / `SCHEMA_MISMATCH` | PASS |
| 26 | `neg-shuffle-source-commit-zero` | 来源 Pin 防伪造 | 将 `v4BaseCommit` 置为 40 个 0 | `SOURCE_COMMIT_INVALID` | Exit 1 / `SOURCE_COMMIT_INVALID` | PASS |
| 27 | `neg-shuffle-case-count-mismatch` | 用例计数规约 | 移除 `shuffle-reversed-v1` 1 个用例 (变为 31) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 28 | `neg-shuffle-duplicate-seed` | 用例主键防重 | 将用例 2 之 `seed` 改为 0 (出现重复 seed) | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 29 | `neg-shuffle-wall-size-invalid` | 136 张守恒不变量 | 将用例 1 之 `threeCasterWall` 缩减为 135 张牌 | `WALL_SIZE_INVALID` | Exit 1 / `WALL_SIZE_INVALID` | PASS |
| 30 | `neg-visibility-schema-mismatch` | Schema 规约 | 篡改 `visibility-v1` 之 `schema` | `SCHEMA_MISMATCH` | Exit 1 / `SCHEMA_MISMATCH` | PASS |
| 31 | `neg-visibility-source-commit-zero` | 来源 Pin 防伪造 | 将 `v4SourceCommit` 置为 40 个 0 | `SOURCE_COMMIT_INVALID` | Exit 1 / `SOURCE_COMMIT_INVALID` | PASS |
| 32 | `neg-visibility-render-sha-mismatch` | 渲染自校验哈希 | 篡改 `renderSha256LfNormalized` 为全 0 | `RENDER_SHA_MISMATCH` | Exit 1 / `RENDER_SHA_MISMATCH` | PASS |
| 33 | `neg-visibility-case-count-mismatch` | 用例计数规约 | 移除 `visibility-v1` 1 个用例 (变为 3) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 34 | `neg-visibility-duplicate-case-id` | 用例主键防重 | 将用例 2 之 `id` 改为用例 1 之 `id` | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 35 | `neg-visibility-missing-input` | 核心结构完整性 | 移除用例 1 之 `input` 段 | `CASE_STRUCTURE_INVALID` | Exit 1 / `CASE_STRUCTURE_INVALID` | PASS |
| 36 | `neg-ai-discard-fixture-identity-mismatch` | Fixture 身份规约 | 将根 `fixture` 改为 `corrupted-ai-fixture` | `FIXTURE_IDENTITY_MISMATCH` | Exit 1 / `FIXTURE_IDENTITY_MISMATCH` | PASS |
| 37 | `neg-ai-discard-provenance-sha-zero` (D-R2-02) | 溯源防全 0 防篡改 | 将 `provenance.generatedAtHead` 置为 40 个 0 | `PROVENANCE_SHA_INVALID` | Exit 1 / `PROVENANCE_SHA_INVALID` | PASS |
| 38 | `neg-ai-discard-v4-engine-commit-zero` | 溯源防全 0 防篡改 | 将 `provenance.v4EngineLastCommit` 置为 40 个 0 | `PROVENANCE_SHA_INVALID` | Exit 1 / `PROVENANCE_SHA_INVALID` | PASS |
| 39 | `neg-ai-discard-render-sha-mismatch` | 渲染哈希校验 | 篡改 `provenance.renderSha256` 为全 0 | `RENDER_SHA_MISMATCH` | Exit 1 / `RENDER_SHA_MISMATCH` | PASS |
| 40 | `neg-ai-discard-case-count-mismatch` | 用例计数规约 | 移除 `ai-discard-flags-v1` 1 个用例 (变为 2) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 41 | `neg-ai-discard-duplicate-case-id` | 用例主键防重 | 将用例 2 之 `caseId` 改为用例 1 之 `caseId` | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 42 | `neg-ai-discard-missing-flag-semantics` | 领域语义完整性 | 移除根 `flagSemantics` 段 | `STRUCTURE_INVALID` | Exit 1 / `STRUCTURE_INVALID` | PASS |
| 43 | `neg-duplicate-fixture-identity-collision` | 样本间身份互斥 | 将 `movement-v1.json` 覆盖至 `shuffle-reversed-v1.json` 制造同名冲突 | `DUPLICATE_FIXTURE_IDENTITY` | Exit 1 / `DUPLICATE_FIXTURE_IDENTITY` | PASS |
| 44 | `neg-missing-git-dependency` | 工具缺失规约 | 在无 git 环境变量下请求 `-VerifyGitPins` | `BLOCKED` | **Exit 2** / `BLOCKED: missing required tool 'git'` | PASS |
| 45 | `neg-test-logpath-target-protected` | 日志路径安全 | 尝试将测试日志指向黄金样本 fixture 路径 | `BLOCKED` | **Exit 2** / `BLOCKED: LogPath ... cannot overwrite` | PASS |

### 6.5 既有基准目录校验器回归验证

运行既有黄金目录校验器 `tools/verify-v5-golden-catalog.ps1`，验证六基准 catalog、manifest 与台账未受任何扰动：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
```

- **执行结果**：原生 Exit `0`
- **输出摘要**：
  `PASS catalog=D:\majiang-wt\companion-verifier-anti-20261026\tests\v5-golden\catalog-v1.json sha256=236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D fixtures=6 requirements=73 cases=10389 random=10000 facts=45 coverage=full:17/partial:21/none:32/v5-only:3`
- **确认结论**：六基准 catalog、10,389 用例与 73 需求锚点完全保持历史冻结状态。

---

## 7. 未覆盖项、依赖与状态声明

1. **统一 CI 脚本未接入声明**：
   - 本次新增之 `tools/verify-v5-companion-fixtures.ps1` 为 standalone 校验工具。
   - 依据授权约束，本任务严禁修改 `tools/verify-v5.ps1`。因此该工具尚未被纳入统一 CI 调度，在全系统自动化流水线中标记为 **`NOT_RUN`**。
   - 兄弟任务 P2.2（Unity / IL2CPP / Android）在原独立线程中继续，本任务不依赖其未交付产物，亦不因其未接入 CI 进行重复返修。
2. **业务逻辑与全字节防御边界界定**：
   - 本工具作为独立的全字节漂移与物理不变量检测工具，负责拦截静态文件篡改、元数据漂移、用例丢失与标识错误；
   - 本工具不执行 C# 运行时代码，不宣称替代 C# 业务单元测试对手牌、役种、摸打时序或可见性投影的动态业务断言；
   - 两者互为补充：C# 测试覆盖端到端业务值消费，本校验器覆盖全字节静态防篡改与来源 Pin 锚定。
3. **环境与平台缺口**：
   - PG、WSS、Unity Editor、IL2CPP 构建、Android 真机/模拟器、iOS 及远端 CI 均未在本次执行，如实记录为 **`NOT_RUN`**。

---

## 8. 产物 SHA-256 汇总表

| 产物文件 | 状态 | 字节数 | SHA-256 |
|---|---|---|---|
| `tools/verify-v5-companion-fixtures.ps1` | 修改/完善 | 26,596 | `57786287948F6F107147BE044A99C2C7F69C9199212FADF852AC5432EF94B3FD` |
| `tools/tests/VerifyV5CompanionFixtures.Tests.ps1` | 修改/完善 | 33,475 | `C09C62AD0FC4D9492DDB2FE387ED675201BC3E417B85A9381FACA9D265EB62FC` |
| `build/companion-fixtures/selftest.log` | 本地测试产物 (ignored) | 36,390 | `570000CFBE40D920EF3776945F8662F7631AC38C8D4DB756FCFB57FBA3DEDAD3` |
| `doc/v5-重构/V5-COMPANION-FIXTURE-VERIFIER.md` | 新增 (迁自旧路径) | 本报告 | 提交时固化 |

---

## 9. 结论

本任务严格依照 `V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` §4 REC-G2-02 建议规范并针对第 1 轮检视意见完成全量闭环修复：
1. 校验器实现严密全字节（LF/CRLF）、严格 UTF-8、BOM 防御、136 牌物理守恒、纯只读无写盘逻辑、NTFS Junction/ReparsePoint 逃逸拦截与 Git 依赖缺失 BLOCKED 退出 2；
2. 回归测试套件实现 47 项正负例全面验证（2 正例、45 负例），原生退出码与错误码全部匹配；
3. 调用方 `probes.ps1` 全部 6 组探针 100% 通过验证；
4. 严格遵守分母隔离，未修改统一 CI、六基准 catalog 或既有台账；
5. 真实仓库全部文件 100% 保持不可变，git working tree 干净整洁，仅包含恰好 3 个预期授权文件。
