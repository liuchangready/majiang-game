# 振听功能检视与验收

日期：2026-09-29。Codex 负责设计、测试与独立检视；Antigravity 通过本地 MCP bridge 完成生产实现。R（规则）与 V（视图/UI）并行交付，F 处理检视修正。

## 原有状态与本次补齐

原引擎已有舍牌振听、临时振听、立直振听的荣和拦截；自摸入口不受振听限制。原缺口是没有面向玩家的状态提示，且无役完成形因为没有 Ron 动作而漏记临时振听。

本次统一 FuritenRules，为本人只读视图加入振听原因和完整舍牌历史；响应窗口补记无役完成形的隐式过牌；HUD 显示当前振听及原因，立直选牌面板显示弃牌后是否振听。任一原因禁止所有待牌的荣和和抢杠，合法自摸仍须玩家确认；不阻止振听立直。

临时振听按本项目既有约定直到本人下次实际摸牌（含岭上）清除，吃碰不直接清除。立直振听持续本局，舍牌振听随当前待牌重算。规则来源和与 WRC 的变体差异见 [设计文档](DESIGN.md)。

## 独立测试

- FuritenRulesContractTest：完整待牌集、改听解除、三原因并存、暗杠占满四张排除第五张、历史回退、无足够副露的不完整牌形拒绝。
- FuritenFlowContractTest：三原因分别拦截全部待牌的 Ron 且拒绝零副作用、三原因保留自摸且只结算一次、临时/立直标记期限、普通/岭上摸牌、立直自动摸切遇到自摸暂停、新局重置、无役完成形不弹空 Pass、AI 同源、被叫走历史牌与抢杠。
- FuritenViewContractTest：非零座位的快照隔离/不可变性、不同弃牌方案振听不同、拟弃牌必须纳入自己的历史。
- FuritenScreenContractTest：真实 GameScreen → LocalGameSession → 引擎点击自摸并结算；悬停方案警示独立于当前 HUD 和最终确认的弃牌。
- 既有回归继续覆盖主动放过荣和/抢杠、吃碰代替荣和、立直选牌、自动摸切与严格暗杠等。

## 检视修正

1. 底层分解器允许按副露数量处理少于 13 张暗牌；新 FuritenRules 初版没有验证实际副露数量。新增独立回归已复现单张碎片被当成待牌的问题，交由 F 加入 13 张等效牌数校验。
2. 初版预览警示在单一原因时接近面板边缘，多原因可溢出；“该弃牌造成振听”对继承临时振听的情况也不够准确。F 改为弃牌后状态短标题及有界原因区，并测量 HUD 文本。

## 最终验证

最终结果：**255 项测试通过，0 失败、0 错误、0 跳过**，较此前基线新增 21 项。桌面 classes 与 Android debug APK 构建通过；旧立直选牌 Java 渲染验收脚本也已适配新的 PlayerView 参数并重新编译通过。新增边界回归先复现失败，F 修复后与全量测试一起通过。

实际 OpenGL 验收：`FURITEN_RENDER_PASS frames=36 confirmations=1 tsumo=1 screenshots=5`。Codex 逐张检视以下截图，HUD 与预览警示均未遮挡待牌或操作按钮，三原因文字保持在边界内：

- [弃 2 索改听 3/6 索，不振听](../../build/furiten/discard-2s-wait-3s-6s.png)：当前 HUD 仍显示原手牌振听，预览方案不显示警告。
- [弃 6 索听 2/5 索，舍牌振听](../../build/furiten/discard-6s-wait-2s-5s.png)：显示弃牌后振听提示，保留确认立直。
- [已选 6 索，悬停 2 索](../../build/furiten/hover-2s-selected-6s.png)：预览随悬停变化，实际确认仍提交已选 6 索。
- [13 面待牌与三原因并存](../../build/furiten/orphans-thirteen-waits.png)：所有牌面、标题与原因完整显示。该快照刻意组合标记用于最大文本布局测试，不表示正常立直流程会出现这一选牌状态。
- [振听自摸按钮](../../build/furiten/furiten-tsumo-choice.png)：振听提示仍在，自摸按钮可点击，无荣和按钮。

Android 产物：`android/build/outputs/apk/debug/android-debug.apk`（最终构建时间 20:18）。完整日志：`build/furiten/final-validation.log`；XML 测试结果：`core/build/test-results/test/`。日志有既有 JBR/LWJGL 原生访问和 Gradle/Android 工具链警告，但最终 Gradle 返回 `BUILD SUCCESSFUL`，渲染进程正常退出。

渲染验收使用真实 OpenGL 和 GameScreen 输入、可控只读会话快照。它验证布局、悬停、确认、取消和自摸按钮；权威规则与结算由上述真实会话测试验证，渲染快照不作为规则正确性的证据。

最终命令：

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat -I tools/architecture/furiten-smoke.init.gradle -I tools/architecture/riichi-choice-smoke.init.gradle test :lwjgl3:classes :android:assembleDebug :lwjgl3:compileRiichiChoiceSmoke :lwjgl3:furitenSmoke --console=plain
```

日志位于 `build/furiten/`，交付记录见 [R](delivery-R.md)、[V](delivery-V.md)、[F](delivery-F.md)。桌面实际渲染不等同于 Android 真机测试。
