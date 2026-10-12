# Anti / agy：袖里藏花 E1 领域实现

2026-10-12。用户已授权 Anti 开发、Codex 编写测试并独立检视。以根 AGENTS.md 和本目录最新 DESIGN.md 为契约，先修复设计指出的规则接入问题，再完整实现本阶段。UI 与线路分阶段验收后继续。

## 文件所有权与接口

仅修改 engine/src/main 的生产实现及本阶段 PROGRESS-ANTI-E1.md、DELIVERY-ANTI-E1.md、CALLBACK-ANTI-E1.json。所有 src/test、测试夹具、tools/architecture 归 Codex，只读。不得删除、跳过、弱化或改写任何测试；遇到测试本身错误，提供证据由 Codex 修正。不得修改 Gradle、用户的 SnapshotLauncher.kt、snapshot/ 或其他现有改动。不委派其他代理，不 git add/commit/reset，不更改全局权限配置。

本次 agy 以 --mode accept-edits --dangerously-skip-permissions 运行，允许执行仓库内必要构建、测试。开工写进度，接口落地后更新进度；Codex 会同时写测试，运行 Gradle 前须看到 doc/sleeve-bloom/TESTS-READY-E1.md。此前可编写生产与做静态检查，不并发启动共享 Gradle。

冻结测试所需接口（可增加成员，不改变以下名称/类型）：

- com.majiang.cheat.bloom：SleeveBloomConfig(enabled: Boolean = false)、SleeveBloomDrawKind { DEAL_EXTRA, NORMAL, RINSHAN }、SleeveBloomKeep { FIRST, SECOND }。
- SleeveBloomRequest(requestId: String, sessionId: String, roundId: String, decisionId: String, controlEpoch: Long)，值相等及 copy 同落汗。
- SleeveBloomChoiceRequest 同上再加 offerId: String、keep: SleeveBloomKeep，值相等及 copy。
- MatchConfig 尾部参数 sleeveBloomConfig: SleeveBloomConfig = SleeveBloomConfig()。
- GameState：sleeveBloomOffer、sleeveBloomUsedOnTurn、lastDrawKind，按设计深复制和复位。
- PlayerSnapshot.sleeveBloom: SleeveBloomView?；视图 canApply、disabledReason、cost、drawKind、decisionId、controlEpoch、stateVersion、pendingOffer。offer 视图含 offerId、firstTile: Tile、secondTile: Tile、drawKind、deadlineMillis、decisionId、controlEpoch。
- TableAuthority.submitSleeveBloom(seat, request, now)、submitSleeveBloomChoice(seat, request, now)：MutationResult。
- TileMovement.swapDrawnWithRinshanReplacement(state, player): Tile。

## 必须解决

1. 正常头端交换与三种杠补替补交换，136 物理守恒，激活只揭示，选择第二张才移动。每次摸牌一次，庄家发牌额外张/立直/吃碰后的 DISCARD 禁止。
2. 岭上指针与总杠次数分离，候选与最终提交禁止第五杠；不误禁第四次补牌，不回退宝牌或实际杠数。
3. 扣费、使用标记、offer 原子安装；10 点可激活，扣至 0 仍可选择。幂等查询在当前决策/epoch/deadline 之前，同 ID 不同类型/载荷冲突，首次拒绝也缓存。
4. 激活/选择后重建决策、rekey 原计时；选择第二张后重算合法动作、预览与最终算分；保留岭上和首巡来源语义。
5. 挂起拒绝普通动作及落汗，托管 AI 不提交；到期先清 offer 再默认摸切，无卡死、无时间赠送。暂停保留，截止前收回真人控制使用新身份继续，旧请求不执行。
6. 当前身份投影 offer，隐私和快照不可变；选择/清理无事件也以 stateChanged 通知。终局清 offer、新局复位。失效清理不能提前摸切。

## 测试、返修与回调

收到 TESTS-READY-E1.md 后运行并等待结束：

```powershell
.\gradlew.bat :engine:test --tests 'com.majiang.cheat.bloom.*' --console=plain
.\gradlew.bat :engine:test --console=plain
```

必须核对退出码和 engine/build/test-results/test 的 XML 测试数、失败、错误、跳过数。失败先定位并修改生产代码，重跑专项和完整 engine，直到全部通过才交付 ready_for_review。不能以编译成功、旧报告、空测试或工具 SUCCESS 代替通过。若有不可自行解决的问题，用 blocked 回调并说明证据，不声称通过。

写 DELIVERY-ANTI-E1.md（改动、命令、结果、真实计数、遗留项）与 CALLBACK-ANTI-E1.json，至少包含 taskId=SB-E1、recipient=codex、status=ready_for_review 或 blocked、modifiedFiles、tests、remainingIssues。最后输出 `CODEX_CALLBACK SB-E1 ready_for_review`（或 blocked）并退出。CLI 退出结果和回调文件通知当前 Codex，不另启 Codex 进程、不调用 invoke_codex。

回调仅表示提交检视。Codex 独立运行测试并检视；发现问题会发返修单，Anti 必须改进、全部测试通过后重新提交交付与回调，不得自行宣称验收通过。
