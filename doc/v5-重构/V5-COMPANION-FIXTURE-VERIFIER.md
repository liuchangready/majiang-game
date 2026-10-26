# V5 伴随黄金样本独立校验器实施报告 (REC-G2-02)

**日期**：2026-10-26
**执行角色**：Anti 独立任务（2026-10-26 授权之最多 4 个独立任务之一）
**冻结工作树基线**：`978a15667f80094ed63c86f69c53195439c95a2c`（分支 `wt/v5-companion-verifier-anti-20261026`）
**任务依据**：落实已接受文档 `doc/v5-重构/V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` §4 中 **REC-G2-02** 独立自动化维护建议，并在本轮完成对第 1 轮（D-R1-01 至 D-R1-06）、第 2 轮（D-R2-01 至 D-R2-02）、第 3 轮（D-R3-01 / D-R2-01 遗留、D-R3-02 / D-R2-02）以及第 4 轮（D-R4-01）独立检视意见的全面闭环批量修复。

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
| **新增** | [`tools/verify-v5-companion-fixtures.ps1`](file:///D:/majiang-wt/companion-verifier-anti-20261026/tools/verify-v5-companion-fixtures.ps1) | 独立只读伴随黄金样本校验器 (PowerShell 5.1 兼容，实现全字节防御、强类型物理守恒校验与来源 Git Pin 溯源) |
| **新增** | [`tools/tests/VerifyV5CompanionFixtures.Tests.ps1`](file:///D:/majiang-wt/companion-verifier-anti-20261026/tools/tests/VerifyV5CompanionFixtures.Tests.ps1) | 伴随校验器回归测试套件 (包含 71 项正负用例、Junction/HardLink 别名免疫防御与进程内清理断言) |
| **新增** | [`doc/v5-重构/V5-COMPANION-FIXTURE-VERIFIER.md`](file:///D:/majiang-wt/companion-verifier-anti-20261026/doc/v5-重构/V5-COMPANION-FIXTURE-VERIFIER.md) | 本实施与验收报告（修正原命名不匹配缺陷 [D-R1-06]，包含 R1/R2/R3/R4 全量闭环记录） |

**绝对零修改确认**：
- `tools/verify-v5.ps1`：**未修改**（保持既有统一 CI 入口不变）
- `tools/verify-v5-golden-catalog.ps1`：**未修改**（已复测原生 Exit 0，SHA 不变）
- `tools/verify-v5-n3-closeout-ledger.ps1`：**未修改**
- `tests/v5-golden/catalog-v1.json`：**未修改**（6 目录分母严格隔离）
- `tests/v5-golden/movement-v1.json` 等全部 11 个黄金文件：**未修改（全字节 SHA-256 全程未动）**
- `build.gradle` / `settings.gradle`：**未修改**
- `dotnet/tests/` 下 C# 消费类：**未修改**
- 用户主目录 `CLAUDE.md`、`doc/ui/round-preparation/PROMPTS.md`：**未修改、未暂存**

---

## 3. 检视缺陷闭环修复映射 (R1、R2、R3 与 R4 全量闭环)

### 3.1 第 4 轮检视缺陷闭环映射 (D-R4-01)

| 缺陷 ID | 严重级别 | 根因与修复措施 | 验证命令 / 复现证据 | 验收判定 |
|---|---|---|---|---|
| **[D-R4-01]** | **MINOR** | **浮点转换分支在 Int64 上界因 double 舍入产生未处理溢出**：<br/>1. **根因**：上一轮引入的浮点数转换分支中，使用了 `$d -gt [long]::MaxValue` 逻辑。由于 IEEE 754 双精度浮点（53 位尾数精度）将 `[long]::MaxValue`（$2^{63}-1$）舍入为 $2^{63}$（`9223372036854775808.0`），导致当输入为 `[double]9223372036854775808.0` 时，`$d -gt [long]::MaxValue` 判定为 `$false`；随后执行 `$wideVal = [long]$d` 强制转换时抛出未处理的 `InvalidCastException: Cannot convert value ... to type System.Int64`；<br/>2. **修复措施**：<br/>- 业务闭环范围前置：直接在浮点数分支对 `$d` 进行有限性与业务范围检验。首先验证 `[double]::IsNaN($d) -or [double]::IsInfinity($d)`，再检验非整数浮点 `[Math]::Floor($d) -ne $d`，随后直接基于业务闭环范围 `[0..135]` 进行比较（`if ($d -lt 0 -or $d -gt 135)`）；<br/>- 彻底消除宽整型溢出风险：对 `[double]9223372036854775808.0`，直接由 `$d -gt 135` 命中并受控返回 `WALL_CONSERVATION_INVALID`，绝不在范围检查前尝试强转 `[long]` 或 `[int]`；并在浮点与宽整型分支均配置可靠的 `try / catch` 兜底保护；<br/>- 回归测试套件扩充：在 `VerifyV5CompanionFixtures.Tests.ps1` 中对 `threeCasterWall` 与 `reversedWall` 均固化 `double-int64-edge` 测试用例（`[double]9223372036854775808.0`），实测退出码 1 且输出 `WALL_CONSERVATION_INVALID`；<br/>- 独立探针验收：调用方 `extended-probes.ps1` 包含的 22 项测试全部满足预期，原生退出码 0，`FAILURES=0`。 | `build/companion-caller-review-r4/extended-probes.ps1` 原样复跑：<br/>- 22 项测试全部通过（`FAILURES=0`，Exit 0）：<br/>- 4 项 HardLink 别名拦截：EXIT=2，UNCHANGED=True<br/>- 2 项 Valid 牌山：EXIT=0，OK=True<br/>- 16 项变异测试（含 `threeCasterWall` 与 `reversedWall` 的 `double-int64-edge`）：**EXIT=1，OK=True，均准确输出 `WALL_CONSERVATION_INVALID`** | **CLOSED** |

---

### 3.2 第 3 轮检视缺陷闭环映射 (D-R3-01 与 D-R3-02)

| 缺陷 ID | 严重级别 | 根因与修复措施 | 验证命令 / 复现证据 | 验收判定 |
|---|---|---|---|---|
| **[D-R3-01 / D-R2-01 遗留]** | **MAJOR** | **硬链接文件别名绕过 LogPath 保护与真实 fixture 覆写**：<br/>1. **根因**：上一轮仅针对 ReparsePoint（Junction/Symlink）做检测，但 NTFS 硬链接的 `FileAttributes.ReparsePoint` 属性为 `0`（`LINK_REPARSE_POINT=False`）。如果 LogPath 传入位于 `build\` 下但 hardlink 到真实黄金 fixture（如 `movement-v1.json`）的硬链接别名，`Test-PathHasReparsePoint` 无法发现，写日志会直接破坏真实黄金样本；<br/>2. **修复措施**：<br/>- 引入 Win32 `GetFileInformationByHandle` P/Invoke 接口（`V5CompanionFileInfoHelper` 类），获取文件底层物理信息（`BY_HANDLE_FILE_INFORMATION`）；<br/>- 在测试脚本最早期且在任何测试逻辑和日志写入前，强校验 `$resolvedLog` 的 `NumberOfLinks > 1`。若大于 1，立即判定为硬链接别名，报 `BLOCKED: LogPath '...' is a hard link alias (link count=...)` 并以退出码 2 退出；<br/>- 提取文件唯一物理标识（`VolumeSerialNumber:FileIndexHigh:FileIndexLow`），与受保护的关键文件（`tests/v5-golden/*`、`build.gradle`、`settings.gradle`、`tools/*.ps1`）对比；若标识匹配，立即报 `BLOCKED` 退出 2；<br/>- 测试套件新增负例 `neg-test-logpath-target-hardlink-fixture`，沙箱构建硬链接并实测退出 2 `BLOCKED`。 | `probes-r3.ps1` 探针复跑：<br/>- `CASE=selftest-logpath-hardlink-fixture`：<br/>`LINK_REPARSE_POINT=False`<br/>`NATIVE_EXIT=2`<br/>`BLOCKED: LogPath ... is a hard link alias (link count=2)`<br/>`FIXTURE_OVERWRITTEN=False` | **CLOSED** |
| **[D-R3-02 / D-R2-02]** | **MINOR** | **Test-WallTilesConservation 类型/异常安全性与证据口径**：<br/>1. **根因**：上一轮对 `threeCasterWall` 的类型检查通过 `[int]::TryParse([string]$item, [ref]$val)` 转换，导致数字字符串 `"0"`..`"135"` 被错误当成合法整数放行；且未处理 `[long]` 超宽整数或异常溢出（`[long]2147483648` 导致未捕获溢出异常）；此外报告统计口径未明确区分原生进程退出码（native exit 0/1/2）与进程内安全断言（in-process assertions）；<br/>2. **修复措施**：<br/>- 强类型重构：重写 `Test-WallTilesConservation`。明确拒绝 `[string]`（字符串形式数字）、`[bool]` 及 `$null`，报 `WALL_CONSERVATION_INVALID` 并退出 1；<br/>- 宽整型安全处理：对 `long`、`uint32`、`uint64` 等类型，首先安全转换为 `long`，并在类型转换至 `[int]` 前先执行数值区间 `[0..135]` 检查，彻底杜绝 `OverflowException` 或 `InvalidCastException`；<br/>- 浮点数类型拦截：对 `double`、`float`、`decimal` 显式检测是否为整型浮点，非整数浮点（如 `0.5`）或超宽浮点立即报 `WALL_CONSERVATION_INVALID`；<br/>- 测试套件与报告口径规范化：测试套件新增 3 项 shuffle-reversed 类型与越界变异测试；并在报告和摘要中显式细分统计：`native0=3, native1=59, native2=6, in_process_assertions=1, total=69`，明确区分原生进程退出码与进程内断言。 | `probes-r3.ps1` 探针复跑：<br/>- `CASE=structure-wall-numeric-string`：<br/>`NATIVE_EXIT=1`<br/>`WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains non-integer tile value '0' (type=String)`<br/>- `CASE=structure-wall-fractional-number`：<br/>`NATIVE_EXIT=1`<br/>`WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains non-integer fractional tile value 0.5`<br/>- `CASE=structure-wall-large-integer`：<br/>`NATIVE_EXIT=1`<br/>`WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains tile out of range [0..135]: 2147483648` | **CLOSED** |

---

### 3.3 第 2 轮检视缺陷闭环映射 (D-R2-01 与 D-R2-02)

| 缺陷 ID | 严重级别 | 根因与修复措施 | 验证命令 / 复现证据 | 验收判定 |
|---|---|---|---|---|
| **[D-R2-01]** | **MAJOR** | **自测 LogPath 保护与析构前缀绕过**：<br/>1. 测试脚本 `VerifyV5CompanionFixtures.Tests.ps1` 的 `$allowedBuild` 缺少末尾目录分隔符，导致 `build.gradle` 发生前缀误匹配；<br/>2. 仅做字符串前缀检查未检测 `ReparsePoint`，导致 `build/alias` Junction 指向 `tests/v5-golden` 时绕过白名单覆盖黄金样本；<br/>3. `Assert-WithinGuidRoot` 仅用 `StartsWith($fullRoot)`，导致根外同前缀兄弟目录（如 `guidRoot-sibling`）被误判在根内；<br/>4. 测试脚本在校验真实仓库未篡改后再执行最终写日志，导致通过别名覆写输入时仍产生假绿。<br/>**修复措施**：<br/>1. 早期边界防御：强制要求 `$LogPath` 严格位于规范化 `$canonicalRepo\build\` 或 `$canonicalRepo\artifacts\` 下（带目录分隔符，阻断 `build.gradle`）；<br/>2. ReparsePoint 检测：实现 `Test-PathHasReparsePoint` 向上递归遍历，发现任何祖先目录或目标包含 Junction / Symlink 即刻报 `BLOCKED` 退出 2；<br/>3. 前缀边界修正：`Assert-WithinGuidRoot` 使用 `$rootWithSlash`，严格拒绝前缀相似的外部兄弟目录；<br/>4. 执行时序调优：先写入最终日志文件，随后再强校验真实仓库所有黄金样本、校验器与构建文件的 SHA-256，彻底消除时序窗口。 | `probes-r2.ps1` 探针复跑：<br/>- `CASE=selftest-logpath-junction-alias`：**NATIVE_EXIT=2**<br/>`BLOCKED: LogPath ... contains reparse points or junction aliases`<br/>`FIXTURE_OVERWRITTEN=False`<br/>`cleanup-probe.ps1` 探针复跑：<br/>- `CASE=cleanup-prefix-boundary`：<br/>`OUTSIDE_GUID_ACCEPTED=False`<br/>- `CASE=cleanup-external-junction`：<br/>`EXTERNAL_NESTED_LINK_DELETED=False` | **CLOSED** |
| **[D-R2-02]** | **MINOR** | **物理守恒范围断言未严格校验 0..135**：原 `shuffle-reversed-v1` 守恒检查仅统计去重元素数量为 136，未验证元素为整数且在 `[0..135]` 物理闭环范围内（如 `136..271` 被放行）。<br/>**修复措施**：<br/>提取并实现 `Test-WallTilesConservation`，严格检查 136 张牌严格互斥覆盖 0..135 物理实体。 | `probes-r2.ps1` 探针复跑：<br/>- `CASE=structure-mode-out-of-range-wall`：**NATIVE_EXIT=1**<br/>`[STRUCTURE_TEST_MODE] FAIL: 1 failure(s) detected:`<br/>`- WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains tile out of range [0..135]: 136` | **CLOSED** |

---

### 3.4 第 1 轮检视缺陷闭环汇总 (D-R1-01 至 D-R1-06)

- **[D-R1-01] 全字节/UTF-8 校验被自动解码绕过**：已实现 BOM 严格检测（`Test-HasBom`）与非宽松 UTF-8 解码（`Get-StrictUtf8String`），固化双形态 raw SHA-256 与 byte profiles。四 fixture 的 LF、CRLF 两种接受形态均 4/4 native 0；四 fixture 均分别覆盖 UTF16/BOM/混合换行变异退出 1。
- **[D-R1-02] 公开 `-SkipHashCheck` 认证虚假 PASS**：已移除公开开关，替换为仅供内部结构分支测试的 `-InternalStructureTestMode`，明确打印 `[STRUCTURE_TEST_MODE]` 标记且绝不输出黄金认证 PASS 与静态哈希；增加 136 牌物理守恒严格校验。
- **[D-R1-03] 只读 validator 允许 `-LogPath` 覆盖仓库 fixture**：validator 内部完全移除文件写操作与 `-LogPath` 参数，全部结果写 stdout/stderr，成为 100% 纯只读工具。
- **[D-R1-04] RepoRoot 边界仅词法 StartsWith，目录联接逃逸**：实现递归 ReparsePoint 属性检测与物理绝对路径强校验，拒绝任何 Junction / Symlink 逃逸。
- **[D-R1-05] 请求 Git 时缺 Git 记为 FAIL 1**：前置预检 `git` 命令，环境缺失明确返回 `BLOCKED` 退出 2。
- **[D-R1-06] expectedFiles 与报告证据不一致**：报告迁移至 `V5-COMPANION-FIXTURE-VERIFIER.md`，diff 恰好 3 个授权文件，全量 SHA-256 真实复核一致。

---

## 4. 四个伴随 Fixture 的固化 Pin 表与来源溯源

依据各原交付报告与调用者独立验收文档（`CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md`、`CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md`、`CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md`、`CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md`），逐一核实并固化真实物理字节、Git LF 与 Windows CRLF 原始 SHA-256、Git Blob 与来源提交：

| 伴随样本 ID | 相对路径 | 标识字段与预期值 | Git LF 规范形态 (Bytes / SHA-256) | Windows CRLF 检出形态 (Bytes / SHA-256) | 冻结提交 (FreezeCommit) / Git Blob | 嵌入来源提交 / 字段名 | 真实用例结构与标识字段 | 独立验收文档依据 |
|---|---|---|---|---|---|---|---|---|
| **movement-v1** | `tests/v5-golden/movement-v1.json` | `schema = "v5-movement-golden-v1"` | 1,278,595 B<br/>`C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13` | 1,331,161 B<br/>`5927F44A650DB963813C352B9786147A5953772862CA33722FDD9CE3E17FA3C5` | `fdd0287b415db3438b23694bb35e4b72d76d50aa`<br/>`25331005f30edfb2870cba8ee4a1b582400f346b` | `v4Commit = "d6bc69fa930d005b355a1273ec732fbdf82bbe2f"` | 8 cases (ID 字段 `caseId`，如 `MV-G004-DRAW-DISCARD`) | `CODEX-V5-P01-MOVEMENT-GOLDEN-ACCEPTANCE.md` |
| **shuffle-reversed-v1** | `tests/v5-golden/shuffle-reversed-v1.json` | `schema = "v5-shuffle-reversed-golden-v1"` | 75,847 B<br/>`5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619` | 77,359 B<br/>`81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902` | `8734494936aad0d451511156a454b989d508468b`<br/>`16ae21dafdf487707bf8f4064a879de22779654f` | `v4BaseCommit = "03084f08af6e2ddefa2ebc6e094de5dac2474fd4"` | 32 cases (ID 字段 `seed`，0..31)，各含 `threeCasterWall` 与 `reversedWall` (严格 0..135 互斥整数) | `CODEX-V5-P01-SHUFFLE-REVERSED-ACCEPTANCE.md` |
| **visibility-v1** | `tests/v5-golden/visibility-v1.json` | `schema = "v5-visibility-golden-v1"` | 141,007 B<br/>`30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E` | 147,185 B<br/>`6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94` | `4086bf3db898d65b9e2119df3247a9beb89ba121`<br/>`eddcc00035978be64bca27efc27ec352ce686bf5` | `v4SourceCommit = "e8a9e9adc401becef6d05d4cf5de972e998df976"` | 4 cases (ID 字段 `id`，如 `VIS-G057A-SNAPSHOT-COPY`) | `CODEX-V5-P01-VISIBILITY-GOLDEN-ACCEPTANCE.md` |
| **ai-discard-flags-v1** | `tests/v5-golden/ai-discard-flags-v1.json` | `fixture = "ai-discard-flags-v1"` | 14,480 B<br/>`448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8` | 14,984 B<br/>`DB1A872F324A8FB994020FB90700E5F64E4E90ECAA63E1CB3FC3A660CAE10AEE` | `daa834a45fb651b786fc4f13a3d1edfc4d8d4a39`<br/>`ef329f5562b47e6e1f9304f247d7a3bb36cf7125` | `provenance.generatedAtHead = "85134e9ce4fd67556f6d2f355249c14f4d3b42d4"` | 3 cases (ID 字段 `caseId`，如 `riichi-river`)，根含 `flagSemantics` | `CODEX-V5-N3-AI-DISCARD-FLAGS-ACCEPTANCE.md` |

---

## 5. 实测验证命令与数值结果

### 5.1 校验器直接运行验证
```powershell
# 1. 默认全量基线校验
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1
# 原生 Exit Code: 0
# PASS: 4/4 companion golden fixtures verified successfully:
#   - movement-v1.json (schema: v5-movement-golden-v1, 8 cases, SHA-256 LF: C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13)
#   - shuffle-reversed-v1.json (schema: v5-shuffle-reversed-golden-v1, 32 cases, SHA-256 LF: 5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619)
#   - visibility-v1.json (schema: v5-visibility-golden-v1, 4 cases, SHA-256 LF: 30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E)
#   - ai-discard-flags-v1.json (fixture: ai-discard-flags-v1, 3 cases, SHA-256 LF: 448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8)

# 2. 包含 Git 历史冻结 Commit Blob 验证
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins
# 原生 Exit Code: 0
# PASS: All 4 historical freeze commit git blobs verified equal to pinned hashes.
```

### 5.2 调用方独立复现探针复跑验证

#### 1) 第 4 轮扩展探针复跑 (`build/companion-caller-review-r4/extended-probes.ps1`)
- **执行结果**：`FAILURES=0`，原生 Exit Code: `0`
- **用例明细**：
  - `CASE=hardlink-tests/v5-golden/movement-v1.json EXIT=2 UNCHANGED=True`
  - `CASE=hardlink-tools/verify-v5-companion-fixtures.ps1 EXIT=2 UNCHANGED=True`
  - `CASE=hardlink-tools/tests/VerifyV5CompanionFixtures.Tests.ps1 EXIT=2 UNCHANGED=True`
  - `CASE=hardlink-build.gradle EXIT=2 UNCHANGED=True`
  - `CASE=threeCasterWall-valid EXIT=0 OK=True`
  - `CASE=threeCasterWall-string EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-null EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-bool EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-fractional EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-large-int EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-negative EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-duplicate EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=threeCasterWall-double-int64-edge EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-valid EXIT=0 OK=True`
  - `CASE=reversedWall-string EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-null EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-bool EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-fractional EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-large-int EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-negative EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-duplicate EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）
  - `CASE=reversedWall-double-int64-edge EXIT=1 OK=True`（输出 `WALL_CONSERVATION_INVALID`）

#### 2) 第 3 轮探针复跑 (`build/companion-caller-review-r3/probes-r3.ps1`)
- `CASE=selftest-logpath-hardlink-fixture`：**NATIVE_EXIT=2**，输出 `BLOCKED: LogPath '...' is a hard link alias (link count=2)`，`LINK_REPARSE_POINT=False`，`FIXTURE_OVERWRITTEN=False`
- `CASE=structure-wall-numeric-string`：**NATIVE_EXIT=1**，输出 `WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains non-integer tile value '0' (type=String)`
- `CASE=structure-wall-fractional-number`：**NATIVE_EXIT=1**，输出 `WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains non-integer fractional tile value 0.5`
- `CASE=structure-wall-large-integer`：**NATIVE_EXIT=1**，输出 `WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains tile out of range [0..135]: 2147483648`

#### 3) 第 2 轮探针复跑 (`build/companion-caller-review-r2/probes-r2.ps1`)
- `CASE=selftest-logpath-junction-alias`：**NATIVE_EXIT=2**，输出 `BLOCKED: LogPath ... contains reparse points or junction aliases`，`FIXTURE_OVERWRITTEN=False`
- `CASE=structure-mode-out-of-range-wall`：**NATIVE_EXIT=1**，输出 `WALL_CONSERVATION_INVALID: shuffle-reversed-v1: case '0' threeCasterWall contains tile out of range [0..135]: 136`

#### 4) 清理断言边界探针复跑 (`build/companion-caller-review-r2/cleanup-probe.ps1`)
- `CASE=cleanup-prefix-boundary`：`OUTSIDE_GUID_ACCEPTED=False`
- `CASE=cleanup-external-junction`：`EXTERNAL_NESTED_LINK_DELETED=False`

#### 5) 第 1 轮探针复跑 (`build/companion-caller-review/probes.ps1`)
- `utf16-default`：**NATIVE_EXIT=1**（拦截 BOM 与无效 UTF-8，检测到哈希不符）
- `mixed-eol-default`：**NATIVE_EXIT=1**（检测到原始字节与 SHA 不符）
- `duplicate-tile-skiphash`：**NATIVE_EXIT=1**（检测到 HASH_MISMATCH 与 WALL_CONSERVATION_INVALID）
- `junction-outside-reporoot`：**NATIVE_EXIT=1**（检测到 PATH_ESCAPE_DETECTED，拒绝重解析点）
- `log-overwrites-fixture`：**NATIVE_EXIT=0**，`FIXTURE_OVERWRITTEN=False`（校验器完全只读）
- `missing-git-dependency`：**NATIVE_EXIT=2**（环境缺 git 明确以 BLOCKED 2 退出）

### 5.3 回归测试套件全量执行 (`tools/tests/VerifyV5CompanionFixtures.Tests.ps1`)
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5CompanionFixtures.Tests.ps1 -LogPath build/companion-fixtures/selftest.log
```
- **原生退出码**：`0`
- **用例总数**：`71`
- **正例基线 (Positives)**：`3`
  - `positive-all-lf-profile`（全 4 fixture LF 纯净形态沙箱验证，Exit 0）
  - `positive-all-crlf-profile`（全 4 fixture CRLF 纯净形态沙箱验证，Exit 0）
  - `positive-real-repo-git-pins`（真实仓库 Git Pin 校验，Exit 0）
- **负例变异 (Negatives)**：`68`
  - **原生 Exit 1**：`61` 项（哈希单字节漂移、编码错误、物理守恒违规、非整型牌值、宽整型溢出、双精度 Int64 上界变异、结构缺失等）
  - **原生 Exit 2 (BLOCKED)**：`6` 项：
    - `neg-missing-git-dependency`（缺少 git 命令依赖）
    - `neg-test-logpath-target-protected-fixture`（LogPath 指向真实黄金 fixture）
    - `neg-test-logpath-target-build-gradle`（LogPath 指向构建脚本 build.gradle）
    - `neg-test-logpath-target-junction-alias`（LogPath 经由 Junction 别名指向受保护 fixture）
    - `neg-test-logpath-target-hardlink-fixture`（LogPath 为硬链接别名指向真实黄金 fixture）
    - `neg-test-logpath-outside-repo`（LogPath 越出仓库 build/artifacts 目录）
  - **进程内断言 (In-Process Assertions)**：`1` 项：
    - `neg-cleanup-prefix-boundary`（验证 `Assert-WithinGuidRoot` 严格拒绝外部同前缀兄弟目录，抛出安全违规异常）
- **失败用例 (Failures)**：`0`
- **执行口径总结**：`total=71 (native0=3, native1=61, native2=6, in_process_assertions=1), failures=0`
- **真实仓库未篡改确认**：`real_fixtures_unchanged=true`（且位于写日志之后执行，零时序漏洞）
- **自测原始日志**：`build/companion-fixtures/selftest.log`
- **日志 SHA-256**：`2D04D243FAF953DFBA79682F7DD41DF31A8F3896D94471A89331ED379EF114FA`

### 5.4 既有六基准目录校验器回归 (`tools/verify-v5-golden-catalog.ps1`)
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
```
- **原生退出码**：`0`
- **输出摘要**：`PASS catalog=.../tests/v5-golden/catalog-v1.json sha256=236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D fixtures=6 requirements=73 cases=10389 random=10000 facts=45 coverage=full:17/partial:21/none:32/v5-only:3`

---

## 6. 产物与测试证据 SHA-256 汇总表

> [!NOTE]
> 遵循独立复核规范，产物表仅记录实际交付的代码产物与外部自测日志，不写入本报告自身的自指哈希。

| 产物 / 文件路径 | 状态 | 字节数 (Bytes) | SHA-256 完整哈希 |
|---|---|---|---|
| [`tools/verify-v5-companion-fixtures.ps1`](file:///D:/majiang-wt/companion-verifier-anti-20261026/tools/verify-v5-companion-fixtures.ps1) | 完善交付 | 29,508 | `A1E80B5C56D56828DEE54F8085AA94BC9A9095AB15A3FE9B47083D2B31A4D047` |
| [`tools/tests/VerifyV5CompanionFixtures.Tests.ps1`](file:///D:/majiang-wt/companion-verifier-anti-20261026/tools/tests/VerifyV5CompanionFixtures.Tests.ps1) | 完善交付 | 50,791 | `734DC480965BDF95C8C5D599E677B290B29C98CFC25DF4B4CCC85EC43BABBBD8` |
| `build/companion-fixtures/selftest.log` | 本地自测日志 (ignored) | 54,678 | `2D04D243FAF953DFBA79682F7DD41DF31A8F3896D94471A89331ED379EF114FA` |

---

## 7. 未覆盖项、依赖与状态声明

1. **统一 CI 调度未接入声明**：本校验器为 standalone 工具，按授权约束严禁修改 `tools/verify-v5.ps1`，在全系统自动化流水线中标记为 **`NOT_RUN`**。
2. **业务逻辑与全字节防御边界界定**：本工具负责静态全字节防篡改、物理守恒律（136 牌实体守恒，精确包含 0..135 互斥整数，防注入防非整数/超宽溢出）与来源 Pin 锚定，不执行 C# 运行时代码，不宣称替代 C# 业务单元测试对规则、摸打与立直时序的动态业务断言；两者互为补充。
3. **环境与运行时缺口**：PG、WSS、Unity Editor、IL2CPP 构建、Android 运行时及远端 CI 均未在本次执行，如实记录为 **`NOT_RUN`**。
