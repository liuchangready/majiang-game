# 任务 T2：进程内绑定（InProcessServer + InProcessBinding，单机无协议）

你是 Anti，负责本任务的纯代码开发。**只用文件读写工具，禁止运行任何命令、构建、测试**——由主会话执行。完成后列出你创建/修改的全部文件路径清单。

必读：
- `doc/v4-migration/TASK-ANTI-T1.md` 的交付物定义（你要实现的 `ServerConnection` 契约以 T1 实际落盘文件为准，先读 `server/src/main/kotlin/com/majiang/server/api/`）
- `doc/SERVER_CLIENT_API.md` §6（进程内绑定的全部设计约束）
- 接线样板：`presentation/src/main/kotlin/com/majiang/MahjongGame.kt` 的 `createLocalHost()`/`launchGame()`（现有单机如何持有 TableHost 并 connect(0)）
- `engine/src/main/kotlin/com/majiang/host/TableHost.kt`、`engine/src/main/kotlin/com/majiang/access/SessionAccess.kt`、`engine/src/main/kotlin/com/majiang/access/local/LocalSession.kt`

## 目标

单机模式：client 通过 `InProcessBinding` 直连同进程 server，无网络协议、无序列化、无心跳。

## 文件所有权（独占）

```
server/src/main/kotlin/com/majiang/server/inprocess/
server/src/test/kotlin/com/majiang/server/inprocess/
```

其余文件只读。T1 已落盘的 api 包也只读（若发现接口缺口，在交付说明里报告，不要改）。

## 交付物

1. `InProcessServer`：
   - `createLocalTable(config: MatchConfig, humanSeat: Int = 0): ServerConnection`——内部创建 `TableHost(config)`、`host.connect(humanSeat)` 取得 `SessionAccess`，空座按 TableHost 现有默认策略 AI 托管；持有宿主生命周期（`close()` 释放宿主）。
   - `listRooms()` 等大厅查询在单机无意义：返回单房间摘要（本地桌）或空列表，KDoc 写明。
2. `InProcessBinding(private val host: TableHost, private val access: SessionAccess) : ServerConnection`：
   - `SessionApi` 方法逐一直通 `SessionAccess` 同名方法（`snapshot/decision/submit/submitShufflePlan/start/nextRound/resume/requestControl/capabilities`），`queryRoundResult` 直通 access 的同名能力（看 `SessionAccess` 实际签名适配）。
   - `LobbyApi.createRoom/joinRoom/resumeSession/leaveRoom`：单机语义——`leaveRoom` 返回 REJECTED（reason=`LOCAL_TABLE_UNSUPPORTED`）或关闭语义由你判断后在 KDoc 写明理由；`resumeSession` 直接返回当前快照的 RoomBinding（进程内不存在断线）。
   - `PushChannel.onUpdate` 直通 `access.subscribe`；`onDisconnect` 仅在 `close()` 后触发一次（进程内无网络故障路径）。
   - `closed`/`disconnectReason`/`close()` 幂等，行为对齐 `client/src/main/kotlin/com/majiang/network/NetworkSessionAccess.kt` 的语义（读它参考，不抄网络部分）。
   - **DTO 深拷贝**：跨层返回前 `deepCopy()`，防止 engine 内部集合外泄（SERVER_CLIENT_API §6 表格"对象引用传递"行）。
3. 单元测试（你写文件，主会话跑）：
   - 端到端验收：`createLocalTable` → `snapshot()` 拿到 seat0 视图 → `start()` → `decision()` 非空 → `submit()` 一个弃牌选项 → `onUpdate` 收到游标前进的 PlayerUpdate → `close()` 后 `closed=true` 且再调用返回 REJECTED/异常语义一致。
   - 幂等：同一 `requestId` 重复 `submit` 重放回执。
   - 参考现有测试：`server/src/test/kotlin/com/majiang/v3/NetworkAcceptanceTest.kt` 的组装方式。

## 陷阱清单

1. TableHost 的自动操作由外部 tick 驱动（渲染循环调 `host.tick`，见 MahjongGame.render）。测试里没有渲染循环——查 TableHost 是否有自驱动/测试驱动入口（`server` 现有测试怎么驱动它就怎么写）；**不要在 binding 里私起线程 tick**。
2. `LocalSession` 的 Future 语义即权威循环提交凭据，不要在 binding 里同步 `.get()` 阻塞。
3. 禁止修改 api 包与 engine；禁止引入新依赖。
4. `resumeToken` 进程内用固定占位值（如 `"LOCAL"`），不参与任何校验。

## 完成标准

主会话执行 `:server:test` 与全量 `test`，并抽检单机链路。除独占路径外 `git status` 无其它改动。
