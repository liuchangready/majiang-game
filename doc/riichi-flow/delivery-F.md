# 交付文档 - Task F: 状态机人类席位判定与自动化流转缺陷修复 (Human Seat Contract Fix)

## 1. 概述与任务定位

- **任务编号**：Task F
- **开发代号**：Antigravity
- **协作模式**：Codex 负责设计、测试编写、构建与集成审查；Antigravity 负责生产实现。
- **任务目标**：修复 Task R 交付后暴露的 `GameStateMachine.isHumanPlayer` 缺陷，收窄人类席位判定边界，消除在手动驱动与未配置 AI 控制器场景下误触发人类自动化摸切的回归问题，恢复契约一致性。

---

## 2. 缺陷根因分析

在 Task R 中，`isHumanPlayer(player)` 实现为：
```kotlin
fun isHumanPlayer(player: Int): Boolean {
    return player == humanPlayerIndex || !aiPlayers.containsKey(player)
}
```
**缺陷影响**：
1. **未配置 AI 控制器不等于人类玩家**：状态机构造函数默认 `humanPlayerIndex = 0`，但在许多单元测试与手动单步驱动的场景（如 `TestTableBuilder` 构造的 fixture）中，并未调用 `initializeMatch()`，导致 `aiPlayers` 字典为空。此时 `!aiPlayers.containsKey(player)` 对 1、2、3 号座位均恒为 `true`。
2. **自动化摸切意外泛化**：当 1、2、3 号座位宣告立直后摸牌时，由于被误判为人类玩家，状态机自动触发了 `checkAndExecuteAutoRiichiDiscard`，打出了本应保留给外部手动断言或单步驱动的摸牌。
3. **回归测试受损**：
   - 破坏了 Codex 新增的回归测试 `RiichiFlowContractTest.missing AI controller does not turn another seat into the human player`；
   - 破坏了既有协议测试 `EngineProtocolContractTest.passing ron in riichi causes permanent furiten surviving the next draw`（1 号位立直摸牌被自动摸切，导致等待 `drawnTile != null` 的循环超时）。

---

## 3. 修改文件与契约收窄

严格遵循任务边界，仅修改 Task F 所属文件：

### 3.1 [`core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameStateMachine.kt)

- **移除回退逻辑**：将 `isHumanPlayer` 修改为严格的有效席位精确匹配：
  ```kotlin
  /** 判定指定座位是否为人类玩家（仅当座位有效且精确匹配 humanPlayerIndex，不根据缺失 AI 控制器回退推断） */
  private fun isHumanPlayer(player: Int): Boolean {
      return player in 0..3 && player == humanPlayerIndex
  }
  ```
- **访问权限收窄**：调整为 `private`，作为状态机内部策略，避免外部非法依赖或状态泄露。
- **行为契约保证**：
  - 默认状态：构造函数默认 `humanPlayerIndex = 0`，仅 0 号位为人类，1/2/3 号位绝不自动变为人类；
  - 显式非 0 号人类：通过 `initializeMatch(humanIndex)` 显式传入非 0 号席位时，对应席位精确判定为人类；
  - 无人类 / 全 AI 模式：支持 `initializeMatch(-1)`，此时所有 0..3 席位均判定为非人类，绝不触发人类摸切/暂停；
  - 手动模拟安全：未调用 `initializeMatch` 的手动驱动 fixture 下，非 0 号座位保留手动控制权，不会因未配置 AI 控制器而被误判定为人类。

### 3.2 文档更新与显式替代说明

- 修正了 [`doc/riichi-flow/delivery-R.md`](file:///D:/majiang/doc/riichi-flow/delivery-R.md) 中关于「判定覆盖未托管给 AI 的所有真人座位」的陈旧描述。
- 本文档（`delivery-F.md`）明确废除并取代 `delivery-R.md` 中关于人类席位推断的表述：**严禁从缺失 AI 控制器推断人类玩家，统一以有效席位严格匹配 `humanPlayerIndex` 为准**。

---

## 4. 自动流转审查 (Automatic Flow Calls Review)

完成修改后，对 `GameStateMachine.kt` 中所有调用 `isHumanPlayer` 的分支进行了完整审查：

1. **摸牌推进 (`DrawTile` / `executeDrawInternal`)**：
   - 仅对匹配 `isHumanPlayer(player)` 的真实人类席位设置 `CHEAT_WINDOW` 阶段并触发 `checkAndExecuteAutoRiichiDiscard`；
   - 对非人类席位（AI 或手动驱动席位），阶段置为 `DISCARD`，在 `autoPlayAi == true` 时交由 `triggerAiTurnInternal`（若无 AI 控制器则安全等待外部操作）。
2. **暗杠后岭上摸牌 (`DeclareKan` CLOSED_KAN)**：
   - 同步执行人类立直暂停/摸切判定或 AI 自动流程，非人类未托管席位不再误触发摸切。
3. **开局发牌 (`StartStacking`)**：
   - 仅当庄家为人类时进入 `CHEAT_WINDOW`，否则进入 `DISCARD`，与庄家起手出牌权逻辑一致。
4. **响应窗口与裁决 (`openResponseWindow` / `resolveAndExecuteResponseWindow`)**：
   - 响应窗口仅对 `!isHumanPlayer(seat)` 的 AI 席位自动收集动作，人类席位严格保留等待用户输入。

---

## 5. 未验证项说明 (Delegated to Codex)

依据无头桥接协议要求，当前会话未执行终端命令、未修改 Gradle、未执行测试：
1. **测试回归验收**：由 Codex 执行完整 213 项测试验证，重点确认：
   - `RiichiFlowContractTest.missing AI controller does not turn another seat into the human player` 成功通过；
   - `EngineProtocolContractTest.passing ron in riichi causes permanent furiten surviving the next draw` 成功通过；
   - 其余全部 211 项测试（包含立直流程、暗杠契约、UI 渲染、链式暗杠等）持续保持通过。
2. **跨平台构建**：由 Codex 执行桌面端 (`:lwjgl3`) 与 Android 端 (`:android`) 构建验证。
