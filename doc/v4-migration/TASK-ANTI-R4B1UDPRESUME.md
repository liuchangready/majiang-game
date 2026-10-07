# Anti 接续 GLM 未完成的 R4B1 UDP 批次

用户于2026-10-06明确继续使用Anti开发，Codex监督/测试/验收。GLM UDP调用01:09因429退出，原CLI已结束，无最终交付。保留全部现有改动，不重写已接受ROOM，不回退、不清理工作区。

先读CONTRACT.md、TASK-GLM-R4B1UDP.md、FEEDBACK-GLM-R4B1UDP.md及本任务最新反馈FEEDBACK-R4B1UDPRESUME.md。TASK-ANTI-R4B1UDP.md的技术范围仍适用，旧“草案/等待ROOM”已被本次正式派发覆盖。

## 当前事实

- ROOM于00:55接受：相关独立23项、server91项通过；完整111项103通过8失败。新增最后ROOM_UPDATE丢包验收后目标112项。历史通过不能代替当前部分UDP源码验证。
- GLM改了ProtocolRoomModels.kt（删兼容构造器）、ApiMessage.kt（严格ROOM_UPDATE）、UdpRoom.kt（包装SharedRoom）、UdpTableServer.kt（registry、可靠room更新、API路由）。没有完成client UDP ACK/排序/身份接线或模块测试迁移。先检查实际声明与编译反馈。
- 客户端ServerConnectionSession房间缓存/查询失效3项留到下一小批。不要改该运行时，不进入B2/R5/R6。

## 本次所有权及完成要求

只修改本批server UDP绑定/会话/路由、必要共享房间接入、protocol房间契约、client UDP绑定及相关模块测试。文件工具ONLY；不执行shell/构建/提交，不改reviewer或设置，不再委派。

1. 完成真实本地/UDP统一SharedRoom/RoomRegistry业务；UdpRoom仅传输管理。请求验证须在权威队列实际执行期检查绑定。ready/config/start真实路由且配置影响实际规则与计时。
2. 完成可靠ROOM_UPDATE：最后一条丢包也能恢复、重复重传ACK不被ReplayGuard挡住、版本不回退、旧房间/旧连接不污染新绑定。检查分片ACK身份：目前send()对每个fragment分配新seq，待确认记录可能追踪原seq，不能出现客户端ACK后服务端永不清理。可按当前未发布协议统一修正，不保留裸JSON兼容。
3. 保留R1实际顶号、显式leave成功回执幂等重放、token撤销vs意外掉线恢复、停服KICK重试/ACK/socket linger、R4A时钟与AI语义。房主leave须自身成功并回大厅，其他成员可靠ROOM_CLOSED。
4. 修调用点和测试新前提：旧UDP开赛须真实setReady，不放宽生产门槛。新UdpRoom构造形状需迁移模块调用，不加旧兼容重载。reviewer由Codex迁移接线，保持断言；请交付列明所需变化。
5. 补有效模块测试：最后房间更新丢包/ACK丢失/乱序、准备取消、配置权限/实际生效、换房/顶号/关闭。不要删除或弱化原有效规则测试。

Codex正在运行 `ANTI-UDP-resume-baseline.log` 的server/client编译，反馈随时写入FEEDBACK-R4B1UDPRESUME.md；结束前必须读取并修复适用问题。

交付delivery-ANTI-R4B1UDPRESUME.md，列实际改动、未完成项、reviewer接线需要，明确测试由Codex运行。不要只提交计划，也不要把未验证当通过。
