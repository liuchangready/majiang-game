# 任务 A 交付报告：赛程配置、击飞/排名政策与独立出千资源账本

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task A 生产实现)
- **协作模式**：Codex 负责测试设计、构建与检视；Antigravity 负责生产代码实现
- **目标设计文档**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 迁移计划与执行契约](PLAN.md)

---

## 1. 交付变更清单

本次 Task A 交付严格限制在规定的写入范围内，未改动任何测试代码、Gradle 构建脚本、Coordinator、GameScreen，亦未侵入 Task B（timing/control）或 Task C（access/sync/client）的代码范围：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/match/MatchConfig.kt` | 新增 | V3 整场赛程与规则配置，包含契约 11 项核心属性、`validateSupported()` 校验与 `toRuleConfig()` 桥接 |
| `core/src/main/kotlin/com/majiang/match/MatchProgression.kt` | 修改 | `MatchRuleConfig` 扩充默认参数与双向桥接；移除硬编码 magic 3/4 赛程判定；支持 `handsPerWind=1..4`、`winds` 连续场风序列与延长战；实现配置化击飞与 `tieBreakSeats` 同分排序 |
| `core/src/main/kotlin/com/majiang/economy/CheatEconomyConfig.kt` | 新增 | 出千经济配置，包含开关、四家初始点数与千术费用映射 |
| `core/src/main/kotlin/com/majiang/economy/CheatLedgerEntry.kt` | 新增 | 独立出千流水值对象，保存 `entryId`、`seat` (`seatId`)、正负 `amount`、`balanceAfter` 与业务 `reason` |
| `core/src/main/kotlin/com/majiang/economy/CheatLedgerStructures.kt` | 新增 | 出千账本不可变快照 `CheatLedgerSnapshot`、局间交接统计 `CheatLedgerSummary` 与座位收支统计 `CheatSeatStatistics` |
| `core/src/main/kotlin/com/majiang/economy/CheatLedger.kt` | 新增 | 独立出千资源账本实现，满足幂等重试、异常零副作用拒绝、并发线程安全与防御性深拷贝 |
| `doc/v3-migration/delivery-A.md` | 新增 | 本交付报告 |

---

## 2. 契约实现细节与设计落地

### 2.1 com.majiang.match.MatchConfig

1. **属性定义与默认值**：
   - `playerCount: Int = 4`
   - `handsPerWind: Int = 4`
   - `winds: List<Int> = listOf(1, 2)` (1=東, 2=南)
   - `startingScores: List<Int> = List(4) { 25000 }`
   - `allowExtension: Boolean = true`
   - `allowAgariYame: Boolean = true`
   - `bankruptcyEnabled: Boolean = true`
   - `bankruptcyThreshold: Int = 0`
   - `bankruptcyInclusive: Boolean = false`
   - `targetScore: Int = 30000`
   - `tieBreakSeats: List<Int> = listOf(0, 1, 2, 3)`
   - `cheatEconomy: CheatEconomyConfig = CheatEconomyConfig()`
2. **`validateSupported()` 强校验**：
   - 拒绝未实现的三人麻将：断言 `playerCount == 4`（若为 3 或其他数值抛出 `IllegalArgumentException`，明确提示三人规则未就绪）；
   - 赛程合法性：断言 `handsPerWind in 1..4`，支持短赛程；断言 `winds` 非空且必须为 `[1]`（东风战）或 `[1, 2]`（半庄战），严禁不从东起或断续的场风；
   - 分数合法性：断言 `startingScores.size == 4` 且点数均非负；断言 `targetScore >= 0`；
   - 座位唯一性：断言 `tieBreakSeats.size == 4`、无重复座次、座位编号均在 `0..3` 范围内。
   - 返回 `this`，支持流式校验。
3. **`toRuleConfig(): MatchRuleConfig`**：
   - 保持与现有 `MatchProgression` 的无缝兼容桥接，将 `MatchConfig` 的全部配置映射至 `MatchRuleConfig`。

### 2.2 com.majiang.match.MatchProgression 与 MatchRuleConfig

1. **`MatchRuleConfig` 扩充与向后兼容**：
   - 保持前 6 个历史属性与位置不变：`length`, `allowBankruptcy`, `targetScore`, `allowWestExtension`, `awardRemainingRiichiSticksToFirst`, `allowAgariYame`；
   - 新增带有安全默认值的参数：`handsPerWind: Int = 4`, `winds: List<Int> = if (length == MatchLength.TONPUUSEN) listOf(1) else listOf(1, 2)`, `bankruptcyThreshold: Int = 0`, `bankruptcyInclusive: Boolean = false`, `tieBreakSeats: List<Int> = listOf(0, 1, 2, 3)`；
   - 提供 `toMatchConfig()` 反向桥接与 `@JvmOverloads` 构造器，确保既有无头单测与新配置系统互相畅通。
2. **重构赛程推进算法，剔除 Magic Number**：
   - 原代码中固定的 `3`（南四局/西四局）全部替换为 `config.handsPerWind - 1`；
   - 原代码中固定的 `4`（局数进位进风）替换为 `config.handsPerWind`；
   - 终局判定以 `config.winds.last()` 为基准；延长战以 `currentRoundWind > config.winds.last()` 判定，延长战最迟在该延长场风打满 `config.handsPerWind` 局且换庄时终局；
   - 完美支持四人短赛（如每风 1 局、2 局、3 局）。
3. **配置化击飞逻辑**：
   - 提取 `fun isBankrupt(score: Int): Boolean = if (config.bankruptcyInclusive) score <= config.bankruptcyThreshold else score < config.bankruptcyThreshold`；
   - 当 `config.allowBankruptcy == true` 时，按配置比较判定击飞，默认 `threshold=0, inclusive=false` 严格等价于历史 `score < 0`。
4. **同分排名策略**：
   - `computeRankings` 排序器在第一关键字（分数降序）并列时，第二关键字依据 `config.tieBreakSeats.indexOf(seat)` 优先级升序排列，默认 `[0, 1, 2, 3]` 保持起家优先，亦支持自定义座次优先。
5. **重载构造器**：
   - `MatchProgression(matchConfig: MatchConfig)`，可直接基于新配置驱动。

### 2.3 com.majiang.economy 独立出千资源账本

1. **`CheatEconomyConfig`**：
   - `enabled: Boolean = false`，默认关闭；
   - `initialPoints: List<Int> = List(4) { 0 }`，校验大小必须为 4 且点数非负；
   - `costs: Map<String, Int> = emptyMap()`，支持通过 `costFor(techniqueId)` 查询技能点数开销。
2. **`CheatLedgerEntry`**：
   - 不可变数据对象：`entryId`, `seat`, `amount`, `balanceAfter`, `reason`；
   - 提供 `val seatId: Int get() = seat` 兼容别名。
3. **`CheatLedger` 核心行为与安全性保证**：
   - **点数独立性**：出千点数仅在账本内部闭环管理，绝不影响麻将分数与供托；
   - **幂等性与重试**：
     - 若 `entryId` 已存在且入参载荷完全一致（`seat == existing.seat && amount == existing.amount && reason == existing.reason`），直接返回原有 `existing` 条目，无任何额外扣费副作用；
     - 若 `entryId` 相同但参数冲突，抛出 `IllegalArgumentException` 显式拒绝，零副作用；
   - **越界与非法检查**：
     - `seat !in 0..3` 抛出 `IllegalArgumentException` 拒绝，零副作用；
     - `entryId.isNotBlank()` 强校验；
   - **溢出与下溢保护**：
     - 使用 `Long` 计算并检查 `Int.MAX_VALUE` 与 `Int.MIN_VALUE` 边界，溢出抛出 `ArithmeticException`，零副作用；
   - **负余额防御**：
     - 若扣减导致余额小于 0（`nextBalance < 0`），抛出 `IllegalStateException` 拒绝，零副作用；
   - **正负流水与审计原因**：
     - `amount > 0` 为奖励/获得，`amount < 0` 为消耗/处罚，所有变更记录在流水列表中；
   - **并发与不可变防御复制**：
     - 内部使用专属对象锁 `synchronized(lock)` 保护全部读写；
     - `balances()` 与 `entries()` 均返回 `Collections.unmodifiableList(...)` 深拷贝视图，外部无法直接篡改账本状态；
   - **防御性快照与交接结构**：
     - `snapshot(): CheatLedgerSnapshot`：提供轻量状态快照；
     - `summary(): CheatLedgerSummary`：提供包含起始余额、最终余额、各座位净变动 (`totalDeltas`)、完整流水与 `seatStatistics`（获得点数、消耗点数、净增量）的完整单局交接与结算结构；
     - `fromSnapshot(...)`：支持从快照恢复账本状态。

---

## 3. 验收建议与测试指引 (供 Codex 参考)

Codex 在编写验收测试与回归验证时，可重点覆盖以下场景用例：

1. **MatchConfig 校验场景**：
   - `playerCount = 3` 调用 `validateSupported()`，断言抛出 `IllegalArgumentException`；
   - `handsPerWind = 0` 或 `5` 调用 `validateSupported()`，断言抛出 `IllegalArgumentException`；
   - `winds = listOf(2)` 或 `listOf(1, 3)` 调用 `validateSupported()`，断言抛出 `IllegalArgumentException`；
   - `tieBreakSeats = listOf(0, 1, 1, 2)` 或包含负数/4，断言抛出 `IllegalArgumentException`；
   - 默认参数或有效短赛配置调用 `validateSupported()`，断言无异常并通过 `toRuleConfig()` 桥接。
2. **MatchProgression 赛程与排名场景**：
   - 短赛推进：`handsPerWind = 2` 的东风战，东二局闲家和牌，验证直接触发终局 `NORMAL_COMPLETION`；
   - 短赛换风：`handsPerWind = 2` 的半庄战，东二局闲家和牌，验证推进到南一局（`roundWind = 2, roundNumber = 0`）；
   - 击飞比较：
     - `bankruptcyThreshold = 0, bankruptcyInclusive = false` 时，得点为 0 不击飞，-100 击飞；
     - `bankruptcyThreshold = 0, bankruptcyInclusive = true` 时，得点为 0 立即击飞；
     - `bankruptcyThreshold = 1000, bankruptcyInclusive = false` 时，得点为 500 立即击飞；
   - 同分排名：
     - 四人得点相同时，配置 `tieBreakSeats = listOf(2, 3, 0, 1)`，验证最终名次第一名为 seat 2，第二名为 seat 3；
   - 既有测试兼容：
     - 现有 400+ 项涉及 `MatchProgression`、`MatchRuleConfig` 的测试应全部无损通过。
3. **CheatLedger 经济系统场景**：
   - 初始点数：`initialPoints = listOf(100, 50, 0, 20)`，验证 `balances()` 与 `balance(seat)` 准确无误；
   - 扣费成功：seat 0 消耗 30 点（`amount = -30`），验证 `balanceAfter = 70`，`balances()[0] == 70`；
   - 幂等重试：以相同参数再次调用 `apply`，返回原 `CheatLedgerEntry`，余额保持 70，流水条目数量不增加；
   - 冲突拒绝：以相同 `entryId` 但不同 `amount` 调用 `apply`，断言抛出异常，余额与流水无副作用；
   - 负余额拒绝：seat 2（余额 0）消耗 10 点，断言抛出 `IllegalStateException`，余额依然为 0，流水未增加；
   - 越界拒绝：seat 4 或 -1 调用 `apply`，断言抛出 `IllegalArgumentException`；
   - 溢出保护：余额接近 `Int.MAX_VALUE` 时大额增加，断言抛出 `ArithmeticException`；
   - 快照与交接：调用 `summary()`，验证 `startingBalances`、`finalBalances`、`totalDeltas` 及 `seatStatistics` 中 `totalGained` 和 `totalSpent` 计算正确。
