# 袖里藏花执行状态

2026-10-13：SB-KAN-DORA 雀魂式杠宝牌时机与袖里藏花联动修订完成，Anti第二版回调后，Codex独立检视及1131项复测通过，详见[当前验收](ACCEPTANCE-KAN-DORA.md)与[开发交付](DELIVERY-ANTI-KAN-DORA.md)。

- 最新规则（SB-KAN-DORA）：
  - **暗杠立即翻宝**：暗杠成立后立即翻开本次杠宝牌，再摸岭上牌；自摸计入新杠宝牌（若立直和牌，结算时翻开对应杠里宝；非立直和牌无里宝）；
  - **大明杠/加杠延迟翻宝**：大明杠/加杠成立后先补牌，本次杠宝牌延迟到弃牌时翻开；弃牌响应/荣和算分前已经生效；若补牌后直接岭上自摸，本次尚未翻开的杠宝不计入；若立直和牌，里宝数量严格仅对应已翻表宝；
  - **加杠抢杠裁决**：加杠先完成抢杠裁决，被抢杠的这次杠不增加杠宝牌、不补牌；
  - **连续杠时机**：前一次已成立明杠待翻指示牌在后一次杠成立、下一次岭上摸牌前翻开；后一次若为暗杠立即再翻本次，若为明杠/加杠则留下本次待翻。后续加杠被抢时不提前揭示前一次仍待翻指示牌；
  - **表宝上限与终局复位**：最多五张表宝，终局清除待翻状态但不补翻，新局复位；
  - **待翻状态模型**：待翻状态（`pendingKanDoraCount`）归属单局 `GameState`，严格受深拷贝、恢复、事务与 `StateInvariants` 守恒审计约束；
  - **袖里藏花限定与联动**：杠后第二候选仅限下一张未取岭上牌 `deadWall[rinshanPointer]`，选第二张时原摸牌写回该槽；不动普通牌山、替补列表或指针；第四杠补牌窗口禁止施术且不扣费；sleeve-bloom 激活、选择、取消、拒绝与查询均不翻宝，超时保持 FIRST 后默认弃牌正常触发待翻宝牌，选择 SECOND 岭上和牌按已翻宝牌计算。
- 分工：Anti 修改生产代码与文档并执行开发验证；Codex 独占新增测试 `SleeveBloomKanDoraTest.kt` 与 `SleeveBloomKanDoraHostTest.kt`，并执行独立检视。
- Codex独立复测：全量1131项与双端构建通过；960×540真实GL冒烟通过（573帧）。新增16项测试，见当前验收。
- Anti开发验证记录：
  - 全量单元测试：1131 项全部通过，0 失败，0 错误，0 跳过（覆盖 client 136, engine 427, presentation 268, protocol 158, server 142）；
  - 袖里藏花领域专项：50 项通过（`SleeveBloomContractTest` 21, `SleeveBloomKanDoraTest` 15, `SleeveBloomWallAndRulesTest` 14）；
  - 双端构建：桌面 classes 与 Android assembleDebug 均通过；
  - 真实桌面 GL 冒烟：1280 视口（583 帧）与 960 视口（568 帧）均输出 `SLEEVE_BLOOM_GL_PASS`；
  - Android 实体机触控未验证。
- 历史验收记录：
  - [岭上候选规则修订验收](ACCEPTANCE-RINSHAN.md)（历史记录）
  - [初版验收](ACCEPTANCE.md)（历史记录）
- 工作区说明：当前工作区包含 SB-KAN-DORA 规则修订的代码、测试和文档。用户原有 `lwjgl3/build.gradle`、`SnapshotLauncher.kt`、`snapshot/`、`doc/README.md`、`doc/beam-swap/` 改动保留在工作区，不执行 Git 提交或推送。
