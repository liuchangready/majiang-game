# R4B2 SERVER RESUME：接续GLM部分实现，完整交付服务端观战

用户最新要求使用Anti，并允许R4验收后继续R5/R6。你只开发本批R4服务端，后续阶段由Codex验收后另行派发。Codex已结束刚启动的GLM SERVER PID23828与CLIENT PID27164，实际exec95970/10170均终止，无并发旧writer；此前GLM额度退出及刚才短暂接续的所有改动保留，不回滚。

先读CONTRACT.md、TASK-ANTI-R4B2SERVER.md、R4B2-SERVER-REVIEW-NOTES.md、FEEDBACK-R4B2SERVER.md，以及最重要的FEEDBACK-R4B2SERVERCONT.md最新实测反馈。历史文件中的GLM开发者/R4后停止不约束本批最新派发；技术要求继续生效。

独占范围：server/src/main与相关模块测试；engine只可改authority/TableAuthority.kt的最小PUBLIC只读入口。protocol已经136模块+13独立通过，完全冻结；client由并行CLIENTRESUME持有；不改其他engine、presentation、平台、build、Codex reviewer。只使用文件工具，不运行shell/build/test、其他agent、提交或设置修改。

当前必须完成：
- 修复真实编译失败：UdpRoom AtomicReference读取缺.get()；UdpTableServer缺PUBLIC重试常量。不能仅修编译就交付。
- UdpTableServer补PUBLIC_SNAPSHOT/RESUME_PUBLIC/QUERY_PUBLIC_ROUND_RESULT会话路由、观战角色与generation先于缓存命中的权限验证；装配publicUpdateSender，ticker接refreshPendingPublicPushes，所有leave/close/替换/停服路径清理。
- 严格checkpoint全三字段，拒绝超前cursor及Long.MAX_VALUE溢出；256窗内补发/窗外明确full，最后消息丢失且无后续动作也能主动可靠恢复，重复ACK不干扰ROOM/KICK。
- 修复InProcess观战join/resume迟到完成覆盖新绑定、订阅安装/关闭竞态；成员代次跨线程可见性；玩家/观战互斥，token隔离，主动顶替通知与旧连接关闭不撤销新绑定。
- 保留真实完整公开评分/history/outcome/供托与隐私白名单，不使用玩家快照兜底/空事件/no-op替代实现。补足模块回归，保留旧规则/计时/ROOM/PLAYER测试。

已存在Codex独立InProcess15项、UDP5项以及完整前序验收。全套预计171项，以实测为准；你不得改reviewer。完成后读FEEDBACK-R4B2SERVERRESUME.md（若存在）和SERVERCONT反馈，写delivery-ANTI-R4B2SERVERRESUME.md，说明实际文件、未完成项、未运行测试，然后结束交Codex验收。
