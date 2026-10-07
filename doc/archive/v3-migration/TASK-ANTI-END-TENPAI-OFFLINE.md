# 最后一处定点修复

Codex 已跑 core 全部178测试，177通过，仅一个断线返回路径失败。只修改 GameScreen.kt 一处条件即可，不要调查其他文件、不要读历史文档、不要测试或运行命令。你负责生产编辑。

文件 core/src/main/kotlin/com/majiang/screen/GameScreen.kt，touchDown 中约3160行，在 showFinalResult 模态之后有：

```kotlin
if (!client.isConnected && !isLoading) {
    return true
}
```

此门控应允许 `showSettlement && isMatchFinished` 的终局明细进行本地交互（分页、查看总成绩）。将条件改为：

```kotlin
if (!client.isConnected && !isLoading && !(showSettlement && isMatchFinished)) {
    return true
}
```

更新其注释，说明断线时保留终局本地模态导航。其余代码一律不改。canHumanAgariYame 已含 !isMatchFinished，最终明细路径不会提交新局/游戏动作。精确失败测试是 EndTenpaiScreenAcceptanceTest.disconnected final result still allows navigating local round details：先进入总成绩→断线→本局明细（已通过）→再点查看总成绩（当前失败）。

直接完成该精确编辑，写 delivery-ANTI-END-TENPAI-OFFLINE.md 并返回。不需要用户确认。
