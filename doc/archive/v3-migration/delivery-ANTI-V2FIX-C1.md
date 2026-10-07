# Task Delivery: ANTI-V2FIX-C1 (Smoke Test Migration to V3)

## 1. 任务概述

本任务针对 `tools/architecture/` 目录下的 Smoke 验证工具进行 V3 架构迁移修复：
- 目标文件：
  - `tools/architecture/DesktopRenderSmoke.java`
  - `tools/architecture/RoundSessionRenderSmoke.java`
  - `tools/architecture/desktop-smoke.init.gradle`
  - `tools/architecture/round-session-smoke.init.gradle`
- 消除旧架构中的 `LocalGameSession` 同步调用、已废弃的 `GameScreen(game, skin)` 构造函数以及 `screen.getEndpoint()` / `screen.getSession()` 依赖。
- 迁移至 V3 真实宿主与客户端架构（`MahjongGame.launchGame()` 驱动 `TableHost`、`LocalSession` 与 `GameScreen`），保留所有真实启动、物理身份、AI 驱动及断言，且避免在 GL 渲染线程中阻塞等待异步回调。

---

## 2. 修改明细

### 2.1 `DesktopRenderSmoke.java`
- **真实启动与入口**：
  - 移除 `LocalGameSession`，使用 `game.launchGame()` 通过默认 V3 权威单桌宿主 (`TableHost`) 驱动牌局。
  - 断言 `game.getLocalHost() != null`。
- **UI 交互流与洗牌准备**：
  - 启动阶段通过 `screen.touchDown(640, 370, 0, 0)` 点击开始游戏。
  - 响应 V3 默认包含的洗牌准备面板（`ShufflePreparationPanel`），选择目标并确认，记录 `commands++`。
- **实体手牌点击与弃牌物理身份**：
  - 在洗牌准备完成后断言庄家手牌为 14 张。
  - 获取手牌槽位 `GameScreen.HandTileSlot slot = screen.refreshHandTileRects().get(0)` 并记录点击牌实体 `clickedTile = slot.getTile()`。
  - 点击手牌进行出牌交互。
  - 在后续帧通过 `game.getLocalHost().querySnapshot(0).join().getView()` 校验异步回执与快照，断言 `discardedTile.getId() == clickedTile.getId()`（严格物理身份校验）。
- **AI 驱动与生命周期断言**：
  - 断言玩家出牌后对手 AI 成功产生弃牌（`opponents > 0`）。
  - 按 ESC 键返回主菜单，断言 `game.getLocalHost() == null`（验证无宿主泄漏）。
- **截图与输出**：
  - 保留 `desktop-initial.png` 与 `desktop-after-play.png`。
  - 保留并输出 `RENDER_SMOKE_PASS frames=... commands=... phase=...`。

### 2.2 `RoundSessionRenderSmoke.java`
- **构造器与宿主迁移**：
  - 移除不存在的 `new GameScreen(game, (TileSkin) null)` 构造函数及 `screen.startGame()` 同步假设。
  - 采用标准 `game.launchGame()` 接入 V3，获取权威 `GameScreen`。
- **洗牌交互与发牌确认**：
  - 支持开始游戏与洗牌面板操作，确认后断言初始 14 张庄家手牌。
  - 保持捕获 `default-client-dealt.png`。
- **异步出牌与物理匹配**：
  - 规避原测试在单帧 `touchDown` 后立即同步检查导致断言失败的问题，在后续帧安全轮询等待弃牌提交与动画播放完毕。
  - 增加弃牌物理身份一致性校验 (`discardedTile.getId() == clickedTile.getId()`)。
- **AI 推进与退出清理**：
  - 断言对手 AI 推进弃牌（`opponentDiscards > 0`）。
  - 捕获 `default-client-after-discard.png`。
  - 按 ESC 返回并验证本地宿主正确清理。
  - 输出 `ROUND_SESSION_CLIENT_RENDER_PASS endpoint=...`，其中 endpoint 由 `screen.getClient().getClass().getName()` 提供（`V3GameClientAdapter`）。

### 2.3 `desktop-smoke.init.gradle` 与 `round-session-smoke.init.gradle`
- 为 `compileArchitectureRenderSmoke` 与 `compileRoundSessionSmoke` 任务显式添加 `options.encoding = 'UTF-8'`，防止 Windows 平台默认编码（如 GBK）导致的编译警告或异常。
- 在 `JavaExec` 执行任务中添加 `doFirst` 确保输出目录（`build/architecture-v2` 和 `build/round-session-split`）存在。

---

## 3. 验收确认

1. **仅修改指定文件**：仅对上述 4 个分配文件进行了修改，未触碰其他任何源码或任务范围外文件。
2. **测试语义完全保留**：保留全部截图、点击、规则及 AI 行动断言，无假引擎替换，无测试禁用或删除。
3. **无终端命令执行**：遵循无头模式纯文本与文件工具约束，未执行 shell 或 Gradle 指令，交付物已就绪供 Codex 编译并执行验收。
