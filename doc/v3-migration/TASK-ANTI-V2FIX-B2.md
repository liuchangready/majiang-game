# B2 测试真实语义返修

B1 真实 V3 提交方向正确，但新增的3个 Thread.sleep(20)轮询不接受。host本地调用与client默认dispatch均可确定性完成；用有界future+UI FIFO barrier，无需墙钟轮询。只改下面列出的core测试，不改共享Fake/Gdx基类（A2在修），不改产品或engine：

- GameScreenTest：删所有 sleep 轮询，保留全部4个测试和真实物理弃牌验证。
- GameScreenSkinSmokeTest：同样存在 realScreen 返回fake、fake.choose绕过真实引擎；用与GameScreenTest相同真实V3装配。保留两皮肤测试与菜单测试，现布局启动中心640,350（headless坐标）。
- MeldChoiceScreenContractTest、RiichiChoiceScreenContractTest、RiichiScreenIntegrationTest、FuritenScreenContractTest、MeldDisplayContractTest、OpponentMeldDisplayAcceptanceTest：保留原测试/真实算法及守恒断言；输入后需要异步回执复位/模型退出则调用Fake.flushUi。Coordinator装配不能丢 GameEvent：fake.update(view,listOf(event))；不要权威锁内等UI导致互锁，优先动作结果后成批发布。RiichiScreenIntegration的杠→自摸需保留结算事件与terminal字段，不能fake.update只更新view。
- RiichiPacingAcceptanceTest：最后E2E测试真实host每tick至多一步和动画busy门控，禁止清动画凑数、放宽断言。flush要排完嵌套UI。其他coordinator/LocalTableRuntime兼容测试保留（engine批2未开始）。

现有旧XML曾9失败：GameScreen开始、两皮肤、RiichiPacing队列非空、MeldChoice取消后ron NPE/拒绝模型未退出、RiichiChoice拒绝后普通打牌不可用、RiichiIntegration杠后tsumo不能用、DiscardInputGate最后理牌。最后一个将另交A组，不要修改。

只用文件读写，尽快逐文件保存，禁止shell/测试，写delivery-ANTI-V2FIX-B2.md。Codex负责运行后再给具体失败。
