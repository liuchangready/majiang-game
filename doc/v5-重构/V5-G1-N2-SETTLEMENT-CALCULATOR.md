# V5 G1 N-2 权威结算计算组件交付报告（R3 返修）

日期：2026-10-25
工作树：`D:/majiang-wt/n2-settlement-calculator`
分支：`wt/v5-n2-settlement-calculator`
基线：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（唯一 PR19 最新已发布头）
R1 交付：`d916cc0bff31ea190dc24e1a00409183c5ae2301`（caller 复核不通过）
R2 交付：`47bb07aada670dce2e1e347454d39825333ec4ec`（caller 复核不通过，2 MAJOR）
**R3 结论：R1/R2 的组件 PASS 结论均作废；D-2-01（四风多舍）与 D-2-02（一发全局历史回归）已修复。N-2 整体、G1、G5 仍未通过；V4 仍是唯一生产基线。**

本交付仍只准备 N-2 计算组件；未接入 `TableActor/TableRuntime/协议宿主`，未替换调用方携带预算 settlement 的旧入口。原调用方旧入口尚未收口，不能把本组件测试通过写成 N-2 完成或 G1 通过。

---

## 1. R1/R2 复核失败与结论更正

### 1.1 R1 失败

caller 对 R1 的行为探针显示 **8 个应拒绝输入被接受，BEHAVIOR_FAILURES=8，exit1**。R1 报告“组件 PASS”错误，已作废。

R1 原始证据：

- 根目录：`C:/Users/sncdc/AppData/Local/Temp/n2-caller-review-848b1336aa8245819ccbccca1dbbedee/`
- `behavior-probes-verified.log` SHA256=`512C7CE67EF9E2906CA39C5365B8C1C462FC3D9AE9FF87020FDF40759FDDBA9D`
- R1 Domain `domain-full.log` SHA256=`4C5C2209665C3CEB90288E866F8677452CCF1007317DD65E871A19F2C746A616`
- R1 TableRuntime `runtime-full.log` SHA256=`C32646E2D22CFF4685DBF72F030155AD58523D820096AF4388501A938D793896`
- R1 solution `solution-release.log` SHA256=`166153C684F1B24609394260C6ABBB95C0AE7FD0EBD92690DDC2AB9828C066F4`

### 1.2 R2 失败

caller 对 R2 `47bb07a` 的新探针（2 个合法对照通过、3 个行为失败，BEHAVIOR_FAILURES=3，exit1）发现：

- `four-winds-extra-discard`：四家各有两张舍牌仍被接受为 FOUR_WINDS；R2 只校验了 FirstDiscard/首巡布尔，没有逐座校验“每家恰一次舍牌”。
- `ippatsu-after-pre-riichi-other-pon` / `ippatsu-after-pre-riichi-own-kan`：立直前已有他家 Pon 或本人暗杠，之后立直并一发的合法和牌被 R2 新增的全局 Meld/Kan 拒绝逻辑误伤。

R2 原始证据：

- 根目录：`C:/Users/sncdc/AppData/Local/Temp/n2-caller-r2-1792880422402/`
- `new-behavior-probes.log` SHA256=`3F828BFE764BFACF6C66617ECCEFFD80D4833BFB493C02C986C1B2AE99518CEA`
- `probes/Program.cs` SHA256=`A2C353FB3B9720D4974CADCE2393808F3C19A05F6CBB828CA13B2A3599D1FD4F`
- `probes/TablePlan.cs` SHA256=`AD362100E4D48CB3A7F4D51BA58E2AB5ECCFCAF6D4CED8461610AE55B5A74022`
- R2 Domain `domain-full.log` SHA256=`C864732E20B3FC2A58009E0C68151A3E417C5FE85A6C14387C23CF304864F915`
- R2 TableRuntime `runtime-full.log` SHA256=`544D4DA5AAC6AC11379A76EFD13C730BFB9CCF7BE2F992BC4242C372F6EF9D45`
- R2 solution `solution-release.log` SHA256=`9B0C0A7DDF310DB0EE9D785465EA474DAA39C36522BBEEC70E3458D453993383`

R2 的 27/349/313/0 warnings/0 errors 本身成立；失败是行为级负例不完整和新增过宽拒绝，不是构建/测试框架问题。

---

## 2. R3 逐 ID 修复映射

### D-2-01 [MAJOR，D-1-01 遗留] 四风多舍仍可流局

**位置**：`RoundSettlementCalculator.ValidateAbortiveReason(FourWinds)`；`AbortiveFourWinds_RequiresMatchingFirstDiscardsAndNoCalls`；报告 §2/§6。

**R2 复现**：R2 探针 `four-winds-extra-discard`。四座首舍 108/109/110/111，各另有舍牌 116/117/118/119；每座 facts 提供完整 history=[首舍,第二舍]、正确 `FirstDiscard`、`IsFirstTurnUninterrupted=true`；无 meld/kan，map 河底 2,2,2,2。R2 实际 accepted=True finished=True；期望拒绝。V4 `RiichiRuleEngine.kt:394-405` 要求每家 `discards.size == 1`。

**R3 修复**：

- 逐座要求 `DiscardHistoryProvided=true` 且恰好 1 条；
- `DiscardHistory[0] == FirstDiscard`；
- 当前 map 本座河恰好 1 张，且该张等于 `FirstDiscard`；
- 首舍实体必须位于该座河底（无鸣牌时）或合法被鸣走副露槽；本座副露区、他家河底/牌山/死山均拒绝；
- 全场仍要求无 Meld、`KanCount=0`、无 `ResponseClaimed/KanDeclared`。

因此 extra/missing/mismatch history 或 river 计数不是 1，均 `AbortiveReasonMismatch`；不能只信 `FirstDiscard` + 首巡布尔。

**新增测试**：`AbortiveFourWinds_RejectsExtraDiscardAndHistoryMismatch`（单座/全座多舍、history 缺失、首舍与 history 不一致）；合法四风正例 `AbortiveDraw_HasNoPaymentsAndRetainsRiichiSticks` 保留并要求 history 恰 1 条。

**验收断言**：R2 探针 `four-winds-extra-discard` 必须拒绝；`four-winds-valid` Calculate+Execute 通过；拒绝时 version/fingerprint/phase/tiles/events 不变。

### D-2-02 [MAJOR，R2 新增回归] 全局历史鸣牌/杠永久禁止一发

**位置**：`RoundSettlementCalculator.ValidateSeatFlags`；`RoundSettlementSeatFacts`。

**R2 复现**：

- `ippatsu-after-pre-riichi-other-pon`：seat0 门清和牌手牌，53 正常 Wall 自摸，可信 riichi/ippatsu；seat2 在立直前已 Pon [108,109,110]。R2 rejected=WinFlagMismatch；期望接受。
- `ippatsu-after-pre-riichi-own-kan`：seat0 立直前已有暗杠 [0,1,2,3]，当前闭牌 10 张 + 53 正常 Wall 自摸，KanCount=1，meld facts 与 map 匹配；之后立直并一发。R2 rejected；期望接受。

R2 在 `ValidateSeatFlags` 用全局 `KanCount>0`、`Meld>0`、`KanDeclared` 推断一发失效，混淆了“立直前的旧鸣牌/杠”和“立直宣告后的新打断”。R1 无此全局拒绝代码，故此前不可发现。

**R3 修复**：

- 移除仅凭全局 Meld/Kan/KanDeclared 历史永久拒绝一发的逻辑。
- 保留 `IsIppatsu` 与 `IsRinshan` 互斥（岭上与一发不可同时成立）。
- `RoundSettlementSeatFacts` 新增可信时序事实 `IsIppatsuInterruptedByCallOrKan`：true 表示当前立直宣告之后、和牌之前发生鸣牌/杠；`IsIppatsu=true` 且该事实 true 时 `WinFlagMismatch`。立直前的旧 Pon/暗杠不设置该事实，不得据此拒绝。
- 该事实由宿主按当前 round 的已验证时序提供；缺失时不从全局历史猜测，保持 N2-GAP-4 信任边界。

**新增/扩展测试**：`HistoricalWinFlagsMustNotContradictTheMapOrKanState`：

- 正例：无鸣一发（控制）；
- 正例：立直前他家 Pon；
- 正例：立直前本人暗杠后正常 Wall 自摸；
- 正例：立直前他家 Pon、之后合法 Ron；
- 负例：`IsIppatsuInterruptedByCallOrKan=true`；
- 负例：`ippatsu+rinshan`（保留）。

黄金筛选同步移除 R2 的 `ippatsu + meld` 提前跳过；合法旧副露一发可进入实际入口，不靠 skip 绕过。计数仍为 300 胜例/100 无和例。

**验收断言**：旧 Pon/旧暗杠后的合法一发 Calculate+Execute 通过且役含 IPPATSU、支付与冻结 evaluator 一致；立直后显式打断、ippatsu+rinshan 拒绝；拒绝源状态不变。

### D-1-01～D-1-04 保留映射

- **D-1-01**：四风/四杠/九种九牌原因级校验已在 R2 加入；R3 完成四风“每家恰一次舍牌”子项。
- **D-1-02**：字牌 Chi/未知枚举显式拒绝保留；合法数牌 Chi/Pon/Kan 仍通过。
- **D-1-03**：天和/地和矛盾校验保留；一发过宽部分由 D-2-02 修正。
- **D-1-04**：振听/完整舍牌历史校验保留；普通 Ron/抢杠正例与自摸不受振听限制通过。

---

## 3. 事实模型与校验

### 3.1 可信事实（map 无法表示）

`RoundSettlementSeatFacts`：

| 字段 | 用途 | 缺失/矛盾 |
|---|---|---|
| `DiscardHistory` + `DiscardHistoryProvided` | 完整本座舍牌历史；振听、四风、地和、九种九牌 | Ron/四风/地和/九种九牌缺历史拒绝；实体/归属/数量矛盾拒绝 |
| `FirstDiscard` | 四风首舍实体 | 四风缺任一拒绝 |
| `IsFirstTurnUninterrupted` | 首巡未被鸣牌/杠打断 | 四风/地和/九种九牌要求 true |
| `IsTemporaryFuriten` / `IsRiichiFuriten` | 同巡/立直振听 | Ron 为 true 拒绝 |
| `IsIppatsuInterruptedByCallOrKan` | 立直宣告后的新鸣牌/杠时序 | 与 `IsIppatsu` 同时 true 拒绝；旧 meld/kan 不设置 |
| `Meld` 类型/实体/caller | 副露历史 | 类型/形状/归属/组不匹配拒绝 |

### 3.2 从 map 推导

roundId/version/fingerprint、dealer/round wind/honba/立直棒/起分、phase/current seat/events/window、winner/loser、seat wind、手牌实体/张数、副露槽位、摸牌证据、latest discard、抢杠 dry-run、宝牌位置、里宝、dora/red、听牌。

### 3.3 拒绝与第二道闸

拒绝为 `RoundSettlementRejectedException` + 稳定 `RoundSettlementRejectionCode`（`AbortiveReasonMismatch`、`MeldTypeMismatch`、`WinFlagMismatch`、`FuritenRon`、`FuritenEvidenceMissing` 等），均发生在构造/执行前；源 round 的 version/fingerprint/phase/events/tiles 不变。

`RoundSettlementCalculation.Execute` 先校验源 roundId/version/fingerprint，再走现有 `Prepare/Install`；伪造旧式 settlement 仍被状态机 `SettlementConservationViolation` 拒绝。裸 `RoundCommand` 无 fingerprint 的边界仍见 §6。

---

## 4. R3 验证与原始证据

CLI：`D:/Unity/dotnet/dotnet.exe`。仅 5 个 expectedFiles。

| 验证 | 实际结果 | exit |
|---|---|---:|
| `*RoundSettlement*` 定向+黄金 | **28 passed / 0 failed / 0 skipped**（24 定向 + 4 黄金） | 0 |
| Domain 全量 | **350 passed / 0 failed / 0 skipped**（基线 322 + 新增 28） | 0 |
| TableRuntime 全量 | **313 passed / 0 failed / 0 skipped** | 0 |
| Solution Release build `--no-incremental` | **0 warnings / 0 errors** | 0 |
| R2 新探针（R3 重跑） | **BEHAVIOR_FAILURES=0**；`four-winds-extra-discard` 拒绝，旧 Pon/旧暗杠一发接受 | 0 |
| R1 旧探针（R3 回归） | **BEHAVIOR_FAILURES=0**；9 标签均拒绝 | 0 |

黄金计数（R3 重跑，未刷新旧产物）：

```text
[N2-GOLDEN] scoring win checked=300 skipped=1013 scanned=2237
[N2-GOLDEN] scoring noWin checked=100 skipped=393 scanned=1211
```

R3 独占证据目录：

`C:/Users/sncdc/AppData/Local/Temp/n2-settlement-r3-evidence-c753121f43814eefa5dc7db72a73197a/`

| 文件 | 字节 | SHA256 |
|---|---:|---|
| settlement-tests.log | 1358 | `344B54DB5313A9F250324CCACBEFAEF21D221F86E398161856206B4072D4FFFF` |
| settlement-tests-xunit.log | 1356 | `424595B86681CBB79730E31DD94F8199BF82F746DE7EED183F07816F0E9C7FD3` |
| domain-full.log | 1368 | `65F2420799DA12D7E714A0FF3E8ED35216F7A2DFF3569FCC63CAE1D085AF0E25` |
| domain-full-xunit.log | 1300 | `76A76416555FEC32D5AEFFB2BC0632F22B6DC0A3CAD71E1AC1B977CD63C82250` |
| table-runtime-full.log | 2862 | `DB784B52936B3A5AC55A4107A1561FB70D57E4D8D876E3DCC9DA78502E82C737` |
| table-runtime-full-xunit.log | 1094 | `5169D09705046E4AE87B7A6D5346FA59A818B8F042C6A0D8D53B098B4957F63C` |
| solution-release-build.log | 3588 | `CF9676A9A0DBDDB221D5E14F939B4CFFEA68B72B0793BED9C3516AD551F1A249` |
| caller-r2-behavior-probes.log | 1252 | `699F6A72B3FFE04D39E9DEC3EE0C5FACE0ACFAC02B5EA6329BDAE0D9B5B74971` |
| caller-r1-behavior-probes.log | 2344 | `AE2A5DF0713DF23E5F4D8FC2121048275CA9F3BF68F4B01942657457D65477A6` |

R2 历史失败证据保留：`new-behavior-probes.log` SHA256=`3F828BFE764BFACF6C66617ECCEFFD80D4833BFB493C02C986C1B2AE99518CEA`（3 failures）；R1 历史失败 SHA 见 §1.1。

---

## 5. 黄金覆盖与不足

- 300 胜例：yaku code/han、yakuHan、scoreHan、isYakuman、yakumanCount、fu、basePoints、capCode、PaymentPlan total、最终分数，并真实执行到终态。
- 100 无和例：全部 `NoWinningHand` 拒绝。
- RG-001：versionAfter/phaseAfter/finalScores/totalDeltas/resultOutcome/resultId 一致；TsumoWin/RoundEnded 存在。
- RG-002：finalScores/totalDeltas/winner/loser/versionAfterRon/ChankanRon 一致；未产生 KanDeclared/RinshanDrawn。
- 不足：随机样本无真实 round/window/河底/供托扣除；部分 dora/ura 与手牌实体冲突、特殊 flag 不可表示，已跳过并计数。24 条 settlementSamples 只验证既有 `SettlementService` 支付规则，不是新入口独立用例。RG-002 物理 addedTile 与冻结样本 19 不同（测试用 16，同为 5m 类型）。V4 `ScoreSettled` 在 C# Domain 无同名事件，不能逐字对照。全量 10,238 条不是本入口覆盖分母。

---

## 6. 后续范围缺口（未因本轮通过而关闭）

| ID | 级别 | 内容 |
|---|---|---|
| N2-GAP-1 | MAJOR | 未接入 `TableActor/TableRuntime/协议`；旧调用方仍可自带 settlement |
| N2-GAP-2 | MAJOR | 裸 `RoundCommand` 无 source fingerprint，直接执行绕过预校验 |
| N2-GAP-3 | MAJOR | C# Domain 无 `ScoreSettled`，N-3 需事件/投影定义 |
| N2-GAP-4 | MAJOR | 宿主事实认证：meld/riichi/ippatsu 时序/天和/地和/舍牌历史/首巡/振听均需可信记录 |
| N2-GAP-5 | MINOR | 多赢家逐 claim 重算；预计算第二 claim 为 stale |
| N2-GAP-6 | MINOR | 杠后新宝牌转移未在 map 建模 |
| N2-GAP-7 | INFO | RG-002 物理 addedTile 差异 |

R3 组件级已知 BLOCKER/MAJOR：**0**（D-2-01/02 已修复；D-1 保留修复仍有效）。上述 N2-GAP-1～4 是后续宿主/协议边界，不能写成 N-2 整体通过。

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

# R2 新探针（只读引用，不修改探针）
& D:/Unity/dotnet/dotnet.exe run --project C:/Users/sncdc/AppData/Local/Temp/n2-caller-r2-1792880422402/probes/Probes.csproj -c Release

# R1 旧探针回归
& D:/Unity/dotnet/dotnet.exe run --project C:/Users/sncdc/AppData/Local/Temp/n2-caller-review-848b1336aa8245819ccbccca1dbbedee/probes/Probes.csproj -c Release
```

未运行：真实 PG/WSS、Unity/IL2CPP、Android/iOS/真机、跨物理网络、远端 CI。未 push、未建 PR、未部署。V4 仍唯一生产；N-2 整体/G1/G5 未通过。
