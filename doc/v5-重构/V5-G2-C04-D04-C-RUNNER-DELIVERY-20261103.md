# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 11）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
代码 HEAD：`9d76dfc345b6b9902d674b46a9455e90c58c963c`（基于 118b2eec；R1-03 保持）。

## 1. R1-06-R1-04：短路径导入对照与真实 errors0

选择路线 C 的路径假设验证；在**新独占短根** `build\c976fe390` 建全新 staging，
复制真实 shared plugin 后仅做只读诊断：

```text
C04DIAG allCount=2 mainType=UnityEngine.ComputeShader
C04DIAG asset type=UnityEngine.ComputeShader name=TraceVirtualOffset main=True
  guid=ff2cbab5da58bf04d82c5f34037ed123 localId=-6772857160820960102
C04DIAG asset type=UnityEngine.Rendering.RayTracingShader name=TraceVirtualOffset main=False
  guid=ff2cbab5da58bf04d82c5f34037ed123 localId=-5126288278712620388
```

同一 `.urtshader` 在旧 267 字符路径下仅返回 ImportLog；在短根下正确产生
ComputeShader 与 RayTracingShader。诊断日志
`build\c976fe390\diag2.log` SHA `3C697C0037E9B7202F19EA6375A76327D53F189CEBD0AB106C0372D7D99E0361`。

随后在短根 `build\bebfca180` 进行一次全新 fresh build：

- BuildReport：`result=Succeeded, errors=0, stepErrorCount=0, stepExceptionCount=0`；
- native exit=0；summary PASS；无 FAIL；
- player `Mahjong.exe` 667136 bytes，SHA
  `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；
- BuildReport SHA `FB7C9A696DD930F1A45198C99577FA07C3359FB5AC99123C8950D9AA22419BF4`；
- package manifest SHA `1F78EC848145DB0096970BDFF77AE499009BC216F976EE94EBE13AD0AFA77930`；
- package seal SHA `B1000A9C6D1FFD29D00CC96C0FE90E3767E604F3B1A1B01FC4ECF3251082D7CB`；
- build receipt SHA `A60443B0BBE3E966A71B13B0EDA5B6CE32424EE6054460409BCF82B540372C72`；
- build execution SHA `CA35EB1087EF01A4AC4D5D2F2FC06241C1D33094A6576DAFD4A2E1BCFE8F959D`；
- source identity `dbebb4011743d56ed4dbea0cac1152015254dde3ff7a49077bef53c3183c50d6`。

同一包真实 SkipBuild（`build\s8e7da647`）：
`SKIPBUILD validate sealed package` → `BUILD_ONLY component PASS`，exit0；
`Test-C04EvidenceSeal` errors=[]，`Test-C04PackageSeal.ok=true`。

E2：未修改 `manifest.json`/`packages-lock.json`/`ProjectVersion.txt`，版本 diff 为空；
未改包源/Editor/global settings。短路径是新的构建根，不是版本变更。

E3（同包六行 GAME）：**NOT_RUN**。仓库没有
`dotnet/tools/C04PlayerCheatVerifier2`（Find-C04Verifier 两条候选均不存在），
D04-A Verifier2 也未交付；不能用旧/伪造 evidence 替代。E3/E4 六行断言未通过，故 R1-04
仍不能关闭。

## 2. C-R10-EVIDENCE：旧封存根被改写事件

诚实记录：R10 的 `C04ShaderHostTypeFix.Diagnose` 实际在旧
`build/r9-routeC3-d2e9d3619dc743c1a6161ae9a8774c49/staging/repo/unity/Mahjong.Unity`
上启动 Unity，并把诊断脚本写入旧 Assets。这是本轮新发生的违规执行事件，不是推测。

- R9-C3 原 `partial-artifacts.csv` 记录该文件 2136 bytes / SHA
  `02EDF1D45127DEBA34CB88BFDBE8E1266F4E4851DFF76704613BD6132D4E4D79`；
- 当前旧文件 SHA `3917AE43728713DDEF9903D3365A657E4855807F61DA960E2FB49ACD2E74B9F4`；
- 旧 R9-C3 `Test-C04EvidenceSeal` 现报
  `EVIDENCE_PARTIAL_HASH:staging/repo/unity/Mahjong.Unity/Assets/Editor/C04ShaderHostTypeFix.cs`；
- R9-C2 仍完整；R9 caller 原件只读保留。

处理边界：**不恢复、不重封、不重算、不删除**旧 C3 原件；旧 C3 不再称为完整原件，
其 BuildReport/errors3 仍作为失败历史有效；manifest/seal 保持当前损坏现场。后续实验只能
新根，且本轮所有新 projectPath/output/log 均在 `build\c976fe390` / `build\bebfca180` 等
新独占根。

## 3. 验收对照

| 断言 | 结果 |
|---|---|
| A9-1 errors=0 | **PASS**（bebfca180） |
| A9-2 native0/无 FAIL | **PASS** |
| A9-3 版本官方配套 | 无版本变更 |
| A9-4 E3/E4 | E4 **PASS**；E3 **NOT_RUN**（缺 Verifier2） |
| A9-5 E5 | runner 76/76、compat 20/20；baseline diff-check0 |
| A9-6 范围/声明 | 未改授权外 tracked；GAME NOT_RUN，未称 C/G2 通过 |

E5 证据：`build/r11-tests-01f450dd69814f0ea4d92850a499a601/runner.log`；
compat SHA `15B40D45D430574A5D2B9F8057F4F3FD2F697C1E273E073DE77F88CB55FAB8F4`。
`git diff --check 1bc348aa..HEAD` exit0；worktree clean。

## 4. 仍需的最小条件

要完成 E3，只需提供/构建 D04-A 的 `C04PlayerCheatVerifier2`（官方路径
`dotnet/tools/C04PlayerCheatVerifier2/bin/Release/net8.0/...dll`），或明确授权使用一个已有的
六行合同 verifier；不需要新的包/Editor/global settings 授权。获得 verifier 后，使用本 same
package `build\bebfca180\build\player` 做真实 SkipBuild + 六行断言，不再重跑 Unity。

E1/E2/E4 已达成，E3 未达成；C 仍未通过，无 approve/MR/push/merge/deploy。
