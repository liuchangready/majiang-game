# C1 小批修复

上一轮 CLI 超时，没有交付。本次只修改 tools/architecture/DesktopRenderSmoke.java、RoundSessionRenderSmoke.java 及其 desktop-smoke.init.gradle、round-session-smoke.init.gradle。最多阅读必要的6文件，不调查算法。

将旧 LocalGameSession / GameScreen(game,skin) / getEndpoint/getSession 调用迁移为真实 V3 宿主客户端。MahjongGame 提供 launchGame() 与 launchGame(SessionAccess)；Screen 提供 (MahjongGame,GameClient,TileSkin)/(MahjongGame,GameScreenClient,TileSkin)。默认入口带洗牌准备；测试若需跳过须在工具自建 host 使用合法 config，不改产品默认。参考 V3RenderSmoke.java。

保留原真实启动、皮肤截图、实体手牌点击、弃牌物理身份/AI 行动/返回菜单释放等所有断言。异步回执和快照需要后续帧才断言，不能在 GL 线程阻塞等待 UI 回调。不得改为假引擎或删掉覆盖。无关其他9个工具本批不动。

不执行 shell/构建，尽快完成并写 delivery-ANTI-V2FIX-C1.md，Codex 编译实跑。
