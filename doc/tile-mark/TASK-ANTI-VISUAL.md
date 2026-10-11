# 落汗图形展示开发任务 V1

2026-10-12，用户已授权推进 Anti 开发，Codex 编写测试并独立验收。先阅读根 AGENTS.md 与 doc/tile-mark/VISUAL-DESIGN.md。按最新确认实现完整功能，不能沿用旧稿的牌种余量/归属预测。

## 范围与约束

- 三家手牌全部画出：普通不透明牌背，仅落汗牌为半透明牌面（alpha 0.55）。标记牌取代牌背，不增加暗手牌张数，不暴露真实手牌顺序。
- 本人手牌、牌河、公开副露、指示牌中有效落汗牌也采用同一透明样式，保持可读、赤五及横置含义。替换汗滴为统一牌面透明样式；飞牌同样遵守。隐藏区域不揭露。
- 删除常驻文字追踪栏、文字位置说明、独立识别牌列。选择费用/数量、必要拒绝文案可保留。各组寿命按设计用小牌和点显示，不能仍输出旧的落汗追踪位置句子。
- 增加可开关牌河提示，顶部仅显示权威 wallRemaining 可摸总数；东南西北四行展示全部暗手牌背面，仅落汗牌透明翻面，包括本人所在行。主牌桌本人普通手牌正常可见。下部可滚动的四方牌河缩略网格支持实体定位高亮。未知归属不展示或猜测。无34牌种统计。
- 面板打开后合理重排/缩放牌桌，保持本人手牌、响应按钮、倒计时不遮挡；统一渲染和输入坐标变换，不以覆盖掩盖。默认关闭，Esc/再次点击关闭；输入不穿透；选牌和结算时收起。不暂停 deadline。
- 改动限 presentation/src/main 及你的新交付/进度/回调文档。无需改 engine/protocol/server/client。禁止修改任何 src/test、tools/architecture（Codex拥有测试）、Gradle文件。若现有测试因已授权行为变更而不适用，报告Codex改测试，不能弱化或删除断言。
- 保留用户文件 lwjgl3/build.gradle、lwjgl3/src/main/kotlin/com/majiang/desktop/SnapshotLauncher.kt、snapshot/，禁止碰这些文件。不要 git commit/add/reset，禁止启动其他代理或递归调用 Codex。

## 冻结纯模型接口 供 Codex 独立测试

在 com.majiang.presentation.game 下实现以下公开接口；可加字段和辅助类，不能修改这些签名。所有返回集合为防御复制的只读集合。

```kotlin
data class HandDisplaySlot(val tile: TileView?) // null=牌背；非null=本人授权有效落汗牌面
class OpponentHandRow(val seat: Int, slots: List<HandDisplaySlot>) { val slots: List<HandDisplaySlot> }
object OpponentHandPresentation {
    fun build(snapshot: GameSnapshotView?): List<OpponentHandRow>
}
class RiverHintView(val wallRemaining: Int?, hands: List<OpponentHandRow>) { val hands: List<OpponentHandRow> }
object RiverHintPresentation {
    fun build(snapshot: GameSnapshotView?): RiverHintView
}
```

OpponentHandPresentation.build 返回按 viewerSeat 后下家、对家、上家顺序的三行；每行 slots 数等于 handTileCounts，仅该家 OPPONENT_HAND 的有效标记可翻面，已知牌按 typeIndex、tileId 排列在一端，牌背无身份。null 快照返回空列表；0 张返回空槽行。重复/超量授权信息安全降级到牌背，不产生多牌或未知牌面。不用 TileView 伪造牌背。

RiverHintPresentation.build 按本局门风东南西北返回四行，同样牌背规则，但本人行允许 OWN_HAND 的标记翻面；NOT_VISIBLE、DISCARD、副露等不作为暗手牌翻面。null 快照为 wallRemaining=null/hands空；STACKING 未发牌阶段 wallRemaining=null；合法0保留0；其余阶段原样读取 view.wallRemaining。准备阶段不展示旧暗手牌位置。

## 执行与回调

开工立即写 doc/tile-mark/PROGRESS-ANTI-VISUAL.md，包含任务号 V1、正在执行、当前步骤；关键步骤更新。生产实现后运行 :presentation:test。Codex会并行增加测试，遇到新测试尚在编写/编译冲突先报告，不自行改测试。交付前必须完成并核对退出码：

```
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
```

Codex随后运行真实GL与独立回归，必要时退回修复。不得声称未执行的测试通过。

完成后写 DELIVERY-ANTI-VISUAL.md 和 CALLBACK-ANTI-VISUAL.json，JSON 至少 taskId=V1, recipient=codex, status=ready_for_review 或 blocked, modifiedFiles, tests(命令/退出码/结果), remainingIssues。终端输出 CODEX_CALLBACK V1 ready_for_review（或blocked）并退出，当前调用结果及回调文件作为通知。不要调用 invoke_codex。必须等测试结束再回调。
