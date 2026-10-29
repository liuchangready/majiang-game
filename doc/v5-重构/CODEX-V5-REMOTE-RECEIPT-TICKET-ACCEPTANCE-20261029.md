# V5 远端回执与同 assignment 新票：调用方验收

2026-10-29，Asia/Shanghai。线程 `thread_20261029_145100_544c`，沿用原任务账本，未新建任务、转派或启动子 Agent。

本任务范围 **PASS / COMPLETED**，MCP `approve_task` 已成功归档。B1 完整 HTTPS Lobby/WSS/PG 集成、Unity 装配及 G2–G5 的状态分别保留，不从本次窄修复推断通过。

## 审查对象与判断

- 冻结 PR20 输入：`734062914adb6f23029f2630a6245ef5e2b4e23e`。
- 实现：`9f223ef9e08111ddb7f364d69c9ace25a2e5584a`；交付：`add8d7df9bbdaba528cadc23c244f9413f9e8d1e`。
- 原工作树：`D:/majiang-wt/remote-receipt-ticket-ds-20261029-1446`，tracked clean，`git diff --check 7340629..HEAD` 原生退出 0。
- 两个生产文件、五个相关测试/辅助文件及交付报告，共八文件。接受测试路径登记调整：新增 `ReceiptScopeContractTests.cs` / `TestSupport/TableReceiptAssertions.cs`，扩展现有 SessionFlow、RuntimeDecision、registry lifecycle 测试，均直接验证本任务缺陷。无 Client、Contracts、TableRuntime、Lobby、统一 verifier 或 Unity 生产越界修改。

B1-BLOCKER-01/01b：`BuildReceipt` 从服务端准入身份重新构造独立 scope，保留完整 owner 字段，不引用客户端可变对象。普通请求 wire digest 使用 Client outbox 的 MJC1 `CanonicalPayloadDigest`；actor 持久幂等仍使用原 raw digest。实际 socket 测试覆盖 Applied/Rejected、控制回执、原始字节重放、冲突及非法 scope；Recorded 的补充反射测试明确不冒充网络证据。ReceiptLookup 保留 requestId/correlation，不把当前连接 scope 伪装为被查询历史 scope。

B1-BLOCKER-02：同 assignment、相同 account/role/room/seat 的新 jti 可以准入；exact jti 在 expiry+skew 前仍拒绝。不同 tuple 在资源写入前 fail-closed；assignment retention 取最大值。已有 commit/WELCOME 后 eviction/rollback/旧 session release 与乱序 A/B/C handover 保持。新增矩阵覆盖释放后重连、在场接管、失败 WELCOME、tuple 四负例、expiry、容量及确定性交错；真实 Kestrel 测试验证旧连接 4403、旧 jti 重放 4401。

空 Close code 症状本任务未复现，保留 UNRESOLVED/NOT_RUN；独立 SpectatorSession/LobbySession 公共类型与跨实例持久票据消费不属本次修复。内存 registry 结果不能证明跨进程一次性消费安全。

## 调用方在当前任务工作区实际运行

证据 C：`artifacts/codex-receipt-ticket-review-c62dfd653a964aa8a904c06e4877f591/`，位于原任务工作树。四项目各自 fresh Release 构建，随后顺序直接运行新测试 DLL，无并发构建。

```powershell
dotnet build dotnet/tests/Mahjong.<Suite>.Tests/Mahjong.<Suite>.Tests.csproj -c Release --no-incremental -nr:false
dotnet dotnet/tests/Mahjong.<Suite>.Tests/bin/Release/net8.0/Mahjong.<Suite>.Tests.dll -noLogo -reporter default -result-xml <C>/<Suite>.xml
```

| Suite | total / passed | failed / errors / skipped / not-run | native exit |
|---|---:|---|---:|
| TableService | 167 / 167 | 0 / 0 / 0 / 0 | 0 |
| Client | 182 / 182 | 0 / 0 / 0 / 0 | 0 |
| TableRuntime | 448 / 448 | 0 / 0 / 0 / 0 | 0 |
| Architecture | 132 / 132 | 0 / 0 / 0 / 0 | 0 |

共 **929/929**；四次构建均原生退出 0、0 warning/error。XML 逐测试节点、Pass 数量与 assembly 汇总一致，`counts.json` 保留完整 SHA256。构建、标准流、XML 和退出码均保留。

重新审查并 fresh 构建运行 ignored `artifacts/receipt-ticket-probe/ReceiptTicketProbe.csproj`：生产 Client `TableSession` + `FileOutboxStore` + 生产 Kestrel TableService，实际 `Applied`、Pending=0、Unresolved=0，build/run 原生退出均为 0。它使用组件 signer 和 loopback WebSocket adapter，未使用生产 TLS 通道或真实 Lobby 签票，不声明完整 B1 通过。

receiver manifest 的 36 个非 bin 源码、报告和原始证据条目重新核对长度/SHA256，36/36 一致。fresh rebuild 会替换本机 bin，旧 bin 哈希不作为本轮产物证明；调用方另存 fresh 证据。C/component-manifest.json SHA256：`f8c7351a17417946591d71f02e523422fe5462f2237f34067fd7d7267614bc63`。原报告中的冻结输入两缺陷复现日志通过 manifest 核对，本次未冒充调用方重新运行旧输入。

## 同批统一收口

本次只使用既有 Draft PR20：`feat/v5-post-g1-batch → v5`。从 fetched PR20 头 `7340629` 建立隔离工作树 `D:/majiang-wt/receipt-batch-a9a74d09`，汇集本任务 `add8d7d` 与同批已接受、尚未推送的 LocalHost 拒绝回执 `4a08dd783ebff2d49f53a723ca3ad84dbe56851e`（线程 `thread_20261029_064512_04b1`，账本 COMPLETED）。未混入仍在返修的完整 B1 或 Unity 装配改动。

实现汇合头 `b93d2a36d4a5ed2fcaf0c6670852a81e2899013f`；两个组件的相关生产/测试路径分别与各自已接受输入 `git diff --quiet` 为 0，无冲突或额外源码修改。相对 PR20 冻结头净改十三文件，4721 insertions / 45 deletions。

LocalHost 组件原验收的限制保持：冷 OS 进程用原 scope 只读查询、真实 Client/FileOutbox 对账可以完成；原键冷 COMMAND 重放在当前准入契约下不可达，明确不计 PASS，不用新 scope 的新键命令替代。其验收记录来自既有账本，本次不操作该线程。

批次证据 B：`D:/majiang-wt/receipt-batch-a9a74d09/artifacts/codex-batch-82d7eefd/`。在实现汇合头上执行既有脚本，仅新增本验收文档，源码和测试无未提交改动。命令如下：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 25480 -LobbyPort 25481 -Root D:/majiang-wt/receipt-batch-a9a74d09/artifacts/codex-batch-82d7eefd/table-pg -LobbyRoot C:/Users/sncdc/AppData/Local/Temp/mahjong-receipt-batch-a30c4b8234b04d5f87393b213f7df524 -LogDir D:/majiang-wt/receipt-batch-a9a74d09/artifacts/codex-batch-82d7eefd/table -UnifiedLogDir D:/majiang-wt/receipt-batch-a9a74d09/artifacts/codex-batch-82d7eefd/unified -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip -RunUnifiedVerification
```

两端口先用 loopback listener 确认空闲且互异；table cluster 的绝对路径在隔离工作树 artifacts 内，lobby cluster 在 TEMP 内，执行前均不存在。官方 PG16.10 ZIP 的 SHA256 重新核对为 `ebb3b6af4fa69dea9951b66855bc4d42dc04e56ccb9aa7024ce3c58bd89d6b0c`。未修改所有权清理保护、项目发现、manifest、skip 门禁或插件复制检查。组件批准不等于批次可合并批准。

## 批次门禁：FAIL，独立于本任务 PASS

既有 table wrapper、lobby wrapper、统一 verifier 均原生退出 **1**。统一 verifier 是 **17 PASS / 1 FAIL / 0 BLOCKED / 0 NOT_RUN**，共 18 个项目行；13 个实际测试程序集 **2577 total / 2576 passed / 1 failed / 0 errors / 0 skipped / 0 not-run**。未将该批次记绿，未修改 verifier、断言、超时或调度配置，未重跑首败来替换结果。

| 本轮实际结果 | 结果 / native exit |
|---|---|
| fresh solution 与逐工程构建 | PASS，0 warnings/errors，exit 0 |
| TableService（本任务再次在汇合头执行） | 167/167，exit 0 |
| DurableHost，真实 PG/WSS | 59/59，exit 0；917.351 秒 |
| LocalHost | **114/115，1 failed，exit 1** |
| 其余十个测试程序集 | 全部通过，exit 0 |
| table Infrastructure，统一前专跑 / 定向 | 42/42、13/13，exit 0；统一批次另跑 42/42 |
| writer 强杀 → PG immediate restart → 独立冷 reader | PASS；恢复 state=1、seq=2、receipt Applied |
| pg_dump → 空 DB pg_restore → 冷 reader | PASS；恢复 events=2、ownerEpoch=3 |
| companion / scratch plugin copy | 4/4，native 0 / 12 assemblies，PASS；tracked Unity assets 未修改 |
| 自有 cluster 清理、环境恢复 | PASS；table/lobby 目录均已删除，两端口重新绑定验证空闲 |

**BATCH-LH-COLD-01 / MAJOR / OPEN**，失败精确位于同批 LocalHost 路径（复现命令即上列未经修改的完整 wrapper）：

```text
Mahjong.LocalHost.Tests.LocalControlTests.PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged
dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs:1278
Assert.Equal(parentPendingDecision, MarkerValue("cold_pending_decision="));
Expected: "local-human-0-0-CheatWindow"
Actual:   ""
```

失败 XML：`B/unified/direct-xml/Mahjong.LocalHost.Tests/direct-results.xml`，SHA256 `5e1e966fc13cd2d98ad38198538d77921b7c469adb87a235ba8f19250f809f48`；原始 framework log 与 native exit 1 保留。未将其归因为特定竞态或产品根因，未以单独重跑绿证明消除。该路径与 accepted LocalHost `4a08dd7` 精确相同；它不属于本 TableService 窄修复，故不向本线程发送空转 rework、不撤回本任务批准，也不自行操作 LocalHost 线程。

后续 LocalHost 修复须解释并消除父进程和冷进程 pending decision 身份不一致，保留原拒绝回执/outbox 对账、精确版本/身份及真实 OS 冷恢复断言；不能用空字符串兼容、删断言、sleep 或重试冲绿代替。关闭本批次缺陷需该用例和默认统一门禁实际通过。

`B/Audit.ps1` 对逐测试节点、assembly 汇总、meta native exit/XML gate 与统一结果进行一致性核对，明确要求保留上述 **FAIL**；其脚本退出 0 只说明证据一致，`caller-audit.json` 的 `batchStatus` 仍为 `FAIL`。`caller-test-counts.json` SHA256 为 `001c23a29813f0ead42283444112ae8f43de92d5f9b8fc108ce6687d88e21962`。所有错误/跳过/未执行为 0，唯一失败仍为上列 LocalHost 用例。默认 companion 的历史 Git pins 验证本轮未请求，不将其写成另一次 pin PASS。

发布前冻结的 `B/caller-evidence-sha256.csv` 覆盖 284 个证据文件，SHA256 `678593d43c1b625a56aec4bc31cda6588a4c62d0ce2689cb26e77522fa24bff0`。后续 MR 操作另存新文件，不 append 已覆盖日志。调用方验收文档含该 manifest 的引用，故不把本文自身放入递归哈希链；本文由最终 Git 提交认证，源码/交付报告、测试 DLL 和原始运行证据分别有完整 SHA256 清单。

批次带已记录首败保留在既有 **Draft PR20** 供审阅，**不具备批次可合并结论**。本次统一发布两项已组件接受的改动及本验收记录，不新建组件 MR，不接入仍在返修的完整 B1/Unity，不 merge v5 或部署。

## 发布与后续验证边界

唯一发布目标为既有 `origin/feat/v5-post-g1-batch`，使用普通 push，并回读 PR20 的 source/base/head/draft 及远端 SHA；最终发布 SHA 与同步结果由本线程 `record_merge_request` 持久记录。此验收文档是实现汇合头后的纯文档提交，测试期间实现无改动。

旧 PR20 `7340629` 的远端 CI 支付/额度阻断历史保留；本轮新头远端 CI 尚未由本文声明通过，不重试 CI 或创建 CI-only 分支。新头是否实际执行需独立观察，不能继承历史本地结果。

V4 保持唯一生产基线，G1 APPROVED、G2–G5 未批准、181 classified 缺口保留。真实设备、跨物理网络、完整多客户端整场、生产 CA/域名、资产/OIDC、跨实例票据消费均未由本任务完成。未合并 v5、部署、改 scheduler 或创建 complete.flag。
