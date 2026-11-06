# PR20 2a3a359 CI 审计的调用方验收

日期：2026-10-31，Asia/Shanghai。验收对象为 DeepSeek 线程 `thread_20261031_061612_8a2d`，报告提交 `3b4d1fad74d66368a5a188df22792e3bb726fd25`，父提交为已接受 `origin/v5` 的 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。

**该报告任务 PASS。** 调用方独立复核原始证据后，在 `D:/majiang-wt/caller-pr20-2a3-audit-integration-20261031` 从 accepted v5 建立隔离集成树，快进到现有 PR20 冻结头 `2a3a359412fd555bcbb78dec119531c3bde35713`，合入报告。合入提交 `edfb07a3549cef26a3f65ca0d8565e290eb51a3a` 对该冻结头仅新增报告；`dotnet/tools/tests/unity/.github` 的 `git diff --exit-code` 为 0。调用方仅勘误报告摘要的一处措辞：两个 API head_sha 相同，实际 checkout SHA 不同。

## 独立核验

证据根 R 为 `D:/majiang-wt/pr20-2a3-ci-audit-ds-20261031/build/caller-ci-review-20261031/`。只读原始证据，调用方脚本及结果写入 ignored build；没有重跑产品测试、PG、CI、Unity 或 Player。

| 检查 | 实际结果 |
|---|---|
| GitHub API 再核对 | [push 41108922475](https://github.com/liuchangready/majiang-game/actions/runs/41108922475) 与 [PR 41108932296](https://github.com/liuchangready/majiang-game/actions/runs/41108932296) 均 completed/success、attempt 1、源 SHA 2a3a359。 |
| `powershell -File R/verify.ps1` | native 0；553 项清单逐项 bytes/SHA256 全匹配，无重复路径；manifest 自身 SHA 为 `FF5A2D97DDDC4ED6748D5B5745DE0A1EC2B884B375FE3CF05922B6C9AE74852D`。 |
| ZIP 原始字节与解包闭包 | 每 run 269 个文件，逐项比较 ZIP stream 与解包文件 SHA；archive digest 与保存的 API metadata 相同，解包无额外文件。 |
| XML / native / summary | 每 run 14 个工程与冻结 tree 工程清单对应；2723 total/pass、0 fail/skip/notRun/errors；每个 test 为 Pass，无重复 id；meta exit0、checkout 与 XML SHA、summary 计数一致；两次测试名称多重集无差异。 |
| 合成提交 | PR checkout `e2a03503df3c413789620c4c31e6327a1afc48aa` 的 parents 为 accepted 8fd7c6c 与 source 2a3a359；与源 tree 均为 `6bd4d4e414de809c1cd630a1c70976023962985f`。 |
| PG 清单与 wrapper | 每 run 表侧 152 pins、Lobby 12 pins 均匹配；表 wrapper exit0、预期 kill 之外 stages exit0；Lobby plannedExit0、无失败阶段、环境恢复及目录清理字段为 true。远端清理仍为 wrapper 自证。 |
| `powershell -File R/supplement.ps1` | native 0；每 run 12 构建日志均 0 warning/error 与 native0；19 插件 output 哈希/长度一致，149 input 条目、7 own assemblies、5 package 条目；6/6 native-exit case。 |
| PG 原始结果 | 每 run 表侧 XML 42/42、targeted13/13，两个 recovery reader 的 recovered/terminal/outbox/cheat 恢复字段均 true；Lobby evidence14条。13 是42的子集，42/89已在2723分母中，不另累加。 |
| 范围 | 接收方 clean，仅一份报告提交；集成不包含两条 P4.5 在途修改；主 checkout 的用户未提交文件未改。 |

首次 caller `verify.ps1` 因 PowerShell 对 JSON 数组再次包装而错误判定 table pin count，native1；原脚本和说明保存在 R/verify-first.ps1 与 first-failure.txt。修正调用方脚本的数组处理及 wrapper exit 属性后执行上表成功结果；接收方证据和产品校验器未修改。一次只读 Git tree 查询也因 PowerShell 对未加引号的 `^{tree}` 解析失败，改为带引号参数后确认 tree；不是产品失败。

| R 下文件 | SHA256 |
|---|---|
| verify.ps1 | `BB0AEF90E348B978594EF1E062162C5BDFA09A4BE6EADD4EB8A9A5BD12046FB4` |
| result.json | `FCED1CFBFBC87FA522E3CB66ED0E844EEF2F2147E9886BE53DB7032DA25B95D7` |
| verify.log | `4DCD818B700A02CD0BE0BE1D0240284FC3CB7BC289A07AC487A5E82AAB6BD053` |
| supplement.ps1 | `6FF13282F2F5B034363D37ED56ED6A268CC8A385EA28127F0DC2E7DDE4119B17` |
| supplement-result.json | `7F676345F2D07A6E5C2980F9966E287A3FF54484BA58DB108B54F1B98CE4DADA` |
| supplement.log | `F7C2095581F0E37053AD5E2A18F68B91A3EFFB98531EA12D7066995C28D9B157` |

## 调度与阶段边界

本轮全局 recent ledger 只读取一次。该次快照中 DeepSeek active/target **2/2**，GLM target0、Anti target0，二者派发禁用，仍活跃的 legacy 任务为0，总 active/ceiling **2/2**：

- `thread_20261031_024102_96e6`：整场驱动 round5 reworking，遗留 D-1-05 的 driver 负例、原请求恢复对账及真实换局停顿根因待交付。
- `thread_20261031_024102_cdf8`：恢复证据导出 round4 queued，遗留 D-3-01 可信采集绕过待交付；queued计入占用。

本轮不派发、重试、恢复、暂停或改派任何线程，不探测 GLM/Anti。两项 failed 历史线程保留原状态，其范围不另建任务。该待审 CI 报告完成审查不释放上述两个执行名额。

G1 APPROVED；G2/G3/G4/G5 未批准，P4.5 仍待两项交付及真实组合验证。报告中的 golden freeze pins NOT_RUN、平台/网络 NOT_RUN、历史失败根因 UNKNOWN 继续有效；181 classified 未覆盖。报告任务通过不构成整个 PR20、完整整场、恢复可信来源、G2/G3 或生产部署批准。

只向同批既有草稿 [PR20](https://github.com/liuchangready/majiang-game/pull/20) 发布报告及本验收记录，不合并 v5。发布后新头触发的 CI 必须单列实际状态，旧冻结头绿色只在实现树等价范围内适用。V4 继续生产；本轮只读核实计划 enabled/PT30M/IgnoreNew，complete.flag 不存在。
