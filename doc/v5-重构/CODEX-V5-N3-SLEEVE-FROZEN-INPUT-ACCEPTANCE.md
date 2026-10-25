# F-01 袖里藏花冻结墙牌输入：调用者独立验收

日期：2026-10-25。Bridge `thread_20261025_141920_f894`，沿用原 callerSessionId；已调用 `approve_task`，返回 COMPLETED。交付 `19d44d5c00b51a881fef8b781d6aeb09317e8af6`，冻结基线 `d6bc69fa930d005b355a1273ec732fbdf82bbe2f`。

F-01 有界通过：`sb-activate-wall-empty-drift` 通过 typed 冻结输入 `32/false` 的真实 Domain 调用返回 Rejected / NoWallCandidate（V4 `SLEEVE_BLOOM_WALL_EMPTY`），不再使用反射能力门。相同规范 map 上，旧 map-only 调用和合法冻结候选 `40/true` 均 Allowed。无候选、离墙、声明与实体不符、预留、dead-wall、非首个候选均拒绝，不自动换牌；墙候选检查先于扣费。权威桥从只读状态构造输入，无 wire/client 候选字段。136 张守恒、回执重放及 commit 失败不发布均有真实测试。

## 范围与审查

交付仅八个文件；原任务允许复用 N1b actor 测试，实际新增在既有 `N1bCheatExecutionTests.cs`，没有创建重复 actor 测试文件。源码、消费者、driver 与报告逐项复核；V4、fixture/catalog/manifest、项目文件未修改。工作树干净，`git diff --check d6bc69f..HEAD` exit0。

已覆盖范围/假绿、功能/守恒、actor 时序与重放、提交失败、输入权威性与隐私、证据/平台口径和文档一致性，未发现需要返修的功能缺陷。Domain/map 为不可变状态，拒绝发生在 Charge 之前。未新增客户端可指定的冻结输入或公开投影。交付证据清单 202/202 SHA-256 一致，历史失败日志保留。

## 独立验证

交付工作树 `D:/majiang-wt/sleeve-frozen-input`，使用 `D:/Unity/dotnet/dotnet.exe`：

| 检查 | 实际结果 | native exit |
|---|---|---:|
| solution Release 非增量 build | 0 warning / 0 error | 0 |
| solution tests | AI438 + Contracts149 + Client132 + Domain483 = 1202 passed / 0 failed / 0 skipped | 0 |
| TableRuntime.Tests Release 非增量 build | 0 warning / 0 error | 0 |
| TableRuntime DLL 全量 | Total365 / Errors0 / Failed0 / Skipped0 / NotRun0 | 0 |
| cheat probe 正控制 | Total95 / Errors0 / Failed0 / Skipped0 / NotRun0 | 0 |
| 外部单字段变异 | 46/46 FAIL_AS_EXPECTED；均真实 failed test，0 unexpected/tool/wrong failure | 各 1 |
| F-01 同输入 conformance | Total1 / Errors0 / Failed0 / Skipped0 / NotRun0 | 0 |
| 默认 probe 总结果 | MATRIX=PASS / READINESS=BLOCKED / MATRIX_PASS_READINESS_BLOCKED | 1 |

标准测试总计 **1567 passed / 0 failed / 0 skipped**。probe exit1 来自保留的七个独立 BLOCKED，不算标准测试失败，也不算整体 readiness 通过。75 handler 分母为 **68 bound / 7 BLOCKED**。

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noLogo
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1 -Repo D:/majiang-wt/sleeve-frozen-input -EvidenceRoot <new-exclusive-dir>
```

## 隔离集成与统一 MR

调用者从最新远端 `origin/feat/v5-1-integration=d6bc69f` 创建 `D:/majiang-wt/codex-sleeve-f01-integration-20261025`，合入交付形成 `a0c17fecc305b4f45c4c97aeee7d017f8a7c7a14`。`git diff --exit-code 19d44d5 HEAD` exit0，整棵树与独立验收一致。隔离工作树重新非增量构建 solution 和 Runtime，均 0 warning/error；重新执行标准全量仍为 **1567/0/0**，命令均 exit0。未重复已验证且源码完全相同的 46 项矩阵。

本记录仅新增调用者验收文档。该任务、合入和验收记录统一正常 fast-forward push 到既有 [PR19](https://github.com/liuchangready/majiang-game/pull/19) 的 `feat/v5-1-integration -> v5`。同批其它任务尚无新交付提交，未将未验收工作混入；不新建子任务 MR，不修改主 checkout 用户文件，不合并 PR。最终发布 SHA 由 Bridge `record_merge_request` 记录。

## 原始证据

证据根 `E=D:/majiang-wt/sleeve-frozen-input/artifacts/codex-f01-review-722f672b08b046269dc12235ddc93223`。原始日志、probe JSON 副本/逐变异日志/CSV/readiness 和交付哈希复核 CSV 均保留；构建和测试可用上列命令重新生成。

| E 下文件 | SHA-256 |
|---|---|
| solution-build.log | 9257F9532BC24345DCBE9FCD1A6FB43461BE6FDCB4D11F11E0800FB3D038B1CC |
| solution-tests.log | 53B5137876A1AB59DD0233B413A63E5311323B8689CD82D9D5BA89EB86D969E7 |
| runtime-build.log | C75523469712D5281EB0F6E3BA76906F0A82C0C5E12A45B0D85BE3905922B55A |
| runtime-tests.log | 5C6D2C1EE912F82C58C5D2AF82FE8695897899315634DF83D5F25F37A02DDC3B |
| delivery-evidence-audit.csv | AE520446D1E4DD5A256C5629BD7F5698338B9F71FC4706FDB969EECCDBD5EE02 |
| probe/summary.json | 857B1E42A4924822A8C8B409A6CC09FEA39DE8260BE815126FD16F7DD19D0041 |
| probe/matrix/matrix.csv | 20F8E03F8D574755FB9C2CCD2B0B6B740E27D09927A50C53CF87C25F3AF0E00B |
| probe/readiness/readiness.log | E158D767554F90BFCABA6D3E1C2C3E5DAFA6D4623F6BD5C2682A8F5771880DA8 |
| integration-build.log | A474F50D2F72995F7317CDF252FB586F31D49A520FA288D5C3F4E7093DD62749 |
| integration-tests.log | 082D97F9369CC1EEED5A719B79BAD509B9E9350A38071F01FC96AF8DFCA492E9 |
| integration-runtime-build.log | 88FFA4B64C09A7B01961809205FC092D9D03213DDF12C76154F97A808B6A27B4 |
| integration-runtime-tests.log | FB95B0F196DD82C5A4E6DD9B5EACAA3A410BE21E7CA72394533C27E62DBD7129 |

## 保留边界

七项仍 BLOCKED：`tm-wrong-phase-round-end`、`tm-capacity-tiles`、`tm-no-active-decision`、`tm-deadline-expired`、`tm-decision-mismatch`、`bm-activate-deadwall-empty`、`bm-activate-deadwall-dora-only`。F-02、F-06、随机输入未纳入本任务。此记录更新 F-01 当前状态，不改写此前有界验收中的历史失败事实。

**N3/G1 尚未通过，G5 未完成，V4 仍唯一生产。** PG/WSS、Unity/IL2CPP、Android/设备、真实网络、V4 Gradle 全量和本次发布远端 CI 为 NOT_RUN；旧 Client `39970580589` 根因仍 UNRESOLVED。组件验收不能代替这些门槛。
