# R5 COMMON：渲染资源、物理牌、计时与结算

只在 Codex 接受整个 R4 后派发。先读 CONTRACT.md、TASK-ANTI-R5.md、R5-R6-PREPARATION-AUDIT.md、R5-UI-INTERFACE-PREPARATION.md 和 R6-TEST-PRESERVATION-INVENTORY.md。用户要求 Anti 开发、Codex 独立验收；旧入口无需兼容。

独占 `presentation/src/main/kotlin/com/majiang/render/**`、`presentation/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt`、`presentation/src/main/kotlin/com/majiang/screen/{SettlementDetailPresenter,SettlementDetailRenderer,FinalResultRenderer,TenpaiPanelRenderer}.kt`，以及这些类对应测试。将其实际迁入 V4 `presentation.common` 及子包，输入只用 `client.api` 自有视图（物理牌 ID、红五、副露、符番役、供托/历史/待牌、权威时钟），不借 engine/protocol/server 类型或旧 GameClient/SessionAccess。保留像素布局、方位副露、红五实体、理牌、动画门控、完整结算与听牌断言；不能以旧测试引用不存在为由删除其有效断言。

`SkinManager` 拥有并释放其注册的 `TileSkin`；GameScreen 借用皮肤不得dispose。明确 `GhostHandTexture` 全局缓存的释放时机，与menu/root路提供的 `UiAssets` 所有权一致。若需要 `UiAssets` API 新成员，先报告给唯一拥有者menu/root，不修改它。不要修改 `GameScreen`、`MenuScreen`、`MahjongGame`、client.api、平台/Gradle、Codex reviewer。

只用内置文件工具，不运行shell/build/test/其它agent。保留所有工作区改动，不提交。交付 `delivery-ANTI-R5COMMON.md` 列实际文件、有效旧断言迁移位置、未完成项、测试未运行声明；Codex将串行构建/检视/返修。
