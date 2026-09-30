# 已完成副露展示验收

日期：2026-09-29。Codex 负责设计、独立测试和画面检视；Antigravity 通过本地 MCP bridge 完成生产代码。设计见 [DESIGN.md](DESIGN.md)。

原问题是领域层已正确保存吃碰杠牌组，但 GameScreen 没有绘制本人的 melds。本次把已完成副露固定在本人手牌右侧，与可出牌的手牌区域分开；副露组不能点作出牌。

吃/碰显示三张，杠显示四张。明副露标明来源和被鸣牌；暗杠两侧牌背，中间正面；加杠仅在同一组展示四张，蓝色标记补入牌。布局由当前 PlayerView 驱动，理牌不移动副露，换局清除旧组。

## 独立验证

MeldDisplayContractTest 覆盖每类副露精确物理牌、暗杠牌背、加杠补牌、非零观察者、只读快照、一至四杠最大布局与真实手牌槽不重叠、渲染调用牌面/牌背皮肤、点击副露不出牌、视图更新清空，以及真实屏幕确认吃碰杠后立即展示。

真实 OpenGL 工具 MeldDisplayRenderSmoke 检视各类型、四副露、四杠、理牌、经典/维基两种皮肤与新局清空。渲染工具使用可控视图，不替代真实会话的规则测试。

## 最终结果

**281 项测试通过，0 失败、0 错误、0 跳过**，本次新增8项。桌面 classes 与 Android debug APK 构建成功。Antigravity 交付见 [delivery-D.md](delivery-D.md)，Codex 独立检视未发现阻断问题。

实际 OpenGL 验收：`MELD_DISPLAY_RENDER_PASS frames=30 screenshots=10 accidentalDiscards=0`。10张截图已逐张检视，两种皮肤均显示完整牌组，最大四杠状态与暗牌/摸牌/理牌按钮没有重叠。

- 单组完成：[吃](../../build/meld-display/completed-chi.png)、[碰](../../build/meld-display/completed-pon.png)、[明杠](../../build/meld-display/completed-open-kan.png)、[暗杠](../../build/meld-display/completed-closed-kan.png)、[加杠](../../build/meld-display/completed-added-kan.png)。
- 多组：[四副露](../../build/meld-display/four-melds.png)、[四杠](../../build/meld-display/four-kans.png)、[理牌后四杠](../../build/meld-display/four-kans-sorted.png)、[经典皮肤四副露](../../build/meld-display/classic-four-melds.png)。
- [新局无副露](../../build/meld-display/new-round-no-melds.png)：快照清空后不残留旧牌组。

渲染工具注入的完成形快照主要用于布局容量测试；真实吃碰杠流程由独立测试验证，不能把截图视作完整随机对局。换肤使用内存偏好，不更改用户的持久化皮肤设置。

最终日志：`build/meld-display/final-validation.log`（BUILD SUCCESSFUL）。APK：`android/build/outputs/apk/debug/android-debug.apk`。既有 JBR/LWJGL 原生访问和构建工具弃用提示未阻断本次运行。

```powershell
$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat -I tools/architecture/meld-display-smoke.init.gradle test :lwjgl3:classes :android:assembleDebug :lwjgl3:meldDisplaySmoke --console=plain
```

日志/截图：`build/meld-display/`；测试XML：`core/build/test-results/test/`。Android构建不等同于真机验证。
