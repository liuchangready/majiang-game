# 袖里藏花 E2 进度记录 (PROGRESS-ANTI-E2.md)

- 任务标识：SB-E2
- 日期：2026-10-12
- 责任人：Anti (agy)
- 状态：已完成，待检视 (ready_for_review)

## 任务目标
按照 AGENTS.md、doc/sleeve-bloom/DESIGN.md、TASK-ANTI-E2.md 与 TESTS-READY-E2.md 规范，完成 E2 阶段生产代码接线（protocol, server, client）：
1. 协议层（protocol）：
   - 独立强类型 DTO：`ProtocolSleeveBloomConfig`、`ProtocolSleeveBloomDrawKind`、`ProtocolSleeveBloomKeep`、`ProtocolSleeveBloomRequest`、`ProtocolSleeveBloomChoiceRequest`、`ProtocolSleeveBloomOfferView`、`ProtocolSleeveBloomView`。
   - `ApiTags` 新增 `SUBMIT_SLEEVE_BLOOM` 与 `SUBMIT_SLEEVE_BLOOM_CHOICE` 并入已知 API 白名单。
   - `ApiRequest` 新增 `sleeveBloom` 与 `sleeveBloomChoice` 载荷字段，严格互斥矩阵校验。
   - 严格 TypeAdapter（字段白名单、类型校验、枚举校验、防伪造、异常包装）。
   - `ProtocolMatchConfig` 扩展 `sleeveBloomConfig`，与千术经济（费用显式为10）校验联动。
   - `ProtocolAccessCapabilities` 扩展 `sleeveBloomSupported`（缺失缺省为 false）。
   - `ProtocolPlayerSnapshot` 扩展 `sleeveBloom: ProtocolSleeveBloomView?` 及交叉校验。
   - `SessionApi` 新增 `submitSleeveBloom` 与 `submitSleeveBloomChoice` 接口。
2. 服务端（server）：
   - `EngineProtocolMapper`：领域与协议 DTO 双向完整转换。
   - `ServerTableHost`：同桌串行队列增加 `submitSleeveBloom` 与 `submitSleeveBloomChoice`，严格 sessionValidator、三连拒、无事件完整快照推送、时钟保持与幂等回执缓存。
   - `SharedRoom`：增加对应方法转发给 `tableHost`。
   - `InProcessBinding`：实现本地调用直通，观战者拒绝。
   - `UdpRoom` 与 `UdpTableServer`：分发处理，观战黑名单拒绝伪造。
   - 能力集声明 `sleeveBloomSupported = true`。
3. 客户端（client）：
   - 独立客户端类型：`SleeveBloomConfig`、`SleeveBloomDrawKind`、`SleeveBloomKeep`、`SleeveBloomRequest`、`SleeveBloomChoiceRequest`、`SleeveBloomOfferView`、`SleeveBloomView`。
   - `MatchConfig` 与 `Capabilities` 扩展字段。
   - `GameSnapshotView` 扩展 `sleeveBloom: SleeveBloomView?` 与深拷贝。
   - `ProtocolViewMapper` 扩展视图映射与深拷贝。
   - `GameSession` 接口增加 `submitSleeveBloom` 与 `submitSleeveBloomChoice`。
   - `ServerConnectionSession`：共用在途请求锁（`ACTION_IN_FLIGHT`）、绑定代次检查、关闭/异常安全释放。
   - `UdpClientBinding`：实现网络报文发送。

## 进度追踪
1. [x] 开工与进度文档初始化
2. [x] 协议层 (protocol) DTO、ApiTags、信封、TypeAdapter、快照与能力集实现
3. [x] 服务端 (server) EngineProtocolMapper、ServerTableHost、SharedRoom、InProcessBinding、UdpTableServer、UdpRoom 实现
4. [x] 客户端 (client) 客户端类型、视图模型、ProtocolViewMapper、GameSession、ServerConnectionSession、UdpClientBinding 实现
5. [x] 模块测试与专项测试验证 (`.\gradlew.bat :protocol:test :server:test :client:test --console=plain --no-build-cache`)：全部通过
6. [x] 全量测试验证 (`.\gradlew.bat test --console=plain --no-build-cache --rerun-tasks`)：1096 项全部通过，0 failure, 0 error, 0 skipped
7. [x] 交付文档与回调文件编写 (`DELIVERY-ANTI-E2.md`, `CALLBACK-ANTI-E2.json`, stdout `CODEX_CALLBACK SB-E2 ready_for_review`)

## 测试夹具与测试发现反馈（给 Codex）
- Codex 准备的测试夹具与假实现（`FakeServerConnection`、`FakeGameSession`、16项 E2 专项测试）完全正确，未发现夹具缺陷。
- 专项测试 16 项（协议 7 项、客户端 4 项、服务端串行宿主 2 项、本地进程内 1 项、真实 UDP 端到端 1 项、观战伪造拒绝 1 项）全部一次性通过。
- 模块测试及全量回归测试无任何失败或跳过。
