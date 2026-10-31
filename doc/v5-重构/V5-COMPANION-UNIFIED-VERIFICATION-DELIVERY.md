# V5 伴随黄金校验器接入统一入口交付

日期：2026-10-26（Asia/Shanghai）

状态：本地定向 PASS；真实 PostgreSQL 统一 CI / 远端 CI `NOT_RUN`（由 caller 集成后执行）。

范围：G1 APPROVED 后登记的维护项“伴随黄金 verifier 接入统一 CI 入口”。不是重新审查 G1，不重开黄金 census，不改变 V4 生产基线。

---

## 1. 基线与边界

- G1 已 APPROVED；PR19 已 MERGED。本工作树从已 fetch 的 `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 新建。
- 工作树：`D:/majiang-wt/companion-ci-20261026`；分支：`wt/v5-companion-ci-20261026`。
- 本任务只做统一入口接入与定向验证；receiver 未 push、未创建/合并 PR、未部署、未转派。
- 四伴随样本与六基准目录分母严格独立：本次不修改 `tests/v5-golden/catalog-v1.json`、六基准目录、181 classified、pin、fixture、分母或原 validator。
- 生产仍是 V4；G2–G5 未批准；未创建 `complete.flag`。Android FAIL、iOS BLOCKED、device/跨物理网 `NOT_RUN` 的原口径不变。

---

## 2. 实际修改文件（范围审计）

| 状态 | 文件 | 说明 |
|---|---|---|
| 修改 | `tools/verify-v5.ps1` | 新增强制 companion gate、完整 CLI 子进程调用、原始命令/exit/日志/meta 证据、四项身份与整体 exit 聚合。 |
| 新增 | `tools/tests/VerifyV5CompanionIntegration.Tests.ps1` | 从真实统一入口证明正例、缺脚本、缺/0 样本、字节篡改、语义篡改、BLOCKED、timeout、以及 .NET PASS 不掩盖 companion FAIL。 |
| 新增 | `doc/v5-重构/V5-COMPANION-UNIFIED-VERIFICATION-DELIVERY.md` | 本交付记录。 |

**未修改**（逐项确认）：

- `tools/verify-v5-companion-fixtures.ps1`：原接受 validator，SHA-256 `50D3B32ED9F06F99463AD160DD893269F4DA9D789F84C6E71D57977B33FEC51F`，未改一行。
- `tests/v5-golden/*.json`、pin 表、`catalog-v1.json`、六基准目录、分母：未修改。
- `tools/tests/VerifyV5Diagnostics.Tests.ps1`：**未修改**；新阶段与其 throwaway manifest/故障日志收集兼容，最终实测 `88/88` 仍通过。
- `.github/workflows/*`、`tools/ci/verify-v5-postgres.ps1`、`dotnet/Mahjong.sln`、项目文件、Unity、机器环境：未修改。
- 本次净变更即上表 3 个文件；没有额外文件。

---

## 3. 统一入口实际行为

### 3.1 调用链

```text
.github/workflows/v5-verify.yml
  -> tools/ci/verify-v5-postgres.ps1（未改，原样传入参数并传播子进程 exit）
    -> tools/verify-v5.ps1（本次修改）
      -> tools/verify-v5-companion-fixtures.ps1（原样、完整 CLI）
```

CI workflow 和 PG wrapper 未改；它们原本就调用 `tools/verify-v5.ps1`，因此新阶段对本地默认入口、旧 wrapper 和新 wrapper 自然生效，不依赖兄弟未提交代码。

### 3.2 强制阶段

在 .NET 项目行与 Unity plugin copy 之后、summary 之前，统一入口**无条件**执行：

```powershell
<当前 PowerShell 宿主> -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools\verify-v5-companion-fixtures.ps1 `
  -RepoRoot <repo> -FixtureDir <repo>\tests\v5-golden
```

- **不**使用 `-InternalStructureTestMode`。
- **不**默认使用 `-VerifyGitPins`；历史 freeze blob 检查在统一入口中明确记为 `NOT_RUN`，不冒充已执行。
- 默认完整字节 hash、严格 UTF-8/JSON、schema/fixture 身份、嵌入 provenance、案例身份与结构，由原 standalone verifier 执行；统一入口不重写弱校验。
- `-CompanionVerifierPath`、`-CompanionFixtureDir`、`-CompanionTimeoutSeconds` 仅用于隔离自测/有界进程控制，不是跳过开关；默认生产路径始终要求完整 `4/4` 认证。
- 子进程隐藏窗口、`CreateNoWindow`、默认 600 秒有界超时；超时/启动失败/无 exit code 一律按 FAIL，绝不当作 0。

### 3.3 结果与整体 exit

| companion 情况 | companion gate | OVERALL | 统一入口 exit |
|---|---|---|---|
| exit 0 且输出认证 4/4 | `PASS` | `PASS`（其他行也 PASS） | `0` |
| exit 1 / 缺脚本 / 缺 fixture / 0 样本 / 字节或语义篡改 | `FAIL` | `FAIL` | `1` |
| verifier 报告 BLOCKED，native exit 2 | `BLOCKED` | `BLOCKED` | `2` |
| 崩溃/超时/无 native exit | `FAIL` | `FAIL` | `1` |
| exit 0 但输出没有完整 4/4 认证 | `FAIL` | `FAIL` | `1` |

`$anyFail` 包含 companion FAIL；summary 的 `OVERALL` 行显式带 `companion gate: ...`。因此前序 .NET 项目行 PASS **不能**掩盖 companion gate 失败。集成 S7 用真实 `Probe.Tests` PASS（1 total / 0 failed）加篡改 fixture 实测：项目行 PASS、`OVERALL: FAIL`、入口 exit 1。

### 3.4 每次保存的证据

`<LogDir>` 下：

- `companion-fixtures.log`：统一入口记录的命令、cwd、timeout、原生 exit、timed out/异常，以及 verifier 原始 stdout/stderr 分段。
- `companion-fixtures.log.stdout.log`、`.stderr.log`：原始子进程流。
- `companion-fixtures.meta.txt`：verifier 路径与 SHA-256、命令、raw native exit、raw log 路径与 SHA-256、四项 fixture 的 `schema/fixture` 身份、case 数、SHA-256，`git blob pins: NOT_RUN`，以及 PASS/FAIL/BLOCKED。
- `summary.log`：`companion gate : PASS/FAIL/BLOCKED ...`、原始命令、raw exit、raw log SHA、四项身份、`git pins: NOT_RUN`、`OVERALL`。

---

## 4. 四项伴随样本身份（本轮实际校验）

本轮工作树为 `w/crlf` 检出；原 verifier 同时接受既定 LF/CRLF profile。下列 PASS 行按既有格式打印 LF SHA-256，实际校验的是当前 CRLF 字节（字节数见右列）。四项仍与六基准分母独立。

| Fixture | 身份字段/值 | Cases | 接受 LF SHA-256 | 本轮 CRLF 字节 / SHA-256 |
|---|---|---:|---|---|
| `movement-v1.json` | `schema=v5-movement-golden-v1` | 8 | `C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13` | 1,331,161 / `5927F44A650DB963813C352B9786147A5953772862CA33722FDD9CE3E17FA3C5` |
| `shuffle-reversed-v1.json` | `schema=v5-shuffle-reversed-golden-v1` | 32 | `5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619` | 77,359 / `81D21F0A0C0C6FDA6EFFB029364D5BC8F4EE30C84F1431246FDD9DB5309F5902` |
| `visibility-v1.json` | `schema=v5-visibility-golden-v1` | 4 | `30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E` | 147,185 / `6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94` |
| `ai-discard-flags-v1.json` | `fixture=ai-discard-flags-v1` | 3 | `448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8` | 14,984 / `DB1A872F324A8FB994020FB90700E5F64E4E90ECAA63E1CB3FC3A660CAE10AEE` |

---

## 5. 实际命令、native exit、计数与日志

证据根：`build/companion-ci-evidence-20261026/`（ignored）。保留的集成 workspace：`build/companion-integration/20261026-204234-e2f916b0/`（ignored）。

| # | 命令（原样） | native exit | 实际计数/结果 | 日志 |
|---|---|---:|---|---|
| 1 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1` | 0 | `PASS: 4/4`，四项身份与 SHA 见上 | `companion-direct-default.log` |
| 2 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins` | 0 | `PASS: 4/4` + `All 4 historical freeze commit git blobs verified equal to pinned hashes.` | `companion-direct-gitpins.log` |
| 3 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5CompanionIntegration.Tests.ps1 -DotnetPath D:\Unity\dotnet\dotnet.exe -KeepWorkspace` | 0 | `57/57` checks PASS（S0–S8） | `integration-selftest-final2.log` |
| 4 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5Diagnostics.Tests.ps1 -DotnetPath D:\Unity\dotnet\dotnet.exe` | 0 | `VerifyV5Diagnostics: PASS (88/88 checks)`；原断言未改 | `diagnostics-selftest-final.log` |
| 5 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5CompanionFixtures.Tests.ps1` | 0 | `total=71 positives=3 negatives=68`；`native0=3 native1=61 native2=6 in_process_assertions=1 failures=0`；`real_fixtures_unchanged=true` | `companion-fixtures-tests-final.log` |

> 第 2 行是**直接运行 standalone verifier** 的补充证据，不是统一入口默认执行的内容。统一入口默认明确 `git pins: NOT_RUN`；不得据此把统一入口说成已执行历史 blob 校验。

### 5.1 集成场景矩阵（S0–S8）

| 场景 | 证明点 | 统一入口 exit | companion gate / OVERALL | 关键原始证据 |
|---|---|---:|---|---|
| S0 正例 | 默认路径真实执行原 verifier，四项身份/SHA 完整 | 0 | PASS / PASS | `s0-positive/logs/summary.log`、`companion-fixtures.meta.txt` |
| S1 缺 verifier 脚本 | 缺脚本不得假绿 | 1 | FAIL / FAIL | raw native exit `-196608`（Windows PowerShell `-File` 缺文件的原生码，原样记录） |
| S2 空 fixture 目录（0 样本） | 0 样本必须失败 | 1 | FAIL / FAIL | 4 条 `FIXTURE_MISSING` |
| S3 单字节篡改 | raw hash 门先拒绝 | 1 | FAIL / FAIL | `HASH_MISMATCH`（并伴随 UTF-8 解码失败） |
| S4 schema 语义篡改（同长度） | 语义篡改失败；hash 门先拒绝是正确口径 | 1 | FAIL / FAIL | `HASH_MISMATCH` + `SCHEMA_MISMATCH`；深层结构仍由原 negative suite 覆盖 |
| S5 BLOCKED | native exit 2 传播为 BLOCKED/2 | 2 | BLOCKED / BLOCKED | meta `raw native exit: 2` |
| S6 悬挂 verifier | 有界 timeout，不得伪造 PASS | 1 | FAIL / FAIL | meta `timed out: True`；raw 无 `PASS: 4/4` |
| S7 .NET PASS 不掩盖 | 真实 `Probe.Tests` PASS 行 + 篡改 fixture | 1 | FAIL / FAIL | `Probe.Tests ... 1 0 0 PASS`；`OVERALL: FAIL ... companion gate: FAIL` |
| S8 原 diagnostics 自测仍在 | 未被替换/删除 | — | — | 第 4 行单独实跑 88/88 |

S7 的 `Probe.Tests` 是隔离 scratch 内最小真实 xunit.v3/.NET 8 测试工程，由统一入口 fresh build + direct runner 执行；没有盲跑 PG、Unity 或全解决方案。

---

## 6. 证据 SHA-256

| 证据文件 | 字节 | SHA-256 |
|---|---:|---|
| `companion-direct-default.log` | 1,742 | `4CBFF73F31BE6E7C709C8061EA6D023203FF0181B67DCDC6B11B76388526B353` |
| `companion-direct-gitpins.log` | 1,890 | `93A2F4D30B63C542D7194788384E43C70D23D3D33B062F423FD2905179876A82` |
| `integration-selftest-final2.log` | 7,756 | `3520FB3C4BE2CDA4D3731332072FB1736BAC016D2D5E68DBAFA195EF36610ADC` |
| `diagnostics-selftest-final.log` | 13,130 | `DCAF363ADACDA17DE2F9FEA36013A3A32FA82B58EAF8D948A71ED450ECF6B0F8` |
| `companion-fixtures-tests-final.log` | 458 | `294F9572AA41ADDF4C95767F841B1668D442498B290D5FD2F4F81941784143BA` |
| `s0-positive/logs/summary.log` | 2,537 | `372A847082C9E28F6970B275D7B411ED7E01BDD3AE8ECC4EBE69B134ED616D8F` |
| `s0-positive/logs/companion-fixtures.log` | 1,386 | `98A65D5C62CA29A061945AC4DD7682DA682732902EF8BD0AB925320F76C852C1` |
| `s0-positive/logs/companion-fixtures.meta.txt` | 2,253 | `BFCFE77C168C5E82A4505F6458D19A01FF2CB8D4CFA79C5662300068A394F5C8` |
| `s7-no-masking/logs/summary.log` | 2,598 | `9D42B6FC732C3A5F6796F4F6F5FE1DE5503B4609AC2A1D0E5F82F6D42C3F8DAE` |
| `s7-no-masking/logs/companion-fixtures.log` | 1,469 | `EE190BF7AE8CC795AA30E3462E28CC6A2AF2E41948B0960997C3E21D6A2965BD` |
| `tools/verify-v5.ps1` | 59,046 | `2CF58DCAF0F2D098F438DBFB8832582F8ADD66A41FB4523118548E1853235B04` |
| `tools/tests/VerifyV5CompanionIntegration.Tests.ps1` | 26,380 | `3C34ED22B6AC04D5555D08CF098F31A1460E94A45324CCF8290965300EB69E09` |
| `tools/verify-v5-companion-fixtures.ps1`（未改） | 30,157 | `50D3B32ED9F06F99463AD160DD893269F4DA9D789F84C6E71D57977B33FEC51F` |
| `tools/tests/VerifyV5Diagnostics.Tests.ps1`（未改） | 43,592 | `C53342B18CF91048180A2D811C0855891DD13721881284A330A3FD89EE041052` |

---

## 7. 未执行 / 不得冒充的项

1. **真实 PostgreSQL 统一 CI**：本轮 `NOT_RUN`。由 caller 在 PR20 串行集成后执行；本轮没有运行 PG wrapper，也没有远端 CI 结果，不能写成远端 PASS。
2. **统一入口历史 Git blob 校验**：默认 `NOT_RUN`。仅第 5 节第 2 行 standalone 直接运行过 `-VerifyGitPins` 并 exit 0；这不等于统一入口执行了历史 blob 校验。
3. **完整生产 .NET 全量测试 / 全解决方案**：本轮未跑。使用隔离空 solution、最小真实 xunit.v3 probe 和 diagnostics throwaway 工程做定向验证；没有用旧 DLL、skip、0 test 或旧产物假绿。
4. **Unity/IL2CPP、Android、iOS、真机、跨物理网**：未在本轮执行；原 Android FAIL、iOS BLOCKED、device `NOT_RUN` 口径不变。
5. **181 classified**：仍未覆盖，不因本维护项升级为 bound/full；四伴随分母独立，不并入六基准。
6. **V4 生产状态、G2–G5 门槛**：不变；未创建 `complete.flag`。

---

## 8. Git/检查记录

- 基线：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（`origin/v5`）。
- 分支：`wt/v5-companion-ci-20261026`。
- `git diff --check`：exit 0。
- `git status --porcelain`：提交后 clean（仅本交付的 3 个文件；证据均在 ignored `build/` 下）。
- 本记录不把本地定向 PASS 写成 PG/远端 CI 已通过；后续 PR20 caller 负责。
