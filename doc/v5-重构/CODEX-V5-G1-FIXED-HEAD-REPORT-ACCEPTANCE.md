# V5 G1 固定头盘点报告：caller 独立验收与 PR19 收口

日期：2026-10-25。Bridge：`thread_20261025_034834_ec08`，沿用原 caller/receiver 会话，四轮交付提交 `98d7846`、`51cc902`、`7c451c6`、`92de169bd8eecaf0da45d34ea1a068f7d16de582`。

**报告交付验收通过，已调用 `approve_task` 归档。G1 仍不批准，V4 仍是唯一生产基线。** 报告针对冻结源码头 `8ee22ebf5164f74db75e5a33e87900f5b87cd8bd`，九门为 **7 PASS / 2 FAIL**：黄金行为全字段对齐与三绝技权威执行链尚未满足。N-1a/N-1b/N-2/N-3 是残余工作；自动化适配增强与宿主 AI 组合根不能被自行升级为本次 G1 强制门。

## 1. 范围及最终返修验收

相对冻结头，交付分支仅新增 `V5-1-REVIEW.md`，源码、测试、工具与 fixtures 均未改。最终工作树干净，diff-check 通过。前轮已接受的依赖守护清单、黄金证据分层、四通道命令映射、正式任务职责和阶段边界保持有效。

本轮直接审阅文档变更、TEMP 探针及负例源码，并在最终交付 `92de169` 上实跑：

| 验证 | 实际结果 |
|---|---|
| R4 主探针 | 21 ASSERT-PASS / 0 ASSERT-FAIL，数值 exit 0，记录实际 HEAD=92de169 |
| native exit 语义 | `cmd /c exit 0/1` 后保存 `$LASTEXITCODE`，分别输出数值 0/1 |
| 隔离 selftest | 故意失败断言，failures=1，数值 exit 1 |
| caller 独立主探针负例 | 只在新 TEMP 副本将 `$e0 -eq 0` 改为 `$e0 -eq 99`；主脚本自身统计 1 次失败并 exit 1，真实源码和原脚本未改 |
| 证据不可变 | R2/R3 历史日志及 receiver R4 正负例日志完整 SHA256 均与交付一致 |
| 新鲜 Release 构建 | TableRuntime.Tests 及依赖，0 警告 / 0 错误，exit 0 |
| 当前交付测试 | TableRuntime **313 passed / 0 failed / 0 skipped**，exit 0 |
| 机械目录校验 | 主探针内 catalog validator exit 0；不代表行为全字段对齐 |

执行命令（PowerShell）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence/run-probes-r4.ps1
$probeExit = $LASTEXITCODE
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence/negative-selftest-r4.ps1
$negativeExit = $LASTEXITCODE
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build
```

实跑输出重定向到下列新独占目录；没有覆盖历史日志。caller 首次误给 `dotnet test` 传入仅 build 支持的 `--no-incremental`，MSB1001/exit1 保留在 `table-runtime.log`；随后按上列 build/test 命令完成真实验证，该调用错误不归因于交付。

## 2. caller 原始证据

探针目录：`C:/Users/sncdc/AppData/Local/Temp/g1-caller-round4-84813b4ebc544f59b3ff50e3a0a896cc/`。

| 文件 | SHA256 |
|---|---|
| probes-current.log | `534F7DD50F2B6387501D0F39C8072112F0BE3945BEDFFA8CE8B9BE282C132944` |
| negative-selftest.log | `C94D38309C0F6E34567BD0F9C8E64C973CAE9ECE55A4A34EF4FC3336E09B9488` |
| negative-main-probe.ps1 | `11D2D77C2DC4430DC3877443C178C13F1C8B8E8067821FDF9894FBA31FD42D7C` |
| negative-main-probe.log | `1B095D56970B7EBD09743A080ACA170B8108D86487F196A3F0E578C7B3E8D77D` |

测试目录：`C:/Users/sncdc/AppData/Local/Temp/g1-caller-r4-tests-6d3a85f0ecac4a6d9c4842d71e9ee1c4/`。

| 文件 | SHA256 |
|---|---|
| build.log | `F3B9E006DC11E9829D351ED26EC03CCF151C6748982DEC8258328B5394558F2C` |
| test.log | `95D75BD05EBB303EF11410E006F8D6A0EBDD73C49ABF0900D7F1E7B1BDDA3E92` |
| table-runtime.log（首次调用错误） | `8AA5A0CC4A5C32385DFA395BD5C85499A0B8B183D12CB70305766F87E583F61C` |

R2/R3 原始哈希分别为 `3138E0B27E61E95A549018BC92CCF5EA360E4D33F154C69EAD730BDBA209C09A`、`146C388923F6427D0527C1806C1C2913E8E78664606649475D394FBFB2B80FE7`。receiver R4 正负例哈希分别为 `A2BF068F5D5164910903D0905CADC16EA38D5C44279A5307A0BA7269F7779BFC`、`FDB777F6F59682D2988B89DC5FE952C90BE208CBBBBE713FFEC5394AFDA9252F`，本轮亲自读取核验。

## 3. 固定头与后续集成头分开

唯一 MR 沿用 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，`feat/v5-1-integration -> v5`。最新已发布头 `cf6f65548c7928fcc64b1c42e06726759ba22915` 已含同批 AccessCapabilities、public history 与 frozen AI context 的独立验收交付。分支审计确认这些组件无额外未发布交付，只有本报告的四个提交待纳入；其他历史/平台/试验分支不属于本批，不合入。

caller 在独占 worktree `D:/majiang-wt/codex-g1-integration-20261025` 上合并报告，得到 `d05358ade779b15886f0283b9dfb8a696d2a822f`。相对 `cf6f655` 仅新增报告，`git diff --exit-code cf6f655 HEAD -- dotnet tests tools assets` 为 0。报告本身仍为固定头审计，不把后续兄弟组件写回历史采样。R4 探针的“仅报告差异”不变量适用于交付分支，不能在含后续组件的集成头冒称同一基线。

集成头独立重新运行 Release solution build（`--no-incremental`）：0 警告 / 0 错误、exit 0；TableRuntime.Tests 的 `dotnet test -c Release`：313 passed / 0 failed / 0 skipped、exit 0；catalog validator：exit 0（仍仅机械校验）。随后发布提交只追加本 caller 验收文档，不改变已验证代码。

集成证据目录：`C:/Users/sncdc/AppData/Local/Temp/g1-r4-integration-9d61e53aa59a4ddda5524ed20cbac32c/`。

| 文件 | SHA256 |
|---|---|
| solution-build.log | `FA9EA5C10CA447017A4E5E6880A61A0D325B5C584EBD28210DB720AFBE85F957` |
| table-runtime.log | `23570BA095671BDF23975812D44CF750B92F1BA188DBC5BC747A485B834316B4` |
| catalog.log | `E3F631D97B0D9C2AF3B5084E4CB751BE0FCAE856D6F231FE8A950E98CF38F6D2` |

## 4. 保留的测试与平台边界

上轮统一 verify 的原始 summary 哈希 `86D9B383B639720936F1C693FBA4D15913B3A393F463958D6673F47D64F2BD0E` 本轮再次核实：**1356 = 1344 passed / 0 failed / 12 skipped**（本地无 PG），严格 verifier exit 1，不能称本地全绿。既存双 CI 1356/0/0 只适用于其记录的旧头；历史 Client 失败仍 UNRESOLVED，不能以当前通过反推 flaky 或已查明根因。

本轮不重跑完整 PG/WSS/Unity/IL2CPP、设备/物理网络或 V4 全量；不宣称当前发布的远端 CI 结果，不批准 G1/G5，不做生产部署。报告所记残余实施任务不因本次文档交付批准而关闭。
