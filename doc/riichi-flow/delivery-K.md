# 交付文档 - Task K: 立直后暗杠合法性校验器 (RiichiKanValidator)

## 1. 概述与任务定位

- **任务编号**：Task K
- **开发代号**：Antigravity
- **协作模式**：Codex 负责设计、测试编写、构建与代码审查；Antigravity 负责生产实现。
- **任务目标**：根据 [WRC 2022 §8.9.1](https://www.worldriichi.org/s/WRC_Rules_2022_20220708_site.pdf) 规范，新增纯函数校验对象 `com.majiang.rules.RiichiKanValidator`，提供严格的立直后暗杠判定能力。

---

## 2. 修改与新增文件清单

- **新增文件**：
  - `core/src/main/kotlin/com/majiang/rules/RiichiKanValidator.kt`
- **修改文件**：无（严格遵循并行任务边界，未修改 `HandDecomposer.kt`、构建脚本、测试套件以及 Task R 所属文件）。

---

## 3. 核心契约与算法实现细节

`RiichiKanValidator.canDeclare(hand: PlayerHand, kanTiles: List<Tile>): Boolean` 实现了以下完整校验逻辑：

1. **门清与摸牌判定**：
   - 检查 `hand.isClosed`（确保无吃、碰、大明杠、加杠等明副露；已有暗杠维持门清）。
   - 检查 `hand.drawnTile != null`。
2. **物理持牌与副本互异性**：
   - 校验 `kanTiles.size == 4` 且所有牌 `typeIndex` 一致（同种牌）。
   - 校验 `kanTiles.map { it.copyIndex }.distinct().size == 4`（确保 4 张互异物理副本，即 `copyIndex 0..3`）。
   - 校验 `hand.drawnTile in kanTiles`（本巡摸牌必须参与本次暗杠；手内原持四张而摸入别种牌时禁止暗杠）。
   - 其余三张牌从 `hand.closedTiles` 中精确比对并核减（`closedRemaining.remove(tile)`），核对失败即拒绝，防止借用他家牌或不存在的物理副本。
3. **手牌规模与已杠支持**：
   - 校验手牌满足 `hand.melds.size in 0..3` 且 `hand.closedTiles.size == 13 - hand.melds.size * 3`，全面支持已有暗杠后的再次暗杠。
4. **排除第 5 张牌的听牌计算**：
   - 原手牌为摸牌前的 `closedTiles`，已有副露为 `hand.melds`。
   - 杠后手牌为核减原刻子后的 `closedTiles`，副露增加本次待成立的 `CLOSED_KAN`。
   - 两边独立枚举 34 种牌计算听牌，严格扣减手牌与副露中已占用的同种牌计数；若已达 4 张则跳过候选（防止将第 5 张牌误算为待牌）。
   - 校验 `waitsOrig.isNotEmpty()`（必须处于听牌）且 `waitsOrig == waitsPost`（杠前后待牌集合严格一致）。
5. **手牌结构与分解规范化契约**：
   - 针对 `waitsOrig` 中的每张待牌，枚举 `origClosed + winTile` 与 `postClosed + winTile` 的全部和牌分解。
   - 拒绝所有非标准形（如七对子 `CHIITOITSU`、国士无双 `KOKUSHI` 等特殊形）。
   - 原手牌在任一待牌下的**每一组**和牌分解都必须包含目标牌的完整刻子（`isTriple && typeIndices == targetType`）；若存在顺子或雀头等任何替代解释，立即拒绝。
   - 对移除该刻子后的原手牌分解面子列表与杠后分解面子列表进行规范化（面子内索引排序、面子列表全序排序），构建 `NormalizedDecomposition`。
   - 验证两边的规范化分解集合与多重集严格相等（`normOrigDecomps.sorted() == normPostDecomps.sorted()`）。
6. **纯函数无副作用**：
   - 仅进行只读计算与校验，不修改传入的 `hand`、`kanTiles` 或全局对局状态。

---

## 4. 未验证项说明 (Delegated to Codex)

依据无头桥接协议要求，当前会话未执行终端命令、未修改 Gradle、未执行测试：
1. **测试执行**：需由 Codex 运行 `./gradlew test` 或对应的 `RiichiKanValidatorTest` 独立验收套件进行单元验证。
2. **Task R 联调**：需与 Task R 产出的 `LegalActionProvider` 及 `GameStateMachine` 进行整机联调，验证摸切自动流转与立直暗杠暂停流程。
3. **平台编译构建**：需由 Codex 验证桌面端 (`:lwjgl3`) 与 Android 端 (`:android`) 的全工程编译。
