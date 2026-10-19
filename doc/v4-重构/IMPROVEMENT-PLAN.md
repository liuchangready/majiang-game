# V4 代码分析复核与改进实施计划（最终合并版）

更新日期：2026-10-15（TCP 目标设计；当前源码仍为 UDP）  
分析基线：f3ad037（旧千术及旧参数体系清理之后）  
状态：MR-01/MR-02 已验收并推送；MR-03 已提交 `3a382c6`（当前未推送）。下一阶段以 [TCP 迁移设计](TCP-MIGRATION-DESIGN.md) 为准，仅修改文档，尚未开发；MR-04/MR-05 原 UDP 实施取消。
执行分工：Anti 开发，Codex 独立复核、测试与验收；问题退回 Anti 返修后复验

本文件是实施与验收的统一入口。2026-10-15 起远端目标调整为 TCP，传输细节以 [TCP 迁移设计](TCP-MIGRATION-DESIGN.md) 为准；已完成 MR 的 UDP 描述保留为历史范围。当前目录不存在 v2 文件，历史合理结论已合并，不再保留失效链接。

| v2 补充 | 最终处理 | 落点 |
|---|---|---|
| PATCH-1 大厅全局缓存 | 纳入；补充保留期限、跨池冲突和容量准入，不承诺无限期命中 | MR-03 |
| PATCH-2 线程池收敛 | 纳入；修正执行器统计，先测量再定预算，补充通道容量、公平性和慢回调隔离 | MR-00、MR-07 |
| PATCH-3 字体共享 | 纳入独立子任务，保持字号、布局及资源所有权 | MR-08d |
| PATCH-4 历史失败定位 | 纳入实测结果；纠正第一项测试的默认配置归因，保留关闭与排队任务的分别验证 | 第 6.2 节 |
| PATCH-5 分片完成缓存 | TCP 目标下取消；业务幂等与结果重放保留 | T2；MR-04 退役 |
| PATCH-6 大类拆分 | 纳入 D-08/D-09；保留不同推送语义、统一回执及事务边界 | 第 9 节 |
| PATCH-7 未绑定建房治理 | 纳入可配置准入；不把单 IP 配额当来源验证，补全局预算与驱逐后的幂等结果 | MR-06 |

## 1. 结论与目标

五份原始分析提供了有价值的维护线索，但混合了真实缺口、过时描述和未经测量的推断。不能直接按原文的 P0/P1 路线图开展整体重写。

本计划按“复现与基线 → 正确性修复 → 资源治理 → 局部拆分 → 测量驱动优化”推进。目标是降低换绑、重连、弱网和长期运行的风险，同时保留已经验收的麻将规则、千术语义与 V4 边界。

成功标准：

1. 每个修复对应明确的触发条件和自动化回归用例。
2. 玩家/观战切换不会派发旧绑定数据；早期缓冲始终有界。
3. 重复报文、重试、缓存淘汰和序号生命周期具有一致契约。
4. 废弃房间、观战成员和后台任务有明确的容量与回收边界。
5. 拆分组件后保持输入输出、事件可见性、扣费和权威时钟行为一致。

## 2. 分析依据及证据等级

原始分析：

- [engine 分析](majiang-engine-architecture-analysis.md)
- [protocol 分析](protocol_architecture_and_review.md)
- [client 分析](client_architecture_and_review.md)
- [server 分析](server_architecture_and_review.md)
- [presentation 分析](presentation_architecture_and_review.md)

约束依据：[AGENTS.md](../../AGENTS.md)、[V4 架构](../GAME_ARCHITECTURE_V4.md)、[TCP 目标契约](TCP-MIGRATION-DESIGN.md)。

| 等级 | 含义 | 对应处理 |
|---|---|---|
| A：已核实事实 | 当前源码或定向探针直接支持 | 可据此设计修复，但仍需验证业务影响 |
| B：风险待复现 | 有危险结构，尚未证明可达故障 | 先补确定性并发/故障注入测试 |
| C：收益待测量 | 分配、延迟、线程或带宽优化假设 | 先采集基线，再决定是否改造 |
| X：失效或错误结论 | 与当前源码、契约或探针结果冲突 | 从实施任务中排除，修正文档 |

本次复核包含源码检查与使用现有编译类的本地 Java 探针；没有做负载压测。上一轮清理验收的 1,224 项模块测试、61 项选定 V4 审查是历史验证记录，不能替代本计划各阶段的重新验收。

### 2.1 必须纠正的原始结论

| 原始结论 | 复核结果 | 实施决定 |
|---|---|---|
| V4 AI 在主事务内同步执行，尚需异步化 | X：TableAuthority 设置 autoPlayAi=false，ServerTableHost 使用独立执行器推理，回传结果带决策身份 | 保留现有模型；不再做一次“AI 异步化” |
| coordinator 对模块外直接公开，测试访问证明生产权限绕过 | X/B：coordinator 已是 internal；decisionClock 等剩余公开面仍可审查 | 只审查真实生产引用，不以同模块测试访问作为越权证据 |
| UseCheatTechnique、怀疑度事件仍需统一或清理 | X：已删除；旧参数类型也已清理 | 不复活旧入口，不把新千术改回 Any/参数字典 |
| AI 无法参与码牌 | X：已有 aiMaySpend 及计算机座位花费测试；托管真人不自动花费有意保留 | 不改变托管经济规则 |
| GameState 四个复制方法分别维护全部字段 | 部分错误：deepCopy/copyInto 已委托 copyFrom；reset 仍独立 | 保留字段完整性审计，避免夸大重复代码量 |
| 每次正常自动重连泄漏一个 scheduler | X：connectUdp 复用 Session 所有的调度器并在终止时关闭 | 仅修复底层直接调用默认参数时的所有权陷阱 |
| 四片报文两次重传就占满八个重组槽 | X：槽按消息计数；初发加两次重传占三个槽 | 验证重组尝试浪费与并发条件，不按“必然死锁”修复 |
| 低序号分片最后到达必然被防重放拒绝 | X：ReplayGuard 接受窗口内未见乱序序号，RSP 还有独立请求关联路径 | 分包类型验证，保留乱序支持 |
| 对序号做 0x7FFFFFFF 掩码即可处理回绕 | X：先得到 0，之后的小序号仍被旧窗口拒绝 | 设计换连接/代次的完整生命周期 |
| ApiResponse 改成 data class，init 校验即可替代包装 | X：校验不阻断传入列表后续修改 | 保留防御性复制，禁止削弱不可变契约 |
| SharedRoom 与 ServerTableHost 各有一套权威串行队列 | X：SharedRoom 复用 tableHost.enqueueOperation | 保留每桌串行，不另建一套队列 |
| 两个重传窗口预占两份完整 256 条缓存 | X：内部窗口按需创建 | 不以“节省一半窗口内存”为拆类依据 |
| 生产 KICK 重试阻塞等待 ACK | X：生产实现通过 scheduler 重试并返回 Future | 真实风险是执行器容量与发送阻塞边界 |
| presentation 依赖 server 违反当前架构 | X：MahjongGame 是被允许的组合根，已有自动边界扫描 | 维持当前七模块；组合根迁移不列为缺陷修复 |
| 所有中文面板依赖静态字形，尚未增量加载 | X/局部成立：UiAssets 和结算字体已增量加载；结算仍独立持有生成器，ShufflePreparationPanel 仍静态 | MR-08d 统一字体所有权，保留布局并清理无生成用途的字表 |
| FakeServerConnection 被打入 protocol 生产包 | X：文件位于 src/test | 不做不存在的生产测试桩迁移 |

定向探针记录：四片消息三次不完整发送占槽依次为 1/2/3，推进 3 秒后为 0；ReplayGuard 接受 3 后接受 1；在 Int.MAX_VALUE 基线后，掩码所得 0 和后续 1 均被拒绝。实施时将这些行为转为 protocol 模块正式测试，不依赖 build 目录中的临时探针。

### 2.2 保留的问题清单

本计划 P1 表示当前应优先验证/治理，P2 表示维护改进，P3 表示条件性优化；尚无依据将整批问题认定为 P0 线上事故。

| 编号 | 问题 | 证据 | 优先级 | 对应任务 |
|---|---|---|---|---|
| C1 | 玩家早期推送队列无界，派发缺少绑定身份封装 | A：UdpClientBinding.earlyPushBuffer/dispatchUpdate/onUpdate | P1 | MR-01 |
| C2 | 玩家/观战绑定方法存在反向嵌套锁顺序 | A 结构、B 可达死锁：syncLock→publicSyncLock 与反向路径同时存在 | P1 | MR-01 |
| C3 | 多处重置逻辑重复；部分字段清理不同 | A 差异、B 后果：activePublicHealToken 已有 epoch 防护，不能仅凭未清理认定污染 | P1 | MR-01 |
| C4 | 重连永久错误依赖 message.contains | A：ServerConnectionSession.doReconnect | P1 | MR-02 |
| C5 | 底层 UdpClient 默认调度器的所有权不明确 | A：connect 默认创建但默认不关闭 | P2 | MR-02 |
| N1 | 请求完成缓存、会话幂等缓存及大厅全局缓存按哈希顺序淘汰 | A：ReliableRequestor、UdpClientSession、UdpTableServer 的 lobbyCompletedRequests/terminatedSessions | P1 | MR-03 |
| N2 | UDP 分片重试与完成记录复杂度 | 当前 UDP 历史风险，迁移前仍存在 | 迁移 | T1/T5，取消 MR-04 原实现 |
| N3 | UDP 出站序号耗尽 | 删除传输 seq/ackSeq，保留业务代次边界审计 | 迁移 | T4/T5，取消 MR-05 原实现 |
| S1 | 房间无空闲 TTL、观战人数无上限，未绑定建房缺少细化准入预算 | A：已有 UDP 房间上限 100；攻击速率与部署影响未测量 | P1 | MR-06 |
| S2 | 每房间多个执行器与派发队列缺少整体预算；公共更新重复编码 | A 结构、C 实际线程/队列/编码开销；不认定空房必有固定线程数 | P2 | MR-07 |
| E1 | 立直状态双份存储，权威回执无固定容量，预览可能重复计算 | A 结构、C 性能；领域幂等不能直接 LRU | P2 | MR-09 |
| U1 | GameScreen/MenuScreen 大，计时/模态逻辑多处维护 | A 结构、B 具体回归风险 | P2 | MR-08、D-04/D-05 |
| U2 | ClassicSkin 旋转能力不同；结算与码牌组件独立持有字体生成器，部分字表无生成用途 | A；字体复用需保留布局和生命周期 | P2/P3 | MR-08d、MR-10 |
| M1 | UdpTableServer、TableAuthority、ApplicationCoordinator 混合多个可分离职责 | A 结构；逐项明确状态归属后拆分 | P2 | D-08、D-09 |
| PERF1 | 深拷贝、享元、压缩的收益；UDP 动态 RTO 已取消 | C | P3 | MR-10 |

主要源码入口：

- [客户端会话](../../client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt)、[UDP 绑定](../../client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)、[UDP 装配](../../client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt)。
- [可靠请求](../../protocol/src/main/kotlin/com/majiang/transport/udp/ReliableRequestor.kt)、[分片](../../protocol/src/main/kotlin/com/majiang/transport/udp/Fragmenter.kt)、[防重放](../../protocol/src/main/kotlin/com/majiang/transport/udp/ReplayGuard.kt)。
- [服务端会话](../../server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt)、[房间](../../server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt)、[UDP 服务](../../server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)。
- [领域权威](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)、[领域状态](../../engine/src/main/kotlin/com/majiang/state/GameState.kt)、[共享字体](../../presentation/src/main/kotlin/com/majiang/presentation/common/UiAssets.kt)。
- [服务端权威宿主](../../server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt)、[应用协调器](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt)、[结算渲染](../../presentation/src/main/kotlin/com/majiang/presentation/common/SettlementDetailRenderer.kt)、[独立宿主审查用例](../../tools/architecture/v4-review/V4ServerHostAcceptanceTest.kt)。

## 3. 不可改变的契约

1. engine 不依赖 protocol/client/server/libGDX；client 不依赖 engine/server 实现；UI 业务仅通过 client.api。
2. 每桌所有权威突变串行；AI 推理在队列外，结果提交校验 roundId、decisionId、controlEpoch；deadline 优先。
3. 136 张物理牌守恒；落汗、袖里藏花、偷梁换柱的窗口、候选、费用和托管语义不变。
4. 玩家私有流与观战公开流隔离；投影不得泄露暗牌、牌山、私人账本或未授权里宝牌。
5. 推送继续携带完整快照，基线重置明确 isFullSnapshot。64 接收缓冲/256 去重/256 重传窗口是当前 UDP 实现；TCP 保留有界早期缓冲、游标与代次校验，删除网络重传窗口，断线使用完整基线恢复，不改变有效业务与可见性保障。
6. 同一请求重复不重复执行；同 ID 不同业务载荷冲突；超时/失败不能隐式退款、重置预算或增加出千资源。
7. 不删除有效测试，不通过放松断言、延长 sleep 或忽略异常让构建通过。
8. 单纯内部重构保持线路契约不变。TCP 迁移是明确的 MJ-TCP/1 线路升级，配套更新两端，声明错误码/预算/恢复差异，不静默复用 UDP v4 帧定义。

## 4. 具体方案与交付拆分

下述组件名为建议的新内部实现，并非当前已存在的 API。

### MR-00：固定基线、复现与观测入口

范围：测试与审查记录，不改产品行为。

- 固定开发起点 SHA，记录当时未提交改动；建立独立分支，禁止夹带截图工具、历史文档删除等其他工作。
- UDP 专用探针保留为历史基线，不再扩建；新增 TCP 成帧、背压、幂等与恢复边界测试。
- 使用 FakeClockAndScheduler、可暂停执行器和屏障构造竞态，不依赖随机 sleep。
- 建立最小指标：队列长度/溢出次数、自愈请求数、重组占槽/超时数、房间/观战数、缓存条目数、未完成后台任务数。不得记录恢复 token、完整私有快照或暗牌。
- 对 1/10/100 房间分别记录新建空房、活跃对局、慢消费者三类负载的实际线程、队列条目/字节、任务等待时间和回收后残留；区分执行器对象数量与已启动线程。冻结 MR-07 的预算表和性能对照输入。
- 按第 6.2 节复核已复现失败；将关闭自身的状态变化与关闭后排队命令是否执行分开验证，不以调整初始阶段或放松断言代替定位。
- 运行标准模块测试和完整独立审查，分类记录通过、新失败、已知基线失败。

完成条件：后续每个风险有可执行的测试入口；复现不了的条目标为 B，不写成已确认故障。

### MR-01：玩家推送门控与绑定状态切换

范围：client 的 UdpClientBinding、ServerConnectionSession 及其测试；第一批最高优先级。

方案：

1. 引入 PlayerDispatchItem，携带入队时的 bindingEpoch、roomId、sessionId、syncEpoch、viewerSeat 和 update。派发前及每个监听器回调之间验证当前身份；终止、换房和换角色后不再派发旧 item。
2. earlyPushBuffer 上限 64。溢出时记录需要全量恢复，停止累积旧批次；监听建立后只对当前绑定发起一次恢复。直到拿到明确全量基线前，不伪造“连续更新”或直接把最后一包当普通续包。
3. 将状态切换收口到一个锁内方法，但区分“新绑定”“恢复同一会话”“离房”“关闭”。只有属于旧身份的缓存被清除；握手期间属于新身份的早到数据必须保留或通过基线恢复，不能一律 clear。
4. 统一跨玩家/观战流的锁顺序为 syncLock → publicSyncLock；需要同时访问两者的代码只能经同一辅助入口。先审计 stateLock/roomLock 的参与路径，确保没有引入新的锁环。
5. 锁内仅做身份判定和状态写回；监听器、网络调用、Future 完成在锁外执行，并再次检查 epoch。不要以 AtomicReference 替换所有字段后就宣称完成原子切换。
6. activePublicHealToken 继续保持 epoch/token 比较及 CAS 完成语义，迟到旧恢复不能清除新恢复标记。

验收场景：

- 无监听器连续接收 65/256 个有效推送，缓冲不超过 64，恢复不并发洪泛，最终快照等于权威快照。
- 监听器 A 回调中离房或换绑，监听器 B 不再收到原绑定的数据。
- 延迟旧自愈响应、旧建房响应和旧离房响应，不覆盖新绑定。
- 玩家→观战→玩家及重连交错，使用屏障验证不会死锁，两个角色缓存互斥且游标不回退。
- 丢包后中间动画可以按全量恢复契约跳过，动作不能重复执行或重复扣费。

退出条件：上述测试通过后再考虑抽出流同步组件；本 MR 不合并上下两层重排算法。

### MR-02：结构化错误与调度器所有权

范围：protocol 的共享 API 错误类型，server 两种绑定映射，client 重连分类与 UDP 装配。

- 定义共享 ServerApiException(code, message, cause) 或等价类型，业务错误保持稳定 code，网络 IOException 保留传输故障身份。
- UDP 与进程内错误统一映射；解包 CompletionException/ExecutionException 后按 code 分类。现存 UdpFrameException 的字段是 reasonCode，不能引用不存在的 errorCode。
- 永久终止只接受明确的房间不存在、token 无效、主动关闭/替换等契约原因。未知错误走有界退避，不能因文案包含 CLOSED/INVALID 就终止。
- UdpClient.connect 将 scheduler 改为可选注入并显式记录 ownership：内部创建的由绑定关闭；外部传入的默认由调用方管理。Session.connectUdp 继续共享一个 Session 调度器。
- 保留终态 ACK 的短暂收尾窗口；双关闭路径若重构，必须继续保证 scheduler 已关闭时 socket 仍能收尾释放，不能简单删除兜底。

验收：临时异常文案含 CLOSED 仍重试；明确无效 token 不重试；共享 scheduler 不被单个绑定关闭；内部 scheduler 在失败创建和关闭后释放；多次重连线程/Socket 数回落到基线，使用可观察释放条件而非只看线程名。

### MR-03：传输及大厅全局缓存的确定性淘汰（已提交；TCP 继承业务语义）

范围：ReliableRequestor、UdpClientSession，以及 UdpTableServer 的 lobbyCompletedRequests、terminatedSessions、lobbyInFlightRequests；不顺带改领域 storedReceipts 的语义。

- 将哈希顺序淘汰改为确定性的 FIFO/时效策略，基础容量维持现有值（完成请求 256、会话回执 512、大厅完成记录及终止会话各 1024）。过期清理优先于容量淘汰。
- 大厅查询和有副作用操作分开计预算，查询流量不能挤出承诺保留期内的 CREATE_ROOM/JOIN_ROOM/RESUME_SESSION 回执。拆池后的总容量及分配需显式配置；同一 `(remoteAddress, requestId)` 的跨 API/跨池冲突仍统一检查，不允许改 API 后重做副作用。
- 在 MR-00 明确各类回执的重试保留期，承诺期内的副作用回执不可因普通容量淘汰消失。新副作用请求先预留回执容量，容量不足在执行前返回明确的可重试拒绝；已受理请求在完成时仍有保留位置。超出保留期的重试定义明确行为，不声称所有重试 100% 命中。
- 同一锁内维护索引、插入顺序和淘汰；重复写同 ID 的顺序策略明确为不延长保留窗口，防止重复流量长期占槽。
- 完成响应以 requestId 为主记录，维护必要的 rspSeq 索引；淘汰主记录时同步清理索引，保留窗口内重复 RSP 必须继续回 ACK。
- 会话与大厅在途请求的回收/完成均使用比较删除，防止迟到完成删掉同 ID 的新条目；已开始的副作用任务不能仅因响应超时就释放身份并允许重复执行。按时间索引或有界定时批次清扫，避免每次写入扫描全表。Future 完成放在缓存锁外。
- 第一版保留现有原文与结构比较语义；后续若用指纹节省内存，必须定义“线路字节相同”或“规范化请求相同”，再使用 SHA-256 等完整摘要。不能直接以 CRC32 替代冲突判定。

验收：可淘汰池在第 257/513/1025 条边界按契约淘汰最早条目；保留中的副作用池满时拒绝新请求且不执行业务；1024 次 LIST_ROOMS 不冲掉保留期内的 CREATE_ROOM 回执；同 ID 改载荷或跨池改 API 被拒；过期、迟到完成和同 ID 重试交错不重复建房；终止会话仍支持承诺期内的收尾处理。与 MR-06 联测房间回收后的缓存失效。

### MR-04：原 UDP 分片生命周期方案取消

TCP 迁移不实施 PreparedDatagramBatch、稳定分片 msgId、FRAG 完成缓存或重复片补 ACK。原问题随 UDP 传输链退役消除，不能在退役前标为已修复。

替代工作为 T1 成帧（长度上限、半帧/多帧/EOF）和 T2 有界队列。业务幂等、重复请求恢复原结果、在途身份不能提前释放等要求保留至 T2/T4，见 [TCP 设计](TCP-MIGRATION-DESIGN.md)。

### MR-05：原 UDP 序号耗尽方案取消

TCP 不继续实现 UDP SequenceAllocator、seq 高水位换连接、unattachedOutboundSeq 重置或 ReplayGuard 回绕；T5 删除这些传输字段及引用。

仍保留 requestId、逻辑身份、sessionId/syncEpoch、cursor、roomVersion、controlEpoch；T4 验证旧连接回调失效、结果不明请求不能换 ID 重做、业务计数边界不回绕。TCP 内核序号与这些业务字段不属于同一层。

### MR-06：房间回收、观战上限与未绑定建房准入

范围：server RoomRegistry/SharedRoom 及目标 TcpServerBinding/TcpTableServer，必要的 protocol 错误码；不把部署策略放进麻将规则配置。依赖 T2/T3 的逻辑恢复身份和 TCP 准入边界。

- 新增服务端 RoomRetentionPolicy 与 RoomCapacityPolicy，采用注入单调时钟。
- 建议初始配置：全员人类连接断开后的大厅 5 分钟、进行中 30 分钟、终局 10 分钟；观战成员上限 32。以上均为拟定默认值，需在交付中标注，可通过服务端配置调整。
- 未绑定 CREATE_ROOM 在分配房间/执行器前检查全局容量、在途建房预留和来源限额；配额检查与预留必须原子化，不能只在异步创建前读取 rooms.size。失败/取消释放预留，成功转为正式配额；幂等重试不重复占用。
- 提供单源 IP 未开局房间配额（候选值 3）、按大厅逻辑身份的配额及全局建房速率预算；共享 NAT 不等于同一用户。TCP 握手不构成用户鉴权，不能据此取消全局预算。
- TCP 在完成 HELLO/WELCOME 并建立有效大厅恢复身份之前禁止 CREATE_ROOM；无须新增 UDP 回程 ACK/cookie 验证。另定义“未形成有效参与”的候选房间：无已连接玩家/观战且创建者连接已失效；短期限回收必须与 resume/join 和幂等账本在权威队列串行复核。
- 房间驱逐与 MR-03 联动：大厅成功回执不能继续返回已撤销的 roomId/token；保留必要的请求身份及房间已失效结果，承诺期内同 ID 重试不会重新创建房间。迟到建房完成也必须检查配额预留和房间代次，不能重新发布已回收对象。
- 空闲定义：没有已连接的人类玩家，也没有已连接的观战者；AI tick 不刷新人类活跃时间。仅角色连接恢复成功重置对应空闲起点。
- FINISHED 但仍有真人连接查看结果的房间不按该空闲 TTL 关闭。进程内单机房间由组合根管理，不因未使用网络连接记录被误回收。
- 巡检只筛选候选；最终“仍空闲、期限到达、代次未变化”的检查和关闭标记必须在该桌权威队列执行，与 resume/join 串行裁决。
- 关闭后注销 RoomRegistry 与传输绑定，撤销 token/scope，清理观察者、待发送队列、关联定时器和未决请求，并关闭房间拥有的执行器。回执保留失效结果，不能让原 ID 重建房间；关闭可重复调用。
- 观战上限按尚未离开的成员计数，包括保留恢复身份的断线成员；同一成员 resume 不额外占位，显式 leave 释放。必要的断线成员独立 TTL 另列扩展，不能默默改变 token 有效期。
- 上限在权威队列中先检查后分配身份；复用已有可表达的拒绝码或新增明确错误码，并验证旧客户端对未知业务拒绝的安全降级。

验收：TTL 边界、resume/join/建房完成与回收竞争；仍有观战者时保留；AI 不延长无人房间寿命；第 33 个观战者拒绝；单 IP 配额、多 IP 耗尽全局预算、共享 NAT 配置与并发预留；建房执行后 TCP 断线再以原身份重试、驱逐后重试、无效恢复身份拒绝；循环回收后注册表、任务数和配额回到基线。

### MR-07：执行器与派发队列预算、公开广播复用

范围：ServerTableHost、SharedRoom 及目标 TcpServerBinding/TcpTableServer，不改变每桌串行权威队列语义。下面 UDP 执行器统计仅作历史对照，TCP 预算在 T0 重测。

源码基线：默认 UDP 房间路径包含 10 个执行器对象，即 6 个单线程执行器（serial、4 个 seatDispatcher、publicDispatcher）、1 个 AI 池（核心 2、最大 4、队列 32）和 3 个 Cached 池（completion、roomNotification、async）。SharedRoom 复用宿主的权威队列，不额外增加一份 serial。注入执行器时所有权和数量可能不同；线程按任务需要启动，以上数量不能转换成“100 个空房必然 700+ 线程”。

建议拆为 MR-07a（预算与派发）、MR-07b（公开载荷复用），分别验收：

- 使用 MR-00 的负载基线确定线程上限、每通道条目/字节上限、全局队列预算及最大等待时间，交付前必须填写具体配置和测量结果，不以“有界”两个字替代容量设计。
- seat/public 派发可改为共享有界池上的串行通道，每个通道有独立有界队列与单一 drain 所有者。采用 drainWip 或等价实现时，覆盖入队与退出竞争、调度拒绝后的计数回退/重调度及关闭；每轮限量 drain 后重新排队，避免热通道长期独占。
- 分别列出可合并的完整状态通知与不可静默丢弃的事件/终态通知。队列满时按该流契约触发全量恢复、隔离慢订阅或明确拒绝；不能简单丢最旧事件，也不能误把“完整快照”当作包含所有历史事件。
- 共享工作池不会自动隔离阻塞回调。网络发送、Future 完成和外部监听器按阻塞风险分开预算；定义慢订阅的断开/隔离策略及完成路径的保留容量，防止所有工作线程被占住后其他房间饥饿。不可中断的外部回调无法靠 shutdownNow 强制结束；关闭需在期限内返回并报告残留，不能承诺任意回调下零线程残留。
- 第一阶段保留现有每桌权威执行器及 AI 推理语义；线程预算包含这部分随房间数增长的成本，不要求总线程数与房间数无关。若测量后还需共享 AI 或虚拟化权威串行通道，另立任务，验证跨房间公平性、deadline 与关闭行为。

- TCP 终态 CLOSE 使用有界控制队列与关闭期限，不复制 KICK 重试/ACK 追踪。TcpTableServer 拥有 selector 与有界分发池，明确关闭责任。
- 房间关闭只停止本房间的通道、订阅及未决记录，不 shutdown 共享池；宿主关闭统一释放拥有的共享资源。排队任务必须取消或有界完成，迟到任务不得重建已关闭房间；不得在权威线程或工作线程上等待自身退出。
- 饱和策略禁止 CallerRuns 把耗时发送压回权威队列；也禁止无日志地丢弃全部终态通知。至少保留连接失效处理，并通过有界调度重试或可观测拒绝完成 Future。
- 统一公开更新编码入口，同一不可变 JSON 载荷编码一次，各 TCP 连接持有独立 ByteBuffer position/limit 和有界写队列引用；不共享可变写进度，不保留 seq/ACK/重传索引。
- 缓存键包含 sessionId、syncEpoch、cursor、fromCursor、isFullSnapshot；只共享确认不含私密数据的 payload。缓存有容量/字节上限，旧会话结束即释放。

验收：1/10/100 房间在约定负载下线程和队列不超过冻结预算；注入慢回调、慢发送和池饱和后，其他房间在预算内继续推进，未决 Future 有界完成或明确失败；关闭一个房间不影响共享池中的其他房间；重复关闭及关闭期间入队不丢失取消结果。广播部分验证 1/8/32 观战者的公开内容等价、相同载荷编码次数由 N 降至 1、不同流及全量/连续消息不串缓存。

### MR-08：表现层与 Codec 的局部拆分

拆成四个小 MR，分别验证，不把字体改动夹带进计时重构：

1. **MR-08a 计时与身份委托**：抽出 AuthorityCountdownTracker 和 DecisionIdentityGuard；先迁移 SleeveBloom/BeamSwap，再评估 TileMark 和准备阶段。不同阶段的预算、候选失效、取消与费用规则留在各业务模型中，不强行继承同一基类。
2. **MR-08b 动画与模态协调**：抽出 DiscardAnimationController 和显式模态优先级协调器。保持现有遮罩、点击拦截、ESC/BACK、断线关闭和权威超时语义；不得让通用“关闭窗口”替代各千术的保留/放弃请求。
3. **MR-08c Codec 按领域拆分**：把 ApiMessage.kt 的房间、公共流、玩家快照和千术 Adapter 分文件；外部 ApiMessageCodec 入口不变。严格 JSON 类型校验留在 Adapter，业务自洽校验留在 DTO；没有等价测试证据前不删重复校验。
4. **MR-08d 字体共享与字表清理**：SettlementDetailRenderer 和 ShufflePreparationPanel 改为借用 UiAssets 管理的字体，组件不再自行创建/释放共享生成器。结算现为 18px，码牌现为 17px，不能直接替换后接受换行和命中区漂移；通过明确字体规格或受控缩放保持布局。共享所有权不强制所有字号只能有一个 BitmapFont。每次绘制恢复颜色、缩放等可变状态，增量字形生成限定在渲染线程；UiAssets 统一负责销毁。引用审计后移除不参与生成的 *_CHARS，保留错误码翻译完备性测试，以真实 GL 缺字检查覆盖实际字体路径；无头测试不依赖 GL。

验收：现有三大绝技及码牌的模型/屏幕契约、动作门控、听牌/结算和公开流不可变测试通过；固定相同输入序列，前后发出的请求、事件和剩余时间一致；字体及桌面显示改动需真实 GL 冒烟。

### MR-09：领域状态与回执的有限治理

- 先建立立直/一发双份状态的不变量清单与唯一写入口，列出读方后逐步迁移为单一来源或只读派生；不要同时改算分、立直后杠和快照映射。
- copyFrom 与 resetForNewRound 保留显式字段清单及针对新旧局状态的测试；先解决重复来源，再决定是否拆 BoardState/SeatState。
- storedReceipts 不直接增加 LRU：被淘汰请求仍可能重试，跨局冲突检查是已有契约。先测量每场真实条目数、拒绝请求占比和保留字节数。
- 若需要硬上限，设计独立准入配额：先查已有 ID，超额度的新 ID 在业务执行前拒绝且不继续增长缓存；已接纳请求保留至会话生命周期结束。配额值和新错误语义基于测量确定，单列兼容测试。
- 预览缓存仅在证明重复计算后引入；键不能只有手牌频次数组，至少覆盖影响结果的决策身份、手牌/副露、弃牌史、立直/振听与规则上下文。

验收：136 守恒、役种/算分、振听、立直后杠、跨局账本全部通过；旧 requestId 重试不重执行；缓存命中与禁用缓存时预览结果逐项相同。

### MR-10：测量后再选择的优化

先记录同一构建、同一硬件、固定输入集的结果，再决定以下候选是否进入开发：

| 候选 | 所需证据 | 方案限制 |
|---|---|---|
| 136 张 TileView/ProtocolTile 享元 | 分配量占比、映射延迟、Android 帧时间 | 反序列化仍先校验 tileId/suit/rank，不能用享元绕过矛盾字段检查 |
| 快照共享替代深拷贝 | 全对象图不可变审计和分配热点 | Kotlin val/List 不等于深度不可变，不能未经审计直接 return this |
| 动态 UDP RTO | TCP 目标下取消 | 保留业务 deadline、请求超时与重连退避，不复制 TCP 重传算法 |
| zlib | 实际早中晚盘、标记、候选、结算载荷大小与压缩 CPU | 能力协商、解压上限、旧端策略；不承诺所有快照单包化 |
| ClassicSkin 纹理缓存 | 旋转视觉需求、纹理内存与批处理指标 | 覆盖赤五、字形、透明度、缩放和资源释放，不能只生成 34 张而丢赤牌差异 |

没有测量优势的项目暂缓，不以类数、行数或“零拷贝”口号作为完成标准。

## 5. 实施顺序、依赖与工作量

工作量为初步工程估算，单位人日，包含开发及独立验证；不是日历承诺。MR-00 后根据复现结果重估。

| 阶段 | 交付 | 依赖 | 估算 | 退出门槛 |
|---|---|---|---|---|
| A 基线 | MR-00 | 无 | 1–2 | 基线与失败分类完整，关键探针可重复 |
| B 客户端正确性 | MR-01、MR-02 | A | 4–6 | 换绑/恢复/所有权场景通过 |
| C TCP 迁移 | T0–T5，继承 MR-03；取消 MR-04/05 | MR-01/02/03 基线 | T0 后重估 | 成帧/幂等/恢复/背压/双端/资源验证通过，UDP 删除审计完成 |
| D 服务端资源 | MR-06、MR-07 | A；错误依赖 MR-02，回收与 MR-03 缓存失效联测 | 5–9 | 原子准入/回收、队列与线程预算、慢回调隔离通过 |
| E 可维护性 | MR-08a～d、MR-09 | UI/Codec 可在 A 后独立推进；领域治理待相关正确性修复稳定 | 6–10 | 行为等价、字体布局/所有权、完整模块及独立审查通过 |
| F 条件性优化 | MR-10 中被测量支持的项 | 性能基线 | 单项另估 | 性能收益可重复且无契约回退 |

原 UDP 路线 A–D 的 17–28 人日估算作废，TCP 路线在 T0 后重估；E 原估 6–10 人日另计。D 阶段估算不包含新增来源挑战协议、AI 池共享或权威队列虚拟化。第 9 节 D-01～D-09 的专项拆分另行估算，不重复计算已经由 MR-08/MR-09 完成的前置工作。应按 MR 独立验收和合入，不等待整批大重构完成才验证。

代码实施建议沿用 Anti 开发、Codex 独立验收。每个任务启动时提供固定 SHA、允许修改的文件、复现用例和退出条件；工具桥接超时只代表调用中断，需先确认后台进程状态，再决定是否续接，避免两个开发进程同时写同一批文件。

## 6. 验证方法与基线失败处理

### 6.1 验证命令

下列为 PowerShell 示例。Gradle 命令必须串行运行；带点号的 -P 参数用引号包住。

~~~powershell
.\gradlew.bat :protocol:test :client:test :server:test --console=plain
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --tests 'com.majiang.review.*' --tests '*V4ModelAcceptanceTest' --tests '*V4FinalArchitectureTest' --tests '*V4AuthorityBoundariesTest' --console=plain
~~~

每次按改动补跑对应的已有测试及新增回归；阶段出口执行完整验证：

~~~powershell
.\gradlew.bat build --console=plain
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --console=plain
~~~

遇到 Kotlin 增量缓存损坏时，先确认没有另一个构建写同一输出目录，再清理受影响模块并重跑。需要时使用以下参数，而不是修改业务代码迎合损坏缓存：

~~~powershell
.\gradlew.bat :protocol:clean build '-Pkotlin.incremental=false' '-Pkotlin.compiler.execution.strategy=in-process' --console=plain
~~~

仅文档合并检查链接与内容，无需重跑全量构建。此前 v2 审查定向运行了两项历史测试，均失败，详见第 6.2 节；这不代表执行了全量测试，也不代表新增方案已完成验证。

### 6.2 已知失败不是永久豁免

[此前验收记录](../legacy-cheat-removal/ACCEPTANCE-CLAUDE.md) 已记载：

- V4ServerHostAcceptanceTest 的 pausedAuthorityExecutorDoesNotBlockInvocationAndCloseEndsQueuedFutures。
- 同类的 shuffleAndActionReceiptNamespacesRemainIndependentAfterLaterCommit。
- SharedRoomTest 的 start versus join config and leave operations are serialized on authority queue 曾出现顺序敏感失败。

2026-10-14 的 v2 审查使用独立审查 init 脚本定向重跑前两项，结果为 2 项执行、2 项失败（源码基线 f3ad037）：

| 用例 | 实测/源码证据 | 正确处理 |
|---|---|---|
| pausedAuthorityExecutorDoesNotBlockInvocationAndCloseEndsQueuedFutures | 在测试第 131 行失败。用例动态记录 originalPhase，关闭宿主并运行排队任务后得到 ROUND_END，与原 STACKING 不同；TableAuthority 默认基态明确为 STACKING，关闭链调用 coordinator 的会话关闭 | 不能归因为默认 shuffleConfig.enabled=false 或测试硬编码初始阶段。先记录 close 返回后的投影，再执行排队任务，分别验证关闭契约与取消任务无副作用；不取消“关闭后不得执行排队命令”的保证 |
| shuffleAndActionReceiptNamespacesRemainIndependentAfterLaterCommit | 在第 179 行失败；相同 requestId 先提交码牌再提交动作，TableAuthority.submitChoice 显式检查 storedShufflePlans 并拒绝跨操作冲突 | 对照现行跨操作身份契约及领域测试确定应修测试还是实现；不能为通过独立测试直接删除冲突检查 |
| SharedRoomTest 的并发准备/配置用例 | 本轮未重跑。源码使用多个 supplyAsync 并发 setReady/join/updateConfig，却假定配置更新最后重置 ready，存在无顺序保证的断言 | 用可控执行器分别覆盖合法入队顺序；尚未以本轮运行证实，不写成新近实测结论 |

MR-00 在最终开发 SHA 上重新执行完整基线，补齐修复前后证据。定向失败只说明旧基线仍有问题，不是本次文档合并引入的代码回归。

MR-00 在实际开发基线重现并定位；确认是测试夹具失效、已有业务缺陷还是环境问题。禁止仅因“旧问题”就永久 exclude。相关模块的 MR 合入前，应修复根因或提供独立跟踪、稳定复现和未扩大影响的证据；存在未解决失败时，验收结论必须明确范围，不能写“全部通过”。

### 6.3 各阶段统一证据

- 修复前失败/修复后通过的确定性回归记录，或行为保持的前后等价结果。
- 标准模块测试与独立审查分别计数，区分执行、缓存命中、跳过、失败。
- TCP 改动附半帧/多帧/断线/重复业务/换代/背压矩阵及真实网络故障注入；资源改动附计数回落证据；UI 改动附真实 GL 冒烟。
- 前后版本的协议语义、私密字段和出千账本对照。
- 验证结果绑定最终提交 SHA；最终代码再变动时补跑受影响检查。

## 7. 发布与回退

- 一个 MR 聚焦一种契约；结构移动与行为修复分开提交，避免无法判断回归来源。
- 内部重构可按提交回退。TTL、观战上限等通过服务端策略配置发布，交付说明明确对断线恢复期限的影响。
- 建房配额、未验证房间驱逐和回执保留期限共同发布；回退配置不能让已撤销的房间/token 重新有效。执行器共享保留可切回的调度装配，切换须在房间创建或明确的停用/排空边界完成，不能让新旧通道同时派发。
- UDP→TCP 不热迁移活动房间：旧服停止接纳新房，结束活动房间后配套升级两端；回退旧发布物并新建房间，不承诺无持久化的房间跨版本恢复。
- 压缩等线路扩展必须能识别对端能力；不支持时走原始未压缩路径。加密、公网账户、撮合、持久化恢复和主机迁移不属于本轮功能。
- 回退后验证旧客户端连接、已在途请求结果、token 恢复和资源释放，不能只验证进程能启动。

## 8. 下一阶段：只完成 TCP 设计，暂不派发开发

MR-01/MR-02/MR-03 保留提交与验收记录。用户要求“先不开发”，本轮仅交付设计及相关文档调整；T0–T5 未启动，生产代码和 UDP 测试不在本轮删除。

后续按 T0 契约冻结 → T1 成帧 → T2 连接/回执 → T3 会话接入 → T4 验收切换 → T5 UDP 删除推进，详见 [TCP 迁移设计](TCP-MIGRATION-DESIGN.md)。MR-06/07 按 TCP 接入重定范围；MR-08/09 的非传输工作继续独立规划。

## 9. 代码拆分专项复核与补充计划

本节补充原计划偏重故障修复、未完整展开的维护性工作。代码拆分的价值在于明确状态所有者、缩小修改影响面、隔离可见性和复用稳定行为；不要求先证明性能收益。仅移动函数到新文件、再把整个原对象传进去，不能算完成职责拆分。

### 9.1 原始建议逐项判断

| 原始建议 | 判断 | 可接受的实施边界 | 原计划覆盖及补充 |
|---|---|---|---|
| client：拆分 ServerConnectionSession | 合理，值得做 | 房间同步、玩家流、观战流分别封装；连接恢复与角色切换仍有唯一协调者 | MR-01 只有前置修复，补充 D-01 |
| client：把两层同步全部合并到 Session | 方向可研究，暂不直接执行 | 先列每层的早到缓存、ACK、游标连续性、全量恢复和进程内保障，再证明可删的重复项 | 不与 D-01 混做；另立契约迁移任务 |
| server：玩家/观战会话分型 | 合理 | TCP 传输状态与逻辑角色分离，不迁入 UDP 窗口与防重放字段 | D-02 并入 T2/T3 |
| server：两种绑定复用代次校验与安装逻辑 | 部分合理 | 复用房间成员代次规则；传输本地的连接代次、监听器与资源释放留在各自适配器 | MR-02 只覆盖错误映射，补充 D-03 |
| protocol：ApiMessage.kt 按领域拆分 | 合理，可较早做 | 注册入口不变，按房间、动作/千术、快照/事件、结算拆分 Adapter；保留严格解析 | 已在 MR-08，细化为 MR-08c |
| protocol：包名规整 | 可选整理，收益较低 | 先审计真实包路径和引用，再单独迁移；不得在拆 Codec 时夹带批量改包 | 不列为第一批必要工作 |
| presentation：提取倒计时和决策身份校验 | 合理 | 使用小型委托组件；三种绝技和码牌各保留草稿、取消及费用规则 | 已在 MR-08，细化为 MR-08a |
| presentation：GameScreen 动画/模态拆分 | 合理 | 动画队列拥有自己的状态；模态协调器管理优先级和输入路由，业务关闭语义由调用者提供 | 已在 MR-08，细化为 MR-08b |
| presentation：MenuScreen 主菜单/大厅拆分 | 合理 | 先拆子 View 与展示状态；保留一个导航和订阅生命周期所有者 | 原计划未展开，补充 D-04 |
| presentation：公共桌面渲染复用 | 合理，不应仅列为性能候选 | 先统一公开渲染输入，再复用静态桌面，最后接动画；私有手牌和千术面板保留在玩家屏 | D-05 |
| presentation：字体生成与释放收口 | 合理，单独实施 | UiAssets 管理字体规格及生命周期，组件借用；不夹带计时改造 | MR-08d |
| engine：GameState 拆 BoardState/SeatState | 可分阶段做 | 先消除重复状态来源，再按拥有关系拆子状态；保留单局原子变更和明确复制语义 | MR-09 已覆盖前置工作，补充 D-06 |
| engine：全部不可变、通用 Map 保存千术状态 | 不接受一并实施 | 全面不可变需要独立对象图及成本审计；千术继续使用有类型的状态结构 | 不把它作为 GameState 拆分的前提或验收目标 |
| engine：算分数据与文案分离 | 方向合理，但涉及线路演进 | 先抽纯格式化职责；字段改为稳定码需要 engine→protocol→client→presentation 全链迁移 | 原计划遗漏，补充 D-07；不并入单纯拆文件 |
| server：路由与回执管理拆分 | 按 TCP 目标调整 | 保留业务请求/推送身份边界，取消 UDP ACK/KICK 重试追踪拆分 | D-08 并入 T2/T3 |
| engine：TableAuthority/ApplicationCoordinator 千术委托 | 合理，逐项实施 | 按领域提取内部处理器，统一身份/回执、锁与事务提交仍由原入口协调 | D-09 |

### 9.2 可派发的拆分任务

以下名称是设计建议。D 编号用于补充维护性任务，不代表已实施或已经创建 MR。

**D-01：客户端同步组件。** 在 MR-01/MR-02 稳定后，按 RoomSyncEngine → PlayerStreamEngine → SpectatorStreamEngine 分步提取。每个组件拥有自己的缓存、版本/游标、恢复标记和监听分发状态；ServerConnectionSession 保留连接、重连、当前角色及公共接口装配。组件返回待派发事件/恢复请求，由协调层在锁外执行，避免相互调用时持锁。切换角色仍由统一过程完成，AtomicReference 只替换引用不能自动使缓存、订阅、Future 与旧回调失效。验收包含 InProcess/TCP、异步查询迟到与全量恢复、监听器内换绑、关闭期间未完成 Future 和玩家/观战互斥；不要求同步算法合并。

**D-02：服务端角色类型。** 并入 TCP T2/T3。公共 TransportConnection 仅拥有 channel/epoch/读写预算/心跳；逻辑角色使用 PlayerState(seat, snapshotSupplier) 或 SpectatorState(spectatorId, publicSnapshotSupplier)，逻辑回执属于验证身份而非物理连接。删除 UDP seq、防重放与 pushWindow/publicWindow 字段。玩家命令入口使用玩家类型，线路哨兵 seat=-1 仅在 DTO 映射边界保留。验收绑定、恢复、顶号、全量同步、关闭及私有/公开隔离。

**D-03：服务端绑定规则。** 并入 T2/T3：参考当前 UdpRoom 与 InProcessBinding 的已验收规则，区分成员 generation、本地 bindingGeneration 和传输 connectionEpoch。由 SharedRoom/房间侧校验成员与准入，并在权威队列最终复核；网络资源由传输所有。复用 MR-02 错误类型，同一套业务参数化场景覆盖 InProcess/TCP 的顶号、旧命令排队、迟到结果、重复离房和关闭。UDP ACK/KICK 不迁入共享层。

**D-04：菜单子组件。** 从 MenuScreen 提取 MainMenuView、LobbyListView、RoomPreparationView 及连接/建房/加房对话框。父层拥有 activeLobby、角色、requestGeneration、订阅与导航，各子组件接收展示状态并上报明确意图。先保持一个 Screen，避免拆成多个 Screen 后改变 hide/dispose 时机或重复订阅。验收覆盖连接取消后迟到完成、刷新后选择保留、准备/开局跳转只发生一次、返回菜单和释放资源；字体与皮肤继续共享。

**D-05：公共桌面渲染。** 定义表现层内部的 TableBoardRenderModel，字段仅包含四家公开牌河/副露、允许显示的手牌背数量、点数、指示牌和时钟等公开数据；分别从 PlayerView 与 PublicBoardView 映射，禁止把 PlayerView 本体传给观战渲染器。第一步提取桌面布局与静态绘制，第二步接入 MR-08b 的动画控制器并定义每个视角的状态实例，第三步处理公开结算详情。玩家输入、自己的手牌、立直选择和私密千术面板不进入公共组件。静态复用可先交付；动画需事件、游标、全量重置和快进适配，不承诺“零成本获得”。验收比较四种相对座位布局、赤牌/副露、全量恢复、局间切换和观战隐私，附桌面 GL 证据。

**D-06：领域状态拆分。** 依赖 MR-09 对重复立直/一发状态的治理。先列每个字段的写入者、读取者、复制/reset 行为和物理实体归属，再按 BoardState、SeatState 与明确类型的千术状态逐块搬迁。第一阶段可保留既有受控可变模型；所有修改仍通过既有领域操作，不能让拆出的子状态成为绕过权威的新写入口。不以删除 copyFrom 或减少类行数验收，而以 136 守恒、快照隔离、终态固定、新局无残留、役种算分和出千行为等价验收。

**D-07：算分格式化边界。** 当前 WinningScoreEvaluator 构造 yakuLines/paymentLines，ScoreCalculator 生成 capReason/formulaText，且文本已进入协议与客户端模型。先单独提取纯 Formatter，保持现有字段和显示结果；再为真正需要本地化/结构化消费的项目设计稳定码与数值字段。是否保留旧字段、如何处理未知码、何时切换线路契约需独立决定；不能直接删除字符串后声称只是重构。数值计算与支付结果必须逐项相等，展示迁移覆盖役满、累计役满、封顶、本场棒、立直棒和里宝牌可见性。**（2026-10-19 更新：格式化职责、engine 稳定码/支付结构已完成并提交 `3f71257`；v5 线路迁移已实施：`PROTOCOL_VERSION=5`、`BUSINESS_VERSION=2`，`capCode`/`yakuEntries`/`paymentPlan`/`scoreDetailVersion` 全链贯通，v4 HELLO/WELCOME 明确拒绝；兼容/回退矩阵与验收见 `ANTI-D07-DELIVERY.md` §12。）**

**D-08：TCP 请求路由与业务回执管理。** 并入 T2/T3：TcpTableServer 管理 listener/selector/资源；路由使用明确的连接与逻辑身份上下文，通过 SharedRoom/ServerTableHost 提交业务。继承 MR-03 的 FIFO、准入、跨 API 冲突和在途所有权；按 TCP 设计管理跨连接回执。取消 UDP 可靠发送追踪、ACK、seq、分片对象的拆分类任务；MR-06/07 在新边界接入。

**D-09：领域权威与协调器的千术内部委托。** 在 MR-09 状态/回执规则明确后，按落汗 → 袖里藏花 → 偷梁换柱 → 码牌分别小步提取。TableAuthority 对外接口和同步入口不变；请求归一化、候选/资格检查和私有预览构建可进入 internal 的专用处理器，ApplicationCoordinator 的实际执行逻辑则在既有命令执行与事务边界内委托。

- 统一的 requestId 命名空间、回执查重、roundId/decisionId/controlEpoch/deadline 判定、事务提交和事件可见性仍由原权威流程协调。不能每种千术新建独立回执表，也不能因拆类导致先扣费后校验或重复扣费。
- 按源码现有位置移动费用与牌实体变更，不把原在协调器内的执行职责迁到 TableAuthority；处理器通过最小类型化输入/能力操作，不获得整棵可变状态或绕过权威的 public 写入口。
- 码牌属于准备阶段，保留其身份及截止语义；不能强塞进与局内千术相同的通用 Handler 生命周期。超时、取消和自动处理仍经原权威流程，不在处理器内新增线程或计时源。
- 与 D-06 同改领域状态文件时串行实施，先完成一个行为等价的切片再开展下一项。验收覆盖三大绝技与码牌全链路、136 守恒、跨操作同 ID 冲突、重试不扣费、资格变化、超时与抢先提交、私有事件过滤，以及 V4 架构/权威边界测试。

### 9.3 实施顺序与拆分验收

1. MR-00 后可先做 MR-08c（Codec 分文件）、MR-08a（计时委托）和独立 MR-08d（字体共享）；与正确性修复交错安排，但同一文件的修改必须串行整合。
2. MR-01/MR-02 后做 D-01；MR-03 后做 D-02；D-03 在 MR-02 及角色边界稳定后做，并与 MR-06 的房间关闭改动协调。
3. MR-08b 后推进 D-05；D-04 可独立交付。UI 拆分不需要等待整个网络资源治理阶段结束。
4. D-06 排在 MR-09 状态治理之后；D-07 的线路字段迁移单独立项。全面不可变、压缩等仍按原测量门槛执行。
5. D-08 并入 TCP T2/T3，继承 MR-03，不再依赖已取消的 MR-04；MR-06/07 复用其边界。D-09 等待 MR-09，并与 D-06 协调文件修改顺序。

每个拆分 MR 都要明确“移走什么、原类保留什么、新组件拥有哪些状态、谁负责关闭”，并用既有场景加必要的边界测试证明行为等价。拒绝通过新增万能 Context、相互持有父对象或把大量字段改成 public 来完成搬家。源码行数下降只作参考；修改一个职责是否还必须横跨多个无关组件，才是维护性收益的检查点。
