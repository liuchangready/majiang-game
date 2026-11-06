# V5 G2-C04-D05 真实四席/观战通道边界核定与下一实现合同

日期：2026-11-02（Asia/Shanghai）
任务性质：**D05 既有前置准备**，不是正式 D05，不是 C04/G2 验收，不是产品 PASS。
状态：`PREPARE_ONLY / NOT_ACCEPTED`。正式 `C04-D05` 仍为 `MAJOR/NOT_RUN`；`C04-G5-01..03` 仍为 `NOT_RUN`；G2–G5 未批准；V4 仍是唯一生产架构。

- frozenHead：`156f87e622cd929d5f69745139c6e581a183d793`
- acceptedBase：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（`origin/v5`，fresh accepted）
- 工作树：`E:/majiang-review/g2-c04-d05-channel-boundary-ds-20261102-0315`
- 分支：`wt/v5-g2-c04-d05-channel-boundary-ds-20261102-0315`
- 唯一 tracked expectedFile：`doc/v5-重构/V5-G2-C04-D05-CHANNEL-BOUNDARY-20261102.md`
- 本任务未 push、未创建/修改 PR、未 record MR、未 approve、未合并 v5、未部署、未改 scheduler/complete.flag；未跑 Unity/Player/Android/PG/整场全量测试；未获取 Unity mutex；未读取或改写任何其他 owner 的锁、台账、会话。

## 0. 结论先行

1. **权威层不是主要缺口。** `TableActor` 已具备按 `(sessionId, audience, bindingEpoch, syncEpoch)` 独立订阅、按座位/观战投影、独立基线/游标、旧 binding/epoch 拒绝和观战 public-only 帧不变量（`TableStreamContracts.cs:602-626`、`TableActor.cs:8404-8455`、`TableActor.cs:7340-7349`）。`TableSession` 已能接收 spectator WELCOME、保存 `PublicSnapshot`、按角色拒绝玩家命令（`TableSession.cs:79-81,548-571,655-659,1815-1836`）。
2. **真正的缺口集中在 LocalHost ingress/composition。** 当前每个 `LocalTableChannel` 第一次 `OpenAsync` 都创建一个独立 `LocalTableRuntime`，后者独占打开存档租约并创建一个 `TableActor`；同一存档目录第二个 channel 直接被 `<room>.lock` 的 `FileShare.None` 拒绝。`HandleHelloAsync` 固定 `_seat=0`、`TableScopeRole.Player`、账号 `local-<room>-seat0`，`BuildWelcome` 固定 `WireRole.Player`，公开 options/request/Hello 均无 role/seat 字段。探针 A/B/C/D 已实际复现这些限制。
3. **当前 driver 把 0 当人、1..3 当 AI。** fresh checkpoint 固定 `{0: LocalHuman, 1..3: Computer}`；driver 只自动完成 `Computer/Takeover` 座位。只增加“四席绑定”而不改初始控制，会让 1..3 的人类连接与 AI 自动决策竞争，形成双写/`DECISION_MISMATCH`。
4. **关闭一个 viewer 当前会关闭整桌。** `LocalTableChannel` 的 permanent close 调用 `LocalTableRuntime.DisposeAsync`，停止 driver、关闭 actor、释放 store 租约；探针 D 在关闭唯一 channel 后 `CaptureRecoveryAsync` 返回 `Unavailable`，同目录可立即重新获得租约，实证了这一点。
5. **选定最小方案只改 LocalHost 组合边界，不改 wire/actor/domain。** 新增一个 room 所有者（单 store/单 lease/单 actor）和每连接 connection runtime；role/seat/account 由 room 颁发的单次 ticket 决定，Hello 仍无 role/seat。四个玩家 + 独立 spectator 复用现有 actor stream/visibility API；关闭 viewer 只 detach 自己的 connection。默认单机 seat0+AI 保留旧路径，存档/恢复/幂等/LOCAL 语义不变。
6. **D03 交集必须等待。** 四人类座位的初始 controller 需要改 `LocalSnapshotFacts.CreateInitialCheckpoint`；Unity 多视图工厂需要改 `LocalTableChannelFactory`。两者都是 D03 当前独占，D05 不得在本任务或 D03 验收前编辑。三绝技 scope 完全不触碰。

### R1 返修映射（2026-11-02）

Caller R1 结论：**0 BLOCKER / 4 MAJOR / 0 MINOR**，全部是本前置报告的下一实施合同问题；不是正式 D05 实现要求，不以 D03/D04 未交付否决本任务。四组均在本报告一次修复；未改 production/既有测试/旧 census；未重跑同源码探针（原 18/18 证据保持，且本轮只改文字）。

| R1 ID | 修复结果 | 主要改动位置 | 验证 |
|---|---|---|---|
| D05-PREP-R1-01 | 已接受 D01 不再写成未完成依赖；N7/P2 改为“新 room 未集成/未重绑”，不是等待该组件 | §1.1、§4.8、N7、P2、D05-14、§8 | 引用 D01 接受报告 SHA；全文不再把 D01 列为未完成/等待 |
| D05-PREP-R1-02 | ticket 改为“Connect 预留、channel.Open 唯一消费、HELLO 只用已验证 claim”；新增 issued/reserved/consumed/attached/detached/failed/expired 状态表 | §4.2、§4.4、§4.5、N1/N2/N4 | 状态表覆盖正常/重复/并发/取消/HELLO 失败/transient/permanent/role switch |
| D05-PREP-R1-03 | 删除“SeatPlan.HumanSeats 每席 live 才 Tick”的静态谓词；新增 initial plan/current controller/controlEpoch/live binding/room phase/open deadline 状态动作表 | §4.4、§4.7、N5/N6/U3、D05-06/13、§8 | released→AI 可前进；spectator close 不影响；已开放 deadline 不被无限推迟 |
| D05-PREP-R1-04 | 隐私矩阵改为 owner+audience+stateVersion+current visibility；允许各席自己的私有值和合法 public discard/reveal；禁止 foreign owner 未授权秘密 | N2/N3、§6.3、D05-10 | 结构化解码+字段归属；不用裸字节/相同 scalar；未真实产生的对照仍 BLOCKED |

R1 复核原件（只读、未覆盖）：

- `E:/majiang-review/caller-d05-boundary-r1-62a7c4e9/build/caller-review/REVIEW.txt` SHA256 `63DEDED93A422259B28956D46FC037E652126E67B4C7B504CCE5FC806A921577`
- `review-manifest.sha256.json` SHA256 `69C56E4BD6C25ABEE8AD85BE8A05EACACC62038B2A544FF924D3EC444BBF9329`（92 项/0 bad）
- `contract-counterexamples.json` SHA256 `DB58B4F2AE67369F7980E2E9ACD75948AA2AAA0870A8FCDA3D863BBDF83521C6`；后 3 项为 `DOCUMENT_CONTRACT_MODEL`/`DOCUMENT_PRIVACY_MODEL`，不是未实现生产代码运行失败/通过。
- 已接受 D01：`doc/v5-重构/V5-G2-C04-RECOVERY-REQUEST-DELIVERY-20261101.md` §5（76 行起，78 行“D01 自身范围通过，D01-R1-01..04 全部关闭”），SHA256 `2FCCEC27B28CB72F54E78D4B9F8D5DDD05B6FA7521881E419FEBD18BC1F15A3C`。

---

## 1. 范围、输入与不可越界

### 1.1 已读取的冻结/现状输入

| 输入 | 用途 | 关键事实 |
|---|---|---|
| `AGENTS.md` | 架构与依赖规则 | V4 生产；七模块单向依赖；观战只消费公开投影 |
| `doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md` | G2/P4 与投影/观战设计 | §2.1.9 座位/观战隔离；F9 观战独立流/独立游标；玩家 API 拒绝观战 |
| `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md` | P4.1–P4.5、G2 | P4.4 验证要求“同局四席与观战画面对比；观战不可调用玩家命令；切换重建会话和基线” |
| `doc/v5-重构/V5-G2-C04-PLAYER-SCENARIO-READINESS-20261101.md` | D05/G5 冻结行 | D05 在第 86 行；G5-01..03 在第 150–152 行，均 `NOT_RUN` |
| `doc/v5-重构/V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json` | D05 缺陷与 G5 case | D05 `MAJOR`；G5-01..03 均 `NOT_RUN`；固定分母 8 父/33 子不扩 |
| `doc/v5-重构/V5-P4.4-SPECTATOR-SETTLEMENT-DELIVERY.md` | 已接受 P4.4 边界 | §4.2 明确使用 `PlayModeScriptedTableChannel`，未将测试通道伪造为生产通道 |
| `doc/v5-重构/V5-G2-C04-RECOVERY-REQUEST-DELIVERY-20261101.md` | 已接受 D01 组件 | §5（76 行起）明确 D01 自身范围通过、D01-R1-01..04 全部关闭；143/143 LocalHost、59/59 工具、8/8 案卷、原请求 Applied 零重复 |
| `D:/majiang/doc/v5-重构/V5-G2-CLOSEOUT-TASKS-20261031.md` | 调用方 03:15 最新节 | 唯一活动 Luna 四动作第 4 轮 reworking；三绝技人工裁决预约 1 个；PR20 头 `156f87e6`；本任务边界 |
| `doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md` | 一次性缺陷清单格式 | 每项 ID/级别/位置/复现/实际 vs 期望/证据/修复/验收断言 |

输入 SHA256：

- readiness md：`3C487762F488C686185FF2698D21BF05FAA7E61FE816664FB9B8523385B2980A`
- census JSON：`720EB795AD5680F0859AB9BD5215C39D1EE19DD18D0B33F05049C0309B75A16E`
- P4.4 已接受报告：`8DE8A2FFB67EE8731CB8079773E716083FC6C98073CF77CB867469ACD7E1D259`
- 已接受 D01 §5：`2FCCEC27B28CB72F54E78D4B9F8D5DDD05B6FA7521881E419FEBD18BC1F15A3C`
- 外部 closeout：`B40A4BFED9D6BB9FAE630F3B436A36FE34E9559E7BA8FE84A0D4351181BB8F4B`

### 1.2 台账与并发边界（本接收方）

- 调用方已完成本轮全局 recent ledger 一次 150 项 + 持久化 threads 补核 215 项；本接收方不读全局 ledger、不监控其他 owner、不派发/返修/唤醒/重试/转派任何线程。
- 唯一活动 Luna 四动作 `thread_20261101_152052_d8c3` 第 4 轮 `reworking` 的原 owner/native session/独占树保持；三绝技删除异常仍保留 1 个 DS 人工裁决预约；本任务不恢复/重复/替换其范围。
- Diagnostics `thread_20261102_011446_fe22` 已独立批准 `completed/MR synced 156f87e6`，不重开。
- 两条现行历史 failed 已有接受后继，不恢复；GLM/Anti 禁派。
- 本任务自身不调用任何 agent-mesh 工具；只使用本地 shell、只写本工作树 ignored `build/` 与唯一报告。

### 1.3 明确不做

- 不改生产代码、既有测试、旧 census/场景清单、`complete.flag`、scheduler、锁、PR、v5。
- 不编辑 D03 独占的 `unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs`、`dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs` 及其它 D03 交集；只在报告中给出未来必需改动和阻塞条件。
- 不触碰三绝技（TileMark/SleeveBloom/BeamSwap）生产/测试/人工裁决范围；探针不执行任何绝技。
- 不新增 protocol/Domain/Unity 对 Domain 的引用；不新增 `InternalsVisibleTo`；不修改 tracked csproj。
- 不把 `.NET` 组件探针、DTO fixture、P4.4 scripted channel 或历史 PlayMode 证据当作正式 C04/G5 Player 通过。
- 唯一 PR20 的当前头 CI 由调用方记录为 `IN_PROGRESS`；本任务不轮询、不重跑、不下载未完成案卷、不宣称当前头 CI 通过。

---

## 2. 真实调用图、所有权与当前限制（对应交付 1）

### 2.1 单机生产装配链

```text
Unity AppComposition.OpenLocalSessionInternalAsync
  -> ITableChannelFactory.CreateAsync(TableTransportKind.Local)
     -> LocalTableChannelFactory.CreateCoreAsync            (D03 独占，当前只读)
        -> LocalTableHost.CreateChannelAsync(options)        (LocalTableHost.cs:237-269)
           -> new LocalTableChannel(options)                  (每 channel 一个)
        -> LocalTableHost.IssueTicket(options)
  -> new TableSession(channel, outbox, options)              (AppComposition.cs:228)
  -> new TableSessionOperationQueue(session)                 (AppComposition.cs:229)
  -> new SessionCoordinator(session, queue, channel, ...)    (AppComposition.cs:230)
  -> queue.OpenAsync(new TableSessionOpenRequest(ticket,...)) (AppComposition.cs:237)
```

`LocalTableChannel.OpenAsync` 首次进入 `OpenInitialCoreAsync`；它只检查 ticket 非空，然后：

```text
LocalTableChannel.OpenInitialCoreAsync                        (LocalTableChannel.cs:190-263)
  -> LocalTableRuntime.OpenAsync(_options)                    (LocalTableChannel.cs:207)
     -> LocalTableStore.OpenAsync(options, clock, files)      (LocalTableRuntime.cs:112)
        -> LocalTableStore.OpenCoreAsync                      (LocalTableStore.cs:130-178)
           -> _files.OpenExclusive(<saveDir>/<room>.lock)     (LocalTableStore.cs:136)
              -> FileStream(..., FileShare.None)              (LocalFileSystem.cs:48-50)
     -> load/create checkpoint
     -> new TableActor(...)                                   (LocalTableRuntime.cs:119-148)
     -> new LocalTableDriver(actor, options, store, ...)      (LocalTableRuntime.cs:97)
```

`LocalTableHost.CreateChannelAsync` 本身不分配文件句柄/租约/actor；资源在 channel 第一次 `OpenAsync` 才获取。这一点是当前设计的正确部分，应在多视图方案中保留（ticket 与 channel 创建仍不获取租约；room 第一次 open 才获取一次）。

### 2.2 每个 channel 是否各自拥有 actor/store？

**是。** `LocalTableRuntime` 是单连接/单 actor/单 store 的所有者：

- `LocalTableChannel` 的字段 `_runtime` 是 `LocalTableRuntime`（`LocalTableChannel.cs:32`）。
- `LocalTableRuntime` 的字段 `_store`（`LocalTableStore`）、`_actor`（`TableActor`）、`_driver`（`LocalTableDriver`）都是实例字段（`LocalTableRuntime.cs:36-43`）。
- `LocalTableRuntime.OpenAsync` 对每个 runtime 调用一次 `LocalTableStore.OpenAsync`，`LocalTableStore` 对每个 store 打开一次独占 lock（`LocalTableStore.cs:64,136`）。
- `LocalTableRuntime.DisposeAsync` 停止 driver、关闭 session、关闭 actor、dispose store（`LocalTableRuntime.cs:2487-2508`）。

因此不存在“一个 actor 多连接”的当前路径；四个 `LocalTableHost.CreateChannelAsync` 会尝试创建四个 runtime/four store，第二个同目录必然 lease 冲突。

### 2.3 同目录二开租约如何拒绝

- lock 文件路径：`Path.Combine(saveDirectory, roomId + ".lock")`（`LocalTableStore.cs:62-64`）。
- 打开方式：`FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None`（`LocalFileSystem.cs:48-50`）。
- `IOException/UnauthorizedAccessException` 被映射为 `LocalTableException(LocalTableErrorKind.LeaseConflict, "The local table is already open by another host: ...")`（`LocalTableStore.cs:134-144`）。
- 该拒绝发生在 Hello/scope 之前：第二个 channel 连 `HandleHelloAsync` 都到不了；因此它只能证明“单租约限制”，不能证明“隐私 fail”或“隐私 pass”。

探针 C1 的原始错误（真实运行）：

```text
LeaseConflict: The local table is already open by another host:
The process cannot access the file '...\evidence\work\table-a\room-d05-channel.lock'
because it is being used by another process.
```

### 2.4 关闭 channel 是否关闭宿主？

**当前：是。** `TableSession.CloseAsync` 发送 permanent close（`TableSession.cs:448-464` 固定 `Permanent=true`）；`LocalTableChannel.CloseAsync` 在 permanent 分支进入 `PermanentCloseCoreAsync`：

- 取出 `_runtime`，`runtime.CloseSession()`；
- `await runtime.DisposeAsync()`（`LocalTableChannel.cs:562-580`）；
- `LocalTableRuntime.DisposeAsync` 停止 driver、关闭 actor、dispose store（`LocalTableRuntime.cs:2487-2508`）；
- Unity `SessionCoordinator` 在 shutdown 后还会 `DisposeChannel()`（`SessionCoordinator.cs:624-636`）。

探针 D 的实证：关闭唯一 channel 后，`LocalTableHost.CaptureRecoveryAsync` 返回 `Unavailable: No open local runtime owns the requested save directory.`，随后同目录新 channel 能重新获得租约。这证明“关闭 viewer 当前会关闭整桌”，而不是“关闭 viewer 只关闭自己的流”。

### 2.5 Hello/Welcome/seat/role 的当前硬编码

`LocalTableRuntime.HandleHelloAsync`（`LocalTableRuntime.cs:458-502`）：

```csharp
string account = "local-" + _options.RoomId + "-seat0";
...
_seat = 0;
_connectionAccount = account;
_sessionOpen = true;
scope = _store.IssueScope(account, TableScopeRole.Player, _options.RoomId, now, expires, scopeId);
_connectionScopeId = scope.ScopeId;
...
Envelope welcome = BuildWelcome(sessionId, bindingEpoch, scope, now, expires);
```

`BuildWelcome`（`LocalTableRuntime.cs:504-564`）固定：

```csharp
SeatId = _seat,
Welcome = new Welcome
{
    Role = WireRole.Player,
    SeatId = _seat,
    ConnectionEpoch = 1,
    ...
    Scope = new WireScope { Role = WireRole.Player, ... }
}
```

同时：

- `Hello` wire 只有 ticket/protocol/build/resume 字段，没有 role/seat（`mahjong.proto:155-167`）；投探针反射也证实 `Hello` 0 个 role/seat 属性。
- `TableChannelConnectRequest` 只有 `Ticket/ClientBuild/ResumeSessionId`（`ITableChannel.cs:12-36`）。
- `TableSessionOpenRequest` 只有 `Ticket/ClientBuild`（`IGameSession.cs:31-51`）。
- `LocalTableHostOptions` 只有 `SaveDirectory/RoomId/RulesetVersion/OwnerInstanceId/LeaseDuration/AutoFlushInterval/Environment`（`LocalTableHost.cs:37-131`；探针 A 的公开属性枚举结果）。

因此每个成功连接都是 `Player/0`；公开 API 没有任何入口请求 viewer/seat。

### 2.6 当前 driver 将 1..3 视为 AI 的影响

初始 controller 在 `LocalSnapshotFacts.CreateInitialCheckpoint` 固定为：

```csharp
{ 0, SeatControllerKind.LocalHuman },
{ 1, SeatControllerKind.Computer },
{ 2, SeatControllerKind.Computer },
{ 3, SeatControllerKind.Computer }
```

（`LocalSnapshotFacts.cs:107-114`）

`LocalTableDriver.DriveOneAsync` 读取 control 快照，把 `Computer/Takeover` 记为 `_aiSeats[seat]`（`LocalTableDriver.cs:169-183`），并只对 `_aiSeats[seat]` 自动完成决策：

- 摸牌/岭上摸牌：只有 AI 座位自动 draw（`LocalTableDriver.cs:184-200`）；
- CheatWindow/Discard：只有 AI 座位自动 CompleteAiDecision（`LocalTableDriver.cs:217-245`）；
- ResponseWindow/ChankanWindow：只有 AI 座位自动完成响应（`LocalTableDriver.cs:250-301`）。

影响：

- 若只给 1/2/3 发 player ticket，但初始 controller 仍是 `Computer`，driver 会继续为这些座位自动决策；人类连接提交同一 decision 时，actor 串行裁决，可能 AI 先提交，产生 `DECISION_MISMATCH` 或人类动作被 AI 抢先。**“绑定”本身不会改变 control kind。**
- 若把 1/2/3 改成 `LocalHuman`，但某个座位没有连接，当前 driver 仍可能为它 `OpenDecisionAsync`；必须按 §4.7 的 `RoomPhase/CanOpenDecision` 约束：`AwaitingSeats` 不打开，`Playing` 下无 live binding 时不新开 decision；不能简单用静态 `HumanSeats` 停整桌。
- driver 为 1..3 预发的 scope 使用 `local-<room>-seatN`（`LocalTableRuntime.cs:83-95`），这是 AI 提交用的服务端 scope；人类连接不应复用这些 scope 冒充 AI。四席实现必须让人类命令只走自己的 server-issued scope，AI 命令只由 driver 在 `Computer/Takeover` 座位提交。
- 当前 `TickAsync` 是桌级超时执行器；如果某个 `LocalHuman` 座位断线但仍 tick，已开放的 decision 会按 deadline 执行（`TableActor.cs:2534-2753` 的 tick/timeout 路径）。多视图方案必须区分“已开放 deadline”（Playing 下照常执行）与“新 decision”（无 live binding 不打开）；显式 `ReleaseControl` 后 current controller 变 AI，AI 可按 epoch 前进；不能靠关闭一个 viewer 来隐式产生 AI 动作，也不能用静态 `HumanSeats` 无连接条件把整桌 Tick/AI 永久停住（§4.7）。

### 2.7 已存在、可直接复用的权威/客户端能力

| 能力 | 现有符号 | 结论 |
|---|---|---|
| 按 audience 订阅 | `TableStreamAudience.ForSeat(int)` / `.Spectator`（`TableStreamContracts.cs:23-64`） | 复用 |
| 独立 session/binding/sync 身份 | `TableStreamIdentity(sessionId, audience, bindingEpoch, syncEpoch)`（`TableStreamContracts.cs:102-148`） | 复用；四席/观战各自身份 |
| 独立游标/基线 | `TableStreamRegistry` 的 `TableStreamIdentityState.Cursor/ProjectionSeq/HighestBindingEpoch/HighestSyncEpoch`（`TableStreamRegistry.cs:24-41`） | 复用 |
| 旧 binding/epoch 拒绝 | `TableActor.cs:7365-7448`；`StaleStreamVersion/AlreadyBound` | 复用 |
| 观战 public-only 帧 | `TableActor.BuildStreamFrame` spectator 分支（`TableActor.cs:8404-8455`）；`TableStreamFrame` 构造断言（`TableStreamContracts.cs:602-626`） | 复用 |
| 观战禁止 seat controller epoch | `TableActor.cs:7340-7349` | 复用 |
| 每桌最大 stream 数 | `TableStreamOptions.MaxStreams = 64`（`TableStreamContracts.cs:716-730`） | 4 玩家 + 多个 spectator 足够；仍需显式 spectator 上限 |
| 可见性投影 | `TableVisibilityProjection.ProjectPublicSnapshot/ProjectPlayerSnapshot/ProjectOwnerPrivate`（`TableVisibilityProjection.cs:390,745,778`） | 复用；不在 Unity/Domain 重写 |
| 客户端 public 视图 | `TableSession.PublicSnapshot`（`TableSession.cs:81`）、`SyncTarget.PublicStream` 校验（`TableSession.cs:655-659`）、`Public` baseline 分支 | 复用；无需新增 SpectatorSession 类型 |
| 客户端角色准入 | `TableSession.EnsureCommandAdmitted`/`IsCommandAdmittedForRole`（`TableSession.cs:356-359,1815-1836`） | 复用；观战本地先拒绝，host 仍需 defense-in-depth |
| 单 store 租约/scope | `LocalTableStore` + `TableScopeRole.Player/Spectator/Lobby`（`TableDurableContracts.cs:134-222`） | 复用；不要新增第二套 lease |

### 2.8 客户端与 Unity 现状

- .NET `Mahjong.Client` **没有** `SpectatorSession` 类型；`TableSession` 同时承载 `PlayerSnapshot`/`PublicSnapshot` 并按 `ProtocolRole` 拒绝玩家命令。Kotlin V4 的 `SpectatorSession.kt` 不是 V5 .NET 生产入口，不能作为 V5 证据。
- P4.4 的 `SpectatorChannelPlayModeTests` 使用 `PlayModeScriptedTableChannel`，P4.4 报告 §4.2 明确“未将测试通道伪造为生产通道”。因此它只能证明 UI/角色切换组件行为，不能证明真实四席/观战通道。
- Unity `AppComposition` 每次 `OpenLocalSessionInternalAsync` 创建一个 channel、一个 `TableSession`、一个 `TableSessionOperationQueue`、一个 `SessionCoordinator`（`AppComposition.cs:207-237`）。没有“一个 room + N view”的装配。
- Unity `LocalTableChannelFactory` 当前只返回单 channel（`LocalTableChannelFactory.cs:149-172,232-256`）；match mode 也只返回当前 round 的一个 channel（`LocalTableChannelFactory.cs:174-205`），不提供同 round 多视图。

### 2.9 当前能力矩阵

| 需求 | 当前 | 证据 |
|---|---|---|
| 默认单机 seat0 Player | 可以 | 探针 A；`LocalHostSessionSmokeTests` 历史 |
| 同一存档目录第二 channel | 拒绝 `LeaseConflict` | 探针 C1 |
| 同一 room 不同存档目录 | 两个独立 actor | 探针 C2–C5（A 0→1，B 0） |
| 四玩家同一 actor | 不可以 | 2.1–2.3；无 room API |
| 独立 spectator 公共流 | 不可以 | 2.5；无 role/seat 入口；`BuildPlayerUpdate` 只映射 Player |
| 四席/观战独立 cursor | 权威层可以，LocalHost 未接线 | `TableStreamRegistry`；单连接 runtime |
| 关闭 viewer 不关闭整桌 | 不可以 | 2.4；探针 D |
| 角色切换清缓存/旧 binding | 权威层有 fence，LocalHost/Unity 未接线 | `TableActor.cs:7365-7448`；P4.4 scripted |

---

## 3. 可复现最小真实通道探针（对应交付 2）

### 3.1 探针位置与封存

- 证据根：`build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a/`
- 探针源码：`build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a/probe/Program.cs`
  SHA256 `17C276C0EED8E47C64D024C8F983AA92B6FF18A4F1EC8466227D54D698A7BC45`
- project：`.../probe/D05ChannelProbe.csproj` SHA256 `485B65AA559ECAA676251E21D6BD9BBA6F70ABAEEBDE104806F1412EED47BABA`
- props：`.../probe/Directory.Build.props` SHA256 `DDFD9D704A64375256A98D9BEC0E81995E378051A39A3BF686C463B26BB81748`
- runner：`.../run.ps1` SHA256 `E71419A3C2FFAB8B39759CCC1D7753CC65ECCCAD1BD5498B05358A9972E8D155`
- 构建/运行日志、native metadata、XML、帧、manifest 都在同一证据根；`nuget/` 仅是本机既有包的复制，manifest 排除。

探针 **只引用现有项目**（`Mahjong.LocalHost`、`Mahjong.Client`、`Mahjong.Contracts`、`Mahjong.Contracts.Wire`），使用 `LocalTableHost.CreateChannelAsync`、`ITableChannel`、`TableSession`、`WireCodec` 公开 API；没有修改 tracked csproj、没有 `InternalsVisibleTo`、没有 private reflection 改写状态、没有手写 Welcome/快照/event、没有 Unity/Domain 引用。最终探针不使用任何私有反射；只对公开类型做属性/方法枚举，用于证明 API surface 缺少 role/seat。

### 3.2 构建与运行命令

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a/run.ps1 `
  -EvidenceRoot build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a `
  -TimeoutSeconds 150
```

runner 内部：

```text
dotnet build .../probe/D05ChannelProbe.csproj -c Release --nologo -v:m
D:\Unity\dotnet\dotnet.exe .../out/Release/net8.0/D05ChannelProbe.dll .../evidence
```

构建结果：**0 warnings / 0 errors**（`build.log` SHA256 `D452EBA92BFFECCD4D77BC7F439CB9E6B960F64191E3C32B8CBA5ED8EC91E3E7`）。
运行结果：**native exit 0、18/18 checks passed、0 failed**（`native-exit.txt`、`run.stdout.log`、`evidence/probe-results.xml`）。
TEMP/TMP/NuGet/CLI home/输出全部在 E 盘证据根；未写 D 盘；未占 Unity mutex。runner 使用 150 秒 watchdog；超时只 `Kill` 本探针子进程，不触碰其他进程；探针在 `finally` 中关闭全部 `TableSession`/channel，进程退出时释放 room/actor/store。

### 3.3 四个 case 的实际结果

| case | 目的 | 结果 | 关键观测 |
|---|---|---|---|
| A | 默认 seat0 Player 路径 | PASS | `Ready`；WELCOME `role=Player, seat=0`；scope account `local-room-d05-channel-seat0`；`PlayerSnapshot` 13 张暗牌；`PublicSnapshot=null` |
| B | 公开 API 不能请求 spectator/其他席位 | PASS | options/connect/request/Hello 均无 role/seat；第二个 fresh 目录仍 `Player/0`；.NET 无 `SpectatorSession`；raw HELLO 可让 fresh host 回显 A 的 session id |
| C | 同目录 lease vs 同 room 不同 actor | PASS | C1 第二 channel `LeaseConflict`；C2 第一 session 仍 Ready；C3 A 0→1 Applied；C4 B 仍 0；C5 session id 不同 |
| D | 关闭唯一 channel 是否关闭宿主 | PASS | `sessionState=Closed`；`CaptureRecoveryAsync` → `Unavailable`；同目录新 channel 重新获取租约 |

#### A 的原始 WELCOME（解码摘要）

```text
messageType=Welcome
sessionId=d05-123456789abcdef0-7
roomId=room-d05-channel
role=Player
seatId=0
bindingEpoch=1
ownerEpoch=1
scopeAccount=local-room-d05-channel-seat0
scopeRole=Player
baselineStateVersion=0
```

#### C 的原始证据

```text
sameDirectorySecondOpen.errorKind=LeaseConflict
sameDirectorySecondOpen.message=The local table is already open by another host:
  The process cannot access the file '...\table-a\room-d05-channel.lock'
  because it is being used by another process.
firstSessionStateAfter=Ready
twoDirectoriesSameRoom.beforeA.playerStateVersion=0
twoDirectoriesSameRoom.afterA.playerStateVersion=1
twoDirectoriesSameRoom.afterA.playerEventSeq=2
twoDirectoriesSameRoom.receiptStatus=Applied
twoDirectoriesSameRoom.afterB.playerStateVersion=0
A session=d05-123456789abcdef0-7
B session=d05-0fedcba987654321-7
```

#### D 的原始证据

```text
sessionState=Closed
captureUnavailable=true
captureDetail=Unavailable: No open local runtime owns the requested save directory.
leaseReacquiredByNewChannel=true
```

### 3.4 原始帧（关键序列）

`evidence/frames/index.json` SHA256 `15F3694945C2E87CBD4DAC9D272453F813308CC08F133BB23346051F4F5DA729`。

| # | channel | dir | type | bytes | SHA256 | 备注 |
|---:|---|---|---|---:|---|---|
| 1 | A | send | Hello | 94 | `3EA8175F3EAB4AFD9833587211643D10DFAD3CED8C8908079D16536F7E095B32` | 原始 SHA；存档为 ticket 脱敏副本 |
| 2 | A | recv | Welcome | 254 | `9CB8EFFEFF810B45DA22743C8EAE7BFE6FBBA16000DD93493A0C2273CEAF130E` | 原始字节 |
| 3 | A | send | SyncRequest | 94 | `7DB5746B8FAD3843EBD9E7DF597125EC746BEA853F041F56CAF1B91726538336` | |
| 4 | A | recv | SyncResponse | 530 | `A63D723A84DAB8F09B748D5C0E602F7484402570AF0B1B199E2A7768D212198E` | Player baseline |
| 11 | A | send | Command | 304 | `0FB0E59988AAF59538FD8302FB363F19082BAF36FAF32ADC7FB8DC6A18290F69` | 真实 discard |
| 12 | A | recv | PlayerUpdate | 369 | `BB6D860C135CB894E5253446077B5E1C6542FF38DD6F4C4F2C85C2B05FE040BA` | stateVersion 1 |
| 13 | A | recv | Receipt | 240 | `766AF2404D21603259B39E2B3247393DFC466B4262C380D9E8C1D18FEA4413BE` | Applied |
| 9 | R | send | Hello | 129 | `BB43995431A3CA73F08C86CB8EF3AD0CB32D99713506F499C327BAE966968A73` | raw resume，ticket 脱敏 |
| 10 | R | recv | Welcome | 254 | `A83C8F38A62E3EDA3DACBF86E7927DD587F7D00631EA5440F4FC088FDB9A7CBD` | 回显 A 的 session id |

Hello 帧的原始字节包含一次性 ticket，因此存档为 `*.redacted.bin`，只保留原始 SHA；Welcome/快照/事件/回执帧是原始字节。ticket 已消费/30 秒过期，不是可重用凭证；报告不打印 ticket 值。

### 3.5 探针明确证明了什么

- 默认公开路径确实走 `LocalTableChannel` → `LocalTableRuntime` → `TableSession`，并得到 `Player/0`、真实 13 张暗牌、真实 serialized Welcome/SyncResponse/PlayerUpdate/Receipt。
- 公开的 options/connect request/session open/Hello 都没有 role/seat 字段；每次成功 open 都是 `Player/0`。
- 同一存档目录第二 channel 在 Hello 之前被 lease 拒绝；第一 session 不受影响。
- 同一 room id、不同存档目录是两个 actor：A 真实 discard 后 `stateVersion 0→1`，B 仍 0，session id 不同。**B 的 cache version 不变本身只是 session 观察，单独不足以证明 actor 隔离**；充分源码依据是每个 channel 在 `LocalTableChannel.cs:207` 创建独立 `LocalTableRuntime`，后者在 `LocalTableRuntime.cs:112` 独立 `LocalTableStore.OpenAsync`，并在 `LocalTableRuntime.cs:119-148` 各自创建 `TableActor`；不同目录因此是不同 runtime/store/actor。
- 关闭唯一 channel 会释放 runtime/lease，关闭后没有 open runtime；同目录可重新打开。
- raw HELLO 可以在 fresh host 上回显另一个 host 的 session id，说明 session id 没有 host-owned 校验。

### 3.6 探针明确未证明什么

- **没有证明 spectator 通道**：公开 API 无 spectator 入口，探针无法创建 spectator；没有 public-stream 隐私扫描、没有 spectator 命令拒绝、没有 spectator cursor。
- **没有证明四席同 actor**：第二个同目录 channel 被 lease 拒绝；不同目录是两个 actor。
- **没有证明角色切换/缓存清理/旧 binding 拒绝**：没有生产 role switch 路径；P4.4 scripted channel 不算。
- **没有证明 Unity PlayMode/Windows Player**；没有跑 Unity/Player/Android/PG/整场/随机 AI 循环；没有执行三绝技。
- **没有证明 C04-D05/G5-01..03/C04/G2 通过**；以上全部只是前置现状证据。

### 3.7 证据封存

- `manifest.json`：66 个条目，覆盖探针源码、props/csproj、runner、build/run logs、native metadata、XML、summary、frames、work 存档；**不包含 manifest 自身**，不包含 `nuget/tmp/out/obj`。
- manifest SHA256：`BBC5A68853A3CE5DC881187BE992482080AED4796338AB2DD6FB3420F1561B19`
- `manifest.sha256`：`7D47F9D7E66FE25EB0A4C07C85846C198CDB0EA2134F742DCA96601EC884D0F9`（文件内容为 manifest hash + 文件名）
- `summary.json` SHA256：`63F7E7DDC50E251721E820A5646B39E358AEB777F749CF888AFA4C433E7D4175`
- `probe-results.xml` SHA256：`EBE025C130073BAAE8AD6413CC8F925A825BA01265149A84449D634D169BFD65`
- `probe.log` SHA256：`2414509104BF65AD91D540C29C4DC59C7F203615FA0685BCDF7B7D21B1EC449E`
- runner 先前的自身缺陷（manifest 锁住外层日志、PS5.1 exit code 读空）已在最终运行前修正；最终封存只以本次 exit 0/18/18 为准，不把中间运行当产品结论。

---

## 4. 选定最小可行方案（对应交付 3）

### 4.1 方案选择

**方案 A（选定）：单 room 核心 + 每连接 connection runtime，wire 不变。**

- 一个 `LocalTableRoom` 拥有唯一 `LocalTableStore`（唯一 lease）、唯一 `TableActor`、唯一 `LocalTableDriver`、唯一 recovery generation。
- 每个 viewer（4 player + N spectator）通过 room 颁发的单次 ticket 建一个 `ITableChannel`；channel 绑定一个 connection runtime，只拥有自己的 session/binding/sync/scope/cursor/outbound queue/stream handle。
- `LocalTableRuntime` 从“表所有者”改为“连接所有者”，复用现有 Hello/Sync/Command/Receipt/PlayerUpdate 逻辑；room 核心负责 store/actor/driver/lease。
- 默认单机继续走旧 `LocalTableHost.CreateChannelAsync`（内部仍可复用 room 核心，但语义是“一个 seat0 viewer 拥有整桌”），保证 `seat0 + AI`、存档、恢复、幂等、LOCAL 行为不变。
- 不修改 `mahjong.proto`、`TableActor`、`TableStream*`、`TableSession`、Domain、Unity 程序集依赖。
- 纯本地验证边界：ticket 是进程内 opaque handle，不是线上账户/JWT/签名服务/硬件凭证；本方案不创建线上账户、匹配、钱包或设备前提，也不声称线上安全。

**方案 B（拒绝）：一个 `ITableChannel` 内多路复用多个 session。** 会破坏 `ITableChannel` 的“一帧一 envelope/单连接”语义，需要重设计协议边界；并且 `TableSession` 的 binding/outbox 不能共享。

**方案 C（拒绝）：每个 viewer 一个独立 runtime/store，靠共享 actor 静态单例。** 会绕过 lease/owner epoch/recovery generation，产生多 owner 写；不可恢复。

**方案 D（拒绝）：让 Unity 直接读 Domain 投影冒充 Player。** 用户明确禁止；且无法证明序列化/权限/通道。

### 4.2 公开 C#9 / netstandard2.1 签名草案

以下为**草案**，未编译、未实现；只给出最小公开面。所有类型放在 `Mahjong.LocalHost`，不新增 wire/Domain 类型。

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;

namespace Mahjong.LocalHost
{
    public enum LocalViewerRole : byte
    {
        Player = 0,
        Spectator = 1
    }

    public enum LocalSeatDisconnectPolicy : byte
    {
        // D05 默认：不因断线新开 decision；已开放 deadline 照常；显式 release 后 AI 可前进。
        HoldForReconnect = 0,
        // 未来显式策略：宽限后 takeover；D05 不实现隐式版本。
        GraceThenAi = 1
    }

    public sealed class LocalTableSeatPlan
    {
        public static LocalTableSeatPlan SingleHumanSeatZero { get; }
        public static LocalTableSeatPlan AllFourHumanSeats { get; }
        public static LocalTableSeatPlan FromHumanSeats(IReadOnlyList<int> humanSeats);

        public IReadOnlyList<int> HumanSeats { get; }
    }

    public sealed class LocalTableHostOptions
    {
        // 既有属性保持不变：SaveDirectory/RoomId/RulesetVersion/OwnerInstanceId/
        // LeaseDuration/AutoFlushInterval/Environment。
        public LocalTableSeatPlan SeatPlan { get; set; } = LocalTableSeatPlan.SingleHumanSeatZero;
        public int MaxSpectators { get; set; } = 4;
        public bool AllowSpectators { get; set; } = true;
        public LocalSeatDisconnectPolicy DisconnectedSeatPolicy { get; set; } =
            LocalSeatDisconnectPolicy.HoldForReconnect;
    }

    public sealed class LocalTableConnectionTicket
    {
        // 不透明、单次、30 秒过期；不写日志/不写 outbox。
        public string Value { get; }
        public LocalViewerRole Role { get; }
        public int SeatId { get; }        // spectator = -1
        public string AccountId { get; }
    }

    public sealed class LocalTableRoom : IAsyncDisposable
    {
        public string RoomId { get; }
        public string SaveDirectory { get; }
        public LocalTableSeatPlan SeatPlan { get; }

        public static ValueTask<LocalTableRoom> OpenAsync(
            LocalTableHostOptions options,
            CancellationToken cancellationToken = default);

        // 只允许 SeatPlan.HumanSeats 中的 seat；0..3；重复/非人类座位抛 InvalidOptions。
        public LocalTableConnectionTicket CreatePlayerTicket(int seatId);

        // AllowSpectators=false 或达到 MaxSpectators 时抛 InvalidOptions。
        public LocalTableConnectionTicket CreateSpectatorTicket();

        // 验证并预留 ticket，返回尚未打开的 channel；实际消费在 channel.OpenAsync 的
        // 唯一线性化点（reserved -> attached），ConnectAsync 不消费。
        public ValueTask<ITableChannel> ConnectAsync(
            LocalTableConnectionTicket ticket,
            CancellationToken cancellationToken = default);

        // 关闭整桌：取消所有连接、停止 driver、关闭 actor、释放 lease。
        public ValueTask DisposeAsync();
    }
}
```

兼容入口（不破坏现有调用）：

```csharp
public static class LocalTableHost
{
    // 既有签名/语义保持：CreateChannelAsync + IssueTicket 的单机 seat0 路径。
    public static ValueTask<ITableChannel> CreateChannelAsync(
        LocalTableHostOptions options,
        CancellationToken cancellationToken = default);

    public static string IssueTicket(LocalTableHostOptions options);

    // 新入口：四席/观战的唯一生产组合。
    public static ValueTask<LocalTableRoom> OpenRoomAsync(
        LocalTableHostOptions options,
        CancellationToken cancellationToken = default);
}
```

说明：

- 不新增 `ITableChannel` 方法；`OpenAsync/SendAsync/ReceiveAsync/CloseAsync` 原样。
- `LocalTableConnectionTicket.Value` 只用于 `TableSessionOpenRequest.Ticket`/`TableChannelConnectRequest.Ticket`；wire Hello 仍只有 ticket，没有 role/seat。
- `SeatPlan` 只影响 fresh checkpoint 的 controller 初始化和 room 允许发放的 player ticket；不改变存档 schema。
- `MaxSpectators`/`AllowSpectators` 是本地资源上限，不是协议字段。

### 4.3 生命周期与 ownership

| 对象 | 所有者 | 创建/释放 | 关键规则 |
|---|---|---|---|
| `LocalTableRoom` | App composition（Unity/测试） | `OpenRoomAsync` 获取一次 lease；`DisposeAsync` 释放 | 唯一 store/actor/driver/lease；不因单个 viewer close 而 dispose |
| `LocalTableStore` | Room | Room open/dispose | 仍是 `<room>.lock` `FileShare.None`；同目录第二 room 仍 `LeaseConflict` |
| `TableActor` | Room | Room open/recovery；Room dispose close | 四席/观战 stream 由 actor 注册；room 不直接改 actor 状态 |
| `LocalTableDriver` | Room | Room open start；Room dispose stop | room 控制 `RoomPhase/CanTickAuthority/CanOpenDecision`；human seat 不自动完成 |
| `LocalTableRuntime`（connection runtime） | 一个 `LocalTableChannel` | `ConnectAsync` 只预留；首次 `channel.OpenAsync` attach；channel permanent close detach | 只拥有 session/binding/sync/scope/cursor/outbound/stream handle |
| `TableSession` | App composition（每 viewer 一个） | `OpenAsync`；`CloseAsync` | 关闭 viewer 不关闭 room；role switch 建新 session |
| `SessionCoordinator` | App composition（每 viewer 一个） | 绑定一个 session/channel | 关闭/切换只影响本 view；不共享 cache |
| `TableStreamHandle` | connection runtime | `SubscribeStreamAsync`；detach/Unsubscribe | 旧 handle 不能 unsub 新 binding；actor fence |

### 4.4 数据流

1. **Room open**：`LocalTableRoom.OpenAsync(options)` 校验 `SeatPlan`、`MaxSpectators`、存档目录；获取一次 `<room>.lock`；加载/创建 checkpoint；创建唯一 actor/driver；不创建任何 viewer。
2. **Ticket issued**：App 为 4 个 human seat 调 `CreatePlayerTicket(seat)`，为 spectator 调 `CreateSpectatorTicket()`。ticket 记录 `(role, seat, accountId, roomInstanceId, expiresAt, state)`，用 `RandomNumberGenerator` 生成 32 字节 opaque value；只在内存；单次；不写日志/outbox。此时状态 `Issued`，**尚未消费**。
3. **Connect reserve**：`LocalTableRoom.ConnectAsync(ticket)` 只做原子 `Issued -> Reserved`：校验 role/seat/account/expiry、seat 唯一、spectator 上限；返回一个尚未打开的 channel。它**不**消费 ticket，也**不**创建 session；重复/并发用同一 ticket 的第二个 `ConnectAsync` 在 `Reserved` 检查处拒绝（`AlreadyBound`/`Unauthenticated`）。`Reserved` 会占用该 seat 或 spectator 名额。
4. **Channel open = 唯一消费点**：`TableSession.OpenAsync` 调 `channel.OpenAsync(request)`；`LocalTableChannel.OpenInitialCoreAsync` 在 room actor 之外做 `Reserved -> Attached` 的线性化，**恰好一次**消费 ticket 并创建 connection runtime。此点之前取消/失败：`Reserved -> Failed` 并释放 seat/spectator 预约；此点之后失败：`Attached -> Detached`，同样释放预约；ticket 永不复用。成功 attach 后 connection 持有 `Claim`（role/seat/account/ticketHash）；sessionId 在 HELLO 成功时由 host 生成/恢复并写入 connection。
5. **HELLO uses claim**：client `TableSession` 发送 Hello（只有 ticket）。connection runtime 校验 `hello.Ticket` 与已 Attached 的 `Claim` 常量时间一致；使用 **Claim** 的 role/seat/account，不调用全局 `TryConsume`、不再消费 ticket、不使用 client 字段（wire 也没有）。若 HELLO 失败，连接进入 `Failed`（不再 Ready），调用方必须关闭；不得回退 legacy seat0。`BuildWelcome` 用 Claim；scope 由 `LocalTableStore.IssueScope(account, role, roomId, ...)` 颁发（spectator 用 `TableScopeRole.Spectator`）。
6. **SYNC**：`TableStreamIdentity(sessionId, audience, bindingEpoch, syncEpoch)`；Player 用 `TableStreamAudience.ForSeat(seat)`，spectator 用 `TableStreamAudience.Spectator`。`SubscribeStreamAsync` 在 actor 单 turn 内注册并排入第一份 full baseline；`TableStreamSubscribeRequest.OwnerEpoch = store.LeaseEpoch`，`RecoveryGeneration = actor.RecoveryGeneration`。
7. **更新**：`OnFrame` 按 `frame.Identity.Audience`：
   - seat：`contract.Player` → `MapPlayerSnapshot/MapPlayerUpdate`，`MessageType.PlayerUpdate`；
   - spectator：`contract.Public` → `MapPublicSnapshot/MapPublicUpdate`，`SyncTarget.PublicStream/MessageType.PublicUpdate`。
   每连接自己的 `_outbound`/`_streamGeneration`/`_sink`；慢 consumer 不影响其他连接。
8. **命令**：`HandleCommandAsync` 使用 connection 的 `_seat/_connectionScopeId/_connectionAccount`；先校验 envelope scope 与当前 connection scope 一致；spectator 直接拒绝（见 4.6）。玩家命令仍走现有 `TableCommandEnvelope`/`TableSettlementRequest`/cheat/settlement 分支；控制命令仍走 `HandleControlCommandAsync`。
9. **Detach**：channel permanent close → `room.DetachAsync(connection)`：取消/等待本连接 outbound、`UnsubscribeStreamAsync(handle)`、标记 session closed、`Attached -> Detached`、释放该 seat/spectator 预约；**不**停止 driver、不关闭 actor、不 dispose store。ticket 已消费且不可复用；重连/切换必须新 ticket。room 只更新 readiness/current controller 派生的 per-seat drive 资格，不用静态 `SeatPlan` live 谓词停整桌（见 4.7）。
10. **Transient reconnect**：同一个 `LocalTableChannel` 的 transient close/reopen 复用已 Attached 的 connection/Claim；`LocalTableChannel.ReopenCoreAsync` 调 `runtime.ReconnectAsync`，**不**重新消费 ticket、不创建新 connection。`TableSession` 若再次发 HELLO，仍用同一 Claim 校验 `hello.Ticket`/`ResumeSessionId`，不查全局 used；新 ticket 或不同 session 不能在 transient 通道上 rebind。
11. **Role switch**：旧 channel permanent close（`Detached`）→ 新 ticket（新 role/seat）→ `Connect` 预留 → `channel.Open` 消费 → 新 session/binding/sync/baseline。旧 Claim 不复活、旧 ticket 不复用；旧 session 不再 pump；旧 channel 的 `_streamGeneration` fence 旧帧；新 `TableSession` 的 `_binding` 从 null 建立，旧 cache 不存在。
12. **Room close**：`LocalTableRoom.DisposeAsync` 关闭所有 connections、停止 driver、关闭 actor、释放 lease；之后同目录才可重新 open。

### 4.5 ticket/session/scope/seat 的权威规则

- **ticket 是唯一绑定来源**：`LocalTableRoom` 的 registry 记录 role/seat/account；`HandleHello` 不接受 client 的 role/seat（wire 没有），也不接受 client 替换 ticket。ticket 生命周期只允许以下状态，**唯一消费点**是 `channel.OpenAsync` 的 `Reserved -> Attached` 线性化；`ConnectAsync` 只做 `Issued -> Reserved`。

| 状态 | 进入动作 | 允许的下一状态 | seat/spectator 预约 | 说明 |
|---|---|---|---|---|
| `Issued` | `CreatePlayerTicket`/`CreateSpectatorTicket` | `Reserved` / `Expired` | 不占 | 未验证、未预留；TTL 过期即 `Expired` |
| `Reserved` | `LocalTableRoom.ConnectAsync` 原子 CAS | `Attached` / `Failed` / `Expired` | **占用** seat 或 spectator 名额 | 同一 ticket 第二个 Connect/并发 Connect 拒绝；取消/超时释放 |
| `Attached`（= `Consumed`） | `channel.OpenAsync` 唯一线性化 | `Detached` / `Failed` | 占用 | ticket 恰好消费一次；connection 持有 `Claim`；HELLO 只验证 Claim |
| `Detached` | channel permanent close / role switch | 终态（新 ticket 才能再连） | 释放 | 旧 ticket/Claim 不复活；transient reopen 不经过此状态 |
| `Failed` | Open 失败/取消、HELLO 失败/协议拒绝 | 终态（新 ticket） | 释放 | 不从 room ticket 回退 legacy seat0；不泄漏名额 |
| `Expired` | TTL 到期且尚未 Attached | 终态（新 ticket） | 释放 | Attached 后的会话不因 ticket TTL 被中途踢出 |

- **线性化/并发**：`Reserved` 用单次 CAS；同一 ticket 只允许一个 channel 成功。`Attached` 后 ticket 不再查全局 `TryConsume`；重复 HELLO 只验证 Claim。`ConnectAsync` 取消、`OpenAsync` 取消、HELLO 失败都必须走 `Detached`/`Failed` 释放预约；不允许 seat/spectator 名额泄漏。
- **transient reconnect**：保持 `Attached`，同一 connection/Claim，`runtime.ReconnectAsync`；不重新消费 ticket、不创建新 connection。`TableSession` 重新发 HELLO 时仍用 Claim 校验同一 ticket/`ResumeSessionId`；新 ticket 不能在旧 transient channel 上 rebind。
- **permanent detach / role switch**：`Detached`；旧 ticket 已消费且不可复用；新连接必须新 ticket。role switch 不复用旧 session/binding/cursor。
- **legacy 兼容**：旧 `CreateChannelAsync` + `IssueTicket` 路径在 channel 构造时标记 legacy 来源，使用 static `LocalTicketIssuer` 并回退 seat0/Player；room ticket 路径永远不走 legacy fallback，room ticket 验证失败也不得降级为 seat0。
- **seat 唯一**：同一 room 同一 seat 同时最多一个 player binding；重复 `CreatePlayerTicket` 或 attach 失败，不踢掉旧 binding（要顶号必须显式 close/claim）。
- **spectator 独立**：每个 spectator 一个 sessionId/bindingEpoch/syncEpoch/scope/cursor；`TableStreamAudience.Spectator` 的 identity key 含 `Kind=Spectator, Seat=-1`；不同 spectator 不共享 cursor。
- **session resume**：只允许 `hello.ResumeSessionId == 当前 connection 已签发 sessionId`；fresh host/其他 host 的 session id 一律重新签发或拒绝（修复探针 B5）。当前 `HandleHelloAsync` 直接采纳 client 的 resume id（`LocalTableRuntime.cs:485-490`），必须改。
- **scope**：Player scope 只对 Player binding 的 command 有效；Spectator scope 只用于读流，不颁发可写 command 权限。`ValidateCommandScopeAsync` 现在的 `scope.Role != TableScopeRole.Player` 检查（`LocalTableRuntime.cs:1688-1690`）保留，并加 role admission。
- **account**：Player 用稳定 `local-<room>-seat<N>`（与现有 seat0 一致），Spectator 用 `local-<room>-spectator-<n>`；重启后 account 不变，outbox 旧 scope 的 receipt query 仍能按 account 读取（`HandleReceiptQueryAsync` 已按 account + supplied scope 查询，不要求 scope == 当前 connection scope）。
- **不持久化 ticket/session secret**：ticket/registry 只在内存；存档 schema 不变；不把 ticket 写入 outbox/log。

### 4.6 观战无命令/无越权

- Client 已按 `ProtocolRole.Spectator` 拒绝 seat action（`TableSession.cs:1815-1836`）。
- Host defense-in-depth：在 `HandleCommandAsync` 最前、任何 store/actor 调用之前，若 connection role == Spectator，直接返回 `ProtocolErrorCode.Unauthenticated`（现有 code，不新增 wire code）或 `ProtocolViolation`，detail `SPECTATOR_READ_ONLY`；不写 receipt、不写 outbox、不触碰 ledger/entities/events/version。
- Spectator 也不能提交 `ClaimControl/ReleaseControl`、settlement、cheat、Ready/action；`ValidateCommandScopeAsync` 的 Player-only 检查继续作为第二道门。
- Spectator 只读 `PublicSnapshot/PublicUpdate`；不订阅 `PlayerSnapshot`，不调用 `ProjectPlayerSnapshot`。

### 4.7 人类席位、AI driver 与 control epoch

- fresh room 的 `SeatPlan.HumanSeats` 决定 initial controllers：human → `LocalHuman`，其余 → `Computer`；默认 `[0]` 与现状一致。
- `LocalTableDriver` 只对 `Computer/Takeover` 自动完成；`LocalHuman` 即使有 pending decision 也不自动提交。
- **绑定不改变 control kind**。Player 绑定到 `Computer/Takeover` 座位时，host 拒绝 seat action（`STALE_CONTROL_EPOCH`/现有 `CONTROL_NOT_HELD` 语义），直到显式 `ClaimControl`；`ClaimControl` 增加 epoch，旧 AI decision 被 actor fence。
- **关闭 viewer 不隐式释放 control**。若产品要 AI 接管，调用方必须显式发送 `ReleaseControl`/`LeaveTable`；`GraceThenAi` 是未来显式策略，D05 不实现静默版本。仅关闭 spectator 不改变任何 seat control。
- **三个正交维度（不得混用）**：
  1. `SeatPlan.HumanSeats` 只决定 **fresh checkpoint 的 initial controllers** 和允许发放的 player ticket；它不是运行期 `Tick` 谓词，也不随 `ReleaseControl` 删除座位。
  2. `currentController/controlEpoch` 来自 actor control snapshot（`LocalHuman`/`Computer`/`Takeover`）；运行期只以当前 controller 决定谁可行动、谁可 AI。
  3. `live binding` 只表示连接可用；**绑定不等于控制权**。spectator 没有 seat 控制权。
- **RoomPhase**：`AwaitingSeats` → `Playing`。`AwaitingSeats` 持续到每个初始 human seat 要么有 live player binding，要么当前 controller 已不是 `LocalHuman`（显式 release 或 recovered AI）；此阶段不打开 decision、不 `Tick`、不产生 authority deadline，spectator binding 不计入。进入 `Playing` 后，单个 viewer 断线**不**回退到 `AwaitingSeats`。
- **CanTickAuthority**：room open 且 `RoomPhase == Playing`。`TickAsync` 在 `Playing` 始终运行，**任何已经开放的 decision deadline 都按现行权威规则执行，不因连接/动画无限推迟**；断线不暂停、不延长 deadline。
- **CanOpenDecision(seat)**：
  - `currentController == Computer/Takeover`：可以；由 driver/AI 打开并完成。
  - `currentController == LocalHuman && live binding`：可以；打开后等待人类，driver 不自动完成。
  - `currentController == LocalHuman && no live binding`：**不打开新 decision**；若断线前已有 open decision，它保留，`TickAsync` 仍按 deadline 处理。返回的旧 binding 若 control/decision 已变化，命令按 `ControlEpoch`/decision 拒绝。
- **CanCompleteAi(seat)**：仅 `Computer/Takeover`；`LocalHuman` 永不自动完成。
- **显式 ReleaseControl / ClaimControl**：`ReleaseControl` 使 current controller → `Computer/Takeover` 并递增 `controlEpoch`；该座位不再阻塞 `AwaitingSeats`/`Playing`，也不再被 `SeatPlan.HumanSeats` 条件卡住，AI 可按 epoch fence 前进。`ClaimControl` 使 current controller → `LocalHuman` 并递增 epoch；旧 AI decision 被 fence。两者都不改 `SeatPlan` 本身。
- **recovered controller 与 SeatPlan 不同**：以 recovered controller 为准；若 recovered 是 `Computer/Takeover`，按 AI 路径；若是 `LocalHuman`，需要 binding 或显式 release。不得用 `SeatPlan` 覆盖持久化控制状态。
- **spectator close**：不影响 `RoomPhase`、`CanTickAuthority`、任何 seat 的 `CanOpenDecision`/`CanCompleteAi`。
- **动画/UI**：不得 gate `TickAsync`；`presentationBusy` 之类的 UI 节奏不能推迟已开放 deadline。产品若要额外暂停，只能是未来显式配置，不能作为本默认方案。

**状态动作表（D05 默认；均为报告模型，不是运行时实现证据）**

| 场景 | current controller | live player binding | open decision | CanTickAuthority | 打开新 decision | AI 完成 | deadline/动作 |
|---|---|---|---|---|---|---|---|
| 开局前未齐 | LocalHuman | 否 | 无 | 否（AwaitingSeats） | 否 | 否 | 无 deadline；等待 binding/release |
| 开局前已齐 | LocalHuman | 是 | 无 | 否→是（全部 human 齐/release 后转 Playing） | 转 Playing 后可 | 否 | 无 deadline，直至 Playing |
| 已开局，人类在线 | LocalHuman | 是 | 无/有 | 是 | 是 | 否 | 人类提交；open 后按 deadline |
| 已开局，人类断线且无 open decision | LocalHuman | 否 | 无 | 是（为其他 seat/deadline） | 否（本 seat） | 否 | 本 seat 等待；不新开 deadline |
| 已开局，人类断线但有 open decision | LocalHuman | 否 | 有 | 是 | 否（本 seat） | 否 | deadline 照常执行/timeout；旧 binding 回来按 stale 拒绝 |
| 显式 release 后无 binding | Takeover/Computer | 否 | 任意 | 是 | 是（AI） | 是 | AI 按 epoch 前进；不被 SeatPlan 卡住 |
| 旧 human binding 仍在但已 release | Takeover/Computer | 是（旧） | 任意 | 是 | 是（AI） | 是 | 旧 binding 命令按 stale epoch 拒绝；AI 不被阻断 |
| spectator close | 任意 | 不影响 | 任意 | 不变 | 不变 | 不变 | 不影响玩家/整桌 |
| recovered controller 与 SeatPlan 不同 | recovered | 按上面行 | 按上面行 | 按 Playing/Awaiting | 按 current controller | 按 current controller | 不覆盖 recovered |
| 已开放 deadline + 任一 viewer close | 任意 | 可能变化 | 有 | 是 | 按 seat | 按 controller | deadline 不延长、不受 UI 动画影响 |

- `ControlEpoch` 与 `BindingEpoch` 分离：binding 只用于连接/流身份，不能当出牌授权；命令携带的 `ControlEpoch` 由 actor 校验。角色切换/控制变化后重建 session/baseline，不复用旧 cursor/decision cache。

### 4.8 默认单机、恢复、幂等、LOCAL 兼容

- **默认单机**：`LocalTableHost.CreateChannelAsync` + `IssueTicket` 旧路径保持；seat0 `LocalHuman`、1..3 `Computer`、`AutoPlay=true` 行为不变；不改存档格式/room id/owner/lease 规则。
- **四席**：使用新 `LocalTableRoom` 路径；`SeatPlan.AllFourHumanSeats`；四个 ticket/四个 `TableSession`/四个 `SessionCoordinator`；同一 `LocalTableStore`/`TableActor`/`RoundId`。
- **恢复**：`LocalTableRoom.OpenAsync` 复用 `LocalTableStore` 的 cold recovery；recovered controllers 来自 checkpoint，不因 `SeatPlan` 覆盖持久化控制状态（若要改变，必须显式 claim/release）。
- **幂等/原请求**：不改变 `TableSession.SubmitAsync/ReconcilePendingAsync/RetryPendingAsync` 与 outbox 语义；account 稳定，旧 scope 的 receipt query 仍可读。**C04-D01 组件已接受**：`V5-G2-C04-RECOVERY-REQUEST-DELIVERY-20261101.md` §5（76 行起）明确 D01 自身范围通过、D01-R1-01..04 全部关闭（143/143 LocalHost、59/59 工具、8/8 案卷、原 request Query Applied / 零重复）。未来新 room 的恢复/幂等仍是 `NOT_RUN`，原因是 **new room 尚未集成、最终同版本证据尚未重绑**，不是等待 D01 组件。新 room 必须沿 accepted 原 request/account/canonical bytes/scope 合同做 query/reconcile 和零重复副作用；**不得自动把旧 scope 改成新 scope，也不得把任意旧 scope 重发当成已接受**。自然 Unknown / 显式 retry 的边界沿用 D01 已接受范围，不因本报告扩大。
- **LOCAL**：仍由 LocalHost 生成 `origin=LOCAL` 语义，不生成线上 token/wallet/排位；不改变 `LocalTableStore`/`LocalSave`/`LocalMatchCoordinator` 的 LOCAL 边界。
- **存档**：一个 room 一个 store；四个 viewer 不各自写存档；room close 才 flush/dispose；`AutoFlushInterval` 仍由 store 负责。

### 4.9 为什么这是最小边界

- 不改 `mahjong.proto`：`Hello` 无 role/seat，`Welcome` 已有 role/seat，`SyncResponse.Target` 已有 `PublicStream`，`PlayerUpdate/PublicUpdate` 已分离，`PublicSnapshot` 已存在。
- 不改 `TableActor`/`TableStream*`：多 stream、audience、cursor、binding fence、spectator public-only 都已存在。
- 不改 `TableSession`：spectator WELCOME/PublicSnapshot/role admission 已存在。
- 只改 LocalHost 的组合/所有权和 Unity 装配；默认旧路径不动，风险局限在 opt-in 四席/观战路径。

---

## 5. 最小文件清单（对应交付 4）

### 5.1 必要生产改动（均为已存在、已核路径）

| 路径 | 原因/符号 | 类型 |
|---|---|---|
| `dotnet/src/Mahjong.LocalHost/LocalTableHost.cs` | 新增 `LocalTableRoom`/`LocalTableConnectionTicket`/`LocalViewerRole`/`LocalTableSeatPlan`；每 room ticket registry；`LocalTableHostOptions` 新属性校验；保留旧 `CreateChannelAsync/IssueTicket` | 必要生产 |
| `dotnet/src/Mahjong.LocalHost/LocalTableChannel.cs` | channel 从“拥有 runtime”改为“拥有 connection”；`OpenInitialCoreAsync` attach room；`PermanentCloseCoreAsync` detach 而非 `runtime.DisposeAsync`；旧单机路径的 owner 语义保留 | 必要生产 |
| `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` | 拆 room core 与 connection runtime；`HandleHelloAsync` 用 ticket 记录而非 seat0/Player；`BuildWelcome` 用 binding；`BuildSyncResponse/BuildPlayerUpdate` 按 audience 映射 Player/Public；`HandleCommandAsync` 用 connection seat/scope；spectator 命令拒绝；`DisposeAsync` 只 detach | 必要生产（最大改动） |
| `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs` | `RoomPhase/CanTickAuthority` + per-seat `CanOpenDecision/CanCompleteAi` gate；human seat 不自动完成；绑定不改变 control；AI scope 只用于 AI 座位；不因单个 viewer close 停止整桌 | 必要生产 |
| `unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs` | 一个 room + N view 的装配；每 view 独立 `TableSession/OperationQueue/SessionCoordinator`；room dispose 只在退出整桌 | 必要 Unity 集成 |
| `unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs` | 确认每 view 独立 coordinator/generation；role switch 新建 coordinator；close 只关本 view | 可能无需改动；若改动，限定生命周期，不改 UI 语义 |

### 5.2 必要测试（优先扩展现有文件，避免新增同义夹具）

| 路径 | 原因 |
|---|---|
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostSessionSmokeTests.cs` | 增加 4 player + 1 spectator 的 real room/ticket/channel/TableSession smoke；同一 actor/round/stateVersion |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCommandTests.cs` | 增加 spectator 命令拒绝零副作用、独立 cursor、关闭 viewer 不关闭整桌 |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs` | 增加 room 恢复后 seat/account/old-scope receipt query 兼容 |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs` | 增加 human/AI control epoch：人类绑定不改变 control、claim/release、stale AI decision 拒绝 |
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs` | 生产 room 装配的 PlayMode 四席/观战/role switch/close viewer（在 room API 与 D03 交集完成后） |

明确**不**把 `unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs` 的 scripted channel 当生产证据；它是已接受的组件参考，不是本任务证明。

### 5.3 后续 Unity/Player 验证（不属于本任务）

| 路径/命令 | 原因 | 状态 |
|---|---|---|
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs` | 多视图 ticket/room 工厂 | **D03 独占，等待** |
| `dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs` | 四人类 initial controllers | **D03 独占，等待** |
| `tools/unity/run-p45-c04-privacy-matrix.ps1` | bounded stream capture/sentinel scan/evidence manifest | **PROPOSED_NEW，未创建** |
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs` | role switch/cache/stale binding | **PROPOSED_NEW，未创建** |
| `dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs` | 四席/观战 raw scan | **PROPOSED_NEW，未创建** |
| Windows final Player | 四席/观战 Player 证据 | **NOT_RUN/BLOCKED**，最终 C03 候选未绑定 |

### 5.4 D03/D04 交集与禁止项

- D03 当前独占 `LocalTableChannelFactory.cs`、`LocalSnapshotFacts.cs` 以及 full-match Player 驱动/runner；本任务只读。
- 未来必需交集：
  - `LocalSnapshotFacts.CreateInitialCheckpoint` 的 controllers 改为读取 `SeatPlan`；这是四人类座位的前提；必须等 D03 验收后串行集成，不能在本任务编辑。
  - `LocalTableChannelFactory` 增加 room/ticket 工厂；同样等 D03。
  - 如果 D03 的 Player 动作驱动也需要四席视图，必须另开范围评审，不从 D05 越界。
- 三绝技（TileMark/SleeveBloom/BeamSwap）的生产、测试、人工裁决范围完全不碰；`LocalSnapshotFacts` 中的 `CheatEconomy/InitialCheatState` 不改。
- 不扩 G3/B2 服务、不重设计协议、不把 Domain 投影直接作为 Player 证明。

---

## 6. 冻结的可执行验收矩阵（对应交付 5）

所有行在实现/D03 交集完成前均为 `PROPOSED/NOT_RUN`；不得用 DTO fixture 替代真实通道，不得用 scripted channel 代替生产装配。

### 6.1 .NET 真实通道层

| ID | 场景 | 命令/入口 | 必断言 | 状态 |
|---|---|---|---|---|
| N1 四席同 actor | room + 4 player tickets + 4 `TableSession` | 真实 `LocalTableRoom/ITableChannel/TableSession`；`SaveDirectory` 同一目录 | 四个 Ready；`Welcome.Role=Player`；seats `{0,1,2,3}` 唯一；同 `RoundId`；一条真实 discard 提交后四席在**同一 stateVersion** 收到 push；每个 seat 的 `PlayerSnapshot.PrivateView.Seat` 只等于自己；四连接 session/binding/cursor 独立 | NOT_RUN |
| N2 独立 spectator 公共流 | room + spectator ticket + `TableSession` | 同一 room，真实 spectator scope | Ready、`Role=Spectator`、`SeatId=-1`；`PublicSnapshot != null`、`PlayerSnapshot == null`；`SyncResponse.Target=PublicStream`；后续 `PublicUpdate` 可解析；public cursor 独立；**只允许 public 白名单**（`hand_count`/discards/public melds/`wall_remaining`/dora/last_discard/public settlement），不得出现 `private_view`/`PrivateCheatView`/per-owner `cheat_points`/`ledger_entries`/sleeve identities/完整 ordered wall/seed | NOT_RUN |
| N3 受保护值 raw scan | N1+N2 的帧 | 捕获 `ITableChannel.ReceiveAsync` 原始 bytes；真实 `WireCodec.Decode` + typed message/字段归属；逐 owner/audience/stateVersion | 每席自己的已授权私有字段**非空正对照**（自己的闭牌/袖牌/私有费用可见）；禁止 foreign owner 的未授权秘密；合法 public discard/reveal（如 owner0 的 tile33 在 v1 合法弃牌）允许出现在 public，但 v0 hidden 时不得出现；见 6.3 | NOT_RUN |
| N4 role switch + 旧 binding | player0 → spectator → player1 → player0；每步新 session | 真实 room/ticket/channel | 每步新 sessionId/bindingEpoch/scope/baseline；旧 PlayerSnapshot/private cache 不在新 session；延迟旧帧注入被 `sessionId/audience/bindingEpoch` 拒绝；新基线正确；无旧私有值残留 | NOT_RUN |
| N5 独立 cursor + close viewer | 4 player + 2 spectator；只推进一个 viewer；关闭一个 spectator/一个 player | 真实 room | 其他 viewer 不阻塞；关闭 spectator 整桌继续；关闭 player 不关闭整桌；关闭后新 viewer 可 attach；关闭的 player 按 §4.7 状态表：不新开 decision，已开放 deadline 照常，显式 release 后 AI 可前进；各 cursor 独立 | NOT_RUN |
| N6 默认 AI/控制回归 | 默认 `SeatPlan.SingleHumanSeatZero`；四人类；release/claim；recovered controller | 旧 `CreateChannelAsync` + 新 room | seat0 人类可行动；1..3 AI 仍自动摸打/响应；四人类 room 中 LocalHuman 不自动提交；released→Computer/Takeover 无 binding 仍可 AI 前进；recovered controller 与 SeatPlan 不同时按 recovered；claim/release epoch 后 stale AI decision 被拒 | NOT_RUN |
| N7 恢复/幂等兼容 | room 保存→关闭→重开；accepted D01 原 request/account/bytes/scope | 同一 room/seat/account；`ReconcilePendingAsync`/receipt query | account/seat 稳定；按 accepted D01 合同查询原 request Applied，零重复副作用；同 request replay 返回 existing；不自动重写 scope/不重提交；自然 Unknown/显式旧 scope retry 不冒充 accepted；**new room 未集成/未同版本重绑，故 NOT_RUN** | NOT_RUN（new room + 最终同版本证据，非 D01 依赖） |
| N8 lease/actor 负例 | 同目录第二 room；同 room 不同目录 | 真实 `LocalTableRoom.OpenAsync` | 同目录第二 room `LeaseConflict`，第一 room 不受影响；不同目录两个 actor，A 命令不改 B version；room close 后 lease 释放 | NOT_RUN |

N5/N6 必须逐行覆盖 §4.7 状态动作表：connected human、disconnected human（无/有 open decision）、released→AI 无 binding、旧 human binding 仍在但已 release、spectator close、recovered controller 与 SeatPlan 不同、`AwaitingSeats` 未齐、`Playing` 已开放 deadline。任何显式 release/AI 行不得被旧 `SeatPlan.HumanSeats` 无连接条件永久阻断；已开放 deadline 不得因 viewer close/动画无限推迟。

### 6.2 Unity PlayMode / Windows Player

| ID | 层 | 场景 | 必断言 | 状态 |
|---|---|---|---|---|
| U1 | UNITY_PLAYMODE | 生产 room 装配四席 + spectator | 四个 `SessionCoordinator`/view 独立；真实 `ITableChannel`（非 scripted）；四席同 stateVersion；spectator 无私有字段；关闭一个 view 不关整桌 | NOT_RUN；等 room API + D03 |
| U2 | UNITY_PLAYMODE | role switch/cache/stale binding | player→spectator→player；旧 cache 清空；旧帧丢弃；新基线正确；私有账本/袖牌不跨角色 | NOT_RUN |
| U3 | UNITY_PLAYMODE | 控制/AI/关闭 | 默认 AI 回归；人类座位不双写；claim/release；关闭 spectator 不影响；关闭 player 按 §4.7：不新开 decision、已开放 deadline 照常、显式 release 后 AI 可前进 | NOT_RUN |
| P1 | WINDOWS_PLAYER | 最终 C03 候选四席/观战 | 实际 Player 证据绑定 source/build/package/player identity；四席/观战 raw/结构化证据；不出现受保护值；关闭 viewer 不关整桌 | NOT_RUN/BLOCKED（最终候选未绑定） |
| P2 | WINDOWS_PLAYER | 恢复/幂等 | 真实 Player 存档/恢复；按 accepted D01 原 request/account/bytes/scope 对账；无重复副作用 | NOT_RUN/BLOCKED（最终候选 + new room 同版本证据；D01 组件已接受，不是依赖） |

### 6.3 受保护值与“独立非空正对照”

| 类别 | 归属/受众 | 真实非空正对照 | 允许的 public/reveal | 禁止/负例 |
|---|---|---|---|---|
| 暗牌 | 每个 player owner 自己的 `private_view.seat` 必须等于绑定 seat | 每个 seat 自己的 `closed_tiles`（13/14 张）非空；其他 seat 也可以有自己的非空 closed_tiles（不是“其他 seat 全空”） | 该 owner 合法弃牌/副露/立直后在当前 stateVersion 的 public discard/meld；spectator 只允许 public 白名单（含 `hand_count`/discards/public melds/dora/scores 等） | 该 owner 的 `closed_tiles` 在**未授权/未揭示**的当前 stateVersion 出现在 foreign seat 或 spectator；不能用“曾经是某 owner 暗牌”对 tile ID 做永久全局禁止 |
| 袖牌 | 每个 owner 自己的 sleeve/offer | 真实 SleeveBloom 成功后该 owner 的 `private_view.sleeve_tiles`/offer 非空；其他 owner 也可以有自己的非空 sleeve | 只允许 public 的合法副露/揭示；sleeve 身份不属于 public | 该 owner 的 sleeve identities/reservation 出现在 foreign seat 或 spectator；不能用“其他 seat 的 sleeve 字段必须为空”误伤其他 owner 自己的合法 sleeve |
| 随机材料 | authority 的 `ShuffleRandomTrace.BaseWall`/`Doubles` | 真实 trace 非空且由测试持有；V5 没有 seed 字段 | `PublicBoardView.wall_remaining`、dora indicators、last_discard 等公开字段 | 任何 player/foreign/spectator payload 含完整 136 张 ordered wall/`Doubles`/seed；proto `seed` 仅 reserved。未来新增 seed 字段必须重绑本行 |
| 私有费用 | 每个 owner 自己的 `PrivateCheatView` | 真实 TileMark/SleeveBloom/BeamSwap 成功后该 owner 的 `cheat_points`/`ledger_entries` 非空，金额/余额为实际服务端值；其他 owner 也可以有自己的非空 ledger | public settlement summary 只允许公开得分/排名；授权揭示按该版本 public 白名单 | 该 owner 的 `cheat_points`/`ledger_entries` 出现在 foreign seat/spectator；**两席余额 scalar 相等不构成泄漏证据**；receipt/event 不得携带未授权 private fee |
| 已合法公开的实体 | 当前 stateVersion + 当前可见性 | 例：owner0 在 v0 的 hidden tile 33，在 v1 合法弃牌后，public `TileDiscarded`/`PublicBoard` 可含 `tile_id=33` | 该实体在当前版本的 public discard/meld/reveal；在它成为 public 的 stateVersion 之后允许 | v0 的 foreign hidden 状态不得显示 tile33；v1 显示是合法 public，不得因 v0 历史而拒绝；归属/版本必须是判定依据，不是 tile ID 黑名单 |

判定方法（未来 N2/N3 必须执行）：

1. 在 `ITableChannel.ReceiveAsync` 边界捕获原始 bytes；用真实 `WireCodec.Decode` 得到 typed `Envelope`/`PlayerSnapshot`/`PublicSnapshot`/event，不用裸字节 grep 或相同 scalar 代替归属。
2. 对每个 frame 记录 `sessionId/audience/seat/bindingEpoch/stateVersion/eventSeq`；按 **owner + audience + 当前 stateVersion + 当前可见性** 检查字段路径。
3. 正例：四席各自的私有字段非空、可读；合法 public discard/reveal 在对应 stateVersion 可读。
4. 负例：把 seat1 的真实 `PlayerSnapshot`（含 seat1 自己的私有值）投递到 seat0 的 session/decoder，必须因 session/seat/audience 不匹配被丢弃；若未被丢弃，typed 字段归属检查必须报 foreign owner 泄漏。
5. 真实无法产生的 sleeve/随机材料/费用对照仍标 `BLOCKED`；不得用本轮模型或 DTO fixture 冒充正式 C04/G5 通过。
6. role switch（player↔spectator、seat 重绑）后必须按**新 sessionId/audience/seat/bindingEpoch/stateVersion** 重跑同一矩阵；旧 owner 的私有事实不得因旧 cache 或旧 binding 进入新 view。

说明：上表的“正对照”必须来自真实 authority/真实命令；其他 seat 自己的私有数据不是本 audience 的泄漏对象，只有 **foreign owner 的未授权事实** 才是负例。public 白名单字段（wall_remaining/dora/last_discard/public players/settlement）不能被“全字段为空”误杀。

### 6.4 G5 状态与正式边界

- `C04-G5-01` 四玩家看自己手牌、不看对家私有：`NOT_RUN`。
- `C04-G5-02` 观战 public stream 无隐藏/私有值：`NOT_RUN`。
- `C04-G5-03` role switch 清 cache、旧 binding 不能发布：`NOT_RUN`。
- 正式 `C04-D05` 仍 `MAJOR/NOT_RUN`；本报告不改变 census、不改变 G2 决定、不把任何行改为 PASS。
- 正式最终候选重绑、Player/Unity 验证待实现/集成后另行执行；本任务不生成最终候选 SHA。

---

## 7. 一次性缺陷/风险清单（对应交付 6）

以下为**本轮全部已知问题**；没有把已复现问题留到下一轮。静态问题与运行失败分开标注。

### D05-01 单 channel 单 runtime，无法同 actor 四席/观战

- 级别：**BLOCKER**
- 位置：`dotnet/src/Mahjong.LocalHost/LocalTableHost.cs:237-269`（每 channel new）；`LocalTableChannel.cs:190-263`（每 channel `LocalTableRuntime.OpenAsync`）；`LocalTableRuntime.cs:36-43,106-148`（store/actor/driver 实例字段）
- 复现：探针 C1（同目录第二 channel）与 C2（同 room 不同目录）；命令见 §3.2。
- 实际：同目录第二 channel `LeaseConflict`；不同目录两个 actor（A 0→1，B 0）；不存在同 actor 多连接。
- 期望：一个 room 一个 store/lease/actor，4 player + N spectator 各自 connection。
- 证据：`build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a/evidence/summary.json` SHA `63F7E7DDC50E251721E820A5646B39E358AEB777F749CF888AFA4C433E7D4175`；`manifest.json` SHA `BBC5A68853A3CE5DC881187BE992482080AED4796338AB2DD6FB3420F1561B19`。
- 最小修复：新增 `LocalTableRoom` 核心（单 store/actor/driver），channel 只 attach connection；room close 才 dispose（见 §4.3/4.4）。
- 验收断言：N1/N2/N5/N8；同目录第二 room 仍 `LeaseConflict`，同 room 4 player+spectator Ready 且同 stateVersion。

### D05-02 Hello/Welcome 固定 seat0/Player/账号

- 级别：**BLOCKER**
- 位置：`LocalTableRuntime.cs:475`（account seat0）、`:491`（`_seat=0`）、`:494`（`TableScopeRole.Player`）、`:527-528`（`WireRole.Player`、`SeatId=_seat`）
- 复现：探针 A/B；每次成功 open 的 Welcome/scope 都是 Player/0；公开 API 无 role/seat。
- 实际：所有连接 Player/0；无法得到 seat1..3 或 spectator。
- 期望：role/seat/account 来自 room 颁发的单次 ticket；wire Hello 仍无 role/seat。
- 证据：`evidence/summary.json`（A/B observations）；源码 SHA `C0CD17AE...`、`74E4FCB6...`。
- 最小修复：ticket registry + `HandleHello` 使用 ticket 记录；`BuildWelcome` 用 binding；scope role 用 player/spectator。
- 验收断言：N1/N2；四个 seat 唯一，spectator `-1`；伪造/重用 ticket 拒绝；Hello 0 role/seat 字段。

### D05-03 spectator 路径未映射（LocalHost 仍按 Player 取 `contract.Player`）

- 级别：**BLOCKER**
- 位置：`LocalTableRuntime.cs:719-745`（`BuildSyncResponse` 固定 `SyncTarget.PlayerStream` + `contract.Player!`）；`:748-769`（`BuildPlayerUpdate` 固定 `MessageType.PlayerUpdate` + `contract.Player!`）；`:582`（固定 `TableStreamAudience.ForSeat(_seat)`）
- 复现：静态检查 + 权威层 spectator 分支对照 `TableActor.cs:8404-8455`；当前无公开 spectator 入口，故不能运行到该分支。
- 实际：即使 actor 产生 spectator frame，LocalHost 映射会取 null `contract.Player` / 发 PlayerUpdate；spectator 无法工作。
- 期望：按 `contract.Audience` 映射 `PublicSnapshot/PublicUpdate`，`SyncTarget.PublicStream`。
- 证据：源码 SHA `74E4FCB6EBCDD0BC30E5790834BB6786778CA801A932B4B8A9109D0C122F0C11`；`TableStreamContracts.cs:602-626`。
- 最小修复：audience switch（§4.4 第 6 步）；不新增 wire 类型。
- 验收断言：N2；spectator baseline/update 可解析；public payload 无私有字段。

### D05-04 关闭一个 viewer 关闭整桌/释放租约

- 级别：**BLOCKER**
- 位置：`LocalTableChannel.cs:562-580`（permanent close → `runtime.DisposeAsync`）；`LocalTableRuntime.cs:2487-2508`（stop driver/close actor/dispose store）；`SessionCoordinator.cs:624-636`（dispose channel）
- 复现：探针 D；关闭唯一 channel 后 `CaptureRecoveryAsync` → `Unavailable`，同目录可重新租约。
- 实际：关闭 viewer = 关闭整桌；其他 view 不存在。
- 期望：channel close 只 detach 自己的 connection；room 显式 `DisposeAsync` 才关闭整桌。
- 证据：`evidence/summary.json` D observations；`native-exit.txt`。
- 最小修复：connection-scoped dispose；room owner 引用；`LocalTableChannel.CloseAsync` 只 detach（§4.3）。
- 验收断言：N5；关闭 spectator 整桌继续；关闭 player 不关闭其他 player；room close 后 lease 释放。

### D05-05 四人类 initial controllers 硬编码（seat0 human，1..3 computer）

- 级别：**MAJOR**
- 位置：`dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs:107-114`；D03 当前独占。
- 复现：默认打开 room，读 control snapshot；seat1..3 为 `Computer`。
- 实际：四席绑定后 1..3 仍 AI；driver 自动完成。
- 期望：`SeatPlan.HumanSeats` 决定 initial controllers；默认 `[0]` 不变。
- 证据：源码 SHA `C4A237EF510F6F05B5D8ABF2F0539444DE95AD201DDB3BE3A7E56D77442E6382`；closeout 03:15 节。
- 最小修复：D03 验收后，在 `CreateInitialCheckpoint` 读取 seat plan；不得在本任务编辑。
- 验收断言：N6；四人类 room 中 1..3 不自动提交；默认 `[0]` + AI 回归。

### D05-06 绑定不改变 control，driver 与人竞争/双写

- 级别：**MAJOR**
- 位置：`LocalTableDriver.cs:169-183,217-245,250-301`；`LocalTableRuntime.cs:83-95`（AI scopes）；`LocalTableDriver.cs:621-646`（AI submit）
- 复现：默认 seat1 为 Computer；给 seat1 发 player ticket 并提交同一 decision；driver 同时自动完成；实际会出现 AI 先提交/人类 `DECISION_MISMATCH` 或反之。
- 实际（静态；本轮无四席注入路径，未实跑竞争）：绑定 seat1 后 AI 仍控制 seat1；无 host-level “human must claim” gate；竞争结果取决于 actor 串行顺序，可能在 AI 先提交后使人类收到 `DECISION_MISMATCH`，或反之。
- 期望：human binding 不改变 control；human 在 Computer/Takeover 座位提交 seat action 被拒，直到 `ClaimControl`；AI 只提交 Computer/Takeover；关闭 viewer 不隐式 release。
- 证据：源码 SHA `2795E366...`、`C4A237EF...`；默认 controller 证据。
- 最小修复：current controller/controlEpoch gate + per-seat `CanOpenDecision/CanCompleteAi` + claim/release；AI scope 只由 driver 使用（§4.7）。
- 验收断言：N6；claim/release epoch；stale AI decision 拒绝；四人类无 AI 提交。

### D05-07 客户端可断言任意 resume session id

- 级别：**MAJOR**
- 位置：`LocalTableRuntime.cs:485-490`
- 复现：探针 B5：fresh `table-resume` 收到 raw Hello `resume_requested=true, resume_session_id=<A session>`，Welcome 回显 A 的 session id。
- 实际：fresh host 接受外来 session id；session id 未绑定 host/account/seat。
- 期望：只允许恢复本 host 已签发且 account/role/seat 匹配的 session；否则新 session 或拒绝。
- 证据：`evidence/frames/0009-R-send-Hello.redacted.bin`/`0010-R-recv-Welcome.bin`；`evidence/summary.json` B5；SHA 见 §3.4。
- 最小修复：room/connection session registry；校验 `(sessionId, account, role, seat, bindingEpoch)`；不要信任 wire resume id。
- 验收断言：N4/N7；跨 host resume 拒绝或重新签发；同 host 同 identity 可 resume；旧 binding 不能借 resume 提权。

### D05-08 ticket registry 是进程全局、只按 roomId，未绑定 host/role/seat

- 级别：**MAJOR**
- 位置：`LocalTableHost.cs:441-508`（`LocalTicketIssuer` static `Dictionary`；`Issue` 只存 roomId；`TryConsume` 只比较 roomId）
- 复现：静态检查；同一进程内同 roomId 的两个不同 SaveDirectory 共享 ticket 命名空间；ticket 不含 role/seat/account。
- 实际：ticket 可跨 host/目录被消费（只要同 roomId），且无法区分 spectator/seat。
- 期望：每 room 独立 ticket registry；记录 role/seat/account/hostInstance/expiry/used；单次。
- 证据：源码 SHA `C0CD17AE1146A989703A4126DBF4333CF5BA9B6FC75EB5AC468F640B9DBA6F67`；`LocalTableRuntime.cs:467`。
- 最小修复：room 内 registry + `RandomNumberGenerator` opaque ticket；旧 `IssueTicket` 仅兼容 seat0（§4.5）。
- 验收断言：N1/N2；跨 room/seat/role ticket 拒绝；replay/expiry 拒绝；ticket 不入日志/outbox。

### D05-09 spectator 命令拒绝未显式化

- 级别：**MAJOR**
- 位置：`LocalTableRuntime.cs:1688-1690`（`scope.Role != Player` 才拒绝；当前所有 scope 都是 Player）；`HandleCommandAsync` 无 role admission
- 复现：当前无 spectator scope，无法运行；静态检查显示一旦签发 Spectator scope，所有 command 会被 `Unauthenticated` 拒绝，但没有显式 `SPECTATOR_READ_ONLY` 语义/零副作用断言。
- 实际：没有 spectator 命令路径；无法证明零副作用。
- 期望：spectator 在 host 入口被明确拒绝；任何 store/actor 调用之前返回；零 receipt/event/version/ledger/entity。
- 证据：源码 SHA `74E4FCB6...`；`ProtocolErrorCode.cs:28-43`（无新增 code）。
- 最小修复：connection role admission + `ProtocolViolation`/`Unauthenticated` + detail `SPECTATOR_READ_ONLY`；不新增 wire code（§4.6）。
- 验收断言：N2/N3；逐 CommandKind 负例；version/event/ledger/entities 零 delta。

### D05-10 没有生产四席/观战测试证据；P4.4 是 scripted channel

- 级别：**MAJOR（证据缺口，不是生产代码 bug）**
- 位置：`doc/v5-重构/V5-P4.4-SPECTATOR-SETTLEMENT-DELIVERY.md:141`；`SpectatorChannelPlayModeTests.cs`
- 复现：读 P4.4 §4.2；测试使用 `PlayModeScriptedTableChannel`。
- 实际：P4.4 只证明 UI/角色切换组件；无真实四席/观战 serialized stream。
- 期望：N1–N8/U1–U3 的真实通道证据。
- 证据：P4.4 SHA `8DE8A2FF...`；readiness D05 第 86 行；G5 第 150–152 行。
- 最小修复：实现后新增/扩展 §5.2 测试；不得用 scripted/DTO 代替。
- 验收断言：N1–N8 全绿；U1–U3 真 PlayMode；P1/P2 真 Player。

### D05-11 Unity 装配只有一个 channel/session/coordinator

- 级别：**MAJOR**
- 位置：`AppComposition.cs:207-237`；`LocalTableChannelFactory.cs:149-172,232-256`
- 复现：静态检查；`OpenLocalSessionInternalAsync` 每次创建一个 channel/session/queue/coordinator；没有 room/多 view API。
- 实际：一个 Unity 进程只能有一个本地牌桌视图。
- 期望：一个 room + 4 player coordinators + N spectator coordinators；关闭一个 view 不关整桌。
- 证据：源码 SHA `C091147B...`、`4F53DDF0...`；D03 03:15 节。
- 最小修复：room composition（§4.3/4.4）；`LocalTableChannelFactory` 改动等 D03。
- 验收断言：U1–U3；真实 `ITableChannel` 装配。

### D05-12 无 spectator 上限/慢 consumer 隔离（设计风险）

- 级别：**MAJOR**
- 位置：`TableStreamOptions.MaxStreams=64`（`TableStreamContracts.cs:723`）；`LocalTableRuntime._outbound` 单队列（`LocalTableRuntime.cs:43`）
- 复现：静态检查；当前单连接；多 spectator 方案未实现。
- 实际：若直接共享一个 outbound/队列，慢 spectator 可能阻塞或无界增长；`MaxStreams` 只是 actor 上限，不是产品 spectator 上限。
- 期望：每 connection 独立 bounded outbound；`MaxSpectators` 显式限制；慢 consumer 断开并可用 full baseline 恢复，不阻塞其他 view。
- 证据：`TableStreamRegistry.cs:103-146`（每 stream channel 有界队列）；`TableStreamOptions`。
- 最小修复：connection 级 outbound + `MaxSpectators` + overflow close/rebuild（§4.4/4.3）。
- 验收断言：N5；慢/停 spectator 不影响玩家；超限拒绝；恢复 baseline 正确。

### D05-13 断线人类 timeout 与 hold 语义（设计风险）

- 级别：**MAJOR**
- 位置：`TableActor.cs:2534-2753`（桌级 tick/timeout）；`LocalTableDriver.cs:169-183`
- 复现：静态检查；当前 `_isActive` 由单 session 控制，关闭 session 会暂停 driver；多视图下关闭一个 view 不能停整桌。
- 实际（静态）：旧报告的静态 `HumanSeats` 谓词会停整桌 Tick；若改为一律不 tick，已开放 deadline 被无限推迟；若照常 tick 但不区分“新 decision/已开放 deadline”，又可能对断线 human 新开 decision。需要 §4.7 的逐状态动作表。
- 期望：`HoldForReconnect` 下 human 无 live binding 时不**新开** decision，但已开放 deadline 由 `CanTickAuthority` 按权威规则执行；显式 `ReleaseControl` 后 current controller 变 AI，AI 可按 epoch 前进；spectator close 不影响。
- 证据：`TableActor.cs:2534-2753`；`LocalTableDriver.cs:169-183`；§4.7 状态动作表。
- 最小修复：`RoomPhase/CanTickAuthority` + per-seat `CanOpenDecision/CanCompleteAi` + current controller/epoch + explicit release；不用静态 `SeatPlan.HumanSeats` 停整桌（§4.7）。
- 验收断言：N6；断线 human 不新开 decision；已有 open deadline 按权威执行；显式 release 后 AI 接管；stale decision 拒绝。

### D05-14 新 room 恢复/旧 scope 与 accepted D01 的边界（R1-01 已修正）

- 状态：原报告把 D01 列为未完成依赖的表述已删除；D01 组件已接受。以下只是新 room 集成边界，不是 D01 缺陷，也不重开 D01。
- 位置：`LocalTableRuntime.HandleCommandAsync:780-785`（命令只接受当前 `_connectionScopeId`）；`HandleReceiptQueryAsync:1829-1845`（receipt query 允许旧 scope，按 account）；已接受 D01 报告 §5。
- 复现：静态检查；新 session 的 current scope 与 outbox 旧 scope 不同；`RetryPendingAsync` 使用旧 scope。新 room 尚未实现，N7/P2 未运行。
- 实际：D01 已接受（143/143 LocalHost、59/59 工具、8/8 案卷、原 request Query Applied / 零重复）；LocalHost 当前 command 仍只接受 current connection scope，旧 scope 仅用于 receipt query。accepted D01 不证明自然 Unknown 或任意旧 scope resend。
- 期望：account 稳定（`local-<room>-seat<N>`）；新 room 沿 accepted D01 原 request/account/canonical bytes/scope 合同做 query/reconcile 与零重复副作用；不自动重写 scope、不重提交；自然 Unknown/显式 retry 仍按 D01 已接受边界，不扩大。
- 证据：`LocalTableRuntime.cs:780-785,1829-1845`；`doc/v5-重构/V5-G2-C04-RECOVERY-REQUEST-DELIVERY-20261101.md` §5（76 行起）SHA256 `2FCCEC27B28CB72F54E78D4B9F8D5DDD05B6FA7521881E419FEBD18BC1F15A3C`。
- 最小修复：只修本报告状态/依赖；不修改 D01 代码/协议/旧 census；未来 room 实现只保证 account/seat 稳定 + connection scope 隔离，不另造 recovery 协议。
- 验收断言：N7/P2 因 **new room 未集成/未同版本重绑** 保持 `NOT_RUN`（不是等 D01）；原 request/account/bytes/scope 对账零重复；不自动改 scope/重提交；D01 不复开。

### D05-15 无 .NET `SpectatorSession` 类型（边界说明，不是缺陷）

- 级别：**MINOR**
- 位置：`dotnet/src/Mahjong.Client/IGameSession.cs`、`TableSession.cs`
- 复现：探针 B4 `TypeExists("Mahjong.Client.SpectatorSession") == false`。
- 实际：`TableSession` 同时承载 Player/Public；Kotlin V4 的 `SpectatorSession` 不是 V5 .NET 生产入口。
- 期望：不需要新增类型；用 `TableSession` + role/PublicSnapshot 即可；若产品要类型糖，做 wrapper，不改协议。
- 证据：`evidence/summary.json` B4。
- 最小修复：无；文档化边界。
- 验收断言：N2 使用 `TableSession` 即可。

### D05-16 ticket 过期清理/内存（MINOR 设计风险）

- 级别：**MINOR**
- 位置：`LocalTableHost.cs:459-508`（`Prune` 只在 Issue/Consume 调用）
- 复现：静态检查；无后台清理。
- 实际：长期只签发不消费可能积累过期 ticket；单机影响小。
- 期望：room close 清空；签发/消费时 prune；可选定时 prune。
- 证据：源码 SHA `C0CD17AE...`。
- 最小修复：room registry 生命周期内清理；不持久化。
- 验收断言：过期/已用 ticket 拒绝；room close 后 registry 清空。

---

## 8. 关键设计风险与解决依赖

| 风险 | 影响 | 解决依赖 |
|---|---|---|
| D03 独占 `LocalSnapshotFacts.cs` | 四人类 initial controllers 不能在本任务实现 | 等 D03 验收；串行集成；本任务只读 |
| D03 独占 `LocalTableChannelFactory.cs` / full-match driver | Unity 多视图工厂不能在本任务实现 | 等 D03；另开范围评审 |
| new room 恢复/幂等尚未集成与最终同版本重绑（D01 组件已接受） | N7/P2 不能提前宣称通过 | 沿 accepted D01 原 request/account/bytes/scope 合同；new room + 最终候选同版本证据；不自动改 scope/重提交 |
| 控制 epoch 与 binding epoch 混用 | 旧 AI/旧 view 可能双写 | 只用 `ControlEpoch` 授权命令；binding 只做流身份；role switch 重建 session |
| 慢 spectator | 内存/延迟 | 每 connection bounded outbound + MaxSpectators + slow consumer close/full baseline |
| 断线 human 的已开放 deadline | 误暂停/无限延长或静默 AI | §4.7 状态表：不新开 decision；已开放 deadline 按权威执行；显式 release 后 AI/epoch 前进；不用静态 `HumanSeats` 停整桌 |
| 公开流新增字段 | 未来私有泄露 | 新字段默认 private；public whitelist + sentinel scan 每次新增重绑 |
| ticket/session secret 持久化 | 凭证泄露 | 仅内存；不写 outbox/log；一次性/过期；redacted 证据 |
| 用 P4.4 scripted/旧 PlayMode 冒充生产 | 假绿 | 只接受真实 `ITableChannel` + 生产 room；G5 仍 NOT_RUN |

---

## 9. 结论、边界与调用方下一步

- 本报告给出的是**现状证据 + 最小实现合同**，不是实现，不是正式 D05/C04/G2 验收。
- 选定方案的核心是：**权威层已具备四席/观战流能力，缺的是 LocalHost 的单 room/多 connection 所有权与 Unity 多视图装配**；wire/actor/domain 不需要重设计。
- 本任务唯一 tracked 交付为本报告；探针源码/产物保留在 ignored 独占 `build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a/`。
- 未 push、未创建/修改 PR、未 record MR、未 approve、未合并 v5、未部署、未改 scheduler/complete.flag；未运行 Unity/Player/Android/PG/整场；未占 Unity mutex。
- 调用方独立审查后再决定是否派发正式 D05 实现；D03 验收前不得编辑其独占文件；三绝技范围保持原人工裁决。
- 本报告完成后等待 callback，不自行启动实施后继。

---

## 10. 证据索引与哈希

### 10.1 探针证据

| 文件 | SHA256 |
|---|---|
| `build/d05-channel-eb6f56e5a3214110b3dc3e5dda4e280a/manifest.json` | `BBC5A68853A3CE5DC881187BE992482080AED4796338AB2DD6FB3420F1561B19` |
| `.../manifest.sha256` | `7D47F9D7E66FE25EB0A4C07C85846C198CDB0EA2134F742DCA96601EC884D0F9` |
| `.../evidence/summary.json` | `63F7E7DDC50E251721E820A5646B39E358AEB777F749CF888AFA4C433E7D4175` |
| `.../evidence/probe-results.xml` | `EBE025C130073BAAE8AD6413CC8F925A825BA01265149A84449D634D169BFD65` |
| `.../evidence/frames/index.json` | `15F3694945C2E87CBD4DAC9D272453F813308CC08F133BB23346051F4F5DA729` |
| `.../evidence/probe.log` | `2414509104BF65AD91D540C29C4DC59C7F203615FA0685BCDF7B7D21B1EC449E` |
| `.../build.log` | `D452EBA92BFFECCD4D77BC7F439CB9E6B960F64191E3C32B8CBA5ED8EC91E3E7` |
| `.../native-metadata.txt` | `CACD1CA51278FF6ED2258D89F48BF0B610D8EDE578BC8964778D8599CC04F182` |
| `.../build-inputs.txt` | `8E3B0BA87283222DC7619F34A44DE76AE90DAC5E367DE45CB777723566B326B0` |
| `.../run.ps1` | `E71419A3C2FFAB8B39759CCC1D7753CC65ECCCAD1BD5498B05358A9972E8D155` |
| `.../probe/Program.cs` | `17C276C0EED8E47C64D024C8F983AA92B6FF18A4F1EC8466227D54D698A7BC45` |
| `.../probe/D05ChannelProbe.csproj` | `485B65AA559ECAA676251E21D6BD9BBA6F70ABAEEBDE104806F1412EED47BABA` |
| `.../probe/Directory.Build.props` | `DDFD9D704A64375256A98D9BEC0E81995E378051A39A3BF686C463B26BB81748` |

### 10.2 关键源码哈希（本报告引用版本）

| 文件 | SHA256 |
|---|---|
| `dotnet/src/Mahjong.LocalHost/LocalTableHost.cs` | `C0CD17AE1146A989703A4126DBF4333CF5BA9B6FC75EB5AC468F640B9DBA6F67` |
| `dotnet/src/Mahjong.LocalHost/LocalTableChannel.cs` | `EBB90FAADD070DE9DAA309BA20FD0E2AC1DA68CD0676780BFDF6D8060FE25976` |
| `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` | `74E4FCB6EBCDD0BC30E5790834BB6786778CA801A932B4B8A9109D0C122F0C11` |
| `dotnet/src/Mahjong.LocalHost/LocalTableStore.cs` | `5BE26BC2E6DAF4BFDFBB12C32AFC22C45D7B26D42EF005C51A34AC2D5E0C73B3` |
| `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs` | `2795E366861D271222E2378400392DDC4A85AB9D40864B2E0FEC7EEF8F4FAE48` |
| `dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs` | `C4A237EF510F6F05B5D8ABF2F0539444DE95AD201DDB3BE3A7E56D77442E6382` |
| `dotnet/src/Mahjong.LocalHost/LocalFileSystem.cs` | `302F396B68B3E97D91FD6C93036D7D5722A7C713D7C69C686D4893F6D79E1942` |
| `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs` | `12FD850F131E15D324AE09BD2E288B60AF7BDE1FB7B914ABD03EF20B8DC7E242` |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `5B70325C065D938B808D2E1717B60DB6473E1A7BB031FA611BD0BA2B30792943` |
| `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs` | `47680D3E95080DF99F584105AC648858E95FD6C2EA586EA5B2ADFBF10443DA90` |
| `dotnet/src/Mahjong.TableRuntime/TableDurableContracts.cs` | `AF0CB6C57326E74C42C01535688E19C6BCD95FAAFD81EE854E1FFC6A09CEFC84` |
| `dotnet/src/Mahjong.Client/TableSession.cs` | `07C23EEE1541E3EA2D879D904FAB1212A54A90B7118175A9328773588CFC1B02` |
| `dotnet/src/Mahjong.Client/ITableChannel.cs` | `3F4A04F914C8FC377033892847A970AA905382F4AB4E3DE20824138E41F5FE16` |
| `dotnet/src/Mahjong.Client/IGameSession.cs` | `6B2D2FC9B4B835E822B4F7B424F8C62D7272DF63FB515E7D5679536BF3CCB2CB` |
| `dotnet/src/Mahjong.Contracts.Wire/mahjong.proto` | `A846FC1FCDE0DAFCBFB0993367E91D40915630A2876B0272D800860CC2B13F81` |
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs` | `4F53DDF07BD7A6D1111AD0265D71AD3B1732A4EF5E0006F1D5368B52C6996F1C` |
| `unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs` | `3F7D51165E40FA75421D99F08843978B06469BDDD020F95113F2FC76D30F958D` |
| `unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs` | `C091147B67C8F85AF318AE1081DFA71D3CFF201546C71B6398060D2118E13FB0` |
| `unity/Mahjong.Unity/Assets/Scripts/Spectator/SpectatorWhitelistMapper.cs` | `ED6ADD9725F390BCBAC3C23998A9316732EB363AC0CE6C3751E3B578E224660C` |
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs` | `672E661B76FD4080A0ACD9FB20798600295D726996303A9557FDC21CED4848CD` |

### 10.3 Git 状态（报告写完后核对）

- `git rev-parse HEAD`：`156f87e622cd929d5f69745139c6e581a183d793`
- branch：`wt/v5-g2-c04-d05-channel-boundary-ds-20261102-0315`
- 报告写入前：tracked clean、`git diff --check` 0；只存在 ignored `build/` 探针产物。
- 本报告提交后：唯一 tracked 变更为本报告；`build/` 仍 ignored；不 push。

<!-- D05-REPORT-BODY-END -->

## 11. 报告哈希

- 正文哈希（文件开头至本文件唯一一个 HTML 正文结束注释行，含该行）SHA256：`8310A61AAB25DF70C70732CCDFF5B585B9EF7445467107B55036F2D603EBF53D`。
- 该哈希不覆盖本节，避免自引用；复算时截取到标记行即可。
- 最终提交后的整文件 SHA256 由提交记录标识；本任务不把未提交的中间 hash 当作验收。
## 12. Caller 独立验收（2026-11-02）

本节更新前文接收方交付时的 `NOT_ACCEPTED` 状态：**本前置报告已独立接受，D05-PREP-R1-01..04 全部关闭**。受审提交为 `86d50a82b7812416e1067023354bae9168cbd42a`，本次补记仅报告，正文冻结哈希 `8310A61AAB25DF70C70732CCDFF5B585B9EF7445467107B55036F2D603EBF53D` 所覆盖内容保持不变。

调用方在 `E:/majiang-review/caller-d05-boundary-r2-8b45c6f1` 实际运行现有文档审计，**40/40、原生退出 0**；并逐段复核 D01 已接受边界、ticket 唯一消费/重连状态机、当前控制者与已开放 deadline 状态表、按 owner/audience/版本/可见性区分私有信息与合法公开信息。11 条最小现有文件路径均存在；相对冻结生产源只有本报告，无源码或既有测试改动。

原真实通道探针 **66 项长度/哈希全部一致**，manifest SHA256 `BBC5A68853A3CE5DC881187BE992482080AED4796338AB2DD6FB3420F1561B19`。原 R1 调用方 fresh 构建/真实通道 **18/18、native 0** 的记录保持有效；本轮只有文档变化，未重跑该探针，不将其计为本轮新产品测试。

完整独立复核、实际审计及保留证据核验位于上述独立树 `build/caller-review/REVIEW.txt`、`audit.json`、`retained-evidence-audit.json`；最终清单为 `review-manifest.sha256.json`。本节在正文哈希范围外。

本接受范围为现状核定与下一实现合同；正式 D05、C04-G5-01..03 仍 `NOT_RUN`，不启动实施后继。D03 独占交集须待其验收后串行安排，三绝技人工范围不变。未运行 Unity/Player/PG/Android/设备，未审当前 publication 的远端 CI；G2–G5 未批准，V4 仍生产。仅同步既有 Draft PR20，不合并、部署或修改定时任务。