# G2 最终候选 CI 诊断与修复任务（Luna 6，2026-11-04）

用户明确授权通过 bridge 下发 Luna 6 处理最终候选 CI。执行器 codex，配置默认 gpt-6-luna；专属 reviewer 独立验收，接收方不再委派。

任务 ID：G2-FINAL-CI-20261104。
候选 PR20 OPEN/Draft，base v5；源分支 feat/v5-post-g1-batch；已核 source 4a855c3c49e65ec97708784d84a784df9f11c199；origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7。
工作树 E:/majiang-review/g2-final-ci-luna6-20261104，分支 wt/v5-g2-final-ci-luna6-20261104。本卡是 caller docs-only 准备；初始生产与测试源码精确等于4a候选。主仓 D:/majiang 有用户改动，只读保留。

先读 D:/majiang/AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md、D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md（当前为同缺陷20轮升级，不能误用历史3轮）、本卡。使用 staged-validation 技能，模板任务卡填在本树全新忽略 build/<GUID>，记录实际阶段/命令/进入条件/有限超时/取消和30秒心跳。

## 全部当前已知事实

1. 同source的 push run41729487041 SUCCESS；pull_request run41729493387 FAILURE。后者实际checkout为合成merge ebc98716b8588d5ff6af774b04674bc4f3dd0e3f，绝不能与source 4a混为一谈。先比较source/合成merge的相关blob、SDK/runner/环境；不要直接断言是PR差异或随机失败。
2. 原失败artifact已只读下载至本树 build/caller-original-ci-41729493387，artifact id13278262108，v5-verify-logs-41729493387-1。保留原件，后续在新输出根hash/审计。
3. 原 summary在 verify-v5/unified/summary.log：20项目PASS、1FAIL、无BLOCKED/NOT_RUN；companion PASS。唯一工程LocalHost total184/pass183/fail1/skip0/error0/native1。
4. 唯一失败：C04RoomTicketTests.RecoveredOpenHumanDecisionRetiresOnOriginalDeadlineWithoutBinding，C04RoomTicketTests.cs:735。
   Expected local-human-initial，Actual local-human-0-0-CheatWindow。原XML verify-v5/unified/direct-xml/Mahjong.LocalHost.Tests/direct-results.xml，SHA256 3483424346A8DB5F57065A12C4A3A2D0EB9700C61E75DDB12E54AE37738BF8F3；原console direct-Mahjong.LocalHost.Tests.log。该方法720起，AutoPlay=true后开真实room/player，等待任意pendingId然后硬断言固定id。须确认是否在读取前生产driver合法推进、存档/关闭/重开哪个精确状态被持久化，不能仅删除字面量断言就称修复。
5. PostgreSQL16.10启动、Infrastructure42/42、target13/13、crash restart、receipt/outbox恢复与backup/restore此前均PASS；外层因统一验证exit1正确失败。不要修改gate来吞错。
6. 保留已接受D05 PlayMode7/7、普通Windows同包七案/P2、D04原六行、慢消费者application-callback修复；不重开已关闭组件，不把历史6/7视为当前。历史FU02恢复首败原因UNKNOWN、SYNC03外部间歇记录保留，不能用一次绿色宣称修复它们；只在本次真实因果范围内调查。
7. 当前没有活动bridge任务；Android补验将在另一独立树交DSH，修改范围与本任务不交叉。无需争抢Unity。

## 精确允许tracked修改（4）

- dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs
- dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs
- doc/v5-重构/V5-G2-FINAL-CI-DELIVERY-20261104.md
- doc/v5-重构/V5-G2-FINAL-CI-BINDING-20261104.json

优先只修改首个测试文件。第二个只在有原件证明同一恢复同步缺陷相关时使用，不能顺带重开历史FU02。
生产Domain/Contracts/Client/LocalHost/Runtime、workflow、verify-v5脚本与gates、Unity/Android、其他任务文件只读。若证实生产缺陷或需要其他文件，一次给全已知可复现项和精确最小范围请求，由caller处理；不能改生产或降断言迁就范围。

## 目标与验收

已知缺陷组 G2-FINAL-CI-01，MAJOR，0个已证实其他组。触发/实际见上；期望：真实冷恢复仍恢复同一已持久化pending decision及原deadline/grace，deadline前不提前退休、跨deadline后恰当退休，零真人binding不新开真人决策，控制epoch/136/幂等不变。测试自身观察必须与准确受审持久化版本/decision关联，不能把“任意非空ID/任意新快照”当目标，也不能将合法生产推进误判为错。
先构造可控确定性调度与真实room/driver回归（保持生产API），证明原错误可触发，修复后两种时序均正确：观察初始decision之前合法后续开窗、持久化关停/恢复边界；错误decision/早退休/重置deadline等负例必须能失败。禁止sleep重试凑绿、增加超时掩盖错误、关闭工程并行、删除/skip/放宽原恢复合同。

A 先核源码/工作树/空间/.NET8/PG可用与输入身份；只读保存source和PR merge对比。
B fresh build定向正负例；预先固定5次目标及受影响恢复/票据类检查，然后完整LocalHost和必要Client/Runtime；每个run独占输出，原XML逐case/native一致，零fail/error/skip/notrun；不能单案绿抵消全量红。实际缺陷批修，不无限重试。
C 保持gate未变，实际入口与runner合同对齐。只有改过runner才要求其工具负例，本任务未授权改runner，不重复无关已绿selftests。
D 冻结code commit后跑与CI等效真实PG统一入口：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification -LogDir artifacts/<本任务唯一GUID>/table -UnifiedLogDir artifacts/<同GUID>/unified
实际DotnetPath等参数先查当前脚本再写命令；使用已有D:/Unity/dotnet/dotnet.exe或已安装8.x实际可用版本。不要猜参数。短构建/组件每步600秒，统一PG全量1800秒可基于冷cache真实成本写明调整为3600秒；每30秒心跳。仅自有一次性loopback PG，归属验证后正常清理，不碰共享服务；不占用Android串行资源。不绕锁/杀他人进程。
E 报告原始首败、根因实证、修复与拒绝矩阵、source/merge/code/docs身份、完整逐项目计数和命令/native、日志/XML/hash、范围clean、未关闭风险。停止writer再稳定封存。

专属reviewer须独立实际复验关键确定性正负例、必要完整LocalHost/统一结果，区分自己跑测和receiver原件审计，按当前协议一次给全缺陷。本任务执行者不得push/改PR/approve/merge/deploy。

本用户已授权处理最终候选CI；范围独立验收后，由caller/reviewer在保留所有已发布接受项的前提下沿现有同一个Draft PR20统一正常推送本已接受修复并record_merge_request（不新增PR、不force-push、不合并v5/main、不部署）。再核新source SHA的push与pull_request两条CI及真实artifact（合成merge另记）。若新头有兄弟已接受改动，按实际受影响闭包验证，禁止覆盖。远端运行未完成保留NOT_RUN/IN_PROGRESS，异步等待回调/条件变化；禁止持续轮询或重复rerun追绿。最终CI闭合须两条目标新头通过，不能拿旧4a push绿代替新头结果。C05/C06/G2-G5不由本任务自动批准，V4继续生产。


【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。
