# BS-E3：表现层与完整交付

仅在Codex明确验收E2并下发后开始。遵守AGENTS、DESIGN、INTERFACES及已写好的VISUAL-DESIGN。Anti只开发生产实现与交付文档，Codex写测试和GL验收脚本。

## 实现

- BeamSwapChoiceModel、BeamSwapPanel、GameScreen输入与渲染接线，严格遵守VISUAL-DESIGN冻结模型与矩形API。update参数名为isPending（不要照抄袖里藏花的pending）。真正的原有暗手牌与目标槽双选；保持原样不依赖选择。未知候选不泄露牌面，标记候选半透明加汗滴；13候选布局可用。
- 五个右侧按钮重排并统一几何。所有相关面板入口可见禁用态与实际输入锁一致。beam modal纳入isModalActive，禁止手牌/理牌/其他千术/快捷键穿透；Esc提交KEEP。截止、私有推送、回执乱序、断线/暂停/接管/恢复依现有权威快照驱动。
- 添加拒绝原因中文说明与超时保持原样提示，补字体资源字符，千术秘籍说明/入口文案/配置映射支持。产品组合根及建房默认配置与袖里藏花一致显式开启beam并配置10点；领域/协议默认仍false，不能偷偷强开旧服务或用户禁用配置。
- 更新状态/接口文档准确反映实现；保留用户SnapshotLauncher、lwjgl3/build.gradle、snapshot等既有工作。不移动或删除既有测试。

## 验证与回调

Codex-owned：BeamSwapChoiceModelContractTest、BeamSwapScreenContractTest以及tools/architecture的GL harness。等待build/beam-swap-e3-tests-ready.json后测试；不得自行改测试文件或断言，错误写FEEDBACK-ANTI-E3.md请Codex修正。

先: presentation:test中*BeamSwap*及现有*SleeveBloom*/*TileMark*相关用例，再运行不筛选的test、:lwjgl3:classes、:android:assembleDebug及Codex提供的GL冒烟，两种视口1280×720/960×540保存截图。运行结果与真实通过数量才写进交付，未做真机验证须明确区别。Anti独占Gradle直到回调，不能将失败隐藏为已完成。

修复后写DELIVERY-ANTI-E3.md、CALLBACK-ANTI-E3.json（recipient=codex,taskId=BS-E3,status=ready_for_review，命令/结果/截图/边界）并stdout输出CODEX_CALLBACK BS-E3 ready_for_review，退出等待Codex独立检视。若有问题按反馈改进并再次回调，不Git提交或推送。
