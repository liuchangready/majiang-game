# V5 P3.2 公开单局历史纯契约（PublicRoundRecord / PublicSessionHistory）

- 日期：2026-10-25
- 状态：**组件 PASS**（P3.2 公开历史只读契约子项，含复核缺陷 D-1-01 修复）；完整 P3.2、G1、G3、G5 **NOT_RUN**，不得据此宣告阶段或平台完成。
- 基线：`563b5ef5801bdee967e0897ba521082a375a3fc0`（PR19 已接受并发布的唯一头）。
- 工作树/分支：`D:/majiang-wt/public-history` / `wt/v5-public-history`。
- 初版实现/测试提交：`ccedb016883d037bfbaf09c407caf8d072bdce8a`。
- 复核修复：D-1-01（MAJOR，`FinalSummary` 结构校验）；复核修复提交：本文件所在提交（最终 HEAD 见交付消息）。
- 文档提交：本文件所在提交（见最终 `git log`；不把文档提交写成实现提交）。
- 生产基线：**V4 仍是唯一生产架构**。本任务只做本地 commit，不 push、不新建 PR、不触发远端 CI、不改默认分支、不部署。

---

## 1. 结论与范围

本任务只补冻结实现矩阵中 `ENG-007 / PublicRoundRecord` 与 `ENG-011 / SessionView` 对应的 **V5 P3.2 公开历史纯契约缺口**：

- `PublicRoundRecord`：单局公开身份（`roundId`/`resultId`）、四席最终比分、四席净变动。
- `PublicSessionHistory`：会话身份、按调用方顺序冻结的单局历史、可选终局摘要（复用现有 `PublicSessionSummary`）。

明确不做：

- 不重做已接受的 actor、流订阅、Wire mapper、`AccessCapabilities`、普通 AI 策略。
- 不实现玩家私有投影、生产投影接线、持久化、Wire 字段演进；这些是下游责任。
- 不依赖或读取未接受的 AI 冻结上下文；本任务不涉及 AI。
- 不新增 `PublicSessionResult` 或其他复制终局比分/排名的平行模型。
- 不复制 `PublicBoardView`/公共快照中的风、局号、本场、庄家等 game state。
- 不宣告完整 P3.2、G1/G3/G5 或平台完成。P3.1/P0.2 已接受组件是本任务前置；本契约可作为 G1 的前置契约之一，但不等同 G1。

同义实现/重复检查：本工作树的 Contracts 没有 `PublicRoundRecord`/`PublicSessionHistory`；`origin/v5` 无对应 `.cs`；只读检索的 `D:/majiang-v5-planning` 规划文档中无对应 V5 实现。V4 存在同名 `access/PublicRoundRecord.kt`，但它是带出千余额/明细的玩家侧模型，不是 V5 公共历史契约；本任务没有复制它。

受控文件（唯一可写代码/文档）：

| 文件 | 作用 |
|---|---|
| `dotnet/src/Mahjong.Contracts/PublicRoundRecord.cs` | 单局公开记录 |
| `dotnet/src/Mahjong.Contracts/PublicSessionHistory.cs` | 会话公开历史 |
| `dotnet/tests/Mahjong.Contracts.Tests/PublicSessionHistoryTests.cs` | 34 个定向用例 |
| `doc/v5-重构/V5-P32-PUBLIC-HISTORY-CONTRACT.md` | 本证据文档 |

未修改 `AGENTS.md`、`CLAUDE.md`、taskbook、review protocol、兄弟任务代码/报告/追踪 JSON；未读取或合并兄弟未接受/未提交代码。证据只写独占 `%TEMP%\mahjong-v5-public-history\`。

---

## 2. 输入契约与 V4 真实行为

### 2.1 已读依据

- `AGENTS.md`：V4 是唯一生产架构；Contracts 只依赖 BCL；观战不含暗牌/种子/私有出千流水。
- `doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`：`access/PublicRoundRecord.kt` 迁 Contracts 时须保留“不含暗牌/详细出千流水”白名单；`SessionView` 只读且不暴露 `MatchSession` 内部版本。
- `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md` §P3.2：按玩家/观战从 Domain 构造白名单视图，不先序列化完整状态再删字段。
- `doc/v5-重构/V5-0-VISIBILITY-MATRIX.md` §8.2：V4 玩家 `session.roundRecords` 含四席 `finalCheatPoints/cheatDeltas`；公共观战 `PublicSessionSummary/PublicScoreRecord` 不含这些字段；V5 默认无出千经济信息（D-12）。
- `doc/v5-重构/V5-0-RULE-CONTRACT.md` §6.4：`totalDeltas` 含立直扣点与供托，供托/立直棒可与分数分开保存；出千点数与麻将点数完全独立。故公开历史不能强制 `delta` 总和为 0，也不能把出千余额混入。
- 用户只读提供的 `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`：一次性完整清单/批量修复原则；本任务按此记录完整证据，不逐条挤牙膏。

### 2.2 真实 V4 源码

| 文件 | 行为 |
|---|---|
| `engine/src/main/kotlin/com/majiang/access/PublicRoundRecord.kt:18-38` | `roundId/resultId/finalScores/deltas/finalCheatPoints/cheatDeltas`；构造只 `require` 两个 ID 非空白；`deepCopy()` 才包 `unmodifiableList` |
| `engine/src/main/kotlin/com/majiang/access/PublicSessionResult.kt:13-28` | `finalScores/rankedSeats/reason/finalCheatPoints`；无 `Finished` 标志 |
| `engine/src/main/kotlin/com/majiang/access/SessionView.kt:19-44` | `roundRecords: List<PublicRoundRecord>` + `finalResult: PublicSessionResult?`；`deepCopy()` 深复制嵌套列表 |
| `engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt:49-78` | 公共投影改用 `PublicScoreRecord`，只复制 `roundId/resultId/finalScores/deltas/remainingRiichiSticks/outcome`；不复制 cheat 字段 |
| `engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:74-146` | `PublicScoreRecord` 构造防御复制并 `require` 两个列表大小恰为 4；无 cheat 字段 |
| `engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt:222-317` | `PublicSessionSummary` 复用 `PublicScoreRecord`，深复制 records，无 cheat 字段 |
| `engine/src/main/kotlin/com/majiang/match/MatchSession.kt:33-40,371-378` | Domain 的 `RoundRecord` 仍含 `finalCheatPoints/cheatDeltas`；这是 V4 玩家侧事实，不是公共白名单 |

### 2.3 独立探针（真实 V4 编译类，不是手抄算法）

- 探针源码：`%TEMP%\mahjong-v5-public-history\V4PublicHistoryProbe.java`（SHA-256 见 §6）。
- 编译/运行：`javac`/`java`（Android Studio JBR OpenJDK 25.0.2），classpath 为只读 `D:/majiang/engine/build/libs/engine.jar` + Kotlin stdlib。
- `engine.jar`：`D:/majiang/engine/build/libs/engine.jar`，2026-10-23 22:22:37，SHA-256 `4C5E375D682DC260161111048CA6F6A7CC35EAA13BD0CF038E9269A6849C2BDB`。V4 源码时间早于该 jar，且 `git -C D:/majiang status` 对相关 `access/`、`projection/` 路径无未提交改动。
- 原始日志：`%TEMP%\mahjong-v5-public-history\v4-public-history-probe.log`；编译/运行命令与 exit、JBR/Kotlin stdlib 路径另见 `probe-meta.txt`；**21 项检查全部 PASS，compile exit 0，run exit 0**。
- 探针断言的是 V4 实际行为，不是 V5 期望；V5 测试不引用 Domain 生成参数，`PublicSessionHistoryTests` 只使用 Contracts 类型与 BCL。

关键观测（原始日志逐行可复核）：

| 观测 | V4 实际行为 |
|---|---|
| P1 | `access.PublicRoundRecord` 公开 `finalCheatPoints/cheatDeltas`，日志原样打印四席 cheat 值 |
| P2/P3 | `access.PublicRoundRecord` 接受空列表、3 元素、5 元素列表；只检查两个 ID 非空白 |
| P4 | 接受负 finalScores 与负 deltas，无“非负”规则 |
| P5/P6 | 空白 `roundId`/`resultId` 抛 `IllegalArgumentException` |
| P7/P8 | 构造器**别名**调用方可变 `List`：构造后改输入会改变记录；通过 getter 可直接改到同一列表；只有 `deepCopy()` 返回的列表不可写 |
| P9/P10 | `projection.PublicScoreRecord` 无 cheat getter；大小 0/3/5 均被拒绝（`must have size 4`） |
| P11 | `PublicScoreRecord` 构造器防御复制，构造后改输入不影响记录 |
| P12 | `SessionView` 允许空 `roundRecords`、`finalResult=null` 的进行中会话 |
| P13 | `SessionView` 允许相邻重复 `roundId/resultId` 的历史（V4 不做去重） |
| P14/P15 | 公共 `PublicSessionSummary` 的 records 是 `PublicScoreRecord`，无 cheat getter |
| P16/P17 | `access.PublicSessionResult` 仍含 `finalCheatPoints`，且无 `Finished` 标志 |
| P18/P19/P20 | 公开方法列表分别见原始日志；access 记录含 `getFinalCheatPoints/getCheatDeltas` |

结论：V4 **玩家侧** `PublicRoundRecord` 带出千字段且构造器不防御复制；V4 **公共投影** `PublicScoreRecord` 才做了四席数量校验和构造时复制。V5 公共历史必须采用公共投影的安全边界，而不是照搬 access 记录。

---

## 3. V5 契约

### 3.1 `PublicRoundRecord`

位置：`dotnet/src/Mahjong.Contracts/PublicRoundRecord.cs:23`（类）、`:50`（构造）、`:88`（四席校验）。

| 成员 | 类型 | 语义 |
|---|---|---|
| `RoundId` | `RoundId` | 已有 Contracts 窄型；`default`/空值拒绝 |
| `ResultId` | `string` | 与 V4 的 result identity 对齐；`ProtocolText.RequireId` 校验非空、≤128、无首尾空白/控制字符 |
| `FinalScores` | `IReadOnlyList<ScoreView>` | 恰四席，索引即 seat；值原样来自输入 |
| `Deltas` | `IReadOnlyList<ScoreView>` | 恰四席，`ScoreView.Score` 表示有符号净变动，非总分；保留负数 |

规则：

- 两个列表必须恰有 seat 0..3 各一次；空、3、5、重复 seat、null 元素都拒绝。
- 输入行可任意顺序；按 seat 存放，保证 `FinalScores[seat].Seat == seat`、`Deltas[seat].Seat == seat`，不改变任何数值。
- 构造时复制为 `ReadOnlyCollection`，输入列表后续增删改不影响记录；公开容器不能转成可写 `List`。
- 不重算结算、不调排名/分数、不发牌、不改分；允许负分、负 delta、delta 总和不为 0（立直供托等合法事实）。
- 不含 cheat 余额/明细、seed、牌/手牌/牌山/里宝、私有 ledger、完整 `MatchSession`。

### 3.2 `PublicSessionHistory`

位置：`dotnet/src/Mahjong.Contracts/PublicSessionHistory.cs:28`（类）、`:56`（构造）、`:140`（`Rounds`）、`:147`（`FinalSummary`）、`:149`（`ValidateFinalSummary`）。

| 成员 | 类型 | 语义 |
|---|---|---|
| `SessionId` | `SessionId` | 已有 Contracts 窄型；`default`/空值拒绝 |
| `Rounds` | `IReadOnlyList<PublicRoundRecord>` | 严格保持调用方顺序；只读、不可重排/替换/清空 |
| `FinalSummary` | `PublicSessionSummary?` | 可选；非 null 时必须是 `Finished=true`，防御复制并校验分数行/席位/排名结构 |

结构处理：

- **顺序**：不按 `roundId`、`resultId`、风/局/本场排序；不隐式去重。相邻与非相邻重复一样在构造时拒绝。
- **重复 roundId**：任一位置重复即 `ArgumentException`，消息带索引；不靠排序合并。
- **重复 resultId**：跨轮复用同一个 result 即拒绝（相邻/非相邻都拒绝），覆盖“跨轮结果边界”。
- **身份错接**：roundId 不得等于此前 resultId；resultId 不得等于任一 roundId；同一记录 roundId==resultId 也被拒绝。两个身份命名空间在冻结历史内互斥。
- **空/进行中**：允许空 `Rounds` + null `FinalSummary`；也允许已有若干局但 `FinalSummary=null`。不从局数推断终场。
- **终局摘要**：只复用现有 `PublicSessionSummary`，不新造 `PublicSessionResult`，不复制 `PublicBoardView` 的风/局号/本场/庄家。`Finished=false` 的摘要被拒绝，避免把进行中摘要标成终局。非空 `FinalScores` 的每一行必须非 null，且 0..3 四席各一次；非空 `Rankings` 必须是 0..3 四席各一次。重复/缺漏席位抛 `ArgumentException`，不静默纠正或去重。
- **空集合契约**：既有 `PublicSessionSummary` 允许 0 或 4 行；本历史为兼容保留空 `FinalScores`/空 `Rankings`（两者各自为空时保留为空，不伪造另一侧），不伪造分数或排名。1–3 行的部分集合无法由该 summary 构造，故历史边界不会见到。合法输入顺序、排名次序与有符号数值原样复制，不重排、不重算、不重新排名。
- **防御复制**：`Rounds` 复制列表；每个 `PublicRoundRecord` 自身已不可变且内部列表只读；`FinalSummary` 复制为新的 summary（分数/排名列表由 summary 构造器再复制）。

非目标/边界：

- 不是 Wire DTO；没有修改 proto、Wire mapper 或客户端线路字段。不能写成“现有 Wire 已承载”。
- 不是生产投影、持久化、玩家私有展示或重连恢复实现。
- 不新增游戏规则；只做公开读模型的结构/安全约束。

---

## 4. V4 与 V5 的差异（安全加强，非等价）

| 维度 | V4 实际 | V5 本契约 | 性质 |
|---|---|---|---|
| cheat 字段 | access `PublicRoundRecord` 含 `finalCheatPoints/cheatDeltas` | 结构上不存在 | 公共观战/历史安全加强 |
| 构造复制 | access 记录别名调用方 List，`deepCopy()` 才冻结 | 构造即复制并只读 | 安全加强（修复可变别名） |
| 四席 | access 记录接受任意长度；公共 `PublicScoreRecord` 只校验 size 4，位置即 seat | 校验 seat 0..3 各一次、去重、null 拒绝 | 公共投影加强为显式席位身份 |
| 历史重复 | `SessionView` 允许相邻重复 round/result | 重复 roundId/resultId 构造时拒绝 | 安全加强，不排序消解 |
| 身份错接 | 无跨集合校验 | round/result 命名空间互斥 | 安全加强 |
| 负值/总和 | 接受负分/负 delta，无零和强制 | 保持接受 | 等价保留合法事实 |
| 终局摘要 | `PublicSessionResult` 带 cheat 且无 Finished；summary 只做浅层数量/范围校验 | 复用 `PublicSessionSummary`，要求 Finished；拒绝 null 分数行、重复/缺漏分数席位与排名席位；空集合兼容保留 | 公共安全加强（D-1-01） |
| 顺序 | List 顺序 | 严格保持输入顺序；summary 合法输入顺序/排名/符号原样保留 | 等价且不可变 |

这些差异是 **V5 对公开历史的收紧**，不是 V4 行为等价移植；没有修改 V4 源码或测试来“凑”V5。V4 允许的畸形集合（空/3/5 列表、重复历史）在 V5 被明确拒绝，并在测试中固定。

### 4.1 复核修复 D-1-01（MAJOR）

**缺陷**：初版只检查 `FinalSummary.Finished`，随后直接复制；既有 `PublicSessionSummary` 仅校验列表数量 0/4 与排名范围，不校验 `ScoreView` 元素非 null，也不校验分数/排名席位唯一。因此三组非法终局摘要被接受：

| 负例 | 非法事实 | 修复前 |
|---|---|---|
| `null-final-score` | `FinalScores` 含 null 行 | `ACCEPTED_INVALID` |
| `duplicate-final-score-seat` | 分数席位 `[0,0,2,3]`，seat 1 缺失 | `ACCEPTED_INVALID` |
| `duplicate-ranking-seat` | 排名席位 `[0,0,2,3]`，seat 1 缺失 | `ACCEPTED_INVALID` |

**修复**：仅在新 `PublicSessionHistory` 边界新增 `ValidateFinalSummary`：

- `FinalScores`：空保留兼容；非空必须恰 4 行、每行非 null、seat 0..3 各一次。
- `Rankings`：空保留兼容；非空必须恰 4 项、seat 0..3 各一次。
- 仅做结构校验；不重算分数、不调整排名、不排序、不去重、不改写合法输入顺序或负值。
- 所有拒绝的 `ParamName` 为 `finalSummary`，调用者传入的 `PublicSessionSummary` 不被修改。

**统一负例矩阵**（全部 `ArgumentException`）：null 分数行、重复分数席位、重复排名席位；另有合法乱序/负分/排名原样保留与空集合兼容正例。原 29 个用例全部保留，新增 5 项后共 34 项。

**原样 caller probe**（路径与源码未改）：

```text
修复前: ACCEPTED_INVALID null-final-score / duplicate-final-score-seat / duplicate-ranking-seat
        INVALID_ACCEPTED=3, exit 1
修复后: REJECTED null-final-score finalSummary
        REJECTED duplicate-final-score-seat finalSummary
        REJECTED duplicate-ranking-seat finalSummary
        INVALID_ACCEPTED=0, exit 0
```

---

## 5. 验证记录

### 5.1 命令与真实计数

所有命令在 `D:/majiang-wt/public-history` 执行，使用 `D:/Unity/dotnet/dotnet.exe`（SDK 8.0.426）。

| # | 命令 | Exit | 真实结果 | 原始日志 |
|---|---|---|---|---|
| 1 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal`（改动前基线） | 0 | **115 passed / 0 failed / 0 skipped** | `baseline-contracts.log` |
| 2 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal` | 0 | **0 warnings / 0 errors**；12 个工程产物 | `d101-solution-build.log` |
| 3 | `dotnet test .../Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo -v:minimal -- --filter-class "Mahjong.Protocol.Tests.PublicSessionHistoryTests"` | 0 | **34 passed / 0 failed / 0 skipped** | `d101-targeted-final.log` |
| 4 | `dotnet test .../Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **149 passed / 0 failed / 0 skipped**（115 基线 + 34 本任务） | `d101-contracts-full-final.log` |
| 5 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **132 passed / 0 failed / 0 skipped** | `d101-client-full.log` |
| 6 | 原样 caller probe `dotnet run --project .../summary-probe/Probe.csproj -c Release`（修复前） | 1 | `ACCEPTED_INVALID` ×3；`INVALID_ACCEPTED=3` | `summary-probe-before.log` |
| 7 | 同一 caller probe（修复后，未改其源码） | 0 | `REJECTED` ×3；`INVALID_ACCEPTED=0` | `d101-summary-probe-final.log` |
| 8 | V4 探针 `javac` + `java`（见 §2.3，D-1-01 未触碰 V4） | 0 / 0 | **21 checks PASS** | `probe-compile.log`、`v4-public-history-probe.log` |

说明：

- `dotnet --filter` 在 xUnit v3/Microsoft Testing Platform 下可能不生效；本轮定向使用仓库既有 `-- --filter-class`，日志显示只跑 34 项，不是全量 149。
- 未把空输出、`Total=0`、旧产物或 skip 当作通过；上述每行都有实际 `passed/failed/skipped` 总数。
- 非增量 solution build 后使用 `--no-build` 复跑 targeted **34/34**、full **149/149**、Client **132/132**，均 exit 0；最终日志为 `d101-targeted-final.log`、`d101-contracts-full-final.log`、`d101-client-full.log`。
- D-1-01 的 caller probe 源码/路径未改；修复前日志保留在 `summary-probe-before.log`，修复后日志在 `d101-summary-probe-final.log`，两次均原样执行。
- 未运行 PostgreSQL、Unity、IL2CPP、Android/iOS 真机、WSS/远端 CI；这些是 **NOT_RUN**，不是 PASS。本任务不依赖它们，也不据此宣告平台完成。

### 5.2 用例覆盖

`PublicSessionHistoryTests` 共 26 个测试方法 / 34 个执行用例：

- 真实公开数值与 seat 映射：`RoundRecordStoresRealPublicValuesBySeatWithoutRepricing`。
- 负分、负 delta、非零和（立直供托）：`RoundRecordAcceptsNegativeScoresAndRiichiDepositDeltas`。
- 空/3/5 行、重复 seat、null 列表/元素：4 个方法（含 3 行 Theory）。
- 空/空白/控制字符/超长 resultId、default roundId：3 个方法（含 6 行 Theory）。
- 构造时深复制、输入后续变动隔离、只读容器拒绝写入：`RoundRecordSnapshotsInputListsAndRejectsWrites`。
- 历史顺序不排序/不去重：`HistoryPreservesCallerOrderWithoutSortingOrDeduplicating`。
- 相邻/非相邻重复 roundId、跨轮重复 resultId、round/result 身份互斥：3 个方法。
- 空/进行中历史、终局摘要必须 Finished、default sessionId、null 历史/元素：4 个方法。
- D-1-01 负例矩阵：`HistoryRejectsFinalSummaryWithNullScoreRow`、`HistoryRejectsFinalSummaryWithDuplicateScoreSeat`、`HistoryRejectsFinalSummaryWithDuplicateRankingSeat`。
- D-1-01 正例：`HistoryPreservesValidFinalSummaryInputOrderRankingsAndSigns`（乱序席位、负分、排名原样）、`HistoryAllowsEmptyFinalSummaryCollectionsForCompatibility`（空集合兼容）。
- 历史列表与 summary 防御复制、只读集合：2 个方法。
- 复用现有 `PublicSessionSummary`、无 `PublicSessionResult`、最小公开属性面：1 个方法。
- 反射扫描公共图/私有字段：拒绝 cheat/seed/tile/hand/wall/ura/dora/ledger/account/MatchSession/private 名称片段与非 Contracts/BCL 类型：1 个方法。

未引用 Domain 生成参数；反射测试只按类型/名称/命名空间扫描，不依赖 Domain 实例。

---

## 6. 文件、SHA-256 与提交

### 6.1 受控文件 SHA-256（提交前工作副本）

```text
107E2FB3E76CC588E7BD69EEB40FD1249EF614D090CA1CD0081C3988DF40F2AF  dotnet/src/Mahjong.Contracts/PublicRoundRecord.cs
594D809C7DFC46E60F143AC591A00DBE5D1C3762C18901818CE2EA024B12DE9E  dotnet/src/Mahjong.Contracts/PublicSessionHistory.cs
7F8F7406A7CA7C0D45BC9E4ED066AFCF154981F095736E13CCCB23D55074C20F  dotnet/tests/Mahjong.Contracts.Tests/PublicSessionHistoryTests.cs
```

本文件的 SHA-256 在写入/提交后由交付消息给出；文档自身不可能在内容中稳定包含自己的最终哈希。上表是 D-1-01 修复后的工作副本：`PublicRoundRecord.cs` 与初版实现提交 `ccedb016883d037bfbaf09c407caf8d072bdce8a` 一致；`PublicSessionHistory.cs` 与测试由复核修复提交（本文件所在提交）更新。

### 6.2 证据日志 SHA-256（`%TEMP%\mahjong-v5-public-history\`）

```text
A4CAD6FFC871F247396BEC3D64B2CA3F7DC88BC0318670B432037E308AB6C592  baseline-contracts.log                  2706 B
A8E327D13773ABEB01EBB1839548780E42288EC5FB6B7E10E8CA0509BB3C9FA0  client-full-after.log                   708 B
EFB5ABD85B77C28929A3FC610606FAD0D6C313924F8D15C0E5D8ECD575377E1A  contracts-full-after.log                738 B
74A3F971F13B8259BA1844F0DBB8BA652229BB040848EAF84F4C58C0D1C062F0  contracts-tests-build2.log             1258 B
C3CE6ED88F68DD14D67D2D054B7AEF3CB9B4411DF9F39F949661E8FECF19D235  solution-build-release.log             5056 B
36CC24668B0547C6FBC121FD8EFDE8758597404D9729EA12B1B354B03F0522D3  targeted-public-history.log             728 B
E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855  probe-compile.log                         0 B
BA57A71A6973844039FFB4DF278560D48DA529FD3CA6E7106FD39A8106226DE1  probe-meta.txt                         1305 B
5DEA0573D619135C34F7E757896B89A73E3F64EA70F70FF36D319A4115256990  V4PublicHistoryProbe.java             10976 B
1A5C0FB0C1746C6627E3D79CDCC51D989D7FBCB4C5E90973530E8C26566E503D  v4-public-history-probe.log            8220 B
```

D-1-01 复现/修复日志：

```text
010D9DF5E276A78EE3EC5C092802B65BE81CA2B7AB0491009EAC044EA6E0A19C  summary-probe-before.log                 440 B
433C5FA896B09A73182A068F7B9D62487DCA9448582DD2C7AF3F47919ED6786F  d101-summary-probe-final.log             314 B
FD397E194D82D5F9FCF1CB4976955227AA249E3C08C0EF30DFF85B1B91BEDFFF  d101-solution-build.log                 3348 B
D8C39ADC860AA083214397461642D19478777CE751D6D41A4570C2FF3A369FC2  d101-targeted-final.log                  728 B
3768AF51418D1A182226C4AD3AA6448CE82122F1C7BCA6336EE16D9F40A5A630  d101-contracts-full-final.log            738 B
CD161E39404D946036EF573B55600B3B706BD0C4B1FA6221035ACE959ED1A1DF  d101-client-full.log                     708 B
2FE03D98770CF51D24DF9D66A9A491AA967FF0E6802E6A29C7D8B004155A5BE5  d101-contracts-build.log                 436 B
BDE0453D65E479BB0A7D2A4C2FE37A756C0DEB2412DE11B98ABFD9C0506AEE6E  d101-tests-build.log                    1258 B
```

原样 caller probe 源码（未修改）：

```text
D1803188DFAE6005482959BA782DC8FFB7045834A99004607D79B36616C2513A  .../summary-probe/Program.cs
4903795C03421D72C161C053FD533A4C73A0B12C09F455DF3BD5599A39BC01C8  .../summary-probe/Probe.csproj
```

V4 源码与 jar 参考 SHA-256：

```text
4C5E375D682DC260161111048CA6F6A7CC35EAA13BD0CF038E9269A6849C2BDB  D:/majiang/engine/build/libs/engine.jar
23FA9D2C1105B1F6954D99C4C47E31A38CDB083C70441058E86902CAF5EB87A8  D:/majiang/engine/src/main/kotlin/com/majiang/access/PublicRoundRecord.kt
C3BB05AEA0C2E3B1B34B65783F04C480477AA6D07A704CCC6AF3B7FDC678C8B8  D:/majiang/engine/src/main/kotlin/com/majiang/access/PublicSessionResult.kt
AE0682D43680E2D1A1727EEC4A275FBA5BBF2698B50BB6D093E9F1FAFDAB5FDF  D:/majiang/engine/src/main/kotlin/com/majiang/access/SessionView.kt
2168A7D02D3A4214E26266D24489D2C26BF254DF87E7FB5F3565FEB58C5935CB  D:/majiang/engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt
FE5EFAA876010B519061EA61A2A0B4B89B8742057FEDB1FC2DEC2CD77D793796  D:/majiang/engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt
```

### 6.3 Git

- 初版实现/测试提交：`ccedb016883d037bfbaf09c407caf8d072bdce8a`（3 files changed, 917 insertions）。
- 复核修复提交：本文件所在提交；修改 `PublicSessionHistory.cs`、`PublicSessionHistoryTests.cs` 与本文档，完成 D-1-01。
- 文档提交：本文件所在提交；最终 HEAD 以交付消息的 `git log`/`rev-parse` 为准。
- 最终复核：`git status --porcelain` 为空；`git diff --check 563b5ef..HEAD` exit 0；范围仅四个 expectedFiles（3 个代码/测试 + 1 个文档）；无越界文件、无未提交改动。
- 未 push、未建 PR、未触发远端 CI、未改默认分支/机器环境/信任/部署。

---

## 7. 状态汇总

| 项目 | 状态 |
|---|---|
| P3.2 公开单局历史纯契约（`PublicRoundRecord`/`PublicSessionHistory`） | **PASS** |
| 复核缺陷 D-1-01（终局摘要 null/重复席位） | **PASS**（3 负例拒绝，caller probe exit 0） |
| 新增定向用例 | **PASS 34/34** |
| Contracts 全量（115 基线 + 34） | **PASS 149/149** |
| Client 必要回归 | **PASS 132/132** |
| Release 非增量 solution build | **PASS 0 warnings / 0 errors** |
| V4 编译类行为探针 | **PASS 21 checks** |
| 完整 P3.2 / G1 / G3 / G5 | **NOT_RUN**（超出本任务） |
| PostgreSQL / Unity / IL2CPP / 真机 / 远端 CI | **NOT_RUN** |
| V4 生产基线 | 未改动；V4 仍是唯一生产 |

本任务不宣告 G1/G3/G5，不宣告 P3.2 完整完成，不宣告任何平台已验收。

## 8. Caller 第 2 轮独立验收与统一 PR19 集成（2026-10-25）

Bridge `thread_20261025_041932_4f99` 第 2 轮交付 `9e72bd8` 已由 caller 独立审查并调用 `approve_task` 归档。D-1-01 全部三类负例关闭，无新增或遗留缺陷。相对已发布基线 `563b5ef5801bdee967e0897ba521082a375a3fc0`，交付仅四个授权文件，工作树干净，`git diff --check 563b5ef..HEAD` exit 0。

独立审查确认：终局摘要校验只位于新 history 边界；既有 summary 不变；合法顺序、排名、有符号分数不重算；空集合兼容明确。所有集合防御复制且只读，行类型 sealed/get-only；公开类型图不含出千、牌、种子或领域实现。纯契约不引入领域执行、权威队列或恢复副作用。

Caller 在交付工作树重新执行以下命令，均 exit 0：

| 验证 | 实际结果 |
|---|---|
| `D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal` | 0 warnings / 0 errors |
| `D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Contracts.Tests/bin/Release/net8.0/Mahjong.Contracts.Tests.dll -class Mahjong.Protocol.Tests.PublicSessionHistoryTests` | 34 total / 0 failed / 0 errors / 0 skipped / 0 Not Run |
| 同一 Contracts runner，不带 `-class` | 149 total / 0 failed / 0 errors / 0 skipped / 0 Not Run |
| `D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll` | 132 total / 0 failed / 0 errors / 0 skipped / 0 Not Run |
| 原样 caller `summary-probe/Probe.csproj`，`dotnet run -c Release` | 三项 REJECTED，INVALID_ACCEPTED=0；源码 SHA256 仍为 D1803188DFAE6005482959BA782DC8FFB7045834A99004607D79B36616C2513A |
| 重新 javac 编译并运行真实 V4 engine.jar 探针 | compile/run exit 0，CHECKS=21 |

独立原始日志目录：`C:/Users/sncdc/AppData/Local/Temp/codex-public-history-round2-review-20261025/`。交付文档列出的 18 个日志哈希另经逐一实读核对，全部一致。Caller 日志 SHA256：

```text
4BEB0A1DFEE0610FB234CE782CF4C1EBE2F7F48E5891BDDD26A240A7D436905F  solution-build.log
F8D4E4CD69E03570D12418E33860AAAA7CDF5C37CB9898607188104E4EEBA3B7  targeted.log
1A2D832934EA8EBDAC883EFD3B5F78F0B9F3FB637855E0C0321E3082F4940AC6  contracts.log
D2AC80ACA1152B715DADAB356E84DC356A4B6385A8A785D5D20C63BC50ED6F67  client.log
433C5FA896B09A73182A068F7B9D62487DCA9448582DD2C7AF3F47919ED6786F  summary-probe.log
E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855  v4-compile.log
3CBCB27BABAD0FB730E221C04A5B21E4D465FEE28BA5C3C84D2704229AB46C63  v4-probe.log
```

Caller 发布提交仅追加本节；C# 内容与已验收交付 `9e72bd8` 完全相同。交付三笔本地提交及本验收记录统一更新既有 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`，目标 `v5`，不新建组件 PR。远端发布前核实原源分支为基线 `563b5ef`，可普通快进推送；保留此前全部已验收批次。同批 frozen-context/readiness 仍在返修，无其他已接受而尚未发布的交付；其工作树不纳入本次发布。精确发布 SHA 与远端复核结果记录于同一 Bridge 线程 MR 登记。

本次仅验收公开历史纯契约组件。完整 P3.2/G1/G3/G5 及生产投影、持久化、Wire 接线仍未完成；PG/WSS/Unity/IL2CPP/设备/物理网络和本次远端 CI 均未作为通过证据。V4 仍是唯一生产架构。
