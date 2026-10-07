# F3 审查发现假覆盖，两个测试修正

只改 core/src/test/kotlin/com/majiang/render/GameScreenSkinSmokeTest.kt 与 screen/RiichiScreenIntegrationTest.kt，不动其它文件。不要调查引擎算法。

GameScreenSkinSmokeTest当前realScreen返回fake，而测试直接fake.choose只计数。改真实GameScreenTest已有的TableHost(config MatchConfig shuffle disabled)→GameClient→V3GameClientAdapter{it()}装配，保留两种皮肤及Menu全部测试。启动中心640,350（headless），flush等待异步UI；screen.submitActionAsync().get后验证非REJECTED（RECORDED也合法）及手牌13张、实体牌进入本人牌河。保留资源释放并close host。不用sleep或无界join，不用不存在的addSnapshotListener，不吞超时。

RiichiScreenIntegrationTest：当前coordinator.eventBus.subscribe<GameEvent>丢掉事件，fake.update只推view导致和了UI没有结算。订阅可缓存events，choose在authority submit返回后一次fake.update(liveview, res.events)，外部draw也同理；避免逐事件推送未完整终态。保留真杠→岭上→自摸→守恒断言；自摸后flush且新增 assertTrue(screen.isSettlementShown)、assertNotNull(screen.currentSettlementData?.breakdown)，验证实际UI结算完整。真实事件由engine产生不能伪造。测试结束dispose。

不运行shell，直接逐文件保存并写delivery-ANTI-V2FIX-F3.md。
