# V5 G2-C04 恢复后原请求对账 — R2 修复交付

- R2 复核原件：`E:/majiang-review/caller-c04-d01-r2-review-836d29b7/build/caller-review/REVIEW.txt`，SHA256 `2AA0D323E7E3B0151C9FA2403C3E72079D413A11369E6D53399E8508B3525355`。
- `D01-R1-01`（隔离 staging，危险 source restore/clean 已移除）与 `D01-R1-04`（tracked 报告、测试路径）保持关闭，不重开。
- 本轮修复 code commit：`5b7a50ac50baf6ed7360ea1ffd1c8e57a5e94899`；Player 源码冻结 commit 仍为 `317f84984db108e7b5c42277eccdaa749482013e`（Player 字节未变，`F083B0E8...`）。本报告为报告-only child commit；最终 HEAD/SHA 见交付消息。
- 范围仍为 11 tracked 路径：8 原有路径 + 2 个授权 helper + 本报告。未改 production/client/runtime/collector/旧 decoder/协议；未 push/PR/approve/MR/merge/deploy/scheduler。
- 结论：`D01-R1-02`、`D01-R1-03` 已按原位修复；R2 已放行变体全部非零 FAIL；八原始正例仍 8/8 PASS，原件/CSV 未改写。正式 C04/G2–G5 未批准，C04-G7-02 仍 BLOCKED。

## 1. D01-R1-02 修复（原始 wire/动作/故障/固定分母）

### 1.1 Wire 身份与握手
- helper 逐帧 protobuf 解码后，新增 `ValidateWireIdentity`：
  - 必须恰有一个 IN `Welcome`、一个 IN player `SyncResponse`。
  - `Welcome.session_id/room_id/seat_id/role/binding_epoch/scope.scope_id` 与 result binding 完全一致；scope account/role/room 与 expected account/Player/local-main 一致。
  - 每个 envelope 非空 `session_id/room_id` 必须等于 binding；每个 `scope` 的 account/role/room 必须匹配，scope id 只能是新 binding scope 或原 request scope。
  - 原始 `ReceiptQuery`/`ReceiptLookup` correlation、request/scope、Found/Applied、receipt digest/version/event 继续逐帧核对。
- `foreign-wire-session`（7 处 session 替换并重算帧 SHA）现在 FAIL：Welcome/帧 session 与 result binding 不一致。

### 1.2 真实 Discard 效果
- probe 保留 prepare/recover 的 IN 原始帧；runner 将 prepare `frames` 复制为 `prepare-frames`。
- helper 从原始 `SyncResponse.Player` / `PlayerUpdate.snapshot` 解码：
  - prepare pre/post 的 hand 数（`ClosedTiles + HasDrawnTileId`）、river 数、具体 river tile、`PlayerUpdate.visible_events` 中 `TileDiscarded` 数量。
  - recover baseline snapshot 的 hand/river/tile 与 result state 对比。
- `prepare.json` 的 hand/river 只作交叉核对，不再作为事实来源。
- `invented-hand-river`（prepare/result 改成 1000/999、1000/1001，raw 不变）现在 FAIL。

### 1.3 故障实际施加与恢复选择
- runner 在 `Apply-Fault` 前后复制 primary/backup 到 `boundary/*-before.save`、`*-after.save`，并记录 `prepare-launch.json` / `prepare-player.log`。
- helper `ValidateFaultBoundary` 用 `LocalTableRecoveryFactsReader` 独立验证：
  - `None`：before/after primary+backup 均有效且 primary 字节不变。
  - `TruncatePrimary`/`BadChecksumPrimary`：after primary 无效、backup 有效。
  - `MissingPrimaryWithBackup`：after primary 不存在、backup 有效。
  - `BothMissing`：after 两者均不存在。
  - `BothBad`：after 两者均无效。
  - `IncompatiblePrimary`：after primary 无效、backup 有效。
  - Force 行：prepare launch native 非 0、未 timeout、`prepare-player.log` 含 `FORCE_KILLED`；非 Force 行 prepare native 0。
- `relabel-all-metadata`（G6-01/None 改 G6-03/TruncatePrimary，raw save/frames 不变）现在 FAIL：boundary 仍是两个有效 save，不符合 Truncate 分支。

### 1.4 固定分母与外部 pin
- `Write-Summary` 新增：
  - `caseSetComplete`；`expectedCaseCount=8`；`uniqueCaseCount`；`partial`。
  - 缺/重复/少于 8 固定 ID 时状态为 `PARTIAL`，永不 PASS。
- `Mode Verify`：
  - 必须显式传 `-HelperPath`（可信 helper，不再由 summary 指定）。
  - 支持 `-ExpectedSummarySha256` 外部 pin；不匹配即失败。
  - 必须 `summary.status=PASS`、`caseSetComplete=true`、8 个 case、8 个唯一固定 ID；空/重复/subset 全拒绝。
- `empty-root-verify`（cases=[]）现在非零 FAIL。
- Unknown 仍为 .NET recording-channel 受控注入；Player 仍只覆盖五 Found + 三安全边界，不宣称自然 Unknown。

## 2. D01-R1-03 修复（launch/process/source/package）

### 2.1 launch 强制
- wrapper 不再 `if (Test-Path launch)`：缺失 `launch.json` 直接 FAIL。
- launch 必须：
  - `probeMode=Recover`、case/fault/outcome 与 result 一致。
  - `timedOut=false`、`nativeExitCode=0`。
  - PID/start/path/SHA 与 result.process 逐项一致。
- `missing-launch`、`bad-native`（native=17、timedOut=true）现在 FAIL。

### 2.2 原始 build/source/package pin
- `Bind-ExistingPlayer` 新增：
  - build marker 同目录必须有 `source-snapshot.json`，且 `sourceHead` 一致。
  - `git cat-file -e <sourceHead>:.../C04RecoveryReconciliationProbe.cs` 与 Editor build 文件均必须存在；`cde5da16` 不含 probe，直接拒绝。
  - package manifest `files.Count` 必须等于 `fileCount`；路径唯一、逐文件 bytes/SHA、总 bytes 匹配；实际 205 文件集合必须与 manifest 双向一致（拒绝空/缺/重复/追加）。
- caller stale-source relabel（改 marker source 为 cde5）与 empty package（fileCount205/files=[]）现在 FAIL；原始 317f 正确 package 仍 PASS。
- Build-Player 在 staging 后写 `staging-source.json`：实际 346 个 Assets/Packages/ProjectSettings/slnx 输入及适配文件 SHA；`source-snapshot.json` 绑定 HEAD/tree/8 输入；`build-shared` 已转发 `-DotnetPath`。

## 3. R2 负例矩阵（实际执行）

| 变体 | 结果 | 退出/证据 |
|---|---|---|
| foreign-wire-session（7 处 session 替换、重算 SHA） | 拒绝 | verifier exit 1 |
| invented-hand-river（raw 不变，claims 1000/999） | 拒绝 | exit 1 |
| relabel-all-metadata（G6-01→G6-03，raw 不变） | 拒绝 | exit 1 |
| missing-launch | 拒绝 | exit 1 |
| bad-native（native17/timedOut） | 拒绝 | exit 1 |
| empty-root-verify（cases=[]） | 拒绝 | 非零 |
| stale source record + forged matching snapshot | 拒绝 | `git cat-file` 证明 cde5 无 probe |
| empty package files/fileCount205 | 拒绝 | `PACKAGE_MANIFEST_COUNT_MISMATCH` |

负例结果：`build/c04-recovery-request/r2-negatives/results.json`、`r2-bind-negatives/results.json`。
正例：八原件/新采八行全部 PASS；caller 原 205 文件/306 CSV 未改动。

## 4. 最终 Player 执行（复用已核验 317f 包，未重建 Unity）

执行根：`build/c04-recovery-request/r2-cases-2/`。
summary SHA `C1D87E55D71BB8D3E7C5C6761B193EB5173BC99CB38096F84FBEB6F6C3E2BB37`；evidence CSV `9F1245DD5ADE8204587B5BDC79F2034879003C495803EDD0DE1637361EEF7A87`。
Player SHA `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；package 205 / `3D5AE0C1DDCD61A11FA5A9F9262A8BA59B6BC90A11E216BB28E6143BC8934D6F`。helper SHA `5C214CFBDEE5174F5415CD8024AD6D870F6B833F8ED8EB28FA3E85690F01B851`。

| 行 | 结果 | stateVersion | eventSeq | hand | river | query | command | result / verification SHA |
|---|---|---|---:|---:|---:|---:|---:|---|
| G6-01 | RECOVERED | 1 | 2 | 13 | 1 | 1 | 0 | `C3F6BC8EC246D2FEF9E25BF3729BCD2BD0AACF10F5B18014E4BF2F136D0847EB` / `E7F7BCA34B5CC74BF6ED080C5E70E4DF2686418D1EA6EB3A5E6510AE525021D4` |
| G6-02 | RECOVERED（Force） | 1 | 2 | 13 | 1 | 1 | 0 | `5DBCCE625857A2921BBC639BD1BF57144E611CEAB1283C81F0ADBD88BBFEC248` / `E2EB8F8C6EBD761ADEDC1D290BAD1C727E88B98D9654059FE900991C4AF44CD5` |
| G6-03 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `7F3B08A572AFE319E4184E3F1E65700B163D766DCEFB5A91AA80C82EC81030FC` / `0DDED235FADA09EA14B4F558C2DDD73961418B0FB3832EB3DDBA343CA5AA316C` |
| G6-04 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `3ADFE4E38E4469E1C4660B07FE4E6F2008BB0E754124E92F19EB15CA44AE51BA` / `480D7A49C9D519F1EC43A667AE94D7B9EBE55B2E0B2359C3D7E643E6CC754E42` |
| G6-05 | RECOVERED_FROM_BACKUP | 1 | 2 | 13 | 1 | 1 | 0 | `56F028481197042C47B34B3500E9A7D4A41ACD922CC67F47D208185B39B9FC68` / `FDBCD1695E5C591018E94703317D8424E42A85DB137F6BAF1CC3A4DDC2833DF0` |
| G6-06 | FRESH_START | 0 | 0 | n/a | n/a | 0 | 0 | `A4F3C2A5340C27D4C8B3B47FBD1B5BF75189BA660EDCF47921D900E0CD71B2FD` / `5601665C63660C1DF8CD33E9CF11EAFD8EC47832DD02AC4F708FE394630A094C` |
| G6-07 | REJECTED | n/a | n/a | n/a | n/a | 0 | 0 | `FB2EEECA3CB9B3B150FF0D246E242E569086D30B29720267C586F6BC2117EB29` / `6C618D2DC5941B51B19A03409C17B05A0431E9302913FF199E3ECA6C0859A7F1` |
| G6-08 | REJECTED | n/a | n/a | n/a | n/a | 0 | 0 | `C2E693FF5A085A68B61E17FFA93312ADA0F5A571F47C878E43FD7D682BCB1C22` / `55484D804ACFDD42D44099855C062FFF9621EA3FC86C43983C3EA33FAF4D10E3` |

所有 Recover native 0；Force prepare native −1；普通 prepare 0。每例保留 `boundary/*`、`prepare-frames`、`prepare-launch.json`、`prepare-player.log`、raw authority/frames/launch。

## 5. .NET/工具/完整性

- LocalHost：**143/143 PASS**，0 failed/skipped，native 0。
- 工具矩阵：**10/10 PASS**，summary SHA `9E6B65466A1423C40DFBC95538ED890E22CAA9CD2B38AE9EE4C666AD74A13DB8`。
- `Mode Verify` 以 `-ExpectedSummarySha256 C1D8...` + 外部 helper 运行 native 0；源 evidence CSV SHA 前后不变 `9F1245DD...`。
- `staging-source.json` SHA `87C468EA8570FF22580B037C2DEF167DADAC6221A3D3C326EBBFB9302C74CF3F`（346 inputs）。
- 全量 .NET 未在本轮重跑；上轮 2,830 total / 2,756 pass / 62 PG-BLOCKED / 12 skip，仍按环境限制单列，不作为 D01 正例或回归。

## 6. 首失败保留与限制

- R2 首轮构建/门禁失败：`r2-cases-1` 的旧 helper（缺 prepare launch 完整检查）保留；`r2-cases-2` 为修正后正例。
- R2 变体原件：`r2-negatives/foreign-wire-session`、`invented-hand-river`、`relabel-all`、`missing-launch`、`bad-native`、`empty-root`；`r2-bind-negatives/stale-source-*`、`empty-package-*` 全部保留。
- 旧 R1 证据 `r1-cases-8`、`r1-final-authoritative`、caller 原件均未改写/回填。
- PG 62 BLOCKED + 12 skip 为环境限制；正式 C04/G2–G5 未批准，C04-G7-02 仍 BLOCKED；旧 Compare BLOCKED17/UNTRUSTED_LOCAL、15 项旧 UI 失败保留。
- 未 push/建改 PR/approve/MR/merge/deploy/scheduler/complete.flag；等待 caller 独立复核。
