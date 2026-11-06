# P5.4b-A 第四轮通知复核记录（任务已进入第五轮）

2026-10-29。任务始终为 `thread_20261027_230302_93fb`；未新建桥接任务、原生会话或转派。

**结论：REVIEW_INCOMPLETE，保留账本已有的第五轮 REWORKING。此次不 approve、不重复 request_rework、不推送或修改 PR。** 复核期间再次查询账本，发现原 caller 已于 03:00:47 UTC 下发第五轮，要求现有 mapper owner 关闭遗留 MAJOR `D-LH-GATE-01`（控制载荷冻结字段 20/21 与 mapper 旧字段 1 不一致）。第四轮回调已过时，不能据此完成最新任务。

冻结交付为 `50f352c964134f8914677180222c52c366e19329`，基线 `c0f16eb77c70296978f658dd5382b9288d556e05`。本次开始时 tracked clean，第四轮净改动六个路径；全任务净改动 23 个路径，diff-check 0。caller 本地收口树从 PR20 头 `e3ddd83` 汇合该冻结提交，merge commit `2a0c8fbb48f4d4f4f60fdee9359599e3d126cfa1`。

## 已完成验证及证据限制

- receiver r10 的 manifest **220/220** 个文件长度及 SHA256 一致；四份原生 XML 节点及摘要一致：152/400/35/49，共 **636/636**，failed/skipped/errors/not-run 均 0。
- 原任务树实际执行完整官方 runner，六工程 fresh Release build，四套回归 636/636，全部 WSS 正负阶段和新增 claimed-opening 强杀恢复阶段均记录 PASS，wrapper native exit **0**；集群已归属核验清理。
- **该次运行不能充作完整冻结提交验收**：运行后段发现第五轮已在原任务树修改 `TableCommandMapper.cs`（11:05:35 本地时间）及 `PostgresDurableTableHostTests.cs`（11:07:56），TableService/测试 DLL 也于 11:07:38/11:08:18 被另行重建。初始 fresh-build 与后续宿主运行可能使用不同产物。保留全部结果，不将这次 exit 0 冒称 `50f352c` 的完整不变产物证明。
- 该次新增链实际记录 PID **1872 → 17840**、boot 不同、claim/checkpoint version 2、旧回执 Found、claim ledger 1/APPLIED 3、后续真实动作 version 4、136/Unassigned 0、首弃不在河、副露 `2:124,125,127`、事件事实保持、strict WSS。此处仅记原始观测及上述限制。
- 随后创建独占 detached 冻结树 `D:/majiang-wt/cx54f-471550af`。确认账本第五轮正在执行后，停止该额外旧提交复跑：只终止经父 PID/命令行核验的自身 PersistenceWorker build；wrapper exit 1，原因为 reviewer 主动取消，**不是产品编译缺陷**；测试 NOT_RUN。`CANCELLED.txt` 保留原因，finally 已清理集群，58341–58344 无监听。未停止 receiver 或其他任务进程，未覆盖其未提交改动。

首跑命令（cwd 为原任务树）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-durable-table-host.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 58141 -HostPort1 58142 -HostPort2 58143 -HostPort3 58144 -Root artifacts/codex-p54b-r4-review-77518cef343d4794b2bc4f2efe49d970/cluster -LogDir artifacts/codex-p54b-r4-review-77518cef343d4794b2bc4f2efe49d970/logs
```

首跑证据根：`D:/majiang-wt/p54b-durable-host-glm-20261027/artifacts/codex-p54b-r4-review-77518cef343d4794b2bc4f2efe49d970`，含 `runner.log`、`independent-audit.json` 和 `logs/`。manifest 220 项独立核对无差异。

| 文件（logs/） | SHA256 |
|---|---|
| wrapper-summary.json | E3923C8695053DF475AF9615863425E47C6631BBDF3254553D7D6B8DE8622965 |
| manifest.sha256.json | 00416C5AEF72D26F514FD25A98795B00E9554A818B5F2E43D2A53037F77FE3F3 |
| claimed-phase1.json | F87B757FF0F705C3670FA1211E5E119D6CDBD34A22BD037F7A3286F9A69A9430 |
| claimed-phase2.json | EA68825C939B92EA253E56E52BF6EE052808B303624419188CA7C9CA9656F16C |
| tests-durablehost.xml | 8D598A718D1CB95CCD00C508C4B9311AE76962023E187C5CB4FF022ED82D50A5 |

## caller 收口准备与批次门禁

隔离树 `D:/majiang-wt/cx54-89b473c1` 的本地提交 `cdcb03163ef76ce5de1d3bc467727a3362d4a38a` 仅登记两个 solution 工程、精确 `TableService → Infrastructure` 声明/程序集依赖及反向拒绝测试，并在既有 table PG wrapper 的 unified 路径提供独立 `mahjong_p54b_test`/未迁移 `mahjong_p54b_empty` 和五个可恢复环境变量。未修改统一 verifier 或领域实现。该提交尚未接受或发布，后续 caller 可按第五轮最终交付决定是否复用。

fresh Architecture build native 0、0 warnings/errors；**132/132**，零 failed/skipped/errors/not-run。证据 `artifacts/caller-c492edcccf66411a8de373238589dda7`。

实际执行 table PG wrapper `-RunUnifiedVerification`（表 58241、Lobby 58242、独占目录）。Infrastructure **42/42**、定向 **13/13**、强杀数据库重启/冷读取/备份恢复均 PASS；统一 verifier **17 PASS / 1 FAIL**，companion **4/4 PASS**，wrapper native **1**，集群清理完成。12 份有效原生 XML 独立解析合计 **2490/2490**，全部节点 Pass、摘要及 meta native exit 0 一致，包括 TableRuntime 448、Architecture 132、LocalHost 102、TableService 152。该次测试启动时 metadata HEAD 为 merge commit `2a0c8fb`，含后来原样提交为 `cdcb031` 的 caller 三文件改动；未把工作树增量冒充旧 HEAD。

唯一 FAIL 为 DurableHost：统一 verifier `Invoke-Captured` 在 PowerShell 5.1 下将预期 `RECOVERY_LEASE_HELD` stderr 当作 `RemoteException`，调用退出值为空，未正常收齐 XML。此行不是 0 测试 PASS，也不计入 2490。批次门禁保持失败，不据此否决冻结组件，也不冒称集成通过。

独立最小探针已复现同一生产捕获函数：子进程打印 stdout/stderr 后真实 native exit **0**，`Invoke-Captured` 却返回 **null**；`stderr-probe.json` 的 `reproduced=true`，探针 exit 0。仅写 ignored artifacts，未改生产捕获函数。

批次证据：`artifacts/batch-05905b12cc/`，包含 `independent-xml-audit.json`、`unified/summary.log`、唯一失败调用原文、全部 XML/meta 和 table wrapper 日志；stderr 探针在上述 `caller-c492...` 目录。首次失败未删除、未通过重试改绿。

现有唯一 Draft PR20 仍为 <https://github.com/liuchangready/majiang-game/pull/20>，本次未推送、未另建 MR、未记录未发生的 synced。待第五轮真实交付及完整独立验收后再统一收口。V4 生产、G1 既有批准、G2–G5 未批准、181 classified 缺口不变；生产 TLS、跨物理网络、完整远端整场、账户资产结算、Unity/设备均未在此验收。
