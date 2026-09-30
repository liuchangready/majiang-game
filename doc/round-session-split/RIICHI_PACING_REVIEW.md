# 立直连续出牌修复验收

日期：2026-09-30。生产开发：Antigravity（MCP Bridge F4）；设计、测试、检视：Codex。

## 根因与修复

1. 原自动循环会在一次玩家命令中执行多巡立直摸切与 AI 操作，然后只推送最终视图。新增测试确认旧同步模式中一次提交确实包含本人多次弃牌。
2. 原界面按最终牌河的座位顺序补动画，真实 `2,3,0,1,2,3,0,1` 被排成 `0,0,1,1,2,2,3,3`。修改前的定向测试已复现两个时序失败。
3. 默认游戏入口现在通过独立 LocalTableRuntime 驱动延迟模式 Coordinator。每次心跳最多执行一个自动动作；动画播放期间不推进，空闲至少 0.15 秒再调度，长帧不补执行多步。AI 响应也是逐动作提交。
4. GameScreen 按可见弃牌事件顺序排动画，用座位与物理牌身份去重；纯快照仍可兜底。重复事件不重播，副露拿走的牌不复活。
5. 自摸、合法立直暗杠、真人荣和/过牌等选择继续暂停；客户端端点不获得推进权威状态的接口。无头兼容入口仍保留原同步模拟模式。

## 验证结果

- 新增 11 项测试：7 项调度/规则/真实端点与界面联动测试，4 项事件顺序/去重/副露调和测试。
- 两轮真实端点与 GameScreen 动画联动顺序为 `0,1,2,3,0,1,2,3`；每次只有一张待播弃牌，播放期间权威版本不变。
- 全量 **442 tests，0 failures，0 errors，0 skipped**，原 431 项均保留通过。
- `:lwjgl3:classes`、`:android:assembleDebug` 通过。
- 默认本地宿主实际 GL 冒烟通过，并确认玩家弃牌后 AI 的调度发生：`ROUND_SESSION_CLIENT_RENDER_PASS`。
- 原动画输入锁 GL 冒烟通过：`DISCARD_INPUT_GATE_RENDER_PASS screenshots=4 discardsWaited=4 submissions=2`。
- 检视默认入口、所有顶层自动循环调用点、单步逻辑复用、宿主生命周期、事件去重和响应保留。Round/Session 规则职责未扩张。

## 证据

- 修改前失败：[日志](../../build/round-session-split/riichi-order-before.log)、[XML](../../build/round-session-split/riichi-order-before.xml)。
- 修复后：[定向测试](../../build/round-session-split/riichi-targeted.log)、[全量测试与构建](../../build/round-session-split/riichi-validation.log)、[桌面冒烟](../../build/round-session-split/riichi-render.log)。
- [默认客户端完成一轮弃牌截图](../../build/round-session-split/default-client-after-discard.png)。
- [设计契约](RIICHI_PACING_FIX.md)、[Anti 交付](delivery-F4.md)。

已运行的旧 JVM 不会热更新，重启游戏后使用修复版本。
