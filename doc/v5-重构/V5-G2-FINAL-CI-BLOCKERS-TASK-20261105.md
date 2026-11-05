# G2 最终 CI 两组阻断收口（2026-11-05，Asia/Shanghai）

用户本轮明确要求“下发任务给luna6 关闭最终 CI 阻断”。执行固定 gpt-6-luna；公共模型目录确认 codex receiver 默认 gpt-6-luna、独立 reviewer 默认 gpt-6.1-sol。接收方不得继续委派，不换模型。

## 身份、范围、检查点

- 新任务树 E:/majiang-review/g2-final-ci-blockers-luna6-20261105，分支 wt/v5-g2-final-ci-blockers-luna6-20261105，创建基线 861756d2a4b09ff01ca2ac6f4b84c32aa773168a。任务卡在其上 docs-only 提交，派发消息提供实际头。
- fresh fetch与GitHub API已核 PR20 OPEN/Draft、base=v5、源分支feat/v5-post-g1-batch、head=861756d2a4b09ff01ca2ac6f4b84c32aa773168a；origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7。已有两个Luna本地/只读复核任务completed；本任务是两组新阻断的实现接续，不重启旧线程或向只读reviewer发越界修码，不覆盖旧任务树。
- 保留已接受源码6850fcc及控制对账、真实恢复driver、完整PG安装验证：本地完整门禁21项目行PASS、14测试工程2872/2872、0fail/error/skip/notrun，真实表库42/42与13/13、Lobby89/89、LocalHost184、Client182、Runtime483。此历史通过不替代修复后的最终组合验证，也不表示候选已获最终独立接受。
- 旧只读复核发现OPEN MAJOR并明确 NO_INDEPENDENT_ACCEPTANCE；其任务completed只接受复核报告。旧证据根 E:/majiang-review/g2-final-ci-green-luna6-20261105/build/117ec7ac36b744e9ae5773d44ec4f658，verdict reviewer-verdict-final.json；caller复现 build/aab9cd5f6f2d4026bbd3086172e8153e/caller-verdict.json。旧树原件全部只读。

只允许下列tracked文件，按最小根因选择，不要求全部修改：
- dotnet/scripts/pg-lobby-verify/cleanup-postgres.ps1
- dotnet/scripts/pg-lobby-verify/pg-common.ps1
- dotnet/scripts/pg-lobby-verify/start-postgres.ps1
- tools/ci/verify-v5-postgres.ps1
- tools/ci/verify-v5-table-postgres.ps1
- tools/ci/test-v5-table-evidence.ps1
- tools/tests/VerifyV5PostgresCi.Tests.ps1
- tools/tests/VerifyV5TablePostgresStartup.Tests.ps1
- doc/v5-重构/V5-G2-FINAL-CI-BLOCKERS-TASK-20261105.md
- doc/v5-重构/V5-G2-FINAL-CI-BLOCKERS-DELIVERY-20261105.md
- doc/v5-重构/V5-G2-FINAL-CI-BLOCKERS-BINDING-20261105.json
workflow、tools/verify-v5.ps1、所有Domain/Client/Runtime/LocalHost业务源码及其测试、Unity/Android、依赖、机器配置只读。若出现真实范围外缺陷，一次给全可执行证据与精确最小scope请求，不暗中扩围。本次允许改工具生产函数与对应自测来修根因，不允许放宽验收门禁。

## 全部已知阻断

A. G2-VALIDATION-01-PATH-CLEANUP-01 / MAJOR
- cleanup-postgres.ps1:86使用Remove-Item递归删除时，完整文件路径达到260字符出现异常、root残留；独立caller已用真实脚本复现短路径成功/长路径失败。真实PG S1/S3长路径清理失败，S5 teardown也不能删除；首次运行native exit未捕获为UNKNOWN，不能写成0。
- 复现入口 tools/tests/VerifyV5PostgresCi.Tests.ps1，独占外部TEMP、pinned PostgreSQL ZIP、按实际参数选择可用BasePort；读取旧verdict及清理日志 D:/g2-reviewer-22831f02b7164f8282f9138d76e8f1e5/temp/VerifyV5PostgresCi-20261105-124219-d97a1e36/logs-s1/pg/cleanup.log。先做小型真实文件系统夹具，不能先重复完整PG/构建找路径bug。
- 最小修复可采用ownership校验后的长路径安全删除，或在解压/启动前依据实际archive闭包预检拒绝不受支持长度。不得改系统长路径策略、拼cmd删除、删除外部目录、旁路marker/data_directory/root/port/db身份或软链/junction边界。
- 退出断言：短路径真实PG正例通过；最深entry>=260字符的真实S1/S3清理通过且root不存在/无自有PG或监听，或在解压/任何PG副作用前准确BLOCKED且无残根/进程；S5 lock-holder失败、释放后正常清理仍成立。WhatIf/foreign marker/foreign listener/可疑root/边界路径拒绝保持。先明确绝对目标在原owned root内再递归删除，纯PowerShell/.NET单shell，不强杀或删他人进程/证据。

B. G2-FINAL-CI-TABLE-EVIDENCE-01 / MAJOR
- 现861756d2的push run41971880599/job127061790102、PR run41971887843/job127061812776均completed/failure，唯一失败步骤Verify table-result XML evidence gate。API只有exit1注解；Node20弃用只是warning，不准据此乱改actions版本。
- workflow .github/workflows/v5-verify.yml第70行真实命令 powershell ./tools/ci/test-v5-table-evidence.ps1；必须修此真实入口，不删步、不continue-on-error、不换fixture为假PASS。
- caller已在本新树执行一次完全相同自测（powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-table-evidence.ps1），native exit1，81/82。原件 artifacts/table-evidence-selftest-8c4c1bdc55bf4f5790ff5e24e10f8019/results.json。70 seal success manifest excludes self and rehashes clean失败：exit=1 sealOk=False rows=0；控制台明确 Test-ManifestPathIncluded 未识别。71预期seal失败负例及其余81案通过。
- 生产verify-v5-table-postgres.ps1有Test-ManifestPathIncluded函数（约213行）；自测AST抽取seal函数时可能遗漏依赖。这是本地证实的调用边界错误；远端是否完全同因须审原run日志/artifact，不凭推断写已确定。保留本次首败，不重复未变输入凑绿。
- 修复生产调用/自测依赖合同的根因，不复制生产算法、手造manifest或放松XML与封存断言。保留全部XML/native出口/错assembly/计数/重复ID/XXE/stale/目录外XML/manifest自排除/重新哈希/封存失败拒绝/已封存根零改写等正负例。
- 同类AST提取依赖在相关工具调用方一次搜索，批量处理scope内已知问题；范围外给完整清单。修复后真实test-v5-table-evidence.ps1完整分母零fail、native0；合法seal有完整非空manifest且重哈希一致，负例native非0且不报告PASS。
- 最终远端push与PR workflow必须分别在最终source/merge身份真实SUCCESS。当前历史FAIL保留，不能把本地2872或工具单绿叫最终CI收绿。

## 执行阶段、资源与证据

先读本树和D:/majiang/AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md、D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md，遵守同一缺陷20轮人工升级。沿用这张卡，在自有build/<GUID>补全实际阶段/命令/端口/绝对root/环境预算、取消与证据；执行昂贵验证前填写。
A 短预检工具/空间/实际自有PG与监听/当前头和原始失败；工具D:/game_dev_tools/Git/cmd/git.exe、D:/game_dev_tools/dotnet/dotnet.exe。D/E空间刚核约314GB/292GB，C盘TEMP历史不足；使用本进程已核足够空间的独占TEMP/TMP，不改系统或他人环境。
B 先两组廉价组件正负例与真实入口合同对齐，再必要真实PG S1/S3/S5与相关自检；不机械重复控制/driver已绿全量。
C 所有workflow已有廉价证据门禁入口按实际顺序验证（native/direct/dotnetTest/table等）；本任务不删减workflow、不放宽native/XML/hash门禁。只有相关输入变化才重跑已绿检查。
D 修复冻结clean候选后执行生产 tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunUnifiedVerification -LogDir <本次独占table目录> -UnifiedLogDir <本次独占unified目录>。精确参数先核，table root必须满足现有artifacts containment；Lobby root自有、真实完整官方pinnedPG、迁移/崩溃恢复/备份恢复/环境恢复/清理完整。逐项目/逐例XML与真实native计数，21行/2872是历史分母，新增必要自测或领域用例要解释而非硬编码。
E 正常停自有writer后写新DELIVERY/BINDING与稳定manifest，提交源/文档便于审查，旧原件/首败/UNKNOWN不改写。记录本任务closure与候选独立接受分别状态，不能用completed代替远端CI。
共享等待上限600s；所有长阶段每30s stdout真实心跳，解压/hash也报告；短预检120s、组件/构建600s、完整PG/统一验证1800s，可事先记录理由延至3600s。无变化不重试、不绕锁、不强杀他人、不新增许可/驱动/系统配置。中断先查自有进程与检查点，未知退出保持UNKNOWN，正常取消和稳定封存。
保留历史FU-02 UNKNOWN/SYNC-03未解释及D05已接受成果；不重跑Unity/Windows/Android，不认证它们已覆盖新DLL。G2总阶段/C06不在此任务，V4继续生产。

## 审核及最终CI闭环

接收方不得push/改PR/新建MR/merge/deploy或改scheduler/complete.flag。交付可审修复和本地真实全绿后交专属独立reviewer实际审查关键正负例、长路径真实PG和完整门禁证据；reviewer记录哪些自己执行、哪些原件核对。
独立接受后由caller/reviewer在既有最终CI收口授权内核fresh远端头，正常保留所有已发布接受项合入既有feat/v5-post-g1-batch/唯一Draft PR20，完成必要发布与远端CI验证；不force-push、不合并v5、不部署、不重复建PR。若有真实权限/授权依赖，明确唯一阻断，不冒称CI通过。
终态绑定最终code/docs/source/PR merge SHA与push/PR两个run id、真实日志artifact/XML/hash、所有阶段及清理状态。只有两组缺陷关闭、独立接受且最终所需远端CI实际SUCCESS，才可声称“最终CI阻断关闭”。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## 本次执行补充（2026-11-05，Luna6）

- 执行树/分支/初始 HEAD：`E:/majiang-review/g2-final-ci-blockers-luna6-20261105` / `wt/v5-g2-final-ci-blockers-luna6-20261105` / `82ee590490fbde2b6a935812436926ace68e0ff7`；开始时 clean、`git diff --check` 通过。原任务卡 SHA-256 与派发值一致。
- 唯一证据根：`E:/majiang-review/g2-final-ci-blockers-luna6-20261105/build/fedfb0c5ff3548568696d536235c4439`。本次长验证临时根：`D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439`；本 PowerShell 进程 `TEMP/TMP` 已指向该目录，D 盘预检可用空间约 292 GiB。C 盘旧 TEMP 不用于本任务。
- 工具核验：`D:/game_dev_tools/Git/cmd/git.exe` 与 `D:/game_dev_tools/dotnet/dotnet.exe` 存在；固定 PostgreSQL ZIP 默认缓存原本缺失，本任务从生产脚本固定的官方 URL 下载到独占 TEMP，SHA-256=`EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C` 后才用于真实 PG。无自有 PostgreSQL 进程；隔离端口对 `58000/58001`、`58100+` 在启动前复核空闲。
- 已知阻断共 2 项，均为上一轮遗留 MAJOR；本轮尚无新增缺陷。初始远端 push/PR CI 的 FAIL 与旧独立审查 OPEN MAJOR 保持历史原件，不重写。

| 阶段 | 命令/输入 | 进入与成功条件 | 超时/证据 |
|---|---|---|---|
| A 核定/短预检 | `git status --short --branch`、`git rev-parse HEAD`、工具/磁盘/进程与监听检查、读取旧 cleanup 日志；固定分支与初始 HEAD 已核对 | 只读边界正常；最小长路径 fixture 已验证普通 .NET 删除失败、`\\?\` 删除成功（路径 289 字符） | 120 秒；记录于本 GUID build 根 |
| B 两组组件批修 | 小型长路径 filesystem fixture；`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-table-evidence.ps1`；真实 PostgreSQL wrapper 自测 `tools/tests/VerifyV5PostgresCi.Tests.ps1 -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -ZipPath <独占 TEMP 中 hash 验证的 pinned ZIP> -BasePort 58000`；表启动自测同理用独占端口对 | 短路径与最深路径清理、真实 S1/S3 清理、S5 锁占用失败后可清理；XML/seal 自测完整分母零失败；foreign/WhatIf/错误输入拒绝断言保留 | 各组件最长 600 秒；输出/原始 artifacts 留在本 GUID build 根或独占 TEMP，ZIP 不进仓库 |
| C 既有廉价门禁 | 按 workflow 顺序运行对应本地 native/direct/dotnetTest/table 工具入口；不改 workflow、不重跑输入未变的旧绿项 | 每个原生 exit、XML 计数及 hash 结果真实通过 | 每项 600 秒；记录逐条命令、exit 与日志 |
| D 冻结候选后的完整组合 | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunUnifiedVerification -LogDir <本树 artifacts 独占目录> -UnifiedLogDir <本 GUID build 独占目录>` | 完整官方 pinned PG、迁移/恢复/备份/环境恢复/清理和 unified 真实门禁全 PASS；停机后确认无遗留进程/监听 | 1800 秒；阶段无输出每 30 秒 stdout 心跳；超时正常取消并记 UNKNOWN，不强杀 |
| E 交付/复核 | 生成 DELIVERY/BINDING 与稳定 manifest，核 diff/哈希/clean，提交可审候选 | 交付证据绑定代码与文档身份；明确任务 closure 与独立接受分别状态 | 不 push、不改 PR、不 merge/deploy；最终远端 CI 和独立接受留给既有授权流程 |

### 已完成阶段证据

- B1：真实 283 字符 owned-root filesystem fixture；`-WhatIf` exit 0 且 root 存在；正式 cleanup exit 0 且 root 不存在。探针日志在本 GUID TEMP `long-cleanup-whatif.log` 与 `long-cleanup-real.log`。
- B2：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-table-evidence.ps1`，native exit 0，82/82；合法 seal `exit=0 sealOk=True rows=4 self=0 bad=0`，负例 `exit=1 sealOk=False`。原始结果 `artifacts/table-evidence-selftest-5b589b5522dd426bbefa7e5687d9ab56/results.json`，控制台日志在独占 TEMP `table-evidence-selftest.log`。
- B3：`tools/tests/VerifyV5PostgresCi.Tests.ps1 -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -ZipPath D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/postgresql-16.10-1-windows-x64-binaries.zip -BasePort 58000`，native exit 0、193 checks / 0 failures；真实 S1 root 的官方 ZIP 最长 entry 拼接路径 279 字符，S1/S2/S3/S5 清理、foreign-root/listener、锁失败与释放后清理均通过。独占 TEMP 原始目录 `D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/VerifyV5PostgresCi-20261105-151747-551135cd`。
- B4：`tools/tests/VerifyV5TablePostgresStartup.Tests.ps1 -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -ZipPath D:/g2-final-ci-blockers-fedfb0c0ff3548568696d536235c4439/postgresql-16.10-1-windows-x64-binaries.zip -BasePort 58100`，native exit 0，153/153，0 skip/not-run；真实 16.10 生命周期、崩溃恢复和失败码/日志/清理断言通过。证据根 `build/ci-pg-startup-selftest/20261105-072227-a7eaa311`。
- C：按 workflow 实际顺序的前三个廉价门禁均 native exit 0：native-exit 24/24，direct-results 19/19，dotnetTest-results 61/61。结果 `build/fedfb0c5ff3548568696d536235c4439/cheap-gates.json`；table evidence 为 B2。第一次仅汇总脚本自身漏初始化变量导致汇总 exit 1，修正汇总器后原脚本重跑均全绿。
