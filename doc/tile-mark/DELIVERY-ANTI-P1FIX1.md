# Anti：P1FIX1 表现层退回修正交付报告 (DELIVERY-ANTI-P1FIX1.md)

历史交付记录：本轮完成了时钟、输入、副露及翻页修正；对手实际小牌面和秘籍文字边界在P1FIX2继续完善。最终结果以[独立验收](ACCEPTANCE.md)为准。

**交付日期**: 2026-10-11\
**执行角色**: Anti\
**复核角色**: Codex\
**交付状态**: ready_for_review\

---

## 1. 修正背景与范围

响应 [`doc/tile-mark/TASK-ANTI-P1FIX1.md`](TASK-ANTI-P1FIX1.md) 的独立源码与截图审查意见，本次修复严格限制在 `presentation` 模块生产代码，不修改任何测试或夹具，不提交 Git，不启动额外代理，已验收的 `engine` / `protocol` / `server` / `client` 保持不动，用户启动器与快照脚本未做变更。

---

## 2. 六项修正落实明细

### 2.1 统一单调时钟与连接环境门控 (Item 1)
1. **单调毫秒时钟源**：
   - [`TileMarkSelectionModel`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt) 默认时钟源由墙钟 `System.currentTimeMillis()` 修正为同源单调毫秒 `System.nanoTime() / 1_000_000L`；
   - [`TileMarkPanel.update`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt) 默认 `nowMillis` 参数改为 `model.monotonicClock()`。
2. **连接环境驱动与草稿失效**：
   - [`GameScreen.onSnapshotAndEvents`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt)、`touchDown` 以及 `render` 渲染主循环均统一采用真实 `(session.connectionState == ConnectionState.CONNECTED) && !session.closed` 与 `tileMarkPanel.model.monotonicClock()` 驱动面板更新；
   - `TileMarkSelectionModel.update` 在检测到 `pending = true`、`!connected`、`snapshot == null`、`paused` 或其它不满足施术条件时立即调用 `cancel()` 清空已选草稿；连接恢复或动作完成后不可复活旧选择。
3. **输入路由保护**：
   - `GameScreen.touchDown` 入口按钮 `tileMarkPanel.btnOpenRect` 判定增设 `canOpenTileMark` 门控（校验未立直、未副露选牌、非弃牌动画中、非 pending、非提交中等）；当处于立直选牌时绝不吞掉输入，保证原有 `btnCancelRiichiRect`（取消立直）交互完全正常。

### 2.2 对手“落汗识别”分行小牌面显示 (Item 2)
1. **多行容纳 12 张牌面**：
   - [`TileMarkPanel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt) 将原本的单行 `joinToString` 改为 `oppMarks.chunked(4)` 分行小牌面显示，最多 3 行容纳 12 张牌面。
2. **三座位绝对不越界与几何隔离**：
   - **下家 (seat 1)**：基准点设为 `(1140f, 345f)`，向下排布 3 行，水平范围 `1140f..1250f`（保留 30px 右侧窗口留白），垂直范围 `285f..345f`（位于上方 P1 状态与下方操作按钮之间，绝对不遮挡行动按钮与自身手牌）；
   - **对家 (seat 2)**：基准点设为 `(500f, 670f)`，位于 P2 状态信息 (`x=640f`) 左侧，完美避开右侧副露区 (`x>=846f`) 与左侧追踪栏 (`x<=340f`)；
   - **上家 (seat 3)**：基准点设为 `(40f, 345f)`，向下排布 3 行，水平范围 `40f..135f`，紧贴左侧空白区，不侵入中央牌桌，不遮挡 P3 状态与上方追踪栏。
3. **保留赤宝牌面与防泄密**：
   - 牌面渲染若 `tile.isRedFive` 则显式添加 `"赤"` 前缀（如 `"赤五万"`）；
   - 牌面信息完全且仅取自当前快照授权的 `TileMarkArea.OPPONENT_HAND` 标记，不查询或泄露对手实际暗手牌顺序与摸牌槽位。

### 2.3 追踪翻页按钮真实绘制与防重叠排布 (Item 3)
1. **真实可点击按钮**：
   - 当 `groups.size > 1` 时，使用 `ShapeRenderer` 真实绘制填充背景与亮青色边框的 `btnTrackingPrevRect` (`Rectangle(200f, 618f, 42f, 22f)`) 与 `btnTrackingNextRect` (`Rectangle(250f, 618f, 42f, 22f)`)，并居中绘制清晰文字 `"上组"` 与 `"下组"`。
2. **各层级内容完全解耦**：
   - 顶部行 (`y=633f`)：左侧绘制标题 `"落汗追踪 [组 N/M]"`，右侧为翻页按钮，两者水平间隔超 40px；
   - 第二行 (`y=611f`)：独立绘制有效期限文本 `"有效期限: ${TileMarkPresentation.durationText(curGroup)}"`，位于按钮下沿下方，无重叠；
   - 第三至六行 (`y=585f, 559f, 533f, 507f`)：4 行分别排布 4 张牌的牌面与位置描述，垂直间隔 26px，距面板底边留有安全空间。
3. **配额满时支持三组浏览**：
   - 即使达到 12 张上限无法继续施术，`touchDown` 与翻页依然可用，可通过点击按钮或测试夹具完整浏览 3 组。

### 2.4 公开副露落汗标记支持 (Item 4)
- 在 [`GameScreen.renderMelds`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 中匹配快照中 `TileMarkArea.EXPOSED_MELD` 的已生效标记：
  - 遍历 `MeldDisplayLayout.layoutAll` 输出的副露组与展示牌 `dt: MeldDisplayTile`；
  - 匹配 `marked.seat == group.ownerSeat && marked.tileId == dt.tile.tileId`；
  - 调用 `drawTileMarkBadge(shape, dt.x, dt.y, dt.width, dt.height)` 绘制外框与汗滴徽标；
  - 复用副露原有实际布局，保留了横置、方向、加杠叠牌与暗牌背面显示，既有手牌/牌河/飞行动画完全保留。

### 2.5 秘籍文案更新与经济键纠偏 (Item 5)
1. **秘籍弹窗文案**：
   - [`MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt) 秘籍第 2 条明确更新为：`"2. 千术消耗: 每次落汗消耗 10 额度点数，每名玩家最多同时维持 12 张标记牌。"`
   - 第 3 条明确更新为：`"3. 有效期限: 成功标记后持续当局另 3 局，到期局自动清除；思考超时仅禁止新施术。"` 说明仅期限到期清除、思考超时只禁止新施术。
2. **清理多余经济键**：
   - 删除 [`MahjongGame.kt`](../../presentation/src/main/kotlin/com/majiang/MahjongGame.kt) 与 [`MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt) 中此前多加的 `shuffle_initial` / `shuffle_draw` / `initial` / `draw`；
   - 仅保留原 costs 并追加 `"TILE_MARK" to 10`。

### 2.6 交付说明数据纠偏 (Item 6)
- 真实核对方法名、字段与测试数量，消除历史报告偏差：
  - 修正落汗相关定向测试为真实存在的 **11 项**（5 项 SelectionModel，2 项 Presentation，4 项 Screen）；
  - 修正 presentation 全量测试为真实存在的 **204 项**；
  - 修正汗滴视觉呈现为真实使用的青蓝双层圆形徽标与矩形轮廓；
  - 修正追踪按钮几何坐标为实际使用的 `(200f, 618f, 42f, 22f)` 与 `(250f, 618f, 42f, 22f)`。

---

## 3. 验证命令与结果

### 3.1 真实落汗定向契约测试 (11 项全部通过)
- **命令**: `./gradlew :presentation:test --tests com.majiang.screen.TileMark*`
- **结果**: **11 项全部通过** (耗时 3s)
  1. `TileMarkSelectionModelContractTest`:
     - `selects four physical copies including drawn tile without permitting fifth or foreign tile` (PASSED)
     - `sorting retains physical selection but decision identity or candidate set invalidates it` (PASSED)
     - `authority deadline elapsed locally prevents confirmation without requiring a fresh push` (PASSED)
     - `new sync epoch replaces expired clock sample even when cursor and decision are unchanged` (PASSED)
     - `pause disconnect pending takeover disabled feature and insufficient quota block stale drafts` (PASSED)
  2. `TileMarkPresentationContractTest`:
     - `four seats and row boundaries locate physical copies and describe one based positions` (PASSED)
     - `opponent recognition is current seat authorized faces only and groups retain independent duration` (PASSED)
  3. `TileMarkScreenContractTest`:
     - `real button and hand clicks submit one mark with no accidental discard and no optimistic effect` (PASSED)
     - `font subset covers technique limits locations durations and translated refusals` (PASSED)
     - `new decision pause and disabled snapshot cancel modal without submitting stale tiles` (PASSED)
     - `reconnecting without a new snapshot blocks confirmation and invalidates selection` (PASSED)

### 3.2 presentation 完整测试套件 (204 项全部通过)
- **命令**: `./gradlew :presentation:test`
- **结果**: **204 tests completed, 0 failed, 0 errors, 0 skipped** (耗时 6s)

### 3.3 真实桌面 GL 冒烟测试
1. **产品级交互与状态机 GL 冒烟**:
   - **命令**: `./gradlew :lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle`
   - **结果**: **BUILD SUCCESSFUL** (退出码 0，耗时 13s)
   - **输出**: `TILE_MARK_GL_PASS frames=366 groups=3 fee=30 riverIndex=0`
2. **对手12张、副露与四家牌河可视化 GL 冒烟**:
   - **命令**: `./gradlew :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle`
   - **结果**: **BUILD SUCCESSFUL** (退出码 0，耗时 4s)
   - **输出**: `TILE_MARK_VISIBILITY_GL_PASS scenarios=7`
   - **截图人工核验**:
     - `visibility-0.png`: 下家 12 张分 3 行完全在屏 (`x=1140`，留有右侧 30px 安全余量)；追踪面板清晰绘制 `[上组]` 与 `[下组]` 按钮，与标题、期限及 4 张牌完全无重叠；
     - `visibility-1.png`: 对家 12 张在 P2 左侧分 3 行排布，不越界且不重叠；
     - `visibility-2.png`: 上家 12 张在屏幕左侧分 3 行排布，不侵入中央牌桌；
     - `visibility-3.png`: 四家牌河第 7 张（索引 6）精确展示落汗徽标；
     - `visibility-4.png`: 三家公开副露（OPEN_KAN / PON / ADDED_KAN）精准绘制青色落汗边框与水滴徽标；
     - `visibility-5.png`: 对家同时持有 12 张识别手牌与公开副露，文字与副露牌面左右物理隔离，互不遮挡；
     - `visibility-6.png`: 标记清除后画面干净，无幽灵残留；
     - `technique-rules.png`: 秘籍模态框文案准确，每玩家上限 12 张与到期清除表述清晰，无行溢出。

### 3.4 全量项目无头测试
- **命令**: `./gradlew test`
- **结果**: **BUILD SUCCESSFUL** (全系统 1000 项测试通过，0 failures, 0 errors)。

### 3.5 跨平台构建核查
- **桌面构建**: `./gradlew :lwjgl3:classes` -> **BUILD SUCCESSFUL**
- **Android 构建**: `./gradlew :android:assembleDebug` -> **BUILD SUCCESSFUL**

---

## 4. 交付文件清单

| 文件路径 | 变更说明 |
|---|---|
| `presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkSelectionModel.kt` | 统一单调毫秒时钟；pending 与失效状态及时清空旧草稿 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt` | 默认单调时钟；对手识别分行小牌面；绘制清晰翻页按钮与分层布局；`TILE_MARK_CHARS` 扩充字形 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt` | 单调时钟与真实连接状态更新；renderMelds 绘制副露落汗标记；入口按钮与取消立直路由隔离 |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt` | 秘籍文案纠偏；清理 defaultCosts 仅保留 TILE_MARK=10 |
| `presentation/src/main/kotlin/com/majiang/MahjongGame.kt` | 清理 defaultCosts 仅保留 TILE_MARK=10 |
| `doc/tile-mark/DELIVERY-ANTI-P1FIX1.md` | 本交付说明 |
| `doc/tile-mark/CALLBACK-ANTI-P1FIX1.json` | 交付回调 JSON |
