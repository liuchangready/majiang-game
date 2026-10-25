# N3 出千决策准入黄金绑定：调用者第 2 轮独立验收

日期：2026-10-25。Bridge：`thread_20261025_114900_c736`。
调用者会话沿用账本记录；未新建任务、会话或转派。

## 验收结论与范围

**组件有界验收通过，已批准归档；源输入 readiness 仍为 BLOCKED 4/5。**
这符合上一轮返修要求允许的“可表达部分真实绑定，不可表达部分明确阻断”。
不表示原五例全部完成，不关闭 N3、G1 或 G5；V4 仍为唯一生产基线。

交付基线 `285781db305980a2d5dfdb3e0eefcc9854296e04`，接受两个本地提交：

- `7d0bb5b72682b02d8881471726774be45a0538e5`：新增 actor 黄金消费者、独立探针和交付报告。
- `8d2709e6d17671cb8ba9c7feba86cbc5cd126fed`：修复 D-R1-01 / D-R1-02。

调用者在 `D:/majiang-wt/n3-table-cheat-admission` 直接从源码重新构建和运行，未依赖开发者旧 DLL 或历史测试日志。
交付累计仅三个文件；实际测试文件名带 `N3` 前缀，属于原任务描述性命名差异。
生产、既有测试、fixture、exporter、catalog、manifest、verifier、项目及环境均未改变。
交付工作树干净，`git diff --check 285781d..HEAD` 通过。

## 两项 MAJOR 的闭环

| 缺陷 | 独立核对结果 |
|---|---|
| D-R1-01：readiness 与输入缺口冲突 | `tm-decision-mismatch` 原输入是 authority 无 active round，现有 actor 始终持有具体 `_round`，故明确 BLOCKED，不计入 bound。旧 round guard 仅作为 supplementary 回归。终局例经真实 Tsumo 后隔离重装原 pending/timing，请求保留 `decisionId=d-1`，真实命中 `RoundFinished`；此异常态明确标注为 fault injection，非业务可达。|
| D-R1-01：readiness 提前退出风险 | 探针先输出分母 5、bound 4、blocked 1 及原因，继续完整运行 28 例矩阵；矩阵独立 PASS，最终 native exit 3，未将缺口反向固定成绿色。|
| D-R1-02：controller 与 V4 不符 | harness 四座均配置 `RemoteHuman`，每例执行前从真实控制快照读取并断言 kind、Connected、epoch0。controller kind 与不能表达的 session/autoRiichiDiscard 字段分开记录。|

`tm-allowed` 和精确 deadline 例通过真实 actor 路径执行；no-timing 与终局例是明确隔离的 fault injection。
合法动作验证一次版本、一次扣费和 receipt 重放不重复；拒绝路径逐项比较真实前后状态、账本、标记、事件、持久提交和发布数量。
tick 前后时序、旧 round、控制代次、终局不可复活以及 136 张物理守恒断言均保留。
无新增公开投影、私有数据泄露或生产恢复路径；本轮未发现新增阻断缺陷。

## 调用者实测

CLI：`D:/Unity/dotnet/dotnet.exe`。
证据根 E：`C:/Users/sncdc/AppData/Local/Temp/codex-n3-r2-review-dd316c823130459689b769ec6a3c0866`。
下列命令均在本交付工作树执行。测试计数顺序为 Total / Errors / Failed / Skipped / Not Run。

| 验证 | 命令或参数 | native exit | 结果 |
|---|---|---:|---|
| Runtime 非增量构建 | `build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v:minimal` | 0 | 0 warning / 0 error |
| 定向新类 | `dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noLogo -class '*N3TableCheatGoldenAdmissionTests*'` | 0 | 7 / 0 / 0 / 0 / 0 |
| Runtime 全量 | 同一 DLL，`-noLogo` | 0 | 362 / 0 / 0 / 0 / 0 |
| solution 非增量构建 | `build dotnet/Mahjong.sln -c Release --no-incremental -v:minimal` | 0 | 12 项目，0 warning / 0 error |
| Domain / Ai / Contracts / Client 全量 | 分别执行 `dotnet/tests/Mahjong.<模块>.Tests/bin/Release/net8.0/Mahjong.<模块>.Tests.dll -noLogo` | 各 0 | 385 / 438 / 149 / 132 项，均 0 error / failed / skipped / not-run |
| mutation/readiness | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-table-cheat-golden.ps1 -EvidenceRoot E/probe`（E 替换为上述证据根） | **3，预期 BLOCKED** | 28 = 5 正 + 23 负；unexpected 0；toolFailure false；matrix PASS；readiness BLOCKED 4/5 |

五个标准模块合计 **1466 passed / 0 failed / 0 skipped**；定向 7 项包含在 Runtime 362 内，不重复加总。
23 个负例均真实执行 7 项、native exit 1、Failed > 0、Errors/Skipped/NotRun 均 0，并匹配预期失败文本。
原 fixture 前后 SHA-256 均为 `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`。

关键独立证据 SHA-256（相对 E）：

| 文件 | SHA-256 |
|---|---|
| `runtime-build.log` | `4E9A3004EC9C4FCFD61F9F75B09505331CEB5458DC5A5F5C1ACFBAD8A13B20DB` |
| `targeted.log` | `708F39790821271CF61909120BD4C23246B97B924716640791BE6BCEA2374B29` |
| `runtime-full.log` | `ACA33C5E90404AB35CB16D59E723D7E62C29DA6F3374C4B78B23D0815BED1B32` |
| `solution-build.log` | `EDB9C4E3955A63498B7B993D399005142B527D50B06E7041ABC31CD9EF7AF04C` |
| `probe.log` | `17C5888EA5F26A532E078EBD9BC0661128C7B63CC26718C7369F0BD8EDFCD73A` |
| `probe/readiness.json` | `10BCD239DBD4EB849980A0212504AEABF9DD472B0E0036033D5ABB4C892D306B` |
| `probe/summary.json` | `C4B7B279B0DDFC62A8BCB15A18A393B3FFC0C77ADFA4002FE63F0F75C170EB3D` |
| `probe/matrix.csv` | `A6985A8AD6198454E47F200F5979B30CEC102C3E88806FF58D8496006224A84C` |

E 下 `sha256.csv` 保存全部独立原始日志和探针产物清单；不将证据 TEMP 内容复制进版本库。
第一轮 readiness 曾错误声明 5/5，该历史结论已由本轮修复取代，不作为通过证据。

## 统一 PR 与剩余边界

复用唯一 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`，目标 `v5`。
本交付是远端已接受头 `285781d` 的直接后继，无集成代码差异；本验收记录仅新增文档。
两轮本地交付及调用者记录统一一次正常快进推送，不另建任务专属 MR。
账本显示同批其他已验收组件均已同步；`thread_20261025_104846_b58a` 与 `thread_20261025_051932_5eeb` 仍在返修，不纳入本次发布。

`tm-decision-mismatch` 的无 active-round 状态仍缺生产生命周期/端口支持；本次不扩大实现范围。
host/session、其余出千字段、完整 N3/G1/G5、Unity/IL2CPP/设备/跨物理网络均不因本组件而关闭。
PG/WSS/Unity/IL2CPP/Gradle/设备/物理网络本轮 NOT_RUN；本地结果不代表本次发布远端 CI 已通过。
主 checkout 的用户未提交文档保持原字节，并从提交范围排除。
