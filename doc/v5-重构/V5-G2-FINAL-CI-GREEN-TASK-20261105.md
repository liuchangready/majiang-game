# G2 最终统一候选 CI 收绿任务（2026-11-05，Asia/Shanghai）

用户本轮明确要求向 Luna 下发最终统一候选 CI 收绿任务。执行模型固定 gpt-6-luna，专属独立 reviewer 使用桥接器配置的不同模型；接收方不得再委派。

## 任务身份与接续

- 工作树：E:/majiang-review/g2-final-ci-green-luna6-20261105
- 分支：wt/v5-g2-final-ci-green-luna6-20261105
- 派发前已接受基线：9e3e08d00dd7e3f9aca563af16b6b0e4c5c35882；其上本任务卡为 docs-only 提交，最终派发头另在工具消息给出。
- fresh fetch 2026-11-05 成功：PR20 源分支 feat/v5-post-g1-batch 为 4a855c3c49e65ec97708784d84a784df9f11c199；origin/v5 为 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7。前者是上述基线祖先；不要把主仓旧分支当当前候选。
- 旧任务 thread_20261104_192433_c77d 已 completed，仅局部修改/缺口报告接受，仍 mergePending；这是明确承接其已接受成果的新范围，禁止重启/改写旧任务、覆盖其树或冒认旧 reviewer 身份。
- 旧树 E:/majiang-review/g2-final-ci-luna6-20261104 保留为只读证据源。其已接受三文件已按字节复制并独立提交至 9e3e08d0；C04 SHA256 DEC3C9F2879B926B81E1E7BC109F6D942C6D151A0C2F2D38DE6E317F391F3E2C。
- 20261104 CI 交付文档和 binding 的 NOT_ACCEPTED/未提交/independentReview NOT_RUN 是开发交付时历史状态；旧任务后续已独立接受局部范围。不要修改历史文档或据其旧文字推翻真实审批，使用本任务新交付记录承接。

## 修改范围

只允许下面精确 tracked 文件，按根因选择必要子集，名单不是要求全部修改：
- dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs
- dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs
- dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs
- dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs
- dotnet/scripts/pg-lobby-verify/start-postgres.ps1
- dotnet/scripts/pg-lobby-verify/pg-common.ps1
- dotnet/scripts/pg-lobby-verify/cleanup-postgres.ps1
- tools/ci/verify-v5-postgres.ps1
- tools/ci/verify-v5-table-postgres.ps1
- tools/tests/VerifyV5PostgresCi.Tests.ps1
- tools/tests/VerifyV5TablePostgresStartup.Tests.ps1
- doc/v5-重构/V5-G2-FINAL-CI-GREEN-TASK-20261105.md
- doc/v5-重构/V5-G2-FINAL-CI-GREEN-DELIVERY-20261105.md
- doc/v5-重构/V5-G2-FINAL-CI-GREEN-BINDING-20261105.json

LocalTableDriver/Runtime 只允许补最小内部确定性测试观察/iteration completion barrier 与必要 accessor，不改变时序、真人绑定门控、领域 deadline 或恢复策略。不要添加公开平台 API。LocalControl 先证明测试观察时序问题，再最小修复；如果证明 Client/其他生产路径有 bug，提交完整可复现缺陷与精确最小范围请求，不越界或用弱断言掩盖。
tools/verify-v5.ps1、workflow、Domain、Contracts、Wire、Client 生产、Unity/Android、依赖与机器配置均只读。PG 脚本只有根因证明需要时改，不能改 gate/skip/过滤用例/伪造数据库或给固定 PASS。

## 已知问题全集与退出条件

1. G2-VALIDATION-02 / MAJOR：LocalControlTests.DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect，约575–690，完整 LocalHost184项中183过1失败；期望 Ready 实际 Synchronizing。先读原 XML/log 和真实 TableSession 控制 receipt→lookup/replay→CONTROL_APPLIED baseline 应用链。需要确定性正负例和真实应用完成观察，拒绝 receipt-only、无关full PUSH、过期/被替换correlation、foreign session/binding/epoch；不可任意sleep、轮询重跑凑绿或去掉 Ready/幂等/旧帧断言。原请求/scope/digest/持久化outbox结果和唯一副作用必须保留。不要未取证就声称与旧SYNC组件同因。
2. G2-VALIDATION-01 / MAJOR：实际统一验证的 Lobby 临时PG缺 psql.exe、pg_ctl.exe、share/timezone/timezonesets，12个LobbyService Npgsql stream-close失败，cleanup失败；旧cluster已用自有pg_ctl正常关闭，原件保留。先核完整官方pinned archive/解包闭包/环境/路径和生命周期，不能只临时补一exe或连接共享数据库凑绿。覆盖不完整安装、缺资源、启动/清理失败的负例，失败必须传递；正常真实自有PG的 psql/Npgsql、迁移与LobbyService89/89、正常清理、环境恢复、无自有残留进程有证据。保持ownership marker、loopback-only、端口拒绝与路径安全；不能关闭别人的PG。若原脚本无缺陷，仅正确恢复环境而不改代码。
3. G2-REVIEW-01 / MAJOR / OPEN_SCOPE_GAP：C04的PrepareRecoveryCaptureOnNextOpen使LocalTableRuntime恢复时deferDriver=true，DriverRunning=false，后续actor.TickAsync只证明actor；旧任务未覆盖真实driver退休后未绑定真人门控。本任务明确授权上述LocalHost两生产文件中最小internal completion seam，确定性运行真实driver恢复路径并观察该轮处理结束；无绑定不得重开真人决策，deadline前不提前退休、跨deadline按原timing退休，错误门控/早退休/重置deadline拒绝。不把StopAsync或手动actor tick当driver验收，不依赖不确定sleep。保留合法首次恢复GraceMillis=5000，只在源RecoveryGraceApplied=false时加一次；不得重新引入“源grace=0恢复也必须0”的已撤回错误oracle。观察点必须不改变生产行为且无死锁。
4. 冻结与完整验证：旧完整Unified19 PASS/2 FAIL仍是FAIL；旧source push41729487041 SUCCESS与PR41729493387 FAILURE的合成merge ebc98716b8588d5ff6af774b04674bc4f3dd0e3f是不同身份，不互代。新候选远端CI尚未运行。全部相关缺口短验证通过后冻结clean精确代码头，执行真实完整门禁，21个项目行是当前历史分母；以真实自动发现合同和逐例XML为准，新增用例/项目必须解释，不能硬编码数量或删用例。完整LocalHost/Client/Runtime、真实PG与统一验证零fail/error/skip/notrun；不得以单案5/5代替全量。

## 已接受成果及保留风险

不重开C01/C02/C03/D03/D04已接受范围、D05隐私/控制/恢复组件、SYNC application-callback绑定；D05独立R7接受：PlayMode7/7、普通Windows同包七场景/P2、206文件包与8共享DLL匹配。旧首败FU-02原因UNKNOWN、SYNC-03间歇记录仍保留，不能声称本任务已解释历史原因。相关完整资料在旧树doc/v5-重构及build，D05接受追加段在V5-G2-C04-D05-FINAL-UNIFIED-DELIVERY-20261103.md:917。
本任务不重跑无变化的Unity/Windows矩阵；若LocalHost生产DLL因新增观察点变化，必须明确受影响闭包与后续应用重新绑定要求，不把旧包冒称新头。Android和C06是其他范围，本任务通过不等于G2阶段批准；V4继续生产。

## 执行阶段与长验证合同

先读工作树AGENTS.md、D:/majiang/AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md、D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md（当前同缺陷20轮规则）。已有此任务卡；在自己build/<GUID>补充实际命令、阶段条件、源码/输入身份、端口、峰值空间、超时、输出和自有进程边界，开始昂贵验证前完成。
A 短预检工具、可用空间、owned进程/端口、原始失败和scope，不重复启动旧任务。
B 一次批修上述已知缺陷，真实组件确定性正负例；关键定向固定5次（每次独占XML），随后必要完整LocalHost/Client/Runtime。
C PG生产端/测试端真实合同与负例、超时/原生退出/取消/封存验证，杜绝假绿。
D 冻结后使用本树实际入口：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunUnifiedVerification -LogDir <本次独占table目录> -UnifiedLogDir <本次独占unified目录>
核实际支持的Root/LobbyRoot/PgZipPath参数和路径约束再填值，使用完整官方pinned PG包和独占自有数据库；不向旧封存根写入。
E 停止自有writer后封存，提交必要源修改和新交付报告/binding，使独立reviewer可审；报告源码/测试/环境/包变动边界、逐项目逐例计数、命令native/XML/SHA、first failure和PASS/FAIL/BLOCKED/NOT_RUN/UNKNOWN。

工具先核 D:/game_dev_tools/Git/cmd/git.exe、D:/game_dev_tools/dotnet/dotnet.exe（当前SDK8.0.426/runtime8.0.32）；指南只作路径参考，不能自动执行setup_environment.ps1或改系统PATH/驱动/许可。D/E目前各约327GB/296GB可用，执行前重新测。
共享资源最多等待600s；长阶段包括解压/hash/测试均每30s实际stdout心跳，阶段超时有界：短预检120s，build600s，完整统一验证1800s，确需3600s须事先记录原因；无变化不机械重跑。正常取消/关闭自有进程，禁止绕锁/杀他人/覆盖旧证据。
若接收方/桥接中断，保留未知退出与原件，从可验证检查点续接，不能伪造原runner成功。所有重要失败一次给全、同类合并后批修，遵守同缺陷20轮人工升级。

## 独立审查与远端CI交接

接收方不push、不改PR、不merge/deploy，不修改scheduler/complete.flag；完成可审源码和本地真实全绿后交专属reviewer。该reviewer需实际复验关键正负例和完整门禁证据，不把开发者执行写成自己重跑。
独立接受后的最终候选需由caller统一核对fresh PR20源头、保留所有已发布接受项，再在现有feat/v5-post-g1-batch/同一Draft PR20上完成必要发布与远端CI核定（不得新建重复PR、force-push或合并v5/部署）。发布前核实当前会话授权与远端祖先；若权限/授权不足则明确列出唯一发布依赖，不能把新头remote CI NOT_RUN写成已收绿。
远端push workflow与PR合成merge workflow必须分别记录source/head/merge SHA、run id、原生artifact/XML/真实计数。最终CI收绿的退出条件包含所需最终头远端CI实际SUCCESS；单纯局部任务completed不等于此目标完成。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

