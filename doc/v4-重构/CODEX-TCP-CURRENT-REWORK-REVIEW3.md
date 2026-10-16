# TCP 统一返修第三轮独立复验：标准测试执行后仍失败

日期：2026-10-16。Anti 再次使用 `agy.exe --dangerously-skip-permissions` 交付 `CODEX_CALLBACK TCP_TEST_COMPILE_FIX ready_for_review`。本轮 `TcpTableServerRegressionTest` 已编译，但实际执行仍失败。完整日志：`build/tcp-current-server-regression-review.log`、`build/tcp-current-rework-all-tests2.log`。

## 本轮状态

- Codex 强制执行 `:server:test --tests com.majiang.server.tcp.TcpTableServerRegressionTest --rerun-tasks`：5 项中 R01/R02/R15 通过，R06/R11 失败，均在 socket 读取时遭遇 `Unexpected EOF`。R06 是未知 lobby token 应答；R11 是停服 `SERVER_SHUTDOWN` 关闭帧。须判断并修复服务端关闭帧冲刷、客户端 helper 的帧读取问题，保持真实协议断言。
- Codex 执行标准 `gradlew.bat test --console=plain`：编译已通过，但 `client:test` 144 项中 2 失败，`presentation:test` 286 项中 2 失败，`server:test` 166 项中 7 失败。其余模块通过。不得宣称全量测试通过。

## 标准测试失败清单

| 模块 | 失败测试 | 现象 |
|---|---|---|
| client | `ServerConnectionSessionReconnectClassificationTest.testUnknownBusinessErrorRetriesWithBoundedExponentialBackoffUpToCap` | 断言失败（`:482`） |
| client | `TcpClientBindingRegressionTest.R07 user callback exception does not cause protocol violation or close binding` | 断言失败（`:183`） |
| presentation | `MenuScreenTest.testRealTcpLobbyConnectionCanCreateAndListRoom` | `getRoomInfo()` 超时（`:122`） |
| presentation | `MenuScreenTest.testRealTcpLobbyStartsPlayerTableAndReturnsToLobby` | `getRoomInfo()` 超时（`:144`） |
| server | `TcpTableServerRegressionTest.R06 unknown lobby token is rejected and reconnect retains valid scope` | 意外 EOF（`:38`） |
| server | `TcpTableServerRegressionTest.R11 server close executes within 1 second and sends shutdown notification` | 意外 EOF（`:38`） |
| server | `TcpTableServerCapacityAndLifecycleTest.UC-C04 server shutdown fails pending requests and sends SERVER_SHUTDOWN` | 意外 EOF（`:224`） |
| server | `TcpTableServerRoutingAndSubscriptionTest.D9 GET_REQUEST_SCOPE query returns valid grantedScope` | 断言失败（`:70`） |
| server | `TileMarkClientTcpIntegrationTest.real client facade casts over TCP receives private push and restores exact river position` | 请求超时 |
| server | `BeamSwapClientTcpIntegrationTest.real TCP activation private push binding replacement and eventless choice complete end to end` | 请求超时 |
| server | `SleeveBloomClientTcpIntegrationTest.real TCP activation private push binding replacement and eventless choice complete end to end` | 请求超时 |

## 仍未关闭的原统一问题

`CODEX-TCP-CURRENT-REWORK-REVIEW2.md` 所列 R04、R07、R09、R10、R14 静态阻断仍未修改。原 R01–R16 不能整体关闭。请按上述失败簇逐个修复，保留标准测试与 Codex 独立测试的实质断言；每一批交付后由 Codex 独立复验。真机/模拟器、跨机慢端与 T4/T5 发布验收仍待完成。

## 本次 Anti 下发与回调记录

使用 `agy.exe --mode accept-edits --dangerously-skip-permissions` 将真实 TCP 房间流程超时批次下发给 Anti。其会话 `f895d744-22dd-4f5c-b6e3-d816f46e5517` 连续约 180 步读取与搜索，未写入生产修复，也未返回 `CODEX_CALLBACK TCP_ROOM_FLOW_FIX`；Codex 中止该空转会话。该批次状态为**已下发、未交付、未验收**，不计入已修复。
