# D-09 领域权威与协调器的千术内部委托 交付说明

- 任务：`doc/v4-重构/IMPROVEMENT-PLAN.md` §9.2 D-09
- 交付方：deepseek_harness
- 基线：`D:\majiang` 工作区 HEAD `ef927aa` + 既有未提交 D-05/D-06/D-07 改动
- 独立检视：Codex（待本轮交付后执行）
- 状态：已实现并完成本地验证；**未提交、未推送**

## 1. 协调与并发约束

- 启动时确认 D-07 `thread_20261019_233852_1648` 与 D-06 `thread_20261019_235302_1760` 均已完成并获批准，主工作区无活动 Gradle 构建。
- 所有 Gradle 命令串行执行，未与 D-05/D-06/D-07 或任何其他任务并发。
- 未修改隔离 D-06 工作树；未覆盖 D-05/D-07 未提交成果；未触碰无关 untracked 历史文件；未向其他代理下发任务。

## 2. 交付范围

按计划建议的四个行为等价切片推进：

| 切片 | TableAuthority 专用处理器 | ApplicationCoordinator 执行能力 |
|---|---|---|
| 落汗 TileMark | `TileMarkSubmissionHandler` | 既有 `submitTileMark` + `CheatLedgerCharge` / `CheatEffectFactory` |
| 袖里藏花 SleeveBloom | `SleeveBloomSubmissionHandler`（激活 + 二选一） | 既有 `submitSleeveBloom` / `submitSleeveBloomChoice` + 同一组能力 |
| 偷梁换柱 BeamSwap | `BeamSwapSubmissionHandler`（激活 + 二选一） | 既有 `submitBeamSwap` / `submitBeamSwapChoice` + 同一组能力 |
| 码牌 Shuffle | `ShufflePreparationHandler`（独立准备阶段生命周期） | 既有 `executeDealWithPreparation` + `CheatLedgerCharge` |

观察者私有预览仍由既有 `TileMarkProjectionFactory` / `SleeveBloomProjectionFactory` / `BeamSwapProjectionFactory` 在 `TableAuthority.querySnapshot` 原位置直接装配，未新增携带父对象的包装器；`CheatSubmissionCheck` 仅表达 `Allowed / Rejected / StaleOffer` 三种预检结果。

## 3. 职责边界

### 3.1 仍由 TableAuthority 原流程协调

- 所有 `submit*` 公共方法签名与同步入口不变。
- 统一 requestId 命名空间、`storedReceipts` / `storedShufflePlans` 查重、跨操作冲突与配额仍由 `TableAuthority` 持有，未新建任何按绝技拆分的回执表。
- 权威锁、`roundId` / `decisionId` / `controlEpoch` / deadline 判定、计时 rekey、事务安装、事件记录与私有/公开可见性全部留在原入口。
- 费用仍在校验通过后由协调器原子扣除；重复请求在回执命中处直接返回原回执，不进入扣费。

### 3.2 进入 internal 专用处理器的职责

- 请求归一化：各处理器返回自己的 `Normalized*Payload`，供 TableAuthority 做回执指纹比较。
- 候选/资格检查：局面阶段、手牌/牌山/王牌候选、立直/摸牌来源、挂起 offer 漂移、费用余额等。
- 私有预览装配继续留在 `TableAuthority.querySnapshot`：只把观察者本人的落汗/袖里藏花/偷梁换柱视图组合进 `PlayerSnapshot`，不改变公共投影白名单；处理器 Input 不携带 `MatchSession` / `RoundStateMachine` 等父聚合。
- 处理器本身无状态：不持有 `TableAuthority`、`ApplicationCoordinator`、`GameState`、`MatchSession`、`RoundStateMachine` 字段，也不互相持有；嵌套 `Input` / `Step` 等事实类型同样不含父聚合。

### 3.3 码牌保持独立准备阶段语义

- `ShufflePreparationHandler` 拥有选择/确认/预留/截止时间/AI 自动确认/deadline 发牌计算状态，但不创建线程或计时源。
- 超时、暂停/恢复、关闭与 tick 驱动仍由 `TableAuthority` 原方法调用；没有把码牌塞进局内三大绝技的统一 Handler 生命周期。
- `TableAuthority` 仍负责把准备记录写回 `RoundStateMachine`，并调用 `ApplicationCoordinator.executeDealWithPreparation` 完成 136 张发牌与费用事务。

### 3.4 ApplicationCoordinator 最小类型化能力

- `CheatLedgerCharge.apply(...)`：以余额/流水/冻结基线重建账本并计算一条扣费流水，不接触 `GameState`、不安装状态、不发事件；失败返回 `null`，由原拒绝路径处理。
- `CheatEffectFactory`：纯构造 `TileMarkGroup`、`SleeveBloomOffer`、`BeamSwapOffer`；安装仍在协调器原位置。
- 实体牌移动继续使用既有 `TileMovement` 能力（`swapHandWithWall` / `swapDrawnWithNextRinshan` / `swapHandWithDeadWall` / `deal`），未搬到 TableAuthority。
- 未把协调器的执行职责迁入 TableAuthority；未新增公开可变入口。

## 4. 行为等价要点

- **requestId**：同一座位跨落汗/袖里藏花/偷梁换柱/码牌/普通选择共用一个命名空间；同载荷重试返回原回执，异载荷返回 `REQUEST_ID_CONFLICT`。
- **费用**：所有扣费在资格/候选/截止时间/余额校验之后；重复请求不重复扣费；码牌预留仅在最终发牌事务中扣除。
- **代次与截止时间**：控制代次变化使旧请求拒绝；同一决策的计时 rekey 保持 `opensAt/deadline/reserve` 不变。
- **挂起 offer**：袖里藏花/偷梁换柱的实际牌局漂移会清理 stale offer，但不退款、不交换、不摸切、不延长预算。
- **码牌**：准备阶段身份、deadline、暂停冻结、AI 自动确认、超时统一发牌与 136 张物理守恒保持。
- **隐私**：落汗组、袖里藏花 offer、偷梁换柱候选仍只进入本人私有预览；公共投影继续过滤 `CheatPointsChanged` 等私密事件。
- **终态/新局**：终局后旧回执仍可安全重放；新局使用新 `roundId`，旧局码牌请求被拒绝。

## 5. 新增确定性回归

新增 `engine/src/test/kotlin/com/majiang/authority/D09CheatDelegationAcceptanceTest.kt`（5 项真实场景）：

1. 落汗成功、同载荷重放、跨落汗/袖里藏花/偷梁换柱/普通选择同 ID 冲突、只扣一次费、其他观察者私有组为空、公共事件过滤。
2. 袖里藏花控制代次切换后旧二选一被拒、收回后新代次成功且不二次扣费；实际牌山漂移触发 stale offer 清理且不退款/不换牌。
3. 偷梁换柱在截止前成功选择后 offer 清除、费用只扣一次；另一场景截止时选择被拒、超时只结算一次并摸切一次。
4. 码牌准备阶段错误代次拒绝、重复请求幂等、重复确认拒绝、截止拒绝、暂停/恢复平移 deadline、AI 自动确认后统一发牌、旧局迟到请求拒绝。
5. 关闭权威入口取消准备与未决请求，迟到操作不再产生费用或状态副作用，回执命名空间清空。

新增 `D09AuthorityDelegationStructureTest.kt`（3 项结构边界）：

- 四个专用处理器均为 `internal class`，不持有权威/协调器/牌局/回执表字段；反射递归检查 `Input` / `Step` 等嵌套类型的字段及其泛型参数，禁止 `MatchSession` / `RoundStateMachine` / `GameState` / `TableAuthority` / `ApplicationCoordinator` 重新进入。
- 源码扫描禁止 `authority/cheat` 可执行代码引用父聚合，并禁止 `PrivateCheatPreviewAssembler` 包装器回归。
- `TableAuthority` 仍是唯一回执命名空间与准备阶段事务所有者；处理器调用点存在，旧的分散准备字段已移除，私有预览仍由原投影工厂装配。
- `ApplicationCoordinator` 在每个费用路径调用 `CheatLedgerCharge`，效果构造走 `CheatEffectFactory`，不接管回执命名空间。

以上测试均通过真实权威入口与物理牌局状态断言，不以“是否调用 Handler”替代业务结果。

## 6. 验证命令、退出码与计数

全部串行执行，返修后最终日志位于 `build/d09-verification/`：`engine-test-rework.log`、`server-test-rework.log`、`full-test-rework.log`、`v4-review-rework.log`。

| 命令 | 退出码 | 结果 |
|---|---:|---|
| `.\\gradlew.bat :engine:test --rerun-tasks --console=plain` | **0** | BUILD SUCCESSFUL；6 tasks；engine **523** tests / 0 failures / 0 errors / 0 skipped |
| `.\\gradlew.bat :server:test --rerun-tasks --console=plain` | **0** | BUILD SUCCESSFUL；12 tasks；server **337** / 0 / 0 / 0 |
| `.\\gradlew.bat test --rerun-tasks --console=plain` | **0** | BUILD SUCCESSFUL；66/66 tasks；全仓 **1,626** / 0 / 0 / 0 |
| `.\\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --console=plain` | **0** | BUILD SUCCESSFUL；V4 独立探针 **108** / 0 / 0 / 0（12 个探针类，含 `V4FinalArchitectureTest` 9 项与 `V4AuthorityBoundariesTest` 2 项） |
| `git diff --check` | **0** | 无空白错误 |

全仓 JUnit XML 计数：

| 模块 | tests | failures | errors | skipped |
|---|---:|---:|---:|---:|
| engine | 523 | 0 | 0 | 0 |
| protocol | 190 | 0 | 0 | 0 |
| server | 337 | 0 | 0 | 0 |
| client | 230 | 0 | 0 | 0 |
| presentation | 346 | 0 | 0 | 0 |
| **合计** | **1,626** | **0** | **0** | **0** |

与 D-06/D-07 最终基线 1,618 相比增量 **+8**，全部来自本任务新增的 5 项真实场景 + 3 项结构边界测试。既有 515 项 engine 测试全部保持通过。

## 7. 变更文件

新增生产代码：

- `engine/src/main/kotlin/com/majiang/authority/cheat/CheatSubmissionCheck.kt`
- `engine/src/main/kotlin/com/majiang/authority/cheat/TileMarkSubmissionHandler.kt`
- `engine/src/main/kotlin/com/majiang/authority/cheat/SleeveBloomSubmissionHandler.kt`
- `engine/src/main/kotlin/com/majiang/authority/cheat/BeamSwapSubmissionHandler.kt`
- `engine/src/main/kotlin/com/majiang/authority/cheat/ShufflePreparationHandler.kt`
- `engine/src/main/kotlin/com/majiang/application/cheat/CheatLedgerCharge.kt`
- `engine/src/main/kotlin/com/majiang/application/cheat/CheatEffectFactory.kt`

修改：

- `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`（提取预检/准备状态；私有预览仍由原投影工厂在原位装配；保留公共入口与回执/锁/事务）
- `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`（扣费与效果构造改为最小能力）

新增测试：

- `engine/src/test/kotlin/com/majiang/authority/D09CheatDelegationAcceptanceTest.kt`
- `engine/src/test/kotlin/com/majiang/authority/D09AuthorityDelegationStructureTest.kt`

文档：

- `doc/v4-重构/ANTI-D09-DELIVERY.md`（本文件）
- `doc/v4-重构/IMPROVEMENT-PLAN.md` §9.2/§9.3 状态注记

## 8. 残余风险与边界

1. 三个既有投影工厂仍直接消费 `MatchSession` / `RoundStateMachine`，但调用点只保留在 `TableAuthority.querySnapshot` 的权威只读路径；它们不再进入任何处理器 Input。若未来要把投影工厂本身也改为窄事实输入，需要同步迁移三个工厂签名，属于独立切片。
2. `TableAuthority` 中处理器“允许”之后的 `checkNotNull(round/timing)` 是防御性断言；正常同步入口下由处理器预检保证。若未来处理器新增可允许路径而漏传事实，会快速失败而不是静默放行。
3. 回执存储、配额、准备阶段记录写回和计时 rekey 仍集中在 `TableAuthority`，这是 D-09 明确要求保留的协调职责，不作为后续“未拆分”缺陷。
4. 本任务未执行桌面真实 GL 冒烟、Android 真机/模拟器、跨物理网络 T4 或 TLS/公网环境实测，不声称通过；这些不属于 D-09 的领域委托范围。
5. 工作区仍包含 D-05/D-06/D-07 的未提交改动；本次未提交、未推送，最终提交由验收方统一处理。

## 9. 独立检视返修记录（2026-10-20）

- 检视发现：`PrivateCheatPreviewAssembler.Input` 同时携带 `MatchSession` 与 `RoundStateMachine`，违反“处理器不获得父对象/最小类型化输入”的边界。
- 返修方式：删除 `PrivateCheatPreviewAssembler` 包装器，将三个既有投影工厂恢复到 `TableAuthority.querySnapshot` 原位置；未扩大 public 可变入口，未迁移权威事务，预览/观战隐私行为不变。
- 边界测试补强：递归检查处理器嵌套 `Input` / `Step` 的字段及其泛型参数，禁止父聚合重新进入；源码扫描禁止 `authority/cheat` 可执行代码引用父聚合，并禁止预览包装器回归。
- 返修后重新串行执行 engine/server 测试、全仓 `test --rerun-tasks`、V4 探针与 `git diff --check`，结果见第 6 节。

## 10. 合入本地 main（2026-10-20）

- 合入方式：从本地 main `e59bc9f` 新建 `feat/d09-cheat-delegation` 分支，按第 7 节文件清单原样搬运 D-09 交付（原工作区 `D:\majiang` 中的未提交改动未受影响）。
- 集成验证：`.\gradlew.bat test --rerun-tasks --console=plain` 退出码 0，66 个任务全部执行完成；全仓 1,622 项测试 / 0 failures / 0 errors / 0 skipped（engine 523、protocol 190、server 337、client 230、presentation 342）。与第 6 节的差异仅来自 main 已合入的 D-04/D-05/D-07 内容，D-09 新增的 8 项 engine 测试全部通过。
- V4 独立探针：`-I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test` 共 108 项全部 PASSED，与第 6 节一致。
- 未执行桌面 GL 冒烟、Android 真机与跨物理网络实测，边界与第 8 节一致。
