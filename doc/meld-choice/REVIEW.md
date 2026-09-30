# 吃碰杠牌组选择验收

2026-09-29。Codex 设计、编写测试并检视；Antigravity 通过本地 MCP bridge 并行开发 M（方案适配与选择状态）和 U（界面与输入）。设计见 [DESIGN.md](DESIGN.md)。

## 行为

原先 CHI/PON/KAN 按钮直接提交第一个合法动作。现在先进入完整牌组预览，选择后确认。吃/碰显示3张，三类杠显示4张；区分外来牌、原碰组与补入牌，高亮参与的精确手牌。单一方案预选但不立即执行，多方案必须先选。取消/返回只关闭预览；PASS 才放弃，WIN 仍保留合法荣和/自摸。立直后的合法暗杠同样经过确认。

方案只来自本人 PlayerView 的合法动作，表现层不增加新的麻将规则。选择期间不改权威手牌、分数或振听。加杠只消耗补入的一张，不重复追加原碰组。

## 独立验收范围

- MeldChoiceProviderContractTest：3种吃法、碰/明杠目标牌、暗杠与加杠区分、物理身份、来源座位、无目标/非本人动作排除、不可变快照与嵌套动作列表、去重、不完整方案拒绝。
- MeldSelectionModelContractTest：多方案确认门槛、单方案预选但模式外不能确认、相等快照/过期快照、分页和跨页选择拦截。
- MeldChoiceScreenContractTest：真实屏幕→会话→引擎分别确认3种吃法、取消/ESC/Android BACK不放过荣和、确认碰才触发过水、三类杠分别落实且只摸1张岭上牌、悬停/理牌/手牌点击不误提交、显式PASS、拒绝/静默过期状态、自摸快捷按钮。
- RiichiScreenIntegrationTest：适配新确认步骤，继续验证立直暗杠后岭上自摸与实际结算。
- 渲染工具 MeldChoiceRenderSmoke 使用真实 OpenGL、牌面素材和 GameScreen 输入；可控会话快照只用于布局与交互验证，权威规则由真实会话测试验证。

## 最终结果

**273 项测试通过，0 失败、0 错误、0 跳过**；新增18项，原有2项屏幕用例适配新确认流程。桌面 `classes` 与 Android `assembleDebug` 通过，APK 位于 `android/build/outputs/apk/debug/android-debug.apk`。

首次全量回归发现旧 GameScreenSessionContractTest 仍要求点击即执行，且提供缺少真实归属/外来目标的虚拟牌组。Codex 将其换为引擎合法动作生成的真实视图，继续断言非零观察者、精确动作和仅确认后提交；未修改生产实现去迁就旧交互。RiichiScreenIntegrationTest 也增加确认步骤，保留原先岭上自摸结算断言。

实际 OpenGL 与输入验收：`MELD_CHOICE_RENDER_PASS frames=39 confirmations=3 screenshots=7`。逐张检视通过：

- [三种吃法](../../build/meld-choice/three-chi-choices.png)：完整显示345/456/567，外来5万标记明确，未选择时确认禁用。
- [悬停与选择独立](../../build/meld-choice/chi-hover-keeps-selection.png)：悬停345高亮3/4万，已选567仍有SELECTED标记，最终确认提交567。
- [碰与和牌选择](../../build/meld-choice/pon-called-and-win.png)：2张本人手牌与1张外来牌，保留RON/PASS/取消。
- [明杠](../../build/meld-choice/open-kan-four-tiles.png)：显示4张牌，仅高亮3张本人手牌。
- [暗杠与加杠并列](../../build/meld-choice/closed-and-added-kan.png)：两类杠分别展示，无默认多方案提交。
- [加杠补牌](../../build/meld-choice/added-kan-one-owned-tile.png)：原碰和补入牌分色，只高亮补入的1张手牌。
- [分页](../../build/meld-choice/choice-page-two.png)：第二页完整可点，页码和翻页状态正确。分页快照刻意注入4种精确物理副本方案用于容量验证，不表示当前规则引擎必定为同牌面枚举4种吃法。

此外重新执行先前的立直/振听渲染验收，`FURITEN_RENDER_PASS frames=36 confirmations=1 tsumo=1 screenshots=5`，确认选牌、13面待牌与振听自摸按钮没有回归。日志：`build/meld-choice/riichi-furiten-render-regression.log`。

最终完整日志：`build/meld-choice/final-validation.log`，包含 `BUILD SUCCESSFUL`。既有 JBR/LWJGL 原生访问和工具链弃用警告未阻断编译、测试或渲染。当前没有发现阻断交付的问题；未执行 Android 真机交互测试。

```powershell
$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat -I tools/architecture/meld-choice-smoke.init.gradle test :lwjgl3:classes :android:assembleDebug :lwjgl3:meldChoiceSmoke --console=plain
```

日志和截图：`build/meld-choice/`；测试XML：`core/build/test-results/test/`。Android 构建验证不等同真机交互测试。
