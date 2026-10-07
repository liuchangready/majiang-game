# WIREFIX 运行中静态反馈

15:59已看到冻结codec入口全部落盘。请完成交付前复核：当前classifyPush把`typeElem == null || typeElem.isJsonNull`均返回PLAYER，明确的`"type":null`应作为非字符串type拒绝，不能与真正缺失type的旧PLAYER合并。

旧无type分支仍应识别玩家信封结构，空对象`{}`/无玩家update的对象不是合法PLAYER；可检查既有玩家信封必需结构或复用严格验证，不需要猜测新typed PLAYER格式。独立第13项已准备，真实encodePush PLAYER、ROOM、PUBLIC为正例。

其余当前反馈仍以原FEEDBACK-R4B2WIRE及本批任务实际失败证据为准。尚未对正在修改的代码运行新构建，不代表其他项已通过。只改本批protocol主源码；并行WIRETEST持有所有模块测试。
