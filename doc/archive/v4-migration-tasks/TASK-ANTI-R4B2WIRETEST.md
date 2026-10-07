# R4B2 WIRETEST：观战协议模块验证与fake接线（明确授权执行）

只用文件工具，不运行shell/RunCommand/build/再委派。Codex负责构建验收，用户要求完成R4后暂停。先读TASK-ANTI-R4B2WIRE.md、FEEDBACK-R4B2WIRE.md以及实际protocol主源码。原WIRE已退出；WIREFIX并行只改protocol主源码，你仅改protocol/src/test。

## 独占范围与任务

1. `R4B2-WIRE-first-protocol.log`证据：FakeServerConnection未实现SpectatorApi六方法导致compileTestKotlin失败。为fake补真实可配置结果/请求记录/PUBLIC订阅及移除/推送，延续现有fake风格；用相关模块测试验证。不能以默认空接口实现修改生产，不弱化现有PLAYER/ROOM断言。
2. 新增PUBLIC模块测试覆盖原任务要求：独立快照/结算历史六outcome/完整评分/合法负分/物理红牌、深层源与getter不变性；五新ApiRequest与SpectatorBinding响应/token脱敏；混合私有字段/私密事件拒绝；strict PUBLIC_UPDATE普通连续与full标记；PUBLIC_RESEND完整checkpoint及错误边界；typed分类未知/非字符串type拒绝以及旧PLAYER/ROOM保持有效。
3. 冻结API：ApiMessageCodec.encodePublicSnapshot(ProtocolPublicSnapshot):String、decodePublicSnapshot(String):ProtocolPublicSnapshot；encodePublicUpdatePush(ProtocolPublicUpdate):String、decodePublicUpdatePush(String):ProtocolPublicUpdate；encodePublicResendRequest(PublicCheckpoint):String、decodePublicResendRequest(String):PublicCheckpoint；`classifyPush(String):PushType`，enum PLAYER/ROOM/PUBLIC已存在。WIREFIX将补这些目前缺失函数，切勿自行改主源码。
4. 所有新类型构造器/字段已落盘，务必读真实定义；不要虚构评分字段或Tile构造参数。公开lifecycle为WAITING_START/ROUND_ACTIVE等真实SessionLifecycle，非ROOM的LOBBY/PLAYING；roundNumber合法零基0；负分合法。

禁止写其他模块、protocol/src/main、tools/architecture/v4-review、build。不能删除/跳过旧模块测试。交付delivery-ANTI-R4B2WIRETEST.md列文件与未运行测试声明；结束前读FEEDBACK-R4B2WIRETEST.md（若有）。源码交付不能宣称验收完成。
