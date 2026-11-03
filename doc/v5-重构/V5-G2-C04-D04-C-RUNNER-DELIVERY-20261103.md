# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 10）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
代码 HEAD：`2283adaf05b4f19fbe13aad69c98fc17cf2f9803`。R1-03 A/B 修复保留
（tests 76/76、compat 20/20；dotnet csproj 34/34；provenance duplicate 明确报错）。

本轮按 R9 新类型依据继续路线 C 的**只读导入/类型诊断**；结论是不能用现有项目侧
SerializedObject/fallback 修复，按停止规则回报。

## 1. 实际诊断（新信息）

对 staging 项目执行只读 Unity 诊断：
`C04ShaderHostTypeFix.Diagnose`，日志
`build/r10-diag2-c24e846b8b944d0ea8b099cfca2bb9b4.log`
（SHA `8ECD5A1F599E6580D5CDBBA5B3CD8F6B53CFA49F2CEA010712F721BD16E0458A`）：

```text
C04DIAG allCount=1
C04DIAG asset type=UnityEditor.AssetImporters.ImportLog name=Import Logs main=False
C04DIAG settingsCount=1
```

即 `AssetDatabase.LoadAllAssetsAtPath(
Packages/com.unity.render-pipelines.core/.../TraceVirtualOffset.urtshader)`
只返回 **ImportLog**，没有 ComputeShader，也没有 RayTracingShader 子对象。该 `.urtshader`
导入实际上是失败的，因此：

- 无法通过 `LoadAllAssetsAtPath` 取得真实 ComputeShader/RayTracingShader 做字段绑定；
- 之前把所有 null Shader 填 `Hidden/InternalErrorShader` 的路线 C 尝试不能保持语义，且不能
  消除 TraceVirtualOffset 错误；
- 正确修复必须修 **包导入器/包版本**，不是 global settings 字段或内存 SerializedObject。

## 2. 已排除方案与证据

- 路线 C（项目侧 SerializedObject/fallback）：R9 两次实测仍 errors=3；
  `build/r9-routeC2-b29144f9136943089bdba6c0452bcde9/build/player/c04-build-report.json`
  SHA `78A6DBF1BC32B53DAFB79C3C3CFCBA3E790330CA5E8103D529DF0C14DA30B33D`。
- 路线 A（同 6000.3 官方 core 包集合调整）：本机 URP/core 17.3.0 的 `package.json` 标注
  `"unity": "6000.3"`，没有可验证的官方 6000.3 替代版本；不能引用 17.4→6000.3 未验证组合。
- 路线 B（Editor 对齐）：本机仅有 6000.3.27f1，没有安装与目标 URP 配套的其它官方 Editor，
  无法实测；需要明确官方版本/安装包后再授权。

## 3. R1-04 验收状态

| 断言 | 结果 |
|---|---|
| A9-1 errors=0 | **FAIL**：errors=3/stepErrors=3 |
| A9-2 native0/无 FAIL | **FAIL**：C2 native1/exit3；C3 unknown/exit7；均无 seal |
| A9-3 版本官方配套 | 未改版本；路线 C 无有效导入修复 |
| A9-4 E3/E4 | **NOT_RUN**：无 clean package |
| A9-5 E5 | runner 76/76、compat 20/20；baseline diff-check0 |
| A9-6 范围 | 未改授权外 tracked；无 GAME 声明 |

## 4. 最小额外授权请求

卡点已定位到包导入层：`com.unity.render-pipelines.core` 17.3.0 的
`UnifiedRTShaderImporter` 对 `TraceVirtualOffset.urtshader` 未产生 ComputeShader/
RayTracingShader，只产生 ImportLog。请授权以下任一具体动作：

1. 允许对 `com.unity.render-pipelines.core` 的 `UnifiedRTShaderImporter`/包源做最小修复，
   使其正确生成 ComputeShader 与 RayTracingShader 子对象；或
2. 提供/安装一个官方 6000.3-compatible URP/core 包版本（含官方 CHANGELOG/发布记录），
   并允许更新 `manifest.json`/`packages-lock.json`；或
3. 提供/安装与目标 URP 官方配套的 Editor 版本，并允许更新 `ProjectVersion.txt`。

在这些授权前，不能用旧 errors6/3 产物、fallback shader、fixture 或摘要关闭 R1-04；C 仍未通过，
无 approve/MR/push/merge/deploy。

## 5. 保留项

- R1-03 A/B：真实 dotnet csproj 34/34 保留；provenance duplicate 明确拒绝。
- 01/02/strict99/FORMAT 及旧 subset 拒绝保持。
- 本轮未保留 C 脚本（诊断后已回退），未改 packages/settings/asmdef/许可。
