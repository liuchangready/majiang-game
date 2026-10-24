# V5 P3.2 公开单局历史纯契约（PublicRoundRecord / PublicSessionHistory）

- 日期：2026-10-25
- 状态：**组件 PASS**（P3.2 公开历史只读契约子项）；完整 P3.2、G1、G3、G5 **NOT_RUN**，不得据此宣告阶段或平台完成。
- 基线：`563b5ef5801bdee967e0897ba521082a375a3fc0`（PR19 已接受并发布的唯一头）。
- 工作树/分支：`D:/majiang-wt/public-history` / `wt/v5-public-history`。
- 实现/测试提交：`ccedb016883d037bfbaf09c407caf8d072bdce8a`。
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
| `dotnet/tests/Mahjong.Contracts.Tests/PublicSessionHistoryTests.cs` | 29 个定向用例 |
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

位置：`dotnet/src/Mahjong.Contracts/PublicSessionHistory.cs:28`（类）、`:49`（构造）、`:128`（`Rounds`）、`:135`（`FinalSummary`）。

| 成员 | 类型 | 语义 |
|---|---|---|
| `SessionId` | `SessionId` | 已有 Contracts 窄型；`default`/空值拒绝 |
| `Rounds` | `IReadOnlyList<PublicRoundRecord>` | 严格保持调用方顺序；只读、不可重排/替换/清空 |
| `FinalSummary` | `PublicSessionSummary?` | 可选；非 null 时必须是 `Finished=true`，并防御复制为新 `PublicSessionSummary` |

结构处理：

- **顺序**：不按 `roundId`、`resultId`、风/局/本场排序；不隐式去重。相邻与非相邻重复一样在构造时拒绝。
- **重复 roundId**：任一位置重复即 `ArgumentException`，消息带索引；不靠排序合并。
- **重复 resultId**：跨轮复用同一个 result 即拒绝（相邻/非相邻都拒绝），覆盖“跨轮结果边界”。
- **身份错接**：roundId 不得等于此前 resultId；resultId 不得等于任一 roundId；同一记录 roundId==resultId 也被拒绝。两个身份命名空间在冻结历史内互斥。
- **空/进行中**：允许空 `Rounds` + null `FinalSummary`；也允许已有若干局但 `FinalSummary=null`。不从局数推断终场。
- **终局摘要**：只复用现有 `PublicSessionSummary`，不新造 `PublicSessionResult`，不复制 `PublicBoardView` 的风/局号/本场/庄家。`Finished=false` 的摘要被拒绝，避免把进行中摘要标成终局。
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
| 终局摘要 | `PublicSessionResult` 带 cheat 且无 Finished | 复用 `PublicSessionSummary`，要求 Finished | 公共安全加强 |
| 顺序 | List 顺序 | 严格保持输入顺序 | 等价且不可变 |

这些差异是 **V5 对公开历史的收紧**，不是 V4 行为等价移植；没有修改 V4 源码或测试来“凑”V5。V4 允许的畸形集合（空/3/5 列表、重复历史）在 V5 被明确拒绝，并在测试中固定。

---

## 5. 验证记录

### 5.1 命令与真实计数

所有命令在 `D:/majiang-wt/public-history` 执行，使用 `D:/Unity/dotnet/dotnet.exe`（SDK 8.0.426）。

| # | 命令 | Exit | 真实结果 | 原始日志 |
|---|---|---|---|---|
| 1 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal`（改动前基线） | 0 | **115 passed / 0 failed / 0 skipped** | `baseline-contracts.log` |
| 2 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal` | 0 | **0 warnings / 0 errors**；12 个工程产物 | `solution-build-release.log` |
| 3 | `dotnet test .../Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo -v:minimal -- --filter-class "Mahjong.Protocol.Tests.PublicSessionHistoryTests"` | 0 | **29 passed / 0 failed / 0 skipped** | `targeted-public-history.log` |
| 4 | `dotnet test .../Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **144 passed / 0 failed / 0 skipped**（115 基线 + 29 新增） | `contracts-full-after.log` |
| 5 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **132 passed / 0 failed / 0 skipped** | `client-full-after.log` |
| 6 | V4 探针 `javac` + `java`（见 §2.3） | 0 / 0 | **21 checks PASS** | `probe-compile.log`、`v4-public-history-probe.log` |

说明：

- `dotnet --filter` 在 xUnit v3/Microsoft Testing Platform 下可能不生效；本轮定向使用仓库既有 `-- --filter-class`，日志显示只跑 29 项，不是全量 144。
- 未把空输出、`Total=0`、旧产物或 skip 当作通过；上述每行都有实际 `passed/failed/skipped` 总数。
- 文档提交后对同一 Release 产物使用 `--no-build` 复跑：targeted **29/29**、full **144/144**，均 exit 0；日志 `final-targeted-public-history.log`（SHA-256 `4F8842740B9167747006E14088CFC15CC97B463BA8425B07B89D824889C22897`）与 `final-contracts-full.log`（SHA-256 `AF800EF2FED2125800B378168A9725E3F65ECE22471B61536321DF7513CDBBD2`）。
- 未运行 PostgreSQL、Unity、IL2CPP、Android/iOS 真机、WSS/远端 CI；这些是 **NOT_RUN**，不是 PASS。本任务不依赖它们，也不据此宣告平台完成。

### 5.2 用例覆盖

`PublicSessionHistoryTests` 共 21 个测试方法 / 29 个执行用例：

- 真实公开数值与 seat 映射：`RoundRecordStoresRealPublicValuesBySeatWithoutRepricing`。
- 负分、负 delta、非零和（立直供托）：`RoundRecordAcceptsNegativeScoresAndRiichiDepositDeltas`。
- 空/3/5 行、重复 seat、null 列表/元素：4 个方法（含 3 行 Theory）。
- 空/空白/控制字符/超长 resultId、default roundId：3 个方法（含 6 行 Theory）。
- 构造时深复制、输入后续变动隔离、只读容器拒绝写入：`RoundRecordSnapshotsInputListsAndRejectsWrites`。
- 历史顺序不排序/不去重：`HistoryPreservesCallerOrderWithoutSortingOrDeduplicating`。
- 相邻/非相邻重复 roundId、跨轮重复 resultId、round/result 身份互斥：3 个方法。
- 空/进行中历史、终局摘要必须 Finished、default sessionId、null 历史/元素：4 个方法。
- 历史列表与 summary 防御复制、只读集合：2 个方法。
- 复用现有 `PublicSessionSummary`、无 `PublicSessionResult`、最小公开属性面：1 个方法。
- 反射扫描公共图/私有字段：拒绝 cheat/seed/tile/hand/wall/ura/dora/ledger/account/MatchSession/private 名称片段与非 Contracts/BCL 类型：1 个方法。

未引用 Domain 生成参数；反射测试只按类型/名称/命名空间扫描，不依赖 Domain 实例。

---

## 6. 文件、SHA-256 与提交

### 6.1 受控文件 SHA-256（提交前工作副本）

```text
107E2FB3E76CC588E7BD69EEB40FD1249EF614D090CA1CD0081C3988DF40F2AF  dotnet/src/Mahjong.Contracts/PublicRoundRecord.cs
05F0104B5EC8E331E30108AB46E7EBE5A53DE6482FE0BB67746C75001CE25C7F  dotnet/src/Mahjong.Contracts/PublicSessionHistory.cs
DB03B6FB7595B5A535B6B613730DFB6EEEC38537C0640258EC4ED7D1D6E44542  dotnet/tests/Mahjong.Contracts.Tests/PublicSessionHistoryTests.cs
```

本文件的 SHA-256 在写入/提交后由交付消息给出；文档自身不可能在内容中稳定包含自己的最终哈希。实现提交 `ccedb016883d037bfbaf09c407caf8d072bdce8a` 的代码/测试与上表一致（提交后未再改代码）。

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

- 实现/测试提交：`ccedb016883d037bfbaf09c407caf8d072bdce8a`（3 files changed, 917 insertions）。
- 文档提交：本文件所在提交；最终 HEAD 以交付消息的 `git log`/`rev-parse` 为准。
- 最终复核：`git status --porcelain` 为空；`git diff --check 563b5ef..HEAD` exit 0；范围仅四个 expectedFiles（3 个代码/测试 + 1 个文档）；无越界文件、无未提交改动。
- 未 push、未建 PR、未触发远端 CI、未改默认分支/机器环境/信任/部署。

---

## 7. 状态汇总

| 项目 | 状态 |
|---|---|
| P3.2 公开单局历史纯契约（`PublicRoundRecord`/`PublicSessionHistory`） | **PASS** |
| 新增定向用例 | **PASS 29/29** |
| Contracts 全量（115 基线 + 29） | **PASS 144/144** |
| Client 必要回归 | **PASS 132/132** |
| Release 非增量 solution build | **PASS 0 warnings / 0 errors** |
| V4 编译类行为探针 | **PASS 21 checks** |
| 完整 P3.2 / G1 / G3 / G5 | **NOT_RUN**（超出本任务） |
| PostgreSQL / Unity / IL2CPP / 真机 / 远端 CI | **NOT_RUN** |
| V4 生产基线 | 未改动；V4 仍是唯一生产 |

本任务不宣告 G1/G3/G5，不宣告 P3.2 完整完成，不宣告任何平台已验收。
