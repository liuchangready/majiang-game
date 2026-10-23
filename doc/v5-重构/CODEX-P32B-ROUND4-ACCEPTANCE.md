# P3.2b 第四轮独立验收

日期：2026-10-24。原线程：`thread_20261024_031743_9246`；沿用原 caller/receiver 会话。

结论：**P3.2b 内存 actor 流组件 PASS**。受测交付为 `177e5aaeddcf27fc146b0020c937ccfc1bc1517d`，固定基线为 `3e78367538b4f7520a1d414c82ef3155507a5206`。前三轮 FAIL 记录保留；本结论不追认历史失败提交。

caller 在交付工作树 `D:/majiang-wt/p32-streams` 独立检查了同一 mailbox 内的基线/注册、句柄来源与当前注册校验、独立游标、提交后投影、恢复 fence/purge 与旧 completion 隔离、下游异步回调，以及本轮决策刷新和故障流容量回收。OpenDecision 成功后刷新完整快照并推进 projectionSeq，不改变 stateVersion/controlVersion；旧 cursor 的 Sync 重建基线。订阅前清理 closed registration，保留 binding/sync high-water 并拒绝旧 handle/精确代次重放。

全部命令使用 `D:/Unity/dotnet/dotnet.exe`，实际运行结果：

| 命令（dotnet 参数） | passed / failed / skipped | exit |
|---|---|---|
| `test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal` | 143 / 0 / 0 | 0 |
| `test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal` | 82 / 0 / 0 | 0 |
| `test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal` | 313 / 0 / 0 | 0 |
| `build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 12 项目，0 warning / 0 error | 0 |
| `git diff --check 3e78367..HEAD` | PASS | 0 |

非重复合计 **538 passed / 0 failed / 0 skipped**。143 项 TableRuntime 包含原有 104 项、29 项流用例及前 3 轮 caller 的 10 项探针；本轮没有修改这些测试。日志为根目录 `artifacts-p32b-caller-round4-{runtime,contracts,domain,build}.log`；独立副本、框架原始日志与 SHA256 清单保存在 `artifacts/p32b-caller-round4/`，不提交构建产物或日志。

验收限于本任务的内存流组件。真实 Protobuf/Wire 编码器、P3.2c 联合接线、真实 WSS/PG 集成、P3.4 持久恢复、Unity/IL2CPP/设备和 G1/G5 本轮均未验证，不计 PASS。生产仍为 V4。现有 PR #19 的 PowerShell 7 PostgreSQL wrapper 环境恢复失败仍是独立的未解决远端 CI 阻断，本组件测试通过不消除此失败。

由 caller 汇总至既有唯一 PR #19（`feat/v5-1-integration` → `v5`）；未交付或未接受的兄弟实现不得因本次验收顺带纳入。合并后的构建/回归与发布 SHA 另行记录。
