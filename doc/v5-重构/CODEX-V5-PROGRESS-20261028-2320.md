# V5 进度检查与 PR20 e27f48b 双 CI 独立复核

日期：2026-10-28，Asia/Shanghai。调用者：Codex；本轮检查始于 23:11 左右。

结论：已接受发布头 `e27f48bfa9dbde0ced8b6148dc0eda1aaf484041` 的 push/PR 双 CI 原始证据复核 **PASS**。本轮只新增本报告，没有新 receiver 交付需要批准，没有派单、返修、恢复或改派。G1 APPROVED；G2–G5 未批准；V4 仍生产。

## 基线与范围

- 实际 fetch 后 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；PR19 已合并，G1 九门批准记录已读取，不重开。
- 本批继续沿用唯一 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，源 `feat/v5-post-g1-batch`、目标 `v5`。冻结其已接受头 e27f48b，在独占 `D:/majiang-wt/codex-progress-20261028-2330` / `wt/v5-codex-progress-20261028-2330` 审查；`origin/v5` 为 HEAD 祖先，exit 0，初始 tracked clean。
- 已读取本树 AGENTS.md、迁移方案、V5-0 至 V5-5 任务书、复核协议、G1 批准记录和最近进度/CI/插件来源验收记录；不按主 checkout 的旧 G1 状态决策。
- 主 checkout 的 CLAUDE.md、doc/ui/round-preparation/PROMPTS.md 和两份未跟踪 V5 文档保留；未修改 receiver、规划树、Unity 环境/Resources/Burst。LocalHost [PR21](https://github.com/liuchangready/majiang-game/pull/21) 仍 OPEN，已发布头 `026a4888d6b9df921411fee3f8dfc5b81ab3670b`，目标 PR20 源分支；不把其在途源码纳入本次检查/发布。

## 全局任务快照与配比

本轮全局 `list_recent_tasks` **一次**，跨工作区返回最近 100 项，按实际 receiver/model 计数；queued 也占名额。没有 waiting_review。没有轮询 receiver 进度。

| 路由 | active / target | 原线程及状态 |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | **2 / 2** | `thread_20261027_154459_1c10` P4.1c，R5 reworking；`thread_20261027_154500_f0b7` P4.1d，R1 queued |
| Claude Code / glm-5.3-flash | **2 / 1** | `thread_20261027_230302_93fb` P5.4b-A 持久宿主，R2 reworking；`thread_20261028_223624_e4ae` P4.2-B 真实 LocalHost 装配，R1 running |
| Anti / configured default | **0 / 1** | mesh 实际报告 exhausted / 429，恢复时间 `2026-11-01T16:54:27.999Z` |
| 总计 | **4 / 4** | 保留已有路由，不为调配比打断/复制任务，不加入第五项 |

P4.2-B 的原派单明确记录用户指定 Claude Code，本轮没有创造第二个 GLM 名额或改派授权。P4.1c/d 在同一 LocalHost 树，d 等该树执行槽，不称物理并行。原 P4.1 R36 failed 的未完义务由既有 a/b/c/d 子任务承接；a/b 已完成且 PR21/MR 记录存在，c/d 未通过，不复制旧失败线程。旧契约准备 failed 也已有后续已接受替代产物。本轮未恢复任何手动暂停任务。

## 最新双 CI 的实际证据

证据根 E：本树 ignored `artifacts/codex-progress-20261028-2330/`。使用 `gh run view --json`、完整 `--log`、`gh run download` 和 `gh api` 获取 run/artifact/PR 合成提交；成功命令原生 exit 0。这是远端业务测试的原始证据复核，未冒充本机重跑业务全量。

| 项 | push | pull_request |
|---|---|---|
| run | [40709681005](https://github.com/liuchangready/majiang-game/actions/runs/40709681005) | [40709695163](https://github.com/liuchangready/majiang-game/actions/runs/40709695163) |
| API head_sha | e27f48b | e27f48b |
| 实际 checkout / artifact head | `e27f48bfa9dbde0ced8b6148dc0eda1aaf484041` | `d34eed1d6e973fcaf2c9e608a5f28d453bf62c86` |
| API 结果 | completed / success / attempt 1 | completed / success / attempt 1 |
| XML 自行求和 | **2,299 passed / 0 failed / 0 skipped** | **2,299 passed / 0 failed / 0 skipped** |
| PG full / targeted | **42/42、13/13** | **42/42、13/13** |
| ZIP bytes / entries | 4,302,718 / 243 | 4,303,165 / 243 |
| ZIP SHA256 | `03F4A468EDED9EBCEDF3FD3F5852BC53D8D2F94B541B7FA48BABDE4979B52C6D` | `4085CDB1BD4207A877F8E887AE2F1357EE7901055BC92C216AA3B7AD44816BCB` |

PR 合成提交双父精确为 8fd7c6c、e27f48b；tree 为 `44833b1440172aa675478e139d655fcaab13e91e`，与本地 reviewed head tree 一致。API head_sha 不冒充 PR checkout，也不把 GitHub 合成提交当作已合并 v5。

每 run 的 11 个 direct runner 原始 XML 分别为 Ai444、Architecture92、Client182、Client.Wss80、Contracts149、Domain529、Infrastructure42、Lobby89、TableRuntime400、TableService152、Unity.TableModel140，合计 2,299。逐项结构、test 节点、collection/assembly 计数、meta 的 head/XML SHA/native exit、console 计数相符，errors/notRun 也为 0。PG42 已包含在 2,299 内；定向13与各阶段不能相加冒充独立用例总数。

本轮实际独立检查：

| 检查与命令 | 本轮结果 |
|---|---|
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File E/audit.ps1` | **397 checks / 0 failures / exit 0**。从当前生产 verifier 提取真实 XML 解析函数；22 个 XML 与 console/meta、两份 PG full/targeted、wrapper、强杀/恢复/空库 restore、278 条 table manifest 哈希/长度全部核对。 |
| 同参数运行 `E/verify-download.ps1` | **exit 0**。两个 ZIP 的 SHA/长度同时符合 artifact API 与 job log，全部 486 个文件内容与已解包证据逐项一致。 |
| 同参数运行 `E/verify-provenance.ps1` | **56 checks / 0 failures / exit 0**。两 run 的强制清输出 Release provenance 各 131 inputs / 19 outputs / 5 packages，全部19输出重哈希正确，精确12-DLL闭包成立；时间位于对应实际 run 内，solution/tests/worker build 均0 warning/error，四伴随样本门通过。 |
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-direct-results.ps1` | **19/19 / exit 0**，本机真实执行 XML 门禁正负矩阵。证据 `artifacts/direct-results-selftest-acaae9a57f184c7886898a5a7a700d7f/`。 |
| 同参数运行 `tools/ci/test-v5-native-exit.ps1` | **6/6 / exit 0**，本机真实执行0/1/2/17、超时、forced writer退出传播。证据 `artifacts/native-exit-1a172ae38cd14f1685adbd680a161a19/`。 |

两个 PG16.10 run 均验证 committed writer 被强杀、PostgreSQL 真正重启、新 reader 恢复 `stateVersion=1/eventSeq=2/eventCount=2`、原 Applied receipt、cheat 状态、未发布 outbox；空库 restore 的 ownerEpoch 从2变3，outbox SHA一致，table阶段全PASS，Lobby清理rootDeleted=true。本轮未连接/启动/清理本机 PG。

ZIP复核最初尝试 Python，但本机仅有不可执行的商店 alias，native9009、没有执行验证；保留 `python-attempt.txt` 与初次脚本，改用 .NET 二进制流下载/校验后 exit0。未修改生产工具、被审代码或远端证据。

573 个证据文件写入不自引用的 `E/evidence-sha256.csv`（含两个ZIP、完整job/API、所有解包证据、本机探针及脚本），manifest SHA256：`6AF7D1DB44B95CDDA22B2880F479F9BBCF2C52D05BAD24E362C1129819794E88`。其中 audit-result / provenance-audit / download-audit 的 SHA256 分别为 `E7A20EC4BC5A8AB96BC58378FCD66BD5BAE7BBE491BEB457379C25759FC15B42`、`49B82714735F3085D62F38205EAC5ED9DEAFE9FEC0DF069C58EFBD65814B307B`、`7C0C3F2E5C69240C623D5015A1559877937CAF02F9FBF237D4DA85DDE2F3559F`。

## 门槛、缺口与下一依赖

- **PASS**：本次 e27f48b 双 CI 证据及本机工具边界探针。插件来源修复已于 e27f48b 独立接受并登记PR20，本轮不重复批准该任务。此次新远端证据关闭其“最新发布头 CI 待验”，不扩大组件批准范围。
- **未关闭**：181 classified 仍未覆盖；历史39970580589缺framework log根因、D-PG-SUP-02四失败归因仍 UNRESOLVED。10/11 direct runner测试DLL SHA未记录、crash reader未直接记录136/stateHash/ledgerHash的已有证据限制保留；131输入/19共享输出provenance不等于补齐测试DLL或crash状态证明。
- **关键路径**：P4.1c 第三响应窗同源Ron/振听正反证据返修、P4.1d控制回执/持久outbox竞争闭环 → 完整LocalHost接受 → 原P4.2-B真实装配/验收 → P4.5/G2。P4.2-B当前仅可做已授权且不借用未接受host的准备，不能提前宣布真实本地局通过。
- **远端路径**：原P5.4b-A R2处理已发出的四项完整缺陷（恢复投影锚点、持久宿主受控WSS、空stdout竞态、长路径清理），接受后才推进后续真实整场/大厅对账/G3。此次现有TableService/PG组件绿不替代这条持久宿主端到端验收。
- **BLOCKED / NOT_RUN**：Anti配额耗尽；P6.1/P6.2等G2+G3，P7等G4。Unity/Player/IL2CPP/Android/iOS/设备/跨物理网络本轮NOT_RUN；历史Android完整阶段FAIL、iOS环境BLOCKED、设备NOT_RUN分别保留，不由本轮CI改写。

调度器实际 Running、PT30M、IgnoreNew；complete.flag 不存在，未创建。受管增量仅本报告，`git diff --check`通过；沿用PR20正常发布，不合并v5、不部署、不纳入在途代码。报告提交后触发的新CI属于新文档头，结果另计；这里的PASS仅对应上述精确受测头及run。
