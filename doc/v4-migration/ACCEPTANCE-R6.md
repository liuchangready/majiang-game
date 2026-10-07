# R6 最终架构清理与验收（2026-10-07）

R6 已通过。V4 是默认单机、UDP 联机及展示入口；旧 TCP/V3 会话、宿主和客户端同步链已删除，无旧入口兼容壳。Anti/GLM 在 R5 前已因额度退出；用户要求“直接继续”后，Codex 与并行子代理完成 R5/R6，并由 Codex 独立运行本记录中的验收。工作区原有迁移改动均保留；本记录不代表已提交 Git。

## 最终运行证据

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 不筛选 `test :lwjgl3:compileKotlin :android:assembleDebug` | 978/978，失败/错误/跳过均 0；桌面编译与 Android 调试 APK 成功 | `build/v4-migration/R6-accepted-full-checks.log`、各模块 `build/test-results/test/TEST-*.xml` |
| 独立 V4 审查 | 188/188，失败/错误/跳过均 0；最终架构 6 项均通过 | `build/v4-migration/R6-final-independent-review.log`、`build/v4-review/server/test-results/test/TEST-*.xml` |
| 真实 LWJGL/GL 冒烟 | desktop、默认 V4 启动、round-session、riichi-choice、furiten、meld、meld-display、meld-choice、discard-input-gate、settlement-detail、end-tenpai，11/11 | `build/v4-migration/R6-final-gl-*.log` |
| 真实 UDP 产品路径 | 菜单连接、建房、列房、双人准备开赛、进入玩家牌桌、主动离房返回大厅通过；四席私密发牌、码牌恢复、顶号与重试回归通过 | `presentation/.../MenuScreenTest.kt`、`server/.../udp/UdpTableServerSmokeTest.kt`；上述全量 XML，定向日志 `R6-menu-real-udp-flow.log` |

全量模块计数：engine 340、protocol 140、client 125、server 121、presentation 252。Gradle 的 `NO-SOURCE`/配置检查任务状态不计作测试跳过。最终独立审查在新增最后一个 server 端点恢复测试前运行；此后未改生产或审查源码，该测试和最终全量 978 项已通过。

## 删除范围与替代

| 删除的 V3 链 | V4 替代与保留断言 |
| --- | --- |
| `engine` 的 `TableHost`、`SessionAccess`/本地访问绑定、`LocalTableRuntime`、`PlayerEndpoint`/`LocalPlayerEndpoint`、`GameSession`/`LocalGameSession` | `TableAuthority`、`ServerTableHost`、`InProcessServer`、`client.api.GameSession`；原宿主 20 项方法迁至 `ServerTableHostCoverageTest`，控制器 6 项迁至 `TableAuthorityControllerMigrationTest`。实际仍用的 access DTO、领域规则和 AI 策略保留。 |
| `client` 的 `GameClient`/旧同步器、TCP `ClientTransport`、`NetworkSessionAccess`、`RemoteSession`/`MallSession` 和空映射器 | `ServerConnectionSession`、有序客户端快照、UDP 传输；物理牌 ID、跨局事件、支付隔离、缺口恢复、关闭在途请求、坏 UDP/超时由 V4 client 测试与独立审查承接。 |
| `protocol` 旧帧/消息/JSON codec；`server` 的 TCP `TableServer`/`ServerRoom`/`ClientConnectionHandler` | V4 typed protocol 与 UDP/进程内同契约房间；真实四席 14/13/13/13 私密发牌、53 个唯一实体 ID、伪造选择拒绝、幂等重试、顶号失效及私有码牌恢复由 UDP 回归承接。 |
| `presentation` 旧 V3 screen/adapter、旧 GL smoke helper | `presentation.menu/game/common` 消费 `client.api`；菜单与牌桌、结算/振听/立直/鸣牌/弃牌动画和渲染测试保留，11 组真实 GL 冒烟按 V4 入口运行。 |

保留原地的有效规则测试包括 `TimeControlAcceptanceTest`、`ShuffleAlgorithmAcceptanceTest`、`ShuffleAtomicDealAcceptanceTest`、`MatchEconomyAcceptanceTest`、`CheatLedgerIntegrationAcceptanceTest`、`WinningScoreContractTest` 和 `ScoreDerivationContractTest`。它们仍核对 20+3 计时、暂停、136 张实体牌及死牌山、原子失败、跨局账本、符番和支付。没有按 `v3` 路径名批量删除领域算法或历史设计文档。

旧 `TableLifecycleAcceptanceTest` 与 `RecoveryProjectionAcceptanceTest` 中仍有效的边界另以 `ServerTableHostCoverageTest`、`ServerTableHostTest`、`InProcessBindingTest`、`V4RecoveryProjectionPreservationTest` 直接验证：非默认资源进入玩家快照、可信扣款主动推送且不重置决策时钟、断线接管与显式收回主动通知、端点关闭后恢复仍需显式收回、终局晚加入恢复完整结果/历史/算分、私有账本原因不进入公开协议、下一局清结算投影但保历史、旧选择拒绝，以及暂停新快照冻结时钟且恢复不赠时。旧查询接口的“分页拒绝”参数在 V4 已不存在，改验未知局查询为空及公开投影不泄露；TCP EOF/长度前缀行为随 TCP 协议一起撤销。

`V4FinalArchitectureTest` 核对实际 package/完全限定类型归属、生产依赖、展示门面公开泛型及旧类型缺席。最终生产依赖为 engine 独立领域、protocol 不依赖 engine、client 不依赖 engine/server、server 依赖 engine/protocol、展示画面仅使用 client.api；组合根持有 server 是刻意的装配边界。源码引用审计和 `git diff --check` 未发现旧产品链引用或空白错误；历史注释中的 V3 名称不是运行入口。
