# 🀄 千术麻将 (Mahjong Cheater) 设计文档库

本目录（`doc/`）存放《千术麻将》完整的架构设计、日麻规则与千术算法规范文档。当前系统默认且唯一生产架构为 **V4 架构**（R1~R6 全部完成验收），旧单体 `core` 与 V3 兼容链已彻底移除。

---

## 📚 核心设计与规范导航

### 1. [千术麻将游戏方案 V4：菜单/牌桌拆分、Client 桥接与 Server 接口化 (GAME_ARCHITECTURE_V4.md)](./GAME_ARCHITECTURE_V4.md)
- **最新目标与落地架构**：
  - 七模块物理拆分（`engine`、`protocol`、`client`、`server`、`presentation`、`lwjgl3`、`android`）；
  - 单机与联机同一条代码路径（单机 = 进程内传输 + `InProcessServer`；联机 = 可靠 UDP + `UdpTableServer`）；
  - Server 唯一权威循环与串行裁决（动作 > 超时 > AI 托管 > 控制权切换）；
  - 全量快照基线 + 单调递增游标（cursor）事件订阅与对账。

### 2. [整体架构与物理守恒机制 (FRAMEWORK_ARCHITECTURE.md)](./FRAMEWORK_ARCHITECTURE.md)
- **严格 136 张物理闭环**：单副麻将实体牌绝对守恒，手牌、牌山、牌河、副露之间仅发生物理转移，无中生有即判定作弊穿帮。
- **四大千术绝技与视线博弈**：
  - 袖里乾坤（手牌与藏牌互换）、移花接木（牌山偷摸换牌）、神仙透视（窥探对手暗牌与牌山深层）、声东击西（敲击引开全场视线）。
  - `SuspicionSystem` 怀疑度系统（0~100%），AI 动态视线追踪与抓包判定（Busted）。

### 3. [日本立直麻将规则设计标准 (RIICHI_RULES_DESIGN.md)](./RIICHI_RULES_DESIGN.md)
> 参照雀魂（Mahjong Soul）规则标准规范

- **规则全集**：四人东风/半庄战、41 种役种与役满、王牌区 14 张、表/里/杠宝牌机制。
- **算分全表**：副底、面子符、雀头符、听牌符、和了符（庄/闲 × 荣和/自摸）与满贯以上固定得点。
- **振听机制**：舍牌振听、同巡振听、立直振听定义与权威状态机裁决。
- **流局判定**：荒牌流局不听罚符分配、四风连打/四杠散了/四家立直/九种九牌途中流局。

### 4. [服务端与客户端通信协议 (SERVER_CLIENT_API.md)](./SERVER_CLIENT_API.md)
- 房间生命周期、连接代次、顶号注销与超时托管。
- 大厅会话（`LobbySession`）与牌桌会话（`GameSession`）线上契约。
- 可靠 UDP 数据报协议（22 字节帧头、分片重组、心跳保活、幂等重试、滑动重传窗口）。

### 5. 核心算法设计说明书（项目根目录）
- **[开局码牌算法说明书 (../STACKING_ALGORITHM.md)](../STACKING_ALGORITHM.md)**：
  - 34 维向量空间与起手牌（0~52）、摸牌流（53~135）预排分布。
  - 碰杠破千法则（动牌导致牌流跳跃）。
- **[牌型重叠度矩阵算法 (../TILE_OVERLAP_MATRIX_ALGORITHM.md)](../TILE_OVERLAP_MATRIX_ALGORITHM.md)**：
  - 牌型特征向量余弦相似度计算冲突矩阵，异花色 0 干扰，同花色冲突衰减。

---

## 🏛️ 迁移记录与归档导航

- **[V4 迁移验收总报告 (v4-migration/ACCEPTANCE-R6.md)](./v4-migration/ACCEPTANCE-R6.md)**：
  - R1~R6 全部完成验收记录、978 项测试全绿、188 项独立审查全绿、11 组真实 GL 冒烟测试全过。
- **[V4 最终契约与规范 (v4-migration/CONTRACT.md)](./v4-migration/CONTRACT.md)**：
  - 严格模块单向依赖（Presentation → Client → Protocol ← Server → Engine）、三层 DTO 隔离。
- **[历史归档目录 (archive/)](./archive/)**：
  - `archive/architecture-v2/`：V2 历史评审与 GSM 合并方案
  - `archive/v3-migration/`：旧 V3 阶段全套任务与交付记录
  - `archive/v4-migration-tasks/`：V4 迁移过程中的 80+ 个临时 Agent 任务派发与反馈单
  - `archive/legacy-specs/`：早期过时设计草案（Ktor/WebSocket 网络设计、V1 API 等）
