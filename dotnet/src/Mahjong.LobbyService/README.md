# Mahjong.LobbyService (P5.3)

状态：**P5.3 独立组件实现；不是 G1/G3 通过，不是全链迁移通过。** 基线 `fb6f751`，工作树 `wt/v5-p53`。V4 仍是生产基线。

本工程只实现大厅：受限游客会话、公开目录、建房/加房/观战、匹配状态机、assignment/席位预订、短期一次性 `TableTicket`、JWKS/轮换、幂等回执。它不依赖 `Mahjong.Domain` / `Mahjong.TableRuntime` / `Mahjong.Client` / TableService，也不读取私有牌局状态。

## 边界

- 唯一共享编译依赖：`Mahjong.Contracts`（协议版本常量）。
- 唯一桌侧输入：`IRoomProjectionReader` 的公开 `room_projection`（roomId/lifecycle/人数/版本/更新时间）。不读 `table_event`、手牌、牌山、种子、出千账本、租约或 `ownerEpoch`。
- TableService 不由本服务同步调用；真实 WSS 握手/准入属于 P5.1，当前未联调。
- TableTicket 只带 `assignmentId`，**不带 TableService `ownerEpoch`**。TableService 在 WELCOME 下发它自己的代次。

## HTTP API（本地/入口前置 TLS）

| 方法/路径 | 说明 | 幂等 |
|---|---|---|
| `POST /v1/auth/session` | 受限游客会话；必须携带客户端高熵 `bootstrapSecret`；`authMode` 只接受 `GUEST`，OIDC 返回 501 | `Idempotency-Key` |
| `GET /v1/auth/session` | 当前游客/能力 | 只读 |
| `GET /v1/rooms?cursor=&limit=` | 公开分页目录（keyset） | 只读 |
| `GET /v1/rooms/{id}` | 房间公开摘要 | 只读 |
| `POST /v1/rooms` | 建房；`maxPlayers` 固定 4、`rulesetVersion=1` | `Idempotency-Key` |
| `POST /v1/rooms/{id}/join` | 加房；`preferSeat` 只是偏好 | `Idempotency-Key` |
| `POST /v1/rooms/{id}/spectate` | 观战；与玩家角色互斥 | `Idempotency-Key` |
| `POST /v1/match/tickets` | 进入 `QUEUED` | `Idempotency-Key` |
| `GET /v1/match/tickets/{id}` | 查询匹配（仅 owner） | 只读 |
| `POST /v1/match/tickets/{id}/cancel` | 取消/已终态幂等返回 | `Idempotency-Key` |
| `POST /v1/table-tickets` | 按 assignment 换取短期票据 | `Idempotency-Key` |
| `GET /.well-known/jwks.json` | 公钥集（无私有参数） | 只读 |
| `GET /health/live`、`/health/ready` | 存活/就绪 | 只读 |

所有有副作用的 HTTP 命令都要求 `Idempotency-Key`。回执按 `(principal, requestId)` + 规范化载荷摘要持久化；同键同载荷返回原结果（`Idempotent-Replayed: true`），异载荷 `409 REQUEST_ID_CONFLICT`。

### 游客 bootstrap（R1 修复）

`deviceId` 只是公开的去重/显示提示，**不是认证凭据**。`POST /v1/auth/session` 还必须包含客户端用 CSPRNG 生成并本地保存的 `bootstrapSecret`（32–128 个 URL-safe 字符）。服务端只保存其 SHA-256：

```json
{"deviceId":"install-hint-01","bootstrapSecret":"<32-128 URL-safe random>","displayName":"Guest"}
```


- 同一 `(deviceId, bootstrapSecret)` 的不同 requestId：同一游客账号，可合法双开多个 session。
- 同一 `deviceId`、不同 bootstrap：不同账号；不知道 Victim bootstrap 的调用者不能取得 Victim 账号、session 或 assignment 票据。
- 相同 requestId + 不同 bootstrap：receipt principal 不同，不会回放 Victim token。
- 丢失 bootstrap：没有恢复路径；以新 bootstrap 创建新游客身份。P5.3 不实现账号找回，也不把 deviceId 当账户恢复。
- 同一 requestId + 同一 bootstrap：幂等回放原 session token。
- 服务端不回显 bootstrapSecret，不写日志。

## 匹配

`QUEUED -> RESERVED -> CONFIRMED -> CANCELLED/EXPIRED`。

- 临时规则：同 `(mode, rulesetVersion, region)` 按创建时间 FIFO，四人成桌；不宣称段位/延迟/公平策略。
- `RESERVED` 有 TTL（默认 15s，可配 5–60s），到期释放 assignment/席位并转 `EXPIRED`。
- 同一账号同一时间只能有一个活跃匹配票据；取消/过期竞态由存储事务串行化，最终只会有 `CANCELLED` 或 `EXPIRED` 之一。
- 一个账号全局只能有一个活跃 lobby assignment（跨房间、跨玩家/观战角色）；不能同时持有两个房间的玩家席位。已有活跃 assignment 时不能入队/建房；已有活跃匹配票据时不能加房/观战/建房。
- 取消/过期释放后可以重新加入；历史遗留的双活跃 assignment 在签发/消费票据时以 `ACCOUNT_ALREADY_SEATED` 拒绝，不能继续消费成双席位。
- 游客排位默认关闭；只有显式 `AllowGuestRankedMatch=true` **且** `ProductDecisionAcknowledged=true` 时才进入临时 FIFO ranked 队列。默认配置下 `RANKED_MATCH_DISABLED`。

## TableTicket

JWS compact，`alg=ES256`、`typ=JWT`、`kid`，ECDSA P-256（BCL `System.Security.Cryptography`）。Claims：

```text
iss=mahjong-lobby  sub=accountId  aud=mahjong-table
room_id  assignment_id  role=PLAYER|SPECTATOR  seat_id?  jti  iat  exp
protocol_major  ruleset_version  projection_version
```

严格校验：签名、`kid` 是否活跃、`alg` 仅 ES256、`typ`、`iss`、`aud`、role、seat（玩家 0..3，观战无 seat）、room、assignment、account、`jti`、`iat/exp/nbf`、TTL 上限、协议/规则/投影版本；payload/header 白名单拒绝未知/重复 claim（包括 `owner_epoch`）。票据 TTL 默认 60s，最大 300s。

- 签发同时持久化 `lobby.table_ticket` 行（jti/assignment/account/room/role/seat/exp/kid），不是纯无状态 JWT。
- 新票据签发会把同 assignment 的旧活跃票据标记 `superseded_at`；重连用新票据，不延长旧票据。
- `TableTicketConsumer`（应用端口，当前无公网 HTTP 路由）在**同一存储事务**内校验票据记录、assignment 席位、匹配票据，写 `table_ticket_consumption(jti 唯一)` 并确认 assignment/`table_confirmed_at`。重复 jti 返回 `TICKET_ALREADY_CONSUMED`；换新票据重连可再次消费，但不会产生第二个活跃席位。
- JWKS 只暴露 `kty/use/alg/kid/crv/x/y`；轮换保留旧公钥至 overlap 结束，`Retire` 后旧票据 `TICKET_KEY_UNKNOWN`。密钥不落日志/客户端。

## 存储与事务

`ILobbyStore` 端口：

- `InMemoryLobbyStore`：仅契约测试。每个事务克隆已提交状态，成功才发布，异常即回滚；**不是持久化证据**。
- `PostgresLobbyStore`：Npgsql 8.0.9 + PostgreSQL（timestamptz/部分唯一索引）。命令、回执、房间/assignment、匹配、票据消费同一事务；`lobby.request_receipt` 唯一键 + advisory lock；active seat/account/assignment 唯一部分索引是最终防双预订栅栏；票据/assignment/匹配行使用 `FOR UPDATE`（统一 assignment → ticket → match 锁序）。
- 迁移：`Migrations/001_lobby.sql`（schema `lobby`）。`Lobby:AutoMigrate=true` 仅用于受控环境显式执行；默认关闭。
- 公开投影读取：`PostgresRoomProjectionReader` 只 SELECT `{ProjectionSchema}.room_projection` 的公开列；表由 P5.1/TableService 拥有，本迁移不创建。

## 临时产品限制（未替产品定案）

`LobbyOptions` / `appsettings.json` 显式暴露并限制：

- `AllowGuestRankedMatch=false`（默认）、`ProductDecisionAcknowledged=false`；开启需要显式确认。
- `SpectatorDelaySeconds` 只接受 0；非 0 启动失败，不伪造延迟观战。
- `MatchRule=FIFO_TABLE_FILL_V1_TEMPORARY`；其他规则启动失败。
- 游客账号保留 7d、回执 7d、会话 24h 为临时配置；清理/导出任务未实现。
- `GET /v1/capabilities` 明确返回 `ProductDecision=PENDING` 与 `LaunchBlocked`（OIDC/排位/资产/观战延迟/匹配策略/数据保留/真实桌集成/公网 HTTPS）。

## 运行

```powershell
# 开发内存模式（仅本地验证）
D:\Unity\dotnet\dotnet.exe run --project dotnet/src/Mahjong.LobbyService/Mahjong.LobbyService.csproj --urls http://127.0.0.1:5183

# 独立测试（不修改 Mahjong.sln）
D:\Unity\dotnet\dotnet.exe test dotnet/tests/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests.csproj -c Release

# PostgreSQL 集成测试（可选；未设置则显式 Skip/NOT_RUN）
$env:MAHJONG_LOBBY_TEST_POSTGRES='Host=...;Database=...;Username=...;Password=...'
D:\Unity\dotnet\dotnet.exe test dotnet/tests/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests.csproj -c Release
```

生产启动必须提供 `Lobby:TicketPrivateKeyPem`（或密钥库等价物）；`Production` 下内存存储和临时密钥被拒绝。HTTPS 由受控入口终止，本任务未配置证书/反向代理。

## 已覆盖 / 未覆盖

**已覆盖（内存/本地 HTTP 契约测试）**：R1 同 deviceId 无 bootstrap 不能接管、不同 bootstrap 为不同账号、同 requestId 不同 bootstrap 不回放 token、同 bootstrap 合法双开/幂等回放；R2 跨房顺序/并发 join 只有一个 assignment、create-vs-join、match-vs-join 互斥、存储直接插入第二活跃 assignment 被拒；并发同键建房、幂等冲突、并发同座位加房、四人匹配/第五人排队、取消/过期竞态、过期释放席位、重复匹配、票据字段/无 ownerEpoch、alg/aud/role/seat/room/assignment/expiry/TTL/重复 claim 拒绝、签名篡改、跨账号签发、并发重复消费、重连新票据、密钥轮换/退休、JWKS 无私钥、公开投影合并/降级、程序集不引用 Domain/TableRuntime。

**NOT_RUN / 未覆盖**：

- 真实 PostgreSQL 事务/唯一约束/锁序/迁移执行：未设置 `MAHJONG_LOBBY_TEST_POSTGRES`，测试显式 Skip；不能把内存测试称为持久化通过。
- 真实 HTTPS/TLS、反向代理、证书轮换、公网入口：未运行。
- 真实 TableService WSS 握手/准入/租约/ownerEpoch 校验：P5.1 未完成，未联调。
- 观战推送/WSS `/v1/lobby`、观战延迟、离线推送：未实现。
- 会话刷新/登出/删号、账号/资产/OIDC、数据保留清理/导出、限流、审计导出：未实现。
- 游客 bootstrap 丢失后的恢复/迁移：未实现；以新 bootstrap 创建新身份，不把 deviceId 当恢复凭据。
- 离开房间/角色切换 API：未实现；当前一个账号全局只允许一个活跃 lobby assignment，需等 assignment 过期或房间关闭。
- 生产部署、密钥库/证书集成、压测/容量数字：不在本任务内。
