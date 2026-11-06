# V5 G2-C02 独立受控恢复采集交付（2026-10-31）

状态：**第 5 轮返修完成；clean 工作树自测 27/27 PASS；本树 fresh Windows Player 构建 native 0，Normal/Force + save-fault 的 GAME 矩阵已取得；删除/复制清理均绑定同一 DELETE-capable 句柄；caller 尚未独立接受。** 本文件不是 G2/P4.5 通过、不是 Compare VERIFIED、不是发布授权。

## 0. 基线、路由与范围

- 工作树：`D:/majiang-wt/g2-c02-controlled-collection-ds-20261031`
- 分支：`wt/v5-g2-c02-controlled-collection-ds-20261031`
- 创建基线：`origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- 实际实施冻结基线：`6750b4362063169cd435302e0cc9c6685325bbe5`（已接受 fail-closed exporter 组件）
- 本轮只读核对：PR20 `b926775ff69a3aeef0763d8aff82f0e98dd150fc`；PR24 `81fa51bc43bbab4b33cbae7ad6f003e082892f3a`。未合并、未发布、未更新 PR。
- 只新增四个文件，未修改旧 exporter/common/schema/verifier、Domain/Client/Contracts、Unity 产品源码、solution、CI 或主工作树。
- B2 原线程 `thread_20261031_124526_0eef` 未触碰；驱动原线程 `thread_20261031_024102_96e6` 未修改、未代跑 (a)/(c)；未重开 exporter 线程 `thread_20261031_024102_cdf8`。
- 未 push、未创建/更新 PR、未 approve 自己、未合并 v5、未部署、未修改 scheduler、未创建 `complete.flag`。
- 首版实现提交：`f909c715`；第 5 轮返修实现提交：`6cae2910`（前置 `3f58a915`、`84455fde` 等；本文件随后仅追加证据）（三个脚本/测试；提交后 tracked clean，仅忽略的 `build/` 证据）。本文件在返修实现提交后仅追加本报告/证据行文；三个实现脚本/测试的字节与 SHA 见 §3.3。
- 四个文件：
  - `tools/unity/run-p45-controlled-recovery-collection.ps1` — 独立 collector
  - `tools/unity/verify-p45-controlled-recovery-collection.ps1` — 离线固定 verifier/案卷检查器
  - `tools/tests/VerifyP45ControlledRecoveryCollection.Tests.ps1` — 独立入口负例矩阵
  - 本文件

## 1. 独立设计审查

### 1.1 本地威胁边界是否明确

明确。信任根限定为：

1. 复核方审查并固定、在其独立 PowerShell 进程直接执行的 collector/verifier 代码；
2. 操作系统返回的 `Process` 对象（PID、startTime、可执行路径、实际命令行、退出/强杀事实）；
3. 复核方本轮直接取得的源码 HEAD/dirty、fresh build 命令/退出/日志、共享插件闭包和完整 Player 包。

边界外：同账号调试器注入、读取复核进程内存、管理员/内核攻陷、复核方主动伪造自己的直接观察记录。独立案卷目录与 Player 数据域只是证据组织分离，**不构成权限隔离**，也不声称不可伪造的密码学认证。无需外部签名服务、硬件根或生产账户；本实现没有新增 HMAC、签名或“给 JSON 认可”的 API。

### 1.2 已有接口能否实现

可以实现，无真实不可实施的接口阻断：

- 已接受的 `P45RecoveryVerifier` 能独立解码 `authority.save` + `cheat-state.bin`，重建 requests/ledger/physical/control/cheat 事实。outbox/journal 位于认证 save 内，但该 verifier 当前只公开上述恢复事实，未单列 outbox 请求/对账事实；本任务因此只封存完整原件并绑定其摘要，不声称独立 outbox 对账（C04 范围）。这是已记录的范围边界，不是本任务私自扩围的理由。
- 已接受的 Player probe 接受 `-p45-recovery-export/phase/challenge/run/attempt/data-dir/player-sha/package-sha/source-head/build-commit/build-provenance/ruleset`，足以绑定直接观察的身份。其路径约束（export 必须在 `-smoke-data` 根内，data 必须等于 `<smoke-data>/local`）在首次参考运行中实际发现并修正；这是使用约束，不是接口缺口。
- 不需要改 exporter/common/schema/verifier 即可实现独立采集；本实现没有 dot-source 旧 `p45-e2e-common.ps1`，也没有加载待测 fixture。

**真实环境阻断（非接口缺口）**：本机 Unity Editor 无有效 license，fresh Player 构建 native exit **198**（`No valid Unity Editor license found`）。因此正式 GAME 新构建 Player 采集为 BLOCKED；详见 §3.1。该阻断需要调用方提供有效 Unity license 后重跑，不能用旧 Player 冒充。

### 1.3 流程/负例是否足够

计划 §3 的流程可实现，并已补强以下检查：

- 启动前后都复算完整 Player 包（205 类文件路径/长度/SHA），不是只看 exe；
- collector 在复制后直接比对 `export.json` 与自身 `Process` 对象的 PID/start/path/hash/argv/package/build/challenge；不依赖 Player 自报；
- 每个 attempt 的原件用稳定复制（前后长度/SHA 一致）封存；复制后再次比对 export 身份；
- verifier 必须绑定复核方独立取得的 case manifest 摘要；严格文件清单，缺/多/改/重算均拒绝；重跑固定 verifier 解码 sealed raw bytes，不采信 summary 来源标签；
- `factsEqual`、`collectionChain`、`callerAcceptance` 三项分开；`callerAcceptance` 永远 `PENDING_INDEPENDENT_REVIEW`，没有自签 accepted 的路径；
- `authority.save` 在恢复重编码时字节可能变化，不当成恢复事实差异；但每个 attempt 的 authority/cheat/export/done 仍逐字节绑定到其 export 和封存记录。

未发现需要扩目标文件才能实施的接口或安全缺口。正式新构建 Player 采集仍 BLOCKED；本交付不把参考旧 Player 结果当 GAME 正例。

## 2. 实现摘要

### 2.1 Collector

`run-p45-controlled-recovery-collection.ps1` 的核心行为：

1. 校验 `BuildExitCode=0`、build log 含 `Build Successful`、source HEAD/dirty（可显式 allowlist）；对 GAME 要求 build commit 等于 source HEAD。
2. 冻结 collector/verifier SHA、Verifier 程序 SHA、dotnet 版本、完整 Player 包清单（路径/长度/SHA）、共享插件闭包、baseline、构建日志；先写 `input-manifest.json` 及摘要，再启动。
3. 创建不复用的 evidence root/case/runId/两个 attempt 和两个 64-hex challenge；`case.json` 在任何 launch 前写入。
4. `Start-Process` 自己启动 Player，默认 `-WindowStyle Hidden`，保留真实 `Process` 对象；读取 PID/startTime/path/exe SHA，并用 `Win32_Process.CommandLine` 核对实际 argv 的全部关键 token。
5. BEFORE：等待 `export.done`；Normal 真等待进程退出；Force 在 export 完成后只终止该自有对象并等待退出/句柄关闭。强杀退出码如实记录，不要求 0。
6. 在独立案卷中用稳定复制封存 `authority.save`、`cheat-state.bin`、`export.json`、`export.done`、`export.error.json`、Player log；复制前后长度/SHA 必须一致。
7. 直接交叉核对 export 与 launch/package/build/challenge；不匹配立即 `EXPORT_IDENTITY` 拒绝，不进入 COMPLETE。
8. 在 case 内数据域执行声明的 save fault；对 primary/backup 用固定 verifier 或 envelope 校验记录故障前后有效性，然后再启动 AFTER。
9. AFTER 用新 Process 对象/新 attempt/新 challenge；完成后重跑固定 verifier，保存 native exit/stdout/stderr；`factsEqual` 只比较恢复事实，不把 authority 重编码字节当事实差异。
10. 写线性 `records.jsonl`（sequence + previous hash + payload hash）、`chain.json`、完整 `manifest.json` 和 sidecar；最终报告只含摘要/路径。

没有 HMAC key、没有 `Write-*Observation` 签发函数、没有 `accepted` 参数。

### 2.2 Offline verifier

`verify-p45-controlled-recovery-collection.ps1`：

- 必须传 `-ExpectedManifestSha256`（以及可选 `-ExpectedInputManifestSha256`）；先校验 manifest 字节摘要，不匹配直接 exit 4。
- 严格复核 case 目录全部文件：不允许缺失/额外/长度或 SHA 变化；复核 `records.jsonl` 线性链、`chain.json` head/count/file SHA。
- 校验 sealed collector/verifier 脚本与运行中的 verifier SHA、固定 verifier 程序 SHA、source/build/package/plugin 摘要。
- 逐 attempt 重跑固定 `P45RecoveryVerifier`，保存 native exit/stdout/stderr，与 sealed facts 和 export facts 交叉比对。
- 校验两个不同 PID/start/challenge/export 目录、同一 data 域、BEFORE/AFTER 顺序、Normal native 0 或 Force 自有对象强杀事实、句柄关闭。
- 校验 export/launch/authority/cheat/done 全字段绑定和 save fault 适用语义。
- 输出 `factsEqual`、`collectionChain`、`callerAcceptance=PENDING_INDEPENDENT_REVIEW`、`gamePositive=false`；不提供 accepted 开关。

### 2.3 Tests

`VerifyP45ControlledRecoveryCollection.Tests.ps1` 全部通过**真实 collector/verifier 入口**（子 PowerShell 进程）执行，fixture 明确标 `FIXTURE`，从不当 GAME 正例。最终 **17/17 PASS, 0 FAIL, 0 NOT_RUN**（2026-10-31）。

## 3. 真实验证

### 3.1 Fresh source/plugin/Player build

第 2 轮记录的 Unity license 198 是历史状态，已由第 3 轮新环境事实取代（caller 空 project 探针 native 0；见 `build/caller-c02-r2-10f450ff/license-probe-result.json`，SHA `65F7970DE8F4ECB0F89AB82DA07EFEB80B2CB8E6122B5330F08201418B9E6251`）。第 5 轮实际重跑本树游戏构建：

| 项 | native exit | 结果 | 日志/证据 |
| --- | --- | --- | --- |
| `tools/unity/build-shared.ps1 -Configuration Release` | 0 | PASS；fresh 插件闭包 | `build/p45-controlled-collection/logs/shared-build.log`，SHA `50CD6F4A1A5FB18398C7B3C594D8D890C142ED7DCDAEDE50123AD05AA5258411` |
| Unity batch Windows Player build | **0** | PASS；`Build Successful` / `Result: Success` / `return code 0` | `build/p45-controlled-collection/logs/unity-build.log`，SHA `BC771AF719F1ABE7689275B4E29C6D510412138B404BCF42C056152E58B37751`；控制台 SHA `F2B6C90C3E1353E80CABDE68146F9B25784767BC1D8D77B38934218C6D4E767F` |

```powershell
D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe -batchmode -quit -nographics `
  -projectPath D:\majiang-wt\g2-c02-controlled-collection-ds-20261031\unity\Mahjong.Unity `
  -buildWindows64Player D:\majiang-wt\g2-c02-controlled-collection-ds-20261031\build\p45-controlled-collection\player\Mahjong.exe `
  -logFile D:\majiang-wt\g2-c02-controlled-collection-ds-20261031\build\p45-controlled-collection\logs\unity-build.log
```

Fresh Player：exe SHA `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；205 files / 128,385,659 B；整包摘要 `B7F40673E4B3E16126F89FBDB7A28914BA12FCD5FBF5F028FAD0970A5356B38A`。构建覆盖的 tracked 插件/Resources/ProjectSettings 已恢复，未提交。
### 3.2 参考机制矩阵（旧 Player，明确 FIXTURE，不是 GAME 正例）

为验证真实 Unity 进程/真实 probe/真实 raw export/固定 verifier 的机制，使用旧 accepted Player 作为**参考进程**：

- Player：`D:/majiang-wt/p45-recovery-export-ds-20261031/build/p45-recovery-export/player/Mahjong.exe`
- exe SHA：`F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`
- 整包 205 files / 128,385,488 B，包摘要 `F4A0D49837E061D706ECC3FB84FEF9561A9C74D662A55A72DB48B783E6B595BF`
- 构建 commit `672b6c395f73ca46e82bf8087bbebeed234381cf`（旧 build，不等于本树当前 source HEAD；因此所有参考 case 的 `evidenceClass=FIXTURE`，`gamePositive=false`）

| case | collector exit | observed | factsEqual | chain | BEFORE PID/native | AFTER PID/native |
| --- | --- | --- | --- | --- | --- | --- |
| reference-normal-4 | 0 | RECOVERED | true | PASS | 58576 / 0 (EXITED_OK) | 63604 / 0 |
| reference-force-1 | 0 | RECOVERED | true | PASS | 57524 / -1 (**FORCE_KILLED**) | 69180 / 0 |
| reference-fault-truncateprimary | 0 | RECOVERED | true | PASS | 41824 / 0 | 57200 / 0 |
| reference-fault-badchecksumprimary | 0 | RECOVERED | true | PASS | 50876 / 0 | 52044 / 0 |
| reference-fault-missingprimarywithbackup | 0 | RECOVERED | true | PASS | 31732 / 0 | 46124 / 0 |
| reference-fault-incompatibleprimary | 0 | REJECTED | false | PASS | 41668 / 0 | 65428 / **1** |
| reference-fault-bothmissing | 0 | FRESH_START | false | PASS | 34716 / 0 | 56108 / 0 |
| reference-fault-bothbad | 0 | REJECTED | false | PASS | 54844 / 0 | 16776 / **1** |

对应 case manifest 摘要：

- normal-4 `8C65C1F937EB1B00CB7FF9567203F8903193F78B1A94BEF1D3D1C247D86E6B6C`；input `24369ED8011B74A94745C5C88A03B860DC711604A192C19F9CD6505020D29C1A`
- force-1 `000ACBAEB480E6AC11A61C15CFECBBEDB72D136D647264BD733D344B8B4BBC8E`；input `E6B9F6B6A099EDA13EBC904D5A356B0931439F1B5CD3BF56DE3EBA78E31ABE7C`
- truncate `873F5861C0491CFA4358F9C4A32FAE7B66F9F97C4471147D0A2BD081E366D1E3`
- bad checksum `4DD4105D11C44E70BB1FEDAE1DEDF4BBEFE3D8A556B6E3A54B6703AB1FEF8D54`
- missing primary/valid backup `759836C19BD1CDEAEB6704AD1591133CF9F809A23A234FE6978DA946CC2EC40C`
- incompatible `72AE986BB61725A9E00CF106CB36D43A503550CB559862C1C8D872F8248B2BE7`
- both missing `36DEEFB6768CA238C9F48121D094B2B1E0EE76FEEC807D62D7505F442BEF5CB7`
- both bad `E9D1B62E0B85B81C70183186640A091442157893E4BE9405AB4AF417719095C3`

离线 verifier 对这 8 个参考案卷全部 exit **0**、`collectionChain=PASS`、`factsEqual` 与上表一致、`gamePositive=false`、`callerAcceptance=PENDING_INDEPENDENT_REVIEW`。汇总：`build/p45-controlled-collection/reference-verify-summary.json`，SHA `1B304B1946252BC74A66EFE0B0878CAD9E4DDB0C752F7A22F6E6200192A0A24E`。

第 2 轮返修后在 clean track 树用新 collector/verifier 重跑同一 8 case，全部 collector exit 0、verifier exit 0、chain PASS、`gamePositive=false`；新 manifest：normal `4FF33AC78A76197B605F4F2F5CC9A5080392B78A2D679CA3897E81EC1A294B52`、force `3DFEF264019936879EAC39BF9E034BE85657E9D4DCA07AD9155A6A8C5FC941F6`、truncate `2CCA5E9B3DC6DD335567B9095C2BE20DD9574042AF280B75E3BB3FE84C36ED10`、bad checksum `F69E30CA15B22ECB3260A9A6B9CB78BC65278624C9A555465660C5591F030CC0`、missing primary `86F1FC6F0D3002E056470DF18B06EB3A1CAAD78B40421690DAE5E9E55F83DA54`、incompatible `7FFC5AF97BD7767EE301852C85169F373E9225CDB7F7DE1F1ADD0A2CA6800B37`、both missing `0691101B9BDF0C40FBBB2FD1848776935A3154CB0CA8DF9FBFAB5DF262C58EC2`、both bad `07B89B51127ECEE20832636537F0A14E3E2B4D5256F07A1347F79B922742DF14`。汇总 SHA 见 §3.3。

**首次失败保留**（未删除、未重写历史）：

| run | 首次失败 | 根因 | 修复 |
| --- | --- | --- | --- |
| reference-normal-1 | BEFORE/AFTER native 1，`ARGUMENT_INVALID: export directory escapes the -smoke-data root` | collector 把 `-smoke-data` 指向 case 子目录，export 在 data 子目录 | 改为整个 evidence root |
| reference-normal-2 | native 1，`data directory is not the production local save directory under -smoke-data` | probe 要求 data 必须是 `<smoke>/local` | data root 改为 `<evidence>/local` |
| reference-normal-3 | 两次 native 0，但 `factsEqual=false`，仅 authority SHA/bytes 不同 | authority 恢复重编码字节被错误当成恢复事实差异 | 事实比较排除 authority 字节，仅保留 cheat/state/ledger/physical/requests/control；artifact 仍逐字节绑定 |

这三条 run 的 `case-result.json`/`collector-result.json` 保留在 `build/p45-controlled-collection/reference-normal-1..3/`。

### 3.3 独立负例/测试矩阵

历史记录：开发期 `run-7` 在**dirty 工作树**（四个新文件未提交）得到 17/17；该记录保留但不再作为最终 clean 验收。第 2 轮返修后的原入口在**clean track 树**重跑：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/tests/VerifyP45ControlledRecoveryCollection.Tests.ps1 `
  -OutputDir build/p45-controlled-collection/tests/clean-final-7 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll
```

结果：**total 25 / passed 25 / failed 0 / NOT_RUN 0，native 0**。新增覆盖 clean/empty dirtyPaths 正例、批准 dirty 正例、missing/malformed dirtyPaths 负例、junction/hardlink 数据域别名、sealed 目标 junction、journal/decoder-output leaf hardlink、预存 sentinel 不被覆盖、BEFORE 缺 `export.json`/`done`/authority/cheat 的统一拒绝。

- `build/p45-controlled-collection/tests/clean-final-7/test-summary.json`，SHA `834BED0C5F5FA4A89D3CB0ADDB23E0283F2E1A8C805507D735162FC61835D4AA`
- `build/p45-controlled-collection/tests/clean-final-7.console.log`，SHA `67895F3E9C9F00A6D33B8EBD9D0BC254563546E8CC94F5B93A705D4156B31837`
- 固定 verifier DLL：SHA `B1E733FB7B7C7082EC0D92F14E79C26087879FFF087B27E43F15E4F20138EAD3`
- 工具脚本 SHA：collector `DA6F48FEA388E21B7EA31D1C2F105A1D0A4379619F634804536E74619708F6AC`；verifier `E8C67540E7EEA4D6702234542B9D0F0505B726117062715422CF8836D66D6286`；tests `013BA3B67C85610153B92919AA34DF98E4DBB2AE4C07CFB6E491ECF05EEE2FFE`
- 返修参考矩阵：`build/p45-controlled-collection/rework-reference-verify-summary.json`，SHA `92403C73080F966E7CC4D8909B8B83D444C69B7CF4DB496375CB25845063217F`
- caller 边界复现脚本重跑：MissingBefore exit 17 `BEFORE_EXPORT_INCOMPLETE`、HardLink exit 19 `RESOURCE_ALIAS`、Junction exit 19 `RESOURCE_ALIAS`，foreign saves 均未变；日志 `build/p45-controlled-collection/caller-repro-after-fix-3.log`，SHA `475FE054BC20E670459A4F38B22D131C05F1A381DFB5FD4DC4917F745347E382`

### 3.4 旧 Compare 仍 BLOCKED

在 accepted 组件证据上重跑旧 `run-p45-e2e.ps1 Compare`：

- exit **17**
- `outcome=BLOCKED`、`pass=false`、`requiredFieldsEqual=true`
- 日志：`build/p45-controlled-collection/old-compare-collection.log`，SHA `10C305EF343752CDF73334F2376D9F33D393E81D698DB846AFDA2B411BDA5A6F`
- accepted 组件自身最终 `UNTRUSTED_LOCAL` 结果保留在
  `D:/majiang-wt/p45-recovery-export-ds-20261031/build/p45-recovery-export/official-r3-672b6c3/normal-20261031-062349/evidence/compare-20261031-065424-0f4059.json`：
  `outcome=BLOCKED / pass=false / requiredFieldsEqual=true / chainStatus=UNTRUSTED_LOCAL / realPassGate=NO_NON_FORGEABLE_COLLECTOR_ATTESTATION`。

本任务没有修改旧 Compare、没有把新结果改名成旧 Compare PASS、没有伪造 VERIFIED。

### 3.5 Fresh GAME Windows Player 矩阵（第 5 轮）

同一 fresh Player、`evidenceClass=GAME`、`-AllowedDirtyPath` 仅覆盖构建产生的 tracked 输出；8 个 case 全部 collector exit 0、offline verifier exit 0、chain PASS、`gamePositive=true`、`callerAcceptance=PENDING_INDEPENDENT_REVIEW`：

| case | observed | factsEqual | BEFORE PID/native | AFTER PID/native | case manifest |
| --- | --- | --- | --- | --- | --- |
| normal | RECOVERED | true | 50160 / 0 | 67652 / 0 | `087EAAE80AE7778483373C53F717732A731DEA0987FA9485ED8F3E9B8BDFE409` |
| force | RECOVERED | true | 55040 / **FORCE_KILLED -1** | 36316 / 0 | `B1F725F46DE405774AB4B714C729F2B17357954E5418F2E9DDBFE668829AE10C` |
| truncateprimary | RECOVERED | true | 62488 / 0 | 65056 / 0 | `14FFD4196C0BF4D8ED93A3696E50FE47A94FE90467CB2B6504110F85EC161262` |
| badchecksumprimary | RECOVERED | true | 69560 / 0 | 7620 / 0 | `EFED93E545E3C258DEC8ED48213406B65EC47837492D060F59E96E03CD52B448` |
| incompatibleprimary | REJECTED | false | 49060 / 0 | 21960 / **1** | `7A38AE113CAEAF6DB589E5F5ADE6748B4F39CE25CB0B158E6A9BB288244A58C7` |
| missingprimarywithbackup | RECOVERED | true | 61952 / 0 | 28592 / 0 | `58CF236457F4B9A273D64B2A0D47E6F5C1B8D2A180055E5AD6F30937DA4EE952` |
| bothmissing | FRESH_START | false | 11824 / 0 | 4936 / 0 | `24E7C32BF6FB20A7CC9BD30198F93B6051149DF7E5CDD1C3A7F23C7632446F46` |
| bothbad | REJECTED | false | 33232 / 0 | 12128 / **1** | `221ED7CFB2A411BA5B62E73DF2D82628303EE327EABCF6EEBC4F3BF3543DC5D4` |

输入 manifest/完整案卷在 `build/p45-controlled-collection/r5-game-*`；汇总 `r5-game-summary.json` SHA `90F0F6B5DA3E9D45CDEFE69C1A8C14C52E36DF0B04C497F167A865DA8AABE58E`。这只是本任务开发版本 GAME 采集事实，仍需 C03 统一候选绑定与 caller 独立接受。
## 4. 计划 §6 逐行矩阵

| 计划行 | 本任务结果 | 证据/说明 |
| --- | --- | --- |
| Normal / Force | **PASS（fresh GAME）** | 本树 fresh Player；两个真实 Unity 进程/attempt、同 data 域、BEFORE/AFTER、native 0 与 FORCE_KILLED -1、factsEqual true、gamePositive true |
| 主文件截断/坏校验/不兼容；主文件缺失且备份有效 | **PASS（fresh GAME）** | truncate/bad checksum/missing primary 均从有效 backup 恢复，factsEqual true；incompatible 被明确 REJECTED，exit 1，无 facts |
| 主备均缺/均坏 | **PASS（fresh GAME，适用语义）** | 均缺为合法 FRESH_START，factsEqual false，未声称恢复旧状态；均坏为 REJECTED，exit 1 |
| 改 source / 重算 summary+manifest | **PASS（负例）** | tests：重算 manifest 后错 PID/start/path、错 facts、错 phase/attempt/challenge 均非 0；caller 摘要不匹配 exit 4 |
| 复制 run、phase/attempt、旧 challenge、错 build/package | **PASS（负例）** | tests 中跨 case manifest exit 4；重算 manifest 后错 phase/attempt/challenge、错 package/build 被 verifier 拒绝 |
| 错 PID/start/path；冒认仍存活进程 | **PASS（负例）** | probe 错 start/path exit 19；Force 冒认外来 PID exit 19，bystander 未被杀 |
| journal/decoder stdout/stderr/exit leaf hardlink | **PASS（负例）** | 句柄绑定后 Records/VerifierStdout 均 exit 19 RESOURCE_ALIAS，foreign sentinel 28B 不变；caller leaf 复现同结果 |
| 缺/截断原件、复制中变化、封存后改单个文件 | **PASS（负例）** | 缺 export.done、截断 authority 后重算 manifest 均非 0；export 后修改 authority 的 changed-original case exit 19、`EXPORT_IDENTITY`；稳定复制前后长度/SHA 校验；未单独制造并发写竞态 |
| 现有公开 collector 签发函数伪造整套文件 | **PASS（负例）** | 新 verifier 对旧公开文件/无 case manifest 拒绝；旧 Compare 仍 BLOCKED17 |
| 仅 fixture/模拟进程 | **PASS（边界）** | 所有 fixture case `evidenceClass=FIXTURE`、`gamePositive=false`；没有标 GAME 正例 |

恢复后的真实请求对账、未知结果按原 ID 对账、零重复副作用属于 C04；本任务不声称通过。最终 G2 功能结论仍依赖 C03 统一候选绑定。

## 5. 复现命令

独立测试矩阵：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/tests/VerifyP45ControlledRecoveryCollection.Tests.ps1 `
  -OutputDir build/p45-controlled-collection/tests/run-7 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll
```

离线复核一个封存案卷（`<manifest>` 必须是复核方从 collector 直接输出固定、不是被测方后来重算的值）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/verify-p45-controlled-recovery-collection.ps1 `
  -CaseDir build/p45-controlled-collection/<run>/case `
  -ExpectedManifestSha256 <collector 输出的 caseManifestSha256> `
  -ExpectedInputManifestSha256 <collector 输出的 inputManifestSha256> `
  -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -OutputDir build/p45-controlled-collection/<run>/verify
```

正式 GAME 采集（需先取得有效 Unity license 并构建本树 fresh Player）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-controlled-recovery-collection.ps1 `
  -Case Normal -SaveFault None `
  -PlayerPath build/p45-controlled-collection/player/Mahjong.exe `
  -BuildCommit <40-hex source HEAD> `
  -BuildLog build/p45-controlled-collection/logs/unity-build.log `
  -BuildCommand "<exact Unity build command>" -BuildExitCode 0 `
  -Baseline D:/majiang-wt/p45-windows-validation-20261030/build/p45-windows-validation/series-r2/private-saves/baseline.save `
  -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -EvidenceRoot build/p45-controlled-collection/game-<unique>
```

`-Case Force` 复用同一命令；save fault 用 `-SaveFault TruncatePrimary|BadChecksumPrimary|IncompatiblePrimary|MissingPrimaryWithBackup|BothMissing|BothBad`。

## 6. 范围与安全检查

- `git status --short` 只应有四个新增文件；tracked 插件 DLL 已恢复，未提交构建产物。
- 未提交任何 `build/` 私有原件、seed/暗牌/私有账本；报告只引用摘要和路径。
- 未修改旧 exporter/common/schema/verifier/Compare；旧 `BLOCKED17/pass=false` 保留。
- 未使用 HMAC/签名/外部服务；collector/verifier 只报告事实与结构，不签发 accepted。
- Unity/插件/Editor/Player 使用 `Local\MahjongV5P45UnityValidation` 互斥；本任务未抢占他人进程，未按进程名批杀。
- 未 push、未创建/更新 PR、未 approve 自己、未合并 v5、未部署、未修改 scheduler、未创建 `complete.flag`。

## 7. 剩余阻断与交接

1. **开发版本 fresh GAME 采集已完成**：本树 fresh Player native 0，Normal/Force + 6 个 save-fault GAME case 全部 collector/verifier exit 0、chain PASS、`gamePositive=true`；仍需 caller 独立接受，并在 C03 统一候选上重绑，不能替代最终 G2。
2. 历史 Unity license 198 记录保留；第 3 轮 caller 空 project 探针与本树实际游戏构建均 native 0，当前状态已不是 license 阻断。参考旧 Player 的 8 个 FIXTURE case 只作历史/兼容证据，不是 fresh GAME 正例。
3. C03 统一候选绑定、C04 恢复后真实请求对账/零重复副作用、C05 平台/视觉、C06 阶段裁决均不在本任务范围。
4. 复制期间并发写竞态没有单独制造实时 race；已实现稳定复制前后长度/SHA 检查，并用 changed-original 负例证明变化原件不会被重新封存为 COMPLETE。若调用方要求并发写探针，可另列最小增量。
5. 现有固定 verifier 不单列 outbox 对账事实；C02 只封存完整原件/摘要并绑定 export，恢复后真实请求对账与零重复副作用仍归 C04，不能据此声称已通过。

## 8. 第 2/3/4/5 轮独立复核缺陷修复映射

| ID | 级别 | 修复 | 验证 |
| --- | --- | --- | --- |
| C02-R1-01 | MAJOR | 测试仅在 dirty allowlist 非空时追加 `-AllowedDirtyPath`；verifier 直接读取 `dirtyPaths` 属性值，校验 `dirty` 为 bool、`dirtyPaths` 为数组（空数组合法）、元素为字符串且与 dirty 标志一致；测试新增 clean/empty、批准 dirty、missing/string/null dirtyPaths | clean 原入口 27/27 native 0；`clean-final-7/test-summary.json` SHA `834BED0C…D4AA`；caller clean case 的 source 检查已不再报错 |
| C02-R1-02 | MAJOR | 新增 Windows 文件身份/最终路径检查（`CreateFileW`+`GetFinalPathNameByHandleW`+`GetFileInformationByHandle`）；在 fault 写入/删除前、seal 目标父目录写入前、每次证据原子写前检查 reparse/junction、最终路径归属和 hardlink 数；junction/hardlink 在写前 fail closed；测试 probe 改为每轮唯一 `CreateNew`，固定 sentinel 不覆盖/不删除 | 测试：junction/hardlink 两项 exit≠0、`RESOURCE_ALIAS`、AFTER 未启动、foreign primary/backup SHA 不变；sealed 目标 junction exit≠0 且 foreign 目录 0 文件；预存 sentinel 哈希不变；正常 8 个参考 case 全部 COMPLETE |
| C02-R1-03 | MAJOR | BEFORE 必须有 `DONE`、四件必需原件、`export.done` 摘要、直接观察身份匹配、固定 verifier 成功，否则 `BEFORE_EXPORT_INCOMPLETE`，不进入 fault/AFTER；AFTER 仅对声明 REJECTED 允许错误/无导出；`beforeRecovered` 同步收紧 | 测试 `missing-before-json/done/authority/cheat/json,done` 全部 exit≠0、`afterStarted=false`、无 factsEqual；正常 Normal/Force 与 6 个 save-fault 参考 case 保留 |
| C02-R1-04 | MINOR | 三个新 ps1 统一 LF、去除 EOF 空行；不动历史 whitespace | `git diff --check 6750b436..HEAD` exit 0；`rework-diff-check.log` SHA `E3B0C442…B855` |
| C02-R1-02 残留 | MAJOR | 所有 journal/decoder stdout/stderr/exit/原子写改为 CreateNew 或已存在 leaf 的持久句柄；GetFileInformationByHandle 校验句柄最终路径、reparse、hardlink 数后才写；native 输出改为 ProcessStartInfo 捕获后由安全句柄落盘，不再 shell 重定向截断 leaf；Player launch 取消 stdout/stderr 按名重定向 | 测试新增 journal/verifier-stdout leaf hardlink；caller leaf 复现 Records/VerifierStdout 均 exit 19 RESOURCE_ALIAS、foreign sentinel 28→28B；日志 leaf-repro-after-fix.log SHA 1BAC58F1393FF250A78E8A8183C768B54458241BE8F7887089B01B51F6E64801 |
| C02-R2-01 | MAJOR | 新环境 license 事实更新后，实际重跑本树 fresh shared/Player；取得 GAME Normal/Force + save-fault 8 case | Unity build native 0；8/8 collector/verifier exit 0、chain PASS、gamePositive=true；汇总 r5-game-summary.json SHA 90F0F6B5DA3E9D45CDEFE69C1A8C14C52E36DF0B04C497F167A865DA8AABE58E |
| C02-R1-02 删除残留 | MAJOR | Remove-OwnedFile 改为 OpenDelete 获取 DELETE-capable 句柄，InspectHandle 校验最终路径/reparse/link 后在同一句柄上 SetFileInformationByHandle(FileDispositionInfo)；unstable-copy/fault 删除全部走该路径，不再 [IO.File]::Delete(path)；新增确定性子目录交换/句柄生命周期测试 | 测试 deterministic handle-lifetime delete keeps a foreign sentinel intact 通过（parent swap 被已校验句柄阻断或原对象删除，foreign sentinel 原字节不变）；clean 入口 27/27；caller delete-race 8 次 foreignExists=true |
| C02-R1-02 copy 清理 | MAJOR | Copy-StableEvidence 目标首次打开改为 CreateNewDelete（GenericWrite+Delete+ReadAttributes，CREATE_NEW，无共享）；MarkDelete 使用同一句柄；清理失败不再吞掉，区分 COPY_CLEANUP_FAILED | 新增 actual unstable-source copy 测试：源 1MiB 在复制窗口被独立线程改 1B，函数报 COPY_UNSTABLE，目标不存在，foreign sentinel 不变 |
| C02-R1-02 test 假绿 | MAJOR | delete lifetime test 的 foreign 移到 OwnershipRoot 外且同名 local-main.save；originalDeleted 按 moved 检查 Park 或 Dir 的真实 leaf；断言 worker completed/attempted；无删除 mutation 会留下 Dir leaf 并 FAIL | clean 套件 27/27；no-delete 变体不能再因 Park 不存在而 PASS |

返修后新增/保留的失败记录：`rework-run-1..6` 保留首次测试失败（clean 空参数、ownership 输出泄漏、fixture `sealed` 关键字、BEFORE 缺 marker 等）；未删除、未放宽断言。最终 clean 入口 `clean-final-7` 27/27。

**callerAcceptance 保持 `PENDING_INDEPENDENT_REVIEW`；本文件不构成 caller 接受或 G2/P4.5 PASS。**
