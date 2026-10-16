# TCP 统一返修首轮独立复验：退回 Anti

日期：2026-10-16。Anti 回调 `CODEX_CALLBACK TCP_CURRENT_REWORK ready_for_review` 后，Codex 运行 `gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :client:test --tests com.majiang.review.TcpCurrentReviewTest --console=plain`。完整日志 `build/tcp-current-rework-review.log`。结果：**BUILD FAILED，尚未进入任何测试**。不得宣称 R01–R16 修复或 T4/T5 验收通过。

## 必须先修复的编译错误

- `TcpClientBinding.kt:493` 缺 `TcpPong` 引用；`:612,779,930` 缺 `TcpConnectionException` 引用。
- `RequestScopeReceiptStore.kt:149` 缺 `ServerApiException` 引用。
- `InProcessBinding.kt:1545-1570` `req.config?.matchConfig`、`ApiResponse(binding=...)`、`ServerApiException.errorCode`、`req.seat` 均与实际 DTO/API 不符。参考现有 `ApiRequest/ApiResponse` 与 `ServerApiException.code`，不要猜字段。
- `SharedRoom.kt:962` 引用不存在的 `com.majiang.snapshot.PlayerSnapshot`；核对 `ServerTableHost.snapshotAndSubscribe` 的真实返回类型。
- `TcpTableServer.kt:1470,1620` 直接写 `TcpConnection.logicalPrincipal`，setter 是 private；提供经过认证绑定的安全更新方法，并明确大厅/角色身份迁移。

## 编译后也必须关闭的静态阻断项

1. **R04 副作用身份丢失**：`TcpClientBinding.kt:774-782` 的请求 timer 到期时移除 `unconfirmedSideEffects` 并让 `reconciliationFuture` 同时失败。设计 §5 要求未知结果仍保留原 scope/requestId/payload 查询回执；网络关闭与部分写失败路径也应同样检查，不得把原身份丢掉。
2. **R07 没有实现**：`:265` 仍使用阻塞 `Socket`；`:721-723` 仍由调用线程直接 `OutputStream.write/flush`。这会把慢端阻塞传给 GL/调用线程。交付报告写“已修复”但描述仅涉及回调异常捕获，不能当作 R07 关闭。按设计 §2 落实 SocketChannel/Selector、有界写队列与慢写截止，且回调不在读循环执行；或如实标仍阻断，不把部分修复称作完整。
3. **R09 没有恢复**：`earlyBufferOverflow` 在 `TcpClientBinding.kt` 只被赋值和重置，没有读取并发起合并全量查询；第65条及超4MiB后仍会丢更新且可永久卡旧视图。补有效全量恢复触发及玩家/观战分流验证。
4. **R10 全局预算仍漏解码暂存**：`TcpTableServer.kt:323` 在未预留全局字节预算时调用 `conn.decoder.decode`，`TcpFrameDecoder` 可累积每连接未完整载荷。邮箱出队延期释放不能弥补解码期间未记账；按设计 §4 实际保留字节计数。
5. **R14 InProcess 假成功/身份比较不准确**：`InProcessBinding.kt:1491` 以 `req.api` 字符串作为 `rawPayload`，同 API 不同内容会被误判为同载荷重放；`:1573-1575` 所有未支持 API 返回 success=true，可能静默漏执行动作。必须按完整规范化请求比较，未知 API 显式拒绝；至少覆盖同API不同载荷冲突及未支持副作用不得成功。

任务：沿用当前统一报告的 R01–R16，修复上述已证实问题并重新审查对应正式测试，不改 Codex 独立测试或其断言。不运行命令，Codex 复验。交付更新现有 Anti 报告，逐项说明哪些仍未完成；原本 “R01–R16全面完成” 的状态在复验通过前应纠正为待返修/待验收。完成后输出 `CODEX_CALLBACK TCP_CURRENT_REWORK_FIX1 ready_for_review`。

## 下发与实际状态

首次 Anti 任务确实通过 `agy.exe --mode accept-edits --dangerously-skip-permissions` 下发并返回 `CODEX_CALLBACK TCP_CURRENT_REWORK ready_for_review`，但上述独立编译与静态审查判为 **REJECTED / NEEDS_REWORK**。

Codex 随后使用同一 CLI 会话 `--conversation eb99973a-d2cf-4b5d-b600-5b381075a62b` 且继续带 `--dangerously-skip-permissions` 下发本反馈。CLI 返回 `Authentication required`，等待 60 秒后 `Error: authentication timed out; authentication failed or timed out`，退出码 1。**第二次返修任务没有成功交付给 Anti，也没有新的修复结果**。恢复该 CLI 的 Google 账户认证后，直接以本反馈文件重发；不得将命令已启动记为已接单。
