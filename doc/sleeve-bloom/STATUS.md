# 袖里藏花执行状态

2026-10-20：**立直与他家回合扩展（SB-RIICHI-OFFTURN）实现完成，进入自动化验收。** 设计见[扩展设计](EXTENSION-RIICHI-OFFTURN-DESIGN.md)，本轮验收记录见[扩展验收](ACCEPTANCE-RIICHI-OFFTURN.md)。

- 本轮规则：
  - 已立直真人可在本人摸牌后的 `CHEAT_WINDOW` 施展；`checkRiichiAutoDiscard` 在已付款 offer 存在时让位，权威截止后才结算 FIRST 并默认摸切。
  - 本人弃牌后由权威签发 `windowId`，直到本人下一次实际普通摸牌或局终；他家行动期间可支付 10 点、私见两张活牌山候选并二选一。
  - 候选与已选预留均为 `TilePool` 活牌山中的物理实体；他家摸牌、岭上尾端替补、偷梁换柱候选均跳过锁定/预留牌；本人在下一次普通摸牌时按实体交付，未选牌解锁并按原相对顺序留下。
  - 预定 offer 使用独立 10 秒权威截止；暂停顺延剩余预算；断线/托管不清除已付款 offer；重连完整私有快照恢复。
  - 立直后仍只开放合法暗杠；杠后新岭上补牌有新的 `turnNumber` 与费用检查，可再次支付 10；第四杠无候选时拒绝且不扣费。
- 实现范围：engine 状态/物理/投影/超时，protocol 强类型请求与私有 DTO，server 串行入口/幂等回执/TCP 与进程内路由，client 请求与快照映射，presentation 入口/面板/预留提示/菜单文案。`ApiProtocol.PROTOCOL_VERSION` 升至 6、`TcpWire.BUSINESS_VERSION` 升至 3，MJ-TCP/1 帧格式不变。
- 开发验证：
  - 全量单元与模块测试：1648 项通过，0 失败、0 错误、0 跳过（engine 539、protocol 193、server 339、client 230、presentation 347）。
  - 新增/更新：`SleeveBloomOffTurnTest`（16 项，含 offer 被他人摸牌越过后 FIRST/SECOND/超时解锁洞、即时普通摸牌以第二个洞为候选并 FIRST/SECOND、只剩洞时激活、预留后置交付、跨座多 offer 交织、终局释放与预览）、协议 offer 预览 codec 测试、服务端 off-turn 主机与重连测试、客户端模型 off-turn 测试；旧回归全部保留。
  - 构建：`:lwjgl3:classes` 与 `:android:assembleDebug` 通过。
  - 真实桌面 GL 冒烟：1280×720 `SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=567`；960×540 同标记、`frames=567`（一次 960 运行因等不到第二次施术机会在 2100 帧超时，随后复跑通过）。该冒烟覆盖原有即时普通摸牌路径，**不**代表他家回合/立直杠后场景已做真机或跨网络实测。
  - 未完成/未宣称：Android 真机触控、跨物理网络 TCP 实测、真实对局中他家回合入口与立直暗杠的 GL 画面走查均未执行；不得记为通过。

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
