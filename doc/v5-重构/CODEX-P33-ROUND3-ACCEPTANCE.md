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
