# V5 G2-C04-D03 Player 动作确认返修交付（2026-11-01）

> **本轮（R3）裁决数据优先。** 下方既有 R1/R2 记录保留为历史原始结果；与其冲突时，以本节和“R3 执行结果”中的最终 fresh 运行及冻结候选为准。完整清单仍为 4 组（2 BLOCKER / 2 MAJOR），均是调用方首次独立审查发现；本轮未新增缺陷 ID。R3 首次 fresh 运行暴露的投影根因归入 D03-R1-01：此前 Actor 版本拒绝先终止订阅，修复后才可发现初态 domain history 与投影不一致。

## R3 完整缺陷与修复映射

### D03-R1-01 — BLOCKER：初始权威版本和初态投影不能与预置合法 round 一致

- **位置：** `LocalSnapshotFacts.CreateInitialCheckpoint/Capture`、`LocalPlayerActionScenario.BuildCallWindow`、`TableActorOptions`、`TableActor`、`LocalTableRuntime.CreateActor`。
- **复现：** 调用方 R2 `build/caller-review/run-tests.ps1`，新建固定 Pon/Chi 场景并通过真实 `TableSession` 订阅、提交；R2 复现 `STALE_STATE_VERSION: The expected state version is not current`。本树 R3 首次复跑同命令后，版本层修复使其继续执行，又复现 `TableProjectionException: Seat 0 discard facts do not match the committed river`。
- **实际 vs 期望：** checkpoint/context 为 round version 13、真实初始 EventSeq 0，但 Actor fresh state version 为 0；修复该错位后，初态 projection 又只读空的 runtime `TableEventRecord` 日志，遗漏 round 中已经真实接受的固定前置 discards。期望四个固定 case 以同一 checkpoint/context/Actor baseline 真实订阅；projection 直接读 round 的真实 Domain event history，不合成 wire event 或 receipt；普通默认启动仍为 0，恢复继续安装经验证 baseline，event high-water 从真实提交记录连续推进。
- **返修前证据：** R2 caller `localhost.xml` SHA-256 `278964D691EF5BF64D90292300FAAA8953E82BFE93429F0A49A3CC6594DA8445`；R3 首次实际 XML 与摘要见 `build/<R3-first-run-GUID>/dotnet-results/LocalHost.xml`、`failure.json`，R3 执行结果中记录各自 SHA-256。
- **修复要求/映射：** 在 `TableActorOptions.InitialStateVersion` 显式配置，默认 `StateVersion(0)`；fresh LocalHost Actor 从同一个真实 `checkpoint.StateVersion` 初始化；恢复流程仍由恢复 baseline 覆盖/核验，不放宽 stale guard。`LocalSnapshotFacts` 的 discard/private history 从 `RoundStateMachine.Events` 投影，包含被鸣牌占用的历史河记录；不把 Domain events 转成伪造 `TableEventRecord`/协议事件。
- **统一验收断言：** Pon/Chi/Riichi/Closed Kan 真实 channel 订阅 baseline=13，首个 Player command 使 stateVersion 恰好 +1；EventSeq 初始仍为 0，后续真实事件连续；Actor 普通 0 启动、cold recovery、过期 expected version 拒绝及 LocalHost/TableRuntime 全集无回归；四个固定 case 目标动作和后态断言通过。

### D03-R1-02 — BLOCKER：Unity App/PlayMode 源码不符合冻结合同，不能编译

- **位置：** `C04PlayerActionProbe.cs`、`LocalTableChannelFactory.cs`、`C04PlayerActionScenarioTests.cs`。
- **复现：** 调用方 `compile-app.ps1`，使用 accepted Unity App RSP/reference closure 与 fresh 本树 LocalHost/Contracts/Client 输出执行独立 Roslyn 诊断。
- **实际 vs 期望：** R2 原始诊断 17 项：缺 `System.Threading`；`ObservedVisibleEventBatch` 不含 `Cursor`；`PlayerOwnState` 不含 `Melds`；`LocalPlayerActionScenario` 缺 `Mahjong.LocalHost` import。真实 API 游标在 `SessionViewState.Cursor`、meld 在 `PlayerTableViewState.OwnMelds`。期望当前 Unity App 合同零错误，不加 Domain asmdef/reference。
- **返修前证据：** `app-csc.stdout.log` SHA-256 `117F5E04D402A72313652B0B811F0AF7243395CD04C71FDA18949587D49ACACF`；调用方完整 REVIEW SHA-256 `4DB7656FB0E1697B208AA229E0930FECED8D078821DFCDB621EADF9A5ECC6A8B`。
- **修复要求/映射：** 补线程与 LocalHost namespace；结构 cursor 从同一 `SessionViewState` 捕获；meld 从 `OwnMelds` 捕获；PlayMode 同步修正；保留 R2 已修的 void facade、Task/UI.Session、nullable epoch、Payload.TileIds 和 coroutine 返回路径。
- **统一验收断言：** fresh shared DLL closure 后，实际 Unity PlayMode 编译成功；精确 Pon/Chi/Riichi/Closed Kan 四个唯一用例全 Passed、0 skip；随后 fresh Windows Player 四 case。

### D03-R1-03 — MAJOR：Player 摘要未由不可缺失的原始事实及动作效果支持

- **位置：** `C04PlayerActionProbe` 成功分支、`Test-C04CaseSummary` 与工具负例。
- **复现：** 调用方 `probe-current.ps1`；R2 `current-probes.json` SHA-256 `8FA97FA145DDE7CAF19312CEEE17A86BDBCC9D7C5BDFC01D1BDB4659561CAC0D`。
- **实际 vs 期望：** 既有 gate 接受 Pon/Chi/Kan meld tile 仍留手、缺 hand after-state、Riichi 供托 +2、binding/eventBinding 同步改 999、owner/eventOwner 同步改 999；`player-action.jsonl` 只有路径无记录，摘要自产 hash 无独立语义基线。期望保存实际 canonical submit、原始 Applied resolution、observer 捕获的精确增量 batch/event、同 acceptance publication PlayerSnapshot 与 LocalHost durable physical facts；summary 必须逐值与原始记录一致。
- **修复要求/映射：** Probe 写入独占 JSONL，一次、CreateNew、typed fields 取自真实 Session/receipt/event/checkpoint；复用现有 canonical parser 与 `ConfirmedVisibleEventObserver`，检查候选 ControlEpoch 与已确认 Player control baseline、room/session/table/scope/round/seat/role/binding/connection/owner、receipt request/digest/commandVersion/high-water、incremental event 与相同 acceptance publication 后态。Pon `[0,1,2]`、Chi `[0,4,8]` 全部退出手牌并在对应 meld；Riichi 25000→24000 且供托恰加 1；Closed Kan `[0,1,2,3]` 退出手牌、死山补牌实体73、死山计数减1；LocalHost before/after physical audit 必须 136 assigned/136 unique。无 after hand/snapshot/receipt/event/physical facts 即失败。
- **统一负例矩阵：** Applied 缺 event；错误 round/seat/request/scope/身份；单边与成对篡改 binding/connection/owner/control；命令版本不超 baseline、command/event/receipt high-water 不同；游标回退；缺/错 case、snapshot、hand、meld、canonical/hash；Pon/Chi/Kan meld tile 仍在手；Riichi 点棒差额或供托不是 +1；Kan replacement/deadwall/136 inventory 不符；native nonzero/timeout。机制负例为 `TOOL_FIXTURE`，不记 GAME。
- **返修前证据：** `current-probes.json` 与 caller `REVIEW.txt` SHA-256 `232EC30F03792F35B8CEE758D98675875AD723D5E5D075AF616091D3E8E33893`。
- **验收断言：** 每个真实 Player positive 必须有单条原始 JSONL 且 canonical payload、Typed Applied receipt、observer batch/event、same-version snapshot、durable physical audit、动作效果全部相互绑定；runner 严格拒绝上述负例，TOOL_FIXTURE 不冒称 GAME。

### D03-R1-04 — MAJOR：source/build/closure/XML 门禁未按实际产物 fail-closed

- **位置：** `run-p45-c04-player-actions.ps1` 的 clean/source archive、marker、pre/postbuild closure、PlayMode XML、seal；`C04PlayerActions.Tests.ps1`。
- **复现：** 调用方 `probe-current.ps1` 对原 marker gate 注入缺 `errors` 和伪 marker `{result:Succeeded,errors:0,target:Windows_FAKE,scenes:[Assets/FakeBoot.unity]}`；此前自有 timeout/drain 与 `.git` 指针复制问题已有 R2 复现。
- **实际 vs 期望：** 旧 marker 只核 result/errors/宽泛 Windows/含 Boot，未绑定 exe bytes/hash、精确目标和完整 enabled scene list；postbuild 只 hash ScriptAssemblies；工具 suite 无真实 clean/dirty archive、closure 差异、缺失/重复/skip XML 夹具。期望 runner 对完整来源、实际包、所有实际 Unity inputs 及精确 XML 做可执行校验，任何不符/缺失均失败。
- **修复要求/映射：** 空/dirty git 输出及 git native exit 安全处理；`git archive`/`tar` 固定 commit staging，并拒绝任意 `.git` 指针；build marker 必含类型正确字段、StandaloneWindows64、冻结 scene 顺序、exe bytes/SHA/buildIdentity；pre/post closure 覆盖 Assets/Packages/ProjectSettings/ScriptAssemblies，记录每项变化 hash，仅允许明确的 UnityGenerated ScriptAssemblies；精确四个唯一 PlayMode Passed/0 skip；manifest 在最终 summary 后只封存停止写入的稳定文件。保持 fresh shared plugin provenance guard、owned process tree timeout/bounded drain、拒绝覆盖/foreign sentinel。
- **统一负例矩阵：** clean/dirty/native git error、foreign `.git` pointer、缺 marker 字段/错误类型/result/target/scene/output/hash/build identity、Assets/Packages/ProjectSettings 输入变化、XML 缺失/重复/skip/错名、native nonzero/timeout/drain、重复 GUID root/已有证据/foreign sentinel 变化。全部通过 production gate 函数在 TOOL_FIXTURE 验证。
- **返修前证据：** R2 `current-probes.json` SHA-256 `8FA97FA145DDE7CAF19312CEEE17A86BDBCC9D7C5BDFC01D1BDB4659561CAC0D`；caller `evidence-manifest.csv` SHA-256 `FFA151A217397437D3AB84C8CDF40A65213CE96B2BBD56441E74597292CC5B97`。
- **验收断言：** 工具 fixture 真实写入非空输入和 foreign sentinel，clean positive、所有上述负例拒绝且失败后证据字节/hash 不变；实际顺序 fresh .NET → PlayMode → fresh Windows Player 四 case；manifest 可独立复算并绑定代码/报告/actual commands/XML/log/package/provenance hashes。

> R3 验证中的所有路径、native exit、XML 计数和 SHA 将在代码冻结候选的 fresh runner 结束后填入下方“R3 执行结果”。既有 R2 failure/XML/manifest 保持只读历史；R3 不会刷新或覆盖它们。

## 候选身份与边界

- acceptedBase：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- 冻结实现前候选：`734b3775fb7e658fcac33d3d6ce07cf195e96001`
- 本轮复核前冻结候选：`2740ff6ecf91539afc125d4c8cce4a66fa9d9de2`
- 本轮授权代码候选：`424b4833`（提交 `8be0f30f`、`0e4525a1`、`b1345adf`、`99f6d6b4`、`b221cb16`、`424b4833`；最终报告另提交）。
- 最终报告 SHA-256：报告提交后补录。
- 受测范围仅是本候选的 C04-D03。V4 仍为唯一生产架构；G2–G5 未批准。本报告不改 census，不宣称正式 C04/G2 通过。D01/C05 增量未读取、未合入。P4.4、UI15、C01、C03 和 2a3a359 仅作已接受历史前置，本轮不重开。

固定分母为 C04-G2-01..04：Pon、Chi、Riichi、Closed Kan。Kan 明确使用暗杠；不宣称覆盖加杠或 chankan。

## 完整缺陷清单与修复映射

调用方移交的完整审查清单为 4 组（2 BLOCKER / 2 MAJOR），均由首次独立审查发现。本轮 fresh 运行新增了能解释原 blocker 的根因信息；发现时机和证据见各项，没有将其拆成额外清单组。以下保留返修前复现，并分开列本轮实际结果。

### D03-R1-01 — BLOCKER：预置 round 与 LocalHost 初始 checkpoint/context 版本错位

- 位置：`LocalSnapshotFacts.cs::CreateInitialCheckpoint`、`LocalPlayerActionScenario.BuildCallWindow`。
- 复现：调用方 `build/caller-review/run-tests.ps1`；直接 LocalHost XML：138 total / 136 passed / 2 failed（Pon、Chi）/ 0 skipped，native exit 1。
- 实际 vs 期望：round 经 13 个已接受合法领域命令后，初始投影 context 仍为 stateVersion 0，真实订阅报 `Context state version 0 predates committed round version 13`；预期是 opt-in 初始 checkpoint/context 与预置 round 对齐，仍保持真实空事件游标并严格校验。
- 返修前证据：调用方 `build/caller-review/localhost.xml` SHA-256 `B1092C8A08AD880CB7444A1EC24866CC573E27FF625D89736E38E049FF55BD43`；调用方 REVIEW SHA-256 `232EC30F03792F35B8CEE758D98675875AD723D5E5D075AF616091D3E8E33893`。
- 修复映射：仅在被显式注入的 round 初态建立 checkpoint 时用 `round.Version` 同时设置 checkpoint 与投影 context 的 StateVersion，EventSeq/high-water 仍为 checkpoint 的 0；不伪造 event/history/receipt，不改变普通 round0 默认值。调用方明确授权新增此文件。
- 本轮实际结果：fresh Release build 成功；完整 LocalHost 138 项中 136 通过、2 失败、0 跳过。Riichi 与 Closed Kan 真实 channel 用例通过；Pon 与 Chi 在目标动作前被 `STALE_STATE_VERSION` 阻断，未能证明成功。
- 新暴露根因及此前不可发现原因：修正 checkpoint/context 后，首次实际执行到 call-window 通道时发现 `TableActor` 将 `_stateVersion` 固定初始化为 0（`dotnet/src/Mahjong.TableRuntime/TableActor.cs:89`），而预置 round/checkpoint 已为 version 13；Actor 对应 store 当前 version13提交 version1，真实调用出现 `The expected state version is not current`。先前 projection 在 `context version 0 < round version 13` 就提前失败，没能触及 Actor/store commit 比较。
- 最小额外文件请求：为保持默认生产行为不变，增加显式 opt-in `InitialStateVersion`，由 LocalHost 用同一 checkpoint version 传入并由 Actor 初始化：`dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs`（`TableActorOptions`）、`dotnet/src/Mahjong.TableRuntime/TableActor.cs`、`dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs`。不需要改 Domain/Client/Protocol；须经调用方授权后再改。
- 验收断言：四场景真实 subscribe 与目标 action 成功；Actor、store、checkpoint/context 的 version 一致；空事件游标和后续真实 event high-water 连续；完整 LocalHost 普通启动/恢复旧例无回归。

### D03-R1-02 — BLOCKER：Unity 新增 Player/Factory/PlayMode 源码不符合当前合同，不能编译

- 位置：`C04PlayerActionProbe.cs`、`LocalTableChannelFactory.cs`、`C04PlayerActionScenarioTests.cs`。
- 复现：调用方 `compile-app.ps1` 使用已接受 Unity App RSP/reference closure、当前 App 源码和 fresh LocalHost/Contracts/Client 输出执行 Roslyn 编译。
- 实际 vs 期望：原诊断包括缺 `Task`、旧 `Cursor.Value`、缺 UI.Session namespace、nullable `ControlEpoch`、事件 tile 读取路径错误、`yield return void`；Factory 暴露 Domain 返回类型导致 CS0012；PlayMode 缺 LocalHost import。期望是 Unity 源码使用当前编译合同，且不增加 Domain asmdef 引用。
- 返修前证据：`app-csc.stdout.log` SHA-256 `1381FBC1C8CD6DA9DEB01F666224971A0B1C3824F5F0D98AFBA96D20DEC6C66E`；import-only 后续诊断 SHA-256 `4766B96C5F0F72B5446AC3589C5FE1327D49901E975F91AE21F1232E8AE6801E`；调用方 REVIEW 中保留逐条编译错误。
- 修复映射：补 Task/UI.Session imports；使用 `StreamCursor.StreamVersion/ProjectionSeq`；明确处理可空 epoch；从 typed `Payload.TileIds` 读取事件；协程正常结束不 yield void；PlayMode 显式引用 LocalHost；Factory 改用 `void ValidateScenario` 门面，不暴露 Domain 返回值。
- 本轮实际结果：源码改正已提交，但 Unity 编译、PlayMode XML 和 Player build 均因前置 .NET fail-fast 而 NOT_RUN；返修前 Roslyn 日志不算修复后编译证明。
- 验收断言：取得额外 Actor 初态授权并通过 fresh LocalHost 后，隔离 fresh shared plugin 实际 Unity PlayMode 编译通过；精确四个唯一测试名均 Passed、零 skip；随后 fresh Windows Player build 成功。

### D03-R1-03 — MAJOR：确认摘要可自洽造绿，且没有验证动作实体效果

- 位置：Player probe 成功分支、runner `Test-C04CaseSummary`、PlayMode/.NET 动作断言。
- 复现：调用方 `probe-runner.ps1`；原 positive 的 command version 9/baseline 11 仍通过；将 command version 改成 1、epoch 改成 999、游标改成 99→0 且 decisionId 置空、payload 改为任意 ASCII 并重算 SHA 仍通过。
- 实际 vs 期望：runner 将 ExpectedPayloadSha256 从同一 summary 传回自身，Probe 在收到事件后立即 PASS；无精确 Pon/Chi tile ownership、Riichi 1000 点及供托、Kan 实体 73 补牌和真实后态证据。期望为 canonical SubmitAction 解码、Applied receipt、同身份同命令版本增量事件和同一接受快照/实体变化相互约束。
- 返修前证据：`runner-probes.json` SHA-256 `A63E04C9493750434C2D4CB1171049783DAD17F1EE86061783AF1DEA2ABC2E11`；调用方 REVIEW SHA-256 同 D03-R1-01。
- 修复映射：Probe 用现有 `CanonicalPayloadParser` 检查命令意图；runner 独立按 MJC1 canonical 结构解析实际 base64 payload，校验 action/round/decision/control epoch/固定 tiles；验证 Applied command/state version 大于 baseline 且相等、event sequence/high-water/cursor 前进并匹配、room/session/table/scope/seat/round/role/binding/connection/owner identity 一致。Probe 只在 same-publication 快照与 observer 确认批次相等时报告 PASS。PlayMode/.NET 对 Pon `[0,1,2]`、Chi `[0,4,8]` meld/hand 转移、Riichi 25000→24000 + 供托、Closed Kan `[0,1,2,3]` 与 replacement draw 73 作值断言。
- 新暴露并已修复的信息：当前已接受 canonical parser 的 string field view 暴露包含内层 UInt32 长度前缀的字节；Probe/test 使用规范 UTF-8 语义前需在授权边界解码该前缀。首次全量执行中 Riichi/Kan 已到达并通过动作断言但字符串断言误失败；这是首轮静态/调用方旧 runner 探针未执行本地新增 typed payload test 时不可发现的问题。Probe、.NET test 和 runner 现解码并校验内层长度，不改共享合同。
- 本轮实际结果：工具边界 positive 1 + negative 23 通过；fresh LocalHost 只证明 Riichi 与 Closed Kan 两项通过，Pon/Chi 未能提交；Player 的真实 Applied receipt、可见事件、同版后态和物理效果未运行，不能将设计断言写作实测。
- 统一负例矩阵：Applied 缺事件、错误 round/seat/request/scope、错误 binding/connection/owner/control epoch、decision 空或错误、命令版本不高于 baseline、事件命令版本不匹配、游标回退/receipt high-water 不符、任意文本重算 SHA、错误动作/实体、缺失动作后态一律拒绝。工具夹具标识为 TOOL_FIXTURE，不冒充 GAME。
- 验收断言：每个真实 case 的规范 payload、候选、receipt、增量事件及动作后状态逐值相符；Kan replacement 为实体 73；失败样例均在同一轮 fixture 中被拒绝。

### D03-R1-04 — MAJOR：runner 来源隔离、进程超时/drain 与阶段门禁不完整

- 位置：runner `gitStatus/source-copy/Invoke-OwnedNative/build/XML/closure` 门禁及 `C04PlayerActions.Tests.ps1`。
- 复现：调用方 `probe-runner.ps1`：clean 状态 `.Trim()` 空值报错；robocopy `/XD .git` 复制 `.git` 文件指针；Timeout 1 秒自有 4 秒进程约 4.91 秒才返回 124，stdout drain 无界；旧 PlayMode 只核 4 个 Passed，不核精确唯一 case 集合；旧工具 fixture 没有实际文件，不能证明保留 foreign sentinel。
- 实际 vs 期望：prebuild closure 未覆盖 fresh plugin/Unity 生成输入；build 仅看 native 0 与 exe；工具负例没有真实非空封存证据。期望是来源无 `.git` 指针、fresh provenance 与 postbuild inputs 可复核、timeout 与输出等待有界、build marker 和精确 PlayMode case matrix fail-closed、封存与 foreign 文件所有权可验证。
- 返修前证据：同 `runner-probes.json` SHA-256 `A63E04C9493750434C2D4CB1171049783DAD17F1EE86061783AF1DEA2ABC2E11`。
- 修复映射：空 git 输出按数组安全处理并检查 native exit；以 `git archive` + `tar` 构建固定 commit staging，不复制 `.git`；记录 prebuild source、postbuild Assets、fresh shared plugin/provenance、ScriptAssemblies 与 Player package hashes；自有 PID/start time、scoped tree kill 和 3 秒有界 drain 均进入阶段记录；build marker 校验 Windows/Succeeded/errors/Boot scene；PlayMode 核对四个固定唯一名字、零 skip；工具夹具创建非空文件与 foreign sentinel，另启动自有 timeout/descendant fixture 并检查限时 kill/drain；既有 RunRoot 一律拒绝覆盖。
- 新暴露并已修复的信息：`dotnet test --logger trx` 在此 xUnit v3/Microsoft.Testing.Platform 入口没有产出 TRX；本轮因此改为 fresh `dotnet build` 后直接启动新 DLL 的原生 `-result-xml` reporter。通过已构建程序集 `--help` 确认原生 reporter 参数后才可定位并修复，旧缺陷在原静态探针阶段不可见。
- 本轮实际结果：工具 positive1/negative23 原生退出0；最终 runner 取得共享 mutex、归档/解包、fresh Release build 均退出0，原生 xUnit XML准确报告 138/136/2/0，runner 按固定 case 识别 Pon/Chi FAIL、Riichi/Closed Kan PASS，并在 fail-fast 下封存 failure summary 与 manifest。因为 LocalHost 未全绿，shared DLL、Unity、PlayMode、Player 阶段没有运行。
- 统一负例矩阵：clean/dirty status、伪 `.git` 来源、错误 marker/scene、PlayMode 缺失/重复/skip、native 非零/timeout/drain、重复或非 GUID root、重复证据、foreign sentinel、canonical/身份/效果错误均应拒绝且不变更封存输入。
- 验收断言：runner 顺序执行 fresh .NET → targeted PlayMode → Windows Player 四 case；阶段失败立即 fail closed，所有输入/输出只在本次 GUID root，封存 summary 后再 hash manifest。

## 本轮验证结果与证据

最终执行命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/c873ab2ce5894db58a984080f1d02ebe -TimeoutSeconds 1800`。runner 等待 `Local\MahjongV5P45UnityValidation` 55.172 秒后持锁；冻结 commit archive / extract native exit 0，fresh LocalHost Release build exit 0；fresh full test native exit 1（fail-fast），总计138/通过136/失败2/跳过0。测试 XML 的 failure summary 明确 Pon、Chi 在 `WaitForDecisionAsync` 收到 `INTERNAL_ERROR/Stream subscribe failed: STALE_STATE_VERSION: The expected state version is not current`；Riichi、Closed Kan 为 Pass。此次测试包含新加的 Actor 权威 TilePositionMap 136-entity audit；通过的 Riichi/Kan 均通过全局物理守恒断言。

- 最终冻结候选：`7afe930fdb6bc80ac35f8f04ba93a0cebb099a40`，包含代码候选 `424b4833` 与此前提交的交付报告版本。Prebuild source closure `source-prebuild-closure.json` SHA-256 `9716AC4A57FE1A3CC1C34F847803055F18770970064BB8643683B616CE69FCA0`。
- 原始 xUnit XML：`build/c873ab2ce5894db58a984080f1d02ebe/dotnet-results/LocalHost.xml`，SHA-256 `5991109347069B0F77355837243AE8F4E92063C1E42410C8987CB9417241127A`；fresh test DLL SHA-256 `C820D33098D32D6062246348DC923E6DF4ABB8FB68E5FB3BB0199791C75306EA`。
- Fresh build stdout SHA-256 `CAED30C9D11199E1B661D5BCEF4026666A961B46E5C00337FC8F192B7500BDF5`；test stdout SHA-256 `088B81C4B3EF4EDD479CACC1201E9BE53A4D009A9562ECD218985D05C2710355`；stderr 为空（empty SHA-256）。
- Failure summary `failure.json` SHA-256 `39B6788D46479F0E1B380F4068BBE763BF0AEBB4628984F9C390C69DA8365D22`；sealed artifact manifest `manifest.json` SHA-256 `C8A3093D549A15C26E8D58F2BFB2B9999D1705CDA96400707871049E9EA34882`，`manifest.sha256` SHA-256 `6790BCF305FEFB501792A3C69B17A5C3EC5BA940459180FECD882F6FD2FD116D`。Manifest 有 1089689 字节，含本 run 的来源 tar、source closure、build、XML、日志、failure summary 所有实际生成文件及逐项 hash；失败封存后没有再追加日志。
- 工具夹具命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerActions.Tests.ps1`，native exit 0；stdout `C04_TOOL_TESTS_PASS positive=1 negative=23` SHA-256 `BA680EFAC147D973AC2A0B71261BAE84987E272AFB901AC89F00D83C0631F30F`，exit 文件 SHA-256 `13BF7B3039C63BF5A50491FA3CFD8EB4E699D1BA1436315AEF9CBE5711530354`。正例为 `TOOL_FIXTURE`，不是 GAME。
- Unity shared plugin provenance、Unity script compilation、精确 PlayMode XML、Player build marker、四个 Windows Player case / summaries / package hashes：全部 `NOT_RUN`，由 fresh .NET fail-fast 阻止。没有 caller Unity/Player/PG/Android/CI 运行属于调用方既有边界；本轮也没有启动这些阶段。

| 固定 case | .NET | PlayMode | Windows Player | 备注 |
|---|---|---|---|---|
| C04-G2-01 Pon | FAIL | NOT_RUN | NOT_RUN | fixed seed 4401；version skew 阻断 seat0 真实订阅 |
| C04-G2-02 Chi | FAIL | NOT_RUN | NOT_RUN | fixed seed 4402；version skew 阻断 seat0 真实订阅 |
| C04-G2-03 Riichi | PASS | NOT_RUN | NOT_RUN | fixed seed 4403；LocalHost action/effect assertions passed |
| C04-G2-04 Closed Kan | PASS | NOT_RUN | NOT_RUN | fixed seed 4404；LocalHost Kan/replacement 73 assertions passed；不覆盖 chankan |

`LocalPlayerActionScenario` 固定 seat0 目标候选：Pon `[0,1,2]`，Chi `[0,4,8]`；固定前置为 seat0 discard132、seat1 discard80、seat2 discard84、seat3 discard2(Pon)/4(Chi)，期间其他响应使用固定 Pass 序列。Riichi hand `[0,4,8,12,16,20,24,28,32,36,37,72,76,124]`；Closed Kan hand `[0,1,2,3,36,40,44,48,52,56,60,64,68,72]`、死山replacement实体73。所有初态/前置构造都核136实体唯一；目标动作走过 LocalHost Player channel 后测试只读检查权威 `RoundStateMachine.Tiles`：TilePositionMap valid/136 assigned/0 unassigned，meld实体离开 seat0 手牌，Kan 补牌73回到seat0 hand。新加的最终权威后态断言尚未由 fresh runner 执行；因 D03-R1-01 的 Actor/store version 断层，Pon/Chi没有运行到 action，也不能声称其目标后态实体守恒已由实测证明。

完整 LocalHost fresh test/XML、工具负例、PlayMode XML、build marker、四份 Player summary/log、package/provenance/source closure 及最终 manifest 均须位于独占 `build/<GUID>`。Unity 6000.3.27f1 与当前 Windows Player 执行之外的平台/版本保持未覆盖；Android 属 C05，iOS/远端 transport 不在本任务范围。

## R3 最终复核闭环记录

本节是本轮最终实际结果，优先于上文较早的 R3 执行尝试记录。复核清单仍为 D03-R1-01 至 D03-R1-04 共四组（2 BLOCKER / 2 MAJOR）；没有把 caller 其他任务或旧版本失败移入本清单。代码冻结候选 `9cdc6ef4a6fc59ee75d610616c756e5184dd925e`，基线 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，实现父发布 `734b3775fb7e658fcac33d3d6ce07cf195e96001`。其 fresh source closure 在 `build/864b3a8a55d44510bd891ce5f2c6617c/source-prebuild-closure.json`，SHA-256 `dfa17b236ac25bc9dea2ef60ca165f4c9a4df0f32d86b7a1e2bbd7abddd2e866`。本报告提交后另给 final report-only HEAD 与报告 SHA；source closure 对应上述代码候选，不冒称包含后续报告字节。

### 四组缺陷、修复映射及验收状态

| ID / 严重级别 / 位置 | 复现命令或脚本；实际 vs 期望 | 证据路径与 SHA-256 | 修复映射、统一验收断言与本轮状态 |
|---|---|---|---|
| **D03-R1-01 / BLOCKER**；`TableRuntimeContracts.cs`、`TableActor.cs`、`LocalTableRuntime.CreateActor`、`LocalSnapshotFacts`、`LocalPlayerActionScenario` | caller 原复现 `build/caller-review/run-tests.ps1`；本树复现 `tools/unity/run-p45-c04-player-actions.ps1`。原实况是 Store/checkpoint=13、fresh Actor=0，Pon/Chi 订阅报 `STALE_STATE_VERSION`；期望同一真实 checkpoint 初始化 Store/Actor，普通默认仍0，恢复继续使用已提交基线。 | caller XML SHA `278964D691EF5BF64D90292300FAAA8953E82BFE93429F0A49A3CC6594DA8445`；本树 `build/864b3a8a55d44510bd891ce5f2c6617c/dotnet-results/LocalHost.xml` SHA `1ab8f487a8ac880d8b543eb9717263126f1de1e9fc853fa4bc2bcf4bc479ccbf`；前一次完整 LocalHost XML `build/7dd028abc3e8422991a5038f5da1b51f/dotnet-results/LocalHost.xml` SHA `8d222d534ae6c0b6780b072efe4e724b5f6d6e252622d382ade11435ff94a7c3`。 | 增加默认0的显式 `InitialStateVersion`，LocalHost 从同一 checkpoint 注入；仅 `c04-` 固定 opt-in round 将真实 Domain round version/history 用于初始 projection，普通 round/历史行为不变；保留 stale guard、EventSeq=0 与真实事件连续性。四个固定 LocalHost 场景在最终候选 XML 都是 PASS；此前 fresh LocalHost 138/138、TableRuntime 483/483 也通过。最终候选全套 LocalHost 有1条既有 furiten 测试失败，详见“新观察”；故全模块无回归验收**未完全通过**，不把该次报告成全绿。 |
| **D03-R1-02 / BLOCKER**；`C04PlayerActionProbe.cs`、`LocalTableChannelFactory.cs`、`C04PlayerActionScenarioTests.cs`、`C04PlayerActionBuild.cs` | caller `compile-app.ps1` 的17条实际 Roslyn 诊断；本树 fresh Unity 命令由 runner 记录。期望使用现有 API 编译、从正式 Boot 入口运行并执行四个精确 PlayMode case；不加 Domain asmdef/reference。 | caller `app-csc.stdout.log` SHA `117F5E04D402A72313652B0B811F0AF7243395CD04C71FDA18949587D49ACACF`；本树 `build/72ad04036ece4fcf9c4da30db08265a0/logs/playmode.xml` SHA `b01802ea80c51cdc55b9ad6c87c2c9de683431e30b261b029f29b943d19a678f`；对应 Unity log SHA `60ad44d226f491c2cde312de96b7cb909b4a88a3ad243e8f210f4686bb0b76b4`。 | 补齐 namespace、真实 cursor/OwnMelds/Payload API、可空 epoch 与协程返回；PlayMode 显式加载真实 `Boot`，由 `GameBootstrapper` 驱动 Menu/Lobby/Table。Unity 实际 XML 精确4/4 Passed、0 failed、0 skipped；此 PlayMode 运行的代码主体与最终候选相同，最终候选新闭包 runner 在全模块 fail-fast 后没有再次启动 Unity。编译/PlayMode 在已记录运行版本**已通过**，但尚非最终完整链路通过。 |
| **D03-R1-03 / MAJOR**；Probe 持久化、`Test-C04CaseSummary`、`Test-C04ActionEvidence` 与 `C04PlayerActions.Tests.ps1` | caller `probe-current.ps1`；本树 `tools/tests/C04PlayerActions.Tests.ps1`。原 gate 曾接受 meld 牌仍在 hand、后态缺失、Riichi 供托+2、两份副本同步篡改 epoch。期望原始 canonical command、typed receipt、精确增量事件、同 acceptance publication 后态及 LocalHost 136实体审计互相绑定。 | caller `current-probes.json` SHA `8FA97FA145DDE7CAF19312CEEE17A86BDBCC9D7C5BDFC01D1BDB4659561CAC0D`；本树工具 suite 最后输出根 `build/af3735efbcd247218fdc5e85e8fa0db5`；目标执行证据必须在 runner 每个 case 的 `player-action.jsonl`，但本轮没有 Player 文件。 | Probe 只写 CreateNew 的原始 JSONL，现有 canonical parser 校验 request/scope/action/round/seat/epochs、baseline→Applied/event/snapshot high-water，并核 Pon `[0,1,2]`、Chi `[0,4,8]` 实体离手、Riichi 恰扣1000并加1供托、Closed Kan `[0,1,2,3]` 与死山补牌73、136唯一实体。工具 fixture 正例1、负例49；负例包含缺事件/后态、错误动作效果、coherent epoch 篡改、错误 request/scope/round/seat、native非零/timeout、哈希与 case/XML 不完整。工具门禁修复已通过；**真实 Player 的 JSONL/effect 本轮未产生，GAME 证据未验收**。 |
| **D03-R1-04 / MAJOR**；runner source/marker/XML/package closure/seal 与工具 suite | caller `probe-runner.ps1`、`probe-current.ps1`；本树运行 `tools/unity/run-p45-c04-player-actions.ps1` 与工具 suite。期望干净/脏树都可处理、marker严格匹配、只封存稳定输入、Unity 包输入及 fresh DLL 闭包可复核、超时/覆盖/外来文件 fail-closed。 | caller `current-probes.json` SHA `8FA97FA145DDE7CAF19312CEEE17A86BDBCC9D7C5BDFC01D1BDB4659561CAC0D`；最终候选失败 run `build/864b3a8a55d44510bd891ce5f2c6617c/manifest.json` SHA `0d4ddb15fdc565d1a60e30beb0ccf21d5b5aab752769c1eca42504dd20494a4e`；`failure.json` SHA `f3f0a0a7b055584aae9e710737252a8c3866666a9e9d580062c1d9101df79346`；最终失败 LocalHost XML SHA 同 D03-R1-01。 | 修复空 git status、Git archive staging、拥有进程树的有界 drain、marker exact target/scenes/output identity、PlayMode 精确名字/无 skip、summary 后封存；UPM NPM/package cache 定向到每次 E 盘 GUID root；pre/post 输入闭包哈希 `Assets/Packages/ProjectSettings/Library/PackageCache/Library/ScriptAssemblies`，仅排除 Windows Player 不消费的 macOS `.bundle/**` 并在 manifest 逐项声明；volatile cache 明确隔离。工具 suite 正例1/负例49通过。Unity 真实 Player Build 曾返回 `result=Succeeded` 但 `errors=6`、native exit1，marker 因非零 errors 拒绝；另一次遍历 macOS `.bundle/Contents` 的缺路径已由平台过滤修正。最终候选因 LocalHost fail-fast 未走到包闭包/Player 阶段。**阶段门禁机制已修，Windows Player build 阶段仍未验收。** |

### 统一负例矩阵与固定分母

49个负例覆盖：Applied 无匹配事件/增量 batch、错误 round/seat/request/scope/room/session/table/role、binding/connection/owner/control epoch（含同步改写两份副本）、decision 或 command version 低于 baseline、receipt/event/high-water/cursor 回退或不一致、错误/缺失 case 与 canonical payload/hash、native 非零或 timeout、缺/重复/extra/skipped PlayMode XML、marker 缺字段/错误 target/result/scenes/executable hash/build identity、clean/dirty source 与伪 `.git` 来源、postbuild Assets/Packages/ProjectSettings/resolved-package closure 改变、输出覆盖或非 GUID/仓外路径、timeout descendant/drain、foreign sentinel 修改、volatile Unity/UPM cache 被误封存，以及 Pon/Chi/Kan meld 牌残留 hand、Riichi 点棒/供托错误、Kan replacement/dead-wall/136 inventory 错误。49项均由实际 `tools/unity/run-p45-c04-player-actions.ps1 -FixtureOnly` 门禁执行，类型标记 `TOOL_FIXTURE`，不冒称 GAME；正确正例1项。每种被拒绝的变体保持现有强断言，不通过 skip、Total0、删除测试或刷新摘要。

固定分母仍为4，不因全套测试异常或 Player 未运行而减少：

| 固定 case | LocalHost 目标测试（最终候选 XML） | Unity PlayMode（实际 run `72ad...`） | Windows Player（最终候选） |
|---|---|---|---|
| C04-G2-01 Pon | PASS | PASS | NOT_RUN：Build errors=6 被 fail-closed 拒绝 |
| C04-G2-02 Chi | PASS | PASS | NOT_RUN：Build errors=6 被 fail-closed 拒绝 |
| C04-G2-03 Riichi | PASS | PASS | NOT_RUN：Build errors=6 被 fail-closed 拒绝 |
| C04-G2-04 Closed Kan（Closed Kan；验证 replacement 73/死山实体） | PASS | PASS | NOT_RUN：Build errors=6 被 fail-closed 拒绝 |

### 本轮最新运行链、原生退出与保留失败

最终候选启动命令为 `powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/864b3a8a55d44510bd891ce5f2c6617c`。Mutex wait 约0.06秒；Archive/Extract/Release build 均 native0；fresh LocalHost XML 为138项/137 Pass/1 Fail/0 Skip，native1，fail-fast 停止，因此这次 TableRuntime/Unity/Player 均 NOT_RUN。唯一失败 `Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` 在第二次 Ron 获得 `STALE_STATE_VERSION`，测试期望 `SETTLEMENT_FURITENRON`。同一测试于更早完整 fresh run `build/7dd028abc3e8422991a5038f5da1b51f` 通过；最终源码内该测试未修改，普通 `room-r1` checkpoint/Actor 初始 state version 均为0。该单次差异是新出现的验证异常，先前通过一次完整 fresh run 无法预见；没有证据将它归因于本任务版本注入，未扩展第五个产品缺陷 ID，也未改授权范围外测试。失败仍保留，不用另一份全绿输出覆盖。

用于定位各阶段的原始证据（每个 GUID 目录的 manifest 已在失败/稳定阶段停止写入后封存；sealed 后未追加日志）：

- 最新最终候选失败：`build/864b3a8a55d44510bd891ce5f2c6617c/dotnet-results/LocalHost.xml` SHA `1ab8f487a8ac880d8b543eb9717263126f1de1e9fc853fa4bc2bcf4bc479ccbf`；`source-prebuild-closure.json` SHA `dfa17b236ac25bc9dea2ef60ca165f4c9a4df0f32d86b7a1e2bbd7abddd2e866`；manifest `0d4ddb15fdc565d1a60e30beb0ccf21d5b5aab752769c1eca42504dd20494a4e`；UPM 每 run 路径和 native stage 都记录在 `logs/runner.log` / `failure.json`。
- 上一次 full fresh .NET + Runtime + PlayMode：`build/7dd028abc3e8422991a5038f5da1b51f`，LocalHost 138/138、TableRuntime 483/483、精确 PlayMode 4/4、0 skip，三组 XML SHA 分别 `8d222d534ae6c0b6780b072efe4e724b5f6d6e252622d382ade11435ff94a7c3`、`758a4667b62feb5b1fffe6e7510b352514c96af116bf95ece69814fb36b9efe3`、`b834d1ebd13a22c37f228bcf2c40f39f48f995005c2731975b56cc787a3588d2`；manifest SHA `054e44dca1c10f7f91b78a9d9b62cad78fd12b8352313e90748e1c20734bcc1e`。PlayMode 后在 package closure 遍历 `.bundle/Contents` 缺路径而停止；这个 specific Windows platform scan bug 后已修，并加工具负例。该候选提交是 `dd718bf6`，与最终 C04/Unity业务代码相同，后续仅 runner closure/platform 声明改变。
- 上一次真实 Player Build 尝试（candidate `5f145cbe240bfbb17a22dc41e441bee6835f1661`，已移除 `-nographics`）：PlayMode 4/4 Passed；BuildReport `result=Succeeded errors=6`，Unity native exit1；marker 未写出、runner拒绝、四 Player case NOT_RUN。log `build/72ad04036ece4fcf9c4da30db08265a0/logs/player-build.unity.log` SHA `b0e6f766814a67e0a03ee3d6d8a6a229c4fa3aa89dd75492160bf1e5b7807a6b`；其中 URP host-type mismatches 为两种路径各重复4次，BuildReport 错误数6；Unity实际已生成 exe 不构成可接受 build 或 GAME 证据。此后 PlayMode 确认真实 Boot 加载后通过，但当前最终 runner 因 .NET fail-fast 未重跑 Player Build。
- 早期 Unity 错误尝试 `build/3882e8d405034f91b66d9fdd664dc331` 和包缓存 scan 失败 `build/7dd028abc3e8422991a5038f5da1b51f` 都保留原 `failure.json` / manifest；前者无图形启动曾 PlayMode 0/4，之后移除 `-nographics`、显式加载 Boot 后真实 PlayMode 4/4。不得把该旧失败输出或未接受 exe 改写成绿色证据。
- 当前工具 suite 命令 `powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/tests/C04PlayerActions.Tests.ps1` native0，输出 `C04_TOOL_TESTS_PASS positive=1 negative=49`；证据 fixture 独占根 `build/af3735efbcd247218fdc5e85e8fa0db5`。旧工具 stdout 曾将外层负例数量写成48（内层实际校验49）；本轮发现并同步修正报告计数，最终外层输出49。

最终 Player 标记仍严格要求 `Succeeded/errors=0/StandaloneWindows64/精确 enabled scenes/实际 exe bytes+hash+buildIdentity`；这组仍在拒绝结果，不放宽门禁。当前 D03 不可批准：R1-01/02 的修复在固定 case 实测及精确 PlayMode 中得到证据，R1-03 的工具门禁与原始证据实现已修但缺四份真实 Player GAME 记录，R1-04 的隔离/闭包/负例机制已修但当前最终候选尚未达到 fresh Player build 阶段。没有 Unity/PlayMode/Player 绿灯被当前全套异常覆盖或宣称。平台只覆盖 Unity `6000.3.27f1` Windows Player；Android/IOS/其他Unity版本未测。报告 commit 后执行 `git diff --check`、检查只暂存授权文件并保证 tracked clean；不 push、不改 PR、不审批、不改 census，不宣称 D03/C04/G2 正式通过，交付后等待 caller 独立复核。

## 历史文件范围记录（早期轮次；R3授权已更新）

返修授权清单共 13 个路径（初始12项+明确授权的 `LocalSnapshotFacts.cs`）；实际改动10个文件，3个既有 `.meta` 未变。逐个实际改动路径为：`doc/v5-重构/V5-G2-C04-PLAYER-ACTIONS-DELIVERY-20261101.md`、`dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs`、`dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs`、`dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs`、`tools/tests/C04PlayerActions.Tests.ps1`、`tools/unity/run-p45-c04-player-actions.ps1`、`unity/Mahjong.Unity/Assets/Editor/C04PlayerActionBuild.cs`、`unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs`、`unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs`、`unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerActionScenarioTests.cs`。普通应用 opt-in 仍关闭，Factory 仅显式 C04 参数调用 scenario facade。代码提交 `424b48335e26fe286e5ec2002828f887bcc8d0b9`；测试冻结提交 `7afe930fdb6bc80ac35f8f04ba93a0cebb099a40` 只比该代码提交多了 pretest report，最终交付为 report-only commits 后的 HEAD。最终报告 SHA-256 由交付消息给出。一次预运行在 report 尚未提交时被 runner clean-source gate 正确拒绝（`build/98096613af534dc6be607e2cf103c8d3`）：failure JSON SHA-256 `548FA90CC170B91892E3DC0AB4BCA2D02073FE486E3F36C40029FE4DDFB12844`、sealed manifest SHA-256 `E5355AFAF96D179B3909FCA41183606CC20903EB8812521B0384532967E8D340`；未开始 build，属预运行输入不干净，不是游戏测试失败。随后干净冻结态完整进入 fresh build/tests 并在已知 Pon/Chi blocker 上 fail-fast。`git diff --check` 通过、暂存区路径受控、最终 tracked tree clean；生成物均 ignored 在 E 盘 `build/<GUID>`。当前 D03 不可批准：需调用方先授权上述 3 个 TableRuntime/LocalHost 接口文件，然后从 fresh build 重新执行 .NET→PlayMode→Player。D01/C05 合并后的最终候选仍须重绑并复验；本任务不 push、不修改 PR/MR、不 approve、不 merge、不 deploy、不改 scheduler/complete.flag，并在交付后等待 callback。

## 本轮最终文件范围

R3明确授权共16个文件；相对接受的实现父提交 `734b3775fb7e658fcac33d3d6ce07cf195e96001`，最终代码候选 `9cdc6ef4a6fc59ee75d610616c756e5184dd925e` 的差异只涉及：`doc/v5-重构/V5-G2-C04-PLAYER-ACTIONS-DELIVERY-20261101.md`、`dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs`、`dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs`、`dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs`、`dotnet/src/Mahjong.TableRuntime/TableActor.cs`、`dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs`、`dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs`、`tools/tests/C04PlayerActions.Tests.ps1`、`tools/unity/run-p45-c04-player-actions.ps1`、`unity/Mahjong.Unity/Assets/Editor/C04PlayerActionBuild.cs`、`unity/Mahjong.Unity/Assets/Editor/C04PlayerActionBuild.cs.meta`、`unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs`、`unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs.meta`、`unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs`、`unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerActionScenarioTests.cs`、`unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerActionScenarioTests.cs.meta`。该清单保留新增TableRuntime三路径的显式授权；不修改清单外文件。

本任务不 push、不修改 PR、不 approve、不合并、不部署、不改 scheduler/complete.flag、不读取或调度其他线程；当前专项未通过，不声明 C04/G2 正式通过。报告提交后保持 tracked clean，并等待 caller 独立复核/callback。