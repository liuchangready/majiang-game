# PR20 7fe7dbf 双 CI：调用方独立复核

日期：2026-10-31（Asia/Shanghai）。本记录接受审计报告的证据结论，不批准 PR20 整批或 G2–G5。V4 仍为生产基线，G1 既有批准有效。

## 对象与隔离集成

- accepted `origin/v5`：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（本轮 fetch 后核对）。
- 审计冻结源码：`7fe7dbf3e37aefb437132d3584cb2ff09cff6af5`，现有 Draft PR20 源分支 `feat/v5-post-g1-batch`。
- 接收者交付：`ae0df3eb3984c0f5b442f4c8079e7f190a86de18`，tree `18505d972dcaf3828570205a8f12a63d81a05935`。
- 调用方独占树：`D:/majiang-wt/caller-ci7fe-review-20261031`，先从 fetched origin/v5 创建，再 ff 到冻结头与审计交付。集成 tree 与接收者一致；审计 delta 仅指定报告 603 行，`git diff --check 7fe7dbf..ae0df3e` exit 0，tracked clean。
- `git diff --quiet 7fe7dbf ae0df3e -- dotnet tools unity .github` exit 0。本次不包含 B2/P4.5 未提交工作，不修改主 checkout、receiver、Unity 环境或默认分支。

## 实际复核

调用方脚本 `build/caller-review/audit.ps1`、日志 `audit.log`、结果 `result.json` 均在本树 ignored build。脚本 exit 0，2,713 项断言通过；首次探针发现 PowerShell 长路径限制及已记录重复展示名后修正审计脚本，不修改原始证据。重复展示名在接收者报告中已有明确分类。

| 检查 | 实测结果 |
|---|---|
| 接收者清单 | 630/630 文件 bytes/SHA256 匹配 |
| 在线 GitHub API | push [41181934433](https://github.com/liuchangready/majiang-game/actions/runs/41181934433)、PR [41181937456](https://github.com/liuchangready/majiang-game/actions/runs/41181937456) 均 completed/success，attempt 1，API head 均为完整 7fe7dbf |
| API artifact digest | 两 ZIP SHA256 与在线 API digest 一致 |
| 原生 direct XML | 每 run 14 测试工程、2,732 case，全部 Pass；0 fail/skip/error；native test ID 唯一 |
| 展示名 | 每 run 2,730 unique project/name；Architecture 与 HTTPS Admission 各一组同名；多重集合两 run 无差异 |
| PostgreSQL 原生 XML | 每 run table 42/42、targeted 13/13 |
| crash / emptyDB restore reader | stateVersion=1、lastEventSeq=2、eventCount=2、commandCount=1、Applied receipt、terminal/outbox/recovered=true；ownerEpoch 2→3 |
| 子清单 | 检查并执行接收者只读 manifest verifier：每 run table 152、lobby PG 12、plugin 19，全部匹配；ZIP 269 entries 与解包 269 files 一致，exit 0 |
| 本轮源码测试 | NOT_RUN；本任务只有文档，直接复核上述真实 CI 逐例产物，未重复无变化全量 |

`outboxPublished=false` 为 writer 在发布前被终止的场景结果，不是 outbox 已发布。原始失败历史和其他平台证据均未改写。调用方结果 SHA256：`A4719D90EF111D27E574DD0DDFAEF20447CFD2143477D21E48116F714A50C725`；调用方脚本 SHA256：`FCC9F2F44D8A828B474C7FA970F6F9F38EC25CE99E1FE4200643EE7E4C458AC5`。完整输入清单：接收者 `build/ci-7fe-audit/EVIDENCE-SHA256.txt`，SHA256 `44CAF1016B56CED339F30472D7182D9F1858E05FFA20CA8151E18C0C5ED3104B`。

## 结论与剩余依赖

审计报告的限定结论独立复核 PASS；本次未发现需要返修该报告的 BLOCKER/MAJOR。在线 API、原始 XML、哈希及隔离集成均已经核对。

桥接器 `approve_task(thread_20261031_154943_30fd)` 拒绝：`no active reviewer role is held`，要求 current reviewer turn；本定时会话不绕过、不重试角色限制，ledger 保持 waiting_review。此前 helper 线程 `thread_20261031_141347_580a` 的同类角色限制仍未解决。代码/文档复核结论与 ledger approval 分开记录。复用唯一 Draft PR20 发布本审计与本复核记录，MR 记录不代表 approve_task 成功；不新建 PR、不合并 v5。

继承缺口保持：D-A68-02 MAJOR（CI direct，缺 `dotnet test` 逐例原生结果）、D-A68-03 MAJOR（历史 1,573 whitespace）、companion 仅 4/11 与历史 blob pins 未运行。新文档 publication head 的 CI 未在本轮验收，不把冻结头绿外推新头。

任务台账本轮只全局读取一次：DeepSeek running 1/target 2（B2 `thread_20261031_124526_0eef`），GLM target 0 / Anti target 0，legacy active 0。P4.5 `thread_20261031_024102_96e6` 第15轮 waiting_review；同类缺陷未修且无新代码，按复核协议 §3 等待人工裁决，不自动返修或复制。P4.1 两个旧 failed 线程不自动重启。B2 单局可信终局验收后才进入 service match 编排。

准备的独立下一任务仅关闭 D-A68-02：保留 direct 严格验证，新增真实 dotnet test 逐例结果捕获/校验及可复现负例，不改产品和现有测试断言，不虚构 XML 或用 direct 代替 dotnet test。实际派发成功与任务 ID 以桥接器返回为准。

G2–G5 未批准；classified 181 未覆盖；Android FAIL / iOS BLOCKED / 真机与跨物理网 NOT_RUN。未创建 complete.flag，未部署/公开发布。
