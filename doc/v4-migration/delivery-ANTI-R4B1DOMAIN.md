# R4B1DOMAIN 交付报告 (delivery-ANTI-R4B1DOMAIN)

## 1. 任务概述与职责声明

- **任务标识**：R4B1DOMAIN（TableAuthority 领域配置修复与原子大厅重构）
- **执行角色**：Antigravity (Anti) 生产开发者
- **验收角色**：Codex 独立测试与验收
- **测试执行声明**：**Anti 严格遵循指令约束，仅使用内置文件读写工具，未运行任何 shell、RunCommand、Gradle 构建或自动化测试。全部用例由 Codex 独立构建与运行验收。**

---

## 2. 修改文件清单

| 文件路径 | 修改性质 | 主要改动内容 |
|---|---|---|
| `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` | 生产代码 | 1. 修复构造器 `timeControl: TimeControlConfig` 重复声明属性问题；<br>2. 增加 `hasStarted` 状态标记与精准大厅未发牌生命周期判定；<br>3. `updateLobbyConfig` 校验大厅生命周期、赛程与时钟合法性后原子更新领域，保留既有 `DecisionClock` 不可变契约并原子替换全新时钟实例；<br>4. 在 `start`、`nextRound` 中记录 `hasStarted = true`，开局或洗牌准备中严格拒绝重配置。 |
| `engine/src/main/kotlin/com/majiang/timing/DecisionClock.kt` | 生产代码 | 保持 `val config: TimeControlConfig` 不可变字段契约，不在局内或无决策期间开放运行时公有可变 setter，配合 TableAuthority 在开局前整桌重置时以全新时钟实例替换。 |
| `engine/src/main/kotlin/com/majiang/match/MatchSession.kt` | 生产代码 | 1. `ruleConfig` 开放为带有 `private set` 的可重配字段；<br>2. 补充 `initialScores` 与 `initialCheatPoints` 访问并支持大厅更新；<br>3. 增加 `updateLobbyConfig(config: MatchConfig)`，前置使用 `check` 强校验初始未流转版本 (`version <= 1L && _roundRecords.isEmpty()`)，杜绝静默 no-op 与半提交。 |
| `engine/src/main/kotlin/com/majiang/round/RoundStateMachine.kt` | 生产代码 | 增加 `updateLobbyConfig(config: MatchConfig)`，前置使用 `check` 强校验初始 STACKING 未发牌状态 (`version == 0L && state.phase == GamePhase.STACKING && state.wall.isEmpty()`)，同步原子更新 `state.scores`、`state.cheatPoints`、`state.roundWind`、`startingScores` 与 `startingCheatPoints`。 |
| `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 生产代码 | 1. 修复构造器形参默认值与 body 字段声明（`matchProgression` 与 `shuffleConfig` 具备真实默认值并在 body 中声明为 `private set` 属性）；<br>2. 实现 `updateLobbyConfig(newConfig: MatchConfig): Boolean`，在读写锁与并发原子守卫内完整校验整场记录、会话版本、单局版本与牌山空状态，原子完成 session、round、matchProgression、shuffleConfig 更新与决策失效。 |
| `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt` | 生产代码 | `updateConfig` 操作成功后调用 `publishStateChanges(emptyList())`，向席位监听器连贯广播最新快照。 |
| `engine/src/test/kotlin/com/majiang/authority/TableAuthorityTest.kt` | 模块测试 | 增加 3 项领域测试用例：<br>1. 默认未发牌权威更新赛程、初始点数与 10+2 计时，验证 `sessionId` 稳定、视图点数正确与 `deadline - opensAt == 12000L`，且开局后再次配置被拒绝、状态与截止时间不变；<br>2. 非法配置抛出 `IllegalArgumentException` 且零部分修改；<br>3. 开启洗牌千术自动应用东风战 50 点预算并在准备阶段锁定配置更新。 |

---

## 3. FEEDBACK-R4B1DOMAIN 反馈处理记录

1. **构造器默认值自引用与编译问题**：
   - 恢复 `ApplicationCoordinator(..., matchProgression: MatchProgression = MatchProgression(), ..., shuffleConfig: ShuffleConfig = ShuffleConfig())` 真实默认形参；
   - 内部属性采用 `var matchProgression: MatchProgression = matchProgression private set` 与 `var shuffleConfig: ShuffleConfig = shuffleConfig private set`，未向公有暴露可变规则对象。
2. **时钟不可变性与 DecisionClock 契约保持**：
   - 撤销 `DecisionClock.config` 的公有可变 setter，完整保留其作为 `val config: TimeControlConfig` 的不可变契约；
   - 在 `TableAuthority` 中将 `var decisionClock` 声明为 `private set`，由权威在开赛前的原子大厅重配流程中实例化全新 `DecisionClock` 替换，消除跨局/活跃局储备被外部污染的风险。
3. **Session 与 Round 状态机前置校验防静默 no-op**：
   - 在 `ApplicationCoordinator.updateLobbyConfig` 入口执行前置全量拦截（`session.version > 1L`、`round.version != 0L`、非 STACKING、非空牌山等均直接返回 `false`）；
   - 在 `MatchSession.updateLobbyConfig` 与 `RoundStateMachine.updateLobbyConfig` 内部加入 `check(...)` 不变量断言，杜绝静默忽略与局部成功。

---

## 4. 未解决项与后续说明

- **本任务范围外项**：
  - 本任务仅针对 R4B1DOMAIN（纯领域配置与时钟重配修复），未改动或集成 UDP 协议报文解析、网络端点与房间运行时实现（留待后续 R4B 批次处理）。
  - `tools/architecture/v4-review/` 目录下 review 测试保持只读未做改动。
- **本任务未决项**：无，领域阻塞问题已全部闭环修复。
