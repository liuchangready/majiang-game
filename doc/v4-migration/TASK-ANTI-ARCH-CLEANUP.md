# 架构清理：测试包名对齐、遗留依赖与边界测试加强

用户授权（2026-10-13）：按架构检视结论执行清理，Anti 开发，Codex 检视验收。这是 R6 后的窄范围卫生清理，不重启迁移任务，不改任何运行行为。

## 开发分工

Anti 独占本任务的源码与测试内容编辑。仅使用内置文件读取/搜索/编辑工具；不要执行 shell/RunCommand、构建、测试、Git 提交、改变权限或委派其它 agent。Codex 独立执行构建/测试与验收，维护 tools/architecture/v4-review（Anti 只读）。不请求用户再次确认。

## 前置状态（Codex 已完成的机械搬移，Anti 不要重复）

Codex 已用 git mv 把 38 个文件移到目标路径并删除全部空目录，但**文件内容（package/import）尚未更新**，当前源码集不可编译是预期状态。Anti 的任务就是按下表完成内容编辑：

| 新路径（相对仓库根） | 原 package | 新 package |
|---|---|---|
| presentation/src/test/kotlin/com/majiang/presentation/game/ 下 24 个文件（原 com/majiang/screen/ 20 个、原 v3/ 4 个，加迁入的 ShufflePreparationUiAcceptanceTest、GameplayLoopRegressionTest、RiichiPreviewContractTest） | com.majiang.screen / com.majiang.v3 / com.majiang / com.majiang.state / com.majiang.session | com.majiang.presentation.game |
| presentation/src/test/kotlin/com/majiang/presentation/testing/ 5 个文件 | com.majiang.testing | com.majiang.presentation.testing |
| presentation/src/test/kotlin/com/majiang/presentation/GdxHeadlessTestRunner.kt | com.majiang | com.majiang.presentation |
| presentation/src/test/kotlin/com/majiang/presentation/menu/MenuScreenTest.kt | com.majiang | com.majiang.presentation.menu |
| presentation/src/test/kotlin/com/majiang/presentation/architecture/ 3 个文件 | com.majiang.state / com.majiang.architecture | com.majiang.presentation.architecture |
| engine/src/main/kotlin/com/majiang/ai/TsumogiriAIStrategy.kt | com.majiang.host.ai | com.majiang.ai |

## 代码范围与契约

1. 按上表逐文件修改 package 声明行，并更新整个 presentation/src/test 源集内的引用 import：
   - `import com.majiang.screen.` → `import com.majiang.presentation.game.`
   - `import com.majiang.v3.` → `import com.majiang.presentation.game.`（如存在）
   - `import com.majiang.GdxHeadlessTestRunner` → `import com.majiang.presentation.GdxHeadlessTestRunner`
   - `import com.majiang.testing.{FakeGameSession,FakeSpectatorSession,FakeTableNavigation,TestUiAssets,TestViewBuilder}` → `import com.majiang.presentation.testing.…`（同名五个类逐个替换）
2. 三个文件需要**额外补充 import**（原先靠同包可见，迁出后必须显式引入；对应 engine 生产包名不变）：
   - MenuScreenTest.kt：补 `import com.majiang.MahjongGame`（多处直接使用 MahjongGame()）。
   - GameplayLoopRegressionTest.kt：补 `import com.majiang.state.*`（原包名与 engine com.majiang.state 相同，正文大量未限定引用 GameState 等）。
   - RiichiPreviewContractTest.kt：补 `import com.majiang.session.*`（同上，正文使用 PlayerView、RiichiPreviewProvider）。
3. **禁止改动** `import com.majiang.testing.TestTableBuilder` 与 `import com.majiang.testing.MeldChoiceFixtures`：它们属于 engine/src/testFixtures/kotlin/com/majiang/testing/（engine 测试夹具包，本次保持原名）。engine testFixtures 本身不动。
4. tools/architecture/BeamSwapLayoutSmoke.java：把其中 `com.majiang.screen.BeamSwapScreenContractTest`、`com.majiang.testing.FakeGameSession`、`com.majiang.testing.FakeTableNavigation` 的 import 同步改为新包名。tools/architecture/v4-review/** 一律不改。
5. engine/src/main/kotlin/com/majiang/ai/TsumogiriAIStrategy.kt：package 行改 com.majiang.ai，删除因此变为同包冗余的 import（如 com.majiang.ai.AIStrategy）；不得改动 AI 决策行为。server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt 第 25 行 import 改为 `import com.majiang.ai.TsumogiriAIStrategy`。
6. client/build.gradle：仅删除第 6 行 `api "com.google.code.gson:gson:2.11.0"`（protocol 已以 api 传递 gson）。不动其他依赖、版本或配置。
7. presentation/src/main/kotlin/com/majiang/MahjongGame.kt：在 render() 中 `server.tick(busy)` 调用前补充注释，说明：单机模式下权威 tick 由 GL 渲染循环驱动；busy 表示 GameScreen 打牌动画回放中，tick(busy) 让本地宿主在回放期间暂停推进，是单机 UX 决策；远端模式权威节奏不受此影响。**只加注释，不改任何逻辑**。
8. 加强 presentation/src/test/kotlin/com/majiang/presentation/architecture/ArchitectureBoundaryContractTest.kt：保留原有 3 个反射测试，新增一个源码扫描测试：
   - 遍历 `src/main/kotlin/com/majiang/presentation` 目录（相对 presentation 模块根）下全部 .kt 文件；
   - 断言不含以下 import：`com.majiang.client.`（前缀 `com.majiang.client.api.` 除外）、`com.majiang.server.`、`com.majiang.transport.`、`com.majiang.network.`、以及 engine 领域包 `com.majiang.{model,state,round,rules,cheat,settlement,timing,stacking,access,ai,application,authority,control,economy,event,host,match,projection,session}.`；
   - 扫描根目录不存在或没有任何 .kt 文件时测试必须失败（防止路径错误静默通过）。

## 禁改清单

- 不删除、不跳过、不弱化任何既有测试断言；测试内容除 package/import/新增项外逐字保留。
- 不动 doc/v4-migration 其他文件、不动 protocol（`com.majiang.server.api` 跨模块包名问题已明确不在本任务范围）、不动 engine testFixtures、不动 Gradle 除 client/build.gradle 第 6 行外的任何构建脚本。
- 工作区含 sleeve-bloom 进行中的未提交改动（约 40 个文件），不属于本任务范围，不得触碰或回退。

## 交付

写入 doc/v4-migration/DELIVERY-ANTI-ARCH-CLEANUP.md：列出全部修改文件、行为变化说明（预期除新增扫描测试外无行为变化）、自检发现的问题、未完成项。完成后退出，等待 Codex 独立构建与测试验收。
