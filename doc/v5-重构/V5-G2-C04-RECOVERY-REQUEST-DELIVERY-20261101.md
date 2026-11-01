# V5 G2-C04 恢复后原请求对账 — R4 修复交付

- R4 复核原件：`E:/majiang-review/caller-c04-d01-r4-review-7b2e8e7d/build/caller-review/REVIEW.txt`，SHA256 `E6A42C7E175488D01A0011A5AFB887BAC28013E8275CF08B5EAC29766B0C81DD`。
- `D01-R1-01` / `D01-R1-04` 保持关闭，不重开。
- 本轮 code commit：`d5428ae6c691f1eb7f11f745e2dffc4a19bebbee`；Player 源码冻结 commit `317f84984db108e7b5c42277eccdaa749482013e`，Player 字节未变。本报告为报告-only child commit；最终 HEAD/SHA 见交付消息。
- 范围仍为 11 tracked 路径；未改 Player/probe/core/collector/旧 decoder/协议；未 push/PR/approve/MR/merge/deploy/scheduler。
- 结论：`D01-R1-03` 的 strict 类型门禁已修；`D01-R1-02` 的 tracked 回归矩阵已补。原 8 正例继续 PASS，原件/CSV 未改写。正式 C04/G2–G5 未批准。

## 1. D01-R1-03 — native/timeout 严格类型

### 修复
- helper 的 `Int/Long/UInt/Bool` 改为严格读取：
  - `Int` 必须是 JSON Number 且可装入 int32；fraction/overflow/string/bool/null 抛错；
  - `Bool` 必须是 JSON True/False；string/number/null 抛错；
  - `Long/UInt` 同样不再默认 0。
- Recover wrapper 对 `nativeExitCode/timedOut`：
  - 属性必须存在、非 null；
  - `nativeExitCode` 必须是严格 int32 整数，拒绝 `false`、`0.4`、`2147483648`；
  - `timedOut` 必须是严格 bool，拒绝 `null`、`"true"`、`1`；
  - Recover 固定 `0/false`；PID/start/path/SHA/case/mode/fault/outcome 与 result 一致。
- Prepare 侧由 helper `ValidateFaultBoundary` 使用同一严格读取；合法 Force prepare 仍保留实际非 0/non-timeout 特例。

### 实际验证
- 原 8 正例 + 合法 Force 仍 PASS。
- Recover：native=false、0.4、2147483648；timedOut=null、`"true"`、1 → 全部 exit 1。
- Prepare：native=0.4、2147483648；timedOut=null、`"true"`、1 → 全部 exit 1。
- `r4-types/results.json`：12 个非法变体全部拒绝；2 个合法变体通过。

## 2. D01-R1-02 — tracked 回归矩阵

`tools/tests/VerifyP45C04RecoveryReconciliation.Tests.ps1` 由 10 例扩展到 **33 例**，保留原 10 例并新增：

- full8 + summary/CSV 双 pin + 可信 helper 正例；
- Recover/Prepare 完整 native/timeout 类型矩阵（缺失/null/bool/string/fraction/overflow/timeout/非零）；
- 合法 Force prepare 正例；
- 实际 case 替换、缺 case、缺 summary pin、错 CSV pin；
- 真实故障机制互标（Truncate↔BadChecksum，拒绝原因在故障检查处）；
- source/build/package：正确 317 绑定、缺 pin、空 package、tree/input 不一致；
- 原 10 个旧负例全部保留。

测试使用真实 recorded 案例只读复制为独占 `TOOL_FIXTURE`，显式传 pins/路径；必要参数缺失时脚本直接失败，不 skip。

## 3. 最终证据

### 正例
执行根 `build/c04-recovery-request/r3-cases-2/`：
- summary SHA `9E8EB182010E6C881BC387FB0DB149AD048B7DBE7DA68D461E66E25DBC1D8212`
- evidence CSV SHA `801A13E3651CD5476873F8F60C375D2008367F83BE1C9B0D1F15358BF1BD0658`（399 项，原文未变）
- Player `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`
- package 205 / `3D5AE0C1DDCD61A11FA5A9F9262A8BA59B6BC90A11E216BB28E6143BC8934D6F`
- strict helper SHA `954BBF665CA57CA736EC71DB840DC39A1F3F8244B9B7E592C7C611053980A793`
- `Mode Verify` 使用 summary/CSV 双 pin + strict helper，native 0。

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

### 工具矩阵
- **33/33 PASS，0 failed**；summary `build/c04-recovery-verifier-tests/4b8d1128344d4ed9bad21c3f0adf24f0/summary.json` SHA `16D596FB71B3142A12A62389382CF85183BFCA80C02A4516EDC3CDD6CB97DABB`。
- LocalHost：**143/143 PASS**，0 failed/skipped，native 0。
- 全量 .NET 未重跑；上轮 2,830 total / 2,756 pass / 62 PG-BLOCKED / 12 skip 仍按环境限制单列。

## 4. 状态与限制

- R3 负例 `r3-negatives/*`、R4 类型 `r4-types/*`、旧 R1/R2 证据与 caller 原件全部保留，未回填。
- PG 62 BLOCKED + 12 skip 为环境限制；正式 C04/G2–G5 未批准，C04-G7-02 仍 BLOCKED；旧 Compare BLOCKED17/UNTRUSTED_LOCAL、15 项旧 UI 失败保留。
- 未 push、未建改 PR、未 approve、未 record MR、未合并 v5、未部署、未改 scheduler/complete.flag；等待 caller 独立复核。
