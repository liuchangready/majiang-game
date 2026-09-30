# Codex 独立审查与验证记录

日期：2026-09-29，15:33。状态：本轮架构范围验收通过，生产实现由 Antigravity 完成，Codex 独立测试并复核。

## 分工与依据

- Codex 编写 [设计契约](DESIGN.md)、[验收标准](ACCEPTANCE.md)、全部新增测试、迁移旧测试，并执行构建和审查。
- Antigravity 通过本机 Agent Mesh MCP Bridge 实现生产代码。初轮 A/B/C 并行，D 集成；审查后 B/C/D 再次按独占文件范围并行返修。详见 [任务记录](TASKS.md)。
- 未修改依赖版本。当前目录没有 Git 仓库，改动前快照保存在 `build/architecture-v2/pre-development-baseline.zip`。
- 不采用开发代理自述“通过”作为证据，下列结果均由 Codex 执行工具得到。

## 缺陷驱动过程

原项目 80 项测试通过，但新增的 11 项架构回归中 10 项失败，详见 [基线](BASELINE.md)。确认的问题包括重复摸牌丢牌却通过旧审计、非法阶段命令修改状态、荣和/自摸重复结算、本场付款未扣及供托未清。

模块交付后进一步发现并交回修复：

| 层次 | 审查发现 | 回归用例 |
|---|---|---|
| 物理牌 | 岭上上限、王牌补充、隐藏替补牌、精确阶段牌数 | TileMovementContractTest、ArchitectureSafetyRegressionTest |
| 结算 | 多种拆解、相同宝牌指示不能去重、16 种听牌座位组合 | WinningScoreContractTest、SettlementServiceContractTest |
| 响应 | 荣和先结算再收响应、碰被拒、抢杠使用旧弃牌 | EngineProtocolContractTest、GameplayLoopRegressionTest |
| 命令 | 空加杠先改阶段后异常、事件监听器递归提交 | EngineProtocolContractTest |
| 场次 | StartStacking 漏推进、终局剩余供托未清 | MatchIntegrationContractTest、MatchProgressionContractTest |
| 视图 | 命令返回值泄漏他家发牌、监听器污染分数数组 | LocalGameSessionContractTest、EventIsolationContractTest |
| 和牌上下文 | 两立直丢标记、岭上阶段已切换、一发未中断 | WinContextIntegrationContractTest |
| 连续运行 | Basic/Trivial 混合策略逐动作检查真实牌和资金 | MixedAiInvariantContractTest |
| 最后一轮 | 加杠取错实体牌、碰后伪自摸、伪造吃面子、鸣牌放弃荣和振听、宝牌翻数显示、四风连打时序 | EngineProtocolContractTest、MatchIntegrationContractTest、WinContextIntegrationContractTest |

## 验证入口

```powershell
.\gradlew.bat :core:test --rerun-tasks
.\gradlew.bat :lwjgl3:classes :android:assembleDebug
.\gradlew.bat -I tools/architecture/desktop-smoke.init.gradle :lwjgl3:architectureRenderSmoke
```

`domain-check.init.gradle` 仅为集成期间的部分模块验证，不代替完整测试。桌面渲染冒烟使用真实 LWJGL/OpenGL 隐藏窗口，提交会话合法动作并输出截图；无头 UI 测试使用 Mock GL，二者证据分开。

## 最终执行证据

| 检查 | 实际结果 | 证据文件 |
|---|---|---|
| 未过滤 `:core:test --rerun-tasks` | **185 项，0 失败、0 错误、0 跳过** | `build/architecture-v2/final-core-test.log`、`final-test-summary.json`、`core/build/reports/tests/test/index.html` |
| `:lwjgl3:classes` | **BUILD SUCCESSFUL** | `build/architecture-v2/final-platform-build.log` |
| `:android:assembleDebug` | **BUILD SUCCESSFUL** | 同上；`android/build/outputs/apk/debug/android-debug.apk` |
| 真实隐藏 LWJGL/OpenGL 冒烟 | **60 帧、15 次会话合法操作，最终 ROUND_END** | `build/architecture-v2/final-render-smoke.log` |
| 截图检查 | 开局牌桌与自摸结算页均成功绘制，金额/下一局按钮可见 | `build/architecture-v2/desktop-initial.png`、`desktop-after-play.png` |

混合 AI 集成覆盖固定种子 7、42、2026 各连续两局，每条命令独立枚举 136 张实体牌和总资金；另外保留原有全自动 Trivial AI 整局回归。所有新增测试由 Codex 编写，开发代理没有修改或禁用测试。

最终修改只在既有 Gradle 模块内完成，未新增网络框架或更换依赖版本。审查日志中的失败记录保留用于追溯，最终结果以上表的 `final-*` 文件为准。中途一次 Antigravity 网络 EOF 调用失败，重试后完成交付，没有改为由 Codex 代写生产实现。

## 明确限制

1. 本轮初验时保守禁用的立直暗杠，已由后续[立直流程补充设计](../riichi-flow/DESIGN.md)与[213 项完整验收](../riichi-flow/REVIEW.md)替代：开放本巡摸牌参与、听牌及所有拆法不变的暗杠。立直后的加杠继续禁止。
2. 完整千术交互、定向码牌、动画和联网不在本轮范围。未接入的 `UseCheatTechnique`、非空 `stackingPlans` 明确拒绝，不再假装执行成功。
3. 结算页沿用现有默认 BitmapFont，中文役名在真实截图中仍有缺字方框；本次渲染通过代表实际 GL 路径和对局可运行，不代表字体与视觉设计验收完成。
4. Android 构建有 Kotlin/R8 metadata 兼容警告；桌面运行有当前 JBR/LWJGL JNI/native-access 警告。构建与冒烟实际成功，本轮遵守既定依赖版本，未升级工具链。Android 真机视觉、触摸与安装运行尚未验证。
