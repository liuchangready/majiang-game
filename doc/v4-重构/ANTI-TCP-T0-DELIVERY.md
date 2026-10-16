# T0 最大载荷测量工具开发交付文档 (ANTI-TCP-T0-DELIVERY.md)

- **开发方**: Anti
- **任务目标**: 修复并完善 T0 最大载荷测量工具 `tools/architecture/v4-improvement/server/TcpT0PayloadMeasurementTest.kt`
- **对应简报**: `tools/architecture/v4-improvement/ANTI-TCP-T0-TASK.txt`
- **基线**: `3a382c6`（分支 `refactor/remove-legacy-cheat-params`）
- **交付状态**: **测量工具代码就绪，等待 Claude Code 主会话运行与数据验收**

---

## 1. 修复与修改点详解

针对 Claude Code 主会话初版测试试跑中暴露的两个问题（码牌准备阶段卡死、正式阶段载荷未覆盖），本次开发严格按照简报确认的源码契约完成以下修复：

1. **码牌准备阶段自动推进（解决卡死根因）**：
   - 在循环遍历各席位时，检测到 `snap.preparation != null && snap.preparation?.selection == null` 时，该席位构造并提交 `ProtocolShufflePlan(requestId = "t0-plan-$seat-$moves", roundId = snap.roundId ?: "", controlEpoch = snap.decision.controlEpoch, selection = ProtocolShuffleSelection())`。
   - 提交成功返回 `RECORDED` 或 `APPLIED` 时，计入该席位的 `sideEffectSubmits[seat]` 与全局 `moves`，并设置 `acted = true`。
   - 若席位已提交（`prep.selection != null`），则跳过该席位等待发牌，不再重复提交。

2. **出千挂起 Offer 自动裁决（防止决策通道阻塞）**：
   - 检查 `snap.sleeveBloom?.pendingOffer != null`：自动提交 `ProtocolSleeveBloomChoiceRequest`（选择保留第一张 `ProtocolSleeveBloomKeep.FIRST`）。
   - 检查 `snap.beamSwap?.pendingOffer != null`：自动提交 `ProtocolBeamSwapChoiceRequest`（选择维持原牌 `ProtocolBeamSwapAction.KEEP_ORIGINAL`）。
   - 成功回执计入有效动作与副作用预算，避免因挂起决策导致后续常规出牌动作被 `SLEEVE_BLOOM_CHOICE_PENDING` / `BEAM_SWAP_CHOICE_PENDING` 拒绝。

3. **回执状态精确分流与预算计数审计**：
   - 仅当回执状态为 `ProtocolReceiptStatus.APPLIED` 或 `ProtocolReceiptStatus.RECORDED` 时，才递增 `moves` 与 `sideEffectSubmits[seat]`。
   - 被拒回执（`REJECTED`）独立计入 `rejectedSubmits`，在测试终末报告中打印审计，杜绝陈旧决策空刷预算假象。

4. **`REQ_envelope` 动态采样修复**：
   - 初版在开局前采样时，因处于准备阶段无决策选项（`decision.options` 为空），导致 `REQ_envelope` 从未被记录。
   - 现调整为在正式打牌阶段，每次由 `primaryOption(snap)` 选定合法动作（和牌 > 打牌 > Pass > 首个选项）时，构造完整 `ApiRequest` 并使用 `TcpMessageCodec.encodeRequestEnvelope` 实时编码并记录最大载荷。

5. **外部 Tick 驱动 TableHost 推进**：
   - 遵循架构规则：`InProcessServer` 不自建后台 Ticker 线程。
   - 在测试循环内显式调用 `server.tick().get(500, TimeUnit.MILLISECONDS)`，驱动权威 TableHost 的 `checkPreparationStep`（截止时间到期发牌）与超时/AI 调度，在等待时执行 `Thread.sleep(5)` 防止 CPU 空转。

6. **结算终态与完整快照补测**：
   - 检测到 `snap.settlementRoundId != null` 时标记 `settled = true` 并跳出循环。
   - 结算后通过 `connections[0].queryRoundResult(rid)` 记录 `RSP_roundResult`。
   - 补充结算后最新终态快照的 `RSP_snapshot`、`RSP_publicSnapshot` 及全量 `PUBLIC_UPDATE(full)` 测量，确保覆盖结算事件与积分结算明细。

7. **预期类型全覆盖与预算断言**：
   - 显式断言 9 种预期消息类型在 `stats.max` 中均已测量（`containsKey`）。
   - 断言各类型最大载荷不超过 `TcpWire.MAX_PAYLOAD_BYTES` (1 MiB)，且不超过 75% 水位线（留有 ≥25% 余量）。
   - 断言 `assertTrue(settled)` 保证整局真实打完。

---

## 2. 预期的 9 种测量点与覆盖清单

| 序号 | 测量类型标识 | 传输与消息形态 | 覆盖时机与内容 |
|---|---|---|---|
| 1 | `PLAYER_UPDATE` | PUSH 帧 (玩家私有流) | 对局中所有摸牌、打牌、鸣牌、出千状态变化的增量/全量私有推送 |
| 2 | `PUBLIC_UPDATE` | PUSH 帧 (观战公开流) | 对局中公开事件流推送（副露、舍牌、立直等） |
| 3 | `PUBLIC_UPDATE(full)` | PUSH 帧 (观战全量标定) | 将公开全量快照包装为 full PUBLIC_UPDATE（开局与结算终态两次标定） |
| 4 | `ROOM_UPDATE` | PUSH/广播帧 | 房间配置、就绪状态及生命周期阶段更新 |
| 5 | `RSP_snapshot` | RSP 响应帧 | 玩家私有全量快照（开局初始状态与局末结算状态两次采样） |
| 6 | `RSP_publicSnapshot` | RSP 响应帧 | 观战者公开全量快照（开局初始状态与局末结算状态两次采样） |
| 7 | `RSP_roomInfo` | RSP 响应帧 | 房间完整信息响应帧 |
| 8 | `RSP_roundResult` | RSP 响应帧 | 单局结算明细事件查询响应帧（局末终态触发） |
| 9 | `REQ_envelope` | REQ 请求信封 | 带有 `SUBMIT_ACTION` 选择载荷的真实 TCP 请求信封（整局动态采集） |

---

## 3. 执行约束与状态声明

- **未运行 Gradle（由 Claude Code 主会话执行）**。
- Anti 严格遵守约束，全程未执行任何 Gradle、git、shell 或外部命令。
- Anti 未调用任何外部代理，除被授权的 `TcpT0PayloadMeasurementTest.kt` 及进度/交付文档外未修改任何文件。
- 本交付文档不包含臆造的实测字节数；实际最大载荷字节数与每席位副作用提交数均由 Claude Code 主会话在运行独立验收套件后出具。

---

## 4. 后续项（供 Claude Code 主会话执行）

1. **运行测量测试**:
   - 执行 `TcpT0PayloadMeasurementTest.kt`（在主会话环境作为独立套件运行）。
   - 记录控制台输出的各消息类型 `max` 字节数与 `count`。
   - 记录 `每座位副作用提交（整局，含码牌/出千/打牌）` 的具体数值。
2. **复核检视结论**:
   - 验证最大载荷在 1 MiB 约束下的余量是否满足 ≥25%（设计 §4 / 检视报告 F14）。
   - 验证每席位提交数是否完全处于 UC-R06 预算（单局 512 / 席位，在途 64）的安全范围内。
   - 确认检视报告第四节剩余项（GET_REQUEST_SCOPE 限速复核等），正式关闭 T0 剩余项并推进 T2 派发。
