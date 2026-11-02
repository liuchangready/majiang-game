# G2-C04-D05 隐私通道交付记录

日期：2026-11-02。执行者：GPT-6-Luna。独立复核者：当前调用方（父代理）。本记录供独立验收，不表示正式 G2/C04 批准。

> **R1 返修状态（2026-11-02，B：G2-C04-D05-R1-ACCEPTANCE-MATRIX）**：R1-04 runner 已在独立组件层批量修复并由真实短负例验证；R1-03 的合法非空袖牌/费用、慢 consumer 真实队列、旧 raw receive、恢复原请求对账场景已实现并通过 .NET 回归。最终 Unity/Windows 同候选同包矩阵仍 `BLOCKED_ON_PRIVACY_AND_RECOVERY`，原因是 Luna 的 R1-01 raw 解码 verifier 与 A 的 R1-02 恢复/生命周期修复尚未集成冻结。D05 未完成、未批准；旧通过证据不冒充新候选通过。下文“R1 前历史状态”保留，R1 新候选状态见新增章节。

## 交付身份与证据封存

- 工作树：`E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1`；分支：`wt/v5-g2-c04-d05-luna6-20261102-c04d05a1`。
- 接受基线：`957ad475e90a99e83fd7c413c0cbe6e30c00620f`；实现起点任务卡提交：`e871905ae2e2dd816d1172a622ac824f18dd4c6f`；最终代码 HEAD：`47ecf6a6a888f3e167a348393d8aa200b1230282`。实现提交依次为 `cd1428ff0afdbccd97f42dffe55c3bc7ffda2dd2`、`ecbc516499a8cac46f2eab811ab9a83cbbcaea14`、`5a4a2db0e092b6ee131320ccf4891620d4541872`、`47ecf6a6a888f3e167a348393d8aa200b1230282`。
- 从任务卡提交到代码 HEAD 共改 24 个 tracked 文件（2324 insertions、81 deletions），未编辑 engine/domain、wire/client、TableActor 或 asmdef。额外授权文件为 `unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs`：仅将 `_bannerRoot`、`_bannerText` UI fixture 注入由 `UnityEditor.SerializedObject` 改为限定字段反射；原 PlayMode 测试与断言保留，解决 Standalone Player 测试编译错误。
- 源码身份清单：对 HEAD `47ecf6a…` 下 2,496 个 tracked 文件逐文件 SHA-256，清单 `build/7b45f498504e47c199407b14d50bf006/tracked-source.sha256`，清单 SHA-256 `32AC4A988CEE5642F9737A740BAF9F8B380D8E627C7913A219BA05F662112F32`。
- Windows Player 包清单：205 个文件，`build/7b45f498504e47c199407b14d50bf006/player-package.sha256`，清单 SHA-256 `74D6BF6AAC6F543461AC78487A325F2681F801DF72C8FF99B8DF0B288B040BCE`。`Mahjong.exe` 实际为 **667,136 字节**，SHA-256 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；旧 marker 的 `bytes=128,346,555` 是整包/构建 totalSize 口径，不是 exe 长度。旧 marker/日志不改写，新 runner 用 `playerBytes` 与 `packageBytes` 分列。
- Player build marker/log SHA-256 `4359E653B746EB0890A877B9B06D2B71624FD540E39C873C05B00F25E896DE6A`；Player Unity editor log SHA-256 `3E6EA6115D102057B72D05F905CE766F54F58D2513B7C7146435A8E2F04B0C67`；插件来源 manifest SHA-256 `D7F8AB3638B211F82B8FBA25338ACF3E9253FF51D299008E8BC14012E44D7A8A`。
- .NET 全量 xUnit v3 XML：154/154，failed/errors/skipped/not-run 均为 0，实际运行 30.146s；`build/22df1f8f5f064d1bb77852adf2a5230f/results/localhost.xml` SHA-256 `DD1B86F129FB437E8CA47BCBE7659077EDB13262B5BDFB0224484673A22BFE46`。按 `-parallelMode none` 串行执行真实测试 DLL；非仅构建或 Total 0。先前默认并行 run 的原 XML/log 保留于 `build/2c3c82de0457457c994ea3d3e8b67e43/`、`build/d124600e2da24c6ca1c01104ec20f499/`；其既有 Riichi fixture 精确 stateVersion 断言曾失败，未改动授权外文件，串行全量复跑绿。
- Unity PlayMode：过滤到真实 `C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions`，1/1 Passed，Unity native exit 0。XML `build/083ec8dbe22342c6b79dd2caee267692/playmode-results.xml` SHA-256 `E78C76C32BF08E67379F92CAD2DAA11117CAD0F6EBE944767A311AD343BFF6FA`；raw 真实生产帧 11 个，`privacy-evidence/raw-frames.json` SHA-256 `DF3E2A2C8E3D58D617322250459D60A547A42C8B34FC5315BA59CF6AAAFA4B6F`；Unity log SHA-256 `4AB103CAA5324E427CA6289619B9A834EBAA1B66C83E928F1EF4B9C9FFB286F7`；来源 manifest SHA-256 `0ABFC636202C164FEDDFB75D84F9D10912A06D75499FA3C0CC5B70E554997EF4`。
- Windows Test Player：同一过滤场景 1/1 Passed，Unity native exit 0。XML `build/55d45940b9d448f9a6df1760b3718a26/windows-player-results.xml` SHA-256 `568282021F65E3DC59E34E28CE2692793D3A3C5B6628C542E4D4B0BAF5ADCD1D`；raw 真实生产帧 11 个，`privacy-evidence/raw-frames.json` SHA-256 `C74C4BC040571FA84588BB369538ECE997800D95D809C5BA2E141160843B5C82`；Unity log SHA-256 `938F2776647CF4216E5E09E8CC23255289C06E47FA6514BAF4C87D1F2BBB5972`；来源 manifest SHA-256 `90333AD993440EF9DF9A477E1DBCF6B8ABE090AB98437A6FFE541E164B8D0ACF`。该 Unity 生成的测试 Player 与独立 `Mahjong.exe` 不是同一个二进制。真实帧 verifier 总体 PASS，自测 1 个正例/5 个负例；修改 capture hash 的负例 native exit 2 并被拒绝，摘要位于 `build/55d45940b9d448f9a6df1760b3718a26/verifier-tests/summary.json`（SHA-256 `448B0F7B04697B78EE2217FB4EFFB6D0B54A05DE2BFA5C2C8439489CBAC28197`）。
- 固定工具：Unity `6000.3.27f1`（`6000.3.27.6761814`），`D:/Unity/dotnet/dotnet.exe` 文件版本 `8.0.3226.50405`，SDK 输出 8.0.426。共享资源锁 `Local\MahjongV5P45UnityValidation`；各 run 用 E 盘独立目录、正常 runner 收尾，PlayMode/Windows Player 清理锁与临时 `S:` 映射。

## 实际命令及阶段结果

- .NET：在本工作树 fresh 构建正常 `bin/Release/net8.0` 和恢复用 CrashWorker/fixtures，再用 xUnit v3 直接运行 `Mahjong.LocalHost.Tests.dll`、串行模式 `-parallelMode none`，写 XML 至 `build/22df1f8f5f064d1bb77852adf2a5230f/results/localhost.xml`；最终全量 native exit 0，154/154。
- Unity PlayMode 由 `tools/unity/run-p45-c04-privacy-matrix.ps1` 调度；核心调用为 Unity `-batchmode -projectPath S:/staging-project -runTests -testPlatform PlayMode -testFilter Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions -testResults <E独占run>/playmode-results.xml -c04PrivacyEvidence <E独占run>/privacy-evidence`，native exit 0。
- Windows Test Player 使用同 runner 的 `-testPlatform StandaloneWindows64` 和同一过滤场景；native exit 0，Test Player 进入 Lobby 并产出 raw frames。
- 独立正式 Player build 使用 Unity `-batchmode -quit -projectPath S:/staging-project -executeMethod C04PrivacyBuild.Build`，输出绑定 `sourceHead=buildCommit=47ecf6…`；BuildReport `Succeeded`，errors=0、warnings=193、场景 6 个，runner 记录 PID 28740 在 235s 后 `unity-player-build exit=0`，并记录 `RUNNER_FINALLY_COMPLETE`。
- 早期 Player build run `build/ff67a4a0d69c42a48bce575f600e53cd` 的 PID 20744 只有到 151s 心跳，之后无 exit/stage summary/Player 产物；按 UNKNOWN 保留，不以之后成功 run 覆盖。外部旧 Unity PID 12824 启动时间 2026-11-02 18:18:32、路径为 Unity 6000.3.27f1，未操作。

## 固定 N/U/P 行状态

| 行 | 状态 | 已证证据与边界 |
|---|---|---|
| N1 | PASS | 真实 LocalHost 四玩家同房、owner 私手非空正对照；一条合法 discard 经真实通道推进，席位快照到同一 stateVersion。默认 seat0 + AI 保持。 |
| N2 | PASS | 观战只见公开 typed stream；真实 spectator command 得到 `SPECTATOR_READ_ONLY`，不产生业务副作用。 |
| N3 | PARTIAL | 每名 owner 非空手牌及公开字段正对照、audience/version 可见性判定通过；未能以既有合法公共命令产生真实袖牌私有状态及费用/ledger 非空正对照，故袖牌和费用子项 BLOCKED。 |
| N4 | PASS | 捕获 seat0 生产序列化 `SyncResponse` 原始字节，经 seat1 新绑定实际 receive 注入旧 session/seat/binding 帧；真实 `TableSession` 拒绝并保持私有快照不变。既有 ticket 过期/重放/取消负例通过。 |
| N5 | PARTIAL | 四玩家 + 两 spectator 的基线/游标独立；关闭 spectator/player 后其他连接仍 Ready；新 spectator 可重新 attach。容量上限实现存在，但没有真实填满有界慢 consumer 队列并断言 overflow 的测试，慢消费者子项未通过验收。 |
| N6 | PARTIAL | 既有真实连接控制 deadline、stale decision、stale epoch 负例通过。D05 完整 room 初始/恢复 controller、release 后 AI 推进且无人类 binding、未连真人不重开 decision 等全矩阵证据不齐。 |
| N7 | PASS | 实际 `LocalTableRoom` close/reopen；原 account/scope/request/canonical bytes 仅做 receipt query，command frame 发送计数为零；原 Applied receipt/digest 返回，state/event/state fingerprint、ledger 和 136 tile hashes 前后稳定。 |
| N8 | PASS | 独立 room 互不影响；同目录第二 owner 遇 lease conflict，释放后可接管。 |
| U1 | PASS | 生产 `AppComposition`、room/channel factory、四玩家 + spectator 的真实 PlayMode 场景通过。 |
| U2 | PARTIAL | production role-switch 创建新 session/coordinator/baseline、清除旧私有 cache 且现有 owner/public typed frames verifier 通过；未有真实 sleeve/fee 正对照，也未在 Unity 上投递延迟旧 wire 原帧。 |
| U3 | PARTIAL | Unity 承载真实 room 与控制/恢复相关现有场景，但没有完整 D05 room-controller/deadline 矩阵的 PlayMode 证据。 |
| P1 | PARTIAL | StandaloneWindows64 正式 build 成功，Windows Test Player 真实场景 1/1；仅定向 role-switch，不代表 Windows Player 全场景矩阵。 |
| P2 | BLOCKED | Windows Player 没有恢复/原请求 query reconciliation 场景；.NET N7 不能替代 Player 证据。 |

## 固定 C04-G5 行状态

| 行 | 状态 | 判定 |
|---|---|---|
| C04-G5-01 | BLOCKED | hands/private 与 public 非空正对照通过，但 sleeve 私有内容正对照缺失；该 row 不能整体 PASS。 |
| C04-G5-02 | BLOCKED | 没有从合法公共命令得到真实非空私有 fee/ledger 对照；不能以空字段、相等 scalar 或 verifier 自测替代。 |
| C04-G5-03 | BLOCKED | role-switch/cache clearing 与 .NET 原始旧 wire rejection 有证据；Unity/Windows Player 的延迟旧真实 wire 投递负例未执行。 |

> **R6 修正状态（2026-11-03，B）**：I2 已用 Unity `-buildPlayerPath` 保留实际 Test Player 包并通过包门禁；I4 改为严格稳定枚举、`upm-cache` 预裁剪、state 落 RunRoot；I3 恢复强断言后 recovery 真实 lookup 通过，但 ClaimControl 暴露 A 公共行为缺口（receipt Applied 后仍 `ControlledByComputer=true`、epoch 不前进）。旧 7/7 已标记为 false-positive，不用于最终验收。

## R6 修正（I2/I3/I4）

### 身份

- 最终代码头 `e63cd1d05539d64ce1da79672236045fed741032`；clean/diff-check0。
- 未改 A/Luna，未新任务/委派，未 push/PR/merge。

### I2：Test Player 包保留

- Windows 测试增加 `-buildPlayerPath RunRoot/test-player`；`Get-C04TestPlayerPackage -PlayerRoot` 直接读取保留包，不再依赖被删除的 `staging/Temp`。
- 实际 Windows 运行后 `test-player/PlayerWithTests` 保留；包门禁通过：
  - `packageFileCount=357`
  - `packageBytes=201647558`
  - executable `PlayerWithTests.exe`，SHA `93043e47abfce89ae9d048dbf34108e101edb367150e6203e0bcee27cd5a623d`
  - package manifest SHA `7a7151764cc6a7e5c9cf9f9d3c85759d5127c071dad45cea3215f618449ccaef`
  - `samePackageForP1P2=true`
- 原 Windows XML 仍为旧 false-positive 证据；新 Windows 运行 XML 6/7，唯一失败为 ClaimControl（见 I3）。

### I3：恢复/控制强断言

- Recovery：
  - 首 host 使用 `RawCaptureChannel` 有界丢弃一次原 request 的真实 Receipt，关闭前 `PendingCommands>0` 断言存在；
  - recover 后恢复 `Assert.IsNotNull(reconciled)`、`FromReceiptLookup=true`、scope/request/digest 对应、`ReceiptQueryFrames>0`；
  - 真实 Unity `RealRoom_ColdReopen...` 本次通过，证明 lookup 实际发生。
- ClaimControl：
  - 保留强断言 `Claimed=true`、`ControlHuman=true`；同时写入 `claimEpochAdvanced` 实际值，不再写常量。
  - 真实 PlayMode/Windows 均在 `ClaimControl` 后观察到 `receipt Applied` 但 `PrivateView.Control.ControlledByComputer=true`，`claimEpoch` 保持 1。测试因此失败，未放宽。
  - 最小 owner 复现：`RunReleasedControlScenarioAsync` 在 release→spectator AI 推进后，新 player seat0 打开并发送 `CommandKind.ClaimControl`，`SubmitExpectReceiptAsync` 收到 Applied；随后 `PlayerSnapshot.PrivateView.Control` 仍 AI/epoch 1。A owner 文件：`dotnet/src/Mahjong.TableRuntime/TableActor.cs` `ReclaimControlAsync` 与 `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` `HandleControlCommandAsync`。这是 A 公共控制行为缺口，不是 B 测试可放宽项。

### I4：严格枚举与 state 隔离

- `Get-C04StableFiles` 递归前裁剪排除目录；`Get-C04FileManifest`、`Get-C04SelectedManifest`、`Assert-C04BuildMarker`、Test Player 包均使用严格枚举；不可读/消失/集合变化直接失败，不再 `SilentlyContinue`/skip。
- `Write-C04SealedEvidence` 显式排除 `upm-cache`；不再遍历 volatile cache。
- 测试 state/save/outbox 从 `Application.persistentDataPath` 改为 `StateRoot()` 派生自 runner 的 `EvidenceRoot` 父级（唯一 RunRoot），不再写用户持久目录。
- reviewer enumeration ACL probe 复跑：
  - 可读 unlisted DLL：`Player package contains an unlisted file...`
  - 目录被 Deny ListDirectory：`EnumerateFileSystemEntries ... Access denied`
  - 两者均不 ACCEPTED；ACL finally 恢复。

### 实际验证与边界

- Unity PlayMode：`6/7`，仅 ClaimControl 失败；旧 `5f1...` 7/7 为 false-positive，保留但不用于验收。
- Windows Player：`6/7`，同一 ClaimControl 失败；包已保留，旧 `25f...` 7/7 同样不用于验收。
- LocalHost 174/174、TableRuntime 483 源码未变，沿用集成有效结果。
- runner 短自测 16/16；accepted verifier 四 raw 场景仍 native0。
- 结论：I2/I4 已闭合；I3 recovery 已闭合，I3 claim 触发 A 公共控制缺口，D05 仍未最终 PASS。

> **R5 集成合同状态（2026-11-03，B）**：从真实集成 HEAD `6315277b` 接续，D05-B-I1 的 schema2、self-test 独占目录、四 owner/ownerEpoch 场景闭包已修复。实际 Unity PlayMode **7/7**，四场景 raw 均通过 accepted verifier；Windows Player 测试 XML **7/7**，但 Test Player 临时包被 Unity 删除，runner 无法完成同包 P1/P2/G5 闭包，最终 D05 仍不标 PASS。

## R5 集成合同返修（D05-B-I1）

### 身份与结论

- 集成起点 `6315277b`（A `4cde1db7` + Luna `15981ccc` + D03）；最终代码头 `fc7c8479c8f37d9fe0596ef29989900dd6310a13`，clean/diff-check0。
- 未回退/未重新合并 A/Luna，未改 A/Luna 源；仅 B9 runner/Unity tests/report。依赖已集成，不再写“A/Luna 未交付”。

### 三项合同修复

1. **schema2**：raw gate 改为 2；短测覆盖 schema2 通过及旧 schema1/空/缺场景拒绝。`integrated-contract-probe` 对同一合法样本 `runnerRawGate=ACCEPTED`。
2. **self-test 独占目录**：改为 `RunRoot/verifier-selftest` 新目录，不再 `unused.json`；fresh self-test 17 正/67 负、native0，仓库 clean。
3. **四 owner/ownerEpoch 闭包**：role-switch 4、four-owner 4、closed-binding 4（seat0 旧/新 + 1..3）、recovery 首主机 single 保留原决策、恢复后 4；逐场景 accepted verifier 全通过，无手写帧/丢帧/放宽。

### 实际执行

- Unity PlayMode **7/7 PASS**，XML SHA `84788E9E319406E60320735B933B0E3D67E00486FE7FE15DCC9C5993302F3861`；raw role-switch 11/four-owner 49/closed-binding 6/recovery 9，四场景 verifier native0。
- Windows Player 测试 **7/7 PASS**，XML SHA `0B872B182D9D30AE2E07E91996D390F7259109F9EC0E6F4C8268343BA9DEE34D`。
- LocalHost **174/174** fresh，XML SHA `5CB9492287DA33C0B9E2A29DCF8D445D8F15269CF2EEBB4A5D297CE44BD5DBE7`；TableRuntime 483 沿用集成结果。
- 最终 Unity 证据：summary `615B3823000BF82C8D714E4A262FF2BE26E01AAAE6CE45A8C8FCAE5BB8F9264F`，manifest `121C62C1A90D10BE13408FDF9D36A624C92B9FBD418B8EAF269F438CEFF09211`，seal `87D34FFB3E205CCF300A817196893DA83D83C0C6140B73406B772D4A1E066F29`。

### 新发现未闭合：D05-B-I2（MAJOR，Test Player 包闭包）

- Windows Player 测试 7/7 通过，但 Unity Test Runner 删除临时 `PlayerWithTests` 包；`Get-C04TestPlayerPackage` 找不到 `staging-project/Temp/PlayerWithTests`，runner 报 `Unity Test Player Temp directory is missing` 并非零。
- 证据根 `build/25f6220e8b334ff6b0251f1c9b3bd9c4`：XML 7/7，runner failure 明确；P1/P2/G5 的同一 Test Player 包身份/完整闭包未完成，不能把 Windows XML 7/7 当最终 PASS。
- 下一轮修复：运行期保留/复制 Test Player 包，或用 `-BuildPlayer` 产物并让 Windows 测试绑定该包；不得删包检查或临时 XML 冒充同包证据。

### 状态

- D05-B-I1：schema2/self-test/四场景闭包闭合；Unity 7/7 真实通过。
- D05-B-I2：Windows 测试 7/7，但同包 P1/P2/G5 仍 BLOCKED；D05 未最终完成。

> **R4 返修状态（2026-11-03，B）**：R4 两个 MAJOR 已批修。进程 ownership 只保留 PPID/Job 可证明亲缘，删除名称/参数相似度 fallback；role-switch 改为独立 `role-switch-raw`，四场景采集互不依赖执行顺序。独立 161/161、483/483、runner 16/16、finalization 4/4、sibling ownership false、capture-order 四路径 PASS、orphan 明确 UNKNOWN。真实 Unity/Windows 仍 BLOCKED_ON_PRIVACY_AND_RECOVERY。R4 细节见下，R3/R2/R1 历史保留。

## R4 返修交付（2026-11-03，B：B-R4-01/02）

### 身份与结论

- R4 代码头：`e212b3ad294ab01b6d6e27b2969da7a74e3db819`；被审 R3 头 `40515787`。工作树 clean，`git diff --check` 0。
- 仅改 B9 内 `LocalPrivacyScenario.cs`、`C04PrivacyStreamMatrixTests.cs`、runner、runner tests、PlayMode tests；未改 A/Luna/Domain/Client/proto/actor/schema/asmdef。
- 结论：**R4 两个 B 自身 MAJOR 已修复并通过定向短负例；最终 Unity/Windows 矩阵仍待 A/Luna 冻结，D05 未完成。**

### B-R4-01：删除相似度 ownership 误认领

- 位置：runner `Get-C04ImageDescendants` / `Get-C04CapturedDescendants` / `Stop-C04OwnedRecord` / `Invoke-C04OwnedProcess` 启动登记段。
- 修复：
  - 完全删除“同 image 名 + 任一路径参数子串 + 创建时间”的 fallback；不再把同名同参数 sibling 当作后代。
  - 只保留可证明的 PPID 后代捕获与 Windows Job Object 归属；无法证明亲缘的进程不捕获、不终止，保留 UNKNOWN 身份。
  - 启动成功后先登记 PID/StartTime/输出路径，再进入 capture/初始化；capture 在 try/finally 内，初始化异常仍会清理已登记自有进程。
  - `Stop-C04OwnedRecord` 在 `drainUnresolved` 且无 captured descendant 时不再清 false/抛错；保留 alive=true/UNKNOWN，Main 因 residual writer 不写正常 seal。
  - 保留普通一层后代、大输出、取消、post-start 异常、单返回、Stop identity mismatch 等负例。
- 实测 reviewer `process-ownership-probe.ps1`：`unrelatedCaptured=false`、`unrelatedTerminated=false`，sibling 未被认领/终止；JSON SHA `1C099D9DCD81591AD0D7AF20B7D9A256E2D3514D1DD93583835BD0FD3F3EBA69`。
- 真实多层 orphan：`registeredAliveBeforeCleanup=true`、`registeredAliveAfterCleanup=true`、`realWriterAliveAfterCleanup=true`，即**明确 UNKNOWN**、不伪造回收；reviewer 最后自行清理 writer。JSON SHA `D2B40D6D4D35A41E898EBED21280F244A3BE3BFF1917E822F79D616F758586ED`。
- 回归：runner **16/16**，JSON SHA `3F12CF94ADC8B43558FE11F95A389BB2284B07AE7004FC6AB23373375901077C`；finalization 4/4 完整封存保留。

### B-R4-02：多场景采集目录隔离

- 位置：`C04PrivacyRoleSwitchPlayModeTests.cs` 原 role-switch Begin、recovery/four-owner/closed-binding Begin/copy；runner `Get-C04ValidatedRawScenes`。
- 修复：
  - role-switch 改为独立 `EvidenceRoot()/role-switch-raw`，不再使用公共父目录；recovery/four-owner/closed-binding 均用各自唯一子目录。
  - runner 要求四场景：`role-switch-raw`、`four-owner-raw`、`closed-binding-raw`、`recovery-raw`；逐场景解析 schema/frames 并分别调用 verifier，缺任一即失败。
  - 每个 case `Begin/Seal/Reset` 独立；失败 finally Reset，不覆盖其他场景原件。
  - Unity 测试 7 例：原 6 例保留，新增 open-deadline-after-player-close；精确 XML 分母同步 7，不删不 skip。
  - U3 扩展已含 ClaimControl、旧 control epoch decision 拒绝、release→spectator AI 推进、open deadline 继续；四 owner 非空正对照新增 .NET 每 seat 循环（all-four room 目标 seat 驱动）。
- 实测：
  - reviewer capture-order 的“先 recovery 后 role-switch”路径在**实际子目录合同**下四路径均 PASS：`role-switch-raw=PASS`、`recovery-raw=PASS`、`four-owner-raw=PASS`、`closed-binding-raw=PASS`，log SHA `7FA919F43DA38064A184EEF6F757044D80B69E36BC2FEF274D439D7164258A3B`。
  - 当前 Unity helper：四席、Version6、TargetClosedTiles=13、Ledger=2、Points=109、非空 offer，`captureAfter=True`、capturedFrames=49（原 0）；组件证据，不是实际 Unity 运行。
  - LocalHost **161/161**（新增每 seat 非空正例），XML SHA `0986088C3FCE8BD1DA32D77DEEEA2DA56FABC64674DE2AEB3CE4880C9B650F3E`；TableRuntime **483/483**，XML SHA `A2F54B091E5B203C069D26A8EB8562E8FFF92001A83312827D85CC239CAACD61`。
  - Unity 测试静态编译 0 error、0 warning；PlayMode DLL SHA `8A4A744082E1304C54DD7D56E2F71D8367BA5537578E9056A3C025F023E20227`。不是实际 Unity/Windows 执行。

### R4 clean preflight 与证据

- summary SHA `709AEF2E117E59444D75B47DE0CAF2A2CA2B6BE9C3D6CA0B260B08F5C954DFE9`
- manifest SHA `A01CCDA51C99D51584FA567102C035138A7D30FEEF2E1D73FA161D3A3125197F`
- seal SHA `078B579694678D327D607C027E4E4F0E13209E9B32CAE1E27DCB6621EE26794E`
- closure 534 文件 / 13,632,812 bytes / SHA `c5002e05e60a6f2fb22e55ee7f89cf7cc469327d4cba340c19b265849e85ed38`
- finalization JSON SHA `088360730A6A9DE8591D1AC22DEAD3B865271D2285DFC00F0ECBCE827CDCEAC7`
- runner 16/16 JSON SHA `3F12CF94ADC8B43558FE11F95A389BB2284B07AE7004FC6AB23373375901077C`
- 源码 SHA：`LocalPrivacyScenario.cs` `048612E88EF6B0A5886CD84682A3972BB468690BCB4BA83BF0AB9450540A3717`；矩阵测试 `CDBB2A569A1E99F58F267BB297F3552012373F185E470D76338E6E107825A185`；runner `222F22F20F94EA1B5CFECD9980DF90FF47E4A6ACE19F4394A027F6531F76ADA7`；runner tests `F0B297E41CBABD677204E8AFC57DF5C84C7969BD760962FD69859B86AF3C0B6D`；PlayMode `0AAA41906EF1B239EAA6969B93ED313CEB8616C5A36A3DC5A56BE4B9286728C2`。

### R4 后状态

- B-R4-01：sibling 误认领关闭；普通 orphan 为明确 UNKNOWN，不误杀、不伪清。
- B-R4-02：四场景目录隔离/顺序独立；7 个 UnityTest 精确分母；四 owner 非空 .NET 正例通过。
- 仍 BLOCKED_ON_PRIVACY_AND_RECOVERY：Luna typed verifier 未集成，A released/cold controller 未集成；真实 Unity/Windows P1/P2 未执行，不标 PASS。

> **R3 返修状态（2026-11-03，B）**：R3 的 3 个 MAJOR 已全部在 B9 内批量修复：封存顺序/失败证据、未解决 orphan writer、非空 raw 采集与 U3 claim/stale-epoch。独立 160/160、483/483、runner 16/16、finalization/orphan/Unity helper 短负例通过；真实 Unity/Windows 仍 BLOCKED_ON_PRIVACY_AND_RECOVERY。R3 细节见下文，R2/R1 历史保留。

## R3 返修交付（2026-11-03，B：B-R3-01/02/03）

### 身份、结论与范围

- R3 代码头：`76b64bb232390b2d76e6a47630637762e027795e`；工作树 clean，`git diff --check` 0。仅改 B 的 3 个已授权代码/测试/工具路径；未改 A/Luna 文件或 Domain/Client/proto/actor/schema/asmdef。
- 结论：**3 项 B 自身缺口独立修复并通过短正负例；最终 Unity/Windows 真实矩阵仍因 A/Luna 未集成冻结而 BLOCKED，D05 未完成。**

### B-R3-01：封存后不再写文件

- 位置：`run-p45-c04-privacy-matrix.ps1` Main finally。
- 修复：先把 `finalization-failure.txt`、summary 的最终 status/error/finalizationFailures 全部落盘，再执行唯一一次 `Write-C04SealedEvidence`；seal 成功后没有任何日志/失败文件写入。seal 失败则删除无效 manifest/seal、写 `seal-failure.txt`、保留原始 failure 并最终非零。`Release-C04Mutex` 不再 `catch {}` 吞 `ReleaseMutex` 异常，改为 `try/finally` dispose 并让异常上抛。
- 实测：reviewer `finalization-probe.ps1`（只注入自有 mutex release 失败）在 clean HEAD 输出 `mainThrew=true`，`sealExists=true`，`sealedCount=4/manifestCount=4/actualCount=4`，`finalizationFailureExists=true/finalizationFailureListed=true`；JSON SHA `52DBA65F93CFB4F1046C51F90DD28ED2D8B6621B05D7F462A0A49D630548D336`。seal I/O 注入仍 native 非零且无有效 seal，`build/seal-r3-final3.log`。

### B-R3-02：orphan writer 不再被 parent 缺失误清

- 位置：`Invoke-C04OwnedProcess`、`Get-C04CapturedDescendants`、`Stop-C04OwnedRecord`、Main finally。
- 修复：
  - 启动后立即登记 PID/StartTime/输出路径；在父进程仍可观察时按 PPID 与同 image/路径参数捕获后代身份（PID+StartTime），同时使用 Windows Job Object；捕获失败不伪造所有权。
  - `drainUnresolved`/captured descendant 未确认停止时，`Stop-C04OwnedRecord` 先停止已捕获的自有后代并复核 PID+StartTime；无法证明停止则保持 alive/UNKNOWN，不释放 mutex/subst、不写正常 seal。
  - Main finally 在仍有 writer 时 `SEAL_WITHHELD_RESIDUAL_WRITER`，不产出正常 seal；`StopRecord` 不再只凭 parent PID 缺失清 false。
- 实测：reviewer `orphan-probe.ps1`（root→middle→writer，writer 持继承输出 30s）在 clean 代码上：`registeredAliveBeforeCleanup=true`、`registeredAliveAfterCleanup=false`、`realWriterAliveAfterCleanup=false`、`elapsed=4.76s`（原 7.199s 且 real writer 仍活）；JSON SHA `62E5D5AE720D890A1CA175FF967AC4120FD21589DFAB60456FD8766AC286BB41`。
- 保留通过项：普通一层后代、大 stdout/stderr、native 非零、取消、post-start 异常、single-return、Stop identity mismatch、锁等待超时；本轮 runner **16/16 PASS**，JSON SHA `C210432FE125094E2339F3E81CDF7A78733758B075831B1458BCC3AD8F3072FF`。actual Main seal probe 改为有界 `Invoke-C04OwnedProcess`，不再 `Start-Process -Wait`。

### B-R3-03：非空 raw、逐场景门禁与 U3

- 修复：
  - `RunFourOwnerPrivacyScenarioAsync` 与 `RunClosedBindingRawPathAsync` 现在在真实 factory/TableSession 场景内启动 `C04PrivacyProbe.Begin`；对应 UnityTest 在 Task 完成后 `Seal` 并把 raw 复制到 `four-owner-raw/`、`closed-binding-raw/`；recovery 场景写 `recovery-raw/`。每个场景独立 Begin/Seal/Reset，失败 `finally` 清理。
  - runner 通过 `Get-C04ValidatedRawScenes` 强制四个场景：role-switch、four-owner、closed-binding、recovery；逐场景解析 schema/frames 并分别调用 privacy verifier，缺任一场景直接失败。工具测试 `required raw scenes...` 覆盖缺 scene。
  - U3 扩展为 release → spectator AI 推进 → 新 player ClaimControl（epoch 前进/恢复真人）→ 旧 control epoch 的 decision 被 Rejected；同时保留 player→spectator→player raw 路径。
  - Test Player package 身份仍由 `Get-C04TestPlayerPackage` 记录，供 P1/P2 同一 Test Player 包执行；实际未跑。
- 实测：
  - reviewer Unity helper 重新编译当前测试源码后：`Version=6`、四席、TargetClosedTiles=13、Ledger=2、CheatPoints=109、非空 offer；`captureAfter=True capturedFrames=49`（原 captureAfter=False/0）；日志 SHA `96826EDE4E84AE23FE407E196C07319D759CA046528FA1A3D258E05F35D5CAD6`。
  - Unity PlayMode 6 例静态编译 0 error、1 个既有 nullable warning；PlayMode DLL SHA `8B1C9DD7FDED84E6426C3611834F97CC221905A41BF670FCA55DAE4D95D5BB2F`。不是实际 Unity 运行；Unity/Windows 仍待 A/Luna 冻结。
- 说明：旧 verifier 不能作为正式 PASS；B 只为 raw 采集/逐场景门禁与 U3 场景源码负责，Luna typed verifier 与 A 控制修复未越界。

### R3 验证证据

- LocalHost：**160/160**，XML SHA `D6A42F6B7716E8D001D1FA1DC00C4301A6C95A72AF02E3EA510B8BA89BF6E006`。
- TableRuntime：**483/483**，XML SHA `E908C4552AE5A0D8B3910A4BDF2EB119DF430C676BF18B1DEFD329E35160DECC`。
- runner：**16/16**，JSON SHA `C210432FE125094E2339F3E81CDF7A78733758B075831B1458BCC3AD8F3072FF`。
- clean preflight：summary SHA `B1CDDA6A46636486E0943049A010B67CB052709BEAD54DEBAA0C77EF687FE4BE`；manifest SHA `6C73A0FC17B5F9686D0E447B7A30EE12D5B0A74FA5932A1A38A369416D271FE3`；seal SHA `8DF17A2CD9691F179FC00B984473355698C9F6E66545A7CDAA98AAF4EF77F63E`；closure 534 文件/13,626,820 bytes/SHA `cc3f0939cde5516fde85a9bed483fb080b53ee1046fd133bc2336fd64547bf6c`。
- 源码 SHA：runner `384A245150D1927F0DD3C02E00136653A1C10DD99F81138961C52C8481866AE1`；runner tests `0685C34EDABB83234326C6CBB980B292E0AE5181B0C1D6FCEE7DDB676DDB602F`；PlayMode `554C8C0A8B387692448658386D2F49C3052FAEDF050E4C8849BC644FD139CE06`。

### R3 后固定行

- B-R3-01/02：独立门禁与 orphan 收尾通过；完整 Unity/Windows 仍待依赖执行。
- B-R3-03：N3 四席/观战同版本非空 target 正例、慢队列活请求、player→spectator→player raw、U3 claim/stale 源码完成；N3/G5 的正式 typed verifier 仍 BLOCKED_ON_PRIVACY；U3/P1/P2 实际执行仍 BLOCKED。
- 原 6 个 UnityTest 保留并精确对齐 XML 分母；未 skip、未删除、未用 .NET 替代 Unity 运行。



### 结论与身份

- 被审/修复工作树：`E:/majiang-review/g2-c04-d05-acceptance-matrix-ds-20261102-r1`；分支不变。
- R2 代码头：`d1706b8d8fa3bcd23dbc56dba1e4ffcf7fc7ee2c`；R2 前被审头 `f7c781c5`。工作树 clean，`git diff --check` 0。
- 只改 B 的授权 9 路径中的 5 个代码/测试/工具文件；未改 A/Luna 文件、Domain/Client/proto/actor/schema/asmdef。
- 结论：**B-R2-01/02/03 的独立实现与短负例已修复并通过；最终 Unity/Windows 真实矩阵仍 BLOCKED_ON_PRIVACY_AND_RECOVERY，D05 未完成、未批准。**

### B-R2-01（MAJOR，R1-04 包闭包/封存门禁）

- 复现：`probe-v3/probe-runner.ps1 -Root <新根>`；`seal-failure-probe-v2.ps1 -ReviewRoot <本根>`。
- 修复：
  - `Assert-C04BuildMarker` 现在要求 marker 必需字段存在且类型正确，禁止 null→0 放行。
  - 包路径使用规范化绝对根 + 末尾目录分隔符；拒绝 rooted、`..`、同名前缀逃逸和 reparse point。
  - 清单路径唯一、大小写不敏感；exe 和 8 个 required plugin 必须在闭包内；实际目录文件集合与清单集合精确相等（唯一排除 `player-package.sha256` 自身）；校验 actual count/bytes/hash。
  - `Write-C04SealedEvidence` 失败不再被 `Write-Host` 吞掉；Main finally 收集 seal/env restore/subst/mutex/process cleanup 失败，最终 native 非零并写 `finalization-failure.txt`；找不到 seal 时不以成功退出。
  - 持久化 `source-closure.sha256`、`staged-input.sha256`、`source-closure-final.sha256`；最终再次验证 source closure/HEAD 未变。
- 修复后实测（新根 `build/probe-r2-final-ef3dc51f763c48948798b9d28bcd0d91`）：
  - `unlistedPackageFile` → `Player package contains an unlisted file: UNLISTED-RUNTIME.dll`（拒绝）。
  - `siblingPrefixEscape` → `Package manifest path contains parent traversal`（拒绝）。
  - `seal-failure-probe-v2` 在 clean HEAD 上 native **1**，输出 `SEAL_FAILED`/`Runner finalization failed`，没有 `ACTUAL_MAIN_RETURNED_SUCCESS`；日志 `build/seal-r2-final.log` SHA `EA4F674409C275C8CD41B53E59FFFEED418799047F5242F742CBBEDB08474954`。
- 工具负例：`C04PrivacyRunner.Tests.ps1` 增加 unlisted、sibling escape、null required field、package manifest closure、Test Player package 等用例；**14/14 PASS**，JSON SHA `40BA29CF0A1D32B09037835856066F15D901AA6B23CD45166BBE38D77AC3F80A`。

### B-R2-02（MAJOR，R1-04 进程/资源收尾）

- 复现：`probe-v3/probe-runner.ps1 -Root <新根>` 的 `InheritedOutput.exe`：父进程先退、子进程继承 stdout/stderr 后睡 7s；`Invoke-C04OwnedProcess -TimeoutSeconds 1 -CloseGraceSeconds 1`。
- 修复：
  - 启动后立即把 PID/StartTicks/输出路径登记到 `OwnedRegistry`；异常路径不再漏登记。
  - 总预算覆盖父进程、stdout/stderr drain；超时后识别并停止可证明自有的 descendant process tree（PID+创建时间+PPID），再以 5s 有界 drain；无法停止则 `drainUnresolved=true`/UNKNOWN，不释放仍被使用的锁或 staging drive。
  - 所有 `GetResult`/`WaitForExit` 有界并清除 void task 管道输出；函数只返回一个 `PSCustomObject`。
  - Main finally 主动清理登记的残留 writer；mutex 只在 `allStopped` 时释放，subst 只在 `allStopped` 时撤销；cleanup/seal 失败纳入整体非零。
- 修复后实测：
  - 同一 descendant fixture：`wallSeconds=3.469`、`elapsedSeconds=3.37`（修复前 7.205/7.155）、`outputTypes=[PSCustomObject]`、`timedOut=true`、`descendantsStopped=1`，无残留自有 writer。
  - 新短负例覆盖：large stdout/stderr、native 非零、父先退+子持管道、拒绝正常退出、cancel-file、post-start 注入异常登记清理、Stop identity mismatch、锁等待超时、single-return cardinality；`C04PrivacyRunner.Tests.ps1` **14/14**。
  - 真实 Main 封存注入 probe 在 clean tree native 非零；reviewer 原 `seal-failure-probe-v2` 已复验。

### B-R2-03（MAJOR，R1-03 B producer/断言）

- 修复：
  - .NET 新增 `Real_room_four_owners_and_spectator_observe_same_version_non_empty_private_target_facts`：真实 `LocalTableRoom` 四人类 + 四 `TableSession` + spectator；真实公共命令产生非空 TileMark/ledger/SleeveBloom offer，所有 owner/spectator 在同一 stateVersion 校验 seat/手牌/私有费用，target 的非空 offer 与费用保留。
  - 慢队列测试在 overflow 后由原 player 发送真实 `ReleaseControl` 请求并收到 receipt/仍 Ready，证明不是只读取旧缓存。
  - 新增 `Closed_player_to_spectator_to_new_player_receive_path_rejects_old_private_raw`：player→关闭→spectator→新 player 的真实 receive 路径注入旧 raw，旧 session id/手牌不污染新 binding。
  - Unity PlayMode 增到 **6 个** `UnityTest`：原四席/角色切换、延迟 raw、冷重开、四席非空 ownership、released AI 无 player binding、closed player→spectator→player raw 路径；全部使用生产 `LocalTableChannelFactory`/`TableSession`，未删除旧 3 例，未 skip。
  - `C04ExpectedTests` 与精确 XML 分母更新为 6；Windows 模式新增 `Get-C04TestPlayerPackage`，记录同一个 Test Player 包的 exe/包 count/bytes/manifest SHA/plugin 闭包，P1/P2 可由同一 Windows Test Player 包执行；代码路径已实现，实际未运行。
- 验证：LocalHost **160/160**（首次 159/160 为 A 所属既有 `C04RoomTicketTests` 帧顺序竞争，fresh 重跑 160/160；两次 XML 均保留）；TableRuntime **483/483**；Unity 测试静态编译 0 error、1 个既有 nullable warning，不是实际 Unity 运行。
- 仍未完成：A 的 R1-02 released/cold controller 修复、Luna 的 R1-01 typed raw verifier 未集成；因此 Unity/Windows 不执行，U3/P1/P2 只到可执行源码/命令与 BLOCKED 状态，不标 PASS。

### R2 验证证据

- LocalHost 绿色重跑 XML：`build/1f3a7069aeb8431eb5957466e6ced29e/localhost.xml`，SHA `02CD91D61A03FB462885639ACD0111EA567C28A685A9BC7AAD2E6A6A3089E64B`；首次 flake XML `build/31b607847a754c5782069b46fc21635c/localhost.xml` SHA `49FC2991CF26839E7234E0F9860CCDCA1BAE0ABCEF8665ADEF5727EE12A9CBFB` 保留。
- TableRuntime XML：`build/31b607847a754c5782069b46fc21635c/tableruntime.xml`，SHA `B4D2AB0D43587DD03AC4A18B261C9DFC56485C5F5D2D1D4E5B2C00681A94EB28`，483/483。
- runner 14/14 JSON：`build/e1610fbb6c7546708c980f68d5e1e434/runner-tests.json`，SHA `40BA29CF0A1D32B09037835856066F15D901AA6B23CD45166BBE38D77AC3F80A`。
- clean preflight：`build/26592ffeea844e9c94970fe981cdd582/summary.json` SHA `92C5E9B06DF8169FDAF206952C86004A0476766F35644D1982556DC9DD82EAC7`；manifest SHA `126880BE74127C0EEB5EDA6CA27F476E548052F07FAD8A6878DF54FB153AFA3E`；seal SHA `D36FAFEC7D8274868ED6C4AF0F3F8EABC9DB7CF7A16B82BFE014F2BFAB2EE13E`；input closure 534 文件/13,614,467 bytes/SHA `56015dcfcbd3f475ce9b1c61ccba81ffe3c2f016ce11e1bdb180a7eb3a981558`。
- reviewer runner probe JSON：`build/probe-r2-final-ef3dc51f763c48948798b9d28bcd0d91/additional-runner-probes.json` SHA `CF085C0A6CA620C6295BAF8D3A2BD36444C63E7166EFB42DB26A95291F305A0C`。
- 关键源码 SHA：`LocalPrivacyScenario.cs` `C19E6AA172B89EDE5D9949BA5E601C2804EF6CD9CD704EE4F5A0BAC1F4CD6612`；`C04PrivacyStreamMatrixTests.cs` `DE0F17A487F1C02A26DC3FB5361C26E590469966747F4DDA491983A44956DAC2`；runner `E420EF1CB5582AFFC1428B13D0516D18D82989AB9B3140CCEF12040F5655E859`；runner tests `E2545992DBCE60D7C3281C3CDEB5D9326C1A2E475E379CC40CC13325A691260D`；PlayMode `CB1E3B83666D73EAC93139657BE597E9374929A1389D3F64CA85640404E2B4BF`。

### R2 后固定行更新

- N3：PARTIAL；.NET 四席/观战同版本非空 target 正例已通过；完整 typed raw 仍待 Luna R1-01。
- N5：新增慢队列后原 player 真实 ReleaseControl/receipt 通过；组件层仍 PASS。
- N6/U3：U3 Unity 源码已补，但 A R1-02 未集成，状态仍 BLOCKED_ON_RECOVERY/NOT_RUN。
- U1/U2：PlayMode 源码 6 例、静态编译 0 error；实际 Unity/Test Player 未执行。
- P1/P2：同一 Windows Test Player 包身份 runner 已实现；实际未执行、未同包验证，仍 BLOCKED_ON_PRIVACY_AND_RECOVERY。
- C04-G5-01/02/03：仍 PARTIAL/BLOCKED，需 Luna verifier 与真实 Unity/Windows 同包矩阵。

## R1 返修交付（2026-11-02，B：G2-C04-D05-R1-ACCEPTANCE-MATRIX）

### 身份、范围与结论

- 工作树：`E:/majiang-review/g2-c04-d05-acceptance-matrix-ds-20261102-r1`；分支：`wt/v5-g2-c04-d05-acceptance-matrix-ds-20261102-r1`。
- 任务卡基线：`6123f0a17e8a47997e577086a125dc58da7a3d7e`（其父 `90140b34`）；R1 代码提交：`b560a9bda85a9e145c8f75f6d846a3d458dd965e`；提交前 tracked/untracked clean，`git diff --check` 0。
- 本卡唯一允许修改的 9 个 tracked 路径中实际改动 6 个（4 改 + 2 新增）；`C04PrivacyBuild.cs.meta`、`C04PrivacyRoleSwitchPlayModeTests.cs.meta` 与 `LocalPrivacyScenario.cs`（dotnet 无 meta）未改/新增正确。没有修改 A 的 19 文件、Luna 的 6 文件、Domain/Client/proto/actor/schema/asmdef。
- 结论：**R1-04 runner 独立组件修复完成；R1-03 独立可做的隐私/慢队列/旧 raw/恢复对账场景完成并通过 .NET 回归；最终 Unity/Windows 同候选同包矩阵 `BLOCKED_ON_PRIVACY_AND_RECOVERY`。D05 未完成、未批准。**
- 本轮没有构建新 Player 包，没有运行真实 Unity/Windows Player，没有使用旧 verifier 冒充新候选 PASS，也没有 approve/push/PR/merge/deploy/scheduler/ complete.flag 操作。

### 全部已知缺陷（不缩减、含 R1-01/02/03/04）

| ID | 级别 | 归属 | 本轮状态 | 位置/摘要 |
|---|---|---|---|---|
| D05-R1-01 | BLOCKER | Luna | `BLOCKED_ON_PRIVACY` | verifier 只核摘要/自声明字段，未用真实 WireCodec/typed parser 解码 raw。 |
| D05-R1-02 | MAJOR | A | `BLOCKED_ON_RECOVERY` | 冷恢复 controller 被新 SeatPlan 覆盖；released AI 无真人 binding 仍 `CanTickAuthority=false`；viewer/ticket 生命周期同组。 |
| D05-R1-03 | MAJOR | B | `PARTIAL`（独立场景完成，最终矩阵 BLOCKED） | N3/N5/N6/U2/U3/P1/P2 及 G5 缺失；旧 1/1 Unity/Test Player 不能替代固定分母。 |
| D05-R1-04 | MAJOR | B | `FIXED_INDEPENDENT` | runner 来源/输入、XML/marker、native、超时/取消/封存缺陷。 |

#### D05-R1-01（Luna，BLOCKER）

- 位置：`dotnet/tools/C04PrivacyMatrixVerifier/Program.cs:64-108,124`、`C04PrivacyMatrixVerifier.csproj`、`C04PrivacyProbe.cs` 的原始帧/身份采集。
- 复现：`powershell -NoProfile -File build/caller-review/run-raw-mutations.ps1`（只读根 `R=E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review`）。
- 实际：原始记录 native0；非法单字节、空字节、跨 seat wire 替换、私有手计数改写四变体也全部 native0 / `PRIVACY_MATRIX_PASS frames=11`。
- 期望：从实际 Welcome/typed 帧解码出 room/session/scope/role/seat/binding/version/owner/audience 与字段内容；畸形/空/错误类型/未知字段/跨身份替换非零；旁路摘要不一致拒绝。
- 证据：`R/raw-mutations.json` SHA `E1B267A2FBF97658A5C0E25D3C58582BBD382A2312EAAC5DDAFF8F24FCA1E145`；各 `.raw.json`/stdout/stderr；`R/audit.json`。
- 修复要求：复用现有 `WireCodec`/typed generated parser，补采原始 Welcome 与身份绑定；允许 verifier 引用现有契约项目；不得只查长度/magic/摘要。
- 验收：合法实际生产序列通过；上述四变体均非零；错 Welcome/owner/audience/version/session/scope、截断/非法类型/未知字段、公流私有值均拒绝；自测用真实合法序列化。
- 本轮处置：B 不改 verifier；runner 保留调用入口，但在 Luna R1-01 修复集成前不执行正式隐私 PASS。

#### D05-R1-02（A，MAJOR）

- 位置：`LocalTableRoom.cs:178-211`、ticket 注册表；`LocalTableRuntime.cs:134-167`/`CreateActor`；`LocalSnapshotFacts.cs:118`；`AppComposition.cs:238-243,523-555`；`LocalTableChannelFactory.cs` 默认共享 room 路径。
- 复现：`D:/Unity/dotnet/dotnet.exe exec build/caller-review/probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll`（真实 `R/probes/Program.cs`，生产 P41Harness/room/channel/TableSession，不反射写权威）。
- 实际：all-AI fresh 保存后改 `AllFourHumanSeats` 冷恢复 → `RECOVERY_CONTROL_INVALID ... Seat 0 changes controller without advancing controlEpoch`；seat0 真 ReleaseControl(Recorded/LOCAL_CONTROL) 后关闭、同计划冷恢复、仅观战绑定 → automated=true/epoch=1 但 `CanTickAuthority=false`。普通真人正对照仍正确。
- 期望：恢复的 controller/epoch 优先于新初始 SeatPlan；已 release AI 无真人 binding 可推进；按 current/recovered controller、live binding、phase、既有 deadline 决定；viewer 关闭不误释放归属、默认单 viewer 退出释放 lease/driver、多 viewer 只 detach 自己、ticket 退役有界不可重放。
- 证据：`R/room-probes.json` SHA `981EC1AED33DA19A699A4B058FDDCDD633D4F98A8CF9EF5D4632531FE12FB2D4`、`room-probes.stdout.txt`、`R/probes/Program.cs`。
- 修复要求：恢复控制快照优先；SeatPlan 只用于 fresh 初始化和允许发票；修 viewer 隔离/默认 owner 资源释放/ticket 有界；不改 actor/domain/schema。
- 验收：两条失败转真实通过；完整 fresh/recovered × human/AI/released/claimed × bound/unbound × preparing/started 矩阵；旧 decision/epoch 拒绝、不重开/延迟 deadline；viewer 关闭超时/隔离保留、lease、其他 viewer/authority 不受影响、ticket 反复退役有界。
- 本轮处置：B 只写矩阵/场景，不抢改 A 的源码；N6/U3/P1/P2 因此保持 BLOCKED_ON_RECOVERY。

#### D05-R1-03（B，MAJOR）

- 位置：原实施卡阶段 B–E、D05 边界合同 §6；原报告固定 N/U/P 与 C04-G5。
- 复现：旧交付只运行一个 Unity/Test Player 场景各 1/1、11 帧；逐行对照 N1–N8/U1–U3/P1–P2、C04-G5-01..03。
- 实际（R1 前）：N3/N5/N6/U2/U3/P1 部分完成，P2 缺失，三条 G5 均 BLOCKED；缺真实非空袖牌/费用正对照、慢 consumer 饱和、完整 controller/deadline、Unity/Windows 旧 raw 延迟 receive、Windows 恢复原 scope/request 查询。
- 期望：授权内可做项全做；固定行按实际分母判定；generic .NET 控制测试不能替代新增 room 边界、Unity/Player 或同包证据。
- 证据：原报告 SHA `E52BB3DA25B8BAEEE36771DE4B033CD229F97A83A80BF4A026CECD428DE9942B`；`R/audit.json`；原 source/package 清单。
- 修复要求：用 `LocalPrivacyScenario`、真实公共合法命令、实际通道建立非空对照；慢队列、真实 raw 延迟、恢复对账、.NET/Unity/Windows 路径；外部缺口给最小复现和精确 BLOCKED，其余继续。
- 验收：每 N/U/P/G5 行有实测 PASS/FAIL/BLOCKED/NOT_RUN 和原件；owner/audience/version 内容对照不靠空值/裸标量；恢复原 request 查询、重放不增副作用；旧角色 raw 不污染新缓存。
- 本轮处置：见下“R1-03 实现与固定行”。

#### D05-R1-04（B，MAJOR）

- 位置：`tools/unity/run-p45-c04-privacy-matrix.ps1:48-62,107-129`；旧 `tools/tests/C04PrivacyMatrix.Tests.ps1:20`；报告包身份。
- 复现：`powershell -NoProfile -File R/run-runner-probes.ps1`（审查自建 sleep 子进程/独占 mutex，仅自有进程）。
- 实际：XML gate 接受 `passed=1,failed=0,skipped=7,total=8` 和 `errors=3`；build 只 `Test-Path` marker；超时 1s+宽限 15s 后辅助进程仍 alive 但 mutex 已释放；HEAD/porcelain 只记录不拒绝、复制 live Assets/Packages/ProjectSettings 无闭包门禁；工具测试同步 ReadToEnd 可能阻塞；报告把整包 bytes 写成 exe 长度。
- 期望：native/XML/marker/包闭包与冻结输入共同决定成功；所有自有 writer 停止后释放锁/映射并稳定封存；取消/失败不是 PASS；无法停止时 UNKNOWN 和残留身份。
- 证据：`R/runner-probes.json` SHA `42FF760521CDAA1460C447EC79383286C151747C6C901E18A725F5DBFFC2A6CD`、`R/owned-timeout-probe/`。
- 修复要求/验收：见下“R1-04 修复映射”；短负例先过，才允许昂贵 Unity/Player。

### R1-04 修复映射与真实负例

| 原缺陷 | 修复 | 实际负例/断言 |
|---|---|---|
| XML 只查 passed/failed | `Assert-C04UnityXml` 固定 3 个 case 全名、`total/passed` 精确、fail/error/skip/inconclusive/not-run 全 0、`result=Passed`、所有 case `Passed`、无 failure/error 节点 | `C04PrivacyRunner.Tests.ps1` 的 valid + skipped/errors/failed/wrongCase/wrongResult/notrun/missing 全部按预期拒绝 |
| marker 只 `Test-Path` | `Assert-C04BuildMarker` 校验 schema/kind/result/errors/target/sourceHead/buildCommit/errorMessages/output/playerBytes/playerSha/package manifest count/bytes/hash/scenes/plugin hashes | 有效 fixture 通过；wrong source/errors/playerBytes/plugin hash/missing scene/missing plugin 均拒绝 |
| HEAD/porcelain 只记录 | `Get-C04SourceIdentity` + `Assert-C04SourceIdentity` 拒绝 dirty、diff --check 非零、错 expected head | 真实 preflight `b560a9bd`：clean、输入闭包 534 文件/13,578,188 bytes/SHA `b734e6fc0f3aaef7b8a7d22022b3a8612faf6b315069214301b2df727d88c1f8` |
| 复制 live 输入无闭包 | `Copy-C04UnityInputs` 复制 Assets/Packages/ProjectSettings，复制前后逐文件 SHA 清单相等；目标侧 mutation 立即失败 | runner 测试 `copy input closure...` PASS；mutation 触发 `input closure mismatch` |
| native 非零被吞 | `Assert-C04NativeSuccess` 明确拒绝 timedOut/cancelled/aliveAfter/非 0 | 大输出子进程 exit 7：返回 exit 7、stdout/stderr 各 ≥1 MiB、`Assert` 抛错 |
| 超时/取消后 writer 残留 | `Invoke-C04OwnedProcess` 有界 `CloseMainWindow`，宽限后仅对 PID+StartTime 匹配的自有进程 `Kill`，等待退出；`Stop-C04OwnedProcess` 拒绝身份变化 | timeout 1s/grace 2s 子进程被停止、`aliveAfter=false`、PID 不存在；cancel-file 同；均非 PASS |
| mutex 先释放后 writer 仍在 | finally 先确认所有自有进程 `aliveAfter=false`，再释放 mutex；残留则状态 `UNKNOWN` 并保留锁 | 测试正常/取消/超时路径均无残留；锁等待 1s 超时，不释放他人 mutex |
| 工具测试同步读 stdout/stderr | runner/测试使用异步 `CopyToAsync` 到文件并 drain；测试有界等待 | 1 MiB×2 大输出 PASS，无阻塞 |
| 封存可被写坏 | `Write-C04SealedEvidence` 写 summary/manifest/seal，manifest 排除自身和 seal，二次 seal 拒绝 | seal 测试：manifest 哈希休眠后不变，二次调用抛 `already sealed` |
| exe/包 bytes 口径 | `C04PrivacyBuild` 写 `playerBytes`/`packageBytes`/`packageFileCount`/`packageManifest`/`packageManifestSha256`；runner 分别校验 | 编译检查 0 error；旧 667,136/128,346,555 口径已在正文纠正，旧 marker 未改 |

- runner 自测：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 -TestRunRoot build/<guid>`，**9/9 PASS、native 0**，JSON SHA `ECA2C18237786E1690662F2770B2ED4D50DF7D672C77F0D08EF2D303557B8F3A`，证据 `build/aea54d077ceb4b4eb7ac2c4188a8eb17`。
- runner 干净预检：`... -RunRoot build/cb29... -PreflightOnly -ExpectedSourceHead b560...`，**PASS/native 0**；summary SHA `716E317E0540A1F858D83FBD7A7D540EB3E7EF7874EEB79A2A4004B2CC002A7F`，manifest SHA `8AD15CD8A6018E69986549232FBE3C7FBE748C535E074F46EA14394FC1CB7AD7`，seal SHA `B5D22E991C86F71F994EFA3949F6A78D2047A5F0EF0A15C6BBEDCA0C2C18894A`。

### R1-03 实现与固定行

新增/修改的真实场景：

- `LocalPrivacyScenario.CaptureSleeveAndFeeAsync`：只用 `TableSession` 公共命令（TileMark、合法弃牌/推进、SleeveBloom、SleeveBloomChoice），不反射、不写权威、不伪造 ledger。返回真实非空 mark group、私有 `cheat_points`、`ledger_entries`、SleeveBloom offer ID/候选 tile。单测 `Legal_public_commands...` PASS。
- `Slow_real_channel_queue_saturates...`：真实 room + spectator `TableSession` 打开后停止 pump，向真实 channel 发送真实 Heartbeat；生产 `AsyncFrameQueue` 在 256 帧边界溢出，后续 `ReceiveAsync` 抛 owned `LOCAL_LOOPBACK_OVERFLOW`；player 仍 Ready，新 spectator 仍可 attach。PASS。
- `Delayed_real_private_wire_from_old_seat_binding...`：捕获 seat0 的真实序列化 `SyncResponse` 原始 bytes，延迟注入 seat1 的新 binding；seat1 session/seat/closed tiles 不变。PASS。
- `New_room_cold_reopen_queries_original_scope...`：真实 room 提交 discard 后关闭、同目录重开；原 account/scope/request 只走 receipt query，`commandFrames=0`、`receiptQueryFrames>0`、canonical digest/state/event/ledger/136 tiles 稳定。PASS。
- Unity 新增两个用例：`RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding`、`RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate`；均使用生产 `LocalTableChannelFactory`/`TableSession` 和真实 wrapper，不手写 controller。静态编译检查 0 error（见下）；**未执行真实 Unity/Player**。

固定行（R1 候选 `e16...`；旧行历史状态不覆盖）：

| 行 | R1 状态 | 依据/边界 |
|---|---|---|
| N1 | PASS（.NET） | 真实四席 room + 非空 private hands；full LocalHost 158/158 含该矩阵。 |
| N2 | PASS（.NET） | 真实 spectator PublicSnapshot/命令拒绝/完整 suite 通过；最终 typed raw 由 N3 门禁。 |
| N3 | PARTIAL / `BLOCKED_ON_PRIVACY` | 合法非空 sleeve/费用正对照 PASS；但完整 raw WireCodec/typed owner+audience+version 判定依赖 Luna R1-01，不能标 N3 PASS。 |
| N4 | PASS（.NET） | 延迟旧 raw 被新 binding 拒绝；旧 ticket/重放负例在 full suite。 |
| N5 | PASS（.NET 组件） | 慢 consumer 真实队列溢出且不影响 player；room 关闭/viewer detach 旧回归在 full suite。 |
| N6 | `BLOCKED_ON_RECOVERY` | A R1-02 两条真实失败仍存在；B 不抢改 `CanTickAuthority`；完整 fresh/recovered/deadline 矩阵待 A。 |
| N7 | PASS（.NET） | 新 room 冷重开原 scope/request query，零重复、零 command。 |
| N8 | PASS（.NET） | full suite 含 lease/room 隔离回归。 |
| U1 | `NOT_RUN`（当前候选） | 旧候选 PlayMode 1/1 只作历史；未在新代码上跑真实 Unity。 |
| U2 | `NOT_RUN`/`PARTIAL` | 已新增 Unity 延迟 raw 用例并静态编译；未执行 Unity/Test Player。 |
| U3 | `BLOCKED_ON_RECOVERY` | 依赖 A 的 controller/deadline/viewer 修复；未跑 Unity。 |
| P1 | `BLOCKED_ON_PRIVACY_AND_RECOVERY` | 无新 Player 包；旧 Test Player 与独立 exe 非同包，不能冒充。 |
| P2 | `BLOCKED_ON_PRIVACY_AND_RECOVERY` | 已有 .NET/PlayMode 恢复对账场景；Windows Player 未执行、未同包。 |
| C04-G5-01 | PARTIAL | 合法非空 sleeve 正对照已在 .NET 建立；最终 Unity/Player typed 判定待 R1-01/同包。 |
| C04-G5-02 | PARTIAL | 私有费用/ledger 非空正对照已在合法公共命令下建立；公流白名单最终 raw 待 R1-01。 |
| C04-G5-03 | PARTIAL | 旧 raw 延迟 receive 在 .NET 拒绝；Unity/Windows 实际 receive 未执行。 |

### 验证命令、原生退出、计数与证据

- .NET 构建：`D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo -v:m -nr:false`，exit 0、0 warning/0 error。
- LocalHost 全量：`D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -noLogo -automated -parallelMode none -failSkips -result-xml build/328.../localhost.xml`，**native 0、158/158、failed/skipped/errors/not-run=0**；XML SHA `003D94BB17E2FE52D2E1F8C6D69E99541479BA48E32CF0D7261DA7A8B2FE680C`。
- TableRuntime：同模式 `...Mahjong.TableRuntime.Tests.dll`，**native 0、483/483**；XML SHA `C6BB07FE615A3AB978F073E78601F645FCFED6E7B8C4BDC5011F5593F71D0740`。
- 慢队列/非空袖牌/费用/旧 raw/恢复对账的 5 个 `C04PrivacyStreamMatrixTests` 均含在 158/158；未用 skip/空测试/旧 DLL。
- Unity 静态编译检查（仅类型/语法，不是 Unity 运行）：PlayMode 文件对 Luna D05 预建 App/UI/Client 程序集编译，exit 0、1 个既有 nullable warning、0 error；Editor 构建脚本对 UnityEditor/UnityEngine 编译，exit 0、0 error。DLL SHA：PlayMode `7975B44376A41E380DF745BA7CBD00F907EFA17B1C12F90D3F7C78A912277E74`；Editor `0AEC7886B5FDBFFB20AF8A8CB4E9D5459E17726DC5C323ADCDFA56920597CE23`。
- verifier 组件（不是正式隐私 PASS）：build exit 0；`self-test unused.json` exit 0，仅输出 `SELF_TEST_PASS positive=1 negative=5`；DLL SHA `EC36FAA4362385673B7516742FB5CC5AEE42225C42900B3F57EE84A9A4378F56`。R1-01 四变体仍按旧 verifier 误 PASS，不能引用该 self-test 作为 G5 证据。
- 本轮源码身份：R1 代码提交 `b560a9bda85a9e145c8f75f6d846a3d458dd965e`。关键文件 SHA：`LocalPrivacyScenario.cs` `45EB16E14DC065E14844839640734E279E07EE7C2486CC0F73BBA84149F64857`；`C04PrivacyStreamMatrixTests.cs` `A6723C924CF45654964C321F9902C255E939F7CC5D5507D88BC7AE4CFE570310`；runner `82B47FEB88D95F012139B8A742FDFEF33C75894CD218E4944560C26F43676421`；runner tests `0A735560F636AED51D95B3071F1999B586AA4C42693DD8638309CFADFA552710`；Unity PlayMode `AAA9962EBA4398B439667F49FD1ECF57A74B7B848599306601D5806143092305`；Unity Editor `5EAE5135255EE4AF3C449A877A8C0D79D66B37F177E81279054B30628D28D84B`。
- 无新 Player/包/Windows XML 原件；P1/P2 不产生 PASS。

### 首次失败、重试与未覆盖

1. 首次独立矩阵运行（`build/dfa97ceb206b4e1ebfe48371c3d12ee7`）2/5 失败：慢 channel 直接 `OpenAsync` 后没有发送 Hello，读 Welcome 有界超时；SleeveBloom 正向 offer 实际只有 2 个候选 tile，测试曾误写 3。已改为真实 `TableSession` 打开慢 spectator、候选数断言 ≥2；原首失败 XML SHA `BB68EE465AFCEF9C3BAB2DE5F96DA536C16842F67B7BD29242765DEA4F84BE08` 保留。
2. runner 自测首轮因 dot-source 参数同名、`$Pid` 自动变量、测试 mutex holder 脚本插值失败；修正后 9/9。保留 `build/runner-tests-first.stdout.log`、`...second...`、`...third...`。
3. 没有执行真实 Unity/Windows Player/Player build；没有 Android/IL2CPP/真机/Postgres/WSS。不能把这部分写成已覆盖。
4. R1-01/R1-02 修复未集成；B 不修改 A/Luna 文件，也不自行 cherry-pick。

### 依赖集成后的接续命令

父代理/专属 reviewer 在 A 与 Luna 增量审查、集成并冻结新 HEAD 后，从本线程（不是已删除的 `thread_20261102_224144_b057`）继续：

```powershell
# 1) 同一冻结候选上的 runner 短自测（先过，再昂贵）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  tools/tests/C04PrivacyRunner.Tests.ps1 `
  -TestRunRoot build/<new-guid>

# 2) .NET 矩阵（先在集成树 clean HEAD 上 fresh build，再运行）
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo -v:m -nr:false
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -automated -parallelMode none -failSkips -result-xml build/<new-guid>/localhost.xml

# 3) 真实 PlayMode（必须使用集成后 Luna verifier 与 A 恢复修复）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -RunRoot build/<new-guid> -ExpectedSourceHead <frozen-head>

# 4) 同候选 Player build 与 Windows Test Player；marker 必须由本次 build 产生
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -RunRoot build/<new-guid> -ExpectedSourceHead <frozen-head> -BuildPlayer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -RunRoot build/<new-guid> -ExpectedSourceHead <frozen-head> -WindowsPlayerTests `
  -PlayerBuildMarker build/<player-build-guid>/player-build.json
```

`-WindowsPlayerTests` 的 Test Player 与独立 `Mahjong.exe` 仍是不同二进制；在父代理明确同包边界前，P1/P2 只能按实际身份记录，不能因 XML 1/1 改写为正式同包 PASS。

## D05 边界映射和剩余事项（R1 前历史，已被上表更新）

以下三行是 R1 前历史映射，保留以说明旧证据边界；其中“队列饱和测试缺失”“N6/U3 全矩阵仍 PARTIAL”“P2 未覆盖”等表述已被本轮新增场景和固定行表更新，不再作为当前实现缺口。

- D05-01/02/08：N1、N2、N5、N8 覆盖 room、seat/role ticket、一次性消费与 attach/detach。D05-03/09：N2 覆盖 spectator 公共视图与只读拒绝。D05-04/12：N5 覆盖 viewer 关闭不销毁 room、viewer 独立游标和人数限制；队列饱和测试缺失。
- D05-05/06/13：席位控制计划、claim/release epoch 和 deadline 的部分实现与既有控制测试存在；N6/U3 全矩阵仍 PARTIAL。D05-07：N4/U2 覆盖 binding/session fencing。D05-14：N7 通过，恢复原 scope 的实现级证据通过，但 P2 未覆盖。D05-15 的 `SpectatorSession`/跨平台行为仍按已接受边界，不扩展为本轮实现范围。
- G5-01/02 未完成的最小依赖是能够由既有合法公共命令产生袖牌内容与费用的真实游戏状态；不修改或代做 D04 三绝技人工范围。G5-03/P2 的最小缺项是将旧 wire 延迟投递及请求恢复场景加入真实 Unity/Windows Player producer 路径。

## 首次失败与重试记录

1. 旧 Player Test 编译首次因 `SpectatorChannelPlayModeTests.cs` 无条件使用 `UnityEditor.SerializedObject` 失败；按父代理明确批准仅改两个 UI fixture 字段注入方式，保留 case/所有断言后重跑，Windows Player 测试通过。
2. MTP 并行全量一次触发既有 Riichi fixture exact+1 stateVersion 断言失败；另一次 N7 测试 comparator 对 digest 字母大小写敏感，实际 canonical digest 正确。修正本轮测试比较后，串行完整 xUnit 154/154 通过；未改任务范围外的 Riichi 测试文件。原 XML/log 保留。
3. `ff67…` build 未知退出状态按 UNKNOWN 留档；不清理、不覆盖。最终新鲜 `7b45…` run 正常完成，才形成正式 Player 产物。

## 交接判定

R1 前历史结论：实现、可重复的 .NET/Unity/Player 实际验证、raw verifier 和清单曾封存；未覆盖项显式 BLOCKED/PARTIAL。该历史不覆盖本轮：R1 新候选最终 Unity/Windows 同包矩阵仍 `BLOCKED_ON_PRIVACY_AND_RECOVERY`，等 A/Luna 集成冻结后按上节命令接续；B 的独立组件已完成，D05 未完成、未批准。代码提交 `b560a9bd` 与本 docs-only 报告提交分开；不推送、不创建 PR/MR、不合并、不部署、不改 scheduler，不复活已删除的原合并线程。