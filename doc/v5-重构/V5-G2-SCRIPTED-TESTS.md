# G2 脚本化用例入口

日期：2026-11-07。开发分支 `task/g2-scripted-test-plan-20261107`，工作树 `D:/majiang-wt/g2-scripted-tests-20261107`，实际起点为本地 `g2_base` 的 `66f31612f23baf9e00e8eb242d0259c5ec2c9d9e`。本次新增统一入口、工具回归和本文，不改变游戏、既有执行器、历史证据或验收状态。

## 生成完整清单

在该任务工作树打开 PowerShell：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1
```

默认只生成计划，不启动 Unity、构建、ADB 或设备。在新的 `build/g<随机ID>/` 中输出：

- `plan.json`：当前源码 HEAD/工作树状态、原用例表和执行器 SHA256、33 个 C04 场景、Android 8 行矩阵、执行器接口及可选参数。
- `cases.csv`：41 行便于人工检查的台账。每行保留固定编号和关联执行套件，初始执行状态为 `NOT_RUN`，判定为 `NOT_ASSESSED`。

33 项 C04 的触发、初态、断言和绑定直接读取既有 [场景 JSON](V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json)，不复制或修改历史状态；Android 按 [C05 固定八行矩阵](V5-G2-C05-ANDROID-EMULATOR-TASK-20261103.md) 列出。41 行是“33 项场景 + 8 行平台合同”，不是两个平台各有 41 个等价自动用例。

## 执行现有自动化

真实长验证前，先按 [长验证模板](../LONG_VALIDATION_TASK_TEMPLATE.md) 填写范围、阶段、进入条件、空间预算、超时和证据要求。先完成廉价合同检查，再冻结干净候选执行。保留原任务所有权和在途验证，不同时启动重复任务。

```powershell
# 先检查新入口自身；不运行游戏。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/G2TestRunner.Tests.ps1

# 真实四动作验证：自行填写实际冻结的 HEAD，不能使用本文起始 SHA 代替新候选。
$candidate = (git rev-parse HEAD).Trim()
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1 `
  -Mode Run -Suite WindowsActions -ExpectedSourceHead $candidate
```

统一入口默认显式传入 Unity `D:/game_dev_tools/unity/Editor/Unity.exe` 和 .NET `D:/game_dev_tools/dotnet/dotnet.exe`，可用同名参数覆盖。每次只执行一个指定套件，子执行器独占新 `build/<GUID>/`，沿用其原始共享锁、超时、身份与包校验；统一入口不再持一层 Unity 锁。

| Suite | 实际执行 | 覆盖范围与限制 |
|---|---|---|
| WindowsFullMatch | `run-p45-full-match.ps1 -Mode All` | 真实整场证据；和牌/流局等分支仍须逐项检查 |
| WindowsActions | `run-p45-c04-player-actions.ps1` | 四动作执行、事件/回执验证 |
| WindowsCheats | `run-p45-c04-player-cheats.ps1 -Case all` | 授权与负例、费用/136 张验证；需真实新 verifier |
| WindowsRecovery | `run-p45-c04-recovery-reconciliation.ps1 -Mode All` | 八种恢复、原请求对账 |
| WindowsPrivacy | `run-p45-c04-privacy-matrix.ps1` | 七案 PlayMode |
| WindowsAppMatrix | 同上 `-WindowsAppMatrix` | 普通 Windows 包七案/恢复原件；不替代独立 PlayMode |
| AndroidProbe | `run-c05-android-emulator.ps1 -Action Probe` | 环境短预检 |
| AndroidBuild | `build-c05-android-emulator.ps1` | 完整模拟器 APK 构建 |
| AndroidBoot | 设备脚本 `StartEmulator` | 自有模拟器启动和 state 文件 |
| AndroidInstall / AndroidStart | `Install` / `Start` | 同包安装、启动；启动指令成功不证明游戏可用 |
| AndroidCapture | `Capture` | 原始截图，画面语义待复核 |
| AndroidInput | `Input` | tap/swipe/back 与前后原件；合法动作效果待复核 |
| AndroidLifecycle | `BackgroundResume` | 后台恢复原件；不覆盖完整显示配置矩阵 |
| AndroidShutdown | `ShutdownEmulator` | 按所有权停止；完整封存/交接仍须检查 |

NuGet 种子、新 verifier、私有 Android 工具链、设备 state/APK 和输入坐标等通过 `-RunnerParametersPath` 指定 JSON 文件。仅允许 `plan.json` 所列可选参数；拒绝覆盖固定执行模式、输出目录、源码 guard，拒绝 fixture/preflight/skip 等替代真实运行的参数。参数按数据传给独立 PowerShell 子进程，不执行 JSON 中的命令字符串。

例如三绝技执行器需要显式指定已经构建好的新 verifier：

```powershell
@{ VerifierPath='D:/path/to/C04PlayerCheatVerifier2.dll' } |
  ConvertTo-Json | Set-Content -Encoding UTF8 build/g2-cheat-options.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1 `
  -Mode Run -Suite WindowsCheats -RunnerParametersPath build/g2-cheat-options.json
```

Android 构建先按环境指南核对独占短 stage、私有 CLI/Ninja 和已有缓存，不能凭统一入口默认值直接开始冷构建：

```powershell
@{
  StagingRoot='D:/c05s/填写本次独占GUID'
  PrivateToolchainRoot='填写已核验的私有工具链根目录'
} | ConvertTo-Json | Set-Content -Encoding UTF8 build/g2-android-build-options.json
```

设备操作复用有所有权记录的 state，沿用同一 APK 及其原目录中的完整构建证据。不要仅移动一个 APK；原执行器会拒绝不匹配证据。示例：

```powershell
@{
  StateFile='填写已核验的StartEmulator产物state.json绝对路径'
  Apk='填写同包APK绝对路径'
  Serial='emulator-5580'
  AvdName='medium_phone'
  AndroidSdkRoot='D:/Android/Sdk'
  AvdHome='D:/Android/avd'
  InputAction='tap'
  X=100
  Y=100
} | ConvertTo-Json | Set-Content -Encoding UTF8 build/g2-input-options.json
# 坐标必须依据当前设备真实画面填写，不能将示例坐标当成合法麻将动作。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1 `
  -Mode Run -Suite AndroidInput -RunnerParametersPath build/g2-input-options.json
```

## 结果含义与未覆盖项

每次运行额外输出 `command.json`、结构化参数请求、`runner.log`、`execution.json` 和统一入口自身产物的 `manifest.json`，保留子执行器真实退出码和证据根。子执行器原件继续按其原有规则封存，不对模拟器活动日志重新算稳定摘要。入口退出 0 只表示计划生成或选定子命令完成；失败退出 1，真实子退出码另存。`RUNNER_COMPLETED` 不等于用例 PASS：逐案结果和包闭包仍在原执行器证据中。计划表不自动写 PASS，也不汇总批准 G2。

关联套件是取证入口，不保证一条合同的全部断言都被自动检查。离线整场、LOCAL 资产实际服务入口拒绝、最终画面与键鼠/触屏语义、完整显示矩阵还需专项断言或人工复核。当前没有把 Windows 全部玩法驱动移植到 Android，不能凭 Android 安装、输入或截图完成判定完整玩法通过。Android 模拟器也不覆盖 ARM64 真机。

各 Windows 套件可能构建不同应用包，入口不把它们拼成“同一最终包全绿”。最后仍需按 C06 要求核定源码/输入/8 DLL/整包/设备身份、逐项复用范围与独立签署。本次只验证新调度工具；不重跑历史产品验证，不变更 G2 的未验收状态。

## 本次验证

Windows PowerShell 5.1 实际执行 `tools/tests/G2TestRunner.Tests.ps1`，30 项工具断言通过、失败 0，原生退出 0。覆盖 41 行完整性、15 套执行器接口、旧证据保护、错误 HEAD/禁止参数拒绝、原生 stderr/退出码保留、特殊字符字面传递和入口产物哈希。进程正负例使用独占微型 Git 仓库中的工具替身，不启动 Unity/ADB。最终记录为 `build/g2-runner-test-9141365dff9f4b4982ae13e833ab1096/test-summary.json`；早期失败记录保留，不计为最终通过。
