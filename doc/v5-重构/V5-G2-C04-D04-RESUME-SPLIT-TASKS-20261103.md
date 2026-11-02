# G2-C04-D04 三绝技专项恢复执行与三任务合同

日期：2026-11-03，Asia/Shanghai。用户明确裁决：“保留原验收标准，合理拆分成多个任务继续执行”。这解除D04原八轮人工停止限制，授权以下新接续任务；不批准旧候选、不降低断言、不删除旧证据，不自动恢复其他人工暂停事项。保留现行20轮复核协议，重现同一缺陷的既有历史必须记录，不能因改名或换任务伪装缺陷首次出现。

## 基线、旧成果与全部缺陷

- 原任务thread_20261101_154558_00a4及其后继已被删除，不能恢复不存在会话或伪造原任务状态。原树E:/majiang-review/g2-c04-player-cheats-ds-20261101-1545、旧证据及git提交只读保留。
- 原候选d83faa3658a32631153a2b0867171817ef282521；旧原R8独立报告E:/majiang-review/scheduled-c04-d04-r8-review-20261101-1918/build/caller-review/REVIEW.txt，SHA256 18FD12054A170A7174A5AD55B8EF4060F3A9869D92703A1A2D42B210A5FB1A44，manifest SHA196AD8E3EC6521681AC83CA26201822A3B634E95D6F474AFE8C860521D126749。
- 必须全文读R8报告及其包含的R7具体缺陷清单；当前0 BLOCKER/3原MAJOR：R1-04原始证据语义绑定，R1-05真实Player第二discard和完整正负流程，R1-06完整包/自有进程/封存。旧工具19/19通过但Strict矩阵19输入中3个错误接受，不能据此宣布工具或GAME通过。
- 原已认可R1-01/02/03和R3-07继续保留。两历史Player run只分别证明mark119+sleeve109/SECOND，以及独立Beam110/SWAP；不能拼成同run119/109/99、完整3笔ledger或六行PASS。
- 当前调用方fresh fetch核定origin/v5=8fd7c6c、PR20=125a0d6d。独立D04规划树E:/majiang-review/g2-c04-d04-resume-plan-20261103从PR20创建，无冲突merge原d83候选，代码头4a7814421cba0b0329b3037810bb14bbf7158e76。保留现有已发布修复及未完成D04成果；这是明确继承的未通过候选，不冒称已接受产品。
- 三接续树从上述共同代码头加本caller文档的冻结提交创建。所有expectedFiles以新任务起点为差异基准，不把继承的17文件旧代码当作本轮已通过。不得改Domain/Client/Contracts/TableActor/LocalTableRuntime/asmdef/D01/D03/UI15/census或机器许可；只读复用现有codec/canonical/recovery能力，额外tracked文件给精确最小请求。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## 固定验收标准（全D04不变）

固定行C04-G3-01..06：落汗成功、袖里藏花成功、偷梁换柱成功、精确费用/私有ledger、136物理守恒、未授权/余额不足两独立真实拒绝。
- 在同一冻结完整包内，authorized同run连续完成五个目标命令：TileMark、SleeveBloom激活、SECOND选择、BeamSwap激活、SWAP选择。费用1/10/10，余额120→119→109→99，3条精确私有ledger，choice不重复扣费。SECOND与SWAP各有精确实体来源/去向，全部0..135恰好一次共136。
- 每个动作绑定原command/raw payload、canonical digest、receipt、精确accepted private publication、相应持久/recovery原件；request/scope/account/role/session/room/seat/round/decision/epoch/version/event/cursor按实际合同分别验证，不误当相等标量，不拿latest后态替代精确提交。
- negative同包执行两个不同合法编码payload、不同request的真实服务拒绝，分别证明未授权和余额不足的准确原因，以及state/event/ledger/实体零副作用；MALFORMED_MESSAGE或INTERNAL_ERROR不能代替业务拒绝。错误码按既有实际合同核定，不硬造期望。
- PlayMode和Windows Player原生exit、逐行计数/XML/log/raw/源码/整包manifest与SHA齐全；无skip/空测试/伪造事件/反射改权威。TOOL_FIXTURE、真实.NET组件、Unity、Player分别标注；历史分包运行不冒充最终同包PASS。

## D04-A：原始证据验证（Luna 6）

模型gpt-6-luna、targetAgent=codex，使用dedicated reviewer和不同sender模型。工作树E:/majiang-review/g2-c04-d04-evidence-luna6-20261103；分支wt/v5-g2-c04-d04-evidence-luna6-20261103。

唯一允许tracked修改（6）：
```text
dotnet/tools/C04PlayerCheatVerifier2/C04PlayerCheatVerifier2.csproj
dotnet/tools/C04PlayerCheatVerifier2/Program.cs
tools/unity/c04-player-cheats-evidence.psm1
tools/tests/C04PlayerCheatEvidence.Tests.ps1
doc/v5-重构/V5-G2-C04-D04-EVIDENCE-CONTRACT-20261103.md
doc/v5-重构/V5-G2-C04-D04-A-EVIDENCE-DELIVERY-20261103.md
```

范围R1-04：实现独立只读typed原件验证器和薄PowerShell调用模块，将旧旁路摘要/任意hash/错误拒绝原因假绿全部关闭。复用现有WireCodec、canonical与read-only recovery工具，不修改共享实现。自测用真实序列化和真实LocalHost生产通道组件原件，不能以手写成功字段代替；该层不能称GAME。

本轮要实现的固定调用合同（新能力，当前尚不存在）：
dotnet <C04PlayerCheatVerifier2.dll> verify --evidence <case-json> --case <authorized|negative> --source-head <sha> --package-manifest <path>
成功native0，缺失/篡改/错误绑定/未知结果非零；输出结构化逐项原因和实际raw事实，不靠日志字符串自声明成功。模块只封装同一CLI，不复制第二套语义判断。在EVIDENCE-CONTRACT文档记录捕获布局、阶段关联、退出码和最小合法/无效样本供B/C消费。

证据布局保持旧case-json元数据兼容，可增加版本明确的新raw sidecar/index，包含原始command/receipt/accepted publication和read-only recovery字节。所有路径必须相对捕获根且拒绝越界/reparse/缺失/换包。缺原件的旧case必须FAIL/BLOCKED，不能因兼容旧记录而回落信摘要。验证物理实体分布、费用/私有ledger和精确SECOND/SWAP归属；任意合法64hex不能替代原字节重算。保留原19项和Strict所有相关拒绝控制，拓展缺原件/错身份/错误原因/相同payload/非零副作用/错包负例。

A独立组件阶段不需要重复Unity。B未产最终Player前，可以提交实际.NET通道正对照及完整工具负例供独立审查；明确整体R1-04最终GAME原件仍待B验证，不声称D04通过。D05已接受隐私verifier可只读参考，不能无论事件语义直接套用为D04PASS。

## D04-C：完整构建、包与进程工具（DSH，优先独立推进）

targetAgent=deepseek_harness，使用其配置默认模型。工作树E:/majiang-review/g2-c04-d04-runner-ds-20261103；分支wt/v5-g2-c04-d04-runner-ds-20261103。

唯一允许tracked修改（6）：
```text
tools/unity/run-p45-c04-player-cheats.ps1
tools/tests/C04PlayerCheats.Tests.ps1
tools/tests/C04PlayerCheatRunner.Tests.ps1
unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs
unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs.meta
doc/v5-重构/V5-G2-C04-D04-C-RUNNER-DELIVERY-20261103.md
```

范围R1-06：先用真实机制短负例批修pinned isolated staging、全源码/生成输入/新鲜DLL闭包、BuildReport步骤Error/Exception归因、完整包manifest、可信SkipBuild、PID/start身份、quoted参数、有界native/timeout/cancel/双流drain/后代writer和正常失败封存。不能只加两个字段、父进程退出便清除未解决writer、封存后继续新增文件、信marker存在或错误summary；不能误杀他人或删除旧证据。

原runner Assert-C04Evidence/Strict的最终语义由A模块/Verifier2统一负责。A未集成时只完成独立机制和组件测试，不保留旧Strict作为正式PASS回退。原参数Case/EvidenceRoot/PlayerPath/SourceHead/BuildCommit/SkipBuild/SelfTest尽量兼容；所有外部调用有有限超时及native非零传播。

BuildReport succeeded但errors=3的历史必须保留并取得真实step日志诊断，处理真实原因，不能滤掉错误/改计数。新构建时完整输入/包身份冻结，复制前后/运行后不变；SkipBuild必须绑定已封存source/build/package，错源/marker/package拒绝。控制失败、资源忙和未知退出分开，不改机器配置/许可/共享生产脚本。

先修所有廉价负例再进行必要一次受控fresh build验证机制；可以提交工具组件+构建证据供独立审查，R1-06最终同包完整游戏证据由B接续，旧已知坏Player不作为反复随机调试。生成插件只在独占staging，源仓保持clean。

## D04-B：真实Player流程、原始采集及最终六行验收（DSH，按空槽接续）

工作树E:/majiang-review/g2-c04-d04-player-ds-20261103；分支wt/v5-g2-c04-d04-player-ds-20261103；只在DSH有空槽且不重复范围时通过bridge派发，不能为预排队突破总3槽。A/C独立工作先行，不要求B实现先等待所有工具。

唯一允许tracked修改（12）：
```text
dotnet/src/Mahjong.LocalHost/LocalPlayerCheatScenario.cs
dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerCheatChannelTests.cs
dotnet/tests/Mahjong.Unity.TableModel.Tests/TableCommandIntentFactoryTests.cs
unity/Mahjong.Unity/Assets/Scripts/Table/TableCommandIntentFactory.cs
unity/Mahjong.Unity/Assets/Scripts/Table/TablePresenter.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerCheatChannelFactory.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerCheatChannelFactory.cs.meta
unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerCheatProbe.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerCheatProbe.cs.meta
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerCheatScenarioTests.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerCheatScenarioTests.cs.meta
doc/v5-重构/V5-G2-C04-D04-B-PLAYER-DELIVERY-20261103.md
```

范围R1-05：在既有真实TablePresenter→intent→TableSession→LocalHost生产链定位SleeveBloom SECOND后的第二discard失败，记录有界UI选择/buttons/pending/admission/submitted intent、对应receipt和精确accepted-publication。修真实原因，不能猜actor、绕授权、写权威、加sleep/任意version等待或伪造失败/成功日志。保留已认可首mark/immediate/SWAP round修复。

更新过时PlayMode的余额110/100、隐藏入口等断言为真实合法119/109/99与完整正负流程。扩充factory/probe的原send/receive采集，在实际channel包装处保留原Welcome/command/receipt/publication及所需recovery，不复制解码器或以摘要当原件。按A文档产物格式交付，必要新增字段先给明确合同并由父代理协调，不抢改A/C文件。

自己的确定性组件/意图/真实通道回归在工具未就绪时继续；最终由父代理将A/C已接受增量及相关D05已接受组件合入冻结候选，再在同一包执行Case all与更新后PlayMode。B保持原线程接续，不另开“最终验证”重复实现；六行未全部实际通过则D04仍未完成，精确记录剩余项。不得把分run历史或组件自测拼成正式PASS。

## 依赖、槽位和原件交接

三任务tracked集合两两交集0。实现可分批推进；最终闭合依赖A+C→B同包六行，不增加阶段人工确认。A/B/C不得改对方树或自行cherry-pick/派任务；父代理/专属reviewer在收到组件后统一集成，receiver交付code和docs-only分开。

D05独立已接受隐私15981ccc、恢复4cde1db7已由调用方合入本地候选dccd7aa9，fresh167/167、483/483、verifier17正67负通过；证据见E:/majiang-review/g2-d05-accepted-components-20261103-d04-resume/build/caller-integration。当前文档头4842dbd6，14项manifest SHA0A5A7F879348CF4F2167AED92329CC814C9E546224992F721379EFCD71B23A98；84自测原件另manifest SHA5066FE737CCE3E1A0D86BAAD6BBA62ED02FB55FA3261302C62D1391FF16388F6。没有取用未审D05 B在途源码，也没有发布。

已结束的D05两个组件实施预约完成，最终矩阵/统一发布交接归并到原D05矩阵thread_20261102_232828_d9f0一次计数；不伪改原bridge MR failed为synced，不发布未审矩阵。现行worker分配：原D05矩阵一个DSH，D04-C一个DSH，D04-A一个Luna，总3；D04-B已准备而未派发，DSH槽空出后按本卡接续，不取消正在修复的D05矩阵。

旧R8报告、manifest、原树、闭合修复和失败原件全部只读保留。完整母报告V5-G2-C04-PLAYER-CHEATS-DELIVERY-20261101.md由调用方最后汇总，三receiver各写自己的唯一A/B/C报告，不同时改母报告。

## 默认分层与资源

先读AGENTS、通用长验证模板和当前20轮协议，再在自己忽略build/<GUID>填写实际命令、成功条件、进入条件、超时/输出/取消与全已知问题。环境短预检120秒、组件/工具默认180秒、必要.NET全套600秒；Unity冷导入参考900秒，Player build1200秒；按实际说明调整，不降断言。最初先确定当前进程许可/磁盘/工具，不将单次batch失败解释为用户没有许可。

E盘独占cache/TEMP/Library/staging/package/evidence；共享锁Local\MahjongV5P45UnityValidation最多等600秒，每30秒心跳。正常取消并确认自有writer停止后稳定封存，不强杀他人、不绕锁、不改机器许可、源文件和旧证据。原生退出未知UNKNOWN；失败封存不伪装正常成功。相关输入未变的有效检查不机械重复；最终同候选同包矩阵必须取得实际新证据。

每任务提供完整ID映射、正负例/命令/native/XML/分母/源与全包身份/日志SHA、首失败、未覆盖、commit和clean/diffcheck；组件与全D04分别判定。阶段在授权内自动继续；真实外部/跨owner依赖才精确BLOCKED，其余工作全部完成，禁止只修第一条就结束。

不再委派、不approve/push/建改PR/merge v5/deploy、改scheduler/complete.flag/旧任务历史/他人会话，不扩大生产规则与程序集依赖。当前用户仅解除D04原停止，其他人工裁决/暂停仍保留；V4生产，D04/C04/G2–G5不自动通过。
