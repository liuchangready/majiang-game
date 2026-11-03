# G2-C04-D04-C 完整构建、包与进程工具返修交付（R1-06 Round 9）

日期：2026-11-03，Asia/Shanghai。任务：D04-C，targetAgent=deepseek_harness。
工作树：`E:/majiang-review/g2-c04-d04-runner-ds-20261103`；当前代码 HEAD
`2283adaf05b4f19fbe13aad69c98fc17cf2f9803`。R7 复核 R1-03 A/B 已修并通过
（policy-probes 34/34、identity 改变、duplicate 明确报错；tests 76/76、compat 20/20）。

本轮按用户裁决处理 R1-06-R1-04：在授权包层/Editor 范围尝试 **路线 C（项目侧导入器修复）**。
结果：**未取得 errors0，按停止规则回报，不声称通过**。

## 1. 路线选择与官方依据

- **路线 C**：仅新增项目侧 Editor 修复脚本，不改包版本/Editor 版本；使用 Unity 官方
  `SerializedObject` / `Shader.Find` API，在 `C04PlayerCheatBuild.Build` 的 BuildPlayer 前
  修复 URP global settings 中的 null Shader 引用，目标是不再触发
  `RenderPipelineResourcesEditorUtils.TryReloadContainedNullFields` 的 host-type 错误。
- 排除 A：6000.3.27f1 的官方内置包集合就是 URP/core 17.3.0（包内 `package.json` 标注
  `"unity": "6000.3"`），没有可验证的 6000.3 官方替代版本；不能引用 17.4→6000.3 未验证组合。
- 排除 B：本机只有 6000.3.27f1，未安装与目标 URP 配套的其它官方 Editor，无法在本轮验证升级。
- 官方依据（API/发布）：
  - https://unity.com/releases/editor/whats-new/6000.3.27f1
  - https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SerializedObject.html
  - https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Shader.Find.html
  - 本机 URP `CHANGELOG.md`/`package.json`（17.3.0, unity 6000.3）。

## 2. 实际尝试与结果

新增（仅作为被回退的验证尝试，最终候选未保留）：
`Assets/Editor/C04ShaderHostTypeFix.cs` + `.meta`，在 `C04PlayerCheatBuild.Build` 中调用
`C04ShaderHostTypeFix.Apply()`；脚本加载 URP global settings，遍历 SerializedObject，
把 null Shader 引用替换为 `Hidden/InternalErrorShader`，`ApplyModifiedPropertiesWithoutUndo`。

失败证据：
- `build/r9-routeC2-b29144f9136943089bdba6c0452bcde9/`
  - BuildReport：`result=Succeeded, errors=3, stepErrorCount=3, stepExceptionCount=0`；
  - 仍为 `Host type is not matching any asset type at Path
    Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/VirtualOffset/TraceVirtualOffset.urtshader.`
    （Autodesk 错误被消除，但 TraceVirtualOffset 仍存在）；
  - 构建期间 `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset` 被 Unity 保存/变更，
    触发 staging transition `STAGING_CHANGED_DURING_BUILD`；
  - native exit=1；无 package seal。
- `build/r9-routeC3-d2e9d3619dc743c1a6161ae9a8774c49/`：再次尝试 hidden SerializedProperty
  遍历，BuildReport 仍 errors=3；job cleanup `remaining=1`，native result unknown；无 seal。
- 最终已回退该 Editor 脚本与调用；未改 manifest/packages-lock/ProjectVersion（E2 无 diff）。

## 3. 验收对照

| 断言 | 结果 |
|---|---|
| A9-1 errors=0 | **FAIL**：errors=3/stepErrors=3 |
| A9-2 native0/无 FAIL | **FAIL**：native 1，summary FAIL，无 seal |
| A9-3 版本官方配套 | 未改版本；路线 C API 依据已列 |
| A9-4 E3/E4 六行/绑定 | **NOT_RUN**：无 clean package |
| A9-5 E5 回归 | runner 76/76、compat 20/20；baseline diff-check 0 |
| A9-6 范围 | 未改授权外 tracked；无 GAME 声明 |

## 4. 卡点与所需额外授权

卡点：路线 C 只能消除 Autodesk 错误，**TraceVirtualOffset.urtshader** 仍由 URP core
`ProbeVolumeBakingResources` 的 null `traceVirtualOffsetCS/RT` 触发；在内存中替换 null
Shader 不能阻止 Unity 在 BuildPlayer 期间保存/重新序列化 global settings，且该 asset 变更
会触发 staging transition。

需要的具体额外授权（任一）：
1. 允许编辑 tracked
   `unity/Mahjong.Unity/Assets/Settings/UniversalRenderPipelineGlobalSettings.asset`
   （清除/修复 `ProbeVolumeBakingResources.traceVirtualOffsetCS/RT` 等 null 资源字段），
   并在 runner transition 中明确允许该确定性转换；
2. 或允许修改 URP/core 包源 / 升级到有官方 6000.3 配套记录的包+Editor 组合；
3. 或允许在 staging preparation 阶段（freeze 前）运行项目侧修复并保存，不把它算作
   build 期间输入变更。

未获这些授权前，不能用旧 errors6/3 产物、fixture 或 summary 刷新来关闭 R1-04；C 仍未通过，
无 approve/MR/push。

## 5. 保留的 R1-03 修复

- Unity IDE 排除仅限 `unity/Mahjong.Unity/*`；真实 dotnet csproj 保留。
- provenance duplicate 明确拒绝。
- tests 76/76、compat 20/20；policy/success/subset 证据沿用 R8。
