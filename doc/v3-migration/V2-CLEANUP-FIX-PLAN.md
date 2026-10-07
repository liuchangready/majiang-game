# V2 接口清理故障分析与修复分工

2026-10-05。Codex 复现、拆分、集成与独立检视，Anti 实施。范围为当前 core 侧 V2 清理的编译、行为测试及可执行验收工具，保留已迁移代码和工作区变更。此次不扩展到 engine 批2/批3删除，不恢复 V2 构造器或兼容适配器，不删除有效规则测试。

最终状态：已完成。561测试、桌面/Android构建、11个真实GL工具全部通过，见 [验收记录](V2-CLEANUP-FIX-REVIEW.md)。原三组任务经超时与审查返修拆为更小批次；另设G4修复确认动作仍读取旧屏幕快照的实际展示回归。

## 已知问题

- 编译阻塞：GameScreenTest 使用 PlayerView、GameState，却遗漏导入，导致 core:compileTestKotlin 失败。engine/client/server 与产品源码需以实际本轮日志结果分别评估。
- 同步语义未迁完：去掉 LegacyEndpointAdapter 后 Screen 回调统一 postRunnable；部分测试仍提交后立即断言，FakeScreenClient 在 choose/start/update 内部等待 UI 队列可能在 UI 线程自锁，且 choose 内 drain 无法等待尚未注册的 whenComplete。
- 快照语义缺失：FakeScreenClient.toSnapshot 忽略 events，固定 cursor/roundId，未提供 settlementRoundId/settlementEvents；新版 Screen 正确按快照恢复/清理，因此旧事件式夹具会错误清空结算或重放动画。
- 部分真实集成测试用 fake.choose 代替实际提交装饰器，只记录动作而不推进领域状态；这种测试即使通过也失去原有覆盖。需保留物理守恒、扣点、响应优先级及单步节奏断言。
- 工具未纳入默认 test 编译：Java GL 验收脚本仍调用旧 launchGame(GameSession)、GameScreen 无参构造器或 FakeGameSession。默认测试绿不能证明这些工具可用。
- Android D8 输出旧 Kotlin metadata 警告；先区分任务是否成功，不把此工具链警告当成 V2 清理根因，暂不升级依赖。

## 文件所有权

| 任务 | 唯一写入范围 | 目标 |
|---|---|---|
| ANTI-V2FIX-A | core 测试的 FakeScreenClient、GdxHeadlessTestRunner 及未列入 B 的 core 测试 | 确定性 UI 调度、V3 假快照语义、原 view-only 测试迁移 |
| ANTI-V2FIX-B | GameScreenTest、RiichiScreenIntegrationTest、RiichiChoiceScreenContractTest、MeldChoiceScreenContractTest、FuritenScreenContractTest、MeldDisplayContractTest、OpponentMeldDisplayAcceptanceTest、RiichiPacingAcceptanceTest | 先恢复编译，再恢复真实提交链与游戏行为验证 |
| ANTI-V2FIX-C | tools/architecture 的 Java GL smoke 及对应 smoke init.gradle，可新增仅供工具使用的 V3 helper | 移除所有可执行工具对已删除 V2 构造器的调用，保留视觉与行为断言 |

A 不修改 B 文件，B 不修改共享夹具与测试基类，C 不修改 core/engine 源码或测试；三组均不修改产品代码。若确认产品代码回归，报告给 Codex，单独分派。A 保持 FakeScreenClient(initial)、snapshot、submitted、update、publish、flushUi 等现有 API，新增辅助不得侵入 engine。其他组可以调用现有 flushUi，但共享 API 更改须说明。

## 验收

Codex 先编译测试以暴露行为失败，逐项检查真假提交链、线程等待位置、快照身份及终局恢复。执行四模块测试、桌面编译、Android 构建；编译全部受支持的 GL smoke，实跑默认 V3 游戏以及立直/副露/结算/听牌关键流程。禁止用删测试、放宽关键断言、吞异常、任意 sleep、恢复 V2 同步分支让结果变绿。

基线日志：build/v3-migration/v2-cleanup-baseline-20261005.log。最终证据与剩余范围另写 V2-CLEANUP-FIX-REVIEW.md。
