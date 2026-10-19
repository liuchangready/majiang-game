# D-05 玩家与观战共用公开桌面渲染（静态第一步）验收记录（Claude 独立核定）

- 基线 SHA：`8910e5b`；交付：ANTI 共 3 轮（thread_20261019_100219_ee47）
- 简报：`tools/architecture/v4-improvement/ANTI-D05-TASK.txt`
- 交付说明：`doc/v4-重构/ANTI-D05-DELIVERY.md`（由 Anti 撰写，以下结论为验收方独立核定）

## 验收证据

1. **编译与测试**：
   - `:presentation:test`：**316/316 全绿**（BUILD SUCCESSFUL）。关键类：
     TableBoardRenderModelContractTest 3/3、SpectatorSharedTableRenderTest 2/2、
     SpectatorScreenContractTest 3/3、GameScreenTest 4/4、
     GameplayLoopRegressionTest 5/5、DiscardInputGateAcceptanceTest 10/10、
     GameScreenSkinSmokeTest 3/3、MenuScreenTest 14/14（D-04 回归无恙）。
   - 全模块 `:engine :protocol :client :server :presentation` test：BUILD SUCCESSFUL。
2. **静态深审**（逐文件对照基线）：
   - `TableBoardRenderModel`：纯公开数据 17 字段全不可变防御复制；init 校验
     viewerSeat/四长度/viewerHandBackCount≥0；零禁用类型（源扫描测试强制）。
   - `TableBoardRenderMapper`：玩家路径落汗标记集（EXPOSED_INDICATOR/EXPOSED_MELD/
     riverMarks）与基线 GameScreen 逐源一致；观战路径 viewerSeat=0、标记全空、
     rowsFromCounts、viewerHandBackCount=handTileCounts[0]。
   - `TableBoardRenderer`：无状态、投影矩阵调用方传入；五段布局取玩家屏基线值；
     字体色/缩放 try-finally 恢复；isSpectator 前缀与 [YOU] 标签按简报实现；
     动画可见性/高亮/时钟经 options 谓词参数注入（为第二步预留）。
   - `GameScreen`：仅删五段渲染与三个对手手牌私有函数；getDiscardStart/
     getDiscardSlotPosition 改为委托公共常量，**数值与基线逐值相等**（diff 中被删
     内联值与渲染器常量逐项核对）；每帧单次构模；gameStarted 门控保留；
     tableCamera 传入；自家明手牌/飞牌动画/面板/结算全部未动。
   - `SpectatorScreen`：snap?.board!=null 守卫后统一调用渲染器（mapper 的
     requireNotNull 不可达）；订阅/生命周期/输入拦截未动；旧五段+私有
     getDiscardSlotPosition 全部删除。
   - `OpponentHandPresentation`：新增 rowsFromCounts（STACKING→0 张、其余按计数
     全 null 槽）；既有 build/buildSlotsForSeat 未动。

## 轮次记录与验收方自修（与 Anti 返修分节，git diff 归因用）

- **第 1 轮**：主体交付合格，但其测试类两处失败。
- **第 2 轮**：Anti 报称完成返修，磁盘核对仅重写了测试文件（与验收方 10:18 的
  emptyList 自修重叠），两处失败原样未动 → 验收方发逐字级返修指令（3 处）。
- **第 3 轮**：Anti 落地全部 3 处（expiresAfterRoundOrdinal 3→4、补第 4 张
  NOT_VISIBLE 标记、渲染器 KDoc 改写去掉禁词字面量）。
- **验收方自修（共 2 行，均在 Anti 的测试夹具，产品代码零改动，断言零放松）**：
  1. `TableBoardRenderModelContractTest.kt:74` `emptySet()`→`emptyList()`
     （selectableTileIds: List<Int> 编译错，第 2 轮期间解锁模块编译）；
  2. 同文件 `activeTileCount = 3`→`4`（TileMarkView L294 契约：必须等于组内
     标记总数；验收方已通读 TileMarkGroupView/TileMarkView 全部 init 契约确认
     此为夹具契约链最后一环）。

## 简报勘误（验收方自认）

简报曾断言"两屏牌河槽位公式在 viewerSeat=0 时数值完全一致"——实际基线
GameScreen rel1=(750+…,440-…)/rel2=(460+…,530-…) 与观战绝对布局不同。
Anti 正确执行了简报的优先级规则（"玩家屏数值优先"），未受该错误陈述误导。

## 接受的偏差（非缺陷）

1. 观战屏布局统一到玩家屏数值：顶部席位标签 (640,670)→(320,675)；中心文案
   x 510→540（保留【观战】前缀）；侧/顶手牌背改为玩家屏对手行样式；牌河
   rel1/2 移至玩家屏公式。**需真实 GL 冒烟确认观战视觉**（无头测试不覆盖美观）。
2. GameScreen 空快照时不再绘制默认占位桌面文案（基线经 defaultEmptyPlayerView
   画默认"東1局"）；仅加载瞬间可见，接受。
3. renderSeatsInfo 新增字体状态恢复（基线无，改进）。
4. 观战手牌数异常时 getOrElse{13}→getOrNull?:0（PublicBoardView init 强制
   size=4，该分支不可达）。

## 结论

**通过**（第一步静态桌面范围）。第二步（动画控制器每视角状态实例）与第三步
（公开结算详情）按计划另立任务。与 D-04（commit 6e91234）无文件交集。
