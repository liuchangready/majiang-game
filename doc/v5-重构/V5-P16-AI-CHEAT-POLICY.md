# V5 P1.6 AI 千术策略配置交付：AiCheatPolicy（ENG-019）

日期：2026-10-24（第 1 轮交付）；2026-10-25（第 2 轮返修：按 caller 独立复核发现的 D-1-01/02/03 三组缺陷一次批量修复，并复跑全部验证）
任务：追踪矩阵 [implementation-v1.json](implementation-v1.json) 中 `ENG-019`（`ai/AiCheatPolicy.kt`，`status: partial`，缺口"C# 千术 AI 策略未移植"）的补齐实现。
范围声明：**本任务只交付这一个纯配置组件**——不启用实际出千、不扣费、不读取权威状态、不改 `AiObservation`/Basic 策略/actor/`Domain`/`Contracts`（ENG-013 Basic 与 AiObservation 由另一任务在独立工作树交付，与本任务无文件/接口交集）。不宣告 P1.6 完整，不宣告 G1/G5 通过；V4 仍是唯一生产基线。
基线：工作树 `D:/majiang-wt/ai-cheat-policy`，分支 `wt/v5-ai-cheat-policy`，起始 HEAD `597eca1ff4688ff61fa4f5fea700bdf9eb3dd1be`（当前已验收 PR19 head），起始工作区 clean。

---

## 1. 交付文件（expectedFiles，共三个）

| 文件 | 内容 | SHA-256（第 2 轮返修后） |
|---|---|---|
| `dotnet/src/Mahjong.Ai/AiCheatPolicy.cs` | ENG-019 移植 | `a5ad09388c04168938717b918007cd0326b09111a204be4e1d2ee757d0135e05` |
| `dotnet/tests/Mahjong.Ai.Tests/AiCheatPolicyTests.cs` | 行为矩阵测试（44 项） | `6ebc087cf27f875606edc06c61071bfd64605cf1b70ec734a2443a375edb74a4` |
| `doc/v5-重构/V5-P16-AI-CHEAT-POLICY.md` | 本交付文档 | 提交后见 commit |

第 1 轮 SHA（历史留档）：`AiCheatPolicy.cs` = `7f5c94b5894efed654e0537b814265e63e65f485f25d0ab1e94030c91c477ad6`，`AiCheatPolicyTests.cs`（42 项）= `cb0bc417d027ef01e032f301cf4be891fed81d68abed19834e3f7cfa97c70b6e`。已提交的第 1 轮 commit（`f4c001e`）不篡改，返修以新 commit 落地。

无其他生产/测试/文档文件被修改或删除。构建产物与原始日志在 ignored 目录 `artifacts/p16-cheat-policy-logs/`（`artifacts/` 经主仓 `.git/info/exclude` 忽略），不入库。

## 2. V4 来源与逐分支映射（以 V4 实际源码为准，非类注释）

V4 来源文件（基线 HEAD 处读取）：`engine/src/main/kotlin/com/majiang/ai/AiCheatPolicy.kt`。全仓核实：`AiCheatPolicy` 在 V4 生产代码中的真实使用点为 `MatchConfig.aiCheatPolicy`（默认 `AiCheatPolicy()`，`MatchConfig.kt:60`）、`TableAuthority.buildAiSeatSnapshot` 的 `config.aiCheatPolicy.restrictWith(strategyPolicy)`（`TableAuthority.kt:2489`）、`takeoverMaySpend` 门（`:2498`）、legacy 洗牌兼容投影 `aiMaySpend && !config.aiCheatPolicy.enabled` 与预算 60（`:1736`、`:2500-2506`）；`withCapability` 在 V4 生产代码中**无调用点**（组合根预留 API），本移植同样只提供该公开方法、不自造调用方。

| V4 源码（`AiCheatPolicy.kt`） | V5 实现（`AiCheatPolicy.cs`） |
|---|---|
| 默认值 `enabled=false, maxSpendPerRound=60, reservePoints=0, riskTolerance=0.5, candidateLimit=6, takeoverMaySpend=false, capabilityToggles=emptyMap()`（`:23-30`） | 构造器可选参数逐一对应；无参构造与 V4 `AiCheatPolicy()` 完全一致（测试 `ParameterlessPolicyMatchesTheV4Defaults`） |
| `init` 校验顺序：budget≥0 → reserve≥0 → risk 非 NaN 且 ∈[0.0,1.0]（闭区间，NaN 因比较为假而必败）→ candidateLimit∈1..64 → toggle key 非空白（`:32-39`） | 同序校验，首个违规先抛（测试 `FirstInvalidRuleWinsInTheV4InitOrder`）；NaN 需显式判（C# 比较对 NaN 同样恒假）；±Infinity、1.0000000000000002、-1e-9、0/65、-1/-3、空白 key（值为 true 也拒）全部拒绝；0.0、1.0、-0.0、1、64 接受。**空白判定为 V4 Kotlin 语义**（`isBlank` = 每字符满足 `Char.isWhitespace()` = `Character.isWhitespace(c) \|\| Character.isSpaceChar(c)`）：0x001C–0x001F 是 V4 空白（.NET 不算）→ 拒绝；0x0085 非 V4 空白（.NET 算）→ 可作稳定 ID；NBSP U+00A0 / U+2007 / U+202F 两侧同为空白 → 拒绝。全 BMP 空白集经 caller 探针逐点核实，V5 私有 `IsKotlinWhitespace` 按此差集精确复刻（测试 `KotlinBlankRuleMatchesTheV4WhitespaceSetOnBothEntryPoints`，构造器与 `WithCapability` 双入口） |
| `isCapabilityToggled`：缺 key 视为 true，**无**空白校验（`:43`） | `TryGetValue` 缺省 true；空白/未知 id 走"缺 key → true"（测试 `UnknownCapabilityIdsDefaultToEnabled`）；null id → `ArgumentNullException`（Kotlin 固有空检查 ≈ NPE） |
| `allows = enabled && isCapabilityToggled`（`:46`） | 有效 id 上保留 `&&` 短路（禁用策略对任意有效 id 返回 false）；**Kotlin 编译器在方法入口、先于函数体注入固有参数 null 检查，故 `Allows(null)` 在总开关两种极性下都抛**（Kotlin NPE → `ArgumentNullException`；caller 以真实 `AiCheatPolicy.kt` + kotlinc 2.4.10 编译执行核实，D-1-02）。第 1 轮"短路使禁用策略免检 null"的判断是误读，第 2 轮已修正实现（入口 null 检查）与测试（`NullCapabilityIdIsRejectedLikeTheKotlinIntrinsicNullCheck` 双极性固化 + 有效 id 短路路径仍 false） |
| `withCapability`：require 非空白（null→NPE、空白→IAE 两条不同异常路径）；`LinkedHashMap` 拷贝后 put；`copy()` 重校验（`:48-53`） | 显式 null→`ArgumentNullException`、空白→`ArgumentException`；新实例经构造器统一重校验；既有 key 保位、新 key 追加（测试 `WithCapabilityPreservesInsertionOrderLikeALinkedHashMap`） |
| `restrictWith(null) → this` 同一引用（`:59`） | `return this`（`Assert.Same` 固化）；`restrictWith(this)` 返回相等但不同实例（Kotlin copy 语义） |
| `restrictWith` 合并：`LinkedHashSet` 并集（左 keys 先、右新 keys 后）→ 值 `isCapabilityToggled(k) && other.isCapabilityToggled(k)`；`enabled=&&`、budget=min、reserve=max、risk=min、candidate=min、takeover=逻辑 AND（`:60-74`） | 逐项同构；**`minOf(Double,Double)` 按 Kotlin 实际委托的 `java.lang.Math.min` 语义实现**（`MinOf` 助手）：两侧均为零时**负零在任一参数序下都胜出**——(+0,−0) 与 (−0,+0) 均得 −0.0（位型 `long.MinValue`）；相等非零值位级同值返回左参。显式实现该规则以固定 netstandard2.1 兼容边界（现代 .NET 的 `Math.Min` 同样在两向选择负零；caller 探针 D-1-01 实测 V4 两向均为 −9223372036854775808，第 1 轮"±0.0 返回左参"的移植是错的，已修正——测试 `RestrictWithRiskZeroSignFollowsJavaLangMathMin`）。并集迭代序 `[a,c,b]` 用例固化 |
| data class：值相等含 `Double.equals` 位级比较（**-0.0 ≠ 0.0**；NaN 不可能入域）；Map 相等按内容、与迭代序无关；hashCode 值相关；`toString` 数据类形状 | `Equals` 用 `BitConverter.DoubleToInt64Bits` 位比较 risk（`NegativeZeroRiskToleranceDiffersFromZeroInEqualityLikeKotlin`）；toggle 表内容等值、序不敏感（`ToggleEqualityIsContentBasedAndIgnoresIterationOrder`）；`==`/`!=`/`GetHashCode` 同步；`ToString` 输出 Kotlin 字段形状（诊断用途） |

**配置≠权威**：本类型是纯配置描述——无任何花钱/读状态/加载能力 API；V4 中费用、余额、房间权威仍由 `TableAuthority` 交集裁决（`buildAiSeatSnapshot` 在 `restrictWith` 之后还叠加 `automated && (!takeover || policy.takeoverMaySpend)` 与 legacy 60 预算投影）。V5 的该投影接线属 TableRuntime/P3.x 既有合同，**不在本任务**；本移植在结构上保证 risk double 只停留在策略配置里（类型零 Domain/Contracts/网络/时钟/随机引用），不能进入 Domain 权威哈希、`CommandIntent` 规范字节或持久收费事实。无动态 DLL 注册、未知能力加载或额外钱包（类型无反射/IO 面）。

## 3. 相对 V4 的安全适配（不可变可防御复制值模型）

1. **可变 Map 引用 → 结构性防御复制**：V4 `capabilityToggles: Map<String, Boolean>` 只承诺接口只读，`copy()` 默认**浅拷贝共享同一 map**，权威不得不手工 `copy(capabilityToggles = HashMap(...))`（`TableAuthority.kt:2822-2823`）来隔离调用方可变 `LinkedHashMap`。V5 构造器一次防御复制（键序保持调用方迭代序），暴露的 `CapabilityToggles` 是私有 `OrderedToggles` 只读视图——非 `Dictionary`/`IDictionary`，**无法转型回可写表**（`ExposedToggleViewCannotBeCastBackToAWritableDictionary` 以 `as IDictionary` 为 null + 强转 `InvalidCastException` 双向固化）；构造后外部改源 map 零影响（`ConstructorCopiesTheSourceDictionary`）。
2. **迭代序**：V4 `LinkedHashMap` 插入序可观察（并集顺序、`withCapability` 保位）。V5 以 `string[] keys + Dictionary` 复刻确定性插入序；`Dictionary` 原生枚举序无契约，故不用其枚举序。
3. **字符串语义**：toggle 键查找/相等用 Ordinal、大小写敏感（Kotlin `String` map key 语义；`ToggleIdsAreComparedOrdinallyAndCaseSensitively`）。
4. **异常类型映射**：Kotlin `IllegalArgumentException` → C# `ArgumentException` 家族（数值越界用其子类 `ArgumentOutOfRangeException`，仍可被 `ArgumentException` 捕获）；Kotlin NPE → `ArgumentNullException`。消息文本按仓库惯例改为"消息 + ParamName"承载（V4 把 `got: X` 拼进消息），非逐字相同——异常文本不是行为契约，见 §5 第 1 条。
5. **哈希数值**：`GetHashCode` 数值与 Kotlin 不同（哈希本就不是契约）；相等→同哈希的不变式有测试固化。

## 4. 测试矩阵与运行证据

### 4.1 新增测试（`AiCheatPolicyTests`，第 2 轮返修后共 44 项，全为有意义断言，非属性回读镜像）

- **总开关不可被配置绕过**：全 toggle 开 + `int.MaxValue` 预算 + takeover 允许的禁用策略对已知/未知能力一律 `Allows=false`（toggle 读值仍 true，两语义分离）；`WithCapability`/`RestrictWith`（双向）均无法复活禁用总开关。
- **takeover 默认禁止**：默认/显式 false；交集要求两侧同真。
- **校验**：budget/reserve 负值、NaN/±Infinity/越界 risk（并核 `ActualValue`）、candidate 0/65/100/-3、空白 key（值为 true 也拒）；V4 init 顺序首违先生效；边界 0.0/1.0/-0.0/1/64 接受。
- **Kotlin 空白全集矩阵**（第 2 轮新增，`KotlinBlankRuleMatchesTheV4WhitespaceSetOnBothEntryPoints`）：拒绝集 0x0009–0x000D、0x0020、0x001C–0x001F、0x00A0、0x1680、0x202F、0x2028、0x2029、0x205F、0x3000 在构造器与 `WithCapability` 双入口全拒（含 `capabilityId` ParamName 核对），U+2000–0x200A 全块逐点扫拒（非抽样）；0x0085 双入口接受为稳定 ID（值 false 仍可查）；混串规则双向（含 0x001C 的混串 V4 空白而 .NET 不空白 → 拒；含 0x0085 的混串 .NET 空白而 V4 不空白 → 收）；0x0085 ID 经 `RestrictWith` 并集存续；可见内容永收。
- **能力语义**：未知 id/空白 id 缺省 true；Ordinal 大小写（含等值分叉）；`Allows = 总开关 ∧ toggle`；null id 双极性均抛 `ArgumentNullException`（D-1-02 修正后语义，含有效 id 短路仍 false 的对照）。
- **WithCapability**：新实例且原件零改动；LinkedHashMap 保位/追加；链式累积且标量保真；同值重写=相等不同实例；null/空白拒绝（空白含 0x001C–0x001F）。
- **RestrictWith**：null 同实例；self 相等不同实例；标量交集方向（budget/candidate 取 min、reserve 取 max——**方向不对称**、risk 取 min、enabled/takeover 取 AND，双向验证）；**risk 符号零 `java.lang.Math.min` 语义**（第 2 轮新增 `RestrictWithRiskZeroSignFollowsJavaLangMathMin`：(+0,−0)/（−0,+0) 位型均 `long.MinValue`、(+0,+0)=0、(−0,−0)=`long.MinValue`，两向值可交换、与 (+0,+0) 位级不等且哈希不同，与非零 risk 混合时零的符号按数值最小侧存续）；未知稳定 id 并集保留且 AND 值；两侧均未提及的 id 不入 map；并集迭代序左先右后；交换律值相等但迭代序不同（非同构对）；结合律 `(a∩b)∩c == a∩(b∩c)`；6 策略 × 6 策略全对单调性性质测试（35 对 × 5 能力 id：交集允许度恰为两侧 `Allows` 的合取，标量界只缩不涨）；双侧输入零突变。
- **防御复制**：构造后改源 map 零影响；暴露视图不可转回可写字典。
- **等值语义**：七个标量字段逐一参与等值（含 toggle 内容变化）；内容等值与序无关；-0.0 与 0.0 位级不等（数值比较相等）；运算符与 Equals 全对齐（含 null/null）。
- **诊断**：`ToString` 确定且为 Kotlin 数据类形状。

### 4.2 运行命令、退出码与计数（全部真实执行，dotnet 8.0.426 @ `D:/Unity/dotnet/dotnet.exe`）

**第 1 轮（历史留档，代码已被第 2 轮更新，计数以第 2 轮为准）：**

| # | 命令（cwd = 工作树根） | exit | 结果 |
|---|---|---|---|
| 1 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj`（Debug 全量） | 0 | **92 通过 / 0 失败 / 0 跳过**（既有 50 + 当轮 42） |
| 2 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --nologo -v:minimal` | 0 | **92 通过 / 0 失败 / 0 跳过** |
| 3 | 同上加 `-- --filter-class "Mahjong.Ai.Tests.AiCheatPolicyTests"`（MTP，仓库既有模式） | 0 | **42 通过 / 0 失败 / 0 跳过** |
| 4-6 | Domain / Contracts / Client `-c Release` | 0 | 322 / 104 / 132 通过，0 失败 |
| 7 | `dotnet build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 0 | 0 警告 / 0 错误 |
| 8 | `git diff --check` | 0 | 无空白错误 |

第 1 轮日志 SHA-256（ignored 目录 `artifacts/p16-cheat-policy-logs/`）：
`debug-test-Mahjong.Ai.Tests.log` `0c0b6df1e3ccdb9e924869c89c9bc8005cf7447b6a166d7ca0897698cb9f513e`、
`release-test-Mahjong.Ai.Tests.log` `2c7d555005be7d70bd25f346a0267d56d3bfb49c4ea4390ba89cede226d8dbef`、
`release-test-AiCheatPolicyTests-filtered.log` `1e3b1a439c193c36a4d5f0ca398b15bcd570a6072c450545e156c47f5098f0b2`、
`release-test-Mahjong.Domain.Tests.log` `b124bd8c911c8343c6f6958c4e112680e8b45a0039960a329a5c8e6e403fbcb9`、
`release-test-Mahjong.Contracts.Tests.log` `8a8e172049899aedec217c3dbe5c44f77366cf79830839b6b4fd0f2e965bf728`、
`release-test-Mahjong.Client.Tests.log` `ade5d4085620f2eeb9333955a1a38f283a5938690161e0d6a5fbe9b6df7e18b6`、
`release-build-solution.log` `78499ea99245aa9b3fc872bc5e407df75c5f3418add9599e75eaa8ba7b0d25a8`。

**第 2 轮返修复验（当前代码，全部真实执行）：**

| # | 命令（cwd = 工作树根） | exit | 结果 |
|---|---|---|---|
| R1 | `powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/eng019-caller-d84d/run-parity.ps1`（caller 统一 V4/C# parity 探针：kotlinc 2.4.10 编译真实 `AiCheatPolicy.kt` + `Probe.kt` 执行，再 `dotnet run` 同探针 C# 侧；探针未修改，`Probe.csproj` 直接引用本工作树源码；本表为最后一次全绿重跑结果） | **0** | **`V4 parity failures=0`**（第 1 轮为 exit 1 / failures=7）。逐项对照：`restrict(+0,-0)` 与 `restrict(-0,+0)` 两侧位型均 `-9223372036854775808`（D-1-01 ✓）；`allows(null),enabled=false/true` Kotlin 侧均 `NullPointerException`、C# 侧均 `ArgumentNullException`（D-1-02 ✓）；U+001C–001F ctor/withCapability 全 REJECT、U+0085 全 ACCEPT、U+00A0/2007/202F 全 REJECT，Kotlin 与 C# 逐点一致（D-1-03 ✓） |
| R2 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Debug` | 0 | **94 通过 / 0 失败 / 0 跳过**（既有 50 基线 + 黄金回放不受影响 + 本组件 44） |
| R3 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release` | 0 | **94 通过 / 0 失败 / 0 跳过** |
| R4 | R3 + `-- --filter-class "Mahjong.Ai.Tests.AiCheatPolicyTests"` | 0 | **44 通过 / 0 失败 / 0 跳过**（第 1 轮 42 项中 1 项按 D-1-02 修正断言语义、1 项按 D-1-03 扩展拒绝码点，断言只收紧不放松；另新增 2 项：空白全集矩阵、risk 符号零矩阵） |
| R5 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/… -c Release` | 0 | **322 通过 / 0 失败 / 0 跳过**（与第 1 轮及 PR19 基线计数一致） |
| R6 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/… -c Release` | 0 | **104 通过 / 0 失败 / 0 跳过**（同上） |
| R7 | `dotnet test dotnet/tests/Mahjong.Client.Tests/… -c Release` | 0 | **132 通过 / 0 失败 / 0 跳过**（同上） |
| R8 | `dotnet build dotnet/Mahjong.sln -c Release --no-restore` | 0 | **0 警告 / 0 错误** |
| R9 | `git diff --check` | 0 | 无空白错误（输出仅 autocrlf 换行提示，非检查失败） |

Total 均非 0、无 skip、无旧 DLL 凑绿（R2/R3 当场重建）；未修改 caller 探针、任何 verifier、黄金 fixture（`tests/v5-golden/ai-v1.json` 原样随套件通过）、追踪矩阵或统一 verifier；未弱化任何既有断言（第 1 轮 42 项中仅按 D-1-02 把错误断言改为 V4 真实语义，属纠错而非放水）。

开发期迭代与误判更正（如实记录）：第 1 轮开发期，首次 Debug 运行因 xUnit2013 分析器（4 处 `Assert.Equal(1, …Count)` 应为 `Assert.Single`）编译失败；第二次编译通过后 2 项失败——`WithCapability(null)` 实现走了空白路径（已改实现加显式 null 检查，正确保留）；**`Allows(null)` 一项当时被我改测试断言为"禁用策略返回 false"，理由是 Kotlin `&&` 短路——该判断是误判**：caller 第 2 轮以真实 `AiCheatPolicy.kt` + kotlinc 2.4.10 编译执行证明 Kotlin 在方法入口、先于函数体注入固有 null 检查，两种极性都抛 NPE（D-1-02）。第 2 轮已同步修正实现（`Allows` 入口 null 检查，注释注明依据）与测试（双极性抛 `ArgumentNullException`），并把本节及 §2/§5 中相应错误表述一并更正。第 1 轮原始开发期日志已被后续运行覆盖，最终态以本节两轮日志为准。

第 2 轮日志（ignored 目录，最终全绿重跑后 SHA-256 实测值）：
`artifacts/p16-cheat-policy-logs/r2-parity-probe.log` `4d11a03e9d66fae23e4b3c4cd4cdd1723ce1bae3ea1fc3a2e2478ae7a0d553a3`、
`.../r2-debug-test-Mahjong.Ai.Tests.log` `4b1450cdd35c1256ace66a3b4ffc0c332b87dac4fd0ba31944203ca11bda4536`、
`.../r2-release-test-Mahjong.Ai.Tests.log` `caaca658c1116b85a10739ff2b35e38c6788f6c22bcda867cef5ec3714a03dd5`、
`.../r2-release-test-AiCheatPolicyTests-filtered.log` `07f0c9ca4cc80836d1b05b3d6b46bbbcb70599072a6f768e740c7aff32e751bd`、
`.../r2-release-test-Mahjong.Domain.Tests.log` `788415619846cd208fc319ad6a3e8b3b87ae11de9e89f35143d7c477d9b732d3`、
`.../r2-release-test-Mahjong.Contracts.Tests.log` `944f066d85dcd67068485613e280860f648d6bf85e2fa8a3b9a673fc9bed0c0c`、
`.../r2-release-test-Mahjong.Client.Tests.log` `c60596f818508cf48454c3cc9aaafd4c33c14d540e30f1cbbbab8bc454e80b99`、
`.../r2-release-build-solution.log` `7512efd5a1be64822cc3bc690cbe592d146461e4477eeb0ba2db05cd424df36a`、
`.../r2-git-diff-check.log` `0b891bd676fce7242d66507f43a12f77eb6d4041100f7e443f958eb125f84ace`。

### 4.3 未运行项（如实声明 NOT_RUN）

| 套件/平台 | 状态 | 理由 |
|---|---|---|
| `Mahjong.TableRuntime.Tests`、`Mahjong.Client.Wss.Tests`、`Mahjong.LobbyService.Tests`、LobbyService PostgresProbe、真实 PG / WSS 大套件 | **NOT_RUN** | 与纯新增配置类型无编译/行为耦合（`Mahjong.Ai` 无新依赖，Solution Release 构建已覆盖全部工程编译面），按任务指示不重跑 |
| Unity / IL2CPP / Android / iOS 真机、V4 Gradle 全量、跨物理网络 | **NOT_RUN** | 不在本任务范围；V4 T4 等历史缺口维持原状 |

## 5. 全部已知缺陷与偏差（一次性给全，按复核协议 §1）

### 5.1 第 2 轮 caller 复核发现并已同轮修复（D-1-01/02/03）

第 1 轮交付虽全绿，但 caller 用真实 V4 `AiCheatPolicy.kt` + kotlinc/stdlib 2.4.10 编译执行的独立 C# parity 探针报告 7 处不一致（2 MAJOR + 1 MINOR），证明"套件绿"不等于"源码语义保真"。三组缺陷一次批量修复，逐条映射：

| ID | 级别 | 缺陷（第 1 轮实际 vs V4 期望） | 修复 | 验收 |
|---|---|---|---|---|
| D-1-01 | MAJOR | `RestrictWith` risk `minOf` 按"`if (a<=b) a else b` 平局返回左参"移植：(+0,−0) 得 +0.0；V4 `minOf(Double,Double)` 委托 `java.lang.Math.min`，两向均得 −0.0（位型 `long.MinValue`） | `MinOf(double,double)` 按 Math.min 语义重写：NaN 臂 + 严格小于比较 + 相等零对按位型符号位 OR 定符号（`AiCheatPolicy.cs`） | 探针 `restrict(+0,-0)`/`restrict(-0,+0)` 均 `-9223372036854775808`；测试 `RestrictWithRiskZeroSignFollowsJavaLangMathMin` 固化四种零组合 + 两向交换 + 与非零混合 |
| D-1-02 | MINOR | 第 1 轮在 `Allows` 上依赖 `&&` 短路，`Allows(null)` 于禁用策略返回 false；V4 Kotlin 编译器在方法入口先于函数体注入固有 null 检查，两种极性都抛 NPE | `Allows` 入口显式 null 检查 → `ArgumentNullException`（注释注明依据）；同步修正第 1 轮被误改的测试断言，并在 §2/§4.2 更正误判记录 | 探针 `allows(null),enabled=false/true` Kotlin 侧 `NullPointerException`、C# 侧 `ArgumentNullException` 双极性一致；测试 `NullCapabilityIdIsRejectedLikeTheKotlinIntrinsicNullCheck` 双极性 + 有效 id 短路仍 false |
| D-1-03 | MAJOR | 空白判定用 .NET `char.IsWhiteSpace` 全集：0x001C–0x001F（Java 独有空白）被误收、0x0085（next line，非 Java 空白/非 space char）被误拒；且第 1 轮文档"NBSP 在 V4 可用"的断言本身有误（`isSpaceChar` 含 NBSP，V4 同样拒绝） | 私有 `IsKotlinBlank`/`IsKotlinWhitespace` 按 Kotlin `isBlank` 精确复刻（`.NET IsWhiteSpace` ∪ {0x001C–0x001F} ∖ {0x0085}，与探针 blankBMP 全集逐点核对一致）；构造器与 `WithCapability` 双入口均改用之；共享 `AiGuard` 未动 | 探针 U+001C–001F 全 REJECT、U+0085 全 ACCEPT、U+00A0/2007/202F 全 REJECT（与 Kotlin 逐点一致）；测试 `KotlinBlankRuleMatchesTheV4WhitespaceSetOnBothEntryPoints`（双入口 × 19 码点拒绝集 + 0x0085 存续 + 混串双向）及空白校验用例扩展 0x001C–0x001F |

修复后统一探针 **exit 0 / `V4 parity failures=0`**（第 1 轮 exit 1 / failures=7，证据：§4.2 R1 行与 `r2-parity-probe.log`）。

### 5.2 仍然存留的已知偏差（当前代码，无 BLOCKER/MAJOR）

| # | 级别 | 位置 | 说明 |
|---|---|---|---|
| 1 | MINOR | `AiCheatPolicy.cs` 校验/守卫 | 异常**文本与子类型**为 .NET 惯例：数值越界用 `ArgumentOutOfRangeException`（V4 恒为 `IllegalArgumentException`）、消息经 ParamName 承载（V4 拼入 `got: X`）。行为契约（抛不抛、抛的时机、null 与空白的异常分型）与 V4 一致；仅文本/子类型差异。 |
| 2 | MINOR | `AiCheatPolicy.cs` `ToString`/哈希 | risk 的诊断渲染用 invariant 格式（如 `-0.0`→`-0`），与 Java `Double.toString` 拼写可有差异；非行为契约。`GetHashCode` 数值与 Kotlin 不同（哈希非契约，相等→同哈希不变式已测）。 |
| 3 | MINOR | 范围边界（非本文件） | 迁移方案 `planTarget="Mahjong.Ai + Domain 配置"` 与矩阵 `v5Targets` 中的 Domain 接线（`MatchConfig` 等价物、`TableAuthority` 投影含 legacy `aiMaySpend && !enabled`/60 预算路径）**未实现**——简报明确不改 Domain/actor，接线属 TableRuntime/P3.x 与后续任务；ENG-019 在矩阵中仍应视为 partial，status 收敛属 caller/独立复核职权。 |
| 4 | MINOR | 范围边界（非本文件） | `withCapability` 与 V4 一样无生产调用点（已全仓核实）；本移植按公开 API 面保真提供，未自造调用方或接线。 |

第 1 轮缺陷表第 2 条（"NBSP-only key 在 V4 可用、V5 拒绝"）**作废**：该条对 V4 的描述有误（`Character.isSpaceChar` 含 NBSP，V4 拒绝 NBSP-only key），且其背后真实的空白集偏差即 D-1-03，已修复；第 2 轮复核未发现新的已知缺陷。

## 6. 结论与边界

- ENG-019 的"C# 千术 AI 策略未移植"缺口已按 V4 实际源码行为补齐：默认关闭、总开关与能力交集、预算候选取 min、reserve 取 max、risk 取 min（含 `java.lang.Math.min` 符号零语义）、takeover 逻辑 AND、并集保留未知稳定 ID、Ordinal 大小写语义、V4 Kotlin 空白全集、`allows` 入口固有 null 检查、NaN/Infinity/负值/越界拒绝——44 项行为矩阵全绿，既有 50 项基线与黄金回放不受影响；caller 统一 parity 探针（真实 V4 源码编译执行对照）exit 0、0 失败。
- 第 1 轮的教训已如实记录（§4.2/§5.1）：测试全绿不能证明源码保真，`allows` null 语义与 `minOf` 符号零两处是靠对 V4 的直接编译执行对照才暴露的；后续移植任务应优先建立同款 parity 探针而非只依赖自写套件。
- 配置组件本身**不构成实际千术 AI 能力**：无候选生成、无扣费、无权威状态读取；实际能力启用仍待能力适配器与 TableRuntime 接线（后续任务）。**P1.6 不因本交付而宣告完整，G1 未过、G5 未过**；V4 仍是唯一生产基线。
- 追踪矩阵 `implementation-v1.json` 的 ENG-019 status 不由本任务改写（矩阵是已验收产物，改写属 caller/独立复核职权）。
- 交付方式：本地 commit，工作树 clean 后停止，等待 caller 独立复核；不 push、不触发 CI、不创建新 PR、不转派。

## 7. Caller 独立验收

2026-10-25，caller 在原工作树独立复核交付 `32caea769eb8d5b22600aadc66630daad65fa350`，确认只涉及约定三个文件，初始工作树 clean，`git diff 597eca1 HEAD --check` 通过。D-1-01/02/03 全部修复，无剩余功能缺陷；已调用 `approve_task(threadId="thread_20261024_231806_d84d")`，原线程归档为 COMPLETED，沿用原 callerSessionId。

全部测试均在本工作树真实重建执行，使用 `D:/Unity/dotnet/dotnet.exe`；没有改 caller 探针、既有断言、fixture、追踪矩阵或 verifier。

| 独立命令/验证 | exit | 结果 | 日志 |
|---|---|---|---|
| 原 `run-parity.ps1`（命令见 §4.2 R1） | 0 | 真实 V4 Kotlin 编译/执行成功，C# `V4 parity failures=0` | `parity.log` |
| 独立补充 console，`dotnet run --project %TEMP%/eng019-caller-r2-supplement/Probe.csproj -c Release` | 0 | 全 BMP 65,536 码点 × 构造器/WithCapability 双入口；与真实 Kotlin 输出的 28 个空白码点集合完全一致，0 差异 | `supplement.log` |
| `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Debug --nologo -v:minimal` | 0 | 94 / 0 / 0 | `ai-Debug.log` |
| 同上 `-c Release` | 0 | 94 / 0 / 0 | `ai-Release.log` |
| `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal` | 0 | 322 / 0 / 0 | `Domain.log` |
| 同命令替换为 `Mahjong.Contracts.Tests` | 0 | 104 / 0 / 0 | `Contracts.log` |
| 同命令替换为 `Mahjong.Client.Tests` | 0 | 132 / 0 / 0 | `Client.log` |
| 同命令替换为 `Mahjong.TableRuntime.Tests` | 0 | 313 / 0 / 0 | `TableRuntime.log` |
| `dotnet build dotnet/Mahjong.sln -c Release --no-restore --nologo -v:minimal` | 0 | 0 警告 / 0 错误 | `build.log` |

Release 非重复合计 **965 通过 / 0 失败 / 0 跳过**；Debug 94 项另列，不重复计入。原探针 SHA256 仍为 `CB0BFECD676EBBD868932911F058C8CCABF3C4008E9982E1E6C6F5B2E825DE7C`。

独立原始证据目录为本工作树 ignored `artifacts/eng019-caller-review-r2/`，含 `SHA256SUMS.txt`：

| 日志 | SHA256 |
|---|---|
| `parity.log` | `E309586FF28005A1E9EF134E7FBFECDEF7E152F0CE00AB18E115A729AD95D476` |
| `supplement.log` | `447D961547C3FA2A34392D5C5865B31C3A74425C774697BD4447F1FC41352DE7` |
| `ai-Debug.log` | `B1628E6BD0D8C999AE0A3ECDF68FE0386F4CDFC34EA761418536CA9F8D8BE415` |
| `ai-Release.log` | `CCF701AD3D4039B1FE8068B55D9D2BCE69BD5D39056D954A3842C1CD9BAB2871` |
| `Domain.log` | `03D06AAB750A4E44DEFA368DC2F5E39B733DDF10089D98B348C0100CF6D25DF6` |
| `Contracts.log` | `ABC6FBB51A60D97A490A7B7F699D295856193C2C5DEA17057DC27357F21C2DAF` |
| `Client.log` | `7F2643E5C4CE75D654C89779A0D3711E326BD65CAD38AFE44175015464E2911A` |
| `TableRuntime.log` | `9B6D029542E2453B92B64C6102090820360573AB506D8CA8A8769FFE5174E381` |
| `build.log` | `1A5EE0461E4E4AFD0A1F58487AC417D10C7AE514BDE5068DE64AF70B661E36FF` |

Caller 收口仅更正注释和报告：本机现代 .NET 8 的 `Math.Min(+0,-0)` 与反向同样得到负零，交付中泛称“.NET 平局返回左参”不准确；显式 `MinOf` 实现及全部测试断言保持不变。D-1-02/03 严重级别按原返修清单分别规范为 MINOR/MAJOR，原始 Bridge 交付报告保留。

唯一 MR 沿用 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`，目标 `v5`；将本任务的两个本地实现/返修提交和 caller 验收文档一起发布，保留远端既有已验收批次。Basic AI 仍在返修、黄金目录仍待审，不混入未经独立验收的改动。主工作树的未提交文档、Unity 环境/Resources/Burst 与本批无关，继续保留。

**PASS** 仅为上述配置组件和回归；真实 PG/WSS、Unity/IL2CPP、Android/iOS/真机、跨物理网络、V4 Gradle 全量及本次发布的远端 CI 均为 **NOT_RUN**。配置接线、实际千术 AI、P1.6 整体、G1/G5 不因此完成，V4 仍为唯一生产基线。
