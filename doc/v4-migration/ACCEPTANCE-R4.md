# R4 验收记录

## R4 整阶段接受（2026-10-07 12:07）

R4A、R4B1及B2 PREVIEW/PUBLIC/WIRE/CLIENT/SERVER 全部完成并通过 Codex 独立验收。最终 SERVERFENCE 将观战恢复的通知与完成链移出 TableHost 单线程权威队列，并以严格递增代次隔离迟到顶号通知；原并发门闩测试及新增旧通知回归全部通过。Anti 桥接末尾报告 stream interrupted，但进程真实退出、文件和交付文档完整；Codex 仅以实际文件和测试裁决。CLIENTFENCE 先前子批14/14、client模块144/144通过。

- `build/v4-migration/R4B2-SERVERFENCE-full-checks.log`：不筛选的全量 `test`、桌面 `:lwjgl3:compileKotlin`、Android `:android:assembleDebug` 成功。模块 XML 合计 **943/943**，engine369、protocol136、client144、server111、presentation183，零失败/错误/跳过。
- `build/v4-migration/R4B2-SERVERFENCE-final-review.log`：最终源码独立审查 **183/183**，零失败/错误/跳过，含实际UDP观战恢复、注册窗口/关房竞态、客户端公开流隔离和门面类型边界。
- 计数与当前347份Kotlin/Java源码哈希保存在 `build/v4-migration/R4B2-accepted-test-counts.json`、`R4B2-accepted-source-hashes.json`。旧宿主有效行为已由新server coverage承接；规则、守恒、计时和算分测试仍执行。

**R4正式接受，按用户最新授权继续R5产品表现/入口迁移，再做R6旧链清理。** 此处的桌面/Android构建只证明当前过渡源码可构建，R5后仍须真实产品画面/双端复验；旧V3入口尚未在R4切换。

## R4B2 最终接线验收进行中（2026-10-07 11:50）

CLIENTFENCE 已由 Codex 接受**客户端子批**：隔离运行时/真实UDP观战14/14、client模块144/144，零失败/错误/跳过；日志与XML汇总为 `build/v4-migration/R4B2-CLIENTFENCE-acceptance-summary.json`，当前client源码哈希见同目录 `R4B2CLIENTFENCE-accepted-hashes.json`。这不代表R4整体通过。

SERVERCHECK2 交付后，Codex补其模块测试中单个 `ResendResponse` import，服务端主源与测试现均能编译。`R4B2-SERVERCHECK2-server-module-fix.log` 实测110项109通过、1失败：InProcess 观战并发恢复中，第一次被顶替的恢复因同步通知阻塞权威队列约5秒而错误成功。Codex 保留有效断言，已将生产返修独占派发 Anti `R4B2SERVERFENCE`；该任务尚未交付。同期 `R4B2-SERVERCHECK2-REVIEWFIX-independent.log` 及XML的183项独立检查全过、零失败/错误/跳过，包含真实UDP观战与绑定注册竞态。独立通过不能掩盖上述模块失败。当前没有全量test、桌面/Android本批构建成功证据，R4仍未接受，R5/R6未启动。

## R4B2 WIRE 协议小批通过（2026-10-06 16:10）

原WIRE只交部分实现，WIREFIX补齐真实codec/请求与令牌隔离，WIRETEST补模块fake/用例，WIREFINAL修classifier显式null/非法无type信封和模块JUnit取值错误。Codex独立串行复验：`R4B2-WIREFINAL-protocol.log`136/136、`R4B2-WIREFINAL-review.log`13/13，零失败/错误/跳过。覆盖typed PLAYER/ROOM/PUBLIC、严格完整PUBLIC_RESEND检查点、公开快照及六种历史outcome、完整结算评分/负分/赤五、事件白名单、私有字段隔离、深层不可变、五种观战请求和token脱敏。合法旧PLAYER/ROOM/PREVIEW模块回归保留。

`R4B2WIRE-accepted-test-counts.json`及`R4B2WIRE-accepted-source-hashes.json`归档，WIREFINAL交付事件标为reviewed/accepted。此前源码哈希对比仅授权protocol及Codex reviewer修改，无旧领域规则/宿主测试缺失。只接受协议阶段，ServerConnection新方法的具体SERVER/CLIENT接线仍待实现，不能把此记录当观战流程或R4全阶段已通过。用户边界仍为整个R4通过后保存并暂停，不进R5/R6。

前置 R3 已于 2026-10-05 20:51 验收通过。R4 分为 R4A（领域权威与服务器异步宿主）和 R4B（统一本地/远端房间、大厅与观战）。阶段通过必须两批均完成；不提前进入 R5。

## R4A 开发前基线

20:54 交 Anti 开发 R4A。R4-authority-baseline-2.log：5 项独立检查中 2 项通过、3 项失败。已有有效行为：竞争请求只有一次出牌、非法请求不刷新 deadline 且超时只执行一次。待修：慢观察者阻塞调用与查询、TableAuthority 缺失、两种 V4 绑定仍引用旧 engine TableHost/SessionAccess。

Codex 将旧宿主相关测试源码与数量记录为 build/v4-migration/R4-host-test-preservation-baseline.json。旧测试阶段保留不等于新宿主已受测试；需逐项迁移或新增等价覆盖，规则、计时、AI 过期结果、准备期守恒与结算断言不能删除。独立测试源码 V4AuthorityAcceptanceTest.kt/V4AuthorityBoundariesTest.kt，实时反馈 FEEDBACK-R4A.md。

当前尚未验收。

## R4AFIX1 中间验证

R4A 初次交付编译未通过，已交 Anti 返修。R4AFIX1-modules-interim.log：engine 345 项中 343 通过、2 失败；失败均在新 TableAuthorityTest，旧 340 项通过。服务器仍因 UdpRoom 快照缓存缺少 PlayerSnapshot 导入编译失败，尚无本轮服务器行为测试结果。

独立 R4A 检查已扩展为 10 项，新增宿主队列阻塞、慢回执消费者、过期 AI、关闭在途 Future、执行时旧连接校验。Anti 新增 ServerTableHostCoverageTest 对应旧宿主 20 项行为，仍待编译与实测。反馈与日志保留，不以新增测试文件数代替通过结果。

## R4AFIX2 中间验证

R4AFIX2-regressions-interim.log：累计独立检查 88 项零失败零跳过，包含 R1—R3 与 R4A 新 10 项。R4AFIX2-modules-interim.log：领域模块 345 项通过。服务器生产和测试均已编译；R4AFIX2-server-interim2.log 共 66 项、7 项失败。失败定位为已发牌 initialState 未建立初始时钟、nextRound 在终局/准备期仍记录 ready、重复码牌回执使用新游标，以及旧立直测试 LocalHuman 接线和异步通知等待。交 R4AFIX3 继续修复。

## R4A 通过（2026-10-05 21:44）

R4AFIX3 修复上述问题，回执缓存按操作/局/座位隔离并有界淘汰；新增独立测试验证码牌与出牌相同请求 ID 互不覆盖。新 ServerTableHostCoverageTest 保留旧 20 项有效行为并新增关闭码牌直接开局检查，共 21 项；旧五份测试文件哈希与基线一致。

- R4AFIX3-final-review.log：89/89 独立检查通过、零跳过，包含真实 UDP 断线/恢复和 R4A 11 项检查。
- R4AFIX3-final-checks.log：全量 test、桌面编译、Android assembleDebug 成功；模块 XML 共 776 项，engine345、protocol99、client83、server66、presentation183，零失败、零错误、零跳过。engine/protocol 如为 UP-TO-DATE，已分别由本批前序实际执行验证。
- 计数归档 R4A-accepted-test-counts.json，源码归档 R4A-accepted-source-hashes.json，均位于 build/v4-migration。

本批接受纯领域 TableAuthority + server 异步宿主在两种 V4 绑定中的接线；旧宿主暂留原产品链至 R5/R6。**R4 整体未通过**：R4B 统一房间、大厅、观战与展示预览尚未完成。R4B 分为 B1 玩家大厅/共享服务，B2 独立观战投影与展示预览；完成后才能进入 R5。

## R4B1 接受（2026-10-06 13:54）

DOMAIN、ROOM、UDP 和客户端房间运行时均已完成小批实现与返修。最后ROOMTESTFINAL仅修正测试等待真实锁外disconnect回调，保留所有断言；Anti进程退出，结构化交付通知已由Codex消费。

- 最新 `R4B1-ROOMTESTFINAL-modules.log`：server101项通过，client111项通过（client未变，UP-TO-DATE）。上一轮 `R4B1-CHECKFIX-modules.log` 中client111项实际执行通过，server仅异步通知测试失败；该项本次已通过。
- `R4B1-FINALFIX-review.log`：完整独立114/114，零失败/跳过，包含旧callback提交污染、同room重建隔离、真实UDP最后更新丢包、离房回执、两次断线恢复。其后生产未变，仅模块测试装配/等待修正。
- `R4B1-FINALFIX-full-checks.log` 中最新生产的桌面compileKotlin与Android assembleDebug任务成功；该次整体构建失败的模块测试已在上述后续模块运行解决。未重跑未变生产的全部构建，不把多份证据说成单次全绿构建。
- 当前模块XML合计847项零失败/错误/跳过：engine348、protocol104、client111、server101、presentation183。汇总及当前源码哈希：`R4B1-accepted-test-counts.json`、`R4B1-accepted-source-hashes.json`。
- 旧宿主5份有效规则/计时/准备测试共20方法相对 `R4-host-test-preservation-baseline.json` 哈希未变。

本记录仅接受R4B1。R4B2观战/展示预览尚待完成；R4整阶段、R5产品切换、R6清理均未接受。默认产品仍V3，当前无V4产品GL实机验收。

## R4B1 开发前基线与额度阻塞

21:44 调用 Anti R4B1，21:45 因 RESOURCE_EXHAUSTED/429 退出；日志 R4B1-1791207841359.stderr.log，提示约 2h57m52s 后恢复。无本批生产代码改动，已验收 357 个源码哈希全部一致。

Codex 新增 V4LobbyAcceptanceTest.kt。第一次基线因测试误用领域便捷属性未编译，已改为协议真实字段；有效基线 R4B1-lobby-baseline2.log 共 4 项、4 项失败：缺少大厅状态/准备/配置/推送契约、本地离房不支持、联机未准备仍能开局、缺少真实准备流程。失败是本批待实现能力，不撤销 R4A 验收。后续累计验收须同时包含 com.majiang.v4lobby.*，不得把这四项从最终验收中排除。

## R4B1DOMAIN 小批通过（2026-10-05 22:58）

首次R4B1大批超时留下未完成实现，CONT1停止后拆小交付。Anti DOMAIN及DOMAINFIX修复了开局前配置原子更新：实际赛制/分数/码牌点数/时钟生效，sessionId稳定；开赛或码牌选择后拒绝更新。DecisionClock恢复不可变配置，协调器更新入口收为internal并在提交前验证配置和版本。

- R4B1DOMAIN-final-review.log：独立领域3项通过，零跳过。
- R4B1DOMAIN-final-engine.log：engine完整348项通过，零失败/错误/跳过。
- build/v4-migration/R4B1DOMAIN-accepted-test-counts.json及accepted-source-hashes.json记录本批证据。
- server仍编译失败（R4B1-after-domain-compile.log），client主源码编译成功。公共房间及绑定待ROOM小批，UDP/运行时随后；本记录不接受整个R4B1，不表示当前全项目构建通过。

## ROOM返修与当前未通过结果（2026-10-05 23:47）

ROOM经FIX1/FIX2修到生产可编译，独立21专项曾单次通过；随后完整server测试发现离房成功却被旧回调误关的竞态。Codex将原独立离房/重建/开局场景强化为同连接循环20次，保留全部断言，稳定重现问题。ROOM不予接受。

最终当前源码：R4B-current-review.log独立109项100通过9失败，R4B-current-server.log服务端86项83通过3失败，均零跳过。9个独立失败包含离房竞态、UDP大厅未接入4项、严格消息信封1项、客户端房间排序/查询/离房失效3项；服务端其余两个失败为房主未ready及合并通知的测试前提，处理要求见FEEDBACK-R4B1ROOMFIX3.md。

Anti FIX3于23:42:34返回429，预计10月6日02:52额度恢复，未完成剩余修复。接续记录、结果JSON与317个Kotlin源哈希已保存。R4B1、R4B2、R5、R6均未接受，默认产品仍V3；未运行当前全项目桌面GL/APK验收。

## ROOM 小批通过（2026-10-06 00:55）

用户指定 Claude Code 的 GLM 接续。FIX4 初版专项22项通过，但完整server90项88通过2失败，且静态审查发现关闭校验与实际关闭分离、离房收尾Future未串联。新增重叠离房独立探针真实复现 APPLIED 后连接误关；未接受该初版。

GLM FIX5将关闭校验和关闭认领原子化，锁外执行通知与资源释放；重叠离房共用在途Future，调用方得到回执前完成解绑/标志复位/终局补裁。修正通知测试时序，新增实际内部绑定回调入口的迟到回调和真实顶号覆盖；非0真人装配测试通过。未修改DOMAIN、UDP或客户端生产代码。

- `GLM-ROOMFIX5-full-review.log`：完整独立111项103通过8失败、零跳过；ROOM相关23项全部通过。剩余为UDP大厅4项、严格房间信封1项、客户端房间运行时3项，符合后续未实现范围；历史已通过能力没有新增失败。
- `GLM-ROOMFIX5-final-server.log`：最终server91项全部通过、零失败/错误/跳过。
- `GLM-ROOM-accepted-source-hashes.json`、`GLM-ROOM-accepted-test-counts.json`及`GLM-ROOMFIX5-full-review-results.json`保存证据。旧5份宿主回归文件哈希保持不变。

本记录仅接受ROOM小批，R4B1整体仍未通过。下一批UDP共用SharedRoom/RoomRegistry、可靠房间推送与严格信封；之后修客户端运行时，再B2/R5/R6。当前默认产品仍V3，未重做当前GL/APK验收。

## R4B2 PUBLIC 纯投影小批通过（2026-10-06 14:41）

初次PUBLIC交付的模块fixture有虚构计分类型，且自定义事件列表比较与iterator边界存在问题，Codex检视后交Anti返修。PUBLICTESTFIX交付并退出后，本次实际执行：

- `R4B2-FIRST-modules.log`：engine367、protocol110、server105零失败/错误/跳过；其中PUBLIC模块12项全通过。client因新增preview测试的isRed属性编译错误未执行，不能使用旧XML计数。
- `R4B2-FIRST-review.log`：完整独立133项130通过3失败；PUBLIC 11项全通过，覆盖私牌/资源/历史隔离、显式里宝授权、深层评分保真、source/getter不可变、数组读取、流局嵌套集合与copy/equals/iterator。
- 其中两处独立测试fixture已由Codex正确适配：弃牌已提交但响应窗口未结束时RECORDED合法，并加强真实状态/事件/物理牌验证；旧runtime假快照更新decisionId时同步更新preview元数据。`R4B2-FIXTURE-review.log`两专项通过，未改生产。
- 剩余真实PREVIEW缺陷为codec接受type/handTiles与真实动作矛盾，已派发R4B2PREVIEWFIX，连同client测试属性接线一起修；完整独立尚未全绿。

仅接受PUBLIC纯领域投影，本小批无网络/观战实际接入。R4B2整体与R4未完成，不推进WIRE直到PREVIEW接受。用户最新要求完成R4后暂停，不进入R5/R6。

## R4B2 PREVIEW 小批通过（2026-10-06 15:16）

PREVIEWFIX补齐三层鸣牌结构与权威动作一致性校验、真实物理副本/赤五/来源/牌数和顺序约束，strict codec调用校验构造。Codex对照真实MeldChoiceProvider纠正加杠action四张、handTiles一张的任务描述。开发者在写完交付后发生API EOF退出；不是配额耗尽，未切GLM，按实际落盘源码验收。

- `R4B2-PREVIEWFIX-review.log`：完整独立134/134通过，零失败/错误/跳过。PREVIEW独立9项涵盖真实待牌/立直/五类鸣牌、三层映射、物理赤五、不可变集合/动作、metadata绑定与五类非法组合严格拒绝；PUBLIC独立11项及前序114项同时通过。
- `R4B2-PREVIEWFIX-modules.log`：engine369/protocol112/client118全部通过；server105项104通过，唯一失败是新mapper测试仍以单张action装配加杠。随后Anti PREVIEWTESTFIX仅改该测试并加强四张/单张/来源/赤五断言，全部生产源码相对reviewed哈希不变。
- `R4B2-PREVIEWTESTFIX-server.log`：server105/105通过，零失败/错误/跳过。合计相关四模块704项全通过，证据来自两次串行运行，未声称一次全绿构建。
- 计数与当前源码哈希归档`R4B2PREVIEW-accepted-test-counts.json`、`R4B2PREVIEW-accepted-source-hashes.json`。已接受PUBLIC三份源码/测试哈希保持不变，前序归档源码无缺失、原有效规则测试未变。
- `R4B2-SETTLEMENT-FIELD-AUDIT.md`静态逐项核对现有Renderer/Presenter所需计分/恢复字段，现有domain→protocol→client/JSON已齐备，不另造评分账本。该审计不替代后续观战真实网络隐私/结算验收。

仅接受PREVIEW小批。下一步WIRE公开协议，随后SERVER/CLIENT实际观战接入；整个R4仍未完成。没有新的桌面/APK或GL运行验收结论，不进入R5/R6。R4整体验收通过后按用户要求保存并暂停。
