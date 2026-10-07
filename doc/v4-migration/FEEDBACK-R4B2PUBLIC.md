# Reviewer 提前发现的不可变边界

当前Codex独立探针正在编写，尚未运行：现有 snapshotGameEvent 对 ScoreSettled/MatchEnded 只复制 IntArray，event getter本身仍返回可修改数组。因此仅 Collections.unmodifiableList(events.map(::snapshotGameEvent)) 不满足公开投影的不可变getter契约，读取其中event数组后修改仍会污染同一输出。

请结合冻结 publicEvents: List<GameEvent> API 做防御式读取或等效深快照，不改已有领域事件类/跨任务文件，不删除真实公开结算事件来规避。PublicTableProjection.settlementEvents、publicEvents返回列表及嵌套评分/历史均需经得起输入修改和getter修改。Reviewer将验证再次读取仍为原值；如用copy-on-read List，其迭代/索引与不可修改语义都应一致。

其他任务约束不变。不是编译错误报告；交付前按要求自查。

## 14:07 深层结算里宝边界

`revealSettlementUra=false` 还必须覆盖 `RonWin/TsumoWin.breakdown.dora.uraTiles/uraHits`，不能只过滤独立 `DoraRevealed(isUra=true)`。`uraEligible=true` 只是计分资格，不能替代调用者对真实公开结算的授权。无授权时剥除这些里宝实体/命中详情；保留公开役种、符、支付、总分及计分数值，不删除和牌事件或整个 breakdown。`true` 时保留完整真实结算明细。独立测试正在加入这个两侧断言。

## 初版DTO落盘后的源码边界

新 `PublicScoreRecord.outcome` 直接保存领域 `RoundOutcome`，其 `ExhaustiveDraw.tenpaiSeats: Set<Int>` 可以来自 mutableSet，当前构造/create/copy/deepCopy 都未防御该嵌套别名。请在本范围内对该分支防御复制并不可修改，保留真实听牌座位，勿改已有RoundOutcome。新公开字段也必须满足原不可变要求。

`CopyOnReadEventList` 的数组读取隔离方向正确；请自查数组事件的比较语义：`GameEvent.ScoreSettled/MatchEnded` 的数据类 equals 按数组引用，`get(i)`每次clone，因此当前 `equals`、`contains(get(0))` 和 projection 的copy/deepCopy等值可能意外失效。至少确保自己公开的DTO复制后值语义一致，不把当前未编译源码视为验收。保持范围，不修改原事件类；若无法在List契约下合理处理，请在交付中明确说明方案。
