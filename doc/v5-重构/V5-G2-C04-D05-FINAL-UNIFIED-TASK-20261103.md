# G2-C04-D05-FINAL：最终统一候选验证（Luna 6）

日期：2026-11-03，Asia/Shanghai。用户明确下发：D05最终统一候选验证，先组件预检，再同版本、同包Unity/Windows隐私、控制和恢复矩阵，执行者Luna 6。当前调用方/bridge专属reviewer独立验收。

## 任务身份与授权边界

- 执行器codex，用户指定gpt-6-luna；已核receiver默认gpt-6-luna、sender reviewer gpt-6.1-sol，走dedicated reviewer，不改变模型配置。
- 隔离树E:/majiang-review/g2-c04-d05-final-unified-luna6-20261103；分支wt/v5-g2-c04-d05-final-unified-luna6-20261103。
- fresh fetch/ls-remote/PR核定起始候选 **2c8238cd29386ab52994c8e3839dfdb85059660c**，现有唯一OPEN/Draft PR20，base v5；accepted origin/v5仍8fd7c6cb701dfbb41c28938de4186749bdd1d6c7，祖先检查通过。此树从候选直接创建，caller先仅加本任务卡，再交付clean起点。
- 派发前全局ledger225项，活动任务0；此任务使用Luna一个槽，不重复旧已完成D05-B9、隐私、恢复或控制修复线程。旧任务只读保留，不回写状态、不删除或假恢复。
- 本任务是统一候选最终验证及必要最小验证胶水，不重做已接受业务实现。禁止生产Domain/Client/Contracts/LocalHost authority/TableActor/存档schema/asmdef、D03/D04工具或任何机器配置变更。发现真实生产缺陷，先保留最小确定性复现和需要的精确路径，再交reviewer/caller处理，不能暗扩scope、吞失败或盲全量重试。
- 原有Windows模式只跑StandaloneWindows64 Test Player，和另建Mahjong.exe不是同一二进制，不能继续以两包拼接认定P1/P2。为实际满足本次同包合同，允许以下名单内新增opt-in C04PrivacyPlayerDriver及runner/build适配；它只驱动现有AppComposition/ITableChannel/TableSession真实生产入口，未显式命令行启用时不得创建host/目录/线程或改变正常产品流程。不是重新实现业务规则，也不因这一已授权适配停在“缺入口”。
- 唯一允许的tracked变更（11路径；不是要求每个都改）：
```text
tools/unity/run-p45-c04-privacy-matrix.ps1
tools/tests/C04PrivacyRunner.Tests.ps1
tools/tests/C04PrivacyPlayer.Tests.ps1
unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs.meta
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs
dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs
doc/v5-重构/V5-G2-C04-D05-FINAL-UNIFIED-DELIVERY-20261103.md
```
新.meta独立GUID，既有GUID/依赖/项目注册不改。忽略的独占build/<GUID>阶段卡/脚本/证据可写。额外tracked文件先给最小具体原因，独立可做部分继续。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## 先读的合同与保留成果

全文读AGENTS、doc/LONG_VALIDATION_TASK_TEMPLATE.md、doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md（当前同一缺陷20轮规则）、V5-G2-C04-D05-CHANNEL-BOUNDARY-20261102.md特别是§4/§6、原IMPLEMENTATION-TASK-20261102.md，以及已有PRIVACY-DELIVERY、PRIVACY-VERIFIER-DELIVERY、RECOVERY-LIFECYCLE-DELIVERY、CONTROL-PROJECTION-DELIVERY、CI-CONTROL-RECONCILE-DELIVERY。旧任务卡“D04人工停止”已由用户后续明确解除，但本任务不改D04；所有原固定分母和验收强度保留。

已接受的输入：
- D05隐私verifier/probe15981ccc：真实wire解码和raw归属，self-test17正例67负例，80 child双流EOF。只读复用，不能通过放宽verifier使Windows数据变绿。
- D05恢复4cde1db7：release/claim/controller/deadline/room生命周期组件通过。
- D05矩阵B9487d6c7a/251a4680：旧round/decision/epoch精确reason、Rejected无提交元数据、单次发送、无pending合同通过；最终Unity/Windows曾6/7，不将旧false-positive 7/7恢复为通过。
- 控制投影207578f0及当前CI控制对账修复c9125edd已集成到本候选。组件范围已通过，不重开旧缺陷；必须确认新统一应用矩阵真的展示最新control epoch/owner/Ready和正确缓冲隔离。
- 最新集成组件记录：LocalHost179/179、TableModel236/236、Client182/182；之前LocalHost178/178和固定目标5/5、LocalControl23/23只绑定各自受测提交。旧C04首败与控制回归CI首败仍保留，不能把一次后来全绿称潜在测试同步缺口已修。
- D03四动作与D04三绝技六行已按原各自候选接受，不重复开发；与本轮控制/共享DLL改变相关的影响回归按实际闭包说明，不将历史包强绑到新源码。
- 起始候选双CI41593580643/41593574031查询时IN_PROGRESS，不能说已绿。记录其最终状态和实际checkout与head；不无变化轮询CI、不为等待CI停止独立预检，也不拿旧CI认证新harness提交。

## 固定矩阵与真实断言

维持 **N1–N8、U1–U3、P1–P2、C04-G5-01..03** 全分母，逐行映射到原合同实际证据。当前定向PlayMode至少包含既有七案，不能删改名躲失败、用Total0或filter失配通过：
1. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions
2. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding
3. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate
4. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator
5. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly
6. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose
7. Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw

关键证明：
1. 同room四真实玩家和独立spectator，单actor/lease，角色/seat/account/session/scope/binding/sync/cursor真实独立；默认seat0+AI、viewer关闭不销毁room和其他viewer保持正确。
2. 真实非空闭牌、合法袖牌、私有费用/ledger按owner+audience+stateVersion+当前可见性校验；公流白名单，无foreign未授权秘密/seed/完整ordered wall；合法弃牌/reveal不误拒，不能靠相同标量或空字段造绿。
3. player→spectator→new player新baseline与缓存清空，旧真实raw经实际receive注入后被binding/session/epoch/role/generation拒绝；不能直接调用view callback冒充wire接收。
4. 控制release→AI、claim→human、无变化claim的epoch与实际SnapshotChanged/Ready有界可见；旧决策/epoch按精确原因Rejected且无提交metadata/零副作用。已开deadline不因真人detach/冷恢复无限延迟，release后仅spectator绑定AI仍能推进；原Ready/cursor/state/event floors及幂等保持。
5. 慢consumer真实有界饱和/独立cursor、取消/关闭隔离、默认本地owner释放lease等按N/U合同覆盖；136实体始终闭合。
6. P2以真实未知结果请求为起点，记录原scope/request/canonical payload与持久结果；Windows同一包退出、重启，使用原outbox/存档恢复后query/reconcile，证明恰好一次副作用和原receipt，不新request偷偷重做。保留进程前后PID/start、运行阶段与包身份；不能把纯.NET或仅存档相等当Player恢复通过。

## 同源码、同包与最小适配合同

- PlayMode与Windows平台不同，不能宣称同一个可执行文件。要求PlayMode绑定最终冻结源码/Unity输入/新鲜共享DLL闭包，Windows的隐私/控制/恢复场景全部使用同一完整不可变包；比较八个共享DLL、场景/配置及manifest，不只比exe。
- 普通Mahjong.exe的P1/P2是真实Windows主验收产物；Unity生成的Test Player只能单列补充证据，绝不改名成普通Player或混用两个包计数。
- 若已有runtime入口足够就复用；否则在授权C04PrivacyPlayerDriver.cs中新增仅显式CLI启用的驱动，支持七案的等价真实生产流程和恢复phase，runner一构建多场景运行，不能每案重新编译不同包。全部动作仍走真实AppComposition/room/channel/TableSession；不写领域状态/伪receipt/事件/缓存/反射权威、不用scripted替身代替目标行为。必要受控延迟/丢回执/旧帧注入包装仅作故障与隔离验证，必须保留真实原件及明确标签。
- 批量修好胶水与组件负例后冻结最终代码head及完整输入closure；docs-only交付提交与被测代码head分开。整个包before/after逐文件长度/SHA一致，P2重启仍运行此包，产物与外部save/outbox/evidence分开。
- 任何实现/输入改变都记录影响与重验闭包；不能把2c8238cd旧CI或旧测试写成新driver通过。真正业务bug若超出名单，保留FAIL/BLOCKED及最小复现请求，独立验证继续，不越界扩业务。

## 分层执行表（先填具体命令再执行）

| 阶段 | 工作与进入条件 | 成功断言/边界 |
| --- | --- | --- |
| A环境与身份短预检 | HEAD/clean/工具空间/当前进程Unity许可/锁、现有API与producer/verifier契约 | 单项默认120秒，许可查询与冷导入分开；不改许可/机器配置；先写独占阶段卡 |
| B组件与最小胶水 | 必要真实场景driver、捕获/runner适配；真实生产组件/控制Ready与原件合同回归 | .NET用正常fresh bin闭包和实际xUnit DLL，不能只build；LocalHost/Client/TableRuntime/TableModel按最终diff影响执行，实际总数/零skip；默认每套600秒 |
| C工具对齐与负例 | 使用接受的真实WireCodec/verifier；同包runner/参数/manifest、XML精确七名、非零/取消/超时/封存 | 真实合法raw正控和错误owner/audience/binding/scope/version/未知字段/坏protobuf/哈希被拒；组件/工具默认180秒，所有已知相关便宜缺陷先修 |
| D冻结与真实验证 | B/C过后冻结源码、inputs、DLL、包；跑七案PlayMode，fresh普通Windows包一构建多场景/恢复重启 | 不删分母；每案有限timeout，原native/XML/逐行状态/raw足够独立验；工具夹具不算GAME |
| E交付与复核 | 证实自有writer停止，稳定summary/manifest，不再改封存文件 | 完整行表、全部首次失败、commit/clean/diffcheck；receiver不自行approve，专属reviewer独立复核 |

示例起点是现有runner的-PreflightOnly、-ExpectedSourceHead、-BuildPlayer和实际.NET构建/runner；先核对真实参数能力，不把拟议新Windows-driver参数当已实现。接收方必须在自己的阶段卡写最终原样命令和调整后的有限超时，不能只复述计划。

共享锁 **Local\MahjongV5P45UnityValidation**，最多等待600秒，每30秒报告心跳；Unity冷导入参考900秒、build1200秒、单Player案参考180秒（恢复可按phase明确调整）。输出/caches/TEMP/NuGet/UPM/Library/短staging/player全部E盘自己的build/<GUID>，不写别人目录或旧证据；A/C预检原生未知UNKNOWN。正常取消、只处置身份可验证的自有进程/后代，writer全部退出后释放锁/映射、收敛双流与封存，不能强杀他人、绕锁或以日志沉默推断挂死。

阶段通过在本授权内自动继续，不新增逐阶段人工等待。失败先归因并做组件/工具最小正负例，只有修因/相关输入或资源变化后再试；不拿完整Unity构建逐个发现小字段问题。相关输入未变且合同允许的有效检查不机械重复，但最终同版本同包要求不能省。

## 交付、边界与后续

唯一交付报告V5-G2-C04-D05-FINAL-UNIFIED-DELIVERY-20261103.md，保留原所有任务历史，不改旧报告结论以掩盖失败。必须有：
- 起始/最终source/build/docs-only身份、完整inputs和包清单、8DLL对照、环境/Unity版本及原CI状态；
- N1–N8/U1–U3/P1–P2/G5三行逐项PASS/FAIL/BLOCKED/NOT_RUN，七案真实名称/计数和Windows一包映射；
- 原command/receipt/publication/recovery/outbox/raw解码、控制/隐私/epoch/cursor/owner/version、恢复前后恰好一次效果；
- 原样命令、native exit/超时/进程身份、XML/log/首失败与稳定manifest/SHA；工具fixture/真实.NET/Editor/普通Windows/Test Player分列；
- 未覆盖平台、未解决新缺陷、代码提交及clean/diffcheck。生成物不入Git。

本任务不自动放行整个C04/G2或Android/C05/C06，V4继续生产。执行者不得再委派、approve、push、建改PR、merge v5/main、部署、改scheduler/complete.flag、旧bridge状态或他人会话。最终专属reviewer/调用方按原合同验收并决定已有PR20的统一交接。
