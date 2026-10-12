# 袖里藏花 E3 进度记录 (PROGRESS-ANTI-E3.md)

- **任务标识**：SB-E3
- **日期**：2026-10-12
- **责任人**：Anti (agy)
- **状态**：已完成 (completed / ready_for_review)

## 任务目标
按照 AGENTS.md、doc/sleeve-bloom/DESIGN.md、VISUAL-DESIGN.md、TASK-ANTI-E3.md 与 TESTS-READY-E3.md 规范，完成 E3 表现层（presentation）生产代码实现与集成验证：
1. 模型层：
   - 实现 `com.majiang.presentation.game.SleeveBloomChoiceModel`。
   - 具备只读 `isChoosing`、`selected`、`canActivate`、`canConfirm`、`remainingMillis`（支持动态外推时钟访问）。
   - 包含权威采样、暂停冻结与补偿、代次与候选变化重置、无预选保留第一张与有选保留第二张。
2. 视图与组件：
   - 实现 `com.majiang.presentation.game.SleeveBloomPanel`。
   - 暴露真实绘制/命中矩形：`btnOpenRect` (1080, 100, 140, 44)、`panelRect` (390, 210, 500, 300)、`firstCardRect` (460, 290, 90, 126)、`secondCardRect` (730, 290, 90, 126)、`btnConfirmRect` (450, 225, 180, 44)、`btnCancelRect` (650, 225, 180, 44)。
   - 伴生对象提供 `translateServerReason` 与 `SLEEVE_BLOOM_CHARS` 字体字形全集。
3. 牌桌屏幕接线 (`GameScreen.kt`)：
   - 持有并暴露公共 `sleeveBloomPanel`。
   - 模态 `isModalActive()` 互斥拦截。
   - 右列布局规范与冲突消除：落汗 y150/h44、藏花 y100/h44、理牌 y50/h36、托管 y6/h36（全 x1080/w140）。
   - 补全托管/收回真实可见绘制与点击，禁用藏花不穿透，禁用点击给出原因 toast 解释。
   - 权威超时结算提示保留第一张且额度不退；终端/过时/绑定重置与手动完成不误报超时。
   - ESC 优先作为保留第一张二选一结束，不退出游戏。
   - 异步提交方法 `submitSleeveBloomAsync` 与 `submitSleeveBloomChoiceAsync`，防重入与 toast 提示。
4. 菜单与组装根：
   - `MenuScreen.kt` 秘籍弹窗文案更新，排版不溢出。
   - `MahjongGame.launchGame` 与 `MenuScreen` 建房默认配置启用 `sleeveBloomConfig` 并指定费用为 10。
5. 验证与交付：
   - 运行 `:presentation:test`（覆盖 Codex 新增的 14 项袖里藏花契约测试，全部通过）。
   - 运行各模块 `./gradlew test --rerun-tasks --no-build-cache`。
   - 运行 `:lwjgl3:classes` 与 `:android:assembleDebug`。
   - 输出 `DELIVERY-ANTI-E3.md`、`CALLBACK-ANTI-E3.json`，控制台输出 `CODEX_CALLBACK SB-E3 ready_for_review` 并退出。

## 进度追踪
1. [x] 开工确认与进度文档创建
2. [x] 实现 `SleeveBloomChoiceModel.kt`（支持动态时钟外推、无预选保留第一张、有选保留第二张、代次清空）
3. [x] 实现 `SleeveBloomPanel.kt`（包围盒几何、字形表、卡片高亮、原因翻译）
4. [x] 接线 `GameScreen.kt`（面板挂载、右列布局调整、托管可见绘制、模态拦截、禁用提示、超时 toast、异步提交）
5. [x] 更新 `MenuScreen.kt`（秘籍弹窗文案扩展、单机与自定义建房配置）与 `MahjongGame.kt`（默认启动配置）
6. [x] 运行 `:presentation:test`（Codex 的 14 项 SleeveBloom 契约测试全部通过）
7. [x] 运行全量模块测试、桌面 classes、Android assembleDebug
8. [x] 编写交付文档并回调 Codex
