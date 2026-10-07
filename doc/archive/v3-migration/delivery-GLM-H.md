# GLM-H 交付：宿主权威整合收尾与编译修复

范围：`engine/src/main/kotlin/com/majiang/{host,access,timing,control,application,round,match,economy,state,event}/**`。
未触碰 client/core/server/tests/build。本交付只改代码；编译与测试由 Codex 执行，本文不声称构建通过。

## 1. 编译修复（真实语法错误）

`engine/src/main/kotlin/com/majiang/host/TableHost.kt` 的 `tick()` 由表达式体改为块体时，
`return withSerialLock { ... }` 结尾只闭合了一层括号（闭的是 lambda），函数体本身少一个 `}`。
后果与 `build/v3-migration/post-migration-compile.log` 完全对应：

- `TableHost.kt:1300:2 Syntax error: Missing '}'`；
- 其后所有成员被解析为 `tick` 的局部函数（日志中大量 "Modifier 'private'/'internal'/'override' is not
  applicable to 'local function'"、`Class 'TableHost' is not abstract and does not implement fun close()`）；
- `access/local/LocalSession.kt` 的全部 `Unresolved reference`（querySnapshot/submitChoice/closeSession 等）
  均为该语法错误的级联——LocalSession 调用的成员在 TableHost 中真实存在，修复括号后即解析。

修复：补齐 `tick()` 的函数闭合括号并归位缩进（现 `return@withSerialLock false` 在 lambda 内 12 空格缩进，
随后 8 空格闭 lambda、4 空格闭函数）。

## 2. D5 未完成项收尾

D5 清单逐项核对了当前源码后再动手，避免重复实现：

1. **已在前次实现、本次仅核验**（未改动）：
   - `controllers.map { it.deepCopy() }` 已替换为 `Collections.unmodifiableList(controllers.toList())`；
   - `BoundMahjongGameAccess.snapshot()` 无缓存旧快照时已返回 `ROUND_NOT_ACTIVE` 异常 future，
     不再把新局快照改写 roundId 伪装旧局；缓存旧快照的 decision.options 与 view.legalActions 均清空；
   - 私有 `disconnectLocked(seat)` 已存在：公开 `disconnect` 带重入守卫，可信内部路径
     （`closeSession` 最后端点关闭）直接调用 `disconnectLocked`，重入守卫不再吞掉合法内部掉线；
     两个端点关闭其一时 `activeSeatSessions[seat]` 非空，不触发掉线（client
     TableLifecycleAcceptanceTest "closing one endpoint" 场景）。
2. **暂停/时钟投影（本次完成）**：`pause()`/`resume()` 现在在串行锁内于状态真实翻转时调用
   `onStateChanged(emptyList())` 发布时钟投影。恢复时 deadline 平移（DecisionClock.resume 已有实现）
   会作为有意义时钟差异推送游标增长（HostRhythm "local pause freezes..." 断言 73000 = 50000 暂停时长 + 23000）。
   暂停抑制面核验：`tick`、`submitChoice`、`startSession`、`signalNextRoundReady` 均已拒绝/抑制
   （tick 首行 `decisionClock.isPausedState` 短路），本次新增的记账入口同样拒绝（见 §3）。
3. **闲置时钟稳定标记（本次完成）**：`buildClockViews()` 无活跃计时的座位原先用 `opensAt=now/deadline=now`，
   纯墙时间流逝会被 `hasMeaningfulClockDifference` 误判为状态变化而推进游标。现改为稳定 `0/0` 空闲标记；
   core 的 `ClockDisplay` 本就以 `deadline > opensAt` 判定"无活跃计时"，与该标记天然兼容。
   `serverNow` 保持真实采样（不参与变更比较）。
4. **删除无引用的重复 LocalSessionAccess（内容已清空）**：全仓 grep 确认
   `access/LocalSessionAccess.kt` 无任何生产/测试引用（仅 doc 提及）。我的工具集没有 unlink 能力，
   已将该文件清空为 0 字节（空文件是合法编译单元）。**请 Codex 执行 `git rm
   engine/src/main/kotlin/com/majiang/access/LocalSessionAccess.kt` 完成物理删除。**

## 3. 可信宿主侧出千记账入口（GLM-H 新增）

- `TableHost.applyCheatLedgerEntry(entryId: String, seat: Int, amount: Int, reason: String): ActionResult`：
  串行锁内转发 `ApplicationCoordinator.applyCheatLedgerEntry`（Coordinator 的权威账本操作，含跨局幂等、
  负余额/溢出拒绝、不变量审计）。仅成功（含同载荷幂等重放）时经 `onStateChanged(result.events)` 发布
  已提交状态与事件；拒绝时零发布、游标不动。已关闭、重入或本地暂停期间返回
  `Rejected(ACTION_NOT_AVAILABLE)`。
- **不刷新当前决策截止时间**：Coordinator 的回合决策键为 `"turn-${round.roundId}-${round.version}-$seat"`
  （ApplicationCoordinator.queryDecision），账本成功提交会提升 `round.version` 使 decisionId 变化，
  若直接走 `syncDecisionsAndClocks` 会 cancel+open 一个带全新免费时间的计时。
  新增 `DecisionClock.rekey(oldKey, newKey)`：同座位、键匹配时原子替换决策键，严格保留
  opensAt/deadline/generation 之外全部时序。宿主记账路径在发布前把旧键迁移到新 decisionId
  （仅当同局且新决策仍有选项），随后的 sync 看到键一致而不重开。client
  TableLifecycleAcceptanceTest "trusted host ledger debit" 断言记账后 deadline 仍为 23000、
  游标增长、重复与透支不推游标——实现按此语义编写。
- `SessionAccess` 接口无任何资源增减能力（核验过接口与 LocalSession 实现），资源变更只能走宿主受信入口。

## 4. 顺带修正（host 内小缺陷）

- `requestControl(false)` 收回控制权时原先统一安装 `SeatController.LocalHuman()`（默认
  `autoRiichiDiscard=true`），会把 `RemoteHuman(false)` 等座位级偏好静默打开。现恢复该座位初始配置的
  真人控制者（LocalHuman/RemoteHuman 原样恢复，其余兜底 LocalHuman），保留偏好。
- 删除 TableHost 内无引用的死字段 `epochCounter` 及其 `AtomicLong` import。

## 5. 核验结论（逐条对照任务清单，读实际声明验证，无反射/伪造 API）

- 真实初始发牌：`startSession` 依据 round 实际发牌状态幂等，`coordinator.start()` 真实发牌（14/13 张断言）。
- epoch 令牌：controlEpoch 来自 ControllerRegistry/ConnectionControl，接管/收回自增、无变化不增。
- deadline 语义：DecisionClock open/complete/expire/cancel/pause/reserve 语义与 TimeControlAcceptanceTest
  全部断言一致（该文件为直接单测，无需改动）。
- 本地暂停抑制一切玩法：见 §2.2；`requestControl` 属控制面（不改牌局状态）未拦截，已在文档注明。
- 旧局绑定不重贴标签：BoundMahjongGameAccess 行为核验通过（§2.1）。
- 真实最后连接掉线：`closeSession` → `disconnectLocked` 按政策激活接管；两连一关不触发。
- 首个更新游标增长：onStateChanged 仅在可见变化时 cursor+1 并推送；提交成功路径游标必增。
- 每 tick 150ms 真实自动推进：`rhythmIntervalMillis` 门控（149 拒 /150 行），长帧不追赶；
  `presentationBusy` 不推迟到期裁决（timeout 分支在门控之前）。
- AI 自摸/杠偏好：BasicAIStrategy 优先 Ron/Tsumo、次选暗杠（ai/** 非本人范围，未改动，仅核验）；
  真人立直自动摸切仅在无自摸/暗杠选项时触发。
- 无局间相位死锁：tick 处理 WAITING_DEALER_CHOICE 自动化庄家决策与全就绪下一局推进。
- 宿主权威状态不外露：SessionAccess/LocalSession/MahjongGameAccess 均只暴露视图与快照，GameState
  不越过边界。
- 配置防御性复制：TableHost.config（scores/winds/tieBreakSeats/cheatEconomy 点数与费用表）、controllers
  不可变列表；TimeControlConfig/DisconnectPolicy 为不可变 data class。

## 6. 限制与待办（不属于本交付）

1. 未执行任何构建/测试命令——由 Codex 运行 `:engine:compileKotlin` 起步，随后全量测试。
2. `LocalSessionAccess.kt` 已清空待 `git rm`（见 §2.4）。
3. `TableHost` 中 `MatchSession`/`RoundStateMachine` import 仅出现在 KDoc，属迁移前遗留的无害冗余，未清理。
4. ClockView 无 paused 字段：暂停发布通常不产生增量更新（无可见变化是诚实行为）；如需向客户端投影
   "已暂停" 状态，须扩 PlayerSnapshot/ClockView DTO——与协议层（N2）归属冲突，未擅自扩展。
5. F2/N2/N3（客户端 Future 线程、协议 DTO、异步传输）由其他任务负责，本交付未触碰。
