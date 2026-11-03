# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 5）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；分支
`wt/v5-g2-c04-d04-runner-ds-20261103`。R4 独立复核
`build/caller-c04c-r4-4dc191082fc244fc9ce00206e843de55/REVIEW.md`
（SHA-256 `384A9D6FEB3368D4C3F6B2750A94F5011512340ED4A72BDC0609F973A12DF504`）已全文读取；
probes/extra-probes/assignment-probe/contract-probes 已用 reviewer 原件复现。旧证据只读。

本轮只修 R4 收窄的 02/03 两组；01 的 structured target-result、Job unknown、assign 清理
保持不动；04 严格 BuildReport/external URP proposal 保持开放。未重复相同 errors6 的 Unity
构建，未改包/设置/许可/asmdef，无 approve/MR。

## 1. 逐 ID 修复

### C-R1-06-R1-02 — 重复 YAML key / 结构 / 残留调用（已修）

- `Test-C04AdditiveYamlTransition` 不再只数整行：
  - 对每个 YAML key 统计 before/after 出现次数；任何 before 已存在的 key，after 次数不得增加
    （`m_SubKind` 仅保留实际多值例外），因此
    `additionalIl2CppArgs: --keep-existing-options` 被追加空值同名 key 时立即 fatal；
  - 保留旧行/旧值、ProjectSettings 顺序子序列、Font 实际重排的既有检查；
  - 新增只允许精确白名单 key 的空值，非空 compiler define/列表外 key/未知资产均 fatal。
- 删除 `Split-C04StagingTransitions` 中两处已删除 `Test-C04GeneratedUnityFile` 的残留调用；
  `STAGING_EXTRA:Assets/Unexpected.asset` 现在返回结构化 fatal，不再 CommandNotFound。
- IDE 生成文件 `.csproj/.slnx/.vscode/*.pdb.meta` 继续按 manifest 排除策略处理，不再用
  关键字当 derivation；真实 build 输入仍 fail-closed。

实测 contract-probes：
- duplicateExistingCompilerKey：`fatal=[STAGING_CHANGED:ProjectSettings/ProjectSettings.asset]`,
  `allowed=0`；
- unexpectedExtra：`throws=false`，无 CommandNotFound；
- 真实 pinned ProjectSettings+Font before/after：`fatal=0/allowed=2`。

### C-R1-06-R1-03 — fake Unity bytes / source linkage / pre-post manifest（已修）

- `Test-C04UnityPlayerArtifact` 改为结构/来源验证，不再只看 MZ/字符串/大小：
  - exe 必须是可解析 PE（e_lfanew/PE 签名/COFF/optional/section table），且导入表确实导入
    `UnityPlayer.dll`；
  - `UnityPlayer.dll` 必须为有效 PE（>=1MiB）；
  - `Mahjong_Data/globalgamemanagers` 必须在偏移 48 含 Unity 版本串
    （`6000.3.27f1` 形态），不是任意文本；
  - `Mahjong_Data/Managed/*.dll` 至少一个为有效 PE。
- package/seal evidence 增加 source→staged 关系：
  - staged Unity 输入行必须是 source manifest 的 `unity/Mahjong.Unity/<path>` 同字节；
  - 仅 ProjectSettings/Font 的受控转换和 `Assets/Plugins/Mahjong/*` 生成闭包可豁免 source 同字节；
  - staged-code 行、provenance `inputs.Path` 必须存在于 source manifest 且长度/SHA 一致；
  - provenance `outputs.Name` 必须落在 staged plugin 目录且 SHA 一致。
- 当前 tracked `New-PackageFixture -ValidEvidence -UnityArtifact`（MZ+字符串/零字节假 DLL/
  g 文本 globalgamemanagers/文本 Managed DLL/fixture.cs）现在被生产 main 拒绝：
  `syntheticGate.ok=false`（`PLAYER_NOT_VALID_PE`、`PLAYER_MISSING_UNITYPLAYER_IMPORT`、
  `PLAYER_UNITY_DLL_NOT_PE`、`PLAYER_DATA_NOT_UNITY_SERIALIZED`、
  `PACKAGE_EVIDENCE_STAGED_NOT_SOURCE` 等），`-SkipBuild -Case build` **exit2/FAIL**。
- 预/后 manifest 分离：
  - main 在 build 前把冻结 staged project 复制到 `staging/before-build/unity/Mahjong.Unity`；
  - `Test-C04PackageSeal` 用 **before snapshot** 校验 staged-input manifest，用 **final tree**
    校验 final manifest，再用 `Split-C04StagingTransitions(before→final)` 校验合法转换；
  - 不再要求两个不同状态同时等于终态。
- `buildEvidenceRelativePath`、provenance `input.Path`/`output.Name` 改为
  `Resolve-C04RelativeMember`（绝对/`..`/越界/祖先 reparse 检查），不再直接 Join 后读取。
- contract-probes 的合法空 key 转换：`transitionAllowed=1/fatal=[]`，reuse 不再报
  `PACKAGE_EVIDENCE_STAGED_TREE`（仍因 fixture 假 artifact/linkage 而拒绝，这是预期的
  fake 负例）。
- 真实组件正例：历史 failed-build 的真实 Unity player 通过 `Test-C04UnityPlayerArtifact`
  与 PE 检查（65/65 测试内 `real-unity-player-artifact`），仅证明 artifact/raw-closure 组件，
  不声称 clean build/GAME/package PASS。

### C-R1-06-R1-04 — 严格门禁/外部缺口（保持）

- 严格 errors=0/step/exception/output 门禁未改；errors99 负例仍拒绝。
- 未重复不变 Unity 构建；历史 errors6/native1/runner3 仅作失败历史，未作为新 runner 验证。
- URP 17.3→17.4 proposal 仍未应用、未验证，不构成修复或 external-only BLOCKED。
- 无 clean errors0 完整包，无可信 SkipBuild 正例；C 仍未通过。

## 2. 验证与证据

- Runner tests：**65/65, native 0**；`build/r5-finaltests-e31d6daf335d45589deb5e70913bdc7f/runner.log`
  （最终提交测试日志；runner SHA `BBCDFC2B243795DB70617413772BD6D72D30FE874405A217673F00CF89D197C3`）。
- Compatibility：**20/20, native 0**；`15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`。
- Reviewer 四探针（新 GUID 根，native0）：
  - probes：peer alive/ownedStopped0；traversal2/2；syntheticGate.ok=false；
    syntheticMain exit2/FAIL；inputMutation fatal/allowed0；strict errors99 拒绝。
  - extra-probes：invalid PE `known=false`；Job query `remaining=-1/cleanup=true`；
    added compiler define fatal。
  - assignment-probe：assign 失败 `ownedWrapperStillAlive=0`。
  - contract-probes：duplicate fatal、unexpectedExtra 无异常、valid transition
    `transitionAllowed=1/fatal=[]`，fake reuse 继续拒绝。
- `probes.json` SHA `AE8D9163FDEBCDC42318882CC8BDEB336BE9476997549489DDA0DFDA2146CDDE`；
  `extra-probes.json` SHA `982B00E9620CCC8CA3FB1F1F05CA194C194E4A6E12C8D1E5E73DE46270B028DD`；
  `assignment-probe.json` SHA `46BED1A519E8069C0AED592AA2C4491BEBFDF1BB88801E1B317F0B19E62587B5`；
  `contract-probes.json` SHA `E6AC66C89632E68E82750AE832A4C79B8EAA988C6FED2134BE271AD314F4DEB5`。
- 历史 failed build main manifest 38、partial 1091，未重跑/未改。

## 3. 提交/边界

- 代码/测试提交：`d97b301f04cfa68895c35bfdfa79d94c95855918`；最终 HEAD 以 `git log -1` 为准。
- 本轮改动仅 runner、Runner tests、本报告；`.meta`/C#/A/B/GAME 未改。
- `git status` clean、`diff-check` 0；未 push/PR/MR/merge/deploy/approve/另派。
- 全 D04 仍 0 BLOCKER / 3 原 MAJOR，未通过；无 clean build/package/SkipBuild 正例。
