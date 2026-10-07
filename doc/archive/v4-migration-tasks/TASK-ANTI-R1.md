# R1 协议与连接可靠性修复

开发 Anti；独立验收 Codex。仅文件工具，不执行命令。完整实现本任务，不进入 R2。

## 必须读取

PLAN.md、CONTRACT.md、REVIEW-2026-10-05.md；tools/architecture/v4-review/V4ReviewAcceptanceTest.kt（只读）；涉及的当前实现与原有测试。

## 授权范围

- protocol/src/main/kotlin/com/majiang/transport/{udp,api}/ 及相应 protocol 测试；必要时 server/api 契约注释。
- server/src/main/kotlin/com/majiang/server/{udp,inprocess}/ 及同路径模块测试。
- client/src/main/kotlin/com/majiang/client/transport/udp/ 及对应测试。
- client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt（只修正缺失 selection 的测试编译问题）。
- doc/v4-migration/delivery-ANTI-R1.md。

本任务明确扩大旧 T3/T4/T5 的所有权，允许协调修改共享 Fragmenter 与 ApiMessage。其余生产代码、Gradle、审查用例和设计文档只读。不得清理 V3 或修改 engine 规则实现。

## 修复要求

1. 修复报告 1–8 的根因；让全部 9 项独立验收按原意通过。非法 JSON/缺字段/未知 api/错误连接身份在解码边界验证，单帧异常不能退出 Receiver。非法连接号先鉴别再处理，避免回射畸形请求。
2. Fragmenter 按连接+msgId 隔离，未绑定报文额外按来源 SocketAddress 隔离；校验 fragCount、index、原始类型、身份、ack 等一致性。缓冲和字节上限明确。允许保留旧 reassemble(frame) 供单连接测试使用，但服务器必须传来源上下文。不要仅全局随机化 msgId 掩盖隔离问题。
3. CREATE/JOIN/RESUME 的绑定前请求用来源+requestId 的有界在途/完成表去重，同请求重发得到同绑定，冲突 payload 明确拒绝。普通会话请求同样不能在首次尚未完成时重复执行。离房保留有界终态回执，丢响应重试能成功；旧绑定不能再操作新席位。
4. 明确窗口外全量 PUSH 标记/识别，client 可直接采用，不循环 RESEND；去除重复回调与分支重复发恢复请求。重连/切房重置 cursor、seq、syncEpoch，陈旧快照不能回滚新基线。不要关闭校验来掩盖丢包。
5. 绑定后 connectionId 必须严格匹配；只对对应绑定 RSP 设置握手例外。房主主动离开收到 APPLIED 并保留大厅连接，其余连接收到 ROOM_CLOSED。普通成员显式离开撤销令牌并更新大厅空位，开赛后交给 AI。关闭服务器先发终结通知后关 socket；KICK 维护有界 ACK/重试终态，收到 KICK 的 client 应能确认重传而不重复通知。
6. 心跳和重试状态机支持真实乱序、丢首包、丢响应、分片丢片；REQ 重试不能因较大 seq 已处理永久被过滤。未绑定心跳需要正确连续序号或独立语义，不能每次 PONG seq=1 导致健康连接被判死。不要削弱身份校验。
7. 修正 client 测试的缺 selection / 缺 ApiTags import；修正服务端回环辅助器一次重组、保存完成帧、按 requestId 匹配响应，保留异步 PUSH/KICK，不加长超时代替修复。原有测试意图保留。
8. 临时 requestControl(Boolean) 在 V4 绑定边界翻译为 true=收回、false=交出，与旧 engine 内部 true=托管相反；修正对应注释与测试。在 InProcessBinding 和 UdpRoom 同时处理，确保同契约同结果。R2 后改明确方法名。
9. 所有实际入站/超时判定使用注入时钟，不漏传到 UdpClientSession；按连接清理已关闭/被顶替状态，避免缓存无限增长。

## 交付和验收

逐项补正常/异常模块测试；真实两端测试仍由 Codex 跑。允许针对共享契约修正现有模块测试，但不得改工具目录的审查测试。如审查测试确需 API 迁移，报告具体变更给 Codex，不能跳过断言。

Codex 执行全量 test、独立 9 项+控制语义/乱序/重复绑定扩展验收、桌面编译和 Android APK 构建。交付报告必须写明修改文件、根因与仍未完成项目；不声称自己已测试。
