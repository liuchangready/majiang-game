# R4B2PUBLICTESTFIX 交付报告

- 交付者：Antigravity (Anti)
- 任务：R4B2 PUBLIC 交付测试装配返修与集合契约修复
- 日期：2026-10-06

---

## 1. 变更文件清单

1. [`engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt`](file:///D:/majiang/engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt)
   - 修复真实领域 API 导入与装配。
   - 修正不可变探测与对称性断言。
   - 新增 `CopyOnReadEventList` 集合对称性、独立投影重建与 `listIterator(index)` 边界断言。
2. [`engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt)
   - 修复 `CopyOnReadEventList.equals` 对普通 `List` 的双向对称性。
   - 修复 `CopyOnReadEventList.listIterator(index)` 越界立即抛出 `IndexOutOfBoundsException` 检查（允许 `0..size`，包含末尾游标）。
3. [`doc/v4-migration/delivery-ANTI-R4B2PUBLICTESTFIX.md`](file:///D:/majiang/doc/v4-migration/delivery-ANTI-R4B2PUBLICTESTFIX.md)
   - 本交付报告。

---

## 2. 修复细节与契约对照

### 2.1 修复 1：领域规则类型与装配导入对齐
- 移除不存在的类导入：`EvaluatedScore`、`PaymentLine`、`WinningLimit`。
- 对齐引入真实领域规则类：`ScoreCalculator`、`FuLine`，以及 `paymentLines: List<String>`。
- 通过 `ScoreCalculator.INSTANCE.calculate(fu = 30, han = 3)` 获取权威得分计算结果 `ScoreCalculator.ScoreResult`。

### 2.2 修复 2：`sampleScoreBreakdown` 构造对齐
- 将 `FuBreakdown` 调整为真实领域构造：
  ```kotlin
  val fuLines = listOf(
      FuLine("底符", 20),
      FuLine("门清加符", 10)
  )
  val fuBreakdown = FuBreakdown(
      lines = fuLines,
      subtotal = 30,
      roundedUpTo = 30,
      finalFu = 30,
      specialNote = null
  )
  ```
- 保持 `uraTiles`、`uraHits`、`totalPoints` 与各测试断言数值（如 4000 点、5600 点）的自洽一致。未新增任何生产同名垫片。

### 2.3 修复 3：不可变集合断言与探针精确化
- 保留 `scores`、`discards`、`melds`、`clocks` 等非空集合的 `UnsupportedOperationException` 抛出断言。
- 对 `proj.settlementEvents` 作为只读实现，使用精确探针检查：
  ```kotlin
  val beforeSize = proj.settlementEvents.size
  val ex = assertThrows<Exception> {
      @Suppress("UNCHECKED_CAST")
      (proj.settlementEvents as MutableList<GameEvent>).clear()
  }
  assertTrue(
      ex is ClassCastException || ex is UnsupportedOperationException,
      "Expected mutation to fail with ClassCastException or UnsupportedOperationException, but got ${ex::class}"
  )
  assertEquals(beforeSize, proj.settlementEvents.size)
  assertEquals(initialEvents, proj.settlementEvents)
  ```
- 拒绝任何篡改，并校验修改前后公开集合内容与长度完全不受污染。

### 2.4 修复 4：保留原测试所有行为与隐私断言
- 保留全部 10 项测试的全部行为覆盖：
  1. 准备阶段与暗牌/牌山信息隐匿
  2. 局中投影与里宝牌隔离
  3. 局间与 state == null 边界
  4. 终局结果与单局历史去私密化（含反射审计）
  5. 荒牌流局嵌套 Set 防御性复制
  6. 事件白名单过滤机制
  7. 里宝牌授权与深层 Breakdown 剥除
  8. 防御拷贝与调用者输入隔离
  9. Getter 返回集合不可变性
  10. CopyOnRead 防御原生数组就地污染
  11. CopyOnReadEventList 值语义与对称性契约

### 2.5 修复 5：`PublicTableProjection.kt` 中 `CopyOnReadEventList` 契约增强
- **`equals` 对称性**：
  - 当 `other is CopyOnReadEventList` 时：逐元素调用 `eventValueEquals(storage[i], other.storage[i])`，支持包含原生数组事件时的值等价，保证独立重建、`copy()` 与 `deepCopy()` 的等价性与 `hashCode()` 一致性。
  - 当 `other` 为普通 `List<*>` 时：逐元素与 `get(i)` 比较。由于含原生数组事件（`ScoreSettled`/`MatchEnded`）时两端各自持有独立深拷贝数组实例，`copyOnRead.equals(normalList)` 与 `normalList.equals(copyOnRead)` 均返回 `false`，彻底消除了原实现中的不对称假阳性；当无数组事件时，双向对称返回 `true`。
- **`listIterator(index)` 边界校验**：
  - 增加即时校验：`if (index < 0 || index > storage.size) throw IndexOutOfBoundsException(...)`。
  - `0` 与 `size`（合法末尾游标）均可正常迭代。
- **测试补充**：
  - 补充同类副本、普通 List（含/不含原生数组事件）的双向对称断言。
  - 补充 `listIterator(0)`、`listIterator(size)`、`listIterator(-1)` 与 `listIterator(size + 1)` 边界断言。
  - 补充独立重建 `PublicTableProjection` 的 `equals` 与 `hashCode` 一致性断言。

---

## 3. 测试与构建声明

**Anti 未运行任何 Gradle 构建、编译、终端命令或测试**。遵守只读与文件修改工具限制，测试编译与验收完全交由监督方 Codex 串行执行。

---

## 4. 未完成项与边界声明

- 无未完成项。
- PUBLIC 范围除授权修复的 `PublicTableProjection.kt` 两个 helper 外其余生产代码保持冻结。
- 未触碰 `tools/architecture/v4-review` 或其他模块生产与测试文件。
