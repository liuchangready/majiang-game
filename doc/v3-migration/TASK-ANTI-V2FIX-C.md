# C 可执行 GL 验收工具迁移

只修改 tools/architecture 下 *RenderSmoke.java 与对应 *smoke.init.gradle（包括 helper 新增）。不改 bridge 脚本、不改产品或测试、不执行终端/构建。只用内置文件编辑。无需读历史设计。

GameScreen 现在只有 (MahjongGame,GameScreenClient,TileSkin) 与 GameClient 构造器；MahjongGame 只有无参 launchGame() 和 launchGame(SessionAccess)（测试 fake 用 game.changeScreen(new GameScreen(game, fakeScreenClient, null))）。旧 launchGame(GameSession)/GameScreen(game,skin)/FakeGameSession 均不能再用。GameStateMachine 也已删除，用真实 V3 宿主或 ApplicationCoordinator 测试装配替代。

迁移 DesktopRenderSmoke、RoundSessionRenderSmoke、MeldRenderSmoke、FuritenRenderSmoke、DiscardInputGateRenderSmoke、SettlementDetailRenderSmoke、MeldChoiceRenderSmoke、RiichiChoiceRenderSmoke、MeldDisplayRenderSmoke 的失效调用。已有 V3RenderSmoke、EndTenpaiRenderSmoke 只在实际需要时修改。可以新增仅工具可见的 SmokeScreenClient Java helper 实现 GameScreenClient，使多个 PreviewSession 复用快照/回执转换；不能把兼容入口加回生产。V3 update 必须包含正确 roundId/cursor/terminal settlement，不能只是把接口名替换。

保留每个工具原本截图、输入、精确物理弃牌、振听/结算/动画断言。现存工具在 GL 线程逐帧运行，不能阻塞等待本线程 postRunnable；任何确认回执后的断言移到后续帧，快照更新后下一帧再点击，不清空动画或吞错误。真引擎 smoke 用真实 V3 host 并释放；不将它换成纯 fake。

对应 init.gradle 编译时包含 helper，遵守 Gradle 模块配置锁，不跨项目在任务执行阶段解析另一个项目 runtimeClasspath。可用同项目 detachedConfiguration + 对方 sourceSet.output（参考 end-tenpai-smoke.init.gradle）。不要升级插件/依赖。

写 delivery-ANTI-V2FIX-C.md 列每个工具对应任务和是否迁完。Codex 负责构建及真实GL检查，别自称已通过未运行的测试。
