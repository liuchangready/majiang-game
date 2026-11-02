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
