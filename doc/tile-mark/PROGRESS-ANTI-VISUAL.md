# 落汗图形展示开发进度 (PROGRESS-ANTI-VISUAL)

- 任务号: V1FIX2
- 执行状态: 已完成待检视 (ready_for_review)
- 当前步骤: 滚动标题重叠收尾与KDoc注释更新完成，GL验证与截图确认通过，移交 Codex 验收
- 负责人: Anti (Antigravity)
- 检视人: Codex

## 步骤规划与完成情况

1. [已完成] 生产代码修改
   - 1. `RiverHintPanel.render`: 方位标题及牌面严格处于固定标题下方的网格可见范围（`riverAreaTop - 36f` 到 `panelRect.y + 10f`），底部不画出面板，标题完整落在视口内才绘制，避免滚动后侵入“四方牌河”固定标题；保留已有滚动、拖动、末张可达及精确点击。
   - 2. `TileMarkPanel` 顶部 KDoc: 更新为“当前图形寿命区”和“完整对手手牌展示”，移除过时的“位置追踪”和“对手区域落汗识别”描述。
2. [已完成] 运行验证与退出码检查
   - `.\gradlew.bat :presentation:test --console=plain` (Exit code 0, 254 passed)
   - `.\gradlew.bat :lwjgl3:tileMarkHintSmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --console=plain` (Exit code 0, TILE_MARK_HINT_GL_PASS)
   - `.\gradlew.bat :lwjgl3:tileMarkHintSmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --console=plain` (Exit code 0, TILE_MARK_HINT_GL_PASS)
3. [已完成] 截图确认
   - 查看基准与缩放尺寸下 `hint-scrolled-bottom.png`，确认方位标题与固定标题无重叠，北家末张完整可见。
4. [已完成] 交付文档与回调
   - 编写 `doc/tile-mark/DELIVERY-ANTI-VISUAL-FIX2.md` 与 `doc/tile-mark/CALLBACK-ANTI-VISUAL-FIX2.json` （根目录重复回调由 Codex 比对内容一致后清理）。
   - 终端输出 `CODEX_CALLBACK V1FIX2 ready_for_review`。
