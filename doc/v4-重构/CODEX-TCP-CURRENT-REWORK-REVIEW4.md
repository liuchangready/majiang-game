# TCP 返修第四轮独立复验：房间流程改善，整体仍未通过

日期：2026-10-16。Codex 已使用 `agy.exe --mode accept-edits --dangerously-skip-permissions` 再次下发 `ANTI-TCP-CURRENT-REMAINING-TASK.txt`。Anti 已在 `ANTI-PROGRESS.md` 写入 `TCP_REMAINING_ROOM_FLOW_IN_PROGRESS`，确认实际接单；会话 `bfe51505-1a19-47ee-a8fa-6307692e68a6` 修改了客户端绑定、服务端连接/路由及 SharedRoom。会话未返回 `CODEX_CALLBACK`，在持续扩大到其他回归排查后被 Codex 中止。本报告只评价已写入的代码，不视为 Anti 正式交付。

独立执行 `gradlew.bat test --console=plain`，日志 `build/tcp-current-after-anti-room-flow.log`：前轮 5 项真实 TCP 超时测试（两项 MenuScreenTest 与 TileMark/BeamSwap/SleeveBloom 三项服务端集成）全部通过。全量失败由 11 项降至 **6 项**：客户端 `TcpClientBindingRegressionTest.R07`、`ServerConnectionSessionReconnectClassificationTest.testUnknownBusinessErrorRetriesWithBoundedExponentialBackoffUpToCap`；服务端 `TcpTableServerRoutingAndSubscriptionTest.D9`、`TcpTableServerCapacityAndLifecycleTest.UC-C04`、`TcpTableServerRegressionTest.R06/R11`。表现层测试本轮通过。再次强制执行 9 项 Codex 独立测试全部通过，日志 `build/tcp-current-after-anti-room-flow-independent.log`。

R04/R07/R09/R10/R14 静态阻断仍可见：副作用超时丢恢复身份、阻塞 Socket/写入、早期更新溢出无恢复、解码暂存未计全局预算，以及 InProcess 编码失败回退 API 名称。详见第二轮报告。结论仍为 **REJECTED / NEEDS_FIX**；T4/T5 未验收。下一批先处理上述 6 项标准测试失败，再关闭静态阻断。保留全部有效断言，不用提高超时时限掩盖问题。
