# V5 LocalHost D-LH-GATE-03/04 第二轮调用方验收

日期：2026-10-29（Asia/Shanghai）。线程：`thread_20261029_041602_732e`，沿用原 caller/receiver 会话；无新任务、转派或子 Agent。

本任务 **PASS / COMPLETED**，已通过 MCP `approve_task` 归档。仅关闭 D-LH-GATE-03、D-LH-GATE-04 和返修新增的 D-LH-GATE-03-SHALLOW。D-LH-GATE-01/02 仍 OPEN，完整 LocalHost、P4.2-B、P4.5、G2–G5 未接受；G1 既有批准与 181 classified 缺口保持原口径，V4 仍是生产基线。

## 审查对象与修复判断

- 冻结基线：`7f223f2870b91c579d0ba46c59f915ea02c2657d`。
- 本轮审查输入：`ec6243dcd198a10d2bb32156fa47d55f561218c1`；代码修复：`10ae8b26f64278cb4648001e301add5e8c4e55c7`；最终交付：`5f14e10837e151f07a5b2df05b292e269d1c8edd`。
- 原工作树：`D:/majiang-wt/lh-gate-repair-ds-20261029`，tracked clean，`git diff --check 7f223f2..HEAD` 原生退出 0。
- 净修改五文件：交付报告、`VisibilityGoldenTests.cs`、`LocalHostRecoveryTests.cs`、CrashWorker `Program.cs`、`visibility-v1.json`。VisibilityGoldenTests 已获 R2 指令明确纳入范围；其余生产实现未被本返修改动。

D-03：fixture 的 Git blob 精确恢复为 `eddcc00035978be64bca27efc27ec352ce686bf5`，未更新旧 fixture/pin/verifier/CI。消费测试固定既有 accepted LF/CRLF 字节身份，保留 baseline、raw/LF/行尾元数据和行为 case 校验；移除历史 Git 对象读取，离线检查受 accepted 整体字节 pin 约束。自洽重算 render 的 source provenance 篡改仍被拒绝。

D-04：先取得 receipt 的命令版本，再等待自动后续提交结束后的静止快照，最终与持久状态精确比较；未把最终版本断言改成下界比较。事件证据区分 discard v1 内原子摸牌与后续 AI 弃牌版本。新增 gate 明确暂停/释放 AI 提交，覆盖观察先于和晚于自动后续的两种时序；独立 OS CrashWorker 经真实恢复路径逐项比较 state/event/checkpoint 版本、roundId、136 实体 hash、账本与回执。两个用例均实际执行通过。

## 本轮调用方实际运行

独占证据根：`build/caller-lh0304-r2-1320d513499f424196b1904ddb158e71/`。以下均为本轮重新运行，不借用旧 DLL 充当新结果。

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false
# 分别直接执行新生成的 LocalHost.Tests、TableRuntime.Tests、Client.Tests DLL，使用 -xml 保存逐例结果。
powershell -NoProfile -ExecutionPolicy Bypass -File <证据根>/review-offline.ps1 -RepoRoot <工作树>
```

两次构建均原生退出 0、零警告/错误；LocalHost 项目引用同时构建 CrashWorker 与 TableRuntime.Tests。

| 运行 | total / passed | failed / errors / skipped / not-run | 原生退出 |
|---|---:|---|---:|
| LocalHost | 102 / 102 | 0 / 0 / 0 / 0 | 0 |
| TableRuntime | 413 / 413 | 0 / 0 / 0 / 0 | 0 |
| Client | 182 / 182 | 0 / 0 / 0 / 0 | 0 |
| full-history VisibilityGolden（定向重复） | 8 / 8 | 0 / 0 / 0 / 0 | 0 |
| 真实 depth=1 VisibilityGolden | 8 / 8 | 0 / 0 / 0 / 0 | 0 |
| 无 `.git` 源码快照 VisibilityGolden | 8 / 8 | 0 / 0 / 0 / 0 | 0 |

三模块合计 **697/697**；定向场景属于重复执行，不混算唯一用例。所有 XML 汇总均与逐例节点重新核对一致。LocalHost 的两个冷恢复版本边界用例均为 Pass。

真实 shallow clone 通过 `file:///D:/majiang` 的 `--depth 1 --single-branch` 创建，HEAD 精确等于交付 `5f14e10`、`is-shallow-repository=true`，读取历史 baseline 对象原生退出 128。无 `.git` 快照设置 `GIT_CEILING_DIRECTORIES` 到独占证据根，`git rev-parse --show-toplevel` 原生退出 128，排除了父工作树历史回退。两者执行与 full-history 相同的 fresh TableRuntime.Tests 产物，SHA256：`212238B22A8FCB561403E5EE9ED70C8FD81036CE0EE9425ECEB51A331CE05E5C`。

未修改的 companion verifier 加 `-VerifyGitPins` **4/4 PASS / 原生 0**；真实 CRLF checkout 上的未修改 catalog verifier **6 fixtures / 73 requirements / 10,389 cases PASS / 原生 0**。

| 独占副本负例 | 结果 |
|---|---|
| 修改 source provenance hash 并重算 render 保持自洽 | VisibilityGolden 8 项：7 Pass / 1 Fail，accepted frozen fixture 字节 pin 拒绝，原生 1 |
| 修改 render 字段 | VisibilityGolden 8 项：7 Pass / 1 Fail，原生 1 |
| fixture 追加一字节 | companion `HASH_MISMATCH`，原生 1 |
| fixture render hash 改为零值 | companion `RENDER_SHA_MISMATCH`，原生 1 |

负例未改工作树中的原件；副本最后恢复原字节。验证脚本初次遇到 PowerShell 5 将 Git stderr 当作终止错误、以及错误预设缺对象退出应为 1 的辅助脚本问题，修正后按实际 Git 128 验证；没有产品测试失败后盲重试或覆盖原产品失败。

## 证据核验

开发方 `final-evidence-sha256.tsv` **321/321** 文件长度与 SHA256 全部匹配，原失败记录保留。调用方 `caller-test-counts.csv` 包含全部八份新 XML 的逐例核对计数和 SHA256，其 SHA256 为 `964CC38D3CC7DDC436DB2D28024554CE5ADEABDC829D6166C2DA72D105885098`。

| 本轮 XML | SHA256 |
|---|---|
| localhost.xml | `05A2C984CF4358805C298CA02B725C44949B893B665AD40F578C18B99B538198` |
| tableruntime.xml | `4C026EDEBA6B96578CCDF517E983397FFB458099D4072EAE0DFD9979E1619632` |
| client.xml | `BB6C96DF7705447814CC1C2A03A79B0BAC42D0AF0433507634A46E9BB81D2040` |
| shallow-visibility.xml | `38C5174358E72ED5396ED144F497F57A2B7C9A35826DE8B41D8015EECB8F2954` |
| nogit-visibility.xml | `F2696FE71F72ADC28BC135E8B936D257CACE27C94C9023CC508C64BC4A27F6F2` |

`caller-evidence-sha256.csv` 固定最初 34 份调用方审查证据，SHA256 `79912DA32E3AE6D8D4CC24DEEDBF3E30BF72E87E11A354FCCB0164DC69F3654A`；后续 MR 操作单独保存在相同证据根，不改写该清单。

## 统一 MR 收口与批次边界

按本次用户的统一 MR 收口指令，在隔离 integration 工作树把已组件接受的 LocalHost `865ca0a` 与本任务 `5f14e10` 收集到最新远端 PR20 头 `183e378`，继续使用唯一现有 **Draft PR20 / feat/v5-post-g1-batch → v5**。不创建其它 MR、不修改历史组件 PR21；不接入在途持久宿主、runtime 返修或 Unity 装配分支。

本次选择以 Draft 展示修复及其必要组件供审阅；此前汇合候选门禁未通过的历史结论保持有效。相对 `183e378`，集成新增原 LocalHost 组件及本返修共 101 文件；再增加本调用方验收记录。集成代码（排除 doc）与实测 `5f14e10` 精确相同，`git diff --quiet 5f14e10 HEAD -- . ':(exclude)doc/**'` 原生退出 0。原任务工作树继续保持交付版本与 clean 状态，整合提交由调用方完成。

本任务批准不构成批次可合并批准：D-01 TableService 控制 tag、D-02 LocalHost 旧 decision 拒绝回执持久化仍 OPEN，完整 LocalHost/P4.2-B/P4.5/G2–G5 继续 BLOCKED。没有合并 v5、部署或创建 complete.flag。

本轮 PG 只重哈希开发方已有证据，未新跑 PG 负载；统一批次全门禁、远端 CI、Unity/Player/IL2CPP、设备与跨物理网络均 NOT_RUN。新推送触发的 CI 必须独立复核，不能继承本地通过或旧 CI 结论。
