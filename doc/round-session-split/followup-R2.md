# R2：单局隔离与审批边界审查修复

只修改 R 原归属：round/** 与 ResponseWindow.kt。禁止 shell/构建/改测试。其他任务并行修复自己文件。写 delivery-R2.md。

1. RoundStateMachine.result 只 events.toList()，install 直接持有 change.result 与 change.events，GameEvent.ScoreSettled.newScores 等数组仍共享。用已有 snapshotGameEvent 递归防御复制，在安装、返回 result、accumulatedEvents、RoundResult outcome ExhaustiveDraw 的 mutable Set 等边界隔离。Codex 已新增 RoundBoundaryAcceptanceTest 的 settlement event arrays 测试，必须通过；不能删除测试。
2. ApprovedRoundStep 是 public data class，任何调用者可以创建审批指令。改为 internal 类型或 internal constructor，只由 RoundCommandProcessor 生成。Round.prepare/install internal，公共 RoundPreparation/Change 对外只允许快照读取，不能让读到的 mutable nested 对象篡改已安装状态。
3. round 权威对象不应依赖规则规划器调用验证或计算。现有 Round.prepare 使用 activePlanner.plan，此依赖让状态机间接驱动规则计算，与 v2 设计边界不符。把外部验证、规划计算留在 RoundCommandProcessor：先在隔离快照上由 RoundTransitionPlanner 生成已经计算的 ApprovedRoundStep/效果，再由 Round.prepare 组装候选状态、事件和版本信息，install 应用。可调整 round 包内部签名，但保持 DEVELOPMENT 对 S/C 的 RoundCommandProcessor.prepare 和 RoundChange/Result 属性契约。RoundStateMachine 不持有 planner/rule 服务。业务拒绝全部在外部规则服务；Round 内保留编程错误断言即可。
4. 保留既有单局规则全部分支，尤其过和振听、响应优先级、加杠抢杠、暗杠后宝牌岭上、荒牌结算和完整累积结算事件。Round 命令结束不执行跨局或 AI。

你可以阅读 Codex architecture 新测试理解隔离要求，不要改测试。编译与测试由 Codex 负责。
