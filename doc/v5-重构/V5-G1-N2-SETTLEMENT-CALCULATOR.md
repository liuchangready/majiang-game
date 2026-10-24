# V5 G1 N-2 权威结算计算组件交付报告（R2 返修）

日期：2026-10-25
工作树：`D:/majiang-wt/n2-settlement-calculator`
分支：`wt/v5-n2-settlement-calculator`
基线：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（唯一 PR19 最新已发布头）
R1 交付：`d916cc0bff31ea190dc24e1a00409183c5ae2301`（caller 独立复核不通过）
**R2 结论：R1 组件 PASS 结论作废；D-1-01～D-1-04 四组 MAJOR 已在本轮组件内修复并有真实负例/正例。N-2 整体、G1、G5 仍未通过；V4 仍是唯一生产基线。**

本交付仍只准备 N-2 计算组件；未接入 `TableActor/TableRuntime/协议宿主`，未替换调用方携带预算 settlement 的旧入口。原调用方旧入口尚未收口，不能把本组件测试通过写成 N-2 完成或 G1 通过。

---

## 1. R1 复核失败与结论更正

caller 对 R1 `d916cc0bff31ea190dc24e1a00409183c5ae2301` 的独立行为探针显示 **8 个应拒绝输入被接受，BEHAVIOR_FAILURES=8，exit1**。R1 报告写成“组件 PASS”是错误的；本轮保留该历史失败证据，不覆盖、不刷新。

R1 caller 原始证据（只读引用）：

- 根目录：`C:/Users/sncdc/AppData/Local/Temp/n2-caller-review-848b1336aa8245819ccbccca1dbbedee/`
- `behavior-probes-verified.log` SHA256=`512C7CE67EF9E2906CA39C5365B8C1C462FC3D9AE9FF87020FDF40759FDDBA9D`
- `probes/Program.cs` SHA256=`8020F5C28DCCDC59EB38CA23D162ACE1D5BDEF7FECE5E446062EF8864615FD82`
- `probes/TablePlan.cs` SHA256=`E20019E764CFECCE5F08B00CE7DFC15D6F4DB2C9FDA8E1357A5B150DE4DD0163`
- R1 Domain `domain-full.log` SHA256=`4C5C2209665C3CEB90288E866F8677452CCF1007317DD65E871A19F2C746A616`
- R1 TableRuntime `runtime-full.log` SHA256=`C32646E2D22CFF4685DBF72F030155AD58523D820096AF4388501A938D793896`
- R1 solution `solution-release.log` SHA256=`166153C684F1B24609394260C6ABBB95C0AE7FD0EBD92690DDC2AB9828C066F4`

R1 已实跑 343/313/0 warnings/0 errors 本身仍成立；问题不是测试数量，而是缺少行为级负例导致误 PASS。R2 把探针标签转成仓库内真实断言，并补充合法正例。

---

## 2. R2 逐 ID 修复映射

### D-1-01 MAJOR：途中流局条件未完整校验

位置：`RoundSettlementCalculator.ValidateAbortiveReason`、`CalculateAbortiveDraw`。

修复：

- **四风**：要求四座均有可信 `FirstDiscard`，四张首舍均为风牌且同一风；四座 `IsFirstTurnUninterrupted=true`；全场无副露、`KanCount=0`、无 `ResponseClaimed/KanDeclared` 事件；首舍实体牌必须位于该座河底或被鸣走后的副露槽，且不能在本座副露区。缺任一事实拒绝。
- **四杠**：要求四座均有可信 meld facts；`KanCount=4`；四座实际 kan 总数为 4，且至少两个不同座位拥有 kan；同座四杠拒绝；facts 与 map 的 Meld 组不匹配拒绝。
- **九种九牌**：只检查当前宣告者 `CurrentSeat`，不扫描任意座位；要求宣告者 `IsFirstTurnUninterrupted=true`、`DiscardHistoryProvided=true` 且为空、当前河为空、全场无副露/杠、门清、手牌 13 或 14 张、至少 9 种不同幺九/字牌。他家九种、已舍牌、已鸣牌、非首巡均拒绝。

新增/保留测试：`AbortiveFourWinds_RequiresMatchingFirstDiscardsAndNoCalls`、`AbortiveFourKans_RequiresKansAcrossAtLeastTwoSeats`、`AbortiveNineTerminals_UsesCurrentSeatAndRejectsLateOrOtherSeat`、`AbortiveDraw_HasNoPaymentsAndRetainsRiichiSticks`（合法四风正例）。

### D-1-02 MAJOR：副露物理集合相等不足以证明合法

位置：`RoundSettlementCalculator.ValidateMelds`、新增 `ValidateMeldShape`、`ValidateMeldOwnership`。

修复：

- 先校验 `MeldType` 已定义；未知枚举（如 `(MeldType)255`）明确 `MeldTypeMismatch`，不再依赖 scorer 偶然异常。
- Chi 必须是 3 张**数牌**、同一花色、连续点数；字牌 Chi 明确拒绝（`Chi cannot contain an honor tile`）。
- Pon/OpenKan/ClosedKan/AddedKan 必须是同种牌、张数 3/4；ClosedKan 仍要求 `fromPlayer=-1`；开副露 caller 0..3 且不得等于本座；Chi 只能来自上家；`calledTile` 必须在实体组内。
- 仍与 Meld 区槽位做物理集合精确匹配，并要求覆盖全部 Meld 组；不修改既有 `ScoringModels.Meld` 或 Rules。

新增测试：`MeldShapeRejectsHonorChiAndUndefinedType`；合法数牌 Chi/Pon/Kan 仍由 `RepresentableV4ScoringWinSamples...` 的开放手牌样本及既有 chankan/pon 测试覆盖。

### D-1-03 MAJOR：历史役标志允许与当前 map 直接矛盾

位置：`RoundSettlementCalculator.ValidateSeatFlags`；`RoundSettlementSeatFacts` 新增 `DiscardHistory`、`IsFirstTurnUninterrupted`。

修复：

- **天和**：必须 tsumo、庄家、`DrawSource.DealExtra`；同时要求全场无 Meld、`KanCount=0`、无任何 Discard、无 `TileDiscarded/ResponseClaimed/KanDeclared` 事件。开 Pon + KanCount1 + DealExtra 的“天和”拒绝。
- **地和**：必须 tsumo、闲家、门清；全场无 Meld/kan；`DiscardHistoryProvided=true` 且为空；当前本座河为空；`IsFirstTurnUninterrupted=true`；事件中不得有本座弃牌/鸣牌/杠。已有 22 张本座河、事实未提供完整历史的“地和”拒绝。
- **一发**：`IsIppatsu` 与 `IsRinshan` 互斥；任何 `KanCount>0`、Meld、`KanDeclared` 均使一发失效。`riichi+ippatsu+rinshan+ClosedKan` 拒绝。
- 保留合法天和（无鸣牌/杠/舍牌、DealExtra）与合法地和（闲家首次无鸣牌、history 空、first-turn=true）正例。

新增测试：`HistoricalWinFlagsMustNotContradictTheMapOrKanState`（3 负例 + 天和/地和 2 正例）。

### D-1-04 MAJOR：Ron 未处理振听，事实模型无法提供缺失历史

位置：`RoundSettlementCalculator.CalculateRon -> CalculateWin`、新增 `ValidateRonFuriten`；`RoundSettlementSeatFacts` 新增 `DiscardHistory`、`DiscardHistoryProvided`、`IsTemporaryFuriten`、`IsRiichiFuriten`。

修复：

- Ron（含抢杠）要求完整 `DiscardHistory`；**缺失历史明确 `FuritenEvidenceMissing`，不默认空**。
- 当前本座河必须是 history 的子集；history 实体牌必须仍位于合法位置：本座河底，或被他家鸣走后的副露槽；重复实体、位于自己手牌/副露、位于他家河底/牌山/死山均拒绝。
- 从当前手牌计算 `HandDecomposer.CalculateWaits`；history 中任一实体牌 typeIndex 与理论待牌相交，拒绝 `FuritenRon`。
- `IsTemporaryFuriten` 或 `IsRiichiFuriten` 为 true，拒绝 `FuritenRon`。
- 自摸不受振听限制；未提供 history 不影响自摸。

新增测试：`RonFuritenAndMissingHistoryAreRejectedButTsumoIsNotBlocked`；普通非振听 Ron 与抢杠正例继续通过。

---

## 3. 事实模型与校验补充

### 3.1 新增可信事实（仅 map 无法表示者）

`RoundSettlementSeatFacts` 新增：

| 字段 | 用途 | 缺失/矛盾处理 |
|---|---|---|
| `DiscardHistory` + `DiscardHistoryProvided` | 完整本座舍牌历史（含被叫走牌）；振听、地和、九种九牌 | Ron/地和/九种九牌缺历史拒绝；实体/归属矛盾拒绝 |
| `FirstDiscard` | 四风首舍实体牌 | 四风缺任一拒绝 |
| `IsFirstTurnUninterrupted` | 首巡未被鸣牌/杠打断 | 四风/地和/九种九牌要求 true |
| `IsTemporaryFuriten` / `IsRiichiFuriten` | 同巡/立直振听 | Ron 为 true 拒绝 |
| `Meld` 类型/实体/caller | 副露历史 | 类型/形状/归属/组不匹配拒绝 |

### 3.2 仍可从 map 推导

roundId/version/fingerprint、dealer/round wind/honba/立直棒/起分、phase/current seat/events/window、winner/loser、seat wind、手牌实体/张数、副露槽位、摸牌证据、普通 Ron 的 latest discard、抢杠 `AddedKan` dry-run、宝牌指示牌位置、里宝、dora/red、听牌。

### 3.3 拒绝与第二道闸

所有新增拒绝均为 `RoundSettlementRejectedException` + 稳定 `RoundSettlementRejectionCode`：`AbortiveReasonMismatch`、`MeldTypeMismatch`、`WinFlagMismatch`、`FuritenRon`、`FuritenEvidenceMissing` 等。拒绝发生在构造/执行前；源 round 的 version/fingerprint/phase/events/tiles 不变。

`RoundSettlementCalculation.Execute` 仍先校验源 roundId/version/fingerprint，再调用现有 `Prepare/Install`；伪造旧式 settlement 仍被状态机 `SettlementConservationViolation` 拒绝。裸 `RoundCommand` 不携带 fingerprint 的边界仍存在（见 §6 N2-GAP-2）。

---

## 4. R2 验证与原始证据

CLI：`D:/Unity/dotnet/dotnet.exe`。工作树 HEAD 为本轮最终提交（见交付回复）；仅 5 个 expectedFiles。

| 验证 | 实际结果 | exit |
|---|---|---:|
| 新增定向 + 黄金（`*RoundSettlement*`） | **27 passed / 0 failed / 0 skipped**（23 定向 + 4 黄金） | 0 |
| Domain 全量 | **349 passed / 0 failed / 0 skipped**（基线 322 + 新增 27） | 0 |
| TableRuntime 依赖回归 | **313 passed / 0 failed / 0 skipped** | 0 |
| Solution Release build `--no-incremental` | **0 warnings / 0 errors** | 0 |
| caller 行为探针（R1 同命令） | **BEHAVIOR_FAILURES=0**；9 个标签全部以 `RoundSettlementRejectedException` 拒绝，未再 accepted | 0 |

R2 独占证据目录：

`C:/Users/sncdc/AppData/Local/Temp/n2-settlement-r2-evidence-22418da0e1654aa89669c598e0e28c5d/`

| 文件 | 字节 | SHA256 |
|---|---:|---|
| settlement-tests.log | 1364 | `AB53ECC07ED62DB6610039BBC0504123A38FEBE4150BCBC1D6B4B5E4FBCD34E1` |
| settlement-tests-xunit.log | 1362 | `C4D6062BFA21BD34B6CA8D7788C466F09821CEB87A8BF39564058A1821253E77` |
| domain-full.log | 1368 | `F98CE7CA26B5B346EE98665E7F826A17788E8069B2C543BF08C0B1FF74526E07` |
| domain-full-xunit.log | 1300 | `094592854EF1636817DD2B565F7CDEE75C213866123167474EA848B3BE11A5C3` |
| table-runtime-full.log | 2862 | `230438459F6E114CE04FB8AE78E39DAD52EA063AA2F61BD79033515102C35C26` |
| table-runtime-full-xunit.log | 1094 | `3D36BB7BED0886F0F754F3F7410A71E7C3E4A61464D542BC20C1C71644C7774C` |
| solution-release-build.log | 3588 | `F75588BD08F7570F79D77A65F2FF5C25271C75355E291771FDE21EB706DCDC1C` |
| caller-behavior-probes.log | 2316 | `E47B26E786D1BB71B6747435C505D94F114E49CF6A3C7578479630AC4A27EEA6` |

黄金实际计数（与 R1 一致，未刷新/未改写）：

```text
[N2-GOLDEN] scoring win checked=300 skipped=1013 scanned=2237
[N2-GOLDEN] scoring noWin checked=100 skipped=393 scanned=1211
```

caller 探针具体拒绝原因（R2 日志原文摘要）：

- `discard-furiten`：缺完整舍牌历史 → `FuritenEvidenceMissing`（本仓库新增测试用 history=[19] 断言 `FuritenRon`）。
- `chiihou-own-existing-discard`：缺完整舍牌历史 → `WinFlagMismatch`。
- `ippatsu-plus-rinshan`：`WinFlagMismatch`。
- `four-winds-no-winds`：缺首舍/首巡事实 → `AbortiveReasonMismatch`。
- `four-kans-same-seat`：caller facts 未提供 meld，先在 `MeldGroupMismatch` 拒绝；本仓库测试用完整 meld facts 专门断言同座四杠 `AbortiveReasonMismatch`。
- `nine-terminals-other-seat-late`：非当前宣告者首巡 → `AbortiveReasonMismatch`。
- `undefined-meld-enum`：显式 `MeldTypeMismatch`（不再是 scorer 的 `InvalidOperationException`）。
- `honor-chi`：显式 `MeldTypeMismatch`。
- `tenhou-with-open-pon`：`WinFlagMismatch`。

`four-kans-same-seat` 的 caller 探针拒绝点更早，是因为其 facts 仅供 seat0 且未携带四个 meld；本仓库 D-1-01 专项测试已用完整四座 facts 覆盖“同座四杠”规则本身。该差异在日志中如实保留，不把提前拒绝冒充规则级验证。

---

## 5. 黄金覆盖与不足

- **scoring random 300 胜例**：逐项比较 yaku code/han、yakuHan、scoreHan、isYakuman、yakumanCount、fu、basePoints、capCode、PaymentPlan total、最终分数，并真实执行命令到状态机终态。
- **scoring random 100 无和例**：新入口全部 `NoWinningHand` 拒绝。
- **RG-001**：versionAfter/phaseAfter/resultFinalScores/resultDeltas/resultOutcome/resultId 与 C# 结果一致；TsumoWin/RoundEnded 存在。
- **RG-002**：finalScores/totalDeltas/winner/loser/versionAfterRon/ChankanRon 一致；未产生 KanDeclared/RinshanDrawn。
- **不足**：随机样本缺真实 round/window/河底/供托扣除；部分 dora/ura 与手牌实体冲突或特殊 flag 不可表示，已跳过并计数。24 条 settlementSamples 只验证既有 `SettlementService` 支付规则，不是新入口 24 个独立用例。RG-002 物理 addedTile 与冻结样本 19 不同（测试用 16，同为 5m 类型），只比较可表示支付字段。V4 `ScoreSettled` 在 C# Domain 无同名事件，不能逐字对照。全量 10,238 条不是本入口覆盖分母。

---

## 6. 仍保留的后续范围缺口（非本轮组件缺陷）

以下不因 R2 测试通过而关闭，也不在本轮越界接线：

**N2-GAP-1 [MAJOR，宿主接线]** 计算器未接入 `TableActor/TableRuntime/协议`；旧调用方仍可自带 `RoundSettlement`。
**N2-GAP-2 [MAJOR，命令绑定]** 裸 `RoundCommand` 不携带 source fingerprint；直接提取 command 执行会绕过 `RoundSettlementCalculation.Execute` 的预校验。
**N2-GAP-3 [MAJOR，事件表示]** C# Domain 无 `ScoreSettled` 事件；支付/最终分在 `RoundResult/Payments`，N-3 需定义事件或投影合成。
**N2-GAP-4 [MAJOR，事实认证]** meld 历史、riichi/ippatsu、tenhou/chiihou、完整舍牌历史、首巡/首舍、振听标志仍必须由宿主可信记录提供；本组件只做物理/组合/矛盾校验，不能证明宿主未漏报。
**N2-GAP-5 [MINOR]** 多赢家每个 claim 推进 version，预计算第二 claim 会被 `StaleSourceVersion` 拒绝；必须逐 claim 重算。
**N2-GAP-6 [MINOR]** 杠后新宝牌转移未在 `TilePositionMap` 建模；本组件只读当前 `DoraIndicators`。
**N2-GAP-7 [INFO]** RG-002 物理 addedTile 差异，仅影响实体身份，不影响本用例支付字段。

R2 组件级已知 BLOCKER/MAJOR 缺陷：**0**（D-1-01～D-1-04 已修复）；上述 N2-GAP-1～4 是后续宿主/协议边界，不能写成 N-2 整体通过。

---

## 7. 复现命令

```powershell
# 新增定向 + 黄金
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v minimal -- --filter-class '*RoundSettlement*'

# Domain 全量
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v minimal

# 依赖回归
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal

# Release solution build
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v minimal

# caller 行为探针（只读引用，不修改探针）
& D:/Unity/dotnet/dotnet.exe run --project C:/Users/sncdc/AppData/Local/Temp/n2-caller-review-848b1336aa8245819ccbccca1dbbedee/probes/Probes.csproj -c Release
```

未运行：真实 PG/WSS、Unity/IL2CPP、Android/iOS/真机、跨物理网络、远端 CI。未 push、未建 PR、未部署。V4 仍是唯一生产版本；N-2 整体/G1/G5 未通过。
