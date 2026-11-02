# G2-C04-D05 R1：DSH 冷恢复与生命周期、验收矩阵分别派发

日期：2026-11-02，Asia/Shanghai。用户最新明确要求将冷恢复与生命周期、验收矩阵分别作为一个任务给DeepSeek Harness，并删除原合并任务。本卡覆盖旧双任务卡中B的合并范围；Luna任务和其六文件权限不变。

## 原任务处置与共同基线

- 原合并线程thread_20261102_224144_b057已按用户要求删除（bridge返回DELETED），不是完成/批准。暂停时审查bootstrap失败，独立核验HEAD90140b349974ee30ed2257a12486c9ed718197e6、tracked/untracked clean、无build产物；普通删除被worktree未合并保护拒绝，在确认全部提交已保留于caller review分支后，按明确删除授权force删除，无独有实现/证据丢失。
- 删除前详情和检查归档：E:/majiang-review/d05-dsh-task-split-f653a66b7c444132a1e9685d4b13e504/original-thread.json、preservation.json。仅删除此旧DSH线程；Luna thread_20261102_224144_4290保持，不复制派发、不操作其工作树/状态。
- 两个新DSH任务使用配置默认模型deepseek/deepseek-v4.1-flash，不改模型配置；从90140b34加本caller任务卡的共同docs-only基线创建，各自独立树/分支。父代理/专属reviewer独立验收，执行者不自批。
- 已审代码47ecf6a6、报告390aa95f，R1结论NOT_ACCEPTED，1 BLOCKER+3 MAJOR。全文必读CODEX-V5-G2-C04-D05-REVIEW-R1-20261102.md，SHA E1792E3D4E32268F143A6C929AFD3223074487A7D070D937515F0974890993BB。全部已知问题仍一次给全，不因拆任务隐去已知缺陷。
- R1-01及verifier测试子项由Luna负责；R1-02本卡A负责；R1-03和R1-04 runner子项本卡B负责。原154/154、483/483和包2496/205项检查仅绑定被审版本，不能冒称新版本通过。
- 取证只读根E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review，稳定50项manifest SHA72A0EB39BB5FEAA1F82B961EB027AA759015E394FBB88126CDED45FAFDD56236。复现脚本若写死旧根，先在自己独占build复制/调整输出后运行，不能覆盖旧证据。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## A：G2-C04-D05-R1-RECOVERY-LIFECYCLE

目标：完整修复R1-02恢复controller、AI推进、room/viewer资源归属及ticket有界生命周期。
工作树E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1；分支wt/v5-g2-c04-d05-recovery-lifecycle-ds-20261102-r1。

唯一允许修改的tracked文件（19）：
```text
dotnet/src/Mahjong.LocalHost/LocalTableHost.cs
dotnet/src/Mahjong.LocalHost/LocalTableChannel.cs
dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs
dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs
dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs
dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs
dotnet/src/Mahjong.LocalHost/LocalTableSeatPlan.cs
dotnet/src/Mahjong.LocalHost/LocalTableConnectionTicket.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalHostSessionSmokeTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCommandTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs
unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs
unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs
unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs
doc/v5-重构/V5-G2-C04-D05-RECOVERY-LIFECYCLE-DELIVERY-20261102.md
```

必须批量修复和验收：
1. all-AI fresh保存后新SeatPlan=AllFourHumanSeats再冷恢复，实际RECOVERY_CONTROL_INVALID；恢复的controller/epoch必须优先于新的初始plan。
2. 真TableSession ReleaseControl回执Recorded/LOCAL_CONTROL后关闭、同计划冷恢复且仅观战绑定，seat0 automated=true/epoch1却CanTickAuthority=false；已release AI无需真人绑定即可推进。
3. 按当前/恢复controller、live binding、room phase、既有deadline决定推进；fresh/recovered × human/AI/released/claimed × bound/unbound × preparing/started矩阵，旧decision/epoch拒绝、不重开/延迟已有deadline。
4. CloseRoomViewerAsync失败/隔离时保留归属；默认单viewer owner退出释放lease/driver，多viewer只detach自己；其他viewer及authority不受关闭影响。
5. ticket过期/failed/detached退役有界且不允许重放，重复/并发/取消路径保留正确拒绝。

详细实际/期望、位置、repro命令和SHA见完整报告R1-02。真实探针源码位于取证根probes/Program.cs，使用现有P41Harness和生产room/channel/TableSession，不反射写权威。不能只根据绿的旧测试宣称复现已消失。

优先保持现有公共组合接口兼容，以便矩阵任务独立编写测试。必要新增API先给最小签名/理由和回归，不能越权限改Domain/Client/proto/actor/schema/asmdef。默认源文件与组合路径全部由A独占，矩阵方不能抢改。

交付新报告V5-G2-C04-D05-RECOVERY-LIFECYCLE-DELIVERY-20261102.md：逐子项修复映射、真实正负例、deadline/lease/epoch/恢复证据、源码和命令/native/XML/计数/SHA、首次失败、未覆盖平台、代码提交及clean。本任务本身的组件验收不需要等待整份G2报告；完成全部自身独立验证后及时提交给专属reviewer及父代理，供B接续。

## B：G2-C04-D05-R1-ACCEPTANCE-MATRIX

目标：完整补齐R1-03固定验收矩阵，并修复R1-04 runner使结果可信。
工作树E:/majiang-review/g2-c04-d05-acceptance-matrix-ds-20261102-r1；分支wt/v5-g2-c04-d05-acceptance-matrix-ds-20261102-r1。

唯一允许修改的tracked文件（9）：
```text
dotnet/src/Mahjong.LocalHost/LocalPrivacyScenario.cs
dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs.meta
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs.meta
tools/unity/run-p45-c04-privacy-matrix.ps1
doc/v5-重构/V5-G2-C04-D05-PRIVACY-DELIVERY-20261102.md
tools/tests/C04PrivacyRunner.Tests.ps1
```

固定分母N1–N8/U1–U3/P1–P2及C04-G5-01..03不缩减。必须完成合法非空袖牌/私有费用ledger正对照、慢consumer真实队列饱和、完整控制/恢复行为观察、Unity/Windows实际receive旧raw延迟投递、Windows原scope/request恢复查询与零重复副作用。使用A交付的真实生产边界，不能手写替代controller、反射改权威、用旧generic .NET测试或空字段替代新增矩阵。

本任务只实现本scope的场景/测试/runner，不修A负责的产品源码。现有合法公共三绝技命令用于数据对照已授权；不接管D04三绝技实现/人工裁决。真实外部功能缺口给最小复现和精确BLOCKED子项，其余独立项继续。

R1-04必须先用短组件负例修正：冻结候选与复制输入闭包；XML精确case/分母且零fail/error/skip/notrun；marker内容/错误数/包身份；native非零上抛；有界超时/取消、正常退出/自有进程身份、writer停止后释放锁/映射与稳定封存。新C04PrivacyRunner.Tests.ps1测试真实runner函数/判断而非复制实现。包含大输出、拒绝正常关闭、伪marker、XML skip/error/count、wrong source/dirty、锁等待超时等负例。不能绕锁/杀他人进程。纠正文档exe667136B vs总包128346555B，不改旧产物。

先把自己独立可做的场景/工具/测试全部完成。最后真实Unity/Windows矩阵同时依赖Luna的新probe/verifier和A的恢复/生命周期修复，必须由父代理/专属reviewer统一集成、冻结后继续；不得用旧verifier/旧生产路径冒充正式PASS，也不空转轮询等待。若独立部分完成而依赖尚未集成，提交完整可复核代码/负例与接续命令，准确标BLOCKED_ON_PRIVACY_AND_RECOVERY，交给父代理和专属reviewer协调原线程接续；这不是全D05完成。

唯一报告仍为V5-G2-C04-D05-PRIVACY-DELIVERY-20261102.md，保留历史并记录每个固定行的真实PASS/FAIL/BLOCKED/NOT_RUN、精确源码/包/工具身份、命令/native/XML/计数/原件/SHA/首次失败、依赖与未覆盖平台。

## 并行与验收规则

- A19/B9/Luna6三方tracked交集0。A的独立组件交付不依赖B；B最终真实应用矩阵依赖A与Luna。双方不自行为对方cherry-pick、写文件或创建任务，由父代理/专属reviewer集成。需要额外文件给精确最小请求，不擅自扩大。
- 本卡、旧完整实施卡、D05边界合同、AGENTS、doc/LONG_VALIDATION_TASK_TEMPLATE.md及20轮复核协议必读，read-only。阶段卡先填写实际命令、进入条件、成功断言、有限超时、输出/取消路径。阶段通过自动继续，不设逐阶段人工等待；跨owner依赖如实记录。
- 输出/缓存/TEMP/Library/Player为E盘独占build/<GUID>，保留初失败/UNKNOWN。短环境预检默认120秒、组件180秒、必要.NET整套600秒；Unity冷导入参考900秒、Player build1200秒，合理调整记录原因，不因时间到转PASS。
- 共享Unity锁Local\MahjongV5P45UnityValidation，最多等600秒，每30秒心跳；不绕锁、不杀他人、不修改机器许可。先正常取消并证实自有writer停止再封存；未知退出UNKNOWN。仅修因/相关输入/资源改变后重试。
- 实际记录/工具fixture/真实Unity/Player分别标注，旧证据不冒称新候选通过；最终合同要求同候选同包时必须满足。代码和docs-only报告分别提交，生成物不进Git。
- 不再委派、不approve/push/建改PR/merge/deploy/改scheduler/complete.flag/改旧任务历史。原合并任务按用户要求删除，不复活或复制；D03/C01/C02/C03接受不重开，D04人工保留，V4生产，D05/C04/G2–G5未批准。
