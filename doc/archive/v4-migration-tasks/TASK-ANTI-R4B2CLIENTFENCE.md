# R4B2 CLIENT FENCE：修复实际缓存提交竞态

用户指定Anti开发、Codex验收，授权R4后继续R5/R6。本批仅R4客户端返修。上一CLIENTRESUME已正常退出且PID已核实不存在；保留其所有实现。先读FEEDBACK-R4B2CLIENTFENCE.md、原CLIENTRESUME任务与最终契约。

独占client主源码与相应模块测试。优先最小修复ServerConnectionSession PUBLIC缓存提交的4项确定性竞态；UdpClientBinding隔离5项已通过，不应为绕过测试重写无关部分。protocol/server/engine/presentation/平台/build与tools reviewer均冻结。禁止shell/build/test/启动其它agent；Codex串行验收。

缓存锁内核对来源connectionEpoch、roomEpoch、room及恢复token所有权，旧身份不能写回/排水/释放或替换新恢复token；查询响应不能仅在锁外验证。新增模块回归，保持公开门面无内部类型泄漏、锁外用户回调和有效10项修复。不得修改reviewer或弱化断言。

同时修复反馈新增的client模块141项中4项失败：观战reconnect fixture实际应返回新连接；恢复13后应继续排水已缓存14；两个PLAYER UDP测试需先建立真实合法绑定再验证自愈/早期回放。全部原行为断言保留，不能为旧fixture撤掉角色隔离或正确排水。

交付前读FEEDBACK-R4B2CLIENTFENCE.md新补充，写delivery-ANTI-R4B2CLIENTFENCE.md，列文件/未完成项/未运行测试，然后结束。R4尚未接受，不能提前开发R5。
