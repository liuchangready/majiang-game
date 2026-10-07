# 连接控制归属迁移与当前架构文档刷新

用户授权：连接控制源码迁到 server，由 Anti 开发，Codex 检视，同时刷新旧设计文档。此次是 R6 后的窄范围收尾，不重启历史迁移任务。

## 开发分工

Anti 独占本任务生产代码、模块测试和下述设计文档。仅使用内置文件读取/搜索/编辑工具；不要执行 shell/RunCommand、构建、Git 提交、改变权限或委派其它 agent。Codex 独立执行构建/测试，维护 tools/architecture/v4-review 下的审查用例（Anti 只读）。不请求用户再次确认。当前工作区基线干净。

## 代码范围与契约

1. 将 engine/src/main/kotlin/com/majiang/control/ConnectionControl.kt 和 DisconnectPolicy.kt 完整迁至 server/src/main/kotlin/com/majiang/server/control/，package 改为 com.majiang.server.control。其中 ControllerRole、ConnectionStatus、SeatConnectionState、DisconnectMode 都仅被连接宿主使用，随实现一起迁移。engine 的 SeatController 与 application/ControllerRegistry 等领域控制实现保留。
2. 更新 server 生产源码与模块测试所有引用；如发现 engine 测试实际引用这些连接类，将对应测试迁至 server，保留全部断言，严禁 engine 反向依赖 server。当前快速扫描仅发现 ServerTableHost、UdpTableServer 和两份 server host 测试；自行核实。
3. 仅变更源码所有权、包名和必要注释，保持断线策略、宽限期、重连不自动收回、controlEpoch、幂等与20+3计时行为不变。不重构 coordinator 公开接口或 client.api，不改协议/默认配置/依赖版本。
4. 删除原 engine 两文件，无旧包 typealias/兼容壳。若内置工具无法删除，交付清单列出待删除的精确路径，Codex 验证目标后执行。
5. 如缺少连接控制策略的直接回归，可在 server 补充有意义的行为测试（立即AI、宽限到期、TIMEOUT_ONLY、重复掉线不续宽限、重连仍须显式收回）。不删除/弱化既有有效断言。独立审查 V4ServerHostAcceptanceTest 的 import 由 Codex 更新。

## 文档范围

刷新 doc/GAME_ARCHITECTURE_V4.md、doc/FRAMEWORK_ARCHITECTURE.md、doc/SERVER_CLIENT_API.md、doc/README.md 和根 AGENTS.md 中作为当前规范的内容。可以整体改写旧总图/旧接口草案，但必须先读实际源码与 doc/v4-migration/CONTRACT.md，以已实现行为为准。历史归档 doc/archive 不修改，R1-R6 验收记录不改写为本次验收。文档应简洁、准确，有可解析 Mermaid 图和有效相对链接。

重点：
- 七模块，engine 独立领域，protocol 独立协议，client -> protocol，server -> engine+protocol；presentation 内 menu/game/common 只用 client.api。MahjongGame 在 presentation 中是组合根，允许装配 server；不要画成 client 依赖 server 实现或 presentation 完全不依赖 server。
- 运行调用链与编译依赖图分开：本地 InProcessServer/Binding 和远端 UDP 绑定共用 RoomRegistry/SharedRoom/ServerTableHost/TableAuthority。protocol 是库不是中转服务。
- 连接控制实现归 server/control；engine 仍有领域控制权角色/决策代次与 SeatControlView 投影，不要宣称删掉所有 control 概念。
- 数据映射 engine -> server mapper -> protocol DTO -> client mapper -> client view，UI 不直接订阅领域 GameEventBus。
- 单机由 MahjongGame.render 驱动 server.tick，远端服务定时 tick。每桌串行队列；AI 推理队列外，结果验证 roundId/decisionId/controlEpoch；权威deadline不能被玩家优先级或动画无限推迟。
- UDP v4、22字节头、1200数据报、1090分片阈值、3秒心跳/10秒判死、256推送窗口。PUSH始终完整快照+可见事件；isFullSnapshot 标记建立/替换基线；座位独立游标、公开观战独立流，sessionId+syncEpoch绑定。不要继续写增量状态模型/512/每桌一个cursor/TCP/Ktor/WebSocket。
- 单局 RoundStateMachine 终态固定，下一次发牌新实例新roundId；MatchSession/Coordinator负责整场交接。分别描述房间、整场、单局生命周期，不混为同一 FSM，包含抢杠 CHANKAN_WINDOW。
- SERVER_CLIENT_API 以 protocol 的实际 ServerConnection/LobbyApi/SessionApi/SpectatorApi 与 client 门面核对方法/字段，去掉旧共享engine DTO和requestControl(Boolean)作为对外接口的描述。claimControl/releaseControl语义准确，观战只读。
- AGENTS 当前规范不得继续要求保留已删除的 V3 兼容入口；旧 Anti/GLM 迁移派发细节可用历史归档链接替代。保留有效规则/守恒/算分/计时测试与双端规范。
- 不把公网账户、自动匹配、三人玩法、持久恢复、主机迁移或加密上线写成已实现。不宣称本次已测试通过；明确等待 Codex 验收。

交付到 doc/v4-migration/DELIVERY-ANTI-CONNECTION-OWNERSHIP.md，列出文件、迁移/删除、行为是否改变、文档校对要点、尚待 Codex 执行的检查。完成文件写入后退出。
