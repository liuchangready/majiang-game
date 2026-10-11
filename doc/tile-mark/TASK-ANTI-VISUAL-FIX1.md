# 落汗图形展示 V1FIX1 独立验收退回

V1 的244项presentation测试已通过，V-01/V-03/V-04修正有效。Codex独立真实GL发现以下问题，必须修正后交付。不要重复阅读整个仓库或历史任务，直接根据截图和定位修改。仍由Anti只改生产、Codex只改测试；禁止改src/test、tools/architecture、Gradle、用户SnapshotLauncher/snapshot；不要git提交或启动其他代理。

## 实证与必修问题

1. **滚动无法到达北家末张**。命令 `:lwjgl3:tileMarkHintSmoke` 已失败，日志 `build/tile-mark-visual/review-V1-gl.log` 报 `Scrolling down must reach North river last tile`。RiverHintPanel 的 `scrollOffsetY - amountY*24` 为负，render又直接加Y，使下部牌更远离视口。改为正确向上移动内容，按真实内容高度计算最大偏移，不要固定400。只有河网格滚动，手牌和总数固定。
2. **没有触摸拖动**。仅 scrolled 无法在Android查看下方牌河。实现 touchDown/touchDragged/touchUp 的网格拖动，超过阈值取消点击，限制在网格，不能拖动手牌区或穿透主牌桌；关闭/换局清拖动状态。滚轮也遵守区域/窗口输入优先级。
3. **侧栏遮挡**。实际查看 `build/tile-mark/screenshots/visibility-4-hint.png`：上方副露只剩一部分，其余被侧栏盖住；下家副露和信息也被盖住。`build/tile-mark-visual/screenshots/hint-open.png`：右侧牌河与暗手牌挤在一起。现在仅将本人手牌和几个按钮平移并不满足设计。使用统一牌桌布局/坐标变换，让整个牌桌（手牌、牌河、副露、座位、倒计时、飞牌、操作）在侧栏左边区域内，渲染与命中坐标一致；不要继续用零散魔法数字修补。可把原1280×720牌桌等比缩放到约840宽左区并垂直居中，侧栏保持原UI相机绘制，所有主牌桌点击做对应逆变换。保留面板打开时正常选牌、弃牌、响应按钮可用。具体实现自行选择，但必须真的无覆盖并实际检查截图。
4. **关闭按钮被面板背景盖住**。renderToggleButton先画，render面板后画将按钮盖掉；截图没有关闭按钮，但热区还在。按钮必须最后绘制或在面板内留独立栏位，不与剩余总数/手牌标题覆盖。打开后鼠标和触摸都能看见并再次点击关闭，Esc继续有效。
5. **寿命面板覆盖上家完整手牌**。`visibility-0.png` 中旧trackingPanelRect仍320×175，盖住左侧牌背行上端。改成紧凑图形区（例如宽不超过190并留好上下位置），4牌+寿命点+组切换均须可见；面板不再空出大块旧文字区。两种hint状态都不能遮住对手牌。
6. **侧方落汗牌过窄且未旋转**。左右牌行把竖向纹理塞进34×24横矩形，letterbox后牌面只有很窄一条（visibility-0右侧12张）。按设计真正旋转牌面到座位朝向，或提供经Codex视觉检视确认的清晰完整牌面布局，不能靠挤压矩形代替旋转。赤五保留。
7. **动画网格一致性**。RiverHintPanel直接画当前全部河牌，没有主桌飞牌/queued suppression。和主桌共用实体可见性门控，飞牌阶段网格目标牌也不要提前出现。只隐藏当前动画实体，不影响权威wallRemaining。
8. **弹窗优先级**。目前touchDown最前先处理hint toggle，选牌/结算上仍可点底层开关。模态期间按钮应禁用/隐藏并阻止底层点击，渲染和输入使用相同门控；正常非模态打开提示后倒计时不停。删去提示面板的长解释句，保留按钮名、方位、数字、牌图形即可。

## 验证与回调

Codex已更新真实GL工具。生产完成后运行以下全部命令并等结束核对退出码：

```
.\gradlew.bat :presentation:test --console=plain
.\gradlew.bat :lwjgl3:tileMarkHintSmoke :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --continue --console=plain
.\gradlew.bat :lwjgl3:tileMarkHintSmoke :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --continue --console=plain
```

GL PASS 只证明断言通过，还需实际查看两种尺寸 hint-open、hint-scrolled-bottom、visibility-0/4/5及对应-hint截图，确认全部手牌/副露/关闭按钮完整可见。不许改测试来迁就生产。若输入已按区域门控，测试需要调整实际鼠标位置或新增拖动用例，由Codex改测试，报告即可。

更新 PROGRESS-ANTI-VISUAL.md；完成写 DELIVERY-ANTI-VISUAL-FIX1.md 与 CALLBACK-ANTI-VISUAL-FIX1.json（taskId V1FIX1 / recipient codex / status ready_for_review或blocked，命令及退出码/截图结论/剩余问题）。终端输出 CODEX_CALLBACK V1FIX1 ready_for_review 并退出。全量最终强制回归与双端构建由Codex执行。
