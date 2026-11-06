# V5 定时检查与独立补验

2026-10-29，Asia/Shanghai。G1 APPROVED；G2–G5 未批准；V4 继续生产，181 classified 未覆盖。origin/v5 经 fetch 仍为 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7；PR19 已合并，不再使用旧源分支。

## 台账与并发收口

本轮全局 recent tasks 仅查询一次（100条），mesh 共147条。查询时 DeepSeek 活动1/2：thread_20261029_015017_36bd，持久宿主5f365d1独立复核；GLM0/1，Anti0/1，总1/4。LocalHost审查 thread_20261029_011900_a9fd 最初 waiting_review，复核过程中另一 caller 回合已完成验收并发布 c646b6e417a8d4cead2f8b642c9f3b166dcc0709 到唯一现有 PR20，本轮不重复 approve/MR 登记或发布其报告。它的四项候选缺陷继续开放，a/b/c/d组件接受保留，完整LocalHost仍未接受。

原P4.1 failed父线程已由a/b/c/d承接，不重试该历史指令。P4.2-B thread_20261028_223624_e4ae 保留原GLM路由及待审准备缺陷；真实工厂等待完整LocalHost。持久宿主原owner thread_20261027_230302_93fb 的第3轮返修因429失败，已有D-R2-01/02/03完整清单，不新建或改派实现线程。

模型发现确认DeepSeek默认 deepseek/deepseek-v4.1-flash、Claude Code默认 glm-5.3-flash。mesh实际返回GLM exhausted，恢复时间2026-10-28T19:04:30.782Z（上海10月29日03:04）；Anti默认 gemini-3.8-flash-high，exhausted至2026-11-01T16:54:27.999Z（上海11月2日00:54）。这是bridge限流状态，不是本轮新请求得到429。不用第三个DeepSeek替代缺失席位，不恢复用户手动暂停任务。

## 本轮实际补验

独立工作树 D:/majiang-wt/codex-gate-input-20261029-0220，分支 wt/v5-codex-gate-input-20261029-0220，先从最新origin/v5创建，再ff到accepted PR20头44b1da8，后ff吸收c646b6e。主目录未提交文件、receiver工作树及Unity资源保持原样。

命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/caller-review.ps1`，cwd为上述工作树。脚本从95b47547b535f38a1fc6328445e209e7d402adcc重新git archive到本树ignored独占快照，只加入原复核的两个测试探针；没有修改候选产品源码或借用旧bin。两个工程fresh Release --no-incremental构建均native0、0 warnings/errors。

| 本轮探针 | total / passed / failed / skipped / errors | native exit |
|---|---|---:|
| AcceptedClaimControlPayloadFromContractsMapsToControlRoute | 1 / 0 / 1 / 0 / 0 | 1 |
| DroppedStaleControlRejectionCannotBeReconciledAfterReconnect | 1 / 0 / 1 / 0 / 0 | 1 |

两个XML各有1个真实失败节点，无not-run；分别复现缺少expectedControlEpoch字段1，以及拒绝回包丢失后count=1/lastLookup=NotFound。脚本因预期失败被正确复现而exit0，不表示产品通过。D-LH-GATE-01/02保持MAJOR；D-03冻结visibility字节和D-04版本竞态沿用已接受报告和caller复核，本轮没有再次执行其全量/PG场景。receiver manifest306/306长度与SHA重新计算一致。

本轮证据根 build/caller-evidence/；caller-control-map.xml SHA256=112F19E7DEBECF52A734936A05468792744C5894EF572E3142409C7071A6DC16；caller-control-rejection.xml SHA256=B5C6F4654C727E683C4FD618411127979288854A7E39A7AB07DD135573BC7429。build/caller-evidence-manifest.csv记录脚本、原始构建/测试日志、XML、计数、原manifest复核与候选ZIP，SHA256=29E7B61CE16C26F3D54AA3B9449444EB0F00D7E0A496EB9CF89A491726ABD7D0。没有把历史978项或PG/WSS/Unity结果冒充本轮新跑。

## 下一独立任务与阶段依赖

GitHub实际返回44b1da88ab66bb73a873f41696cc5d0d82210216的push [40739411504](https://github.com/liuchangready/majiang-game/actions/runs/40739411504) 与PR [40739420590](https://github.com/liuchangready/majiang-game/actions/runs/40739420590) 均completed/failure。`gh run view 40739420590 --log-failed`实际exit1、`log not found: 122996661051`；不能推断产品失败、flaky或修复。本轮不重跑远端CI。

准备第二个DeepSeek做这对失败运行的原始artifact/API/原生XML核验，独立于正在运行的持久宿主PG/WSS源码审查。工作树 D:/majiang-wt/ci-44b1-audit-ds-20261029-0220，分支wt/v5-ci-44b1-audit-ds-20261029-0220，同样从origin/v5创建后ff至已接受c646b6e；仅允许一份新审查报告，禁止改CI/产品源码、重跑或push。新任务ID/是否启动以异步dispatch实际结果为准，不在文档预填。准备后分配若成功为DeepSeek2/2、GLM0/1、Anti0/1，总2/4；后两席BLOCKED于上述限流。下发后结束本回合。

LocalHost四项集成修复及完整门禁是P4.2-B生产装配/P4.5/G2前置；持久宿主三项返修及正式接受是剩余HTTPS大厅签票、多客户端整场对账/P5.4b/G3前置。P6等待G2+G3，P7等待G4。设备/跨物理网络/整场/G2–G5本轮NOT_RUN；Android、iOS的既有独立状态不被改写。scheduler实际Running，complete.flag不存在。只把本进度文档发布到既有PR20，不纳入未验收候选、不merge v5、不部署。
