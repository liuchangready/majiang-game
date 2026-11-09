# 测试方法与覆盖范围

核对日期：2026-11-09；统计参考快照为清理前 `e074806a39a674b898120c828038efcd1b5c58d7`。本文区分源码中的测试规模、自动化覆盖和实际验收；静态数字不等同本任务执行结果。

## 测试规模

| 层级 | 数量 | 统计口径 |
|---|---:|---|
| .NET，14 个测试工程 | 2,895 | Fact 加展开的 InlineData / MemberData 数据行 |
| Unity EditMode | 90 | Test 定义 |
| Unity PlayMode | 168 | UnityTest 定义 |
| V5 合计 | 3,153 | 上述三项相加，非全量通过记录 |
| 当前 Kotlin / Java 游戏代码和测试 | 0 | 旧七模块与其专属 JVM 工具已从当前树移除；历史报告仍记录当时结果 |
| PowerShell 工具回归 | 21 个脚本 | 每个脚本有多项断言，不按脚本数冒充用例数；旧 Kotlin/catalog 专属自测移除，新增 frozen source provenance 自测 |

清理前参考源码中的 1,838 个 Kotlin 测试方法随旧模块一起移除；这只是旧测试实现数量，不是新增通过数，也不代表 V5 规则测试覆盖等价。后续变化须重新统计或读取对应版本的真实 XML。G2 Windows 32 项和 Android 平台矩阵属于验收要求，与代码测试存在覆盖关系，不重复相加。

当前活动黄金来源门禁是 `tools/verify-v5-source-provenance.ps1`（由 `verify-v5.ps1` 与 CI 必跑）；它校验 10 个 fixture 的冻结字节、70 条历史源码 pins，且普通模式不依赖 `.git`。`catalog-v1.json` 是早期索引快照，保留原文但不再作为当前输入或通过条件。旧 1,838 个 Kotlin 方法是删除的旧测试实现数量，不计为新增通过；所有现行 C# golden 行为断言仍保留。

## .NET：规则与服务集成

| 测试工程（Mahjong. 前缀省略） | 展开用例 | 主要覆盖 |
|---|---:|---|
| Domain.Tests | 529 | 牌位置与守恒、洗牌黄金样本、合法动作、役种/符/支付、单局/整场、三绝技与账本 |
| Ai.Tests | 444 | 普通动作及千术候选、授权视图、确定性与预算策略 |
| Contracts.Tests | 152 | 协议模型、编解码、稳定码与黄金数据 |
| Architecture.Tests | 132 | 模块依赖与源码边界 |
| Client.Tests | 182 | 会话、视图绑定、outbox、回执、对账与防重复 |
| Client.Wss.Tests | 82 | WSS 通道、心跳、重连及异常边界 |
| TableRuntime.Tests | 493 | 串行权威、决策/deadline、控制代次、可见性、持久化/恢复及错误码 |
| LocalHost.Tests | 197 | 本地房间、真实客户端通道、存档/备份、原请求对账、控制权和固定玩法场景 |
| Infrastructure.Tests | 42 | 存储、事件、检查点、回执及结算 outbox；真实 PG 路径需显式环境验证 |
| LobbyService.Tests | 89 | 大厅、房间目录、匹配/票据、幂等与持久化接口 |
| TableService.Tests | 204 | 真实服务入口、鉴权、消息帧、隐私、结算权威与 LOCAL 资产拒绝 |
| TableService.DurableHost.Tests | 59 | 持久宿主、恢复锚点、声明动作与事件历史 |
| P5.4b.B1.HttpsAdmission.Tests | 54 | HTTPS 入桌与票据边界 |
| Unity.TableModel.Tests | 236 | Unity 可用纯 C# 牌桌投影、输入意图、动画/事件确认及整场执行边界 |

完整统一入口（当前 V5 .NET 测试入口）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe `
  -LogDir "artifacts/verify-v5/$([guid]::NewGuid().ToString('N'))"
```

该入口构建 solution，发现测试工程，校验原生退出码、XML、控制台计数和 companion fixtures，并在无旧源码目录的情况下校验冻结 golden/provenance。历史来源由 `tests/v5-golden/source-provenance-v1.json` 中的原提交、路径和 Git blob SHA-1 记录；普通测试不读取旧源码、不调用 Gradle、不要求完整 Git 历史或联网。定向诊断可使用项目自己的 xUnit direct runner；记录实际执行数量，不假定过滤参数必然生效。

真实 PostgreSQL、持久 WSS、HTTPS 入桌及结算链路分别使用 `tools/ci/verify-v5-table-postgres.ps1`、`verify-v5-postgres.ps1`、`verify-v5-durable-table-host.ps1`、`verify-v5-https-admission.ps1`、`verify-v5-b2-terminal.ps1`。先读脚本参数与前置，使用隔离数据库/端口/密钥和独占输出，不把普通 .NET 绿灯当作真实 PG 或跨网络通过。

## Unity Test Framework

项目已安装 UTF 1.6.0。Editor 的 Window → General → Test Runner 中分别选择 EditMode / PlayMode，可运行全部或指定测试。

- EditMode：导航、组合根与程序集边界、场景静态结构、字体/视口、结算展示、私有账本授权等。
- PlayMode：Boot/菜单/大厅/牌桌导航、真实 LocalHost 与客户端动作、三绝技、四席/观战/角色切换、控制权、动画、暂停恢复、安全区、输入与 UI 原型回归。
- `C04PlayerActionScenarioTests` 现有 5 案：庄家自摸、碰、吃、立直、暗杠及岭上补牌。固定牌山与前置合法动作由 LocalPlayerActionScenario 创建，最终动作经客户端提交。
- 三绝技与隐私测试已有真实通道/Presenter 断言；部分测试仍是组件级或夹具回归，不能统一宣称整场端到端。
- 直接调用 Button.onClick 或提交命令意图，不代表真实 OS 鼠标/触屏命中、DPI 和焦点链路通过。

自动调用使用 Unity 的 `-runTests -testPlatform EditMode|PlayMode` 及独占 XML/日志目录。沿用现有执行器的共享锁和超时，不在另一个任务持锁时手动开第二个验证进程；Player 平台测试和普通交付包验收分别记录。当前未将全部 32 项迁移进 UTF。

## Windows Player / Android 套件

在当前任务根目录生成计划（不启动 Unity/ADB）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1
```

执行一个指定套件（长验证前先完成任务卡与候选冻结）：

```powershell
$candidate = (git rev-parse HEAD).Trim()
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1 `
  -Mode Run -Suite WindowsActions -ExpectedSourceHead $candidate
```

Windows 套件为 WindowsFullMatch、WindowsActions、WindowsCheats、WindowsRecovery、WindowsPrivacy（独立 PlayMode）、WindowsAppMatrix（普通 Player 七案及恢复）。三绝技需通过 RunnerParametersPath 显式绑定本候选新 verifier；其余可选参数以入口生成的计划和子执行器接口为准。

Android 套件为 Probe / Build / Boot / Install / Start / Capture / Input / Lifecycle / Shutdown，统一入口名带 Android 前缀。复用有所有权记录的设备 state 和同包 APK；ADB 输入或截图完成只构成部分证据，不代表合法麻将动作与触屏效果已验证。

旧 Kotlin 测试移除后的数量变化不得报告成测试通过；只有清理后实际运行的 .NET/Unity XML 与原生退出记录算本候选执行证据。完整命令和缓存前置见[构建与出包](BUILD_AND_PACKAGE.md)。

## G2 Windows 固定 32 项验收要求

每行都含成功条件、拒绝边界和原件绑定要求。下面是固定分母，不是 32 项已通过清单。

| ID | 验收要求 |
|---|---|
| C04-G1-01 | 菜单 → 本地房 → 准备/码牌 → 首回合；136 张与首次出牌回执/事件正确 |
| C04-G1-02 | 分别完成真实和牌与荒牌流局；比分/本场/供托一致，终局不可复活 |
| C04-G1-03 | 连庄、轮庄及下一局创建，新 roundId，分数与供托只结转一次 |
| C04-G1-04 | 整场终局、最终排名和账目，无未决 outbox |
| C04-G2-01 | 碰：副露/来源实体正确，非法或过期请求零变更 |
| C04-G2-02 | 吃：合法顺子及来源正确，不可用组合拒绝 |
| C04-G2-03 | 立直/双立直事实、一次供托扣款、一发与非法非听牌拒绝 |
| C04-G2-04 | 杠、岭上补牌及抢杠窗口，非法杠拒绝且 136 守恒 |
| C04-G3-01 | 落汗授权成功及实体追踪 |
| C04-G3-02 | 袖里藏花授权成功、候选/交换与私有视图 |
| C04-G3-03 | 偷梁换柱授权成功、等额交换及窗口边界 |
| C04-G3-04 | 三绝技精确一次费用、私有余额和账本 |
| C04-G3-05 | 三绝技及后续摸打保持 0..135 各一次 |
| C04-G3-06 | 未授权与余额不足分别拒绝，合法成功对照 |
| C04-G4-01 | 真实断线、策略宽限后 AI 接管及旧决策拒绝 |
| C04-G4-02 | 离开/重入同一房间，全量基线、旧绑定解绑、未决请求对账 |
| C04-G4-03 | 重连不自动收回；显式 claim、自增 epoch、旧命令/回调失效 |
| C04-G5-01 | 四席只见本人授权私有信息，无对手暗牌/种子/费用泄露 |
| C04-G5-02 | 观战仅公开投影，独立公开游标 |
| C04-G5-03 | 角色切换先清私有缓存，延迟旧 binding 事件丢弃 |
| C04-G6-01 | 有效主档正常重启，原动作对账而不重复提交 |
| C04-G6-02 | 自有 Player 强制终止与冷启动，已提交动作仅生效一次 |
| C04-G6-03 | 主档截断、有效备份恢复 |
| C04-G6-04 | 主档 checksum 错误、有效备份恢复 |
| C04-G6-05 | 主档缺失、有效备份恢复 |
| C04-G6-06 | 两档均缺失，新房/会话/scope，不携带旧请求 |
| C04-G6-07 | 两档均坏，拒绝恢复、原件与状态不变 |
| C04-G6-08 | 不兼容主档按策略拒绝，无私自回退和游戏提交 |
| C04-G7-01 | 真实离线环境完成整场，无远端通道/出站尝试 |
| C04-G7-02 | 真实服务权威入口拒绝 LOCAL 结算资产且零副作用，合法服务端结算对照 |
| C04-G8-01 | 基线/宽/高支持比例的菜单、牌桌、结算完整画面与可读性 |
| C04-G8-02 | 真实鼠标/键盘关键流程，一次输入至多一动作，取消/失焦/旧输入不误提交 |

Android 触屏项 C04-G8-03 和 C05 的 E1/B1/D1/R1/V1/I1/L1/F1 八行平台合同不计入 Windows 32 项。统一 Plan 共 41 行：33 C04 + 8 C05。

## 已有结果与尚缺证据

当前 Windows 台账仍为 18 PASS / 11 PARTIAL / 3 NOT_RUN，任务未验收。已有整场、三绝技、恢复、隐私/七案及五动作支持证据分别属于各自候选/包；不能混拼最终包全绿。当前五动作支持包不等于最终干净候选通过。

仍需补齐确定性荒牌/连庄及新局绑定、加杠/抢杠等分支、完整非法/过期矩阵、真实断线接管、物理输入与多比例牌桌/结算画面、受控离线整场及同候选同包的全部必需原件。Android 安装不代表玩法/触控/生命周期或 ARM64 真机通过。

精确 trigger / initialFixture / assert / bindings 在当前基线的 `doc/v5-重构/V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json`；最新 Windows 缺口见同目录 `V5-G2-WINDOWS-LUNA6-R14-EXECUTION-SUPPLEMENT-20261109.md`。这些文件在旧主目录未必存在，应到当前基线/任务工作树核对。历史报告只按其原受测身份使用。

判定必须同时核对真实非零用例数、零失败/跳过/未运行、原生退出码、XML 与日志、请求/回执/事件/权威后态以及完整包哈希。工具自测、exit 0、RUNNER_COMPLETED、菜单截图均不能替代完整合同；共享锁、证据封存及复核流程按 [AGENTS](../AGENTS.md) 和[长验证模板](LONG_VALIDATION_TASK_TEMPLATE.md)执行。
