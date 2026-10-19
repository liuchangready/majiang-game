# D-07 算分格式化边界：总体交付

日期：2026-10-19（2026-10-19 追加 v5 线路落地）
基线 HEAD：`070424486445c9d4cfeaf15132d1ef837c96c8`（首次交付）；v5 实施基于其后工作区，未提交、未推送
状态：**v5 结构化算分线路已实施并完成 engine → server mapper → protocol → client mapper/view → presentation 全链迁移；v4 业务版本撤回，双端 HELLO/WELCOME 明确拒绝。**

> 阅读顺序：§1–§10 记录首次交付时的职责拆分与 v4 阻断证据（历史事实，保留可追溯性）；
> §11 列出当时仍待独立立项的事项；**§12 记录本次 v5 升版的实际实施、兼容/回退矩阵与测试证据**。
> 当前代码以 §12 为准。

本交付对应 `doc/v4-重构/IMPROVEMENT-PLAN.md` §9.2 D-07，并按 §9.3 第 4 条处理“D-07 的线路字段迁移单独立项”：当前版本内能安全完成的职责拆分、稳定码目录、结构化支付与回归测试已落地；engine → protocol → client → presentation 的新增线路字段没有进入当前 MJ-TCP/1，而是给出了可直接执行的 v5 字段契约、迁移/回退/切换规则及旧端严格解析的阻断证据。

---

## 1. 结论摘要

### 1.1 已完成

1. **纯格式化组件**：`ScoreTextFormatter` 是无状态 `object`，统一负责 `title`、`capReason`、`formulaText`、`yakuLines`、`paymentLines` 的既有文案。
2. **职责收口**：
   - `ScoreCalculator` 不再手写标题/封顶/公式文本，只保留符、番、基本点、封顶判定、精确原始基本点、支付函数等数值逻辑，并委托 Formatter 生成文本。
   - `WinningScoreEvaluator` 的役种行和支付行委托 Formatter，不再经由 `SettlementService` 取文案。
   - `SettlementService.formatPaymentLines` 保留原签名与逐字输出，只委托同一 Formatter。
3. **数值不变**：普通分、满贯/跳满/倍满/三倍满/累计役满、役满 1/2/N 倍、本场棒、立直棒、庄/闲自摸、庄/闲荣和的支付与总得点均以既有支付函数为准。Formatter 的支付行改为读取新的 `ScoreResult.paymentPlan` 数值结构，金额仍由原四个支付函数计算。
4. **稳定码目录**：
   - `Yaku.code`：42 个役种的稳定大写 ASCII 码，独立于 Kotlin 枚举名与日文展示名。
   - `ScoreCap.code` + `ScoreTextFormatter.YAKUMAN_CODE`：`MANGAN`、`HANEMAN`、`BAIMAN`、`SANBAIMAN`、`KAZOE_YAKUMAN`、`YAKUMAN`。
   - `ScoreResult.capCode`：未封顶为 `null`，役满为 `YAKUMAN`，其余为对应封顶码。
   - `ScoreBreakdown.yakuEntries`：稳定码 + 既有展示名 + 番数，且与 `yakuLines` 做构造期一致性校验。
5. **结构化支付**：`ScoreResult.paymentPlan` 返回 `PaymentKind`、`PayerRole`、`PaymentComponent(amount,count)`、`PaymentPlan(total)`；`ScoreBreakdown.paymentPlan` 由 `score/isDealer/isTsumo` 派生。`totalPoints` 与 `paymentPlan.total` 使用同一结构，避免两套汇总规则漂移。
6. **兼容与隐私**：既有字符串字段、数值字段、`uraEligible` 里宝牌可见性、观战公开投影均未改变；`ScoreBreakdown` 快照深拷贝包含新增结构化役种行，公开投影的里宝牌清洗逻辑不变。
7. **线路保护**：没有向 `ProtocolScoreResult`、`ProtocolScoreBreakdown`、`ProtocolAccessCapabilities`、`ApiResponse`、`TcpHello` 增加任何字段；v4 序列化字段集合由 server 测试锁定。
8. **跨模块稳定码守卫**：protocol 新增 `ProtocolScoreCodes` 冻结码表；server 测试逐项比对 engine `Yaku.code`、`ScoreCap.code`、`PaymentKind.code`、`PayerRole.code`。
9. **强制重编译稳定**：`org.gradle.parallel=false`；`KotlinCompile`/`Jar`/`Test` 禁用 build cache。修复后 `clean test` 与完整 `--rerun-tasks` 均全绿，不再依赖 `UP-TO-DATE`。

### 1.2 v5 线路迁移（原“受独立协议版本升级约束”，现已在 §12 实施）

- `ProtocolScoreResult.capCode`
- `ProtocolScoreBreakdown.yakuEntries`
- `ProtocolScoreBreakdown.paymentPlan`
- `ProtocolAccessCapabilities.scoreDetailVersion`
- 稳定码/支付结构进入 client 视图与 presentation 的全链字段

首次交付时这些字段被旧端严格未知字段解析与 `businessVersion == 1` 硬相等阻断（见 §6，历史）。本次按 §7 方案完成独立协议升版：`ApiProtocol.PROTOCOL_VERSION = 5`、`TcpWire.BUSINESS_VERSION = 2`，双端拒绝 v4，详见 §12。

### 1.3 明确未做

- 开发代理交付时未提交（commit）、未推送（push）。
- 未修改 Gradle 模块依赖，七模块单向依赖保持原样。
- 未改动 136 张物理牌守恒、洗牌/码牌、三大绝技经济与判定、观战私有/公开流隔离。
- 未新增 UI 文案或改变任何既有显示字符串；presentation 的 `SettlementDetailPresenter` 未改。
- 未把 engine 稳定码“猜”进 client：客户端没有码字段时继续逐字显示旧字符串，不凭展示名反推线路码。

---

## 2. 修改清单

### 2.1 生产代码

| 文件 | 变更 |
|---|---|
| `engine/src/main/kotlin/com/majiang/rules/ScoreTextFormatter.kt` | 纯 Formatter；`ScoreCap.code`；`ScoreText.capCode`；`yakuEntries`、`yakuEntriesFromLegacy`；支付行改为读取 `paymentPlan`；保留既有文案逐字输出 |
| `engine/src/main/kotlin/com/majiang/rules/ScoreCodes.kt` | 新增 `YakuEntry`、`PaymentKind`、`PayerRole`、`PaymentComponent`、`PaymentPlan` 及不变量 |
| `engine/src/main/kotlin/com/majiang/rules/ScoreCalculator.kt` | `ScoreResult.capCode`；`paymentPlan()`；`totalPoints()` 委托 `paymentPlan.total`；文本委托 Formatter；数值与支付公式保持 |
| `engine/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt` | `ScoreBreakdown.yakuEntries` 与 `paymentPlan`；役种/支付委托 Formatter；不再 import `SettlementService` |
| `engine/src/main/kotlin/com/majiang/rules/YakuEvaluator.kt` | `Yaku.code` 稳定码；`fromCode`/`fromDisplayName`/`isKnownCode` 兼容查找 |
| `engine/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt` | 快照深拷贝包含 `yakuEntries`，保持不可变隔离 |
| `engine/src/main/kotlin/com/majiang/settlement/SettlementService.kt` | `formatPaymentLines` 保留原入口，逐字委托 Formatter |
| `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolScoreCodes.kt` | 新增 v5 稳定码/字段名契约目录；当前不参与序列化 |

### 2.2 构建稳定性修复（独立验收发现）

| 文件 | 变更 |
|---|---|
| `gradle.properties` | `org.gradle.parallel=true` → `false`；强制重编译时模块间 Kotlin 编译/测试任务不再并发，避免主编译产物与 testFixtures/server classpath 竞态 |
| `build.gradle` | 对 `KotlinCompile`、`Jar` 与 `Test` 任务关闭 build cache（`outputs.cacheIf { false }`）；保留其他任务缓存。避免 `--rerun-tasks` 与缓存组合下测试结果 `in-progress-results-generic.bin` 被并发清理/丢失，以及 Kotlin 编译产物/classpath 归档从缓存恢复时的可见性问题 |

### 2.3 测试

| 文件 | 覆盖 |
|---|---|
| `engine/src/test/kotlin/com/majiang/rules/ScoreTextFormatterContractTest.kt` | 既有字符串逐字等价、普通分/各封顶/役满/累计役满/本场棒/立直棒/里宝牌可见性/支付/兼容入口（10 项） |
| `engine/src/test/kotlin/com/majiang/rules/ScoreCodesAndPaymentPlanTest.kt` | 42 个役种码唯一且稳定、封顶码、四类支付计划的角色/人数/金额/总额、`yakuEntries` 与 `yakuLines` 一致、未知旧名回退、快照隔离与错配拒绝（6 项） |
| `protocol/src/test/kotlin/com/majiang/transport/api/ScoreWireBoundaryTest.kt` | v4 对 `capCode`/`yakuEntries`/`paymentPlan`/`scoreDetailVersion`/`scoreDetail`/HELLO 新字段的严格拒绝；当前协议版本低于结构化门槛（4 项） |
| `server/src/test/kotlin/com/majiang/server/mapping/ScoreCodeContractTest.kt` | engine 与 protocol 码表逐项一致；v4 mapper 不输出新增字段；既有字符串/数值映射逐项相等（4 项） |
| `client/src/test/kotlin/com/majiang/client/mapping/ScoreLegacyViewContractTest.kt` | 旧字符串与数值字段经协议→视图逐字保留；未知役种不猜测、不改写（1 项） |
| `server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolMapperTest.kt` | `normalizedFields` 明确排除 engine-only `capCode`/`yakuEntries`，其余映射仍全字段比较 |

---

## 3. engine → server → protocol → client → presentation 字段流转审计

### 3.1 现有字段

| 字段 | engine | server | protocol | client | presentation | 结构化消费现状 |
|---|---|---|---|---|---|---|
| `fu`、`han` | `ScoreResult` | `EngineProtocolMapper.toProtocolScoreResult` | `ProtocolScoreResult` | `ScoreResultView` | 总番/符数/支付推导 | 数值，已端到端 |
| `basePoints` | `ScoreResult` | 同上 | 同上 | 同上 | 基本点、支付推导 | 数值，已端到端 |
| `rawBasePoints` | `ScoreResult` | 同上 | 同上 | 同上 | 兼容旧字段 | 数值，已端到端 |
| `rawBasePointsExact` | `ScoreResult`（BigInteger 精确值字符串） | 同上 | 同上 | 同上 | 原始基本点展示 | 字符串承载大整数，当前展示消费 |
| `isYakuman`、`yakumanMultiplier` | `ScoreResult` | 同上 | 同上 | 同上 | 役满标题/固定基本点 | 数值/布尔，已端到端 |
| `title` | Formatter 生成 | 同上 | 同上 | 同上 | 役满标签、兜底标题 | 文本；无稳定码消费者 |
| `capReason` | Formatter 生成 | 同上 | 同上 | 同上 | 封顶说明 | 文本；无稳定码消费者 |
| `formulaText` | Formatter 生成 | 同上 | 同上 | 同上 | 当前 Presenter 主要自行拼公式，未强依赖该字段 | 文本；无结构化消费者 |
| `yakuLines` | `ScoreBreakdown`（`List<Pair<String,Int>>`） | 转 `Pair` 列表 | `ProtocolScoreBreakdown.yakuLines` | `ScoreBreakdownView.yakuLines` | 役种行文本 | 文本 + 番数；无稳定役种码 |
| `paymentLines` | Formatter 生成 | 转字符串列表 | `ProtocolScoreBreakdown.paymentLines` | `ScoreBreakdownView.paymentLines` | 基础支付分配一行 | 文本；无逐付款方结构化金额 |
| `honbaAddition` | `ScoreBreakdown`（300×本场） | 映射 | 同名字段 | 同名字段 | 本场棒展示 | 数值，已端到端 |
| `riichiAddition` | `ScoreBreakdown`（1000×立直棒） | 映射 | 同名字段 | 同名字段 | 供托展示 | 数值，已端到端 |
| `totalPoints` | `ScoreBreakdown` | 映射 | 同名字段 | 同名字段 | 最终总得点 | 数值，已端到端 |
| `uraEligible` | `ScoreBreakdown` | 映射 | 同名字段 | 同名字段 | 里宝牌是否展示；公开投影清洗 | 布尔，已端到端 |
| `isDealer`、`isTsumo` | `ScoreBreakdown` | 映射 | 同名字段 | 同名字段 | 支付角色/文案分支 | 布尔，已端到端 |
| `dora` 计数/牌列表 | `DoraBreakdown` | 映射 | `ProtocolDoraBreakdown` | `DoraBreakdownView` | 表/里/赤宝牌明细 | 数值与物理牌，已端到端 |
| `ScoreSettled.payments` / `newScores` | `SettlementService`/事件 | 映射 | 数值 Map/List | 数值 Map/List | 实际收支 | 数值，与基础明细分离 |

### 3.2 新增的 engine-only 结构化字段

| 字段 | 当前状态 | 真实结构化消费需求 | 未上线原因 |
|---|---|---|---|
| `ScoreResult.capCode` | engine 已有，server 不映射 | 本地化/稳定封顶原因；避免依赖日文 `capReason` | 新增 v4 字段会被旧端拒绝 |
| `ScoreBreakdown.yakuEntries` | engine 已有，server 不映射 | 稳定役种码、排序/筛选/本地化 | 同上；且需要新的 element DTO |
| `ScoreBreakdown.paymentPlan` | engine 派生，server 不映射 | 逐付款方金额，消除 presentation 自行重算乘数/取整 | 同上；且需要新的 object DTO |

### 3.3 结论

- 当前 presentation 对“文本标签”只做展示，没有真正的稳定码消费者；对“支付金额”则确实存在客户端重算：`SettlementDetailPresenter` 依据 `basePoints`、`isDealer`、`isTsumo` 重新乘 2/4/6 并向上取整。`paymentPlan` 是这一真实结构化需求的 engine 侧答案，但把逐付款方金额送到 client 必须新增线路字段。
- 现有数值字段已覆盖符、番、基本点、精确原始基本点、本场、立直棒、总得点、役满倍数和里宝牌资格；没有“缺数值”导致必须立即改线路的阻断性缺陷。
- 因此本版本不新增线路字段，也不在 client 侧从日文字符串反推稳定码（那会造成第二套、易漂移的码源）。

---

## 4. 已完成契约：稳定码与结构化支付

### 4.1 役种稳定码

`Yaku.code` 为 42 项、唯一、大写 ASCII；当前与枚举名一致，但它是独立冻结字段，后续枚举重命名不得改码。完整集合由 `ProtocolScoreCodes.YAKU_CODES` 镜像，server 契约测试逐项比对。

```text
RIICHI IPPATSU MENZEN_TSUMO TANYAO PINFU IIPEIKO
YAKUHAI_HAKU YAKUHAI_HATSU YAKUHAI_CHUN YAKUHAI_BAKAZE YAKUHAI_JIKAZE
HAITEI HOUTEI RINSHAN CHANKAN
DOUBLE_RIICHI CHANTA ITTSU SAN_SHOKU_DOUJUN SAN_SHOKU_DOUKOU
TOITOI SAN_ANKOU SAN_KANTSU SHOUSANGEN HONROUTOU CHIITOITSU
HONITSU JUNCHAN RYANPEIKOU CHINITSU
KOKUSHI SUUANKOU DAISANGEN SHOUSUUSHII DAISUUSHII TSUUIISOU
RYUUIISOU CHINROUTOU CHURENPOUTOU SUUKANTSU TENHOU CHIIHOU
```

规则：

- 新增役种只能追加新码，不能复用已删除码。
- `displayName` 继续作为旧字符串字段的展示名，必须与重构前逐字一致。
- 未知码回退：客户端优先使用同一元素的 `displayName`；若为空则使用旧 `yakuLines[i].first`；仍为空则显示 `不明役`，但不得丢弃整条结算。未知码不得映射到另一个已存在役种。

### 4.2 封顶原因码

| 码 | 含义 | 现有 `title` | 现有 `capReason` |
|---|---|---|---|
| `MANGAN` | 满贯 | `満貫` | `満貫封頂` |
| `HANEMAN` | 跳满 | `跳満` | `跳満` |
| `BAIMAN` | 倍满 | `倍満` | `倍満` |
| `SANBAIMAN` | 三倍满 | `三倍満` | `三倍満` |
| `KAZOE_YAKUMAN` | 累计役满 | `数え役満` | `数え役満` |
| `YAKUMAN` | 役满（具体倍数由 `yakumanMultiplier` 承载） | `役満`/`ダブル役満`/`N倍役満` | 同 `title` |

`capCode` 为 `null` 表示未封顶。未知 `capCode` 回退到 `capReason`，再回退到 `title`；`basePoints` 永不由码推导。

### 4.3 支付结构

```text
PaymentKind: DEALER_TSUMO | NON_DEALER_TSUMO | DEALER_RON | NON_DEALER_RON
PayerRole:   DEALER | NON_DEALER | DISCARDER
PaymentComponent(payerRole, amount, count)
PaymentPlan(kind, components, total)   // total == Σ(amount × count)
```

- `DEALER_TSUMO`：`NON_DEALER` × 3，金额 = `dealerTsumoPayment()`。
- `NON_DEALER_TSUMO`：`DEALER` × 1 + `NON_DEALER` × 2，金额分别 = `nonDealerTsumoPayment()` 的两个值。
- `DEALER_RON`：`DISCARDER` × 1，金额 = `dealerRonPayment()`。
- `NON_DEALER_RON`：`DISCARDER` × 1，金额 = `nonDealerRonPayment()`。
- 以上均为**基础支付**，不含本场棒与供托立直棒；后两者继续由 `honbaAddition`、`riichiAddition` 数值字段承载，实际收支由 `SettlementService`/`ScoreSettled.payments` 承载。
- `paymentLines` 必须与 `paymentPlan` 渲染一致：庄家自摸「自摸：闲家各付 N」；闲家自摸「自摸：庄家付 N、闲家各付 M」；荣和「荣和：P<seat> 支付 N」/「荣和：放铳者 支付 N」。

---

## 5. 待独立立项的 v5 线路字段契约

以下字段名、类型、顺序、不变量和回退规则已经冻结为 `ProtocolScoreCodes` 常量与本文契约；**当前 v4 不实现**。实现时必须作为独立协议版本任务，不得夹带进 v4 的普通重构。

### 5.1 `ProtocolScoreResult` 新增可选字段

```json
{
  "fu": 30,
  "han": 5,
  "basePoints": 2000,
  "title": "満貫",
  "isYakuman": false,
  "yakumanMultiplier": 0,
  "rawBasePoints": 3840,
  "capReason": "満貫封頂",
  "formulaText": "30符 × 2^(2+5) = 3840 → 満貫封頂 2000",
  "rawBasePointsExact": "3840",
  "capCode": "MANGAN"
}
```

- `capCode`: `String?`，可选；`null` 表示未封顶。值域见 §4.2。
- 既有 10 个字段保持名称、类型、必填性不变；`capCode` 只做追加。
- 编码器在 v5 下可始终输出；v4 编码器绝不输出。

### 5.2 `ProtocolScoreBreakdown` 新增字段

```json
{
  "yakuLines": [{"first": "平和", "second": 1}],
  "yakumanCount": 0,
  "dora": { "...": "既有结构不变" },
  "fu": { "...": "既有结构不变" },
  "score": { "...": "见 5.1" },
  "paymentLines": ["荣和：P3 支付 1000"],
  "honbaAddition": 0,
  "riichiAddition": 0,
  "totalPoints": 1000,
  "uraEligible": false,
  "isDealer": false,
  "isTsumo": false,
  "yakuEntries": [
    {"code": "PINFU", "displayName": "平和", "han": 1}
  ],
  "paymentPlan": {
    "kind": "NON_DEALER_RON",
    "components": [
      {"payerRole": "DISCARDER", "amount": 1000, "count": 1}
    ],
    "total": 1000
  }
}
```

不变量：

- `yakuEntries.size == yakuLines.size`，且逐项 `displayName/han` 与 `yakuLines` 对应元素相等；顺序一致。
- `paymentPlan.total == Σ(component.amount × component.count)`；`components` 非空；`amount >= 0`、`count >= 1`。
- `paymentPlan` 只描述基础支付；`totalPoints` 含本场/供托，二者不得混用。
- `yakuLines`、`paymentLines`、`title`、`capReason`、`formulaText` 在 v5 中仍然必填，且对同一计分事实与 v4 逐字相同。
- `yakuEntries.code` 未知时，客户端回退到 `displayName`；`paymentPlan` 缺失/未知 `kind`/不变量失败时，客户端回退到 `paymentLines`；不得因结构化字段异常拒绝整条快照。

### 5.3 capability 与握手

- `ProtocolAccessCapabilities` 追加 `scoreDetailVersion: Int`，默认 `0`；v5 服务端返回 `1`。
- `ProtocolScoreCodes.CONTRACT_VERSION = 1` 对应 `scoreDetailVersion = 1`。
- 不能只加 capability 字段：旧 `ProtocolAccessCapabilitiesAdapter` 对未知字段同样严格拒绝。因此 capability 字段也必须和业务协议版本一起升级。
- `TcpWire.BUSINESS_VERSION` 从 `1` 升到 `2`，`ApiProtocol.PROTOCOL_VERSION` 从 `4` 升到 `5`。`TcpHello`/`TcpWelcome` 已有 `businessVersion`，两端当前都是硬相等比较；这是比新增 HELLO 字段更小、更明确的版本通道。
- 若未来要求 v1/v2 灰度互通，必须另行设计 HELLO 能力位图并放宽旧端未知字段策略；该放宽本身会破坏当前严格边界，不能作为本次迁移的默认前提。

---

## 6. 阻断证据：为什么不能在当前 MJ-TCP/1 安全加字段

### 6.1 旧端严格未知字段拒绝

- `ProtocolScoreResultAdapter` 的允许字段集合精确为：
  `fu, han, basePoints, title, isYakuman, yakumanMultiplier, rawBasePoints, capReason, formulaText, rawBasePointsExact`
- `ProtocolScoreBreakdownAdapter` 的允许字段集合精确为：
  `yakuLines, yakumanCount, dora, fu, score, paymentLines, honbaAddition, riichiAddition, totalPoints, uraEligible, isDealer, isTsumo`
- `ProtocolAccessCapabilitiesAdapter`、`ApiResponseAdapter`、`TcpHelloAdapter` 同样调用 `checkUnknownFields`。
- 这些 adapter 在解析到未知字段时抛 `JsonParseException`，旧客户端会丢弃整条快照/响应，而不是忽略新字段。

新增 `capCode`、`yakuEntries`、`paymentPlan` 或 `scoreDetailVersion` 时，旧客户端在第一次收到含新字段的结算快照就会失败。这不是“可选字段向后兼容”，因为旧端不是宽松解析器。

### 6.2 业务版本没有协商，只有硬相等

- 服务端 `TcpTableServer` 对 HELLO 的 `businessVersion` 与 `TcpWire.BUSINESS_VERSION` 做不等即 `VERSION_MISMATCH` 关闭。
- 客户端 `TcpClientBinding` 对 `TcpWelcome` 的 `businessVersion` 同样做硬相等校验。
- 因此：
  - 新服务端 + 旧客户端：旧客户端无法连接；
  - 旧服务端 + 新客户端：新客户端无法连接；
  - 不能靠“客户端不请求新 API”绕过，因为快照本身就会携带新字段。
- 升 `businessVersion` 是明确的独立协议升级，必须双端配套发布；不是当前 v4 内的安全增量。

### 6.3 首次交付时的版本门槛（历史）

- `ApiProtocol.PROTOCOL_VERSION = 4`（当时）。
- `ProtocolScoreCodes.STRUCTURED_SCORE_MIN_PROTOCOL_VERSION = 5`。
- 当时测试断言 `4 < 5`，防止在未升级协议时把结构字段塞进 v4。v5 实施后该测试已改为“历史 v4 fixture 解码 + 新升版门槛”的有效验证，见 §12。

### 6.4 已验证的阻断测试

`protocol` 模块 `ScoreWireBoundaryTest` 四项：

1. v4 `ProtocolScoreResult` 精确字段集合；注入 `capCode` 后抛 `Unknown field`。
2. v4 `ProtocolScoreBreakdown` 精确字段集合；注入 `yakuEntries`、`paymentPlan` 后均抛 `Unknown field`。
3. v4 `ProtocolAccessCapabilities` 注入 `scoreDetailVersion`、`ApiResponse` 注入 `scoreDetail` 后均拒绝。
4. `TcpHello` 注入 `scoreDetailVersion` 后由 `TcpProtocolException` 包装的 `Unknown field` 拒绝；当前协议版本低于结构化门槛。

### 6.5 为什么不走“新增旁路 API”作为替代

曾评估“新增一个只让新客户端查询结构化算分的旁路 API”，它确实可避开旧客户端对既有 DTO 的解析，但不作为本交付方案：

- 仍需新的 `ApiTag`、`ApiResponse` 承载字段/新 DTO 与 capability；这已经超出 `ApiTags`/T0 冻结契约，是独立协议立项，而不是 D-07 的“纯格式化边界”。
- 旁路 API 会形成第二套结算明细数据源，快照内仍只有旧字符串；无法满足“engine → protocol → client → presentation 全链迁移”的本来目标。
- 结算查询还涉及局历史保留、房间回收、观战公开投影和幂等，新增存储/生命周期面远大于给既有 breakdown 追加字段。
- 因此本交付选择：当前版本完成 engine 侧安全实现与 v5 契约冻结；把真正的全链字段迁移留给独立协议版本。

---

## 7. 独立协议迁移方案（可直接执行）

### 7.1 前置条件

1. 冻结本交付的 `ProtocolScoreCodes` 码表；任何码变更先改契约测试。
2. 确认 `TcpWire.BUSINESS_VERSION` 2、`ApiProtocol.PROTOCOL_VERSION` 5 的发布窗口；活动房间不热迁移。
3. 客户端与服务端同一发布批次；旧服务端停止接纳新房，活动房间结束/排空后升级。

### 7.2 DTO 与编解码步骤

1. `protocol`：
   - 新增 `ProtocolYakuEntry(code, displayName, han)`、`ProtocolPaymentComponent(payerRole, amount, count)`、`ProtocolPaymentPlan(kind, components, total)`。
   - `ProtocolScoreResult` 追加 `capCode: String? = null`。
   - `ProtocolScoreBreakdown` 追加 `yakuEntries: List<ProtocolYakuEntry> = emptyList()`、`paymentPlan: ProtocolPaymentPlan? = null`。
   - `ProtocolAccessCapabilities` 追加 `scoreDetailVersion: Int = 0`。
   - 更新上述 adapter 的允许字段集合、严格类型校验、跨字段不变量与未知码处理；未知 `code`/`kind` 不得让解析失败。
   - 保持 `ProtocolScoreCodes` 为唯一码表；adapter 拒绝非字符串/非整数/空数组/负数 count 等结构错误。
2. `server`：
   - `EngineProtocolMapper` 在 v5 输出 `capCode`、`yakuEntries`、`paymentPlan`；v4 编码器路径保持原字段集合。
   - 由连接协商到的业务版本选择编码器/序列化字段集；不能在 v4 路径输出任何新字段。
   - 现有 `yakuLines`/`paymentLines` 继续由同一 engine 值生成，测试断言二者与结构化字段一致。
3. `client`：
   - `ProtocolViewMapper` 映射新字段到 `ScoreResultView`/`ScoreBreakdownView` 的新可选属性；旧字段映射不变。
   - 新客户端连接旧服务端时在 HELLO/WELCOME 明确失败并提示升级，不得静默降级。
   - 同一 v5 内若 `scoreDetailVersion = 0` 或字段缺失，回退旧字符串。
4. `presentation`：
   - 标签优先使用 `code` 的本地化表；码未知时回退 `displayName`，再回退旧 `yakuLines`。
   - 支付展示优先使用 `paymentPlan` 的 `components`；计划缺失/未知时回退旧 `paymentLines`。
   - 数值（`totalPoints`、`honbaAddition`、`riichiAddition`、`basePoints` 等）继续来自数值字段，不从文本解析。
   - 观战公开投影仍受 `uraEligible` 与公开白名单约束，新增字段不得泄露里宝牌。

### 7.3 版本/切换/回退规则

- **v4 关闭**：v5 服务端不接受 v4 HELLO；旧客户端收到 `VERSION_MISMATCH` 并提示升级。不能在同一服务进程内同时向 v4 和 v5 输出不同字段集，除非另建完整的双编码器与协商测试（不属于本方案）。
- **升级顺序**：停止旧服新建房 → 等活动房间结束/明确排空 → 升级服务端 → 配套客户端发布 → 新建房间。无持久化，不承诺活动房间/旧 token 跨版本恢复。
- **回退**：回退两端到上一发布物并重启；只新建房间，不恢复旧进程房间；旧 token/回执不跨版本复用。
- **灰度**：若要灰度，先做 HELLO 能力位图与宽松/严格双解析设计，不能直接用当前 `businessVersion` 硬相等通道。
- **未知码**：客户端必须容错；服务端不得因为客户端未知码而拒绝连接。码表新增只追加，不改变既有语义。
- **旧字符串保留**：v5 至少保留一个大版本周期；删除前必须有独立契约迁移与全量展示回归，不得以“已经用码”为由直接删除。

### 7.4 迁移验收矩阵

1. v4 adapter 对每个新字段仍严格拒绝（保留本交付测试）。
2. v5 adapter：字段类型、未知码、缺失可选字段、空/非法数组、负数 count、`total` 不守恒、`yakuEntries` 与 `yakuLines` 不一致、`paymentPlan` 与 `paymentLines` 不一致。`paymentPlan` 还必须与 `isDealer`/`isTsumo`、四种 kind 的 payerRole/count 形态、`basePoints` 的百位取整金额、`honbaAddition`/`riichiAddition` 和 `totalPoints` 全部一致；任一不一致只降级 `paymentPlan`，不拒绝快照。
3. server v4 编码字节级字段集合不变；v5 编码字段集合精确等于契约。
4. engine → server → protocol → client → presentation 全链：普通分、满贯、跳满、倍满、三倍满、累计役满、1/2/N 倍役满、庄/闲自摸/荣和、本场棒、立直棒、里宝牌可见/不可见、观战公开流。
5. 旧客户端拒绝新服务端、新客户端拒绝旧服务端；错误码/提示明确；无静默降级。
6. 回退：上一发布物 + 新房间可连接；旧 token/房间不复活；资源释放正常。
7. 回归：136 张守恒、七模块依赖、三大绝技经济/判定、TCP 帧/心跳/幂等/重连矩阵不变。

---

## 8. 测试执行记录

全部 Gradle 串行执行，未并发。工作目录 `D:\majiang`。

### 8.1 定向测试

| 命令 | 结果 |
|---|---|
| `.\gradlew.bat :engine:test --tests "com.majiang.rules.ScoreTextFormatterContractTest" --console=plain` | BUILD SUCCESSFUL，10/10 PASSED，exit 0 |
| `.\gradlew.bat :engine:test --tests "com.majiang.rules.ScoreCodesAndPaymentPlanTest" --tests "com.majiang.rules.ScoreTextFormatterContractTest" --console=plain` | BUILD SUCCESSFUL，16/16 PASSED，exit 0 |
| `.\gradlew.bat :protocol:test --tests "com.majiang.transport.api.ScoreWireBoundaryTest" --console=plain` | BUILD SUCCESSFUL，4/4 PASSED，exit 0 |
| `.\gradlew.bat :server:test --tests "com.majiang.server.mapping.ScoreCodeContractTest" --tests "com.majiang.server.mapping.EngineProtocolMapperTest" --console=plain` | BUILD SUCCESSFUL，12/12 PASSED，exit 0 |
| `.\gradlew.bat :client:test --tests "com.majiang.client.mapping.ScoreLegacyViewContractTest" --tests "com.majiang.client.api.ProtocolViewMapperTest" --console=plain` | BUILD SUCCESSFUL，19/19 PASSED，exit 0 |

说明：`ScoreWireBoundaryTest` 首次运行时，HELLO 用例按 `JsonParseException` 断言，但实际被 `TcpProtocolException` 包装；已将断言改为检查 `TcpProtocolException` 的 message/cause，重跑通过。这是测试边界修正，不是放宽产品严格性。

### 8.2 模块全量

| 命令 | 结果（JUnit XML 汇总） |
|---|---|
| `.\gradlew.bat :engine:test --console=plain` | 506 tests，0 failures，0 errors，0 skipped，BUILD SUCCESSFUL，exit 0 |
| `.\gradlew.bat :protocol:test --console=plain` | 178 tests，0 failures，0 errors，0 skipped，BUILD SUCCESSFUL，exit 0 |
| `.\gradlew.bat :server:test --console=plain` | 334 tests，0 failures，0 errors，0 skipped，BUILD SUCCESSFUL，exit 0 |
| `.\gradlew.bat :client:test --console=plain` | 218 tests，0 failures，0 errors，0 skipped，BUILD SUCCESSFUL，exit 0 |
| `.\gradlew.bat :presentation:test --console=plain` | 316 tests，0 failures，0 errors，0 skipped，BUILD SUCCESSFUL，exit 0 |

合计 1,552 项（engine 506 + protocol 178 + server 334 + client 218 + presentation 316）。新增/修改测试 15 项：engine 6、protocol 4、server 4、client 1。

### 8.3 强制重编译与干净构建（独立验收修复）

独立验收报告 `--rerun-tasks` 退出码 1 后，本任务先复现并定位：

1. 第一次复现（验收方原命令）在 `:protocol:test` 失败，CLI 报 `java.io.EOFException`；Gradle daemon 日志显示真实根因是
   `NoSuchFileException: engine/build/test-results/test/binary/in-progress-results-generic.bin`，即测试 worker 与 Gradle 测试结果文件清理/缓存发生竞争。
2. 仅加 `--no-parallel --max-workers=1` 仍会在 `:engine:test` 复现同类测试结果文件竞争；加 `--no-build-cache` 后同一强制重编译通过。确认 build cache 参与了该竞争。
3. 任务图本身没有缺依赖：`:engine:compileTestFixturesKotlin --dry-run` 明确先执行 `:engine:compileKotlin`、`:engine:classes`、`:engine:jar`；`:server:compileKotlin --dry-run` 也先执行 `:engine:compileKotlin`。`engine/build/libs/engine.jar` 内的 `com/majiang/access/ActionReceipt.class`、`ClockView.class` 等均存在。因此不是 `dependsOn` 缺失，而是强制重编译时并行模块任务与缓存恢复/结果文件清理的竞态。
4. `clean` 第一次失败是上一轮失败遗留的 **client test worker 孤儿进程**（PID 21732，父 Gradle daemon 已停止）占用 `engine.jar`；终止该孤儿后重试 `clean test` 通过。这是本任务自己上一次失败留下的环境残留，不是新代码编译错误。

修复：

- `gradle.properties`：`org.gradle.parallel=false`，所有模块任务串行。
- `build.gradle`：`KotlinCompile`、`Jar` 与 `Test` 任务禁用 build cache；其余任务缓存保留。`Jar` 也被禁用是因为 server 的 compileClasspath 消费 `engine.jar`，不能允许强制重编译时从旧缓存恢复过期归档。

修复后证据：

| 命令 | 结果 |
|---|---|
| `.\gradlew.bat :engine:compileTestFixturesKotlin :server:compileKotlin --rerun-tasks --console=plain` | **BUILD SUCCESSFUL in 17s，5 actionable tasks: 5 executed，exit 0**；原先两个失败任务均实际执行 |
| `.\gradlew.bat :engine:test :protocol:test :server:test :client:test :presentation:test --rerun-tasks --console=plain` | **BUILD SUCCESSFUL in 1m 37s，25 actionable tasks: 25 executed，exit 0**；1,552 tests，0 failures/errors/skipped |
| `.\gradlew.bat clean test --console=plain` | **BUILD SUCCESSFUL in 1m 33s，73 actionable tasks（49 executed，24 from cache），exit 0**；1,552 tests，0 failures/errors/skipped |

以上命令均为强制/干净重编译证据，不再以 `UP-TO-DATE` 作为验收依据。强制重编译日志确认 `:engine:compileKotlin`、`:engine:compileTestFixturesKotlin`、`:server:compileKotlin` 均为实际执行任务（非 UP-TO-DATE），原先的 application/event/model/session/state 与 `ActionReceipt`/`ClockView` 未解析引用未再出现。

### 8.4 启动器/Android 构建

| 命令 | 结果 |
|---|---|
| `.\gradlew.bat :lwjgl3:compileKotlin :android:assembleDebug --console=plain` | BUILD SUCCESSFUL in 12s，49 actionable tasks（13 executed，8 from cache，28 up-to-date）；exit 0；APK 重新生成 |
| `.\gradlew.bat -I tools/architecture/v4-review.init.gradle :server:test --tests "com.majiang.v4lobby.V4PublicProjectionAcceptanceTest" --tests "com.majiang.v4lobby.V4PublicWireAcceptanceTest" --console=plain` | BUILD SUCCESSFUL，24/24 PASSED，exit 0（公开投影/里宝牌权限/观战隐私与线路回归） |

Android 构建出现既有 R8/D8 Kotlin metadata 警告（`RoundTransitionPlanner` 等）和 `stripDebugDebugSymbols` 无法剥离预编译 `.so` 的提示，均与本次算分改动无关，构建结果成功；未把它伪装成“零警告”。

### 8.5 未执行的环境项

- 桌面真实 GL 冒烟、Android 真机/模拟器网络实测、跨物理网络 T4 验收未执行；D-07 不改变 presentation 源码与传输代码，本交付不声称这些环境项已通过。
- 开发代理没有再下发任务；交付时未提交、未推送。

---

## 9. 行为等价与边界说明

- `ScoreCalculator.calculate` 的符、番、`basePoints`、`isYakuman`、`yakumanMultiplier`、`rawBasePoints`、`rawBasePointsExact` 与重构前逐项相等；`ScoreDerivationContractTest`、`ScoreTextFormatterContractTest` 均在完整 engine 套件中通过。
- `title`、`capReason`、`formulaText`、`yakuLines`、`paymentLines` 的所有既有断言原样通过；`SettlementService.formatPaymentLines` 的四个入口与 Formatter 输出逐字一致。
- `totalPoints` 仍等于四个支付函数组合后的总额（现在经 `paymentPlan.total` 同一结构），本场/立直/最终合计字段未改。
- `ScoreBreakdown` 新增 `yakuEntries` 会进入其 `equals/hashCode`（与 `yakuLines` 构造期一致性校验），但不会改变任何线路字段、字符串显示或数值；快照 copies 新增列表并保持不可变；`EngineProtocolMapperTest.normalizedFields` 明确排除 engine-only 字段，其余字段继续全字段比较。
- 136 张物理牌守恒、洗牌/码牌、三大绝技经济/判定未触及；相关 engine/server 全量测试通过。
- 观战隐私：`uraEligible` 与 `sanitizeBreakdown` 未改；公开流不新增任何私有牌字段。

---

## 10. 最终验收命令

以下为独立验收发现编译阻断后的最终证据，均串行执行：

```powershell
.\gradlew.bat :engine:compileTestFixturesKotlin :server:compileKotlin --rerun-tasks --console=plain
.\gradlew.bat clean test --console=plain
.\gradlew.bat :engine:test :protocol:test :server:test :client:test :presentation:test --rerun-tasks --console=plain
.\gradlew.bat :lwjgl3:compileKotlin :android:assembleDebug --console=plain
```

结果：

- 聚焦编译：退出码 0；BUILD SUCCESSFUL in 17s；5 actionable tasks（5 executed）
- `clean test`：退出码 0；BUILD SUCCESSFUL in 1m 33s；73 actionable tasks（49 executed，24 from cache）
- `:engine:test :protocol:test :server:test :client:test :presentation:test --rerun-tasks`：退出码 0；BUILD SUCCESSFUL in 1m 37s；25 actionable tasks（25 executed）
- 模块计数（两条命令的 JUnit XML 一致）：engine 506、protocol 178、server 334、client 218、presentation 316，合计 **1,552 tests，0 failures / 0 errors / 0 skipped**
- 构建：`:lwjgl3:compileKotlin :android:assembleDebug` 成功（exit 0）；APK 重新生成
- 独立 V4 公开投影/线路复核：24/24 PASSED（exit 0），未把 `tools/architecture` 探针混入模块测试计数
- 备注：全仓 `test` 不包含需要真实 GL/Android 环境的冒烟；D-07 未改 presentation 源码或 UI 行为，现有 presentation 316 项全量通过。
- 构建稳定性修复见 §8.3；不再以 `UP-TO-DATE` 的成功作为验收证据。

---

## 11. v5 中已关闭的独立协议版本事项（原首次交付遗留）

1. 把 `capCode`、`yakuEntries`、`paymentPlan` 送入 protocol DTO。
2. 把 `scoreDetailVersion` 加入 `ProtocolAccessCapabilities`。
3. 把 `businessVersion` 升到 2、`ApiProtocol.PROTOCOL_VERSION` 升到 5，并实现双端配套切换。
4. client `ScoreResultView`/`ScoreBreakdownView` 增加稳定码/支付结构字段。
5. presentation 用稳定码本地化、用 `paymentPlan` 替代客户端重算。
6. v5 编解码、未知码回退、版本拒绝、回退与全链展示验收。

以上均已在本次 v5 实施中关闭；具体文件、契约、回退矩阵与测试证据见 §12。`tools/architecture` 探针中的旧 `businessVersion = 1` 握手已同步改为 `TcpWire.BUSINESS_VERSION`。

---

## 12. v5 结构化算分线路实施记录（2026-10-19）

### 12.1 版本与传输边界

| 常量 | v4（撤回） | v5（当前） | 说明 |
|---|---:|---:|---|
| `ApiProtocol.PROTOCOL_VERSION` | 4 | **5** | 业务协议版本；结构化算分门槛 |
| `TcpWire.BUSINESS_VERSION` | 1 | **2** | HELLO/WELCOME 硬相等协商 |
| `TcpWire.WIRE_VERSION` | 1 | **1** | MJ-TCP/1 帧格式不变 |
| `TcpWire.HEADER_SIZE` | 12 | **12** | 魔数/type/flags/length 均不变 |
| `ProtocolScoreCodes.CONTRACT_VERSION` | — | **1** | 对应 `scoreDetailVersion=1` |

- `TcpHello` / `TcpWelcome` 的 `businessVersion` 由双端与 `TcpWire.BUSINESS_VERSION` 硬相等校验；v4（businessVersion=1）与新客户端、v5 服务端均返回 `VERSION_MISMATCH`。客户端 `connectInitial` 保留该 `TcpProtocolException`，不再降级成笼统的握手超时。
- 心跳（3s/10s）、12 字节帧、`requestScope`/幂等回执、玩家/公开独立游标、全量快照、`sessionId/syncEpoch/controlEpoch`、重连与隐私边界均未改变。
- **不提供 v4/v5 同进程混发、双编码器或活动房间热迁移**。升级窗口：停止旧服新建房 → 排空活动房间 → 服务端升级 → 同批客户端发布 → 新建房。回退：双端回上一发布物并重启，旧房间/token/回执不跨版本恢复。

### 12.2 线路 DTO（冻结字段）

```text
ProtocolScoreResult:
  fu, han, basePoints, title, isYakuman, yakumanMultiplier,
  rawBasePoints, capReason, formulaText, rawBasePointsExact,   // 旧字段，必填性不变
  capCode: String? = null                                       // v5 追加

ProtocolScoreBreakdown:
  yakuLines, yakumanCount, dora, fu, score, paymentLines,
  honbaAddition, riichiAddition, totalPoints, uraEligible,
  isDealer, isTsumo,                                            // 旧字段，必填性不变
  yakuEntries: List<ProtocolYakuEntry> = emptyList(),           // v5
  paymentPlan: ProtocolPaymentPlan? = null                      // v5

ProtocolYakuEntry:      { code, displayName, han }
ProtocolPaymentComponent: { payerRole, amount, count }
ProtocolPaymentPlan:    { kind, components, total }

ProtocolAccessCapabilities:
  ...既有 8 字段..., scoreDetailVersion: Int = 0                 // v5，服务端发 1
```

严格编解码（`ProtocolScoreAdapters.kt`、`ProtocolScoreBreakdownAdapter`）：

- 未知字段一律 `Unknown field` 拒绝；元素级对象同样检查，不因新增结构化容器放松其他 DTO。
- JSON 类型严格：字符串/整数/布尔/数组/对象不匹配即拒绝；整数拒绝小数/科学计数法/溢出。
- 上限：役种条目 ≤ 64、支付分组 ≤ 8、码长 ≤ 64、展示名 ≤ 256、`han` 0..1000、金额 ≤ 10,000,000、人数 1..8、总额 ≤ 100,000,000、`scoreDetailVersion` 0..1000。
- 构造器与列表副本：`ProtocolScoreBreakdown.yakuEntries`、`ProtocolPaymentPlan.components`、`ProtocolScoreBreakdown.yakuLines/paymentLines` 均为不可变副本，外部可变列表污染无效。
- 一致性：`yakuEntries` 必须与 `yakuLines` 等长且逐项 `displayName/han` 相等；否则整组结构化条目降级为空。`paymentPlan` 必须非空且 `total == Σ(amount × count)`，并且必须通过跨字段校验（见 §12.4）；否则降级为 null。二者都**不拒绝整条快照**。
- 未知码：`capCode`、`code`、`kind`、`payerRole` 的未知取值是允许的向前兼容扩展点，解析保留原值；由 client mapper/展示层决定回退。未知字段/错误类型不是扩展点。

### 12.3 全链映射

| 层 | 变更 |
|---|---|
| engine | 既有 `capCode`/`yakuEntries`/`paymentPlan` 不变；无协议依赖 |
| server `EngineProtocolMapper` | `toProtocolScoreResult` 输出 `capCode`；`toProtocolScoreBreakdown` 输出 `yakuEntries`/`paymentPlan`；`toProtocolCapabilities` 输出 `scoreDetailVersion=1`。`TcpRequestRouter`、`InProcessBinding` 的 capability 同样发 1 |
| protocol | 上述 DTO、严格 adapter、`ProtocolScoreCodes` 码表/上限；编码器始终输出 v5 字段集 |
| client `ProtocolViewMapper` | `ScoreResultView.capCode` 仅保留已知封顶码；`ScoreBreakdownView.yakuEntries`；`PaymentPlanView` 仅保留通过 `ProtocolPaymentPlanValidator` 全量校验的计划；`Capabilities.scoreDetailVersion` |
| presentation `SettlementDetailPresenter` | 役种行优先 `yakuEntries.displayName/han`；封顶行优先 `capCode` 本地化；支付行优先 `paymentPlan.components/total` 并在“基础支付分配”展示逐付款方金额；缺失/未知时逐级回退 |

数值权威性：`fu/han/basePoints/rawBasePoints/rawBasePointsExact/honbaAddition/riichiAddition/totalPoints` 始终来自数值字段；`paymentPlan` 只替代基础支付展示，不改变本场/立直/总得点，也不从码推导基本点。展示层仍以 `totalPoints` 为最终总得点。

**四种支付形态的跨字段不变量**（`ProtocolPaymentPlanValidator`）：

| kind | isDealer/isTsumo | components（role/count） | 金额（base = basePoints） | 冻结 paymentLines |
|---|---|---|---|---|
| `DEALER_TSUMO` | true / true | `NON_DEALER` / 3 | `round100(base×2)` 每家 | `自摸：闲家各付 X` |
| `NON_DEALER_TSUMO` | false / true | `DEALER` / 1 + `NON_DEALER` / 2 | `round100(base×2)` + `round100(base×1)` | `自摸：庄家付 A、闲家各付 B` |
| `DEALER_RON` | true / false | `DISCARDER` / 1 | `round100(base×6)` | `荣和：P<n>/放铳者 支付 X` |
| `NON_DEALER_RON` | false / false | `DISCARDER` / 1 | `round100(base×4)` | `荣和：P<n>/放铳者 支付 X` |

同时要求：

- `plan.total == Σ(amount × count)`（已由 `isStructurallyConsistent` 校验）。
- `totalPoints == plan.total + honbaAddition + riichiAddition`，且本场/立直不为负。
- `paymentLines` 恰好一行，金额与计划逐项相等；旧文本是权威回退源。
- 未知 `kind` 在协议层保留原值但客户端不消费；已知 kind 下未知 `payerRole`、重复角色、多余/缺失分组、人数或金额不符均降级。

### 12.4 兼容/回退矩阵

| 输入/场景 | 解码行为 | client view | presentation |
|---|---|---|---|
| 历史 v4 payload（无新字段） | 正常解码，`capCode=null`、`yakuEntries=[]`、`paymentPlan=null`、capability=0 | 旧字段完整 | 旧 `capReason`/`yakuLines`/`paymentLines` |
| `scoreDetailVersion=0` 或缺失 | 正常解码 | 结构化字段若存在仍按结构校验；不存在为旧视图 | 全部走旧文本 |
| `scoreDetailVersion` 未知更高值 | 正常解码（0..1000 内） | 不据此拒绝快照 | 字段缺失/不可解释时回退 |
| `capCode=null` | 正常 | null | `capReason` → `title` |
| `capCode` 未知码 | 保留原值 | 映射为 null | `capReason` → `title` |
| `yakuEntries` 缺失 | 空列表 | 空 | `yakuLines` |
| `yakuEntries` 与 `yakuLines` 数量/内容不一致 | 整组降级为空 | 空 | `yakuLines` |
| `yakuEntries.code` 未知 | 保留原值 | 保留 `displayName/han` | 使用 `displayName`，不丢弃结算 |
| `paymentPlan` 缺失/为空/总额不守恒 | null | null | `paymentLines` + 既有重算展示 |
| `paymentPlan` 的 kind 与 `isDealer`/`isTsumo` 或四形态角色/人数冲突 | 已知 kind 降级为 null | null | `paymentLines` |
| `paymentPlan` 金额与 `basePoints` 取整公式不符 | 已知 kind 降级为 null | null | `paymentLines` |
| `totalPoints != plan.total + honba + riichi` | 降级为 null | null | `paymentLines` |
| `paymentPlan` 与旧 `paymentLines` 金额/形态不一致 | 降级为 null | null | `paymentLines` |
| `paymentPlan.kind` 未知 | 保留原值（结构合法时） | null | `paymentLines` |
| 已知 kind 下 `payerRole` 未知/重复/分组缺失 | 降级为 null | null | `paymentLines` |
| 新字段错误 JSON 类型/越界/超限 | 抛 `JsonParseException`，载荷拒绝 | — | — |
| 新字段之外的未知字段 | 抛 `Unknown field`，载荷拒绝 | — | — |
| 全量快照 / 重连基线 | 同一条事件 DTO 解码路径 | `isFullSnapshot` 语义不变 | 结构化字段随快照保留 |
| 观战公开流 | engine `PublicProjectionFactory` 先按 `revealSettlementUra` 清洗里宝，再进协议 | 只读公开事件 | 复用同一 Presenter；无私有手牌/账本/千术字段 |
| 旧 businessVersion=1 握手 | 服务端/客户端 `VERSION_MISMATCH` | 连接失败 | 明确提示升级，不静默降级 |

### 12.5 修改文件

生产代码：

- `protocol/.../api/ProtocolScoreCodes.kt`（上限、支付/Payer 判定、v5 注释）
- `protocol/.../api/ProtocolModels.kt`（`ProtocolYakuEntry`、`ProtocolPaymentComponent`、`ProtocolPaymentPlan`、三处 DTO 新字段、不可变副本）
- `protocol/.../api/ProtocolScoreAdapters.kt`（新增严格元素 adapter）
- `protocol/.../api/ProtocolPaymentPlanValidator.kt`（新增四形态/角色人数/取整金额/总得点/paymentLines 跨字段校验）
- `protocol/.../api/ProtocolPlayerSnapshotAdapters.kt`（capCode / scoreDetailVersion）
- `protocol/.../api/ApiMessage.kt`（breakdown adapter 一致性/降级、adapter 注册）
- `protocol/.../server/api/ApiErrors.kt`（`PROTOCOL_VERSION=5`）
- `protocol/.../tcp/TcpWire.kt`（`BUSINESS_VERSION=2`）
- `server/.../mapping/EngineProtocolMapper.kt`（结构化映射 + capability）
- `server/.../tcp/TcpRequestRouter.kt`、`server/.../inprocess/InProcessBinding.kt`（capability=1）
- `client/.../api/ViewModels.kt`（`YakuEntryView`、`PaymentComponentView`、`PaymentPlanView`、`ScorePaymentCodesView`、视图字段）
- `client/.../api/ClientModels.kt`（`Capabilities.scoreDetailVersion`）
- `client/.../mapping/ProtocolViewMapper.kt`（双向结构化映射与安全过滤）
- `client/.../transport/tcp/TcpClientBinding.kt`（保留 `VERSION_MISMATCH` 协议异常）
- `presentation/.../common/SettlementDetailPresenter.kt`（结构化优先 + 回退）
- `engine/.../rules/ScoreCodes.kt`、`WinningScoreEvaluator.kt`（注释同步 v5）

测试：

- `protocol/.../ScoreWireBoundaryTest.kt`（历史 v4 fixture + v5 门槛 + 未知字段）
- `protocol/.../StructuredScoreCodecTest.kt`（严格类型/上限/未知码/不一致降级/不可变/全量快照；含 kind/角色/人数/取整金额/totalPoints/paymentLines 反例与四形态正例）
- `server/.../ScoreCodeContractTest.kt`（v5 字段集、码表、capability）
- `server/.../EngineProtocolMapperTest.kt`（结构化字段纳入全字段比较）
- `client/.../ScoreStructuredViewContractTest.kt`（结构化映射/未知码/不可变/capability；含绕过 adapter 的跨字段反例）
- `client/.../ScoreLegacyViewContractTest.kt`（旧字段回退）
- `client/.../TcpBusinessVersionHandshakeTest.kt`（旧 WELCOME 拒绝）
- `presentation/.../StructuredSettlementDetailPresenterTest.kt`（结构化优先/回退/役满支付）
- `presentation/.../StructuredScorePipelineAcceptanceTest.kt`（engine→protocol→client→presentation；玩家 + 观战隐私）
- `server/.../TcpTableServerHandshakeTest.kt`（旧 businessVersion=1 → VERSION_MISMATCH；WELCOME=2）
- 既有测试中的 HELLO/WELCOME 字面量统一改为 `TcpWire.BUSINESS_VERSION`；`tools/architecture` 探针同步。

### 12.6 验收命令与结果

全部串行执行，未并发 Gradle；最终一次全仓强制重跑：

| 命令 | 退出码 | 结果 |
|---|---:|---|
| `.\gradlew.bat test --rerun-tasks --console=plain` | **0** | BUILD SUCCESSFUL in 1m17s；66 actionable tasks 全部 executed |
| `.\gradlew.bat :lwjgl3:compileKotlin :android:assembleDebug --console=plain` | **0** | BUILD SUCCESSFUL；`android-debug.apk` 34,542,122 bytes（既有 D8/R8 Kotlin metadata 警告，构建成功） |
| `.\gradlew.bat :server:test --tests "...TcpTableServerHandshakeTest" --rerun-tasks` | **0** | 11/11，真实 TCP v1/未来版本拒绝、v2 接受 |
| `.\gradlew.bat :protocol:test --tests "...ScoreWireBoundaryTest" --tests "...StructuredScoreCodecTest" --rerun-tasks` | **0** | 4/4 + 12/12（含支付形态/反例/普通分/各封顶/役满/本场/立直） |
| `.\gradlew.bat :presentation:test --tests "...StructuredScorePipelineAcceptanceTest" --tests "...StructuredSettlementDetailPresenterTest" --rerun-tasks` | **0** | 2/2 + 4/4 |
| `.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --rerun-tasks` | **0** | 108/108，七模块依赖/分层/权威边界 |
| `.\gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :client:test :server:test --rerun-tasks` | **0** | 22/22 TCP 独立探针 |

JUnit XML 计数（强制重跑，无 UP-TO-DATE；工作区同时包含已整合的 D-06 engine 变更）：engine 515、protocol 190、server 337、client 230、presentation 346，合计 **1,618 tests，0 failures / 0 errors / 0 skipped**。相对上一轮 1,605：D-06 整合 +9（engine `GameStateFieldChecklistTest` 3→12），D-07 本轮返修 +4（protocol 支付不变量 3 项、client 跨字段反例 1 项）。

未执行/未声称通过：桌面真实 GL 冒烟、Android 真机或模拟器网络实测、跨物理网络 T4 验收、TLS/公网环境。D-05 已交付的观战 GL 冒烟证据仍属于 D-05 自身记录，本次未重跑。
