# V5 B1 候选补丁组合兼容性与行为复核（2026-10-30）

状态：**独立复核报告。对“已复核 B1 候选补丁 + 精确候选 00cd02d”的组合兼容性与实际行为判定为 PASS（仅限本次组合树与 B1 范围），另有一项 MINOR 补丁元数据发现。本报告不是 PR20 验收，不是 B1 批次验收，不是 G1 重审，也不是 G2–G5 通过；V4 仍是唯一生产基线。**

- 已验收基线：`origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，tree `61f50856aedd78e0f8609e282baa8313ae8f2a14`。
- 精确候选：`00cd02d80172ebd13a2945e4e8bacfe486ae0c21`（PR20 祖先，tree `72502a19a808738f9e2c801f83e47d46dc07761d`，parent `2bb6e681...`）。
- 未验收 PR20 头：`0e5d77a9f8f4052e6b98c222da7ba94229e5ffe1`（tree `d38192d46525a610c043ff57c6e61dac93d3ca73`）。
- 本报告只新增一个提交文件；候选代码只经 `git archive` 进入 ignored `build/`，没有 checkout/merge/提交候选源码。
- 未 push、未创建/修改 PR、未触发 CI、未改 machine trust、未运行 Unity、未写其他工作树、未使用 delegation。

---

## 0. 结论摘要

| 问题 | 结论 | 关键证据 |
|---|---|---|
| 三个已复核 B1 候选补丁能否与精确候选 `00cd02d` 组合？ | **YES** | first/heartbeat 已在候选树内；lookup 对 `00cd02d` `git apply --check` exit 0，正向应用 exit 0。 |
| 组合后是否保持首回执、lookup、heartbeat 的预期行为？ | **YES** | 组合树 173/173 TableService、454/454 TableRuntime、82/82 Client.Wss、54/54 B1 gate；真实 B1 wrapper 13/13 阶段 PASS、54/54 tests、suite PASS。 |
| lookup digest 是否真的从空变为原始 MJC1 digest？ | **YES（仅新行）** | `FirstReceiptCanonicalDigestHex == LookupCanonicalDigestHex == 6f99...250a`；`CanonicalReceiptIdentityVerified=true`；DB fact `same_key_different_payload_and_kind` 的 `actualDigest` 同值。 |
| heartbeat 是否仍在真实多客户端场景下绑定正确？ | **YES** | `B1-HEARTBEAT-OK 0:3/3,1:3/3,2:3/3,3:3/3,-1:3/3`；恢复后 `0:6/6,2:6/6,3:6/6,-1:6/6`；binding mismatch/close 为 0。 |
| 补丁元数据是否完全精确？ | **NO — MINOR** | lookup patch 的 `index old..new` 前缀与 `00cd02d`/组合树的实际 blob 不一致；见 §2.4。功能内容与报告中的 patched SHA256 完全相同，故不构成功能阻断。 |
| 是否有测试被跳过、断言被放宽、auth/trust 被绕过？ | **NO** | 所有运行 `Skipped=0`；lookup 补丁只加可选载荷 digest，不读客户端 digest，不改 scope/ticket/TLS；负例实际失败于修复前。 |
| PR20 / B1 批次 / G2–G5 是否批准？ | **NO / NOT_RUN** | 本报告只给组合候选证据；PR20 仍由 caller 独立复核与集成。 |
| 是否证明生产跨实例票据原子性？ | **NO — NOT CLAIMED** | B1 为 loopback、单实例 TableService；`db-facts.json` 明示 `lobbyTicketConsumptionRows=0`，TableService 使用 in-memory one-time jti registry。 |

**一句话结论：** 在 `00cd02d` 上应用 lookup digest patch 后，三者组合可构建、可运行，首回执与 lookup 的 MJC1 digest 一致，heartbeat 绑定在真实 B1 五客户端场景中保持正确；未发现功能不兼容或安全回退。唯一缺陷是 lookup patch 的 git index 元数据陈旧，需作为 MINOR 记录并修正可复现哈希表。

---

## 1. 基线、候选与边界

| 项 | 精确值 |
|---|---|
| accepted base | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| base tree | `61f50856aedd78e0f8609e282baa8313ae8f2a14` |
| exact candidate | `00cd02d80172ebd13a2945e4e8bacfe486ae0c21` |
| candidate tree | `72502a19a808738f9e2c801f83e47d46dc07761d` |
| candidate parent | `2bb6e681c26060407542f09bc2495c4b30b2c7c0` |
| PR20 tip | `0e5d77a9f8f4052e6b98c222da7ba94229e5ffe1` |
| PR20 tip tree | `d38192d46525a610c043ff57c6e61dac93d3ca73` |
| `00cd02d` 是否为 `0e5d77a` 祖先 | **YES**（`git merge-base --is-ancestor 00cd02d 0e5d77a` exit 0） |
| `8fd7c6c` 是否为 `00cd02d` 祖先 | **YES**（exit 0） |

`00cd02d → 0e5d77a` 的差异只有 5 个文档，`dotnet/` 与 `tools/` 零差异：

```text
A doc/v5-重构/V5-B1-LOOKUP-DIGEST-FIX-20261030.md
A doc/v5-重构/V5-LOCALHOST-CALLER-113-115-INVESTIGATION-20261030.md
A doc/v5-重构/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.md
A doc/v5-重构/patches/V5-B1-LOOKUP-DIGEST-FIX-20261030.patch
A doc/v5-重构/patches/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch
```

因此 PR20 头与 `00cd02d` 的**生产/测试源码逐字节相同**；PR20 头只是收集了 lookup patch 的文档，**没有把 lookup 修复应用到源码**（`0e5d77a` 的 `TableSessionHandler.cs` 仍为 `39bfce0`，grep `wire.CanonicalPayloadSha256 = lookup.Digest` 无匹配）。精确候选 `00cd02d` 本身也不含 lookup patch 文件（`git cat-file -e` exit 128）。

边界：

- 候选树用 `git archive --format=tar 00cd02d dotnet doc/v5-重构 tools global.json` 取出到 ignored `build/b1-combination/cand-00cd02d/`；未在候选树中 checkout、未把候选文件加入主仓库索引。
- B1 wrapper 的 `stage guard` 记录 `head=3065107580a028f0b6d89d04d5496e83226734f0`；这是 ignored 组合树内为满足只读 guard 而创建的 **scratch commit**，不是 `00cd02d`。候选/组合内容身份以 §2 的 tree/blob/文件 SHA256 为准，不能把该 scratch commit 当作候选提交。
- 所有修改/构建只在 ignored `build/` 内；主工作树 `git status --porcelain=v1` 在写本报告前为空。
- 未修改 `AGENTS.md`、任务书、复核协议、统一 verifier、CI、Unity、TLS/trust、PG 数据目录或其他工作树。

---

## 2. 补丁身份、适用性、精确哈希与顺序

### 2.1 补丁字节身份

| 补丁 | bytes | SHA-256 |
|---|---:|---|
| `V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029.patch` | 11,167 | `2E2A52D850FE8720F443EA21BF431DE2B7F6E04C7322FDCFE8B2A5861B3D8F29` |
| `V5-B1-HEARTBEAT-BINDING-FIX-20261030.patch` | 71,072 | `16AE6536E406444407DEC9BB71EB34C5C2B44B2B3B3CE15B46A5F89F38EB1643` |
| `V5-B1-LOOKUP-DIGEST-FIX-20261030.patch` | 37,146 | `58AAC2B41DE1FB67FA91193C93FA396FDB8238072287D5BD744138025063D757` |

三个 SHA256 与各自报告完全一致。lookup patch 只存在于 `0e5d77a`（及更晚的 PR20 文档提交），不在 `00cd02d`；它可应用于 `00cd02d`。

### 2.2 精确候选的已有状态

在 `00cd02d` 的树中：

- first-receipt 补丁**已经应用**：`NegativeProbes.cs = 46270df...`（first postimage）；`B1Models.cs` 先经过 first 的 `0c838b4...`，再被 heartbeat 改为 `5a77794...`。
- heartbeat 补丁**已经应用**：10 个文件的 postimage 与候选树逐一相同，包括 `TableSessionHandler.cs = 39bfce0...`、`B1Models.cs = 5a77794...`、`HeartbeatLivenessGateTests.cs = e4d7285...`、`HeartbeatBindingAuditChannel.cs = f9fd985...`、`ServerHeartbeatBindingTests.cs = 927526f...`。
- lookup 补丁**未应用**：候选 `TableActor.cs = 2b819e7...`、`TableRuntimeContracts.cs = 7b92df0...`、`TableCommandMapper.cs = b5e100c...`、`TableSessionHandler.cs = 39bfce0...`；候选没有 `B1ReceiptDigestTests.cs` 与 `B1ReceiptDigestPersistenceTests.cs`。

### 2.3 适用性（在 fresh `00cd02d` 提取 + scratch Git 中实跑）

| 补丁 | `git apply --check` | 解释 |
|---|---:|---|
| first-receipt | **exit 1** | 已应用；重放失败是预期，不是缺陷。 |
| heartbeat | **exit 1** | 已应用；重放失败是预期，不是缺陷。 |
| lookup | **exit 0** | 可应用到精确候选；实际 `git apply` exit 0。 |

反向/正向顺序验证：

```powershell
git apply -R --check heartbeat.patch   # exit 0
git apply -R heartbeat.patch           # exit 0
# TableSessionHandler -> a97655eda5107550de3032303211d579ab2609f8
# B1Models            -> 0c838b489b853afbb452f16678329ec5b19296d3
git apply -R --check first.patch       # exit 0
git apply -R first.patch               # exit 0
# B1Models            -> b20b2a12051f80a961f7a241fb0de18b68b27ff4
# NegativeProbes      -> 19c532de3ddb1bc7e10a1ff9e34125b09bb4fa57
git apply first.patch                  # exit 0
# B1Models -> 0c838b4..., NegativeProbes -> 46270df...
git apply heartbeat.patch              # exit 0
# B1Models -> 5a77794..., TableSessionHandler -> 39bfce0...
```

这证明 first → heartbeat 的严格顺序：first 的唯一中间 postimage `B1Models=0c838b4...` 正是 heartbeat 的 preimage；反向必须先撤 heartbeat、再撤 first，正向必须先 first、再 heartbeat。

lookup 与 heartbeat 的顺序：两种顺序都 exit 0，且最终内容收敛到同一哈希：

- heartbeat → lookup：`TableSessionHandler = 83ddd421fa71a81ac882583dc120f80b3be7e9d3`，`B1Models = 5a77794...`。
- lookup → heartbeat：同样得到 `TableSessionHandler = 83ddd42...`，`B1Models = 5a77794...`；`git diff --no-index` exit 0（无差异）。

### 2.4 声明的 index 哈希 vs 候选实际哈希（MINOR 发现 D-01）

lookup patch 的 `index old..new` 前缀来自作者生成补丁时的树，**不是** `00cd02d` 的 blob；这些 old blob 在本仓库对象库中不存在（`git rev-parse` 无法解析）。实际组合内容如下：

| 文件 | patch 声明 `index` | candidate pre（full） | 组合后 actual（full） | 组合后 SHA256 |
|---|---|---|---|---|
| `TableActor.cs` | `cf10689..b4107d9` | `2b819e789484ffa0bf73b4adc8cbda342825f896` | `615e8fdeb570368940150367ccf011b8bdb69504` | `7D7BF03E...59BE3A` |
| `TableRuntimeContracts.cs` | `4d99a60..9f9cbdf` | `7b92df03895aa4ec0bebef667cdcb4055870b65f` | `fa13d57fbe55900ed3ec4aa6dbb9afc436487e10` | `A06ADE07...324FAA` |
| `TableCommandMapper.cs` | `ec26947..b3aa2c7` | `b5e100c035db978875538b0106cea69615dc8259` | `3d6b8a8e1b2a1d6b282ed90c8416f7aa26c0a998` | `3E516389...0A974B` |
| `TableSessionHandler.cs` | `14df43c..07edb18` | `39bfce00d7acab12c8243b5a79e28607ed4656c0` | `83ddd421fa71a81ac882583dc120f80b3be7e9d3` | `7E8DF6F4...3FC36E` |
| `TableDurableTestSupport.cs` | `8c05767..c2187da` | `fea2813b78c1600fd0384ef1d2b91d26b952976e` | `4e8e647025cc9606bf09d951476e3d3448a409ec` | `366BD3FE...DDA1F1` |
| `SessionFlowTests.cs` | `8832b3c..3754c8b` | `d2440b0dd67b8adfea11a542821b753d2581a33f` | `9c027881a69f29f6da8534be4d0e68e1909ecaba` | `5133AE80...DAEAC9` |
| `B1ReceiptDigestPersistenceTests.cs` | `0000000..b0cf835` | 不存在 | `b0cf8350ebbb54fa3a0e7d8c3d04e1863aa0f771` | `FEEB8B4E...3839D1` |
| `B1ReceiptDigestTests.cs` | `0000000..07ad1a2` | 不存在 | `07ad1a2078d681d72978f44ac794a89b12b63af2` | `E4945C1C...B27055` |

关键点：

1. 两个新文件 postimage 与声明完全一致；
2. 六个修改文件的 patch 声明的 postimage **不能**在 `00cd02d` 上复现（因为补丁是在更早/未含 heartbeat 的树上生成的）；
3. 但组合后四个生产文件的 SHA256 `7D7BF03E...`、`A06ADE07...`、`3E516389...`、`7E8DF6F4...` 与 lookup-fix 报告 §6 的 patched SHA256 **逐字相同**；
4. 因此这是**补丁头元数据陈旧**，不是组合内容错误。它不影响 `git apply` 和运行结果，但会影响“按 patch 头验哈希”的审计可复现性。

**判定：D-01 MINOR / traceability only。** 不因此改写历史提交，也不因此放松任何断言；caller 若要按 blob 哈希验收，应要求补丁作者重新生成 index 头或提供“candidate-relative pre/post + 内容 SHA256”清单。

### 2.5 顺序结论

- **first → heartbeat 是强制的**（B1Models 的中间 blob 是 heartbeat 的 preimage）。
- **lookup 与 heartbeat 可交换**；两者都应用后内容相同。
- **lookup patch 的声明 index 不是 candidate-relative**，不要用 `index` 头当作 `00cd02d` 的验收哈希；用 §2.4 的 actual 全哈希或文件 SHA256。

---

## 3. 组合树：构建与 focused 测试（正向）

组合树 = fresh `00cd02d` + `lookup.patch`，位于 ignored `build/b1-combination/work-lookup`。

| 命令（原样，工作目录为组合树） | native exit | 结果 |
|---|---:|---|
| `dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --nologo -v:minimal` | **0** | 0 warnings / 0 errors |
| `dotnet test ... --no-build`（TableService full） | **0** | **173 / 173 passed**, 0 failed, 0 skipped |
| `dotnet test ... -- --filter-class Mahjong.TableService.Tests.B1ReceiptDigestTests` | **0** | **3 / 3 passed** |
| `dotnet test ... -- --filter-class Mahjong.TableService.Tests.ServerHeartbeatBindingTests` | **0** | **3 / 3 passed** |
| `dotnet test ... -- --filter-method '*CommandReceipt_ReplayAndDurableLookup*'` | **0** | **1 / 1 passed**（强化后的 lookup 断言） |
| `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/... -c Release` | **0** | 0 warnings / 0 errors |
| `dotnet test ... --no-build`（TableRuntime full） | **0** | **454 / 454 passed**, 0 failed, 0 skipped |
| `dotnet test ... -- --filter-class Mahjong.TableRuntime.Tests.B1ReceiptDigestPersistenceTests` | **0** | **1 / 1 passed** |
| `dotnet build dotnet/tests/Mahjong.Client.Wss.Tests/... -c Release` | **0** | 0 warnings / 0 errors |
| `dotnet test ... --no-build`（Client.Wss full） | **0** | **82 / 82 passed**, 0 failed, 0 skipped |
| `dotnet build dotnet/tests/Mahjong.P5.4b.B1.HttpsAdmission.Tests/... -c Release` | **0** | 0 warnings / 0 errors |
| `dotnet test ... --no-build`（B1 gate unit） | **0** | **54 / 54 passed**, 0 failed, 0 skipped |

计数与报告对照：

- `TableService` 从候选的 170 增至 **173**（lookup 新增 3 个测试）；
- `TableRuntime` 从候选的 453 增至 **454**（新增 1 个持久化测试）；
- `Client.Wss` 保持 **82**；
- B1 gate unit 保持 **54**（44 frozen + 10 heartbeat gate）；
- 全部 `Skipped=0`、`NotRun=0`、`Errors=0`。

---

## 4. 负向控制（不是只跑绿）

### 4.1 未应用 lookup patch 的候选

在 fresh `00cd02d` 上只复制新测试文件（生产源码不变），构建 exit 0：

| 命令 | native exit | 结果 |
|---|---:|---|
| `dotnet test ... -- --filter-class Mahjong.TableService.Tests.B1ReceiptDigestTests` | **1** | **3 failed / 0 passed / 3 total** |
| `dotnet test ... -- --filter-method '*CommandReceipt_ReplayAndDurableLookup*'` | **1** | **1 failed / 0 passed / 1 total** |
| `dotnet test ...`（full TableService） | **1** | **4 failed / 169 passed / 173 total** |

实际失败为 `Expected: "<MJC1 digest>" / Actual: ""`，即 lookup 侧空 digest；不是编译失败、不是 skip。失败测试：

- `AppliedLookup_PreservesOriginalDigest_AndSameKeyConflictKeepsIt`
- `RejectedLookup_PreservesOriginalDigest_AndUnknownResultStaysDigestFree`
- `LookupScopeIsolation_AndSameAccountHistoricalScopePreservesDigest`
- `SessionFlowTests.CommandReceipt_ReplayAndDurableLookup`

### 4.2 heartbeat pre-fix 负向控制

在组合树上仅反向应用 heartbeat 的 `TableSessionHandler.cs` hunk（保留 lookup 修改），结果 blob `69bd989...`；构建 exit 0。运行 `ServerHeartbeatBindingTests`：

- native exit **1**
- **3 failed / 0 passed / 3 total**
- 失败均为 `Expected: "<admitted session id>" / Actual: ""`，即 server PING 丢失绑定身份。

这证明组合树中的 3/3 heartbeat 正向结果确实来自 heartbeat 修复，而不是测试环境偶然通过。

### 4.3 断言强度

- lookup 测试断言 immediate receipt digest、lookup inner digest、lookup top-level digest 三者相等，并验证 same-key conflict 不覆盖原值；
- unpatched 上该断言实际失败（空字符串），不是“空也算通过”；
- 没有删除 `Rejected`/`ReasonCode`、scope、ticket、TLS 检查；
- 没有新增 skip，没有改统一 verifier。

---

## 5. 真实隔离 PostgreSQL

环境：本机 Windows 10，PostgreSQL **16.10**，独立 `initdb` 集群，端口 **61271**，`--auth-local=trust --auth-host=trust`，独立 data directory；使用临时 32-byte payload key；运行角色 `mahjong_table_runtime` 由 migration 创建，admin 为 `mahjong_pg`。未改机器信任，未触碰其他工作树的 PG 进程/数据。

| 命令 | native exit | 结果 |
|---|---:|---|
| `initdb -D ... -U mahjong_pg --auth-local=trust --auth-host=trust` | **0** | 16.10 cluster created |
| `pg_ctl start` / 直接 `postgres.exe` 启动 + `pg_isready` | **0** | accepting connections |
| `dotnet build dotnet/tests/Mahjong.Infrastructure.Tests/... -c Release` | **0** | 0 warnings / 0 errors |
| `dotnet test ... --no-build`（Infrastructure full） | **0** | **42 / 42 passed**, 0 failed, 0 skipped |
| `dotnet test ... -- --filter-method '*ReceiptOptionalFields_ArePreservedForAppliedRecordedAndRejected*'` | **0** | **1 / 1 passed** |
| `dotnet test ... -- --filter-method '*ReceiptOptionalFields...*'` 后查询 `table_store.request_receipt` | **0** | 至少 1 行 `REJECTED`，`canonical_payload_digest` 为 **32 bytes**，hex `e3baef3c...428a527c` |

`PostgresR1RegressionTests.ReceiptOptionalFields_ArePreservedForAppliedRecordedAndRejected` 明确覆盖 Applied/Recorded/Rejected 的 `canonicalDigest` round-trip，并在真实 PG 上通过；这验证了 lookup patch 没有破坏既有 nullable `canonical_payload_digest` 列的持久化语义。

清理：

- `pg_ctl stop -m fast` exit **0**；
- `pg_isready` 61271 在停止后返回 no response（exit 2）；
- 删除本集群 data directory；删除后 `Test-Path` false；
- 61271/61371/61372/61373 均无监听；
- 无本任务路径下的 `postgres.exe` 残留。

---

## 6. 端到端 B1：HTTPS Lobby + WSS TableService + PostgreSQL

组合树 + 真实 LobbyService HTTPS + 真实 TableService WSS + 真实 PG；原始 B1 wrapper 未修改，端口 61371/61372/61373，`SuiteTimeoutSeconds=480`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools\ci\verify-v5-https-admission.ps1 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -PgPort 61371 -LobbyPort 61372 -TablePort 61373 `
  -Root ...\artifacts\b1-combined-61371 `
  -LogDir ...\artifacts\b1-combined-logs-61371 `
  -SuiteTimeoutSeconds 480
```

结果：

| 项 | 实测 |
|---|---|
| wrapper native exit | **0** |
| 阶段 | **13 / 13 PASS** |
| B1 unit tests | **54 / 54 passed**, 0 failed, 0 skipped, 0 notRun |
| suite 结果 | `P54B-B1-HTTPS-ADMISSION-PASS` |
| suite marker | `B1-SUITE-OK room=room_01a18a0da32b7c93be4aefc4bb168839 normalVersion=1 crashVersion=2 recovered=True` |
| manifest | count=**374**, sha256=`86C25B2B2A47BFCB90033D7E80CFF5AC057255A4552B7C955997A6F58394FCCC` |
| cleanup | PASS：`ownership-verified cluster removed; ports free; no postmaster` |
| secrets | `secretMaterialRemoved=true`；无机器信任修改 |

### 6.1 首回执 / lookup / heartbeat 实际值

`suite.json`：

```text
FirstReceiptCanonicalDigestHex   = 6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a
LookupCanonicalDigestHex         = 6f99fc869c3abed62d2a1c0ca103d23113d650ba433e0bcb7f5e601d9d0d250a
FirstReceiptDigestVerified       = true
LookupReceiptDigestAvailable     = true
LookupReceiptDigestMatchesExpected = true
CanonicalReceiptIdentityVerified = true
ReceiptDigestDomainMismatch      = false
SameKeyDifferentBytesPreservedOriginal = true
NormalReplaySameReceipt          = true
Blocked                          = false
```

负例矩阵（10 条）：

```text
tls_untrusted_root=rejected_https_and_wss
tls_wrong_san=rejected_https_and_wss
tls_expired=rejected_https_and_wss
same_key_different_bytes=PRESERVED
same_key_lookup_digest=PRESENT
cross_account_query=SCOPE_MISMATCH
spectator_player_command=SCOPE_ROLE_NOT_AUTHORIZED
tampered_ticket=rejected
foreign_room_ticket=rejected
expired_lobby_ticket=rejected
```

DB facts（4/4 `ok=true`）：

- `same_key_different_payload_and_kind`：`expectedCount=1 actualCount=1 expectedJournalCount=0 actualJournalCount=0 expectedStatus=Rejected actualStatus=REJECTED expectedDigest=6f99...250a actualDigest=6f99...250a`；
- `cross_account_receipt_query`、`spectator_player_command`、`invalid_ticket_room` 均为 0 行副作用。

heartbeat 两阶段（`suite.log`）：

```text
B1-HEARTBEAT-OK 0:3/3,1:3/3,2:3/3,3:3/3,-1:3/3 newPingsMin=3 newPongsMin=3 elapsedMs=15121 configuredMs=10000 actualPingMs=5000
B1-HEARTBEAT-POST-RECOVERY-OK 0:6/6,2:6/6,3:6/6,-1:6/6 newPingsMin=6 newPongsMin=6 elapsedMs=30093
```

即：早期全五人阶段每客户端 3 个新 PING/PONG，恢复后四个在线客户端每客户端 6 个；配置 10,000 ms，实际发送周期 5,000 ms（`max(250, interval/2)`）；binding mismatch 与意外 close 均为 0。

### 6.2 与报告声明的对照

- first-receipt 报告：首回执 MJC1 digest `6f99...250a`，lookup 仍空、`CanonicalReceiptIdentityVerified=false`；本组合运行把 lookup 也变为同值，其它字段保持。
- lookup-fix 报告：其 patched 生产源 SHA256 与本次组合树逐字相同（§2.4），其单测 173/173、454/454 与本轮一致；其未跑的完整 B1 由本轮独立补上。
- heartbeat 报告：其 unpatched 3 个 server 失败的空 session 形状、54/54 B1 unit、真实五客户端 liveness 结论与本轮一致；本轮还叠加了 lookup 修复，仍保持 heartbeat 绑定。
- 54c CI 审计：本报告没有使用其“2,641 green”作为本轮证据，也没有把旧 `40932995962` LocalHost 失败写成已修复；该历史失败仍是 caller 的独立事项。

---

## 7. 代码与信任边界复核

lookup patch 的实际代码只做四类事：

1. **TableRuntimeContracts**：给 `TableCommandEnvelope` / `TableCheatCommandEnvelope` 增加可选 `PayloadDigest? CanonicalPayloadDigest`，默认 null；这是进程内构造面，不是 wire 字段。
2. **TableCommandMapper**：从保留的原始 `payload.AsSpan()` 计算 `CanonicalPayloadDigest.Compute(...)`，同时保留 `TablePayloadSha256.Compute(...)`；前者只用于回执身份，后者仍是 idempotency/conflict key。
3. **TableActor**：把 envelope 的 canonical digest 传入 ordinary/cheat accepted 与 rejected receipt 构造点；internal commands 仍为 null。
4. **TableSessionHandler**：`ReceiptLookup.Found` 时把 `lookup.Digest` 写入 lookup-level `CanonicalPayloadSha256`；NotFound/Unknown 保持空。

未改：wire proto、SQL migration、`TablePayloadSha256`、`Matches`、receipt-first 顺序、scope/account/role 检查、ticket/admission、TLS/StrictRootTrust、client outbox、verifier、CI、Unity。

安全判定：

- digest 只由服务端从原始 wire canonical-payload 字节计算，**不读客户端 digest**，不从 protobuf 重新序列化；
- same-key different-bytes 仍是 `REQUEST_ID_CONFLICT`，原 receipt 与 digest 不被覆盖；
- foreign scope 仍 `SCOPE_MISMATCH`，other-account own scope 仍 `NotFound`，同账号 historical scope 仍可查；
- NotFound/Unknown 不伪造 digest；
- legacy NULL 行不 backfill、不改 `NOT NULL`；
- 没有 bypass auth、ticket、TLS、machine trust，也没有删除/弱化探针。

---

## 8. 发现（完整清单）

### D-01 MINOR（补丁元数据 / traceability，非功能阻断）

**位置**：`doc/v5-重构/patches/V5-B1-LOOKUP-DIGEST-FIX-20261030.patch` 的 `index old..new` 行。

**复现**：

```powershell
git apply --check --verbose lookup.patch   # exit 0
git hash-object dotnet/src/Mahjong.TableService/TableSessionHandler.cs
# 83ddd421...（组合后）
# patch 声明 14df43c..07edb18，与 candidate 39bfce0 不一致
git rev-parse 14df43c                        # unable to resolve
```

**实际**：六个修改文件的 old blob 前缀不在本仓库；new blob 前缀与组合树不符；新文件 postimage 相符。组合后四个生产文件的 SHA256 与 lookup-fix 报告 §6 的 patched SHA256 完全相同。

**期望**：补丁头应相对精确候选生成，或附带 candidate-relative pre/post 全哈希；`index` 行不应被误读为 `00cd02d` 的验收哈希。

**修复要求**：仅重生成补丁元数据/清单，不改生产逻辑、不删除测试、不放宽断言；不得用 `git apply --3way` 或 `--fuzz` 绕过。

**验收断言**：在 fresh `00cd02d` 上 `git apply --check` exit 0；组合后逐文件 full blob 与提交的 candidate-relative 清单一致；两个新文件 postimage 继续一致。

### D-02 INFO（已应用状态）

first-receipt 与 heartbeat 在 `00cd02d` 中已经应用，正向 `git apply --check` 必然 exit 1。两者的报告冻结的是更早候选（first: `a2a1bf9...`；heartbeat: `928379f...`）。对精确候选应记录“已经应用”，不要重复 patch；本轮用反向/正向 round-trip 证明了 exact postimage。

### D-03 INFO（PR20 头未应用 lookup）

`0e5d77a` 与 `00cd02d` 的源码零差异；lookup patch 只以文档形式存在于 PR20 头。**不能**把 PR20 头当作已修复 lookup 的树；caller 必须显式应用 patch。

### D-04 边界（不声称）

- 本报告不声称 B1-BLOCKER-03 的 legacy 行已可证明身份；仅验证新行 producer。
- 不声称生产跨实例票据原子性。B1 `db-facts.json` 明示 `lobbyTicketConsumptionRows=0`；TableService 的 one-time jti registry 是进程内实现，非跨实例原子消费。
- 不声称 CI、Unity、Android、IL2CPP、真机/弱网、公网或跨物理网络通过；全部 `NOT_RUN`。
- 不声称 PR20、B1 批次、G2–G5、G1 重审通过。

---

## 9. 清理与证据

### 9.1 清理

- B1 wrapper：`stage cleanup: PASS (exit 0) ownership-verified cluster removed; ports free; no postmaster`。
- 手工 PG：`pg_ctl stop -m fast` exit 0；61271 no response；data directory 删除。
- 61371/61372/61373 均无监听；无本任务路径下的 `postgres.exe`。
- 主工作树只提交本报告；`build/` 全部 ignored；候选源码未进入主仓库索引。

### 9.2 关键 SHA-256

| 项 | SHA-256 |
|---|---|
| first patch | `2E2A52D850FE8720F443EA21BF431DE2B7F6E04C7322FDCFE8B2A5861B3D8F29` |
| heartbeat patch | `16AE6536E406444407DEC9BB71EB34C5C2B44B2B3B3CE15B46A5F89F38EB1643` |
| lookup patch | `58AAC2B41DE1FB67FA91193C93FA396FDB8238072287D5BD744138025063D757` |
| candidate tar (our extraction) | `88D9D32B8238205CAA682D046333C47D511EDCB2EF999B8841DE0E647F644F1C` |
| B1 `suite.json` | `BFBA050D2B4CEF485CA711490A4DFFB013A671332B498703F7DA6EEDD434583C` |
| B1 `suite.log` | `174DDD36123E75D5C9635924CCD7516023F1618DE01BD32FE2061E69D5CD0B16` |
| B1 `wrapper-summary.json` | `10C001D38FC58EFF780287C9F929AC2E00534B679590C8B9922AB7A7EBA51943` |
| B1 `db-facts.json` | `D25BC4A69AF0B3AFB08F53876008E1137D2C4611F3C2CFCCD13B666EFD351D5B` |
| B1 `negative-facts-audit.json` | `AFC1C3A1BACE738B0E3D3E64E4635D990EAE0862C811CDA228D57929889C8E7B` |
| B1 `tests-b1.xml` | `F9B55021E3970E95066662F1A70E7E204CBACDF5A736658E00DCAC8B295C0F8B` |
| TableService full log | `C339417AD3E7F0913314FDC9C9B6673910A7924A89C1EEE6815201C3372DE552` |
| TableRuntime full log | `A13B3AC139C111C1F06EEE5BD9A2A86EC6884234117998DAE09DAA76100F967B` |
| Client.Wss full log | `DC999BB7B015CCF34F9D397B6F537FC6FC0B1974E04026A438DF92912CAE2200` |
| B1 gate full log | `0AC0D1F8A9AA6AA25009FEE3D9EB5514F09097982C66E6027D4CBCDE7D843C77` |
| PG full log | `1108CF26F38CBFAF1627113A221F36335AFCFA87B09DDE05DBA98DA4AC206EDC` |
| unpatched targeted log | `D9543D705E1DC12F0CE0D9964459923F41027EDD3D2D4150ED36A4461F834C8E` |
| unpatched full log | `06E19A07FDF6D1E1BA880F7F41A886B8968EE1A11B7CC810525BFF35DEC012C5` |
| heartbeat negative log | `1C4E24C0AD6F135357467E8BD0CB83011199DE9BF2AAED874ECDFDD423A68C30` |
| B1 combined console log | `EBE68BD591F1B03FC78730F66CD55532982D42F7A8E44E204E1D48DB8A2285DD` |

完整 ignored 清单：`build/b1-combination/logs/EVIDENCE-SHA256.txt`；补丁适用性行：`build/b1-combination/logs/PATCH-APPLICABILITY.txt`。这些 `build/` 文件不提交。

---

## 10. 判定矩阵与最终结论

| 维度 | 判定 |
|---|---|
| 精确候选 `00cd02d` 身份/树 | **PASS** |
| first-receipt patch 在候选中的 postimage | **PASS（已应用；round-trip 精确）** |
| heartbeat patch 在候选中的 postimage | **PASS（已应用；10/10 postimage 精确）** |
| lookup patch 对 `00cd02d` 的适用性 | **PASS（apply check/apply exit 0）** |
| lookup patch 声明的 git index 哈希 | **MINOR FAIL（D-01；元数据陈旧）** |
| first → heartbeat 顺序 | **PASS（强制；reverse/forward 均精确）** |
| lookup 与 heartbeat 顺序 | **PASS（内容可交换，最终哈希一致）** |
| 组合构建 | **PASS（0 warnings / 0 errors）** |
| 组合 focused tests | **PASS（173/173、454/454、82/82、54/54）** |
| 负向控制 | **PASS（unpatched 3/3 失败、full 4/173、heartbeat 3/3 失败）** |
| 真实隔离 PostgreSQL | **PASS（42/42；focused 1/1；32-byte digest round-trip）** |
| 端到端 B1 HTTPS/WSS/PG | **PASS（13/13 stages；54/54；suite PASS；cleanup PASS）** |
| PR20 验收 | **NOT_RUN / NOT APPROVED** |
| B1 批次验收 | **NOT RUN / NOT CLAIMED** |
| G1 重审 / G2–G5 | **NOT_RUN / 未批准** |
| 生产跨实例票据原子性 | **NOT TESTED / NOT CLAIMED** |
| Unity / Android / IL2CPP / 真机 / 公网 | **NOT_RUN** |

**最终结论：** `00cd02d` + first-receipt + heartbeat + lookup digest patch 的组合在本次隔离环境中功能兼容且行为正确；首回执与 lookup 均返回同一个 MJC1 domain digest，heartbeat 在真实五客户端 B1 场景中保持正确。唯一发现是 lookup patch 的 `index` 元数据不相对精确候选，属于 MINOR 可复现性缺陷，不影响已实测行为。PR20、B1 批次与 G2–G5 仍由 caller 独立裁决，本报告不构成任何批准。
