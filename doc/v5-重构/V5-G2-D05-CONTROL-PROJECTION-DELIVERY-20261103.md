# G2-D05-CONTROL-PROJECTION 交付与范围阻塞记录

日期：2026-11-03（Asia/Shanghai）
状态：**第 3 轮批量修复候选已完成，等待专属 reviewer 复核；不自行 approve 或建 MR。** 本报告的第 2 轮内容保留为历史记录。当前 D05-CONTROL-01 候选已通过 LocalControlTests、完整 LocalHost、TableRuntime、Client Release 验证；组件是否接受仍由专属 reviewer 与原 D05 矩阵线程裁决。没有重开 D05 矩阵、隐私/恢复组件或 C01/C02/C03/D03；V4 仍为生产基线，D05/G2–G5 仍未通过。

## 第 3 轮独立复核返修记录

### 已知缺陷全集：D05-CONTROL-01 — MAJOR（0 新缺陷组）

- **历史与范围：** 原 D05-I3 / B 第 9 轮；缺陷计数不因任务名或轮次重置。R2 独立复核仍报 1 组 MAJOR，review-root 为 `build/reviewer-control-r2-567b879bf683476e86d2fb2994ee344e/`，`localcontrol.log` SHA-256 `EBAA618312898FA344757F29701BABB1D34AD0FD5BA84E8C37FBE16389B9709D`、XML SHA-256 `1C93B9F40CF543D4342767BA6DEFFAAD293CD12ADBE6CF01E76A2B7447BB1BE5`，实际 23 项 / 22 pass / 1 fail。该轮要求同一 receiver 批量修复 deadline、重连与已前进 projection cursor 生命周期。当前批准且唯一 tracked 范围为 `LocalTableRuntime.cs`、`LocalControlTests.cs`、`TableSession.cs`、本报告四个文件；R1 三文件不足只作历史记录。
- **位置：** `TableSession.HandleSyncResponseAsync` cursor floor guard 与控制 baseline 生命周期；`RequestFullBaselineAsync`；`LocalTableRuntime.HandleSyncAsync` / `OnFrame` / `TableStreamSink`；`LocalControlTests.ClientClaimReleaseProducesDurableRecordedResults`、`ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch`、receipt-query/replay 与旧帧回归。
- **复现命令（修复前 fresh 失败）：**

  ```powershell
  D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal
  D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -method Mahjong.LocalHost.Tests.LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch -noLogo -noColor -result-xml build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/deadline-diagnostic-r3.xml
  ```

- **修复前实际 vs 期望：** 回执 `deadline-release` 为 `Recorded/LOCAL_CONTROL`，session `loc-8` / binding 1 / Player seat 0 / scope `loc-7`；匹配 correlation `sync-5` 的真实 `SyncResponse` 确实到达，cursor `66/1`、control `epoch 1 / automated=true`，客户端拒绝点为 `drop-cursor-floor`，已确认 floor `1/3`。此前 actor 每次新 sync epoch 重置 projection cursor，早期 push overflow 又不断提升 StreamVersion，导致同 binding 的重试仍低于双分量 floor并卡在 Synchronizing。期望是在不降低 floor、不伪造 cursor 的前提下，通过现有 transient reconnect / host-issued binding 合同恢复真实 baseline；同 session 的正确 client snapshot 到达后继续 stale/合法动作断言。
- **修复要求与映射：** `TableSession` 只对 `CONTROL_APPLIED` 引发的、身份及当前 correlation 均已匹配的 cursor floor 回退，关闭当前 channel 并进入既有 transient reconnect 状态；普通 stale sync 仍按原合同丢弃并等待。由现有真实 ticket/binding 恢复路径取得新 binding 后重发相关 baseline，不写 cache、不改 floor、不扩权限。`LocalTableRuntime` 为每次订阅分配新 callback generation，并校验完整 session/audience/binding/syncEpoch identity；旧 generation 的在途帧不得消费新 correlation。两个 receipt 分支的 baseline 请求保持原实现。`LocalControlTests` 通过真实 LocalTableHost、ITableChannel、TableSession 与 host-issued reconnect ticket 验证回执到 snapshot，并延迟旧 subscription 帧作负例。
- **修复后结果：** deadline 用例通过；raw receipt 后绑定从 2 升到 3，scope 从 `loc-9` 升到 `loc-10`；`sync-6` 为当前 session `loc-8` / binding 3 / Player seat 0 / correlation、cursor `4/1`，客户端 guard 为 `accepted-full-baseline`，最终 snapshot epoch 2 / automated=false。Release 的 raw `Recorded/LOCAL_CONTROL` receipt 后 client 为 epoch 1 / automated=true；Claim 后 epoch 2 / automated=false；无变化 Claim 保持 epoch 2 / automated=false。旧 sink 延迟 callback 被 active identity/generation guard 丢弃，随后生产 SyncResponse 才完成观察。

### 统一负例矩阵

| 项 | 验收断言 / 实际证据 |
|---|---|
| 已前进 projectionSeq 的 control baseline | deadline 真实场景 floor `1/3`、incoming `1/1` 不降低 floor；正常重连取得 binding 3 baseline `4/1` 并恢复 Ready。 |
| 延迟旧 sink callback | `ClientClaimReleaseProducesDurableRecordedResults` 在真实 SyncResponse 被 gate 暂存时投递旧 frame；断言 `FRAME_DROPPED` 且 `activeIdentity=False`，再放行真实 response。 |
| receipt / lookup / replay | 完整 `LocalControlTests` 23 项通过，包含 persistent outbox receipt-query、同 ID 同 bytes 精确重放与不同 bytes 冲突；目标控制 snapshot 关联匹配 receipt。 |
| no-op control / 副作用 | Release→Claim→无变化 Claim 的真实 command/receipt/snapshot 连续回归；control epoch 1→2→2。既有 request-scope / recovery 幂等断言保留。 |
| stale action / 零副作用 | deadline 测试继续验证旧 decision/epoch 业务拒绝后旧动作不提交，再验证新 epoch 合法动作；完整 LocalHost 全套通过。 |
| 多席与观战隔离、关闭竞态 | 完整 TableRuntime 483 项包含 `FourSeatsAndSpectatorsSeeOnlyTheirOwnPrivateEventAndPayload`、`ControlEpochChangeClosesTheOldSeatStreamButRefreshesSpectator` 及 stream close/recovery 回归；本轮未改 actor/权限合同。 |

### fresh 验证与证据身份

命令均使用 `D:/Unity/dotnet/dotnet.exe` 8.0.426，候选 DLL 均来自以下 Release `-t:Rebuild --no-restore`，无旧 DLL 复用：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -class Mahjong.LocalHost.Tests.LocalControlTests -noLogo -noColor -result-xml build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/localcontrol-delivery-final.xml
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -noLogo -noColor -result-xml build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/localhost-delivery-final.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noLogo -noColor -result-xml build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/tableruntime-final-full.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll -noLogo -noColor -result-xml build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/client-final-full.xml
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -method Mahjong.LocalHost.Tests.LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch -noLogo -noColor -result-xml build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/deadline-final.xml
```

| 命令 | native 退出 | XML Total / Passed / Failed / Errors / Skipped / NotRun | 证据 |
|---|---:|---:|---|
| `build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal` | 0 | 编译 0 warning / 0 error | `round3/localhost-delivery-rebuild.log` |
| `exec ...Mahjong.LocalHost.Tests.dll -class Mahjong.LocalHost.Tests.LocalControlTests -noLogo -noColor -result-xml .../localcontrol-delivery-final.xml` | 0 | 23 / 23 / 0 / 0 / 0 / 0 | XML SHA-256 `D980F2D2C9F9F07BE68432CAE391F29F35E7EC35A98D85E6418B6BF536753727` |
| `exec ...Mahjong.LocalHost.Tests.dll -noLogo -noColor -result-xml .../localhost-delivery-final.xml` | 0 | 167 / 167 / 0 / 0 / 0 / 0 | XML SHA-256 `43C29E7662C1EE3757A18DBBA522EF59783BF88A1EFB730D0E5A67298551CFDF` |
| `build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal`；`exec ...Mahjong.TableRuntime.Tests.dll -noLogo -noColor -result-xml .../tableruntime-final-full.xml` | 0 / 0 | 483 / 483 / 0 / 0 / 0 / 0 | XML SHA-256 `CB003702A219247A832CF4583B628E81783FE22AF1DEC7F98AF95500FFBE24DD` |
| `build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release -t:Rebuild --no-restore -v:minimal`；`exec ...Mahjong.Client.Tests.dll -noLogo -noColor -result-xml .../client-final-full.xml` | 0 / 0 | 182 / 182 / 0 / 0 / 0 / 0 | XML SHA-256 `236E763F9D10FE73475274A56E38EABAB9255F36D7875CF9C57A22C9CCC29C91` |
| `exec ...Mahjong.LocalHost.Tests.dll -method Mahjong.LocalHost.Tests.LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch -noLogo -noColor -result-xml .../deadline-final.xml` | 0 | 1 / 1 / 0 / 0 / 0 / 0 | XML SHA-256 `2233D0B10D58633B63BEA0E55CDBDE55410C450CC6C6014DF7CA8346180929DF` |

修复前决定性日志 `round3/deadline-diagnostic-r3.log` SHA-256 `3335A8ECE605DE6097DEAE13DEC81FDE2C15987F49B5ED6528717A106FF6F81C`；脱敏关联记录 `round3/baseline-deadline/control-sync-diagnostic.txt` SHA-256 `A1CACB96EB4DED192C100420A9AAFB8284E60F7947E5FEF5DB89D86FA5C628AF`。修复后 deadline 的 raw request/receipt、六条 sync request 与脱敏关联记录在 `round3/deadline-control-evidence-r3/`；其中关联记录 SHA-256 `DE1A7A645328BB2594412211A5E643BBD8E344BF80CC39729DDAB2DC15F7555E`，request SHA-256 `85E26E533B8318075610E2F618ED6D850B365DC422D226D3A1F03C59BF4F002D`，receipt SHA-256 `D870715C9384877CD8B8343C3959DA095E94D6AF3EF65E507780A38389316063`。三条 control raw bytes / 回执及前后投影汇总在 `round3/raw-control-evidence-r3/`；汇总 SHA-256 `1DFC339A6E3E1441A1499C11903913592EB5DD00488980046CC9AFA0F90290BB`。完整原件不含 ticket、整帧私有快照或其他座位秘密。

Release→Claim→无变化 Claim 的原始请求文件 SHA-256 依次为 `D0B86B9D40E113483718A7D9945A397B0E0A6770ECFFBB1613C970194FF6ECA3`、`5C56885208B2F433DEC4CF516F15BFA905292F6144D91250B16492438D9FA8D6`、`32B724014C7C19A02B5008405F06E5FEE72D6AC9F32D1D79E906538048945802`；匹配 receipt 文件 SHA-256 依次为 `2CA55F77D4BE5D5C6B1A69556FCA7626DEAF28CF210B2A08A5A1D56ACBBC9B3A`、`87060E8F50ECDB4CF0063CCD919F493BE490E28B4A5D0E9889820B45E29994E3`、`B4499A0619BC75F02F54F0F7E3641DEE70D1FAE8FB2ECBD8FC249716E76580F4`。日志 `round3/control-projection-final.log` SHA-256 `8C17D24EF188D04E0FA1900EA2864D907A1C663960F3FF5109440607C5839DD1`、XML SHA-256 `9A2793AD811AF3AB500D374AB3B9F71A93E3F5CA4FDDCB69E9BAC7A33C0050F4`；projection summary 中 receipt 与最终 client/producer/actor 三方 epoch、automation、cursor 一致。

最终 Release DLL SHA-256：`Mahjong.LocalHost.Tests.dll` `C984FA21C5605685B514CF4AB7F15C6895959522CF3EE3563E7B5C3112FC025A`；`Mahjong.Client.Tests.dll` `BEC33C6F00D809097ED9EAE1E62FDF1F954C87FD4C4917CEB5EBF07D0529E9D9`；`Mahjong.TableRuntime.Tests.dll` `6C2A7D8D02E642F23C031539DB6DBE7C0427AFBDFD1FCB63AF19042387FC660E`。本轮完整 LocalHost 首次运行曾有 1 项 deadline timing 偶发失败，单项复现通过，随后最终完整重建运行为 167/167；该失败日志 `round3/localhost-full-r3.log` 与 XML 保留，不隐藏这次波动。早期 Client Rebuild 在本轮控制 flag 调整前有 2 项 cursor guard 预期失败（当时错误地对任意 stale baseline 触发 reconnect）；实现已收窄到关联的 control baseline lifecycle，最终 Client 全套 182/182。历史清理构建的实际缺 assets 退出见第 2 轮记录，没有以外层 runner 状态冒称成功。

### 源文件身份、提交与边界

本轮输入 base `4842dbd6aab16e40c8f952b170842df06e6c0d2f`、上一代码提交 `877e3cd1`、上一报告提交 `39f2454d`；无 cherry-pick。源码 SHA-256：`TableSession.cs` `350CDE59C76B187C8592D21680AD07C69994918222ACF91BBD54140A1B961DE7`，`LocalTableRuntime.cs` `C49917CACF9F53E4E8A823DF7CC401868E8ACDAEE8C8042B07FF167BD9D6DD7D`，`LocalControlTests.cs` `15658CBC1929657D8E9E049D89B14A8BFFB3A1FDB54EA089C52FAB71F6F7FB5D`。仅 4 个批准文件有 tracked diff。代码提交 `207578f0715cf3fd769f54f9036f172b04955fa9`；报告单独提交。两次提交后 `git diff --check` 与 tracked clean 状态列于末节。

未运行 Unity / Player / PlayMode、Windows/Game/D05/G2 全矩阵；未验证全房新生产组包，亦不宣称 D05/G2 通过。组件候选完成后等待本任务专属 reviewer 仅复核新增差异，再由原 D05 矩阵线程进行同包最终裁决；不自行 approve、不建 MR、不 push、不写 `complete.flag`。

### 本轮提交与封存

- 代码独立提交：`207578f0715cf3fd769f54f9036f172b04955fa9`（3 个授权代码/测试文件）。
- 报告独立提交（仅交付报告；与代码分开）。提交后 `git diff --check` 通过、`git status --short` 为空；tracked 差异仅上述 4 个批准文件。ignored build/log/XML/raw evidence 留在 `build/g2-d05-control-projection-517c6fdfe4e845bdb1b0b2e441e12d68/round3/`。
- 本地候选封存后等待专属 reviewer；未 approve、未 push/PR/merge/deploy。

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
