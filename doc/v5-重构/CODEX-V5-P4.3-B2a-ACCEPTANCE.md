# V5 P4.3-B2a Client 可见事件供给独立验收

日期：2026-10-27（Asia/Shanghai）。结论：**APPROVED，仅 P4.3-B2a 组件范围**。

任务 `thread_20261027_194656_8f37` 第 2 轮，接受候选
`378c337a9612e5e7d4fec632d461f6c9578022f2`，冻结起点
`36014038bcbe61f7b9acdd4fad4694f329bbd4d2`。
Caller 已调用 `approve_task`，桥接返回 COMPLETED。

## 实现与缺陷关闭

Client 在确认快照通知中增加不可变的类型化事件批次，带完整绑定身份、流游标和事件序号。
旧五参数构造入口及 IGameSession 接口保持兼容；无需 Unity 或服务端改动。
事件在对应帧真正接受时交付，早期缓冲、去重、授权座位/观战隔离和重同步仍有效。
全量基线携带的事件供对账，PlayableEvents 仅供增量事件消费。

- D-R1-01（MAJOR）关闭：SYNC_RESPONSE 与有/无事件的全量 PUSH 均按快照 event_seq
  建立整段历史水位；后续帧不能把水位以内的历史重新标成可播放事件。水位以上的稀疏事件仍合法。
- D-R1-02（MINOR）关闭：新增测试已回到 expectedFiles 的单数文件名
  `TableVisibleEventTests.cs`。冻结到候选仅五个授权文件，无越界净改动。

新增正式回归覆盖 Player 0–3、Spectator、空/带事件基线、重连、换局和稀疏新事件。
上一轮 caller 原四探针源码/断言 SHA256 保持
`BD4B7C52E3670E966A67DE281C1616E0B23903CC885E84198F224441E210709F`；
本轮只复制工程并精确调整 Compile Link 文件名，未改探针源码或断言。
R1 的 1 pass / 3 fail 原始证据保留，未覆盖为通过。

## Caller 当前任务树实测

执行树：`D:/majiang-wt/p43-b2a-client-events-ds-20261027`。
证据根：`build/codex-b2a-review-r2-378c337/`。

| 项目 | Fresh build / direct runner 结果 |
| --- | --- |
| Client | 182/182 |
| Contracts | 149/149 |
| WSS | 80/80 |
| 原四条 caller 探针 | 4/4 |

所有构建和 runner native exit 0，构建零警告/零错误；XML 均非零选中，
failed/errors/skipped/not-run 全 0。候选 tracked clean，冻结..HEAD diff-check exit 0。
直接运行 fresh Release DLL；未用 dotnet test 的空成功或旧 DLL 代替执行。

复现模式（SDK `D:/Unity/dotnet/dotnet.exe`）：

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release
& D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll -result-xml build/codex-b2a-review-r2-378c337/client.xml
& D:/Unity/dotnet/dotnet.exe build build/codex-b2a-review-r2-378c337/probes/ReviewProbe.csproj -c Release
& D:/Unity/dotnet/dotnet.exe exec build/codex-b2a-review-r2-378c337/probes/bin/Release/net8.0/Mahjong.Client.Tests.dll -class Mahjong.Client.Tests.CodexReviewTests -result-xml build/codex-b2a-review-r2-378c337/probes.xml
```

Contracts/WSS 同样分别 fresh build 对应 csproj，再 direct runner 输出同目录 XML。
原始四份 XML、build/run 日志及探针工程均在证据根。
`evidence-sha256.csv` SHA256：
`B0E9524A56B77ACBD6884205CC4B655F9088C02A17C2386677B6F1839260412F`。

## 同一 PR20 的集成实测

从 PR20 当前已接受头 `c5efdf2442dec548e8be82f2363a72a5218b8735` 建立隔离树
`D:/majiang-wt/codex-b2a-accepted-20261027`，保留已接受 P5.1 等全部内容，
merge 接受候选形成 `7f567e68cf346a61896adf66d3ff50a3688096fa`。
Client 生产及测试目录与接受候选逐树零差异；不纳入 Android/P4.1 在途源码。
其余近期已接受任务均已有 PR20 synced 记录，没有遗漏的已接受组件待合入。

执行 `dotnet build dotnet/Mahjong.sln -c Release`，native exit 0、0 warning/0 error。
Architecture 和 TableModel 为独立工程，随后分别显式 fresh build，再直接运行四个测试程序集：

| 集成测试 | 结果 |
| --- | --- |
| Client | 182/182 |
| Architecture | 92/92 |
| Unity.TableModel（真实源码链接） | 134/134 |
| TableService（含真实回环 WebSocket） | 152/152 |
| 合计 | 560/560 |

所有构建/runner native exit 0；XML failed/errors/skipped/not-run 全 0。
首次在仅构建 solution 后调用独立 Architecture DLL 失败，因为该工程不在 solution，尚无产物；
日志保留为 `initial-architecture-missing-dll.log`。显式构建工程后通过，未改测试或产品代码。

集成证据根：`build/codex-b2a-integrated-7f567e6/`。
其 `evidence-sha256.csv` SHA256：
`4D89B5A88A079F1D213DBAF5193AC2331AFF82C3F1DAB3B3FD45F0B9F193D9F4`。

| 集成 XML | SHA256 |
| --- | --- |
| Client | `9E601883332C2ABC52553A8C4CF4676A0F193C9AF8D24C1EA75834F257B1B4AD` |
| Architecture | `461DEA8208808CA5EB1F2B1CBBB7019A4EF951AD016CDA1B1EF20B10D44F0BD8` |
| TableModel | `6BA7F437751AC088763DF4F0D89624EAEBF096B2A725B47642986D571133A6D2` |
| TableService | `1B394E3AC07717CC4FE7353B053C0B0F10F661292581F36DD1D4B0D02A3954E7` |

## 发布登记与保留边界

沿用唯一草稿 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，
源分支 `feat/v5-post-g1-batch`、目标 `v5`。本报告为 caller 独立验收记录，
额外只增加此文档；不新建组件 PR，不部署、不合并到 v5、不创建 complete.flag。
桥接 merge-back SHA 由 caller 在成功推送后另行登记。

新提交远端 CI 待独立执行/核实。先前 c5efdf2 的 2257/2257 为历史 CI，不能移用到新头。
本轮未重跑 PG、Unity Editor/PlayMode/Player/IL2CPP、真机或跨物理网络；
纯模型源码链接和回环 WSS 不等于生产完整 E2E。
Unity B2b 消费接线、P4.2-B、P4.5、P5.4b 仍需后续交付。
G1 APPROVED；G2–G5 未批准，V4 继续生产。
181 classified、Android FAIL/iOS BLOCKED、设备/跨网 NOT_RUN 继续按原台账保留。
