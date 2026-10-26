# P0.1 G-004..G-007 movement golden：调用者独立验收

日期：2026-10-25。Bridge：`thread_20261025_141920_33e3`，第 3 轮。调用者已执行 `approve_task`，返回 `COMPLETED`。仅接受下述物理字段绑定；**G1 仍 BLOCKED，G5 未完成，V4 仍是唯一生产基线**。

## 交付与集成范围

- 交付头：`d39be0704cd13e5a86762ca6b03efb320b87dd2b`，包含 `fdd0287`、`ce84af0`、`d39be07` 三个本地任务提交。
- 冻结任务基线：`d6bc69fa930d005b355a1273ec732fbdf82bbe2f`。至交付头恰好新增五个授权文件；工作树干净、`git diff --check` 通过。无生产源码、旧五 fixture、manifest/catalog 或统一 verifier 修改。
- 隔离集成基线：已发布的 `origin/feat/v5-1-integration`，`03084f08af6e2ddefa2ebc6e094de5dac2474fd4`。同批 F-01 与 typed shuffle 已在远端，本次保留其改动。
- 实测集成提交：`253cc6bf582c9ab911914ef7b0ecb8c90101259c`。相对最新远端仍恰为上述五文件，逐个与交付头一致；本验收记录是随后新增的唯一文档。
- 调用者统一更新既有 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，源 `feat/v5-1-integration`，目标 `v5`；不创建子任务 MR。主 checkout 用户未提交文件保持原样，未合入未验收的其他工作树。

## 复核结论

8 个真实 V4 case / 22 个 step 覆盖普通摸打、碰、吃、暗杠/加杠/明杠与岭上，以及重复实体碰和第五次岭上拒绝。C# 使用 fixture 初始状态和动作调用现有 `TilePositionMap` 生产方法，比较 136 实体位置、指针、返回值、摸牌证据、河、副露物理组、王牌替换和宝牌指示牌。HAND 与副露组内顺序只在结构合法后规范化。

本轮 D-2-01 已修复：所有 initial/before/after 路径在工厂调用前校验无主区域的 raw seat 必须为 -1，不能用工厂重置后的值证明原始数据合法。D-2-02 已修复：生成命令仅接受文档化 Windows/POSIX 精确形式，禁用 export、注释和错误 selector 均拒绝；不执行 fixture 内任意 shell。上一轮全部假绿反证已重新跑过，未发现新的已知代码缺陷。

本任务不新增生产并发、恢复或公开投影行为；deadline/租约/代次/公开流隐私变更 N/A。原始导出器和 fixture 在本轮未变，未重复改写黄金产物；之前独立双导出的确定性结论保留在任务账本。

## 本次独立运行

工具：.NET SDK `8.0.426`；本机 Gradle wrapper 使用 Android Studio JBR `25.0.2`。以下均为新鲜构建后的实际 native runner，不使用旧 DLL。表中计数来自 runner summary/XML，Errors/Skipped/Not Run 均为 0。

| 验证 | 结果 |
|---|---|
| 交付 Domain Release Rebuild | exit0，0 warnings/errors |
| 交付 Domain 全量 | 480 passed，0 failed，exit0 |
| V4 `TileMovementContractTest` + exporter inert | XML 分别 10 与 1；failures/errors/skipped 均 0，exit0 |
| 上一轮 caller driver 的新独占副本 | 22 行符合预期，mismatches=0，exit0；其中 20 个负例真实 exit1/Failed≥1，2 个合法控制 exit0 |
| 交付提交内 movement probe | baseline10；41 负例 exit1/Failed≥1；6 正例 exit0/Failed0；48 行 count gate 全通过，probe exit0 |
| probe SelfNegative 最终有效运行 | baseline10/Failed0 后明确输出 deliberate exit1，native exit1 |
| 旧 catalog validator | exit0；仅证明旧五 fixture/catalog 未变，不证明新增 fixture 登记或 G1 |
| 集成 solution + Runtime tests 非增量 Release build | 两次 exit0，0 warnings/errors |
| 集成 Domain / AI / Contracts / Client / Runtime | 502 + 438 + 149 + 132 + 365 = **1,586 passed / 0 failed / 0 skipped / 0 not-run**，五次 native exit0 |
| 集成 movement probe | baseline10 + 41 真实失败负例 + 6 合法正例；48 行再次通过，exit0 |

复现命令（从相应 worktree 根执行）：

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release -t:Rebuild --nologo -v:minimal
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -xml <独占证据目录>/domain.xml -noColor -noLogo
./gradlew.bat :engine:test --tests com.majiang.model.TileMovementContractTest --tests com.majiang.model.V5TileMovementGoldenExporterTest --rerun-tasks --console=plain
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-movement-binding.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-movement-binding.ps1 -SelfNegative
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental --nologo -v:minimal
# 对上表五个项目分别运行其新建 bin/Release/net8.0/<项目>.dll，参数 -xml <独占输出> -noColor -noLogo。
```

SelfNegative 前两次环境失败均保留，**不计 PASS**：第一次 C 盘临时空间不足，git archive 未完成；第二次使用过长的 D 盘 TEMP 路径，PowerShell Archive 展开/回滚失败。最终仅对子进程设置短目录 `TEMP=TMP=D:/majiang-wt/mvtmp-14d49e1f`，成功发现/通过 10 项基线后才主动退出 1；未修改系统环境、源码或 gate。集成 probe 也使用 D 盘子进程 TEMP。

## 原始证据与 SHA-256

调用者交付复核目录（工作树 `D:/majiang-wt/movement-golden-g004-g007`）：

- `build/codex-movement-final-14d49e1fb00244c3b3b59ec5c912570d/`：新鲜 build、Domain XML/log、V4 XML/log、22 行 driver 副本及逐例日志、catalog、三次 SelfNegative 日志。
- `build/movement-binding-probe/20261025T080954Z-32868/`：交付 probe 的 48 行结果和逐例原始日志。
- `build/movement-binding-probe/20261025T081441Z-36264/`：最终有效 SelfNegative 的绿色 baseline 原始日志。

调用者集成工作树 `D:/majiang-wt/codex-movement-final-integration-20261025`：

- `artifacts/codex-movement-final/`：solution/Runtime build、五模块 XML/log、集成 probe 汇总。
- `build/movement-binding-probe/20261025T081338Z-43292/`：集成 probe 的 48 行结果及逐例日志。
- `artifacts/codex-movement-final/evidence-sha256.json`：**157 个证据文件**的绝对路径、字节数与完整 SHA-256；manifest 自身 SHA-256 为 `2BEC258D735E066866F23004FC12B530BF24A10BF38F96A9E3B12EFDADE94AAB`。证据目录为 ignored 本地审计产物；干净仓库可用提交内 probe 重建验证，不依赖私有 TEMP 中的旧 DLL。

| 关键原始日志 | SHA-256 |
|---|---|
| 交付 `domain-build.log` | `C348E06905C80BB14107645989350070C4E9A173625DBEA7BB74A449EEEBDEAD` |
| 交付 `domain-full.log` | `38BD103C3577A3C9753FC3633944A145398F87714FFAE38146503CA43C75EF08` |
| 交付 `engine-targeted.log` | `D61B39089B679C8332C2021934C48E36F31882F8702FAB90478D8B02E69315FE` |
| 交付 `review-r2-run.log` | `E784876D5C553265D0CDE0974E6CA28951A1AC2CB61F68FDD9F99FDF26F9D8A7` |
| 交付 `probe.log` | `84EB29129E74974CE4B8DA29DEA60F273947ED5AD63C60AF289F3CFCE2113702` |
| 交付 `selfnegative-attempt3.log` | `948AF2CD2789E76FF4B0C9AE95C02BDFD70D93BEF2E995377E761C43D2BFC5E1` |
| 集成 `solution-build.log` | `649A5EF2BBCC163E74ECE6FFD058DAE5558F6AB0258406FB2255A8FF61CCEA5E` |
| 集成 `runtime-build.log` | `F49BFE8FA2A9111EC1ED2F255E3BD9828B574725CB18A8A5CC5437076386C950` |
| 集成 `probe.log` | `E16A54C1828E1982D01DB449BB7667220CF5AEAC714ABEC5383217BF23A3E797` |

返修前原始目录 `build/codex-movement-r2-37629d5e80dd44ca8749d5eff5bdcc01/` 未覆写，三个 hash 独立重算一致：driver `2538370608740294D4B9066DE05740386C7F16B712B2279A8D301649A9E5D6A8`；CSV `C2EE9B1B5EA804D5CEA21003612418B1CA8E2B709DBA0C1108E1A78680AC64A2`；汇总 `64B93088D2DBA63B5354823A9AAA65F7AC58034E1E511664E9CF2C0DE92FB3CC`。历史假绿仍保留为失败证据。

## 验收边界

`discardHistory`、副露 type/fromPlayer/calledTile、V4 totalKanCount/stale lastDiscard 等仍为 SOURCE_ONLY/UNREPRESENTABLE，不声称已跨运行时比较；无共同规范编码的 hash 不硬比。受保护尾牌、袖里藏花、里宝等未在本 fixture 覆盖。旧 catalog/manifest 不变，本任务不将 G-004..G-007 静默登记为全量通过。

PG、WSS、Unity、IL2CPP、Android/iOS、真实设备、跨物理网络及本次发布的远端 CI 均 **NOT_RUN**；本次 push 自动触发的 CI 不能由上述本地数字替代。旧 Client CI `39970580589` 根因仍 UNRESOLVED。完整 N3/G1/G5 未获验收。
