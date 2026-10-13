# 架构清理交付简报 (DELIVERY-ANTI-ARCH-CLEANUP.md)

- **执行开发者**：Anti
- **检视与验收者**：Codex
- **交付时间**：2026-10-13
- **执行依据**：`doc/v4-migration/TASK-ANTI-ARCH-CLEANUP.md`

---

## 1. 任务概述与约束执行情况

按任务简报要求，Anti 独占执行源码与测试内容编辑，严格遵守以下约束：
1. **纯文本工具**：仅使用内置文件读取/编辑工具完成所有修改；
2. **禁止执行指令**：未执行任何 shell/RunCommand 命令、Gradle 构建、单元测试、Git 提交或权限变更；
3. **未越权委派**：未调用子代理或委派其他 agent；
4. **保留既有改动**：保留工作区内既有的全部改动（包括 sleeve-bloom 约 40 个未提交文件，以及此前已完成的 testing 包、TsumogiriAIStrategy、client/build.gradle 等）。

---

## 2. 修改文件清单

本次清理涵盖测试包声明对齐、导入引用更新、边界测试加强及依赖与注释核对：

### 2.1 架构契约与边界测试 (presentation/src/test/kotlin/com/majiang/presentation/architecture/)
1. `ArchitectureBoundaryContractTest.kt`
   - `package com.majiang.state` → `package com.majiang.presentation.architecture`
   - 逐字保留原有 3 个反射测试
   - 新增 `presentation source files strictly respect architectural layer boundaries` 源码扫描测试：递归扫描 `src/main/kotlin/com/majiang/presentation` 下全部 `.kt` 文件，校验不存在 `com.majiang.client.`（除 `client.api.` 外）、`com.majiang.server.`、`com.majiang.transport.`、`com.majiang.network.` 以及 engine 19 个领域包（`model, state, round, rules, cheat, settlement, timing, stacking, access, ai, application, authority, control, economy, event, host, match, projection, session`）的非法引用；若目录不存在或无 `.kt` 文件强制失败。
2. `RiichiPacingAcceptanceTest.kt`
   - `package com.majiang.architecture` → `package com.majiang.presentation.architecture`
   - 更新 `GdxHeadlessTestRunner` 与 `presentation.testing` 导入
   - 严格保留 engine 测试夹具导入 `import com.majiang.testing.TestTableBuilder`

### 2.2 对局场景测试 (presentation/src/test/kotlin/com/majiang/presentation/game/) - 共 28 个文件
均将 package 声明更新为 `package com.majiang.presentation.game`，并更新 `GdxHeadlessTestRunner` 与 `presentation.testing.*` 引用：
1. `AsyncScreenAcceptanceTest.kt` (原 `com.majiang.v3`)
2. `BeamSwapChoiceModelContractTest.kt` (原 `com.majiang.screen`)
3. `BeamSwapScreenContractTest.kt` (原 `com.majiang.screen`)
4. `DiscardAnimationIntegrationTest.kt` (原 `com.majiang.screen`)
5. `DiscardInputGateAcceptanceTest.kt` (原 `com.majiang.screen`)
6. `EndTenpaiScreenAcceptanceTest.kt` (原 `com.majiang.v3`)
7. `FuritenScreenContractTest.kt` (原 `com.majiang.screen`)
8. `GameplayLoopRegressionTest.kt` (原 `com.majiang.state`，补充 `import com.majiang.state.*`，保留 `TestTableBuilder`)
9. `GameScreenSessionContractTest.kt` (原 `com.majiang.screen`)
10. `GameScreenTest.kt` (原 `com.majiang.screen`)
11. `MeldChoiceScreenContractTest.kt` (原 `com.majiang.screen`)
12. `MeldSelectionModelContractTest.kt` (原 `com.majiang.screen`)
13. `RiichiChoiceScreenContractTest.kt` (原 `com.majiang.screen`)
14. `RiichiDiscardOrderAcceptanceTest.kt` (原 `com.majiang.screen`)
15. `RiichiPreviewContractTest.kt` (原 `com.majiang.session`，补充 `import com.majiang.session.*`)
16. `RiichiScreenIntegrationTest.kt` (原 `com.majiang.screen`)
17. `RiverHintPanelSelectionContractTest.kt` (原 `com.majiang.screen`)
18. `ScreenRecoveryAcceptanceTest.kt` (原 `com.majiang.v3`)
19. `SettlementDetailPresentationAcceptanceTest.kt` (原 `com.majiang.screen`)
20. `ShufflePreparationUiAcceptanceTest.kt` (原 `com.majiang`)
21. `SleeveBloomChoiceModelContractTest.kt` (原 `com.majiang.screen`)
22. `SleeveBloomScreenContractTest.kt` (原 `com.majiang.screen`)
23. `SpectatorScreenContractTest.kt` (更新 `presentation.testing.*` 导入)
24. `TileMarkPresentationContractTest.kt` (原 `com.majiang.screen`)
25. `TileMarkScreenContractTest.kt` (原 `com.majiang.screen`)
26. `TileMarkSelectionModelContractTest.kt` (原 `com.majiang.screen`)
27. `TileMarkVisualModelsContractTest.kt` (原 `com.majiang.screen`)
28. `V2CleanupFixtureAcceptanceTest.kt` (原 `com.majiang.v3`)

### 2.3 共享展示与渲染测试更新 (presentation/src/test/kotlin/com/majiang/presentation/common/) - 共 9 个文件
同步更新引用已迁出类的 import（主要为 `GdxHeadlessTestRunner` 与 `presentation.testing`）：
1. `common/SettlementDetailRendererTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)
2. `common/render/DoraSparkleTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)
3. `common/render/GameScreenSkinSmokeTest.kt` (`GdxHeadlessTestRunner` 与 `presentation.testing.*`)
4. `common/render/GhostHandTextureTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)
5. `common/render/SkinManagerTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)
6. `common/render/TileDrawBufferTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)
7. `common/render/TileMarkAlphaContractTest.kt` (`GdxHeadlessTestRunner` 与 `presentation.testing.TestViewBuilder`)
8. `common/render/WikipediaSkinMappingTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)
9. `common/render/WikipediaSkinPreviewTest.kt` (`import com.majiang.presentation.GdxHeadlessTestRunner`)

### 2.4 前置已落盘状态核对（确认状态正常、保持未动）
1. `presentation/src/test/kotlin/com/majiang/presentation/testing/` 5 个文件（`FakeGameSession.kt`, `FakeSpectatorSession.kt`, `FakeTableNavigation.kt`, `TestUiAssets.kt`, `TestViewBuilder.kt`）：包名已为 `com.majiang.presentation.testing`，状态正确。
2. `presentation/src/test/kotlin/com/majiang/presentation/GdxHeadlessTestRunner.kt`：包名已为 `com.majiang.presentation`，状态正确。
3. `presentation/src/test/kotlin/com/majiang/presentation/menu/MenuScreenTest.kt`：包名已为 `com.majiang.presentation.menu`，已含 `import com.majiang.MahjongGame`，状态正确。
4. `presentation/src/test/kotlin/com/majiang/presentation/architecture/ClientSelectionAcceptanceTest.kt`：包名与导入已对齐，状态正确。
5. `engine/src/main/kotlin/com/majiang/ai/TsumogiriAIStrategy.kt`：包名已为 `com.majiang.ai`，无冗余同包导入，状态正确。
6. `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`：第 25 行已为 `import com.majiang.ai.TsumogiriAIStrategy`，状态正确。
7. `client/build.gradle`：第 6 行冗余 gson 依赖已清理，状态正确。
8. `presentation/src/main/kotlin/com/majiang/MahjongGame.kt`：`render()` 中 `server.tick(busy)` 权威时钟驱动与动画暂停说明注释已就绪，生产逻辑未修改。
9. `tools/architecture/BeamSwapLayoutSmoke.java`：已引用 `com.majiang.presentation.game.BeamSwapScreenContractTest` 与 `com.majiang.presentation.testing.*`，状态正确。

---

## 3. 行为变化说明

- **生产代码行为**：无任何行为变化。`MahjongGame.kt` 仅有注释，`TsumogiriAIStrategy.kt` 决策逻辑逐字保持。
- **测试代码行为**：
  - 除包名与 import 对齐外，全部原有测试断言、夹具逻辑逐字保留；
  - `ArchitectureBoundaryContractTest` 新增 1 个源码静态扫描测试方法，确保表现层不引入任何非法后端或领域依赖。

---

## 4. 自检发现与处理记录

1. **同包隐式引用断裂补全**：
   - `GameplayLoopRegressionTest` 迁出 `com.majiang.state` 后，显式补充 `import com.majiang.state.*`；
   - `RiichiPreviewContractTest` 迁出 `com.majiang.session` 后，显式补充 `import com.majiang.session.*`。
2. **测试夹具边界保护**：
   - 严格保留 `com.majiang.testing.TestTableBuilder`，未与 `com.majiang.presentation.testing` 混淆。
3. **跨包引用全量更新**：
   - 在全扫描 `presentation/src/test` 源集时，同步更新了 `common` 与 `common.render` 下 9 个用例对 `GdxHeadlessTestRunner` 及 `TestViewBuilder`/`FakeGameSession` 的导入路径，确保整个 presentation 测试源集无遗留旧包引用。

---

## 5. 未完成项

- **无**。所有指定编辑均已按契约完成。请 Codex 独立执行构建与测试验收。
