# Anti：E1 局次边界修复

只修改 `engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkModels.kt` 的 `TileMarkGroup` 构造校验。

Codex 独立测试发现创建局次可以为零/负数，`createdRoundOrdinal + 3` 还可溢出，导致不合法标记组被接受。请在验证期限前要求 `createdRoundOrdinal in 1..(Int.MAX_VALUE - 3)`，再保留期限必须等于创建局次加 3 的规则。最大合法创建局次 `Int.MAX_VALUE - 3` 应成功且期限等于 Int.MAX_VALUE。

用户更新执行要求：本任务调用 agy 时加入 `--dangerously-skip-permissions`，允许在仓库执行下述 Gradle 验证命令。测试由 Codex 编写维护，Anti 可以运行但不得修改、删除、跳过测试或弱化断言。不要委派代理、修改全局权限配置或执行 Git 提交。保留其他现有代码和用户改动；不需要重复读取长设计或重新规划。

## 验证、改进与完成通知

1. 先运行 `./gradlew :engine:test --tests com.majiang.cheat.mark.TileMarkModelContractTest --console=plain`，要求 Codex 的 9 项模型测试全部通过。
2. 再运行 `./gradlew :engine:test --console=plain`，要求本轮全部 engine 测试通过。必须核对 XML 报告的总数、失败、错误及跳过数，不能只看 exit code 或缓存提示。
3. 若失败，定位失败原因并在本任务生产代码范围内修复，再运行相关测试及完整 engine 测试。若必须修改既有测试或任务外代码，写出证据交 Codex 处理，不得自行扩大范围或删测。
4. 全部通过后写 `DELIVERY-ANTI-E1FIX1.md`，列出改动、实际命令、退出结果、测试报告计数；将 `PROGRESS-ANTI.md` 更新为 E1_TESTS_PASSED_AWAITING_CODEX。
5. 写 `CALLBACK-ANTI-E1.json`，含 `taskId: "E1FIX1"`、`recipient: "codex"`、`status: "ready_for_review"`、交付文档路径、测试命令及计数。若阻塞，状态写 `blocked` 并给出原因，不能伪称通过。
6. 最后输出 `CODEX_CALLBACK E1FIX1 ready_for_review`（或 blocked）及简短摘要，退出本轮。当前 Codex 通过等待 agy CLI 退出收到这个完成回执，再核对实际差异和测试报告；不得另启一个 Codex 进程冒充通知当前会话。

此通知只代表 Anti 交付，独立验收由 Codex 决定。Codex 若发现问题，Anti 按反馈继续改进，再验证、重新交付并通知；不得自行宣称独立验收通过。
