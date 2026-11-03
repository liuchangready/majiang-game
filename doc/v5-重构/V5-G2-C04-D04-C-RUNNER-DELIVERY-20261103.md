# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 12）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
代码 HEAD：`69b9e669a3094ba00806b333730733d9ab12ebba`。R11 已认可 E1/E2/E4；本轮只修
默认路径/cheap 预检残项，未重开 01/02/03/FORMAT/strict99。

## 1. 默认短根与路径预算

- 默认 `EvidenceRoot` 改为 `build\c<8hex>`（如 `build\c72008d51`），不再使用
  `build\c04-d04c-yyyyMMdd-HHmmss-8hex`。
- 每次运行生成完整 `runId`（GUID）与 `requestedUtc`，写入 `run-id.txt` 和 `summary.json`；
  短目录冲突时递增 8 位片段，超过 32 次失败，不覆盖已有 root。
- 新增 `Get-C04PathBudgetErrors`，在创建 root/mutex/共享构建/Unity 前检查关键路径 <=250：
  - `staging\repo\unity\Mahjong.Unity`
  - `...\PackageCache\...\TraceVirtualOffset.urtshader(.meta)`
  - `...\com.unity.visualscripting@...\YamlDotNet\Unity.VisualScripting.YamlDotNet.dll`
- 超长显式 root 直接 `C04FAIL:PATH_BUDGET`，包含路径长度/阈值/可用短根建议；不创建
  evidence root、不等待 mutex、不启动 Unity。
- `-SkipBuild` 不创建 Unity staging，不做 staging 路径预算，避免无关长输出误拒。

## 2. 实测

Default root fresh build（无 `-EvidenceRoot`）：
`build\c72008d51`，`runId=72008d51-e8c6-41d3-b0dd-e9e7350bef77`，
`requestedUtc=2026-11-03T05:16:59.3778789Z`。

| 项 | 值 |
|---|---|
| BuildReport | `Succeeded, errors=0, stepErrorCount=0, stepExceptionCount=0` |
| native | **0**；summary PASS |
| BuildReport SHA | `93648E317937FD3A7D3FA1D3478A72EEA82A95680F30CE53100C5710ECB18996` |
| package manifest SHA | `DE903AE8651F400FBCE7EB6F0D477A8EC521F011DD6E40B06B08563E7453FBBA` |
| package seal SHA | `D67E34645CF26FB7F07CD995555EF5AD4CCB019B501A8C9BE9EC658108ADFCF1` |
| execution SHA | `5B82A4BF0BDD6FEAF0A70C8AB5B4D163696317689BF63EF3A28D505EB4EB53BA` |
| player | 667136 bytes，SHA `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807` |
| source identity | `9ff8a3ef59fce909c18c0bbcd09c14aee2b06fc073f92ae1e0e65ba4f503d4c3` |

同包 SkipBuild（`build\s68b0d394`）：`BUILD_ONLY component PASS`，exit0；
`Test-C04PackageSeal.ok=true`。

长根负例：
`build\c04-d04c-<GUID>` 在无 mutex/Unity 下 exit1：
`PATH_BUDGET:trace-urtshader-meta:270>250,...yaml-dotnet-dll:278>250`；未创建 root。

短根正例：`build\c12345678` 预算 0 错误。

## 3. 验收对照

| 断言 | 结果 |
|---|---|
| A9-1 errors=0 | **PASS**（c72008d51） |
| A9-2 native0/无 FAIL | **PASS** |
| A9-3 版本官方配套 | 无版本变更 |
| A9-4 E3/E4 | E4 **PASS**；E3 最终同包集成门禁，未在本轮执行 |
| A9-5 E5 | runner **78/78**、compat 20/20；baseline diff-check0 |
| A9-6 范围/声明 | 未改授权外 tracked；GAME NOT_RUN，未称 GAME/G2 通过 |

E3 说明：A thread 已完成并接受 `ca7d5c15...`；B 在 waiting_review；仓库尚无
`C04PlayerCheatVerifier2`。按本轮指令，E3 是最终同包集成门禁，不作为本次 C 路径残项，
也不虚报通过。

## 4. 证据完整性事件（保留）

R10 诊断曾在旧 R9-C3 根启动 Unity 并改写
`Assets/Editor/C04ShaderHostTypeFix.cs`：原 2136 bytes/SHA
`02EDF1D45127DEBA34CB88BFDBE8E1266F4E4851DFF76704613BD6132D4E4D79` →
当前 `3917AE43728713DDEF9903D3365A657E4855807F61DA960E2FB49ACD2E74B9F4`；
旧 C3 seal 现报 `EVIDENCE_PARTIAL_HASH`。未恢复/未重封/未重算/未删除；旧 C3 不再称完整原件，
其 errors3 BuildReport 仍作失败历史；R9-C2/caller 原件只读保留。后续实验均在新根。

## 5. 提交/边界

- 代码/测试提交：`69b9e669a3094ba00806b333730733d9ab12ebba`
- E5：`build/r12-tests2-818a1413ce5e4423b3a07ed5ebd3a1ca/runner.log`；compat SHA
  `15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`；
- `git diff --check 1bc348aa..HEAD` exit0；worktree clean。
- 未改包/Editor/global settings/许可，无 push/PR/MR/merge/deploy/approve。

## 6. 调用方独立验收与统一 PR 收口（2026-11-03）

调用方已批准 D04-C 自身组件范围：受审 `f042fbf10cecac6d5d0a4f4df2777bfd7e220fb4`，
代码 `69b9e669a3094ba00806b333730733d9ab12ebba`。本次关闭默认路径与廉价预检残项，
保留原进程所有权、完整源码/生成物/provenance/运行时闭包及严格 BuildReport 门禁。

当前任务工作区实际重跑 Runner **78/78**、compat **20/20**，native0；基线 diff-check0、
tracked clean。独立验证默认根 `c72008d51` 原始 BuildReport 为 Succeeded/errors0/
stepErrors0/stepExceptions0，四外部阶段均 known/native0/remaining0/cleanupFailed=false。
当前源身份 `9ff8a3ef59fce909c18c0bbcd09c14aee2b06fc073f92ae1e0e65ba4f503d4c3`
与真实包绑定，生产 package gate ok、fresh/reuse evidence seal 均 errors=[]。
构建原记录的 sourceHead/buildCommit 为 `861fbde6573b48502e28e3ac6bdd5cccd11f0d54`，
实际冻结工作字节由 input manifest/上述 identity 绑定，不以最后的文档 HEAD 替代构建记录。

调用方实际执行同一包的 build-only SkipBuild：native0/PASS/GAME NOT_RUN、没有构建阶段，
新的长输出根封存校验通过。显式超长 fresh 根实际 native1/PATH_BUDGET，未创建根、
未等 mutex、未启动构建；真实 dotnet csproj raw/effective=34/34。首次调用方探针漏传
SourceHead 而被 USAGE 拒绝，修正后通过；初次原件已保留，不记为产品失败。

独立报告：
`E:/majiang-review/g2-c04-d04-runner-ds-20261103/build/caller-c04c-r12-571a3b447c7943fc8d3177f1bbcf0f07/REVIEW.md`，
SHA `740153F65E22C2C66630C569590092F463C9AFA1AABA0D1F1EED4D4FAFF81664`；
同根含全部命令脚本、退出码、结果、保留的初次失败和 `evidence-sha256.csv`。
R10 对旧 C3 的完整性事件继续按第4节保留，不恢复、不重封掩盖。

统一集成以当前 PR20 远端 `ae17ca5cb9af685470458498a4c1feaec1a2ec48` 为基线，
无冲突 merge 本任务得到 `b5a0ad5188e6e9ef4da355334eabe8acb261a463`。
五个 C 路径与受审候选逐字节一致，已发布 A/D05 成果保持，未导入未审 B 在途树。
该合并代码实际 Runner **76/76**、compat **20/20**，native0；此新工作树不包含历史 R3
产物，所以该脚本内两条条件性旧产物检查未执行。调用方另用合并后的生产函数检查本次
已接受真实 c72008d51 产物：artifact errors=[]、PE=true（两项通过）。这不是新的集成包
构建或 GAME 运行。最后追加本节仅文档，不改已测代码。

调用方只向既有统一 `feat/v5-post-g1-batch` / OPEN Draft PR20 普通推送一次并登记 MR，
不另建组件 PR、不合并 v5、不部署。完整 E3 同候选同包六行仍由 A+C→B 最终集成取得，
本次不声称 E3、D04/C04/G2–G5 或新远端 HEAD CI 通过；所有最终断言和旧失败保留。
