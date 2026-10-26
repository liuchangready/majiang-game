# N3 cheat-v1 测试消费者独立验收（有边界）

日期：2026-10-25。Bridge：`thread_20261025_104846_b58a`，沿用原 caller/receiver 会话。第 4 轮交付 `1da4835f591de2b08ad4a2e7f2f0f47ddcd145f1`，包含 `a24b491`、`8280579`、`cc20c66`；冻结基线 `900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。

## 验收结论

调用者已通过 `approve_task` 将该任务归档。本次批准测试消费者的可表示部分与阻塞诊断：67/75 handler 真实行为绑定、6/6 movement、ledger/TileMarkState 深字段。8 个 handler 仍 BLOCKED，完整 N3/G1 未通过，G5 未完成，V4 仍是唯一生产架构。

第 4 轮仅改报告。D-R3-01 的 nextWallTileId 截断、证据表断行/路径前缀及 enum 计数均修复；12 条证据路径及 SHA-256 全部匹配，enum CSV 的逐例失败数为 `1,1,2,2,2,2,1`。报告 SHA-256 为 `1E3496110A93101F15C7FCEC0088D2D33397B878B805CE5B0BB01F177884517F`。无新增已知缺陷，不再要求重复返修。

累计差异仅原 expectedFiles 四文件；交付工作树 clean，累计 `git diff --check 900a043..HEAD` exit0。无生产、fixture/exporter、catalog、统一 verifier、项目或 solution 变更。拒绝/stale/expired 的后态和事件校验、136 张守恒、严格 enum token、工具失败传播已核查；该测试变更不增加生产并发、恢复或公开流安全面。

## 新鲜独立执行结果

交付工作树 `D:/majiang-wt/n3-cheat-binding`：

| 检查 | 结果 | native exit |
|---|---|---:|
| Release solution 非增量 build | 0 warning / 0 error | 0 |
| solution tests | AI435 + Contracts149 + Client132 + Domain463 = **1179 passed / 0 failed / 0 skipped** | 0 |
| CheatGolden 正控制 | Total94 / Errors0 / Failed0 / Skipped0 / NotRun0 | 0 |
| 外部副本单字段变异 | **46/46 FAIL_AS_EXPECTED**，各例 Total94、Failed>0；0 unexpected/tool/wrong failure | 各例 1 |
| readiness 能力门 | **BLOCKED_INPUT**，Total1 / Failed1 / Errors0 / Skipped0 / NotRun0 | 1 |
| 默认探针整体 | MATRIX=PASS，READINESS=BLOCKED，MATRIX_PASS_READINESS_BLOCKED | 1 |

readiness 的非零是已要求保留的缺输入诊断，不是产品行为违约；map-only 合法墙候选 Allowed 控制通过。未将该失败算作标准测试通过，也未用 MatrixOnly 隐藏它。本轮未重跑此前的 296 叶扫描、独立 stale/expired 恶意后态审计、MatrixOnly 或工具失败注入；其历史证据已保留，12 条文档证据哈希已重新核对。

## PR19 隔离集成

从已发布 `origin/feat/v5-1-integration` 的 `85f94ca623cee950899ff76139a9b18dd3cc6457` 新建调用者工作树 `D:/majiang-wt/codex-cheat-r4-integration-20261025`，合入交付形成 `db684dca660a658342fae0a619555eb802160ed1`。原四轮提交完整保留，无冲突；三个消费者源码/脚本与交付逐字相同。

集成候选新鲜 Release 非增量 solution 和 TableRuntime.Tests 构建均 0 warning/error、exit0。solution：Domain470 + AI438 + Contracts149 + Client132 = **1189/0/0**；单独构建并执行 TableRuntime **362/0/0/0/0**，总计 **1551 passed / 0 failed / 0 skipped**，测试命令均 exit0。集成副本矩阵再次 46/46 符合预期，正控制 94，readiness 仍 BLOCKED_INPUT/native1，工具失败 0。

本验收记录是调用者附加文档；统一正常 fast-forward push 到既有 PR19 的源分支 `feat/v5-1-integration`，目标 `v5`。只推送本任务四轮改动、隔离合入和验收记录；未接受的 field-audit 工作不在本次发布范围。没有新建子任务 MR，不合并 PR，不修改主 checkout 用户文件。

## 可复制命令与证据

在相应工作树根运行；集成时将 `-Repo` 改为上述集成路径，每次使用不存在的新独占证据目录：

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1 -Repo D:/majiang-wt/n3-cheat-binding -EvidenceRoot <new-exclusive-directory> -SkipBuild
# 集成候选另行构建/执行，不借用旧 Runtime DLL：
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noLogo
```

新证据根 `E=C:/Users/sncdc/AppData/Local/Temp/codex-n3-cheat-r4-e1e60990d6e74c01a4d89d76dea4762c`。`probe/` 为交付矩阵，`integration-probe/` 为集成矩阵，均包含原始副本、逐变异 JSON、原始日志、CSV、summary 与独立清单。`report-evidence-audit.csv` 保存 12 条路径/哈希比较，`report-enum-audit.json` 保存逐例计数原数据。

| E 下文件 | SHA-256 |
|---|---|
| solution-build.log | 2E81652F50C28E964F97D825A80D9161AFC407A9A5DCDBD495528C65E85E8435 |
| solution-tests.log | 751FDC7AD7659265B205F79A8CE49557E201394DA02C46E68C1A07F11F8A5194 |
| integration-build.log | B21D32139C83D58915812F85CDD102AA6331178758483F9EFCE8E2D1EFB823F9 |
| integration-tests.log | 8E072FD627DCBB2674EB6DC4B5F59AF0C07895C82B61B51B8030C3102A50E433 |
| runtime-build.log | CE8BFFA5D41EDA640BCCF9465F10B920410A73A89E70FB7F60844B220A0A4758 |
| runtime-tests.log | FFB0CB1C4DEC0005BD722B014BFB50E75D7B26F5F3E3DF4D11755CED6D70E0F0 |
| report-evidence-audit.csv | 6D7690A9F019C7B8545F0249B464A8B5DAB191589016B597C2BC387E1D750E29 |
| probe/summary.json | 011CEAB7B5CB8B6FE023CDA1316AE1458130024FDDC283B8D6CB14DD7CC0F9AC |
| probe/matrix/matrix.csv | 7704D9BFBA944A2E2AD3BFEFC97EFD48AFA1E3D43A6DEEFF08A7AD4390796DDC |
| integration-probe/summary.json | 04B9800D6CA48B1808987283A32DA9C447040666CE77EAAF22811A4A91BFFD3A |
| integration-probe/matrix/matrix.csv | 44EB72FFF70D602B73430ABD8C8F15A04A0FE1B5B3592E284F5277B64AFBE44D |
| caller-evidence-sha256.csv（386 文件） | D03750EBBA5475DABF554CDE41032B30A192163B9FF1F8DE6329EF3466BC21DB |

## 继续保留的边界

- F-01：缺 frozen nextWallTileId/nextWallTileInWall 输入；不能要求 map-only 调用拒绝其它合法候选。
- F-02：两个 beam 过滤态无法用规范 14 张王牌区表示。
- F-03：四个决策/截止时间案例属于 actor；独立 table-admission 组件已有有边界验收，不能把它补算为本 Domain 消费者已绑定。
- F-06：两组各六张标记输入不能由当前四张组构造。
- F-04/F-05/F-07 的源 note、stale index 映射与两项合法等价仍见交付报告，未更改原 fixture。

PG/WSS、Unity/IL2CPP、Gradle、设备/物理网络和本次发布远端 CI 均 NOT_RUN；不据此声称平台或 G1/G5 验收通过。
