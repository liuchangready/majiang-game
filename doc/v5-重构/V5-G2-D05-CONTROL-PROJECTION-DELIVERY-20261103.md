# G2-D05-CONTROL-PROJECTION 交付与范围阻塞记录

日期：2026-11-03（Asia/Shanghai）
状态：**第 2 轮部分修复，组件验收阻塞 / D05-CONTROL-01 未关闭**。普通真实控制回执与回执查询路径均已证明可通过 correlated full baseline 更新 client snapshot；deadline/自动接管后控制的真实 stream 场景仍会停在 Synchronizing，未完成完整矩阵。本报告没有把部分通过称为组件接受，也未重开 D05 矩阵、隐私/恢复组件或 C01/C02/C03/D03。V4 仍为生产基线；D05/G2–G5 仍未通过。

## 身份、范围与任务结论

- 工作树：`E:/majiang-review/g2-d05-control-projection-luna6-20261103`
- 分支：`wt/v5-g2-d05-control-projection-luna6-20261103`
- 冻结 HEAD/base：`4842dbd6aab16e40c8f952b170842df06e6c0d2f`
- SDK：`D:/Unity/dotnet/dotnet.exe`，8.0.426；E 盘可用空间 52,648,816,640 bytes。独占 ignored 输出根：`build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/`；TEMP/TMP 在其 `tmp/`。
- 第 2 轮批准的范围为四个路径：`LocalTableRuntime.cs`、`LocalControlTests.cs`、`TableSession.cs`、本报告。`LocalTableRuntime.cs` 保持冻结源版本；`TableSession.cs` 是本轮唯一生产改动，LocalControl 回归使用真实 `ITableChannel` 与 `SnapshotChanged` full-baseline 事件；当前四条路径以外无 tracked diff。
- 缺陷总数 **1 组 MAJOR，0 BLOCKER，0 MINOR**。这是原 D05-I3 公共控制投影缺陷，连续历史仍为 B 第 9 轮；没有重置 20 轮规则，也没有新发现缺陷。
- 普通控制回执目标回归已通过，但 deadline/自动接管流同步仍未通过完整 LocalControl 验收；未将部分候选声明为可接受。没有 push/PR/merge/deploy/机器设置变更；未写 `complete.flag`。

## 全部已知缺陷（单组）

### D05-CONTROL-01 — MAJOR（原 D05-I3 遗留）

**位置：** `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs:428`（`TerminationSink = new DebugTerminationSink()`），`:727`（现有同步入口），`:1612`（`HandleControlCommandAsync`）；`dotnet/src/Mahjong.Client/TableSession.cs:893`（同 binding、相同 cursor 的 full push 去重）；回归位于 `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs:105` 起的 `ClientClaimReleaseProducesDurableRecordedResults`。

**复现命令：**

```powershell
$env:G2_CONTROL_EVIDENCE_DIR='E:\majiang-review\g2-d05-control-projection-luna6-20261103\build\g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68\raw-control-evidence-r6'
D:\Unity\dotnet\dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -method Mahjong.LocalHost.Tests.LocalControlTests.ClientClaimReleaseProducesDurableRecordedResults -noLogo -noColor
```

真实调用链为 `LocalTableHost → ITableChannel → TableSession`。测试保存三组真实控制请求/receipt 原始二进制，并对比真实 `TableSession.PlayerSnapshot` 与只用于关联诊断的 actor 控制快照。保存文件在本树 ignored 的 `raw-control-evidence-r6/`，不含整帧私有快照或 Welcome/ticket。

**实际：**

| 阶段 | 实际 raw request | 实际匹配回执 | 客户端投影 | actor 诊断 |
|---|---|---|---|---|
| 初始基线 | — | — | epoch 0 / automated=false / cursor 0/1 | — |
| Release | request `control-0-dd2f58bc149d42908622c72bcda6c48e`，ReleaseControl，controlEpoch 0 | 同 ID `Recorded/LOCAL_CONTROL` | epoch 0 / automated=false | 后续真实 actor 状态显示已变化 |
| Claim | request `control-1-b85313916c3246a9902f1625504be66a`，ClaimControl，controlEpoch 1 | 同 ID `Recorded/LOCAL_CONTROL` | epoch 0 / automated=false | — |
| 无变化 Claim | request `control-2-e389e3a3b68e40dca9784026da8d0e24`，ClaimControl，controlEpoch 2 | 同 ID `Recorded/LOCAL_CONTROL` | epoch 0 / automated=false | epoch 2 / LocalHuman / controlVersion 2；该无变化请求未再推进代次 |

三条成功回执的 `CommandVersion`、`EventSeq` wire presence 为 true、值为本地控制回执定义的零；`CommittedAtMs` 未出现。三条请求 bytes 分别为 187/189/189 bytes，三条 receipt bytes 各 212 bytes。回归退出 **native 1**，xUnit `Total=1, Failed=1, Errors=0, Skipped=0, Not Run=0`，失败为 `transition=0; expectedEpoch=1; expectedAutomated=True; clientEpoch=0; clientAutomated=False`。

独立返修尝试还验证了“只改 LocalRuntime 重订阅”不够：临时实验经真实 actor 生成 epoch 1 / automated=true 的完整私有帧，但 producer cursor 仍是 `0/1`，与 TableSession 已确认 cursor 相同；客户端按既有相同 cursor 去重规则丢弃该 full push，snapshot 仍为 epoch 0。该实验 patch 只保存在 ignored 证据目录，没有保留在 tracked 源码；没有伪造 cursor、反射或直接写 client cache。

外部 R9 原件同样记录成功 `Recorded/LOCAL_CONTROL` receipt 和控制投影不一致：`E:/majiang-review/g2-c04-d05-acceptance-matrix-ds-20261102-r1/build/reviewer-r9-afe017eef05c47788407409095877efe/real-control-run.log`，SHA-256 `10FCDA3CFA0064ED16A34562C9F341CFE6A73CD910FE806F46D500080B74592C`；原完整判定 `REVIEW-FINAL.md`，SHA-256 `A8E9E23475D92395287F2D49A99D91494D3640C7D8F6330DD20D1E44F171E383`。本轮没有重跑该 helper。

**期望：**同一真实 session 每次变更在对应 Recorded receipt 后，经生产同步通道观察到正确 automated/human 与递增 control epoch；无变化 Claim 保持 epoch 2，不重复副作用。

**证据路径与 SHA-256：**

- 本轮三次控制复现汇总：`build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/raw-control-evidence-r6/control-projection.txt` — `8196B67136E73980DF907C722A161CD2845F52D0798ECB12FA18F769172DC98B`
- `outbound-control-request-0.bin` — `5A2396919CAE27332383768E1EFEB7D0A22E63C8967C95B4518158E88F29B402`
- `outbound-control-request-1.bin` — `DCC94F1D2F655991F5692D9B830FC773C62BD881E073AE6FAA9238766EEA785E`
- `outbound-control-request-2.bin` — `84212EBC4B61A9988EA53CB117F62F87AFE4EBDA6EC3C8972E116B108FD4F701`
- `inbound-control-receipt-0.bin` — `51DE3615FF636D71475C3A48D330BBFF3EEEA3B5FA68FF6082337D6D45B4B20E`
- `inbound-control-receipt-1.bin` — `39905B14BA29775B22A5E00168F766D295F8923CC9B42F968402D72A232D68CE`
- `inbound-control-receipt-2.bin` — `B91C409DC58D8C3C52275A7F2D7D7B232B9394429406A25783FF7595C4604C0E`
- 目标单测日志 `final-control-repro-r6.log` — `FB65DCE7ADB106A46851BDC32912F63C07B35397C202A7C9C38EF23C87FA6655`
- LocalControl 全类日志 `localcontrol-class.log` — `906825563B1F8D5AFE67B5FAC3CB0A4330272DBF62954ACA95687E48973B5C8B`
- 同类脱敏 wire 路径的临时重订阅实验日志 `targeted-control6.log` — `924BE878D6256C1477F1BB6DE6C56E83B3508974A99ADFD41D44A3F42FA6A6A7`；临时 patch `localruntime-resubscribe-experiment.patch` — `6BEA3023F82287A5253700BDCBF1AE461F20A52875A8CEAF32834CC9618D15B2`。

**修复要求：**当前三文件不足。只允许的 `LocalTableRuntime.cs` 无法令 `TableSession` 接受 actor 新 sync epoch 的 full push：actor 为新 sync epoch 合法重置 projection cursor 至 0/1，而 Client 不能在没有活动 sync correlation 时接收相等 cursor 的 unsolicited full push。严禁在 LocalRuntime 伪造更大的 cursor、反射/写缓存、放宽 TableSession 的重复帧保护或改 TableActor 旧 epoch 关闭安全。

**精确最小扩围：**增加一个 tracked 源码路径 `dotnet/src/Mahjong.Client/TableSession.cs` 的修改授权。让 TableSession 在收到匹配命令的 `Recorded/LOCAL_CONTROL` 后，通过现有 `RequestFullBaselineAsync` 请求正常 correlated full baseline；现有 `LocalTableRuntime.HandleSyncAsync` 处理此真实 SyncRequest 并返回当前 actor 快照。客户端只在当前 session/binding/scope 的匹配 SyncResponse 到达后，才将该控制命令视为已完成投影观察。已授权的 `LocalControlTests.cs` 延续真实通道断言并等待实际 snapshot 更新；不需扩到 Client 测试文件即可做此组件验收。这样走现有相关同步合同，保持 actor 的旧流安全关闭与客户端同 cursor 防回退规则。

**统一负例与验收断言：**改动获授权后一次覆盖 Release→Claim 真实 receipt 与 client epoch/automation；无变化 Claim 不增 epoch/receipt side effects；同 ID 同 bytes 重放仅回原 receipt、不同 bytes 冲突；claim 后原 round/decision/epoch stale action 被业务拒绝且目标 receipt 不含 CommandVersion/EventSeq/CommittedAtMs、无 command/event/ledger/entity 副作用；其他合法连接/四席和 spectator 隐私不受影响；错误 session/binding/role 与旧 generation 回调不得覆盖 baseline；close/dispose 竞态有限完成。producer、request ID、receipt、sync correlation 与最终 client snapshot 必须在同一执行日志/证据中关联。现有 B9 stale-action/精确 reason/拒绝无提交元数据/幂等与隐私恢复接受项保持关闭且不重开。

## 验证记录与状态

| 检查 | 实际结果 | 状态 |
|---|---|---|
| A：SDK/路径/空间/范围 | SDK 8.0.426；base/branch 正确；E 盘约 52.65 GB 可用；输出与 TEMP 独占 | PASS |
| 首次真实通道失败保存 | `dotnet test ... --filter FullyQualifiedName~ClientClaimReleaseProducesDurableRecordedResults` 因 Testing Platform runner 不使用 VSTest filter，实际执行 LocalHost 167 项：166 pass / 1 fail / 0 error / 0 skip，native 1；原始 `baseline-failure.log` 保留 | FAIL（缺陷复现） |
| 目标回归 Release build | `dotnet build ... -c Release --no-restore` fresh 编译当前 test 与依赖，0 warning/0 error，native 0 | PASS（仅构建） |
| 目标 LocalControlTests 方法 | 真实 Release→Claim→无变化 Claim、raw request/receipt；Total 1 / Failed 1 / Errors 0 / Skipped 0 / Not Run 0，native 1 | FAIL（D05-CONTROL-01） |
| 当前全套 LocalControlTests 类 | 23 total / 22 passed / 1 failed / 0 error / 0 skip / 0 notrun，native 1；其余既有控制测试均通过 | PARTIAL，投影阻塞仍在 |
| 临时 LocalRuntime 重订阅实验 | build native 0；单例 Total 1 / Failed 1 / Errors 0 / Skipped 0 / Not Run 0；producer 帧 epoch/automation 正确但 cursor 相等，TableSession 仍旧 | FAIL（证明文件范围不足；patch 已撤回） |
| 实验中一次完整 LocalHost suite | 第一次试验等待旧设计未完成 baseline；对本任务自有 runner 正常 Ctrl+C 取消。`RECOVERED_AFTER_INTERRUPTION`，原生退出/用例计数 UNKNOWN；不计 PASS，原日志保留 | UNKNOWN |
| LocalHost/TableRuntime 完整候选套件、Client 相关回归 | 未运行于最终候选；因必须扩入 Client 源码才能修复，当前继续运行无助于消除确定范围阻断 | NOT_RUN |
| Unity/Windows/GAME/D05/G2/G5 | 未运行、不占 Unity 锁 | NOT_RUN |

原 R9 的 7/7 false-positive 历史继续保留；原 Unity 和 Windows 各 6/7 失败仍是原矩阵证据，不因本轮纯 .NET 复现覆盖或重算。

## 交付闭合

- 已知缺陷全集仍为单组 D05-CONTROL-01 MAJOR。没有把诊断实验当修复，亦没有新追加缺陷。
- 本轮改变：`LocalControlTests.cs` 新增实际 `TableSession.PlayerSnapshot` 断言、真实 channel wire capture（仅在 `G2_CONTROL_EVIDENCE_DIR` 指定的 ignored 目录保存命令/回执和脱敏投影事实），以批量执行 Release/Claim/无变化 Claim；此断言当前真实失败。最终文件 SHA-256 `F5A303DDA5A76576C13E868BE4768C3008F48FE92D9642E6AC4F02E07C23D554`。
- `LocalTableRuntime.cs` 与冻结 base 无差异；最终 SHA-256 `97F032057A8310C61E7F5D7E7D259E0B8B2BD32FBF9A014444BA98D424D5E851`。未修改 Client/Domain/Contracts/TableActor/隐私/恢复/Unity/生产 deadline/权限代码。
- 由于缺陷仍无法在授权边界内关闭，未做代码或 docs-only 提交；没有 clean status 声明。最终 `git diff --check` 为 0，tracked diff 仅 `LocalControlTests.cs` 与本交付报告。
- 独立 reviewer/父 caller 可以用上述失败原件复核，并决定是否仅扩入 `TableSession.cs`；不要求原 D05 矩阵 B 线程重复空转。获准扩围前，组件和所有 Unity/Windows 矩阵仍未通过。

## 第 2 轮执行补充（2026-11-03）

### 缺陷映射与当前结论

本轮没有新增缺陷 ID；所有发现合并为同一组 **D05-CONTROL-01 / MAJOR（原 D05-I3，B 第 9 轮）**。新增证据显示普通 control receipt 同步已修复，但同一控制流生命周期内还存在 correlated baseline 无法完成的场景，故不关闭该 ID。

- **生产映射：**`dotnet/src/Mahjong.Client/TableSession.cs` 的 `HandleReceiptAsync` 与 `HandleReceiptLookupAsync`：仅在 Claim/Release、`LOCAL_CONTROL`、`Recorded`、digest 匹配分支中，先 `ResolveTrackedAsync` 再调用 `RequestFullBaselineAsync("CONTROL_APPLIED", force:false, ...)`。未更改 cursor、floor、同 cursor 防回退、actor epoch 保护或缓存写入。普通真实同步链是 `TableSession → SyncRequest(correlation, KnownCursor) → LocalTableRuntime.HandleSyncAsync → actor resubscribe → correlated SyncResponse → TableSession.SnapshotChanged(full baseline)`。
- **回归映射：**`ClientClaimReleaseProducesDurableRecordedResults` 等待 receipt 与真实 `SnapshotChanged` full-baseline 事件，并断言 client snapshot：Release 后 epoch 1 / automated=true；Claim 后 epoch 2 / automated=false；无变化 Claim 仍为 epoch 2 / automated=false。生产通道原始字节、actor 诊断及 projection 事实均在 ignored `raw-control-evidence-r15/`。
- **回执查询映射：**`DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` 增加 receipt-query resolution 与后续真实 full-baseline 事件的关联屏障。该方法单独执行曾 1/1 通过；完整类 / 完整 LocalHost 执行中，回连初始 baseline 曾停在 Synchronizing，属于当前同步缺陷组，不以单独绿跑掩盖。

### 新增的同组复现

**位置：**上述两个 `TableSession` receipt 分支；deadline/旧 epoch 生产回归 `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs:947` 起的 `ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch`，有效 control 在权威 deadline 自动接管之后提交。

**复现命令：**

```powershell
D:\Unity\dotnet\dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -method Mahjong.LocalHost.Tests.LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch -noLogo -noColor
```

**实际 vs 期望：**目标候选在成功 `Recorded/LOCAL_CONTROL` 后 `TableSession.State=Synchronizing`，10 秒有界等待内未收到 `IsFullBaseline=true` 的 `SnapshotChanged`；`TableSession.LastError` 与 `LocalTableRuntime.LastCommandException` 均为空，runtime 最新帧 cursor 为 `90/48`（另一完整类运行观察到 `90/46`）。因状态未恢复 Ready，后续同 session 旧 epoch / 新 epoch 断言无法继续。期望该控制 receipt 后由当前 binding 的 correlated full SyncResponse 完成 baseline，恢复 Ready，随后原 stale epoch 拒绝和合法新 epoch 断言照常通过。

当前最小批准变更只触发一次 `force=false` baseline request；不能在 `TableSession` 伪造 baseline / cursor 或重试轮询来掩盖无响应。第 1 轮“仅 Runtime 重订阅的实验”也已证明旧流关闭与投影重建需要完整生命周期关联。本轮指定原三文件保持不变，因此这份复现是当前审批范围内无法自修的边界证据；如果下一审批要继续，仅能基于该真实请求/stream 失败精确扩展本地同步通道终止/重订阅协调路径，仍不得改 actor、Client 其他文件、权限或 cursor 契约。

**统一修复要求与验收断言：**D05-CONTROL-01 仍要求：匹配 Recorded receipt 及 receipt-query/replay 均完成相关 baseline；真实 LocalHost deadline/stream termination 后同 session 恢复 Ready；旧代次回调不能覆盖新 baseline；原旧动作精确业务拒绝且无提交 metadata / 无 ledger 或实体副作用；同 ID 同 bytes 仅回原结果、不同 bytes 冲突、无变化 control 不重复副作用；close/dispose 有界完成；四席与观战私有隔离继续成立。必须一次批量通过完整负例矩阵，不得删减既有断言。

### 第 2 轮命令与计数

输入身份仍是冻结 base `4842dbd6aab16e40c8f952b170842df06e6c0d2f` 与当前候选源码；无 cherry-pick。Release LocalHost 项目先 `clean -c Release`（0 warning/error），再 clean 后 `build -c Release --no-restore`（0 warning/error、native 0），生成的同候选 DLL 执行以下直接 MTP 命令：

| 命令/范围 | 结果（native / xUnit Total, Failed, Errors, Skipped, Not Run） | 证据 |
|---|---|---|
| `-method ...ClientClaimReleaseProducesDurableRecordedResults` | native 0 / 1, 0, 0, 0, 0；通过 | `final-control-repro-r15.log` SHA-256 `62B3120C26F5974CE01B79FCD81D33854B92A175A927A5F8D6DA4AA40C9C64CE` |
| `-method ...DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` | 单独 native 0 / 1, 0, 0, 0, 0；通过；包含真实 receipt-query 与后续 baseline 事件屏障 | `targeted-lookup-r14.log`（ignored） |
| `-method ...ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch` | native 1 / 1, 1, 0, 0, 0；失败：receipt 后 10 秒无 full baseline，状态 Synchronizing | `targeted-deadline-r12.log` SHA-256 `EFBFF4411CCA7F6E412793E7C88FD35305AFB362D4AEDA84A6CF5299F7342D4D` |
| `-class ...LocalControlTests`（最终候选） | native 1 / 23, 2, 0, 0, 0；失败为回连初始 baseline 与 deadline 后 control baseline | `localcontrol-class-r15.log`（ignored） |
| 完整 `Mahjong.LocalHost.Tests.dll` | native 1 / 167, 3, 0, 0, 0；两项控制同步失败，另 Chankan 测试一次超时 | `localhost-suite-r14.log` SHA-256 `A2571CB12100961979A3BF26266FD05A81F93064849B4156D860D366D9B8A78F` |
| Chankan 失败用例单独复跑 | native 0 / 1, 0, 0, 0, 0；通过，认定完整并发批次的一次性超时，不另立产品缺陷 | `targeted-chankan-r15.log`（ignored） |
| 完整 `Mahjong.TableRuntime.Tests.dll` | native 0 / 483, 0, 0, 0, 0；通过 | `tableruntime-suite-r14.log` SHA-256 `A38EF90DA552F9D51882C51DDE362C2A2BA9477ECC5FC68B19C79EBA7726FA31` |
| 完整 `Mahjong.Client.Tests.dll` | clean 后缺少 assets 的第一次 build 为 native 0 / Not Run；随后 restore + Release build（0 warning/error）后执行；native 0 / 182, 0, 0, 0, 0 | `client-tests-suite-r15.log` SHA-256 `E5D653E8746E5365DEAC05E0DFCBA1899A679C0747C5603204F678CB358F1253` |
| Unity/Windows/GAME/D05/G2/G5 | 未运行；无 Unity 锁与环境结论 | NOT_RUN |

完整 LocalHost 首次出现的一次 Chankan 候选超时已针对同一 fresh DLL 单独重跑通过；不把它加入 D05 缺陷组。回连单项独立通过与完整类中的初始 baseline 卡住也均保留，后者不计 PASS。

### Raw 输入、receipt 与投影

最终成功目标执行 `raw-control-evidence-r15/control-projection.txt` SHA-256 `6A6FEE594DC72BC2344C050AB3FA3B9663E6D6957BF3A8A1121A233CD4A97775`：

| 次序 | raw request / kind / request epoch | 匹配 raw receipt | receipt 后 client snapshot |
|---|---|---|---|
| 1 | `control-0-e671cfba03d54e4aa7703323ab6b6e52` / ReleaseControl / 0 | 同 ID `Recorded/LOCAL_CONTROL`, `replayed=False` | epoch 1 / automated=true |
| 2 | `control-1-c57a8e66f8c244dfa74d80ee8f2c04d8` / ClaimControl / 1 | 同 ID `Recorded/LOCAL_CONTROL`, `replayed=False` | epoch 2 / automated=false |
| 3 | `control-2-b6bdcc957b73428bbd9c51b6bd4eea7e` / ClaimControl / 2 | 同 ID `Recorded/LOCAL_CONTROL`, `replayed=False` | epoch 2 / automated=false（无变化）|

三条回执均有 `CommandVersion` / `EventSeq` presence，`CommittedAtMs` absent；最终 client、producer 与 actor 均 epoch 2 / human，控制副作用版本 2。该成功基线的 cursor 为 `0/1`，由 correlated sync baseline 得到；没有制造更高 cursor。首轮失败仍保留于 `final-control-repro-r6.log` SHA-256 `FB65DCE7ADB106A46851BDC32912F63C07B35397C202A7C9C38EF23C87FA6655`：Release/Claim/Claim 分别 observed client epoch `0/0/0`，actor 最终 epoch 2，作为先失败证据。

本次 r15 三个原始 request 文件 SHA-256：request-0 `A21460678A3D28922824932B958DBFD47A17E6393800798B2CC86D265D388532`；request-1 `C005E55F626F802A10812CA9011AE99274B89AD800D9F5F6F4FBD470D8A70A5D`；request-2 `14FE42EFC8CE38F13B9880C72CAEEA28CC039687DBACE6D1CF331D53C11E5E8A`。对应 receipt-0 `DF71AB749CE2D80E7E36CB8749D4357B9CE0AC82B83A8C2F5FC743E755E314F8`；receipt-1 `906B929FBCAF0AC77E272C8095A3C9DCCFD3939B10D2D3A679DD74D52CD997A3`；receipt-2 `85A7D48AA274BE1DD7390A2458040607DCCB2F0329BE3C5C3046B48AF454F5D9`。

### 路径、diff 与未覆盖

本轮源码 SHA-256：`TableSession.cs` `7F658A57DAA9FCEC883AFD72112AF2C42D5834500B62FB9ACFE90F803196857F`；`LocalControlTests.cs`（包含真实 receipt/baseline 关联测试与失败诊断）`E3E9F453D9D443C4748C346B0F7C0203251208A4454171B5FBE475F713D0DA08`。`LocalTableRuntime.cs` 与冻结版相同，SHA-256 `97F032057A8310C61E7F5D7E7D259E0B8B2BD32FBF9A014444BA98D424D5E851`。代码 + 回归本地提交 `877e3cd1`，文档独立提交随后单独记录；`git diff --check` 通过。当前两提交可检视，但 D05-CONTROL-01 尚未全矩阵通过，因此状态仍 BLOCKED、未组件接受。未覆盖且仍需完整验收：全部负例矩阵、同 ID/冲突/无变化副作用、四席+观战隔离、其他连接 baseline 保留、错 binding/role/旧代次回调、close/dispose 与终止竞态、full LocalHost 无失败、同包最终矩阵复核。
