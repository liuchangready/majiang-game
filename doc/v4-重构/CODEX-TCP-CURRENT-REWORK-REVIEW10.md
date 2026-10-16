# TCP 返修第十轮独立复验：R09 两项修复通过，房间溢出仍阻断

日期：2026-10-16。Anti 原会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R09_FIX2 ready_for_review` 后，Codex 独立复验。

## 已通过

- 上轮两个失败探针 `listener registration cannot strand a concurrent early public update`、`mixed early streams share the 64 item admission bound` 均已转绿；源码确认监听器判空、早期入队与三队列总条数准入在同一 `stateLock` 内完成。
- R09 标准定向套件强制重跑通过，日志 `build/tcp-current-review10-r09-target.log`；标准全仓强制重跑 **1,245/1,245** 项通过，日志 `build/tcp-current-review10-all-forced.log`；Android 调试 APK 与桌面 jar 构建通过，日志 `build/tcp-current-review10-dual-build.log`。
- 原 12 项 Codex 独立用例均通过，日志 `build/tcp-current-review10-independent-forced.log`。新增第 13 项后独立套件为 12 过、1 败，日志 `build/tcp-current-review10-independent-13.log`。

## R09 剩余阻断

1. **房间信息溢出后最新版本丢失（独立复现）**：`dispatchRoomUpdate` 在早期队列已满时虽更新 `latestRoomInfo` 并置 overflow，但 `onRoomUpdate` 仅排水前 64 条，未向新监听器交付已缓存的最新版本。新增独立用例 `room update overflow must deliver the latest room version on listener mount` 建立真实观战绑定后送入 65 个版本，挂载监听器无后续 PUSH；版本 65 未交付，稳定失败，日志 `build/tcp-current-review10-r09-room-overflow.log`。玩家/观战 `snapshot()` 不等价于房间信息 DTO 更新。请在保持顺序和去重的前提下，交付最新 `ProtocolRoomInfo` 或执行对应全量查询。
2. **恢复重试耗尽后静默停滞（静态确认）**：`triggerPlayerRecovery`/`triggerPublicRecovery` 最多再试 5 次；达到上限后仍保留 pending/overflow，却既不继续重试，也不显式失败/触发重连。若之后无 PUSH，客户端永久维持缺失基线。需在有界重试耗尽后进入可观察的断线重连/失败状态，或采用受限而持续的恢复机制，不能静默悬挂。

另列入 R07 联动审查：`triggerPlayerRecovery`/`triggerPublicRecovery` 在 `stateLock` 内直接调用 `snapshot()`/`publicSnapshot()`，而当前请求仍同步写 Socket；慢端可在状态锁内阻塞。后续 R07 非阻塞化时必须一并移出锁外。

结论：**R09 仍为部分通过**；第 13 项 Codex 独立测试保持失败探针。R04/R14 已关闭，R07/R10 仍待修，R01–R16 整体与 T4/T5 继续待验收。
