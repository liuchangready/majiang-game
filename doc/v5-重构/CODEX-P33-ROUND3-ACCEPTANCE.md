# P3.3 第三轮独立验收

日期：2026-10-24。原线程：`thread_20261024_012512_72d3`。
开发交付：`5f4f8295297d60011f07115fe9b984a1ef80c4ba`，基线 `99612cc`。

结论：P3.3 组件验收通过。批量 raw AI 在恢复后拒绝；单项和批量 actor observation 均核对本 actor 当前恢复代次。ConditionalWeakTable 不保活历史 observation。恢复账本、base-first、grace 本地重建、逐项 tick deadline 复核及提交后安装保持有效。原 25 项 TableActorTests 未改。

Codex 在 `D:/majiang-wt/p33-control` 使用 `D:/Unity/dotnet/dotnet.exe` 独立执行：

| 命令 | 结果 | 退出码 |
|---|---|---|
| `test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal`（交付原样） | 72 passed / 0 failed / 0 skipped | 0 |
| `test dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 541 passed / 0 failed / 0 skipped；Domain 313、AI 14、Contracts 82、Client 132 | 0 |
| 同一 TableRuntime 命令（增加 caller 3 项探针后） | 75 passed / 0 failed / 0 skipped | 0 |
| `git diff --check 99612cc..HEAD` | PASS | 0 |

非重复合计：616 passed / 0 failed / 0 skipped。框架原始日志位于各测试项目 `bin/Release/net8.0/TestResults`；开发者交付日志与 SHA256 清单仍见 P3.3-TIMING-CONTROL-HANDOFF.md。

新增 `CodexP33Round3ReviewProbes` 验证：恢复代次 1 捕获的推理，在代次 2 恢复后才转换为 raw submission 或 AiDecision batch 都不能提交；拒绝后新捕获结果可提交。另验证身份字段和 generation 相同的另一 actor observation 不能授权本 actor，当前 actor 新观察仍可正常单项提交。

此验收限定 P3.3；不宣称 P3.4 持久化/lease/冷恢复、P3.2b 流订阅、G1/G5、Unity/设备/真实网络通过。本树未重跑 Lobby/PG/WSS，开发者记录的本树未配置 PG 缺口不由本组件验收抹除。最新集成分支已有独立 PG 组件验收，集成后的测试结果另行记录。V4 仍为生产基线。

由 caller 合入既有 PR #19（`feat/v5-1-integration` → `v5`），不另建子任务 PR。

## 合并后统一验收

在独占集成树 `D:/majiang-wt/codex-p33-final-20261024`，以远端 `d5e89fa` 为基线合入 caller 提交 `8825384`，无冲突。实际受测合并提交：`9ff6be1806e9cbeeec7f8c10847ff76b76b1da0d`。保留已验收的 P3.2a、PG 和诊断组件；不合入其他在途任务。

新建自有 PostgreSQL 16.10 临时实例（端口 55464、数据库 `mahjong_lobby_test_codex_p33_final`、根目录 `D:/majiang-pg-tmp/codex-p33-final-batch-20261024`），通过既有脚本验证所有权和 data_directory，设置 PG 环境后原样运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 -Configuration Release -RunnerMode dotnetTest -DotnetPath D:/Unity/dotnet/dotnet.exe -LogDir D:/majiang-wt/codex-p33-final-20261024/artifacts/p33-integration-review/release
```

结果：**OVERALL PASS，退出 0；814 passed / 0 failed / 0 skipped**。Client 132、Domain 313、AI 14、LobbyService 89、Contracts 82、TableRuntime 104、WSS 80。TableRuntime 包含原 25、P3.2a 29、P3.3 47 和 caller 新增 3 项。9 个项目行均 PASS；两个 console helper 只验证构建，不贡献测试数。Release solution build 为 0 warnings / 0 errors；scratch 目录 12-DLL 闭包检查通过。finally 清理已核对所有权的自有 PG，wrapper 实际退出 0。

本地证据：`artifacts/p33-integration-review/` 下的 wrapper、unified-console.log、release/summary.log、原始框架 TestResults、PG evidence 与 evidence-sha256.csv。未提交二进制、临时数据库或 artifacts。本轮没有重跑 PG 重启/隔离专项及诊断专项，沿用各组件先前独立验收；不把普通 Lobby 测试等同于这些专项。远端 CI 结果仍需实际运行证据。
