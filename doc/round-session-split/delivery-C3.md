# 任务 C3 交付报告：客户端查询一致性与封装强化

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task C3)
- **验收 Agent**：Codex (设计、测试、构建、检视)

---

## 1. 任务背景与核心改进

针对 Task C2 审查中指出的查询一致性风险与内部状态封装问题，本次任务 C3 完成了以下改进：

### 1.1 原子单次查询，杜绝牌面与动作撕裂
- **原有问题**：`LocalPlayerEndpoint.view()` 先调用 `legalActions()` 再调用 `coordinator.queryView(viewerSeat)`。两次独立读取之间若发生动作提交，旧动作候选将覆盖新牌面视图，破坏查询一致性。
- **改进落地**：`view()` 仅以单次 `coordinator.queryView(viewerSeat)` 获取的 `baseView` 及其内置 `legalActions` 为唯一权威快照；端点仅在端点已关闭或非真人控制者（`Computer` / `Takeover`）时安全清空动作列表（`baseView.copy(legalActions = emptyList())`），不重新调用 `legalActions()` 覆盖规则引擎产出的合法动作。

### 1.2 `legalActions()` 单点派生，杜绝重复实现与递归
- **改进落地**：`legalActions()` 重构为单一表达式 `view().legalActions`。
- **效果**：完全避免重复实现控制者类型过滤逻辑，消除潜在递归隐患，确保外部直接调用 `endpoint.legalActions()` 与从 `endpoint.view().legalActions` 获取的数据严格处于同一版本快照。

### 1.3 消除客户端对 `round.state` 的直接访问
- **原有问题**：`isMatchStarted()` 直接读取内部可变 `round.state.phase` 与 `round.state.wall`，违反三层解耦与客户端防作弊隔离规范。
- **改进落地**：
  - `isMatchStarted()` 改为通过脱敏只读视图 `coordinator.queryView(viewerSeat)` 的 `phase != GamePhase.STACKING || wallRemaining > 0` 以及 `round.version > 0L` 进行安全判断，彻底消除对内部可变 `round.state` 的任何直接引用；
  - `currentController(baseView)` 与 `isMatchStarted(baseView)` 支持复用 `view()` 已获取的视图快照，避免内部产生二次查询开销。

### 1.4 收紧 Coordinator 构造属性可见性
- **改进落地**：`LocalPlayerEndpoint` 主构造器中 `coordinator` 属性由 `public` 调整为 `protected`：
  ```kotlin
  open class LocalPlayerEndpoint(
      override val viewerSeat: Int = 0,
      protected val coordinator: ApplicationCoordinator = ApplicationCoordinator(),
      controllers: List<SeatController>? = null
  ) : PlayerEndpoint
  ```
- **效果**：保持构造参数名称、类型与默认值完全兼容（测试代码仍可自由注入 Coordinator），但不再向持有端点引用的外部客户端暴露全权 `ApplicationCoordinator`。

### 1.5 既有机制完整保留
- 完整保留 C2 交付的无端点锁架构（`AtomicBoolean` 管理生命周期，杜绝死锁）；
- 完整保留每个监听者广播独立的 `snapshotGameEvent` 与 `view()` 深拷贝快照；
- 完整保留控制权统一由 ApplicationCoordinator 权威裁决与 UI `submitAction` 决策桥接。

---

## 2. 变更文件清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/client/LocalPlayerEndpoint.kt` | 修改 | `view()` 单次原子查询快照，`legalActions()` 单点派生，消除 `round.state` 访问，`coordinator` 属性受保护封装 |
| `doc/round-session-split/delivery-C3.md` | 新增 | 本交付报告 |

---

## 3. 验收与状态说明

- **边界隔离**：变更严格限制在 `core/src/main/kotlin/com/majiang/client/` 与交付报告，未触碰 `application`、`round`、`session`、`screen`、测试代码或 Gradle 配置。
- **环境规范**：无头环境，未执行任何 Shell、终端、构建或测试指令。
- **后续协同**：请 Codex 进行后续编译构建与全量回归验证。
