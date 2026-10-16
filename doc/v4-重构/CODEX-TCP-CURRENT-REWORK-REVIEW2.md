# TCP 统一返修第二轮独立复验：编译专项通过，整体仍退回

日期：2026-10-16。Anti 使用 `agy.exe --dangerously-skip-permissions` 交付 `CODEX_CALLBACK TCP_COMPILE_FIX ready_for_review`。本轮仅验收它修复了先前的生产源码编译错误，不代表 R01–R16 或 T4/T5 通过。

## 已通过

- 强制重跑 `gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :client:test --tests com.majiang.review.TcpCurrentReviewTest --rerun-tasks --console=plain`：生产源码重新编译成功，9 项独立测试全部通过。日志 `build/tcp-current-rework-review2-forced.log`。
- `InProcessBinding` 的未知 API 已改为明确失败，载荷正常编码路径已从 API 名称改为完整请求编码。R14 仍需补编码失败路径及覆盖测试。

## 新发现：标准测试仍阻断

1. `gradlew.bat test --console=plain` 失败：Anti 新增的 `server/src/test/kotlin/com/majiang/server/tcp/TcpTableServerRegressionTest.kt` 无法编译。错误包括不存在的 `room.tableHost.timeline`、`ApiRequest(seat=...)`、`room.connectionStates`，以及 `room.snapshotAndSubscribe(token=..., cursor=..., validator=...)`。详见 `build/tcp-current-rework-all-tests.log:180-196`。必须按真实 API 修正测试，且保持 R01/R02/R15 行为断言，不得删除或弱化。
2. `MenuScreenTest` 两项真实 TCP 大厅测试持续超时：`testRealTcpLobbyConnectionCanCreateAndListRoom` 在 `getRoomInfo()` (`MenuScreenTest.kt:122`)；`testRealTcpLobbyStartsPlayerTableAndReturnsToLobby` 在同一调用 (`:144`)。隔离强制重跑仍重现，见 `build/tcp-current-presentation-isolated.log` 和测试 XML。追查服务端 ROOM_INFO 应答与客户端 Future 映射，修生产根因，不延长测试超时掩盖问题。

## 编译后静态阻断仍在

- **R04** `TcpClientBinding.kt:764-770,783-790,911-917`：请求超时/写失败/重放超时会删除副作用恢复记录；超时时还让 reconciliationFuture 失败。未知结果的原 scope/requestId/payload 因此失去对账能力。
- **R07** 同文件 `:258-263,707-712`：依旧 `java.net.Socket` + 调用线程 `OutputStream.write/flush`，不符合设计的异步、有界非阻塞 I/O 与慢写截止。
- **R09** `earlyBufferOverflow` 仅赋值/清零，不触发玩家或观战的合并全量恢复。
- **R10** `TcpTableServer.kt:312` 解码器暂存字节尚未纳入全局预算，完整帧到邮箱入队才记账。
- **R14** `InProcessBinding.kt:1468-1472` 完整编码失败时回退 `req.api`；同 API 不同内容会重新合并为同一载荷。应明确拒绝编码失败，且补对应回归用例。

下一步 Anti 任务：先修上述两类标准测试阻断并复核真实 TCP 大厅返回，再按 R04/R07/R09/R10/R14 完成实现。Codex 独立复验。不得将第二轮 9 项绿色写成整体验收通过。
