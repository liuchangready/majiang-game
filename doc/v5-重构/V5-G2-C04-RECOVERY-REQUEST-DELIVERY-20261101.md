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

## 5. Caller 独立验收与 PR20 收口（2026-11-01）

以上是接收方交付时的状态。本节为调用方独立复核：**D01 自身范围通过，D01-R1-01..04 全部关闭**。此结论不批准正式 C04 或 G2–G5。

- 接收方最终提交：e699e74ddf50d66185866399e5129324d2ed97f9；接收方报告原件 SHA-256：88B8217E91CFE840B392BE8EB4D66CDDA4A4D320F032012E354C22E3169AB2AC。
- 从现有 PR20 已接受头 f3ba015b7b54061a0b144ddf546cd6f8e37548ca 合入本任务；受测集成提交 f1c8a05495b76eac49278bf3f0b625b38ada2030，仅新增原授权 11 个路径，没有合入 D03/D04 未验收代码。
- 调用方在同一授权工具测试文件补齐 26 个回归；最终工具受测提交 a8a480fb1a45b814112b4a78cfcf8d6af0c61c72。相对受测集成提交只改变工具测试，verifier、Player probe 和业务源码未变。最后发布仅更新本报告与工具脚本末尾空行，无运行逻辑变化。
- 调用方证据根：E:/majiang-review/caller-c04-d01-r5-closeout-97d128a7/build/caller-review。run-tests.ps1、run-final-tools-clean.ps1 及对应 .native.json 保留完整实际命令、PID/start、退出码、原始 stdout/stderr。

| 独立验证 | 实际结果 |
|---|---|
| LocalHost fresh Release Rebuild / 原生直接执行 | build exit 0；**143/143**，0 failed/errors/skipped/not-run，native 0 |
| verifier fresh Release Rebuild | exit 0；使用调用方新编译 helper |
| 接收方原样 tracked 工具矩阵 | **33/33**，native 0 |
| 补齐后的 tracked 工具矩阵 | **59/59**，0 failed，native 0；33 已包含在 59 中，不能相加 |
| 固定八案卷离线验证 | 外部 summary/CSV 双 pin + fresh helper，**8/8 PASS**；含合法 Force prepare |
| 原案卷完整性 | **399/399** 文件 bytes/SHA 匹配，原 summary/CSV 前后不变 |
| Player 复用绑定 | 原 317f8498 source/build/snapshot pins 与 **205** 文件包匹配；被改写来源和缺失 pin 均拒绝 |
| PowerShell 5.1 | runner、wrapper、工具矩阵三份脚本解析零错误 |

原八案卷为五个 recovered（其中三个 backup recovery）、两个 rejected、一个 fresh start；恢复请求查询有真实原请求 Applied 回执，零重复提交与零重复副作用。UnknownOnce 仅为 .NET 受控通道注入，不声称 Player 自然触发 Unknown。

调用方独立重放上一轮全部错误类型变体：Recover 的 bool/fraction/null/string/native17，以及 Prepare 的 bool/fraction/null、timeout null/string/number、int32 overflow 全部非零拒绝。入库矩阵进一步覆盖缺字段、真实 timeout/nonzero、Force 错误结果、准确八分母/重复/额外/未知行、manifest 缺失/实际文件哈希错误、故障类型互标、源码 tree/input/record 和包文件错误；负例断言对应门禁原因。人工重新封存的副本仅为 TOOL_FIXTURE，不是新的 GAME 记录。

调用方扩展矩阵首次运行失败日志保留在 tool-suite-final59.*：包含启动时尚未提交的 clean-source 拒绝，以及 PowerShell hashtable 的 Remove 方法被误当键读取导致的夹具错误。随后改为显式键读取并提交；最终完整矩阵在干净提交上重跑，tool-suite-final59-clean.native.json exit 0。这些调用方夹具问题未归为产品缺陷，也没有回填原始失败。

关键原件 SHA-256：

| 文件 | SHA-256 |
|---|---|
| localhost.xml | E5C02181D373AAD4D40730A6287781712F8D6FC08A07DA20A4FF1F7455DA1761 |
| fresh helper DLL | 2FE4E4ABDAB9BBC43D1737B7F0FDBFBC8D446F0D993BD9B456434DFEF17EB6AF |
| tool-matrix-final59-clean/summary.json | 5E54B7148A7E20D5A6946A839B5E6D3951BE1B9BF626937C99E1BAEA6434F23D |
| tool-suite-final59-clean.stdout.log | 41E544F994A2A83D95665C2D1E68FB56A139A16EB20CDB316E896D548DED3F58 |
| current-probes.json | CC010692EC858F5FF5C4D3BC309AECE6121C5929001D311E1AFB21D71A75E228 |
| original-seal-audit.json | B5C69129F97EE7E3110BA539DEF9832F85081FF8D94D8592F19A02F8B83C3B34 |

本轮调用方没有启动 Unity、Player、PG、Android、设备或 CI；八份 GAME 记录仍绑定原 317f8498 Player，不能称为新合并头的 Player 运行。历史未配置 PG 门禁结果不否决 D01；C03 旧 15 项 UI 失败记录保留，后续 UI15 已独立接受，不能描述为当前仍有这 15 项失败。

唯一发布入口沿用 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，源分支 feat/v5-post-g1-batch、目标 v5，保持 OPEN/Draft；无新增子任务 PR。正式最终候选同版本重绑/Player 复验、C04 其他场景与 LOCAL 来源语义、C05 APK/设备/视觉输入、自然 PG 整场终局及 G2–G5 验收仍独立处理。未合并、部署或修改定时任务。
