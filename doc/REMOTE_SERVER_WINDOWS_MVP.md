# Windows 远端大厅 MVP

此交付把现有 `Mahjong.LobbyService` 与 `Mahjong.TableService` 作为两个 Windows x64 服务进程运行。游客会话、房间分座和短时票据由大厅处理；TableService 通过 V5 WSS、票据校验和权威运行时推进本局。第一个权威 `RoundEnd` 后，客户端可调用大厅对账端口；内存部署由大厅读取 TableService 的公开房间投影，将房间单调关闭并释放大厅席位。投影只包含房间生命周期、座位/观战计数、公开版本和规则版本，不包含牌、分数、身份、票据、事件详情或种子。

## 限制与存储

- 内存大厅与内存桌服不跨重启保留会话、房间、消费票据或牌局。将两个进程部署在同一 Windows 主机；单桌最多四席。本 MVP 客户端创建一个房间，只运行至第一份权威结算。
- `Lobby:StorageProvider=Memory` 默认保留。`Production` 下要显式设置 `Lobby:AllowInMemoryStorageInProduction=true`，接受进程重启丢失状态的限制。TableService 默认 `TABLESERVICE_STORE=memory`。这不是持久化、故障恢复或生产可用性声明。
- PostgreSQL 模式未改动；LobbyService 继续读其现有只读 `room_projection`，TableService 继续使用现有 durable host。两个服务的持久模式配置、连接与密钥要求维持原样。
- 内存 TableService 保留现有有界房间注册表及消费票据上限；容量满时失败关闭。第一份 `RoundEnd` 后房间投影为 `FINISHED`，新 join、签发或消费旧座票的大厅请求在对账后拒绝。TableService 自己仍按旧生命周期拒绝终态命令与入桌。

## 发布与证书

需要 Windows x64。下面发布为 self-contained，目标机器不需要单独安装 .NET 8 Runtime；系统仍需满足 .NET 8 支持的 Windows x64 与 TLS/加密平台依赖。发布物包含各自依赖，两个服务是两个进程，不安装 Windows 服务。

在仓库根目录执行：

```powershell
$out = 'D:\deploy\mahjong-remote-mvp'
dotnet publish dotnet/src/Mahjong.LobbyService/Mahjong.LobbyService.csproj -c Release -r win-x64 --self-contained true -o "$out\lobby"
dotnet publish dotnet/src/Mahjong.TableService/Mahjong.TableService.csproj -c Release -r win-x64 --self-contained true -o "$out\table"
```

证书由部署者现有 PKI 签发。为 Lobby HTTPS 与 Table WSS 配置有效的服务器证书 PFX，SAN 必须覆盖客户端使用的 DNS 名称。Lobby 通过 `ASPNETCORE_Kestrel__Certificates__Default__Path` / `...__Password` 读取 PFX；TableService 通过 `TABLESERVICE_TLS_PFX` / `TABLESERVICE_TLS_PFX_PASSWORD` 读取 PFX。生产客户端正常使用 `https://<域名>:<大厅端口>` 和 `wss://<域名>:<桌服端口>/v1/table?room=<roomId>`，并按操作系统正常证书链验证。

票据由 ECDSA P-256 密钥签发。Lobby 私钥只放在受控密钥存储或仅服务帐号可读的 PEM 文件，再用 `Lobby__TicketPrivateKeyPem` 注入；不要把私钥写进配置文件、命令行、日志或发布目录。TableService 只接收其公钥文件 `TABLESERVICE_TICKET_KEYS_FILE`。两个服务 issuer/audience 必须相同并保持默认值 `mahjong-lobby` / `mahjong-table`。轮换时先让 TableService 同时信任新旧公钥，再切换 Lobby 签名键，待旧票据 TTL 和 skew 过期后再移除旧公钥。

若要在本机环回验证私有测试 CA，可复用现有测试材料生成器（仅生成测试证书和随机票据密钥，不访问数据库、不改信任库）：

```powershell
$material = Join-Path $PWD 'local-test-material'
[IO.Directory]::CreateDirectory($material) | Out-Null
dotnet run --project dotnet/tests/Mahjong.TableService.PersistenceWorker/Mahjong.TableService.PersistenceWorker.csproj -c Release -- certgen --out $material
dotnet run --project dotnet/tests/Mahjong.TableService.PersistenceWorker/Mahjong.TableService.PersistenceWorker.csproj -c Release -- keygen --dir $material
```

生成的 `root.pem` 是公开测试根，`leaf.pfx` 是 localhost/127.0.0.1 服务器证书，`private.pem` / `public.pem` 是本次随机票据键。测试后删除私钥与 PFX；任何客户端测试都必须显式把该 `root.pem` 作为唯一自定义信任根，不能关闭证书校验。

内存双进程部署还需要一份只用于两个服务间调用的随机共享密钥。环回示例在启动任何服务前生成它；正式部署将相同值分别注入两服务的秘密存储，不将密钥写入发布目录或仓库：

```powershell
[IO.Directory]::CreateDirectory($material) | Out-Null
$randomBytes = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($randomBytes)
$admissionKey = [Convert]::ToBase64String($randomBytes)
[IO.File]::WriteAllText((Join-Path $material 'admission.key'), $admissionKey)
$rng.Dispose()
Remove-Variable admissionKey, randomBytes, rng
```

## 启动

使用两个前台 PowerShell 窗口，以便分别观察并用 Ctrl+C 正常退出。下例的 loopback TLS 材料可用于本机测试；远端部署将绑定地址和 URL 换成主机网卡地址/域名，并使用 PKI 证书。

Lobby 窗口：

```powershell
$env:ASPNETCORE_URLS = 'https://127.0.0.1:5183'
$env:ASPNETCORE_Kestrel__Certificates__Default__Path = "$material\leaf.pfx"
$env:ASPNETCORE_Kestrel__Certificates__Default__Password = ''
$env:Lobby__StorageProvider = 'Memory'
$env:Lobby__AllowInMemoryStorageInProduction = 'true'
$env:Lobby__TicketPrivateKeyPem = Get-Content -Raw "$material\private.pem"
$env:Lobby__TableServiceBaseUrl = 'https://127.0.0.1:5184'
$env:Lobby__TableServiceTlsRootCertificatePath = "$material\root.pem"
$env:Lobby__TableServiceAdmissionSharedKey = Get-Content -Raw "$material\admission.key"
& "$out\lobby\Mahjong.LobbyService.exe"
```

TableService 窗口：

```powershell
$env:TABLESERVICE_STORE = 'memory'
$env:TABLESERVICE_BIND_ADDRESS = '127.0.0.1'
$env:TABLESERVICE_HTTP_PORT = '5184'
$env:TABLESERVICE_TLS_PFX = "$material\leaf.pfx"
$env:TABLESERVICE_TLS_PFX_PASSWORD = ''
$env:TABLESERVICE_TICKET_KEYS_FILE = "$material\public.pem"
$env:TABLESERVICE_REQUIRE_LOBBY_ADMISSION = 'true'
$env:TABLESERVICE_LOBBY_ADMISSION_BASE_URL = 'https://127.0.0.1:5183'
$env:TABLESERVICE_LOBBY_ADMISSION_SHARED_KEY = Get-Content -Raw "$material\admission.key"
$env:TABLESERVICE_LOBBY_ADMISSION_TLS_ROOT_CERTIFICATE_PATH = "$material\root.pem"
$env:TABLESERVICE_REJECT_FINISHED_ADMISSION = 'true'
& "$out\table\Mahjong.TableService.exe"
```

将 `TABLESERVICE_BIND_ADDRESS` 设为非回环 IP 时，TableService 启动校验要求配置 PFX；Lobby 的 Kestrel HTTPS endpoint 也必须提供证书。`Lobby__TableServiceBaseUrl` 指向桌服 HTTPS URL；若桌服使用私有 CA，`Lobby__TableServiceTlsRootCertificatePath` 必须指向公开 CA 证书。该设置会严格验证链、有效期和 URL 主机名，不会接受任意证书。

两个服务的 `*AdmissionSharedKey` 必须由受控密钥存储注入为同一份至少 32 字符随机值，不写入仓库、发布目录或命令行历史。携带该凭证的 Lobby→Table projection/retire 与 Table→Lobby admission 请求均只允许 HTTPS、正常证书链/主机名校验且不跟随重定向；私有 CA 只能通过显式根证书路径增加信任。TableService 在本地签名/协议校验及暂存本地 one-time jti 后，必须经严格证书验证的 HTTPS 调用 Lobby 内部消费端点；Lobby 事务重新核对 assignment 仍有效、仍是当前房间/账号/座位且未过期/替换/消费，成功后 TableService 才能发送 WELCOME。该服务端点不供桌面客户端调用。`REJECT_FINISHED_ADMISSION=true` 仅用于本内存单局 MVP；它拒绝 RoundEnd 后的新 WSS admission。桌服 HTTP 公开投影用 actor 已发布的锁外快照并限制并发数，不将远端 GET 排入桌 actor 控制邮箱。Lobby 先提交不可逆 `CLOSED` 并排入独立内存重试，再带共享服务密钥调用 retire；相同请求键重放和维护循环均可重试资源清理。TableService 先关闭终局新准入，给每个当前流一个事件驱动且有界的 RoundEnd WebSocket 发送机会，再释放终局 actor/占用记录并保留 `TicketMaxTtlSeconds + ClockSkewSeconds` 的房间 tombstone，防止仍在签名有效窗内的旧票据创建新 runtime。失联/慢流不会无限占用房间槽位。持久模式不启用这项 MVP runtime 回收。

部署者自行分配明确端口并配置现有网络边界。此交付不修改防火墙、系统信任、系统服务或机器安全设置。

## 客户端真实接入顺序

1. 每位玩家独立用 CSPRNG 生成并安全保存 32 字节 bootstrap secret（base64url，无 `=` padding），调用 `POST /v1/auth/session`：`{"deviceId":"<installation hint>","displayName":"<name>","authMode":"GUEST","bootstrapSecret":"<generated secret>"}`。保存返回的 `sessionToken` 与本地 bootstrap secret；每次请求使用唯一 `Idempotency-Key`。bootstrap 不发送到日志或 URL。
2. 房主调用 `POST /v1/rooms`，body 为 `{"title":"<room title>","maxPlayers":4,"visibility":"PUBLIC","allowSpectators":false}`。其他三人各自调用 `POST /v1/rooms/{roomId}/join`，body 为 `{"preferSeat":<0..3>}`（也可省略）。请求头带各自 `Authorization: Bearer <sessionToken>` 和新 `Idempotency-Key`。只有大厅响应的 `assignmentId` / `seatId` 是席位来源；确认收到四个唯一座位后才为每个 assignment 领票。
3. 每人用 `POST /v1/table-tickets`、body `{"assignmentId":"<that player's assignmentId>"}`、Bearer token 和唯一幂等键领票。短票据只传给对应玩家客户端，不共享或写日志。
4. 客户端创建 `WebSocketTableChannel(new WebSocketTableChannelOptions(new Uri("wss://<域名>:<桌服端口>/v1/table?room=<roomId>")))`，再创建 `TableSession(channel, outboxStore)` 并调用 `OpenAsync(new TableSessionOpenRequest(ticket, clientBuild), token)`。证书链和主机名按系统 TLS 正常验证。WELCOME 中的 `TableSessionBinding` 才是 session、seat、scope、ownerEpoch 和 baseline 的权威来源。
5. 到期续票：先用当前账号/session 和仍有效的 `assignmentId` 再调一次 `/v1/table-tickets`，使用新幂等键；对当前 `TableSession` 调用 receipt/reconcile API 处理所有未决请求，再用新票据打开同房间的新 `TableSession`。大厅在线确认当前 assignment 后才会返回 WELCOME，新 session 确认后桌服按原 handover 规则关闭旧连接。若 assignment 已过期、被替换、房间已关闭或旧票已消费，准入失败；不要重放未知结果的领域动作。
6. 等 `TableSession.State == Ready` 并消费 `SnapshotChanged` / `ReceiptResolved`。动作只能从 `PlayerSnapshot.PrivateView.Decision.LegalActions` 选取，将 decision、预期版本、scope 和新 requestId 映射为 `ProtocolCommand` 后调用 `TableSession.SubmitAsync`。客户端不生成领域状态、牌、算分、结算、scope 或 ownerEpoch。
7. 以公开 `PublicSnapshot.View.Board.Phase == RoundEnd` 和同一 `roundId` 的 `SettlementRoundId` / `SettlementEvents` 确认权威终局。调用 `POST /v1/rooms/{roomId}/reconcile`（Bearer + 新 `Idempotency-Key`）后，大厅单调关闭房间并请求桌服清理单局内存 runtime；本 MVP 拒绝同房间后续新入座、旧有效票据和终局后 WSS admission。

仓库内实际网络客户端验方是 `dotnet/tests/Mahjong.P5.4b.B2.Terminal.Worker`。`natural --spectator false` 使用四个独立大厅访客、四个真实 `ClientHandle`（包装 `WebSocketTableChannel` 与 `TableSession`），从合法决策推进至真实 RoundEnd，并断言大厅对账后房间 CLOSED / 后续 join 返回 `409 ROOM_CLOSED`。它不构造领域状态或伪造结算。Unity UI 仍默认 LocalHost，不属于本接入证明。

## 验证入口

本地环回要用发布目录二进制分别启动两个服务，然后从另一进程运行 B2 worker `natural --spectator false --require-lobby-terminal true`；容量/预约续验再加 `--exercise-expired-assignment true --exercise-capacity-reuse true --exercise-public-reads true --renew-at-seconds 25 --action-delay-ms 900`。这些定向选项会覆盖预约过期/换人票拒绝、spectator 越权动作拒绝、四席确认超期保留、动作期间连续公开房间读取、旧票在 RoundEnd 后拒绝及 `MaxRooms=1` 终局回收。参数 `--require-lobby-terminal` 默认关闭，以保留已有 PostgreSQL B2 runner 的独立持久投影验方；只有内存 MVP 明确打开它，测试对账关闭与 runtime 回收。将 `--lobby-url`、`--table-port`、`--ca-root`、`--out`、`--secrets`、`--outbox-root` 指向本次独占目录。发布二进制、worker 和客户端必须来自同一冻结源码；证据记录命令、原始退出码、xUnit 实际计数与包哈希。

回环 TLS/WSS 验证不等于跨物理主机连通、Windows 服务安装、Unity Player、设备、压测或公网发布验收。
