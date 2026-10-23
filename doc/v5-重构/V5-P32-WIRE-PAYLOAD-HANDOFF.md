# V5 P3.2 Wire 载荷与投影映射交付说明

日期：2026-10-24。
状态：**P3.2 Wire 载荷组件本地实现与定向验证完成；不是完整 P3.2，不是 G1/G5，不是远端 CI/真实 WSS/PG 验收通过。**
基线：`D:/majiang-wt/p32-wire`，`wt/v5-p32-wire`，`3e78367538b4f7520a1d414c82ef3155507a5206`。
范围：Contracts typed 载荷、append-only Wire 字段、`ProjectionWireMapper`、Wire strict 校验、TableRuntime 纯 `TableProjectionContractMapper` 及定向测试。

> 本组件只落实已验收 P3.2a §7 第 8 项与 §8 的真实缺口：P3.2a 的 `ProjectToContract` 只有元数据、`EncodeBody()` 不是 Wire、`TableOwnerPrivateView` 未进 Contracts。未创建 TableService、未接线 actor/mailbox/stream、未做真实 Wire 流、未重做 P3.2a/P3.3 旧线程、未合入 P3.2b 在途源码。

---

## 1. 基线与边界

| 项目 | 值 |
|---|---|
| worktree | `D:/majiang-wt/p32-wire` |
| branch | `wt/v5-p32-wire` |
| baseline HEAD | `3e78367538b4f7520a1d414c82ef3155507a5206` |
| 开始 `git status --porcelain=v1` | 空 |
| 本轮最终提交 | 见本次交付响应（本地 commit，不 push、不建 PR） |
| 禁止动作 | 未切分支、未 push、未建 PR、未触发远端 CI、未接管其他线程、未修改其他工作树 |

### 1.1 修改/新增文件

新增：

- `dotnet/src/Mahjong.Contracts/ProtocolProjectionPayload.cs`
- `dotnet/src/Mahjong.Contracts.Wire/ProjectionWireMapper.cs`
- `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs`
- `dotnet/tests/Mahjong.Contracts.Tests/ProjectionWireMapperTests.cs`
- `dotnet/tests/Mahjong.TableRuntime.Tests/TableProjectionContractMapperTests.cs`
- 本文

修改：

- `dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs`（兼容构造重载、typed event body、owner fields）
- `dotnet/src/Mahjong.Contracts.Wire/mahjong.proto`（只 append，未复用 reserved）
- `dotnet/src/Mahjong.Contracts.Wire/WireCodec.cs`（strict 路径与范围/owner/oneof 校验）
- `dotnet/tests/Mahjong.Contracts.Tests/SchemaCompatibilityTests.cs`（冻结字段表与 schema fingerprint）

未修改：

- `TableActor.cs`、`TableMailbox.cs`、`TableRuntimeContracts.cs`、`TableRuntimePorts.cs`、`TableControlContracts.cs`、`TableControlMessages.cs`、`TableActorMessages.cs`
- `TableVisibilityProjection.cs`、`TableEventProjection.cs`、`TableVisibilityContexts.cs`
- `Mahjong.Domain`、任何 `.csproj`、`Mahjong.sln`、CI/workflow、verifier
- `AGENTS.md`、Unity 环境/Resources/Burst、其他在途任务文件

---

## 2. Contracts typed 载荷

`ProtocolProjectionPayload.cs` 新增有界不可变值对象：

| 类型 | 字段/用途 |
|---|---|
| `ProjectedEventPayload` | 21 类事件的完整 typed body；按类型工厂构造，禁止无关字段混入 |
| `YakuView` / `PaymentView` / `ScoreBreakdownView` | 结算公开事实；ura 实体需显式授权 |
| `SleeveBloomView` / `SleeveBloomReservationView` | 两个 offer 槽、window、reservation、createdTurn、drawKind、relative deadline |
| `TileMarkView` / `TileMarkGroupView` / `MarkedTileView` | owner-only 标记组与组生成代数 |
| `BeamSwapView` / `BeamSwapCandidateView` | owner-only 候选槽位、手牌、drawn、slotKind/index |
| `ShufflePreparationView` | 仅保留类型边界；P3.2a 只给布尔，因此本 mapper 不伪造明细 |
| `ConcealedMeldView` | 公共暗杠占位：只含 type/count/fromSeat，不含 identity |

`ProtocolSnapshot.cs` 的兼容扩展：

- `ProtocolEventRecord.KnownTyped(...)` + `ProjectionPayload`：生产路径必须有 typed body，不能发送空 payload。
- `PlayerPublicView.ConcealedMelds`：公共暗杠只给 `MeldType.ClosedKan`、4 张计数、`fromSeat=-1`；公共 `Melds` 仍禁止 `ClosedKan` 实体牌。
- `PrivatePlayerView.SleeveTiles` / `OwnerMelds`：仅本人袖牌与含暗杠四张的本人副露。
- `PrivateCheatView.OwnerTileMark` / `ImmediateSleeveOffer` / `OffTurnSleeveOffer` / `SleeveReservation` / `WindowId` / `OwnerBeamSwap` / `Preparation`：owner-only typed 主体；原布尔标志构造仍保留。
- `MeldView` 构造拒绝重复物理牌 id。

P3.2a 的 `PublicSnapshotView`、`PlayerSnapshotView`、`TableOwnerPrivateView`、`TableProjectedEvent` 未被修改；新映射只消费它们。

---

## 3. Wire append-only schema 变化

`mahjong.proto` 只追加字段/消息/枚举，未使用任何 reserved 编号，未改旧字段号、wire type 或语义。

| 消息 | 新字段 | 类型 | 说明 |
|---|---|---|---|
| `PlayerPublicView` | `concealed_melds = 13` | repeated `ConcealedMeldView` | 公共暗杠占位，不含 identity |
| `ConcealedMeldView` | 新消息 | type/count/from_seat | `CLOSED_KAN`、4、-1 |
| `PlayerPrivateView` | `sleeve_tiles = 16` | repeated uint32 | 仅 owner |
| `PlayerPrivateView` | `owner_melds = 17` | repeated `MeldView` | 含暗杠四张，仅 owner |
| `PrivateCheatView` | `off_turn_sleeve_bloom = 13` | `SleeveBloomView` | 与即时 offer 分离，可同时存在 |
| `PrivateCheatView` | `sleeve_bloom_reservation = 14` | `SleeveBloomReservationView` | reservation 独立 |
| `SleeveBloomView` | `created_turn_number = 15` | uint32 | 来源 turn |
| `SleeveBloomView` | `draw_kind = 16` | `SleeveBloomDrawKind` | DEAL_EXTRA/NORMAL/RINSHAN |
| `SleeveBloomView` | `has_deadline = 17` | bool | immediate 无 deadline，避免 0 被误读 |
| `SleeveBloomReservationView` | 新消息 | offer/tile/window | 不覆盖两个 offer 槽 |
| `TileMarkGroupView` | `created_round_id = 8` | string | 组生成局 |
| `TileMarkGroupView` | `created_round_ordinal = 9` | int32 | 组生成序 |
| `TileMarkGroupView` | `expires_after_round_ordinal = 10` | int32 | 过期局序 |
| `BeamSwapCandidateView` | `slot_kind = 8` | `BeamSwapSlotKind` | WALL/DEAD_WALL/REPLACEMENT |
| `BeamSwapCandidateView` | `slot_index = 9` | uint32 | 与旧 `slot` 数值一致；旧字段语义保持 |
| `BeamSwapView` | `has_deadline = 13` | bool | Domain 无 beam deadline，显式 false |
| `RonWin` | `is_chankan = 16` | bool | 保留 P3.2a 已投影的 CHANKAN 公开事实 |

新增枚举 `SleeveBloomDrawKind`、`BeamSwapSlotKind`；未新增 `EventType`，事件 still 21 类（1..21）。

### 3.1 能力与版本协商边界

- `protocolMajor=1`、`protocolMinor=0`、`projectionVersion=1` 未变。新增字段均为可选/追加字段；旧 minor-0 解码器会按 Protobuf 规则忽略未知字段，但**不能把“忽略”当作已理解**。
- 公共暗杠占位 `concealed_melds` 是新增可选能力：新客户端可正确计数；旧客户端看不到该字段时仍只会看到“无 meld”。因此产品/联合接线必须把该能力纳入客户端能力协商或升级门槛，不能把旧客户端视为已支持暗杠占位。
- 两个 offer、reservation、sleeve/beam/mark 为 owner-only 新增可选字段；未引入新的必填握手语义，未擅自提升 minor。
- 未知事件、未知 oneof、未知字段：现有策略不变——未知事件请求全量基线；public encode exit 继续拒绝未知字段；不会因为新类型放宽。
- `projectionVersion` 不一致仍拒绝；major 不一致仍拒绝；V4 `MJTC`/business 8 仍按 legacy 拒绝。

---

## 4. 映射规则与拒绝边界

### 4.1 `ProjectionWireMapper`（Contracts.Wire）

只接收 Contracts 投影 DTO，不引用 TableRuntime/Domain：

- `MapPublicSnapshot` / `MapPlayerSnapshot`：复制 `sessionId`、`roundId`、`cursor.streamVersion/projectionSeq`、`stateVersion`、`eventSeq`、`sessionVersion`、`roundVersion`；不分配 cursor/projectionSeq。
- `MapPublicUpdate` / `MapPlayerUpdate`：`fromCursor` 显式传入，`cursor` 取快照 cursor；事件只来自传入的可见事件。
- `MapEvent` / `MapPublicEvent`：从 `ProjectedEventPayload` 构造 21 类 oneof；未设置 `unknown_payload`；未知/未分类事件拒绝。
- 公共路径拒绝 private 事件类型即使声明 PUBLIC；`TsumoWin/RonWin` 必须 `SETTLEMENT_PUBLIC`；ura 必须 `SETTLEMENT_PUBLIC` + `ura_authorized`；`ure` breakdown 继续校验。
- `SleeveBloom`：`has_deadline` 区分“无 deadline”与“已到期”；只发送 remaining，不发送绝对 monotonic 值。
- `BeamSwap`：Domain 无 deadline，`has_deadline=false`、remaining=0，不以 0 伪造已到期。
- 暗杠：公共 `MapPublicEvent` 拒绝带 identity 的 closed kan；owner `MapEvent` 可带四张；公共快照用 `ConcealedMeldView`。
- `MapPublicSnapshot` 必填 round id；settlement round/finish reason 若非空必须是稳定 id/code。

### 4.2 `TableProjectionContractMapper`（TableRuntime）

只依赖 Contracts + 既有 Domain 类型；不读 actor、mailbox、stream、Domain 隐藏状态、系统时间：

| 输入 | 输出 |
|---|---|
| `PublicSnapshotView` + 可选 typed settlement events + 显式 public concealed-kan facts | 新 `PublicSnapshotView`（identity/cursor 原样） |
| `PlayerSnapshotView` + `TableOwnerPrivateView` + `TableProjectionTimeContext` | 新 `PlayerSnapshotView`（owner sleeve/owner melds/typed cheat） |
| `TableProjectedEvent` + `TableProjectionEventFacts` | `ProtocolEventRecord.KnownTyped` |
| `TableOwnerCheatView` + time context + hasPreparation | `PrivateCheatView` |

显式事实边界：

- `turnNumber`、`wallSize`、`wallRemaining`、meld、discard flags、riichi discard、loser、yaku/han/fu/score/breakdown、tenpai、payments、new/final scores、round wind/number、offer id/resolution 等 P3.2a 不携带的事实必须由 `TableProjectionEventFacts` 提供；缺失时抛 `TableProjectionException(InvalidPayload)`，不填 0/空串。
- `Chi/Pon/Kan` 的 meld 必须与投影事件的 tile ids 一致；公共暗杠若 facts 带入四张 identity 直接拒绝。
- owner view 与 player snapshot 的 closed/drawn/furiten/discard history/balance 必须一致；不一致拒绝。
- `TableProjectionTimeContext.MonotonicNowMs` 由调用方显式提供；off-turn deadline 为绝对 monotonic 时，mapper 计算 `max(0, deadline-now)`，不读系统时间。deadline=0 视为无 deadline（immediate/未设置），不伪造“已到期”。
- `TableSeatPrivateFacts.HasPreparation` 只映射布尔；P3.2a 没有 target/cost 明细，mapper 留 `Preparation=null`，不伪造。
- settlement events 必须带 typed body，且只能是 settlement 类型或授权 ura Dora；metadata-only 记录被拒绝。
- 公共 concealed-kan facts 只能指向板内座位；只生成 identity-free 占位。

---

## 5. Wire strict 校验

`WireCodec` 原有 public encode/decode 校验未放宽，新增：

- `EnsureStrict(Envelope)`：拒绝整棵可达树的未知字段；校验 body/type 配对、seat/owner、牌 id、重复牌、meld、暗杠占位、私有事件类型、Tsumo/Ron settlement visibility。
- `ValidateEventStrict(EventEnvelope)`：21 类事件 oneof/type/schema/visibility；private 事件必须 PRIVATE；public 事件必须 PUBLIC/SETTLEMENT_PUBLIC；win 必须 SETTLEMENT_PUBLIC；ura 必须授权。
- 公共可达图：`PublicSnapshot`/`PublicUpdate`/`SyncResponse(public)` 继续拒绝未知字段；公共 `PlayerPublicView.Melds` 禁止 closed-kan 实体牌；`ConcealedMeldView` 必须是 closed kan/4/-1；越界牌/重复牌/越界 seat 拒绝。
- 私有 owner：`PlayerSnapshot/Update` 的 `private_view.seat` 与 envelope seat 不一致时 strict 拒绝；`OwnerMelds` 四张暗杠只在 private 可达；`SleeveTiles`/owner melds 不得与手牌/袖牌重叠。
- `Encode` 对 `PlayerUpdate.VisibleEvents` 调用 strict event 校验；`Encode` 不强制 envelope seat=private seat（保留既有 Client 对“错误 owner 被客户端拒收”的测试路径），`Decode` 保持对未知 optional 字段的既有宽容；strict 才做 owner 绑定。这是有意的兼容边界。
- 未改 `Decode` 对未知字段/minor 的宽容策略；未把未知字段静默丢弃后再转发。

---

## 6. 验证记录

全部使用 `D:/Unity/dotnet/dotnet.exe`。原始日志目录：
`C:/Users/sncdc/AppData/Local/Temp/p32-wire-final-20261024/`（临时目录，不提交）。
框架 TestResults 仍在各测试项目 `bin/<Config>/net8.0/TestResults/`。

### 6.1 最终命令、计数、exit

| # | 命令 | 结果 | exit |
|---:|---|---|---:|
| 1 | `dotnet build dotnet/Mahjong.sln --nologo -v minimal` | 成功，0 warning / 0 error | 0 |
| 2 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --nologo -v minimal` | 成功 | 0 |
| 3 | `dotnet build dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj --nologo -v minimal` | 成功 | 0 |
| 4 | `dotnet build dotnet/tests/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests.csproj --nologo -v minimal` | 成功 | 0 |
| 5 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj --nologo -v minimal --no-build` | **96 passed / 0 failed / 0 skipped**（基线 82 + 新增 14） | 0 |
| 6 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --nologo -v minimal --no-build` | **109 passed / 0 failed / 0 skipped**（基线 104 + 新增 5） | 0 |
| 7 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj --nologo -v minimal --no-build` | **132 passed / 0 failed / 0 skipped** | 0 |
| 8 | `dotnet test dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj --nologo -v minimal --no-build` | **80 passed / 0 failed / 0 skipped**（单元，不是真实 WSS） | 0 |
| 9 | `dotnet test dotnet/tests/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests.csproj --nologo -v minimal --no-build` | 77 passed / 0 failed / **12 skipped**（PG 未配置） | 0 |
| 10 | `dotnet test ...Contracts.Tests... -c Release --nologo -v minimal` | 96 / 0 / 0 | 0 |
| 11 | `dotnet test ...TableRuntime.Tests... -c Release --nologo -v minimal` | 109 / 0 / 0 | 0 |
| 12 | `dotnet test ...Client.Tests... -c Release --nologo -v minimal` | 132 / 0 / 0 | 0 |
| 13 | `dotnet build dotnet/Mahjong.sln -c Release --nologo -v minimal` | 成功，0 warning / 0 error | 0 |
| 14 | `git diff --check` | PASS（无空白错误） | 0 |

非重复合计（Debug）：96 + 109 + 132 + 80 + 77 = **494 passed / 0 failed / 12 skipped**。其中 12 skip 全部是 LobbyService 的既有 PG 条件跳过，不代表本轮 PG 通过；不得当作统一入口 PASS。
Release 定向：96 + 109 + 132 = **337 passed / 0 failed / 0 skipped**。

### 6.2 测试覆盖

- 所有 21 事件：Contracts typed payload -> `ProjectionWireMapper` -> Encode -> Decode -> 再 Encode 字节稳定；private/public 分类与 oneof 匹配。
- 公共流 + 四席矩阵：15 个公共事件在 public update 与 4 个座位 player update 上真实编码；6 个 private 事件在 owner stream 编码，public 映射拒绝；共 60 个 player update + 15 个 public update。
- owner 隐藏信息：四席 player snapshot、sleeve tiles、owner melds（含暗杠四张）、private ledger、mark group、beam 候选/hand、两种 sleeve offer + reservation + window。
- 暗杠：公共 `ConcealedMeldView`（无 identity）与 owner `OwnerMelds`（四张）分离；公共 `MeldView` closed kan 被拒绝；公共/owner `KanDeclared` 红action 边界。
- 里宝：未授权 ura 被 mapper/Wire 拒绝；settlement-public + authorized 成功；breakdown ura 同样受控。
- 非法输入：未知事件、oneof mismatch、越界牌 136、重复牌、错误 owner seat、metadata-only settlement、空候选/负 deadline/空必填事实。
- legacy：major 8 拒绝；future minor optional field decode 仍接受；本 build 仍不能 emit 更高 minor。
- schema：字段号表、reserved 范围、proto/descriptor 一致、fingerprint、protoc 生成两次字节一致。
- TableRuntime：真实 P3.2a 投影 -> Contracts mapper；missing fact 拒绝；暗杠公共/owner 边界；单局/整场 session 摘要保持；owner/snapshot 不一致与缺 time context 拒绝。既有 104 项未改且全绿。

### 6.3 状态

| 项目 | 状态 |
|---|---|
| Contracts.Tests | **PASS** 96/0/0（Release 同） |
| TableRuntime.Tests | **PASS** 109/0/0（Release 同；基线 104 保留） |
| Client.Tests | **PASS** 132/0/0（Release 同） |
| Client.Wss.Tests（单元） | **PASS** 80/0/0（Debug） |
| LobbyService.Tests（单元） | **PARTIAL/PASS with 12 skipped**：77/0/12，PG 未配置 |
| 真实 WSS/TCP 流 | **NOT_RUN** |
| 真实 PostgreSQL / 统一入口 | **NOT_RUN**（未配置 PG；若跑统一入口应如实 FAIL，未改 verifier/未加 skip） |
| Unity Editor / IL2CPP / Android/iOS 真机 | **NOT_RUN** |
| G1 / G5 | **未通过/未执行** |
| 完整 P3.2 | **未完成**（P3.2b actor/stream/Wire 联合接线未做） |

### 6.4 实际编码样本

最小 PublicSnapshot（无 board，仅 identity/session）经 `ProjectionWireMapper` + `WireCodec.Encode`：

```
0801180120012806320B636F72722D73616D706C653A0E726571756573742D73616D706C65F202300A0E73657373696F6E2D73616D706C65120C726F756E642D73616D706C651A040807100B200728653202120038014002
```

SHA256：

```
b96cf57d7e64c55d497543dec791eec13711ab297fcbc5fa490285fc56897c51
```

说明：这是最小 identity 样本，用于证明生成代码真实编码；完整 21 事件/四席样本在测试 fixture 中生成并逐字节 round-trip，不以该最小样本代替覆盖结论。

---

## 7. 文件 SHA256

| 文件 | SHA256 |
|---|---|
| `dotnet/src/Mahjong.Contracts/ProtocolProjectionPayload.cs` | `858F2563212434E472A0AE18C616DA2DEF77A15430260840A23218A4C7810DF9` |
| `dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs` | `141BAD6ACAC40F505863BC26137BD897FA229F673398CDE4FCE688362B10E3FE` |
| `dotnet/src/Mahjong.Contracts.Wire/mahjong.proto` | `90AD5565C2F3F4AA267DAD03A66952F49A480959645CC47E4C1B4997AA99C61A` |
| `dotnet/src/Mahjong.Contracts.Wire/ProjectionWireMapper.cs` | `F967005C122CA256FD635C72A81F6BE060C060067C528DE75877348A1C07445F` |
| `dotnet/src/Mahjong.Contracts.Wire/WireCodec.cs` | `94D5FFD8A046E8107F7DBC2083EA4961E69D67DC22AB067FBF4DBC1E0CF37ADC` |
| `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs` | `8756DAC368CD6901075BB47789AD63C59BA4325524F60D650BA2AF8A52A99683` |
| `dotnet/tests/Mahjong.Contracts.Tests/ProjectionWireMapperTests.cs` | `6044F4B8B4356C6E32D8388C2A3AD4EEBC71C50628BF0C9C2E7598583A2DC70F` |
| `dotnet/tests/Mahjong.Contracts.Tests/SchemaCompatibilityTests.cs` | `61FE44BB01C2BC0BBE80266CA0F0D280F57FE0FE046BB5448F3ED76B9F339030` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableProjectionContractMapperTests.cs` | `C52160E5105D5F490D876DD2127F3FDC134E6B5D1790B40BF798859A8560C2F1` |
| 本文 | 见最终交付响应（提交后计算） |

---

## 8. 未完成与责任

- **P3.2b**：actor 内提交后原子投影、per-stream cursor/projectionSeq、binding/sync epoch、首基线/订阅、恢复、早期缓冲、旧 epoch/round 拒绝、真实 Wire 流测试。本组件只提供纯载荷与映射，不接线。
- **联合接线**：caller 在 P3.2b 接受后，用本 mapper + `TableProjectedEvent` + actor 提供的 turn/meld/win/settlement facts 组装 `ProtocolEventRecord`/Wire update；未交付前不得宣称完整 P3.2。
- **真实网络/PG/平台**：全部 NOT_RUN；没有把 Lobby 的 12 个 PG skip 或 WSS 单元测试当作真实 WSS/PG。统一入口在无 PG 时会 FAIL，本轮未运行、未改 verifier、未加 skip。
- **客户端能力**：公共暗杠占位、两个 offer、sleeve/beam/mark 新字段是新增 optional 能力；旧客户端忽略未知字段时不具备这些语义，产品侧必须做能力/升级门槛，不能默认为已支持。
- **准备明细**：P3.2a 只有 `HasPreparation` 布尔，Contracts 保留 `Preparation=null`；不伪造 target/cost/seeThrough。需要明细时由 P3.2b/actor 端口补显式输入。
- **deadline**：只接受显式 `MonotonicNowMs` 或已有 remaining；不读系统时间；绝对 monotonic 值不上线。
