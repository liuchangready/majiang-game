# R4B2 并行 B：独立 PUBLIC 领域投影

R4B1已经Codex正式接受，现在明确启动本任务。只新增engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt、PublicProjectionFactory.kt及engine同包测试文件，必要同包不可变辅助类型。另一个Anti改私有玩家预览跨层文件，禁止改TableAuthority/PlayerSnapshot/已有DTO/protocol/server/client/presentation/build/reviewer。后续独立批次接入权威宿主与观战网络，当前不伪造集成。文件工具开发，不shell/build/提交/再委派；额度自动交GLM，Codex测试。

## 冻结领域契约

对象PublicProjectionFactory提供：

```
project(session: SessionSnapshot, state: GameState?, roundId: String?, roundVersion: Long?,
        clocks: List<ClockView>, paused: Boolean,
        settlementRoundId: String? = null, settlementEvents: List<GameEvent> = emptyList(),
        revealSettlementUra: Boolean = false): PublicTableProjection
publicEvents(events: List<GameEvent>, revealSettlementUra: Boolean = false): List<GameEvent>
```

输入由后续集成在同一权威队列截取MatchSession.readState()和RoundStateMachine.readState()；纯函数，不读取PlayerView、querySnapshot(seat)、不伪造seat0/-1。不要给领域类型加入连接/网络/游标；server之后包装独立PUBLIC stream的cursor/syncEpoch。

输出PublicTableProjection：sessionId/sessionVersion/roundId/roundVersion，board:PublicBoardView?，session:PublicSessionSummary，clocks/paused/settlementRoundId/settlementEvents。state=null允许board=null，局间/终场信息来自session，不伪造桌面。

PublicBoardView包含phase、roundWind/roundNumber/honbaSticks/riichiSticks、dealerSeat/currentTurnPlayer、wallRemaining、doraIndicators、scores、riichiStatus、四家discards/melds/handTileCounts、lastDiscard/lastDiscardPlayer。暗牌张数=closedTiles.size+drawn是否存在，不含任何暗牌实体/袖牌/牌山顺序/未揭示ura/decision/options/准备选择或资源余额。

PublicSessionSummary包含lifecycle、scores、roundWind/roundNumber/dealerSeat/honba、roundRecords:List<PublicScoreRecord>、finalResult:PublicMatchResult?。PublicScoreRecord至少保留roundId/resultId/finalScores/deltas；PublicMatchResult保留finalScores/rankedSeats/reason；可补真实公开赛程/结算元数据，不能照搬包含私密字段的对象。已有SessionView/PublicRoundRecord/PublicSessionResult含finalCheatPoints/cheatDeltas，不可直接复用为观战历史。

## 隐私与不可变

- publicEvents采用明确事件类型白名单；流程、公开弃牌/副露/立直/和牌/流局/计分/终局、表宝、公开抓包允许；发牌、摸牌、岭上摸牌、成功千术、余额流水、私人窥视/警戒状态等丢弃。不能用SessionUpdate.filterVisibleEvents(events,-1)：其默认放行及RoundEnded授权ura会泄漏。
- ura只在调用者明确证明真实公开和牌结算并传revealSettlementUra=true时允许已公开DoraRevealed，不从state.uraDoraIndicators复制。DIVINE_SIGHT或普通RoundEnded本身不能授权。真实公开获胜者ScoreBreakdown可保留完整明示结算详情，不能把预览/他人私牌夹入。
- 每个公开DTO构造时深复制并不可变，包括四家list、meld.tiles、history、分数及事件评分深层集合；修改输入不能改变输出，getter不允许改。snapshotGameEvent等已验证纯复制工具可复用，但要确保实际不可变。
- 不升级依赖、不改原规则算法、不新增public-private互转兼容壳。

## 测试与交付

模块测试覆盖准备/局中/结算/历史；修改输入暗牌具体值、drawn实体、袖牌、牌山内部顺序、未公开ura、码牌选择/资源余额而保持公开计数相同时，公开输出必须不变。白名单独立验证私有事件被滤除，ura授权正负、评分详情保真、嵌套集合immutable与防御拷贝。

交付delivery-ANTI-R4B2PUBLIC.md，列完整最终字段/白名单与后续宿主调用方式，明确未运行测试且尚未接线；结束前读FEEDBACK-R4B2PUBLIC.md。不能宣称整个观战工作流完成，不推进R5/R6。
