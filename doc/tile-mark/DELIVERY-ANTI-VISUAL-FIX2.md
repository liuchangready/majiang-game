# 落汗提示 V1FIX2 生产交付报告 (DELIVERY-ANTI-VISUAL-FIX2.md)

- **任务编号**：V1FIX2
- **开发人员**：Anti (Antigravity)
- **检视验收**：Codex (独立检视与验收)
- **交付状态**：`ready_for_review`

---

## 1. 缺陷修复与生产代码改造

针对 Codex 在真机视觉验收中发现的 `hint-scrolled-bottom.png` 滚动后“南家 (18)”方位标题与固定“四方牌河”重叠问题，以及 KDoc 注释滞后问题，完成如下精准生产修复：

| 序号 | 问题 | 修复策略与实现 | 涉及文件 |
| :--- | :--- | :--- | :--- |
| **1** | **滚动后方位标题与牌面侵入固定标题** | 明确定义四方牌河网格可见视口上下界：`riverContentTop = riverAreaTop - 36f`，`riverContentBottom = panelRect.y + 10f`。<br/>1) 方位标题绘制严格要求完整落在视口内才绘制：`currentGridY in (riverContentBottom + 18f)..riverContentTop`。滚动上移越过视口顶部的方位标题（如南家、东家）自动不予绘制，彻底消除与“四方牌河”固定标题的重叠；同时底部不绘制出面板界外。<br/>2) 牌面绘制严格要求完全位于网格可见范围内：`ty in riverContentBottom..(riverContentTop - riverTileH)`。牌面顶端不超过 `riverContentTop`，底端不低于 `riverContentBottom`，不侵入固定标题，底部也不画出面板。<br/>3) 完整保留通过的滚动、拖动、北家末张可达性及精确点击。 | `RiverHintPanel.kt` |
| **2** | **TileMarkPanel 顶部 KDoc 描述过时** | 更新顶部 KDoc 契约条目：将过时的“位置追踪”和“对手区域落汗识别”修正为“当前图形寿命区”与“完整对手手牌展示”。同步更新 `render` 函数关于 UI 组件绘制的注释。 | `TileMarkPanel.kt` |

---

## 2. 验证命令与退出码

执行了任务指定的全部验证命令：

### 2.1 单元与契约测试
```bash
.\gradlew.bat :presentation:test --console=plain
```
- **退出码**：`0`
- **结果**：全量 254 项测试全部通过（0 失败，0 错误）。

### 2.2 真实 GL 冒烟测试（1280x720 基准尺寸）
```bash
.\gradlew.bat :lwjgl3:tileMarkHintSmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --console=plain
```
- **退出码**：`0`
- **断言输出**：
  ```
  TILE_MARK_HINT_GL_PASS fourWinds=true expiry=true wallZero=true escape=true riverFocus=true scroll=true drag=true scaledDiscard=true
  ```

### 2.3 真实 GL 冒烟测试（960x540 缩放变体）
```bash
.\gradlew.bat :lwjgl3:tileMarkHintSmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --console=plain
```
- **退出码**：`0`
- **断言输出**：
  ```
  TILE_MARK_HINT_GL_PASS fourWinds=true expiry=true wallZero=true escape=true riverFocus=true scroll=true drag=true scaledDiscard=true
  ```

---

## 3. 真实截图复检结论

通过内置图片查看工具检查了两种尺寸的生成截图：

1. **基准尺寸 `build/tile-mark-visual/screenshots/hint-scrolled-bottom.png` (1280x720)**：
   - 分割线下方的固定标题“四方牌河”清晰独立呈现，上方留白规范；
   - 滚动后向上越过视口的“南家 (18)”方位标题已被完全剔除，与固定标题“四方牌河”**零重叠**；
   - 南家牌面、西家标题及牌面、北家标题及牌面依序干净展示；北家末张位于视口底部且完整可见。
2. **缩放尺寸 `build/tile-mark-visual/screenshots-scaled/hint-scrolled-bottom.png` (960x540)**：
   - 缩放变体下同样完全无重叠，固定标题“四方牌河”保持纯净清晰；
   - 所有牌面与网格方位元素在 960x540 下等比适配正确。

---

## 4. 权限与交付约束核验

- [x] 仅修改生产文件（`RiverHintPanel.kt`、`TileMarkPanel.kt`）与本任务交付文档。
- [x] 未修改任何测试文件（`src/test`）、`tools/` 脚本、Gradle 脚本、用户 `SnapshotLauncher` 或 `snapshot`。
- [x] 源码及文档中文均使用 UTF-8 编码。
- [x] 未执行 git commit，未启动任何子代理或外部代理。
- [x] 全量测试与双端构建移交 Codex 独立执行。
