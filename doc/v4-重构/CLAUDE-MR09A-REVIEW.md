# MR-09a 验收记录：立直/一发双份状态治理与字段显式清单

- 验收日期：2026-10-18
- 验收方：Claude Code 主会话（独立检视与运行全部测试；不采信交付方自述）
- 开发方：Anti（agent-mesh-bridge 线程 `thread_20261018_151306_45de`，共 5 轮）
- 基线 SHA：`a36de58`（feat/mj-tcp-migration），基线 `:engine:test --rerun` 实测 470 项 0 失败
- 任务简报：`tools/architecture/v4-improvement/ANTI-MR09A-TASK.txt`
- 交付说明：`doc/v4-重构/ANTI-MR09A-DELIVERY.md`（本记录对其代码性断言逐项独立复核）

## 1. 验收结论：通过（R09a-T1～T7 全部满足）

| 标准 | 验收方实测证据 |
|---|---|
| T1 唯一写入口 | 8 处写点迁移逐行比对简报一致；`Mr09RiichiWriteFunnelScanTest` 通过且经变异验证：向 LegalActionProvider 注入 `s.ippatsuActive[0] = false` 后套件 BUILD FAILED，还原后 BUILD SUCCESSFUL（探针已还原，工作区无残留） |
| T2 不变量 | `RiichiMirrorInvariantTest` 6 场景（真实立直流/双立直/碰断一发/暗杠断一发/新局重置/深拷贝往返/入口单测含 seat 越界拒斥）全部通过 |
| T3 行为等价 | `:engine:test` 480/480（基线 470 + 新增 10）；既有立直流、闭杠清一发（WinContextIntegrationContractTest）、振听、全自动两局对局全绿 |
| T4 字段清单 | `GameStateFieldChecklistTest` 通过：反射 40 字段双向归类（32 RESET + 8 PRESERVED），未分类新字段即失败；重置语义与保留语义逐字段断言 |
| T5 复制管线 | copyFrom/deepCopy 往返 + 镜像不变量保持，含 PlayerHand.deepCopy 镜像传播 |
| T6 扫描套件 | `-I v4-review -PreviewOnly=true` 全套件 **108/108**（含新扫描测试） |
| T7 零外溢 | git 对照基线：3 改（engine 生产）+ 4 新建（2 测试/1 扫描/1 交付文档）+ 12 删（特批）+ 1 改（验收方自有，见 §3）；跨模块零改动，protocol/client/server/presentation 测试全部无需变更且全绿（166/183/214/304，0 失败） |

## 2. 逐轮问题与裁定（全部由验收方实测发现并要求返修）

| 轮次 | 问题 | 根因 | 处理 |
|---|---|---|---|
| 1 | `GameStateFieldChecklistTest` 编译失败：幻觉 API `tilePool.getZone()`（3 处） | 未先读 TilePool 真实 API | 逐字改 `getLocation(tile)?.zone`，复验 0 残留 |
| 1 | `-I` 套件整体编译失败 | **存量缺陷（非本 MR 引入）**：12 个 MR-02/UDP 时代文件引用 T5（07d6aae）已删除类；T5 审计漏 tools/ 目录 | 逐文件核实 12 个全部为 UDP 死引用后特批删除；TCP 等价覆盖在标准套件 |
| 3 | 4 个场景失败：断言读过期 fixture | `ApplicationCoordinator.kt:118` 对 `initialState.deepCopy()` 执行动作，`readState()` 才是正确读方 | 按 WinContextIntegrationContractTest:26/52/56 范式整体替换四场景 |
| 4 | 场景 1 失败 `expected 13 but was 12` | 过度断言：响应窗口全 Pass 后 `RoundTransitionPlanner.kt:368` 自动推进巡目；`riichiTurn` 语义是宣言巡目 | 改用 `riichiEvent.turnNumber` 对照（事件首字段，GameEvent.kt:54） |
| 5 | — | — | 480/480 通过，验收关闭 |

## 3. 验收方自有改动（不属于 Anti 交付）

`tools/architecture/v4-review/V4AuthorityBoundariesTest.kt`：原扫描 T5 已删除的 `server/.../udp` 目录抛 NoSuchFileException（同为 T5 遗留套件破损）。按其原意改为扫描现存两种绑定目录（inprocess + tcp），目录缺失视为失败而非豁免。修复后全套件 108/108。

## 4. 对交付文档的两处更正（不影响验收结论）

1. §3.4 读方审计表中 `doubleRiichiDeclared` 的读方列写了 "PublicTableProjection / PlayerView / 各绝技 ProjectionFactory"——经全库 grep 核实这些位置读取的是 `riichiDeclared`；`doubleRiichiDeclared` 的生产读方仅 `RiichiRuleEngine.kt:92`。
2. §7 示例命令 `gradlew test -I tools/architecture/v4-review/init.gradle.kts` 文件名与用法有误，正确命令见 §5。

## 5. 验收命令与结果（绑定当前工作区状态）

```
./gradlew :engine:test --console=plain                # 480 tests, 0 failures
./gradlew :protocol:test :client:test :server:test :presentation:test
                                                       # 166/183/214/304, 0 failures
./gradlew -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --console=plain
                                                       # 108 tests, 0 failures
```

## 6. 残留事项与后续

1. 镜像三字段仍为 `var`（测试夹具合法直写）；编译级强制收敛留给 D-06（BoardState/SeatState 拆分）或后续把 server/presentation 夹具迁至入口后改 `private set`。
2. `WinningScoreEvaluator.prepareEvaluationHand` 的镜像传播（L331-333）与 `RoundTransitionPlanner` L551 `hand.riichiDeclared` 读按计划保留为记录在案的例外/读方，行为由 INV1 保证等价。
3. **MR-09b（待派发）**：storedReceipts 每场条目数/拒绝占比/保留字节测量 + 预览重复计算测量（计划第 3、5 条），测量数据落地后再决定是否需要准入配额（第 4 条）。
4. 扫描套件自 T5 起不可编译的存量问题本次已根治；后续 MR 的 `-I` 阶段出口命令恢复可用。
