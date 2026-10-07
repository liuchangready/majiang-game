# Codex 独立诊断（2026-10-07 12:41）

Anti GAME中途明确429退出，自动GLM也返回429/1310（报告2026-10-09 14:46:33重置），未交付。现时`GameScreen.kt`仍是旧包161KB原文件，新`com.majiang.presentation.game.GameScreen`和`SpectatorScreen`尚未落盘。`build/v4-migration/R5-quota-partial-presentation-compile.log` 因此对ROOT组合根报两类未解析的新画面引用；不要让ROOT用反射、旧包壳或兼容构造绕过，应按`R5-UI-INTERFACE-PREPARATION.md`冻结签名完成真实画面迁移，再与ROOT/COMMON串行构建。所有旧点击、物理ID、动画、码牌、计时、结算与观战私密隔离断言仍需保留。

## Codex 续检（2026-10-07 16:52）

15:36 重启的 GAME 在桥接 45 分钟硬限制处退出，真实 writer 已消失；新 `presentation.game.GameScreen`、`SpectatorScreen`、牌桌模型/面板及部分迁移测试已落盘，但 **没有** `delivery-ANTI-R5GAME.md`，仍非交付。无 writer 时实跑 `build/v4-migration/R5-after-anti-presentation-compile.log`，GAME 自有 3 处编译错误：

- `GameScreen.kt:997` 和 `SpectatorScreen.kt:419` 调用不存在的 `MeldDisplayRenderer.renderMelds`。现 V4 COMMON 契约是 `MeldDisplayLayout.layoutAll(PlayerView)` 得 `MeldDisplayGroup`，再 `MeldDisplayRenderer.append(TileDrawBuffer, TileSkin, groups)` 并按既有 buffer 流程 flush；观战屏只有公开快照，不能伪造玩家私有 `PlayerView`，要用公开副露数据按公共布局契约渲染或向 Codex 报告需要 COMMON 扩公开视图输入，不能直接沿用 V3 旧 API。
- `SpectatorScreen.kt:188` 向 `FinalResultRenderer.render` 传 `PublicMatchResultView?`，现 renderer 接 `PublicSessionResultView?`。用公开字段做类型适配或报告 COMMON 扩公开结果入口，保留终局四人分数/名次/原因及立直棒去向，不注入私有玩家数据。

此外任务要求删除 `presentation/GameScreenClient.kt`、`V3GameClientAdapter.kt` 生产壳；当前它们和旧 `screen/GameScreen.kt` 仍存在，需在切换引用与迁移有效屏幕断言后按 R5/R6 边界处理，不能靠保留旧屏使测试虚假通过。请完成交付文档并写明未运行测试，由 Codex 继续独立构建验收。CLIENT 147/147、SERVER 111/111 现均通过；表现层尚不能编译，R5 未接受。
