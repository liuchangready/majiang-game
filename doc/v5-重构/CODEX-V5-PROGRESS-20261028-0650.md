# V5 巡检：R7 冻结候选与当前 PR20 的 PostgreSQL 失败（2026-10-28 06:50 UTC+8）

G1 APPROVED；G2–G5 未批准，V4 仍为生产基线。本轮没有接受新的实现代码。

## 基线、归属与隔离

本轮 fetch 后 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，PR19 已合并。后续批次沿用唯一开放 PR20：`feat/v5-post-g1-batch → v5`，检查对象为 `5e638b90609fa4e48bf0f8c4531445adabe84c20`。

审查树 `D:/majiang-wt/codex-progress-20261028-r7` / `wt/codex-progress-20261028-r7` 从本轮 origin/v5 创建，fast-forward 至该 PR20 头，再无提交合入 B2b 冻结候选 `79f9573bc4a367193258e3e2518b6d6373fab5f3`。定向检查结束后已 `git merge --abort`，发布增量仅为本报告；未发布候选源码或 receiver 未提交内容。

主目录仍为旧分支，原有 CLAUDE.md、UI prompts 和未跟踪状态文件均未动。B2b receiver 在冻结候选之外已有 SessionCoordinator.cs 未提交修改；派单前 reservation 复查确认原线程已进入 R8 reworking，保留原路由，本轮不重复请求返修，也不将这部分改动混入验收。LocalHost 树为 clean 的 `98540d4dbb547952becd441c619e762fcbf6642f`；P4.1c/d 继续保持用户手动暂停。

## B2b 独立定向检查

证据根 E：`D:/majiang-wt/codex-progress-20261028-r7/build/`（ignored）。所有检查使用本轮审查树中的冻结提交；未修改生产代码或断言。

| 检查 | 实际结果 |
|---|---|
| fresh TableModel Release build / direct runner | PASS，exit0/0，138/138，errors/failed/skipped/not-run 全0，build 0 warnings/errors |
| fresh Client Release build / direct runner（默认输出目录） | PASS，exit0/0，182/182，errors/failed/skipped/not-run 全0 |
| Unity 原调用方 R3/R4/R5/R6 九项反例 | PASS，native0，selected9/passed9/failed0/skipped0/inconclusive0 |
| 隔离项目与审查树生产 C# 对照 | 49/49 SHA256 一致，0 mismatch |
| 冻结 `c0f16eb..79f9573` diff-check | PASS，exit0 |
| 反向 spectator→player 两阶段永久矩阵 | NOT_RUN；receiver R7 明确未交付 |
| 整项 B2b 接收、完整 Unity/设备验收 | 未批准；REVIEW_INCOMPLETE |

Unity 九项反例使用原调用方 R6 的隔离工程副本与原样已注入测试，将其中生产 Scripts 替换为本轮审查树对应源码后重新编译。沿用该副本的共享插件集；本次不是 fresh 全套共享 DLL/Unity 正式全量验收，不重复累计 receiver 的 Edit62/Play126 或 Player 构建证据。九项通过证明既有授权撤销反例在冻结源码上的定向结果，不消除缺失矩阵或接收方在途修改。

Client 首次使用 `--artifacts-path build/caller-r7/client-fresh`：build exit0，runner exit1，175/182；七项均为 FileOutboxCrashWorkerTests 的 FileNotFoundException。工程 CopyOutboxCrashWorker target 从固定 `bin/Release/net8.0` 复制，无法找到自定义 artifacts 输出中的 worker。本轮改用全新审查树的默认输出目录 fresh build 后 182/182。首次日志/XML保留，归为调用方验证布局问题；没有修改测试工程或以旧 worker 替代，也没有将首次失败归因于产品回归。

原样命令：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release --no-incremental --artifacts-path build/caller-r7/fresh --nologo -v minimal
D:/Unity/dotnet/dotnet.exe exec build/caller-r7/fresh/bin/Mahjong.Unity.TableModel.Tests/release/Mahjong.Unity.TableModel.Tests.dll -noLogo -noColor -xml build/caller-r7/model.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-incremental --nologo -v minimal
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll -noLogo -noColor -xml build/caller-r7/client-default.xml
D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe -batchmode -runTests -testPlatform PlayMode -testFilter "CodexR3_;CodexR4_;CodexR5_;CodexR6_" -projectPath E/unity-r7/project -testResults E/unity-r7/extra.xml -logFile E/unity-r7/extra.log
```

E 在 Unity 命令中展开为上述绝对证据根。原始 build/run 日志与 XML 位于 E/caller-r7，Unity 原始日志/XML/native-exit.txt 位于 E/unity-r7。

| 原始证据 | SHA256 |
|---|---|
| E/caller-r7/model.xml | `9F8AB46FBE9540C1B837294E530873C01CFAE4CCE4F7B997E986BE657C9BE0A9` |
| E/caller-r7/client-default.xml | `451A518DE588526F7DD96F834296F0462F7E8B65ED19F5BD99FF726B46A3F384` |
| E/unity-r7/extra.xml | `E10C95FED9CE038D8094CE91721BD6D0394E791B2DD675BB6E95C70F7ECE0F5E` |
| E/unity-r7/extra.log | `F9871810DFB294F9C6D12CB8177CDDF6B59A26D0B6D0673FC6615492FB297694` |
| E/caller-r7/production-cs-audit.csv | `20ACA3830842DB643FF670D3B7DE96B1D402354708F29C5F5BC9511B23C49F19` |

## 当前 PR20 的双 CI：不能用 push 成功覆盖 PR 失败

两个 run 的 headSha 都是 `5e638b90609fa4e48bf0f8c4531445adabe84c20`，原始产物均已下载。不是重试旧 run，未修改 verifier。

| run | 实际结果 |
|---|---|
| [40593411699 / pull_request](https://github.com/liuchangready/majiang-game/actions/runs/40593411699) | FAIL：PG16.10 cluster、XML负例7、fresh build通过；真实PG total35/passed34/failed1/errors0/skipped0/not-run0，exit1；未进入后续统一验证 |
| [40593403558 / push](https://github.com/liuchangready/majiang-game/actions/runs/40593403558) | PASS：真实PG35/35；crash/restart、empty-DB backup restore及统一入口通过；summary 为14 PASS/0 FAIL/0 BLOCKED/0 NOT_RUN，companion PASS |

失败用例：`Mahjong.Infrastructure.Tests.PostgresR1RegressionTests.NonEmptyCheatLedgerAndFacts_WithCheckpointAndTail_RecoverStrictly`。框架 XML 中的真实断言：`CLOCK_REGRESSION: UTC time moved backwards across boot.`，源位置 PostgresR1RegressionTests.cs:448。原因尚未确定，不能推断为 flaky、已修复或仅测试时钟问题；禁止通过放宽跨 boot 时钟守卫获得绿色。

失败完整 job.log/run.json、tests.log/tests.xml、build 日志及 PostgreSQL 日志保留于 E/ci-40593411699。决定性 XML：`E/ci-40593411699/v5-verify-logs-40593411699-1/verify-v5/table/tests.xml`，SHA256 `D1E7ED36719600513D1423332C2D4305AC53DE27C18CF705947C7A4DEC39DC14`。`E/ci-40593411699-sha256.csv` 索引非 tmp 文件；下载产物中的 tmp 编译器缓存存在不可解析路径，因此该索引明确不覆盖 tmp，原下载目录未删除。成功产物位于 E/ci-40593403558。本轮已核对上述决定性 XML/summary，不声称重审了所有远端断言。更早缺失 framework log 的历史失败仍 UNRESOLVED。

## 台账、派单准备与依赖

全局 recent tasks 工具本轮只调用一次；准备与派单前另读取本地 ledger 核对 reservation，未轮询 unchanged receiver 日志。

| 路由 | 调用前 active / target | 原线程或准备 |
|---|---:|---|
| DeepSeek Flash | 1 / 2 | B2b `thread_20261027_230256_426c` 已 R8 reworking；不重复派单。第二槽准备修复本次独立出现的 P5.2 CI CLOCK_REGRESSION，范围限 PG 测试与交付报告，不占 GLM 宿主文件。 |
| GLM Flash | 1 / 1 | 持久宿主 `thread_20261027_230302_93fb` reworking，保留原路由与冻结基线，未交付不接收。 |
| Anti 默认模型 | 0 / 1 | P4.1a `thread_20261027_154438_fc96` failed/TIMEOUT，准备原线程 retry；不恢复 P4.1c/d，不改派。P4.1b 已完成并记录 PR21，不代表整项 LocalHost 被接受。 |
| 合计 | 2 / 4 | 两个调用成功后预计4/4，最终按桥返回值记录，准备不等于成功。 |

新 CI 跟进树 `D:/majiang-wt/p52-ci-clock-ds-20261028` / `wt/v5-p52-ci-clock-ds-20261028` 从最新 origin/v5 创建，再 fast-forward 至已审查 PR20 `5e638b9`；未采用旧 main 或 PR19 封闭来源。scope 预查与现有 active/paused/failed V5 任务无重叠。模型发现确认 DeepSeek 默认 `deepseek/deepseek-v4.1-flash`、Anti 默认 `gemini-3.8-flash-high`，Anti 调用不指定 model。

P4.2-B/P4.5 等完整 accepted LocalHost 与 B2b；完整远端 P5.4b 后续批次等 accepted 持久宿主；P6.1/P6.2 等 G2+G3；P7 等 G4。空槽不可通过复制这些任务填补。

调度为 PT30M / IgnoreNew，complete.flag 不存在。181 classified 仍未覆盖；Android Wire AOT 工具链组件历史通过不等于完整游戏或真机通过，完整游戏/真机/跨物理网络 NOT_RUN，iOS BLOCKED。未部署、未批准 G2–G5、未创建完成标记。
