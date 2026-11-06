# V5-B1 首回执 digest 与 lookup 标志修复报告（2026-10-29）

状态：**隔离测试设施修复报告，不是批次验收，不是 G2–G5 通过。**
冻结候选：`a2a1bf9099ce6b607008cb017d7be60577166118`（PR20，未验收集成候选，**不是** accepted baseline）。
已验收基线：`origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
交付形式：基于 `8fd7c6c` 的本地提交，只包含本报告、可应用的 patch 与脱敏证据；未提交候选代码整体，未 push/PR/merge/部署。
生产状态：**V4 仍是唯一生产基线；G2–G5 未批准；B1-BLOCKER-03 仍 OPEN/BLOCKED。**

---

## 0. 结论摘要

- 在隔离目录中用 `git archive` 取出 `a2a1bf9` 的逐字节内容，先以**未打补丁**候选重跑原始 HTTPS 准入 suite，复现了冻结报告的 `native 1`：suite 退出 1、`B1-SUITE-FAIL ... [redacted]`、负例矩阵只完成 3 条 TLS 项、`CanonicalReceiptIdentityVerified=true` 与 `ReceiptDigestDomainMismatch=false` 仍是误导性早退值。
- 对 B1 测试设施做最小 patch 后，在隔离 PostgreSQL 16.10、临时 TLS、独占端口上重跑原 suite：**suite native exit 0，wrapper native exit 0，44/44 B1 tests，负例矩阵完整执行，11 条 assertions 到 `negative_matrix_complete`，4 条 DB negative facts 全部 `ok=true`，cleanup PASS。**
- 首个回执实测 `CanonicalPayloadSha256 = 6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a`，等于 `firstPayload.Digest.ToHex()`（MJC1 域分离 digest），不等于裸 SHA-256；`FirstReceiptDigestVerified=true`。
- lookup 回执 digest 仍为空：`LookupCanonicalDigestHex=""`、`LookupReceiptDigestAvailable=false`、`CanonicalReceiptIdentityVerified=false`、`ReceiptDigestDomainMismatch=true`。这是**如实记录**，不是新失败，也不是已关闭项；**B1-BLOCKER-03 保持 OPEN/BLOCKED**，需要生产/契约 owner 单独裁决。
- 本结果只是隔离测试设施证据，**不构成 B1 批次验收或 PR20 验收**。调用方仍需独立复核并把 patch 应用到实际 PR20 后重跑。

---

## 1. 边界、基线与冻结输入

| 项 | 精确值 |
|---|---|
| accepted baseline | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| 冻结候选 | `a2a1bf9099ce6b607008cb017d7be60577166118` |
| 冻结候选来源 | `git archive --format=tar ... a2a1bf9` → 本树 ignored `build/b1-fix-pr20/src` |
| 候选内容副本 HEAD（scratch） | `8b7b96545dbb15c560a142c115b08961da72c430`（未打补丁，仅本地 scratch git） |
| 打补丁树 HEAD（scratch） | `46e2c018f477f8399d29947af05b264ecfef5bb8`（仅本地 scratch git） |
| patch | `doc/v5-重构/patches/V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029.patch` |
| patch SHA-256 | `2E2A52D850FE8720F443EA21BF431DE2B7F6E04C7322FDCFE8B2A5861B3D8F29` |
| 本报告 | `doc/v5-重构/V5-B1-FIRST-RECEIPT-DIGEST-FIX-REPORT-20261029.md` |

说明：`git archive` 内容本身没有 `.git`；为满足原 suite 的只读 guard（`git -C $RepoRoot rev-parse HEAD`），在**隔离 ignored 目录**中初始化了本地 scratch git，仅用于运行和留证。日志中的 `head=` 是 scratch commit，不是把 `a2a1bf9` 冒充成该 commit；候选内容由 `git archive a2a1bf9` 和文件级 hash 对照确认。交付提交仍基于 `8fd7c6c`，不在主工作树提交候选代码。

范围限制：

- 只在 `D:\majiang-wt\b1-fix-ds-20261029` 工作树及其 ignored `build/b1-fix-pr20/` 隔离目录内操作；
- 未修改其他工作树、Unity/Resources/Burst、全局信任、生产代码、TLS 校验或统一 verifier；
- 未 push、PR、merge、部署；未调用跨工作树资源或删除他人 PG 进程/目录。

---

## 2. 缺陷与根因（与冻结诊断一致）

冻结诊断（PR20 `doc/v5-重构/V5-B1-INTEGRATED-FAILURE-DIAGNOSIS-20261029.md`）§3/§6 指出：B1 探针仍把首个回执的 `CanonicalPayloadSha256` 与**裸 SHA-256** 比较，而生产自 `9f223ef` 起写入的是 `CanonicalPayloadDigest.Compute(...)` 的 **MJC1 域分离 digest**。

未打补丁候选的本轮实测与之一致：

```text
suite.log:
  B1-SUITE-FAIL InvalidOperationException: [redacted]

suite.json:
  SameKeyDifferentBytesPreservedOriginal = false
  CanonicalReceiptIdentityVerified = true      # 早退路径用 expectedDigest 赋值
  ReceiptDigestDomainMismatch = false          # 早退路径固定值
  NegativeResults = 3（仅 TLS）
  Assertions 最后 = multi_client_confirmation_after_recovery；无 negative_matrix_complete

runner.log:
  stage suite: FAIL / suite native exit 1
  stage cleanup: PASS (exit 0)
```

因此本轮先复现失败，再只改 B1 测试设施；**未把生产 `TableSessionHandler`、TLS 校验、票据校验或 verifier 放宽。**

---

## 3. 最小 patch 内容

Patch 只改两个文件，均为 B1 测试设施：

```text
dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/NegativeProbes.cs
dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Worker/B1Models.cs
```

### 3.1 首回执改为 MJC1 域 digest 断言

`RunSameKeyConflictAsync()`：

- `plainDigest` 保留为**诊断值**，不再作为协议期望；
- 期望值改为 `firstPayload.Digest.ToHex()`（`CanonicalPayloadDigest`，MJC1 域分离）；
- 首个回执仍要求 `Status=Rejected`、`ReasonCode` 非空、digest 等于 MJC1 域 digest；
- 失败 detail 同时记录 `domainDigestMatch` 与 `plainDigestDiagnosticMatch`，便于区分“域不匹配”和“生产回执异常”。

### 3.2 首回执与 lookup 的 digest 分开记录

新增并输出以下实际观测字段（`B1Models.SuiteEvidence`）：

```text
FirstReceiptCanonicalDigestHex
LookupCanonicalDigestHex
FirstReceiptDigestVerified
LookupReceiptDigestAvailable
LookupReceiptDigestMatchesExpected
```

`ConflictProbeResult` 同时保存：

```text
FirstReceiptDigestHex
FirstReceiptDigestVerified
LookupDigestAvailable
LookupDigestMatchesExpected
LookupDigestHex
DigestDomainMismatch
CanonicalReceiptIdentityVerified =
    FirstReceiptDigestVerified && LookupDigestAvailable && LookupDigestMatchesExpected
```

因此：

- 首回执通过时 `FirstReceiptDigestVerified=true`，并记录实际 MJC1 digest；
- lookup digest 为空时 `LookupReceiptDigestAvailable=false`，`CanonicalReceiptIdentityVerified=false`，不再用早退值伪造 `true`；
- `ReceiptDigestDomainMismatch` 按实际观测计算；lookup 不可用时为 `true`，以避免再被误读为“已验证”；
- `NegativeFact.ExpectedCanonicalDigestHex` 使用**实际 lookup digest**；lookup 为空时为空，DB audit 仍记录 `actualDigest=""`，并在 `NegativeResults` 增加 `same_key_lookup_digest=UNAVAILABLE_B1_BLOCKER_03`。

### 3.3 明确未改的内容

- 不修改生产 `TableSessionHandler` / `CanonicalPayloadDigest` / `TablePayloadSha256`；
- 不修改 `tools/ci/verify-v5-https-admission.ps1`；
- 不修改 TLS 信任、证书校验、票据校验、数据库 schema 或回执持久化；
- 不删除 `Rejected` / `ReasonCode` 校验，不接受空 digest 作为“首回执通过”，不注入伪 digest。

---

## 4. 环境与命令

| 项 | 实测 |
|---|---|
| OS | Windows 10，本机 loopback |
| .NET SDK | `D:\Unity\dotnet\dotnet.exe` 8.0.426 |
| PostgreSQL | 16.10，`initdb -U mahjong_pg`，独立 data directory，独占端口 |
| 未打补丁运行 | PG 59171 / Lobby HTTPS 59172 / Table WSS 59173 |
| 打补丁运行 | PG 59471 / Lobby HTTPS 59472 / Table WSS 59473 |
| TLS | 每轮新生成临时 root/leaf，客户端逐连接 `CustomRootTrust`；未改机器信任 |
| 票据 | 真实 Lobby HTTPS 签发；未自签正票 |
| 数据库角色 | `b1_lobby_owner` / `b1_table_owner` 迁移，运行角色仅 DML；见 runner 的 role-separation |

核心命令（完整列表见 `evidence/.../commands.txt`）：

```powershell
git archive --format=tar --output=build\b1-fix-pr20\src-a2a1bf9.tar a2a1bf9099ce6b607008cb017d7be60577166118   # exit 0
tar -xf build\b1-fix-pr20\src-a2a1bf9.tar -C build\b1-fix-pr20\src                                              # exit 0

# 未打补丁：复现 native 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\ci\verify-v5-https-admission.ps1 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -PgPort 59171 -LobbyPort 59172 -TablePort 59173 `
  -Root ...\src\artifacts\b1-pg-unpatched-59171 `
  -LogDir ...\src\artifacts\b1-logs-unpatched-59171 -SuiteTimeoutSeconds 420
# wrapper exit 1；suite exit 1

# 打补丁：原 suite
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\ci\verify-v5-https-admission.ps1 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -PgPort 59471 -LobbyPort 59472 -TablePort 59473 `
  -Root ...\patched\artifacts\b1-pg-patched-59471 `
  -LogDir ...\patched\artifacts\b1-logs-patched-59471 -SuiteTimeoutSeconds 480
# wrapper exit 0；suite exit 0

# 新解压候选上验证 patch 可应用并编译
git apply --check doc\v5-重构\patches\V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029.patch   # exit 0
git apply doc\v5-重构\patches\V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029.patch           # exit 0
dotnet build ...\Mahjong.P5.4b.B1.HttpsAdmission.Worker.csproj -c Release --no-incremental --nologo -v:minimal -nr:false
# exit 0，0 warnings / 0 errors
```

---

## 5. 结果

### 5.1 未打补丁候选（复现）

| 项 | 值 |
|---|---|
| wrapper native exit | **1** |
| suite native exit | **1** |
| suite.log | `B1-SUITE-FAIL InvalidOperationException: [redacted]` |
| tests-b1 | 44/44 PASS（未能覆盖负例矩阵） |
| NegativeResults | 3（TLS only） |
| `SameKeyDifferentBytesPreservedOriginal` | `false` |
| `CanonicalReceiptIdentityVerified` | `true`（早退值，非 lookup 证据） |
| `ReceiptDigestDomainMismatch` | `false`（早退值） |
| manifest | count=212，sha256=`E477C226302C2B40DE64E4D51C0A6740ECCA31FF54306A5CF4F5AECC3D74E5EC` |
| cleanup | PASS（exit 0，ownership-verified cluster removed） |

### 5.2 打补丁候选（隔离原 suite）

| 项 | 值 |
|---|---|
| wrapper native exit | **0** |
| suite native exit | **0** |
| tests-b1 | **total=44 passed=44 failed=0 errors=0 skipped=0 notRun=0** |
| suite 断言 | **11**，最后为 `negative_matrix_complete` |
| NegativeResults | **10**（TLS 3 + same-key 2 + cross-account + spectator + tampered + foreign + expired） |
| NegativeFacts | **4**，DB audit 全部 `ok=true` |
| PG facts | `stateVersion=5 events=10 normal=1/APPLIED journal=1 eventRows=2 crash=1/APPLIED journal=1 eventRows=2 negFacts=4 settlementOutbox=0 lobbyConsumption=0 ledger=NOT_RUN` |
| 首回执 MJC1 digest | `6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a` |
| lookup digest | `""`（空） |
| `FirstReceiptDigestVerified` | `true` |
| `LookupReceiptDigestAvailable` | `false` |
| `CanonicalReceiptIdentityVerified` | `false` |
| `ReceiptDigestDomainMismatch` | `true` |
| manifest | count=371，sha256=`8B324B4BBBFAEBD336902B10A7CD5E2B73D405AF7A73F6CE39A9AEF14F1E8361` |
| cleanup | PASS（exit 0，ownership-verified cluster removed；ports free；no postmaster） |
| secrets | `secretMaterialRemoved=true`；无机器信任修改 |

负例矩阵实际结果：

```text
tls_untrusted_root=rejected_https_and_wss
tls_wrong_san=rejected_https_and_wss
tls_expired=rejected_https_and_wss
same_key_different_bytes=PRESERVED
same_key_lookup_digest=UNAVAILABLE_B1_BLOCKER_03
cross_account_query=SCOPE_MISMATCH
spectator_player_command=SCOPE_ROLE_NOT_AUTHORIZED
tampered_ticket=rejected
foreign_room_ticket=rejected
expired_lobby_ticket=rejected
```

### 5.3 如何解读 `ReceiptDigestDomainMismatch=true`

这不是新的生产失败，也不是已关闭的 identity：

- **首回执**已严格等于 MJC1 域 digest，`FirstReceiptDigestVerified=true`；
- **lookup** 按冻结契约不转发 actor 的裸 digest，`LookupCanonicalDigestHex=""`；
- 因而 `LookupReceiptDigestAvailable=false`、`CanonicalReceiptIdentityVerified=false`；
- `ReceiptDigestDomainMismatch=true` 表示“lookup 侧没有可验证的 MJC1 digest”，而不是“首回执 digest 错误”。

DB audit 中 `same_key_different_payload_and_kind` 的 `actualDigest` 也是空字符串，说明该 rejected receipt 的 `canonical_payload_digest` 在数据库中确实为空；这是 B1-BLOCKER-03 的直接证据，不是本 patch 生成的伪值。`ExpectedCanonicalDigestHex` 为空是因为 lookup/DB 侧实际没有 digest；首个回执的 domain digest 已由 suite 独立断言并记录，不能把 DB 侧的“无值”写成“已验证”。

---

## 6. B1-BLOCKER-03 状态

**BLOCKED/OPEN。** 本 patch 不改变生产/契约：

- lookup 仍不返回 canonical digest（`LookupCanonicalDigestHex=""`）；
- `CanonicalReceiptIdentityVerified=false`；
- `ReceiptDigestDomainMismatch=true`；
- 未注入伪 digest、未改成“空也算通过”、未修改 verifier 或生产回执持久化。

需要契约 owner 在以下两种方向中单独裁决（沿用冻结诊断 §6.3）：

1. 为 `Rejected` 持久化并返回 MJC1 域 digest；或
2. 明确接受 lookup 不返回 digest，Client 以 outbox 的期望 digest 绑定，并把 B1-BLOCKER-03 降级为 classified/BLOCKED 的产品裁决。

在裁决前，任何“首回执、lookup、outbox 三者相等”的断言都不应被写成通过。

---

## 7. 清理与证据

- 打补丁运行根 `...\b1-pg-patched-59471` 已由 suite 的 ownership-verified cleanup 删除；`material`（临时 TLS/私钥/guest secrets）不存在。
- 59471/59472/59473 均无监听；本集群无 postgres 进程残留；机器上其他 worktree 的 6 个 postgres 进程未被触碰。
- wrapper：`cleanupOk=true`、`secretMaterialRemoved=true`。
- 脱敏证据目录：`doc/v5-重构/evidence/V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029/`
  - `unpatched/`：复现 native 1 的 `runner.log`、`suite.log`、`suite.json`、`wrapper-summary.json`、manifest；
  - `patched/`：PASS 的 `runner.log`、`suite.log`、`suite.json`、`suite-summary.json`、`negative-facts-audit.json`、`db-facts.json`、`wrapper-summary.json`、manifest、tests 摘要；
  - `commands.txt`、`cleanup-check.txt`、`patch-apply-check.txt`、`SHA256SUMS.txt`。

证据日志中的绝对工作树路径已替换为 `<worktree>` / `<isolated-*-tree>`；未包含 ticket/token/私钥/证书。`SHA256SUMS.txt` 是本目录脱敏副本的哈希，不是原始 ignored 日志的哈希；原始日志路径和运行输出已在上表和 `commands.txt` 中给出。

---

## 8. 判定矩阵

| 维度 | 判定 |
|---|---|
| 冻结候选 native 1 的复现 | **PASS（本轮实测 native 1，与冻结诊断一致）** |
| 首回执对 MJC1 域 digest 的断言 | **PASS（`6f99...`，`FirstReceiptDigestVerified=true`）** |
| 负例矩阵完整执行 | **PASS（10 条结果，4 条 DB facts，到 `negative_matrix_complete`）** |
| 原 HTTPS 准入 suite（打补丁、隔离环境） | **PASS（suite exit 0，wrapper exit 0）** |
| 44/44 B1 tests | **PASS** |
| patch 在新解压候选上可应用/编译 | **PASS（apply check/apply/build 均 exit 0）** |
| 清理与 secrets | **PASS（cleanup 0，material removed，ports free）** |
| lookup canonical digest | **BLOCKED/OPEN（B1-BLOCKER-03，空 digest 如实记录）** |
| B1 批次验收 | **NOT PASS / 未宣称**；隔离 suite 通过不等于批次验收 |
| PR20 验收 | **NOT PASS / 未宣称**；调用方独立复核并应用到实际 PR20 后重跑 |
| G2–G5 | **NOT_RUN / 未批准**；V4 仍是生产 |

**最终结论：** 冻结候选的 native 1 在本轮被独立复现；最小 B1 测试设施 patch 将首回执断言改为实际 MJC1 域 digest，并把首回执/lookup 的 digest 标志拆开如实记录，使原 HTTPS 准入 suite 在隔离 PG 16.10/TLS/独占端口上完成全部负例并返回 0。B1-BLOCKER-03 仍为 OPEN/BLOCKED，lookup digest 为空没有被伪装成通过。本结果仅是隔离测试设施证据，不是批次验收，也不改变 V4 生产基线与 G2–G5 状态。
