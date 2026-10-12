# SB-E2 测试已就绪，待 E1 验收后启动

Codex 已完成16项专项：protocol 7、client 4、server宿主2、进程内1、真实UDP1、原始UDP观战伪造拒绝1（复用TileMarkUdpContractTest的原始报文夹具）。client 的 FakeServerConnection 和 presentation 的 FakeGameSession 已补新接口的测试实现。E1已验收且E2已下发，由Anti独占Gradle执行任务单规定的相关模块测试与全量test。

全量test不包含doc/sleeve-bloom目录下的*.kt.pending表现层草稿，它们在E3才移入测试源集。禁止为了过测改测试或添加假的成功实现。测试夹具/接口命名疑问提供证据给Codex，由Codex修正。
