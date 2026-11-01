# V5 G2-C04 恢复后原请求对账 — R3 修复交付

- R3 复核原件：`E:/majiang-review/caller-c04-d01-r3-review-1e98a642/build/caller-review/REVIEW.txt`，SHA256 `3B2247936FA70921E61C71FC84B3C7C37F3E79973066FDD7453F01586224EB20`。
- `D01-R1-01`（隔离 staging）与 `D01-R1-04`（tracked 报告/测试路径）保持关闭，不重开。
- 本轮修复 code commit：`c8571d37b75f7889514a4f68e16bcef7e7cb4231`；Player 源码冻结 commit 仍 `317f84984db108e7b5c42277eccdaa749482013e`；Player 字节未变。本报告为报告-only child commit，最终 HEAD/SHA 见交付消息。
- 范围仍为 11 tracked 路径。未改 production/client/runtime/collector/旧 decoder/协议；未 push/PR/approve/MR/merge/deploy/scheduler。
- 结论：`D01-R1-02`、`D01-R1-03` 的所有 R3 子项修复；R3 已放行变体全部非零 FAIL；正例 8/8 PASS，原件/CSV 未改写。正式 C04/G2–G5 未批准，C04-G7-02 仍 BLOCKED。

## 1. D01-R1-02 修复（固定分母、完整清单、故障类型）

### 1.1 外部 pin 传递到实际文件
- `Mode Verify` 现在强制要求：
  - `-ExpectedSummarySha256`；
  - `-ExpectedEvidenceSha256`（完整 evidence CSV）；
  - 可信 `-HelperPath`。
- summary 8 行必须：
  - 精确 8 个固定 ID、8 行、8 唯一；
  - `caseSetComplete=true`、`rowCount=8`、`status=PASS`；
  - 每个 `caseResult.resultSha256` 与实际 `result.json` 匹配；
  - `verificationSha256` 与实际源 `verification.json` 匹配；
  - 实际 `result.caseId/fault/expectedOutcome/actualOutcome` 与 summary 行一致。
- evidence CSV：
  - 逐项路径/bytes/SHA 校验；
  - 拒绝重复/空行；
  - 实际文件集合与 CSV 双向完全一致；
  - CSV 自身必须等于外部 `ExpectedEvidenceSha256`。
- 逻辑 case 目录装错实际案卷（如 case01 放 case03）现在因实际 caseId/result SHA/CSV 不符而 FAIL；空/缺/重复/未知 ID/额外 row 均拒绝；subset 为 `PARTIAL`。

### 1.2 故障类型按冻结注入机制区分
`ValidateFaultBoundary` 不再把 `Truncate`/`BadChecksum`/`Incompatible` 都当作“任意 invalid primary”：

- `TruncatePrimary`：after 长度 = max(1, before.Length/2)，且是 before 的精确前缀；backup 字节不变。
- `BadChecksumPrimary`：after 长度/header 不变，仅 byte44 = before[44] XOR 0x5A；backup 不变。
- `IncompatiblePrimary`：after 长度不变，仅 bytes4..7 变为 2/0/0/0；backup 不变。
- `None`：primary/backup 字节均不变。
- `MissingPrimaryWithBackup`：primary 不存在、backup 不变且有效。
- `BothMissing`：两者不存在。
- `BothBad`：primary 为 before 的 1/3 前缀；backup 仅 byte44 XOR 0xA5，长度不变。
- Force：prepare launch native 非 0、未 timeout、日志含 `FORCE_KILLED`；非 Force prepare native 0。

`truncate-as-checksum`、`checksum-as-truncate`、`incompatible-as-checksum`、`checksum-as-incompatible` 全部 FAIL。

### 1.3 保留的 raw 事实
- 继续从原始 `SyncResponse`/`PlayerUpdate` 解码 hand/river/tile/`TileDiscarded`。
- 继续用 `LocalTableRecoveryFactsReader` 校验 pre/post/repeat 的 state/event/ledger/physical/receipt。
- outbox 仍用 `FileOutboxStore` 重读 1/1/0；136/136 与 ledger 零增量保留。

## 2. D01-R1-03 修复（Recover native、source 树/输入、package）

### 2.1 Recover native 字段
- wrapper 对 Recover `launch.json` 强制检查属性**存在**、非 null、类型正确：
  - `nativeExitCode` 必须为数值；
  - `timedOut` 必须为 bool；
  - `nativeExitCode=0`、`timedOut=false`；
  - PID/start/path/SHA、case/mode/fault/outcome 与 result 一致。
- `missing-recover-native`（删除两属性）现在 FAIL。
- Prepare 侧由 helper 的 `ValidateFaultBoundary` 检查 native/timedOut 存在、Force 非 0 特例、普通 0；`missing-prepare-native` FAIL。

### 2.2 外部 source/build/package pin
`Bind-ExistingPlayer` 现在要求四个外部 pin，且不再信任 descriptor 自述：

- `ExpectedSourceHead`
- `ExpectedBuildRecordSha256`
- `ExpectedPackageManifestSha256`
- `ExpectedSourceSnapshotSha256`

并执行：
- marker/snapshot/sourceHead 与 pin 逐一相等；
- 重新计算 git tree（`git ls-tree -r --full-tree`）与 snapshot.treeSha256；
- 对 snapshot 中每个输入路径用 `git cat-file blob` 计算真实 SHA256，必须等于 snapshot input；
- `git cat-file -e` 仅作为附加检查，不单独作为来源证明；
- package `files.Count == fileCount`、路径唯一、逐文件 bytes/SHA、总 bytes、实际文件集合双向匹配。

`stale-source-993-coherent-descriptor`（marker/snapshot 一致改 993，仍用 317 pin）FAIL；cde5 无 probe FAIL；empty package FAIL；正确 317/205 包 PASS。

## 3. R3 负例矩阵（实际执行）

| 变体 | 实际结果 |
|---|---|
| duplicate-actual-case（case01 实为 case03，summary pin 不变） | 非零 FAIL |
| truncate-as-checksum | exit 1 FAIL |
| checksum-as-truncate | exit 1 FAIL |
| incompatible-as-checksum | exit 1 FAIL |
| checksum-as-incompatible | exit 1 FAIL |
| missing-recover-native | exit 1 FAIL |
| missing-prepare-native | exit 1 FAIL |
| stale source 993 + coherent descriptor | FAIL（external pin/tree/input） |
| empty package fileCount205/files=[] | FAIL（count mismatch） |

证据：`build/c04-recovery-request/r3-negatives/results.json`；旧 R2 负例保留。

## 4. 最终 Player 正例（复用已核验 317f 包，未重建 Unity）

执行根：`build/c04-recovery-request/r3-cases-2/`。
summary SHA `9E8EB182010E6C881BC387FB0DB149AD048B7DBE7DA68D461E66E25DBC1D8212`；evidence CSV `801A13E3651CD5476873F8F60C375D2008367F83BE1C9B0D1F15358BF1BD0658`（399 项）。
Player `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；package 205 / `3D5AE0C1DDCD61A11FA5A9F9262A8BA59B6BC90A11E216BB28E6143BC8934D6F`；helper `9288AD30C935A5353D931B7BB0E206F058E8C83EF9AA191E5BF4CC28E6F37A5F`。

| 行 | 结果 | stateVersion | eventSeq | hand | river | query | command | result / verification SHA |
|---|---|---|---:|---:|---:|---:|---:|---|
| G6-01 | RECOVERED | 1 | 2 | 13 | 1 | 1 | 0 | `85EF6594C07953A232DA15101F69279873DC4A40E8D0491527AC7105AA7B33BC` / `A88A484A0CA031615843C33CC6BE97936016960DA231EEBC35EFB0521CA72CC7` |
| G6-02 | RECOVERED（Force） | 1 | 2 | 13 | 1 | 1 | 0 | `693182E592D10A1096EAE675D8184F952F5C6BB4DF0F31E5A7B048E436AC397D` / `DCD7FFBDDC78A18C6089DF6707AFDCC2D255BEEC7E85461EA688EA4A481925BC` |
| G6-03 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `102BBE661C9CA7659D458A82C088912113BB823BF7DEC0E9783A46E32626B5CA` / `21A6B31387934DAB7348A8C85E770348EF19026FDFB53DA2F757AA87C0425DE2` |
| G6-04 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `A18705ED1DFADE53238F19766FCB5AC70DB018F650161ECDC639FEFD4DC12AA8` / `DB2607AC12F4CB3DA8F4C7A54DED05C06245A56BB4D51D89E57446CE5B63BEFE` |
| G6-05 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `E7076E0DA6AB9383663D47B859CBF14F0CFD3C07A8F75B8CF345E09A1B061F94` / `20170A7609A9F8ABE532B73BD51EFC775D5FF3D70FADB0E42B9B96C63ED500B0` |
| G6-06 | FRESH_START | 0 | 0 | n/a | n/a | 0 | 0 | `5880E4B8991F60E33F2809C8A74F863E98D2E8EC6D83CD9C4977D70C6012FAB3` / `566E539E70099B81338F8A167016BF72075B0AE163EB87591320A8C81BF2B4E1` |
| G6-07 | REJECTED | n/a | n/a | n/a | n/a | 0 | 0 | `80B89518E2BF20C8CF0A24FBF3852455205C4087555DB42DC041C239713DCF41` / `B054708FB2BBD4A494CEA11B3FB6F4ED18383545AA2B2A38849EADD043748026` |
| G6-08 | REJECTED | n/a | n/a | n/a | n/a | 0 | 0 | `A4082235A843F3303DE3148D6AE2A2962B986FCA51260AE85F6D24FF2647AB50` / `250C4DA8AC1BAB4B1A7B60C6D331D9C849D432B7F941D1C6554187D50C0690B7` |

所有 Recover native 0；Force prepare native −1；普通 prepare 0。每例保留边界/快照/launch/player.log。

## 5. 其他验证与限制

- LocalHost：**143/143 PASS**，0 failed/skipped，native 0。
- 工具矩阵：**10/10 PASS**，summary SHA `0BD3126A438D0C86C5D9F8EA1467C03B6F46C58F665A022B4D24D1B4000F730E`。
- `Mode Verify` 用 `-ExpectedSummarySha256 9E8E...`、`-ExpectedEvidenceSha256 801A...`、外部 helper，native 0；源 CSV SHA 前后不变。
- 全量 .NET 未重跑；上轮 2,830 total / 2,756 pass / 62 PG-BLOCKED / 12 skip 仍按环境限制单列。

## 6. 首失败与状态

- R3 首失败 `r3-cases-1`（旧 helper 缺最新 fault/native 检查）保留；`r3-cases-2` 为修正后正例。
- R3 负例 `r3-negatives/*` 全部保留；R2/R1 旧证据、caller 原件未改写。
- PG 62 BLOCKED + 12 skip 为环境限制；正式 C04/G2–G5 未批准，C04-G7-02 仍 BLOCKED；旧 Compare BLOCKED17/UNTRUSTED_LOCAL、15 项旧 UI 失败保留。
- 未 push/建改 PR/approve/MR/merge/deploy/scheduler/complete.flag；等待 caller 独立复核。
