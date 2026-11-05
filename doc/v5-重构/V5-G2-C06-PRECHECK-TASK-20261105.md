# G2-C06 验收证据预审与缺口整理（2026-11-05，Asia/Shanghai）

用户本轮明确要求下发给Luna6。执行codex，configuredModel=gpt-6-luna；专属独立reviewer配置gpt-6.1-sol。不继续委派、不切换模型。
本任务可在Windows/Android原线程继续期间完成：交付正确、可复核的预审与缺口记录即可接受本任务，不等依赖全结束、不批准G2。完成态必须区分PRE_REVIEW_COMPLETE与G2_NOT_ACCEPTED。

## 身份、范围与依据

- 新worktree E:/majiang-review/g2-c06-precheck-luna6-20261105，分支wt/v5-g2-c06-precheck-luna6-20261105；业务/工具基线59e8bd741385b5d7b39ccdbc47573dde552067d1。本卡在其上docs-only提交，派发消息给出实际HEAD。来源提交、任务文档提交、运行包身份分别记录，不把docs-only变更伪造成业务变更。
- 只允许tracked修改4文件：
- doc/v5-重构/V5-G2-C06-PRECHECK-TASK-20261105.md
- doc/v5-重构/V5-2-REVIEW.md
- doc/v5-重构/V5-G2-C06-EVIDENCE-INDEX-20261105.json
- doc/v5-重构/V5-G2-C06-GAPS-20261105.md
- 业务代码、已有测试/runner/verifier、Unity/Android资源/配置、历史文档/签署、其他工作树与原始证据全部只读。本树ignored build/<GUID>可写原始查询响应、最小核对脚本、稳定snapshot及manifest；不改源、不build、不启动Unity/Player/PG/模拟器，不争抢共享Unity锁，不做UI输入或系统/许可证/驱动变更。
- 读取本树与D:/majiang/AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md及当前V5-REVIEW-REWORK-PROTOCOL.md（同缺陷20轮）。重点依据本树V5-0-1-IMPLEMENTATION-TASKS.md的P4.1–P4.5/G2及相应最新接受记录，不用陈旧文档头/旧AGENTS生产描述覆盖后续已接受事实。
- D:/majiang/doc/v5-重构/V5-G2-CLOSEOUT-TASKS-20261031.md是主仓未跟踪历史清单，更新停在11-03且与后续接受不同步；只读保存引用/来源hash，不能修改主仓。新GAPS文档作为当前收尾清单，明确替代其当前状态视图并保留旧单历史，勿复制过时待办为新阻断。
- 无重复C06线程。Windows thread_20261105_180011_21e1当前R3 reworking，树E:/majiang-review/g2-win-rebind-codex-20261105；Android thread_20261104_181451_ad90当前R8 reworking，树D:/majiang/build/bridge-dispatch-20261104/android。仅标其在途范围/原审最新缺口，不冒认当前未接受代码或未封存产物已通过，也不向其原reviewer代发approve/rework或另派重复任务。
- 原Gemini Windows thread_20261105_175234_6532顶层completed但唯一执行轮paused/duration0，无实际Windows接受成果；不能因任务completed算平台PASS。其后继Codex任务才是当前Windowsowner。

## 必须解决的证据一致性问题

1. 建立P4.1–P4.5与G2门槛的完整映射：每条要求给稳定ID、明确断言、实现/历史接受来源、源码commit、真实构建/包/8DLL/资源闭包、运行命令与native/逐例计数、原件路径/hash、独立复核身份/实际动作、适用平台和状态。标明历史候选通过与当前59e最终包覆盖差异，不将旧包重标当前通过。
2. 已接受C01/C02/C03/D03/D04/D05保持，原12round真实整场、四动作、三绝技六行、D05 PlayMode7/7与Windows同包7案/P2均保留各自真实版本。追读接受追加段及旧失败处置，避免只读报告开头旧NOT_ACCEPTED就把已关闭实现重新列回返修。
3. 当前LocalHost变更6850fcc以及PG/tool75d2782均已接受；本地21项目行PASS、14测试工程2872/2872、table42/42+13/13、真实PG恢复/清理证据有独立原件。区分project/helper行、真实测试节点、skip/error/notrun及预期crash进程非0，不用汇总数字凑分母。
4. UI模块与门面依赖、LOCAL不触网整场/假线上结算资产入口拒绝、四席/观战公私可见性与切换清理、存档恢复/原请求对账/幂等/deadline/controlEpoch/136唯一实体，用现有结构化测试/真实应用/静态引用证据分别支撑。可只读rg/解析asmdef与项目引用检查依赖；静态检查不叫运行验证，运行通过也不自动证明结构边界。
5. 平台矩阵必须列桌面Editor、ordinary Windows、Android完整APK与实际设备/模拟器、iOS真实覆盖；黑帧/exit0/菜单装饰桌/工具fixture不等于应用视觉或合法输入。Windows最终包当前尚未接受；Android最近仍有许可/编排/假绿与证据缺口，E1等历史环境通过不能让B1/R1/V1/I1/L1全行变PASS。以稳定原审清单记录截至时间，不复核在途worktree live写入结果抢走原审归属。
6. iOS、真实远端WSS/HTTPS/PG整场、弱网/发布/回滚属于后续G3–G5或明确平台边界，不整套添加为G2前置。G2仅本地体验、V4仍可回退生产，C06预审不能批准阶段切换。
7. 历史FU-02 UNKNOWN/SYNC-03间歇风险保留，区分未解释历史风险、当前可复现缺陷与平台/证据缺口，不声称已修根因，也不无变化重开已接受实现。
8. 每条缺口给严重性、实际原因/证据、当前owner/thread、明确最小退出条件、可并行/必须等待依赖及待最终签署项；同类合并，不人为拆任务凑数。已在Windows/Android原任务的缺陷挂原owner，不派重复代码/构建/验证任务。

## 远端CI补记与证据来源

- 原审计thread_20261105_165108_49fe已completed，仅预审报告接受：两既有run41995915276(push)/41995924338(PR)均SUCCESS，各14工程2872/2872、table42/42+13/13，109封存文件hash、独立4782条ZIP断言、222项table manifest与12项PG哈希均核过。实际PR checkout合成merge0794af7f3dd601f4fc75154f92add6b96fcc40f6，不能拿API head_sha直接当checkout。
- 原审计当时因末尾fresh PR head查询TLS失败，文档保留BLOCKED；后续独立接受记录在该树8cbc33a8追加，不能只读旧正文的reviewer NOT_RUN当最终事实。
- caller本轮已经成功查询GitHub API PR20并Git ls-remote native0：OPEN/Draft、base=v5、branch=feat/v5-post-g1-batch、head=59e8bd741385b5d7b39ccdbc47573dde552067d1、merge_commit_sha=0794af7f3dd601f4fc75154f92add6b96fcc40f6；origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7。原命令结果在本任务build/caller-preflight-20261105/query-results.json，只读引用，新增末尾身份补记，不改旧审计封存/原报告或声称历史TLS没发生。不重复跑CI或下载未变artifact，查询时间以后不外推。
- 原始只读源：
  E:/majiang-review/g2-final-remote-ci-luna6-20261105/doc/v5-重构/V5-G2-FINAL-REMOTE-CI-* 与其build/e98c7d38-23e1-4c58-b662-410fb80199a8、build/reviewer-8a68c18e/；
  E:/majiang-review/g2-final-ci-blockers-luna6-20261105/doc/v5-重构/V5-G2-FINAL-CI-BLOCKERS-* 与build/reviewer-g2-d94acc1ff6c4470db58e0e710914fab4/；
  E:/majiang-review/g2-final-ci-green-luna6-20261105/中的DELIVERY/BINDING、build/117ec7ac36b744e9ae5773d44ec4f658与build/aab9cd5f6f2d4026bbd3086172e8153e；
  本候选已提交的C01/C02/C03/D03/D04/D05接受文档及其引用的稳定build原件，D05独立R7接受段在V5-G2-C04-D05-FINAL-UNIFIED-DELIVERY-20261103.md:917。
- 文档/桥接摘要仅作为索引，所有PASS必须有合适来源与独立接受范围。已接受完整审计可核其manifest/身份再引用，无需机械复算每个巨大artifact；引用范围/复核方式必须如实。原件不存在或hash不符准确BLOCKED/UNKNOWN，不虚构文件/数字或取live变化文件补齐。对在途产物只记状态与截至时间；确需snapshot则只读稳定复制/双读hash校验并明确未接受性质，不动其writer。

## 交付与有限执行合同

- V5-2-REVIEW.md明确标题“C06预审草稿/待最终平台证据”，当前G2_NOT_ACCEPTED；列门槛证据表、平台矩阵、已接受范围、残余风险与正式裁决进入条件。不得写“G2通过/已完成迁移”或制造最终签署。
- INDEX JSON为可解析的简明证据映射，引用真实路径/source/hash/原生计数/状态/独立接受范围与适用候选；与REVIEW和GAPS的ID、数量和结论一致，不做新调度器或泛化schema框架。
- GAPS文档为当前优先清单，明确原owner与依赖，保留已关闭成果及最终包未重绑的差异；可接受的预审结论不受其他线程未完成阻塞，不无限等回调或持续轮询。
- 在build/<GUID>填写实际只读阶段卡；先短预检(120s)、合同与接受索引(有界约600s)、原件/结构核对(单段600s，需更多先记录理由)、文档交叉校验与封存(300s)。hash大文件/扫描时每30s stdout进度；单次等待不超30s，不因只读任务启动全量构建或设备。资源不可访问记录最小缺口并完成独立可做项。
- 只做必要JSON parse、证据ID/链接路径/hash/计数、diff--check/范围一致性检查，不引入测试框架。最终本地提交4文档、工作树clean，停止自有writer后生成稳定manifest，不再改封存文件。专属reviewer独立核对关键原件、映射和边界，可接受准确的PRE_REVIEW_COMPLETE而G2仍NOT_ACCEPTED。
- 不push/改PR/new MR/merge/deploy/改scheduler/complete.flag、不恢复/改派其他线程或改其身份；无需把这份草稿推到受审final source制造新CI。后续平台证据到齐后的正式C06裁决另按实际授权处理。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；此任务只允许文档与只读核验，范围外代码缺陷给完整清单及原owner，不以此暗扩scope。
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。
