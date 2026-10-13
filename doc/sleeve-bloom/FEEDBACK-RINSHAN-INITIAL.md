# SB-RINSHAN 首轮测试反馈（Codex）

首轮专项35项中34项通过、1项失败：连续四杠测试的第四杠分支收到 NO_ACTIVE_DECISION，而非预期 SLEEVE_BLOOM_WALL_EMPTY。

Codex检查确认这是测试夹具漏同步：前三轮使用 bloom() 帮助函数会调用 syncDecisionsAndClocks，第四杠改为直接提交拒绝测试后漏掉这一调用。实际测试通过 coordinator.handleAction 建立新杠决策，须模拟宿主同步决策时钟再走权威请求。

Codex已在第四杠分支补 a.syncDecisionsAndClocks(1_000, emptySet())；保留全部第四杠拒绝、不扣费、幂等、指针及136实体断言。生产代码不得绕过 NO_ACTIVE_DECISION 门禁。请使用最新测试重跑专项并继续全仓测试/双端构建/GL/文档交付。
