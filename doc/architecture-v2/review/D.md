# D 最后一轮定点返修（前轮主要问题已通过，不要重写整个引擎）

B/C 已交付停止。Codex 最新实际运行183项测试，5失败，178通过。日志 build/architecture-v2/integration-test.log 与 core/build/test-results/test。你只需要编辑 state/GameStateMachine.kt 和 state/LegalActionProvider.kt，以下4处明确修复；不要修改测试、设计、其他生产文件、不要运行终端。其余模块已通过，不要重做。

1. **加杠实体牌选择错误（引发3个失败）**：GameStateMachine.openResponseWindow 当前 targetTile = pendingKan.tiles.first()；getKanOptions 返回 existingPon.tiles + 新增牌，因此first是已经在meld里的牌(测试5万copyIndex=0)，而真正新增牌是手里的copyIndex=3。应当在 pendingKan.tiles 中按权威手牌 closedTiles/drawnTile 找唯一新增的实体牌（不能仅按sameType；也不要改成last依赖顺序）。然后抢杠事件及无人抢后的TileMovement.addedKan均使用这一张。相关3个测试：passing ron in riichi、claimed discard still causes furiten、passing chankan commits added kan。
2. **吃碰后伪自摸**：LegalActionProvider 当前 `currentDrawn != null || allClosed.size % 3 == 2` 会给碰后的11张闭手生成自摸。必须要求真实 currentDrawn != null（庄家发牌/普通摸牌/岭上已满足）。executeActionOnCandidate Tsumo也不能用closedTiles.last当摸牌兜底。测试pon never grants tsumo before another tile has been drawn。
3. **伪造吃牌面子类型**：validateActionPreconditions DeclareChi匹配仅检查allowed.meld.type==CHI，漏了action.meld.type==CHI，导致copy(type=PON)也被Accepted。完整检查请求自身类型，与合法面子一致。测试chi rejects a forged meld type without recording a response。
4. **选择碰/吃也是放弃荣和**：只在action is Pass时标记振听不完整。如果窗口有DeclareRon而玩家选择任意非Ron响应（包括Pon/Chi/OpenKan），即放弃合法Ron，应标记同巡/立直振听。应用于手工和AI响应的三处相同逻辑；拒绝的请求不能标记。上面pon测试已补充sameTurnFuriten断言。

开发完成后只在 delivery/D.md 中追加本轮实际修改摘要（不需要重写100行报告），不要声称测试通过。Codex负责立即复测与最终平台构建。你仍用内置文件工具，不要调用RunCommand。本轮修复范围小且已经定位，无需重复阅读所有旧交付文件。

上一轮调用因Google云端连接EOF中断，未修改任何文件；本次是网络重试，不是重复已做工作。

再补两处同样位于GameStateMachine.kt的已实测问题，本轮一并修复（不需要改其他模块）：
5. RonWin/TsumoWin事件的han使用yakuResult.totalHan，漏了宝牌，导致UI翻数与实际付款不一致。改用candidate/evaluated.scoreResult.han（役满保留其0语义即可）；可给yakuNames附加宝牌数量但不改变有役判断。新增MatchIntegrationContractTest.win event reports total han including dora：固定4役翻+2宝牌=6翻30符，庄家自摸18000。
6. 第四位玩家打出首张牌时，executeActionOnCandidate Discard/Riichi立即把isFirstUninterruptedGo=false，导致随后checkFourWinds永远无法命中。将首巡结束标记延后到advanceToNextDrawInternal里的流局检查之后、下一位摸牌之前（副露/杠中断仍立即false）。新增WinContextIntegrationContractTest.four identical first wind discards要求第四张同风后直接FOUR_WINDS流局，没有第五次摸牌。不要放宽规则或删测试。

最新未过滤185项测试，7项失败，全部由上述6点覆盖。请完成两文件局部修复后交回Codex，不要再重复生成整个GameStateMachine。
