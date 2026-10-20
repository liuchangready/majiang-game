# 看破实施拆分与 Codex 复核门槛

状态：整体委托实施完成，Codex 独立验收通过（2026-10-20）。唯一规则基线为 [DESIGN.md](DESIGN.md)。DeepSeek Harness 已在同一任务 `thread_20261020_211310_1351` 内按 E1→A2→U3 顺序实施并逐阶段自测；Codex 独立检视真实 diff，强制重跑五模块 1710 项测试（0 失败），并复验 Android debug 构建与桌面 GL 冒烟。Android 真机与跨物理网络未实测。

## E1 — engine 权威规则与原子发牌

范围：`engine`。允许修改 `ShuffleSelection`、`ShuffleConfig`、`ShufflePreparationView`、`ShufflePreparationHandler`、`MatchConfig`、`TileMarkState`、`MatchSession`、`ApplicationCoordinator`、`TableAuthority` 及相应 engine 测试。不要修改 protocol/client/server/presentation。

交付要求：

1. 增加 `seeThrough` 选择、10 点固定成本、单独选择也算有效方案、70 点总费用上限；`seeThroughEnabled` 默认关闭，启用时校验洗牌与经济配置及固定 `SEE_THROUGH=10`。
2. 准备提交时拒绝禁用能力、资金不足、错误局/代次、已确认、截止时间及无效阶段；重复 `requestId` 原样回执，冲突载荷拒绝。保留现有截止时未确认者跳过的语义。
3. 截止时冻结所有原始方案和看破席位集合；仅在生成牌山的有效方案副本中移除被看破者的 `initialTargets`，`drawTargets` 不变；原始费用仍按被看破前的方案计算。无人看破时保证同种子输出不变。
4. 发牌候选状态先通过 136 张/经济不变量，再将对手现存落汗组清除与 `RegisterDealtRound` 合为一次 session 变更；成功发牌才安装、扣费和清标，失败不留副作用。多人看破对称结算。不得清除发牌后新施放的落汗。
5. 添加针对单人、多人的偷天换日与落汗、瞒天过海不受影响、纯看破、余额 9/10/70、重试、截止和发牌失败的有意义测试。

Codex 复核：检查原始/有效方案分离、计费及清标原子性，运行 `:engine:test`；任何失败和架构越界要求同一任务返修。

## A2 — protocol/server/client 线路与私有视图

前置：E1 完成并通过 DeepSeek 自测。范围：`protocol`、`server`、`client` 及相应测试。不要修改 engine/presentation。

交付要求：

1. `ProtocolShuffleSelection` 与客户端 `ShuffleSelection` 的构造、`cost()`、`isActive`、copy/equality/hash/序列化和映射完整加入 `seeThrough`；解析只接受 JSON 布尔，缺省 false，显式 null/数字/字符串拒绝。
2. `ShuffleConfig` 的配置 DTO、反序列化白名单、服务端/客户端 mapper、客户端准备视图均携带 `seeThroughEnabled`；禁用能力的服务端校验始终在 engine，不依赖 UI。
3. 业务协议版本 6→7，所有能力声明/默认版本/握手测试一致；仅业务版本变化，MJ-TCP/1 固定帧头 `wireVersion` 仍为 1。旧业务版本明确拒绝，避免错误理解布尔字段。
4. 保持 `SUBMIT_SHUFFLE_PLAN` 的专有载荷、信封 requestId/roundId/controlEpoch、观战只读、私有选择不向他家/观战泄漏、TCP 作用域幂等与重连一致。增加正反编解码、畸形字段、双传输提交和私密投影测试。

Codex 复核：检查版本所有引用、严格 DTO 与映射覆盖、公开流泄漏，运行 `:protocol:test :client:test :server:test`。

## U3 — 菜单、牌桌与 AI 决策

前置：A2 完成并通过 DeepSeek 自测。范围：`presentation`、`engine/ai`（必要时 `TableAuthority` 的 AI 投影/`server` 的 AI 接线）及相应测试。不要修改 protocol DTO 契约。

交付要求：

1. 单机与建房预设显式启用看破并配置 `SEE_THROUGH=10`；准备面板显示可选开关、10 点费用、合计最多 70 点、选中/禁用/确认/重连状态；只有看破也允许确认，跳过仍是全空方案。
2. UI 文案解释发牌前生效、仅抵消本局偷天换日并清除对手既有落汗；不暗示能抓包、禁用瞒天过海或永久封锁新落汗。保持 1280×720 和现有屏幕尺寸下可点、可读。
3. AI 在合法策略开启时把看破作为准备候选，能够与自己的码牌目标组合，服从预算/点数/候选上限；不能读取其他座位的私有方案。旧策略或能力关闭时行为不变。
4. 测试准备模型的费用/锁定/恢复、真实输入点击、AI 候选与权威验资；运行相关 presentation/AI 测试和桌面 GL 冒烟（环境支持时）。

Codex 复核：审查 UI 的真实矩形和输入、视觉可读性、AI 信息边界；运行模块测试与必要 GL 冒烟，最后全量 `./gradlew test` 和 Android debug 构建。Android 真机/跨物理网络仍须如实标记为未实测。

## 统一交付格式

DeepSeek 在整体任务回调时按阶段提供改动文件清单、关键设计选择、实际运行的命令与结果、未完成项及风险，不要提交或推送。Codex 对照设计逐项复核，必要时在原整体任务会话要求返修；三个阶段全过后再更新设计状态与文档索引。任务执行过程中不得复活旧 UDP/V3/TCP 协议，也不得改变七模块依赖方向。
