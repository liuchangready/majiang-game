# V3 当前代码结构与运行方式

2026-10-01。目标规范见 [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)，实际验收状态见 [STATUS.md](STATUS.md)。本文件描述已经接入的代码路径，不把目标设计中的所有部署能力当作完成。

## 模块与职责

| 模块 | 内容 | 依赖 |
|---|---|---|
| engine | RoundStateMachine、MatchSession、规则/结算/验证、ApplicationCoordinator、TableHost、计时、控制、资源账本、共享 access DTO 与 LocalSession | Kotlin/JVM，不依赖 libGDX、client 或 server |
| client | GameClient、ClientSynchronizer、协议 JSON、TCP 客户端、RemoteSession/MallSession | engine |
| presentation | MahjongGame 组合根、GameScreen、展示桥接、皮肤、动画和交互（原 core 模块，2026-10-05 更名，包名仍为 com.majiang.*） | engine、client、libGDX |
| server | TableServer、ServerRoom、连接与房间生命周期、独立启动入口 | engine、client；无图形依赖 |
| lwjgl3 / android | 平台窗口与启动 | presentation |

包名保留 `com.majiang.*`，机械迁移不产生两份领域实现。共享访问协议放在 engine，避免本地宿主反向依赖 client。

## 权威链路

默认本地入口为 `MahjongGame.launchGame()` → `TableHost.connect(0)` → `SessionAccess` → `GameClient` → `V3GameClientAdapter` → `GameScreen`。Screen 根据可见快照渲染并选择候选，提交 `ActionChoice`，不会自行推进或修改权威状态。

联网使用 `RemoteSession` / `MallSession`，二者共享 TCP 协议与服务端 TableHost，仍实现同一 SessionAccess。服务器把连接身份绑定到固定席位和连接代次，重连替换旧连接后，旧连接失去操作能力；托管需显式收回。

MatchSession 保存整场账本、进度、历史和排名，每次实际发牌创建新 RoundStateMachine。麻将合法性、候选准备、结算和不变量审计在外部服务中执行；TableHost 串行协调命令、时钟、控制与 AI。单局结束后不重置旧实例继续使用。

每个席位默认每局 20 秒储备、每次决策 3 秒免费时间。到期响应默认 Pass，摸打阶段选择合法弃牌；不自动替真人和牌。立直且没有自摸/合法暗杠选择时，宿主按节奏单步摸切。AI 在后台计算并在提交时重新验证决策和控制代次；局间自动庄家采用确定性继续政策，不在桌锁内调用任意策略。

## 查询、事件与恢复

SessionAccess 的 Future 接口包括快照、候选、提交、开赛、下一局、活动单局、订阅、恢复与控制切换；公共查询还有 `querySession()`、`queryRoundRecords(offset, limit)`、`queryFinalResult()`。MahjongGameAccess 绑定具体 roundId，旧局不能提供新局的可执行候选。

PlayerSnapshot 包含按座位过滤的牌面、决策、权威时钟、出千余额、Session 进度/历史/结果、连接与托管状态、暂停状态，以及终局恢复所需的公开结算事件。明细保留役、番、符、宝牌、里宝牌、赤宝牌与支付计算；不包含未授权的对手暗牌或私人账本原因。

PlayerUpdate 使用前置游标和新快照游标保证顺序。GameClient 缓冲缺口并异步恢复；关闭后迟到回调失效。网络写入走有界队列，畸形帧、掉线、关闭与超时会终结挂起 Future。

## 运行与验证

```powershell
# 桌面游戏
.\gradlew.bat :lwjgl3:run

# 无图形服务器（局域网测试按需改 bind）
.\gradlew.bat :server:run -Pbind=127.0.0.1 -Pport=9090

# 分模块回归
.\gradlew.bat :engine:test :client:test :presentation:test :server:test :lwjgl3:classes

# Android 调试包
.\gradlew.bat :android:assembleDebug

# 默认 V3 入口真实 GL 冒烟：发牌、点击弃牌、AI 响应、返回菜单
.\gradlew.bat -I tools/architecture/v3-smoke.init.gradle :lwjgl3:v3Smoke
```

原规则、守恒、立直、振听、响应优先级、副露与结算测试均保留，按模块迁移。测试夹具共享于 engine 的 testFixtures。测试结果与截图记录在 build/v3-migration；开发代理的静态报告不代表构建或验收通过。

## 兼容与能力边界

旧 PlayerEndpoint/GameSession/LocalTableRuntime 入口继续支撑尚未迁移的显式调用及规则/UI 回归。产品默认从 MahjongGame.launchGame 进入 V3；同步兼容外观不得阻塞等待网络，也不得伪造成功回执。已清理没有调用的重复 LocalSessionAccess 与迁移前临时构建脚本。

当前网络是局域网明文 TCP，已有可调用房间与连接接口；不代表已提供公网账户服务、自动匹配界面或生产部署。三人麻将、主机迁移和持久化崩溃恢复仍未实现。出千点数为整场内独立资源账本，受信宿主入口记账，不是跨比赛账户经济系统。
