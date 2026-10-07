# C2 审查返修与剩余工具

Codex 编译 C1 失败：DesktopRenderSmoke.java:103、RoundSessionRenderSmoke.java:89 调用不存在的 Tile.getId()。Tile 是 Kotlin data class，equals 包含 suit/rank/copyIndex；改成 `!discardedTile.equals(clickedTile)` 即为完整物理身份比较。先立即修这两处。

然后迁移余下7个 Java GL 工具：MeldRenderSmoke、FuritenRenderSmoke、DiscardInputGateRenderSmoke、SettlementDetailRenderSmoke、MeldChoiceRenderSmoke、RiichiChoiceRenderSmoke、MeldDisplayRenderSmoke 及对应 init.gradle。只改这些 tools/architecture 文件，可以新增 SmokeScreenClient.java helper。不要读历史文档或引擎内部算法。

原 PreviewSession/FakeGameSession 都是纯展示 fake，替换为实现 GameScreenClient 的 Java helper（参考 interface、PlayerSnapshot 和 PlayerDecision 构造器）。保留它们现有的 update/event发布/submitted 语义；注入 `game.changeScreen(new GameScreen(game, session, null))`，不能 game.launchGame(session)，此重载只支持 SessionAccess。

V3 fake 需要完整快照和递增cursor、settlementRoundId/settlementEvents，特别 SettlementDetail 的单独事件发送不可在下一次无事件刷新清空结算。事件类型按现有源码，不发明API。Java实现 Kotlin有默认函数的接口也要实现 submitShufflePlan 等编译要求（不支持则 REJECTED）。各init.gradle 编译包含 helper。

UI输入/更新回执异步：点击后或update后次帧再断言需要落地的状态，不能在GL线程 join UI Future。保留原每个用例和断言，不清空动画、不吞异常、不删测试场景。不恢复生产V2接口，不修改core或engine，不执行shell/测试。Codex编译实跑。

完成写 delivery-ANTI-V2FIX-C2.md。
