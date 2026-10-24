# V5 P1.6 AI 千术策略配置交付：AiCheatPolicy（ENG-019）

日期：2026-10-24
任务：追踪矩阵 [implementation-v1.json](implementation-v1.json) 中 `ENG-019`（`ai/AiCheatPolicy.kt`，`status: partial`，缺口"C# 千术 AI 策略未移植"）的补齐实现。
范围声明：**本任务只交付这一个纯配置组件**——不启用实际出千、不扣费、不读取权威状态、不改 `AiObservation`/Basic 策略/actor/`Domain`/`Contracts`（ENG-013 Basic 与 AiObservation 由另一任务在独立工作树交付，与本任务无文件/接口交集）。不宣告 P1.6 完整，不宣告 G1/G5 通过；V4 仍是唯一生产基线。
基线：工作树 `D:/majiang-wt/ai-cheat-policy`，分支 `wt/v5-ai-cheat-policy`，起始 HEAD `597eca1ff4688ff61fa4f5fea700bdf9eb3dd1be`（当前已验收 PR19 head），起始工作区 clean。

---

## 1. 交付文件（expectedFiles，共三个）

| 文件 | 内容 | SHA-256 |
|---|---|---|
| `dotnet/src/Mahjong.Ai/AiCheatPolicy.cs` | ENG-019 移植 | `7f5c94b5894efed654e0537b814265e63e65f485f25d0ab1e94030c91c477ad6` |
| `dotnet/tests/Mahjong.Ai.Tests/AiCheatPolicyTests.cs` | 行为矩阵测试（42 项） | `cb0bc417d027ef01e032f301cf4be891fed81d68abed19834e3f7cfa97c70b6e` |
| `doc/v5-重构/V5-P16-AI-CHEAT-POLICY.md` | 本交付文档 | 提交后见 commit |

无其他生产/测试/文档文件被修改或删除。构建产物与原始日志在 ignored 目录 `artifacts/p16-cheat-policy-logs/`（`artifacts/` 经主仓 `.git/info/exclude` 忽略），不入库。

## 2. V4 来源与逐分支映射（以 V4 实际源码为准，非类注释）

V4 来源文件（基线 HEAD 处读取）：`engine/src/main/kotlin/com/majiang/ai/AiCheatPolicy.kt`。全仓核实：`AiCheatPolicy` 在 V4 生产代码中的真实使用点为 `MatchConfig.aiCheatPolicy`（默认 `AiCheatPolicy()`，`MatchConfig.kt:60`）、`TableAuthority.buildAiSeatSnapshot` 的 `config.aiCheatPolicy.restrictWith(strategyPolicy)`（`TableAuthority.kt:2489`）、`takeoverMaySpend` 门（`:2498`）、legacy 洗牌兼容投影 `aiMaySpend && !config.aiCheatPolicy.enabled` 与预算 60（`:1736`、`:2500-2506`）；`withCapability` 在 V4 生产代码中**无调用点**（组合根预留 API），本移植同样只提供该公开方法、不自造调用方。

| V4 源码（`AiCheatPolicy.kt`） | V5 实现（`AiCheatPolicy.cs`） |
|---|---|
| 默认值 `enabled=false, maxSpendPerRound=60, reservePoints=0, riskTolerance=0.5, candidateLimit=6, takeoverMaySpend=false, capabilityToggles=emptyMap()`（`:23-30`） | 构造器可选参数逐一对应；无参构造与 V4 `AiCheatPolicy()` 完全一致（测试 `ParameterlessPolicyMatchesTheV4Defaults`） |
| `init` 校验顺序：budget≥0 → reserve≥0 → risk 非 NaN 且 ∈[0.0,1.0]（闭区间，NaN 因比较为假而必败）→ candidateLimit∈1..64 → toggle key 非空白（`:32-39`） | 同序校验，首个违规先抛（测试 `FirstInvalidRuleWinsInTheV4InitOrder`）；NaN 需显式判（C# 比较对 NaN 同样恒假）；±Infinity、1.0000000000000002、-1e-9、0/65、-1/-3、空白 key（含 `"\t"`、`" \t\n"`）全部拒绝；0.0、1.0、-0.0、1、64 接受 |
| `isCapabilityToggled`：缺 key 视为 true，**无**空白校验（`:43`） | `TryGetValue` 缺省 true；空白/未知 id 走"缺 key → true"（测试 `UnknownCapabilityIdsDefaultToEnabled`）；null id → `ArgumentNullException`（Kotlin 固有空检查 ≈ NPE） |
| `allows = enabled && isCapabilityToggled`（`:46`） | 同式，含短路：**禁用策略上 `Allows(null)` 返回 false 而不抛**——与 Kotlin `&&` 短路逐字一致（测试 `NullCapabilityIdIsRejectedLikeTheKotlinIntrinsicNullCheck` 以两种极性固化） |
| `withCapability`：require 非空白（null→NPE、空白→IAE 两条不同异常路径）；`LinkedHashMap` 拷贝后 put；`copy()` 重校验（`:48-53`） | 显式 null→`ArgumentNullException`、空白→`ArgumentException`；新实例经构造器统一重校验；既有 key 保位、新 key 追加（测试 `WithCapabilityPreservesInsertionOrderLikeALinkedHashMap`） |
| `restrictWith(null) → this` 同一引用（`:59`） | `return this`（`Assert.Same` 固化）；`restrictWith(this)` 返回相等但不同实例（Kotlin copy 语义） |
| `restrictWith` 合并：`LinkedHashSet` 并集（左 keys 先、右新 keys 后）→ 值 `isCapabilityToggled(k) && other.isCapabilityToggled(k)`；`enabled=&&`、budget=min、reserve=max、risk=min、candidate=min、takeover=逻辑 AND（`:60-74`） | 逐项同构；`minOf` 平局规则按 Kotlin `if (a<=b) a else b` 逐字实现（`MinOf` 助手，±0.0 对返回左操作数，不用 `Math.Min` 以免 .NET 符号零差异）；并集迭代序 `[a,c,b]` 用例固化 |
| data class：值相等含 `Double.equals` 位级比较（**-0.0 ≠ 0.0**；NaN 不可能入域）；Map 相等按内容、与迭代序无关；hashCode 值相关；`toString` 数据类形状 | `Equals` 用 `BitConverter.DoubleToInt64Bits` 位比较 risk（`NegativeZeroRiskToleranceDiffersFromZeroInEqualityLikeKotlin`）；toggle 表内容等值、序不敏感（`ToggleEqualityIsContentBasedAndIgnoresIterationOrder`）；`==`/`!=`/`GetHashCode` 同步；`ToString` 输出 Kotlin 字段形状（诊断用途） |

**配置≠权威**：本类型是纯配置描述——无任何花钱/读状态/加载能力 API；V4 中费用、余额、房间权威仍由 `TableAuthority` 交集裁决（`buildAiSeatSnapshot` 在 `restrictWith` 之后还叠加 `automated && (!takeover || policy.takeoverMaySpend)` 与 legacy 60 预算投影）。V5 的该投影接线属 TableRuntime/P3.x 既有合同，**不在本任务**；本移植在结构上保证 risk double 只停留在策略配置里（类型零 Domain/Contracts/网络/时钟/随机引用），不能进入 Domain 权威哈希、`CommandIntent` 规范字节或持久收费事实。无动态 DLL 注册、未知能力加载或额外钱包（类型无反射/IO 面）。

## 3. 相对 V4 的安全适配（不可变可防御复制值模型）

1. **可变 Map 引用 → 结构性防御复制**：V4 `capabilityToggles: Map<String, Boolean>` 只承诺接口只读，`copy()` 默认**浅拷贝共享同一 map**，权威不得不手工 `copy(capabilityToggles = HashMap(...))`（`TableAuthority.kt:2822-2823`）来隔离调用方可变 `LinkedHashMap`。V5 构造器一次防御复制（键序保持调用方迭代序），暴露的 `CapabilityToggles` 是私有 `OrderedToggles` 只读视图——非 `Dictionary`/`IDictionary`，**无法转型回可写表**（`ExposedToggleViewCannotBeCastBackToAWritableDictionary` 以 `as IDictionary` 为 null + 强转 `InvalidCastException` 双向固化）；构造后外部改源 map 零影响（`ConstructorCopiesTheSourceDictionary`）。
2. **迭代序**：V4 `LinkedHashMap` 插入序可观察（并集顺序、`withCapability` 保位）。V5 以 `string[] keys + Dictionary` 复刻确定性插入序；`Dictionary` 原生枚举序无契约，故不用其枚举序。
3. **字符串语义**：toggle 键查找/相等用 Ordinal、大小写敏感（Kotlin `String` map key 语义；`ToggleIdsAreComparedOrdinallyAndCaseSensitively`）。
4. **异常类型映射**：Kotlin `IllegalArgumentException` → C# `ArgumentException` 家族（数值越界用其子类 `ArgumentOutOfRangeException`，仍可被 `ArgumentException` 捕获）；Kotlin NPE → `ArgumentNullException`。消息文本按仓库惯例改为"消息 + ParamName"承载（V4 把 `got: X` 拼进消息），非逐字相同——异常文本不是行为契约，见 §5 第 1 条。
5. **哈希数值**：`GetHashCode` 数值与 Kotlin 不同（哈希本就不是契约）；相等→同哈希的不变式有测试固化。

## 4. 测试矩阵与运行证据

### 4.1 新增测试（`AiCheatPolicyTests`，42 项，全为有意义断言，非属性回读镜像）

- **总开关不可被配置绕过**：全 toggle 开 + `int.MaxValue` 预算 + takeover 允许的禁用策略对已知/未知能力一律 `Allows=false`（toggle 读值仍 true，两语义分离）；`WithCapability`/`RestrictWith`（双向）均无法复活禁用总开关。
- **takeover 默认禁止**：默认/显式 false；交集要求两侧同真。
- **校验**：budget/reserve 负值、NaN/±Infinity/越界 risk（并核 `ActualValue`）、candidate 0/65/100/-3、空白 key（值为 true 也拒）；V4 init 顺序首违先生效；边界 0.0/1.0/-0.0/1/64 接受。
- **能力语义**：未知 id/空白 id 缺省 true；Ordinal 大小写（含等值分叉）；`Allows = 总开关 ∧ toggle`；null id 的 NPE/短路双极性。
- **WithCapability**：新实例且原件零改动；LinkedHashMap 保位/追加；链式累积且标量保真；同值重写=相等不同实例；null/空白拒绝。
- **RestrictWith**：null 同实例；self 相等不同实例；标量交集方向（budget/candidate 取 min、reserve 取 max——**方向不对称**、risk 取 min、enabled/takeover 取 AND，双向验证）；未知稳定 id 并集保留且 AND 值；两侧均未提及的 id 不入 map；并集迭代序左先右后；交换律值相等但迭代序不同（非同构对）；结合律 `(a∩b)∩c == a∩(b∩c)`；6 策略 × 6 策略全对单调性性质测试（35 对 × 5 能力 id：交集允许度恰为两侧 `Allows` 的合取，标量界只缩不涨）；双侧输入零突变。
- **防御复制**：构造后改源 map 零影响；暴露视图不可转回可写字典。
- **等值语义**：七个标量字段逐一参与等值（含 toggle 内容变化）；内容等值与序无关；-0.0 与 0.0 位级不等（数值比较相等）；运算符与 Equals 全对齐（含 null/null）。
- **诊断**：`ToString` 确定且为 Kotlin 数据类形状。

### 4.2 运行命令、退出码与计数（全部真实执行，dotnet 8.0.426 @ `D:/Unity/dotnet/dotnet.exe`）

| # | 命令（cwd = 工作树根） | exit | 结果 |
|---|---|---|---|
| 1 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj`（Debug 全量） | 0 | **92 通过 / 0 失败 / 0 跳过**（既有 50 + 新增 42） |
| 2 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal` | 0 | **92 通过 / 0 失败 / 0 跳过** |
| 3 | 同上加 `-- --filter-class "Mahjong.Ai.Tests.AiCheatPolicyTests"`（MTP，仓库既有模式） | 0 | **42 通过 / 0 失败 / 0 跳过**（2 的子集，不重复计数） |
| 4 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal` | 0 | **322 通过 / 0 失败 / 0 跳过**（与 PR19 基线计数一致） |
| 5 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal` | 0 | **104 通过 / 0 失败 / 0 跳过**（同上） |
| 6 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --nologo -v:minimal` | 0 | **132 通过 / 0 失败 / 0 跳过**（同上） |
| 7 | `dotnet build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 0 | 已成功生成，**0 警告 / 0 错误** |
| 8 | `git diff --check` | 0 | 无空白错误（提交前对暂存区再复核） |

Total 均非 0、无 skip、无旧 DLL 凑绿（1/2 号命令当场重建）；未修改任何 verifier、探针、黄金 fixture（`tests/v5-golden/ai-v1.json` 原样随套件通过）、追踪矩阵或统一 verifier；未弱化任何既有断言。黄金回放 `AiReplayGoldenTests` 在同一工程内随全量运行通过。

开发期迭代（如实记录，非交付后失败）：首次 Debug 运行因 xUnit2013 分析器（4 处 `Assert.Equal(1, …Count)` 应为 `Assert.Single`）编译失败；第二次编译通过后 2 项失败——`WithCapability(null)` 实现走了空白路径（Kotlin 语义应为 NPE 等价，已改实现加显式 null 检查）、`Allows(null)` 测试预期未考虑 Kotlin `&&` 短路（实现正确，已改测试）。第三次起全绿。原始开发期日志被后续运行覆盖，最终态以本节日志为准。

原始日志（ignored 目录，含 SHA-256）：
`artifacts/p16-cheat-policy-logs/debug-test-Mahjong.Ai.Tests.log` `0c0b6df1e3ccdb9e924869c89c9bc8005cf7447b6a166d7ca0897698cb9f513e`、
`.../release-test-Mahjong.Ai.Tests.log` `2c7d555005be7d70bd25f346a0267d56d3bfb49c4ea4390ba89cede226d8dbef`、
`.../release-test-AiCheatPolicyTests-filtered.log` `1e3b1a439c193c36a4d5f0ca398b15bcd570a6072c450545e156c47f5098f0b2`、
`.../release-test-Mahjong.Domain.Tests.log` `b124bd8c911c8343c6f6958c4e112680e8b45a0039960a329a5c8e6e403fbcb9`、
`.../release-test-Mahjong.Contracts.Tests.log` `8a8e172049899aedec217c3dbe5c44f77366cf79830839b6b4fd0f2e965bf728`、
`.../release-test-Mahjong.Client.Tests.log` `ade5d4085620f2eeb9333955a1a38f283a5938690161e0d6a5fbe9b6df7e18b6`、
`.../release-build-solution.log` `78499ea99245aa9b3fc872bc5e407df75c5f3418add9599e75eaa8ba7b0d25a8`。

### 4.3 未运行项（如实声明 NOT_RUN）

| 套件/平台 | 状态 | 理由 |
|---|---|---|
| `Mahjong.TableRuntime.Tests`、`Mahjong.Client.Wss.Tests`、`Mahjong.LobbyService.Tests`、LobbyService PostgresProbe、真实 PG / WSS 大套件 | **NOT_RUN** | 与纯新增配置类型无编译/行为耦合（`Mahjong.Ai` 无新依赖，Solution Release 构建已覆盖全部工程编译面），按任务指示不重跑 |
| Unity / IL2CPP / Android / iOS 真机、V4 Gradle 全量、跨物理网络 | **NOT_RUN** | 不在本任务范围；V4 T4 等历史缺口维持原状 |

## 5. 全部已知缺陷与偏差（一次性给全，按复核协议 §1）

| # | 级别 | 位置 | 说明 |
|---|---|---|---|
| 1 | MINOR | `AiCheatPolicy.cs` 校验/守卫 | 异常**文本与子类型**为 .NET 惯例：数值越界用 `ArgumentOutOfRangeException`（V4 恒为 `IllegalArgumentException`）、消息经 ParamName 承载（V4 拼入 `got: X`）。行为契约（抛不抛、抛的时机与参数命名）与 V4 一致；仅文本/子类型差异。 |
| 2 | MINOR | `AiCheatPolicy.cs` 空白判定 | Kotlin `isBlank` 基于 `Character.isWhitespace`（不含 NBSP U+00A0 等 no-break 空格），C# `IsNullOrWhiteSpace` 含之：NBSP-only key 在 V4 可用、V5 拒绝。能力 ID 实践中为 ASCII 稳定串，无产品影响。 |
| 3 | MINOR | `AiCheatPolicy.cs` `ToString` | risk 的诊断渲染用 invariant 格式（如 `-0.0`→`-0`），与 Java `Double.toString` 拼写可有差异；非行为契约。`GetHashCode` 数值与 Kotlin 不同（哈希非契约，相等→同哈希不变式已测）。 |
| 4 | MINOR | 范围边界（非本文件） | 迁移方案 `planTarget="Mahjong.Ai + Domain 配置"` 与矩阵 `v5Targets` 中的 Domain 接线（`MatchConfig` 等价物、`TableAuthority` 投影含 legacy `aiMaySpend && !enabled`/60 预算路径）**未实现**——简报明确不改 Domain/actor，接线属 TableRuntime/P3.x 与后续任务；ENG-019 在矩阵中仍应视为 partial，status 收敛属 caller/独立复核职权。 |
| 5 | MINOR | 范围边界（非本文件） | `withCapability` 与 V4 一样无生产调用点（已全仓核实）；本移植按公开 API 面保真提供，未自造调用方或接线。 |

无 BLOCKER/MAJOR 级已知缺陷。

## 6. 结论与边界

- ENG-019 的"C# 千术 AI 策略未移植"缺口已按 V4 实际源码行为补齐：默认关闭、总开关与能力交集、预算候选取 min、reserve 取 max、risk 取 min、takeover 逻辑 AND、并集保留未知稳定 ID、Ordinal 大小写语义、NaN/Infinity/负值/越界拒绝——42 项行为矩阵全绿，既有 50 项与黄金回放不受影响。
- 配置组件本身**不构成实际千术 AI 能力**：无候选生成、无扣费、无权威状态读取；实际能力启用仍待能力适配器与 TableRuntime 接线（后续任务）。**P1.6 不因本交付而宣告完整，G1 未过、G5 未过**；V4 仍是唯一生产基线。
- 追踪矩阵 `implementation-v1.json` 的 ENG-019 status 不由本任务改写（矩阵是已验收产物，改写属 caller/独立复核职权）。
- 交付方式：本地 commit，工作树 clean 后停止，等待 caller 独立复核；不 push、不触发 CI、不创建新 PR、不转派。

## 7. Caller 独立验收

（由 caller 复核后追加；本节为占位，不预写验收结论。）
