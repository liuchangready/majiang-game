# V5 G2-C03 选择性统一与单一冻结候选复验交付（2026-11-01）

日期：2026-11-01（Asia/Shanghai）。执行者：DeepSeek Harness（`deepseek/deepseek-v4.1-flash`，未改模型）。
工作树：`E:/majiang-review/g2-c03-unification-ds-20261101-0943`
分支：`wt/v5-g2-c03-unification-ds-20261101-0943`

结论口径（先读）：

- 本交付是**已接受组件的集成候选复验**，不是 G2/C03 通过，不是最终 G2 裁决，不是 C01/C02 重启，也不批准 P4.5/G2–G5。V4 仍是唯一生产架构。
- 14 文件 accepted exporter 增量已按源提交精确应用；`bae6..b33f0aad` 只改这 14 文件，`diff-check=0`，没有回退 C01/C02/C05，没有合并 PR24 整分支或旧 driver。
- 冻结代码 `b33f0aad21c962a6f147dbfdb8a63fd98942d4a2` 的定向 .NET/工具/12 局 Player/8 场景受控恢复均有本轮原件。**统一全量 .NET 门禁因无 PostgreSQL 而 FAIL（3 个 PG 依赖行），全量 PlayMode 因 15 个既有 UI probe 失败而 FAIL**；两者均未被改写成 PASS。
- 旧 RecoveryExport Compare 在候选上保持 **BLOCKED / exit 17 / pass=false / factsEqual=true / UNTRUSTED_LOCAL**；负例矩阵 22/22。旧 Compare 的历史 `NO_NON_FORGEABLE_COLLECTOR_ATTESTATION` 门禁没有被删除或放宽。
- 受控恢复 8 场景全部是**本轮同一 Player、同一冻结源码的 GAME 采集**：collector exit 0、offline verifier exit 0、collection chain PASS、`gamePositive=true`；`callerAcceptance` 一律 `PENDING_INDEPENDENT_REVIEW`，本报告不自行接受。
- 不声称 C04 的恢复后 outbox 实际重提/零重复副作用已经覆盖；不声称 Android/adb/iOS/IL2CPP/远端正例、当前头 CI 或 G2–G5 通过。
- 未 push、未创建/修改 PR、未 record MR、未 approve、未 merge v5、未部署、未改 scheduler、未创建 `complete.flag`。

---

## 1. 输入、基线与来源 SHA

### 1.1 工作树与基线

| 项 | 值 |
|---|---|
| 起始 HEAD / 当前 PR20 候选 | `bae6b8b0c98c9ed80256373060a9e754d6fc4296` |
| 分支 | `wt/v5-g2-c03-unification-ds-20261101-0943` |
| 起始 status | clean（`git status --porcelain` 空），`diff-check=0` |
| accepted `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（仍为该值） |
| accepted exporter 共同基线 / merge-base | `2a3a359412fd555bcbb78dec119531c3bde35713` |
| accepted exporter 最终提交 | `6750b4362063169cd435302e0cc9c6685325bbe5` |
| PR24（仅历史暂存，绝不整体合并） | `81fa51bc43bbab4b33cbae7ad6f003e082892f3a`，基分支 `feat/v5-post-g1-batch` |
| C01 唯一后继 final / 代码 | `5d103e3614e09fc7d4632b352c7944b13b6b1ce7` / `aec0c9a5` |
| C01 收入 PR20 | `1eb029d34378a587bc36b812baae4cef26f16665` |
| C02 final / R5 实现 | `699bbaf4989245c487cb6b40f1e6418153242882` / `6cae2910` |
| C02 收入 PR20 | `c43187ef`，独立接受 `60dec69a` |
| C05 final / 收入 PR20 | `a215f2d95d2747735c059425336187630442b30c` / `d1acb120` |
| 本交付冻结代码 | `b33f0aad21c962a6f147dbfdb8a63fd98942d4a2` |

### 1.2 已读调度与规则

- `D:/majiang/doc/v5-重构/V5-G2-CLOSEOUT-TASKS-20261031.md` 顶部 09:46 记录（以及 09:15/05:18/04:45/04:24 相关条目）。
- 本树 `AGENTS.md`、`CLAUDE.md`。
- `doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`（P4/P4.5 相关章节）。
- `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md` 的 P4.1–P4.5/G2。
- `doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`。
- `doc/v5-重构/CODEX-V5-G2-C01-ACCEPTANCE-20261101.md`、`CODEX-V5-G2-C02-ACCEPTANCE-20261101.md`、`CODEX-V5-G2-C05-ACCEPTANCE-20261101.md`。
- accepted exporter 交付：`doc/v5-重构/V5-P4.5-RECOVERY-EXPORT-DELIVERY-20261031.md`。

### 1.3 环境与硬件（实际值）

| 项 | 值 |
|---|---|
| 主机 | `DESKTOP-U2CRNCD`，HUAWEI `MRGF-XX` |
| OS | Microsoft Windows 10 专业版 10.0.19045，build 19045 |
| CPU / RAM | 12th Gen Intel Core i7-1260P，12 核 / 16 逻辑；15.7 GB |
| GPU | Intel Iris Xe Graphics（无独立 GPU） |
| .NET SDK | `8.0.426`，`D:\Unity\dotnet\dotnet.exe` |
| PowerShell | Windows PowerShell 5.1.19041.6456（Desktop） |
| Unity | `D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe` |
| PostgreSQL | **无服务、5432 不可连**；三个 PG 测试工程因此 fail-closed |
| 磁盘 | 工作全部在 E:；D: 仅读取既有 baseline，未写新大产物 |

---

## 2. 选择性统一：14 文件精确增量

### 2.1 来源 delta（merge-base → accepted exporter）

```powershell
git diff --stat 2a3a359412fd555bcbb78dec119531c3bde35713..6750b4362063169cd435302e0cc9c6685325bbe5
# 14 files changed, 5207 insertions(+), 117 deletions(-)
```

14 文件与本树最终 blob 全部逐字节等于 `6750b436`：

| 文件 | 类型 | HEAD blob（=6750） |
|---|---|---|
| `doc/v5-重构/V5-P4.5-RECOVERY-EXPORT-DELIVERY-20261031.md` | A | `4ef50c94` |
| `dotnet/src/Mahjong.LocalHost/LocalTableHost.cs` | M | `dca3d4ba` |
| `dotnet/src/Mahjong.LocalHost/LocalTableRecoveryExport.cs` | A | `dcd92516` |
| `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` | M | `90b3c988` |
| `dotnet/tests/Mahjong.LocalHost.Tests/P45RecoveryExportTests.cs` | A | `b8292c7d` |
| `dotnet/tools/P45RecoveryVerifier/P45RecoveryVerifier.csproj` | A | `c23632e6` |
| `dotnet/tools/P45RecoveryVerifier/Program.cs` | A | `24a44b5c` |
| `tools/tests/VerifyP45RecoveryExport.Tests.ps1` | A | `fffd6adf` |
| `tools/unity/p45-e2e-common.ps1` | M | `44089b80` |
| `tools/unity/p45-e2e-evidence.schema.json` | M | `e8dc3d63` |
| `tools/unity/run-p45-e2e.ps1` | M | `a3f0625f` |
| `tools/unity/run-p45-recovery-export.ps1` | A | `272e3385` |
| `unity/Mahjong.Unity/Assets/Scripts/App/P45RecoveryExportProbe.cs` | A | `77a0479d` |
| `unity/Mahjong.Unity/Assets/Scripts/App/P45RecoveryExportProbe.cs.meta` | A | `6c559593` |

### 2.2 为什么没有整文件覆写掉后来代码

应用前先核对五个既有文件在共同基线 `2a3a359` 与当前 `bae6` 的 blob；它们**完全相同**，说明这些路径上没有后来接受的 match/恢复/控制改动可被覆盖：

| 文件 | 2a3a359 blob | bae6 blob | 相同 |
|---|---|---|---|
| `LocalTableHost.cs` | `b270ca6a19185025578f99b82d37c53d608dfc1f` | 同左 | 是 |
| `LocalTableRuntime.cs` | `49369157172f27a51063348021211723d9db5241` | 同左 | 是 |
| `tools/unity/p45-e2e-common.ps1` | `47d2d495a3451d989fbf462a38369f59a5212549` | 同左 | 是 |
| `tools/unity/p45-e2e-evidence.schema.json` | `0dbb7d3dfc926985be4ce05a62669852189d846f` | 同左 | 是 |
| `tools/unity/run-p45-e2e.ps1` | `e0fe4d6f8cc7a510aa08c98cb684ed24f7b9ca34` | 同左 | 是 |

后来接受的 C01/C02/C05 代码位于其它路径；本提交没有触碰它们。`git diff --name-only bae6..b33f0aad` 恰为 14 个文件，没有 `LocalMatchCoordinator`、`TableActor`、`TableService`、C02 四文件或 C05 六文件。

### 2.3 冻结提交与精确 diff

```powershell
git commit -m "feat(v5): integrate accepted P4.5 recovery export into G2-C03 candidate"
# b33f0aad21c962a6f147dbfdb8a63fd98942d4a2

git diff --stat bae6b8b0c98c9ed80256373060a9e754d6fc4296..HEAD
# 14 files changed, 5207 insertions(+), 117 deletions(-)
git diff --check bae6b8b0c98c9ed80256373060a9e754d6fc4296..HEAD
# exit 0
```

- 代码冻结 SHA：`b33f0aad21c962a6f147dbfdb8a63fd98942d4a2`
- 测试/工具冻结 blob（节选）：`P45RecoveryExportTests.cs=b8292c7d7b72310ef63f0a276066debf22a3a45c`；`P45RecoveryVerifier/Program.cs=24a44b5c6d8369905ec09c0b44d8c902c90a0f7f`；`VerifyP45RecoveryExport.Tests.ps1=fffd6adf9d9e49042fa87c599bf5106b0cfa1d4a`；`run-p45-recovery-export.ps1=272e3385153de23edd811a06af8f81a170ccbc2b`；`VerifyP45ControlledRecoveryCollection.Tests.ps1=144a70756c4c2a7c6f471c631eff57e7474ac950`；`run-p45-controlled-recovery-collection.ps1=210f14c8a348cdcef5eb836214a3a15867d4b448`；`verify-p45-controlled-recovery-collection.ps1=8ee62d03e1b2ed9da8b98d8f9803b62614a7185f`。
- 报告 SHA：见 §14 的交付说明（报告无法自引用自身字节 SHA；提交后由 `git hash-object`/SHA-256 记录在交付消息）。
- 所有临时脚本、原始日志、下载与构建均在 ignored `build/g2-c03-20261101-ds/`，未提交。

---

## 3. 编译器与 .NET 定向门禁

### 3.1 in-tree verifier fresh Rebuild（不依赖另一 worktree DLL）

```powershell
D:\Unity\dotnet\dotnet.exe build dotnet/tools/P45RecoveryVerifier/P45RecoveryVerifier.csproj -c Release -t:Rebuild
# native exit 0；0 warnings / 0 errors
```

- `P45RecoveryVerifier.dll` SHA-256：`387409D20C7B7921470D99BBB3B078A8108422D32A624F8D826CFB123B5626A9`
- Rebuild log：`build/g2-c03-20261101-ds/logs/verifier-rebuild.log`，SHA-256 `12C790E2E245144C6903A0C96A0C9DE2574356796FC024D41BEE0BA6359CD3C5`
- 默认 verifier 对 accepted fixture 只读解码成功（native 0）：authority 16515 B，round `local-main-r1`，v3/seq6，物理 136/unique136，ledger source `CURRENT_COMMITTED_ACTOR`；输出 `logs/verifier-sample.log`。

### 3.2 明确受影响 .NET 用例（在冻结 `b33f0aad` clean HEAD 上重跑，Release + 原生 XML）

| Suite | 命令要点 | native | 真实计数 | 原生 XML SHA-256 |
|---|---|---:|---:|---|
| `Mahjong.LocalHost.Tests` | `dotnet test ... -c Release -- --report-xunit-xml ...` | 0 | **134/134**，fail0/skip0（含 7 个 exporter 定向） | `6CBC52DF835C30EB1DA02AD1DBD82B6DF5D66C08C1595EC92ABCF058007D22F5` |
| `Mahjong.TableRuntime.Tests` | 同上 | 0 | **483/483** | `6B35AB83FF5B6176F3F2DC2DF928C9DECA304C08CC6D48513A096422CEB4E8CD` |
| `Mahjong.Unity.TableModel.Tests` | 同上 | 0 | **235/235** | `CE9AEF070C85C91E9A3D5A4F2DC9EF9590EAB5A387D02D5A92807D9EFA72830F` |
| `Mahjong.TableService.Tests` | 同上 | 0 | **203/203** | `DC4F31F68EF427C5F28AB8142AD205F1CDBA7B518C2C932C643CB441736E64CF` |

合计 **1,055/1,055**，0 fail / 0 skip。7 个 exporter 用例为：`CaptureUsesOnePersistedVersionAndSurvivesColdReopen`、`PreparedCaptureRunsBeforeDriverAndIsTakenOnce`、`TamperedAuthorityEnvelopeIsRejected`、`CurrentCheatAggregateIsRequiredAndCarriesNonEmptyLedger`、`PersistedControlResultsAreExportedWithCanonicalIdentityAndDigest`、`ReleaseControlThroughPublicSessionIsPersistedAndExported`、`TileMarkThroughPublicSessionExportsCurrentNonEmptyLedger`（XML 内均 `Pass`）。

原始 XML 在 `build/g2-c03-20261101-ds/tests/*-final-xml/`（冻结 HEAD 重跑），提交前首次运行 XML 保留在 `tests/xml/`；测试日志在 `logs/`。

### 3.3 统一 .NET 门禁：实际执行、实际 FAIL（PG 环境缺失）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -LogDir build/g2-c03-20261101-ds/verify-v5
# native exit 1；OVERALL: FAIL
```

- 21 个项目；18 行 PASS、3 行 FAIL（脚本口径不是 BLOCKED）。
- 实际执行 **2,820** 个 case：**2,746 pass / 62 fail / 12 skipped / 0 errors / 0 not-run**。
- 3 个 FAIL 行全部是 PostgreSQL 依赖，不是 C03 代码回归：
  - `Mahjong.TableService.DurableHost.Tests`：59 total，**38 pass / 21 fail**；`BLOCKED: MAHJONG_P54B_TEST_PG_ADMIN is required ... This assembly deliberately has no skipped/missing-PG fake-green path.`（`DurableTableHostEnvironment.cs:163`）。
  - `Mahjong.Infrastructure.Tests`：42 total，**1 pass / 41 fail**；`BLOCKED: MAHJONG_P52_TEST_POSTGRES_ADMIN is required ...`（`PostgresTableTestEnvironment.cs:182`）。
  - `Mahjong.LobbyService.Tests`：89 total，**77 pass / 12 skipped**；缺 `MAHJONG_LOBBY_TEST_POSTGRES`（`PostgresLobbyTestSupport.cs:30`），统一门禁把 skip 判 FAIL。
- 证据：`verify-v5/summary.log`、`verify-v5.log`（SHA `8D94561420CAF4AE7EF131C09B6044548CE011215DFFBD65DAB7CE3AF0A4B22B`）、逐工程日志与原生日志；未启动/安装 PostgreSQL、未改环境变量、未把 skip 改写为 pass。
- 统一门禁结论：**FAIL（环境 BLOCKED，见 C03-D-02）**；定向 .NET 结果不受影响。

---

## 4. 现有工具入口与负例（全部实际执行）

| 入口 | 命令要点 | native | 真实计数 | 证据 |
|---|---|---:|---|---|
| E2E 基础工具 16 项 | `VerifyP45E2ETooling.Tests.ps1 -OutputDir .../tool-tests/e2e` | 0 | **16/16**，fail0/skip0 | summary SHA `53A73D3510D8BFA337B7BD18230FC4F1EFF960914072B67B4A7C530185923590`；log SHA `D13E953B...` |
| C01 原工具套件 | `VerifyP45FullMatchDriver.Tests.ps1` | 0 | **32/32** | log SHA `674AA3E538C566EAA97B760FD3DBD06587A97F4445F6FE6EF915CCB1F44B8B2C` |
| C02 控制恢复原 27 case | `VerifyP45ControlledRecoveryCollection.Tests.ps1 -VerifierPath <in-tree DLL>` | 0 | **27/27**，skip0 | summary SHA `95C4F434E38398209E632271D9DE5420D2FBEE9E6C239023BB2A24D4A2A589E2`；log SHA `0C60A996...` |
| C05 工具断言（不跑 Android/adb） | `VerifyP45AndroidGame.Tests.ps1` | 0 | **29 条命名断言**；含 36/36 无效 Install/Start/Stop 前置阻断、两个完整隔离 run | log SHA `DCD00AA91168579D6DE486469C329A424FB94EA3AADEFBD0E089940535C6508C` |
| accepted RecoveryExport 负例入口 | `VerifyP45RecoveryExport.Tests.ps1 -RunDir <clean-prestate run>` | 0 | **22/22** | summary SHA `37D9AE2C7F5F441B7E4FC042031B010694C14562E4C6619F64241D59B15423F4`；log SHA `044FC46D...` |

说明：

- C02 27-case 首次在尚未提交的 staged 树上执行时，clean-source 回归项失败，得到 **26/27**（log SHA `F1C15A4FA2A9E4AF74BD892485BE799CE5D54E50F0101FF76EE932F3A0FBB25A`）；冻结提交后在 clean tracked 树重跑 **27/27**。首失败保留在 `logs/c02-tests.log`。
- accepted RecoveryExport 负例首次在生成 dirty 树上执行时，旧链因“sealed run 不是 clean BUILD_LOG_ATTACHED”在更早门禁 BLOCKED，得到 **21/22**（log SHA `EEF9475B581ABF38FC5B2B43E9468A41FB877374D84D2E179106DB9729BDAE3C`）；在构建/采集结束后将生成 tracked 输出恢复为 HEAD 字节，再跑 clean-prestate 得 **22/22** 与 UNTRUSTED_LOCAL。play 仍为同一 `b33f0aad` 构建，构建时 dirty 已绑定（见 §8、§10）。
- C05 只执行工具/fixture 断言；**没有**真实 Android 构建、安装、启动、截图或 adb 设备操作。

---

## 5. Unity PlayMode 与 Windows Player

### 5.1 共享插件 fresh build / provenance

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-shared-plugins.ps1 -Configuration Release
```

- build native 0；**150 inputs / 19 outputs**；log SHA `1620F929863E55F36CDA3003B8A5CC32756CA5B113E880001B2EAFE94E67BF3B`。
- provenance native 0；`SHARED_PLUGIN_PROVENANCE: PASS`；provenance.json SHA `3FFDB76620B7575E3FAA6623CAA714CAEFF2178599DCC2683DF4B43C4A54DE77`；log SHA `62F3BA41D1091BEEE58E43FAA3C0299D55B6A0BE1662A4EAE67985B3E2D4ADAA`。
- 全部 Unity 操作均持有 `Local\MahjongV5P45UnityValidation` 命名互斥；没有杀其他 Unity/Player 进程。

### 5.2 PlayMode 实际结果

| 运行 | 命令/条件 | native | 结果 | XML SHA-256 |
|---|---|---:|---|---|
| 首次（插件未同步） | `-batchmode -nographics -runTests -testPlatform PlayMode` | 1 | 编译失败：`TableVisibleEventBatch` 未在旧 plugin DLL；未生成 XML | log `7226389D6E6A88A4D38A926A1428773E9BE482E4FD556CD2FB98DF2F65BBBAAF` |
| 全量 `-nographics` | 同上（fresh plugin 后） | 2 | **147 total / 117 pass / 30 fail**；TMP importer + RenderTexture 无图形设备 | `0E85ECA47D6698FB37CE428090037E4E1C5C67E18B9D2D7AA64F95ED7EE25FC9` |
| 全量（有图形设备） | `-batchmode -runTests -testPlatform PlayMode`（不加 `-nographics`） | 2 | **147 / 132 pass / 15 fail / 0 skip**；失败均位于既有 `TablePlayModeTests` 的 UI probes（C03 未改该文件；未建第二工作树做 base rerun） | `36B332C646FE2A92B5E1A4E71D1A347887CB93E7F4CD6DAAF82F5F968E875E98` |
| C01 执行边界套件 | `-testFilter Mahjong.Unity.Tests.PlayMode.LocalFullMatchDriverPlayModeTests`（有图形） | 0 | **7/7** | `ADBF0B1CA1D1E8D6859168527CA3E999F11706355417CD7C1642237067E5B964` |
| 控制 UI 套件 | `-testFilter Mahjong.Unity.Tests.PlayMode.TableControlPlayModeTests`（有图形） | 0 | **2/2** | `DA2FC43E88676D72BABA87200F7217390FB5B631ED1237BCC194F94F2521AA5A` |
| 控制 UI（首次 `-nographics`） | 同上，TMP importer 干扰 | 2 | 2 total / 1 pass / 1 fail | `97D2661E3F2249BB5E19F43389A6B1995EC93D3555F4F43EE474AA36730DB071` |

**全量 PlayMode 不绿**是候选层的真实事实，见 C03-D-01；C01 相关 7/7 与控制 UI 2/2 已通过，不能把“全量 132/147”写成 147/147。

### 5.3 Windows Player 构建

```powershell
D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe -batchmode -nographics -quit `
  -projectPath unity/Mahjong.Unity `
  -buildWindows64Player build/g2-c03-20261101-ds/fullmatch/player/Mahjong.exe `
  -logFile build/g2-c03-20261101-ds/fullmatch/logs/unity-build.log
```

- Unity native **0**；`Build Successful` / `Build Finished, Result: Success.`；build log SHA `67D965E6E503C4A30F6838967EA372CC797D3C5E177E3B90379FAF05A5181243`；exit record SHA `B28CE7C4317C904708DEEB9F0C4B5B0ED405AED27F82F771B2FCFE318827E11E`。
- Player：**205 files / 128,462,754 B**；`Mahjong.exe` SHA `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；package manifest SHA `8335C4249A89F2C463B9702C93ED2A62CF11F083FDB7A3985F32B374D678C7CC`；`player-files.json` 完整 205 条路径/长度/SHA。
- 构建时源码 `b33f0aad`，tree `a34c6a7f7d810ea99384bb7ff1fca2c167904f9a`，**dirty=true**，21 个生成 tracked 路径 + 1 untracked；source.json SHA `FB2B4C02133B8AF099F8E15E3C16A4EE3F1F2FC8F578F88E0C0A7E06234674B9`，dirty patch SHA `8AB6ED8D253235D845562D9326FEB1ECB53219B69ED19E40A8928144D8B67761`。没有把该构建称为 clean HEAD 构建。

---

## 6. 12 局整场 Player 实跑

命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 `
  -Mode All -RunDir build/g2-c03-20261101-ds/fullmatch `
  -UnityPath D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe `
  -DotnetPath D:\Unity\dotnet\dotnet.exe -TimeoutSeconds 5400
# tool native exit 0；PLAYER_EXIT disposition=EXITED_OK native=0
```

| 断言 | 实际 |
|---|---|
| 真实 round 数 / 唯一 roundId | **12**（`local-main-r1..r12`） |
| 逐局 terminal | 12 `round_terminal` |
| 逐局 `RoundEnded` 观察 | 12 `round_end_observed` |
| 逐局 match commit | 12 `match_round_committed` |
| 动作计划 / 确认 / client receipt | 252 / 252 / 252；252 个唯一 requestId |
| 仅 receipt 合法动作（无可见事件） | 41（Pass 类；其余有可见事件确认） |
| `match_finished` | 1；`records=12`，`finishReason=WestCompletion` |
| 终局原因 | 每局均为合法 `ExhaustiveDraw`（Draw），不是伪造和牌 |
| 分数 / 排名 | `[25000,25000,25000,25000]` / `[0,1,2,3]` |
| 136 物理守恒 | 12/12 局用同一 in-tree verifier 独立解码，`tileCount=136`、`uniqueCount=136` |
| native / tool | Player native 0；tool 0；driver outcome PASS，exitCode 0 |

证据：

- driver summary SHA `CFE928C5C174303797268E3CF478F2E09622354D083CCFE2F24BAB4171D0C395`
- tool summary SHA `F039B63EF34B50698B0DF559DDFAA3D3444D8B118E519EE93FF4CC58EAA0C617`
- fullmatch analysis SHA `3CC902FD7FF3FCDC157A3AB7AA31D2D06000AD0D6BE81DCBAFFE3BB05E5A6635`
- 12 局 round facts SHA `6F1BA360B9ADE82155778694A3080F10A435819D3ACB71828E891E1FC9213AFE`
- run log SHA `87BBDE6CC84471D518BBC56646165101E9FCB3AEE6CDF7DBEE281AEFE2449D04`

边界：这是完整 12 局本地整场与 136 守恒证据，不是 Android/iOS、观战、C04 outbox 对账、远端 PG/WSS 或 G2 通过；`visualEvidence=NOT_RUN`（隐藏窗口不作为通过）。

---

## 7. 受控恢复 8 场景（本轮同一 Player 的 GAME 采集）

### 7.1 采集命令（同一模板，逐 case 唯一 evidence root）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-controlled-recovery-collection.ps1 `
  -Case <Normal|Force> -SaveFault <None|TruncatePrimary|BadChecksumPrimary|IncompatiblePrimary|MissingPrimaryWithBackup|BothMissing|BothBad> `
  -PlayerPath build/g2-c03-20261101-ds/fullmatch/player/Mahjong.exe `
  -BuildCommit b33f0aad21c962a6f147dbfdb8a63fd98942d4a2 `
  -BuildLog build/g2-c03-20261101-ds/fullmatch/logs/unity-build.log `
  -BuildCommand "<exact Unity build command>" -BuildExitCode 0 `
  -Baseline D:/majiang-wt/p45-windows-validation-20261030/build/p45-windows-validation/series-r2/private-saves/baseline.save `
  -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll `
  -DotnetPath D:\Unity\dotnet\dotnet.exe -EvidenceClass GAME `
  -AllowDirtySource -AllowedDirtyPath "<15 generated plugin/ProjectSettings paths>" `
  -EvidenceRoot build/g2-c03-20261101-ds/c02-game/<case>
```

### 7.2 结果（collector 与独立 verifier 分开）

| case | expected | observed | factsEqual | chain | gamePositive | BEFORE PID/native | AFTER PID/native | case manifest SHA-256 |
|---|---|---|---|---:|---|---|---|---|
| normal | RECOVERED | RECOVERED | true | PASS | true | 64056 / 0 EXITED_OK | 1272 / 0 EXITED_OK | `C06ADBA03C9B00E966299159F50C301C6E58AF2825E4F17C158F08CDC5698125` |
| force | RECOVERED | RECOVERED | true | PASS | true | 61104 / **-1 FORCE_KILLED** | 740 / 0 EXITED_OK | `CCA7F6F03CD2D2568A1707D176C8400639CA8DF8169DC3C0F188E581FFEC3EB8` |
| truncateprimary | RECOVERED_FROM_BACKUP | RECOVERED | true | PASS | true | 21580 / 0 | 53544 / 0 | `3F65F9BA68D5629856902C6895FE08BA9D7C8430FD6F22F6A88D80EC49E5C2D1` |
| badchecksumprimary | RECOVERED_FROM_BACKUP | RECOVERED | true | PASS | true | 52000 / 0 | 13104 / 0 | `D904E55FC2C6DD215AD695BF374821EFE425B9ED96D337AB973CE278BC58B7D1` |
| incompatibleprimary | REJECTED | REJECTED | false | PASS | true | 56164 / 0 | 59884 / **1** | `D144A097998FA3BFC60D1B6E5D3CDCE06AD7FAD9A10A55EE8A481D7033E349FD` |
| missingprimarywithbackup | RECOVERED_FROM_BACKUP | RECOVERED | true | PASS | true | 47332 / 0 | 19004 / 0 | `F4D1296B5421AFF170E2137860BAA0C8249EA2AF655C2B1D1858ADBF948F8A04` |
| bothmissing | FRESH_START | FRESH_START | false | PASS | true | 3160 / 0 | 58204 / 0 | `A320B442AA7BB0662163B44CB179B1274FFEF67B5614FBF6929F5AC8F6C5CC34` |
| bothbad | REJECTED | REJECTED | false | PASS | true | 62048 / 0 | 54396 / **1** | `07C6EEF3A6272626FC403095E31F575ADE6EACE6012DDB10738E24C057F4E2D1` |

- 8/8 collector native exit **0**，`status=COMPLETE`，`collectionChain=PASS`，`gamePositive=true`，`callerAcceptance=PENDING_INDEPENDENT_REVIEW`。
- 8/8 独立离线 verifier native exit **0**，`collectionChain=PASS`，factsEqual 与上表一致；`callerAcceptance` 仍为 pending。汇总 SHA `1C553B9620820BA1E16CA21DE7B7768B076C36DBF2E4F8029570C91708B25AF2`。
- 期望语义：坏主档/缺主档从有效备份恢复；不兼容/双坏显式 REJECTED；双缺为合法 FRESH_START；没有把 REJECTED/FRESH_START 写成 factsEqual=true。
- 所有原始 authority/cheat/export/player log 封存在各自 case 下；报告只引用摘要与路径，不含暗牌/seed/token/私有账本原文。

---

## 8. 旧 accepted RecoveryExport / Compare 门禁

### 8.1 clean-prestate 结果（候选上的实际门禁）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-recovery-export.ps1 `
  -Case Both -PlayerPath build/g2-c03-20261101-ds/fullmatch/player/Mahjong.exe `
  -BuildCommit b33f0aad21c962a6f147dbfdb8a63fd98942d4a2 `
  -BuildLog build/g2-c03-20261101-ds/fullmatch/logs/unity-build.log `
  -BuildCommand "<exact>" -EvidenceRoot build/g2-c03-20261101-ds/recovery-export-clean `
  -VerifierPath dotnet/tools/P45RecoveryVerifier/bin/Release/net8.0/P45RecoveryVerifier.dll `
  -DotnetPath D:\Unity\dotnet\dotnet.exe
# native 0；两对 Compare 均 exit 17 / outcome=BLOCKED / pass=false
```

两条 Compare 均为：

- `outcome=BLOCKED`，`pass=false`，`requiredFieldsEqual=true`，`factsEqual=true`
- `chainStatus=UNTRUSTED_LOCAL`
- `realPassGate=NO_NON_FORGEABLE_COLLECTOR_ATTESTATION`
- 证据：normal Compare SHA `CD2AAEF027E3E3CA647EA4B74DD954C5D6F0B376C33847D6D41FD1F51CF07195`；force Compare SHA `0FC6D4EBE2705C844E09FC8620A6BD8828CCFA663EAB9A248AA778E956B9A65F`；summary SHA `74FDEAF940FEE23444F2B71D5358D6215D1252178EE0FFCDF865BB5510E55866`。

### 8.2 负例矩阵

`VerifyP45RecoveryExport.Tests.ps1` 对 clean-prestate normal run：**22/22 PASS，0 fail，0 skip，native 0**，summary SHA `37D9AE2C7F5F441B7E4FC042031B010694C14562E4C6619F64241D59B15423F4`。

覆盖：独立解码、stale cheat 拒绝、复制 run 无外部 collector key 拒绝、UNTRUSTED_LOCAL、重算 SHA 改 state/ledger/physical/receipt、错 phase/run/build/challenge/package、篡改 authority、fixture 改名、缺证据 BLOCKED、四类固定差异 FAIL16、signer API 无实际进程对象拒绝、零 Player 新 Prepare 重签拒绝。

### 8.3 首次 dirty 运行的差异（保留，不覆盖）

在生成 tracked 输出仍 dirty 时，旧链在“sealed run 必须 clean BUILD_LOG_ATTACHED”处 BLOCKED（chainStatus=BLOCKED），Compare 仍 exit 17/pass=false/factsEqual=true；负例矩阵 21/22（唯一失败即要求 UNTRUSTED_LOCAL）。随后在构建/采集完成后把生成 tracked 输出恢复为 HEAD 字节，再跑 clean-prestate 得到上述 UNTRUSTED_LOCAL/22/22。Play 仍是同一 `b33f0aad` + build-shared 生成输出的构建；构建时 21 dirty 路径与 patch SHA 已绑定（§5.3）。**clean-prestate 只用于评估旧 Compare 的 clean-source 前置门，不声称 Player 是 clean HEAD 构建。**

---

## 9. 首次失败与纠正时间线（全部保留原件）

1. **Unity PlayMode attempt-1**：插件未 fresh 同步，`ISessionView.cs` 报 `TableVisibleEventBatch` 缺失，Unity native 1；先执行 `build-shared.ps1` 后修复。log SHA `7226389D...`。
2. **C02 27-case attempt-1**：staged 未提交树导致 clean-source 回归 26/27；冻结提交后 27/27。log SHA `F1C15A4F...`。
3. **RecoveryExport negative attempt-1**：dirty 树导致旧链提前 BLOCKED、21/22；clean-prestate 22/22。log SHA `EEF9475B...`。
4. **PlayMode `-nographics`**：TMP importer 与 `RenderTexture.Create` 失败，30 fail；改用有图形 batch 后 15 个既有 UI probe 仍失败。XML `0E85...` / `36B3...`。
5. **PlayMode control `-nographics`**：TMP 窗口导致 1/2；有图形 2/2。XML `97D2...` / `DA2F...`。
6. **fullmatch diffCheck=2**：生成字体 asset 行尾空白，不是源码 delta；恢复后 `bae6..b33f0aad` 的 `diff-check=0`。

没有删除、重写或改名任何历史失败；首次失败日志/XML 与后续成功结果并存。

---

## 10. 完整缺陷/观察清单（一次给全）

### C03-D-01（MAJOR，开放，候选层；本 14 文件 delta 不含该测试/UI 路径，但未用 base 工作树重跑证明“历史既有”）

- **位置**：`unity/Mahjong.Unity/Assets/Tests/PlayMode/TablePlayModeTests.cs:344,361,459,560,587,624,665,717,799,880,970,1002,1105,1185,1378`（15 个 probe）。
- **原样复现**：
  ```powershell
  D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe -batchmode `
    -projectPath unity/Mahjong.Unity -runTests -testPlatform PlayMode `
    -testResults build/g2-c03-20261101-ds/unity/playmode-gfx.xml `
    -logFile build/g2-c03-20261101-ds/unity/playmode-gfx.log
  ```
- **实际**：native 2；147 total / 132 pass / 15 fail / 0 skip。失败包括 `ProbeReview01`（Expected 1 But was 0）、`ProbeReview02`、`ProbeReview08`、`R2FormalProbe01/08/10`、`R3FormalProbe07`、`R4FormalProbe04`、`R5FormalProbe04`、`Step02/03/04/05/06/10`。
- **期望**：全量 PlayMode exit 0；若这些 probe 只适用于单独/人工场景，应在测试清单中显式隔离并给出理由，不能用 skip/空断言伪造绿色。
- **证据**：`unity/playmode-gfx.xml` SHA `36B332C646FE2A92B5E1A4E71D1A347887CB93E7F4CD6DAAF82F5F968E875E98`；log；`-nographics` 首失败 XML `0E85...`。
- **修复要求**：单独 UI/PlayMode 任务定位这些 probe 的产品/测试夹具问题；不得在 C03 内改 UI、放宽断言或删除 probe。C03 的受影响件是 C01 `LocalFullMatchDriverPlayModeTests` 7/7 与 `TableControlPlayModeTests` 2/2，均已绿。
- **验收断言**：全量 PlayMode 147/147 exit 0，或经调用方批准的显式隔离清单；隔离后仍须实际执行剩余用例并给出逐例 XML。

### C03-D-02（BLOCKED 环境，开放，门禁完整性）

- **位置**：`dotnet/tests/Mahjong.TableService.DurableHost.Tests/DurableTableHostEnvironment.cs:163`；`dotnet/tests/Mahjong.Infrastructure.Tests/PostgresTableTestEnvironment.cs:182`；`dotnet/tests/Mahjong.LobbyService.Tests/PostgresLobbyTestSupport.cs:30`。
- **原样复现**：
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
    -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
    -LogDir build/g2-c03-20261101-ds/verify-v5
  ```
- **实际**：native 1；OVERALL FAIL；3 行：DurableHost 38/59（21 fail）、Infrastructure 1/42（41 fail）、Lobby 77/89（12 skip）。错误原文明确为缺 `MAHJONG_P54B_TEST_PG_ADMIN` / `MAHJONG_P52_TEST_POSTGRES_ADMIN` / `MAHJONG_LOBBY_TEST_POSTGRES`，并声明“no skipped/missing-PG fake-green path”。
- **期望**：统一门禁 21 行全绿、exit 0、无 skip；PG 用例必须真实连接独立测试库。
- **证据**：`verify-v5.log` SHA `8D94561420CAF4AE7EF131C09B6044548CE011215DFFBD65DAB7CE3AF0A4B22B`；`verify-v5/summary.log`；三个工程的 native XML/日志。
- **修复要求**：提供 PostgreSQL 与上述环境变量（或等价受控测试实例）；不要改测试为 skip、不要用内存假实现替代 PG、不要删除 fail-closed 断言。不是改生产代码的理由。
- **验收断言**：同一 `verify-v5.ps1` 在具备 PG 的环境重跑 exit 0，`OVERALL: PASS`，0 fail/0 skip，并保留逐工程 XML。

### C03-D-03（MINOR，证据边界，开放披露）

- **位置**：`build/g2-c03-20261101-ds/fullmatch/source.json`、`recovery-export-clean/`。
- **原样复现**：读 `source.json` 的 `dirty=true` 与 21 条 status，再读 clean-prestate Compare。
- **实际**：Player 构建时源码 dirty（生成插件/字体/设置），clean-prestate Compare 的 `source.dirty=false`，二者是不同 prestate；若不说明，会被误读为 clean HEAD 构建。
- **期望**：报告/复核者必须同时引用 build-time dirty patch 与 Player package manifest，不能把 clean-prestate Compare 当作 clean build 证据。
- **证据**：source.json SHA `FB2B4C02133B8AF099F8E15E3C16A4EE3F1F2FC8F578F88E0C0A7E06234674B9`；dirty patch SHA `8AB6ED8D253235D845562D9326FEB1ECB53219B69ED19E40A8928144D8B67761`；Player manifest SHA `8335C424...`。
- **修复要求**：无需改代码；保留该披露与 dirty patch。
- **验收断言**：复核者能从 source.json + patch + player-files.json 复算同一 prestate 与 205 文件包。

除上述 1 个候选层 MAJOR、1 个环境 BLOCKED、1 个 MINOR 证据边界外，本 14 文件集成范围内**未发现**新的 BLOCKER/MAJOR 代码缺陷；没有已知缺陷被隐藏到下一轮。

---

## 11. PASS / FAIL / BLOCKED / NOT_RUN

### PASS（本轮实际执行）

- 14 文件精确集成、编译、`diff-check=0`。
- in-tree verifier fresh Rebuild（0 warning/error）。
- LocalHost 134/134；TableRuntime 483/483；TableModel 235/235；TableService 203/203。
- E2E 16/16；C01 FullMatchDriver 32/32；C02 27/27；C05 29 条断言；RecoveryExport 负例 22/22。
- shared plugin build/provenance（150/19，PASS）。
- PlayMode C01 7/7；控制 UI 2/2。
- Windows Player 构建 native 0；12 局整场 PASS；252 动作/回执；12×136 物理守恒。
- 8 场景受控恢复 collector + offline verifier 全部 exit 0、chain PASS、gamePositive=true。
- 旧 Compare BLOCKED17/pass=false/UNTRUSTED_LOCAL（门禁仍在）。

### FAIL

- 统一 .NET 门禁：3 个 PG 依赖行 FAIL（环境缺失，见 C03-D-02）。
- 全量 PlayMode：132/147（15 个既有 UI probe 失败，见 C03-D-01）。

### BLOCKED

- PostgreSQL 相关全量门禁。
- 真实 Android/adb/设备、iOS、IL2CPP 真机、远端正例、观战/移动端完整矩阵：NOT_RUN/BLOCKED，未冒充通过。
- 调用方独立接受：`callerAcceptance=PENDING_INDEPENDENT_REVIEW`，不是 PASS。

### NOT_RUN

- b33f0aad 的新远端 CI（未 push/未触发；当前头 CI 限制见 §12）。
- C04 恢复后 outbox 实际重提/请求对账/零重复副作用矩阵。
- C05 正式 APK 重建、安装、启动、截图/视频/输入矩阵。
- 真实 PostgreSQL 自然终局、WSS、B2/G3。
- 全量 PlayMode 的逐 probe 修复（单独任务）。

---

## 12. 当前头 CI 限制（不继承历史绿）

- 调用方观测的 PR20 当前头仍为 `bae6b8b0...`；两条 CI `41261636998` / `41261632618` 当时为 `IN_PROGRESS`。旧头 `2813...` 的绿色报告不可推广。
- 本交付的新代码是 `b33f0aad...`，**没有任何远端 CI run**；本轮未轮询、未重跑、未 dispatch CI。
- accepted `origin/v5` 仍 `8fd7c6c`；PR20 OPEN/Draft；PR24 OPEN/Draft `81fa51b`。唯一 PR20 的发布/更新与独立审查归调用方；本交付不 push、不创建/修改 PR、不 record MR。
- 因此本报告只给本地/可复现证据；不能写成“当前头 CI 通过”或“G2 通过”。

---

## 13. 正式 C04/C05 的下一依赖

- **C04（Luna，正式 Player 功能/隐私/恢复组合）**：必须绑定本候选 `b33f0aad` + 本报告 Player package（205 files / 128,462,754 B / manifest `8335...`）或其调用方重新构建的等价包；在 C03 调用方独立接受后，执行恢复后的真实请求对账/outbox 原 ID 重提/零重复副作用。C03 的 8 场景只证明 facts/chain/封存，不证明 C04 的 outbox 重提已覆盖。
- **C05（Android/视觉/输入）**：必须在 C03 独立接受后，以同一冻结源码重建 fresh APK、跑授权设备矩阵、截图/视频/输入/安全区；本轮只跑工具 29 断言，不跑 adb/Android。
- 两者都不得以本轮组件证据替代自己的正式矩阵；C03 不启动其正式任务，也不为填槽造工作。

---

## 14. 证据路径、SHA256 清单与最终状态

### 14.1 证据根

`build/g2-c03-20261101-ds/`（ignored）：

- `logs/`：全部命令日志、首失败、Unity build/PlayMode、C02、fullmatch、verify-v5。
- `tests/xml/`：四个原生 .NET XML。
- `fullmatch/`：Player 205 文件、driver JSONL/summary、round facts、build/source/provenance。
- `c02-game/<case>/`：8 个 GAME 案卷、collector/verifier、sealed attempts。
- `recovery-export-clean/`：旧 Compare/负例 clean-prestate 原件。
- `tool-tests/`：E2E/FullMatch/C02/C05/Recovery 负例 summary。
- `verify-v5/`：统一门禁逐工程日志/XML。
- `evidence-sha256.csv`：4,182 个证据文件的 SHA-256/字节/相对路径；文件 SHA `49A8F50BB19A5600AF19253195B4FD2B9BA292873CE6CD2EC2458E2222355746`，597,038 B。

### 14.2 关键 SHA 汇总

| 项 | SHA-256 / 值 |
|---|---|
| 冻结代码 commit | `b33f0aad21c962a6f147dbfdb8a63fd98942d4a2` |
| 集成 diff | 14 files, +5207/−117；`diff-check=0` |
| verifier DLL | `387409D20C7B7921470D99BBB3B078A8108422D32A624F8D826CFB123B5626A9` |
| Player exe | `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807` |
| Player package manifest | `8335C4249A89F2C463B9702C93ED2A62CF11F083FDB7A3985F32B374D678C7CC`（205 / 128,462,754 B） |
| Unity build log | `67D965E6E503C4A30F6838967EA372CC797D3C5E177E3B90379FAF05A5181243` |
| fullmatch driver summary | `CFE928C5C174303797268E3CF478F2E09622354D083CCFE2F24BAB4171D0C395` |
| fullmatch analysis | `3CC902FD7FF3FCDC157A3AB7AA31D2D06000AD0D6BE81DCBAFFE3BB05E5A6635` |
| Unity PlayMode（全量有图形） | `36B332C646FE2A92B5E1A4E71D1A347887CB93E7F4CD6DAAF82F5F968E875E98` |
| PlayMode C01 / control | `ADBF0B1CA1D1E8D6859168527CA3E999F11706355417CD7C1642237067E5B964` / `DA2FC43E88676D72BABA87200F7217390FB5B631ED1237BCC194F94F2521AA5A` |
| C02 27-case summary | `95C4F434E38398209E632271D9DE5420D2FBEE9E6C239023BB2A24D4A2A589E2` |
| Recovery negative 22/22 | `37D9AE2C7F5F441B7E4FC042031B010694C14562E4C6619F64241D59B15423F4` |
| C02 GAME 8-case summary | `610910276F7B3F458F925ACAD433F667C4E277560649C571D720A328CBB7A58C` |
| C02 verify summary | `1C553B9620820BA1E16CA21DE7B7768B076C36DBF2E4F8029570C91708B25AF2` |
| unified gate log | `8D94561420CAF4AE7EF131C09B6044548CE011215DFFBD65DAB7CE3AF0A4B22B` |

### 14.3 最终状态

```powershell
git rev-parse HEAD          # b33f0aad21c962a6f147dbfdb8a63fd98942d4a2
git status --porcelain=v1   # 空（本报告提交后）
git diff --check            # exit 0
```

交付只有 14 个集成文件 + 本报告；构建生成文件不混提交，最终 tracked 树 clean。所有生成 dirty 的字节、provenance、dirty patch 与 Player 包已封存在 ignored evidence，未被冒称 clean 构建。

**本交付等待调用方独立复核；不 publish、不声称 G2/C03 完成。**
