# V5 G2-C05 Android 环境与候选绑定准备（2026-11-01）

## 0. 结论与边界

**性质**：本任务只做 Android 环境核对、候选源码/共享插件/工具链绑定、设备只读发现和下一步构建交接。**没有**运行 Unity/Player/Android 构建，没有安装/卸载/启动游戏，没有下载/升级共享 SDK，没有更改项目或机器设置，没有 push/PR/MR/approve/merge/deploy，也没有改 scheduler/complete.flag。

**结论**：

- 环境本体可用：Unity 6000.3.27f1、AndroidPlayer/IL2CPP、OpenJDK 17.0.20.1、SDK android-36/build-tools 35/36、NDK 27.2.12479018、CMake 3.22.1、锁定 adb 均存在且版本检查 native exit 0。
- 候选静态绑定完成：`8fd7c6cb`（accepted base）→ `b33f0aad`（C03 code）→ `db9e5d53`（caller tested merge）→ `cde5da16`（accepted publication）。`db9e5d53..HEAD` 的代码差异为 0，发布增量只有 C03 交付文档。
- **最终 APK 仍 NOT_RUN**（本轮未授权/未尝试构建），但**不是**被私有 toolchain 阻塞：本树默认 `build\android-toolchain\private\toolchain.json` 缺失只是默认路径事实；原已接受 manifest 仍在 `D:\majiang-wt\android-toolchain-ds-20261027\build\android-toolchain\private\toolchain.json`，SHA256 `E478...`，Ninja/CLI helper 均存在且匹配；显式 `-AndroidToolchainManifest` 的只读 preflight native exit 0。详见 §2.4 与 R1-02。
- **设备矩阵 NOT_RUN_NO_DEVICE**：锁定 adb `devices -l` native exit 0，设备列表为空；没有自动启动模拟器，没有用系统 adb 替代，没有杀共享 adb daemon。
- 当前 7 个自有 plugin DLL 的 `ProductVersion` 仍嵌入 `8fd7c6cb`，只是历史 base 工件，不是本候选输出；最终构建必须强制重建并重新绑定（这是准备观察，不是本轮缺陷）。
- 本 JSON/报告冻结在 **cde5 快照**，是 **UI15 修复前**的历史准备：UI15 已在 `734b3775` 通过并发布，但不在本快照内；C04 恢复对账仍是独立增量。本文件不是最终 C05/G2 通过。C03 组件验收不等于 G2 通过；正式 C04/C05/G2–G5 未批准；V4 仍是唯一生产。

**提交身份**：

| 项 | 值 |
|---|---|
| accepted base / origin-v5 | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| candidate publication (binding-time HEAD) | `cde5da1649d42151abaefc2edc5f558a287d703f` |
| C03 code commit | `b33f0aad21c962a6f147dbfdb8a63fd98942d4a2` |
| caller tested merge | `db9e5d536f618a9a6991e67498b73ce34d056f4d` |
| receiver final | `2beb3ef9b457e8ecb7704cea9f2e080c8e86f45f` |
| binding-time HEAD tree | `8c4933aad6615f8230cc038e54c2a721e253aa00` |
| branch | `wt/g2-c05-android-prep-ds-20261101-1300` |
| worktree status at probe | tracked clean；R1 交付为两文件精确重命名，无其他 tracked 改动 |
| base→HEAD | 724 files changed, +254184/−1683；代码路径 `db9e5d53..HEAD` diff exit 0 |

**交付路径（R1 后恰好两个新增/重命名 tracked 文件；精确 expectedFiles）**：

- `doc/v5-重构/V5-G2-C05-ANDROID-CANDIDATE-READINESS-20261101.md`（本报告）
- `doc/v5-重构/V5-G2-C05-ANDROID-CANDIDATE-BINDING-20261101.json`（machine-readable binding）

---

## 1. 来源、祖先与发布增量

实际执行（git 只读）：

```powershell
git rev-parse HEAD
git rev-parse --abbrev-ref HEAD
git rev-parse 'HEAD^{tree}'
git rev-parse origin/v5
git merge-base --is-ancestor 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7 HEAD
git merge-base --is-ancestor b33f0aad21c962a6f147dbfdb8a63fd98942d4a2 HEAD
git merge-base --is-ancestor db9e5d536f618a9a6991e67498b73ce34d056f4d HEAD
git diff --quiet db9e5d536f618a9a6991e67498b73ce34d056f4d..HEAD -- unity dotnet tools assets gradle engine protocol client server presentation lwjgl3 android tests
git diff --name-status db9e5d536f618a9a6991e67498b73ce34d056f4d..HEAD
```

结果：

- 三个 `merge-base --is-ancestor` 均 exit 0。
- `git diff --quiet ...` exit 0：从 caller tested merge `db9e5d53` 到 publication `cde5da16`，代码树零差异。
- `db9e5d53..HEAD` 仅 `M doc/v5-重构/V5-G2-C03-UNIFIED-CANDIDATE-DELIVERY-20261101.md`（+20/−2，即 §15 caller 接受记录）。
- `8fd7c6cb..HEAD` 为 724 files changed, +254184/−1683；不把该总量误写成 C03 代码增量。

C03 来源接受事实保留为历史，不在本轮重跑：`receiver 2beb3ef9`、`code b33f0aad`、`caller tested merge db9e5d53`、`publication cde5da16`；14 工程 2820/2820、12 round/8 GAME、LocalHost 134/134、TableService 204/204、C02 工具 27/27 均为历史接受事实。PlayMode 旧 baseline 与候选均 147/132 pass/15 fail，15/15 一致；旧 Compare BLOCKED17/UNTRUSTED_LOCAL 与 LOCAL G7-02 BLOCKED 保留，不放宽门禁。

---

## 2. 环境实测

所有命令均只读、进程内有界；未启动 Unity/Player/Android 构建。子进程 TEMP/TMP 在本次证据进程内指向本树 ignored `build/c05-android-prep-1a33507caee9/temp`，未改系统环境。

### 2.1 Unity / Android Build Support

| 项 | 实测值 |
|---|---|
| Unity exe | `D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe`，282,544 B，ProductVersion `6000.3.27f1_672d562a42a7`，SHA256 `CFAF4794DF5B7AD0AE3AFE43EDCB6CCB1E08B422A2D6A768E52CE485E0BBA673` |
| ProjectVersion | `m_EditorVersion: 6000.3.27f1` / `m_EditorVersionWithRevision: 6000.3.27f1 (672d562a42a7)` |
| AndroidPlayer | `Editor\Data\PlaybackEngines\AndroidPlayer` 存在 |
| IL2CPP | `Editor\Data\il2cpp\build\deploy\il2cpp.exe` 存在，153,512 B，SHA256 `8EF43C2A7357B910ED85FF9345F1F56D83AFB55A8F22B080DC0E2E00CA0535F7` |
| Android extension | `UnityEditor.Android.Extensions.dll` SHA256 `38CE680A9484981FBAFF4B8096A342D0ED2544AA69024BBBC7680A1E81920353` |
| Build program | `AndroidPlayerBuildProgram.exe` SHA256 `E8C8253A7373CC4FE00A54B4E63FE0052AAE1B5F3804D5A27A294B2A33852D18` |
| UnityVersion.txt | **缺失**（脚本预期路径 `Editor\Data\Resources\UnityVersion.txt`）；历史 summary 同样记 `unityVersion=UNKNOWN`，见 OBS-04 |

### 2.2 JDK / SDK / NDK / CMake

| 项 | 实测值 |
|---|---|
| OpenJDK | junction → `D:\Android\jdk17\jdk-17.0.20.1+1`；`java -version` native exit 0，stderr 原文 `openjdk version "17.0.20.1" 2026-08-18` / Temurin-17.0.20.1+1；java.exe SHA256 `1977F302375ADBB920D41DAC65C7E22EB9C2ED8E1E8D6258964154FF16F14406` |
| SDK | junction → `C:\Users\sncdc\AppData\Local\Android\Sdk`；platforms `android-36`；build-tools `35.0.0,36.0.0`；cmdline-tools `16.0,latest,latest-23-newcli-backup`；cmake `3.22.1` |
| android.jar | 27,768,026 B，SHA256 `D9EB9DA824D9E247A352F570F01E1169E725B2954BCA9E283A71786C59B59F9A` |
| aapt2 | `build-tools\36.0.0\aapt2.exe`，5,423,200 B，SHA256 `BABF3122E515DDB954C5AC4669E085CE990536C035E3072DE30127BDDD6E3608`；`aapt2 version` native exit 0，版本写在 stderr：`Android Asset Packaging Tool (aapt) 2.20-13193326` |
| CMake | `cmake\3.22.1\bin\cmake.exe`，10,036,320 B，SHA256 `3754D54D635F7D6E1C9DBC9EC8F1D61BE26E72E2EB97F3EE00CE0508B921CA99`；`cmake --version` exit 0，`cmake version 3.22.1` |
| SDK manager | `cmdline-tools\16.0\bin\sdkmanager.bat` 存在，2,295 B，SHA256 `14866E8B3EDE1D90B633042748254BD264E0678F68B9390540E839588DBB47F4`；source.properties `Pkg.Revision=12.0` |
| NDK | junction → `D:\Android\Sdk\ndk\27.2.12479018`；source.properties `Pkg.Revision = 27.2.12479018`、`Pkg.ReleaseName = r27c`，SHA256 `70F1C5165E6D997B18782A49CFF71DA7A3CAACD0C4CD811DA24C43359FB83CF6` |
| NDK clang | `clang.exe` 126,674,016 B，SHA256 `DCDDC0336730B1053998E305CC41B93614C62209ACB96B4875D44FC3A2B2D522`；`--version` exit 0，Android clang 18.0.3 |
| ARM64 工具链 | `aarch64-linux-android21..26-clang.cmd` 存在；`sysroot\usr\lib\aarch64-linux-android` 存在 |

### 2.3 adb 与设备

| 项 | 实测值 |
|---|---|
| 锁定 adb | `AndroidPlayer\SDK\platform-tools\adb.exe`，8,273,560 B，SHA256 `B4A6B455702684652CCCF7B46258B29E653538904359A58FD4931CF3EF286B3F` |
| `adb version` | native exit **0**；`Android Debug Bridge version 1.0.41` / `Version 37.0.1-15733141` |
| `adb devices -l` | native exit **0**；stdout 28 B：`List of devices attached\r\n\r\n`；stderr 79 B：`* daemon not running; starting now at tcp:5037\r\n* daemon started successfully\r\n` |
| 设备 | **无设备**；authorized 0 / offline 0 / unauthorized 0；serial=null；未启动模拟器 |
| 状态 | `NOT_RUN_NO_DEVICE`；DeviceInfo/Install/Start/Stop/Capture 全部 `NOT_RUN` |

另实际执行了已接受的 C05 Devices 动作（只用锁定 adb）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Devices -EvidenceDir build/c05-android-prep-1a33507caee9/device
```

`device-list.json` 记录 `status=PASS`、`nativeExit=0`、`stdout=List of devices attached\r\n\r\n`、`stderr=* daemon not running...`、`devices=[]`，SHA256 `A6D72E4A0613407603BE90CBBCE6F545F2646F7DB4C3016B95DEC10B66C5E6DC`。外层 PowerShell 包装在 120 s 内未关闭（adb daemon 继承输出句柄），在 JSON 完整后只停止了本任务的包装进程，**没有杀共享 adb daemon**；这是已记录的 UNSEALED 限制，见 OBS-05。

### 2.4 磁盘 / TEMP / 离线依赖 / 许可 / mutex

| 项 | 实测值 |
|---|---|
| C: | 16,718,323,712 B free（15,943.84 MiB） |
| D: | 107,405,312 B free（102.43 MiB） |
| E: | 407,340,257,280 B free（388,469.94 MiB） |
| 系统 TEMP/TMP | `C:\Users\sncdc\AppData\Local\Temp` |
| 证据 TEMP/TMP | 进程内改为 `build\c05-android-prep-1a33507caee9\temp`；目录写探针 PASS，证据根写探针 PASS |
| dotnet | `D:\Unity\dotnet\dotnet.exe`，SDK 8.0.426 |
| NuGet cache | `C:\Users\sncdc\.nuget\packages` 存在；Google.Protobuf 3.28.3、Grpc.Tools 2.68.1、System.Memory 4.5.3、System.Buffers 4.4.0、System.Numerics.Vectors 4.4.0、System.Runtime.CompilerServices.Unsafe 4.5.2 |
| UPM cache | `%LOCALAPPDATA%\Unity\cache\upm\db` 存在；未做逐包离线解析 |
| 离线 restore | **NOT_RUN**；静态 cache 存在不等于离线下 restore 已通过 |
| Unity 许可 | **NOT_VERIFIED**；不启动 Unity 无法验证 batchmode entitlement |
| Unity mutex | `Local\MahjongV5P45UnityValidation`：**未获取**；探测时不存在。最终重资源阶段必须持锁，等待 30 s 失败即 BLOCKED，不得再启第二个 Unity |
| 私有 toolchain 默认路径 | 本树 `build\android-toolchain\private\toolchain.json`、`ninja\1.13.2\ninja.exe`、`android-cli\android-cli-sdkmanager16.cmd` **不存在**；这是 fresh worktree 的默认路径负例，不是原 toolchain 身份消失 |
| 原已接受 toolchain 显式路径 | `D:\majiang-wt\android-toolchain-ds-20261027\build\android-toolchain\private\toolchain.json` 存在，3,483 B，SHA256 `E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934`；`ninjaExe` 603,648 B / `E52A7AD9538D9618C67A0BD777964E2EEC8A30F68B810A2F6ADCE1F2DAF847B8`；`androidCliBin` 1,452 B / `F66B015EF9F2054D8E4A9877185A6D922109AA68A4AF366F5E96C2629F7BD210`；JDK/SDK/NDK/CMake 身份与 manifest 一致 |
| 显式 manifest preflight | 用冻结脚本的 manifest/helper 语句、显式 `-AndroidToolchainManifest` 原路径只读执行：native exit **0**，未创建输出目录、未启动 Unity/helper、未改机器；日志 `build/c05-android-prep-r1-d114005f87be/logs/accepted-manifest-preflight.log`，SHA256 `B75BB362BC7BB2E7BB460EABEA52E8000AD9CD95B5584FDDF8FB76CB2B0FE4F4` |

---

## 3. 候选源码 / 共享插件输入 / 输出绑定

### 3.1 源码清单

`sourceIdentity.repositoryManifest`：`git ls-tree -r HEAD` 共 **2,445** 个文件，按 LF 规范化后 SHA256 `5DC2BE90E54E5068952D73D36A81BC7398EF440F87F760C7FC6ACD349AB835D1`。

| Scope | Files | Tree SHA | ls-tree SHA256 |
|---|---:|---|---|
| `unity/Mahjong.Unity/Assets` | 314 | `6e7b6caad625e25a3e438d0ca196ef217a03fff4` | `CA8BEF8C10975D7F0A587ABCF2B827B9D0969C73C00CE78AD20EE81E2BB98AED` |
| `.../Assets/Scripts` | 130 | `eead90ecba23144e63a30835284c24a1707888d2` | `8106C836DA9716832D40F879EFB45D62AA64FB6776782D872A98D2B7ADBFF49E` |
| `.../Assets/Scenes` | 14 | `b6c7982d4bb4bf9b17a6640d60b8f6bd996f97fd` | `9F18B2CB53E86F290D7A1C607C88A64382F773906A383CE1A8C4CEBD2096CEBB` |
| `.../Assets/Plugins` | 32 | `344ac09e885a1d85ea0e9965efbcd51a401dfdf1` | `187AE23D05D5FFFB15DD24827E7C0C94E54C4213948896560033A0F1CAD488D6` |
| `.../Assets/Editor` | 6 | `4335d13602111e5740d85c116b88071466b63a3e` | `9B91A732F74183C9C21751963B42ACCBE876C5736614E6AA225BF61B5AC6F780` |
| `.../ProjectSettings` | 24 | `bf4a66fbf741aa2cf35f1c02c58925fbc1a708a3` | `BFF017621A979E7ADC71297D48704CE42D7AED68F3CB4178C3AD3B465272FA4C` |
| `.../Packages` | 2 | `3652c32b9e24657bcb350ef8d641455cbffd99bb` | `4A2F32C9617764AF4C9B51E4DBF05B6E6C5E3D59A947336C71EC18C75E7869C0` |
| `dotnet/src` | 193 | `c1a6dfb8ff3b2d9992f1fdaf0d3356813778dd70` | `0E1143CC4E8046B5C5A8CE7CB1BC9310CAD9841A9294ADF06FBB776D0A66E2C5` |
| `tools/unity` | 28 | `3b8cb75dbb231bf094ac190f49f6a90c645b8f55` | `A862201B71512A5AA159BCF481E07391E4A5957727D3C39E8D9A1AE644235C62` |
| `assets` | 60 | `6b39c2bfa74929916679ca81d87c4b01c97efbfd` | `ABA9213D6D468000C9A0C6B14D9B1A0BE318BF07B34616AFB19489B33B6ED378` |

关键文件（完整 SHA256 在 JSON `sourceIdentity.keyFiles`）：

| 文件 | SHA256 |
|---|---|
| `C05FullGameAndroidBuild.cs` (6,988 B) | `695C6A561F95D469B678C9B3D3F554BB11449F28D2EBEC77708FC105F3FCA19A` |
| `C05FullGameAndroidBuild.cs.meta` | `8CD627EC26F85F0681931B0EAB03B20E5D57677B08C44BA9165FC08DB38CBA5D` |
| `ProjectSettings.asset` (22,356 B) | `828AEA94FE602596FF287C28586DB34F18ABD1B37F75075594D2AC3CBD8EEE34` |
| `EditorBuildSettings.asset` (872 B) | `309F769F677F6E82B17578A96217A85D54AC97DE05E444DB74E7F20A770761E6` |
| `ProjectVersion.txt` | `56AEAD51018C992AF406752E47C49E8D9458C70F00B983F4BB603B1B3AFC223E` |
| `Packages/manifest.json` | `7816B51C8B2ECE6C1515B947D3F0790E095F0910A9E6768FDBBE6F588BF697A7` |
| `Packages/packages-lock.json` | `5EA8C9829228AA440ABDD301B3B836D8BE3891D3AC4D9D2FC950C545792C2DB2` |
| `TMP Settings.asset` | `3D789ABE0C8946EFC2BF8C131E1A28EDBB75F82A96421E195E64C2DEDC8EA2E2` |
| `NotoSansSC-Regular SDF.asset` | `2751EB9F4EFC598FCDD575D9C05380A57B72FAB12B01FBE57B688D779BF77C0C` |
| `build-p45-android-game.ps1` (32,253 B) | `33FCFE1BF536E71376CF27AF6F245B5880218570C02F20B17EA3F768D3C5A640` |
| `run-p45-android-game.ps1` (20,221 B) | `A87CC5B5EAFEDD38013E0157E79EEC6CD5CC652388395229D8AB31603269B9DF` |
| `repair-android-toolchain.ps1` (41,523 B) | `60E531C1079BFDF3BC357CD4903EF9910F218BC6DD65F85CF9FB286774A2DE32` |
| `build-shared.ps1` | `05BF3661E5F01614EEFEEDDA946C7B9960B36DD157708D1EEABD6A1F569588AD` |
| `check-shared-plugins.ps1` | `5D4D3C21F3B39E89B6C26757818EDC3DE5C610F679CD14BF2B6B9F671B71F7D9` |
| `shared-plugin-provenance-lib.ps1` | `16ACC8D791CE9AA3A6A3625892CFB07E5ECE52520AF01103462CDA978C48E852` |
| `VerifyP45AndroidGame.Tests.ps1` | `240D85355E21888C0EF7E076DC4FD00956A61A32B3A4F2341B4FD347CDBE73F1` |

六个启用场景（顺序固定）：

| 场景 | Bytes | SHA256 |
|---|---:|---|
| `Assets/Scenes/Boot.unity` | 9,544 | `4179572E4BCA429F6F7FF5A2E135E09455F977EF8A5130C04C56C9940E809D4A` |
| `Assets/Scenes/Menu.unity` | 64,793 | `43EF15582C03A2FB3BD9CA6B035D0D06EB6DE5E8252A5870449B487D4E052F76` |
| `Assets/Scenes/Lobby.unity` | 49,649 | `5745BED2EE9E20FF9CEFF6A2852709E53DF5D819EBD983580946348207FE9AB2` |
| `Assets/Scenes/Table.unity` | 807,991 | `70BEAC72D6ED0A691F516C1049C9AEC10BE3B68BF63E4EAA7D8D16F8E22822EB` |
| `Assets/Scenes/Spectator.unity` | 195,915 | `26A359BF59DD4A7A6749972D50A88A6A777733914684BD934FE61B8BD3E368BD` |
| `Assets/Scenes/Settlement.unity` | 92,801 | `917902D47EC31210AB11AF360FBAF3E65142DE2117A9975E326EAB32ECBCD2FC` |

Android 构建设置静态核对：

- package `com.majiang.game`；`overrideDefaultApplicationIdentifier: 1`；`AndroidBundleVersionCode: 1`。
- `AndroidMinSdkVersion: 25`；`AndroidTargetSdkVersion: 0`（自动；已接受 SDK 最高 platform 为 android-36 → 36）。
- `AndroidTargetArchitectures: 2`（ARM64）；`scriptingBackend.Android: 1`（IL2CPP）。
- `defaultScreenOrientation: 5`；portrait/upside-down 0，landscape-right/left 1；`androidStartInFullscreen: 1`；`androidRenderOutsideSafeArea: 1`。
- TMP Settings 与 `NotoSansSC-Regular SDF.asset` 存在。

### 3.2 共享 plugin 输入

使用 `tools/unity/shared-plugin-provenance-lib.ps1` 的 **只读静态** `Get-SharedPluginInputClosure`（不 build、不 restore、不写仓库）：

- 7 个自有项目：Domain / Contracts / Contracts.Wire / TableRuntime / Client / LocalHost / Ai。
- 输入数：**150**；按 Path 排序后逐行 `path + SHA256` 的 aggregate SHA256：`996CACB3CB0633B81609AF0FD7A51F40C9AB1BC199BED261DF50055F1ED06A20`。
- 工具身份：`build-shared.ps1` `05BF3661...`、`shared-plugin-provenance-lib.ps1` `16ACC8D7...`。
- 历史 C03 §15 的 unified 输入也是 150；本轮只做静态枚举，**没有**与 C03 manifest 做逐字节比较，也不声称已构建。
- 19 outputs / 7 ownDLL / 150 inputs 的历史值仅注明来源，不作为本轮新构建结果。

### 3.3 当前 plugin 输出是 base 工件，不是候选输出

7 个自有 DLL 的 `FileVersionInfo.ProductVersion` 全部为 `1.0.0+8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；最后提交在 2026-10-26。**它们是 accepted base 工件，不是 C03 candidate 输出**。

| DLL | Bytes | SHA256 | Embedded commit |
|---|---:|---|---|
| `Mahjong.Domain.dll` | 377,856 | `01414649429B7435D2A00EEC812A73F0D3B0A544AE12F484E19972584AD66679` | `8fd7c6cb` |
| `Mahjong.Contracts.dll` | 119,296 | `3F51D1ACA7831251D251543365AAD7643E45200B93D9AEACFE14E1133D6106F3` | `8fd7c6cb` |
| `Mahjong.Contracts.Wire.dll` | 386,560 | `AA6357D05EE4B9E08914F8C6AC98D6A8AC8699C497C37D8DA928202A2967975F` | `8fd7c6cb` |
| `Mahjong.TableRuntime.dll` | 482,816 | `C39D1EFAC086B867ED275607C6AF64F706CF3ED776A6694E42B935673C471D73` | `8fd7c6cb` |
| `Mahjong.Client.dll` | 183,808 | `8215C55B9FD9A08A571B68CA889DEC414788FFFDAF0F902792ADC072D51FC046` | `8fd7c6cb` |
| `Mahjong.LocalHost.dll` | 3,584 | `03EA16885BA8E5396EA2B516485E38D84A0336F68EDB7B1AE699CFC220E1E10D` | `8fd7c6cb` |
| `Mahjong.Ai.dll` | 128,512 | `BC8F18D0134DA2CDF520A2468E51EA1FA34CB9EA42ED7A61BAA8D454A19417AD` | `8fd7c6cb` |
| `Google.Protobuf.dll` | 483,936 | `16921420D56EE464D4B4C43BA70C7300172E2CF3852F80CB91B71932569D3A5F` | `5fda5abd`（3.28.3 第三方） |

C03 §15 的“7 个自有 DLL 嵌入精确 commit 相符”是 caller 在其 unified 构建输出上核对的，不是本树当前 tracked DLL 的证据。最终构建必须重跑 `build-shared.ps1` + `check-shared-plugins.ps1`，并把 post-build 的 7 个 DLL SHA/commit 绑到最终候选。

---

## 4. Android 构建合同与候选产物状态

| 项 | 合同值 |
|---|---|
| 入口 | `C05FullGameAndroidBuild.Build`（`unity/Mahjong.Unity/Assets/Editor/C05FullGameAndroidBuild.cs`） |
| package | `com.majiang.game` |
| minSdk / targetSdk | 25 / 0（自动解析 36） |
| ABI | 仅 `arm64-v8a` |
| backend | IL2CPP |
| scenes | Boot / Menu / Lobby / Table / Spectator / Settlement，顺序固定，六者必须启用 |
| TMP | Settings + FontAsset 必须存在 |
| clean cache | `BuildOptions.CleanBuildCache` |
| 其他 | 横屏限定、全屏、outside-safe-area；APK 必须晚于 build 开始时间 |
| 已接受显式 toolchain manifest | `D:\majiang-wt\android-toolchain-ds-20261027\build\android-toolchain\private\toolchain.json`，SHA256 `E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934`；默认本树路径缺失不影响该显式路径 |

**当前候选产物：全部 null + NOT_RUN**：

| 项 | 值 |
|---|---|
| APK | `null` |
| bytes | `null` |
| SHA256 | `null` |
| BuildReport / Unity log / native exit | `null` / `null` / `null` |
| package | `null`（未观察；目标值只在 §4 合同表） |
| ABI | `null`（未观察；目标值只在 §4 合同表） |
| Install / Start / Visual / Touch / Weak-network / iOS | `NOT_RUN` / `NOT_RUN` / `NOT_RUN` / `NOT_RUN` / `NOT_RUN` / `NOT_RUN` |

历史 52,142,409 B / `0506AEEA...` 的 APK 只作为历史事实列出，`current=false`，**没有**被当作当前产物或当前构建证据。

---

## 5. R1 复核发现与修复映射（0 BLOCKER / 3 MAJOR / 0 MINOR）

本轮完整清单均为首次复核发现；没有其他已知本任务缺陷留到下一轮。下列三条均已在本轮同一份文档/JSON 内修复。过去的 D01–D05 不再作为缺陷计数：其中“默认 manifest 缺失”是局部事实，“stale DLL / TEMP / UnityVersion / adb 包装”是准备观察或已记录边界，均不是本轮返修缺陷。

### C05-PREP-R1-01 — MAJOR — 未使用明确授权的 expectedFiles（已修复）

- **位置**：上一轮 tracked 路径 `doc/v5-重构/V5-G2-C05-ANDROID-ENV-BINDING-PREP-20261101.md/.json`。
- **原样复现**：

```powershell
git diff --name-only cde5da16 b52c47fe
git ls-tree -r --name-only b52c47fe -- doc/v5-重构/V5-G2-C05-ANDROID-CANDIDATE-READINESS-20261101.md doc/v5-重构/V5-G2-C05-ANDROID-CANDIDATE-BINDING-20261101.json
```

- **实际**：两个指定字面路径均不存在；上一轮自行使用了 ENV-BINDING-PREP 命名。
- **期望**：bridge expectedFiles 精确为 `V5-G2-C05-ANDROID-CANDIDATE-READINESS-20261101.md` 与 `V5-G2-C05-ANDROID-CANDIDATE-BINDING-20261101.json`。
- **证据**：Caller `REVIEW.txt` SHA256 `14CF762D399E2111DC291ADB68830C58BC59CBFA5A692FB4DE58FED8D68E6E77`；`independent-findings.json` SHA256 `1A42EBB8BCC79F541A1D9E9A938A6972981D883A78176CC2A0144C943DF8D615`。
- **修复要求**：仅 `git mv` 本任务自己的两个文件到精确路径；更新全部内部引用和 ignored validator；保留旧 ignored 证据，不保留 tracked 旧别名。
- **验收断言**：相对 `cde5da16` 最终只新增/重命名这两个精确路径；旧别名不存在；无生产/设置/工具改动；tracked clean；交付区间 diff-check exit 0。

### C05-PREP-R1-02 — MAJOR — 把默认本树 manifest 缺失误判为原已接受工具链不可用（已修复）

- **位置**：上一轮报告 §0/§2/§5 D01/§6/§7/下一 owner；JSON `environment.privateToolchain`、`statuses`、`finalBuildPreconditions`、`commands`。
- **原样复现**：

```powershell
$m='D:\majiang-wt\android-toolchain-ds-20261027\build\android-toolchain\private\toolchain.json'
(Get-FileHash $m -Algorithm SHA256).Hash
$j=Get-Content $m -Raw | ConvertFrom-Json
(Get-FileHash $j.ninjaExe -Algorithm SHA256).Hash
(Get-FileHash $j.androidCliBin -Algorithm SHA256).Hash
Test-Path build\android-toolchain\private\toolchain.json   # 仅默认路径负例：False
```

- **实际**：原已接受 manifest 仍存在，SHA256 `E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934`；`ninjaExe` SHA256 `E52A7AD9538D9618C67A0BD777964E2EEC8A30F68B810A2F6ADCE1F2DAF847B8`；`androidCliBin` SHA256 `F66B015EF9F2054D8E4A9877185A6D922109AA68A4AF366F5E96C2629F7BD210`，均存在且匹配。用冻结脚本的 manifest/helper 语句、显式传入该路径只读执行：native exit **0**。仅本树默认路径缺失。
- **期望**：把“默认路径缺失”与“原已接受显式路径可用”分开；不制造新 pin、额外 caller 裁决、复制/provision/重绑依赖。
- **证据**：`build/c05-android-prep-r1-d114005f87be/logs/accepted-manifest-preflight.log` SHA256 `B75BB362BC7BB2E7BB460EABEA52E8000AD9CD95B5584FDDF8FB76CB2B0FE4F4`；`manifest-identity.json` SHA256 `07A17B361821F1E8AD7068D6B45705F24FADF263E007A140195F366A5467CE61`；caller 原始 preflight 片段 `original-toolchain-preflight.txt`。
- **修复要求**：在两份文档中记录原 manifest/两个 helper/JDK/SDK 身份；下一步命令显式使用 `-AndroidToolchainManifest` 指原已接受路径；保留默认缺失负例；不得复制 manifest、provision、重 pin、下载/升级 SDK 或改源码。
- **验收断言**：原 pin/两个 helper 实际只读校验通过；默认缺失仍如实记录；不再要求新 manifest 授权；所有新增输出在 E 盘；preflight 成功不被写成 APK 通过。

### C05-PREP-R1-03 — MAJOR — 最终交接命令/产物 schema 合同不正确（统一修复组，已修复）

- **位置**：上一轮报告 §4/§5 D02/§6.1–§6.4；JSON `commands`、`candidateArtifact`、`expectedEvidenceFields`。
- **原样复现**：

```powershell
Select-String tools/unity/build-p45-android-game.ps1 -Pattern 'catch \{' -Context 0,4
# 读取实际 failure-summary 的 [pscustomobject]@{...} 键
Get-Content D:\majiang-wt\g2-c05-android-game-luna6-20261101\build\p45-android-game\20261101-r2-clean-2ccc7295\summary.json -Raw | ConvertFrom-Json
```

- **实际**：
  - (a) 旧 template 只赋 `$root`，却用相对 `-File tools/unity/...` 且不切 cwd；从另一 cwd 执行会选中另一树脚本，输出/manifest 可能跨树；device 模板也是相对脚本和松散 `<E:\case>` 占位。
  - (b) `candidateArtifact.status=NOT_RUN`，却把配置值 `packageId=com.majiang.game`、`abis=['arm64-v8a']` 写成 actual 字段。
  - (c) `failure-summary` 期望字段 `failedGate/firstError/evidence paths` 与冻结脚本实际写出的 `status/sourceHead/dirtyPaths/inputSha256/toolchainManifestSha256/nativeExit/error/buildReportLine/apkExists/evidenceRoot/unityLog` 不符。
  - (d) D02 旧验收要求“APK 内同一 DLL 字节一致”，而目标是 ARM64 IL2CPP；历史 APK 的证据是 native `libil2cpp.so`，不是 managed project DLL 打包。
- **期望**：绝对脚本路径/严格 root 约束；未观察 package/ABI 为 null；failure-summary 映射真实冻结键；managed 输入 provenance 与 native APK 证据分层。
- **证据**：`build/c05-android-prep-r1-d114005f87be/tool-fixture-results.json` SHA256 `E14E36608F7665143D0A929F6A6F3D27A4061DBB36B8F69580CE911B88C6AD93`；`logs/tool-fixture.log` SHA256 `46D77CE9F1F5A7332E183B257E77D977614BBC9A55A36D02687C214BEBFFD470`；冻结脚本 catch 行 `tools/unity/build-p45-android-game.ps1:238-242`；历史 summary `62AD41746D25B3B40E889F35154CA02A7EFBF97B1B55AD0042C9F8577821716B`。
- **修复要求**：
  - (a) 用 `Join-Path $root 'tools/unity/...'` 得到绝对 `$buildScript`/`$runner`；`$case=Join-Path $root 'build\c05-final-...'`；`-File $buildScript`/`$runner`；`-OutputRoot`/`-EvidenceDir` 均在 `$root\build`；每条命令后检查 `$LASTEXITCODE`。
  - (b) `candidateArtifact.packageId=null`、`abis=null`；目标 package/ABI 仅保留在 `androidBuildContract`。
  - (c) 只列冻结脚本实际字段；`failedGate/firstError/evidencePaths` 明确标为“未来可选扩展，当前不产出”，不改已接受脚本。
  - (d) 共享 DLL 以 fresh 构建输入/provenance/编译链绑定；APK 以自身 SHA、native ABI/package/BuildReport 校验；没有逐 DLL→APK 直接证据时明确边界；不得要求 APK 含 managed DLL 或把桌面 Player DLL 相等当 Android 证据。
- **验收断言**：跨 cwd 微型 TOOL_FIXTURE 记录 fixture-root 绝对脚本、root 内 output/evidence、native exit 0；actual package/ABI null；failure-summary 键与冻结源一致；不要求 IL2CPP APK 内 managed DLL；未启动 Unity/设备。

### 5.1 准备观察与边界（不计入本轮缺陷）

| ID | 事实 | 处理 |
|---|---|---|
| OBS-01 | 本树默认 `build\android-toolchain\private\toolchain.json` 缺失 | 默认负例保留；显式原已接受路径可用，见 R1-02 |
| OBS-02 | 7 个 tracked 自有 DLL 仍嵌 `8fd7c6cb` | 历史 base 工件，非 candidate；最终构建强制重建并重绑 |
| OBS-03 | 冻结脚本不设置子进程 `TEMP/TMP/GRADLE_USER_HOME` | 交接命令在调用进程内固定到 `$root\build\<case>` |
| OBS-04 | `UnityVersion.txt` 路径缺失，summary 会记 UNKNOWN | 最终证据从 ProductVersion/ProjectVersion 取版本；不改门禁 |
| OBS-05 | pinned adb daemon 可继承 stdout 句柄，包装可能不退出 | JSON 完整后只停包装；daemon 不杀；可标 UNSEALED |
| OBS-06 | 无设备 / 无 APK / 未验证 license / 未做离线 restore | 设备 BLOCKED；其余 NOT_RUN，不推断通过 |

## 6. 最终候选构建前置门槛与命令

1. UI15 已在 `734b3775` 通过并发布，不在本 cde5 快照内；冻结 post-UI15/post-recovery candidate 后，重新绑定 tree/source/plugin 输入。任何合并后本 JSON 失效。
2. 显式使用原已接受 manifest：`-AndroidToolchainManifest 'D:\majiang-wt\android-toolchain-ds-20261027\build\android-toolchain\private\toolchain.json'`；先核对 SHA `E478...`、Ninja `E52A...`、CLI `F66B...`。默认本树路径缺失不是全局 blocker；不要复制、provision、重 pin 或下载/升级 SDK。
3. 提交两份 expectedFiles；build 脚本的 `Get-C05SourceBoundary` 会拒绝其 expectedFiles 之外的 staged/unstaged/untracked 路径。
4. 进程内设置 `TEMP/TMP/GRADLE_USER_HOME` 到 `$root\build\<case>`；不改系统环境。
5. 获取并持有 `Local\MahjongV5P45UnityValidation`；等待 30 s 失败即 BLOCKED，不启动第二个 Unity/Player。
6. 确认 Unity batchmode license/entitlement，且没有其他 Unity/Player 在运行。
7. 强制重建 7 个共享项目与 Google.Protobuf；`check-shared-plugins.ps1` PASS；bind post-build DLL SHA/commit；不得复用当前 `8fd7c6cb` DLL。
8. 用 C05 入口构建 APK；要求 Unity native exit 0、BuildReport Succeeded/errors 0、六场景、TMP、`com.majiang.game`、minSdk25/targetSdk36、ARM64-only、APK mtime 晚于 build start、aapt2 badging 与 report/project 一致。
9. 只有唯一 authorized online serial 才跑设备动作；无设备则 BLOCKED/NOT_RUN；不自动启模拟器，不用默认 adb。
10. 跑视觉/触控/返回/后台恢复/宽高比/安全区/弱网；iOS 无硬件则 NOT_RUN。
11. 保留首失败、native exit、逐例 XML、manifest/hash；禁止 skip/空测试/旧产物/Total0/删测试/弱化断言/刷新摘要获得绿色。

### 6.1 构建命令（绝对脚本路径；不依赖 cwd）

```powershell
$root = 'E:\majiang-review\<final-candidate-worktree>'
$buildScript = Join-Path $root 'tools/unity/build-p45-android-game.ps1'
$case = Join-Path $root ('build\c05-final-' + [guid]::NewGuid().ToString('N'))
$out  = Join-Path $case 'android-build'
$env:TEMP = Join-Path $case 'temp'
$env:TMP  = $env:TEMP
$env:GRADLE_USER_HOME = Join-Path $case 'gradle-home'
New-Item -ItemType Directory -Force -Path $case, $env:TEMP, $env:GRADLE_USER_HOME | Out-Null

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript `
  -UnityPath 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe' `
  -OutputRoot $out `
  -AndroidToolchainManifest 'D:\majiang-wt\android-toolchain-ds-20261027\build\android-toolchain\private\toolchain.json' `
  -MutexWaitSeconds 30 -UnityTimeoutSeconds 7200
$native = $LASTEXITCODE
if ($native -ne 0) { throw "C05 build native exit=$native" }
```

### 6.2 设备命令（同一 `$root`/`$case`；有 authorized serial 才执行）

```powershell
$runner = Join-Path $root 'tools/unity/run-p45-android-game.ps1'
$apk = Join-Path $out 'Mahjong.apk'
$serial = '<authorized-online-serial>'

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Devices -EvidenceDir (Join-Path $case 'device')
if ($LASTEXITCODE -ne 0) { throw "device discovery exit=$LASTEXITCODE" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action DeviceInfo -Serial $serial -EvidenceDir (Join-Path $case 'device-info')
if ($LASTEXITCODE -ne 0) { throw "DeviceInfo exit=$LASTEXITCODE" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial $serial -Apk $apk -EvidenceDir (Join-Path $case 'install')
if ($LASTEXITCODE -ne 0) { throw "Install exit=$LASTEXITCODE" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Start -Serial $serial -Apk $apk -EvidenceDir (Join-Path $case 'start')
if ($LASTEXITCODE -ne 0) { throw "Start exit=$LASTEXITCODE" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial $serial -Apk $apk -EvidenceDir (Join-Path $case 'device')
if ($LASTEXITCODE -ne 0) { throw "Capture exit=$LASTEXITCODE" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Stop -Serial $serial -Apk $apk -EvidenceDir (Join-Path $case 'stop')
if ($LASTEXITCODE -ne 0) { throw "Stop exit=$LASTEXITCODE" }
```

### 6.3 APK 校验命令

```powershell
$aapt2 = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\build-tools\36.0.0\aapt2.exe'
$badging = Join-Path $case 'apk-badging.txt'
& $aapt2 dump badging $apk *> $badging
if ($LASTEXITCODE -ne 0) { throw "aapt2 badging exit=$LASTEXITCODE" }
Get-FileHash $apk -Algorithm SHA256
Get-Content (Join-Path $out 'summary.json'), (Join-Path $out 'build-report.json') -Raw | ConvertFrom-Json
```

### 6.4 预期证据字段

| 文件 | 必须字段 |
|---|---|
| `summary.json` | status, sourceHead, dirtyPaths, inputSha256.taskInputs, inputSha256.productionInputs, unityVersion, unityPath, toolchainManifestSha256, androidToolchain, apk, apkBytes, apkSha256, nativeExit, buildReport, metadata, evidenceRoot, adbPresent |
| `build-report.json` | schema, result, errors, warnings, target, output, bytes, backend, architectures, packageId, minSdkVersion, targetSdkSetting, landscapeOnly, fullscreen, outsideSafeArea, scenes, tmpResources |
| `apk-metadata.txt` | package, minSdkVersion, targetSdkVersion, native-code |
| `unity.log` | native exit、首错误、BuildReport marker、license/entitlement 失败（如有） |
| `shared-build.log` | dotnet path、7 个项目 build、warnings/errors、copied outputs |
| `plugin-provenance.log` | input count/hash、7 own DLL hashes、19 outputs、provenance PASS |
| `failure-summary.json` | **实际冻结键**：status, sourceHead, dirtyPaths, inputSha256, toolchainManifestSha256, nativeExit, error, buildReportLine, apkExists, evidenceRoot, unityLog。`failedGate/firstError/evidencePaths` 当前脚本不产出，不能当作现有 schema；`evidenceRoot` 实际序列化为字面 `build/p45-android-game/<run>`，必须另存调用 run 目录 |

## 7. PASS / FAIL / BLOCKED / NOT_RUN

### PASS（本轮实际执行）

- HEAD `cde5da16`、branch、tracked clean（探针时）；accepted base/code/tested merge 三个祖先检查 exit 0。
- `db9e5d53..HEAD` 代码路径 diff exit 0；发布增量只有 C03 文档。
- Unity 6000.3.27f1 revision `672d562a42a7`；AndroidPlayer/IL2CPP/OpenJDK/SDK/NDK 存在。
- java / adb version / adb devices / cmake / clang / aapt2 版本检查 native exit 0。
- ProjectSettings/六场景/TMP/package/minSdk/target/ABI/IL2CPP/横屏/全屏/安全区静态合同核对通过。
- 源码树/10 scopes/16 关键文件/6 场景/7 own DLL/Google.Protobuf 全部 SHA 计算并可复算。
- 只读静态 shared input closure：150 inputs，aggregate `996CACB3...`；未 build。
- E: 证据/TEMP 写探针 PASS；磁盘实测完成；未改系统环境。
- 锁定 adb devices native exit 0、0 设备；C05 Devices JSON PASS。
- 原已接受显式 manifest/两个 helper 的只读 preflight native exit 0；默认本树路径缺失负例保留。
- 跨 cwd TOOL_FIXTURE PASS：fixture-root 绝对 build/device 脚本、root 内 output/evidence、native exit 0；未启动 Unity/设备。
- JSON 结构与 hash 一致性自检在本轮新证据根重新执行（结果见 §9；不覆盖旧 38/38 原件）。

### FAIL

- 无。R1 三条 MAJOR 已修复；默认 manifest 缺失是局部负例，不是失败。

### BLOCKED

- Android 设备矩阵：无 authorized online 设备；Install/Start/Stop/Capture/DeviceInfo NOT_RUN。

### NOT_RUN

- Unity batchmode license/entitlement。
- Unity Editor 启动、Android APK 构建、Windows Player、Gradle/Unity build cache。
- dotnet restore / 离线 restore 证明。
- 设备安装、启动、游戏、视觉、触控、返回、后台恢复、宽高比/安全区、弱网。
- iOS 构建/设备。
- 正式 post-UI15/post-recovery candidate 绑定与构建。
- C04 恢复后原请求对账、C04/C05/G2–G5 验收。

---

## 8. 失效与重绑

任一条件出现，本 JSON 立即失效，必须重算并重绑：

- candidate/source/Unity Assets/ProjectSettings/Packages/shared dotnet 输入/plugin DLL/build 脚本/场景/TMP 资源有任何提交或 dirty/untracked 变化。
- Unity editor/module、OpenJDK、SDK platform/build-tools、NDK、CMake、adb、已接受显式 manifest、Ninja、android-cli 有任何变化。
- UI15（已在 734b3775 发布）或 C04 recovery/其他增量合回 `cde5da16` 之后，或 PR20 publication head 变化。
- 设备 serial/OS/ABI/display 变化，或 APK 重建后 summary/build-report/metadata hash 不一致。
- 已接受的 C05 build/run/verification 脚本或六个生产场景变化。

---

## 9. 证据、验证与交付合规

**历史证据根**：`build/c05-android-prep-1a33507caee9/`（ignored，保持原 38/38 与首失败原件，不覆盖）。
**R1 证据根**：`build/c05-android-prep-r1-d114005f87be/`（ignored，新 GUID；本轮的 manifest preflight、TOOL_FIXTURE、validator 输出）。

| 证据 | SHA256 |
|---|---|
| R1 `logs/accepted-manifest-preflight.log`（显式原 manifest，native exit 0） | `B75BB362BC7BB2E7BB460EABEA52E8000AD9CD95B5584FDDF8FB76CB2B0FE4F4` |
| R1 `manifest-identity.json`（manifest/helper/JDK/SDK 身份） | `07A17B361821F1E8AD7068D6B45705F24FADF263E007A140195F366A5467CE61` |
| R1 `tool-fixture-results.json`（跨 cwd build/device 调用） | `E14E36608F7665143D0A929F6A6F3D27A4061DBB36B8F69580CE911B88C6AD93` |
| R1 `logs/tool-fixture.log` | `46D77CE9F1F5A7332E183B257E77D977614BBC9A55A36D02687C214BEBFFD470` |
| R1 `validation-results.json`（57/57 PASS，exit 0） | `751920C2711A5E2D1807FE71531D1276E22D3B6F825FA648CC98476D803859CB` |
| Caller `REVIEW.txt` | `14CF762D399E2111DC291ADB68830C58BC59CBFA5A692FB4DE58FED8D68E6E77` |
| Caller `independent-findings.json` | `1A42EBB8BCC79F541A1D9E9A938A6972981D883A78176CC2A0144C943DF8D615` |
| Caller `evidence-manifest.csv` | `9F9E3BA594341241325CA1DACC6BF58B382AC9E741A6FE95E2FBC13F2E6541E0` |
| 旧 `binding-facts.json` | `16DBC0937260D3B17D9766457C397D47E2AAC6005DF323715D209414BF6986A7` |
| 旧 `shared-input-closure.json` | `F2910668DC2D63266C0B6327FCEF70366B64ED313BCCE34CB62BCDF63D86C4C4` |
| 旧 `device-list.json` | `A6D72E4A0613407603BE90CBBCE6F545F2646F7DB4C3016B95DEC10B66C5E6DC` |
| 旧 `logs/device-discovery.log` | `2B6128B9583E7D83DA17073AB135CAA93500006AD07779AD3E95C8A36318BA07` |

`temp/adb.log` 1,224 B 由 pinned adb daemon 持有，**UNSEALED**，未 hash、未删除、未杀 daemon。

**首次错误/异常保留**（历史根，未覆盖）：

1. `ENV-PROBE-1` exit 1：`GetFullPath called with 1 argument: The path is not of a legal form.`（`build/c05-android-prep-1a33507caee9/logs/collect-env.log`）。探针脚本自身缺陷，不是环境失败。
2. `ENV-PROBE-2` 无输出 >600 s 后只终止本任务探针 PID；无 raw 输出，job `pwsh-437b6ef5d0f74730`。不是环境/产品失败。
3. `UNITYVERSION-LOOKUP` exit 1：路径不存在；对应 OBS-04。原错误 SHA256 `84881A9588FCC4BC1E6EE4142FCBAA35D9F3E028F9739533A5036E26C33867A1`。
4. `DEVICE-WRAPPER-EXIT`：JSON 完整后包装未退出，只停止包装，adb daemon 保留；对应 OBS-05。
5. R1 的 manifest 默认缺失负例（`Test-Path` False）与显式路径 PASS 同时保留；caller 初版探针 regex/expected-path 脚本错误为 caller 侧历史，不是 receiver 产品或文档失败。

**JSON 自检**：`build/c05-android-prep-r1-d114005f87be/validate-binding-r1.ps1 -NoWrite` 实际执行 **57/57 PASS、native exit 0**，覆盖精确 expectedFiles/旧别名、candidate 冻结身份、actual package/ABI null、原已接受 manifest/helper/preflight、绝对脚本/root 内 evidence、failure-summary 真实字段、3 条 R1 修复、UI15 快照后发布、150 输入、7 个非 candidate DLL、设备 none、scope/BOM/LF。验证结果 `validation-results.json` SHA256 `751920C2711A5E2D1807FE71531D1276E22D3B6F825FA648CC98476D803859CB`；JSON SHA256 为 `E07C052DF07C9B87FAF751AD1F5AF3D254814EE056DB5C2928B53EFCFEADCF7A`。该自检不替代任何产品测试、Unity 构建或设备结果。

**交付合规**：

- R1 最终 tracked delta 恰好是两个精确 expectedFiles（旧名以 `git mv` 移除）：`V5-G2-C05-ANDROID-CANDIDATE-READINESS-20261101.md` 与 `V5-G2-C05-ANDROID-CANDIDATE-BINDING-20261101.json`；无旧别名、无生产/测试/场景/ProjectSettings/packages/verifier/CI/scheduler 改动。
- R1 构建/日志/临时/证据都在 ignored `build/c05-android-prep-r1-d114005f87be/`；旧证据根保持原样，未混入暂存区。
- `git diff --cached --check` exit 0；交付区间 `cde5da16..HEAD` 与 `HEAD^..HEAD` 的最终 diff-check/clean 结果在交付回执给出。
- 全 base 区间 `8fd7c6cb..HEAD` 的 `git diff --check` 仍 exit 2：原因是 12 个**既有**历史文件（7 个 `doc/v5-重构/patches/*.patch` + 5 个历史 md）的尾随空白，均有原件、未修改、不属于本交付；本交付两个文件不在该清单。该首失败保留在历史证据根 `logs/base-diff-check.log`，R1 不修改这些原件。
- 不 push、不创建/修改 PR、不 record MR、不 approve、不合并 v5、不部署、不改 scheduler/complete.flag。
- 唯一 PR20 与独立复核发布由本任务 caller 回调处理；交付后结束，等待独立复核。

**下一 owner**：caller 指定的正式 C05 Android owner，在 post-UI15（734b3775）/post-recovery 候选冻结后重新绑定并构建；使用现有已接受显式 manifest 路径，无需新的 manifest pin/provisioning 批准。本接收方不自行批准、不继续跑构建、不代签 G2。

**报告自身 SHA256**：见交付消息/复核回执（避免文件内自引用；JSON 不含报告 SHA）。

## 10. Caller 独立验收（2026-11-01）

本准备任务通过，C05-PREP-R1-01/02/03 三组 MAJOR 均关闭。Receiver final 为 `3980c6f400080aa513deefb51567e5e1c82eb5c7`；提交时报告 SHA256 为 `C15D2FCA42A347E341237D269B5C4F787A068DC59266F4309C5647DCAA2B83AB`，JSON SHA256 为 `E07C052DF07C9B87FAF751AD1F5AF3D254814EE056DB5C2928B53EFCFEADCF7A`。Caller 将这两个授权文档合入已接受 UI15 publication `734b3775`，受检 merge 为 `0cc22831e3871e9601ddf8f8a534276b40ead4af`；生产源码、UI15 测试和工具均与此前 PR20 完全一致。发布提交仅补本节，不修改 JSON 绑定。

实际执行与证据位于 `E:/majiang-review/caller-c05-prep-r2-closeout-582b16e4/build/caller-review/`：

- 原 `validate-binding-r1.ps1 -NoWrite`：**57/57 PASS，native exit 0**；没有覆盖 receiver 原件。输出 SHA256：`668E1371C80EA59DEC6B3EA4D7676FE7C0FA9A5CA6CCF5A9D16F61D34DB18C0F`。
- Caller 独立检查：**32/32 PASS**。逐一重算 150 个 shared 输入、16 个关键文件、6 个场景、8 个插件文件、8 份 authority 文档及 22 份带摘要的证据；冻结 candidate tree、精确两文件范围、原始报告/JSON 摘要均一致。`independent-results.json` SHA256：`C9FFB4BA002EAB8C764BA584EA690B8E5016B39A068FAC0E55426A8C55A9E95C`。
- 实际执行未改动的只读 manifest/helper preflight：原已接受 `E478...` pin 和两个 helper 匹配；未创建真实构建输出、未运行 helper/Unity/APK/设备。
- 解析实际冻结脚本 AST，确认 failure-summary 的 11 个键与 JSON 完全一致；解析全部命令组，PS5.1 语法通过。
- 使用 JSON 中的原样 build/device 命令，仅将 `$root` 替换为带空格的独占 TOOL_FIXTURE 根，从另一 cwd 执行，确认构建及六种设备动作都选择 fixture 根下的绝对脚本和输出目录。fixture 只记录参数，没有 Unity、adb、APK 或设备副作用；成功路径 native0。另将 mock build 设为返回 17，原模板以 native1 拒绝继续，这是预期负例通过。

Caller 首次组装检查脚本时，嵌套 here-string 导致 PowerShell 解析失败；没有执行产品或写 receiver 证据。改用独立脚本文件后上述检查全部通过。该调用方包装错误不计作产品测试失败。

本次仅接受 **cde5 历史快照的环境调查、绑定与正式构建交接准备**。JSON 没有被重绑为当前 PR20 最终候选；UI15 和后续恢复增量合回后，正式 C05 owner 必须重新固定最终候选与输入。执行正式构建时由已接受 build 脚本取得并持有 Unity mutex，外层不要先持同名锁再跨进程调用脚本。没有新 pin/provisioning 授权依赖。

APK、Unity/Player、设备、视觉/触控、弱网、离线 restore 和 license 实测仍保留原 NOT_RUN/NOT_VERIFIED/无设备边界；不将 TOOL_FIXTURE 或 preflight 写成游戏结果。C04/C05/G2–G5 未获本次批准。使用唯一现有 PR20，保持 OPEN/Draft；不合并、不部署、不改调度，PR24 保持原状。
