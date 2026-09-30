# 立直选牌与待牌预览验收

设计、测试与审查：Codex。生产开发：Antigravity（经本地 Agent Mesh MCP dispatcher 委派 P/U/L）。

- P：预览提供器与纯选牌状态已交付，9 项独立验收通过。
- L：真实物理副本可选与第五张待牌过滤已交付，2 项独立验收通过。
- U：界面已交付，首轮完整 233 项测试全部通过。首次桥接返回空响应并包含 `denied_actions: CallMcpTool`，不能视为完成；按只用原生文件工具要求重试后完成，未修改权限。最初进度将拒绝原因笼统描述为终端，实际日志为 MCP 工具拒绝。
- 界面旧行为的红灯基线保留在 `build/riichi-choice/ui-red.log`：一键直接立直不满足本轮显式选牌确认契约。

既有 `GameScreenSessionContractTest` 中的一键立直用例由新的选牌/取消/确认用例替代，其余既有测试保留。测试与渲染验收工具均由 Codex 编写，开发代理不得修改。

实际 OpenGL 首轮完成 30 帧、一次精确确认、三张截图，验证切换弃牌和十三面待牌；人工检视发现摸牌与选中标签重叠，以及悬停标题可能跨过分隔线，F 已修正。第二轮四张截图与实际悬停验收通过，临时悬停二索预览三/六索时，确认仍准确提交此前选中的六索。

Android BACK 生命周期测试先暴露未恢复设置；F 增加恢复后，扩展测试又暴露 hide 与 dispose 重复恢复覆盖其他屏幕设置、重复 show 覆盖原设置的问题，已交给 Q 添加一次捕获/一次释放标记。这是独立 UI 生命周期缺陷，不影响已经通过的待牌算法与宣言逻辑。

## 最终结果（2026-09-29）

**通过。** P/U/L/F/Q 生产开发均由 Antigravity 完成；Codex 完成设计、独立测试、真实渲染验收与审查返修。

| 验证 | 最终结果 |
| --- | --- |
| 全量 `gradlew test` | 234 项，0 失败，0 错误，0 跳过（既有213项 + 新增21项） |
| 预览与选择模型 | 9 项，覆盖不同弃牌不同待牌、七对、国士十三面、已有暗杠、快照与不可变性 |
| 物理副本选择规则 | 2 项，含选择摸到的同种副本并准确提交 |
| 真实界面输入与领域联动 | 10 项，覆盖选择/改选/取消/悬停/排序/拒绝/过期状态/返回键生命周期/确认才扣点 |
| 实际 LWJGL/OpenGL | 30 帧、一次精确确认、四张截图；候选变化、悬停不改选择、十三面及取消通过 |
| 桌面编译与 Android 调试 APK | 均成功；APK 为 `android/build/outputs/apk/debug/android-debug.apk` |

统一最终命令：

```text
gradlew.bat -I tools/architecture/riichi-choice-smoke.init.gradle test :lwjgl3:classes :android:assembleDebug :lwjgl3:riichiChoiceSmoke --console=plain
```

证据：`build/riichi-choice/final-validation.log` 的 `BUILD SUCCESSFUL` 与 `RIICHI_CHOICE_RENDER_PASS`；`core/build/test-results/test/TEST-*.xml`。PowerShell 对 native stderr 警告可能显示非零外层状态，结论以实际 Gradle 构建结果、测试 XML 与运行断言为准。

已检查的实际截图：

- [弃六索听二/五索](../../build/riichi-choice/discard-6s-wait-2s-5s.png)
- [弃二索听三/六索](../../build/riichi-choice/discard-2s-wait-3s-6s.png)
- [已选六索，悬停二索临时预览](../../build/riichi-choice/hover-2s-selected-6s.png)
- [国士十三面待牌](../../build/riichi-choice/orphans-thirteen-waits.png)

生产改动仅四个文件：`session/RiichiPreviewProvider.kt`、`screen/RiichiSelectionModel.kt`、`screen/GameScreen.kt`、`rules/RiichiRuleEngine.kt`。既有立直后自动摸切、自摸暂停、严格暗杠与物理守恒测试继续通过。

限制：Android 本轮完成构建与无头触摸/返回键契约测试，未在真机操作。预览只展示理论待牌牌面，不估计未知牌山剩余张数。已有 JBR/LWJGL 原生访问警告仍存在，实际桌面验收无崩溃。游戏进程没有热更新能力。
