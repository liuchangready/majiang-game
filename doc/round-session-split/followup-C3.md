# C3：客户端查询的一致性与封装

C2 已交付。只改 client/**（其他任务正在改 application 和 Round，不要碰）。禁止 shell/测试/构建。写简短 delivery-C3.md。

LocalPlayerEndpoint.view() 当前先 `val actions=legalActions()` 再 `val baseView=coordinator.queryView(viewerSeat)`，两次独立查询之间可能发生动作提交，然后旧候选覆盖新牌面，违反一致性查询要求。必须以一次 coordinator.queryView 的 baseView 和其内置 legalActions 为唯一牌面/动作快照；端点只按关闭状态/控制者类型清空按钮，不重新调用 legalActions 来覆盖规则结果。独立 legalActions() 本身也可读取同一个已过滤 view，避免重复实现与递归。

客户端不要直接访问 `round.state`：isMatchStarted() 可用 coordinator.queryView(viewerSeat).phase 是否为 STACKING 判断，或通过协调器的只读状态接口，不读取内部可变 GameState。LocalPlayerEndpoint 的 coordinator 构造属性改 private/protected，不向客户端公开全权 Coordinator（构造参数名称、类型与默认值保持，测试仍可注入）。

保留 C2 的无端点锁、每监听者事件深复制、控制权统一验证和 UI submitAction；不要改任何测试或其他任务的接口。
