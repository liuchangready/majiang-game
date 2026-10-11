# 落汗 (Tile Mark) 开发进度 - Anti

Codex 最终复核（2026-10-11 23:57）：P1FIX2已回调，完整功能通过独立验收；1000项测试、双端构建、产品GL及两种尺寸的可视GL全部通过。详见[最终验收](ACCEPTANCE.md)。下文为Anti的分阶段交付历史。

## 历史阶段

### E3：权威接线、原子费用与私有投影 (TASK-ANTI-E3)
- **状态**: 复核退回修复（发现通知回调锁升级死锁）
- **历史记录**: 36 项落汗测试 / 376 项 engine 测试全数通过，见 [`doc/tile-mark/DELIVERY-ANTI-E3.md`](DELIVERY-ANTI-E3.md) 与 [`doc/tile-mark/CALLBACK-ANTI-E3.json`](CALLBACK-ANTI-E3.json)。

### E3FIX1：关闭通知回调死锁与权威清理顺序修复 (TASK-ANTI-E3FIX1)
- **状态**: 已通过 Codex 独立验收（落汗 37 项、engine 377 项通过，跨模块构建测试成功）
- **交付内容**:
  - 生产代码修改：
    - [`engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt)
    - [`engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)
  - 交付说明：[`doc/tile-mark/DELIVERY-ANTI-E3FIX1.md`](DELIVERY-ANTI-E3FIX1.md)
  - 回执声明：[`doc/tile-mark/CALLBACK-ANTI-E3FIX1.json`](CALLBACK-ANTI-E3FIX1.json)

### A1 / A1FIX1：线路契约与编解码 (TASK-ANTI-A1 / TASK-ANTI-A1FIX1)
- **状态**: 已通过 Codex 独立验收（11 项契约测试、protocol 16 套件 151 项全数通过）
- **交付内容**:
  - 生产代码修改（仅限 `protocol`）：
    - [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolTileMarkModels.kt`](../../protocol/src/main/kotlin/com/majiang/transport/api/ProtocolTileMarkModels.kt) (独立 DTO 定义、不可变防御性复制、强类型约束与牌河精确定位)
    - [`protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt`](../../protocol/src/main/kotlin/com/majiang/server/api/SessionApi.kt) (新增 `submitTileMark` 异步接口，提供明确失败默认实现)
    - [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`](../../protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt) (`ProtocolAccessCapabilities.tileMarkSupported`、`ProtocolMatchConfig.tileMarkConfig`、`ProtocolPlayerSnapshot.tileMark` 私有字段与牌河实体匹配校验)
    - [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](../../protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt) (`ApiTags.SUBMIT_TILE_MARK`、`ApiRequest.tileMark`、严格 Gson TypeAdapters、白名单与互斥载荷校验、旧线兼容)
  - 交付说明：[`doc/tile-mark/DELIVERY-ANTI-A1.md`](DELIVERY-ANTI-A1.md) 与 [`doc/tile-mark/DELIVERY-ANTI-A1FIX1.md`](DELIVERY-ANTI-A1FIX1.md)
  - 回执声明：[`doc/tile-mark/CALLBACK-ANTI-A1.json`](CALLBACK-ANTI-A1.json) 与 [`doc/tile-mark/CALLBACK-ANTI-A1FIX1.json`](CALLBACK-ANTI-A1FIX1.json)

---

## 当前阶段

### A2：服务端映射、本地和 UDP 接线 (TASK-ANTI-A2)
- **状态**: 已通过 Codex 独立验收（135 项全量通过，交付文档示例与源码对齐纠偏完成）
- **交付内容**:
  - 生产代码修改（仅限 `server`）：
    - [`server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`](../../server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt)
    - [`server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`](../../server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt)
    - [`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`](../../server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt)
    - [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](../../server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
    - [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](../../server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
    - [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](../../server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
  - 交付说明：[`doc/tile-mark/DELIVERY-ANTI-A2.md`](DELIVERY-ANTI-A2.md) (已按实际源码更正 `HostReceiptKey.Action/Shuffle/Mark`、错误码 `STALE_SESSION/SEAT_DISCONNECTED/SEAT_NOT_HUMAN`、去除虚构 helper)
  - 回执声明：[`doc/tile-mark/CALLBACK-ANTI-A2.json`](CALLBACK-ANTI-A2.json)

---

## 当前阶段

### A3：客户端门面、映射与恢复 (TASK-ANTI-A3)
- **状态**: 已通过 Codex 独立验收
- **交付内容**:
  - 生产代码修改（仅限 `client`）：
    - [`client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt`](../../client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt) (纯净领域 DTO：TileMarkConfig/Request/Area/MarkedTile/Group/View/RiverPositionView，不可变防御性复制与强约束校验)
    - [`client/src/main/kotlin/com/majiang/client/api/ClientModels.kt`](../../client/src/main/kotlin/com/majiang/client/api/ClientModels.kt) (MatchConfig.tileMarkConfig 与 Capabilities.tileMarkSupported 扩展与深拷贝支持)
    - [`client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`](../../client/src/main/kotlin/com/majiang/client/api/ViewModels.kt) (GameSnapshotView.tileMark 可空私有快照与牌河实体匹配校验)
    - [`client/src/main/kotlin/com/majiang/client/api/GameSession.kt`](../../client/src/main/kotlin/com/majiang/client/api/GameSession.kt) (GameSession.submitTileMark 接口与默认失败实现)
    - [`client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt`](../../client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt) (配置、能力、快照与标记模型的完整双向映射，toProtocolTileMarkRequest 转换)
    - [`client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt`](../../client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt) (submitTileMark 门面、pendingState & activeActionId 在途锁与出牌/洗牌互斥、actionSubmissionSeq 递增与防旧响应解锁、完整快照原子替换、离房/转观战私有状态清理)
    - [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](../../client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt) (submitTileMark 覆盖 SessionApi，通过 ApiTags.SUBMIT_TILE_MARK 接入可靠请求传输)
  - 交付说明：[`doc/tile-mark/DELIVERY-ANTI-A3.md`](DELIVERY-ANTI-A3.md)
  - 回执声明：[`doc/tile-mark/CALLBACK-ANTI-A3.json`](CALLBACK-ANTI-A3.json)

---

## 当前阶段

### P1：表现层与组合根 (TASK-ANTI-P1)
- **状态**: ready_for_review (已通知 Codex 独立复核)
- **交付内容**:
  - 生产代码修改（仅限 `presentation`）：
    - [`presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPresentation.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPresentation.kt) (牌河精确核对、对手手牌识别、位置中文文案与有效期限格式化)
    - [`presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt) (纯选牌模型：四牌选择、第五张拒绝、代次/流切换重采样、暂停/断线/托管/配额作废清理)
    - [`presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt) (真实交互几何矩形、三组跟踪分页栏、对手识别渲染、拒绝原因翻译与全量中文字型)
    - [`presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) (暴露 tileMarkPanel、防双击在途提交、手牌抬高、牌桌各处微痕渲染、互斥门控与安全释放)
    - [`presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt) (单机/大厅新建房间启用 tileMarkConfig 与费用、秘籍弹窗文案更正)
    - [`presentation/src/main/kotlin/com/majiang/MahjongGame.kt`](../../MahjongGame.kt) (默认产品单机房间显式启用 tileMarkConfig 并追加 TILE_MARK=10)
  - 文档更正：
    - [`doc/tile-mark/DELIVERY-ANTI-A2.md`](DELIVERY-ANTI-A2.md) 与 [`doc/tile-mark/DELIVERY-ANTI-A3.md`](DELIVERY-ANTI-A3.md) 更正去除虚构字段及 0 费用描述。
  - 交付说明：[`doc/tile-mark/DELIVERY-ANTI-P1.md`](DELIVERY-ANTI-P1.md)
  - 回执声明：[`doc/tile-mark/CALLBACK-ANTI-P1.json`](CALLBACK-ANTI-P1.json)
- **验证结果**:
  - 定向 UI 契约测试：**9 项落汗契约测试通过** (`:presentation:test` 204 项全数通过，0 失败，0 错误)。
  - 真实桌面 GL 冒烟测试：**PASS** (`:lwjgl3:tileMarkSmoke` 真实视口 1280x720，3 轮落汗双击提交、扣费 30、12 牌配额满、弃牌索引 0、截图 6 份、ESC 返回菜单无泄漏)。
  - 项目全量测试：**65 任务全部通过** (`./gradlew test`)。
  - 跨平台构建：`:lwjgl3:classes` 通过，`:android:assembleDebug` 通过。
- **状态声明**:
  - 生产代码仅修改 `presentation` 模块；
  - 零修改测试或测试夹具；
  - 未执行 Git 提交，未启动额外代理，未改动用户 `lwjgl3/build.gradle`、`SnapshotLauncher.kt`、`snapshot/`；
  - 回调文件仅保存在 `doc/tile-mark/` 下。
