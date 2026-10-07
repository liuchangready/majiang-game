# 任务 ANTI-END-TENPAI-POLISH 交付报告：击飞终局与听牌提示表现层返修

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-END-TENPAI-POLISH 生产代码返修)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、真实 OpenGL 验证与验收审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不使用命令行或外部终端执行）
- **依据契约**：
  - 任务指令：ANTI-END-TENPAI-POLISH
  - 相关生产文件：
    - `core/src/main/kotlin/com/majiang/screen/TenpaiPanelRenderer.kt`
    - `core/src/main/kotlin/com/majiang/screen/FinalResultRenderer.kt`
    - `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`

---

## 1. 唯一修改范围与文件清单

严格限定于指定的 3 个表现层渲染/场景源文件与本交付报告，未改动任何 engine 领域文件、测试代码或 Gradle 构建配置：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/screen/TenpaiPanelRenderer.kt` | 修改 | 面板扩宽至 420px，牌面放大至 32x44、横间隔 36px，每行 7 张；单行高度 126px、双行 172px；底行距底 14px、第二行高 50px（距底 64px）；标题距顶 12px、说明距顶 40px（字号 >= 15px）；增加 glyphLayout 动态约束，彻底解决说明超界与长标题截断问题。 |
| `core/src/main/kotlin/com/majiang/screen/FinalResultRenderer.kt` | 修改 | 显式开启 `GL20.GL_BLEND`；排名行背景改为高对比度深暗色底；席位统一采用“座位1/2/3/4”并对本人标记“[你]”，移除所有猜测的“AI”标签；终局原因规范为“击飞终局 / 正常终局 / 延长战结束 / 庄家终了”，不带括号内枚举代码；移除分数后追加的“[击飞]”假标签，负分仅着色。 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | `touchDown` 中将总成绩模态交互提前至断线门控与托管处理之前，全面吞掉对底层托管与游戏控件的点击穿透，且保证断线态下仍可点击返回菜单或查看明细；整场终局或末局明细禁止发起托管请求；整场终局/总成绩模态/结算态下隐藏底部状态盒与托管按钮；总成绩模态遮罩正确遮挡底层 UI，仅保留顶部返回入口、排名和两个实际按钮。 |
| `doc/v3-migration/delivery-ANTI-END-TENPAI-POLISH.md` | 新增 | 本交付报告 |

---

## 2. 缺陷返修详细说明

### 2.1 TenpaiPanelRenderer 几何与字形适配返修

- **原缺陷**：240px 宽面板导致 19 字中文说明溢出右边框，24x34 牌面过小，标题/说明/牌面拥挤重叠。
- **返修方案**：
  1. `computePanelRect(waitsCount)` 宽度固定为 420px，单行高度 126px，双行（待牌 > 7）高度 172px，保留左下角 `(220f, 195f)` 严格位于手牌 (40..120) 及操作按钮 (145..189) 之上；
  2. 牌面几何调整为 `width = 32f`, `height = 44f`, `spacingX = 36f`，每行上限 7 张（国士无双 13 面自适应为上 7 下 6 两行）；
  3. 牌面 Y 轴坐标：单行或底行距底 14px (`panelRect.y + 14f`)，双行上半行高 50px (`panelRect.y + 14f + 50f = panelRect.y + 64f`)；与上方说明保持充分间隙；
  4. 标题行基线距顶 12px (`panelRect.y + panelRect.height - 12f`)，说明行基线距顶 40px (`panelRect.y + panelRect.height - 40f`)；
  5. 中文字体 scale 调整为 0.85（基础 18px 对应约 15.3px，满足 `>= 15px` 要求），英文字体 scale 调整为 1.0（对应 15px）；
  6. 引入 `glyphLayout` 测量：标题行（原听牌 + 待牌数 + 振听警示）及说明行若测量宽度超出可用宽度（392px），动态按比例缩放，保证普通 2 面、国士 13 面、原听牌+振听长标题绝不越界，说明文字完整呈现。

### 2.2 FinalResultRenderer 渲染对比度与文本规范返修

- **原缺陷**：未显式开启 `GL20.GL_BLEND` 导致名次行背景被 OpenGL 渲染为高亮纯色块，文字对比度极低；其他席位全部硬编码为 AI；中文终局原因展示括号内枚举代码；按 score < 0 擅自添加“击飞”伪标签。
- **返修方案**：
  1. 在 `shape.begin(ShapeRenderer.ShapeType.Filled)` 前显式开启 `Gdx.gl.glEnable(GL20.GL_BLEND)` 与 `glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA)`；
  2. 四人名次行背景采用不透明深暗色底（深暗金 `0.20,0.18,0.08`、深暗银 `0.14,0.18,0.20`、深暗铜 `0.18,0.14,0.10`、深暗红 `0.20,0.10,0.10`），文字对比度提升至极致，金色、银色、铜色及浅红名次与名次文本极其清晰；
  3. 席位名称统一使用 `座位${seat + 1}`，仅本人标记 `[你]`（英文 `Seat ${seat + 1} [YOU]` / `Seat ${seat + 1}`），无权威控制身份时绝不猜测标注为 AI；
  4. 中文终局原因严格对齐枚举映射为“击飞终局”（BANKRUPTCY）、“正常终局”（NORMAL_COMPLETION）、“延长战结束”（WEST_COMPLETION）、“庄家终了”（AGARI_YAME）、“途中流局”（ABORTIVE），不展示括号内枚举；缺失 `finalResult` 时仅显示“比赛已结束”，不伪造原因；
  5. 移除分数末尾由 `score < 0` 衍生的 `[击飞]` / `[BUSTED]` 标签，仅对负分保留醒目浅红文字着色 (`Color(1.0f, 0.45f, 0.45f, 1f)`)。

### 2.3 GameScreen 交互穿透、断线门控与遮罩层级返修

- **原缺陷**：
  1. `touchDown` 中总成绩模态处理位于断线门控与托管处理之后，导致点击可穿透触发托管，且断线后返回主菜单与本局明细失效；
  2. 总成绩弹窗显示时，底层仍绘制底部状态盒与托管按钮；
  3. 已终局的末局明细仍允许点击托管。
- **返修方案**：
  1. **输入顺序重排**：在 `touchDown` 起始（返回主菜单之后）立即将 `showFinalResult` 设为第一优先拦截模态（1.2 阶段）。当总成绩模态开启时，点击“返回主菜单”直接调用 `exitToMenu()`，点击“查看本局明细”切换至 `showSettlement = true; showFinalResult = false`；点击模态外或其它任意区域一律返回 `true` 吞掉输入，绝不穿透至下层的托管、手牌、理牌等任何控件；断线态下这两个模态操作完全可用；
  2. **托管点击门控**：在 1.5 托管处理条件中增加 `!isMatchFinished && !showSettlement`，确保整场已终局、总成绩弹窗或结算明细状态下绝对无法触发托管请求；
  3. **悬停门控**：`isTakeoverHovered` 增加 `!isMatchFinished && !showSettlement && !showFinalResult` 判定，终局或模态下不呈现悬停高亮；
  4. **渲染绘制层级与隐藏**：
     - 在 `render(delta)` 中，底部状态盒 `renderPlayerStatusBox` 与托管按钮 `renderTakeoverButton` 增加 `if (!isMatchFinished && !showFinalResult && !showSettlement)` 保护，整场终局、总成绩弹窗与结算态下完全不绘制；同时在两个方法内部首行增加防御性返回；
     - 在 `renderTopHud` 中，当 `showFinalResult == true` 时，绘制完顶部 `< MENU` 返回按钮后直接 `batch.end(); return`，使得全屏 78% 黑色遮罩正确遮挡底层的桌面与场况信息，视野中仅保留：顶部返回入口（`< MENU`）、总成绩模态内的 4 席排名以及底部的“返回主菜单”和“查看本局明细”两个实际按钮。

---

## 3. 验收确认

本次返修完全满足 5 项缺陷指引要求：
- [x] 1. `TenpaiPanelRenderer` 420px 宽，32x44 牌面，36px 横间隔，每行 7 张，单行高 126px、双行 172px，距底/行高精准，说明文字字号 >= 15px，长标题自适应不越界。
- [x] 2. `FinalResultRenderer` 显式开启 alpha 混合，深暗色高对比度行底，各色名次文字清晰可读。
- [x] 3. 总成绩席位显示“座位1/2/3/4”与“[你]”，不猜 AI；终局原因纯中文无枚举代码；负分着色且无伪造击飞标签。
- [x] 4. `touchDown` 中总成绩模态先于断线门控和托管处理，拦截吞掉所有底层交互；已终局和明细下禁止发托管请求。
- [x] 5. 终局与总成绩时隐藏托管按钮和底部状态盒；模态遮罩正确遮挡底层 UI；保留顶部返回入口、排名和两个实际按钮。
