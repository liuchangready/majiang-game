# 袖里藏花 E3FIX1 修复进度记录 (PROGRESS-ANTI-E3FIX1.md)

- **任务标识**：SB-E3FIX1
- **日期**：2026-10-12
- **责任人**：Anti (agy)
- **审查人**：Codex
- **状态**：已完成 (completed / ready_for_review)

## 任务目标与 Codex 检视退回项修复

根据 `TASK-ANTI-E3FIX1.md` 及 Codex 独立复测反馈，针对首轮交付的 5 项问题进行精准修复：

1. **理牌按钮尺寸更正**：
   - 撤销早期 CLI 错误指令 `h36`，恢复权威尺寸 `Rectangle(1080f, 50f, 140f, 44f)`（高度 44f）。
   - 保留 `GameScreenTest` 第 126 行 `height >= 44f` 的合法性，明确不属于夹具缺陷。
   - 纵向布局：落汗 `y150 h44`、藏花 `y100 h44`、理牌 `y50 h44`、托管 `y6 h36`，全在 `x1080 w140`，间距 6/6/8 像素，互不重叠。
2. **超时提示判定完善**：
   - 超时提示（“超时已默认保留第一张，额度不退”）不能仅依靠 `serverNow >= deadlineMillis` 且 offer 消失。
   - 严格核验同 session / round / epoch、有效控制上下文（非 TAKEOVER 接管）、以及原第一张发生权威默认摸切（`lastDiscard` 或 `discards[viewerSeat].last()` 包含 `oldOffer.firstTile`）的权威证据。
   - 支持跨所有席位 clock 提取权威 `serverNow` 最大值。
   - 终局、身份替换、候选失效（`stale-after-deadline`）、手动完成均不发生误报。
3. **双向模态禁用视觉呈现**：
   - 当落汗选牌模态激活时（`tileMarkPanel.model.isSelecting`），袖里藏花入口按钮 `sleeveBloomPanel.btnOpenRect` 呈现灰色禁用视觉（`max(r,g,b) < 160 && max - min < 40`）。
   - 当袖里藏花二选一模态激活时（`sleeveBloomPanel.model.isChoosing`），落汗入口按钮 `tileMarkPanel.btnOpenRect` 呈现灰色禁用视觉。
   - 触摸输入拦截与渲染视觉保持完全同步一致。
4. **候选牌来源标注文字坐标微调**：
   - 解决卡底 `y290` 与按钮顶 `y269` 之间文字被遮挡问题。
   - 将 `font.draw` 的绘制 y 坐标由 `274f` 修正为 `286f`，使“已摸到”和“牌山次张/王牌替补”在 21px 缝隙内完整呈现，不遮挡卡片也不被按钮覆盖。
5. **秘籍弹窗文本换行排版**：
   - 解决 `MenuScreen.kt` 秘籍弹窗第 5 项文本越过面板右边界 `x=1020` 的问题。
   - 保持完整准确的规则说明：“第二候选为牌山次张或杠后王牌替补；未选牌留在或退回对应位置；超时保留原摸牌并摸切，费用不退”，绝不删减任何规则细节。
   - 进行分行排版并调整各行纵向间距，确保警告文本和关闭按钮无重叠。

## 进度追踪

- [x] 1. 检视退回问题定位与测试反例分析
- [x] 2. 生产代码修改：
  - [x] `GameScreen.kt`：`btnSortRect` 调整为 `Rectangle(1080f, 50f, 140f, 44f)`，按钮文本居中；
  - [x] `GameScreen.kt`：`onSnapshotAndEvents` 增加默认摸切第一张与权威 session/control 证据校验；
  - [x] `TileMarkPanel.kt` & `SleeveBloomPanel.kt`：渲染参数增加双向模态互斥标记，禁用时均渲染灰色；
  - [x] `SleeveBloomPanel.kt`：来源提示文字绘制 y 坐标调整为 `286f`；
  - [x] `MenuScreen.kt`：秘籍弹窗第 5 项多行换行排版与间距优化。
- [x] 3. 运行 `:presentation:test` 单元测试：268 项全通（0 失败，0 错误）。
- [x] 4. 运行全仓全量测试：`.\gradlew.bat test --rerun-tasks --no-build-cache --console=plain`：1110 项全通（engine 407, protocol 158, server 141, client 136, presentation 268）。
- [x] 5. 运行平台编译：`.\gradlew.bat :lwjgl3:classes :android:assembleDebug --console=plain` 全通过。
- [x] 6. 运行真实 GL 冒烟测试：
  - [x] 1280 视口冒烟（包含模态互斥像素断言）：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=564` 通过。
  - [x] 960 视口冒烟（包含模态互斥像素断言）：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=583` 通过。
- [x] 7. 编写交付文档 `DELIVERY-ANTI-E3FIX1.md` 与回调数据 `CALLBACK-ANTI-E3FIX1.json`。
- [x] 8. 触发回调通知 Codex 进行独立检视与验收。
