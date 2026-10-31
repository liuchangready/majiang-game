# WebSocket 测试客户端读超时修复：调用方独立验收

日期：2026-10-31，Asia/Shanghai。结论：**组件 ACCEPTED；PR20 整批及 G2–G5 未批准。**

## 身份与范围

- 任务：`thread_20261031_141347_580a`，DeepSeek Harness，`deepseek/deepseek-v4.1-flash`。
- 原 PR20 基线：`8b3bdd35545b1235b1212ea03fce1153e6ba3cdc`；接收者交付：`3e8836a21018dc99100731ad52a93619560fb1d4`，代码提交 `0e5224346fc9fa9cfcf72935302274960e1975e4`。
- 调用方独占集成树：`D:/majiang-wt/caller-receive-timeout-integration-20261031`，分支 `wt/caller-receive-timeout-integration-20261031`，先从 fetched accepted `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 创建，再快进至原 PR20，合入交付。
- 实测集成提交：`efe5e9e3a33430e1c3047d101302731b32be4db4`；双父分别为上述 PR20 与接收者提交；其 tree 与交付完全相等。仅四个声明文件变化，无在途 B2、P4.5 或未提交工作混入。

## 独立检查与结论

调用方阅读完整 helper、九条回归、隐私测试及报告，核对单一底层 receive、完整 message 分片累积、4 MiB 上限、等待与 socket 生命周期取消分离、消费者串行、pending 一次消费、replay/watermark、关闭及错误传播、dispose 观察 pending。隐私断言保持，新增超时后 socket 为 Open 的断言。D-A68-01 和同因 D-A68-06 在本组件范围关闭；原失败 CI 不改写为成功。

调用方在集成树执行（日志根 `build/caller-receive-review/`）：

```powershell
dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-incremental --nologo
dotnet exec dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll -noLogo -noColor -result-xml build/caller-receive-review/all.xml
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/caller-receive-review/audit-evidence.ps1
git diff --check 8b3bdd35545b1235b1212ea03fce1153e6ba3cdc..HEAD
git diff --quiet 3e8836a21018dc99100731ad52a93619560fb1d4 efe5e9e3a33430e1c3047d101302731b32be4db4
```

| 检查 | 真实结果 |
|---|---|
| fresh Release build | exit 0；0 warning / 0 error |
| 集成源码 TableService，direct 原生逐例 XML | exit 0；180 total / 180 passed / 0 failed / 0 skipped / 0 notRun / 0 errors；包括九条新 helper 回归及目标隐私用例 |
| 接收者证据清单 | 255/255 文件大小与 SHA-256 匹配 |
| 接收者原生逐例 XML | 22/22 匹配：全量 180、新回归 9、隐私 20×1；逐例全 Pass |
| 接收者原 helper 红例 | manifest 中首失败保留；FAIL/exit 1，Expected Open / Actual Aborted |
| 本任务 delta diff-check、集成 tree 对齐 | exit 0；工作树实测前后 tracked clean |
| 新精确发布头 push/PR 双 CI | 本次本地验收时 NOT_RUN；交给独立后续证据任务，禁止拿旧绿代替 |
| 本轮调用方 PG、Unity、Player、设备、跨网 | NOT_RUN |

关键证据 SHA-256：

- 集成测试 XML：`C45D4046C22DD86909B16E63502C13A696071300B81C4A878AD09B319A2CCD77`。
- 集成测试日志：`70BCD4727C6D66F81489F488226C32BCFB08C1E786522C97550117E3A0BB8896`。
- 新构建测试 DLL：`C8F8CE9F299D50863DDD016F09D23079BFDFEB61DAE9CD00DC1CD226D8C1E685`。
- 接收者 manifest：`F3C43588F1B1A363CDCD25A086E09EAE4CD91B10609CA4A2B8D223E7D0A6EEDE`。

接收者另有真实 `dotnet test` 全量 180/180 的聚合 console，已核验其日志哈希；调用方本轮执行的是 direct，未冒称 dotnetTest 逐例 XML。接收者 managed unified exit 1：DurableHost 21 fail、Infrastructure 41 fail、Lobby 12 skip，均保留为缺 PG 环境下的 FAIL/BLOCKED/NOT_RUN，不能用于整批通过。

## 遗留与独立任务

1. D-A68-02 的 dotnetTest 逐例证据仍 NOT_RUN，当前 CI 的 direct 原生 XML 只能按 direct 命名；本组件验收不替代 G1 原决定或新增阶段门槛。D-A68-03 全量历史 diff-check 仍 exit 2 / 1,573 条 pre-existing whitespace，不重写历史 patch/evidence；本次 delta 干净。D04–05、D07–08 保留原审计边界。
2. P4.5 `thread_20261031_024102_96e6` 第15轮仍为 `c3c95af6023e13915656acd686269475d5dcceb8`，没有新增源码。调用方重新核对 driver 文件 SHA 与第14轮独立审查相符，fixture 仍直接手写 Fail；D-1-05(a) 及 (c) 内部入口/生产确认回归未交付，组件不批准。保留已接受的 (b)、12 局整场样本和既有 reservation 对照，不重复测试已关闭项。
3. 按 [复核与返修协议](V5-REVIEW-REWORK-PROTOCOL.md) §3「同一类缺陷第 3 次出现：升级人工裁决，停止自动返修」，P4.5 不再发送无改动重复自动返修；原线程保持 waiting_review，未取消、暂停、转派或复制范围。此前第14轮证据 `native-exits.json`、`source-audit.json`、`formal-audit.json` SHA 依次为 `52832A61458AA64E82A9FA12E97CFBCBD3C9AB10530E148708C9B60B38B98342`、`2344BCA418321CF273BFEB9145D43BA44E8B0874CEF74692F8073A13A68E4108`、`7F184790DB6166645A06861A9E61FD32C6DC3BA44A257D28D626A1245AFDD286`，均重新核对相符；不冒称本轮新跑 Unity/Player。
4. 本次全局台账只读一次（196 条）：DeepSeek running 1（B2 `thread_20261031_124526_0eef`），waiting_review 2（本组件、P4.5）；GLM/Anti 活跃 0，调度目标各 0。B2 下一依赖仍是可信单局终局验收后再做 service match 编排，本轮不越依赖派发。
5. 组件发布复用现有 Draft PR20，调用方负责 push/MR 记录；随后空闲一槽可派发精确新头双 CI 审计，保持总活跃上限 2。不得新建重复 PR，不重开 PR19，不合入 v5。

V4 仍是生产基线；G1 已批准，G2–G5 未批准，classified 181 未覆盖；Android FAIL / iOS BLOCKED / 真机及跨物理网 NOT_RUN。Scheduler 仍启用、PT30M、IgnoreNew；complete.flag 不存在且本轮不创建。主工作树的无关修改、Unity 环境、Resources/Burst 均保留。
