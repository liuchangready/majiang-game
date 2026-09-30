# 独立 UI 提交修复

只允许修改 core/src/main/kotlin/com/majiang/screen/GameScreen.kt 的 submitAction 方法与其说明，不要改其他文件。C3 正在改 client，S3 正在改 application。禁止 shell/构建/改测试。写 delivery-UI.md。

当前 submitAction 存在两个严重问题：
1. option 精确相等匹配失败后对 DiscardTile/DeclareRiichi 用 sameType 回退，可能把赤牌、普通牌、不同 copyIndex 的实体牌混淆。必须严格匹配真实实体牌和完整候选组合；不能同种牌回退。现有 UI 的合法 GameAction 已携带具体实体牌，精确匹配足够。
2. capabilities.decisions=true 时，queryDecision/submitChoice 发生 Throwable 或找不到候选，会退回 endpoint.submit(action)，绕开决策时效机制；若远端实际提交成功但响应异常还可能重复执行。对于支持 decisions 的端点，只能通过 submitChoice；没有匹配候选返回 ActionResult.Rejected(ACTION_NOT_AVAILABLE)。异常可返回明确拒绝/保持待确认，但绝不能第二次提交旧接口。只有 capabilities.decisions=false 的旧测试替身才允许走 submit(action)。不要 catch Throwable 隐藏严重错误；必要时捕获适当异常，但不重试提交。

保持其他按钮行为和旧 FakeGameSession 测试替身工作。不要更改其他任务文件。
