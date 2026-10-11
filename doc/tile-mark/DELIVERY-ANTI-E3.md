# 落汗 (Tile Mark) E3 交付说明 - Anti

## 1. 任务背景与目标
- **任务代号**: E3 (TASK-ANTI-E3)
- **目标**: 完成 engine 领域的落汗千术权威闭环接线，包括：
  - `MatchConfig` 配置校验与透传保护；
  - `MatchSession` 不可变状态与原子候选安装，生命周期实际发牌与结算事件接线；
  - `TileMarkViews` 不可变视图模型与私有投影工厂；
  - `PlayerSnapshot` 私有落汗字段装配与只读隔离；
  - `ApplicationCoordinator.submitTileMark` 原子扣费与标记组安装，136 张牌物理守恒与零出牌副作用；
  - `TableAuthority.submitTileMark` 权威串行接线、全阶段/身份校验、规范化跨 API 幂等与冲突检测、时钟 rekey 延续（保持 opensAt/deadline/reserve）；
  - 私有投影工厂牌河索引 `RiverPosition(roundId, seat, discardIndex)` 与鸣牌副露/手牌/指示牌状态精准转换。
- **约束遵循**:
  - 严格保持测试由 Codex 维护，未新增、删除或修改任何测试；
  - 仅修改 `engine` 模块生产代码与交付文档，未触碰 protocol、server、client、presentation；
  - 未执行 Git commit，未另起代理。

---

## 2. 修改与交付文件清单

### 生产代码文件
1. [`engine/src/main/kotlin/com/majiang/match/MatchConfig.kt`](../../engine/src/main/kotlin/com/majiang/match/MatchConfig.kt)
   - 追加 `tileMarkConfig: TileMarkConfig = TileMarkConfig()`；
   - `validateSupported()` 在 `tileMarkConfig.enabled` 时强制要求 `cheatEconomy.enabled` 且 `costs["TILE_MARK"] == 10`。
2. [`engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkViews.kt`](../../engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkViews.kt)
   - 定义不可变视图结构：`TileMarkArea`、`MarkedTileView`、`TileMarkGroupView`、`TileMarkView`；
   - 构造器内部对所有列表进行防御性复制并封装为 `Collections.unmodifiableList`。
3. [`engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt`](../../engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt)
   - 追加可空字段 `tileMark: TileMarkView? = null`；
   - 在 `deepCopy()` 中保留其不可变实例；公开投影与公共结果绝不暴露任何落汗字段。
4. [`engine/src/main/kotlin/com/majiang/match/MatchSession.kt`](../../engine/src/main/kotlin/com/majiang/match/MatchSession.kt)
   - 增加只读属性 `var tileMarkState: TileMarkState = TileMarkState() private set`；
   - `SessionChange` 追加 `targetTileMarkState: TileMarkState? = null`，null 保留原值，非空由原子候选安全安装；
   - 追加 `ApprovedSessionStep.ApplyTileMark` 与 `ApprovedSessionStep.RegisterDealtRound`；
   - 生命周期对接：
     - `StartSession`：初始已物理发牌状态登记局号；
     - `AcceptRoundResult`：调用 `completeRound(roundId)` 清理到期组；`FINISH_SESSION` 时清空所有组；
     - `ChooseDealerEnd`：庄家流局结束 (`agariYame`) 时清空所有组；
     - `Close`：关闭场次清空所有组；
     - `StartNextRound` / `Pause` / `Resume` 保留当前标记状态。
5. [`engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkProjectionFactory.kt`](../../engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkProjectionFactory.kt)
   - 私有投影工厂：根据当前观察者座位生成只读私有投影；
   - 牌河索引：在 `view.discards[seat]` 中寻找 tileId，构造精准匹配的 `RiverPosition(roundId, seat, discardIndex)`；
   - 鸣牌识别：若牌被其他玩家鸣走，自动转为 `EXPOSED_MELD`，清除牌河位置；
   - 手牌定位：本人手牌为 `OWN_HAND`，对手手牌为 `OPPONENT_HAND`（不透露对手手牌排序与摸牌标记）；
   - 宝牌指示牌为 `EXPOSED_INDICATOR`；
   - 牌山、未公开王牌、未实际发牌统一为 `NOT_VISIBLE`；
   - 计算 `canApply`、`disabledReason` 及仅限本人手牌且未标记的 `selectableTileIds`。
6. [`engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`](../../engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt)
   - 实现 `submitTileMark(seat: Int, request: TileMarkRequest): ActionResult`：
     - 在写锁与动作守卫内原子准备单局扣费候选与 Session 标记组安装候选；
     - 生成座位级作用域 `entryId = "mark-$seat-${request.requestId}"` 与 `groupId = "mark-$seat-${request.requestId}"`；
     - 原子扣除 10 点出千点数，记录 `CheatLedger` 条目与 `CheatPointsChanged` 事件；
     - 零出牌副作用：不移动 136 张物理牌位置，不推进出牌回合，不修改麻将积分；
     - 订阅者先观察到已安装标记组与新出千余额，再发布变更；
   - 物理发牌对接：在 `executeDealWithPreparation`、`nextRound()`、`handleAction(GameAction.StartStacking)` 中统一登记物理发牌局号；
   - 增加 `close()` 级联安装 `Close` 会话变更。
7. [`engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`](../../engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)
   - 实现 `submitTileMark(seat: Int, request: TileMarkRequest, now: Long): MutationResult`；
   - 完整错误检查：`TILE_MARK_DISABLED`、`HOST_PAUSED`、`INVALID_SEAT`、`WRONG_PHASE`、`DECISION_MISMATCH`、`STALE_CONTROL_EPOCH`、`ACTION_NOT_AVAILABLE`、`NO_ACTIVE_DECISION`、`DEADLINE_EXPIRED`、`NOT_YOUR_TURN`、`TILE_MARK_RIICHI_LOCKED`、`TILE_MARK_INVALID_SELECTION`、`TILE_MARK_TILE_NOT_OWNED`、`TILE_MARK_ALREADY_MARKED`、`TILE_MARK_CAPACITY_EXCEEDED`、`INSUFFICIENT_FUNDS`、`REQUEST_ID_CONFLICT`；
   - 跨 API 幂等与冲突检测：`StoredSubmission` 支持 `NormalizedTileMarkPayload`，归一化排序牌张；同一 ID 换载荷、跨 `submitChoice` 或 `submitShufflePlan` 均返回 `REQUEST_ID_CONFLICT`；重试相同请求返回原回执与 `stateChanged = false`；
   - 时钟延续：施术成功后生成新决策，调用 `decisionClock.rekey(oldKey, newKey)` 延续截止时间与储备余额；
   - `querySnapshot` 装配私有 `tileMarkView`；
   - `close()` 级联关闭协调器会话；
   - `sanitizeConfig` 复制保留 `tileMarkConfig`。

---

## 3. 测试与验证结果

### 3.1 专门落汗测试套件验证
运行 `./gradlew :engine:test --tests "com.majiang.cheat.mark.*"`：
- `TileMarkAuthorityContractTest`: 16/16 全部通过
- `TileMarkModelContractTest`: 9/9 全部通过
- `TileMarkStateContractTest`: 10/10 全部通过
- `TileMarkRequestIsolationTest`: 1/1 全部通过
- **落汗测试总计**: 36/36 全部通过 (100%)

### 3.2 全量 Engine 测试套件验证
运行 `./gradlew :engine:test`：
- **测试用例总数**: 376 项
- **通过数量**: 376 项 (100%)
- **失败数量**: 0 项
- **跳过数量**: 0 项
- **测试套件总数**: 52 套件
- **耗时**: ~2.7 秒
- **物理守恒律与原有功能回归**: 零退化，所有立直、算分、码牌、千术、连接控制与时钟测试无缝通过。

---

## 4. 关键设计实现说明
1. **时钟延续与 Deadline 保留**:
   施术成功后不调用 complete/cancel，而是通过 `decisionClock.rekey(timing.key, newKey)` 原子更新决策键，使得当前出牌回合的时限（包含 reserve 剩余）无缝延续至新决策，防止玩家利用落汗重置思考时间或逃避超时。
2. **跨 API 幂等与载荷规范化**:
   客户端 `TileMarkRequest` 的 `tileIds` 统一按升序规范化存储。无论重试时牌张顺序如何颠倒，均可识别为相同请求；同一 requestId 若携带不同牌张、不同代次/局号，或被用于普通出牌/洗牌请求，均被判定为 `REQUEST_ID_CONFLICT`。
3. **座位隔离与 Ledger 唯一性**:
   `CheatLedger` 的 `entryId` 与标记组的 `groupId` 均携带席位编号 (`mark-$seat-${request.requestId}`)，确保不同玩家并发使用相同客户端请求 ID 时互不干扰，完全隔离。
