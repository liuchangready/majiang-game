# 任务 ANTI-END-TENPAI-OFFLINE 交付报告：断线终局本地明细交互门控修复 (GameScreen)

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-END-TENPAI-OFFLINE 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责设计、测试与实测审查；Antigravity 负责生产代码定点修改（严格遵循内置文件工具，不调用终端或命令，不改动测试源码与 Gradle）。
- **任务目标**：修复 `EndTenpaiScreenAcceptanceTest.disconnected final result still allows navigating local round details` 测试失败（先进入总成绩 -> 断线 -> 本局明细 -> 再点查看总成绩返回）。

---

## 1. 唯一改动范围与交付清单

严格遵循契约，仅修改生产代码一处门控条件及对应注释，并输出交付文档：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 将 `touchDown` 中的断线输入拦截条件更新为：`!(showSettlement && isMatchFinished)` 时放行本地结算弹窗交互（分页、切换标签及点击 MATCH ENDED 返回总成绩面板）。更新对应注释。 |
| `doc/v3-migration/delivery-ANTI-END-TENPAI-OFFLINE.md` | 新增 | 本交付报告 |

---

## 2. 缺陷根因与代码改动

### 2.1 缺陷根因

在整场终局（`isMatchFinished == true`）且客户端与服务端断开连接（`!client.isConnected && !isLoading`）的场景下：
1. 用户首先在总成绩面板点击“查看本局明细”，此时设置 `showFinalResult = false; showSettlement = true`。
2. 当用户在结算弹窗中浏览明细并试图点击“MATCH ENDED / 查看总成绩”按钮（`btnNextRoundRect`）返回总成绩面板时，输入事件在到达结算弹窗处理逻辑（`if (showSettlement)`）前，被断线安全门控拦截：
   ```kotlin
   if (!client.isConnected && !isLoading) {
       return true
   }
   ```
   导致点击被直接吞掉，无法切回 `showFinalResult`，测试断言失败。

### 2.2 修复方案与安全性分析

- **条件变更**：
  将拦截条件修改为：
  ```kotlin
  // 若处于断线态且非加载中，禁用后续牌桌内交互输入，仅允许点击返回主菜单（以及总成绩模态与终局本地明细模态导航）
  if (!client.isConnected && !isLoading && !(showSettlement && isMatchFinished)) {
      return true
  }
  ```
- **安全性保证**：
  放行 `showSettlement && isMatchFinished` 时，仅影响本地结算弹窗内部的纯视图/模态逻辑：
  1. **AI/托管接管**：`btnTakeoverRect` 要求 `!isMatchFinished && !showSettlement`，依然无法触发。
  2. **流局连庄推进/终局判定**：`canHumanAgariYame()` 显式包含 `!isMatchFinished`，不会触发连庄选择提交。
  3. **新局推进**：非终局分支（`else` 中调用 `client.nextRound()`）在 `isMatchFinished == true` 时不会进入；此时仅命中 `else if (isMatchFinished)` 分支，该分支仅执行纯本地 UI 状态切换：
     ```kotlin
     showSettlement = false
     showFinalResult = true
     riichiSelectionModel.cancel()
     meldSelectionModel.cancel()
     return true
     ```
     完全不发起任何网络请求或游戏状态推进，完全符合离线断网下安全浏览本地记录的原则。

---

## 3. 完成情况

- [x] 完成 `GameScreen.kt` 约 3160 行处的定点编辑与注释更新；
- [x] 未改动任何其他代码或测试，未修改 Gradle 配置；
- [x] 完成并归档交付报告 `delivery-ANTI-END-TENPAI-OFFLINE.md`。
