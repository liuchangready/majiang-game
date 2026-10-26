# V5 进展复核与剩余任务交接（2026-10-26）

V4 仍为唯一生产基线。G1 BLOCKED，G5 未完成。主工作区停在历史 6c35736，CLAUDE.md 与 doc/ui/round-preparation/PROMPTS.md 的用户改动原样保留；本次只在隔离树 D:/majiang-wt/codex-progress-20261026-1010 操作。唯一发布入口仍为 PR19，feat/v5-1-integration → v5。

## 本次独立结果

出千边界交付 ba74567 已在本树从远端 85134e9 合入后独立构建和测试。随后原任务的合法 reviewer 已接受并推送 f3bfa45；本次 approve_task 因未持有该线程 reviewer role 被拒绝，未绕过角色检查、修改角色文件或重复派该任务。读取实际账本确认其 completed/synced 后，本树正常 merge 该远端头，保留 CODEX-V5-N3-CHEAT-RESIDUAL-ACCEPTANCE.md。最终相对远端代码/测试/工具无新增差异，只有 Anti 报告及本记录。

Anti 报告 2bb0dcf 通过独立静态复核，approve_task 成功。原 P4.1 草案 b43435d / thread_20261025_194659_8d8a 仍为 FAILED、未接受、未集成，不启动旧 native session。报告的 3 MAJOR + 1 MINOR 指向未来准备工作；没有实现 LocalHost。Caller 在集成副本修正三处：ITableChannel 是四方法加一个属性；PowerShell delimiter 使用 ([char]9)，cmdlet 不声称 native exit；完整 checkpoint 策略仍须处理不增加 stateVersion 的独立 AppendTimingAsync 写回。

| 检查 | 本次结果 |
|---|---|
| Release fresh build：Domain.Tests / TableRuntime.Tests | 各 exit0，0 warnings / 0 errors |
| fresh DLL direct runner，完整 Domain | 517 passed / 0 failed / 0 skipped / 0 errors，exit0 |
| fresh DLL direct runner，完整 TableRuntime | 375 passed / 0 failed / 0 skipped / 0 errors，exit0 |
| XML 精确核实新增边界类 | Domain 11、actor 2，全部 Pass，已包含在上述总数 |
| 独立静态脚本 | 53 checks PASS，exit0：四行 identity/kind/parent/histClass/notes SHA、原草案 blob/转码副本、恢复源码/检查点源码/P4.2 hash、报告计数、生产零改动 |
| catalog | PASS：6 fixtures / 73 requirements / 10389 cases / 45 Facts；full17 / partial21 / none32 / V5-only3 |

命令和原始证据在本树 ignored artifacts/progress-20261026-1010/：verify-boundaries.ps1、test-summary.json、两个 build/test 日志及 XML、static-review.ps1/json/log。前者先检查 fresh build 的 numeric exit，再执行 DLL；XML 要求非空、零失败/跳过/errors，并逐名确认新增类数量。静态检查是静态检查，不作为 LocalHost 冷恢复运行 PASS。

台账校验首次执行 FAIL（803 cascading failures）：新 worktree 缺少七份原审查 ignored 日志，三个已登记文档被 core.autocrlf 转为 CRLF 而与原始证据 hash 不同。原失败日志 ledger.log 保留。只从已接受集成树复制七份历史原件至本树 ignored 证据位置，三个文档恢复为 HEAD Git blob 的原始字节并逐一验证既有 pin 与文本等价；不刷新 pin，不修改 verifier，不把历史日志当新实跑。evidence-provisioning.json 记录来源与 hash。一次 Python 别名启动未产生 provisioning 文件，未作为成功，改由带 numeric exit 和强断言的 PowerShell 脚本实际完成 10 项 provisioning；中间失败运行保留。最终同一未修改校验器复验 exit0，24 CHECK-OK / 0 CHECK-FAIL，37 evidence pins 与14 adjudication digests全通过，实际向量仍411/175/6（ledger-final.log）。

## 最新已完成 CI 的实际证据

下载并检查了两个 run 的 summary、run-info、wrapper、原始框架日志和 PostgreSQL JSONL：

- push [40242886655](https://github.com/liuchangready/majiang-game/actions/runs/40242886655)，实际 checkout 85134e9ce4fd67556f6d2f355249c14f4d3b42d4。
- PR [40242890229](https://github.com/liuchangready/majiang-game/actions/runs/40242890229)，API headSha=85134e9，实际 checkout 是 PR merge SHA b2759f245f40c43c73739af4d6993794ee8987df，不能当作相同 SHA。

两者各 1767 passed / 0 failed / 0 skipped，build exit0、12 DLL scratch closure PASS、wrapper plannedExit0；PostgreSQL 16.10 loopback55460 实际身份与 rollback/concurrency 等事件存在，owned cluster deleted、环境恢复成功。这里仅确认已记录的 Lobby PG 合同，未推断 P5.2 事件存储已实现。新集成头 CI 不使用这些旧 SHA 的 PASS 代替。历史 39970580589 框架日志缺失与原因未明仍保留，不以最新 Client132/132 宣称其根因已修复。

## 本轮明确的后续裁决

原 f3bfa45 验收仅接受证据并保留四条 pending。本轮在其后，caller 基于上述独立检查，明确接受下列四项为 **classified（未覆盖、非 bound/full）** 的表示边界，留给 caller 在下一串行台账补丁中应用；共享台账仍由 caller 独占修改，不把该补丁重复派给 receiver。这项裁决本身不改现有台账，也不宣布门2/G1通过：

1. B1 mv-swap-drawn-with-wall-backfill note：V4 旧槽 backfill 是实体视图 alias；C# 唯一位置表不重复实体。C# 合法 discard29 → seat1 draw33 的指针/尾牌/实体守恒实测通过；V4 后续仍只为源码推导，完整双向 runtime NOT_RUN。分类仅豁免旧槽 alias 的物化表示，不豁免规则或136守恒。
2. B8 tm-capacity-tiles note：精确 V4 groups=2 且 marked12 的合成输入不能由 C# exactly-four group 表达。可达12+4拒绝零费用/状态变动、8+4成功已实测。保留原 note 的算术笔误及 SHA，不新增错误规则。
3. B8 tm-decision-mismatch note：无活跃局的 authority 输入不能通过要求 concrete RoundStateMachine 的 actor 构造；null-round 构造拒绝和已存在局但无 open decision 的拒绝有独立断言，彼此不替代。
4. B8 bm-activate-deadwall-empty note：V4 空 eligibility filter 不等于物理死山为空；C# 无该过滤输入且必须保留14实体，规范指针4下候选14/9的断言和13实体构造拒绝已实测。分类仅针对非规范过滤表示，不豁免可达空候选路径的零费用/零 offer。

每项保留 P1.4 + caller 责任与 2026-11-30 复访；新增相应生产表示/适配入口时重新验证，不重开全量 census。预期台账补丁只触及上述四个 identity，411 bound 不变，175→179 classified，6→2 pending；B6 isClaimed/isRiichi 两项仍依赖原 GLM 线程，不预先销账。四项分类生效还须补丁通过独立复核并集成。

另一独立任务可依据已接受 Anti 审计新增 P4.1 契约补充：完整 checkpoint + 独立 timing 原子写回、N-2 外部接线依赖、纯 .NET 工厂/所有权/取消/错误、P4.2 App 薄适配及路径。只写补充契约，不重复旧 FAILED 草案或实现生产代码，不提前依赖 N-2 未接受产物。

## 门槛与调度

N-2 thread_20261026_071845_d58f（DeepSeek Flash）与 AI thread_20261026_071845_5a2b（GLM Flash）保留原路由、原树返修。Anti 契约补充若派单成功，实际分配为 DeepSeek1/2、GLM1/1、Anti1/1，总数3/4；实际新线程号及 QUEUED/RUNNING 以 bridge 返回为准。第二个 DeepSeek 槽暂空：P3组件已经交付，N-2与AI范围已被占有；完整G1重审等待这两项返回，P4/P5生产实现等待G1；共享台账分类补丁归caller串行操作。没有新的、依赖已就绪且不重复的DeepSeek任务。不以第三个DeepSeek或第二个GLM填Anti槽，不恢复手动paused任务。

G1 的下一依赖是两项 N3 AI 行、四项分类补丁的接受与 N-2 宿主可信结算的接受，再对同一最终头做九门重审。P4.1/P4.2/P5.1/P5.2 实现继续受 G1 门槛约束。Unity/IL2CPP、Android/iOS 真机、MSVC/SDK/NDK/macOS/Xcode、跨物理网络及 G2–G5 本轮 NOT_RUN/BLOCKED，不拿 .NET/CI 结果代替。Windows scheduler 当前启用、PT30M、IgnoreNew；complete.flag 不存在。
