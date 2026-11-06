# LocalHost 95b4754 门禁审查输入：调用方验收

2026-10-29，Asia/Shanghai。任务 `thread_20261029_011900_a9fd`，沿用原线程及 caller 身份；本轮没有另派实现或新建任务。

本任务范围为冻结候选的独立审查文档及 ignored 证据，**审查交付 PASS，候选门禁 REWORK**。接受 `bddf4be47fe440cffd71eedfd3c14fe3e4d640b6` 的[独立报告](V5-LOCALHOST-BATCH-GATE-INDEPENDENT-REVIEW-20261029.md)，不把报告发现的产品缺陷退回给只负责审查的 receiver，也不因其他在途任务否决本报告。

原 expectedFiles 登记为 `V5-LOCALHOST-INTEGRATION-GATE-REVIEW-20261029.md`，实际交付使用上述 `V5-LOCALHOST-BATCH-GATE-INDEPENDENT-REVIEW-20261029.md`。调用方接受这一文件名登记调整：交付仍为同一份约定报告，唯一新增 tracked 文件，没有产品、测试、verifier 或 pin 越界修改。报告 SHA256 为 `7D8001CBC5C1DBA78E69A49D930DCF795B124ED03C61F041E18F75F31E7997D0`。

## 独立复核与本轮实跑

冻结候选仍为 `95b47547b535f38a1fc6328445e209e7d402adcc`，tree `8017f1e355847c94f0cea1ae31855478238caae8`，双父为 `17808cca932269ac8058270a5684b33d02d82abd` 和 `865ca0a455bda625c660c6b7e1df049f88d4591e`。

调用方在当前工作树的 `build/caller-gate-review-20261029/` 建立新的独占快照。重新 `git archive` 的 ZIP SHA256 为 `9720FD553EBC9AA387062ACA2E73146EF8F3677D8256E2486487A69D25810AFC`，与 receiver 的精确候选归档相等；未使用他人 bin/Library 作为本轮测试产物。

- receiver 外层 manifest `9CDB394EDA3230E68BAA74E56FDBF50183D95FFA099BC614E126449A953E649D` 的 306 行全部重新计算长度和 SHA：306/306，一致。
- `test-counts.csv` 的 25 行原生 XML 均核对汇总与逐例 Pass/Fail/总数，无不一致。
- `D:/Unity/dotnet/dotnet.exe build <snapshot>/dotnet/Mahjong.sln -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false`：native exit 0，0 warnings/errors。
- 用相同参数 fresh 构建 LocalHost.Tests、Architecture.Tests 和后续两个探针工程，均 exit 0，0 warnings/errors。独立执行脚本为证据根下 `verify.ps1`，直接运行新 DLL，写原生 XML；每次 native exit 单独保留在 `verification.log`。

| 本轮新跑工程 | total / passed | failed / errors / skipped / not-run | native exit |
|---|---:|---|---:|
| LocalHost | 101 / 101 | 0 / 0 / 0 / 0 | 0 |
| TableRuntime | 413 / 413 | 0 / 0 / 0 / 0 | 0 |
| Client | 182 / 182 | 0 / 0 / 0 / 0 | 0 |
| Architecture | 130 / 130 | 0 / 0 / 0 / 0 | 0 |
| TableService | 152 / 152 | 0 / 0 / 0 / 0 | 0 |

共 978/978；逐例数与 XML 汇总一致。LocalHost 的 `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` 通过，源码明确创建新的 OS 子进程执行 CrashWorker `furiten-read`，并校验 worker exit 0 和恢复 marker。本次通过不注销旧 c600611 / 40715805408 的未归因失败。

## 缺陷证据与裁决

1. **D-LH-GATE-01 保留 MAJOR**：将 receiver 的探针测试源码复制到调用方独占快照，fresh 编译后执行，1 total / 1 failed、native exit 1；真实异常仍为 `Missing required field expectedControlEpoch (1)`。XML SHA256 `E12D4A748F1360C03153CF48A7E0120AAF02702F9A286CC68E57ED99D7178BBC`。Contracts 20/21 与 TableService tag 1 不一致，普通 TableService 152 项通过不能覆盖这一缺口。
2. **D-LH-GATE-02 保留 MAJOR**：同样只在独占快照加入原探针，fresh 编译后 1 total / 1 failed、native exit 1，实际 `count=1 lastLookup=NotFound`；XML SHA256 `EE9D5955AB50DD2B826FB8C2EBD4481BF8D462FC12C2E9EB15B30BDCADA57F2E`。
3. **D-LH-GATE-03 保留 MAJOR**：accepted 基线 companion `-VerifyGitPins` 本轮 exit 0、4/4；精确候选独立 clone HEAD/tree/clean 已核对，同一命令 exit 1，`HASH_MISMATCH` 与 `RENDER_SHA_MISMATCH` 两项复现。该 clone 是合法 CRLF checkout，visibility actual `B388934E339086F9519EA2703B0110A131A224A1EB55F94B4CA7D8D9BCDAA2BE` / 147185B，render `e8d8f1a543f0554adeed88383060f49ea0392e56e8e8648a0cb7caab1edce502`。catalog 独立新跑 exit 0，6 fixtures / 73 requirements / 10389 cases；它不替代 companion。
4. **D-LH-GATE-04 保留 MAJOR**：原始 PG wrapper XML 和 framework log 的 `saved=2 committed=1` 已重新核验，100/101、native exit 1。调用方这次 standalone 101/101 不能消除已发生的竞态；没有盲重跑原 PG 首败以覆盖它。

两处修复口径补充，供后续产品修复使用：

- D-02 的控制入口在 `LocalTableRuntime.cs:773-777` 转入 `HandleControlCommandAsync`，先查询 durable prior，再在约 `1190` 调用 `TryRejectStaleDecision`，随后才准备新 control result。报告所称早退应理解为“早于新结果持久化”，并非控制 handler 之前。异字节冲突必须保留原请求的 durable outcome，不能把冲突拒绝覆盖原成功记录；拒绝对账也必须绑定原 digest/kind 与身份。
- D-04 不能只改成捕获 discard receipt 的版本后，就要求包含自动摸牌的最终 store 等于该较早版本。修复须选定一致的权威边界，等待自动后续提交及 snapshot 对齐，或明确隔离后续推进，再校验存档与冷恢复的精确版本、roundId；不接受 sleep、`>=`、删断言或重试冲绿。

## 证据、发布范围与未执行项

调用方证据 manifest 共 28 行，重哈希 28/28，SHA256 `D7036A4FAEAF32577DE356603CF3AF456F02722B14AB11BD2C485C6354769CC0`；索引包括原生日志/XML、`receiver-audit.json`、`baseline-test-dll-sha256.csv`、构建日志和复核脚本。独立快照内只有两个探针测试文件被增改，产品源码保持候选原样；ignored 快照和证据不提交。

真实 PG 本轮仅复核 receiver 的原始证据，未冒充调用方新跑。原 table wrapper 的实质阶段通过与 cleanup exit 1 分开保留；两处 owned PG root 已不存在，55720/55730 无监听。Unity/Player/IL2CPP、Android/iOS、真机、跨物理网络、整场、候选远端 CI 本轮均 NOT_RUN；旧 3afefd5 / 40729458798 失败继续保留。

唯一收口 PR 继续使用 **PR20：feat/v5-post-g1-batch → v5**。以远端 `44b1da88ab66bb73a873f41696cc5d0d82210216` 为本轮发布比较基线，保留其已发布的同批审查记录；本轮只新增独立报告与本调用方验收记录。冻结候选实现、在途产品修改和 PR21 不随本次推送；不另建 MR，不合并 v5，不部署，不创建 complete.flag。

本任务的审查交付完成；a/b/c/d 组件接受保留，完整 LocalHost 仍未接受，候选四项修复义务继续追踪。G1 APPROVED、G2–G5 未批准、181 classified 未覆盖，V4 仍为唯一生产基线。
