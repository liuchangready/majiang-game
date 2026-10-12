# Codex 首轮反馈（SB-E1）

2026-10-12 12:00 前后首轮 XML：27 项、5 失败、0 错误、0 跳过。尚未通过。

已由 Codex 修正的测试问题：两项和牌测试在结算后访问 coordinator.activeRound!!，但协调器允许释放活动局。现改为和牌前保存 RoundStateMachine 引用，在结算后检查该局状态，保留役种与物理守恒断言。Anti 不需要也不得修改测试。

Anti 必须修复生产问题并重跑：

1. 错误 sessionId 不得写入当前整场去重账；修正激活与选择的作用域校验位置。
2. 无 DecisionClock timing 时私有投影 canApply 必须 false，不能存在无限选择窗口。
3. 成功请求重放先查幂等，再对首次请求检查暂停，暂停不能将旧成功回执改成 HOST_PAUSED。
4. 静态检视：ApplicationCoordinator.submitSleeveBloom 仍 val cost = 10；按设计读取经过 MatchConfig 校验的配置，不能只门面读配置。
5. 静态检视：两个 TableAuthority 新入口需保持既有同步约束；核对 @Synchronized 与座位/作用域/回执访问的原子性，不让直接调用竞态破坏去重结果。

继续按 TASK-ANTI-E1.md 自测完整 engine，所有失败先修复再 ready_for_review；本反馈不代表其他代码已通过最终检视。
