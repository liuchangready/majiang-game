# R04 专项第六轮复验：主要路径通过，仍退回

日期：2026-10-16。Anti 在同一会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R04 ready_for_review` 后，Codex 独立复验。

## 已通过

- `gradlew.bat test --rerun-tasks --console=plain`：标准 1,234 项（engine 470、protocol 166、server 166、client 146、presentation 286）全部通过，0 失败/错误/跳过。日志 `build/tcp-current-review6-all-forced.log`。
- 原 Codex 9 项独立用例全绿，日志 `build/tcp-current-review6-independent-forced.log`；Android 调试包与桌面 jar 构建通过，日志 `build/tcp-current-review6-dual-build.log`。
- 源码中一般副作用超时、写失败、临时断线与重放超时现保留 `unconfirmedSideEffects` 和原请求对象，迟到正确 RSP 可兑现 `reconciliationFuture`；Anti 新增了超时及断线回归测试。

## R04 剩余阻断

1. **无效 RSP 仍擦除未知副作用身份（已独立复现）**：`TcpClientBinding.kt:539-548` 的 requestScope 不匹配分支不区分副作用，直接 `unconfirmedSideEffects.remove(reqId)` 且使对账 Future 失败。该帧不能证明服务端未执行原动作；删除原 `(scope,requestId,payload)` 违反设计 §5。新增真实 TCP 独立用例 `TcpCurrentReviewTest.wrong scope response cannot erase unknown side effect identity`，执行命令和失败日志见 `build/tcp-current-review6-r04-invalid-rsp.log`，稳定失败于记录为空的断言。应拒绝错误 RSP，但保留副作用身份供后续原身份对账；只读请求仍可直接失败。
2. **128 条恢复记录上限非原子**：`TcpClientBinding.kt:754-756` 先读 `ConcurrentHashMap.size`、`:791-792` 再写入，多线程并发可一起通过检查，突破声明的 128 条硬上限。请将容量预留与插入统一为原子准入，或采用等价严格有界机制，补并发回归。
3. **恢复重放绕过协商在途上限**：`replayPendingSideEffects()` 在 `:957-981` 遍历最多 128 条记录并全部写入 `inFlightRequests`，不检查 `limits.maxInFlightRequests`（默认 64）。应限量调度，完成/超时一项后继续下一项；保留原身份，不以新 requestId 重做。补超过 64 条恢复记录的边界用例。

结论：R04 **部分通过但不能关闭**。请沿用同一 Anti 会话修复上述三项；R07/R09/R10/R14 继续待修。T4/T5 仍未验收。
