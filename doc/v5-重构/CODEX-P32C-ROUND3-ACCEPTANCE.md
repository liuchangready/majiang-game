# P3.2c 第三轮独立验收与 PR19 集成

日期：2026-10-24。原线程：`thread_20261024_031743_6c0a`，沿用账本的 caller 身份；未另建任务、会话或转派。

结论：**P3.2c Contracts/Wire 载荷组件 PASS**。交付提交为 `06f809fe74383eb331212256929ae1c1042de403`，初始基线为 `3e78367538b4f7520a1d414c82ef3155507a5206`。前两轮 FAIL 保留，不追认失败提交。caller 已调用 Bridge `approve_task`，线程返回 COMPLETED 并归档。

## 审查结论

本轮独立核对 `PrivateCheatView.window_id = 15` 的 append-only schema、Contracts 到真实 Protobuf 的映射、ID 校验，以及原 owner/public 校验的调用路径。字段独立于两个 offer 和 reservation，关闭状态为空串；未占用 reserved 7..12，未改变旧字段编号或 wire type。四席 window-only open/close/replacement、双 offer 加 reservation、schema 再生成与字段表的持久回归通过。

原 caller window-only 复现再次运行：Contracts 保留 `bloom-window-0-7-1`，Decode 为 Ok，Wire JSON 包含精确 `windowId`，open/closed 字节不同。原 8 个恶意 Wire 探针全部 REJECTED，覆盖错误 owner、公共暗杠 identity、跨座位私有事件、公共 win 可见性。持久安全测试还覆盖 raw Decode、Sync/settlement 可达图、4×4 owner 矩阵、resolved owner provenance 和 future-minor optional 兼容。探针程序的 exit 0 本身不作为安全证明，caller 已核对 8 条拒绝输出。

## 交付工作树复验

工作树 `D:/majiang-wt/p32-wire`，分支 `wt/v5-p32-wire`。SDK 全部使用 `D:/Unity/dotnet/dotnet.exe`。

| 实际命令（SDK 参数） | passed / failed / skipped | exit |
|---|---|---|
| `build dotnet/Mahjong.sln --nologo -v minimal` | 0 warning / 0 error | 0 |
| `test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj --nologo -v minimal` | 104 / 0 / 0 | 0 |
| `test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --nologo -v minimal` | 111 / 0 / 0 | 0 |
| `test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj --nologo -v minimal` | 132 / 0 / 0 | 0 |
| `test dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj --nologo -v minimal` | 80 / 0 / 0 | 0 |
| `run --project C:/Users/sncdc/AppData/Local/Temp/codex-p32c-review-20261024/Review.csproj --nologo -v minimal` | 8/8 恶意输入被拒绝 | 0 |
| `run --project C:/Users/sncdc/AppData/Local/Temp/codex-p32c-round2-window/Review.csproj -v minimal` | 窗口保留，open/closed 字节不同 | 0 |
| `git diff 3e78367..HEAD --check` | PASS | 0 |

非重复单元测试合计 **427 passed / 0 failed / 0 skipped**。日志、框架日志副本和 SHA256 清单保留在该树 `artifacts/p32c-caller-round3/`，不提交构建产物。没有为本轮重跑 Lobby/PG 或严格统一入口。

## 批次集成与发布

唯一既有 PR 为 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，源 `feat/v5-1-integration`，目标 `v5`。caller fetch 后核对已发布基线为 `8af240d1cd1c958295b2f04d724409bd6077ad75`，其中已包含此前接受的 P3.2a/P3.2b/P3.3、PG 与诊断工作。当前组件相对远端仅有 `8a6045a`、`d29d70a`、`06f809f` 三个未推送提交；没有纳入其他未验收分支或本地 artifacts。

caller 在隔离树 `D:/majiang-wt/codex-p32c-final-20261024`、分支 `wt/v5-codex-p32c-final-20261024` 合入受验交付，无冲突。受测合并提交为 `955c4199ae610b36fd80094a1352edbb5992a5a4`。

| 实际命令（同一 SDK） | 结果 | exit |
|---|---|---|
| `build dotnet/Mahjong.sln -c Release --nologo -v minimal` | 0 warning / 0 error | 0 |
| `test dotnet/Mahjong.sln -c Release --no-build --nologo -v minimal` | 563 / 0 / 0：Domain 313、Contracts 104、Client 132、AI 14 | 0 |
| `test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal` | 150 / 0 / 0：已接受流组件 143 + 载荷 mapper 7 | 0 |
| `test dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj -c Release --nologo -v minimal` | 80 / 0 / 0 | 0 |
| `git diff 8af240d..HEAD --check` | PASS | 0 |

集成非重复合计 **793 passed / 0 failed / 0 skipped**。TableRuntime/WSS 测试不在 solution 中，已分别调用。原始日志、框架结果与 SHA256 位于集成树 `artifacts/p32c-integration-review/`。最终发布提交在受测 merge 上仅追加本验收文档；由 caller 一次推送至既有 PR 源分支，更新同一个 PR，并通过 `record_merge_request` 记录最终 SHA。

本轮证明的是两个已接受组件合并后的构建与回归，尚未把 actor 流接入实际 Wire 编码器。**完整 P3.2、P3.4、G1/G5 未通过**；真实 TableService WSS/PG 联合流、Unity/IL2CPP 和设备测试未运行。当前 WSS 80 项属于组件测试，不等于完整桌服务端到端验收。V4 仍是唯一生产基线。

既有远端 CI 的 PowerShell 7 PostgreSQL wrapper 环境变量恢复失败仍未解决：历史 push run `40018364132` 和 PR run `40018373581` 均 exit 1；更早的 `39970580589` Client 失败根因仍未解决。本批次没有修改 wrapper、放宽 verifier 或通过 skip 计为 PASS；本地 793/0/0 不替代新发布 SHA 的远端 CI 结果。
