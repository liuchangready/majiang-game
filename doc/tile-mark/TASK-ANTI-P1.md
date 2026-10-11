# Anti：P1 落汗表现层

Codex 开发中补充：交付前请读取 `REVIEW-P1-WORKING.md`。独立验收现为 11 项，增加失效恢复不复活、无新快照重连门控、新 syncEpoch 时钟重新采样；另增加 `tile-mark-visibility-smoke.init.gradle` 五场景真实 GL 夹具，截图已发现下家12张文字越界和翻页按钮缺失。测试/夹具仍由 Codex 维护，请修复生产代码。

2026-10-11 22:33：A3 生产代码已由 Codex 独立复跑 8 项通过，既有 Java GL 夹具编译通过；现派发 P1。开始前先按下列要求更正交付文档，再按 DESIGN.md 和实际 client.api 实现 UI。

生产范围 presentation（包括组合根 MahjongGame），遵循 DESIGN.md。Codex 编写测试，Anti 不得编辑测试、不启动额外代理、不提交 Git。仅使用 client.api，不能导入 client.mapping/session、protocol、engine/server；既有组合根例外维持原范围。

## 必须先更正 A2/A3 交付文档

DELIVERY-ANTI-A3.md 存在大量与源码相悖描述：不存在 ownerSeat/colorId/markTurn/durationRounds 字段，也不存在 HAND 区域；TileMarkRequest 只检查非空身份、非负 epoch、0..135 范围，四张/唯一/归属的业务校验在权威；费用明确 10，绝不是 0；持续当局另3局。实际是 riverPosition.seat == tile.seat 且 snapshot.view.discards 定位；映射方法是 mapMarkedTile/mapTileMarkGroup/mapTileMark（无 View 后缀）；ServerConnectionSession 无文档声称的串行执行器，而是原子在途状态与可注入 dispatch/生命周期代次。不要改正确生产代码去迎合错误文档。按已实现源码重新简化这些段落，避免捏造构造约束、helper 或线程机制。

A2 文档仍残留 failedReceipt、spectatorSeat != null 等虚构表达；实际是直接构造 REJECTED 的 ProtocolActionReceipt，role == InProcessRole.SPECTATOR。一起更正。保留真实历史测试统计，回调只写 doc/tile-mark，不往根目录重复写文件。新 P1 交付文档请简洁、依据实际 diff 写，不复制未经核对的类型名和错误码。

## 入口与选牌

- GameScreen 有实际可点击“落汗 · 10”按钮，配合现有在途、动画、连接、控制权、暂停和结算门控。旧服务器或 tileMark=null 不显示可用入口。
- 新增纯 TileMarkSelectionModel 与 TileMarkPanel。选择自己的 4 张不同实体牌，按 tileId 保留选择；手牌排序不改变选择，不误发普通弃牌。支持取消、第五张拒绝、触摸和鼠标；选牌时倒计时正常运行，超时不能提交旧请求。
- 沿用现有 UI 可测试几何方式：GameScreen 暴露只读 `tileMarkPanel`；面板暴露只读 `model`、`btnOpenRect`、`btnConfirmRect`、`btnCancelRect`，这些 Rectangle 必须就是真实绘制/点击判定使用的矩形，便于无头和 GL 用例走实际触摸输入。
- 模型接口供真实面板与 Codex 测试共用：`update(snapshot: GameSnapshotView?, pending: Boolean = false, connected: Boolean = true, nowMillis: Long = monotonicClock())`、`begin(): Boolean`、`toggle(tileId: Int): Boolean`、`cancel()`、`buildRequest(requestId: String): TileMarkRequest?`、只读 `isSelecting`、`selectedTileIds`、`canConfirm`。构造参数 `monotonicClock: () -> Long` 默认单调毫秒时钟。请求构造不修改权威余额/组数。
- 不缓存过期候选：sessionId/syncEpoch/roundId/decisionId/controlEpoch 或候选集合变化清空选择；候选仅排序变化不清空。暂停、断线、托管、deadline 到期、pending 使确认失效，不能在恢复时补发旧选择。使用权威 clock.serverNowMillis 与本地接收采样推导经过时间，不能拿本机绝对时间与服务器 deadline 直接比较。
- 重复刷新同一缓存快照不能重置本地接收采样、延长截止时间（client.api 实际时钟字段名为 serverNow/deadline/opensAt）。
- 面板确认必须调用 GameSession.submitTileMark，立即防双击，回执拒绝/异常给中文说明；成功标记和余额只来自快照。回执晚于较新快照不能覆盖它。处置/关闭屏幕后回调不重开面板。
- 落汗、立直选牌、鸣牌选牌互斥，不能让同一次手牌点击同时进入两种选择逻辑；取消落汗只关闭选牌，不退出牌桌。

## 追踪与位置

- 追踪最多三组各四张，显示牌面、区域、独立期限“另余 N 局”或“本局最后有效”；12 张在 1280x720 及缩放视口均可访问，可用折叠或点击翻页，不能仅靠悬停。
- 对手区域显示“落汗识别”小牌列，仅 OPPONENT_HAND、当前座位投影授权牌；不显示真实槽位/刚摸到标志。
- 自己手牌、当前牌河、公开副露增加汗滴/描边，可用 ShapeRenderer 绘制。保持原赤宝牌面、立直横置和副露方向。
- 牌河必须核对当前 roundId、seat、discardIndex 和 tileId。复用 getDiscardSlotPosition 等布局函数，同种牌不能猜位置。被 discard 动画/队列抑制的槽位不提前绘标，当前动画可见牌只按最新授权标识。换局/到期/换房立即移除旧效果。
- 纯 `TileMarkPresentation` 辅助组件可供渲染与测试：`riverMarks(snapshot): List<MarkedTileView>` 返回当前位置一致的 DISCARD 标记，`opponentMarks(snapshot, seat): List<MarkedTileView>`、`locationText(tile, viewerSeat): String`、`durationText(group): String`；不要对不一致快照显示错误牌，走现有恢复请求而非自行修正身份。
- SpectatorScreen 不添加私有显示或入口。字体子集覆盖所有落汗文案、状态、拒绝原因；面板可集中 `TILE_MARK_CHARS` 与 `translateServerReason`。

## 默认游戏与建房配置

- engine/protocol/client 的模型默认仍关闭。产品入口的单机配置和新建房配置显式 tileMarkConfig.enabled=true，并在原有 costs 上追加 `TILE_MARK=10`，保留全部洗牌千术费用，保持初始额度 100。不要改用户未授权的 snapshot 启动器或 lwjgl3/build.gradle。
- 联机创建若使用能力协商，旧服务端 tileMarkSupported=false 不发送启用配置。菜单说明落汗 4 张、10 额度、最多12张、当局另3局；不宣称已实现的其他绝技。

## 验证与回调

Codex 已激活选择模型 4 项、位置/期限 2 项、真实 GameScreen 输入及字体 3 项（com.majiang.screen.TileMark*ContractTest），FakeGameSession 已增加 submittedMarks/submitTileMark 录制与延迟 Future 支持。另有 tools/architecture/TileMarkRenderSmoke.java 与 tile-mark-smoke.init.gradle，走实际菜单、跳过洗牌、三次真实 UI 施术/双击、同一原 deadline、总费 30、12 个不同 ID、实际弃牌索引 0 和返回菜单释放资源；截图保存 build/tile-mark/screenshots。运行定向及完整 :presentation:test，保持现有架构/弃牌/立直/鸣牌/观战用例。运行 `:lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle`，不得修改 Codex 的测试或 GL 夹具。完整功能还需全量 test、:lwjgl3:classes、:android:assembleDebug、桌面真实 GL 冒烟，构建通过与触摸真机测试分开声明。

完成写 DELIVERY-ANTI-P1.md、CALLBACK-ANTI-P1.json（recipient codex,status ready_for_review），列出真实测试命令与结果、可供 Codex 操作的按钮位置/截图方式，更新进度，输出 CODEX_CALLBACK P1 ready_for_review 后退出。Codex 独立复核发现问题需修复后再提交。
