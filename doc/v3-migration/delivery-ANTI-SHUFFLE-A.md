# 任务 ANTI-SHUFFLE-A 交付报告：洗牌需求模型与牌山生成器

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-SHUFFLE-A 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、构建与集成验收；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不调用终端、命令行或其它代理）
- **契约依据**：
  - [`doc/v3-migration/SHUFFLE-CONTRACT.md`](file:///D:/majiang/doc/v3-migration/SHUFFLE-CONTRACT.md)
  - [`STACKING_ALGORITHM.md`](file:///D:/majiang/STACKING_ALGORITHM.md)
  - [`TILE_OVERLAP_MATRIX_ALGORITHM.md`](file:///D:/majiang/TILE_OVERLAP_MATRIX_ALGORITHM.md)
  - [`doc/GAME_ARCHITECTURE_V3.md`](file:///D:/majiang/doc/GAME_ARCHITECTURE_V3.md)

---

## 1. 唯一写入范围与交付清单

严格遵循任务所有权契约，所有新建类与算法仅位于 `engine/src/main/kotlin/com/majiang/stacking/**` 与本交付报告，未修改任何测试源码、Gradle 构建脚本、Coordinator 或其他任务所拥有的模块文件：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| [`engine/src/main/kotlin/com/majiang/stacking/ShuffleTarget.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleTarget.kt) | 新建 | 定义 6 种洗牌做牌目标枚举：`CHINITSU_MAN`, `CHINITSU_SOU`, `CHINITSU_PIN`, `DAISANGEN`, `KOKUSHI`, `DAISUUSHII` |
| [`engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt) | 新建 | 洗牌全局与单局配置数据类（默认 `enabled=false`, 准备时长 10000ms, 摸牌轮数 8 轮, 做牌强度 0.75, 干扰敏感度 0.65, `aiMaySpend=false`），含完整参数强校验与深拷贝 |
| [`engine/src/main/kotlin/com/majiang/stacking/ShuffleSelection.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleSelection.kt) | 新建 | 玩家洗牌千术目标选择方案（起手牌目标 `initialTargets` 40 点，后续摸牌目标 `drawTargets` 20 点，同时使用 60 点），提供 `cost()`、`isActive` 与不可变防御性 `deepCopy()` |
| [`engine/src/main/kotlin/com/majiang/stacking/ShufflePlanRequest.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShufflePlanRequest.kt) | 新建 | 客户端提交方案请求 DTO（含 `requestId`, `roundId`, `controlEpoch`, `selection` 及深拷贝与强校验） |
| [`engine/src/main/kotlin/com/majiang/stacking/ShufflePreparationStatus.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShufflePreparationStatus.kt) | 新建 | 准备阶段状态枚举：`SELECTING`（倒计时选择中）、`GENERATING`（截止并生成牌山发牌） |
| [`engine/src/main/kotlin/com/majiang/stacking/ShufflePreparationView.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShufflePreparationView.kt) | 新建 | 玩家私有准备阶段快照视图（权威时钟、截止时间、确认状态、当前方案、预留点数与可用余额及防御性深拷贝） |
| [`engine/src/main/kotlin/com/majiang/stacking/SeatShufflePlan.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/SeatShufflePlan.kt) | 新建 | 席位与方案绑定结构（`seat in 0..3` 校验与深拷贝） |
| [`engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt) | 新建 | 牌山物理槽位划分计算器：起手槽位 `initial(dealerSeat, seat)`（庄家 14 张、子家 13 张，区间 0..52）与后续摸牌槽位 `draws(dealerSeat, seat, count)`（区间 53..121，严格排除 122..135 王牌区） |
| [`engine/src/main/kotlin/com/majiang/stacking/ShuffleTargets.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleTargets.kt) | 新建 | 34 维需求特征空间运算工具：`demand`（L1 归一化）、`overlap`（余弦相似度 [0.0, 1.0]）、`combined`（等权归一化）与 `cosineSimilarity` |
| [`engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt) | 新建 | 确定性牌山洗牌与自适应千术生成器（136 张物理守恒、种子可重现、空方案/无千术不变性、席位公平性、王牌区绝对隔离、定向重叠度衰减、非贪婪智能牌型启发式与有界采样优化） |
| [`doc/v3-migration/delivery-ANTI-SHUFFLE-A.md`](file:///D:/majiang/doc/v3-migration/delivery-ANTI-SHUFFLE-A.md) | 新增 | 本交付报告 |

---

## 2. 算法设计与技术选型

### 2.1 物理守恒与确定性重现
- **136 张物理闭环**：基础牌池通过 [`TilePool.buildStandardSet()`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/model/TilePool.kt#L197-L205) 构建，完整保留 34 种牌、每种 4 张副本（`copyIndex` 0..3）及关东三张制赤宝牌（`rank==5 && copyIndex==3`）的物理身份。
- **确定性重现**：由传入的 `seed` 构建 Kotlin 独立随机发生器 `Random(seed)`，相同种子与输入必然产生完全一致的 136 张牌物理排列。
- **零千术不变律**：当 `config.enabled == false` 或所有方案均未选择目标（`activePlans.isEmpty()`）时，直接返回基础洗牌结果，绝不产生多余的随机数消费，与完全不传方案（`emptyList()`）的牌山完全一致。
- **入参顺序无关性**：在执行任何做牌前，对 `plans` 按 `seat` 升序规范化（`canonicalPlans = plans.sortedBy { it.seat }`），消除输入列表顺序对随机序列的影响。

### 2.2 王牌隔离与槽位所有权
- **王牌绝对保护**：王牌区固定为 `122..135`（14 张牌）。在 [`ShuffleSlots`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt) 中严格限定：起手槽位处于 `0..52`，摸牌槽位仅取 `53..121`；在 [`ShuffleWallGenerator`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt) 的优化中，候选供体（donor）严格限制在 `0..121`，王牌区既不可被做牌瞄准，也绝不可作为换牌供体，杜绝操纵宝牌或岭上牌的作弊漏洞。
- **槽位锁定与已提交不可侵犯**：各席位只能优化归属于本人的槽位。每轮优化完成的槽位计入 `committedSlots`：
  - 起手发牌阶段（0..52）完成后，全部 53 个起手槽位被锁定；
  - 后续摸牌阶段（53..121）绝对不允许从手牌区抽调牌张作为摸牌流的供体；
  - 任何已优化提交的槽位不可作为他人或后续轮次的换牌供体。
- **席位公平轮转**：在每一轮（起手 14 轮、摸牌至多 18 轮）内部，使用种子随机打乱当前轮次有权行动的席位序列（`participatingSeats.shuffled(rng)`），彻底消除固定座位号（0 -> 1 -> 2 -> 3）的特权倾轧。

### 2.3 34 维需求向量与定向干扰模型
- **特征维度划分**：
  - 0..8: 万子 1..9 万
  - 9..17: 索子 1..9 索
  - 18..26: 筒子 1..9 筒
  - 27..30: 风牌 東, 南, 西, 北
  - 31..33: 三元牌 白, 發, 中
- **余弦重叠度**：
  $$\text{Overlap}(A, B) = \frac{\vec{D}_A \cdot \vec{D}_B}{\|\vec{D}_A\|_2 \|\vec{D}_B\|_2} \in [0.0, 1.0]$$
  - 异花色（如万 vs 索）：重叠度严格为 0.0，双方完全不干扰；
  - 同花色（如万 vs 万）：重叠度为 1.0，剧烈冲突；
  - 国士无双 vs 清一色（万）：仅在 1万 与 9万 重叠，重叠度 $\approx 0.185$。
- **定向衰减与独立阶段**：
  起手牌与后续摸牌流独立分别计算衰减，起手目标不覆盖摸牌目标：
  $$\text{Interference}_i = \sum_{j \neq i} S_{\text{base}} \times \text{Overlap}(i, j)$$
  $$S'_i = S_{\text{base}} \times \frac{1}{1 + \lambda \times \text{Interference}_i} \quad (\lambda = \text{config.interference})$$
  若无人争抢，有效强度 100% 保留，不存在人为全局惩罚。

### 2.4 非贪婪智能牌型启发式与有界采样优化
拒绝简单粗暴地见同花色就抓，针对各番型特点设计专用效用函数：
- **国士无双 (`KOKUSHI`)**：
  - 极力优先补齐当前手牌中缺失的孤张幺九牌（效用 10.0）；
  - 当手牌尚无任何幺九对子时，第二张同种幺九牌作为雀头备选给予中等加成（5.0），已有雀头后降至 1.0；
  - 出现第 3 张同种幺九牌时效用骤降至 0.2，第 4 张评分为 0.0，严格避免同种幺九成刻成杠破坏国士形。
- **大三元 (`DAISANGEN`) / 大四喜 (`DAISUUSHII`)**：
  - 优先在对应字牌（白发中 / 东南西北）上成刻：首张 6.0，成对 8.0，成刻 10.0；
  - 一旦成刻，该字牌边际效用骤降至 1.0，引导算法去换取其它未成刻的目标字牌。
- **清一色 (`CHINITSU_MAN/SOU/PIN`)**：
  - 基础花色张数（4.0）+ 搭子临近度（两面相邻 +2.0，嵌张/边张 +1.0）+ 雀头/成刻（+2.5 / +3.5）+ 赤宝牌加成（+0.5），优先构造搭子和顺子而非散乱中张。
- **有界局部优化与随机混合采样**：
  - 仅扫描主牌山活牌（0..121）未锁定槽位；
  - 优先在非目标牌槽位中寻找正向收益 $\Delta > 0$ 的供体；若借用他人未提交槽位，必须验证对方对该牌效用为 0；
  - 以 $\Delta^2$ 为权重进行有界轮盘赌随机采样，使得高收益目标获得极大概率提升，同时保留适度随机性，杜绝死板硬编码的天和模板。

---

## 3. 契约符合性核验

| 契约条款 | 规范要求 | 落地状态 |
|---|---|---|
| 公开签名精确性 | 严格对齐 `SHUFFLE-CONTRACT.md` 第 13..49 行类型与函数签名 | 100% 一致 |
| 异步与外部依赖 | 纯 Kotlin/JVM，不引入非 Android minSdk 21 脱糖支持的库，无外部网络/协程依赖 | 严格符合 |
| 136 物理守恒 | 基于 TilePool，原地槽位对调，赤宝牌 copyIndex 完好保留 | 严格符合 |
| 王牌区隔离 | 122..135 绝不作为目标，绝不作为供体 | 严格符合 |
| 零出千不变 | 空方案或未启用与无方案产生完全相同牌山 | 严格符合 |
| 输入顺序无关 | 按席位升序规范化输入 plans | 严格符合 |
| 席位公平性 | 每轮内部 seeded 打乱行动顺序 | 严格符合 |
| 阶段独立性 | 起手牌与摸牌流独立计算画像、重叠与衰减 | 严格符合 |
| 启发式做牌 | 国士防堆叠4张、大三元/大四喜促成刻、清一色看搭子 | 严格符合 |
| 参数合法性校验 | 校验 seat 0..3 唯一、plannedDraws 0..18、strength/interference 有穷非负等 | 严格符合 |
| 范围边界控制 | 仅实现 `engine/.../stacking/**` 与本报告，不改动测试或其它模块 | 严格符合 |

*(注：根据任务契约要求，本报告仅记录 Antigravity 生产代码交付与算法设计规范，不宣称已执行任何单元测试或构建指令，测试与最终集成验证交由 Codex 负责。)*
