# G2-C05 Android 模拟器优先补验交付（DSH，第 5 轮返修）

日期：2026-11-04，Asia/Shanghai。
执行者：`deepseek_harness`（配置默认模型 `deepseek/deepseek-v4.1-flash`，未传替代 model）。
专属验收者：独立 Reviewer（本报告只提交执行证据，不自行批准）。
工作树：`E:/majiang-review/g2-c05-android-validation-ds-20261103`；分支 `wt/v5-g2-c05-android-validation-ds-20261103`；HEAD `5b484774860aa9b59ed1dce26f6efa720ed44b7d`。
`generatedAtUtc`：`2026-11-04T04:30:00Z`。

> 对应 `build/c05-review-r4-20261104/review.md`（SHA256
> `D4E3DD47E65EA0575BFCFCEF8A480DBD0AB4DC4F88ECCC2F39B8353A3D8EF15F`）的唯一残留
> `C05-R1-03`（MAJOR）。本轮只改 PowerShell/tests/docs，APK 输入闭包未变
> （C# SHA256 仍 `CCF5…0D82`，源闭包仍 373 文件），保留 030443 冷构建 APK；
> 未重跑 Unity、未启动 AVD。其余三组已关闭，不重开。

---

## 0. 固定八行

| 行 | 判定 | 依据 |
|---|---|---|
| E1 环境与镜像 | **PASS** | Unity/JDK/NDK/SDK/emulator/AVD/accel 实测；D 盘 SDK/AVD 有任务卡授权。 |
| B1 完整游戏 APK | **PASS** | 保留 `build/c05-emulator-build-staged-20261104-030443`：54,031,339 B，SHA256 `1E57F07C…E2A9`；修正工具独立接受。 |
| D1 模拟器启动 | **BLOCKED_ACCEL** | 无 hypervisor/AEHD；未重试。 |
| R1/V1/I1/L1 | **NOT_RUN** | 无 booted 设备。 |
| F1 封存与交接 | **PASS** | 7 路径、源闭包、provenance、无残留进程、diffcheck 0。 |

---

## 1. C05-R1-03 修复映射

- **位置**：`run-c05-android-emulator.ps1` `Get-C05RTargetProcessState`、`Stop`；`TargetStateFixtureTest`。
- **实际（修前）**：probe 失败/超时/drain/native2 后只要独立 `echo` 成功就推断 ABSENT；PASS probe 忽略 stderr；多 PID 行被误判 UNKNOWN；Stop 只保存归一化 processState，原始 query 丢失。
- **修复**：
  1. **删除 echo 回退推断**。只有 `status=PASS`、`timedOut=false`、`drainComplete=true`、native exit=0、stderr 为空、且只有一个格式良好的 `C05_PIDOF_EXIT` marker 的完整观测才产生结果；
  2. marker 前后只允许空白或空白分隔的十进制 PID；多 PID 合法；marker 重复、缺失、尾随文本、非数字文本、code1+有 PID、code0+无 PID 一律 UNKNOWN；
  3. timeout/drain timeout/native 非零/exit null/missing marker/任意 stderr 一律 UNKNOWN；不再把 exit1 或空输出广义解释为 absence；
  4. `Stop` 结果保存 `processStateReason` 与完整 `processProbe`（command/status/exit/timedOut/drainComplete/stdout/stderr/observedUtc），失败查询原件不再丢失；
  5. 更新 fixture 为真实生产 shell 协议：ABSENT 是 transport native0 + `C05_PIDOF_EXIT:1`，不是 native1 无 marker。
- **验收**：
  - reviewer `process-probe.ps1` 7/7 匹配：valid-absent **ABSENT**、valid-running **RUNNING**、timeout/silent-native2/drain/stderr 均 **UNKNOWN**、multi-PID **RUNNING**（`D959…1C51`）；
  - reviewer `current-cli.ps1`：Start exit0；stop-absent exit0 PASS；stop-running exit1 RUNNING；stop-empty-error/stderr-error exit1 UNKNOWN；failed captures exit1（`6C96…EE14`）；
  - 原始 probe 已封存（`A444…D660`）；
  - 本任务 `run-target-state` 12 例（3 正例 + 9 负例）PASS。

---

## 2. B1 输入不变与复核

- APK：`build/c05-emulator-build-staged-20261104-030443/Mahjong-emulator-x86_64.apk`，54,031,339 B，SHA256 `1E57F07C6961886407F41C7CFAAE583ABCEE7BA8674E0CF7834581DB4272E2A9`。
- BuildReport：schema 2、`Succeeded`、`errors=stepErrors=stepExceptions=0`、六场景、8 DLL、IL2CPP x86_64、`durationSeconds=546.882`；summary elapsed 618。
- 源闭包 373 文件 before/after `77958E49…FA81`；ProjectSettings 前后 `828AEA…8EEE34`；C# `CCF5…0D82` 未变。
- 本轮只改 PS/tests/docs，APK 输入不变，故不重建；修正工具重新接受同一 APK 的证据哈希。

---

## 3. 回归

- **TESTS PASS assertions=92 fixtures=15**，`build/round5-tests/run-2e6394e3/`（`19BD…C9BE`）。
- 其他已关闭三组保持：parent0 qemu 清理、fractional/boolean/receipt 拒绝、nested junction 零写入（92/15 中继续覆盖）。
- reviewer 原件只读保留；新复现目录 `build/c05-review-repro7-25c737691b074929879806890e18e6e1/`。

---

## 4. 环境、D1 与未覆盖

- D 盘 SDK/AVD 授权：`doc/v5-重构/V5-G2-C05-ANDROID-EMULATOR-TASK-20261103.md` §“用户最新目录与接续检查点”；构建 cache/TEMP/Library/staging/APK/证据仍在本任务 E 盘 `build/`。
- `emulator -accel-check` exit 6，`HypervisorPresent=False`，无 AEHD；D1 `BLOCKED_ACCEL`，R1/V1/I1/L1 `NOT_RUN`。
- 未覆盖：真机、Windows、Editor、iOS、G2–G5。

---

## 5. 接续命令

```powershell
$root='E:\majiang-review\g2-c05-android-validation-ds-20261103'
$apk='E:\majiang-review\g2-c05-android-validation-ds-20261103\build\c05-emulator-build-staged-20261104-030443\Mahjong-emulator-x86_64.apk'
$env:ANDROID_HOME='D:\Android\Sdk'; $env:ANDROID_SDK_ROOT='D:\Android\Sdk'; $env:ANDROID_AVD_HOME='D:\Android\avd'
& 'D:\Android\Sdk\emulator\emulator.exe' -accel-check   # 必须 exit 0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action StartEmulator -AvdName medium_phone -Serial emulator-5580 -EmulatorPort 5580 -AvdWaitSeconds 600
$state='<StartEmulator 证据目录>\state.json'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action Install -Apk $apk -StateFile $state
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action Start -Apk $apk -StateFile $state
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action Capture -Apk $apk -StateFile $state
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action Input -InputAction tap -X 540 -Y 1200 -Apk $apk -StateFile $state
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action BackgroundResume -Apk $apk -StateFile $state
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\tools\unity\run-c05-android-emulator.ps1" -Action ShutdownEmulator -StateFile $state
```

`CAPTURED_REQUIRES_REVIEW`/`EXECUTED_REQUIRES_REVIEW` 由 reviewer 查看原始 PNG/视频判定；任何 probe 失败/超时/drain/stderr/格式异常保持 UNKNOWN，不得 PASS。

---

## 6. 结论

- 唯一残留 C05-R1-03 已关闭：不再从 echo 推断 absence；完整 marker 协议、stderr/multi-PID/矛盾记录拒绝；原始 probe 封存。
- 030443 APK 保留并用修正工具复核；APK 输入未变。
- D1 继续 `BLOCKED_ACCEL`；V4 生产；V5 G2–G5 未批准。

## 7. 专属 reviewer 独立验收与统一交接（2026-11-04）

本任务自身的环境核定、独立工具、完整 APK 与受阻交接范围已通过独立检视；四组返修缺陷全部关闭。
工具代码提交为 `7ee6d72b5553abae6103f55b1afccac0be125c00`。提交前后的受检五个代码文件字节相同；
APK 的构建源仍为 `5b484774…` 加记录的输入哈希，未把工具提交或后续统一 PR 头冒称为新设备实测版本。

- Reviewer 实际运行：工具回归 **92 assertions / 15 fixtures、native exit 0**；原进程探针 7/7 匹配。
- Reviewer 真实生产 runner CLI（`TOOL_FIXTURE`）：Start/合法 ABSENT 正例 exit 0；RUNNING、native2、stderr 错误、失败截图均 exit 1，分别保存准确状态；四份 Stop 原件包含完整 `processProbe`。
- Reviewer 实际复核：五个工具 SHA 与 binding 一致，373 个源码冻结文件无差异；APK、summary、BuildReport、metadata、共享 provenance 与 manifest 哈希一致。
- Reviewer 证据根：`build/c05-review-r5-20261104/`；此前四轮独立原件保留于对应 `c05-review*` 目录。
- 同批已接受并已发布内容继续汇入既有统一 Draft PR20；D05 两个在途任务仍由其原 reviewer 处理，本验收不导入其未接受改动。

本次批准不等同模拟器游戏通过：D1 仍为 `BLOCKED_ACCEL`，R1/V1/I1/L1 仍为 `NOT_RUN`。
ARM64 真机、未来含 Android 相关改动的统一候选、全 C05/C04/G2–G5 与新 PR 头远端 CI 均未获此验收认证。
PR 保持 OPEN/Draft；未合并、部署或修改调度。
