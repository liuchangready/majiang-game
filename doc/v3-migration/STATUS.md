# V3 重构实际状态

最新增量（2026-10-05）：展示模块由 `core` 更名为 `presentation`。目录、settings.gradle、lwjgl3/android 依赖与文档同步更新，包名保持 `com.majiang.*` 不变；顺带移除 engine 架构测试中指向旧 `core/src` 的死路径回退。历史文档中的 `:core:test` 命令为当时实跑记录，保留不改。

最新增量（2026-10-05）：core侧V2接口清理返修完成。Anti开发，Codex分析拆分与独立检视；**561项测试全部通过（0失败、0跳过）**，桌面/Android构建及**11个真实GL验收**通过。修复异步测试夹具、真实动作覆盖、节奏用例控制者装配和9个旧GL工具；补齐立直/副露确认使用最新客户端候选的UI校验。engine批2/批3兼容接口保留，Android尚未真机验证。见 [根因与验收](V2-CLEANUP-FIX-REVIEW.md)、[分工](V2-CLEANUP-FIX-PLAN.md)。

最新增量（2026-10-02）：击飞终局界面与常驻听牌提示完成。Antigravity 开发，Codex 设计与独立验收；**556 项测试全部通过**，桌面/Android 构建及真实 GL 检查通过。支持终局排名、明细切换、断线返回菜单，以及默听/立直/副露的完整待牌展示。见 [设计](END-TENPAI-DESIGN.md) 与 [验收记录](END-TENPAI-REVIEW.md)。

最新增量（2026-10-02）：洗牌千术四组 Anti 并行开发及返修验收完成。每局独立 10 秒选择、偷天换日/瞒天过海、原子扣点与跨局结转、网络传输和中文界面已接入。最终 **539 项测试全部通过**，桌面/Android 构建与真实 GL 流程通过。设计、任务和实测证据见 [SHUFFLE-ACCEPTANCE.md](SHUFFLE-ACCEPTANCE.md)。此前 `3+20` 与四家副露功能保留；以下为历史迁移记录。

记录时间：2026-10-01 17:07（Asia/Shanghai）。Codex 负责设计、审查和实测；Antigravity 经本地 Agent Mesh MCP Bridge 执行生产开发。本轮迁移与返修验收通过，工作区改动尚未提交。

## 当前验收

模块迁移、宿主、客户端、网络和展示接线已经实现。ANTI-U4 已完成并退出，Codex 对最终代码完成联跑和真实 GL 检查。

- 引擎 318、客户端 30、展示 core 154、服务端 3 项，共 **505 项测试全部通过，0 失败、0 跳过**。
- U3 最后修订引入的两项吃牌选择框回归已在 U4 修复：未选择时确认保持选项；候选刷新失效时吞掉当前点击，防止穿透。
- Android 调试 APK、桌面 classes、server installDist 最终联跑构建通过，证据：build/v3-migration/anti-final-u4-validation.log。
- 最终代码的真实 GL 冒烟通过：发牌、点击弃牌、AI 响应、返回菜单释放宿主；日志 anti-render-smoke-final.log，截图 v3-dealt.png / v3-after-discard.png 已检视。
- 完整验收矩阵见 [ACCEPTANCE.md](ACCEPTANCE.md)。Android 仅验证构建，未做真机测试。

## 已落地

- engine / client / presentation（原 core）/ server 分层；137 个文件机械迁移并逐项校验 SHA-256，原 71 个测试源文件全部保留在对应模块。
- RoundStateMachine 只承载单局状态；MatchSession 管理整场进度、分数、出千点数、历史和排名；规则校验与结算服务在两者之外。
- TableHost 串行管理请求、每席位 20+3 计时、超时默认动作、暂停、AI 控制与立直摸切节奏。
- LocalSession / RemoteSession / MallSession 共享异步接口；GameClient 处理候选、请求门控、事件顺序与恢复，Screen 负责展示和交互。
- TCP 服务端绑定席位和连接代次，支持房间连接、断线托管、重连替换、显式收回控制；快照提供公开历史、最终排名、控制态和完整结算恢复。
- 默认桌面/Android 入口已经使用 V3。保留旧显式入口用于兼容和有效规则/UI 回归；删除空置重复实现与迁移前临时脚本。

代码路径、运行命令和接口说明见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 开发追溯

用户先前指定 Claude Code GLM 接续时，GLM 因额度中断。用户本次指定继续 Anti 后，已切回 Antigravity；不再等待 GLM。

本轮 R1、C1、T1/T2、S1、U1/U2/U3/U4 均已交付，开发进程均已结束。各任务的范围和报告见本目录 TASK-ANTI-*.md / delivery-ANTI-*.md；桥接执行日志见 build/v3-migration/bridge-ANTI-*。报告仅代表开发交付，测试以 Codex 实际运行结果为准。早期失败日志保留作为返修证据，不代表最终状态。

## 范围边界

本轮实现四人本地与局域网接入基础。三人麻将、公网认证/TLS 部署、自动匹配/大厅 UI、持久化崩溃恢复和主机迁移尚未实现。洗牌阶段千术与 UI 已接线；摸打阶段千术暂未开放。出千点数归属整场对局，不是跨比赛账户系统。
