# V4 远端传输迁移到 TCP：设计与删除计划

日期：2026-10-15（2026-10-16 更新实施与返修状态）。源码核对基线：`1664c60749cc8bf5b81196322e8f81e2f7151bdb` 加未提交 TCP/T5 工作区。状态：**MJ-TCP/1 传输已落地，UDP 已清理，当前正处于 R01–R16 统一返修与独立回归验收阶段**。

本设计确定目标为“进程内单机 + TCP 远端”，保留 V4 七模块、领域权威、业务 DTO 和客户端门面。远端传输已切换为 TCP (MJ-TCP/1 契约，12 字节固定帧头)，旧 UDP 生产链路与测试已清理；旧 TCP/V3 不恢复。历史 UDP 验收只证明原实现，不充当 TCP 验收。三大绝技定义为：落汗（TileMark）、袖里藏花（SleeveBloom）、偷梁换柱（BeamSwap）。幂等存储统一采用 `RequestScopeReceiptStore`。

## 1. 决策与范围

- 采用单条持久 TCP 连接承载一个客户端的大厅及当前玩家/观战绑定。玩家与观战角色继续互斥，私有流与公开流不混合。
- 使用 JDK NIO `SocketChannel` / `ServerSocketChannel` / `Selector`；不引入 WebSocket、QUIC 或 Netty，不依赖 JVM 虚拟线程，保持 JVM 17 与 Android minSdk 21 约束。平台兼容性由双端构建和真机/模拟器网络测试验收。
- TCP 只接管字节可靠、有序交付；业务幂等、错误分类、代次隔离、权威 deadline、重连和全量恢复仍由现有业务层负责。TCP 不保证对端业务已执行。
- 不长期维护 UDP/TCP 双栈；实施期间可使用明确的测试装配并行验证，最终出口删除远端 UDP 运行链和开关。切换前 UDP 的线上缺陷仍需评估，不能因设计完成就视为已消除。
- 本轮范围不含公网账户、撮合、持久化、主机迁移及 TLS 实现；TCP 本身不加密。初始目标继续限定局域网/受信网络，公网发布需独立 TLS 设计与验收。

依据：[RFC 9293](https://www.rfc-editor.org/rfc/rfc9293.html) 定义 TCP 为可靠有序字节流，但不提供应用消息边界；因此需要成帧，不需要再实现 UDP 分片重传。未来 TLS 如启用，副作用命令不使用 0-RTT，相关重放约束见 [RFC 8446](https://www.rfc-editor.org/rfc/rfc8446.html)。

## 2. 模块与所有权

| 模块 | 目标职责 | 不应承担 |
|---|---|---|
| engine | 现有规则、经济账本、权威与控制代次 | Socket、TCP DTO、协议序号 |
| protocol | 现有 API/DTO、ServerApiException；新增纯字节 `TcpFrameCodec` 与消息类型；传输中立时钟/调度接口 | 建立连接、线程池、房间实现 |
| client | `TcpClient` 装配、`TcpClientBinding : ServerConnection`、请求关联与连接资源；现有 ServerConnectionSession 继续管理重连和视图 | 领域裁决、服务器房间实现 |
| server | `TcpTableServer` 接入与连接管理、`TcpServerBinding` 调用共享房间服务；房间侧幂等回执存储 | 在 I/O 线程执行权威动作或监听器 |
| presentation | 组合根将 connectUdp 装配换成 connectTcp，地址/端口输入继续复用 | TCP 帧、网络线程和领域逻辑 |

TCP 连接对象拥有 channel、SelectionKey、读缓冲、写队列和未完成请求；关闭原子且幂等。服务端拥有一个 selector 事件循环与其全局预算。客户端每个 Session 拥有一个可复用 I/O 循环和调度器，重连只替换连接，不每次新建线程池。外部注入资源仍由调用方释放，内部资源由装配所有者释放。DNS/连接建立不阻塞 GL 线程。

I/O 循环仅做有界读写、帧校验、排队，不执行 JSON 大对象映射、用户回调或房间权威操作；解码/分发采用有界工作池及每连接串行邮箱，保持同连接消息派发顺序。完成 Future 和调用监听器保持在状态锁与权威队列之外。服务器全局线程与队列预算纳入 MR-07，不能以无限 cached pool 代替阻塞治理。

## 3. 新线路成帧与握手

架构仍称 V4；新的线路标识命名为 **MJ-TCP/1**，与旧 UDP Version=4 分离。实施时把传输版本与业务契约版本分开；`ApiProtocol.PROTOCOL_VERSION` 已随 D-07 v5 从 4 升到 5，旧常量不保留兼容含义（见本节版本语义）。

每帧固定 12 字节头，全部多字节整数大端序：

| 偏移 | 长度 | 内容 |
|---:|---:|---|
| 0 | 4 | 魔数 ASCII `MJTC`（0x4D4A5443） |
| 4 | 1 | wireVersion=1 |
| 5 | 1 | type，见下表 |
| 6 | 2 | flags，v1 必须为 0 |
| 8 | 4 | payloadLength，无符号，1..1,048,576 字节；先用 Long 检查范围，再分配 |
| 12 | N | 严格 UTF-8 JSON；各类型有明确 DTO，不使用运行时类名反射分发 |

没有 seq、ackSeq、CRC32、connectionId 或 FRAG 子头。TCP 内核分段不等于应用层分片；写队列保留 ByteBuffer position 直至写完，读取累计完整帧才交付。一次读取可以有半头、半体或多帧；EOF 时未完整帧不派发。错 magic/版本/flags、未知类型、非法长度、坏 UTF-8/JSON 立即关闭并清理，不扫描字节流尝试“找回同步”。错误响应仅在安全解出请求身份且连接可写时发送。

| type | 消息 | 方向及载荷 |
|---:|---|---|
| 1 | HELLO | C→S：wireVersion=1、businessVersion=2、可选 lobbyResumeToken；每连接只能一次 |
| 2 | WELCOME | S→C：版本、connectionEpoch（不重复的服务器生成字符串）、lobbyResumeToken、大厅 requestScope 与 expiresAt、预算与期限 |
| 3 | REQ | C→S：requestScope、requestId、现有 ApiRequest；外层与现有 id 必须一致 |
| 4 | RSP | S→C：相同 requestScope/requestId、现有 ApiResponse |
| 5 | PLAYER_UPDATE | S→C：ProtocolPlayerUpdate |
| 6 | PUBLIC_UPDATE | S→C：ProtocolPublicUpdate |
| 7 | ROOM_UPDATE | S→C：ProtocolRoomInfo |
| 8 / 9 | PING / PONG | 双向：有界 nonce，PONG 回显；使用本地单调时间测 RTT，不比较两机墙钟 |
| 10 | CLOSE | S→C：稳定 reason code；关闭通知为尽力而为，不等 ACK |

HELLO/WELCOME 完成前不处理业务；5 秒内未完成则断开。业务版本不匹配明确拒绝，不自动降级 UDP。TCP listener 与旧 UDP listener 不能自动协议探测互通；客户端必须更新。`connectionEpoch` 仅标识这次传输，不能用作跨连接业务去重键，也不替代 syncEpoch/controlEpoch。

**版本语义（D-07 v5）**：`TcpWire.WIRE_VERSION` 仍为 1，12 字节 MJ-TCP/1 帧格式、心跳、作用域/幂等、全量快照与重连语义不变；`TcpWire.BUSINESS_VERSION` 升为 2、`ApiProtocol.PROTOCOL_VERSION` 升为 5，承载结构化算分字段 `capCode`/`yakuEntries`/`paymentPlan` 与 capability `scoreDetailVersion`。双端对 HELLO/WELCOME 的 wire/business 版本做硬相等校验；旧 businessVersion=1 明确回 `VERSION_MISMATCH`，无同进程 v1/v2 混发、无活动房间热迁移。旧 v4 payload 只作为历史解码 fixture 存在于测试中，不构成旧端兼容承诺。

## 4. 容量、超时与慢客户端

下表为**设计初值，不是已测性能结论**。T0 测量真实四人、三绝技候选、观战和结算最大载荷后冻结；调整须同步文档、协商参数及边界测试，不可通过静默截断快照满足上限。

| 项目 | 初值及处理 |
|---|---|
| 单帧 / 单连接收帧内存 | 1 MiB 载荷 / 一个在组装帧 + 最多 64 KiB 解码暂存；不得按恶意长度立即预分配全部内存 |
| 每连接待发送 | 256 帧且总计不超过 4 MiB，含部分写出的当前帧 |
| 每连接待处理业务 | 最多 64 个在途请求；接收串行邮箱最多 64 帧且 4 MiB；超限不得无界积压 |
| 服务器网络全局预算 | 256 MiB（所有读/写/解码邮箱实际保留字节合计）；共享不可变字节按实际所有权计数；预留后再分配 |
| 连接数 / 未握手连接 | 512 / 64；超限拒绝，不能先创建无界任务；不是容量承诺 |
| 单次 selector 轮转 | 每连接最多读/写 64 KiB 或处理 32 帧后让出，避免大快照饿死其他连接 |
| 连接 / 握手 / 普通请求超时 | 5 秒 / 5 秒 / 10 秒；请求超时只代表结果未知，不撤销已受理动作 |
| 心跳 / 判死 | 3 秒 / 10 秒无完整合法入站帧；双方均执行，零散恶意字节不能续命 |
| 不完整帧 / 停滞写入 | 从首字节起 10 秒硬截止 / 连续 10 秒无写进展即关闭；不依赖 TCP keepalive 及时判死 |
| 终态收尾 | 尝试排出终态通知最多 1 秒，随后强制释放 channel；没有 UDP 的 2 秒补 ACK 任务 |

写队列满不能丢弃回执或任意删事件；标记慢连接、停止接纳新请求并断开，客户端通过恢复快照继续。已受理副作用继续由账本记录，网络关闭不能释放在途业务身份让重复请求再执行。新请求准入不足优先返回可重试 `SERVER_BUSY`（拟新增码，实施时核对），连错误帧也无法排入则直接断开。

心跳/关闭帧可使用独立保留的少量控制队列（最多 8 帧、8 KiB，计入全局预算），只能在完整帧边界插入，不能插入正在写出的帧。已排队 RSP/UPDATE 不因优先控制帧改变业务相对顺序。启用 TCP_NODELAY；效果由测量确认，不承诺消除弱网排队。

## 5. 业务请求幂等与跨连接恢复

MR-03 的 FIFO、准入前预留、跨 API 冲突校验、完成后比较删除、在途副作用不因网络超时解锁等语义必须迁移，不能把 TCP “可靠”理解成业务 exactly-once。

幂等键为 `(logicalPrincipal, requestScope, requestId)`；会话中的玩家/观战 principal 来自服务端验证的恢复身份，大厅 principal 来自 HELLO/WELCOME 的随机恢复 token。**不能用源 IP/端口、Socket、connectionEpoch 作为跨连接唯一键**。握手返回大厅 token 后才能建房，因此 CREATE_ROOM 回包丢失时仍有恢复查询身份；首次 WELCOME 丢失不会发生建房副作用。

同键同载荷重放原结果或关联原在途 Future；同键不同 API/载荷返回 `REQUEST_ID_CONFLICT`。请求身份是跨 API 的，不能按方法名拆成可重复执行的多个键。认证失败不能读取别人的历史结果。恢复 token 不写日志；本轮无持久化，服务器进程重启后旧 token 明确失效。

回执查询与普通读取分开预算。普通读取可重新发起；出牌、出千、建房等副作用在超时/断线后保留原 requestId 和原 payload，先恢复身份再以原身份请求结果。不得自动生成新 ID 重做；失效的 decisionId 按既有权威规则拒绝，不延长 deadline。

为明确有界缓存与过期边界，新增服务端颁发 `requestScope`：绑定 logicalPrincipal、签发时间、失效时间，不属于传输连接；恢复必须继承仍有效 scope。初始有效期 10 分钟，接纳时校验，不接受客户端自行声明的任意 scope。已接纳请求保留身份至最终完成，完成结果至少保留至 scope 失效；副作用记录容量满则**受理前拒绝**，有效期内不 FIFO 淘汰副作用回执。到期 scope 先查询仍保留的原结果/在途记录，仅允许关联原结果，绝不重新执行；无记录时返回 `REQUEST_SCOPE_EXPIRED`（拟新增码）。客户端把不明结果显示为需对账，不转成新请求自动执行。新 scope 只供后续新动作，不能复用旧未知请求。scope 元数据保留在已验证逻辑会话中，未知 scope 直接拒绝，无需无限 tombstone。

WELCOME 颁发大厅 scope；玩家/观战绑定 RSP 外层另携带角色 scope 与 expiresAt，现有 RoomBinding/SpectatorBinding 领域内容不塞入传输字段。新增只对已认证逻辑身份开放的 `GET_REQUEST_SCOPE` 控制查询：返回当前仍有效 scope，过期后按需创建一个新 scope，同身份并发调用返回同一个结果；不创建房间、不执行游戏动作，按查询速率限制，不要求一个已有效 scope 才能调用。普通 REQ 必须有 scope；scope 查询仅凭已完成握手及当前绑定身份准入，不能借此切换 principal 或绕过副作用容量。其载荷/错误码在 T0 冻结，并对 InProcess 提供等价的内部回执管理入口，不能形成两种业务幂等规则。

初值：每逻辑成员最多 512 个已受理副作用记录（含在途），在途不超过 64；每 scope 结束且所有在途完成后回收。多 scope 合并计入同一预算，不能轮换 scope 绕过上限。大厅初值副作用总记录 1024、查询缓存 256，真实内存另受全局字节预算约束；T0 依据 MR-03 与一场完整对局动作量复核。当前 MR-03 具体 TTL/容量不是 TCP 已实现值，迁移验收必须覆盖两者差异。

断线时客户端 pending Future 在明确超时/关闭边界完成为结构化未知结果错误；可恢复的请求身份由有界恢复记录持有，不用永不结束的 Future 承载整个恢复过程。恢复成功后重试原请求可取得已完成回执；服务端尚在执行时关联同一结果。业务 receipt 的 APPLIED/RECORDED/REJECTED 与 TCP 写完成明确分离。

## 6. 推送、基线与重连

- 保留完整快照 + 可见事件、独立玩家/公开 cursor、roomVersion、sessionId、syncEpoch、controlEpoch。保留 MR-01 的身份验证、监听器间换绑检查、统一锁顺序与锁外回调。
- 单连接 TCP 入站没有网络乱序；新绑定不再建立 UDP 网络重排/补包窗口。但请求响应、权威异步查询和 UI 延迟回调仍可能交错，版本与代次检查不能删除。
- 新建/恢复绑定在同桌串行队列内原子获取基线并安装订阅；将包含明确完整基线的 RSP 排到该连接的业务输出队列后，才释放后续同流 UPDATE。客户端先采纳绑定基线再排水早到消息；早期缓冲仍限 64 条且 4 MiB，溢出走一次合并的全量恢复。
- TCP 不保留服务端 256 条**网络重传**窗口，不发送 RESEND/ACK。中断后调用现有 resume/snapshot/publicSnapshot 业务接口获取全量基线；重连不承诺重放断线期间全部动画。结算、账本结果使用其业务查询接口核对，不能仅靠瞬时动画事件保存结果。
- 同流 UPDATE 游标回退/重复丢弃，出现缺口先停派普通事件并合并一次全量查询，明确 full 基线后再继续；非法身份立即隔离。并发全量查询必须有 token/epoch 门控，旧恢复响应不能覆盖更晚完整推送。
- 新 TCP 连接认证后在房间权威队列中替换绑定代次，旧连接命令即使已在队列中也再次验证，旧 Future/回调不得更新新 UI。重连不自动 claimControl。
- 保留 MR-02 分类器：typed 永久原因终止，未知/网络故障有界退避；500/1000/2000/4000/8000ms 封顶，最多 10 次失败。TCP EOF/RST 本身不是“token 无效”；CLOSE 丢失后由恢复请求确认是否永久失效。
- 不实现 UDP seq 高水位换连接。业务 Long 游标/代次仍需拒绝溢出：注入边界测试，达到上限时显式终止该业务流/会话并要求新身份，不能在原身份下回绕；不因新传输自动重置控制代次。

## 7. 代码删除与迁移清单

以下是**实施时动作**，本轮只修改文档。删除前须完成引用审计与替代测试；不按包名批量删除。

| 当前文件/组件 | 目标动作 | 前置条件/保留内容 |
|---|---|---|
| protocol `transport/udp/UdpFrame.kt`、`UdpPacketType.kt`、`UdpProtocol.kt` | 删除 UDP 帧、ACK/FRAG/RESEND、MTU/RTO/序号常量 | TCP codec/type/limits 独立实现；仍被业务使用的 reason code 迁入稳定 API 错误定义 |
| `Fragmenter.kt`、`ReplayGuard.kt`、`PushRetransmitWindow.kt` | 删除 | UDP 运行链退役；保留业务请求幂等与游标隔离 |
| `ReliableRequestor.kt` | 删除 UDP 重试、rspSeq→requestId 和补 ACK 缓存 | 提取 TCP 有界 pending 请求与超时关联；MR-03 在途/回执约束迁至服务端账本 |
| `PushSequencer.kt` | 拆分后删除 UDP 专用版本 | 先保留/迁移通用 cursor、重复抑制、完整基线规则；移除网络乱序和 RESEND 分支 |
| `HeartbeatPacer.kt` | 替换 UDP 专用封包/RTO逻辑 | TCP 心跳复用已验证的超时语义；迁移时钟测试 |
| `UdpClock.kt` 中 UdpClock/UdpScheduler/Cancellable | 迁至 `transport.common` 并中性命名，不整文件直接删除 | Session、服务端与测试都有引用；保留注入与所有权契约 |
| client `transport/udp/UdpClient.kt`、`UdpClientBinding.kt` | TCP 替代验收后删除 | binding 身份/生命周期与 API 方法迁移完成，不把上千行 UDP 实现原样改名 |
| client `ScheduledExecutorUdpScheduler.kt` | 移动并改为传输中立名称 | 保留 MR-02 内建释放、注入不越权关闭、可观察关闭验证 |
| server `udp/UdpTableServer.kt` | 提取路由/大厅准入/业务回执后删除 | TCP 不复制重传记录、ACK跟踪、unattachedOutboundSeq、terminated UDP 重发缓存 |
| server `udp/UdpClientSession.kt`、`UdpRoom.kt` | 提取业务绑定校验/逻辑身份/幂等后删除 | 保留 MR-03 FIFO/容量/冲突与 MR-02 错误映射；不能删除共享房间状态 |
| ServerConnectionSession.connectUdp、MahjongGame 装配 | 改为 connectTcp，删除最终 UDP 开关及工厂 | UI 不感知帧协议；单机 InProcess 继续使用 |
| ReconnectErrorClassifier 的 UdpFrameException 分支 | TCP 切换后移除，统一 typed 网络/协议错误 | 不复活 message.contains 分类 |
| 旧 UDP tests、MR-04/05 探针 | 纯分片/seq/ACK测试在 UDP 退役提交中删除 | 对每个业务断言提供 TCP 对应测试；MR-01/02/03、136 张守恒、规则与计时测试保留 |
| 历史 UDP 验收报告 | 保留，标历史 | 不改写历史结果为 TCP 通过，不复制旧 TCP/V3 兼容壳 |

## 8. 替代任务与实施门槛（尚未启动）

| 阶段 | 交付 | 验收出口 |
|---|---|---|
| T0 契约冻结 | 载荷/预算测量、当前基线、业务测试映射、requestScope 对账语义、版本与错误码确认 | 真实最大载荷能容纳，所有新增行为有用例；不凭经验宣称性能改善 |
| T1 protocol | MJ-TCP/1 帧 codec、握手 DTO、中性时钟接口 | 任意切分读取、连续多帧、超限长度/坏版本/EOF、预算释放；不启动 TCP 默认入口 |
| T2 连接与服务端 | selector、部分读写、pending 请求、心跳/关闭、业务路由和身份回执存储 | 先预留后执行、同键冲突、队列饱和、断线后原请求查回结果、在途超时不重复执行 |
| T3 会话接入 | TcpClientBinding、玩家/观战/大厅恢复、组合根装配 | MR-01/02/03 行为等价；不自动收回控制权；无私有信息泄露 |
| T4 独立验收与切换 | 全模块、独立业务审查、双端构建/GL、真实断网与慢端测试、资源基线 | 修复所有阻断项；客户端/服务器配套升级；停止旧服新建房，结束现有房间后切换 |
| T5 删除 | 按第 7 节删除 UDP 代码/纯传输测试，消除依赖与开关 | 全量引用审计 + 重跑受影响测试；发布物仅 InProcess/TCP，无旧 TCP/V3 回流 |

MR-04/05 的原 UDP 实现取消，由 T1/T2/T4 覆盖其有效目标。MR-03 已提交的业务保证继续执行；MR-06 的空房/观战/建房预算与 MR-07 的工作池/慢端治理继续保留，按 TCP 入口重定范围。D-08 改为 TCP 路由与逻辑回执管理，不再拆 UDP 可靠发送追踪。MR-10 动态 UDP RTO 从候选中删除。

发布不在活动房间中热切换传输，回退使用上一版本发布物与配套客户端，重启创建新房；无持久化，不能承诺原进程房间与 token 跨版本恢复。不把“暂留双栈便于回退”变成永久兼容维护。

## 9. 必须通过的验证矩阵

1. Codec：每一个头部切点、逐字节体、连续 3 帧、最大长度与最大+1、零/0xffffffff、坏 magic/version/type/flags/UTF-8/JSON、部分帧 EOF、慢速灌入、缓冲归还。
2. 请求：部分写后断线；服务器执行前/后断线；RSP 写完但客户端未应用时断线；原 requestId 恢复；跨 API 冲突；scope 过期与容量满；在途副作用尚未完成时重复请求；客户端 Future 有界完成。
3. 流：绑定 RSP 与首 PUSH 相邻、角色互换、监听器内 close/rebind、早期 65 条溢出、旧查询迟到、游标缺口、全量恢复失败后重试、private/public 分离、结算结果补查。
4. 资源：玩家/观战各至少 3 次真实中断，旧 socket、selection key、buffer、pending、timer 全部回收；Session 共享调度器存活至终止；TCP ESTABLISHED/应用持有资源回落。TIME_WAIT 单独计数为内核正常收尾，不能等同应用 socket 泄漏或要求立即为零。
5. 负载与关闭：慢写端/慢监听器/工作池饱和不得阻塞别桌 deadline；停止服务时未决请求明确失败；高水位队列不无限增长。MR-07 的 1/10/100 房间测量独立记录，不以本次六次重连替代。
6. 回归：四模块、独立 V4 业务矩阵、三绝技经济、136 张守恒、真实桌面与 Android 建连/后台恢复；不把原 UDP 专用测试删掉后减少的数量当作性能或质量收益。

本次设计交付只做文档一致性/引用检查，不运行 Gradle、不派发开发、不删除源码、不提交或推送。后续实施须以此设计冻结的契约与实际验收作为删除门槛。

## 10. 用例集与检视补充（2026-10-15 追加）

- 设计用例集：[TCP-MIGRATION-USECASES.md](TCP-MIGRATION-USECASES.md)——把 §9 验证矩阵展开为 UC-F/H/R/S/L/C/M 可执行用例，作为 T1–T5 各批任务的行为规格与验收清单。
- 检视与冻结决策：[TCP-MIGRATION-REVIEW.md](TCP-MIGRATION-REVIEW.md)——对照基线 `3a382c6` 的核对结论。其 **D1–D13 冻结决策修订本文相应表述**（要点：同键冲突沿用现有 `CONFLICT` 不新增 `REQUEST_ID_CONFLICT`；`SERVER_BUSY` 已存在；新增 `REQUEST_SCOPE_EXPIRED`；CLOSE 码表与 PING nonce、WELCOME/GET_REQUEST_SCOPE、REQ/RSP 信封字段冻结；时钟迁移拆 T1+T5 两步）。**与本文冲突处以冻结决策为准。**
- 用户已授权按 T1 → T2 → T3 →（T4 主会话验收）→ T5 顺序派发 Anti 开发（文件工具约束、验收由 Claude Code 主会话执行）；T0 剩余测量项见检视报告第四节，须在 T2 验收前关闭。
