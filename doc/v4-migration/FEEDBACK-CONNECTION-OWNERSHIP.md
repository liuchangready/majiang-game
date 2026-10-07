# Codex 初审：代码等价，文档需精确返修

继续由 Anti 修改文档。上一进程已退出，生产迁移两文件剥离包名/注释后与 HEAD 完全等价，既有测试仅改 import，新增9测试合理。Codex 现在负责删除旧占位文件和运行构建。**本批严格冻结所有生产代码/测试/Gradle，只修改下面五份文档与交付记录**，不需要重新全库阅读，不运行命令。直接依据以下源码核实事实修正，完成后退出。

## 必须纠正（五份当前文档一起查）

1. `ClientMapper` 不存在。真实类为 `client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt` 的 `ProtocolViewMapper`；`ServerConnectionSession` 也在 `client/api`，client 没有 `mapping/` 或 `sync/` 目录；`PushSequencer` 在 `protocol/transport/udp`。FRAMEWORK源码树必须反映真实位置，presentation 内目录是 `com/majiang/presentation/menu|game|common`，MahjongGame 是 `com/majiang/MahjongGame.kt`。模型是 PlayerHand，不是 Hand。client模块不拥有服务端 InProcessBinding（该实现属于server）。UI方法是 GameSession.submit，不是 submitAction。client时钟类型是 ClockInfo，不是engine的ClockView。
2. 当前无 AnimationQueue 类。FRAMEWORK删掉该已实现宣称，可写 GameScreen 消费 GameUpdateView 的完整 snapshot + visibleEvents，DiscardAnimation/TileDrawBuffer 等驱动显示；不宣称已实现所有出千/鸣牌动画。
3. README 删除 `动作 > 超时 > AI 托管 > 控制权切换`。统一写串行处理、玩家提交校验deadline、tick先处理超时再处理AI，动画只门控自动节奏。不要写所有同桌请求总共只能成功一个（不同决策可依次成功；响应窗口也允许多个座位记录响应）。
4. AGENTS 的 `256窗口有序同步` 太含混：256是服务端推送重传窗口；client运行时 maxBufferSize=64，去重已应用游标集合256。可以只写有界缓存/有序同步，别混淆窗口。presentation仅client.api约束只针对menu/game/common，组合根允许server且Gradle真实存在该依赖。
5. 两份架构文档的状态图必须纠正，建议将三种生命周期分别画独立图/独立标识避免FINISHED重名。SessionLifecycle 无 IN_ROUND，真实为 ROUND_ACTIVE；还有 CREATED、WAITING_START、ROUND_RESULT、WAITING_DEALER_CHOICE、WAITING_READY、FINISHED、PAUSED、CLOSED（不要求画出未走的每个枚举值，但不能发明状态）。下一局按配置可经PREPARING，不要只画SETTLED->PLAYING；未启用码牌可LOBBY->PLAYING；终场也可PLAYING->FINISHED，房主离房任何阶段可CLOSED。
6. 实际 RoundTransitionPlanner：发牌一次操作内经过DEALING后直接CHEAT_WINDOW（庄家已有14张）；普通摸牌DRAW->CHEAT_WINDOW；CHEAT_WINDOW/DISCARD出牌->RESPONSE_WINDOW；响应吃碰后进入DISCARD；无人响应则下家摸牌（内部DRAW后CHEAT_WINDOW）；加杠由当前玩家操作打开CHANKAN_WINDOW，而非RESPONSE_WINDOW->CHANKAN_WINDOW。CHANKAN_WINDOW荣和->ROUND_END，全员过->KAN_DRAW->CHEAT_WINDOW；普通响应大明杠->KAN_DRAW；自摸CHEAT_WINDOW->ROUND_END、荒牌流局也终局。RoundStateMachine终局无回边，新一局由Coordinator创建新实例。可以画简化主要路径，但要标注DEALING/DRAW/KAN_DRAW可能为一次提交中的中间阶段，不照抄旧GamePhase.allowedTransitions。
7. SERVER_CLIENT_API首图把mapper画成调用API的中介不准确：ServerConnectionSession分别使用ProtocolViewMapper和ServerConnection接口；InProcessBinding与UdpClientBinding实现ServerConnection接口。protocol不是运行节点中转。注释或文字明确com.majiang.server.api仍属于protocol模块。
8. SERVER_CLIENT_API离房：只有主动离房成功者保持未绑定大厅；房主离房时其他成员收到ROOM_CLOSED并终结连接，绝不是所有成员连接都回未绑定大厅。SpectatorBinding无seat字段，不得写 seat=-1；独立spectatorId与公开快照。观战禁止玩家写操作但仍可leaveRoom/查询房间。对外claim/release无布尔；不要说V4彻底删除内部requestControl(Boolean)，ConnectionControl内部仍有该方法。
9. 删除GAME_ARCHITECTURE_V4中的“局域网发现”已实现宣称。当前是指定服务器地址/端口后的列表及房间号加入，无自动发现。
10. FRAMEWORK不写“任意出千/声东击西产生绝对安全窗口”，只写改变视线，合法性与结果由领域规则裁决。守恒审计不能简化成tileMap.size==136：TilePool绑定状态时调用StateInvariants审计真实各区域容器、唯一实体归属和账本一致性；保留这一关键约束。不要用“作弊穿帮/抓包”代指内部守恒审计失败。
11. 本次在验收前不宣称通过，在文档/交付记录注明R6为历史已验收，本次迁移由Codex另行验收。交付记录待删除动作由Codex执行，无需保留危险的递归删目录示例；改为两个精确文件加仅确认空目录后删除。

框架图和协议细节能保留已正确的22字节头、1200/1090、3s/10s、256、独立投影游标等。不要扩大为规则重写，不更改有效现存源码。新增内容优先简洁准确，不需要增加更多推测的类名或行为。完成后更新 DELIVERY-ANTI-CONNECTION-OWNERSHIP.md 的返修记录。
