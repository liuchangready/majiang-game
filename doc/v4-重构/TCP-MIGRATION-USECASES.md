# TCP 迁移设计用例集（UC）

日期：2026-10-15。基线 `3a382c6`。上游契约：[TCP-MIGRATION-DESIGN.md](TCP-MIGRATION-DESIGN.md)（下称"设计"）；冻结决策见 [TCP-MIGRATION-REVIEW.md](TCP-MIGRATION-REVIEW.md)（下称"检视"，D 编号）。状态：设计补充，未开发。

本文件把设计 §9 验证矩阵展开为可执行用例，作为 T1–T5 各批任务的行为规格与验收清单。「矩阵」列对应设计 §9.x；「阶段」为实现批次。用例与设计正文冲突时，以检视 D 系列冻结决策为准。

## A. UC-F 成帧与编解码（阶段 T1；矩阵 9.1）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-F01 | 逐字节切分：对一条合法帧的字节流，在 0..N-1 每个字节边界处断开分多次喂入解码器 | 每种切法最终恰好解出一条与原帧等值的帧，无重复、无丢字节、无错帧 |
| UC-F02 | 混合半帧：一次喂入 = 半个头 + 完整第二帧 + 第三个帧的半体 | 按序解出完整帧，剩余半体等待后续字节 |
| UC-F03 | 单次读多帧：快照大帧、1 字节小帧、接近上限帧连续到达 | 全部按序解出，边界互不串扰 |
| UC-F04 | 长度边界：payloadLength=1、=1,048,576 合法；=0、=1,048,577、=0xFFFFFFFF 非法 | 非法值在头部校验即拒绝（解码器进入终态错误），先用 Long 比较再定分配，不得先转 Int 出负数 |
| UC-F05 | 错误即关闭：坏 magic、wireVersion≠1、flags≠0、type∉1..10、坏 UTF-8、坏 JSON 六类各一例 | 解码器进入不可恢复错误态并报告原因；残余字节不再产出任何帧（不扫描找回同步）；坏 UTF-8/JSON 在消息解析层同样终态 |
| UC-F06 | 部分 EOF：半头或半体时流结束 | 未完整帧不派发，缓冲释放，报告"未完整帧即 EOF" |
| UC-F07 | 慢速灌入：整帧按每秒百余字节缓慢到达，10 秒内凑齐 | 在组装截止（UC-L08）前正常解出；凑不齐由连接层关闭，解码器本身不超时 |
| UC-F08 | 缓冲有界：喂入声明 1 MiB 的大帧 | 解码暂存 ≤64 KiB + 一个在组装帧；不得按头部长度立即预分配全部载荷内存 |
| UC-F09 | 编码侧防护：encode 越界 type、空载荷、超限长度 | encode 当场抛 IllegalArgumentException，不产出坏帧 |
| UC-F10 | 控制帧同路：PING/PONG/CLOSE 编码后与数据帧走同一成帧路径 | 无独立旁路，头部字段一致 |

## B. UC-H 握手与会话建立（DTO 阶段 T1；执行 T2/T3；矩阵 9.1/9.2）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-H01 | 首次握手：connect → HELLO(wire=1,business=1,无 token) → WELCOME | WELCOME 含 connectionEpoch、lobbyResumeToken、lobbyScope(scope+expiresAtMillis)、limits（按 D6）；握手完成后才可发 REQ |
| UC-H02 | 重复 HELLO：同连接第二次 HELLO | 按 PROTOCOL_VIOLATION 关闭，不重发 WELCOME |
| UC-H03 | 握手前业务：WELCOME 前发 REQ | 拒绝并按 PROTOCOL_VIOLATION 关闭，不进入业务路由 |
| UC-H04 | 握手超时：建连后 5 秒未完成握手 | 服务端按 HANDSHAKE_TIMEOUT 关闭；未握手连接占用计入 UC-L04 上限 |
| UC-H05 | 版本不匹配：wireVersion 或 businessVersion 不符 | CLOSE(VERSION_MISMATCH) 后关闭；不自动降级、不回退 UDP、不做协议探测 |
| UC-H06 | 恢复握手：携有效 lobbyResumeToken 重连 | 新连接继承同一 logicalPrincipal，颁发新 scope；token 无效/过期则明确错误（INVALID_TOKEN 语义），不得静默换新身份 |
| UC-H07 | connectionEpoch 唯一性：同进程多条连接 | epoch 互不重复；仅作传输标识，任何业务去重不得以其为键（由 UC-R11 证明） |
| UC-H08 | HELLO 头部保留位：flags≠0 | 帧级非法即关闭（UC-F05），不进入握手状态机 |
| UC-H09 | 未握手桶上限：64 条未完成握手连接时第 65 条到达 | 立即拒绝（SERVER_OVERLOADED 类 CLOSE 或 accept 层拒绝），不排队、不创建无界任务 |

## C. UC-R 请求幂等与 requestScope（阶段 T2；矩阵 9.2）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-R01 | 同键同载荷重放：副作用（如 CREATE_ROOM）完成后重发 (scope,requestId,原载荷) | 返回原结果回执（同一房间），不二次执行 |
| UC-R02 | 在途关联：业务执行中重发同键 | 关联同一在途结果；两个等待方取得同一完成值，不重复执行 |
| UC-R03 | 同键冲突：同 (principal,scope,requestId) 换 API 或换载荷 | 返回现有错误码 CONFLICT（D1），不执行新载荷 |
| UC-R04 | 断线三时序：服务器执行前断线 / 执行后断线 / RSP 已写但客户端未应用即断线 | 客户端保留原 requestId+原 payload，恢复身份后重放/查询取得已受理结果或关联在途；绝不自动生成新 ID 重做；decisionId 失效仍按权威规则拒绝 |
| UC-R05 | scope 过期：到期 scope 的原请求重放 | 先查保留回执：有则回原结果；无则 REQUEST_SCOPE_EXPIRED（D3）；两者都不重新执行 |
| UC-R06 | scope 容量：成员副作用记录 512（在途≤64）满 / 大厅副作用池 1024 满 | 受理前 SERVER_BUSY（D2，已存在）拒绝：不执行业务、不留半状态、不占在途条目；多 scope 合并计数，轮换 scope 不能绕过 |
| UC-R07 | scope 准入与查询：未知 scope / 客户端自造 scope；GET_REQUEST_SCOPE 并发同身份；过期后调用 | 未知 scope 直接拒绝；并发同身份返回同一 scope 结果；过期后按需新建；查询受速率限制，且不能借此切换 principal 或绕过副作用容量 |
| UC-R08 | 认证隔离：身份 A 的失败凭证请求 B 的历史结果 | 返回 INVALID_TOKEN 类拒绝，绝不回放他人回执；恢复 token 不写日志 |
| UC-R09 | 回执保留：scope 有效期内高频查询池 FIFO 淘汰 | 已完成副作用回执保留至 scope 失效，不被查询流量冲掉（MR-03 副作用池语义迁移） |
| UC-R10 | 客户端 Future 有界：断线后 pending 请求 | 在明确超时/关闭边界完成结构化"结果未知"错误，不永悬；恢复后可重查 |
| UC-R11 | 幂等键与传输解耦：换 IP/端口/连接/epoch 后重放同键 | 仍命中原回执/原在途（键= logicalPrincipal+scope+requestId） |

## D. UC-S 推送、基线与重连（阶段 T3；矩阵 9.3）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-S01 | 绑定基线原子性：新建/恢复绑定与后续 UPDATE 竞争 | 同桌串行队列内原子取基线+安装订阅；含完整基线的 RSP 先入业务输出队列，同流 UPDATE 才放行；客户端先采纳基线再排水早到消息 |
| UC-S02 | 角色互换：玩家↔观战重绑 | 旧流游标作废，私有/公开流独立游标不混流；监听器间换绑检查保留（MR-01） |
| UC-S03 | 监听器内再入：回调内 close/rebind | 不死锁、不重复派发、不乱序（MR-01 行为等价） |
| UC-S04 | 早缓冲溢出：恢复期早到消息第 65 条或累计超 4 MiB | 触发一次合并的全量恢复，不做增量修补 |
| UC-S05 | 迟到旧查询：并发两次全量恢复，旧响应后到 | token/epoch 门控丢弃旧响应，不覆盖更晚基线 |
| UC-S06 | 游标异常：fromCursor 回退/重复/缺口 | 回退重复丢弃；缺口先停派普通事件并做一次全量查询，拿到 full 基线后继续；非法身份立即隔离 |
| UC-S07 | 无重传语义：链路丢包由 TCP 承担 | 线路上无 RESEND/ACK/FRAG；中断恢复一律 resume/snapshot/publicSnapshot 全量基线 |
| UC-S08 | 重连绑定代次：旧连接命令已在房间权威队列中 | 新连接认证后替换绑定代次；旧命令再次验证被拒；旧 Future/回调不更新新 UI；不自动 claimControl |
| UC-S09 | 游标溢出：Long 游标/代次逼近上限注入边界值 | 显式终止该流/会话并要求新身份，不回绕、不因换传输重置控制代次 |
| UC-S10 | 结算对账：结算瞬间断线 | 结算/账本结果经业务查询（QUERY_ROUND_RESULT 等）核对，不依赖瞬时动画事件 |

## E. UC-L 容量、心跳与资源（阶段 T2 行为/T4 实测；矩阵 9.4/9.5）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-L01 | 发送队列上限：每连接待发 256 帧或 4 MiB（含部分写出的当前帧） | 标记慢连接、停止接纳新请求并断开（SLOW_CONSUMER）；不丢弃已排队 RSP/UPDATE |
| UC-L02 | 接收邮箱上限：串行邮箱 64 帧/4 MiB | 不无界积压：先背压读，仍超限按慢连接断开 |
| UC-L03 | 全局预算：所有连接读/写/解码/邮箱实际保留字节合计 | ≤256 MiB，预留后再分配；共享不可变字节按所有权计一次 |
| UC-L04 | 连接上限：总连接 512、未握手 64 | 超限立即拒绝，不先创建无界任务 |
| UC-L05 | selector 公平：连接 A 持续收 1 MiB 快照、连接 B 小请求 | 单轮每连接读/写 ≤64 KiB 或 ≤32 帧即让出；B 的请求不被饿死（确定性双连接测试） |
| UC-L06 | 心跳：客户端每 3 秒 PING；双方 10 秒无完整合法入站帧判死 | PONG 逐字节回显 nonce（D5）；RTT 用本地单调钟（D11），不比较两机墙钟 |
| UC-L07 | 恶意续命：持续到达零散字节但永不组成完整合法帧 | 不刷新判死计时（只有完整合法帧刷新），10 秒仍关闭 |
| UC-L08 | 不完整帧截止：首字节起 10 秒未凑齐一帧 | 硬截止关闭，不依赖 TCP keepalive |
| UC-L09 | 停滞写：连续 10 秒写零进展 | 关闭该连接 |
| UC-L10 | 控制帧优先：业务大帧写出中收到需急发的 PING/CLOSE | 控制队列 ≤8 帧/8 KiB、计入全局预算，只在完整帧边界插入；已排队 RSP/UPDATE 相对顺序不变 |
| UC-L11 | 终态收尾：正常/异常关闭 | 终态通知最多尝试排出 1 秒后强制释放 channel；无 UDP 式 2 秒补 ACK 任务 |
| UC-L12 | 资源回收：玩家/观战各 ≥3 次真实中断 | socket、selectionKey、buffer、pending、timer 全回收；Session 共享调度器存活至会话终止；TIME_WAIT 单独计数不判泄漏 |
| UC-L13 | NODELAY：桌面与 Android 建连 | 均设置 TCP_NODELAY；平台不支持时记录不失败 |

## F. UC-C 关闭与错误分类（阶段 T2/T3；矩阵 9.2/9.5）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-C01 | CLOSE 码表：版本不符 / 协议违规 / 握手超时 / 心跳判死 / 停服 / 慢连接 / 超载 / 顶号 | 每个码有发出方用例且落 D4 枚举；客户端能按码稳定分类 |
| UC-C02 | EOF/RST 临时性：对端直接断 | 一律有界退避重试（500/1000/2000/4000/8000ms，≤10 次），不判 token 无效；typed 网络/协议异常（D13）承载 reasonCode |
| UC-C03 | CLOSE 丢失：服务端 CLOSE 后立刻 RST，客户端未收到 CLOSE | 由恢复请求（resume 等）的业务回包确认是否永久失效，不把 EOF 当永久 |
| UC-C04 | 停服：服务器 shutdown | 未决请求明确结构化失败；连接按 SERVER_SHUTDOWN 收尾 |
| UC-C05 | 错误帧不可排：队列饱和到连错误帧都无法入队 | 直接断开，不等待 |

## G. UC-M 迁移与删除（阶段 T5；矩阵 9.6）

| 编号 | 用例 | 预期 |
|---|---|---|
| UC-M01 | 引用审计：设计 §7 清单逐文件审计引用为零后删除 | 不按包名批量删除；`UdpClock.kt` 引用（全仓 273 处/54 文件）收编到 transport.common 中性接口（D10） |
| UC-M02 | 测试映射：被删 UDP 专有断言逐条对应 TCP 测试 | 映射表（T1 交付初版，T5 核对）全覆盖；MR-01/02/03、136 张守恒、规则与计时测试保留通过 |
| UC-M03 | 装配切换：connectTcp + MahjongGame 组合根 | UI 不感知帧协议；地址/端口输入复用；InProcess 路径行为不变 |
| UC-M04 | 无协议探测：旧 UDP 客户端探测 TCP listener | 明确失败并提示升级，不自动互通 |
| UC-M05 | 无双栈残留：构建产物检查 | 仅 InProcess/TCP；无 UDP 开关、工厂、旧 TCP/V3 兼容壳 |

## 用例 ↔ 任务批次覆盖表

| 批次 | 覆盖用例 | 验收方 |
|---|---|---|
| T0 契约冻结（主会话） | 全部数值初值的实测复核（UC-F04 上限、UC-L 系列预算、UC-R06 容量） | Claude Code |
| T1 protocol | UC-F01–F10、UC-H01/H02/H03/H05/H08 的 DTO 与常量层 | Claude Code |
| T2 连接与服务端 | UC-H01–H09、UC-R01–R11、UC-L01–L05/L07–L11、UC-C01/C04/C05、InProcess 等价回执（F14） | Claude Code |
| T3 会话接入 | UC-S01–S10、UC-C02/C03、UC-L06/L12/L13 客户端侧、分类器与装配 | Claude Code |
| T4 独立验收与切换 | UC-L12 实机、UC-M04、四模块回归与真机网络 | Claude Code（+Codex 抽查） |
| T5 删除 | UC-M01–M05 | Claude Code |
