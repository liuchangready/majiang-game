# V5 P3.2a 可见性投影交付说明

日期：2026-10-24  
状态：**P3.2a 可独立实现部分完成；不是完整 P3.2 验收，不是 G1 通过。**  
基线：`wt/v5-p32-visibility`，`a08629164935af1269e0bc1061f693371aed13ff`。  
范围：只新增 TableRuntime 投影与测试文件；未改 Contracts/Wire、未改 P3.3 在途文件、未改 solution/csproj/verifier。

> 本拆分只为避开正在执行的 P3.3 共享文件。P3.2b 的原子基线/订阅、binding/sync 流注册、actor 接线、恢复与 Wire 事件映射仍未完成；完整 P3.2 退出标准不因本交付降低。

---

## 1. 交付边界

### 1.1 已实现

- 从**不可变已提交 Domain 状态**（`RoundStateMachine`、`CheatState`）与显式 `TableProjectionContext` 直接构造
  `PublicSnapshotView`、`PlayerSnapshotView`、`TableOwnerPrivateView`。
- 显式白名单逐字段读取；不先把权威状态序列化再删字段。
- 逐事件、逐座位/观战的事件可见性策略；私有事件只到 owner；观战无私有事件。
- 暗杠事件的公开信息与实体牌身份分离：事件类型/座位公开，四张牌身份仅 owner。
- 里宝事件只在显式结算批次且 `RevealSettlementUra=true` 时投递；否则整条事件不投递。
- 未分类/未知事件默认拒绝；没有“claimed PUBLIC”入参，调用方无法提升可见性。
- 输出集合全部为新建只读集合；投影不返回 Domain 对象、`TilePositionMap`、墙顺序、种子或 `SourceFingerprint`。
- 事件保留来源 `EventSeq` / `CommandVersion` / `EventOrdinal`；本组件**不分配**流游标。
- 不同隐藏状态（墙顺序/他家暗牌/私有账本）产生相同公共投影的测试。
- 三绝技成功/拒绝、私有账本、结算、CHANKAN、鸣杠立直等测试。

### 1.2 明确未实现（P3.2b / 其他 owner）

- actor 内原子“提交成功 → 投影 → 发布”接线。
- 首基线/订阅原子建立；`bindingEpoch` / `syncEpoch` / `streamVersion` / `projectionSeq` 注册与拒绝。
- 每座位/观战独立游标、重连恢复、旧 binding/epoch 清理、全量基线重发。
- Wire/Protobuf 事件体映射；`TableProjectedEvent.EncodeBody()` 是内部投影体，不是 Wire。
- Domain 未提供的 meld/discard/riichi/furiten/preparation 事实的 actor 端口实现。
- 真实 PG、TCP/WSS、Unity/IL2CPP、Android/设备、G1/G5 验收。
- 未加入假 actor、旁路锁或未被调用的伪接线。

---

## 2. 字段映射

### 2.1 公共快照 `PublicSnapshotView`

| Contracts 字段 | 来源/责任 | 说明 |
|---|---|---|
| `SessionId` | `TableProjectionIdentity.SessionId` | 调用方显式提供；非秘密 |
| `RoundId` | `round.RoundId` | 必须与 `Identity.RoundId` 一致，否则拒绝 |
| `Cursor` | `Identity.Cursor` | **原样复制**；P3.2b 分配，本组件不推进 |
| `StateVersion` | `Identity.StateVersion` | 已提交桌版本；不得小于 `round.Version` |
| `EventSeq` | `Identity.EventSeq` | 已提交事件水位；不当作公共流 cursor |
| `SessionVersion` | `Identity.SessionVersion` | 调用方显式提供 |
| `RoundVersion` | `round.Version` | 单局诊断版本 |
| `View.Board.Phase` | `round.Phase` | 显式映射到 `ProtocolGamePhase` |
| `View.Board.RoundWind/RoundNumber/Honba/RiichiSticks` | `RoundSpec` / round | 公开场况 |
| `View.Board.DealerSeat/CurrentSeat` | round | 公开 |
| `View.Board.WallRemaining` | `round.Tiles.WallRemaining` | **只给数量**；不含墙顺序 |
| `View.Board.DoraIndicators` | `round.Tiles.DoraIndicators` | 只含已翻开表宝；不含里宝 |
| `View.Board.Scores` | `round.Result.FinalScores` 优先，否则 `round.Scores` | 四席公开点数 |
| `View.Board.Players[].HandCount` | `round.Tiles.CountAt(Hand, seat)` | 只给张数 |
| `View.Board.Players[].Melds` | `TableSeatPublicFacts.Melds` | 逐张对照 Domain Meld zone；**暗杠整组不进入公开 Contracts meld** |
| `View.Board.Players[].Discards` | `TableSeatPublicFacts.Discards` | 必须与 Domain Discard zone 的牌及顺序完全一致 |
| `View.Board.Players[].Riichi` | `TableSeatPublicFacts.Riichi` | Domain 不建模立直，显式端口 |
| `View.Board.LastDiscard` | `round.Tiles.LastDiscard` + 公开河事实 | 证据必须能在公开河事实中找到 |
| `View.Board.Paused` | `TableProjectionPolicy.Paused` | 显式上下文 |
| `View.Session` | `TableSessionProjection` 或已结束 round 的 result | 无可派生数据时为空摘要，不伪造 |
| `View.Clocks` | `TableProjectionContext.Clocks` | P3.3/actor 提供；最多四席 |
| `View.Paused` | `Policy.Paused` | 显式上下文 |
| `View.SettlementRoundId` | `Session.SettlementRoundId` 或刚结束 round id | 空串表示无结算展示 |
| `View.SettlementEvents` | `Context.Events` 中结算类事件的投影 | 观战按 `SettlementBatch` 门控；玩家流不受该门控 |

公共快照**不构造**：`my*` 手牌、摸牌、候选、`decision`、`cheatPoints`、`controls`、`preparation`、`presentationPreview`、`tileMark`、`sleeveBloom`、`beamSwap`、牌山/死山顺序、种子、`StateFingerprint`、里宝实体（未授权时）。

### 2.2 玩家快照 `PlayerSnapshotView`

| Contracts 字段 | 来源/责任 | 说明 |
|---|---|---|
| `Public` | 同 §2.1，按该座位 audience 投影 | 每席独立对象，不复用公共流对象 |
| `Private.ClosedTiles` | Domain `Hand` zone，扣除该席 `DrawnTile` 证据 | 仅请求座位；他家不生成 |
| `Private.DrawnTileId` | `round.Tiles.DrawnTile`，仅当 `Seat == viewer` | 他家为 `null` |
| `Private.Furiten` | `TableSeatPrivateFacts.Furiten` | Domain 不建模振听，显式端口 |
| `Private.DiscardHistory` | `TableSeatPrivateFacts.DiscardHistory`，缺省为当前河 | 可包含被鸣走的牌；必须覆盖当前河 |
| `Private.Decision` | `TableSeatDecision.Decision` | 仅该座位的 P3.3/actor 决策 |
| `Private.Cheat` | `CheatState.ViewFor(seat)` 映射 | 只含本人余额/本人 ledger；`Has*` 标志 |
| `Seat/Cursor/StateVersion/EventSeq/...` | 同 §2.1 | 每个座位传自己的 `Cursor` |

`PrivatePlayerView` 不包含袖牌、标记组、offer/candidate 明细；这些由 §2.3 owner 扩展承载。

### 2.3 owner 扩展 `TableOwnerPrivateView`（本任务新增，不进 Contracts/Wire）

当前 Contracts 私有类型不足，无法表达“本人袖牌、三绝技 offer/候选、落汗组、私有账本明细”。为避免把这些私货塞进公共类型，本任务新增 owner-only 值对象：

| 字段 | 来源 | 说明 |
|---|---|---|
| `ClosedTiles/DrawnTileId/Furiten/DiscardHistory` | 同 §2.2 | 本人 |
| `SleeveTiles` | Domain `Sleeve` zone | 仅 owner |
| `Melds` | `TableSeatPublicFacts.Melds` | 含暗杠四张实体牌；仅 owner |
| `Cheat.Balance` | `CheatPrivateView.Balance` | 仅 owner |
| `Cheat.Ledger` | `CheatPrivateView.Entries`（已按座位过滤） | 仅 owner |
| `Cheat.MarkGroups` | `CheatPrivateView.MarkGroups`（已按 owner 过滤） | 含实体牌 id |
| `Cheat.ImmediateOffer/OffTurnOffer` | `SleeveBloomOffer` | 含两张候选实体牌、deadline/window |
| `Cheat.Reservation` | `SleeveBloomReservation` | 含预留实体牌 |
| `Cheat.WindowId` | `SleeveBloom.WindowIdFor(seat)` | 仅 owner |
| `Cheat.BeamSwapOffer` | `BeamSwapOffer` | 含候选槽位/手牌 id/牌面 |

该扩展的所有集合均为新建只读副本；未暴露 `CheatState`、`CheatLedgerEntry`、`TileMarkGroup`、`SleeveBloomOffer`、`BeamSwapOffer` 等 Domain 对象。

### 2.4 Domain 未提供、必须由显式上下文/端口负责的字段

| 事实 | 责任 | 本组件行为 |
|---|---|---|
| meld 类型、called tile、from seat、暗杠标记 | `TableSeatPublicFacts` / P3.2b actor | 逐张对照 Meld zone；缺失且 Domain 有 meld 则拒绝 |
| discard turn number / riichi discard / tsumogiri | `TableSeatPublicFacts` | 牌与顺序对照 Domain；标志原样复制 |
| riichi 状态 | `TableSeatPublicFacts` | 原样复制，不推断 |
| furiten、完整舍牌历史（含被鸣走） | `TableSeatPrivateFacts` | 原样复制；历史必须覆盖当前河 |
| preparation/看破/预留 | `TableSeatPrivateFacts.HasPreparation` | 只输出布尔，不伪造明细 |
| decision/control/deadline/合法候选 | `TableSeatDecision`，P3.3/actor 提供 | 不读取 `DecisionClock`/`ControlRegistry`，不生成候选 |
| match finished/finish reason/final scores/rankings | `TableSessionProjection` 或 round result | 无 match 数据时 rankings 为空 |
| clocks | `TableProjectionContext.Clocks`，P3.3/actor 提供 | 原样复制 |
| 流游标/订阅/恢复 | P3.2b | 本组件只复制 `Cursor` |

---

## 3. 事件白名单（逐类型、逐座位/观战）

`TableEventProjection.Project(input, audience, policy)` 的 `audience` 只能是 `Spectator` 或 `Seat(0..3)`。**没有 claimed visibility 参数**；可见性由下表与 `ProtocolEventPolicy` 决定。

| # | ProtocolEventType | 玩家流 | 观战流 | 事件体字段/红action |
|---:|---|---|---|---|
| 1 | `StackingStarted` | 四席 | 是 | 无牌 |
| 2 | `WallBuilt` | 四席 | 是 | 无牌；不带墙顺序 |
| 3 | `TileDealt` | 仅 owner | 否 | owner 一张实体牌 |
| 4 | `DealingComplete` | 四席 | 是 | 无牌 |
| 5 | `TileDrawn` | 仅 owner | 否 | owner 一张实体牌 |
| 6 | `TileDiscarded` | 四席 | 是 | 一张公开弃牌 |
| 7 | `ChiDeclared` | 四席 | 是 | 三张副露实体牌 |
| 8 | `PonDeclared` | 四席 | 是 | 三张副露实体牌 |
| 9 | `KanDeclared` | 四席 | 是 | 明杠/加杠一张或四张公开牌；**暗杠仅 owner 得四张，其他席/观战得空牌集** |
| 10 | `RinshanDraw` | 仅 owner | 否 | owner 一张实体牌 |
| 11 | `RiichiDeclared` | 四席 | 是 | 无牌/无待牌 |
| 12 | `TsumoWin` | 四席 | 仅结算批次 | 和牌一张；不含完整手牌 |
| 13 | `RonWin` | 四席 | 仅结算批次 | 和牌一张；`IsChankan` 公开；不含完整手牌 |
| 14 | `DoraRevealed` | 四席（表）/结算授权（里） | 表公开；里仅结算批次+显式授权 | 表宝牌一张；里宝未授权整条不投递 |
| 15 | `CheatPointsChanged` | 仅 owner | 否 | owner 得 amount/newBalance；其他席/观战整条不投递 |
| 16 | `DrawGame` | 四席 | 是 | 仅稳定原因码（`EXHAUSTIVE_DRAW`/`ABORTIVE_DRAW`）；不复制 Domain `Detail` |
| 17 | `ScoreSettled` | 四席 | 是 | 事件本身不带支付明细；公开结算数值在 `PublicSessionSummary` |
| 18 | `RoundEnded` | 四席 | 是 | 局号/场况由快照承载 |
| 19 | `MatchEnded` | 四席 | 是 | 最终分数/胜者由 session 摘要承载 |
| 20 | `SleeveBloomResolved` | 仅 owner | 否 | 私有事件；offer id 只在 owner 快照 |
| 21 | `BeamSwapResolved` | 仅 owner | 否 | 私有事件；候选只在 owner 快照 |

补充：

- `CheatPointsChanged` / `TileDealt` / `TileDrawn` / `RinshanDraw` / `SleeveBloomResolved` / `BeamSwapResolved` 由 `ProtocolEventPolicy.IsPrivateEventType` 固定为 PRIVATE；调用方不能提升。
- `TsumoWin` / `RonWin` 为 `SettlementPublic`：玩家四席始终可见；观战只在 `Policy.SettlementBatch=true` 时可见。
- 里宝 `DoraRevealed(IsUraDora=true)` 必须同时满足 `SettlementBatch=true` 与 `RevealSettlementUra=true`；否则返回 `null`（不投递），不会生成“空事件”。
- 未授权里宝不会进入 `PublicMatchView.SettlementEvents`；不会造成公开游标缺口，因为 P3.2b 只对实际发射的事件递增 `projectionSeq`。
- 暗杠事件类型/座位是公开事实；四张牌 identity 只在 owner 流出现。公开 Contracts meld **整组省略**，避免用占位牌或假牌表达。
- 玩家流的公共事件与观战流共用同一事件白名单，但观战额外受 `SettlementBatch` 门控；玩家流不看该门控（结算事件对四席公开）。
- 事件 `EventSeq` / `CommandVersion` / `EventOrdinal` 始终复制来源，不重编号；`EventSeq` 不是公共流 cursor。

### 3.1 未知/未分类事件

| 情况 | 行为 |
|---|---|
| `ProtocolEventType.Unspecified` / 数值不在 1..21 | 构造/投影抛 `TableProjectionException(UnknownEventType)` |
| 未来新增但未进白名单的事件 | 默认拒绝；不会因 claimed PUBLIC 放宽 |
| Domain `RoundStarted` / `ResponseWindowOpened` / `ChankanWindowOpened` / `ResponseClaimed` / `WallExhausted` | `FromRoundEvent` 返回 `null`，不猜测成其他公开事件 |
| `CheatEventKind.TileMarkApplied` / offer cleared / reservation delivered | `FromCheatEvent` 返回 `null`；事实在 owner 快照/账本，不重命名成无关事件 |

---

## 4. 上下文校验

`TableVisibilityProjection.Validate` 在每次投影时执行，拒绝：

- `Identity.RoundId != round.RoundId` → `RoundIdMismatch`。
- `Identity.StateVersion < round.Version` → `StaleStateVersion`（旧 round/旧上下文）。
- `Cursor.StreamVersion > StateVersion` → `FutureCursor`。
- 事件 `EventSeq` 非严格递增 → `EventOrderMismatch`。
- 事件 `EventSeq > Identity.EventSeq` 或 `CommandVersion > StateVersion` → `EventVersionMismatch`。
- public/private facts 或 decisions 重复座位 → `DuplicateSeat`。
- Domain 有 Meld/Discard 但缺 public facts → `MissingPublicFacts`。
- meld 牌集合不等于 Meld zone、河牌顺序/牌不符、last-discard 证据不匹配 → `PublicFactsMismatch`。
- 私有舍牌历史漏掉当前河牌 → `PrivateFactsMismatch`。
- session 声称已结束但 round 未结束 → `InvalidContext`。
- 事件/座位/tile id 等非法值在构造器即抛 `ArgumentException`/`ArgumentOutOfRangeException`。

未校验：`controlEpoch`/`decisionId` 是否仍属当前控制者、binding/sync epoch、round 结果与 match 排名一致性；这些是 P3.3/P3.2b/P3.4 责任。本组件不授权动作。

---

## 5. actor 接线签名（P3.2b 使用，本任务未接线）

**唯一正确时机**：actor 单消费者内，`ITableCommitPort.CommitAsync` 返回 `Committed` 且新 `RoundStateMachine`/`CheatState` 已安装之后。

```csharp
// 每个流各构造一个 context；cursor 必须来自该流的 P3.2b 注册表。
var spectatorContext = new TableProjectionContext(
    new TableProjectionIdentity(
        sessionId,
        new RoundId(round.RoundId),
        committedStateVersion,
        lastEventSeq,
        spectatorCursor,          // P3.2b owns allocation
        sessionVersion),
    new TableProjectionPolicy(paused, settlementBatch, revealSettlementUra),
    clocks,
    publicFacts,
    privateFacts,
    decisions,
    committedEvents,
    sessionProjection);

PublicSnapshotView spectator = TableVisibilityProjection.ProjectPublicSnapshot(
    round,
    spectatorContext);

// 每个座位使用自己的 cursor，不要复用观战 context。
PlayerSnapshotView seat = TableVisibilityProjection.ProjectPlayerSnapshot(
    round,
    cheatState,
    seatIndex,
    seatContext);

TableOwnerPrivateView ownerDetails = TableVisibilityProjection.ProjectOwnerPrivate(
    round,
    cheatState,
    seatIndex,
    seatContext);

// 事件：只发射 Project 返回非 null 的事件；返回 null 的隐藏事件不占 projectionSeq。
foreach (TableEventRecord record in committedEvents)
{
    TableEventProjectionInput? input = TableVisibilityProjection.FromRoundEvent(record);
    if (input == null) continue;

    TableProjectedEvent? visible = TableEventProjection.Project(
        input,
        TableProjectionAudience.Spectator, // 或 ForSeat(seat)
        policy);
    if (visible == null) continue;

    // 也可直接取 Contracts 元数据事件（payload 仍为空，等待 Wire 映射）：
    ProtocolEventRecord? contractEvent = TableEventProjection.ProjectToContract(
        input,
        TableProjectionAudience.Spectator,
        policy);

    // P3.2b: visible -> Contracts.Wire protobuf；然后递增该流 projectionSeq 并发布。
}
```

约束：

- 不要先把 `RoundStateMachine`/`CheatState` 序列化再裁剪；不要读取 `TableSnapshot.StateFingerprint` 发给未授权流。
- 不要用全表 `event_seq` 冒充公共流 cursor；`ProjectionSeq` 只按实际发射递增。
- owner 扩展只发给该座位；不能进入观战/其他座位/公共日志。
- `TableProjectionPolicy.SettlementBatch/RevealSettlementUra` 必须来自权威结算批/产品授权，不能由客户端声明。

---

## 6. P3.3 控制输入契约

- 本组件只消费 `TableSeatDecision(int seat, DecisionView decision)`：
  - `DecisionView.DecisionId`、`ControlEpoch`、`DeadlineRemainingMs`、`LegalActions` 均由 P3.3/actor 冻结后传入。
  - 本组件**不引用** `DecisionClock`、`ControlRegistry`、`TableActor`、`TableRuntimeContracts.cs`、`TableRuntimePorts.cs`，不修改这些文件。
  - 本组件不判断 deadline、不回收/授予控制权、不生成或补全合法候选；空候选由 P3.3 决定是否允许。
- 旧 `controlEpoch` / 旧 `roundId` / 旧 `decisionId` 的拒绝必须在 P3.3/actor 入队前完成；本组件的 round/state/cursor 校验只是防止旧上下文被用于投影。
- 本组件不知道 binding/sync epoch；P3.2b 必须按流拒绝旧 binding/epoch，并清空旧私有缓存。

---

## 7. P3.2b 待完成清单（给 caller 的可审查接线步骤）

1. **流注册表**：为 `spectator` 与 `seat0..3` 建立独立 `StreamState`：`bindingEpoch`、`syncEpoch`、`streamVersion`、`projectionSeq`、baseline 标记。
2. **提交后原子投影**：只在 `CommitAsync == Committed` 后，使用同一 actor 单消费者构造 context、投影、入 outbox；投影失败不得回滚/改写已提交状态，必须按故障处理并保留旧基线。
3. **首基线**：新连接/重连先发 `isFullSnapshot=true` 的 `ProjectPublicSnapshot` 或 `ProjectPlayerSnapshot`；同一原子步骤内注册订阅，避免基线后漏事件。
4. **事件发射**：对每个流按 `ProjectEvents` 过滤；只有非 null 事件递增 `projectionSeq`；`streamVersion` 使用该事件所属 `StateVersion`；隐藏事件不产生 gap。
5. **旧 binding/epoch**：任何 `bindingEpoch`/`syncEpoch`/`roundId`/`cursor` 不匹配 → 拒绝增量并要求全量基线；切换玩家↔观战必须重建流身份和私有缓存。
6. **早期缓冲/缺口**：每流独立有界缓冲；超限或 `ProjectionSeq` 跳跃 → 全量基线，不退化为“补一个空事件”。
7. **恢复**：`TableRecoveryBaseline` 安装后重新投影所有流；恢复期间不推进 deadline；旧 actor/lease 的投影不得发布。
8. **Wire 映射**：把 `TableProjectedEvent` 的 `Type/Visibility/EventSeq/CommandVersion/EventOrdinal/TileIds/...` 映射到 P2.1 protobuf；不要发送 `EncodeBody()` 作为 Wire payload。
9. **端口实现**：actor 从规则/控制层填充 `TableSeatPublicFacts`（meld/discard/riichi）与 `TableSeatPrivateFacts`（furiten/preparation/history）；Domain 没有的事实不得推断。
10. **验收补齐**：旧 binding/epoch、订阅原子性、恢复、独立 cursor、多观战者字节一致、Wire 未知字段/私有嵌套拒绝。

---

## 8. 已知接口缺口（需 caller 决策，未擅自扩大 Contracts）

1. **Contracts 私有玩家类型不足**：`PrivatePlayerView`/`PrivateCheatView` 没有袖牌、标记组、offer 候选、beam 候选、preparation 明细。本任务新增 `TableOwnerPrivateView` owner-only 扩展；未改 Contracts/Wire。若要求走 Contracts，需要 P3.2b/Contracts owner 评审后扩展。
2. **暗杠的公开 meld 表达**：`MeldView` 强制 3/4 张实体牌，无法“只公开暗杠事实、不公开 identity”。本任务在公共快照中省略暗杠 meld，在 `KanDeclared` 事件中公开“发生暗杠”但不公开牌；owner 扩展保留四张。若要公共 meld 计数/占位，需要新 Contracts 字段。
3. **`DecisionView` 无座位字段**：本任务用 `TableSeatDecision.Seat` 包装；actor 必须保证合法候选属于该座位。
4. **事件 payload**：`ProtocolEventRecord` 在 P3.2a 使用空 payload（仅元数据，可用 `ProjectToContract` 直接取得）；完整投影体在 `TableProjectedEvent`，`EncodeBody()` 明确不是 Wire。P3.2b 必须做 Wire 映射，不能直接把内部体当 Protobuf。
5. **match/session 数据**：`TableSessionProjection` 由 match 层提供；无数据时只给 round 结果可派生的 finished/final scores，rankings 为空，不伪造名次。

---

## 9. 验证记录

### 9.1 基线

| 项目 | 结果 |
|---|---|
| `git status --porcelain=v1 -b`（开始） | `## wt/v5-p32-visibility`，无改动 |
| `git log -1 --format="%H %s"` | `a08629164935af1269e0bc1061f693371aed13ff Merge commit 'f414c8e' into wt/v5-codex-integration-20261024` |
| 基线 TableRuntime.Tests | 25 passed / 0 failed / 0 skipped |

### 9.2 实际运行（全部使用 `D:\Unity\dotnet\dotnet.exe`）

| 命令 | 结果 | 退出码 |
|---|---|---|
| `dotnet build dotnet/src/Mahjong.TableRuntime/Mahjong.TableRuntime.csproj --nologo -v minimal` | 成功，0 warning / 0 error | 0 |
| `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --nologo -v minimal` | 成功，0 warning / 0 error | 0 |
| `dotnet test ...Mahjong.TableRuntime.Tests.csproj --nologo -v minimal --no-build -- --filter-class "Mahjong.TableRuntime.Tests.TableVisibilityProjectionTests"` | 18 passed / 0 failed / 0 skipped | 0 |
| `dotnet test ... --no-build -- --filter-class "Mahjong.TableRuntime.Tests.TableVisibilityCheatTests"` | 4 passed / 0 failed / 0 skipped | 0 |
| `dotnet test ... --no-build -- --filter-class "Mahjong.TableRuntime.Tests.TableVisibilitySettlementTests"` | 4 passed / 0 failed / 0 skipped | 0 |
| `dotnet test ...Mahjong.TableRuntime.Tests.csproj --nologo -v minimal` | **51 passed / 0 failed / 0 skipped**（既有 25 + 新增 26） | 0 |
| `dotnet test ...Mahjong.Contracts.Tests.csproj --nologo -v minimal --no-build` | 82 passed / 0 failed / 0 skipped | 0 |
| `dotnet test ...Mahjong.Domain.Tests.csproj --nologo -v minimal --no-build` | 313 passed / 0 failed / 0 skipped | 0 |

说明：`dotnet test --filter "FullyQualifiedName~TableVisibility"` 在 xUnit v3 Microsoft Testing Platform 下未生效，实际跑了全量 51；定向运行改用 `-- --filter-class ...`，上表记录的是真实定向结果。

### 9.3 未运行 / 不计入 PASS

| 项目 | 状态 |
|---|---|
| PostgreSQL / PG 统一入口 | **NOT_RUN**（本任务纯投影，不依赖 PG；未修门禁） |
| TCP/WSS 真实网络 | **NOT_RUN**（P3.2b/P5） |
| Unity Editor / IL2CPP / Android 设备 | **NOT_RUN** |
| G1 / G5 阶段验收 | **未通过/未执行**；本交付不能作为 G1/G5 PASS |
| 完整 P3.2 退出标准 | **未完成**（P3.2b + Wire/actor 接线缺失） |

---

## 10. 文件与 SHA256

新增文件（不含本说明）：

| 文件 | SHA256 |
|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableVisibilityContexts.cs` | `DE876FA50D4E69C2F8D3E04646FB62671C2625DAE7E93CA255F435B35165DE94` |
| `dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs` | `FA0F57611338DA79C6B0DA9125B1F4AA489B0FF075562B3C04A68C12D3C62D18` |
| `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs` | `E38E550D025BFD5F6A098873DCDBC64F3ADC2EEC3D98E5EFA4AFA0DB4AB823DC` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilityTestSupport.cs` | `60651A4ED0A2C5AB051D515D316A44F41F30B9334EC4F0EF0BF5ECD3C66CDEB5` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilityProjectionTests.cs` | `1164B6E3CC185E043C62BB22B57D33633EB2F41F3CD1090DD1A7DDCCBFBBDFEE` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilityCheatTests.cs` | `975A527B15D68A261A985085B18BF92CB3BF4478C2F199ABF9A56FA268E79BD4` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilitySettlementTests.cs` | `FC25DB4E374FDE9FCBD2464729B7959EDA71C36481E89FD349735F463620F59D` |
| `doc/v5-重构/V5-P32A-VISIBILITY-PROJECTION.md` | 本文 |

未修改：`AGENTS.md`、Contracts/Wire、`TableActor.cs`、`TableRuntimeContracts.cs`、`TableRuntimePorts.cs`、`ControlRegistry.cs`、`DecisionClock.cs`、`TableRuntimeFixtures.cs`、`Mahjong.sln`、任何 csproj/verifier/Unity 环境/Resources/Burst。
