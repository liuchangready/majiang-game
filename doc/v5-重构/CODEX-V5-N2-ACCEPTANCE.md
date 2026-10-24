# V5 N-2 caller 独立验收与统一 PR19

日期：2026-10-25。Bridge：`thread_20261025_051932_b6be`；callerSessionId：`rollout-2026-10-25T05-11-20-01a16fc0-f9c1-7751-8e86-78d03bdf28ce`。沿用原任务，未新建任务/会话或转派。

## 结论与范围

**N-2 权威结算计算组件及 N-1a 集成复验 PASS；N-2 整体/G1/G5 仍未通过，V4 唯一生产。**

交付基线 `8254c9b783740ddf519cebc15bb50a8f9974cf58`，三个本地实现/返修提交 `d916cc0`、`47bb07a`、`954b566a177cf6ba818a7aee3bc9fbe37b3fb524`。交付仅五个 expectedFiles，clean status、diff-check 均通过。R1/R2 的历史组件 PASS 已作废；其失败日志保留，不以本轮结果覆盖。

caller 核对计算入口、不可变模型、物理绑定、既有 evaluator/支付复用、源 roundId/version/fingerprint、Prepare/Install、终态和守恒闸，实跑原始 R1/R2 探针。D-2-01 四风多舍现拒绝，合法四风仍可执行；D-2-02 旧 Pon/本人旧暗杠不永久取消新立直一发，立直后的显式打断与岭上一发仍拒绝。Ron 振听、非法字牌 Chi、天/地和矛盾等既有负例保持。

不新增网络、数据库、公开流或 ID/时间/随机来源。deadline、租约、控制代次、幂等与冷恢复仍由既有宿主处理，本组件没有新增该层实现或平台验收。黄金入口只覆盖可表示子集，不能把全量 10,238 样本称为新入口覆盖。

## 独立验证与集成修正

CLI：`D:/Unity/dotnet/dotnet.exe`。独立接收树：`D:/majiang-wt/n2-settlement-calculator`；隔离集成树：`D:/majiang-wt/codex-n2-integration-20261025`。

| 验证 | 原始 R3 交付 | 最终集成 |
|---|---|---|
| Release solution build `--no-incremental` | 0 warnings / 0 errors，exit0 | 0 warnings / 0 errors，exit0 |
| Domain Release 全量 | 350 passed / 0 failed / 0 skipped，exit0 | 374 passed / 0 failed / 0 skipped，exit0 |
| TableRuntime Release 全量，显式构建 | 313 / 0 / 0，exit0 | 316 / 0 / 0，exit0 |
| 结算 direct runner | Domain 全量包含 28 项 | 28 passed / 0 errors / 0 failed / 0 skipped / 0 Not Run，exit0 |
| R1 原探针 | 9 标签拒绝，BEHAVIOR_FAILURES=0，exit0 | 同样通过 |
| R2 原探针 | 5 组，BEHAVIOR_FAILURES=0，exit0 | 同样通过 |

集成已发布并独立验收的 N-1a 头 `3bfe952cc7adb9bdc2f5bb0a7589a26ec731886b`，合并候选 `21a560485d1fa69e2118c0ad7316c352bacace27`。首次集成 Domain **373 passed / 1 failed / 0 skipped，exit1**，不冒充通过。

**D-3-01 [MAJOR，集成信息，已修复]**：`CheckedOverflowAndWrongHandFactsAreRejectedWithoutMutation` 的初始分数 `[long.MaxValue,1,1,1]` 被 N-1a 新增 `RoundStateMachine.SumScores` checked 总分校验提前拒绝，尚未进入计算器。原冻结基线没有该总分校验，故这是集成后发现的新信息。

caller 立即对原 thread 调用 `request_rework`，附完整复现、失败日志、修复要求与断言；Bridge 返回 `no active reviewer role is held`，拒绝已归档任务的该次 review action。此前 `approve_task` 只依据原基线组件全绿，不能代替集成验收。未新建任务/会话或转派，也未把失败候选推送。

caller 在自己的隔离集成树仅修正新增测试输入为 `[long.MaxValue,-long.MaxValue,25000,25000]`：初始 checked 总分为 50,000，源 round 可合法创建，结算支付才触发赢家单席溢出并返回 `ArithmeticOverflow`。保留既有拒绝/错手牌断言，补充源 fingerprint/phase/tiles/events 不变断言；不改状态机、生产计算器、规则、fixtures 或 verifier。最终全量与原探针按上表重新实际执行。

最终黄金实测：胜例 checked=300 / skipped=1013 / scanned=2237；无和例 checked=100 / skipped=393 / scanned=1211。这里的 skipped 是样本映射过滤计数，测试框架 skipped=0。

复现命令（在对应工作树执行）：

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class '*RoundSettlement*' -showLiveOutput -noLogo
# 最终集成探针：复制原探针源码到独占 TEMP，仅将 HintPath 指向集成树新构建 DLL
& D:/Unity/dotnet/dotnet.exe run --project <证据根>/integrated-probes-r1/Probes.csproj -c Release
& D:/Unity/dotnet/dotnet.exe run --project <证据根>/integrated-probes-r2/Probes.csproj -c Release
```

## 原始日志与 SHA256

证据根：`C:/Users/sncdc/AppData/Local/Temp/n2-caller-r3-review-f21472d2860048469e9b64b458189f37/`。

| 文件 | SHA256 |
|---|---|
| domain.log（原 R3） | B35B1C82BACB48E6329C2C853D5A7CD681AF9E478D4AC4964F1F5FA1B7AE1C4B |
| runtime.log（原 R3） | 0EA3081CD71FE450C9BEEEE9D321D71601E4EB860603C72BC9E767FAD21029A5 |
| build.log（原 R3） | DF5595F5AFC75225939ACC25433F6D17DC4E9C81230ACE5B509EFB0689A97FE2 |
| integrated-domain.log（历史失败） | 1843A851F304793EC19DE90968AA723254A4E106DC053F25D92AE7DEF3BE75AC |
| integrated-domain-native.log（历史堆栈） | 8A91010F20D14626F287F1925FB45CF1F772CE4B9A6986E1F99CCA71165F099F |
| final-build.log | F9484E1AD23781C76CE814E279D014DC7BD71E9199D0987128C7A46929967884 |
| final-domain.log | 7AB554FA464159E3D1DE87B5F5A0634C5CD7C581580B6925BFE332DEFB0B2968 |
| final-runtime.log | FD6D43E47000AE2338198E962252F4EBDF39D3EFF04FEAC0E3CC5F8605167D49 |
| final-settlement-direct.log | B85D334BBBC270A72D6CFC33B6C0C83A273854258CE07DA70F687D1F63F7C59D |
| final-r1-probes.log | AE2A5DF0713DF23E5F4D8FC2121048275CA9F3BF68F4B01942657457D65477A6 |
| final-r2-probes.log | 699F6A72B3FFE04D39E9DEC3EE0C5FACE0ACFAC02B5EA6329BDAE0D9B5B74971 |

## 发布与后续边界

复用唯一开放 PR19 `feat/v5-1-integration -> v5`，汇总三个本地交付提交、caller 集成测试修正与验收记录，一次普通快进 push，不新建子任务 MR，不合并 PR 或部署。账本确认同批 N-1a 已发布；N-1b running、N-3 reworking，未验收产物不纳入本次发布。发布 SHA、远端核对和同步结果记入同 thread 的 `record_merge_request`。

N-2 整体的宿主接线、旧自带 settlement 入口收口、裸命令 source 绑定及可信历史认证仍未关闭。N-1a 已新增杠宝实体层翻宝能力；R3 原组件报告的该项缺口描述针对其旧基线，不能覆盖最新集成状态。事件序列/投影与 N-3 审计另行验收，不因本组件付款结果对齐而宣称完成。

真实 PG/WSS、Unity/IL2CPP、Android/iOS/设备、跨物理网络、V4 全量及本次远端 CI 均 NOT_RUN。历史失败继续为 FAIL；最终组件/集成回归为 PASS；N-2 整体/G1/G5 仍未通过。
