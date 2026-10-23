# V5 P3.2 actor/Wire 联合接线交付说明

日期：2026-10-24。工作树：`D:/majiang-wt/p32-joint`，分支：`wt/v5-p32-joint`，基线：`437bd53ca8771764d62ef75226b73efded2baabd`（已接受 PR19 真实 head；已含 P3.1、P3.2a、P3.2b、P3.2c、P3.3、`0e3fa52`、`09d2a4f`）。

状态：**P3.2 缺失的 actor -> 白名单投影 -> Contracts typed payload -> 真实 ProjectionWireMapper/WireCodec 严格编解码 -> 当前 Client 接收 联合接线已在本地实现并通过定向 Release 测试；不是 G1/G5，不是远端 CI、真实 WSS/PG、Unity/IL2CPP、Android/iOS 或跨物理网络验收。** P3.4 仍等待本任务完成后的独立工作。

> 本任务只补 taskbook P3.2 明确未完成的联合接线。已接受的 P3.2a/P3.2b/P3.2c/P3.3 组件不重做；未改 Contracts/Wire、Domain、`Mahjong.sln`、verifier、CI/workflow、Unity 环境/Resources/Burst、machine/user 全局环境；未 push、未建 PR、未触发 CI、未转派、未合入兄弟 worktree 或未接受代码。

---

## 1. 基线、分支与文件范围

| 项目 | 值 |
|---|---|
| `git rev-parse --show-toplevel` | `D:/majiang-wt/p32-joint` |
| 分支 | `wt/v5-p32-joint` |
| 初始 HEAD | `437bd53ca8771764d62ef75226b73efded2baabd` |
| 初始 `git status --porcelain=v1` | 空（干净） |
| 兄弟 worktree | 只读核对；未覆盖、未合并、未改写 |
| 远端 / push / PR / CI | 未执行 |

### 1.1 expectedFiles（实际变更全集）

新增：

1. `dotnet/src/Mahjong.TableRuntime/TableStreamEncodingContracts.cs`
2. `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamWireIntegrationTests.cs`
3. `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamWireTestSupport.cs`
4. `doc/v5-重构/V5-P32-JOINT-WIRING.md`（本文）

修改：

5. `dotnet/src/Mahjong.TableRuntime/TableActor.cs`
6. `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs`
7. `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs`
8. `dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj`
9. `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamTestSupport.cs`

未修改：`dotnet/src/Mahjong.TableRuntime/Mahjong.TableRuntime.csproj`、`Mahjong.sln`、Contracts/Wire 源码、Domain、Client 生产源码、`tools/dotnet/verify-*`、`.github/workflows/*`、Unity 环境/Resources/Burst、其他 worktree。范围清单与实际 `git status` 一致，无第 10 个已跟踪文件。

### 1.2 依赖边界

- `Mahjong.TableRuntime` 仍只引用 `Mahjong.Domain`、`Mahjong.Ai`、`Mahjong.Contracts`；没有新增 `Mahjong.Contracts.Wire`、Google.Protobuf、Client、Socket、数据库或 Unity 依赖。
- 运行时源码中唯一的 `Mahjong.Contracts.Wire` 字样是 `TableStreamEncodingContracts.cs` 的注释，不是引用。
- 真实 Protobuf 编解码在测试工程和宿主装配层完成。测试工程新增 `Mahjong.Contracts.Wire` 与 `Mahjong.Client` 引用，只用于联合验证，不是生产模块依赖。
- 未新建第八模块、LocalHost 骨架或 TableService 骨架。

---

## 2. 接线后的数据流

每次 actor 单消费者发布：

```text
ITableCommitPort.CommitAsync == Committed
  -> 安装 _round / _stateVersion / _lastEventSeq
  -> CaptureStreamProjection()
       - 冻结 committed event 窗口（round/state/version/ordinal）
       - trusted source 返回 explicit facts / cheat / public+private facts / session / policy
       - 校验 fact 的 (type,event_seq,command_version,event_ordinal) 属于同一 capture
       - 取 actor clock 的 monotonic now / utc
  -> 每流：TableVisibilityProjection（原 P3.2a 白名单，不改）
  -> 每流：TableProjectionContractMapper.MapEvent / MapPublicSnapshot / MapPlayerSnapshot
       - typed ProtocolEventRecord、typed public/player snapshot
       - owner sleeve / mark / offer / reservation / window / beam
       - 绝对 monotonic deadline -> relative remaining
       - 公共暗杠：identity-free ConcealedMeldView
  -> TableStreamContractFrame（纯 Contracts DTO，绑定 from/cursor/round/state/control/owner/recovery）
  -> ContractsStreamFrameEncoder -> host ITableStreamContractCodec
       - ProjectionWireMapper.MapPublicUpdate/MapPlayerUpdate/MapEvent
       - WireCodec.EnsureStrict + WireCodec.Encode（生产路径，不用 EncodeCompatibility）
  -> TableStreamFrame.PayloadSize = 实际字节数
  -> 64 帧 / 4 MiB 每流有界队列（现有值未改）
  -> 单流发送泵 -> sink（慢 sink 不阻塞 actor）
```

`TableStreamFrame` 仍是内存投影；新增的 `Contract` 是同一 capture 冻结出的 Contracts DTO。编码器不能回读 actor 状态，也不会把 `TableProjectedEvent.EncodeBody()` 当 Wire payload 发送。

### 2.1 首基线/更新/决策/控制/恢复/Sync 的 FromCursor

| 场景 | FromCursor |
|---|---|
| 新订阅首 full baseline | `StreamCursor.Empty`（0/0） |
| 提交后 update | 该流上一个已发射 cursor |
| OpenDecision same-state refresh | 该流上一个已发射 cursor；`stateVersion/controlVersion` 不变 |
| 控制变更 refresh | 该流上一个已发射 cursor；只增 `controlVersion` |
| 恢复 full baseline | 恢复前该流 cursor；`projectionSeq` 继续，`stateVersion` 为恢复后值 |
| 显式 Sync 重建 full baseline | 该流上一个已发射 cursor；恢复 `NeedsSync` 在 Sync 后清除 |

`Cursor` 始终为该流本次发射后的 cursor；`FromCursor` 进入真实 `PlayerUpdate`/`PublicUpdate`，Client 的 `StreamCursor.Classify` 可直接判定 `Baseline/Advance/Refresh/Gap`。`EventSeq` 只作诊断，不冒充 cursor。

### 2.2 事实端口与 provenance

`TableProjectionEventFact` 绑定：

- `ProtocolEventType Type`
- `EventSeq EventSeq`
- `StateVersion CommandVersion`
- `uint EventOrdinal`
- `TableProjectionEventFacts Facts`

`TableProjectionContextRequest.CommittedEvents` 冻结本次 capture 的事件窗口；`TableProjectionContextData.EventFacts` 返回同一 capture 的事实。actor 在投影前校验：

- fact 的 seq 必须存在于 committed event 窗口；
- type/commandVersion/ordinal 必须与事件输入一致；
- 同 seq 不得重复；
- 没有 committed event 时不得有 fact；
- 事实缺失不在端口处填 0/空列表，而由 `TableProjectionContractMapper` 在事件真正需要时抛 `TableProjectionException(InvalidPayload)`，流按 `ProjectionFailed` 关闭。

Domain 已建模、由 request/record 直接得到的值（座位、事件牌、`WallRemaining`、`RoundWind/RoundNumber`、结算 winner/loser/tenpai/final scores 等）由测试端口的 `WireEventFacts` 从同一 request 推导；Domain 未建模的 turn number、riichi/tsumogiri 标志、kan meld 明细、yaku/han/fu/base score 由显式 `WireEventFactOptions` 或可信宿主端口提供。缺失时端口抛错，不猜测。

### 2.3 暗杠与 owner 边界

- `TableProjectionContractMapper.WithoutMeld()` 只用于公共/他人席的 closed-kan 事件：即使同一份 owner facts 带有四张实体牌，公共路径也只会得到 `KanType=CLOSED_KAN`、无 meld body。
- 公共快照的 `ConcealedMeldView` 只含 `type/count/fromSeat`；owner `PlayerPrivateView.OwnerMelds` 才含四张身份。
- owner 流保持 `SleeveTiles`、私有 ledger、mark group、两个 offer、reservation、window、beam 候选；观战帧结构上只有 `PublicSnapshotView`。
- `MapOwnerCheat` 新增窗口一致性 fail-closed：live off-turn offer 或 reservation 的 window 必须存在且等于该席 `WindowId`；缺 window 不再产生可被误读为空串的载荷。

### 2.4 绝对 deadline

- actor capture 显式记录 `MonotonicMillis`；`MapPlayerSnapshot` 用 `TableProjectionTimeContext(capture.MonotonicMillis)`。
- `SleeveBloomView.has_deadline` + `deadline_remaining_ms`：绝对 deadline=2000、capture now=1000 时线上为 `has_deadline=true, remaining=1000`。
- Beam 无 Domain deadline，明确 `has_deadline=false`，不用 0 伪造“已到期”。
- 不发绝对 monotonic 值。

---

## 3. 宿主可直接复用的严格 codec 装配

下面代码在生产宿主项目中实现 `ITableStreamContractCodec`（该宿主可以引用 `Mahjong.Contracts.Wire`；`Mahjong.TableRuntime` 本身不引用）：

```csharp
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.TableRuntime;

public sealed class HostStrictTableStreamCodec : ITableStreamContractCodec
{
    public byte[] Encode(TableStreamContractFrame frame)
    {
        var envelope = new Envelope
        {
            ProtocolMajor = ProtocolConstants.Major,
            ProtocolMinor = ProtocolConstants.Minor,
            RulesetVersion = ProtocolConstants.RulesetVersion,
            ProjectionVersion = ProtocolConstants.ProjectionVersion,
            MessageType = frame.Public != null
                ? MessageType.PublicUpdate
                : MessageType.PlayerUpdate,
            SessionId = frame.SessionId.Value,
            RoomId = frame.RoomId,
            SeatId = frame.Audience.Seat,
            BindingEpoch = frame.BindingEpoch.Value,
            Cursor = ProjectionWireMapper.MapCursor(frame.Cursor)
        };

        if (frame.Public != null)
        {
            envelope.PublicUpdate = ProjectionWireMapper.MapPublicUpdate(
                frame.Public,
                frame.Events,
                frame.FromCursor,
                frame.IsFullSnapshot);
        }
        else
        {
            envelope.PlayerUpdate = ProjectionWireMapper.MapPlayerUpdate(
                frame.Player!,
                frame.Events,
                frame.FromCursor,
                frame.IsFullSnapshot);
        }

        // 生产路径：严格校验 + 严格 Encode；绝不使用 EncodeCompatibility/反射。
        WireCodec.EnsureStrict(envelope);
        byte[] bytes = WireCodec.Encode(envelope);

        // 可选但建议：解码/再编码字节稳定校验，防止宿主装配漂移。
        WireDecodeResult decoded = WireCodec.Decode(bytes);
        if (!decoded.IsOk || decoded.Envelope == null)
        {
            throw new WireProtocolException(decoded.ErrorCode, decoded.ErrorCode);
        }

        WireCodec.EnsureStrict(decoded.Envelope);
        byte[] again = WireCodec.Encode(decoded.Envelope);
        if (!bytes.AsSpan().SequenceEqual(again))
        {
            throw new WireMalformedException("Strict encode/decode/re-encode is not byte-stable.");
        }

        return bytes;
    }
}
```

actor 装配：

```csharp
var codec = new HostStrictTableStreamCodec();
var streamOptions = new TableStreamOptions
{
    Encoder = new ContractsStreamFrameEncoder(codec),
    MaxQueuedFrames = 64,              // 现有值，未改
    MaxQueuedBytes = 4 * 1024 * 1024   // 现有值，未改
};

var actor = new TableActor(
    tableOptions,
    committedRound,
    commitPort,
    clock,
    publicationSink,
    controlCommitPort,
    projectionContextSource,           // 必须提供四席 facts / owner cheat / session / policy
    streamOptions);
```

`ITableProjectionContextSource.Capture` 必须返回同一 request 的 `TableProjectionContextData`，并填写 `EventFacts` 与 `ConcealedMeldFacts`；不能从客户端字段或可变 actor 状态补事实。生产装配必须使用 `WireCodec.Encode`，`EncodeCompatibility` 只保留给既有恶意/兼容 fixture。

---

## 4. 联合测试（12 项，全部真实 actor + 真实 Wire）

文件：`dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamWireIntegrationTests.cs`；测试 codec 为 `StrictWireStreamCodec`，真实调用 `ProjectionWireMapper`、`WireCodec.EnsureStrict/Encode/Decode`，不使用 fake encoder 冒充 Wire。

1. `real_actor_four_seats_and_two_spectators_receive_strict_wire_baselines_without_private_leaks`
   - 真实 `TableActor` 公开 API，四席 + 两个观战并发订阅；各自首 full baseline 严格解码/再编码。
   - 四席 owner ledger/mark/off-turn/window/reservation/offer 隔离；观战只有 public；私有字符串不出现在其他席字节；无 `P32A` 内部体、无 seed/fingerprint/wallOrder/stateHash 字段。
2. `commit_publishes_only_after_success_and_failure_emits_no_bytes_or_cursor`
   - 慢 commit 未返回前无新帧/游标；Release 后更新帧；commit 失败无新字节/游标，已提交状态保留。
3. `successful_commit_emits_typed_events_and_snapshot_with_strict_round_trip`
   - 真实 discard：typed `TileDiscarded` 事件、`FromCursor` 连续、快照更新、Encode/Decode/再编码字节稳定、实际 `PayloadSize`。
4. `committed_tsumo_emits_typed_settlement_event_and_snapshot`
   - 真实 Tsumo：`TsumoWin` + `RoundEnded` typed settlement 事件、快照 `SettlementEvents`、最终点数；yaku/han/fu/score 来自显式事实端口，不由 Domain 猜测。
5. `open_decision_refreshes_owner_private_and_public_clock_on_same_state_version`
   - 成功 OpenDecision 同 stateVersion refresh：owner 私有 decision/剩余时间，其他席/观战只有公开 clock；无 Domain 事件；cursor +1。
6. `control_takeover_and_reclaim_close_old_streams_and_reject_stale_bindings_and_epochs`
   - 显式 takeover -> 旧席流关闭；旧 control epoch 拒绝；旧 binding/sync 拒绝；新 binding baseline；显式 reclaim -> 新 epoch；旧流关闭；新 baseline controlVersion 前进。
7. `recovery_fences_queued_frames_rebaselines_and_sync_gates_updates`
   - 恢复 fence 丢弃旧队列；旧 completion 不污染新代次；新 generation full baseline；`NeedsSync` 期间增量被扣住；显式 Sync 重建 current full baseline 后才恢复 update。
8. `actual_encoded_bytes_enforce_frame_and_byte_limits_and_isolate_slow_sink`
   - 默认 64 帧边界下 66 次真实提交触发慢 sink `SlowConsumer`，快 sink 继续收 67 帧；`MaxQueuedBytes` 的真实编码字节超限返回 `MessageTooLarge`，不截断。
9. `missing_event_facts_fail_closed_after_commit_and_do_not_lose_committed_state`
   - 缺事件 facts：commit 成功但流 `ProjectionFailed`、无新帧；已提交 stateVersion/事件保留；修复后新订阅可重建含该提交的 baseline。
10. `closed_kan_placeholder_and_owner_identities_stay_separated_and_no_seed_fields`
    - 真实 actor baseline：公共 `ConcealedMeldView`（4/-1）无身份；owner `OwnerMelds` 四张；其他席无 owner 身份；无内部体/seed 字段；生产 `WireCodec.Encode` 对错误 owner 抛 `PublicProjectionException`。
11. `missing_window_for_live_offer_fails_closed_instead_of_fabricating_one`
    - 有 off-turn offer/reservation 但 window 被清除：订阅 fail closed，不伪造空 window/载荷。
12. `current_client_accepts_actor_wire_update`
    - 当前 `TableSession` + `InMemoryOutboxStore` 接收 actor 真实 baseline/update 字节：WELCOME/SYNC_RESPONSE Ready，update 触发 `SnapshotChanged`，cursor/state/board 与 actor 帧一致。

现有 152 项 TableRuntime 用例与 caller 安全探针全部保留并通过；未删除、未改弱、未增加 skip；未改 verifier/生产校验。

---

## 5. 真实命令、计数、exit、日志

SDK：`D:/Unity/dotnet/dotnet.exe`（8.0.426）。全部构建/测试为实际运行，非引用历史结果。

| # | 命令 | 结果 | exit |
|---:|---|---|---:|
| 1 | `dotnet build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 成功，12 项目，0 warning / 0 error | 0 |
| 2 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild --nologo -v:minimal` | 成功，0 warning / 0 error | 0 |
| 3 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal` | **164 passed / 0 failed / 0 skipped**（152 基线 + 12 联合） | 0 |
| 4 | `dotnet test ...TableRuntime.Tests... -c Release -- --filter-class "Mahjong.TableRuntime.Tests.TableStreamWireIntegrationTests"` | **12 passed / 0 failed / 0 skipped** | 0 |
| 5 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal` | **104 passed / 0 failed / 0 skipped** | 0 |
| 6 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --nologo -v:minimal` | **132 passed / 0 failed / 0 skipped** | 0 |
| 7 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal` | **322 passed / 0 failed / 0 skipped** | 0 |
| 8 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal` | **17 passed / 0 failed / 0 skipped** | 0 |
| 9 | `git diff --check` | PASS（无空白错误） | 0 |

非重复 Release 合计：`164 + 104 + 132 + 322 + 17 = 739 passed / 0 failed / 0 skipped`。TableRuntime 的 164 已包含 12 项联合用例，不重复相加。Debug 全量同树另行记录为 **164/0/0**。

原始日志目录（未提交，位于临时目录）：

`%TEMP%/p32-joint-final-20261024/`

- `solution-build-release.log` / `.exit`
- `tableruntime-tests-rebuild-release.log` / `.exit`
- `tableruntime-tests-release.log` / `.exit`
- `wire-integration-tests-release.log` / `.exit`
- `contracts-tests-release.log` / `.exit`
- `client-tests-release.log` / `.exit`
- `domain-tests-release.log` / `.exit`
- `ai-tests-release.log` / `.exit`
- 早期调试日志：`%TEMP%/p32-joint-wiretests1..7.log`、`p32-joint-tabletests-debug*.log`、`p32-joint-testbuild*.log`

框架 TestResults 仍在各测试工程 `bin/Release/net8.0/TestResults/`。

---

## 6. Domain/actor 未实现路径（逐项声明）

本任务只接线已有真实 actor command 范围，不扩大为三绝技 actor 领域执行或 P3.4：

| 路径 | 本任务行为 |
|---|---|
| `DiscardTile`、`DrawTile`、`Tsumo`、`Ron`、`Pass`、`AddedKan`、`ExhaustiveDraw`、`AbortiveDraw`、`Stacking` | Domain `RoundActionKind` 已有；真实 actor 命令可产生对应事件，测试覆盖 discard/tsumo；其余事件映射由既有 P3.2a 白名单与 facts 端口承载 |
| Chi / Pon / Riichi / ClosedKan 命令 | Domain 未建模（无对应 `RoundActionKind`）；不伪造命令，不宣称已执行；公共暗杠占位通过可信 public facts + `ConcealedMeldFacts` 的 baseline 真实接线验证，事件 body 的 closed-kan 边界由既有 P3.2c 测试与 `WithoutMeld` 接线保持 |
| 三绝技 actor 命令（P1.4） | 本任务不执行；owner cheat/sleeve/mark/window/beam 由可信 projection source 提供，真实 owner 输出已验证 |
| turn number、riichi/tsumogiri、yaku/han/fu/base score、meld 明细 | Domain 未建模/不足；显式 `TableProjectionEventFacts` 端口，缺必填 fail closed |
| P3.4 持久化/租约/检查点/冷恢复/回执 | **NOT_IMPLEMENTED**；本任务只用 actor 内存恢复 baseline 和 fake commit/control 端口 |
| 整场 session 终局 | 仍由 `TableSessionProjection` 端口提供；单局结果不推导整场终局 |

---

## 7. 状态分类

| 分类 | 项目 |
|---|---|
| **PASS** | Release solution build 0/0；TableRuntime 164/0/0；Wire 联合 12/0/0；Contracts 104/0/0；Client 132/0/0；Domain 322/0/0；AI 17/0/0；`git diff --check` |
| **FAIL** | 无生产/测试失败；无放行 verifier、无新增 skip、无空通过 |
| **BLOCKED** | 不适用（本任务本地实现可运行）；真实 PG、WSS/TCP、Unity/IL2CPP、Android/iOS、跨物理网络未由本任务提供环境 |
| **NOT_RUN** | 真实 PostgreSQL/统一 PG 入口；真实 WSS/TCP 网络；远端 CI；Unity Editor/IL2CPP；Android/iOS 真机/模拟器；跨物理网络；P3.4；G1/G5；线上 E2E |

V4 仍是唯一生产架构。G1/G5 未通过；本交付不宣称平台、真机、跨物理网络或线上验收通过。CI env 补修 `8d1bb9f` 与 receiver 的 `b473c4f` 均未读取/合入，由 caller 独立收口；本任务未运行统一 realPG，未来 caller 执行时无真实 PG 配置应如实 FAIL，不得把本文件的本地 739/0/0 写成统一入口 PASS。

---

## 8. 仍需 caller 独立复核/收口

1. 复核本 commit 的 9 个 expectedFiles 与真实 actor/Wire 路径，不把本地 commit 当作远端已推送。
2. 将本联合接线接入唯一 PR19（`feat/v5-1-integration -> v5`）的 caller 集成流程；push/PR/CI/record 由 caller 负责。
3. 统一 realPG 由 caller 执行；本任务未配置、未运行、未改 verifier。
4. P3.4、真实 WSS/TCP、Unity/IL2CPP、设备/弱网/跨物理网络、G1/G5 仍按原任务书独立验收。

---

## 9. 文件 SHA256

| 文件 | SHA256 |
|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableStreamEncodingContracts.cs` | `ABBD643D37DC871712F941B056A160126BCDD8C8D47726925B52D76177CDF4C4` |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `60934C2091A6DC4041D0401716ACD9225736733C7FEB510680E904A16231F313` |
| `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs` | `913A1822EADD236A1F1FE4A63C004566BF4ACF852B0C824F5B1C485E8D9B53B2` |
| `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs` | `1E40F29FAB43B80E921920DB1209D3FE5D8296E47DD4A02EF485E5D1BD5EE1D6` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj` | `F0D81FDE2A7F78E7E9A4C4F8C57E4B4A317B7BDFB92C9137501213ADF7239F1B` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamTestSupport.cs` | `3B6037FD70E9733EED5DE02AE6CB3B6D34F1C6AD6214F0466144AFEF19057189` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamWireIntegrationTests.cs` | `2AEAE176CC65FBEEFBABAED8503376948023CE969E15FE61201967CB03015B15` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamWireTestSupport.cs` | `4EA4A88697A31A810776E1A6226406C4A5E224D410EC3FF3A7631A47FC4378B0` |
| `doc/v5-重构/V5-P32-JOINT-WIRING.md` | 本文 SHA256 见提交后最终响应（自引用无法在文件内固定） |
