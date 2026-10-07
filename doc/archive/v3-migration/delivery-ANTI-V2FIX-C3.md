# 任务 ANTI-V2FIX-C3 交付报告：FuritenRenderSmoke 与 RiichiChoiceRenderSmoke 工具迁移至 V3

- **任务编号**：ANTI-V2FIX-C3
- **交付日期**：2026-10-05
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **协作与验收角色**：Codex (Designer, Test Author, Reviewer & Builder)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[STATUS.md](STATUS.md)、[ARCHITECTURE.md](ARCHITECTURE.md)

---

## 1. 交付变更清单

本次交付严格遵守代码所有权边界，仅修改了分配给本任务的文件，未修改任何生产代码、共享辅助类（SmokeScreenClient）、核心引擎或 UI 逻辑：

| 模块 / 路径 | 变更类型 | 说明 |
|---|---|---|
| `tools/architecture/FuritenRenderSmoke.java` | 修改 | 将 `PreviewSession` 改为继承 `SmokeScreenClient`，使用 `GameScreen(game, session, null)` 和 `changeScreen`；移除旧 `GameSession` 契约及事件总线；对非法动作 override `choose` 返回 `REJECTED`；适配 V3 异步 UI 回调，合理安排更新/点击后的落地断言帧；完整保留 5 处截图与全部断言 |
| `tools/architecture/RiichiChoiceRenderSmoke.java` | 修改 | 将 `PreviewSession` 改为继承 `SmokeScreenClient`，使用 `GameScreen(game, session, null)` 和 `changeScreen`；移除旧 `GameSession` 契约及事件总线；对非法动作 override `choose` 返回 `REJECTED`；适配 V3 异步 UI 回调帧步，完整保留 4 处截图与全部断言 |
| `tools/architecture/furiten-smoke.init.gradle` | 修改 | 在 `compileFuritenSmoke` 任务的 `source` 中加入 `SmokeScreenClient.java` |
| `tools/architecture/riichi-choice-smoke.init.gradle` | 修改 | 在 `compileRiichiChoiceSmoke` 任务的 `source` 中加入 `SmokeScreenClient.java` |
| `doc/v3-migration/delivery-ANTI-V2FIX-C3.md` | 新增 | 本交付报告 |

---

## 2. 迁移方案与异步时序适配

### 2.1 PreviewSession 继承 SmokeScreenClient
- 两个测试中的内部静态类 `PreviewSession` 从 `implements GameSession` 改为 `extends SmokeScreenClient`。
- 移除了旧 `GameSession` 契约方法（`getViewerSeat`, `view`, `legalActions`, `start`, `submit`, `nextRound`, `subscribe`, `close`）及其内部手写的订阅者列表与 `SessionUpdate` 构造。
- 复用 `SmokeScreenClient` 已实现的 `getViewerSeat`, `getSnapshot`, `isConnected`, `start`, `nextRound`, `choose`, `addListener`, `close` 以及 `update(PlayerView)`。
- 保留动作合法性裁决：重写 `choose(GameAction a)`，若 `this.view != null && !this.view.getLegalActions().contains(a)` 则返回 `ReceiptStatus.REJECTED`，合法动作委托 `super.choose(a)` 并累加到 `submitted` 列表中。测试中通过 `session.submitted` 访问已提交动作。

### 2.2 启动与场景切换接入 V3
- 将 `game.launchGame(session)` 替换为标准 V3 场景装载调用：
  ```java
  game.changeScreen(new GameScreen(game, session, null));
  screen = (GameScreen) game.getScreen();
  screen.startGame();
  ```
- `changeScreen` 安全释放前置场景，`GameScreen` 在初始化时直接从 `client.snapshot` 获取首帧视图，并通过 `client.addListener` 监听后续增量更新。

### 2.3 Gradle 编译源配置
- 在 `furiten-smoke.init.gradle` 的 `compileFuritenSmoke` 任务中增加：
  ```groovy
  source gradle.rootProject.file('tools/architecture/SmokeScreenClient.java')
  source gradle.rootProject.file('tools/architecture/FuritenRenderSmoke.java')
  ```
- 在 `riichi-choice-smoke.init.gradle` 的 `compileRiichiChoiceSmoke` 任务中增加：
  ```groovy
  source gradle.rootProject.file('tools/architecture/SmokeScreenClient.java')
  source gradle.rootProject.file('tools/architecture/RiichiChoiceRenderSmoke.java')
  ```
- 确保在隔离类目录中联合编译时，`SmokeScreenClient` 正确作为基类解析。

### 2.4 V3 UI 回调异步时序与帧调度 (GL Thread Safety)
- **根因分析**：
  在 V3 架构中，`GameScreen` 的会话监听回调通过 `runOnUi { onSnapshotAndEvents(snap, events) }` 投递至 `Gdx.app.postRunnable`。同理，`submitActionAsync` 完成时的状态重置亦在 `postRunnable` 中运行。
  若在 GL 渲染线程的同一帧内调用 `session.update(...)` 并紧接着断言或点击依赖新视图的按钮，`currentView` 尚未在当帧刷新，会导致点击穿透或断言失败。
- **调度重排**：
  1. **首段（常规听牌与立直选牌确认）**：
     - Frame 3: 点击进入立直选牌模式 (`click(720, 160)`)。
     - Frame 6: 选中 2 索 (`click(767, 80)`)。
     - Frame 9: 捕获截图 `discard-2s-wait-3s-6s.png`。
     - Frame 10: 选中摸到的 6 索 (`click(1000, 80)`)。
     - Frame 13: 捕获截图 `discard-6s-wait-2s-5s.png`；断言在选牌阶段未提前提交动作；设置光标悬停位置。
     - Frame 15: 捕获悬停截图 `hover-2s-selected-6s.png`；点击确认立直 (`click(720, 160)`)；光标复位。
     - Frame 16: 落地断言已提交动作等于 `DeclareRiichi(2, 6s)`。
  2. **第二段（国士无双 13 面听牌与取消立直）**：
     - Frame 18: 会话增量更新 `session.update(orphans())`，触发异步 `postRunnable`。
     - Frame 20: 在新视图落地后点击进入立直选牌模式 (`click(720, 160)`)。
     - Frame 22: 点击摸牌 2 万 (`click(1000, 80)`)。
     - Frame 25: 捕获截图 `orphans-thirteen-waits.png`。
     - Frame 27: 点击取消立直 (`click(1100, 160)`)。
     - Frame 30: 断言取消未新增动作 (`session.submitted.size() == 1`)。在 `RiichiChoiceRenderSmoke` 中在此输出结果并退出。
  3. **第三段（振听自摸结算，仅 FuritenRenderSmoke）**：
     - Frame 30: 会话更新为自摸和牌状态 `session.update(orphans(true))`。
     - Frame 33: 在新状态落地并渲染后，断言 `canHumanTsumo()` 为真且 `canHumanRon()` 为假；捕获截图 `furiten-tsumo-choice.png`；点击自摸按钮 (`click(900, 160)`)。
     - Frame 36: 落地断言已提交第二项动作为 `DeclareTsumo`；输出通过信息并退出游戏。
- 绝不使用 `CompletableFuture.get()` 或在 GL 线程阻塞等待异步任务，完全保持帧驱动。

---

## 3. 验收建议与 Codex 运行指引

Codex 可在无头 GL / 桌面环境中通过对应的 init 脚本执行验收：

1. **Furiten 冒烟测试**：
   ```powershell
   .\gradlew.bat -I tools/architecture/furiten-smoke.init.gradle :lwjgl3:furitenSmoke
   ```
   - 预期输出：`FURITEN_RENDER_PASS frames=36 confirmations=1 tsumo=1 screenshots=5`
   - 预期生成截图（输出目录 `build/furiten/`）：
     - `discard-2s-wait-3s-6s.png`
     - `discard-6s-wait-2s-5s.png`
     - `hover-2s-selected-6s.png`
     - `orphans-thirteen-waits.png`
     - `furiten-tsumo-choice.png`

2. **Riichi Choice 冒烟测试**：
   ```powershell
   .\gradlew.bat -I tools/architecture/riichi-choice-smoke.init.gradle :lwjgl3:riichiChoiceSmoke
   ```
   - 预期输出：`RIICHI_CHOICE_RENDER_PASS frames=30 confirmations=1 screenshots=4`
   - 预期生成截图（输出目录 `build/riichi-choice/`）：
     - `discard-2s-wait-3s-6s.png`
     - `discard-6s-wait-2s-5s.png`
     - `hover-2s-selected-6s.png`
     - `orphans-thirteen-waits.png`

---

## 4. 交付总结

任务 ANTI-V2FIX-C3 已严格按规范完成。`FuritenRenderSmoke` 和 `RiichiChoiceRenderSmoke` 现已完全脱离旧 `GameSession` 接口，无缝接入 V3 `SmokeScreenClient` 与 `GameScreen` 表现层，并正确适配了异步 UI 渲染回调的帧步调度，所有原有断言与截图无一遗漏。
