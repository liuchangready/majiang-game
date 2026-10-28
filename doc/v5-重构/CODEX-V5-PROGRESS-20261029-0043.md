# V5 进度检查与 c78e6bc 双 CI 原始证据复核

日期：2026-10-29，Asia/Shanghai。G1 APPROVED；G2–G5 未批准；V4 继续生产。

## 基线与范围

已读取 AGENTS、最新接受树中的迁移方案、全阶段任务书、返修协议和 G1/最新进度记录。fetch 后 origin/v5 仍为 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，PR19 已合并，不重开。PR20 是当前后续批次唯一目标 v5 的开放审查 PR，源 `feat/v5-post-g1-batch`，本轮核验头 `c78e6bc172abba507dd10b07346e6d76496078f1`。

独占工作树 `D:/majiang-wt/codex-ci-20261029-0050` / `wt/v5-codex-ci-20261029-0050` 从已 fetch 的 origin/v5 创建，再 fast-forward 到 c78e6bc；祖先检查 exit0，初始 tracked clean。主目录仍在旧 feat/v5-1-integration，原 CLAUDE.md、PROMPTS.md 修改和两份未跟踪 V5 文档均保留。没有导入 receiver 未提交代码、PR21 或 P4.2-B 候选；本轮产品源码零修改。

## 一次全局账本快照

本轮仅调用一次 list_recent_tasks（最近100项，跨工作区），没有轮询线程进度。queued 计入活动数。

| 路由 | active / target | 任务与实际状态 |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | 3 / 2 | `thread_20261029_001656_6c2f` P4.1d 独立复核 running；`thread_20261029_001657_f4f1` P4.2-B 准备独立复核 running；`thread_20261027_154500_f0b7` 原 P4.1d R2 queued |
| Claude Code / glm-5.3-flash | 1 / 1 | `thread_20261027_230302_93fb` P5.4b-A R2 reworking |
| Anti / 配置默认 | 0 / 1 | 空位；总上限已满，本轮不新增或改派 |
| 总计 | 4 / 4 | 保留既有线程、模型与排队返修；没有新 dispatch/rework/resume/retarget |

DeepSeek 超目标1项是本次开始前已有的独立复核加原线程排队组合；不通过取消/改派在途任务机械凑配额，也不以 Anti 空位新增第五项。历史 Anti 限额记录仍保留，本轮未重新探测其可用性，不把历史429当本轮新实测。

waiting_review 的 `thread_20261028_223624_e4ae` 是原 GLM P4.2-B，交付 `8878bd4` 只完成 UI/导航准备并明确 BLOCKED_DEPENDENCY；已有上述 DeepSeek 独立复核输入任务，caller 待其交付后完整裁决，本轮不重复派单或批准。P4.1a/b/c 和架构修复的 completed/MR义务已查阅；原失败 P4.1 草案由 a/b/c/d 承接，不复制或恢复旧线程。

## 新增独立证据：c78e6bc 双 CI PASS

| run | API source head | 实际 checkout | 审核结果 |
|---|---|---|---|
| [push 40723694741](https://github.com/liuchangready/majiang-game/actions/runs/40723694741) | c78e6bc | c78e6bc | PASS |
| [PR 40723706972](https://github.com/liuchangready/majiang-game/actions/runs/40723706972) | c78e6bc | `3c17cd928cd3f2ddf0bf99560a526f703e2fd184` | PASS |

GitHub commit API 确认合成提交双父精确为 origin/v5 8fd7c6c、c78e6bc；合成 tree 与源提交 tree 均为 `411ab898d0cef9328fc48de49ef356ff18458e62`。没有混淆 API head 和真实 checkout。

两份 artifact（`v5-verify-logs-40723694741-1` / `v5-verify-logs-40723706972-1`）及完整 job log 已实际下载，各命令 native exit0。本轮独立解析每份11个 direct XML：逐个确认非零、test 节点数等于 total、所有节点 Pass、failed/skipped/errors/not-run 均0，再与每个 `runs/<project>/direct.meta.txt` 的 native exit0、total、XML SHA256逐项匹配。

| 测试工程 | push 实际通过 | PR 实际通过 |
|---|---:|---:|
| Ai | 444 | 444 |
| Architecture | 130 | 130 |
| Client | 182 | 182 |
| Client.Wss | 80 | 80 |
| Contracts | 149 | 149 |
| Domain | 529 | 529 |
| Infrastructure | 42 | 42 |
| LobbyService | 89 | 89 |
| TableRuntime | 400 | 400 |
| TableService | 152 | 152 |
| Unity.TableModel | 140 | 140 |
| 合计 | 2337/2337 | 2337/2337 |

两 run 的 solution build exit0、共享12 DLL scratch copy PASS、四伴随 fixture gate PASS、统一 OVERALL PASS。companion 的历史 Git blob pin 检查仍为 NOT_RUN（未请求/获取历史对象），不把当前字节校验扩大为历史 pin 验证。此处为远端原始产物复核，不是本机重新执行2337项。

PG16.10 wrapper 两 run 均 exit0、所有阶段 PASS；另逐节点读取 PG 原生 XML，full42/42、targeted13/13，0 failed/skipped/errors/not-run。42项已在统一2337中，13项是重复定向执行，不加为新的独立用例。原始 PostgreSQL recovery log 均含不正常关闭、redo starts 和 ready 行；writer 被杀后 reader 及 pg_dump/pg_restore 空库 reader 的 stateVersion=1、lastEventSeq=2、eventCount=2、commandCount=1、receipt=Applied、cheatVersion=1、outboxSha256 均对应，恢复 ownerEpoch 分别2/3。这只核实记录的字段与阶段，不扩大为未跑的完整持久 WSS 宿主/整场对账。

复现命令（cwd 为本独占工作树；下载命令与审计均实际执行，native exit0）：

```powershell
gh run view 40723706972 --json headSha,event,status,conclusion,jobs
gh run view 40723694741 --json headSha,event,status,conclusion,jobs
gh run download 40723706972 -n v5-verify-logs-40723706972-1 -D artifacts/c78-dual-ci/pr
gh run download 40723694741 -n v5-verify-logs-40723694741-1 -D artifacts/c78-dual-ci/push
gh run view 40723706972 --log
gh run view 40723694741 --log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File artifacts/c78-dual-ci/audit.ps1
```

证据根 `D:/majiang-wt/codex-ci-20261029-0050/artifacts/c78-dual-ci/`；含原始XML/log/meta/PG记录、API合成提交、可执行 audit.ps1、audit-result.json。314份关键证据的 `evidence-sha256.csv` SHA256 为 `3F3560CB4F2894F76A1B1FD1E5ECA9C5E58035F44C2EA5F90E7B00CF7309734B`；清单范围为根文件、unified顶层/direct XML/逐项meta、PG顶层，明确不含 table/tmp 编译临时目录，也不是整份artifact逐文件认证。

## 门禁与下一依赖

当前 c78e6bc 的远端统一门禁 PASS 不销掉 PR21 c600611 的原冷读失败/黄金变更，亦不证明完整 LocalHost 集成通过。P4.1d 原 R2 正在排队修复已记录的 decision 退休后持久控制回执重放缺陷；独立审查输入完成后仍需 caller 根据实际最终交付做全范围裁决。完整 LocalHost 接受及集成阻断关闭后，原 GLM P4.2-B 才可在同线程激活真实工厂与运行联调，再推进 P4.5/G2。PR21 仍为已有 Draft 子批次，目标 PR20 源分支，不另建重复 PR。

远端 P5.4b-A R2 持久宿主修复尚未交付验收，后续真实 Lobby HTTPS签票、多客户端整场、结算/未知结果对账仍待该依赖，再到 G3。P6.1/P6.2 等待 G2+G3，P7 等待 G4。

181 classified 仍未覆盖；历史39970580589、D-PG-SUP-02归因及既有跨构建DLL/hash证据限制保留。Android既有历史FAIL/后续构建记录各自保留，iOS环境BLOCKED、真实设备与跨物理网络NOT_RUN；本轮没有平台/Unity/IL2CPP/设备实跑。scheduler 本轮实测 Running、PT30M、IgnoreNew；complete.flag 不存在。没有合并到v5、部署或批准G2–G5。本报告仅汇入现有PR20；文档发布后的新头须与本报告已验c78e6bc分开，不沿用旧SHA的CI结论。
