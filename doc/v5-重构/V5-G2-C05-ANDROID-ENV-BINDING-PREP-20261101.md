# V5 G2-C05 Android 环境与候选绑定准备（2026-11-01）

## 0. 结论与边界

**性质**：本任务只做 Android 环境核对、候选源码/共享插件/工具链绑定、设备只读发现和下一步构建交接。**没有**运行 Unity/Player/Android 构建，没有安装/卸载/启动游戏，没有下载/升级共享 SDK，没有更改项目或机器设置，没有 push/PR/MR/approve/merge/deploy，也没有改 scheduler/complete.flag。

**结论**：

- 环境本体可用：Unity 6000.3.27f1、AndroidPlayer/IL2CPP、OpenJDK 17.0.20.1、SDK android-36/build-tools 35/36、NDK 27.2.12479018、CMake 3.22.1、锁定 adb 均存在且版本检查 native exit 0。
- 候选静态绑定完成：`8fd7c6cb`（accepted base）→ `b33f0aad`（C03 code）→ `db9e5d53`（caller tested merge）→ `cde5da16`（accepted publication）。`db9e5d53..HEAD` 的代码差异为 0，发布增量只有 C03 交付文档。
- **最终 APK 构建当前 BLOCKED**：独占新树没有已接受的私有 Android toolchain manifest，且该 manifest 的冻结 SHA 含绝对路径/生成时间，不能在本树重新生成后仍等于冻结值；不能复制别的 worktree 的私有 manifest 来“过闸”。详见 C05-PREP-D01。
- **设备矩阵 NOT_RUN_NO_DEVICE**：锁定 adb `devices -l` native exit 0，设备列表为空；没有自动启动模拟器，没有用系统 adb 替代，没有杀共享 adb daemon。
- 当前 7 个自有 plugin DLL 的 `ProductVersion` 仍嵌入 `8fd7c6cb`，不是本候选输出；最终构建必须强制重建并重新绑定（C05-PREP-D02）。
- 本 JSON/报告是 **UI15 15 项 PlayMode 失败修复与 C04 恢复对账增量合回之前**的准备候选绑定，不是最终 C05/G2 通过。C03 组件验收不等于 G2 通过；正式 C04/C05/G2–G5 未批准；V4 仍是唯一生产。

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
| worktree status at probe | tracked clean；交付前仅两个新增 expectedFiles |
| base→HEAD | 724 files changed, +254184/−1683；代码路径 `db9e5d53..HEAD` diff exit 0 |

**交付路径（恰好两个新增 tracked 文件）**：

- `doc/v5-重构/V5-G2-C05-ANDROID-ENV-BINDING-PREP-20261101.md`（本报告）
- `doc/v5-重构/V5-G2-C05-ANDROID-ENV-BINDING-PREP-20261101.json`（machine-readable binding）

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
| UnityVersion.txt | **缺失**（脚本预期路径 `Editor\Data\Resources\UnityVersion.txt`）；历史 summary 同样记 `unityVersion=UNKNOWN`，见 C05-PREP-D04 |

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

`device-list.json` 记录 `status=PASS`、`nativeExit=0`、`stdout=List of devices attached\r\n\r\n`、`stderr=* daemon not running...`、`devices=[]`，SHA256 `A6D72E4A0613407603BE90CBBCE6F545F2646F7DB4C3016B95DEC10B66C5E6DC`。外层 PowerShell 包装在 120 s 内未关闭（adb daemon 继承输出句柄），在 JSON 完整后只停止了本任务的包装进程，**没有杀共享 adb daemon**；这是已记录的 UNSEALED 限制，见 C05-PREP-D05。

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
| 私有 toolchain | `build\android-toolchain\private\toolchain.json`、`ninja\1.13.2\ninja.exe`、`android-cli\android-cli-sdkmanager16.cmd` **全部不存在**；构建脚本会在 Unity 启动前抛 `BLOCKED: accepted private Android toolchain manifest missing` |

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
| 私有 toolchain manifest | 脚本要求 SHA256 `E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934` |

**当前候选产物：全部 null + NOT_RUN**：

| 项 | 值 |
|---|---|
| APK | `null` |
| bytes | `null` |
| SHA256 | `null` |
| BuildReport / Unity log / native exit | `null` / `null` / `null` |
| package | `com.majiang.game`（配置值，非已构建产物） |
| ABI | `arm64-v8a`（目标，非已构建产物） |
| Install / Start / Visual / Touch / Weak-network / iOS | `NOT_RUN` / `NOT_RUN` / `NOT_RUN` / `NOT_RUN` / `NOT_RUN` / `NOT_RUN` |

历史 52,142,409 B / `0506AEEA...` 的 APK 只作为历史事实列出，`current=false`，**没有**被当作当前产物或当前构建证据。

---

## 5. 完整缺陷清单（一次给全）

### C05-PREP-D01 — BLOCKER — 私有 Android toolchain manifest 缺失且冻结摘要在新路径不可复现

- **位置**：`tools/unity/build-p45-android-game.ps1:176-185,789-823`；`build/android-toolchain/private/toolchain.json`。
- **原样复现**：

```powershell
Test-Path build\android-toolchain\private\toolchain.json
Test-Path build\android-toolchain\private\ninja\1.13.2\ninja.exe
Test-Path build\android-toolchain\private\android-cli\android-cli-sdkmanager16.cmd
Select-String tools/unity/build-p45-android-game.ps1 -Pattern "accepted private Android toolchain manifest missing|E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934|generatedAt|privateRoot|ninjaDir|androidCliBin|jdkHome|sdkRoot"
```

- **实际**：三个默认路径全部不存在；脚本在 line 178 抛 `BLOCKED: accepted private Android toolchain manifest missing`，发生在获取 Unity mutex / 启动 Unity 之前。历史 C05 开发 summary 使用的 manifest SHA 是 `E478...`，其 manifest 生成在 `D:\majiang-wt\g2-c05-android-game-luna6-20261101\build\android-toolchain\private`。该 worktree 的只读 check-only repair summary（2026-11-01T03:18）已记录 `manifest=null`、缺 private ninja、缺 CLI adapter；私有目录是临时产物且已不存在。manifest 写入内容含 `generatedAt` 与绝对 `privateRoot/ninjaDir/ninjaExe/androidCliBin/jdkHome/sdkRoot/cmake`，因此在本 E: 树重新生成**不可能**等于冻结的 D: manifest 摘要；复制别人的 manifest 虽可过 SHA，却会把构建指向别的 worktree。
- **期望**：本授权树（或 caller 批准的稳定共享私有根）内存在 manifest，SHA 是明确批准的新 pin，ninja/CLI/JDK/SDK 路径均落在授权根；不允许跨 worktree 路径，不允许静默 re-pin。
- **证据**：`binding-facts.json`；`logs/provenance.log`；外部只读历史 summary `62AD41746D25B3B40E889F35154CA02A7EFBF97B1B55AD0042C9F8577821716B`；外部只读 check-only summary `CF1D19BFF7A6DEC28A23272ABAE064615BD099774CD25088A356F04E5BD018D4`。
- **修复要求**：caller 必须二选一并记录：(a) 为最终 worktree/candidate 批准新的 manifest pin；或 (b) 批准稳定共享私有根并重新 pin。只允许用已接受的 repair 脚本 + 固定 Ninja/CLI 摘要来 provision；禁止复制别的 worktree manifest、禁止安装/升级共享 SDK、禁止在 manifest 缺失/不符时启动 Unity。
- **验收断言**：manifest 在绑定的路径存在；SHA256 等于 caller 批准值；`ninjaExe`/`androidCliBin` 存在且匹配 manifest 中的 hash；`jdkHome/sdkRoot/ndk/cmake` 均在授权根内；build preflight 通过该 gate；失败时没有启动任何 Unity 进程。

### C05-PREP-D02 — MAJOR — tracked 7 个自有 plugin DLL 仍嵌 accepted base commit，不是 C03 候选输出

- **位置**：`unity/Mahjong.Unity/Assets/Plugins/Mahjong/*.dll`。
- **原样复现**：

```powershell
Get-ChildItem unity/Mahjong.Unity/Assets/Plugins/Mahjong -Filter *.dll |
  ForEach-Object { [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductVersion }
git log -1 --format="%h %ad %s" -- unity/Mahjong.Unity/Assets/Plugins/Mahjong/Mahjong.Domain.dll
```

- **实际**：7 个自有 DLL 全部 `1.0.0+8fd7c6cb...`；candidate publication 是 `cde5da16`，code 是 `b33f0aad`。它们是 base 工件，不是 candidate 输出；若直接拿当前 DLL/当前 APK 作证据，会把 stale 输出冒充候选。
- **期望**：最终 Android 构建从冻结算入强制重建 7 个共享项目与 Google.Protobuf，逐 DLL 绑定 post-build SHA256 与 embedded commit 到最终候选，再让 Unity 打 APK。
- **证据**：`binding-facts.json`；`Mahjong.Domain.dll` SHA256 `01414649429B7435D2A00EEC812A73F0D3B0A544AE12F484E19972584AD66679`。
- **修复要求**：不得复用/提交 stale tracked DLL 作为候选证据。运行 `build-shared.ps1`、`check-shared-plugins.ps1`，记录 7 个 DLL 的新 SHA 与 ProductVersion commit；若 commit 不等于最终候选 HEAD（或 caller 批准的等价 code commit）或 provenance 不符，fail closed。C03 §15 的核对对象是 caller unified 构建输出，不是本树 current DLL。
- **验收断言**：post-build 7 个 DLL 时间戳新鲜；ProductVersion commit 等于最终候选 HEAD（或批准值）；SHA 与 provenance 一致；APK 内同一 DLL 字节一致；交付暂存区不包含这些生成 DLL。

### C05-PREP-D03 — MAJOR — 已接受构建脚本未把子进程 TEMP/TMP/GRADLE_USER_HOME 固定到 E: run root

- **位置**：`tools/unity/build-p45-android-game.ps1:198-236`；build invocation wrapper。
- **原样复现**：

```powershell
Write-Output $env:TEMP; Write-Output $env:TMP
Select-String tools/unity/build-p45-android-game.ps1 -Pattern "env:TEMP|env:TMP|GRADLE_USER_HOME"
Get-PSDrive C,D,E | Select-Object Name,@{n="FreeMiB";e={[math]::Round($_.Free/1MB,2)}}
```

- **实际**：宿主 TEMP/TMP 是 `C:\Users\sncdc\AppData\Local\Temp`；脚本只给 Unity 子进程设置 `ANDROID_CLI_BIN/JAVA_HOME/PATH`，没有设置 TEMP/TMP/GRADLE_USER_HOME。D: 只剩约 102.4 MiB。照文档直接跑会违反“新增 build/temp/cache/log/evidence 全在 E:”的约束，尽管 APK 输出路径在 E:。
- **期望**：最终构建的每个子进程、临时/cache/log/output 都在独占 E: ignored `build/<GUID>` case root；不改系统环境变量。
- **证据**：`binding-facts.json` 的 `environment.disks/processTemp`；`logs/provenance.log`。
- **修复要求**：调用进程先建 E: case root，设置 `$env:TEMP/$env:TMP/$env:GRADLE_USER_HOME`，验证可写，再以 `-OutputRoot` 指向同一 case root 调 `build-p45-android-game.ps1`。不要改机器/用户环境，不要把 cache 放 D:。
- **验收断言**：启动前子进程 TEMP/TMP/GRADLE_USER_HOME 均在 E: case root；构建后 D:/C: 没有新增 cache/temp/log 文件；所有证据仍在 E: case root。

### C05-PREP-D04 — MINOR — UnityVersion.txt 查找路径缺失，summary 会记 `unityVersion=UNKNOWN`

- **位置**：`tools/unity/build-p45-android-game.ps1:171-173`；历史 `summary.json`。
- **原样复现**：

```powershell
Get-Content D:\Unity\Hub\Editor\6000.3.27f1\Editor\Data\Resources\UnityVersion.txt
Get-Content D:\majiang-wt\g2-c05-android-game-luna6-20261101\build\p45-android-game\20261101-r2-clean-2ccc7295\summary.json |
  ConvertFrom-Json | Select-Object unityVersion
```

- **实际**：文件不存在，`Get-Content` exit 1；历史 accepted C05 summary 记录 `unityVersion: UNKNOWN`，但 Unity.exe ProductVersion 是 `6000.3.27f1_672d562a42a7`，ProjectVersion.txt 也记录 revision `672d562a42a7`。
- **期望**：最终 summary 记录真实版本/revision，或明确把 UNKNOWN 标为非权威字段，同时从 ProductVersion/ProjectVersion 绑定版本。
- **证据**：`logs/first-unityversion.err` SHA256 `84881A9588FCC4BC1E6EE4142FCBAA35D9F3E028F9739533A5036E26C33867A1`；历史 summary。
- **修复要求**：最终证据从 Unity.exe ProductVersion 与 ProjectSettings/ProjectVersion.txt 取版本；不要为这个展示字段改绿门禁，也不要把 summary `unityVersion` 当权威。
- **验收断言**：最终 summary/report 显示 `6000.3.27f1` 与 revision `672d562a42a7`，来源可复核；任何 UNKNOWN 字段显式标非权威。

### C05-PREP-D05 — MINOR — 锁定 adb 设备发现 JSON 有效，但外层包装因 adb daemon 继承 stdout 句柄不退出

- **位置**：`tools/unity/run-p45-android-game.ps1:26-39,98-104`；caller 输出管道。
- **原样复现**：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action Devices -EvidenceDir build/c05-android-prep-1a33507caee9/device 2>&1 |
  Tee-Object build/c05-android-prep-1a33507caee9/logs/device-discovery.log
```

- **实际**：打印设备列表、`DEVICE_STATUS=NOT_RUN_NO_DEVICE`、`DEVICE_EVIDENCE=build-relative`，并写出 `device-list.json`（status PASS / nativeExit 0 / devices=[]）。外层 PowerShell 在 120 s 内未退出；JSON 完整后只停止了本任务包装进程，adb daemon PID 66388 保留。该现象与既有 accepted 记录的 `real-devices.log` UNSEALED 一致。
- **期望**：有界设备发现返回可观察 exit code 与完整 JSON，不需要调用方杀共享 adb daemon；已知继承句柄限制标 UNSEALED，不当作设备失败。
- **证据**：`logs/device-discovery.log` SHA256 `2B6128B9583E7D83DA17073AB135CAA93500006AD07779AD3E95C8A36318BA07`；`device-list.json` SHA256 `A6D72E4A0613407603BE90CBBCE6F545F2646F7DB4C3016B95DEC10B66C5E6DC`。
- **修复要求**：最终设备动作不要通过长生命周期父管道；把包装输出重定向到文件，JSON 完整后关闭包装句柄；绝不杀共享 adb server；仍被继承就记 UNSEALED 并只凭已封存 JSON 继续。
- **验收断言**：设备动作在有界时间内返回 exit code 和完整 JSON，或明确 UNSEALED 且保留同样原始证据；共享 adb daemon 与其他设备操作保持存活。

### 5.1 非缺陷的明确缺口

| ID | 状态 | 内容 |
|---|---|---|
| G-DEVICE | **NOT_RUN_NO_DEVICE** | 没有 authorized online 设备；serial/API/ABI/model/insets 均无；Install/Start/Stop/Capture/DeviceInfo NOT_RUN。 |
| G-APK | **NOT_RUN** | 没有本候选 APK、BuildReport、native exit、aapt2 badging、安装/启动/视觉/触控/弱网/iOS 证据。 |
| G-OFFLINE | **NOT_RUN** | 只验证 NuGet/UPM cache 存在；没有实际 dotnet restore/Unity package resolve。 |
| G-LICENSE | **NOT_VERIFIED** | 没有启动 Unity，无法验证 batchmode entitlement。 |

---

## 6. 最终候选构建前置门槛

1. UI15 15 项 PlayMode 修复与 C04 恢复/原请求对账增量先合回；冻结最终 candidate SHA，重算本 JSON 的 tree/source/plugin 绑定。任何合并后本 JSON 失效。
2. 解决 C05-PREP-D01：caller 批准本 worktree 的新 manifest pin，或批准稳定共享私有根；不得复制别的 worktree manifest。
3. 提交本报告/JSON 两个 expectedFiles；build 脚本的 `Get-C05SourceBoundary` 会拒绝其 expectedFiles 之外的 staged/unstaged/untracked 路径。
4. 设置进程内 `TEMP/TMP/GRADLE_USER_HOME` 到独占 E: case root；不修改系统环境。
5. 获取并持有 `Local\MahjongV5P45UnityValidation`；等待 30 s 失败即 BLOCKED，不启动第二个 Unity/Player。
6. 确认 Unity batchmode license/entitlement，且没有其他 Unity/Player 正在运行。
7. 强制重建 7 个共享项目与 Google.Protobuf；`check-shared-plugins.ps1` PASS；bind post-build DLL SHA/commit；不得复用当前 `8fd7c6cb` DLL。
8. 用 C05 入口构建 APK；要求 Unity native exit 0、BuildReport Succeeded/errors 0、六场景、TMP、`com.majiang.game`、minSdk25/targetSdk36、ARM64-only、APK mtime 晚于 build start、aapt2 badging 与 report/project 一致。
9. 只有唯一 authorized online serial 才跑设备动作；无设备则 BLOCKED/NOT_RUN；不自动启模拟器，不用默认 adb。
10. 跑视觉/触控/返回/后台恢复/宽高比/安全区/弱网；iOS 无硬件则 NOT_RUN。
11. 保留首失败、native exit、逐例 XML、manifest/hash；禁止 skip/空测试/旧产物/Total0/删测试/弱化断言/刷新摘要获得绿色。

### 6.1 精确构建命令（模板；当前在 D01 处会 BLOCKED）

```powershell
$root = 'E:\majiang-review\<final-candidate-worktree>'
$case = Join-Path $root ('build\c05-final-' + [guid]::NewGuid().ToString('N'))
$out  = Join-Path $case 'android-build'   # 必须不存在；脚本拒绝复用
$env:TEMP = Join-Path $case 'temp'
$env:TMP  = $env:TEMP
$env:GRADLE_USER_HOME = Join-Path $case 'gradle-home'
New-Item -ItemType Directory -Force -Path $case, $env:TEMP, $env:GRADLE_USER_HOME | Out-Null

powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-p45-android-game.ps1 `
  -UnityPath 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe' `
  -OutputRoot $out `
  -AndroidToolchainManifest (Join-Path $root 'build\android-toolchain\private\toolchain.json') `
  -MutexWaitSeconds 30 `
  -UnityTimeoutSeconds 7200
```

当前 bare 复现（不启动 Unity，只证明 gate）：

```powershell
Test-Path build\android-toolchain\private\toolchain.json   # False
# build script 在此处抛 BLOCKED，未创建 Unity 子进程
```

### 6.2 设备命令（有 serial 才执行）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action Devices -EvidenceDir <E:\case>\device
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action DeviceInfo -Serial <serial> -EvidenceDir <E:\case>\device-info
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action Install -Serial <serial> -Apk <E:\case>\android-build\Mahjong.apk -EvidenceDir <E:\case>\install
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action Start -Serial <serial> -Apk <E:\case>\android-build\Mahjong.apk -EvidenceDir <E:\case>\start
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action Capture -Serial <serial> -Apk <E:\case>\android-build\Mahjong.apk -EvidenceDir <E:\case>\device
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 `
  -Action Stop -Serial <serial> -Apk <E:\case>\android-build\Mahjong.apk -EvidenceDir <E:\case>\stop
```

### 6.3 APK 校验命令

```powershell
$aapt2 = 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\build-tools\36.0.0\aapt2.exe'
& $aapt2 dump badging <E:\case>\android-build\Mahjong.apk *> <E:\case>\apk-badging.txt
Get-FileHash <E:\case>\android-build\Mahjong.apk -Algorithm SHA256
Get-Content <E:\case>\android-build\summary.json,<E:\case>\android-build\build-report.json -Raw | ConvertFrom-Json
```

### 6.4 预期证据字段

| 文件 | 必须字段 |
|---|---|
| `summary.json` | status, sourceHead, dirtyPaths, inputSha256.taskInputs, inputSha256.productionInputs, unityVersion, unityPath, toolchainManifestSha256, androidToolchain, apk, apkBytes, apkSha256, nativeExit, buildReport, metadata, evidenceRoot, adbPresent |
| `build-report.json` | schema, result, errors, warnings, target, output, bytes, backend, architectures, packageId, minSdkVersion, targetSdkSetting, landscapeOnly, fullscreen, outsideSafeArea, scenes, tmpResources |
| `apk-metadata.txt` | package, minSdkVersion, targetSdkVersion, native-code |
| `unity.log` | 原生命令/exit、首错误、BuildReport marker、license/entitlement 失败（如有） |
| `shared-build.log` | dotnet path、7 个项目 build、warnings/errors、copied outputs |
| `plugin-provenance.log` | input count/hash、7 own DLL hashes、19 outputs、provenance PASS |
| `failure-summary.json` | status、failedGate、nativeExit、firstError、evidence paths |

---

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
- JSON 结构/hash 一致性自检 **38/38 PASS**（见 §9；这是报告完整性检查，不是产品/Unity/设备测试）。

### FAIL

- C05-PREP-D04 MINOR：`UnityVersion.txt` 路径缺失，summary 会记 `UNKNOWN`（历史 accepted summary 也是 UNKNOWN）。

### BLOCKED

- C05-PREP-D01 BLOCKER：私有 toolchain manifest 缺失且冻结摘要不可在新路径复现；Unity/APK 构建不能开始。
- Android 设备矩阵：无 authorized online 设备；Install/Start/Stop/Capture/DeviceInfo NOT_RUN。
- 最终候选 APK：NOT_RUN（pre-increment + D01 + 无设备）。

### NOT_RUN

- Unity batchmode license/entitlement。
- Unity Editor 启动、Android APK 构建、Windows Player、Gradle/Unity build cache。
- dotnet restore / 离线 restore 证明。
- 设备安装、启动、游戏、视觉、触控、返回、后台恢复、宽高比/安全区、弱网。
- iOS 构建/设备。
- C04 恢复后原请求对账、UI15 15 项修复——均为独立增量。

---

## 8. 失效与重绑

任一条件出现，本 JSON 立即失效，必须重算并重绑：

- candidate/source/Unity Assets/ProjectSettings/Packages/shared dotnet 输入/plugin DLL/build 脚本/场景/TMP 资源有任何提交或 dirty/untracked 变化。
- Unity editor/module、OpenJDK、SDK platform/build-tools、NDK、CMake、adb、私有 manifest、Ninja、android-cli 有任何变化。
- UI15/recovery 或其他增量合回 `cde5da16` 之后，或 PR20 publication head 变化。
- 设备 serial/OS/ABI/display 变化，或 APK 重建后 summary/build-report/metadata hash 不一致。
- 已接受的 C05 build/run/verification 脚本或六个生产场景变化。

---

## 9. 证据、验证与交付合规

**证据根**：`build/c05-android-prep-1a33507caee9/`（ignored）。

| 证据 | SHA256 |
|---|---|
| `binding-facts.json` | `16DBC0937260D3B17D9766457C397D47E2AAC6005DF323715D209414BF6986A7` |
| `shared-input-closure.json` | `F2910668DC2D63266C0B6327FCEF70366B64ED313BCCE34CB62BCDF63D86C4C4` |
| `logs/collect-env.log`（首失败） | `25F873AF44BECA6C74128256CD3A4B950544A93FDFB4A0CF0D9B02000858FCF5` |
| `logs/chunk1.log` | `814599B6272B6FF4703616BA0F4FFE15D0AFC16588343595A9F62EA35A66DEE0` |
| `logs/chunk2.log` | `2FB6E72D67C35FCD8E25C604AEEF7315BF667AFBADA301F280579075849CBEE1` |
| `logs/provenance.log` | `A792A1F85EEE561A05A25D89C142E6A741C44CA77469D0DD48BC5B04D46C68AA` |
| `logs/java.err` | `2C05C59B13A701146C37D79936303EB8080CD353E096756DFAB1CD833A306423` |
| `logs/adb-version.out` | `8CCEA4420D6846FE79AD8D98FB1447AF4BD1324FDB4DB418B73C97D949AF4692` |
| `logs/adb-devices.out` | `BBFCC7239A6F1BE3544814EBDC6BCC9B66BF5B1F0B03BAA3340808F19265A630` |
| `logs/adb-devices.err` | `1AC362B61180A2AFBEEABEC06AED462AE82740149358C7A2EC926BE3E1B1ECFF` |
| `logs/first-unityversion.err` | `84881A9588FCC4BC1E6EE4142FCBAA35D9F3E028F9739533A5036E26C33867A1` |
| `logs/device-discovery.log` | `2B6128B9583E7D83DA17073AB135CAA93500006AD07779AD3E95C8A36318BA07` |
| `device/20261101-125149-8f614c3f/device-list.json` | `A6D72E4A0613407603BE90CBBCE6F545F2646F7DB4C3016B95DEC10B66C5E6DC` |
| `validation-results.json` | `5F26915833A9B0B21F2A28E9BF49552F4508831BB0C9C6CFE680CDABCAEDFB8F`；38/38 PASS |
| `logs/base-diff-check.log`（全 base 区间 exit 2，12 个既有文件） | `01EA8CEE25637836721193A00E53D0DAD84DEDE773160E84888AE48D9989101D` |
| `logs/show-check.log`（`git show --check HEAD`） | `61A66BCCDA45CED731689B6C9DC26DB1ACF1168B16866405BDF45C6E6FE24D89` |

`temp/adb.log` 1,224 B 由 pinned adb daemon 持有，**UNSEALED**，未 hash、未删除、未杀 daemon。

**首次错误/异常保留**：

1. `ENV-PROBE-1` exit 1：`GetFullPath called with 1 argument: The path is not of a legal form.`（`logs/collect-env.log`）。这是本任务探针脚本自身缺陷，不是环境失败；已改用有界直接探针。
2. `ENV-PROBE-2` 无输出 >600 s 后只终止本任务探针 PID；无 raw 输出，job `pwsh-437b6ef5d0f74730`。不是环境/产品失败。
3. `UNITYVERSION-LOOKUP` exit 1：路径不存在（`logs/first-unityversion.err`）；对应 C05-PREP-D04。
4. `DEVICE-WRAPPER-EXIT`：JSON 完整后包装未退出，只停止包装，adb daemon 保留；对应 C05-PREP-D05。

**JSON 自检**：`validate-binding.ps1` 实际执行 **38/38 PASS**，覆盖 commit/祖先/发布增量、APK null、150 输入逐文件 SHA、10 scopes、6 场景、8 plugin、全部 evidence/authority SHA、repo manifest 重算、结构/状态分离、无 BOM/LF。验证结果写入 `validation-results.json`；JSON SHA256 为 `182288AAAA340BA8DCCBF77773F26AFF352388CE3A63B69CC9814A73897FA65E`。该自检不替代任何产品测试、Unity 构建或设备结果。

**交付合规**：

- tracked 变更恰为两个新增 expectedFiles；没有生产、测试、场景、ProjectSettings、packages、verifier、CI、scheduler 改动。
- 构建/日志/临时/证据都在 ignored `build/c05-android-prep-1a33507caee9/`，未混入暂存区。
- `git status --porcelain` 仅两项；`git diff --cached --check` exit 0；`git show --check HEAD` exit 0；交付区间 `cde5da16..HEAD` 与 `HEAD^..HEAD` 的 diff-check 均 exit 0。
- 全 base 区间 `8fd7c6cb..HEAD` 的 `git diff --check` exit 2：原因是 12 个**既有**历史文件（7 个 `doc/v5-重构/patches/*.patch` + 5 个历史 md）的尾随空白，均有原件、未修改、不属于本交付；本交付两个文件不在该清单。该首失败已保留在 `logs/base-diff-check.log`。
- 不 push、不创建/修改 PR、不 record MR、不 approve、不合并 v5、不部署、不改 scheduler/complete.flag。
- 唯一 PR20 与独立复核发布由本任务 caller 回调处理；交付后结束，等待独立复核。

**下一 owner**：caller 指定的正式 C05 Android owner，在 UI15 与 C04 recovery 增量合回、且 caller 对私有 toolchain manifest pin/provisioning 作出决定之后。本接收方不自行批准、不继续跑构建、不代签 G2。

**报告自身 SHA256**：见交付消息/复核回执（避免文件内自引用；JSON 不含报告 SHA）。
