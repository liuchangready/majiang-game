# V5 G2-C04 恢复后原请求对账与零重复副作用 — R1 修复交付

- 复核原件：`E:/majiang-review/caller-c04-d01-r1-review-94e237c1/build/caller-review/REVIEW.txt`，SHA256 `D24D54D0F33279D0DF45C0599634F76D6924045E4D55B1B7CE8C09AD1AB33B8E`；evidence CSV `A2B99814C23F651D56591D515B342D2CF088EA0E5B1C3A8620B873B914AA141D`。
- 上一轮被复核代码：`993d172a2ee953a74134a95bb219a0560e22a18b`。
- 本轮冻结代码/Player 源码 commit：`317f84984db108e7b5c42277eccdaa749482013e`（父链从 `cde5da1649d42151abaefc2edc5f558a287d703f` 起）。
- 本报告提交为报告-only child commit；最终 HEAD 与报告文件 SHA256 见交付消息。
- 范围：8 个原有路径（含独立 verifier）+ 经授权的 2 个只读 .NET helper 文件 + 本 tracked 报告 = **11 个 tracked 路径**；未改冻结 production/client/runtime/collector/旧 decoder，未 fork 协议。
- 状态：D01-R1-01..04 四组全部修复并验证；C04-G6 八行在真实 Windows Player 中 **8/8 PASS**。正式 C04/G2–G5 仍未批准；C04-G7-02 仍 BLOCKED；旧 Compare BLOCKED17/UNTRUSTED_LOCAL 与 15 项旧 UI 失败保留。未 push/建改 PR/approve/MR/merge/deploy/scheduler/complete.flag。

## 1. R1 缺陷逐条映射

### D01-R1-01 BLOCKER — 宽泛清理破坏非本次所有改动

修复：

- `Build-Player` 不再对 source tree 执行任何 `git restore`/`git clean`。
- 每次构建把 `Assets/Packages/ProjectSettings/slnx` 复制到本树 ignored `build/<GUID>/staging-project`，并只在 staging 内复用 Unity `Library`、运行 build-shared（`-PluginOutputDir` 指向 staging）与 Player build。
- 源树只读；构建开始前只做 `git status --untracked-files=no` 的 tracked-clean preflight。预检后到达的 source 文件/并发修改不会被本工具写入或删除。
- staging 内仅删除两个 Editor-only provenance 文件（源树 guard 会错误校验仓库默认 plugin 目录）：`SharedPluginProvenanceGuard.cs` 与 `SharedPluginProvenancePlayProbe.cs`；删除前记录 SHA 到 `staging-adaptation.json`，同一 `check-shared-plugins.ps1` 已先对 staging plugin 目录实际执行并通过。Player 不包含 Editor 程序集。
- 无 `Remove-Item` 作用于 source tree；仅 fault fixture 的 case `local/*.save` 与 staging 文件。

证据：`build/c04-recovery-request/r1-final-authoritative/staging-adaptation.json`；`source-snapshot.json`（`treeSha256=04AF08800BF60D7EE98AE811B07AF0183FABAEB55149D4DF99E5F99E1B09F663`）；source tree 构建前后 `git status` clean。旧失败构建全部保留在 `r1-final-1..5`、`r1-cases-3..6`。

### D01-R1-02 MAJOR — verifier 只信 summary/帧 metadata

修复：

- 新增只读 helper `dotnet/tools/C04RecoveryReconciliationVerifier/Program.cs`，引用现有 `Mahjong.Contracts.Wire`、`Mahjong.LocalHost`、`Mahjong.Client`。
- 逐帧用 `WireEnvelope.Parser` 解码；核对 direction/body/type/session/room/role/account/scope/request/correlation 与 metadata 完全一致。
- recovered 行要求一个真实 OUT `ReceiptQuery`（原 request/scope/correlation）和一个匹配 IN `ReceiptLookup` Found/Applied，receipt 的 canonical digest/evaluated/command/event 与原件一致；拒绝原 command resend/自然 receipt 替代。
- 从 probe 导出的 raw `authority/*.save` + `*.bin` 用 `LocalTableRecoveryFactsReader` 独立重算 state/event/ledger/physical/receipt；比较 prepare-pre/prepare-post/recover-baseline/after/repeat；重复查询零增量；outbox 用 `FileOutboxStore` 重读 1/1/0 与原 request/digest。
- FRESH_START 要求旧 outbox 保留 1、无旧 scope/request frame、无 Command/Query、stateVersion 0 且无旧 receipt；REJECTED 要求 0 frame、无 binding/Ready、raw primary/backup SHA 与 input 完全不变。
- 固定 8 行 case/fault/outcome 映射；错误/空/重复/错 case 拒绝。`prepare.api` 改为真实 `SUBMIT_ACTION`。
- Probe 的 IN/OUT frame 全部落盘；`.NET UnknownOnce` 仅为 .NET recording-channel 的受控传输注入，报告中明确不是 Player/真实服务自然 Unknown；Player 只覆盖五个 Found + 三个安全边界。
- 工具 fixture 全部改为从真实案卷复制的 TOOL_FIXTURE；10/10：2 正例 + 8 负例（garbage reframed、missing inbound lookup、foreign identity、invented ledger、rejected ready、missing raw authority、wrong digest、wrong case policy）。

证据：`build/c04-recovery-request/r1-final-authoritative/helper/C04RecoveryReconciliationVerifier.dll` SHA256 `CC70263C8F44F7147C7D3A1E6A2EEB7615AECFA12BAD6734B2A9E81B738AB812`；工具 summary `build/c04-recovery-verifier-tests/8cabf19ea1044d54b589b8ef25676e89/summary.json` SHA256 `BE591094009B6AB52751F970FE5128A9A9208CF7610D60288164F9B623AD38BD`，10/10。

### D01-R1-03 MAJOR — build/source/process 绑定、原生 gate、封存清单不完整

修复：

- 先冻结源码 commit（本轮 Player 源码 commit `317f8498`），再构建；`source-snapshot.json` 记录 HEAD、`git ls-tree` tree hash、C04 输入文件 SHA；build marker 强制 `sourceHead/buildCommit` 等于冻结 commit。
- staging 构建；Unity 由真实 .NET `Process` 启动并等待，记录 PID/start/path/native exit；`nativeExit != 0` 立即失败。
- Build marker 必须 `result=Succeeded` 且 `errors=0`；marker output/playerSha/plugin hashes 必须与实测 exe/staging plugins 一致。
- 现有 Player 只能通过 `-BuildRecordPath` + `-PackageManifestPath` 绑定原 build record/package，不再用当前 HEAD 重贴标签。
- `Write-Summary` 在写 CSV 前完成最后一次 tool.log 写入，并只枚举 `player/helper/logs/prepare*/cases` 与顶层文件，排除 staging；manifest 生成后不再写任何封存文件。实测 `logs/tool.log` CSV SHA 与实际一致；`Mode Verify` 只写新 `-OutputDir`，源 evidence CSV SHA 前后不变（`27E4F221...`）。
- 旧 runs（`r1-final-1..5`、`r1-cases-1..6`）保留为 invalid/incomplete，不回填、不重标。

证据：`source-snapshot.json` SHA256 `4C62B6FEF8C1F747255C752242D0EEE13D4E5119D6BECD62E310592B4880444D`；`build-marker.json` SHA256 `F27A29251FED7713F1118E4E7FB9C7870CC7B9EDDF3B65A41BCE2C16F094F2A3`（Succeeded/errors0/warnings193）；`logs/unity-exit.json` native 0；Player SHA `F083B0E8...`；package 205 files manifest `3D5AE0C1...`；summary `02D73013...`；evidence CSV `27E4F221...`。

### D01-R1-04 MAJOR — tracked 报告缺失、路径不符

修复：

- 工具测试已改名/移动为 `tools/tests/VerifyP45C04RecoveryReconciliation.Tests.ps1`。
- 本文件 `doc/v5-重构/V5-G2-C04-RECOVERY-REQUEST-DELIVERY-20261101.md` 为 tracked 报告。
- 保留必要独立 `tools/unity/verify-p45-c04-recovery-reconciliation.ps1` 及授权 helper 两文件。
- 最终 tracked diff 为 11 路径（8 原有 + 2 helper + 本报告），无其它 scope；报告记录真实命令/计数/native/首失败/大小写/限制。

## 2. 最终 Player 执行（本轮新运行）

构建/执行根：`build/c04-recovery-request/r1-final-authoritative/`。
Player build：source `317f8498`；Unity native 0；marker Succeeded/errors0；Player `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；package 205 files / manifest `3D5AE0C1DDCD61A11FA5A9F9262A8BA59B6BC90A11E216BB28E6143BC8934D6F`。

| 行 | 结果 | stateVersion | eventSeq | hand | river | 原 scope query | 原 request Command | result SHA / verification SHA |
|---|---|---|---:|---:|---:|---:|---:|---|
| G6-01 | RECOVERED | 1 | 2 | 13 | 1 | 1 | 0 | `D0C7C396CF326E301C6D78192939223DA13D80C39388514312E28198B8B818BF` / `9534727F611072D9C03135241A65ED80534438C6E7F2C985D8A07AF23CB2B71D` |
| G6-02 | RECOVERED（Force） | 1 | 2 | 13 | 1 | 1 | 0 | `B8049859862984BDBF76C8562A0EB2A3456F445F8CF17547A0ADAAB852945ADA` / `131FAE9C120027D022D0A7433CD5A9C932EFBB2E396FB672D25BA84E043ECA2C` |
| G6-03 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `4760702A76BFF1DC66422B33908E3885778417967CF0CEC434E7F3206799BDAF` / `9420484566E9529D36F5E365BB9E5F0624CCF7E1D2FECFD3B69E3F6F3B967614` |
| G6-04 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `4D91F89E48B7C4FF60CCE51D7B7652ED5CFB874DAD831D03998BF856F98BDD47` / `17765A1AF439AD025FB8F003417497809C0EBAF9F3A73B4F153F8E60484D558C` |
| G6-05 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `987C0D3ED77EBAA654F2EE1504A25BF59659CF96D5BEFC36DEE0D7CFAEA5577C` / `C682655C30E9B241296B04BB57FFBEBBB475F428BAF2632CFB4CEBC0CFCA5DB1` |
| G6-06 | FRESH_START | 0 | 0 | n/a | n/a | 0 | 0 | `E1B49599446D8D799AEB9CF633EF5D744E27F7DBF0DA41E5CD6FB8FC0DCEC16D` / `9517E8D95EDB3DF78A4C027BAB273BBA8A4349108549D8F0C00D69193B2E1FDE` |
| G6-07 | REJECTED | n/a | n/a | n/a | n/a | 0 | 0 | `3EE796B85C5733426C219392AFEB40A2D086F570864A09F67CCCEA2F6E25669A` / `85693B23FC8C1C7FD43B9830F2F8DC3EE89E9E920AEB79CB9B6B528BB0277FC6` |
| G6-08 | REJECTED | n/a | n/a | n/a | n/a | 0 | 0 | `748BCCF3655BBF425117C34F4B87D42F4953040B4820BE51ADA4870E8C70BD42` / `852BE023FEA186974CFFF7D6BFE7C6D3DEACD95759669E90C496A1068F53CE6E` |

所有 Recover native 0；普通 Prepare 0；Force Prepare native −1（自主 `Process.Kill`，`prepare-force/player.log` 有 `FORCE_KILLED`）。每个 case 均有 raw `authority/*.save/*.bin/*.dat`、`frames/*.bin`、`launch.json`、`player.log`、`result.json`、`verification.json`。

## 3. .NET / 工具 / 完整性

- LocalHost：**143/143 PASS，0 failed/skipped，native 0**（含 9 个 C04 新用例）。
- 工具负例：**10/10 PASS**，summary SHA `BE591094009B6AB52751F970FE5128A9A9208CF7610D60288164F9B623AD38BD`。
- 全量 `.NET verify-v5` 未在本轮重跑；上一轮实际 2,830 total / 2,756 pass / 62 fail / 12 skip，62 fail 全为 PG env BLOCKED（`MAHJONG_P54B_TEST_PG_ADMIN` / `MAHJONG_P52_TEST_POSTGRES_ADMIN`），12 为 LobbyService 既有 skip；不把其写成 D01 回归或全绿。
- 封存：`summary.json` SHA `02D7301322BD1413EFFCD06321DAAEBD4E3439C2F6737D1F96AB07C982C20165`；evidence CSV `27E4F221CB2EE1D9A844725F1A60E0502145DE2E9743E8EF0E7E9F3379B86F97`（548 行，tool.log CSV=actual）；`source-snapshot` `4C62B6FE...`；`Mode Verify` 在 `r1-verify-2` native 0，源 CSV SHA 前后完全一致。

## 4. 首失败/旧案卷保留

- R1 首轮构建失败：`r1-final-1`（staging package cache 不完整、native1）、`r1-final-2`（Library 复制路径过长）、`r1-final-3`（source-tree provenance guard 误校验默认 plugin）、`r1-final-4`（移除 guard 后 play probe 编译失败）、`r1-final-5`（case 未复制 prepare/authority，helper 失败）。
- R1 verifier 首失败：`r1-cases-3/4/5`（rejected 缺 binding/null、helper 旧路径）。全部保留，未删除、未用旧产物替代。
- 旧 C03/C01/C02/C05 证据、旧 Compare、15 项 UI 失败均未重跑/改写。

## 5. 限制与状态

- PG 依赖全量测试仍为环境 BLOCKED；本轮未获 PG 资源，不要求重跑。
- 正式 C04 33 行、C05、G2–G5、Android/iOS 设备仍未通过；本报告只关闭 D01-R1 四组修复。
- Player 8/8 是 C04-D01 Player 场景证据，不等于 G2。
- 未 push/建改 PR/approve/MR/merge/deploy/scheduler/complete.flag；等待 caller 独立复核与唯一 Draft PR20 处理。
