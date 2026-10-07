# R4B2 SERVER CHECK：修复交付编译及测试接线

用户指定Anti开发、Codex验收，授权继续R5/R6。上一SERVERRESUME于11:00:31已正常退出（exec81238 exit0/PID5560不存在），保留全部改动。本批只server主源码及其模块测试；protocol/engine/client/展示/平台/build/reviewer冻结，CLIENTFENCE独占client。

Codex实际运行 `:server:test :client:test --continue`，日志 `build/v4-migration/R4B2-ANTIRESUME-modules.log`。server主源码编译失败：UdpRoom.kt:541 `ProtocolPublicUpdate(full = true)`，冻结协议真实参数为 `isFullSnapshot`。先修这个实际错误，再仔细检查新增模块fixture/API，不要仅改一词便忽略明显测试接线缺陷。

尚未运行到server模块测试，以下为源码预检、不是实测失败：
- UdpRoomTest负数PublicCheckpoint在构造器已require拒绝，不能先构造再期待handlePublicResend返回null；保留非法checkpoint拒绝语义，验证正确边界（或原始wire非法输入）。
- testUdpTableServerSpectatorEndToEnd调用异步handleFrame后立即spectators.keys.first()/getSpectatorSession，存在空或未安装窗口；应等待真实响应或可控completion。
- 角色非法请求权限判断在缓存之前直接发RSP，不写成功请求缓存。该测试用waitForResponse(session cache)等待非法请求必然不能证明响应。改接实际UDP数据报/捕获发送响应，不能为fixture将越权响应塞进成功幂等缓存。保留双方角色权限拒绝与公开/私有字段断言。
- 合法checkpoint使用当前snapshot.cursor，不凭空假设PUBLIC从0开始；严格窗口及超前/溢出断言保留。

原SERVERRESUME、SERVERCONT契约和反馈继续生效。注册补PUBLIC、关闭/代次防迟到复活与InProcess订阅生命周期修复不得回退；独立server新增2个确定性竞态测试仍待执行。查看现有API签名补必要import，不自行改protocol。

只使用内置文件工具，不运行shell/build/test或其它agent。交付前读FEEDBACK-R4B2SERVERCHECK.md（若有），写delivery-ANTI-R4B2SERVERCHECK.md，列实际文件/未完成/未运行测试，随后结束，由Codex统一验收。R4未通过，不开发R5。
