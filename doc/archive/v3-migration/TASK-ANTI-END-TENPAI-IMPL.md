# 本次开发限定

直接实施 END-TENPAI-DESIGN.md。不要重新调查全项目，不要读 PLAN.md，不要读历史交付/架构文档或测试文件。Codex 已经完成规则侧验证：V3 自摸击飞、流局罚符击飞、供托一次结算和零分策略均通过，不需要修改 engine 宿主/规则。

仅需读这些文件并立即开始编辑：
- engine/src/main/kotlin/com/majiang/session/RiichiPreviewProvider.kt、PlayerView.kt，rules/FuritenRules.kt；新增 session/TenpaiPreviewProvider.kt，固定 waits(view) API。
- core/src/main/kotlin/com/majiang/screen/GameScreen.kt（重点初始化与 onSnapshotAndEvents、render、结算渲染及 touchDown）和 screen/SettlementDetailRenderer.kt（字体、按钮文案）。可新增 core/screen 的纯表现或渲染类。
- 必要时读 engine/access/SessionView.kt、PublicSessionResult.kt 和 core/MahjongGame.kt 的退出路径。

事实：正常 V3 的 FINISHED 已被 Coordinator 投影为 MATCH_END。GameScreen 在 MATCH_END 点击只隐藏结算并留在死牌桌，这是产品缺陷；另有 ROUND_END + session.FINISHED 恢复边界错误和异步 nextRound 失败丢失结算。总成绩界面必须接到实际按钮，并提供返回菜单。

请暴露只读 isMatchFinished、isFinalResultShown 和 tenpaiWaits 供 UI 验收；命名可直接使用这些。下一局按钮现有矩形 (520,145,240,46) 保留；总成绩“返回菜单”建议使用同位置。保留结算详情供总成绩页返回查看。

不要花时间编写/运行测试，Codex 的测试已就位。不修改 Gradle 或测试。优先最小实现，开发完再检查所改代码。所有生产代码由你完成。写 delivery-ANTI-END-TENPAI-IMPL.md 后返回。
