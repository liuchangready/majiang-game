# R4B2 WIREFINAL：修复两个已定位的收尾问题（明确执行）

前两路WIREFIX/WIRETEST均已退出。只做以下精确小修，不重新实现PUBLIC，不扩展需求。整个R4完成后暂停，不进R5/R6。只文件工具，禁止shell/RunCommand/build/再委派，Codex独立验收。

1. **生产所有权仅protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt的classifyPush及对应注释**。最新真实`R4B2-WIREFIX-review.log`13项12通过1失败：`type:null`被当PLAYER，违反非字符串type拒绝。当前`if(typeElem == null || typeElem.isJsonNull) return PLAYER`必须区分真正缺失与明确null。旧无type分支也须验证实际玩家信封结构，不接受空对象`{}`；保留真实encodePush→decodePush合法玩家基线。参考FEEDBACK-R4B2WIREFIX.md。公开六codec/请求/令牌/immutable其余12项已过，禁止重构这些无关路径。
2. **测试所有权仅protocol/src/test/kotlin/com/majiang/transport/api/ProtocolPublicModelsCodecTest.kt**。`R4B2-WIREFIX-protocol.log`compileTestKotlin失败，因为导入JUnit Assertions.assertNotNull返回Unit，却在249附近`val b=assertNotNull(safe.board)`后使用b.scores等，453附近把assertNotNull(result.publicEvents)当List。改为正确的Kotlin返回值断言或先断言再安全取值，保留全部嵌套不可变验证，不删除/跳过测试。其他文件不改。

两处完成即可写delivery-ANTI-R4B2WIREFINAL.md并结束，明确没有自行运行测试。结束前读FEEDBACK-R4B2WIREFINAL.md（若有）。协议仍未接受，下一SERVER/CLIENT由Codex验收后另派，勿自行启动。
