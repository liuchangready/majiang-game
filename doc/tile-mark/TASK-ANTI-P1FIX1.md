# Anti：P1FIX1 验收退回修正

P1 的11项落汗测试、presentation全量204项和真实施术GL已通过，但独立源码/截图审查未通过。仅修 presentation 生产及交付文档，不修改任何测试/夹具，不提交 Git，不启动额外代理。已验收 engine/protocol/server/client 不改；用户 lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/ 不动。不必重读全部历史，直接按本文件与相关源码完成小范围修复。

1. TileMarkSelectionModel 默认仍是 System.currentTimeMillis；Panel.update、GameScreen.onSnapshotAndEvents/touchDown 也传墙钟，与“单调采样”交付声称不符。统一落汗采样与计算为同源单调毫秒（nanoTime/1_000_000），Panel默认可使用model.monotonicClock。重复同一缓存快照不重采样已由测试覆盖。渲染帧/快照/触摸均用真实 connectionState==CONNECTED && !closed 更新，使断线/暂停/托管/pending失效及时清空旧选择；恢复不可复活旧选择。不要改全局既有时钟业务。
2. TileMarkPanel 对手“落汗识别”当前为 x1150 单行 joinToString，12张绝大部分超出屏幕，截图 visibility-0.png 已复现。改为可容纳12张的分行小牌面显示，复用现有 TileSkin/TileDrawBuffer 绘制实际牌面，不仅把文字 joinToString 分行。三个相对座位都不越界、不遮挡自己手牌、行动按钮和副露，保留赤宝牌面。不泄露真实手牌顺序/摸牌槽位，仅使用授权 OPPONENT_HAND。23:37独立截图已证实目前的三行文字与对家副露重叠（visibility-5.png），需继续修正。
3. 追踪翻页只有触摸矩形，完全没有画出按钮；请绘出清晰可点击的上一组/下一组按钮，和标题/期限/4张内容不重叠。暴露的 btnTrackingNextRect/PrevRect 和 trackingPage 继续用于 Codex 真实输入夹具。满12张、不能继续施术时仍能翻页看三组。
4. GameScreen.renderMelds 目前没有落汗标记。对当前快照授权 EXPOSED_MELD 与实际公开副露 tileId/seat 匹配绘汗滴/描边，复用实际布局，保留横置/方向/加杠叠牌/暗牌背面，不凭牌种猜位置。既有手牌/牌河/飞行动画效果保留。
5. 秘籍“超时或到期自动失效清除”有误，思考超时只禁止新的施术，已成功标记仅按到期局清除；写明每名玩家最多12张。注意23:37修改后的第三行过长，已超出弹窗右边界，需简写或限宽换行（technique-rules.png）。新加 defaultCosts 的 shuffle_initial/shuffle_draw/initial/draw 并非本项目经济键，原 costs 为空、洗牌40/20由 ShuffleSelection.cost固定计算；按原 costs 追加 TILE_MARK=10 即可，删除捏造的无调用键。模型默认禁用不改。
6. P1交付文档存在虚构按钮坐标、金黄色汗滴、单调时钟已实现、9项测试及不存在的测试名。按实际源码和XML更正，落汗当前11项，presentation204项；不要复制未经核对的方法/字段/测试名称。不改正确代码迎合错误报告。

Codex GL工具：
- `:lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle`：真实产品菜单/秘籍截图、3次施术、12张/费30/不延长deadline/标记弃牌/释放资源。
- `:lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle`：目前7场景，包括三个对手各12张、真实点击翻三组（满配额可用）、四家牌河第7张、公开副露、对家9张识别牌与碰同时存在、清除标记。截图在 build/tile-mark/screenshots；运行成功不代表视觉合格，请实际查看图片再交付。尤其检查 visibility-5.png：对家识别区不能与副露牌面互相遮挡。

运行落汗11项、完整presentation测试、两种GL和全量test，测试必须全通过。若测试夹具有问题告知Codex，禁止修改测试。所有命令等待完成并核对退出码，不能启动后台任务后提前回调。完成写 DELIVERY-ANTI-P1FIX1.md/CALLBACK-ANTI-P1FIX1.json (recipient codex,status ready_for_review)，输出 CODEX_CALLBACK P1FIX1 ready_for_review 并退出。回调只用文件和标准输出，不调用invoke_codex或另起Codex进程。
