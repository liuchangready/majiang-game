# V4 迁移状态

**2026-10-13 架构清理已验收：** 依当日架构检视结论执行卫生清理：presentation 测试包名全部对齐 `com.majiang.presentation.*`（含 screen/v3 旧包与 testing 同名包重叠消除，engine testFixtures 不动）、TsumogiriAIStrategy 迁入 `com.majiang.ai`、删除 client 重复 gson 依赖、MahjongGame tick 注释、ArchitectureBoundaryContractTest 新增表现层边界源码扫描测试；全部空目录骨架清除。Anti 开发、Codex 检视；归一化 diff 纪律检查、残留扫描、全量 1054 项测试、桌面编译与 Android APK 均通过，无测试削弱。详见 [ACCEPTANCE-ARCH-CLEANUP.md](ACCEPTANCE-ARCH-CLEANUP.md)；遗留的 protocol 包名重叠与 presentation 测试 engine 解耦列为后续事项。

**2026-10-07 连接控制归属收尾已验收：** Anti 将连接生命周期实现迁至 `server/control`，Codex 完成引用审查、旧文件删除、独立测试和文档终审。当前全量 928 项、独立审查 189 项，失败/错误/跳过均为 0；桌面与 Android 构建、真实 GL 冒烟、13 张 Mermaid 图语法检查通过。当前基线已在此前提交中清理重复渲染测试，因此本次计数与历史 R6 不同。详见 [本次验收](ACCEPTANCE-CONNECTION-OWNERSHIP.md)。下方 R1–R6 计数和未提交状态是历史记录。

**2026-10-07 18:37 最新：R1–R6 全部完成并验收，默认入口已切 V4，无用 V3 运行链已清理。** R6 最终不筛选测试 **978/978**、独立审查 **188/188**，均零失败/错误/跳过；桌面编译、Android 调试 APK、11 组真实 GL 冒烟和菜单真实 UDP 开赛/离房通过。保留全部有效规则、136 张物理守恒、算分、资源与时钟断言；具体删除替代和日志见 [R6 验收](ACCEPTANCE-R6.md)。下方各阶段“未完成”、额度和自动接续描述均为历史记录；已核对旧自动续接 PID 不存在，无本任务写入进程。当前改动在工作区，未提交 Git。

**2026-10-07 18:10 最新：用户要求直接继续，R5 功能门槛已通过并开始 R6。** Codex 在 Anti/GLM 额度阻塞后直接修复、迁移和验收；无 Anti/GLM 写入进程。全量 1015 项、桌面编译、Android APK 及 11 组真实 GL 冒烟均通过，证据见 [R5 验收](ACCEPTANCE-R5.md)。独立审查在菜单搬包前测得 188 项中 5 项失败；其中菜单包已修，余旧 TCP/V3 链与模块依赖归 R6，清理后必须重跑。当前并行清理分工：protocol/server、client、engine 三个文件范围，Codex 负责交叉测试、Gradle 串行验收与最终收口。R6 尚未通过。

**2026-10-07 12:46 最新：R5因Anti与备选GLM额度均耗尽暂停开发，持续目标按三回合相同阻塞审计标为blocked。** 无写入进程，374份当前源码哈希未变化；R4已接受，R5部分实现不编译且client仍有2项失败，R6未开始。恢复条件为Anti额度约15:35恢复、GLM报告10月9日14:46:33恢复，或用户明确更改开发者分工。具体反馈/接续命令见 [RESUME.md](RESUME.md)。

**2026-10-07 12:42 最新：R5受双提供者额度阻断，未完成。** 三路Anti均因明确429切本地GLM；GLM三路又因429/1310每周/月上限退出，服务报告10月9日14:46:33重置；Anti报告约12:29后三小时五分重置。无活跃写入进程，部分代码及哈希全部保留。Codex无writer时实测server111/111通过，client147项145过2失败（未知域名初连误报成功），presentation编译失败（GAME新画面未落盘及ROOT/COMMON自身错误）。诊断与精确续接见 [RESUME.md](RESUME.md) 顶部及三份FEEDBACK。R5不接受，R6未启动。

**2026-10-07 12:30 最新：R5三路均因Anti明确429配额耗尽，按用户已授权规则自动转本地Claude/GLM继续同一任务。** 原Anti进程全部退出，COMMON/GAME/ROOTFIX的部分改动保留，新GLM三路互斥运行，精确PID/会话见 [RESUME.md](RESUME.md)。R5尚未交付、编译或验收；R6未开始。

**2026-10-07 12:29 最新：R5 COMMON/GAME继续开发，ROOT初版已交付但源码审查未接受，Anti正进行ROOTFIX。** 返修涵盖单机真正开赛、席位/观战角色、离房失败保持可信状态、旧兼容构造与迟到回调；任务/PID见 [RESUME.md](RESUME.md)。R5尚未编译或验收，R6未启动。

**2026-10-07 12:07 最新：R5已实际并行派发三路Anti。** R4正式通过的943项全量、183项独立、桌面与Android构建证据见 [ACCEPTANCE-R4.md](ACCEPTANCE-R4.md)。R5 COMMON/GAME/ROOT 三个写入进程均真实存活，互斥文件范围及额度观察器见 [RESUME.md](RESUME.md) 顶部。R5开发尚未交付或验收，不运行共享Gradle；R6仍待R5通过。

**2026-10-07 12:08 最新：R4整阶段正式接受，R5即将启动。** SERVERFENCE 修复观战并发恢复，原门闩及新增代次测试通过；Codex最终不筛选全量test **943/943**、独立review **183/183**、桌面编译与Android调试APK构建成功，均零失败/错误/跳过。证据及源码哈希见 [ACCEPTANCE-R4.md](ACCEPTANCE-R4.md)。R5三路接口/所有权已冻结，接下来 Anti 并行开发 common/game/root，Codex串行构建验收；R6在R5通过后执行。旧状态段落均为历史，不代表当前失败仍存在。

**2026-10-07 11:36 最新：R4 客户端子批已接受，服务端继续并发恢复返修；R5/R6尚未启动。** CLIENTFENCE 的独立14/14与client模块144/144均通过。SERVERCHECK2服务端主源和测试已编译，server模块110项中1项失败（InProcess观战并发恢复旧请求错误成功）；Codex独立review 183/183通过。Anti现以 `R4B2SERVERFENCE` 独占服务端修此真实竞态，具体执行与日志见 [RESUME.md](RESUME.md)。整R4尚未接受；通过全部模块和双端后再并行派发R5。下方历史“R4后暂停”已被用户继续R5/R6的新授权撤销。

**当前：用户指定Anti开发，R4验收后继续R5/R6，不在R4暂停。** SERVERRESUME/CLIENTRESUME均已正常交付退出，Codex实际验收未通过：客户端独立14项10过4失败（缓存锁并发提交污染新绑定）；client模块141项137过4失败；server因UdpRoom协议参数名错误生产编译失败，server测试/完整独立/双端尚未跑。已真实并行接续Anti SERVERCHECK和CLIENTFENCE返修，旧写入进程已结束，真实PID/exec/日志与新额度观察器见RESUME顶部。349份源码哈希、测试XML/结果JSON和反馈均保存。R5/R6准备与有效测试保留清单已落盘，阶段开发尚未启动。下方blocked/GLM指定/停止边界均为历史记录。

**18:21补充：Codex独立客户端运行时测试4项实跑，2通过2失败，零错误/跳过。** 已复现重复full重复通知与离房后的旧PUBLIC迟到通知；证据R4B2-GLM-quota-runtime-review.log / -results.json。GLM额度尚未恢复，两路已退出，无开发进程，R4仍未接受；完整独立现在预计171项，不能拿本次4项替代整体验收。

**18:18当前：GLM两路因429/1308五小时用量上限退出，服务提示今晚23:00:05重置。** 已确认两PID消失、exec均exit1，无正式交付；全部改动保留、通知已检视但不接受。Codex实测client主源码编译成功，server因PUBLIC缓存读取和重试常量缺失编译失败；client模块测试因fixture签名不符未能编译，尚未执行。本批完整独立及双端验收未运行。新增反馈、日志、346份当前源码哈希和接续会话已归档，详见RESUME顶部。用户指定GLM开发仍有效；当前没有活跃开发进程，R4未完成，不进入R5/R6。

**18:00当前：用户直接指定GLM开发、Codex验收。** 原Anti两路实际进程/执行句柄均不存在，无交付或quota证据，部分实现全部保留。真实编译定位server公开快照构造缺失和client校验函数作用域错误；两端尚未接受。已并行启动本地Claude Code的glm-5.2[1m]接续SERVERCONT与CLIENTCONT，精确PID/session/日志见RESUME顶部。WIRE仍为已接受的136模块+13独立。完成整个R4后保存并暂停，不进入R5/R6。

更新日期：2026-10-06。用户指定 Anti 开发，额度用完自动转 Claude/GLM；Codex 测试验收。不保留 V3 入口兼容性。

**16:11当前（覆盖下方历史）：** WIRE协议正式接受：模块136/136、独立13/13全通过，证据归档。16:10:51已实际并行派Anti SERVER/CLIENT完成观战真实接入，protocol冻结，精确进程/日志见RESUME顶部。R4整阶段仍未完成；两端交付后还需模块/完整独立/真实UDP与双端构建验收。完成R4即保存暂停，不进入R5/R6。

**15:49当前（覆盖下方历史）：** WIRE原批已退出但未完成：生产编译通过；模块测试fake缺新方法编译失败，独立协议12项2通过10失败。已实际并行派Anti WIREFIX补protocol主源码、WIRETEST补该模块测试，范围互斥。后者首次启动网络EOF已退出后重试，未发生额度耗尽。真实进程/日志与接续步骤在RESUME顶部；当前不能接受WIRE或启动SERVER/CLIENT，整个R4仍未完成。R4全部通过后保存并暂停，不进入R5/R6。

**最新停止边界：用户要求完成R4后停止。** R4未完成，现在继续；整体验收通过后保存并暂停，不派发R5/R6。PUBLIC与PREVIEW已接受，当前Anti开发观战WIRE；后续仍须完成SERVER与CLIENT实际接入。当前真实PID/日志与停止点见RESUME顶部。

**15:17当前：** PREVIEWTESTFIX仅修改server加杠fixture后，server105/105通过；相关四模块合计704全部通过、独立134/134通过，PREVIEW已接受，证据已归档。Anti WIRE于15:16:22实际启动，独占protocol，只做协议阶段验收；新增抽象接口后的两端接线由下一SERVER/CLIENT批完成。R4整阶段尚未通过，默认产品仍V3。

**15:13当前实测：** PREVIEW返修后完整独立134/134通过，engine369/protocol112/client118模块全通过；server105项104通过1失败，仅新增加杠测试仍使用非法单张action。Anti PREVIEWTESTFIX已实际启动且仅改这份测试，生产冻结。待server模块复验通过再接受PREVIEW/派WIRE；整个R4尚未完成。上一PREVIEWFIX写完交付后API EOF退出，非额度耗尽，未切GLM。精确接续见RESUME顶部。

**14:42 实测进展（覆盖后面的旧数字）：** PUBLIC纯领域投影已接受（模块12、独立11全部通过），还不是观战工作流。四模块首轮engine367/protocol110/server105通过，client新测试属性错误导致编译失败；完整独立133项130通过3失败，其中2项fixture适配后专项通过，余1项真实鸣牌展示组合与动作不一致仍被codec放行。已实际派发Anti PREVIEWFIX修该生产缺陷和client测试；待复验通过后进行WIRE与观战SERVER/CLIENT。真实会话/PID/日志见RESUME当前执行点，不进入R5/R6。

**13:55 最新：R4B1正式通过，已自动推进R4B2。** B1模块847项零失败/跳过、独立114/114、桌面/APK证据已归档（ACCEPTANCE-R4）。Anti两路并行：R4B2PREVIEW私有展示预览端到端、R4B2PUBLIC独立公开领域投影。完成通知/持续目标/额度自动接续已运行。B2仍需后续观战server/client真实接入；R5/R6未开始，默认产品仍V3。真实PID与接续看RESUME顶部。

本轮监督补充：两路Anti仍开发，已看到三层预览DTO与纯PUBLIC初版代码；新增17项Codex独立用例尚未编译运行（累计目标131，待实测）。已将深层里宝授权、action及流局outcome可变别名、预览round/option绑定反馈给开发者；观战wire/角色/独立流接线草案已完善但未派发。没有新的验收通过结论。

**13:50 当前（覆盖下文）：自动接续目标已启用。** 用户要求Anti完成即通知Codex检视/下一步，桥接已加入待检视事件，持续目标负责继续，非文件本身唤醒会话。CHECKFIX模块client111/111、server100/101，余一项进程内离房通知测试未等待锁外回调；已派发仅测试修复R4B1ROOMTESTFINAL，生产冻结。最新生产独立114/114与双端构建证据仍有效。B1未正式接受，下一步交付后自动模块验收、通过后派发B2。详情RESUME顶部。

**13:44 最新状态（覆盖下文）：R4B1未接受。** FINALFIX两路交付后，独立114/114再次通过；client110/111（新增并发测试未排水建房dispatcher而超时），server测试缺ApiResponse导入未编译，不能沿用旧通过数。engine348/protocol104/presentation183零失败/跳过，桌面/APK构建任务成功。已实际启动SERVERCHECKFIX、RUNTIMECHECKFIX，仅改模块测试，生产冻结。PID/日志/额度接续见RESUME顶部。B2/R5/R6未开始，默认入口仍V3。

**11:49 当前：未完成验收，两个 Anti 已并行返修。** 三路FINAL已交付；独立114/114通过，旧callback缓存竞态回归已绿。本轮模块845项840通过5失败：client108/109、server97/101，其余模块零失败，零跳过。桌面编译/APK任务成功。已派发SERVERFINALFIX（停服定时器清理及测试装配）、RUNTIMEFINALFIX（查询测试装配及并发投递核对），真实PID/日志/额度接续见RESUME.md。R4B1尚未接受，B2/R5/R6未开始，默认入口仍V3。下方记录均为历史。

**当前：09:14:38已启动三个Anti任务并行开发UDPSERVER、UDPCLIENT、RUNTIME。** 原串行R4B1UDPRESUME已由Codex停止，保留所有改动。共享协议冻结，文件所有权互斥，构建由Codex串行执行。用户授权Anti配额耗尽时自动转Claude/GLM。ROOM接受记录不变，当前UDP/运行时仍须验收；接续见RESUME.md。

09:17自动额度接续观察器已运行，真实任务状态见build/v4-migration/parallel-r4b-20261006-0914.status.json。仅明确quota耗尽且原写入进程退出才转GLM，不自动接受交付；暂停方法见RESUME.md。当前3项均为Anti运行中，尚无新交付验收结果。

**01:00最新状态：ROOM已于00:55接受（专项23项、server91项通过）；GLM已开始R4B1UDP。** 完整独立历史111项103通过8失败；新增最后房间更新丢包用例后目标112，基线因UDP大厅未路由而失败。当前调用与接续以RESUME.md顶部为准，下面FIX4/FIX5及Anti额度描述均为历史。

当前接续：已确认本地 Claude Code 配置模型 glm-5.2[1m]，派发 TASK-GLM-R4B1ROOMFIX4.md 修复 ROOM 离房竞态和模块测试。下文 Anti 配额信息为历史记录。当前验收基线仍为独立100/109、server83/86，尚未宣布新的通过结果。

00:43 GLM FIX4已交付，Codex中间专项22/22通过（含新增普通成员离房用例），server90项88通过2失败；非0真人牌数断言已随后修正，通知测试和关闭/离房收尾并发窗口继续返修。已实际派发 TASK-GLM-R4B1ROOMFIX5.md，沿用同一 GLM 会话。ROOM未接受，UDP尚未派发。完整独立目标因新增用例变为110，尚未重跑全部。

当前产品仍走 V3 默认入口，V4 基础组件已落盘但未完成产品切换。最终契约见 [CONTRACT.md](CONTRACT.md)，顺序见 [PLAN.md](PLAN.md)。

## 最近通过的 R1 基线

- R1 独立验收扩展到 21 项，全部通过；[验收记录](ACCEPTANCE-R1.md) 保留返修与失败历史。
- 全量 Gradle 检查成功，模块 XML 合计 678 项、零失败、零跳过。engine 340 项为 UP-TO-DATE，其余模块本轮执行。
- 桌面编译与 Android APK 构建通过，不等于 V4 已接入或移动端已真机验证。
- [最初独立审查](REVIEW-2026-10-05.md) 的 1 通过/8 失败，以及 T1/T2/T3 的 632 项为历史记录，不作为当前状态。

**用户于 2026-10-05 19:42 明确恢复任务，继续 Anti 开发、Codex 监督验收。** 从 R2FIX4 接续，保留既有修改。此前暂停交接见 [RESUME.md](RESUME.md)。

20:13 R2 正式通过。R2FIX5 修复了零基局数和可空响应 ID 的过度校验；独立检查 52/52、模块 XML 合计 721 项零失败零跳过，桌面编译与 Android APK 构建成功。R2 接受当前 V4 三层 DTO 边界，旧 TCP/展示依赖仍按计划留待 R5/R6 清理。证据见 [验收记录](ACCEPTANCE-R2.md)。

## 当前任务

| 阶段 | 状态 | 内容 |
|---|---|---|
| R1 | 通过 | 16:52 独立回归 21 项全绿；全量 test、桌面编译、Android APK 构建通过。证据 R1FIX3-review.log 与 R1-final-checks.log；详见 ACCEPTANCE-R1.md |
| R2 | 通过 | 独立 52/52，模块 721 项零失败零跳过，桌面/Android 构建通过 |
| R3 | 通过 | 20:51 独立检查 78/78、模块 745 项零失败零跳过，桌面/Android 构建通过；Anti R3FIX1 已结束 |
| R4 | R4A、R4B1、B2 PUBLIC/PREVIEW通过；WIRE开发中 | 当前相关四模块704项、独立134项通过。正在完成观战协议，随后SERVER/CLIENT实际接入及整体验收；整体R4尚未通过 |
| R5 | 本轮不启动 | 用户要求R4完成后暂停；V4产品入口与展示拆包待后续明确恢复 |
| R6 | 本轮不启动 | 用户要求R4完成后暂停；V3清理与最终验收待后续明确恢复 |

开发日志在 build/v4-migration。delivery-ANTI-R*.md 仅表示开发交付，必须经 Codex 实测才能更新为通过。未通过前不提前删除仍承载产品行为的旧实现。

21:46 接续点已保存至 [RESUME.md](RESUME.md)。Anti 提示额度约在 2026-10-06 00:43（北京时间）恢复，此为服务端提示估算；本轮没有开发进程继续运行，也未安排自动重试。R4A 接受的 357 个源码哈希均未改变，新增文件仅 Codex 大厅验收用例。此处是外部额度阻塞，不是用户新发出的暂停指令。

21:51 用户再次要求继续，重新派发日志 R4B1-1791208311080。以上21:46无运行进程为历史接续状态，本次调用结果待确认。新增准备撤回验收，基线 R4B1-lobby-baseline3.log 为5项预期失败；没有重复运行未变化的R4A全量测试。

22:14：ProtocolRoomModels.kt和LobbyApi新大厅接口已落盘，R4B1-protocol-interim.log编译通过。公共服务和绑定仍待实现，不代表B1通过。独立大厅用例扩为6项（新增配置权限/重置准备/客端推送/实际计时）；后续需跑累计95项及新增模块用例。R4B2简报仅为待派发草案。

22:39：R4B1调用于22:36:51触发45分钟时限并退出，部分实现已保留；同一会话派发R4B1CONT1继续修复。编译错误及架构审查证据见FEEDBACK-R4B1.md，当前engine编译失败，新行为未验收。新增两个房间订阅运行时用例后，独立目标累计97项。用户没有暂停，继续按Anti开发/Codex验收推进。

22:50：CONT1未产生新交付，于22:45由Codex停止自己的CLI后拆成DOMAIN小批，当前Anti会话3c26e98f-10df-4e67-ac10-4ec9c127160f执行中。领域独立3项可经v4-domain-review.init.gradle只在engine运行，待实现；运行时新增5项、大厅6项，加历史89累计103。R4B1ROOM草案为领域通过后的下一批，尚未派发。

23:47最终记录：DOMAIN及FIX已接受；ROOM经过FIX1/FIX2，单次21专项曾通过，但完整回归及强化离房循环发现竞态，未接受ROOM。FIX3完成了暂停测试接线及非0真人座位部分装配后，于23:42:34因429退出，提示3h9m28s恢复（预计10月6日02:52）。当前源码/测试能编译，独立109项100通过9失败，server86项83通过3失败，均零跳过。证据R4B-current-review.log、R4B-current-server.log和对应results.json；317个Kotlin源码哈希已存档。无开发CLI运行，未安排自动重试；用户GUI保留。此为外部阻塞，不是用户暂停。剩余清单和恢复命令见RESUME.md；UDP任务仅草案。
