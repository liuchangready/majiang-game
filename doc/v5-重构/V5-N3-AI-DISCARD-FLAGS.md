# V5 N3-B6：`$.v5Observation.mappingLimits[*]` idx1/idx2（isClaimed / isRiichi）非空值级绑定证据

任务性质：共享台账 §14.1 剩余六条中的 B6 两条（idx1 每张弃牌 isClaimed、idx2 每张弃牌 isRiichi）的明确后续；在其复访条件（"有弃牌实例及值级消费断言"）上交付真实行为证据，并给出 caller 串行裁决用 proposedVerdict。**本报告不宣布门 2、不宣布 G1/G5、不宣布完整移植；V4 仍是唯一生产架构；不创建 `complete.flag`；不改共享台账/校验器/catalog/manifest/已接受报告。**

## 1. 原身份与历史（原样保留）

两条 pending 的原身份来自已接受裁决 `bb34338e3b20d562c029204f42c1ca612f943e58`（AI 文本裁决，报告 `V5-N3-AI-TEXT-ADJUDICATION.md` §2 表行 1/2）与共享台账 §14.1 行 5/6：

| # | 原身份 id | histClass | originalVerdict | 原 fixture 值 |
|---|---|---|---|---|
| 1 | `N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|1` | UNBOUND | pending | `PlayerView has no public claimed-discard flag; isClaimed is false.` |
| 2 | `N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|2` | UNBOUND | pending | `riichi status is available as a per-seat flag but is not attached to individual discards by PlayerView; per-discard isRiichi is false.` |

原 C# 边界（保持不变）：`AiReplayGoldenTests.cs:381-394` 解析两旗；冻结 ai-v1 fixture `visibleDiscards=[]`；`AiReplayStateGoldenTests.cs:606 Assert.Empty`。**ai-v1 本体与既有 AI 测试全部冻结不动。**

## 2. 实际 V4 来源边界（零 V4 生产改动）

逐旗溯源，全部来自真实 V4 领域输出，不制造结果：

- **isRiichi**：`GameEvent.TileDiscarded.isRiichiDiscard`（由 DeclareRiichi 转换置 true）。立直宣言打出的牌**留在牌河**；与 `PlayerView.riichiStatus`（按座位公开投影）交叉锚定。
- **isClaimed**：V4 `PlayerView.discards` 只持未被鸣牌的牌（`TileMovement.pon` 执行 `fromHand.discards.removeLast()`），被鸣走的牌离开活跃牌河、留在 `PlayerHand.discardHistory`，由公开鸣牌元数据（`Meld.calledTile` + `Meld.fromPlayer`，经 `PonDeclared` 事件发布）标识。因此：**牌河条目 isClaimed=false 是 V4 构造使然**；被鸣弃牌导出为 `claimedDiscards` 条目 isClaimed=true。
- 边界声明：V4 `PlayerView` 至今**不暴露**每张弃牌的 claimed/riichi 布尔。本任务不改 V4 生产代码制造字段；两旗只由公开事件、公开投影与公开鸣牌元数据推导。idx1/idx2 原行是否升 classified 由 caller 裁决。

## 3. 三个案例的真实构造（新 exporter，非手工 golden）

新独立 exporter `engine/src/test/kotlin/com/majiang/state/V5AiDiscardFlagsGoldenExporterTest.kt`（与冻结 ai-v1 exporter 不同文件、不同名；JSON 辅助类型全部嵌套以避开同包 top-level 冲突）：

| caseId | 构造 | 断言锚点 |
|---|---|---|
| `riichi-river` | seat0 立直（`DeclareRiichi(0, east0=tileId108)`），事件计数循环推进其余三家弃牌 | river[0]={seat0,riverIndex0,tileId108,isRiichi:true,isClaimed:false}；其余 ≥4 条 isRiichi=false；riichiStatusBySeat=[T,F,F,F]；scores[0]=24000；riichiSticks=1；claimedDiscards=[] |
| `claimed-discard` | seat1 弃 MAN1(c0)，seat2 `DeclarePon` | claimedDiscards[0]={seat1,tileId0,isRiichi:false,isClaimed:true,absentFromActiveRiver:true,presentInDiscardHistory:true,derivedFrom={PonDeclared,meldSeat2,calledTileId0,fromPlayer1}}；meld.single()=PON；tileId0 不在任何 river |
| `riichi-discard-claimed` | seat0 立直宣言打 east0，seat1 `DeclarePon` 鸣走 | claimedDiscards[0]={seat0,tileId108,isRiichi:true,isClaimed:true}——**两旗同真**的最强组合；scores[0]=24000；players[0].riichiDeclared；east0 不在 river、在 discardHistory |

每案例 doc 由 `PlayerView.fromGameState(live, viewerSeat=3, emptyList())` 公开投影提取；recordedEvents 只收 TileDiscarded/RiichiDeclared/PonDeclared（隐私上排除 TileDealt/TileDrawn）；无任何手填 golden 值、无假哈希。

## 4. fixture 与导出证据

- `tests/v5-golden/ai-discard-flags-v1.json`（14480 字节，仅由上述 exporter 实际生成）。
- provenance：`generatedAtHead=85134e9ce4fd67556f6d2f355249c14f4d3b42d4`（工作树冻结头）、`v4EngineLastCommit=3f5370c30983a4047dade0835bb2c49dacc5ebbf`、`nativeExitCode=0`、`trackedDirtyClass=test-only`、`renderSha256=caf13b8250b7421e89f0e879631d4f9ed6264badda0ea39fbad0d0dbb305e4b8`（自指：把该字段置 null 后对全文 SHA-256 必须复现自身）。
- `sourceHashes`：11 个真实源码 pin（PlayerView/GameEvent/RoundTransitionPlanner/TileMovement/PlayerHand/Meld/Tile/GameAction/exporter 自身/AiObservation.cs/TableActor.cs），各含 sha256/sha256Lf/lastCommit；exporter 自我 pin 与提交字节一致（`3a38d87d…`，已核对）。
- 导出器内置确定性双跑与默认模式验证：export 模式两跑 render 相等才写文件；普通模式 `validateCommittedAiDiscardFlagsGoldenFixture()` 把 committed cases 切片与**实时重推导**（真实 V4 域再打三局）字节比对，并重算 renderSha256。

## 5. C# 消费链（`AiDiscardFlagsGoldenTests.cs`，6 个 [Fact]）

新文件 `dotnet/tests/Mahjong.Ai.Tests/AiDiscardFlagsGoldenTests.cs`；只新增 `Mahjong.Ai.Tests.csproj` 一个 copy 条目（`ai-discard-flags-v1.json` → PreserveNewest），既有条目与依赖零改动；Ai 生产代码零改动（只用 `AiVisibleDiscard`/`AiObservation` 既有公开 API）。fixture 路径解析：默认 `AppContext.BaseDirectory/ai-discard-flags-v1.json`，环境变量 `MAHJONG_AI_DISCARD_FLAGS_GOLDEN` 可覆盖（**置而缺失 = 硬失败**）。

1. `FixtureHasProvenanceAndTheThreeDerivedCases` — fixture 标识/版本/provenance pin/11 个 source pin 含 PlayerView.kt+AiObservation.cs+TableActor.cs/caseIds 恰为三案例。
2. `RiverAndClaimedFlagValuesFollowTheV4DiscardSemantics` — 值级语义契约：riichi-river 河 ≥5 条且全部 !IsClaimed、恰 1 条 IsRiichi（seat0/riverIndex0）；riichiStatusBySeat=[T,F,F,F]；claimed-discard 单条 IsClaimed&&!IsRiichi 且不在河；两旗同真条目 IsClaimed&&IsRiichi。
3. `AiObservationConsumesRiverFlagsThroughThePublicProjectionSurface` — 逐案例 `BuildRiver`→公开构造 `AiObservation`，逐条 Seat/Tile.Value/IsRiichi/IsClaimed 值相等；立直弃牌 ⇒ `riichiStatusBySeat[seat]==true`。
4. `ClaimedDiscardsStayOutsideEveryObservationRiver` — 被鸣 tileId 不出现在任何 `observation.VisibleDiscards`；独立 `AiVisibleDiscard` 公开构造消费；两旗同真；两旗案例中 seat0 河为空。
5. `ObservationDefensivelyCopiesDiscardsAndStaysReadOnly` — 源列表 Clear()/Add 后观察不变；`(ICollection<AiVisibleDiscard>)VisibleDiscards).Add` 抛 `NotSupportedException`（ReadOnlyCollection 背书）。
6. `FixtureAndPublicSurfaceExposeOnlyPublicDiscardInformation` — 递归属性名扫描对照禁止集（wall/wallOrder/wallTiles/wallPrefix/deadWall/seed/sleeve/sleeveTiles/closedTiles/handTileIds/drawnTileId/uraDora/hiddenTiles/tilePositionMap）；`typeof(AiVisibleDiscard)` 公开属性恰为 {IsClaimed,IsRiichi,Seat,Tile}；AiObservation 无 Sleeve/Seed/Ura/DeadWall/ClosedTiles，Wall 属性必须为 RemainingWallCount。

## 6. 变异反证（TEMP 内副本，全部值级失败）

`artifacts/TEMP/n3-b6-discard-flags/make-mutants.sh` 对 fixture 副本逐段翻旗，`MAHJONG_AI_DISCARD_FLAGS_GOLDEN` 指向副本直跑（选类 `-class`，Total=6 非空选择）：

| 副本 | 翻转 | exit | Failed/6 | 命中的值级断言 |
|---|---|---:|---:|---|
| `mut-claim-river.json` | riichi-river 河 5 条 isClaimed false→true | 1 | 2 | `no active-river entry may be claimed`；`Assert.DoesNotContain() Failure: Filter matched` |
| `mut-riichi-river-on.json` | 河 4 条 false→true | 1 | 2 | `a riichi discard must belong to a seat that declared riichi`；恰 1 条 IsRiichi 的 `Assert.Equal` 期望 1 |
| `mut-riichi-river-off.json` | 河立直条 true→false | 1 | 1 | 恰 1 条 IsRiichi 的 `Assert.Equal` 期望 1 |
| `mut-claim-claimed.json` | 末案 claimed isClaimed true→false | 1 | 2 | `the ponned riichi discard must be isClaimed=true` |
| `mut-riichi-claimed.json` | 末案 claimed isRiichi true→false | 1 | 2 | `the ponned riichi discard must be isRiichi=true` |

原 fixture 同命令 exit=0、6/6 通过。C# 侧语义断言与 exporter 输出互不自我背书：断言读 fixture 值，翻任何一旗即失败。首次变异运行曾因 bash 路径插值缺陷（`\\$m` 未展开→"missing fixture" 硬失败）得到 6/6 全失败的**无效证据**，已用正斜杠路径重跑并以本表为准。

## 7. 实际命令、exit、计数与日志 SHA256

| 步骤 | 命令（仓库根） | exit | 计数 | 日志（SHA256） |
|---|---|---:|---|---|
| 导出 | `V5_AI_DISCARD_FLAGS_EXPORT=1 ./gradlew :engine:test --tests "com.majiang.state.V5AiDiscardFlagsGoldenExporterTest" --rerun-tasks --console=plain` | 0 | cases=3 bytes=14480 | `export-run-10.log` `235bdb46411fa7720f291e16e57eeb650aa9ee1a7623c1936938828a27d4f114` |
| 验证（默认模式强制重跑） | `./gradlew :engine:test --tests "com.majiang.state.V5AiDiscardFlagsGoldenExporterTest" --rerun-tasks --console=plain` | 0 | 2/2 passed | `validate-run-6.log` `1bd95243daf78780aba5da406a52e659e4e92e85073d02ed097087cf79c57345` |
| C# 构建 | `D:/Unity/dotnet/dotnet.exe build -c Release --nologo`（dotnet/tests/Mahjong.Ai.Tests） | 0 | 0 警告 0 错误 | `csharp-build-1.log` `0520eb4d83f92815163ad83af5a21f8d956245c798da5136f11e275f8393649c` |
| C# 原件直跑 | `D:/Unity/dotnet/dotnet.exe bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests -result-xml …xunit-original.xml` | 0 | Total 6，Errors 0，Failed 0，Skipped 0，NotRun 0，0.355s（xUnit v3 In-Process Runner v4.0.2，.NET 8.0.32） | `xunit-original.log` `ebd67eeb2e3eaf6513a49a459679d31c2e6127e66c472418f14becc9ef629f86` |
| 变异 ×5 | 同上，`MAHJONG_AI_DISCARD_FLAGS_GOLDEN=…/mut-*.json` | 均 1 | 2/2/1/2/2 失败 | 见 `evidence-sha256.txt`（五个日志逐条 SHA256 在案） |

过程证据（同目录）：export-run-1..9（含失败迭代）、validate-run-1..5、`xunit-mut-*.log/xml`、`node-count.txt`。失败迭代的原因均已定位并修复：同包 top-level 声明冲突（JSON 辅助类型改嵌套）、`casesSection` 切片次序缺陷（trimEnd 先于 removeSuffix 会把 `]\n` 留在切片内；文件尾 LF 又使 removeSuffix 失配——最终改为按文档最后一个 `]` 精确切片）、重推导需按文档嵌入缩进（indent 2）渲染。

## 8. §8a 增量登记（沿用原 Visit 增量算法，只登新 fixture，不重算全量）

`ai-discard-flags-v1.json` 按已接受原脚本（n3-closeout-ledger `artifacts/TEMP/r3/incremental-census-repro.ps1` 的 `Visit`）逐节点计数：root 路径 `$`；**每个 value 在类型分发前即 nodes++ 并登记 path（标量既计 node 也计 leaf）**；object 子项走 `path+'.'+name`，数组子项走 `path+'[*]'`；含 null 的标量计 leaf。

| fixture | Paths | LeafPaths | NodeInstances | LeafInstances |
|---|---:|---:|---:|---:|
| `tests/v5-golden/ai-discard-flags-v1.json` | 89 | 63 | 401 | 292 |

- 计数命令 exit 0：`powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/n3-b6-discard-flags/count-fixture-nodes-visit-v2.ps1 -FixturePath <fixture绝对路径> -OutPath <输出路径>`；结果 `node-count-v2.txt`（fixture sha256=`448bab14def9809dbdcc4be1148cd53370fee1a44541535794ac7dfbe5b5cbe8`）、运行日志 `node-count-v2-run.log`（SHA256 见 §15）。
- **历史错误记录（保留不覆盖）**：本轮首版 `count-fixture-nodes.ps1`/`node-count.txt` 曾报 26/63/109/292——其 Walk 仅对容器递增 nodes/paths，漏算全部标量的 node/path 登记（26=89−63 恰为容器路径数、109=401−292 恰为容器实例数），非原算法。原脚本与日志原样保留于 `artifacts/TEMP/n3-b6-discard-flags/`，仅作历史，不再作为有效 PASS。
- 不入 592 GAP 分母、不改 1516 paths / 655145 nodes 历史全量、不升任何旧 73 条为 full。

## 9. 新 TSV（2 条，identity+history 原样保留）

`V5-N3-AI-DISCARD-FLAGS.tsv`：恰好两行，id/package/fixture/caseId/path/idx/kind/parent 与 histClass/originalVerdict/orig* 列**逐字保留原 TSV 值**；新增 proposedVerdict、行为锚点、命令、反证、owner、revisit 与独立 sourceBoundary 列。原 mappingLimits 说明文字（idx3 隐私、容器行）不因本批改变。行级 proposedVerdict 均为 **classified-proposal**（是否接受由 caller 串行裁决），不是 bound/full。

## 10. 残余与新证据的分离

- **残余（仍 pending）**：idx1/idx2 在 ai-v1 冻结 fixture 上仍无弃牌实例——`Assert.Empty` 边界未变；若 caller 不接受本批 proposal，两行保持 pending（期限 2026-11-30 不变）。V4 `PlayerView` 不暴露每张弃牌旗这一事实本身**未被本批消除**，只在 sourceBoundary 中如实陈述。
- **新行为证据（独立增量，供 caller 串行裁决）**：companion fixture `ai-discard-flags-v1` 上 isClaimed/isRiichi 均有非空实例、C# 值级消费与变异反证；满足 §14.1 行 5/6 复访条件的"有弃牌实例及值级消费断言"分支（"exporter/PlayerView 实际暴露此字段"分支**未**采用）。

## 11. PASS / FAIL / BLOCKED / NOT_RUN

| 项 | 状态 |
|---|---|
| 真实 V4 非空 isRiichi/isClaimed 源 + C# 值级消费 | **PASS** |
| companion fixture 仅由真实 exporter 生成、无手填 golden | **PASS** |
| 原件 6/6 通过 + 5 变异值级失败（非空选择） | **PASS** |
| §8a 增量登记（89/63/401/292，原 Visit 算法） | **PASS** |
| 新 TSV 2 条 + 新报告 | **PASS** |
| ai-v1 原行改账（升 classified） | **NOT_RUN**（caller 串行裁决权） |
| 共享台账/校验器/manifest 改写 | **NOT_RUN**（禁改） |
| BLOCKED 项 | 无 |

## 12. 红线复核

未触碰：主 checkout、规划树、兄弟树、用户未提交文件、Unity 环境/Resources/Burst、共享台账/verifier/catalog/manifest/已接受报告、旧 census/ai-v1/既有 AI 测试。未发生：reassign/push/建 PR/触发 CI/改机器级信任/部署/创建 complete.flag。TEMP/TMP 全程指向 `D:\…\artifacts\TEMP\n3-b6-discard-flags\tmp`。改动仅限：exporter kt、companion fixture json、C# 测试 cs、csproj 一个 copy 条目、本报告、新 TSV。V4 生产代码零改动。

## 13. 复现命令（从仓库根）

```bash
# 导出（重生成 fixture；带确定性双跑）
V5_AI_DISCARD_FLAGS_EXPORT=1 ./gradlew :engine:test --tests "com.majiang.state.V5AiDiscardFlagsGoldenExporterTest" --rerun-tasks --console=plain
# 默认模式验证（committed cases vs 实时重推导 + renderSha256 自指）
./gradlew :engine:test --tests "com.majiang.state.V5AiDiscardFlagsGoldenExporterTest" --rerun-tasks --console=plain
# C# 构建 + 原件直跑（记录 -result-xml）
cd dotnet/tests/Mahjong.Ai.Tests && D:/Unity/dotnet/dotnet.exe build -c Release --nologo
D:/Unity/dotnet/dotnet.exe bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests -result-xml <xml路径>
# 变异反证（TEMP）
bash artifacts/TEMP/n3-b6-discard-flags/make-mutants.sh tests/v5-golden/ai-discard-flags-v1.json
MAHJONG_AI_DISCARD_FLAGS_GOLDEN=<mut>.json D:/Unity/dotnet/dotnet.exe bin/Release/net8.0/Mahjong.Ai.Tests.dll -class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests
# §8a 计数（原 Visit 算法复刻）
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/n3-b6-discard-flags/count-fixture-nodes-visit-v2.ps1 -FixturePath <fixture绝对路径> -OutPath <输出路径>
```

## 14. 交付记录

- 本报告与新 TSV 随最小提交进入工作树（提交 SHA 见交付消息）；`artifacts/TEMP/` 不入库。
- 提交清单（6 文件）：`engine/src/test/kotlin/com/majiang/state/V5AiDiscardFlagsGoldenExporterTest.kt`、`tests/v5-golden/ai-discard-flags-v1.json`、`dotnet/tests/Mahjong.Ai.Tests/AiDiscardFlagsGoldenTests.cs`、`dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj`、`doc/v5-重构/V5-N3-AI-DISCARD-FLAGS.md`、`doc/v5-重构/V5-N3-AI-DISCARD-FLAGS.tsv`。

## 15. 返修记录（第 2 轮，D-B6-01/02/03 一次修毕）

| 缺陷 | 修复映射 | 验收证据 |
|---|---|---|
| D-B6-01 MAJOR：首版计数 26/63/109/292 为自造算法（漏算标量 node/path） | §8 改用原 Visit 逐节点复刻脚本 `count-fixture-nodes-visit-v2.ps1`，按 89/63/401/292 登记；旧脚本/日志原样保留为历史错误记录；冻结分母与历史全量不动 | 计数 exit 0（`COUNT_EXIT=0`）；结果 fixture sha256=`448bab14def9809dbdcc4be1148cd53370fee1a44541535794ac7dfbe5b5cbe8`；`node-count-v2-run.log` SHA256=`856bf03396ef9df8ac01aaef721cedc75aa01095e460e370bf143cee8f35e39b`；`node-count-v2.txt` SHA256=`6ad281f6a456ceecaa3eef1a3c1aef6901bc93794f2047af12934caf0bcf2bd0` |
| D-B6-02 MAJOR：新 TSV 两行 package 误为 ai-v1 | 两行 package 改回 `GAP:N3-B6`（fixture 列保持 ai-v1）；`fix-verify-tsv-identity.pl` 按 id 对冻结原行（CRLF 容错读取，仅比较不回写）逐列核对 13 个 identity/history 列 | 核对 exit 0、mismatch=0（26/26 MATCH，恰 2 行 × 32 列）；`tsv-identity-verify.log` SHA256=`9083cc0816395ebc373d3ea7e7d674e396556732f195a1407ed6af15721432f2` |
| D-B6-03 MINOR：两个文档路径不符 expectedFiles | `git mv` 为 `doc/v5-重构/V5-N3-AI-DISCARD-FLAGS.md` / `doc/v5-重构/V5-N3-AI-DISCARD-FLAGS.tsv`；§9/§14 内部引用同步更新；B6 仅作语义名称保留 | `git diff --name-status 85134e9..HEAD` 恰 6 个 expectedFiles；旧 B6 命名路径不存在；`git diff --check` 干净 |

行为证据标注：exporter/fixture/C# 测试源码字节本轮**未变**（HEAD `daa834a` 行为面被 caller 独立复验 PASS）；本轮对行为测试标 **NOT_RUN（源码字节未变，不伪装重跑）**。未创建 complete.flag；不宣告门 2/G1/G5；修毕交回 caller 统一 PR19 集成。
