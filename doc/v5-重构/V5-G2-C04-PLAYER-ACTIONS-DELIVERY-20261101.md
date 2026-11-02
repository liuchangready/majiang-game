# V5 G2-C04-D03 Player 动作确认返修交付（2026-11-02）

## 冻结候选与结论

- acceptedBase：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；本地分支基线：`734b3775fb7e658fcac33d3d6ce07cf195e96001`。
- R4 独立审查代码：`91a8c7ce613f35a8bf28368d2ae441a73ab5cb67`。本轮最新代码候选：`ac841640`（后续仅报告提交不改变代码）。
- 结论：**D03 当前不通过，未申请批准**。R1-01、R1-02 API/编译/四项 PlayMode 子项和 R1-04 严格 marker 类型子项保持关闭。R1-02 runner 已补完整 NuGet seed array 支持；当前 fresh .NET、四 raw verifier positives/九 negatives 和 shared provenance 全过，但 Unity warmup 首次发现并修正 Probe overload 错误。最新隔离 run 约 10 分钟未取得 Unity mutex，PlayMode、Player、四 GAME 未执行。R1-03 verifier 的完整原始事实绑定已在四个真实 .NET channel TOOL_FIXTURE 通过，GAME 侧证据尚未取得。R1-04 最终 source/resolved-package/pre-post build closure 与成功 seal 尚无本候选实测。
- 没有 push、PR/MR 操作、approve、合并或部署。

## 完整缺陷清单与修复映射

### D03-R1-02 — BLOCKER：Windows Player / 四个真实 GAME 链未在冻结候选闭环

- **位置**：`tools/unity/run-p45-c04-player-actions.ps1` 的 fresh source/shared-plugin/Unity/Player/GAME 阶段；`unity/Mahjong.Unity/Assets/Editor/C04PlayerActionBuild.cs`。
- **复现命令**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/2bbcb6abb0aa44af97d2dccf91397cfd`。
- **实际 vs 期望**：latest candidate fresh LocalHost 138/138、TableRuntime 483/483、verifier build/selftest 均 native 0；随后 `BuildSharedClosure` 因 `https://api.nuget.org/v3-flatcontainer/npgsql/index.json` SSL EOF / NU1301 native 1 fail-fast，Unity/Player/GAME 没运行。不得把旧 exe 或更早候选结果算入本轮通过。更早候选 `15dff24de09f4650a41ee6f95a4d29e5` 的 PlayMode 四项全 Passed、Windows Build native 0、marker `Succeeded/StandaloneWindows64/errors=0/bytes=667136`；但只执行到 Pon 一案且后续 raw-verifier 拒绝，故不满足四 GAME 验收。72ad 的 6 个 shader host-type errors 在该更早 fresh build 中未重现，但 latest candidate build 闭环仍缺。
- **证据路径 / SHA-256**：latest failed root `build/2bbcb6abb0aa44af97d2dccf91397cfd`：`logs/build-shared.log`（manifest 收录）、`failure.json` SHA `490D0584B1EF7F7BBFEF8FE765E8CE5DFE3E7C73FC1CF9033947BC11E9C418C1`；`logs/build-shared.log` SHA `66172CA77AD7062CEF12AD2897C929D67EB47913F97F20483855748BDA310E10`；`manifest.json` SHA `619C393B110B57DC5BCE3730689BB82466FE0E0710BB6FA440422A3AD8D9524A`，`manifest.sha256` SHA `26DA73302D8393C203F0A990D2A4D0C2B6C621BA1C6119A8423CA22DB6203478`。历史完整至 Player 的 `build/15dff24de09f4650a41ee6f95a4d29e5`：PlayMode XML SHA `DCC72807E35F741CA9FD7B4A604C004BDEECCA540831F860E319E65E1DDA1324`，Build marker SHA `51315DFA624401FFF776D63CAC92F3BA61711C8D05329A5CB326B0A68A95452D`，Unity build log SHA `6B91D57CBD6C1C01C6C11FD640404B2C291483781E722259248DA79F1C0BCFBB`，但候选 HEAD 为 `762df7b9c972fcd57e635c84a31b303acd3f3f52`，只能作历史证据。
- **修复映射**：保留严格 BuildReport/marker 门禁；runner 使用每 GUID 的独占 NuGet/UPM/TEMP、fresh shared DLL provenance、pre/post Unity input/package closure 和 native exit。未删除 shader/error 门禁或复用旧 DLL。网络错误不是代码绿灯，需 caller 网络可用后由独立复核决定是否再跑。
- **验收断言**：对 `91a8c7ce…` fresh LocalHost→TableRuntime→verifier→shared plugin→精确 4 PlayMode→Windows Player `native=0/errors=0`→四个 GAME；四案均有匹配 request/scope/round/seat/epoch 的 Applied receipt、可见事件、同 publication raw protobuf/authority state 与实体效果，封存 hash 闭环。当前四案最新候选均 NOT_RUN。

### D03-R1-03 — MAJOR：真实 raw facts、canonical 回执 digest、动作后态 publication 绑定

- **位置**：`C04PlayerActionProbe.WriteActionEvidence` 与提交/捕获路径；`dotnet/tools/C04PlayerActionVerifier/Program.cs`；runner `Test-C04ActionEvidence`。
- **复现命令**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/15dff24de09f4650a41ee6f95a4d29e5`（封存原始失败）。
- **实际 vs 期望**：真实 Pon 提交已 Applied 且收到 `PonDeclared`，但 verifier 从真实 protobuf/accepted publication 拒绝了 after snapshot：事件 acceptance sequence 为 2，后态快照来自 sequence 3。更早真实运行还揭示：回执 digest 使用 MJC1 `BusinessPayload.Digest`（域分离/长度前缀）而非裸 SHA256；evidence 曾被 pretty-print 成 80 行而 runner 按单条 JSONL 检查；初始 controlEpoch=0 是合法基线，非缺失 epoch。期望四案 raw evidence 的 protobuf 与 authority recovery bytes 可由现有 parser 真实解码，payload/receipt/身份/epoch/cursor/同一 publication 与 136 实体效果逐值一致。
- **证据路径 / SHA-256**：`build/15dff24de09f4650a41ee6f95a4d29e5/cases/C04-G2-01/player-action.jsonl` SHA `8856EBA61A723158C69289FFA97112BFF3B1C5476DC0E36FB56AFF3B622D515D`；`summary.json` SHA `97EB023CB1FA55A00B12E3E7AB17A2A77387CD5B05CECC25F8DAF68615E37024`；manifest SHA `FDE3D26482986BBB158F0B880FCA42FB4D7C7E5FA1A71FB5E516BB7A4C7B82BA`，manifest.sha256 文件 SHA `B01C0DA2BCA87DEFE5A67E7A910ED3388747C2D9A3A5E7F3C2B90E104B884614`。更早 canonical digest 错误原件保存在 `build/9fa774237b374b03903b3a0fbdc81d40`；Pretty JSONL 失败在 `build/ec663238de0e4488bb9846ab6f7bfc61`；两者均未覆盖或改写。
- **修复映射**：probe 记录 `intent.Command.Payload.Digest`；action JSONL 改成单行 JSON；verifier 调用现有 `CanonicalPayloadDigest`、生成 protobuf `PlayerSnapshot.Parser` 和 `LocalTableRecoveryFactsReader` 解码/核对实际 bytes；controlEpoch 可为 0 但必须绑定 canonical payload；最新 probe 在 `SessionCoordinator.Changed` 捕获有界、按 `AcceptanceSequence` 索引的真实 `SessionViewState`，并逐字段核对其 `VisibleEventBatch` 与 observer 事件批次，再用该 snapshot 生成 after bytes。无更晚 `coordinator.Current` 替换。
- **验收断言**：raw verifier 的负例拒绝非法 protobuf（1-byte 与 ASCII）、空原件、receipt/digest/request/scope 不符、错误 baseline/event/after epoch、物理伪造、后态 acceptance sequence 不同；正例是真实 codec 序列化消息。最新代码含修复但因上组 fresh shared-plugin failure 未进入 Player 验收，故此 ID 仍未关闭。

### D03-R1-04 — MAJOR：marker 类型、稳定 seal 与工具负例闭环

- **位置**：runner `Test-C04BuildMarker`、FixtureOnly seal、`tools/tests/C04PlayerActions.Tests.ps1`。
- **复现命令**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerActions.Tests.ps1`。
- **实际 vs 期望**：早先生产 marker 逻辑把 Boolean/fractional `errors/bytes` cast 成整数接受；tool-fixture seal 曾递归纳入变化中的 `temp/VBCSCompiler/AnalyzerAssemblyLoader`，且 overwrite 测试读取 live temp 导致哈希竞态。期望只接受字段齐全、类型严格为整数、范围正确并与真实 exe/scene/buildIdentity 绑定的 marker；seal 只包含停写的稳定 owned outputs，volatile roots 明确分离，foreign sentinel/旧证据不变。
- **证据路径 / SHA-256**：最新正负夹具命令在 HEAD `91a8c7ce…` 退出 0，输出 `C04_TOOL_TESTS_PASS positive=1 negative=59 protobuf=PASS malformed=2`；封存 fixture root `build/7121b479d8254488ac185e720b39f501`，`fixture-seal.json` SHA `4B1BD69412A8AA7366AA46F4C133096141C4DE43C1A24265CD5707865771B7C6`。原非稳定 seal 竞态在 `build/5c81bbcb1afa4271a9dfbe7504c61c47` 的命令输出，后续修复在 `ce8674be` 与 `762df7b9`。
- **修复映射**：严格整数 marker 类型/target/scene/hash/buildIdentity 校验保留；FixtureOnly 与 production 统一使用 `Get-C04StableArtifactFiles`；排除 Library/Temp/Logs/TEMP/NuGet/UPM/verifier 临时 build roots，显式记录 volatile 分类；测试按 sealed stable manifest 校验 overwrite 前后 byte/hash，不遍历 live cache。
- **统一负例矩阵 / 验收断言**：summary/action 25 例（Applied 缺事件、round/seat/request/epoch/control 错、native nonzero/timeout、case/payload/hash 缺失、版本/游标回退、canonical action/tile/meld 错、claim tile 仍在手牌/缺 post-hand、Riichi 点棒供托错误、Kan replacement/draw/136 实体错误）；raw evidence 8 例（缺 receipt、缺 baseline protobuf、event epoch 错、unique/count 错、after state 错、动作效果错、acceptance sequence 错）；marker 11 例（errors 缺失、target/scene/hash/result 错、Boolean/fraction/string/negative errors、fraction/Boolean bytes）；基础工具机制 15 例（owned descendant timeout/有界 drain、clean/dirty/archive/staging、native nonzero、精确/重复/缺失/skip XML、marker 与闭包矛盾、overwrite/path boundary/foreign sentinel）。共 59 个负例，positive=1；TOOL_FIXTURE 不宣称 GAME。marker/build/package 的最新候选集成复验仍被 R1-02 环境门禁阻断。

## 独立运行结果与失败身份

- **Latest candidate `91a8c7ce…`，run `build/2bbcb6abb0aa44af97d2dccf91397cfd`**：LocalHost Release build native 0；LocalHost XML 138/138/0fail/0skip，SHA `ABFBD889CF8FA538BB3CA53DE98487C306479CB650AFEABD12585AEF81E8689F`；TableRuntime 483/483/0fail/0skip，XML SHA `6F3EA92C9A0A1B99AF2113793E4D3080C294CA3DA760479E1A617EECB537E27E`；verifier build/selftest native 0；shared plugin native 1，Npgsql NuGet SSL EOF，Unity 没有运行。失败 manifest SHA `619C393B110B57DC5BCE3730689BB82466FE0E0710BB6FA440422A3AD8D9524A`。
- **Latest candidate 独立 run `build/d88dc4f941be48feb7ce3886b5967a8b`**：LocalHost 137/138、0 skip、native 1；唯一失败 `LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw`：期望 Applied，实际 Rejected；XML SHA `612D85256A70934B6326E9D2F4F8C75426F9ED42CA3864BA6661AF259D0444D9`；manifest SHA `3D0DD005694B3383A575DF9DF39187C24B61BB5E0748959D01D5364CADC9AEA8`。这与调用方此前记录的 FuritenRon/STALE_STATE_VERSION 属同一历史异常；没有受控 baseline/根因诊断，故不归因 D03、不增加第五产品缺陷、不修改未授权测试、不重跑刷绿。此失败不能覆盖 `2bb` 独立 138/138 结果。
- **历史候选 `762df7b9…`，run `build/15dff24de09f4650a41ee6f95a4d29e5`**：LocalHost 138/138、TableRuntime 483/483；PlayMode 精确 Pon/Chi/Riichi/ClosedKan 四项全 Passed；Windows Player Build native 0/errors 0，marker 正确；实际 GAME Pon native 0，但独立 verifier 拒绝不同 acceptance sequence。它不能证明修复后的 `91a8c7ce…` 四案通过。

## 暂存、diff 与最终边界

`git diff --check 734b3775..91a8c7ce` 通过；最后状态 tracked clean。范围相对 base 限于 18 个授权文件：交付本文、LocalPlayerActionScenario、LocalSnapshotFacts、LocalTableRuntime、TableActor、TableRuntimeContracts、C04PlayerActionChannelTests、verifier csproj/Program、工具测试/runner、C04 Build/Probe/Factory/PlayMode 及对应三个 Unity `.meta`；无 Domain/Client/Protocol/asmdef、D01/D04/UI15/C01/C02/census 变更。

当前仍需 latest-candidate fresh shared-plugin/Unity/Player 与四个 GAME 通过，才能关闭 R1-02/R1-03/R1-04 并交给 caller 独立审查。没有将历史 Player 成功、旧 marker 或工具夹具冒充本候选通过；没有 push/PR/approve/MR/merge/deploy。
## 第5轮批量返修与独立复验（2026-11-02）

本节记录本轮新增的实际复验和后续根因修复；R4 记录的 3 组仍是完整缺陷分母，未新增产品缺陷 ID。代码提交候选为 `ac841640`（父提交 `a8600669`、`74819bb7`）；当前受限于 Unity mutex 未取得，**D03 仍不通过**。无 push、PR/MR、approve、merge 或 deploy。

### D03-R1-02 — BLOCKER：本候选还没有完成 Windows Player / 四个 GAME

- **位置**：`tools/unity/run-p45-c04-player-actions.ps1` 的 NuGet seed、Unity import、package closure 与 build 阶段；`unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs` 的 after binding capture。
- **复现命令**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/<fresh-guid> -NugetSeedRoot E:\majiang-review\caller-c04-d03-r4-8c2e719a\build\caller-review\shared-seeded-packages -NugetSeedAuditPath E:\majiang-review\caller-c04-d03-r4-8c2e719a\build\caller-review\nuget-seed-audit.json`。
- **实际 vs 期望**：`build/e96efb830bf940cc8443ec92eced0262` 首次发现 audit JSON array 被 PS5 pipeline 嵌套成一条拼接 path，fresh run 在 seed 阶段拒绝；根因是原工具夹具只用单行 audit。首失败 `failure.json` SHA `9F20B0CB23F1B14776C675310789B8FC5638FB239B8A752EB88D22023F9F7115` / partial manifest SHA `54655E4ACDD81FAE561146866BCF6348C647FAE0D0C1C7582E1000DAE4FDA3E4` 原样保留。修复后 `build/0406f26ef29549278ca11606759b0f53` 的 seed、LocalHost、TableRuntime、raw verifier positives/negatives、shared build/provenance 均通过；Unity warmup 实际发现 `C04PlayerActionProbe.cs:498` 把 `TableVisibleEventBatch` 传给只收 identity/Observed batch 的 overload，native 1。其原始 Unity log SHA `655E94CC7957DE5D4513B84BB5B984C1C34FD875668B3AE8634FA19ADBA44F42`。补充 Client batch identity overload 后，`build/e6ccbf93bef74f6d9d0b1a6f0d4dcda6` warmup native 0（log SHA `507790C13966BC3607BFFFC969431837C69BA41E5E740806478968B18F269E99`），但执行会话中断，未产生闭包结果或稳定 summary，不能算通过。最新独占 run `build/b1d5db9ae0f64e11b08f08c9cac793fc` 等待 mutex 约 10 分钟后未获资源；按无法获锁边界结束自己的等待。该 root 的 `logs/runner.log` SHA `6F86F9C36F732952375F567A7C6FA144DD9A0C7B832F389DF118F5EABEB95AE1` 只记录 WAIT_MUTEX，尚无 Unity/PlayMode/Player/GAME 阶段。
- **证据路径与 SHA-256**：`build/0406f26ef29549278ca11606759b0f53/dotnet-results/LocalHost.xml` `6749AC56DAEEFFC0DCF4773F23E60A70DDA8535158AA2CB419511DF3EFEDA9FC`（138/138，0 skip/error，runner 中原生 0）；`dotnet-results/TableRuntime.xml` `5C55A43D714A80D015F4128540E5EA7AE9F4D22C54FF7CD53A4F17B4EC17A705`（483/483，原生 0）；`tool-verifier-matrix.json` `1ED7524A25808BD80ED414993AEFECEABF078D9EF1E080CF94B0C7EC2657B085`；`logs/unity-package-import-warmup.stdout.txt` `7ED64BC0DB6CA1E2365834102F388626C3ACA2408B8DB7DDF8DE3BAFEE232E53`；partial-run 原始文件保留在各自 GUID root。
- **修复要求与本轮映射**：NuGet importer 直接解析完整 JSON array；工具夹具现有双包正例逐包核 SHA256、SHA512、size/cache record 并校验复制。Probe 从同一个 `TableVisibleEventBatch` 序列化 after binding，并将同 accepted publication 的所有事件及 projection cursor 原样绑定。首失败都保留。此修复不覆盖 Unity/Player/GAME 缺口。
- **验收断言**：seed 28 包的 fresh build、精确四 PlayMode、Windows Player native 0 / errors 0 / strict marker、四个真实 GAME 各自完整原始证据并封存同一源码/包闭包。当前未达到；最新 run 仍在锁等待，PlayMode/Player/GAME=`NOT_RUN`。

### D03-R1-03 — MAJOR：typed raw 原件与事实逐项绑定

- **位置**：`dotnet/tools/C04PlayerActionVerifier/Program.cs`、`dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs`、`dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs`，及 Player 的 `RawEventBatchJsonBase64` capture。
- **复现命令**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/0406f26ef29549278ca11606759b0f53 -NugetSeedRoot E:\majiang-review\caller-c04-d03-r4-8c2e719a\build\caller-review\shared-seeded-packages -NugetSeedAuditPath E:\majiang-review\caller-c04-d03-r4-8c2e719a\build\caller-review\nuget-seed-audit.json`；该 run 对 fresh LocalHost 写出的四份 raw TOOL_FIXTURE 实际调用 fresh verifier，并逐项运行所列 negative mutations。
- **实际 vs 期望**：当前四份 .NET raw positives 均为 verifier native 0；9 项变体均 native 1 拒绝。完整验证现用 generated `PlayerSnapshot.Parser` 解码 baseline/after，`LocalTableRecoveryFactsReader` 解 authority 原件并逐实体重算 136 placements/hash；校验真实 raw receipt、binding、accepted event batch、request/scope/account、room/session/round/seat/epoch/version/cursor/event/typed payload 与 summary，并验证 Pon/Chi claimed tile 来源、Riichi 1000 点及一根供托、Kan 0..3 加 deadwall replacement 73。PLAYER/GAME 原始封装仍未能执行，不能把 TOOL_FIXTURE 当 GAME。
- **统一负例矩阵 / 证据**：`build/0406f26ef29549278ca11606759b0f53/tool-verifier-matrix.json` SHA `1ED7524A25808BD80ED414993AEFECEABF078D9EF1E080CF94B0C7EC2657B085`：四个 `TOOL_FIXTURE_LOCALHOST_CHANNEL` positives 全 PASS，9 个 negatives 全 REJECTED：MalformedBaselineProtobuf、InvalidAfterProtobuf、CoherentOwnerEpochFlatRewrite、CoherentBindingEpochFlatRewrite、ForeignRequestScope、ForeignRoom、PhysicalImageHashMismatch、AcceptanceSequenceMismatch、ClaimedTileStillInHand。四个原件和逐案 stdout/stderr 都在 `tool-channel-fixtures/` 与 `logs/verifier-*`。
- **验收断言**：拒绝非法 protobuf、缺失原件/hash不符、相干篡改 identity/request/epoch、物理位置伪造；四份真实 GAME 原始记录可用相同 helper 解码并通过。TOOL 矩阵通过；GAME 验收因 R1-02 锁等待未运行，故该组真实 Player 证据要求仍未关闭。

### D03-R1-04 — MAJOR：严格 marker 子项关闭，最终包闭环未完成

- **位置**：`tools/unity/run-p45-c04-player-actions.ps1` marker/source closure/post-build closure/seal checks。
- **复现命令**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerActions.Tests.ps1`。
- **实际 vs 期望**：工具套件 native 0：2 positive / 61 negative；严格 errors/bytes 整数类型、精确 target/scenes/build identity、manifest/owned files 与 stage gate 夹具均过。当前候选仍没有完成 resolved package 输入闭包及同候选 Windows Player/post-build closure/seal，故不能以工具门禁代替实际 package evidence。
- **证据路径**：最近完整工具输出 `build/4786affdd0c145ecbaba43c93a79c4eb`；Unity阶段目前仅有 `b1d.../logs/runner.log` 的 WAIT_MUTEX 记录。R4 strict-marker native fixture 关闭证据保留于上一轮原始 manifest，未改写。
- **修复要求与验收断言**：保留整数类型和 fail-closed guards；本候选完成 source/generated/resolved package pre/post 闭包绑定、native 结果及 marker/exe hash 绑定、writers 停止后的稳定 seal。当前包闭环未实跑，R1-04 总组仍未关闭。

### 本轮命令与范围

- 新鲜 LocalHost：restore/build/test 均原生 0；LocalHost XML 138/138/0 failed/skip/error。fresh C04 verifier build 0，四例 verify 0。TableRuntime XML 483/483/0 failed/skip/error。
- 工具夹具：`tools/tests/C04PlayerActions.Tests.ps1` 原生 0，2 positives / 61 negatives；此前双包 seed 夹具修复前后的输出和首次失败均留在各自 `build/<GUID>`。
- 共享插件：本轮 run `0406...` seeded fresh source build native 0；provenance、copied plugin guard 均 native 0。seed source 文件 SHA 与 caller audit 一致；不复用旧业务 DLL。
- Unity warmup 首次实际原生失败并已修编译错误；修后 run `e6cc...` warmup native 0，但未得到完整 runner seal；最新 `b1d...` mutex 未获。当前没有新的 PlayMode、Windows Player、GAME、PG、Android 或 CI 运行。
- 授权文件改动均限既有授权路径；当前代码提交为 `74819bb7`、`a8600669`、`ac841640`。本报告文件记录了当前未完成的 mutex 状态并随本轮最终 diff 提交；无生成物入库。
