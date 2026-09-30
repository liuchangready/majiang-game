# C2：客户端集成审查修复

只修改原 C 归属文件；额外允许修改组合根 `core/src/main/kotlin/com/majiang/MahjongGame.kt`，让默认 launchGame 直接装配 PlayerEndpoint/LocalPlayerEndpoint。R2、S2 并行修改其他文件。禁止 shell/构建/改测试。写 delivery-C2.md。

1. 多端点事件隔离：notifySubscribers 现在给所有 listeners 传同一个 SessionUpdate。一个监听者可以改 GameEvent.ScoreSettled.newScores，污染后续监听者。每个 listener 必须收到独立深复制事件，保留既有 SessionSnapshotContractTest / MatchIntegrationContractTest 断言。
2. 控制者非人类时旧 submit 返回 ACTION_NOT_AVAILABLE；目前变成 NOT_YOUR_TURN，导致 SeatControllerSessionAcceptanceTest 语义回归。统一交给 Coordinator 验证并保留既有拒绝原因，避免客户端复制权限规则和错误。
3. 移除端点对 Coordinator 调用的嵌套 stateLock。S2 会加统一一致性锁，若 view/submit 持端点锁等待协调器，而协调器回调又等待另一个端点锁，就会死锁。端点关闭用 volatile/atomic、listeners 用 CopyOnWrite、协调器执行期间不要持端点锁。由协调器保证查询一致性，端点只做展示和控制类型过滤。
4. 初始化控制配置兼容：显式 controllers 必须用于 start 配置（即使 engine 已有不同配置也应按原 LocalGameSession.start 明确覆盖）；未指定 controllers 才保留 engine 已有配置。构造一个多端点不能偷偷重置已经开局的整场配置。请阅读 SeatControllerSessionAcceptanceTest / LocalGameSessionContractTest 的既有行为，修正配置时点和 started 判断。
5. 保持 LocalGameSession.engine/eventBus 的旧私有封装，不要由 private 变成公开 val 泄露权威引擎。GameScreen 只依赖 PlayerEndpoint；MahjongGame.launchGame 的参数改 PlayerEndpoint，默认 LocalPlayerEndpoint；GameSession 作为继承 PlayerEndpoint 的兼容接口仍可传入，避免破坏旧测试和 Java 冒烟。
6. UI 所有实际动作提交（包括听牌、吃碰杠、自摸、荣和、过、和止、出牌）都要走统一 submitAction 决策选择桥接；不能只改一个按钮。保留 input gate、组合与听牌选择模型以及假端点的 capabilities.decisions=false 回退。
7. 检查新 PlayerEndpoint 的默认方法对 Kotlin 与 Java 测试替身兼容（不要修改构建配置）。已有 tools/architecture/*RenderSmoke.java 的 PreviewSession implements GameSession 需要继续能编译；如果 JVM 默认方法已支持则不用处理。

Codex 新测试在 core/src/test/kotlin/com/majiang/architecture；工具冒烟 tools/architecture/RoundSessionRenderSmoke.java 走真正默认 GameScreen 路径。不要修改这些测试来适配错误实现。
