# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 6）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；分支
`wt/v5-g2-c04-d04-runner-ds-20261103`。R5 独立复核
`build/caller-c04c-r5-8b616eef443249fab8f04a5cedf66acd/REVIEW.md`
（SHA-256 `65FD420C9883AC48728EE74735D882746AAC3D66E0C42919DD96608209406F25`）已全文读取；
subset-run2/subset-probe、probes/extra/assignment/contract 已用 reviewer 原件复现。旧证据只读。

本轮只修 R5 收窄的 **03 完整闭包/构建来源绑定** 与 **行尾 MINOR**。01/02 已独立认可的进程、
重复 key、strict errors99、失败封存保持不动；04 外部 URP/errors6 继续开放。未重复 heavy build，
未改 A/B、包/设置/许可/asmdef，无 approve/MR。

## 1. C-R1-06-R1-03 — 完整 source/staged/provenance/runtime 闭包（已修）

根因修复不是继续加 magic/string，而是**反向完整集合**：

- **source→staged Unity 完整集**：从当前 `Get-C04SourceInputRows` 的全部
  `unity/Mahjong.Unity/**` 推导 expected；除已记录的 ProjectSettings/Font 转换和
  PerformanceTestRun 删除外，任何缺失、额外、字节变化都 fatal。840 源行只保留
  `Boot.unity + 1 code + 10 fixture` 的旧 subset 现在产生数百项
  `PACKAGE_EVIDENCE_REQUIRED_STAGED_MISSING`。
- **source→staged code 完整集**：所有非 `unity/**` 源文件必须出现在
  `staged-code-manifest` 且长度/SHA 一致；缺失 Domain/Client/Contracts/LocalHost/tools/tests/
  golden 等均 fatal，额外 code 也 fatal。
- **provenance 完整集**：用既有生产 helper `Get-SharedPluginInputClosure -RepoRoot $root`
  计算权威 152 项 expected set；provenance `inputs` 必须与之**精确相等**（不是子集），缺项、
  多项、重复/改标签均 fatal。共享输出与 plugin closure 精确匹配。
- **plugin/runtime 实际字节**：closure 中 DLL 必须是有效 PE；`Mahjong.*`/`Google.Protobuf`
  必须与 `Mahjong_Data/Managed` 中同名 DLL 字节一致；text 共享 DLL/旧 runtime 混用被拒绝。
- **fresh 执行记录**：新增
  `build/player/c04-build-execution.json`，成功路径记录 sourceIdentity/playerSha/
  unityExitCode/targetPid/targetStartFailed/buildId；reuse 要求存在、`kind` 正确、
  unity exit0/known、targetStartFailed=false、source/player/buildId 与 seal/receipt 一致。
  旧 failed artifact + 新 fixture report/receipt 不能再冒充当前 BUILD PASS。
- 保留真实 PE/import/Unity serialized 校验、before/final 分离、完整 raw tree、safe member
  reparse/越界控制；provenance `input.Path`/`output.Name` 和 `buildEvidenceRelativePath` 均经
  `Resolve-C04RelativeMember`。

实测 reviewer subset-run2（复制到新根）：
`gateOk=false`，`mainExit=2`，`mainStatus=FAIL`，`gameStatus=NOT_RUN`，
`sourceInputCount=840`，`stagedUnityCount=10`，`stagedCodeCount=1`，`provenanceInputs=1`，
`stagingHasDomain=false`，`newBuildPerformed=false`；错误包含数百个
`REQUIRED_STAGED_MISSING`/`REQUIRED_CODE_MISSING`、`PROVENANCE_MISSING_INPUT`、
`BUILD_EXECUTION_MISSING`、`PLUGIN_NOT_PE`/`MANAGED_MISSING`。不再 PASS。

保留真实组件正例：历史 failed-build 的真实 Unity player 通过 PE/import/Unity 数据检查
（65 项测试中的 `real-unity-player-artifact`）；这只是 artifact/raw-closure 组件，不是 clean
build/GAME/PASS。

## 2. C-R5-FORMAT — 行尾/基线 diff（已修）

- 将 runner/tests/report 统一恢复为 **LF、UTF-8 无 BOM、无行尾空白**；没有改全局 Git 配置。
- `git diff --check 1bc348aaf78c7773af1495981efea7f93eb0a5c9..HEAD` 现为 **exit0**；
- 工作树 `git status --porcelain` clean。

## 3. 验证

- Runner tests：**65/65, native 0**（`build/r6-tests-81ac2fe04be341d3b594d970f8d2a848/runner.log`）。
- Compatibility：**20/20, native 0**，SHA
  `15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`。
- reviewer probes/extra/assignment/contract（新 GUID 根）native0：
  peer alive/owned0、traversal2/2、synthetic FAIL/main exit2、invalid PE unknown、
  Job query remaining-1/cleanup true、assign wrapper alive0、duplicate key fatal、
  unknown extra 结构化 fatal、合法 transition `allowed1/fatal[]`。
- subset-run2：`gateOk=false/mainExit=2/FAIL`，见第 1 节。
- 未运行 Unity/Player/PlayMode/GAME；未重跑 errors6；未改历史原件。

## 4. 提交/边界

- 代码/测试提交：`f40e0777599208874141b5de87aafea1141bd32b`；最终 doc-only HEAD 以
  `git log -1` 为准。
- 改动仅 runner、Runner tests、本报告；`.meta`/C#/A/B/GAME 未改。
- C 仍未通过：无 errors0 clean package、无可信 SkipBuild 正例；全 D04 仍 0 BLOCKER /
  3 原 MAJOR，无 approve/push/PR/merge/deploy。
