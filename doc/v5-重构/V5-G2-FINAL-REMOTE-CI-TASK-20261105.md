# G2 最终头远端 CI 核验与收口（2026-11-05，Asia/Shanghai）

用户明确选择“最终头远端 CI 核验与收口”并指定下发Luna6。公共模型目录已确认codex receiver配置gpt-6-luna、独立reviewer默认gpt-6.1-sol。按此执行，不换模型，不继续委派。
这是一项只读代码/远端与新增本地审核文档的任务，不是重开已接受的CI修复，也不批准G2。

## 身份与范围

- worktree E:/majiang-review/g2-final-remote-ci-luna6-20261105；分支wt/v5-g2-final-remote-ci-luna6-20261105；受审源码固定59e8bd741385b5d7b39ccdbc47573dde552067d1。
- 实际派发头为该受审源码上的本任务卡docs-only提交，见派发消息；任务报告本地docs提交与受审远端head严格区分。
- 2026-11-05刚核GitHub：唯一PR20 OPEN/Draft、base=v5、head branch=feat/v5-post-g1-batch、head=59e8bd741385b5d7b39ccdbc47573dde552067d1；API merge_commit_sha=0794af7f3dd601f4fc75154f92add6b96fcc40f6（暂态字段，不自动认定为job实际checkout）。
- 当前push run41995915276、pull_request run41995924338均in_progress，均API head_sha=59e8bd74；结束状态待实际核定。优先核验这两个已有运行，不新触发重复运行。
- 原修复线程thread_20261105_150733_6eb6已completed、本地独立接受；code75d2782只改PG清理与table证据自测，source/docs头59e8bd74。不要恢复/改写旧线程、修改旧证据，勿冒认原审身份。
- 允许tracked修改仅3个文件：
- doc/v5-重构/V5-G2-FINAL-REMOTE-CI-TASK-20261105.md
- doc/v5-重构/V5-G2-FINAL-REMOTE-CI-REVIEW-20261105.md
- doc/v5-重构/V5-G2-FINAL-REMOTE-CI-BINDING-20261105.json
- 所有业务/工具/workflow/测试/依赖/历史文档只读。原件、API响应、下载包、校验脚本只写本任务忽略目录build/<GUID>；已有文档不覆盖。主仓与Android树完全不动。

## 必须保留的有效证据及历史

修复本地独立接受资料在 E:/majiang-review/g2-final-ci-blockers-luna6-20261105：
- 本地table evidence真实入口82/82；长路径WhatIf/302字符cleanup及真实pinnedPG最长280字符S1/S3清理通过，wrapper193/193、startup153/153；S5锁占用失败/释放后正常清理有效。
- 完整生产table42/42+定向13/13，unified21项目行PASS、14测试工程2872/2872，0fail/error/skip/notrun；2050文件hash核对匹配；自有PG/监听0。
- 独立证据 build/reviewer-g2-d94acc1ff6c4470db58e0e710914fab4/reviewer-verdict.json；对应DELIVERY/BINDING在doc/v5-重构/V5-G2-FINAL-CI-BLOCKERS-*。
- 历史push41971880599/PR41971887843失败已由真实job日志确认缺Test-ManifestPathIncluded；保留FAIL不覆盖。本地通过不替代新头远端通过。
- 控制对账与恢复driver已接受；FU-02 UNKNOWN/SYNC-03未解释继续保留，不声称旧间歇原因被本次修复。Unity/Windows/Android新包绑定与C06均非此任务，V4继续生产。

## 阶段与验收

先读工作树AGENTS.md、D:/majiang/AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md、D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md（同缺陷20轮人工升级）。沿用这张任务卡，在自有build/<GUID>补实际阶段/命令/超时/输出/资源预算和原生退出记录。

A. 只读身份及环境预检
使用现有Git/GitHub认证工具或公共API，只读获取PR、run、attempt、job、steps、workflow ref、source SHA、base与真实checkout SHA；必要检查check-runs/annotations/实际checkout日志。Git可用D:/game_dev_tools/Git/cmd/git.exe。不要把页面上head_sha自动等同PR合成merge实际checkout。记录查询时间Asia/Shanghai与UTC、原API响应与hash，API访问失败如实BLOCKED不猜结果。

B. 有界等待已有run
两个已有run若未完成，以30–60s间隔只读查询实际GitHub状态并输出phase/run/job/elapsed/最新进展，每次网络操作20–30s超时，单次等待/阻塞不超过60s，总等待最多3600s；workflow已给定更短超时时以其为准。不要轮询bridge自身任务，没有变化不反复下载artifact。超过总界限记录PENDING/UNKNOWN/BLOCKED及可验证检查点，不能判PASS；不改workflow超时、不绕门禁、不取消他人run，不为重试而重跑同头。后续有新输入/资源变化再接续。

C. 原始日志与artifact审计
运行完成后取得每个run的完整job日志和artifact（不只看绿色状态），按run id/attempt独占目录下载一次，保留原zip/hash、展开文件清单、日志/native exit/结构化summary/XML/manifest/provenance。下载解包前校验目标绝对路径在本任务证据根内并拒绝zip-slip。使用现有身份权限，不输出令牌或复用不明认证。
按实际workflow逐步核对cheap evidence gates（native/direct/dotnetTest/table）及完整真实PG/统一验证入口是否真实执行、零skip；全局summary与14工程逐例XML/assembly/console/native一致，历史2872为参考分母，实际变动必须解释不能硬编码数量。核对table42/42和13/13、PG crash/backup恢复、cleanup和环境恢复的实际产物与退出；manifest self排除/各项hash按生产规则复算，原生失败不能由绿色XML覆盖。只读检查关键修复确实在实际checkout里，PR合成merge与源头差异有追溯。
每个必需检查/文件缺失均单列FAIL/BLOCKED/NOT_RUN，不能用本地旧证据填补远端缺失，不将“下载成功/CI徽章绿”当内容核验通过。提交或依赖变化导致实际分母改变时说明。
如果未下载到私有/过期artifact，报告精确访问/保留期依赖与可行接续，不用截图或摘要代替原件。

D. 缺陷与闭环
若run失败或出现假绿/证据不一致，给一次性完整可执行缺陷清单：ID/严重性/文件行或step、原样输入命令、actual vs expected、原件hash、最小修复范围和验收断言。只读定位，不越界改源、不削弱gate；将“核验报告完成”与“最终CI收绿”区分。若确有需修生产/工具/workflow的新缺陷，交原实现收口范围，不向只读reviewer反复发修码空转。
若两个最终所需run都SUCCESS且日志artifact验证通过、结束时PR20仍是受审59e8bd74，才能结论FINAL_REMOTE_CI_VERIFIED。若受审期间PR头更新，记录原头结果与CHECKPOINT_SUPERSEDED及新头/run的精确接续需求，不能用旧头认证新头，不自行无限追逐移动候选。不要自动rerun/dispatch/push来制造新结果。

E. 封存与独立审查
停止所有本任务writer，校验原件后写REVIEW/BINDING与稳定manifest；允许提交这三份新文档到本地任务分支，生产源码无diff，不push或改PR（文档推送也会改变被审final head）；无需本地全量build/Unity/设备/真实PG重跑。
报告每个run id/attempt/event/source/base/merge/checkout/workflow/artifact身份、全部检查真实计数和native、下载文件hash、缺口、终态时间、最终PR head，及自己实际执行与仅审原件的边界。
专属独立reviewer实际审查关键原件、hash/计数矩阵与结论，不把接收方原件审计改称独立重跑CI。任务报告可以被接受，但有任何实际缺口时最终CI不能判已通过。
不merge v5、不部署、不新建PR/改scheduler/complete.flag、不改许可或机器配置、不动其他任务；此任务即使通过也不构成G2/C06或平台验收批准。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；本任务是只读审计，不允许以此条款扩大修码范围。
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## 执行记录（2026-11-05，Asia/Shanghai）

- 本地独占原件与审计证据根：`build/e98c7d38-23e1-4c58-b662-410fb80199a8`。任务卡原始 SHA-256：`04AD64D6320A821EB2EFB6300AB7F722134C27B1EDEA3E720E0AB745DBBABD33`。
- A：本地派发 HEAD/branch/clean 状态已记录；GitHub REST 初始读取 PR20、run、jobs、workflow id/path 均成功。最后成功的 PR live response 为 2026-11-05 09:13:05 UTC（17:13:05 +08）：OPEN/Draft，head `59e8bd74…`，base `8fd7c6cb…`，merge field `0794af7f…`。另有两条 run 完成后的 API snapshots。
- B：只读轮询已有 run `41995915276` 与 `41995924338`，45 秒节奏，单次网络超时 25 秒，总轮询 411 秒；未 dispatch/rerun/cancel。push 于 09:00:08 UTC SUCCESS，PR 于 08:59:25 UTC SUCCESS，attempt 均为 1。
- C：按 run/attempt 各下载一次完整 job-log ZIP 与 artifact ZIP。原始 ZIP、API artifact metadata digest、文件清单均保留；每包 625 项。初次匿名下载 403/401 后使用当前已配置的 Git Credential Manager 在内存中访问；未输出/落盘 token。普通路径展开遇 Windows MAX_PATH 后，仅在 build 证据根以短目录/extended path完成展开；该 partial 目录未用于结论。
- D：独立解析 14 个 direct XML/console/meta/native exit；复算两个 run 的 222 项 table manifest 与 12 项 PG evidence hash；核对 42/42、13/13、PG crash/backup restore、cleanup/env restore。运行 `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build/e98c7d38-23e1-4c58-b662-410fb80199a8/audit-evidence.ps1`，exit 0。未运行本地 build 或重新测试。
- E：已形成 [复核报告](V5-G2-FINAL-REMOTE-CI-REVIEW-20261105.md) 与 binding JSON，源码/工具/workflow/test/Android 无改动。最后一次 09:26 UTC 的 live PR 读取因 TLS/连接关闭失败，`git ls-remote` 也以 TLS unexpected EOF 失败；current head 之后状态记 UNKNOWN/BLOCKED。独立 reviewer 未运行，原因是用户明令不得继续委派，未冒称独立审查。
- 本轮未发现新的 CI 代码缺陷；远端 run/artifact 事实通过，但任务整体不记 `FINAL_REMOTE_CI_VERIFIED`。历史失败、FU-02 UNKNOWN、SYNC-03 与平台边界继续保留。