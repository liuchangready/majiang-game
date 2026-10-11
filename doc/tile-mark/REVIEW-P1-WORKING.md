# P1 开发中独立观察

2026-10-11 22:57，P1 尚在开发，以下按当前源码/真实 GL 结果记录，交付时需重新核对，不能据此声称验收。

- Codex 补强选牌测试：暂停/托管/额度不足等失效后恢复原快照，不得恢复旧选择。测试文件仍由 Codex 维护。
- 默认时钟目前是 System.currentTimeMillis，GameScreen/Panel 也传墙钟；任务要求同源单调毫秒（例如 nanoTime/1_000_000）。需要同时改采样与读取源，避免混用。
- 新增 Codex 独立 GL 可视夹具 `TileMarkVisibilitySmoke.java` / `tile-mark-visibility-smoke.init.gradle`。真实运行五个场景成功，但截图发现下家 12 张“落汗识别”从 x1150 单行绘制，绝大部分超出窗口；需要可容纳 12 张的分行小牌布局（按需求显示牌面）。三组追踪当前有点击矩形但未绘翻页按钮，用户无法发现入口。
- 当前公开副露 renderMelds 尚无汗滴/描边。请核对最终实现。
- 截图位置 build/tile-mark/screenshots/visibility-0.png（下家12张）、visibility-1.png（对家12张）、visibility-2.png（上家12张）、visibility-3.png（四家牌河第7张）、visibility-4.png（清除）。工具只提供渲染证据，不是自动视觉正确断言。

独立运行：`./gradlew.bat :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --console=plain`。输出 `TILE_MARK_VISIBILITY_GL_PASS scenarios=5`；仍有上述视觉问题，暂不验收。

23:04 独立运行 `:presentation:test --tests com.majiang.screen.TileMarkSelectionModelContractTest`：5 项中 3 通过、2 失败，已复现失效恢复后仍保留旧选择（第132行），以及 syncEpoch 更换却不重新采样时钟（第107行）。请修复实现，不得删改测试。

23:05 当前输入实现的落汗入口矩形 (1080,150,140,44) 与立直取消矩形重叠；touchDown 在立直选牌处理之前无条件吞掉该区域，即使落汗按钮隐藏/未启用也 return true，可能导致原有“取消立直”失效。请保持原有输入测试并修正实际可见按钮的路由顺序。

23:08 秘籍新增长文案应检查模态框内换行；“超时或到期自动失效清除”误导为思考超时清除成功标记，应说明仅期限到期清除、思考超时禁止新施术。新增 defaultCosts 中 shuffle_initial/shuffle_draw/initial/draw 并非本项目使用的经济键：原配置 costs 为空，实际洗牌40/20在 ShuffleSelection.cost 固定计算；只需按原 costs 追加 TILE_MARK=10，勿捏造无调用的键。
