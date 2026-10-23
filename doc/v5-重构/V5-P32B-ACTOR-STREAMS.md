# V5 P3.2b actor 流注册与帧边界交付说明

日期：2026-10-24（第 4 轮返修）
状态：**P3.2b 组件已针对第 3 轮决策刷新/容量回收阻断返修；不是完整 P3.2、P3.4 或 G1 通过。**
工作树：`D:/majiang-wt/p32-streams`，分支 `wt/v5-p32-streams`。
基线：`3e78367538b4f7520a1d414c82ef3155507a5206`（PR #19 已审查 head；已含 P3.2a、P3.3）。
第 1 轮交付：`ac2f4ab6756024f6e96474ad76109954c5fe1736`，caller FAIL（句柄来源/恢复队列/回调阻塞）。
第 2 轮交付：`ec8decbe5a65fe92970737118ee1a460260ff554`，caller FAIL（旧 generation completion 会关闭当前出箱）。
第 3 轮交付：`dae514588e5146414173d471c7f1904ca7b73782`，caller FAIL（OpenDecision 不刷新流；关闭 sink 占用 MaxStreams）。
本轮在 `dae5145` 上返修，并保留 caller 新增的 `CodexP32bReviewProbes.cs`、`CodexP32bRound2ReviewProbes.cs`、`CodexP32bRound3ReviewProbes.cs`。
范围：TableActor/mailbox 的流接线、流注册、按流投影与发送、恢复/失租/故障生命周期、测试与说明。未改 Contracts/Wire、Domain、P3.2a 投影源/上下文、项目文件（csproj/sln）、Unity 环境/Resources/Burst、CI 或兄弟 P3.2c 文件。

> P3.2a 的纯投影组件已独立验收；本交付只负责把它接到 actor 的提交后时序、流身份、游标、队列和生命周期上。实际 Protobuf/Wire 打包、真实 PG、WSS、Unity/设备与完整 G1 仍未完成，由 caller 在 P3.2c 接入后联合验收。

---

## 1. 交付边界

### 1.1 已实现

- `Subscribe/Unsubscribe/Sync` 作为 actor 控制消息进入**同一个有界 mailbox**，由唯一消费者串行处理；首 full baseline 的投影、入队和注册在同一 actor turn 内完成，没有“先读 snapshot、再由别的线程注册”的窗口。
- 席位 0..3 与公共观战流各自独立的 `(sessionId, audience)` 流身份；观战只拿 `PublicSnapshotView`，席位拿 `PlayerSnapshotView` + `TableOwnerPrivateView`。多观战者各自的公开事实载荷一致，但游标和身份独立。
- `TableStreamHandle` 绑定 actor 私有来源对象 + token，`Equals`/`GetHashCode` 均包含来源；`Unsubscribe/Sync` 先校验来源、当前注册对象引用和 token，外 actor 的同号 token 不能命中、更不能借外来席位 handle 生成本地私有帧。
- `bindingEpoch`、`syncEpoch`、`controlEpoch`、`ownerEpoch`、`recoveryGeneration` 分离校验；`bindingEpoch` 只作为宿主签发的重绑代数/防回退水位，不是出牌授权。出牌仍由 P3.1/P3.3 的 owner/control/round/state 校验。
- commit 成功并安装新状态后才投影；失败/取消/拒绝的 commit 零发布。投影失败不改回/不回滚已持久状态，关闭受影响的流并记录 `PublicationFault`。
- `OpenDecisionAsync` 成功进入 `_pending`/`DecisionClock` 后，在同一 actor turn 内对所有已订阅流发一次 same-state refresh：`projectionSeq` 递增，`stateVersion`/`controlVersion` 不变，不伪造 Domain 事件；只有本席 private 得到 `DecisionId`/候选/剩余时间，其他席/观战只得到公开 `Clocks`。无效/重复/拒绝的 OpenDecision 不发布。
- 每个订阅在容量判断前先回收 `Channel.IsClosed` 的旧 registration；关闭/失败的 consumer 不再永久占用 `MaxStreams`。身份 high-water 保留：旧 handle 无权操作新流，已消费的 binding/sync 精确重放被拒绝，新 session 的合法 binding/sync 可立即恢复服务。
- 每流 `projectionSeq` / `streamVersion`：只按实际发射递增；`streamVersion` 是观察到已提交的 `stateVersion`，控制变更只增加 `controlVersion` 与 `projectionSeq`，不伪造 stateVersion。事件 `eventSeq` 与流游标分离。
- 每流独立有界队列和单发送泵；慢 sink 不阻塞 actor；消息数上限始终生效，配置可信编码器时同时按真实编码字节数限流。溢出/编码失败/投影失败/失租/关闭都有明确终止原因。
- 恢复：`BeginRecovery` 递增 generation 后立即对所有 channel 做 **fence + purge**：清空旧代次排队帧、禁止旧 generation 再入队。发送泵在调用 sink 前通过 `TryClaimDispatch` 在 channel 锁内对当前 `allowedGeneration` 做一次原子认领：fence/close 先发生则丢弃，认领先发生则唯一在途调用不可撤回；不再使用解锁前缓存的 generation。旧 generation 的失败/取消 completion 不会关闭或清空当前 generation 的出箱，当前 generation 的真实 sink 错误仍会断流。
- `CompleteRecovery` 安装后对仍有效流排入新代次 full baseline 并置 `NeedsSync`，宿主显式 Sync 前不发送增量。旧 owner/旧控制者的私有流关闭而不是继续发旧内容。
- 取消与 termination 观察者不在 actor 线程、也不在 channel 锁内执行：`Cancel()` 与 `OnStreamTerminated` 均以一次性后台任务派发，阻塞/重入/抛异常的下游回调不能卡住 mailbox。
- 新测试直接使用真实 `TableActor` 公开 API 和受控 commit/sink；覆盖提交前后订阅、慢 commit、四席与观战隔离、独立游标、重绑/切换、旧 binding/sync/round/recovery、慢 sink/溢出、commit 失败、投影失败、恢复队列代次、控制变更和编码边界。既有 104 个 TableRuntime 用例与 caller provenance 探针保持不变并通过。

### 1.2 明确未实现（P3.2c / P3.4 / 其他 owner）

- Contracts/Wire protobuf 事件体与快照编码；`ITableStreamFrameEncoder` 目前只有测试假实现，不是真实 Wire。`TableStreamFrame` 是内存边界。
- P3.2c 的新增 Contracts 载荷、Wire 适配和最终字节联合测试。
- P3.4 的 `ITableStore/IReceiptStore/ITableLease/ISettlementOutbox`、检查点、持久恢复、重复/并发回执合同。
- 真实 PostgreSQL、WSS/TCP、Unity/IL2CPP/Android/iOS、G1/G5 阶段验收。
- 未配置可信 projection context source 时，席位流会被拒绝；这不是可用默认值，生产装配必须由 caller 提供真实 facts/cheat/session 来源。
- 当前 actor 不执行 P1.4 cheat 命令；`TableProjectionContextData.Cheat` 由可信源提供。Domain 没有建模的 meld/riichi/furiten/preparation/session 事实不做推断。

---

## 2. 公共 API 与帧边界

新增文件：

| 文件 | 作用 |
|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs` | 流身份/句柄/请求/结果/帧/编码器/投影上下文端口/流选项 |
| `dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs` | 每身份水位、每订阅注册、有界队列与异步发送泵 |
| `dotnet/src/Mahjong.TableRuntime/AssemblyInfo.cs` | 仅向测试程序集开放 internal dispatch seam |

改动：

| 文件 | 作用 |
|---|---|
| `TableActor.cs` | 流 API、mailbox 处理、提交后投影/发布、OpenDecision 刷新、容量回收、恢复/失租/关闭/故障联动 |
| `TableActorMessages.cs` | Subscribe/Unsubscribe/Sync 三类控制消息 |
| `TableStreamActorTests.cs`、`TableStreamTestSupport.cs` | 29 个 P3.2b case 与受控 sink/commit/投影源 fixture |
| `CodexP32bReviewProbes.cs`、`CodexP32bRound2ReviewProbes.cs`、`CodexP32bRound3ReviewProbes.cs` | caller 第 1/2/3 轮探针，原样保留并纳入交付 |
| `CodexP32bReviewProbes.cs` | caller 第 1 轮新增的 5 项独立探针，原样保留并纳入交付 |

### 2.1 actor 入口

```csharp
ValueTask<TableStreamControlResult> SubscribeStreamAsync(
    TableStreamSubscribeRequest request,
    CancellationToken cancellationToken = default);

ValueTask<TableStreamControlResult> UnsubscribeStreamAsync(
    TableStreamHandle handle,
    CancellationToken cancellationToken = default);

ValueTask<TableStreamControlResult> SyncStreamAsync(
    TableStreamSyncRequest request,
    CancellationToken cancellationToken = default);
```

`SubscribeStreamAsync` 的请求包含：

- `TableStreamIdentity(SessionId, TableStreamAudience, BindingEpoch, SyncEpoch)`；
- `OwnerEpoch`、`RecoveryGeneration`（宿主从权威状态读取，不是客户端值）；
- `ITableStreamSink`；
- 可选 `ExpectedRoundId`、席位可选的 `ExpectedControlEpoch`。

处理顺序（全部在 actor 单消费者内）：

1. 校验生命周期、owner、recovery generation、round、席位控制器 epoch；
2. 校验 binding/sync 不回退；同一身份已有活跃注册且 binding/sync 未前进时返回 `ALREADY_BOUND`；
3. 计算该身份的候选 `projectionSeq`（新 syncEpoch 从 0 重置；同一 syncEpoch 的重绑延续）；
4. 用当前已提交状态构造 full baseline；
5. 创建有界 channel 并先入队 baseline；
6. 只有第 5 步成功，才将新 handle 设为当前注册，并关闭被替换的旧 channel；
7. 返回 `Subscribed` + baseline cursor。

因此不存在“baseline 已生成但事件可能先到”的顺序；若第 4/5 步失败，旧注册不变或保持关闭，不产生半个新流。

`SyncStreamAsync` 语义：

- handle 必须是该身份当前注册；旧 handle 不复用、不能取消新订阅；
- `ExpectedSyncEpoch/BindingEpoch` 不匹配 → `Rejected`；
- owner 不匹配或已失租 → `Closed`，不向旧 owner 发新内容；
- 旧 recovery generation → `Rejected`；
- round 不匹配、游标不匹配、未来游标或历史缺口 → 入队 full baseline，返回 `BaselineQueued`（`STREAM_GAP`），不补空事件；
- 恢复 baseline 刚安装时该流置 `NeedsSync`，增量帧被扣住；`Sync` 先建立当前 full baseline 并清门控，之后才恢复增量；
- 游标、stateVersion、controlVersion 均匹配且不在 `NeedsSync` → `InSync`；
- channel 已因慢消费/编码/投影失败关闭 → `Closed`，返回通道终止原因。

### 2.2 `TableStreamFrame`

每帧携带：

| 字段 | 说明 |
|---|---|
| `Handle/Identity` | session、audience、binding/sync epoch |
| `IsFullSnapshot` | 明确 baseline/reset 标记；更新帧为 false |
| `Cursor` | `StreamCursor(StreamVersion, ProjectionSeq)`，只属于本流 |
| `StreamVersion` | 发送时观察到的已提交 `stateVersion` |
| `StateVersion` | 同一已提交 stateVersion 的显式副本 |
| `EventSeq` | 表级事件水位，仅诊断；**不是**流 cursor |
| `OwnerEpoch/ControlVersion/RecoveryGeneration` | 独立生命周期/控制元数据 |
| `RoundId` | 当前已提交 round |
| `Public` | 观战 `PublicSnapshotView`；席位帧为 null |
| `Player` | 席位 `PlayerSnapshotView`（含公开部分 + 本席私有）；观战帧为 null |
| `OwnerPrivate` | 席位 owner 扩展（袖牌/标记组/offer/候选等）；观战帧为 null |
| `Events` | 本次发布中该 audience 实际可见的 `TableProjectedEvent`，可为 0 条 |
| `EncodedPayload/PayloadSize` | 仅当配置可信编码器时存在；否则为 null/0，不做字节冒充 |

帧的边界是**内存投影**，不是 protobuf：

- 观战帧只允许 `Public`；构造函数拒绝观战帧携带 `Player/OwnerPrivate`。
- 席位帧只允许 `Player`（含 `Player.Public`）和 `OwnerPrivate`；不额外复制公共对象。
- 每帧都带当前完整公开/本席允许私有快照；更新帧不能是空 payload。没有可见事件时仍发送当前快照，`projectionSeq` 连续推进，不用空事件补洞。
- 不包含 `TableSnapshot.StateFingerprint`、`SourceFingerprint`、墙顺序、种子或完整 Domain 对象。

### 2.3 谁拥有流/游标

- actor 拥有 `TableStreamIdentityState`（binding/sync 高水位、`ProjectionSeq`、`StreamCursor`、当前 handle token）和每 actor 唯一的私有 handle 来源对象。
- `TableStreamHandle` 的相等性/哈希包含来源 + token；`FindCurrentRegistration` 先拒绝外 actor 来源，再要求当前注册对象引用完全一致，最后校验 token。`Sync` 的 baseline 只使用注册自己的 handle 身份，不使用请求传入的身份。
- channel 只拥有发送队列；它不读 actor 状态、不回写游标。
- sink 只收到帧；sink 的慢/异常不会在 actor 线程等待，也不会修改状态。
- 身份在水位表中保留，unsubscribe 后旧 sync/binding 不能回退；同一身份的新 syncEpoch 才重置 `projectionSeq`。

---

## 3. Trusted projection context 责任

### 3.1 端口

```csharp
public interface ITableProjectionContextSource
{
    TableProjectionContextData Capture(TableProjectionContextRequest request);
}
```

`TableProjectionContextRequest` 绑定 `RoomId`、已提交 `RoundStateMachine`、`StateVersion`、`LastEventSeq`、`OwnerEpoch`、`ControlVersion`、`RecoveryGeneration` 和 actor 时钟。`TableProjectionContextData` 返回：

- `RoundId`、`StateVersion`（必须等于请求值，否则 actor 拒绝）；
- `PublicFacts`（四席 meld/discard/riichi；逐张对照 Domain Meld/Discard zone）；
- `PrivateFacts`（本席 furiten/preparation/discard history）；
- `Session`（整场结束/排名/结算 round，缺省为中性未结束）；
- `Policy`（paused/settlementBatch/revealSettlementUra；由权威结算批/产品授权，不由客户端声明）；
- `Cheat`（`CheatState`，席位流必需；观战忽略）；
- `SessionVersion`（诊断用）。

actor 自己构建 `ClockView` 与 `TableSeatDecision`（P3.3/P3.1 冻结的 deadline/候选），并把可信 facts 传给已验收的 `TableVisibilityProjection`/`TableEventProjection`。Domain 未提供的事实不允许由本 actor 猜测；source 必须显式给出并受 round/state 校验。

### 3.2 责任边界

- source 是服务端可信端口，可以读取 Domain 状态来生成 discards/melds；它不是传输 sink。最终发给 sink 的只有白名单 view/projection 帧。
- source 必须为每个已提交版本重新捕获；actor 不缓存跨版本的 facts。
- 四席 facts 应完整提供；P3.2a 会在有 meld/discard 的席位上检查缺失/不一致。
- 席位流缺少 `Cheat` 时 actor 拒绝该次投影/订阅；不会用空账本伪装真实私有状态。
- source 抛异常或返回错 round/version 时，已提交状态不回滚；受影响流按 `ProjectionFailed` 关闭并写 `PublicationFault`。

### 3.3 决策映射

actor 从 `_pending` + `DecisionClock` 生成 `DecisionView`：

- `DecisionId`、`ControlEpoch`、剩余 deadline、候选来自已冻结的 PendingDecision；
- `LegalActionKind` 仅映射 Contracts 已冻结的 action 类型；`Cheat/Other` 等尚无 Contracts 类型的候选不伪造；
- 旧/过期/paused 决策不进入快照；
- P3.2c 若扩展 Contracts 动作类型，需要同步扩展该映射。

---

## 4. 单消费者原子点与发布顺序

### 4.1 提交成功后

`TableActor.CommitAndInstallAsync` 的顺序保持：

1. `ITableCommitPort.CommitAsync` 返回 `Committed`；
2. 安装 `_round/_stateVersion/_lastEventSeq`；
3. 关闭已提交的 decision（若属于该命令）；
4. 追加 recent events；
5. `BuildSnapshot()`（P3.1 authority snapshot）；
6. `Publish(TablePublication.Applied)`：
   - 先 `PublishToStreams(publication)`：按流构造 context、投影、编码、入队；
   - 再调用既有同步 `ITablePublicationSink`（P3.1 兼容）。
7. 返回 `TableOperationResult.Accepted(receipt, records)`。

`PublishToStreams` 不等待任何 sink；慢消费者只影响自己的 channel。流投影在 actor 线程完成，使用同一已安装状态；不存在“状态提交后、事件发布前另一个线程插入”的窗口。

### 4.2 基线/事件顺序

- 订阅前提交：已有流先收到该提交的 update；随后订阅在 actor turn 内看到提交后状态，baseline 已包含该提交的结果（不会重复发事件，也不会漏状态）。
- 订阅后提交：订阅先 baseline（旧状态），提交后 update（新状态 + 事件）。
- 提交阻塞期间排队的订阅：commit 完成后先发布给已有流，再处理订阅 baseline；新流只收到 post-commit baseline。
- 控制变更：提交 control port 成功后安装 control、发布 `ControlChanged`；每流收到同一 stateVersion 的刷新帧（新 controlVersion、新 projectionSeq），不增加 stateVersion。
- OpenDecision：`ProcessOpenDecision` 成功后同一 actor turn 内刷新已订阅流；stateVersion/controlVersion 不变，只有 projectionSeq 前进。订阅在 OpenDecision 之前则先 baseline（无 decision）、再收到刷新；订阅在之后则 baseline 直接包含 decision。`Sync` 用旧 baseline cursor 会因 projectionSeq/stream cursor 变化返回 `BaselineQueued`，不依赖客户端传 `StreamCursor.Empty`。
- 恢复：安装 baseline 后发布 `Recovered`；仍有效且 owner/controller 匹配的流收到 `IsFullSnapshot=true` 的新 baseline；不匹配的流关闭。该 baseline 同时置 `NeedsSync`：在宿主显式 `Sync` 到达前不再发送增量；期间若提交/控制变更推进，`Sync` 会用当前状态再发 full baseline 并清门控，避免“恢复基线后静默跳过更新”。

### 4.3 失租/关闭/故障

- `LeaseLost`：发布 `LeaseLost`，所有流以 `LEASE_LOST` 关闭，之后不再入队内容。
- `CloseAsync`：关闭标记处理时所有流以 `LOCAL_CLOSED` 关闭。
- actor 消费异常：`SetFaulted` 关闭所有流，reason `INTERNAL_ERROR`。
- `ProcessBeginRecovery`：递增 recovery generation 后立即对所有 channel 执行 `PurgeAndFence(newGeneration)`；旧 generation 的排队帧被清空。泵在出队后、sink 调用前用 `TryClaimDispatch` 在锁内对**当前** fence 做认领：fence 先发生则丢弃，认领先发生则该次调用不可撤回；已认领的旧 completion 不会关闭新代次出箱。Recovering 期间没有提交/发布，不推进 deadline。
- `CompleteRecovery`：新代次 full baseline 入队并置 `NeedsSync`；旧 owner/旧 controller 的私有流直接关闭。恢复期间释放慢 sink 时不会出现第二个旧发送，恢复后的首新发送必须是 full baseline。

### 4.4 下游回调隔离

- `TableStreamChannel.Close`、溢出关闭和 sink 异常关闭都只做 `_closed`/清队列/生成一次 termination，然后立即返回；actor 绝不等待 sink。
- `CancellationTokenSource.Cancel()` 会在后台任务中调用，避免执行 sink 注册的取消回调阻塞 actor；异常被隔离。
- `ITableStreamTerminationSink.OnStreamTerminated` 同样以一次性后台任务派发，不在 channel 锁内、不在 actor 线程；阻塞/重入/抛异常不改变 mailbox 进度，也不会重复通知。

---

## 5. 每流约束、失败策略与可观测性

### 5.1 队列

`TableStreamOptions`：

- `MaxQueuedFrames`（默认 64）始终生效；
- `MaxQueuedBytes`（默认 4 MiB）仅在配置 `ITableStreamFrameEncoder` 时生效；
- `MaxStreams`（默认 64）；
- `Encoder`、`TerminationSink`。

每个注册有独立 `TableStreamChannel`：

- actor 只做 `TryEnqueue`；一个 FIFO 泵在 thread pool 上调用 sink；
- 消息数或字节数超限 → 关闭该 channel、清空其队列、异步通知 termination，actor 继续处理其他流；
- recovery fence 后旧 generation 的排队帧被丢弃，`TryEnqueue` 也拒绝旧 generation，防止恢复后重放旧内容；
- 溢出不会被说成“补一个空事件”；该流必须重新订阅取得 full baseline；
- 没有 encoder 时 actor 不声称对象估算是 protobuf 字节，也不做假的字节上限；此时消息数上限仍是硬上限。

### 5.2 编码/字节端口

`ITableStreamFrameEncoder.Encode(TableStreamFrame)` 返回 `TableEncodedFrame(bytes, declaredSize)`。actor 在入队前校验：

- `bytes` 非空且长度 > 0（零长度 → `EncoderFailed/MALFORMED_MESSAGE`）；
- `declaredSize == bytes.Length`（谎报 → 拒绝）；
- 长度 ≤ `MaxQueuedBytes`（超限 → `MESSAGE_TOO_LARGE`）；
- encoder 抛异常 → `EncoderFailed/INTERNAL_ERROR`；
- 校验失败不会注册新流；更新帧校验失败会关闭该流，已提交状态保留。

测试只用确定性假 encoder，不代表真实 Protobuf/Wire；P3.2c 的真实编码器接入后，caller 仍需做最终字节联合测试。

### 5.3 终止结果

`TableStreamTermination` 的 `Kind/Reason/Detail` 明确区分：

- `Unsubscribed`、`Displaced`、`SlowConsumer`、`MessageTooLarge`；
- `ProjectionFailed`、`EncoderFailed`；
- `StaleControlEpoch`、`StaleOwner`；
- `LeaseLost`、`ActorClosed`、`ActorFaulted`。

sink 异常也会关闭该流（`ActorFaulted/INTERNAL_ERROR`），但不会把 actor 生命周期置为 Faulted；actor 不因下游异常回滚已提交状态。termination 与取消回调均为 actor 外的一次性后台派发；`OnStreamTerminated` 阻塞或抛出不会阻塞 `GetSnapshot/Unsubscribe/Close` 等 mailbox 操作。

### 5.5 第 1–3 轮阻断的修复语义

- **句柄来源**：`TableStreamHandle` 增加 actor 私有来源对象；`Equals/GetHashCode` 使用来源 + token。`FindCurrentRegistration` 先拒绝外 actor 来源，再要求 `ReferenceEquals(registration.Handle, handle)`，最后校验当前 token。`Sync` 基线使用 `registration.Handle` 构造帧，不再使用调用方传入的 handle 身份；外 seat handle 不能把本地 spectator 流变成 `Player/OwnerPrivate` 帧。
- **恢复隔离/原子认领**：`ProcessBeginRecovery` 在递增 generation 后立即对所有 channel `PurgeAndFence`；旧排队帧被清空。泵在出队后、sink 调用前调用 `TryClaimDispatch`：它在 channel 锁内读取**当前** `_allowedGeneration`，fence/close 先发生则丢弃，认领先发生则该次调用不可撤回。不再使用解锁前缓存的 generation 值，因此“摘出后暂停、随后 fence/close、再恢复泵”不会启动新的旧派发。已认领旧 generation 的 sink 抛错/取消时，catch 先比较 frame generation 与当前 fence：旧 completion 只清理自身在途状态，不关闭、不清空当前出箱；当前 generation 的错误仍按 `ActorFaulted/INTERNAL_ERROR` 断流。
- **回调隔离**：`CloseLocked` 不再同步 `Cancel()`；`Close`/overflow/sink 异常都只生成一次 termination，然后由 `Task.Run` 执行取消和 termination 通知。actor 线程不等待任何下游回调；`BlockingTermination` 与 `BlockingCancellation` 探针证明 `GetSnapshot/Unsubscribe` 仍能在 500ms 内完成。
- **决策刷新**：`OpenDecisionAsync` 成功返回 `Configured` 后，actor 立即调用 `PublishDecisionRefreshToStreams`；它复用当前 `CaptureStreamProjection` 生成新 `projectionSeq` 的 same-state 帧，`stateVersion` 不变、无 Domain event。`BuildStreamDecisions` 只把决策放进该席 `PlayerSnapshotView.Private.Decision`；其他席/观战的 `PublicMatchView.Clocks` 可看到公开 deadline。失败/重复/拒绝返回不触发刷新。`Sync` 通过已推进的 stream cursor 识别丢失的决策刷新，返回 `BaselineQueued` 并重建 full baseline；`NeedsSync` 期间刷新被扣住，由恢复后的 Sync baseline 补齐。
- **容量回收**：`ProcessSubscribeStream` 在任何容量判断前调用 `RemoveFaultedStreams`，清理 `Channel.IsClosed` 的 registration；同时保留 identity high-water，旧 handle 继续被拒绝，已消费的精确 binding/sync 重放也被拒绝。新 session 可在旧 consumer 故障后立即获得空出的容量，无需 command/control publication 或 Sync 旧死 handle。

### 5.4 私有隔离

- 观战帧结构上只有 `PublicSnapshotView`；没有 `Player/OwnerPrivate` 字段。
- 席位帧只含本席 `PlayerSnapshotView` 和本席 `TableOwnerPrivateView`。
- 事件逐 audience 过滤；`TileDrawn/TileDealt/RinshanDraw/CheatPointsChanged/SleeveBloomResolved/BeamSwapResolved` 只到 owner；观战无私有事件；暗杠事件类型/座位公开、四张牌 identity 仅 owner。
- 不发送 `StateFingerprint/SourceFingerprint`/墙顺序/种子/完整 Domain。
- 控制器变更会关闭旧控制者的席位流；旧 handle 不能订阅新席位私有内容，也不能取消新订阅。

---

## 6. P3.2c 最终适配/接线步骤

建议 caller 按以下顺序接入（本任务不实现这些步骤）：

1. **实现真实 `ITableProjectionContextSource`**：由权威规则/控制层把 meld/discard/riichi/furiten/preparation/session/cheat 状态映射为 `TableProjectionContextData`，每个 `Capture` 用请求的 round/state 校验；不要把客户端字段直接填入。
2. **实现 `ITableStreamFrameEncoder`**：把 `PublicSnapshotView`、`PlayerSnapshotView`、`TableOwnerPrivateView`、`TableProjectedEvent` 映射到 P3.2c 的 protobuf 载荷。不要发送 `TableProjectedEvent.EncodeBody()` 作为 Wire payload；不要重新序列化已编码字节当规范摘要。
3. **装配 actor**：使用 8 参数构造函数
   `new TableActor(options, round, commitPort, clock, publicationSink, controlCommitPort, projectionSource, streamOptions)`；
   生产必须提供 source 与 encoder，并为每流设置 fib 有界队列和 termination sink。
4. **映射流身份**：从 WSS 票据/宿主权威状态生成 `SessionId`、audience、`BindingEpoch`、`SyncEpoch`、`OwnerEpoch`、`RecoveryGeneration`；不要把客户端 local binding epoch 当授权。
5. **接 Subscribe/Unsubscribe/Sync**：把 WSS 的 `HELLO/WELCOME`、`SYNC_REQUEST`、重连/rebind 映射到这三个 actor 调用；baseline/update 帧的 `IsFullSnapshot`、`Cursor`、`ProjectionSeq`、`StreamVersion` 原样进入 envelope。
6. **接 Wire 事件**：`TableStreamFrame.Events` 只包含可见事件；P3.2c 负责 protobuf 映射和未知字段/私有嵌套拒绝；`EventSeq` 作为诊断字段，不用作客户端 cursor。
7. **接慢消费者/关闭**：`ITableStreamSink.PublishAsync` 必须异步且不得同步等待 actor；termination sink 把 `SlowConsumer/MessageTooLarge/ProjectionFailed/StaleControlEpoch/LeaseLost` 映射为传输断开/重同步。
8. **联合验收**：真实 Wire 字节、P3.2c 新 Contracts 载荷、真实 WSS、恢复/租约/持久化仍需 caller 与 P3.4/P5 一起验；本交付不能替代。

---

## 7. 验证记录

### 7.1 基线

| 项目 | 结果 |
|---|---|
| 开始时 `git status --porcelain=v1 -b` | `## wt/v5-p32-streams`，干净 |
| 开始时 `git rev-parse HEAD` | `3e78367538b4f7520a1d414c82ef3155507a5206` |
| 基线 TableRuntime Release | 104 passed / 0 failed / 0 skipped，exit 0 |

### 7.2 实际运行（全部使用 `D:/Unity/dotnet/dotnet.exe`，Release）

| 命令 | 结果 | 退出码 |
|---|---|---|
| `dotnet build dotnet/src/Mahjong.TableRuntime/Mahjong.TableRuntime.csproj -c Release --nologo -v:minimal` | 成功，0 warning / 0 error | 0 |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bRound3ReviewProbes"` | **3 passed / 0 failed / 0 skipped**（本轮 caller 探针） | 0 |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bRound2ReviewProbes"` | **2 passed / 0 failed / 0 skipped** | 0 |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bReviewProbes"` | **5 passed / 0 failed / 0 skipped** | 0 |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal` | **143 passed / 0 failed / 0 skipped**（104 基线 + 29 TableStreamActorTests case + 5 第 1 轮探针 + 2 第 2 轮探针 + 3 第 3 轮探针） | 0 |
| `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal` | **82 passed / 0 failed / 0 skipped** | 0 |
| `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal` | **313 passed / 0 failed / 0 skipped** | 0 |
| `dotnet build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 成功，0 warning / 0 error（12 个项目） | 0 |
| `git diff --check` | PASS（无空白错误） | 0 |

原始日志（未提交，位于工作树根）：

- `artifacts-p32b-baseline-tabletests.log`（基线 104）
- `artifacts-p32b-build-round4.log`（TableRuntime build）
- `artifacts-p32b-round3-probes-final.log`（本轮 caller 3 项，3 passed）
- `artifacts-p32b-round2-probes-round4.log`（2 passed）
- `artifacts-p32b-round1-probes-round4.log`（5 passed）
- `artifacts-p32b-tests-round4-first.log`（最终全量 143）
- `artifacts-p32b-contracts-tests-round4.log`（82）
- `artifacts-p32b-domain-tests-round4.log`（313）
- `artifacts-p32b-solution-build-round4.log`（最终 solution build）
- `artifacts-p32b-caller-round3-framework-original.log`（caller 第 3 轮原始框架失败日志，保留备查）

> 第 1 轮 121、第 2 轮 129、第 3 轮 135 都是历史结果，分别被 caller 判定 FAIL；本轮不把它们当作通过证据。当前有效结果为 143 与 caller 5+2+3 项探针。

### 7.3 测试覆盖

`TableStreamActorTests`（29 个 case；含第 1–3 轮修复回归）：

1. 观战 baseline 明确为 full、只含 public、Sync 为 InSync；
2. 四席 + 两观战：私有手牌/私有事件隔离、观战公开字节一致、无 fingerprint/private 字段；
3. 慢 commit 期间排队的订阅得到 post-commit baseline，不漏状态/不重复事件；
4. commit 前订阅：baseline 后收到 update；
5. commit 失败：零流帧；
6. 提交后投影失败：状态保留、流隔离、可重新 baseline；
7. 恢复期间 sync 门控、恢复后 full baseline、Sync 释放门控；
8. 提交后恢复：最终已提交状态进入 baseline；
9. 失租：所有流关闭且不再发布；
10. 新 binding 替换旧 handle；旧 handle 不能 Sync/取消；
11. 游标缺口/round 不匹配 → full baseline；旧 sync epoch 拒绝；
12. 玩家→观战切换保持独立身份、观战无私有泄漏；
13. 慢 sink 不阻塞 actor；队列溢出只关闭慢流；
14. 零长、谎报 size、超大、编码异常均在注册前拒绝；
15. 控制变更刷新同一 stateVersion、只增 controlVersion；
16. 控制器 epoch 变更关闭旧席位私有流、观战刷新、可重新订阅；
17. 旧异步 sink 延迟完成不污染新注册；
18. 外 actor handle 不能 Unsubscribe/Sync；外 seat handle 不能把本地观战流变成私有帧；合法本地 handle 仍可 InSync/继续收 update；
19. 恢复期间阻塞首 baseline、让旧 update 排队；`BeginRecovery` 后释放 sink，恢复期间只有一次旧调用、无第二个旧发送；`CompleteRecovery` 后首个新发送为 generation 1 full baseline；
20. termination sink 阻塞时，`ReportLeaseLost` 与随后 `GetSnapshot` 均能在 actor 线程外完成，不被下游回调卡住；
21. `BeforeDispatchClaim` 处暂停后，fence 或 close 先完成；释放后旧帧不得调用 sink（2 case）；
22. 旧 generation 0 sink 在 Recovering 期间抛 `IOException`，不关闭出箱；随后 generation 1 baseline 可发送；第二次恢复得到 generation 2 baseline；`Sync` 后继续收 generation 2 update；
23. 当前 generation 的 sink 抛 `IOException` 仍明确断流为 `ActorFaulted/INTERNAL_ERROR`；
24. **本轮新增**：OpenDecision 成功刷新已订阅本席，`StateVersion/ControlVersion` 不变、`ProjectionSeq+1`、无 Domain event；本席 private 有 decision id/剩余时间，其他席/观战只有公开 clock；
25. **本轮新增**：无效 OpenDecision 与重复 OpenDecision 不发布；成功 OpenDecision 后旧 baseline cursor 的 Sync 返回 `BaselineQueued` 并重建含 decision 的 full baseline；
26. **本轮新增**：恢复 `NeedsSync` 期间 OpenDecision 被扣住，Sync 才建立含 decision 的 full baseline；
27. **本轮新增**：MaxStreams 真正满时拒绝新身份；Unsubscribe 后同 session 新 binding/sync 可立即订阅；
28. **本轮新增**：首 baseline sink 失败后，新 session 在无 command/control/Sync 旧死 handle 的情况下立即获得容量；旧 handle 与精确旧 binding/sync 重放被拒绝。

Caller `CodexP32bReviewProbes`（保留并纳入交付，5 项）：

- `ForeignActorHandleCannotUnsubscribeLocalStream`
- `ForeignSeatHandleCannotSyncSpectatorIntoPrivateFrame`
- `RecoveryDropsQueuedOldGenerationBeforeNewBaseline`
- `DownstreamTerminationAndCancellationCallbacksCannotBlockActor(false/true)`

Caller `CodexP32bRound2ReviewProbes`（保留并纳入交付，2 项）：

- `OldGenerationSinkFailureCannotDiscardRecoveredBaseline(false)`（IOException）
- `OldGenerationSinkFailureCannotDiscardRecoveredBaseline(true)`（OperationCanceledException）

Caller `CodexP32bRound3ReviewProbes`（保留并纳入交付，3 项）：

- `ClosedSinkDoesNotRetainActiveStreamCapacity`
- `OpenDecisionRefreshesExistingSeatStream`
- `SyncCannotReportInSyncWithMissingDecision`

### 7.4 未运行 / 不计入 PASS

| 项目 | 状态 |
|---|---|
| 真实 Protobuf/Wire 联合字节测试 | **NOT_RUN**（P3.2c 接入后由 caller 验） |
| PostgreSQL / 统一 PG 入口 | **NOT_RUN**（本任务无需配置 PG；不冒充既有 814 证据为本轮重跑） |
| TCP/WSS 真实网络 | **NOT_RUN** |
| Unity Editor / IL2CPP / Android/iOS 设备 | **NOT_RUN** |
| P3.4 持久化/租约/冷恢复 | **NOT_IMPLEMENTED** |
| P3.4 完整回执/恢复端口 | **NOT_IMPLEMENTED** |
| G1 / G5 阶段验收 | **未通过/未执行**；本交付不能作为 G1/G5 PASS |
| 完整 P3.2 退出标准 | **未完成**：P3.2b 已交付，P3.2c/Wire 接线与联合验收仍待 caller |

---

## 8. 文件与 SHA256

新增/修改源文件（不含本说明）：

| 文件 | SHA256 |
|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `2B5159BE615D9872F5BA91ACB58737CA926C5F4CD2571CF692345BC568D0DBF6` |
| `dotnet/src/Mahjong.TableRuntime/TableActorMessages.cs` | `2A53251BA7B2A55A42AD59E459ADC20B1753BE22ABABC0F837833A5635426C17` |
| `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs` | `99DFFC7EEF68827A565088FCF12146D1A0407C300736990D16E01CD09BA6C7A3` |
| `dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs` | `D0B067820A22C2FFD4D6F10144A1F4C8B8DD167462D183B2A8AFD771C3619B7D` |
| `dotnet/src/Mahjong.TableRuntime/AssemblyInfo.cs` | `43DD4580E35E6EB27A2DCEFE4D371AF42F2762ECECC5000CFF86AF9FFBA29B4A` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs` | `3D9F88C5E7411A321BB3CD89E6AF32AF8CFE783F2963737751F0660E653C6A11` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamTestSupport.cs` | `37F0183F239381C44C4A5FC08370888791A033D9F3FE30E46F7072D8DDC4B19B` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/CodexP32bReviewProbes.cs` | `AD9527CBE8E1A0E6E6533D5693FD7D7BBA5B0E28D506F8D8994783E390CF0AAA` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/CodexP32bRound2ReviewProbes.cs` | `9774C7C0E237F52FFA3231940FE32835D2D0732B106CAA723BA21891B578B05A` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/CodexP32bRound3ReviewProbes.cs` | `71DD825E8C21BBB8BE53EA542C1D2F303CF07F88050B74B40F24D91BC26CA0B3` |

未修改：Contracts/Wire、Domain、`TableVisibilityContexts.cs`、`TableVisibilityProjection.cs`、`TableEventProjection.cs`、`TableRuntimeContracts.cs`、`TableMailbox.cs`、`Mahjong.sln`、任何 csproj/verifier、Unity 环境/Resources/Burst、兄弟 P3.2c 新增 mapper、CI workflow。caller 新增的 `CodexP32bReviewProbes.cs`、`CodexP32bRound2ReviewProbes.cs`、`CodexP32bRound3ReviewProbes.cs` 按授权原样保留并纳入本提交。`AssemblyInfo.cs` 只给测试程序集开放 internal 调度 seam，不改变公开 API。

---

## 9. 仍需 caller 联合验收

- 将 P3.2b 与 P3.2c 的 Contracts/Wire 载荷、真实 Protobuf 编码和最终字节上限联合验证；
- 用真实 `ITableProjectionContextSource` 提供四席 meld/discard/riichi/furiten、整场 session 与 cheat 状态；
- 真实 WSS/TCP 的 baseline/update/断连重绑、慢消费者断开、旧 binding/sync 拒绝；
- P3.4 的持久化、租约、检查点/恢复、回执幂等与真实 PG 合同；
- 恢复事件历史（当前 `_recentEvents` 是 P3.1 窗口，P3.4 冷恢复需显式结算/事件基线）；
- 完整 P3.2 退出标准、G1/G5 和设备/平台证据。

本交付只声明上表实际运行的 **143/5+2+3/82/313** 与 solution build；不声明 P3.4、真实 PG、真实 Wire、WSS、Unity/设备或 G1 通过。
