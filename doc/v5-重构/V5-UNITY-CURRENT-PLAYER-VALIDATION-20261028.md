# V5 当前已接受 Unity 集成树 Windows Player 构建与运行补验

日期：2026-10-28（Asia/Shanghai）
结论：**PASS（仅限本任务范围；人工目视布局为 NOT_RUN）**——已审查 PR20 头 `392b973` 的完整 Unity 集成树完成一次 fresh Windows 64 Player 构建，并用既有 `run-player-smoke.ps1` 实际跑通菜单 → 大厅 → 返回菜单 → 退出弹窗取消/确认 shell 流程；4 张 Player PNG 经解码与 Windows OCR 实际核验 UI 文字/弹窗，人工目视布局因本模型无图像输入未做。**不是 P4.2-B、P4.5、G2 或整场 E2E 通过；不覆盖 LocalHost、Android/iOS/IL2CPP、真机、跨物理网络或持久 WSS 宿主。**

任务树：`D:/majiang-wt/unity-player-current-ds-20261028`
分支：`wt/v5-unity-player-current-ds-20261028`
冻结 SHA：`392b9739aff96d0fa83acd4bef3c2a3667ccc7d4`
上游基线：`origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（已验证为 HEAD 祖先）
最终代码 SHA：仍为 `392b973`；本任务只新增本报告一个文档提交，报告提交自身的字面 SHA 见交付回执（报告不能自引用自身提交哈希）。

本任务未转派、未创建子任务、未操作其它 bridge 线程；P4.1c/d 仍保持手动 paused，未恢复/复制；未读取或采用其它 receiver 在途未提交代码。除只读核对 B2b 超时原始证据外，所有构建、运行和写入均在本树。

---

## 1. 冻结、环境与并发前置

| 项 | 实际值 |
|---|---|
| `git rev-parse HEAD` | `392b9739aff96d0fa83acd4bef3c2a3667ccc7d4` |
| `git rev-parse --abbrev-ref HEAD` | `wt/v5-unity-player-current-ds-20261028` |
| `git rev-parse origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `merge-base --is-ancestor origin/v5 HEAD` | exit 0 |
| 冻结前 tracked 状态 | clean（`git status --porcelain=v1` 0 行） |
| `git diff --check 392b973..HEAD`（验证前） | exit 0 |
| OS | Windows 10 19045，64-bit |
| 物理内存 | 16,108 MB；Player 构建启动时可用约 1.3 GB（已记录，未杀其它任务进程） |
| CPU/GPU | 16 逻辑处理器；Intel Iris Xe；运行期最终使用 D3D11 |
| Unity | `D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe`，`6000.3.27f1 (672d562a42a7)` |
| .NET | `D:/Unity/dotnet/dotnet.exe 8.0.426` |
| Player 构建前 Unity.exe | 无其它 `Unity.exe` 实例；构建独占本树。其它 bridge 的 dotnet/MSBuild 进程存在，未被杀/改 |

Unity 许可证由现有 `Unity.Licensing.Client` 正常解析（构建日志有 `Successfully resolved entitlement details`）；未购买、未激活、未改机器信任或全局 PATH。

---

## 2. 实际命令

```powershell
# 1) fresh Release 共享程序集与 Unity 插件闭包（本树）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 -Configuration Release

# 2) fresh Windows 64 Player（本树独占输出，1200 秒有界）
#    实际参数（脚本记录于 logs/player-build-meta.json）：
#    Unity.exe -batchmode -projectPath "unity/Mahjong.Unity"
#      -buildWindows64Player "<evidence>/player/win64-d39f48d9/Mahjong.exe"
#      -logFile "<evidence>/logs/player-build.log" -quit

# 3) 对 fresh exe 运行既有 shell smoke
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-player-smoke.ps1 `
  -PlayerExe "<evidence>/player/win64-d39f48d9/Mahjong.exe" `
  -ScreenshotDir "<evidence>/screenshots" `
  -LogFile "<evidence>/logs/player-run.log"

# 4) fresh 编译后的原 StrictCompiled 边界检查（策略未改）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-unity-boundaries.ps1 `
  -Project unity/Mahjong.Unity -Policy tools/unity/p42-boundary-policy.json `
  -StrictCompiled -Report "<evidence>/logs/boundary-strict.json"

# 5) 独立证据复核脚本
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<evidence>/scripts/verify-evidence.ps1" `
  -Root "<evidence>" -RepoRoot (Get-Location).Path
```

`<evidence> = D:/majiang-wt/unity-player-current-ds-20261028/build/unity-player-ds-20261028-47bd8e77`。

启动说明：前两次包装调用**没有启动 Unity**——宿主没有 `pwsh`，随后直接执行 `.ps1` 又被执行策略拒绝；两次均未产生 Unity 子进程、Player 日志或 exe，记录在 `logs/player-build-launcher-failure.txt`。第三次改用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` 才实际启动 §4 的 Unity 构建。这两次不计为 Player 构建 FAIL，也不计为 PASS；没有因此改源码、构建配置或放宽断言。

---

## 3. build-shared：fresh Release 插件闭包

```text
native exit 0
solution build: 0 Warning(s), 0 Error(s), 43.77s
copied 19 files
```

19 个复制项 = 7 个自有 DLL + 7 个 PDB + `Google.Protobuf.dll` + `System.Buffers.dll` + `System.Memory.dll` + `System.Numerics.Vectors.dll` + `System.Runtime.CompilerServices.Unsafe.dll`。PDB 与第三方 DLL 也按工具实际闭包复制；未复制测试框架、Grpc.Tools、SDK payload。

**关键源码一致性结论：** 构建后 Player 的 7 个自有托管程序集与 fresh `dotnet/src/*/bin/Release/netstandard2.1/*.dll` **逐字节 SHA256 全部相同**；`Mahjong.Client.dll` 的实际 SHA256 为
`AC485AD6FA3E7E4351CFBFDE45FEA167B5E91C5B2B766E6EC04AB0829F3AB6B1`。

**已提交插件闭包陈旧（MINOR，见 §8 D-U-01）：** 冻结树中 tracked 的 7 个自有 DLL 与 392b973 当前源码的 fresh 输出全部不同；`Mahjong.Client.dll` tracked 为 183,808 bytes / `8215C55B...`，fresh 为 203,264 bytes / `AC485AD6...`。tracked 插件最后一次变更停留在 `ae33982`（P4.2 平台壳），而 Client 源码在 P4.2 之后已更新。按已接受流程先执行 `build-shared.ps1` 后，Player 使用的是 fresh 闭包；未提交任何 DLL/PDB。

原始证据：

| 证据 | SHA256 |
|---|---|
| `logs/build-shared.log`（13,768 bytes） | `4405702737E36FCC0BF3A409E3A5524B886DBB49101B9D06B67CE02BEF39A1B4` |
| `logs/build-shared-exit.txt`（native exit 0，47.17s） | `EB0C1C2CC0510CD6DC72AF2E9F23B3CB5B72406718EFE527D087B59FC02C9CA5` |
| `logs/plugin-closure-compare.csv`（7 DLL tracked/fresh/Player 对比） | `C6366526EA6262EC7AC28EF5BE1F3E92875BB4E651401973C62E13075E1787E1` |

---

## 4. fresh Windows 64 Player 构建

| 项 | 实际值 |
|---|---|
| Unity PID | 53144 |
| start / end (UTC) | `2026-10-28T03:50:37Z` / `2026-10-28T04:03:03Z` |
| elapsed | **746.12 s**（有界 timeout 1200 s） |
| Unity native exit | **0** |
| 结果标记 | `Build Finished, Result: Success.` |
| 目标 | `building x86_64 windows player!`；`StandaloneWindows64` |
| 输出 | `<evidence>/player/win64-d39f48d9/` |
| `Mahjong.exe` | 667,136 bytes；SHA256 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807` |

`Mahjong.exe` 的 SHA256 与历史 B2b R10 构建记录相同，这是 Unity Player 启动器 stub 的正常现象，**不能**用该 hash 单独证明 fresh。fresh 证据是：本树全新 GUID 输出目录、746 s 的 Unity 构建日志、`Build Finished, Result: Success.`，以及 Player 内 7 个托管程序集与 fresh 源码逐字节一致。没有复制旧 exe、DLL 或任何历史产物。

构建实际导入并打入了 `EditorBuildSettings` 的 6 个已提交场景：`Boot`、`Menu`、`Lobby`、`Table`、`Spectator`、`Settlement`（日志逐项列出 `Assets/Scenes/*.unity`；`Mahjong_Data/level0..level5` 六个 level 文件）。没有删场景、删程序集或换精简项目。

**实际 backend：Mono，不是 IL2CPP。** 依据：Player 运行日志 `Mono path[0] = .../Mahjong_Data/Managed`、`Mono config path = .../MonoBleedingEdge/etc`；构建目录含 `MonoBleedingEdge/`；构建日志以 `Cleanup mono` 结束；`ProjectSettings.asset` 的 `scriptingBackend:` 为空（默认 Mono），`apiCompatibilityLevel: 6`。Player 运行期实际使用 D3D11（D3D12 被机器上的 user filter 拒绝后回退），不是 IL2CPP/Vulkan。

关键数据文件（同目录）：

| 文件 | Length | SHA256 |
|---|---|---|
| `Mahjong_Data/globalgamemanagers` | 221,476 | `404D0A4DF89EDFEDC46C7D3564DA7A9716161CB0AD34349556DF422B8011C760` |
| `Mahjong_Data/resources.assets` | 348,712 | `2384B05BDBE5FA3D273AF4E5F8955F23B4EE695358CA7FF8BDA16EA7B4BD9AEB` |
| `Mahjong_Data/sharedassets1.assets` | 17,849,008 | `C631C2CF47228230E7B189A621FCE6B55F36B3ADAE1C062F0724D13EC4722884` |

原始证据：

| 证据 | SHA256 |
|---|---|
| `logs/player-build.log`（1,978,311 bytes） | `F9B51C4A1379533C4533F20320E9F1A506787EDFBFF7749ABB6FBF03B8510928` |
| `logs/player-build-meta.json`（PID/start/end/args/timeout/exit） | `643492E276D7963D4563E43F3E4DC97D59ECAA91A56CF4A1B2DAEFEE27C36ECB` |
| `logs/player-build-exit.txt` | `570A7B3B037F79695951C734BFB7C964477D84375C59B91ABD96CE1693D5E74E` |
| `logs/player-managed-vs-source.csv`（7/7 match） | `32E82E52357BBBFFCB1098F3FEFAB38538C2602C85A11B0DAE2E551D6E7F0023` |

Player 中确实编译进了 B2b/Table 代码：`Mahjong_Data/Managed/Mahjong.Unity.Table.dll` 含 `ClientVisibleEventMapper`、`ConfirmedAnimationQueue`、`TablePresenter`、`SessionAuthority`、`VisibleEventBatch` 类型字符串。**但本轮 Player 只运行了菜单/大厅/退出弹窗 shell，没有进入牌桌或消费任何真实增量事件；编译进去不等于整局互动在 Player 实跑。**

---

## 5. fresh Player shell smoke 与截图

```text
Launching standalone player: <evidence>/player/win64-d39f48d9/Mahjong.exe
Player exited cleanly with ExitCode: 0
native_exit=0
elapsed_s=10.15
Player smoke test completed successfully!
```

生命周期 marker 按 `run-player-smoke.ps1` 的严格顺序实际出现，未出现 `SMOKE_ERROR:`、`QUIT_SHUTDOWN_FAULT:` 或 Exception：

```text
SCENE_LOAD_COMPLETED:Menu
SMOKE_STEP:NAVIGATE_TO_LOBBY
SCENE_LOAD_COMPLETED:Lobby
SMOKE_STEP:LOBBY_LOADED
SMOKE_STEP:NAVIGATE_TO_MENU
SCENE_LOAD_COMPLETED:Menu
SMOKE_STEP:MENU_RETURNED
SMOKE_STEP:MODAL_OPEN
SMOKE_STEP:MODAL_CANCEL_CLICKED
SMOKE_STEP:MODAL_REOPENED
SMOKE_STEP:MODAL_CONFIRM_CLICKED
QUIT_CONFIRMED: Application quit confirmed by user.
QUIT_REQUESTED: Initiating application shutdown.
SMOKE_SUCCESS: Application shutdown completed cleanly.
```

### 5.1 4 张截图原始证据

| 文件 | Bytes | 尺寸 | SHA256 |
|---|---:|---|---|
| `screenshots/01-menu.png` | 523,833 | 1280×720，有效解码 | `0C9887D9A0AEF144B59349322DF60812330BC0C7224211C0652EF40BB6F4D6DE` |
| `screenshots/02-lobby.png` | 549,684 | 1280×720，有效解码 | `7C89C0C86D2F01E7DB81B51FF0D68783DA71261ADF919CBD4ABC9784697E7087` |
| `screenshots/03-menu-returned.png` | 523,833 | 1280×720，有效解码 | `0C9887D9A0AEF144B59349322DF60812330BC0C7224211C0652EF40BB6F4D6DE` |
| `screenshots/04-quit-modal.png` | 246,397 | 1280×720，有效解码 | `305BC3C7F75BAAE7766B1FDBC21A105A54CC986CD18B63EEEFFD12B4ACC693E3` |

4 张均为 PNG magic + 1280×720 + `System.Drawing.Image` 成功解码，长度均 > 1000 bytes。不同 hash 数为 3；`01-menu` 与 `03-menu-returned` 相同是**预期**的“返回同一菜单”画面，不是截图复用或导航失败；菜单/大厅、菜单/弹窗均不同。`run-player-smoke.ps1` 原断言实际通过。

### 5.2 UI 文字与弹窗检查（OCR 实际提取）

本模型不支持图像输入，**不能声称已做人工目视布局审查**；为避免用“看过了”代替证据，改用 Windows OCR（`Windows.Media.Ocr`，`zh-Hans-CN`）对 4 张原始 PNG 做实际文字提取。OCR 原始输出保留字间空格并有个别字形误识别（例如 `P4.1` 被识别为 `P4彐`）；下列是按语义归并后的关键字符串，逐字原文与逐行结果见 `logs/screenshots-ocr.json`。关键 UI 文案与预期一致：

- `01-menu.png`：`千术麻将 (Mahjong Cheater)`、`进入大厅`、`退出游戏`
- `02-lobby.png`：`本地宿主未接入（P4.1）`、`本地对局通道尚未就绪 (LOCAL_HOST_UNAVAILABLE)。P4.2平台壳已就绪，等待P4.1本地工厂对接。`、`开始本地对局（不可）`、`返回菜单`
- `03-menu-returned.png`：与菜单页相同的三项
- `04-quit-modal.png`：`退出游戏`、`确定要退出千术麻将吗？`、`确定`、`取消`

大厅真实边界仍是**本地工厂 Unavailable、开始按钮禁用**，没有被 fake 改成可玩；OCR 明确显示“不可”。退出弹窗的取消/重开/确认由日志 marker 实际串起。OCR 原文与逐行结果在 `logs/screenshots-ocr.json`（SHA256 `C52B358B366ECA60AA3A31410EA57048E24FF6768C40FC03D4833208175C3355`）。

**边界：**人工目视布局/颜色/遮挡为 **NOT_RUN**（本模型无图像输入）；UI 文字、弹窗内容与截图有效性通过 OCR/解码实际核验。没有用 Editor 截图替代 Player 截图。

原始证据：

| 证据 | SHA256 |
|---|---|
| `logs/player-run.log`（4,327 bytes） | `F029B4984DB75A4403BA37064FC979C32EBDFB9D33D7E2BF42319FF9E9C8BFE7` |
| `logs/player-smoke-wrapper.log` | `25A2ABE9FD4B1F0C4461160D27FF212517D8758F438B1BFEF571F7FE0FDCD56B` |
| `logs/player-smoke-exit.txt`（native 0，10.15s） | `08EE9802D20F41B2ADA73F5826341B6E0C9388157D6C8321DB60837AA8866291` |
| `logs/screenshots-inventory.csv` | `C19580CFC20CBEF11B575836FB1ACAF71C7B51ECF6E0C29631A639CEA6C96BCF` |

---

## 6. fresh 编译后的 StrictCompiled 边界检查

命令未改策略、未加白名单、未关闭校验：

```text
Boundary check PASSED. All asmdefs and source boundaries satisfy policy.
native_exit=0
elapsed_s=11.03
```

`logs/boundary-strict.json` 实际字段：

```json
{
  "Passed": true,
  "ErrorCount": 0,
  "asmdef_dag": "PASS",
  "source_reflection_scan": "PASS",
  "metadata_direct_refs": "PASS"
}
```

caller 预检时 `metadata_direct_refs` 为 **NOT_RUN**；本轮在 fresh Unity 编译产出 `Library/ScriptAssemblies` 后按原 `-StrictCompiled` 入口实际执行，metadata 直接引用检查为 **PASS**。没有把预检的 NOT_RUN 写成 PASS，也没有刷新摘要绕过检查。

| 证据 | SHA256 |
|---|---|
| `logs/boundary-strict.json` | `7DF51665FB522BEFEDC5E4C9BDB7D6747A6839BDBD2AA0CF2BDDB6ADDB80A5BD` |
| `logs/boundary-strict.log` | `198E8C0359A927FF7D8C41F60A9D0BB7DD121C6B7EBAA0BAF6C6A2E4BD433B63` |
| `logs/boundary-strict-exit.txt` | `210E59C9E6FCFC69F2937F7ECB587D713A3D51D2C80F1E1424D26058A0D8FDB4` |

---

## 7. 临时差异、精确恢复与源码一致性

构建/导入期间 Unity 与本工具产生的 tracked 差异共 **21 个路径**：

- 14 个 `Assets/Plugins/Mahjong` 自有 DLL/PDB（fresh 复制）
- `Assets/Resources/PerformanceTestRunInfo.json`、`PerformanceTestRunSettings.json` 及其 `.meta` 被 Unity 构建删除（4 个）
- `Assets/Art/UI/Fonts/NotoSansSC-Regular SDF.asset`
- `Mahjong.Unity.slnx`
- `ProjectSettings/ProjectSettings.asset`

处理方式：

1. 验证前对 310 个 tracked Unity 文件建立 `Path/Length/SHA256` 清单，并物理备份 `Plugins/ProjectSettings/Scenes/Packages-manifest`。
2. 构建后先把 21 个差异的 status/diffstat/binary patch 和生成版本保存到 `post-generated/`。
3. 对 `ProjectSettings.asset` 与字体 asset 做去行尾空白归一比较：**normalized diff = 0**（只是 Unity 的序列化/行尾写法变化，无语义设置变化；未改 backend、分辨率、Input、边界策略）。
4. `Mahjong.Unity.slnx` 的差异只是 Unity 重新生成时移除了 7 个 `<Project Path="...">` 条目；已原样恢复。
5. 只用**明确路径**执行：
   `git restore --source=HEAD -- <上述 21 个路径>`；
   **未使用 `git reset --hard`，未使用 `git clean`**。
6. 恢复后重新计算 310 个 tracked Unity 文件：**310/310 的 Length/SHA256 与验证前完全一致，diff 0**；`git status --porcelain=v1 --untracked-files=all` 为 0 行。

Unity 不可避免生成的 `Library/`、`Temp/`、`Logs/`、`UserSettings/` 均为 ignored 缓存，未入 git；验证脚本/构建脚本/Player/截图/日志只写入 ignored `build/unity-player-ds-20261028-47bd8e77/` GUID 独占目录。

原始证据：

| 证据 | SHA256 |
|---|---|
| `pre/unity-tracked-inventory.csv`（310 行验证前清单） | `47B625B7D431FB234CF15E280B74A70409E21B657E0946AC756552E756B2ECD7` |
| `logs/unity-tracked-inventory-restored.csv`（310 行恢复后清单） | `47B625B7D431FB234CF15E280B74A70409E21B657E0946AC756552E756B2ECD7` |
| `logs/restore-summary.txt` | `F7ECAAFAD5DEABBD066BBCBEB981A839427F120C4531C944B5C0C2889639A9EF` |
| `post-generated/tracked-diff-after-build.patch`（21 路径原始差异） | 见 manifest |

---

## 8. 已知缺陷 / 观察（只读审查，不越界修复）

### D-U-01（MINOR）：已提交 Unity 插件闭包与当前源码不同步

- **位置**：`unity/Mahjong.Unity/Assets/Plugins/Mahjong/Mahjong.*.dll`（7 个自有程序集）。
- **复现**：在冻结树 tracked clean 时执行
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 -Configuration Release`，
  再比较 `Assets/Plugins/Mahjong/*.dll` 与 `dotnet/src/*/bin/Release/netstandard2.1/*.dll`。
- **实际**：7/7 自有 DLL 的 tracked 版本与 fresh 源码版本均不同；`Mahjong.Client.dll` tracked 183,808 bytes / `8215C55B9FD9A08A...`，fresh 203,264 bytes / `AC485AD6...`。tracked 插件最后一次提交为 `ae33982`（P4.2 平台壳）。
- **期望**：干净 checkout 上，要么 committed 插件闭包等于当前源码构建，要么构建前置检查在闭包陈旧时 fail closed，避免未执行 `build-shared.ps1` 的 Editor/Player 使用旧 Client。
- **证据**：`logs/plugin-closure-compare.csv`、`logs/build-shared.log`、`pre/unity-tracked-inventory.csv`。
- **最小修复范围**：调用者决定是否提交 fresh 插件闭包，或在既有 Unity 构建入口增加 SHA/源码来源前置断言；本任务不提交二进制、不改生产代码/工具。
- **验收断言**：clean checkout 按文档构建后，Player `Mahjong_Data/Managed/*.dll` 与 fresh 源码 7/7 相同；若闭包陈旧，前置检查必须非零失败并打印差异，而不是静默使用旧 DLL。
- **本任务影响**：不否决本次 PASS——本轮按要求先执行了 `build-shared.ps1`，Player 实际使用 fresh 闭包；该条是复现性/流程风险，不是本次 Player 构建失败。

本轮未发现新的 BLOCKER/MAJOR。B2b R11 的 300 秒 Player 构建超时仍是既有事实，本轮不宣称其根因已关闭（见 §9）。

---

## 9. 原 B2b 超时事实保留

B2b R11 的可选 Player 构建在 300 秒超时、整体 wrapper exit 1，Player 运行 **NOT_RUN**；本轮没有覆盖、重写或把该历史写成绿色：

| 原证据 | 实际 |
|---|---|
| `D:/majiang-wt/p43-b2b-events-ds-20261027/build/p43-b2b-r11-8c3e5f7a1d294b60/player-build-exit.txt` | 内容 `TIMEOUT`；SHA256 `7D6EA5030DEA18CB0A0CF44A8210EA9CDD14C8259A68AEC56F6B1F7A13F06C8C` |
| 同目录 `logs/build-player.log` | SHA256 `BC43769242F452595815DA436682BBE5E2D25115A56BCE729918B93AB52EF871` |
| `build/p43-b2b-r11-8c3e5f7a1d294b60-wrapper.log` | SHA256 `E4048EA53D53E82524E957A6B97F17DEA598FCAB272831D87C7972FE7EC4308F` |

本轮 746.12 s < 1200 s 只说明**在有界 1200 s 下本次 fresh 构建完成**，不等于 B2b 300 s 超时的根因已定位或关闭；没有修改源码/构建配置/断言来缩时，也没有把延长 deadline 当作根因修复。若后续要关闭该历史项，仍需单独给出 300 s 失败的可复现根因与修复证据。

---

## 10. PASS / FAIL / BLOCKED / NOT_RUN 矩阵

| 项 | 结论 |
|---|---|
| 冻结 SHA / 分支 / origin-v5 祖先 / tracked clean | **PASS** |
| `build-shared.ps1` fresh Release | **PASS**（native 0，0 warning/0 error，19 copied，47.17 s） |
| 7 个自有 DLL tracked 与 fresh 源码一致 | **FAIL（D-U-01 MINOR，流程风险）**；按文档先 build-shared 后 Player 实际使用 fresh |
| fresh Windows 64 Player 构建 | **PASS**（Unity native 0，746.12 s，Success） |
| Player 目标/backend | **PASS**：`StandaloneWindows64` x86_64，**Mono**（非 IL2CPP） |
| 6 个已提交场景打入 Player | **PASS** |
| Player 7 个托管程序集与 fresh 源码逐字节一致 | **PASS（7/7）** |
| 既有 shell smoke（菜单→大厅→返回→退出弹窗取消/确认） | **PASS**（native 0，10.15 s，marker 有序） |
| 4 张 Player PNG 签名/1280×720/有效解码/必要不同 hash | **PASS**（3 个不同 hash；01=03 为预期同菜单） |
| UI 文字/大厅 Unavailable/开始禁用/退出弹窗（OCR） | **PASS**（Windows OCR 实际文字） |
| 人工目视布局/颜色/遮挡 | **NOT_RUN**（本模型无图像输入；未冒充看过） |
| fresh 编译后 StrictCompiled（metadata_direct_refs 实际 PASS） | **PASS** |
| 临时差异精确恢复 / 310 tracked 文件 hash 一致 / 最终 clean | **PASS** |
| B2b 300 s Player 超时根因 | **NOT_RUN / 未关闭**（历史事实保留） |
| 完整 LocalHost、P4.2-B、P4.5 整场 | **NOT_RUN**（非本任务范围；P4.1b 存档组件不代表完整 LocalHost） |
| 持久 WSS 宿主 / 完整远端 E2E | **NOT_RUN**（GLM 在途任务范围，不复制） |
| Android 完整游戏/真机、iOS、跨物理网络、IL2CPP | **NOT_RUN / 未批准** |
| 全量 Editor/PlayMode/.NET/PG/双 CI | **NOT_RUN**（由已接受组件/兄弟线程负责，本任务不重复） |
| G2–G5 | **未批准**；181 classified 仍未覆盖 |
| P4.1c/d | 仍手动 **paused**，未恢复/复制 |

---

## 11. 证据根、manifest 与提交边界

证据根（ignored）：

`D:/majiang-wt/unity-player-current-ds-20261028/build/unity-player-ds-20261028-47bd8e77/`

Manifest：

- `manifest-sha256.csv`：**332 个文件**，总计 **137,091,323 bytes**，SHA256 **`39A2707B11405DC7FDABB59E68EFDDD67596EFFBBDA65F689E3B9A038E77ECEC`**
- `manifest-meta.json`：记录行数/字节数/manifest hash
- 排除：`temp/**`（Unity TEMP 缓存）、`manifest-sha256.csv`、`manifest-meta.json`（避免自引用）；报告文件在证据根外
- 保留：`pre/` 清单与环境、`pre-backup/` 原 tracked 备份、`post-generated/` 构建期差异、`logs/` 原始日志/JSON/CSV、`screenshots/` 4 张 PNG、完整 Player 输出、`scripts/` 审计脚本

独立复核脚本 `scripts/verify-evidence.ps1` 实际结果：

```text
SUMMARY checks=29 failures=0
native_exit=0
```

其日志/exit 的 SHA256：

| 证据 | SHA256 |
|---|---|
| `logs/verify-evidence.log` | `C7F38F29B00441E4CD9B1D361C5ADC8BD0068D99C33D214779052773AF9717AE` |
| `logs/verify-evidence-exit.txt` | `9E1005972F5F14EE9DDEB31A909741C193521A8D5D14722E0F2930D0C1905F09` |

提交边界：

- 只新增本报告一个 tracked 文件；净 diff = 1 个文档。
- 未提交对生产代码、正式测试、verifier、sln、asmdef、边界策略、机器信任、EditorPrefs、共享 Editor/SDK/PATH、scheduler 的修改；Unity 构建期临时生成的插件/资源/slnx/ProjectSettings 差异已按 §7 精确恢复。
- 未提交 DLL/PDB/Player/截图/日志；未 push、未创建 PR/MR、未合并 v5、未部署、未创建 `complete.flag`。
- 最终由 caller 审查后把本报告并入唯一 PR20：`https://github.com/liuchangready/majiang-game/pull/20`；不创建组件 PR。
- 本报告只证明当前已接受完整 Unity 集成项目的 Windows Player fresh 构建与既有 shell 运行；不把历史通过或组件通过扩大为迁移完成。
