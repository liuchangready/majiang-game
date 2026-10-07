# C 第二轮返修（仅事件/会话边界，先前 PlayerView/AI/UI 返修已完成）

文件所有权：event/GameEventBus.kt、可新增 event 包事件快照辅助、session/SessionUpdate.kt、session/LocalGameSession.kt。禁止改 state/、rules/、UI/AI/PlayerView；D 和 B 正在并行修复。

Codex 实测两项失败：
1. EventIsolationContractTest：第一个监听器修改 ScoreSettled.newScores[0] 会影响第二监听器和总线历史。总线发布入参、保存历史、各监听器、getHistory 返回值之间必须分别深拷贝 arrays、maps、nested meld tiles、yaku lists。将快照辅助放 event 包，不能让领域事件反向依赖 session 包；SessionUpdate 可复用辅助。保留优先级、异常隔离、回放行为。
2. MatchIntegrationContractTest.session observers and command results have independent settlement arrays：LocalGameSession.notifySubscribers 复用同一个 SessionUpdate，前监听器污染后一监听器。为每个监听器创建独立事件快照，Accepted.events 与订阅快照也相互隔离。三条命令返回值脱敏现已通过，不要回退。
3. subscribe 首次推送也要捕获回调异常；close 后不得继续执行命令或重新订阅并启动已关闭游戏。
4. D 正在修复引擎 Accepted.events 包含全部后续 AI/响应/结算事件；你直接使用它，无需绕过引擎自己解析总线差值或暴露 state。

只修复上述生产文件，勿改测试。更新 delivery/C.md，如实说明未运行命令，完成后停止编辑交 Codex 复测。
