# G2-C04-D05-R1-PRIVACY 隐私验证器返修交付

日期：2026-11-02（Asia/Shanghai）
执行器：Codex / GPT-6 Luna
独立验收：父代理及 bridge dedicated reviewer（本报告不自批）

## 1. 范围与结论

本次仅处理分工卡 A：`G2-C04-D05-R1-PRIVACY`。完成 R1-01 的 verifier / 原始采集修复，以及 R1-04 中 `tools/tests/C04PrivacyMatrix.Tests.ps1` 的有界进程与输出边界。代码提交为 `7a65577b5be5d531c5f107fcc56461553604bda2`；报告采用独立 docs-only 提交。

结论是 **组件范围 PASS，等待独立验收**。验证器从实际 WireCodec 编解码的 Welcome 与 typed projection 重建身份和私有/公开字段；29 个原始字节/身份/摘要负例均 native exit 2。进程测试通过。旧 schema-1 的 11 帧样本因没有 Welcome 原字节以 native exit 3 拒绝。未运行 Unity / Windows Player，也未把 WireCodec 工具夹具标成 GAME；最终同候选应用矩阵由 B 集成后执行。

完整独立复核输入：`CODEX-V5-G2-C04-D05-REVIEW-R1-20261102.md`，SHA-256 `E1792E3D4E32268F143A6C929AFD3223074487A7D070D937515F0974890993BB`。路由和文件分工以 `V5-G2-C04-D05-SPLIT-BRIDGE-TASKS-20261102.md` 为准。

| R1 ID | 级别 / 原问题 | 本分工结果 | 责任边界 |
|---|---|---|---|
| R1-01 | BLOCKER：只检查自报摘要，raw 未解码 | **PASS**：真实 WireCodec / generated protobuf 解码 Welcome 和 projection；真实 raw 欢迎帧随每条记录保留；合法 typed 正例和 29 项负例实测 | A；待独立 reviewer 验收 |
| R1-02 | MAJOR：冷恢复 controller/epoch 与 room/viewer/ticket 生命周期 | **未在本树修改或运行** | B（DeepSeek Harness） |
| R1-03 | MAJOR：N/U/P 与 C04-G5 矩阵缺项 | **未在本树修改或运行** | B（DeepSeek Harness） |
| R1-04 | MAJOR：runner provenance、XML/marker、timeout 收尾及 verifier tests 同步读流/无超时 | **A 的 verifier 测试脚本子项 PASS**；生产 runner 的其余缺陷仍未关闭 | 脚本子项 A；production runner B |

本报告不把整条 R1-04、R1-02、R1-03、D05、C04、G2–G5 写为完成。

## 2. R1-01 缺陷与修复映射

### R1-01 BLOCKER — Welcome 与 projection raw 未解码

- **位置：** `dotnet/tools/C04PrivacyMatrixVerifier/Program.cs`、`.csproj`；`unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs`。
- **复现输入：** 原复核树只读脚本 `E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review/run-raw-mutations.ps1`；原样本位于只读交付树 `E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1/build/55d45940b9d448f9a6df1760b3718a26/privacy-evidence/raw-frames.json`。没有在旧 R 路径运行或写入脚本。
- **原实际 / 期望：** 原 verifier 对非法 `[1]`、空 raw、seat1 raw 跨 seat 替换、只改私手计数且重算/沿用摘要都 native 0 并打印 `PRIVACY_MATRIX_PASS frames=11`。期望从真实 Welcome 和 typed projection 字节导出身份、受众、owner、版本及字段值；任何字节或摘要不符都拒绝。
- **修复：** verifier 增加 `Mahjong.Contracts.Wire` 项目引用，调用生产 `WireCodec.Decode` 并从生成的 `Envelope` / `Welcome` / `SyncResponse` / `PlayerUpdate` / `PublicUpdate` / snapshot 类型重建事实。身份链校验 Welcome body 与 envelope header 的 session、room、seat、binding epoch、owner epoch 和版本；校验 `RequestScope` 的 role、room、scope/account、有效期；校验后续 envelope 与 Welcome 的 session/room/seat/binding/owner 一致。验证 projection target、Welcome role、seat、private owner、当前版本、快照 session、游标、全量/空 genesis 规则、实际手牌实体唯一性及私有视图归属。实际解码值逐字段与采集摘要对照；摘要不再作为事实来源。未知 protobuf 字段在 envelope 和嵌套 typed tree 中拒绝。
- **原始采集修复：** `C04PrivacyProbe.RecordInbound` 收到真实 Welcome 时保留实际原字节 Base64 和 SHA；后续记录同时写入该 Welcome raw/hash 与 Welcome 解出的 scope/account/role/seat。相同 session 收到不一致 Welcome 时将 session 标为歧义，并把冲突 Welcome 的原字节保留为无有效 Welcome 绑定的记录；verifier 因记录本身不是 projection/缺少可验证 Welcome 而 fail closed，不把冲突 binding 继续采作有效正例。保留 `Begin`、`Seal`、`CapturedFrameCount`、`ResetForTest` 和既有 `RecordInbound(ReadOnlyMemory<byte>)` 签名；schema 升至 2。
- **验收断言与结果：** 完整合法 WireCodec 序列 native 0；raw 空、`[1]`、截断、跨 seat 原字节替换、摘要计数改写、缺 Welcome、Welcome 帧冒充 projection、错 Welcome/session/role/seat/scope/binding/owner/version、错 audience/message type、未知 message type 和未知字段均 native 非零。最终 29 项均 native exit 2；旧 11 帧 schema-1 样本 native exit 3 并记录为历史格式不满足新契约。

工具自测使用 `WireCodec.Encode` 生成 4 个 player Welcome + 私有投影以及 1 个 spectator Welcome + public 投影，再从 JSON 重新读取并由 verifier 重新解码；另用 WireCodec 生成合法空 genesis push。四个 player 的生产序列化正例均包含 13 张真实 typed 手牌实体、非空 owner sleeve tile、ledger entry、cheat points、立即/离身袖花候选和 reservation；self-test 显式断言这些内容从 typed wire 解出且计数匹配。public spectator 正例显式断言没有这些私有 owner 值。该数据是工具夹具，不是游戏实测。

### R1-01 负例固定分母（29）

以下每个 JSON 都由工具用新鲜 GUID 输出创建，verifier 原生拒绝（exit 2）；summary 是保持原值或被篡改的旁路摘要，不作为拒绝的唯一原因。

```text
raw-empty
raw-invalid-one-byte
truncated-projection-wire
cross-seat-raw-summary-unchanged
hand-count-summary-rewritten
missing-welcome
welcome-record-is-not-projection
zero-version-without-valid-genesis
public-private-summary
unknown-field-summary-false
wrong-welcome-session
wrong-welcome-role
wrong-welcome-seat
wrong-welcome-envelope-session
wrong-welcome-envelope-binding
wrong-welcome-scope-id
wrong-welcome-scope-role
wrong-welcome-scope-room
wrong-welcome-version
wrong-welcome-owner-epoch
wrong-welcome-binding-epoch
wrong-projection-owner
wrong-projection-audience
wrong-projection-message-type
unknown-projection-message-type
wrong-projection-version
projection-unknown-field
welcome-unknown-field
public-nested-unknown-field
```

## 3. R1-04 已分配脚本子项

**原缺陷：** `tools/tests/C04PrivacyMatrix.Tests.ps1` 先同步读完 stdout 再读 stderr，且没有有限等待，子程序阻塞或单侧大输出时可能卡住。R1-04 的生产 runner 问题（候选/输入闭包、XML case 分母、marker 内容、产物包身份、锁与超时收尾）属于 B，不在本树改动。

**修复：** 每个受测 child 独立启动 stdout / stderr 的 `ReadToEndAsync`，带固定有限 timeout；记录 PID、启动时间、native exit、耗时、两流字符数、EOF drain 和 stop path。先发协作 cancel-file 并等 2.5 秒；拒绝取消或 timeout 时只对本 harness 创建的精确 child 进程有界处置，等进程退出和两流 EOF 后再写 stable stdout/stderr/native-exit 文件。未能确认进程停止或输出 EOF 时记 `UNKNOWN` 并失败。`RunRoot` 必须是新目录；成功和失败均写 summary / stable manifest。PowerShell 不再通过 stdout / stderr 顺序同步读取子进程。

**验收命令与结果：** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyMatrix.Tests.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -VerifierDll <GUID>/pinned-verifier/C04PrivacyMatrixVerifier.dll -RunRoot <fresh-GUID> -LegacyRawFramesPath <read-only schema-1 sample> -ProcessTimeoutMs 20000`，脚本 native 0。最终实际运行 38 个子进程；所有 38/38 stdout 和 stderr 都 drain 到 EOF；29 个 verifier 负例 native 2；合法序列与 self-test 共 3 次 verifier process native 0；native 37 / stderr 原样保留；双流各 750,000 字符；协作取消 native 23；拒绝协作取消和 timeout 的自有子进程均停止、输出收尾稳定（native `-1` 表示被处置终止，不伪装成正常退出）。

## 4. 其他已知缺陷和 owner 状态

下面各条来自必读完整复核，不因为未分配给 A 而从问题清单删除。

### R1-02 MAJOR — 冷恢复控制者、viewer 和 ticket 生命周期（B）

- **位置：** `dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs` ticket/viewer 生命周期；`LocalTableRuntime.cs` 恢复与 authority tick；`LocalSnapshotFacts.cs` 控制快照；`unity/.../AppComposition.cs` 和 `LocalTableChannelFactory.cs` owner 生命周期。
- **复现：** 原复核树只读探针源码 `build/caller-review/probes/Program.cs`、`CallerRoomProbes.csproj`，依赖既有 P41Harness 和真实 room/channel/TableSession；构建后执行 `D:/Unity/dotnet/dotnet.exe exec build/caller-review/probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll`。
- **实际 / 期望：** all-AI room 关闭后用新 AllFourHumanSeats 计划冷恢复报 `RECOVERY_CONTROL_INVALID ... changes controller without advancing controlEpoch`；真实 ReleaseControl 后虽恢复 automated=true、epoch=1，但只绑定 spectator 时 `CanTickAuthority=false`。期望恢复当前保存的 controller/epoch 优先于初始 SeatPlan，已 release AI 可推进。报告还列明 viewer shutdown 非 Completed/quarantined 时错误移除 viewer、默认单 viewer 未释放整桌 owner lease、失效 ticket 可在 room close 前无界累积/重放风险；期望保留隔离资源、默认 owner 关闭释放 lease、ticket retired 有界且不可重放。
- **修复与验收 owner：** B 批量修复当前控制快照/代次/phase/deadline 判断、viewer 关闭隔离、默认单 viewer owner 释放和 ticket retired；运行真实冷恢复及生命周期矩阵。A 未修改、未复跑、未接受。

### R1-03 MAJOR — 授权矩阵不完整（B）

- **位置 / 复现：** 对照分工卡和原交付的 N1–N8/U1–U3/P1–P2、C04-G5-01..03 固定分母；复核报告列出的 Windows/Unity 原件和原 PlayMode/Test Player XML。
- **实际 / 期望：** 原交付只覆盖四席/观战角色切换一个 Unity 和 Test Player 场景，不能代替固定场景分母。缺失包括真实非空袖牌/费用 ledger 对照、慢 consumer 饱和、真实 receive 旧 raw 延迟注入、Windows 恢复后原 scope/request 查询与零重复副作用、完整 room/controller/deadline 组合；P2 缺失，三条正式 G5 仍 BLOCKED。期望在授权内补齐真实数据/通道和固定每行原件；外部产品缺陷则精确标 BLOCKED，不用摘要或旧产物替代。
- **修复与验收 owner：** B 使用本次审过的 verifier，完成其 N/U/P 与 G5 范围；A 组件夹具通过不改变上述状态。

### R1-04 MAJOR — production runner 和报告口径（B）

- **位置：** `tools/unity/run-p45-c04-privacy-matrix.ps1`、生产 runner 的 XML/marker/来源锁定/timeout finally；复核报告 §R1-04。
- **实际 / 期望：** 复核脚本证明 XML 可接受 skip/error/count mismatch；marker 只按存在判断；超时后自有 writer 仍活动而共享 mutex 已释放；没有足够候选/输入闭包门禁。报告将 package bytes 写成 exe 大小：实际 Mahjong.exe 667,136 bytes、SHA-256 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`，总 package 128,346,555 bytes。期望 runner 固定源码/输入闭包、精确 XML case/分母且零失败/skip/error/not-run、校验 marker 和包身份，只有全部 writer 停止且 drain 后才释放锁并封存；文档分别报告 exe 与总包口径，不改写旧 marker/log。
- **修复与验收 owner：** B 处理生产 runner、其自有短负例和文档口径。A 的脚本测试子项不等于生产 runner 已修复或最终矩阵通过。

## 5. 运行阶段、身份与证据

### 候选身份与源码

- 工作树：`E:/majiang-review/g2-c04-d05-privacy-luna6-20261102-r1`。
- 分支：`wt/v5-g2-c04-d05-privacy-luna6-20261102-r1`。
- 起点：`90140b349974ee30ed2257a12486c9ed718197e6`。**实际初始状态与派发卡所述“clean”不一致：第一次 `git status` 已显示四个 A 授权文件为 tracked modified**：`.csproj`、verifier `Program.cs`、`C04PrivacyProbe.cs`、`C04PrivacyMatrix.Tests.ps1`；没有其他 tracked 修改。它们在本轮开始前已存在，来源无法从该基线提交历史判定；先检查了完整 diff，再保留并修正，最终由下面代码提交统一封存。未清理或覆盖其他树/旧证据。
- 代码提交：`7a65577b5be5d531c5f107fcc56461553604bda2`。四个代码路径单独提交；本报告作为下一独立 docs-only 提交。无其他 tracked 路径改动。
- 实际构建器：`D:/Unity/dotnet/dotnet.exe`，SDK 8.0.426。PowerShell：Windows PowerShell 5.1。
- 最终独占根：`build/ca141c707e01474d9ac0c6fb11d0c162/`；最终 verifier 完整输出副本位于 `pinned-verifier/`。工具夹具/日志/缓存和 probe compile 只留 ignored build，不入 Git。
- 源文件 SHA-256：`Program.cs` `E8BA4AF87B3E4884C0799A678E9F575436C1B0234216A4E96EB79A702BF857A4`；`.csproj` `FCAFC434158D60B61F89869D6EB5FDC7463AC9A7D3E8C844CCB6EDF3573DD3C1`；`C04PrivacyProbe.cs` `72D087D8350622EE3E34BCA57D3135F7BBB0966EDF4EE59837C0AEAC5202C213`；`C04PrivacyMatrix.Tests.ps1` `0A100512051766F49E9DE689E6821FFBDEF40078E7E950A21D52E0A62018DF0F`。
- 最终 pinned verifier DLL SHA-256 `DB786D725C548B30E56424A597E95A4D4FBC9B62162B9A31AF893DD11724F8CA`；`Mahjong.Contracts.Wire.dll` `417B21C85E9FEED4CFC54F10328DB8EC3CFD43AEC37F4076A25AAEA254F9D99D`；`Mahjong.Contracts.dll` `ADF05F81F80ADF33C32D195CF1EBDF2C810C29C22D4A66DAA7CE371E55AB5B6E`。

### 执行与结果

| 阶段 | 实际命令/输入 | 结果 |
|---|---|---|
| 恢复/编译 | `D:/Unity/dotnet/dotnet.exe restore dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj --disable-parallel`；随后 `build ... -c Release --no-restore` | 最终 build native 0，0 warnings / 0 errors；日志 `build/ca141c707e01474d9ac0c6fb11d0c162/verifier-build.log` |
| Probe 共享源码编译 | 独占 `probe-compile/ProbeCompile.csproj` 链接现有 `C04PrivacyProbe.cs` 和生产 `Contracts.Wire`，只用最小 `UnityEngine.JsonUtility` stub | native 0，0 warnings / 0 errors；只证明 C# / contract 可编译，不是 Unity 执行；`probe-build.log` |
| verifier self-test | pinned DLL `self-test <new fixture dir>` | native 0；`positive=2 negative=29 serializedFrames=5 source=WireCodec`；direct output 与 native exit 留在 GUID 根 |
| verifier/进程负例脚本 | `tools/tests/C04PrivacyMatrix.Tests.ps1`，见 `build/ca141c707e01474d9ac0c6fb11d0c162/powershell-final-c811a3073b3348f4bff281a028f1bd4a/` | native 0；29 项负例 native 2；38/38 child stdout/stderr drain；每个 child 有 PID/startUtc、native exit 和 stop path |
| Unity/Player/GAME | 未运行 | `NOT_RUN`；按分工由 B 集成后执行最终应用矩阵；无 GAME 证据 |

旧样本未改动，原始路径 `E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1/build/55d45940b9d448f9a6df1760b3718a26/privacy-evidence/raw-frames.json`，SHA-256 `C74C4BC040571FA84588BB369538ECE997800D95D809C5BA2E141160843B5C82`，schemaVersion 1、11 帧。新 verifier 输出 native 3：`VERIFY_ERROR InvalidDataException: unsupported raw capture schema; Welcome-bound schema 2 is required`。这是预期的格式拒绝；不把它标为真实隐私矩阵 PASS 或产品缺陷。

| 稳定证据 | SHA-256 |
|---|---|
| `verifier-build.log` | `F2B2E0550E41863BB4A0A3C4356C214182FE0C865150B57BAD4F7E6004B1F79E` |
| `probe-build.log` | `124466B7B920B14CD13232335E7469BE1A7A546656B1F86DBDF736D7249D9190` |
| `privacy-tests.log` | `111C8999808FEA12E92C612DCA199DD850E8627160DC3B898E1F38675159B23D` |
| final process `summary.json` | `2A48F4BFDB2E11CD88D488A4FA2BED59CEC3700CE5F4616A47D4636F1F43C66D` |
| final process `stable-manifest.json` | `DA85AF5C2B26917387D7BEC739CD49F4B3B86F906A5EEC199152284D981B461F` |
| full GUID evidence root `stable-manifest.json` | `A4A5B7235EF2BE22885F15D8DE328DC8B02CC224E81354A26A080FD38AD33C52` |

### 首次失败与恢复

首次 `build --no-restore` 因新 verifier 项目尚无 `obj/project.assets.json` 报 `NETSDK1004`；执行项目 restore 后消除。添加非空 owner fixture 后，首轮编译报 CS8604（nullable `frames`）；改为先绑定非空 `fixtureFrames` 后，最终 Release build 通过。首个 nullable 失败日志保留于 `build/4d77fa94a9e94324b47e70790c1ad1b5/verifier-build.log`，成功重试日志与后续完整最终源码证据未覆盖该首次失败。

## 6. 范围、未覆盖与验收交接

- 实际 tracked 代码变更只涉及四个授权源文件；`.meta` 无需变更；唯一新增 tracked 文档为本报告。没有写 B 工作树、矩阵 runner、其他应用源码或原件，也没有扩大程序集依赖到 Unity 以外的生产程序集。生产引用只新增已存在的 `Mahjong.Contracts.Wire` project reference。
- 原始 R 复核根与 50 项 manifest、原始 390aa95f 交付、旧 schema-1 capture 均只读。新 raw mutation/self-test JSON 位于本树 ignored 的新 GUID 根。
- 未运行 Unity/Player/Android/IL2CPP/设备；未取 Unity 锁；未验证真实应用当前 Welcome+projection capture；组件 PASS 不替代 B 的最终 GAME 矩阵。
- 验收清单：合法生产 WireCodec 序列 PASS；29/29 verifier 负例 native 非零；老样本诚实拒绝；并发 drain / nonzero / cooperative cancel / cancel refusal / timeout / stable output PASS；probe compile-only PASS；R1-02/R1-03 和 R1-04 production runner 仍由 B 执行。最终 D05/C04/G2 阶段结论保留给父代理独立验收。
- 代码与报告两段 commit；未 push、未创建/修改 PR、未 approve、未 merge、未 deploy、未改 scheduler 或旧 bridge/task history。

## 7. 最终 Git 记录

- 代码提交：`7a65577b5be5d531c5f107fcc56461553604bda2`。
- 报告 docs-only 提交：本报告单独提交；完整提交哈希由最终 Git 记录给出。
- 最终 `git status --short --branch`：报告提交完成后应为 clean。
- `git diff --check`：代码提交与报告提交均须通过。

## 8. R2 独立复核遗留与批量修复（2026-11-02）

本节记录独立复核 `build/reviewer-31068e32cd73cd7baa5e1537/REVIEW.md`（SHA-256 `DD7939159AFDAB6BD58182E81998FABC8533CE49818EB3339A39FAE4DB262E8C`）指出的两组 A 遗留。只读证据根及其中原件未改写；本轮所有新产物放在新的 ignored `build/` 根。下列修复没有改 B 的矩阵、恢复、生命周期或 production runner。

### A-R2-01 — R1-01 BLOCKER：生产 wire 语义、tile 内容归属和冗余字段

- **位置：** `dotnet/tools/C04PrivacyMatrixVerifier/Program.cs` 的 Welcome/header identity、`ReadProjection`、`CompareSummary` 与 typed snapshot 审核逻辑。
- **复现命令：** 本轮实际构建命令为 `D:/Unity/dotnet/dotnet.exe build RealProbe.csproj -c Release`（工作目录 `build/a-r2-run-42ce0180a0434ce887414e9ee99b6221/`）；真实通道命令为 `D:/Unity/dotnet/dotnet.exe bin/Release/net8.0/RealProbe.dll real real-channel-sync-0a52cb117fce435d87a605daef25b6f9`（工作目录同上）；验证命令为 `D:/Unity/dotnet/dotnet.exe dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll verify build/a-r2-run-42ce0180a0434ce887414e9ee99b6221/real-channel-sync-0a52cb117fce435d87a605daef25b6f9/capture/raw-frames.json`。真实 capture 变异器由 WireCodec 重新序列化：`D:/Unity/dotnet/dotnet.exe a-r2-run-42ce0180a0434ce887414e9ee99b6221/contract-cases/bin/Release/net8.0/ContractProbes.dll a-r2-run-42ce0180a0434ce887414e9ee99b6221/real-channel-sync-0a52cb117fce435d87a605daef25b6f9/capture/raw-frames.json real-wire-final-5782bde402d448cab1a9f907beeea3d5`（工作目录 `build/`）；逐个 `verify <case.json>`。所有路径均为本轮新的 ignored 产物，不覆盖复核原件。
- **实际与期望：** 复核发现真实生产 Welcome 的 typed `OwnerEpoch=1`，但本地入站 envelope header 为零；同步/更新同样不填此 header。旧实现把零当成冲突，真实通道因此被拒绝。旧实现还把每帧闭牌写死为 13–14，拒绝 Pon 后的 10 张闭牌；仅看 owner/数量，接受跨席复制的隐藏手牌及公共牌河泄出当前隐藏 tile；并接受 `SyncResponse` 的冗余 StateVersion 或 envelope Cursor 与 typed snapshot 不同。期望是零 header 表示“未携带”，非零冲突才拒绝；从 typed Welcome 取正 owner epoch 并核验同房间所有连接一致；按闭牌+摸牌与 owner meld 数检查手牌逻辑数量；对同 room/round/stateVersion 比较跨席隐藏 tile 和当前公开 tile；核对 SyncResponse 版本和 Cursor 的真实冗余字段，同时接受合法公开后续状态。
- **证据：** 独立复核原件见只读 `build/reviewer-31068e32cd73cd7baa5e1537/`。本轮真实生产 LocalTableHost/Room/ITableChannel/TableSession 通道原件为 `build/a-r2-run-42ce0180a0434ce887414e9ee99b6221/real-channel-sync-0a52cb117fce435d87a605daef25b6f9/capture/raw-frames.json`，10 帧 SHA-256 `28C4EB3344169E54FDBEFB50B69224777361D1BEC7FDCF1F639F4A1F4D1E8DF2`；通道连接 4 player + 1 spectator，五路原始 Welcome/投影均保留。夹具仅以 `JsonUtility` stub 编译并运行当前 probe，共享实际 WireCodec 与 LocalHost/Client 源码；这是 .NET 通道证据，不是 Unity/GAME。
- **修复映射：** (1) A-R2-01a：typed Welcome 的正 owner epoch 为准，header epoch 为零时接受、非零不一致时拒绝，并要求同房间 Welcome owner epoch 一致；(2) A-R2-01b：将闭牌、drawn tile、袖牌及 owner meld tile 的物理唯一性/范围和 hand-size/meld 关系分开校验，闭牌不再固定 13–14；(3) A-R2-01c：按同 room/round/stateVersion 汇总 typed 私有隐藏实体与公共 discard/meld/dora/last-discard；隐藏实体不得跨 seat 重复或出现在当前公共 board，合法公开后的推进版本可通过；(4) A-R2-01d：校验 SyncResponse typed state/event 版本、PlayerUpdate/PublicUpdate 自身 cursor 与 snapshot cursor、存在时 envelope cursor 与 snapshot cursor；并校验 MessageType 与 body oneof 相符，防止同步改写摘要掩盖错误。
- **验收断言与结果：** Release verifier build native 0、0 warnings/0 errors；WireCodec self-test `positive=4 negative=35 serializedFrames=5`；不改真实 capture native 0，marker 为 `PRIVACY_MATRIX_PASS frames=10 sha256=28c4eb3344169e54fdbefb50b69224777361d1bec7fdcf1f639f4a1f4d1e8df2`。从这份真实 capture 生成的 `legal-open-pon.json` native 0；`wrong-sync-state-version.json`、`wrong-envelope-cursor.json`、`foreign-closed-hand-content.json`、`public-current-hidden-tile-as-discard.json` 均正常 native 2 且有拒绝诊断。真实 capture case SHA-256 依次为 `BFC4E22850347173FAE246B45E7C9FAB14105E1BE14920403A067AC82E9F26EB`、`664438DA85695FE79E56A18C2244C12191BEEC6FCF73880E0C04E54C666E64B9`、`80B2B88BC12BFEC156C81A6D1BD64F9B1678F4165DB344CB04CF21B8C564B769`、`F5593CB6B7F19E0D7B408DC2F1E015845636E38F24E4E27256983B2F510AB264`、`EF96C602D6704D1E53C99A19309AE019F3F56E3129BF02BAD1539AF71A6D109F`。同轮固定 WireCodec 负例 `wrong-projection-message-type-summary-rewritten` 与 `wrong-welcome-owner-epoch-header-unset` 也均为正常 native 2，case SHA-256 分别为 `DC5D0A42670899215F41D03FE343C6B4A921274F8D075021250C0DFBBBD71744`、`9704D65672FB3CE8A190CB469F5BF75F48030C06DB2A5EB4BD52E590204DDF34`；六项逐项结果都在 `build/a-r2-script-legacy-31d05dd5111149fba190b64518126e1f/summary.json` 的进程记录中为 `EXITED/nativeExit=2`，stderr 非空。原 29 个负例全部保留，另增加 6 个 typed-content/冗余字段/owner/message-type 负例；self-test 不冒称 GAME。

### A-R2-02 — R1-04 verifier script MAJOR：正常拒绝判定与异常清理

- **位置：** `tools/tests/C04PrivacyMatrix.Tests.ps1` 的 `Invoke-Verifier`、`Invoke-BoundedProcess` catch/finally 和 harness 自身的进程故障用例。
- **复现命令：** 最终完整运行：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyMatrix.Tests.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -VerifierDll E:/majiang-review/g2-c04-d05-privacy-luna6-20261102-r1/dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll -RawFramesPath E:/majiang-review/g2-c04-d05-privacy-luna6-20261102-r1/build/a-r2-run-42ce0180a0434ce887414e9ee99b6221/real-channel-sync-0a52cb117fce435d87a605daef25b6f9/capture/raw-frames.json -LegacyRawFramesPath E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1/build/55d45940b9d448f9a6df1760b3718a26/privacy-evidence/raw-frames.json -RunRoot E:/majiang-review/g2-c04-d05-privacy-luna6-20261102-r1/build/a-r2-script-legacy-31d05dd5111149fba190b64518126e1f -ProcessTimeoutMs 20000`。复跑时 `RunRoot` 必须换成新的 GUID 路径。脚本内 timeout shim 对 `raw-empty.json` 睡眠而不判 raw；异常注入将 cancel 文件写到不存在的父目录，触发 child 启动后的写入异常。
- **实际与期望：** 旧脚本只看 child 非零退出，故超时杀掉的 child 也被算作正常拒绝；catch/finally 只 Dispose，可能让自有 writer 继续运行、双流未到 EOF，却写出看似稳定的日志和 native exit。期望负例必须 `State=EXITED`、verifier native 2 且有拒绝诊断（旧 schema 独立 native 3）；TIMED_OUT/CANCELLED/强制终止/UNKNOWN 永不作为拒绝成功。任何启动后异常均须对带 PID+startUtc 的自有 child 有界收尾、双流到 EOF 后再封存；若不能证明停止则记 UNKNOWN 且不写稳定产物，不编造 native exit。
- **证据：** 最终脚本摘要 `build/a-r2-script-legacy-31d05dd5111149fba190b64518126e1f/summary.json` SHA-256 `B3A8400291F67960EEAF1DE082D456E2790B005F3DE3E64D63238EA8AD66BC5C`，stable manifest SHA-256 `0165A7DBB619419A3A93451390B18BCED88D130520B7F80E84794783FD235CA1`。timeout shim 项记录 `TIMED_OUT/nativeExit=null`，脚本断言其不能通过负例；异常清理项记录 `FAILED/EXCEPTION_OWNED_PROCESS_CLEANUP/nativeExit=null/stdoutDrained=true/stderrDrained=true`，返回后 PID 已停止且 stdout 哈希稳定。旧 schema-1 原件只读，native 3，SHA-256 `C74C4BC040571FA84588BB369538ECE997800D95D809C5BA2E141160843B5C82`。
- **修复映射：** A-R2-02a：`Invoke-Verifier` 强制正常 EXITED、精确 expected native code 和拒绝诊断；成功例还必须含新鲜 `PRIVACY_MATRIX_PASS` marker。A-R2-02b：异常路径按已记录的 PID/startUtc 核实并收尾自有 child，统一 drain 双流；仅 EOF 后封存；强制停止或其它非自然退出明确记录 `nativeExit=null` 及 stop path；清理失败/EOF 不明则 UNKNOWN。测试保留已有 native 非零、大双流、协作取消、拒绝取消、超时边界，并新增 timeout shim 与 cancel-file 写异常注入。
- **验收断言与结果：** 最终 PowerShell 脚本 native 0，`result=PASS`、35 个 serialized negatives 均正常拒绝、真实 capture 10 帧 native 0；双流各 750,000 字符，native 37 原样保留，协作取消 exit 23，拒绝取消和 timeout 均记录自有进程停止，timeout shim 不能冒充 verifier 拒绝，exception cleanup 双流 EOF 且无 writer。旧 schema-1 专门按正常 native 3 拒绝。summary 明确区分正常退出、超时/取消和强制收尾。

### 本轮失败、复测和范围

首次 Release 编译发现 protobuf `PublicBoardView` 的非 optional `last_discard` 不生成 `HasLastDiscard`；改为 null 检查后恢复编译。首次正例运行发现合法 reveal fixture 没有建模摸牌且 state/event 双写未同步；补入 draw、SyncResponse redundant version 后正例通过。第一次完整脚本运行发现负例诊断白名单漏掉 same-state 内容诊断；补足诊断匹配后再跑。真实 capture 最初只含不同启动阶段投影；增加每条真实 channel 的同轮 full sync 后得到同 stateVersion 对照，并把 stateVersion 0 的真实初始化投影纳入比较，随后真实 foreign/public 泄漏变异均可靠拒绝。以上临时失败和修复均留在新 GUID ignored 根，不改复核原件。

本轮未运行 Unity、Windows Player 或最终 GAME 矩阵；B 的产品/矩阵范围仍由 B 负责，最终集成矩阵仍未执行。组件与工具证据不升级为 D05/C04/G2 完成结论。

### R2 Git 封存

- 代码提交（仅 verifier 与 verifier tests）：`0d81dfd7cf9524c4dcb78290044d55715e7d9b96`。
- 本报告作为单独 docs-only 提交；其提交哈希和最终 clean 状态由最终 Git 记录确认。
- 未 push、未建改 MR/PR、未 approve、未 merge、未 deploy，未触碰 scheduler、旧 bridge/task history 或 B 的工作树。

## 9. R3 独立复核遗留批量修复（2026-11-03）

本节只处理本轮 `A-R3-01`，保留已独立通过的 `A-R2-02` verifier-script 子项。R3 reviewer 原件在只读根 `build/reviewer-r2-a259194b6466823507b1/`；审查前工作树为 clean，HEAD `215def9a9dd39f553c0030cfca56bd30c103dd66`。所有新增生成物位于独占 ignored 根 `build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/`。

### A-R3-01 — R1-01 内容归属 BLOCKER

- **位置：** `dotnet/tools/C04PrivacyMatrixVerifier/Program.cs` 的 `Verify` visibility 分组、`ReadProjection`、`ExtractPublicTiles` 及 typed event 内容读取；`tools/tests/C04PrivacyMatrix.Tests.ps1` 的 self-test 固定分母和负例路径。
- **复现：** reviewer 原件 `build/reviewer-r2-a259194b6466823507b1/REVIEW.md` 给出原始复现及SHA。重新生成真实通道记录的命令为 `D:/Unity/dotnet/dotnet.exe build build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/ContractProbes.csproj -c Release`，随后 `D:/Unity/dotnet/dotnet.exe build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/bin/Release/net8.0/ContractProbes.dll real build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/fresh-real-channel-r3`。真实序列化变异命令为 `D:/Unity/dotnet/dotnet.exe build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/bin/Release/net8.0/ContractProbes.dll mutate-real build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/fresh-real-channel-r3/capture/raw-frames.json build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/real-mutations-final`；对输出逐项执行 `D:/Unity/dotnet/dotnet.exe dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll verify <case.json>`。这些探针源码位于本轮 ignored 根，只链接真实 WireCodec、LocalHost、Client 和提交的 probe；JsonUtility stub 不替代领域、权威、wire 或数据，也不标记为 GAME。
- **实际 vs 期望：** 旧代码按 `(room, roundId, stateVersion)` 分组并丢弃空 round，因此只把同一真实 raw 中 seat 的 `RoundId` 清空或改成伪造值，就让跨席对照被跳过；该变异仍能 native 0。旧代码还只数 `VisibleEvents`、只扫描 board，遗漏 `PlayerUpdate.VisibleEvents`、`PublicUpdate.VisibleEvents` 和 `PublicMatchView.SettlementEvents` 中真实 typed tile ID，隐藏牌公开事件可 native 0。期望是 round 必须作为经验证事实而非可伪造分组键；公开 typed event 内容须按事件 schema、序号、command/state version、当前 round 和当前可见性纳入审查；缺少 board/round/事件上下文须 fail closed，同时允许合法公开弃牌、合法 round 切换和旧历史事件。
- **证据路径与哈希：** 本轮真实四席+spectator raw `build/a-r3-run-ef9235c0d60f455d976cb282cafc3605/fresh-real-channel-r3/capture/raw-frames.json` SHA-256 `C4D5525146C145B89448652E26F149BDCC505C365100C1320D0F548069EF5E75`；采集 stdout `fresh-real-channel-r3.stdout.txt` 报 `Ready=5 captured=5`，verifier 输出 `PRIVACY_MATRIX_PASS frames=5`。真实轮次变异结果 `real-mutations-final/results.txt` SHA-256 `1FA5EA1288549D747C3AF3AD825F82CD6F155C585985F1CFBCF44876CF49F2E6`；六项 `foreign-empty-round`、`foreign-forged-round`、`foreign-same-round`、`public-hidden-empty-round`、`public-hidden-forged-round`、`public-hidden-same-round` 均正常 native 2。四个原复现中的空/伪造 round 绕过现已拒绝。新增 WireCodec self-test 包含公开结算、玩家更新与观战更新三条隐藏实体泄露路径负例。
- **修复映射：** (1) `Verify` 现在按 room + authoritative stateVersion 分组，并要求每组 projections 有且只有一个非空 round identity；不把独立 projection cursor 与 stateVersion 混比。(2) 私有投影必须带 round identity；公共 board 缺失时不能产生 PASS。(3) 解析 `PlayerUpdate` 的 `EventEnvelope` 和 `PublicUpdate`/settlement 的 `PublicEventEnvelope` typed body，核对 event type/oneof、schema、event_seq、command_version、visibility 与 unknown payload；只将当前 stateVersion、当前 settlement round 的公开实体同 owner 隐藏手牌比较，历史事件不永久拉黑 tile。(4) `C04PrivacyMatrix.Tests.ps1` 保留原 35 个负例并增加 6 个轮次（空、空白、伪造，跨席和公开对照）及 3 个 typed-event 隐藏实体负例。Self-test 断言 6 个正例、44 个负例；正例包含合法 Pon、公开 reveal 后推进、合法 round switch、旧版本 public event。
- **验收断言和结果：** `D:/Unity/dotnet/dotnet.exe build dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj -c Release` native 0、0 warnings/errors。`D:/Unity/dotnet/dotnet.exe dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll self-test <fresh-root>` native 0，marker `SELF_TEST_PASS positive=6 negative=44 serializedFrames=5 source=WireCodec`；stdout SHA-256 `8865A9D843D701DC2726E5D8B5555C2DB9E823847890570AD611631EE26395D6`。完整脚本命令为 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyMatrix.Tests.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -VerifierDll <current Release verifier> -RawFramesPath <fresh-real-channel-r3>/capture/raw-frames.json -RunRoot <new GUID root> -ProcessTimeoutMs 20000`，native 0；44 个序列化负例均 EXITED/native 2，真实 .NET raw 5 帧 native 0。摘要 `full-privacy-tests-final/summary.json` SHA-256 `A6D7506D50518BB2C84B7261361D0ED8AD578FC4B66CDB79720618264FC36B20`，stable manifest SHA-256 `E03EF86DA283D8432A0EAE34A3F7682BE7E2611CD87A6700CC86E1448CA3E46E`。整套 56 个 harness child 的 stdout/stderr 均到 EOF；44 个 verifier 负例 native 2；双流各 750,000 字符；正常协作 cancel observed exit 23、拒绝 cancel 和 timeout 被标记并有界收尾，exception cleanup 在 EOF 后封存。外部 raw-empty timeout shim 复跑 native 1 且 summary `FAIL`，`negative-raw-empty` 状态为 `TIMED_OUT`，证明被 harness 终止不能冒充 verifier native 2；summary SHA-256 `B00C461848D867DDE0ADD24CBEDC71C46F6965742BB5872FB1AA5155B8015CC2`。
- **首次失败与处理：** 首次 Release build 报两个 protobuf event oneof 的本地函数重载冲突，分开命名 public/player event 解码函数后通过。首次合法历史事件正例暴露 optional bytes 的默认空 `UnknownPayload` 不能用 null 判空，改用生成属性 `HasUnknownPayload` 后正例通过；新增 board 必需断言后修正 nullable flow 并重新执行完整脚本。最终 Release build 和 self-test 无告警错误。
- **范围和 owner：** 未修改或接管 B 的 matrix/recovery/production runner；没有运行 Unity、Player、Android、IL2CPP，也没有将组件或 .NET capture 宣称为 GAME。最终集成 Unity/Player 矩阵仍由 B 执行。A-R2-02 的已通过进程边界逻辑保留，仅更新分母、诊断白名单和固定负例名以覆盖 R3。

### R3 Git 封存

- 代码提交（仅 `Program.cs` 与 verifier tests）：`9ed09e4fc0fca9fb3b8d81cb3bb909f60a968a91`。
- 本节为独立 docs-only 更新提交；提交哈希由后续 Git 记录给出。代码与报告分开提交；无 push、MR/PR、approve、merge 或 deploy。
- `git diff --check` 通过；报告提交完成后工作树应 clean。

## 10. R4 独立复核遗留批量修复（2026-11-03）

本节仅修复 R4 独立复核提出的 `A-R4-01`，保留 R3 已通过的 round identity 内容审核与 R2-02 进程边界结果。本轮 reviewer 原件只读根为 `build/reviewer-r3-c224c3364bd9b2f40d5e/`；`REVIEW.md` SHA-256 `FB835A3C96A5AE071DCECC17ED2E4FD74638C99ABCA07F990DB32ABCD4092C54`，manifest SHA-256 `CB011BDDADA0FBE208C34FD4D5777104BAEF1BF1DD25FF4A5561BD43A4B05568`。代码基线 HEAD 为 `49d8e773c8df5af724bec69fb9400d54f537f0d7`。本轮所有执行和输出在独占 ignored 根 `build/a-r4-run-f13e13071f4f4f6aadd9ea2406c95277/`，没有改写旧证据。

### A-R4-01 — R1-01 typed event 历史可见性与结算揭示 BLOCKER

- **位置：** `dotnet/tools/C04PrivacyMatrixVerifier/Program.cs` 的 `ValidatePublicEventVisibility`（历史 commandVersion 关联、terminal win 授权及隐藏碰撞处理）、`ReadProjection` / `ReadEventTileContent` / `ReadPublicEventTileContent`（三种 typed event 路径）；self-test fixture 在 `BuildTsumoFixture`、`BuildHistoricalLeakFixture`。`tools/tests/C04PrivacyMatrix.Tests.ps1` 固定完整负例与诊断断言。
- **复现命令：** 使用 R3 审查器生成器并从最终 verifier 检查每个新鲜 WireCodec 输入：`D:/Unity/dotnet/dotnet.exe build build/a-r4-run-f13e13071f4f4f6aadd9ea2406c95277/event-probes/EventProbes.csproj -c Release`；`D:/Unity/dotnet/dotnet.exe build/a-r4-run-f13e13071f4f4f6aadd9ea2406c95277/event-probes/bin/Release/net8.0/EventProbes.dll build/reviewer-r3-c224c3364bd9b2f40d5e/script-tests/wirecodec-fixtures/valid.json <fresh-GUID-root>`；对目录中每个 JSON 执行 `D:/Unity/dotnet/dotnet.exe dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll verify <case.json>`。探针保留 WireCodec typed body，未伪造 status 或 summary；它们是工具合同输入，不是 GAME。
- **实际 vs 期望：** 旧实现只把 `CommandVersion == snapshot.StateVersion` 的 event 内容加入可见实体集合，因此 v1 仍被 owner 隐藏持有的 tile，可在 v2 的 `PlayerUpdate.VisibleEvents`、`PublicUpdate.VisibleEvents` 或当前 round `SettlementEvents` 以 commandVersion 1 公开；有/无完整 v1 四席历史的六种组合都曾 native 0。相反，真实结算语义允许 Tsumo winner 的 `DrawnTileId` 仍在 owner 私有投影中，同时以 `SettlementPublic` 事件公开；旧无条件 hidden/public 碰撞将玩家更新、观战更新和 settlement 三种合法输入都拒绝。期望是按事件原 commandVersion/round/事件类型匹配完整历史 owner 与公开事实，不足以证明时 fail closed；对合法 terminal Tsumo 只授权其实际 winner 的实际 drawn/win tile，其他隐藏实体仍拒绝。
- **统一修复要求：** 将每个公开 typed event 作为原命令版本的可见性事实，关联 room、round、stateVersion、event sequence 及完整四席 owner 快照，不再因事件版本早于当前快照而跳过内容审计。若历史对照缺失或歧义则拒绝 PASS。建模结算公开授权时，必须同时验证 `SettlementPublic`、Tsumo/Ron 类型、事件 actor/winner、terminal owner snapshot 与 win tile；不得全局忽略 SettlementPublic、全量移除公开 tile 碰撞或永久拉黑 tile。保留现有合法历史 reveal、合法 round switch/后续 tile reuse 与原 44 负例。
- **统一负例/正例矩阵与断言：** 三条 event 路径各有“保留原 v1 owner history”和“无 v1 历史”两种隐藏 TileDiscarded(t0) 负例，六项均须正常 native 2；结算授权负例覆盖错 winner、错 win tile、非 terminal phase、commandVersion 不匹配，均须拒绝；授权 Tsumo 的 player update、public update、settlement 三项必须 native 0。最终独立复跑的六个历史泄露输入均 native 2，三项授权结算输入均 native 0。self-test 为 9 正例、54 负例；原 44 负例全部保留，新增 6 个历史路径负例与 4 个错误结算负例。
- **复核证据路径与 SHA：** `build/a-r4-run-f13e13071f4f4f6aadd9ea2406c95277/reviewer-r3-cases-22615c193bae42f38ddf1af9b8a9560f/results.json` SHA-256 `47B4AAD167B1469F4A6246B01A6333B2C9F44192D0BA0E73C6E1E1AE304442F4`；六个历史负例分别位于该目录，native 2；三个正例 SHA 分别为 player update `D2AC4F7A8B2FA7876AEB0EFE548C3C7AD9DBE7B4BD5A635CD77D77F214EC6A62`、public update `A4FEDFDD43ACD4E6A142860B69D3B753AF48382B075C0C90AFEAABB1210D8F4C`、settlement `8F3BEBB56716E775A298818CCDCC17D754CA93E8E72CAEF73C88C55EBF1A3EF8`；六个负例 SHA 与逐例退出码也在 `results.json`。最终真实 .NET 四席+spectator capture `build/a-r4-run-f13e13071f4f4f6aadd9ea2406c95277/fresh-real-channel/capture/raw-frames.json` SHA-256 `7AD5DD37A5B14ECFAA849312DD66BBF4DE3E85ED48B1C18526889112B99EA453`，真实 channel 输出 `REAL_CHANNEL_PROBE Ready=5 captured=5`，verifier native 0、`PRIVACY_MATRIX_PASS frames=5`。该采集 source-link 实际 `C04PrivacyProbe` 和生产 .NET LocalTableHost/Room/Channel/Session；JsonUtility stub 只代替 Unity 序列化器，不代表 Unity/GAME。
- **验收结果：** `D:/Unity/dotnet/dotnet.exe build dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj -c Release` native 0、0 warnings/errors。self-test 命令 `D:/Unity/dotnet/dotnet.exe dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll self-test <fresh-root>` native 0，marker `SELF_TEST_PASS positive=9 negative=54 serializedFrames=5 source=WireCodec`，stdout SHA-256 `C8F92A307E55F9A4F8B32B7EB917C128C81A1696911D3D506AA8CC7DF11A2E68`。完整脚本命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyMatrix.Tests.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -VerifierDll <current Release verifier> -RawFramesPath build/a-r4-run-f13e13071f4f4f6aadd9ea2406c95277/fresh-real-channel/capture/raw-frames.json -LegacyRawFramesPath E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1/build/55d45940b9d448f9a6df1760b3718a26/privacy-evidence/raw-frames.json -RunRoot <fresh-GUID-root> -ProcessTimeoutMs 20000`；native 0，summary `PASS`，54 个 serialized negative，真实 5 帧 native 0，另含旧 schema 正常 native 3；67 个 child 双流全部 EOF。双流各 750,000 字符、正常 native nonzero 37、协作取消 exit 23、拒绝取消与 timeout 有界收尾、异常 cleanup 双流 EOF 且没有 writer，timeout shim 被标记 `TIMED_OUT` 而未冒充拒绝。summary SHA-256 `53E3C6F0A19669A919278528DB1ECE6D57CB8DD827F4A506DF572F3D6F191C15`，stable manifest SHA-256 `F70DC4A1316D3E144B7AF2254A04B880FCBE930BE5052F6654134B1E8674B1B2`。
- **首次失败与处理：** reviewer 原件探针第一次输出目录被已存在目录占用，未运行验证；改用新 GUID 根后成功。首次本轮真实通道探针构建因探针源码布局遗漏链接的 helper 文件失败，调整为独立 `real-probe` 源码夹具后 Release build native 0、0 warnings/errors。完整最终脚本和最终九项 WireCodec 复跑均通过；以上仅为本轮 harness/fixture 的首次尝试，不改变旧证据。
- **范围：** 本轮没有修改 B 路径、领域规则或 136 张物理实现；没有运行 Unity、Player、Android 或 IL2CPP，也没有把工具 fixture/.NET capture 宣称为 GAME。最终集成矩阵仍归 B。

### R4 Git 封存

- 代码提交仅包含 `Program.cs` 与 `tools/tests/C04PrivacyMatrix.Tests.ps1`；报告以独立 docs-only 提交封存，提交哈希由 Git 记录给出。
- 未 push、未建改 MR/PR、未 approve、未 merge 或 deploy；`git diff --check` 通过，最终工作树 clean。
