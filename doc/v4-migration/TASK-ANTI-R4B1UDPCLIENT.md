# 并行B：客户端UDP可靠房间推送

用户授权多个agent并行，Anti优先，额度耗尽由Codex切Claude/GLM。你只负责客户端UDP传输，服务端由另一agent实现，运行时由第三agent实现。

读CONTRACT.md、TASK-GLM-R4B1UDP.md、FEEDBACK-GLM-R4B1UDP.md，结束前读FEEDBACK-R4B1UDPCLIENT.md（若有）。只用文件工具，不shell/构建/提交/再委派。

## 独占所有权

- client/src/main/kotlin/com/majiang/client/transport/udp/**
- client/src/test/kotlin/com/majiang/client/transport/udp/**
- 不修改ServerConnectionSession、client.api契约/mapper、protocol、server、reviewer、构建配置。共享协议已有严格信封，无需改公共类型。

## 固定交接契约

服务端以PUSH完整`ROOM_UPDATE`信封发送ProtocolRoomInfo，按房间version独立于对局cursor排序，每连接最多保留最新一条待确认。房间消息重试/分片使用稳定消息seq，客户端ACK.ackSeq回指完整消息seq。客户端必须在ReplayGuard丢弃重复/旧seq之前识别并ACK有效的房间重传；不能给非法/外来身份的数据续命。ACK确认与UI去重分开。不能因ACK丢失永久重复发送或永久抑制确认。

## 要完成

1. 接完ROOM_UPDATE ACK与重试接收；维持game PUSH cursor/replay既有行为。丢最后room更新和丢第一次ACK都必须自行收敛，不能依赖UI轮询或用户下一次请求。
2. roomId+当前连接/绑定代次隔离；乱序/重复version不回退。leave与新房建立后缓存/版本重置，旧room消息和延迟查询不能污染新房。建房/加入/恢复握手早期推送不能丢最终状态，也不能因新room version低于旧room而永不更新。完整room视图允许跳版本合并，不把跳版本当game cursor缺口。
3. 保留断开/顶号/停服socket linger、请求重传与已有分片身份隔离。
4. 补假时钟/传输模块测试覆盖丢ACK重复ACK、乱序、外来roomId、换房及早期消息。独立真实丢最后配置更新用例为V4RoomDatagramAcceptanceTest，只读。

交付delivery-ANTI-R4B1UDPCLIENT.md，明确新行为和未验证项。不要扩展至ServerConnectionSession缓存任务。
