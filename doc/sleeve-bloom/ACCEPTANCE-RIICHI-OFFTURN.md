# 袖里藏花立直与他家回合扩展验收

日期：2026-10-20。范围：[扩展设计](EXTENSION-RIICHI-OFFTURN-DESIGN.md) 的 engine / protocol / server / client / presentation 接线与回归。

## 结论

实现已完成并通过本轮自动化验证；未执行 Android 真机与跨物理网络实测，不将其记为通过。

- 全量测试：**1648 项通过，0 失败、0 错误、0 跳过**（engine 539、protocol 193、server 339、client 230、presentation 347）。
- 构建：`:lwjgl3:classes`、`:android:assembleDebug` 通过。
- 协议版本：`ApiProtocol.PROTOCOL_VERSION` 由 5 升至 6，`TcpWire.BUSINESS_VERSION` 由 2 升至 3；新增 OFF_TURN_OFFER/RESERVED 私有语义，旧业务版本在 HELLO/WELCOME 硬拒绝；MJ-TCP/1 12 字节帧格式不变。
- 真实桌面 GL 冒烟：1280×720 `SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=567`；960×540 同标记、`frames=567`（一次 960 运行等待第二次施术机会超时，随后复跑通过）。该脚本覆盖原有即时普通摸牌闭环，不覆盖他家回合/立直杠后的真实画面。
- 未完成：Android 真机触控、跨物理网络 TCP 实测、真实对局他家回合与立直暗杠的 GL 走查。

## 覆盖矩阵

| 设计要求 | 实现入口 | 自动化证据 |
|---|---|---|
| 他家回合 `windowId` 强类型激活 | `SleeveBloomOffTurnRequest`、`TableAuthority.submitSleeveBloomOffTurn`、`ServerTableHost.submitSleeveBloomOffTurn` | `SleeveBloomOffTurnTest`、`SleeveBloomServerContractTest` |
| offer `FIRST/SECOND` 选择 | `SleeveBloomOffTurnChoiceRequest`、协调器 `submitSleeveBloomOffTurnChoice` | 同上；未选牌保持 WALL、选中牌转预留 |
| 独立 10 秒权威截止 | `SleeveBloomOffer.deadlineMillis`、`TableAuthority.checkTimeout`、`ServerTableHost.tickInternal` | `authority deadline settles FIRST...`、`off-turn authority deadline...host tick` |
| 暂停冻结/恢复 | `ApplicationCoordinator.shiftSleeveBloomOffTurnDeadlines` | `pause freezes off-turn choice time...` |
| TilePool 活牌山锁定预留 | `GameState.availableWallIndices/availableWallTileCountFor`、`TileMovement.drawNormal/drawRinshan` | 他家摸牌跳过预留、岭上替补不偷尾端、交付只发生一次 |
| 跳过后解锁洞 | `availableWallIndices` 从 0 扫描并按 TilePool WALL 归属过滤；`swapHandWithWall` 清理手牌旧槽位 | offer 被他人摸牌越过后 FIRST/SECOND/超时解锁的第二/第一张可再次摸到；跨座 offer 交错；终局释放后全部实体仍可达 |
| 即时普通摸牌使用洞候选 | 即时 NORMAL 激活/选择改为 `nextAvailableWallIndex` + TilePool WALL；不再依赖 `wallPointer` 连续区间 | 跨座两个洞、普通摸走第一洞后，第二洞作为即时 offer 候选；FIRST/SECOND 均成功；`wallPointer==wallEndExclusive` 仍可激活 |
| 本人下一次普通摸牌交付 | `TileMovement.drawNormal` 预留分支、`RoundTransitionPlanner` 标记 `usedOnTurn` | `reservation survives other draws...`、`delivered reservation consumes...` |
| 立直可施术 | 移除 `SLEEVE_BLOOM_RIICHI_LOCKED` 拒绝；投影/入口继续按余额与候选启用 | `SleeveBloomContractTest` 立直正例 |
| 自动摸切不抢技能 | `TableAuthority.checkRiichiAutoDiscard` 在已付款即时 offer 存在时返回 null | `riichi auto discard waits...` |
| 真实和牌/暗杠按最终牌重算 | 选择后重建决策；即时 offer 另带两只候选各自的可胡/可杠私有预览，合法动作/预览沿用实际 `drawnTile` | `immediate offer previews tsumo...`、`immediate offer preview flags...`、既有 Kan 回归 |
| 杠后新补牌二次扣 10 | 新 `turnNumber` + `lastDrawKind=RINSHAN` + 费用检查 | 既有 `SleeveBloomKanDoraTest` 全绿 |
| 136 张实体守恒 | `StateInvariants` 锁定集合/墙区间审计 | 每个新增测试调用 `TestTableBuilder.assertPhysical` 与 `StateInvariants.assertValid` |
| 私有牌面不泄露 | 仅本人 `SleeveBloomView.pendingOffer/reservation`；公开投影无字段 | 引擎/服务端其他座位快照为空；协议公开 JSON 断言 |
| MJ-TCP/1 作用域幂等 | 新标签纳入 `isSideEffectApi`；`TableAuthority` 按 `(seat, requestId)` 保存载荷指纹 | 同载荷重放返回原回执；不同载荷同 ID `REQUEST_ID_CONFLICT` |
| 重连完整快照 | `PlayerSnapshot/ProtocolPlayerSnapshot/GameSnapshotView` 全量映射 | `SleeveBloomServerContractTest` 重连快照保留预留 |
| 表现层入口/面板/预留提示 | `SleeveBloomChoiceModel`、`SleeveBloomPanel`、`GameScreen` | `SleeveBloomChoiceModelContractTest`、既有 screen contract 全绿 |

## 主要改动文件

- engine：`GameState.kt`、`TileMovement.kt`、`RoundTransitionPlanner.kt`、`ApplicationCoordinator.kt`、`TableAuthority.kt`、`SleeveBloomSubmissionHandler.kt`、`SleeveBloomModels.kt`、`StateInvariants.kt`、`LegalActionProvider.kt`、`RiichiRuleEngine.kt`、`PlayerSnapshot.kt`。
- protocol：`ProtocolSleeveBloomModels.kt`、`ProtocolCheatAdapters.kt`、`ApiMessage.kt`、`ProtocolModels.kt`、`SessionApi.kt`。
- server：`ServerTableHost.kt`、`SharedRoom.kt`、`TcpRequestRouter.kt`、`InProcessBinding.kt`、`EngineProtocolMapper.kt`。
- client：`SleeveBloomModels.kt`、`GameSession.kt`、`ViewModels.kt`、`ProtocolViewMapper.kt`、`ServerConnectionSession.kt`、`TcpClientBinding.kt`。
- presentation：`GameScreen.kt`、`SleeveBloomChoiceModel.kt`、`SleeveBloomPanel.kt`、`MenuScreen.kt`。
- tests：`SleeveBloomOffTurnTest.kt`（新增）、`SleeveBloomContractTest.kt`、`GameStateFieldChecklistTest.kt`、`ProtocolSleeveBloomContractTest.kt`、`SleeveBloomServerContractTest.kt`、`SleeveBloomChoiceModelContractTest.kt`。

## 口径说明

- 活牌山仍以 `[wallPointer, wallEndExclusive)` 为审计区间。锁定/预留实体未被删除；`availableWallIndices()` 与 `drawNormal`/`drawRinshan` 共同提供跳过锁定槽位、按实体交付、尾端替补不偷预留的等价无洞抽取语义。
- 他家回合 offer 的 10 秒不是座位行动时钟，不写入 `DecisionClock`；`SleeveBloomOffer.deadlineMillis` 由权威单调时钟维护，暂停时整体顺延。
- 立直暗杠仍由 `RiichiKanValidator` 判定待牌/结构不变；加杠仍只对未立直玩家开放并走抢杠窗口。
- GL 冒烟只证明原有即时普通摸牌 UI 闭环，不代表本轮新增他家回合/立直暗杠场景已做真机或网络实测。
