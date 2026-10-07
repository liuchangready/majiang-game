# 任务 ANTI-CLOCK 交付报告：紧凑可见玩家倒计时 (free+reserve)

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task ANTI-CLOCK 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责设计、测试、构建与检视；Antigravity 负责生产代码实现（严格使用内置文件读写工具，不执行外部 Shell/终端命令）
- **唯一写入范围**：
  - `core/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt`
  - `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
  - `doc/v3-migration/delivery-ANTI-CLOCK.md`

---

## 1. 交付变更清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt` | 修改 | `displayText` 呈现格式改为紧凑的 `free+reserve` 整秒形式：<br>1. 活动决策倒计时：正毫秒值向上取整（ceil）且永不为负，例如开始 `3+20`、1s后 `2+20`、3s后 `0+20`、4s后 `0+19`、超时 `0+0`；<br>2. 空闲态（无当前活动免费决策时间）：展示 `0+20`（或当前实际储备秒数），`hasActiveTiming=false`；<br>3. 时钟为 null 时：展示 `--+--`；<br>4. 完整保留 `freeSeconds: Long`、`reserveSeconds: Float` API 算术与既有权威 deadline/serverNow/local elapsed 估算逻辑。 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | `renderPlayerStatusBox` 状态盒呈现优化：<br>1. 保持原左下角 175x82 坐标区域不变，杜绝与手牌交互区干涉；<br>2. 紧凑倒计时作为主视觉大字，采用 scale 1.8 绘制在 y+48 坐标，在顶部席位标签 (y+70) 与底部点数 (y+20) 之间留出充足间隙；<br>3. 颜色映射：活跃时采用明亮亮黄白色 `(1.0, 0.95, 0.40)`；免费耗尽且储备不足 5 秒（`isUrgent`）为红色；空闲态为柔和灰绿色；断线展示大型 `--+--`；<br>4. 状态标签精简：暂停状态移至顶部小标签 `P[seat] [PAUSED]`，居中保持相同的大字冻结倒计时；断线顶部展示 `P[seat] [OFFLINE]`；去除横跨矩形的长前缀文本；<br>5. 完整保留现有状态框边框紧急红色告警逻辑以及 `finally` 恢复字体 scale 和 color 机制；时钟纯展示，暂停机制 `elapsed = 0` 保持不变。 |
| `doc/v3-migration/delivery-ANTI-CLOCK.md` | 新增 | 本交付报告 |

---

## 2. 契约满足与验收对照

1. **格式精确度**：
   - 活动开始：`3+20`
   - 1秒后：`2+20`
   - 3秒后：`0+20`
   - 4秒后：`0+19`
   - 过期归零：`0+0`
   - 空闲态：`0+20`（或实际储备，如 `0+17`）
   - Null 时钟：`--+--`
2. **纯展示契约**：
   - 未改动任何引擎状态机、网络协议、测试文件或构建脚本；
   - 客户端无任何倒计时权威伪造或提前超时提交。
3. **视觉布局**：
   - 状态盒边界保留为 `Rectangle(35f, 40f, 175f, 82f)`；
   - 字体缩放使用 scale 1.8，字号显著大于原 0.75 辅助字；
   - 顶行 y+70、主倒计时 y+48、底行 y+20 垂直层次清晰。

交付完毕，请 Codex 检视并执行验收回归。
