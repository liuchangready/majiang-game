# MR-03 独立检视与验收记录（Claude Code 主会话）

- **任务**: IMPROVEMENT-PLAN.md §4 MR-03「传输及大厅全局缓存的确定性淘汰」
- **开发**: Anti（两轮：首版 + R1/R2 返修）
- **检视与测试**: Claude Code 主会话（本轮验收方，替代 Codex 流程）
- **基线**: `8a4b59f`（分支 `refactor/remove-legacy-cheat-params`）
- **独立验收测试**（Claude Code 编写，位于 `tools/architecture/v4-improvement/`，Anti 全程不可修改）:
  - `protocol/Mr03CompletedRequestCacheReviewTest.kt`（7 用例）
  - `server/Mr03SessionCacheReviewTest.kt`（8 用例）
  - `server/Mr03LobbyCacheReviewTest.kt`（9 用例）
  - 装配：`tools/architecture/v4-improvement.init.gradle`（本轮新增 server 模块接线）

## 第一轮验收（2026-10-15）

| 套件 | 结果 |
|---|---|
| Claude 独立 MR-03 套件（24 用例） | 23/24 通过；1 失败为 R1 缺陷用例 |
| 模块套件 :protocol:test :client:test :server:test | 全部通过（含 Anti 新增 2 个模块测试文件） |
| 独立 V4 审查（v4-review.init.gradle, PreviewOnly） | 全部通过 |

首轮退回 Anti 的缺陷（详见 ANTI-MR03-REWORK-TASK.txt）：

- **R1（严重）finally 兜底摧毁异步副作用请求的在途身份**：`handleLobbyReq` 的 try 块为副作用 API 注册异步完成回调后同步返回，外层 `finally` 立即（`reservationConsumed` 仍为 false 时）递减预留、比较删除在途条目并异常完成 promise。每个异步副作用请求的并发去重在受理瞬间失效，同 ID 重复请求被重新执行（重复建房）。由 `concurrent duplicate create request creates exactly one room` 用例捕获（两份相同 CREATE_ROOM 建出两个房间）。存量模块测试无此场景，故未拦截。
- **R2 completeLobby 在 lobbyLock 锁内完成 Future 与发送 RSP**：违反「Future 完成放在缓存锁外」契约；等待方 whenComplete 回调（含 socket 发送）在完成线程上于锁内执行，慢发送会阻塞全部大厅请求。

测试侧勘误（Claude 自查自纠，非实现问题）：
- `Mr03CompletedRequestCacheReviewTest` 首版两处 ACK 计数笔误（`complete()` 本身产生 ACK 未计入）与一处 rspSeq 字面量笔误（10_002 应为 10_001），已修正。Anti 曾主动上报其中字面量笔误且未借机迎合实现，合规。

## 第二轮（返修复验，2026-10-15 02:20–02:40）

返修内容（仅改 UdpTableServer.kt 与 Mr03ModuleCacheTest.kt，符合文件所有权约束）：

- **R1 修复**：`InFlightLobbyEntry` 新增 `asyncHandoff` 标记；六个异步完成分支在注册 whenComplete 后、return 前置位；`finally` 释放条件改为 `isSideEffect && !asyncHandoff && CAS(reservationConsumed)`。检视确认竞态安全：异步回调先完成时 CAS 已被 completeLobby 消费而跳过；回调未决时 asyncHandoff 阻止误释放；仅「从未移交完成路径且从未消费」才释放。
- **R2 修复**：completeLobby 的 `nextOutboundSeq`/`promise.complete`/`sendRsp` 全部移出 `synchronized(lobbyLock)`（记录/预留/删除留锁内），等待方回调不再持锁做 socket I/O。
- **新增模块级回归**：`test concurrent duplicate side-effect requests create exactly one room`。

| 套件 | 结果 |
|---|---|
| Claude 独立 MR-03 套件 | **24/24 通过**（protocol 7 + session 8 + lobby 9，0 失败） |
| :protocol:test | 174/174（含 Anti 新增 ReliableRequestorDeterministicCacheTest 3 项） |
| :client:test | 170/170 |
| :server:test | 161/161（含 Anti 新增 Mr03ModuleCacheTest 5 项） |
| :engine:test | 470/470 |
| 独立 V4 审查（v4-review.init.gradle, PreviewOnly） | 通过 |
| 合计模块测试 | 975/975，0 失败/0 错误/0 跳过 |

## 结论

- **MR-03 验收通过**：确定性 FIFO/时效淘汰、拆池预算与副作用回执预留、跨池冲突统一检查、比较删除与副作用在途身份保留、锁外 Future 完成等契约点全部有独立测试覆盖并通过；R1/R2 缺陷已返修关闭。
- 与 MR-06 的「房间回收后的缓存失效联测」按计划留待 MR-06 交付时执行（MR-06 未开始）。
- 行为变更说明（契约 §3-8 要求单独注明）：大厅完成回执由单一池 1024 拆为副作用池 1024 + 查询池 1024；副作用池满时新请求收 SERVER_BUSY（此前为哈希序静默挤占）；超出 60s 保留期的同 ID 重试按明确契约重新执行。线路帧格式与既有 API 错误码未变。
- 未提交：工作区改动等待用户授权提交。
