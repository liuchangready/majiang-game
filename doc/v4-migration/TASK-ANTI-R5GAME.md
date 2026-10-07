# R5 GAME：V4玩家与独立观战牌桌

只在 Codex 接受整个 R4 后派发。先读 CONTRACT.md、TASK-ANTI-R5.md、R5-R6-PREPARATION-AUDIT.md、R5-UI-INTERFACE-PREPARATION.md、R6-TEST-PRESERVATION-INVENTORY.md。用户授权入口替换且不要求旧V3兼容，Anti开发、Codex验收。

独占 `presentation/src/main/kotlin/com/majiang/screen/{GameScreen,MeldSelectionModel,RiichiSelectionModel}.kt`、`presentation/src/main/kotlin/com/majiang/presentation/{ShufflePreparationModel,ShufflePreparationPanel,GameScreenClient,V3GameClientAdapter}.kt`、新 `SpectatorScreen` 及相应game/观战旧画面测试和fake。把玩家牌桌实迁 `com.majiang.presentation.game`，构造仅收 `UiAssets`（根借用）、`GameSession`、`TableNavigation`、可选 `TileSkin`；观战仅 `SpectatorSession/PublicSnapshotView`，不伪造玩家快照。删除旧 `GameScreenClient/V3GameClientAdapter` 生产壳，不保留重载。

玩家动作只提交client.api `ActionChoice` 的 `optionId`/decisionId/controlEpoch，立直/副露/弃牌均按真实物理牌ID；异步回执、出牌FIFO动画、码牌预算/截止、振听与听牌、20+3计时、完整结算/历史/排名/续局及断线可信状态全部迁移。恢复不重播旧弃牌，离房/关闭后旧回调不刷新画面；观战不出现玩家私有按钮或手牌。保留旧UI精确点击/动画/资源/结算断言，迁移到client.api fake；不能靠模型往返测试替代屏幕交互。

不能 import `MahjongGame`、engine、protocol、server 或旧GameClient；不能自行写共用render、MenuScreen、UiAssets/TableNavigation、client.api、平台/Gradle、reviewer。缺展示数据时向Codex报告，由唯一拥有者menu/root补client.api，不私自复制类型或算法。只用内置文件工具，不运行shell/build/test/其它agent/提交。交付 `delivery-ANTI-R5GAME.md` 记录文件、旧行为断言对应位置、未完成项、未运行测试声明。
