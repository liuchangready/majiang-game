# 🀄 千术麻将 (Mahjong Cheater) - 架构与开发指南 (AGENTS.md)

本文档汇总了本项目当前的所有模块架构、设计模式、双端运行规范与开发约定，供后续参与开发的 AI Agent 与开发者参考。

---

## 1. 游戏定位与核心玩法特色

- **游戏类型**：2D 策略心理战麻将、千术绝技、视线博弈与防抓包出千对局。
- **出千与怀疑度机制**：
  - **四大绝技**：袖里乾坤（藏牌互换）、移花接木（牌山偷牌）、神仙透视（窥探对手牌与底牌）、声东击西（制造响动吸引全场视线）。
  - **警戒与怀疑度**：AI 拥有独立视线状态与 0~100% 怀疑度。出千需选准时机，若在直视时强行出千将触发捉奸在床（Busted）负面惩罚。

---

## 2. 跨平台架构与技术规范

- **游戏引擎**：libGDX 1.14.2
- **编程语言**：Kotlin 2.4.10 (JVM 17)
- **桌面后端**：LWJGL 3 (1280x720 视口)
- **移动后端**：Android 沉浸式全屏横屏 (minSdk 21, targetSdk 36)
- **多场景状态机**：
  - `MenuScreen`：主菜单，集成出千绝技秘籍弹窗、设置与快速启动。
  - `GameScreen`：牌桌场景骨架。

---

## 3. 核心算法设计文档

- 详见设计规范文档：
  - **整体架构与物理守恒**：[`doc/FRAMEWORK_ARCHITECTURE.md`](file:///D:/majiang/doc/FRAMEWORK_ARCHITECTURE.md)
  - **日麻规则、发牌与算分全表（参考雀魂）**：[`doc/RIICHI_RULES_DESIGN.md`](file:///D:/majiang/doc/RIICHI_RULES_DESIGN.md)
  - **开局码牌算法**：[`STACKING_ALGORITHM.md`](file:///D:/majiang/STACKING_ALGORITHM.md)
    - **物理守恒律**：单副牌 136 张严格物理闭环；
    - **码牌两大维度**：起手发牌槽位（0~52）与后续摸牌槽位（53~135）；
    - **牌型定向影响**：34 维向量余弦相似度计算两两冲突，异花色互不干扰，同花色严重衰减；
    - **碰杠破千法则**：开局后牌山绝对冻结，顺位因吃碰跳跃导致好牌被对手摸走。

---

## 4. 运行与验证指令

```bash
# 启动桌面版
./gradlew :lwjgl3:run

# 运行无头测试
./gradlew test --info

# 构建 Android 调试 APK
./gradlew :android:assembleDebug
```

## 5. 单局、整场与客户端的职责边界

- `round/RoundStateMachine` 只保存与应用一局的状态变更，和牌或流局后保持终态。下一次发牌（包含连庄）创建新实例与新的 `roundId`。
- `match/MatchSession` 串联整场各局，管理局间账本、进度、历史和整场结果；不计算麻将规则，不执行请求验证。
- `round/RoundRuleService`、`RoundCommandProcessor`、`RoundTransitionPlanner` 在状态机之外完成单局合法性检查、规则计算和候选变更准备；复用既有规则、算分与响应裁决组件。
- `application/ApplicationCoordinator` 组织独立验证器、控制与请求注册表、候选审计、提交和更新发布。AI 与自动操作在领域对象之外调度。
- 默认单机游戏由 `application/LocalTableRuntime` 与 `MahjongGame.render` 驱动延迟自动操作，每次最多一个动作，等待弃牌动画结束再继续；客户端不直接推进权威状态。立直时序修复与验收见 [记录](doc/round-session-split/RIICHI_PACING_REVIEW.md)。
- `client/PlayerEndpoint` 是客户端契约，`LocalPlayerEndpoint` 提供本地实现；`GameScreen` 展示投影视图、选择候选并提交。客户端不能自行推进权威牌局。
- `GameStateMachine`、`GameSession`、`LocalGameSession` 为旧调用者保留兼容入口；新功能应接入以上职责边界，不把算法重新放回兼容层。

详细设计与任务契约见 [接口规格 v2](doc/MAHJONG_STATE_MACHINE_API_SPEC.md) 和 [分类开发设计](doc/round-session-split/DEVELOPMENT.md)。本地端点不代表已实现远端传输、账户认证或持久化恢复，这些能力必须显式声明。
