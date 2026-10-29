# V5 原生捕获修复交付（D-CAPTURE-01）

日期：2026-10-29
任务：修复统一验证器 `tools/verify-v5.ps1` 在 Windows PowerShell 5.1 下捕获原生 stderr 时丢失真实退出码的 MAJOR 缺陷。
状态：**工具执行/捕获边界修复已在本树验证；第 2 轮已复现并修复 D-CAPTURE-01-SCOPE（caller-local LASTEXITCODE 遮蔽）；第 3 轮已复现并修复 D-CAPTURE-01-RESTORE-EXISTENCE（变量原本不存在时未移除）；未重跑完整 unified/PG/WSS 批次，不代表整批通过，不宣称 G2/G3 通过。**

---

## 1. 结论与边界

- 只修执行与捕获边界：`tools/verify-v5.ps1` 的 `Invoke-Captured`。
- 未修改任何测试结果判定规则：`Read-DirectResultXml`、`Test-DirectInvocationEvidence`、VSTest/console 计数解析、`selected-count`、skip/not-run 门槛均保持原样。
- 未修改测试项目、业务代码、fixtures、manifest、pin 或 scheduler。
- 未 push、未建 PR/MR、未合并、未部署、未创建 `complete.flag`。
- 未触碰其他工作树、另一 caller 的 `cx54-89b473c1` 树、其 PG/WSS 集群或进程。
- 仅使用本原生会话与 `deepseek/deepseek-v4.1-flash`，未转派、未建子任务、未操作其他线程。

---

## 2. 冻结基线与工作树

| 项 | 值 |
|---|---|
| 冻结基线 HEAD | `e3ddd83fec0bc2592bb490594e5452b672bbb8f7`（已审查发布的唯一 Draft PR20 头） |
| 本树分支 | `wt/v5-native-capture-ds-20261029-1145` |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| 开工前 tracked 状态 | clean |
| V4 生产 | 保持唯一生产基线 |
| 门禁口径 | G1 APPROVED；G2–G5 未批准；181 classified 未覆盖 |

未同步任何在途代码；未把其他树的改动带入本树。

---

## 3. 缺陷登记、复现与证据

```text
[ID] D-CAPTURE-01
严重级别：MAJOR
位置：tools/verify-v5.ps1 :: Invoke-Captured（原约 line 137/178 起）
```

### 3.1 复现

caller 在本树独占 artifacts 目录的原始复现：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File artifacts/caller-capture-repro-09e55e195f184f18b09268561d7c24ff/probe-native-stderr.ps1
```

原始材料保持只读、未被覆盖：

- 目录：`artifacts/caller-capture-repro-09e55e195f184f18b09268561d7c24ff/`
- `stderr-probe.json` SHA256：`CC934DCF7A06E5CD2990ADC9CCF3566230DD115159577BCA3173EC6F09A11429`
- 原始结果：`capturedExit=null`、`nativeExit=0`、`nativeStdout="stdout marker"`、`nativeStderr="expected refusal diagnostic"`、`reproduced=true`。

原始生产日志的核心内容：

```text
stdout marker
[verify-v5] invocation threw an exception:
System.Management.Automation.RemoteException: expected refusal diagnostic
```

### 3.2 实际 vs 期望

| 维度 | 修复前实际 | 期望 |
|---|---|---|
| 原生 stderr | 在 `$ErrorActionPreference='Stop'` 下被包装为 `RemoteException`，管道中止 | 作为完整诊断文本保留 |
| 真实 native exit | `$LASTEXITCODE` 尚未读取即进入 catch，`capturedExit=null` | 0/1/2/17 原样返回 |
| stdout | 截断或与异常混排 | 完整捕获 |
| 真实 PowerShell 异常 | 可能被一并吞入 stderr 语义 | 继续 fail-closed（返回 null/非成功） |
| 未执行命令/不确定 exit | 可能复用陈旧 `LASTEXITCODE` | fail-closed，绝不默认 0 |
| 父会话状态 | `ErrorActionPreference`/`LASTEXITCODE` 未恢复 | 局部修改并在 finally 还原 |

### 3.3 D-CAPTURE-01-SCOPE（同一 MAJOR 的 scope 遗留；第 2 轮）

```text
[ID] D-CAPTURE-01-SCOPE
严重级别：MAJOR
位置：tools/verify-v5.ps1 :: Invoke-Captured（约 line 200 保存 global、
      216 清空 global、239 未限定作用域读取、finally 恢复）；
      tools/ci/test-v5-captured-exit.ps1 :: Add-CaptureCase 只污染 global
```

**复现**（caller 只读源码/独占证据探针；AST 提取真实生产 `Invoke-Captured`）：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File artifacts/caller-capture-review-e98ed1e9443e43ab9f57b8194a98e3df/probe.ps1 `
  -EvidenceDir artifacts/caller-capture-review-e98ed1e9443e43ab9f57b8194a98e3df
```

探针在普通调用函数 `Test-LocalExit` 中设 `$local:LASTEXITCODE=0`，然后调用：

1. `Invoke-Captured -Command { 'no native command' }`
2. `Invoke-Captured -Command { & $exe -NoProfile -NonInteractive -Command 'exit 17' }`

**实际（修前）**：两例 `Actual` 均为 `0`；第一例日志为
`no native command` + `[verify-v5] native exit code: 0`；第二例真实 native
exit 17，日志/返回值仍是 `native exit code: 0`。探针脚本自身 exit 0 只表示
探针正常执行，绝不代表被测通过。

**期望**：第一例必须 `null`/failure；第二例必须准确返回 `17`；调用方
local sentinel 与 script/global 原状态均应保持。

**证据（原始目录只读，未覆盖）**：

- `artifacts/caller-capture-review-e98ed1e9443e43ab9f57b8194a98e3df/probe.ps1`
  SHA256 `CE32D23A31EFCABA0CB34616F77B3551D5959E308F1AAD3006F8517FFAF6E9CA`
- `local-scope-results.json`
  SHA256 `E70C65F067BE02F66DC9CBB8DA5FC731B0B19BD8F10A9D7CAC65799313F8758E`
- 修前重放与修后重放使用新的独占目录，见 5.4。

**原因**：第 1 轮只保存/清空 `$global:LASTEXITCODE`，读取时使用未限定作用域的
`$LASTEXITCODE`。调用函数的 `$local:LASTEXITCODE` 在作用域链上更近，遮蔽了
script/global 的真实 native 值；没有 native 命令时也把 local 陈旧值当成成功。

**修复**：

- 同时保存/清空/恢复 `$script:LASTEXITCODE` 与 `$global:LASTEXITCODE`。
- 读取只使用 `$script:`/`$global:`，绝不使用未限定名；两者都非空但冲突时
  视为 uncertain，返回 null（fail-closed）。
- 不修改调用方 local sentinel；`finally` 恢复 script/global 原值。
- 保留第 1 轮的 native stderr 完整捕获、真实 PowerShell 异常 fail-closed、
  EAP/可用 native preference 局部还原。

**验收**：真实生产函数矩阵扩展到 44 例（16 全局 + 14 local + 14 script），
覆盖 local sentinel 0/4242、script sentinel 0/4242；每种 sentinel 均覆盖
no-native → null、native exit 0/1/2/17 且双流、throw → null、不存在可执行
文件 → null；逐例检查 local/script/global/unqualified sentinel 与 EAP/可用
native preference 保持。修前 35/44（9 failed，exit 1），修后 44/44
（0 failed，exit 0）。caller 探针重放修后为 `(null, 17)`。详见 5.4。

### 3.4 D-CAPTURE-01-RESTORE-EXISTENCE（第 3 轮 MINOR）

```text
[ID] D-CAPTURE-01-RESTORE-EXISTENCE
严重级别：MINOR
位置：tools/verify-v5.ps1 :: Invoke-Captured 约 206-207 保存 script/global 值、
      finally 末尾直接赋回；tools/ci/test-v5-captured-exit.ps1 原 44 例均预先
      建立 LASTEXITCODE，未断言“调用前不存在”的场景
```

**本轮新信息**：第 2 轮新增 script 作用域保存/清空/恢复路径；复核该新 finally
路径发现只保存值、未保存存在性。第 2 轮已明确要求“检查原变量存在性/值与局部
偏好还原，不污染父会话”，本次由新增路径的补充探针发现该遗漏；不是新增领域
范围或按批次依赖返修。

**复现**（cwd 当前工作树；原始目录只读）：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File artifacts/caller-capture-r2-1793248746625/probe-existence.ps1 `
  -EvidenceDir artifacts/caller-capture-r2-1793248746625
```

探针 AST 提取真实生产 `Invoke-Captured`，清除 script/global
`LASTEXITCODE` 后分别跑 no-native、native exit 0/1/2/17（stdout+stderr）、
throw、missing exe。

**实际（修前）**：退出码全部准确，但 **7/7 存在性断言失败，探针 exit 1**；
每例 `BeforeScript=false`、`BeforeGlobal=false`，调用后
`AfterScript=true`、`AfterGlobal=true`。函数在 finally 留下值为 null 的新变量。

**期望**：调用前不存在的变量，调用后仍不存在；原本存在的变量值保持；
caller-local、EAP 与可用 native preference 保持；原 native stderr 与异常
fail-closed 行为继续通过。

**证据（原始目录只读，未覆盖）**：

- `artifacts/caller-capture-r2-1793248746625/probe-existence.ps1`
  SHA256 `FBEECE04D1E48306E37CC8C0DFF318FDB987C71CC6F5DDC92FFE7C1F2A6986F8`
- `existence-results.json`
  SHA256 `F6C277F9B575EFBEF4C2582030A8A0DB618276157C38F02727430CBD7D9FC5F6`

**修复**：

- 调用前用 `Get-Variable -Scope Script/Global` 精确保存“存在性 + 值”，不再
  只读取未限定的当前值。
- 清空改为 `Remove-Variable -Scope Script/Global`，不再赋 `$null`；这样
  原本不存在的变量不会被本次调用创建成 null 变量。
- finally 按保存的存在性恢复：原本存在则赋回原值；原本不存在则移除本次
  引入的变量。script/global 在普通 `.ps1` 中可能别名同一作用域，移除/恢复
  操作对两者都执行且幂等，不会删除祖先 local 变量。
- 保留第 1/2 轮的 stderr 捕获、真实异常 fail-closed、显式作用域读取、
  EAP/可用 native preference 局部还原。

**验收**：矩阵扩展为 **51 例 = 16 global + 14 local + 14 script + 7
existence**。7 个 existence 例全部从 script/global 变量缺失开始，覆盖
no-native、native 0/1/2/17、throw、missing；断言调用前后
`BeforeScript/BeforeGlobal=false`、`AfterScript/AfterGlobal=false`，并保留
44 例 sentinel 覆盖。修前 44/51（7 failed，exit 1），修后 51/51
（0 failed，exit 0）。caller 探针从 7/7 失败变为 0/7 失败、exit 0。
详见 5.5。

---

## 4. 修复内容

修复只在 `Invoke-Captured` 内：

1. 调用前保存 `$ErrorActionPreference`、`$script:LASTEXITCODE` 与 `$global:LASTEXITCODE` 的**存在性和值**，以及当前 host 可见的 `PSNativeCommandUseErrorActionPreference`。
2. 仅在该次调用内把 `$ErrorActionPreference` 设为 `Continue`，使 native stderr 以 `ErrorRecord` 到达管道而不是终止管道。
3. 对 `FullyQualifiedErrorId` 为 `NativeCommandError*` 的记录，取 `$_.Exception.Message` 作为原始 stderr 行写入日志；不再保留 PowerShell 的 `program : ...`、`CategoryInfo` 包装。
4. 对非 `NativeCommandError*` 的真实 PowerShell 错误记录重新 `throw`，由原 catch 记录异常并返回 null，保持 fail-closed。
5. 调用前用 `Remove-Variable -Scope Script/Global` 清空 `LASTEXITCODE`；读取只用这两个显式作用域。只有本次调用确实产生整数退出码才返回该值；无原生命令、命令未执行、退出码不确定或两作用域冲突时返回 null，并在日志中写明 uncertain。
6. `finally` 按保存的存在性恢复：原本存在则恢复 `$ErrorActionPreference`、`PSNativeCommandUseErrorActionPreference`（若存在）与 script/global `LASTEXITCODE` 值；原本不存在则移除本次引入的变量。调用方 local 值不修改。
7. 日志在原有输出之后追加 `[verify-v5] native exit code: N` 诊断行；不参与任何测试结果判定。

PowerShell 5.1 实测版本：`5.1.19041.6456`。本机未安装 `pwsh`，因此 **pwsh 实际运行未执行**；代码在存在 `PSNativeCommandUseErrorActionPreference` 时局部禁用并还原，避免 pwsh 7.3+ 把 native stderr 提升为终止错误。

---

## 5. 验收矩阵（真实子进程）

测试脚本：`tools/ci/test-v5-captured-exit.ps1`。
它用 PowerShell AST 从 `tools/verify-v5.ps1` 提取**真实生产 `Invoke-Captured`**，不镜像其实现，然后用真实 PowerShell 子进程逐例调用。

### 5.1 先保存修前失败

对冻结 blob（`e3ddd83:tools/verify-v5.ps1`，本树 `pre-fix-verify-v5.ps1`，git blob 校验一致）运行同一 16 例矩阵：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-captured-exit.ps1 `
  -EvidenceDir artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/pre-fix-matrix-final `
  -VerifyScriptPath artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/pre-fix-verify-v5.ps1
```

结果：**4/16 passed，12 failed，exit 1**。
`results.json` SHA256：`E7AABDCE7A688551FEBF7490CB29A0E536AA0B7679162A7B7045488680167B68`。

### 5.2 修后全矩阵

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-captured-exit.ps1 `
  -EvidenceDir artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/post-fix-matrix-final
```

结果：**16/16 passed，0 failed，exit 0**。
`results.json` SHA256：`7A2B35B4AB5B5D63366DF077B73BA818CF5ED07D4434C41A80D4B499C29D7C56`。

| # | 用例 | 断言 | 修前 | 修后 |
|---:|---|---|---|---|
| 1 | `exit0-both-streams` | exit 0；stdout+stderr 均在日志 | FAIL | PASS |
| 2 | `exit1-both-streams` | exit 1；stdout+stderr 均在日志 | FAIL | PASS |
| 3 | `exit2-both-streams` | exit 2；stdout+stderr 均在日志 | FAIL | PASS |
| 4 | `exit17-both-streams` | exit 17；stdout+stderr 均在日志 | FAIL | PASS |
| 5 | `stderr-only-exit17` | exit 17；仅 stderr 也保留 | FAIL | PASS |
| 6 | `no-output-exit0` | exit 0；无输出不伪造异常 | FAIL（未恢复 LASTEXITCODE） | PASS |
| 7 | `spaces-path-and-args` | 含空格路径/参数/Unicode 参数 | FAIL | PASS |
| 8 | `unicode-both-streams` | Unicode stdout+stderr | FAIL | PASS |
| 9 | `bulk-dual-streams-no-deadlock` | 1200 行×256B 双流交错，无死锁、首尾完整 | FAIL（截断） | PASS |
| 10 | `powershell-throw-fails-closed` | 真实 throw → null | PASS | PASS |
| 11 | `missing-executable-fails-closed` | 不存在可执行文件 → null | PASS | PASS |
| 12 | `powershell-write-error-fails-closed` | 非终止 PowerShell error → null | PASS | PASS |
| 13 | `native-stderr-then-powershell-error-fails-closed` | 先 native stderr 后 PowerShell error → null，不误判成功 | FAIL | PASS |
| 14 | `no-native-command-fails-closed` | 未执行原生命令 → null，不复用污染值 | FAIL（返回 9876） | PASS |
| 15 | `polluted-lastexitcode-throw-fails-closed` | 污染 4242 后 throw → null | PASS | PASS |
| 16 | `real-exit-overrides-polluted-lastexitcode` | 污染 4242 后真实 exit 17 → 17 | FAIL | PASS |

矩阵还逐例检查：调用后 `$ErrorActionPreference` 与调用方 `LASTEXITCODE` 恢复；成功的 native 用例不得出现 `invocation threw an exception`。

### 5.3 caller 原始探针修后重放

在独立副本 `caller-repro-after-fix/` 重放同一场景（原目录未改动）：

- `capturedExit=0`
- `nativeExit=0`
- `nativeStdout="stdout marker"`
- `nativeStderr="expected refusal diagnostic"`
- 生产日志同时含 stdout marker、stderr 原文、`[verify-v5] native exit code: 0`，无 `invocation threw`
- `fixed=true`，exit 0
- `stderr-probe-after-fix.json` SHA256：`5E25AA219CF8B8BC6733940409C2DC148F1D564172CEF1FC2034D3C1EF10F3F4`

### 5.4 第 2 轮 scope 矩阵（44 例）与 caller 重放

同一测试脚本已扩展并继续通过 AST 提取真实生产 `Invoke-Captured`。
矩阵构成为 **16 个全局 sentinel 例 + 14 个 local sentinel 例 + 14 个 script
sentinel 例 = 44 例**；local 覆盖 sentinel `0` 与 `4242`，script 覆盖
`0` 与 `4242`，每种均覆盖 no-native、native exit 0/1/2/17（stdout+stderr）、
真实 throw、不存在可执行文件。每例还检查调用前后 local/script/global/
unqualified sentinel、`$ErrorActionPreference` 与可用
`PSNativeCommandUseErrorActionPreference` 的保持。

修前（`3b8af95:tools/verify-v5.ps1` 冻结 blob，git blob 校验一致）：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-captured-exit.ps1 `
  -EvidenceDir artifacts/d-capture-01-scope-d97531049e6940f5a56ff8f5514af540/pre-fix-scope-matrix `
  -VerifyScriptPath artifacts/d-capture-01-scope-d97531049e6940f5a56ff8f5514af540/pre-fix-scope-verify-v5.ps1
```

结果：**35/44 passed，9 failed，exit 1**。失败集中在 local sentinel：
`local0-no-native`（错误返回 0）、`local0-exit1/2/17`（均错误返回 0）、
`local4242-no-native`（错误返回 4242）、`local4242-exit0/1/2/17`（均错误返回
4242）。`results.json` SHA256：
`2585A49E57D20AE24E912A0FE3032B1625BAB63EEC8F0D352F51D0918A0EDBC4`。

修后：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-captured-exit.ps1 `
  -EvidenceDir artifacts/d-capture-01-scope-d97531049e6940f5a56ff8f5514af540/post-fix-scope-matrix
```

结果：**44/44 passed，0 failed，exit 0**。`results.json` SHA256：
`ED0003A4D7AF0C0BB786B9AD08C3E9D35B751041796AE6C0987191CF2018EB73`。

caller 探针在**新的独占目录**重放（原始失败目录未改动）：

| 运行 | `local-stale-no-native` | `local-stale-exit17` | LocalExit |
|---|---|---|---|
| 修前 `pre-fix-caller-probe/` | `0`（错） | `0`（错） | `0` |
| 修后 `post-fix-caller-probe/` | `null`（对） | `17`（对） | `0`（保持） |

修后 `local-no-native.log` 含 no-native 输出与 uncertain 说明，不含
`native exit code: 0`；`local-exit17.log` 含 `native exit code: 17`。
修后 `post-fix-caller-probe/local-scope-results.json` SHA256：
`910C86654279CAC0EE1E89B40045E4A2806ACA9049F3E2347851596EC6EDB360`。

### 5.5 第 3 轮存在性矩阵（51 例）与 caller 重放

同一测试脚本继续用 AST 提取真实生产 `Invoke-Captured`。矩阵构成为
**16 global + 14 local + 14 script + 7 existence = 51 例**；44 例 sentinel
覆盖保持不变，新增 7 例全部从 script/global `LASTEXITCODE` **不存在**开始：

| # | existence 用例 | 期望 |
|---:|---|---|
| 1 | `no-native` | exit null；调用前后 script/global 均不存在 |
| 2 | `native0` | exit 0；stdout+stderr；前后均不存在 |
| 3 | `native1` | exit 1；stdout+stderr；前后均不存在 |
| 4 | `native2` | exit 2；stdout+stderr；前后均不存在 |
| 5 | `native17` | exit 17；stdout+stderr；前后均不存在 |
| 6 | `throw` | exit null；前后均不存在 |
| 7 | `missing` | exit null；前后均不存在 |

每例同时检查 EAP 与可用 `PSNativeCommandUseErrorActionPreference` 保持。

修前（`6f0a317:tools/verify-v5.ps1` 冻结 blob，git blob 校验一致；
SHA256 `7184EBBF761D8E382F28697A4A8E1A75F2F1F0CF668070917795B81C6BAA48E3`）：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-captured-exit.ps1 `
  -EvidenceDir artifacts/d-capture-01-existence-8f540e41bb164f7581f6a00816bf0767/pre-fix-existence-matrix `
  -VerifyScriptPath artifacts/d-capture-01-existence-8f540e41bb164f7581f6a00816bf0767/pre-fix-existence-verify-v5.ps1
```

结果：**44/51 passed，7 failed，exit 1**；7 个失败全部是存在性断言
（`AfterScript=true`、`AfterGlobal=true`），退出码本身均正确。`results.json`
SHA256：`219BE4E370F38FFB36A75FA8E0F0C2FE1F696AD0F6E335D38FADF4C182B643CA`。

修后：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-captured-exit.ps1 `
  -EvidenceDir artifacts/d-capture-01-existence-8f540e41bb164f7581f6a00816bf0767/post-fix-existence-matrix
```

结果：**51/51 passed，0 failed，exit 0**；7 个 existence 例均为
`BeforeScript/BeforeGlobal=false`、`AfterScript/AfterGlobal=false`。
`results.json` SHA256：
`FA94142218BBEB010C8877BD03C3D7FBD1CCEAD72819500317A8673C34AF6138`。

caller 探针在新独占目录重放（原始失败目录未改动）：

| 运行 | 失败数 | 探针 exit | 说明 |
|---|---:|---:|---|
| 修前 `pre-fix-caller-probe/` | 7/7 | 1 | 退出码正确，但变量被留下 |
| 修后 `post-fix-caller-probe/` | 0/7 | 0 | 变量缺失状态保持 |

修后 `post-fix-caller-probe/existence-results.json` SHA256：
`ED8E2C6705CBACCD5B8B702B15C886540F2E1AC616128FC04D5292E81D713286`。

---

## 6. 现有工具测试与真实 runner 验证

### 6.1 现有负例/工具测试

| 命令 | 结果 |
|---|---|
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-native-exit.ps1` | 6/6 行为符合，exit 0 |
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-direct-results.ps1` | 19/19 行为符合（负例仍红），exit 0 |

两份证据目录已复制到 `artifacts/d-capture-01-.../existing-tests/`。

### 6.2 fresh 小型真实 direct runner

项目：`dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj`。
先删除该测试项目 `bin/obj`（ignored），再用 `D:/Unity/dotnet/dotnet.exe` 重新构建；不拿旧 DLL。

| 项 | 实测 |
|---|---|
| build | exit 0；warning 0；error 0 |
| DLL | `bin/Release/net8.0/Mahjong.Architecture.Tests.dll` |
| DLL 新鲜 | true（LastWriteTime 晚于本次 build 起点） |
| DLL SHA256 | `2116B7427B567B196463FDB73AA40F4062619364FD8CB5921E0BC8E3AACD3B57` |
| direct native exit | 0 |
| selected/passed/failed/skipped/errors/notrun | 130 / 130 / 0 / 0 / 0 / 0 |
| result XML | 130 个 test 节点；`Test-DirectInvocationEvidence.Ok=true` |
| XML SHA256 | `5C7D7FB3F97AF91E886778A9D1DAEB9C9589A15533AF39C91ED37A76DD93303E` |
| 结论 | XML、console、native exit 三者一致 |

命令：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/direct-run-probe.ps1 `
  -RepoRoot D:\majiang-wt\native-capture-ds-20261029-1145 `
  -EvidenceDir artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d `
  -DotnetPath D:\Unity\dotnet\dotnet.exe
```

### 6.3 真实 `dotnet test` 调用边界

同一项目，经生产 `Invoke-Captured`：

- native exit 0
- `Passed! - Failed: 0, Passed: 130, Skipped: 0, Total: 130`
- `buildErrors=0`
- 结果：PASS

### 6.4 小型真实 unified verifier 集成

用 isolated 的 tiny solution + tiny manifest 跑生产统一入口（不接 PG/WSS，不碰集群）：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/verify-v5.ps1 `
  -ManifestPath artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/unified-tiny/tiny-manifest.json `
  -LogDir artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/unified-tiny/logs `
  -RunnerMode direct -NoProjectDiscovery -SkipPluginCopyCheck `
  -DotnetPath D:\Unity\dotnet\dotnet.exe
```

结果：`OVERALL: PASS`，exit 0。

- project rows：1 PASS / 0 FAIL / 0 BLOCKED / 0 NOT_RUN
- `Mahjong.Architecture.Tests`：direct 130/130，0 failed，0 skipped
- direct XML gate：PASS（130/130，XML 与 console、native exit 一致）
- companion gate：PASS 4/4
- plugin copy：NOT_RUN（本 isolated 运行显式 `-SkipPluginCopyCheck`；不是 PASS）

### 6.5 第 2 轮 scope 修复后的复跑

为避免只依赖第 1 轮证据，scope 修复后重新执行了全部工具/runner 验证：

| 验证 | 第 2 轮结果 |
|---|---|
| `test-v5-captured-exit.ps1` | 44/44，exit 0（见 5.4） |
| `test-v5-native-exit.ps1` | 6/6，exit 0 |
| `test-v5-direct-results.ps1` | 19/19，负例仍红，exit 0 |
| fresh direct build | exit 0；warning 0；error 0 |
| fresh direct DLL | SHA256 `DD69DAE437C9629CF534B5C28CD89CFCC5C1D86794556020C8C0EE5FBE5CAF9C`，fresh=true |
| direct runner | native exit 0；selected/passed/failed/skipped/errors/notrun = 130/130/0/0/0/0 |
| direct XML | 130 节点，gate PASS，SHA256 `6A152B9A3B5D6B1D03B4D21369477D4AD7941F0896FF728CB7AA6A06711832B9` |
| real `dotnet test` | exit 0；`Failed: 0, Passed: 130, Skipped: 0, Total: 130` |
| isolated tiny unified | `OVERALL: PASS`，130/130，direct XML gate PASS，companion 4/4，plugin NOT_RUN，exit 0 |
| tiny unified direct XML | SHA256 `535102AC0954BF828F8D98F1F8FD92950B9C696DB3AF1F6262E7229AC7974B11` |

第 2 轮证据根：`artifacts/d-capture-01-scope-d97531049e6940f5a56ff8f5514af540/`
（含 `pre-fix-scope-verify-v5.ps1`、`pre-fix-scope-matrix/`、
`post-fix-scope-matrix/`、`pre-fix-caller-probe/`、
`post-fix-caller-probe/`、`existing-tests/`、`direct-run/`、`dotnet-test/`、
`unified-tiny/`、`SHA256-MANIFEST.txt`）。

### 6.6 第 3 轮 existence 修复后的复跑

| 验证 | 第 3 轮结果 |
|---|---|
| `test-v5-captured-exit.ps1` | 51/51，exit 0（16 global + 14 local + 14 script + 7 existence） |
| `test-v5-native-exit.ps1` | 6/6，exit 0 |
| `test-v5-direct-results.ps1` | 19/19，负例仍红，exit 0 |
| fresh direct build | exit 0；warning 0；error 0 |
| fresh direct DLL | SHA256 `30FC29F581E0A413A3CC17E6DDA26ABF8E76CD670953030B7AF2E570A7E155D4`，fresh=true |
| direct runner | native exit 0；selected/passed/failed/skipped/errors/notrun = 130/130/0/0/0/0 |
| direct XML | 130 节点，gate PASS，SHA256 `83ABD0E78DE72C8166ACFD292E8D4C97C9A437E45E7F593018F772FB5DC15D6B` |
| real `dotnet test` | exit 0；`Failed: 0, Passed: 130, Skipped: 0, Total: 130` |
| isolated tiny unified | `OVERALL: PASS`，130/130，direct XML gate PASS，companion 4/4，plugin NOT_RUN，exit 0 |
| tiny unified direct XML | SHA256 `6F38E2901A091FA661C76F81A3ECE91780BF65236C7A04E616F1E77D1CC4F09D` |

第 3 轮证据根：`artifacts/d-capture-01-existence-8f540e41bb164f7581f6a00816bf0767/`
（含 `pre-fix-existence-verify-v5.ps1`、`pre-fix-existence-matrix/`、
`post-fix-existence-matrix/`、`pre-fix-caller-probe/`、
`post-fix-caller-probe/`、`existing-tests/`、`direct-run/`、`dotnet-test/`、
`unified-tiny/`、`SHA256-MANIFEST.txt`）。

---

## 7. 历史失败、本轮验证与未跑项

### 7.1 历史失败（只读，未改写）

- 原首次真实批次：统一 verifier 17 PASS/1 FAIL、wrapper 1；真实 `DurableHost` 的预期 `RECOVERY_LEASE_HELD` stderr 被 `RemoteException` 截断，XML 未收齐。
- 其余 12 份 XML 为 2490/2490 全绿，但不能代表整批。
- 原 FAIL 记录、原始 XML、caller-c492edcccf66411a8de373238589dda7 等历史材料均未修改。
- 本任务不把缺 XML / unknown exit / 历史 FAIL 改成 PASS。

### 7.2 本轮已验证

- 捕获边界：第 1 轮 16/16；第 2 轮扩展为 44/44；第 3 轮扩展为 51/51，
  真实子进程矩阵通过。
- caller 探针：第 1 轮 stderr 场景修后 `capturedExit=0`；第 2 轮 local-scope
  场景修后 `(null, 17)`；第 3 轮 existence 场景由 7/7 失败变为 0/7 失败、
  exit 0。
- 现有 native-exit 与 direct-results 工具测试：通过，负例仍红。
- fresh 小型 build / dotnet test / direct runner / tiny unified：第 3 轮复跑通过。

### 7.3 未跑 / 明确缺口

- **完整 unified / PG / WSS 批次：NOT_RUN**。由 caller 在最终汇合版本执行；本任务不宣称该整批通过。
- **Android：FAIL**（沿用历史结论）；本任务未重跑。
- **iOS：BLOCKED**（无 macOS/Xcode/iOS 工具链）；本任务未重跑。
- **真机 / 跨物理网络：NOT_RUN**。
- **G2/G3：未批准、未宣称通过**。
- **G1 之外 181 classified 未覆盖项：未变化**。
- `pwsh`：本机未安装，未实跑；仅有 5.1 实测 + 代码兼容保护。
- plugin copy：tiny unified 中显式跳过，未单独重跑。

### 7.4 远端 CI

PR20 的两条 CI：

- `40805673521`
- `40805666808`

均因支付/额度问题在启动前 BLOCKED；本轮未盲重试、未推 CI ref、未修改远端。

---

## 8. 文件、提交与证据

### 8.1 变更文件

| 文件 | 变更 |
|---|---|
| `tools/verify-v5.ps1` | `Invoke-Captured` 捕获边界 + script/global 作用域 + 存在性还原 |
| `tools/ci/test-v5-captured-exit.ps1` | 真实子进程矩阵，第 3 轮扩展到 51 例（44 sentinel + 7 existence） |
| `doc/v5-重构/V5-NATIVE-CAPTURE-DELIVERY-20261029.md` | 本交付文档 |

### 8.2 提交

| 提交 | 内容 |
|---|---|
| `36d332592cfcd2879d786d01137297ec97b3f1` | 第 1 轮 `fix(v5): capture native stderr without losing exit codes` |
| `3ceef6b8a4056e5a1de01dea64328304f77f7b14` | 第 2 轮 `fix(v5): prevent caller-local LASTEXITCODE shadowing`（scope 修复 + 44 例矩阵） |
| `3a39ab8a9ce3953e5e047ec8071557b66c0d5873` | 第 3 轮 `fix(v5): preserve LASTEXITCODE variable existence`（存在性还原 + 51 例矩阵） |
| 本文件提交 | 交付文档更新；提交后最终 HEAD 以 `git rev-parse HEAD` 与最终回复为准 |

### 8.3 证据目录

第 1 轮证据根：

```text
artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/
```

第 2 轮证据根：

```text
artifacts/d-capture-01-scope-d97531049e6940f5a56ff8f5514af540/
```

第 3 轮证据根：

```text
artifacts/d-capture-01-existence-8f540e41bb164f7581f6a00816bf0767/
```

第 3 轮根包含：

- `pre-fix-existence-verify-v5.ps1`：`6f0a317:tools/verify-v5.ps1` 冻结副本，
  git blob 校验一致；SHA256
  `7184EBBF761D8E382F28697A4A8E1A75F2F1F0CF668070917795B81C6BAA48E3`
- `pre-fix-existence-matrix/`：修前 51 例结果（44/51，7 failed，exit 1）
- `post-fix-existence-matrix/`：修后 51 例结果（51/51，0 failed，exit 0）
- `pre-fix-caller-probe/`、`post-fix-caller-probe/`：caller existence 重放
- `existing-tests/`、`direct-run/`、`dotnet-test/`、`unified-tiny/`
- `SHA256-MANIFEST.txt`（全部证据文件清单）

关键 SHA256：

| 对象 | SHA256 |
|---|---|
| 当前 `tools/verify-v5.ps1` | `363A398D9CEFCA1C7F2BA5AC5DD2B7989BD6EDE49812C6E1E9BA487CAD7EDE66` |
| 当前 `tools/ci/test-v5-captured-exit.ps1` | `23E30A125D683BCBDAFF4B33ADE5B3FD14E2A3420965F8B253981F25C5A1FA26` |
| 修前 51 例 `results.json` | `219BE4E370F38FFB36A75FA8E0F0C2FE1F696AD0F6E335D38FADF4C182B643CA` |
| 修后 51 例 `results.json` | `FA94142218BBEB010C8877BD03C3D7FBD1CCEAD72819500317A8673C34AF6138` |
| 修后 caller existence JSON | `ED8E2C6705CBACCD5B8B702B15C886540F2E1AC616128FC04D5292E81D713286` |
| 第 3 轮 direct XML | `83ABD0E78DE72C8166ACFD292E8D4C97C9A437E45E7F593018F772FB5DC15D6B` |
| 第 3 轮 direct DLL | `30FC29F581E0A413A3CC17E6DDA26ABF8E76CD670953030B7AF2E570A7E155D4` |
| 第 3 轮 tiny unified XML | `6F38E2901A091FA661C76F81A3ECE91780BF65236C7A04E616F1E77D1CC4F09D` |
| 原 caller existence `probe-existence.ps1` | `FBEECE04D1E48306E37CC8C0DFF318FDB987C71CC6F5DDC92FFE7C1F2A6986F8` |
| 原 caller existence `existence-results.json` | `F6C277F9B575EFBEF4C2582030A8A0DB618276157C38F02727430CBD7D9FC5F6` |
| 原 caller local-scope `probe.ps1` | `CE32D23A31EFCABA0CB34616F77B3551D5959E308F1AAD3006F8517FFAF6E9CA` |
| 原 caller stderr `stderr-probe.json` | `CC934DCF7A06E5CD2990ADC9CCF3566230DD115159577BCA3173EC6F09A11429` |

### 8.4 冻结/最终 SHA 与净 diff

- 冻结基线：`e3ddd83fec0bc2592bb490594e5452b672bbb8f7`
- 第 1 轮实现提交：`36d332592cfcd2879d786d01137297ec97b3f1`
- 第 2 轮实现提交：`3ceef6b8a4056e5a1de01dea64328304f77f7b14`
- 第 3 轮实现提交：`3a39ab8a9ce3953e5e047ec8071557b66c0d5873`
- 第 3 轮实现提交净变更：2 files changed，175 insertions(+)，12 deletions(-)
- 交付文档提交后的最终 HEAD、`git status`、`git diff --check e3ddd83..HEAD` 与净 diff，以最终回复中的实测为准。

---

## 9. 明确未做的事

- 没有修改任何测试项目、业务代码、fixture、manifest、pin 或 scheduler。
- 没有放宽 XML/console/selected-count/skip 判定。
- 没有用 skip、空测试、旧 DLL、旧产物或刷新摘要获得绿色。
- 没有把 `stderr` 丢弃、全局 `Ignore`、或给 null 默认 0。
- 没有只匹配/过滤 `RECOVERY_LEASE_HELD` 特例。
- 没有只修 caller 探针、只对 exit 17 做特例，或用 global-only sentinel 代替
  local/script 覆盖。
- 没有把“值为 null 的现有变量”当作“变量不存在”，也没有用赋 null 代替移除。
- 没有 push、PR/MR、合并到 `v5`、部署或创建 `complete.flag`。
- 没有接管另一 caller 的 PG/WSS 集群或进程。
