# B4 已定位根因，只应用精确修改

只修改三个文件。不要再调查TableHost/引擎算法，Codex已读源码定位，按下列精确修改立即落盘。B3长时间调查无交付，已停止。

1. core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt：
   - 删除当前所有 addSnapshotListener 及dealt/discarded future/订阅等待。GameClient无此API。
   - 点start按钮后调用flushUi；submitActionAsync(action).get(3,SECONDS)后调用flushUi，再读client.snapshot。真实本地host+GameClient默认即时dispatch无需轮询。
   - flushUi实现：val done=CompletableFuture<Unit>(); Gdx.app.postRunnable { Gdx.app.postRunnable { done.complete(Unit) } }; done.get(3,TimeUnit.SECONDS)。不catch异常，不sleep。
   - 回执assertNotEquals(ReceiptStatus.REJECTED,receipt.status)，因为打开响应窗会返回RECORDED；保留13张手牌、牌河+1、实体Tile相等断言。所有4测试保留。
2. screen/MeldChoiceScreenContractTest.kt：stale分支只设置latest=...，删除该分支base.update(latest)，保持silently stale语义；其余断言保留。
3. architecture/RiichiPacingAcceptanceTest.kt 最后一个E2E：
   - 在构造coordinator/host之前建立 `val controllers = listOf(SeatController.RemoteHuman(autoRiichiDiscard = true)) + List(3) { SeatController.Computer(passAndDiscard) }`。
   - coordinator创建后立即 `c.initializeMatch(controllers)`；host使用 `controllers = controllers`。
   - 这是已确定的根因：TableHost:163 `coordinator ?: run{...initialize...}` 跳过外部coordinator初始化；TableHost:1584 resolveAiStrategyForSeat读coordinator.controllerFor，而非传入host的controllers。没初始化时用了默认BasicAI，可能吃碰/和牌提前终局，破坏顺序两圈用例。
   - 原passAndDiscard策略无需擅自变化；不扩展步数上限，不清动画、不放宽断言。两层UI flush可一致化。

只读这三个测试，不读其它文件，禁止shell。完成只写简短delivery-ANTI-V2FIX-B4.md。
