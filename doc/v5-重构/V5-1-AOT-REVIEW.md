# V5-1 P2.2 AOT 最小加载与平台边界复验报告

日期：2026-10-23  
任务：P2.2 AOT 最小加载与平台边界（依赖 P2.1）  
比较基线：`a96c195`（`feat/v5-1-integration`）  
执行工作树：`D:\majiang-wt\p22`（分支 `wt/p22`）  
本轮性质：交付范围返修 + link.xml 可选程序集修复 + 重新实测

**结论：交付范围已收拢到六个允许路径；.NET 全量 315 项测试 PASS，.NET 18 类 Wire 消息探针 PASS；`link.xml` 在“普通工程缺可选程序集”和“smoke 工程程序集齐全”两种形态下均由安装的 UnityLinker 实际解析通过（exit=0），且程序集存在时 `preserve="all"` 生效；Unity 脚本与生成的 IL2CPP helper 对安装的 Unity API 编译 PASS；Unity Editor 加载仍 BLOCKED（许可 exit=198）；Windows/Android IL2CPP build 因宿主缺少 MSVC/Windows SDK/NDK 而 BLOCKED，对应 runtime 为 NOT_RUN。独立 UnityLinker 解析不是 IL2CPP build，也不是 runtime；不得把本报告当作 Unity/IL2CPP 已通过证据。**

## 1. 交付范围返修

初次提交 `7469651` 改动了 15 个文件，超出初始 prompt 的 `Only modify the exact expectedFiles` 与六路径账本。本轮以 `a96c195` 为基准恢复/移除范围外改动，未重写历史，未触碰 `D:\majiang` 或其他任务文件，未推送、未创建 MR。

最终允许且实际存在的六个路径：

```text
tools/unity/test-wire-aot.ps1
doc/v5-重构/V5-1-AOT-REVIEW.md
unity/Mahjong.Unity/Assets/Scripts/Platform/WireAotSmoke.cs
unity/Mahjong.Unity/Assets/Scripts/Platform/WireAotSmoke.cs.meta
unity/Mahjong.Unity/Assets/link.xml
unity/Mahjong.Unity/Assets/link.xml.meta
```

已恢复到基线的范围外路径：

- `dotnet/Mahjong.sln`
- `dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj`
- `dotnet/tests/Mahjong.Contracts.Tests/WireAotSmokeTests.cs`（移除）
- `dotnet/tests/Mahjong.Contracts.Wire.AotSmoke/`（整个探针工程移除）
- `dotnet/tests/Mahjong.Wire.AotSmoke.Runner/`（整个运行器工程移除）
- `tools/unity/check-aot-toolchain.ps1`（移除，逻辑并入 `test-wire-aot.ps1`）
- `tools/unity/build-il2cpp-aot-smoke.ps1`（移除，逻辑并入 `test-wire-aot.ps1`）
- `doc/v5-重构/V5-1-P2.2-AOT-PLATFORM-REPORT.md`（移除，内容迁入本报告）

探针工程、CLI 运行器与 IL2CPP Editor helper 不再作为 tracked 文件存在。它们由 `test-wire-aot.ps1` 在每次运行时生成到：

```text
build/p22-aot/<stamp>/probe/Mahjong.Contracts.Wire.AotSmoke/
build/p22-aot/<stamp>/probe/Mahjong.Wire.AotSmoke.Runner/
build/p22-aot/<stamp>/il2cpp-helper/MahjongIl2cppSmokeBuild.cs
```

`build/` 已被仓库忽略；没有向 tracked 文件列表新增任何路径。Unity 侧只把生成程序集复制到本次新建的临时工程，仓库的 `Assets/Plugins/Mahjong` 与 `ProjectSettings` 未改动。

范围外的集成代价（明确记录为 integration gap）：探针不再作为 xunit 用例进入 `dotnet test`，因为那需要新增测试源码并修改测试 csproj；改用同一 CLI 探针作为独立入口。故 .NET 全量为基线 315 项（Contracts 82 + Domain 233），不是初次提交时的 316 项。

### 1.1 第二轮独立复验追加修复：link.xml 可选程序集

第二轮独立复验发现一项真实构建阻断：`unity/Mahjong.Unity/Assets/link.xml` 无条件 `preserve="all"` 了 `Mahjong.Contracts.Wire`、`Mahjong.Contracts.Wire.AotSmoke`、`Google.Protobuf`，但 tracked 的 `Assets/Plugins/Mahjong` 只有生产五模块 DLL，这三个程序集只存在于脚本生成的临时 smoke 工程（或真实启用 Wire 的工程）。因此即使不运行 smoke，正常 Unity 工程的 managed stripping / IL2CPP 也会在描述文件解析阶段失败。

用安装的 UnityLinker（6000.3.27f1，无许可）复现原始错误：

```text
Mono.Linker.LinkerFatalErrorException: ILLink: error IL1013:
  Error processing 'unity\Mahjong.Unity\Assets\link.xml';
  Unity.Linker.XmlResolutionException: Unable to find the file for the assembly
  named 'Mahjong.Contracts.Wire, Version=0.0.0.0, ...' specified in link file.
```

修复只改 `link.xml`：对三个可选程序集增加 Unity 支持的 `ignoreIfMissing="1"`，`Mahjong.Contracts` 仍无条件保留。程序集缺失时跳过该规则；程序集存在时仍执行 `preserve="all"`。没有提交 DLL、没有修改 `ProjectSettings`、没有把可选程序集放进仓库的正常工程，也没有扩大六路径范围。

实际解析验证见 4.4 节，结论：

- 普通工程形态（三个可选程序集不存在）：exit=0，只 Link `Mahjong.Contracts`，输出中没有三个可选 DLL，不再出现 IL1013；
- smoke 工程形态（三个可选程序集齐全）：exit=0，四个程序集均 Link，`preserve="all"` 分别保留 Wire 177 / AotSmoke 5 / Google.Protobuf 360 个类型；
- 复验者给出的最小原始命令现在仍 exit=1，但错误已变为 `mscorlib` 无法解析：该命令没有把 Unity IL2CPP 的 `build/deploy` 加入 `--search-directory`。这是宿主/搜索路径缺口，与本轮“描述文件引用了缺失程序集”的缺陷不同，也不代表 IL2CPP build 或 runtime 通过。

## 2. 环境

| 项 | 实测值 |
|---|---|
| 主机 | Windows 10 19045，`DESKTOP-U2CRNCD` |
| .NET SDK | 8.0.426（`D:\Unity\dotnet\dotnet.exe`；`dotnet` 不在 PATH） |
| Unity | 6000.3.27f1（`D:\Unity\Hub\Editor\6000.3.27f1`） |
| .NET 目标 | 共享库 `netstandard2.1` / C# 9；运行器 `net8.0` |
| Google.Protobuf | 3.28.3 |
| Android SDK | `C:\Users\sncdc\AppData\Local\Android\Sdk`（本机 SDK 无 `ndk` 目录） |
| Android JDK / adb | Android Studio JBR；`platform-tools\adb.exe` |
| Android NDK | 未安装（`ANDROID_NDK_HOME` 为空，SDK 下无 `ndk`） |
| MSVC / Windows SDK | 未安装（无 `cl.exe`、无 `Windows Kits\10`） |
| Unity 许可 | 仅 entitlement 文件；batchmode 仍返回 198 |

## 3. .NET 实测

### 3.1 全量测试

命令：

```powershell
& 'D:\Unity\dotnet\dotnet.exe' test dotnet/Mahjong.sln -c Release --nologo
```

原始结果（退出码 0）：

```text
已通过! - 失败: 0，通过: 82，已跳过: 0，总计: 82  - Mahjong.Contracts.Tests.dll
已通过! - 失败: 0，通过: 233，已跳过: 0，总计: 233 - Mahjong.Domain.Tests.dll
```

合计 **315 通过、0 失败、0 跳过**。该数字是恢复范围后的基线；`7469651` 中的第 316 项来自被移除的范围外 xunit 文件。

### 3.2 AOT 探针

命令（脚本内部等价于对生成工程执行 `dotnet run`）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\unity\test-wire-aot.ps1 -Configuration Release
```

原始输出（退出码 0）：

```text
PASS codec=netstandard2.1 messages=18 bytes=1417 unknownFields=1 legacyV4=rejected assembly=Mahjong.Contracts.Wire
```

覆盖 18 类消息：HELLO、WELCOME、COMMAND、RECEIPT、PLAYER_SNAPSHOT、PLAYER_SNAPSHOT_QUERY、PUBLIC_SNAPSHOT、PUBLIC_SNAPSHOT_QUERY、ROOM_UPDATE、SYNC_REQUEST、SYNC_RESPONSE、RECEIPT_QUERY、RECEIPT_LOOKUP、HEARTBEAT、ERROR、CLOSE、PLAYER_UPDATE、PUBLIC_UPDATE。每类执行 `Encode → Decode → 校验 messageType/bodyCase → 再 Encode 字节相等`；另验证未知字段计数与 V4 `MJTC` 魔数拒绝。

该结果只证明 .NET 8 运行时、生成代码与 `WireCodec` 路径可用；不证明 Unity Editor、IL2CPP、Android 或 iOS。

## 4. Unity 实测

脚本先复制临时工程、删除其中旧的插件 DLL，仅加入 `Mahjong.Contracts` / `Mahjong.Contracts.Wire` / 探针 / `Google.Protobuf`，然后执行：

```text
Unity.exe -batchmode -quit -nographics -projectPath <temp> ^
  -executeMethod Mahjong.Unity.Platform.WireAotSmoke.Run ^
  -logFile build\p22-aot\<stamp>\unity-wire-aot.log
```

### 4.1 Unity 脚本编译（补充检查）

用 Unity 自带 Roslyn 分别编译：

- Editor 分支：`-define:UNITY_EDITOR`，退出码 0；
- Player 分支：`-define:MAHJONG_WIRE_AOT_SMOKE`（不定义 `UNITY_EDITOR`），退出码 0。

结论：**PASS（仅编译）**。引用为 Unity 6000.3.27f1 的 `UnityEngine.dll`、`UnityEditor.dll`、`netstandard.dll`。这不等于 Unity Editor 导入、场景加载或 IL2CPP 编译通过。

### 4.2 IL2CPP helper 编译

生成的 `MahjongIl2cppSmokeBuild.cs` 使用：

```csharp
var options = new BuildPlayerOptions
{
    scenes = scenes,
    locationPathName = output,
    target = buildTarget,          // StandaloneWindows64 或 Android
    targetGroup = group,
    options = BuildOptions.None,
    extraScriptingDefines = new[] { "MAHJONG_WIRE_AOT_SMOKE" }
};
PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP);
```

符号随每次 `BuildPlayer` 通过 `extraScriptingDefines` 传入，不再先 `SetScriptingDefineSymbolsForGroup` 再立刻构建、也不依赖异步 Editor 重编译。目标平台与 IL2CPP 后端均为显式选择。

用 Unity Roslyn 对 helper 源文件编译，退出码 0、无诊断：

```text
exit=0; no diagnostics
```

结论：**PASS（仅 API 可编译）**。由于缺少许可与本机 C++/NDK 工具链，helper 从未真正执行构建，**build = NOT_RUN**。

### 4.3 Editor 实际尝试

实测：

```text
[Licensing::Client] Error: Code 404 while processing request
  (status: Found 0 entitlement groups and 0 free entitlements matching requested entitlement ids)
No valid Unity Editor license found. Please activate your license.
exit=198
```

结论：**BLOCKED（Unity 许可）**，不是编译失败，也不是 PASS。

原始日志与结果：

- `build\p22-aot\20261023-185848\unity-wire-aot.log`
- `build\p22-aot\20261023-185848\results.txt`
- `build\p22-aot\20261023-185848\unity-smoke-compile.log`（`exit=0; no diagnostics`）
- `build\p22-aot\20261023-185848\unity-smoke-player-compile.log`（`exit=0; no diagnostics`）
- `build\p22-aot\20261023-185848\il2cpp-helper-compile.log`（`exit=0; no diagnostics`）

### 4.4 link.xml 描述文件解析（独立 UnityLinker，无许可）

`test-wire-aot.ps1` 在 `.NET AOT probe` 之后新增两个真实 UnityLinker 场景，直接调用安装的 `UnityLinker.exe`，只验证描述文件解析与 `preserve="all"`，**不是 IL2CPP build、player build 或 runtime**。

场景 A（普通工程缺可选程序集）：`--search-directory` 仅含 `Assets/Plugins/Mahjong`、`NetStandard/ref/2.1.0` 与 IL2CPP `build/deploy`。结果：

```text
PASS  Link.xml normal project  exit=0; descriptor resolved
actions: Link => Mahjong.Contracts
outputs: Mahjong.Contracts.dll only (Wire/AotSmoke/Google.Protobuf absent)
```

该场景证明 `ignoreIfMissing="1"` 让正常工程不再因三个可选程序集缺失而报 IL1013。

场景 B（smoke 工程程序集齐全）：把生成的探针输出 DLL 复制到 `build/p22-aot/<stamp>/linker/smoke-input`，四个程序集均存在。结果：

```text
PASS  Link.xml smoke project   exit=0; descriptor resolved
preserved Google.Protobuf=360, Mahjong.Contracts.Wire=177, Mahjong.Contracts.Wire.AotSmoke=5
actions: Link => Google.Protobuf / Mahjong.Contracts /
                 Mahjong.Contracts.Wire / Mahjong.Contracts.Wire.AotSmoke
```

脚本还断言以下具体类型被 `preserve="all"` 保留：`Mahjong.Contracts.Wire.Envelope`、`Mahjong.Contracts.Wire.Hello`、`Mahjong.Contracts.Wire.AotSmoke.WireAotProbe`、`Google.Protobuf.MessageParser`。

证据（最终运行 `20261023-185848`）：

- `build\p22-aot\20261023-185848\linker\normal-absent\UnityLinker_Diagnostics\AssemblyActions-BeforeOutput.log`
- `build\p22-aot\20261023-185848\linker\normal-absent\UnityLinker_Diagnostics\TypePreserve-BeforeMark.log`
- `build\p22-aot\20261023-185848\linker\smoke-present\UnityLinker_Diagnostics\AssemblyActions-BeforeOutput.log`
- `build\p22-aot\20261023-185848\linker\smoke-present\UnityLinker_Diagnostics\TypePreserve-BeforeMark.log`
- `build\p22-aot\20261023-185848\results.txt`

中间运行 `20261023-185609` 曾因 PowerShell `Start-Process` 在同时重定向 stdout/stderr 时返回空 `ExitCode`，把两个本已 exit=0 的场景误记为 FAIL。实现已改为用 .NET `Process` 异步读取两条流并读取真实退出码；`20261023-185716` 起两个场景均为 PASS。`185609` 仅作中间证据保留，不作为结果。

复验者原始最小命令（只加插件目录与 NetStandard）单独留证：

```text
build\p22-linker-review-exact-repro.log
```

其 exit=1 的错误是 `Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'mscorlib'`，已无 `IL1013`/`XmlResolutionException`。原因是该最小命令未搜索 `...\il2cpp\build\deploy`；脚本场景补上该目录后 exit=0。这属于宿主命令/搜索路径的后续缺口，不是本次缺失程序集缺陷，也不能据此宣称 IL2CPP 构建通过。若宿主后续还出现其他 linker 缺口，应在对应平台的真实构建中单独记录。

## 5. IL2CPP 平台实测

### 5.1 工具链盘点（只读、实际探测）

`test-wire-aot.ps1` 每次运行都实际探测环境变量、Unity 安装目录、`vswhere` 与常见 SDK 路径，不再硬编码“NDK/SDK 不存在”。本机结果：

```text
PASS     Unity editor             D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe
PASS     Unity IL2CPP compiler    ...\Editor\Data\il2cpp\build\deploy\il2cpp.exe
PASS     Windows IL2CPP module    ...\Variations\win64_player_development_il2cpp
PASS     Android Player module    ...\PlaybackEngines\AndroidPlayer
PASS     Android SDK              C:\Users\sncdc\AppData\Local\Android\Sdk
BLOCKED  Android NDK              not found
PASS     Android adb              C:\Users\sncdc\AppData\Local\Android\Sdk\platform-tools\adb.exe
PASS     JDK java.exe             C:\Program Files\Android\Android Studio\jbr\bin\java.exe
BLOCKED  MSVC cl.exe              not found
BLOCKED  Windows SDK              not found
INFO     Unity license            1 entitlement file(s) only; batchmode seat validation is the only proof
```

### 5.2 Windows IL2CPP

命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\unity\test-wire-aot.ps1 -Il2cppTarget Windows
```

实测（退出码 2）：

```text
BLOCKED  Windows IL2CPP build     prerequisites missing:
  MSVC cl.exe (Visual Studio C++ workload / Build Tools); Windows 10/11 SDK
NOT_RUN  Windows IL2CPP runtime   build did not run; no player was produced
```

Unity 构建未启动，未生成 `.exe`。结论：**build BLOCKED / runtime NOT_RUN**，不是 PASS。该入口同时复跑了两个 UnityLinker 场景，均 PASS；结果见 `build\p22-aot\20261023-185914\results.txt`。

### 5.3 Android IL2CPP

命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\unity\test-wire-aot.ps1 -Il2cppTarget Android
```

实测（退出码 2）：

```text
BLOCKED  Android IL2CPP build     prerequisites missing: Android NDK
NOT_RUN  Android IL2CPP runtime   build did not run; no player was produced
```

未生成 APK；即使只生成 APK，脚本也不会记 runtime PASS：只有安装后从 logcat 读到 `[WireAotSmoke] PASS` 才记 runtime PASS。结论：**build BLOCKED / runtime NOT_RUN**。该入口同时复跑了两个 UnityLinker 场景，均 PASS；结果见 `build\p22-aot\20261023-185929\results.txt`。

### 5.4 iOS

无 macOS、无 Xcode、无 iOS 真机，记 **BLOCKED / NOT_RUN**。不以 Editor 或 Windows 结果替代。

### 5.5 需要补齐的资源

| 目标 | 缺失 | 补验入口 |
|---|---|---|
| Unity Editor | 有效 seat/headless 许可 | 激活后运行 `test-wire-aot.ps1` |
| Windows IL2CPP | Visual Studio C++ 工具 + Windows SDK + 许可 | `test-wire-aot.ps1 -Il2cppTarget Windows` |
| Android IL2CPP | Android NDK（与 Unity 6.3 匹配）+ 许可 + 设备 | `test-wire-aot.ps1 -Il2cppTarget Android` |
| iOS IL2CPP | macOS + Xcode + 设备/签名 | 在 macOS 上复用同一探针与 `link.xml` |

## 6. 可执行性与安全性修订

1. **每次构建符号**：`BuildPlayerOptions.extraScriptingDefines = ["MAHJONG_WIRE_AOT_SMOKE"]`，目标 `StandaloneWindows64` / `Android`，后端显式 `IL2CPP`；不再改写 ProjectSettings 定义。
2. **临时目录删除**：所有递归 `Remove-Item` 先 `Resolve-Path`，要求目标位于本次 `mahjong-wire-aot-<guid>` 临时根内；清理临时根时还要求其位于系统 TEMP 且叶名带本次前缀，否则拒绝删除。已用含空格路径 `build\p22-aot\temp with space` 实测创建、构建、Editor 启动与清理。
3. **进程启动**：`Start-Process -WindowStyle Hidden`；参数经 `ConvertTo-NativeArgument` 逐个加引号后拼成一条命令行，覆盖含空格的 UnityPath/临时路径/日志路径。含空格临时路径实测 Unity 正常接收 `-projectPath` 并走到许可阶段（exit=198）。
4. **状态区分**：结果按 `.NET AOT probe`、`Link.xml normal project`、`Link.xml smoke project`、`Unity script compile`、`IL2CPP helper compile`、`Unity Editor load`、`Windows/Android IL2CPP build`、`Windows/Android IL2CPP runtime`、`iOS IL2CPP` 分别记 PASS/FAIL/BLOCKED/NOT_RUN；退出码 0 仅当全部 PASS，1 为 FAIL，2 为 BLOCKED 或 NOT_RUN。
5. **只读探测**：Unity、MSVC、Windows SDK、Android SDK/NDK、adb、JDK 均按实际路径探测。Android SDK 在有 `platforms`/`platform-tools` 时记 PASS，不因“Unity 未捆绑 SDK”而误报缺失。

## 7. 基线保持

以下保持 `a96c195` 基线，未修改：

- `dotnet/src/Mahjong.Contracts.Wire/mahjong.proto`（schema 未改）
- `unity/Mahjong.Unity/ProjectSettings/`
- `unity/Mahjong.Unity/Assets/Plugins/Mahjong/` 下既有 DLL
- V4 生产源码与既有验收文档

`git diff --name-only a96c195` 的预期结果为第 1 节六个路径；实际已在提交前核对。

## 8. 实际状态汇总

| 检查 | 状态 | 证据 |
|---|---|---|
| .NET 8 加载生成代码 | **PASS** | 18 类消息，`bytes=1417`，退出码 0 |
| .NET 全量测试 | **PASS** | 315 通过 / 0 失败 / 0 跳过 |
| Unity 脚本对 Unity 程序集编译 | **PASS（补充）** | Editor/Player 两个分支均 exit=0 |
| IL2CPP helper 对 Unity API 编译 | **PASS（补充）** | Unity Roslyn exit=0，无诊断 |
| link.xml 普通工程（缺可选程序集） | **PASS（描述文件解析）** | UnityLinker exit=0；仅 Link `Mahjong.Contracts`；无 IL1013 |
| link.xml smoke 工程（程序集齐全） | **PASS（描述文件解析）** | exit=0；Wire 177 / AotSmoke 5 / Protobuf 360 类型被保留 |
| Unity Editor 导入/执行 | **BLOCKED** | 许可 404，exit=198 |
| Windows IL2CPP build | **BLOCKED** | 缺 MSVC、Windows SDK |
| Windows IL2CPP runtime | **NOT_RUN** | 无 player |
| Android IL2CPP build | **BLOCKED** | 缺 NDK |
| Android IL2CPP runtime | **NOT_RUN** | 无 APK/设备 |
| iOS IL2CPP | **BLOCKED / NOT_RUN** | 无 macOS/Xcode/设备 |
| AOT 裁剪实测 | **NOT_RUN** | 无 IL2CPP 日志；`link.xml` 解析不等于裁剪通过 |
| TLS/WSS | **NOT_RUN** | P2.2 不含传输实现/端点 |

## 9. Integration gap（明确未完成项）

1. 探针未纳入 `dotnet test`：纳入需要修改测试 csproj 或新增测试文件，均超出六路径账本；当前以同一 CLI 探针独立验证。
2. Windows/Android IL2CPP 只有 helper 编译证据，没有真实 player/APK，也没有 runtime 证据；缺 MSVC、Windows SDK、NDK 与 Unity 许可。link.xml 的两个 UnityLinker 场景只证明描述文件解析和 preserve 规则，不能替代 IL2CPP 裁剪实测。
3. Unity Editor 的生成代码加载未实测：许可在脚本执行前终止进程。
4. iOS 与 TLS/WSS 不在本轮可执行范围。
5. 复验者的原始最小 linker 命令仍因未搜索 IL2CPP `build/deploy` 而在 `mscorlib` 解析处 exit=1；脚本场景补齐搜索目录后通过。这是宿主命令/搜索路径缺口，与已修复的“link.xml 引用缺失程序集”不同，需在真实 IL2CPP 构建中另行确认宿主是否还有后续 linker 缺口。

## 10. 不得宣称的内容

- 不宣称 Unity Editor 已加载生成代码。
- 不宣称 IL2CPP 构建或运行通过。
- 不宣称 Android/iOS 真机通过。
- 不宣称裁剪安全；`link.xml` 只是保守保留规则。两个 UnityLinker 场景的 PASS 只表示描述文件在两种程序集形态下可解析、`preserve="all"` 生效，不等于真实 IL2CPP 裁剪、构建或运行通过。
- 不宣称 WSS/TLS 可用。
- 不把本报告的 .NET PASS 扩展为 G1 的 Unity/IL2CPP 门槛通过。
