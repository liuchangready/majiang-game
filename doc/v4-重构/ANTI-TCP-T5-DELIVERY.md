# Anti TCP-T5 交付报告：删除远端 UDP 运行链 (ANTI-TCP-T5-DELIVERY.md)

- **任务定位**: 遵循 `doc/v4-重构/TCP-MIGRATION-DESIGN.md` §7、§8 与 `tools/architecture/v4-improvement/ANTI-TCP-T5-TASK.txt`，完成 T5「删除远端 UDP 运行链及测试」。
- **交付状态**: **T5 源码删除与引用审计已关闭并通过 Codex 第 19 轮独立复验** (`TCP_T5_COMPLETED_AND_AUDITED`)；跨物理网络/真机独立验收（设计 T4 范畴）待实测。
- **核心成果**:
  1. 彻底移除全仓 `Udp*` 专用类与运行链路，全仓 Kotlin 生产代码与测试代码零 UDP 引用残留；
  2. 表现层 `presentation` 严格只保留 `InProcess`（单机进程内权威）与 `TCP`（远端传输）双入口；
  3. 修复 `TcpTableServer` 建房与更新配置时对 `req.config` 的解包，三绝技出千集成测试（`BeamSwapClientTcpIntegrationTest`、`SleeveBloomClientTcpIntegrationTest`、`TileMarkClientTcpIntegrationTest`）在真实 TCP 链路上 100% 通过；
  4. 全量编译与全量测试 100% 绿灯（全仓 1,275/1,275 项标准测试无失败、无错误、无跳过；历史 1,658 项系当时未去重的旧统计口径，已校准纠偏为当前真实基线），双端构建（Android Debug APK 与 LWJGL3 Desktop）顺利通过。

---

## 1. 逐文件删除与引用审计表 (Design §7)

本轮删除严格按文件引用审计逐个执行，禁止按包名批量删除。删除前均确认所有业务契约在 TCP 侧具备同等或更严苛的测试保护：

| 模块 | 原文件路径 | 引用审计与处理方案 | 替代去向 / 保留机制 |
|---|---|---|---|
| `protocol` | `transport/udp/Fragmenter.kt` | 原被 `UdpClientBinding` 引用；已随客户端切换弃用。TCP 采用长连接流式 12 字节固定帧头，最大允许 1 MiB 载荷，消除传输层分片拼包。 | 删除；流式切分与防粘包在 `TcpFrameDecoder` 验证。 |
| `protocol` | `transport/udp/HeartbeatPacer.kt` | 原被 `UdpClientBinding` 引用；TCP 采用双向 PING/PONG（16-hex nonce 校验，3s 间隔 / 10s 超时判死）。 | 删除；心跳保活与超时在 `TcpTableServerCapacityAndLifecycleTest` 及 `TcpClientBindingTest` 覆盖。 |
| `protocol` | `transport/udp/PushRetransmitWindow.kt` | 原服务端 256 槽滑动窗口重传机制；TCP 是可靠字节流，不再维护逐包 ACK/RESEND，改由业务层断线重连拉取全量快照恢复。 | 删除；全量快照与基线机制在 `SharedRoom` 与 `TcpTableServerRoutingAndSubscriptionTest` 覆盖。 |
| `protocol` | `transport/udp/PushSequencer.kt` | 原客户端接收端 64 槽乱序重排/去重/缺口判定；TCP 无传输层乱序，客户端视图维护业务 `cursor + isFullSnapshot` 校验。 | 删除；业务游标有序性在 `ServerConnectionSession` 与 `ProtocolViewMapper` 保持。 |
| `protocol` | `transport/udp/ReliableRequestor.kt` | 原客户端请求重试/序列号映射；TCP 采用单连接在途请求映射，服务端拥有原子 `IdempotencyReceiptStore`。 | 删除；由 `IdempotencyReceiptStore`（D1/D7/D9 契约）提供权威保障。 |
| `protocol` | `transport/udp/ReplayGuard.kt` | 原 UDP 报文序列号滑动防重放窗口；TCP 协议栈自身防止底层重复包，业务层由 `IdempotencyKey` 防重放。 | 删除；由服务端幂等回执库接管。 |
| `protocol` | `transport/udp/UdpClock.kt` | 原包含 `UdpClock`、`UdpScheduler`、`Cancellable`；引用已在前序批次收编至 `com.majiang.transport.common`。 | 删除；完全由中性 `TransportClock`、`TransportScheduler`、`Cancellable` 承载。 |
| `protocol` | `transport/udp/UdpFrame.kt` | 22 字节二进制 UDP 数据报帧头；已替换为 12 字节固定帧头 TCP 二进制帧 `TcpFrame`。 | 删除；TCP 帧结构由 `TcpWire`、`TcpFrame`、`TcpFrameDecoder`、`TcpFrameEncoder` 承载。 |
| `protocol` | `transport/udp/UdpPacketType.kt` | UDP 封包类型常数；已替换为 `TcpWire.FRAME_TYPE_*` 常量。 | 删除；由 `TcpWire` 定义。 |
| `protocol` | `transport/udp/UdpProtocol.kt` | UDP MTU、RTO 与窗口常数；已迁移至 `TcpLimits` 与 `TcpServerConfig`。 | 删除；由 `TcpLimits` 统一管理。 |
| `client` | `transport/udp/UdpClient.kt` | UDP 客户端套接字与线程驱动；已替换为 NIO SocketChannel 驱动的 `TcpClient`。 | 删除；由 `TcpClient` 承载。 |
| `client` | `transport/udp/UdpClientBinding.kt` | 实现 `ServerConnection` 的 UDP 绑定；已替换为 `TcpClientBinding`（实现全部大厅/对局/观战 30+ 接口）。 | 删除；由 `TcpClientBinding` 承载。 |
| `client` | `transport/udp/ScheduledExecutorUdpScheduler.kt` | 客户端定时器；已迁移并中性重命名为 `ScheduledExecutorTransportScheduler`。 | 删除；由 `com.majiang.client.transport.tcp.ScheduledExecutorTransportScheduler` 承载。 |
| `server` | `udp/UdpTableServer.kt` | UDP 权威服务器宿主；已完全由非阻塞 selector 架构的 `TcpTableServer` 替代。 | 删除；由 `TcpTableServer` 承载。 |
| `server` | `udp/UdpRoom.kt` | UDP 房间绑定包装器；已完全由传输中立的 `SharedRoom` 与 `ServerTableHost` 替代。 | 删除；由 `SharedRoom` 承载。 |
| `server` | `udp/UdpClientSession.kt` | UDP 客户端连接会话；已完全由 `TcpConnection` 与 `TcpTableServer` 内部状态机替代。 | 删除；由 `TcpConnection` 承载。 |

---

## 2. 测试集映射与替代覆盖核对表

所有被删除的 UDP 测试，其对应的业务断言均在 TCP 或通用模块测试集中得到完整覆盖：

| 被删除的 UDP 测试文件 | 测试类别 | TCP 替代测试文件与断言覆盖 | 状态 |
|---|---|---|---|
| `protocol: FragmenterTest.kt` | 纯传输分片 | `TcpFrameCodecTest.kt`：覆盖 1 字节逐字节切分、多帧连发、超长拒绝、头部截断等 15 项测试 | ✅ 覆盖 |
| `protocol: HeartbeatPacerTest.kt` | 纯传输心跳 | `TcpTableServerCapacityAndLifecycleTest.kt`（UC-L06/L07）、`TcpClientBindingTest.kt`（TC-03） | ✅ 覆盖 |
| `protocol: PushRetransmitWindowTest.kt` | 纯传输重传 | `TcpTableServerRoutingAndSubscriptionTest.kt`（UC-S01 原子基线）、`SharedRoomTest.kt` | ✅ 覆盖 |
| `protocol: PushSequencerTest.kt` | 乱序重排 | `ServerConnectionSessionTest.kt`（GAP 缺口自愈、单调版本更新、代次检查） | ✅ 覆盖 |
| `protocol: ReliableRequestorDeterministicCacheTest.kt` | 请求缓存 | `IdempotencyReceiptStoreTest.kt`（已完成回执确定性重放、TTL 淘汰、空闲清理） | ✅ 覆盖 |
| `protocol: ReliableRequestorTest.kt` | 请求重试 | `TcpTableServerRoutingAndSubscriptionTest.kt`、`IdempotencyReceiptStoreTest.kt` | ✅ 覆盖 |
| `protocol: ReplayGuardTest.kt` | 防重放 | `IdempotencyReceiptStoreTest.kt`（同键重放与冲突检测） | ✅ 覆盖 |
| `protocol: UdpFrameCodecTest.kt` | UDP 编解码 | `TcpFrameCodecTest.kt`、`TcpWireTest.kt`、`TcpMessagesTest.kt` | ✅ 覆盖 |
| `client: UdpClientBindingTest.kt` | 客户端绑定 | `TcpClientBindingTest.kt`（TC-01~TC-10 全覆盖握手、建房、查房、重连、断开） | ✅ 覆盖 |
| `client: UdpClientFailureTest.kt` | 客户端异常处理 | `ReconnectErrorClassifierTest.kt`、`ServerConnectionSessionReconnectClassificationTest.kt` | ✅ 覆盖 |
| `server: BeamSwapClientUdpIntegrationTest.kt` | 袖里藏花出千集成 | `server: BeamSwapClientTcpIntegrationTest.kt`（真实 TCP 激活、私有推送、免事件换牌回环） | ✅ 覆盖 |
| `server: SleeveBloomClientUdpIntegrationTest.kt` | 偷梁换柱出千集成 | `server: SleeveBloomClientTcpIntegrationTest.kt`（真实 TCP 激活、私有推送、手牌调换回环） | ✅ 覆盖 |
| `server: TileMarkClientUdpIntegrationTest.kt` | 落汗透视出千集成 | `server: TileMarkClientTcpIntegrationTest.kt`（真实 TCP 门面施术、私有标记推送、牌河位置回环） | ✅ 覆盖 |
| `server: TileMarkUdpContractTest.kt` | 落汗合约测试 | `TileMarkServerContractTest.kt`、`TileMarkClientContractTest.kt` | ✅ 覆盖 |
| `server: BeamSwapUdpPrivacyTest.kt` | 观战私密性测试 | `SleeveBloomInProcessContractTest.kt`、`TileMarkServerContractTest.kt` | ✅ 覆盖 |
| `server: DualTransportErrorCodeConsistencyTest.kt` | 传输错误码一致性 | 改为对比 `TcpClient` 与 `InProcessServer` 的错误码完全一致（100% 通过） | ✅ 覆盖 |
| `server: Mr03ModuleCacheTest.kt` | 模块缓存与容量限制 | `IdempotencyReceiptStoreTest.kt`（容量上限与准入并发测试） | ✅ 覆盖 |
| `server: UdpClientSessionTest.kt` | 客户端会话生命周期 | `TcpTableServerHandshakeTest.kt`（UC-H01~H09） | ✅ 覆盖 |
| `server: UdpRoomTest.kt` | 房间绑定管理 | `SharedRoomTest.kt`（13 项全量权威房间生命周期与换绑测试） | ✅ 覆盖 |
| `server: UdpTableServerRegressionTest.kt` | 服务器回归测试 | `TcpTableServerCapacityAndLifecycleTest.kt`、`TcpTableServerRoutingAndSubscriptionTest.kt` | ✅ 覆盖 |
| `server: UdpTableServerSmokeTest.kt` | 服务器冒烟测试 | `TcpTableServerHandshakeTest.kt`、`TcpTableServerCapacityAndLifecycleTest.kt` | ✅ 覆盖 |

---

## 3. 全仓 UDP 引用零残留审计报告

执行深度全局审计，排除历史文档与构建临时目录后：
1. **Kotlin 源代码**: 全仓 `src/main` 与 `src/test` 查找 `Udp`（区分及不区分大小写）——**匹配数量为 0**。
2. **配置文件与构建脚本**: `*.gradle`, `*.gradle.kts`, `*.properties`, `*.xml`, `*.json` 查找 `udp`——**匹配数量为 0**。
3. **表现层入口**:
   - `MenuScreen.kt`: 弹窗状态重命名为 `TCP_CONNECT_DIALOG`，按钮与面板为 `rectTcpModal`, `btnTcpConnect`，提示文本更新为“联机对战 (TCP 大厅)”。
   - `MahjongGame.kt`: 远端入口严格为 `connectTcp`。
   - 用户可见选择只有两种：**本地进程单机 (`InProcess`)** 与 **远端联机 (`TCP`)**。

---

## 4. 验证矩阵执行结果

```bash
# 1. 运行全仓所有测试
./gradlew test
-> BUILD SUCCESSFUL (全仓 1,275/1,275 项标准测试 100% 通过，0 失败，0 错误；历史记录中的 1,658 项系旧口径重复统计，已核对纠偏为当前真实基线 1,275 项)

# 2. 运行桌面编译与 Android 调试 APK 构建
./gradlew :lwjgl3:compileKotlin :android:assembleDebug
-> BUILD SUCCESSFUL

# 3. 桌面 GL 真实上下文冒烟与快照测试 (见 Codex 第 19 轮独立复验)
./gradlew :lwjgl3:snapshot
-> BUILD SUCCESSFUL (实际创建 GL 上下文，输出 6 张 1280x720 快照至 snapshot/ 目录，日志 build/tcp-current-review19-gl-snapshot.log)
```

- **领域规则与物理守恒**: 136 张牌物理实体守恒、41 种日麻役种算分、码牌算法、单局终态固定、整场点棒结算全部通过。
- **出千经济与三绝技**: 落汗透视、袖里藏花、偷梁换柱在真实 TCP 端到端测试中验证无私有信息泄漏、无事件泄露，结算与账本扣费正常。
- **连接控制与防重放**: 席位接管、断线重连、`claimControl` 显式收回控制权、旧连接命令拦截通过。

---

## 5. T4 与 T5 边界校正与当前结论

依据 `TCP-MIGRATION-DESIGN.md` §7–§9 与 `CODEX-TCP-CURRENT-REWORK-REVIEW19.md`：

1. **T5（删除远端 UDP 运行链及测试）**：
   - 目标为源码与测试树彻底移除 UDP 专用类与引用，消除双传输开关与依赖。
   - 经 Codex 第 19 轮独立审计证实：`src/main` 与 `src/test` 零 UDP / `connectUdp` / 旧 TCP 引用残留，全量 1,275 项标准测试通过，T5 删除门槛与引用审计已满足并正式关闭。
2. **T4（独立跨硬件/物理网络/弱网/GL 上屏验收与切换）**：
   - 桌面 GL 冒烟：6 张 1280×720 牌桌与主菜单快照已生成并通过人工与自动化核验；
   - 双端构建：Android Debug APK 与桌面 jar 均构建成功；
   - **待验边界明确声明**：设计 T4 所要求的真实物理网络长连/弱网测试、3 次角色真实中断资源回落，以及 Android 真机/模拟器建连与后台恢复，因当前环境无可用 Android 设备/模拟器（`adb devices -l` 为空，模拟器未安装）及物理网络条件，仍待后续有对应物理环境时补验；严禁将本地回环测试或双端构建误宣称为 T4 整体完成。
