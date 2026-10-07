# 任务 ANTI-SHUFFLE-D 交付报告：洗牌千术准备界面与产品接线

- **交付日期**：2026-10-02
- **执行角色**：Antigravity (Task ANTI-SHUFFLE-D 生产代码实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责测试设计、构建与审查；Antigravity 负责生产代码实现（严格使用内置文件读/写/改工具，不使用命令行或外部终端执行）
- **契约基准**：
  - [SHUFFLE-CONTRACT.md](SHUFFLE-CONTRACT.md)
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  - [STATUS.md](STATUS.md)

---

## 1. 唯一所有权与写入范围

严格遵守任务所有权约束：
- 仅写入 `core/src/main/**` 生产代码以及本文档 `doc/v3-migration/delivery-ANTI-SHUFFLE-D.md`；
- 未修改任何 `engine`、`client`、`server`、`test`、`build`、`assets` 或脚本文件；
- 遵循 A/B/C 冻结契约接口进行面向契约编程。

### 变更文件清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationModel.kt` | 新增 | 洗牌千术纯表现层状态模型：草稿选择、费用测算、点数预览、预算超支禁用、多选混合倾向与摸牌提示、单局生命周期重置与重连恢复 |
| `core/src/main/kotlin/com/majiang/presentation/ShufflePreparationPanel.kt` | 新增 | 洗牌千术准备模态展示面板：10s独立倒计时、偷天换日与瞒天过海各6项多选、预算与锁定状态、模态输入守卫、中文字形按需生成与安全释放 |
| `core/src/main/kotlin/com/majiang/presentation/GameScreenClient.kt` | 修改 | 新增 `submitShufflePlan(request: ShufflePlanRequest): CompletableFuture<ActionReceipt>` 默认方法，默认返回 `UNSUPPORTED_ACTION` 拒绝 |
| `core/src/main/kotlin/com/majiang/presentation/V3GameClientAdapter.kt` | 修改 | 实现 `submitShufflePlan`，代理至 `client.submitShufflePlan(request).deliveredOnDispatch()` 并在展示线程派发回执 |
| `core/src/main/kotlin/com/majiang/MahjongGame.kt` | 修改 | `createLocalHost` 默认参数启用 `MatchConfig(shuffleConfig = ShuffleConfig(enabled = true))`，赛制风场保持 `[1, 2]`，保留旧无参 Screen 兼容语义 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 最小化接入：挂载 `shufflePreparationPanel`、快照更新、模态渲染、输入穿透阻断、隐藏准备期普通 3+20 倒计时及开局按钮、安全销毁 |
| `doc/v3-migration/delivery-ANTI-SHUFFLE-D.md` | 新增 | 本交付报告 |

---

## 2. 核心架构与设计实现

### 2.1 纯表现层模型 (ShufflePreparationModel)
- **解耦设计**：完全不依赖 libGDX 图形与权威状态机，可直接在纯 JVM / 无头测试中实例化并断言。
- **草稿选择与费用**：
  - 偷天换日（影响起手配牌）：非空目标集耗费 40 点，清空为 0 点；
  - 瞒天过海（影响前 8 次顺位预计摸牌）：非空目标集耗费 20 点，清空为 0 点；
  - 总消耗实时计算（0、20、40、60 点）；
  - 允许在点数不足（超支）时自由勾选编辑草稿，但实时禁用确认按钮并输出 `"点数不足"` 禁用原因。
- **提示文案驱动**：
  - 多选混合倾向：任一区域目标数 > 1 时，触发展示 `"※ 多选为混合倾向，不保证同时成牌"`；
  - 顺位摸牌改变：瞒天过海目标数 > 0 时，触发展示 `"※ 吃碰杠会改变预期摸牌"`。
- **单局与重连恢复生命周期**：
  - 同局快照更新（`roundId == prep.roundId`）：保持用户正在勾选的草稿，不因快照心跳重置；
  - 新局快照更新（`roundId != prep.roundId`）：自动清空草稿与提交状态；
  - 权威确认恢复：若权威快照反映 `prep.confirmed == true`，自动恢复权威已锁定的 `prep.selection` 并置为锁定态；
  - 超时防误扣：超时未确认进入 `GENERATING` 时自动锁定，绝不把未确认草稿自动代扣提交。

### 2.2 准备模态面板 (ShufflePreparationPanel)
- **1280x720 虚拟视口与包围盒**：
  - 居中弹窗：`modalRect = Rectangle(180f, 90f, 920f, 540f)`；
  - 独立大倒计时：`countdownBoxRect = Rectangle(770f, 560f, 110f, 48f)`，剩余 <= 3s 醒目标红；
  - 偷天换日区域：`sectionInitialRect = Rectangle(200f, 345f, 880f, 185f)`，含 6 项独立目标按钮；
  - 瞒天过海区域：`sectionDrawRect = Rectangle(200f, 155f, 880f, 185f)`，含 6 项独立目标按钮；
  - 底部操作：`btnSkipRect = Rectangle(680f, 102f, 180f, 42f)`（不使用千术，提交空选择），`btnConfirmRect = Rectangle(880f, 102f, 180f, 42f)`（确认出千）。
- **模态输入守卫**：
  - 活动期间全屏遮罩及内部所有非交互区域均消费触摸（返回 `true`），绝对阻断点击穿透到牌桌下层手牌、托管切换、理牌开关、下一局及鸣牌按钮；
  - 顶部返回菜单（Back to Menu）按契约优先于模态弹窗，保持随时可退回主菜单。
- **倒计时时间源**：
  - 基于权威 `prep.serverNowMillis` 结合本地单调流逝时间推算；
  - 权威 `snap.paused == true` 时本地流逝时间冻结，恢复后顺延，不可被客户端伪造时间。
- **中文字形与无头测试防护**：
  - 仅在真实 GL 渲染线程按需调用 FreeType 生成面板所需字形；
  - 显式字符集 `SHUFFLE_PANEL_CHARS` 严格限定为面板所需的汉字与符号，避免全 Unicode 字形加载造成内存暴增；
  - 构造函数与纯模型完全不触碰 GL 与字体生成器，无头单元测试零崩溃风险；
  - 面板持有并实现 `Disposable`，场景销毁时严格释放字体与生成器。

### 2.3 表现层最小化集成 (GameScreen / MahjongGame)
- `GameScreen` 仅通过委托挂载 `shufflePreparationPanel`，未在 `GameScreen.kt` 内堆积千术业务逻辑；
- 准备阶段若手牌为空，不重新弹出 `START GAME` 按钮；
- 准备阶段隐藏普通 `3+20` 倒计时（显示 `--+--`），不调用权威单局决策时钟；
- 默认产品入口 `MahjongGame.createLocalHost()` 启用 `ShuffleConfig(enabled = true)`。

---

## 3. UI 公共测试契约接口 (Public Test Surface)

供 Codex 编写无头测试与展示断言的公共接口：

### 3.1 纯模型状态查询与操作 (`panel.model`)
```kotlin
// 核心状态
val model: ShufflePreparationModel
val isActive: Boolean
val isLocked: Boolean
val isAuthorityConfirmed: Boolean
val isSubmitting: Boolean
val canConfirm: Boolean
val canSkip: Boolean

// 点数与费用
val availablePoints: Int
val initialCost: Int      // 40 或 0
val drawCost: Int         // 20 或 0
val totalCost: Int        // 0, 20, 40, 60
val remainingPointsPreview: Int
val isAffordable: Boolean

// 提示文案与原因
val hasMixedTargetsNotice: Boolean
val hasDrawNotice: Boolean
val confirmDisabledReason: String? // "点数不足", "已确认", "提交中...", "洗牌生成中...", null

// 目标集合
val initialTargets: MutableSet<ShuffleTarget>
val drawTargets: MutableSet<ShuffleTarget>

// 交互操作与断言辅助
fun toggleInitialTarget(target: ShuffleTarget): Boolean
fun toggleDrawTarget(target: ShuffleTarget): Boolean
fun clearDraft()
fun remainingSeconds(nowSystemMillis: Long): Long
fun update(prep: ShufflePreparationView?, paused: Boolean = false, epoch: Long = 0L, nowSystemMillis: Long = ...)
fun update(snapshot: PlayerSnapshot, nowSystemMillis: Long = ...)
```

### 3.2 几何包围盒与点击测试 (`panel.*`)
```kotlin
// 弹窗与独立倒计时
val modalRect: Rectangle                        // (180, 90, 920, 540)
val countdownBoxRect: Rectangle                 // (770, 560, 110, 48)

// 偷天换日 6 目标按钮
val btnInitialTargets: Map<ShuffleTarget, Rectangle>
// - CHINITSU_MAN: (220, 435, 260, 38)
// - CHINITSU_SOU: (510, 435, 260, 38)
// - CHINITSU_PIN: (800, 435, 260, 38)
// - DAISANGEN:    (220, 385, 260, 38)
// - KOKUSHI:      (510, 385, 260, 38)
// - DAISUUSHII:   (800, 385, 260, 38)

// 瞒天过海 6 目标按钮
val btnDrawTargets: Map<ShuffleTarget, Rectangle>
// - CHINITSU_MAN: (220, 245, 260, 38)
// - CHINITSU_SOU: (510, 245, 260, 38)
// - CHINITSU_PIN: (800, 245, 260, 38)
// - DAISANGEN:    (220, 195, 260, 38)
// - KOKUSHI:      (510, 195, 260, 38)
// - DAISUUSHII:   (800, 195, 260, 38)

// 底部操作按钮
val btnSkipRect: Rectangle                      // (680, 102, 180, 42)
val btnConfirmRect: Rectangle                   // (880, 102, 180, 42)

// 输入交互模拟入口
fun touchDown(tx: Float, ty: Float): Boolean
fun mouseMoved(tx: Float, ty: Float): Boolean
```

---

## 4. 交付总结与测试归属

- **完成状态**：Task ANTI-SHUFFLE-D 生产实现全部就绪。
- **测试归属声明**：本交付报告仅代表生产代码开发完成，不包含任何自创测试通过断言（"No testclaims"）。所有单元测试、集成测试与真实 GL 验收均由 Codex 独立设计、构建并执行验收。
