# T1 验收记录：protocol 帧与握手契约层（CLAUDE-TCP-T1-REVIEW.md）

- 验收方：Claude Code 主会话（独立检视 + 独立验收测试 + 全量构建）
- 交付方：Anti（开发 + 一轮返修）
- 基线：`3a382c6`（分支 refactor/remove-legacy-cheat-params）
- 结论：**验收通过（2026-10-15）**。一轮返修 + 验收方三处测试侧修正后全绿。

## 1. 验收结论

| 项目 | 结果 |
|---|---|
| 独立验收套件（本批新增 17 项） | 17/17 通过（tools/architecture/v4-improvement/protocol/TcpT1FrameContractReviewTest.kt，`-I` 独立运行） |
| 既有独立套件回归 | protocol 26 / client 21 / server 17，全绿（含 MR-01/02/03 验收测试） |
| protocol 模块测试 | 210/210（MR-03 基线 179 + Anti 新增 31） |
| 全仓模块回归 | engine 470 / protocol 210 / client 170 / server 161 / presentation 286，合计 1297，0 失败/0 错误/0 跳过 |
| 范围审计 | 仅 protocol 模块新增 + ApiErrors 追加 + 测试 + 文档；UdpClock.kt 及既有 273 处引用未动（D10） |

## 2. 检视发现与处置

### 第一轮（返修由 Anti 完成，简报 ANTI-TCP-T1-REWORK-TASK.txt）

- **R1（生产，已返修）**：`TcpCloseReasons` 初版把 `SERVER_SHUTDOWN`/`LOCAL_CLOSED` 归入 TRANSIENT，与 MR-02 `ReconnectErrorClassifier.PERMANENT_ERROR_CODES` 及设计 §6「保留 MR-02 分类器」冲突（会导致停服/本地关闭后自动重连的行为回退）。返修后 PERMANENT=6 / TRANSIENT=4 / ALL=10，与 MR-02 对齐；TcpWireTest 断言同步（独立验收测试 `close reasons classification aligns with MR-02 permanent set` 复核通过）。
- **R2（测试，已返修）**：TcpMessagesTest 更新帧用例的 DTO 构造参数与真实模型不符（Anti 无法运行 Gradle，编译暴露）。返修后按 `ProtocolPublicModels.kt`/`ProtocolRoomModels.kt` 真实定义重写。
- **R3（测试，已返修）**：`Throwable.message` 可空性 7 处编译错误，改 `?.contains(...) == true`。

### 验收方修正（本记录留档，均为测试侧、无断言削弱）

1. `ApiDtoValidationTest`（[ApiDtoValidationTest.kt:275](protocol/src/test/kotlin/com/majiang/server/api/ApiDtoValidationTest.kt#L275)）：既有全集精确断言按 D3 契约扩展加入 `REQUEST_SCOPE_EXPIRED`（仍为 17 码精确相等）——T1 简报预授权的「按契约扩展断言」情形，Anti 遗漏该文件。
2. `TcpFrameCodecTest` UC-F05 case 1：终态后 `decode()` 的正确行为是抛 `TcpProtocolException`（非返回空表），改为 `assertThrows`——生产行为与契约一致，测试预期写错。
3. `TcpMessagesTest` D7 用例：消息子串断言 `mismatch` 改为实际消息文案 `does not match`——语义断言不变。

## 3. 契约符合性确认

- 帧层：12 字节大端头、魔数 MJTC、校验顺序 魔数→wireVersion→type→flags→长度(Long 无符号比较)，六类非法终态且不找同步，半头/半体/多帧任意切分等价（独立测试穷举全部两段式切点），1 MiB 上限边界（0/1/1MiB/1MiB+1/0xFFFFFF/0xFFFFFFFF），EOF 未完整帧不派发，解码暂存有界。
- 信封层（D5/D6/D7）：HELLO/WELCOME/REQ/RSP/PING/PONG/CLOSE 字段与冻结值逐项一致；外层 requestId 与内层 `ApiRequest.requestId` 双侧（解析与出帧）强制一致；未知字段/非法值严格拒绝；nonce 16 hex 回显；`grantedScope` 仅可选存在于 RSP 信封。
- 错误码与分类（D3/D4）：`REQUEST_SCOPE_EXPIRED` 并入 `ApiErrors.ALL`（17）；`TcpCloseReasons` 全集 10 码，分类与 MR-02 对齐。
- 中性时钟（D10/D11）：`TransportClock`（墙钟+单调纳秒）、`TransportScheduler`、`Cancellable` 就位，TCP 包零依赖 UdpClock。
- 边界遵守：未启动 TCP 默认入口（T2/T3 范围）、未删任何 UDP 文件、未改 engine/client/server/presentation。

## 4. 遗留与后续

1. **T0 剩余项（主会话，T2 验收前关闭）**：最大载荷实测、UC-L/UC-R06 预算初值复核、GET_REQUEST_SCOPE 限速复核（检视报告第四节）。
2. **T2 派发**：按 ANTI-TCP-T2-TASK.txt（派发前主会话按 T1 实际 API 微调简报）。
3. Anti 交付的「UDP 纯传输测试→TCP 对应测试映射表」初版在 ANTI-TCP-T1-DELIVERY.md §3，T5 验收时核对。
4. 未 commit（等待用户指示）；建议提交范围：protocol 新增 9 生产文件 + ApiErrors + 4 测试文件 + 本记录与进度文档。
