# V5 P3.4 回执、租约和恢复端口合同交付

- 状态：**P3.4 本地实现完成，等待 caller 独立验收**。不是 G1/G5 验收，不是 V5 全链通过，不改变 V4 唯一生产状态。
- 唯一工作树：`D:/majiang-wt/p34-receipts`（分支 `wt/v5-p34-receipts`）。
- 冻结基线：`daeb19a64951174426924026fe974c8ae5caacd9`。
- 实现提交：`9e909ea6b7dfb411b1e2511fa4f0f9d05307ba38`（`feat(v5-p34): add receipt, lease and cold-recovery contracts`）。
- 文档提交：本文件所在的最终本地 HEAD；见最终回执。未 push、未建 PR、未触发 CI。
- 依据：计划 §3.2.2 / §3.4.2–§3.4.4 / F4–F6，以及已接受的 P3.1 actor、P3.2 严格按流投影、P3.3 计时/控制实现。没有替换已验 actor API，没有改任务书、矩阵、黄金 fixtures、统一 verifier 或 CI。

## 1. 交付范围与明确边界

本轮只实现 P3.4 的接口、内存同形事务实现、actor 接入和冷恢复合同：

- `ITableStore` / `IReceiptStore` / `ITableLease` / `ISettlementOutbox` 端口；
- 一个明确的原子提交边界，组合事件、命令 journal、回执、终局 outbox 和可选 checkpoint；
- 回执先于 scope 查验、精确重放、CONFLICT、REJECTED 回执；
- `ownerEpoch` 与 `stateVersion` 双栅栏、租约续租失败/旧 owner/stale state 停止服务；
- checkpoint + 命令 journal 的真实 Domain 重放，校验 136 牌、状态哈希/指纹、事件序号/ordinal、控制代次、决策时钟、真实 CheatLedger、终局 outbox；
- 恢复后严格 Wire 玩家/观战基线，旧 generation/epoch 拒绝；
- 冷恢复时钟调整和 `RecoveryAdjusted` 审计。

明确 **NOT_RUN**：真实 PostgreSQL/迁移/行锁/advisory lock、P5.2 `Mahjong.Infrastructure`、TableService/LocalHost、P5.1 WSS、真实进程崩溃/备份恢复、Unity/IL2CPP/真机/物理网络、真钱钱包/资产消费者。Per-table 只使用内存实现模拟 PostgreSQL 同形约束；不得把本报告的 memory PASS 写成 PG PASS。

P3.2 完整 committed facts 的语义所有者仍是宿主。actor 不拥有 `TileMarkState` / `SleeveBloomState` / `BeamSwapState`，本轮不伪称重建这些私有子状态；checkpoint 持久化 **真实 CheatLedger**，并提供可选的宿主 committed-facts/private-state 不透明 payload（SHA-256 绑定、逐字节回放），其语义重建属于 P5.2/宿主。详见 §8 限制。

## 2. 最终接口与不可变数据

### 2.1 端口

新增 `dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs`：

```csharp
public interface ITableStore
{
    ValueTask CreateRoomAsync(TableCheckpoint initialCheckpoint, CancellationToken ct);
    ValueTask<TableStateRow?> LoadStateAsync(string roomId, CancellationToken ct);
    ValueTask<TableCheckpoint?> LoadCheckpointAsync(string roomId, CancellationToken ct);
    ValueTask SaveCheckpointAsync(TableCheckpoint checkpoint, CancellationToken ct);
    ValueTask<TableDurableCommitResult> CommitControlAsync(
        TableControlCommitRequest request, TableCheckpoint checkpoint, CancellationToken ct);
    ValueTask<IReadOnlyList<TableEventRecord>> LoadEventsAfterAsync(
        string roomId, EventSeq afterSeq, CancellationToken ct);
    ValueTask<IReadOnlyList<TableCommandJournalRecord>> LoadCommandsAfterAsync(
        string roomId, StateVersion afterVersion, CancellationToken ct);
    ValueTask<TableDurableCommitResult> CommitAsync(
        TableDurableCommitRequest request, CancellationToken ct);
    ValueTask AppendRecoveryAuditAsync(TableRecoveryAuditRecord audit, CancellationToken ct);
    ValueTask<IReadOnlyList<TableRecoveryAuditRecord>> LoadRecoveryAuditsAsync(
        string roomId, CancellationToken ct);
}

public interface IReceiptStore
{
    ValueTask<TableDurableReceipt?> TryGetAsync(
        string accountId, RequestScopeId scopeId, RequestId requestId, CancellationToken ct);
}

public interface ITableLease
{
    ValueTask<TableLeaseAcquireResult> AcquireAsync(
        string roomId, string ownerInstance, TimeSpan ttl, CancellationToken ct);
    ValueTask<TableLeaseRenewResult> RenewAsync(
        string roomId, string ownerInstance, OwnerEpoch expectedEpoch, TimeSpan ttl, CancellationToken ct);
    ValueTask<TableLeaseSnapshot?> GetAsync(string roomId, CancellationToken ct);
}

public interface ISettlementOutbox
{
    ValueTask<TableSettlementOutboxRecord?> TryGetAsync(
        string matchId, int resultVersion, CancellationToken ct);
    ValueTask<IReadOnlyList<TableSettlementOutboxRecord>> ListUnpublishedAsync(CancellationToken ct);
    ValueTask<bool> MarkPublishedAsync(
        string matchId, int resultVersion, ulong publishedAtUtcMs, CancellationToken ct);
}
```

范围/身份端口：

- `IRequestScopeAuthority.LookupAsync`：宿主颁发的 scope；actor 只从这里取得 `accountId/role/room/expiry/revocation`。
- `ITableTransactionalScopeReader.ReadScopeForCommit`：**必须在提交事务内**同步读取同一 scope 行；内存实现用同一把提交锁，P5.2 用 `FOR SHARE`/等价锁。
- `TableCommandEnvelope` 只携带宿主设置的 `ScopeId`、`Api`、`PayloadSha256`；不携带可授权的 account/role/room 声明。缺失三者时 durable 路径拒绝且不写回执。
- `ITableStore`/`IReceiptStore`/`ITableLease`/`ISettlementOutbox` 是**同一个原子存储/事务的逻辑面**，不是四个可独立部署的写者。`InMemoryTableStore` 一个对象实现四个端口；P5.2 必须在同一 PostgreSQL 事务内实现。

### 2.2 不可变/防御复制数据

- `TablePayloadSha256`：32 字节不可变 SHA-256 值；构造/读取防御复制；`Compute` 计算原始字节摘要。
- `TableScopeRecord`：不可变宿主 scope；role 为 `Player/Spectator/Lobby`，lobby 不带 room，桌 scope 必须绑定 room。
- `TableDurableReceipt`：`(accountId, scopeId, requestId)` + `api` + 原始业务字节摘要 + 定义性 `Receipt`（APPLIED/RECORDED/REJECTED）+ `evaluatedStateVersion`/`commandVersion`/`eventSeq` + 过期时间。
- `TableCommandJournalRecord`：每个已接受 `commandVersion` 一条，含严格编码的 `RoundCommand` 字节、schema、请求身份、API/摘要；恢复重放的是**命令**，不是保存的候选状态。
- `TableEventRecord`：保留 P3.1 精简字段，并新增可选 `eventSchema`/`payload`。payload 是严格的“事件事实绑定”（commandVersion、ordinal、source fingerprint），不是整份候选状态。
- `TableCheckpoint`：状态/控制/计时/cheat/宿主 facts 各自 byte[] + SHA-256 + schema/ruleset/version/seq/fingerprint/bootId/createdUtc；构造时验证每个 payload 的摘要。
- `TableDurableCommitRequest`：已接受的批次非空事件、命令 journal、状态 payload/指纹；REJECTED 批次无事件/journal/outbox/state；终局 outbox 只允许 terminal；内部命令显式 `internalCommand=true`。
- `TableSettlementOutboxRecord`：`(matchId,resultVersion)` 稳定键、payload 摘要自校验、`publishedAt` 只能前进/幂等。

## 3. 唯一原子提交边界

`InMemoryTableStore.CommitAsync` 的顺序（P5.2 必须保持同一顺序）：

1. 房间存在检查（内存锁/事务级 advisory lock 等价物）。
2. **回执查重先于 scope**：同 `(accountId,scopeId,requestId)` 存在且 `api+payloadSha256` 相同 → 返回原回执（REPLAYED，不再执行）；不同 → CONFLICT，零副作用。
3. 无旧回执时才做 **scope 事务内复查**：存在、账户匹配、Player role、room 匹配、未撤销、未过期；失败 → SCOPE_REJECTED，不写回执/事件/状态。
4. **租约栅栏**：owner instance + `owner_epoch` 必须匹配且 `now < lease_expires_at`；否则 STALE_OWNER。
5. **独立 state CAS**：`table_state.state_version == expectedStateVersion`；否则 STALE_STATE。
6. 已接受批次：事件非空、ordinal `0..N-1`、`seq` 从 `last_event_seq+1` 连续、共享 `command_version=expected+1`；command journal 必须存在；重新计算状态 payload 的 SHA-256（不信任调用方传入的 hash）；可选 checkpoint 必须与本次 version/seq/hash/fingerprint 一致。
7. REJECTED 批次：只暂存 receipt；仍必须先通过租约和 state 两道栅栏；不写事件、不递增 stateVersion、不写 outbox/checkpoint。
8. 终局 outbox：只有 `terminal=true` 才允许；`(matchId,resultVersion)` 已存在且 payload 摘要相同 → 幂等跳过；不同 → OUTBOX_CONFLICT 整批回滚。
9. 所有写入先 stage，验证全部通过后一次性 apply；注入任一点失败都不会留下部分事件/回执/outbox/checkpoint。
10. 提交成功后才由 actor 安装内存状态并发布；`AfterApplyBeforeReturn` 注入返回 `OUTCOME_UNKNOWN`，actor 进入 Faulted，不安装/不发布，回执可查。

版本语义与 P3.1/P3.2 一致：`stateVersion` 是命令版本，每个已接受状态变化恰好 +1；一个命令的 N 个事件共享 `commandVersion`，`eventOrdinal=0..N-1`；`eventSeq` 独立递增；REJECTED 没有事件、`commandVersion=null`、版本不变。

## 4. actor 接线

`TableActor` 新增可选 `TableDurableContext`（store/receipts/lease/outbox/scopes/ownerInstance/leaseTtl/bootId/ruleset/matchId/receiptRetention/cheatLedgerProvider/committedFactsProvider）。未配置 durable 时完整保留旧 P3.1–P3.3 路径；配置后新持久路径实际由 actor 使用。

### 4.1 玩家命令

`ProcessCommandAsync`：

1. `GuardServing`：恢复/失租/关闭期间拒绝进入；不写回执。
2. 要求 `ScopeId+Api+PayloadSha256`；缺失 → `PAYLOAD_IDENTITY_REQUIRED`，零副作用。
3. 读取宿主 scope 以取得账户；**先查回执**：
   - 存在且 API/摘要相同 → `TableOperationResult.FromReplay`，不调用 Domain、不消费 decision/time reserve、不写事件/outbox；
   - 存在但 API/摘要不同 → `CONFLICT`。
4. 无回执才校验 scope（Player/room/未撤销/未过期）；认证/scope 失败无回执、无事件、无状态变化。
5. 进入原 P3.1/P3.3 权威检查（owner、round、decision、deadline、expectedState、controlEpoch、recoveryGeneration）。
6. 业务拒绝 → `CommitRejectedReceiptAsync`：REJECTED 回执仍走原子边界并通过 owner/state 栅栏；栅栏失败返回 Failed/LeaseLost，不写回执；`StaleOwnerEpoch`/`StaleStateVersion`/`StaleRecoveryGeneration` 等栅栏拒绝不落回执。
7. 接受 → 构建事件（带事实绑定 payload）、command journal、玩家回执、终局 outbox（仅终局）、终局 checkpoint；一次 `CommitAsync`。提交成功后才安装状态、关闭 decision、发布；REPLAYED 结果不安装。

### 4.2 内部 tick/AI

- 内部命令使用 `internalCommand=true`，仍写事件、command journal、state CAS、lease 栅栏和终局 outbox；不生成客户端回执。
- 明确边界：客户端 scope 命令只能来自 `Player` scope；内部 tick/AI 由 actor/宿主驱动，不经过 client scope，但仍有 command journal 供恢复重放。
- tick 的 timeout/AI 结果仍按 P3.3 顺序先 timeout 再 AI；只有成功提交后才 `Expire/Complete` 决策时钟和移除 pending。

### 4.3 租约/故障

- `RenewLeaseAsync` 在 actor consumer 上续租；失败/过期/旧 owner 立即 `LeaseLost`，清 pending/时钟、关闭流并发布 LeaseLost，不自动重试。
- 提交返回 `StaleOwner` → LeaseLost；`StaleState`/`OutcomeUnknown` → Faulted；都不发布状态。
- `RecoverFromStoreAsync`：显式冷恢复入口。进入 RECOVERING → 获取新 ownerEpoch → checkpoint+journal 重放 → 安装已验证 baseline → SERVING。任何失败保持非 Serving。

### 4.4 回执查询

`TableActor.QueryReceiptAsync(accountId, scopeId, requestId)`：

- 需要已认证账户；scope 必须属于该账户，否则返回 NOT_FOUND，不泄露其他身份是否存在回执。
- 只读 durable store，恢复/失租/Faulted 时仍可对账；返回原 `Receipt` 的 replay 视图（只改 `Replayed` 标志，不改变原版本/状态/事件序号）。

## 5. checkpoint、journal 与恢复

### 5.1 持久数据模型（内存同形）

内存实现使用与计划 §3.4.1 对应的逻辑行：

| 端口/行 | 关键字段 |
|---|---|
| lease | roomId, ownerInstance, ownerEpoch, expiresAtUtcMs, updatedAtUtcMs |
| state | roomId, stateVersion, lastEventSeq, stateFingerprint, stateHash, rulesetVersion, controlVersion |
| event | roomId, seq, commandVersion, ordinal, eventType/eventSchema/payload, seat/tile/detail |
| command journal | commandVersion, evaluatedStateVersion, request/account/scope/api/payloadSha256, schema, encoded RoundCommand |
| receipt | accountId, scopeId, requestId, api, payloadSha256, status/reason, evaluated/command/eventSeq, detail, createdAt/expiresAt |
| checkpoint | roomId, stateVersion, lastEventSeq, stateFingerprint, state/control/timing/cheat/facts payload+hash, schema/ruleset, bootId, createdAt |
| outbox | matchId, resultVersion, payload+hash, createdAt, publishedAt |
| recovery audit | roomId, kind, UTC, previous/current bootId, downtime, grace, previous/current owner, detail |

P5.2 必须把它们落到真实表，并保留唯一键/外键/行锁语义；不得把“有表/有字典”当作完成。

### 5.2 恢复算法

`TableRecoveryService.RecoverAsync`：

1. `AcquireAsync` 获取新租约（epoch 单调递增）；其他 owner 仍持有 → BLOCKED `RECOVERY_LEASE_HELD`。
2. 读取 `table_state` 和最新 checkpoint；无 checkpoint → FAILED `CHECKPOINT_MISSING`，不静默开新局。
3. 校验 checkpoint schema/ruleset、state version/seq 不超前；校验 control/timing/cheat/facts payload 的 SHA-256。
4. `RoundRecoveryCodec.Decode` → `RoundRecovery.Restore`：重建 `RoundSpec/TilePositionMap/phase/currentSeat/window/result/events`，检查 136 牌守恒、result/事件一致、重建 `SourceFingerprint` 与 checkpoint 相同；再编码并比对 state payload hash。
5. 读取 checkpoint 之后的全部事件和全部 command journal（不是 `SnapshotEventWindow`）。
6. 校验命令/事件连续性：每个 `stateVersion` 恰好一条 journal；事件从 `checkpoint.lastSeq+1` 连续、同一命令 ordinal `0..N-1`；每个已接受版本至少一条事件；journal evaluated version = version-1；event schema/journal schema 已知。
7. 对每个命令调用真实 `RoundStateMachine.Execute`，比较重放事件的 type/ordinal/seat/tile/detail 与持久化事件；比较事件事实 payload 的 commandVersion/ordinal/source fingerprint；比较最终 `SourceFingerprint` 和重新计算的 state hash 与 `table_state`。
8. 终局检查：Domain `IsFinished` 时必须有 `RoundResult` 和对应 `(matchId,stateVersion)` 的唯一 outbox；否则 FAILED。
9. 控制/计时：解码 control checkpoint 并校验 controlVersion 与 state 一致；解码 timing，按显式 policy 排除停机时间 + grace（仅 bootId 不同时），并移除已被重放命令消费的 decision；写 `RecoveryAdjusted` 审计。
10. 返回 `TableRecoveryBaseline`（新 ownerEpoch、round、control baseline、timing）和报告；actor 通过 `CompleteRecoveryAsync` 安装并置严格 recovery generation 栅栏。

失败闭合：坏 checkpoint/未知 schema/事件缺口/重复 ordinal/缺失 journal/错误 state hash/错误 fingerprint/旧 owner/租约被持有/终局缺 outbox 都返回 FAILED/BLOCKED，actor 保持或进入非 Serving；不跳坏事件、不静默重开。

### 5.3 时钟/控制/决策

- checkpoint 持久化 bootId、createdAt UTC、四席 reserve、每个 open decision 的 `remainingBase/remainingReserve/overdue`（相对值，不持久化本地 Stopwatch 原点）。
- 恢复时 `downtime=max(0, utcNow-checkpointUtc)`（bootId 不同才计），`grace` 由显式 policy 提供；只加到仍存活的 decision 的 base，写审计。
- 控制变化（断开/重连/托管/收回/策略）通过 `CommitControlAsync` 原子更新 controlVersion + 对应 checkpoint，不推进 stateVersion/eventSeq；恢复安装精确 control epoch/kind。
- 若跨 boot 仍存在“断线 + 绝对 monotonic grace deadline”，无法可靠换算，恢复 fail closed `RECOVERY_GRACE_TIMING_REQUIRED`；P5.2 必须改存 UTC 依据或显式剩余 grace。本轮没有伪造 grace 值。

## 6. 测试

### 6.1 命令、计数、结果

在 `D:/majiang-wt/p34-receipts`、`D:/Unity/dotnet/dotnet.exe` 下执行：

| 命令 | 结果 |
|---|---|
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal` | **199 passed / 0 failed / 0 skipped**（基线 168，新增 31） |
| `dotnet test dotnet/Mahjong.sln -c Release --nologo -v minimal` | **575 passed / 0 failed / 0 skipped**（Domain 322、Contracts 104、Client 132、Ai 17） |
| `dotnet build dotnet/Mahjong.sln -c Release --nologo -v minimal` | **0 warnings / 0 errors** |
| `dotnet build dotnet/src/Mahjong.TableRuntime/Mahjong.TableRuntime.csproj -c Release --nologo -v minimal` | **0 warnings / 0 errors** |
| `git diff --check` | **exit 0**（无空白错误） |

TableRuntime 全量已自动发现新增测试；未修改统一 verifier、未增加 skip、未删除/弱化原 168 项。

### 6.2 新增 31 项覆盖

回执/scope/事务（`TableDurableReceiptTests.cs`）：

1. `AcceptedReceiptReplayReturnsOriginalWithoutNewVersionOrEvent`
2. `RejectedReceiptReplaysOriginalResultAfterStateAndClockChange`
3. `RecoveryReplaysAfterNonZeroCheckpointAndKeepsRejectedReceipt`
4. `AcceptedCommandHasNonEmptyEventsAndOneContiguousCommandVersion`
5. `LostResponseIsQueryableAndExplicitRecoveryReplaysSameRequest`
6. `TerminalOutboxFaultRollsBackAndRetryWritesOnlyOneResult`
7. `RecoveryIsBlockedWhileAnotherLiveOwnerHoldsLease`
8. `InternalTickCommitsDurablyWithoutClientReceipt`
9. `InternalTimeoutIsJournalledAndReplayedByRecovery`
10. `CheckpointPersistsHostCommittedFactsAndRealCheatLedger`
11. `SameKeyDifferentPayloadOrApiIsConflictWithNoSideEffect`

租约/围栏/并发（`TableDurableLeaseRecoveryTests.cs`）：

12. `ScopeRoleRoomAndCrossAccountQueryAreNotClientClaims`
13. `RevokedOrExpiredScopeRejectsNewRequestsButReplaysCommittedReceipt`
14. `RevocationAtCommitBoundaryRollsBackEverything`
15. `OldOwnerIsStoppedAndNewLeaseEpochRecoversBeforeServing`
16. `SameEpochWrongStateIsRejectedWithoutEventOrPublication`
17. `RejectedReceiptCannotBypassOwnerFence`
18. `TwoActorsSameKeyCommitOnceAndSecondSeesExactReceipt`
19. `EveryPreApplyFaultPointRollsBackAndDoesNotConsumeDecision`（枚举全部 transaction fault point，包括 apply 后丢回包）
20. `LeaseRenewalFailureStopsServingWithoutRetry`
21. `ConcurrentSameKeyAcrossTwoActorsCommitsOnce`
22. `ControlEpochAndDecisionTimingAreRecoveredConsistently`
23. `TerminalCommitWritesOneOutboxAndReplayKeepsItUnique`
24. `RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch`

坏 checkpoint/日志（`TableDurableCorruptionTests.cs`）：

25. `UnknownCheckpointSchemaIsRefused`
26. `CorruptCheckpointPayloadIsRefused`
27. `EventGapIsRefused`
28. `DuplicateOrdinalIsRefused`
29. `MissingCommandJournalIsRefused`
30. `WrongStateHashIsRefused`
31. `CorruptCheatLedgerIsRefused`

测试特点：

- 使用真实 `RoundStateMachine`、真实 `RoundSettlement`/`RoundResult`、真实 `CheatLedger`，没有自定义 reducer 绕过 Domain。
- 故障注入覆盖 `AfterReceiptLookup`、`AfterScopeCheck`、`AfterLeaseCheck`、`AfterStateCheck`、`AfterEventStage`、`AfterReceiptStage`、`AfterOutboxStage`、`BeforeApply`、`AfterApplyBeforeReturn`；不含 sleep/重试式证明。
- 撤销/过期用可控 `BeforeScopeCheckForTest` 门闩在提交边界执行；并发同键用 `Task.WhenAll` + 单锁 store，不靠时序猜测。
- 恢复测试覆盖非零 checkpoint 后续多事件、REJECTED 回执、内部 tick journal、136 物理守恒、控制 epoch/timing、终局 outbox、坏哈希/坏 schema/缺口/重复 ordinal。
- Wire 基线测试在恢复后分别用旧 ownerEpoch、旧 recoveryGeneration 订阅并断言拒绝，再用新 epoch/generation 得到玩家/观战全量基线。

### 6.3 未运行/不可宣称

- 真实 PostgreSQL、数据库迁移、advisory/row lock、备份恢复：**NOT_RUN**（P5.2）。
- 真实进程 kill/DB commit 后崩溃/网络分区：**NOT_RUN**；仅用内存注入 `AfterApplyBeforeReturn` 模拟“已提交未回包”。
- WSS/TableService/P5.1、LocalHost/P4、Unity/P4：**NOT_RUN**，不在本轮。
- V4 Gradle 全量、Unity 许可/SDK/NDK/macOS/Xcode/真机/物理网络：**NOT_RUN**，不能把本报告当平台证据。
- 没有修改或读取黄金 catalog / implementation traceability 的在途返修交付；P3.2/P3.3 既有 168 项继续通过。
- G1/G5 未宣告；P5.1 未提前实现；V4 仍是唯一生产架构。

## 7. 文件与 SHA-256（实现提交）

| 文件 | SHA-256 |
|---|---|
| `dotnet/src/Mahjong.Domain/RoundStateMachine.cs` | `837021c103a2ce21628aa4fbd9399d50e99eb1eaf5937f6dfe08aa7b0b3f6260` |
| `dotnet/src/Mahjong.Domain/RoundRecovery.cs` | `722c4c948cfa4f52204cc7d3ab20b526c6ac53df29718906f1f8dfca849ec26e` |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `6b6389bdab0f27fc516c5cacfaa8b8f40955323b4cac3629f378cf9dd7bad1ef` |
| `dotnet/src/Mahjong.TableRuntime/TableActorMessages.cs` | `9da34eec455321a34e2363c505811d7bd1d17628f77a875c5a5d7475c2745abe` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs` | `b0c2ce55a19b37940931f49523846c6e059cadcef11af4df71c30546a0f85bbf` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimePorts.cs` | `649dc5879c3664c27a6b5999d15c801ef6a19c59c482610fb13f99ad79929d59` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeReasonCode.cs` | `32d640e1f64b7a8d6b3f3940405fcd71e8a0464a77d9a92abf3b67eac5879f24` |
| `dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs` | `ccbaf581aad856bc997ab1a827f48eaf4db76b664cf39dcf973a596f68dea55a` |
| `dotnet/src/Mahjong.TableRuntime/InMemoryTableStore.cs` | `61838e1e315888c16b5caa5a94c8962957935212b38937daa6431f6988e2df8c` |
| `dotnet/src/Mahjong.TableRuntime/TableCheckpointCodecs.cs` | `3de2f4ee0a463aab044adf50a35d81f3902229f2ec6d74ea618f6c48cca3884d` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryPayloads.cs` | `f038da76c82ca20d7e61f0314d1bef86cbc4e4d2904ff9c4af230b4914237378` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryService.cs` | `4b7856a1e09a8e4746760436af6204903958064faffeff250e820fd39e04df48` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableDurableTestSupport.cs` | `44f6b842b56fce6c5b35f61fcf40bfed9c0cd7aa2a6e1fc3b3870c2a3d94b0ba` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableDurableReceiptTests.cs` | `0bdbdc908c956653075d926a3995fe8875f8ad0f7cd4b48aea369ba0057097ef` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableDurableLeaseRecoveryTests.cs` | `a81dc25ee28bb2351ba3345f15f03c3691e4f2e829d6230db6e97a44ccb8179a` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableDurableCorruptionTests.cs` | `f759a028451030797085a9ef08dbdd4ecfd791d03b9e470ce3a4cde90ef2b94a` |

实现提交差异：16 个文件，`8028 insertions(+), 16 deletions(-)`（`git show --stat 9e909ea`）。未新增项目/包引用，未修改 `Mahjong.sln`。

## 8. 明确局限与 P5.2 接入要求

1. **真实 PG**：必须用单一事务实现 §3 顺序；advisory lock 按 room、receipt 查重、scope `FOR SHARE`、lease `FOR UPDATE`、state CAS 行数检查、event `(room,seq)`/`(room,command_version,ordinal)` 唯一键、outbox 唯一键。内存实现不是 PG 证明。
2. **PAUSED_STORAGE**：本轮内存 store 不会出现 DB 慢/断；actor 将存储异常/未知结果映射为 Faulted/LeaseLost。P5.2 需要明确 `PAUSED_STORAGE` 状态、拒绝新命令、显式恢复，不得自动重试掩盖漂移。
3. **控制 journal**：当前每个 control change 与对应 checkpoint 原子写，没有独立 control event 表；P5.2 可增加 `table_control_event` 并保留同版本 CAS。恢复已能校验 controlVersion/epoch/kind。
4. **私有状态**：只持久化真实 `CheatLedger`；`TileMarkState/SleeveBloomState/BeamSwapState` 的完整私有字段仍由宿主/P5.2 持有，本轮只提供可选 opaque facts/private payload + SHA-256 绑定。P5.2 必须提供严格 codec 和语义校验，不能把 opaque hash 校验写成“私有状态已重建”。
5. **P3.2 facts**：actor 通过 `CommittedFactsProvider` 持久化宿主提供的完整 committed-facts payload；恢复验证 hash/逐字节回放，不读取可裁剪 `SnapshotEventWindow`，也不伪造 facts。投影 source 的语义绑定仍属 P3.2/宿主。
6. **回执清理/保留**：`ReceiptRetention` 默认 24h、可配置；本轮不实现后台清理。P5.2 清理前必须确认无未决 outbox/审计/对账需求，且不短于 scope 有效期。
7. **租约续租循环**：本轮提供显式 `RenewLeaseAsync`；P5.1/宿主需周期调用，失败立即停服，不做“最后一次写入”。
8. **checkpoint 周期**：本轮支持显式 `SaveCheckpointAsync`，终局提交原子带 checkpoint；非终局周期（局结束/200 事件）由 P5.2 调度。恢复要求至少存在初始 checkpoint；P5.2 建房时必须原子创建。
9. **outbox 消费者**：本轮只实现唯一写入/查询/mark-published；真钱钱包/资产消费者、`(accountId,matchId,resultVersion,entryType)` 幂等入账属于 P5.2/P5.4，不伪造。
10. **事件/复杂度**：`RoundCommand`/checkpoint codec 是 Domain 内确定性二进制 helper，不依赖 TableRuntime/网络/IO；若规则版本扩展命令字段，必须升 schema 并保留旧 reader/迁移，不能静默忽略未知字段。
11. **测试真实度**：故障注入是单进程内存锁；没有真实 DB 两阶段/网络分区/进程 kill。P5.2 必须用 PostgreSQL 测试容器/故障注入重跑 F4–F6 矩阵。
12. **平台**：Unity/IL2CPP/Android/iOS/macOS/Xcode/真实设备/物理网络/WSS 完全 NOT_RUN；V4 仍是唯一生产。

## 9. 交付状态

- 本地实现提交：`9e909ea6b7dfb411b1e2511fa4f0f9d05307ba38`。
- 文档提交：最终本地 HEAD（本文件）。
- 工作树在本轮结束后 clean；未 push、未建 PR、未触发 CI、未转派。
- 等待 caller 独立验收并负责唯一 PR19 `feat/v5-1-integration -> v5` 及 merge-back；G1 需 caller 综合黄金/迁移完整性与平台证据，本报告不自行宣告 G1/G5。
