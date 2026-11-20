# G2-LOCAL-LITE-20261119 长验证任务卡

## 身份与范围
- 执行者：bridge 派发执行（目标模型 gpt-6-luna）；独立复核由 bridge 流程处理。
- 源基线：用户指定 g2_base `5fe836504af627da67cf46af0ca6657eafdacee5`。
- 执行树：`E:/agent-space/thread_20261119_192821_cc27/worktree`；branch `mesh/task/thread_20261119_192821_cc27`；HEAD 起点 `5fe836504af627da67cf46af0ca6657eafdacee5`。
- bridge 交付目标：`feat/g2-local-lite-20261119`，`D:/majiang-wt/g2-local-lite-delivery-20261119`；执行树仅作业源码树。不得自行提交、推送、合并或部署。
- 保护项：`D:/majiang` 未提交文件及其他 worktree 不读写；不触及 main、g2_base、ui_prot、proto-V3、V5-G2-base。
- 允许：本基线 Unity App/UI/场景、必要 LocalHost/Client 生命周期接线、定向测试、Unity 工具、任务/交付文档、官方 build-shared 生成的完整插件闭包。
- 禁止：改领域规则/版本合同、服务/DB/远端；手改 Plugins DLL/PDB；弱化/删除/skip 既有测试；迁入其他分支 UI/调试实现。

## 目标与固定矩阵
实现 G2-Local-Lite：Windows 鼠标、1 人 + 3 `LocalAiProfiles.Basic`、单局；简化开始/测试局面/退出；真实客户端操作与结算；五个既有人工动作初态；可重复有界关闭/重开、测试数据隔离及可理解诊断。固定逐项 L01-L12 按用户任务原文验收，缺原件不报 PASS；组件、真实鼠标、Player 分开记录。

## 阶段与停止条件
A. 短预检：核对工具/磁盘/进程/脚本锁参数；源与插件 provenance 身份；建立独占 GUID 输出。无长阶段启动。
B. 核心实现与回归：先普通随机单局闭环，再五人工案例/诊断/生命周期；执行确定性正负例和受影响 .NET/UTF 回归。限时由各正式 runner 参数确定；每阶段失败原件保留，不靠旧 DLL掩盖。
C. 冻结候选：记录源码 HEAD/dirty diff、插件 provenance、场景/构建输入闭包；确认所有 writer 已退出。
D. 同包 Player：GUID 独占输出下构建完整 Windows Player；运行受影响真实 Player 矩阵和可用鼠标链路；L01-L12 按证据标注，不足项 NOT_RUN/BLOCKED/UNKNOWN。
E. 封存：收集原生 exit、非零测试数/XML、日志、逐场景、完整包清单和 SHA-256；停止自有 writer 后才写最终 manifest/报告。

## 资源与命令约束
- 固定工具：Unity `D:/game_dev_tools/unity/Editor/Unity.exe`，.NET `D:/game_dev_tools/dotnet/dotnet.exe`；已测 .NET SDK `8.0.426`。Unity 可执行路径存在，文件版本查询暂未返回值，需经受控 Unity runner 预检再确认。
- Unity 项目必须为本执行树 `unity/Mahjong.Unity`；共享 DLL 仅经 `tools/unity/build-shared.ps1` 生成、`check-shared-plugins.ps1` 验收。缺 `Library/SharedPluginProvenance/provenance.json` 时正常重建。
- 输出：`build/<GUID-N>/` 或 runner 独占 GUID 根；存在即拒绝覆盖。测试数据使用任务目录，与正式持久化/outbox 隔离并保留历史。
- Unity/设备共享资源沿用现有 runner 锁；总锁等待最多 600 秒、30 秒心跳，阶段有界超时、正常取消。长阶段只启动自有进程；不杀他人进程、不绕锁、不改许可/安全/网络配置。
- 短预检发现脚本锁和取消接口后记录其确切名称、参数及自有进程身份；失败保留首败。仅修复原因/输入变化/资源恢复后重试。

## 证据路径
本任务卡：`doc/G2_LOCAL_LITE_TASK_20261119.md`。执行日志/Player/哈希清单位于本工作树的本次独占 GUID 产物根；最终交付说明：`doc/G2_LOCAL_LITE_DELIVERY_20261119.md`。

## 第 3 轮复核返修增补（2026-11-20）
- 输入：D-1-03、D-1-04、D-2-01、D-2-02 与 D-1-01 的自动证据补齐；D-1-02、D-1-05 已由独立复核确认通过，不重复改动其行为。
- 仅改当前执行树；保留第 2 轮所有 `build/independent-r2-*` 与 `build/review-r2-*` 首败/成功原件。共享插件继续只由 `tools/unity/build-shared.ps1` 生产、`check-shared-plugins.ps1` 核验。
- Unity 工具预检：`D:/game_dev_tools/unity/Editor/Unity.exe`、`D:/game_dev_tools/dotnet/dotnet.exe` 均须存在；长阶段继续由 Player build runner 获取 `Local\MahjongV5P45UnityValidation`，最多等 600 秒、每 30 秒心跳，阶段 timeout 以 runner 参数和脚本子阶段上限为准。生命周期负例 runner 自有进程仅可 CloseMainWindow/kill 自有 PID，timeout 1..600 秒；取消用独占 `MutexCancelFile`。
- 本轮唯一输出命名：`build/review-r3-player-<GUID-N>/`、`build/review-r3-lifecycle-<GUID-N>/`、`build/review-r3-boot-io-<GUID-N>/`、`build/review-r3-runner-negative-<GUID-N>/`。先确认路径不存在；Player 生命周期数据根位于执行树外的短独占目录；生成包前停止该轮 Unity/.NET writer。
- 依序运行 AppComposition EditMode、Lite UI EditMode、C04 PlayMode 与生产共享插件闭包；受影响 .NET LocalHost/Client/TableRuntime 全量 suite；同一冻结 Player 执行 10 局生命周期、启动存储 I/O 故障、runner 超时和短锁等待负例。首败封存，仅修复原因或输入后可重跑。没有交互桌面/操作者时，真实鼠标动作维持 BLOCKED 并写出最短续跑步骤。
- 第 3 轮首败：`build/review-r3-player-candidate-b93f72f4a61b45c6b772fc4d095fd1df/`，EditMode 编译报 `Mahjong.LocalHost` namespace 不存在；原因是 EditMode asmdef 没有引用官方插件 `Mahjong.LocalHost.dll`。保留日志与 runner manifest；修复为仅给 EditMode 测试 asmdef 增加该插件引用，未跳过测试或放宽断言。重跑使用新 GUID 输出。
- 第 3 轮第二次首败：`build/review-r3-player-candidate-2-37a7c2cf32d943e0b2c5ea69ab238f32/`，Unity 编译报 `SessionCoordinator.IsReady` 不存在；新增断言误用了旧式属性名。按公开 `Current.State`/`CanSubmit` 契约修正测试，不改变验证含义，原件保留，后续仅以新 GUID 重跑。
- 第 3 轮第三次首败：`build/review-r3-player-candidate-3-96e35b915dcb4ed8b425f557428bf53f/`，EditMode 测试真实房间已打开，但在 viewer pump/dispatcher 发出首个快照之前就断言 Ready，XML 显示实际为 Disconnected。测试现先等待并消费真实初始投影再进入座位冲突负例；保留原 XML，不降低断言。

## 第 4 轮复核返修增补（2026-11-20）
- 输入：D-1-03 两个新发现的真实关闭边界（查账已在途、owner capture 超预算）以及 D-1-01 自动真实动作/结算缺口。第 3 轮刚加入关闭前查账逻辑此前不存在，故这两条分支属于本轮新信息；其他已通过项不重开。
- 实现重点：统一 close deadline 覆盖已接收 Submit drain、owner Capture 与同 scope ReceiptQuery；in-flight query 只等待，不重复查。未知/超时保持旧 authority quarantine，不能返回 Completed 或允许第二 host。Player 生命周期脚本会在 dealer-tsumo 案例中通过真实 Client -> LocalHost -> TableRuntime 提交一条玩家命令并验证确认事件与 RoundEnd 投影，仍明确不计为物理鼠标。
- 定向回归：AppComposition EditMode 增加 in-flight 查账成功/超时、owner capture 卡住三项；保留 unknown、部分启动、共享 room、并发关闭原断言。若任何测试/构建首败，保存独占 GUID 原件，仅在修复原因后使用新目录重试。
- 长阶段实际入口仍为 `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-g2-local-lite-player.ps1 -RunRoot <全新 build/review-r4-player-GUID> -TimeoutSeconds 1500 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30`；生命周期入口为 `tools/unity/run-g2-local-lite-player-lifecycle.ps1 -PlayerDirectory <同一冻结候选 player> -DataRoot <执行树外全新目录> -EvidenceRoot <全新 build/review-r4-lifecycle-GUID> -TimeoutSeconds 300 -MutexTimeoutSeconds 600 -HeartbeatSeconds 30`。每个目录启动前确认不存在；停止本轮 Unity/Player writer 后再封存。
- 同包需要真实 LocalHost 动作/结算的自动行由生命周期 runner 证明；L01-L06/L08 鼠标流程、菜单/结算路线的物理点击、以及 GUI 正常关闭后重启需有可见 Windows 桌面操作者。无交互操作者时保持 BLOCKED，并给出被冻结候选、按钮与应收集证据，不重建候选等待输入。
- 第 4 轮首败：`build/review-r4-player-2-b3dd2258fd204369b74462108b6a5438/`，EditMode 编译时新集成测试缺少 `Mahjong.Unity.Table` using；同时 fixture 随机分支误把产品显示值 `random` 当 scenario ID 传入。已补命名空间并改用 `null` 选择随机；保留原日志，后续用新 GUID 构建，不改变断言。
- 第 4 轮第二次首败：`build/review-r4-player-3-d2d8a3156c494ee8bf346b9e9c070da9/`，EditMode 编译中新测试未导入 wire 生成类型别名，且 `ITableChannel` 接口本身没有 `Dispose`。已按现有测试命名惯例加 `WireEnvelope = Mahjong.Contracts.Wire.Envelope`，并仅当被装饰 channel 实现 `IDisposable` 时释放。保留 XML/Unity 日志，后续使用全新 GUID。
- 第 4 轮第三次首败：`build/review-r4-player-4-eb010ce4b16d4966b60717deb8fc1989/`，AppComposition 新真实房间测试总计 27 项中 24 通过，三个新端到端用例失败：close fault/timeout 预期枚举在测试用例中对调；已修正映射。丢失回执的 dealer-tsumo 测试用了 1 秒全程查账预算，实测超时，现将测试限定预算增至 5 秒以观察真实旧 scope 查账；产品仍使用单一配置 deadline。原 27 项 XML 和日志保留，新输入使用新 GUID 验证。
- 第 4 轮第四次执行目录 `build/review-r4-player-5-4e8936cab77546cd85c2efda475a3390/` 在 Unity 首次导入/编译期间被新一轮用户消息打断，runner 输出 session 已中断，目录留有不完整 Unity 日志、没有 XML/summary/manifest，系统中已无本轮 Unity 或 Player writer。未将其计作通过或测试失败；环境恢复后另开 GUID 重跑。真实丢回执测试进一步等待 ReceiptLookup 返回，以在途时检查 room 未关闭。
- D-1-03 真实负例现覆盖 applied+丢首回执+原 request/scope 查账成功、owner 在途查询保持 room、command 未到真实 LocalHost 时 Unknown 且 outbox 原 request/scope 保留、Submit/APP_QUIT 竞态，以及 LocalTableChannel 和真实 LocalTableRoom dispose 延迟/失败。LocalTableRoom dispose 注入仅编译在 Unity Editor 的测试边界；完整应用构建继续使用原生产路径。
- 第 4 轮第五次首败：`build/review-r4-player-8-ce611b036ce74bbeb2bf4c193fcdefea/` 中 AppComposition 29/29、Lite UI 3/3、C04 PlayMode 5/5 通过，但 Windows Player 编译失败：测试用 LocalTableRoom dispose 注入字段在非 Editor 构建路径被引用。已将 seam 完整包进 `UNITY_EDITOR` helper；r8 的失败日志保留，修复后只用全新 GUID 构建。

## 第 5 轮独立复核返修增补（2026-11-20）
- 新输入：D-1-03 再现于权威已 Applied、Applied receipt 同时触发 transient transport loss、Client 转入 Reconnecting 的状态。第 4 轮丢回执测试保持 Ready，未制造 transport loss；因此此前不能发现这个状态门槛缺陷。保留独立复现原件 `build/independent-r4-2813dd8e51394901aa0161628f4ce19a/transport-loss-probe.log` 与 repeat 原件。
- 最小修复：关闭始终在有限预算内 drain 已接收 Submit 并 capture outbox；只有发起新的同 scope ReceiptQuery 要求 Client Ready。若 non-Ready 且仍有未决则 quarantine、保留 room/outbox、禁止新 host；non-Ready 且无未决仍走普通 close。
- 新回归：真实 Lite AppComposition -> Client/TableSession -> LocalTableRoom。其一 Applied receipt 后断开，验证仅一次 action、原 request/scope 持久化、返回 CloseFault 且旧 host 保留；其二无未决请求时制造 transient transport loss，验证 Reconnecting 状态仍能 clean close。既有 Ready in-flight query、capture deadline、writer 故障用例保持原断言。
- 本轮只跑 Unity 定向 AppComposition + Lite UI/C04 回归、官方共享插件生产/核验及同候选 Windows Player 生命周期；不重复人工鼠标验证或无关 .NET 全量。输出路径 `build/review-r5-candidate-7a3f2ed37a2142d98ee04e9709578d61/`，共享锁、超时及心跳沿用 `tools/unity/build-g2-local-lite-player.ps1`。
- 第 5 轮首败：`build/review-r5-candidate-7a3f2ed37a2142d98ee04e9709578d61/` 中新增的两项真实 transport-loss 测试均在 5 秒状态断言失败。实际 Transport 已通知 session，但 `SessionCoordinator.Current` 是最后一次桌面投影，transport fault 不保证立即发布新投影，所以测试读到旧 Ready。该结果不是产品关闭路径回归；保留 XML/日志/manifest。测试改为从 coordinator 的 owner operation queue 读取真实 Client 状态，原 Reconnecting/close/outbox 断言不变；只在新 GUID 输出重跑。
