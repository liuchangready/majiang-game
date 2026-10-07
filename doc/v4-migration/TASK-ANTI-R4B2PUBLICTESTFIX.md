# R4B2 PUBLIC 交付测试装配返修

Codex已读取正式PUBLIC交付并确认原PID36232退出。沿用原PUBLIC会话继续，本任务现在明确授权。PREVIEW仍在另一路开发；允许修改 `engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt`、本任务交付文档，以及下方第5项限定的 `engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt` 集合helper方法，不改其他生产/模块测试、reviewer、构建或设置。不执行shell/build/再委派/提交。当前反馈来自确定的源码API对照，Codex尚未运行共享工程编译。

## 必须修复

1. imports EvaluatedScore、PaymentLine、WinningLimit 不存在。请读取实际 `engine/.../rules/WinningScoreEvaluator.kt`、`ScoreCalculator.kt`、`FuCalculator.kt`，使用真实 `ScoreCalculator.ScoreResult`（可通过 `ScoreCalculator.INSTANCE.calculate`）、`FuLine` 与 `List<String>`。
2. sampleScoreBreakdown中的 `FuBreakdown(base=...,lines=listOf(Pair),total=...)` 不符合现有 `FuBreakdown(lines: List<FuLine>, subtotal, roundedUpTo, finalFu, specialNote?)`，完整修正fixture，并确保计分参数及原断言仍有一致含义。不新增生产同名类迁就测试。
3. gettersReturnUnmodifiableCollections 中 `settlementEvents as MutableList` 对Kotlin只读List实现先抛ClassCastException，这也是正确拒绝修改；用精确接受ClassCastException或UnsupportedOperationException的不可变探针，并确认修改前后公开内容不变。其余非空集合修改断言必须保留，不用宽泛任意Exception吞失败。
4. 全面静态核对本文件构造参数/字段是否与真实领域API一致，保留原10项测试全部行为与隐私、source/getter、值语义断言。不得删除/跳过/弱化测试。
5. 最终只读审查确认两个CopyOnReadEventList集合契约问题，明确授权在PublicTableProjection.kt里只修相关helper而不改字段或脱敏策略：
   - equals对普通List以eventValueEquals内容判true，但普通List反向用GameEvent数组引用equals会false，破坏对称性。需兼顾同CopyOnReadEventList以及PublicTableProjection独立重建/copy/deepCopy的值相等/hash相等；与普通List比较必须双向一致，含数组事件时可以双向false。不能通过暴露内部数组/取消getter复制解决。不要无证据更改已有数组事件类或扩大范围。
   - listIterator(index)需在调用时校验0..size，负数或size+1立即抛IndexOutOfBoundsException；size本身是合法末尾游标。新增/补充模块断言。

PUBLIC其余生产冻结。发现其他确需生产修复先报告具体证据，不能跨范围修改。结束前重新读本任务及FEEDBACK-R4B2PUBLICTESTFIX.md（若有），写 `delivery-ANTI-R4B2PUBLICTESTFIX.md` 列修改与未完成项，并明确未运行构建测试。Codex等待PREVIEW与本任务退出后统一验证，不推进WIRE/R5/R6。
