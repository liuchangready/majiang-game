# SB-RINSHAN：杠后候选仅限岭上牌 交付报告 (DELIVERY-ANTI-RINSHAN.md)

- **任务标识**：SB-RINSHAN
- **责任人**：Anti (agy)
- **审查人**：Codex
- **日期**：2026-10-12
- **状态**：已完成全部生产契约实现并通过 1115 项全量测试、双端构建与 1280/960 视口 GL 冒烟验证 (ready_for_review)

---

## 1. 任务背景与核心规则实现

依据 `doc/sleeve-bloom/TASK-ANTI-RINSHAN.md` 及用户最新规则契约，本次完整实现“杠后候选仅限岭上牌”：

1. **核心规则**：
   - 正常摸牌（`WALL`）路径规则与指针保持不变；
   - 杠后（暗杠、大明杠、加杠）施展袖里藏花时，第一候选固定为当前刚摸到的岭上牌（`drawnTile`），第二候选严格限定为下一张未消费的岭上牌实体（`deadWall[rinshanPointer]`）；
   - 绝不从普通活牌山头尾或王牌替补列表拉取第二候选；
   - 前置校验约束：`rinshanPointer in 1..3`、`totalKanCount < 4`、原摸牌等于刚被消耗的槽位 `deadWall[rinshanPointer - 1]` 且在本人手牌、下一岭上牌实体物理归属于 `TileLocation.DEAD_WALL`；
   - 第四杠后禁止施术：开第四杠后（`totalKanCount >= 4` 或 `rinshanPointer >= 4`）禁止施展袖里藏花，原子扣费 0，统一向客户端/上层返回 `SLEEVE_BLOOM_WALL_EMPTY`。

2. **物理守恒与原语重构 (`swapDrawnWithNextRinshan`)**：
   - 废除并彻底删除旧原语 `swapDrawnWithRinshanReplacement`；
   - 新增并接入 `TileMovement.swapDrawnWithNextRinshan(state, seat)`：
     - 置换本人手牌 `drawnTile` 与王牌中下一张岭上牌 `deadWall[rinshanPointer]`；
     - 原摸牌写回下一岭上槽 `deadWall[rinshanPointer]`，新候选移入玩家手牌并成为 `drawnTile`；
     - `rinshanPointer`、普通牌山指针（`wallPointer`）、牌山剩余张数（`wallRemaining`）、王牌替补列表（`deadWallReplacements`）、宝牌指示牌、开杠次数（`totalKanCount`）与宝牌判定严格保持不变；
     - 严格遵守 136 张物理实体守恒与 14 张有效王牌守恒；下一次补牌时将真实取回未选原牌。

3. **权威校验与协调器同步**：
   - `TableAuthority.submitSleeveBloom`：杠后来源核验 `rinshanPointer in 1..3`、`totalKanCount < 4`、原摸牌归属本人手牌、下一岭上牌在王牌；第四杠直接拒绝；
   - `TableAuthority.submitSleeveBloomChoice`：核验 `offer.secondTile == deadWall[rinshanPointer]`，过期的 offer 拒绝并触发 stale 处理；
   - `ApplicationCoordinator`：同步候选解析与 `swapDrawnWithNextRinshan` 原语调用；保持超时保留第一张并摸切等安全兜底；
   - `SleeveBloomModels.rinshanValid`：私有投影门限对齐为 `rinshanPointer in 1..3 && totalKanCount < 4`。

4. **表现层文案与排版**：
   - 候选标签更新：第二候选卡片上方来源标签由旧「王牌替补」更新为「岭上次张」（正常摸牌为「牌山次张」）；
   - 拒绝原因翻译：`SLEEVE_BLOOM_WALL_EMPTY` 翻译更新为「候选不足（牌山摸完或第四杠不可用）」；
   - 秘籍弹窗排版优化：主菜单「雀圣千术绝技秘籍」弹窗第二/三行缩短为短句，彻底杜绝 760 宽弹窗在 18px 字体下的右侧截断/溢出：
     - 第二行：`   第二候选为牌山次张或岭上次张；第四杠不可用；`
     - 第三行：`   未选牌留在或退回原位；超时保留原牌并摸切，费用不退。`
   - 字形覆盖：`SLEEVE_BLOOM_CHARS` 包含全部新增及调整字形。

---

## 2. 生产代码修改清单

| 修改文件 | 涉及逻辑 |
| :--- | :--- |
| `engine/src/main/kotlin/com/majiang/model/TileMovement.kt` | 删除旧 `swapDrawnWithRinshanReplacement`，实现新 `swapDrawnWithNextRinshan`，原子置换 hand.drawnTile 与 deadWall[rinshanPointer] |
| `engine/src/main/kotlin/com/majiang/cheat/bloom/SleeveBloomModels.kt` | `rinshanValid` 门限由旧条件调整为 `rinshanPointer in 1..3 && totalKanCount < 4` |
| `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` | 激活前置条件与 stale choice 校验对齐 `deadWall[rinshanPointer]` 与第四杠不可用规则 |
| `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 候选解析与 choice 物理操作对接 `swapDrawnWithNextRinshan` |
| `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt` | 候选标签更新为「岭上次张」，原因翻译更新为「候选不足（牌山摸完或第四杠不可用）」，字形表同步扩充 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt` | 秘籍弹窗第 5 条优化为短句分行，彻底解决 760px 宽度弹窗文字溢出风险 |

*注：未修改任何测试文件或 tools 脚本；未修改 `lwjgl3/build.gradle`、`SnapshotLauncher.kt`、`snapshot/`；既有设计/状态/接口/README 文档由 Codex 统一维护。*

---

## 3. 验证执行与测试结果

### 3.1 领域与袖里藏花专项测试
```bash
./gradlew :engine:test --tests "*SleeveBloom*"
```
- **执行结果**：35 项全部通过（0 failures, 0 errors, 0 skipped）。
- **包含内容**：
  - `SleeveBloomWallAndRulesTest`：14 项（含 Codex 补充的第三杠第四岭上槽/宝牌边界、第四杠拒绝不扣费、原牌与次张互换物理守恒等）。
  - `SleeveBloomTableAuthorityTest`：16 项。
  - `SleeveBloomProjectionTest`：5 项。

### 3.2 全仓全量回归与跨平台构建
```bash
./gradlew test :lwjgl3:classes :android:assembleDebug --rerun-tasks --no-build-cache
```
- **执行结果**：BUILD SUCCESSFUL。
- **全仓测试总计**：**1115** 项测试全部重新执行，**100% 通过**（0 failures, 0 errors, 0 skipped）。
- **各模块细分**：
  - `client`：136 tests, 0 failures, 0 errors, 0 skipped
  - `engine`：412 tests, 0 failures, 0 errors, 0 skipped
  - `presentation`：268 tests, 0 failures, 0 errors, 0 skipped
  - `protocol`：158 tests, 0 failures, 0 errors, 0 skipped
  - `server`：141 tests, 0 failures, 0 errors, 0 skipped
- **跨平台构建**：
  - `:lwjgl3:classes`：UP-TO-DATE / SUCCESSFUL
  - `:android:assembleDebug`：SUCCESSFUL

### 3.3 真实 GL 渲染冒烟测试
1. **标准 1280 视口 GL 冒烟**：
   ```bash
   ./gradlew -I tools/architecture/sleeve-bloom-smoke.init.gradle sleeveBloomSmoke
   ```
   - 结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=575`
2. **移动适配 960 视口 GL 冒烟**：
   ```bash
   ./gradlew -I tools/architecture/sleeve-bloom-smoke.init.gradle sleeveBloomSmoke -PsmokeWidth=960
   ```
   - 结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=562`

### 3.4 截图视觉核验
通过真实 GL 渲染生成的截图文件进行了像素与视觉核验：
- `build/sleeve-bloom/screenshots-1280/technique-rules.png`：秘籍弹窗居中渲染，第 5 条新短句排版右侧预留约 40px+ 边距，无任何超宽溢出或截断。
- `build/sleeve-bloom/screenshots-960/technique-rules.png`：在 960 视口下弹窗文字同样完整容纳在背景边框内，排版工整，字形清晰。
- `build/sleeve-bloom/screenshots-1280/offer-0.png`：二选一面板卡片完整渲染，候选标签与倒计时状态正确。

---

## 4. 未验证边界与已知限制

1. **真实物理 Android 触屏交互**：本次已通过 `:android:assembleDebug` 编译通过，但未在连接的 Android 实体机上进行指尖触摸输入测试。
2. **高 DPI 缩放监视器**：当前桌面 GL 冒烟在无缩放环境及 1280/960 虚拟视口验证通过，未在 125%/150% 等 Windows 系统 DPI 缩放显示屏上做物理抓屏。

---

## 5. 结论

`SB-RINSHAN` 全部生产代码与表现层排版已经完工，1115 项全量测试、双端构建与两次视口 GL 冒烟全部通过。已生成专属交付文档与机器可读回调，现提交 Codex 进行独立复审。
