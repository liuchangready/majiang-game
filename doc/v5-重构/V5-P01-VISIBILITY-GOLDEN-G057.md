# V5 P0.1-a/P0.1-b 可见性/快照深拷贝黄金 —— G-057a..G-057d（R2 返修）

日期：2026-10-25
性质：**严格 test-only**；仅原 5 个 expectedFiles。
本轮：合并遗留 `[D-G057-R2-01]`（R1-01/02 的 C mapped payload 完整比较/未授权 breakdown 覆盖）批量返修。
结论：R2 修复完成；C 的 authorized/unauthorized 全部可见事件逐字段比较，MapPublicSnapshot typed records 逐值/同一性验证，23 个复核变异全部真实 FAIL，提交内 probe 扩为 **67 native runs**。**G1 未批准、G5 未完成、V4 唯一生产**；无平台通过声明。

---

## 1. 基线与范围

| 项 | 值 |
|---|---|
| worktree/branch | `D:/majiang-wt/visibility-golden-g057` / `wt/v5-visibility-golden-g057` |
| 冻结 base | `1432d775526de921f2cde3aba283236fc3b8e8c1` |
| R2 起始 HEAD | `e8a9e9adc401becef6d05d4cf5de972e998df976` |
| 交付 | 仍仅 5 文件；无生产/旧 fixture/catalog/manifest 修改 |

```text
engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt
tests/v5-golden/visibility-v1.json
dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs
tools/v5-golden/probe-visibility-binding.ps1
doc/v5-重构/V5-P01-VISIBILITY-GOLDEN-G057.md
```

---

## 2. D-G057-R2-01 修复映射

### 缺陷（复核实测）

- C 用例只比较 `kind/seat/tileIds/isUraDora`；`expected.settlement.authorized/ unauthorized` 的 `score/han/fu/yaku/turn/fromSeat/calledTileId/riichi/wind/breakdown` 均未消费。
- `MapAll` 构建了 payload 但仅对 Tsumo 做了 4000/3/30 硬编码；`MapPublicSnapshot` 只做 NotNull/Contains。
- 未授权分支整块省略 breakdown；`expected.tsumoBreakdown.unauthorized` 注入私有 ura 也 PASS。
- `uraAuthorized` 由 caller 常量覆盖、输入被忽略，且 Kotlin 对 unauthorized 也写 true。
- probe 没有覆盖 C 的 yaku/han/fu/score/breakdown/authorization、settlement fromSeat/called/riichi/wind 等路径。

### 修复

1. **统一 mapped-event 比较器** `AssertScenarioEvents` 同时用于 G-057b 与 G-057c 的 authorized/unauthorized：对每个可见事件调用 `MapEvent` 后逐值比较 `TurnNumber/Seat/TileIds/Meld(type,tiles,called,fromSeat)/FromSeat/RiichiDiscard/Tsumogiri/WallRemaining/RoundWind/RoundNumber/LoserSeat/Yaku(code+han)/Han/Fu/Score`。
2. **Breakdown 全字段**：`AssertBreakdownShared` 比较 `Han/Fu/TotalPoints/Yaku(code+han)/DoraCount/UraCount/UraTiles/UraHits`；`AssertBreakdownAuthorization` 比较显式 `UraAuthorized`。`uraHits` 组数同时与 `uraHitTiles` 长度互校。
3. **未授权分支不再整块跳过**：
   - V4 unauthorized 的安全非 null breakdown（数值/yaku/空 ura）现在通过 call-safe facts（`includeUraIdentities=false`, `uraAuthorized=false`）映射并逐值比较。
   - raw 私有身份 + `uraAuthorized=false` 实测被 `ScoreBreakdownView` 拒绝；该分支明确是 **caller authorization contract**，不是 V5 自动脱敏。
   - `expected.tsumoBreakdown.unauthorized` 的 `uraTiles/uraHitTiles` 现被消费；注入 34/35 会真实 FAIL。
4. **授权上下文显式化**：
   - raw fixture event 的 `breakdown.uraAuthorized` 是显式 **V5 adapter source-authorization** 输入（不是 V4 ScoreBreakdown 属性）；C# `Facts` 读取并校验：raw identity 与 source authorization=false 组合被拒绝。
   - expected canonical blocks 记录 authorized=true / unauthorized=false；unauthorized 不再写 true。
   - 输出 `uraAuthorized` 变异真实 FAIL。
5. **MapPublicSnapshot 路径**：`SettlementRecords` 从已逐值比较的 `MapEvent` 记录中筛选；`MapPublicSnapshot` 后的 `typedSettlementEvents` 用 `Assert.Same` 逐条验证是同一批完整 payload，而不是只查非空/Contains。
6. **重复表示一致性**：expected settlement 事件内的 breakdown 与独立 `expected.tsumoBreakdown.{authorized,unauthorized}` 都消费；共享字段互校，canonical 额外校验 authorization。
7. **probe**：同一矩阵新增 23 个 C mapped 变异（输入/输出 yaku/han/fu/score/breakdown/authorization、fromSeat/called/riichi/wind/turn、unauth private ura/uraHits），保留原 38 负例（R1 含 18 新增）与 5 正例；总计 **67 native runs**。

---

## 3. 复核 23 变异结果

直接运行复核方 `build/review-g057-r2/extra-mutations.ps1`（fresh DLL，独立 extra-repo，逐次重算 render hash）：

| 变异 | exit | 计数 |
|---|---:|---|
| `input-c-event-yaku` | 1 | 8/0/1/0/0 |
| `input-c-breakdown-yaku` | 1 | 8/0/1/0/0 |
| `input-c-breakdown-han` | 1 | 8/0/1/0/0 |
| `input-c-breakdown-fu` | 1 | 8/0/1/0/0 |
| `output-c-breakdown-yaku` | 1 | 8/0/1/0/0 |
| `output-c-breakdown-han` | 1 | 8/0/1/0/0 |
| `output-c-breakdown-fu` | 1 | 8/0/1/0/0 |
| `output-c-unauth-private-ura` | 1 | 8/0/1/0/0 |
| `output-c-unauth-private-urahits` | 1 | 8/0/1/0/0 |
| `output-c-event-score` | 1 | 8/0/1/0/0 |
| `output-c-event-han` | 1 | 8/0/1/0/0 |
| `output-c-event-fu` | 1 | 8/0/1/0/0 |
| `output-c-event-yaku` | 1 | 8/0/1/0/0 |
| `output-c-event-turn` | 1 | 8/0/1/0/0 |
| `input-c-event-turn` | 1 | 8/0/1/0/0 |
| `output-c-event-fromseat` | 1 | 8/0/1/0/0 |
| `output-c-event-called` | 1 | 8/0/1/0/0 |
| `output-c-event-riichi` | 1 | 8/0/1/0/0 |
| `output-c-event-wind` | 1 | 8/0/1/0/0 |
| `input-c-event-fromseat` | 1 | 8/0/1/0/0 |
| `output-c-event-breakdown-ura` | 1 | 8/0/1/0/0 |
| `input-c-breakdown-authorization` | 1 | 8/0/1/0/0 |
| `output-c-breakdown-authorization` | 1 | 8/0/1/0/0 |
| baseline | 0 | 8/0/0/0/0 |

`extra-results.csv`：1 baseline exit0，23 变异 exit1；无 skip/error/notRun。

---

## 4. 字段分类（R2 修正）

| 类别 | 字段 |
|---|---|
| INPUT | state（136 placement/melds/discards/hands/sleeve/lastDiscard/privateCheat）；event 全字段（turn/seat/tiles/ura/fromSeat/called/flags/wall/round/yaku/han/fu/score/breakdown）；`events[].breakdown.uraAuthorized`（显式 source-authorization adapter，不是 V4 字段）；clock；viewerSeat/legalActions/policy |
| OUTPUT | V4 expected snapshot/seatViews/board/session/settlement/hiddenTileIds；canonical `tsumoBreakdown.{authorized,unauthorized}`（含显式 authorization）；mapped `ProjectedEventPayload` 全部适用字段；Breakdown 全字段；MapPlayerSnapshot 的 sleeve/owner meld/cheat；MapPublicSnapshot typed settlement records |
| STRUCTURE | 136 唯一实体/位置、wall/王牌/dora 槽、currentPlayer==dealer、输入重复表示一致性、事件 turn 非降序、cheat balanceAfter==points、uraHits 组数==uraHitTiles 长度、case/G 行唯一 |
| META | schema/version/commit/taskPaths/sourceHashes/exporterSource/renderSha256LfNormalized |
| SOURCE_ONLY | `v4WallPointer/v4WallEndExclusive/v4RinshanPointer/kanCount/v4DeadWall/drawSource/legalActions/sessionUpdateWholesaleCopy/v4CheatPointsChangedVisibilityCounts`；`breakdown.indicatorTiles`（V5 无同形字段）；`breakdown.score`（V5 用事件 Score）；V4 `uraHits` 组数（映射到 V5 UraHits 列表并互校） |
| UNREPRESENTABLE | V4 legalActions→P3.3 DecisionView；V4 未过滤 SessionUpdate 列表；V4 session summary 全形状；V4 暗杠公开身份；V4 river turnNumber 分配 |

**不再声明**“Yaku/breakdown 未绑定”或“unauthorized 整个输出已对齐”。未授权分支准确口径：V4 安全非 null breakdown 可映射并已逐值比较；V4 raw 私有身份需要 caller 显式授权，V5 不自动裁剪。

---

## 5. 测试计数 / exit（最终）

| 项 | exit | 计数 |
|---|---:|---|
| Release solution `--no-incremental` | 0 | 0 warning/error |
| TableRuntime.Tests 构建 | 0 | 0 warning/error |
| C# `VisibilityGoldenTests` 定向 | 0 | Total=8, Errors=0, Failed=0, Skipped=0, NotRun=0 |
| TableRuntime 全量 | 0 | 373/0/0/0/0 |
| Domain TilePositionMap | 0 | 59/0 |
| Domain TileMovementGolden | 0 | 10/0 |
| Domain CheatVisibility | 0 | 10/0 |
| Contracts ProjectionWireMapper | 0 | 16/0 |
| Contracts ProtocolContract | 0 | 30/0 |
| Gradle exporter | 0 | tests=2, failures=0, errors=0, skipped=0 |
| Gradle SessionSnapshot | 0 | tests=8, failures=0, errors=0, skipped=0 |
| Gradle PublicProjection | 0 | tests=12, failures=0, errors=0, skipped=0 |
| 提交内 probe | 0 | **67 native runs**：baseline + 61 FAIL 负例 + 5 PASS 正例 |
| Probe self-negative | 1 | 绿色 baseline 后故意 exit1 |

---

## 6. Fixture / 证据

- fixture：141007 bytes，LF；raw/LF SHA-256 `30291ec270efb6f2a7d8c6984d53e7ae2dbd5ae45a41a7114ee69288749f346e`；`renderSha256LfNormalized=642cfdda1c11587c63ae9f8587c24c4959f7ed3e8bea5bd51d70a36a7b4cb4b8`。
- `v4ProductionTreeClean=true`；`trackedModifiedFiles` 为 task paths。
- probe evidence：`build/visibility-binding-probe/20261025T102947Z-46996/`；`results.csv` SHA-256 `32ca7da6a64d121df365866d1805684fb8f5cefb863edc7412b728e6d79bcf7d`。

| 证据 | SHA-256 |
|---|---|
| `build/r2-solution-build.log` | `e349863039e7990699ffdb3731c3cb95ab3ea5af54da8ea5107d7b3f427c46e4` |
| `build/r2-trt-build.log` | `54747d2a7e27139aba02fabfe6bc65f391f516e4d43a269225fde6af212da3ba` |
| `build/r2-csharp-run3.log` | `ec4699a00de88ef48e5aec61b84fa318af65a67b49c037707e95f9c4ab76e24c` |
| `build/r2-trt-full.log` | `703596fef50c1cbf649ffea7e9a44c6cd6cf4ce78511f88bc85a09e6dd9270db` |
| `build/r2-domain-TilePositionMapTests.log` | `7ea7ca5c179fe28809e8875069f023e382311d4f21b13448586c00bedda75358` |
| `build/r2-domain-TileMovementGoldenTests.log` | `5efeeb171085acf2a3cc004c286c86a4144ad016d5dbe071359670feda44e6ea` |
| `build/r2-domain-CheatVisibilityTests.log` | `521341214d655107755fdbf55b84a7d16adfaf29d34b37a8cfd78ab56f83e734` |
| `build/r2-contracts-ProjectionWireMapperTests.log` | `b186872b9154e028b35df045b23c9bf5b09da59270f137a7510b194b8a0bf3bc` |
| `build/r2-contracts-ProtocolContractTests.log` | `ed3e8a25f046acf3fc889dd91a792b2048148a66396d70dfaef63ae2b1f469f0` |
| `build/r2-gradle-final.log` | `8f1fdf67147e30f3695cab76b383f2eb42526d4863b0c51733e0da902c031d02` |
| `build/r2-export3.log` | `7a24a3824b638e46268f191d4711f15c56fa933bb4391382eed85060aa7fdd9a` |
| `build/r2-probe-final.log` | `a854dff58069758d63ef78ff63a8d40560df8c9f61b18335eda8fa462d03c3ee` |
| `build/r2-probe-selfneg.log` | `2f9b6012771a680471cf1ad58e9e35d5a9a311db36de82349e90ce6ee5d46ff0` |
| `build/r2-extra-final.log` | `26f320de77a06e15a8fb11acacf59f9e707dec82ac0be5309c93f67998c2d896` |
| `build/review-g057-r2/extra-results.csv` | `2df0a7fa005bc72a3af2c8eaec9999251536f54a0ede891e8061ef75dab4dba7` |
| probe `results.csv` | `32ca7da6a64d121df365866d1805684fb8f5cefb863edc7412b728e6d79bcf7d` |
| probe `baseline.log` | `de4aac7f29e1127d50f0253993e6e99d7ef6f092754bb871e6f8944ec48ec631` |
| probe `negative-c-output-event-score.log` | `536c908a8e6383674b50b449553be769e708ab5a37ede5dddd64af712398ce92` |
| probe `negative-c-output-unauth-private-ura.log` | `cb09749b828c6b33e85fd736595a11d1cb806488f7f41fbafc2411ee4aafcc76` |

---

## 7. 仍存在的缺陷/差异（不在本轮修复权限）

| ID | 级别 | 状态 | 内容 |
|---|---|---|---|
| D-01-01 | MAJOR | BLOCKED（V4 冻结） | V4 `CheatPointsChanged` 泄漏四席；V5 仅 owner |
| D-01-03 | MINOR | UNREPRESENTABLE | V4/V5 session summary 不同形；仅 finished=false 可比 |
| D-01-04 | MINOR | UNREPRESENTABLE | V4 legalActions vs P3.3 DecisionView |
| D-01-05 | MINOR | 差异（V5 更严格） | V4 公开暗杠身份；V5 隐藏占位 |

已确认并撤销的失实说明：D-01-02（无 breakdown 映射）、D-01-06（袖牌无法映射）不成立。
表示差异：V4 未过滤 SessionUpdate 列表、river turnNumber 分配、breakdown.indicatorTiles/breakdown.score。

---

## 8. 边界

- **G1 未批准，G5 未完成**；不导出 73 例、不改 catalog/manifest。
- **V4 唯一生产**；无平台重跑/平台通过声明。
- 未 push/PR/CI/默认分支/全局环境变更；caller 通过后统一 MR。
