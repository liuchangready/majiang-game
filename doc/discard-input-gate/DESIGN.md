# 出牌演出结束前的输入锁（Codex设计/测试，Anti开发）

## 已确认问题
GameScreen.isHumanTurn 只查最新 PlayerView 逻辑权限，LocalGameSession.submit 可同步跑完多位AI并返回自己的下一回合。此时上轮弃牌动画队列还在播，用户可连续出牌；checkDiscardsDiff 仅 render 调用，还存在 submit 更新视图至下一帧之间的抢点窗口。

## D任务：只修改 GameScreen.kt（必要时新增小型纯表现层helper）
不改领域引擎/会话/规则/测试/Gradle。使用内置文件读写搜索，不运行shell、RunCommand、terminal、MCP或其他agent，不改权限。Codex负责所有测试和编译。交付说明最多10行。

1. 新增只读 `val isDiscardPlaybackBusy: Boolean`，定义为 currentDiscardAnim!=null || discardQueue.isNotEmpty()；只读，不能偷偷清动画或改领域。手牌出牌、立直选牌、吃碰杠方案、和/Pass、自摸/九种九牌等牌局操作与理牌，均在busy期间不可点击；返回菜单、结算按钮仍可用。锁期点击直接吞掉，不排队在解锁后补发。
2. **消除帧前窗口**：在订阅 SessionUpdate 中更新currentView、处理事件后立刻调用 checkDiscardsDiff() 同步入队，使 session.submit 返回前锁即生效。保留render检测作为幂等兜底。构造时 lastDiscards 已取初始视图，不重播已存在牌河。StackingStarted 重置 lastDiscards 到新视图并清空动画，避免新局遗漏/残留；和牌/流局继续快进所有动画。
3. touchDown 拦截放在返回/结算之后、SORT及所有游戏交互之前，必须覆盖已经打开的立直/副露选牌确认。hover在busy期间清除手牌、候选和理牌的悬停；入队当下也清掉旧hover，render先更新动画再计算hover，不显示误导的可操作高亮。
4. isHumanTurn 必须同时 !busy 和真实存在属于viewer的合法 DiscardTile（去掉仅isMyTurn+phase即允许的宽松旁路），保留gameStarted/结算/选牌模式条件。所有 canHumanRon/Tsumo/Pon/Chi/Kan/Riichi/Pass/AbortiveDraw 查询增加!busy，以使按钮显示与点击一致；canHumanAgariYame 不受动画锁影响，结算本身清队列。
5. renderActionButtons 在busy时不给玩家显示可用出牌动作，显示简短可读 `WAITING FOR DISCARDS...`；SORT也不可点击且无可用悬停。已打开选择面板可暂时隐藏（模型保留并继续随view失效刷新），busy结束恢复合法操作，不能自动替玩家Pass。无需生成新字体。
6. 清理队列边界：checkDiscardsDiff 时从当前/待播动画中移除已被吃碰杠取走的牌（按seat+真实Tile身份），不能让已经不在牌河的队列项拖延解锁。正常排队3张必须播完3张才解锁，中间current变null但queue非空不可放行。新局、结算、hide/dispose仍清理，不用计时器sleep或固定冷却代替真实队列。
7. 不因为动画锁修改引擎legalActions/响应优先级，也不自动提交任何动作。不禁用on-response真正需要等玩家选择的机会：最后动画结束后原有荣和/吃碰杠/Pass须恢复，避免卡局。

## Codex验收
- 同步submit后立刻二次点击，未render也只能提交1次。
- 一次快照带3张弃牌，正在播/待播全部结束前不能点手牌、SORT、立直或和牌/副露/Pass；中间衔接无解锁间隙；解锁后新点击正常，旧点击不回放。
- RESPONSE_WINDOW无DiscardTile时手牌不可出，动画结束后Ron/Pass恢复并可执行；只有Tsumo没有Discard的本人回合不能擅自出牌。
- 已打开立直/副露面板不能绕过锁确认；牌河移除待播牌会清掉该项；结算、新局释放锁；既有364项回归保持（如旧UI测试依赖新弃牌后立即操作，由Codex按新行为补演出推进，而非放松生产锁）。
- 全量test、桌面/Android构建；真实GL验证等待提示及解锁手牌点击。
