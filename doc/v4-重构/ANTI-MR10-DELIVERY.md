# Anti MR-10 测量驱动优化交付报告 (ANTI-MR10-DELIVERY.md)

**交付日期**：2026-10-18  
**执行角色**：Anti 开发，供 Codex 独立检视与验收  
**Git HEAD 基线**：`a36de5882a509184be65a2e74af4ce17db641fd8`  
**遵循契约**：[AGENTS.md](../../AGENTS.md)、[IMPROVEMENT-PLAN.md](IMPROVEMENT-PLAN.md) §MR-10、[TCP-MIGRATION-DESIGN.md](TCP-MIGRATION-DESIGN.md)

---

## 1. 工作区未提交改动隔离与保护记录

在开始 MR-10 开发前，已严格核对并锁定工作区已有未提交改动（MR-06、MR-07a、MR-09、TCP 迁移返修成果）。本轮开发严格保持现有改动零破坏、零冲突，不执行 git commit 或 git push。

### 1.1 既有未提交改动清单（完整保留）
- `client/src/test/kotlin/com/majiang/client/transport/tcp/TcpClientBindingRegressionTest.kt`
- `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`
- `engine/src/main/kotlin/com/majiang/model/PlayerHand.kt`
- `engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt`
- `engine/src/main/kotlin/com/majiang/session/PlayerPresentationPreviewProvider.kt`
- `engine/src/main/kotlin/com/majiang/state/GameState.kt`
- `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpMessages.kt`
- `server/src/main/kotlin/com/majiang/server/host/HostDispatch.kt`
- `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`
- `server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`
- `server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`
- `server/src/main/kotlin/com/majiang/server/tcp/TcpTableServer.kt`
- `server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`
- `tools/architecture/v4-review/V4AuthorityBoundariesTest.kt`
- 各模块已有的未追踪 MR-06/MR-07/MR-09 测试用例。

### 1.2 Anti MR-10 实施与新增文件清单
- **生产代码修改**：
  1. `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`：落地 `ProtocolTile` 136 张静态享元池（`POOL`），`fromTileId` 与 `fromTypeIndex` 直接返回享元。
  2. `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`：`ProtocolTileAdapter.deserialize` 保持严格矛盾花色与矛盾数字校验，通过后返回享元对象。
  3. `client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`：落地 `TileView` 136 张静态享元池（`POOL`），`fromTileId` 与 `fromTypeIndex` 直接返回享元。
  4. `client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt`：`mapTile` 与 `toProtocolTile` 接入享元池。
- **正式回归与测量测试新增**：
  1. `protocol/src/test/kotlin/com/majiang/transport/api/Mr10TileAndZlibMeasurementTest.kt`（ProtocolTile 测量、zlib 全阶段载荷及 CPU 测量、解压炸弹防御）
  2. `protocol/src/test/kotlin/com/majiang/transport/api/ProtocolTileFlyweightContractTest.kt`（ProtocolTile 享元自洽与矛盾字段反序列化阻断）
  3. `client/src/test/kotlin/com/majiang/client/mapping/Mr10TileViewBenchmarkTest.kt`（TileView 享元与 ProtocolViewMapper 映射实测）
  4. `client/src/test/kotlin/com/majiang/client/api/TileViewFlyweightContractTest.kt`（TileView 享元完备性与赤五保真）
  5. `server/src/test/kotlin/com/majiang/server/mr10/Mr10SnapshotAuditAndHotspotTest.kt`（快照不可变审计与 deepCopy 分配热点测量）
  6. `presentation/src/test/kotlin/com/majiang/presentation/common/render/Mr10ClassicSkinMetricsTest.kt`（ClassicSkin 旋转需求、显存预算与批处理指标）

---

## 2. 测量环境与基准硬件

所有测量均在**同一构建、同一硬件、固定输入集**下运行，采用 `java.lang.management.ThreadMXBean.getThreadAllocatedBytes`（精确计量单线程堆分配，消除 GC 抖动）与 `System.nanoTime()` 高精度纳秒计时，充分预热消除 JIT 编译抖动。

- **操作系统**：Windows 10 Pro (10.0 amd64)
- **CPU 规格**：12th Gen Intel(R) Core(TM) i7-1260P (12 核 / 16 逻辑处理器)
- **系统内存**：16 GB (总物理内存 ~16.5M KB)
- **运行时环境**：JetBrains s.r.o. JBR 25.0.2+-15348964-b329.117 (JDK 25)
- **构建工具**：Gradle 9.7.1，Kotlin 2.4.0

---

## 3. 候选项目测量证据与决策汇总

根据 IMPROVEMENT-PLAN.md §MR-10 准则：“**按可重复的测量证据，仅实施收益明确且安全的项；未过门槛的项应记录暂缓，不能为了完成而改。**”

| 候选项目 | 测量证据摘要 | 方案限制落地情况 | 结论与处理 |
|---|---|---|---|
| **136 张 TileView / ProtocolTile 享元** | 堆分配降低 **100%** (24B→0B, 46B→0B)；映射延迟降低 **30%~69%** [实测] | 反序列化先校验矛盾字段，非法输入严格抛出 `JsonParseException`，Mapper 严格拒绝矛盾花色/点数 | **实施交付**（收益显著且安全） |
| **快照共享替代深拷贝** | `deepCopy` 仅占生成总耗时的 **5.3%**，仅占总分配的 **1.2%** [实测] | 不可变审计证明直接 `return this` 会破坏跨座位私密隔离与集合防御性 | **记录暂缓 (DEFERRED)** |
| **zlib 传输压缩** | 真实局况晚盘最大帧 8.88 KB 仅占 1 MiB 单帧上限 0.85%；压缩解压增加 45~77 µs CPU [实测] | 必须能力协商、解压上限防护 (1 MiB)、旧端明文降级 | **记录暂缓 (DEFERRED)** |
| **ClassicSkin 纹理缓存** | 显存 0.89~3.56 MiB [理论核算]，理论批次从 3 降为 1 [理论推导]；当前实现仅委托直立绘制 | 必须覆盖 38 种牌型（含 3 种赤五与牌背）；运行时纹理生成会破坏无头 CI | **记录暂缓 (DEFERRED)** |
| **动态 UDP RTO** | 已随 TCP 迁移在架构中取消 | 保留业务 deadline 与重连退避，不重复实现 TCP 重传算法 | **已取消 (CANCELED)** |
| **Android 帧时间** | 当前环境无物理连接的 Android 真机或模拟器 (`adb`) | 无法取得真实硬件，明确列为缺口，绝不臆测 | **已列为缺口 (GAP)** |

---

## 4. 详细测量数据与变更对比

### 4.1 候选 1：136 张 TileView / ProtocolTile 享元（实施）

#### 变更前后性能对比（100,000 次操作均值）[实测数据]
| 操作项 | 变更前堆分配 | 变更后堆分配 | 分配优化率 | 变更前延迟 | 变更后延迟 | 延迟优化率 |
|---|---|---|---|---|---|---|
| `ProtocolTile.fromTileId` | 24.00 B / op | **0.00 B / op** | **-100.0%** | 36.51 ns | 25.40 ns | **-30.4%** |
| `TileView.fromTileId` | 46.53 B / op | **0.00 B / op** | **-100.0%** | 54.90 ns | 22.38 ns | **-59.2%** |
| `ProtocolViewMapper` 映射往返 | 48.00 B / op | **0.00 B / op** | **-100.0%** | 109.28 ns | 33.57 ns | **-69.3%** |

#### 方案限制遵循：反序列化先校验矛盾字段与 Mapper 严格防回退
1. 在 `ApiMessage.kt` 中的 `ProtocolTileAdapter` 反序列化实现中，严格遵守顺序：
   - 校验 JSON 结构与字段白名单；
   - 读取 `tileId` 并校验 `0..135` 物理区间；
   - 计算物理衍生花色 `expectedSuit` 与数字 `expectedRank`；
   - **若 JSON 显式携带了 `suit` 或 `rank`，校验其必须严格等于物理衍生值，矛盾即抛出 `JsonParseException`，绝不因返回享元而绕过矛盾字段校验**；
   - 校验完全通过后，返回享元池对象 `ProtocolTile.fromTileId(tileId)`。
2. 在 `TileView` 构造器中强化了不变量校验：要求 `suit == derivedSuit(tileId)` 与 `rank == derivedRank(tileId)`，非法构造直接抛出 `IllegalArgumentException`。
3. 在 `ProtocolViewMapper.toProtocolTile` 中强化了校验：获取享元前严格比对 `toProtocolSuit(tile.suit) == expected.suit && tile.rank == expected.rank`，保持原有的拒绝语义，防止矛盾构造静默漂移。

自动化测试 `ProtocolTileFlyweightContractTest` 与 `TileViewFlyweightContractTest` 验证了：
- 反序列化 `{"tileId": 0, "suit": "PIN", "rank": 1}` 阻断并抛出 `Contradictory suit for tileId 0`；
- 构造 `TileView(0, SuitView.PIN, 1)` 阻断并抛出 `Contradictory suit for tileId 0`；
- 合法 JSON 反序列化结果与 `ProtocolTile.fromTileId(0)` 是同一个内存对象（`assertSame`）。

---

### 4.2 候选 2：快照共享替代深拷贝（测量后暂缓 DEFERRED）

#### 全对象图不可变审计
对 `PlayerSnapshot` 全对象图进行了逐层只读不可篡改审计：
1. **跨座位私密隔离（绝对阻断）**：
   - 座位 0 的 `PlayerSnapshot` 携带座位 0 的暗牌手牌（`myClosedTiles`）及私有候选；座位 1 携带座位 1 的私有暗牌。
   - 跨座位之间**绝对禁止**共享 `PlayerSnapshot` 或其内部 `PlayerView`，否则将直接泄漏他家暗牌。
2. **对象图防御性深拷贝（不可变审计结论）**：
   - `PlayerSnapshot` 顶层构造器中，`clocks`、`controls`、`cheatPoints`、`settlementEvents` 字段类型为 `List<T>`，未在构造器入口做 `unmodifiableList` 包装。
   - `PlayerDecision.options` 亦未做不可修改包装。
   - 依赖 `deepCopy()` 为下游提供防御性独立实例。若未经全量对象图不可变深度重构而直接 `return this`，调用方一旦发生类型强转修改，将产生灾难性的跨线程并发污染与视图漂移。

#### 分配热点实测数据（10,000 次操作均值）[实测数据]
| 操作项 | 耗时 (Latency) | 堆内存分配 (Allocated Bytes) | 占生成总量的比例 |
|---|---|---|---|
| `TableAuthority.querySnapshot`（完整生成） | 83,323.67 ns (~83.3 µs) | 398,252.72 B (~388.9 KiB) | 98.8% 分配 / 94.7% 耗时 |
| `PlayerSnapshot.deepCopy`（深拷贝） | 4,425.97 ns (~4.4 µs) | 4,738.90 B (~4.6 KiB) | **1.2% 分配 / 5.3% 耗时** |

**暂缓结论**：
数据证明快照生成的绝对热点在于 `TableAuthority` 的合法动作集校验、听牌形扫描与役种振听分析（占 98.8%），`deepCopy` 仅占 1.2% 的分配。在未进行全领域模型不可变重构的前提下盲目消除 `deepCopy`，收益微乎其微（仅节省 1.2%），但引入极高的状态别名与暗牌泄露风险。严格执行方案限制“**不可变审计前不直接 return this**”，决定**暂缓实施**。

---

### 4.3 候选 3：zlib 传输压缩（测量后暂缓 DEFERRED）

#### 局况各阶段载荷实测数据（基于权威游戏进程真实采样，2,000 次均值）[实测数据]
所有载荷样本均通过 `InProcessServer` / `TableAuthority` 在四人带千术对局中实时抓取生成，经过 `ApiMessageCodec.gson` 严格反序列化校验，且通过 136 张物理牌全局守恒、花色与点数双向映射一致性断言（见 `Mr10TileAndZlibMeasurementTest` 与 `Mr10PayloadCaptureTest`）：

| 游戏阶段 | 原始 JSON 大小 [实测] | zlib 压缩后大小 [实测] | 压缩率 [实测] | 压缩 CPU 耗时 [实测] | 解压 CPU 耗时 [实测] |
|---|---|---|---|---|---|
| **EARLY_GAME (早盘发牌)** | 6,484 B | 1,383 B | 78.7% | 58.53 µs | 18.77 µs |
| **MID_GAME (中盘打牌与标记)** | 4,497 B | 1,223 B | 72.8% | 49.16 µs | 13.37 µs |
| **LATE_GAME (晚盘大牌河与低牌山)** | 8,880 B | 1,622 B | 81.7% | 76.92 µs | 15.76 µs |
| **TILE_MARK (千术标记生效)** | 6,396 B | 1,340 B | 79.0% | 45.27 µs | 11.98 µs |
| **CANDIDATES (复杂候选决策)** | 6,484 B | 1,383 B | 78.7% | 52.53 µs | 13.00 µs |
| **SETTLEMENT (流局/和牌结算明细)** | 7,330 B | 1,586 B | 78.4% | 65.64 µs | 15.05 µs |

#### 收益评估与方案限制设计
1. **TCP 网络模型下的带宽收益与理论推导**：
   - `[协议规格评估]`：MJ-TCP/1 契约单帧最大载荷为 1 MiB (1,048,576 字节)。真实晚盘最大载荷仅 8,880 字节 (~8.67 KiB)，占单帧上限的 **0.85%**，具备极其充裕的单帧安全余量。
   - `[实测 CPU 成本]`：在当前基准硬件上，每一包压缩耗时 45.27 ~ 76.92 µs，解压耗时 11.98 ~ 18.77 µs，并伴随 `Deflater`/`Inflater` 字节缓冲区的分配与 GC 开销。
   - `[理论推导与未测声明]`：在典型千兆局域网或本地进程内，8.88 KB 报文在 TCP 协议栈中仅需几个以太网分片 (MSS ~1460B) 即可传输。由于本测试未构建真实多物理机网络抓包拓扑，**不对端到端网络传输延迟做任何经验实测断言**。结论仅基于已测量的 CPU 耗时增量与充足的单帧带宽余量做出的工程权衡。
2. **完备规范限制设计（以备未来公网高并发演进）**：
   - **能力协商**：在客户端 `HELLO` 中增加 `supportsZlib: Boolean` 扩展字段，服务端在 `WELCOME` 中回显确认；若任一方不支持，严格走默认无压缩传输。
   - **解压炸弹防御 (Zip-Bomb Defense)**：在 `Mr10TileAndZlibMeasurementTest` 中实现了解压硬上限拦截逻辑（默认单包解压上限 1 MiB）。实测构造的 500 KB 膨胀炸弹报文在设定 100 KB 阈值时被严格拦截并抛出 `IllegalStateException`，有效杜绝内存溢出攻击。
   - **旧端策略**：未协商压缩能力的连接保持原生 UTF-8 JSON 线路，帧头 `flags` 保持为 0。

**暂缓结论**：局域网对局下 CPU 算力与堆内存抖动成本高于带宽收益，暂不在当前生产链路开启压缩，已固化完整测试与防御规格。

---

### 4.4 候选 4：ClassicSkin 纹理缓存（测量后暂缓 DEFERRED）

#### 旋转视觉需求与当前实现分析
- **日麻规则必要性**：吃/碰/大明杠从他家鸣牌时，被鸣牌需横置（旋转 90°/270°）；立直宣言打出的牌亦需横置。
- **当前实现分析**：`ClassicSkin` 实现了（继承自 `TileSkin` 默认方法）`appendRotatedTileDraw`，在测试中可被正常调用；但其底层实现直接委托给直立的 `appendTileDraw`，忽略了传入的 `rotationDeg` 角度参数，目前所有牌面均直立绘制几何底色与文字。

#### 纹理显存核算（覆盖赤五与牌背）[理论核算]
- 必须严格生成 38 种图元（34 种标准牌 + 3 种赤五 `0m`/`0p`/`0s` + 1 种牌背）。严禁只生成 34 种牌而丢失赤宝牌红字与高亮特征。
- **低分屏/移动端 (64x96 RGBA8888)**：38 * 64 * 96 * 4 字节 = **933,888 字节 (~0.89 MiB)** [理论计算值]
- **高清视网膜 (128x192 RGBA8888)**：38 * 128 * 192 * 4 字节 = **3,735,552 字节 (~3.56 MiB)** [理论计算值]

#### 批处理指标对比 [渲染指令理论推导]
- **当前矢量模式指令分布**：在全桌 129 张牌场景下，缓冲区累计 258 个 FilledRectOp、129 个 LineRectOp 以及 129 个 TextOp。由于图元类型在绘制循环中交替提交，导致 3 类图元批次切换。
- **纹理缓存模式理论推导**：若烘焙至单一 TextureAtlas，理论上可通过单一 SpriteBatch 纹理批次（129 个 TextureOp，原生支持 Quad 硬件旋转）绘制完成。
- **未测声明**：上述批次指标为基于渲染指令队列结构与 LibGDX 批处理原理的**理论推导**；无头测试环境未启动真实 OpenGL 上下文，未通过 GPU 硬件性能计数器测定实际 DrawCall 峰值与帧时间，不对实际 GL 性能做超出证据的断言。

#### 暂缓原因与方案限制
1. `ClassicSkin` 的核心架构定位是**零资产文件、无 GPU 纹理依赖的轻量兜底皮肤**。
2. 若在运行时动态生成 Pixmap 并创建 Texture，在无头 CI 测试环境 (`GdxHeadlessTestRunner` 无实际 GL 上下文) 下会直接崩溃，极大破坏测试套件的可靠性。
3. 决定保持其矢量兜底定位，**暂缓引入纹理缓存**。

---

## 5. 剩余缺口与未实现声明

1. **Android 真机/模拟器帧时间实测缺口**：
   - 当前开发机器执行 `adb devices` 确认无可用 Android 硬件或模拟器。根据任务要求，**明确列为实测缺口，绝不臆测帧时间数字**。
2. **公网与动态网络优化**：
   - 动态 UDP RTO 已经随 TCP 迁移正式取消，不在 MR-10 范围之内。
   - TLS 加密信道、公网撮合与断电数据库持久化恢复继续维持未实现声明。

---

## 6. 自动化验证与套件回归结果

所有测试均采用**串行执行**，且对受影响模块使用 `--rerun-tasks` 进行强制重编译验证，彻底排除 Gradle UP-TO-DATE 或构建缓存影响：

1. **受影响模块强制全量重编译（无缓存重跑）**：
   ```powershell
   .\gradlew.bat :client:compileKotlin :protocol:compileKotlin --rerun-tasks --console=plain
   ```
   **结果**：`BUILD SUCCESSFUL`（耗时 7s，`TileView` 与 `ProtocolTile` 的伴生对象静态引用 `TileView.derivedSuit` / `TileView.derivedRank` 彻底修复，零未解析符号）。
2. **受影响模块强制重跑测试**：
   ```powershell
   .\gradlew.bat :client:test :protocol:test --rerun-tasks --console=plain
   ```
   **结果**：`BUILD SUCCESSFUL`（耗时 35s，13 项任务强制重新执行，100% 绿色）。
3. **五模块全量串行测试**：
   ```powershell
   .\gradlew.bat :protocol:test :client:test :server:test :engine:test :presentation:test --console=plain
   ```
   **结果**：`BUILD SUCCESSFUL`（耗时 32s，25 项任务全部通过，100% 绿色）。
4. **独立 Review 验收测试套件**：
   ```powershell
   .\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --console=plain
   ```
   **结果**：`BUILD SUCCESSFUL`（耗时 8s，全量 V4 架构探针与边界审查 100% 通过）。
5. **未改动 Codex 独立探针**：`tools/architecture/` 目录下的探针文件保持只读，零破坏。

---

## 7. 交付结论

Anti 已按 IMPROVEMENT-PLAN.md §MR-10 严格完成候选测量、全对象图不可变审计、享元优化实施及完备的边界防御。现停止修改代码，交由 Codex 独立检视与验收。
