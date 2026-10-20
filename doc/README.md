# 🀄 千术麻将 (Mahjong Cheater) 设计文档库

本目录（`doc/`）存放《千术麻将》的架构设计、日麻规则与千术算法规范。当前生产架构为 **V4**，旧单体与 V3 兼容运行链已移除。连接控制实现已迁入 server 模块的 `com.majiang.server.control` 包，并由 Codex 独立验收；最新证据见 [连接控制迁移验收](v4-migration/ACCEPTANCE-CONNECTION-OWNERSHIP.md)。R1–R6 的历史验收记录保留原始口径。

---

## 📚 核心设计与规范导航

### 1. [千术麻将游戏方案 V4：菜单/牌桌拆分、Client 桥接与 Server 接口化 (GAME_ARCHITECTURE_V4.md)](./GAME_ARCHITECTURE_V4.md)
- **目标与落地架构**：
  - 七模块物理拆分（`engine`、`protocol`、`client`、`server`、`presentation`、`lwjgl3`、`android`）；
  - 单机与联机同一套代码路径（单机 = 进程内调用 + `InProcessServer`；联机 = 可靠 UDP + `UdpTableServer`）；
  - `ServerTableHost` 每桌唯一权威串行队列：统一入队串行裁决，玩家提交严格校验 deadline，tick 先裁决超时再调度 AI，不同决策按序生效，响应窗口允许多个座位提交记录；动画仅门控客户端自动推进节奏；
  - 队列外异步 AI 推理，提交时校验 `roundId`、`decisionId` 与 `controlEpoch`；
  - 权威 Deadline（20s 储备 + 3s 思考）优先裁决，不被动画无限推迟；
  - PUSH 始终为全量快照 + 可见事件，`isFullSnapshot` 明确建立/重置基线，服务端 256 滑动重传窗口，独立座位游标与观战公开流。

### 2. [整体架构与物理守恒机制 (FRAMEWORK_ARCHITECTURE.md)](./FRAMEWORK_ARCHITECTURE.md)
- **严格 136 张物理闭环**：单副麻将实体牌（0..135）绝对守恒，手牌、牌山、牌河、副露之间仅发生物理转移，TilePool 绑定状态时由 StateInvariants 严格审计容器归属、唯一实体与账本一致性。
- **三大线上千术与出千经济**：
  - 落汗（标记实体牌追踪透视）、袖里藏花（藏匿调换与牌山原语）、偷梁换柱（手牌与活牌山/王牌等额互换）。
  - `CheatLedger` 出千点流水账本与经济体系，出千变动私有同步与局末汇总。
- **生命周期清晰分层**：
  - 房间生命周期 (`ProtocolRoomLifecycle` / `RoomLifecycle`)；
  - 整场局间账本与排名 (`MatchSession` / `SessionLifecycle`)；
  - 单局状态机 (`RoundStateMachine`)：终态固定不可复活，发牌连庄均创建新实例与新 `roundId`，包含 `CHANKAN_WINDOW` 抢杠裁决。

### 3. [日本立直麻将规则设计标准 (RIICHI_RULES_DESIGN.md)](./RIICHI_RULES_DESIGN.md)
> 参照雀魂（Mahjong Soul）规则标准规范

- **规则全集**：四人东风/半庄战、41 种役种与役满、王牌区 14 张、表/里/杠宝牌机制。
- **算分全表**：副底、面子符、雀头符、听牌符、和了符（庄/闲 × 荣和/自摸）与满贯以上固定得点。
- **振听机制**：舍牌振听、同巡振听、立直振听定义与权威状态机裁决。
- **流局判定**：荒牌流局不听罚符分配、四风连打/四杠散了/四家立直/九种九牌途中流局。

### 4. [服务端与客户端通信协议 (SERVER_CLIENT_API.md)](./SERVER_CLIENT_API.md)
- 协议接口明细：大厅服务 (`LobbyApi`)、对局服务 (`SessionApi`)、观战服务 (`SpectatorApi`)、推送通道 (`PushChannel`)。
- 控制权显式语义：`claimControl()`（真人收回控制）与 `releaseControl()`（AI 托管），观战只读接入。
- 可靠 UDP 数据报协议：版本 4、22 字节帧头、1200 字节数据报上限、1090 字节分片阈值、3 秒心跳 / 10 秒判死、服务端 256 推送重传窗口。
- 客户端数据映射流水线：`engine` → `server EngineProtocolMapper` → `protocol DTO` → `client ProtocolViewMapper` → `client 视图模型`。

### 5. 核心算法设计说明书（项目根目录）
- **[开局码牌算法说明书 (../STACKING_ALGORITHM.md)](../STACKING_ALGORITHM.md)**：
  - 34 维向量空间与起手牌（0~52）、摸牌流（53~135）预排分布。
  - 碰杠破千法则（动牌导致牌流跳跃）。
- **[牌型重叠度矩阵算法说明书 (../TILE_OVERLAP_MATRIX_ALGORITHM.md)](../TILE_OVERLAP_MATRIX_ALGORITHM.md)**：
  - 牌型特征向量余弦相似度计算冲突矩阵，异花色 0 干扰，同花色冲突衰减。

---

## 功能设计草案

- [偷梁换柱千术设计](beam-swap/DESIGN.md)：**已实现并独立验收**，见[最终验收](beam-swap/ACCEPTANCE.md)：1225项测试、双端构建与双视口GL通过，Android真机未验证。运行时换牌千术：正常摸牌后可将原有暗手牌中的一张牌与活牌山后续八张互换，杠后补牌可与死山任意未展示牌（岭上槽/宝牌暗区/替补）互换，每次 10 点；候选默认牌背、仅本人落汗标记命中亮面（未标记可盲换），零透视、视觉由落汗供给；两步激活→选择交互，与袖里藏花**同取牌互斥（二选一）**、挂起全局唯一。命名决策：原提案「偷天换日」与开局码牌重名，经用户确认定名「偷梁换柱」，开局码牌命名不动。
- [袖里藏花千术设计](sleeve-bloom/DESIGN.md)：已实现规则为杠后仅在原补牌与下一岭上牌之间二选一，第四次杠补牌窗口禁止施术且不扣费。[立直与他家回合扩展设计](sleeve-bloom/EXTENSION-RIICHI-OFFTURN-DESIGN.md)已补充，**尚未实现或验收**。既有验证见[岭上修订验收](sleeve-bloom/ACCEPTANCE-RINSHAN.md)，[初版验收](sleeve-bloom/ACCEPTANCE.md)保留历史记录。含V4分层、136实体守恒、幂等、时钟、控制权恢复与算分契约。
- [袖里藏花图形设计](sleeve-bloom/VISUAL-DESIGN.md)：右列入口和二选一模态面板、权威倒计时、重连与控制权恢复；杠后候选标签为“岭上次张”。验证进度见[岭上修订验收](sleeve-bloom/ACCEPTANCE-RINSHAN.md)，Android真机触控未验证。
- [落汗图形展示与牌河提示设计](tile-mark/VISUAL-DESIGN.md)：Anti 开发、Codex 独立验收通过，1050 项测试、双端构建与两种视口真实 GL，见[展示验收](tile-mark/ACCEPTANCE-VISUAL.md)。完整对手牌背、落汗半透明牌面；提示按东南西北显示暗手牌、牌山剩余可摸总数和可定位牌河，支持滚轮与触摸拖动。Android 真机触摸尚未验证。
- [落汗千术设计](tile-mark/DESIGN.md)：每次从本人手牌标记 4 张实体牌，消耗 10 个额度；每位玩家最多保留 12 张有效标记，各组当局生效并持续后续 3 局，牌河记录具体位置。Anti 实现，Codex 独立编写测试及检视，已完成全链路验收：1000项测试、双端构建和真实GL通过，详见[最终验收](tile-mark/ACCEPTANCE.md)。Android真机触摸尚未验证。

## 🏛️ 迁移记录与归档导航

- **[V4 迁移验收总报告 (v4-migration/ACCEPTANCE-R6.md)](./v4-migration/ACCEPTANCE-R6.md)**：
  - R1~R6 全部完成验收记录、978 项测试全绿、188 项独立审查全绿、11 组真实 GL 冒烟测试全过。
- **[V4 最终契约与规范 (v4-migration/CONTRACT.md)](./v4-migration/CONTRACT.md)**：
  - 严格模块单向依赖（Presentation → Client → Protocol ← Server → Engine）、三层类型隔离。
- **[历史归档目录 (archive/)](./archive/)**：
  - `archive/architecture-v2/`：V2 历史评审与 GSM 合并方案
  - `archive/v3-migration/`：旧 V3 阶段全套任务与交付记录
  - `archive/v4-migration-tasks/`：V4 迁移过程中的历史 Agent 任务派发与反馈单
  - `archive/legacy-specs/`：早期过时设计草案（Ktor/WebSocket 网络设计、V1 API 等）
