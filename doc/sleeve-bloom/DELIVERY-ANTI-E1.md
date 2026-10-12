# 袖里藏花 E1 交付报告 (DELIVERY-ANTI-E1.md)

- 任务标识：SB-E1FIX1 (Codex 退回检视整改)
- 提交人：Anti (agy)
- 检视人：Codex
- 日期：2026-10-12
- 状态：`ready_for_review`

---

## 1. 改动清单 (modifiedFiles)

本次改动严格遵循单向所有权与模块边界规范，仅修改 `engine/src/main` 生产代码与 `doc/sleeve-bloom/` 交付追踪文件，测试代码 (`src/test`) 保持只读，未修改其他模块代码，未执行任何 Git 提交：

1. `engine/src/main/kotlin/com/majiang/cheat/bloom/SleeveBloomModels.kt`
   - `SleeveBloomProjectionFactory.create`：强化候选来源槽位与物理位置审计：
     - 正常摸牌：核验摸牌属本人手牌、活牌山余量有效、`wall[wallPointer]` 真实存在于 `WALL`；
     - 岭上摸牌：核验摸牌属本人手牌、`rinshanPointer in 1..4`、`deadWall[rinshanPointer - 1] == drawnTile`（即摸牌真实匹配已消耗岭上槽位）、`deadWallReplacements` 非空且末尾替补牌归属 `DEAD_WALL`；
     - 任一来源失效时均直接判定 `canApply = false`，给出原因 `SLEEVE_BLOOM_WALL_EMPTY`。
2. `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`
   - 增加 `@Synchronized` 同步注解至 `submitSleeveBloom` 与 `submitSleeveBloomChoice`，与同类入口保持相同并发与同步边界；
   - 首次门禁拒绝入账：`HOST_PAUSED`、`SLEEVE_BLOOM_DISABLED` 与 `SESSION_CLOSED` 在作用域通过后入账 `storedReceipts`，恢复或重试后保持拒绝结果，防止意外扣费或执行；
   - 幂等重放优先：先查 `storedReceipts` 缓存，匹配直接返回历史回执（含暂停期间合法重放），异构返回 `REQUEST_ID_CONFLICT`；
   - 错误绑定/作用域（`seat !in 0..3`、`sessionId` 不匹配）提前返回，杜绝污染本局请求身份；
   - 在权威入口处复核摸牌所属及正常/岭上来源候选归属；
   - 实例化 `ApplicationCoordinator` 及 `updateLobbyConfig` 时透传 `cheatEconomy` 与 `sleeveBloomConfig`。
3. `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`
   - 构造参数及属性新增 `cheatEconomyConfig: CheatEconomyConfig` 与 `sleeveBloomConfig: SleeveBloomConfig`，并在 `updateLobbyConfig` 中同步更新；
   - `submitSleeveBloom` 读取 `cheatEconomyConfig.costs["SLEEVE_BLOOM"]`，校验开关、经济启用且费用精确为 10；
   - 协调器可重入写锁内全量复核控制器身份（`LocalHuman` / `RemoteHuman`）、控制代次 `controlEpoch`、当前玩家回合、阶段及未立直；
   - 锁内全面复核摸牌归属与候选槽位（`consumed rinshan slot == drawnTile`、替补非空且属 `DEAD_WALL`、正常牌本次张属 `WALL`），校验失败在扣费与安装 offer 前立即拒绝（`ACTION_NOT_AVAILABLE`）；
   - `submitSleeveBloomChoice` 在写锁内全面复核控制器身份、代次、开关、offer 标识与轮次一致性、候选牌归属及物理位置，再行物理交换。

---

## 2. 检视退回项修复对照

| 编号 | 检视反馈项 | 修复方案与生效逻辑 | 对应回归测试 |
|---|---|---|---|
| 1 | 暂停期间激活被拒后，恢复时不应扣费或成功 | `TableAuthority.submitSleeveBloom` 在 `decisionClock.isPausedState` 时创建 `ActionReceipt(HOST_PAUSED)` 并入账 `storedReceipts`。恢复后重放相同请求 ID 直接返回缓存的拒绝回执，不扣费、不产生 offer。 | `activation rejected during pause remains rejected after resume` (PASSED) |
| 2 | 暂停期间选择被拒后，恢复时不应在同请求 ID 下执行 | `TableAuthority.submitSleeveBloomChoice` 将 `HOST_PAUSED`（以及禁用/关闭门禁）入账 `storedReceipts`。重放先于暂停判断，恢复后同请求 ID 维持原拒绝回执，不发生物理换牌。 | `choice rejected during pause cannot execute later under the same request id` (PASSED) |
| 3 | 岭上来源原槽不匹配时在扣费前拒绝 | 投影工厂、权威入口与协调器锁内均校验 `consumed rinshan slot == drawnTile`、`rinshanPointer in 1..4`、`deadWallReplacements` 非空且属 `DEAD_WALL`。正常摸牌亦校验候选归属。来源失效时在扣费、安装 offer、物理交换前直接拒绝。 | `rinshan source with mismatched original slot rejects before charging` (PASSED) |
| 4 | TableAuthority 新入口缺少 `@Synchronized` | 为 `submitSleeveBloom` 与 `submitSleeveBloomChoice` 添加 `@Synchronized`，保证幂等、状态及 rekey 的原子性。 | 静态检视 & 并发一致性通过 |
| 5 | ApplicationCoordinator 硬编码 cost = 10，缺少锁内完备复核 | `ApplicationCoordinator` 构造函数与大厅更新接入权威透传的 `CheatEconomyConfig` 与 `SleeveBloomConfig`，读取 `costs["SLEEVE_BLOOM"]`；写锁内完整复核开关、经济、费用、控制器身份、代次及候选状态；选择在锁内复核 offer 轮次与物理位置。 | 静态检视 & 全量测试通过 |

---

## 3. 运行与验证命令及真实 XML 测试计数 (tests)

### 3.1 专项测试 (com.majiang.cheat.bloom.*)
```powershell
.\gradlew.bat :engine:test --tests 'com.majiang.cheat.bloom.*' --console=plain
```
- **退出码**：`0` (BUILD SUCCESSFUL)
- **实际 XML 测试统计**：
  - `TEST-com.majiang.cheat.bloom.SleeveBloomContractTest.xml`：21 项，0 失败，0 错误，0 跳过
  - `TEST-com.majiang.cheat.bloom.SleeveBloomWallAndRulesTest.xml`：9 项，0 失败，0 错误，0 跳过
  - **专项合计**：`30` 项，`0` 失败，`0` 错误，`0` 跳过

### 3.2 完整 engine 测试套件
```powershell
.\gradlew.bat :engine:test --console=plain
```
- **退出码**：`0` (BUILD SUCCESSFUL)
- **实际 XML 测试统计** (`engine/build/test-results/test/TEST-*.xml`)：
  - 总测试数 (tests)：`407`
  - 失败数 (failures)：`0`
  - 错误数 (errors)：`0`
  - 跳过数 (skipped)：`0`

---

## 4. 遗留项 (remainingIssues)

- 遗留项列表：`[]` (无未决阻碍或遗留缺陷)
- 状态说明：本阶段整改已全面完成并通过全量验证，现提交由 Codex 再次检视。本报告不作最终验收声明。
