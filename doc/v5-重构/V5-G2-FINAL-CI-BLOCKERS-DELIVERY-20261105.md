# G2 最终 CI 两组阻断返修交付（2026-11-05）

## 状态

`LOCAL_REPAIR_COMPLETE / PENDING_INDEPENDENT_ACCEPTANCE_AND_FINAL_REMOTE_CI`

本交付只确认本树两项已知 MAJOR 的修复及本地真实验证。旧 head 的 push/PR workflow 失败记录保留；本任务没有 push、改 PR、merge 或部署，也没有独立 reviewer 的接受记录。因此不声明“最终 CI 阻断关闭”。

## 缺陷与修复映射

| ID / 级别 | 位置与根因 | 修复 | 关键断言 |
|---|---|---|---|
| `G2-VALIDATION-01-PATH-CLEANUP-01` / MAJOR | `dotnet/scripts/pg-lobby-verify/cleanup-postgres.ps1` 原第 86 行；ownership、marker、listener 与进程检查通过后，递归 `Remove-Item` 遇到 Windows 长路径失败并留下 root。 | 仅将已解析且已通过既有所有权/边界校验的 root 转为 Win32 extended-length drive/UNC 路径，以 `[System.IO.Directory]::Delete(path, true)` 删除；WhatIf、marker/data_directory/root/port/db 身份检查、listener 与活动进程拒绝路径不变。 | 283 字符 owned filesystem fixture 的 WhatIf 不删除、正式 cleanup exit 0 且 root 消失；真实 pinned PG wrapper 自测 S1/S2/S3 通过；S5 lock holder 时 cleanup 失败并保留 root，释放后 cleanup 成功；foreign root/listener 等拒绝断言通过。 |
| `G2-FINAL-CI-TABLE-EVIDENCE-01` / MAJOR | `tools/ci/test-v5-table-evidence.ps1` 抽取生产 seal AST 时遗漏 seal 调用的 `Test-ManifestPathIncluded` helper，导致真实 CI 命令在合法 seal 场景报函数未识别。 | 将此生产 helper 纳入自测已有的 AST 函数抽取清单；不复制 seal 算法、不改变 XML/hash/manifest 断言。 | workflow 相同入口 native exit 0，82/82；合法 manifest 4 行、排除 self、重新 hash 全匹配；预期封存失败仍 exit 1，且不报 PASS。 |

同类依赖检查覆盖 `VerifyV5TablePostgresStartup.Tests.ps1` 与相关 AST 提取调用方；该测试已显式提取 `Test-ManifestPathIncluded`，没有发现同一遗漏。无新增缺陷，无越界文件修改。

## 本地验证

固定生产 ZIP 从官方 URL 下载至本次独占 TEMP，SHA-256 与生产脚本固定值一致：`EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`。测试与完整验证使用 Windows PowerShell 5.1、.NET SDK 8.0.426；未修改机器级配置。

| 检查 | 实际命令/输入 | 结果 | 原始证据 |
|---|---|---|---|
| 长路径 fixture | 自有 D 盘 TEMP；真实长路径清理脚本，最深文件名 283 字符 | WhatIf exit 0 且 root 保留；正式 cleanup exit 0 且 root 消失 | `D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/long-cleanup-{whatif,real}.log` |
| table evidence | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-table-evidence.ps1` | native 0；82/82 | `artifacts/table-evidence-selftest-5b589b5522dd426bbefa7e5687d9ab56/results.json` |
| PostgreSQL wrapper | `tools/tests/VerifyV5PostgresCi.Tests.ps1 -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -ZipPath D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/postgresql-16.10-1-windows-x64-binaries.zip -BasePort 58000` | native 0；193 checks / 0 failures；真实 S1/S3 root 删除；S5 故障与释放后清理断言通过 | `D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/VerifyV5PostgresCi-20261105-151747-551135cd` |
| Table PostgreSQL startup | `tools/tests/VerifyV5TablePostgresStartup.Tests.ps1 -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -ZipPath D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/postgresql-16.10-1-windows-x64-binaries.zip -BasePort 58100` | native 0；153/153；0 skip/not-run；真实 crash recovery、native failure 与清理通过 | `build/ci-pg-startup-selftest/20261105-072227-a7eaa311` |
| 既有 cheap gates | `test-v5-native-exit.ps1`, `test-v5-direct-results.ps1`, `test-v5-dotnettest-results.ps1`，各自以 Windows PowerShell 5.1 / `-ExecutionPolicy Bypass` 执行 | native 0；分别 24/24、19/19、61/61 | `build/fedfb0c5ff3548568696d536235c4439/cheap-gates.json` 与独占 TEMP 原始日志 |
| 完整生产组合 | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunUnifiedVerification -LogDir artifacts/g2-final-ci-blockers-fedfb0c5ff3548568696d536235c4439-table -UnifiedLogDir artifacts/g2-final-ci-blockers-fedfb0c5ff3548568696d536235c4439-unified` | native 0；table 42/42，定向 13/13；unified 21/21 行 PASS、14 测试工程 2872/2872、0 fail/error/skip/not-run；PG 16.10 crash recovery 与 pg_dump/restore 通过 | table `wrapper-summary.json` / `manifest.sha256.json` / `cleanup-evidence.json`；unified `summary.log` / `run-info.txt` / per-project XML 与 metadata；console `D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/full-verification-console.log` |

完整组合结束时自有 PostgreSQL 进程数 0、所用测试端口无监听；cleanup 证据确认 `rootDeleted=true`、`rootExistsAfter=false`、`postStopProcessCount=0`。构建验证在提交 `75d2782ae0ce7c1458cfd906252c29d24caa144c` 上执行；delivery 与 binding 是随后添加的文档，不改变被验证代码。

## 最终闭环边界

- 本地候选提交：`75d2782ae0ce7c1458cfd906252c29d24caa144c`；本次代码改动文件为 cleanup 脚本与 table evidence 自测。任务卡及本交付文档另有记录。
- 旧远端失败 run：push `41971880599` / job `127061790102`，PR `41971887843` / job `127061812776`；仍按历史 FAIL 记录，不将本地 PASS 映射为远端 PASS。
- 等待既有流程的专属独立 reviewer 实际检查关键正负例、长路径真实 PG 与完整门禁原件并给出接受结论；其后由原授权 caller/reviewer 核 fresh PR20 source/merge 身份并发布必要变更，确认 push 与 PR workflows 分别真实 SUCCESS。
