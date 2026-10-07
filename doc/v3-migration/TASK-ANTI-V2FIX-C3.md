# C3 两个工具迁移

只改 tools/architecture/FuritenRenderSmoke.java、RiichiChoiceRenderSmoke.java 与 furiten-smoke.init.gradle、riichi-choice-smoke.init.gradle。不修改其它文件（共享SmokeScreenClient由其他任务维护）。已有 tools/architecture/SmokeScreenClient.java 实现GameScreenClient，可以继承复用 update/view/submitted/getSnapshot/start/choose。

将各 PreviewSession implements GameSession 改为 extends SmokeScreenClient，删除旧GameSession契约方法及bus（使用helper listeners）；若原测试通过 submitted计数，则用helper已有public submitted。原自定义拒绝选择可override choose返回REJECTED。`game.launchGame(session)` 改为 `game.changeScreen(new GameScreen(game, session, null))`。init.gradle 的JavaCompile加入SmokeScreenClient.java source。

V3 UI回调异步：保留原用例所有截图与断言，更新和点击后的落地断言安排下一个render帧，不能在GL线程等待Future/postRunnable。只迁接口，不删除测试场景，不执行shell。最多读这四文件+helper+接口，不调查其它算法。

完成写delivery-ANTI-V2FIX-C3.md。
