# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 8）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`。R7 独立复核
`build/caller-c04c-r7-2fa3596905e74fdf829767072706205b/REVIEW.md`
（SHA-256 `CEE7AF920FFC6846DC62817137A9838649BA91215568B4E922F2F90E907455A8`）已全文读取；
policy-probes / success-and-consumer 原件已复现。01/02、strict99、旧 subset 拒绝、FORMAT 保留。

本轮只修 03 的 A/B；未改 A/B/GAME/包设置/许可/asmdef，未重跑 errors6，04 外部 URP 仍开放。

## 1. C-R1-06-R1-03 修复映射

### A. Unity IDE 排除不得误删真实 .NET csproj

- `Get-C04EffectiveSourceRows` 现仅对 **`unity/Mahjong.Unity/*` 内**的
  `.csproj`/`.slnx`/`.pdb.meta`/`.vscode` 派生文件排除；`dotnet/**` 的真实 MSBuild 项目定义
  全部保留。
- `Get-C04SourceIdentity`、source/staged copy、source manifest、consumer expected/sourceByPath、
  code reverse/expected 继续共用该 helper。
- 真实 `dotnet` csproj 恢复为 source/code/provenance 的必需输入；`.slnx` 仍按 Unity 派生排除。
- policy-probes 实测：raw dotnet projects **34**，effective **34**；7 个 shared csproj 仍出现在
  `Get-SharedPluginInputClosure`；临时 Git fixture 修改 `Example.csproj` 的
  `DefineConstants` 后 identity 由
  `6bd02d098868fd779cbbb4c2d6a28ff49512d8d4a2d4ff12ee372897c83471e0`
  变为 `235a9768e901020bdfff17b2ef8f28a51d4e24b59f639e0077b6b5a015903117`。
- success-and-consumer 的 real staged `Domain.csproj` consumer errors 为 `[]`。

### B. provenance duplicate 明确拒绝

- `actualProvMap` 建 map 前使用规范化（`/`→`\`、lowercase）路径；已存在时立即
  `PACKAGE_EVIDENCE_PROVENANCE_DUPLICATE_INPUT`，不再静默覆盖。
- 同时保留 source/staged/code/closure 的重复拒绝；expected 仍使用
  `Get-SharedPluginInputClosure` 权威完整集，hash/length/双向成员检查未放宽。
- policy-probes 的 otherwise-correct 单输入 + 重复项：`declaredEntries=2`、`uniqueEntries=1`、
  `errors=[PACKAGE_EVIDENCE_PROVENANCE_DUPLICATE_INPUT:dotnet/Directory.Build.props]`。

## 2. 正/负矩阵

- **Positive**：producer execution/provenance 真实命令通过（record true、destination true、
  package true、CWD false）；真实 dotnet csproj 保留；Unity `.slnx` raw 有/effective 无；
  真实 staged `Domain.csproj` consumer 无错误。
- **Negative**：修改真实 csproj 的 DefineConstants/引用/编译输入改变 identity；缺失/篡改
  任一真实输入失败；重复 provenance 明确失败；旧 840→1+10 subset 仍 main exit2/FAIL；
  历史 8 个 shared DLL/Managed 字节校验保持。
- 未运行 Unity/Player/PlayMode/GAME；无 errors0 clean package。

## 3. 验证/证据

| 项 | 结果 |
|---|---|
| Runner tests | **76/76, native 0** |
| Compatibility | **20/20, native 0** |
| policy-probes | raw/effective dotnet 34/34；identity 改变；duplicate 明确报错 |
| success-and-consumer | producer 全 true；real Domain.csproj consumer errors `[]` |
| subset | gate false / main exit2 / FAIL，含 `REQUIRED_CODE_MISSING:...Domain.csproj`，无 `.slnx` 假必需项 |
| baseline diff-check | exit0；worktree clean |

证据：

- tests：`build/r8-tests-01da546fccf1460c842ccbe6c0d0ceed/runner.log`；
- policy：`build/r8-policy-9142cb4bb1c4442c8b5c333cea07d986/policy-probes.json`；
- success/consumer：`build/r8-success-4c3a612d7bbf44668c64f1d1b46fd990/success-and-consumer.json`；
- subset：`build/r8-subset-a0ac656ca4d842179c4c1152fdd8cad4/subset-probe.json`。

## 4. 提交/边界

- 代码/测试提交与最终 HEAD 以 `git log -1`/`git rev-parse HEAD` 为准。
- 改动仅 runner、Runner tests、本报告；未改 `.meta`/C#/A/B/GAME。
- C 仍未通过：无 errors0 clean package、无可信 SkipBuild 正例；全 D04 仍 0 BLOCKER /
  3 原 MAJOR，无 approve/push/PR/merge/deploy。
