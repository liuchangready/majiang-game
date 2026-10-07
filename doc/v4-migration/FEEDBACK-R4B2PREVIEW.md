# Codex 独立验证准备

当前reviewer在编写真实TableAuthority -> server mapper -> codec -> client mapper链路探针，尚未运行：待牌/摸牌前与弃牌后、红五/普通五立直精确optionId与振听、chi/pon/三类kan全字段、自动控制下候选约束、三层immutable/deepCopy、wire篡改roundId/decisionId/controlEpoch/optionId拒绝。

请优先按任务完成并落盘三层DTO与snapshot字段，便于reviewer按实际类型接线；不需为此暂停实现。完整预览携带的optionId必须存在于同一snapshot的decision.options，不能通过仅验证非空字符串接受错配。decoder须比较预览元数据与snapshot.decision，必要时检查具体action/物理tile关系，不能让外来预览诱导UI提交另一个动作。

此为提前告知的验收边界，不是编译失败报告；最终仍需完成端到端与模块测试。不要改reviewer文件。
# 14:07 初始代码只读反馈（未运行编译，仍由你完成）

## 新模块测试装配提醒（源码已读，尚未运行）

`ProtocolPreviewModelsCodecTest` 的篡改用例当前使用String.replace，会把snapshot/decision/preview中的同名roundId、decisionId、controlEpoch同时替换成一致的新值，未制造跨对象不一致。应解析JsonObject，仅改presentationPreview子对象的指定字段后再解码并断言拒绝。

同一用例 `val rogueSnap = snap.copy(presentationPreview=rogue...)` 与 mismatchedSnap 位于assertThrows外，而最新ProtocolPlayerSnapshot构造器已立即校验optionId/action，会在被测decode之前失败。应从合法snapshot生成JSON后，只改preview子节点的optionId/action。保留“不存在ID”和“存在ID但不同物理动作”两类断言；不要关掉构造校验或删除测试。物理牌构造应使用相互一致的tileId/suit/rank，避免fixture先因无关格式错误失败。

用户最新要求完成整个R4后停止；本任务仍正常完成开发/交付，不推进R5/R6。

## 模块fixture实际Tile接口阻断

只读复核新增 `PlayerPresentationPreviewProviderTest.kt`：领域 `Tile(suit,rank,copyIndex)` 第三参数是0..3副本序号，不是0..135全局ID；当前大量4..99值会在fixture构造时失败（例如75行）。110/111/317行的 `isRed` named参数不存在，151/152/156/160行的 `.isRed` 属性也不存在，实际应读取 `isRedFive`，赤五由rank=5且copyIndex=3表示。请按现有Tile.kt修正所有fixture，保留赤五/普通五物理区分与原玩法断言。

`ProtocolPreviewModelsCodecTest.kt`约40行同样用了不存在的isRed constructor参数。ProtocolTile(tileId,suit,rank)要求三者一致：如ID4是MAN2，ID51是SOU4，ID10/11/12仍属MAN，不能分别写成MAN4/MAN5/PIN。优先使用真实 `ProtocolTile.fromTileId` 或按 typeIndex*4+copyIndex 构造；赤五万ID19。全面复核新测试中的领域和协议物理牌，不能把期待的异常变成fixture参数错误的误通过。

ProviderTest约177行 `DeclarePon(0,listOf(ponTile1,ponTile2,calledTile))` 错把被碰牌放入动作参数；真实DeclarePon只收本人两张，MeldChoiceProvider对size!=2会返回null，导致ponOptions.size==1的断言无法成立。308行actionMismatchedPreview中的 `Tile(PIN,9,99)` 也需换成合法但与真实动作不同的物理副本，避免assertThrows外先失败。

随后新增的 `server/.../EngineProtocolPreviewMapperTest.kt` 和 `client/.../ProtocolViewPreviewMapperTest.kt` 也复制了同一类错误，必须一并修正：server领域Tile的50/51/101等副本值及isRed参数/属性无效；client ProtocolTile isRed构造参数无效、ID与suit/rank不匹配、TileView只有isRedFive而没有isRed。protocol现有isRed只读派生属性存在，不代表constructor接受该参数，也不能据此给domain/client新增兼容别名。server碰牌动作传本人2张、大明杠本人3张、暗杠4张，按真实GameAction和Provider语义装配。对全局ID断言同步替换成真实物理ID，保留全部赤五/等值断言。

已读新 `PlayerPresentationPreview.kt`：`GameAction.deepCopy()` 是 `com.majiang.application.deepCopy` 扩展，目前该文件未导入，请交付前核对。`MeldPreviewOption.action` 直接保留构造输入，而 `DeclareChi.meld.tiles`、`DeclarePon.tiles`、`DeclareKan.tiles` 是原领域可变别名；仅封装另列的 tiles/handTiles 不足以保证新增DTO深层不可变。需要在新增DTO范围内防御复制/不可变包装该 action 及其嵌套集合（不跨任务改原GameAction），同时保留准确值和完整动作匹配。snapshot 的 preview roundId 也应与 snapshot 及 decision.roundId 一致，不能在 snapshot.roundId=null 时放行旧局预览。以上是正在开发版本的提前提示，不代表最终测试结论。
