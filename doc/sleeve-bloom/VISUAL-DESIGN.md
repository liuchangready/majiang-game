# 袖里藏花图形设计

**扩展设计（尚未实现）**：他家回合预定入口、预留牌提示、立直后的和牌/暗杠选择见[立直与他家回合扩展设计](EXTENSION-RIICHI-OFFTURN-DESIGN.md)。本文记录当前已实现的牌桌交互。

日期：2026-10-13。本文随[岭上候选规则修订](ACCEPTANCE-RINSHAN.md)更新，当前验证状态见[执行状态](STATUS.md)。[初版GL验收](ACCEPTANCE.md)保留历史记录。Android真机触控未验证。每次10额度、摸牌后出牌前施术、二选一保留、超时保留第一张；杠后仅可多选下一岭上牌，第四次杠补牌窗口禁用。Anti实现，Codex编写测试并独立检视。

袖里藏花的展示由两部分组成：右列常驻入口按钮（仅在权威 `canApply` 时可用），以及激活成功后由私有快照 `sleeveBloom.pendingOffer` 驱动的**二选一模态面板**——两张候选牌并排展示、点击选中、确认保留或取消（取消即保留第一张）。选择结束后不展示未保留牌的退回去向。整体交互沿用落汗的「选牌 + 确认/取消」两步防误触范式。

## 已确认的展示口径

| 内容 | 口径 |
|---|---|
| 两张候选牌 | 激活后必须同时可见，并标注各自来源（已摸到的 / 牌山次张 / 岭上次张），玩家先选后确认 |
| 未保留的牌 | 选择前两张候选均可见；选择后牌墙无图形、牌山剩余数不变、无退回动画或位置提示 |
| 超时 | 面板随权威结算（保留第一张）自动关闭，不退款提示需表达「额度已消耗」 |
| 其他玩家 | 不出现任何新增全局动画、音效或提示，不展示私有候选；既有余额变化与各自获准的落汗投影照常更新，不能宣称完全无感知 |

以下几何与文案为首版设计定稿，实施与检视时可微调，但按钮位置、模态互斥与倒计时口径不得偏离。

## 入口按钮

右列常驻按钮，位于落汗按钮正下方，间距 6 像素。本文所有矩形均使用 `GameScreen` 的 1280×720 UI 世界坐标：原点在左下，y 向上，输入先经 camera unproject；不直接使用原点在左上的屏幕触点坐标。

| 元素 | 几何（1280×720） | 说明 |
|---|---|---|
| 按钮矩形 | `(1080, 100, 140, 44)` | 上边缘 y=144，距落汗入口 `(1080, 150, 140, 44)` 下边缘 6 像素；实施时验证右列与手牌区无重叠 |
| 文案 | 「袖里藏花 · 10」 | 名称与权威费用并排；禁用时按钮变灰并在悬停/点击时提示禁用原因 |

E3 准备及 Codex 复核发现：既有 `GameScreen.btnTakeoverRect = (1080,108,140,36)` 与本入口重叠，且该区域只有点击逻辑、没有对应可见绘制；原理牌按钮 `(1080,58,140,44)` 也与本入口重叠2像素。因此右下列统一为：落汗 `(1080,150,140,44)`、袖里藏花 `(1080,100,140,44)`、理牌 `(1080,50,140,44)`、托管/收回 `(1080,6,140,36)`。分别保持6、6、8像素间距。移动理牌绘制及命中区域，托管按钮补齐可见绘制并共用实际命中矩形；检查手牌区无重叠。不能仅调整事件分支优先级掩盖重叠：袖里藏花禁用、在途或非本人回合时，点击其按钮仍应被本按钮消费或提示，绝不能穿透切换托管或理牌。保留理牌和托管/收回功能，并分别测试三个实际按钮。此安排替代 PREP-ANTI-E3.md 中仅将托管下移到y=50的初步建议。

可用条件与落汗入口一致：权威 `sleeveBloom.canApply == true` 且非忙碌、非暂停、无在途请求、非任何选牌模态（落汗、立直、副露、洗牌）中。禁用原因直接取权威 `disabledReason` 的中文翻译（见原因码表）。按钮不因余额恰好等于费用而特殊显示；余额不足时按钮置灰并提示「千术点不足」。

点击即提交激活请求（`requestId` 为新 UUID），不乐观扣点、不预弹面板；激活成功后面板由下一份权威快照驱动出现。在途期间按钮禁用（`isPending` 门控）。

## 二选一面板

激活成功、私有快照 `pendingOffer` 非空、连接有效且本人拥有真人控制权、未暂停时弹出模态面板；权威清除 `pendingOffer`（选择完成、超时结算、失效清理或终局）后关闭。断线、托管、暂停只隐藏面板并清空本地选中状态，不表示领域 offer 消失；恢复行为见下文。960×540 视口整体按比例缩放。

| 元素 | 几何（1280×720） | 说明 |
|---|---|---|
| 面板矩形 | `(390, 210, 500, 300)` | 居中偏上，避开手牌区（y ≤ 116）与行动按钮排（y ≈ 145） |
| 标题 | 面板顶部居中，文字基线 y=490 | 「袖里藏花 · 二选一」 |
| 费用标注 | 标题下方，文字基线 y=455 | 「已消耗 10 额度」，面板可见时恒显示；权威超时关闭时以 toast 提示已保留第一张、额度不退 |
| 倒计时 | 面板右上角 | 权威决策剩余时间（与座位时钟同一 deadline），最后 3 秒变红 |
| 第一候选牌卡 | `(460, 290, 90, 126)` | 大尺寸牌面（大于手牌 54×76 以示强调），下方标注「已摸到」 |
| 第二候选牌卡 | `(730, 290, 90, 126)` | 下方标注按 `drawKind`：杠后为「岭上次张」，正常摸牌为「牌山次张」 |
| 选中框 | 牌卡描边 | 点击牌卡选中（青色描边），再点切换；默认未选中 |
| 确认按钮 | `(450, 225, 180, 44)` | 位于牌卡下方；「保留所选」，未选中时灰色不可点，选中后橙色 |
| 取消按钮 | `(650, 225, 180, 44)` | 「保留第一张」；暗红，无需选牌即可点；提交中或身份/计时失效时同样禁用 |

交互细节：

- 面板为模态，注册进 `GameScreen.isModalActive()`：期间手牌点击、出牌、立直、杠、自摸、落汗入口及其他行动按钮全部挂起（引擎侧同时拒绝，双保险）。牌河提示面板若开着则自动收起（复用既有模态自动收起逻辑）。
- 面板外点击不关闭、不穿透（吞掉输入），必须通过两个按钮或 Esc 结束。
- Esc 等价于「保留第一张」。
- 确认提交选择请求（`keep = FIRST/SECOND` 按 所选牌卡：第一卡=FIRST，第二卡=SECOND）后在途期间两按钮禁用；回执 `REJECTED` 时按原因码表 toast 提示，面板保持（`SLEEVE_BLOOM_STALE_OFFER` 等局面失效原因除外——此时面板随快照关闭）。
- 取消提交 `keep = FIRST`，只完成二选一并继续原出牌决策，不立即摸切；超时才在保留第一张后执行默认摸切。两者均不退款，不本地直改领域状态。
- 倒计时归零后面板不立即本地消失，等权威结算的下一份快照清除 `pendingOffer` 后关闭，避免本地时钟与权威不一致时提前关面板。
- 牌卡来源文字位于卡片下沿y=290与按钮上沿y=269之间；注意libGDX BitmapFont.draw的y不能当作字形底部基线，初始绘制参考y=286，实际字形包围盒须完整落在此间隙内；标题、费用、倒计时与牌卡不得互相覆盖，字形与触控热区须经真实 GL 验证。

选择成功后的落点：`pendingOffer` 消失、`myDrawnTile` 更新为保留牌，沿用既有摸牌独立槽与金色高亮渲染，无新增手牌视觉；保留第一张时画面无任何牌面变化。自摸、杠、立直、弃牌入口与预览使用新快照的合法选项，清空旧 `decisionId` / `optionId` 关联的本地动作，不复用换牌前选项。两张候选牌的来源标注帮助玩家理解退回语义，但退回本身不展示去向。

## 状态变化与重连

- 身份变化清空：`sessionId`、`syncEpoch`、`roundId`、`decisionId`、`controlEpoch`、`offerId` 或候选实体任一变化，本地选中状态立即失效；仅当新的权威快照仍含 offer 且具备操作资格时重建面板，默认未选中。视图中的决策键与控制代次取当前权威值，不取激活时旧值；`pendingOffer` 为空时退出选择态。
- 倒计时采用权威时间采样 + 本地单调采样推估，不直接比较两端单调时钟；面板打开、动画或重连不重置预算。暂停时停止外推，恢复后按权威剩余时间继续，遵守原决策时钟的暂停补偿。
- 断线时立即隐藏面板、清空选中，不等断线后收不到的快照；领域 offer 保留。宽限期内重连且仍为真人控制时，含有效 offer 的完整快照恢复面板，不含则关闭。已托管者重连不自动收回，须显式 `claimControl()`；截止前收回后允许按新身份重新选牌，截止后展示权威结算后的局面。
- 托管、暂停、结算、观战状态下不显示面板；恢复真人控制或解除暂停后按最新完整状态恢复仍有效的 offer，不自动补发旧选择、不重新激活或扣费。快照与回执乱序到达时，以当前身份和有序权威快照为准，迟到回执不得重新打开已结束的面板或覆盖新选中状态。
- 落汗与袖里藏花同窗并用时两个模态互斥：打开落汗选牌时袖里藏花入口禁用，反之亦然；各自完成后再开放另一个。

## 层次与接口设计

表现层新增纯视图与纯逻辑组件，类型只依赖 `client.api`：

```kotlin
// 当前实现的关键接口；绘制参数和默认参数值省略。
class SleeveBloomChoiceModel(monotonicClock: () -> Long) {
    val isChoosing: Boolean
    val selected: SleeveBloomKeep?
    val canActivate: Boolean
    val canConfirm: Boolean
    val remainingMillis: Long
    fun update(snapshot: GameSnapshotView?, pending: Boolean, connected: Boolean, nowMillis: Long)
    fun select(keep: SleeveBloomKeep): Boolean
    fun buildActivateRequest(requestId: String): SleeveBloomRequest?
    fun buildChoiceRequest(requestId: String, keep: SleeveBloomKeep): SleeveBloomChoiceRequest?
    fun cancel()
}

class SleeveBloomPanel(session: GameSession, model: SleeveBloomChoiceModel) {
    val model: SleeveBloomChoiceModel
    val isDisposed: Boolean
    fun update(snapshot: GameSnapshotView?, pending: Boolean, connected: Boolean, nowMillis: Long)
    // render(...) 接收共享绘制资源、快照、忙碌状态及其他模态状态。
    fun dispose()
    companion object {
        fun translateServerReason(reason: String): String
    }
}
```

`SleeveBloomChoiceModel` 承担纯逻辑（可用性判定、选中状态、请求构造、deadline 推估、身份失效清空），可无头测试；`SleeveBloomPanel` 持有模型、绘制几何与渲染。`GameScreen` 负责输入分发和异步提交：字段持有、`isModalActive()` 注册、快照/每帧更新、渲染步骤、`touchDown` 模态优先分支、激活/选择各自的防重入标志与 toast、Esc 处理及 `dispose`。`cancel()` 仅清理本地模型；用户 Esc 必须提交 FIRST 选择，不能用本地cancel代替权威结算。

原因码中文表（`translateServerReason`）：`SLEEVE_BLOOM_DISABLED`→「袖里藏花未启用」、`SLEEVE_BLOOM_RIICHI_LOCKED`→「立直后禁止施展」、`SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED`→「庄家起手第 14 张不可施展」、`SLEEVE_BLOOM_NO_DRAWN_TILE`→「当前无刚摸的牌」、`SLEEVE_BLOOM_ALREADY_PENDING`→「二选一进行中」、`SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW`→「本次摸牌已施展过」、`SLEEVE_BLOOM_WALL_EMPTY`→「候选不足（牌山摸完或第四杠不可用）」、`SLEEVE_BLOOM_NO_PENDING_OFFER`→「没有进行中的二选一」、`SLEEVE_BLOOM_OFFER_MISMATCH`→「选择已过期」、`SLEEVE_BLOOM_STALE_OFFER`→「局面已变化，选择失效」、`SLEEVE_BLOOM_CHOICE_PENDING`→「请先完成袖里藏花二选一」；余额不足、超时、代次不符等沿用落汗面板既有翻译。第二候选标签根据来源分为「牌山次张」与「岭上次张」。面板借用UiAssets统一拥有的FreeType/NotoSansSC中文字体（按需生成字形），不自行重复加载或释放共享字体，不依赖系统字体。`SLEEVE_BLOOM_CHARS`枚举可见文案与原因翻译，供字形覆盖契约检查。

主菜单「雀圣千术绝技秘籍」弹窗追加条目：「袖里藏花：摸牌后出牌前消耗 10 额度，同时翻看第二候选牌（牌山次张或杠后岭上次张，第四杠不可用），二选一保留，未选的留在或退回对应位置；超时默认保留原摸牌并摸切，费用不退。」产品组装（`MahjongGame.launchGame`）配置 `costs = {"TILE_MARK" to 10, "SLEEVE_BLOOM" to 10}`；当前单机与建房入口随落汗一起显式设置 `SleeveBloomConfig(enabled = true)`；领域及协议模型缺省仍为关闭。

## 实施顺序与验收

本设计在 [DESIGN.md](DESIGN.md) 第一、二阶段（Engine、接口与 API）通过验收后实施；实施仍由 Anti 实现、Codex 测试检视。

| 验收项 | 必须覆盖 |
|---|---|
| 入口按钮 | `canApply` 各禁用原因（立直、非本人窗口、无摸牌、庄家第 14 张、已施术、牌山空、余额不足）下的置灰与提示；在途禁用；与其他模态互斥 |
| 面板出现与消失 | 激活成功后随快照弹出；选择、取消、超时、终局与失效清理随快照关闭；断线/托管/暂停即时隐藏，不据此清除领域 offer；快照与回执两种乱序及旧回执迟到 |
| 二选一交互 | 两卡点击选中与切换、确认需先选中、保留第一张无需选牌；在途/身份失效时两按钮与 Esc 均不能重复提交；面板外点击不穿透不关闭；两张牌来源标注正确 |
| 倒计时 | 与座位时钟一致、最后 3 秒变红、归零后等权威快照关闭；打开面板与动画不延长预算；暂停停止外推、恢复正确；重连与收回控制权不赠送时间 |
| 落点渲染 | 保留第二张后摸牌槽显示新牌且金色高亮不变；自摸/杠/立直等入口按新选项增减，弃牌实体与预览更新；旧动作不可提交；保留第一张牌面不变；无退回去向可视化 |
| 原因提示 | 全部新原因码与复用码的中文 toast；`STALE_OFFER` 类失效时面板关闭而非滞留 |
| 重连与身份 | 宽限内重连恢复；托管重连未收回不显示，截止前显式收回后按新代次恢复；超时后不重开；暂停恢复；身份或候选变化清空选中；不补发旧选择、不重复扣费 |
| 布局与实机 | 1280×720 与 960×540 真实 GL 截图：面板与手牌、行动按钮、牌河提示不重叠；`SLEEVE_BLOOM_CHARS` 字形覆盖测试；Android 构建与真机触控分别记录 |

文档阶段仅检查设计一致性与引用；实施阶段运行相关新增测试、全量回归、桌面及 Android 构建和真实 GL 检查。

## 源码依据

- [现有客户端视图](../../client/src/main/kotlin/com/majiang/client/api/ViewModels.kt)（`GameSnapshotView` / `PlayerView.myDrawnTile` / `cheatPoints`）
- [落汗客户端契约（模板）](../../client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt)
- [落汗选牌模型（代次清空与 deadline 采样模板）](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt)
- [落汗面板（按钮几何、字体字形子集、原因翻译模板）](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt)
- [牌桌绘制与模态互斥入口](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt)
- [袖里藏花规则设计](DESIGN.md)
