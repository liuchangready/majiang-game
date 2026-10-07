# E3 最后两个展示工具与helper完善

只修改tools/architecture/SmokeScreenClient.java、DiscardInputGateRenderSmoke.java、SettlementDetailRenderSmoke.java与discard-input-gate-smoke.init.gradle、settlement-detail-smoke.init.gradle。其余工具已交其他Anti，不改。helper需保持现有API方便已迁工具编译。

两工具的PreviewSession extends SmokeScreenClient 替换GameSession，game.changeScreen(new GameScreen(game,session,null))替换launchGame(session)，各init.gradle编译加入helper。DiscardInputGate的choose应保留原真实视图变更（4家新增牌河）并通过super.update广播，返回ActionReceipt；submitted只加一次。SettlementDetail的emit多种算分事件直接使用helper update(List<GameEvent>)。

异步调整：DiscardInputGate同帧双击仍应只提交一次（由isSubmitting门控），但snapshot驱动的isDiscardPlaybackBusy须下一帧检查；所有4次动画、播放中理牌/打牌锁定、解锁后第二次提交等断言都保留，不能清动画或直接跳到结果。SettlementDetail emitRon后下一帧再点CALC标签；点击下一局后下一帧断言settlement隐藏。保留全部截图、字体glyph/文本换行/分页/役满/满贯/legacy覆盖。

helper 当前 createSnapshot 固定smoke-round，完善round identity：开始新场景的StackingStarted换ID并清旧settlement；update(view,events)里终局事件保留到后续无事件刷新；单独update(events)表示该工具新展示的独立结算场景，每次新win应新settlement ID并替换旧events，ScoreSettled单独到达应合并而非新ID。nextRound fake成功应发布无settlement快照，且不能复制旧events。快照/事件防御性deepCopy，普通choose验证legalActions（现有工具需提交动作应在view legalActions里）。不得吞异常或返回假成功绕过断言。

不改core/engine，不执行shell，不读其它算法/历史。写delivery-ANTI-V2FIX-E3.md。
