# ANTI-R1 任务交付报告：编译器错误修复与宿主 AI 隔离

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity（生产开发，接续 GLM 额度耗尽后的任务）
- **任务编号**：ANTI-R1（实现 [TASK-GLM-P2.md](TASK-GLM-P2.md) 与 [TASK-GLM-H3.md](TASK-GLM-H3.md)）
- **验收方**：Codex（测试设计、编译构建与端到端验证）

---

## 1. 所有权与写入范围说明

严格遵守 ANTI-R1 契约范围，未触碰 Gradle、测试文件、UI、GameClient/sync、网络传输或 server 模块：

| 变更文件 | 所属范围 | 变更说明 |
|---|---|---|
| `client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt` | `client/.../network/protocol/**` | 修复泛型枚举解析 `T.entries` 编译错误，采用 `enumValues<T>()` 保持严格白名单反序列化 |
| `engine/src/main/kotlin/com/majiang/host/TableHost.kt` | `engine/.../host/**` | 移除 `WAITING_DEALER_CHOICE` 锁内对任意 `AIStrategy` 的同步调用，实施确定性宿主会话推进政策 |
| `doc/v3-migration/delivery-ANTI-R1.md` | `doc/v3-migration/**` | 本交付报告 |

---

## 2. P2 任务实施：ProtocolJson 泛型枚举编译修复 (TASK-GLM-P2)

### 2.1 缺陷根因
在 `ProtocolJson.kt:198-202` 中，私有内联扩展函数：
```kotlin
private inline fun <reified T : Enum<T>> JsonReader.enumField(typeName: String, field: String): T {
    val raw = stringField()
    return T.entries.firstOrNull { it.name == raw }
        ?: throw JsonParseException("Unknown $typeName $field value: $raw")
}
```
Kotlin 编译器对泛型类型参数 `reified T : Enum<T>` 不支持通过类型参数点运算符访问属性（`T.entries` 会报 `Type parameter 'T' cannot have or inherit a companion object, cannot be left hand side of dot; unresolved entries/it`）。

### 2.2 修复方案与代码变更
将 `T.entries` 替换为 Kotlin 官方合法的泛型枚举迭代 API `enumValues<T>()`：
```kotlin
private inline fun <reified T : Enum<T>> JsonReader.enumField(typeName: String, field: String): T {
    val raw = stringField()
    return enumValues<T>().firstOrNull { it.name == raw }
        ?: throw JsonParseException("Unknown $typeName $field value: $raw")
}
```

### 2.3 契约保障与全面审计
1. **严格拒绝语义不变**：若字符串不在枚举白名单中，依然抛出 `JsonParseException("Unknown $typeName $field value: $raw")`，不会捏造默认值。
2. **零代码改写 (No Codec Rewrite)**：未改动其他任何适配器结构或 DTO 字段。
3. **全包审计**：审计了 `client/src/main/kotlin/com/majiang/network/protocol/**` 下全部 3 个源文件：
   - `NetworkMessages.kt`：无泛型枚举调用，白名单常量定义正确；
   - `NetworkFrameCodec.kt`：纯流操作，无泛型反射；
   - `ProtocolJson.kt`：仅此一处 `enumField` 泛型方法，其余各适配器（`TileAdapter`、`MeldAdapter`、`GameEventAdapter`、`PlayerViewAdapter`、`ActionReceiptAdapter`）均通过此统一方法解析，编译障碍消除。

---

## 3. H3 任务实施：TableHost WAITING_DEALER_CHOICE AI 隔离与宿主策略 (TASK-GLM-H3)

### 3.1 缺陷根因
Codex 验收用例 `HostAiIsolationAcceptanceTest.dealer session choice never executes AI strategy while holding table lock` 失败在第 64 行：
- 南四局（尾庄 All Last）庄家自摸和牌后，满足和了终了条件，赛程进入 `SessionLifecycle.WAITING_DEALER_CHOICE`。
- 测试中将庄家托管/接管给慢速或卡死 AI（`StalledAi`）。
- `TableHost.tick()` 在持有串行锁 `withSerialLock` 时直接执行：
  ```kotlin
  val strategy = resolveAiStrategyForSeat(dealer)
  val chosenAction = strategy.decideAction(...)
  ```
- 导致权威线程被任意第三方 AI 阻塞（`StalledAi` 阻塞 5 秒），`host.tick()` 超时，后续所有并发查询（如 `host.querySnapshot(1)`）因无法获取串行锁而一同被卡死。

### 3.2 策略选择与架构考量
对照 `TASK-GLM-H3` 规范：
> "Either reuse bounded async scheduling with session/decision/controlEpoch validation plus bounded deterministic fallback (do not use single-round clock illegally for session choices), or explicitly define a deterministic automated session policy choosing a legal option without invoking arbitrary AI. User asks configurable AI for gameplay; session progression may use a documented host continuation/end policy."

我们选择**确定性宿主会话推进政策 (Deterministic Automated Session Policy)**，原因如下：
1. **职责分离**：`AIStrategy` 定义为单局内打牌决策（自摸/出牌/立直/副露），并未针对赛程级（`DeclareAgariYame` 终结比赛 vs `StartStacking` 连庄）设计评估模型；`BasicAIStrategy` 遇到局间选项默认返回 `null`。
2. **绝对安全与零挂起**：会话级推进不调度后台异步任务，不涉及跨局任务生命周期管理与超时取消，彻底杜绝任何 AI 阻塞宿主权威主循环或查询接口的隐患。
3. **禁止单局时钟滥用**：单局 `DecisionClock` 严禁作用于 `roundId == null` 的局间决策，确定性推进完全规避了这一非法操作。

### 3.3 实施细节 (TableHost.kt)
更新 `TableHost.tick()` 第 3 步（局间庄家决策分支）：
```kotlin
// 3. 处理局间庄家决策 (WAITING_DEALER_CHOICE) 下的自动化推进，防止全 AI 对局永久卡死 (TASK-GLM-H3)
// 契约保证：
// - 绝不在权威线程或持有桌锁期间同步调用任意 AIStrategy（防止慢/卡死 AI 阻塞权威线程与状态查询）；
// - 不将单局 DecisionClock 非法用于会话级决策；
// - 遵循宿主确定性赛程推进政策：优先选择 opt-continue 继续比赛，若不可用则选择首个合法动作（如 opt-agari-yame）；
// - 真人庄家决策保持完全独立，仅当席位为自动化 (isAutomated) 时触发；
// - 严格受控于动作节奏 (rhythmIntervalMillis) 与展示门控 (presentationBusy)，每 tick 最多执行一个动作。
if (this.coordinator.session.lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE) {
    val dealer = this.coordinator.session.currentDealer
    if (connectionControl.seatState(dealer).isAutomated) {
        val elapsed = now - lastActionTimeMillis
        if (!presentationBusy && (rhythmIntervalMillis <= 0L || elapsed !in 0 until rhythmIntervalMillis)) {
            val decision = this.coordinator.queryDecision(dealer)
            if (decision.options.isNotEmpty()) {
                val chosenAction = decision.options.firstOrNull { it.optionId == "opt-continue" }?.action
                    ?: decision.options.first().action

                val actionResult = this.coordinator.handleAction(chosenAction)
                if (actionResult is ActionResult.Accepted) {
                    val newRound = this.coordinator.activeRound
                    if (newRound != null && newRound.roundId != lastActiveRoundId) {
                        decisionClock.beginRound(newRound.roundId)
                        lastActiveRoundId = newRound.roundId
                    }
                    onStateChanged(actionResult.events)
                    lastActionTimeMillis = now
                    return@withSerialLock true
                }
            }
        }
    }
}
```

### 3.4 契约与不变量核验
- **真人决策完全保留**：仅当 `connectionControl.seatState(dealer).isAutomated` 为 true 时触发自动动作；真人保持等待，客户端可在收到 `WAITING_DEALER_CHOICE` 时自由提交 `opt-agari-yame` 或 `opt-continue`。
- **每 tick 最多一个自动动作**：执行成功后立即 `return@withSerialLock true` 并记录 `lastActionTimeMillis`。
- **节奏与暂停门控生效**：`rhythmIntervalMillis` 门控与 `presentationBusy` 正常拦截；若宿主处于本地暂停（`pause()`），`tick()` 在函数首部短路返回 false。
- **控制权收回与代次隔离**：若真人收回席位（`requestControl(false)`），`isAutomated` 翻转为 false，自动策略立即停止；由于没有派遣异步 AI 任务，不存在收回后被迟滞任务篡改的风险。
- **出千账本与资源守恒未受影响**：未修改任何 `ApplicationCoordinator`、`CheatLedger` 或结算代码，GLM-H2 修复的跨局点数保留继续保持。

---

## 4. 建议 Codex 验证命令

由 Codex 运行真实构建与验收测试：

```powershell
# 1. 编译客户端与引擎
./gradlew :client:compileKotlin :engine:compileKotlin

# 2. 验证修复用例：AI 隔离验收测试
./gradlew :engine:test --tests "com.majiang.v3.HostAiIsolationAcceptanceTest" --info

# 3. 验证宿主与协议相关全部验收测试
./gradlew :engine:test --tests "com.majiang.v3.TableHostAcceptanceTest" --info
./gradlew :engine:test --tests "com.majiang.v3.HostRhythmAcceptanceTest" --info
./gradlew :engine:test --tests "com.majiang.v3.CheatLedgerIntegrationAcceptanceTest" --info
./gradlew :client:test --tests "com.majiang.v3.NetworkCodecAcceptanceTest" --info
./gradlew :client:test --tests "com.majiang.v3.TableLifecycleAcceptanceTest" --info
```
