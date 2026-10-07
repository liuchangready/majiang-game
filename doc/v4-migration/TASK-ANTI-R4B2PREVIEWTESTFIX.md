# R4B2 PREVIEW 最后模块测试装配修复

Codex明确授权执行本任务，Anti仅使用文件工具，不运行shell/build/提交/改设置/再委派。用户要求R4完成后暂停，不进入WIRE/R5/R6。本批生产全部冻结，仅允许修改`server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolPreviewMapperTest.kt`及本任务交付文档。

真实证据`build/v4-migration/R4B2-PREVIEWFIX-modules.log`：engine369、protocol112、client118全通过，server105项104通过1失败。唯一失败`test MeldPreviewOption for all meld kinds roundtrip`第160行，ADDED_KAN装配`GameAction.DeclareKan(0,listOf(addedTile),ADDED_KAN)`被正确拒绝。

实际`MeldChoiceProvider.buildAddedKanChoice`接受action.tiles完整4张，handTiles才单张；当前fixture已经拥有ponTile1/ponTile2/calledPon/addedTile且tiles顺序正确。请将该action改为真实四张物理牌，保留handTiles仅addedTile、原碰组来源/被鸣牌、赤五与全部映射往返断言。可补断言明确action四张/handTiles一张，但不改生产校验或放宽预期，不删除/跳过测试。

本次PREVIEWFIX源码交付后接口EOF退出，但源码已落盘并完成实际模块验证；本任务只修上述真实无效fixture，不重复上批生产工作。完成写`delivery-ANTI-R4B2PREVIEWTESTFIX.md`，说明改动并明确未运行测试。Codex单独复验server模块并决定接受。结束前读FEEDBACK-R4B2PREVIEWTESTFIX.md（若有）。
