# V5 P3.4 回执、租约和恢复端口合同交付（第 25 轮返修）

- 状态：**第 25 轮 caller 独立验收通过，Bridge 已归档；统一纳入已有 PR19**。不是 G1/G5，不是 V5 全链通过；V4 仍是唯一生产架构。
- 唯一工作树：`D:/majiang-wt/p34-receipts`（分支 `wt/v5-p34-receipts`）。
- 冻结基线：`daeb19a64951174426924026fe974c8ae5caacd9`。
- 第 1 轮实现：`9e909ea6b7dfb411b1e2511fa4f0f9d05307ba38`。
- 第 2 轮实现：`1438f2e1b9caf4fbe9531cd3cde63169e219d747`（主体 `343b7a2271d5da4089646f48f521fcb69016d637`）。
- 第 3 轮实现：`46dfffd2aedfed3329c1b9e7d7318dfcecbf200d`（`fix(v5-p34): install explicit grace and typed projection facts`）。
- 第 4 轮实现：`8f3f580d32203ef203431eb2e5c88f9f1ad66bea`（`fix(v5-p34): validate recovered event facts before serving`）。
- 第 5 轮实现：`13f6adbccdd9325cde1c5eed17aac3473f3ba49c`（`fix(v5-p34): bind recovered event facts to replay wall and result`）。
- 第 6 轮实现：`deafa78690e3f32ecbf277817f4482bd97e123cf`（`fix(v5-p34): bind checkpoint history and abortive draw facts`）。
- 第 7 轮实现：`8811e979e57230fc955b90a450e1e95b737c4247`（`fix(v5-p34): bind checkpoint command groups and fingerprints`）。
- 第 8 轮实现：`36356b3850fba148b7db0d83adab67d3735143de`（`fix(v5-p34): retain replay bindings and terminal evidence`）。
- 第 9 轮实现：`1e1d33423e9df736f285371999afc3f9afc8fdca`（`fix(v5-p34): validate bindings at atomic save boundary`）。
- 第 10 轮实现：`961f5942b70a6b3e644bf034a17a52ac1bb44ab5`（`fix(v5-p34): reject unsupported checkpoint versions at save`）。
- 第 11 轮实现：`e793514bd44ed9eef13a099065e6de5589992b86`（`fix(v5-p34): validate checkpoint side payload semantics`）。
- 第 12 轮实现：`dfbb184505deac51f5afb8c77486bf0d934d2769`（`fix(v5-p34): validate zero-version side payloads and truncation`）。
- 第 13 轮实现：`37b216121e560e21338dc47672cf2c8d8c2c8127`（`fix(v5-p34): preserve required cheat ledger at save`）。
- 第 14 轮实现：`28c04ddd5d624aefa737e414cd69ccdb94f400eb`（`fix(v5-p34): bind checkpoint room and timing origin`）。
- 第 15 轮实现：`b3e41b5860f2e7fbea2855d91c1ac706377d48e5`（`fix(v5-p34): bind accepted timing journal to checkpoint`）。
- 第 16 轮实现：`f36eb0857df2d55daae42e49a89deb662ea66164`（`fix(v5-p34): fence accepted checkpoint control version`）。
- 第 17 轮实现：`d518f256061258e02ee7dcd875ed5e9443471e4f`（`fix(v5-p34): preserve authoritative control snapshot at save`）。
- 第 18 轮实现：`d8ba28600614cefcd0ff2a04389d33b83ac1ff34`（`fix(v5-p34): allow fenced control rebase after recovery`）。
- 第 19 轮实现：`ca2638cfd2638af4850d4e0adce99965cf3a7f4a`（`fix(v5-p34): prevent grace restart via timestamp shift`）。
- 第 20 轮实现：`3772dc791656e94b7e068eeaa5957a55a51c5b9d`（`fix(v5-p34): rebuild same-boot grace recovery timing`）。
- 第 21 轮实现：`7e0e0236cb29ddc07673531f68762f519ae3a5c4`（`fix(v5-p34): let recovered grace survive ordinary time`）。
- 第 22 轮实现：`43cc8e456588de9eaaf8723034fd3b103ff0a814`（`fix(v5-p34): protect live grace from same-version expiry`）。
- 第 23 轮实现：`f90403954ba896020e35d34fee09a8104cc83002`（`fix(v5-p34): revalidate scope and lease after waits`）。
- 第 24 轮实现：`9a0ba73f329c9439b692af922271cff2e0ad008a`（`fix(v5-p34): stop serving when lease expires at fence`）。
- 第 25 轮实现：`4dffd221ba0442290d2848238bea14f55a49b9a8`（`fix(v5-p34): replay committed receipts after lease loss`）。
- 本文件提交为最终本地 HEAD；未 push、未建 PR、未触发 CI、未转派。
- caller 的 `CodexP34ReviewProbes.cs`、`CodexP34Round2ReviewProbes.cs`、`CodexP34Round3ReviewProbes.cs`、`CodexP34Round4ReviewProbes.cs`、`CodexP34Round5ReviewProbes.cs`、`CodexP34Round6ReviewProbes.cs`、`CodexP34Round7ReviewProbes.cs`、`CodexP34Round8ReviewProbes.cs`、`CodexP34Round9ReviewProbes.cs`、`CodexP34Round10ReviewProbes.cs`、`CodexP34Round11ReviewProbes.cs`、`CodexP34Round12ReviewProbes.cs`、`CodexP34Round13ReviewProbes.cs`、`CodexP34Round14ReviewProbes.cs`、`CodexP34Round15ReviewProbes.cs`、`CodexP34Round16ReviewProbes.cs`、`CodexP34Round17ReviewProbes.cs`、`CodexP34Round18ReviewProbes.cs`、`CodexP34Round19ReviewProbes.cs`、`CodexP34Round20ReviewProbes.cs`、`CodexP34Round21ReviewProbes.cs`、`CodexP34Round22ReviewProbes.cs`、`CodexP34Round23ReviewProbes.cs`、`CodexP34Round24ReviewProbes.cs` 均保留、纳入提交并通过；未删除或弱化探针。

## 1. 第 25 轮修复结论

|缺陷|修复|
|---|---|
|R34 LeaseLost 生命周期挡在 receipt-first 之前，已提交原键重交变 Rejected|`ProcessCommandAsync` 先验证 durable identity 与 scope 可用，再做 receipt lookup；已有 receipt 且 API/digest 匹配时，Serving 或 LeaseLost 均返回 `Replayed` 原回执（含已持久 REJECTED）；不同 digest/API 返回 Conflict，跨身份不泄露。无 receipt 的新请求仍先由 GuardServing 失败关闭，绝不恢复 Serving、重新评估 Domain、写新回执/事件或发布成功内容。|
|R33 租约自然过期返回普通 Failed，actor 仍 Serving|`CommitAsync` 自然过期返回 StaleOwner disposition；actor accepted/REJECTED 公共 switch 均 EnterLeaseLost，Lifecycle=LeaseLost，关闭玩家/观战流，后续新命令失败关闭；LeaseLost 控制通知保留，成功 Applied 不发布。|
|R32 scope/lease 等待后仍用旧 now 授权|`CommitAsync` 在等待事务 scope 行锁/`BeforeScopeCheckForTest` 之后、读取 scope 后重新采样 UTC，用新 `scopeNow` 判断 scope 过期；在 `AfterScopeCheckForTest` 之后、lease fence 之前再次采样 `now`，用新时间判断 lease 过期。|
|R31/R30/R29 恢复 grace|保留活 grace 绝对 deadline、已过期不复活、普通时间推进可保存、同 boot 默认 GRACE_THEN_AI 显式 remaining/overdue 重建；跨 boot 无可信 grace 继续 fail closed。|
|R29 来源 boot|使用 checkpoint 自身的 BootId（control 载荷所在 checkpoint）判断 control 时间可比较，不只依赖可能被更新过的 timing journal boot；跨 boot 仍不信任绝对时间，缺少可信 grace 证据继续 fail closed。|
|R28/R26/R27 防篡改|保留同 boot deadline 不得后移/过期不得复活、控制语义与权威快照绑定、非时间字段不可改写；所有校验仍在 mutation 前。|

## 2. 历史轮次修复结论

|缺陷|修复|
|---|---|
|R21 保存可用 CheatPayload=null 擦除已有有效 ledger|`InMemoryTableStore` 新增 `ValidateCheatCarryover`：若当前持久 checkpoint 的 `CheatPayload != null`，候选 checkpoint 的 `CheatPayload == null` 直接 Failed；现有 ledger 存在即为可信持久元数据，不能由本次空载荷推断可选。|
|R21 Save/CommitControl/Commit 一致|同一 carryover 校验接入 `SaveCheckpointAsync`、`CommitControlAsync`、`CommitAsync` accepted checkpoint；均在 mutation 前失败，旧 checkpoint/facts/timing/state/control/lease 更新时间/CommitCount 不变。|
|合法可选路径|现有 checkpoint 无 cheat payload 时仍允许空载荷；完整合法 ledger 保存/恢复继续通过。未通过放宽 `RequireCheatLedger` 或空 ledger 替代。|
|测试证据|`CodexP34Round12ReviewProbes` zero/nonzero 两 case：保存 Failed、旧 CheatPayloadSha256/TimingVersion/CommitCount 不变、旧恢复点仍 recover。|

## 2. 历史轮次修复结论

|缺陷|修复|
|---|---|
|R20 Save/CommitControl/Commit 路径|共享 validator 在 `SaveCheckpointAsync`、`CommitAsync` accepted checkpoint、`CommitControlAsync` 的 mutation 前调用；所有截断 payload 返回 Failed，旧恢复点不变。|
|测试证据|`CodexP34Round11ReviewProbes` 9 case：zero/nonzero × none/control-version/truncated control/cheat/facts/timing；全部无异常、Failed 且旧恢复点可恢复。|

## 2. 历史轮次修复结论

|缺陷|修复|
|---|---|
|R12 checkpoint 前 history 的 SourceFingerprint 可伪造|新增 `TableCommandBindingsCodec`/`TableCommandBinding`：actor 在每次成功命令执行后记录 commandVersion、事件数、`nextState.SourceFingerprint` 到独立 `_commandBindings`；checkpoint 持久化 `CommittedCommandBindingsPayload` + SHA。恢复逐组校验每条 history fact 的 SourceFingerprint 必须等于该组证据，不能只补最后一组。|
|R12 command 边界可 regroup|恢复按 command binding 的 eventCount 逐组消费 history，要求每组版本/ordinal 从 0 连续、seq 连续覆盖；把事件移入相邻命令、改 history/latestFacts 的 commandVersion/ordinal 都会被组边界/事件数证据拒绝。|
|证据来源不能是 history 自身|binding 由 actor 执行结果累计，checkpoint 工厂只接受 `committedCommandBindings` 独立入参；不再从 history payload 重新 Build。缺失证据时 nonzero checkpoint fail closed。|
|旧测试/正例|新增 4 个 first/last 组的 eventCount/fingerprint 负例；正确非零 checkpoint + 尾部 replay、终止 checkpoint、AbortiveDraw 正例继续通过。|
|R10 checkpoint 前 history 的 fake discard tile 仍 Serving|`ValidateCheckpointHistoryAgainstCaptured` 将 `checkpointHistory` 与 `checkpoint.StatePayload` 中受 hash/fingerprint 保护的 `RoundRecoveryState.Events` 前缀逐条绑定：type/seat/tile/detail 必须一致；支持仅 TilesDealt/DoraRevealed 的初始化前缀偏移；history payload 的 commandVersion/ordinal 必须与记录一致。|
|R10 commandVersion 跳版本/空版本/末尾不等|`ValidateHistory` 强制 commandVersion 从 1 连续覆盖到 `checkpoint.StateVersion`，每个版本至少一个事件、同一版本 ordinal 从 0 连续、seq 从 1 连续，最后必须落在 `checkpoint.LastEventSeq`。|
|R11 AbortiveDraw 漏校验 tenpai|`ValidateDomainEventValues` 同时覆盖 `ExhaustiveDraw` 与 `AbortiveDraw`；tenpaiPlayers 必须与 `round.Result.Outcome.TenpaiSeats` 完全一致，空列表也不能被 `[0]` 替换。|
|R10/R11 测试|新增 `CheckpointHistoryPrefixMustBindCapturedDomainEvent` 的 type/seat/detail/version-gap；`CommandBindingEvidenceMustCoverExactCommandGroups` 的 first/last count/fingerprint 负例。|
|R9 TileDrawn 的 wallRemaining 与真实 replay 矛盾仍 Serving|`ValidateRecoveredEventFacts` 新增 final `RoundStateMachine` 入参；从可信 checkpoint/final `Tiles.WallRemaining` 反向重放完整 history：最后一个 TileDrawn 对应 final wall，向前每个 TileDrawn +1，得到**每个 draw 时点**的期望 wall，逐条比对 fact.wallRemaining。不是拿 final wall 校验所有历史 draw。|
|R9 RoundEnded 的 roundWind/roundNumber 与不可变 round spec 矛盾仍 Serving|对 `RoundEnded` 逐条校验 fact.roundWind/roundNumber 必须等于最终 round 的 `RoundWind/RoundNumber`；合法 wire 范围（+1 mod 4）也不能通过。|
|R9 draw tenpai / ron loser 未绑定 Domain|`ExhaustiveDraw`/`AbortiveDraw` 的 tenpaiPlayers 必须等于 `round.Result.Outcome.TenpaiSeats`（排序后比较）；`RonWin` 的 loserSeat 必须等于 committed result loser。|
|非零 checkpoint 之前历史无逐事件校验|反向 wall 重建覆盖完整 `fullHistory`（checkpointHistory + 之后的 events），不区分 checkpoint 前后；kan/RinshanDrawn 的 live-wall 反演未建模时明确 fail closed，绝不猜补。|
|正向多 draw 未验证|`RecoveryInstallsStrictWireBaseline...` 的非零 checkpoint、2 个 Draw 现在断言每帧 TileDrawn wall 存在且互不相同；`WireEventFacts` 也按逆序 wall 为每个 draw 生成独立值。|
|测试/fixture 用 final wall 填所有 draw|`WireEventFacts.Build` 按同一逆序规则为每个 TileDrawn 计算 wallRemaining；与恢复校验独立实现，避免互相“对齐”。|

## 2. 历史轮次修复结论

|缺陷|修复|
|---|---|
|R7 facts 数量相等但重复/错 type 仍 Serving|恢复前新增 `TableVisibilityProjection.ValidateRecoveredEventFacts`：每个 committed history 事件必须恰好对应一个 fact；seq 不得重复；fact.Type/CommandVersion/EventOrdinal 必须与 `TableEventProjection.FromRoundEvent` 的投影完全一致；不再只比较数量或“存在某条匹配”。|
|R8 fact 身份正确但字段为空仍 Serving|同一验证路径实际调用 `TableEventProjection.Project` + `TableProjectionContractMapper.MapEvent`，强制按事件类型校验 turnNumber/wall/riichiDiscard/tsumogiri/meld/yaku/fu/score/payments 等必需字段；使用 settlementBatch=true/revealUra=true 的验证策略覆盖终局 win/settlement facts，但不对外发布。|
|R8 独立新宿主 Wire 断言不足|`RecoveryInstallsStrictWireBaseline...` 增加非零 checkpoint、2 个事件、`SnapshotEventWindow=1`、真实费用 ledger 的玩家/观战语义断言；新增终局 `TerminalRecoveryWireExposesTypedSettlementFromPersistedFacts`，从持久 facts 恢复后断言 TsumoWin settlement、玩家私有 ledger 与 spectator settlement；outbox 仍逐字节一致。|
|测试用空 event facts 掩盖缺口|删除 `MinimalEventFacts`；默认 harness 使用真实 `WireEventFacts.Build`，终局测试使用显式 `TerminalFactsSource`（真实 win yaku/han/fu/score），不再用空 facts 保旧测试。|
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
- `ValidateCheckpointHistoryAgainstCaptured` 将 checkpointHistory 与 `RoundRecoveryState.Events`（受 StatePayload SHA/fingerprint 保护）逐条绑定：type/seat/tile/detail 必须一致；prefix 偏移只允许 TilesDealt/DoraRevealed；history payload 的 commandVersion/ordinal 必须一致；
- `ValidateCommandBindings` 使用独立 `CommittedCommandBindingsPayload`（actor 执行时逐命令累计的 eventCount + SourceFingerprint）逐组消费 history：组边界、ordinal、seq、fact.SourceFingerprint 必须一致；不能靠 history/latestFacts 自洽或自身 SHA；
- `TableCheckpointValidator.TryValidate` 在 store 的 SaveCheckpoint/Commit/CommitControl 真正 apply 之前执行同一 history/binding/Domain-prefix 校验；失败返回 Failed 且旧 checkpoint/facts/timing/CommitCount 不变；
- 尾部真实 replay 每个成功版本也累加 command binding（`next.SourceFingerprint` + committed.Count），并随 baseline 安装；save 时与 checkpoint 前缀证据合并，避免保存不完整恢复点；
- `ValidateHistory` 强制 version 1..checkpoint.StateVersion 每版非空、ordinal/seq 连续、末尾等于 checkpoint.LastEventSeq；
- `TableVisibilityProjection.ValidateRecoveredFacts` 以恢复后的 round 校验 public/private/meld 与 136 牌；
- `TableVisibilityProjection.ValidateRecoveredEventFacts` 以完整 `fullHistory` 做唯一、一一覆盖校验：
  - 每个 committed event 恰好一条 fact；
  - fact seq 不得重复，也不得遗漏；
  - fact.Type/CommandVersion/EventOrdinal 必须与 `TableEventProjection.FromRoundEvent` 完全一致；
  - 对每个可见 audience 调用现有 `TableEventProjection.Project` + `TableProjectionContractMapper.MapEvent`，按事件类型校验 turnNumber/wall/riichiDiscard/tsumogiri/meld/yaku/fu/score/payments 等必需字段；
  - 以 `settlementBatch=true`/`revealSettlementUra=true` 的验证策略覆盖终局 win/settlement facts，但不对外发布；
  - **Domain 可核验值**：从 final `round.Tiles.WallRemaining` 反向遍历完整 history，最后一个 TileDrawn 为 final wall、向前每个 TileDrawn +1，逐条比对 fact.wallRemaining；RoundEnded 的 roundWind/roundNumber 必须等于最终 round spec；ExhaustiveDraw/AbortiveDraw 的 tenpaiPlayers 必须等于 result.TenpaiSeats；RonWin 的 loserSeat 必须等于 result loser；
  - 反向 wall 重建覆盖 checkpoint 前后全部事件；KanDeclared/RinshanDrawn 的 live-wall 反演未建模时明确 fail closed，不猜补；
- `TableRecoveryBaseline.ProjectionFacts` 是 typed `TableProjectionContextData`；
- `TableRecoveryAuxState.ProjectionFacts` 交给宿主 sink；
- `ProcessCompleteRecoveryAsync` 在 sink 安装后仍执行 §3 的 lease/state/control/timing 最终 fence。

任何缺失、重复、错 type/ordinal/version、空必需字段、与 Domain wall/round/result 矛盾的值都在 sink Install 之前返回 `CHECKPOINT_INVALID`，无 `Recovered` 发布、无可用玩家/观战基线。

### 4.4 独立新宿主的 Wire 验证

`RecoveredProjectionSource`：

- 恢复前只用于捕获真实 live facts（`_live` 带真实 `WireEventFacts`）；
- sink 安装后 `Capture` **只**读取 `Sink.Installed.ProjectionFacts` 与 `Sink.Installed.Cheat`，校验 round/state 后返回，不调用 `V32Fixtures.BuildPublicFacts/BuildPrivateFacts`，不 fallback `V32Fixtures.Cheat`；
- `UsedRecovered` 由测试断言为 true。

严格 Wire 断言：

- `RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch`：非零 checkpoint、2 个 Discard/Draw 事件、`SnapshotEventWindow=1`、真实 `TILE_MARK` 费用 ledger；玩家帧断言 stateVersion、私有 closed tiles/discard history、cheat ledger entry 与 balance；观战帧断言 2 张弃牌、无 settlement events；并断言每个 TileDrawn 的 wallRemaining 存在且互不相同；old ownerEpoch/old recoveryGeneration 拒绝。
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
| caller `*CodexP34Round4ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round5ReviewProbes`（4 cases） | **4 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round6ReviewProbes`（3 cases） | **3 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round7ReviewProbes`（3 cases） | **3 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round8ReviewProbes`（3 cases） | **3 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round9ReviewProbes`（4 cases） | **4 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round10ReviewProbes`（4 cases） | **4 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round11ReviewProbes`（9 cases） | **9 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round12ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round13ReviewProbes`（6 cases） | **6 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round14ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round15ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round16ReviewProbes`（4 cases） | **4 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round17ReviewProbes`（4 cases） | **4 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round18ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round19ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round20ReviewProbes`（3 cases） | **3 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round21ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round22ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round23ReviewProbes`（4 cases） | **4 passed / 0 failed / 0 skipped** |
| caller `*CodexP34Round24ReviewProbes`（2 cases） | **2 passed / 0 failed / 0 skipped** |
| `dotnet test .../Mahjong.TableRuntime.Tests.csproj -c Release` | **313 passed / 0 failed / 0 skipped**（原 168 + 145，含 77 探针/case） |
| `dotnet test dotnet/Mahjong.sln -c Release` | **575 passed / 0 failed / 0 skipped**（Domain 322、Contracts 104、Client 132、Ai 17） |
| `dotnet build dotnet/Mahjong.sln -c Release` | **0 warnings / 0 errors** |
| `git diff --check daeb19a..HEAD` | **exit 0** |

新增/调整覆盖：

- R34：`CodexP34Round24ReviewProbes` 的 accepted/rejected 候选过期失租后，原已提交请求重交返回 Replayed，原回执字段一致，Event/Receipt/CommitCount 不变；新 ID 命令仍 Rejected(LEASE_LOST)。
- R33：`CodexP34Round23ReviewProbes` 的 accepted/rejected × BeforeScope/AfterScope 四 case；lease 自然过期均 Failed(StaleOwnerEpoch)，Lifecycle 非 Serving，零事件/回执。
- R32：`CodexP34Round22ReviewProbes` scope/lease 锁等待后过期；均非 Accepted，零回执/事件/CommitCount，禁止 Applied/Recovered 成功 publication；LeaseLost 控制通知允许。
- R31：`CodexP34Round21ReviewProbes` 的 overdue=0/1；旧 grace 仍未来时把 deadline 提前到 now/now-1 均 Failed，CommitCount/TimingVersion/ControlPayload 不变。
- R30：`CodexP34Round20ReviewProbes` 的 (250,1)/(250,10000)/(20000,1)；恢复后时间推进仍可 Save，未到期 deadline 不变，已过期保持过期，再次 Recover 成功。
- R29：`CodexP34Round19ReviewProbes` 默认 GRACE_THEN_AI elapsed250/20000；同 boot 恢复成功，elapsed250 deadline 不变，elapsed20000 保持过期；恢复后 Save 和再次 Recover 成功。
- R28：`CodexP34Round18ReviewProbes` 的 same-boot shift250/shift20000；两时间同时后移保存 Failed，旧 deadline/CommitCount/TimingVersion 不变。
- R27：`CodexP34Round17ReviewProbes` 的 connected/disconnected × same/cross boot 四 case；恢复后 Save 成功、控制版本/epoch 不变、再次恢复成功；`RecoveredControlRebaseCannotExtendGraceDeadline` 证明 +60000 grace 仍拒绝。
- R26：`CodexP34Round16ReviewProbes` 的 accepted/Save × 原控制/seat epoch+10；改 epoch 均 Failed，原 epoch 正例 Committed+恢复。
- R25：`CodexP34Round15ReviewProbes` 的 accepted control 同版本正例/control+1 负例；负例 Failed，旧恢复点保留。
- R24：`CodexP34Round14ReviewProbes` 的 accepted checkpoint + request.Timing 同 origin/篡改 origin 两 case；篡改候选 Failed，旧恢复点保留。
- R22/R23：`CodexP34Round13ReviewProbes` 的 control foreign-room/erase-ledger 组合与 timing-origin corrupt/合法组合；失败保留旧恢复点。
- R21：`CodexP34Round12ReviewProbes` zero/nonzero；缺失 cheat ledger 的候选保存被拒，旧恢复点保留。
- R19/R20：`CodexP34Round11ReviewProbes` 的 zero/nonzero × none/control-version/truncated control/cheat/facts/timing；全部返回 Failed、无异常、旧恢复点仍 recover。
- R18：`CodexP34Round10ReviewProbes` 的 none/control-version/cheat-payload/facts-payload；无效 side payload 保存被拒，旧恢复点保留。
- R17：`CodexP34Round9ReviewProbes` 的 none/schema/ruleset；无效保存不覆盖旧 checkpoint，旧恢复点仍可 recover；丢失响应后 recover→save→recover→原 ID 精确回执重放继续通过。
- R16：`CodexP34Round8ReviewProbes` 的 none/empty/fingerprint；`InvalidSaveBindingsMustNotReplaceCheckpointOrMutateEvidence` 的 partial/event-count/group-mismatch 三负例；断言旧 checkpoint/facts/timing/CommitCount 不变且旧恢复点仍 recover。
- R13：`ReplayEvidenceMustSurviveSaveAndSecondRecovery` checkpoint0/1；`ReplayBindingsSurviveCommandAfterRecoveryAndSecondSave` 新命令后 save/recover。
- R14：`TerminalCommandAfterEarlierCommitMustCreateAtomicCheckpointAndOutbox` v1 discard 后 v2 AbortiveDraw。
- R15：R5/R6 probes 与 corruption helpers 保留 command binding 元数据；`none` 同形正例通过。
- R10：`CodexP34Round5ReviewProbes` 的 checkpoint fake tile、type/seat/detail/version-gap；正向非零 checkpoint+尾部 replay 继续通过。
- R11：`CodexP34Round5ReviewProbes` 的 AbortiveDraw 正常/错误 tenpai 两 case；错误 `[0]` 在 sink 前失败，正常空 tenpai 恢复成功。
- R9：`CodexP34Round4ReviewProbes` 的 wrong-wall（TileDrawn 非零 wall 改 0）与 wrong-round-wind（RoundEnded 合法范围但 +1）两 case；均在 sink Install/Serving 前 `CHECKPOINT_INVALID`。
- R7/R8：`CodexP34Round3ReviewProbes` 的 duplicate、wrong-type、missing-fields 三 case；均在任何 sink/Serving 之前失败。
- R8 Wire：`RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch`（多 draw wall 互异、非零 checkpoint、裁剪窗口、ledger）、`TerminalRecoveryWireExposesTypedSettlementFromPersistedFacts`、`FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero`。

- R5：`CrossBootRecoveryWithOpenDecisionMustInstallExplicitGrace`、`RecoveredCrossBootDecisionCanCommitBeforeGraceDeadline`、`RecoveredCrossBootDecisionTimesOutAtGraceDeadlineWithoutSecondGrant`、`SameBootRecoveryChargesActiveElapsed...`、`LongCrossBoot...`、`TwoConsecutiveRecoveries...`。
- R6：`LeaseExpiringDuringHostInstallMustNotEnterServing`、`OwnerTransferDuringHostInstallFailsClosed`、`TimingChangeDuringHostInstallFailsClosed`、`HostInstallExceptionFailsClosedBeforePublishingRecovered`。
- R4：`RecoveryInstallsTypedCheatLedgerAndLatestCommittedFacts`、`FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero`、`RecoveryInstallsStrictWireBaseline...`、`MissingCommittedFactsIsRefused`、`TamperedProjectionFactsAreRefused`、`UnmodeledPrivateCheatSubstateIsRefused`。
- 既有回执/scope/租约/事务/fence 测试继续通过。

## 6. SHA-256（第 25 轮实现）

| 文件 | SHA-256 |
|---|---|
| `dotnet/src/Mahjong.Domain/DecisionClock.cs` | `75f4314a580155f1b27a28a5db63601a7d78a1b9d8a4fc729b3570fe1e2dfaec` |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `1f8b94ec3fd8fc8acf394eb95e5d669f7dc498a9a2fcb1c70d13b5c33275e9fe` |
| `dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs` | `a4b8f30f532ce1b949bae6152a39edef99d0c527c9d26e33b490d528c7c5c375` |
| `dotnet/src/Mahjong.TableRuntime/InMemoryTableStore.cs` | `be2de09958c2cc62a315523ae95a62a3bef7d11705a7335efb6621f059d4b63b` |
| `dotnet/src/Mahjong.TableRuntime/TableCheckpointValidator.cs` | `f01435562d6a1c8e88d1e886671f23e45a9f428cc6ee65dc8e4dd277acbc8817` |
| `dotnet/src/Mahjong.TableRuntime/TableCheckpointCodecs.cs` | `d2783a6c08ffa4e35b741b92444755d3d69e4e0ab2d814bcd236c0897b5d1a9c` |
| `dotnet/src/Mahjong.TableRuntime/TableControlContracts.cs` | `dc89595468c0802545a270c58133d121c11f5eb5fb72f375339739c2596ec498` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryPayloads.cs` | `9969129b6076189daa4853ca6e2961b78c8b5f2880c9b0dc9fc8e4a881eda9ef` |
| `dotnet/src/Mahjong.TableRuntime/TableRecoveryService.cs` | `59f2de5b75043865341f9e7501cebfa1f700c62eec934266d8612cd2cbd360ad` |
| `dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs` | `214402ac6c8b2334e4ac3afdef9d79d8c57acd278f95b1982ea37c213afd1341` |
| `dotnet/src/Mahjong.TableRuntime/TableCommandBindingsCodec.cs` | `dc7af337ee49fc1dcdeda2b2a670a9c7a7931b0ea8eef54a9329c3ac2a3ba2e1` |
| `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs` | `0dd9d9ebdbc5e4db79790c66a14a0b3b631f7a83aa839a332b3230bea77e7b25` |
| `dotnet/src/Mahjong.TableRuntime/TableProjectionFactsCodec.cs` | `8ece59cb1fee07ba33f781938c0acec2fb39c2b9d819e817b95417b1b26a02ad` |
| `dotnet/tests/.../TableDurableTestSupport.cs` | `55019aef8f15bc26e30441ad78335c58a5a08c862e090dd4f466699707b6d534` |
| `dotnet/tests/.../TableDurableReceiptTests.cs` | `114778553802ed5f2f97098d506ce4957c7faed8538a8bc3a9e01ffea463d4c6` |
| `dotnet/tests/.../TableDurableLeaseRecoveryTests.cs` | `187d1798eb4ab1a743b0bda2e3a503b0f7dcc58d911d52ed3629db3ad7c24622` |
| `dotnet/tests/.../TableStreamWireTestSupport.cs` | `8914663bff9439f2e00679cde7d08c003c93382a18c741b2d844dd6c2e02d249` |
| `dotnet/tests/.../TableDurableCorruptionTests.cs` | `39e6d6f95c39dec1038632fadfd3bf29fa057316847f08768989ff8838f2f738` |
| `dotnet/tests/.../TableDurableTimingRecoveryTests.cs` | `6c377c2a39c7180d48b277976933d57dfef73845c67e15af358f67b8c35ef833` |
| `dotnet/tests/.../CodexP34Round2ReviewProbes.cs` | `af73da1ec72854d453412ffe77e5dda27dea9577303ff16494de0ffc3d872747` |
| `dotnet/tests/.../CodexP34Round3ReviewProbes.cs` | `0c3078bc55db43d7097822697936994bff7aeb10c6c865a8482724a38346fce9` |
| `dotnet/tests/.../CodexP34Round4ReviewProbes.cs` | `413ffb4cc3aed1ceb84391ebed738c68525eb8fb45820c7f8d9bc2551acb2df9` |
| `dotnet/tests/.../CodexP34Round5ReviewProbes.cs` | `1b9d80ddc3c0574747df857988d8b700f22f03b579552f33aa0acf7367781353` |
| `dotnet/tests/.../CodexP34Round6ReviewProbes.cs` | `003752027d3ef246e17ca6a33fb332cfa35cda16119c897d101a5312024bbdac` |
| `dotnet/tests/.../CodexP34Round7ReviewProbes.cs` | `2285283a57b6969939204d5011b5364d2986def8fa35ca9363097aaf21642e8e` |
| `dotnet/tests/.../CodexP34Round8ReviewProbes.cs` | `42c8adb943fbeddecc57db076ed6933d3d02f478d19aaef7740e9ae7256de42c` |
| `dotnet/tests/.../CodexP34Round9ReviewProbes.cs` | `c4127b21105cbbb16fa512a6dfe6d818f6d5276205ebd00be09d01488c062ad9` |
| `dotnet/tests/.../CodexP34Round10ReviewProbes.cs` | `3d85ed9ef7db1ac9d1545f15bfe969bdb8a2726e88370d2d431663a6463ab32a` |
| `dotnet/tests/.../CodexP34Round11ReviewProbes.cs` | `39ccb7ed05f304e01452f84d9b6e50d5937b0ddc4d5c2f80c1271acc9d52f967` |
| `dotnet/tests/.../CodexP34Round12ReviewProbes.cs` | `402212ee40106dab5ad637ad16f53e02f30d7ad4514113041ef5a68764879c52` |
| `dotnet/tests/.../CodexP34Round13ReviewProbes.cs` | `a573b542b35f543e39e945d80522002bef7ff104a58b9f4c9f1e45434528fdd6` |
| `dotnet/tests/.../CodexP34Round14ReviewProbes.cs` | `3a6c3a1700d959257d51e3e7012d2c1d0265fee17dc0ca2cab8cc7b0fa8cca22` |
| `dotnet/tests/.../CodexP34Round15ReviewProbes.cs` | `0fb1def55d3e1d35cc0f8987ff92f93516764260175e832b7bbea0f446006ccf` |
| `dotnet/tests/.../CodexP34Round16ReviewProbes.cs` | `32f2b5990b296efed7db4bcb7c8017576aa2198828a746766ed8cb1520362367` |
| `dotnet/tests/.../CodexP34Round17ReviewProbes.cs` | `27c92d5cc878c40159a426557d8e030083a7bdecb4bcc3f900ef8eb99574110b` |
| `dotnet/tests/.../CodexP34Round18ReviewProbes.cs` | `0bbff45b92e57b73f0fd3789af46731ba9586f5cae4c03f5650cafb16dca0da6` |
| `dotnet/tests/.../CodexP34Round19ReviewProbes.cs` | `e4a6efbf317bcd0a45d6a936fe19a9a392b738e4b418ab97bde6245abbafb71d` |
| `dotnet/tests/.../CodexP34Round20ReviewProbes.cs` | `5d86de87423d89cc6fb1a12e27af0702f168a791c3815df45021db61cc3113cf` |
| `dotnet/tests/.../CodexP34Round21ReviewProbes.cs` | `36b0cb3c108f6717a8f5675e414dd642848149ab6f8a104ef81ad3c8e5351d8d` |
| `dotnet/tests/.../CodexP34Round22ReviewProbes.cs` | `aec2eb1351975191b76e0e2ce9f4b5aa1ff0549deff7f4ace734d83dd85d70ff` |
| `dotnet/tests/.../CodexP34Round23ReviewProbes.cs` | `9b5b94850eccfd4af98e6a6fac4f2c7c63635633adc74b3da44ef9b2e7a36865` |
| `dotnet/tests/.../CodexP34Round24ReviewProbes.cs` | `db33a4aa7806934d1a44f28cb9cf0f3a6cb4595a8b62783bed257e478c255a9b` |

第 25 轮实现提交：2 文件，`96 insertions(+), 8 deletions(-)`。未新增项目/包引用，未修改 `Mahjong.sln`、任务书、矩阵、黄金 fixtures、统一 verifier 或 CI。

## 7. NOT_RUN 与边界

- 真实 PostgreSQL/P5.2、migrations、advisory/row lock、DB 慢/断、备份从空库恢复：**NOT_RUN**。
- 真实进程 kill/DB 提交后崩溃/网络分区：**NOT_RUN**；`AfterApplyBeforeReturn` 与 sink 时钟推进都是内存确定性注入。
- WSS/TableService/P5.1、LocalHost/P4、Unity/IL2CPP/真机/物理网络：**NOT_RUN**。
- V4 Gradle 全量、Unity 许可/SDK/NDK/macOS/Xcode：**NOT_RUN**；V4 仍是唯一生产版本。
- G1/G5 未宣告；P5.1 未提前实现。

## 8. 交接

- 第 25 轮实现 HEAD：`4dffd221ba0442290d2848238bea14f55a49b9a8`。
- 本文件提交为最终本地 HEAD；工作树 clean。
- 等待 caller 第 25 轮独立验收；caller 负责唯一 PR19 `feat/v5-1-integration -> v5` 及 merge-back。不自行宣告 G1/G5。

## 9. Caller 第 25 轮独立验收与统一 PR19

验收对象为 `48c350062e05e6e775d2c48a7ab494948d31be4d`。Caller 在当前工作树独立审查第 25 轮差异、原探针及 durable 命令入口，确认已提交回执在 LeaseLost 下仍可按原 API/digest 精确重放，新请求继续失败关闭，不安装候选状态、不新增事件/回执或 Applied 发布。可信宿主仍负责 scope 与原业务字节摘要，查询仍校验账户归属。

独立执行（全部 exit 0）：

| 命令 | 结果 |
|---|---|
| `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal` | 313 passed / 0 failed / 0 skipped，包含全部历轮独立探针 |
| `D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --nologo -v minimal` | Domain 322、Contracts 104、Client 132、Ai 17；合计 575 passed / 0 failed / 0 skipped |
| `D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --nologo -v minimal` | 0 warnings / 0 errors |
| `git diff --check daeb19a..HEAD` | 无空白错误 |

合计 888 项，不是包含 PostgreSQL、WSS 等独立项目的统一 verifier 全量。第 7 节全部 NOT_RUN 和未建模恢复失败关闭边界继续有效，不宣告 G1/G5。

Caller 已调用 `approve_task(threadId="thread_20261024_081554_9483")`，Bridge 返回 COMPLETED。随后将远端已有统一分支 `6f1fbee7d8fe94bf98307800168acf56b05d19c4` 合入当前工作树，得到 `a07e1db12c03922895822e3dd7439e8e580d3fcd`，无冲突；该合并仅新增已验收的 traceability 文档/工具，`git diff --exit-code 48c3500..a07e1db -- dotnet` 为 0，已测 .NET 源码和测试完全一致。

统一交付沿用 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`，目标 `v5`。本任务的全部本地提交一次性纳入该分支，不创建子任务 PR，不包含其他尚未验收工作树，不合并或部署 PR。远端最终提交以 Bridge `record_merge_request` 的记录为准。
