# 落汗提示 V1FIX2：滚动标题重叠收尾

V1FIX1 的 254 项 presentation 测试通过，主要布局问题已修正。Codex 直接查看 `build/tile-mark-visual/screenshots/hint-scrolled-bottom.png`，发现滚动后的“南家 (18)”画到固定“四方牌河”标题上，必须修正后交付。

只处理下列小范围问题，不重复阅读历史任务或整个 GameScreen：

1. RiverHintPanel.render 中滚动区域的方位标题使用了 `(panelRect.y - 20f)..(riverAreaTop + 10f)`，会侵入固定标题。让方位标题及牌面严格处于固定标题下方的网格可见范围，底部也不要画出面板。标题完整落在视口内才绘制即可，无需重构。保留已经通过的滚动、拖动、末张可达及精确点击。
2. TileMarkPanel 顶部 KDoc 仍声称“位置追踪”和“对手区域落汗识别”，与已删除的表现不符，只更新注释为当前图形寿命区和完整对手手牌展示。

权限与分工：仅 Anti 修改上述生产文件和本任务交付文档，不改测试、tools、Gradle、用户 SnapshotLauncher/snapshot，不提交，不启动其他代理。Codex 编写并拥有独立测试。不要拓展其他功能。

完成后运行并核对退出码：

```
.\gradlew.bat :presentation:test --console=plain
.\gradlew.bat :lwjgl3:tileMarkHintSmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --console=plain
.\gradlew.bat :lwjgl3:tileMarkHintSmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --console=plain
```

查看两种尺寸 `hint-scrolled-bottom.png` 确认方位标题与固定标题没有重叠。写 `doc/tile-mark/DELIVERY-ANTI-VISUAL-FIX2.md` 和 `CALLBACK-ANTI-VISUAL-FIX2.json`，包含 taskId=V1FIX2、recipient=codex、status=ready_for_review、测试命令/退出码/截图结论。终端输出 `CODEX_CALLBACK V1FIX2 ready_for_review` 后退出。Codex 最后独立运行全量测试及双端构建。
