# 落汗图形展示验收

日期：2026-10-12。开发：Anti；独立测试与审查：Codex。状态：V1、V1FIX1、V1FIX2 三轮交付后，最终独立验收通过。

## 交付范围

三名对手显示完整暗手牌槽，普通牌为不透明牌背，本人有效落汗牌为半透明牌面。提示按钮展开非模态侧栏，显示权威牌山剩余可摸总数、按本局东南西北排列的四方暗手牌，以及可滚动定位的公开牌河网格。未知归属不推测，标记失效即恢复牌背。

牌桌在提示展开时等比缩入左侧，输入做对应逆变换。标记牌保留赤五、实体身份及牌河位置；左右手牌牌面旋转。图形寿命区替换原文字位置栏。落汗费用、上限和跨局规则沿用既有 engine/API，不增加领域或协议改动。

## 独立证据

Codex 新增 50 项测试，包含纯展示模型、透明度与绘制状态、焦点随公开牌移动、动画隐藏和点击热区、模态输入优先级。最终强制全量执行 1050 项，失败、错误、跳过均为 0：engine 377、protocol 151、server 136、client 132、presentation 254。

Codex 独立运行以下命令，退出码全部为 0：

```powershell
.\gradlew.bat test --rerun-tasks :lwjgl3:classes :android:assembleDebug --console=plain
.\gradlew.bat :lwjgl3:tileMarkSmoke :lwjgl3:tileMarkHintSmoke :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-smoke.init.gradle --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --continue --console=plain
.\gradlew.bat :lwjgl3:tileMarkHintSmoke :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-hint-smoke.init.gradle --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle -PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled --continue --console=plain
```

本地证据在 `build/tile-mark-visual/final-tests-build.log`、`final-test-counts.json`、`final-gl.log`、`final-gl-scaled.log`。全量测试及双端构建 34 秒，86 个任务全部执行；GL 基准 19 秒、缩放 6 秒。三个工具均输出对应 `GL_PASS`；真实施术流程为 `frames=365 groups=3 fee=30 riverIndex=0`。最终验证期间生产源码哈希无变化，Anti 未修改已冻结的 Codex 测试。

真实桌面 GL 工具由 Codex 维护：

- `TileMarkRenderSmoke`：真实菜单进入游戏、连续三次各选四张、每次扣 10、总上限 12、双击确认不重复扣费、权威 deadline 不延长、标记牌弃入精确牌河槽位、退出菜单。
- `TileMarkHintSmoke`：提示开关、普通公开牌定位、滚轮与触摸拖动到北家末张、Esc 优先关闭、换庄门风、到期恢复牌背、合法剩余 0、缩放后的实际手牌点击只提交指定弃牌。
- `TileMarkVisibilitySmoke`：7 种三家手牌/牌河/副露/过期组合，分别截取提示关闭和展开状态；1280×720、960×540 两种视口。

验收过程曾退回普通牌背透明、焦点清除、绘制状态污染、滚动方向、触摸拖动、布局遮挡、旋转、动画门控、弹窗输入及滚动标题重叠问题，详见 [独立审查记录](REVIEW-VISUAL.md)。Anti 回调只是待审交付依据，最终结果以 Codex 独立运行和截图检视为准。

Codex 直接检视两种尺寸提示展开、滚动到底、副露与完整暗手牌的真实截图，确认固定标题、关闭按钮、副露与寿命区无遮挡；另检视真实游戏 `marked-river.png`，确认本人落汗牌及弃牌使用半透明牌面。截图保存在 `build/tile-mark/screenshots[-scaled]/` 和 `build/tile-mark-visual/screenshots[-scaled]/`，不作为产品资产提交。

## 验证边界

Android 调试 APK 构建通过；桌面模拟触摸拖动不能替代 Android 实机验收。本次不声称通过 Android 真机触控测试。用户原有 `lwjgl3/build.gradle`、`SnapshotLauncher.kt` 和 `snapshot/` 不纳入本次提交，前两项哈希与任务开始时一致。
