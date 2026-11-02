# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 4）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness，使用其配置默认模型。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；分支
`wt/v5-g2-c04-d04-runner-ds-20261103`。R3 独立复核：
`build/caller-c04c-r3-3155cc9e1cdd4c21bf0c832b26d1b162/REVIEW.md`
（SHA-256 `1E76E5499D2B2A355C568DE348B3694A8B6A9CE6D428EC59ACFF259C7683BA1C`），
probes/extra-probes/assignment-probe 已复现。旧 R8/R1/R2/R3 证据、历史 errors=3/6 原件只读保留。

本轮仅修 C/R1-06 三组机制；R1-04 严格门禁保留，未获外部授权不改包/设置/allowlist，
也没有为相同 errors6 重复昂贵 Unity 构建。C 仍不通过，无 approve/MR。

## 1. 逐 ID 修复

### C-R1-06-R1-01 — wrapper 启动/退出、Job 未知、assign 清理（已修）

- 生成的 launcher 现在输出**结构化 target-result**：
  - 目标启动/等待在 `try/catch` 中，`ErrorActionPreference=Stop`；
  - 成功写 `{status:EXITED, wrapperPid, targetPid, exitCode}`；
  - 启动失败/loader 失败写 `{status:START_FAILED, targetPid:0, exitCode:null, error}` 并 exit90；
  - 外层不再把 wrapper 的 0 当成 target 已启动；target-result 缺失/非 EXITED/无 exitCode
    都使 `exitCodeKnown=false`、`targetStartFailed=true`。
- 外层记录 wrapperPid/targetPid 区分身份；正常退出仍以 target exitCode 为准。
- Job query `-1` 不再被 `>0` 吞掉：Stop-C04Job 在 query 未知时仍 Terminate/Close 并返回
  remaining=-1；所有调用方以 `-ne 0` 传播 unknown，`cleanupFailed=true`、exitKnown=false。
- assign 前即将 `ownershipKnown=true`；assign 失败/assign 前异常时，即使 wrapper 未入 Job，
  finally 也用 exact PID+StartTime 停止仍等待 signal 的 wrapper，并确认 remaining=0 后再抛错。
- 保留 peer 存活、无 CommandLine/path 扫描、两层后代 2/2。

实测（reviewer probes 新复制根）：
- invalid PE：`exit=null, known=false, targetStartFailed=true`，无 wrapper 残留；
- Job query 注入 -1：`exit=null, known=false, remaining=-1, cleanup=true`；
- assign 注入失败：`ownedWrapperStillAlive=0`，异常 `OWNERSHIP_UNKNOWN`；
- 正常 native0/7、timeout/cancel、quoted、大双流、detached writer 继续通过。

### C-R1-06-R1-02 — 输入转换不再用 YAML superset（已修）

- 删除“保留所有旧行即合法”的宽松规则。`ProjectSettings.asset` 与 Font SDF 的 changed 转换：
  - before 非空行必须原序/原值保留（ProjectSettings 要求子序列顺序；Font 允许实际重排但每行计数保留）；
  - 只允许 **精确白名单 key 的空值新增**；ProjectSettings 白名单由本 pinned 输入实际 Unity
    build 的 123 个字段生成，`m_SubKind` 最多 18 次，其余每 key 最多 1 次；
  - 任何非空新增行（如 `Standalone: ARBITRARY_CODE_PATH`）、列表外 key、额外重复 key、
    旧值/旧行删除/重排（ProjectSettings）均 fatal；
  - before/after SHA 写入 transition 记录，final effective manifest 继续入 receipt。
- 生成 IDE 文件（`.csproj`、`.slnx`、`.vscode/*`、`*.pdb.meta`）从 staged 输入闭包中**明确排除**，
  不再用“XML/JSON 关键字”当作 derivation；可执行 build 输入不受此豁免。
- copy→warmup、warmup→build 两个边界都使用同一 exact transition gate；build 后仍校验
  staged-code-manifest 全代码闭包和 final manifest。

实测：reviewer extra-probe 追加 `scriptingDefineSymbols/Standalone:ARBITRARY_CODE_PATH` 后
`fatal` 非空、`allowed=0`；真实 pinned ProjectSettings/Font 转换在本地 before/after 文件上
仍允许 2 项，证明不是“全拒绝”而是具体字段验证。

### C-R1-06-R1-03 — 生产复用不再接受 fixture（已修）

- `Test-C04PackageSeal` 除 receipt/closure/report 关系外，现要求实际 Unity Player 工件：
  - `Mahjong.exe` 字节包含 `UnityPlayer.dll` 标记；
  - 相邻 `UnityPlayer.dll` 为 PE/MZ 且 >=1MiB；
  - `Mahjong_Data/globalgamemanagers` >=1KiB；
  - `Mahjong_Data/Managed/*.dll` 非空。
  因此系统 `powershell.exe`/文本 DLL/`fixture.cs` 的 `New-PackageFixture -ValidEvidence` 被拒绝，
  不再依赖文件名黑名单或自报字段。
- build evidence 还要求真实 `staged-project-input/final/code-manifest` 并逐项对 staged 树/代码树
  重算（不是只 hash CSV）：缺失、额外、篡改条目 fatal。
- provenance 每个 input 必须在 `staging/repo` 下存在且长度/SHA 匹配；outputs 在 staged plugin
  目录存在且 SHA 匹配；source manifest 与当前 source labels 逐项比对。
- 所有 evidence/provenance 相对成员、祖先 reparse、越界均用现有 safe member 检查。
- 合成 `-ValidEvidence` 生产 main 实测：`syntheticGate.ok=false`（含
  `PLAYER_NOT_UNITY_EXECUTABLE`/`PLAYER_UNITY_DLL_MISSING` 等），`-SkipBuild -Case build`
  **exit2/FAIL**，无 PASS seal。

### C-R1-06-R1-04 — 严格 BuildReport / errors6（保持开放）

- 严格 errors=0/step/exception/output binding 门禁未改；allowlist 仅兼容参数，不参与放行。
- 未重复相同 errors6 heavy build；保留 R3 的 6 条 URP step 原文与失败封存。
- 外部 proposal 保留在 `build/r3-proposal-cfe026ce686d4e2388640ba25590eba1/`（未应用、未批准）。
  版本升级仍是未验证假设；没有把它写成已修复或 external-only BLOCKED。
- 因此无 errors0 完整包、无可信 SkipBuild 正例，C 仍未通过。

## 2. 验证与证据

- Runner tests：**62/62, native 0**；`build/r4-finaltests-8b68aa9410d343ea93cb68d26b634e53/runner.log`，
  SHA `7A27C7D1C0492368B98558F6CDCF509428CBB48CAEAB38289C9708176A844959`。
- Compatibility：**20/20, native 0**；同目录 `cheats.log`，SHA
  `15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`。
- Reviewer probes（新复制根，native0）：
  `peerSentinelAlive=true/ownedStopped=0`；`descendantTraversal=2/2`；
  `syntheticGate.ok=false`；`syntheticMain=exit2/FAIL`；
  `inputMutation.fatal` 非空、`allowed=0`；`strictReport.ok=false`。
  `probes.json` SHA `054CC4B1E7897A30C2267A63247ADD4C56BD131A30C30F339ACFD6AEAE678D86`。
- extra-probes：invalid PE `known=false`；Job query `remaining=-1/cleanup=true`；
  added compiler config fatal。
- assignment-probe：`failure=OWNERSHIP_UNKNOWN`，`ownedWrapperStillAlive=0`。
- 旧 failed fresh build 的 main evidence manifest 实际为 **38 entries**（R3 报告曾写 20，现更正），partial 为 **1091 entries**；两套原件的哈希/长度已由 reviewer 独立重算 0 错误。\n- 未运行 Unity/Player/PlayMode/GAME；未新增/覆盖旧构建证据。

## 3. 提交与边界

- 代码/测试提交：`7ec23a23ebb96b0249b3d303ae2f674ea8915b73`；最终 doc-only HEAD 以
  `git log -1` 为准。
- 改动仅 `run-p45-c04-player-cheats.ps1`、`C04PlayerCheatRunner.Tests.ps1` 与本报告（C# 沿用
  R3 的严格门禁/URP import 诊断；`.meta` 未改）。`git status` clean、`diff-check` 0。
- 全 D04 仍 0 BLOCKER / 3 原 MAJOR，未通过；A/B 未实现，未 push/PR/MR/merge/deploy/approve。
