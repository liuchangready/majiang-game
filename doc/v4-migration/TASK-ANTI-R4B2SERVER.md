# R4B2 SERVER 实际观战接入（Codex明确授权执行）

16:10 Codex已接受WIRE：protocol模块136/136、独立协议13/13全部通过。PREVIEW/PUBLIC此前接受，现按真实签名核定并授权本批，bridge实际调用为执行凭据。Anti仅文件工具开发，禁止shell/build/提交/改设置/再次委派；Codex统一测试验收。用户最新边界为完成整个R4后暂停，绝不进入R5/R6。

实际WIRE交付见delivery-ANTI-R4B2WIREFIX.md和delivery-ANTI-R4B2WIREFINAL.md（原WIRE未写delivery，由这两批补完并验收）。ApiMessageCodec.classifyPush(String):PushType、encode/decodePublicSnapshot、encode/decodePublicUpdatePush、encode/decodePublicResendRequest已落盘，精确类型以protocol源文件为准。ResumeSpectatorRequest字段为resumeToken/checkpoint；ApiRequest对应token/publicCheckpoint。SpectatorBinding含roomId/spectatorId/resumeToken/snapshot，不能混用玩家RoomBinding。protocol冻结，任何真实缺口先写FEEDBACK或交付报告给Codex协调，不能自行改。

先读CONTRACT.md、已接受WIRE交付及`R4B2-SERVER-REVIEW-NOTES.md`。该notes的所有权、只读权威入口、身份权限、可靠性与验收要求作为本任务具体要求；若与已接受WIRE签名冲突，先报告Codex协调，不自行改protocol。

## 独占修改范围

server的room/SharedRoom、RoomRegistry（仅必要接线）、host/ServerTableHost、mapping、inprocess绑定/服务、udp绑定/会话/服务及相应模块测试。允许最小扩展engine/authority/TableAuthority.kt的PUBLIC只读投影和公开结算查询，参考notes推荐签名；复用已接受PublicProjectionFactory和真实结算缓存，不改已接受private preview/规则算法。新增相关server内部类型允许，但不新建第二套房间服务或评分账本。

为通过真实房间绑定验证确定性和牌/完整历史，明确允许SharedRoom构造器增加可选`initialState: GameState? = null`，传给已存在的ServerTableHost initialState入口。仅可信组合根/测试装配可传，不加入wire请求或允许远端上传GameState；初始房间lifecycle仍须从真实权威状态推导，不能伪装大厅绕过准入。测试可注册已有单机四座房间再通过合法绑定/观战入口操作，不反射替换tableHost、不新增规则提交后门。

protocol冻结，client由另一Anti独占；engine/projection、presentation、平台入口、build及reviewer不改。保留所有已接受ROOM、PLAYER、计时、守恒测试。

## 完成标准

- 独立观战成员、token、generation在四座之外。满房可加入，不假造seat、不影响四座控制/AI/准备/资源。
- TableAuthority纯读PUBLIC查询，ServerTableHost单桌队列内读取、独立PUBLIC epoch/cursor/256历史，队列外有序通知/序列化/外部Future完成。保留完整公开board/history/outcome/供托归属和真实授权结算评分。
- InProcess与UDP全部真实实现SpectatorApi，明确未绑定/玩家/观战事实。同一连接互斥角色；观战私有查询和写入全部拒绝，失败不改变权威状态。token不能跨角色，旧generation即使命中缓存也不能继续操作。
- WIRE提供的PUBLIC_RESEND checkpoint必须核对角色/session/epoch，cursor为已应用基线，补发cursor+1起；窗口外full基线。玩家原RESEND独立保留。
- 最后一条PUBLIC更新丢失且之后无动作仍可恢复：最新消息稳定seq/分片主动重试，ACK不误清ROOM/KICK；订阅、调度器、窗口、Future在leave/close/替换/停服时确定性清理，含晚注册竞态。
- 显式leave撤销token并返回大厅；意外断线保留恢复资格；旧连接关闭不撤销新连接；host离房/停服终结所有观战者。幂等LEAVE回执在丢包时真实重放。
- 模块测试覆盖notes八组风险，使用真实接口与可控时钟/调度，不删除或弱化有效断言。不能用生产默认unsupported/no-op或空公开事件满足接口。

交付`delivery-ANTI-R4B2SERVER.md`，列具体文件、实际接口、对CLIENT的注意点、未完成项，明确未运行测试。结束前读取FEEDBACK-R4B2SERVER.md（若存在）。源码交付不等于验收通过。
