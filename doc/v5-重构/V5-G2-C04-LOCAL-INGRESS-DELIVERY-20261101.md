# C04-D02 / G7-02 本地结算入口回归准备

状态：**有限组件证据完成；G7-02 仍 BLOCKED。** 本报告仅记录独立回归准备，不是正式 C04/G2 验收。census 未改，仍为 8 组 / 33 行、32 NOT_RUN / 1 BLOCKED / 0 PASS；最终候选仍等待 C03 独立接受。

## 基线、候选与改动范围

- 冻结 PR20 候选基线：`4b5012f0abedde309a14b8a7d8201eca53d4fc31`，来自 accepted `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 的冻结候选；没有把 OPEN/Draft PR20 描述为已合并。
- 工作分支：`wt/v5-g2-c04-local-ingress-luna6-20261101-1043`。
- 最终本地提交 SHA：提交后记录于调用方 checkout；本报告不代表 PR20 已发布或合并。
- census 源文件哈希：`doc/v5-重构/V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json` 与 readiness 文档均未修改。
- 唯一允许且实际修改的 tracked 文件：
  - `dotnet/tests/Mahjong.TableService.Tests/B2TerminalSettlementTests.cs`
  - `doc/v5-重构/V5-G2-C04-LOCAL-INGRESS-DELIVERY-20261101.md`
- 测试源基线 SHA256：`400D0C0C3B7AC4F926E21CF0259F8BE2E4775F088311ADE728E70567D991123B`。
- 测试源候选 SHA256：见 `build/c04-d02-77b733b39106442ba2e13c3124896fc2/sha256-manifest.txt`。
- 生产源只读 SHA256：`TableCommandMapper.cs` `A67D6B7527D4C7CBDF778178CCC2FBB93790471E4D0B292397D3EE2F0B5DB84B`；`TableSessionHandler.cs` `EDB3E31C180446B06FF8C9AA4A96B10AEE2CD70C87775309561FA78C25C71212`；`ProtocolCommand.cs` `EB3C7599631BF3AFEA88069F2EEA4722D49A0690574D8C26B96D18DEA009FDA5`。

没有改生产实现、协议、项目/solution、CI、统一 verifier、Unity 资产、census 或接受报告。

## 已核实的生产信任边界

1. TableService 当前唯一玩家动作入口是 `CommandKind.SubmitAction`。`ProtocolCommand.cs:305-318` 校验 round、decision、control epoch、action、可选 tile IDs / meld type；该类型没有 LOCAL 来源、RoundSettlement、分数、支付或终局资产字段，也没有提交客户端结算资产的 command kind。
2. 真实链路为 `TableTestClient.SendAsync` → ClientWebSocket / loopback Kestrel → `TableSessionHandler.ReceiveLoopAsync` 解码及认证连接身份 → `DispatchCommandAsync:1380` → `TableCommandMapper.Map:1472` → `MapSubmitAction:360`。Tsumo / Ron 在 `TableCommandMapper.cs:437-459,504-529` 被映射为不含客户端计分事实的 typed `TableSettlementRequest`；`TableSessionHandler.cs:1582-1586` 交给 `Actor.SubmitSettlementAsync`。Round calculator / committed round facts 产生分数，projection 从同一已接受 calculation 产生和牌事件及终局分数。
3. 可真实上送的 match 层 `DeclareAgariYame`（actionType 13）被 `TableCommandMapper.cs:481-483` 在 Actor 前拒绝，错误为 `InternalError / command.type / MATCH_AUTHORITY_UNAVAILABLE`。它证明 match 权威缺失时 fail-closed，不证明存在 LOCAL provenance 检查。
4. MJC1 parser 会解析额外 canonical tags；当前 validator/mapper 只读取冻结字段。本回归将未知 tag 8=`"LOCAL"`、tag 9=`999999999` 随合法 Tsumo 一同上送。真实服务应用的是该合法 Tsumo action，Receipt digest 与整份 canonical payload digest 相同，wire 和局结果却与 actor 保存的权威 calculation 匹配。该结果证明未知字段没有成为服务端计分输入；**它不是 LOCAL 来源被识别或拒绝的证据**。因此不能把“任意字符串 LOCAL”或未知字段宣称为可信 provenance。
5. 现有 `RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring` 保留原样，仍只直达 `room.Actor.SubmitAsync`。它没有被重命名或冒充为 wire 测试。

### 最小可复现输入与结果

真实 loopback player session，fixture `DealerTsumoRound(roomId)`，`roomId=room-b2-local-ingress`、round=`room-b2-local-ingress-r1`、seat 0。连接通过测试 signer 签发临时 player ticket；秘密未写入证据或报告。scope、session、binding、owner 均从该连接的 WELCOME 取得；control epoch 和 decision ID 从该轮私有决策取得；stateVersion 取提交前服务快照。拒绝 request ID 固定为 `req-b2-local-ingress-match`，canonical command 是 `SUBMIT_ACTION`，fields 1/2/3/4 分别为上述 round、decision ID、control epoch、actionType 13。

实际响应为 `ERROR InternalError`, `fieldPath=command.type`, `detailCode=MATCH_AUTHORITY_UNAVAILABLE`，而非业务 Receipt。请求没有进入 actor / receipt store；transport error bookkeeping 与业务 receipt 分开观察。拒绝前后 actor stateVersion / commit version、eventSeq / recent event count、终局事件数、四家分数、136 个 tile ID 的逐实体位置、私有 ledger（通过真实 sync full snapshot 复读）及 settlement outbox 均无增量。

紧接着同一认证连接、同一 round/decision/version 提交固定 request ID `req-b2-local-ingress-tsumo`，动作 `DeclareTsumo`、winning tile 12，canonical payload 另附未知 tag 8/9。Receipt 为 Applied。测试比较公开 TsumoWin 的 han/fu/total points 与 `room.Actor.LastSettlementCalculation` 的 breakdown，并比较终局四家分数与该 calculation 的 settlement；总分守恒为 100000，terminal outbox 恰一条。原 envelope / request ID 完全相同地再发一次，服务返回 Applied + `Replayed=true`；version、eventSeq、scores 与 outbox 仍不增加。

这覆盖了当前可构造的 match-authority 拒绝及服务端算分对照，但**没有一个可代表“由 LOCAL 产生的结算资产”的真实 wire 输入**。当前合同无法判定某个 round settlement / score packet 是否来自 LOCAL，因为服务端入口根本不接收此类资产或来源凭据。G7-02 必须保持 BLOCKED。

## 测试和构建证据

测试项目是 xUnit v3 / Microsoft.Testing.Platform executable (`TestingPlatformDotnetTestSupport=true`)。`dotnet test` 在这个项目上只执行 MSBuild；一次 `dotnet test ... --filter ...` 返回 native exit 0，但没有测试 summary、逐例结果或 TRX，故不计 PASS。实际执行使用 fresh 构建的候选 DLL 及 xUnit v3 原生 runner。环境 SDK 为 .NET SDK 8.0.426；所有 artifacts、NuGet package cache、temp、XML 和 console 文件都在本工作树 ignored `build/c04-d02-77b733b39106442ba2e13c3124896fc2/`。

| 命令 | Native exit | 原始结果 |
| --- | ---: | --- |
| `dotnet restore dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj --packages build/c04-d02-77b733b39106442ba2e13c3124896fc2/nuget --artifacts-path build/c04-d02-77b733b39106442ba2e13c3124896fc2/artifacts --nologo -v:minimal` | 0 | `restore.log`；只还原目标测试及其项目引用 |
| `dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-restore --no-incremental --artifacts-path build/c04-d02-77b733b39106442ba2e13c3124896fc2/artifacts --nologo -v:minimal -nr:false` | 0 | 最终接受构建 `accepted-build.log`，0 warnings / 0 errors |
| `dotnet build/c04-d02-77b733b39106442ba2e13c3124896fc2/artifacts/bin/Mahjong.TableService.Tests/release/Mahjong.TableService.Tests.dll -noColor -noLogo -method Mahjong.TableService.Tests.B2TerminalSettlementTests.WireIngress_RejectsUnbackedMatchSettlement_ThenComputesTsumoFromAuthority -result-xml build/c04-d02-77b733b39106442ba2e13c3124896fc2/accepted-targeted.xml` | 0 | 1/1 passed，0 failed/errors/skipped/not-run |
| `dotnet build/c04-d02-77b733b39106442ba2e13c3124896fc2/artifacts/bin/Mahjong.TableService.Tests/release/Mahjong.TableService.Tests.dll -noColor -noLogo -class Mahjong.TableService.Tests.B2TerminalSettlementTests -result-xml build/c04-d02-77b733b39106442ba2e13c3124896fc2/accepted-b2.xml` | 0 | 24/24 passed，0 failed/errors/skipped/not-run |
| `dotnet build/c04-d02-77b733b39106442ba2e13c3124896fc2/artifacts/bin/Mahjong.TableService.Tests/release/Mahjong.TableService.Tests.dll -noColor -noLogo -result-xml build/c04-d02-77b733b39106442ba2e13c3124896fc2/accepted-full.xml` | 0 | 204/204 passed，0 failed/errors/skipped/not-run；这是 TableService 全套普通套件，不是 PostgreSQL 验收 |

上表目录为 `build/c04-d02-77b733b39106442ba2e13c3124896fc2`。原始 console、XML、native-exit 文件及 SHA256 逐项见同目录 `sha256-manifest.txt`（manifest SHA256 `EE5A9733D26C09581F320FED8D6E82C180B2D4E2027C8C52C49FF0F4371FABF9`）。三份最终 XML 中 PG 专属 skip 数均为 0；本次没有启动或连接 PostgreSQL，故真实 PG 阶段为 NOT_RUN，不以 memory store 替代 PG 结论。

### 首次失败均保留

- 隔离 artifacts 下首次 `build --no-restore`：native exit 1，`NETSDK1004 project.assets.json missing`；在同目录完成真实 restore 后 fresh build 成功。复现原件在 `first-no-restore-reproduction/console.log` 和 `native-exit.txt`。
- 初版断言触发 xUnit2031（`Assert.Single(collection.Where(...))`）；改用 predicate overload。原 console 为 `fresh-build.log`。
- 增加私有 ledger 复读时先误选领域 view 类型，再误用 `Ledger` 而不是 wire `LedgerEntries`；两次 fresh build 分别报 CS1503 / CS1061，修正后通过。原 console 为 `final-build-r2.log`、`final-build-r3.log`。
- 首次定向运行因测试错误要求 pre-actor ERROR 回显 requestId 而超时（生产 ERROR 不回显 requestId）；修正谓词后通过。失败用例 XML/console 保留为 `targeted.xml`、`targeted-runner.log`。
- 增加 authority calculation 对照时曾把 `Score.Han`（含 dora 的计分 han）与公开 `TotalYakuHan` 混比，首次失败 Expected 3 / Actual 2。改为比较同一映射语义 `Breakdown.TotalYakuHan` 后最终定向、B2 类和整套均通过；原件 `verified-targeted.xml`、`verified-targeted.log` 保留。
- `dotnet test` 的 exit 0 仅生成构建结果且没有 case output；其返回不作为任何测试成功证据。

## 缺陷与未覆盖项

### C04-D02-LOCAL-01 — MAJOR / BLOCKED

- **位置：** `dotnet/src/Mahjong.Contracts/ProtocolCommand.cs:305-318`（SubmitAction schema validation）；`dotnet/src/Mahjong.TableService/TableCommandMapper.cs:360-529`（可接受 action 到 server intent 的唯一映射）；`dotnet/src/Mahjong.TableService/TableSessionHandler.cs:1380-1604`（真实 ingress / actor dispatch）。
- **复现：** 当前协议不能编码一个带可验证 LOCAL provenance 的结算资产。唯一实际 match-level 试探输入是报告上节 actionType 13 的 `SUBMIT_ACTION`；服务端拒绝为 `MATCH_AUTHORITY_UNAVAILABLE`。另将未知 tag 8 `LOCAL` 与 tag 9 `999999999` 附加到合法 Tsumo，服务端忽略未知标签并按自己的合法动作完成服务端结算；这不是一条被服务端识别为 LOCAL 的请求。
- **实际 vs 期望：** 实际保护的是服务端计分权威及未实现 match-layer AgariYame fail-closed；不存在 LOCAL 来源判别或资产拒绝入口。G7-02 要求存在一条真实服务入口能识别并拒绝 LOCAL 产物，当前输入模型不能表达该断言。
- **证据 SHA256：** `accepted-targeted.xml` `EF0FD48D7028C8BE7B99C68DD2F503B393BB991732EA6EB631DDB452F822B754`；`accepted-full.xml` `C51E0D529F8BB019B4A9D417D8762C6D187EB7553685DD22E3FC8FB7882DD7CC`；候选测试源码 `CD6EE2F00EE7974B778DE1F6E529BCBCBAFCA926AA98E2B7BD134DAC3D9997E1`。原始失败记录另列于 manifest。
- **最小修复要求：** 由后续明确获批的工作先定义服务端可验证的 LOCAL provenance / settlement asset 契约及入口，再在 handler 入口做来源/资产拒绝并与合法 typed intent 分流；不得信任客户端自填字符串或未知字段。当前准备任务不扩展生产实现或协议。
- **验收断言：** 独立持有真实 owner/scope/control/round/version/request identity 的 loopback client 上送可验证 LOCAL 资产，handler 返回明确拒绝码，actor/receipt business state/outbox/ledger/scores/events/136 entities 全零业务增量；同 channel 合法 tsumo 仍与服务端 calculator、终态分数、唯一 commit/outbox 一致；相同 request replay 零额外业务作用。

### 状态分栏

- **PASS（组件）：** 最终 fresh 定向 1/1、B2TerminalSettlementTests 24/24、TableService 全套 204/204；仅覆盖真实 loopback memory-host、handler/mapper/actor/calculator 与相同请求重放。
- **BLOCKED：** census 的 C04-G7-02 / C04-D02 真实 LOCAL 来源和 LOCAL 资产拒绝语义；原因是当前 wire/schema 没有该可信类型或入口。
- **NOT_RUN：** 正式 C04 33 行、最终 C03 候选绑定、Player/Unity/设备、真实 PostgreSQL 阶段、远端 B2 整场、G3/G4/G5。没有运行 Unity、Player、adb、Android/iOS/设备、C03 测试矩阵或 `Local\\MahjongV5P45UnityValidation`。
- 不增加或重新解释 census 分母，不把此交付宣称为 G2/G3/G4/G5 或正式 C04 PASS。
