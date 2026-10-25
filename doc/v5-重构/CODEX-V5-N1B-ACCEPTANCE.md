# N-1b actor 组件第 6 轮独立验收与统一 PR19 集成

日期：2026-10-25。Bridge：`thread_20261025_061655_e4bd`，原线程第 6 轮，已调用 `approve_task` 归档。

接受交付 `7f3c444ef2c530099b96811e02bceb4eb9995c80`，包含自 `3bfe952cc7adb9bdc2f5bb0a7589a26ec731886b` 起的六笔开发/返修提交。隔离集成以既有 PR19 头 `8ef0f49f452349cfb1d7ad35cfeec8558d7075db` 为父，正常合并得到 `d1485892698879f3d8624134229fded37393521c`，无冲突。后续出版提交只增加本文，不改变已测试的代码。

## 接受的行为与范围

七个 typed 出千命令通过 `TableActor.SubmitCheatAsync` 进入同一权威队列。状态、136 张物理牌、账本、事件和回执在提交成功后安装；失败保留原状态。跨启动的 offer 持有各自 BootId，每笔命令和系统 sweep journal 也保存真实执行 BootId。恢复使用每笔 journal 的时钟域以及与在线相同的过期谓词，不再从 checkpoint 猜测。

本轮审阅最终七文件修改及累计差异、既有返修反馈和测试。D-R5-01 的两条重放路径已修复；保留完整 cheat 指纹绑定和旧 schema fail-closed。检查 request/scope/API/SHA 隔离、容量下调后的已提交回执重放、owner/control/recovery 代次、deadline、费用/实体守恒、终态及私有 Wire 流，没有发现本轮新增阻断。交付树 clean，`git diff --check 3bfe952..HEAD` 和 `git diff-tree --check HEAD^ HEAD` 均 exit0。冻结 Wire/Receipt/PG/Golden/verifier 未改。

同批已接受 N-1a、N-2 和 N3 scoring 已在远端 PR19 基线上，集成保留它们。N-3 审计和 N3-A 轮次字段绑定仍为 reworking，未复制其工作树或纳入未接受交付。主 checkout 用户未提交内容保持原状。所有六笔 N-1b 提交和本次集成/验收记录统一正常推送到 `origin/feat/v5-1-integration`，复用唯一 PR19 → `v5`。

## 独立验证

CLI：`D:/Unity/dotnet/dotnet.exe`。

交付工作树：`D:/majiang-wt/n1b-cheat-execution`。

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release
```

| 验证 | 交付头 | 集成 d148589 | native exit |
|---|---:|---:|---:|
| Release solution 非增量构建 | 12 项目，0 warning/error | 12 项目，0 warning/error | 0 |
| Domain 全量 | 346 | 385 | 0 |
| AI 全量 | 435 | 435 | 0 |
| Client 全量 | 132 | 132 | 0 |
| Contracts/Wire 全量 | 149 | 149 | 0 |
| TableRuntime（独立重新编译） | 355 | 355 | 0 |
| 标准用例合计 | **1417** | **1456** | 0 |
| 既有独立探针 | 19 | 19 | 0 |
| 新增 mixed-boot 独立探针 | 2 | 2 | 0 |

所有最终测试均 0 failed / 0 skipped，测试数非零；Runtime 未放入 solution，故显式单独构建运行。Domain 数量增加来自基线已接受 N-2/scoring，不是本次添加的 Domain 测试。

证据根 `E=C:/Users/sncdc/AppData/Local/Temp/codex-n1b-r6-bfaf6db9`，旧探针源文件复制自先前独立审阅证据。`probes/N1bCopy.cs` 来自当前交付源码，仅将测试 class 改 partial；其余测试逻辑不变。ReviewR6 使用真实公开 actor/store API，无核心转换 mock。集成 `integration-probes/Review.csproj` 的 ProjectReference 和支持测试源路径全部改为隔离集成树，重新编译后一次执行 21 项。

```powershell
$evidence = 'C:/Users/sncdc/AppData/Local/Temp/codex-n1b-r6-bfaf6db9'
& D:/Unity/dotnet/dotnet.exe test "$evidence/probes/Review.csproj" -c Release -- --filter-method '*Review_*' '*ReviewR2_*' '*ReviewR3_*' '*ReviewR4_*' '*ReviewR5_*'
& D:/Unity/dotnet/dotnet.exe test "$evidence/probes/Review.csproj" -c Release -- --filter-method '*ReviewR6_*'
# 在 D:/majiang-wt/codex-n1b-integration-20261025 再执行上述构建和标准测试命令
& D:/Unity/dotnet/dotnet.exe test "$evidence/integration-probes/Review.csproj" -c Release -- --filter-method '*Review_*' '*ReviewR2_*' '*ReviewR3_*' '*ReviewR4_*' '*ReviewR5_*' '*ReviewR6_*'
```

ReviewR6 两个参数覆盖新 boot checkpoint 前/后：旧 boot 的 seat0 offer、新 boot 的 seat1 offer 同时存在；普通 discard journal 与新 offer 后，tick 仅把旧 offer 强制 FIRST，本 boot 未到期 offer 保留。冷构造另一个 actor 后验证完整 CheatState/round 指纹、136 张逐牌位置、版本及 eventSeq，与在线结果完全一致。原激活请求返回 Replayed；新 boot offer 正常 SECOND 选择，双方余额各 990。再以第三 boot 冷构造 actor，从同一 checkpoint/journal 链恢复完整最终状态。最终 2/2 通过。新增 TEMP 探针编写时两次引用不存在的 generation 属性导致编译失败，改为首次恢复的真实 generation=1 后重新编译通过；这些编译错误未算作产品缺陷或成功测试。

## 原始日志 SHA-256

均位于证据根；`sha256.csv` 保存完整路径。集成和交付日志分别保留。

| 文件 | SHA-256 |
|---|---|
| build.log | `0A11068B4216DF8420295C2F72C0ADEA642B67A683AE36EF40A0F44D6A322C99` |
| solution-tests.log | `F89545B9B1EAE6B630A7329AF7EDFA7304D8673E575BF0ACA3EA10783DD5EA7F` |
| runtime-tests.log | `8985429F540D6202EE5E5D020401175E5F08762A6F0F1E6E227C7CB48E8EE019` |
| probes.log | `8B576644083CC9ED27BFDB8F5F15A36181FDA1CA39ECE94458E7F3EF39754BF2` |
| mixed-boot-probes.log | `CABCB598D89080452A344AE733CAA54BD8439779E86FF81825005C6C11EE3ADC` |
| integration-build.log | `E388879105C5A7CB5EFDADAE4741F1140E5659035D6425AF3900851C84984D0C` |
| integration-solution-tests.log | `30264B48CE77B1856429619B33DAB9D59BA222B4893346EE38E5BF045B9ADC46` |
| integration-runtime-tests.log | `21AFA429DA854EAB197379F3A073B0CE53D6388AD97B9C672B62C8AD41760ED1` |
| integration-probes.log | `282C99D24B9B2341E71E90048EDEB9FAAB4C7FB66143190D1D2AE54A7D1D325E` |

## 结论边界

PASS 仅为 N-1b actor 组件及上述隔离集成回归。协议 CommandKind 到 actor 的宿主入站映射仍属 P4.1/P5.1，未在此补齐。PG、WSS、Unity、IL2CPP、真实设备和跨物理网络 NOT_RUN；本地 in-memory store 恢复测试不作为 PG 证明。G1 仍未批准，G5 未完成，V4 仍是唯一生产架构，无 complete.flag。推送可能触发现有 CI，但本报告不把此前或发布后的远端 CI 记为本次已通过。
