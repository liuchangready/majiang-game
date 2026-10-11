# Anti: E2 跨局标记状态交付文档

## 1. 任务概述与改动说明

- **任务 ID**: E2
- **目标**: 实现 engine 模块内独立、不可变的跨局标记状态组件 `TileMarkState`，供后续 `MatchSession` 及权威原子调度接入。
- **生产代码实现文件**: [`engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkState.kt`](../../engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkState.kt)
- **实现细节**:
  - `TileMarkState` 类设计：
    - 公开无参构造器 `TileMarkState()` 建立初始空状态（`version = 0L`, `dealtRoundOrdinal = 0`, 空只读集合）。
    - 内部主构造器私有，禁止外部直接构造任意内部状态。
    - 严格只读属性：`version: Long`、`dealtRoundOrdinal: Int`、`dealtRoundIds: List<String>`、`settledRoundIds: Set<String>`、`groups: List<TileMarkGroup>`。
    - 所有集合属性在构造时进行防御性复制并由 `Collections.unmodifiableList` / `Collections.unmodifiableSet` 封装，杜绝任何外部变异与集合外泄。
  - 核心方法契约：
    - `registerDealtRound(roundId: String): TileMarkState`：
      - 拒绝空白/非法 roundId；
      - 若 `roundId in dealtRoundIds` 则直接返回原实例（幂等防倒退与防重复扣减）；
      - 新 roundId 仅允许在初始空状态或上一局已结算（`dealtRoundIds.last() in settledRoundIds`）时登记，局次递增 1、版本号递增 1；未结算时登记新局抛出 `IllegalArgumentException`。
    - `addGroup(group: TileMarkGroup): TileMarkState`：
      - 仅允许在当前已登记且未结算的活跃局创建，严格校验 `group.createdRoundId == currentRoundId` 且 `group.createdRoundOrdinal == dealtRoundOrdinal`；
      - 同组 ID 已存在且内容完全一致时幂等返回原实例；同组 ID 存在但定义不同（不同牌、不同席位等）则抛出 `IllegalArgumentException`；
      - 席位容量校验：单席位最多 3 组且最多 12 张牌，同席位各组实体牌严格互斥不重叠；不同席位允许标记相同物理牌实体；超限或重叠抛出 `IllegalArgumentException`；
      - 成功添加时返回新实例，版本号递增 1。
    - `completeRound(roundId: String): TileMarkState`：
      - 拒绝空白 roundId 及未知 roundId；
      - 若该 roundId 已完成结算，幂等返回原实例；
      - 必须是当前已登记局，结算时将该 roundId 加入 `settledRoundIds`；
      - 自动过滤移除到期组（即满足 `expiresAfterRoundOrdinal <= dealtRoundOrdinal` 的标记组），保留尚未到期的组，版本号递增 1。
    - `groupsForSeat(seat: Int): List<TileMarkGroup>`：
      - 校验席位范围 `seat in 0..3`，非法席位抛出 `IllegalArgumentException`；
      - 返回该席位不可变列表包装视图。
    - `clearSeat(seat: Int): TileMarkState`：
      - 校验席位范围 `seat in 0..3`；
      - 移除该席位所有标记组，保留其他席位组、发牌局次与结算历史；若该席位本无标记组则幂等返回原实例，有实质变化时版本号递增 1。
    - `clearAll(): TileMarkState`：
      - 清除所有席位标记组，保留发牌局次与结算历史；若原本即为空组则幂等返回原实例，有实质变化时版本号递增 1。
    - 覆盖 `equals`、`hashCode` 与 `toString`。不提供破坏不可变约束的任意替换 `copy` 方法。

## 2. 实际验证命令与结果

### 2.1 落汗模块全部测试
- **命令**: `.\gradlew :engine:test --tests "com.majiang.cheat.mark.*" --console=plain`
- **退出状态**: BUILD SUCCESSFUL (退出码 0)
- **用例明细 (共 19 项)**:
  - `TileMarkModelContractTest` (9 项):
    - `feature is opt in for existing engine callers()` - PASSED
    - `group rejects non player owners()` - PASSED
    - `group cannot be created before first deal or with overflowing expiry()` - PASSED
    - `river position uses zero based index for all four seats()` - PASSED
    - `last effective ordinal includes current round and exactly three future rounds()` - PASSED
    - `pending selection cannot change when caller edits its list()` - PASSED
    - `four copies of the same face remain four distinct physical marks including red five()` - PASSED
    - `group rejects incomplete duplicate and out of deck identities()` - PASSED
    - `installed group cannot be retargeted through source or returned collection()` - PASSED
  - `TileMarkStateContractTest` (10 项):
    - `only a new successful deal advances ordinal and old callbacks cannot rewind current round()` - PASSED
    - `mark is active in casting round and all three future rounds until last result accepted()` - PASSED
    - `staggered groups expire independently and release exactly four slots()` - PASSED
    - `capacity is per owner and shared physical targets across owners are allowed()` - PASSED
    - `overlap cannot refresh active marks but reselecting expired tiles is allowed()` - PASSED
    - `group creation requires current registered unsettled round()` - PASSED
    - `duplicate active group is idempotent and conflicting identity is rejected()` - PASSED
    - `completion rejects unknown rounds and replay of old completed round does not change current state()` - PASSED
    - `clearing one occupant preserves other owners and all round accounting()` - PASSED
    - `returned collections cannot mutate snapshots or later transitions()` - PASSED

### 2.2 完整 engine 模块全量测试
- **命令**: `.\gradlew :engine:test --console=plain`
- **退出状态**: BUILD SUCCESSFUL (退出码 0)

## 3. 测试报告 XML 核对结果

已核对 `engine/build/test-results/test/` 下实际生成的 JUnit XML 报告文件：

1. **落汗套件详情**:
   - `TEST-com.majiang.cheat.mark.TileMarkModelContractTest.xml`: tests=9, failures=0, errors=0, skipped=0
   - `TEST-com.majiang.cheat.mark.TileMarkStateContractTest.xml`: tests=10, failures=0, errors=0, skipped=0
   - 落汗测试合计: 19 项，0 失败，0 错误，0 跳过。

2. **全量 engine 模块汇总**:
   - 测试套件数 (Suites): 50
   - 总测试用例数 (Tests): 359
   - 失败数 (Failures): 0
   - 错误数 (Errors): 0
   - 跳过数 (Skipped): 0

## 4. 交付约束遵循情况

- 仅编写了生产代码 `TileMarkState.kt`，未修改、弱化或跳过任何测试。
- 未改动任何非任务代码。
- 未执行 Git 提交。
- 未另起外部代理或委派其他子代理。
- 本批次仅完成不可变跨局状态组件，不宣称整个落汗已全部实现，等待 Codex 独立复核验收。
