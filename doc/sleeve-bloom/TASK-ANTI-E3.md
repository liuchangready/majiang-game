# Anti / agy：袖里藏花 E3 表现层

实施期Codex反馈：请读取FEEDBACK-E3-INITIAL.md。旧理牌高至少44测试正确，任务已改高44；另新增3项提示回归，E3现共13项。不要改测试，按反馈修复后继续完整验证。

状态：正式下发；E2已通过独立验收，TESTS-READY-E3.md已发布。遵守 AGENTS.md、DESIGN.md、VISUAL-DESIGN.md。Codex 编写测试和独立检视，Anti 仅生产实现。所有测试只读，不修改用户 SnapshotLauncher.kt / snapshot/ / Gradle，不委派，不自行 Git 提交。

允许 presentation/src/main 的界面、组合根与菜单必要接线。UI 只依赖 client.api；前几层只读，有缺陷回报 Codex 转阶段返修。严格按视觉稿：入口(1080,100,140,44)，两候选卡与确认/保留第一张按钮、权威倒计时、身份变化清选中、模态防穿透、断线/托管/暂停隐藏后恢复，回执与快照乱序安全。启用配置随现有落汗默认策略，秘籍说明准确。

冻结测试接口（允许增加）：

- presentation.game.SleeveBloomChoiceModel(monotonicClock: () -> Long = ...)；只读 isChoosing、selected: SleeveBloomKeep?、canActivate、canConfirm、remainingMillis: Long；update(snapshot: GameSnapshotView?, pending: Boolean = false, connected: Boolean = true, nowMillis: Long = monotonicClock())；select(keep): Boolean；buildActivateRequest(requestId): SleeveBloomRequest?；buildChoiceRequest(requestId, keep): SleeveBloomChoiceRequest?。选择FIRST取消不需预选，确认按钮必须先有selected。构造请求不改快照；pendingOffer自动开启选择界面，不靠乐观激活。
- SleeveBloomPanel 公共 model、btnOpenRect、panelRect、firstCardRect、secondCardRect、btnConfirmRect、btnCancelRect、isDisposed，companion translateServerReason 与 SLEEVE_BLOOM_CHARS。矩形为实际绘制/命中矩形，不是只给测试的副本。
- GameScreen.sleeveBloomPanel，按TileMarkPanel同式挂入实际render/input/modal/dispose，所有屏幕异步完成以当前绑定/身份为准，迟到回执不能清除新offer的选中。

准备阶段见 PREP-ANTI-E3.md，但其中仅将托管下移到y=50的建议会撞理牌按钮，已由Codex再次修正。按最新版VISUAL-DESIGN.md：保持落汗y150/高44、袖里藏花y100/高44，理牌移至(1080,50,140,44)，托管/收回移至(1080,6,140,36)，补托管可见绘制、保持实际绘制与命中一致；不只是让启用时的袖里藏花点击优先，禁用入口也不能穿透切换托管或理牌。不能禁用或删除原控制权/理牌功能来规避冲突。Codex已新增实际按钮分离与禁用点击测试。

收到 TESTS-READY-E3.md 后运行 :presentation:test，修复后运行 test、:lwjgl3:classes、:android:assembleDebug。必须真实执行且核对XML数量；界面另由Codex真实GL检查。若有测试夹具问题只报证据，Codex负责修正。

写 PROGRESS-ANTI-E3.md、DELIVERY-ANTI-E3.md、CALLBACK-ANTI-E3.json（taskId=SB-E3，recipient=codex，真实命令/计数/剩余项）。全部通过才ready_for_review，最后输出 CODEX_CALLBACK SB-E3 ready_for_review 并退出通知当前Codex。失败先修复，检视退回必须改进后重交；回调不代表验收通过。
