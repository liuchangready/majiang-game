# V5 伴随黄金样本独立校验器实施报告 (REC-G2-02)

**日期**：2026-10-26
**执行角色**：Anti 独立任务（2026-10-26 授权之最多 4 个独立任务之一）
**冻结工作树基线**：`978a15667f80094ed63c86f69c53195439c95a2c`（分支 `wt/v5-companion-verifier-anti-20261026`）
**任务依据**：落实已接受文档 `doc/v5-重构/V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` §4 中 **REC-G2-02** 独立自动化维护建议。

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

本次实施严格遵守最小修改原则，仅新增 3 个授权文件，未修改任何既有生产代码、测试代码、黄金 fixture 或公共校验器：

| 动作 | 文件路径 | 类型 / 职责 |
|---|---|---|
| **新增** | `tools/verify-v5-companion-fixtures.ps1` | 独立只读伴随黄金样本校验器 (PowerShell 5.1 兼容) |
| **新增** | `tools/tests/VerifyV5CompanionFixtures.Tests.ps1` | 伴随校验器回归测试套件 (包含 39 项正负例) |
| **新增** | `doc/v5-重构/V5-COMPANION-VERIFIER-REPORT-ANTI-20261026.md` | 本实施与验收报告 |

**绝对零修改确认**：
- `tools/verify-v5.ps1`：**未修改**（保持既有统一 CI 入口不变）
- `tools/verify-v5-golden-catalog.ps1`：**未修改**（已复测原生 Exit 0，SHA 不变）
- `tools/verify-v5-n3-closeout-ledger.ps1`：**未修改**
- `tests/v5-golden/catalog-v1.json`：**未修改**（6 目录分母严格隔离）
- `tests/v5-golden/movement-v1.json` 等全部 11 个黄金文件：**未修改（SHA-256 全程一致）**
- `dotnet/tests/` 下 C# 消费类：**未修改**
- 用户主目录 `CLAUDE.md`、`doc/ui/round-preparation/PROMPTS.md`：**未修改、未暂存**

---

## 3. 四个伴随 Fixture 的固化 Pin 表与来源溯源

依据各原交付报告与调用者独立验收文档（`CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md`、`CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md`、`CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md`、`CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md`），逐一核实真实固化的物理字节、规范 LF-normalized SHA-256、检出原始 CRLF SHA-256、Git Blob 与来源提交：

| 伴随样本 ID | 相对路径 | 标识字段与值 | 规范 LF SHA-256 / 检出 Raw CRLF SHA-256 | 冻结提交 (FreezeCommit) / Git Blob | 嵌入来源提交 / 字段名 | 真实用例结构与标识字段 | 独立验收文档依据 |
|---|---|---|---|---|---|---|---|
| **movement-v1** | `tests/v5-golden/movement-v1.json` | `schema = "v5-movement-golden-v1"` | LF: `C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13`<br/>Raw: `5927F44A650DB963813C352B9786147A5953772862CA33722FDD9CE3E17FA3C5` | `fdd0287b415db3438b23694bb35e4b72d76d50aa`<br/>`25331005f30edfb2870cba8ee4a1b582400f346b` | `v4Commit = "d6bc69fa930d005b355a1273ec732fbdf82bbe2f"` | 8 cases (ID 字段 `caseId`，如 `MV-G004-DRAW-DISCARD` 等)，各含 `initialState` 与 `steps` | `CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md` |
| **shuffle-reversed-v1** | `tests/v5-golden/shuffle-reversed-v1.json` | `schema = "v5-shuffle-reversed-golden-v1"` | LF: `5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619`<br/>Raw: `81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902` | `8734494936aad0d451511156a454b989d508468b`<br/>`16ae21dafdf487707bf8f4064a879de22779654f` | `v4BaseCommit = "03084f08af6e2ddefa2ebc6e094de5dac2474fd4"` | 32 cases (ID 字段 `seed`，值为 0..31)，各含 `dealerSeat`、`threeCasterWall` (136)、`reversedWall` (136) | `CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md` |
| **visibility-v1** | `tests/v5-golden/visibility-v1.json` | `schema = "v5-visibility-golden-v1"` | LF: `30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E`<br/>Raw: `6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94` | `4086bf3db898d65b9e2119df3247a9beb89ba121`<br/>`eddcc00035978be64bca27efc27ec352ce686bf5` | `v4SourceCommit = "e8a9e9adc401becef6d05d4cf5de972e998df976"` | 4 cases (ID 字段 `id`，如 `VIS-G057A-SNAPSHOT-COPY` 等)，各含 `input`、`expected`、`sourceOnly` | `CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md` |
| **ai-discard-flags-v1** | `tests/v5-golden/ai-discard-flags-v1.json` | `fixture = "ai-discard-flags-v1"` | LF: `448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8`<br/>Raw: `DB1A872F324A8FB994020FB90700E5F64E4E90ECAA63E1CB3FC3A660CAE10AEE` | `daa834a45fb651b786fc4f13a3d1edfc4d8d4a39`<br/>`ef329f5562b47e6e1f9304f247d7a3bb36cf7125` | `provenance.generatedAtHead = "85134e9ce4fd67556f6d2f355249c14f4d3b42d4"` | 3 cases (ID 字段 `caseId`，如 `riichi-river` 等)，根含 `flagSemantics` (`isRiichi`, `isClaimed`) | `CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md` |

> [!IMPORTANT]
> **真实数据结构差异核实**：
> 1. `ai-discard-flags-v1` 根标识字段为 `fixture`（无 `schema` 字段），其用例由 `caseId` 标识；
> 2. `shuffle-reversed-v1` 的 32 项用例**无 `caseId` 字段**，其唯一主键为 `seed`（0..31 的整数）；
> 3. `visibility-v1` 的 4 项用例 ID 字段为 `id`（而非 `caseId`）；
> 4. `movement-v1` 的 8 项用例 ID 字段为 `caseId`；
> 5. 校验器严格按每份 fixture 的真实结构做最小必要断言，绝不假设统一 schema，绝不自造不存在的同名字段。
> 6. 嵌入的来源提交（如 `v4Commit`、`generatedAtHead`）与 PR19 接受提交（`FreezeCommit`）严格区分，绝不混淆或伪造。

---

## 4. 校验器与测试套件实现说明

### 4.1 校验器设计 (`tools/verify-v5-companion-fixtures.ps1`)
- **只读安全性**：全程只读仓库与 fixture 路径，无任何文件写入、进程外修改或配置副作用。
- **路径逃逸防御**：解析规范化绝对路径，强制校验 `$resolvedFixtureDir` 及各个 fixture 文件路径必须严格位于 `$canonicalRepoRoot` 内部，任何试图通过 `..\` 逃逸仓库根的行为均直接报 `PATH_ESCAPE_DETECTED` 并退出 1。
- **数值退出码规约**：
  - `Exit 0`：PASS（4/4 伴随样本完全通过）
  - `Exit 1`：FAIL（检测到哈希不符、结构违规、非法来源提交、缺失文件等）
  - `Exit 2`：BLOCKED（缺少必需的 PowerShell 环境工具，如 `Get-FileHash`、`ConvertFrom-Json`）
- **多阶段严密校验**：
  1. **阶段 1：存在性与体积校验**：文件必须存在且体积 > 0。
  2. **阶段 2：哈希防篡改校验**（支持 `-SkipHashCheck` 供结构测试）：计算规范化 LF SHA-256，严格比对固定 Pin。
  3. **阶段 3：JSON 语法反序列化**：验证文件格式为合法 UTF-8 JSON。
  4. **阶段 4：唯一身份与 Schema 校验**：验证 `schema` / `fixture`、版本号，并在会话中追踪已见身份，防御不同伴随 fixture 之间的身份碰撞 (`DUPLICATE_FIXTURE_IDENTITY`)。
  5. **阶段 5：生成器与来源提交校验**：校验导出器路径、提取状态；对嵌入的提交哈希验证必须为 40 字符十六进制，且**绝对不能全为 0**（精准拦截 D-R2-02 来源置 0 反例）。
  6. **阶段 6：用例数与用例 ID 唯一性及集合比对**：用例数组长度必须精确匹配预期；用例 ID/seed 必须非空且唯一；提取集合必须与固定白名单集合完全双向一致。
  7. **阶段 7：领域物理结构不变量校验**：
     - `shuffle-reversed-v1`：验证每例之 `threeCasterWall` 与 `reversedWall` 必须均严格包含 136 张实体牌。
     - `visibility-v1`：验证每例必须包含 `input`、`expected`、`sourceOnly` 结构。
     - `ai-discard-flags-v1`：验证根结构必须包含 `flagSemantics`，且具有 `isRiichi` 与 `isClaimed` 语义。
  8. **阶段 8：Git 提交 Blob Pin 校验**（使用 `-VerifyGitPins` 开关）：通过 `git rev-parse "<FreezeCommit>:<RelativePath>"` 比对历史冻结提交中的对象 Blob 是否全等，不依赖当前 HEAD。

### 4.2 回归测试套件设计 (`tools/tests/VerifyV5CompanionFixtures.Tests.ps1`)
- **独占 TEMP 沙箱隔离**：测试在 `$env:TEMP` 中创建带 GUID 的隔离目录，仅复制 4 个伴随 fixture 及校验器本身，运行完后在 `finally` 块中彻底清理。
- **真实仓库不可变性断言**：在自测开始前记录真实仓库 4 个伴随 fixture 的 SHA-256，自测全部执行完毕后重新计算并比对，断言没有发生单字节篡改（`real_fixtures_unchanged=true`）。
- **覆盖边界与受控测试机制说明**：
  - 若仅依靠端到端全量运行，在文件被篡改时，阶段 2 的 SHA-256 哈希校验会最先拦截（报 `HASH_MISMATCH`），导致结构与语义校验逻辑无法被外部变异触发。
  - 为证明结构、用例数、唯一 ID、字段语义及来源校验分支均为真实生效的防护网（非死代码），测试套件对语义层面的负例启用了校验器的 `-SkipHashCheck` 受控开关，直接检验结构分支在哈希通过或未检查时的独立拦截能力，实证每个分支均具备明确语义代号的 Exit 1 阻断能力。

---

## 5. 独立执行验证与实验结果

### 5.1 校验器直接运行验证

```powershell
# 1. 默认全量基线校验 (含 LF 规范哈希比对)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1
# 原生 Exit 0，输出：PASS: 4/4 companion golden fixtures verified successfully

# 2. 包含 Git 历史冻结 Commit Blob 验证
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins
# 原生 Exit 0，输出：All 4 historical freeze commit git blobs verified equal to pinned hashes.

# 3. 受控跳过哈希模式基线校验 (验证语法与结构基线通过)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -SkipHashCheck
# 原生 Exit 0，输出：PASS: 4/4 companion golden fixtures verified successfully

# 4. 路径逃逸防御测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -FixtureDir "C:\Windows"
# 原生 Exit 1，输出：FAIL: PATH_ESCAPE_DETECTED FixtureDir 'C:\Windows' escapes RepoRoot ...
```

### 5.2 回归自测套件执行

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5CompanionFixtures.Tests.ps1
```

- **实际运行总结**：
  - **原生退出码**：`0`
  - **用例总数**：**39 项**
  - **正例基线 (Positives)**：**2 项**（TEMP 沙箱基线 + 真实仓库带 Git Pin 基线，均 Exit 0）
  - **负例变异 (Negatives)**：**37 项**（全部原生 Exit 1，且匹配预期错误代号）
  - **失败用例 (Failures)**：`0`
  - **真实仓库未篡改确认**：`real_fixtures_unchanged=true`
  - **自测原始日志**：`build/companion-fixtures/selftest.log`
  - **日志 SHA-256**：`E646E07ED83E7D5F9A8777B66688124FD35F11313BD6EC09A987947CD0A643AA`

### 5.3 负例测试完整矩阵 (37 项变异)

| 序号 | 用例编号 / 名称 | 目标防御层 | 注入变异操作 | 预期失败代号 | 实际结果 / Exit Code | 判定 |
|---|---|---|---|---|---|---|
| 1 | `neg-missing-movement` | 文件存在性 | 删除 `movement-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 2 | `neg-missing-shuffle-reversed` | 文件存在性 | 删除 `shuffle-reversed-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 3 | `neg-missing-visibility` | 文件存在性 | 删除 `visibility-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 4 | `neg-missing-ai-discard-flags` | 文件存在性 | 删除 `ai-discard-flags-v1.json` | `FIXTURE_MISSING` | Exit 1 / `FIXTURE_MISSING` | PASS |
| 5 | `neg-empty-fixture` | 文件体积 | 将 `movement-v1.json` 清空为 0 字节 | `FIXTURE_EMPTY` | Exit 1 / `FIXTURE_EMPTY` | PASS |
| 6 | `neg-path-escape` | 路径安全 | 指定逃逸出仓库根之 `-FixtureDir ..\..\escaped` | `PATH_ESCAPE_DETECTED` | Exit 1 / `PATH_ESCAPE_DETECTED` | PASS |
| 7 | `neg-single-byte-drift-movement` | 哈希全字节防御 | 变异 `movement-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 8 | `neg-single-byte-drift-shuffle-reversed` | 哈希全字节防御 | 变异 `shuffle-reversed-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 9 | `neg-single-byte-drift-visibility` | 哈希全字节防御 | 变异 `visibility-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 10 | `neg-single-byte-drift-ai-discard-flags` | 哈希全字节防御 | 变异 `ai-discard-flags-v1.json` 单个字符 | `HASH_MISMATCH` | Exit 1 / `HASH_MISMATCH` | PASS |
| 11 | `neg-truncated-json` | JSON 语法容错 | 将 `movement-v1.json` 截断至 150 字节 | `INVALID_JSON` | Exit 1 / `INVALID_JSON` | PASS |
| 12 | `neg-corrupted-json-syntax` | JSON 语法容错 | 破坏 `ai-discard-flags-v1.json` JSON 结构 | `INVALID_JSON` | Exit 1 / `INVALID_JSON` | PASS |
| 13 | `neg-movement-schema-mismatch` | Schema 规约 | 篡改 `movement-v1` 之 `schema` | `SCHEMA_MISMATCH` | Exit 1 / `SCHEMA_MISMATCH` | PASS |
| 14 | `neg-movement-root-case-count-mismatch` | 用例计数规约 | 将 `movement-v1` 根 `caseCount` 改为 9 (数组实为 8) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 15 | `neg-movement-cases-array-mismatch` | 用例计数规约 | 移除 `movement-v1` 中第 8 个用例 (数组减至 7) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 16 | `neg-movement-duplicate-case-id` | 用例主键防重 | 将用例 2 之 `caseId` 赋给用例 1 | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 17 | `neg-movement-source-commit-zero` | 来源 Pin 防伪造 | 将 `v4Commit` 置为 40 个 0 | `SOURCE_COMMIT_INVALID` | Exit 1 / `SOURCE_COMMIT_INVALID` | PASS |
| 18 | `neg-movement-generator-mismatch` | 生成器溯源 | 篡改 `movement-v1` 之 `generator` 路径 | `GENERATOR_MISMATCH` | Exit 1 / `GENERATOR_MISMATCH` | PASS |
| 19 | `neg-shuffle-schema-mismatch` | Schema 规约 | 篡改 `shuffle-reversed-v1` 之 `schema` | `SCHEMA_MISMATCH` | Exit 1 / `SCHEMA_MISMATCH` | PASS |
| 20 | `neg-shuffle-source-commit-zero` | 来源 Pin 防伪造 | 将 `v4BaseCommit` 置为 40 个 0 | `SOURCE_COMMIT_INVALID` | Exit 1 / `SOURCE_COMMIT_INVALID` | PASS |
| 21 | `neg-shuffle-case-count-mismatch` | 用例计数规约 | 移除 `shuffle-reversed-v1` 1 个用例 (变为 31) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 22 | `neg-shuffle-duplicate-seed` | 用例主键防重 | 将用例 2 之 `seed` 改为 0 (出现重复 seed) | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 23 | `neg-shuffle-wall-size-invalid` | 136 张守恒不变量 | 将用例 1 之 `threeCasterWall` 缩减为 135 张牌 | `WALL_SIZE_INVALID` | Exit 1 / `WALL_SIZE_INVALID` | PASS |
| 24 | `neg-visibility-schema-mismatch` | Schema 规约 | 篡改 `visibility-v1` 之 `schema` | `SCHEMA_MISMATCH` | Exit 1 / `SCHEMA_MISMATCH` | PASS |
| 25 | `neg-visibility-source-commit-zero` | 来源 Pin 防伪造 | 将 `v4SourceCommit` 置为 40 个 0 | `SOURCE_COMMIT_INVALID` | Exit 1 / `SOURCE_COMMIT_INVALID` | PASS |
| 26 | `neg-visibility-render-sha-mismatch` | 渲染自校验哈希 | 篡改 `renderSha256LfNormalized` 为全 0 | `RENDER_SHA_MISMATCH` | Exit 1 / `RENDER_SHA_MISMATCH` | PASS |
| 27 | `neg-visibility-case-count-mismatch` | 用例计数规约 | 移除 `visibility-v1` 1 个用例 (变为 3) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 28 | `neg-visibility-duplicate-case-id` | 用例主键防重 | 将用例 2 之 `id` 改为用例 1 之 `id` | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 29 | `neg-visibility-missing-input` | 核心结构完整性 | 移除用例 1 之 `input` 段 | `CASE_STRUCTURE_INVALID` | Exit 1 / `CASE_STRUCTURE_INVALID` | PASS |
| 30 | `neg-ai-discard-fixture-identity-mismatch` | Fixture 身份规约 | 将根 `fixture` 改为 `corrupted-ai-fixture` | `FIXTURE_IDENTITY_MISMATCH` | Exit 1 / `FIXTURE_IDENTITY_MISMATCH` | PASS |
| 31 | `neg-ai-discard-provenance-sha-zero` (D-R2-02) | 溯源防全 0 防篡改 | 将 `provenance.generatedAtHead` 置为 40 个 0 | `PROVENANCE_SHA_INVALID` | Exit 1 / `PROVENANCE_SHA_INVALID` | PASS |
| 32 | `neg-ai-discard-v4-engine-commit-zero` | 溯源防全 0 防篡改 | 将 `provenance.v4EngineLastCommit` 置为 40 个 0 | `PROVENANCE_SHA_INVALID` | Exit 1 / `PROVENANCE_SHA_INVALID` | PASS |
| 33 | `neg-ai-discard-render-sha-mismatch` | 渲染哈希校验 | 篡改 `provenance.renderSha256` 为全 0 | `RENDER_SHA_MISMATCH` | Exit 1 / `RENDER_SHA_MISMATCH` | PASS |
| 34 | `neg-ai-discard-case-count-mismatch` | 用例计数规约 | 移除 `ai-discard-flags-v1` 1 个用例 (变为 2) | `CASE_COUNT_MISMATCH` | Exit 1 / `CASE_COUNT_MISMATCH` | PASS |
| 35 | `neg-ai-discard-duplicate-case-id` | 用例主键防重 | 将用例 2 之 `caseId` 改为用例 1 之 `caseId` | `DUPLICATE_CASE_ID` | Exit 1 / `DUPLICATE_CASE_ID` | PASS |
| 36 | `neg-ai-discard-missing-flag-semantics` | 领域语义完整性 | 移除根 `flagSemantics` 段 | `STRUCTURE_INVALID` | Exit 1 / `STRUCTURE_INVALID` | PASS |
| 37 | `neg-duplicate-fixture-identity-collision` | 样本间身份互斥 | 将 `movement-v1.json` 覆盖至 `shuffle-reversed-v1.json` 制造同名冲突 | `DUPLICATE_FIXTURE_IDENTITY` | Exit 1 / `DUPLICATE_FIXTURE_IDENTITY` | PASS |

---

## 6. 既有基准目录校验器回归验证

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
| `tools/verify-v5-companion-fixtures.ps1` | 新增 | 21,791 | `B3AC62DB2C4A6FEDE5B452DC8443BBD8C650B32CF2B9FAF79691BDEE5C37BBF3` |
| `tools/tests/VerifyV5CompanionFixtures.Tests.ps1` | 新增 | 18,348 | `9D4B160877FC8174E3822EFAB53AD7E2748F4EF4A1655C772411C8BCC8DAD083` |
| `build/companion-fixtures/selftest.log` | 本地测试产物 (ignored) | 2,755,758 | `E646E07ED83E7D5F9A8777B66688124FD35F11313BD6EC09A987947CD0A643AA` |
| `doc/v5-重构/V5-COMPANION-VERIFIER-REPORT-ANTI-20261026.md` | 新增 | 本报告 | 提交时固化 |

---

## 9. 结论

本任务严格依照 `V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` §4 REC-G2-02 建议规范完成：
1. 建立独立只读校验器 `tools/verify-v5-companion-fixtures.ps1`，涵盖四个伴随 fixture 的固化 Pin、LF 规范哈希、来源提交全 0 拦截、用例结构与物理守恒断言，以及 Git 冻结 Blob 验证；
2. 建立回归测试套件 `tools/tests/VerifyV5CompanionFixtures.Tests.ps1`，在隔离 TEMP 沙箱中完成 2 项正例基线与 37 项语义负例变异，全部 Exit 0/1 符合预期；
3. 严格遵守分母隔离，未修改统一 CI、六基准 catalog 或既有台账；
4. 真实仓库全部文件 100% 保持不可变，git working tree 干净整洁，仅包含 3 个预期授权文件。
