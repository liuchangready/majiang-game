# 偷梁换柱 E2 线路、服务端与客户端交付报告 (DELIVERY-ANTI-E2.md)

- **任务标识**：BS-E2
- **提交人**：Anti (agy)
- **检视人**：Codex
- **日期**：2026-10-13
- **状态**：`ready_for_review`

---

## 1. 变更列表 (Modified & Added Files)

本次开发严格遵守单向编译依赖与模块职责划分，仅实现 `protocol`、`server`、`client` 三个模块的生产代码与交付文档。未修改任何测试源码，未降低断言；未修改 presentation/UI 层（未进入 E3）；完整保留未提交的 `SB-KAN-DORA` 与工作区既有改动：

### 1.1 协议层实现 (`protocol`)
1. `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolBeamSwapModels.kt` (新建)：
   - 核心契约独立 DTO：`ProtocolBeamSwapConfig(enabled = false)`、`ProtocolBeamSwapWindow` (`WALL`, `DEAD_WALL`)、`ProtocolBeamSwapAction` (`SWAP`, `KEEP_ORIGINAL`)、`ProtocolBeamSwapSlot`（密封类：`WallSlot(index)`、`DeadWallSlot(index)`、`ReplacementSlot(index)`，均校验 `index >= 0`）。
   - 独立请求 DTO：`ProtocolBeamSwapRequest(requestId, sessionId, roundId, decisionId, controlEpoch)`、`ProtocolBeamSwapChoiceRequest(...)`（严格区分 `SWAP` 需提供 `handTileId` 与 `targetSlot`，`KEEP_ORIGINAL` 必须省略二者）。
   - 独立视图 DTO：`ProtocolBeamSwapCandidateView(slot, marked, tile)`（未标记严禁泄露 tile 实体，已标记必须提供 non-null tile）、`ProtocolBeamSwapOfferView(...)`（防御性不可变包装）、`ProtocolBeamSwapView(canApply, disabledReason, cost = 10, ...)`。
2. `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`：
   - `ProtocolMatchConfig` 接入 `beamSwapConfig: ProtocolBeamSwapConfig`，加严配置校验（启用时必须开启千术经济且 `costs["BEAM_SWAP"] == 10`）。
   - `ProtocolAccessCapabilities` 接入 `beamSwapSupported: Boolean = false`。
   - `ProtocolPlayerSnapshot` 接入私有视图 `beamSwap: ProtocolBeamSwapView?`，实现交叉门禁校验（CHEAT_WINDOW、摸牌存在、暗手牌白名单、剔除杠牌与摸牌等）。
3. `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`：
   - 标签注册：`ApiTags.SUBMIT_BEAM_SWAP`、`ApiTags.SUBMIT_BEAM_SWAP_CHOICE` 加入 `KNOWN` 白名单。
   - 信封扩展：`ApiRequest` 接入 `beamSwap` 与 `beamSwapChoice` 字段。
   - 互斥矩阵：加严全部已存在 API 标签与新标签之间的载荷互斥校验，明确观战请求拒绝携带两个 beam swap 载荷。
   - TypeAdapter 序列化与严格反序列化：
     - `ProtocolBeamSwapSlotAdapter`：精准校验 `kind`、`index`，拦截负数、非整数、实体 ID 伪造及未知字段。
     - `ProtocolBeamSwapConfigAdapter`：默认 disabled，严格 boolean 解析。
     - `ProtocolBeamSwapRequestAdapter`：校验五元组标识及非负 epoch。
     - `ProtocolBeamSwapChoiceRequestAdapter`：严格区分 SWAP / KEEP_ORIGINAL，拦截显式 null 与字段注入。
     - `ProtocolBeamSwapCandidateViewAdapter`：校验 slot/tile 为非空 JSON 对象，拦截未标记时携带 tile 或标记时缺失 tile。
     - `ProtocolBeamSwapOfferViewAdapter`：candidates 元素显式校验为非空 JSON 对象（拦截 `[null]`、`[1]`、`[[]]` 等解析异常），校验手牌索引 0..135 与非负时间戳/代次。
     - `ProtocolBeamSwapViewAdapter`：恒定 cost = 10，校验 pendingOffer 为非空 JSON 对象。
4. `protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt`：
   - 增加方法 `submitBeamSwap` 与 `submitBeamSwapChoice`；兼容旧实现的默认方法返回 `UnsupportedOperationException` 失败 future。

### 1.2 服务端实现 (`server`)
5. `server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`：
   - 实现领域与传输模型双向映射：`toEngineConfig`、`toProtocolConfig` / `toProtocolMatchConfig`、`toProtocolCapabilities` (声明 `beamSwapSupported = true`)、`toProtocolBeamSwap*` 与 `toEngineBeamSwap*`。
6. `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`：
   - `HostReceiptKey` 扩展独立键：`BeamSwap(requestId)` 与 `BeamSwapChoice(requestId)`。
   - 串行提交：`submitBeamSwap` 与 `submitBeamSwapChoice` 投递至独占串行队列。
   - 校验时序：进入队列后先执行 `sessionValidator` 校验，再处理权威执行与回执查询。
   - 回执与重放：保留首次权威执行回执（包括 REJECTED 回执）及其原始 cursor；对冲突请求立即返回 `REQUEST_ID_CONFLICT`。
   - 状态推送：`publishStateChanges` 检测 `prevSnap.beamSwap != candidateSnap.beamSwap` 并触发快照广播。
7. `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`：
   - 门面转发 `submitBeamSwap` 与 `submitBeamSwapChoice` 至 `SharedRoom`。
   - 能力声明：未绑定与已绑定上下文均上报 `beamSwapSupported = true`。
   - 观战拦截：观战绑定返回 `REJECTED / SPECTATOR_READ_ONLY` 回执。
8. `server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt` & `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`：
   - 增加 `submitBeamSwap` 与 `submitBeamSwapChoice` 转发链路。
9. `server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`：
   - UDP 调度：分发 `ApiTags.SUBMIT_BEAM_SWAP` 与 `ApiTags.SUBMIT_BEAM_SWAP_CHOICE`，解析请求并返回 `ApiResponse(receipt = ...)`。
   - 观战隔离：观战请求遇到 beam 操作返回失败响应，错误为 `UNAUTHENTICATED_SESSION`。
   - 能力上报：大厅阶段与会话阶段 capabilities 均声明 `beamSwapSupported = true`。

### 1.3 客户端实现 (`client`)
10. `client/src/main/kotlin/com/majiang/client/api/BeamSwapModels.kt` (新建)：
    - 客户端完全独立数据契约：`BeamSwapConfig`、`BeamSwapSlot`、`BeamSwapWindow`、`BeamSwapAction`、`BeamSwapRequest`、`BeamSwapChoiceRequest`、`BeamSwapCandidateView`、`BeamSwapOfferView`、`BeamSwapView`。
11. `client/src/main/kotlin/com/majiang/client/api/ClientModels.kt`：
    - `MatchConfig` 接入 `beamSwapConfig: BeamSwapConfig`。
    - `Capabilities` 接入 `beamSwapSupported: Boolean = false`（旧服务端缺失时保持 false）。
12. `client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`：
    - `GameSnapshotView` 接入 `beamSwap: BeamSwapView? = null`，补全 copy/deepCopy/equals/hashCode/toString 防御性隔离。
13. `client/src/main/kotlin/com/majiang/client/api/GameSession.kt`：
    - 扩展门面接口：`submitBeamSwap` 与 `submitBeamSwapChoice`。
14. `client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt`：
    - 实现客户端与协议模型双向映射，接入 `mapSnapshot`、`mapMatchConfig`、`toProtocolMatchConfig`、`mapCapabilities`。
15. `client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt`：
    - 实现 `submitBeamSwap` 与 `submitBeamSwapChoice`。
    - 在途锁保护：与普通出牌决策及其他千术共用 `pendingState`，无乐观状态伪造。
    - 异常与生命周期：同步抛错与异步失败均释放 `pendingState` 锁；重绑定与 session close 正确使未决 future 终结，防止陈旧回执污染新代次。
16. `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`：
    - 实现 UDP 传输层组包与 `reliableRequestor.request` 发送。

---

## 2. 接口与契约达成

- **隐私守恒**：
  - 普通快照与公开快照严禁泄露他人 `pendingOffer`；未标记候选牌面严格隐藏（`tile = null`），仅暴露物理槽位标记；标记命中的候选展示真实牌面。
  - 观战者无法通过本地或 UDP 线路调用 BeamSwap；本地返回 `SPECTATOR_READ_ONLY` 拒绝回执，UDP 返回 `UNAUTHENTICATED_SESSION` 失败响应。
- **回执幂等与时序**：
  - 串行宿主先校验 `sessionValidator`，再进入权威状态机。
  - 对同一请求 ID 重试，严格重放初次执行结果及原始 cursor（包括 REJECTED 回执）；冲突请求判定为 `REQUEST_ID_CONFLICT`。
- **客户端锁一致性**：
  - `ServerConnectionSession` 内的 `submitBeamSwap` 与 `submitBeamSwapChoice` 严密参与在途请求门禁，禁止并发决策提交；断线/重绑定自增代次废弃旧请求。

---

## 3. 测试验证结果

### 3.1 Codex 专项 BeamSwap 契约与集成测试
执行命令：
`./gradlew :protocol:test --tests "*BeamSwap*" :client:test --tests "*BeamSwap*" :server:test --tests "*BeamSwap*" --rerun-tasks`
- **Protocol 契约**：`ProtocolBeamSwapContractTest`（14 项通过）
- **Client 契约**：`BeamSwapClientContractTest`（10 项通过）
- **Server 契约**：`BeamSwapHostContractTest`（7 项通过）
- **UDP 集成**：`BeamSwapClientUdpIntegrationTest`（1 项通过）
- **UDP 隐私**：`BeamSwapUdpPrivacyTest`（1 项通过）
- **小计**：**33 项全部通过，0 失败**

### 3.2 四模块全量无缓存测试
执行命令：
`./gradlew :protocol:test :client:test :server:test :engine:test --no-build-cache`
- **engine**：471 项全部通过
- **protocol**：172 项全部通过
- **server**：151 项全部通过
- **client**：146 项全部通过
- **总计**：**940 项全量通过，0 失败，0 错误，0 跳过**
- **构建状态**：**BUILD SUCCESSFUL in 20s**
