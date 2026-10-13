# SB-KAN-DORA 交付文档：雀魂式杠宝牌时机与袖里藏花联动 (Revision 2)

- **任务代号**：SB-KAN-DORA
- **修订版本**：Revision 2（已根据 `FEEDBACK-KAN-DORA-REVIEW1.md` 首轮反馈完成全部修正）
- **日期**：2026-10-13
- **开发代理**：Anti
- **检视与测试代理**：Codex (独占测试开发与独立检视验收)
- **状态**：`ready_for_review`

---

## 1. 契约实现概要

依据 [TASK-ANTI-KAN-DORA.md](TASK-ANTI-KAN-DORA.md) 规则契约，本项目在现有 sleeve-bloom 基础上严格修正杠宝牌翻开时机，实现雀魂/天凤式杠宝牌时机与袖里藏花联动：

1. **暗杠立即翻宝**：
   - 暗杠（`MeldType.CLOSED_KAN`）成立后立即翻开本次杠宝牌指示牌（`revealNewDoraOnCandidate`），再摸岭上牌。
   - 岭上自摸和牌直接计入本次新杠宝牌；若立直和牌，结算时翻开对应的杠里宝牌（立直和牌才有里宝，非立直和牌无里宝）。
2. **大明杠与加杠延迟翻宝**：
   - 大明杠（`MeldType.OPEN_KAN`）和加杠（`MeldType.ADDED_KAN`）成立后先补摸岭上牌，本次杠宝牌指示牌标记为待翻状态（`candidate.pendingKanDoraCount++`），延迟到该玩家打牌时翻开。
   - 弃牌响应（他人荣和等）与算分前，杠宝牌已经生效并翻开（`stepEvents.add(DoraRevealed)`），不等待弃牌无人荣和才翻。
   - 大明杠/加杠补牌后直接岭上自摸，本次尚未翻开的杠宝不计入和牌算分；若立直和牌，里宝数量严格仅对应已翻表宝（立直和牌才有里宝）。
3. **加杠抢杠与连续杠**：
   - 加杠先开放抢杠响应窗口（`CHANKAN_WINDOW`）；若发生抢杠和牌，本次加杠不成立，不增加杠宝牌、不摸岭上牌。
   - 连续杠时，前一次已成立明杠的待翻指示牌，在后一次杠实际成立、下一次岭上摸牌前翻开；后一次若为暗杠立即再翻本次，若为明杠/加杠则留下本次待翻。
   - 后续加杠被抢杠时，不得提前揭示前一次仍待翻的指示牌。
4. **单局状态管理与物理守恒审计**：
   - `GameState` 新增 `pendingKanDoraCount: Int` 字段，完整支持 `copyFrom`、`deepCopy`、`copyInto`、`resetForNewRound` 与事务回滚。
   - `StateInvariants.assertValid` 强审计：断言 `pendingKanDoraCount in 0..4`、`pendingKanDoraCount <= totalKanCount`、`doraIndicators.size in 0..5` 且 `doraIndicators.size + pendingKanDoraCount <= 5`。
   - 最多五张表宝，终局清除待翻状态（`buildRoundResult` 中置零）但不补翻，新局复位。
5. **袖里藏花（Sleeve Bloom）联动**：
   - 杠后第二候选仅限下一张未取岭上牌 `deadWall[rinshanPointer]`，选第二张时原牌写回下一岭上槽；不动普通牌山、替补列表或指针；第四杠补牌窗口禁止施术且不扣费。
   - sleeve-bloom 激活、FIRST/SECOND 选择、取消、拒绝与快照查询均不触发翻宝。
   - 超时先保持 FIRST 再默认弃牌，该弃牌正常触发待翻杠宝牌翻开；选择 SECOND 后岭上自摸和牌严格使用已翻表宝计算。
6. **快照与事件权威同步**：
   - 私有快照、公开投影、网络线路与 UI 视图均严格映射真实已翻开的 `doraIndicators`，无仅 UI 隐藏、实际算分提前生效的偏差。
   - 普通弃牌（`GameAction.DiscardTile`）与立直弃牌（`GameAction.DeclareRiichi`）均经严格审计处理待翻杠宝。

---

## 2. 变更文件清单

### 生产源码
- [`engine/src/main/kotlin/com/majiang/state/GameState.kt`](../../engine/src/main/kotlin/com/majiang/state/GameState.kt)：
  - 新增 `var pendingKanDoraCount: Int = 0` 字段；
  - `resetForNewRound()` 重置 `pendingKanDoraCount = 0`；
  - `copyFrom()` 深度复制 `pendingKanDoraCount`。
- [`engine/src/main/kotlin/com/majiang/state/StateInvariants.kt`](../../engine/src/main/kotlin/com/majiang/state/StateInvariants.kt)：
  - 增加对 `pendingKanDoraCount` 的范围与守恒审计（`in 0..4`、`<= totalKanCount`、`doraIndicators.size + pendingKanDoraCount <= 5`）。
- [`engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt`](../../engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt)：
  - 新增 `revealPendingKanDora(candidate, events)` 辅助方法；
  - 暗杠 `CLOSED_KAN`：先翻开前次待翻宝牌，再翻开本次暗杠宝牌，再摸岭上牌；
  - 大明杠 `executeOpenKanCommit` 与加杠 `commitAddedKanAndRinshan`：先翻开前次待翻宝牌，将本次杠宝牌记入 `pendingKanDoraCount`，再摸岭上牌；
  - 普通弃牌 `DiscardTile` 与立直弃牌 `DeclareRiichi`：打牌后、开启响应窗口前执行 `revealPendingKanDora`；
  - 终局结算 `buildRoundResult`：清除 `pendingKanDoraCount = 0`，不补翻；
  - 保留原有流局 when 中的 `else -> DrawReason.EXHAUSTIVE` 控制变更差异。

### 文档
- [`doc/RIICHI_RULES_DESIGN.md`](../RIICHI_RULES_DESIGN.md)：
  - 3.1 节 Mermaid 流程图更新为暗杠立即翻宝、加杠抢杠裁决、大明杠与加杠登记待翻、弃牌后响应前翻待翻的真实流向；
  - 5.3 节与 11.3 节详细规定翻宝时机，明确限定立直和牌才有里宝。
- [`doc/sleeve-bloom/DESIGN.md`](DESIGN.md)：
  - 首段明确本次 SB-KAN-DORA 与历史验收记录边界；
  - 新增「杠宝牌翻开时机与袖里藏花联动 (SB-KAN-DORA)」章节，明确限定立直和牌才有里宝。
- [`doc/sleeve-bloom/STATUS.md`](STATUS.md)：更新当前工作区改动与真实验证数据，列全 Codex 新增测试与分工。
- [`doc/sleeve-bloom/DELIVERY-ANTI-KAN-DORA.md`](DELIVERY-ANTI-KAN-DORA.md)：本交付文档。
- [`doc/sleeve-bloom/CALLBACK-ANTI-KAN-DORA.json`](CALLBACK-ANTI-KAN-DORA.json)：机器可读回调元数据（revision=2）。

---

## 3. 验证与测试结果

### 3.1 领域与专项测试
- 执行命令：`./gradlew :engine:test --tests '*SleeveBloom*'`
- 结果：**50 项通过，0 失败，0 错误，0 跳过**
  - `SleeveBloomContractTest`：21 项通过
  - `SleeveBloomKanDoraTest`（Codex 独占新增）：15 项通过
  - `SleeveBloomWallAndRulesTest`：14 项通过

### 3.2 全量回归与跨平台构建
- 执行命令：`./gradlew test :lwjgl3:classes :android:assembleDebug --rerun-tasks --no-build-cache`
- 结果：**BUILD SUCCESSFUL**，86 个任务全量重新执行通过。
- **单元测试全量结果**：**1131 项全部通过，0 失败，0 错误，0 跳过**
  - `client`：136 项通过
  - `engine`：427 项通过
  - `presentation`：268 项通过
  - `protocol`：158 项通过
  - `server`：142 项通过（包含 Codex 新增的 `SleeveBloomKanDoraHostTest`）
- **桌面后端编译**：`:lwjgl3:classes` SUCCESSFUL
- **移动后端编译**：`:android:assembleDebug` SUCCESSFUL

### 3.3 真实桌面 GL 冒烟测试
- **1280×720 视口冒烟**：
  ```bash
  ./gradlew -I tools/architecture/sleeve-bloom-smoke.init.gradle sleeveBloomSmoke
  ```
  结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=583`
- **960×540 移动视口冒烟**：
  ```bash
  ./gradlew -I tools/architecture/sleeve-bloom-smoke.init.gradle sleeveBloomSmoke -PsmokeWidth=960
  ```
  结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=568`

---

## 4. 未验证边界与已知限制

1. **Android 实体机触控输入**：已通过 `:android:assembleDebug` 编译生成 Debug APK，未在物理手机触摸屏上进行真机测试。
2. **高 DPI 桌面环境**：GL 冒烟在无缩放环境及 1280/960 视口下通过，未在 125%/150% 等 Windows 系统 DPI 缩放监视器上抓取物理截图。
3. **工作区既有保留项**：工作区中原有的 `doc/README.md`、`lwjgl3/build.gradle`、`SnapshotLauncher.kt`、`snapshot/`、`doc/beam-swap/` 改动按约定予以保留，不执行 Git 提交或推送。

---

## 5. 交付结论

SB-KAN-DORA 生产代码与相关设计文档已严格按契约和首轮检视反馈完成修正，全量 1131 项单元测试、双端构建及 GL 冒烟全部通过，无已知失败，现提交 Codex 进行独立复测与最终检视。
