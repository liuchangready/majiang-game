# E3 检视工作记录（尚未验收）

Codex 在 Anti 开发期间补充并维护独立测试，以下不代表生产交付已经通过。

- 新增 `TileMarkRequestIsolationTest`：不同座位使用同一 requestId 应各自成功，内部组 ID 和流水 ID 不能冲突。
- 扩展回放用例：修改 sessionId、roundId、decisionId、controlEpoch 任一项必须 REQUEST_ID_CONFLICT，不能只比较选牌集合。
- 新增下一局准备阶段用例：choice 复用此前落汗 ID 必须冲突，不能以 WRONG_PHASE 覆盖原成功回执。
- 20:57 的第一次权威层 XML：16 项、3 项失败。其中准备阶段失败属于 Codex 夹具错误：现有码牌流程必须等权威 deadline，即使四座位已确认也不会提前发牌。Codex 已修正测试，先验证确认时仍未发牌，再推进 ManualClock 到 preparation.deadlineMillis 验证真实发牌；不更改生产码牌时序。其余两项对应实际请求指纹/回执覆盖缺陷，须 Anti 修复生产实现。

当前落汗测试为 36 项（模型 9 + 状态 10 + 权威 16 + 请求隔离 1），必须同时运行并核对全量 engine。

Codex 修改后的 `TileMarkAuthorityContractTest.kt` SHA-256：D4D0ACBB03170D6636390655B279287B10282F4308DA9C66B7887AEC5FF59F04；`TileMarkRequestIsolationTest.kt`：4EF14C7DE81C3741993CAEDFF62A743C63E3C8C956859018FB8053EB6DCCF814。E1/E2 测试未改。

源码另有待最终复核项：新 coordinator.close 在订阅回调中可能尝试从已有 read lock 升级为 write lock；需避免重入死锁。收到交付后检查最终代码再决定回归用例与修复要求。
