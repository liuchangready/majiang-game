# V5 N-1a caller 独立验收与统一 PR19

日期：2026-10-25。Bridge：`thread_20261025_051931_34f1`；沿用原 caller/receiver 会话，未转派。

## 结论与冻结提交

**N-1a Domain 组件 PASS，已通过 approve_task 归档。** 基线 `8254c9b783740ddf519cebc15bb50a8f9974cf58`，代码 `5f377c8a62d432b1bd6f474f8f08ca8f609bbabe`，交付文档 `2cdf59eeb4b6370a64df1f1a11c1483d402ad9f2`。本文件是 caller 的验收证据，不修改实现。

审查全部 10 个交付文件，并核对 V4 LegalActionProvider、RoundTransitionPlanner、TileMovement、RiichiKanValidator。Chi/Pon/明杠 claim/暗杠/立直有真实物理转移、阶段/座次/实体校验，多席声明等齐后按 Ron > Pon/明杠 > Chi 裁决；立直扣点/供托、门清/听牌/摸切锁、杠宝与岭上时序、不可变状态/fingerprint、Recovery v2/checkpoint schema 2 均通过本次复验。

AgariYame 已由 MatchSession.ChooseDealerEnd 负责，不增加单局终态动作。类型级食替在 V4 没有实现，本组件没有新增规则。额外实体层翻宝、恢复、checkpoint 文件为实现本任务状态保存所必需，caller 已复核其准确路径与原因；未扩展为三绝技或 actor/投影改造。

## 独立验证

工作区：`D:/majiang-wt/n1a-round-decisions`。CLI：`D:/Unity/dotnet/dotnet.exe`。所有命令 exit 0。

| 命令 | 实际结果 |
|---|---|
| `dotnet build dotnet/Mahjong.sln -c Release --no-incremental` | 0 warnings / 0 errors |
| `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build` | 346 passed / 0 failed / 0 skipped |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release` | 显式构建后 316 passed / 0 failed / 0 skipped |
| `dotnet run --project <证据目录>/probes/ReviewProbes.csproj -c Release` | 4 passed / 0 failed |
| `git diff --check 8254c9b..HEAD`、`git status --porcelain=v1` | 无差异格式错误；交付工作树 clean |

独立探针直接引用本次新编译的 Domain DLL，只写独占 TEMP，不改仓库测试、helper 或 verifier。Fixture.cs 摘取交付的 136 实体摆牌器；行为断言由 caller 按 V4 源码编写：

1. Chi 先入队、Pon 后入队，两次中途冷恢复均保留声明，第三席 Pass 后才裁决 Pon。
2. 连续四次暗杠，每次恢复都保留 fingerprint；最终五张宝牌、四次岭上、四副杠、两张手牌，136 张守恒。
3. 暗杠未消耗原摸牌时保留实体，随后仍可门清立直，正确扣 1000 点并加棒，冷恢复一致。
4. 明杠欠翻宝后立即暗杠，先清旧欠翻再翻暗杠宝，最终三张指示牌且 pending=0，恢复一致。

## 原始证据

目录：`C:/Users/sncdc/AppData/Local/Temp/codex-n1a-review-383a503e73a34c69bb7c4bb9560b03ea/`。

| 文件 | SHA256 |
|---|---|
| build.log | D903772F7750D7A15A86B2EA70178E765B95E80E12D23B5D75B479C6001C7DE2 |
| domain.log | B0933C32D1B4FDF53A430700FEDFD969B77AA48F91E6AAC4236E49C0A6D1B4AF |
| runtime.log | FF6349E75D2D2A0A372FD31A033B6B41A95DC8E5365F11D3CC5859BCE720FC39 |
| probes.log | EC45C1C5A373BA06EB37D85395B742D2B0014E405536D01E457D5F4E1703C814 |
| probes/Program.cs | 556E8B5E490D7C3ACC3DD71ED19E6BF8C9DA3D9C9AC5EA63C522884E7EED030A |
| probes/Fixture.cs | 1EBC1F036A19A152004290E41F3A96086534EE70009B90603D1AC5C70E8DE909 |
| probes/ReviewProbes.csproj | F7E227C74CB032AD78A576F0A2C83122B37B460583EA725C69F3DC76FA635649 |

证据中的 exit 与计数来自原生进程实际返回，不使用旧 DLL、空测试或 skip 获得绿色。

## 范围、已知后续与发布

D-N1a-01（杠/岭上 facts-backed checkpoint）与 D-N1a-02（Chi/Pon/Riichi Domain→协议事件投影）仍为已记录的下游责任；本任务的无 facts Domain/checkpoint 合同已验证，不宣称带 facts 的杠后恢复已通。AI/超时接线、双立直/一发/振听及计分宿主事实仍待后续。N-1b、N-2 整体接线、N-3、G1/G5 均未因此关闭，V4 仍唯一生产。

PG/WSS、Unity/IL2CPP、设备、跨物理网络、Kotlin 全量和本次远端 CI 均 NOT_RUN，不以历史结果替代。

沿用唯一开放 PR19 `feat/v5-1-integration -> v5`，不创建子任务 PR。远端头已用 gh 与 git ls-remote 核对为冻结基线。20 项最近任务账本核对：本任务是唯一 completed 且尚无 MR 登记的任务；同批 N-2 返修中、N-3 待验收，其产物不作为已验改动发布。本次把本任务两笔交付及本验收记录一次普通快进推送到既有 PR19。隔离集成树的 dotnet 内容与已验交付完全一致，无代码二次改动。主 checkout 的用户未提交文件保持原样；MR 不合并、不部署。发布 SHA、远端校验及最终同步状态由 record_merge_request 记录。
