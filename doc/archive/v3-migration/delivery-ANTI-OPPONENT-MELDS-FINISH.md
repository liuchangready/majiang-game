# 任务 ANTI-OPPONENT-MELDS-FINISH 交付报告：四家副露展示收尾

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-OPPONENT-MELDS-FINISH 生产代码接续与收尾)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责设计、测试用例编写、构建实测与代码审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不调用终端、命令行或其它代理）
- **依据契约**：
  - [TASK-ANTI-OPPONENT-MELDS.md](TASK-ANTI-OPPONENT-MELDS.md)
  - [OpponentMeldDisplayAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/render/OpponentMeldDisplayAcceptanceTest.kt)

---

## 1. 交付范围与生产代码变更

严格约束在既定的三个生产文件与交付报告范围内，未修改任何测试源码、Gradle 配置或其他生产模块：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 1. 补齐 `displayedTableMelds()` 公开方法：委托 `MeldDisplayLayout.layoutAll(currentView)`，与只返回本人的 `displayedMelds()` 并存，修复 line 706 与测试调用处未定义符号导致的编译失败。<br>2. 顶家信息 HUD 坐标微调：将顶家（relSeat = 2）姓名与分数绘制 Y 坐标从 `590f` 移至 `580f`，处于 CALLED/ADD 子标签（`y=595f`）下方、顶家牌河（`y=530+36=566f`）上方，彻底避免横向重叠。<br>3. 保持现有 `computeHandPosition` 相对座位调用契约（调用方已传入 `relSeat`，内部不二次重映射）。 |
| `core/src/main/kotlin/com/majiang/render/MeldDisplayLayout.kt` | 保留/确认 | 保留原已实现的 `layoutAll`、`MeldDisplayGroup(ownerSeat, y)`、四家相对视点布局（左侧 x80/右侧 x1050 纵向，顶家 x322..958 y600 横向）、暗杠首尾背面、加杠最后一牌 ADD 等几何契约，返回不可变列表。 |
| `core/src/main/kotlin/com/majiang/render/MeldDisplayRenderer.kt` | 保留/确认 | 保留原已实现的渲染指令生成、自己与对手标签区分（顶家 y666、侧边 y+68、CALLED/ADD y-5）、暗杠/加杠/明副露标签文字及高亮框线。 |
| `doc/v3-migration/delivery-ANTI-OPPONENT-MELDS-FINISH.md` | 新增 | 本交付报告。 |

---

## 2. 关键补齐与调整细节

### 2.1 补齐 `GameScreen.displayedTableMelds()`
- **现象**：编译审查日志指出 `GameScreen` 第 706 行调用了 `displayedTableMelds()`，但该方法此前未被声明。
- **改动**：在 `displayedMelds()`（保持本人副露）旁新增：
  ```kotlin
  /**
   * 获取当前观察者本人的已完成副露展示列表（纯视图驱动，不读取他家暗牌）。
   */
  fun displayedMelds(): List<MeldDisplayGroup> = MeldDisplayLayout.layout(currentView)

  /**
   * 获取牌桌全场四家的已完成副露展示列表（以当前观察者为相对视点）。
   */
  fun displayedTableMelds(): List<MeldDisplayGroup> = MeldDisplayLayout.layoutAll(currentView)
  ```
- **效果**：`render` 第 706 行直接取得全场四家相对布局列表提交至 `MeldDisplayRenderer.append`，同时满足 `OpponentMeldDisplayAcceptanceTest` 全部 6 个用例的直接查询契约。

### 2.2 顶家 HUD 避免覆盖副露子标签 (y590 -> y580)
- **现象**：对家副露 tiles 位于 `y=600f`，其鸣牌与加杠子标签（`CALLED` / `ADD`）位于 `y - 5f = 595f`。原有顶家名字和分数位于 `y=590f`，在 `x=500..775` 区域产生叠字。
- **改动**：将 `renderTableInfo` 中 `rel == 2`（对家）的绘制 Y 坐标从 `590f` 调整为 `580f`：
  ```kotlin
  2 -> {
      // 对家 (顶部，放置于 y580 避免副露与顶部 HUD 遮挡)
      font.draw(batch, "$label ($wind) : ${currentView.scores[seat]} pts", 500f, 580f)
  }
  ```
- **效果**：顶家信息位于子标签 `y=595f` 之下、顶家牌河第一行 `y=530f + 36f = 566f` 之上，留有安全间距，上下互不重叠。

### 2.3 静态校验与参数防呆确认
1. `computeHandPosition`：`renderDiscardAnimation()` 调用处已经传入 `relSeat = (anim.seat - client.viewerSeat + 4) % 4`，`computeHandPosition` 内部直接使用 `when (seat % 4)`，未进行二次重映射，保持相对起点与终点正确。
2. 牌河与出牌起点：`getDiscardStart`、`getDiscardSlotPosition` 与 `renderDiscardRivers` 均统一采用相对观察者视点映射，顶家牌河基准 `y=530f` 且 `step=-38f` 向下生长。
3. 交互隔离：对手副露牌未加入 `handTileRects`，全场副露点击不会产生弃牌或错误提交。

---

## 3. 验收准备完毕

本次交付已完成静态检查与接口对齐，交由 Codex 执行验收构建、`OpponentMeldDisplayAcceptanceTest` 6 项测试及真实 GL 冒烟验证。
