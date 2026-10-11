# P1FIX2：最后两处显示修正

P1FIX1 功能修正、11项落汗/204项presentation/全量1000项自测和两个GL已完成；对家文本与副露重叠已在最终截图修复。仅剩以下小范围显示调整，由 Anti 修改生产，Codex负责测试，不提交Git、不启动别的代理、不改测试/GL工具/用户文件。不必读历史任务或全部源文件。

1. `TileMarkPanel.render` 的对手识别目前仍是 chunked(4)+joinToString 文字。按设计要显示真正的小麻将牌面：复用已有 TileSkin.appendTileDraw 与 TileDrawBuffer（GameScreen已有实例，可作为render参数传入），4列3行最多12张，保持赤五图案，不是文本替代牌面。可采用约26×36虚拟像素、横间隔28、纵间隔38；右侧x1140起，顶部对家x500起，上家x40起；各自标题下排布。确保底部不碰操作区、对家不碰右侧副露/左侧追踪栏/中间座位标签。只画 TileMarkPresentation.opponentMarks 授权列表，不映射真实手牌槽位。不新增图片资源，不改领域/API。
2. `MenuScreen.renderSecretsModal` 第3行过长，technique-rules.png 最右“新施术”已超出760宽弹窗。简写例如“3. 有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。”，实际截图确认所有文案在框内；每人12张/费用10/4实体规则不变。

范围原则上仅 TileMarkPanel.kt、GameScreen.kt（传渲染依赖）和 MenuScreen.kt。已有时钟/输入/标记逻辑保持；不反复读取GameScreen全部80KB，用rg定位必要片段。完成运行完整 `:presentation:test`（204项）和两个GL，命令如下，必须同步等命令结束并核对退出码；截图实际查看，不得把工具PASS视作视觉检查通过。

```
./gradlew.bat :presentation:test --console=plain
./gradlew.bat :lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle --console=plain
./gradlew.bat :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --console=plain
```

可视GL7场景，重点 visibility-0.png（右侧12张）、visibility-1.png（对家12张）、visibility-5.png（对家9张+碰）、technique-rules.png。不用再加测试；最终全量重跑、双端构建及缩放GL由Codex独立执行。

交付只需简短 DELIVERY-ANTI-P1FIX2.md/CALLBACK-ANTI-P1FIX2.json，recipient codex/status ready_for_review，列真实结果。输出 CODEX_CALLBACK P1FIX2 ready_for_review 并退出，不调用invoke_codex/任何其他代理回调。
