# V5 G2-C04-D03 Player 动作确认返修交付（2026-11-02）

## 冻结候选与结论

- acceptedBase：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；本地分支基线：`734b3775fb7e658fcac33d3d6ce07cf195e96001`。
- 最后受测代码候选：`91a8c7ce613f35a8bf28368d2ae441a73ab5cb67`。本报告提交只更新本文，不改变该受测候选。
- 结论：**D03 当前不通过，未申请批准**。原 4 组缺陷均无新增 ID；R1-01 已关闭，R1-02 的 API/编译/PlayMode 子项已关闭。R1-02 Windows Player 的 fresh 构建已有先前候选 native 0 证据，但本候选被 shared-plugin NuGet SSL 错误挡在 Unity 前；四个 GAME 尚未在最新候选完成。R1-03 已修 canonical digest、单行 JSONL 和同 acceptance publication after-snapshot 绑定，但最后一项尚无最新候选实测。R1-04 严格 marker/稳定 seal 门禁及 59 个负例通过工具夹具；最新候选未走到 Unity package/build 阶段。
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