# V5 巡检与 LocalHost 候选动作复验（2026-10-28 Asia/Shanghai）

G1 APPROVED；G2–G5 未批准，V4 仍为生产基线。本轮未接受新的实现代码。

## 基线与隔离

本轮 fetch 后 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；PR19 已合并，不重开。当前后续集成 PR20 的已接受头为 `ed9820adbcf4871850345f7596d4f39b6553f3a9`。

新审查工作树 `D:/majiang-wt/codex-progress-20261028-current` / `wt/codex-progress-20261028-current` 从本轮 origin/v5 创建，fast-forward 到 ed9820a，再将 PR21 候选 `98540d4dbb547952becd441c619e762fcbf6642f` 无提交合入以检查当前集成兼容性。测试后已 `git merge --abort`，正式发布增量只有本报告；未发布候选源码或其它工作树的未提交改动。

PR21 当前为 Draft、目标为 PR20 源分支。它汇总 P4.1 未接受代码及已通过的 P4.1b 存档子项，不能当作整批已接受或已经合入 v5。本轮未合并、未改目标、未另建 PR；后续接受实现仍须由 caller 向当前 v5 集成并记录，不能凭子项 approve 接收整树。

## 独立 fresh 检查

证据根 E：`D:/majiang-wt/codex-progress-20261028-current/build/caller-review/`（ignored）。未修改候选源码、测试、规则或断言。

原样命令（在审查树执行）：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-incremental --artifacts-path build/caller-review/fresh --nologo -v minimal
D:/Unity/dotnet/dotnet.exe exec build/caller-review/fresh/bin/Mahjong.LocalHost.Tests/release/Mahjong.LocalHost.Tests.dll -noLogo -noColor -class Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests -xml build/caller-review/candidates.xml
```

| 检查 | 实际结果 |
|---|---|
| fresh Release build（含 CrashWorker） | PASS，exit0，0 warnings/errors |
| CandidateAcceptanceTests | FAIL，exit1；total7 / passed2 / failed5 / errors0 / skipped0 / not-run0 |
| 吃牌 | `Applied` 期望，实际 `Rejected` |
| 碰牌、明杠 | `Assert.Single`，实际集合为空 |
| 暗杠后岭上自摸 | `SETTLEMENT_MELDSEATMISMATCH, evaluated=1` |
| 立直 | `OperationCanceledException`，后续快照未在既定时限就绪 |

这是 PR21 候选与当前 PR20 合并内容上的定向检查，不是 P4.1 全量验收。P4.1b 历史 29/29、TableRuntime400/400 和 LocalHost 全量60/69均为既有调用方记录，本轮未重跑，不重复累计。其它4项历史全量失败仍未关闭；P4.1c/d 用户手动暂停，本轮不恢复、不复制其工作。

| 原始证据 | SHA256 |
|---|---|
| E/build-localhost.log | `8854757E8104BCA6A795BB55CC23E89A2174F30223FC6FF0EB042DAEBC802679` |
| E/candidates.xml | `29CE9BF4A07E115389E905B545FAECB8ED46EB4AC6E4898A8C283D5A45A78E9E` |
| E/candidates.log | `F80ADF7391A87C38A09ABF092E452902726B2AA4A60B0F18E4AC8957A6EDEE44` |

## 台账与后续执行准备

全局 bridge recent tasks 仅调用一次。失败线程的原始 assessment 均为 `TIMEOUT`（7200s），不属于手动暂停：

| 路由 | 实查活跃 / 目标 | 原线程及本轮动作准备 |
|---|---:|---|
| DeepSeek Flash | 1 / 2 | B2b `thread_20261027_230256_426c` R7 reworking；保留原路由，不重复派发。 |
| GLM Flash | 0 / 1 | `thread_20261027_230302_93fb` failed；源码未提交、未正式交付，准备原线程 retry，不能接受其在途实现。 |
| Anti 默认模型 | 0 / 1 | P4.1b 已 completed/MR_RECORDED；P4.1a `thread_20261027_154438_fc96` failed，准备将本轮5项候选动作失败一次交回原线程，保留已接受存档与暂停范围。 |
| 合计 | **1 / 4** | 两次原线程调用成功后预计3/4；最终状态以调用结果为准，不把准备当成功。 |

模型发现已确认 Claude Code 默认 `glm-5.3-flash`，Anti 默认 `gemini-3.8-flash-high`；Anti 调用不指定 model。第二个 DeepSeek 槽位留空：P4.2-B/P4.5 等待完整 accepted LocalHost 与 B2b；完整远端 P5.4b 后续批次等待 accepted 持久宿主；P6.1/P6.2 等待 G2+G3；P7 等待 G4。失败 Anti 任务不改派 DeepSeek 填槽。

持久宿主当前未交付，完整功能审查记 REVIEW_INCOMPLETE；只原路重试超时任务，不编造产品缺陷清单。LocalHost 定向5项均为同一既有候选动作闭环范围，不扩展成另一单机任务；其它功能/安全/并发维度本轮未完整重审，不批准任务。

PR20 当前 ed9820a 的两次 CI API 均 completed/success：[40577165874](https://github.com/liuchangready/majiang-game/actions/runs/40577165874)、[40577154569](https://github.com/liuchangready/majiang-game/actions/runs/40577154569)。本轮仅核对 API/headSha，未下载其原始产物，不宣称重审了全部测试；历史9136508双CI原始证据已在既有报告接受。旧缺失 framework log 的历史失败仍 UNRESOLVED。

Windows 调度仍为 PT30M / IgnoreNew，complete.flag 不存在。181 classified仍未覆盖；Android完整游戏/真机与跨物理网络 NOT_RUN，iOS BLOCKED。Android Wire AOT APK历史组件通过不代表完整游戏/设备通过。未部署、未批准G2–G5、未创建complete.flag。
