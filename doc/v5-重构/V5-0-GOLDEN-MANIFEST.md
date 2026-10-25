# V5-0 黄金样本清单（P0.1）

日期：2026-10-23（原始盘点）；2026-10-24（当前事实与逐项核对更新）
分支/基线：原始盘点 `v5` / `48125ff`；当前核对 PR19 head `205506f07f6a1ccfef6b32cf97390431e3349f8d`
状态：**历史样本目录设计 + 当前五组已导出 fixture 的机械证据索引；不是 G1/G5 验收，不是全 73 例对齐结果。**

## 0. 当前事实与历史盘点

### 0.1 原始盘点（2026-10-23，历史，保留）

以下五条是 2026-10-23 P0.1 设计时的真实状态，保留作为历史证据；它们不再代表 2026-10-24 工作树：

- `tests/v5-golden/` 当时不存在；当时没有创建任何样本文件。
- **当时已导出样本 0 个**。因此当时已导出数为 0、对齐率暂无分母，不能计算百分比，也不能写“V5 已通过”。
- 所有 V4 行为样本当时标 `pending` 或 `pending-v4-replay`；只属 V5 的新能力标 `v5-only`，不计入 V4 对齐率。
- 本清单当时没有填写任何预期 `stateHash`、`event_hash`、`snapshot_hash`。哈希必须在真实导出时由 V4 运行结果生成；手填哈希视为伪造。
- V4 源码/测试在 `f8f4543..48125ff` 之间无 diff；原始表格引用当时 `48125ff` 工作树路径与行号。

### 0.2 历史事实（2026-10-24，以 `205506f` 五组文件为准；N3-C1 前口径，保留）

- `tests/v5-golden/` 现有 **5 个** 已导出的 V4 fixture：`physical-v1.json`、`scoring-v1.json`、`round-v1.json`、`cheat-v1.json`、`shuffle-v1.json`。文件冻结在 PR19 head `205506f07f6a1ccfef6b32cf97390431e3349f8d`；各文件内嵌的 `v4Commit` 是原始导出时的 V4 提交，可能不同于冻结提交。
- 本任务新增机器可读目录 `tests/v5-golden/catalog-v1.json`，逐文件记录真实 SHA-256、schema、源 V4 commit/exporter 命令、实际样本 ID、分组计数、C# 消费位置/定向命令、G-xxx 锚点。
- 本任务新增只读校验器 `tools/verify-v5-golden-catalog.ps1` 和 TEMP 自测 `tools/tests/VerifyV5GoldenCatalog.Tests.ps1`。校验器从真实 JSON 重算文件/源码哈希、分组计数、ID 唯一性和锚点；损坏 catalog、SHA 变更、重复 ID、未知 schema、缺文件均 exit 1。正常 exit 0；缺工具 exit 2 BLOCKED。
- 五组 fixture 的真实文件事实：

| fixture | bytes | SHA-256 | schema | 内嵌 V4 commit | 具体 case |
|---|---:|---|---|---|---:|
| `physical-v1.json` | 836,369 | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | `v5-physical-golden-v1` | `32f645e` | 6 |
| `scoring-v1.json` | 8,315,131 | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | `v5-scoring-golden-v1` | `97831c8` | 10,238 |
| `round-v1.json` | 43,474 | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | `v5-round-golden-v1` | `21a7d2c` | 25 |
| `cheat-v1.json` | 42,120 | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | `v5-cheat-golden-v1` | `a96c195` | 81 |
| `shuffle-v1.json` | 99,467 | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | `v5-shuffle-golden-v1` | `37b4dd6` | 32 |

- **73 条 G 目录需求**的当前覆盖：`full` 17、`partial` 20、`none` 33、`v5-only` 3。`full` 表示该 G 条目的全部约定断言在当前五组 fixture + C# 读取范围内都有证据；`partial` 表示只覆盖部分断言并单列缺口，不冒充整项完成。**`full` 仍不是 G1 对齐率或验收结论**；`none` 的 33 项仍保留责任任务。
- **分母彼此独立，不能相加**：73 是 G 目录需求；10,382 是五组 fixture 的具体 JSON case；其中 `scoring-v1` 的 10,000 条 `randomSamples` 是这 10,382 的子集，不是另一个可加分母；42 是 5 个 C# Golden reader 类的 `[Fact]` 数；5 是 fixture 文件数。另有 5 个 V4 exporter 和 5 个 C# reader 类。
- 五组 fixture 的存在不等于 73 条需求全部导出。33 条 `none`、20 条 `partial` 仍不得写成“已对齐”；G1 仍 `BLOCKED`，G5 未完成，V4 仍是唯一生产基线。
- 未导出/纯 V5 需求继续按 §8.2 的责任任务保留；本清单没有把在途 G-049 AI 导出或任何兄弟工作树代码写入本目录。

---

### 0.3 N3-C1 增量目录（2026-10-25，索引冻结头 `004c247`；`205506f` 历史 pin 保留）

- 本节是 N3-C1 的增量口径，不重写 §0.1/§0.2 的 2026-10-23/24 历史结果。旧五组 fixture 的文件字节、SHA-256、内嵌 `v4Commit`、`caseTotal` 和原报告全部不变；`205506f07f6a1ccfef6b32cf97390431e3349f8d` 仍是它们的 fixture freeze。当前 catalog 索引在 `004c24708917168877efaba2565da9dc375fe170` 刷新，**不是** fixture 重新导出。
- **caller 裁决**：把真实已接受 `tests/v5-golden/ai-v1.json` 纳入 `catalog-v1.json` 的 `fixtures[]` 和本 manifest；G-049 只从 `none` 登记为 `partial`。不得写成 `full`、不得写成“全部 AI 移植”、不得写成 G1 通过。
- `ai-v1.json` 实测：schema `v5-ai-golden-v1`；51,271 bytes；SHA-256 `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；LF 归一化 `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2`；Git blob `032d79e95e45bde9cb29046cad5f49f338fa78f3`。fixture 内嵌 `v4Commit=205506f...`（等于历史 fixture freeze，不是当前索引头），`v4EngineSourceCommit=3f5370c...`、`v4DotnetAiSourceCommit=a96c195...`、`v4DotnetDomainSourceCommit=5f4f829...`、`generatedAtHead=b49911c...`。
- 生成命令（fixture 内嵌原文）：`$env:V5_AI_GOLDEN_EXPORT='1'; .\gradlew.bat :engine:test --tests "com.majiang.state.V5AiGoldenExporterTest" --console=plain`；exporter `engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt`，raw SHA-256 `F70DA6A4AFBB06FA1BC80F2D0A6573BFB5A63DD302B2CA52122A551D623C76F8`，LF SHA-256 `0E4289DE81E9BCC31D9D37E1A22D79D63816E807A4619912A70B787E1E3DA863`。
- 六组目录事实（旧五组沿用 §0.2 原值，不重新导出）：

| fixture | bytes | SHA-256 | schema | 内嵌 V4 commit | 具体 case | catalog C# reader | Facts |
|---|---:|---|---|---|---:|---|---:|
| `physical-v1.json` | 836,369 | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | `v5-physical-golden-v1` | `32f645e` | 6 | `PhysicalGoldenTests` | 9 |
| `scoring-v1.json` | 8,315,131 | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | `v5-scoring-golden-v1` | `97831c8` | 10,238 | `ScoringGoldenTests` | 7 |
| `round-v1.json` | 43,474 | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | `v5-round-golden-v1` | `21a7d2c` | 25 | `RoundReplayGoldenTests` | 6 |
| `cheat-v1.json` | 42,120 | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | `v5-cheat-golden-v1` | `a96c195` | 81 | `CheatGoldenTests` | 16 |
| `shuffle-v1.json` | 99,467 | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | `v5-shuffle-golden-v1` | `37b4dd6` | 32 | `ShuffleGoldenTests` | 4 |
| `ai-v1.json` | 51,271 | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | `v5-ai-golden-v1` | `205506f` | 7 | `AiReplayGoldenTests` | 3 |

- 具体 JSON case：**10,389** = 旧五组 10,382 + ai-v1 的 7 个执行 case。ai-v1 的 `decisionClock`、`recordedIntent`、`v5Observation`、`reproducibility`、`sourceFileSha256`、`unexportedOrMappedSamples` **不是**执行 case，不能重复计数；`legalActionRefs`/`mappingLimits`/events 也不是 case。
- G-049 的 `partial` 边界：真实 `Replay → Submit → DeadlineArbiter → 同一接受动作 → RoundStateMachine` 与 6 个拒绝 case 的零副作用已有 C# 证据；但 `mappingBasis`/`mappingLimits`（GAP:N3-B6）、`v4.events[*].text`/`v4Call.action.text`（GAP:N3-B9）、全 case 的 C# 后态 vs V4 全树差分、AI host/cheat intent 仍未绑定或未导出。故 G-049 只能 partial。
- **Δ29 的确定分解（不是漏洞、不是同一分母）**：旧五组 catalog 口径 10,382 = physical 6 + scoring 10,238 + round 25 + cheat 81 + shuffle 32；冻结 N-3 审计的 replay 案例口径 10,353 = physical 6 + scoring 10,238 + round 2（只 `roundTerminal`+`chankanRon`）+ cheat 75（只三个 handler 数组）+ shuffle 32。Δ = 10,382 − 10,353 = **29** = round **+23**（catalog 多计 `matchProgression`16 + `decisionClock`6 + `controlEpoch`1）+ cheat **+6**（catalog 多计 6 个 `tileMovement`）。这 29 是**旧 catalog 相对旧审计定义多出的条目**，不是新增样本，也不加到 10,389。
- 分母各自独立：73 = G 需求行；10,389 = 六组具体 JSON 执行 case；10,000 = scoring `randomSamples`，是 scoring 10,238 的子集，不另加；45 = 六个 catalog C# reader 类的 `[Fact]`（Domain 42 + Ai 3）；6 = fixture 文件数。另有 `AiReplayStateGoldenTests` 3 个已接受状态绑定 Fact，属独立证据类，**不加入** catalog 的 45 Fact 分母。审计全集 1,516 归一化路径 / 655,145 节点实例 / 592 GAP units / 17 UNA units 已包含 ai-v1，本次不改这组分母，也不给旧分母追加样本。
- G1 仍 `BLOCKED`，G5 未完成，V4 仍是唯一生产架构；`ai-v1` 的加入不等于 N3/G-049/G1 关闭。

---

## 0A. 状态、字段与禁止事项（历史定义，保留）

### 0A.1 提取状态

| 状态 | 含义 | 是否计入对齐率 |
|---|---|---|
| `pending` | V4 测试/夹具已存在，输入输出可运行；尚未导出为语言中立 JSON | 否 |
| `pending-v4-replay` | 需要给 V4 增加只读录制点/固定 ID/记录实际随机结果或完整状态，当前公开输出不足 | 否 |
| `v5-only` | V4 没有对应行为，属于 V5 新能力（协议重写、持久化、租约、Unity 等） | 否（单列） |
| `exported` | 已写入 `tests/v5-golden/` 并可由命令重放 | 是；2026-10-23 原始盘点为 0，当前计数见 §0.2 当前事实与 §8.3.2 |

### 0A.2 单例 JSON 目标字段（未导出，仅冻结形状）

```json
{
  "sampleId": "G-xxx",
  "v4Commit": "48125ff",
  "rulesetVersion": 1,
  "source": {
    "file": "engine/src/main/kotlin/...",
    "line": "123-145",
    "testClass": "com.majiang....",
    "testMethod": "..."
  },
  "command": "gradlew ...",
  "input": {
    "fixture": "TestTableBuilder...",
    "seed": null,
    "clockMillis": 1000,
    "sessionId": "记录实际值",
    "roundId": "记录实际值",
    "decisionId": "记录实际值",
    "controlEpoch": 0,
    "requestId": "记录实际值",
    "tiles": "逐张 TileId",
    "config": {}
  },
  "expected": {
    "events": [{"type": "...", "ordinal": 0, "fields": {}}],
    "state": {
      "phase": "...",
      "version": 0,
      "tilePlacements": "136 项",
      "scores": [0, 0, 0, 0],
      "cheatPoints": [0, 0, 0, 0],
      "ledger": [],
      "hashStatus": "pending"
    },
    "projection": {
      "playerSeat0": "白名单字段摘要",
      "public": "白名单字段摘要",
      "forbidden": ["暗牌", "牌山顺序", "seed"]
    }
  },
  "status": "pending"
}
```

规则：

1. `seed`/`clockMillis`/`sessionId`/`roundId`/`decisionId`/`requestId` 必须记录**实际使用值**，不能只写“随机”。
2. 输入必须包含随机结果（或实际牌山/抽牌序列）、权威时间、ID、`controlEpoch`、配置版本；仅记录 seed 不足以证明跨运行时回放。
3. 预期事件必须有类型、同一 `commandVersion` 内的 `eventOrdinal`、关键字段；预期状态必须有 136 槽位置表、分数/供托/账本、生命周期/版本；预期投影必须按座位/公开流分开。
4. `hashStatus` 只有 `computed-from-v4-run` 才可填值；本次全部 `pending`。
5. 任何 `exported` 样本必须能由清单命令重新生成；生成器版本、V4 提交、命令、输入摘要、输出摘要同目录保存。

### 0A.3 命令模板

以下模板在 `D:\majiang` 执行；`CLASS` 为表中“来源/测试类”列的类名。所有命令均使用仓库 wrapper，不依赖全局 `gradle`。

- `C-E`：`.\gradlew.bat :engine:test --tests "CLASS" --console=plain --no-daemon`
- `C-P`：`.\gradlew.bat :protocol:test --tests "CLASS" --console=plain --no-daemon`
- `C-S`：`.\gradlew.bat :server:test --tests "CLASS" --console=plain --no-daemon`
- `C-C`：`.\gradlew.bat :client:test --tests "CLASS" --console=plain --no-daemon`

（历史口径，2026-10-23）本次会话当时实际运行 **470** 个 V4 定向用例（首轮 366 + 复核补跑 104；0 失败、0 跳过）；命令与分批结果见 `V5-0-TRACEABILITY.md` §1.3。另有审查侧独立全量 V4 回归 1,852/0/0。该运行只证明 V4 测试断言通过，不证明样本已导出，也不证明 V5 对齐。当前五组 fixture 已在后续任务导出；本节数字仍是 V4 证据，不是 V5 测试数。

---

## 1. 牌实体、位置与 136 守恒

| ID | 来源（文件:行 / 测试方法） | 命令 | 输入（时间/随机/夹具） | 预期事件/状态/投影（来源） | 提取状态 |
|---|---|---|---|---|---|
| G-001a | `engine/.../model/Tile.kt:49-52`；`RedFiveTest` 的 4 个方法 | C-E `com.majiang.model.RedFiveTest` | `TilePool.buildStandardSet()`；无随机、无时钟。 | 标准集 136 张；赤五恰为 Man5c3/Sou5c3/Pin5c3 共 3 张；Man/Pin/Sou 的 5 的 copy0..2 与所有非 5 数牌、字牌均 `isRedFive=false`。 | `pending` |
| G-001b | `engine/.../model/Tile.kt:62-68`；`RiichiRulesVerificationTest` 的 `34维索引正确性`、`136张牌物理守恒` | C-E `com.majiang.rules.RiichiRulesVerificationTest` | `Tile(...).typeIndex` 边界抽样 + `TilePool.buildStandardSet()`；无随机。 | 万 0..8 / 索 9..17 / 筒 18..26 / 风 27..30 / 三元 31..33 的边界索引；136 张按 `typeIndex` 分组为 34 组、每组 4 张。 | `pending` |
| G-001c | `model/Tile.kt:112-120`；`fromTileId`/`tileId` 当前无测试方法 | 需新增 V4 探针；当前无可用命令 | `fromTileId(0)`、`fromTileId(135)`、对 0..135 逐值 roundtrip；非 0..135 输入。 | `fromTileId(0)=Man1c0`、`fromTileId(135)=Dragon3c3`、`tileId` 往返一致；越界抛 `IllegalArgumentException`。**现有 RedFive/34 维测试未断言该映射，必须新增探针。** | `pending-v4-replay`（新增探针） |
| G-002 | `model/TileLocation.kt:14-22`；`TileLocation.init` 当前无直接测试 | 需新增 V4 探针；当前无可用命令 | 直接构造 `TileLocation(WALL, 0)`、`TileLocation(HAND, -1)`、`TileLocation(HAND, 0)`；无随机。 | 前两者 `IllegalArgumentException`；`TileLocation(HAND, 0)` 保留 `zone/player`；非法构造不产生状态（纯值对象）。**TileMovementContractTest 只覆盖合法位置移动，不覆盖这些非法构造。** | `pending-v4-replay`（新增探针） |
| G-003a | `model/TilePool.kt:197-205`；`ShuffleAlgorithmAcceptanceTest` 的 `generation conserves identities dead wall and is reproducible...` | C-E `com.majiang.v3.ShuffleAlgorithmAcceptanceTest` | `generate(seed=0..31, ...)` 的 136 张结果；无时钟。 | 每次结果 136 张、等于 `TilePool.buildStandardSet()` 集合、恰有 3 张赤五；王牌段 122..135 与空计划一致。 | `pending` |
| G-003b | `model/TilePool.kt:28`（private `tileMap`）、`:48-51`（`setLocation`）、`:128-145`（`assertStandaloneConservation`）；`state/StateInvariants.kt:306-316`（池账本逐张比对） | 待新增 V4 测试 `com.majiang.model.TilePoolProbeTest`（test-only reflection）；当前无可用命令 | **Probe A（缺失）**：`TilePool(gameState=null)`；测试专用 Java 反射取得 private `tileMap`，`remove(Man1c0)`；调用 `assertConservation()`。**Probe B（不一致）**：`TestTableBuilder.create()` 得到合法 state；取一张 `players[0].closedTiles` 的实体牌，用公开 `setLocation(tile, TileLocation.WALL)` 篡改池账本；调用 `state.tilePool.assertConservation()`。无随机。 | A：`IllegalArgumentException` 且消息含 `exactly 136`/`has 135`；失败后 map 仍 135、未被自动修复。B：绑定 gameState 走 `StateInvariants`，抛 `IllegalStateException` 且消息含 `TilePool audit failed`；审计不修改实际容器。**duplicate-key 分支无法构造**：`MutableMap<Tile,TileLocation>` 对相等 `Tile` 键只能覆盖，不能并存；`assertStandaloneConservation` 的 duplicate 检查在此结构下不可直接触达（静态限制）。重复物理墙由 G-010 的可运行用例覆盖，重复 Pon 由 G-007 覆盖。 | `pending-v4-replay`（新增探针） |
| G-004 | `model/TileMovement.kt`；`TileMovementContractTest` 方法 `normal draw and discard preserve exact identities and discard history` | C-E `com.majiang.model.TileMovementContractTest` | `TestTableBuilder.create(phase=DRAW)`；`top=wall[wallPointer]`；seat0；无随机、无时钟（fixture `turnNumber=12`）。 | `draw(0)` 返回 `top` 且进入手牌；`discard(0, top)` 后 `discardHistory=[top]`；池位置 HAND/DISCARD 精确；136 守恒。（测试 `:14-25`） | `pending` |
| G-005 | 同上，方法 `pon transfers all three tiles and retains original discard history`、`chi transfers both hand tiles and called tile` | C-E `com.majiang.model.TileMovementContractTest` | Pon：Pin5 copy0/1 在手，copy3 在 seat2 牌河；Chi：Man2/3 copy2 在手，Man4 copy2 在 seat3 牌河；无随机。 | Pon 后 seat2 牌河空、`discardHistory=[copy3]`；三张进入 MELD；Chi 后 3 张进入 MELD；136 守恒；源牌河清空。（测试 `:27-50`） | `pending` |
| G-006 | 同上，方法 `closed kan preserves an unrelated drawn tile and replenishes dead wall`、`added kan consumes the just drawn fourth copy exactly once`、`open kan removes discard and transfers all four tiles` | C-E `com.majiang.model.TileMovementContractTest` | 暗杠 Sou8 copy0..3 + 无关摸牌 Man9c3；加杠 Pin6 碰牌+copy3；明杠 Sou7 三张+seat2 弃 copy3；无随机。 | 暗杠后无关摸牌保留、`drawnTile=null`、岭上后 `deadWall.size - rinshanPointer + replacements=14`、原宝牌指示不变；加杠消耗第四张一次；明杠移走弃牌、4 张入 MELD；136 守恒。（测试 `:52-99`） | `pending` |
| G-007 | 同上，方法 `duplicate physical copies in a pon fail before mutation`、`fifth rinshan draw cannot consume a dora indicator` | C-E `com.majiang.model.TileMovementContractTest` | Pon：Man5c0 在手，弃 Man5c3，传 `[a,a]`；第五次岭上：先做 4 次 drawRinshan+discard。 | 两例均 `IllegalArgumentException`；`actualTiles` 前后完全相等；136 守恒。（测试 `:112-135`） | `pending` |
| G-008 | 同上，方法 `wall swap preserves ownership and exact physical set` | C-E `com.majiang.model.TileMovementContractTest` | 手牌 Man9c3；`wallTile=wall[wallPointer]`；无随机。 | `wallTile` 入手牌、`hand` 回到 `wall[wallPointer]`；所有权/物理集合不变；136 守恒。（测试 `:101-110`） | `pending` |
| G-009 | `state/StateInvariants.kt:239-328`；`TileMovementContractTest` 方法 `initial state audit does not overlook a hidden replacement tile` | C-E `com.majiang.model.TileMovementContractTest` | `GameState()`；`deadWallReplacements.add(Man1)`；初始全 UNASSIGNED。 | 审计必须看见隐藏 replacement；合法空开局放行；任何重复/缺失/14 王牌不满足均抛；136 唯一。 | `pending-v4-replay`（需录完整 136 位置表） |
| G-010 | `application/ApplicationCoordinator.kt:2883-2943`；`ShuffleAtomicDealAcceptanceTest:11-24` | C-E `com.majiang.v3.ShuffleAtomicDealAcceptanceTest` | `GameState()` + `cheatPoints=50`；`broken=good` 但 `broken[135]=broken[0]`（**重复物理墙**）；`cheatCosts={0:40}`；无随机、无时钟。 | `Rejected`；`round.version` 不变；`cheatPoints=[50,50,50,50]`；`cheatEntries` 空；`wall` 空；四家 hand 空。**这是可运行的“重复物理墙拒绝”证据**（区别于 G-003b 的 Map duplicate-key 静态限制）。 | `pending` |
| G-011 | `ApplicationCoordinator.kt:2945-2992`；`ShuffleAtomicDealAcceptanceTest:25-32` | C-E `com.majiang.v3.ShuffleAtomicDealAcceptanceTest` | 同上但用 `good=buildStandardSet()`；`cheatCosts={0:40}`；`cheatPoints=50`。 | 状态断言：`Accepted`；`cheatPoints[0]=10`；单条 `cheatEntries.amount=-40`；`frozenStartingCheatPoints[0]=50`；之后再发牌 `Rejected` 且余额仍 10；136 守恒。事件序列（源码构造，测试未断言）：`CheatPointsChanged` → `StackingStarted` → `DoraRevealed(deadWall[4], isUra=false)` → `WallBuilt(wallSize=122)` → 53×`TileDealt` → `DealingComplete`；`phase=CHEAT_WINDOW`、`currentPlayer=dealer`。 | `pending-v4-replay`（状态测试覆盖；事件序列/顺序需 V4 录制） |
| G-012 | `state/StateInvariants.kt:292-328`；`ShuffleAtomicDealAcceptanceTest:32`；`TestTableBuilder` 仅作导出辅助 | C-E `com.majiang.v3.ShuffleAtomicDealAcceptanceTest` | 同 G-011 的 `good` 墙；dealer=0。 | 发牌后 136 张实际容器 = 标准 136 集合；`wall` 列表 122、`wallPointer=53`、剩余活牌山 69、王牌 14、`rinshanPointer=0`；四家 13/14 张；无重复；`StateInvariants.assertValid` 通过。（测试 `:32`） | `pending-v4-replay`（需完整 136 位置表/摘要） |

---

## 2. 和牌、役、符、算分、结算

| ID | 来源（文件:行 / 测试方法） | 命令 | 输入（时间/随机/夹具） | 预期事件/状态/投影（来源） | 提取状态 |
|---|---|---|---|---|---|
| G-013 | `rules/WinningScoreContractTest:17-30`；`rules/WinningScoreEvaluator` | C-E `com.majiang.rules.WinningScoreContractTest` | 手牌：Man112233 + Pin445566 + Sou77，和牌 Pin6c1；荣和、门清、seatWind=2、roundWind=1；无随机。 | 分解为 REGULAR（非七对）；`totalHan=4`、`fu=30`、`totalPoints=7700`；手牌/摸牌不被修改。（测试 `:17-30`） | `pending` |
| G-014 | `rules/FuBreakdownContractTest:46-69`；`rules/FuCalculator` | C-E `com.majiang.rules.FuBreakdownContractTest` | 手牌：Man123 + Pin456 + Sou789 + Man888 + Pin99；和牌 Pin9c1；门清自摸；seatWind=1、roundWind=2。 | 小计=28；行项和=28；切上=30；`finalFu=30`；无 specialNote。（测试 `:46-69`） | `pending` |
| G-015 | `rules/WinningScoreContractTest:51-83`；`rules/WinningScoreEvaluator` | C-E `com.majiang.rules.WinningScoreContractTest` | 手牌 Man5c3+Man5c0；指示牌 Man4c0；无随机。 | `breakdownDora`：indicator=2、red=1、total=3；`countDora=3`；单张红五 + 指示牌时 indicator=1、red=1、total=2。（测试 `:51-83`） | `pending` |
| G-016 | `rules/WinningScoreContractTest:42-49`；`rules/WinningScoreEvaluator` | C-E `com.majiang.rules.WinningScoreContractTest` | 副露 Chi Man123；暗牌 Man456 + Pin78955 + Sou123；和牌 Pin5c1；非门清；`extraHan=10`；无随机。 | 无役（dora 不是起和役）→ `evaluate` 返回 `null`；不得因宝牌/赤五和牌。（测试 `:42-49`） | `pending` |
| G-017 | `rules/ScoreCalculator.kt:129-205`；`RiichiRulesVerificationTest` 的 5/6/7/8/10/11/13 翻用例；`ScoreCodesAndPaymentPlanTest:89-102` 的 capCode | C-E `com.majiang.rules.RiichiRulesVerificationTest`；C-E `com.majiang.rules.ScoreCodesAndPaymentPlanTest` | `calculate(fu,han)`：3 翻 70 符、4 翻 40 符、5 翻、6/7 翻、8/10 翻、11/12 翻、13+ 翻；无随机。 | base=2000/2000/2000/3000/3000/4000/4000/6000/6000/8000；非庄/庄荣和支付按表；capCode=MANGAN/HANEMAN/BAIMAN/SANBAIMAN/KAZOE_YAKUMAN；无切上满贯。`rawBasePointsExact` 未在该测试中断言，不写入预期。 | `pending` |
| G-018 | `rules/YakuEvaluator.kt:15-68`；`rules/RiichiRulesVerificationTest` 的 `役满`/`双倍役满` | C-E `com.majiang.rules.RiichiRulesVerificationTest` | `yakumanCount=1/2`；无随机。 | base=8000/16000；闲家荣和 32000/64000、庄家荣和 48000/96000；闲家自摸庄家 16000/其他 8000；庄家自摸 16000。`isYakuman`/文案未在该测试断言，不写入预期。 | `pending` |
| G-019 | `rules/YakuEvaluator.kt:15-68`；`rules/ScoreCodesAndPaymentPlanTest:52-73` | C-E `com.majiang.rules.ScoreCodesAndPaymentPlanTest` | `Yaku.entries` 全量；无随机。 | 测试断言：所有 `Yaku` 的 stable `code` 唯一、非空、旧 `displayName` 不变；新增役种必须登记稳定码。**“42 个”这一字面数量与文档 41 的差异未由该测试直接断言，需 V4 计数探针 + P0.2/G0 裁决（D-01）。** | `pending-v4-replay`（计数探针 + `D/待裁决`） |
| G-020 | `settlement/SettlementService.kt:55-86`；`SettlementServiceContractTest:23-31` | C-E `com.majiang.settlement.SettlementServiceContractTest` | `scores=[25000,25000,25000,25000]`、dealer=0、honba=2、sticks=3；30 符 3 翻；荣和。 | 闲家荣和 winner=1/loser=2：deltas `[0,7500,-4500,0]`、sticks=0；庄家荣和 winner=0/loser=2：`[9400,0,-6400,0]`、sticks=0；总资金守恒。（测试 `:23-31`） | `pending` |
| G-021 | 同上 `SettlementServiceContractTest:33-47,83-87` | C-E `com.majiang.settlement.SettlementServiceContractTest` | 同上；自摸；非闲家/庄家；dealer 0 或 3。 | 闲家自摸 winner=1：`[-2200,7600,-1200,-1200]`；庄家自摸 winner=0：`[9600,-2200,-2200,-2200]`；dealer=3 时 winner=1 的付款庄家为 2200、闲家 1200；sticks=0。 | `pending` |
| G-022 | `SettlementService.kt:142-183`；`SettlementServiceContractTest:50-64` | C-E `com.majiang.settlement.SettlementServiceContractTest` | `funded`（25000×4、dealer0、honba2、sticks3）；tenpai mask 0..15。 | 0/4 听无收支；1 听 +3000 / 三家 -1000；2 听 +1500 / 两家 -1500；3 听 +1000 / 一家 -3000；`remainingRiichiSticks=3`；16 例均守恒。 | `pending` |
| G-023 | `SettlementService.kt:185-200`；测试 `:66-69` | C-E `com.majiang.settlement.SettlementServiceContractTest` | 同上；途中流局。 | deltas `[0,0,0,0]`；newScores 原样；sticks=3；守恒。（测试 `:66-69`） | `pending` |
| G-024 | `SessionBoundaryAcceptanceTest` 的 `riichi deposits transfer exactly once across round and session ledgers` | C-E `com.majiang.architecture.SessionBoundaryAcceptanceTest` | 首局 seat0 已立直、scores[0]=24000、`riichiSticks=1`；和牌后 `nextRound()`；无随机。 | `result.startingScores[0]=24000`、`startingRiichiSticks=1`；`finalScores.sum()+remainingRiichiSticks*1000=100000`；`remainingRiichiSticks=0`；next 局 scores 与 result.finalScores 相同、`riichiSticks=0`。**“本局 -1000/+1000 deltas”未由该测试直接断言，不写入预期。** | `pending` |

---

## 3. 单局、整场、CHANKAN、事件原子性

| ID | 来源（文件:行 / 测试方法） | 命令 | 输入（时间/随机/夹具） | 预期事件/状态/投影（来源） | 提取状态 |
|---|---|---|---|---|---|
| G-025 | `match/MatchProgression.kt:223-300`；`MatchProgressionContractTest:18-42` | C-E `com.majiang.match.MatchProgressionContractTest` | 初始 dealer=0、wind=1、round=0、honba=2、scores=25000×4；outcome：庄家自摸、闲家自摸、荒牌（庄听/庄未听）、途中流局。 | 庄家和：同 wind/round/dealer、honba=3；闲家和：wind=1/round=1/dealer=1/honba=0；荒牌庄听：round0/dealer0/honba3；荒牌庄未听：round1/dealer1/honba3；途中流局：round0/dealer0/honba3。（测试 `:18-42`） | `pending` |
| G-026 | `MatchProgression.kt:233-300`；测试 `:44-74,104-108` | C-E `com.majiang.match.MatchProgressionContractTest` | 东四换庄、南四达返点、南四未达返点、西入、西四、东风战不延长。 | 东四→南一（wind2/round0/dealer0）；南四有人≥30000→MatchFinished；无人→西一（wind3/round0/dealer1）；西入有人≥30000→MatchFinished；西四→MatchFinished（不产生北风）；东风战无延长→东四后 MatchFinished。（测试 `:44-74,104-108`） | `pending` |
| G-027 | `MatchProgression.kt:247-267`；测试 `:76-102` | C-E `com.majiang.match.MatchProgressionContractTest` | 尾庄、scores 35000/25000/20000/20000 或 30000 tie；`dealerDeclaredAgariYame`；`remainingRiichiSticks=2`。 | 尾庄独占第一→`canDealerDeclareAgariYame=true`，宣告→MatchFinished(AGARI_YAME)；同分→false；终局供托 2 根归同分第一 seat0：final `[32000,30000,20000,20000]`、rank `[0,1,2,3]`、sum=102000。（测试 `:83-102`） | `pending` |
| G-028a | `round/RoundStateMachine.kt:152-201`；`RoundBoundaryAcceptanceTest:62-79` | C-E `com.majiang.architecture.RoundBoundaryAcceptanceTest` | `GameState()` + `RoundCommandProcessor(random=Random(41))`；和牌手牌 Man/Pin/Sou 234 + Sou678 + Man5c3，drawn Man5c0。 | prepare 不改 state/version；install 后 version=1、14 张手牌；终局 `phase=ROUND_END`、`result.events` 含 `TsumoWin`、不含 `MatchEnded`；此后 StartStacking/AgariYame/Tsumo 全部 Rejected 且指纹不变。**该用例只证明单局终态不可复活，不证明 nextRound 会创建新 roundId。** | `pending` |
| G-028b | `SessionBoundaryAcceptanceTest:29-50` 的 `dealer repeat creates a new machine and leaves the old round immutable`；`CheatLedgerIntegrationAcceptanceTest:21-42` | C-E `com.majiang.architecture.SessionBoundaryAcceptanceTest`；C-E `com.majiang.v3.CheatLedgerIntegrationAcceptanceTest` | 首局和牌/结算后调用 `nextRound()`；dealer repeat；记录实际旧/新 `roundId`、`sessionId`、`version`；无随机（UUID 需记录实际值）。 | `assertNotSame(old,next)`；`assertNotEquals(old.roundId,next.roundId)`；dealer=0、honba=1、`next` 分数=`result.finalScores`；旧 `old` 的 view/result/resultId/finalScores/events 不变；`CheatLedger` 用例额外断言新局 `roundId` 不同于旧局且 cheatPoints 传递。**不断言新局 version 的具体值，也不伪造 sessionId。** | `pending`（UUID 实际值需录制） |
| G-029 | `round/RoundTransitionPlanner.kt:254-259,293-350`；`UnifiedActionAcceptanceTest:220-250`；`SleeveBloomKanDoraTest` | C-E `com.majiang.state.UnifiedActionAcceptanceTest` | seat0 Pon Man5c0..2，drawn Man5c3；seat1 听 Man46 + Pin123789 + Sou12366；AI 宣告 ADDED_KAN；无随机。 | 先进入 `CHANKAN_WINDOW`；副露仍 PON、`rinshanPointer=0`、`totalKanCount=0`；seat1 合法 `DeclareRon`；荣和后 `RonWin`、无 `KanDeclared`/`RinshanDraw`；136 守恒。（测试 `:220-250`） | `pending` |
| G-030 | `RoundTransitionPlanner.kt:232-249`；`SleeveBloomKanDoraTest:286-307` | C-E `com.majiang.cheat.bloom.SleeveBloomKanDoraTest` | seat0 已 Pon Man9×4+暗刻 Man2×4；drawn Man9c3；deadWallPrefix 指定；`doraIndicators` 初始 1 张；连续加杠/暗杠。 | 连续两次杠：`doraIndicators` 变为 `[deadWall[4],deadWall[6],deadWall[8]]`；新事件含 **2 个 `DoraRevealed`**，最后一个 `DoraRevealed` 在 `RinshanDraw` 之前；后续弃牌不再重复 `DoraRevealed`；136 守恒。（测试 `:286-307`） | `pending`（多事件样本） |
| G-031 | `v3/CheatLedgerIntegrationAcceptanceTest:44-59`；`application/CheatLedgerCharge.kt` | C-E `com.majiang.v3.CheatLedgerIntegrationAcceptanceTest` | 初始 cheatPoints `[10,20,30,40]`；无效操作：overdraw -11、overflow Int.MAX、seat4；无随机。 | 每个操作 `ActionResult.Rejected`；`round.version` 不变；cheatPoints/scores 不变；136 守恒；订阅事件列表为空（**无事件拒绝**）。（测试 `:44-59`） | `pending` |

---

## 4. 三绝技与出千账本

| ID | 来源（文件:行 / 测试方法） | 命令 | 输入（时间/随机/夹具） | 预期事件/状态/投影（来源） | 提取状态 |
|---|---|---|---|---|---|
| G-032 | `authority/cheat/TileMarkSubmissionHandler.kt:97-112`；`TileMarkAuthorityContractTest:64-89` | C-E `com.majiang.cheat.mark.TileMarkAuthorityContractTest` | `TestTableBuilder.create()`；`cheatPoints=10`；TileMark cost=10；ManualClock=1000；4 张本人手牌；`requestId="mark"`；无随机。 | `APPLIED`；cheatPoints[0]=0；`cheatEntries` 一条 -10；`activeGroups.single().tiles` = 请求 4 张；所有牌的 `TileLocation` 前后相同；scores/currentPlayer/deadline 不变；decisionId 刷新；随后可正常弃牌；136 守恒。（测试 `:64-89`） | `pending` |
| G-033 | `TileMarkSubmissionHandler.kt:104-109`；`TileMarkAuthorityContractTest:91-107`、`TileMarkStateContractTest` 的过期/容量用例 | C-E `com.majiang.cheat.mark.TileMarkAuthorityContractTest`；过期/容量子例另跑 `com.majiang.cheat.mark.TileMarkStateContractTest` | 40 点；3 组各 4 张；第 4 组；另例 `createdRoundOrdinal+3` 跨 4 局；无随机。 | 3 组共 12 张、花费 30、余额 10；第 4 组 `REJECTED`、余额仍 10、状态对象不变、`canApply=false`；组在 `createdRoundOrdinal+3` 后过期；晚到 replay 不复活；看破清标只保留指定席位。（测试 `:91-107` + `TileMarkStateContractTest`） | `pending` |
| G-034 | `TileMarkSubmissionHandler.kt:60-113`；`TileMarkAuthorityContractTest:109-140` | C-E `com.majiang.cheat.mark.TileMarkAuthorityContractTest` | 40 点；非法选择 3/5/重复/-1/他家；余额 9；deadline 恰好；立直；他家回合；无随机。 | 全部 `REJECTED`；reason 对应 `TILE_MARK_INVALID_SELECTION`/`TILE_MARK_TILE_NOT_OWNED`/`TILE_MARK_ALREADY_MARKED`/`INSUFFICIENT_FUNDS`/`DEADLINE_EXPIRED`/`TILE_MARK_RIICHI_LOCKED`/`NOT_YOUR_TURN`；余额/组/version 不变；无部分扣费。（测试 `:109-140`） | `pending` |
| G-035 | `authority/cheat/SleeveBloomSubmissionHandler.kt:248-283,286-350`；`SleeveBloomContractTest:27-145` | C-E `com.majiang.cheat.bloom.SleeveBloomContractTest` | `TestTableBuilder.create(drawn=0 to Sou9c3, wallPrefix=[Pin9c3], phase=CHEAT_WINDOW)`；points=10；ManualClock=1000；`SLEEVE_BLOOM` cost=10；无随机。 | 激活 `APPLIED`；余额 0；offer.first=drawn Sou9c3、offer.second=wall[wallPointer] Pin9c3；所有牌位置不变；仅 owner snapshot 有 offer；他席无；timing key 更新但 deadline/reserve 不变；choice SECOND 交换两张实体牌、墙指针不前进、弃牌候选刷新；FIRST 不扣费且位置不变；136 守恒。（测试 `:76-158`） | `pending` |
| G-036 | `SleeveBloomSubmissionHandler.kt:352-407`；`SleeveBloomOffTurnTest` 的 `off-turn activation charges once reveals only owner and replays idempotently` 等 | C-E `com.majiang.cheat.bloom.SleeveBloomOffTurnTest` | `baseState`：seat0 已弃 Man1c0、phase=DRAW、currentPlayer=1、points=30、`openSleeveBloomWindow(0)`；ManualClock=1000；offTurnChoiceMillis=10000；无随机。 | 激活 `APPLIED`、扣 10、仅 owner 看到 `windowId`；二选一截止=签发+10000；暂停冻结、恢复保留剩余；超时 FIRST 保留原牌并解锁第二张；reservation 在下一次本人普通摸牌才交付；岭上替补不偷活牌山尾部；每座最多一个 offer/reservation；136 守恒。（测试方法名） | `pending` |
| G-037 | `SleeveBloomSubmissionHandler.kt:352-407`；`SleeveBloomOffTurnTest` 的 `wrong window other seat and duplicate reservation requests are rejected without charge`、`stale candidate...` | C-E `com.majiang.cheat.bloom.SleeveBloomOffTurnTest` | 错误 windowId、他家 seat、重复 reservation、过期 offer、非成员候选；无随机。 | 全部拒绝且不扣费、不建 offer/reservation、不改状态；stale offer 清空但不移动/不退款/不赠新预算；reason `SLEEVE_BLOOM_NO_WINDOW`/`SLEEVE_BLOOM_NO_PENDING_OFFER`/`SLEEVE_BLOOM_STALE_OFFER` 等。（测试方法名） | `pending` |
| G-038 | `authority/cheat/BeamSwapSubmissionHandler.kt:165-211,237-302`；`BeamSwapContractTest:28-181` | C-E `com.majiang.cheat.beam.BeamSwapContractTest` | 同 G-035 夹具（drawn Sou9c3、墙首 Pin9c3、CHEAT_WINDOW）；points=10；ManualClock=1000；`BEAM_SWAP` cost=10；无随机。 | 激活 `APPLIED`；余额 0；offer 记录 drawnTileId/eligibleHandTileIds/candidates；激活不移动牌、不延长 deadline；SWAP 用首个可选手牌+首个候选槽，刷新弃牌候选、保留 drawn slot/clocks；KEEP_ORIGINAL 花费机会、不退款、不移动；136 守恒。 | `pending` |
| G-039 | `BeamSwapSubmissionHandler.kt:237-302`；`BeamSwapContractTest:242-340` | C-E `com.majiang.cheat.beam.BeamSwapContractTest` | 错误 offerId、非成员手牌、外部槽、候选漂移（牌山/王牌/摸牌来源变化）、阶段变化；无随机。 | 拒绝/清 stale offer；不移动牌、不弃牌、不退款、不赠新预算；reason `BEAM_SWAP_NO_PENDING_OFFER`/`BEAM_SWAP_OFFER_MISMATCH`/`BEAM_SWAP_INVALID_CHOICE`/`BEAM_SWAP_STALE_OFFER`；有效 offer 不被外部失败破坏。（测试 `:242-340`） | `pending` |
| G-040a | `TileMarkAuthorityContractTest:64-89,182-190,345-373`；`state/StateInvariants.kt:292-328` | C-E `com.majiang.cheat.mark.TileMarkAuthorityContractTest` | `TestTableBuilder.create()`；points=10/40；4 张本人手牌；owner/其他 seat/公开投影；无随机。 | 成功后 `TilePool.buildStandardSet()` 逐张 `TileLocation` 前后相同；`TestTableBuilder.assertPhysical` 通过；owner `activeGroups.size=1`；其他 seat `activeGroups`/`selectableTileIds` 空且 `canApply=false`；`queryPublicProjection()` 的字段不含 `tileMark`；`CheatPointsChanged` 订阅回调看到已提交的 group；deep copy 集合不可变（`UnsupportedOperationException`）。 | `pending` |
| G-040b | `SleeveBloomContractTest:76-158`；`state/StateInvariants.kt:292-328` | C-E `com.majiang.cheat.bloom.SleeveBloomContractTest` | `drawn=0 to Sou9c3`、`wallPrefix=[Pin9c3]`、phase=CHEAT_WINDOW；points=10；owner/其他 seat；无随机。 | 激活后 `locations(before)==locations(after)`、`wall`/turn/scores 不变；`CheatPointsChanged` 订阅回调看到 `(0, pendingOffer!=null)` 同次发布；owner 有 `pendingOffer`，其他 seat `null`；SECOND 后 `drawnTile`/`wall[wallPointer]` 精确互换、墙指针不前进、旧 action 失效；FIRST 后所有牌位置不变；`physical(after)` 通过。 | `pending` |
| G-040c | `BeamSwapContractTest:78-150,457-477`；`state/StateInvariants.kt:292-328` | C-E `com.majiang.cheat.beam.BeamSwapContractTest` | 同 G-035 夹具；points=10；owner/其他 seat；私有 snapshot 篡改 foreign hand/decision；无随机。 | 激活后 `locations(before)==locations(after)`、`wall`/turn/scores 不变；`CheatPointsChanged` 回调看到 `(0, pendingOffer!=null)` 同次发布；owner 有 `pendingOffer`，其他 seat `null`；offer 的 `candidates` 不含 marked/具体 tile；私有 `copy` 拒绝他家手牌/foreign decision；`candidates`/`selectableHandTileIds` 不可变；`physical(after)` 通过。 | `pending` |
| G-041 | `economy/CheatLedger.kt:70-124`；`CheatLedgerIntegrationAcceptanceTest:21-70` | C-E `com.majiang.v3.CheatLedgerIntegrationAcceptanceTest` | 初始 `[10,20,30,40]`；`skill-1` seat0 amount -7；重复同载荷；同 id +6；overdraw -11；overflow Int.MAX；seat4；无随机。 | 首扣后 `[3,20,30,40]`；重复同载荷返回原 entry、version 不变；不同载荷/越界/溢出/负余额全部拒绝且零副作用；麻将 scores 不变；136 守恒；终局 summary 的 gain/spend/net 独立于点棒。（测试 `:21-70`） | `pending` |

---

## 5. 码牌、看破、计时、控制、AI

| ID | 来源（文件:行 / 测试方法） | 命令 | 输入（时间/随机/夹具） | 预期事件/状态/投影（来源） | 提取状态 |
|---|---|---|---|---|---|
| G-042 | `stacking/ShuffleSlots.kt:14-79`；`ShuffleAlgorithmAcceptanceTest:32-49` | C-E `com.majiang.v3.ShuffleAlgorithmAcceptanceTest` | dealer=0..3；seat=0..3；`initial/dealer`、`draws(18)`；无随机。 | 起手槽 union 恰为 0..52（53 张）；庄 14 槽、子 13 槽；庄最后槽=52，子最后槽=48+相对座次；摸牌槽从 53+offset 起、步长 4、止于 ≤121；绝不含 122..135。（测试 `:32-49`） | `pending` |
| G-043 | `ShuffleWallGenerator.kt:40-207`；`ShuffleAlgorithmAcceptanceTest:75-160` | C-E `com.majiang.v3.ShuffleAlgorithmAcceptanceTest` | 计划 seat0 KOKUSHI、seat1 DAISANGEN、seat2 CHINITSU_MAN；seed=0..31；dealer=seed%4；四施术者 seed=0..127；空计划 seed=77；无时钟。 | 136 唯一、标准集、3 赤五；同 seed/逆序 plans 结果相同；王牌段 `122..135` 与空计划相同；四人有效时逐张等于基础洗牌；无目标/关闭/strength=0 时逐张等于基础洗牌；三人仍改变牌山；非法 seat/配置被拒绝。 | `pending` |
| G-044 | `stacking/ShuffleSelection.kt`、`authority/cheat/ShufflePreparationHandler.kt`；`SeeThroughEngineAcceptanceTest` 的 12 个方法 | C-E `com.majiang.seethrough.SeeThroughEngineAcceptanceTest` | 方案 cost 0/10/30/40；看破席位 0/1/多人；deadline 冻结；无随机。 | 看破单独 10；与统一目标组合上限 40；看破者目标被抵消、对手目标被抑制；多人看破对称；无人被抵消时牌山不变；发牌时保留本人标记、清对手标记；失败墙/费用/资格零副作用。（测试方法名） | `pending` |
| G-045 | `authority/cheat/ShufflePreparationHandler.kt:54,64-69`；`ShuffleWallGenerator.kt:62-65` | 无独立测试可导出（源码核对） | `start(roundId, now, config)` 内部 `SecureRandom.nextLong()`；无 getter/事件；对比 `generate(seed,...)` 可直接给 seed。 | 当前公开输出只能看到 `roundSeed` 的效果（实际墙），不能看到 seed；要复现整局必须记录实际 136 墙/抽牌序列。不能用“记录 seed”声称 V4 准备阶段可回放。 | `pending-v4-replay`（D-07） |
| G-046 | `timing/DecisionClock.kt:105-153,182-208`；`TimeControlConfig.kt:14-36`；`TimeControlAcceptanceTest` 的 10 个方法 | C-E `com.majiang.v3.TimeControlAcceptanceTest` | `baseMillis=3000`、`reserveMillis=20000`、`ReserveScope.ROUND`/`SESSION`；ManualClock=1000；open/complete/cancel/pause/resume。 | 首决策 deadline=open+23000；重复 open 不刷新；cancel 扣已用储备；ROUND 新局重置、SESSION 不重置；pause/resume 不赠新 3s；旧局 key 不能开在新局；恰好 deadline 拒绝。（测试方法名） | `pending` |
| G-047 | `timing/TimeoutPolicy.kt:12-66`；`ServerTableHost.kt:1630-1705`；`TimeControlAcceptanceTest` 的 `response timeout passes ron and own turn timeout only picks a legal discard` | C-E `com.majiang.v3.TimeControlAcceptanceTest` | 响应窗口含 Ron/Pass；自己回合含摸牌/多种弃牌；无随机。 | 有 Pass 必选 Pass；否则选当前摸牌弃牌；否则最小自然序合法弃牌；绝不自动和/杠/立直/千术；`tick` 中超时在 `presentationBusy` 之前裁决；AI 结果在其后处理。 | `pending` |
| G-048 | `server/.../control/ConnectionControl.kt:145-231,282-339`；`ConnectionControlTest:13-166` | C-S `com.majiang.server.control.ConnectionControlTest` | ManualClock=1000/2000；`IMMEDIATE_AI`、`GRACE_THEN_AI` grace=5000、`TIMEOUT_ONLY`；disconnect/reconnect/reclaim。 | Immediate：断线即 TAKEOVER、epoch=1、grace=null；Grace：2000 断线→deadline=7000，6999 仍 HUMAN/epoch0，7000 接管 epoch1；重复断线不延长；reconnect(false) 保留 TAKEOVER/epoch1，`reclaimControl()`→HUMAN/epoch2；TIMEOUT_ONLY 永不接管；旧 expectedEpoch 无副作用。（测试 `:13-166`） | `pending` |
| G-049 | `authority/TableAuthority.kt:708-751`；`server/.../host/ServerTableHost.kt:1857-1900`；`UnifiedActionAcceptanceTest` 的 AI 视图用例 | C-E `com.majiang.state.UnifiedActionAcceptanceTest` | AI 只拿 `PlayerView`+合法动作；旧 round/decision/controlEpoch/deadline/候选；无随机（测试用确定性策略）。 | 任一 stamp 不匹配→`STALE_AI_SUBMISSION`、无状态变化；AI 不能读完整状态/他家暗牌；AI 只能提交本席位合法动作；浮点风险只用于排序，不进权威哈希。 | `pending-v4-replay`（需记录 `AiDecisionRecorded`/intent） |

---

## 6. 回执、协议、客户端同步、投影

| ID | 来源（文件:行 / 测试方法） | 命令 | 输入（时间/随机/夹具） | 预期事件/状态/投影（来源） | 提取状态 |
|---|---|---|---|---|---|
| G-050a | `protocol/.../TcpWire.kt:9-45,83-92`；`TcpWireTest` | C-P `com.majiang.transport.tcp.TcpWireTest` | `TcpWire`/`TcpCloseReasons` 常量；`ApiErrors.ALL`；无随机。 | 12 字节头；`WIRE_VERSION=1`、`BUSINESS_VERSION=8`、`PROTOCOL_VERSION=8`；payload 1..1,048,576；帧类型 1..10；关闭原因永久/临时集合；`REQUEST_SCOPE_EXPIRED` 在 `ApiErrors.ALL`。 | `pending`（V4 值样本） |
| G-050b | `protocol/.../TcpFrame.kt`；`TcpFrameCodecTest` | C-P `com.majiang.transport.tcp.TcpFrameCodecTest` | 1 字节逐片喂入、混合半帧、20 字节慢到达、坏魔数/版本/flags/type、长度 0/1048577/0xFFFFFFFF、坏 UTF-8/JSON、部分 EOF；无随机。 | 合法帧跨片重组；坏魔数/版本/flags/type/长度/UTF-8/JSON 进入致命 `PROTOCOL_VIOLATION`/`VERSION_MISMATCH`；部分 EOF 抛致命异常；解码器不因只收到头就预分配 1 MiB。 | `pending`（V4 值样本） |
| G-050c | `protocol/.../TcpMessages.kt`；`TcpMessagesTest` | C-P `com.majiang.transport.tcp.TcpMessagesTest` | HELLO/WELCOME/REQ/RSP/PING/PONG/PLAYER_UPDATE/PUBLIC_UPDATE/CLOSE 编解码；未知字段、缺字段、错帧型、错 requestId、16 hex nonce；无随机。 | 合法消息 roundtrip；PING/PONG nonce 原样回显；未知字段/缺字段/错帧型/错 requestId 严格拒绝（`PROTOCOL_VIOLATION`）；`grantedScope` 可选。 | `pending`（V4 值样本） |
| G-051 | `protocol/.../server/api/ApiErrors.kt:13-85`；`protocol/.../TcpCloseReasons.kt:17-94`；`TcpWireTest` | C-P `com.majiang.transport.tcp.TcpWireTest` | `ApiErrors.ALL`、`TcpCloseReasons.ALL`；无随机。 | `ApiErrors.ALL` 17 项且含 `CONFLICT`/`SERVER_BUSY`/`REQUEST_SCOPE_EXPIRED`；关闭原因 `ALL=10`、永久 6、临时 4，互斥且并集为全集；`SERVER_SHUTDOWN`/`ROOM_CLOSED`/`DISPLACED...`/`LOCAL_CLOSED`/`HEARTBEAT_TIMEOUT`/`VERSION_MISMATCH`/`PROTOCOL_VIOLATION`/`HANDSHAKE_TIMEOUT`/`SERVER_OVERLOADED`/`SLOW_CONSUMER` 全在集合。业务拒绝走 receipt 的分离由 G-052 覆盖，不在本行。 | `pending` |
| G-052 | `access/ReceiptStatus.kt`；`authority/TableAuthority.kt:181-299,541-646`；`Mr09ReceiptQuotaTest` | C-E `com.majiang.authority.Mr09ReceiptQuotaTest` | 默认 `maxReceiptsPerSeat=8192`；本测试覆写为 `10`；同 requestId 同载荷/不同载荷；配额满；无随机。 | 同载荷重放原回执；不同载荷 `REQUEST_ID_CONFLICT`；超配额 `RECEIPT_QUOTA_EXCEEDED`；REJECTED 不改状态；录制/应用状态区分；每座独立配额。 | `pending`（本次未运行该类） |
| G-053 | `server/.../idempotency/RequestScopeReceiptStore.kt:210-295`；`RequestScopeReceiptStoreTest:48-119` | C-S `com.majiang.server.idempotency.RequestScopeReceiptStoreTest` | FakeClock=1,000,000；principal `user-1`；scope 10min；`req-1`/`req-3`；CREATE_ROOM 载荷 `{"api":"CREATE_ROOM"}`/`{"mode":"hanchan"}`；无随机。 | 首次 Admitted；complete R101；同键同载荷 Replay R101；同键不同载荷/API Conflict；在途同键 WaitInFlight 共享同一 Future；不重复执行副作用。 | `pending` |
| G-054 | `RequestScopeReceiptStore.kt:244-295,448-490`；`RequestScopeReceiptStoreTest:145-213` | C-S `com.majiang.server.idempotency.RequestScopeReceiptStoreTest` | FakeClock=10,000,000；scope 600,000ms；advance 11min；maxInFlight=2、maxCompleted=3；无随机。 | 过期 scope 上已有回执→Replay；新 requestId→`ScopeExpired`（不重做、不写新回执）；在途 2 后第 3 个→`ServerBusy`；完成 3 个副作用后第 4 个→`ServerBusy`；过期清理扣减成员计数。 | `pending` |
| G-055 | 迁移方案 §D7/F4；`TcpClientBinding.kt:2205-2280` 对照 | 无 V4 命令 | V5：持久 outbox + `RECEIPT_QUERY`；scope/requestId/payloadSha256；UNKNOWN 展示对账；不自动换新 ID。 | V4 无此消息/API；V5 必须新增。预期：同键同载荷返回原回执；不同载荷 CONFLICT；未提交/不明→UNKNOWN；过期 scope 无回执→`REQUEST_SCOPE_EXPIRED`，绝不重做；客户端杀进程/重连仍可查询。 | `v5-only`（不计入 V4 对齐率） |
| G-056a | `client/.../PlayerStreamEngine.kt:60-84`；`PlayerStreamEngineTest` 的 6 个方法 | C-C `com.majiang.client.session.PlayerStreamEngineTest` | 初始快照；顺序更新；缺口；全量替换；早期缓冲安装；监听器异常；无随机。 | 初始快照安装、顺序推进、缺口触发重排/恢复、全量基线清空重排、早期缓冲清空、监听器异常隔离。**该测试只覆盖少量乱序，不证明第 65 条重排溢出；也不覆盖 256 条已应用游标去重。** | `pending` |
| G-056b | `client/.../SpectatorStreamEngine.kt`；`SpectatorStreamEngineTest` 的 8 个方法 | C-C `com.majiang.client.session.SpectatorStreamEngineTest` | 观战初始快照；乱序/缺口；全量基线；早期缓冲 2 条溢出；绑定安装；无随机。 | 观战独立游标推进、缺口恢复、全量基线替换、早期缓冲 2 条溢出的合并全量、绑定期间推送不丢失。**64 条观战早期缓冲上限由 G-056c 的 binding 测试覆盖，不在本命令内。** | `pending` |
| G-056c | `client/.../TcpClientBinding.kt:319-370,417`；`TcpClientBindingRegressionTest` 的 `R09 role switch...`、`R09 early buffer 65 items...`、`R09 early buffer wire byte budget 4 MiB...`、`R04 128 unconfirmed...`、`R04 replay ... more than 64` | C-C `com.majiang.client.transport.tcp.TcpClientBindingRegressionTest` | MockPeer；`TcpLimits(maxInFlightRequests=64)`；请求超时 100ms/5000ms；65 条早期玩家/观战更新；4 MiB 字节预算；128 条未确认副作用；70 条重放；无随机。 | `bindingEpoch` 在 leaveRoom/joinSpectator 后递增并清空早期缓冲；早期玩家/观战缓冲上限 64；4 MiB 字节上限；第 65 条/超字节触发溢出并请求全量；未确认副作用硬上限 128，满额返回 `SERVER_BUSY`；重放时在途 ≤64。 | `pending` |
| G-056d | `client/.../PlayerStreamEngine.kt:60-63`（`maxBufferSize=64`、`maxAppliedBatchCursors=256`）；`TcpClientBinding.kt:1483-1504` 的 `capturedEpoch` 派发门控 | 需新增 V4 探针；当前无可用命令 | 构造 65 条乱序玩家更新；257 个不同 `fromCursor` 批次；派发前推进 `bindingEpoch`。 | **现有测试未证明**：第 65 条重排溢出/全量恢复；257 个游标后的去重淘汰/行为；旧 `bindingEpoch` 在派发前变化时丢弃旧更新。需新增 V4 探针并记录实际行为，不能把源码静态事实写成已复现。 | `pending-v4-replay`（新增探针） |
| G-057a | `session/PlayerView.kt:12-70`；`SessionSnapshotContractTest` 的 `view never shares a meld's tile collection...`、`view snapshots the nested tile list...`、`view scores and hand stay unchanged...` | C-E `com.majiang.session.SessionSnapshotContractTest` | `GameState()` + `PlayerView.fromGameState(state, 0, actions)`；推进权威 state 后读旧 view；无随机。 | 旧 view 的 `myClosedTiles`/`scores` 不被后续 state 修改；副露 tile 列表/合法动作 tile 列表与源集合解耦（源 `clear()` 后仍保留 3/2 张）。**对手只暴露 `handTileCounts` 的断言不在本测试内，见 G-057d。** | `pending` |
| G-057b | `session/SessionUpdate.kt:56-93`；`SessionSnapshotContractTest` 的 `session update does not retain...`、`private dealt drawn and rinshan...`、`unpublished ura dora...`、`public discard...` | C-E `com.majiang.session.SessionSnapshotContractTest` | 私有 `TileDealt`/`TileDrawn`/`RinshanDraw`；未公开 `DoraRevealed(isUraDora=true)`；公开 `TileDiscarded`；生产者的可变事件列表；无随机。 | 生产者列表变更不影响 `visibleEvents`；私有发牌/摸牌/岭上仅 owner 可见；未公开里宝对 viewer=1 为空；公开弃牌四席均可见。 | `pending` |
| G-057c | `projection/PublicProjectionFactory.kt:96-153`；`PublicProjectionTest` 的 12 个方法 | C-E `com.majiang.projection.PublicProjectionTest` | 四家手牌/牌河/副露；发牌/摸牌/岭上/里宝/千术流水事件；`revealSettlementUra=false/true`；无随机。 | 公开流只含白名单；发牌/摸牌/岭上/千术流水默认丢弃；里宝仅 `revealSettlementUra=true` 或和牌/结算授权时保留；`deepBreakdown` 的 `uraTiles/uraHits` 未授权时剥除；返回集合不可变/深拷贝。 | `pending` |
| G-057d | `session/PlayerView.kt:12-45`（对手仅 `handTileCounts`；不暴露牌山顺序/未公开里宝）；当前无直接断言该字段的测试 | 需新增 V4 探针；当前无可用命令 | seat0 视角；对家手牌/摸牌非空；`uraDoraIndicators` 非空；牌山含顺序；构造 `PlayerView.fromGameState(state, 0, ...)`。 | **现有测试未证明**：对手暗牌/摸牌不以具体牌出现；`handTileCounts` 只给张数；牌山顺序/未公开里宝不进入 view。需新增 V4 探针，不能把源码静态字段声明写成已复现。 | `pending-v4-replay`（新增探针） |

---

## 7. V5 新能力样本（只属 V5，不计入 V4 对齐率）

| ID | 来源/依据 | 命令 | 输入（V5 目标） | 预期事件/状态/投影 | 状态 |
|---|---|---|---|---|---|
| G-058 | 迁移方案 §3.4/F5/F6；V4 无 ownerEpoch/JDBC/租约 | V5 命令待 P3.4/P5.2 提供，当前无 | V5 `ownerEpoch`/`table_lease`/`table_state`/`table_event`/`request_receipt`/`checkpoint`/`settlement_outbox`；Postgres 事务。 | 同键同载荷重放、不同载荷冲突；拒绝不递增版本、无事件；接受命令至少 1 事件；ownerEpoch + stateVersion 双 fence；提交失败不发布；冷恢复重放后 136/账本/哈希一致。 | `v5-only` |
| G-059 | 迁移方案 §3.2.3/F1/F8；V4 无 Protobuf/WSS/TLS/Unity | V5 命令待 P2.1/P2.2/P5.1/P5.4 提供，当前无 | `protocolMajor=1`、`rulesetVersion=1`；Protobuf oneof/字段号；WSS/TLS；Unity/IL2CPP。 | major 不匹配拒绝；minor 未知字段忽略；未知事件请求全量；入站 64 KiB/出站 1 MiB；V4/V5 不混连；IL2CPP 无反射序列化；WSS 证书/分片/慢消费者测试。 | `v5-only` |

---

## 8. 覆盖核对与提取 backlog

### 8.1 必需覆盖项

| P0.1 要求 | 样本 |
|---|---|
| 136 张位置转移 | G-003a、G-004、G-005、G-006、G-007、G-008、G-012 |
| 34 类牌与赤五 | G-001a、G-001b、G-003a、G-043 |
| 常见及边界和牌算分 | G-013、G-014、G-015、G-016、G-017、G-018、G-019 |
| 连庄/终局/流局 | G-020、G-021、G-022、G-023、G-024、G-025、G-026、G-027 |
| 终态不可复活/新局新 ID | G-028a（单局终态）、G-028b（nextRound 新 roundId） |
| CHANKAN_WINDOW | G-029 |
| 三绝技成功 | G-032、G-035、G-038 |
| 三绝技拒绝/回退 | G-033、G-034、G-036、G-037、G-039 |
| 三绝技物理/私有 | G-040a（落汗）、G-040b（袖里藏花）、G-040c（偷梁换柱） |
| AI 托管/真人收回 | G-048（控制代次）；G-049（AI stale/视图） |
| 超时裁决 | G-046、G-047 |
| 多事件命令 | G-011（发牌）、G-030（杠+宝牌+岭上） |
| 无事件拒绝 | G-031 |
| 回执/幂等 | G-052、G-053、G-054 |
| 协议错误/帧 | G-050a、G-050b、G-050c、G-051 |
| 投影/观战 | G-056a..G-056d、G-057a..G-057d |
| V5 新能力 | G-055、G-058、G-059 |

### 8.2 提取批次与责任

| 批次 | 内容 | 责任 | 完成标准 |
|---|---|---|---|
| P0.1-a | 把已有测试断言导出为 JSON：G-001a/b、G-003a、G-004..G-008、G-010、G-013..G-018、G-020..G-023、G-024、G-025..G-039、G-040a..c、G-041..G-044、G-046..G-048、G-050a..c、G-051..G-054、G-056a..c、G-057a..c | P0.1 执行者 + 对应 P1/P2/P3 任务 owner | 每例有输入/预期/命令；`exported`；`dotnet test` 可读 |
| P0.1-b | 录制完整状态/随机/时间/ID 或新增探针：G-001c、G-002、G-003b、G-009、G-011、G-012、G-019、G-045、G-049、G-056d、G-057d | P1.1/P1.3/P1.4/P1.5/P1.6/P3.2 | 记录实际牌山/抽牌/ID/时钟；G-001c/G-002/G-003b/G-056d/G-057d 先补 V4 探针，不接受源码静态推断 |
| P0.1-c | V5 新能力样本：G-055、G-058、G-059 | P2.1/P3.4/P5.x | 以 V5 实现和测试为准；不得拿 V4 样本冒充 |

### 8.3 对齐率口径

#### 8.3.1 历史口径（2026-10-23，保留）

- 当时清单 73 例：`pending` 59、`pending-v4-replay` 11（G-001c、G-002、G-003b、G-009、G-011、G-012、G-019、G-045、G-049、G-056d、G-057d）、`v5-only` 3（G-055、G-058、G-059）、`exported` 0。
- 当时分母 = `exported` 的 V4 样本数；当时为 **0**。
- 当时 `pending`/`pending-v4-replay`/`v5-only` 不计入分子或分母，因此不能报告“对齐率 100%”或“黄金样本已建立”。
- 这段是历史盘点，不抹掉；它说明 2026-10-23 的真实起点。

#### 8.3.2 当前机械口径（2026-10-25，`catalog-v1.json`；N3-C1 六组）

- 六组 fixture 已存在并被 6 个 catalog C# Golden reader 类读取；但 **73 条 G 需求并未全部导出**。
- 73 条 G 需求：`full` 17、`partial` 21、`none` 32、`v5-only` 3。`full` 指该条目的全部约定断言在当前范围内有证据；`partial` 只覆盖部分断言并单列缺口；`none` 与 `v5-only` 不进入 V4 对齐分子。G-049 由 `none` 改为 `partial`，不是 `full`。
- 具体 JSON case：**10,389**（旧五组 10,382 + ai-v1 7）。其中 scoring 的 `randomSamples` **10,000** 条是 scoring 10,238 的子集，不另加；catalog C# `[Fact]` **45**（6 个类：Domain 42 + Ai 3）；fixture **6**。这些分母不能相加成一个“完成数”。`AiReplayStateGoldenTests` 的 3 个已接受状态绑定 Fact 是独立证据类，不加入 45。
- 历史五组口径（2026-10-24，保留）：10,382 / 42 Facts / 5 classes / 5 fixtures。冻结审计 replay 口径 10,353；Δ29 = round +23 + cheat +6，是 catalog 相对审计定义多出的条目，不是新增样本。详细分解见 §0.3 与 `V5-N3-C1-CATALOG-UPDATE.md`。
- “fixture 已导出 + C# 测试运行通过”只证明这些样本可读、可差分，不等于 73/73，也不等于 G1 PASS。当前仍有 32 条 `none`、21 条 `partial`。
- 逐项状态见 §8.5；机器可读锚点/计数/哈希见 `tests/v5-golden/catalog-v1.json`，并由 `tools/verify-v5-golden-catalog.ps1` 从真实 JSON 重新计算。verifier 只证明索引/计数/provenance，不替代领域行为测试。

### 8.4 复现命令（当前）

```powershell
# 1) 只读校验六组 fixture + catalog（正常 exit 0；坏 catalog/SHA/重复 ID/未知 schema/缺文件 exit 1）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1

# 2) 在独占 TEMP 副本注入坏输入；验证 FAIL 且真实 fixture 不变
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5GoldenCatalog.Tests.ps1 -LogPath <TEMP>\selftest.log

# 3) 定向运行 6 个 catalog C# Golden reader 类（45 Facts；Domain 42 + Ai 3；不跑 PG）
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class "*GoldenTests" -noColor -noLogo
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class "Mahjong.Ai.Tests.AiReplayGoldenTests" -noColor -noLogo

# 4) 已接受 AI 状态绑定证据（3 Facts，独立分母；不是 catalog 的 45）
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -class "Mahjong.Ai.Tests.AiReplayStateGoldenTests" -noColor -noLogo
```

注意：该测试工程是 xUnit v3 Microsoft.Testing.Platform；实测 `dotnet test --filter "FullyQualifiedName~Golden"` 被忽略并运行全部 322 个 Domain 测试，因此定向证据使用 direct runner 的 `-class`，并报告 runner 的实际选中数。Domain `*GoldenTests` = 42/0/0，Ai `AiReplayGoldenTests` = 3/0/0；全量 Domain/Ai 数只能作为全量证据，不能替代定向计数。

### 8.5 逐项当前状态（catalog-v1 机械核对）

> §1–§7 表内的“提取状态”保留 2026-10-23 历史盘点，不改成当前值；本节给出当前 catalog 覆盖。`full`/`partial`/`none`/`v5-only` 的口径见 §8.3.2。锚点格式为 `fixture:group/id`；组级锚点表示整组。

| ID | 历史提取状态（2026-10-23，保留） | 当前 catalog 覆盖 | 当前锚点（fixture:group/id） | C# 证据（method/字段） | 责任任务 | 说明 |
|---|---|---|---|---|---|---|
| G-001a | `pending` | `full` | physical-v1:samples/G-001a | PhysicalGoldenTests.G001aStandardSetMatchesDomainTileIdentityAndRedFiveRules: TileId fields, 136 tiles, redFiveIds 19/55/91 | P1.1 | 136 tiles and exactly 3 red fives exported; C# G001a asserts every identity/flag field. |
| G-001b | `pending` | `full` | physical-v1:samples/G-001b | PhysicalGoldenTests.G001bTypeIndexBoundariesAnd34GroupsMatchDomain: boundaryTypeIndexes, 34 groups, totalTiles=136 | P1.1 | 34 typeIndex groups and 10 boundary indexes exported; C# G001b replays. |
| G-001c | `pending-v4-replay` | `full` | physical-v1:samples/G-001c | PhysicalGoldenTests.G001cTileIdRoundTripAndIllegalInputsMatch: valid 0..135, boundaries 0/135, illegal inputs | P1.1 | 0..135 roundtrip plus 4 illegal inputs exported; C# G001c replays semantics (not exception-class equality). |
| G-002 | `pending-v4-replay` | `full` | physical-v1:samples/G-002 | PhysicalGoldenTests.G002ZoneOwnerSemanticsMapToImmutablePositions: legal/illegal zone-owner probes; no-seat owner structural equivalence | P1.1 | 20 zone/owner probes exported; C# maps legal positions and illegal-seat semantics; no-seat-zone owner is structurally unconstructible in C#. |
| G-003a | `pending` | `full` | physical-v1:samples/G-003a | PhysicalGoldenTests.G003aExportedWallsDriveDomainMapAndConservationWithoutRngClaim: 32 walls, red fives, 136 positions, dead-wall segment | P1.1 | 32 fixed seeds with complete walls and 4352 positions; C# replays explicit walls structurally, not JVM RNG reproduction. |
| G-003b | `pending-v4-replay` | `partial` | physical-v1:samples/G-003b | — | P1.1 | Real deletion and ledger-mismatch probes exported; C# covers missing placements and single-ledger invariant. Duplicate-key branch is NOT_CONSTRUCTIBLE in V4 MutableMap. |
| G-004 | `pending` | `none` | — | — | P1.1 | No draw/discard TileMovement case in the five fixtures. |
| G-005 | `pending` | `none` | — | — | P1.1 | No pon/chi case in the five fixtures. |
| G-006 | `pending` | `none` | — | — | P1.1 | No kan case in the five fixtures. |
| G-007 | `pending` | `none` | — | — | P1.1 | No duplicate-copy/fifth-rinshan rejection case in the five fixtures. |
| G-008 | `pending` | `partial` | cheat-v1:tileMovement/mv-swap-hand-with-wall-direct<br>cheat-v1:tileMovement/mv-swap-hand-with-dead-wall | — | P1.1 | Two hand/wall swap cases exported and replayed; no draw/discard/pon/kan movement cases. |
| G-009 | `pending-v4-replay` | `none` | — | — | P1.1 | No hidden-replacement/full-136 audit trace exported. |
| G-010 | `pending` | `none` | — | — | P1.1 | No duplicate-physical-wall deal rejection case in the five fixtures. |
| G-011 | `pending-v4-replay` | `none` | — | — | P1.1 | No full deal event sequence/state trace exported. |
| G-012 | `pending-v4-replay` | `none` | — | — | P1.1 | No post-deal full 136-placement trace exported. |
| G-013 | `pending` | `partial` | scoring-v1:winSamples | — | P1.2 | 48 curated win samples cover the evaluator, but the exact 4-han/30-fu/7700 G-013 hand is not separately anchored. |
| G-014 | `pending` | `partial` | scoring-v1:fuSamples | — | P1.2 | 9 fu samples cover rounding, but the exact 28->30 G-014 case is absent. |
| G-015 | `pending` | `partial` | scoring-v1:doraSamples/D-002 | — | P1.2 | D-002 has indicator=2/red=1/total=3; the single-red-plus-indicator 1/1/2 case is absent. |
| G-016 | `pending` | `full` | scoring-v1:noWinSamples/N-001 | ScoringGoldenTests.CuratedWinAndNoWinSamplesMatchV4 -> CompareNoWinSample (N-001): Evaluate returns null | P1.2 | Open hand with dora but no yaku exported as noWin; C# replays. |
| G-017 | `pending` | `full` | scoring-v1:scoreSamples | ScoringGoldenTests.ScoreTableSamplesMatchV4 -> CompareScoreSample: base/raw/cap/payments/payment plans | P1.2 | 147 score-table samples include fu/han caps and rawExact; C# ScoreTableSamplesMatchV4 replays every field. |
| G-018 | `pending` | `full` | scoring-v1:scoreSamples/S-YAKUMAN-1<br>scoring-v1:scoreSamples/S-YAKUMAN-2 | ScoringGoldenTests.ScoreTableSamplesMatchV4 -> S-YAKUMAN-1/2/3/4: base points, multipliers, payments | P1.2 | Yakuman 1 and 2 (plus 3/4) and payment plans exported; C# replays. |
| G-019 | `pending-v4-replay` | `partial` | scoring-v1:winSamples | — | P1.2 | All 42 distinct yaku codes appear in winSamples and C# asserts 42, but the 41/42 D-01 adjudication is still unresolved. |
| G-020 | `pending` | `full` | scoring-v1:settlementSamples/ST-001<br>scoring-v1:settlementSamples/ST-002 | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-001/ST-002: deltas, newScores, remainingRiichiSticks | P1.2 | Nondealer/dealer ron deltas and conservation exported; C# replays exact deltas. |
| G-021 | `pending` | `full` | scoring-v1:settlementSamples/ST-003<br>scoring-v1:settlementSamples/ST-004<br>scoring-v1:settlementSamples/ST-007 | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-003/ST-004/ST-007: tsumo deltas/newScores/sticks | P1.2 | Nondealer/dealer tsumo and dealer=3 payer split exported; C# replays exact deltas. |
| G-022 | `pending` | `full` | scoring-v1:settlementSamples | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-100..ST-115: all 16 tenpai masks deltas/newScores/sticks | P1.2 | ST-100..ST-115 cover all 16 tenpai masks; C# replays deltas and stick handling. |
| G-023 | `pending` | `full` | scoring-v1:settlementSamples/ST-008 | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-008: abortive draw deltas/newScores/sticks | P1.2 | Abortive draw deltas/zero-change exported; C# replays. |
| G-024 | `pending` | `none` | — | — | P1.2 | No session-boundary riichi-stick transfer case in the five fixtures. |
| G-025 | `pending` | `full` | round-v1:matchProgression/RG-003a<br>round-v1:matchProgression/RG-003b<br>round-v1:matchProgression/RG-003c<br>round-v1:matchProgression/RG-003d<br>round-v1:matchProgression/RG-003e | RoundReplayGoldenTests.MatchProgressionMatrixReplaysExactly -> RG-003a..e: next/dealer/wind/round/honba/scores | P1.3 | Basic dealer/nondealer tsumo, exhaustive draw and abortive progression rows exported; C# replays all fields. |
| G-026 | `pending` | `full` | round-v1:matchProgression/RG-003f<br>round-v1:matchProgression/RG-003g<br>round-v1:matchProgression/RG-003h<br>round-v1:matchProgression/RG-003i<br>round-v1:matchProgression/RG-003j<br>round-v1:matchProgression/RG-003p | RoundReplayGoldenTests.MatchProgressionMatrixReplaysExactly -> RG-003f..j,p: next/final/rankings/reason/transferSeat | P1.3 | East-4 dealer rotation, south-4 target, west extension, west-4 finish and tonpuusen finish rows exported; C# replays. |
| G-027 | `pending` | `full` | round-v1:matchProgression/RG-003m<br>round-v1:matchProgression/RG-003n<br>round-v1:matchProgression/RG-003o | RoundReplayGoldenTests.MatchProgressionMatrixReplaysExactly -> RG-003m..o: agari-yame/final/rankings/transferSeat | P1.3 | Agari-yame eligibility/declaration and tie transfer exported; C# replays. |
| G-028a | `pending` | `partial` | round-v1:roundTerminal/RG-001 | — | P1.3 | C# RoundTerminalReplaysExactly asserts version/phase/result/scores/rejected actions and presence of TsumoWin, but not absence of MatchEnded or the full eventTypes sequence (ScoreSettled/RoundEnded). |
| G-028b | `pending` | `none` | — | — | P1.3 | No nextRound/new-roundId trace in the five fixtures. |
| G-029 | `pending` | `full` | round-v1:chankanRon/RG-002 | RoundReplayGoldenTests.ChankanRonReplaysExactly: phase/stateHash/rinshan/kan/events/result/scores/version | P1.3 | CHANKAN_WINDOW state and ron-before-kan atomicity exported; C# replays. |
| G-030 | `pending` | `none` | — | — | P1.3 | No double-kan/DoraRevealed/RinshanDraw multi-event trace exported. |
| G-031 | `pending` | `partial` | cheat-v1:ledger | — | P1.3 | Ledger rejections prove zero balance/entry side effects; no event-list/version assertion for a no-event command. |
| G-032 | `pending` | `partial` | cheat-v1:tileMarkHandler/tm-allowed<br>cheat-v1:ledger/chargeOnce | — | P1.4 | Allowed outcome and ledger charge exported; no single case with full post-state, group tiles, locations and deadline. |
| G-033 | `pending` | `partial` | cheat-v1:tileMarkHandler/tm-capacity-groups<br>cheat-v1:tileMarkHandler/tm-capacity-tiles<br>cheat-v1:tileMarkState/expiry | — | P1.4 | Capacity and expiry cases exported; C# replays expiry/cross-seat, capacity cases are reason-covered only. |
| G-034 | `pending` | `partial` | cheat-v1:tileMarkHandler | — | P1.4 | 19 rejection cases with stable reasons exported; C# replays a representative subset, not every case. |
| G-035 | `pending` | `partial` | cheat-v1:sleeveBloomHandler/sb-activate-allowed<br>cheat-v1:sleeveBloomHandler/sb-choose-first | — | P1.4 | Activation/choice cases and golden inputs exported; C# binds a subset, not the full snapshot. |
| G-036 | `pending` | `partial` | cheat-v1:sleeveBloomHandler/sbo-activate-allowed<br>cheat-v1:sleeveBloomHandler/sbo-choose-allowed<br>cheat-v1:sleeveBloomHandler/sbo-choose-expired | — | P1.4 | Off-turn activation/reservation/expiry cases exported; C# binds reservation delivery, not every timing branch. |
| G-037 | `pending` | `partial` | cheat-v1:sleeveBloomHandler/sb-choose-stale-turn<br>cheat-v1:sleeveBloomHandler/sb-choose-offer-mismatch<br>cheat-v1:sleeveBloomHandler/sbo-activate-no-window | — | P1.4 | Stale/wrong-window/no-offer reasons exported; C# replays a subset. |
| G-038 | `pending` | `partial` | cheat-v1:beamSwapHandler/bm-activate-allowed<br>cheat-v1:beamSwapHandler/bm-choose-swap<br>cheat-v1:beamSwapHandler/bm-choose-keep | — | P1.4 | Activation/swap/keep cases exported; C# binds candidate entities and keep, not the full snapshot. |
| G-039 | `pending` | `partial` | cheat-v1:beamSwapHandler/bm-choose-invalid-slot<br>cheat-v1:beamSwapHandler/bm-choose-invalid-foreign-hand<br>cheat-v1:beamSwapHandler/bm-choose-stale-turn | — | P1.4 | Invalid/stale/foreign cases exported; C# replays a subset. |
| G-040a | `pending` | `none` | — | — | P1.4 | No owner/other-seat TileMark projection case in the five fixtures. |
| G-040b | `pending` | `none` | — | — | P1.4 | No owner/other-seat SleeveBloom projection case in the five fixtures. |
| G-040c | `pending` | `none` | — | — | P1.4 | No owner/other-seat BeamSwap projection case in the five fixtures. |
| G-041 | `pending` | `partial` | cheat-v1:ledger | — | P1.4 | C# Ledger_Section_ReplaysAgainstCheatLedger asserts replay/conflict/overdraw/overflow/invalid seat/summary/snapshot, but not round version, mahjong scores, or 136-tile conservation; those G-row assertions remain unverified. |
| G-042 | `pending` | `full` | shuffle-v1:slotMatrix | ShuffleGoldenTests.SlotMatrixMatchesCSharpShuffleSlots: 16 dealer/seat initialSlots/drawSlots | P1.5 | 16 dealer/seat slot entries with initial and draw slots exported; C# replays. |
| G-043 | `pending` | `partial` | shuffle-v1:cases | — | P1.5 | 32 fixed-seed walls, red fives, dead-wall isolation, 3/4-caster laws exported; illegal seat/config rejection is not exported. |
| G-044 | `pending` | `none` | — | — | P1.5 | See-through/anti-target cases are not in the five fixtures. |
| G-045 | `pending-v4-replay` | `none` | — | — | P1.5 | ShufflePreparation SecureRandom seed is not exported; D-07 remains. |
| G-046 | `pending` | `partial` | round-v1:decisionClock/RG-004a<br>round-v1:decisionClock/RG-004b<br>round-v1:decisionClock/RG-004c<br>round-v1:decisionClock/RG-004d<br>round-v1:decisionClock/RG-004e<br>round-v1:decisionClock/RG-004f | — | P1.3 | RG-004a..f have 9 successful open calls but no duplicate-open rejection and no old-roundId-key open rejection; DecisionClockTracesReplayExactly only replays successful open/complete/cancel/pause/resume/remainingReserve/expiry paths. The G-046 duplicate-open and old-round assertions remain unverified. |
| G-047 | `pending` | `none` | — | — | P1.3 | TimeoutPolicy selection is not in the five fixtures. |
| G-048 | `pending` | `partial` | round-v1:controlEpoch/RG-005 | — | P1.3 | RG-005 covers engine ControllerRegistry epochs only; server ConnectionControl grace/reclaim is explicitly not exported by the engine-side exporter. |
| G-049 | `pending-v4-replay` | `partial` | ai-v1:cases/AI-002-old-round<br>ai-v1:cases/AI-003-old-decision<br>ai-v1:cases/AI-004-old-epoch<br>ai-v1:cases/AI-005-wrong-seat<br>ai-v1:cases/AI-006-candidate-not-legal<br>ai-v1:cases/AI-007-exact-deadline<br>ai-v1:cases/AI-001-legal-intent-before-deadline<br>ai-v1:decisionClock<br>ai-v1:recordedIntent<br>ai-v1:v5Observation | `AiReplayGoldenTests` 3 Facts: provenance, Replay/Submit mapping, accepted DeadlineArbiter; `AiReplayStateGoldenTests` 3 accepted Facts: real RoundStateMachine execution, rejected zero side effects, field coverage/unrepresentable guard | P1.6 | ai-v1 accepted and indexed. G-049 stays **partial**: `mappingBasis`/`mappingLimits` (GAP:N3-B6), `v4.events[*].text`/`v4Call.action.text` (GAP:N3-B9), a C# post-state vs V4 full-tree differential for every case, and AI host/cheat intent remain unbound or unexported. Do not read as full, all-AI migration, or G1. |
| G-050a | `pending` | `none` | — | — | P2.1 | No protocol/wire fixture among the five. |
| G-050b | `pending` | `none` | — | — | P2.1 | No frame-codec fixture among the five. |
| G-050c | `pending` | `none` | — | — | P2.1 | No message-codec fixture among the five. |
| G-051 | `pending` | `none` | — | — | P2.1 | No error/close-reason fixture among the five. |
| G-052 | `pending` | `none` | — | — | P2.1 | No receipt-quota fixture among the five. |
| G-053 | `pending` | `none` | — | — | P2.1 | No request-scope receipt fixture among the five. |
| G-054 | `pending` | `none` | — | — | P2.1 | No receipt-store expiry/saturation fixture among the five. |
| G-055 | `v5-only` | `v5-only` | — | — | P5.4a | V5-only persistent outbox/RECEIPT_QUERY; no V4 fixture by design. |
| G-056a | `pending` | `none` | — | — | P3.2 | No player-stream fixture among the five. |
| G-056b | `pending` | `none` | — | — | P3.2 | No spectator-stream fixture among the five. |
| G-056c | `pending` | `none` | — | — | P3.2 | No binding early-buffer/unconfirmed-side-effect fixture among the five. |
| G-056d | `pending-v4-replay` | `none` | — | — | P3.2 | No 65-item reorder/257-cursor/bindingEpoch-race probe. |
| G-057a | `pending` | `none` | — | — | P3.2 | No PlayerView snapshot fixture among the five. |
| G-057b | `pending` | `none` | — | — | P3.2 | No SessionUpdate visibility fixture among the five. |
| G-057c | `pending` | `none` | — | — | P3.2 | No public-projection fixture among the five. |
| G-057d | `pending-v4-replay` | `none` | — | — | P3.2 | No opponent-hidden/ura/wall-order probe. |
| G-058 | `v5-only` | `v5-only` | — | — | P3.4 | V5-only ownerEpoch/lease/Postgres contract; no V4 fixture by design. |
| G-059 | `v5-only` | `v5-only` | — | — | P2.1/P2.2/P5.1/P5.4 | V5-only Protobuf/WSS/TLS/Unity/IL2CPP behavior; no V4 fixture by design. |

---

## 9. 边界声明

- 本文件保留 2026-10-23 原始盘点，并在 2026-10-24 增补当前事实、逐项状态和分母；历史数字不被删除或改写。
- 五组 fixture、五个 V4 exporter、五个 C# Golden reader 类均为只读证据；本任务没有修改它们的预期值、fixture 内容、V4 Domain/Ai/TableRuntime 生产逻辑、CI wrapper 或其他文档。
- 本任务新增/修改的授权文件：`tests/v5-golden/catalog-v1.json`、`tools/verify-v5-golden-catalog.ps1`、`tools/tests/VerifyV5GoldenCatalog.Tests.ps1`、本清单、`doc/v5-重构/V5-GOLDEN-CATALOG-RECONCILIATION.md`。
- catalog 的 SHA-256、分组计数、ID 唯一性和锚点由校验器从真实 JSON 重新计算；没有手填预期哈希，没有把 `generationResult` 的自述当成“所有样本已比较”。
- 本任务没有把 V4 的 1,275 项历史运行数、1,826 个静态测试方法、470 个定向用例、1,852 全量回归或本题 42 个 C# Fact 写成 V5 验收结果。
- G1 仍 `BLOCKED`，G5 未完成；V4 仍是唯一生产基线。Unity/IL2CPP/Android/iOS 真机、真实网络、PG/WSS/CI 未在本任务运行或未通过。
- 本任务未 push、未创建 PR、未触发远端 CI、未修改机器全局环境/信任；交付为本地 commit，由 caller 独立复核后负责唯一 PR19 与 merge-back。
