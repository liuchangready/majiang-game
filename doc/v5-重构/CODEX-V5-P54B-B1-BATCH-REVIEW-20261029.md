# P5.4b-B1 批次复核（2026-10-29）

B1 测试设施在冻结输入上的交付已独立验收；这不代表真实客户端对账或恢复闭环通过。第 5 轮诊断值白名单审查及 Release 单测 44/44 通过，0 失败、0 跳过；`git diff --check e0d9ff3..54821b9` 通过。

将 B1 交付与既有 PR20 头 `2c9278c8775d359a582e5a9878604101cb84c44d` 合并后，源码头 `792ce41cb949e552929ad837a4c1630258157c36` 上复跑 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-https-admission.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -PgPort 58871 -LobbyPort 58872 -TablePort 58873`：

- 隔离 PostgreSQL 16.10、角色隔离、fresh Release build、44/44 测试、Lobby HTTPS 与 Table WSS 服务就绪均 PASS。
- suite native exit 1，wrapper exit 1 / FAIL；`suite.log` 仅保留 `InvalidOperationException: [redacted]`，现有证据不足以归因。不得把此结果解释成原冻结输入的 exit 2 或端到端通过。
- 所有权核验清理 PASS；manifest 212 项，SHA256 `D14DDA17C1311064938F3BDD8FA8B64ACE2B8F33DCBE323F11A5265E6B679302`。原始证据位于本工作区 ignored `artifacts/p54b-b1-logs-20261029-101711-5bccd1b3f80a42518ccb0d2cfe74e072/`。

PR 保持 Draft，批次真实链路失败待定位；未知结果恢复、canonical digest、跨实例票据消费、完整整场、G2–G5 和设备跨物理网络均不能由本次验收推断通过。
