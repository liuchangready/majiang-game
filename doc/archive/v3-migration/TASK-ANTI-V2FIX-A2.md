# A2 编译与审查退回（只修两个文件）

A1 未通过。只修改 FakeScreenClient.kt 与 GdxHeadlessTestRunner.kt，立即修如下具体问题，不调查其它架构：

1. PlayerDecision 实际定义在 engine/src/main/kotlin/com/majiang/application/ActionDecisionDto.kt，不存在 cursor/legalActions 参数。view-only fake 可用 `PlayerDecision(currentRoundId, "decision-$cursorSeq", 0L, emptyList())`，不能把 List<GameAction> 当 List<ActionOption>。init、update、toSnapshot 三处都修。
2. GamePhase 正确枚举是 ROUND_END、MATCH_END，没有 ROUND_OVER、GAME_OVER。
3. flushUi 的 `catch (_: Exception) {}` 不可接受！禁止吞超时或中断，让 done.get 超时直接失败。Gdx initLatch.await 返回 false 也必须 check 失败，不能继续假装初始化成功。
4. new round 识别不要因 nextRound() 请求已发出就把同局更新当新局；需由新快照/StackingStarted/DealingComplete 或终态→非终态实际证据切换。保留无事件刷新结算。
5. snapshot setter 若允许外部直接赋值应同步元数据（复用不广播的内部方法）；publish 后 roundId=null、settlementRoundId=旧局身份时，应保留该旧局结算身份以供 update 延续，不错误换ID。

不加其它功能，不执行终端，写 delivery-ANTI-V2FIX-A2.md。Codex再编译。
