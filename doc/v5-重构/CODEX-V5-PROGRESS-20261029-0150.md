# V5 进度检查与待审交付分流

2026-10-29，Asia/Shanghai。G1 APPROVED；G2–G5 未批准；V4 仍为生产版本；181 classified 继续属于未覆盖分类。

## 基线、任务与环境

本轮 fetch 的 origin/v5 为 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7；PR19 已合并，不重开或复用。唯一当前后续批次 PR20 为 feat/v5-post-g1-batch → v5，检查时远端头 02f22e30842544812eb5bd3e24dd63bd2023d7ee。既有 LocalHost 子批次 PR21 头 865ca0a455bda625c660c6b7e1df049f88d4591e，仍以 PR20 源分支为目标；不创建重复 PR。

全局 recent tasks 仅查询一次（最近100条）；检查时活动 DeepSeek 1/2（thread_20261029_011900_a9fd，LocalHost 95b4754 汇合门禁），GLM 0/1，Anti 0/1，总1/4。两项 GLM 待审：thread_20261028_223624_e4ae（P4.2-B 准备8878bd4）与 thread_20261027_230302_93fb（持久宿主5f365d1）。失败的旧 P4.1 已由 a/b/c/d 承接，不恢复或复制。已完成的审查输入和组件具有各自 MR 登记，不将 ledger completed 等同最终产品接受。

模型发现：DeepSeek 默认 deepseek/deepseek-v4.1-flash；Claude Code 默认 glm-5.3-flash；Anti 默认 gemini-3.8-flash-high。mesh 返回 Anti exhausted / 429，恢复时间 2026-11-01T16:54:27.999Z（上海11月2日00:54）。这是 bridge 携带的限流状态，不是本轮新发模型调用获得429；Anti 席位 BLOCKED，不以额外 DeepSeek/GLM 替代。

caller 和新的只读审查工作树均先从 origin/v5 创建，再 fast-forward 到上述已接受 PR20 头：

- D:/majiang-wt/codex-progress-20261029-0150 / wt/v5-codex-progress-20261029-0150，仅本进度记录。
- D:/majiang-wt/p54b-review-ds-20261029-0150 / wt/v5-p54b-review-ds-20261029-0150，仅持久宿主独立审查报告；候选源代码只在 ignored 独占快照中运行。

均核实 HEAD、祖先、分支和 clean status。主目录旧集成分支及 CLAUDE.md、PROMPTS.md、两份未跟踪文档保留；没有改 receiver 工作树或用户 Unity 资源。

## P4.2-B 准备裁决与范围登记

沿用已接受的[独立审查输入](V5-P4.2-B-PREP-INDEPENDENT-REVIEW-INPUT-20261029.md)及其 caller 实测证据。该输入已完成、MR_RECORDED；产品候选 8878bd4eaf171c19dc83adf6270aa579120337c4 尚未接受。caller 采用其已确认缺陷作一次批量 REWORK，不重复派实现线程。

完整清单10项（3 MAJOR、7 MINOR）仍以原编号追踪：D-01 路由重入 owner 门、D-03 成功/重入/quarantine/销毁回归、D-05 quarantine 终态被吞，属于必须修复的3项 MAJOR；D-10 晚 Initialize 为 MINOR 代码项。D-06 默认 diff-check、D-07 完整证据哈希、D-09 provenance 为证据/流程修正。D-04 是原 PlayMode 130/131 的 URP/TMP 导入环境差异，不归因本准备代码，不要求越界修资源；D-02 同场景公开 API 风险不冒充已证实 UI 路径。本轮允许在 pending open 期间明确拒绝同场景 reload，或用导航代次取消旧 open，两者须有公开入口回归。

D-08 的 caller 范围登记：原始派单明确授权 IAppNavigation 异步能力、Lobby 入口与 AppRouter 生命周期适配；现明确保留候选9个实际路径，包含 LocalGameStartTests.cs 及 meta、V5-P4.2-B-UNITY-LOCALHOST-PREP-DELIVERY.md。允许在原 expectedFiles 的 PlayMode 路径增加4类有意义回归及按需新增 LocalHostCompositionPlayModeTests.cs/meta；不授权改 Settlement/URP/TMP、Domain/LocalHost 或其他并行任务文件。该登记只确认准备范围，不接受未修代码或激活真实工厂，不回改历史设计文档。

原有证据已由前轮 caller 在精确候选快照复跑：EditMode独立探针5/1/4/0 exit2，公开导航PlayMode探针2/1/1/0 exit2。真实 BackToMenu→OpenLobby 取消有效；公开 LoadAsync(Lobby) 晚完成进入 Table 是 API 风险。现候选SHA未变，本轮不无理由重复这些运行。返修须保持原始失败、一次修全全部已确认项，原会话原 GLM 路由；完整 LocalHost 尚未通过95b4754汇合门禁，因此此次只修准备代码，不提前生产激活。

## 持久宿主交付证据核验

候选 c0f16eb77c70296978f658dd5382b9288d556e05 → 5f365d1e27bfdc5c0eaa3b6cc5b2547ef6e86eb7，21路径，6474 insertions / 38 deletions；tracked clean，冻结范围默认 diff-check exit0。新恢复锚点、有限重试、TLS worker和runner属于本次审查对象；未接受、未发布任何候选实现。

本轮独立执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/progress-review/audit-receiver-evidence.ps1
```

cwd 为上述 caller 工作树。脚本只读 receiver R18 原始证据并写本树 ignored JSON，native exit0。R18 证据根 D:/majiang-wt/p54b-durable-host-glm-20261027/artifacts/p54b-durable-host-20261028-165554；manifest.sha256.json 的247/247文件 SHA256和长度匹配，manifest自身 SHA256=AA3F72B1B53193B3C3C7A61CEBDD3BEDE2232C8B11CB60C11D916AD090483562。

| 原生XML | total / passed | failed / skipped / errors / not-run | 逐例核验 |
|---|---|---|---|
| TableService | 152 / 152 | 0 / 0 / 0 / 0 | 152 Pass |
| TableRuntime | 400 / 400 | 0 / 0 / 0 / 0 | 400 Pass |
| Infrastructure | 35 / 35 | 0 / 0 / 0 / 0 | 35 Pass |
| DurableHost | 20 / 20 | 0 / 0 / 0 / 0 | 20 Pass |

607条汇总与逐例数一致。wrapper-summary.json实际27个阶段全为PASS，SHA256=62B7D59A18C19A64D4CADFB817D861F7D2ECC1A83E6F69EB9A00E45DAAD2AEF8；receiver摘要“28阶段”与此不同，留给审查报告精确解释，不能硬编码改计数。上述是对交付历史证据的核验，不是本轮新跑607项或新跑真实PG/WSS全链。

本树 build/progress-review/receiver-evidence-audit.json（SHA256=B32FEED9AD25190736E65B940BEFEBF79BD3A9EFF55B66906DABC241F4A77DF3）和 audit-receiver-evidence.ps1（SHA256=BF7634186441FAACC7E6BA5411C0CF4D9A6C6A80E25FE620CF28935EDD3A96C8）保存复核过程。准备第二个DeepSeek做本候选完整独立审查输入：新快照fresh构建、独占PG/WSS官方链、新OS进程强杀恢复、D-R1-01–04负例与源码/权限/回执/租约检查；不得修改产品或接管原GLM实现。新报告完成仍由caller最终裁决，原thread保持waiting_review，不绕过角色限制。

## 下一依赖与本轮边界

下发前准备状态：DeepSeek1/2，GLM0/1，Anti0/1。若两项异步调用成功，分配为DeepSeek2/2（现存LocalHost门禁+持久宿主审查）、GLM1/1（P4.2-B原线程返修）、Anti0/1（限流），总3/4；实际结果/thread ID以bridge调用返回为准，不在文档预填虚构ID。下发后结束回合，不轮询不变进度。

LocalHost全量候选95b4754仍待完整汇合门禁，伴随visibility黄金完整性是已记录阻断；不修改pin/verifier放行。P4.2-B真实工厂等待该候选正式接受，P4.5/G2等待完整本地链。持久宿主验收后才推进剩余HTTPS大厅签票、多客户端整场/对账的P5.4b/G3。P6等待G2+G3，P7等待G4，发布仍需最终批准。

本轮无新增远端CI、Unity/Player/Android/iOS/设备/跨物理网络/整场实跑，全部NOT_RUN；历史失败与后续构建证据独立保留。scheduler核实Running、PT30M、IgnoreNew，complete.flag不存在。仅将本进度文件通过既有PR20发布，不携带在途实现、不合并v5、不部署。
