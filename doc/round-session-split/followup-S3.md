# S3：编译与外部结果验证修复

S2 已交付。只改 S 归属 application/**、match/MatchSession.kt、state/GameStateMachine.kt；不要改 round/client/session/screen/test。其他任务正在 R3、C2 修复。禁止 shell/构建/改测试。写 delivery-S3.md。

1. 编译错误见 build/round-session-split/acceptance-first.log：ApplicationCoordinator.kt 834/835/836/861/862 的 roundRes 被推导为 RoundResult?。R2 的 change.result 现在是防御复制 custom getter，不能 smart cast。先 `val roundRes = change.result`，再判断该局部变量 null；或者在 if 中 requireNotNull，但避免重复读取和无意义断言。
2. init 调用 validateStartSession(session, firstRound)，要求 firstRound.state.phase==STACKING，导致所有接受已有合法牌局的 GameStateMachine / Coordinator 测试 fixture (DRAW/DISCARD/...) 都立即失败。构造函数一直允许传入正在进行的 GameState。必须提供外部验证的初始化/恢复路径，允许完整合法的进行中局；仍不能允许非法重复开局。普通空白GameState的第一次开赛仍正常。StateInvariants 对完整已发牌 fixture 可验证；旧 standalone tests 某些初始状态是未完全发牌的测试装配，也请查看原测试约定保持兼容，不要为了通过新校验破坏旧fixture的准备流程。
3. RoundResultValidator 缺少结果与候选状态的一致性：必须验证 change.state.phase==ROUND_END、change.state.scores.toList()==result.finalScores、change.state.riichiSticks==result.remainingRiichiSticks，以及 session.activeRound 与 round 同源，终态没有开放 responseWindow。仅验证 result 内部总量等式可能接受与实际状态不一致的金额。Codex 已加 ExternalValidationAcceptanceTest 三个对抗用例。
4. Legacy state import 仅限 version0 是合理的迁移保护，但要同步本局起始账本：importLegacyStateIfUnstarted 改了 session初始账本后 Round 的 startingScores/startingRiichiSticks 仍是构造旧值，会在验收时不匹配。如确需导入未执行任何动作的 fixture，协调更新两边初始基线；只允许旧适配器调用，不让正常客户端导入。
5. 保持首次 Round 深拷贝、终态只读快照、跨局新实例、请求作用域严格匹配、深复制和一致性锁等 S2 修复，不得回滚来绕过问题。

先完成这些明确问题。Codex 随后执行新验收与全部401旧测试再反馈，不要自行运行命令。
