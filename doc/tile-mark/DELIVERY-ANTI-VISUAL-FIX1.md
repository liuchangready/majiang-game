# 落汗图形展示 V1FIX1 生产交付报告 (DELIVERY-ANTI-VISUAL-FIX1.md)

- **任务编号**：V1FIX1
- **开发人员**：Anti (Antigravity)
- **检视验收**：Codex (独立检视与验收)
- **交付状态**：`ready_for_review`

---

## 1. 缺陷修复对照与技术实现

针对 Codex 在真机 GL 验收中指出的 8 项退回问题，已完成全部生产代码改造（仅修改 `presentation/src/main`，未修改任何测试或工具配置）：

| 序号 | 退回缺陷 | 修复策略与生产代码实现 | 涉及文件 |
| :--- | :--- | :--- | :--- |
| **1** | **滚动无法到达北家末张** | 修正 `RiverHintPanel` 滚动方向（向下滚动内容向上平移 `ty = ... + scrollOffsetY`），动态计算真实内容高度 `calculateContentHeight` 与最大滚动偏移 `calculateMaxScroll`，彻底移除固定 400 魔法值。仅牌河缩略网格滚动，手牌行与顶部总数严格固定。 | `RiverHintPanel.kt` |
| **2** | **缺少触摸拖动** | 在 `RiverHintPanel` 中实现 `touchDown`/`touchDragged`/`touchUp`，`GameScreen` 实现对应的 `touchDragged` 与 `touchUp` 输入适配。引入 `DRAG_THRESHOLD = 8f` 防误触阈值；拖拽位移超过 8px 时取消点击并恢复原实体高亮焦点；拖动严格限制在牌河网格内；关闭面板及换局时重置拖拽与滚动状态；滚轮优先检测面板包围盒。 | `RiverHintPanel.kt`<br/>`GameScreen.kt` |
| **3** | **侧栏遮挡牌桌副露/手牌** | 弃用零散魔法数字修补，在 `GameScreen` 引入 `tableCamera`。提示面板展开时，将整个 1280×720 主牌桌统一等比缩放到左侧 840px 区域（缩放比 0.65625，居中偏移 `offsetX = 10f, offsetY = 123.75f`，占用 X: 10..850），侧栏保留在 860..1270，天然保留 10px 安全边距。所有牌桌组件统一使用 `tableCamera.combined` 绘制；主牌桌指针点击与悬停通过统一逆变换 `tableTx = (uiTx - 10) / scale, tableTy = (uiTy - 123.75) / scale` 调度，渲染与命中 100% 保持一致。对家副露、下家副露、各家座位信息、手牌完全无遮挡。 | `GameScreen.kt` |
| **4** | **关闭按钮被面板背景盖住** | 调整 `GameScreen.render` 中的渲染顺序：先绘制 `riverHintPanel.render` 面板主体与网格，再绘制 `riverHintPanel.renderToggleButton`。使按钮在视觉上置顶处于面板右上角 `(1100, 660, 150, 42)`，鼠标与触摸均可清晰看见并再次点击关闭；Esc 键关闭逻辑正常生效。 | `GameScreen.kt` |
| **5** | **寿命面板覆盖上家手牌** | 重构 `TileMarkPanel` 的几何包围盒：`trackingPanelRect` 紧凑化为 `Rectangle(20f, 530f, 185f, 95f)`（宽 185 <= 190，Y 范围 530..625），上家手牌位于 X=245, Y=182..518，留出 40px 横向间距与 12px 纵向间距，彻底杜绝重合。顶部显示组别文本与紧凑翻页按钮，中部 4 张 20x28 标记小牌，底部 4 个寿命圆点，无论提示开闭均不遮挡任何对手手牌。 | `TileMarkPanel.kt` |
| **6** | **侧方落汗牌过窄且未旋转** | 在 `TileSkin` 中新增并在 `WikipediaSkin` 实现 `appendRotatedTileDraw`，左右对手手牌落汗牌面真实旋转 ±90°（左家 -90°，右家 +90°），在 34x24 矩形槽中等比居中饱满呈现，消除了竖向纹理强行压缩问题；保留赤五纹理与半透明细边框。 | `TileSkin.kt`<br/>`GameScreen.kt` |
| **7** | **动画网格一致性** | `RiverHintPanel.render` 引入 `isTileVisibleInRiver` 回调，与主桌共用 `DiscardAnimation.shouldSuppressDiscard` 及 `discardQueue` 暂存过滤门控。打牌飞行动画阶段，提示网格中的目标牌不提前出现，动画结束后与主桌同步落地呈现；不改变权威 `wallRemaining`。 | `RiverHintPanel.kt`<br/>`GameScreen.kt` |
| **8** | **弹窗模态输入与渲染门控** | `isModalActive()` 统一判定：结算弹窗、终局总评、洗牌准备、落汗选牌、立直选牌、副露选牌。模态激活时自动收起并隐藏提示面板及切换按钮；`touchDown` 优先消费模态交互并阻断对底层提示按钮与牌桌的穿透；非模态打开提示面板时倒计时时钟平滑插值不停；精简提示面板内文案，移除冗长说明，仅保留按钮名、方位、数字及牌图形。 | `GameScreen.kt`<br/>`RiverHintPanel.kt` |

---

## 2. 验证执行与退出码

按照任务规范要求，执行了全部 3 组验证命令：

### 2.1 单元与契约测试
```bash
.\gradlew.bat :presentation:test --console=plain
```
- **退出码**：`0`
- **结果**：254 项测试全部通过（包含 `TileMarkScreenContractTest`、`RiverHintPanelSelectionContractTest`、`TileMarkVisualModelsContractTest`、`TileMarkPresentationContractTest` 等）。

### 2.2 真实 GL 冒烟测试（1280x720 基准）
```bash
.\gradlew.bat :lwjgl3:tileMarkHintSmoke :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --continue --console=plain
```
- **退出码**：`0`
- **断言输出**：
  - `TILE_MARK_HINT_GL_PASS fourWinds=true expiry=true wallZero=true escape=true riverFocus=true scroll=true drag=true scaledDiscard=true`
  - `TILE_MARK_VISIBILITY_GL_PASS scenarios=7`

### 2.3 真实 GL 冒烟测试（960x540 缩放变体）
```bash
.\gradlew.bat :lwjgl3:tileMarkHintSmoke :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --continue --console=plain
```
- **退出码**：`0`
- **断言输出**：
  - `TILE_MARK_HINT_GL_PASS fourWinds=true expiry=true wallZero=true escape=true riverFocus=true scroll=true drag=true scaledDiscard=true`
  - `TILE_MARK_VISIBILITY_GL_PASS scenarios=7`

---

## 3. 真实截图复检结论

通过内置图片查看工具逐一检视了生成截图：

1. **`build/tile-mark-visual/screenshots/hint-open.png`**：
   - 主牌桌整体等比缩放在左侧 840px 区域内，右家手牌、右侧牌河与侧栏之间有充裕安全空白，无挤压重叠。
   - 侧栏右上角清晰可见青色“关闭提示”按钮，且与左侧“牌山剩余: 12 张”无重合。
   - 左右对手手牌有效落汗牌面旋转 90 度，纹理清晰饱满。
2. **`build/tile-mark-visual/screenshots/hint-scrolled-bottom.png`**：
   - 牌河网格平滑向上滚动，北家牌河三行共 18 张完整呈现在视口底部，第 18 张末张清晰可见且命中包围盒正确。
   - 顶部手牌展示与牌山剩余计数位置固定，未随牌河滚动发生偏移。
3. **`build/tile-mark/screenshots/visibility-0.png`**：
   - 左上角落汗寿命追踪面板为紧凑图形区（宽 185px），4 张小牌、4 个寿命点及组切换翻页按钮完整显示。
   - 上家（P3）完整的 13 张牌背行顶端与寿命面板之间存在清晰间隔，完全无覆盖。
4. **`build/tile-mark/screenshots/visibility-4-hint.png`**：
   - 上方对家加杠副露（P2 ADDED KAN）、右侧下家碰牌副露（P1 PON）、下方本人大明杠副露（P0 OPEN KAN）及左侧暗杠副露全部完整呈现在缩放后的左区，无任何部分被侧栏覆盖。
   - 所有玩家点数信息、倒计时与主桌按钮均完全可见。
5. **`build/tile-mark/screenshots/visibility-5-hint.png`**：
   - 对家 9 张已揭示落汗手牌与碰牌副露完整呈现，与右侧展开侧栏无遮挡。
6. **`screenshots-scaled/` (960x540 缩放变体)**：
   - 所有元素等比适配正常，无错位或溢出。

---

## 4. 交付约束核验

- [x] 仅修改生产代码（`presentation/src/main`）。
- [x] 未修改任何测试代码（`src/test`）、架构工具脚本（`tools/architecture`）、Gradle 配置或快照启动器。
- [x] 源码中文保持 UTF-8 编码。
- [x] 未进行 git commit，未启动任何外部代理。
- [x] 剩余问题：无。全量最终强制回归与双端构建移交 Codex 独立执行。
