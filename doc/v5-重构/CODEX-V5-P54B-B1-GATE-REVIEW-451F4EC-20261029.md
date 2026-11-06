# V5 P5.4b-B1 HTTPS/WSS 准入复跑独立复核（451f4ec，2026-10-29）

状态：**独立复核（REVIEW），不是验收通过，不是批次 PASS，不是 G2–G5 通过。**
复核对象：冻结 PR20 候选 `451f4ec9803cb45c9b01a5724d58718ac6ad1728`（未验收）。
已验收基线：`origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
生产状态：**V4 仍是唯一生产基线；G2–G5 未批准；B1-BLOCKER-03 仍 OPEN/BLOCKED。**

---

## 0. 结论摘要

- 在基于 `8fd7c6c` 的本工作树中，用 `git archive` 把 `451f4ec` 取到 ignored 隔离目录；隔离目录的 git tree 与候选 tree **逐 blob 相等**（`23a5734f8506ab979eb7114875113132678e2bce`，2314 个文件）。未 checkout、未 merge、未把候选代码整体提交到本工作树。
- 在隔离 PostgreSQL 16.10、真实 LobbyService HTTPS、真实 TableService WSS、独占 loopback 端口 `59771/59772/59773` 上复跑原 `tools/ci/verify-v5-https-admission.ps1`：**wrapper native exit 0 / PASS**；13 个功能阶段全部 exit 0；B1 tests **44/44**（0 failed / 0 errors / 0 skipped / 0 not-run）；suite **exit 0**；11 条 assertions 到 `negative_matrix_complete`；10 条负例结果；4/4 负例 PG facts；manifest **371** 项，SHA256 `5E03D70B59DCE1FA5DAA5E59AAE35A6B903C86577EEF4E6D66C807739EF22486`；cleanup PASS。
- 首回执实测 `CanonicalPayloadSha256 = 6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a`，等于 `firstPayload.Digest.ToHex()`（MJC1 域分离 digest）；按 `CanonicalPayloadDigest` 的域前缀实现，裸 SHA-256 不是协议期望（既有诊断曾实测两者不同）；`FirstReceiptDigestVerified=true`。
- **lookup 回执 digest 仍为空**：`LookupReceiptDigestAvailable=false`、`LookupReceiptDigestMatchesExpected=false`、`CanonicalReceiptIdentityVerified=false`、`ReceiptDigestDomainMismatch=true`。这不是新失败，也没有被伪造成绿色；**B1-BLOCKER-03 保持 OPEN/BLOCKED**。gate 的 exit 0 只表示 B1 准入矩阵通过，不表示“首回执、lookup、outbox 三者 canonical digest 相等”已证明。
- 另有一次**复核方法造成的保存失败**：首跑把 `Tee-Object` 输出写进 `LogDir\wrapper.console.log`，manifest 生成时该文件仍被父进程持有，`Get-FileHash` 报 “file is being used by another process”，wrapper 因此 exit 1 / FAIL。该次所有功能阶段（含 suite、pg-facts、cleanup）均 PASS；这是复核侧 out-of-band 写日志造成的 manifest 读取冲突，不是候选或 B1 断言失败。该次结果已原样保留；随后用 `LogDir` 外的 console 重跑一次（Run B），未改代码、断言或 verifier。
- 本报告只证明：**在 isolated loopback 环境、exact frozen candidate 上，B1 HTTPS/WSS 准入矩阵可重复通过**。它不证明 B2 整场、G3、真实公网/弱网、Unity/Android/iOS 或任何容量结果。

---

## 1. 基线与精确 SHA

| 项 | 精确值 / 说明 |
|---|---|
| accepted baseline | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（`origin/v5`） |
| frozen PR20 candidate | `451f4ec9803cb45c9b01a5724d58718ac6ad1728` |
| candidate tree | `23a5734f8506ab979eb7114875113132678e2bce`（2314 files） |
| candidate 是否含 digest correction | 是；`c8fe901915ec4c29a80775ad26639b16486f85b8` 是 `451f4ec` 的祖先（`git merge-base --is-ancestor c8fe901 451f4ec` exit 0） |
| `c8fe901` 内容 | 只改 B1 测试设施 `NegativeProbes.cs` / `B1Models.cs`，96 insertions / 16 deletions |
| candidate `NegativeProbes.cs` | SHA256 `805661F328D544A9D6E0825449547F5CB15C5F22E520185C71970A57630FCDBA` |
| candidate `B1Models.cs` | SHA256 `7C5FEF3B1117B355084CFE3A7A492EE73657584CD938C7BB1C968163DBEEED5F` |
| fix patch | `doc/v5-重构/patches/V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029.patch`，SHA256 `2E2A52D850FE8720F443EA21BF431DE2B7F6E04C7322FDCFE8B2A5861B3D8F29` |
| patch-apply-check 记录的 patched hashes | `805661F3…` / `7C5FEF3B…`，与 exact candidate 两文件相同 |
| 隔离目录 scratch head | `66eefbcc9d9e32080e28113ee609779c5a0f4692`（仅本地 scratch git） |
| 隔离目录 scratch tree | `23a5734f8506ab979eb7114875113132678e2bce`，与 candidate tree 相等 |

**取件命令与 native exit：**

```powershell
git archive --format=zip -o build\review-candidate-451f4ec.zip 451f4ec9803cb45c9b01a5724d58718ac6ad1728
# exit 0
Expand-Archive -Path build\review-candidate-451f4ec.zip -DestinationPath build\review-candidate-451f4ec -Force
# exit 0
```

隔离目录内初始化本地 scratch git（只为满足 runner 的 `git rev-parse HEAD` guard 和留证）：

```text
scratch head 66eefbcc9d9e32080e28113ee609779c5a0f4692
scratch tree 23a5734f8506ab979eb7114875113132678e2bce
candidate tree 23a5734f8506ab979eb7114875113132678e2bce
tree-equal True
```

说明：scratch head 不是 `451f4ec`；`451f4ec` 是通过 `git archive` 的内容与 tree 相等来钉住的。日志中的 `head=66eefbc…` 是 scratch commit，不得写成候选 commit。未把候选代码提交到本工作树，未 merge/push/PR/部署。

---

## 2. 范围与边界

- 只在本工作树 `D:\majiang-wt\b1-gate-451f-ds-20261029` 及其 ignored `build/review-candidate-451f4ec/` 内操作。
- 未修改其他工作树、其他分支、Unity/Resources/Burst、`dotnet/Mahjong.sln`、统一 verifier、生产代码、测试断言、TLS 校验或票据校验。
- 未改机器证书存储/全局信任；临时 TLS root/leaf 仅由被测客户端逐连接 `CustomRootTrust` 使用。
- 未 push、PR、merge、部署、转派/子代理；未写 `complete.flag`。
- 唯一计划提交物：本报告（`doc/v5-重构/CODEX-V5-P54B-B1-GATE-REVIEW-451F4EC-20261029.md`）。`build/` 下的候选副本、日志、证据均为 ignored，不进入提交。
- 复跑前的环境事实：CurrentUser Root 证书 **39**、LocalMachine Root **39**；机器上已有 6 个其他工作树的 `postgres.exe`（PID 5760/32868/45600/51284/59824/60448，路径均 `D:\majiang-pg-tmp\codex-integrated-r3-20261024\...`）。本次未触碰这些进程。

---

## 3. 先审计既有 B1 批次/诊断/修复证据

### 3.1 批次复核（未通过输入）

`ab8d45f27f9f9d277127eba8872abb7456292557` 的 `CODEX-V5-P54B-B1-BATCH-REVIEW-20261029.md` 记录：在 `792ce41` 上复跑同一 runner，suite **native exit 1**、wrapper exit 1 / FAIL、`suite.log` 只有 `InvalidOperationException: [redacted]`、manifest 212 项 SHA256 `D14DDA17C1311064938F3BDD8FA8B64ACE2B8F33DCBE323F11A5265E6B679302`。该失败是历史事实，本报告不把它改写为 PASS。

### 3.2 独立诊断

`a2a1bf9` 的 `V5-B1-INTEGRATED-FAILURE-DIAGNOSIS-20261029.md` 用冻结证据 + 源码 + 独立探针把最可能路径定位到 `NegativeProbes.RunSameKeyConflictAsync()` 的首回执 raw-SHA-256 vs MJC1 域 digest 比较：首回执 `Rejected/ROUND_MISMATCH`、digest 等于域 digest、不等于裸 SHA；lookup 为 `Found`，但 digest 为空。诊断明确写了“不是原进程逐调用追踪”，并保持 B1-BLOCKER-03 OPEN。

### 3.3 修复报告与证据

`e5acff0` / `9428870` 的 `V5-B1-FIRST-RECEIPT-DIGEST-FIX-REPORT-20261029.md` 记录：未打补丁候选复现 **native 1**；最小 B1 测试设施 patch 后，隔离 PG16.10/TLS/独占端口上 **suite exit 0、wrapper exit 0、44/44、11 assertions、10 negative results、4/4 negative facts、manifest 371、cleanup PASS**。

本报告对 `doc/v5-重构/evidence/V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029/SHA256SUMS.txt` 的 **18** 个条目逐项重算：**0 mismatch / 0 missing**。其中：

```text
patched/suite.json              8CE4F4C9EF0AB0E0A88C4EC3D3C5806192CEF4670136503F836C16D946216B68
patched/suite-summary.json      47AD2F5A066BACDD6F032B043956E30BBB34570B6EDD74B6F31AFB07B3A62084
patched/db-facts.json           6D16F74382458B8BAAE8CA15C3E4EF3BB8CC6798BA1489A155DA09B77D09A7952
patched/negative-facts-audit.json 361AC52050D15365AF61960CC2550001ADC3B32144DDCDD4EBA7383ACCDA776B
patched/wrapper-summary.json    DCB0BD3F33698842D8FD25974A2AC8D57EDAE2FE2E06868A79161FCC437A1080
unpatched/suite.json            103AEBEB4C5406AFA3DC833A31EC34877EC32A7C33452BFEFCE3B284622351E9
```

`patched/wrapper-summary.json`：exitCode 0、status PASS、cleanupOk true、secretMaterialRemoved true，13 个 runner 阶段（含 tests-b1）均 PASS；`patched/suite.json` 的 `FirstReceiptDigestVerified=true`、`LookupReceiptDigestAvailable=false`、`CanonicalReceiptIdentityVerified=false`、`ReceiptDigestDomainMismatch=true`。该证据与本次 exact candidate 的 harness 文件 byte-level 相同（见 §1），因此本次复跑不是另起一套实现。

**证据边界：** fix evidence 的 guard head 是 scratch `46e2c018f477f8399d29947af05b264ecfef5bb8`，不是 `451f4ec`；候选内容一致由 `git archive` tree 与两文件 SHA256 证明，报告不把 `451f4ec` 写成该次运行的直接 head。

---

## 4. 复跑环境与原始命令

| 项 | 实测 |
|---|---|
| OS | Windows 10.0.19045（本机 loopback） |
| .NET SDK | `D:\Unity\dotnet\dotnet.exe` **8.0.426**（host 8.0.32） |
| PostgreSQL | **16.10**，`initdb -U mahjong_pg`，`--auth-local=trust --auth-host=trust`，独占 data directory |
| PG 离线 zip | `D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip`，SHA256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`（runner 内置期望一致） |
| Run A 端口 | PG 59671 / Lobby HTTPS 59672 / Table WSS 59673 |
| Run B 端口 | PG 59771 / Lobby HTTPS 59772 / Table WSS 59773 |
| 数据库 | `mahjong_b1_lobby`（owner `b1_lobby_owner`，app `b1_lobby_app`）、`mahjong_b1_table`（owner `b1_table_owner`，app `mahjong_table_runtime`），只读 `b1_observer` |
| TLS | 每次运行新生成隔离 root/leaf；客户端逐连接 `CustomRootTrust`；正票只由真实 Lobby HTTPS 签发 |
| 机器信任 | 未改；跑前/跑后 CurrentUser Root 39、LocalMachine Root 39 |

**Run B 原始命令**（console 写在 `LogDir` 外的 `artifacts/review-runs/run2-console.log`）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\ci\verify-v5-https-admission.ps1 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -PgZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip `
  -PgPort 59771 -LobbyPort 59772 -TablePort 59773 `
  -Root D:\majiang-wt\b1-gate-451f-ds-20261029\build\review-candidate-451f4ec\artifacts\b1-review2-451f4ec-20261029\pg `
  -LogDir D:\majiang-wt\b1-gate-451f-ds-20261029\build\review-candidate-451f4ec\artifacts\b1-review2-451f4ec-20261029\logs `
  -SuiteTimeoutSeconds 600
```

Run A 命令相同，仅端口 `59671/59672/59673`、Root/LogDir 为 `b1-review-451f4ec-20261029`，并把 console `Tee-Object` 到 `LogDir\wrapper.console.log`。

---

## 5. 原生 exit、阶段与计数

### 5.1 Run B（无复核侧文件冲突的正式 gate 运行）

wrapper native exit **0**；末行 `P54B-B1-HTTPS-ADMISSION-PASS`。阶段逐项 native exit：

| 阶段 | exit | 实测摘要 |
|---|---:|---|
| guard | 0 | sdk 8.0.426；head `66eefbc…`；branch `review/b1-451f4ec`；trackedDirty=no |
| role-separation | 0 | lobby/table owner、schema owner、app attrs `false/false/false`、DDL false、DML true |
| cluster | 0 | PostgreSQL **16.10**；data_directory 为本集群；port 59771 |
| fresh-build | 0 | 2 个 B1 工程 Release `--no-incremental -nr:false` |
| tests-b1 | 0 | **total 44 / passed 44 / failed 0 / errors 0 / skipped 0 / not-run 0**；XML SHA256 `D7CEB42332BD9E0BD75CEC97220514E4A5454A428BD0208429986C4616861BDD` |
| fresh-build-accepted-helper | 0 | accepted keygen/certgen helper fresh build |
| keygen-certgen | 0 | 隔离 ES256 票据密钥 + 测试 TLS 链 |
| fresh-build-services | 0 | LobbyService + TableService Release fresh |
| lobby-https | 0 | 真实 Lobby HTTPS `/health/ready` 200，PID 57900 |
| table-wss | 0 | 真实 Table WSS `/health/live` + `/health/ready` 200，PID 55436 |
| suite | 0 | room `room_01a187bce6e57656ba03d818a8d05958`；normalVersion 1；crashVersion 2；recovered True |
| pg-facts | 0 | stateVersion 5；events 10；normal 1/APPLIED journal 1 eventRows 2；crash 1/APPLIED journal 1 eventRows 2；negFacts 4；outbox 0；lobbyConsumption 0；ledger NOT_RUN |
| cleanup | 0 | ownership-verified cluster removed；ports free；no postmaster |
| manifest（finally，非 stage） | 0 | count **371**；SHA256 `5E03D70B59DCE1FA5DAA5E59AAE35A6B903C86577EEF4E6D66C807739EF22486`；audit missing 0 / mismatched 0 |

suite 断言 **11** 条，顺序到 `negative_matrix_complete`；负例结果 **10** 条；`NegativeFacts` **4** 条且 DB audit 全部 `ok=true`。上表 13 个 runner stage 与 `wrapper-summary.json.stages` 一致；manifest 由 `finally` 生成，不在 `stages` 数组内，其 exit 0 指 `Write-Manifest` 成功且 371 项逐项复验通过。

### 5.2 Run A（已保留的复核侧失败）

Run A 的功能阶段同样全部 PASS（含 suite exit 0、pg-facts exit 0、cleanup exit 0），但 wrapper native exit **1 / FAIL**：

```text
manifest error: The file '...\logs\wrapper.console.log' cannot be read: ... because it is being used by another process.
P54B-B1-HTTPS-ADMISSION-FAIL
```

原因：Run A 用 `Tee-Object` 把 console 写入 `LogDir\wrapper.console.log`；`Write-Manifest` 递归哈希 `LogDir` 时该文件仍被父 PowerShell 管道持有。该文件不是 runner 证据，也没有被 manifest 排除。**这是复核侧 out-of-band 写入造成的读取冲突，不是候选断言、suite、PG 或 cleanup 失败。** 本报告保留该次 exit 1 与其 `wrapper-summary.json`（`manifestError` 字段），不把它洗成绿色；Run B 是移走该冲突后的独立第二次执行，代码/断言/verifier 均未改动。

Run A 关键事实：room `room_01a187b9195b76e98b3751ff86350e64`；44/44；suite `6f99…`；B1-BLOCKER-03 flags 同 Run B；DB stateVersion 6 / events 12 / normal 1/APPLIED / crash 1/APPLIED；cleanup PASS；manifest 未生成。

### 5.3 关键证据 SHA256（Run B）

```text
suite.json                         956974C92428025B56EACE90092D2E05ADB4537F08FF8220B7EA85C221F5B4D2
suite-summary.json                 A56BCE7EF29626E6FE6CB4E9D2E88E7C8532115532351C6B94FC1A6D589B9F70
db-facts.json                      BC18B96D0AE4BE845E0DE2C687F548F32761D5F9E3E279A4A252AFF78C50C849
negative-facts-audit.json          5AE9D8838621D45FA51B30D36F14D0F4EF45D1A2655C0E4A627F8EF37A57E474
runner.log                         93C1F4A4A07119406E87920F51B955B8F885BC475BFBB285A357B5FA72185DCA
tests-b1.xml                       D7CEB42332BD9E0BD75CEC97220514E4A5454A428BD0208429986C4616861BDD
wrapper-summary.json               500619FBB7E1DE39EC36F0DCC9732176841169E889490043E5396433918F8F59
manifest.sha256.json               5E03D70B59DCE1FA5DAA5E59AAE35A6B903C86577EEF4E6D66C807739EF22486
lobby.stdout.log                   8E3ED7A21E206142EA79576AB281F6D625B4B114BD348D051766DC72C6C30172
table.stdout.log                   28B414D208061DCE1D174CB6870B1BF829D540680AFC6B8640BD9441717E8E4B
suite.log                          AFEE09067B9621D781B6253AD3EFAFA4B09C26A053C72AE6BBF6B975604BBEDB
```

---

## 6. digest / replay / identity 断言核查

### 6.1 digest 域

- 生产 `Mahjong.Contracts/CanonicalPayloadDigest.cs` 的 `Compute` 先追加域前缀 `MAHJONG-V5-CANONICAL-COMMAND\0` 与长度，再做 SHA-256；裸 `TablePayloadSha256` 不是 wire receipt 的协议期望。
- `TableService/TableSessionHandler.cs:1481` 写 `wireDigestHex = CanonicalPayloadDigest.Compute(payloadBytes).ToHex()`；`MapReceipt(result, wireDigestHex)` 写入首回执；`BuildReceipt` 在 `:2513` 回填 `reply.Scope = BuildAuthoritativeScope(_identity!)`。`TableSession.cs:1356` 现在要求 receipt 带 `Scope`，`1373-1375` 只在 digest 非空且不等于 outbox digest 时才判冲突。
- 本次首回执：`6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a`；`FirstReceiptDigestVerified=true`。该值按域前缀计算；既有诊断实测裸 SHA 与其不同，符合域分离。
- **lookup**：`LookupCanonicalDigestHex=""`；`LookupReceiptDigestAvailable=false`；`LookupReceiptDigestMatchesExpected=false`；`CanonicalReceiptIdentityVerified=false`；`ReceiptDigestDomainMismatch=true`。负例仍保留原 receipt 的 status/reason/evaluated/version，第二请求为精确 `REQUEST_ID_CONFLICT`。因此：**首回执 MJC1 身份已验证；lookup 侧 canonical 身份未验证；B1-BLOCKER-03 仍 OPEN/BLOCKED。** 不把 `CanonicalReceiptIdentityVerified=false` 写成 PASS，也不注入伪 digest。

### 6.2 replay / 幂等

- `NormalReplayObservedReceipts=2`；同一条原始命令字节只捕获一次，exact-byte 重发后第二 receipt 为 `Applied`、`Replayed=true`、`CommandVersion=1` 与原 receipt 相同；`NormalReplaySameReceipt=true`；客户端 outbox `PendingCommands.Count=0`。
- PG：`normalRequestCount=1`、`normalRequestStatus=APPLIED`、`normalCommandVersion=1`、`normalJournalCount=1`、`normalEventRows=2`；没有第二条正常 receipt/journal。`appliedReceipts=3` 是房间级计数；normal request 本身仍由 `normalRequestCount=1` 单独核验，额外 receipt 不属于 normal request（其归属未在本报告单独追踪）。
- 崩溃链：真实 OS kill 客户端（crash PID 23540）→ 新 PID 28820 / 新 boot id → 磁盘 outbox `PendingBeforeQuery=1`、`DeliveryUnknownBeforeQuery=true` → 新 Lobby 票 + 新全量基线 → `RECEIPT_QUERY` 返回 `Applied/2`（`ResolvedFromLookup=true`）→ `PendingAfterQuery=0`、`OutboxEntriesAfter=0`；四个在线客户端 + 恢复进程都看到 crash 动作；恢复 session id hash 与原 session 不同。

### 6.3 identity / 隐私

- 4 玩家 + 1 观战全部准入；`AdmittedPlayers=4`、`SpectatorAdmitted=true`；座位 0/1/2/3 + spectator NoSeat；账号、room、role、seat 与 Lobby assignment 逐项相等；binding/owner epoch 均 > 0；**5 个不同 session id**。
- 5 个客户端都安装 full baseline；player/spectator private stream 隔离；观战无 private view；公共投影/事件批次由 typed/descriptor 白名单持续审计（`PrivacyBatchesObserved>0`）。
- TLS 负例 6/6（HTTPS/WSS × untrusted-root / wrong-SAN / expired）；tampered / foreign-room / expired ticket 均被 4401/INVALID_TOKEN 拒绝；观战提交玩家命令 `SCOPE_ROLE_NOT_AUTHORIZED`；跨账号查询 `SCOPE_MISMATCH`；3 条零副作用负例的 receipt/journal/event 均为 0，same-key 为 1 条 REJECTED receipt、0 journal、0 event。

**断言精度备注（MINOR，不改变本次 gate 结论）：** 代码只断言每个客户端有 `ConfirmedCursor`，未断言 5 个 cursor 值互不相同或逐座位单调；“独立 cursor”是部分证明。跨账号负例同时换了 account 和 room，foreign-room 负例使用不存在的 room 字符串，因此它们证明“被拒绝”，但没有单独隔离 account-only 与 existing-room-only 边界。

---

## 7. 清理、所有权与无全局信任修改

Run B 的 cleanup 阶段 exit 0，独立复核：

- `Test-Path <run-root>` → **False**；`Test-Path <run-root>\material` → **False**。
- 59771/59772/59773 均无监听；`pg_ctl stop -m immediate` 后无本集群 postmaster；`postmaster.pid` 不存在。
- 机器上只剩复跑前已有的 6 个其他工作树 `postgres.exe`（PID/路径与 pre-run 相同），本次未触碰。
- `secretMaterialRemoved=true`；manifest 中 `private.pem` / `.pfx` / `keys` / `tls` / `guest-secret` / `outbox` 条目 **0**。
- 扫描证据目录未发现实时 JWT/Bearer/私钥/`sessionToken`/`bootstrapSecret`；唯一 pattern hit 是 `tests-b1.xml` 内 redaction 单测的**合成** `password`/JWT 样本输入，不是有效凭据。xUnit XML 含本机 `computer`/`user` 元数据，属于本地证据元数据，不是认证秘密；该目录 ignored，不提交。
- CurrentUser/LocalMachine Root 证书数跑前跑后均为 **39/39**；未改机器信任、未使用 AcceptAll、未启用明文 fallback。

Run A 同样完成 cleanup（run root 不存在、端口空闲、secretMaterialRemoved=true）；其失败只在 manifest 读取，不在资源所有权或清理。

---

## 8. 判定与遗留

| 维度 | 判定 |
|---|---|
| exact candidate 取件/tree 等价 | **PASS**（tree `23a5734…`，2314 files） |
| B1 harness 与 fix evidence 文件级一致 | **PASS**（`805661F3…` / `7C5FEF3B…`） |
| 既有 fix evidence 哈希自洽 | **PASS**（18/18，0 mismatch） |
| B1 tests | **PASS**（44/44，0 skip/not-run） |
| B1 HTTPS/WSS 准入矩阵（Run B） | **PASS**（native exit 0，13 stages，11 assertions，10 negatives，4/4 facts） |
| 首回执 MJC1 digest | **PASS**（`6f99…`，`FirstReceiptDigestVerified=true`） |
| exact-byte replay / outbox 清理 | **PASS**（same version/replayed，pending 0，PG 1/1/2） |
| 强杀 → 新 PID → 磁盘 outbox → RECEIPT_QUERY | **PASS**（new pid/boot，Found 清空） |
| cleanup / 无全局信任修改 | **PASS**（端口空闲、无 postmaster、39/39、其他 PG 未动） |
| lookup canonical digest / 三方身份 | **BLOCKED/OPEN（B1-BLOCKER-03）**；`CanonicalReceiptIdentityVerified=false` |
| Lobby 跨进程 ticket consumption | **OPEN（B1-MAJOR-03）**；`lobbyTicketConsumptionRows=0` |
| B1 批次验收 / PR20 验收 | **NOT ACCEPTED**；本报告只是 review |
| B2 整场 / 结算 / wallet/ledger | **NOT_RUN** |
| G2–G5 / 真实网络 / 真机 / 容量 | **NOT_RUN / 未批准**；V4 仍是生产 |

**本次发现：** 在 B1 HTTPS/WSS 准入矩阵内未发现新的 BLOCKER；Run B 无失败阶段。Run A 的 exit 1 是复核侧 console 文件与 manifest 的读取冲突，已保留并单列，不计为候选缺陷。B1-BLOCKER-03 与 B1-MAJOR-03 是既有、已知且未被本次 gate 关闭的遗留。

**判定：** B1 准入矩阵在 exact frozen candidate 上可重复通过；这不是 B1 全面验收，也不是 PR20 验收。由于 lookup canonical digest 仍为空，任何“首回执、lookup、outbox 三者 identity 相等”的结论都不得写成通过。

---

## 9. 仍缺的 P5.4b / G3 范围

1. **B1-BLOCKER-03（lookup digest）**：`Rejected` lookup 的 `canonical_payload_digest` 仍为空；需要生产/契约 owner 在“为 Rejected 持久化并返回 MJC1 域 digest”与“明确接受 lookup 不返回 digest、由 outbox 期望 digest 绑定并降级为 classified/BLOCKED”之间裁决。裁决前不能宣称三方 canonical identity 通过。
2. **B1-MAJOR-03（跨进程票据消费）**：Lobby `table_ticket_consumption` 仍为 0 行，TableService 使用内存 one-time jti registry；“Lobby 一次性消费”与 TableService assignment 消费语义仍未统一。G3 若要求跨实例/重启后的票据消费，需要持久化权威。
3. **B2 整场与结算**：本次只做 B1 准入 + 单动作 + 强杀恢复；未做多客户端完整一局/连庄/整场、结算、点棒/立直棒、观战全流程。`settlement_outbox` 本次为 0，未验证结算 outbox 消费者。
4. **账户/资产**：wallet/ledger schema 不存在，`ledgerStatus=NOT_RUN`；账户资产唯一写者、结算 outbox、线上资产功能仍关闭。
5. **PG 故障注入/恢复**：未注入 DB 慢写/断开、DB 提交后进程崩溃、checkpoint 损坏、事件缺口、租约过期/owner 转移、双实例旧 owner 写入、备份空库恢复；这些是 G3 的核心项，不能由本次 loopback 成功推断。
6. **票据/顶号/过期边界**：本次有 tampered/foreign/expired 负例，但未覆盖同 assignment 旧票 TTL 内重签与安全接管的全矩阵、跨实例顶号、票据密钥轮换。
7. **真实网络与平台**：仅本机 loopback；未验证公网 CA/域名、TLS 卸载、跨物理网络、弱网/丢包/切网/后台恢复、Android/iOS 真机/IL2CPP、Unity 客户端。不得写成平台或网络通过。
8. **G3 其余检查**：迁移脚本/索引/约束的独立审查、脱敏日志的完整口径、构建依赖方向、票据密钥边界、实际故障注入点与每次结果，仍需 `V5-3-REVIEW.md` 级别独立执行；本次只覆盖 B1 runner 的已实现子集。

G2、G4、G5 不在本次范围；V4 仍是唯一生产基线，PR20 保持 Draft/未验收，不能由本报告创建 `complete.flag`。

---

## 10. 证据索引与提交边界

本工作树 ignored 证据根：

```text
build/review-candidate-451f4ec/                         # git archive 451f4ec 的隔离只读副本（scratch git）
build/review-candidate-451f4ec/artifacts/b1-review-451f4ec-20261029/logs/    # Run A（native 1，manifest 冲突，保留）
build/review-candidate-451f4ec/artifacts/b1-review2-451f4ec-20261029/logs/   # Run B（native 0，正式 gate）
build/review-candidate-451f4ec/artifacts/review-runs/run2-console.log        # Run B console（LogDir 外）
build/review-candidate-451f4ec/artifacts/review-runs/run2-exit.json
```

Run B 关键文件：`suite.json`、`suite-summary.json`、`db-facts.json`、`negative-facts-audit.json`、`tests-b1.xml`、`runner.log`、`wrapper-summary.json`、`manifest.sha256.json`、`manifest.sha256.txt`、`manifest-audit.json`、`lobby.stdout.log`、`table.stdout.log`、`suite.log`（SHA256 见 §5.3）。

Run A 关键文件：`suite.json`（`C6D8991594FE2DB3F08D0AF077C88CDA7FB14B6D7AF641EB2DAFFEE6532D4A89`）、`wrapper-summary.json`（`E1295513FA0FF00156C7A9EEE17304D71A61BA5D101E74D152D66EFD1A7B3B28`，含 `manifestError`）、`runner.log`（`74D9A30552FCC1B39CB25FCB12874493B508E91756A84091B29AF8468760D316`）。

提交边界：本报告是唯一提交文件，基于 `8fd7c6c`；不提交 `build/`、候选代码、日志、证书、密钥或 outbox；不 push/PR/merge/部署。
