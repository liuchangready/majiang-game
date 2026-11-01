# V5 G2-C04 Player 场景分母与准备状态（2026-11-01）

## 范围与冻结状态

本文件与同名 JSON 只准备最终 Player 场景清单和执行交接，不实现业务或 harness，不代替 C03/C01/C02/C05，不宣称正式 C04 通过。只读冻结来源 `bae6b8b0c98c9ed80256373060a9e754d6fc4296`；`origin/v5` 接受基线 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。C03 候选未交付，本报告不选择 SHA。正式状态固定为 32 `NOT_RUN`、G7-02 `BLOCKED`；全部正式行都不是 PASS。

8 个父组/33 个子 case ID 是固定分母。新增观察必须记入最近的既有 row 缺口，不扩分母。所有后续候选/Player/设备证据都要绑定 source/build/package/player identity、room/session/seat/role/round/stateVersion、event sequence/cursor、decision/control epoch、connection/binding epoch、scope、原 request ID 和 canonical payload SHA。

## 固定父组

| 父组 | 固定子 case 数 | 场景 |
|---|---:|---|
| C04-G1 | 4 | Navigation, round and match progression/settlement |
| C04-G2 | 4 | Real Pon / Chi / Riichi / Kan |
| C04-G3 | 6 | Three cheats, cost, authorization and 136 physical entities |
| C04-G4 | 3 | Takeover, leave/re-enter, explicit claim and stale epochs |
| C04-G5 | 3 | Four seats, spectator privacy and role-switch cleanup |
| C04-G6 | 8 | Eight recovery classes plus original-request reconciliation/no duplicate side effect |
| C04-G7 | 2 | Offline full match and service-entry rejection of LOCAL settlement assets |
| C04-G8 | 3 | Final effective visuals, mouse/keyboard/touch, aspect ratio and safe area |

## 执行与结果合同

每个 JSON row 固定记录触发步骤、初态/fixture、真实客户端入口、成功与拒绝断言、身份绑定、证据、命令键、Player/设备要求、C03依赖、状态、缺口和实际实现符号。拒绝断言必须比较提交前后版本、事件、费用/账本与136实体。不能用 private reflection 写结果、fixture 日志/截图黑帧、native exit 或空断言代替游戏通过。

### G6 八类恢复结果（不可互换）

| ID | 故障 | 冻结预期 | 原 request 处置 | 副作用合同 |
|---|---|---|---|---|
| G6-01 | None / 正常退出 | RECOVERED | 对账原 primary receipt；Found 不 resend；Unknown 仅原 scope/key eligibility 下可 Retry | 原 Discard 提交一次效果，恢复对账增量为0 |
| G6-02 | None / 强杀 | RECOVERED | 同上，另需 exact process FORCE_KILLED 证据 | 同上 |
| G6-03 | TruncatePrimary | RECOVERED_FROM_BACKUP | 对账包含原 receipt/state 的有效 backup | 原效果一次，对账增量0 |
| G6-04 | BadChecksumPrimary | RECOVERED_FROM_BACKUP | 对账包含原 receipt/state 的有效 backup | 原效果一次，对账增量0 |
| G6-05 | MissingPrimaryWithBackup | RECOVERED_FROM_BACKUP | 对账有效 backup 原 receipt/state | 原效果一次，对账增量0 |
| G6-06 | BothMissing | FRESH_START | `NOT_REPLAYED_IN_NEW_SCOPE`；绝不把旧命令注入新房间 | 旧 request 对新 scope 的增量为0；新房正常初始化单独计算 |
| G6-07 | BothBad | REJECTED | `NOT_APPLICABLE_SAFE_REFUSAL`；不得 Ready/query/retry/submit | 状态/事件/outbox/ledger/entity 零增量 |
| G6-08 | IncompatiblePrimary | REJECTED（不允许 backup 备选分支） | `NOT_APPLICABLE_SAFE_REFUSAL`；不得 Ready/query/retry/submit | 状态/事件/outbox/ledger/entity 零增量 |

G6-01..05 使用明确的真实 Discard request，记录原 canonical requestId/scope/API/digest、Applied receipt 及恢复版本中的命令效果：一个 `TileDiscarded`、一次手到河的实体移动、提交时 stateVersion +1、ledger delta 0、牌实体始终为 0..135。重复查询/安全重放必须新增零效果；仅在同原 scope/key/action 符合 eligibility 且 lookup=Unknown 时允许 `RetryPendingAsync`，不得用新 Submit 冒充 reconciliation。G6-06/07/08 不得强制产生一条新业务副作用。冻结策略来自实际 `Get-ExpectedOutcome`：BothMissing=FRESH_START、BothBad=REJECTED、IncompatiblePrimary=REJECTED。

`ITableChannel` 仅有 `OpenAsync/SendAsync/ReceiveAsync/CloseAsync`，没有 Submit/QueryReceipt。真实 client API 是 `TableSession.SubmitAsync`、`ReconcilePendingAsync`、`RetryPendingAsync`；本地 receipt wire handler 是 `LocalTableRuntime.HandleReceiptQueryAsync`。重复 `SubmitAsync` 对 tracked identity 可只返回 existing，不等于 reconciliation。JSON 的 G6 入口按分支区分恢复查询与安全拒绝。

### G3-06 修正

G3-06 是授权缺失与余额不足的 cheat 拒绝：Player cheat UI -> TablePresenter/TableCommandIntentFactory -> TableSession.SubmitAsync -> LocalTableChannel。两个不同 payload 使用不同 request ID；绑定 scope、座位、授权/余额、版本、receipt、私有 ledger 与136实体；拒绝应零版本/事件/账本/实体增量，并有合法授权/足额对照。现有 `.NET` 组件证据为 `LocalHostCheatTests.TileMarkIsSubmittedThroughTheClientAndProjectsPrivately`、`SleeveBloomAndBeamSwapAreSubmittedThroughTheClient` 和 `N1bCheatExecutionTests.tile_mark_from_the_wrong_seat_is_rejected_with_zero_side_effects`，不构成最终 Player 证明。C02 recovery factsEqual 不属于此 row 证据。

## 证据层与历史溯源

以下是既有历史/组件证据，均非本轮重跑，不能覆盖 C03 未来新树；formal C04 仍 NOT_RUN/BLOCKED。层名统一按 JSON catalog。

| 层 | 可引用证据 | 解释边界 |
|---|---|---|
| DOTNET_PURE | C01 accepted report 1,048/1,048，report SHA `24F867B86CCBBE08200817CE792A336C059EE3F5C70068F8EF9AC26BB7E9C6E9`；B2TerminalSettlementTests source SHA `400D0C0C3B7AC4F926E21CF0259F8BE2E4775F088311ADE728E70567D991123B` | C02 PowerShell 27/27 不属于 .NET；TableService 旧测试直接 Actor.SubmitAsync，不经过 handler/channel 且无显式 LOCAL provenance。 |
| SCRIPT_TOOL_FIXTURE | C02 27/27 native0 summary SHA `DFB0808D9DA9571C56832C353A4110F07F4B78449C8A23F369DD23E9420B2D5B`；C05 29/29 SHA `7FED2C155FEF311ED4A143B72A4506280876465AFAEA981A6A1042A5D80CEC5B`；C05 gates 28/28 SHA `ED70FE7C2E32BC69359AADBA0A716F0EB8F4EBBAE1818C759AA4910876A96569` | 工具参数/夹具/门禁，不是业务.NET、Player 或设备结果。 |
| UNITY_EDITMODE | P4.3 report SHA `C91AECCC9EA97B59A74A4309D686A60FDF57B51A0F23D3A5EFAC83C2C9ECEB3E`；P4.4 report SHA `8DE8A2FFB67EE8731CB8079773E716083FC6C98073CF77CB867469ACD7E1D259` | 场景/组件，不是最终 Player。 |
| UNITY_PLAYMODE | C01 7/7 native0 XML SHA `3777ED8FDBE7CF1887FBEC44777009E3BFCD626C951BDD436820F4FF376A1F4F` | 不绑定未来 C03。 |
| WINDOWS_PLAYER | C01 12 distinct rounds、12 terminal commits、1 match_finished/WestCompletion，native0/tool0，summary SHA `575BF95B811B5DEE316FBB82F9A02350D5458EC3F5E5FA0BB8A2D266A84C3539`；C02 8/8 native0 recovery facts SHA `18F3EFB34F68FE8277CD9F4A0D8DFD7510F95EB61A4DB3BCC9B56D2A4EE6D040`；旧 Pon Applied 但 PonDeclared 缺失，native31/tool10，source `0d338ec79a9ca5d01897d7c1c70ec2b0e2f73156` | C01 Player source.json 为 `f8ed9090` 加声明的 R4 dirty patch；C02 为 R5 code `6cae2910bd8afde984f5b5efd4b3e5c4b8fc1507`、receiver `699bbaf4989245c487cb6b40f1e6418153242882`、tested integration `c43187efba69c03753f952276900af2c52287246`，Player 包含21项声明生成文件dirty inputs、diff SHA `9819741E1D827A9F2F6B50B942025C19F2CF54189CD1BB3529041028987C2272`。均非C03。 |
| ANDROID_DEVICE | C05历史APK 52,142,409 bytes，SHA `0506AEEA2593B7E70C7B2796CE353D3B401A12ADCBF1FFFCC24A0464481C8D6B` | pinned adb 无设备；安装/游戏/视觉/触控 NOT_RUN。 |
| IOS_DEVICE | 外部只读计划路径及捕获 head/hash 见 JSON evidenceSources | 不是本仓库文件，也不是设备证据；iOS NOT_RUN。 |

H-C02 分层拆分：实现基线 `6750b4362063169cd435302e0cc9c6685325bbe5`，实际R5 code `6cae2910bd8afde984f5b5efd4b3e5c4b8fc1507`，receiver final `699bbaf4989245c487cb6b40f1e6418153242882`，tested integration `c43187efba69c03753f952276900af2c52287246`，accepted/frozen integrated head `bae6b8b0c98c9ed80256373060a9e754d6fc4296`。历史 PowerShell 27/27 只属 SCRIPT_TOOL_FIXTURE；GAME 8/8 只证明记录的恢复事实，不证明原request reconciliation。四份接受报告的真实SHA和每项source metadata在JSON `historicalEvidence`。外部计划为 `D:/majiang/doc/v5-重构/V5-G2-CLOSEOUT-TASKS-20261031.md`，当前只读捕获 head `6c3573620f61e8cbb6a13c8c4b81abef0b173fd9`；外部计划文件在捕获时为untracked，SHA `DA6D65038637393CE3AB36B101C888E6B2288A7FEA05D680A4492F53B193B865`，不属于该HEAD内容，不复制进冻结仓库。

## 现有命令、真实入口与限制

本轮仅核对路径、符号、参数声明和引用，以下命令没有执行。占位符在未来绑定；所有输出、日志、TEMP/TMP 指向本树 ignored `build/<GUID>`。

- .NET: `dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore`; `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-restore`.
- G7-02 现有部分负例：`dotnet test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring`。其 source test 使用真实 TableServiceTestHost 但直接 Actor.SubmitAsync，不能证明真实LOCAL wire ingress。
- P4.3: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-p43-scenes.ps1 -UnityExe <final-C03-Unity.exe> -ProjectPath unity/Mahjong.Unity -OutputDir build/c04/<GUID>/p43 -LogDir build/c04/<GUID>/logs-p43 -TempDir build/c04/<GUID>/temp-p43`.
- P4.4: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-p44-views.ps1 -UnityExe <final-C03-Unity.exe> -ProjectPath unity/Mahjong.Unity -OutputDir build/c04/<GUID>/p44 -LogDir build/c04/<GUID>/logs-p44 -TempDir build/c04/<GUID>/temp-p44`.
- Control UI: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-p45-control-ui.ps1 -EvidenceRoot build/c04/<GUID>/control -SkipPlayer`; SkipPlayer 是真实参数且不覆盖Player控制路径。
- Full match: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir build/c04/<GUID>/full-match -UnityPath <final-C03-Unity.exe> -DotnetPath <E-drive-dotnet.exe> -TimeoutSeconds 5400`。其Mode没有Pon/Chi/Riichi/Kan或cheat选择器。
- Recovery collector/verifier: JSON `commandCatalog`列出逐项实参；保存故障支持None/TruncatePrimary/BadChecksumPrimary/IncompatiblePrimary/MissingPrimaryWithBackup/BothMissing/BothBad。收集+离线manifest校验没有恢复后同request channel reconciliation 命令。
- Android tool `tools/unity/run-p45-android-game.ps1` 使用 `-Action Devices|DeviceInfo|Install|Start|Stop|Capture`，不是Mode。仅设备识别与最终APK绑定后才可执行；fixture和APK本身不是设备游戏证据。

## 完整已知缺口

六项产品/证据缺口 C04-D01..D06 与本轮文档缺陷 C04-PREP-R1-01..03 分开。完整缺陷含复现、实际/期望、证据、修复、验收断言与拟议文件/命令在JSON `knownDefects`。提案文件尚不存在是预期，本轮没有创建。

| ID/级别 | 范围 | 下一步验收要点 |
|---|---|---|
| C04-D01 BLOCKER | C04-G6-01..08; real TableSession/ITableChannel and TableSessionHandler receipt query | For G6-01..05 assert exact original receipt identity and command effect vector once, then zero delta on repeat; for G6-06 no old-scope submit; for G6-07/08 no Ready/action and zero deltas. Preserve per-case manifest/hash/native exit. |
| C04-D02 MAJOR | C04-G7-02; TableService wire ingress to TableActor | Wire test captures envelope/source/scope/request, rejection code, unchanged version/events/ledger/entities and accepted server-origin control; command must traverse handler/channel, not Actor directly. |
| C04-D03 MAJOR | C04-G2-01..04; tools/unity/run-p45-full-match.ps1 and Player action driver | Four distinct Player case records bind request/scope/seat/round/version and Applied receipt to PonDeclared/ChiDeclared/RiichiDeclared/KanDeclared (plus replacement/chankan semantics where applicable); missing action fails. |
| C04-D04 MAJOR | C04-G3-01..06; cheat UI/authority and Player observation | Three distinct authorized cases assert exact configured fee/ledger and 136-ID multiset; unauthorized and insufficient balance each use distinct request IDs and assert reject plus zero event/version/ledger/entity delta. |
| C04-D05 MAJOR | C04-G5-01..03; player/spectator serialized streams, role binding and caches | For each seat and spectator scan raw serialized public payloads for secret sentinels; repeat after each role switch and inject delayed prior-binding delivery; assert cleared caches and no visible stale data. |
| C04-D06 MAJOR | C04-G8-01..03; final Windows Player, Android device and visual/input evidence | Retain original screenshot/video, package/player/device identity and display geometry; assert each physical input maps to expected visible action/receipt. No black frame or native exit alone counts. |

### 有限文件边界与未来命令

| 缺口 | 文件类型/路径 | 执行或观察合同 |
|---|---|---|
| C04-D01 | PROPOSED_NEW: `dotnet/tests/Mahjong.LocalHost.Tests/C04RecoveryRequestReconciliationTests.cs` - same canonical request recovery/reconciliation contract<br>PROPOSED_NEW: `tools/unity/run-p45-c04-recovery-reconciliation.ps1` - bind each frozen recovery row to Player and structured request/effect observations | PROPOSED after C03: dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~C04RecoveryRequestReconciliationTests; then powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-recovery-reconciliation.ps1 -PlayerPath <bound-final-player> -EvidenceRoot build/c04/<GUID>/recovery-request <br>验收：For G6-01..05 assert exact original receipt identity and command effect vector once, then zero delta on repeat; for G6-06 no old-scope submit; for G6-07/08 no Ready/action and zero deltas. Preserve per-case manifest/hash/native exit. |
| C04-D02 | EXISTING_REPO_TEST_FILE: `dotnet/tests/Mahjong.TableService.Tests/B2TerminalSettlementTests.cs` - add a distinct wire-ingress LOCAL negative and server-origin positive control; stop if trusted origin injection requires production changes<br>NO_TRACKED_SOURCE_CHANGE: `none unless the test demonstrates a missing trusted-origin guard` - do not presume or silently expand production scope | Existing partial only: dotnet test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring. Proposed wire-ingress row has no command until implemented; expected observation is explicit LOCAL rejected before commit plus valid trusted server-origin control. <br>验收：Wire test captures envelope/source/scope/request, rejection code, unchanged version/events/ledger/entities and accepted server-origin control; command must traverse handler/channel, not Actor directly. |
| C04-D03 | PROPOSED_NEW: `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerActionScenarioTests.cs` - four real action request/result/event assertions<br>PROPOSED_NEW: `tools/unity/run-p45-c04-player-actions.ps1` - bounded Player action selector and structured evidence | PROPOSED after C03: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -Action Pon,Chi,Riichi,Kan -PlayerPath <bound-final-player> -EvidenceRoot build/c04/<GUID>/actions; this command is not currently executable. <br>验收：Four distinct Player case records bind request/scope/seat/round/version and Applied receipt to PonDeclared/ChiDeclared/RiichiDeclared/KanDeclared (plus replacement/chankan semantics where applicable); missing action fails. |
| C04-D04 | PROPOSED_NEW: `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerActionScenarioTests.cs` - authorized/underfunded cheat Player outcomes plus exact fee and 136 entity observations<br>PROPOSED_NEW: `tools/unity/run-p45-c04-player-actions.ps1` - bounded three-cheat scenario selection and receipt/state capture<br>EXISTING_REPO_TEST_FILE: `dotnet/tests/Mahjong.TableRuntime.Tests/N1bCheatExecutionTests.cs` - read-only reference for authorization negative; do not reopen accepted fix | PROPOSED after C03: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -Action TileMark,SleeveBloom,BeamSwap,Unauthorized,InsufficientBalance -PlayerPath <bound-final-player> -EvidenceRoot build/c04/<GUID>/cheats; this command is not currently executable. <br>验收：Three distinct authorized cases assert exact configured fee/ledger and 136-ID multiset; unauthorized and insufficient balance each use distinct request IDs and assert reject plus zero event/version/ledger/entity delta. |
| C04-D05 | PROPOSED_NEW: `dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs` - serialized four-seat/public sentinel scan and role/binding assertions<br>PROPOSED_NEW: `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs` - role-switch cache cleanup and delayed stale-binding delivery<br>PROPOSED_NEW: `tools/unity/run-p45-c04-privacy-matrix.ps1` - bounded stream capture, sentinel scan and evidence manifest | PROPOSED after C03: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -PlayerPath <bound-final-player> -EvidenceRoot build/c04/<GUID>/privacy; assert zero protected sentinels on public stream and reject stale binding after each role switch. <br>验收：For each seat and spectator scan raw serialized public payloads for secret sentinels; repeat after each role switch and inject delayed prior-binding delivery; assert cleared caches and no visible stale data. |
| C04-D06 | NO_TRACKED_SOURCE_CHANGE: `none` - manual device/visual/input evidence; no source harness required for this prep<br>GENERATED_EVIDENCE_OUTPUT: `build/c04/<GUID>/visual-input/evidence.json and build/c04/<GUID>/visual-input/screens/` - record artifact/player/device/display/viewport/insets/input event-to-receipt and unmodified screenshots/video; Windows/Android separate, iOS NOT_RUN without hardware | Manual, no tracked source command: capture actual final Windows Player and identified Android device separately; evidence.json records build/package/device/display/viewport/insets and input event -> matching receipt/action. iOS remains NOT_RUN absent device. <br>验收：Retain original screenshot/video, package/player/device identity and display geometry; assert each physical input maps to expected visible action/receipt. No black frame or native exit alone counts. |

D03/D04未来Player命令共用所列脚本，但当前不存在，需单独审查有限实现范围。D06是人工/设备证据模板，没有新增源文件。已存在路径、未来提案、外部只读引用分别带kind，不能混为缺失仓库文件。

## C03接受后的有限执行顺序

1. 调用方独立接受并冻结一个C03 SHA后，本矩阵再按SHA绑定；本准备稿不预选。输出/日志/临时目录使用E盘ignored `build/<GUID>`，并核对唯一owner/mutex。
2. 先定向/全量.NET；后串行Unity P4.3、P4.4与control UI，共用 `Local\MahjongV5P45UnityValidation`，不得并发重资源阶段。
3. 再跑候选Windows full-match、G6逐故障收集/校验、恢复request对账、真实动作/cheat、隐私stream/role切换及LOCAL服务入口专项；不覆盖C01/C02/C05范围。
4. 有已识别Android设备且最终APK身份匹配才跑设备操作；iOS保持单列。G3远端整场和G4拓扑仍留在其批准范围。
5. 首个未解释失败、native非零、case缺失、身份/hash不符、旧binding泄漏或意外mutex争用即停，保留首失败、逐例native、manifest/hash；不skip、不用旧DLL/黑帧/空测试/放宽断言造绿。

## 本轮R1返修与静态校验

R1复核原件只读于caller隔离树：`E:/majiang-review/caller-c04-r1-review-b17187ab/build/caller-review/REVIEW.txt` SHA `9901C951EB027D99B3AA0437B879EC1427FCCA8AC491DD76F48E76C551ABACF6`；证据CSV SHA `A2C684054EF80EC77EBF67E94F315995DC9A347341703E5F4800DE6C230ED1CB`；独立语义检查原结果SHA `7A30025AC9870356C02AB1060365C3AEC5B9A296DCE6490BC1870D94697E8849`。原review 13项检查5通过/8未通过归并为3项文档缺陷；caller已接受的19/19结构核验仍保留为历史事实，不替代本轮语义检查。

本轮逐条修复映射：

- C04-PREP-R1-01 MAJOR：G6八行按collector精确结果拆成五个恢复、一个新局、两个拒绝；统一“同request安全处置”而不强迫拒绝产生副作用；真实API改为TableSession方法与LocalTableRuntime本地receipt handler；G3-06恢复为授权/余额负例。
- C04-PREP-R1-02 MAJOR：H-C02拆开base、R5实现、receiver、tested integration、accepted head和dirty Player输入；C02 27/27移到SCRIPT_TOOL_FIXTURE；DOTNET_PURE层名统一；修复Combined recovery报告真实路径；计划书显式分类external_readonly并记录捕获hash/head；C01措辞分开12个round、12个terminal commit与一次match_finished/WestCompletion。
- C04-PREP-R1-03 MINOR：D01-D06逐项给出kind标注的最小未来文件/无源文件证据边界、公开入口、拟议命令/观察、验收断言与明确排除范围；未来文件没有创建。

本轮静态审计脚本 `build/c04-census-r1-c664f2a42c1a4d1bbfb1f64209731855/audit-r1.ps1` 共17/17通过，PowerShell native exit 0；日志 `audit-r1-final.log` SHA256 `72DB1B059EC940E46DD70B106D00EF2FDE24B6D9A4C9B72D7DEC1EA0D96D839F`。首轮静态审计器自身的语义误报输出保留在同目录 `audit-r1.log`，不作为代码/产品失败结论。只运行JSON/资料/路径/符号/参数和双文档映射检查；没有Unity、Player、adb、设备、网络拓扑、CI或Unity mutex运行。正式状态仍32 NOT_RUN + 1 BLOCKED；C03 candidateSha仍null。
## 调用方独立复核（准备范围）

接收方交付 `1df3b21df639c46b2c026cd86421b4dc3774d834` 已在独立工作树 `E:/majiang-review/caller-c04-r2-closeout-681e927d` 复核。原审计器复制到 caller ignored 目录后实际执行 17/17、native exit 0；调用方另行执行 14/14 静态检查、native exit 0，包含五种错误合同变体均被拒绝、原 R1 矛盾被拒绝、33 行双文档 ID 集合、169 个实现引用及证据来源、四份历史报告与源文件 SHA、外部计划捕获 SHA、未来提案未创建和正式状态下限。以上是文档与来源静态检查，不是 31 个游戏用例。

调用方仅修正文档呈现：未来文件表的生成表达式改为明确路径，Markdown mutex 使用单个反斜杠，并将 D01 location 的本地 receipt handler 写明。初次独立检查 12/14、native exit 1 的原件保留；修正后 14/14。证据位于该树 `build/caller-review/`，`independent-results.json` SHA256 `4E40A70C86EB78148C3875733D4F327DF96203D5024498921651EE41244CC2F6`，`independent-final.log` SHA256 `A6FDCF66121F3C9011B85A1E92F335CCE1D38838D18D718ABC214B796081AB8A`，复跑 `receiver-audit-final.log` SHA256 `72DB1B059EC940E46DD70B106D00EF2FDE24B6D9A4C9B72D7DEC1EA0D96D839F`。未运行 Unity、Player、adb、设备或 CI，也没有获取 Unity mutex。

C04-PREP-R1-01/02/03 在本准备范围关闭；只接受这两份准备文档。8 组/33 行仍为 32 NOT_RUN、1 BLOCKED、0 PASS，C03 SHA 仍为空。D01–D06 是未来正式执行/实现缺口；拟议文件及参数不是现有能力，后续实现须单独限定范围、复核，并重新绑定实际候选版本。C03、正式 C04、设备验收和 G2–G5 不因本准备任务通过。

## 33个固定case索引

| ID | 场景 | 实际入口 | 命令键 | 状态 |
|---|---|---|---|---|
| C04-G1-01 | Menu to local room, ready/wall setup and first turn | Actual Windows Player UI -> AppRouter.StartLocalGameAsync -> LocalTableChannelFactory -> TableSession -> TablePresenter. | UNITY_P43, PLAYER_FULL_MATCH | NOT_RUN |
| C04-G1-02 | Real round completion by win and exhaustive draw | Windows Player UI through TablePresenter/ITableCommandSink and actual local authority channel. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G1-03 | Renchan and next round creation | Player UI and actual TableSession/local authority. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G1-04 | Whole match settlement and final rankings | Player LocalFullMatchPlayerDriver through production FullMatchExecutionBoundary and real LocalHost. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G2-01 | Pon from a live response window | Player UI -> TablePresenter/command sink -> TableSession -> LocalTableChannel. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G2-02 | Chi from a live response window | Player UI/table command path. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G2-03 | Riichi declaration and double-riichi facts | Player UI/table command path. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G2-04 | Kan declaration, replacement draw and chankan window | Player UI through production TableSession/local authority. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G3-01 | TileMark (落汗) successful authorized execution | Player cheat UI -> normal command channel -> authoritative ledger/round. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G3-02 | SleeveBloom (袖里藏花) successful authorized execution | Player cheat UI and actual authority command. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G3-03 | BeamSwap (偷梁换柱) successful authorized execution | Player cheat UI -> actual authority command. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G3-04 | Exact fee debit and private ledger change for each successful cheat | Player cheat controls and server-side/local authority validation. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G3-05 | 136 physical entity invariant across all three cheats | Actual authority state and real Player event/receipt observation; never private reflection writes. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G3-06 | Unauthorized and insufficient-balance cheat rejection | Player cheat prompt -> TablePresenter/TableCommandIntentFactory -> TableSession.SubmitAsync -> actual LocalTableChannel/authority response; use separate request IDs for unauthorized and insufficient-balance payloads. | PLAYER_FULL_MATCH (general runner only; no cheat selector), Independent bounded Player scenario driver required | NOT_RUN |
| C04-G4-01 | Disconnect takeover and stale AI decision rejection | Player UI/transport and actual local runtime; control component path via test-p45-control-ui. | UNITY_CONTROL, PLAYER_FULL_MATCH | NOT_RUN |
| C04-G4-02 | Leave/re-enter same room and data without unsafe duplicate action | Player routing through AppRouter/SessionCoordinator and actual local host. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G4-03 | Explicit claim reclaims control and clears prior epoch | Player control UI via normal channel. | UNITY_CONTROL, PLAYER_FULL_MATCH | NOT_RUN |
| C04-G5-01 | Four player seats see own hand and not opponent private state | Player stream/client mapper/TablePresenter; protocol/public channels as configured. | UNITY_P43, PLAYER_FULL_MATCH | NOT_RUN |
| C04-G5-02 | Spectator public stream excludes hidden and private values | Spectator channel/public stream and SpectatorPresenter; not private player API. | UNITY_P44, PLAYER_FULL_MATCH | NOT_RUN |
| C04-G5-03 | Role switch clears caches and old binding cannot publish | Client binding lifecycle -> mapper/presenter/role router. | UNITY_P44, PLAYER_FULL_MATCH | NOT_RUN |
| C04-G6-01 | Normal restart from valid primary | Windows Player startup -> TableSession.ReconcilePendingAsync -> receipt query envelope over ITableChannel.SendAsync/ReceiveAsync -> LocalTableRuntime.HandleReceiptQueryAsync. RetryPendingAsync is permitted only after Unknown and original same-scope request remains eligible; never submit a new command to emulate reconciliation. | PLAYER_RECOVERY -Case Normal -SaveFault None, PLAYER_RECOVERY_VERIFY, Proposed post-C03 request reconciler: tools/unity/run-p45-c04-recovery-reconciliation.ps1 | NOT_RUN |
| C04-G6-02 | Strong kill and cold restart | Windows Player startup -> TableSession.ReconcilePendingAsync -> receipt query envelope over ITableChannel.SendAsync/ReceiveAsync -> LocalTableRuntime.HandleReceiptQueryAsync. RetryPendingAsync is permitted only after Unknown and original same-scope request remains eligible; never submit a new command to emulate reconciliation. | PLAYER_RECOVERY -Case Force -SaveFault None, PLAYER_RECOVERY_VERIFY, Proposed post-C03 request reconciler: tools/unity/run-p45-c04-recovery-reconciliation.ps1 | NOT_RUN |
| C04-G6-03 | Truncated primary plus valid backup | Windows Player startup -> TableSession.ReconcilePendingAsync -> receipt query envelope over ITableChannel.SendAsync/ReceiveAsync -> LocalTableRuntime.HandleReceiptQueryAsync. RetryPendingAsync is permitted only after Unknown and original same-scope request remains eligible; never submit a new command to emulate reconciliation. | PLAYER_RECOVERY -Case Normal -SaveFault TruncatePrimary, PLAYER_RECOVERY_VERIFY, Proposed post-C03 request reconciler: tools/unity/run-p45-c04-recovery-reconciliation.ps1 | NOT_RUN |
| C04-G6-04 | Bad checksum primary plus valid backup | Windows Player startup -> TableSession.ReconcilePendingAsync -> receipt query envelope over ITableChannel.SendAsync/ReceiveAsync -> LocalTableRuntime.HandleReceiptQueryAsync. RetryPendingAsync is permitted only after Unknown and original same-scope request remains eligible; never submit a new command to emulate reconciliation. | PLAYER_RECOVERY -Case Normal -SaveFault BadChecksumPrimary, PLAYER_RECOVERY_VERIFY, Proposed post-C03 request reconciler: tools/unity/run-p45-c04-recovery-reconciliation.ps1 | NOT_RUN |
| C04-G6-05 | Missing primary plus valid backup | Windows Player startup -> TableSession.ReconcilePendingAsync -> receipt query envelope over ITableChannel.SendAsync/ReceiveAsync -> LocalTableRuntime.HandleReceiptQueryAsync. RetryPendingAsync is permitted only after Unknown and original same-scope request remains eligible; never submit a new command to emulate reconciliation. | PLAYER_RECOVERY -Case Normal -SaveFault MissingPrimaryWithBackup, PLAYER_RECOVERY_VERIFY, Proposed post-C03 request reconciler: tools/unity/run-p45-c04-recovery-reconciliation.ps1 | NOT_RUN |
| C04-G6-06 | Both primary and backup missing | Windows Player recovery/startup classifier and visible safe-start UI. The old request is classified NOT_REPLAYED_IN_NEW_SCOPE; no TableSession Submit/Retry or receipt query is issued against the new scope. | PLAYER_RECOVERY -Case Normal -SaveFault BothMissing, PLAYER_RECOVERY_VERIFY, No request Submit after FRESH_START or REJECTED outcome; observe zero-send boundary | NOT_RUN |
| C04-G6-07 | Both files bad | Windows Player startup recovery refusal UI/state. Expected REJECTED; no Ready gameplay session and no Submit, RetryPendingAsync, ReconcilePendingAsync, or receipt query. | PLAYER_RECOVERY -Case Normal -SaveFault BothBad, PLAYER_RECOVERY_VERIFY, No request Submit after FRESH_START or REJECTED outcome; observe zero-send boundary | NOT_RUN |
| C04-G6-08 | Incompatible primary is rejected per frozen recovery policy | Windows Player recovery classifier and refusal UI/state. Expected REJECTED; no Ready session, no backup fallback, no Submit/RetryPendingAsync/ReconcilePendingAsync or receipt query. | PLAYER_RECOVERY -Case Normal -SaveFault IncompatiblePrimary, PLAYER_RECOVERY_VERIFY, No request Submit after FRESH_START or REJECTED outcome; observe zero-send boundary | NOT_RUN |
| C04-G7-01 | Offline complete match remains playable without network | Player UI -> local composition -> LocalTableChannel/LocalTableRuntime. | PLAYER_FULL_MATCH | NOT_RUN |
| C04-G7-02 | Service/settlement authority rejects LOCAL assets | TableServiceTestHost connects a player, but accepted test RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring calls room.Actor.SubmitAsync directly; it bypasses the TableSessionHandler wire command ingress. Actual service route candidates: dotnet/src/Mahjong.TableService/TableSessionHandler.cs:DispatchCommandAsync and TableActor.SubmitSettlementAsync. | dotnet test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring (existing partial authority rejection only; not the required wire ingress), Independent service-ingress test/driver required for explicit LOCAL provenance | BLOCKED |
| C04-G8-01 | Effective final visual quality and layout at supported aspect ratios | Real Windows Player visual display; Android must be separately measured for its device row. | ANDROID_C05_TOOLS_ONLY for future authorized actual capture; no Windows visual automation currently proves semantics | NOT_RUN |
| C04-G8-02 | Mouse and keyboard controls across critical gameplay flow | Actual Player event system/Input System, not invoking presenter methods. | Manual final Player run; no existing command automates physical keyboard/mouse semantics | NOT_RUN |
| C04-G8-03 | Touch targets, touch navigation and device safe area | Actual Android application and touch screen; ADB records process/install/capture but does not prove gameplay by itself. | ANDROID_C05_TOOLS_ONLY (verify exact integrated interface and device identity before use) | NOT_RUN |
