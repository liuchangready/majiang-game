# R4B2 CLIENT CONT：用户指定GLM接续观战客户端（明确执行）

恢复执行说明：上一GLM run于18:16因429/1308五小时额度退出，没有交付；现在已过服务报告重置时间，Codex核实原writer/bridge均不存在，347份源码与保存点一致后恢复本会话。先读FEEDBACK-R4B2CLIENTCONT.md最新实测部分，再读原任务。client主源码已编译通过，但ProtocolViewMapperTest类型/参数不符导致模块测试编译失败；Codex独立runtime4项实跑2失败（重复full通知、成功leave后旧PUBLIC通知）。你的部分实现全部保留，必须修复并继续覆盖原全部行为；server由另一GLM并行补齐，protocol冻结。开发者不跑测试，不改Codex reviewer，交付后Codex统一验收。

用户17:58直接要求GLM开发、Codex验收。原Anti两路和其bridge/watchers已核实不存在、无交付、无quota记录；保留部分实现，本批不是与旧任务重叠写入。只完成整个R4，验收后暂停，不进R5/R6。

先读CONTRACT.md、TASK-ANTI-R4B2CLIENT.md、R4B2-CLIENT-REVIEW-NOTES.md、FEEDBACK-R4B2CLIENT.md。完整原任务行为要求继续生效，开发主体改为本地Claude Code配置的GLM。只文件工具，不跑shell/build/test/其他agent，不改设置/凭据/Codex reviewer。

## 独占范围

client/api的PUBLIC模型/门面/映射/ServerConnectionSession、client UDP绑定及必要调度器装配、对应模块测试/fakes。protocol全冻结（136模块+13独立已验收）；server及TableAuthority由并行SERVERCONT持有。其他engine、presentation、平台、build、V3链不改。

## 当前证据与完成要求

- PUBLIC DTO、SpectatorSession、Lobby观战入口、ProtocolViewMapper、runtime及UDP初版已落盘；新SpectatorSession三个默认空成员已改abstract，别再加默认空行为。
- Codex17:57真实`R4B2-BINDINGS-interrupted-compile.log`：client在ServerConnectionSession.kt约2471处validatePublicGameEventView引用失败。实际校验为PublicViewModels.kt顶层函数，按真实作用域修正；不能删除PUBLIC事件校验让编译通过。server单独错误由另一GLM负责。
- **必须修原FEEDBACK-R4B2CLIENT.md全部已实证缺陷**：PUBLIC没有ACK、ReplayGuard先占seq后丢缺口消息导致补发永远被拒、full任意跨epoch/回滚、publicSnapshot/resumePublic/selfHeal旧Future跨bindingEpoch污染、runtime ROOM更新内层仍只认player currentBinding、旧恢复回调清掉新isPublicResuming。
- PUBLIC使用独立严格连续游标、有界重排/早期缓冲、完整checkpoint RESEND、重复合法消息仍ACK但用户回调恰好一次；full仅当前合法流且不回退，换流仅受当前绑定保护的权威恢复响应。早期缓冲溢出必须有明确恢复，不能静默丢最后一次变化。
- join/resume互斥玩家/观战，wrong-role推送不污染另一个缓存；自动重连使用spectatorId/token/checkpoint，至少连续两次重连仍稳定。显式离房可回大厅再加入，旧连接/旧room generation/Future/延迟dispatcher不能污染新会话；关闭/替换/ROOM_CLOSED/SERVER_SHUTDOWN无自动抢占。
- 所有公开字段/完整结算评分/历史outcome与供托归属保真、嵌套不可变、公开门面不泄漏protocol/engine；真正SpectatorSession只读契约不继承玩家写接口。所有监听器锁外，close/leave/恢复失败清理订阅/调度/receiver/outstanding Futures。
- 保留原PLAYER/ROOM/PREVIEW模块测试。原批新增/修改的三个模块测试也要核对实际编译与fixture，不删有效断言；Codex会跑真实InProcess/UDP丢最后消息/首ACK/缺口/超窗full/私有接口拒绝/两次恢复/停服receiver退出。

完成后写delivery-GLM-R4B2CLIENTCONT.md列实际文件、接口、未完成项，明确你未运行测试。结束前读FEEDBACK-R4B2CLIENTCONT.md（若有）及原反馈；不自行跨入R5/R6。
