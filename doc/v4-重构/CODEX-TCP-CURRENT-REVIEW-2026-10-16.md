# TCP 迁移当前实现独立检视与统一返修单

日期：2026-10-16。检视者：Codex。基线 HEAD：`1664c60749cc8bf5b81196322e8f81e2f7151bdb` 加本次开始时已有的未提交 TCP/T5 工作区。**结论：REQUEST_CHANGES，不能判定 T4/T5 验收通过。**

依据：`TCP-MIGRATION-DESIGN.md`，其中 §10 指定 `TCP-MIGRATION-REVIEW.md` D1–D13 为冲突时的冻结契约；同时核对 T0/T1/T2/T3/T5 交付、现有源码和测试。用户本轮明确授权检视后统一交给 Anti 处理，覆盖旧设计阶段“本轮不派发开发”的历史限制。

## 当前进度与实测

| 阶段 | 本次核对结果 |
|---|---|
| T0/T1 | 已有测量及独立验收记录；当前实际帧头为 12 字节，MJ-TCP/1 codec 已实现。未把历史 UDP 验收当作 TCP 验收。 |
| T2 | NIO 服务端、业务路由、scope/receipt 实现存在，但运行调度、掉线处理、身份和资源约束有未关闭缺陷。 |
| T3 | TCP 已进入组合根，客户端能握手和执行基础业务；恢复、超时、幂等和慢端行为尚不符合冻结契约。 |
| T4 | 本次独立用例发现阻断项；未找到可核验的完整 T4 独立报告及真机网络、慢端、各三次断网资源证据。不能凭 T5 自报标记完成。 |
| T5 | 工作区已经删除 UDP 生产链路及相关测试；抽查四模块 Kotlin/Gradle 入口无 UDP 引用。删除动作已发生，安全删除的验收前提尚未成立；本轮不擅自恢复 UDP 或删除更多业务断言。 |

本次验证：

1. 新建独立验收源集，不改生产代码或既有测试：`tools/architecture/tcp-current-review/client/TcpCurrentReviewTest.kt`。
2. 执行 `gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :client:test --tests com.majiang.review.TcpCurrentReviewTest --console=plain`：**9 项，9 失败，0 errors**。前七项真实 TCP 回环，后两项直接验证共享回执契约。失败均对应下方源码证据，不是编译失败。
3. 执行常规 `gradlew.bat test --console=plain`：Gradle 日志 `BUILD SUCCESSFUL in 25s`（65 tasks，2 executed / 63 up-to-date）。当前标准源集 XML 合计 **1,214 项，0 失败/错误/跳过**：engine 470、protocol 166、server 154、client 138、presentation 286。不是强制重跑全部测试，也不是交付报告声称的 1,658 项。
4. 日志：`build/tcp-current-review-run.log`、`build/tcp-current-review-baseline.log`；独立 XML：`build/tcp-current-review/client/test-results/test/TEST-com.majiang.review.TcpCurrentReviewTest.xml`。
5. 本轮没有执行桌面 GL、Android 真机/模拟器网络、慢端负载或再次双端构建；不把未执行项目记为通过。

## 统一问题清单

行号均指本次检视开始时工作区。P1 为迁移验收阻断；P2 为必须纠正的证据/状态问题。动态复现和静态确认分开标明。

### R01 · P1 · 远端入口没有驱动权威 tick（静态确认）

`server/tcp/TcpTableServer.kt:156-196,376-426` 的事件循环只运行网络计时及 scope 清理；`server/TableServerMain.kt:79` 起的主循环只 sleep。TCP 包没有 `room.tick()` 或权威 Ticker 调用。`SharedRoom.kt:958` 提供 tick，但没有被生产 TCP 服务调度。

影响：真实远端对局无法依靠权威时间推进 AI、超时裁决及托管；业务测试直接提交动作不能证明实际整局能自动运行。按设计 §2/§6 和 AGENTS §5.2 恢复独立、有界、可关闭的远端 Ticker，不在 I/O 线程做领域运算。验收必须从生产服务入口启动，在客户端不操作、不手动调用 room.tick 的条件下验证 deadline 与 AI 推进。

### R02 · P1 · TCP 断开未通知房间连接控制（静态确认）

`TcpTableServer.kt:440-443` cleanup 只删除连接/归还握手桶；`TcpConnection.kt:333-382` force close 只释放网络与订阅，没有调用 `SharedRoom.disconnectSeat(seat, generation)`（SharedRoom:679）或 `disconnectSpectator`（:793）。

影响：真实 EOF/RST/心跳超时不启动领域掉线宽限与接管。按捕获的角色身份及代次，且精确一次通知权威队列；旧连接清理不得使新连接掉线。测试玩家与观战真实断网、宽限接管、重连不自动 claimControl、顶号后旧清理无效。

### R03 · P1 · 自动重连被 EOF 分类及工厂生命周期破坏（动态 + 静态）

`TcpClientBinding.kt:355,368` 把读取故障/EOF 归为 `LOCAL_CLOSED`；`:713-729` 视为永久并清空未确认副作用；`ServerConnectionSession.kt:904-924` 同样永久终止。独立 `remote EOF...` 测试失败，确认常规网络断开不会自动重连。

此外 `ServerConnectionSession.kt:3046-3053` 的 `firstCall` 工厂仅在 doReconnect(:963) 使用，首次重连返回已失效的 initialBinding，未建立新 socket；恢复失败时 doReconnect(:1001,1051) 又调用同一复用绑定的终止 close，删除其恢复记录。`TcpClientBinding.close():1405` 在 handleFailure 已置 closed 后提前返回，可能跳过自有 scheduler 清理；旧 reader 使用共享 decoder/状态，缺乏贯穿成功读帧与回调的连接代次门控。

要求分开“本次连接断开”与“Session 永久终止”，修复首次重连工厂、资源所有权、旧 reader/心跳/回调隔离；覆盖玩家/观战各至少三次真实中断，恢复原身份与未决记录，显式 close 不复活。

### R04 · P1 · pending 请求无超时，关联也不原子（动态 + 静态）

`TcpClientBinding.kt:612-667` 没有安排 requestTimeoutMillis 计时；协商的超时字段未被消费。独立 `request completes by negotiated timeout...` 用 WELCOME 声明 150ms、保持连接开放，700ms 后仍未完成。该问题在持续收到心跳而 RSP 丢失/卡住时可无限等待。

同处 `size` 检查后普通 map 写入不是原子准入；同 requestId 会覆盖旧 PendingRequest。发送失败直接删除 unconfirmed 记录，没有区分零字节与部分写后未知结果。恢复重放(:772)复用已经 exceptional 完成的旧 Future，也没有新的可查询对账结果通道。

落实原子准入/冲突检查、有界 pending 和恢复记录、可取消请求 timer、结构化未知结果及独立对账完成通道。不能仅依赖心跳判死兜底请求超时。

### R05 · P1 · scope 过期自动换 scope 重做副作用（动态复现）

`TcpClientBinding.kt:471-475,499-527` 对所有 REQUEST_SCOPE_EXPIRED 调用 GET_REQUEST_SCOPE，再以新 scope + 原 requestId/payload 重发。scope 是幂等键的一部分，这实际上是一个新业务身份，违反设计 §5“不明结果需对账；新 scope 只供后续新动作”。独立 `expired side effect...` 确认 SET_READY 收到过期后被自动换 scope 重发。

要求普通读取与副作用恢复分流；未知副作用只按原身份查回执，无记录则明确需对账，禁止自动重做。还须覆盖大厅 CREATE_ROOM 回包丢失：当前恢复仅在 resumeSession/resumeSpectator 后触发，大厅没有已知房间绑定时不能找回建房结果。

### R06 · P1 · 恢复身份未认证、有效 scope 不继承（动态 + 静态）

`TcpTableServer.kt:551-562` 直接接受客户端任意非空 lobbyResumeToken 并拼 principal，没有已签发身份注册验证；每次 HELLO 均 issueLobbyScope。独立 `unknown lobby resume token...` 和 `reconnect retains still valid lobby scope...` 均失败。

`bindPlayerToRoom:1423` 与 `bindSpectatorToRoom:1564` 继续用大厅 principal 签发新 role scope，未把经认证的玩家/观战恢复身份作为角色 principal；跨大厅 token 恢复同一角色时不能沿用角色回执身份。要求服务端维护有界、可回收的已签发身份，未知/进程重启前 token 明确 INVALID_TOKEN；恢复继承有效 scope；认证角色和大厅身份分离；认证失败不能查历史回执。测试相同角色跨连接恢复原请求与容量不可绕过。

### R07 · P1 · 客户端阻塞写入调用线程、读线程执行用户回调（静态确认）

`TcpClientBinding.kt:260` 用 java.net.Socket；`:600-606` 在调用者线程直接 OutputStream.write/flush，业务接口同步走此路径。慢端可阻塞 GL 调用线程；心跳同样同步写，可能卡住共享调度线程。`:342-456` 的 reader 直接 JSON 解码、完成 Future、调用用户监听器；慢监听器阻塞入站处理。

与设计 §1/§2/§4 的 SocketChannel/Selector、有界写队列、停滞写入截止、I/O/回调隔离不符。按冻结架构补齐可复用客户端 I/O 循环、有界串行派发和部分读写预算；不能用“返回 Future”声称整个 API 已非阻塞。验收慢写/慢回调时 GL、心跳与关闭均能前进。

### R08 · P1 · 客户端协议状态/双向心跳/RSP 身份校验缺口（动态 + 静态）

`:389-455` 不处理合法服务端 PING，落入 PROTOCOL_VIOLATION。独立 `server initiated PING...` 收到 EOF 而非回显 PONG。`:461-469` 仅按 requestId 移除 pending，忽略 requestScope；独立 `response with wrong requestScope...` 确认错误 scope 仍成功完成请求。

WELCOME 也没有客户端阶段与版本一致性校验，重复 WELCOME 未明确拒绝。要求完整握手状态机、双向 PING/PONG nonce 回显、RSP scope/id/连接代次同时匹配，严格处理错误方向、握手前业务和重复握手；不要把用户回调异常当作协议解析错误。

### R09 · P1 · 早期推送丢弃/无界缓存和监听器换绑检查回退（静态确认）

`:535-569` 的 player/public 第 65 条直接静默丢弃，无 overflow 标记、无合并全量恢复，且没有 4MiB 字节预算。`:575-587` room 早期队列甚至没有条数上限。注册监听器排水与 reader 新消息派发没有统一串行顺序；循环调用监听器期间不检查 closed/bindingEpoch，首个监听器 close/rebind 后仍继续给剩余监听器旧流数据。bindingEpoch 只递增，没有用于这些派发校验。

恢复 MR-01 身份与监听器间换绑门控、64条/4MiB 双限、overflow 合并全量恢复、早到数据与在线派发有序；用两监听器 close/rebind、65条及大帧、玩家↔观战互换和无后续推送场景验收。

### R10 · P1 · 全局内存预算和 selector 公平配额未覆盖实际持有数据（静态确认）

`TcpTableServer.kt:299` 直接调用不带预算的 decoder；`TcpFrameDecoder.kt:180-202` 分配 chunks/拼接载荷，直到 `TcpConnection.enqueueMailbox:213` 才 reserve。所有连接未完成帧及正在解码的内存没有纳入全局预算。pollMailbox(:240)在解析持有 frame 时提前归还；写帧调用前也已编码分配。

`TcpOutboundQueue.kt:80,195-211` 队列配额不包含移出的 currentBuffer，超过“含正在部分写出的当前帧”限制；writeToChannel(:140)没有临时限制 buffer.limit 到剩余轮转字节数，单次 write 可超过 64KiB。handleRead 一次 decode 可产出超过32帧，全部排入后才判断轮转上限。

要求分配前预留，跨组装/邮箱/处理中/发送的预算所有权转移明确，关闭真实归还；严格限制当前帧在内的配额与每轮字节/帧数。此项是已冻结 TCP 传输预算，不扩展为 MR-06/07 全部工作。

### R11 · P1 · 慢端停服可无限自旋且终态帧绕过当前半帧（静态确认）

`TcpTableServer.kt:1696-1704` 在 close 中直接向非阻塞 channel 写 CLOSE，`write()==0` 时无限 while，没有1秒截止；且绕过 outboundQueue 当前部分写出的帧，可能把 CLOSE 拼入业务帧中。`:1729-1736` 停工作池并 reset 记账，但没有 join 现有 I/O/工作回调或关闭 registry 中的房间宿主（不能用 reset 证明资源释放）。

要求关闭经唯一写通道在完整帧边界进行、最多1秒收尾，禁止 write=0 忙等；资源停止/所有权顺序和外部注入 registry 的处理明确。慢端不读、半帧写出时停服，验证有界返回、线程/房间/预算实际回收及业务回执仍可正确完成。

### R12 · P1 · 回执完成在共享锁内执行外部回调，比较删除未守住完成一次（动态 + 静态）

`RequestScopeReceiptStore.kt:274-326` 在 synchronized(stateLock) 内 complete Future。独立 `receipt completion invokes callbacks outside store lock` 的回调让另一线程查询 store，确认被锁阻塞。该锁覆盖所有 principal，慢回调会拖住全服回执与准入。

completeReceipt/failInFlight 忽略 `remove(key,inFlight)` 返回值，重复或迟到完成仍扣 memberInFlight/增加 completed 计数、覆盖结果，未满足 MR-03 比较删除语义。要求锁内一次状态提交，锁外完成 Future，重复完成无副作用，并补重复/迟到完成、回调重入和多桌不阻塞测试。

### R13 · P1 · 查询绕过在途上限，scope 查询限速未实现（动态 + 静态）

`RequestScopeReceiptStore.kt:230-248` 只有 isSideEffect 才校验 memberInFlight 上限，而所有读取都增加计数并登记 Future。独立 `ordinary queries obey member in flight bound` 配2槽提交3个未完成查询，第三个仍受理。串行 mailbox 出队很快而权威查询可继续积压，邮箱上限不能替代业务在途限制。

`getOrCreateScopeForPrincipal:137-153` 只是更新 lastScopeQueryTime，从未比较 scopeQueryRateLimitMillis，D9 1次/秒没有执行。要求分别落地读取/回执恢复预算、每连接及逻辑成员有界在途、scope 查询限速，并保持同身份并发 scope 一致与过期重建语义。

### R14 · P1 · InProcess 共享回执只有字段，没有接入（静态确认）

全 server 引用审计显示 `inprocess/InProcessServer.kt:30` 只声明 receiptStore；InProcessBinding 未调用该 store 的 scope/准入/完成入口。注释“对齐联机与单机幂等语义”不成立。按冻结 F12 让内部入口实际复用同一业务回执规则，补同键冲突、跨API冲突、容量、在途完成和过期行为的双传输一致性测试。

### R15 · P1 · 基线与订阅未在同一权威操作中原子建立（静态确认）

`TcpTableServer.kt:1426-1468` 先 room.snapshot，待异步 completion 回调发送 RSP 后才 room.subscribe；观战对应 :1567-1608。`ServerTableHost.kt:995-1026` 的 subscribe 再向 serialExecutor 提交独立操作，因此两者之间可发生突变。subscribe 自带新基线可修复部分状态，但不满足设计 §6 的原子获取/订阅契约，不能据此承诺中间事件和代次安全。

要求权威队列一次操作取得基线并装订阅，用有界门控保证 RSP 入业务队列后释放同流 UPDATE；建立后续绑定时旧异步快照完成不得回装旧订阅。覆盖绑定 RSP/首 PUSH 相邻、快照与订阅之间强制插入突变、并发角色变换。

### R16 · P2 · 文档、测试映射和 T4 状态证据不一致

T5 报告多处写16字节帧头，实际 `TcpWire.HEADER_SIZE=12`；多次引用不存在的 `IdempotencyReceiptStore/Test`，实际为 `RequestScopeReceiptStore/Test`；BeamSwap/SleeveBloom 中文对应颠倒。1,658 项与当前标准测试 XML 1,214 项不同，须说明统计源集、时点与独立套件，不能把旧构建目录累计入全仓结果。

T3 的10项测试中 TC-04/09 直接调用 reconnect，不验证 Session 真实断网自动恢复；TC-05 允许正常完成，未证明无响应请求超时；TC-03仅等待5秒，未覆盖10秒判死。T5 用笼统文件映射声称覆盖所有被删UDP业务断言，尚不能支持该结论。

修订当前设计开头/AGENTS 的历史状态与实际进度说明；保留历史验收结果。补逐断言TCP映射和 T4 独立验收矩阵，缺证据项明确待验收；双端构建不能替代 Android 真机/模拟器网络与后台恢复测试。业务Long游标/代次溢出、慢端、多次断网等没有本轮证据，不标通过。

## 给 Anti 的执行与验收边界

- 按 R01–R16 统一处理，建议批次：A 运行与恢复(R01–06)，B 客户端协议/流/资源(R07–09)，C 服务端资源与回执/基线(R10–15)，D 文档与测试映射(R16)。交叉依赖可调整顺序，但必须逐项回填。
- 保护现有未提交改动、`tools/architecture/` 独立验收源码及本报告；不得降低断言、删除有效业务测试，不能只把实现现状改写成设计正确。
- 不 commit/push、不恢复旧TCP/V3，不扩展到账号/TLS/主机迁移或完整MR-06/07，不擅自恢复UDP。
- 本轮 Anti 负责开发及新增正式回归测试；Codex 负责独立复验。沿用文件工具编辑约束，Anti 不启动 Gradle/shell；提交测试命令与待运行清单即可，不自报执行结果。
- 验收至少：本次9项失败全部转绿；补 R01/R02 实际生产驱动、真实自动重连、慢写停服与流隔离测试；标准模块测试；然后双端构建/GL和T4真实网络资源矩阵。没有实测条件时如实留为待验收。
- 交付写 `ANTI-TCP-CURRENT-REWORK-DELIVERY.md`，更新 `ANTI-PROGRESS.md`，逐R编号列修改、正式测试、已执行/未执行证据和遗留项。不要同步反向调用正在等待的 Codex，避免桥接互等；由本次调用返回交付摘要。

派发与验收更新：已直接使用 `agy.exe --mode accept-edits --dangerously-skip-permissions` 派发并多次续发。Anti 首轮交付后生产代码编译失败，见 [首轮返修复验](CODEX-TCP-CURRENT-REWORK-REVIEW1.md)；编译专项返修后 9 项独立测试通过，但标准测试仍失败，见 [第二轮](CODEX-TCP-CURRENT-REWORK-REVIEW2.md)和[第三轮复验](CODEX-TCP-CURRENT-REWORK-REVIEW3.md)。当前整体结论仍为 **REJECTED / NEEDS_REWORK**，`TCP_REWORK_COMPLETED_PENDING_REVIEW` 是 Anti 旧自报，不能作为验收结论。
