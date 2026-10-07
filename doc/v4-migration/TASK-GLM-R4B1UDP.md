# R4B1 UDP 共用房间业务与可靠房间更新

2026-10-06 00:55，Codex已接受ROOM小批：相关独立23项通过、最终server91项通过。完整独立111项103通过8失败；下一批正式派发给 Claude Code 的 GLM。保留已接受的ROOM及DOMAIN行为。只使用文件工具，不运行shell/构建/测试/提交，不改reviewer或设置，不再委派。

阅读 CONTRACT.md、TASK-ANTI-R4B1UDP.md 的技术要求、FEEDBACK-R4B1.md 和实际源码。后者原草案的“未派发”状态被本任务授权覆盖，历史Anti所有权改为GLM。直接实施，不只给建议。修复完成后写 delivery-GLM-R4B1UDP.md，并在结束前查看 FEEDBACK-GLM-R4B1UDP.md（如果存在）。

## 本批范围

- server UDP adapter/session/server routing、必要的共享服务接入、protocol房间信封、client UDP传输绑定，以及对应模块测试。
- UdpRoom只保留网络会话、绑定身份、重传和终端ACK等传输职责。成员、准备、配置、开赛、离房、恢复必须使用进程内同一个SharedRoom/RoomRegistry；不再复制大厅/权威状态，不向SharedRoom引入UDP/socket。
- GET_ROOM_INFO、SET_READY、UPDATE_CONFIG真实路由；联机所有真人准备齐后仅房主可开赛；开局前配置更新实际生效并重置准备；resume使用服务端真实游标。保留单机装配策略。
- ROOM_UPDATE严格显式信封：拒绝缺失/错误type、额外信封字段、裸旧JSON。移除未发布的ProtocolRoomInfo兼容构造器并更新调用点。
- 房间版本与对局推送游标分开；房间更新完整视图经可靠ACK/重试或等价恢复，丢失最后一条也会恢复，不能依赖下一次用户操作或UI轮询。旧连接/旧房间、乱序和重复不能污染当前状态；不要用裸lifecycle/members分支绕过可靠机制。
- 保留R1的顶号/令牌撤销vs意外掉线恢复、离房成功回执幂等重放、停服终端重试与socket linger、R4A时钟/AI/有界权威队列。绑定校验传递至队列实际执行期。房主leave成功返回未绑定大厅，其他成员可靠收到ROOM_CLOSED。
- 模块测试覆盖真实或假时钟下房间推送丢失/乱序、ready/config/start及生命周期。旧UDP测试新增真实setReady前提，保留原有效隐私/计时/终局断言。若reviewer需改前提，写到交付供Codex调整，禁止自行编辑tools/architecture/v4-review。

## 重要边界

ServerConnectionSession客户端房间缓存/查询/离房失效3项仍在后续runtime小批，本批不擅自修改。B2观战/展示预览、R5产品入口、R6清理均不在本批。不要升级依赖或重写规则。不要为编译保留第二份房间业务或旧LOCAL占位兼容路径。

拆解实现时先保持编译结构清楚，避免大批未闭合的接口改名。交付只写实际调用链、改动、未验证/未完成项；测试结果由Codex实测。
