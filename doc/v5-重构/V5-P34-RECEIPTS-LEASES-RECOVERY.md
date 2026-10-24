# V5 P3.4 回执、租约和恢复端口合同交付（第 2 轮返修）

- 状态：**第 2 轮本地返修完成，等待 caller 第 2 轮独立验收**。不是 G1/G5，不是 V5 全链通过；V4 仍是唯一生产架构。
- 唯一工作树：`D:/majiang-wt/p34-receipts`（分支 `wt/v5-p34-receipts`）。
- 冻结基线：`daeb19a64951174426924026fe974c8ae5caacd9`。
- 第 1 轮实现提交：`9e909ea6b7dfb411b1e2511fa4f0f9d05307ba38`。
- 第 2 轮实现提交：`343b7a2271d5da4089646f48f521fcb69016d637`（`fix(v5-p34): fence checkpoints and rebuild durable timing`）。
- 本文件的提交为最终本地 HEAD；未 push、未建 PR、未触发 CI、未转派。
- caller 的 `CodexP34ReviewProbes.cs` 被保留、纳入提交并通过；未删改探针。

## 1. 第 2 轮修复结论

| 缺陷 | 本轮修复 |
|---|---|
| R1 恢复时钟把相对 remaining 再加 downtime | 改为按 checkpoint/timing journal 的 `bootId + UTC + monotonic capture` 计算：同 boot 只扣实际活跃时间；跨 boot 排除 downtime，只加显式 grace；不再出现 `remaining + 60000 + grace`。 |
| R1 连续恢复重复赠 grace | 每次恢复安装前原子追加新的 timing observation（当前 boot/UTC/monotonic + 调整后剩余）；第二次恢复从该 observation 计算，不再重复加 grace。 |
| R2 checkpoint 后 reserve 消费丢失 | 每个已接受命令的事件/journal/回执/outbox 与**post-command timing snapshot** 同一事务提交；REJECTED 不变 timing。恢复使用最新 timing journal，不靠删除 decision 保留旧 reserve。 |
| R2 checkpoint 后新 open decision 无持久记录 | `OpenDecisionAsync` 在 durable 路径先原子 `AppendTimingAsync`（owner/state/control/timing CAS），成功后才安装内存 pending；失败不安装。 |
| R3 `SaveCheckpointAsync` 绕过 owner/control | 新增 `TableCheckpointFence`；checkpoint 写入必须同事务校验 owner instance + epoch + live lease + stateVersion + controlVersion + timingVersion。旧 owner 立即 LeaseLost，stale control/state 立即 Faulted。 |
| R4 恢复 baseline 不携带/安装 cheat/facts/history | `TableRecoveryBaseline` 现携带真实 `CheatState`（由持久化 ledger 重建）、committed-facts envelope 和完整 committed history；actor 通过 `ITableRecoveryStateSink` 安装，缺 sink/缺可信 facts 时 fail closed。还原后 `_committedEventLog` 从持久历史重建，不再依赖 `SnapshotEventWindow`。 |
| R4 outbox 只检查存在 | 恢复时重新编码终局 result 并逐字节/摘要比对 outbox payload；不一致 fail closed。 |
| scope 读后撤销可插入提交 | `InMemoryRequestScopeAuthority` 提供持有锁的 `TableScopeReadLease`；store 在 scope 校验后一直持有到 apply/rollback，revoke 只能在线性化点之前或 commit 之后生效。 |

## 2. 时钟模型（R1）

### 2.1 持久化 observation

`TableTimingCheckpoint` 在原有四席 reserve + open decisions 之外持久化：

- `CapturedAtUtcMs`
- `CapturedMonotonicMillis`
- `BootId`

每个 `TableTimingJournalRecord` 还记录 `TimingVersion`、`StateVersion`、`ControlVersion`。以下操作各自推进 `TimingVersion+1` 并写完整 observation：

- 已接受命令（atomic command batch 内）；
- `OpenDecisionAsync`；
- `SaveCheckpointAsync`；
- control change checkpoint；
- 恢复安装前的最终 adjusted observation。

### 2.2 恢复计算

令 `t` 为选中的最新 timing observation（优先 `timingRow`，否则 checkpoint；两者都必须与当前 state/control 一致）：

- **同 boot**：`activeDelta = max(0, nowMonotonic - t.CapturedMonotonic)`；不赠 grace；base-first 扣减 base，再扣 reserve，超出部分记 overdue。
- **跨 boot**：`activeDelta = 0`；`downtime = max(0, nowUtc - t.CapturedAtUtc)` 只写审计，不加入 remaining；`remainingBase += grace`。grace 只加一次。
- UTC 回拨跨 boot、或同 boot monotonic 回拨 → `CLOCK_REGRESSION`，不 Serving。
- 跨 boot 只要 control snapshot 仍有“断线 + 绝对 monotonic grace deadline”，无论本次 `graceMillis` 是否为 0，都 fail closed `RECOVERY_GRACE_TIMING_REQUIRED`；不会因 grace=0 放过旧 monotonic grace。
- 恢复结束写 `RecoveryAdjusted` 审计，包含实际 downtime、applied grace、前后 boot、前后 owner epoch。

第 1 轮测试 `ControlEpochAndDecisionTimingAreRecoveredConsistently` 中 `originalBase+3000` 固化了错误行为；本轮按合同改为 `originalBase+1000`（跨 boot 只加 1000 grace，不加重置的 2000 active/downtime）。

## 3. Timing 与 reserve 的原子合同（R2）

`TableDurableCommitRequest` 现包含 `TableTimingCheckpoint Timing` 和 `TimingVersion`：

- 已接受批次必须有 `TimingVersion == state.TimingVersion + 1`；事件、command journal、回执、outbox、checkpoint 和 timing record 同一事务。
- actor 在提交前克隆 `DecisionClock`，对 `Complete`/`Expire` 在 clone 上执行，生成 post-command timing snapshot；提交成功后才安装 clone，不再在提交后二次 `Complete/Expire`。
- REJECTED 批次不写 timing，不推进 reserve/decision。
- `TableTimingJournalRecord` / checkpoint 的 `TimingVersion` 是 CAS：旧 timing 写入返回 stale，actor 停服。
- 恢复从最新 timing journal 取四席 reserve 和 decisions；不再用 `consumedSeats` 删除旧 decision 后保留旧 reserve 的近似做法。
- checkpoint 后新 open decision 的 `AppendTimingAsync` 与 owner/state/control/timing fence 同一锁/事务；失败不安装内存 pending。
- 提交后丢回包：timing/事件/回执已提交，查询可对账；显式恢复后同 request 回放不重复消费 reserve。

## 4. Checkpoint 栅栏（R3）

`ITableStore.SaveCheckpointAsync` 改为：

```csharp
ValueTask<TableDurableCommitResult> SaveCheckpointAsync(
    TableCheckpoint checkpoint,
    TableCheckpointFence fence,
    CancellationToken ct);
```

`TableCheckpointFence` 包含 room、ownerInstance、expectedOwnerEpoch、expectedStateVersion、expectedControlVersion、expectedTimingVersion。

`InMemoryTableStore` 在同一 `_gate`/事务内：

1. 校验 lease 的 owner/epoch 且未过期；
2. 校验 stateVersion；
3. 校验 controlVersion；
4. 校验 timingVersion；
5. 校验 checkpoint 的 stateVersion/lastSeq/stateFingerprint/stateHash/controlVersion/timingVersion；
6. 全部通过才替换 checkpoint、timing observation、可选 facts。

旧 owner 返回 `STALE_OWNER` 并由 actor 进入 LeaseLost；同 epoch 但旧 control 返回 `STALE_CONTROL_VERSION` 并由 actor 进入 Faulted。没有“actor 先读 lease”的 TOCTOU 线性化点。

## 5. Scope 线性化（额外核对）

`InMemoryRequestScopeAuthority` 新增 `BeginScopeForCommit`，返回 `TableScopeReadLease`：

- store 在回执查重通过、开始 scope 校验时获取该 lease；
- lease 持有 scope 的同一把锁直到 commit apply/rollback；
- `Revoke` 只能发生在 scope 校验之前（commit 看到 revoked 而拒绝），或 commit 释放锁之后（只影响后续命令）；
- 不再存在“读取 scope 后释放锁、apply 前 revoke 插入”的窗口。

测试用 `AfterScopeCheckForTest` 门闩阻塞 commit、并发启动 revoke，确认 revoke 在 apply 前不能完成；commit 完成后 revoke 才完成，后续命令被 `REQUEST_SCOPE_EXPIRED` 拒绝。

## 6. 冷恢复数据完整性（R4）

### 6.1 恢复数据

`TableRecoveryService` 现在返回的 `TableRecoveryBaseline` 包含：

- 重建后的 `RoundStateMachine`（136 牌、状态 hash/fingerprint、窗口/终局）；
- 精确 control baseline 与 adjusted timing；
- **真实 `CheatState`**：从持久化 `CheatLedger` 条目重建；若 `CheatState.Version != Ledger.Version`（存在本任务未建模的 TileMark/Sleeve/Beam 私有子状态），恢复返回 `CHECKPOINT_INVALID`，block Serving，不把空子状态当成功。
- **typed committed-facts envelope**：`TableCommittedFactsCodec` 绑定 schema、roundId、stateVersion、body SHA-256；body 本身是宿主定义的 P3.2 facts/private payload，通过 `ITableRecoveryStateSink.Install` 安装。缺 facts（当 `RequireCommittedFacts` 为真）或 decode 不匹配当前 round/state 时 fail closed。
- **完整 committed history**：checkpoint 的 history payload + checkpoint 之后全部事件；不是 `SnapshotEventWindow`。actor 在 `ProcessCompleteRecovery` 中用 baseline 历史重建 `_committedEventLog`，`_recentEvents` 只按窗口截取展示。
- 终局 outbox：恢复重新编码 `RoundResult`，与持久化 outbox payload 逐字节/摘要比对；不一致 fail closed。

### 6.2 宿主安装边界

`TableDurableContext.RecoveryStateSink` 必须提供当 recovery 携带 cheat/facts 时；actor 在安装任何 Domain 状态前调用：

```csharp
void Install(TableRecoveryAuxState state);
```

`TableRecoveryAuxState` 类型化携带 `CheatState`、facts payload 和完整 history。sink 抛错时 actor 保持 Faulted，不 Serving。该边界避免“只校验 hash 后丢弃”而宣称恢复成功。

### 6.3 真实投影验证

`RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch` 使用新的 `RecoveredProjectionSource`（不是 `TestProjectionSource`）读取 sink 安装的 `CheatState`，在恢复后的 actor 上取得玩家/观战严格 Wire 基线；同时断言 old ownerEpoch、old recoveryGeneration 被拒绝。`FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero` 用 `SnapshotEventWindow=0` 证明历史来自持久 checkpoint/journal 而非诊断窗口。

### 6.4 仍需明确限制

- `CommittedFacts` body 的语义 codec 仍属宿主/P5.2；本任务提供的是严格 envelope + state/round/SHA-256 绑定 + 安装 sink，不声称 TableRuntime 能自行解释所有 P3.2 public/private 事实。宿主未提供 body 或 sink 时，durable recovery 默认 fail closed。
- 非空 `TileMark/SleeveBloom/BeamSwap` 私有子状态未在本任务建模；checkpoint 记录其版本，恢复检测到非空即 fail closed，不静默替换成空状态。
- 真实 PostgreSQL/P5.2、备份恢复、真实进程 kill、WSS/TableService、LocalHost、Unity/IL2CPP/真机/物理网络仍 **NOT_RUN**。

## 7. 测试与证据

在 `D:/majiang-wt/p34-receipts`、`D:/Unity/dotnet/dotnet.exe` 下：

| 命令 | 结果 |
|---|---|
| `dotnet test .../Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal -- --filter-class '*CodexP34ReviewProbes'` | **3 passed / 0 failed / 0 skipped** |
| `dotnet test .../Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal` | **218 passed / 0 failed / 0 skipped**（原 168 + 本轮/新增 50，含 caller 3 探针） |
| `dotnet test dotnet/Mahjong.sln -c Release --nologo -v minimal` | **575 passed / 0 failed / 0 skipped**（Domain 322、Contracts 104、Client 132、Ai 17） |
| `dotnet build dotnet/Mahjong.sln -c Release --nologo -v minimal` | **0 warnings / 0 errors** |
| `git diff --check` / `git show --check HEAD` | **exit 0** |

本轮新增/调整的测试覆盖：

- R1：`RelativeRemainingTimeMustNotGainDowntimeTwice`、`SameBootRecoveryChargesActiveElapsedAndDoesNotGrantGrace`、`LongCrossBootDowntimeIsExcludedButGraceIsAppliedOnce`、`UtcRollbackAcrossBootFailsClosed`、`TwoConsecutiveRecoveriesDoNotGrantGraceTwice`。
- R2：`ReplayMustKeepReserveConsumedAfterCheckpoint`、`DecisionOpenedAfterCheckpointIsRecoveredWithoutLoss`、`MultipleSeatsAndInternalTimeoutKeepReservesConsistent`、`FailedCommitDoesNotAdvanceTimingJournalAndRetrySucceeds`、`LostResponseTimingIsRecoveredAndReplayDoesNotReconsume`。
- R3：`OldOwnerMustNotOverwriteNewOwnersControlCheckpoint`、`SaveCheckpointRejectsExpiredLeaseAndLeavesCheckpointUnchanged`、`SameEpochOldControlCannotOverwriteCheckpoint`、`ConcurrentOldAndNewOwnerCheckpointSavesLeaveNewOwnerState`。
- scope：`RevokeAfterScopeReadCannotInterleaveBeforeCommitApply`、既有 `RevocationAtCommitBoundaryRollsBackEverything`。
- R4：`RecoveryInstallsTypedCheatLedgerAndLatestCommittedFacts`、`MissingRecoverySinkFailsClosedInsteadOfServing`、`UnmodeledPrivateCheatSubstateIsRefused`、`FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero`、`RecoveryInstallsStrictWireBaseline...`、`TerminalCommitWritesOneOutboxAndReplayKeepsItUnique`（含 outbox payload 一致性）。

## 8. 变更文件与 SHA-256（第 2 轮）

| 文件 | SHA-256 |
|---|---|
| `dotnet/src/Mahjong.Domain/DecisionClock.cs` | `b02e20150b67beb330a42a7201e6fadb075bd95873fc67211ea5858d9f19fafe` |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `673a1ed591484de06e28d9a7feee129b28dbc42f876f35a991d59498483d6bcc` |
| `dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs` | `82b5912b643aedd2e48e4279beb3f833cb41838acecec8b5395194b0d905015f` |
| `dotnet/src/Mahjong.TableRuntime/InMemoryTableStore.cs` | `6221b2b27abc307f9428cf46b9a119d3cec8bf71faf64247daab9e1850587e6d` |
| `dotnet/src/Mahjong.TableRuntime/TableCheckpointCodecs.cs` | `6e210de402c9c6d261897a3997cca9d34fbdb7608e6c9cbf43aac7aaadfd9279` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryPayloads.cs` | `531c618855a8e7b90736dc67559874fdea832484c7732d26c26d96bad4b1a892` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryService.cs` | `a8029d8f7c014e73c2fc4e94c6621e6b4113a09b6fc67aa7fb61d9afeda28fdc` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs` | `696e71bb7dbb51f4e858dda543c3897f009fb4c7d083e7783e1a8b95a17cc366` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeReasonCode.cs` | `7fece62eaf709597e10eca661afbad64cb2dbc3626a60a32af401b3e95840343` |
| `dotnet/tests/.../TableDurableTestSupport.cs` | `27fa7163335782f8dda23f82670333436c598d47a1880a83d651f6785b0d8e46` |
| `dotnet/tests/.../TableDurableReceiptTests.cs` | `904a54ce1be4109498e8037b007398376f4d2656ea092b1c5478a08f679dd639` |
| `dotnet/tests/.../TableDurableLeaseRecoveryTests.cs` | `5f0417033233eaf6c6486d123cae0dcef2cdb7c4d6e2abf465946436f9259eec` |
| `dotnet/tests/.../TableDurableCorruptionTests.cs` | `e2a49fbe8e5a0e2f28dc6f913c6d80e9779ce184d18ac94803224e6f1d163660` |
| `dotnet/tests/.../TableDurableTimingRecoveryTests.cs` | `546e67ce13c917ef6632b5ee7614d824c760377f0b3965bc42bf32cae0efcc3d` |
| `dotnet/tests/.../CodexP34ReviewProbes.cs` | `3873c6ac75f10d1555b317d1f114402f5951c35708c1998c012d998d6e25212c` |

第 2 轮实现提交差异：15 文件，`2475 insertions(+), 130 deletions(-)`。未新增项目/包引用，未修改 `Mahjong.sln`、统一 verifier、黄金 fixtures、任务书或 CI。

## 9. 明确 NOT_RUN 与边界

- 真实 PostgreSQL、migrations、advisory/row lock、DB 慢/断、备份从空库恢复：**NOT_RUN**（P5.2）。
- 真实进程 kill/DB 提交后崩溃/网络分区：**NOT_RUN**；`AfterApplyBeforeReturn` 是内存注入的“已提交未回包”，不等于真实崩溃。
- WSS/TableService/P5.1、LocalHost/P4、Unity/IL2CPP/真机/物理网络：**NOT_RUN**。
- V4 Gradle 全量、Unity 许可/SDK/NDK/macOS/Xcode：**NOT_RUN**；V4 仍是唯一生产版本。
- G1/G5 未宣告；P5.1 未提前实现；P5.2 的真实 DB/消费者仍未交付。

## 10. 交接

- 第 2 轮实现 HEAD：`343b7a2271d5da4089646f48f521fcb69016d637`。
- 本文件所在提交为最终本地 HEAD。
- 等待 caller 第 2 轮独立验收；caller 负责唯一 PR19 `feat/v5-1-integration -> v5` 及 merge-back。本报告不自行通过 G1/G5。
