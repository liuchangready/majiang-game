# V5 进度与 PR21 c600611 双 CI 失败核验

日期：2026-10-28，Asia/Shanghai。G1 APPROVED；G2–G5 未批准；V4 继续生产。

## 基线、归属与占位

已 fetch；origin/v5 仍为 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7。独占工作树 D:/majiang-wt/codex-progress-20261028-2345 从 origin/v5 创建，再 fast-forward 到当前已接受 PR20 头 14f1da3a417d542fdccbf26cd4b77f8f9064a6d8。没有导入 PR21 或 receiver 未提交源码。已读取 AGENTS、迁移方案、全阶段任务书、返修协议、G1 决定及最新进度。主目录原有四项修改/未跟踪文档保留。

全局 bridge 最近100项只调用一次；准备补位前只读磁盘 delegation_threads.json 核对 reservation，三个执行线程未变化、无 waiting_review、Architecture 路径未被占用。

| 路由 | 实际 active / target | 线程 |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | 1 / 2 | thread_20261027_154500_f0b7，P4.1d running |
| Claude Code / glm-5.3-flash | 2 / 1 | thread_20261027_230302_93fb，P5.4b-A R2 reworking；thread_20261028_223624_e4ae，P4.2-B running |
| Anti / configured default | 0 / 1 | mesh exhausted/429，恢复时间 2026-11-01T16:54:27.999Z，即北京时间11月2日00:54 |
| 总计 | 3 / 4 | 保留用户已指定的两个 GLM 在途任务，不改派、不恢复旧暂停任务 |

P4.1c 已由原 caller 独立接受，MR记录指向 Draft PR21 的 c600611；本轮不重复批准。P4.1a/b已有验收与MR。旧 P4.1 failed R36 的义务由 a/b/c/d 承接，不复制旧任务。c/d 的历史用户暂停后有用户 executor picker 改派审计，本轮没有 resume/retry/retarget。

## 本轮新核验：PR21 两个 run 均 FAIL

对象为 [PR21](https://github.com/liuchangready/majiang-game/pull/21) c60061133be2a3350792d90d5c3c70ca2d0fa8a4。原始 API、完整 job log、artifact 和框架日志已下载至本树 ignored E=artifacts/pr21-c600611-ci/，各下载 native exit0。未重跑远端CI，没有把新绿run覆盖原失败。

| run | 实际 checkout | 结果 |
|---|---|---|
| [push 40715805408](https://github.com/liuchangready/majiang-game/actions/runs/40715805408) | c60061133be2a3350792d90d5c3c70ca2d0fa8a4 | FAIL：Architecture 3项；LocalHost 1项 |
| [PR 40715821874](https://github.com/liuchangready/majiang-game/actions/runs/40715821874) | 0b6c48cf2f519fe6f6c9b1b163579ea3e032c97b | FAIL：Architecture 3项；companion gate 两项校验失败 |

GitHub commit API 确认 PR 合成提交双父精确为14f1da3、c600611，tree=2c2c7d460c5cc8ad441342c62ade91c4c3cfbce3。API source head 与实际合成checkout分开记录。push沿用旧source verifier，PR合成树包含当前PR20 verifier，两run的测试范围/执行模式不同，不能横向当同一次重试。

PR 12份 direct XML逐项读数：Ai444、Architecture92（89pass/3fail）、Client182、Client.Wss80、Contracts150、Domain529、Infrastructure42、Lobby89、LocalHost93、TableRuntime413、TableService152、Unity.TableModel140；合计2406/2403pass/3fail/0skip。XML计数另存E/xml-counts.json；不是本机重跑2406项，也不是全证据完整性认证。PR PG full42/42、targeted13/13日志为绿，但统一wrapper失败；不宣称整个PG/批次门禁PASS。

以下是本轮全部已确认阻断类别，均需在完整LocalHost集成前关闭：

1. **D-LH-CI-ARCH（MAJOR，既有集成缺口）**：ProjectDeclarationGraphMatchesTheExactAllowlist、AssemblyReferenceGraphIsWithinTheAllowedClosureAndKeepsRequiredPositiveEdges、ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing 均失败。实际 LocalHost 合同所需 Client/Contracts/Contracts.Wire 项目边，以及 Client/Wire/Google.Protobuf 程序集边尚被旧规则禁止；负例因此混入3条既有诊断。只允许按已接受LocalHost通道合同调整这一模块的精确白名单；保留所有其他边界，旧 LocalHost→Client 的禁用负例应替换成真实禁用边，并增加授权边正例。不能扩大为任意Mahjong或任意第三方依赖。
2. **D-LH-CI-GOLDEN（MAJOR，新远端证据）**：c600611相对PR20修改 visibility-v1.json 内两份V5消费者源码元数据及render摘要，冻结数据样本未变，但当前原始SHA/render与既有pin不符。PR gate明确 HASH_MISMATCH、RENDER_SHA_MISMATCH，原日志记录实际CRLF SHA B388934E339086F9519EA2703B0110A131A224A1EB55F94B4CA7D8D9BCDAA2BE、render e8d8f1a543f0554adeed88383060f49ea0392e56e8e8648a0cb7caab1edce502。应保留/恢复已接受冻结样本，把当前消费者证据放交付报告或独立增量，禁止刷新 verifier pin掩盖改动。归原P4.1c/LocalHost集成owner收口，本轮未改黄金或重开G1。
3. **D-LH-CI-COLD（MAJOR，新远端证据）**：push真实框架日志 ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw 失败，furiten-read worker exit=3、stderr空，位置LocalHostCandidateAcceptanceTests.cs:1233/1360。LocalHost 93项中92pass/1fail；PR direct模式93/93不能证明旧失败原因解决。完整原始框架日志保留，并复制到E/push-localhost-framework.log便于短路径读取。归原P4.1c/LocalHost owner定位冷读worker失败，保留其stdout/marker/恢复诊断；不先判flaky、不盲重试、不得删断言或串行化后声称根因已关。

独立定向探针 E/probe.ps1：只在本树ignored目录复制四份fixture，将visibility替换为c600611冻结Git blob；调用当前真实companion verifier。实际native1，命中上述两项visibility错误，探针本身exit0，E/probe-result.json标记成功复现拒绝。未改受管fixture或verifier。首轮用普通PowerShell递归读取下载目录遇到Windows长路径错误，不计完整核验；后改用显式路径和\\?\文件流读取原始框架日志，索引用长路径API，下载compiler tmp目录明确排除。

## 独立补位准备与依赖

仅准备一项 DeepSeek **D-LH-CI-ARCH精确白名单及正负回归**，新工作树 D:/majiang-wt/localhost-arch-ds-20261028 / wt/v5-localhost-arch-ds-20261028，同样从fetched origin/v5创建再ff至14f1da3，tracked clean。文件范围限 DependencyDirectionTests.cs 和独立交付文档，不碰 LocalHost/runtime/store、黄金、Unity或在途TableService。可在已接受基线用真实项目XML隔离fixture/合成程序集图证明合同，不以完整host尚未接受为由引入在途代码。模型发现默认确认为deepseek/deepseek-v4.1-flash，无须替换/询问模型。报告提交时尚未dispatch，最终ID/是否排队以bridge结果为准；成功后预计DS2/2、GLM2/1、Anti0/1、总4/4，已有GLM超配只保留，不新增GLM。

当前关键路径：P4.1d控制回执闭环及上述LocalHost集成阻断→完整LocalHost接受→原P4.2-B真实装配→P4.5/G2。远端P5.4b-A须完成已发R2四类修复后再推进后续真实大厅/整场对账/G3。P6.1/P6.2等待G2+G3，P7等待G4。Anti不可用不授权其它模型替代；不凭空派满。

181 classified仍未覆盖；历史39970580589根因、D-PG-SUP-02归因及既有测试DLL/冷恢复hash证据限制保留。Android历史FAIL/iOS环境BLOCKED/device与跨物理网NOT_RUN各自保留；本轮没有Unity/IL2CPP/平台/设备执行。调度器Running，complete.flag不存在，未创建。仅本报告进入现有PR20，不合并v5、不更改PR21、不部署。E/evidence-sha256.csv保存非compiler-tmp证据索引；其SHA和文件数见本轮终端输出。本报告不把一次读取的artifact认证为全套PASS。
