# V5 P1.6 AI 纯候选值模型交付：AiDecisionModels 七类型（ENG-017）

日期：2026-10-25（第 1 轮交付）
任务：追踪矩阵 [implementation-v1.json](implementation-v1.json) 中 `ENG-017`（`ai/AiDecisionModels.kt` 纯候选值模型子集）的补齐实现，为 ENG-018（候选工厂/能力注册/实际接线）铺路。
范围声明：**本任务只移植 V4 `AiDecisionModels.kt` 第 21..202 行的七个纯值类型**——`AiPhase`、`AiRisk`、`AiEffect`、`AiVisibleParams`、`AiCandidate`、`AiDecisionStamp`、`AiCandidateLimits`。不重做已验收的 `AiCheatPolicy`/`AiObservation`/`AiDecision`/`CommandIntent`/Basic 策略；不实现能力注册、候选生成、收费提交或实际 actor 接线（ENG-018 范围）；不移植同文件后半部分的 `AiCheatView`/`AiSeatSnapshot`/`AiDecisionContext`/`AiDecisionStrategy`（依赖 `PlayerView`/`PlayerDecision` 投影，属后续任务）。不引入任何 `PlayerView`/`GameState`/authority 引用或隐藏投影，不创建 `AiSeatSnapshot`/`AiCheatView`/`AiDecisionContext`。**本交付不等于完整 P1.6、不宣告 G1/G5 通过、不是平台/端到端验收；V4 仍是唯一生产基线。**
基线：工作树 `D:/majiang-wt/ai-candidate-models`，分支 `wt/v5-ai-candidate-models`，起始 HEAD `78441944283ca111cc344f19d152514ed577574c`（已验收 PR19 历史头），起始工作区 clean。远端已合入的独立启发式任务（`3c2e7f5e735faeb1f73eb4699b7e9551105ee897`）本任务**不依赖、不拉取、不覆盖**本冻结基线。

---

## 1. 交付文件（expectedFiles，共三个）

| 文件 | 内容 | SHA-256 |
|---|---|---|
| `dotnet/src/Mahjong.Ai/AiCandidateModels.cs` | ENG-017 移植（7 类型 + `KotlinSemantics` 语义助手） | `5efc70f2a0521e9ac3985b6af32372aa19b383177413d8da70d18bd97088789b` |
| `dotnet/tests/Mahjong.Ai.Tests/AiCandidateModelsTests.cs` | 行为矩阵测试（71 项，探针逐例固化） | `54f3f64918d840fcb1a75bde7f9f0333886b3fbb5e5444809e266176dbb4e8bf` |
| `doc/v5-重构/V5-P16-AI-CANDIDATE-MODELS.md` | 本交付文档 | 提交后见 commit |

无其他生产/测试/文档文件被修改或删除。黄金目录 `tests/v5-golden/`（含 `ai-v1.json`）零触碰——兄弟黄金目录任务（thread_20261024_055105_a2cb 第 10 轮）在途，本任务不消费任何未接受目录；`Mahjong.Ai.Tests.csproj` 既有的 golden 拷贝基础设施未改动。不修改冻结 implementation-v1/catalog/fixture/原 exporter/统一 verifier/`Domain`/`Contracts`/actor/Unity。不修改 V4 engine 任何 tracked 文件。

## 2. V4 行为核实方法：真实源码差分探针（非注释移植）

**证据合法性**：探针驱动的对象是本机真实 V4 编译产物 `D:/majiang/engine/build/classes/kotlin/main`（Kotlin 2.4.10/JVM 17 构建）。先以 git 核实该产物对应的源码在两个参考点之间**字节一致**：

```
git diff --stat 78441944283ca111cc344f19d152514ed577574c 3c2e7f5e735faeb1f73eb4699b7e9551105ee897 \
  -- engine/src/main/kotlin/com/majiang/ai/AiDecisionModels.kt \
     engine/src/main/kotlin/com/majiang/ai/AiCheatPolicy.kt
# 输出为空 → 基线与远端 head 的两个文件字节一致
```

**探针实现**：独立 TEMP 目录 `…/Temp/claude/eng017-probe-wdCN/` 中的纯 ASCII Java 反射驱动 `Eng017Probe.java`（特殊字符一律以 `ch(0x…)` 码点构造，杜绝源码传输/编码歧义），只读访问 V4 编译类，不改任何 tracked 文件。JDK 为本机 `D:/android/jdk17/jdk-17.0.20.1+1`，stdlib 为 Gradle 缓存 `kotlin-stdlib-2.4.10.jar`。

```
# 编译（exit=0）
D:/android/jdk17/jdk-17.0.20.1+1/bin/javac.exe -encoding UTF-8 Eng017Probe.java
# 运行（exit=3，设计如此：170 项检查中 2 条为消息格式差、0 条行为差）
D:/android/jdk17/jdk-17.0.20.1+1/bin/java.exe -Dfile.encoding=UTF-8 \
  -cp "C:/Users/sncdc/.gradle/caches/modules-2/files-2.1/org.jetbrains.kotlin/kotlin-stdlib/2.4.10/8943c84ddc6d5cc00a10dbc3736c397066eeaab5/kotlin-stdlib-2.4.10.jar;D:/majiang/engine/build/classes/kotlin/main;." \
  Eng017Probe > eng017-probe.log 2>&1
# eng017-probe.log SHA-256 = 47fe64ae853a8c6ce75a8855c61cd2dc0d2507d1b300be668fa77c97d1c37c51
# SUMMARY checked=170 mismatch=2；两条 mismatch 均为探针期望字符串
# "UnsupportedOperationException" 与实际 "UnsupportedOperationException: null" 的消息格式差
# （UOE 无 message），不可变性行为本体完全正确（§6 日志全部 OK 行；对应 C# 测试
# ValuesViewCannotBeCastBackToAWritableDictionary 与
# LimitsEnabledIdsViewCannotBeCastBackToAWritableCollection）。
```

全量探针日志原样嵌入 §6。探针的每一行 OK 都对应 C# 测试中的一个或多个断言；两条消息格式差不产生断言差异。

**探针阻止的一次真实移植缺陷**：V4 `toIntOrNull` 的溢出判定依赖 stdlib 的 `limitBeforeMul` 预检算法。C# 第一稿采用"负累加后比较"的直觉移植，`"-2147483649"` 会在 unchecked 环绕后把累加值翻成正数骗过比较、返回错误结果——对照探针期望（V4 返回 null，`int(-2147483649 overflow) → null`）在自检中 caught，实现改为与 stdlib 同构的 `limitBeforeMul` 预检算法，并由测试 `IntHandlesIntegerExtremesAndOverflowByReturningNull` 固化。这正是"按实际 V4 行为而非注释移植"的价值案例。

## 3. 逐字段 V4 行为表（探针实证 → C# 实现）

异常映射惯例沿用已验收的 `AiCheatPolicy` 移植：Kotlin 固有参数 null 检查（NPE）→ `ArgumentNullException`；`require` 文本校验 → `ArgumentException`；`require` 数值区间 → `ArgumentOutOfRangeException`（仍是 `ArgumentException` 子类，兼容捕获）；校验顺序一律镜像 V4 init 块、首违先生效。V4 异常消息中的 `got: X` 文本按仓库惯例改为 ParamName 承载（异常文本非行为契约）。

### 3.1 `AiPhase`（V4 `:21-26`）

| V4 | 行为/证据 | C# 实现 |
|---|---|---|
| `enum class AiPhase { IDLE, SHUFFLE_PREPARATION, ORDINARY_ACTION, OFFER_CHOICE }` | 探针：序数 `0=IDLE 1=SHUFFLE_PREPARATION 2=ORDINARY_ACTION 3=OFFER_CHOICE` | `enum AiPhase { Idle=0, ShufflePreparation=1, OrdinaryAction=2, OfferChoice=3 }`：C# PascalCase 命名（与 Domain `RoundPhase` 惯例一致），数值显式钉死 V4 序数，XML 文档逐项给出 V4 常量名映射；`KotlinPhaseName` 提供 V4 名（诊断 `ToString` 用） |

### 3.2 `AiRisk`（V4 `:34-45`，data class）

| 字段/成员 | V4 行为 | 探针证据 | C# 实现 |
|---|---|---|---|
| `exposure: Double = 0.0` | `init` 校验 `!isNaN && in 0.0..1.0`（**闭区间**；NaN 因比较恒假必败；**-0.0 通过**，因 `-0.0 >= 0.0` 为真）；暴露在前、先校验 | `NaN/±Inf/nextUp(1.0)/-4.9E-324` 全拒（IAE，消息含字段名）；`1.0,1.0` 与 `-0.0,-0.0` 构造成功；(NaN,NaN) 首报 exposure | 同式校验，`ArgumentOutOfRangeException(ParamName=exposure)`；`-0.0` 接受且**存储位型保持负零**（测试 `LimitsAccept…` 同型断言于 AiRisk：`AiRiskAcceptsTheInclusiveBoundariesIncludingNegativeZero` 位级核对 `long.MinValue`） |
| `suspicion: Double = 0.0` | 同上，后校验 | (NaN,NaN) 时 exposure 先抛 | 同上（ParamName=suspicion；顺序测试 `AiRiskValidatesExposureBeforeSuspicionLikeTheV4InitBlock`） |
| `penalty: Double`（计算属性） | `exposure*0.5 + suspicion*0.5`，IEEE 语义 | `AiRisk(-0.0,-0.0).penalty` 原始位型 `0x8000000000000000`（负零）；`(0.5,0.25)→0.375`、`(1,0)→0.5`、`(0,1)→0.5` | 同式；IEEE 加法两侧与 JVM 一致：(-0)+(-0)=-0、(-0)+(+0)=+0（测试 `AiRiskPenaltyKeepsTheNegativeZeroSignBitsLikeTheJvm` 三向位型断言） |
| data class 等值 | `Double.equals` **位级**比较：**-0.0 ≠ 0.0** | `AiRisk(0.0,0.0).equals(AiRisk(-0.0,-0.0)) = false` | `BitConverter.DoubleToInt64Bits` 位比较（`AiRiskEqualityIsBitwiseSoNegativeZeroDiffersFromZero`） |
| `hashCode` | Kotlin 数据类 31 链 + `Double.hashCode`（位型异或高低半字），**数值确定** | 两份 `(0.0,0.0)` 哈希同为 `0` | Java 同构哈希（`JavaDoubleHash`），**C# 与 JVM 数值一致**（`AiRiskJavaParityHashIsDeterministicAndMatchesTheJvmValue` 断言 0）；仅诊断/排序一致性用，非权威摘要 |
| 默认值 | `AiRisk() = (0.0, 0.0)` | `AiEffect() defaults` 用例组同法 | 可选参数 `exposure=0.0, suspicion=0.0` |

### 3.3 `AiEffect`（V4 `:52-61`，data class）

| 字段/成员 | V4 行为 | 探针证据 | C# 实现 |
|---|---|---|---|
| `handValue/shantenImprovement/retainedValue: Int = 0` | **无任何校验** | `Int.MinValue/Int.MaxValue/-1` 全部构造成功 | 原样存储（`AiEffectIntegersAreUnvalidatedLikeV4`） |
| `note: String = ""` | `require(note.length <= 120)`；`length` 是 **UTF-16 code unit 数** | 120 chars 接受、121 拒；**60 个代理对（120 单元）接受、61 对（122 单元）拒绝** | `Note.Length <= 120`（`AiEffectNoteBoundaryIsOneHundredTwentyUtf16Units` 含代理对双向用例）；null note → ANE |
| data class 等值 | 全字段、大小写敏感 | note "n" vs "m"、"N" 分叉 | 逐字段 `Equals` + 运算符 + Java 同构 31 链哈希（等值→同哈希固化） |

### 3.4 `AiVisibleParams`（V4 `:68-107`，非 data class 但自定义 map 等值）

| 成员 | V4 行为 | 探针证据 | C# 实现 |
|---|---|---|---|
| 存储 | `Collections.unmodifiableMap(LinkedHashMap(raw))`：**防御复制 + 插入序可观察 + 不可变视图** | `of(b=2,a=1)` 与 `from(LinkedHashMap{b,a})` 迭代均 `[b,a]`；构造后改源 map 零影响；`values.put` 抛 UOE | 私有 `string[] keys + Dictionary(Ordinal)` 复刻确定性插入序；暴露 `Values` 为私有 `OrderedEntries : IReadOnlyDictionary`（非 `Dictionary`/`IDictionary`，无法转回可写表——测试 `ValuesViewCannotBeCastBackToAWritableDictionary` 以 `as` 为 null + 强转 `InvalidCastException` 双向固化） |
| `of(vararg Pair)` | 逐项 `require(key.isNotBlank())` 后 `map[key]=value`：**空白 key 拒绝；重复 key 后值胜出且保留首次插入位**；value 无校验 | `""`/`" "`/NBSP/`0x1C` key 全拒（IAE `parameter key cannot be blank`）；**`0x85` key 接受**（非 Kotlin 空白）；重复 key `of(k=1,a=x,k=2)` → size 1、`string(k)="2"` | `IsKotlinBlank` 精确复刻（`AiCheatPolicy` 同款差集：0x001C–0x001F 为 V4 空白而 .NET 不算、0x0085 反之、NBSP 两侧同为空白）；重复 key 保位换值（`OfDuplicateKeysKeepFirstPositionAndTakeLastValue`） |
| `from(map)` | **零校验**（空白 key 合法） | `from({""→…, NBSP→…})` 构造成功且可读回 | 不校验（`FromPerformsNoValidationLikeV4`） |
| `string(key)` | `values[key]`，缺失返回 null | `string(b)="2"`、`string(missing)=null` | `TryGetValue`；null key → ANE |
| `int(key)` | `values[key]?.toIntOrNull()`：**可选 ASCII `+`/`-` 符号 + 逐 UTF-16 单元十进制位**；**从不 trim**；`Character.digit(c,10)` 语义（接受 Unicode Nd 位值：全角４２、阿拉伯-印度٤₂ 均解析；全角＋/－ 是标点不是符号；代理对按单元判、孤立代理拒）；**溢出→null**（stdlib `limitBeforeMul` 预检）；空串/裸符号→null | 全矩阵 23 例逐条通过：`42→42`、`" 42"/"42 "/"\t42"/VT42VT→null`、`+42/-42`、`""`/`"+"`/"-"→null、`2147483647→MaxValue`、`2147483648→null`、`-2147483648→MinValue`、`-2147483649→null`、全角４２→42、٤٢→42、`42.0`/`0x10`/`1_000`→null、`＋42→null`、`-０→0`、`-0→0` | `KotlinSemantics.KotlinToIntOrNull` 与 stdlib 同构（符号判定用 `firstChar < '0'` 门——低于 `'0'` 且非符号即拒，天然覆盖空白/0x1C/0x85；`limitBeforeMul` 预检杜绝环绕）；位值经 `CharUnicodeInfo.GetDecimalDigitValue`（ASCII 快速路径）。四个测试矩阵固化全部探针例 |
| `intList(key)` | `split(',')` → `mapNotNull { it.trim().toIntOrNull() }` → 缺 key 空表：**逗号分割保留空元素；元素 Kotlin-trim；不可解析元素静默丢弃** | `"1, 2 ,x,3.5,+4,－5"→[1,2,4]`（全角负号元素被丢）；`"４２,٤٢, ７ "→[42,42,7]`；`""→[]`；`","→[]`；`"1,,2"→[1,2]`；`"99999999999,5"→[5]`（溢出只丢自身）；`"-2147483648, +2147483647"→[Min,Max]` | `Split(',')`（与 Kotlin split 一样保留空元素）+ `KotlinTrim` + `KotlinToIntOrNull` + 丢弃（`IntListSplitsTrimsParsesAndDropsUnparsableElements` 全矩阵） |
| `stringSet(key)` | `split(',')` → `map { it.trim() }` → `filter { isNotEmpty() }` → `LinkedHashSet` → 缺 key 空集：**trim 后去空、去重保首次出现序** | `"b, a ,b,,NBSPa"`：**NBSP 是 Kotlin 可 trim 空白** → 元素归并为 `"a"` → `[b,a]`；`"　,85x, x"`（U+3000 trim 成空被丢、**U+0085 不可 trim** 存活）→ `[85x, x]`；`"a,b,a"→[a,b]`；`""`/`","→[]`；`VT z VT→[z]`（VT 可 trim） | `KotlinTrim` + 首现序去重。V4 返回 `LinkedHashSet`；netstandard2.1 无 `IReadOnlySet`，故以**只读列表**暴露同一迭代序（元素保证唯一），成员判断走列表内容——有意适配、见 §4。全矩阵固化（`StringSetTrimsDropsEmptiesAndDedupesInFirstOccurrenceOrder`） |
| 等值/哈希 | 自定义：`values == other.values`（**map 内容等值、与迭代序无关、Ordinal 大小写敏感**）；`hashCode = values.hashCode()`（Java map 哈希：Σ(key^value)，序不敏感、**数值确定**）；`toString = "AiVisibleParams($values)"` | `(a=1,b= two)` 与 `(b= two,a=1)` 相等且**同哈希 1068702**；缺 key/值异/大小写分叉全 false；`EMPTY` 哈希 0、`toString="AiVisibleParams({})"`、内容形 `AiVisibleParams({a=1, b= two})` | 逐条复刻；**哈希数值与 JVM 一致**（`ParamsJavaParityHashMatchesTheJvmValues` 断言 1068702/0）；`ToString` Kotlin Map 形状 |

### 3.5 `AiCandidate`（V4 `:125-141`，data class）

| 字段/成员 | V4 行为 | 探针证据 | C# 实现 |
|---|---|---|---|
| `optionId` | `require(isNotBlank())` | `""`/`" "`/NBSP 拒 | `IsKotlinBlank` → `ArgumentException(ParamName=optionId)`；null → ANE |
| `capabilityId` | `require(isNotBlank())`，后于 optionId | `""` 拒；组合违例时 optionId 先抛 | 同上，顺序测试 `CandidateValidatesInTheV4InitOrder` |
| `phase` | 非空枚举，无校验 | 参与等值 | `AiPhase` 属性；枚举是值类型无 null 面 |
| `cost: Int` | `require(cost >= 0)` | `-1` 拒（IAE `cost must be non-negative`）；`Int.MaxValue` 接受 | `ArgumentOutOfRangeException(ParamName=cost)`；MaxValue 接受 |
| `risk` / `estimatedEffect` | 非空引用 | 参与等值（**位级**：risk -0.0 分叉两个其余相同的候选） | null → ANE；等值委托 `AiRisk`/`AiEffect` 位级 `Equals` |
| `parameters = AiVisibleParams.EMPTY` | 默认共享 EMPTY 实例 | 等值按 map 内容（与迭代序无关）；空串 value 原样保留 | 可选参数 `parameters = null` → 归一为 `AiVisibleParams.Empty`（**同一单例引用**，`Assert.Same` 固化）；V4 类型面无法传 null，归一即 V4 默认语义 |
| `terminalForWindow` / `isSafeDefault = false` | 布尔参与等值 | 各自分叉 | 原样 |
| data class 等值/哈希 | 九字段全等值；哈希 31 链含枚举槽（**JVM 枚举哈希是身份哈希、跨进程不稳定**） | 等值候选同哈希（同 JVM 内）；九字段逐一分叉矩阵通过 | `Equals` 九字段；`GetHashCode` Java 同构 31 链，**枚举槽以序数替代 JVM 身份哈希**（保 "等值→同哈希" 不变式并获得跨进程确定性；V4 该槽位本就不可跨进程复现，见 §4 有意适配 3）；数值断言仅限等值一致性，不宣称权威摘要 |

### 3.6 `AiDecisionStamp`（V4 `:150-171`，data class）

| 字段 | V4 行为 | 探针证据 | C# 实现 |
|---|---|---|---|
| `seat: Int` | `require(seat in 0..3)`，**首项校验** | `-1`/`4` 拒（消息含值） | `ArgumentOutOfRangeException(ParamName=seat)`，顺序首位（`StampValidatesInTheV4InitOrder`） |
| `sessionId: String` | `require(isNotBlank())`，第二项 | `""`/`" "`/NBSP/`0x1C`/TAB 拒；**`0x85` 接受** | `IsKotlinBlank` → `ArgumentException(ParamName=sessionId)`；NEL 接受用例固化 |
| `roundId/decisionId/offerId/windowId: String?` | **无任何校验**（非 null 时 `""` 合法） | 空串 roundId/decisionId/offerId/windowId 构造成功；全 null 身份字段构造成功 | 不校验（`StampLeavesNonSessionIdentityStringsUnvalidated` + `StampAcceptsAllNullIdentityFieldsLikeV4`）——**保留 V4 宽松面，不收紧** |
| `controlEpoch/stateVersion: Long` | `require(>= 0)`，第三/四项 | `-1` 拒；`0` 与 `Long.MaxValue` 接受；组合违例顺序 exposure→…逐级验证 | `ArgumentOutOfRangeException`，顺序测试覆盖（controlEpoch 先于 stateVersion 先于 deadline） |
| `deadlineMillis: Long?` | **仅非 null 时** `require(>= 0)`；IDLE stamp 携带 null | `-1` 拒；`null`/`0`/`Long.MaxValue` 接受 | `long?` 提升 `deadlineMillis < 0` 判断（null 自然跳过） |
| data class 等值/哈希 | 十字段全参与；**null 与有值恒分叉**（五个可空字段逐一验证）；大小写敏感 | 十字段翻转矩阵 + 3 个 null-vs-set 用例全过；等值→同哈希 | 逐字段 `Equals`（`==` 于 `string?`/`long?` 的 null 语义与 Kotlin 一致）；Java 同构哈希（null 字段哈希 0、枚举槽序数替代，理由同 §3.5） |

### 3.7 `AiCandidateLimits`（V4 `:176-202`，**非 data class**）

| 成员 | V4 行为 | 探针证据 | C# 实现 |
|---|---|---|---|
| 类语义 | **刻意不做 data class**：无 equals/hashCode 覆盖 → **身份相等** | 两个内容全同的实例 `equals=false`；`equals(self)=true` | 密封类、**零 Equals/GetHashCode/运算符覆盖**（`LimitsAreIdentityComparedLikeTheNonDataClassTheyPort`）；`ToString` 为 JVM 风格诊断身份头 + 标量（V4 无数据类 toString 可移植，明示诊断用途） |
| `enabledCapabilityIds` | 构造参数（非 val）→ `Collections.unmodifiableSet(LinkedHashSet(src))`：**防御复制 + 去重保首现序 + 不可变**；**属性初始化先于 init 块**（校验抛出时拷贝已发生） | 源 `[b,a,b]` → 存储迭代 `[b,a]`、size 2；构造后改源零影响；`add` 抛 UOE | 私有 `string[] keys + Dictionary(Ordinal)` 成员表；暴露只读列表视图（私有 `OrderedIds : IReadOnlyList`，不可转回可写集合——cast 双向断言）；构造器先拷贝后校验（镜像 Kotlin 执行序）。netstandard2.1 无 `IReadOnlySet` 的适配见 §4 |
| `maxSpend/remainingPoints/reservePoints: Int` | 各自 `require(>= 0)`，顺序 maxSpend→remaining→reserve | `-1` 逐一拒；组合违例首违先生效 | 同序 `ArgumentOutOfRangeException`（ParamName 逐一核对，`LimitsValidateInTheV4InitOrder`） |
| `candidateLimit: Int` | `require(in 1..64)` | `1`/`64` 接受；`0`/`65` 拒 | 同式 |
| `riskTolerance: Double` | `require(!isNaN && in 0.0..1.0)`：**-0.0 接受**（IEEE `-0.0 >= 0.0` 为真）且原样存储 | `-0.0`/`0.0`/`1.0` 接受；`NaN`/`±Inf`/`-0.1`/`nextUp(1.0)` 拒 | 同式；**存储位级保持负零**（`LimitsAcceptTheInclusiveRiskToleranceBoundariesIncludingNegativeZero` 位型断言 `long.MinValue`） |
| `spendablePoints` | `maxOf(0, minOf(maxSpend, remainingPoints - reservePoints))`：三输入均已校验非负 → `remaining-reserve ∈ [-Max, Max]` **无溢出面** | `(5,3,2)→1`、`(5,2,5)→0`、`(0,0,0)→0`、`(Max,Max,0)→Max`、`(5,Max,Max)→0`、`(10,7,0)→7` | `Math.Max(0, Math.Min(MaxSpend, RemainingPoints - ReservePoints))`，六例矩阵固化 |
| `allows(id)` | `id in enabledCapabilityIds`：LinkedHashSet contains → **Ordinal、大小写敏感**；参数非空型（固有 null 检查） | `allows(b)=true`、`allows(z)=false`；空集 allow nothing | `Dictionary.ContainsKey(Ordinal)`；null → ANE；`AllowsUsesOrdinalCaseSensitiveMembership` 含大小写分叉 |
| `canSpend(cost)` | `cost >= 0 && cost <= spendablePoints` | spendable 0 时 `canSpend(0)=true`、`(1)=false`、`(-1)=false`；spendable 1 时 `(1)=true`、`(2)=false` | 同式，五例固化 |

### 3.8 与 V4 行为一致但值得点名的"宽松面"

- **stamp 非会话身份串不校验**（`""` 合法）——保留，不收紧；这些字段在 V4 生产路径中由 `TableAuthority.buildAiSeatSnapshot`（`:2455-2539`）派生（SHUFFLE_PREPARATION 的 `decisionId="preparation-$roundId-$seat"`、IDLE 的 deadline 为 null、`stateVersion = round?.version ?: 0` 等），本移植只承载值、不复制该装配逻辑。
- **`From` 接受空白 key**——保留；V4 生产调用全部走 `of`/字面量 key。
- **`intList` 静默丢弃不可解析元素**——保留；调用方（ENG-018 能力适配器）负责编码责任，与 V4 相同。

## 4. 相对 V4 的安全适配与有意收紧（全部不改变合法输入行为）

1. **结构性防御复制**：V4 的 `unmodifiableMap/unmodifiableSet` 依赖调用方自律（V4 `TableAuthority` 曾需手工深拷贝配合）；V5 构造器一次性复制，暴露视图非 `Dictionary`/`IList`/数组，无法转回可写集合（两个 cast 测试双向固化）。`AiCandidate` 持有的 `AiRisk`/`AiEffect`/`AiVisibleParams` 本身不可变，无复制面。全部类型零可变状态、零时钟/随机/IO/反射面——跨线程冻结读取安全。
2. **哈希数值策略**：字符串/整数/双精度槽用 Java 同构哈希（**跨进程确定**，且在 V4 数值确定处与 JVM 逐值一致：`AiRisk(0,0)=0`、`params {a=1,b=" two"}=1068702`、`EMPTY=0`）；**枚举槽以序数替代**——JVM 枚举哈希是进程随机身份哈希，V4 的 stamp/candidate 哈希值本就不可跨 JVM 进程复现，序数替代在保住 "等值→同哈希" 不变式的同时获得跨进程确定性。所有 `GetHashCode` 仅为进程内哈希表与诊断一致性服务，**绝不作为权威摘要**——权威身份仍是 `CommandIntent` SHA-256（V4 `AiRequestId` 亦对原始串做 SHA-256，属 ENG-018 接线，本任务不实现）。
3. **`IReadOnlySet` 缺位适配**：netstandard2.1 无 `IReadOnlySet<T>`。`StringSet`/`EnabledCapabilityIds` 以只读列表暴露（迭代序 = V4 `LinkedHashSet` 首现序，元素保证唯一），成员判断分别走列表内容与 `Allows`（V4 生产消费点 `CheatAiRegistry.candidates`/调度器也只经 `limits.allows/canSpend`，无直接集合成员 API 依赖）。
4. **null value 拒绝**：`Of`/`From` 对 null key/value 抛 `ArgumentNullException`。V4 类型面（`Pair<String,String>`/`Map<String,String>` 非空型参数）编译期就产不出 null 值，只有未类型化 JVM 路径可注入；该收紧不改变任何合法输入行为，且 null value 若真能入 map 会使 `string()` 返回 null 而污染 C# 非 null 返回面。
5. **诊断 `ToString` 的 double 格式**：.NET invariant 最短往返（`0` 而非 JVM 的 `0.0`）——纯诊断、非行为契约，测试按 .NET 形状断言并注明。`AiVisibleParams.ToString` 内容为纯字符串，与 JVM 输出逐字节一致（探针核对）。
6. **异常消息文本**：改为 ParamName 承载，非逐字移植（同 `AiCheatPolicy` 惯例）。

## 5. 测试矩阵与运行证据

### 5.1 新增测试（`AiCandidateModelsTests`，71 项，全为行为断言、非属性回读镜像）

- **AiPhase**：序数/名称映射（探针序数逐项断言）。
- **AiRisk**：默认值；penalty 混合值矩阵；**负零符号位**三向位型断言；NaN/±Inf/nextUp/-ε 拒绝；闭区间边界含 -0.0（存储位级）；exposure→suspicion 顺序；**位级等值**（-0.0≠0.0、逐字段分叉、null/异型）；Java 同构哈希（数值 0 对齐 JVM + 等值一致性）。
- **AiEffect**：默认值；**UTF-16 单元边界**（120/121 chars、60/61 代理对）；整数无校验（MinValue/MaxValue/-1）；null note ANE；全字段等值大小写敏感；运算符。
- **AiVisibleParams**：`of`/`from` 插入序；重复 key 保位换值；EMPTY 语义（内容/等值/哈希 0/toString）；`from` 零校验（空白 key 合法）；`of` Kotlin 空白 key 矩阵（含 0x1C 拒、0x85 收）；null key/value ANE；防御复制；视图 cast 双向防护；`string` 缺失/null；**`int` 23 例探针矩阵**（含 `-2147483649` 环绕回归用例、全角/阿拉伯-印度位值、从不 trim）；**`intList` 7 例**（静默丢弃、溢出只丢自身、int 极值）；**`stringSet` 6 例**（NBSP/VT 可 trim、0x85 不可 trim、去重保首现序）；map 内容等值序不敏感/大小写敏感/null 运算符；**JVM 数值哈希对齐（1068702/0）**；Kotlin Map 形状 toString（含 `AiVisibleParams({})`）。
- **AiCandidate**：默认参数归一 EMPTY（`Assert.Same`）；空白 id 矩阵（0x1C 拒、0x85 收）；cost -1/MaxValue；init 顺序（optionId→capabilityId→cost）；四 null 组件 ANE（risk/effect 直通构造器）；**九字段等值矩阵**（含 risk -0.0 位级分叉）；参数 map 序不敏感等值 + 哈希一致；运算符。
- **AiDecisionStamp**：全 null 身份字段合法；非会话身份串零校验（`""` 合法）；seat 0..3 边界与越界；sessionId Kotlin 空白矩阵（0x1C 拒、0x85 收）；epoch/version/deadline 非负 + `Long.MaxValue` + deadline null/0；五段 init 顺序；**十字段等值矩阵含 3 个 null-vs-set**；大小写敏感 + 哈希一致；运算符。
- **AiCandidateLimits**：id 去重保首现序；防御复制；视图 cast 防护；`allows` Ordinal 大小写 + null ANE；空集 allow nothing；**spendable 六例矩阵含 int 极值**；**canSpend 五例**；**身份相等语义**（内容同 ≠ 相等）；riskTolerance 边界含 -0.0 存储位级 + NaN/±Inf/越界拒绝；candidateLimit 1..64；三预算非负；**五段 init 顺序**；null id 源 ANE；诊断 toString 形状。
- **跨模型**：候选哈希跨等值实例一致（并含参数 map 序不敏感一致性）——注释明示非权威摘要、权威身份归 `CommandIntent` SHA-256。

### 5.2 运行命令、退出码与计数（全部真实执行，dotnet 8.0.426 @ `D:/Unity/dotnet/dotnet.exe`，cwd = `dotnet/`）

| # | 命令 | exit | 结果 |
|---|---|---|---|
| 1 | `dotnet build Mahjong.sln -c Release --no-incremental --nologo` | 0 | **0 警告 / 0 错误**（日志 SHA-256 `594a722fad4ba9c5bf6a3c304e1531fc41d8c044cc2094f64d9e7a2bdf80f376`） |
| 2 | `dotnet test tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-build --nologo` | 0 | **243 通过 / 0 失败 / 0 跳过**（既有基线 172 + 本任务新增 71；日志 SHA `f6c2554559447ce61d33871d941c7958ed5baa32e61fdc7ccf06a27426fc57b9`） |
| 3 | 同上加 `-- --filter-class "Mahjong.Ai.Tests.AiCandidateModelsTests"`（MTP） | 0 | **71 通过 / 0 失败 / 0 跳过**（新增类全量单独核数） |
| 4 | `dotnet test tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build --nologo` | 0 | **322 通过 / 0 失败**（兼容无回归；日志 SHA `95e915f32054e30c61bf28c57d8d60f529801f41ee58cfb3edd599f6e9bd2252`） |
| 5 | `dotnet test tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo` | 0 | **104 通过 / 0 失败**（日志 SHA `e75b260fe506b1049b00093d6495f9952664d7eb6aae4fc0c2468f6130280fc1`） |
| 6 | `dotnet test tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-build --nologo` | 0 | **132 通过 / 0 失败**（日志 SHA `cca67a319a6bf8167df7b96e34c0eb66a5cd0341df1932fd15ce1286e580dbca`） |
| 7 | `git diff --check`（工作树根） | 0 | 无空白错误 |

探针运行证据：`javac` exit=0；`java` exit=3（**设计值**——探针把消息格式差也计入 mismatch，两条均为 `UnsupportedOperationException` 无 message 的字符串格式差，行为面 0 差异；`SUMMARY checked=170 mismatch=2`）；探针日志 SHA-256 `47fe64ae853a8c6ce75a8855c61cd2dc0d2507d1b300be668fa77c97d1c37c51`，全文见 §6。

**已知缺陷一次性全列**（无隐藏）：
1. 探针有 2 条消息格式差（UOE 无 message），已在上文说明，非实现缺陷；
2. `AiCandidateLimits.ToString` 是**新造**的诊断格式（V4 无数据类 toString 可移植），明示不承载语义；
3. `StringSet`/`EnabledCapabilityIds` 因 netstandard2.1 缺 `IReadOnlySet` 而以只读列表暴露（§4.3），ENG-018 若升级 TFM 可无损换型；
4. 枚举槽哈希为序数替代（§4.2），V4 原值跨进程本就不可复现，无行为回归面。

以上 4 条全部不改变合法输入行为。

## 6. 探针原始日志（eng017-probe.log，SHA-256 `47fe64ae853a8c6ce75a8855c61cd2dc0d2507d1b300be668fa77c97d1c37c51`）

```text
== AiPhase ==
== AiRisk ==
PROBE    AiRisk(-0.0,-0.0).penalty | actual="value=-0.0 rawBits=0x8000000000000000"
OK       AiRisk(0.0,0.0).equals(AiRisk(-0.0,-0.0)) | expected=false | actual=false
PROBE    hashCode 0-vs-neg0 | actual="h(0,0)=0 h(-0,-0)=0"
OK       AiRisk(0.5,0.25).penalty | expected=0.375 | actual=0.375
OK       AiRisk(1.0,0.0).penalty | expected=0.5 | actual=0.5
OK       AiRisk(0.0,1.0).penalty | expected=0.5 | actual=0.5
OK       AiRisk(NaN,0) | expected=IllegalArgumentException | actual=IllegalArgumentException: exposure must be in 0.0..1.0, got: NaN
OK       AiRisk(0,NaN) | expected=IllegalArgumentException | actual=IllegalArgumentException: suspicion must be in 0.0..1.0, got: NaN
OK       AiRisk(+Inf,0) | expected=IllegalArgumentException | actual=IllegalArgumentException: exposure must be in 0.0..1.0, got: Infinity
OK       AiRisk(0,+Inf) | expected=IllegalArgumentException | actual=IllegalArgumentException: suspicion must be in 0.0..1.0, got: Infinity
OK       AiRisk(-Inf,0) | expected=IllegalArgumentException | actual=IllegalArgumentException: exposure must be in 0.0..1.0, got: -Infinity
OK       AiRisk(nextUp(1.0),0) | expected=IllegalArgumentException | actual=IllegalArgumentException: exposure must be in 0.0..1.0, got: 1.0000000000000002
OK       AiRisk(-minValue,0) | expected=IllegalArgumentException | actual=IllegalArgumentException: exposure must be in 0.0..1.0, got: -4.9E-324
OK       AiRisk(1.0,1.0) | constructed
OK       AiRisk equal copies equal | expected=true | actual=true
OK       AiRisk equal copies same hashCode | expected=0 | actual=0
== AiEffect ==
OK       AiEffect() defaults | expected="hv=0 si=0 rv=0 note=""" | actual="hv=0 si=0 rv=0 note="""
OK       note length 120 accepted | constructed
OK       note length 121 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: effect note must be <= 120 chars
OK       note 60 surrogate pairs (120 UTF-16 units) accepted | constructed
OK       note 61 surrogate pairs (122 UTF-16 units) rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: effect note must be <= 120 chars
OK       AiEffect(int.MinValue, int.MaxValue, -1, note) accepted (no int validation) | constructed
OK       AiEffect equals by all fields | expected=true | actual=true
OK       AiEffect note distinguishes | expected=false | actual=false
== AiVisibleParams ==
PROBE    of(b=2,a=1) values iteration order | actual=["b", "a"]
PROBE    from(LinkedHashMap b=2,a=1) order | actual=["b", "a"]
OK       string(b) | expected="2" | actual="2"
OK       string(missing) | expected=null | actual=null
OK       from(emptyMap).equals(EMPTY) | expected=true | actual=true
OK       EMPTY hashCode equals from(emptyMap) hashCode | expected=0 | actual=0
OK       EMPTY values empty | expected=true | actual=true
PROBE    EMPTY toString | actual="AiVisibleParams({})"
OK       defensive copy: source mutation invisible | expected=false | actual=false
MISMATCH values map unmodifiable | expected="UnsupportedOperationException" | actual="UnsupportedOperationException: null"
OK       of duplicate key: last wins | expected="2" | actual="2"
OK       of duplicate key: single entry | expected=1 | actual=1
OK       of blank key rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: parameter key cannot be blank
OK       of space key rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: parameter key cannot be blank
OK       of NBSP-only key rejected (Kotlin isBlank) | expected=IllegalArgumentException | actual=IllegalArgumentException: parameter key cannot be blank
OK       of FS(0x1C)-only key rejected (Kotlin ws) | expected=IllegalArgumentException | actual=IllegalArgumentException: parameter key cannot be blank
OK       of NEL(0085)-only key accepted (not Kotlin ws) | constructed
OK       int(42) | expected=42 | actual=42
OK       int(space42) | expected=null | actual=null
OK       int(42space) | expected=null | actual=null
OK       int(+42) | expected=42 | actual=42
OK       int(-42) | expected=-42 | actual=-42
OK       int(empty) | expected=null | actual=null
OK       int(plus only) | expected=null | actual=null
OK       int(minus only) | expected=null | actual=null
OK       int(2147483647) | expected=2147483647 | actual=2147483647
OK       int(2147483648 overflow) | expected=null | actual=null
OK       int(-2147483648) | expected=-2147483648 | actual=-2147483648
OK       int(-2147483649 overflow) | expected=null | actual=null
OK       int(fullwidth 42) | expected=42 | actual=42
OK       int(arabic-indic 42) | expected=42 | actual=42
OK       int(42.0) | expected=null | actual=null
OK       int(0x10) | expected=null | actual=null
OK       int(1_000) | expected=null | actual=null
OK       int(TAB42) | expected=null | actual=null
OK       int(fullwidth-plus 42) | expected=null | actual=null
OK       int(-fullwidth-zero) | expected=0 | actual=0
OK       int(-0) | expected=0 | actual=0
OK       int(VT 42 VT) no trim in int() | expected=null | actual=null
OK       int(single arabic 4) | expected=4 | actual=4
OK       intList('1, 2 ,x,3.5,+4,FWminus5') | expected=[1, 2, 4] | actual=[1, 2, 4]
OK       intList(FW42,AR42,space FW7 space) | expected=[42, 42, 7] | actual=[42, 42, 7]
OK       intList(empty) | expected=[] | actual=[]
OK       intList(comma) | expected=[] | actual=[]
OK       intList('1,,2') | expected=[1, 2] | actual=[1, 2]
OK       intList('99999999999,5') | expected=[5] | actual=[5]
OK       intList(int extremes signed) | expected=[-2147483648, 2147483647] | actual=[-2147483648, 2147483647]
OK       stringSet('b, a ,b,,NBSPa') order | expected=["b", "a"] | actual=["b", "a"]
OK       stringSet(IDEO,NELx,x) order | expected=["\u0085x", "x"] | actual=["\u0085x", "x"]
OK       stringSet('a,b,a') size | expected=2 | actual=2
OK       stringSet('a,b,a') first | expected="a" | actual="a"
OK       stringSet(empty) | expected=[] | actual=[]
OK       stringSet(comma) | expected=[] | actual=[]
OK       stringSet(VT z VT) | expected=["z"] | actual=["z"]
PROBE    toString | actual="AiVisibleParams({a=1, b= two})"
OK       map-content equality is order independent | expected=true | actual=true
OK       order-independent instances same hashCode | expected=1068702 | actual=1068702
OK       different values not equal | expected=false | actual=false
OK       missing key not equal | expected=false | actual=false
OK       empty equals empty | expected=true | actual=true
OK       case-sensitive keys | expected=false | actual=false
== AiDecisionStamp ==
PROBE    AiPhase ordinals | actual="0=IDLE 1=SHUFFLE_PREPARATION 2=ORDINARY_ACTION 3=OFFER_CHOICE"
OK       equals copy | expected=true | actual=true
OK       hashCode equal for equal stamps | expected=-2083009822 | actual=-2083009822
OK       seat differs | expected=false | actual=false
OK       sessionId differs | expected=false | actual=false
OK       roundId null vs set | expected=false | actual=false
OK       decisionId differs | expected=false | actual=false
OK       controlEpoch differs | expected=false | actual=false
OK       phase differs | expected=false | actual=false
OK       stateVersion differs | expected=false | actual=false
OK       offerId differs | expected=false | actual=false
OK       windowId differs | expected=false | actual=false
OK       deadline differs | expected=false | actual=false
OK       window null vs set | expected=false | actual=false
OK       offer null vs set | expected=false | actual=false
OK       deadline null vs set | expected=false | actual=false
OK       blank roundId accepted | constructed
OK       all-null identity fields accepted | constructed
OK       seat 4 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: seat must be in 0..3, got: 4
OK       seat -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: seat must be in 0..3, got: -1
OK       blank sessionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: sessionId cannot be blank
OK       space sessionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: sessionId cannot be blank
OK       NBSP sessionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: sessionId cannot be blank
OK       FS(0x1C) sessionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: sessionId cannot be blank
OK       NEL(0085) sessionId accepted (not Kotlin ws) | constructed
OK       controlEpoch -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: controlEpoch must be non-negative: -1
OK       stateVersion -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: stateVersion must be non-negative: -1
OK       deadline -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: deadlineMillis must be non-negative: -1
OK       deadline 0 accepted | constructed
OK       Long.MAX values accepted | constructed
== AiCandidate ==
OK       equal copies equal | expected=true | actual=true
OK       optionId differs | expected=false | actual=false
OK       capabilityId differs | expected=false | actual=false
OK       phase differs | expected=false | actual=false
OK       cost differs | expected=false | actual=false
OK       risk -0.0 vs 0.0 distinguishes | expected=false | actual=false
OK       note differs | expected=false | actual=false
OK       terminal differs | expected=false | actual=false
OK       safeDefault differs | expected=false | actual=false
OK       params map-order irrelevant for equality | expected=true | actual=true
OK       equal candidates hashCode equal | expected=1172113711 | actual=1172113711
OK       params content differs | expected=false | actual=false
OK       blank optionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: optionId cannot be blank
OK       whitespace optionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: optionId cannot be blank
OK       NBSP optionId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: optionId cannot be blank
OK       blank capabilityId rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: capabilityId cannot be blank
OK       cost -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: cost must be non-negative, got: -1
OK       cost int.MaxValue accepted | constructed
OK       empty-string param value kept | expected="" | actual=""
== AiCandidateLimits ==
PROBE    enabled ids iteration order (b,a,b) | actual=["b", "a"]
OK       dedup size | expected=2 | actual=2
OK       allows(b) | expected=true | actual=true
OK       allows(z) | expected=false | actual=false
OK       spendable(5,3,2)=1 | expected=1 | actual=1
OK       spendable(5,2,5)=0 | expected=0 | actual=0
OK       spendable(0,0,0)=0 | expected=0 | actual=0
OK       spendable(MAX,MAX,0)=MAX | expected=2147483647 | actual=2147483647
OK       spendable(5,MAX,MAX)=0 | expected=0 | actual=0
OK       spendable(10,7,0)=7 | expected=7 | actual=7
OK       canSpend(0) true when spendable 0 | expected=true | actual=true
OK       canSpend(-1) false | expected=false | actual=false
OK       canSpend(1) false when spendable 0 | expected=false | actual=false
OK       canSpend(1) true when spendable 1 | expected=true | actual=true
OK       canSpend(2) false when spendable 1 | expected=false | actual=false
OK       defensive copy of enabled ids | expected=false | actual=false
MISMATCH enabled ids unmodifiable | expected="UnsupportedOperationException" | actual="UnsupportedOperationException: null"
OK       V4 AiCandidateLimits is identity-equal (not a data class) | expected=false | actual=false
OK       identity equals self | expected=true | actual=true
OK       riskTolerance -0.0 accepted | constructed
OK       riskTolerance 0.0 accepted | constructed
OK       riskTolerance 1.0 accepted | constructed
OK       riskTolerance NaN rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: riskTolerance must be in 0.0..1.0, got: NaN
OK       riskTolerance +Inf rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: riskTolerance must be in 0.0..1.0, got: Infinity
OK       riskTolerance -Inf rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: riskTolerance must be in 0.0..1.0, got: -Infinity
OK       riskTolerance -0.1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: riskTolerance must be in 0.0..1.0, got: -0.1
OK       riskTolerance nextUp(1.0) rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: riskTolerance must be in 0.0..1.0, got: 1.0000000000000002
OK       candidateLimit 1 accepted | constructed
OK       candidateLimit 64 accepted | constructed
OK       candidateLimit 0 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: candidateLimit must be in 1..64, got: 0
OK       candidateLimit 65 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: candidateLimit must be in 1..64, got: 65
OK       maxSpend -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: maxSpend must be non-negative, got: -1
OK       remaining -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: remainingPoints must be non-negative, got: -1
OK       reserve -1 rejected | expected=IllegalArgumentException | actual=IllegalArgumentException: reservePoints must be non-negative, got: -1
OK       empty ids allow nothing | expected=false | actual=false
OK       empty limits spendable 0 | expected=0 | actual=0
SUMMARY checked=170 mismatch=2
PROBE MISMATCH
```

## 7. NOT_RUN 清单（本任务明确未执行项）

| 项 | 原因 |
|---|---|
| TableRuntime / LobbyService / Postgres / WSS / Outbox 相关测试与真机链路 | 非 ENG-017 文件面；`Mahjong.Ai` 不依赖这些模块，本交付无任何接线变更 |
| Unity / IL2CPP / Android 设备端验证 | 无 Unity/设备改动；V5 Unity 壳未引用本新文件 |
| 远端 CI | receiver 不 push、不触发 CI（按任务约束，由 caller 统一走 PR19） |
| 黄金目录（`tests/v5-golden/`）与统一 verifier | 兄弟任务第 10 轮在途，本任务不消费未接受目录、不改 fixture/verifier；`ai-v1.json` 基线零触碰 |
| ENG-018 候选工厂/注册表/快照投影/调度接线/`AiRequestId` | 明确不在本任务（见头部范围声明） |
| 独立 V4 JVM 进程的随机化核对（enum 身份哈希跨进程值） | 无意义：JVM 身份哈希本进程随机，C# 侧已用序数替代并文档化（§4.2） |

## 8. 边界声明（防验收口径漂移）

- 本交付是 `implementation-v1.json` 中 `ENG-017` 的**纯候选值模型子集**补齐；ENG-017 更广的投影面（`AiCheatView`/`AiSeatSnapshot`/`AiDecisionContext`/`AiDecisionStrategy`，依赖 `PlayerView`/`PlayerDecision`）与 ENG-018（`AiCandidateFactory`/`AiCandidateScorer`/`CheatAiRegistry`/`AiCapabilityDescriptor`/`AiRequestId`/实际 actor 接线）**均未完成**，不在本交付宣称范围。
- 本交付**不等于**完整 P1.6，**不宣告** G1/G5 通过，**不是**平台/端到端验收；V4 仍是唯一生产架构。
- 不修改已验收的 `AiCheatPolicy.cs`/`AiObservation.cs`/`AiDecision.cs`/`CommandIntent.cs`/Basic 策略；`IAiStrategy`/`AiObservation`/`Domain`/`Contracts` 零改动（git status 仅三个 expectedFiles）。
- 本任务不依赖、不消费兄弟黄金目录任务（thread_20261024_055105_a2cb）与独立启发式任务（远端 `3c2e7f5`）的任何在途产物；三任务文件面互不相交。
