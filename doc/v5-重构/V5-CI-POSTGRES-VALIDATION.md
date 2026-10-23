# V5 CI 真实 PostgreSQL 统一门禁验证报告

- 分支：`wt/v5-ci-postgres-20261024`（worktree `D:\majiang-wt\ci-postgres-20261024`）
- 基线：`a08629164935af1269e0bc1061f693371aed13ff`
- 本报告覆盖的提交：`c66a8bc`（第一轮交付）→ `92ed402`（P1 返修，当前 HEAD）
- 远端 CI（GitHub Actions `v5-verify`）：**NOT_RUN**（本轮约束：不 push、不建 PR、不触发远端；由调用方审阅后统一收口既有 PR19）

## 1. 结论摘要

| 项 | 状态 | 证据 |
|---|---|---|
| 本地统一门禁（真实 PG，Release，HEAD `92ed402`） | **PASS** | 735 pass / 0 fail / 0 skip，退出码 0（§5） |
| 包装器自测 VerifyV5PostgresCi.Tests（HEAD `92ed402`） | **PASS** | 154 checks / 0 failures，退出码 0（§4.2） |
| P1 资源归属缺陷（独立审查发现） | **已修复并有回归钉死** | §3、§4 |
| 远端 CI | **NOT_RUN** | 未推送、未触发 |
| G1 / G5 门禁、Unity/IL2CPP/Android/iOS 生产平台 | 不涉及、不声明 | 仅 .NET V5 树 |

## 2. 背景与交付物

基线 `a086291` 的 CI 失败恰为一行：`Mahjong.LobbyService.Tests` 89 例中 12 例因 runner 上
`MAHJONG_LOBBY_TEST_POSTGRES` 未设置而 skip，统一门禁 `tools/verify-v5.ps1`（未改动，
SHA-256 `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`）对任何
skip>0 的行判 FAIL。修复 = CI 经新包装器提供真实一次性 PostgreSQL 16.10。

交付文件（本报告发布时的 SHA-256）：

| 文件 | SHA-256 | 状态 |
|---|---|---|
| `tools/ci/verify-v5-postgres.ps1` | `203E91346E2172B871133CBFA263DADF8FEE1408AB6E5ADAFEBCE80218CABDB7` | `92ed402` |
| `tools/tests/VerifyV5PostgresCi.Tests.ps1` | `E193F5B0051D07ED6B38299AAE13E13A15023609316BBE0D263DB763308991C1` | `92ed402` |
| `.github/workflows/v5-verify.yml` | `842588DCFAD4FF70EA1F243385699F4168EA75DD82C960E46B5F469B8FACC50E` | `c66a8bc` |

固定输入：PostgreSQL 16.10-1 EDB binaries zip（`D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip`，
SHA-256 = pg-common 锁定值 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`，预检比对一致）；
dotnet `D:\Unity\dotnet\dotnet.exe`（8.0.426，仅本机显式参数，wrapper 本身从 DOTNET_ROOT/PATH 解析，无机器本地硬编码）；
本机测试端口仅 55460/55461（绝不使用 55450-55452）。

## 3. 独立审查发现的 P1 缺陷与返修（`92ed402`）

**缺陷**（第一轮 `c66a8bc`）：wrapper 只拒绝 worktree 内的 Root，不拒绝调用前已存在的
Root；finally 仅凭 Test-Path 就调用 accepted cleanup。合法 `pg-owner.json` 的 owner 是
pg-lobby-verify 通用脚本常量 `mahjong-p53-pg`，不能证明属于本次调用——因此 wrapper 会
删除历史调用的根目录，甚至在 precheck 端口冲突时进入对外来根的清理。

**审查方复现**（证据保留于本仓库
`artifacts/verify-v5/review-existing-root-d1a1d0952c58446fb6bc6254643be6ff/pg/`）：
预置合法 marker（owner=`mahjong-p53-pg`，port=55462，database=`mahjong_lobby_test_review`，
createdUtc 为过去时间）+ `preexisting-sentinel.txt`，以坏 zip 调 wrapper → 观测
`SENTINEL_BEFORE=True`、start failed exit 1、cleanup PASS、`PREEXISTING_ROOT_SURVIVED=False`
（外来根连同哨兵被删除）。

**返修内容**：

1. `Test-WrapperPathExists`：在任何副作用产生前拒绝**一切已存在**的 Root——合法 marker
   目录、空目录、悬空重解析点（Test-Path 会穿透 reparse 而漏报）、以及同一目录的规范化
   别名（大小写、尾分隔符、正斜杠、8.3 短名经文件系统解析）。拒绝即 `exit 2`（BLOCKED），
   不建 LogDir、不建 staging、不设环境变量、不碰端口。
2. `Test-WrapperClusterOwnership`：finally 的归档/清理一律先过所有权判定——marker 的
   root/port/database 字段须与本调用完全一致，且与 start 成功时记录的 marker SHA-256
   一致；否则判 `foreign`：**绝不归档、绝不删除**，cleanup FAIL，整体失败但保留原始错误
   与验证器退出码。本次调用 start 失败留下的半初始化根（marker 缺失或字段吻合）仍判
   `owned` 并照常清理。
3. `wrapper-summary.json` 的 cluster 段新增 `ownership` 字段（owned / owned-unmarked /
   foreign / absent）。
4. accepted pg scripts（`dotnet/scripts/pg-lobby-verify/*.ps1`）保持零改动；修复完全
   不依赖端口判定绕过。

## 4. 故障回归证据（自测，154 checks / 0 failures，exit 0）

命令：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\VerifyV5PostgresCi.Tests.ps1 -DotnetPath D:\Unity\dotnet\dotnet.exe -ZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip`

- 第一轮（`c66a8bc`）：108 checks / 0 failures（证据 `%TEMP%\VerifyV5PostgresCi-20261024-034557-71c4abf5`）
- 第二轮返修后（`92ed402`）：**154 checks / 0 failures**（证据 `%TEMP%\VerifyV5PostgresCi-20261024-040600-008f08be`）

新增回归（P1 钉死）：

- **S7 预存在根拒止**（13 checks）：预置合法 marker + 哨兵 + 坏 zip 调 wrapper →
  exit 2、BLOCKED 消息点名路径、LogDir 未创建、哨兵与 marker 哈希前后一致、四个
  `MAHJONG_LOBBY_*` 环境变量保持调用前值；同一目录的尾分隔符别名与大小写别名同样
  exit 2 且零副作用。
- **S8 既有 listener + 预存在根**（7 checks）：静态守卫先于端口 precheck 触发（控制台
  无 "already in use"），外来 listener 存活、marker/哨兵哈希不变、零创建。
- **S6 新函数单元**（13 checks）：悬空 junction 必拒；owned/foreign（错 port/root/db、
  marker 损坏、哈希被替换）/owned-unmarked 判定矩阵；文件 SHA-256 与 Get-FileHash 一致。
- **S2/S4 强化**（4 checks）：本次调用半初始化根在 setup 失败后仍判 `owned` 并清理
  （detail `owned cluster deleted`）；precheck 冲突时 ownership=absent、cleanup
  skipped-PASS（绝不清理他人目录）。
- 原有 S0/S1/S3/S5 全绿：S1 仍为真实 PG + 真实统一验证器端到端（fixture 经真实门禁，
  非 mock）；S3 桩退出码逐字传播（mock 仅用于错误传播控制）；S5 清理失败时验证器
  exit 0 不被掩盖。多余的 mock 不计入真实验证。

## 5. 唯一一次真实统一门禁（当前 HEAD `92ed402`）

命令（在仓库根执行）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\verify-v5-postgres.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -PgZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip -Port 55460
```

- **退出码 0**；包装器 10 阶段全 PASS；`plannedExit 0`、`failedStages 0`
- head/分支：`92ed402ed48c0426ebd41d5760b6b61279d475f0` / `wt/v5-ci-postgres-20261024`
  （summary.log 与 run-info.txt 双处记录）
- 集群：根 `%TEMP%\mahjong-p53-pg-ci-20261024-040908-b9c461ff`（唯一 TEMP 根）、端口
  55460、库 `mahjong_lobby_test`；server identity
  `…/mahjong-p53-pg-ci-20261024-040908-b9c461ff/data (verified)`；`ownership: owned`、
  `rootDeleted: true`；环境四变量 `restoredToPreCall: true`
- **计数（summary.log 逐行）**：

| 项目 | TOTAL | FAILED | SKIPPED | RESULT |
|---|---|---|---|---|
| Mahjong.Client.Tests | 132 | 0 | 0 | PASS |
| Mahjong.Domain.Tests | 313 | 0 | 0 | PASS |
| Mahjong.Ai.Tests | 14 | 0 | 0 | PASS |
| **Mahjong.LobbyService.Tests**（原 CI 失败行） | **89** | **0** | **0** | **PASS** |
| Mahjong.Contracts.Tests | 82 | 0 | 0 | PASS |
| Mahjong.TableRuntime.Tests | 25 | 0 | 0 | PASS |
| Mahjong.Client.Wss.Tests | 80 | 0 | 0 | PASS |
| （helper，仅构建：PostgresProbe、Outbox.CrashWorker） | — | — | — | PASS |

  合计 **735 pass / 0 fail / 0 skip，OVERALL: PASS（9 rows PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN）**
- 真实性证据：`pg/postgres-evidence.jsonl` 14 行由真实 lobby 测试写入（`server.identity`：
  PostgreSQL 16.10 compiled by VC++ 1944, 127.0.0.1:55460, owner `mahjong-p53-pg`）
- 证据目录（保留、未删除、未入库）：
  `artifacts/verify-v5/20261024-040908-pg-6a1a3914/`，其 `pg/evidence-sha256.txt` 清单
  （12 项，含 wrapper-summary.json、cluster server logs、owner-marker、迁移/启动/清理
  日志）：

```
73488546A8FAEEAD46CE9C6C01B2C6228BE6AFE78576BB4CE44573425366D9C3  cleanup.log
265693BF2EF6F2BAFD79AE4423DD54CD1224ECC17243D1095F845F213AD41A84  env-restore.jsonl
3BD016F625C9050F7C07A1335637F3319E41962FF049B3F56334C6D5C061766D  migrate.log
DC9F1A917368D9F9C23E4A7106071E32564D2836C4EEC7CAAF8BF28A19353CEA  owner-marker.json
B38172C072A6631B26CDF36E06D69AF29E7FE0B1E88D87599C5A7715E716C3FA  postgres-evidence.jsonl
36D010506DAC6A42958897A2A3218C8647B6BE60EFD62F1C4B8BBB24D37134CE  start-postgres.log
1338751888A55509B275A5D11E27061C057E1530C3B1A1BAAA4FAB59E2FB2AE7  verifier-console.log
3F416A81D6796F21244D80796737DB161D72255B695485EBD058651EB84C464B  wrapper-steps.log
A3E0193D2F623701EBA03567F35ACDE0C2BFF2D7DC70E62F0179FCCDC77A2A8B  wrapper-summary.json
E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855  root-logs\pg_ctl-start-20261024-040931-859.err
62D393F0AA1D8F61EF84BB53DE0FF2071FDD0D9C5E0054DC7CDBB64EC01A10F6  root-logs\pg_ctl-start-20261024-040931-859.out
931C487CA3E9688C95B1C8B2A299647B6D1A2385FDCA3F598C898DB622B65CF3  root-logs\postgres.log
```

- 第一轮门禁（`c66a8bc`，修复前 wrapper）：同为 735/0/0、exit 0，证据
  `artifacts/verify-v5/20261024-034843-pg-237a9f30/` —— 两轮均为各自 HEAD 的唯一一次
  实跑，未重复刷数。
- 清理验证：运行后 postgres 进程数 0、集群根已从磁盘删除、TEMP staging 已拆除。

## 6. 失败路径行为矩阵（全部有自测钉死）

| 场景 | 行为 | 场景 |
|---|---|---|
| Root 已存在（合法 marker / 空目录 / 悬空 junction / 别名） | `exit 2`，零副作用，marker/哨兵/环境不变 | S6/S7/S8 + 审查复现场景 |
| 端口被外来 listener 占用（Root 全新） | precheck 拒止，非零退出，listener 存活，不创建/不清理任何目录 | S4 |
| setup 失败（如坏 zip），半初始化根属本次调用 | 整体 exit 1，`owned` 判定下仍清理本次根，环境还原，verifier=NOT_RUN | S2 |
| 验证器失败（桩 exit 3） | 退出码逐字传播，清理照常，不回退内存/不吞码 | S3 |
| 清理失败（FileShare.None 锁） | 整体 exit 1，验证器 exit 0 保留不被掩盖，根保留并留证 | S5 |
| finally 时 marker 字段/哈希与本次不符（外来根） | 判 `foreign`：绝不归档/删除，cleanup FAIL，保留原始错误与验证器退出码 | S6 单元 + wrapper 门控逻辑 |

## 7. 范围与限制声明

- 本报告仅覆盖 V5 .NET 树（engine/protocol/client/server 迁移件）的统一验证门禁与
  其 CI 包装；**不声称** G1、G5 任何门禁已通过，**不声称** Unity/IL2CPP/Android/iOS
  等生产平台已验证（需 Unity 授权编辑器与平台工具链，另行跟踪）。
- 全系统默认且唯一生产架构仍为 V4（TCP MJ-TCP/1）；V5 为迁移中实现。
- 远端 GitHub Actions 结果以原始测试框架日志为唯一判定依据，本轮未触发任何远端运行。
- 历史 Client run39970580589 根因仍为未决事项，与本门禁无关。

## 8. Codex 独立验收与批次集成（2026-10-24）

原任务 `thread_20261024_015954_9287` 第二轮交付 `49fea41`（代码 `92ed402`）经调用方独立审查通过，已调用 `approve_task` 归档。调用方沿用原工作区与任务，未转派。

独立复跑：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5PostgresCi.Tests.ps1
```

在交付 HEAD `49fea41b95b9b6c79830cb42407af45b5a616be4` 上得到 **154 checks / 0 failures，exit 0**。原始控制台为 `artifacts/review-ci-pg-20261024-041433/selftests.log`；场景证据为 `%TEMP%/VerifyV5PostgresCi-20261024-041434-7374829c`。真实 PG fixture 与故障控制场景分别计数，未将桩执行视为真实业务门禁。既有合法 marker、哨兵、别名、外来 listener 保护及半初始化本次资源清理均通过。

随后在同一分支合并 PR #19 已验收基线 `3e78367538b4f7520a1d414c82ef3155507a5206`，得到批次代码 HEAD `b7c52d9bd76b5195f073f93b5a0e742a1a4cf532`，无冲突；新增的 P3.2a/P3.3 测试使统一门禁总数从 735 增至 814。在该 HEAD 执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-postgres.ps1 `
  -Configuration Release -RunnerMode dotnetTest `
  -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -Port 55460 -LogDir artifacts/verify-v5/caller-ci-pg-20261024-0417
```

结果 **814 passed / 0 failed / 0 skipped，exit 0，9 rows PASS，OVERALL: PASS**：Client 132、Domain 313、Ai 14、Lobby 89、Contracts 82、TableRuntime 104、WSS 80；两个 helper 仅构建。solution build 与 scratch 12-DLL 闭包均通过，未改 Unity 资源。包装器与 accepted PG 脚本、验证器保持交付代码，未过滤 skip 或缩减发现范围。

证据目录 `artifacts/verify-v5/caller-ci-pg-20261024-0417/` 包含完整测试日志、`summary.log`、`run-info.txt`、逐次运行元数据与 `pg/`。真实 PostgreSQL 16.10 使用唯一 TEMP 根、loopback 55460 和专用测试库，迁移两次成功，测试写入的 `pg/postgres-evidence.jsonl` 共 14 行。包装器记录 `ownership=owned`、`rootDeleted=true`、`restoredToPreCall=true`、`plannedExit=0`、`failedStages=[]`；调用方逐项核对 `pg/evidence-sha256.txt` 全部匹配，并确认 55460/55461 无 listener。控制台留存于 `artifacts/review-ci-pg-20261024-041433/unified-console.log`，批次 SHA-256 清单为同目录 `review-sha256.txt`。

调用方另行读取 PR #19 上次失败 run `40006967267` 原始日志，确认 Lobby 行为 89 总数、0 failed、12 skipped；日志留存 `artifacts/review-ci-pg-20261024-041433/previous-ci-40006967267.log`。这不解释历史 run39970580589。

本地验收时远端新提交 CI 仍为 **NOT_RUN**；调用方接续统一推送既有 PR #19（`feat/v5-1-integration` → `v5`），远端结论以该提交实际 workflow 日志及任务 MR 记录为准。本节本地 PASS 不代表 G1/G5、Unity/设备或生产部署通过。
