# 立直流程验收记录

设计与测试：Codex。生产开发：Antigravity，经本地 Agent Mesh MCP dispatcher 委派 K / R 并行任务。

- K：`RiichiKanValidator.kt` 已交付，静态审查与 9 项独立规则测试通过。
- R：自动摸切、合法动作集与 UI 提示已交付。首次全量 213 项中 211 项通过、2 项失败；F 已修正真人座位判定，最终全量 213 项全部通过。
- R 首次调用触发 headless RunCommand 拒绝，无有效交付；已要求只使用文件工具重试，未修改权限。
- 首次完整编译的红灯是 R 尚未添加 `autoRiichiDiscard` 构造器参数；不是已完成实现的测试结果。
- 独立规则门禁使用 `tools/architecture/riichi-rule-check.init.gradle` 隔离输出；不替代最终无过滤测试。

新增独立验收覆盖纯规则、真实 136 张物理牌桌、单命令事件集合、重入保护、对手荣和、连续岭上杠、牌山/补牌容量、非零真人座位，以及 GameScreen -> LocalGameSession -> GameStateMachine 的实际按钮调用。

## 审查发现与返修

R 将缺少 AI 控制器的座位也判定为真人，导致单步引擎中其他座位被自动摸切。Codex 新增回归测试复现；既有永久立直振听测试也因此失败。已交给 Antigravity F 限定修复为严格 humanPlayerIndex 匹配，不允许以未配置控制器推断真人。

测试夹具曾重复使用同一张二万，已由 Codex 修正物理副本；这属于测试数据错误，不是生产缺陷。需要单步观察立直后的既有协议测试显式关闭 autoRiichiDiscard，保留原断言。

## 最终验收（2026-09-29）

结论：通过。K、R、F 生产实现均来自 Antigravity；Codex 编写设计、28 项新增测试、测试夹具修正、旧单步测试的显式开关、桥接与验收工具，以及文档，并独立审查和执行验收。

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| `gradlew test :lwjgl3:classes :android:assembleDebug` | BUILD SUCCESSFUL；213 测试，0 失败、0 错误、0 跳过 | `build/riichi-flow/final-validation.log`；`core/build/test-results/test/TEST-*.xml` |
| 严格暗杠独立规则 | 9 项通过 | `build/riichi-flow/rule-test.log` |
| 新增对局流程与错误座位回归 | 18 项通过 | `RiichiFlowContractTest` |
| 真实按钮 → 会话 → 引擎 | 1 项通过，非零座位暗杠后岭上自摸并结算 | `RiichiScreenIntegrationTest` |
| 实际 LWJGL/OpenGL 渲染 | 60 帧、17 次合法操作，至 ROUND_END | `build/riichi-flow/desktop-smoke.log`，`RENDER_SMOKE_PASS` |
| Android 调试 APK | 构建成功 | `android/build/outputs/apk/debug/android-debug.apk` |

桌面冒烟使用隐藏的独立窗口，不控制或关闭用户对局。Android 本次仅构建，未在真机验收；当前 JBR/LWJGL 的既有 JNI/native-access 警告仍存在，实际冒烟无崩溃。完整游戏启动后默认开启真人立直自动摸切；正在运行的旧进程不具备热更新能力。

生产文件仅四处：`rules/RiichiKanValidator.kt`、`state/LegalActionProvider.kt`、`state/GameStateMachine.kt`、`screen/GameScreen.kt`。不改变 AI 策略、结算或物理牌移动服务；新自动出牌沿用现有事务、136 张守恒与响应窗口。

验收后确认先前游戏窗口已退出，并通过 `gradlew :lwjgl3:run` 启动新版本供用户试用。
