# 并行A：UDP服务端接入与生命周期

用户授权多个agent并行，Anti优先，额度耗尽由Codex切Claude/GLM。你只实现本任务范围。之前串行R4B1UDPRESUME已停止，部分GLM代码保留；ROOM已接受23专项/server91。当前UDP未接受。

读CONTRACT.md、TASK-ANTI-R4B1UDPRESUME.md、FEEDBACK-R4B1UDPRESUME.md及本任务反馈FEEDBACK-R4B1UDPSERVER.md（若有）。不要重新开发已接受领域/ROOM。

## 独占所有权

- server/src/main/kotlin/com/majiang/server/udp/**
- server/src/test/kotlin/com/majiang/server/udp/**（及确有必要的新UDP server模块测试）
- 必要时修改server/room共享服务及其测试；如共享类型改变，允许最小修正server/inprocess消费者并保留ROOM语义。
- 不改protocol、client、reviewer、构建配置或其他agent交付。协议固定如下，有问题写反馈文档给Codex，不私改公共契约。

## 固定交接契约

协议使用现有严格`ROOM_UPDATE`信封，`ProtocolRoomInfo.version`为房间版本，独立于对局cursor。UDP包类型PUSH、ACK，客户端ACK回指收到的完整消息seq。服务端房间更新重传必须保持稳定消息seq；对房间更新分片使用同一原消息seq，不调用每片生成新seq的路径，否则ACK无法清除原在途记录。其他包现有可靠性不要回退。

客户端agent负责重复/过旧seq的有效ROOM_UPDATE也ACK、房间排序与绑定隔离。你负责按ACK清理、每连接仅最新完整房间视图有界在途、重试/兜底、连接退出和停服清理；避免旧版本并发回调覆盖新版本待发。

## 要完成

1. 修缺失的3个ROOM_UPDATE重试常量等编译问题。
2. UdpRoom/服务端真正使用SharedRoom/RoomRegistry，ready/config/start/leave/resume都走共享业务，执行期校验绑定；不复制准备/成员/规则。
3. 保留R1离房回执重放、顶号、令牌撤销vs掉线恢复、终端KICK丢包/ACK/停服linger和R4A规则/时钟/AI。
4. 特别检视UDP resume：当前在SharedRoom Future的thenApply中读全局generation并无条件installBinding，两个并发resume的完成顺序可能把旧绑定装回。将generation与绑定安装顺序纳入权威序列或原子校验；缓慢kick/通知不得让较旧完成覆盖最新恢复。补有意义回归。
5. 迁移UdpRoom模块构造/真实ready开局前提，保留断言；reviewer已由Codex迁移，禁止修改。

只用文件工具，不shell/构建/提交/再委派。最终读取本任务反馈；交付delivery-ANTI-R4B1UDPSERVER.md。不推进B2/R5/R6，不声称没跑的测试通过。
