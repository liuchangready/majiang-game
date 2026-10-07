# B 真实牌局集成测试修复

首先立即补 core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt 缺失的 com.majiang.session.PlayerView 与 com.majiang.state.GameState 导入，让 Codex 可以运行完整测试。然后处理本组真实链路；不要等待其他组或调查历史架构。

仅修改 core/src/test 下这8文件：GameScreenTest、RiichiScreenIntegrationTest、RiichiChoiceScreenContractTest、MeldChoiceScreenContractTest、FuritenScreenContractTest、render/MeldDisplayContractTest、render/OpponentMeldDisplayAcceptanceTest、architecture/RiichiPacingAcceptanceTest。不改其他测试、共享 FakeScreenClient/GdxHeadlessTestRunner、产品代码或工具。只用内置读写编辑；不执行终端/测试。

共享 FakeScreenClient 保持 initial/snapshot/submitted/update/publish/flushUi API，A 正修它的线程与终局快照语义。不要为当前缺陷绕过共享夹具；必要时测试在动作回执处理后明确 flushUi。

重点：
- GameScreenTest.realScreen 返回 fake，而打牌测试直接 fake.choose 只记录动作，并未经过 coordinator 装饰器。改成实际 Screen→client→authority 链路并证明弃牌实际进入牌河/手牌变化，保留发牌、物理身份等断言。不能把真集成改成假动作计数。
- 优先使用 TableHost→GameClient→V3GameClientAdapter（已有 ScreenRecoveryAcceptanceTest 范例）。若保留测试自有 coordinator 装饰器，事件必须有序推送，并保留 ScoreSettled、terminal settlement 快照所需事件；不能只更新 PlayerView 丢事件。调用回调时不要持 authority 锁等待 UI 再查询 authority 导致互锁。
- clear V2 后 UI 回调 postRunnable，动作调用与下一次输入/断言间要正确等待；拒绝回执处理后模型退出和再次提交能力都需验证。不要 sleep/放宽断言/删测试。
- RiichiPacing 最后端到端用例保留真正的动画门控与 host 时钟节奏，clock/tick/flush 按权威流转驱动，不通过无限加速/直接清空动画让断言过。
- 部分已有 coordinator-only 测试无需改；不再清理 engine 批2接口。

写 delivery-ANTI-V2FIX-B.md。最小范围逐项修，禁止删除合法测试或恢复V2构造器。
