# 袖里藏花 E2 交付报告 (DELIVERY-ANTI-E2.md)

- **任务标识**：SB-E2
- **交付日期**：2026-10-12
- **责任人**：Anti (agy)
- **检视人**：Codex (独立检视)
- **交付状态**：ready_for_review

---

## 1. 交付范围与生产代码变更

本次交付严格遵守 AGENTS.md、DESIGN.md、TASK-ANTI-E2.md 及 TESTS-READY-E2.md 规范：
- 严格限制在 `protocol/src/main`、`server/src/main`、`client/src/main` 生产代码内。
- 未修改任何测试文件 (`*Test.kt`)、未修改 `engine`、未修改 `presentation`、未修改 Gradle 构建脚本。
- 完整保留既有工作区改动与未追踪资产。
- 未执行 git commit，等待 Codex 独立复审。

### 1.1 协议层 (protocol)
- **`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolSleeveBloomModels.kt`** (新增)：
  - 强类型线路模型：`ProtocolSleeveBloomConfig`、`ProtocolSleeveBloomDrawKind`、`ProtocolSleeveBloomKeep`、`ProtocolSleeveBloomRequest`、`ProtocolSleeveBloomChoiceRequest`、`ProtocolSleeveBloomOfferView`、`ProtocolSleeveBloomView`。
  - `ProtocolSleeveBloomOfferView` 强制校验排除 `drawKind == DEAL_EXTRA`。
- **`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`**：
  - `ProtocolAccessCapabilities` 扩展 `sleeveBloomSupported: Boolean = false`。
  - `ProtocolMatchConfig` 扩展 `sleeveBloomConfig: ProtocolSleeveBloomConfig = ProtocolSleeveBloomConfig()`，联动经济配置强制校验显式费用为 10。
  - `ProtocolPlayerSnapshot` 扩展 `sleeveBloom: ProtocolSleeveBloomView? = null`，在 `init` 实施阶段、行动玩家、摸牌实体、决策 ID 与代次的完整交叉校验。
- **`protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt`**：
  - 声明 `submitSleeveBloom` 与 `submitSleeveBloomChoice` 线路接口。
- **`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`**：
  - `ApiTags` 新增 `SUBMIT_SLEEVE_BLOOM` 与 `SUBMIT_SLEEVE_BLOOM_CHOICE` 并入白名单。
  - `ApiRequest` 新增 `sleeveBloom` 与 `sleeveBloomChoice` 载荷字段，严格互斥矩阵校验。
  - 注册并实现 5 个独立专用 TypeAdapter，支持严格属性白名单、禁止伪造字段、类型安全与严格 controlEpoch 校验。
  - 线路上禁止观战者请求携带玩家特权字段。

### 1.2 服务端 (server)
- **`server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`**：
  - 完整双向转换 `SleeveBloomConfig`、`SleeveBloomDrawKind`、`SleeveBloomKeep`、`SleeveBloomRequest`、`SleeveBloomChoiceRequest`、`SleeveBloomOfferView`、`SleeveBloomView`。
  - 快照与配置转换全面接入。
  - `toProtocolCapabilities` 明确声明 `sleeveBloomSupported = true`。
- **`server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`**：
  - `HostReceiptKey` 扩展 `Bloom(roundId, seat, requestId)`。
  - 同桌串行队列接入 `submitSleeveBloom` 与 `submitSleeveBloomChoice`：校验 `sessionValidator`、连接断开与非真人三连门禁；调用领域权威；时钟保持与重键同步；无事件二选一选择（`FIRST` 保留第一张）强制通过 `prevSnap.sleeveBloom != candidateSnap.sleeveBloom` 触发快照推送。
  - 幂等回执缓存与冲突检测完善。
- **`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`**：
  - 转发 `submitSleeveBloom` 与 `submitSleeveBloomChoice` 至 `ServerTableHost`。
- **`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`**：
  - 本地直通实现 `submitSleeveBloom` 与 `submitSleeveBloomChoice`，严格拒绝观战者角色。
  - 能力集声明 `sleeveBloomSupported = true`。
- **`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`**：
  - 分发会话命令至 `SharedRoom`。
- **`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`**：
  - 大厅与房间能力集声明 `sleeveBloomSupported = true`。
  - `spectatorForbiddenApis` 严格加入 `SUBMIT_SLEEVE_BLOOM` 与 `SUBMIT_SLEEVE_BLOOM_CHOICE`。
  - 请求路由分发处理两类袖里藏花报文。

### 1.3 客户端 (client)
- **`client/src/main/kotlin/com/majiang/client/api/SleeveBloomModels.kt`** (新增)：
  - 独立客户端类型：`SleeveBloomConfig`、`SleeveBloomDrawKind`、`SleeveBloomKeep`、`SleeveBloomRequest`、`SleeveBloomChoiceRequest`、`SleeveBloomOfferView`、`SleeveBloomView`。
- **`client/src/main/kotlin/com/majiang/client/api/ClientModels.kt`**：
  - `MatchConfig` 扩展 `sleeveBloomConfig: SleeveBloomConfig = SleeveBloomConfig()`。
  - `Capabilities` 扩展 `sleeveBloomSupported: Boolean = false`。
- **`client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`**：
  - `GameSnapshotView` 扩展 `sleeveBloom: SleeveBloomView? = null`，包含交叉校验、`deepCopy`、`equals` 与 `hashCode`。
- **`client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt`**：
  - 扩展配置、能力集、请求、视图模型映射。
- **`client/src/main/kotlin/com/majiang/client/api/GameSession.kt`**：
  - 门面定义 `submitSleeveBloom` 与 `submitSleeveBloomChoice`。
- **`client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt`**：
  - 完整实现 `submitSleeveBloom` 与 `submitSleeveBloomChoice`。
  - 共用在途原子锁 `pendingState`（防止多发并返回 `ACTION_IN_FLIGHT`）。
  - 绑定当前代次与房间代次，异常/关闭/取消安全释放当前请求锁。
- **`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`**：
  - 组装并发送 UDP `ApiRequest` 报文。

---

## 2. 真实测试验证报告 (来自 XML 测试结果)

通过命令执行全量验证：
```powershell
.\gradlew.bat :protocol:test :server:test :client:test --console=plain --no-build-cache
.\gradlew.bat test --console=plain --no-build-cache --rerun-tasks
```

### 2.1 模块分项汇总 (XML 统计)

| 模块 | 测试总数 | 失败 (Failures) | 错误 (Errors) | 跳过 (Skipped) | 结果 |
|---|---|---|---|---|---|
| **client** | 136 | 0 | 0 | 0 | ✅ PASSED |
| **engine** | 407 | 0 | 0 | 0 | ✅ PASSED |
| **presentation** | 254 | 0 | 0 | 0 | ✅ PASSED |
| **protocol** | 158 | 0 | 0 | 0 | ✅ PASSED |
| **server** | 141 | 0 | 0 | 0 | ✅ PASSED |
| **全系统总计** | **1096** | **0** | **0** | **0** | ✅ **ALL PASSED** |

### 2.2 袖里藏花专项契约测试详情 (16 项)

| 契约测试类 | 覆盖重点 | 用例数 | 结果 |
|---|---|---|---|
| `ProtocolSleeveBloomContractTest` | 线路编解码、互斥矩阵、白名单、非法 controlEpoch/keep 拒绝、私有快照校验、费用联动 | 7 | ✅ PASSED |
| `SleeveBloomServerContractTest` | 串行宿主映射、时钟保持、两阶段私有投影、双回执幂等重放、执行时 binding/deadline 校验 | 2 | ✅ PASSED |
| `SleeveBloomInProcessContractTest` | 本地绑定、观战拒绝、公开快照绝无泄露、保留第一张无事件快照主动推送 | 1 | ✅ PASSED |
| `SleeveBloomClientUdpIntegrationTest` | 真实 UDP 网络端到端施术、庄家起手第14张排除、私有快照 PUSH、重连恢复、第二张换牌与第一张事件空推 | 1 | ✅ PASSED |
| `TileMarkUdpContractTest` (观战伪造) | 原始 UDP 观战报文伪造袖里藏花两类操作入口拦截拒绝 | 1 | ✅ PASSED |
| `SleeveBloomClientContractTest` | 客户端共用在途锁、乐观缓存防伪造、旧流量防复活、同步/异步/关闭失败仅释放自身请求锁、视图映射回环 | 4 | ✅ PASSED |
| **E2 专项总计** | **全链路线路、宿主、客户端、本地及真实 UDP 闭环** | **16** | ✅ **ALL PASSED** |

---

## 3. 遗留问题与后续阶段准备

- **本阶段遗留问题**：0
- **表现层草稿**：`doc/sleeve-bloom/*.kt.pending` 保持原状，待 E2 验收通过后，在 E3 阶段正式接入 `presentation` 牌桌 UI。
