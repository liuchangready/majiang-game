# R4B2 SERVER CONT：用户指定GLM接续真实观战服务端（明确执行）

恢复执行说明：上一GLM run于18:16因429/1308五小时额度退出，没有交付；现在已过服务报告重置时间，Codex核实原writer/bridge均不存在，347份源码与保存点一致后恢复本会话。先读FEEDBACK-R4B2SERVERCONT.md最新实测部分，再读原任务；你自己的部分实现全部保留。实际server编译失败及UDP尚未接完的路由/装配/ticker/清理必须一并完成，不能只修常量和.get()。CLIENT由另一GLM并行修复，protocol冻结。Codex统一构建，开发者不跑测试，不改reviewer。

用户17:58明确要求“继续使用glm来开发，你来验收”。你是本地Claude Code已配置的GLM开发者，Codex负责独立构建/验收。原Anti SERVER PID35288、CLIENT PID30568及bridge/watchers在17:57经实际进程与exec句柄核验均已不存在，未交付、无quota证据；不是等待中的并发旧任务。保留所有部分实现，从当前代码接续，不重写已接受能力。用户边界为整个R4完成后暂停，不进入R5/R6。

先读CONTRACT.md、TASK-ANTI-R4B2SERVER.md、R4B2-SERVER-REVIEW-NOTES.md、FEEDBACK-R4B2SERVER.md。原任务完整行为要求继续生效，开发主体改为GLM；本文件覆盖旧标题中的Anti指令。只用Read/Edit/Write/Glob/Grep，不跑shell/build/test/其他agent，不改settings/凭据，不改Codex reviewer。

## 独占范围（并行CLIENTCONT不得跨写）

server/src/main及相关模块测试：SharedRoom/必要RoomRegistry、ServerTableHost、PUBLIC mapper、InProcessServer/Binding、UDP UdpTableServer/UdpRoom/UdpClientSession等真实接线。engine仅允许TableAuthority.kt原任务授权的最小PUBLIC只读入口；engine/projection与其他领域规则冻结。protocol全部冻结（136模块+13独立验收已过）；client归另一GLM，presentation/平台/build/reviewer不改。

## 当前真实状态与接续优先级

- 原批只落盘TableAuthority公开查询、新PublicProtocolMapper、host独立PUBLIC stream/订阅/发布结构、SharedRoom独立观战成员及InProcess部分权限接口；**server UDP文件目前没有本批观战接线**。不能把单纯修编译当完整交付。
- Codex17:57运行`R4B2-BINDINGS-interrupted-compile.log`：engine/protocol编译过；server失败，ServerTableHost多处引用未定义buildProtocolPublicSnapshot，后续copy类型推断错误源于此。补真实公开投影→协议构造，勿新增玩家快照兜底/兼容别名。toProtocolGamePhase先前笔误已经修为toProtocolPhase，不重复修改。
- FEEDBACK-R4B2SERVER.md的实际缺陷必须修：完整PUBLIC checkpoint在InProcess/SharedRoom/host被退化裸cursor并忽略身份；resumeSpectator缺已绑定角色互斥；观战私有queryRoundResult成功返回emptyList而非明确拒绝；替换观战仅改generation但无主动终结旧连接，不能依赖下一次游戏动作通知。
- 实现剩余**真实UDP**：SpectatorApi五请求、观战独立身份/代次，严格PUBLIC_RESEND sessionId+syncEpoch+cursor，256历史内补cursor+1、窗外full，最新消息稳定seq/分片主动重试及合法ACK取消，不能只被动RESEND。角色/代次校验必须先于幂等缓存命中；ROOM/KICK/player不受PUBLIC ACK污染。
- 保留四座状态/AI/计时不受观战加入退出影响；意外断线可恢复、显式leave撤销token、旧close不撤销新绑定，房主离房/停服正确终结观战且自身回大厅。队列内权威操作，锁外回调/Future完成和发送，公开订阅及任务在所有终结路径确定性释放。
- SharedRoom可选initialState已在原任务明确授权（仅可信装配，不入wire），确保非空时生命周期按真实已发牌状态初始化；供Codex真实Tsumo/公开完整评分/历史恢复验收。

所有结算、公开history/outcome/供托归属/评分保真，复用真缓存，不座位0伪观战、不另造账本、不空事件绕过隐私测试。补模块测试并保留所有旧规则/宿主/ROOM/player断言；你不运行测试。Codex reviewer新增观战checkpoint与绑定角色拒绝用例，仅可读、不可改。

结束前读取FEEDBACK-R4B2SERVERCONT.md（若有）及原FEEDBACK-R4B2SERVER.md，完成后写delivery-GLM-R4B2SERVERCONT.md，列实际文件/完整接口/未完成项、声明未运行测试。交付后结束由桥接通知Codex，不自行派下一阶段。
