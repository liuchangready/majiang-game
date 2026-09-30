# AI、本地玩家与未来其他玩家的操作能力审查

日期：2026-09-30。依据当前生产代码；联机设计文档中的计划不视为已实现功能。本次只审查与运行既有测试，未修改生产代码。

后续进展：本文保留改造前的审查结论。逐座位控制者与统一动作执行现已完成，最新实现和验证见 `doc/seat-controllers/DESIGN.md` 与 `doc/seat-controllers/REVIEW.md`；网络通信等其他缺口仍按各自范围另行实施。

## 结论

普通麻将合法动作、规则计算和公开信息投影已经具备共用基础，但控制者模型、命令执行入口、局末决策和多人会话仍不统一。当前默认运行路径是一个本地真人加三个基础 AI；尚不能直接将某个 AI 替换为远端真人。

## 已共用的能力

- `LegalActionProvider` 按座位、阶段和牌面生成动作，不按真人或 AI 给予额外吃碰杠、立直、和牌或九种九牌权限。
- `ResponseResolver` 收齐有资格座位的响应后裁决：荣和高于碰/大明杠，高于吃；同级按座次头跳。AI 先算出答案不会因此获得优先权。
- AI 策略只收到自身 `PlayerView` 和合法动作。与界面一样，它看不到他家暗牌、牌山顺序和未公开里宝牌。此结论针对现有策略数据接口，不代表本地进程对不可信插件形成安全沙箱。
- 规则、振听、和牌与结算服务共用。已有座位投影可以用于未来其他玩家，但尚无生产网络会话实现。

## 差异与缺口

### 1. 单真人控制者模型（多人接入前必须处理）

`GameStateMachine.humanPlayerIndex` 是单个整数。`initializeMatch` 为其他三个座位安装 AI；自动响应、自动出牌及立直自动摸切均据此分流。第二名真人若只增加一个客户端，会仍被当成 AI 自动决策。

来源：`state/GameStateMachine.kt:43`、`:79`、`:381`、`:470`、`:751`。

应改为逐座位控制者配置，区分本地真人、远端真人、AI 和临时托管；控制者切换不改变座位的规则权限。

### 2. 合法选择相同，实际策略不同（大部分属于合理差异）

真人可以选择界面展示的合法动作，也可以放弃和牌。默认 `BasicAIStrategy` 有和必和，有九种九牌机会就选择流局；有立直就按理论待牌种类数量挑选方案；主动选择暗杠和役牌碰，不主动吃、大明杠或加杠。其立直评估未利用界面已有的完整振听预览，也没有按公开剩余张数、得点期望或防守风险评价。

`TrivialAIStrategy` 通常摸切、不主动鸣牌立直，但默认开局并未使用它。`AdvancedAIStrategy` 当前只是委托基础 AI，没有独立高级算法。

来源：`ai/AIStrategy.kt:41`、`ai/TrivialAIStrategy.kt:16`、`ai/AIPlayer.kt:15`。

应保留策略自由度，同时让各种控制者消费同一套可选方案和辅助分析数据。

### 3. AI 内部执行未完整复用外部校验（需要收敛）

真人经过 `LocalGameSession.submit` 的座位限制和 `handleAction` 的前置检查。AI 回合从 `triggerAiTurnInternal` 直接执行候选动作；AI 响应直接调用 `ResponseWindow.recordResponse`。后者只检查该座位有资格且未重复响应，不验证动作本身属于该座位的合法集合。

现有内置策略按合法列表选动作，因此不能据此声称当前 AI 已经进行了非法操作。但未来替换策略时，错误返回值不会得到与外部提交完全相同的拒绝保护。物理守恒检查不能替代动作权限和规则校验。

来源：`session/LocalGameSession.kt:64`、`state/GameStateMachine.kt:222`、`:385`、`:819`、`state/ResponseWindow.kt:45`。

应共用内部校验及提交函数，并由调度器串行调用；不能简单在现有处理过程内递归调用带重入守卫的公开入口。

### 4. 等待节奏与立直自动化不同（单机可用，联机需要明确协议）

AI 在一次提交调用中同步决策，可连续推进多个回合；真人等待界面点击。最新的出牌队列门禁仅存在于 `GameScreen`，AI 和规则引擎不会等待动画。

真人立直后，无合法自摸/暗杠时自动摸切；有选择时暂停。基础 AI 则立即按自己的优先级选择和牌或暗杠。该策略差异合理，但自动摸切目前只按唯一的 `humanPlayerIndex` 启用。

联机需要定义服务器行动截止时间、客户端演出与可操作时间的关系，避免客户端动画消耗掉玩家全部响应时间。无需让权威规则引擎依赖某台机器的渲染帧。

来源：`screen/GameScreen.kt:258`、`:2515`、`state/GameStateMachine.kt:778`、`:819`。

### 5. 局末结束与下一局控制不平权（实际流程缺口）

合法动作支持符合条件的尾庄提交 `DeclareAgariYame`，真人界面也有对应入口。但 AI 调度只处理出牌阶段和响应窗口，没有在 `ROUND_END` 调用 AI 决策；因此默认 AI 不会主动执行该结束选择。

下一局由本地会话的 `nextRound()` 推进，没有四个座位的准备/确认模型。如果直接移植到多人界面，无法保证其他玩家仍在查看结算时不会被某一方推进。

来源：`state/LegalActionProvider.kt:35`、`state/GameStateMachine.kt:132`、`:819`、`session/LocalGameSession.kt:88`、`screen/GameScreen.kt:2474`。

应补齐局末庄家决策，并把结束选择与玩家确认结算/下一局准备分开。

### 6. 其他玩家缺少共享会话与网络命令保障（尚未实现）

目前生产实现只有 `LocalGameSession`，没有 `OnlineGameSession`、共享房间会话或网络模块。LocalGameSession 的更新由其自身 start/submit/nextRound 通知本实例订阅者；即便四个实例注入同一引擎，也没有自动向四个视角分发更新的中央机制。

动作没有请求编号、期望状态版本或响应窗口编号；内部窗口虽有 id，客户端提交并不携带它。迟到的 Pass 等命令若在新窗口仍合法，现有动作结构不足以判断它原本属于旧窗口。也没有超时、断线、重连与托管交接流程。

`isProcessingAction` 是普通布尔重入守卫，不是多线程串行队列或原子锁；联网回调不应直接并发调用引擎。

来源：`session/GameSession.kt`、`session/LocalGameSession.kt:120`、`session/SessionUpdate.kt:15`、`state/GameAction.kt`、`state/GameStateMachine.kt:74`、`settings.gradle`。

应增加统一对局调度与按座位广播，所有人通过身份绑定的端点提交；加入命令幂等、版本/窗口校验和断线托管协议。

### 7. 千术与抓包需要区分实现和设计

当前 `UseCheatTechnique` 在状态机入口明确返回 `UNSUPPORTED_ACTION`，合法动作不提供千术；执行器和怀疑度系统虽存在，但生产开局没有接线。因此当前本人和 AI 都不能通过正式会话正常使用四大千术。

联机文档 6.2 明确设计抓包仅由 AI 观察者触发，真人没有抓包操作。若今后按该设计接入千术，AI 与真人会存在有意的能力差异：纯真人局无抓包风险，声东击西也没有 AI 视线可操纵。这是既有设计取舍，不能当作已经上线的行为，也不在本次审查中擅自改变。

来源：`state/GameStateMachine.kt:246`、`cheat/CheatActionExecutor.kt`、`cheat/SuspicionSystem.kt`、`doc/NETWORK_LOBBY_DESIGN.md:460`。

### 8. 所有人共有的选牌完整性限制

碰的候选取同类手牌前两张；吃的每种顺子结构只找第一组物理牌。若红五和普通五共存，可能没有同时提供“使用红五”和“保留红五”的全部候选。界面只展示合法列表，AI 也从该列表选择，所以这是双方共有的能力缺口。

来源：`state/LegalActionProvider.kt:199`、`rules/RiichiRuleEngine.kt:146`、`session/MeldChoiceProvider.kt:49`。

应在规则动作生成层枚举会影响红宝牌保留的有效组合，再由界面和 AI 共用。无需仅为普通同色同点、结果完全等价的物理副本增加重复界面选项。

## 建议实施顺序

1. 逐座位控制者 + 共用动作校验提交 + 串行调度。
2. AI 局末决策 + 多人结算确认 + 按座位配置自动操作。
3. 共享会话广播 + 网络命令编号/窗口/版本 + 超时重连托管。
4. 红五鸣牌组合完整性 + AI 评估增强；千术按既有设计单独接线验收。

保留现有 `GameAction`、`PlayerView`、规则服务与响应仲裁基础，不需要推翻已完成的麻将规则。

## 验证范围

运行 ArchitectureBoundaryContractTest、CommandValidationContractTest、LocalGameSessionContractTest、ResponseResolverContractTest、RiichiFlowContractTest、MixedAiInvariantContractTest、SessionSnapshotContractTest，共 50 项通过，0 失败、0 错误。日志：`build/player-capability-audit-tests.log`。

这些测试验证现有边界、规则与不变量；没有把不存在的多人通信、异常 AI 或断线场景当成已通过测试。上述缺口中未另作运行复现的部分，依据标明的生产代码路径进行静态判断。
