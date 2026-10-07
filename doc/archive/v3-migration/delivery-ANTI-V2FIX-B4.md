# Delivery: ANTI-V2FIX-B4

## 任务目标
按照 Codex 设计与定位的根因，对指定三个测试文件进行精确修改与一致化，不引入额外变更或调查，保留所有测试意图与断言。

## 修改清单

1. [`core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt`](file:///D:/majiang/core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt)
   - 移除了未使用的 `ActionReceipt` 与 `PlayerSnapshot` 导入。
   - `flushUi()` 实现为双层 `Gdx.app.postRunnable` 调度并在 3 秒内 `done.get()`，不捕获异常、不进行 sleep。
   - 开局发牌及打牌交互在交互后均通过 `flushUi()` 排空 UI 队列，再读取权威 `client.snapshot`。
   - 保留所有 4 项测试及全部断言（包含手牌张数物理守恒、本人牌河增加与实体弃牌校验）。

2. [`core/src/test/kotlin/com/majiang/screen/MeldChoiceScreenContractTest.kt`](file:///D:/majiang/core/src/test/kotlin/com/majiang/screen/MeldChoiceScreenContractTest.kt)
   - 确认 stale 分支仅设置 `latest = latest.copy(legalActions=listOf(GameAction.Pass(2)))`，无 `base.update(latest)` 调用，保持 silently stale 语义。
   - 保留所有断言与用例。

3. [`core/src/test/kotlin/com/majiang/architecture/RiichiPacingAcceptanceTest.kt`](file:///D:/majiang/core/src/test/kotlin/com/majiang/architecture/RiichiPacingAcceptanceTest.kt)
   - `flushUi()` 一致化为双层 `Gdx.app.postRunnable` 调度。
   - 在最后一个 E2E 测试中，于构造 coordinator/host 之前统一声明 `controllers`：
     `val controllers = listOf(SeatController.RemoteHuman(autoRiichiDiscard = true)) + List(3) { SeatController.Computer(passAndDiscard) }`
   - coordinator 实例化后立即执行 `c.initializeMatch(controllers)`，修复 TableHost 传入外部 coordinator 时跳过控制器初始化、导致后续策略回退到 BasicAI 产生意外副露/和牌提前终局的缺陷。
   - TableHost 明确传入 `controllers = controllers`。
   - 保留原有步数上限、不放宽断言、不变更 passAndDiscard 策略。
