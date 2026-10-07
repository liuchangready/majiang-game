# R4B2PREVIEWFIX 私有预览首轮验收返修交付报告

**开发者**: Antigravity (Anti)  
**阶段**: R4B2 私有玩家展示预览端到端返修  
**日期**: 2026-10-06  

---

## 1. 缺陷修复概览

对照 `TASK-ANTI-R4B2PREVIEWFIX.md`、`FEEDBACK-R4B2PREVIEWFIX.md` 及首轮真实失败证据，完成以下两项生产阻断缺陷修复：

1. **Client 测试真实属性编译错误修复**：
   - 在 [`client/src/test/kotlin/com/majiang/client/api/ProtocolViewPreviewMapperTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ProtocolViewPreviewMapperTest.kt#L66) 第 66 行，将 `viewOpt.discardTile.isRed` 修正为真实属性 `viewOpt.discardTile.isRedFive`，保留赤五完整断言。
   - 不增加任何兼容 alias 或拓展属性。

2. **严格 Codec 与构造结构防伪校验（彻底解决 `strictCodecRejectsMeldCompositionContradictingUnchangedAuthorityAction`）**：
   - 在 `client`、`engine`、`protocol` 三层的 `MeldPreviewOption` / `ProtocolMeldPreviewOption` / `MeldPreviewOptionView` 构造函数中增加严格组合自洽校验：
     - 限制 `action` 只能为 `DeclareChi`、`DeclarePon`、`DeclareKan`，严禁 `Pass`、`DiscardTile`、千术等动作伪装鸣牌选项；
     - 严格校验 `tiles` 与 `handTiles` 物理牌去重（物理牌守恒，拒绝重复牌）；
     - `tiles` 必须包含全部 `handTiles`；
     - 依照真实 `MeldChoiceProvider` 契约，对 5 类副露（CHI / PON / OPEN_KAN / CLOSED_KAN / ADDED_KAN）施加完备的结构一致性核对；
   - 在传输层 `ApiMessage.kt` 中，`ProtocolMeldPreviewOptionAdapter` 和 `ProtocolRiichiPreviewOptionAdapter` 反序列化捕获 `IllegalArgumentException` 并转换为 `JsonParseException`，确保篡改展示字段（如 `type = PON`、`handTiles = []` 或以 `calledTile` 替换本人手牌）时严格反序列化失败。

---

## 2. 最终结构约束规则

在 `engine/access/PlayerPresentationPreview.kt`、`protocol/transport/api/ProtocolPreviewModels.kt` 与 `client/api/PreviewModels.kt` 保持完全一致的物理结构约束：

### 2.1 鸣牌类型约束 (MeldPreviewOption / ProtocolMeldPreviewOption / MeldPreviewOptionView)
1. **通用约束**：
   - `optionId.isNotBlank()`；
   - `action` 仅限 `DeclareChi` / `DeclarePon` / `DeclareKan`；
   - `tiles` 无重复物理牌；
   - `handTiles` 无重复物理牌；
   - `tiles.containsAll(handTiles)`。
2. **CHI**：
   - `action is DeclareChi` 且 `action.meld.type == CHI`；
   - `tiles.size == 3` 且 `tiles == action.meld.tiles`（保留顺序与赤五）；
   - `calledTile != null` 且 `calledTile == action.meld.calledTile`；
   - `fromPlayer != null && fromPlayer in 0..3` 且 `fromPlayer == action.meld.fromPlayer` 且 `fromPlayer != action.player`；
   - `calledTile in tiles` 且 `calledTile !in handTiles`；
   - `handTiles.size == 2` 且 `handTiles == tiles.filter { it != calledTile }`；
   - `addedTile == null`。
3. **PON**：
   - `action is DeclarePon` 且 `action.tiles.size == 2`；
   - `handTiles.size == 2` 且 `handTiles == action.tiles`；
   - `calledTile != null`；
   - `fromPlayer != null && fromPlayer in 0..3` 且 `fromPlayer != action.player`；
   - `calledTile !in handTiles`；
   - `tiles.size == 3` 且 `tiles == handTiles + calledTile`（本人牌后追加被鸣牌）；
   - `tiles` 内全部牌同牌种 (`sameType` / `typeIndex` 一致)；
   - `addedTile == null`。
4. **OPEN_KAN**：
   - `action is DeclareKan` 且 `action.kanType == OPEN_KAN`；
   - `action.tiles.size == 3`；
   - `handTiles.size == 3` 且 `handTiles == action.tiles`；
   - `calledTile != null`；
   - `fromPlayer != null && fromPlayer in 0..3` 且 `fromPlayer != action.player`；
   - `calledTile !in handTiles`；
   - `tiles.size == 4` 且 `tiles == handTiles + calledTile`（本人牌后追加被鸣牌）；
   - `tiles` 内全部牌同牌种；
   - `addedTile == null`。
5. **CLOSED_KAN**：
   - `action is DeclareKan` 且 `action.kanType == CLOSED_KAN`；
   - `action.tiles.size == 4`；
   - `handTiles.size == 4` 且 `handTiles == action.tiles`；
   - `calledTile == null`、`fromPlayer == null`、`addedTile == null`；
   - `tiles.size == 4` 且 `tiles == handTiles`；
   - `tiles` 内全部牌同牌种。
6. **ADDED_KAN**（依 `FEEDBACK-R4B2PREVIEWFIX.md` 收紧为真实 4 张契约）：
   - `action is DeclareKan` 且 `action.kanType == ADDED_KAN`；
   - `action.tiles.size == 4`（遵照真实 `MeldChoiceProvider` 契约，不设 1 张兼容分支）；
   - `action.tiles.containsAll(tiles) && tiles.containsAll(action.tiles)`；
   - `addedTile != null`；
   - `handTiles.size == 1` 且 `handTiles[0] == addedTile`；
   - `calledTile != null`；
   - `fromPlayer != null && fromPlayer in 0..3` 且 `fromPlayer != action.player`；
   - `tiles.size == 4` 且 `tiles.last() == addedTile`（原碰组后追加加杠牌）；
   - `addedTile in tiles`、`calledTile in tiles`、`addedTile != calledTile`、`calledTile !in handTiles`；
   - `tiles` 内全部牌同牌种。

### 2.2 立直类型约束 (RiichiPreviewOption / ProtocolRiichiPreviewOption / RiichiPreviewOptionView)
- `optionId.isNotBlank()`；
- `discardTile == action.discardTile`；
- `waits.distinct().size == waits.size`（待牌无重复）。

---

## 3. 精确修改文件清单

1. [`client/src/main/kotlin/com/majiang/client/api/PreviewModels.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/PreviewModels.kt)：
   - `RiichiPreviewOptionView.init` 增加 `discardTile` 与 `waits` 约束；
   - `MeldPreviewOptionView.init` 增加动作类型、去重、5 类副露结构自洽约束。
2. [`engine/src/main/kotlin/com/majiang/access/PlayerPresentationPreview.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/PlayerPresentationPreview.kt)：
   - `RiichiPreviewOption.init` 增加 `discardTile` 与 `waits` 约束；
   - `MeldPreviewOption.init` 增加动作类型、去重、5 类副露结构自洽约束。
3. [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolPreviewModels.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ProtocolPreviewModels.kt)：
   - `ProtocolRiichiPreviewOption.init` 增加 `discardTile` 与 `waits` 约束；
   - `ProtocolMeldPreviewOption.init` 增加动作类型、去重、5 类副露结构自洽约束。
4. [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)：
   - `ProtocolRiichiPreviewOptionAdapter` 捕获 `IllegalArgumentException` 包装为 `JsonParseException`；
   - `ProtocolMeldPreviewOptionAdapter` 捕获 `IllegalArgumentException` 包装为 `JsonParseException`。
5. [`client/src/test/kotlin/com/majiang/client/api/ProtocolViewPreviewMapperTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ProtocolViewPreviewMapperTest.kt)：
   - 修正第 66 行 `isRed` -> `isRedFive`；
   - 新增 `MeldPreviewOptionView` 与 `RiichiPreviewOptionView` 构造函数反例校验单元测试（含 ADDED_KAN 4 张契约测试）。
6. [`protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPreviewModelsCodecTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPreviewModelsCodecTest.kt)：
   - 新增 `ProtocolMeldPreviewOption` 构造函数反例校验单元测试；
   - 新增快照反序列化线上传输篡改（修改 type=PON、清空 handTiles、以 calledTile 替换 handTile[0]、ADDED_KAN 牌数错误等）拒绝测试。
7. [`engine/src/test/kotlin/com/majiang/session/PlayerPresentationPreviewProviderTest.kt`](file:///D:/majiang/engine/src/test/kotlin/com/majiang/session/PlayerPresentationPreviewProviderTest.kt)：
   - 新增 `MeldPreviewOption` 与 `RiichiPreviewOption` 构造函数反例校验单元测试（含 ADDED_KAN 4 张契约测试）。

---

## 4. 未完成项

- **无**。所有权范围内两项阻断缺陷与反馈项均已全部修复。

---

## 5. 测试执行状态说明

- **Anti 明确未运行任何测试、构建、Gradle 命令或终端命令**。
- 所有改动严格通过内置文件编辑工具落盘，待由 Codex 串行执行全量测试与架构验收。
