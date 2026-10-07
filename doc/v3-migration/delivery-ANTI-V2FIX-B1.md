# Delivery: ANTI-V2FIX-B1

## 任务背景与目标
- 针对 `core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt` 进行小批修复与 V3 迁移；
- 补充 `GameState` 与 `PlayerView` 缺失的包导入；
- 将 `realScreen` 及前 2 个测试（开局发牌、人类打牌交互推进）迁移至真实权威管线：`TableHost` → `GameClient` → `V3GameClientAdapter` → `GameScreen`；
- 打牌测试通过 `screen.submitActionAsync` 提交真实动作，断言手牌物理张数变为 13 张且弃牌真实进入本人牌河（`view.discards[0]`），而不依赖假计数或单项 APPLIED；
- 采用真实回执等待与 UI 队列排空（`flushUi`），避免 UI 线程 join UI 派发的 Future；
- 适配开局按钮现行布局中心坐标 `(640, 350)`，各测试在 `finally`/`use` 中妥善释放 `screen` 与 `host` 资源；
- 保留全部 4 个既有测试用例，未修改其他文件或共享 helper。

## 变更清单
1. **修改文件**：[`core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt`](file:///D:/majiang/core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt)
   - 补齐包导入：`com.majiang.session.PlayerView`、`com.majiang.state.GameState`、`com.badlogic.gdx.Gdx`、`com.majiang.client.GameClient`、`com.majiang.host.TableHost`、`com.majiang.match.MatchConfig`、`com.majiang.presentation.V3GameClientAdapter`、`com.majiang.stacking.ShuffleConfig` 等；
   - 定义 `RealFixture(val screen: GameScreen, val client: GameClient, val host: TableHost) : AutoCloseable`，保证 `use` / `finally` 时自动关闭 `screen.dispose()` 与 `host.close()`；
   - 重构 `realScreen()`，采用 `TableHost(config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)))`，连接席位 0 获取 `SessionAccess`，创建 `GameClient` 并同步完成握手，再通过 `V3GameClientAdapter(client) { it() }` 注入 `GameScreen`；
   - 迁移 `GameScreen点击开始对局正常开局发牌`：点击现布局开局按钮中心 `(640, 350)`，等待手牌发牌落地，校验人类手牌为 14 张且各席位手牌数为 `[14, 13, 13, 13]`；
   - 迁移 `GameScreen人类打牌交互推进流程`：通过 `screen.submitActionAsync(GameAction.DiscardTile(0, tileToDiscard))` 真实提交，获取并校验回执 `ReceiptStatus.APPLIED`，执行 `flushUi()` 排空 UI 任务，严格断言打牌后手牌物理张数由 14 变为 13 张，弃牌进入本人牌河 `viewAfter.discards[0]` 且弃牌数自增 1；
   - 修复后两个理牌测试（`GameScreen理牌开关默认OFF且点击即时切换生效`、`GameScreen理牌开关位置不与返回按钮及开始按钮冲突`）的资源释放，增加 `try { ... } finally { screen.dispose() }` 避免资源泄露。

2. **新建文档**：[`doc/v3-migration/delivery-ANTI-V2FIX-B1.md`](file:///D:/majiang/doc/v3-migration/delivery-ANTI-V2FIX-B1.md)

## 验证与验收指引
- 本任务未执行终端与命令工具；
- 交付 Codex 后可通过以下指令执行回归验证：
  ```bash
  ./gradlew :core:test --tests "com.majiang.screen.GameScreenTest"
  ```
