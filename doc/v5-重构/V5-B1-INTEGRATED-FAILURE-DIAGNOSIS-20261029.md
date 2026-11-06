# V5-B1 集成失败独立诊断：P5.4b-B1 HTTPS 准入 suite native 1（2026-10-29）

状态：**独立诊断报告，不是验收通过，也不是批次 PASS。**

范围：仅本 DeepSeek 会话；只读其他工作树，只写本工作树 ignored 诊断目录与本报告。未修改生产代码、测试、统一 verifier、Unity/Resources/Burst、`sln`、其他工作树或机器信任；未 merge/push/PR/部署。

---

## 0. 结论摘要

- 已在冻结证据与源码调用路径上定位 `suite native 1` 的**最可能**抛出点：`NegativeProbes.RunSameKeyConflictAsync()` 的首个回执校验失败，`SuiteRunner` 的 `Require` 抛出 `InvalidOperationException`，被 `Program.Redact` 整体脱敏为 `[redacted]`，suite 返回 1。原始运行没有保留未脱敏异常文本或该进程的直接字段追踪，因此这是证据支持的最可能路径，不是对原进程的逐调用证明。
- 直接原因的最可能解释不是新的生产回归，而是 **B1 负例探针与 PR20 生产 digest 契约不同步**：候选源码 `792ce41`（含 `9f223ef`）的首个回执已按 B1-BLOCKER-01 的二级根因修复为 MJC1 **域分离** canonical digest；B1 探针仍把它与 **裸 SHA-256** 比较，在候选代码上二者不等。
- 精准探针在独立进程中实测首个回执为 `Rejected / ROUND_MISMATCH`，其 `CanonicalPayloadSha256` 等于域 digest、不等于裸 SHA-256；同键异 kind 返回 `REQUEST_ID_CONFLICT`；回执查询为 `Found / Rejected / ROUND_MISMATCH`，原 status/reason/evaluated/version 均保留。该探针复现了同一代码路径与输入形状，但不是原始 suite 进程的直接追踪；生产侧行为与候选代码一致。
- 该失败使批次不能判 PASS：负例矩阵在首项后中断，后续跨账号/观战越权/篡改/异房/过期票负例本轮 **NOT_RUN**；不能据此推断它们通过或失败。
- 另有一条独立但被本次失败提前掩盖的事实：`Rejected` 回执的 lookup digest 为空；B1 的 `CanonicalReceiptIdentityVerified=true` 是早退路径用 `expectedDigest` 赋值的，并不代表 lookup 真的返回了 digest。B1-BLOCKER-03 仍需单独裁决，不能用放宽校验或注入伪 digest 关闭。
- 精确修复范围在 B1 测试设施 `NegativeProbes.cs` 的首回执 digest 比较及其证据标志；本 native 1 不需要改生产 `TableSessionHandler`。若仍要求 Rejected lookup 的 canonical 身份，另立生产/契约裁决。

---

## 1. 基线、冻结输入与证据边界

| 项 | 精确值/说明 |
|---|---|
| accepted baseline | `origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| 候选/复现头 | `792ce41cb949e552929ad837a4c1630258157c36` |
| 未通过批次（仅冻结诊断输入，不视为 accepted） | `ab8d45f27f9f9d277127eba8872abb7456292557` |
| B1 round-5 交付头 | `5a270b949c53fd1ec7faf00b07df00fe4192bdb8` |
| PR20 生产修复提交 | `9f223ef9e08111ddb7f364d69c9ace25a2e5584a` `fix(v5): restore receipt scope/digest and safe ticket re-admission` |
| 本候选源码副本 | `git archive 792ce41` → 本树 ignored `build/p54b-diag/src` |
| 原始冻结证据 | 只读 `D:\majiang-wt\p54b-b1-https-admission-ds-20261029-1250\artifacts\p54b-b1-logs-20261029-101711-5bccd1b3f80a42518ccb0d2cfe74e072\` |
| 冻结 review 文本 | `ab8d45f:doc/v5-重构/CODEX-V5-P54B-B1-BATCH-REVIEW-20261029.md`（仅 11 行） |

边界：

- `ab8d45f` 是**未通过批次**，本报告只把它作为冻结诊断输入，不当作 accepted baseline，不继承其“通过”含义。
- 未 checkout、未修改其他工作树/分支；原始证据目录只读，未写、未删、未重命名。
- 未运行或修改统一 verifier；所有诊断脚本/工程都在本树 ignored `build/p54b-diag/` 下。
- 未改机器信任；临时 TLS 根只由诊断客户端逐连接使用。
- V4 仍是生产；G1 approved；G2–G5 未批准，本报告不改变阶段结论。

---

## 2. 原始证据审计（只读）

原始证据目录（下称 `E`）的 `runner.log` 记录：

```text
stage guard: PASS ... head=792ce41...
stage cluster: PASS ... postgres=16.10 ... port=58871
stage tests-b1: PASS total=44 passed=44 failed=0 errors=0 skipped=0 notRun=0
stage lobby-https: PASS ... pid=52876
stage table-wss: PASS ... pid=61112
FAILED: The B1 suite failed with exit 1
stage exception: FAIL (exit 1)
stage cleanup: PASS ... ports free
```

`E/suite.log` 仅 2 行：

```text
B1-SUITE-FAIL InvalidOperationException: [redacted]
```

`E/suite.json` 的关键字段：

| 字段 | 观测值 | 含义 |
|---|---|---|
| `AdmittedPlayers` | `4` | 四玩家准入已发生 |
| `SpectatorAdmitted` | `true` | 观战准入已发生 |
| `BaselineFullForAll` | `true` | 五个真实 Client 均安装全量基线 |
| `PrivateStreamsIsolated` | `true` | 玩家/观战私有流隔离 |
| `NormalActionStateVersion` | `1` | 普通动作已 APPLIED/1 |
| `NormalReplaySameReceipt` | `true` | exact-byte replay 返回同一 receipt |
| `CrashCommandVersion` | `2` | 强杀客户端命令已提交 |
| `RecoveryFound` | `true` | 新 PID 磁盘 outbox 查询到 Applied |
| `RecoveredProjectionConfirmed` | `true` | 恢复进程投影确认 |
| `AllClientsSawCrashAction` | `true` | 四在线客户端 + 恢复进程确认 |
| `NegativeResults` | 仅 3 条 TLS 负例 | same-key 等负例尚未加入 |
| `SameKeyDifferentBytesPreservedOriginal` | `false` | same-key 冲突探针失败 |
| `ReceiptDigestDomainMismatch` | `false` | 早退路径的默认值，不能据此判断 digest 域 |
| `CanonicalReceiptIdentityVerified` | `true` | 早退路径用 `expectedDigest` 赋值，**不是** lookup 证据 |
| `CrossAccountReceiptQueryRejected` 等 | 全 `false` | 未执行，不是失败 |
| `Assertions` | 最后为 `multi_client_confirmation_after_recovery`，再一条 `FAILED: InvalidOperationException: [redacted]` | 失败发生在该断言之后、`negative_matrix_complete` 之前 |

由 `Assertions` 可基本排除更早的失败：`new_pid_disk_outbox_receipt_query_found` 与 `multi_client_confirmation_after_recovery` 均已加入；`negative_matrix_complete` 未加入；`NegativeResults` 已含三条 TLS 负例。因此失败最可能位于 `NegativeProbes.RunAsync` 中 TLS 负例之后、same-key 探针返回处。该推断依赖冻结的聚合标志，无法替代原进程的直接追踪。

`E/wrapper-summary.json`：`exitCode=1`、`status=FAIL`、`cleanupOk=true`、`secretMaterialRemoved=true`；manifest 212 项，SHA256 `D14DDA17C1311064938F3BDD8FA8B64ACE2B8F33DCBE323F11A5265E6B679302`（review 记录）。

本次独立哈希（原始证据，只读）：

```text
suite.json              7184E1F61AD16504AFA7E131C363A97CDB38C677FE6B0786C9D008BFC186CEEA
suite.log               80DB756FD8B373CE2E6ADD7730633FE23D095A18510433E79FFE04077ABC14CD
runner.log              CF66B30682510239E3D7853C07823AA9A57B197593A619187DF3D1183DBA3B82
wrapper-summary.json    4F7A372C8D63AA1A6CA88B87DCD8F1DB292EC7E6F5EA0B208206B4F5C97D57EE
manifest.sha256.txt     18509E6417E8F8C26EAE8369AC95A5A3EA583D7F9212D6FB1CC30EAC1B09B092
```

---

## 3. 调用路径与根因

### 3.1 失败调用链（候选 `792ce41`）

```text
SuiteRunner.RunAsync                      SuiteRunner.cs:574
  → NegativeProbes.RunAsync               NegativeProbes.cs:18
    → TlsNegativesAsync                   NegativeProbes.cs:27, 250
    → RunSameKeyConflictAsync             NegativeProbes.cs:322
      → 首个回执校验                       NegativeProbes.cs:349-361
      → return Preserved=false            NegativeProbes.cs:353-361
    → Require(...)                        SuiteRunner.cs:55-58
      → InvalidOperationException         SuiteRunner.cs:948 (Require)
  → catch (Exception)                     SuiteRunner.cs:592-605
    → Program.Redact                      Program.cs:111-130
    → B1-SUITE-FAIL ... [redacted]        SuiteRunner.cs:603
    → return ExitFailed (1)               SuiteRunner.cs:604
```

### 3.2 具体比较（为什么在候选代码上不等）

B1 探针在 `NegativeProbes.cs:335-336` 计算的是 **裸 SHA-256**：

```csharp
string plainDigest = Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(firstPayload.CopyBytes()));
string expectedDigest = firstPayload.Digest.ToHex();   // MJC1 域分离 digest
```

随后 `NegativeProbes.cs:349-351` 要求首个回执：

```csharp
first.Receipt.Status == Rejected
&& !string.IsNullOrEmpty(first.Receipt.ReasonCode)
&& first.Receipt.CanonicalPayloadSha256 == plainDigest
```

但候选生产代码自 `9f223ef` 起在 `TableSessionHandler.cs:1481` 写入的是：

```csharp
string wireDigestHex = CanonicalPayloadDigest.Compute(payloadBytes).ToHex();
```

并由 `TableSessionHandler.cs:1542` 的 `MapReceipt(result, wireDigestHex)` 写入回执。`CanonicalPayloadDigest.Compute` 在 `Contracts/CanonicalPayloadDigest.cs:41,57-64` 先追加域前缀 `MAHJONG-V5-CANONICAL-COMMAND\0` 和长度，再做 SHA-256；`TablePayloadSha256.Compute`（`TableRuntime/TableDurableContracts.cs:110-121`）才是裸 SHA-256。两者不同域，不能相等。

`9f223ef` 的 diff 明确把旧值 `TablePayloadSha256` 换成 `CanonicalPayloadDigest`，并在 `SessionFlowTests.cs` 中把断言从 `Sha256Hex(payload)` 改为 `CanonicalPayloadDigest.Compute(payload).ToHex()`。B1 的 `NegativeProbes.cs` 没有随该生产修复同步，形成 **stale assertion**。

### 3.3 为什么日志只有 `[redacted]`

`SuiteRunner.cs:55-58` 抛出的消息按源码拼出的形如（原始日志未保留该文本）：

```text
Same-key different-bytes conflict did not preserve the original receipt:
FIRST_RECEIPT_NOT_EXACT_REJECTION (status=Rejected, reason=ROUND_MISMATCH, plainDigestMatch=False)
```

`Program.Redact` 是白名单，不是 sanitizer：该消息含空格、逗号、括号和 `plainDigestMatch=False` 等非白名单结构，`IsSafeDiagnostic` 返回 false，整段输出 `[redacted]`。因此 `suite.log` 只剩 `InvalidOperationException: [redacted]`，不能从日志反推具体分支；必须结合源码与探针。

### 3.4 评估其他早退分支

`RunSameKeyConflictAsync` 有四个早退点会返回 `Preserved=false`，另有最终判定；逐一评估，而不是把冻结标志当作逐调用证明：

| 分支 | 行 | 会置 `CanonicalDigestHex` | 与冻结证据一致性/评估 |
|---|---|---:|---|
| `NO_FIRST_RECEIPT_...` | 346 | 空 | 会令 `CanonicalReceiptIdentityVerified=false`；冻结值为 true，与该分支不符 |
| `FIRST_RECEIPT_NOT_EXACT_REJECTION` | 353 | `expectedDigest` | 与 `Canonical=true`、`ReceiptDigestDomainMismatch=false` 一致；独立探针也显示首回执 status/reason 正确、仅 digest 不匹配，因此最可能 |
| `SECOND_NOT_EXACT_CONFLICT_...` | 399 | `expectedDigest` | 可能；但独立探针实测第二请求返回可接受的 `REQUEST_ID_CONFLICT`，且该分支不会产生“原 receipt 未保留”的语义 |
| `ORIGINAL_NOT_FOUND` | 419 | `expectedDigest` | 可能；但独立探针实测 lookup 为 `Found` |
| 最终 `ORIGINAL_CHANGED` | 432 | `expectedDigest` 或空 | 会令 `ReceiptDigestDomainMismatch=true`（lookup digest 为空或不等于裸 SHA）；冻结值为 false，与该分支不符 |

**评估：** 冻结证据与源码共同支持 `FIRST_RECEIPT_NOT_EXACT_REJECTION` 为**最可能**分支，即首回执 digest 与 `plainDigest` 不等；独立探针也复现了这一比较失败。由于原始 `suite.log` 已脱敏且没有原进程字段追踪，不能仅凭冻结证据把 lookup 丢失、第二请求冲突码错误等其他分支绝对排除；但这些分支与冻结标志或独立探针结果不一致。

---

## 4. 精准探针（独立复现同一比较，不是原进程追踪）

### 4.0 证据分级

| 层级 | 本报告实际拥有 | 能支持什么 | 不能支持什么 |
|---|---|---|---|
| A. 原始运行直接证据 | 冻结 `suite.log`（已脱敏）、`suite.json`、`runner.log`、`wrapper-summary.json` | 失败阶段、native exit、聚合字段、断言顺序 | 未脱敏异常文本、原进程具体分支、原进程收到的回执字段 |
| B. 静态代码/提交证据 | `792ce41` 源码、`9f223ef` diff、`SuiteRunner`/`NegativeProbes`/`TableSessionHandler` | 候选代码在该路径上必然产生 raw-vs-domain 不等 | 原始运行确实执行到了哪一行 |
| C. 独立复现 | 本报告 §4.1–4.4 的隔离 PG/TLS 探针 | 同一代码路径和输入形状下的字段/分支结果 | 原始 suite 进程的实际字段与调用顺序 |

因此，后文的“最可能原因”是 A+B+C 的合成判断；C 不是 A 的直接替代，报告不把独立复现写成原始运行的未脱敏追踪。

### 4.1 环境与隔离

| 项 | 实测 |
|---|---|
| OS | Windows 10（本机 loopback） |
| .NET SDK | `D:\Unity\dotnet\dotnet.exe` 8.0.426 |
| PostgreSQL | **16.10**，`initdb -U mahjong_pg`，独立 data directory，独占 loopback 端口 **58971** |
| Lobby | 真实 `Mahjong.LobbyService.dll`，HTTPS `127.0.0.1:58972`，Postgres store |
| Table | 真实 `Mahjong.TableService.dll`，WSS `127.0.0.1:58973`，Postgres host registry |
| TLS | 每次新生成临时 root/leaf，客户端逐连接 `CustomRootTrust`；未改机器信任 |
| 票据 | 真实 Lobby HTTPS `POST /v1/table-tickets` 签发；未自签正票 |
| 角色分离 | `diag_lobby_owner/app`、`diag_table_owner/app`、`diag_observer`；应用角色无 super/createDB/createRole |
| 数据库 | `mahjong_diag_lobby` / `mahjong_diag_table`，owner 执行迁移，运行角色仅 DML |
| 清理 | `cleanup PASS`；58971/58972/58973 无监听；无本集群 postmaster；运行根删除；临时密钥/TLS 随根删除 |

### 4.2 复现命令与 native exit

候选源码副本：

```powershell
git archive --format=tar --output=build\p54b-diag\src-792ce41.tar 792ce41
tar -xf build\p54b-diag\src-792ce41.tar -C build\p54b-diag\src
```

隔离环境与精准探针：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\p54b-diag\harness\run.ps1
```

该脚本只在本树 ignored 目录内创建 PG/临时 TLS/探针工程副本；不调用或修改仓库
`tools/ci/verify-v5-https-admission.ps1`。本轮 native exit：

| 步骤 | native exit |
|---|---:|
| `git archive` / `tar -xf` | 0 / 0 |
| probe `dotnet build -c Release --no-incremental` | 0 |
| LobbyService / TableService / accepted keygen-certgen helper build | 0 / 0 / 0 |
| harness provisioning + probe | 0 |
| `cleanup PASS`（ownership marker、端口、postmaster） | 0 |
| 冻结的原始 B1 suite（只读证据，不是本轮重跑） | **1** |

### 4.3 探针做了什么

诊断 worker（本树 ignored 副本）复用 B1 的真实 Lobby/WSS 客户端，建立 4 个玩家、1 个房间、4 张 Lobby 签发正票并全部准入；然后对 seat 0 发送与 B1 完全同形的首个 `SubmitAction(round-b1-conflict, missing-decision-b1, DiscardTile, tileIds=[1])`，再以同一 `requestId` 发送 `SubmitTileMark`，最后在原 scope 下 `RECEIPT_QUERY`。只记录状态、稳定 reason code、合成 payload 的 digest 与布尔比较，不记录 ticket/token/私钥/证书/私牌。

### 4.4 实测结果

`conflict-probe.json`（本树 ignored，完整路径见 §7）：

```text
canonicalBytesLength = 107
plainSha256          = BD1F7C5463553B4F717DE5A85EA69207AA7AA08C2611C13C7BDB0C1875B86BC2
domainDigest         = 6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a
domainDiffersFromPlain = true

first.bodyCase                 = Receipt
first.status                   = Rejected
first.reasonCode               = ROUND_MISMATCH
first.canonicalPayloadSha256   = 6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a
first.digestEqualsPlain        = false
first.digestEqualsDomain       = true
first.evaluatedStateVersion    = 0
first.hasCommandVersion        = false

second.bodyCase                = Error
second.errorCode               = REQUEST_ID_CONFLICT
second.errorDetailCode         = REQUEST_KEY_CONFLICT

lookup.bodyCase                = ReceiptLookup
lookup.lookupStatus            = Found
lookup.receiptStatus           = Rejected
lookup.reasonCode              = ROUND_MISMATCH
lookup.canonicalPayloadSha256  = ""          (空)
lookup.digestEqualsDomain      = false
lookup.digestEqualsPlain       = false

b1ProbeCurrentFormula.firstDigestEqualsPlainDigest = false
b1ProbeCurrentFormula.firstStatusRejected          = true
b1ProbeCurrentFormula.firstReasonNonEmpty          = true
b1ProbeCurrentFormula.wouldPassFirstCheck          = false
b1ProbeCurrentFormula.preservedFields              = true
```

解读：

1. 在该次独立探针中，首个回执为 `Rejected/ROUND_MISMATCH`，status 与 reason 都满足 B1 的条件；唯一不满足的是 `digest == plainDigest`。
2. 首回执 digest 等于 `expectedDigest`（MJC1 域 digest），与生产按 `9f223ef` 修复后的代码一致；B1 的 `plainDigest` 是旧契约。
3. 第二请求返回 `REQUEST_ID_CONFLICT`（detail `REQUEST_KEY_CONFLICT`），B1 的 `exactConflict` 接受 `Error.Code == REQUEST_ID_CONFLICT`，该分支不会失败。
4. lookup 为 `Found/Rejected/ROUND_MISMATCH`，status/reason/evaluated/version 均保留；`preservedFields=true`。lookup digest 为空是既有的 B1-BLOCKER-03/契约缺口，不是本次首回执断言失败的原因。
5. 因此，冻结 suite 的 native 1 **最可能**是首回执 raw-vs-domain 误判；独立探针不支持“生产 receipt 丢失或冲突码错误”的解释，但由于原始日志已脱敏且无原进程字段追踪，不能把它写成对原始运行的逐调用证明。

探针 native exit：`0`（探针自身成功完成并记录证据；这不代表 B1 suite 通过）。完整 B1 suite 本轮 **NOT_RUN**，未用探针结果冒充 suite PASS。

---

## 5. 影响

| 项目 | 判定 | 说明 |
|---|---|---|
| 冻结原始证据审计 | **PASS** | 只读复核了 suite.json/log/runner/wrapper 与 review 文本 |
| 失败阶段/最可能调用路径 | **PASS（证据支持）** | 最可能为 `NegativeProbes.cs:349-361` 与 `SuiteRunner.cs:55-58`；原始日志已脱敏，未做原进程字段追踪 |
| 首回执 raw/domain 误判的独立复现 | **PASS** | 隔离 PG16.10/TLS/独占端口探针；复现同一代码路径与输入形状，不是原执行追踪 |
| 原始运行的直接分支追踪 | **NOT_RUN / 不可用** | 冻结 `suite.log` 仅 `[redacted]`，无原进程回执字段或未脱敏异常 |
| B1 suite 整体 | **FAIL** | 原冻结运行 native 1；本轮未重跑完整 suite，不能改写为 PASS |
| same-key 之后的负例矩阵 | **NOT_RUN** | 被首回执断言中断；跨账号/观战/篡改/异房/过期票均未在本轮执行 |
| B1-BLOCKER-03（Rejected lookup canonical digest） | **BLOCKED/OPEN** | lookup digest 为空；不能被首回执修复顺带宣告关闭 |
| 生产 `TableSessionHandler` 对本 native 1 的责任 | **无新增缺陷证据** | 生产按 `9f223ef` 统一首回执为 MJC1 域 digest；B1 断言未同步 |
| 批次验收 | **FAIL / 不可接受为 PASS** | suite native 1；必须修复测试设施并重跑，且单独裁决 canonical 缺口 |

影响边界：

- 该失败**最可能**是测试设施的假阴性，但它仍然真实阻断了批次验收；在修复并重跑前，不能把 B1 或 PR20 批次写成 PASS。
- 由于负例矩阵提前中断，不能推断跨账号、观战越权、篡改/异房/过期票负例通过；它们在本次冻结运行中未执行。
- 没有证据表明生产 scope、票据或回执持久化出现新的安全回归；本报告不把“测试误判”扩大成“生产无缺陷”的结论。

---

## 6. 精确修复范围

### 6.1 必须修（直接消除 native 1）

只改 B1 测试设施，不改生产：

```text
dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/NegativeProbes.cs
  - RunSameKeyConflictAsync 约 :335-361
  - 首回执 digest 比较从 plainDigest 改为 expectedDigest
    （即 firstPayload.Digest.ToHex() / CanonicalPayloadDigest.Compute(payload).ToHex()）
  - plainDigest 仅保留为诊断值，不再作为协议期望
```

要求：

- 不得把 `first.Receipt.CanonicalPayloadSha256` 与裸 SHA-256 比较；
- 不得删除 `Rejected`/`ReasonCode` 检查；
- 不得为通过而改成“digest 为空也接受”或注入伪 digest；
- 修复后首个回执应满足：`status=Rejected`、`reason` 非空、digest == `expectedDigest`。

### 6.2 必须同步修（证据标志，避免再次误导）

`NegativeProbes.cs` / `SuiteRunner.cs` 的标志语义：

- `CanonicalReceiptIdentityVerified` 在本次早退路径中由 `expectedDigest` 赋值，不能证明 lookup 返回 digest；应由**实际**首回执/lookup 的 `CanonicalPayloadSha256` 计算，或拆成 `FirstReceiptDigestVerified` 与 `LookupDigestAvailable` 两个字段。
- `ReceiptDigestDomainMismatch` 在早退路径固定为 `false`，会掩盖 lookup 空 digest；应按实际值计算。
- 这样即使首回执检查修好，报告也不会把 `CanonicalReceiptIdentityVerified=true` 误读为 lookup 已验证。

### 6.3 单独裁决（不要混入 native 1 修复）

`Rejected` 回执的 lookup digest 为空（探针实测），与 `9f223ef` 的注释和 `SessionFlowTests` 的断言一致：生产有意不在 lookup 转发 actor 的裸 digest，由 Client 用 outbox 保留的期望 digest 绑定。B1-BLOCKER-03 若要“首回执、lookup、outbox 三者相等”，需要生产/契约 owner 决定是：

1. 为 `Rejected` 持久化并返回 MJC1 域 digest；或
2. 明确接受 lookup 不返回 digest，Client 以 outbox 期望 digest 绑定，并把 B1-BLOCKER-03 降级为 classified/BLOCKED 的产品裁决。

不得用测试侧放宽或伪造 digest 关闭该项。

### 6.4 验收断言（修复后必须实跑）

在隔离 PG16.10、临时 TLS、独占端口上重跑原 `tools/ci/verify-v5-https-admission.ps1`：

- `SameKeyDifferentBytesPreservedOriginal=true`；
- 首回执 `status/reason/evaluated/version` 与 lookup 一致，首回执 digest == MJC1 domain；
- 第二请求精确 `REQUEST_ID_CONFLICT`；
- lookup `Found`，原 receipt identity 未被覆盖；
- 负例矩阵继续执行到 `negative_matrix_complete`；
- 若 lookup digest 仍为空，`CanonicalReceiptIdentityVerified` 必须如实为 false / BLOCKED，不能写成 PASS；
- suite/wrapper 的 native exit 必须由实际运行给出；不得用单测 44/44 或本探针成功替代 suite PASS。

---

## 7. 证据、清理与状态

### 7.1 本报告引用的可复核证据

本树 ignored 诊断根：

```text
build/p54b-diag/
```

关键文件（均为 ignored，不提交）：

```text
build/p54b-diag/src/                                      # git archive 792ce41 候选源码副本
build/p54b-diag/probe/                                    # 诊断 worker 副本（含 ConflictProbe.cs）
build/p54b-diag/harness/run.ps1                           # 独立隔离环境编排脚本
build/p54b-diag/logs-20261029-110309-8d498006d0284b659effcb748c4965c5/
  conflict-probe.json                                     # 精准探针结果
  runner.log                                              # 阶段与原生命令
  build-probe.log / build-lobby.log / build-table.log / build-accepted-helper.log
  lobby.stdout.log / table.stdout.log / postgres.log
```

哈希：

```text
conflict-probe.json     A6BA8E21242904030D26BFFA8521AE1C80EA52AB58914FFFF8A1EF6BBBAF9554
runner.log（诊断）      FA51A0DF8F9D1312D0DE5797E74644DA9A196398A24A95879BBE4B76DF6DBE37
build-probe.log         C6979CB47EDD1B51800C94D1C1001727BDAD0E052AC140596DB590DAF3F59F65
build-lobby.log         28DBB0CB6394231F4D0290B4110CDCCFBEE984BA649DD8F867E92FA68B486499
build-table.log         4CCFBD192958D25E04358132690FF6BDB53CCE2D758B062B54F28A9EA6231234
build-accepted-helper.log 63D2E7DBEF99C343B0CAD420221A749CA91DCBDA3F1E4713E3BD15AB6B7A79F5
```

原始冻结证据哈希见 §2；路径中的工作树/ GUID 不是秘密，未包含 ticket/token/私钥/证书。

### 7.2 清理核验

- `cleanup PASS ownership-verified cluster removed; ports free`。
- 58971/58972/58973 无监听；本诊断集群无残留 postmaster；运行根已删除。
- Lobby/Table 子进程已停止；临时 `material/keys`、`material/tls`、guest secrets 随运行根删除。
- 未改机器证书存储/信任；未触碰其他工作树中既有的 postgres 进程。
- 诊断源码副本、探针工程和日志都在 ignored `build/p54b-diag/`，不进入提交。

### 7.3 状态、交付路径与提交边界

- 本报告路径按复核声明的 expectedFiles 使用 `doc/v5-重构/V5-B1-INTEGRATED-FAILURE-DIAGNOSIS-20261029.md`；本轮不新增其他交付文件，不改其他范围。
- 提交前 `git status --porcelain=v1` 仅本报告；不提交 `build/`、不提交诊断脚本/日志/证书。
- 本报告是本次唯一交付文件；两轮本地提交均只涉及该报告。本地 commit，不 push/PR/merge/部署。
- V4 生产、G1 approved、G2–G5 未批准不变。

---

## 8. 判定矩阵

| 维度 | 判定 |
|---|---|
| 原始 suite native 1 的抛出点/阶段 | **PASS（证据支持的最可能路径）** |
| 直接原因（首回执 raw-vs-domain） | **PASS（源码 + 独立复现支持；非原进程追踪）** |
| 候选代码/独立探针的首回执与 lookup 契约 | **PASS（首回执域 digest 实测；lookup 空 digest 为契约既有缺口）** |
| 原始运行的直接分支追踪 | **NOT_RUN / 不可用** |
| 完整 B1 suite 重跑 | **NOT_RUN**（本轮只做精准探针） |
| 冻结批次 B1 suite | **FAIL（native 1）** |
| same-key 之后的负例矩阵 | **NOT_RUN**（被中断） |
| B1-BLOCKER-03 | **BLOCKED/OPEN** |
| 批次 acceptance | **FAIL / 不得宣称 PASS** |
| 本次诊断证据与清理 | **PASS** |

**最终结论：** `792ce41` 的 suite native 1 **最可能**为 B1 负例探针的旧 raw-SHA-256 断言；生产侧在 `9f223ef` 后使用 MJC1 域分离 digest，独立探针实测原 receipt 身份字段保留。该失败最可能是测试设施假阴性，未发现新的生产回归证据；但原始日志已脱敏且没有原执行追踪，不能把它写成逐调用定论。batch 仍为 **FAIL**，修复范围应限定在 B1 测试设施的首回执 digest 比较与证据标志，B1-BLOCKER-03 的 lookup digest 另立裁决；不得以本报告或单测重跑宣称批次 PASS。
