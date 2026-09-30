# 出牌演出期间输入限制：验收记录

日期：2026-09-30。设计、验收测试及代码检视由 Codex 完成；生产代码由 Antigravity 完成。

## 原因与修复

本地会话同步处理 AI 操作后，可能已经发布玩家下一次可以出牌的状态，而界面仍在播放上一轮出牌。原有输入判断只检查逻辑状态，且牌河差异在下一帧才转为动画队列，导致动画期间和两次快速点击之间都存在提前操作的入口。

GameScreen 现在在接收会话更新时同步维护出牌动画队列。当前动画或队列非空时，拦截手牌、理牌和牌局操作，并清除悬停效果；队列全部播放完毕且玩家确实拥有合法出牌动作后，才恢复出牌输入。等待期间的点击直接丢弃，不会在解除限制后补交。

检查了鸣牌和立直选牌确认入口、被吃碰杠取走的待播牌，以及新局、结算等边界。被取走的牌会从待播项中清理，新局及结算会清空演出状态，避免残留锁定。规则层合法动作保持不变，不引入自动过牌或固定时间延迟。

## 自动验收

- 修复前运行首批 8 项回归用例：7 项失败、1 项通过，确认能够复现问题。
- 最终新增 10 项验收测试，覆盖同步更新后连续点击、整段动画队列、手牌与理牌拦截、和牌及过牌恢复、合法动作校验、鸣牌确认、立直确认、即时清除悬停，以及被鸣牌取走和新局结算边界。
- `gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain` 成功；共 374 项测试，0 失败、0 错误、0 跳过。
- 真实 LWJGL/OpenGL 图形验证成功：`DISCARD_INPUT_GATE_RENDER_PASS screenshots=4 discardsWaited=4 submissions=2`。四家出牌动画结束前持续点击不会提交操作；结束后新点击正常出牌，并立即进入下一段等待状态。
- 已检查等待中与播放完成后的截图。Android 已完成调试 APK 构建，未进行真机操作测试。

## 证据位置

- 设计：`doc/discard-input-gate/DESIGN.md`
- 开发交付：`doc/discard-input-gate/delivery-D.md`
- 验收用例：`core/src/test/kotlin/com/majiang/screen/DiscardInputGateAcceptanceTest.kt`
- 图形验证：`tools/architecture/DiscardInputGateRenderSmoke.java`
- 修复前结果：`build/discard-input-gate/before.log`
- 全量验证：`build/discard-input-gate/validation.log`
- 图形验证日志及截图：`build/discard-input-gate/`

结论：本次提前操作问题已修复，验收通过。
