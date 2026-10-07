# D 第一轮整合审查：不通过，必须修复后交回

Codex 已实际执行未过滤 `:core:test --rerun-tasks`：168 tests，7 failed。日志 build/architecture-v2/integration-test.log，逐例 XML 位于 core/build/test-results/test。测试为 Codex 所有，不得修改。你仍仅用文件工具开发，构建由 Codex 负责。

## 核心事务/窗口问题（优先修复）

1. 当前 `handleAction(DeclareRon)` 直接进入 executeActionOnCandidate 结算，之后才进入 postCommitProgression 记录响应：严重违反先收齐响应再裁决。EngineProtocolContractTest 的双家荣和要求第一个响应后 scores 不动、无 Win/ScoreSettled，最后只结算离打牌者最近者一次。抢杠的 Ron 更因依赖 lastDiscard 而被拒绝。响应阶段的 Ron/Pon/Chi/OpenKan/Pass 必须走统一【仅记录响应→完整后唯一裁决】分支；目标/来源取 ResponseWindow，不依赖过期 lastDiscard。Pon/Chi 当前 executeActionOnCandidate 无分支直接 false，BasicAI 正常选碰也被拒绝（seed=7 已复现）。
2. isProcessingAction 在 publish 前 finally=false，监听器可以递归提交合法动作；DealingComplete 监听器尝试合法出牌的测试证明漏洞。防重入必须覆盖整个外层命令（含发布/自动推进），内部 AI/摸牌不要递归调用公开 handleAction 后被自己的锁阻挡：使用内部执行函数/队列，公开入口单一 guard。每个公共命令返回的 Accepted.events 应包括它触发的所有自动摸打、响应裁决、结算事件，顺序与 bus 一致。
3. 当前后置流程直接修改 state、currentResponseWindow、nextRoundMetadata，异常会在主状态已提交后发生。要求所有合法动作先完整校验具体物理牌与组合，处理窗口和强制推进后再提交/发布；不能靠 catch(Throwable) 把程序不变量错误伪装成普通 Rejected。至少确保拒绝时状态、窗口响应、资金、事件、元数据均不动。内部不变量损坏要明确抛出，与玩家非法操作区分。
4. validateActionPreconditions 的 `containsAll`/只匹配 kanType 太宽：空列表、重复牌、错误牌组、伪造 fromPlayer/calledTile 均可能通过；统一与合法动作按完整语义相等匹配（牌组可无序但身份/数量/类型必须完全一致）。窗口第二次响应需拒绝。
5. OpenKan提交遗漏 currentPlayer=player，必须设置再审计/岭上摸牌。任何杠仅在 wallRemaining>0、rinshanPointer<4 时提供，禁止提供执行必失败的“合法动作”。明杠和暗/加杠后都清除全家一发并标记首巡中断。

## 独立测试已证实的边界漏洞

6. LocalGameSession 的 start/submit/nextRound 原样返回引擎 Accepted.events，泄露全部发牌；三条返回通道必须与订阅相同按 viewerSeat 过滤，并防御复制。订阅者之间也必须隔离 IntArray。闭包订阅初次回调也不能把异常抛出破坏订阅流程。
7. GameEventBus 的 history、发布者、所有监听器共享同一个事件数组：EventIsolationContractTest 修改第一监听器 newScores[0] 后第二个收到 -999。发布时保存独立快照，逐监听器独立快照，getHistory 每次也返回深快照。不要引入 event→session 反向层依赖；可提取领域事件快照函数到 event 包供 SessionUpdate 复用。
8. WinningScoreEvaluator.countDora 当前 count{type in targets} 会把相同指示牌去重：2张5万 + 3张3筒，对两个4万指示和一个2筒指示应7翻，不是5；再加一个4万里宝应9。按每个指示牌逐张累计。

## 静态审查发现、不能等随机测试碰到

9. StartStacking 的非空 stackingPlans 当前被静默忽略。原项目也没有已实现的码牌服务，本轮不扩张开发码牌算法；对于非空且未支持的计划明确返回 UNSUPPORTED_ACTION，不能假装成功。空计划保持随机注入可复现与现有发牌行为。
10. StartStacking(ROUND_END) 与 nextRound 应采用同一推进路径，不能一个应用 nextRoundMetadata 一个无视它。MatchFinished/AgariYame 发奖剩余供托后必须 riichiSticks=0，保证总钱不增加；MatchEnded一次且终局不再接受下一局。
11. 当前 uraDoraIndicators 从未填充，立直和牌漏里宝；isFirstUninterruptedGo 从不更新，buildWinContext 的 isDoubleRiichi 依赖当前河为空，打完立直宣言牌后必为 false，导致两立直无法计分。请让首巡/天和地和/两立直/岭上/抢杠上下文有明确来源（必要的每座位标记进入 GameState 并深拷贝/重置）；副露中断必须生效。无需扩张到全部日麻变体，但不能破坏原有规则。
12. GameScreen 已加动作按钮后检查副露 UI 不再依赖旧 public state/fsm；按钮包括 Pass、吃、碰、三种杠、立直、九种九牌、可选终了。返回菜单应释放会话订阅。

请先完整读失败 XML 与当前新测试，再修正生产实现。更新 delivery/D.md，说明真实已改内容；你未运行测试，不能声称测试通过。完成后停止写文件，交 Codex 复测。

## 本轮并行所有权及最新结果

最新 Codex 实测为 173 tests，9 failed（已修正两项夹具问题，新增伪造加杠/场次/订阅隔离）。B 并行修复上述第 8 项 WinningScoreEvaluator，C 并行修复第 6、7 项所有事件/会话文件；D 禁止编辑 B/C 文件！你负责其他问题和生产 state/rule context/model/UI，具体见 TASKS.md 最后章节。

初次 delivery/D.md 包含不存在的 enum 值、构造器参数名和“100%完整”错误断言，更新交付文档必须与实际源代码一致；未跑测试不要写通过。已经编译成功不等于逻辑符合契约。

补充合法性：碰/吃后有14张等效牌但没有真实 drawnTile，不能提供自摸；当前 LegalActionProvider 的 `currentDrawn != null || allClosed.size % 3 == 2` 会允许碰后伪造自摸。新增 EngineProtocolContractTest.pon never grants tsumo before another tile has been drawn 已固定这个场景。真实发牌庄家/普通摸牌/岭上均有 drawnTile，自摸必须来源于真实摸牌。

Codex 新增真实隐藏 LWJGL/OpenGL 冒烟入口 tools/architecture/DesktopRenderSmoke.java，当前成功绘制并截图后，在 UI 会话的合法 DeclarePon 被 INVALID_COMBINATION 拒绝处失败（render-smoke.log）；与混合 AI 测试同一引擎缺陷。修复后由 Codex 重跑。最终全量测试现为182项，其中13项引擎相关失败，B/C模块已通过复测。

15:15 第二轮引擎代码的即时复测：182项中6失败，其中2项为Codex夹具问题已修正（UI无役场景改完整136张；“不听牌”手牌误放了国士十三面已改中张破听）。剩余4项：
- 3个加杠无人抢的用例现在抛 Player 0 does not hold 5m copyIndex=0；openResponseWindow 的 targetTile 错取 pendingKan.tiles.first()（是原Pon牌），应从实际玩家hand.closedTiles/drawnTile识别新增的第4张（fixture为5m copyIndex=3），不能依赖列表首尾，也不能改变逻辑类型后忽略物理身份。
- 碰后未摸牌却仍生成 DeclareTsumo；LegalActionProvider 必须要求 currentDrawn != null，见上条补充及 EngineProtocolContractTest。
其他重大缺陷（响应等待/头跳/重入/非法组合/连续AI/场次推进/两立直/岭上/不支持计划拒绝）已在真实复测中通过。D请在交付前一并修复上述两处，不要修改测试。
