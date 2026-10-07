# 任务 ANTI-V2FIX-D3 交付报告：副露烟测工具迁移 (MeldChoiceRenderSmoke & MeldDisplayRenderSmoke)

- **交付日期**：2026-10-05
- **执行角色**：Antigravity
- **调用链路**：codex -> antigravity
- **任务编号**：ANTI-V2FIX-D3
- **目标设计与任务契约**：
  - [GAME_ARCHITECTURE_V3.md](GAME_ARCHITECTURE_V3.md)
  - [STATUS.md](STATUS.md)
  - [ARCHITECTURE.md](ARCHITECTURE.md)

---

## 1. 唯一修改范围与交付清单

严格遵循任务所有权契约，仅修改授权的 4 个工具文件及本交付报告，未改动任何其他源码与共享 `SmokeScreenClient.java`：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `tools/architecture/MeldChoiceRenderSmoke.java` | 修改 | 1. `PreviewSession` 由 `implements GameSession` 迁为 `extends SmokeScreenClient`，删除旧 `GameSession` 契约方法，保留自有 `update` 并调用 `super.update(v)`；<br>2. 启动由 `game.launchGame(session)` 迁为 `game.changeScreen(new GameScreen(game, session, null))`；<br>3. 适配 V3 UI 回调异步性，将 `session.update` 提前一帧推送到位（frame 11、23、32），使下帧点击在视图就绪后命中，并将确认后断言独立在下一帧（frame 22），完整保留所有 7 张截图、3 次确认、2 次取消与全部断言，总帧数保持 39。 |
| `tools/architecture/MeldDisplayRenderSmoke.java` | 修改 | 1. `PreviewSession` 由 `implements GameSession` 迁为 `extends SmokeScreenClient`，复用 `SmokeScreenClient`；<br>2. 启动由 `game.launchGame(session)` 迁为 `game.changeScreen(new GameScreen(game, session, null))`；<br>3. 移除多余的 Kotlin 接口 import；保持 30 帧、10 张截图与无误打牌断言不变。 |
| `tools/architecture/meld-choice-smoke.init.gradle` | 修改 | `compileMeldChoiceSmoke` 编译任务的 source 增加 `SmokeScreenClient.java`。 |
| `tools/architecture/meld-display-smoke.init.gradle` | 修改 | `compileMeldDisplaySmoke` 编译任务的 source 增加 `SmokeScreenClient.java`。 |
| `doc/v3-migration/delivery-ANTI-V2FIX-D3.md` | 新增 | 本交付报告。 |

---

## 2. 详细迁移内容与技术分析

### 2.1 会话类 `PreviewSession` 继承体系迁移

- 原实现直接 `implements GameSession`，含有已废弃的旧版契约方法（`legalActions()`, `start()`, `submit()`, `nextRound()`, `subscribe()` 及 `SessionUpdate` 总线）。
- 现统一为 `extends SmokeScreenClient`：
  - 构造调用 `super(2, null)`，绑定 viewerSeat 2；
  - 保留测试自有 `update(PlayerView v)`，内部调用 `super.update(v)` 触发快照创建与客户端监听器通知；
  - 动作确认与记录统一走 `SmokeScreenClient` 已有的 `choose(GameAction a)` 与 public 字段 `submitted`；
  - 移除了旧 `GameSession` / `SessionUpdate` / `kotlin.jvm.functions.Function1` / `kotlin.Unit` 依赖。

### 2.2 场景加载与换屏方式对齐 V3

- 将原 `game.launchGame(session)` 替换为标准的 V3 组合根换屏入口：
  ```java
  game.changeScreen(new GameScreen(game, session, null));
  ```
- 确保 `GameScreen` 经由 `GameScreenClient` 异步接口订阅会话更新与发送动作，正确处理生命周期与资源释放。

### 2.3 V3 UI 异步回调与帧调度调和

- **异步机制**：在 V3 `GameScreen` 中，客户端监听器通知通过 `runOnUi { onSnapshotAndEvents(...) }` 派发至 libGDX 主循环（`Gdx.app.postRunnable`），在下一渲染帧开始前实际生效。若在同一帧内同时执行 `session.update(...)` 与 UI 元素点击（如点击吃牌/杠牌按钮），因当帧尚未应用新视图，对应的合法动作集合未刷新，导致按钮未激活或卡片点击穿透未捕获。
- **MeldChoiceRenderSmoke 帧时序重构**：
  - **Frame 11**：调用 `session.update(chi(false))`，快照更新事件排队；
  - **Frame 12**：UI 应用 `chi(false)` 视图，执行 `click(500, 160)` 命中吃牌按钮并展开吃牌候选面板；
  - **Frame 15**：截图 `three-chi-choices.png`，点击卡片选择第 3 个吃牌组合；
  - **Frame 18**：移动鼠标至另一卡片触发 hover；
  - **Frame 21**：截图 `chi-hover-keeps-selection.png`，点击确认提交吃牌动作并重置光标；
  - **Frame 22**：落地等待下一帧验证 `session.submitted.get(1)` 等于选中的 `chosenChi`，防止异步确认未落地导致断言竞争；
  - **Frame 23**：调用 `session.update(ownKan())`，快照更新排队；
  - **Frame 24**：UI 应用 `ownKan()` 视图，执行 `click(350, 160)` 命中杠牌按钮并展开暗杠/加杠面板；
  - **Frame 27**：截图 `closed-and-added-kan.png`，点击卡片选择加杠；
  - **Frame 30**：截图 `added-kan-one-owned-tile.png`，点击确认提交加杠动作；
  - **Frame 32**：调用 `session.update(chi(true))`，快照更新排队；
  - **Frame 33**：UI 应用多页吃牌视图，执行 `click(500, 160)` 展开吃牌面板并 `click(960, 242)` 翻至第 2 页；
  - **Frame 36**：截图 `choice-page-two.png`，点击取消；
  - **Frame 39**：断言 3 次提交数（开杠、吃牌、加杠），打印退出：
    ```
    MELD_CHOICE_RENDER_PASS frames=39 confirmations=3 screenshots=7
    ```

### 2.4 Gradle Init 脚本增强

在 `tools/architecture/meld-choice-smoke.init.gradle` 与 `tools/architecture/meld-display-smoke.init.gradle` 中，为对应的 `JavaCompile` 任务增加 `SmokeScreenClient.java` 作为编译源码：
```groovy
source gradle.rootProject.file('tools/architecture/MeldChoiceRenderSmoke.java')
source gradle.rootProject.file('tools/architecture/SmokeScreenClient.java')
```
确保无单独编译产物时能平稳独立完成 JavaExec 构建与运行。

---

## 3. 验收对照与规范自检

1. **测试意图与断言**：完全保留原所有立直/副露实体 tile 布局、卡片选择、7/10 张截图、取消/确认、输入及回执断言，无任何删除或弱化。
2. **GL 线程安全**：无在 GL 线程中使用 `Thread.sleep` 或阻塞操作，严格使用帧状态驱动。
3. **文件权限与修改范围**：仅修改 4 个指定文件与创建交付报告，未执行任何 shell 命令行。
