# 袖里藏花 E1 进度记录 (PROGRESS-ANTI-E1.md)

- 任务标识：SB-E1FIX1
- 日期：2026-10-12
- 责任人：Anti (agy)
- 状态：已完成 (ready_for_review)

## 进度追踪

### SB-E1 阶段
1. [x] 开工与进度初始化
2. [x] 冻结接口定义与模型创建 (`com.majiang.cheat.bloom`)
3. [x] 领域状态扩展 (`GameState`, `MatchConfig`, `CheatEconomyConfig`)
4. [x] 物理原语与守恒实现 (`TileMovement.swapDrawnWithRinshanReplacement`)
5. [x] 杠次数与岭上指针解耦及五杠门禁检查 (`RoundStateMachine`, `LegalActionProvider`, `RoundRuleService`, `RoundTransitionPlanner`)
6. [x] 权威与协调器接入 (`TableAuthority`, `ApplicationCoordinator`, 原子扣费, offer挂起, 幂等账)
7. [x] 决策重建、计时 rekey 与超时结算
8. [x] 私有投影工厂与快照校验 (`PlayerSnapshot`, `SleeveBloomProjectionFactory`)
9. [x] 役种算分与合法动作重算验证
10. [x] 等待 TESTS-READY-E1.md (就绪并已验证)
11. [x] 运行测试、修复问题、交付与回调

### SB-E1FIX1 阶段（Codex 退回检视整改）
1. [x] **修复项 1**：`TableAuthority.submitSleeveBloom` 在暂停期初次拒绝（`HOST_PAUSED`）入账到 `storedReceipts`，恢复后重放保持拒绝状态，杜绝意外扣费与激活。
2. [x] **修复项 2**：`TableAuthority.submitSleeveBloomChoice` 在暂停期初次拒绝（`HOST_PAUSED`）入账到 `storedReceipts`，恢复后重放保持拒绝状态；同时入账 `SLEEVE_BLOOM_DISABLED` 与 `SESSION_CLOSED` 等门禁拒绝；优先检查成功重放；会话作用域不匹配提前返回不污染账本。
3. [x] **修复项 3**：在 `SleeveBloomProjectionFactory`、`TableAuthority` 与 `ApplicationCoordinator` 锁内全面增加岭上与正常来源槽位一致性校验：
   - 岭上摸牌：校验 `drawnTile` 在玩家手中、`rinshanPointer in 1..4`、`deadWall[rinshanPointer - 1] == drawnTile`、`deadWallReplacements` 非空且末张在 `DEAD_WALL`；
   - 正常摸牌：校验 `drawnTile` 在玩家手中、`wallRemaining >= 1`、`wallPointer < wallEndExclusive`、次张在 `WALL`；
   - 校验失败在扣费与创建 offer 之前立即拒绝（`SLEEVE_BLOOM_WALL_EMPTY` / `ACTION_NOT_AVAILABLE`），杜绝扣费后选择时才暴露原槽失效。
4. [x] **修复项 4**：为 `TableAuthority.submitSleeveBloom` 与 `submitSleeveBloomChoice` 增加 `@Synchronized` 注解，与类内既有提交入口对齐同步边界。
5. [x] **修复项 5**：`ApplicationCoordinator` 构造函数接收并存储 `cheatEconomyConfig` 与 `sleeveBloomConfig`（并在 `updateLobbyConfig` 中同步更新）；`submitSleeveBloom` 读取 `cheatEconomyConfig.costs["SLEEVE_BLOOM"]` 并验证为 10；协调器锁内全量复核开关、经济、费用、控制器身份（真人）与候选归属；`submitSleeveBloomChoice` 在锁内完整复核 offer、轮次、候选归属与物理位置后再行交换；`TableAuthority` 在初始化与更新大厅配置时透传配置。
6. [x] 专项测试验证：`.\\gradlew.bat :engine:test --tests 'com.majiang.cheat.bloom.*' --console=plain` 退出码 0，30 项全部通过（3 失败项完全修复）。
7. [x] 完整引擎测试验证：`.\\gradlew.bat :engine:test --console=plain` 退出码 0，407 项 XML 全部通过，0 failures, 0 errors, 0 skipped。
8. [x] 更新 `CALLBACK-ANTI-E1.json`、`DELIVERY-ANTI-E1.md`、`PROGRESS-ANTI-E1.md`，输出 `CODEX_CALLBACK SB-E1FIX1 ready_for_review`。
