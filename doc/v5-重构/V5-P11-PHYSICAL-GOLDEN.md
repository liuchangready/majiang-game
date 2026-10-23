# V5 P1.1 物理牌 V4 → C# 黄金样本（G-001a/b/c、G-002、G-003a/b）

日期：2026-10-24
工作树：`D:/majiang-wt/physical-golden`
分支：`wt/v5-physical-golden`
任务起始基线：`32f645ec9b134e9ad261a2df89437416b20b3119`（clean）
本次提交前 HEAD：`32f645ec9b134e9ad261a2df89437416b20b3119`
V4 生产架构：唯一生产基线；**G1/G5 未通过**。
范围：只补清单六项物理牌条目，不重新开发 P1.1，不做 73 例全量审计。

---

## 0. 结论先行

| 项目 | 结果 | 证据 |
|---|---|---|
| V4 真实探针 + 导出 `physical-v1.json` | **PASS** | 1 个导出器测试通过，exit 0；6/6 目标样本 `exported` |
| V4 既有物理针对测试 | **PASS** | 32 个既有测试 + 1 个导出器 inert 测试 = 33/0/0，exit 0 |
| C# Domain.Tests | **PASS** | 322/0/0（基线 313 + 新增 9），`dotnet test` exit 0 |
| C# 逐项 golden 差分 | **PASS** | 9 个新 `[Fact]` 全绿；XML 322/0/0，exit 0 |
| 导出可再生成（同输入） | **PASS** | 连续两次导出 SHA256 完全相同：`21a3c4c8…60f2` |
| G-003b duplicate-key 运行时分支 | **NOT_CONSTRUCTIBLE** | `MutableMap<Tile,TileLocation>` 相等键覆盖；已记录实际 `size=1`，未伪造 |
| G-003b C# mutable 损坏注入 | **NOT_APPLICABLE / 结构替代** | C# 单账本不可变 map；以构造期拒绝 + 只读视图验证，未写成异常等价 |
| G1 | **NOT_RUN（范围外）** | 只导出六项；未运行 G1 全套 |
| 全 73 例审计 | **NOT_RUN（范围外）** | 其余 67 项未重审 |
| 平台/Unity/IL2CPP/Android/iOS/真实网络/PG/WSS/CI | **NOT_RUN** | 本任务不涉及；不得声称已测 |

V4 仍是唯一生产；未修改 V4 规则、Domain 生产码、csproj、CI、actor、Wire、主清单或其他黄金文件。

---

## 1. 授权文件与提交边界

本次只创建/修改以下 4 个授权文件：

1. `engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt`
2. `tests/v5-golden/physical-v1.json`
3. `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs`
4. `doc/v5-重构/V5-P11-PHYSICAL-GOLDEN.md`（本文件）

`git diff --name-only HEAD` 为空（无 tracked 生产文件修改）。生成时 `git status --porcelain` 只有任务文件：

```
?? doc/v5-重构/V5-P11-PHYSICAL-GOLDEN.md
?? dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs
?? engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt
?? tests/v5-golden/physical-v1.json
```

`V5-0-GOLDEN-MANIFEST.md` 未被修改：它不在授权文件内。因此清单头部聚合计数仍写“exported 0”；这是**未被本任务篡改**的旧聚合值，不能据此否认本文件已导出的六项，也不能据此声称全清单已完成。

---

## 2. 六个清单条目的 crosswalk

清单原始状态：3 个 `pending`（G-001a、G-001b、G-003a）+ 3 个 `pending-v4-replay`（G-001c、G-002、G-003b）。本次六项全部变为 `exported`。

| ID | V4 真实调用/探针 | 实际导出内容 | C# 公开 API 断言 | 结果 |
|---|---|---|---|---|
| **G-001a** | `TilePool.buildStandardSet()` | 136 个 tile 全字段（tileId/suit/rank/copy/typeIndex 5 个身份字段 + 6 个标志位）；赤五 3 张 | `TileId.All` 逐张 Value/TypeIndex/CopyIndex/Suit/Rank/IsRedFive/IsNumber/IsHonor/IsTerminal/IsTerminalOrHonor/IsSimple；赤五恰为 19/55/91 | **PASS** |
| **G-001b** | `Tile.fromTypeIndex` 边界 + 136 标准集按 typeIndex 分组 | 10 个边界样本；34 组、每组 4 张；总计 136 | `TileId.FromTypeIndex` 边界；`TileId.All` 按 TypeIndex 分组；Suit/Rank 一致 | **PASS** |
| **G-001c** | `Tile.fromTileId(0)`、`(135)`、0..135 逐值、-1/136/Int.MIN/Int.MAX | 136 个合法往返；4 个非法（V4 `IllegalArgumentException` + 原始 message） | `new TileId(id)`、`FromTypeIndex(type,copy)`、`TryFromValue`；非法输入 `ArgumentOutOfRangeException`（显式映射，非等价类型） | **PASS** |
| **G-002** | 直接构造 `TileLocation(zone, player)` 20 例（10 接受/10 拒绝） | 每例 zone/player/结果/异常类型/message/语义分类/csharpMapping | Wall/DeadWall/Unassigned 无座位工厂；Hand/Discard/Meld/Sleeve 座位工厂；非法 seat 抛 `ArgumentOutOfRangeException` | **PASS（V4 语义）**；4 个“无座位区带 owner”在 C# 为结构强制，非异常等价，见 §3 |
| **G-003a** | `ShuffleWallGenerator.generate(seed, seed%4, threePlans, enabled)`，seed 0..31（既有 accepted 输入） | 32 例 ×（完整 136 wall + 完整 136 baseWall + 136 positions + 3 赤五 + 规范 SHA256），共 64 张完整墙、4352 positions | 以 V4 wall 作为显式 `RecordedRandomDraw` → `TilePositionMap.CreateWall`；逐 tile 位置、136 守恒、14 王牌、C# hash 可重建；不逐字节复现 V4 RNG | **PASS（结构/位置）** |
| **G-003b** | A：standalone `TilePool` 反射删除 private `tileMap[Tile(MAN,1,0)]`；B：合法 `TestTableBuilder` 后 `setLocation(closedTiles[0], WALL)` | A：136→135，`IllegalArgumentException` “exactly 136…135”，审计后仍 135、无自动修复；B：`IllegalStateException` “TilePool audit failed…”，容器仍 136/未改，二次审计仍失败 | A：`FromPlacements` 135 项/重复 tile/重复 position 抛 `ArgumentException`；B：C# 单账本不可变 map，重建同 placements hash 相同，`AsPlacements` 只读 | **PASS（V4 探针 + C# 结构 invariant）**；mutable 损坏注入 C# 侧 NOT_APPLICABLE，见 §3 |

> 上表 “G-001c” 的异常类型只在语义分类层对照：V4 `java.lang.IllegalArgumentException`，C# `System.ArgumentOutOfRangeException`，不声称异常类相同。

### 2.1 计数（本任务范围）

- 原未导出目标：**6**
- 本任务新实际导出：**6**
- 失败：**0**
- 本任务目标剩余不适用：**0**
- 全局 73 例中未触碰：**67**（未审计，不是“通过”，也不是本任务失败）
- 既有 `scoring-v1.json` / `round-v1.json` / `cheat-v1.json` / `shuffle-v1.json`：**未重做、未覆盖**

不能把“6/6 目标”写成“73/73”或“G1 已过”。

---

## 3. 真实的 V4 探针结果

### 3.1 G-001a / G-001b / G-001c

- G-001a：136 张、136 个唯一身份、tileId 顺序 0..135；赤五恰 3 张，ID 为 **19, 55, 91**；其余 133 张 `isRedFive=false`。全部 136 张满足 `tileId = typeIndex*4 + copyIndex`。
- G-001b：边界 typeIndex `0,8,9,17,18,26,27,30,31,33`；34 个 typeIndex 每组恰好 4 张；`MAN 0..8 / SOU 9..17 / PIN 18..26 / WIND 27..30 / DRAGON 31..33`。
- G-001c：0..135 全部 `Tile.fromTileId(id).tileId == id`；`fromTileId(0)=Man1c0`，`fromTileId(135)=Dragon3c3`。非法输入 `-1, 136, -2147483648, 2147483647` 均抛 `java.lang.IllegalArgumentException`，原始 message 例如：
  - `tileId must be 0..135, got -1`
  - `tileId must be 0..135, got 136`

### 3.2 G-002：zone-owner 矩阵

V4 接受（10）：

| zone | player | 结果 |
|---|---|---|
| WALL | -1 | `TileLocation(WALL,-1)` |
| DEAD_WALL | -1 | `TileLocation(DEAD_WALL,-1)` |
| UNASSIGNED | -1 | `TileLocation(UNASSIGNED,-1)` |
| HAND | 0,3 | 保留 zone/player |
| DISCARD | 0,3 | 保留 zone/player |
| MELD | 0,3 | 保留 zone/player |
| SLEEVE | 1 | 保留 zone/player |

V4 拒绝（10）：

- 无座位区带 owner：`WALL/0`、`WALL/3`、`DEAD_WALL/0`、`UNASSIGNED/0` → `IllegalArgumentException`，message 含 `must not have player owner`。
- 有座位区非法 player：`HAND/-1`、`HAND/4`、`DISCARD/-1`、`DISCARD/4`、`MELD/-1`、`SLEEVE/4` → `IllegalArgumentException`，message 含 `must belong to player 0..3`。

C# 映射说明（不捏造等价）：

- V4 `TileLocation` 只有 `(zone, player)`，没有 slot index；C# `TilePosition` 是 `(zone, seat, index)`，多一个 slot。合法位置/所有权按 zone+seat 对照，index 是 C# 额外维度。
- C# 不为 `WALL/DEAD_WALL/UNASSIGNED` 提供带 seat 的工厂，因此“无座位区带 owner”在 C# **无法构造**；测试断言 `TileZoneRules.RequiresSeat(zone)==false` 且座位工厂返回 `Seat=-1`。这是结构强制，不是相同异常类型/消息，故不写成“异常等价 PASS”。
- 有座位区非法 seat：C# 工厂直接抛 `System.ArgumentOutOfRangeException`，与 V4 `java.lang.IllegalArgumentException` 通过语义分类 `invalid-player-owner` 对照，保存了两端真实异常类型。

### 3.3 G-003a：32 个固定 seed 的真实生成

接受的既有输入（原样复用 `ShuffleAlgorithmAcceptanceTest`）：

```kotlin
plans = [
  SeatShufflePlan(0, ShuffleSelection(ShuffleTarget.KOKUSHI)),
  SeatShufflePlan(1, ShuffleSelection(ShuffleTarget.DAISANGEN)),
  SeatShufflePlan(2, ShuffleSelection(ShuffleTarget.CHINITSU_MAN))
]
for (seed in 0L..31L) {
  dealer = (seed % 4).toInt()
  wall = ShuffleWallGenerator().generate(seed, dealer, plans, ShuffleConfig(enabled = true))
}
```

实际断言/记录：

- 每个 seed 的 wall 都是 136 张、与标准集逐身份一致、赤五恰 3 张。
- `threePlans.reversed()` 得到逐张相同 wall（排序无关）。
- `emptyList()` 的 base wall 与 three-caster wall 在 122..135 王牌段逐张相同。
- 用 V4 `TilePool` 对每个 wall slot 真实 `setLocation` 后 `getLocation` 回读，记录 136 个 `(tileId, zone, seat, slot)`；`standalone.assertConservation()` 通过。
- 每例记录完整 wall、完整 baseWall、赤五 ID、规范 SHA-256。32 例的 `allConserved/allRedFiveThree/allOrderIndependent/allDeadWallIsolated` 均为 true。
- 不声称 C# 复现 V4 JVM `Random(seed)`：C# 只把 V4 生成的显式 wall 作为输入，验证 136 位置/结构法律与 C# 自身 hash 的确定性。

### 3.4 G-003b：两个真实的“坏审计”探针

Probe A（standalone，测试专用反射）：

1. `TilePool(gameState=null)`，初始 136，`Tile(MAN,1,0)` 位置 `UNASSIGNED`。
2. `getDeclaredField("tileMap")` + `setAccessible(true)`，`remove` 该键；实际 `mapSize 136 → 135`。
3. `assertConservation()` 抛 `java.lang.IllegalArgumentException`：
   `Pool must contain exactly 136 tiles, but has 135`
4. 失败后实测：`pool.size=135`、`mapSize=135`、`getLocation(tile)=null`、`missingTileIds=[0]`；**没有自动补回**。
5. 第二次审计再次抛**同一 message**，证明失败不会自修复。

Probe B（bound gameState）：

1. `TestTableBuilder.create()` 合法状态，初始审计通过（`accepted`），池 136，目标牌 `players[0].closedTiles[0]` 在池中为 `HAND:0`。
2. 公开 `tilePool.setLocation(target, TileLocation.WALL)` 只改池账本；实际容器仍是 136 且仍含该牌。
3. `state.tilePool.assertConservation()` 抛 `java.lang.IllegalStateException`：
   `TilePool audit failed for tile Tile(suit=MAN, rank=1, copyIndex=0): actual container has TileLocation(zone=HAND, player=0), but TilePool recorded TileLocation(zone=WALL, player=-1)`
4. 失败后实测：池仍 136、目标池位置仍 `WALL:-1`、实际 136 张集合/hash 前后相同、`closedTiles` 仍含该牌；**没有自动修复**。
5. 第二次审计抛**同一 message**。

duplicate-key 限制（必须明确）：

- `MutableMap<Tile, TileLocation>` 以 `Tile` 的 data-class 相等性为键；第二次 `put` 相同键是**覆盖**，不会产生两个 entry。
- 导出器实际构造并观测：`firstValue=WALL`、`finalValue=HAND:0`、`observedMapSize=1`。
- 因此 `assertStandaloneConservation` 的 duplicate-key 检查在此容器结构下**不可直接触达**（`auditBranchReachability="unreachable"`）。重复物理墙由 G-010 的可运行用例覆盖；本任务**没有伪造**一个 duplicate-key 异常。
- G-003b 的 `duplicateKeyStaticLimitation.status = NOT_CONSTRUCTIBLE`。

---

## 4. C# 对照与不可变 map 的差异

`PhysicalGoldenTests.cs` 真实读取 `tests/v5-golden/physical-v1.json`，新增 9 个 `[Fact]`：

1. `GoldenFileCarriesRealV4ProvenanceAndOnlySixSamples`
2. `G001aStandardSetMatchesDomainTileIdentityAndRedFiveRules`
3. `G001bTypeIndexBoundariesAnd34GroupsMatchDomain`
4. `G001cTileIdRoundTripAndIllegalInputsMatch`
5. `G002ZoneOwnerSemanticsMapToImmutablePositions`
6. `G003aExportedWallsDriveDomainMapAndConservationWithoutRngClaim`
7. `G003bStandaloneDeletionProbeIsExportedAndCsharpRejectsMissingPlacements`
8. `G003bBoundLedgerMismatchProbeIsExportedAndCsharpMapIsSingleLedger`
9. `G003bDuplicateKeyBranchIsDocumentedAsNotConstructible`

### 4.1 V4 mutable pool vs C# immutable map

| V4 | C# | 如何验证 |
|---|---|---|
| `TilePool.tileMap` 可 `setLocation`/反射删除，容器与账本可分离 | `TilePositionMap` 的 positions 数组就是唯一账本；无独立 mutable ledger | 构造期 `BuildDense` 拒绝 <136、重复 tile、重复 position；实例只读 |
| G-003b A：删除一个键后审计拒绝 135 | 135 项 `FromPlacements` 抛 `ArgumentException`（exactly 136） | 直接最小复现 |
| G-003b A duplicate-key | 重复 tile → `Duplicate physical tile`；重复 position → `occupied by both` | 直接最小复现 |
| G-003b B：池账本与实际容器不一致 | 结构上不存在“第二本账”；`Validate(map)` 是唯一审计 | 同 placements 重建 map，`StateHash`/每 tile 位置一致；`AsPlacements()` 为只读集合，`Add` 抛 `NotSupportedException` |
| 反射/mutable 损坏注入 | **NOT_APPLICABLE** | 不把 C# 构造成本较低的 invariant 测试写成对 V4 反射探针的等价 PASS |

结论口径：

- V4 的两个“坏审计”探针本身：**PASS（真实运行）**。
- C# 对 G-003b 的对应 invariant：**PASS（结构映射）**。
- C# 对“反射删除一个 mutable 键/池账本失配”的异常等价：**NOT_APPLICABLE**，因为 C# 没有该可变结构；文档明确，不冒充。

### 4.2 位置/hash 映射

- V4 wall slot **0..121 = WALL**，**122..135 = DEAD_WALL**；C# `TilePosition.Wall(0..121)`、`DeadWall(0..13)`。G-003a 对照时 DeadWall index 使用 `slot - 122`。
- V4 `Zone` 枚举顺序与 C# `TileZone` 不同；JSON 用名字（`WALL`/`DEAD_WALL`/…）对照，不用枚举序号。
- V4 `TileSuit` / C# `TileSuit` 通过名字对照；类型顺序按 `typeIndex`，不按 C# 枚举底层序号。
- V4 **没有**跨语言 canonical state hash（`hashCrosswalk.v4CanonicalStateHash = "NOT_AVAILABLE"`）。C# `TileStateHash` v3（FNV-1a，字段流）是 V5/C# 自己的定义，**不是 V4 hash**，不声称等同。
- 本文件额外记录的规范摘要：`SHA-256(ASCII "G-003a|<seed>|" + 136 个 tileId 以逗号连接)`；每例 `wallCanonicalSha256` 由 V4 导出器实际计算，C# 重算并逐例比对。C# map hash 只验证“同一 placements 重建稳定”，不把 C# hash 回填成 V4 expected。

---

## 5. 命令、退出码、原始日志与结果

### 5.1 版本

- Gradle：**9.7.1**（wrapper）
- Gradle Launcher JVM：**25.0.2**（JetBrains s.r.o. 25.0.2+‑15348964‑b329.117；`JAVA_HOME=C:\Program Files\Android\Android Studio\jbr`）
- 项目 Kotlin：**2.4.10**（`gradle.properties`）；Gradle 自带 Kotlin 报告为 2.4.0
- .NET SDK：**8.0.426**；Host 8.0.32；x64
- Domain：`netstandard2.1`，C# 9；测试：`net8.0`，C# 9
- OS：Windows 10.0.19045，win-x64

### 5.2 V4 导出（PASS）

命令：

```powershell
$env:V5_PHYSICAL_GOLDEN_EXPORT='1'
.\gradlew.bat :engine:test --tests "com.majiang.model.V5PhysicalGoldenExporterTest" --rerun-tasks --console=plain
```

实际结果：

- `V5PhysicalGoldenExporterTest > exportPhysicalGoldenSamples() PASSED`
- `BUILD SUCCESSFUL`，**exit 0**
- 输出：`tests/v5-golden/physical-v1.json`，**806,060 bytes**
- 原始日志：`C:\Users\sncdc\AppData\Local\Temp\physical-golden-gradle-export7.log`
- 日志 SHA256：`d8f8b72435b3d3a23470e36b71a8b335da549108c0183b85819add6d1bf95d2c`
- JSON SHA256：`21a3c4c84604cd6e873b0346632d765e232704be988247f3c93d0d6d679160f2`

再生成确定性：

- 相同工作树输入连续导出两次（export6、export7），JSON SHA256 均为 `21a3c4c8…60f2`，**逐字节相同**，exit 0。
- export6 日志 SHA256：`e0ad74aafe3f34d85e1ebd95ba1e75141867614c1c7df1d5309e32f500ab5501`
- 第一次生成（当时 C# 测试文件尚未创建）provenance 的 `gitStatusPorcelain` 必然不同，故其哈希不同；这不是物理内容随机。稳定输入后的两次导出才作为确定性证据。
- 导出器无时钟读取；G-003a 只使用固定 seed 0..31；其他样本无随机。纯值对象显式写 `clockMillis/sessionId/... = "not-applicable"`，未编造 ID。

### 5.3 V4 既有物理针对测试（PASS）

命令：

```powershell
.\gradlew.bat :engine:test `
  --tests "com.majiang.model.RedFiveTest" `
  --tests "com.majiang.model.TileMovementContractTest" `
  --tests "com.majiang.model.V5PhysicalGoldenExporterTest" `
  --tests "com.majiang.v3.ShuffleAlgorithmAcceptanceTest" `
  --tests "com.majiang.v3.ShuffleAtomicDealAcceptanceTest" `
  --tests "com.majiang.rules.RiichiRulesVerificationTest.34维索引正确性" `
  --tests "com.majiang.rules.RiichiRulesVerificationTest.136张牌物理守恒" `
  --rerun-tasks --console=plain
```

实际结果：`BUILD SUCCESSFUL`，**exit 0**。

| 测试类 | tests | failures | errors | skipped |
|---|---:|---:|---:|---:|
| `com.majiang.model.RedFiveTest` | 4 | 0 | 0 | 0 |
| `com.majiang.model.TileMovementContractTest` | 10 | 0 | 0 | 0 |
| `com.majiang.model.V5PhysicalGoldenExporterTest`（inert，无 env） | 1 | 0 | 0 | 0 |
| `com.majiang.rules.RiichiRulesVerificationTest`（仅 2 个物理方法） | 2 | 0 | 0 | 0 |
| `com.majiang.v3.ShuffleAlgorithmAcceptanceTest` | 15 | 0 | 0 | 0 |
| `com.majiang.v3.ShuffleAtomicDealAcceptanceTest` | 1 | 0 | 0 | 0 |
| **合计** | **33** | **0** | **0** | **0** |

XML 目录：`D:\majiang-wt\physical-golden\engine\build\test-results\test\`
原始日志：`C:\Users\sncdc\AppData\Local\Temp\physical-golden-gradle-final-targeted2.log`
日志 SHA256：`0334b89d780c7b75611d364094d943185c7a9f7fae9ac0757be13175d47412fe`

XML SHA256：

- `TEST-com.majiang.model.RedFiveTest.xml`：`2f7ec57b89a459e80e94f4f561d4b54f9c3db3d70cf1bbed156e8592a76ae515`
- `TEST-com.majiang.model.TileMovementContractTest.xml`：`aa8b3d878bb8e192694a4176247e76164d144db5f13b5cc2f41a16cf2c3e4b69`
- `TEST-com.majiang.model.V5PhysicalGoldenExporterTest.xml`：`8ee1f4dad7ce6141d2734612c02fa6f5bf6611199711de71e0661f529edc9987`
- `TEST-com.majiang.rules.RiichiRulesVerificationTest.xml`：`2ce94dc77a3f240bddd45967aaae2a46b7e7d7b2cb8b3dce8bc82528876d0054`
- `TEST-com.majiang.v3.ShuffleAlgorithmAcceptanceTest.xml`：`46e951072c41f7c9fa4dae5ea9e564044b5e4caf502916d24d95f395f9b07a12`
- `TEST-com.majiang.v3.ShuffleAtomicDealAcceptanceTest.xml`：`d936cff139d868a0df69f6a46fb520fbfc8e9999f23475eca509c8bcbe326ee5`

### 5.4 C# Domain.Tests（PASS）

命令：

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
```

实际结果：

- `Passed! - Failed: 0, Passed: 322, Skipped: 0, Total: 322`
- **exit 0**
- 基线引用：313（任务输入/既有记录）；本任务新增 9 个 `[Fact]`，**322 = 313 + 9**；没有删除/跳过既有测试。
- 说明：本次没有在“改动前的 313 状态”单独重跑；313 是任务给定基线，322 是实际运行结果。未把 313 冒充本轮实测。
- 原始日志：`C:\Users\sncdc\AppData\Local\Temp\physical-golden-domain-final2.log`
- 日志 SHA256：`093ca0c3cddab2a196e1432a75d487f75e1d8d12eeaff6933c2e917daca6b469`

XML 生成（同程序集 direct runner；因 `dotnet test` MTP 不注册 xUnit 的 `-result-*`）：

```powershell
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll `
  -noColor -noLogo `
  -result-junit "C:\Users\sncdc\AppData\Local\Temp\physical-golden-evidence\domain-junit.xml" `
  -result-trx   "C:\Users\sncdc\AppData\Local\Temp\physical-golden-evidence\domain.trx" `
  -result-xml   "C:\Users\sncdc\AppData\Local\Temp\physical-golden-evidence\domain-xunit.xml"
```

实际结果：`Total: 322, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，**exit 0**。

- `domain-junit.xml`：322/0/0，SHA256 `df1177335359bf59c93c7ee6461e42c669e6f92114d04e0b764f4dbd5b44e55a`
- `domain.trx`：SHA256 `017beafaeccff8b6e94582ad3dff5bc6b5543e1757c8316e33e0eabd1116ae8d`
- `domain-xunit.xml`：SHA256 `09565d6770ebbf5bd19972e7d9fa22526952824a410bfc2a133a22296ab6f065`
- direct 原始日志：`domain-direct2.log`，SHA256 `5dac593a306ed8cb760a0fa986c2ed8478a2d88e62b9fab1f4bb5266993abf0c`

失败的**工具参数**尝试（必须记录，不当作测试失败）：

```powershell
D:/Unity/dotnet/dotnet.exe test ... -- --result-junit ... --result-trx ...
```

结果：**exit 1**，`Unknown option '--result-junit'` / `Unknown option '--result-trx'`；这是 `dotnet test` MTP 包装器不注册 xUnit reporter 的 CLI 限制，不是测试断言失败。随后改用 direct runner 成功生成 XML。对应日志 `domain-final.log`（evidence 目录），SHA256 `d9707c12934e83b027f88d1010c71e38ee386618508273aaf0681efee8e84741`。

### 5.5 未运行

- 全量 `./gradlew test`：**NOT_RUN**（按要求只跑定向）。
- PG/WSS/CI/远端：**NOT_RUN**。
- Unity/IL2CPP/Android/iOS/真实设备/跨物理网络：**NOT_RUN**。
- G1 全套、G5、V5 发布：**NOT_RUN**。
- 其余 67 个清单条目：**NOT_RUN**。

---

## 6. 证据 SHA256 清单

### 6.1 本任务文件

| 文件 | SHA256 | 备注 |
|---|---|---|
| `tests/v5-golden/physical-v1.json` | `21a3c4c84604cd6e873b0346632d765e232704be988247f3c93d0d6d679160f2` | 806,060 bytes |
| `engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt` | `d1d72e393a3ee455016b15a17252d2ae6b74acd9478c8822dfd3b20acaf968ac` | 与 JSON 内 `exporterSourceSha256` 一致（C# 测试 LF 规范化后重算通过） |
| `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` | `d0d90a3e8275bc3616f352c69be3532bb25b19abafadc490d3be35095cdb5852` | 9 个新 Fact（原始工作树字节 SHA；提交 blob 由 git 计算） |
| `doc/v5-重构/V5-P11-PHYSICAL-GOLDEN.md` | 提交后由 git 计算，见最终报告 | 本文件 |

### 6.2 JSON 内记录的 V4 生产源

| 文件 | SHA256 |
|---|---|
| `engine/src/main/kotlin/com/majiang/model/Tile.kt` | `6657290cc42efa0d3c58893ffd00d92b78e3d871ebfb426194caccd6770072b0` |
| `engine/src/main/kotlin/com/majiang/model/TileLocation.kt` | `e21dc9c47b37706d35bd6dc048ad0d43678d1d6cb447f4bc7f92761397591869` |
| `engine/src/main/kotlin/com/majiang/model/TilePool.kt` | `43b2562949c569122d81434960fb1cb838f20f6425d8fd4dd9c0552d22122f5c` |
| `engine/src/main/kotlin/com/majiang/state/StateInvariants.kt` | `db410243f699c19b944f243346e7dd5c0d9a41f117ecf4e342eea54f5c0d762e` |
| `engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt` | `ee98ace333cdbdc8f76c6b17b8c7134a6cf5a0e5168adf34b4c532afd34b1dd1` |
| `engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt` | `9386871529939dad346639a311dd84cc7645d8a26af9fb566247f7d4267d95a8` |
| `engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt` | `cc4b1f4718e2268a130e967a9921841fdb6a676808188242fa0791042369c0d0` |
| `engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt` | `45fe486e596a38ad696f7900a277886b7f6cc7a103e8717d299e9658cac5da0a` |

`physical-v1.json` 的 `v4Commit` 为 `32f645ec9b134e9ad261a2df89437416b20b3119`；`v4ProductionTreeClean=true`，`trackedModifiedFiles=[]`，`trackedProductionModifiedFiles=[]`。生成时 `worktreeDirty=true` 仅因四个任务文件为 `??`（见 §1），没有任何 tracked 生产文件改动。

源码 hash 算法：`SHA-256` over UTF-8 text，先把 CRLF 规范化为 LF（`sourceHashAlgorithm` 字段）。这样 Windows `core.autocrlf=true` 的 checkout 不会让 C# 侧源码 hash 校验因换行符而假失败；C# 测试使用同一 LF 规范化算法。

---

## 7. 真实性与边界声明

1. **没有手填 expected 哈希**：G-001/G-002 是真实构造/异常调用；G-003a 是真实 `generate` 输出；G-003b 是真实反射/篡改/审计。`wallCanonicalSha256` 由导出器计算，C# 重算比对。
2. **没有把 C# 输出当黄金**：JSON 只由 Kotlin 导出器写；C# 只读。C# hash 不回填。
3. **没有静态读源码冒充行为**：G-001c/G-002/G-003b 是新增真实探针；G-003a 复用 accepted 输入。
4. **没有改生产码/校验器**：未改 `Tile.kt`、`TilePool.kt`、`TileLocation.kt`、`StateInvariants.kt`、`Domain`、csproj、CI、actor、Wire；未增加 skip/放宽校验。
5. **没有扩 scope**：只 6 项；其余 67 项未审；没有声称 73/73 或 G1 通过。
6. **没有平台/真机/网络证据**：不得声称 Unity、IL2CPP、Android、iOS、真实 WSS/PG、跨物理网络或 CI 已测。
7. **未 push/建 PR/触发远端 CI**：本提交只在本地工作树，返回 caller 做唯一 PR19 集成与 merge-back。
8. **未接管/修改兄弟线程**：未使用在途源码，未改其他工作树，未改主清单，未改 Unity environment/Resources/Burst。
9. **P3.2 未完整接线、P3.4 受门控、P4.1/4.2/P5.1/5.2 受 G1 门控**：本任务不改变这些状态。

### 7.1 逐项状态

| 状态 | 条目 |
|---|---|
| **PASS** | V4 六项导出；V4 33 个定向测试；C# 322 个 Domain 测试；9 个新差分；同输入再生成一致 |
| **FAIL** | 仅一次工具参数尝试：`dotnet test -- --result-junit/--result-trx` exit 1（unknown option），已改用 direct runner；非测试失败 |
| **BLOCKED** | 无（本任务六项均完成）；G1/全 73/平台验证不是本任务可完成项 |
| **NOT_RUN** | 全量 Gradle；PG/WSS/CI/远端；Unity/IL2CPP/真机/弱网；G1/G5；其余 67 项 |
| **NOT_APPLICABLE** | C# 对 V4 mutable pool 反射损坏/池账本失配的异常等价；C# 无该结构，已用结构 invariant 替代并明确 |
| **NOT_CONSTRUCTIBLE** | V4 `MutableMap` duplicate-key 运行时分支；已用实际覆盖观测说明，未伪造异常 |

---

## 8. 给 caller 的集成提示

- 本分支在 `32f645e` 上仅新增 4 个授权文件；可直接 cherry-pick/合并到 PR19 集成批次。
- 合并前建议复核：`git show --stat` 只含这 4 个文件；`physical-v1.json` 的 `exporterSourceSha256` 与 exporter 文件一致；C# 测试源 hash 与文件一致。
- 本文件不修改 `V5-0-GOLDEN-MANIFEST.md`；若后续由有权限的 owner 更新清单，六项应标 `exported`，其余 67 项保持未审计状态，不能把本文件当 73 例完成证据。
- 本任务未修改 P3.2/P3.4/P4/P5 或 CI；PR19 的 P3.2c merge-back 记录不受影响。
