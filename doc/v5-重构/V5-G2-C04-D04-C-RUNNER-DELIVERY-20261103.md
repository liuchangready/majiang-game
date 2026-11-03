# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 7）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`。R6 独立复核
`build/caller-c04c-r6-37ac0ff3c2fc4baf9724d16135f37084/REVIEW.md`
（SHA-256 `02F864A7C08BA44BB1D00C62CE58C9F392DA04D929BA85C1D7055802FDEA9C77`）已全文读取；
positive-run2 与 subset 原件已复现。01/02、strict99、旧 subset 拒绝、FORMAT 保留。

本轮只修 R6 新成功路径 A/B/C；未重复 Unity heavy build，未改 A/B/GAME/包设置/许可/asmdef，
04 外部 errors6/URP 仍未授权、仍开放。

## 1. C-R1-06-R1-03 修复映射

### A. 成功路径 provenance 赋值/执行记录

- 原 `Write-C04Json ... -Object $executionRecord        $provenanceCopy = ...` 已消除；不再把赋值作为多余参数传给函数。
- 现在 `Write-C04Json` 的实际命令在同一命令的布尔表达式里完成
  `$provenanceCopy = Join-Path $packageRoot 'c04-plugin-provenance.json'`，命令文本仍可被
  reviewer AST 定位/执行；随后独立校验：
  - `$provenanceCopy` 非空；
  - 绝对路径位于 package root 内；
  - 再执行 Copy-Item。
- `$playerRelative` 在 main 的 executionRecord 构造前由 `$playerFull`/package root 明确推导，
  executionRecord 的 `playerRelativePath` 不再依赖函数局部名。
- 失败路径不会把 null Destination 交给 Copy-Item/CWD。

实际 positive-run2（复制到新根）：
`executionRecordWritten=true`、`provenanceDestinationAssigned=true`、
`requiredPackageProvenancePresent=true`、`misdirectedIntoCwd=false`、`error=''`。

### B. producer/consumer 统一排除策略

- 新增 `Get-C04EffectiveSourceRows`，对 raw source 统一排除
  `.csproj`/`.slnx`/`.pdb.meta`/`.vscode/`（IDE 派生，不作为冻结 build 输入）。
- `Get-C04SourceIdentity`、source/staged copy、source manifest、consumer `expectedSource`/
  `sourceByPath`、code reverse/expected 全部使用同一 effective helper。
- 不再出现 `PACKAGE_EVIDENCE_REQUIRED_STAGED_MISSING:Mahjong.Unity.slnx`；真实
  Domain/Client/Contracts/LocalHost/tools/tests/golden 仍全部为必需项。
- 真实 source 策略测试：raw 含 `unity/Mahjong.Unity/Mahjong.Unity.slnx`，effective 不含；
  producer/consumer 正例通过。

### C. duplicate exact-set assertion

- source manifest actual map、stagedMap、codeMap、provenance actualProvMap 均在赋值前检查重复，
  发现重复添加 `*_DUPLICATE` 错误；closure rows 按 name 检查 `PACKAGE_EVIDENCE_PLUGIN_DUPLICATE`。
- 保留 `Get-SharedPluginInputClosure` 权威 expected set，不降低完整集。

## 2. 正/负矩阵

- Positive producer：AST 提取真实 `Write-C04Json` 命令 + 后续 actual Copy-Item，在隔离 CWD
  执行；execution record 写入、provenance 目标赋值/包内存在/字节等于 fixture、无 CWD 文件。
- Positive source policy：raw 含 `.slnx`、effective 不含；所有真实非 IDE 输入仍必需。
- Negative subset：840→1+10 旧 artifact 包仍 `gateOk=false/mainExit=2/FAIL`，错误含数百项
  `REQUIRED_STAGED_MISSING`/`REQUIRED_CODE_MISSING`/`PROVENANCE_MISSING_INPUT`/
  `BUILD_EXECUTION_MISSING`/`PLUGIN_NOT_PE`/`MANAGED_MISSING`，不再含 `.slnx` 假必需项。
- 真实历史 8 个 shared DLL 与 Managed 对应 DLL 字节一致，保留校验；无放宽。
- 未运行 Unity/Player/PlayMode/GAME，无 clean errors0 package；04 仍开放。

## 3. 验证/证据

- Runner tests：**73/73, native 0**（`build/r7-finaltests-94e0da8acc9147cc9956d28a40d25b89/runner.log`，SHA `802CDA00DD349EF204B99B012598FD0576CAB3FF8234512FF94366EB4686B4F7`）；
- Compatibility：**20/20, native 0**。
- positive-run2：`build/r7-finalpositive-c9d98be1891c4020bc89d209b3f41a78/positive-path-probes.json`（SHA `D36B400413510983944837E5865F8F0EF6F558CA40AB10695D90920F27B72A0B`）。
- subset：`build/r7-subset-528d969a25c34a4f9bf62b61df46aa32/subset-probe.json`（SHA `8FC00CBD7E661DCB3EACB110456D7FFD0A9D40333EEFE03B735E21BC9F383DD2`）
  （gate false/main exit2/FAIL，无 `.slnx` 错误）。
- reviewer probes/extra/assignment/contract 仍 native0；fake PE、重复 key、unknown extra、
  合法 transition 结果保持。
- 基线 `git diff --check 1bc348aaf78c7773af1495981efea7f93eb0a5c9..HEAD` exit0；
  工作树 clean。

## 4. 提交/边界

- 代码/测试提交与最终 HEAD 以 `git log -1`/`git rev-parse HEAD` 为准。
- 改动仅 runner、Runner tests、本报告；未改 `.meta`/C#/A/B/GAME。
- C 仍未通过：无 errors0 clean package、无可信 SkipBuild 正例；全 D04 仍 0 BLOCKER /
  3 原 MAJOR，无 approve/push/PR/merge/deploy。
