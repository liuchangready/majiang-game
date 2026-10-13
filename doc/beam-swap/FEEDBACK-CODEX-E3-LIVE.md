# Codex E3 开发中检视

## 测试准备补充

- 旧SleeveBloomScreenContractTest的按钮几何断言已由Codex更新：y=100改为VISUAL统一布局要求的106；其他原断言保留。这是布局契约改变，不是放宽测试。
- BeamSwapChoiceModelContractTest现12项，补断线一段时间后恢复同一缓存快照不得重置倒计时、缺失本人时钟时保持模态但禁止选择/提交。断线恢复不得把旧serverNow重新当成刚采样时间。
- Codex真实产品GL脚本新增第三次激活后不提交，等待权威超时KEEP和默认弃牌推送清模态；现在输出`BEAM_SWAP_GL_PASS choices=2 timeouts=1 fee=30`。正常SWAP、手动KEEP及超时各扣10。等待真实截止需额外约23秒，脚本最大帧数3600。其他测试和脚本错误仍由Codex维护，Anti不要直接改。

## 配置接线检查点

产品显式默认配置有三处需覆盖：MahjongGame.launchGame、MenuScreen单机启动、MenuScreen建房确认。请三处均启用beam并添加BEAM_SWAP成本10；下层LobbySession/ServerConnectionSession的null默认和用户显式disabled不得被强改。旧服务缺失能力/beam权威视图时入口不能点击。

## 模型初审

初版BeamSwapChoiceModel的默认monotonicClock使用System.currentTimeMillis，应与已有SleeveBloom模型一样使用System.nanoTime()/1_000_000，避免系统校时改变剩余预算。断线cancel清空采样会让恢复同缓存快照重新计满，12项测试已覆盖。另canActivate目前不检查截止，且无offer时不采本人时钟；权威canApply=true的缓存快照到期后仍能构造激活请求，应按本人时钟禁用入口，保持服务端最终裁决。

## 面板初审

- WallSlot标签当前用绝对index+1，例如下33…下40；普通候选应按offer内顺序显示下1…下8，槽地址仍保留绝对索引用于请求。
- 暴露的Rectangle和LinkedHashMap可被调用者修改，破坏实际命中布局。按VISUAL要求返回防御性副本/只读映射，不要让布局读者改内部状态。
- 编译检查点：初版使用不存在的SuitView.fromTileId且TileView构造参数不符，候选有真实tile时可直接使用client TileView；不要凭ID重新推导隐藏牌面。
- 补真实BEAM_SWAP_INVALID_CHOICE的中文翻译；不要只提供不存在的INVALID_HAND_TILE/INVALID_TARGET_SLOT拆分码。
- 当前先画“汗”字后flush牌卡，汗标被后画的牌面覆盖。应先flush牌面，再画汗标及选择覆盖层；GL最大布局包含1张标记牌，会专门检视。

模型测试现13项（新增激活截止），Screen现4项（新增真实错误码中文及字形），几何测试已补布局副本防御断言。均由Codex追加。

模型修订复核：canActivate的`cachedDeadline > 0 && remainingMillis <= 0`仍会让缺本人时钟/零截止的快照绕过门禁。应要求有效本人时钟和剩余时间>0；缺时钟测试同步覆盖激活及选择两种阶段。

GameScreen异步入口初审：回执仅释放局部提交标志、不清offer，推送先到不会错误关闭面板。轻微提示时序问题：失败回调缺发起session/round/syncEpoch检查，旧局回执可能在新局弹旧错误；建议回调释放自己的标志后，仅在发起scope仍匹配时显示toast，不改变权威快照。

首轮全量旧用例失败：MeldChoiceScreenContractTest两项、RiichiChoiceScreenContractTest一项。它们的模态取消坐标(1100,160)与新增beam入口(1080,156,140,44)重叠；若禁用入口仍先吞点击，会阻止现有模态取消，后续点击甚至落到荣和。必须优先处理当前模态的真实按钮，不能仅把入口canOpen设false后提前return。保留旧测试。
