# V5 远端回执票据窄修复交付（2026-10-29）

状态：**本地交付；未 push/PR/MR/merge/部署，未写 `complete.flag`，未操作 scheduler。**
适用输入：独立审查发布的唯一 Draft PR20 头 `734062914adb6f23029f2630a6245ef5e2b4e23e`（下称 `7340629`），accepted `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
本文件只记录本轮窄 TableService 修复，不改变 V4 生产基线、G1 已批准结论、G2–G5 未批准状态和 181 classified 缺口。

---

## 0. 范围、冻结与工作树

| 项 | 值 |
|---|---|
| 工作树 | `D:\majiang-wt\remote-receipt-ticket-ds-20261029-1446` |
| 分支 | `wt/v5-remote-receipt-ticket-ds-20261029-1446` |
| 冻结输入 HEAD | `734062914adb6f23029f2630a6245ef5e2b4e23e` |
| accepted origin/v5 | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（`git merge-base --is-ancestor origin/v5 7340629` = 0） |
| 实现/源码 commit | `9f223ef9e08111ddb7f364d69c9ace25a2e5584a` |
| 交付 HEAD | 本报告 commit；`git rev-parse HEAD` 在交接消息和 `artifacts/remote-receipt-ticket-ds-20261029-1446/round2/manifest.json` 中记录 |
| V4/V5 | V4 仍是唯一生产；G1 APPROVED；G2–G5 未批准；181 classified 保持 |

**实际修改（仅此 7 个 tracked 文件）**

```text
dotnet/src/Mahjong.TableService/TableSessionHandler.cs
dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs
dotnet/tests/Mahjong.TableService.Tests/ReceiptScopeContractTests.cs          (new)
dotnet/tests/Mahjong.TableService.Tests/TestSupport/TableReceiptAssertions.cs (new)
dotnet/tests/Mahjong.TableService.Tests/SessionFlowTests.cs
dotnet/tests/Mahjong.TableService.Tests/RuntimeDecisionRuntimeTests.cs
dotnet/tests/Mahjong.TableService.Tests/TableTicketRegistryLifecycleTests.cs
```

`9f223ef` 净 diff：**7 files changed, 955 insertions(+), 27 deletions(-)**；生产源仅前两个文件（43 + 23 行 diff），其余全部是测试；**未修改** Client、Contracts/Wire、Domain、TableRuntime、Infrastructure、LobbyService、`Mahjong.sln`、Architecture 白名单、统一 verifier、Unity/Resources/Burst、golden/pin、scheduler。

`git diff --check 7340629..9f223ef`：**exit 0**。报告 commit 前 tracked working tree 为空；报告 commit 后再核对 tracked clean（见 §7）。

---

## 1. 修复/未修总表

| ID | 严重级别 | 本轮状态 | 摘要 |
|---|---|---|---|
| B1-BLOCKER-01 | BLOCKER | **FIXED** | Server receipt 现在携带权威 scope；另发现并修复第二根因：wire digest 与 Client outbox 的 MJC1 digest 算法不一致，导致 APPLIED 被 Client 结算为 Conflict |
| B1-BLOCKER-01b | BLOCKER（01 的第二根因，新信息） | **FIXED** | `canonical_payload_sha256` 改为 Client `CanonicalPayloadDigest`；scope 修复前该缺陷被 Client 的“无 scope 直接丢弃”遮蔽，无法看到 |
| B1-BLOCKER-02 | BLOCKER | **FIXED（仅 in-memory 窄修复）** | 同 assignment 同 tuple 的新 jti 可安全重连/接管；exact jti 一次性；不同 tuple fail-closed；两阶段 commit/evict/rollback 保持 |
| B1-MAJOR-01 | MAJOR | **UNRESOLVED / NOT_RUN** | 空 Close code 在本树与 final9 原始日志中均未复现；只做只读定位，未改错误码、未吞 Close、未放宽 250ms |
| B1-MAJOR-02 | MAJOR | **UNRESOLVED（不扩公共 API）** | Client 仍只有 `TableSession`，无独立 `SpectatorSession`/`LobbySession` |
| B1-MAJOR-03 | MAJOR | **UNRESOLVED（不接线）** | TableService 仍用 `InMemoryTicketConsumptionRegistry`；Lobby `table_ticket_consumption` 未接线；跨实例票据消费风险仍在 |

---

## 2. B1-BLOCKER-01：回执 scope 与 wire digest

### 2.1 缺陷与根因

**位置（修复后行号）**

- `dotnet/src/Mahjong.TableService/TableSessionHandler.cs:2503` `BuildReceipt`
- `...TableSessionHandler.cs:2520` `BuildAuthoritativeScope`
- `...TableSessionHandler.cs:1481`、`:1542` wire digest
- `...TableSessionHandler.cs:1790-1802` `DispatchReceiptQueryAsync`
- 原缺陷：`BuildReceipt` 设置 `RequestId`/`CorrelationId` 但不设置 `Envelope.Scope`；`dotnet/src/Mahjong.Client/TableSession.cs:1356-1361` 对 `envelope.Scope == null` 直接 return，导致 APPLIED 回执永不 resolve。

**复现（冻结输入，本轮 fresh 构建）**

```powershell
dotnet build artifacts/base-repro-7340629b/artifacts/caller-b1-probes/Probe.csproj -c Release --no-incremental -nr:false
dotnet artifacts/base-repro-7340629b/artifacts/caller-b1-probes/bin/Release/net8.0/Probe.dll
```

实际（`artifacts/.../round2/base-repro.log`，native exit **2**）：

```text
same_assignment_new_jti: first=Accepted, commit=Committed, after_release=DuplicateJti; expected_new_ticket=Accepted
receipt_scope: present=False, request_preserved=True; expected_scope_present=True
confirmed_defects=2
```

**修复**

- `BuildReceipt` 用连接 admission 的 `TableConnectionIdentity` 重新构造一个**独立**的 `Contracts.Wire.RequestScope`：scopeId、accountId、role、roomId、issuedAt/expiresAt 全部来自 server-issued identity，而不是客户端 `request.Scope` 的可变对象。
- 该 identity 正是 `TableCommandMapper.Map` 用来 fence 请求的同一 scope（envelope 的 `ScopeId` 与 identity 不符会在 actor 前被 `SCOPE_MISMATCH` 拒绝）；因此这是原请求的**服务端权威 scope**，不是从当前身份伪造历史 scope。
- `BuildControlReceipt` 复用同一 `BuildReceipt`，控制回执同样带 scope。
- `ReceiptLookup` 不附加当前 scope，避免把当前身份的 scope 冒充被查询旧 scope；只回显原 `requestId`/`correlation`。

### 2.2 第二根因 B1-BLOCKER-01b（新信息）

**为何此前不可发现**：scope 缺失时 Client 在 `HandleReceiptAsync` 第一行即丢弃回执，根本不会走到 digest 校验。scope 修复后，独立 Client probe 立即暴露：server receipt 的 `canonical_payload_sha256` 是 **raw SHA-256**，而 Client outbox 使用 `CanonicalPayloadDigest`（MJC1 域分隔 idempotency digest）。Client 因此把真实 APPLIED 结算为 `Conflict`。

**证据（`artifacts/.../round2/client-probe-3.log`）**

```text
server_receipt ... status=Applied digest=053b8a62...
client_receipt_status=Unknown
resolved_digest=ef9ce89c...
resolved_disposition=Conflict
probe_exit=1
```

**修复**：`DispatchCommandAsync` 在 wire 面改用 `CanonicalPayloadDigest.Compute(payloadBytes).ToHex()`，actor 的持久幂等仍使用 `TablePayloadSha256`，两者职责分离；exact-byte replay/冲突仍由 actor 的 raw digest 判定。`MapLookupReceipt` 不转发 actor 的 raw digest（durable receipt 存的是 raw 值，转发会让 Client 每次都 Conflict）；Client 对空 digest 使用自己保留的 expected digest 完成历史对账。该取舍在 §5 的安全边界和 §8 的未覆盖项中明示。

### 2.3 全路径审计矩阵

| 路径 | 实际断言 | 结果 |
|---|---|---|
| 普通 APPLIED | 真实 Kestrel + socket，receipt scope 全字段 = WELCOME scope；digest = Client MJC1 digest | PASS（`RuntimeDecisionRuntimeTests.RealRoomDecision...`，`SessionFlowTests.CommandReceipt...`） |
| 普通 REJECTED | 真实入站命令被 actor 拒绝，回执仍带权威 scope | PASS（`RuntimeDecisionRuntimeTests` rejected 断言） |
| 控制回执 | 真实 Kestrel `ClaimControl`（字段 20/21），回执 Applied/Rejected、scope 权威，client 伪造 account/role/room 不生效 | PASS（`ReceiptScopeContractTests.RealKestrel_ControlReceipt...`） |
| RECORDED 分支 | 当前 handler 只映射 Applied/Rejected；对状态无关的 `BuildReceipt` 做直接分支守卫 | PASS（`ReceiptScopeContractTests.BuildReceipt_RecordedStatus...`；**不是端到端证据**） |
| exact-byte replay | 同一 `byte[]` 重发；`Replayed=true`，requestId/correlation/scope/status/reason/digest/version 不变 | PASS（`SessionFlowTests.CommandReceipt...` 真 socket） |
| 同键异 bytes | 返回 `REQUEST_KEY_CONFLICT`，不覆盖原 receipt；随后 lookup 仍为原回执 | PASS（同上） |
| ReceiptLookup | 回显 requestId/correlation；**无 Scope**；不转发 raw digest；Found/Replayed/status/reason/version 正确 | PASS（同上） |
| scope 伪造 | 同一 ScopeId 但伪造 account/role/room/time：回执 scope 仍为权威值 | PASS（`SessionFlowTests.CommandIdentityFences...`，`ReceiptScopeContractTests...`） |
| 客户端 outbox | 生产 `TableSession` + `FileOutboxStore` + 生产 TableService：APPLIED 后 `PendingCommands=0`、`LoadUnresolvedAsync=0` | PASS（独立 probe，`artifacts/.../round2/client-probe-final.log`） |

**独立 Client probe 最终输出（native exit 0）**

```text
server_receipt ... status=Applied digest=48304632... scope=ade23460...
client_receipt_resolved=True
client_receipt_status=Applied
resolved_disposition=Applied
client_pending_after=0
client_outbox_unresolved_after=0
client_last_error=
probe_exit=0
```

说明：该 probe 用真实生产 `TableSession`、`FileOutboxStore`、TableService Kestrel 组合；票据由**组件测试 signer**签发（非真实 Lobby），传输用最小 loopback adapter（生产 `WebSocketTableChannel` 的 `?room=` 路径与当前 route 的适配不在本任务范围）。**不等于**真实 Lobby HTTPS 签票、TLS/WSS 或 B1 全链路 PASS。

---

## 3. B1-BLOCKER-02：同 assignment 新 jti 安全重连/接管

### 3.1 缺陷与修复

**位置**

- `dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs:277-300` `Consume` 的 assignment 分支
- `...TableTicketConsumptionRegistry.cs:373-390` assignment retention

**修复前**：`_assignments` 已有同一 assignment 时，只要 jti 不同就无条件 `DuplicateJti`，旧连接已释放后同 tuple 的新票也被拒绝。

**修复后**

1. 先做 exact-jti 检查：任何已消费 jti 在 expiry + skew 前一律 `DuplicateJti`（无论 release/rollback）。
2. 再做 assignment tuple 检查：account/role/room/seat 任一不同 → `AssignmentMismatch`，在 seat/capacity/记录写入前返回，零资源/零副作用。
3. tuple 相同且 jti 不同 → 交给既有两阶段 `Consume → CommitAdmission → EvictOnWelcome → Release`；新 jti 不继承旧 jti 的一次性授权，旧的 proposal 由新 proposal 覆盖，`CommitAdmission` 只允许最新 provisional 提交，旧提交返回 `Superseded`。
4. 不同 account 的 live seat owner 仍由 `LiveOwner` fence 拒绝；新票成功 WELCOME 后才由 `EvictOnWelcome` 顶掉旧连接（4403），失败的新票 `RollbackSeatHandover` 只删自己的 entry，不踢旧 owner、不复活旧连接。
5. assignment retention 取 `max(旧, 新)`：较短的 re-ticket 不能缩短较长旧票的 assignment binding；旧 session release 不删除 assignment；expiry purge 只按 retained window 清理。
6. `_consumedJtis`/`_assignments` 仍受 `MaxRetainedConsumedTickets` 限制，purge 后下一个 Consume 才接受新票；seat/spectator 资源由 `_sessions` 独立释放。

### 3.2 负例/正例矩阵（全部实际运行）

| 断言 | 测试 |
|---|---|
| 旧连接释放后同 assignment 新 jti Accepted；旧/新 jti 均不可重放 | `SameAssignmentFreshJti_AfterRelease_IsAccepted_AndOldJtiStaysReplayProof` |
| 旧连接在场，新票 WELCOME 后旧连接 4403；旧 jti 4401 | `RealKestrel_SameAssignmentFreshJti_DisplacesOld4403_AndOldJtiReplayIsRejected` |
| 新票 handover 失败/rollback，旧 owner 保留；两个 jti 都仍不可重放 | `SameAssignmentFreshJti_AfterFailedHandover_KeepsOldOwner_AndBothJtisSpent` |
| 两个新 jti 确定性交错，至多一个 commit、一个 live owner | `ConcurrentFreshJtis_SameAssignment_AtMostOneCommitAndOneLiveOwner` |
| 不同 account/role/room/seat 全部 fail-closed，当前 owner 不变，容量不泄漏 | `SameAssignmentDifferentTuple_FailsClosed_AndLeavesCurrentOwner`、`AssignmentMismatch_DoesNotLeakSpectatorCapacity` |
| 较短 re-ticket 不缩短 assignment binding；旧 jti 仍拒绝、异 tuple 仍拒绝 | `ShorterLivedReissue_DoesNotShortenTheAssignmentsBindingWindow` |
| retained store 满后 fail-closed；expiry + skew 后回收容量 | `RetainedTicketStore_IsBounded_AndExpiryPurgeReclaimsCapacity` |
| A/B/C 乱序 welcome、双失败回滚、晚到 WELCOME self-close、旧 owner 不复活 | 既有 `FailureChain_*`、`OutOfOrderWelcome_*`、`GatedRealHandlers_*`、`WelcomeMatrix_*` 全部保留并通过 |
| 观战 capacity 不泄漏、release 后仍可重新观战 | `SpectatorCapacity_DoesNotLeakAcrossSameAssignmentRefresh`、`ReleaseAfterPurge_*` |

### 3.3 边界

本修复只在 **单进程 in-memory** registry 内成立。没有新增 Lobby 同步调用、没有把 TableService 接到 Lobby 持久消费表、没有扩大公共 API。跨进程/跨实例的唯一消费权威仍是未解决项（见 B1-MAJOR-03）。

---

## 4. B1-MAJOR-01：空 Close code / PROTOCOL_VIOLATION

**结论：UNRESOLVED / NOT_RUN；本轮未改代码、未改错误码、未吞 Close。**

只读核对：

- `TableSessionHandler.SendCloseAndCloseAsync` 只从 `CloseDirective.Code` 构造 `Close.Code`（`...:2685-2689`），`Directive(code)` 的 5 个构造点全部使用非空常量（`LOCAL_CLOSED`、`LEGACY_V4_REJECTED`、`PROTOCOL_VIOLATION` 等）；`EnqueueClose` 也只接收该 `CloseDirective`。
- B1 final9 `table.stdout.log` 中全部 `close delivered code=...` 都是非空稳定 code（`LOCAL_CLOSED`、`INVALID_TOKEN`、`DISPLACED_BY_NEW_CONNECTION`、`HEARTBEAT_TIMEOUT`、`SLOW_CONSUMER`、`MALFORMED_MESSAGE`、`PROTOCOL_VIOLATION` 等）。
- B1 final9 `suite-summary.json` 记录的是 `serverReceiptScopePresent=false`/`clientPendingAfterServerReceipt=1`，没有可复现的“空 code Close”字段；报告所称 `LastError=PROTOCOL_VIOLATION` 在保留的 client/table 日志中未被定位到空 code 的 Close 帧。
- 本树真实的 250ms 配置回归（`RealKestrel_SameAssignmentFreshJti...` 等）所有 close 都有 code；未把 50ms 历史 `SLOW_CONSUMER` 当作已支持能力，也未放宽 slow-consumer 阈值。

因此：空 code 更可能来自 B1 test transport/时序或未保留的客户端本地关闭路径；**没有足够的本树证据归因为生产缺陷**。保持 `UNRESOLVED`，需要原 B1 thread 用其完整 runner/5-client 负载复跑定位。

---

## 5. B1-MAJOR-02 / B1-MAJOR-03：未扩大范围

### B1-MAJOR-02（UNRESOLVED）

`dotnet/src/Mahjong.Client/` 仍只有 `TableSession`（role 由 ticket/binding 决定），没有独立 `SpectatorSession`/`LobbySession` 公共类型。本轮不新增公共 API；B1 若要求独立类型，必须另立 API/契约任务，不能由测试自定义替代。

### B1-MAJOR-03（UNRESOLVED）

TableService 仍注册 `InMemoryTicketConsumptionRegistry`（`TableServiceWebHost.cs`），Lobby 的 `ConsumeTableTicketAsync`/`lobby.table_ticket_consumption` 未被 WSS 消费路径调用。**本轮的 registry 修复不证明跨实例/跨进程一次性消费安全，也不证明 G3。** 需要 Lobby/TableService owner 明确唯一消费权威与持久化事务；不得用测试直接写 Lobby 消费表冒充接线。

---

## 6. 验证命令、native exit 与真实计数

所有命令在本树、Release、`--no-incremental -nr:false` 下执行；测试直接运行构建出的 DLL，XML 用 xUnit v3 `-result-xml` 生成；stdout/stderr 分开保留。

### 6.1 构建

```powershell
dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-incremental -nr:false --nologo
dotnet build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-incremental -nr:false --nologo
dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -nr:false --nologo
dotnet build dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj -c Release --no-incremental -nr:false --nologo
```

四次 build 均 **0 warning / 0 error / exit 0**；日志：`round2/build-*-final.log`。

### 6.2 最终测试（隔离运行，无并发构建）

```powershell
dotnet <testdll> -noLogo -reporter default -result-xml <xml>
```

| suite | total | passed | failed | errors | skipped | notRun | native exit | XML SHA256 |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| TableService.Tests | 167 | 167 | 0 | 0 | 0 | 0 | **0** | `6a457cff867f2019472e286b3d6695ad1a6aea9c8c37d0d4196fd3cbd50db8ee` |
| Client.Tests | 182 | 182 | 0 | 0 | 0 | 0 | **0** | `bae9ca6ebf6da72d1db9d4533fc18bd6510e9539d781aed8a0db4a34faa5853b` |
| TableRuntime.Tests | 448 | 448 | 0 | 0 | 0 | 0 | **0** | `3f5224045baf651d0edcc7b2b0026a20e5533264c66f28eef3d255d2fc279bcd` |
| Architecture.Tests | 132 | 132 | 0 | 0 | 0 | 0 | **0** | `beb6b6caab12615d59f63469f5b23f3eeb7351c1a91312ae35c541fa6fe76a59` |

所选 B1 断言（10 个方法，另跑一次；XML `ad91b793...`）：

```text
Total: 10, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, native exit 0
```

XML/原始 stdout/stderr：

```text
round2/tableservice-capture.xml / .stdout.log / .stderr.log
round2/client-capture.xml     / .stdout.log / .stderr.log
round2/tableruntime-capture.xml / .stdout.log / .stderr.log
round2/architecture-capture.xml / .stdout.log / .stderr.log
round2/b1-selected-capture.xml / .stdout.log / .stderr.log
```

所有 stderr 为 0 字节；没有 skip、空测试、not-run 或旧 XML。中间一次探索性全量 TableService 运行在并发构建负载下出现过 1 个旧测试超时（`tableservice-final2.log` 保留）；随后隔离运行同一最终 DLL 为 167/167 exit 0，未通过删测试/放宽断言/刷新摘要处理。

### 6.3 冻结输入复现

```powershell
dotnet artifacts/base-repro-7340629b/artifacts/caller-b1-probes/bin/Release/net8.0/Probe.dll
# 输出：after_release=DuplicateJti；receipt present=False；confirmed_defects=2
# native exit 2
```

### 6.4 独立 Client + TableService probe

```powershell
dotnet build artifacts/receipt-ticket-probe/ReceiptTicketProbe.csproj -c Release --no-incremental -nr:false
dotnet artifacts/receipt-ticket-probe/bin/Release/net8.0/ReceiptTicketProbe.dll
# status=Applied, pending=0, outbox_unresolved=0, native exit 0
```

该 probe 的 ticket 是组件测试 signer，不是真实 Lobby 签发；传输是 loopback adapter，不是真实 TLS。它证明的是 **Client TableSession/FileOutboxStore + 生产 TableService 回执/scope/digest 集成**，不证明 B1 全链路。

---

## 7. SHA256 manifest（源码/构建/原始证据）

完整 JSON（生成后不再 append 任何被覆盖日志）：
`artifacts/remote-receipt-ticket-ds-20261029-1446/round2/manifest.json`。

源码/测试：

```text
37180de6d542f7dfe2927400f258f6f3f264aa9b765c5b4de848a0be46c41f72  dotnet/src/Mahjong.TableService/TableSessionHandler.cs
8395f0ceb3446c8e3e809bc6d162720dc9f7f325eee14a217f030feef7415d4f  dotnet/src/Mahjong.TableService/TableTicketConsumptionRegistry.cs
99ebab48e8dc2f54612908c80e5cafbed13c003164f059e22f543ffb84e57017  dotnet/tests/Mahjong.TableService.Tests/ReceiptScopeContractTests.cs
78cdd460d63e1f3a6c9a1cfc091124d47689bf8386549c556072fadb8a3f46aa  dotnet/tests/Mahjong.TableService.Tests/TestSupport/TableReceiptAssertions.cs
c9c7406ed35c629a42628d97a583c844365a37ceca77c435ac809f74e42786a3  dotnet/tests/Mahjong.TableService.Tests/SessionFlowTests.cs
f3c1d0158d302ef6f1b6c698daa2d06442e30cd470655ec3deb2283130281b8b  dotnet/tests/Mahjong.TableService.Tests/RuntimeDecisionRuntimeTests.cs
cce54b12ecca66b94f5fca20d039a48e82de17953d11da79479e6808988df96e  dotnet/tests/Mahjong.TableService.Tests/TableTicketRegistryLifecycleTests.cs
```

构建产物（测试 bin 中的生产 DLL 与 src bin 同一 SHA）：

```text
1485e7398ef85c2989a5455fb2a5f40bb914b1553ec49e716324c4b9dbe659ed  Mahjong.TableService.dll
5e716bc67de93548996dab6bb1f4db0a6630e796537bf2a1b578e4ada1b16707  Mahjong.TableService.Tests.dll
64fb4202d41f65ee429f9361a9b3ff11b68e20024d1a73c09659e3658aecddab  Mahjong.Client.dll
bfc6da28790111aa972af7fb0072c31df1ca0d8a2dd51fce434dc6d5593435ec  Mahjong.Client.Tests.dll
e80269a13b9162ccc878b3a9add5571625bfd0952d0246a6a6d1ec096ac68e9c  Mahjong.TableRuntime.dll
51ab296978ee57c87798dbdac26f42f9d34d369653ab9d2f2543968299cda842  Mahjong.TableRuntime.Tests.dll
d3954a5a4f36420e0869c32a0293e15bb3ec6a0981f8e2f81fe2ee2c7a3896f0  Mahjong.Architecture.Tests.dll
```

原始 XML/stdout/stderr：

```text
6a457cff867f2019472e286b3d6695ad1a6aea9c8c37d0d4196fd3cbd50db8ee  tableservice-capture.xml
df8c0992e263a3762786340d78190ce4e4c2ffb356f0081ae190eb5b0bd181eb  tableservice-capture.stdout.log
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  tableservice-capture.stderr.log (empty)
bae9ca6ebf6da72d1db9d4533fc18bd6510e9539d781aed8a0db4a34faa5853b  client-capture.xml
45dc17fb8a2788ff680a0a71d27a4ebbeb257dd7de7a7b9bf285f903a9e0f5a8  client-capture.stdout.log
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  client-capture.stderr.log (empty)
3f5224045baf651d0edcc7b2b0026a20e5533264c66f28eef3d255d2fc279bcd  tableruntime-capture.xml
401009745d77a054eaaa5b301af5355191024eb7d1627ee95b58452044492028  tableruntime-capture.stdout.log
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  tableruntime-capture.stderr.log (empty)
beb6b6caab12615d59f63469f5b23f3eeb7351c1a91312ae35c541fa6fe76a59  architecture-capture.xml
5ac7e53c5240e4ed957e93929010bfd63fc1ba70d76d7985d58d86008327d19c  architecture-capture.stdout.log
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  architecture-capture.stderr.log (empty)
ad91b79348520a336d6d35072eb7d6184c23c1273022bf70e74c862e0ef31c62  b1-selected-capture.xml
0768903452fa39de57537966b1ec44700cb9f2bf89d2476570493b1501685e31  b1-selected-capture.stdout.log
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  b1-selected-capture.stderr.log (empty)
```

冻结复现与 probe：

```text
6238768f82734049181d06b369f8b2d106f5ce3b53f84ea1e77d0e5537248e21  base-repro.log
853bcfcdc8bce0bdbc31cd4be3d69fd9d7d4be081a473aed6d461995ae998440  base-repro-exit.txt
73880e5432f9c02da906d4b0bf01f74a28cae814108a754deb2ebd6b37389892  client-probe-final.log
78ebd2162c939d68a56087f6a12217d82309cbd3dbbe6ee7677b6bddfeb37edc  client-probe-final-exit.txt
6709a4b871947cbd306b49c3a08be5ea386e5c5f89c020dec6a4017b140f7eba  client-probe-3.log   （digest 修复前的 Conflict 证据）
bd2e44a7c8a494cd24829510ab5d8369a617045237ba4ff58f66a7866c53c7f0  client-probe-4.log   （digest 修复后）
be1e046feddc60ab99311bf2e361afee89032cd48830f54292b970e6604c233a  artifacts/receipt-ticket-probe/Program.cs
1d93bff6058c50b2fba6133ae205a7475647c9d3077f702f5ec5d2d0335aedc6  artifacts/receipt-ticket-probe/ReceiptTicketProbe.csproj
6441936b6be9231b64985e0bd890e5403210f16266b6cceedeccab222ab438f4  ReceiptTicketProbe.dll
```

---

## 8. 安全边界、未覆盖项与下一依赖

**维持的安全边界**

- 不改 Client 兜底、不放宽 account/room/role/seat 校验、不从当前身份伪造历史 scope；回执 scope 是新建对象，避免客户端可变对象浅引用。
- 非法 scope 仍在 mapper 前以 `SCOPE_MISMATCH` 拒绝，不生成合法 Receipt；lookup 的旧 scope 不附加当前 scope。
- 不同 assignment tuple 在资源写入前 fail-closed；旧 jti 在 expiry + skew 前不可重放；失败新票不能踢旧连接/复活旧连接。
- lookup 不转发 actor raw digest，Client 用自身 retained expected digest 结算；这是对既有 actor digest 算法边界的兼容处理，不是放宽回执状态/版本检查。

**未覆盖 / 不得推断为 PASS**

- 真实 Lobby HTTPS 签票、Lobby 一次性消费接线、跨进程/跨实例 ticket consumption（B1-MAJOR-03）。
- 完整 B1 四玩家 + 独立观战真实 HTTPS/WSS/PG16.10 整场、kill/recover、`RECEIPT_QUERY` 闭环；本任务只证明窄修复前置，不宣称 B1/G3 PASS。
- 本树独立 probe 为组件 signer + loopback adapter；未做真实 TLS、真实域名、跨物理网络、Android/iOS、PG 故障注入。
- 空 Close code 仍未定位（B1-MAJOR-01）；250ms 是已验证配置，50ms 历史 `SLOW_CONSUMER` 不算支持。
- G2–G5 未批准；181 classified 缺口未改变；V4 仍是生产/回退基线。

**下一依赖**：由原 B1 thread（`thread_20261029_125154_b973`）在 caller 复核后重跑真实 Lobby HTTPS 签票 → WSS → 4+1 整场/kill/recover；本任务不复制/修其 worker、runner、manifest 或越界文件。caller 复核后只统一现有 Draft PR20（https://github.com/liuchangready/majiang-game/pull/20）。

---

## 9. 交付状态

- 本地 commit：`9f223ef9e08111ddb7f364d69c9ace25a2e5584a`（源码+测试）；本报告为后续本地 docs commit。
- 未 push、未创建/更新 PR/MR、未 merge 回 `v5`、未部署、未写 `complete.flag`、未操作 scheduler/其他线程。
- `git diff --check 7340629..9f223ef` = 0；报告 commit 后 `git status --porcelain` 仅允许报告自身提交前为空；最终 tracked clean 以交接时 `git status` 为准。
