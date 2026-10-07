# Task Delivery: ANTI-V2FIX-A3

## 任务概述
修复 `core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt` 中 `applySnapshot` 函数的 Kotlin 跨模块 `val` 智能类型转换（smartcast）编译报错。

## 变更文件
- [`core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt`](file:///D:/majiang/core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt)

## 修改详情
在 `applySnapshot` 方法中，将原有的判断分支：
```kotlin
if (snap.roundId != null) {
    currentRoundId = snap.roundId
} else if (snap.settlementRoundId != null) {
    currentRoundId = snap.settlementRoundId
}
```
替换为 Elvis safe-call 模式：
```kotlin
(snap.roundId ?: snap.settlementRoundId)?.let { currentRoundId = it }
```
避免了跨模块 nullable property 在 if-branch 无法智能转换（smartcast）为 non-null `String` 赋给 `currentRoundId: String` 的问题。
