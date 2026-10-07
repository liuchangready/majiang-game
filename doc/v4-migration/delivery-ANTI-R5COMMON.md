# R5 COMMON 交付记录 (Anti)

## 1. 任务概述

本交付完成 V4 迁移任务 `R5COMMON`：将共享渲染资源、物理牌视图、权威时钟格式化、结算推导与弹窗渲染、比赛最终成绩总览、常驻听牌小面板等表现层组件完全迁移至 V4 `presentation.common` 及其子包。所有组件出参入参严格限定于 `com.majiang.client.api` 视图模型与展示库原生图元，彻底杜绝任何 `engine`、`protocol`、`server` 或旧 `GameClient/SessionAccess` 依赖。

## 2. 交付生产文件清单

所有生产代码均位于 `presentation/src/main/kotlin/com/majiang/presentation/common/` 及其子包中：

1. [`com/majiang/presentation/common/ClockDisplay.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/ClockDisplay.kt)
   - 基于 `com.majiang.client.api.ClockInfo` 与本地增量毫秒计算。
   - 保留 `free+reserve` 整秒格式（如 `3+20`, `0+19`, `0+0`, `--+--`）、5 秒紧急警戒判定 (`isUrgent`)。
2. [`com/majiang/presentation/common/render/TileDrawBuffer.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TileDrawBuffer.kt)
   - 纯表现层分桶批处理缓冲：`FilledRectOp`, `LineRectOp`, `TextureOp`, `TextOp`, `OverlayTextureOp`。
   - 支持多层绘制与单次 `flush(batch, shape, font)` 提交。
3. [`com/majiang/presentation/common/render/TileSkin.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TileSkin.kt)
   - 迁移至 `com.majiang.client.api.TileView` 与 `SuitView`。
   - 包含 `TileSkin` 接口、`SkinId` 枚举、`WikipediaSkin` 与 `ClassicSkin` 实现。
4. [`com/majiang/presentation/common/render/SkinManager.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/SkinManager.kt)
   - 独占拥有并管理 `TileSkin` 生命周期（`WikipediaSkin`、`ClassicSkin`）。
   - 包含 `SkinPreferences` 抽象与 `GdxSkinPreferences`、`InMemorySkinPreferences` 实现。
5. [`com/majiang/presentation/common/render/TileArranger.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/TileArranger.kt)
   - 基于 `TileView` 的纯函数手牌排序器。
   - 总序规范：万(1~9) → 索(1~9) → 筒(1~9) → 東南西北 → 中發白（三元牌降序），同型牌按 `copyIndex` 升序稳定排列。
   - 提供 `sortForDisplay(closed, drawn): ArrangedHand`，精确识别摸牌在排序后的槽位。
6. [`com/majiang/presentation/common/render/DiscardAnimation.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/DiscardAnimation.kt)
   - 基于 `TileView` 的打牌飞行动画时间线与贝塞尔曲线计算。
   - 包含 `bezier`, `easeInOutCubic`, `poseAt`, `controlPoint`（四座位离心上抛 40~90px），以及 `shouldSuppressDiscard`、`isQueuedRiverTile` 视图扣牌调和。
7. [`com/majiang/presentation/common/render/DoraSparkle.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/DoraSparkle.kt)
   - 迁移至 `TileView`，独立实现数牌/字牌顺位循环映射（`doraIndicatorTarget` 与 `computeVisibleTargets`）。
   - 1.2s 周期正弦脉动确定性 alpha `[0.15, 1.0]`，赤宝牌（红五）始终脉动，交互高亮时抑制光环描边但保留辉光与星芒。
8. [`com/majiang/presentation/common/render/GhostHandTexture.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/GhostHandTexture.kt)
   - 80x72 幽灵手纹理程序化生成与全局缓存，提供显式 `dispose()` 生命周期管理。
9. [`com/majiang/presentation/common/render/MeldDisplayLayout.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/MeldDisplayLayout.kt)
   - 迁移至 `com.majiang.client.api.PlayerView`, `MeldView`, `MeldTypeView`, `TileView`。
   - 维护视角相关四家席位副露排布（0=底端, 1=右侧, 2=顶端, 3=左侧），暗杠首尾翻转，加杠第4张标 `added`，外来鸣牌标 `called`，严格限制于 4-kan 物理安全边界。
10. [`com/majiang/presentation/common/render/MeldDisplayRenderer.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/render/MeldDisplayRenderer.kt)
    - 消费 `MeldDisplayGroup` 与 `TileSkin`，将副露牌面与 CALLED / ADD / CLOSED / FROM 文本追加到 `TileDrawBuffer`。
11. [`com/majiang/presentation/common/SettlementDetailPresenter.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/SettlementDetailPresenter.kt)
    - 纯函数结算推导对象，消费 `com.majiang.client.api.ScoreBreakdownView` 与 `TileView`。
    - 维护 `SettlementSection` (YAKU, DORA, CALC) 3 个标签页，每页 6 行分页切片。完整推导役满倍数、表里宝牌命中指示牌/目标/命中牌、符数拆解及四家付款明细。
12. [`com/majiang/presentation/common/SettlementDetailRenderer.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/SettlementDetailRenderer.kt)
    - 和牌详情模态弹窗 (180, 95, 920, 540) 渲染器。
    - 消费 `SettlementDetailView`, `ScoreBreakdownView`, `TileSkin`, `TileDrawBuffer`。
    - 安全管理 NotoSansSC FreeType 中文字体按需字形与 headless/无 GL 回退。
    - 包含 `SettlementModalGeometry` 常量与 `wrapRowText` 纯函数折行测量。
    - 解决 Codex 独立诊断反馈：在调用处将跨模块 `settlement.winTile` 保存到局部只读变量 `val winTile` 再进行非空判定，杜绝跨模块 smart cast 编译报错。
13. [`com/majiang/presentation/common/FinalResultRenderer.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/FinalResultRenderer.kt)
    - 比赛总成绩模态弹窗渲染器。
    - 消费 `com.majiang.client.api.PublicSessionResultView` 与 `SessionProgressView`。
    - 呈现四人最终排名（冠/亚/季/殿）、本人席位标记 `[你]`、负分击飞高亮、权威终局原因中文映射（击飞终局 / 正常终局 / 延长战结束 / 庄家终了）。
14. [`com/majiang/presentation/common/TenpaiPanelRenderer.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/TenpaiPanelRenderer.kt)
    - 手牌上方常驻听牌小面板渲染器。
    - 消费 `List<TileView>`，`computePanelRect(waitsCount)` 自动适配 1 行 (126h) 与 2 行 (172h) 面板高度，x220, y195 安全避让操作按钮。支持振听警告边框与提示。

## 3. 交付测试文件清单及旧有效断言对应

所有测试代码均位于 `presentation/src/test/kotlin/com/majiang/presentation/common/` 及其子包中，共 15 个测试文件，完整承接并保留所有原有功能、物理守恒与视觉断言：

1. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/TileArrangerTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/TileArrangerTest.kt)
   - 对应原 `render/TileArrangerTest.kt`。
   - 完整保留：34 种牌型全顺序断言、4 张同型牌按 copyIndex 升序稳定性、摸牌插入与索引识别、空手牌/仅摸牌边界、三元牌中發白降序、五花色总序。
2. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/DiscardAnimationTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/DiscardAnimationTest.kt)
   - 对应原 `render/DiscardAnimationTest.kt`。
   - 完整保留：贝塞尔端点与单调性、easeInOutCubic 缓动特性、阶段切分与 isDone、离心上抛控制点弧度、shouldSuppressDiscard 扣牌调和、isQueuedRiverTile 排队条目扣牌。
3. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/DoraSparkleTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/DoraSparkleTest.kt)
   - 对应原 `render/DoraSparkleTest.kt`。
   - 完整保留：数牌/字牌端点循环指示、1.2s 周期与 `[0.15, 1.0]` alpha 值域、赤宝牌持续闪光、四层光环与十字闪光星分桶、交互高亮抑制光环描边。
4. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/GhostHandTextureTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/GhostHandTextureTest.kt)
   - 对应原 `render/GhostHandTextureTest.kt`。
   - 完整保留：80x72 尺寸、掌心半透明度、指尖像素、四角透明、纹理缓存复用与安全 dispose。
5. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/TileDrawBufferTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/TileDrawBufferTest.kt)
   - 对应原 `render/TileDrawBufferTest.kt`。
   - 完整保留：分桶操作收集与 clear 幂等性、ClassicSkin (2 Filled + 1 Line + 1 Text) 与悬停/摸牌高亮色、14 张手牌批次指令总数、WikipediaSkin 纹理与描边、牌背指令分桶、34 种全量追加、赤宝牌 0m/0p/0s 红色文字、overlayTextures 旋转与缩放。
6. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/WikipediaSkinMappingTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/WikipediaSkinMappingTest.kt)
   - 对应原 `render/WikipediaSkinMappingTest.kt`。
   - 完整保留：34 种牌型无重复映射、字牌/三元牌文件名规范、数牌映射、赤宝牌 man5r/pin5r/sou5r 物理资源存在性。
7. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/WikipediaSkinPreviewTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/WikipediaSkinPreviewTest.kt)
   - 对应原 `render/WikipediaSkinPreviewTest.kt`。
   - 完整保留：34 张普通单牌 + 3 张赤宝牌 CPU 健全性加载与联系表生成。
8. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/SkinManagerTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/SkinManagerTest.kt)
   - 对应原 `render/SkinManagerTest.kt`。
   - 完整保留：默认皮肤 WIKIPEDIA、setSkin / toggleSkin 切换、InMemoryPreferences 跨实例读取、GdxSkinPreferences 真实配置读写。
9. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/MeldDisplayContractTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/MeldDisplayContractTest.kt)
   - 对应原 `render/MeldDisplayContractTest.kt` 中针对 `MeldDisplayLayout` 与 `MeldDisplayRenderer` 的契约测试。
   - 完整保留：5 种副露物理牌一对一展示、暗杠首尾翻转与加杠第4张标记、观察者视角隔离与输入不可变性、快照不可变保护、4-kan 物理安全边界不重叠、TileSkin 牌面/牌背分桶与 CALLED/ADD/CLOSED/FROM 标签。
10. [`presentation/src/test/kotlin/com/majiang/presentation/common/render/OpponentMeldDisplayAcceptanceTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/render/OpponentMeldDisplayAcceptanceTest.kt)
    - 对应原 `render/OpponentMeldDisplayAcceptanceTest.kt` 中针对四家相对席位副露排布的契约测试。
    - 完整保留：四家视角相对席位物理坐标验证（底/右/顶/左）、最大容量（16 个杠）互不重叠且避让手牌中央操作区。
11. [`presentation/src/test/kotlin/com/majiang/presentation/common/ClockDisplayTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/ClockDisplayTest.kt)
    - 针对 `ClockDisplay` 的专项单元测试。
    - 覆盖：空 clockInfo 默认 `--+--`、空闲状态保留储备 `0+20`、活跃状态免费时间优先递减（3+20 → 2+20 → 0+20 → 0+19）、免费时间耗尽且储备不足 5 秒时触发 `isUrgent`。
12. [`presentation/src/test/kotlin/com/majiang/presentation/common/SettlementDetailPresenterTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/SettlementDetailPresenterTest.kt)
    - 对应原 `screen/SettlementDetailPresentationAcceptanceTest.kt` 中针对 `SettlementDetailPresenter` 的全部领域与展现断言。
    - 完整保留：三标签页分页覆盖、役满排除常规翻数与符数公式、符数条目独立行与本场费乘数、四种获胜角色（庄/闲 × 自摸/荣和）原始与取整算分等式、荣和 30 符保底说明。
13. [`presentation/src/test/kotlin/com/majiang/presentation/common/SettlementDetailRendererTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/SettlementDetailRendererTest.kt)
    - 针对 `SettlementDetailRenderer` 文本折行与几何常量的单元测试。
    - 覆盖：短文本单行保持、长文本多行折行且无字元丢失截断、空文本安全处理、弹窗几何常数一致性。
14. [`presentation/src/test/kotlin/com/majiang/presentation/common/FinalResultRendererTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/FinalResultRendererTest.kt)
    - 针对 `FinalResultRenderer` 视图模型与终局规则映射的单元测试。
    - 覆盖：四人终局名次与点数封装、击飞负分识别、渲染器无头创建安全。
15. [`presentation/src/test/kotlin/com/majiang/presentation/common/TenpaiPanelRendererTest.kt`](file:///D:/majiang/presentation/src/test/kotlin/com/majiang/presentation/common/TenpaiPanelRendererTest.kt)
    - 针对 `TenpaiPanelRenderer` 的几何计算单元测试。
    - 覆盖：1~7 待牌单行高度 (126px)、8~13 待牌双行高度 (172px)、x220, y195 安全停靠手牌上方。

## 4. 独立诊断问题修复

根据 `doc/v4-migration/FEEDBACK-R5COMMON.md` 中 Codex 指出的编译诊断：
- 在 [`SettlementDetailRenderer.kt`](file:///D:/majiang/presentation/src/main/kotlin/com/majiang/presentation/common/SettlementDetailRenderer.kt) 中，将跨模块公开属性 `settlement.winTile` 提取至局部变量 `val winTile = settlement.winTile` 后再进行判空并传入 `tileSkin.appendTileDraw`，彻底解决跨模块属性不可直接 smart cast 为非空 `TileView` 的 Kotlin 编译器限制。

## 5. 待 R6 清理的旧文件清单

下列旧路径文件在 R5 期间已由新包完全承接，待并发分支 `GAME`（`GameScreen`）与 `ROOT`（`MenuScreen` / `MahjongGame`）完成引用切换后，在 R6 统一执行无用 V3 清理：
- `presentation/src/main/kotlin/com/majiang/presentation/ClockDisplay.kt`
- `presentation/src/main/kotlin/com/majiang/render/**` (9 个文件)
- `presentation/src/main/kotlin/com/majiang/screen/{SettlementDetailPresenter,SettlementDetailRenderer,FinalResultRenderer,TenpaiPanelRenderer}.kt`
- `presentation/src/test/kotlin/com/majiang/render/**` (8 个测试文件)
- `presentation/src/test/kotlin/com/majiang/screen/SettlementDetailPresentationAcceptanceTest.kt`

## 6. 测试未运行声明

**声明：按照用户严格限制与防多进程写冲突规范，Anti 仅使用内置文件读写/编辑工具完成所有生产与测试代码编写，未在本地执行任何 shell、terminal、Gradle build 或测试运行。全部代码已就绪，等待 Codex 串行执行编译构建、测试验收与架构审查。**
