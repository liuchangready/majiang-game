# A3 单点编译返修

只修 core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt 的 applySnapshot 函数。Codex :core:compileTestKotlin 报194行：String? 不能赋给 String，跨模块 nullable val 未能smartcast。

将 if (snap.roundId != null) ... else if (...) ... 整段替换成：
```kotlin
(snap.roundId ?: snap.settlementRoundId)?.let { currentRoundId = it }
```

不要修改任何其它内容。不要读其他文件或文档，不执行shell。立即保存并写 delivery-ANTI-V2FIX-A3.md。独立新增的 V2CleanupFixtureAcceptanceTest.kt 是 Codex 审查用例，勿动。
