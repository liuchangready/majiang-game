# 立直连续出牌修复设计与开发任务

## 原因

ApplicationCoordinator.runAutomaticActionLoop 在一次命令中持续执行 AI 和真人立直摸切，直到真人有自摸/杠/响应选择或流局。一次广播因此可能跨越多轮。GameScreen.checkDiscardsDiff 再按 seat 0..3 遍历最终牌河，导致弃牌动画按座位聚集而不是事件时间顺序。

## Anti 开发范围

只修改 production Kotlin 与 delivery-F4.md，不修改测试、构建脚本或本设计。Codex 负责测试和执行验证。

1. ApplicationCoordinator 构造末尾增加 `val deferAutomaticActions: Boolean = false`，默认保留现有无头/兼容同步行为。启用时 start、submitChoice、submitPlayerAction、handleAction、nextRound、控制者变更等顶层命令不附带自动动作循环。公开可信宿主入口 advanceAutomaticActions 在该模式每次最多执行一个自动动作（AI 响应/AI 回合/真人立直摸切各算一个），随后提交广播。复用同一选择逻辑，不复制麻将规则；非延迟模式行为保持不变。单个动作内部自然触发的摸牌/窗口推进仍是原子转换。
2. 新建 `application/LocalTableRuntime.kt`，与客户端分离的本地宿主调度器。公开 `coordinator` 与 `endpoint: LocalPlayerEndpoint`，构造默认 coordinator=ApplicationCoordinator(deferAutomaticActions=true)，require 注入 coordinator 延迟模式。公开 `update(delta: Float, presentationBusy: Boolean)` 与 close。动画忙时不推进也不累积欠账；空闲至少 0.15 秒才调用一次 advanceAutomaticActions；一帧最多一次，不用 while 追赶，忽略非法/负 delta，大 delta 只触发一次；close 后不推进。无计时线程，无 GL 依赖。普通真人选择、响应等待、自摸和合法立直暗杠均由 coordinator 保持暂停；该方法不会自动 Pass 真人或代选和牌/杠。
3. 在 MahjongGame（应用组合根）持有本地 runtime。增加 `createLocalEndpoint()` 创建/注册本地 runtime 并返回 endpoint，替换 GameScreen 便利构造的 LocalPlayerEndpoint()。`MahjongGame.launchGame` 默认路径必须使用此宿主，显式外部 endpoint 不自行创建调度器。覆写 MahjongGame.render：只在当前 screen 是匹配该 runtime.endpoint 的 GameScreen 时，先以 Gdx.graphics.deltaTime 与 screen.isDiscardPlaybackBusy 调用 runtime.update，再 super.render。替换本地桌子与 dispose 时关闭旧 runtime。GameScreen 仍只查询/提交 PlayerEndpoint，不调用 advanceAutomaticActions，不持有 coordinator；远端/共享端点不能通过客户端接口推进 authority。保留现有公共构造及 launchGame(session=...) 兼容。
4. GameScreen 消费 SessionUpdate 时，以 visibleEvents 中 TileDiscarded 的顺序给动画排队，然后做牌河差异兜底和副露调和。按座位+物理 Tile 身份去重，防止同一广播被事件和快照差异重复入队；不凭 seat 循环排序已有事件。不把已被吃碰杠取走的牌重新入队。保留纯快照更新兼容、结算清空、动画期间禁止操作等原逻辑。
5. 不修改 RoundStateMachine/MatchSession 职责与验证，不依赖客户端时钟进行麻将规则裁决。本地播放节奏只属于宿主；旧无头同步批处理仍可用。

## Codex 验收

- 复现一次同步自动动作跨多轮，以及原先动画 seat 分组顺序错误。
- 延迟模式显式弃牌最多产生其本身弃牌；多次宿主 tick 每次最多一个自动动作，0→1→2→3→0 按真实事件推进，无重复物理牌。
- 动画忙时牌山/版本不变；长帧不追赶；close 不再推进。
- 立直自摸、合法暗杠保持选择；真人 Ron/Pass 等响应不被跳过。
- 批次弃牌按事件时间顺序播放（包含非 0 起手及多轮），事件+快照不重复，已有动画继续保持 FIFO；被副露取走的牌不复活。
- 原 431 项回归、桌面与 Android 编译、默认宿主实际渲染冒烟。
