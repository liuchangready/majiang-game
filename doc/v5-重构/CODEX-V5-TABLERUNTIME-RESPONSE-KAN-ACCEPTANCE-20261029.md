# V5 TableRuntime 响应窗口与杠恢复：调用方第 3 轮验收

日期：2026-10-29。线程 `thread_20261029_051448_61f0`；沿用原线程，未转派或创建任务/会话。MCP `approve_task` 已返回 COMPLETED。

本任务范围 **PASS**。批准候选 `8bb2bda412df48e48f7c382aab4967b145f5d2af`，冻结基线 `183e378da273081075a5eb54472ffe6dfd29f670`。批准不表示整批、完整 LocalHost、P4.2-B、真实 PG/WSS 或 G2–G5 通过。V4 仍为生产基线，G1 既有批准和 181 classified 缺口保持。

## 范围与审查

候选仅改 7 个已授权文件：TableActor、TableVisibilityProjection、两个专用 helper、两个正式测试文件与交付报告。工作树 clean，基线祖先检查、原生 diff --check 均为 0。没有修改 Domain/Contracts/TableService/LocalHost、黄金 fixture/pin 或统一 verifier。

审查与实际回归确认：

- 响应窗口结束时，失效 pending 与 timing 随同状态提交原子安装；仍开放窗口的其他响应保留。非 durable 普通、timeout/AI、cheat/deferred sleeve 与 durable 分支均复用候选 clock/pending，失败不提前消耗。
- 恢复先校验原始 roundId/controlEpoch/重复座位/timing，再过滤过期项；保留当前座实际可执行的 ClosedKan/AddedKan，包括 Kan-only 与含 Discard 两种候选集合。
- 按实际物理模型反向计数普通摸牌与 Rinshan 的 live-wall 消耗；验证杠副露、事件顺序和同命令补牌配对，保留缺事件/错墙/错 meld 拒绝与暗杠脱敏。
- OpenKan、ClosedKan、AddedKan/chankan Pass、多杠、chankan Ron 终态、最后可补牌到墙归零及 WallExhausted/ExhaustiveDraw，覆盖 checkpoint0+tail 和 final checkpoint 真实内存恢复及合法推进。136 实体守恒、拒绝零消耗和终态不复活保留。

## 独立实跑

证据根（本任务树 ignored）：`artifacts/codex-r3-acceptance-97a292c668144b81805b57b8a939078e/`。

候选以 `--no-incremental --artifacts-path <独占GUID>/build` fresh 构建，0 warnings/errors、native exit 0。直接执行该 fresh DLL，`full.xml` 为 **435 total / 434 passed / 1 failed / 0 skipped / 0 inconclusive**，native exit 1；唯一失败是既有 frozen source hash 消费方式不适用当前源码。两个任务正式类分别 **17/17** 与 **18/18**。

原始独立探针源码经 `CustomAfterMicrosoftCommonTargets` 仅追加到测试工程、重新 fresh 编译，原三项及上轮补充四项分别 **3/3、4/4**，native exit 0，无 skip。receiver manifest 实际 **19 条 / 19 条长度与 SHA256 匹配**；交付摘要“21 文件”与文件实际条目数不同，以实测 19 为准。

## 唯一 PR20 收口

从最新远端 `origin/feat/v5-post-g1-batch=eeff5f9688d95afb5d8187137baab92103f1c8ce` 创建本任务 ignored 证据目录内的独立收口 checkout，合并已接受候选，合并提交 `580b1b1`。未修改任何既有兄弟工作树。

冲突解决保留已接受 LocalHost 的 candidateFuriten/InstallFuriten 与本任务的 candidateClock/pending 原子安装；墙验证采用本任务更严格的证据模型。两 helper 和两正式测试与候选完全一致。

收口测试 `RoundDecisionCheckpointTests` 的旧 ClosedKan fixture 带了 `calledTile=0/fromSeat=0`，被新严格校验正确拒绝；调用方只将其改为 Domain ClosedKan 实际语义 `null/NoSeat`。保留正向恢复和篡改墙/状态负例，未放宽生产校验。

最新 PR20 已包含 732e 获验收的 frozen fixture 消费修复。本轮没有改动 visibility-v1.json、任何黄金 pin、VisibilityGoldenTests 或 verifier；新收口树的黄金行为测试随全量实际通过。

| 收口实际运行 | total / passed / failed / skipped | native exit |
|---|---|---:|
| TableRuntime Release fresh | 448 / 448 / 0 / 0 | 0 |
| 原独立探针 + 上轮补充探针（合并代码再次 fresh 编译） | 3 / 3 / 0 / 0；4 / 4 / 0 / 0 | 0 |
| Client Release fresh（含独立 OS CrashWorker） | 182 / 182 / 0 / 0 | 0 |
| Domain fresh | 529 / 529 / 0 / 0 | 0 |
| Architecture（全部十模块 Release 产物齐备后） | 130 / 130 / 0 / 0 | 0 |
| LocalHost Release fresh | 102 / 101 / 1 / 0 | 1 |
| 未改 companion verifier，含 VerifyGitPins | 4 / 4 | 0 |

.NET solution、LocalHost、Client、runtime 最终构建均 0 warnings/errors。原始 XML、日志及 caller-manifest.json 保留证据与 SHA256；不把重复运行混算唯一测试数量。

工具装配失败保留：初次 Client artifacts-path 构建未将 test-only CrashWorker 放到其固定查找目录，7 项 FileNotFound；改为当前隔离树普通 Release fresh 构建后 182/182。Architecture 初次缺 Infrastructure/LobbyService 固定 Release 路径产物，以及一次误指向未生成的 Release 测试 DLL；补齐实际生产构建，执行原 fresh guard DLL 后 130/130。这些不是删除或放宽断言。

## 批次阻断与未测项

本轮 LocalHost 的 `PartialCancellationOfConcurrentInitialOpenLetsTheOtherWaiterSucceed` 实际失败：预期 OperationCanceledException，实际未抛出。该测试在 cancel 与 gate completion 紧邻时竞争；本轮未改其通道实现或测试，未靠重跑/扩大 timeout 覆盖失败。其原始 101/102 结果保留，批次保持 BLOCKED，不据此否决已通过的 TableRuntime 任务。

D-LH-GATE-01/02、在途 93fb 持久宿主、04b1 拒绝回执、e4ae Unity 装配仍按各自 owner/线程处理，未纳入未经批准代码。TableService Rinshan 反向墙事实源缺口仍见开发交付 §6；本任务未声称真实服务恢复通过。PG/WSS 新进程、整场、Unity/Player/IL2CPP、设备、跨物理网和远端新头 CI 均 NOT_RUN。本轮只统一更新现有 draft PR20，不新建组件 MR、不合并到 v5、不部署、不写 complete.flag。

## 复跑命令与原始证据

在独立收口 checkout 内执行（输出均重定向到上述 GUID 证据根）：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-incremental -v:minimal
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-incremental -v:minimal
D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental -v:minimal
D:/Unity/dotnet/dotnet.exe build dotnet/src/Mahjong.LobbyService/Mahjong.LobbyService.csproj -c Release --no-incremental -v:minimal
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental -v:minimal
# 直接执行各 fresh DLL：-noColor -automated -result-nunit <唯一XML路径>
# Domain/Architecture 的 fresh DLL 在本收口树 artifacts/related-build/bin/<project>/debug/。
# 原探针两源文件的精确绝对路径由证据根 Probes.targets 记录；build 使用
# --no-incremental --artifacts-path <收口树>/artifacts/probe-build -p:CustomAfterMicrosoftCommonTargets=<证据根>/Probes.targets
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -RepoRoot . -VerifyGitPins
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot .
```

主要 XML：`full.xml`（原候选）、`final-Mahjong.TableRuntime.Tests.xml`、`integration-client-release.xml`、`integration-Mahjong.Domain.Tests.xml`、`architecture-ready.xml`、`integration-Mahjong.LocalHost.Tests.xml`、两份 `integration-*Probes.xml`。所有已结束运行的 44 个 log/XML/targets 均有字节长度与 SHA256，`caller-manifest.json` SHA256 为 `94ED70992FDEBA3AC88CEA803B2CD11DC0A7D951BE42EDEB2A9E510A8EAAF565`。

额外 catalog 实跑 **FAIL / native exit 1**，保留 `catalog.log`：当前隔离 checkout 的 `core.autocrlf=false`，catalog 的六个旧样本使用 CRLF 原始字节 pin，而 checkout 为 LF。对 ai-v1 独立只读规范化计算得到 CRLF 字节数 51271、SHA256 `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`，精确等于原 pin；未更改 checkout/样本/pin/verifier 来获得绿色。不把既有历史 CRLF checkout 的通过结果冒充本轮 catalog 通过。本轮 batch 仍 BLOCKED。
