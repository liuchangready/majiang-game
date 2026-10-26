# V5 N-2 宿主结算接线（D-CUR-1）Round 2 返修记录

日期：2026-10-26。工作树 `D:/majiang-wt/n2-host-20261026`，分支 `wt/v5-n2-host-20261026`，冻结基线 `85134e9ce4fd67556f6d2f355249c14f4d3b42d4`。

## 范围

本记录只覆盖 N-2 的 TableRuntime 宿主接线返修，不重复已接受的 `RoundSettlementCalculator` 计算组件，不宣告 N-2 整体、G1 或 G5 通过。V4 仍是唯一生产基线。

授权文件：

- `dotnet/src/Mahjong.TableRuntime/TableSettlementCommands.cs`
- `dotnet/src/Mahjong.TableRuntime/TableActor.cs`
- `dotnet/tests/Mahjong.TableRuntime.Tests/N2HostSettlementTests.cs`
- `dotnet/src/Mahjong.Domain/Rules/RoundSettlementCalculator.cs`（仅为 D-HOST-R1-03 的 PASS/真实鸣牌区分授权）
- 本记录

## D-HOST-R1-01 意图座位绑定

`ProcessSettlementCoreAsync` 在身份校验前调用 `ValidateSettlementIntentSeat`：

- Tsumo：意图座位必须等于当时 `CurrentSeat`，且等于 `DrawnTile.Seat`；
- Ron：意图座位是荣和赢家，由 calculator 继续校验窗口资格；
- ExhaustiveDraw/AbortiveDraw：意图座位必须等于当前权威座位，不能再借用其他座位的 control/decision 身份。

串座返回 `SETTLEMENT_WINNERNOTCURRENT` / `SETTLEMENT_SEAT_MISMATCH`，零状态、零分数、零事件、零付款。测试 `TypedTsumoBindsWinnerSeatToAuthorityAndRejectsForeignSeat` 覆盖另座位已有 pending decision 的负例和合法当前赢家正例。

## D-HOST-R1-02 双立直

`IsDoubleRiichi` 只检查宣告者**自己**在 `RiichiDeclared` 之前的舍牌；其他座位先行舍牌不再取消双立直。真实 `Chi/Pon/Kan` 仍取消；`RiichiDeclared` 与同条 `TileDiscarded` 紧邻顺序有显式处理。测试 `PassAndOtherSeatDiscardsDoNotCancelDoubleRiichiAndScoresMatchCalculator` 用真实命令构造非庄家首舍立直，并通过 actor 结算与 calculator 的分数/役种对照，确认 `DOUBLE_RIICHI`、`IPPATSU` 与最终分数一致。

## D-HOST-R1-03 PASS 与真实鸣牌/杠区分

`ResponseClaimed` 的 `Detail=PASS` 不再被当作鸣牌：

- `TableSettlementCommands` 的 `IsFirstTurnUninterrupted`、`IsDoubleRiichi`、`IsIppatsuInterrupted`、`IsChiihou`、`IsTenhou` 统一使用 `IsRealCallOrKan`；
- `RoundSettlementCalculator` 的 Tenhou/Chiihou/FourWinds 守卫改用 `HasRealCallOrKan`/`IsRealCallOrKan`；真实 Chi/Pon/Kan 与 KanDeclared 仍打断，PASS 不打断。

行为结果：PASS 后地和成立，actor 结算 `9000/57000/17000/17000`（合计 100000），直接 calculator 也接受正确 chiihou facts。真实 Chi 负例仍使 `IsFirstTurnUninterrupted=false`，Domain 既有 FourWinds 真实鸣牌负例继续通过。

## D-HOST-R1-04 结算与 pending offer 原子提交

`ProcessSettlementCoreAsync` 在 `VerifySource` 后调用原 `TrySettleOfferForCommand`：

- 存在 SleeveBloom immediate 或 BeamSwap offer 时，使用 `CommitCheatAndInstallAsync` 将 offer 到期/账本/回执/journal/outbox 与终局结算作为一个 `RoundCheatChange` 提交；
- journal 写真实计算出的 `RoundCommand`，不写请求 placeholder；
- 无 offer 时仍走原 `CommitAndInstallAsync`。

测试覆盖：SleeveBloom immediate 成功结算；BeamSwap 成功结算；`ITableCommitPort.FailNext` 失败后 offer/状态/分数不变，重试成功；durable 成功提交、同 payload 重放、冷恢复后 offer 已清空且 outbox 仍为 1、付款不重复。

## 回归与边界

- `N2HostSettlementTests`：22 项全绿。
- TableRuntime 全量：395/0/0/0/0。
- Domain 全量：506/0/0/0/0。
- Release solution build：0 warnings / 0 errors。
- caller 原独立行为探针：`BEHAVIOR_FAILURES=0`，native exit 0（未修改探针断言）。
- N2-GAP-1 actor 接线关闭；N2-GAP-2 authority 模式关闭、legacy 无 port raw 入口仍为明确残余；N2-GAP-3 `ScoreSettled` 仍属 N-3；N2-GAP-4 可推导事实关闭、ippatsu/furiten 仍为宿主认证事实。真实 P4/P5、PG/WSS、Unity/IL2CPP/设备/远端 CI 均为 NOT_RUN。
