# R5 GL 冒烟工具迁移清单（只读审计）

R4 已接受，R5 三路正在开发；以下是 Codex 拥有的 `tools/architecture` 接线任务，尚未修改或运行。现有 11 组 `*-smoke.init.gradle`：默认入口相关 3 组加专项场景 8 组。

- `DesktopRenderSmoke.java`、`V3RenderSmoke.java`、`RoundSessionRenderSmoke.java` 直接使用 `MahjongGame.launchGame()/getLocalHost()`、旧 `Tile`、`GameScreen.HandTileSlot`、`refreshHandTileRects()`、`ShuffleTarget` 与旧准备面板；R5后改为真实V4菜单/大厅/牌桌入口，保留14张、实体ID出牌、AI续行动及退出释放断言。
- 其余专项工具以 `SmokeScreenClient.java` 为共同旧 `GameScreenClient`/engine DTO阻断点，或依赖 `EndTenpaiScreenAcceptanceTest.Client`；待新 `GameScreen(UiAssets, GameSession, TableNavigation, ...)` 定型后，迁成client.api fake与新的资源/导航门面。保留原真实GL点击、截图、动画、结算和资源断言，不因旧构造器失效直接删除场景。
- 最小真实桌面检查先迁 `desktop-smoke.init.gradle` 与 `DesktopRenderSmoke.java`，运行 `./gradlew -I tools/architecture/desktop-smoke.init.gradle :lwjgl3:architectureRenderSmoke --console=plain`，同时运行默认 `./gradlew :lwjgl3:run` 检查菜单。Windows使用 `./gradlew.bat`。其它8组逐项迁移并运行。R5写入进程退出前不得运行Gradle。
