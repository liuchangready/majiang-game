# 交付报告：任务 R3 — 修复 RoundStateMachine 编译错误

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task R3)
- **验收 Agent**：Codex

---

## 1. 任务背景与问题定位

在验收测试与编译阶段（日志 `build/round-session-split/acceptance-first.log`），Kotlin 编译器报告以下错误：
```text
RoundStateMachine.kt:113:28 Smart cast to RoundResult is impossible, because result is a property that has an open or custom getter.
```

### 根本原因
`RoundChange.kt` 中属性 `result` 具有自定义 getter（通过 `get() = _result?.deepCopy()` 实现防御性复制），Kotlin 编译器无法对具有 open 或 custom getter 的属性在 `if (change.result != null)` 分支内进行智能类型转换（Smart Cast）至非空类型 `RoundResult`。

---

## 2. 修改内容

- **修改文件**：[`core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt)
- **修改位置**：`RoundStateMachine.install` 方法中的终局结果同步逻辑。

### 代码变更对比
```kotlin
// 修改前
if (change.result != null) {
    this._result = change.result.deepCopy()
}

// 修改后
val changeResult = change.result
if (changeResult != null) {
    this._result = changeResult.deepCopy()
}
```

将 `change.result` 提取为不可变的局部变量 `val changeResult` 后进行空检查，使编译器可以安全进行 Smart Cast，调用 `deepCopy()`。

---

## 3. 边界说明

1. 严格遵守修改范围限制，仅修改了 `core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt` 该处 Smart Cast 错误。
2. 未改动任何外部 API 契约、方法签名或可见性。
3. 未运行任何 shell/Gradle 构建命令，保持环境纯净，供验收 Agent（Codex）联调与回归。
