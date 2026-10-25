# V5 N3 袖里藏花冻结 next-wall 输入补齐（F-01）

日期：2026-10-25。工作树：`D:/majiang-wt/sleeve-frozen-input`，分支 `wt/v5-sleeve-frozen-input`。冻结基线 `d6bc69fa930d005b355a1273ec732fbdf82bbe2f`（本任务唯一 PR19 最新已发布已接受头），`git merge-base --is-ancestor` exit 0，`git status` clean 起始。本报告只关闭已接受 N3 cheat-v1 的 **F-01“缺冻结 nextWall 输入”**，不是 N3/G1/G5 通过；V4 仍是唯一生产架构。

## 0. 结论

| 对象 | 结果 | 证据 |
|---|---|---|
| F-01 `sb-activate-wall-empty-drift` | **CLOSED**：同输入真实调用 Rejected / `SLEEVE_BLOOM_WALL_EMPTY` | `CheatGolden` 32 个 sleeve case 全绿；readiness native0 |
| 75 handler 分母 | **68 PASS / 0 FAIL / 7 BLOCKED** | manifest 绑定 68；其它 7 个 BLOCKED 原样保留 |
| 外部单字段变异 | **46/46 FAIL_AS_EXPECTED** | 每例 native1，Total95，Failed 1–4，0 unexpected/tool/wrong |
| readiness | **PASS**：native0 / Total1 / Failed0 / Skipped0 / NotRun0 | 不再是能力门或 `BLOCKED_INPUT` |
| 默认探针整体 | `MATRIX=PASS` / `READINESS=BLOCKED`（7 个其它 case）/ `MATRIX_PASS_READINESS_BLOCKED` / exit1 | 不把关闭 F-01 冒充 N3 通过 |
| Domain 全量 | **483 passed / 0 failed / 0 skipped / 0 Not Run** | native exit 0 |
| solution 全量 | AI438 + Contracts149 + Client132 + Domain483 = **1202 passed / 0 failed / 0 skipped** | native exit 0 |
| TableRuntime 全量 | **365 passed / 0 failed / 0 skipped / 0 Not Run** | native exit 0 |
| Release 构建 | solution 与 TableRuntime 均 **0 warning / 0 error** | 均 `--no-incremental`，native exit 0 |
| PG/WSS/Unity/IL2CPP/Android/真实网络/Gradle/远端 CI | **NOT_RUN** | 无相应生产变更，未重跑重型平台 |

## 1. 范围与输入源

本任务由 caller 在独占 worktree 中明确授权修改 C# 生产输入合同。只改以下 expectedFiles：

- 新增 `dotnet/src/Mahjong.Domain/Cheat/SleeveBloomActivationInput.cs`
- `dotnet/src/Mahjong.Domain/Cheat/CheatSubmission.cs`
- `dotnet/src/Mahjong.Domain/RoundCheatTransition.cs`
- `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenBindingTests.cs`
- 新增 `dotnet/tests/Mahjong.Domain.Tests/SleeveBloomFrozenInputTests.cs`
- `dotnet/tests/Mahjong.TableRuntime.Tests/N1bCheatExecutionTests.cs`
- `tools/v5-golden/probe-cheat-binding.ps1`
- 本报告

V4 七模块源码、`tests/v5-golden/cheat-v1.json`、catalog/manifest、统一 verifier、项目/solution、主 checkout 用户未提交文件均未修改或复制提交。未新增 wire/client 字段，未改规则版本、费用、时钟或 RNG。

输入源锚点：

- V4 处理入口 `engine/src/main/kotlin/com/majiang/authority/cheat/SleeveBloomSubmissionHandler.kt:224-284`：`nextWallTileId`/`nextWallTileInWall` 在 NORMAL 分支校验，缺失或不在墙内 → `SLEEVE_BLOOM_WALL_EMPTY`；该检查在 `cheatPoints < cost` 之前。
- V4 权威构造 `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt:955-995`：`nextTile = candidateState.nextAvailableWallTile()`，`nextWallTileInWall = tilePool.getLocation(nextTile) == WALL`。
- V4 可用墙槽 `engine/src/main/kotlin/com/majiang/state/GameState.kt:576-613`：从槽 0 扫描（不是从 pointer），跳过已离墙实体，跳过受保护 offer/reservation；`nextAvailableWallIndex()` 取首个。
- V4 exporter `engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt:716`：`case("sb-activate-wall-empty-drift", activation(nextWallTileInWall = false), rejected("SLEEVE_BLOOM_WALL_EMPTY"), "candidate left the wall")`；默认 `nextWallTileId=32` 见同文件 443-444，且该 case 未导出 placements。C# 测试据此重建输入并明确标注了 source anchor，未改 fixture、未制造期望。

## 2. 实现

### 2.1 不可变 typed input

`SleeveBloomActivationInput` 显式区分：

- `Unspecified`：旧调用未提供冻结证据；保留原 map-only 搜索，其它合法墙候选仍 Allowed。
- `Frozen(TileId? nextWallTileId, bool nextWallTileInWall)`：权威决策面观察到该候选；**显式 null 也拒绝**，不得回退为重新搜索。
- `FromAuthoritativeState(map, sleeve)`：按 V4 `nextAvailableWallTile()` 语义从只读 map + cheat state 构造；无候选时返回 `Frozen(null,false)`，不是 `Unspecified`。
- `TryResolveFirstAvailableWallCandidate`：唯一扫描实现；`CheatSubmission.FindFirstAvailableWallIndex` 改为委托它，避免两套语义漂移。

### 2.2 Domain 校验

`CheatSubmission.ActivateSleeveBloomWithFrozenInput` 在 NORMAL 分支：

1. 冻结证据缺失 / `nextWallTileInWall=false` → `NoWallCandidate`。
2. 候选必须在 `TileZone.Wall` 且 `0 <= index < WallEndExclusive`（越界拒绝）。
3. 候选不得被 off-turn offer / reservation 保护。
4. 候选必须等于当前权威 first-available 墙槽（槽与实体都一致）；不等即 drift，拒绝而**不换另一张牌**。
5. 候选必须实际占用其墙槽（实体一致性）。
6. 通过后才进入原 `Charge`（费用/点数检查）。

顺序与 V4 一致：墙候选缺失先于 `INSUFFICIENT_FUNDS`；新增测试 `FrozenDriftPrecedesInsufficientFunds_LikeV4Order` 固定该语义。冻结的 true/false 与权威 map 必须同时成立：客户/调用方声称 `true` 但实体已离墙会拒绝；声称 `false` 但实体仍在墙也拒绝，任一方向都不能覆盖权威事实。

旧 API 保留原 7 参数签名与行为，内部委托 `Unspecified`；新入口使用**不同方法名**，不产生 `GetMethod` 重载歧义。RINSHAN 路径完全不变（已有 dead-wall 证据），DEAL_EXTRA 语义不变。

### 2.3 权威入口

`RoundCheatTransition.Prepare` 的 `SubmitSleeveBloom` 在 NORMAL draw 时用同一份只读 `round.Tiles` 与 `before.SleeveBloom` 构造 `FromAuthoritativeState`，再调用 typed 入口；RINSHAN 传 `Unspecified`。不从 wire/client 读取候选，不新增隐藏泄露，不引入时钟/RNG/全局可变状态。

拒绝路径返回 `CheatTransition.Rejected`：`State=null`、`Map=null`、`Events=[]`、`Changed=false`。测试逐项断言 state version、ledger version/balance/entries、marks version、sleeve version/offer/reservation/usedOnTurn、beam usedOnTurn、map state hash/revision/完整 136 placements 均未变化。

## 3. 测试

新增/更新的真实调用：

| 用例 | 断言 |
|---|---|
| `sb-activate-wall-empty-drift`（golden theory） | 冻结 32/false；map 上 32 是 river、40 是合法墙候选；Rejected `NoWallCandidate`，零副作用 |
| `LegacyMapOnly_WithoutFrozenInput_StillAllowsTheFirstLiveWallCandidate` | 旧调用仍 Allowed，SecondTile=first available |
| `FrozenAuthoritativeCandidate_IsReplayedExactly` | 冻结合法候选 Allowed，SecondTile 精确 |
| `FrozenReservationAwareCandidate_SkipsTheProtectedTile` | reservation 被跳过，冻结值不换牌 |
| `FrozenNullCandidate_RejectsWithoutSubstitutingOrMutating` | 显式 null 拒绝 |
| `FrozenCandidateClaimedInWallButPhysicallyOffWall_Rejects` | 声称 true 不能覆盖实体 |
| `FrozenCandidateClaimedOffWallButPhysicallyInWall_Rejects` | 声称 false 也不能覆盖实体 |
| `FrozenCandidateReservedByAnOffTurnSelection_Rejects` | 被预留候选拒绝 |
| `FrozenCandidateInsideTheDeadWall_Rejects` | 非 live-wall 区域拒绝 |
| `FrozenCandidateThatIsNotTheFirstAvailableWallTile_RejectsInsteadOfSwitching` | 不自动换牌 |
| `FrozenDriftPrecedesInsufficientFunds_LikeV4Order` | 墙空优先于点数不足 |
| `FrozenHoleBeforeTheWallPointer_IsTheAuthoritativeCandidate` | pointer 前解锁洞可作为权威候选（V4 从 0 扫描） |
| `frozen_wall_activation_skips_a_foreign_reservation_and_replays_the_same_receipt`（actor） | 正常施术；foreign reservation 被跳过；同载荷 replay 返回同一 receipt，不重复扣费/加版本 |
| `sleeve_activation_commit_failure_publishes_nothing_and_leaves_no_charge_or_offer`（actor） | commit 失败不发布、无扣费/offer/版本；重试成功只扣一次 |
| `frozen_missing_wall_candidate_rejects_activation_with_zero_side_effects_and_replays`（actor） | 权威无墙候选时 actor 拒绝、零副作用、同载荷重放稳定 |

`CheatGoldenBindingTests.BindingManifest_PreservesTheFullDenominator` 更新为 68 bound / 7 blocked（4 actor + 3 unrepresentable），分母 75 不变。`SleeveBloomSpecs` 仍 32。

## 4. 复现命令与结果

最终证据根：

`D:/majiang-wt/sleeve-frozen-input/artifacts/sleeve-frozen-input-final-20261025-143449`

（`artifacts/` 由仓库 `.git/info/exclude` 忽略，未提交；不是唯一私有 TEMP 来源。）

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noLogo
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1 -Repo D:/majiang-wt/sleeve-frozen-input -EvidenceRoot <new-exclusive-dir>
```

| 检查 | 计数 | native exit |
|---|---|---:|
| solution build | 0 warning / 0 error | 0 |
| solution tests | AI438 + Contracts149 + Client132 + Domain483 = 1202 / 0 failed / 0 skipped | 0 |
| TableRuntime build | 0 warning / 0 error | 0 |
| TableRuntime tests | 365 / 0 failed / 0 skipped / 0 Not Run | 0 |
| Domain focused golden sleeve | 32 / 0 / 0 | 0 |
| Domain focused frozen input | 12 / 0 / 0 | 0 |
| actor focused frozen | 3 / 0 / 0 | 0 |
| actor focused commit-failure | 2 / 0 / 0 | 0 |
| probe positive control | Total95 / Errors0 / Failed0 / Skipped0 / NotRun0 | 0 |
| probe mutations | 46/46 FAIL_AS_EXPECTED，各 Total95，Failed 1–4，0 unexpected/tool/wrong | 各 1 |
| probe readiness | Total1 / Errors0 / Failed0 / Skipped0 / NotRun0 | 0 |
| probe overall | MATRIX=PASS，READINESS=BLOCKED，blockedCount=7 | 1 |

`READINESS=BLOCKED` 的原因是保留的 7 个独立 BLOCKED：`tm-wrong-phase-round-end`、`tm-capacity-tiles`、`tm-no-active-decision`、`tm-deadline-expired`、`tm-decision-mismatch`、`bm-activate-deadwall-empty`、`bm-activate-deadwall-dora-only`。F-01 不再在 blocked 列表中；未声称 N3/G1 通过。

## 5. 证据 SHA-256（完整）

源码：

> 下表按本 Windows 工作树的实际字节（含 CRLF）计算；Git 提交 blob 会做行尾规范化，复核时请对同一 worktree 执行 `Get-FileHash -Algorithm SHA256`。提交本身的准确 SHA 以交付答复中的 `git rev-parse HEAD` 为准。

| 文件 | SHA-256 |
|---|---|
| `SleeveBloomActivationInput.cs` | `BD7476F6D2F6680303E4180FFB7FE05F160A038BE2888611BCCC4309E7569FFC` |
| `CheatSubmission.cs` | `CD3E18980981FC04ADE1661BE5A14ADA1CC1AF767E868BA7C361AD9BC50DBD12` |
| `RoundCheatTransition.cs` | `E4F815B3143F13DA3E1C901570582F380BC027ACDF780430CB33D6D796FF27E7` |
| `CheatGoldenBindingTests.cs` | `FBCCBE81277DD02B53F2CECE4A2F17E0570EB0D87E66C47DE9930C50A8A69AC0` |
| `SleeveBloomFrozenInputTests.cs` | `3A7D8A516C6CD46C99C3E6287A7905F524271092F51BD2A81BAE3D03D96A5272` |
| `N1bCheatExecutionTests.cs` | `5242A25F4E752BC6F630730496DE8A062BB722FD0F3A542F27306881D49020E0` |
| `probe-cheat-binding.ps1` | `641B15D662D923072928EFE1D646884002E3FD3D7014A944B37D0299A7C39763` |

证据（均为上述 final 证据根下文件）：

| 文件 | SHA-256 |
|---|---|
| `solution-build.log` | `5527EAB5D5887A398662525B782A367A3CF4A3A71D46BB8AAF6674FCAB99522D` |
| `solution-tests.log` | `FDFF1B7A1669C3BF376745771A9A14D2CB38B62D8EBCA89CBA3F9981C5EC9567` |
| `runtime-build.log` | `274E21A4F52895A64BAD3A5A0844618F7925E37EB197FDA0190E6E61EF319B6F` |
| `runtime-tests.log` | `F7F67ED7C27716724289AF710977AB910331857CD709750CA6EB1C72B2197A99` |
| `focused-golden-sleeve.log` | `A4EFB77F5838B1C9B73314C6E99C7061559A775B662CEFF63A67BC87FA1C8F67` |
| `focused-frozen-input.log` | `20F1BB61835D96A002C77E59117C10A4C5AAFE1362B51A5055A2C6213A9903A5` |
| `focused-actor-frozen.log` | `ACC62B3B5E315D5C0BE54AE2F9BF663A53E5DE7355120D633BE9AEE657CF0877` |
| `focused-actor-commit.log` | `6A3A39504F4EFFD8F095612ADD49C17B0D955F612F5828EA6C861A1B354AEF52` |
| `probe/summary.json` | `272CE637C9E75F9EF0440A4EF7117A76198C1212CCBA6E23F6CB613DA8402990` |
| `probe/matrix/matrix.csv` | `55F009B068EDFAE4EC375B90927A577A1D22AD6ED0190F46EFF34E9BA69FFDD2` |
| `probe/evidence-sha256.csv` | `444B762852BC9F1B5E81964961768C7EA9C3AE65FBE675BAD8BE792E10D17B03` |
| `probe/readiness/readiness.log` | `8AFA3519ABC3C957C0FC20CC403E700E266831B8D417D74913F9FCC120140BFE` |
| `probe/readiness/build.log` | `B7099963E5C89C058D722A87EB10FEE399A9F6555DF473E4137D7CE0DB3134EF` |
| `delivery-evidence-sha256.csv`（202 文件全清单） | `6F8C4BB56803999571FB64895B72E92C220A2441FEE15115BCBC7953DAFE9C19` |

历史失败证据（未删除，保存在同一证据根 `history/`）：

| 文件 | SHA-256 |
|---|---|
| `domain-first-run-2-failures.log` | `465D59F146B5ED627982B058EA242BBA9C647B62F1413FF189FC5046542F6900` |
| `domain-dotnet-test-2-failures.log` | `91F9B5FD8779DE5D150746032193285CBA42692B9D9C78AC0AAE942BBF8C903A` |
| `runtime-first-run-1-failure.log` | `9F3AC1ECF437A21CBB6254336DE59427D2EF758BC2AAFCDFCC648FD658184114` |

历史失败原因均已在测试内修正：一是 `TilePositionMap` 守恒校验禁止墙槽越过 `WallEndExclusive`，越界负例改用 dead-wall 实体；二是“零副作用”断言不能假定初始 reservation 为空；三是 actor commit 失败断言必须排除此前 discard 已发布的 publication。生产代码未因这些测试自身缺陷而放宽。

## 6. 兼容性与剩余门槛

- 旧 `CheatSubmission.ActivateSleeveBloom(state,map,context,economy,sleeveConfig,seat,requestId)` 保持原签名与原 map-only 行为；合法其它墙候选的正控制仍 Allowed。
- 新入口不同名，反射 `GetMethod` 无重载歧义；无新增可选参数或 wire 字段。
- `rulesetVersion=1`、费用 10、draw kind、rinshan/dead-wall、136 守恒、事件/ledger 语义均未改变。
- 未改 V4 源码、exporter、fixture、catalog/manifest、统一 verifier、项目/solution、默认分支或环境。
- 未 push、未创建/合并 PR、未触发远端 CI、未转派、未读取兄弟任务未提交产物。

仍保持：

- **BLOCKED**：7 个 handler（4 个 tile-mark actor 决策/截止时间；`tm-capacity-tiles` F-06；2 个 beam dead-wall 过滤态 F-02）。
- **不在此任务**：F-02/F-06、随机输入、AI 重放等。
- **NOT_RUN**：PG/WSS、Unity/IL2CPP、Android/真机、真实网络、V4 Gradle 全量、远端 CI。
- **G1 仍 BLOCKED，G5 未完成，V4 仍是唯一生产架构**。本报告不把组件 PASS、readiness PASS 或目录 validator 当作 G1/G5 通过。

本报告与上述源码/测试位于同一本地提交；提交 SHA 由 caller 在 `git log -1` 核对，等待独立复核、隔离集成、push 及唯一 PR19 `feat/v5-1-integration -> v5` 与 merge-back。
