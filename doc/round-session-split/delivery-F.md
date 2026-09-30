# 任务交付报告：Task F - 独立 UI 提交修复 (delivery-F.md)

## 1. 任务背景与核心问题

在表现层 `GameScreen.kt` 的 `submitAction` 方法中，存在两个影响决策时效性与物理实体牌一致性的严重缺陷：
1. **同种牌模糊回退风险**：原实现中，当 `decision.options` 严格匹配失败后，对 `DiscardTile` 与 `DeclareRiichi` 采用了 `sameType` 回退匹配。由于 `sameType` 忽略了 `copyIndex` 与赤牌属性（如红五与普通五、同一牌种的不同副本），可能导致玩家意图打出的实体牌被混淆。现有的合法 `GameAction` 已经携带了具体的物理实体牌，严格精确匹配即可。
2. **决策时效性与重复提交风险**：在 `capabilities.decisions == true` 时，若 `queryDecision` 或 `submitChoice` 抛出 `Throwable` 或未找到匹配候选，原实现捕获 `Throwable` 并回退调用 `endpoint.submit(action)`。这会绕开协调器的决策时效与代次校验；若远程端点实际已处理成功但响应中出现异常，还会导致对局动作被重复执行。此外，捕获全局 `Throwable` 隐藏了严重的 JVM 错误。

---

## 2. 修复措施与实现方案

在 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` 中对 `submitAction` 进行了针对性重构：

```kotlin
    /**
     * 统一动作提交辅助函数。
     * 针对支持决策候选机制的端点 (capabilities.decisions == true)，仅通过 queryDecision / submitChoice 提交结构化选择；
     * 严格按真实实体牌与完整候选组合进行精确匹配，禁止同种牌模糊回退。
     * 若未找到匹配候选或调用中发生异常，返回明确拒绝 ActionResult.Rejected(ACTION_NOT_AVAILABLE)，绝不回退至旧 submit(action) 接口重试。
     * 仅当端点不支持决策候选 (capabilities.decisions == false，如旧测试替身) 时，才通过 submit(action) 提交。
     */
    fun submitAction(action: GameAction): ActionResult {
        if (endpoint.capabilities.decisions) {
            return try {
                val decision = endpoint.queryDecision()
                val option = decision.options.firstOrNull { it.action == action }
                    ?: return ActionResult.Rejected(com.majiang.state.RejectionReason.ACTION_NOT_AVAILABLE)
                val choice = ActionChoice(
                    requestId = UUID.randomUUID().toString(),
                    roundId = decision.roundId,
                    decisionId = decision.decisionId,
                    controlEpoch = decision.controlEpoch,
                    optionId = option.optionId
                )
                endpoint.submitChoice(choice)
            } catch (e: Exception) {
                ActionResult.Rejected(com.majiang.state.RejectionReason.ACTION_NOT_AVAILABLE)
            }
        }
        return endpoint.submit(action)
    }
```

### 关键细节说明：
1. **严格物理牌精确匹配**：移除 `sameType` 旁路匹配，直接采用 `it.action == action`，完全基于 Kotlin 数据类的结构相等性，完整比对动作类型、目标座位、物理 `Tile`（花色、数值、`copyIndex`、赤宝牌）及组合副露。
2. **决策通道独占与防御**：当 `endpoint.capabilities.decisions == true` 时，必须且仅能通过 `submitChoice` 提交，杜绝绕过决策时效机制；找不到候选时明确返回 `ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)`。
3. **异常处理边界收紧**：将 `catch (_: Throwable)` 替换为 `catch (e: Exception)`，不吞没系统级严重错误（如 `Error`）；捕获常规异常后返回明确的 `ActionResult.Rejected`，绝不重试回退到 `submit(action)`，彻底消除重复提交隐患。
4. **旧测试替身兼容**：仅当 `endpoint.capabilities.decisions == false` 时（例如旧的 `FakeGameSession`），允许执行 `endpoint.submit(action)`，保证已有轻量测试与替身资产无缝运行。

---

## 3. 修改文件范围

- **生产代码**：
  - `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`（仅修改 `submitAction` 方法与其 KDoc 注释）
- **交付文档**：
  - `doc/round-session-split/delivery-F.md`
  - `doc/round-session-split/delivery-UI.md`

未修改任何其他模块文件、测试代码或构建配置。
