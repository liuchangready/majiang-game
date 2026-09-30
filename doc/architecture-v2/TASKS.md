# Antigravity 开发任务分配

当前派发状态：A/B/C 已初次交付，A/C 已完成一轮返修；D DISPATCHED（2026-09-29 14:46 Asia/Shanghai）。所有第一批生产文件的必要集成修改权限移交 D。Codex 验收尚未通过。

派发使用本机 Agent Mesh Bridge 的 MCP dispatcher / invoke_antigravity，调用记录保存在 build/architecture-v2/bridge-{A,B,C}.json 与 stdout/stderr 日志。tools/architecture/bridge-runner.cjs 仅适配 Windows 参数引用、隐藏子进程窗口及 accept-edits 模式；不修改已安装插件。

所有任务先阅读 DESIGN.md 与 ACCEPTANCE.md。Codex 保留 core/src/test、验收报告与设计文件的写权限。开发者可以运行测试，但不得改动它们；需要迁移测试时提交变更需求。

## 第一批：A、B、C 并行

| 任务 | 内容 | 独占生产文件 | 前置 |
|---|---|---|---|
| A | 物理身份、实际容器审计、原子移动 | model/**、state/GameState.kt、state/StateInvariants.kt、cheat/CheatActionExecutor.kt 中仅牌张移动适配 | DESIGN 3 |
| B | 纯结算、拆解计分组合、头跳仲裁、场次推进 | settlement/**、match/**、state/ResponseResolver.kt；rules 中新增组合服务，现有计分文件必要修正 | DESIGN 5，4.2 仲裁策略 |
| C | 会话接口、可见视图、界面迁移、AI 视图策略 | session/**（LocalGameSession 除外）、screen/GameScreen.kt、ai/**、MahjongGame.kt 必要装配 | DESIGN 6；先用接口编写，不等待引擎 |

第一批不得编辑 GameStateMachine.kt、GameAction.kt、GamePhase.kt、event/** 或 LocalGameSession.kt。B 的仲裁是纯函数，不自行控制引擎。C 不写生产假会话来掩盖缺失的 LocalGameSession；只完成与固定接口的对接。

## 第二批：D 集成（依赖第一批交付）

任务 D 独占 state/GameStateMachine.kt、GameAction.kt、GamePhase.kt、ActionResult.kt、LegalActionProvider.kt、ResponseWindow.kt、event/**、session/LocalGameSession.kt，并接入 A/B/C。

工作：统一拒绝语义、合法动作、响应窗口、真实守恒、结算/场次、UI/AI 视图投影、事务后事件、固定种子与测试模式、下一局/终局。核对所有旧调用点和平台编译。需要修改 A/B/C 所有文件时，由对应任务结束后转交所有权，禁止同时写同一个文件。

这是有依赖的并行开发，不能让四个任务同时重写状态机。Codex 在 A/B/C 返回时分别审查纯模块，然后审查 D 的集成。

## 交付格式

分别在 `doc/architecture-v2/delivery/A.md`、B.md、C.md、D.md 提交：

1. 完成项与源文件清单。
2. 与 DESIGN.md 的偏差；没有则明确写“无”。
3. 自检命令、结果与日志路径；未测试必须写未测试。
4. 必须由其他任务接入的接口。
5. 未完成项和已知限制。

未获派发前不得把状态自行改成 COMPLETE。最终 PASS/FAIL 由 Codex 独立验收记录。

## 第二轮并行返修（15:04 起，优先于此前文件移交条款）

第一轮 D 已停止。Codex 173 项测试发现 9 项失败，拆成以下独占修复任务并行交 Antigravity：

- B：仅 rules/WinningScoreEvaluator.kt，修复重复宝牌累计；读 review/B.md。
- C：event/GameEventBus.kt、新增 event 包事件快照辅助、session/SessionUpdate.kt、session/LocalGameSession.kt，修复事件/订阅者/返回值隔离；读 review/C.md。不要改 UI/AI/PlayerView，已完成。
- D：state/**、rules/RiichiRuleEngine.kt、必要的 model 字段适配、screen/GameScreen.kt。修复事务/响应/抢杠/场次/UI；读 review/D.md。review/D 中 6、7 分配 C，8 分配 B，你不要编辑其文件；其余全由你完成。
- Codex：所有测试、设计、独立构建和验收；无人得改测试。

每个任务交付只更新自己的 delivery 文件；不要修改本 TASKS.md。

## 最终完成记录（2026-09-29 15:33）

A、B、C、D 均已停止编辑，生产实现与多轮返修全部由 Antigravity 完成。Codex 最终独立验证：185项测试全部通过（0失败/错误/跳过）；桌面编译与 Android debug APK 构建通过；真实隐藏 OpenGL 60帧/15次合法操作正常到达 ROUND_END。参阅 REVIEW.md 的证据与明确限制。中途网络EOF已重试恢复，未把失败调用计为交付。
