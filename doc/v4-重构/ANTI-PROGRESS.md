# Anti 开发进度跟踪 (ANTI-PROGRESS.md)

- **状态**: MR10_REWORK_COMPLETED / READY_FOR_CODEX_VERIFICATION
- **当前批次交付文档**: `doc/v4-重构/ANTI-MR10-DELIVERY.md`
- **基线**: `a36de58` (HEAD)
- **更新时间**: 2026-10-18（Anti 完成 Codex 检视四项返修：Mapper 矛盾校验防回退、权威局况载荷采样与 136 牌物理守恒验证、实测/核算/推导严格分类、ClassicSkin 委托实现表述修正）
  - **MR-10 返修实施与验证内容**：
    1. **Mapper 与 TileView 拒绝语义防回退（已修复并增补回归）**：
       - `TileView` 构造器增加 `suit == derivedSuit(tileId)` 与 `rank == derivedRank(tileId)` 严格不变量校验。
       - `ProtocolViewMapper.toProtocolTile` 增加 `toProtocolSuit(tile.suit) == expected.suit && tile.rank == expected.rank` 享元前一致性断言，彻底防止非法或矛盾构造静默通过。
       - `TileViewFlyweightContractTest` 增补测试：验证 `TileView(0, PIN, 1)` 等矛盾花色/点数严格抛出 `IllegalArgumentException`，保持原有的拒绝语义。
    2. **zlib 阶段性载荷真实采样与物理牌守恒验证（已修复并重新测量）**：
       - 彻底废弃手写 JSON 样本，新增 `Mr10PayloadCaptureTest` 通过真实权威 `InProcessServer` / `TableAuthority` 驱动完整四人带千术对局，实时捕获 6 个阶段的真实载荷：`EARLY_GAME` (6,484 B), `MID_GAME` (4,497 B), `LATE_GAME` (8,880 B), `TILE_MARK` (6,396 B), `CANDIDATES` (6,484 B), `SETTLEMENT` (7,330 B)。
       - `Mr10TileAndZlibMeasurementTest` 接入真实样本，并增加严格物理守恒断言：验证所有台面活动牌（暗手牌 + 摸牌 + 四家打牌河 + 副露 + 宝牌指示牌）100% 满足 0..135 物理区间、花色/点数双向严格映射且台面无任何重复 tileId。
       - 基于真实采样重新测量 zlib 压缩率（72.8% ~ 81.7%）、压缩耗时（45.27 ~ 76.92 µs）与解压耗时（11.98 ~ 18.77 µs）。
    3. **实测、核算与理论推导严格分类，移除未测断言（已修正）**：
       - 严格标记 `[实测数据]`（JVM 堆分配、nanoTime CPU 耗时、压缩尺寸）、`[理论核算]`（38 种牌型显存字节数 38*W*H*4）、`[理论推导与协议规格评估]`（渲染指令队列批次推导、单帧 1 MiB 余量评估）。
       - 彻底移除未经实机抓包测定的“局域网传输 <0.1 ms”断言与未经验证的 GPU 帧时间断言。
    4. **ClassicSkin 旋转实现表述修正（已修正）**：
       - 纠正报告表述：明确 `ClassicSkin` 实现了（继承自 `TileSkin` 默认实现）`appendRotatedTileDraw`，且测试中正常调用；其当前行为是将旋转绘制直接委托给直立的 `appendTileDraw`，忽略角度参数。
    5. **136 张 TileView / ProtocolTile 享元实施交付**：
       - `ProtocolModels.kt` 与 `ViewModels.kt` 建立 136 静态享元池，`fromTileId` 与 `fromTypeIndex` 直接返回享元实例。
       - `ApiMessage.kt` 中 `ProtocolTileAdapter` 严格遵循“反序列化先校验矛盾字段”原则。
       - 堆分配 100% 消除，映射往返耗时降低 69.3%。
    6. **快照共享替代深拷贝测量与暂缓 (DEFERRED)**：
       - 全对象图不可变审计证明跨座位共享存在暗牌泄漏严重漏洞，`deepCopy` 仅占 1.2% 分配，按测量结果记录暂缓。
    7. **Android 帧时间与硬件缺口**：
       - 经 `adb devices` 探测确认开发机无连接的 Android 真机或模拟器，明确列为实测缺口，绝不臆测。
  - **新增与更新测试用例**：
    - `protocol/src/test/kotlin/com/majiang/transport/api/Mr10TileAndZlibMeasurementTest.kt`（真实载荷验证、守恒断言、zlib 测量、炸弹防御）
    - `protocol/src/test/resources/mr10-payloads/*.json`（6 个真实权威捕获的阶段性载荷文件）
    - `server/src/test/kotlin/com/majiang/server/mr10/Mr10PayloadCaptureTest.kt`（权威进程实时采样生成测试）
    - `protocol/src/test/kotlin/com/majiang/transport/api/ProtocolTileFlyweightContractTest.kt`（ProtocolTile 享元自洽与矛盾字段反序列化阻断）
    - `client/src/test/kotlin/com/majiang/client/mapping/Mr10TileViewBenchmarkTest.kt`（TileView 享元与 ProtocolViewMapper 映射实测）
    - `client/src/test/kotlin/com/majiang/client/api/TileViewFlyweightContractTest.kt`（TileView 享元完备性、赤五保真、矛盾构造与映射拒绝回归）
    - `server/src/test/kotlin/com/majiang/server/mr10/Mr10SnapshotAuditAndHotspotTest.kt`（快照不可变审计与 deepCopy 分配热点测量）
    - `presentation/src/test/kotlin/com/majiang/presentation/common/render/Mr10ClassicSkinMetricsTest.kt`（ClassicSkin 旋转委托、显存预算与批处理指标）
  - **测试验证**：
    - 强制无缓存重编译与重跑（`:client:compileKotlin :protocol:compileKotlin --rerun-tasks`）100% 编译通过，彻底修复 `TileView.derivedSuit` / `TileView.derivedRank` 符号作用域。
    - 受影响模块强制重跑（`:client:test :protocol:test --rerun-tasks`）13 项任务 100% 通过。
    - 五模块串行测试（`:protocol:test :client:test :server:test :engine:test :presentation:test`）100% 通过。
    - 独立 Review 验收套件 100% 通过。
    - 工作区既有未提交改动（MR-06、MR-07a、MR-09、TCP 迁移）完整保留，零破坏。

- **历史阶段记录**:
  - **MR-01**: 已提交 `35a87c6` 并验收通过（已推送）。
  - **MR-02**: 已提交 `8a4b59f` 并验收通过（已推送）。
  - **MR-03**: 已提交 `3a382c6` 并验收通过（未推送）；验收记录 `doc/v4-重构/CLAUDE-MR03-REVIEW.md`。
  - **TCP-T1**: 开发+一轮返修后验收通过，提交 `1664c60`，见验收记录 `doc/v4-重构/CLAUDE-TCP-T1-REVIEW.md`。
  - **MR-06/07/09**: 返修完成，交付报告 `doc/v4-重构/ANTI-MR06-MR09-REWORK-DELIVERY.md`。
  - **MR-10**: 测量驱动优化完成，交付报告 `doc/v4-重构/ANTI-MR10-DELIVERY.md`。
- **基线SHA**: `1664c60` / 当前开发基于 `a36de58` (HEAD)
