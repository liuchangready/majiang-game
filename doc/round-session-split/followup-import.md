# 编译修复：仅补一个扩展函数 import

只改 `core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`。禁止 shell、构建、测试或其他文件改动，不需要阅读全部设计。S4 已结束。

编译日志 build/round-session-split/regression-second.log：第71、74行调用 GameAction.deepCopy() 未导入扩展函数。
扩展定义在 `com.majiang.application.EventSnapshot.kt`（`internal fun GameAction.deepCopy(): GameAction`）。

请添加正确的 `import com.majiang.application.deepCopy`，不修改其他行为。简短记录 delivery-import.md 后完成。
