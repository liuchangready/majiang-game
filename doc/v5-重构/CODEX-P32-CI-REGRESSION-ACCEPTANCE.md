# P3.2 CI 流调度补修独立验收

日期：2026-10-24。任务：`thread_20261024_045327_5e21`。交付：`0e3fa527676aa9af0b61932bf9d9b28f7533916b`，含前轮 `8e6b7ca`，起点 `32f645e`。调用者已通过 Bridge `approve_task` 归档；本记录只验收该组件，不宣布完整 P3.2 或 G1/G5 通过。

## 结论与变更

原测试把 `SubmitAsync`/首次订阅返回当作下游已送达。恢复 fence 和新 binding 可以合法丢弃尚未认领的旧帧，故该假设会导致真实 CI 失败。本补修先等待旧 update/baseline 送达，再测试恢复或换绑；另以 `BeforeDispatchClaim` 暂停旧帧，确定性验证 fence/replacement 先发生的分支。

第二轮增加默认关闭的 internal `ChannelCreated` / `ObservePumpExit` / `PumpExit` 观测接口。测试捕获暂停的 pump invocation，release 后等待该 invocation 在 `PumpLoopAsync` 返回后的 `finally` 完成，随后才断言旧 sink 帧数。旧 handle 的 Sync/Unsubscribe 拒绝和新流 Sync/后续 update 均有断言，阻塞 TCS 的释放放在覆盖 arm/订阅/等待/断言的 `finally` 中。生产 close/fence/dispatch claim/rebaseline 逻辑未改。

`Close` 安排的终止通知本身不能证明旧发送泵完成；验收依据是 pump 完成屏障。交付文档中“Displaced 同步发出”的措辞应理解为关闭时安排通知，实际通知经 `Task.Run` 派发，不影响本次屏障结论。

审查相对起点的全部五个文件；未发现 verifier、skip、超时阈值或生产校验放宽。交付 16 项 SHA256 清单全部匹配；mutation A/B/C 原始日志分别证实放行关闭 binding、放行旧 generation、丢失恢复 baseline 会使新增反例失败。调用者既有集成证据另含两项 mutation 检出记录，源码已恢复；当前复验重新构建并通过。

## 当前 reviewer 实跑

SDK 为 `D:/Unity/dotnet/dotnet.exe`（8.0.426），Release。以下命令均 exit 0。

| 工作树 / 提交 | 命令 | 结果 |
|---|---|---|
| `D:/majiang-wt/p32-ci-regression` / `0e3fa52` | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild --nologo -v:minimal` | 0 警告 / 0 错误 |
| 同上 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal` | 152 通过 / 0 失败 / 0 跳过 |
| 同上 | `dotnet build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | 12 项目，0 警告 / 0 错误 |
| `D:/majiang-wt/codex-stream-review-20261024` / `66d410734c24a6c3dc8955c4e9a1428c7ce48cff` | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal` | 152 / 0 / 0 |
| 同上 | `dotnet test dotnet/Mahjong.sln -c Release --nologo -v:minimal` | Domain 322 + Contracts 104 + Client 132 + AI 14 = 572 / 0 / 0 |
| 两树 | `git diff --check`（交付/集成范围） | PASS |

当前集成复验合计 **724 / 0 / 0**。solution 不包含 TableRuntime、WSS 和 Lobby 的测试工程，故不得把 solution 的 572 项写成统一全量。首次尝试把 `-t:Rebuild` 直接传给 `dotnet test` 被 CLI 当作测试选项，产生 MSB1008；未执行测试，随后改为上表独立 build + test。此调用错误不作为产品失败或通过证据。

原始日志：任务树 `artifacts-codex-p32-acceptance-{rebuild,tests,solution}.log`；集成树 `artifacts-p32-acceptance-integrated-{tableruntime,solution}.log`。哈希清单在集成树 `artifacts/codex-stream-review/acceptance-evidence-sha256.csv`。

## 既有集成证据与边界

同一精确集成提交 `66d4107` 已留存的调用者证据位于 `artifacts/codex-integrated-realpg/`：`run-info.txt` 记录该 HEAD；严格 verifier 七个测试工程为 Client 132、Domain 322、AI 14、Lobby 89、Contracts 104、TableRuntime 152、WSS 80，合计 **893 / 0 / 0**。两个 helper 只计构建，12 DLL scratch copy 通过。`pg/wrapper-summary.json` 记录本地 PowerShell 5.1 wrapper exit 0、独占 loopback PG `55465`、迁移/清理/环境恢复通过且自有根已删除。这里是对已有归档的核对，当前 reviewer 没有再次启动 PG，不把该记录冒充新的远端 CI。

远端 `205506f` 的 push `40027686851` 与 PR `40027695129` 仍是 FAIL；此前 `40020648672` 的调度失败及其余历史 FAIL 继续保留。本任务不验收兄弟线程的 PowerShell 7 环境恢复修复，不合入其待审变更。真实新 CI 必须按实际 run 另行评估。

## 唯一 MR 交付

复用 [PR #19](https://github.com/liuchangready/majiang-game/pull/19)，源 `feat/v5-1-integration`，目标 `v5`，远端 `origin`。已存在的 caller 候选 `66d4107` 是已发布 `205506f` 与本交付的合并；相对远端仅本任务五文件，随后只增加本验收记录。物理黄金样本及此前未推送的独立证据已在 `205506f` 发布，本次全部保留。

调用者核对账本：本补修已 completed；CI 环境恢复仍 waiting_review，AI 黄金样本与目录核对仍 running，均不纳入本次补修。调用者统一正常推送并更新既有 PR，不新建子任务 MR、不 force push。发布后的精确 SHA 与 MR 状态由 `record_merge_request` 回写同一 thread。

V4 仍是唯一生产。完整 actor/Wire 联合接线、P3.4、G1/G5、Unity/IL2CPP、Android/iOS 真机和跨物理网络未由本验收建立通过结论。
