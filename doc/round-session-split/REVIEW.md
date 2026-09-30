# 单局 / Session / 客户端拆分：检视与验收

日期：2026-09-30。设计、测试与检视：Codex；生产代码开发：Antigravity（本地 MCP bridge 并行任务 R / S / C 及后续返修）。

状态：本轮本地架构拆分验收通过。最终 431 项测试全部通过，0 失败、0 错误、0 跳过；桌面编译、Android Debug 构建及两项真实桌面冒烟通过。

## 1. 开发与集成结果

| 分类 | 实际实现 | 边界 |
|---|---|---|
| 单局 | RoundStateMachine、RoundChange、RoundResult | 保存并应用单局变更；终态不再启动下一局 |
| 单局外部规则 | RoundRuleService、RoundCommandProcessor、RoundTransitionPlanner | 合法性、规则计算、响应裁决与结算准备在状态机外部 |
| 整场 | MatchSession、SessionSnapshot、SessionResult、RoundRecord | 串联新局实例、交接积分、保存历史摘要、处理整场终局 |
| 应用与外部验证 | ApplicationCoordinator、ControllerRegistry、DecisionRegistry、RequestRegistry、RoundResultValidator、SessionCommandValidator | 组织验证、审计、提交、AI 与自动操作、时效与幂等 |
| 客户端 | PlayerEndpoint、LocalPlayerEndpoint、GameScreen | 展示座位投影，选择精确候选，订阅权威更新 |
| 兼容入口 | GameStateMachine、GameSession、LocalGameSession | 转发到新路径，兼容现有调用者；不保留另一套规则实现 |

默认 MahjongGame / GameScreen 已接入 LocalPlayerEndpoint。每次发牌（包含连庄）创建独立 RoundStateMachine；Session 终局不会把已结束 Round 改写为 MATCH_END。

## 2. 检视发现与已完成修复

- 旧状态引用共享会在下一局改写前一局：权威 Round 与兼容镜像隔离，旧局保持终态。
- 结算事件、候选与请求回执浅复制会泄漏可变引用：在安装、返回、广播和重放边界深复制。
- 下一局先写 Session 后发牌可能留下半启动状态：先准备、验证和审计，再提交活动局引用。
- 只查缓存无法保证选择仍有效：校验当前局、决策上下文、控制代次和精确作用域；同窗口不同座位保持独立。
- 无事件的响应记录仍改变可用操作：成功记录后必须广播视图更新。
- 端点锁与协调器锁交叉可能死锁：客户端不持锁调用协调器，一致性边界由协调器统一管理。
- 按同种牌回退匹配可能选错赤牌或实体副本：界面只提交精确匹配的候选。
- 选择提交异常后改走旧接口可能重复操作：支持决策的端点禁止此回退。
- AI 候选的嵌套集合也须不可修改；异常与非法动作仍使用已有安全兜底。

## 3. 测试保留与新增

保留原有 401 项测试；未通过删除断言、禁用测试或修改规则期望规避失败。

新增 30 项架构验收，位于 `core/src/test/kotlin/com/majiang/architecture/`：

- 单局候选准备与提交隔离、终态不可重开、快照与事件隔离。
- 连庄新实例、前局冻结、和止属于 Session、积分及供托只交接一次。
- 本局立直扣点 → 自摸 → 下一局的完整账本流程。
- 多端点更新、关闭隔离、响应窗口独立决策、旧局与旧控制代次拒绝。
- 重复请求幂等、作用域伪造拒绝、候选及重放回执的可变数据隔离。
- 外部结果验证拒绝重复消费、候选账本不一致和非终态伪装结果。
- 并发查询不能看到 AI 批处理的中间状态。
- 领域依赖边界、精确牌实例提交和异常时禁止二次提交。

额外桌面冒烟使用真实默认端点，并保留原输入动画闸门的冒烟用例。

## 4. 验证记录

| 检查 | 结果 | 日志 |
|---|---|---|
| 修改前基线 | 401 项通过 | build/round-session-split/baseline-tests.log |
| 首轮新验收（历史） | 30 项中 27 通过，3 项失败已修复 | build/round-session-split/acceptance-second.log |
| 首轮全量回归（历史） | 431 项中 19 项失败，均已修复 | build/round-session-split/regression-first.log |
| UI 精确候选与异常提交 | 2 项通过 | build/round-session-split/ui-selection-validation.log |
| 首轮真实桌面默认路径 | 发牌、玩家出牌与 AI 推进通过 | build/round-session-split/render-first.log |
| 首轮桌面与 Android Debug 构建 | 通过 | build/round-session-split/platform-first.log |
| 最终全量测试 | 431 项通过，0 失败、0 错误、0 跳过 | build/round-session-split/validation-final.log |
| 最终桌面编译与 Android Debug 构建 | 通过 | build/round-session-split/validation-final.log |
| 最终默认端点桌面冒烟 | 真实发牌、玩家出牌、AI 推进、动画结束及可操作界面通过 | build/round-session-split/render-final.log |
| 最终输入动画闸门冒烟 | 等待全部 4 次出牌动画后解锁，下一次操作重新上锁 | build/round-session-split/render-final.log |

最终验证命令：

```powershell
.\gradlew.bat :core:test :lwjgl3:classes :android:assembleDebug --console=plain
.\gradlew.bat -I tools/architecture/round-session-smoke.init.gradle -I tools/architecture/discard-input-gate-smoke.init.gradle :lwjgl3:roundSessionSmoke :lwjgl3:discardInputGateSmoke --console=plain
```

桌面日志包含 `ROUND_SESSION_CLIENT_RENDER_PASS endpoint=com.majiang.client.LocalPlayerEndpoint` 和 `DISCARD_INPUT_GATE_RENDER_PASS screenshots=4 discardsWaited=4 submissions=2`。最终截图已人工检视，出牌动画结束后正常展示可用操作。

产物：

- [新默认端点发牌截图](../../build/round-session-split/default-client-dealt.png)
- [新默认端点出牌后截图](../../build/round-session-split/default-client-after-discard.png)
- [Android Debug APK](../../android/build/outputs/apk/debug/android-debug.apk)

## 5. 本轮实现范围

实现当前本地游戏路径的职责拆分、共享本地端点、独立验证、请求选择与完整对局衔接。客户端能力声明明确 `local=true`、`remoteTransport=false`、`persistence=false`。

接口规格 v2 是后续完整接口方向，并不意味着其中的远端传输、账号认证、持久化恢复、订阅游标补发、全部管理接口已实现。本轮不声称具备这些能力。当前 Session 保留局历史摘要；现有界面继续通过脱敏事件展示详细结算。

旧 GameStateMachine 为现有测试与受信任调用者保留可变状态镜像及显式修改兼容。镜像与各局权威数据隔离，默认客户端路径不注册此镜像；后续远端适配应只暴露 PlayerEndpoint 的绑定座位接口。

实现契约见 [分类开发设计](DEVELOPMENT.md)，完整方向见 [接口规格 v2](../MAHJONG_STATE_MACHINE_API_SPEC.md)。
