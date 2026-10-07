# B2 计时检视返修

仅修改 timing/**、control/** 中确有必要的实现；不改测试或其他任务代码。A/B已交付，D正在集成，公开契约保持兼容。

Codex定向测试18项中发现DecisionClock两项失败，详见build/v3-migration/wave1-tests.log与TimeControlAcceptanceTest。

1. expire(key,generation) 当前没有检查clock.nowMillis是否已到deadline，任意提前回调就扣光储备。必须尚未到期、PAUSED状态或已关闭/错代次时返回false且无副作用。
2. open(key)当前不检查key.roundId匹配currentRoundId，可以旧局任务打开新局预算。必须拒绝不匹配。所有公开seat查询验证0..3。
3. pause/resume时，不能接受正常玩家完成命令，也不能在暂停期间expire。取消时只结算暂停前用时；resume保留余量，不能重置免费时间。
4. SESSION储备beginRound时若还有旧活动计时，应先按当前有效时间结算再关闭，不能清空未结算耗时白送余额。ROUND作用域可在结算后重置。
5. reset不能重置generation计数导致旧timer与重用key碰撞。时间相加检查溢出，配置拒绝无法安全计算的总时长；不强制单调时钟本身非负（System.nanoTime源可负），只拒绝算术溢出。
6. 检查ConnectionControl的重复disconnect/reconnect/宽限任务幂等与过时代次，不把单次断线重复通知延长宽限；必要时修正并在交付说明。

新增测试由Codex完成。请交付delivery-B2.md并明确每项修复。
