# 🀄 千术麻将 (Mahjong Cheater) - 架构与开发指南 (AGENTS.md)

**2026-10-07 当前迁移状态：** R1–R6 全部完成并验收，默认产品入口为 V4，旧 TCP/V3 运行链已按引用与测试保留审计清理。不筛选全量测试 978/978、独立审查 188/188、桌面/Android 构建和 11 组真实 GL 冒烟均通过；见 [R6 验收](doc/v4-migration/ACCEPTANCE-R6.md)。用户“直接继续”后由 Codex 与并行子代理收口；此前 Anti/GLM 额度、开发者分工、暂停与阶段未完成叙述均为历史记录。工作区改动尚未提交 Git。

本文档汇总了本项目当前的所有模块架构、设计模式、双端运行规范与开发约定，供后续参与开发的 AI Agent 与开发者参考。

## 当前迁移约定 2026 年 10 月 6 日

**最新用户授权：使用 Anti 开发，并继续完成 R5 和 R6。Codex 监督与验收；先完成并验收 R4，再推进 R5 产品入口/表现层迁移和 R6 引用审计/V3 清理，不再在 R4 后停止。以下 GLM 指定及“R4 后停止”段落仅为历史记录，均被本条覆盖。刚重启的两路 GLM 已按用户切换要求结束并确认 exec 终止，保留全部已落盘改动。Anti 明确配额耗尽时仍按已有授权切本地 GLM；同一任务禁止重叠写入，独立文件范围可并行。**

历史开发者切换：上一轮用户17:58指定GLM，留下R4部分实现和失败证据；本轮用户已重新指定Anti，以上最新授权为准。保留GLM全部部分改动，不能按历史停止或开发者文字覆盖当前指令。

**最新执行边界：用户已恢复并授权完成 R5、R6。继续 R4B2 与 R4 全部验收，通过后自动派发 R5 产品入口/表现层迁移，再执行 R6 引用审计与无用 V3 清理。此前“R4 后停止”已撤销；阶段验收门槛不变，不能未验收 R4 就混入 R5/R6。**

用户最新授权：**继续由 Anti 开发 V4，Codex 监督与测试验收；完成后清理无用 V3，不考虑旧入口兼容性**。以 [V4 计划](doc/v4-migration/PLAN.md)、[最终契约](doc/v4-migration/CONTRACT.md)、[当前状态](doc/v4-migration/STATUS.md) 为准。GLM 已接受的 ROOM 修复与未完成 UDP 改动均保留；当前派发恢复使用 TASK-ANTI 文档，历史交付保留作为记录。

下文 V3 结构和保留兼容入口的要求是迁移起点，不再约束最终实现。不要为旧入口新增兼容重载、别名或转发壳。有效规则、物理守恒、算分、计时与玩法测试仍必须保留并迁移，不能删除或跳过来使迁移通过。

用户补充授权：Anti明确返回配额耗尽时，自动转Claude Code（沿用本地GLM配置）接续当前未完成任务；保留全部已落盘改动，Codex继续监督与验收，同一任务不重叠启动两个写入进程。无需再次询问切换许可。

用户已授权多个agent并行开发。当前按server UDP、client UDP、ServerConnectionSession三个互不重叠文件范围派发；同一任务切换开发者前必须结束旧写入进程。共享protocol契约冻结，Codex串行执行构建与整体验收，不能跨任务修改他人文件。

用户已要求 Anti 交付后自动通知 Codex 继续检视和下一步。当前会话已启用持续目标，桥接结束时写入 `build/v4-migration/review-notifications/*.json` 交付事件（事件文件本身不唤醒会话，由持续目标驱动接续）。Codex 自动确认进程退出、读取交付并验收，失败返修、通过再进入下一阶段，不等用户催问。额度切换与暂停约定不变；交付事件绝不代表验收通过。

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
    - **碰杠破千法则**：开局后牌山绝对冻结，顺位因吃碰跳跃导致好牌被对手摸走。
  - **牌型重叠度矩阵算法**：[`TILE_OVERLAP_MATRIX_ALGORITHM.md`](file:///D:/majiang/TILE_OVERLAP_MATRIX_ALGORITHM.md)
    - **34维特征空间建模**：万、条、筒与字牌特征向量余弦相似度；
    - **两两冲突冲突矩阵**：异花色 0 干扰，同花色 1.0 剧烈踩踏；
    - **个性化熟练度衰减与物理牌池配额**。

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
- 默认单机入口 `MahjongGame.launchGame()` 由 `host/TableHost` 与 `MahjongGame.render` 驱动自动操作，每次最多一个动作；动画可门控自动节奏，但不无限推迟权威超时。每座位默认每局 20 秒储备＋每次 3 秒思考时间。
- `access/SessionAccess` 与绑定 roundId 的 `MahjongGameAccess` 是统一异步契约；`GameClient` 维护有序快照与恢复，`GameScreen` 经表现适配器选择候选并提交。客户端不能自行推进权威牌局。
- `LocalSession`、`RemoteSession`、`MallSession` 使用相同访问接口，网络房间复用 TableHost。快照包含公开 Session 历史/最终结果、权威控制状态及终局恢复明细；连接替换后旧连接不能继续操作。
- `GameSession`、`LocalGameSession` 为旧调用者保留兼容入口（旧 `GameStateMachine` 已按 GSM 合并方案A删除，统一走 `ApplicationCoordinator`）；新功能应接入以上职责边界，不把算法重新放回兼容层。

V3 模块：`engine` 保存纯 JVM 领域、宿主及共享 access；`client` 保存客户端同步与网络协议；`presentation` 保存 libGDX 展示；`server` 保存无图形房间服务；平台启动保留在 lwjgl3/android。禁止 engine 反向依赖 client、server 或 libGDX。

`PlayerEndpoint`、`LocalPlayerEndpoint`、`LocalTableRuntime` 保留为旧显式调用和回归测试兼容入口，产品默认不走此链。新功能接入 V3；不要通过删除有效规则测试规避迁移失败。

当前结构见 [V3 代码与运行](doc/v3-migration/ARCHITECTURE.md)、[验收状态](doc/v3-migration/STATUS.md)，目标设计见 [游戏方案 V3](doc/GAME_ARCHITECTURE_V3.md)。已实现局域网 TCP 接口，不代表公网账户服务、自动匹配界面、三人规则、持久化恢复或主机迁移已经完成。
