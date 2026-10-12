# 袖里藏花表现层断线入口禁用修复 (SB-E3FIX2) 交付报告 (DELIVERY-ANTI-E3FIX2.md)

- **任务标识**：SB-E3FIX2
- **责任人**：Anti (agy)
- **审查人**：Codex
- **日期**：2026-10-12
- **状态**：已完成单点精确修复并通过所有全量测试与三次真实GL冒烟 (ready_for_review)

---

## 1. 修复目标与变更说明

依据 `TASK-ANTI-E3FIX2.md` 要求，本次为单点精确补齐渲染与输入的一致性门禁：

- **问题根因**：
  `SleeveBloomPanel.render` 中入口按钮绘制的 `canOpen` 原先直接读取快照 `bloom.canApply`，而忽略了 `SleeveBloomChoiceModel.canActivate` 中追踪的连接状态（`isConnected`）、控制权角色（`role == "HUMAN"`）、点数余额（`points >= bloom.cost`）及未决 offer 门禁。当发生真实网络断开（`session.close()`）时，模型已正确返回 `canActivate = false`，但入口按钮仍因旧快照中的 `bloom.canApply == true` 呈现亮橙色（RGB 216, 114, 38），与输入判定和模型状态不一致。

- **生产代码落点**：
  1. **`presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt`**：
     将入口按钮 `canOpen` 判定由 `bloom.canApply` 调整为 `model.canActivate`，同时保留所有既有表现层门控（`!isDiscardPlaybackBusy`、`!isPaused`、`!session.isPending`、`!isSubmitting`、`!riichiSelecting`、`!meldSelecting`、`!tileMarkSelecting`）：
     ```kotlin
     // 1. 绘制常驻入口按钮（二选一期间隐藏）
     if (!model.isChoosing) {
         val canOpen = model.canActivate &&
             !isDiscardPlaybackBusy &&
             !isPaused &&
             !session.isPending &&
             !isSubmitting &&
             !riichiSelecting &&
             !meldSelecting &&
             !tileMarkSelecting
     ```
  2. **`presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt`**：
     在触摸拦截中同样将 `canOpenBloom` 对齐为 `sleeveBloomPanel.model.canActivate && ...`，使渲染条件与输入判定完全镜像一致。

- **约束遵守**：
  未修改任何测试代码、构建脚本、其他模块源码或模型定义；无 Git commit，无另启 Codex。

---

## 2. 验证结果与执行数据

### 2.1 全量单元测试 (`.\gradlew.bat test --rerun-tasks --no-build-cache --console=plain`)
全仓 5 个模块共 **1110** 项测试全部重新执行，**100% 通过**，无失败、无错误、无跳过：

- **engine**：407 tests, 0 failures, 0 errors, 0 skipped
- **protocol**：158 tests, 0 failures, 0 errors, 0 skipped
- **server**：141 tests, 0 failures, 0 errors, 0 skipped
- **client**：136 tests, 0 failures, 0 errors, 0 skipped
- **presentation**：268 tests, 0 failures, 0 errors, 0 skipped
- **总计**：**1110 项全部通过**

### 2.2 跨平台构建 (`.\gradlew.bat :lwjgl3:classes :android:assembleDebug --console=plain`)
- `:lwjgl3:classes`：BUILD SUCCESSFUL
- `:android:assembleDebug`：BUILD SUCCESSFUL

### 2.3 真实 GL 冒烟测试（三次全部通过）

1. **新增断线场景 GL 冒烟 (`-PsmokeDisconnect=true`)**：
   ```powershell
   .\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeDisconnect=true --console=plain
   ```
   - 结果：`SLEEVE_BLOOM_DISCONNECT_GL_PASS fee=0 frames=432`
   - 像素断言验证：真实断线后，入口按钮像素采样严格满足禁用色 `max(r,g,b) < 160 && max - min < 40`，生成截图 `build/sleeve-bloom/screenshots-1280-disconnect/disconnected-entry.png`。

2. **标准 1280 视口 GL 冒烟**：
   ```powershell
   .\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke --console=plain
   ```
   - 结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=561`
   - 包含通过：落汗模态开启期间藏花入口禁用灰色像素采样、藏花二选一期间落汗入口禁用灰色像素采样、两次二选一交互完整闭环、额度与 deadline 权威核验、返回菜单无泄漏。

3. **标准 960 视口 GL 冒烟**：
   ```powershell
   .\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeWidth=960 --console=plain
   ```
   - 结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=564`
   - 截图保存在 `build/sleeve-bloom/screenshots-960`，卡片与来源说明均正常显示。

---

## 3. 交付文件清单

| 文件路径 | 说明 |
| :--- | :--- |
| `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt` | `canOpen` 接入 `model.canActivate`，反映连接与控制门限 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt` | `canOpenBloom` 对齐使用 `sleeveBloomPanel.model.canActivate` |
| `doc/sleeve-bloom/DELIVERY-ANTI-E3FIX2.md` | 本交付报告 |
| `doc/sleeve-bloom/CALLBACK-ANTI-E3FIX2.json` | 机器可读回调结构数据 |

---

## 4. 结论

断线入口置灰缺陷已彻底修复并通过真实 GL 像素断言与全量 1110 项测试验证。提交当前回调请求 Codex 独立复审。
