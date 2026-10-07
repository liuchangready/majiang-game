# 洗牌千术实施契约（2026-10-02）

用户授权并行 Anti 开发，Codex 负责设计、测试、集成验收。只实现洗牌阶段；摸打千术不开放。本文件优先于旧 PLAN 的历史任务范围。现有工作区所有改动必须保留，禁止重置、提交或编辑测试。

## 功能冻结

每次发牌（含连庄）有固定10秒独立准备窗口，不提前结束，不消耗/刷新3+20。确认前本地多选；确认后锁定；超时未确认自动无千术。发牌前不可看到任何手牌、牌山或宝牌。偷天换日40点影响起手；瞒天过海20点影响前8次正常顺位预计摸牌；各每局最多一次，可同时使用60点。多目标等权、归一化，总强度不叠加。目标6项：万/条/筒清一色、大三元、国士无双、大四喜。不同目标允许混选，UI解释混合倾向不保证兼容成牌。

东风战 winds=[1] 整场50点，半庄[1,2]整场100点，各局/连庄/进入南场不恢复。确认仅预留，扣费与成功发牌一次原子提交；重复请求幂等；失败不扣，已发牌不退款。计划和预留私有，仅本人可查询；已公开余额仍遵守原协议，不能谎称使用行为完全保密。其他人确认不应产生目标/费用事件泄漏。

兼容模式：MatchConfig 新增 shuffleConfig:ShuffleConfig=ShuffleConfig()，默认enabled=false，保持现有规则与夹具测试直接发牌。真实产品 MahjongGame 默认入口显式启用，客户端/网络支持同一功能。启用且未显式提供经济配置时，Host依赛制设置50/100与固定费用；显式经济配置保持。初始已发牌状态不能逆插准备阶段。

## 共享类型冻结（由 A 唯一创建 engine/.../stacking/**）

package com.majiang.stacking

enum ShuffleTarget { CHINITSU_MAN, CHINITSU_SOU, CHINITSU_PIN, DAISANGEN, KOKUSHI, DAISUUSHII }

data class ShuffleConfig(enabled:Boolean=false, selectionMillis:Long=10000L, plannedDraws:Int=8, strength:Double=0.75, interference:Double=0.65, aiMaySpend:Boolean=false)

data class ShuffleSelection(initialTargets:Set<ShuffleTarget> = emptySet(), drawTargets:Set<ShuffleTarget> = emptySet()) {
  fun cost():Int // 40 if initial nonempty +20 if draw nonempty
  fun deepCopy():ShuffleSelection // immutable sets
}

data class ShufflePlanRequest(requestId:String, roundId:String, controlEpoch:Long, selection:ShuffleSelection) { fun deepCopy():ShufflePlanRequest }

enum ShufflePreparationStatus { SELECTING, GENERATING }

data class ShufflePreparationView(roundId:String, status:ShufflePreparationStatus, opensAtMillis:Long, deadlineMillis:Long, serverNowMillis:Long, confirmed:Boolean, selection:ShuffleSelection?, reservedPoints:Int, availablePoints:Int) { fun deepCopy():ShufflePreparationView }

data class SeatShufflePlan(seat:Int, selection:ShuffleSelection)

object ShuffleTargets {
 fun demand(target:ShuffleTarget):DoubleArray //34 typeIndex顺序，L1归一
 fun overlap(a:ShuffleTarget,b:ShuffleTarget):Double
 fun combined(targets:Set<ShuffleTarget>):DoubleArray //等权归一；empty全0
}

class ShuffleWallGenerator {
 fun generate(seed:Long, dealerSeat:Int, plans:List<SeatShufflePlan>, config:ShuffleConfig=ShuffleConfig(enabled=true)):List<com.majiang.model.Tile>
 //136唯一物理牌，类型红五copy身份保留，固定种子可重现。只返回宿主内部，绝不传给客户端。
}

object ShuffleSlots {
 fun initial(dealerSeat:Int,seat:Int):List<Int> //4x3发牌+单张+庄第14，0..52
 fun draws(dealerSeat:Int,seat:Int,count:Int=8):List<Int> //53..121 only，绝不122..135王牌
}

模型需强校验合法有限参数；反序列化层仍独立校验，不能依赖Gson调用构造器。

## 跨层接口冻结

B 唯一修改 engine access/host/config/coordinator/round/state：
- PlayerSnapshot 末尾增加 preparation:ShufflePreparationView?=null，deepCopy包含它。
- SessionAccess 增加 submitShufflePlan(request:ShufflePlanRequest):CompletableFuture<ActionReceipt>，默认返回UNSUPPORTED_ACTION拒绝（保持旧fake可编译）。
- LocalSession 转发到 TableHost.submitShufflePlan(seat:Int, request:ShufflePlanRequest)。
- 合法提交返回RECORDED，错误明确REJECTED；空selection为主动不使用，也锁定。
- preparation.clock独立，不要用decisionClock.open计时。所有查询/发布包含本人准备状态；暂停冻结serverNow/deadline，恢复平移，不赠送时间；重连不重开。
- 准备时普通决策options为空；麻将submit/旧start/next/activeGame不能绕过准备，重复start不重置。
- 方案状态归属本局，计时/规则/生成在外部服务，不把算法塞进Round或Session。统一提交费用与136牌发牌结果，使用现有prepare/audit/install，不能先扣费再尝试发牌。

C 唯一修改 client/** 与 server/**：
- GameClient.submitShufflePlan(request:ShufflePlanRequest):CompletableFuture<ActionReceipt>，同已有pending/close/异常机制。
- 网络请求类型 SubmitShufflePlan，SessionAccess异步同一方法，Server绑定当前seat/connection generation后转发；不能接受客户端指定他人seat、wall/seed或任意费用。
- ProtocolJson 显式写读 preparation 与选择、请求；严格白名单与范围，缺失preparation可解析旧快照；协议新能力不静默丢字段。服务端房间已采用Host.config需保持传递shuffleConfig，未知请求明确拒绝。

D 唯一修改 core/** 生产（测试归Codex）：
- GameScreenClient 新方法submitShufflePlan(request)，默认UNSUPPORTED_ACTION；V3Adapter转发，Legacy不造成功回执。
- 默认 MahjongGame 本地创建启用shuffleConfig=true，赛制不随意改动；50/100由B权威config计算。
- Screen准备模态展示10秒、两个千术/六个多选目标、费用与余额、确认/不使用、已锁定/生成中；普通牌桌输入被捕获。按roundId恢复/清空，不把空手牌准备阶段当成错误。
- UI倒计时基于权威serverNow样本+本地单调经过时间，暂停冻结；不能操作权威倒计时。确认带当前roundId/controlEpoch/requestId，双击门控，错误保留草稿并显示原因；超时草稿不自动花费。
- 字体必须能显示真实中文千术名（当前默认BitmapFont不含中文）。可使用assets内授权字体或生成局部中文文字图元，不能显示方框；若需字体文件，由Codex提供，不下载未知授权字体。先完成布局和中文字体接口，再报告需求。
- 摸打千术不开放；不改已有倒计时、吃碰杠、结算、副露显示行为。

## 验收

Codex编写算法/宿主/协议/UI测试。物理守恒、多目标趋势而非保证成牌、同种争牌衰减、异花色对称、国士唯一种类/字刻子进度、分区与庄家轮转、准备10秒截止/暂停/跨局/重试/旧代次、原子消费、私有投影、真实TCP、UI输入/恢复/实际GL。原有511项测试保留，仅产品入口GL脚本因10秒流程需更新，不能删除原断言。
