# D-03 / D-08 主工作区整合复核

日期：2026-10-19。基线：`cca271a`，保留 D-01、D-02、D-03 及历史未跟踪文档和任务脚本。整合代码提交 `e500ce1` 已推送至 `origin/feat/mj-tcp-migration`。

## 结论

D-03 在 `D:\majiang` 独立验收通过。D-08 在隔离工作树 `D:\majiang-d08-20261019` 独立验收后，已整合至主工作区，并完成合并后的回归。D-08 的隔离工作树验收本身不替代本次合并验证。

## 整合点

- 对 `TcpConnection.kt` 和 `TcpTableServer.kt` 以 D-02 共同基线做三方合并，保留 D-03 的关闭帧/半关闭竞态处理以及 D-08 的路由委托。
- 将 D-03 的房间只读成员代次与令牌访问、玩家和观战者顶号订阅、重复离房处理移入 D-08 的 `TcpRequestRouter`。
- 将 D-08 的 `LogicalIdentityContext`、`TcpReceiptManager`、`TcpRequestRouter` 与回执存储修改带入主工作区；补充 `TcpReceiptManager.currentScopeForPrincipal`，确保离房成功后颁发大厅作用域。
- 保留 D-03 对 `SharedRoom`、`InProcessBinding` 与双传输参数化测试的改动。未恢复 UDP 或旧 TCP/V3。

## 主工作区验证

- `:server:test --tests com.majiang.server.binding.DualTransportBindingScenarioParameterizedTest --tests com.majiang.server.tcp.TcpRequestRouterAndReceiptTest --rerun-tasks`：通过。
- `:server:test :client:test :protocol:test --rerun-tasks`：server **330/330**、client **217/217**、protocol **174/174**，共 **721/721**，失败、错误、跳过均为 0；日志：`build/d03-d08-integrated-tests.log`。
- `-I tools/architecture/tcp-current-review.init.gradle :client:test --tests com.majiang.review.TcpCurrentReviewTest --rerun-tasks`：通过；日志：`build/d03-d08-integrated-probes.log`。
- `git diff --check`：通过。

TCP 迁移设计 T4 的跨物理网络、弱网与 Android 真机/模拟器实测仍待补验。D-04～D-07 与 D-09 尚未实施。
