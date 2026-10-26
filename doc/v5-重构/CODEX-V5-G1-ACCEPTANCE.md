# V5 G1 最终裁决登记

日期：2026-10-26（Asia/Shanghai）。裁决方：Codex caller，依据用户“完成 G1 最终裁决登记”的明确指令。

**裁决：G1 APPROVED，九门 9 PASS / 0 FAIL / 0 BLOCKED / 0 NOT_RUN。**

判定对象为已发布集成头 `9fe3a8d4c5b4f7d14ef7055c71a50fe99beed448`。本记录是阶段批准，不再只是组件或报告验收；它更新当前 G1 状态，历史报告中的“尚未批准”仍保留其当时语境。本次新增裁决文件，既有实现、规则、样本、台账及校验器不变。

批准范围：V5-1 共享核心、Contracts/Wire、TableRuntime 及其已接受客户端增量。放行任务书规定的 V5-2/V5-3 后续实施。**V4 仍是唯一生产基线；G2/G3/G4/G5 未通过，不创建 complete.flag，不据此宣称 V5 已上线。**

## 1. 判定依据及增量适用性

- [任务书 G1 与 N3 收口规则](V5-0-1-IMPLEMENTATION-TASKS.md)：只按既有九门判定，classified 不计 full，不因更细粒度重新打开已关闭 census。
- [最终九门汇合报告](V5-G1-FINAL-READINESS-20261026.md)及其[调用者验收](CODEX-V5-G1-FINAL-READINESS-ACCEPTANCE.md)：交付 `b04134d` 已独立验收，原线程 `thread_20261026_144755_d25e` 已 COMPLETED；本文件作出其明确留给 caller 的最终裁决。
- [运行时验收](CODEX-V5-G1-RUNTIME-REVIEW-ACCEPTANCE.md)、[门 2 验收](CODEX-V5-G1-GOLDEN-REVIEW-ANTI-ACCEPTANCE.md)、[客户端队列验收](CODEX-V5-P54A-SESSION-OPERATIONS-ACCEPTANCE.md)、结算拒绝回归及三项维护守护已汇入现有 PR19。
- 本轮独立验证上述输入头及 `71a1fbb`、`d9d17d8`、`27111a2`、`2d6eb6a`、`77c990d` 均为判定对象祖先。
- 最新完整 CI 对应 `c5698412c44b70565ecf4fc89846f998194cbf30`。该头到判定头仅新增最终报告和报告验收两份文档；`dotnet/tools/tests/unity/.github` 的 `git diff --exit-code` 为 0，因此其实现、测试、校验和构建配置证据适用于本裁决对象。本次文档提交亦保持此等价关系。

## 2. 九门最终裁决

| 门 | 既定条件 | 最终判定 | 适用证据与边界 |
|---|---|---|---|
| 1 | dotnet test 独立全绿 | PASS | 已完成 push/PR 双 CI 各 1,922 passed、0 failed、0 skipped；本轮复核原始框架日志、退出码和证据哈希，并确认实现树等价。 |
| 2 | 已导出黄金对齐、未导出单列 | PASS | 采用已接受的字段级硬收口规则：592 = 411 bound + 181 classified + 0 pending；181 classified 继续属于未覆盖分类，不冒充 full。六基准与四伴随分母隔离。 |
| 3 | 依赖方向静态测试 | PASS | 已接受的八模块精确白名单及独立 78/78 证据；最新双 CI 已包含该维护增量。 |
| 4 | Domain 无隐式随机、时钟、浮点权威 | PASS | 运行时审查及已接受确定性 source-scan；12/12 定向证据已复核，Domain 生产内容未改变。 |
| 5 | 三绝技与 136 张物理守恒 | PASS | N-1a/N-1b、N-2 计算及权威宿主接线、黄金收口和守恒验收已汇合；AuthorityUnavailable 三项拒绝/对照及既有 N2 22 项证据维持有效。宿主组合根仍属 P4/P5。 |
| 6 | Wire 可重复生成 | PASS | 既有 SchemaCompatibilityTests 含 protoc 代码及 descriptor 重复生成断言；Contracts 149 项包含在完整 CI 中。 |
| 7 | 座位及观战私有性 | PASS | 按座位/公共投影、AI 可见观察及观战命令前置拒绝的已接受证据；不把公开流投影通过等同远端服务已实现。 |
| 8 | TableRuntime 并发、版本、恢复合同 | PASS | TableRuntime 400 项完整 CI 与权威队列、控制代次、回执、租约、原子持久端口、冷恢复合同验收；真实桌事件 PostgreSQL 实现属于 P5.2。 |
| 9 | 可用 Unity/IL2CPP 构建日志与缺口分列 | PASS（既定口径） | P2.2 的 .NET、Editor、Windows IL2CPP 构建/运行证据已接受；Android FAIL、iOS BLOCKED 和设备 NOT_RUN 继续单列，不转为平台 PASS。 |

九门合计恰 9 项。旧 `V5-1-REVIEW.md`、分门审查和最终汇合报告共同保留完整审查历史；本文件是当前阶段批准记录，不覆盖或改写旧证据。

## 3. 本轮实际复核

独占工作树：`D:/majiang-wt/g1-final-decision-20261026`，分支 `wt/v5-g1-final-decision-20261026`，从上述判定头创建。原主 checkout 的 `CLAUDE.md` 与 `doc/ui/round-preparation/PROMPTS.md` 未修改。

证据根 E：`build/g1-decision-20261026/`（ignored）。

| 检查 | 本轮结果 | 实际范围 |
|---|---|---|
| `powershell -File E/audit-evidence.ps1` | native 0，AUDIT-PASS | 逐项重哈希既存 144 份证据；每 run 8 份原始框架日志、8 份 native meta、12 项 PG pins，wrapper 全阶段成功；九个已接受输入祖先、实现树等价、592 行向量及既有本地 XML 计数通过。 |
| 原六基准目录校验 | native 0 | 6 fixtures / 73 requirements / 10,389 cases / 10,000 random / 45 Facts；full17 / partial21 / none32 / V5-only3 是目录覆盖标签，不代替字段级收口。 |
| `powershell -File tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins` | native 0 | 当前四份伴随样本及四份历史 freeze blob 全部通过；未并入六基准分母。 |
| Git 增量与内容检查 | native 0 | 判定头与已完成 CI 实现树相同；本次仅新增裁决记录，diff-check 通过。 |

原始双 CI 为远端历史实跑、本轮读取复核：

- [push 40287034965](https://github.com/liuchangready/majiang-game/actions/runs/40287034965)：精确头 `c569841`，1,922/0/0。
- [PR 40287040814](https://github.com/liuchangready/majiang-game/actions/runs/40287040814)：合成 checkout `7c4998f56007caa5353a4113a26491dcd6e62181`，parents 为 `457274c` 与 `c569841`，1,922/0/0。API head_sha 标源分支提交，不把它误认作合成 checkout。

这不是本轮新执行 1,922 项、PostgreSQL、Unity/IL2CPP 或真机测试。PG 产物覆盖 Lobby 存储，不等同尚未实现的 P5.2 桌事件存储。判定头上自动触发的更新 CI 按各自实际状态记录，不冒充已完成；批准依据为上述已成功 CI 和实现树等价。

本轮初次以 LF 检出的新工作树运行旧 catalog 校验器时，`ai-v1` 原始长度及 exporter 原始 SHA 校验失败（native 1，保留 E/catalog.log）。尝试 `git -c core.autocrlf=true checkout-index --all --force` 仍未恢复逐文件既定字节形态，第二次 native 1 保留为 E/catalog-crlf.log，不称成功。最终仅对六 fixture 及六 exporter 枚举 LF/CRLF 表示，要求候选原始 SHA 精确匹配既有 catalog pin 才恢复该表示；12/12 pin 匹配、逐文件规范文本相同、canonical Git diff 为 0（E/restore-pinned-eol.ps1、pinned-eol.json、pinned-eol.log）。原校验器随后在相同内容上复验通过（E/catalog-pinned.log）。未修改文本内容、pin 或 verifier，前两次失败不被覆盖。

校验结束后，检出表示恢复为判定头 Git blob 的原始字节，避免将任何 EOL 差异带入提交；本次受管净变更仅此裁决文件。以下日志为本轮真实文件字节 SHA256，不自引用本报告：

| E 下证据 | SHA256 |
|---|---|
| audit-evidence.ps1 | `803E4040A142326EDB5C312FE0E03E1B2A96C48F70AC09398E815B4C92DDDD19` |
| audit.json | `E6CEA14D9CAE16F82FEABE620B3C910172F2D0F5C7154926F671416BE1889D29` |
| audit.log | `E62CCC86E029D348E4D77B4C43E3B81411948C8F90CE582DC89411C74140AF88` |
| catalog-pinned.log | `477A14B241030198CACB1850CEFC4A01869C004BC1A8CD606983723E8FA4B15E` |
| companion.log | `2840A98BF1FE562A198B38ADD80B8A5C9FB2C9734376D2B398463C21BE2CB84B` |
| pinned-eol.json | `985176C1233B40D26F1586C4A73742D3C3CC998B5DBB2759CAC0F3530C61CD69` |

## 4. 保留项及后续责任

1. **181 classified 仍未覆盖**，原理由、owner、复访条件和冻结分母有效；不因 G1 批准升级为 bound/full。
2. 原台账 verifier 依赖外部 census、七份历史 ignored 文件与指定 EOL 字节布局。最终汇合树中其 native 1 与 TEMP 缺失记录原样保留；35/35 EOL 内容证明不冒充该次 exit 0。本次不修改或放宽校验器，不重开已接受硬收口。
3. Android：sdkmanager `0xC0000409`、Ninja 缺失导致 APK 构建 FAIL，runtime NOT_RUN；iOS：缺 macOS/Xcode，BLOCKED。设备、跨物理网及 V4 T4 缺口继续追踪至相应平台验收。
4. 历史 CI `39970580589` 根因仍 UNRESOLVED，后续绿 run 不证明其根因已关闭。
5. LocalHost 目前为空壳；`TableSessionOperationQueue` 接入 Unity 组合根、本地整场及恢复、真实 WSS 桌服务、PostgreSQL 桌事件存储、账户资产可信入口均按 P4/P5 任务实施，不写成已具备。
6. 伴随 verifier 的统一 CI 接入是后续维护项，独立工具通过不冒充统一入口已接入。

## 5. 放行、合并窗口与调度登记

从本裁决起，P4.1、P4.2 核心、P5.1、P5.2 具备阶段入口：

- P4.1 使用已接受补充协议的静态 `LocalTableHost.CreateChannelAsync` 工厂及内部 `LocalTableChannel`，复用既有已登记工程。
- P4.2 核心可用 unavailable 工厂独立推进；P4.2-B 等真实 P4.1 工厂。P4.3/P4.4/P4.5 继续等待自己的上游交付。
- P5.1 可先用冻结票据合同和假签发器验准入；P5.2 只依赖已接受持久端口/schema，不等待票据。新工程的共享 sln 登记由集成方串行处理。

定时任务读取本记录后按既定四槽配额执行：DeepSeek Harness 2、GLM 1、Anti 1。不得再次以历史报告“G1 未批准”阻塞已放行任务，也不重复派发九门汇合审查。

任务书规定 G1 后第一个集成窗口、至迟 24 小时内完成 PR19 合并到 `v5`。后续责任人为 caller；应在 **2026-10-27 15:50（Asia/Shanghai）前**完成最终差异/检查及合并登记，遇新阻断须如实记录。本次用户授权范围为最终裁决登记：先将本文件发布到既有 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，记录合并窗口，不在本次执行 PR 合并、部署或公开发布。

本裁决记录、PR 状态和 Bridge MR 登记由 caller 同步；保留原任务线程和证据，不创建新验收代理任务。G5 前定时计划保持启用，complete.flag 必须不存在。
