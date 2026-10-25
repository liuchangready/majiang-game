# V5 N-3 子任务：scoring-v1 黄金 expected 字段绑定补齐

日期：2026-10-25。任务性质：G1 已导出算分黄金字段绑定补齐，**不是**新功能，**不是**完整 N-3 全字段审计的替代。
工作树：`D:/majiang-wt/n3-scoring-binding`；分支：`wt/v5-n3-scoring-binding`；冻结已接受 PR19 头：`b7c780269a3c240ccda9b8a0edb7d3ad3b7e7a9b`。
本交付只改三个文件：`ScoringGoldenTests.cs`、新增 `ScoringGoldenBindingTests.cs`、本文档。未改生产 Domain/Runtime/AI/Contracts/Wire，未改 fixture/exporter/catalog/manifest/统一 verifier/csproj，未改任何其他 GoldenTests，未复制或提交主 checkout 的 `CLAUDE.md`、`PROMPTS.md`、任务书、协议、Unity 环境/Resources/Burst 用户改动。
本批次未执行：push、创建 PR、merge、远端 CI、部署、修改默认分支或环境；未创建 `complete.flag`。V4 仍是唯一生产架构，G1 仍未批准，G5 未完成。

> **结论边界（必读）**：本交付只关闭 `tests/v5-golden/scoring-v1.json` 的 **scoring 消费者字段绑定**缺口。它不关闭完整 N-3 审计，不覆盖另外四个 fixture，不覆盖 20 个 partial，不覆盖 round/cheat/shuffle/AI/physical 的后续分配，不把 Scoring 组件 PASS 写成 G1/G5 PASS。

---

## 1. 已确认缺陷、复现与关闭结果

### 1.1 缺陷

`dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs::CompareWinSample` 读取真实 `WinningScoreEvaluator.Explain` 并逐字段比较，但**没有消费 `winSamples[*].expected.status`**。因此 fixture 可以把一个实际会和的样本标成 `noWin`，消费者仍然全绿。

修复前调用方在固定头 `b7c780269a3c240ccda9b8a0edb7d3ad3b7e7a9b` 构建后，仅把 TEMP 副本第一个 `"status":"win"` 改为 `"noWin"`，用 `MAHJONG_REPO_ROOT` 指向 TEMP，执行 Domain ScoringGoldenTests，得到 **7 passed / 0 failed / 0 skipped / exit 0**。原始证据保留，未改写：

- 日志：`C:/Users/sncdc/AppData/Local/Temp/codex-v5-progress-8df1baa3ba/scoring-status-mutated.log`
- 日志 SHA-256：`F7157139473CAAD70C7AB8BCEA65A0A1516FF79C146B3F11E167BD5E98D99318`
- 该次 TEMP mutant fixture：`.../scoring-mutant/tests/v5-golden/scoring-v1.json`
- mutant SHA-256：`87551C331F0DAE9B1496C74327DA8DBC7AFECAD347FC98BB60DC1BC33E03DBB4`（8,315,133 bytes）
- 实仓 `tests/v5-golden/scoring-v1.json` SHA-256：`53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E`（8,315,131 bytes；本任务前后一致，未改）

### 1.2 修复

`CompareWinSample` 现在在调用 `Explain` 之前读取并断言：

```csharp
Assert.Equal("win", expected.GetProperty("status").GetString());
```

随后仍要求 `Explain` 返回非 null（null 会抛 `InvalidOperationException`），其余 36 个 win expected leaf 的原断言全部保留。这样 `expected.status` 与实际 result 存在性/语义双向绑定，不是再手写一份 expected 常量，也不是只检查 fixture 内部关系。删除 status、类型错误（数字）、win→noWin 都会在真实比较路径失败。

同一 fixture 上，修复后只改第一个 win status 为 `noWin`，执行同一 `--filter-class '*ScoringGoldenTests'`：

```text
失败! - 失败: 1，通过: 6，已跳过: 0，总计: 7，exit 1
winSamples mismatches (first 20):
W-001: Assert.Equal() Failure: Strings differ
Expected: "win"
Actual:   "noWin"
```

原始日志：`C:/Users/sncdc/AppData/Local/Temp/v5-n3-scoring-binding/logs/status-win-to-nowin.xunit.log`，SHA-256 `A8CB248BBDAB1B7302EB87049BB90D2FF18032FA2B5EAC248E760D56B9F4F6A1`。这是真实的测试失败，不是 `Assert.Throws` 自测通过计数。

---

## 2. 全量枚举方法与 fixture 事实

使用独占 TEMP 脚本而非抽样：读取完整 JSON，按 section 遍历每个样本的每个对象/数组，数组归一化为 `[]`，得到 leaf path（标量/null 为叶），同时统计出现实例数。未使用“首中末”抽样。

- 枚举脚本：`C:/Users/sncdc/AppData/Local/Temp/v5-n3-scoring-binding/enumerate-scoring.js`
- 脚本 SHA-256：`3F8D4795CEAC88DA4B340D5292F883E57E88EC60ED9E294E60CC0D226B2284BC`
- 原始文本：`.../leaf-paths.txt`，SHA-256 `2A91AD3781FFF56FCAEF781D35A1C2B2FC79CF2D6D98D774C1CCDD4AC2330910`
- 机器 JSON：`.../leaf-paths.json`，SHA-256 `22B5B2C85492F361093AEF2268316837BE21EF5A38BBEACDA6BD3F36ABEC5166`

总计：**10,238 samples，249 个七 section leaf path，476,093 个 leaf instances**；另有 60 个 root metadata leaf path（各 1 次）。

| section | samples | leaf paths | leaf instances | expected output paths / instances | input paths / instances | metadata/provenance paths / instances |
|---|---:|---:|---:|---:|---:|---:|
| `winSamples` | 48 | 70 | 3,876 | 37 / 1,912 | 25 / 1,574 | 8 / 390 |
| `noWinSamples` | 4 | 31 | 166 | 1 / 4 | 23 / 134 | 7 / 28 |
| `fuSamples` | 9 | 26 | 346 | 6 / 76 | 13 / 207 | 7 / 63 |
| `doraSamples` | 6 | 22 | 141 | 7 / 42 | 8 / 57 | 7 / 42 |
| `settlementSamples` | 24 | 21 | 712 | 3 / 216 | 11 / 328 | 7 / 168 |
| `scoreSamples` | 147 | 40 | 6,321 | 31 / 4,998 | 3 / 441 | 6 / 882 |
| `randomSamples` | 10,000 | 39 | 464,531 | 11 / 76,532 | 23 / 337,999 | 5 / 50,000 |
| **合计** | **10,238** | **249** | **476,093** | **96 / 83,780** | **106 / 340,740** | **47 / 51,573** |

`expected` 的 96 个 leaf 已全部由 `ScoringGoldenBindingTests` 的声明集合逐路径核对，路径集合相等，不是只断言数量。完整 249 路径、实例数与分类见 §8 附录。

---

## 3. expected 输出绑定矩阵（96/96）

所有 actual 都来自真实 C# 生产代码；没有 fixture 自洽比较，也没有用目录 validator 代替算分行为。

### 3.1 `winSamples`（37）

消费者：`CompareWinSample` → `WinningScoreEvaluator.Explain`，以及 `CompareYaku` / `CompareDora` / `CompareFu` / `CompareScore` / `ComparePaymentPlan`。

| expected leaf | 准确断言 |
|---|---|
| `expected.status` | `Assert.Equal("win", status)`；`Explain` 返回 null 时抛异常 |
| `expected.yaku[].code` | 先 `Assert.Equal(expected.yaku.Length, result.Yaku.Count)`；逐项 `Assert.Equal(code, result.Yaku[i].Code)` |
| `expected.yaku[].displayName` | 逐项 `Assert.Equal(displayName, result.Yaku[i].DisplayName)` |
| `expected.yaku[].han` | 逐项 `Assert.Equal(han, result.Yaku[i].Han)` |
| `expected.totalHan` | `Assert.Equal(expected.totalHan, result.TotalYakuHan)` |
| `expected.scoreHan` | `Assert.Equal(expected.scoreHan, result.Score.Han)` |
| `expected.isYakuman` | `Assert.Equal(expected.isYakuman, result.Score.IsYakuman)` |
| `expected.yakumanCount` | `Assert.Equal(expected.yakumanCount, result.YakumanCount)` |
| `expected.dora.indicatorCount` | `Assert.Equal(expected, result.Dora.IndicatorCount)` |
| `expected.dora.uraCount` | `Assert.Equal(expected, result.Dora.UraCount)` |
| `expected.dora.redCount` | `Assert.Equal(expected, result.Dora.RedCount)` |
| `expected.dora.totalHan` | `Assert.Equal(expected, result.Dora.TotalHan)` |
| `expected.dora.indicatorTiles[]` | `Assert.Equal(ReadTileIds(expected), result.Dora.IndicatorTiles.ToArray())` |
| `expected.dora.uraTiles[]` | `Assert.Equal(ReadTileIds(expected), result.Dora.UraTiles.ToArray())` |
| `expected.dora.redTiles[]` | `Assert.Equal(ReadTileIds(expected), result.Dora.RedTiles.ToArray())` |
| `expected.fu.lines[].description` | 先断言行数；逐行 `Assert.Equal(description, result.Fu.Lines[i].Description)` |
| `expected.fu.lines[].fu` | 逐行 `Assert.Equal(fu, result.Fu.Lines[i].Fu)` |
| `expected.fu.subtotal` | `Assert.Equal(expected, result.Fu.Subtotal)` |
| `expected.fu.roundedUpTo` | `Assert.Equal(expected, result.Fu.RoundedUpTo)` |
| `expected.fu.finalFu` | `Assert.Equal(expected, result.Fu.FinalFu)` |
| `expected.fu.specialNote` | null/字符串后 `Assert.Equal(expected, result.Fu.SpecialNote)` |
| `expected.score.fu` | `Assert.Equal(expected, result.Score.Fu)` |
| `expected.score.han` | `Assert.Equal(expected, result.Score.Han)` |
| `expected.score.basePoints` | `Assert.Equal(expected, result.Score.BasePoints)` |
| `expected.score.rawBasePoints` | `Assert.Equal(expected, result.Score.RawBasePoints)` |
| `expected.score.rawBasePointsExact` | `Assert.Equal(expected, result.Score.RawBasePointsExact)` |
| `expected.score.capCode` | `Assert.Equal(expected, result.Score.CapCode)` |
| `expected.score.isYakuman` | `Assert.Equal(expected, result.Score.IsYakuman)` |
| `expected.score.yakumanMultiplier` | `Assert.Equal(expected, result.Score.YakumanMultiplier)` |
| `expected.paymentPlan.kind` | `Assert.Equal(expected, actual.KindCode)` |
| `expected.paymentPlan.components[].payerRole` | 先断言组件数；逐项 `Assert.Equal(payerRole, RoleCode(actual.Components[i].PayerRole))` |
| `expected.paymentPlan.components[].amount` | 逐项 `Assert.Equal(amount, actual.Components[i].Amount)` |
| `expected.paymentPlan.components[].count` | 逐项 `Assert.Equal(count, actual.Components[i].Count)` |
| `expected.paymentPlan.total` | `Assert.Equal(expected, actual.Total)` |
| `expected.honbaAddition` | `Assert.Equal(expected, result.HonbaAddition)` |
| `expected.riichiAddition` | `Assert.Equal(expected, result.RiichiAddition)` |
| `expected.totalPoints` | `Assert.Equal(expected, result.TotalPoints)` |

### 3.2 `noWinSamples`（1）

| expected leaf | 准确断言 |
|---|---|
| `expected.status` | `Assert.Equal("noWin", status)`；随后 `WinningScoreEvaluator.Evaluate(...)` 必须返回 null |

### 3.3 `fuSamples`（6）

消费者：`CompareFuSample` → `FuCalculator.CalculateBreakdown`，再走 `CompareFu`。

| expected leaf | 准确断言 |
|---|---|
| `expected.lines[].description` | 行数 + 逐行 description |
| `expected.lines[].fu` | 逐行 fu |
| `expected.subtotal` | `Assert.Equal` |
| `expected.roundedUpTo` | `Assert.Equal` |
| `expected.finalFu` | `Assert.Equal` |
| `expected.specialNote` | null/字符串 `Assert.Equal` |

### 3.4 `doraSamples`（7）

消费者：`CompareDoraSample` → `WinningScoreEvaluator.BreakdownDora`，再走 `CompareDora`。

| expected leaf | 准确断言 |
|---|---|
| `expected.indicatorCount` | `Assert.Equal` |
| `expected.uraCount` | `Assert.Equal` |
| `expected.redCount` | `Assert.Equal` |
| `expected.totalHan` | `Assert.Equal` |
| `expected.indicatorTiles[]` | 完整数组 `Assert.Equal` |
| `expected.uraTiles[]` | 完整数组 `Assert.Equal` |
| `expected.redTiles[]` | 完整数组 `Assert.Equal` |

### 3.5 `settlementSamples`（3）

消费者：`CompareSettlementSample` → `SettlementService.Ron/Tsumo/ExhaustiveDraw/AbortiveDraw`。

| expected leaf | 准确断言 |
|---|---|
| `expected.deltas[]` | `Assert.Equal(expected.deltas, actual.Deltas.ToArray())` |
| `expected.newScores[]` | `Assert.Equal(expected.newScores, actual.NewScores.ToArray())` |
| `expected.remainingRiichiSticks` | `Assert.Equal(expected, actual.RemainingRiichiSticks)` |

### 3.6 `scoreSamples`（31）

消费者：`CompareScoreSample` → `ScoreCalculator.Calculate`，再走 `CompareScore` / `ComparePaymentPlan`。

| expected leaf | 准确断言 |
|---|---|
| `expected.basePoints` | `Assert.Equal(expected, actual.BasePoints)` |
| `expected.rawBasePoints` | `Assert.Equal(expected, actual.RawBasePoints)` |
| `expected.rawBasePointsExact` | `Assert.Equal(expected, actual.RawBasePointsExact)` |
| `expected.capCode` | `Assert.Equal(expected, actual.CapCode)` |
| `expected.isYakuman` | `Assert.Equal(expected, actual.IsYakuman)` |
| `expected.yakumanMultiplier` | `Assert.Equal(expected, actual.YakumanMultiplier)` |
| `expected.payments.dealerTsumo` | `Assert.Equal(expected, actual.DealerTsumoPayment())` |
| `expected.payments.nonDealerTsumoDealer` | `Assert.Equal(expected, actual.NonDealerTsumoPayment().Dealer)` |
| `expected.payments.nonDealerTsumoOther` | `Assert.Equal(expected, actual.NonDealerTsumoPayment().Other)` |
| `expected.payments.dealerRon` | `Assert.Equal(expected, actual.DealerRonPayment())` |
| `expected.payments.nonDealerRon` | `Assert.Equal(expected, actual.NonDealerRonPayment())` |
| `expected.paymentPlans.{dealerTsumo,nonDealerTsumo,dealerRon,nonDealerRon}.kind` | 四个 plan 分别 `Assert.Equal(expected, actual.KindCode)` |
| 上述四个 plan 的 `components[].payerRole` | 四个 plan 分别断言组件数并逐项 `RoleCode` |
| 上述四个 plan 的 `components[].amount` | 四个 plan 分别逐项 `Assert.Equal` |
| 上述四个 plan 的 `components[].count` | 四个 plan 分别逐项 `Assert.Equal` |
| 上述四个 plan 的 `total` | 四个 plan 分别 `Assert.Equal` |

四个 plan 的 5 个 leaf 共 20 个路径，均在上表 5 行中逐一覆盖，路径集合测试会阻止新增 plan 未分类。

### 3.7 `randomSamples`（11）

消费者：`CompareRandomSample` → `WinningScoreEvaluator.Explain`；10,000 条全量，不是抽样。

| expected leaf | 准确断言 |
|---|---|
| `expected.status` | `"noWin"` → result 必须 null；`"win"` → result 非 null；其他值失败 |
| `expected.yaku[].code` | yaku 数量 + 逐项 `Assert.Equal(code, actual.Yaku[i].Code)` |
| `expected.yaku[].han` | 逐项 `Assert.Equal(han, actual.Yaku[i].Han)` |
| `expected.yakuHan` | `Assert.Equal(expected, actual.TotalYakuHan)` |
| `expected.scoreHan` | `Assert.Equal(expected, actual.Score.Han)` |
| `expected.isYakuman` | `Assert.Equal(expected, actual.Score.IsYakuman)` |
| `expected.yakumanCount` | `Assert.Equal(expected, actual.YakumanCount)` |
| `expected.fu` | `Assert.Equal(expected, actual.Fu.FinalFu)` |
| `expected.basePoints` | `Assert.Equal(expected, actual.Score.BasePoints)` |
| `expected.capCode` | `Assert.Equal(expected, actual.Score.CapCode)` |
| `expected.totalPoints` | `Assert.Equal(expected, actual.PaymentPlan.Total)` |

---

## 4. 输入绑定、语义与不可比较项

输入字段按真实 reader/构造参数绑定；**不要求所有等价输入变动都失败**，也未为输入新增常量断言。

| section | 输入 leaf（数量） | 真实消费者 |
|---|---|---|
| win | `closedTiles[]`、`drawnTile`（2） | `ReadHand` → `PlayerHand` |
| win | `context.*`（14） | `ReadContext` → `WinContext` |
| win | `melds[].type/tiles[]/fromPlayer/calledTile`（4） | `ReadMeld` → `Meld` |
| win | `doraIndicators[]`、`uraDoraIndicators[]`（2） | `ReadTileIds` → `Explain` |
| win | `honba`、`riichiSticks`（2） | `Explain` 参数 |
| win | `extraHan`（1） | **未消费**：exporter 对 win 恒写 0，`Explain` 没有该参数；非生产行为字段 |
| noWin | `closedTiles[]`、`drawnTile`（2） | `ReadHand` |
| noWin | `context.*`（14） | `ReadContext` |
| noWin | `melds[].type/tiles[]/fromPlayer/calledTile`（4） | `ReadMeld` |
| noWin | `extraHan`（1） | `Evaluate(extraHan:)` |
| noWin | `honba`、`riichiSticks`（2） | 值恒 0；`Evaluate` 无这些参数 |
| fu | `decomposition.pattern/jantai/mentsu[].typeIndices[]/mentsu[].isSequence`（4） | `ReadDecomposition` |
| fu | `winTile`、`isTsumo`、`isClosed`、`seatWind`、`roundWind`（5） | `FuCalculator.CalculateBreakdown` 参数 |
| fu | `openMelds[].type/tiles[]/fromPlayer/calledTile`（4） | `ReadMeld` |
| dora | `closedTiles[]`、`drawnTile`（2） | `PlayerHand` |
| dora | `melds[].type/tiles[]/fromPlayer/calledTile`（4） | `ReadMeld` |
| dora | `doraIndicators[]`、`uraDoraIndicators[]`（2） | `BreakdownDora` 参数 |
| settlement | `settlementKind`、`scores[]`、`dealer`、`honba`、`riichiSticks`、`winner`、`loser`、`tenpai`/`tenpai[]`、`fu`、`han`（11） | `SettlementInput` + `SettlementService` 分支 + `ScoreCalculator.Calculate` |
| score | `fu`、`han`、`yakumanCount`（3） | `ScoreCalculator.Calculate` |
| random | `closed[]`、`drawn`（2） | `PlayerHand` |
| random | `context.*`（14） | `ReadContext` |
| random | `melds[].type/tiles[]/fromPlayer/calledTile`（4） | `ReadMeld` |
| random | `dora[]`、`ura[]`（2） | `Explain` 参数 |
| random | `removedWinTile`（1） | 分布计数输入；真实评分仍由 `Explain` 计算 |

不可比较/未覆盖说明：

1. `win.input.extraHan` 在全部 48 条中恒为 0，且 `Explain` 无此参数；它不是漏绑定的 expected 输出，不新增常量断言。
2. `noWin` 的 `doraIndicators[]`/`uraDoraIndicators[]` 是空数组（无 leaf 实例），`honba`/`riichiSticks` 恒 0，`Evaluate` 也没有这些参数；不能据此声称缺失输出绑定。
3. 所有导出样本的 `drawnTile`/`drawn` 均为 null；`ReadHand` 支持非 null，但 fixture 没有非 null 实例。这是**输入覆盖限制**，不是 expected 未绑定；未在本任务伪造非 null 样本。
4. `melds[].fromPlayer`、`calledTile` 被读入 `Meld`，部分产出可能不随其变化；输入结构绑定不等于每个值都必须导致失败，本任务没有用输入 mutation 凑数。
5. `kind`、`note`、`tags[]`、`source.*` 是生成器溯源/说明；`id` 仅用于失败定位。它们不是生产算分输出，未新增 constant oracle 断言。
6. `extractionStatus`、schema、rulesetVersion、v4Commit、generationResult、randomMeta 的聚合计数由既有 provenance/分布测试消费；不是单个样本的输出差分。

---

## 5. 新增定向测试与自测矩阵

新增 `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenBindingTests.cs`，11 个 fact：

1. `ExpectedOutputLeafPathSetIsExactlyClassified`：核对七 section 的实际 `expected` leaf 集合与声明集合完全相等；断言 samples=10,238、declared=96、actual=96，缺失/未分类均失败。
2. `PristineFixturePassesThroughTheBindingHarness`：把原 fixture 全部 10,238 条经同一比较入口跑一遍，防止“harness 永远抛异常”造成 mutation 假绿。
3-9. 七 section 各自的 `*ExpectedOutputsAreBehaviorallyBound`：对该 section 的每个 expected leaf 取首个含该路径的样本，做类型保持的错误值替换（数字 +1、字符串加后缀、bool 取反、null→字符串、status win/noWin 互换），再调用 `CompareSampleForBindingTests`，要求真实比较路径抛出异常；未被拒绝即失败。
10. `WinStatusExpectationRejectsDeletionWrongTypeAndInversion`：删除 status、数字 status、win→noWin 三种编辑必须失败。
11. `WinTotalPointsExpectationRejectsDeletionWrongTypeAndOffset`：删除 totalPoints、字符串 totalPoints、+1 偏移必须失败。

同一比较入口由 `ScoringGoldenTests` 的 internal `CompareSampleForBindingTests` 分发到原有 private `Compare*Sample`；没有复制第二套断言，也没有把预期失败写成正常通过。

**in-test mutation 计数**：96 个 expected leaf 逐路径注入，全部被拒绝；另有 6 个显式删除/类型/偏移编辑被拒绝。这 11 个 fact 是**自测成功计数**，与 §6 外部错值注入的**失败计数**分开记录。

---

## 6. 验证命令、真实计数与外部错值注入

全部命令使用 `D:/Unity/dotnet/dotnet.exe`；先构建成功再测试，避免旧 DLL。

| 验证 | 命令 | 实际结果 | native exit |
|---|---|---|---:|
| solution Release build | `D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental` | 12 项目，**0 warnings / 0 errors** | 0 |
| Domain 全量 | `... test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build` | **385 passed / 0 failed / 0 skipped / 385 total** | 0 |
| 原 ScoringGoldenTests | 同一 csproj，`-- --filter-class '*ScoringGoldenTests'` | **7 passed / 0 failed / 0 skipped / 7 total** | 0 |
| 新增 BindingTests | 同一 csproj，`-- --filter-class '*ScoringGoldenBindingTests'` | **11 passed / 0 failed / 0 skipped / 11 total** | 0 |
| 两类合并 | `-- --filter-class '*ScoringGolden*'` | **18 passed / 0 failed / 0 skipped / 18 total** | 0 |

Domain 集成基线 374，本任务只增 11 个测试、不改生产，385=374+11。

### 6.1 外部 TEMP fixture 错值注入（真实失败）

每次把实仓 fixture 复制到独占 TEMP，用 `mutate-scoring.js` 修改，`MAHJONG_REPO_ROOT` 指向 TEMP，运行原 `*ScoringGoldenTests`（7 facts）。实仓 fixture 未写。所有失败均为 xUnit 真实断言失败，不是 `Assert.Throws`。

| mode | 注入 | 总计 | failed | passed | skipped | exit |
|---|---|---:|---:|---:|---:|---:|
| `status-win-to-nowin` | win[0].expected.status = `noWin` | 7 | 1 | 6 | 0 | 1 |
| `status-nowin-to-win` | noWin[0].expected.status = `win` | 7 | 1 | 6 | 0 | 1 |
| `status-delete` | 删除 win[0].expected.status | 7 | 1 | 6 | 0 | 1 |
| `status-number` | win[0].expected.status = 123 | 7 | 1 | 6 | 0 | 1 |
| `totalpoints-delete` | 删除 win[0].expected.totalPoints | 7 | 1 | 6 | 0 | 1 |
| `totalpoints-number` | totalPoints = `"1000"` | 7 | 1 | 6 | 0 | 1 |
| `totalpoints-offset` | totalPoints += 1 | 7 | 1 | 6 | 0 | 1 |
| `all-sections` | 七 section 各注入一个代表性 expected 错值 | 7 | 6 | 1 | 0 | 1 |

`all-sections` 的 6 个失败分别对应 Dora、Settlement、Random、Score、Fu、Curated(win+noWin 同一 fact)；第 7 个 provenance fact 不受影响而通过。win 与 noWin 共用 `CuratedWinAndNoWinSamplesMatchV4`，所以合并注入时 win 断言先失败、noWin 被遮蔽；`status-nowin-to-win` 单独运行证明 noWin 分支也真实失败：

```text
noWinSamples mismatches (first 20):
N-001: Assert.Equal() Failure: Strings differ
Expected: "noWin"
Actual:   "win"
```

外部证据（独占 TEMP；所有日志均为 xUnit 原始 runner 日志）：

| 文件 | SHA-256 |
|---|---|
| `logs/status-win-to-nowin.xunit.log` | `A8CB248BBDAB1B7302EB87049BB90D2FF18032FA2B5EAC248E760D56B9F4F6A1` |
| `logs/status-nowin-to-win.xunit.log` | `9BDE6721A66A1D40A5A46B8C8354F2C45437A162E86A58F35C55751DA8401FD9` |
| `logs/status-delete.xunit.log` | `4926A80703AE453B43301B580BAF6BF7A45708287A103E09D4D406DC24440E88` |
| `logs/status-number.xunit.log` | `DE8B46C0177348699701C01293B506038A6028CB8395DCAE4AB85C5135D8F162` |
| `logs/totalpoints-delete.xunit.log` | `5E6C030DEEB3812371714A272BA5BB8051F7B94FE61871C6A3AC01DE690AED73` |
| `logs/totalpoints-number.xunit.log` | `D4AFA750561936F4A05BEF2B06629F3A175315A44D53D443F70B824E811E86D5` |
| `logs/totalpoints-offset.xunit.log` | `DED4AB519DEE227BD4643E8697F5907917FB42F49DF18A7B25658B3650C62C30` |
| `logs/all-sections.xunit.log` | `2F9F9812E73AA3AE7F5A93FD7301D3E65527D80407DA32587DF079C4732159B8` |
| `mutate-scoring.js` | `42363808B60D0A8788B61905EFB16AA52F58F4A64D5CEC643F8048E621EC119C` |
| `solution-build.log` | `F0BFE7F334CE1A8760E207D6C66510719E479411926ABB196CA43D5B5A464865` |

---

## 7. 交付边界与 PASS/FAIL/BLOCKED/NOT_RUN

### 7.1 文件清单

| 文件 | 变更 |
|---|---|
| `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` | `CompareWinSample` 增加 `expected.status` 断言；新增 internal binding harness（仍分发到原比较函数） |
| `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenBindingTests.cs`（新） | 96 expected leaf 路径集合、原 fixture 正控、七 section 逐 leaf mutation、status/totalPoints 删除/类型/偏移负例 |
| `doc/v5-重构/V5-N3-SCORING-GOLDEN-BINDING.md`（新） | 本报告与完整枚举附录 |

未改：生产 `Domain/Runtime/AI/Contracts/Wire`、`tests/v5-golden/scoring-v1.json`、exporter、catalog、manifest、统一 verifier、csproj、其他 GoldenTests、冻结 G1 报告、用户未提交文件。

### 7.2 结论

| 状态 | 内容 |
|---|---|
| **PASS** | scoring-v1 七 section 全量 leaf 枚举与分类；96/96 expected output leaf 有真实 C# 比较；win `expected.status` 与 result 存在性/语义绑定；原 fixture 7/7 + 11/11 + Domain 385/385；solution Release 0 warning/0 error；八种外部 TEMP 错值注入均真实失败；`git diff --check` 无输出 |
| **FAIL** | 本任务范围内无未修复失败。修复前的历史 `status` mutant 曾 7/7 全绿，已作为缺陷证据保留，修复后同 mutant 1 failed/6 passed。 |
| **BLOCKED** | 无（本任务文件与工具链可用） |
| **NOT_RUN** | 完整 N-3 审计；scoring 之外的 fixture（ai/cheat/physical/round/shuffle/catalog）；20 partial；round/cheat/shuffle/AI/physical 后续补齐；PG/WSS/Unity/IL2CPP/真机重平台；G1/G5 验收。未把这些写成通过。 |

**Scoring 组件 PASS ≠ N-3 全量 PASS ≠ G1 PASS ≠ G5 完成。** 完整 N-3 审计仍待其 owner/后续分配；本任务不刷新冻结 N3 审计，不消去历史缺口，不改在途 N1b/N3 文件。

owner 边界：N1b（Domain/Runtime 出千执行层）由 `thread_20261025_061655_e4bd` 的 DeepSeek 原线程 round 3 返修；N3 全量字段审计由 `thread_20261025_051932_5eeb` 的 GLM 原线程 round 3 返修冻结头 `8254c9b`。本任务未读取、未合并、未修改这两个线程的任何代码/报告；本任务从 caller 独立复现的 scoring 缺陷出发，只关闭 scoring `expected` 绑定，不声称关闭整个 N3。

---

## 8. 附录：七 section 全量归一化 leaf path 机器集合

说明：`[]` 表示任意数组层级；`instances` 是该路径在全部样本中的 leaf 出现次数（不是样本数）。`class` 为 `output`（expected 生产输出）、`input`（生产输入）或 `metadata/provenance`（结构/溯源）。根级 60 个 metadata path 单列于本节末尾。

#### winSamples (48 samples, 70 leaf paths, 3876 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `winSamples[].expected.dora.indicatorCount` | 48 | output |
| `winSamples[].expected.dora.indicatorTiles[]` | 4 | output |
| `winSamples[].expected.dora.redCount` | 48 | output |
| `winSamples[].expected.dora.redTiles[]` | 1 | output |
| `winSamples[].expected.dora.totalHan` | 48 | output |
| `winSamples[].expected.dora.uraCount` | 48 | output |
| `winSamples[].expected.dora.uraTiles[]` | 1 | output |
| `winSamples[].expected.fu.finalFu` | 48 | output |
| `winSamples[].expected.fu.lines[].description` | 134 | output |
| `winSamples[].expected.fu.lines[].fu` | 134 | output |
| `winSamples[].expected.fu.roundedUpTo` | 48 | output |
| `winSamples[].expected.fu.specialNote` | 48 | output |
| `winSamples[].expected.fu.subtotal` | 48 | output |
| `winSamples[].expected.honbaAddition` | 48 | output |
| `winSamples[].expected.isYakuman` | 48 | output |
| `winSamples[].expected.paymentPlan.components[].amount` | 53 | output |
| `winSamples[].expected.paymentPlan.components[].count` | 53 | output |
| `winSamples[].expected.paymentPlan.components[].payerRole` | 53 | output |
| `winSamples[].expected.paymentPlan.kind` | 48 | output |
| `winSamples[].expected.paymentPlan.total` | 48 | output |
| `winSamples[].expected.riichiAddition` | 48 | output |
| `winSamples[].expected.score.basePoints` | 48 | output |
| `winSamples[].expected.score.capCode` | 48 | output |
| `winSamples[].expected.score.fu` | 48 | output |
| `winSamples[].expected.score.han` | 48 | output |
| `winSamples[].expected.score.isYakuman` | 48 | output |
| `winSamples[].expected.score.rawBasePoints` | 48 | output |
| `winSamples[].expected.score.rawBasePointsExact` | 48 | output |
| `winSamples[].expected.score.yakumanMultiplier` | 48 | output |
| `winSamples[].expected.scoreHan` | 48 | output |
| `winSamples[].expected.status` | 48 | output |
| `winSamples[].expected.totalHan` | 48 | output |
| `winSamples[].expected.totalPoints` | 48 | output |
| `winSamples[].expected.yaku[].code` | 77 | output |
| `winSamples[].expected.yaku[].displayName` | 77 | output |
| `winSamples[].expected.yaku[].han` | 77 | output |
| `winSamples[].expected.yakumanCount` | 48 | output |
| `winSamples[].extractionStatus` | 48 | metadata/provenance |
| `winSamples[].id` | 48 | metadata/provenance |
| `winSamples[].input.closedTiles[]` | 644 | input |
| `winSamples[].input.context.isChankan` | 48 | input |
| `winSamples[].input.context.isChiihou` | 48 | input |
| `winSamples[].input.context.isClosed` | 48 | input |
| `winSamples[].input.context.isDoubleRiichi` | 48 | input |
| `winSamples[].input.context.isHaitei` | 48 | input |
| `winSamples[].input.context.isHoutei` | 48 | input |
| `winSamples[].input.context.isIppatsu` | 48 | input |
| `winSamples[].input.context.isRiichi` | 48 | input |
| `winSamples[].input.context.isRinshan` | 48 | input |
| `winSamples[].input.context.isTenhou` | 48 | input |
| `winSamples[].input.context.isTsumo` | 48 | input |
| `winSamples[].input.context.roundWind` | 48 | input |
| `winSamples[].input.context.seatWind` | 48 | input |
| `winSamples[].input.context.winTile` | 48 | input |
| `winSamples[].input.doraIndicators[]` | 3 | input |
| `winSamples[].input.drawnTile` | 48 | input |
| `winSamples[].input.extraHan` | 48 | input |
| `winSamples[].input.honba` | 48 | input |
| `winSamples[].input.melds[].calledTile` | 9 | input |
| `winSamples[].input.melds[].fromPlayer` | 9 | input |
| `winSamples[].input.melds[].tiles[]` | 34 | input |
| `winSamples[].input.melds[].type` | 9 | input |
| `winSamples[].input.riichiSticks` | 48 | input |
| `winSamples[].input.uraDoraIndicators[]` | 2 | input |
| `winSamples[].kind` | 48 | metadata/provenance |
| `winSamples[].note` | 48 | metadata/provenance |
| `winSamples[].source.file` | 48 | metadata/provenance |
| `winSamples[].source.note` | 48 | metadata/provenance |
| `winSamples[].source.testClass` | 48 | metadata/provenance |
| `winSamples[].tags[]` | 54 | metadata/provenance |

#### noWinSamples (4 samples, 31 leaf paths, 166 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `noWinSamples[].expected.status` | 4 | output |
| `noWinSamples[].extractionStatus` | 4 | metadata/provenance |
| `noWinSamples[].id` | 4 | metadata/provenance |
| `noWinSamples[].input.closedTiles[]` | 50 | input |
| `noWinSamples[].input.context.isChankan` | 4 | input |
| `noWinSamples[].input.context.isChiihou` | 4 | input |
| `noWinSamples[].input.context.isClosed` | 4 | input |
| `noWinSamples[].input.context.isDoubleRiichi` | 4 | input |
| `noWinSamples[].input.context.isHaitei` | 4 | input |
| `noWinSamples[].input.context.isHoutei` | 4 | input |
| `noWinSamples[].input.context.isIppatsu` | 4 | input |
| `noWinSamples[].input.context.isRiichi` | 4 | input |
| `noWinSamples[].input.context.isRinshan` | 4 | input |
| `noWinSamples[].input.context.isTenhou` | 4 | input |
| `noWinSamples[].input.context.isTsumo` | 4 | input |
| `noWinSamples[].input.context.roundWind` | 4 | input |
| `noWinSamples[].input.context.seatWind` | 4 | input |
| `noWinSamples[].input.context.winTile` | 4 | input |
| `noWinSamples[].input.drawnTile` | 4 | input |
| `noWinSamples[].input.extraHan` | 4 | input |
| `noWinSamples[].input.honba` | 4 | input |
| `noWinSamples[].input.melds[].calledTile` | 2 | input |
| `noWinSamples[].input.melds[].fromPlayer` | 2 | input |
| `noWinSamples[].input.melds[].tiles[]` | 6 | input |
| `noWinSamples[].input.melds[].type` | 2 | input |
| `noWinSamples[].input.riichiSticks` | 4 | input |
| `noWinSamples[].kind` | 4 | metadata/provenance |
| `noWinSamples[].note` | 4 | metadata/provenance |
| `noWinSamples[].source.file` | 4 | metadata/provenance |
| `noWinSamples[].source.note` | 4 | metadata/provenance |
| `noWinSamples[].source.testClass` | 4 | metadata/provenance |

#### fuSamples (9 samples, 26 leaf paths, 346 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `fuSamples[].expected.finalFu` | 9 | output |
| `fuSamples[].expected.lines[].description` | 20 | output |
| `fuSamples[].expected.lines[].fu` | 20 | output |
| `fuSamples[].expected.roundedUpTo` | 9 | output |
| `fuSamples[].expected.specialNote` | 9 | output |
| `fuSamples[].expected.subtotal` | 9 | output |
| `fuSamples[].extractionStatus` | 9 | metadata/provenance |
| `fuSamples[].id` | 9 | metadata/provenance |
| `fuSamples[].input.decomposition.jantai` | 9 | input |
| `fuSamples[].input.decomposition.mentsu[].isSequence` | 26 | input |
| `fuSamples[].input.decomposition.mentsu[].typeIndices[]` | 78 | input |
| `fuSamples[].input.decomposition.pattern` | 9 | input |
| `fuSamples[].input.isClosed` | 9 | input |
| `fuSamples[].input.isTsumo` | 9 | input |
| `fuSamples[].input.openMelds[].calledTile` | 6 | input |
| `fuSamples[].input.openMelds[].fromPlayer` | 6 | input |
| `fuSamples[].input.openMelds[].tiles[]` | 22 | input |
| `fuSamples[].input.openMelds[].type` | 6 | input |
| `fuSamples[].input.roundWind` | 9 | input |
| `fuSamples[].input.seatWind` | 9 | input |
| `fuSamples[].input.winTile` | 9 | input |
| `fuSamples[].kind` | 9 | metadata/provenance |
| `fuSamples[].note` | 9 | metadata/provenance |
| `fuSamples[].source.file` | 9 | metadata/provenance |
| `fuSamples[].source.note` | 9 | metadata/provenance |
| `fuSamples[].source.testClass` | 9 | metadata/provenance |

#### doraSamples (6 samples, 22 leaf paths, 141 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `doraSamples[].expected.indicatorCount` | 6 | output |
| `doraSamples[].expected.indicatorTiles[]` | 12 | output |
| `doraSamples[].expected.redCount` | 6 | output |
| `doraSamples[].expected.redTiles[]` | 4 | output |
| `doraSamples[].expected.totalHan` | 6 | output |
| `doraSamples[].expected.uraCount` | 6 | output |
| `doraSamples[].expected.uraTiles[]` | 2 | output |
| `doraSamples[].extractionStatus` | 6 | metadata/provenance |
| `doraSamples[].id` | 6 | metadata/provenance |
| `doraSamples[].input.closedTiles[]` | 30 | input |
| `doraSamples[].input.doraIndicators[]` | 7 | input |
| `doraSamples[].input.drawnTile` | 6 | input |
| `doraSamples[].input.melds[].calledTile` | 2 | input |
| `doraSamples[].input.melds[].fromPlayer` | 2 | input |
| `doraSamples[].input.melds[].tiles[]` | 6 | input |
| `doraSamples[].input.melds[].type` | 2 | input |
| `doraSamples[].input.uraDoraIndicators[]` | 2 | input |
| `doraSamples[].kind` | 6 | metadata/provenance |
| `doraSamples[].note` | 6 | metadata/provenance |
| `doraSamples[].source.file` | 6 | metadata/provenance |
| `doraSamples[].source.note` | 6 | metadata/provenance |
| `doraSamples[].source.testClass` | 6 | metadata/provenance |

#### settlementSamples (24 samples, 21 leaf paths, 712 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `settlementSamples[].expected.deltas[]` | 96 | output |
| `settlementSamples[].expected.newScores[]` | 96 | output |
| `settlementSamples[].expected.remainingRiichiSticks` | 24 | output |
| `settlementSamples[].extractionStatus` | 24 | metadata/provenance |
| `settlementSamples[].id` | 24 | metadata/provenance |
| `settlementSamples[].input.dealer` | 24 | input |
| `settlementSamples[].input.fu` | 24 | input |
| `settlementSamples[].input.han` | 24 | input |
| `settlementSamples[].input.honba` | 24 | input |
| `settlementSamples[].input.loser` | 24 | input |
| `settlementSamples[].input.riichiSticks` | 24 | input |
| `settlementSamples[].input.scores[]` | 96 | input |
| `settlementSamples[].input.settlementKind` | 24 | input |
| `settlementSamples[].input.tenpai` | 8 | input |
| `settlementSamples[].input.tenpai[]` | 32 | input |
| `settlementSamples[].input.winner` | 24 | input |
| `settlementSamples[].kind` | 24 | metadata/provenance |
| `settlementSamples[].note` | 24 | metadata/provenance |
| `settlementSamples[].source.file` | 24 | metadata/provenance |
| `settlementSamples[].source.note` | 24 | metadata/provenance |
| `settlementSamples[].source.testClass` | 24 | metadata/provenance |

#### scoreSamples (147 samples, 40 leaf paths, 6321 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `scoreSamples[].expected.basePoints` | 147 | output |
| `scoreSamples[].expected.capCode` | 147 | output |
| `scoreSamples[].expected.isYakuman` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerRon.components[].amount` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerRon.components[].count` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerRon.components[].payerRole` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerRon.kind` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerRon.total` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerTsumo.components[].amount` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerTsumo.components[].count` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerTsumo.components[].payerRole` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerTsumo.kind` | 147 | output |
| `scoreSamples[].expected.paymentPlans.dealerTsumo.total` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerRon.components[].amount` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerRon.components[].count` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerRon.components[].payerRole` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerRon.kind` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerRon.total` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerTsumo.components[].amount` | 294 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerTsumo.components[].count` | 294 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerTsumo.components[].payerRole` | 294 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerTsumo.kind` | 147 | output |
| `scoreSamples[].expected.paymentPlans.nonDealerTsumo.total` | 147 | output |
| `scoreSamples[].expected.payments.dealerRon` | 147 | output |
| `scoreSamples[].expected.payments.dealerTsumo` | 147 | output |
| `scoreSamples[].expected.payments.nonDealerRon` | 147 | output |
| `scoreSamples[].expected.payments.nonDealerTsumoDealer` | 147 | output |
| `scoreSamples[].expected.payments.nonDealerTsumoOther` | 147 | output |
| `scoreSamples[].expected.rawBasePoints` | 147 | output |
| `scoreSamples[].expected.rawBasePointsExact` | 147 | output |
| `scoreSamples[].expected.yakumanMultiplier` | 147 | output |
| `scoreSamples[].extractionStatus` | 147 | metadata/provenance |
| `scoreSamples[].id` | 147 | metadata/provenance |
| `scoreSamples[].input.fu` | 147 | input |
| `scoreSamples[].input.han` | 147 | input |
| `scoreSamples[].input.yakumanCount` | 147 | input |
| `scoreSamples[].kind` | 147 | metadata/provenance |
| `scoreSamples[].source.file` | 147 | metadata/provenance |
| `scoreSamples[].source.note` | 147 | metadata/provenance |
| `scoreSamples[].source.testClass` | 147 | metadata/provenance |

#### randomSamples (10000 samples, 39 leaf paths, 464531 leaf instances)

| normalized leaf path | instances | class |
|---|---:|---|
| `randomSamples[].closed[]` | 112046 | input |
| `randomSamples[].context.isChankan` | 10000 | input |
| `randomSamples[].context.isChiihou` | 10000 | input |
| `randomSamples[].context.isClosed` | 10000 | input |
| `randomSamples[].context.isDoubleRiichi` | 10000 | input |
| `randomSamples[].context.isHaitei` | 10000 | input |
| `randomSamples[].context.isHoutei` | 10000 | input |
| `randomSamples[].context.isIppatsu` | 10000 | input |
| `randomSamples[].context.isRiichi` | 10000 | input |
| `randomSamples[].context.isRinshan` | 10000 | input |
| `randomSamples[].context.isTenhou` | 10000 | input |
| `randomSamples[].context.isTsumo` | 10000 | input |
| `randomSamples[].context.roundWind` | 10000 | input |
| `randomSamples[].context.seatWind` | 10000 | input |
| `randomSamples[].context.winTile` | 10000 | input |
| `randomSamples[].dora[]` | 14831 | input |
| `randomSamples[].drawn` | 10000 | input |
| `randomSamples[].expected.basePoints` | 5971 | output |
| `randomSamples[].expected.capCode` | 5971 | output |
| `randomSamples[].expected.fu` | 5971 | output |
| `randomSamples[].expected.isYakuman` | 5971 | output |
| `randomSamples[].expected.scoreHan` | 5971 | output |
| `randomSamples[].expected.status` | 10000 | output |
| `randomSamples[].expected.totalPoints` | 5971 | output |
| `randomSamples[].expected.yaku[].code` | 9382 | output |
| `randomSamples[].expected.yaku[].han` | 9382 | output |
| `randomSamples[].expected.yakuHan` | 5971 | output |
| `randomSamples[].expected.yakumanCount` | 5971 | output |
| `randomSamples[].extractionStatus` | 10000 | metadata/provenance |
| `randomSamples[].id` | 10000 | metadata/provenance |
| `randomSamples[].melds[].calledTile` | 8319 | input |
| `randomSamples[].melds[].fromPlayer` | 8319 | input |
| `randomSamples[].melds[].tiles[]` | 24957 | input |
| `randomSamples[].melds[].type` | 8319 | input |
| `randomSamples[].removedWinTile` | 10000 | input |
| `randomSamples[].source.file` | 10000 | metadata/provenance |
| `randomSamples[].source.note` | 10000 | metadata/provenance |
| `randomSamples[].source.testClass` | 10000 | metadata/provenance |
| `randomSamples[].ura[]` | 1208 | input |


### 8.8 根级 metadata（60 leaf paths，各 1 次；全部 `metadata/provenance`）

```text
$.schema
$.generator
$.generatorVersion
$.v4Commit
$.rulesetVersion
$.extractionStatus
$.generatedByCommand
$.exportedAtNote
$.v4RulesSourceNote
$.unexportedSamples
$.generationResult.winSamples
$.generationResult.noWinSamples
$.generationResult.fuSamples
$.generationResult.doraSamples
$.generationResult.settlementSamples
$.generationResult.scoreSamples
$.generationResult.randomSamples
$.generationResult.totalSamples
$.generationResult.exporterTestResult
$.randomMeta.seed
$.randomMeta.iterations
$.randomMeta.win
$.randomMeta.noWin
$.randomMeta.openHands
$.randomMeta.closedHands
$.randomMeta.tsumo
$.randomMeta.ron
$.randomMeta.removedWinTile
$.randomMeta.yakumanSamples
$.randomMeta.deterministicNote
$.randomMeta.yakuCounts.CHANKAN
$.randomMeta.yakuCounts.CHANTA
$.randomMeta.yakuCounts.CHIIHOU
$.randomMeta.yakuCounts.CHIITOITSU
$.randomMeta.yakuCounts.CHINITSU
$.randomMeta.yakuCounts.DOUBLE_RIICHI
$.randomMeta.yakuCounts.HAITEI
$.randomMeta.yakuCounts.HONITSU
$.randomMeta.yakuCounts.HOUTEI
$.randomMeta.yakuCounts.IIPEIKO
$.randomMeta.yakuCounts.IPPATSU
$.randomMeta.yakuCounts.ITTSU
$.randomMeta.yakuCounts.JUNCHAN
$.randomMeta.yakuCounts.MENZEN_TSUMO
$.randomMeta.yakuCounts.PINFU
$.randomMeta.yakuCounts.RIICHI
$.randomMeta.yakuCounts.RINSHAN
$.randomMeta.yakuCounts.RYANPEIKOU
$.randomMeta.yakuCounts.SAN_ANKOU
$.randomMeta.yakuCounts.SAN_SHOKU_DOUJUN
$.randomMeta.yakuCounts.SAN_SHOKU_DOUKOU
$.randomMeta.yakuCounts.SUUANKOU
$.randomMeta.yakuCounts.TANYAO
$.randomMeta.yakuCounts.TENHOU
$.randomMeta.yakuCounts.TOITOI
$.randomMeta.yakuCounts.YAKUHAI_BAKAZE
$.randomMeta.yakuCounts.YAKUHAI_CHUN
$.randomMeta.yakuCounts.YAKUHAI_HAKU
$.randomMeta.yakuCounts.YAKUHAI_HATSU
$.randomMeta.yakuCounts.YAKUHAI_JIKAZE
```

这些字段不参与单样本算分输出差分：`schema`/`rulesetVersion`/`v4Commit`/`generationResult`/`extractionStatus` 用于 provenance 与计数一致性；`randomMeta` 用于 10,000 随机分布的计数断言；`generatedByCommand`/notes 仅说明生成方式。没有用它们替代真实 C# 行为比较。
