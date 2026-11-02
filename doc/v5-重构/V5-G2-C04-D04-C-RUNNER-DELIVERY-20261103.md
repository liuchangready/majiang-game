# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 2）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness，使用其配置默认模型。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；分支
`wt/v5-g2-c04-d04-runner-ds-20261103`；独立复核报告：
`build/caller-c04c-review-37317f2d43684e04b4f1b03bc342321a/REVIEW.md`
（SHA-256 `AFBFA62BC91DA0790CD09DEB9F1E67BC68C3917751FFDB1E2AA8E4B564DDA942`），已全文读取。

本报告仅处理复核列出的 C/R1-06 四个 MAJOR 子组，不涉及 A/B，不把组件结果升格为
C04-G3/GAME 通过。原 R1-01/02/03、R3-07 与旧失败原件继续保留；旧 D04/R8 证据未改写。

## 1. 范围

tracked 修改仍限制在六个授权路径：

| 文件 | 状态 |
|---|---|
| `tools/unity/run-p45-c04-player-cheats.ps1` | 返修 |
| `tools/tests/C04PlayerCheats.Tests.ps1` | 更新 |
| `tools/tests/C04PlayerCheatRunner.Tests.ps1` | 重写 |
| `unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs` | 严格失败关闭 |
| `unity/Mahjong.Unity/Assets/Editor/C04PlayerCheatBuild.cs.meta` | 未改 |
| `doc/v5-重构/V5-G2-C04-D04-C-RUNNER-DELIVERY-20261103.md` | 本报告 |

未改共享生产源码、asmdef、机器配置、旧证据；未 push/PR/merge/deploy/approve。

## 2. 逐 ID 修复映射

### C-R1-06-R1-01 — 自有进程完成与端到端控制传播（MAJOR）

根因：只按当前 `ParentProcessId` 树清理，中间父进程先退出后丢失后代；`ObservedChildren`
未消费；根 native0 时未要求 `drainComplete`；异常路径未保证清理；PID 身份允许 2 秒容差；
timeout/cancel/unknown 被包装成 `BUILD_FAILED`；非正 timeout 关闭上界；mutex 等待不响应
CancelFile。

修复（`run-p45-c04-player-cheats.ps1`）：

- 每次进程启动保存 exact PID + CIM `CreationDate`；`Stop-C04ExactProcess` 必须 ticks 完全相等，
  缺失/不等不杀。
- 每 250ms 收集 owned 后代 PIDs 到 invocation-local `Observed`；清理时从 root + 所有 observed
  节点遍历当前树，即使中间父进程已退出仍可找到其后代。
- 额外使用 Windows Job Object（KILL_ON_JOB_CLOSE，若宿主允许）；Add-Type 不可用时自动降级，
  不把探测失败当成进程所有权成功。
- 额外按本次调用参数中的唯一长路径 token 匹配仍在运行的 writer，并严格限制创建时间不早于
  本次启动前 5 秒；早于启动的进程一律跳过，避免误杀历史/他人进程。
- 根退出后仍会执行 owned 清理；清理后重查 `remaining`。`drainComplete=false`、
  `ownedProcessesRemaining>0` 或 `cleanupFailed=true` 均不得被调用方当成成功。
- `Start-C04BoundedProcess` 在异常 `finally` 中先杀 exact root/owned，再关闭流。
- Shared build/guard、Unity warmup/build、Player、Verifier 均门控 `drainComplete` 与 owned 余量。
- `Invoke-C04UnityBuild` 单独传播 `UNITY_BUILD_TIMEOUT`（4）、`CANCELLED`（5）、
  `UNKNOWN_EXIT`（7）；main case 汇总保留控制类别，不再一律归 1。
- 所有 timeout 参数必须 >0；mutex 以 1 秒切片等待并检查 `CancelFile`/Ctrl+C。

验证：`process-probe` 真实中间进程退出后 writer 继续写；修复后 `drained=true`、
`remaining=0`、`stopped>=1`、childAlive=false、writesBefore==writesAfter（本轮新证据见 §5）。
`control-probes` 真实短 stub 的超时/取消分别得到 exception `UNITY_BUILD_TIMEOUT`/`CANCELLED`
和 mappedExit 4/5。

### C-R1-06-R1-02 — 输入/源冻结（MAJOR）

根因：identity 只看 dotnet/unity/tools 的 Git 状态，漏掉被复制到 staging 的 `tests` 与 ignored
Unity 资产；staging 变更只把 `.cs/.asmdef` 判 fatal；SourceAfter 在 Player 前采样；插件末态不重校。

修复：

- `Get-C04SourceInputRows` 直接枚举**实际复制闭包**：`dotnet/`、`tests/`、`unity/Mahjong.Unity/`
  （排除 Library/Temp/Logs/obj/UserSettings 与 fresh plugin 目录）、四个 tools/unity 脚本、
  root build props。包含 ignored 文件；`Get-C04SourceIdentity` 以该 manifest 的内容 SHA 为 identity，
  不再依赖 HEAD/commit，因此 doc-only 提交不改变 identity，但任何 code/test/ignored asset 变化都会改变。
- `New-C04StagedRepository` 逐文件比较 source rows 与 staged rows；缺失/变化/额外立即
  `STAGING_COPY_MISMATCH`。
- Unity warmup 完成后才写 `staged-project-input-manifest.csv`（覆盖全部 Assets/ProjectSettings/
  Packages 等，非仅 C#）；Player build 后逐文件比较。只允许精确的 Unity 构建派生转换：
  `ProjectSettings.asset` 变更、Font SDF 变更、`PerformanceTestRun*.json(.meta)` 删除；其他任意
  source/staging 变更失败。转换列表写入 `staging-workspace-diff.json`/summary，不是广泛豁免。
- build 后重新读取 plugin closure（provenance + 每个输出长度/SHA），必须与 build 前 pluginSet 相同。
- main 在 Player/Verifier 完成后、封存前重新计算 SourceAfter；任何变化强制 FAIL，不再复用旧值。

验证：review probe 的 `sourceClosure.identityUnchanged=false`、ignored asset 被复制；本轮
`Split-C04StagingTransitions` 仅允许上述精确转换，其他 asset/settings/package/asmref/meta/plugin
变更都进入 fatal。真实 fresh build 的 staging transition 记录见 §5。

### C-R1-06-R1-03 — SkipBuild、路径与失败封存（MAJOR）

根因：只重算自报 seal 字段；合成 14-byte 文本 exe/空 provenance 可被 sealed 并 PASS；ancestor junction
被跟随；failure seal 排除 build/player/staging/repo；finalize 失败可留下 PASS seal。

修复：

- 新 `c04-build-receipt.json`：freshBuild=true、buildId、sourceHead/buildCommit、source input
  manifest SHA/计数、staged input manifest SHA、package/report/provenance/closure/player 全关系。
  Seal 绑定 receipt SHA。
- SkipBuild 重新验证：sourceHead/buildCommit 为 40-hex 且是当前 HEAD 的 ancestor；source input
  identity 等于当前实际输入；receipt/manifest/report/provenance/closure/player 相互一致；
  provenance `inputs.Count>0`、`outputs` 与 `c04-plugin-closure.csv` 逐项 SHA 一致；BuildReport
  plugins 非空且等于 closure；exe 必须是 PE（MZ）且 >=64KB；缺失/篡改/错 source/错 build/
  合成包/空 provenance 均失败。
- `Assert-C04PathSafe` 检查目标及所有已存在祖先的 reparse point；evidence root、package、
  player、report、provenance、closure 均使用它；所有 package member 路径必须相对、无 `..`、
  不越界；`Get-C04FilesUnder` 拒绝 file reparse。
- Evidence seal 增加 `partial-artifacts.csv`：覆盖 `build/player` 与 `staging/repo`
  （排除 Library/Temp/Logs/obj/bin/UserSettings），seal 绑定其 SHA；失败/部分产物篡改可检测。
- finalize 先写 summary/manifest/seal 并独立 `Test-C04EvidenceSeal`；失败时删除 seal、重写
  FAIL summary/seal；再次失败则删除 seal 并写 `FINALIZE_FAILED.txt`，不保留 sealed PASS。
- 合成 SkipBuild 现在 native 非 0、status FAIL，不再写 PASS seal。

验证：review probe 的 `syntheticSkip` 从 native0/PASS 变为 **native1/FAIL**；本任务测试覆盖
missing receipt、empty provenance inputs、extra/missing/hash、fake source head、PE/text exe、
partial.dll 封存后篡改、ancestor junction。真实可信 fresh package SkipBuild：本轮无干净 fresh
build（见 §6），因此不声称通过；这是 C 构建限制，不是放宽。

### C-R1-06-R1-04 — BuildReport 错误验收（MAJOR）

根因：allowlist + generic `Error building Player: N errors` 放行；99 条只有 1 条 step 也可 ok；
缺 outputSha/bytes、非空 exception 也 ok；真实 build 仍 errors=6；文档过度声称。

修复：

- 删除生产 allowlist 放行；`AllowedNonFatalStepErrors` 仅保留为旧调用兼容参数，**完全忽略**。
- `Test-C04BuildReport` 恢复 fail-closed：`result=Succeeded`、`errors` 数值且为 0、无
  Error/Exception/Assert step、`stepCount>0`、`exception` 空、target 正确、output/bytes/
  outputSha 必填并与实际一致、六场景、plugins 非空并与 closure 匹配；`errors` 与 step 数不一致失败。
- `C04PlayerCheatBuild.Build` 恢复：`result!=Succeeded`、`errors!=0`、step error/exception、
  output 缺失任一即抛异常，保留原始计数与逐 step 消息，不改计数。
- 因真实 Unity build 仍为 `Succeeded/errors=6`（两条 URP 包级 `Host type` 文案各重复），
  本轮**没有任何组件构建 PASS**，runner 以 exit 3 失败并封存 FAIL；没有把 6 标成非致命或 0。
  历史 errors=3 原件保留，不声称已证明与本次 6 条的逐步等价；本次只记录真实 6 条原文。
- 修复根因需要改 Unity URP 包/项目设置（`Packages`/`ProjectSettings` 或包版本），不在六个授权
  tracked 文件内；本报告明确请求该越界最小授权，不自行修改共享设置。

## 3. 统一负例矩阵（本任务测试）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheatRunner.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheats.Tests.ps1
```

- Runner：**51 passed / 0 failed / native 0**。
- Compatibility：**20 passed / 0 failed / native 0**。
- 合计 **71**：quoting、native nonzero、timeout、zero-timeout 拒绝、detached writer、strict
  BuildReport（summary-only 99、one-known+98、missing output binding、exception、unknown）、
  source identity（tracked tests+ignored asset）、copy equality、asset mutation、package
  receipt/provenance/closure/PE/extra/hash/fake head、partial failure seal、ancestor junction、
  control exit mapping、固定 verifier CLI、无旧 Strict fallback。

## 4. 旧证据与历史

- 旧 R8 `REVIEW.txt`（SHA `18FD…A1A44`）与 manifest 只读保留。
- 历史 `player-build.json`/`player-build2.json`（`Succeeded/errors=3`）只读保留。
- 本轮实际 fresh build 的 6 条 step 原文保留在 `build/rework-fresh-113ec3b4e59d4a2696b2479ea5887718/`；
  未删除、未改计数。
- 旧 candidate/旧 tree/旧 build/player 未覆盖。

## 5. 本轮真实负例与 fresh build 证据

（见最终回复/证据索引；本节由同一交付提交固定路径与 SHA。）

## 6. 结论与边界

- C/R1-06 四子组：**实现已批修；机制测试 71/71；真实 detached writer、timeout/cancel、
  synthetic SkipBuild、junction、strict BuildReport 负例通过**。
- 真实 fresh build：**FAIL，errors=6，native1，runner exit3，无 package seal/PASS**。因此
  **R1-06 组件构建仍未通过**；C 不通过，无 approve/MR。
- 真实可信 fresh package 的 SkipBuild 成功、六行 GAME 证据：**NOT_RUN/BLOCKED**，需先完成
  URP 包级错误根因修复（越界授权）或由调用方提供干净包；不得用合成包替代。
- R1-04/A、R1-05/B 不在本任务范围，未运行 Player/PlayMode；全 D04 仍
  **0 BLOCKER / 3 原 MAJOR 不变，未通过**。

## 7. 提交与清洁

- 代码/测试提交与最终 doc-only 提交、diff-check、clean 状态见最终回复。
- 未 push/PR/merge/deploy/approve；未改共享生产源码/asmdef/机器许可。
