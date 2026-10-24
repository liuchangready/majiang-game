# V5 P3.4 回执、租约和恢复端口合同交付（第 4 轮返修）

- 状态：**第 4 轮本地返修完成，等待 caller 第 4 轮独立验收**。不是 G1/G5，不是 V5 全链通过；V4 仍是唯一生产架构。
- 唯一工作树：`D:/majiang-wt/p34-receipts`（分支 `wt/v5-p34-receipts`）。
- 冻结基线：`daeb19a64951174426924026fe974c8ae5caacd9`。
- 第 1 轮实现：`9e909ea6b7dfb411b1e2511fa4f0f9d05307ba38`。
- 第 2 轮实现：`1438f2e1b9caf4fbe9531cd3cde63169e219d747`（主体 `343b7a2271d5da4089646f48f521fcb69016d637`）。
- 第 3 轮实现：`46dfffd2aedfed3329c1b9e7d7318dfcecbf200d`（`fix(v5-p34): install explicit grace and typed projection facts`）。
- 第 4 轮实现：`8f3f580d32203ef203431eb2e5c88f9f1ad66bea`（`fix(v5-p34): validate recovered event facts before serving`）。
- 本文件提交为最终本地 HEAD；未 push、未建 PR、未触发 CI、未转派。
- caller 的 `CodexP34ReviewProbes.cs`、`CodexP34Round2ReviewProbes.cs`、`CodexP34Round3ReviewProbes.cs` 均保留、纳入提交并通过；未删除或弱化探针。

## 1. 第 4 轮修复结论

|缺陷|修复|
|---|---|
|R7 facts 数量相等但重复/错 type 仍 Serving|恢复前新增 `TableVisibilityProjection.ValidateRecoveredEventFacts`：每个 committed history 事件必须恰好对应一个 fact；seq 不得重复；fact.Type/CommandVersion/EventOrdinal 必须与 `TableEventProjection.FromRoundEvent` 的投影完全一致；不再只比较数量或“存在某条匹配”。|
|R8 fact 身份正确但字段为空仍 Serving|同一验证路径实际调用 `TableEventProjection.Project` + `TableProjectionContractMapper.MapEvent`，强制按事件类型校验 turnNumber/wall/riichiDiscard/tsumogiri/meld/yaku/fu/score/payments 等必需字段；使用 settlementBatch=true/revealUra=true 的验证策略覆盖终局 win/settlement facts，但不对外发布。|
|R8 独立新宿主 Wire 断言不足|`RecoveryInstallsStrictWireBaseline...` 增加非零 checkpoint、2 个事件、`SnapshotEventWindow=1`、真实费用 ledger 的玩家/观战语义断言；新增终局 `TerminalRecoveryWireExposesTypedSettlementFromPersistedFacts`，从持久 facts 恢复后断言 TsumoWin settlement、玩家私有 ledger 与 spectator settlement；outbox 仍逐字节一致。|
|测试用空 event facts 掩盖缺口|删除 `MinimalEventFacts`；默认 harness 使用真实 `WireEventFacts.Build`，终局测试使用显式 `TerminalFactsSource`（真实 win yaku/han/fu/score），不再用空 facts 保旧测试。|

## 2. 历史轮次修复结论

|缺陷|修复|
|---|---|
|R5 跨 boot 开放决策在真实 actor 安装时 `Remaining base 6000 > Config.Base 1000`|恢复 grace 改为**独立字段** `RecoveryGraceMillis`/`RecoveryGraceApplied`，不塞进正常 base。Domain 新增显式 `RestoreDecision(..., recoveryGrace, applied)`；base 仍受 `Config.BaseMillis` 原校验，grace 在 base 之后、reserve 之前消费，并记录到 `DecisionTiming`。actor 安装时拆分 total/base 后调用该重载。|
|R5 只测 service 不测 actor|新增真实新宿主 actor 跨 boot 恢复测试：sink、open decision、deadline=`now+6000`、恢复后真实命令与 timeout。|
|R6 sink 安装期间租约过期仍 Serving|`ProcessCompleteRecoveryAsync` 在 sink/history 之后、任何 `Serving`/`Recovered` 发布之前执行 `RevalidateRecoveryInstallAsync`：owner instance+epoch、未过期 lease、state/control/timing、checkpoint 全部重验；失败 Faulted，无成功 publication。|
|R6 owner/timing/异常未覆盖|新增 sink 期间强制 owner 转移、timing 追加、sink 抛异常的确定性测试；均无 `Recovered` 发布。|
|R4 facts 仍是无参数 `Func<byte[]>`、RecoveredProjectionSource 仍用 fixtures|新增严格 `TableProjectionFactsCodec`（真实 `TableProjectionContextData` 类型，含 public/private/event/session/policy/concealed）；`ProjectionFactsProvider(TableProjectionContextRequest)` 捕获真实 P3.2 facts。`TableRecoveryBaseline/AuxState` 携带 typed `TableProjectionContextData`；`RecoveredProjectionSource` 恢复后只使用 sink 安装的持久 facts/cheat，`UsedRecovered` 由测试断言。|
|R4 facts 缺失/损坏/不完整仍可 Serving|codec 强制 round/state、4 个 public、4 个 private；恢复前用实际历史做唯一一一覆盖、type/seq/version/ordinal 绑定与逐事件 P3.2 投影/Contracts 字段校验；缺 facts/坏 schema/不匹配均阻止 Serving。|

## 2. R5：受控恢复 grace 贯通 service→baseline→actor→DecisionClock

### 2.1 数据模型

`TableRecoveredDecision` 新增：

- `RecoveryGraceMillis`：恢复策略给出的剩余 grace；
- `RecoveryGraceApplied`：是否已经授予过 grace，防止下一次恢复重复赠时。

`TableTimingCheckpoint` codec 持久化这两个字段。`DecisionTiming` 新增同名属性；`CalculateReserveSpent` 从 `BaseMillis + RecoveryGraceMillis` 之后才开始消耗 reserve。

`DecisionClock` 保留原 `RestoreDecision`（正常 base 校验）并新增：

```csharp
RestoreDecision(key, now, remainingBase, remainingReserve, overdue,
                recoveryGraceMillis, recoveryGraceApplied)
```

- `remainingBase <= Config.BaseMillis` 仍强制；
- grace 独立校验非负；base 先消费，grace 其次，reserve 最后，overdue 按同一顺序扣减；
- 恢复 deadline = `now + baseRemaining + graceRemaining + reserveRemaining`；
- `RecoveryGraceApplied` 表示已授予，后续恢复不再叠加。

### 2.2 兼容观察值

`TableRecoveredDecision.RemainingBaseMillis` 继续报告 **base+grace** 的总剩余（旧探针 `remaining+500` 仍成立）；actor 在调用 Domain 时拆分：

```csharp
restoreBase = max(0, RemainingBaseMillis - RecoveryGraceMillis)
```

因此 Domain 的 base 上限不被绕过，deadline 又包含显式 grace。正常决策 `RecoveryGraceMillis=0`，行为不变。

### 2.3 行为

- 跨 boot：不把 downtime 加进 remaining；只加策略 grace 一次。
- 同 boot：按 monotonic 活跃时间扣减 base/grace/reserve，不加 grace。
- 连续恢复：timing observation 带着 `RecoveryGraceApplied`，第二次恢复不再赠 grace。
- 恢复后真实 actor 可在 grace 截止前提交命令；超过 deadline 的 `TickAsync` 产生 timeout，均写入正常事件/journal/timing。
- 非空旧 monotonic grace 在跨 boot 时仍 fail closed，即使本次 `graceMillis=0`。

## 3. R6：完整安装边界后的最终 fence

`ProcessCompleteRecoveryAsync` 顺序改为：

1. 校验 baseline、control/timing/history；
2. 调用 `ITableRecoveryStateSink.Install`（宿主准备/安装）；
3. 校验完整 committed history 尾部；
4. **`RevalidateRecoveryInstallAsync`**：重新读取 lease/state/checkpoint/timing，确认：
   - owner instance 与 `baseline.OwnerEpoch` 一致且租约未过期；
   - stateVersion/lastEventSeq/fingerprint/hash/controlVersion/timingVersion 与 baseline 一致；
   - checkpoint 不领先 state/control/timing；
   - timing journal 的 version/state/control 与 baseline 一致；
5. 只有通过后才安装 `_round/_control/_decisionClock/_pending` 并设置 Serving/发布 `Recovered`。

失败路径：

- sink 抛异常 → `RECOVERY_STATE_SINK_FAILED`；
- 安装期间租约过期 → `STALE_OWNER_EPOCH`；
- owner 转移/control/timing/state 变化 → `RECOVERY_STATE_CHANGED`/`RECOVERY_TIMING_INVALID`；
- 任何失败都进入 Faulted，不发布成功、不暴露可用玩家/观战基线。

测试用 `CallbackRecoverySink`/`ActionRecoverySink` 推进 clock、Acquire 新 epoch、追加 timing、抛异常；均断言 `recovered.Recovered == false`、Lifecycle != Serving、无 `Recovered` publication。

## 4. R4：真实 P3.2 facts 的捕获、codec、安装与投影

### 4.1 捕获接口

`TableDurableContext` 移除无参数 `Func<byte[]> CommittedFactsProvider`，改为：

```csharp
Func<TableProjectionContextRequest, TableProjectionContextData?>? ProjectionFactsProvider
```

actor 用真实 `RoundStateMachine`、`StateVersion`、`EventSeq`、owner/control/recovery generation、UTC/monotonic 和完整 committed events 构造 request；provider 或现有 `ITableProjectionContextSource` 返回真实 `TableProjectionContextData`。若返回 null 或 round/state 不匹配，提交/checkpoint 失败，不写入伪造 facts。

### 4.2 严格 codec

`TableProjectionFactsCodec`（MJPF schema 1）编码：

- 4 个 `TableSeatPublicFacts`：seat、riichi、`TableMeldFact`（type/tiles/called/fromSeat）、`DiscardView`；
- 4 个 `TableSeatPrivateFacts`：furiten/preparation/discard history；
- `TableSessionProjection`；
- `TableProjectionPolicy`；
- 全部 `TableProjectionEventFact` + `TableProjectionEventFacts` 字段（turn/wall/meld/yaku/score/breakdown/payments/scores/offer/beam/hand/target 等，含 presence tag）；
- `TablePublicConcealedMeldFact` 计数。

Decode 强制 round/state 匹配当前恢复目标、public/private 各 4、无尾随字节、未知 schema/枚举/长度拒绝。

### 4.3 恢复验证与安装

- `TableRecoveryService` 读取 store 的最新 facts（不是旧 checkpoint 快照），用 codec 解码；
- `TableVisibilityProjection.ValidateRecoveredFacts` 以恢复后的 round 校验 public/private/meld 与 136 牌；
- `TableVisibilityProjection.ValidateRecoveredEventFacts` 以完整 `fullHistory` 做唯一、一一覆盖校验：
  - 每个 committed event 恰好一条 fact；
  - fact seq 不得重复，也不得遗漏；
  - fact.Type/CommandVersion/EventOrdinal 必须与 `TableEventProjection.FromRoundEvent` 完全一致；
  - 对每个可见 audience 调用现有 `TableEventProjection.Project` + `TableProjectionContractMapper.MapEvent`，按事件类型校验 turnNumber/wall/riichiDiscard/tsumogiri/meld/yaku/fu/score/payments 等必需字段；
  - 以 `settlementBatch=true`/`revealSettlementUra=true` 的验证策略覆盖终局 win/settlement facts，但不对外发布；
- `TableRecoveryBaseline.ProjectionFacts` 是 typed `TableProjectionContextData`；
- `TableRecoveryAuxState.ProjectionFacts` 交给宿主 sink；
- `ProcessCompleteRecoveryAsync` 在 sink 安装后仍执行 §3 的 lease/state/control/timing 最终 fence。

任何缺失、重复、错 type/ordinal/version、空必需字段都在 sink Install 之前返回 `CHECKPOINT_INVALID`，无 `Recovered` 发布、无可用玩家/观战基线。

### 4.4 独立新宿主的 Wire 验证

`RecoveredProjectionSource`：

- 恢复前只用于捕获真实 live facts（`_live` 带真实 `WireEventFacts`）；
- sink 安装后 `Capture` **只**读取 `Sink.Installed.ProjectionFacts` 与 `Sink.Installed.Cheat`，校验 round/state 后返回，不调用 `V32Fixtures.BuildPublicFacts/BuildPrivateFacts`，不 fallback `V32Fixtures.Cheat`；
- `UsedRecovered` 由测试断言为 true。

严格 Wire 断言：

- `RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch`：非零 checkpoint、2 个 Discard/Draw 事件、`SnapshotEventWindow=1`、真实 `TILE_MARK` 费用 ledger；玩家帧断言 stateVersion、私有 closed tiles/discard history、cheat ledger entry 与 balance；观战帧断言 2 张弃牌、无 settlement events；并断言 old ownerEpoch/old recoveryGeneration 拒绝。
- `TerminalRecoveryWireExposesTypedSettlementFromPersistedFacts`：真实 TsumoWin+RoundEnded facts（显式 yaku/han/fu/score）持久化后恢复；玩家/观战帧断言 TsumoWin settlement 非空，outbox payload 与重建 `RoundResult` 逐字节一致。
- `FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero` 证明历史来自持久 checkpoint/journal，而非 `SnapshotEventWindow`。
- 测试不再使用 `MinimalEventFacts`；默认 harness 使用真实 `WireEventFacts.Build`，终局测试使用 `TerminalFactsSource`。

### 4.5 仍 fail closed 的未建模状态

- 非空 `TileMark/SleeveBloom/BeamSwap` 私有子状态仍未建模；checkpoint 记录总版本，恢复检测到 `CheatState.Version != Ledger.Version` 即失败，不替换为空状态。
- 真正的 P3.2 facts 语义由宿主 `ITableProjectionContextSource`/`ProjectionFactsProvider` 提供；TableRuntime 只做严格类型化持久化、校验和安装，不自行推导未建模业务值。
- 缺 facts、未知 schema、round/state 不匹配、private/event facts 不完整、坏哈希均阻止 Serving。

## 5. 测试与证据

| 命令 | 结果 |
|---|---|
| caller `*CodexP34ReviewProbes`（旧 3 探针） | **3 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round2ReviewProbes`（2 探针） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round3ReviewProbes`（3 cases） | **3 passed / 0 failed / 0 skipped** |
| `dotnet test .../Mahjong.TableRuntime.Tests.csproj -c Release` | **231 passed / 0 failed / 0 skipped**（原 168 + 63，含 8 探针/case） |
| `dotnet test dotnet/Mahjong.sln -c Release` | **575 passed / 0 failed / 0 skipped**（Domain 322、Contracts 104、Client 132、Ai 17） |
| `dotnet build dotnet/Mahjong.sln -c Release` | **0 warnings / 0 errors** |
| `git diff --check daeb19a..HEAD` | **exit 0** |

新增/调整覆盖：

- R7/R8：`CodexP34Round3ReviewProbes` 的 duplicate、wrong-type、missing-fields 三 case；均在任何 sink/Serving 之前失败。
- R8 Wire：`RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch`、`TerminalRecoveryWireExposesTypedSettlementFromPersistedFacts`、`FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero`。

- R5：`CrossBootRecoveryWithOpenDecisionMustInstallExplicitGrace`、`RecoveredCrossBootDecisionCanCommitBeforeGraceDeadline`、`RecoveredCrossBootDecisionTimesOutAtGraceDeadlineWithoutSecondGrant`、`SameBootRecoveryChargesActiveElapsed...`、`LongCrossBoot...`、`TwoConsecutiveRecoveries...`。
- R6：`LeaseExpiringDuringHostInstallMustNotEnterServing`、`OwnerTransferDuringHostInstallFailsClosed`、`TimingChangeDuringHostInstallFailsClosed`、`HostInstallExceptionFailsClosedBeforePublishingRecovered`。
- R4：`RecoveryInstallsTypedCheatLedgerAndLatestCommittedFacts`、`FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero`、`RecoveryInstallsStrictWireBaseline...`、`MissingCommittedFactsIsRefused`、`TamperedProjectionFactsAreRefused`、`UnmodeledPrivateCheatSubstateIsRefused`。
- 既有回执/scope/租约/事务/fence 测试继续通过。

## 6. SHA-256（第 4 轮实现）

| 文件 | SHA-256 |
|---|---|
| `dotnet/src/Mahjong.Domain/DecisionClock.cs` | `75f4314a580155f1b27a28a5db63601a7d78a1b9d8a4fc729b3570fe1e2dfaec` |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `cc72f87332d6825d3b9f950e7cf07de822cefdc19026d5ec865dbd2e092ad586` |
| `dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs` | `f73c0e1e7a37694c792609bad3e237c81c1cb0ff6decc6f20f9d9710f885ea8d` |
| `dotnet/src/Mahjong.TableRuntime/InMemoryTableStore.cs` | `4acec2fca3c44801918429a3a6fb95028f271d7459b989b0b326f8aa4557c6cb` |
| `dotnet/src/Mahjong.TableRuntime/TableCheckpointCodecs.cs` | `d2783a6c08ffa4e35b741b92444755d3d69e4e0ab2d814bcd236c0897b5d1a9c` |
| `dotnet/src/Mahjong.TableRuntime/TableControlContracts.cs` | `dc89595468c0802545a270c58133d121c11f5eb5fb72f375339739c2596ec498` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryPayloads.cs` | `92c5813edb64b6694fcfcc6887e4498701ead0c8bdb83506de924b907dca2bb6` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryService.cs` | `747a6306ae9b2e4ba20d360cf28a3da4708238336c91d17c7bde7593a1080e53` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs` | `f7b962b551f1f01a3818828ea4c9641ebe037fd09bdd7327864bf2f38aa33c47` |
| `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs` | `5c0c690f3d85111d0c493b5def620f08485f7f61fdf104c56ba0716afe0cc2a9` |
| `dotnet/src/Mahjong.TableRuntime/TableProjectionFactsCodec.cs` | `8ece59cb1fee07ba33f781938c0acec2fb39c2b9d819e817b95417b1b26a02ad` |
| `dotnet/tests/.../TableDurableTestSupport.cs` | `55019aef8f15bc26e30441ad78335c58a5a08c862e090dd4f466699707b6d534` |
| `dotnet/tests/.../TableDurableReceiptTests.cs` | `114778553802ed5f2f97098d506ce4957c7faed8538a8bc3a9e01ffea463d4c6` |
| `dotnet/tests/.../TableDurableLeaseRecoveryTests.cs` | `8a6fd787d89d127f601318c545e121285752bcb357c9eb85fd4a9fed408d941e` |
| `dotnet/tests/.../TableDurableCorruptionTests.cs` | `a8067ab0930397584d15db3c0e5344c9ddd4a2c8db1302cead6fe48d554c503c` |
| `dotnet/tests/.../TableDurableTimingRecoveryTests.cs` | `6c377c2a39c7180d48b277976933d57dfef73845c67e15af358f67b8c35ef833` |
| `dotnet/tests/.../CodexP34Round2ReviewProbes.cs` | `af73da1ec72854d453412ffe77e5dda27dea9577303ff16494de0ffc3d872747` |
| `dotnet/tests/.../CodexP34Round3ReviewProbes.cs` | `0c3078bc55db43d7097822697936994bff7aeb10c6c865a8482724a38346fce9` |

第 4 轮实现提交：6 文件，`300 insertions(+), 59 deletions(-)`。未新增项目/包引用，未修改 `Mahjong.sln`、任务书、矩阵、黄金 fixtures、统一 verifier 或 CI。

## 7. NOT_RUN 与边界

- 真实 PostgreSQL/P5.2、migrations、advisory/row lock、DB 慢/断、备份从空库恢复：**NOT_RUN**。
- 真实进程 kill/DB 提交后崩溃/网络分区：**NOT_RUN**；`AfterApplyBeforeReturn` 与 sink 时钟推进都是内存确定性注入。
- WSS/TableService/P5.1、LocalHost/P4、Unity/IL2CPP/真机/物理网络：**NOT_RUN**。
- V4 Gradle 全量、Unity 许可/SDK/NDK/macOS/Xcode：**NOT_RUN**；V4 仍是唯一生产版本。
- G1/G5 未宣告；P5.1 未提前实现。

## 8. 交接

- 第 4 轮实现 HEAD：`8f3f580d32203ef203431eb2e5c88f9f1ad66bea`。
- 本文件提交为最终本地 HEAD；工作树 clean。
- 等待 caller 第 4 轮独立验收；caller 负责唯一 PR19 `feat/v5-1-integration -> v5` 及 merge-back。不自行宣告 G1/G5。
