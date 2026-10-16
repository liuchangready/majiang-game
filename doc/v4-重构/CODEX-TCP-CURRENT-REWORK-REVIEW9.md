# TCP 返修第九轮独立复验：R09 仍退回

日期：2026-10-16。Anti 沿用会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R09 ready_for_review` 后，Codex 独立复验。

## 已确认进展

- R09 标准定向套件强制重跑通过，日志 `build/tcp-current-review9-r09-target.log`；标准全仓强制重跑 1,243 项通过，日志 `build/tcp-current-review9-all-forced.log`；原 10 项 Codex 独立测试通过，日志 `build/tcp-current-review9-independent-forced.log`；Android 调试 APK 与桌面 jar 构建通过，日志 `build/tcp-current-review9-dual-build.log`。
- 客户端已用实际 `TcpFrame.payload.size` 记早期字节，并在监听器挂载时尝试触发玩家/观战全量查询。单流 65 条、无后续 PUSH、字节计数、旧游标和角色切换的新增标准测试通过。

## 仍阻断 R09 的问题

1. **监听器挂载竞态使更新永久滞留（独立复现）**：`dispatchPublicUpdate`/`dispatchPlayerUpdate` 在 `stateLock` 外先复制监听器列表，发现为空后才入锁排队；`onPublicUpdate`/`onUpdate` 在该间隙可完成注册与排水。先判空、后注册排水、再入队的交错会把消息留在早期队列，无后续 PUSH 时无法交付。新增独立用例 `listener registration cannot strand a concurrent early public update` 用同一状态锁强制该交错，稳定得到“应收 1，实收 0”，日志 `build/tcp-current-review9-r09-race.log`。需在同一串行/锁边界决定入队或派发，并确保先排水后在线消息的顺序。
2. **64 条连接级上限被三队列分别计算（独立复现）**：当前 player/public/room 各自检查 `.size < 64`，虽然共用字节预算，房间信息与当前角色更新就可合计达到 128 条。新增独立用例 `mixed early streams share the 64 item admission bound` 以房间信息和公开流各 40 条模拟同一观战连接，合计 80 条仍准入，日志 `build/tcp-current-review9-r09-mixed-cap.log`。需在 `stateLock` 下以三队列总条数原子准入，触顶走溢出恢复。
3. **全量恢复失败没有重试状态**：`onPublicUpdate`/`onUpdate` 在发出 `publicSnapshot()`/`snapshot()` 前即把 `earlyBufferOverflow` 清零（约 `TcpClientBinding.kt:1735-1753,1800-1818`），`whenComplete` 只处理成功分支。若请求失败或超时，丢弃的早期推送仍丢失，标志也已清零，且无后续 PUSH 时不会再次补查。设计 §6/§9 明确要求全量恢复失败后重试。应保留恢复待办状态、合并在途查询，并在有界重试/重连后重获完整基线，受代次约束。

另需注意：新增“2 MiB/单帧 >4 MiB”字节测试调用 `dispatchPlayerUpdate` 注入模拟长度，超过 MJ-TCP/1 单帧 1 MiB 上限；它可以验证内部计数，却不能作为真实线缆大帧验收。无需删除该测试，但应补符合线路上限的多帧累积场景。

结论：**R09 部分通过，不能关闭**。新增两项 Codex 独立用例保持为失败探针，不得改弱。R07/R10 仍待修，R04/R14 已关闭；R01–R16 整体与 T4/T5 继续待验收。
