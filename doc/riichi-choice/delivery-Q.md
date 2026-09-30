# 交付记录：任务 Q（Android BACK 键生命周期所有权修复）

- **任务名称**：Android BACK 键生命周期所有权与状态保护 (Task Q: BACK Lifecycle Ownership Correction)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试编写、构建、回归验证）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界约束，任务 Q 仅修改指定生产源文件并新增本文档：

1. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
   - 增加私有活跃状态与所有权标记 `private var ownsBackCatch = false`；
   - 在 `show()` 激活时仅捕获一次宿主原有的 BACK catch 状态（`if (!ownsBackCatch)`），防止重复调用 `show()` 覆盖初始保存的值；
   - 在 `hide()` 与 `dispose()` 仅在持有所有权时（`if (ownsBackCatch)`）执行一次还原，并在还原后立即清除所有权（`ownsBackCatch = false`）；
   - 避免未曾 `show()` 过的实例在 `dispose()` 时误改全局 BACK 状态；
   - 避免在 `hide()` 离开后、由新场景接管修改了 BACK 状态的情况下，旧场景延迟 `dispose()` 破坏新场景的 BACK 配置；
   - 保留既有 `input.inputProcessor == this` 身份校验，未变动任何视觉与立直选牌逻辑。
2. `doc/riichi-choice/delivery-Q.md`（本文档）

---

## 2. 问题分析与修复实现

### 2.1 缺陷场景分析

在之前的实现中，`previousCatchBack` 缺乏显式的所有权/活跃状态追踪：
1. **重复 `show()` 覆盖初始值**：若外部生命周期重复调用 `show()`，此时 `Input.Keys.BACK` 已被首次 `show()` 置为 `true`，导致 `previousCatchBack` 被误更新为 `true`，退出场景时无法还原回原本的 `false`。
2. **场景隐藏后被销毁破坏新场景配置**：场景 A 隐藏（`hide()`）后，系统切换至场景 B 并改变了 BACK catch 策略；随后场景 A 执行 `dispose()`，因无条件执行 `input.setCatchKey(Input.Keys.BACK, previousCatchBack)`，将场景 B 的配置强制覆盖。
3. **未展示场景销毁副作用**：若构建了 `GameScreen` 实例但从未调用 `show()`，直接调用 `dispose()` 会将 BACK 键重置为默认值 `false`，产生非预期的全局副作用。

### 2.2 修复方案与代码变更

- **所有权标记定义**：
  ```kotlin
  private var previousCatchBack = false
  private var ownsBackCatch = false
  ```

- **`show()` 阶段幂等捕获**：
  ```kotlin
  override fun show() {
      Gdx.input?.let { input ->
          input.inputProcessor = this
          if (!ownsBackCatch) {
              previousCatchBack = input.isCatchKey(Input.Keys.BACK)
              ownsBackCatch = true
          }
          input.setCatchKey(Input.Keys.BACK, true)
      }
  }
  ```

- **`hide()` 与 `dispose()` 阶段有条件释放**：
  ```kotlin
  override fun hide() {
      riichiSelectionModel.cancel()
      sessionSubscription?.close()
      sessionSubscription = null
      Gdx.input?.let { input ->
          if (ownsBackCatch) {
              input.setCatchKey(Input.Keys.BACK, previousCatchBack)
              ownsBackCatch = false
          }
          if (input.inputProcessor == this) {
              input.inputProcessor = null
          }
      }
  }

  override fun dispose() {
      riichiSelectionModel.cancel()
      sessionSubscription?.close()
      sessionSubscription = null
      session.close()
      tileSkin.dispose()
      Gdx.input?.let { input ->
          if (ownsBackCatch) {
              input.setCatchKey(Input.Keys.BACK, previousCatchBack)
              ownsBackCatch = false
          }
          if (input.inputProcessor == this) {
              input.inputProcessor = null
          }
      }
  }
  ```

---

## 3. 边界与权限遵循说明

1. **生产所有权**：仅修改 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`；未修改 Task P、L、F 产物，未修改测试用例或 Gradle 配置。
2. **工具权限契约**：全流程仅使用原生内置文件查看与编辑工具，零 terminal / run_command / shell 调用。
3. **移交验收**：交由 Codex 执行 `RiichiChoiceScreenContractTest.android back catch setting is restored when screen leaves` 及全量测试回归。
