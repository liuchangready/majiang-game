# R4B2 CLIENT RESUME：接续GLM部分实现，修复观战同步与生命周期

用户最新要求使用Anti，并允许R4验收后继续R5/R6。你仅完成本批R4客户端，不自行进入后续阶段。Codex已结束刚重启的GLM两进程并取得exec终止，所有已落盘改动保留，无旧writer重叠。

先读CONTRACT.md、TASK-ANTI-R4B2CLIENT.md、R4B2-CLIENT-REVIEW-NOTES.md、FEEDBACK-R4B2CLIENT.md和FEEDBACK-R4B2CLIENTCONT.md最新实测反馈。历史文件中的GLM开发者/R4停止要求被最新用户授权覆盖，技术契约继续生效。

独占范围：client/api PUBLIC模型/门面/mapper/ServerConnectionSession、client UDP绑定及必要调度器装配、对应模块测试。protocol完全冻结；server和TableAuthority由并行SERVERRESUME持有；不改其他engine、presentation、平台、build、V3旧链、Codex reviewer。只文件工具，无shell/build/test/其他agent/设置/提交。

Codex当前证据：client主源码编译通过；模块测试在ProtocolViewMapperTest缺ProtocolPublicCheckpoint及TileView多余第4参数处编译失败。真实协议为com.majiang.server.api.PublicCheckpoint，可import alias；TileView仅tileId/suit/rank，isRed由id派生。只修正确测试签名，不能新增生产兼容壳或删除断言。

独立V4SpectatorRuntimeIsolationTest已实跑4项2失败：同cursor full导致[3,3]重复通知；成功leave清空缓存后排队的旧PUBLIC通知仍被派发。旧恢复Future顺序隔离及异流full拒绝两项通过，但不能据此跳过UDP恢复标志归属与锁间竞态修复。日志R4B2-GLM-quota-runtime-review.log及-results.json。

必须完成：
- UDP/runtime full去重，合法重复仍ACK但只回调一次；普通消息严格连续、有界重排、丢包补发和窗外full恢复；早期缓冲溢出明确恢复。
- 恢复标志由对应请求/绑定代次拥有，旧Future不能清除新标志、阻塞新恢复或污染缓存。所有推送/早期缓冲/延迟dispatcher贯穿来源身份，不在派发时把旧数据认作新epoch。
- 完整只读SpectatorSession、PLAYER/ROOM/PUBLIC隔离与字段保真；显式leave后可重入，意外断线用spectator凭据连续恢复两次；替换/停服等终局清理订阅、timer、receiver与Future。
- 保留全部有效PLAYER/ROOM/PREVIEW模块测试，新增观战回归；Codex完整独立预计171项，含真实InProcess15/UDP5/运行时4，均不得修改或删除。

结束前读FEEDBACK-R4B2CLIENTRESUME.md（若存在）及CLIENTCONT反馈，写delivery-ANTI-R4B2CLIENTRESUME.md，列具体文件、未完成项、未运行测试。生产交付后由Codex统一串行构建验收。
