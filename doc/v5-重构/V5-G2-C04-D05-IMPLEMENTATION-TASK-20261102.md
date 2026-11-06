# G2-C04-D05 实施任务卡（Luna6 执行，当前调用方验收）

日期：2026-11-02。用户明确要求：同步并集成 D03 后，将 D05 下发给 Luna6，当前调用方负责独立验收。本卡是实施授权，不只是前置准备；不解除三绝技人工裁决、不批准正式 G2。

## 1. 基线、执行者与目标

- 执行者：GPT-6-Luna；一个执行实例，不再委派、桥接派发或唤醒旧线程。
- 复核者：当前调用方。阶段通过后执行者在本授权内自动继续，不新增逐阶段人工等待点；父代理可并行只读检查。
- 工作树：`E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1`，所有实现和构建指定此目录，默认 `D:/majiang` 不写实现。
- 分支：`wt/v5-g2-c04-d05-luna6-20261102-c04d05a1`。
- 起点：`957ad475e90a99e83fd7c413c0cbe6e30c00620f`；本任务卡后续为 docs-only 初始提交。开始时记录实际完整 HEAD。
- 该起点已发布到现有 Draft PR20 的 `feat/v5-post-g1-batch`，`git ls-remote` 已确认。其 D03 代码集成提交为 `2bbb4e9c3be42ea65a02af9625fb826700280cd2`，D03 原 Windows 受测代码 `ba9c83a319389bb872eaced0f2e39d66c5995f30`。
- 接受基线仍为 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；V4 生产；正式 C04/G2–G5 尚未批准。
- 目标：实现并证明一个真实 LocalHost room 的四席/观战独立通道、私有信息隔离、角色切换缓存清理和旧绑定拒绝，完成下述真实通道、Unity 和 Windows Player 矩阵，交父代理独立验收。

先读本树完整 `AGENTS.md`、`doc/LONG_VALIDATION_TASK_TEMPLATE.md`、适用深层说明与当前复核协议，再读以下已接受输入：

1. `CODEX-V5-G2-C04-D03-CONTINUATION-ACCEPTANCE-20261102.md`：D03 接续接受及统一候选实测，交集现可串行修改。
2. `V5-G2-C04-D05-CHANNEL-BOUNDARY-20261102.md` **全文**，特别是 §4 ticket/控制状态机、§5 文件边界、§6 N/U/P 矩阵、§12 前置接受。该文件正文的旧 D03 等待语句已由本卡替代；其他契约保留。签名草案不要求照抄未编译代码。
3. `V5-G2-C04-RECOVERY-REQUEST-DELIVERY-20261101.md`、C04 固定 census/准备报告；只读引用已接受 D01、D03，不能重开其历史缺陷。

上述文件均位于 `doc/v5-重构/`。原边界准备工作树只读，不使用它的旧基线开实现。

## 2. 保留的有效结果与本轮缺口

D03 已取得同包四 Player GAME、对应 raw verifier native 0，PlayMode 4/4；调用方独立原件/代码复核、3,286 条哈希核对及四正例/两负例重验已过。统一代码候选 fresh LocalHost **147/147**、TableRuntime **483/483**、新生成四份动作 TOOL_FIXTURE 经 fresh verifier 全过。本轮须保留这些实现及必要回归；不能把历史 Windows 包标成 D05 新版本通过。

正式 D05 的缺口是已接受前置合同列明的单 store/lease/actor/driver room 核心、每 viewer connection 生命周期、seat/role 绑定和独立公开流，以及生产 Unity 多视图装配和最终隐私证据。本任务一次处理所有已知同类问题，不把准备探针、scripted channel 或模型当实现。

原始前置报告保留的缺陷/风险 ID 用于修复映射，不另造不相干分母。正式固定分母仍是 **C04-G5-01..03**，层内检查 **N1–N8、U1–U3、P1–P2**；不改 census 扩分母或提前标绿。

## 3. 最小实施边界与核心不变量

- 单 `LocalTableRoom` 拥有一个 store/lease/actor/driver；每连接只有自己的 session/scope/audience/binding/cursor/队列。关闭一个 viewer 只 detach，关闭 room 才释放整桌。不同 room 互不影响，同目录第二 owner 仍 lease conflict。
- room 颁发进程内一次性 ticket，按已接受的 issued/reserved/consumed/attached/detached/failed/expired 语义实现：Connect 预留，Open 唯一消费；HELLO 不接受客户端自选 role/seat、foreign session 或复用票据。并发/取消/HELLO失败/过期/重复消费/座位争用均有负例，凭据不写日志或存档。
- 复用既有 wire、TableActor audience stream/visibility、TableSession 和 Domain。默认单机 seat0+AI 的公开 API、控制、存档、D01 原请求对账和 LOCAL 语义保持。
- 默认四人类 room 开局等待各初始人类绑定或显式 release；Playing 不因 viewer 断线退回开局等待。LocalHuman 不由 AI 双写；已有 decision deadline 照常，未连接人类不新开 decision；release 后 AI 可前进，claim/release 的 controlEpoch fencing 有效。恢复用持久化 controller，不用初始 SeatPlan 覆盖。
- spectator 只映射 PublicSnapshot/PublicUpdate/PublicStream，不能走 Player 私有映射；spectator 命令拒绝且零业务增量；慢 consumer 有界隔离与人数上限。
- role switch 新建 session/coordinator/baseline，清理旧私有 cache；延迟旧真实帧不得进入新 binding/view。controlEpoch 与 bindingEpoch 不混用。
- 136 实体、幂等、账本归属与单向模块依赖保持。Unity 不直接依赖 Domain，不新增 asmdef 依赖或协议字段。

## 4. 分阶段执行与进入条件

### A：短预检与任务卡落地

记录实际 HEAD/范围、依赖、空间、工具路径/版本、资源锁与唯一证据根。已有 Hub 当前许可可用于 Unity6000.3.27f1 batchmode，曾 native198 是历史，不推断用户无许可，不改账号/许可设置。仅在资源状态变化或需启动 Unity 时做有界预检，避免重复冷导入。

### B：纯 .NET 实现与确定性回归

先只完成 room/connection/ticket/seat/controller 生命周期和 N1–N8，使用生产 `ITableChannel` + `TableSession` 和真实序列化入口，保留默认单机、lease、恢复及原请求零重复。不先跑 Unity 来发现核心逻辑错误。

构建使用现有 `D:/Unity/dotnet/dotnet.exe`，C#9/netstandard2.1 共享库。测试直接运行真实 xUnit DLL 并记录 XML/native exit；`dotnet test` 仅构建或 Total0 不能算通过。现有恢复测试按仓库正常 `bin/Release/net8.0` 路径寻找 CrashWorker/TableRuntime fixtures，必须在本 E 盘独占 source 中 fresh 构建这些真实辅助闭包；仅 `--artifacts-path` 输出会产生13项找不到DLL的假失败，禁止借旧工作树 DLL 补绿。

全部已知缺陷批修后跑必要 LocalHost 与关联 TableRuntime 回归。源码/输入不变不机械重复；实际失败先查原件，不刷绿。

### C：Unity 装配与证据工具

在 room API 稳定后接生产 AppComposition/Factory/SessionCoordinator 多 view，保持普通 UI 路径。增加定向 U1–U3、opt-in probe、独立 build/runner 和必要 raw verifier；不复用 D03 collector key 或修改其 driver/runner。

首次昂贵构建前，对齐真实 producer 的序列化 schema、角色/枚举/cursor/version/epoch、accepted publication、command/receipt/event/状态与验证器。用真实原件正对照、错误 owner/audience/seat/scope/binding/版本/缺字段/坏protobuf/哈希和原生非零负例证明门禁，不使用“摘要字段相互抄值”证明正确。短锁超时/取消必须能失败封存。

隐私矩阵以 **owner + audience + stateVersion + 当前可见性** 做 typed 字段归属验证；每个 owner 自己的私有字段必须有真实非空正对照，合法公开弃牌/副露不误判为泄漏。不用裸字节 grep、相同余额 scalar 或“所有私有字段都为空”造绿。

为袖牌/私有费用正对照，可以调用既有合法公共命令产生真实数据，但不修改/返修/接管三绝技实现或 D04 人工范围；不能伪造 ledger、event、状态或反射改权威。合法正对照若因真实现有缺口无法产生，明确对应子项 BLOCKED 并给父代理最小依赖，不冒称 D05 全部完成。

### D：冻结最终候选与真实 Player

前置全过后冻结完整 HEAD 与输入闭包，定向 PlayMode U1–U3 实际执行，再 fresh Windows Player 同包跑 P1/P2（含 C04-G5-01..03 真实隐私/角色场景和恢复对账）。正常应用通过已有组合边界与真实 room/channel，不能只用 MonoBehaviour 手写状态、直接领域投影、scripted channel 或打印成功日志代替。

记录实际 source/build/package/player identity、各连接 role/seat/account/session/scope/binding/control/sync/cursor、版本/事件、原请求/canonical digest/receipt 与同 publication 后态；原始 raw 帧、结构化归属判定、恢复前后零重复效果都要能独立重验。目标事件 seq、receipt high-water、acceptance counter、完整 stream cursor 和 latest checkpoint 各按实际合同区分。

新真实失败必须先归因并做组件级复现/负例验证，再按变化重验受影响闭包；不能每发现一个硬编码映射错误就直接重复完整构建。禁止为了省时去掉 provenance 或最终同候选同包要求。

### E：交付，父代理独立验收

阶段通过自动前进；只在必须外部交互、额外具体文件授权或确实资源不可得时报告明确阻塞，不因日常决策停止。最终停止自有 writer 后写 summary，封存稳定 manifest/哈希；封存后不再写已哈希日志。

唯一交付报告：`doc/v5-重构/V5-G2-C04-D05-PRIVACY-DELIVERY-20261102.md`，包括逐 ID 修复映射、全部已知问题、N/U/P 与三条正式 row 的 PASS/FAIL/BLOCKED/NOT_RUN、源码/包/工具身份、原生命令/计数/XML/log/manifest SHA、首次失败、未覆盖平台，以及 clean/diff-check。代码与 docs-only 报告身份分开，生成物不入 Git。

父代理独立检查代码/真实原件，并进行必要独立回归；执行者不自行批准、不推送、不开/改 PR/MR、不合并/部署、不改 scheduler/complete.flag，不操作原 bridge task 或其他 owner 的状态。

## 5. 默认资源与失败边界

- E 盘独占 `build/<GUID>`；缓存、TEMP/TMP、UPM/NuGet、Library、短路径 staging、Player 均在 E；不改机器环境。拒绝覆盖旧证据，不复用旧业务 DLL。
- Unity/Player/共享插件构建使用 **`Local\MahjongV5P45UnityValidation`**；不嵌套跨进程持锁，不绕锁，不杀其他进程。默认总等锁600秒、每30秒心跳。
- 短工具预检、.NET阶段、首次Unity导入、Player build、各Player场景分别明确有限超时；可参考已有导入900s/build1200s，但按实际成本说明。文件闭包/哈希扫描也发进度，不能静默数小时。
- 正常取消使用 runner 支持的 cancel-file/正常退出，**不使用 Ctrl-C 跳过 finally**。先以短负例验证失败收尾。
- 区分产品/工具缺陷、资源等待、环境失败和会话故障；只在具体原因修复或条件改变后重试。进程是否活跃以自有 PID/startTicks/日志为准，不轮询其他 owner 全局 ledger。
- 每阶段向父代理同步明确结果，长阶段持续心跳。复核协议沿当前树已同步的20轮规则，不因此绕过三绝技既有人工保留。

## 6. 精确 tracked 文件授权

以下为允许修改清单，不要求无故触碰每个文件。需要额外路径时先做完独立可做部分，并给父代理最小文件和可复现原因；不能私自扩大到 Domain/Client/协议/TableActor/存档格式或程序集依赖。

```text
dotnet/src/Mahjong.LocalHost/LocalTableHost.cs
dotnet/src/Mahjong.LocalHost/LocalTableChannel.cs
dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs
dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs
dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs
dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs
dotnet/src/Mahjong.LocalHost/LocalTableSeatPlan.cs
dotnet/src/Mahjong.LocalHost/LocalTableConnectionTicket.cs
dotnet/src/Mahjong.LocalHost/LocalPrivacyScenario.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalHostSessionSmokeTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCommandTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs
dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj
dotnet/tools/C04PrivacyMatrixVerifier/Program.cs
unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs
unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs
unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs.meta
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs.meta
unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs.meta
tools/unity/run-p45-c04-privacy-matrix.ps1
tools/tests/C04PrivacyMatrix.Tests.ps1
doc/v5-重构/V5-G2-C04-D05-PRIVACY-DELIVERY-20261102.md
```

本任务卡、D05边界报告、D03交付/runner/probe、AGENTS、通用模板、复核协议、旧census只读；忽略的独占 build 诊断/证据可写。新 Unity `.meta` 用独立 GUID。

## 7. 开始执行

先核实当前 HEAD 和 tracked clean，填写阶段命令/超时/产物，再进入 A/B。保留主仓和所有旧工作树改动。本卡已给出实施与必要验证授权，直接推进到可供父代理独立验收的结果；不要只报告准备完毕或阶段通过就结束。
