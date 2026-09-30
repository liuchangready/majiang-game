# R3：修复单局代码的一个编译错误

只允许改 `core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt`，其他任务仍在运行。禁止 shell / 构建 / 测试。尽快完成，写 delivery-R3.md。

编译日志 build/round-session-split/acceptance-first.log：
`RoundStateMachine.kt:113:28 Smart cast to RoundResult is impossible, because result is a property that has an open or custom getter.`

install 中 `if (change.result != null) { this._result = change.result.deepCopy() }` 不能对 custom getter smart cast。先将 change.result 保存为局部 val，再空检查或 let，然后 deepCopy。修复这一处，不修改 API 或其它功能。
