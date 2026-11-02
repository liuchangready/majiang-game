# G2-C04-D05 R1 双任务 bridge 分工与接续合同

日期：2026-11-02，Asia/Shanghai。用户明确要求：隐私验证器通过 bridge 给 Luna 6；验收矩阵、冷恢复与生命周期给 DeepSeek Harness。**本卡覆盖上一份单执行者 bridge 任务卡的路由/分工，不改动旧文档或失败记录。** 上次模型切换被拒绝的派发没有启动任何执行者；原生 Luna 已交付停止，不并行唤醒。

## 固定基线与完整问题

- 原代码47ecf6a6、交付390aa95f；基线eb9b55411fcb8c0ffd0d527bbf3edb9009299082在此基础上仅加完整独立复核和旧返修任务卡。本次再加本卡，仍为caller docs-only。
- 全文必读：CODEX-V5-G2-C04-D05-REVIEW-R1-20261102.md，SHA256 E1792E3D4E32268F143A6C929AFD3223074487A7D070D937515F0974890993BB。四组问题一次给全：R1-01 BLOCKER真实wire验证、R1-02 MAJOR恢复和生命周期、R1-03 MAJOR缺失矩阵、R1-04 MAJOR验证runner。不得隐去未分配给自己的问题；按分工标明其他owner。
- 复现脚本/原件只读根 E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review；audit SHA 8BCB44FB92881AC3E9DB24D0DB205A5E65566D41CD40E96CA8EAA354B8353B13；50项稳定manifest SHA 72A0EB39BB5FEAA1F82B961EB027AA759015E394FBB88126CDED45FAFDD56236。
- 原实现与证据只读树 E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1。父代理fresh154/154和483/483、源码2496/包205项匹配只绑定原候选，不称返修版本通过。
- 原实施任务卡、D05边界合同全文、AGENTS、doc/LONG_VALIDATION_TASK_TEMPLATE.md与当前20轮复核协议必读。父代理/bridge独立reviewer复核，执行者不自批。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## A：G2-C04-D05-R1-PRIVACY，Luna 6

执行器codex，模型gpt-6-luna（当前receiver默认）；sender reviewer模型gpt-6.1-sol与之不同。当前bridge支持同agent的dedicated reviewer，不需要切换DeepSeek模型，不改机器配置。

树 E:/majiang-review/g2-c04-d05-privacy-luna6-20261102-r1；分支 wt/v5-g2-c04-d05-privacy-luna6-20261102-r1。完整关闭R1-01，并关闭R1-04中属于verifier测试脚本的有界进程/输出问题。

唯一允许修改的tracked文件（6）：
```text
dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj
dotnet/tools/C04PrivacyMatrixVerifier/Program.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs.meta
tools/tests/C04PrivacyMatrix.Tests.ps1
doc/v5-重构/V5-G2-C04-D05-PRIVACY-VERIFIER-DELIVERY-20261102.md
```

必须实际从WireCodec/typed generated parser重建Welcome身份和快照内容；采集并保留实际Welcome原字节。空raw、非法[1]、跨seat raw替换但摘要不变、手牌计数摘要改写、错误身份/owner/audience/版本/未知字段都必须非零拒绝；合法当前实际序列正向通过。不得只检查自声明decodeStatus、SHA或summary。不能要求旧11帧缺Welcome样本继续PASS；诚实记录旧样本的不足。self-test/fixture必须真实合法序列化，不能冒充GAME。

复现见完整报告R1-01和run-raw-mutations.ps1。修复C04PrivacyMatrix.Tests.ps1同步stdout/stderr和无超时的问题，补有界子进程、native非零/大输出/取消/稳定收尾，原测试覆盖不弱化。

先在忽略的独占build/<GUID>用真实生产序列化完成工具正负例；必要时建立只读共享源码编译夹具检查probe，不修改asmdef或新增生产程序集依赖。该任务无需反复跑完整Unity/Player。最终真实应用矩阵由集成后的B任务执行，不把组件样本贴GAME标签。

## B：G2-C04-D05-R1-MATRIX-RECOVERY，DeepSeek Harness

执行器deepseek_harness，使用其已配置默认deepseek/deepseek-v4.1-flash；用户未要求改变此模型，不传非默认model。
树 E:/majiang-review/g2-c04-d05-matrix-recovery-ds-20261102-r1；分支 wt/v5-g2-c04-d05-matrix-recovery-ds-20261102-r1。

完整处理R1-02、R1-03与R1-04的runner部分。验收矩阵所需runner修复是本任务组成，不能因verifier属于A而漏掉该已知MAJOR。新增C04PrivacyRunner.Tests.ps1只用于有意义的真实runner短负例，原verifier测试脚本归A，只读。

唯一允许修改的tracked文件（27）：
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
unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs
unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs
unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs.meta
unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs.meta
tools/unity/run-p45-c04-privacy-matrix.ps1
doc/v5-重构/V5-G2-C04-D05-PRIVACY-DELIVERY-20261102.md
unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs
tools/tests/C04PrivacyRunner.Tests.ps1
```

必须复现并修复：all-AI存档在新SeatPlan下冷恢复controller/epoch错误；真实ReleaseControl后冷恢复AI却CanTickAuthority=false。当前/恢复controller优先于初始计划，已开放deadline不因未绑定真人无限延迟。批量修viewer quarantine归属、默认单viewer owner资源释放、ticket退役有界且不允许重放；完整矩阵按R1-02断言。

补N1–N8/U1–U3/P1–P2与C04-G5-01..03的缺项：合法非空袖牌和私有费用/ledger、慢consumer真实饱和、实际receive旧raw延迟注入、Windows原scope/request恢复查询和零重复副作用。调用合法公共三绝技命令用于数据对照已授权，但不改/接管D04三绝技实现和人工范围。真实外部缺陷须给最小复现/精确子项BLOCKED，独立可做部分继续，不以空值或旧测试替代。

runner先修固定候选与复制输入闭包、XML精确case/分母/零fail/error/skip/notrun、marker真实内容/产物包身份、原生非零上抛、超时取消后writer停止再释放锁/映射与稳定封存。短负例涵盖伪marker、skip/error、超时/取消/拒绝正常关闭、大输出、资源归属；不能杀他人进程或绕锁。修正文档exe667136B与总包128346555B的口径，不改旧marker/日志。

## 两任务交付接口与依赖

- 两树从同一caller文档基线创建，tracked范围交集为零。不得写对方文件、变更对方工作树或自行为对方cherry-pick；父代理统一集成和绑定候选。
- A维护C04PrivacyProbe公开Begin/Seal/CapturedFrameCount/ResetForTest与现有RecordInbound签名兼容；捕获格式保留frames及现有身份字段，可增加原始Welcome记录/字段，验证从字节重建，摘要仅作一致性核对。verify <path>成功native0、任一无效native非零，保留PRIVACY_MATRIX_PASS含frames/hash便于runner消费；self-test数量由真实矩阵更新，不固定假五负例。
- B继续使用现有probe调用和正式wire契约，不手写另一套采集/验证替身。若需要新增记录上下文/类型，提交精确调用签名与实际wire样本给父代理协调，不抢改A文件；不为协调先停掉全部独立工作。
- A在独占build中交付新鲜合法真实序列化输入/负例、格式说明、verifier/probe代码提交和工具测试证据。B先完成产品/矩阵实现、.NET回归和runner负例；当前树的旧verifier绝不能用于正式隐私PASS。
- 最终Unity/Windows完整矩阵需双方已审查修复和冻结合并候选。B若独立工作全部完成但A增量尚未集成，提交可review的全部代码/证据，正式应用矩阵明确BLOCKED_ON_PRIVACY_VERIFIER，不空转轮询或用旧工具刷绿；父代理/专属reviewer在收到双方交付后按bridge依赖机制接续B原线程完成，不新增重复实现任务。
- 两边的实际原件/异常/未覆盖必须完整保留。独立组件通过只认可对应范围，不能将D05/C04/G2写为完成。

## 默认阶段、时限与交付

A/B均先填写独占阶段卡：实际命令、进入条件、成功断言、超时、输出和取消边界。短环境预检默认120秒，确定性工具/组件180秒，必要.NET整套600秒；A完成工具/采集合同交付，B在廉价缺陷全部修复且新verifier集成后进入贵验证。合理调整预算需记录原因，不降低断言。

共享Unity锁Local\\MahjongV5P45UnityValidation，最多等600秒，每30秒心跳；Unity冷导入参考900秒，Player build1200秒，按实际场景设有限超时。E盘独占GUID输出、缓存/TEMP/Library/Player，不改机器许可、不强杀他人、不改旧原件。先正常取消并核验自有进程身份/停止writer再封存，未知退出UNKNOWN；具体原因修复/输入变化/资源变化后才重试。

阶段在授权内自动继续，不新增逐阶段人工确认。不能只完成第一条就结束。真实跨owner依赖须明确，独立工作全部做完；父代理协调最终候选。

A唯一新交付报告为PRIVACY-VERIFIER-DELIVERY；B更新原PRIVACY-DELIVERY并保留历史。均给逐ID映射、真正正负例/命令/native/XML/计数/SHA/源码/输入/包身份、首次失败、未覆盖、commit及clean/diffcheck；代码和docs-only报告分开。

两边均不得再委派、approve、push、建改PR、merge、deploy、改scheduler/complete.flag、改原bridge/原生任务历史、扩Domain/Client/proto/TableActor/存档schema/asmdef。D03/C01/C02/C03已接受范围不重开，D04人工保留，V4生产，G2–G5未批准。

