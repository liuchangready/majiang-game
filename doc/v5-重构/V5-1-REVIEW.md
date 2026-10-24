# V5-1-REVIEW — 首次当前集成头阶段审查报告（G1 证据盘点，第 4 轮返修版）

> 本报告由实现侧审查代理产出，**不构成 G1 门槛批准**。caller 需独立复核全部证据锚点后另行判定。
> 依据 `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md:108`（下称"任务书"）："生成 `V5-1-REVIEW.md` 记录实际测试数、失败、设备与未覆盖范围。"

## 0. 审查元信息与修订记录

| 项 | 值 |
|---|---|
| 审查对象（唯一固定头） | `8ee22ebf5164f74db75e5a33e87900f5b87cd8bd`（PR19 head，分支 `feat/v5-1-integration`） |
| 审查工作树 | `D:\majiang-wt\g1-readiness`，分支 `wt/v5-g1-readiness`；第 3 轮采样 HEAD `51cc902dcbc46400476f4c753aa69774f94c4f4c` = 审查头 + 仅本报告文件（`git diff --stat 8ee22eb..HEAD`：1 file changed, 467 insertions，源码面与审查头逐字节一致；第 3 轮探针 A 段复核） |
| HEAD 采样语义（历史→当前分层，§12.1） | `98d784624fb5…fce4` = 第 2 轮探针采样 HEAD（返修前）；`51cc902dcbc4…f4c` = 第 2 轮交付 = 第 3 轮探针采样 HEAD（R3 固定采样探针，**历史证据**，§12.3）；`7c451c673220…941` = 第 3 轮交付 = **本轮（第 4 轮）探针采样 HEAD**（第 4 轮脚本对 HEAD 只记录数值并以审查不变量验证，不与硬编码 commit 比较，§12.2） |
| 审查日期 | 2026-10-25（第 1 轮 `98d7846`、第 2 轮 `51cc902`、第 3 轮 `7c451c6`；本版为第 4 轮返修） |
| 第 2 轮探针证据（历史，禁止覆盖/刷新） | `C:/Users/sncdc/AppData/Local/Temp/g1-review-r2-evidence/run-probes.ps1` 与 `probes.log`（SHA256 `3138e0b27e61e95a549018bc92ccf5ea360e4d33f154c69ead730bdba209c09a`，183 行，EXIT=0）；R3/R4 探针均对其做哈希不变校验（ASSERT-PASS，§12.3） |
| 第 3 轮探针证据（历史采样事实，禁止覆盖/刷新） | `C:/Users/sncdc/AppData/Local/Temp/g1-review-r3-evidence/run-probes-r3.ps1` 与 `probes-r3.log`（SHA256 `146c388923f6427d0527c1806c1c2913e8e78664606649475d394fbfb2b80fe7`，170 行，14 ASSERT-PASS / 0 ASSERT-FAIL，进程 exit 0）。**该包的 HEAD 断言绑定采样点 51cc902**——在后续交付 commit 上复制运行会按断言合同如实 exit 1（caller 独立复现证实，§0.3）；源码面断言与其余 13 项不受影响。历史 14 PASS/0 FAIL 保持原始事实；当前交付复核用第 4 轮脚本（§12.2） |
| 第 4 轮独立探针证据（当前交付可复现） | `C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence/run-probes-r4.ps1` 与 `probes-r4-9a0ca4c9e583.log`（GUID 独占日志；SHA256 `a2bf068f5d5164910903d0905cadc16ea38d5c44279a5307a0ba7269f7779bfc`，123 行，21 ASSERT-PASS / 0 ASSERT-FAIL，**数值进程 exit 0**；隔离负例 `negative-selftest-r4.ps1`→`negative-selftest-6258b41fec33.log`（SHA256 `fdb777f6f59682d2988b89dc5fe952c90be208cbbbbe713ffec5394afda9252f`）断言失败→**数值 exit 1**；§12.2） |
| caller 复核证据（只读引用） | 第 1 轮：`C:/Users/sncdc/AppData/Local/Temp/g1-caller-review-20261025/`（source-audit.log SHA `49DA45BB…AC48E`、audit.log `0BA76DF4…60D63`、catalog.log `E3F631D9…8F6D2`；本树 5 次定向测试 1,187/0/0，日志 SHA 见 §10）。第 2 轮：`C:/Users/sncdc/AppData/Local/Temp/g1-caller-round2-20261025/`（review-audit.ps1 SHA `8106B0A0489508F5E11C0AACD95311C2F5D3681121DAA4D16AE804FCDBC9F5E1`、review-audit-run.log SHA `9A0E1218F8AED56CB9F4E49BDC95EC307BF317CF64B6B5B5B89559E513EA2145`、catalog PASS exit0 log SHA `E3F631D97B0D9C2AF3B5084E4CB751BE0FCAE856D6F231FE8A950E98CF38F6D2`）。第 3 轮：`C:/Users/sncdc/AppData/Local/Temp/g1-caller-round3-20261025-045851/`（evidence-command-repro.log SHA `B8AB7DA9397C553D5679476D0BABCBC846E0AA16D9A59FB9601DCBDDA4C3D00C`：`$?` 布尔实证 True/False、R3 脚本在 7c451c6 上 HEAD 断言如实 exit 1 且其余断言全过；R2/R3 日志哈希未覆盖） |
| 本地统一 verify（caller 提供，本轮未重跑） | 第 3 轮 caller 实测（`tools/verify-v5.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe`，验证器自动避让已有目录、实际日志在 `…-045851-1`；summary.log SHA `86D9B383B639720936F1C693FBA4D15913B3A393F463958D6673F47D64F2BD0E`）：solution 编译 PASS exit 0、scratch 12-DLL PASS；Client 132/Domain 322/Ai 316/Contracts 104/TableRuntime 313/Wss 80 均 0 失败 0 跳过；Lobby 89=77+12（无本地 PG skip）；**总数 1,356 = 1,344 passed / 0 failed / 12 skipped；严格 verifier 整体 exit 1**——非全绿，如实分列（§5.7；与第 2 轮 caller 结果 1,344/0/12 + summary SHA `272112BBB8C9…CD2` 一致，环境缺口未变） |
| 结论 | **G1 不批准**。九门逐项：**7 PASS / 2 FAIL / 0 BLOCKED / 0 NOT_RUN**（§2，与九行一一对应） |
| 历史冻结审计 | 依赖矩阵 `437bd53`、黄金目录 `205506f`：引用其结论，不改写历史、不把已完成组件重列为未做 |

**范围声明**：本报告只读源码/既存 CI 产物 + 独占 TEMP 探针；唯一被修改的受管文件是本报告。不读取/不合入两个在途兄弟任务的未提交产物；不重复下发；V4 保持唯一生产基线。

### 0.1 第 2 轮返修逐组映射（R1-1..R1-5 → 本版落点）

| 组 | 第 1 轮缺陷 | 本版处理 | 落点 |
|---|---|---|---|
| R1-1 | 假发现"仅 Lobby 有装配级静态测试" | **撤回**。固定头实有 8 处依赖方向/冻结契约断言（§3.3 保护边清单）；门 3 改判 **PASS（现有守护）**，残余为精确白名单增强建议（I-5，非门）；S-1 重写（不拒绝 BCL/Protobuf/Npgsql，列统一发现纳管） | §3.3、§7 I-5、§9 S-1 |
| R1-2 | "已导出 10,382 用例经 validator 对齐"、门 2 PASS | **撤回**。validator 自述 never executes production code（机械校验）；catalog status=`evidence-index-not-acceptance`；G-028a 全序列未断言、G-003b structural-only、mv-swap-drawn-backfill 仅结构。门 2 改判 **FAIL**，五层拆分 + 残余责任单列（新缺陷 N-3） | §3.2、§7 N-3 |
| R1-3 | 动作差集漏 DealComplete/DeclareExhaustiveDraw；错标 P3.2/P3.3 职责；D-3/D-4 无出千/计分实际任务；DAG 缺入边 | **重写**。完整 13 动作映射表（§4.2）；事件按 协议 21=19+2（schema 冻结）/域内 16 内部事件/出千私有两类 分层（§4.3）；残余按任务书正名（P1.1/P1.3/P1.2/P1.4/P3.1 范围延伸）标"建议 G1 补齐子任务，尚未分配"（§7 N-1/N-2、§8）；已完成 P3.2=按流投影和快照恢复、P3.3=计时托管控制竞态 不改名（§4.5） | §4、§7、§8 |
| R1-4 | 门 4 计数矛盾（5/2/2 vs 实际行）；D-2 把建议变强制门；平台门自扩张 | **改正**。每门一个状态，总计 7/2/0/0 与九行一致（§2）；D-2 降级为 I-4（G1 原文只要求状态不含隐式权威，未要求特定 source-scan 守护）；门 9 按 P2.2 出口口径判（可用平台原始日志 + 缺口分列），Unity/IL2CPP 子项 BLOCKED 明示且无通过宣称 | §2、§3.4、§3.9、§7 I-4 |
| R1-5 | 锚点/格式错误（守恒混用、spectator 测试位置、meta 文件名、§6 断链、§12 占位符、§1 表述、缺陷缺复现/SHA） | **全部改正**。物理 136 守恒（TilePositionMap/PhysicalGoldenTests）与点棒/点数守恒（RoundStateMachine:742-750）分列（§3.5）；spectator 测试=`Mahjong.Client.Tests/IndependentReviewTests.cs:66`（§3.7）；meta=`dotnetTest.meta.txt`（§5.2）；探针统一为 §6 编号并给真实日志+SHA；§12 全部可复制命令无占位符；缺陷表每条含复现/证据 SHA | 全文 |

### 0.2 第 3 轮返修逐组映射（R1-3 遗留 / R1-5 遗留 → 本版落点）

| 组 | 第 2 轮遗留缺陷 | 本版处理 | 落点 |
|---|---|---|---|
| R1-3（遗留，MAJOR） | 第 2 轮重写 13 动作表时把 **AI/超时适配器 `MapAction` 误当玩家/协议入口**（"actor 协议命令入口仅通 2"由此而来；固定头玩家路径实为 `SubmitAsync(TableCommandEnvelope)` :175 → `ProcessCommandCoreAsync` :1039 → `_round.Execute(command.Command)` :1151 直达，不经过 MapAction，Tsumo/Discard 端到端有既存测试）；责任/DAG 不一致（§7 列 N-4 MAJOR 而 §8 关闭公式仅 N-1/N-2/N-3）；§9 缺关键残余的具体文件与测试 scope | **撤回误判，按四通道重写**：①域 `RoundCommand` 实际行为 / ②actor 玩家提交（envelope 直达 :1151，对 9 种域动作无 kind 白名单 + 守卫）/ ③AI·超时 `LegalAction` 适配（`MapAction` 仅 Pass/Discard 两分支，调用点仅 :1422（AI 返回）/:1611（超时 fallback）→`ApplyInternalCommandAsync` :1923-1931）/ ④协议→宿主适配（**P4.1 LocalHost 空壳 / P5.1 TableService 未开工，后阶段**）。三绝技独立逐类表（7 CommandKind→域入口→actor 零接线，§4.2.1）。N-1 按真实缺口重写为三子项（域缺 5 决策动作 N-1a + 出千执行层三段未接 N-1b + 自动适配 2/10 可选扩展 N-1c）；**N-4 改判阶段事实 SF-1**（正式条款：G1 九门原文无 AI 项 + P1.6 退出为组件级已接受 + 组合根属 P4.1/P5.1 宿主产物，§7.4）；负例矩阵只作用于真实新增入口、自动阶段权限保留（§7.2）；§8 关闭公式=N-3+N-1a/N-1b/N-2，SF-1 移出；§9.1 给 N-1a/N-1b/N-2/N-3 源码+测试文件 scope | §0、§2、§3.5、§4.1、§4.2、§4.2.1、§4.5、§6、§7.0/7.1/7.2/7.4、§8、§9.1、§10 |
| R1-5（遗留，MINOR） | §12 仍有占位路径（`sha256sum …/ci-push/…/summary.log` 的"…"）；裸运行 `run-probes.ps1` 只出 stdout 不生成日志（脚本无重定向；`ErrorActionPreference=Continue` 使断言失败不改变退出码）；§0 未区分采样 HEAD 与交付 HEAD | **全部改正**：§12.4 给 PowerShell 可直接运行的 CI 两 bundle 文件发现+`Get-FileHash` 完整命令（无省略路径）；§12.2 探针命令显式重定向 `> probes-r3.log 2>&1` 并记录 exit/SHA；R3 脚本改为显式 `ASSERT-PASS/ASSERT-FAIL` 令牌+失败计数器，**任一断言失败即 exit 1**（诊断脚本退出与断言 PASS 由退出码区分）；§0/§12.1 标明 `98d7846`=第 2 轮采样 HEAD、`51cc902`=第 2 轮交付=第 3 轮采样 HEAD；第 2 轮 `probes.log`（SHA `3138e0b2…c09a`）保留为历史证据、不得覆盖，第 3 轮脚本 I 段做哈希不变校验（ASSERT-PASS） | §0、§6、§12.1-12.4 |

### 0.3 第 4 轮返修逐组映射（R1-5 遗留-2 → 本版落点）

| 组 | 第 3 轮遗留缺陷（caller 第 3 轮独立复核新增，E=`g1-caller-round3-20261025-045851`，evidence-command-repro.log SHA `B8AB7DA9…D00C`；§12.2/R3 探针为第 3 轮新写命令，非生产缺陷） | 本版处理 | 落点 |
|---|---|---|---|
| R1-5（遗留-2，MINOR）§12.2 复现命令不可按声称结果复现 | 三点：① 命令块 `echo "PROBE-EXIT=$?"` 未声明 shell——PowerShell 中 `$?` 是布尔（caller 实证 exit0→`True`、exit1→`False`），与报告声称的数值 0/1 不符；② R3 脚本 `delivery HEAD is 51cc902` 硬编码断言把**历史采样点**写成当前可复制复现——交付前进到 `7c451c6` 后按 §12.2 复制运行必然 ASSERT-FAIL exit 1（caller 实证其余断言全过，HEAD 断言如实失败）；③ 命令块 `> probes-r3.log` 固定同名重定向，复制即覆盖 R3 历史日志，违反独占路径 | **a) 数值退出码语义**：§12.2 命令块改 PowerShell 原生写法——`powershell -File …` 为 native 调用，随后**立即** `$r4Exit = $LASTEXITCODE` 保存数值并输出；R4 脚本 A 段自证 `cmd /c exit 0`→`$LASTEXITCODE=0`、`cmd /c exit 1`→`=1`（日志 ：3-6，ASSERT-PASS×2），全程不使用 `$?`。**b) 历史采样与当前复验分离**：R3 固定采样探针（含 HEAD=51cc902 断言）整体标注**历史采样事实**（§0/§12.3），14 PASS/0 FAIL 与 SHA 保持原始历史事实；当前交付复核另写 `run-probes-r4.ps1`——HEAD 断言改为**审查不变量**（`git rev-parse HEAD` 仅记录 40-hex 数值；`git merge-base --is-ancestor 8ee22eb HEAD`=0；对冻结头 `git diff --name-only` 恰 1 文件且为 V5-1-REVIEW.md；`-- dotnet tests tools assets` 零变化），**不硬编码交付 commit、无循环**；MapAction/玩家路径 :1151/出千零接线/域六入口/端到端锚点/计分/AI/validator 断言强度全部保留、失败非零语义不变。历史采样点复现另附**完全隔离 TEMP worktree 命令**（§12.2 模板，本轮未执行，不触碰当前/兄弟工作区）。**c) 独占日志**：R4 日志用 GUID 文件名 `probes-r4-9a0ca4c9e583.log`，负例独立日志 `negative-selftest-6258b41fec33.log`；R4 脚本 I 段对 R2 **与 R3** 日志做哈希不变校验（ASSERT-PASS×2）；实测数值 exit：R4 主探针=0（21 PASS/0 FAIL）、负例=1。**负例语义证明**：隔离 TEMP `negative-selftest-r4.ps1`（故意失败断言）→ ASSERT-FAIL → 数值 exit 1——修正过程中发现并修复该负例脚本自身的作用域 bug（函数内裸 `$fail++` 创建局部副本，改 `$script:fail`），主探针族无此问题 | §0、§1、§5.7、§6、§12.1、§12.2、§12.3 |

## 1. 环境前置核对（实际执行）

- `git rev-parse HEAD` → `7c451c6732203a98738b2cc5fe40a4c4ad1cf941`（本轮采样时的实际 HEAD，由第 4 轮探针 B 段记录；交付 commit 在本轮返修后会再前进一次，审查不变量——冻结头祖先 + 仅报告差异——对任意后续交付 commit 均成立，采样语义见 §0/§12.1）；分支 `wt/v5-g1-readiness`；`git status --porcelain` 空；`git diff --check HEAD^ HEAD` exit 0；`git merge-base --is-ancestor 8ee22eb… HEAD` exit 0（第 4 轮探针 A/B 段）。
- 未触碰 `D:/majiang` 主 checkout 及其用户未提交文件；未 reset/覆盖任何兄弟工作树；未 push/建 PR/触发远端 CI。
- 文档阅读：`AGENTS.md`、`DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`V5-0-1-IMPLEMENTATION-TASKS.md`、`V5-0-GOLDEN-MANIFEST.md`、`UNITY-ENVIRONMENT.md` 全文通读用于口径；此外**大量源码文件与 CI 产物按证据需要阅读**（第 1 轮"读过且仅读过四文档"表述有误，特此更正；主 checkout 用户文件始终未动）。
- 本报告引用的 CI 产物位于 caller 下载基 `D:/majiang-wt/codex-progress-20261025/artifacts/progress-20261025/`（只读）。

## 2. G1 总结论

九门逐项状态（每门恰好一个状态；总数=行数）：

| # | 门条件（任务书 :108） | 状态 | 一句话依据 |
|---|---|---|---|
| 1 | `dotnet test` 独立全绿 | **PASS** | 双 CI run 1,356/0/0 逐 meta 亲读（§5.2）；caller 本树 5 项目 1,187/0/0 复证（§0 表） |
| 2 | 已导出黄金样本对齐率 100%，未导出样本单列 | **FAIL** | 机械目录校验 PASS ≠ 行为全字段对齐：G-028a 全序列未断言、G-003b/mv-swap-drawn-backfill 仅结构、20 partial 未对齐（§3.2 五层拆分，N-3） |
| 3 | 依赖方向静态测试通过 | **PASS** | 固定头 8 处静态断言存在且双 run 全绿（§3.3 保护边清单）；残余精确白名单为建议（I-5，非门） |
| 4 | Domain 不含隐式随机/时钟/浮点权威 | **PASS** | 严格 grep 零命中（探针 C 段 NO-MATCH）；守护测试为建议非门条款（I-4） |
| 5 | 三绝技与 136 守恒通过 | **FAIL** | 136 守恒组件级证据充分；三绝技：域转移面与玩家 envelope 通道在（Tsumo/Discard 端到端有既存测试），但**出千 7 协议命令执行层零接线**、域缺 5 类玩家决策动作（§3.5/§4.2/§4.2.1，N-1a/N-1b；N-2 结算未接线） |
| 6 | Wire 可重复生成 | **PASS** | 稳定字节/字段号/错误码稳定 Fact 群 + Contracts 104 全绿（§3.6） |
| 7 | 按座位/观战私有性通过 | **PASS** | 可见性 6 Fact + 观战命令前置拒绝（IndependentReviewTests.cs:66）+ 座位级 AI 观察（§3.7） |
| 8 | TableRuntime 并发、版本与恢复合同通过 | **PASS** | TableRuntime 313 全绿 + CI 恢复/租约/回执证据事件（§3.8） |
| 9 | Unity/IL2CPP 可用平台的构建日志和缺口分列 | **PASS**（按 P2.2 出口口径） | 可用平台（Windows .NET）有原始构建日志；不可用平台（Unity Editor/IL2CPP/NDK/iOS）缺口分列、标阻塞、无通过宣称（§3.9）；Unity/IL2CPP 子项状态=**BLOCKED** 明示 |

**计数核对：PASS=门 1,3,4,6,7,8,9（7 项）；FAIL=门 2,5（2 项）；BLOCKED=0；NOT_RUN=0；合计 9 = 行数。**

**G1 不批准**：门 2（黄金行为全字段对齐未证实）与门 5（三绝技权威链未通）未满足。在 N-1a/N-1b/N-2/N-3（§7）完成前保持 V4 唯一生产基线，不进入 V5-2/3 集成验收。SF-1（原 N-4，AI 组合根）为阶段事实，不在 G1 关闭公式（§7.4/§8）。本报告与第 1 轮"BLOCKED"结论在"不批准"上一致，但门级判定与计数以本版为准。

## 3. G1 门条件逐项证据

### 3.1 门 1 `dotnet test` 独立全绿 — PASS

两 CI run（§5.1）7 测试项目 meta（`dotnetTest.meta.txt`，每项目一份，共 14 份）全部 exit 0，合计 **1,356 passed / 0 failed / 0 skipped**：Ai 316、Client 132、Client.Wss 80、Contracts 104、Domain 322、Lobby 89、TableRuntime 313。解构建 exit 0、12-DLL 闭包 PASS、summary 9 行 OVERALL PASS（7 测试项目 + 2 个仅构建 helper，helper 未计入用例数）。caller 于本树（同源码面）5 次定向 `dotnet test` 复证 1,187/0/0 全 exit 0（含一次非标准输出路径失败后按受支持路径复验，详见 §10，不归因历史 39970580589、不称 flaky）。

### 3.2 门 2 黄金对齐（已导出 100% / 未导出单列）— FAIL

**五层拆分（不得混计）**：

| 层 | 事实 | 证据 |
|---|---|---|
| L1 机械目录校验 | PASS：validator 只查存在性/SHA/schema/计数/ID 去重/锚点/manifest 覆盖，**自述"never executes production code"**；本树复跑 PASS exit 0（R2 探针 H/O 段 + 第 3 轮探针 J 段复跑一致），catalog SHA `16F7FD7F…891B`，fixtures=5/requirements=73/cases=10,382/random=10,000/facts=42/coverage=full:17/partial:20/none:33/v5-only:3 | `tools/verify-v5-golden-catalog.ps1:9-11`；`tests/v5-golden/catalog-v1.json:4-5` |
| L2 目录自我定位 | catalog `status=evidence-index-not-acceptance`，notice 明示"does not assert G1/G5, V5 production readiness, or full 73-item coverage"——**10,382 是目录 case 数，不构成行为对齐分子** | catalog-v1.json:4-5（探针 H 段） |
| L3 真实 C# 行为差分（已存在） | 17 项 full 覆盖需求中含真实调用断言，如 PhysicalGoldenTests 逐项对照 C# 公开 API 且"所有 expected 都来自 V4 真实调用"、"不把 C# 实现当 oracle"；scoring/cheat/round 各黄金测试为真实 C# 执行 | `PhysicalGoldenTests.cs:15-27`（探针 H 段 :20-22） |
| L4 结构-only 与未断言字段边界 | ① G-003b V4 mutable 损坏注入探针在 C# 侧记 **structural**（C# 无可变容器，"没有把不可复现的 mutable 损坏注入写成 PASS"）；② `TileMovement_DrawnBackfill_IsAV4OnlyPath_RecordedAsStructure`（CheatGoldenTests.cs:644）：mv-swap-drawn-with-wall-backfill 为 V4-only 路径，C# `ChooseBeamSwap` 按设计拒绝摸牌实体，**仅断言黄金内部结构**；③ G-028a（manifest :343，`pending/partial`）：`RoundTerminalReplaysExactly`（RoundReplayGoldenTests.cs:101）断言 version/phase/result/scores/rejected+`TsumoWin` 在场（:134），**不断言 `MatchEnded` 缺席与完整 eventTypes 序列（ScoreSettled/RoundEnded）**；④ 20 项 partial 覆盖需求未对齐，33 项 none 单列 | manifest :343；三处测试源码（探针 H 段行号） |
| L5 未导出/无覆盖 | none:33 单列于目录 coverage 字段；v5-only:3 另列 | validator 输出（探针 O 段） |

**判定**：门 2 的"已导出黄金样本对齐率为 100%"必须指行为级全字段对齐；L4 所列边界（G-028a 序列、V4-only 路径、partial×20）使其当前**不可宣布**。**不批准该门**；残余逐项落 N-3（§7）。历史 P0.1 冻结审计（437bd53/205506f）的已接受组件结论不被本判定改写——本门判定针对"当前集成头可宣布的 G1 对齐口径"。

### 3.3 门 3 依赖方向静态测试 — PASS（现有守护；残余增强为建议）

第 1 轮"仅 Lobby 有装配级测试"为**假发现，撤回**。固定头实有 8 处（全部双 run 全绿，行号经探针 B 段复核）：

| 断言 | 位置 | 实际保护边 |
|---|---|---|
| `DomainDoesNotReferenceContractsWireProtobufUnityOrDatabase` | `dotnet/tests/Mahjong.Contracts.Tests/SchemaCompatibilityTests.cs:376` | Domain ✗ Contracts/Wire/Google.Protobuf/UnityEngine/System.Data |
| `ContractsDoesNotReferenceWireOrProtobuf` | 同文件 :389 | Contracts ✗ Wire/Protobuf |
| `WireReferencesContractsAndProtobuf` | 同文件 :399 | Wire ✓ Contracts + Google.Protobuf（正向） |
| `EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents` | 同文件 :409 | 协议事件面冻结 19+2（门 2/事件口径相关） |
| `DomainAssemblyDoesNotReferenceContractsUnityNetworkOrDatabase` | `dotnet/tests/Mahjong.Domain.Tests/TilePositionMapTests.cs:1049` | Domain ✗ Contracts/Client/Server/TableRuntime/LocalHost/UnityEngine/UnityEditor/System.Net/System.Data/Microsoft.Data（子串匹配，覆盖 Microsoft.Data.SqlClient 类） |
| Ai 程序集禁边（含观察暗牌属性扫描） | `dotnet/tests/Mahjong.Ai.Tests/AiDecisionTests.cs:437-452`（断言消息 :449） | Ai ✗ TableRuntime/Contracts/LocalHost/UnityEngine |
| `ClientAssemblyDoesNotReferenceDomainOrTableRuntime` | `dotnet/tests/Mahjong.Client.Tests/TableSessionTests.cs:749` | Client ✗ Domain/TableRuntime/LocalHost；✓ Contracts + Wire（正向） |
| `LobbyAssemblyDoesNotReferenceDomainOrTableRuntime` | `dotnet/tests/Mahjong.LobbyService.Tests/ArchitectureAndPolicyTests.cs:13` | Lobby ✗ Domain/TableRuntime |

**未保护边（残余，均为建议非门）**：TableRuntime 允许集（Domain/Ai/Contracts）无正向白名单断言；Wire ✗ Domain/Client 未显式断言；LocalHost ✗ 其余未断言；BCL 层面（如 Domain ✗ System.Net 已在 :1049 覆盖，其余程序集未逐一遍历）。→ I-5（§7）。

**门 3 判定**：G1 条款是"依赖方向静态测试通过"。现有 8 处断言覆盖核心禁边（Domain=∅依赖、Contracts✛Wire、Client✛Domain/TableRuntime、Ai✛TableRuntime/Contracts、Lobby✛Domain/TableRuntime）且全绿 → **PASS**。不能把"非完整白名单"偷换为"无测试"（第 1 轮错误），也不把白名单精确化升格为门条件。

### 3.4 门 4 Domain 无隐式随机/时钟/浮点权威 — PASS（现状；守护为建议）

严格 grep（`dotnet/src/Mahjong.Domain` 全部 .cs）：`new Random(|DateTime.Now|DateTime.UtcNow|DateTimeOffset.Now|DateTimeOffset.UtcNow|Guid.NewGuid|Stopwatch.|Environment.TickCount|Thread.Sleep|Task.Delay|Math.Random` → **NO-MATCH**（探针 C 段，可复现命令见 §12）。

边界情况（均非"隐式权威"，如实记录）：
1. `RoundRecovery.cs:735/790/876/893/1184/1242`、`RoundStateMachine.cs:1039/1063`：读取**调用方供给**的 `settlement.CompletedAtUtc`（`RoundTypes.cs:61-71` `PersistedUtcTime`）并哈希——时间为记录输入，非墙上时钟。
2. `Stacking/ShuffleConfig.cs`：`double` 仅作码牌调参，`"R"` 不变文化格式化（:97）；shuffle 确定性由 shuffle-v1（32 用例）与 P0.1 冻结跨 JVM/.NET LCG 差分覆盖。

**守护缺口定位**：任务书 G1 原文为状态要求（"Domain 不含隐式随机/时钟/浮点权威"），**未明文要求特定 source-scan 守护测试**；第 1 轮把"补 source-scan"当作 D-2 级 MAJOR 强制阻断属于自加门，**降级为建议 I-4**（§7）。当前状态 PASS 以本轮探针为证。

### 3.5 门 5 三绝技与 136 守恒 — FAIL（守恒证据充分；三绝技权威链未通）

**136 物理守恒（组件级证据充分，与点棒/点数守恒分列）**：
- 域层：`TilePositionMap` 为"exactly 136 physical tiles"的不可变位置表（`TilePositionMap.cs:184`），`TotalTileCount = 136`（:193）、构造期 136 槽强校验（:224）、`ConservationReport`/`ConservationException` 守恒审计（:125-180）；`PhysicalGoldenTests` 断言 `Assert.Equal(136, size)` 与 `Assert.Equal(136, distinctIdentities)`（`PhysicalGoldenTests.cs:104-105`）。
- 码牌/随机：shuffle-v1（32 用例）黄金差分；非法双占/重复拒绝在 P1.1 契约测试。
- （分列）**点棒/点数守恒**（非 136 物理守恒）：`RoundStateMachine.cs:742-750` 验证调用方结算的 score/stick 起终恒等，`:802-822` 验证结果一致性——此为**结算数值守恒**，不是 136 物理守恒的锚点（第 1 轮混用，更正）。

**三绝技（四通道分离核验；第 2 轮"13 动作 actor 仅通 2"系误判 `MapAction` 为玩家入口，本版撤回并更正）**：

- **通道①域权威**：`RoundStateMachine.Execute`（:294，dispatch :207-225）接受全部 9 种 `RoundActionKind` 域动作（RoundTypes.cs:31-42）；出千转移不在该枚举——出千经由 `CheatSubmission` 六个公开入口（`ApplyTileMark` :109、`ActivateSleeveBloom` :222、`ChooseSleeveBloom` :322、`ExpireSleeveBloomImmediate` :417、`ActivateOffTurnSleeveBloom` :442、`ChooseBeamSwap` :904）的独立转移面，需显式调用（探针 E/F 段）。
- **通道②actor 玩家路径**：`SubmitAsync(TableCommandEnvelope)`（`TableActor.cs:175`）→ `ProcessCommandCoreAsync`（:1039，守卫 `GuardServing` :1041、state version/control epoch 拒绝 :1140-1146）→ **`_round.Execute(command.Command)`（:1151）直达，不经过 MapAction**。envelope 仅承载域 `RoundCommand`（`TableRuntimeContracts.cs:134-142` 构造参数、:180 `Command` 属性），**无出千载荷字段**。端到端既存测试：`committed_tsumo_emits_typed_settlement_event_and_snapshot`（`TableStreamWireIntegrationTests.cs:715`，`RoundCommand.Tsumo` :747 → `SubmitAsync` :752 → `Accepted` :754 → Wire 事件可见）；`TerminalOutboxFaultRollsBackAndRetryWritesOnlyOneResult`（`TableDurableReceiptTests.cs:250`）；Discard 链路经 `P31Fixtures.BuildDiscardChain`（`TableRuntimeFixtures.cs:785`，`RoundCommand.Discard` :796 构造 envelope）贯穿数十个 `SubmitAsync` 用例（如 TableStreamWireIntegrationTests.cs:145-160）。
- **通道③AI/超时（自动化座位）**：`MapAction(LegalActionOption)`（`TableActor.cs:4819`）全仓仅 3 处出现——定义 :4819 与两处调用：AI 返回 :1422（null→保持 pending，:1423-1425）、超时 fallback :1611（null→`UNSUPPORTED_ACTION` :1623）→ `ApplyInternalCommandAsync`（:1923-1931）→ `_round.Execute`（:1931）。**仅映射 `Pass`（:4824）/`Discard`（:4827）两分支**。它是自动化座位适配器，**不是玩家/协议入口**。测试：`DeadlineBoundaryTimeoutWinsOverSameTickAi`（`TableActorTests.cs:182`）、`AiBeforeDeadlineIsAcceptedAndItsTileIsApplied`（:213，AI 取高牌/超时 fallback 取低牌的判别式 fixture `BuildDiscardDecision`）。
- **通道④协议→宿主适配**：`CommandKind` 27 种（含出千 7 命令）到 `RoundCommand`/出千命令的宿主映射属 **P4.1 LocalHost（空壳工程，G2 门内容）/ P5.1 TableService（未开工，V5-3）后阶段**（任务书 :114-119、:156-161）——阶段事实，非本头缺陷。
- **真实缺口**（→ N-1）：(a) 域缺 5 类玩家决策动作（Chi/Pon/Kan(claim)/Riichi/AgariYame，RoundTypes.cs:31-42 无对应值，Execute 无对应 case）；(b) 出千 7 命令（`SubmitTileMark=12`…`SubmitBeamSwapChoice=18`，ProtocolCommand.cs:21-27）在 actor/envelope 层**零接线**（探针 G 段：`SubmitTileMark|SubmitSleeveBloom|SubmitBeamSwap|CheatCommand|CheatSubmission` 在 `Mahjong.TableRuntime` src 零命中）；(c) 自动化座位适配仅覆盖 LegalActionKind 10 值中的 2 值（Pass/Discard）。

**判定**：136 守恒半项证据成立；三绝技半项"通过"要求资格/费用/窗口/等额物理移动在权威链端到端成立（任务书 P1.4 退出标准），当前出千命令执行层三段（envelope→actor 分发→CheatSubmission 调用）不通 → 门 5 **FAIL**。

### 3.6 门 6 Wire 可重复生成 — PASS

锚点（`dotnet/tests/Mahjong.Contracts.Tests/`）：`identical_projection_maps_to_stable_bytes`（ProjectionWireMapperTests.cs:12 起）、`EnvelopeFieldNumbersAreStable`、`CommandReceiptCursorAndSyncFieldNumbersAreStable`、`ErrorVocabularyIsStableWellFormedAndHasNoDuplicates`、`ApplicationCloseCodesMapToStableWebSocketCodes`、`EveryV4ApiErrorMapsToADistinctStableV5Code`、`RulesetAndProjectionMismatchesHaveDistinctStableCodes`、`StreamVersionMayRepeatWhileProjectionSeqAdvances`；`EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents`（SchemaCompatibilityTests.cs:409）冻结字段号 1..21。Contracts.Tests 104 双 run 全绿。

### 3.7 门 7 按座位/观战私有性 — PASS

- 投影层：`dotnet/tests/Mahjong.TableRuntime.Tests/TableVisibilityProjectionTests.cs:16-181` 六 Fact——`public_snapshot_uses_explicit_public_fields_only`、`player_snapshot_keeps_own_hand_and_draw_private`、`decision_is_visible_only_to_its_owner`、`private_events_reach_only_owner`、`public_events_reach_every_seat_and_spectator`、`settlement_events_gate_only_spectator`；另有 `different_hidden_states_produce_identical_public_snapshot`、`public_snapshot_does_not_expose_hidden_wall_or_hand_tiles`、`event_bodies_preserve_source_versions_and_redact_hidden_payload`。
- 会话层：`SpectatorMustRejectPlayerCommandBeforePersistenceAndSend` 位于 **`dotnet/tests/Mahjong.Client.Tests/IndependentReviewTests.cs:66`**（第 1 轮误标 TableStream 两文件，更正）：断言 `CountSent(Command)==0` 且 `store.SaveCount==0`——持久化与发送前双重拒绝。
- 协议策略层：`ProtocolEventPolicy.PrivateTypes`（ProtocolSnapshot.cs:69-78）显式 6 类私有（TileDealt/TileDrawn/RinshanDraw/CheatPointsChanged/SleeveBloomResolved/BeamSwapResolved），未知事件不静默当公有。
- AI 观察层：actor 座位级 `AiObservation`（`TableActor.cs:390-404, 4105-4200`）+ Ai 模型守卫（`AiObservation.cs:73-83` per-seat 契约、:318-321 上下文属主、:349 副露属主、:421 候选属主）。

### 3.8 门 8 TableRuntime 并发、版本与恢复合同 — PASS

TableRuntime.Tests 313 双 run 全绿；P3.4 回合探针在树（`CodexP34Round3..6ReviewProbes.cs`）；`TableDurableContracts.cs` state_version=命令版本 CAS、event_seq 逐事件、ownerEpoch+stateVersion 双栅栏、checkpoint 后重放校验；租约/恢复 `TableDurableLeaseRecoveryTests.cs`、`TableRecoveryService.cs`。CI 证据事件：`recovery.expiredReservation`、`receipt.replay`、`receipt.terminalReplay`、`concurrency.crossRoomJoin ×3`、`crossConnectionRowLock`、`transaction.rollback`、`ticketConsumption`（§5.4）。

### 3.9 门 9 Unity/IL2CPP 可用平台的构建日志和缺口分列 — PASS（按 P2.2 出口口径）

任务书 P2.2 出口原文："可用平台有原始构建日志，缺口有具体资源清单"；并明文授权"若缺 MSVC/SDK/NDK/iOS 设备，标阻塞并提供补验脚本，**不宣称通过**"。按此口径：

- **可用平台（Windows .NET，实际可用且已测）**：两 CI run 在 Windows runner 产出解构建 exit 0、12-DLL 闭包 PASS、9 项目行 PASS 与原始 job 日志（§5）；caller 本树 dotnet test 复证。原始日志存在 ✓。
- **不可用平台（Unity Editor / IL2CPP / Android NDK / macOS / iOS / 真机）**：双 run 无相关 job；环境缺口**分列**于 `doc/v5-重构/UNITY-ENVIRONMENT.md` 与 V5-0 T4 缺口清单（历史事实口径，本轮未重探查、不将历史证据表述为当前实测）；无任何通过宣称 ✓。
- Unity/IL2CPP 子项状态 = **BLOCKED**（工具链缺失，维持历史口径）；`Mahjong.LocalHost` 工程空壳（0 .cs）属 P4.1 未开工（G2 门内容，非本门缺陷）。

第 1 轮把该门自扩张为"所有平台必须全通过"，更正为上述 P2.2 口径；此为唯一一次口径校正，未降低 Windows 侧证据要求。

## 4. 组件状态分类与完整映射（P1.1–P3.4）

分类口径：完整功能 / 仅独立组件 / 接口先行 / 未接线 / 平台资源阻塞。

### 4.1 组件状态总表

| 组件 | 状态 | 关键证据 |
|---|---|---|
| Domain 牌实体/位置表/守恒（P1.1） | 完整功能（组件级，黄金差分） | TilePositionMap 136 槽+守恒审计；PhysicalGoldenTests 真实 API 对照 |
| Domain 计分 42 役（P1.2） | 仅独立组件，**未接线**（N-2） | `Rules/Yaku.cs` 42 条；`YakuEvaluator/WinningScoreEvaluator` 在 src/Rules 外零调用（探针 M 段） |
| Domain 单局/整场/计时/控制代次（P1.3） | 完整功能（组件级）；**命令面缺 5 类玩家决策动作**（N-1a） | `RoundStateMachine.Execute` :294 覆盖 9 kinds；缺 Chi/Pon/Kan(claim)/Riichi/AgariYame |
| Domain 三绝技/账本/私有状态（P1.4） | 仅独立组件+黄金（含结构-only 边界）；**执行层三段未接**（envelope 载荷/actor 分发/投影，N-1b） | Cheat/ 8 文件；CheatSubmission 六入口（:109/:222/:322/:417/:442/:904）；CheatGoldenTests |
| Domain 码牌/记录随机（P1.5） | 完整功能（组件级） | ShuffleConfig+shuffle-v1 黄金 |
| AI 能力/意图记录（P1.6） | 仅独立组件（组件级退出已接受）；**组合根属宿主层**（SF-1 阶段事实，§7.4） | AiDecisionService 在 src/Mahjong.Ai 外零引用（探针 H 段）；AiDecisionTests 观察纯度+禁边全绿 |
| Contracts/Wire（P2.1/P2.2） | 完整功能（契约面冻结 19+2 事件、27 CommandKind、13 动作） | SchemaCompatibilityTests;ProtocolSnapshot |
| TableRuntime actor（P3.1） | 完整功能（组件合同）；**玩家 envelope 通道直达域**（9 kinds 无 kind 白名单）；自动化座位适配仅 2/10（N-1c 可选扩展） | 玩家路径 :175→:1039→:1151（守卫 :1140-1146）；`MapAction` :4819 仅 :1422（AI）/:1611（超时）调用；UNSUPPORTED_ACTION :1623 |
| 按流投影和快照恢复（P3.2，**正名**） | 完整功能（组件级，6 可见性 Fact+红action） | TableVisibilityProjectionTests；TableProjectionContractMapper |
| 计时、托管与控制竞态（P3.3，**正名**） | 完整功能（组件级） | actor 时钟/deadline/controlEpoch 守卫；TableRuntime 313 |
| 回执/租约/恢复端口（P3.4） | 接口先行（**按任务书明文**：仅接口+内存契约测试，真实 PG 属 V5-3A——阶段事实，非缺陷） | TableDurableContracts.cs:1427-1526；InMemoryTableStore.cs:147-152 唯一实现 |
| LobbyService/PG（P5.3 范围） | 完整功能（组件级） | 89 用例；PostgresLobbyStore；应用级重启回执重放 PostgresLobbyStoreTests.cs:619-671 |
| Client/Wss | 组件完整（132+80；TLS 正向链无条件 Fact） | WssEndpointTests.cs:96 + 4 负控 |
| LocalHost | 空壳工程（0 .cs） | P4.1 未开工（G2 门） |
| Unity/IL2CPP/Android/macOS/真机 | 平台资源阻塞（分列，无通过宣称） | UNITY-ENVIRONMENT.md；T4 清单 |

### 4.2 完整 13 个 ProtocolActionType 四通道映射表（R1-3 遗留更正版，逐项可复算）

枚举源：`dotnet/src/Mahjong.Contracts/ProtocolCommand.cs:40-56`（ProtocolActionType 13 值）；域动作源：`dotnet/src/Mahjong.Domain/RoundTypes.cs:31-42`（RoundActionKind 9 值）；自动化座位合法动作面：`dotnet/src/Mahjong.Domain/DeadlinePolicy.cs:7-18`（LegalActionKind 10 值，**第三套枚举**）。四通道定义（第 2 轮把通道③误作玩家/协议入口，本版更正）：

- **①域**：`RoundStateMachine.Execute(RoundCommand)`（:294，dispatch :207-225）的实际行为；
- **②actor 玩家提交**：`SubmitAsync(TableCommandEnvelope)` :175 → `ProcessCommandCoreAsync` :1039（守卫）→ `_round.Execute(command.Command)` :1151——envelope 承载 `RoundCommand`，**对 9 种域动作无 kind 白名单**；系统阶段动作（建立/摸牌）不走玩家命令；
- **③AI/超时适配**：`MapAction(LegalActionOption)` :4819（仅 :1422 AI 返回 / :1611 超时 fallback 两处调用，经 `ApplyInternalCommandAsync` :1923-1931 入域）；
- **④协议→宿主**：ProtocolActionType 到 actor/宿主的命令映射，属 **P4.1 LocalHost（空壳，G2）/ P5.1 TableService（未开工，V5-3）**——后阶段，非本头缺陷。

| # | ProtocolActionType | ①域 RoundCommand 行为 | ②actor 玩家提交 | ③AI/超时 MapAction | ④协议→宿主 | 分类 |
|---|---|---|---|---|---|---|
| 0 | Unspecified=0 | — | — | — | — | 占位 |
| 1 | StartStacking=1 | ✓ `StartStacking=0`（工厂含发牌，产 `TilesDealt`；域测试 RoundStateMachineTests.cs:254 等） | 系统动作：对局建立走 actor 回合装配路径，**非玩家命令**（保留自动阶段权限，§7.2） | 不适用（建立期无自动化决策） | P5.1 后阶段 | 域有 / 玩家面不适用 |
| 2 | DealComplete=2 | **无同名值**；语义并入 `StartStacking`（发牌完成即 `TilesDealt`，协议侧 `TileDealt=3`/`DealingComplete=4` 事件） | 同上（系统动作） | 不适用 | 同上 | 无同名值 / 语义入口在 |
| 3 | DrawTile=3 | ✓ `DrawTile=1`（权威回合推进驱动） | 系统动作（同上） | 不适用 | 同上 | 域有 / 玩家面不适用 |
| 4 | DiscardTile=4 | ✓ `DiscardTile=2` | **已通**：`RoundCommand.Discard` 构造 envelope（`P31Fixtures.BuildDiscardChain`，TableRuntimeFixtures.cs:785/:796）→`SubmitAsync`→:1151；端到端 TableStreamWireIntegrationTests.cs:145-160 等数十用例 | **已通**：:4827 | P5.1 后阶段 | **已接线**（玩家+自动双通道，均有测试） |
| 5 | DeclareChi=5 | ✗ 域无动作 | ✗（无可承载域命令） | ✗（`LegalActionKind.Chi=4` 无映射） | 同上 | **缺行为**（域） |
| 6 | DeclarePon=6 | ✗ | ✗ | ✗（Pon=5 无映射） | 同上 | **缺行为**（域） |
| 7 | DeclareKan=7 | **部分**：加杠 `DeclareAddedKan=6` 在；明/暗杠 claim 无独立域动作（`ResponseClaimed`/`ChankanWindowOpened` 事件面在，命令入口缺） | AddedKan 可承载（同 :1151 路径）；claim 缺域动作 | ✗（Kan=6 无映射） | 同上 | **部分缺行为**（域 claim） |
| 8 | DeclareRiichi=8 | ✗ 域无动作（`RiichiSticks` 仅为状态/结算量，RoundTypes.cs:113-191） | ✗ | ✗（Riichi=7 无映射）；决策**视图**已映射候选（`MapProtocolAction` `LegalActionKind.Riichi`→`DeclareRiichi`，TableActor.cs:5829-5830） | 同上 | **缺行为**（域；视图有/执行无） |
| 9 | DeclareTsumo=9 | ✓ `DeclareTsumo=3` | **已通**：`committed_tsumo_emits_typed_settlement_event_and_snapshot`（TableStreamWireIntegrationTests.cs:715→:747→:752→:754）端到端 Accepted+Wire 事件；另有 TableDurableReceiptTests.cs:250 故障回滚重试链 | ✗（Tsumo=2 无映射） | 同上 | **玩家已接线**（有端到端测试）/ 自动适配缺 |
| 10 | DeclareRon=10 | ✓ `DeclareRon=4` | envelope 结构可承载（同 :1151 无白名单路径）；本轮未定位 Ron 专属 actor 级端到端测试，如实标注 | ✗（Ron=3 无映射） | 同上 | 玩家通道结构可承载 / 自动适配缺 |
| 11 | Pass=11 | ✓ `Pass=5` | envelope 结构可承载（同上） | **已通**：:4824 | 同上 | 玩家结构+自动已通 |
| 12 | DeclareAbortiveDraw=12 | ✓ `DeclareAbortiveDraw=8` | envelope 结构可承载（同上） | ✗ | 同上 | 域有 / 自动适配缺 |
| 13 | DeclareAgariYame=13 | ✗ 域无动作 | ✗ | ✗（Other=9 无映射） | 同上 | **缺行为**（域） |

注记：
1. 域独有 kinds（协议无同名动作）：`DeclareAddedKan=6`（协议经 `DeclareKan`+`MeldKind.AddedKan=5` 承载）；`DeclareExhaustiveDraw=7`（协议无同名动作；荒牌流局由 `WallExhausted=14`/`ExhaustiveDraw=12` 域事件与 `DrawGame=16` 协议事件承载，属权威自动判定面）。
2. 第 2 轮表中把 StartStacking/DealComplete/DrawTile 标为"阶段入口"却要求其接进 `MapAction`，自相矛盾——三者是权威自动推进的系统动作，本版归位"玩家面不适用"；`MapAction` 是 LegalActionKind→RoundCommand 的**自动化座位适配器**（`LegalActionKind.Cheat=8` 无映射：自动化座位不作出千决策，出千为玩家主动命令，如实记录）。
3. "玩家结构可承载"指 envelope 类型+`:1151` 直达路径对相应 `RoundActionKind` 无过滤（源码结构事实）；Tsumo/Discard 另有端到端测试，其余 kinds 的专属 actor 级测试为验收期补齐项（N-1 验收断言）。

#### 4.2.1 三绝技命令独立映射（7 CommandKind，逐类；R1-3 遗留 b 项）

协议命令面 `CommandKind`（`dotnet/src/Mahjong.Contracts/ProtocolCommand.cs:21-27`）→ 域转移面 `CheatSubmission`（`dotnet/src/Mahjong.Domain/Cheat/CheatSubmission.cs`）→ 执行层接线事实：

| CommandKind | 值 | 域入口（候选对应） | actor/envelope 接线 | 投影/事件面 | 缺口 |
|---|---|---|---|---|---|
| SubmitTileMark | 12 | `ApplyTileMark` :109 | ✗ 零命中（探针 G 段） | 协议私有两事件 `SleeveBloomResolved=20`/`BeamSwapResolved=21` schema 冻结（SchemaCompatibilityTests.cs:409-417）；座位流受信 cheat 投影上下文 TableActor.cs:5955-5969 在位 | envelope 无出千载荷（TableRuntimeContracts.cs:134-142 仅 `RoundCommand`）+ actor 无分发 + 投影未调 |
| SubmitSleeveBloom | 13 | `ActivateSleeveBloom` :222 | ✗ | 同 | 同 |
| SubmitSleeveBloomChoice | 14 | `ChooseSleeveBloom` :322（:417 `ExpireSleeveBloomImmediate` 为权威过期路径，无协议命令） | ✗ | 同 | 同 + 命令↔入口逐一对应未定 |
| SubmitSleeveBloomOffTurn | 15 | `ActivateOffTurnSleeveBloom` :442 | ✗ | 同 | 同 |
| SubmitSleeveBloomOffTurnChoice | 16 | `ChooseSleeveBloom` :322（候选对应） | ✗ | 同 | 同 |
| SubmitBeamSwap | 17 | `ChooseBeamSwap` :904 | ✗ | 同 | 同 |
| SubmitBeamSwapChoice | 18 | `ChooseBeamSwap` :904（选择面对应） | ✗ | 同 | 同 |

**结论**：域转移面（P1.4 六公开入口）、协议命令面（P2.1 七命令）、事件面（两私有类型）三层组件齐备；缺的是**执行层三段——envelope 扩展（出千载荷）→ actor 分发 → `CheatSubmission` 调用 → 投影调用**，当前全为零命中（探针 G 段）。此为门 5 FAIL 的核心构成（N-1b）。诚实注记：CommandKind↔域入口的逐一对应（尤其 14/16/18 三个 Choice 族）属接线设计决策，本表列"候选对应"，最终对应由实现任务定义并经 §7.2 出千行验收。

### 4.3 事件面分层（R1-3 要求：RoundEvent/CheatEvent/Match 与协议映射分开）

1. **协议冻结面（完整）**：`ProtocolEventType` 21 值 = V4 19 类（字段号 1-19 逐一冻结）+ V5 私有 `SleeveBloomResolved=20`、`BeamSwapResolved=21`；schema 冻结测试 `EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents`（SchemaCompatibilityTests.cs:409-417）；私有策略 `ProtocolEventPolicy.PrivateTypes` 6 类；未知事件拒绝（:79 IsKnownEventType，:256/:295 双闸）。**第 1 轮以"域内 16 vs V4 19"推断协议缺失是错误口径，撤回。**
2. **域内生命周期事件（内部）**：`RoundEventType` 16 值（RoundTransition.cs:56-74）含 V4 无的窗口/终局事件（`ResponseWindowOpened`/`ChankanWindowOpened`/`WallExhausted`），与协议 21 类**非一对一**，映射在投影层 `TableProjectionContractMapper`（WallBuilt :424、DealingComplete :433、RiichiDeclared :471、CheatPointsChanged :505、ScoreSettled :521、MatchEnded :531 等 payload 构造器在位）。
3. **出千私有事件**：协议 20/21 两类为 Private；域侧经 Cheat/Ledger/Projection 携带（checkpoint `TableActor.cs:2618-2641`；恢复 `:3371-3389`）。
4. **整场事件**：`MatchEnded=19` 协议在册；域侧 `MatchProgression.cs`（连庄/本场/终局归属）为另线模块。

### 4.4 持久化端口（P3.4，阶段事实）

接口 `ITableStore/IReceiptStore/ITableLease/ISettlementOutbox`（TableDurableContracts.cs:1427-1526），单原子边界 `CommitAsync`（:1469-1475 注释）；唯一实现 `InMemoryTableStore`（InMemoryTableStore.cs:147-152，四接口同实现，按 PG 同形事务约束）。任务书 P3.4 明文："这里仅完成接口与内存契约测试，**真实 PG 实现属于 V5-3A**"——内存-only 是**按计划的阶段边界，不是缺陷**（第 1 轮 D-6 撤回）。真实 PG 现存于 Lobby 线（PostgresLobbyStore/PostgresRoomProjectionReader）；跨进程重启探针见 §5.4。

### 4.5 AI 观察与决策（P1.6 组件语义 vs P4.1/P5.1 宿主组合根边界）

actor 座位级观察（:390-404/:4105-4200，含 Serving/时钟/决策窗/控制纪元守卫）；Ai 模型层本座位强制（§3.7）；`IAiStrategy.Decide(AiObservation, CancellationToken)` 双参签名冻结（AiDecisionTests.cs:431-436）。**组件语义（P1.6，已按组件级退出接受）**：观察纯度、迟到/过期拒绝、意图记录契约已测。**宿主组合根边界（SF-1 阶段事实，§7.4）**：`IAiStrategy/AiDecisionService` 在 src/Mahjong.Ai 之外零引用（探针 H 段），`BasicAiContext` 无生产构造点（喂真实 actor 观察将抛 `BasicAiContextException`，BasicAiStrategy.cs:61/:502）——strategy→观察装配属**宿主组合根**，即 P4.1 LocalHost（任务书 :114-119，现空壳工程，G2 门内容）与 P5.1 TableService（:156-161，未开工，V5-3）的产物清单；G1 九门原文（任务书 :107）九项无 AI 项。自动化管线本身在位：AI 返回 :1422 → `MapAction` → `ApplyInternalCommandAsync` :1923-1931 → `_round.Execute` :1931（TableActorTests.cs:182/:213 两通道判别式测试）。

## 5. CI 远端产物亲自核验记录

### 5.1 run 身份（自 run-info.txt 亲读）

| | ci-push | ci-pr |
|---|---|---|
| URL | https://github.com/liuchangready/majiang-game/actions/runs/40125990495 | https://github.com/liuchangready/majiang-game/actions/runs/40125992805 |
| GITHUB_RUN_ID / attempt | 40125990495 / 1 | 40125992805 / 1 |
| 实测 checkout | `8ee22ebf…8bd`（GITHUB_SHA） | `e1e0e88eae5518b0d4e6f4bb9532432ed11a6fc5`（PR19 merge，对 origin/v5 `457274c`） |
| ref / runner | refs/heads/feat/v5-1-integration / Windows | refs/pull/19/merge / Windows |
| 验证脚本 SHA256 | `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`（== 本地 `tools/verify-v5.ps1`） | 同左 |

产物本地基（caller 下载，本代理只读）：`D:/majiang-wt/codex-progress-20261025/artifacts/progress-20261025/{ci-push,ci-pr}/`。

### 5.2 每项目 meta 与框架日志

两 bundle 各 7 个测试项目 run 目录；per-project meta 文件名为 **`dotnetTest.meta.txt`**（第 1 轮误写 JSON，更正；探针 K 段列出 7 条路径），14 份全部 exit 0、头一致、携带框架日志 SHA256（逐份读取核验）。汇总 `summary.log` 双份均 7 行项目计数 + OVERALL PASS 9 行。

关键 SHA256（第 1 轮重算，本轮复核一致）：

| 文件 | SHA256 |
|---|---|
| push summary.log | `b8bdaf8a32b37d22ebc9502f908d28c63af685c7f83094a8a3e3973a08d1fd12` |
| push run-info.txt | `bf7c583459f26ae1befc1fda148457ecb55640ee8bda160276b5475d0e2d22ea` |
| push wrapper-summary.json | `04f498dbcf49bf388f6f286c3734139961f317ba379aacd54492d3de973246a5` |
| push postgres-evidence.jsonl | `d2afb1a976144e780c804a336752d0de2aefdcd5441fd9f5771154ad3ec3bc77` |
| PR summary.log | `017f61c24b382116aa667b46819e0c3a563ad38d07332d2c8c4335d31ecaa277` |
| PR run-info.txt | `9910ae738ca5ed0329740853f4a775cff7321edeee32f393d38482b077d88c1f` |
| PR wrapper-summary.json | `779846c009c4e5cb9171ec16bc2a24e18e8afd24e4dfb1e1e7a34a15badd3915` |
| PR postgres-evidence.jsonl | `e31cb92f8781518b120e3f6a9a06603a7b358991be756ee286d02c25525a4c33` |
| ci-push-40125990495.log（job log） | `419df7c2f2ef197e41a08d046d4c9d12d7e035987ee5f4d19a58d0addd7a8936` |
| ci-pr-40125992805.log（job log） | `89a771ce96996998442d631e386c33d13ae32dda14a4d1662e0450f50c305da4` |

（注：探针 H 段日志中源自 CJK 注释的行在 PS 5.1 GBK 控制台显示为乱码，系控制台代码页显示问题；底层文件为 UTF-8，行号与 ASCII 片段可定位，内容以本报告引用的 UTF-8 读取为准。）

### 5.3 统一用例 vs wrapper 额外探针

统一 1,356 用例 = 7 测试项目 `dotnet test`；wrapper 十阶段（precheck/start-postgres/ownership-marker/environment/migrate/verify/archive-cluster-evidence/cleanup/env-restore/write-evidence）为额外 PG 包装层，不增减用例数；两 run wrapper-summary 均 10 阶段 PASS、verifier exit 0。env-restore：两 run 各 4 变量 `restored-absent`、mechanism=`provider-set-item-null`、`rootDeleted: true`。

### 5.4 PG 证据逐条（亲自读 evidence jsonl + root 日志）

两 run `postgres-evidence.jsonl` 均含：server identity（PG 16.10）、`terminal.projection`、`receipt.terminalReplay`、`recovery.expiredReservation`、`migration.repeated`（migrationRuns:3）、`concurrency.crossRoomJoin ×3`、`crossConnectionRowLock`、`receipt.replay`、`transaction.rollback`、`ticketConsumption`、`roomFill`。

**真实 PG 两进程重启写读：本轮两个 CI run 中不存在。** `root-logs/postgres.log` 仅 `ready-count=1`（单次 postmaster 启动），无 `restart.json`；evidence 无 `restart.write|restart.read` 事件。CI 内的"重启"证据是**应用级**回执重放（PostgresLobbyStoreTests.cs:619-671）。真正两进程探针（`dotnet/tests/Mahjong.LobbyService.PostgresProbe/Program.cs`：write→真重启（postmaster 启动时间必须变化 :198-202、读写必须异进程 :203-206）→read）由本地 `dotnet/scripts/pg-lobby-verify/verify-all.ps1` 编排，**不在 CI wrapper 内**（I-1）。caller 独立确认 ready-count=1。

### 5.5 正向 TLS 链归因

MTP/xUnit v3 框架日志为 UTF-16LE（`iconv` 转码后仅见 per-assembly total 汇总，无 per-test 明细，I-3）。归因为组合证据：(a) 正向用例为无条件 `[Fact]`（`WssEndpointTests.cs:96` `PositiveLocalWssTrustChainCompletesTlsHandshakeAndWireRoundTrip`，每测试独立根 CA + socket factory，机器存储零改动，:110-113 注释）；(b) Wss 80/0/0 全绿 meta；(c) 4 负控同为无条件 Fact（自签 :76、异主机名 :150、不受信根 :174、过期叶 :199，均断言 `TlsHandshakeFailed+Permanent`）。

### 5.6 历史 PR 39970580589 Client131/132 — UNRESOLVED（维持）

失败框架日志在历史 bundle 缺失，无法补齐根因；当前双 run 全绿不构成原因已查明；不盲重试、不标 flaky（I-2）。caller 本轮两次非标准输出路径的本地测试失败已定位为路径/发现机制问题并按受支持路径复验通过，**不归因于该历史失败**。

### 5.7 本地统一 verify 与 CI 差异分列（非全绿声明，如实记录）

| 运行 | 结果 | 证据 |
|---|---|---|
| CI（远端产物核验，§5.1-5.4） | 1,356 passed / 0 failed / 0 skipped，14 份 per-project meta 全 exit 0 | §5.2 |
| caller 本树统一 verify（caller round-2 指令提供，**本轮未重跑**） | 总数 1,356 = **1,344 passed / 0 failed / 12 skipped**（Lobby 89 = 77+12：无本地 PG 的 12 个 PG 用例 skip）；**严格 verifier 整体 exit 1** | caller E=`C:/Users/sncdc/AppData/Local/Temp/g1-caller-round2-20261025`：review-audit.ps1 SHA `8106B0A0489508F5E11C0AACD95311C2F5D3681121DAA4D16AE804FCDBC9F5E1`、review-audit-run.log SHA `9A0E1218F8AED56CB9F4E49BDC95EC307BF317CF64B6B5B5B89559E513EA2145`；本地统一 summary.log SHA `272112BBB8C990F86F1F425AC0A99DCBB186E29AFA5810E87999A59B73DCCCD2`；catalog PASS exit0 log SHA `E3F631D97B0D9C2AF3B5084E4CB751BE0FCAE856D6F231FE8A950E98CF38F6D2` |
| caller 本树统一 verify（caller round-3 实测，**本轮未重跑**；`tools/verify-v5.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -LogDir …round3-20261025-045851`，验证器自动避让已有目录、实际日志 `…-045851-1`） | solution 编译 PASS exit 0、scratch 12-DLL PASS；Client 132/Domain 322/Ai 316/Contracts 104/TableRuntime 313/Wss 80 均 0 失败 0 跳过；Lobby 89=77+12（无本地 PG）；**总数 1,356 = 1,344 passed / 0 failed / 12 skipped；严格 verifier 整体 exit 1**——与上轮环境缺口一致 | 本地统一 summary.log SHA `86D9B383B639720936F1C693FBA4D15913B3A393F463958D6673F47D64F2BD0E` |

差异说明：本地 skip 12 = 无本地 PG 实例的 PG 依赖用例（CI wrapper 启 PG 后同用例全过，§5.3/5.4），这是两环境计数的唯一结构差异；**严格 verifier exit 1 如实记录为非全绿状态**——本轮不重新解释其判据、不修改 verifier、不用 skip/放宽断言造绿。本报告任何位置均不得表述为"本轮本地运行全绿"。

## 6. 本轮探针记录（第 1-4 轮，全部可复现）

统一编号（第 1 轮 §3.2/§3.3/§3.4 的"§6.0/§6.1/§6.2"断链已废弃，改指本节）：

| 编号 | 探针 | 轮次 | 结果 | 证据 |
|---|---|---|---|---|
| P-A | 黄金目录 validator 本树复跑 | R1+R2 | PASS exit 0；catalog SHA `16F7FD7F…891B`；73 需求 full17/partial20/none33/v5only3 | probes.log O 段 |
| P-B | 依赖方向：8 处静态断言定位 | R2 | 全部在位（:376/:389/:399/:409/:1049/:449/:749/:13） | probes.log B 段 |
| P-C | 依赖方向 csproj/using 图 | R1 | Domain=∅、Contracts=∅、Wire→Contracts、Ai→Domain、Client→{Contracts,Wire}、Lobby→Contracts、TableRuntime→{Domain,Ai,Contracts}、LocalHost→TableRuntime | 第 1 轮 Bash 记录；结论并入 §3.3 |
| P-D | Domain 确定性严格 grep | R1+R2 | NO-MATCH | probes.log C 段 |
| P-E | 动作/事件四枚举逐值提取 | R2 | RoundActionKind 9 / ProtocolActionType 13 / ProtocolEventType 21 / RoundEventType 16 | probes.log D 段 |
| P-F | 域权威入口 dispatch | R2 | `Execute` :294 覆盖 9 kinds（:207-225） | probes.log E 段 |
| P-G | actor MapAction 面积 | R2 | 仅 Pass(:4824)/Discard(:4827)；P3.1 边界注释 :4830 | probes.log F 段 |
| P-H | 协议 19+2 冻结 + 私有策略 | R2 | :54-55/:69/:79/:91/:256/:295 | probes.log G 段 |
| P-I | Golden 结构-only 边界 | R2 | validator 头(:9-11)/catalog status(:4-5)/G-028a(manifest:343)/Physical(:20-22,:104-105)/Cheat(:644)/Round(:101,:134) | probes.log H 段 |
| P-J | 136 物理守恒锚点 | R2 | TilePositionMap :125/:172/:184/:193/:224 + PhysicalGoldenTests :104-105 | probes.log I 段 |
| P-K | spectator 前置拒绝锚点 | R2 | IndependentReviewTests.cs:66 | probes.log J 段 |
| P-L | CI meta 文件名 | R2 | 7× `dotnetTest.meta.txt` | probes.log K 段 |
| P-M | 兄弟任务符号 | R1+R2 | AccessCapabilities 0；Ai 三符号仅 AiCandidateModels.cs:21 注释 | probes.log L 段 |
| P-N | 计分生产调用者 | R1+R2 | src/Rules 外 NO-MATCH（未接线） | probes.log M 段 |
| P-O | AI 组合根 | R1+R2 | src/Mahjong.Ai 外 NO-MATCH；BasicAiContextException :61/:502 | probes.log N 段 |
| P-P | Ai 本座位观察（源读） | R1 | AiObservation.cs:73-83/:318-321/:349/:421 守卫 | 第 1 轮源读，锚点见 §3.7 |
| P-Q | 按流隐私测试锚点 | R1 | TableVisibilityProjectionTests 6+3 Fact | §3.7 |
| P-R | 四通道核验：MapAction 全部出现点 / 玩家路径直达 / 出千命令零接线 | R3 | MapAction 恰 3 处（:1422 AI / :1611 超时 / :4819 定义，ASSERT-PASS）；`_round.Execute` :1151（玩家）/ :1931（内自动）；出千符号在 TableRuntime src NO-MATCH（ASSERT-PASS） | probes-r3.log B/C/G 段 |
| P-S | 端到端测试锚点（玩家双命令 + 自动双通道） | R3 | committed_tsumo :715（`RoundCommand.Tsumo` :747→SubmitAsync :752→Accepted :754）；TerminalOutboxFault :250；DiscardChain fixtures:785/:796；AI/超时 TableActorTests.cs:182/:213 | probes-r3.log D 段 |
| P-T | 三套枚举分离 | R3 | CommandKind 出千块 :21-27（SubmitTileMark=12…SubmitBeamSwapChoice=18）；LegalActionKind 10 值（DeadlinePolicy.cs:7-18）；RoundActionKind 9 值（RoundTypes.cs:31-42） | probes-r3.log E 段 |
| P-U | 域出千六入口 | R3 | CheatSubmission.cs :109/:222/:322/:417/:442/:904（ASSERT-PASS ≥6） | probes-r3.log F 段 |
| P-V | 状态复检 + 历史证据不可变 + validator 复跑 | R3 | 计分未接线/AI 无组合根 NO-MATCH（ASSERT-PASS×2）；旧 probes.log SHA 等值（ASSERT-PASS）；validator PASS exit 0（ASSERT-PASS） | probes-r3.log H/I/J 段 |
| P-W | 当前交付复核：数值退出码语义 + 审查不变量 HEAD + 历史日志（R2+R3）不可变 + 保留断言复跑 + 负例 | R4 | `cmd /c exit 0/1`→`$LASTEXITCODE`=0/1（ASSERT-PASS×2）；ACTUAL-HEAD 记录=7c451c6…941、`merge-base --is-ancestor 8ee22eb`=0、对冻结头恰 1 文件（报告）且 dotnet/tests/tools/assets 零变化（ASSERT-PASS×4）；R2+R3 日志 SHA 等值（ASSERT-PASS×2）；MapAction 恰 3 处/:1151 直达/:715/:250/AI·超时锚点/域六入口/出千零命中/计分·AI NO-MATCH/validator exit0（ASSERT-PASS×13，强度不减）；隔离负例断言失败→数值 exit 1 | probes-r4-9a0ca4c9e583.log（A-J 段）+ negative-selftest-6258b41fec33.log |

第 2 轮探针脚本与日志：`C:/Users/sncdc/AppData/Local/Temp/g1-review-r2-evidence/run-probes.ps1`、`probes.log`（SHA256 `3138e0b27e61e95a549018bc92ccf5ea360e4d33f154c69ead730bdba209c09a`）——**历史证据，禁止覆盖/刷新**（R3/R4 脚本 I 段哈希不变校验 ASSERT-PASS）。第 3 轮探针脚本与日志：`C:/Users/sncdc/AppData/Local/Temp/g1-review-r3-evidence/run-probes-r3.ps1`、`probes-r3.log`（SHA256 `146c388923f6427d0527c1806c1c2913e8e78664606649475d394fbfb2b80fe7`，170 行，14 ASSERT-PASS / 0 ASSERT-FAIL，进程 exit 0）——**固定采样 HEAD=51cc902 的历史采样事实**；其 HEAD 硬编码断言不迁移到当前交付（§0.3），源码断言已由 R4 脚本以等强度承接。第 4 轮探针（当前交付可复现）：`C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence/run-probes-r4.ps1`、`probes-r4-9a0ca4c9e583.log`（SHA256 `a2bf068f5d5164910903d0905cadc16ea38d5c44279a5307a0ba7269f7779bfc`，123 行，21 ASSERT-PASS / 0 ASSERT-FAIL，**数值进程 exit 0**）与隔离负例 `negative-selftest-r4.ps1`、`negative-selftest-6258b41fec33.log`（SHA256 `fdb777f6f59682d2988b89dc5fe952c90be208cbbbbe713ffec5394afda9252f`，断言失败→**数值 exit 1**）；断言合同与复现命令见 §12.2。**历史 CI 证据（§5）为既存产物只读核验，与本轮新执行探针分开列示。** 未重跑项：本地 `verify-all.ps1` 全套（含 PG 重启探针）、77/91 串行 PS 自测、Unity/设备链路——无代码变化不盲重跑重型自测。

## 7. 缺陷/缺口清单（第 4 轮现行全量；每条含复现/证据/修复/验收）

### 7.0 第 1 轮清单更正记录

| 原编号 | 处置 | 理由 |
|---|---|---|
| D-1"仅 Lobby 有静态测试" | **撤回（假发现）** | 固定头实有 8 处断言（§3.3）；"非完整白名单"≠"无测试" |
| D-2 source-scan 守护缺失为 MAJOR | **降级 I-4** | G1 原文为状态要求，未明文要求该守护；不得自加门 |
| D-3/D-4 | **重构为 N-1/N-2** | 原责任归属错标 P3.2/P3.3，且无出千/计分实际任务；按任务书正名 |
| D-5 | **重构为 N-4** | 内容不变，编号规整 |
| D-6"表事件端口仅 InMemory" | **撤回（非缺陷）** | 任务书 P3.4 明文真实 PG 属 V5-3A，阶段事实 |
| 第 2 轮 N-1"13 协议动作 actor 仅通 2" | **撤回表述，重构为 N-1a/N-1b/N-1c** | 该表述把 AI/超时适配器 `MapAction` 误当玩家/协议入口（R1-3 遗留）；四通道更正后真实缺口=域缺 5 动作（N-1a）+出千执行层三段未接（N-1b）+自动适配 2/10（N-1c 可选），§7.1 |
| 第 2 轮 N-4"AI 组合根缺失"MAJOR | **改判阶段事实 SF-1**（§7.4） | G1 九门原文（任务书 :107）无 AI 项；P1.6 退出为组件级且已接受；组合根属 P4.1/P5.1 宿主产物清单。不得一处列 MAJOR 一处关闭公式遗漏（消除 §7/§8 不一致） |

### 7.1 现行缺陷/缺口（同类已合并）

**N-1 [MAJOR] 权威链真实缺口：域缺 5 类玩家决策动作（N-1a）+ 出千 7 命令零执行层接线（N-1b）+ 自动化座位适配 2/10（N-1c，可选扩展）**

第 2 轮表述"13 协议动作 actor 仅通 2"**撤回**：该表述把 AI/超时适配器 `MapAction` 误当玩家/协议入口（§4.2 四通道更正）。修正后事实：玩家 envelope 通道 `:175→:1039→:1151` 对全部 9 种域动作无 kind 白名单，Tsumo/Discard 端到端有既存测试（§3.5 通道②）。真实缺口三子项：

**N-1a 域侧缺 5 类玩家决策动作**
- **位置**：`dotnet/src/Mahjong.Domain/RoundTypes.cs:31-42`（`RoundActionKind` 9 值无 DeclareChi/DeclarePon/DeclareKan(claim)/DeclareRiichi/DeclareAgariYame）；`RoundStateMachine.cs:207-225`（dispatch 无对应 case）、`:294`。
- **复现**：探针 E 段枚举逐值（§12.5）；协议 5 动作（ProtocolActionType 5/6/7-claim/8/13）无可承载域命令（§4.2 表"缺行为"行）。
- **实际 vs 期望**：实际=协议鸣牌/立直/和了止动作无域命令面；期望=任务书 P1.3 退出口径下单局状态机覆盖全部玩家决策（与协议 21 类事件映射对齐）。
- **证据路径**：probes-r3.log E 段；RoundTypes.cs/RoundStateMachine.cs 固定头源码。
- **修复要求**（建议 G1 补齐子任务，尚未分配；文件 scope 见 §9.1）：`RoundActionKind` 增 5 值、`Execute` dispatch 补 case、`RoundTransition` 产对应域事件（claim 可沿用 `ResponseClaimed` 泛化或新增，须与协议 21 类映射表对齐）。不得改名已完成 P3.2（按流投影和快照恢复）/P3.3（计时托管控制竞态）。

**N-1b 出千 7 命令零执行层接线**
- **位置**：协议命令面 ProtocolCommand.cs:21-27（7 值在）；域转移面 `CheatSubmission.cs:109/:222/:322/:417/:442/:904`（六入口在）；**缺**：envelope 出千载荷（`TableRuntimeContracts.cs:134-142` 仅 `RoundCommand`）、actor 分发（探针 G 段零命中）、投影调用。
- **复现**：§12.5 G 段命令（期望 NO-MATCH）；§4.2.1 表逐行"✗"。
- **实际 vs 期望**：实际=三绝技命令在 actor 层无任何路径，`CommandKind` 12-18 无消费者；期望=任务书 P1.4 退出标准——资格/费用/窗口/等额物理移动经权威链端到端成立（协议命令→回执→域事件→投影）。
- **证据路径**：probes-r3.log G 段（NO-MATCH 断言）；E/F 段；§4.2.1。
- **修复要求**（建议 G1 补齐子任务，尚未分配；文件 scope 见 §9.1）：`TableCommandEnvelope` 扩展出千载荷（或显式变体）→ actor 按 `CommandKind` 分发至 `CheatSubmission` 对应入口 → `TableProjectionContractMapper` 投影 20/21 私有事件；命令↔入口逐一对应由实现任务定义（§4.2.1 注记）。
- **依赖**：N-2 结算接线不依赖本项；但 P1.4 端到端验收依赖 §7.2 出千行负例矩阵。

**N-1c 自动化座位适配 2/10（可选扩展，不单独构成 G1 阻断）**
- **位置**：`TableActor.cs:4819`（`MapAction` 仅 Pass :4824/Discard :4827）；`DeadlinePolicy.cs:7-18`（LegalActionKind 10 值）；调用点 :1422（AI，null→保持 pending :1423-1425）/:1611（超时 fallback，null→`UNSUPPORTED_ACTION` :1623）。
- **复现**：探针 B 段（恰 3 处出现断言）。
- **实际 vs 期望**：实际=自动化座位（AI/超时）只能响应 Pass/Discard；期望=若产品要求自动化座位响应鸣牌/立直窗口，则扩展映射。**是否扩展属产品决策，由 caller 定**；G1 九门文本无 AI 项，本子项不构成 G1 阻断——列出是为与 N-1a/N-1b 的伴生面（新窗口打开后自动化座位的行为）一次说清，避免遗漏。
- **修复要求**（若实施）：`MapAction` 按 N-1a 新增域动作补对应 `LegalActionKind` 分支，保留 null→pending 语义；§7.2 仅在实施时适用其扩展行。

**N-1 验收断言（统一）**：§4.2 表"缺行为"行转"已接线"；§4.2.1 七行"actor/envelope 接线"列转"✓"；§7.2 负例矩阵全过；黄金对齐（round/cheat 目录）新增用例一致。

**N-2 [MAJOR] 结算计算未接线（计分评估器零生产调用者）**
- **位置**：`dotnet/src/Mahjong.Domain/Rules/YakuEvaluator.cs`/`WinningScoreEvaluator.cs`（src/Rules 外零调用，探针 H 段复检 ASSERT-PASS）；`RoundStateMachine.cs:21`（头注释"computed by the frozen rules layer; this class verifies"）、`:742-750`（结算守恒验证）、`:802-822`。
- **复现**：§12.5 计分接线命令（Get-ChildItem 排除 Rules 扫引用，期望 NO-MATCH）。
- **实际 vs 期望**：实际=`RoundSettlement` 由调用方供给（既存 Tsumo 端到端测试即由测试供给 settlement，TableStreamWireIntegrationTests.cs:747）、状态机只验证；期望=权威链内由规则层计算结算（P1.2 组件已黄金验收，差在接线）。
- **证据路径**：probes-r3.log H 段；probes.log（R2）M 段；RoundStateMachine.cs 源码。
- **修复要求**（建议 G1 补齐子任务，尚未分配；文件 scope 见 §9.1）：结算由 YakuEvaluator/WinningScoreEvaluator 在权威链计算，保留 :742-822 守恒/一致性验证为第二道闸。
- **验收断言**：权威链产生的 settlement 与 scoring-v1（10,238 用例）一致；守恒断言对错误计算拒绝（§7.2 结算行）。

**N-3 [MAJOR] 黄金全字段对齐残余（门 2 关闭条件）**
- **位置**：manifest G-028a（`doc/v5-重构/V5-0-GOLDEN-MANIFEST.md:343`，pending/partial）；`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs:101,134`；`CheatGoldenTests.cs:644`；`PhysicalGoldenTests.cs:20-26`；manifest 内 20 项 partial 覆盖需求。
- **复现**：§12.5 沿用命令（Golden 边界组，对应 R2 probes.log H 段，日志为不可变历史证据）；或读 manifest partial/pending 列。
- **实际 vs 期望**：实际=终局重放不断言完整 eventTypes 序列（ScoreSettled/RoundEnded 序列、MatchEnded 缺席）、V4-only 物理移动路径仅结构、20 partial 未对齐；期望=门 2"已导出黄金样本对齐率 100%"可依行为级证据宣布。
- **证据路径**：R2 probes.log H 段（SHA `3138e0b2…c09a`，§12.3）；manifest；E/catalog.log（SHA `E3F631D9…8F6D2`）。
- **修复要求**（按 manifest 既定 pending 项推进，不新造口径）：① RoundTerminalReplaysExactly 增补全事件序列断言（含 MatchEnded 缺席/ScoreSettled/RoundEnded 次序）；② V4-only 路径（mv-swap-drawn-with-wall-backfill）明确产品决策（C# 入口或永久结构记录）；③ 20 partial 逐项转 full 或记录不可导出理由。
- **验收断言**：manifest 对应行转 accepted/full 或留有显式决策记录；validator 机械层与行为断言层分开报告，不合并成"100%"。

### 7.2 统一负例矩阵（只作用于真实新增入口；自动阶段权限保留）

**作用域（第 2 轮口径更正）**：本矩阵只对下列真实新增入口生效——(a) N-1a 新增域动作的命令提交（Chi/Pon/Kan(claim)/Riichi/AgariYame）；(b) N-1b 接线后的 7 出千命令；(c) 仅当 caller 决定实施 N-1c 时，扩展后的 `LegalActionKind` 映射。**不作用于既有系统阶段动作**：StartStacking/DealComplete/DrawTile 属权威自动推进（保留自动阶段权限，§4.2 表），超时 Pass/Discard fallback 通道已有守卫测试（TableActorTests.cs:182 等）——第 2 轮把它们要求接进玩家命令适配属口径错误，已随 §4.2 更正，不为其追加玩家负例。

| 负例类 | 断言 | 适用 |
|---|---|---|
| 资格不满足 | 拒绝且零状态变更（无事件、版本不变） | (a)(b)(c) |
| 窗口关闭/过期 | 拒绝；决策窗外提交不入账 | (a)(b)(c) |
| 旧 controlEpoch/旧 roundId | 拒绝，不可复活/不可跨局生效（既有 :1140-1146 control epoch 守卫语义扩展到新入口） | (a)(b)(c) |
| 原子性（扣费/物理移动失败） | 回滚零副作用；出千类 entryId 同载荷重放返回原回执、异载荷冲突（P1.4 契约） | (b) |
| 结算错误注入 | 守恒/结果一致性验证拒绝错误 settlement（RoundStateMachine.cs:742-822 既有闸保持） | N-2 |

### 7.3 INFO 挂账（非门槛缺陷）

| ID | 内容 | 证据 |
|---|---|---|
| I-1 | CI wrapper 不含 PG server-restart 探针（仅本地 verify-all.ps1）；G2/G3 前决定是否纳入（需评估一次性集群生命周期与安全） | §5.4 |
| I-2 | 历史 PR 39970580589 Client131/132 UNRESOLVED：失败框架日志缺失；当前绿不构成根因结论；不盲重试不称 flaky | §5.6 |
| I-3 | MTP 框架日志 UTF-16 无 per-test 明细；归因为组合证据；可选改进=wrapper 增 per-test 明文清单（caller 决定，非门） | §5.5 |
| I-4 | Domain 确定性 source-scan 守护测试（建议增强，非 G1 门条款；当前状态 PASS 以 P-D 探针为证） | §3.4 |
| I-5 | 依赖方向精确白名单增强（建议，非门）：补 TableRuntime 正向允许集、Wire✛Domain、LocalHost✛其余断言 | §3.3 |

### 7.4 阶段事实（非缺陷）：SF-1 AI 组合根属宿主层（P4.1/P5.1），非 G1 阻断

原编号 N-4（第 2 轮 MAJOR），本版按正式条款改判：

1. **G1 门文本无 AI 项**：任务书 :107 G1 九门逐字为"dotnet test 独立全绿；已导出黄金样本对齐率为 100%，未导出样本单列；依赖方向静态测试通过；Domain 不含隐式随机/时钟/浮点权威；三绝技与 136 守恒通过；Wire 可重复生成；按座位/观战私有性通过；TableRuntime 并发、版本与恢复合同通过；Unity/IL2CPP 可用平台的构建日志和缺口分列"——无任何 AI/组合根条款。
2. **P1.6 退出为组件级且已接受**：任务书 :81-83 P1.6 退出标准="迟到/过期结果拒绝；同一记录意图回放一致；AI 无暗牌读取路径"——组件级语义，AiDecisionTests.cs:431-452（观察纯度+程序集禁边）已测且双 run 全绿。组件退出不被本报告改写（冻结口径）。
3. **组合根属宿主产物清单**：strategy→观察装配是组合根职责，落点为 P4.1 LocalHost（任务书 :114-119：`LocalTableHost : ITableChannel`、本地时钟/随机适配；现空壳工程 0 .cs，G2 门内容）与 P5.1 TableService（任务书 :156-161，未开工，V5-3）。两阶段均未开工是**阶段事实**（同 P3.4 内存-only 口径），非本头缺陷。

**现状保留（如实记录，不作为缺陷计）**：`IAiStrategy/AiDecisionService` 在 src/Mahjong.Ai 外零引用（探针 H 段 ASSERT-PASS）；`BasicAiContext` 无生产构造点（BasicAiStrategy.cs:61/:502，喂真实 actor 观察将抛 `BasicAiContextException`）。自动化管线在位：AI 返回 :1422 → `MapAction` → `ApplyInternalCommandAsync` :1923-1931 → `_round.Execute` :1931，判别式测试 TableActorTests.cs:182（超时胜同 tick AI）/:213（deadline 前 AI 被接受并落牌）。

**落点与依赖**：P4.1 组合根任务（G2 阶段入口）；依赖在途兄弟任务 T-A 的纯上下文（§10）与 N-1 候选面（§4.2 决策视图已有候选映射）。**不进 §8 G1 关闭公式**；此改判消除第 2 轮"§7 列 N-4 MAJOR 而 §8 关闭公式无 N-4"的不一致。

## 8. 残余责任任务与依赖 DAG（按任务书正名）

```
N-1a 域侧 5 动作扩展（P1.1/P1.3 延伸）──→ N-1b 出千执行层三段接线（P1.4×P3.1 延伸）
        │                                        │
        └──→ N-2 结算计算接线（P1.2 组件→权威链）   │
                 │                               │
N-1c 自动化适配扩展（可选，caller 决策；G1 九门无 AI 项，不阻断）
                 │
N-3 黄金对齐残余推进（manifest pending/partial 逐项）
                 │
I-1/I-3 wrapper 探针与清单（可选，caller 决策）
                 │
        G1 关闭判定（caller）
                 │
   ┌─────────────┼──────────────────────────────┐
   ▼             ▼                              ▼
V5-2/G2（P4.1 LocalHost 组合根：      V5-3A 表事件 PG ITableStore 族    V5-3/G3（P5.1 WSS 宿主与准入：
SF-1 AI 装配 + 宿主命令映射入口；      （P3.4 明文后阶段，独立可先行）     协议→actor 入站命令映射在此落位）
依赖 P5.4a Client 接口基线；                                           → G4（P6.x）→ G5（P7.x）
T-A 纯上下文在此被消费）
```

- **G1 关闭 = 门 2（N-3 推进至可宣布的对齐口径）+ 门 5（N-1a/N-1b 完成 + N-2 接线 + §7.2 负例矩阵过）**；N-1c 可选；**SF-1 不在关闭公式**（§7.4）；门 1/3/4/6/7/8/9 维持 PASS 即可（门 9 的 Unity/IL2CPP 子项按 P2.2 保持"阻塞分列"口径）。
- 阶段事实不构成 DAG 阻塞：AI 组合根=SF-1→P4.1（G2）；表事件 PG 存储=V5-3A（P3.4 明文）；协议→宿主入站命令映射=P5.1（V5-3）——三者均不需为 G1 提前。
- N-1a/N-1b/N-2/N-3 均为"建议 G1 补齐子任务，**尚未分配**"——下发与否、粒度与 expectedFiles 由 caller 决定；两个在途 DS 任务（§10）不重复下发。

## 9. 可下发的文件范围建议（本轮不实施；下发与否由 caller 决定）

### 9.1 G1 补齐子任务建议文件范围（对应 N-1a/N-1b/N-2/N-3，均"尚未分配"）

| ID | 内容 | 建议源码文件 | 建议测试文件 |
|---|---|---|---|
| S-N1a | 域侧 5 决策动作扩展（N-1a） | `dotnet/src/Mahjong.Domain/RoundTypes.cs`（RoundActionKind 增值）、`RoundStateMachine.cs`（dispatch case + 窗口裁决）、`RoundTransition.cs`（新事件，须与协议 21 类映射表对齐） | `dotnet/tests/Mahjong.Domain.Tests/`（RoundStateMachineTests 扩展：每动作接受/拒绝/守恒；黄金 round 目录对齐新增用例） |
| S-N1b | 出千执行层三段接线（N-1b） | `dotnet/src/Mahjong.TableRuntime/TableRuntimeContracts.cs`（envelope 出千载荷扩展）、`TableActor.cs`（CommandKind→CheatSubmission 分发）、`TableProjectionContractMapper`（20/21 私有事件投影） | `dotnet/tests/Mahjong.TableRuntime.Tests/`（7 命令端到端 + §7.2 出千行负例 + 回执幂等/冲突） |
| S-N2 | 结算计算接线（N-2） | `dotnet/src/Mahjong.Domain/RoundStateMachine.cs`（或 caller 定义的新结算接线点）：YakuEvaluator/WinningScoreEvaluator 入权威链，保留 :742-822 守恒第二道闸 | `dotnet/tests/Mahjong.Domain.Tests/`（scoring-v1 全量对齐 + 错误 settlement 拒绝注入） |
| S-N3 | 黄金对齐残余推进（N-3） | `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`（:101/:134 全事件序列断言）、`CheatGoldenTests.cs`（:644 V4-only 路径产品决策）、`doc/v5-重构/V5-0-GOLDEN-MANIFEST.md`（G-028a 与 20 partial 逐项） | 同左（manifest 行转 accepted/full 或显式决策记录） |

### 9.2 增强建议（均非 G1 门槛项）

| ID | 内容 | 文件范围 | 纳管方式 |
|---|---|---|---|
| S-1 | 依赖方向**精确白名单**增强（补 §3.3 未保护边，I-5 的落地形） | 新增 `dotnet/tests/Mahjong.Architecture.Tests/`（csproj + DependencyDirectionTests.cs）：对 8 个 src 程序集断言**正/负双向白名单**；断言目标只限 Mahjong 号段程序集与显式第三方（Wire→Google.Protobuf、Lobby→Npgsql 合法），**不拒绝 BCL 引用** | 置于 `dotnet/tests/` 即被 `tools/verify-v5.ps1` 自动发现（其头部注释 ：6-8：auto-discovers `*.Tests` under dotnet/tests|src）；可选同时登记 `dotnet/Mahjong.sln` |
| S-2 | Domain 确定性 source-scan 守护（I-4 的落地形） | 新增 `dotnet/tests/Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs`（不改现有测试）：禁用符号组=§3.4 regex；豁免表显式列 `PersistedUtcTime` 消费路径与 ShuffleConfig 数值调参并附理由；内嵌违例自检样本 | 同上自动发现 |
| S-3 | wrapper per-test 明文清单（I-3 的落地形，可选） | `tools/ci/verify-v5-postgres.ps1` 输出增强 | 属 CI 脚本改动，须 caller 批准 |

## 10. 在途兄弟任务（in-flight，未接受）

| 任务 | 固定头处事实（探针 P-M） | 状态 |
|---|---|---|
| Ai `AiCheatView`/`AiSeatSnapshot`/`AiDecisionContext` 纯不可变上下文 + 测试 | 全仓仅 `dotnet/src/Mahjong.Ai/AiCandidateModels.cs:21` 注释引用 V4 类名；无类型/测试落地 | **in-flight 未合入**；不读取未提交产物；不重复下发；SF-1→P4.1 组合根依赖其接受（§7.4） |
| Contracts `AccessCapabilities` 纯契约 + 测试 | 全仓 0 命中 | **in-flight 未合入**；同上 |

caller 本树复证日志（只读引用，SHA 由 caller 记录）：domain-in-tree-test.log `6C58EA84…AB642`、runtime-test.log `8F19FAA4…4D06`、contracts-test.log `C7EE456C…54AB0`、Mahjong.Ai.Tests-test.log `DC404918…B4425`、client-standard-test.log `9AF080E9…67192`。

## 11. REVIEW_INCOMPLETE 范围声明

以下事项本轮无法完全查明，明示范围：

1. **Unity/IL2CPP/macOS/Android/真机/跨物理网络构建**：无当前实测证据（环境缺口未重探查）；按 P2.2 口径以"缺口分列+不宣称通过"呈现（§3.9），不出任何当前 PASS。
2. **真实 PG 两进程重启写读的当前证据**：CI 不含该探针（§5.4）；本地历史记录为 P5.3 历史事实，本轮未重跑（不盲跑重型自测）。
3. **历史 PR 39970580589 根因**：失败框架日志缺失，无法补齐（I-2）。
4. **per-test 名级归因**：受 UTF-16 框架日志限制，采用组合归因（§5.5）。
5. **G-028a 之外 manifest 全部 partial 项的逐项深审**：本轮确认 20 partial 存在并单列（§3.2 L4/L5），未逐项复验每条 partial 的断言边界——逐项推进即 N-3 的执行体，属后续任务而非本轮可闭项。

除上述五项，本报告其余结论均有本轮可复现命令与可复算 SHA256 支撑。

## 12. 证据与命令附录（全部可复制，无占位符）

### 12.1 HEAD 采样语义（R1-5 遗留澄清）

| HEAD | 语义 |
|---|---|
| `8ee22ebf5164…8bd` | 审查对象（冻结源码头），各轮不变 |
| `98d784624fb5…fce4` | 第 2 轮探针执行时的采样 HEAD（第 1 轮报告 commit，返修前） |
| `51cc902dcbc4…f4c` | 第 2 轮最终交付 commit；= 第 3 轮探针采样 HEAD（R3 探针 A 段实测，**历史采样事实**，§12.3） |
| `7c451c673220…941` | 第 3 轮最终交付 commit；= 第 4 轮探针采样 HEAD（R4 探针 B 段 `ACTUAL-HEAD=…` 记录，不与常量比较）；本轮交付将在其上仅追加本报告修改 |
| 交付↔冻结头差异 | 恒仅本报告文件（R4 探针 B 段：`changed-vs-frozen-count=1` 且文件名匹配 V5-1-REVIEW.md；`code-side-changed-count=0`）——源码/测试/tools/黄金 fixtures 面与冻结头逐字节一致，该不变量对任意后续交付 commit 成立 |

### 12.2 当前交付复核（第 4 轮探针：数值退出码 + 审查不变量 HEAD + 独占日志）

脚本 `C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence/run-probes-r4.ps1`（纯 ASCII）：显式 `ASSERT-PASS/ASSERT-FAIL` 令牌 + `$script:assertFail` 失败计数器，**任一断言失败即数值进程 exit 1**，全部通过才 exit 0。对第 3 轮遗留的三处复现缺陷的更正：

1. **数值退出码（不用 `$?`）**：shell 明确为 PowerShell。`powershell -File …` 是 native 调用，随后**立即**把 `$LASTEXITCODE` 保存进变量再输出——`$?` 是布尔（True/False），不得用于退出码报告。脚本 A 段自证语义：`cmd /c exit 0` → `$LASTEXITCODE` 打印 `0`；`cmd /c exit 1` → 打印 `1`（probes-r4 日志 ：3-6，ASSERT-PASS×2）。
2. **HEAD 判断=审查不变量，非硬编码 commit**：B 段对实际 HEAD 只做记录（`ACTUAL-HEAD=<40-hex>`），验证三个不变量——冻结头祖先（`git merge-base --is-ancestor 8ee22eb… HEAD` exit 0）、对冻结头 `git diff --name-only` 恰 1 文件且为 `V5-1-REVIEW.md`、`-- dotnet tests tools assets` 零变化（源码/测试/tools/黄金 fixtures 与冻结头逐字节一致）。不把返修前文档 commit 当交付 HEAD 的必要条件，不用硬编码未来 commit 制造循环——脚本在任意基于冻结头的交付 commit 上均有效。
3. **独占日志**：日志文件名内嵌每次运行新生成的 GUID，不重用 R2/R3 历史日志名；脚本 I 段对 R2 与 R3 历史日志做哈希不变校验。

复现命令（PowerShell，逐行可复制；`$g` 每次运行生成新 GUID 防覆盖）：

```
cd C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence
$g = [guid]::NewGuid().ToString('N').Substring(0,12)
powershell -NoProfile -ExecutionPolicy Bypass -File run-probes-r4.ps1 > "probes-r4-$g.log" 2>&1
$r4Exit = $LASTEXITCODE                 # native 调用后立即保存数值退出码
Write-Output "PROBE-EXIT=$r4Exit"       # 输出数值 0 或 1（非 True/False）
(Get-FileHash "probes-r4-$g.log" -Algorithm SHA256).Hash
```

本轮实测（记录为历史事实；重跑会生成新 GUID 日志，结果应一致）：日志 `probes-r4-9a0ca4c9e583.log`，SHA256 `a2bf068f5d5164910903d0905cadc16ea38d5c44279a5307a0ba7269f7779bfc`，123 行，**21 ASSERT-PASS / 0 ASSERT-FAIL，PROBE-EXIT=0**（数值）；`ACTUAL-HEAD=7c451c6732203a98738b2cc5fe40a4c4ad1cf941`（本轮采样时）；`changed-vs-frozen-count=1`；`code-side-changed-count=0`；尾行 `===== ALL ASSERTIONS HELD (exit 0) =====`。

**断言失败非零语义的隔离负例**（不改真实源码）：`C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence/negative-selftest-r4.ps1` 内含一个故意失败断言，运行即 `ASSERT-FAIL` → `failures=1` → **数值 exit 1**。本轮实测：日志 `negative-selftest-6258b41fec33.log`，SHA256 `fdb777f6f59682d2988b89dc5fe952c90be208cbbbbe713ffec5394afda9252f`，`NEG-NUMERIC-EXIT=1`。

```
cd C:/Users/sncdc/AppData/Local/Temp/g1-review-r4-evidence
$g = [guid]::NewGuid().ToString('N').Substring(0,12)
powershell -NoProfile -ExecutionPolicy Bypass -File negative-selftest-r4.ps1 > "negative-selftest-$g.log" 2>&1
$negExit = $LASTEXITCODE
Write-Output "NEG-PROBE-EXIT=$negExit"   # 期望数值 1
(Get-FileHash "negative-selftest-$g.log" -Algorithm SHA256).Hash
```

**历史采样点（51cc902）如需复现 R3 探针**——用完全隔离的 TEMP worktree，不得切换/重置当前或兄弟工作区（本轮未执行，命令仅作历史采样复现路径；R3 原脚本硬编码 `$repo='D:\majiang-wt\g1-readiness'`，复现前须把复制副本中该行改为下列临时路径，原脚本文件不改动）：

```
git -C D:\majiang-wt\g1-readiness worktree add --detach C:\Users\sncdc\AppData\Local\Temp\g1-r3-replay-20261025 51cc902dcbc46400476f4c753aa69774f94c4f4c
# 在副本脚本中把 $repo 行改为 'C:\Users\sncdc\AppData\Local\Temp\g1-r3-replay-20261025' 后运行；
# 用完清理：git -C D:\majiang-wt\g1-readiness worktree remove C:\Users\sncdc\AppData\Local\Temp\g1-r3-replay-20261025
```

### 12.3 历史采样证据（R2 + R3，均不可变；禁止覆盖/刷新）

| 轮次 | 文件 | SHA256 | 事实 |
|---|---|---|---|
| R2 | `C:/Users/sncdc/AppData/Local/Temp/g1-review-r2-evidence/run-probes.ps1` + `probes.log`（183 行，EXIT=0） | `3138e0b27e61e95a549018bc92ccf5ea360e4d33f154c69ead730bdba209c09a` | 第 2 轮探针历史采样；R3/R4 脚本 I 段哈希不变校验均 ASSERT-PASS |
| R3 | `C:/Users/sncdc/AppData/Local/Temp/g1-review-r3-evidence/run-probes-r3.ps1` + `probes-r3.log`（170 行，14 PASS / 0 FAIL，进程 exit 0） | `146c388923f6427d0527c1806c1c2913e8e78664606649475d394fbfb2b80fe7` | **固定采样 HEAD=51cc902 的历史采样事实**；其 `delivery HEAD is 51cc902` 断言绑定采样点——在后续交付 commit 上复制运行会按断言合同如实 exit 1（caller 独立复现：E=`g1-caller-round3-20261025-045851`，evidence-command-repro.log SHA `B8AB7DA9…D00C`，其余断言全过）；当前交付复核一律走 §12.2 R4 脚本 |

哈希不变校验命令（只读）：

```
(Get-FileHash 'C:\Users\sncdc\AppData\Local\Temp\g1-review-r2-evidence\probes.log' -Algorithm SHA256).Hash
(Get-FileHash 'C:\Users\sncdc\AppData\Local\Temp\g1-review-r3-evidence\probes-r3.log' -Algorithm SHA256).Hash
# R4 探针 I 段实测两值均与上表等值（ASSERT-PASS×2）
```

### 12.4 CI 两 bundle 文件发现与哈希（PowerShell 可直接运行，无省略路径）

```
$base = 'D:\majiang-wt\codex-progress-20261025\artifacts\progress-20261025'
foreach ($bundle in 'ci-push','ci-pr') {
  Get-ChildItem -LiteralPath (Join-Path $base $bundle) -Recurse -File |
    Where-Object { $_.Name -in @('summary.log','run-info.txt','wrapper-summary.json','postgres-evidence.jsonl','dotnetTest.meta.txt') } |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName }
}
```

§5.2 表 10 项 SHA 即上述输出的对应行（`dotnetTest.meta.txt` 每 bundle 7 份共 14 份亦在输出中）。validator 机械层复跑（探针 J 段，PASS exit 0，catalog 文件 SHA `16F7FD7F98…`）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-v5-golden-catalog.ps1 -RepoRoot .
```

### 12.5 关键分段命令（第 3 轮探针 A-J 段核心，均只读；工作目录=审查工作树）

```
# A 段：头/范围
git rev-parse HEAD; git status --porcelain; git diff --stat 8ee22ebf5164f74db75e5a33e87900f5b87cd8bd..HEAD; git diff --check HEAD^ HEAD
# B 段：MapAction 全部出现点（应恰 3 处：1422 AI / 1611 超时 / 4819 定义）
Select-String -Path dotnet\src\Mahjong.TableRuntime\TableActor.cs -Pattern 'MapAction\(' -Encoding UTF8
# C 段：玩家路径直达与守卫（:175 入口 / :1039 守卫 / :1151 直达 / :1140-1146 control epoch）
Select-String -Path dotnet\src\Mahjong.TableRuntime\TableActor.cs -Pattern 'SubmitAsync\(|_round\.Execute' -Encoding UTF8
Select-String -Path dotnet\src\Mahjong.TableRuntime\TableRuntimeContracts.cs -Pattern 'public TableCommandEnvelope\(' -Encoding UTF8 -Context 0,9
# D 段：端到端测试锚点
Select-String -Path dotnet\tests\Mahjong.TableRuntime.Tests\TableStreamWireIntegrationTests.cs -Pattern 'committed_tsumo_emits_typed_settlement_event_and_snapshot|RoundCommand\.Tsumo|harness\.Actor\.SubmitAsync' -Encoding UTF8
Select-String -Path dotnet\tests\Mahjong.TableRuntime.Tests\TableDurableReceiptTests.cs -Pattern 'TerminalOutboxFaultRollsBackAndRetryWritesOnlyOneResult' -Encoding UTF8
Select-String -Path dotnet\tests\Mahjong.TableRuntime.Tests\TableActorTests.cs -Pattern 'DeadlineBoundaryTimeoutWinsOverSameTickAi|AiBeforeDeadlineIsAcceptedAndItsTileIsApplied' -Encoding UTF8
# E 段：三套枚举分离
Select-String -Path dotnet\src\Mahjong.Contracts\ProtocolCommand.cs -Pattern 'SubmitTileMark|SubmitSleeveBloom|SubmitBeamSwap' -Encoding UTF8
Select-String -Path dotnet\src\Mahjong.Domain\DeadlinePolicy.cs -Pattern 'enum LegalActionKind' -Encoding UTF8 -Context 0,12
Select-String -Path dotnet\src\Mahjong.Domain\RoundTypes.cs -Pattern 'enum RoundActionKind' -Encoding UTF8 -Context 0,11
# F 段：域出千六入口
Select-String -Path dotnet\src\Mahjong.Domain\Cheat\CheatSubmission.cs -Pattern 'public static CheatTransition (ApplyTileMark|ActivateSleeveBloom|ChooseSleeveBloom|ExpireSleeveBloomImmediate|ActivateOffTurnSleeveBloom|ChooseBeamSwap)' -Encoding UTF8
# G 段：出千命令 actor 层零接线（期望 NO-MATCH）
Get-ChildItem dotnet\src\Mahjong.TableRuntime -Recurse -Filter *.cs | Select-String -Pattern 'SubmitTileMark|SubmitSleeveBloom|SubmitBeamSwap|CheatCommand|CheatSubmission' -Encoding UTF8
# H 段：计分/AI 状态复检（期望均 NO-MATCH）
Get-ChildItem dotnet\src -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch 'Rules' } | Select-String -Pattern 'YakuEvaluator|WinningScoreEvaluator' -Encoding UTF8
Get-ChildItem dotnet\src -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch 'Mahjong.Ai' } | Select-String -Pattern 'IAiStrategy|AiDecisionService|new BasicAiContext' -Encoding UTF8
```

第 1-2 轮分段命令（依赖方向 8 断言定位、Domain 确定性 grep、Golden 边界、136 守恒、spectator、CI meta 文件名、兄弟任务符号）见交付 `51cc902` 版 §12 第 1-9 组——命令未变、仍可复现，其输出即 R2 `probes.log`（不可变历史证据，§12.3）对应段。

冻结引用：依赖矩阵 `437bd53`；黄金目录 `205506f`（caller 第 13 轮验收集成于 PR19，见 `doc/v5-重构/` 验收文档与 `V5-0-GOLDEN-MANIFEST.md`）。复核协议（只读）：`D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`。

—— 审查代理（实现侧），2026-10-25，第 4 轮返修版。本报告不批准 G1。
