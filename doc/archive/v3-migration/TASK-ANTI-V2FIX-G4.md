# G4 确认候选读取旧快照的实际展示回归

此任务明确授权修改产品 core/src/main/kotlin/com/majiang/screen/GameScreen.kt，只改touchDown中CONFIRM RIICHI及CONFIRM副露两个相邻分支。不要改测试，不调查其它算法。

Codex core 182测试剩1失败：MeldChoiceScreenContractTest:127 quietly stale 应0次choose，实际1。移除旧legacy polling后，client.snapshot已更新、UI通知尚未执行时，currentView仍是旧候选。

精确修法：两个确认分支在读取confirmationAction后，取得 `val latestView = client.snapshot?.view ?: currentView`；合法性contains(action)以latestView.legalActions为准，不以currentView为准。已失效时cancel对应selection，并调用refreshLocalSelectionModels()更新显示，消费本次点击但不submit。候选仍合法时保持现有异步提交/拒绝回执流程；未选牌时仍只消费点击不发动作。

仅校验本地已缓存快照，不同步查宿主、不join，不把authority规则放UI。不要对所有submitActionAsync全局添加view.legalActions限制（session动作可能不同），本任务只立直/副露确认两处。

保持服务端最终校验，不恢复V2接口、不删测试。立即保存写简短delivery-ANTI-V2FIX-G4.md。Codex重跑原测试和图形验收。
