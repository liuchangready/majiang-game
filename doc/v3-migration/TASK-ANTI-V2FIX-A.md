# A 共享测试夹具与界面测试迁移

只读本任务和 V2-CLEANUP-FIX-PLAN.md，勿读历史迁移计划。使用内置读写编辑工具，禁止终端/构建。优先完成共享夹具，再迁移属于你的测试。不要等待其他组。

所有权：core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt、GdxHeadlessTestRunner.kt，以及 core 测试中除以下 B 文件的全部测试：GameScreenTest、RiichiScreenIntegrationTest、RiichiChoiceScreenContractTest、MeldChoiceScreenContractTest、FuritenScreenContractTest、MeldDisplayContractTest、OpponentMeldDisplayAcceptanceTest、RiichiPacingAcceptanceTest。不改产品文件/engine/tools。

根因与要求：
1. GameScreen.runOnUi 已统一异步 postRunnable，不恢复 V2 同步分支。以明确的 UI 调度/排空工具让测试在断言前等待实际回调完成。不要任意 sleep；不要在 UI 线程 postRunnable 后 get 自锁。测试基类可以捕获真实 headless UI 线程并提供安全 helper；Fake.flushUi 在同一线程必须不阻塞。保留现有 API 供 B 调用。
2. FakeScreenClient.toSnapshot 当前忽略事件，cursor 固定0。构造符合 V3 的单调 cursor、round identity、settlementRoundId/settlementEvents；终局事件推送后无事件更新应保留同局结算，真正新局才清理。结算事件应复制隔离，不用全域可变引用。支持 view-only 假快照，不泄露规则状态；不能自动伪造麻将合法动作或最终结果。
3. start/choose 内等待只能等待已有任务，不能保证 caller 之后注册的回执回调；迁移测试须在必要断言/下一次点击前显式 flush。initialize/start 的回执成功也不能伪造已经发牌。
4. 核查 ClientSelectionAcceptanceTest 的同步 screen.submitAction 断言，应验证真正的异步回执/提交次数，而非偶然尚未完成被映射为 Rejected。AsyncScreen、ScreenRecovery、EndTenpai 等既有 V3 异步断言保持。
5. 保留每项原测试的行为断言，不允许删用例/跳过/降低覆盖。无需编写新功能测试，Codex 独立验收。

写 delivery-ANTI-V2FIX-A.md，列出迁移路径、线程/快照设计与无法处理的具体问题。不要遍历无关目录。
