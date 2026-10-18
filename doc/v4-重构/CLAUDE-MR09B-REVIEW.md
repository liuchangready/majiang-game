# MR-09b 验收记录（CLAUDE 独立核定）

- 日期：2026-10-18
- 任务：storedReceipts 与预览重复计算测量探针（IMPROVEMENT-PLAN §4 MR-09 第 3、5 条）
- 开发：Anti（线程 thread_20261018_164718_822c，共 5 轮：1 交付 + 3 返修 + 批准）
- 基线：MR-09a 已验收未提交改动之上（文件零重叠，按文件隔离 diff 验收）
- 结论：**通过**。测量仪器零行为变更，三用例有效，数据足以支撑 MR-09 收尾决策。

## 1. 交付物与外溢核查

| 文件 | 类型 | 核定 |
|---|---|---|
| engine/.../authority/TableAuthority.kt | 修改（+59 行） | 仅 internal `ReceiptStoreMetrics` + `receiptStoreMetrics()` 只读快照 + 近似字节估算器；submit*/query* 逻辑零改动 ✓ |
| engine/.../session/PlayerPresentationPreviewProvider.kt | 修改（+48 行） | 仅内部计数器（LongAdder + 有界 8192 身份集合 + 冻结标志）+ createPreview 首行插桩；既有逻辑一行未动 ✓ |
| engine/src/test/.../Mr09ReceiptAndPreviewMeasurementTest.kt | 新建 | 三测量用例 ✓ |
| doc/v4-重构/ANTI-MR09B-DELIVERY.md | 新建 | 交付说明 ✓ |

MR-09a 的 6 个文件零触碰；未实现任何 LRU/配额/缓存（符合本任务边界）。

## 2. 验证结果（验收方实测）

- `:engine:compileTestKotlin`：通过
- `:engine:test` 全量：**483/483，0 失败**（MR-09a 后 480 + 新增 3）
- `-I v4-review -PreviewOnly` 全套件：**108/108，0 失败**（含 Mr09RiichiWriteFunnelScanTest，R09b-T5）
- spam 线性断言与 close 归零断言实测通过（R09b-T4）

## 3. 测量数据（MR09-MEAS 输出，验收方运行采集）

| 场景 | 条目 | 拒绝占比 | 构成 | 近似字节 | 对局完成度 | 预览调用 | 重复率 |
|---|---|---|---|---|---|---|---|
| match1（每次提交 1 次拉取/席） | 680 | 0.00% | recorded=120 / applied=560 | 204,712（~301B/条） | roundEnded=8，FINISHED，iterations=265 | 2,716 | duplicates=2,005（**74%**） |
| match2（每次提交 3 次拉取/席，重连模型） | 690 | 0.00% | recorded=130 / applied=560 | 207,732（~301B/条） | roundEnded=8，FINISHED，iterations=277 | 8,268 | duplicates=7,547（**91%**） |
| spam（500 唯一 requestId 非法请求） | ≥500（线性） | 100% | 全 REJECTED | 176,352（~352B/条） | — | — | — |

驱动模型：4×LocalHuman（满员人类房，真实最坏情形）、摸切优先→Pass→首弃牌策略、winds=1。四席条目分布均匀（[171,167,178,164]）。

## 4. 返修记录（3 轮）

1. **R1**：drive() 生命周期守卫跳过 start()（TableAuthority init 自动建局使初始 lifecycle=ROUND_ACTIVE）→ 无条件调用 start()；`isAutomated=true` 剥离 options（ServerTableHost 外部 AI 语义）→ 四席统一 false。两项中后者源于验收方简报表述误导，责任在简报。
2. **R2**：1+3Computer 模型下 Computer 席 submitChoice 被 `ApplicationCoordinator.submitChoice` L516-518 `isHuman()` 检查拒绝（ACTION_NOT_AVAILABLE ×20000 空转，applied=1、roundEnded=0）；自动席真实路径为 `submitAiChoice` 且不写 storedReceipts。改 4×LocalHuman 后对局完整打满（验收方以同构造临时诊断用例先行验证后下发逐字指令）。
3. **R3**：两处注释瑕疵清理（简报指示尾巴、空体 if）。

验收过程插曲：本机 `python` 为 WindowsApps 占位 stub，两次诊断补丁被静默吞掉导致一次基于陈旧 XML 的误判，改用 Edit 工具后纠正——已按"磁盘核对优先于报告"原则处理，未影响交付。

## 5. 对 MR-09 第 3/4/5 条的决策输入（验收方结论）

**条目量级（第 3 条）**：东风战一整场（8 局、满员人类）≈ 680-690 条 / ~205KB；半庄量级按比例 ≈ 1,400-2,700 条 / 0.4-0.8MB——合法对局的会话生命周期保留无容量压力，**无需为正常负载引入 LRU/淘汰**（符合"storedReceipts 不直接增加 LRU"的预判）。

**增长风险（第 4 条）**：唯一 requestId 线性增长实测坐实（~300-352B/条、仅 close() 释放、无准入上限）；恶意/失控客户端 100 万请求 ≈ 300MB+，构成真实 DoS 面。**建议立项 MR-09c 准入配额**（先查已有 ID、超额新 ID 业务执行前拒绝、已接纳条目保留至会话结束、配额值参考本数据取远超合法上限的量级如每席 8,192、单列兼容测试）；在配额落地前该风险以本记录为已知受控项。

**预览重复计算（第 5 条）**：即使单次拉取模型重复率已达 **74%**（同一决策身份在多次 querySnapshot 拉取间重复推导），重连式多次拉取达 **91%**——"证明重复计算"前置条件已满足，预览缓存具备立项依据；键设计必须遵守计划约束（覆盖决策身份、手牌/副露、弃牌史、立直/振听与规则上下文，不能只有手牌频次数组）。是否立项由用户裁定（候选 MR-09c 或并入 D 系列）。

## 6. 遗留与建议

- MR-09 主体（第 1、2 条）已由 MR-09a 完成并验收；第 3、5 条测量完成；第 4 条（配额）与缓存实现均未纳入本任务，依据 §5 数据单独立项。
- 全部 MR-09 改动（09a+09b）仍在工作区未提交，建议以独立 commit 分别记录 `mr09a`/`mr09b`（结构迁移与测量仪器分离，符合 §7 回退要求）。
