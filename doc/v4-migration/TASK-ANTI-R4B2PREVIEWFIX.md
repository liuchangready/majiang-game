# R4B2 私有预览首轮验收返修

Codex已确认PREVIEW正常交付并退出；本任务明确授权继续原会话。开发只使用文件工具，不shell/build/提交/改设置/再委派。用户要求完成整个R4后暂停；本批不进入观战WIRE、R5或R6。PUBLIC已独立验证，不改projection范围。

## 真实失败证据

`build/v4-migration/R4B2-FIRST-modules.log`：engine367/protocol110/server105全部通过，client测试编译失败 `ProtocolViewPreviewMapperTest.kt:66:40 Unresolved reference isRed`。真实TileView属性为isRedFive，不新增兼容alias。

`R4B2-FIRST-review.log` 独立133项，130通过3失败，零跳过。其中2项是Codex自有测试装配需适配，Codex负责；你不修改reviewer。真实生产阻断 `V4PreviewAcceptanceTest.strictCodecRejectsMeldCompositionContradictingUnchangedAuthorityAction`：来自真实chi权威快照，optionId/action保持不变，只改preview.type= PON、handTiles=[]、或以calledTile替换一张本人handTile，三种均被decode成功接受。表明当前只绑定optionId/action，展示组合字段仍可与真实动作矛盾。

## 所有权

允许原PREVIEW范围内与本缺陷直接相关的 `engine/access/PlayerPresentationPreview.kt`、`protocol/transport/api/ProtocolPreviewModels.kt`、`ProtocolModels.kt`/`ApiMessage.kt`、`client/api/PreviewModels.kt`及必要对应快照校验；对应四模块新Preview测试可修正和补充。各层mapper只在确有必要时最小修改，不改算法provider、权威规则、server宿主/绑定、client运行时/UDP、PUBLIC投影、presentation/build/reviewer。尽量通过新DTO构造的结构校验统一约束，严格codec不得绕过。

## 必须修复

1. client测试真实属性接线错误，保留赤五断言。
2. 鸣牌预览type与其action类型/kanType必须一致，只允许DeclareChi/Pon/Kan作为鸣牌预览，不能合法optionId却用Pass/Discard/千术动作伪装。
3. 按物理牌完整等值核对tiles/handTiles/calledTile/addedTile/fromPlayer与action表达的一致性，不能按typeIndex替代physical tile；保留顺序语义与赤五。CHI的action.meld完整信息可直接比较，PON/OPEN_KAN的action.tiles是本人参与牌，加上被鸣牌才是完整副露；CLOSED_KAN完整4张、无外来牌；ADDED_KAN真实动作包含完整4张，handTiles仅含补入1张，预览保留原碰组的公开来源/被鸣牌与第4张。遵循现有MeldChoiceProvider真实契约，不把规则计算搬进protocol/client。对不在动作中携带的原碰组信息，可结合快照公开melds进行一致性核对或采用合理结构约束，但不得虚构来源。
4. 明确拒绝少牌、重复物理牌、外来牌冒充本人牌、type/action矛盾等格式不一致；不能丢弃整个预览或不带optionId规避。真实五种鸣牌候选、立直候选、待牌、source/getter不可变、三层映射、strict metadata全部仍须通过。
5. 新增对应模块构造/codec反例，并保留所有有效原断言；手工test fixture若此前本来无效，应对照真实provider改为合法物理数据，不关掉校验。

交付 `delivery-ANTI-R4B2PREVIEWFIX.md`，说明最终结构约束和精确修改文件、未完成项，明确未运行测试。结束前读FEEDBACK-R4B2PREVIEWFIX.md（若有）。Codex统一复验并决定接受；不因自己已写交付就推进后续阶段。
