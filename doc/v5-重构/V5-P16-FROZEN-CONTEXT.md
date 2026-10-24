# V5 P1.6 AI 冻结上下文基线（AiCheatView / AiSeatSnapshot / AiDecisionContext）

日期：2026-10-25
状态：**组件级交付与第 3 轮返修记录，不是完整 P1.6、G1 或 G5 验收。**
基线：`8ee22ebf5164f74db75e5a33e87900f5b87cd8bd`（已复核 PR19 head）
第 1 轮提交：`4e594a59795a87432222651c257614e20285c40b`
第 2 轮提交：`45e99970d36077a0073fd1310348245d25a945e1`
第 3 轮修复提交：本轮最终 HEAD（见最终交付说明）
分支/工作树：`wt/v5-ai-frozen-context` / `D:/majiang-wt/ai-frozen-context`
V4 仍是唯一生产架构；G1 仍 **BLOCKED**，G5 **NOT_RUN**。

## 1. 范围与文件

本任务只补齐 ENG-017 的安全上下文基线，不实现 ENG-018 候选工厂/注册表、ENG-020/022–025 能力、actor 接线、提交、物理移动或算分。最终相对基线只交付：

| 文件 | 作用 |
|---|---|
| `dotnet/src/Mahjong.Ai/AiCheatView.cs` | `AiPrivateFactScope`、`AiSeatFacts`、`AiCheatView` 及准备/落汗/袖里藏花/偷梁换柱纯事实 |
| `dotnet/src/Mahjong.Ai/AiSeatSnapshot.cs` | 冻结普通合法选项、私有事实、stamp/limits/policy/takeover、可选 observation/seat facts，并做结构/阶段/身份校验 |
| `dotnet/src/Mahjong.Ai/AiDecisionContext.cs` | 候选防御复制、Ordinal 唯一/查找、首个 safeDefault、`AiDecisionStrategy` 入口 |
| `dotnet/tests/Mahjong.Ai.Tests/AiSeatSnapshotTests.cs` | 快照/事实结构、冻结复制与身份门测试（22 + 66 项） |
| `dotnet/tests/Mahjong.Ai.Tests/AiDecisionContextTests.cs` | 阶段、候选、offer 手牌与策略入口测试（31 项） |
| `doc/v5-重构/V5-P16-FROZEN-CONTEXT.md` | 本说明与证据 |

未修改 `AiCandidateModels.cs`、`AiObservation.cs`、`AiDecision.cs`、`AiCheatPolicy.cs`、`Mahjong.Domain`、`Mahjong.Contracts`、actor、golden、追踪 JSON、统一 verifier、项目依赖或主 checkout。未合并兄弟在途代码，未 push/建 PR/触发远端 CI。

文件 SHA-256（提交前内容，文档自身除外）：

| 文件 | SHA-256 |
|---|---|
| `AiCheatView.cs` | `274a578c5c1927ba719fca20aac9fd08749e494d604c5026b28340c2ec95d455` |
| `AiSeatSnapshot.cs` | `2bf083fd93372677a2dd099c6077d29813109f096b96af31cd70db2668ca5f6d` |
| `AiDecisionContext.cs` | `15a806ee1a7210e315ad24e858da707bde1ebcb5506550ae923c16a1f4cd41d6` |
| `AiSeatSnapshotTests.cs` | `0592fc19f8ba527802eb8ff9eecd365fd60d07aa684e83ec0578452bb5d7f0b5` |
| `AiDecisionContextTests.cs` | `d33488708d18ffaa170d5f8b2790f259c44ace9462ef10f019dfbd54dc774dcd` |

## 2. V4 行为证据与实际覆盖边界

用真实编译的 V4 类（`D:/majiang/engine/build/classes/kotlin/main`，`kotlin-stdlib-2.4.10`）做了 Java 反射探针，验证：

- `candidate(id)` 为精确、大小写敏感的 `firstOrNull`；`OPT-A` 不匹配 `opt-A`。
- `safeDefault()` 返回第一个 `isSafeDefault`。
- 构造时复制候选列表；源列表清空后上下文仍保留原三项。
- 重复 `optionId` 抛 `IllegalArgumentException`；`candidate(null)` 抛 `NullPointerException`；
  大小写不同的 `Case`/`case` 可共存。
- `AiCheatView.deepCopy()` 返回独立实例；负 `cheatPoints` 抛 `IllegalArgumentException`；
  null 候选列表抛 `NullPointerException`。

证据：
- 第 1 轮探针源码/日志：`C:/Users/sncdc/AppData/Local/Temp/ai-frozen-probe/V4Probe.java`（SHA-256 `c791f02f...`）、
  `.../v4-probe.log`（SHA-256 `15342308ddf505a15c6317e06f9f4d5a1e77946adb8690207691cdf2ef8bc841`）。
- V4 源码 `AiDecisionModels.kt` SHA-256 `c86e3379300a2824b4736f72b5f55eae9a3c7a275df2f817900f55fa58686371`。

**边界（不得夸大）**：为使 V4 `AiDecisionContext` 可构造，探针对 `AiSeatSnapshot` 仅用
`Unsafe.allocateInstance` 提供非 null 持有者；被验证的 `candidate/safeDefault/candidates`
不读取 snapshot 字段，`AiCandidate`/`AiCheatView` 为真实构造。该探针**不覆盖**
`PlayerView`/`PlayerDecision` 的完整装配，也不代表 V4 端到端差分。

## 3. V5 安全加强与非等价处

- `AiCheatView.Scope`（`AiPrivateFactScope`：seat/session/current round）是所有私有内容的显式所有者绑定；非零 `CheatPoints` 与四类技术事实一样属于私有事实。无 scope、跨 seat/session/round 复用、DeepCopy 改写绑定均拒绝。默认全空零余额视图/未建局 IDLE 可无 scope；已绑定余额在未建局 IDLE 可 `round=null`，但技术事实必须有当前轮。
- 落汗事实校验 `OWN_HAND` 必须属于观察座位、`OPPONENT_HAND` 不能指向观察座位；`DISCARD` 的
  `RiverPosition.RoundId` 必须是当前 scope round。`OWN_HAND` 描述当前投影位置，
  `CreatedRoundId` 只是创建历史，不豁免当前已知手牌一致性；历史组仍可合法存续，`NotVisible` 等位置不受手牌成员约束。
- 当前手牌来源：优先 `AiSeatFacts.MyHandTileIds`；省略 SeatFacts 时使用真实 `AiObservation`
  的 closed hand + drawn tile；两者同时存在时先要求一致。`TileMark.SelectableTileIds` 也必须属于已知本座位手牌。
- `AiSeatFacts` 独立于普通决策，提供 V4 `PlayerView.myClosedTiles`、`myDrawnTile` 与合并手牌；
  这正是 V4 `SleeveBloomAiCapability.kt:99` 与 `BeamSwapAiCapability.kt:72,74` 消费的字段。
  offer 窗口强制要求 seat facts；准备未发牌/IDLE 无局可用空手牌表示；无当前轮时 closed/drawn 必须为空。
- 普通合法选项存在（或普通 observation 存在）时，`OrdinaryAction` 必须携带非空 round/decision/deadline；
  没有普通 DecisionKey 的窗口只能以真实 off-turn window 表达，不伪造 DecisionKey。
- `IDLE` 不允许普通选项、observation、pending offer、offerId/deadline/window 或非空候选。
- `AiDecisionContext` 要求每个候选的 `Phase` 与 snapshot phase 完全一致；IDLE 只接受空候选，
  因此 `safeDefault` 只能取当前阶段候选。
- 所有集合在构造器内逐层复制并以只读视图暴露；外部不能转回可写 `List`/数组/`Dictionary`。

与 V4 的已知不可等价：
- V4 的私有视图没有显式 owner/session/round；V5 增加 `AiPrivateFactScope` 并拒绝缺失/串线。
- V4 允许准备视图与 pending offer 共存；V5 拒绝该歧义，要求单一活动窗口。
- V4 允许准备 stamp 的合成 decision id 与附属视图 decision id 分歧；V5 仅在
  `ShufflePreparation` 阶段允许该一处分歧。
- V4 `AiDecisionStrategy.cheatPolicy` 有默认 null；V5 接口要求实现方显式实现 `CheatPolicy`
  （不依赖 Unity/netstandard2.1 默认接口成员），测试覆盖显式 null/policy。
- V4 `PlayerView`/`PlayerDecision` 被替换为可选 `AiObservation` + `AiSeatFacts` 纯事实；
  不携带完整状态、牌山顺序、对手完整暗手、种子或 authority 引用。

## 4. 下游投影义务

1. P3.2/actor 必须按座位从授权投影填充 `AiPrivateFactScope`、`AiCheatView` 和 `AiSeatFacts`；
   不得把完整 `CheatState`/`CheatPrivateView`/`PlayerView` 传入 AI。
2. offer（sleeve/beam）必须提供 `AiSeatFacts`，其中 `MyClosedTileIds`/`MyDrawnTileId` 与 V4
   `myClosedTiles`/`myDrawnTile` 同义；`BeamSwap` 的 selectable 必须是该手牌子集。
3. ENG-018 候选工厂必须生成与 snapshot phase 相同的 `AiCandidate.Phase`，`optionId` Ordinal 唯一；
   普通观察存在时必须与 `AiSeatSnapshot`/`AiSeatFacts` 手牌一致。
4. 投影层需把 V4/Domain 的 `ShuffleTarget`、`SleeveBloomDrawKind`、`SleeveBloomOfferMode`、
   `BeamSwapWindow`、`BeamSwapSlotKind` 映射为 AI 事实枚举。
5. `AiDecisionStrategy` 实现方必须显式给出 `CheatPolicy`（通常 null），`Choose` 只返回候选
   `optionId` 或 null；不得提交命令。

## 5. 第 2–3 轮缺陷修复映射

| 缺陷 | 修复 | 主要测试/断言 |
|---|---|---|
| D-1-01 私有事实缺 owner/session/round；Observation session 未校验 | `AiPrivateFactScope` + `AiCheatView.Scope`；`AiSeatSnapshot` 比较 seat/session/round；Observation.SessionId Ordinal 匹配；own/opponent/current river/current own-hand 关系校验；DeepCopy 保留绑定 | Observation session 负例；无 scope/错 seat/session/round 负例；历史 mark 组正例；当前 river 轮次负例；DeepCopy 绑定正例 |
| D-1-02 阶段/候选不匹配；普通活动身份缺失；IDLE 孤立 window | `AiDecisionContext` 强制候选 phase；IDLE 禁止非空候选；`OrdinaryAction` 有普通选项/observation 时强制 round/decision/deadline，无普通决策时要求真实 off-turn window；IDLE 禁止 window | 8 个 Probe 负例；四阶段匹配/不匹配矩阵；普通身份逐字段负例；IDLE 空候选/孤立 window 负例；off-turn 正例 |
| D-1-03 无普通 DecisionKey 的 offer 缺本座位手牌 | 新增 `AiSeatFacts`（closed/drawn/combined hand + scope）及 Snapshot/Context 透传；offer 强制 seat facts；observation 与 seat facts 手牌不一致拒绝；手牌源变更/可写容器负例 | sleeve/beam offer 无观察正例；手牌复制/只读；Observation 手牌不一致负例；错误 seat/session/round 负例；准备空手/IDLE 空手正例 |
| D-1-04 测试路径/文档不符 | 三份 `AiFrozenContext*.cs` 合并为原约定 `AiSeatSnapshotTests.cs` 与 `AiDecisionContextTests.cs`；新增本文档；保留 82 项原语义并补新负例 | 相对基线恰六路径；两测试文件 88 + 31 项；AI 全量 435 |
| D-2-01 非零余额绕过私有事实门控 | `AiCheatView.HasPrivateFacts` 纳入 `CheatPoints != 0`；技术事实另走 `HasTechniqueFacts`，仅技术事实要求当前轮；默认零余额空视图仍可无 scope；DeepCopy 保绑定 | 未绑定非零余额负例；跨 seat/session/round 复用负例；合法绑定余额正例；未建局余额正例；错轮次负例；DeepCopy 正例 |
| D-2-02 无局非空手牌/OWN_HAND 历史组绕过/仅 Observation 未校验 | `AiSeatFacts` 无当前轮时强制空手；`OWN_HAND` 使用当前已知手牌（SeatFacts 优先，否则 Observation closed+drawn），不再以 `CreatedRoundId==current` 为条件；`TileMark.SelectableTileIds` 同样核对已知手牌 | 无局 closed/drawn 负例；历史组 CURRENT OwnHand 缺牌负例/匹配正例；仅 Observation 缺牌负例/匹配正例；NotVisible 历史组正例；drawn 成员正例；selectable 负例 |

## 6. 验证命令与证据（第 3 轮最终）

工具：`D:/Unity/dotnet/dotnet.exe` 8.0.426。

| 验证 | 命令（简） | Exit | 实际计数 | 日志 SHA-256 |
|---|---|---:|---|---|
| 解决方案 Release 非增量构建 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental` | 0 | 12 projects，0 warnings，0 errors | `dfdf4cda5f1b164706fe7cd8ab712d036fe886b5f4fa7de686792f02cdd0e8fb` |
| AI 全量 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/... -c Release --no-build` | 0 | 435 passed / 0 failed / 0 skipped | `7199821f90d542b1051c77fe0307e66075740351a3200c3399eba1ae02af6bfc` |
| Domain 回归 | 同上，Domain | 0 | 322 / 0 / 0 | `e4bab7038f16456adff58f8c0d31051bb4df35466abbfd0c522bb620e27545a4` |
| Contracts 回归 | 同上，Contracts | 0 | 104 / 0 / 0 | `af615a93d4be5f35551e873badf9cdd42c57344287e1ff56a3ae76f86a74595d` |
| Client 回归 | 同上，Client | 0 | 132 / 0 / 0 | `693425dd69d68712c0387b37a9f1d1a8d937982f930a0ea6bd30f9773a970d2c` |
| 定向 `AiSeatSnapshotTests` | 直接 runner `-filter /.../AiSeatSnapshotTests` | 0 | 22 / 0 / 0 | `59e9650eaa51af69e19491bafeff19ddf1d627ecd5ac2dcd65ab9cfa7acbd900` |
| 定向 `AiSeatSnapshotIdentityTests` | 直接 runner | 0 | 66 / 0 / 0 | `485eea01e1960735c3da40838aba7342bcbea19de542788e0eba07f8021dac6b` |
| 定向 `AiDecisionContextTests` | 直接 runner | 0 | 31 / 0 / 0 | `dd82853f54fb65fc1b7655a5246ba9853c5292edfa5ad91b0a687357073a48f9` |
| `dotnet test --filter` 核对 | `... --filter 'FullyQualifiedName~AiSeatSnapshotTests'` | 0 | 仍执行 435（filter 被忽略） | `d73d00cc56a6b578fe6bea7a3da54f774ebdbdaae789c033c6ad6b42dfd091f4` |
| 独立 6 负例扩展探针（适配 API 后） | `dotnet run --project .../ai-frozen-r3-probe/Probe.csproj -c Release` | 0 | 6/6 `REJECTED`；`REPRODUCED_DEFECT_CASES=0`；全部 CONTROL 通过 | `7fc9020d9f25c6205d68cae031d4222c673948d26345cbded9eed862100f0b70` |
| 原 8 负例探针（R2 适配副本） | `dotnet run --project .../ai-frozen-r2-probe/Probe.csproj -c Release` | 0 | 8/8 `REJECTED`；`REPRODUCED_DEFECT_CASES=0`；`ownHand available=True` | `7788f21edeec8bee6afa1308d439d623fb44e015284fd56ead34fd9e27c66ed3` |

独立探针源码：`C:/Users/sncdc/AppData/Local/Temp/ai-frozen-r3-probe/Program.cs`（SHA-256 `af0be3b51eb81e9d018f1252148210be8d70a69298c726b176e8fc454e2c5122`），
`Fixtures.cs` 与 R2 相同（SHA-256 `ab3aaafa2757f91b9c6383a16e4a462d2066876902bb63e19e30f05ba70761f5`）。
该目录是复核方 TEMP 扩展探针的 API 适配副本；唯一适配是给 balance-only 正例补 `Scope()`，
6 个 MustReject 语义未删除/弱化；所有 CONTROL 均在 MustReject 外成功构造。

基线对照：第 1 轮 AI 全量 398（316 baseline + 82 new）；第 2 轮 423（316 + 107）；
第 3 轮 435（316 + 119），本轮净增 12 项。日志在 `C:/Users/sncdc/AppData/Local/Temp/`，
未纳入提交以保持六文件范围。

## 7. 未测平台/边界

- Unity、IL2CPP、Android/iOS 真机、跨物理网络、桌面 GL：**NOT_RUN**；本任务无相关改动。
- 完整 P1.6、ENG-018 工厂/能力注册、actor 接线、提交/物理移动/算分：**NOT_RUN**，不在本组件范围。
- G1：**BLOCKED**（沿用基线状态）；G5：**NOT_RUN**（无发布/灰度证据）。
- 本文件不把 V4 `Unsafe` 占位探针写成真实 `PlayerView`/`PlayerDecision` 装配验证。
