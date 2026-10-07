# N：真实联网传输与房间服务

D已经完成TableHost，D2正在修正实现，接口保持。E即将机械迁移纯源码；engine/client的现有类若尚未在新目录找到，先从core/src/main读取同包文件。现在开始实现新增network/server文件，不会与迁移冲突。所有权：client/src/main/kotlin/com/majiang/network/**、server/src/main/kotlin/**、client/build.gradle、server/build.gradle中新增网络依赖。不能改engine、GameClient、Screen、测试。E已交付且不再修改Gradle。

目标：真实Socket往返复用TableHost/SessionAccess，不用内存代理假装RemoteSession。实现一份服务器供大厅和自建房复用，以及两种统一SessionAccess网络适配。桌面和Android客户端不得依赖server或UI。

## API

server包 `TableServer(bindHost:String="127.0.0.1", port:Int=0)` : AutoCloseable，start()，val boundPort:Int；每房间TableHost，房间成员稳定seat，默认房主seat0，其他空位可AI补齐，加入比赛后不得任意覆盖别人的座位。支持CREATE_ROOM/JOIN_ROOM/LIST_ROOMS/RESUME，随机不可猜resumeToken绑定room+seat+连接代次。房间ID随机；不能仅凭客户端提供seat获得他人身份。

client network包统一`NetworkSessionAccess : SessionAccess`，`RemoteSession` 与 `MallSession`使用相同实现只表示发现/部署模式，工厂CompletableFuture异步连接，不能在UI线程阻塞。建议：
- `RemoteSession.create(host:String,port:Int,playerName:String, config:MatchConfig=...):CompletableFuture<RemoteSession>`
- `RemoteSession.join(host:String,port:Int,roomId:String,playerName:String):CompletableFuture<RemoteSession>`
- `RemoteSession.resume(host:String,port:Int,roomId:String,token:String):CompletableFuture<RemoteSession>`
- MallSession对应工厂，及listRooms返回可见房间摘要。
客户端公开只读roomId/resumeToken供UI安全保存，不打印token日志。实现所有SessionAccess方法、绑定roundId的MahjongGameAccess，不能把旧局submit转发当前局。

## 传输与运行

采用有长度上限的UTF8 JSON帧 over TCP，本次限定LAN/回环可验证的transport。每连接I/O线程与异步请求future，相关回执ID，服务器事件推送；捕获关闭和I/O失败使pending futures完成异常，不无限等待。心跳/读超时触发disconnect，重连RESUME调用宿主reconnect并可requestControl(false)。close幂等、关闭接受循环和连接/调度任务。

可使用已缓存Gson2.11.0作为JSON解析/编码库，并使用显式白名单type adapters处理GameAction/GameEvent/其他sealed类型；严禁ObjectInputStream、网络Class.forName或任意类反射标签。请求从JsonObject显式读取并验证必填字段、长度、版本，Kotlin非空默认不可依赖Gson自动校验。服务端仅接收白名单操作和ActionChoice，客户端不得上传GameState或内部GameAction系统命令。编码Snapshot与Event必须先经过宿主按座位投影。

Gson官方文档提醒Kotlin默认参数/非空不自动支持，Android混淆有限制（https://google.github.io/gson/）；此处不采用开放反射输入模型，必需字段解码后显式验证，类型标签使用固定映射。若不能完整可靠编解码复杂现有DTO，提供显式适配而不要丢弃事件/结算字段。库版本集中root gradle.properties可提出补充交Codex，不擅自修改未授权构建配置。

单桌tick使用server调度独立于任何客户端动画，建议50ms节拍，host保证150ms自动动作节奏；server自身仍不可直接改Round/Session。慢连接使用有界发送队列/断开恢复，不能在host订阅回调里做阻塞socket写，使一个客户端拖住整桌。

订阅/查询快照握手必须一致，初始推送游标与resumeEpoch正确，多个客户端只得到自己的手牌。token重复登录应废止旧连接或明确拒绝，旧连接断开不能将新连接的座位再次标为掉线。

加入/准备/开局流程：创建房间后配置与身份冻结点明确，比赛未开始可以加入空位；开局后只允许本人token恢复，不能以join顶替AI/真人原席位。host.nextRound已有ready语义，服务器只转发绑定身份。

提供server mainClass启动入口和`server:run` Gradle任务，参数port/bind，文档命令可本机复现。支持cleartext只作为LAN能力如实说明，公网TLS认证和持久化不声称已完成。

Codex负责真实loopback端到端、多客户端隐私、请求去重、断线接管/恢复、未知消息/超长帧拒绝与端点关闭测试。交付delivery-N.md写明方法签名、如何运行、限制。
