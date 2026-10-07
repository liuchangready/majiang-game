# D3 两个工具迁移

只改 tools/architecture/MeldChoiceRenderSmoke.java、MeldDisplayRenderSmoke.java 与 meld-choice-smoke.init.gradle、meld-display-smoke.init.gradle。不修改其它文件（共享SmokeScreenClient由其他任务维护）。已有 tools/architecture/SmokeScreenClient.java 实现GameScreenClient，可以继承复用 update/view/submitted/getSnapshot/start/choose。

各PreviewSession implements GameSession迁为extends SmokeScreenClient，删除旧GameSession契约方法及bus（用helper listeners），保留测试自有update场景但需调用super.update实际推送快照。原submitted计数用helper已有public submitted。game.launchGame(session)改 game.changeScreen(new GameScreen(game,session,null))。init.gradle编译加入helper source。

V3 UI回调异步：update后或click后需等待落地的断言放下一帧。保留原所有立直/副露实体tile、卡片选择、截图、取消/确认、输入/回执断言。不能删场景/吞异常/GL线程阻塞等待。最多读这四文件+helper+接口，不读历史架构。不执行shell。写delivery-ANTI-V2FIX-D3.md。
