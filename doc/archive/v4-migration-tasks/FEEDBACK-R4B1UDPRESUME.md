# Anti UDP 接续：Codex 当前编译/检视证据

2026-10-06 09:11，`build/v4-migration/ANTI-UDP-resume-baseline.log`：protocol及client主源码编译通过，server主源码失败。

`UdpTableServer.kt` 未定义三个已使用常量：`ROOM_UPDATE_RETRY_BASE_MILLIS`（168/216）、`ROOM_UPDATE_MAX_ATTEMPTS`（181）、`ROOM_UPDATE_REFRESH_MILLIS`（207）。请按既有重试/心跳规则补完，不要只补常量就宣称任务完成。

原GLM没有完成client UDP ACK接线，也没有更新UdpRoom新构造的模块测试/reviewer调用点。需补全可靠推送与测试前提迁移。详情TASK-ANTI-R4B1UDPRESUME.md、FEEDBACK-GLM-R4B1UDP.md。特别检查分片重传ACK身份与ReplayGuard先后顺序；丢最后ROOM_UPDATE的独立用例必须真的丢包后自动恢复。

Codex只拥有reviewer接线/测试与构建。不要修改tools/architecture/v4-review；交付明确需要的构造/ready前提变更。

09:13严格房间信封独立检查已通过（`ANTI-UDP-protocol-review.log`），不代表整个UDP已接受。Codex已迁移reviewer两处前提：V4ReviewAcceptanceTest真实host/guest开赛前setReady；V4AuthorityAcceptanceTest用SharedRoom装配UdpRoom并setReady。原顶号/投影/时钟/超时断言全部保留，无删除测试。
