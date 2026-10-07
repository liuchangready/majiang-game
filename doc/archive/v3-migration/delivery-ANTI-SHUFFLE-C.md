# 任务 ANTI-SHUFFLE-C 交付报告：客户端与网络洗牌千术传输

- **任务编号**：ANTI-SHUFFLE-C
- **交付日期**：2026-10-02
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **设计与验收**：Codex (Designer, Test Author, Reviewer & Builder)
- **遵循契约**：[SHUFFLE-CONTRACT.md](SHUFFLE-CONTRACT.md)

---

## 1. 变更清单与所有权边界

本次交付严格遵守代码所有权边界，仅修改 `client/src/main/**` 与 `server/src/main/**` 生产代码，未修改任何测试套件、构建配置、`engine` 或 `core` 模块：

| 模块 / 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `client/src/main/kotlin/com/majiang/network/protocol/NetworkMessages.kt` | 修改 | 新增 `NetworkRequest.TYPE_SUBMIT_SHUFFLE_PLAN` 请求类型白名单与 `shufflePlan: ShufflePlanRequest?` 协议字段，协议版本保持版本 3 增量兼容 |
| `client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt` | 修改 | 新增 `ShuffleConfigAdapter`、`ShuffleSelectionAdapter`、`ShufflePreparationViewAdapter`、`ShufflePlanRequestAdapter`；`PlayerSnapshotAdapter` 扩展 `preparation` 读写（支持旧快照无 prep 兼容）；`MatchConfigAdapter` 扩展 `shuffleConfig` 编解码；`NetworkRequestAdapter` 扩展 `SUBMIT_SHUFFLE_PLAN` 必填与边界校验 |
| `client/src/main/kotlin/com/majiang/network/NetworkSessionAccess.kt` | 修改 | 实现 `SessionAccess.submitShufflePlan(request)` 接口重写，异步通过长连接发送网络请求，统一映射回执 |
| `client/src/main/kotlin/com/majiang/client/GameClient.kt` | 修改 | 实现 `GameClient.submitShufflePlan(request)`，严格前置校验连接态、游标对齐、SELECTING 阶段、未确认、roundId/controlEpoch 匹配与点数充足性；统一 pending 门禁与生命周期守护 |
| `server/src/main/kotlin/com/majiang/server/ServerRoom.kt` | 修改 | 实现 `submitShufflePlan` 命令转发，严格校验当前活跃连接身份及连接代次，直接通过绑定的 `LocalSession` 提交权威 TableHost，杜绝客户端伪造他人席位、牌山、种子或费用 |
| `server/src/main/kotlin/com/majiang/server/TableServer.kt` | 修改 | 在 `handleSessionRequest` 中接入 `TYPE_SUBMIT_SHUFFLE_PLAN` 分发，异常或校验失败均产出结构化回执，杜绝客户端 Future 悬挂 |
| `doc/v3-migration/delivery-ANTI-SHUFFLE-C.md` | 新增 | 本交付报告 |

---

## 2. 关键设计与契约落实

### 2.1 客户端准备方案提交门控与前置校验 (`GameClient.submitShufflePlan`)
- **异步非阻塞与并发门控**：复用既有 `pendingState.compareAndSet(false, true)` 路径，在途期间拒绝重复提交，杜绝竞态与重复按键；回执到达后在未关闭状态下原子复位。
- **前置完备状态核验**：
  1. `isClosed` -> 立即拒绝 `CLIENT_CLOSED`；
  2. 游标缺口或恢复中 (`hasGapState || isResuming || synchronizer.hasBufferedGaps`) -> 拒绝 `CLIENT_OUT_OF_SYNC`；
  3. 未连接 (`snapshot == null`) -> 拒绝 `NOT_CONNECTED`；
  4. 无活跃准备视图 (`prep == null`) -> 拒绝 `NO_ACTIVE_PREPARATION`；
  5. 阶段不是准备选择 (`prep.status != ShufflePreparationStatus.SELECTING`) -> 拒绝 `PREPARATION_NOT_SELECTING`；
  6. 已经确认锁定 (`prep.confirmed`) -> 拒绝 `PREPARATION_ALREADY_CONFIRMED`；
  7. 局号不一致 (`request.roundId != prep.roundId`) -> 拒绝 `ROUND_ID_MISMATCH`；
  8. 控制代次不一致 (`request.controlEpoch != ownControlEpoch`) -> 拒绝 `CONTROL_EPOCH_MISMATCH`；
  9. 点数不足 (`request.selection.cost() > prep.availablePoints`) -> 拒绝 `INSUFFICIENT_CHEAT_POINTS`。
- **生命周期守护**：客户端在关闭状态下收到的迟到回执安全忽略，不复活状态；底层同步异常时即时复位 `pendingState` 并返回 failed future，杜绝永久假死。客户端不自行生成或推导牌山。

### 2.2 协议层序列化与严格反序列化 (`ProtocolJson`)
- **严格白名单与边界约束**：
  - `ShuffleTarget` 仅限 6 种合法枚举字符串（`CHINITSU_MAN`, `CHINITSU_SOU`, `CHINITSU_PIN`, `DAISANGEN`, `KOKUSHI`, `DAISUUSHII`）；未知字面量抛出 `JsonParseException`；
  - `ShuffleSelection` 采用 `LinkedHashSet` 读取并包装为不可变 Set，集合上限限制为 6，消除空构造器注入与外部修改风险；
  - `ShufflePreparationView` 时钟字段校验：`opensAtMillis >= 0`、`deadlineMillis >= opensAtMillis`、`serverNowMillis >= 0`、预留与可用点数 `>= 0`；
  - 绝对不序列化服务端内部牌山、随机种子或其他席位的方案，保障物理信息私密性；
  - 旧版快照解析兼容：快照中的 `preparation` 字段缺省或为 null 时安全提供 null，无缝反序列化历史快照；
  - `MatchConfigAdapter` 完整支持 `shuffleConfig` 字段的读写，缺失时安全回退至默认 `ShuffleConfig()`，保持旧测试与协议兼容；
  - `NetworkRequestAdapter` 针对 `TYPE_SUBMIT_SHUFFLE_PLAN` 强制要求 `shufflePlan` 载荷非空且严格校验。
- **版本策略**：协议版本保持为 3（增量扩展新请求类型与快照字段），不盲目递增主版本号，保持既有版本 3 夹具与客户端测试畅通。

### 2.3 网络层统一异步流与权威服务端防伪 (`NetworkSessionAccess` / `ServerRoom` / `TableServer`)
- **不可伪造身份与代次绑定**：
  - 客户端通过 `NetworkSessionAccess.submitShufflePlan` 发送 `TYPE_SUBMIT_SHUFFLE_PLAN` 请求；
  - `ServerRoom` 在执行命令前，通过 `commandTarget(conn)` 严格校验请求连接必须等于该席位当前的 `activeBinding.connection` 且连接代次一致；
  - 方案直接交由服务端登记的该席位 `LocalSession`（`TableHost.connect(seat)`）处理，席位由服务端强绑定，客户端无法为他人指定席位或篡改费用；
  - 配置传递保证：`ServerRoom` 构造 `TableHost` 时直接传递 `config`（包含完整的 `shuffleConfig`），服务端没有静默关闭洗牌千术的构造器默认值。
- **异常防护与无悬挂设计**：
  - `TableServer.handleSessionRequest` 使用统一的 `respond { ... }` 异常兜底，网络解析失败或领域异常均返回对应 `requestId` 的错误响应，客户端在途 Future 绝不悬挂。

---

## 3. 交付说明

所有生产改动已在对应模块精准就位，接口严格符合 [SHUFFLE-CONTRACT.md](SHUFFLE-CONTRACT.md) 规定的类型签名与语义约束。由 Codex 负责后续测试设计、全量编译与回归构建验证。
