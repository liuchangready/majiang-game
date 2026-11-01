# V5 Diagnostics 自检恢复交付（R2，2026-11-02）

状态：**DIAG-R1-01 的 A/B/C 已在本轮同一脚本内批量修复并完成本地实测，待调用方独立复核**。本交付不代表合并 `v5`、不代表 V4/V5 生产验收、不代表 G2–G5 批准。

## 0. 身份、范围与复核输入

- 工作树：`E:/majiang-review/diagnostics-selftest-ds-20261102-0113`
- 分支：`wt/v5-diagnostics-selftest-ds-20261102-0113`
- accepted base：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- 冻结来源：`28a000349e604a9214c8225c15d102445cdb2cb6`
- 已接受候选代码：`ccdfac6b16d116a3b336b6b63de51103f66bb7bf`
- 修复后脚本 SHA-256：`B3369147080210C1D4812D392136E976199BA4E52723CA82E14D353408172897`
- R1 审查的交付 commit：`193c328961b3530342f27daffe979dd46da5658f`
- R2 parent：`193c3289`；R2 交付 commit 见最终返回与 manifest 的 `deliveryCommit` 行。
- 授权文件仍只有两个：
  1. `tools/tests/VerifyV5Diagnostics.Tests.ps1`
  2. `doc/v5-重构/V5-CI-DIAGNOSTICS-SELFTEST-DELIVERY-20261102.md`
- 未修改 `tools/verify-v5.ps1`、生产 helper、workflow、业务/.NET、包版本、AGENTS 或主仓文档。未 push/改 PR/approve/record MR/merge/deploy/scheduler。
- Caller R1 输入（只读）：
  - `E:/majiang-review/caller-diagnostics-r1-43c9ab26/build/caller-review/REVIEW.txt`
    SHA-256 `01CF15997220DEBE12393B91F2C4FCCF9B4DF38E2912A785268F81D57FE2887F`
  - 同目录 `review-final-manifest.sha256.json`
    SHA-256 `150A822F738A3969EC0DEEAF115B0802491DCC7E4A2F4585AB0F38F7A5171471`
- 环境：Windows PowerShell `5.1.19041.6456`；`D:/Unity/dotnet/dotnet.exe` SDK/runtime `8.0.426`。TEMP/TMP/DOTNET_CLI_HOME、构建、日志、证据与 NuGet 测试缓存均在本树独占 E 盘 `build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/`，D 盘未写入。

## 1. 对 Caller R1 结论的承接

Caller R1 已认可原 D-DIAG-01/02/03/04 的格式、路径、S7 中止与零测试文案修复，并实际复核 R1 commit 的完整 Diagnostics **125/125、native 0**，四个定向矩阵 **61/61、51/51、19/19、24/24** 各 native 0；同时指出新增 reader/断言组比 accepted 合同弱，指定副本仍被误收。R2 不重开生产 helper，不声称真实 125 输出含损坏 XML，只修同一测试脚本内的证据断言。

本轮唯一缺陷组：

| ID | 级别 | 位置 | 复现 | 实际 vs 期望 |
|---|---|---|---|---|
| DIAG-R1-01A | MAJOR | `Get-DotnetTestEvidence` / `Test-DotnetTestEvidence` / `Assert-DotnetTestEvidence` 的 `NativeExit` 参数与场景调用 | caller `probe-assertions.ps1`：`pass-valid` 接受后仅把 `NativeExit` 设为 `$null` | 旧实现 `[int]$NativeExit` 把 null 变 0，`pass-native-null` accepted=true；期望 unknown/null 在任何比较前拒绝，且场景传实际观测 exit 而非预期常量 |
| DIAG-R1-01B | MAJOR | 新 XML reader 约 439–644、S1/S2/S6/S7/S9/S10 调用点、TOOL 矩阵 | caller `probe-assertions.ps1` 把另一 Pass 节点改 Fail；caller `probe-xml-contract.ps1` 的 duplicate-ids/缺属性/错误 assembly/4.0.20/schema999 | 旧 reader 只看 selected case、aggregate sum 与 family prefix；上述副本 accepted=true。期望覆盖全部 case 状态计数、必需属性、ID、assembly、锁定 framework/schema/计数一致性 |
| DIAG-R1-01C | MAJOR | 新 raw 断言、S2/S6/S9 的 stale 扫描范围 | 两个 caller probe 复制真实 raw，前置 `VERIFY-V5-STALE-OLD-RUN` 并把 mtime 设为 2 天前 | 旧实现 fail-stale-raw / PASS stale-raw accepted=true；S2/S6/S9 只扫旧 `test-results` 子树，未覆盖 `dotnetTest-xml/Probe.Tests` 的新 accepted raw。期望旧 marker/旧时间被拒，当前 raw 被证明 |

统一修复要求：同一套共用函数必须同时服务真实 S1–S11 和新负例；不得只在 fixture 外再包拦截；不得 skip、空测试、放宽断言或刷新摘要。

## 2. A：原生结果保持实际观察

- `Test-DotnetTestEvidence` 与 `Assert-DotnetTestEvidence` 的 `NativeExit` 改为 `[object]`；`Test-NativeExitObservation` 不再作为独立依赖（已内联），避免外部 AST 探针漏抽 helper。
- 在任何 row/XML/raw/meta 比较前：
  - `$null` 直接拒绝：`native exit is unknown (null); never treated as 0`；
  - `Boolean`、`Double`/fraction、字符串等非整数观测拒绝；
  - 仅接受整数类型族，再转 `[long]` 与 meta/row/XML 对照。
- 场景调用全部改为实际观测变量：S1 `$exit1`、S2 `$exit2`、S5 `$exit5`、S6 `$exit6`、S7 `$exit7`、S9 `$exit9`、S10 `$exit10`；不再以常量 0/1 补足。
- 保留原有 `exit -ne 0` 实际断言和预期非零负例；PASS 要求实际 0，FAIL/zero 要求实际非零。

验收证据（本轮 fresh TOOL 目录，caller 原逻辑适配后实际 AST 抽取新函数）：

| Probe case | suppliedNativeExit | accepted |
|---|---|---:|
| pass-valid | 0 | true |
| fail-valid | 1 | true |
| pass-native-null | null | false |
| fail-two-failed-nodes | 1 | false |
| fail-stale-raw | 1 | false |

## 3. B：只读复用 accepted 结构合同

- 启动时从**未修改**的 `tools/verify-v5.ps1` 用 AST 提取 accepted `Read-DirectResultXml`，作为 XML 结构权威；`Get-DotnetTestEvidence` 在外部只读探针只抽取共享函数时也会自行查找并提取该 reader（仍只读、无复制 parser）。
- accepted reader 负责：唯一 assembly、必需 `total/passed/failed/skipped/errors/not-run`、ID 唯一、test `result` 合法、collection 必需字段及逐 collection 聚合、node/collection/assembly 计数一致、assembly identity、xUnit v3 family。
- Diagnostics-specific 追加检查：
  - framework 必须精确为 `xUnit.net v3 4.0.2`，仅允许真实 `+build` 元数据；
  - `schema-version` 必须为 `3`；
  - assembly 必须是 rooted 的 `Probe.Tests` Release 输出（真实场景按 `$probeDir` 精确路径，外部探针按同形路径校验）；
  - 显式复核全部 test 节点 `Fail/Pass/Skip/NotRun` 计数与 accepted aggregate 一致；
  - 失败场景要求 exactly one 预期 case 为 `Fail`、failure message 含本次断言文本，raw 同时含 case/断言；
  - PASS 场景要求所有节点 Pass、raw 为 `Passed!`，native 实际 0。
- TOOL baseline XML 改为合法 native-shaped：真实 Probe.Tests Release assembly path、schema 3、xUnit 4.0.2+build、唯一 test ID、必需 collection/assembly 属性；不再用 `assembly X`、无 ID、缺 collection 字段的弱基线证明“接受”。

验收证据（adapted caller `probe-xml-contract.ps1`，fresh GUID 输出）：

| case | Diagnostics | accepted structural | accepted production gate |
|---|---:|---:|---:|
| valid | true | true | true |
| duplicate-ids | false | false | false |
| missing-count-attributes | false | false | false |
| wrong-assembly | false | false | false |
| wrong-framework (4.0.20) | false | true（family-only） | false |
| wrong-schema (999) | false | true（family-only） | false |
| native-boolean | false | true | true（旧 production cast，按 caller 要求不重开） |
| native-fraction | false | true | true（同上） |
| stale-raw | false | true | true（production XML gate 不读 raw，非生产回归） |

## 4. C：当前 raw 证据的 old/stale 边界

- 每个真实场景在 `Invoke-Verify` 启动子进程前记录 `lastVerifyStartedUtc`，调用点把该实际开始时间传给共享证据函数；raw 与 XML 的 `LastWriteTimeUtc` 必须不早于开始时间减 5 秒。
- 共享 raw 检查显式拒绝内容中的 `VERIFY-V5-STALE-OLD-RUN`；仅文件存在、非空、case/assertion/counts 相同不再足够。
- 保留并继续执行旧 `test-results` 子树的 stale 扫描；S2/S6/S9 另外直接断言新 accepted raw 为 current-run 且不含 stale marker，避免“未找到旧目录”被当作当前证据已证明。
- S7 缺目录时仍安全枚举（`Test-Path` 守卫），不中止 S8–S11，也不以缺目录替代当前失败证据。
- 外部只读探针未传实际开始时间时，使用该 run 自身 meta/summary 的保守回退锚点；真实场景始终传实际观察值。回退不用于把已知旧 marker 或 2 天前 mtime 洗成新。

验收证据（同上两个 adapted caller probes，均 fresh TOOL 目录）：

- `fail-stale-raw`：accepted=false，原因 `raw framework log contains the known stale-old-run marker`。
- PASS `stale-raw`：accepted=false，同 marker。
- `pass-valid` / `fail-valid` 的复制 valid control：accepted=true。
- 真实 S2/S6/S9 在最终运行中均通过“accepted raw current + no stale marker”断言；S7/S10 no-stale 断言保留。

## 5. 最终完整执行（R10）

命令（PS 5.1，独占 TEMP/CLI home/NuGet，`-KeepWorkspace`）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/tests/VerifyV5Diagnostics.Tests.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -KeepWorkspace
```

- stdout：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/repro/fixed-r10.stdout.log`
  （14,251 bytes，SHA-256 `3097A912996DAED3D545D3F0E06099E6E27EE7FC62DA5509D9999F11D39127C8`）
- stderr：`.../repro/fixed-r10.stderr.log`（0 bytes，SHA-256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`）
- native exit：`0`
- workspace：`build/.../temp/VerifyV5Diagnostics-20261102-023900-82542849/`
- **142/142 checks PASS**（本轮扩分母，未删除既有 125 项）。

逐场景断言数：`(pre accepted reader)` 1、S1 12、S2 7、S3 10、S4 5、S5 6、S6 10、S7 8、S8 17、TOOL_FIXTURE 35、S9 10、S10 9、S11 10、run identity 2。

| 场景 | runner | native exit | total/failed/skipped | XML |
|---|---|---:|---|---|
| S1 | `dotnet test (MTP)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 |
| S2 | `dotnet test (MTP)` | 0 | 2/0/0 PASS | T=2 P=2 |
| S3 | `in-process (direct)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 |
| S4 | `in-process (direct)` | 0 | 2/0/0 PASS | T=2 P=2 |
| S5 | `dotnet test (MTP)` | 1 | 0/0/0 FAIL | T=0 |
| S6 | `dotnet test (MTP)` | 0 | 2/0/0 PASS | T=2 P=2 |
| S7 | `dotnet test (MTP)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 |
| S9 | `dotnet test (MTP)` | 0 | 2/0/0 PASS | T=2 P=2 |
| S10 | `dotnet test (MTP)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 |

S8 实际 17 项（生产脚本 AST、4 helper、双 source root 同名隔离、双 runner 分目录、full sha 对照、`Invoke-Captured` partial+exception 且不伪造 0、真实 exit3 正例）；S11 实际 10 项（快照盲区整目录排除）。S1/S3/S5/S7/S10 的受控失败为预期负例，不累计成产品全绿。

R10 关键证据 SHA-256：

| 场景 | summary | XML | raw |
|---|---|---|---|
| S1 | `2B8EBBBDD39109C41DA77985991BF790BB70B95DB8059E14D4F64883CF7D68B9` | `8BC4CED775D39C939D31038E3DDD2E3409E9E012C05DC8A59A8D1547464C40E5` | `332E252AA26504D40197FE7BAFB8A86B5DDF332790EE48240D31DEF8B0E33C4C` |
| S2 | `4D66053B8E0F493FA7A927B8488E1773AAD2C140DFF36F861654D1CCC8BD09BF` | `B353114F336DE1D217C5E3B9C6DC62BAF456D2D1AA433C2B3B7718D34BA4F263` | `EBF7ACF44685E06FA975CE16DB9EE631E389C694F624165B539B38EF8B29821D` |
| S3 | `CD677449DECE03B5385E7CDF49229EF51A45C964C035E7E6A3FE83C5C650FDDD` | `C79A546B8CFF53ACCFF2D9B3AC1AC2B90B0E0914F6DF097B5516AC708394341A` | n/a（direct console） |
| S4 | `7A8D906A4F72D0FA3620443691B755826BE1C491B7C5495112DB14B4EDC9B133` | `D3E0F991B9DDC99062ECAB2539316457AE3C7C8FAC5248FB65086E2EE38D4F56` | n/a（direct console） |
| S5 | `5E866B0013753C574CB092B32D614655110B2CDF6F7F889C031636922DBD3629` | `B09C83DB0A357DB8CCC1ECFAF111D778899B27FC6AD26DC143BB827E6B4A9144` | `6F7CBCB66ABF4B9D061CD9912240B9075D5B7883E9FDB6C855945CFDAFA23D08` |
| S6 | `85A3B1BA2E00ED84EB195B88756A966D188403FF7669CAB4982D4C079DB4F0EE` | `8E95F4C78B54750CF57227BC8C8D99735D2A04FDCBD9320FF51B8E80DAAEF0CD` | `7ACA25E2F5DFEF160E7E2FF56E4AB46AD23D207D63D7BC2A443CB1E95460093C` |
| S7 | `8ECE8D04498F4D225438C1B0C22A568A7E7136A9B16F1124F5F1E7C809B9E584` | `684DBCC16D5B5F83889B601BC8074CA293DCAF75B8D91E83612BB67DAA7A528A` | `6969CAE482EEBE69E3797CE4AA9007A6DB6EDFB5DB18064AA7D1A20A737572AE` |
| S9 | `CA75CDC6E63C28623F1272F3321CF1A50ADAAE1FABA80118C9F9C7AA16CAEDBE` | `E434EDF4FC9E0C175CC04B2836DABF5A893B17D4765CE01A5FC3706D7E1235EF` | `8155E3CE379C27EF0241FA1D350F272509B0625E4A1CFB8C629B6D2893897C1A` |
| S10 | `8C87772CA8D9F1DF1A76EDEC772E29DDD7C13C864EF9B0A64470F08961DB4218` | `F7C8692B07B7F2FD12656EE232957CDAD163356B830A108E20875A32AAB54794` | `D520ABFCAFA72108F9604329E6E55CBB1B6E2B225655C9779C478453E880DAD8` |

## 6. 定向回归

全部 PS 5.1 实际执行、各自 native exit 0；它们是工具边界矩阵，不是 14 工程全量。

| 矩阵 | 结果 | 证据 |
|---|---:|---|
| dotnetTest results/path | 61/61 | `r2-regression/dotnettest/results.json`，SHA-256 `C0246FDB56713353901555A155658F64A55DC36BE083E3CED779EC330878F3D4` |
| captured exit / `Invoke-Captured` | 51/51 | `r2-regression/captured/results.json`，SHA-256 `DAC9DA3B6CEE82770D67247F47CDF52233F1EA607B026C0CCAC05F8D4A0951B7` |
| direct XML gate | 19/19 | `r2-regression/direct-results/results.json`，SHA-256 `1696A7ED97BD58933F156849AB74B1DBFC205D0F7EA3FD2C22B20061418E9A59` |
| native exit/metadata helper | 24/24 | `r2-regression/native-exit/results.json`，SHA-256 `25391C0EFF2AC04F75FF19B127F9B619E471A0544539B1FA2C0E2E623B7262F6` |

## 7. 证据与可运行补验

- 原 R1 失败/通过证据与 caller 原件保持只读，未覆盖。
- R2 adapted caller probes：
  - `r2-probes-e/assertion-probes.json`（A/C 的同源验证）
  - `r2-probes-xml2/xml-contract-verified.json`（B 的同源验证）
- R10 最终 workspace 保留；所有新产物在 `build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/`。
- 封存：`evidence-manifest.csv`（路径/bytes/SHA-256；不含自身）与 `evidence-manifest.sha256.txt`；报告副本在 `report/`。
- 补验命令同上；预期 `VerifyV5Diagnostics: PASS (142/142 checks)`、native 0。若生产合同再次漂移，新断言应失败而不是被放宽。

## 8. 提交与工作树约束

- 只暂存/提交两个授权文件；`git diff --cached --check`、`git diff --check` 为 0。
- 生成物（`build/`、`artifacts/`、日志、NuGet 缓存、manifest）全部 ignored，不提交。
- 未修改 workflow、生产 verifier/helper、业务/.NET、Unity、scheduler、其他线程/树/锁；未杀他人进程。
- 提交后逐文件审计暂存区、diff-check、tracked clean；R2 commit SHA 与 report SHA 在最终返回中报告。

## 9. 明确未覆盖

未运行当前头远端 CI、未下载/重跑 `41329645233`/`41329638524`；未运行 14 工程全量、PG、Unity、Player、Android、设备或整场随机；非 Windows 分支未实测；历史 `39970580589` framework 日志缺失仍 BLOCKED。G2–G5 未批准，V4 仍生产。本交付不构成生产验收，也不把预期受控失败累计成产品全绿。
