# 交付记录：任务 F（立直选牌视觉验收修正）

- **任务名称**：立直选牌与待牌预览视觉验收修正 (Task F: Visual Acceptance Corrections)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试编写、构建、OpenGL 截图验收）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界约束，任务 F 仅修改指定生产源文件并新增本文档：

1. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
   - 修复摸牌与选中标语重叠问题：当处于立直选牌模式且选中的正是摸牌时，抑制绘制 `[DRAW]`，仅保留明确无二义性的 `[SELECTED]` 标语，彻底避免双重标语在相同 y 坐标重叠绘制，同时避免垂直堆叠与行动按钮冲突；
   - 修复待牌面板左侧标题越界问题：将左侧标题在所有情况下统一固定为精简的 `"DISCARD"`，不再使用会越过 x=360 分隔线侵入右侧 `"PREVIEW WAITS"` 的 `"DISCARD (PREVIEW)"`，悬停预览状态交由右侧标题和颜色明确表达；
   - 完善 Android BACK 键生命周期安全捕获：在 `show()` 中保存原有 catch 状态并设置 `setCatchKey(Input.Keys.BACK, true)`，确保移动端系统 BACK 按键能准确分发至 `keyDown` 执行无副作用取消；并在 `hide()` 与 `dispose()` 中恢复先前的 catch 状态与清理 `inputProcessor`。
2. `doc/riichi-choice/delivery-F.md`（本文档）

---

## 2. 视觉修复与生命周期细节

### 2.1 摸牌与选中标语重叠消除 (`[DRAW]` / `[SELECTED]`)
- **问题分析**：在实际 OpenGL 截图 `build/riichi-choice/discard-6s-wait-2s-5s.png` 中，当玩家选中的立直弃牌恰为本巡摸牌时，`renderPlayerHands` 绘制了 `[DRAW]`，随后 `renderRiichiSelectionOverlay` 在相同的 `y = rect.y + rect.height + 18f` 坐标绘制了 `[SELECTED]`，两个带方括号的字串字形发生完全重叠。若采取垂直堆叠方案，上层标签会侵入 y=145..189 的行动按钮区域（如 CONFIRM RIICHI / CANCEL）产生更大冲突。
- **修复方案**：
  - 在 `renderPlayerHands` 渲染手牌附属标记时增加条件判定：
    ```kotlin
    val isSelectedInRiichi = riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == drawnSlot.tile
    if (!isSelectedInRiichi) {
        font.draw(batch, "[DRAW]", drawnSlot.rect.x + 2f, drawnSlot.rect.y + drawnSlot.rect.height + 18f)
    }
    ```
  - 当摸牌被选中时，跳过 `[DRAW]` 绘制；由 `renderRiichiSelectionOverlay` 统一定点绘制唯一的 `[SELECTED]` 标语。
  - 当选牌模式未开启、未选择牌或选择了其他手牌时，`[DRAW]` 正常显示在摸牌上方，互不干扰；
  - 无论理牌开关为 ON 还是 OFF，无论使用何种牌面皮肤，均严格遵循此无重叠规则。

### 2.2 待牌预览面板标题越界消除 (`DISCARD`)
- **问题分析**：原实现中，当鼠标悬停在合法弃牌临时预览待牌时，左侧弃牌区标题使用了 `"DISCARD (PREVIEW)"`。该字符串自 `x = 250f` 开始绘制，长度超出 `x = 360f` 竖向分隔线，侵入右侧起始于 `x = 380f` 的 `"PREVIEW WAITS (N):"` 标题，造成严重的文本重叠。
- **修复方案**：
  - 左侧标题统一固定为短标题 `"DISCARD"`，绘制范围稳定在 `x = 250f..310f`，与 `x = 360f` 分隔线保持充足安全间距；
  - 悬停预览状态已由右侧标题 `"PREVIEW WAITS ($waitCount):"` 及其亮青色字体（`Color(0.4f, 0.9f, 1.0f, 1f)`）充分展现，两栏标题职责清晰、零交叉重叠。

### 2.3 Android BACK 键生命周期安全管理 (`setCatchKey`)
- **问题分析**：在 Android 平台下，若未对 libGDX 显式声明捕获系统 BACK 键，按下系统返回键会触发默认的 Activity 销毁/暂停逻辑，无法将键盘事件传递至 `InputProcessor.keyDown(Input.Keys.BACK)`，导致选牌模式无法通过 BACK 键平滑取消。同时，若无条件直接设置且不恢复，可能破坏外部宿主或切换至其他屏幕时的按键预期。
- **修复方案**：
  - 在 `GameScreen` 中增加私有状态 `private var previousCatchBack = false`；
  - 在 `show()` 时通过 `Gdx.input?.let` 安全获取先前的 catch 状态：
    ```kotlin
    previousCatchBack = input.isCatchKey(Input.Keys.BACK)
    input.setCatchKey(Input.Keys.BACK, true)
    ```
  - 在 `hide()` 与 `dispose()` 退出场景时恢复原有状态：
    ```kotlin
    Gdx.input?.let { input ->
        input.setCatchKey(Input.Keys.BACK, previousCatchBack)
        if (input.inputProcessor == this) {
            input.inputProcessor = null
        }
    }
    ```
  - 配合既有 `keyDown`：
    ```kotlin
    if (keycode == Input.Keys.ESCAPE || keycode == Input.Keys.BACK) {
        if (riichiSelectionModel.isSelecting) {
            riichiSelectionModel.cancel()
            return true
        }
        ...
    }
    ```
    精准实现：选牌模式下按 BACK 仅退出选牌模式、返回普通手动回合、不打牌不扣点；非选牌模式下按 BACK 返回主菜单。

---

## 3. 并行与权限边界遵循说明

1. **严格所有权与文件边界**：
   - 仅修改 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`；
   - 未修改 `RiichiPreviewProvider.kt`、`RiichiSelectionModel.kt`、`RiichiRuleEngine.kt` 或任何其他任务文件；
   - 未修改测试文件、Gradle 脚本或依赖配置。
2. **工具权限契约**：
   - 全程仅使用内置文件查看与编辑工具；
   - 未执行任何 `run_command`、系统终端或 MCP 工具；
   - 不声明视觉验收结论，交由 Codex 重新生成并校验截图。

---

## 4. 移交 Codex 验证项说明 (Delegated to Codex)

依据无头开发模式与任务契约约定，由 Codex 统一执行后续验证：
1. **截图生成与比对**：
   - 重新生成 `build/riichi-choice/discard-6s-wait-2s-5s.png`，验证摸牌被选中时仅显示唯一的 `[SELECTED]` 标语，无 `[DRAW]` 重叠；
   - 验证理牌开关 ON/OFF 及不同皮肤下均无标签冲突；
   - 验证悬停预览时左侧 `"DISCARD"` 与右侧 `"PREVIEW WAITS"` 在 `x = 360f` 分隔线两侧完全独立无重叠。
2. **测试与构建套件**：
   - 运行 233 项及新增回归测试（`./gradlew test`）；
   - 验证 Android 与 Desktop 编译无报错。
