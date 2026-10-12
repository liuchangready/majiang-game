# 袖里藏花表现层修复 (SB-E3FIX1) 生产交付报告 (DELIVERY-ANTI-E3FIX1.md)

- **任务标识**：SB-E3FIX1
- **责任人**：Anti (agy)
- **审查人**：Codex
- **日期**：2026-10-12
- **状态**：已完成全部修复并全量验证通过 (ready_for_review)

---

## 1. 交付概览与修改范围声明

本次修复完全响应 `doc/sleeve-bloom/TASK-ANTI-E3FIX1.md` 的所有退回项与指令：
- **修改模块限定**：仅修改 `presentation/src/main/kotlin` 下的生产代码及本任务文档，未变动任何其他模块代码、构建脚本或测试代码。
- **测试代码所有权**：所有测试代码和 GL 冒烟脚本均为只读，完全归 Codex 维护与所有。
- **零失败交付承诺**：全部 5 处退回缺陷彻底修复，全量 1110 项测试和真实双尺寸 GL 冒烟无一失败，真实 XML 数据核验完毕。

---

## 2. 核心缺陷修复与生产落地细节

### 2.1 理牌按钮尺寸更正 (Fix 1)
- **问题根因**：首轮执行时受到早期过时 CLI 指令误导设为了 `h36`，导致既有单元测试 `GameScreenTest:126` (`assertTrue(screen.btnSortRect.height >= 44f)`) 失败。
- **生产修复**：
  - 在 `GameScreen.kt` 中更正 `btnSortRect = Rectangle(1080f, 50f, 140f, 44f)`（高度恢复为 44f）。
  - 右侧纵列完整几何布局与无缝间隙：
    - 落汗入口：`Rectangle(1080f, 150f, 140f, 44f)`（y 跨度 150..194）
    - 袖里藏花：`Rectangle(1080f, 100f, 140f, 44f)`（y 跨度 100..144，间距 6px）
    - 理牌开关：`Rectangle(1080f, 50f, 140f, 44f)`（y 跨度 50..94，间距 6px）
    - AI托管/收回：`Rectangle(1080f, 6f, 140f, 36f)`（y 跨度 6..42，间距 8px）
  - 调整理牌按钮文字绘制纵坐标至 `btnSortRect.y + 28f`，保证在 44px 高度下垂直居中。

### 2.2 超时提示核验权威摸切证据 (Fix 2)
- **问题根因**：原先仅比较 `serverNow >= deadlineMillis` 且挂起 offer 消失即弹出提示，导致超时后因其他原因（如发牌失效清理、更换新牌）清除 offer 时误将失效判定为“保留第一张”。
- **生产修复**：
  - 在 `GameScreen.kt` 的 `onSnapshotAndEvents` 中建立严密的权威超时默认摸切校验：
    ```kotlin
    val oldOffer = activeBloomOffer
    val newOffer = snap.sleeveBloom?.pendingOffer
    if (oldOffer != null && newOffer == null) {
        val latestServerNow = snap.clocks.map { it.serverNow }.maxOrNull() ?: 0L
        val myDiscards = snap.view.discards.getOrNull(snap.viewerSeat) ?: emptyList()
        val firstTileDiscarded = (myDiscards.lastOrNull()?.tileId == oldOffer.firstTile.tileId) ||
            (snap.view.lastDiscard?.tileId == oldOffer.firstTile.tileId && snap.view.lastDiscardPlayer == snap.viewerSeat)
        val myControl = snap.controls.firstOrNull { it.seat == snap.viewerSeat }
        val isTakeover = (myControl?.role == "TAKEOVER")
        val isTimeout = (latestServerNow >= oldOffer.deadlineMillis && oldOffer.deadlineMillis > 0L) &&
            firstTileDiscarded &&
            !isTakeover
        if (isTimeout && !bloomChoiceSubmitted && !roundChanged && !epochChanged && !sessionChanged &&
            snap.view.phase != GamePhaseView.ROUND_END && snap.view.phase != GamePhaseView.MATCH_END) {
            showToast("超时已默认保留第一张，额度不退")
        }
        activeBloomOffer = null
        bloomChoiceSubmitted = false
    }
    ```
  - **证据要求**：
    1. 同一 Session、Round、SyncEpoch 上下文；
    2. 真人席位未被 AI 接管（`!isTakeover`）；
    3. 从所有席位 clock 中提取最大 `serverNow >= deadlineMillis`；
    4. 明确要求检测到权威实施的默认摸切事实（牌河末尾或最新打出牌即为 `oldOffer.firstTile`）；
    5. 成功过滤 `stale-after-deadline`、`terminal`、`binding` 等反例，同时精准保留权威超时正例。

### 2.3 双向模态互斥禁用视觉同步 (Fix 3)
- **问题根因**：落汗选牌模态与藏花二选一模态虽有点击拦截，但入口按钮的渲染颜色未联动对方模态状态，导致落汗选牌时藏花按钮仍呈现亮橙色。
- **生产修复**：
  - `TileMarkPanel.render` 引入可选参数 `sleeveBloomChoosing: Boolean = false`，当袖里藏花处于二选一时，落汗入口按钮强制应用 `canOpen = false`，渲染为灰色半透明背景与低亮描边。
  - `SleeveBloomPanel.render` 引入可选参数 `tileMarkSelecting: Boolean = false`，当落汗处于选牌时，藏花入口按钮强制应用 `canOpen = false`，渲染为灰色半透明背景与低亮描边。
  - 在 `GameScreen.render` 中双向传入对方状态：
    - `tileMarkPanel.render(..., sleeveBloomChoosing = sleeveBloomPanel.model.isChoosing)`
    - `sleeveBloomPanel.render(..., tileMarkSelecting = tileMarkPanel.model.isSelecting)`
  - 在 `GameScreen.touchDown` 中保证输入拦截与视觉呈现完全一致，并杜绝击穿。
  - 经 GL 冒烟测试中的两处像素取样验证，禁用按钮像素 RGB 严格满足 `max(r, g, b) < 160 && max - min < 40`。

### 2.4 候选牌来源标注文字坐标修正 (Fix 4)
- **问题根因**：卡底为 `y=290`，操作按钮顶为 `y=269`。原文字绘制 y 坐标设为 `274f`，由于 `BitmapFont.draw` 以字符顶部作为 y 锚点，字形向下延伸至约 `258f`，被高度至 `269f` 的确认/取消按钮大面积遮挡。
- **生产修复**：
  - 在 `SleeveBloomPanel.kt` 中将来源标注绘制 y 坐标调整为 `286f`：
    ```kotlin
    font.draw(batch, "已摸到", firstCardRect.x + 20f, 286f)
    val secondLabel = if (offer.drawKind == SleeveBloomDrawKind.RINSHAN) "王牌替补" else "牌山次张"
    font.draw(batch, secondLabel, secondCardRect.x + 10f, 286f)
    ```
  - 调整后字符顶部为 `286f`，底部约为 `270f`，刚好位于 `[269f, 290f]` 的 21px 缝隙内，与卡片和按钮均无重叠，完整清晰可辨。

### 2.5 秘籍弹窗文本多行排版优化 (Fix 5)
- **问题根因**：秘籍弹窗第 5 项文本过长，以单行绘制时超出了面板右边界 `x=1020`。
- **生产修复**：
  - 保持完整准确的规则阐述，绝不删减任何说明细节。
  - 在 `MenuScreen.kt` 中将第 5 项拆解为 3 行排版，行高调整为 26px，并匀出各条目纵向间距：
    ```kotlin
    font.draw(batch, "1. 落汗绝技: 摸牌/出牌决策时，选择手牌中 4 张不同实体牌进行微痕标记。", modalRect.x + 50f, modalRect.y + 405f)
    font.draw(batch, "2. 千术消耗: 每次落汗消耗 10 额度点数，每名玩家最多同时维持 12 张标记牌。", modalRect.x + 50f, modalRect.y + 365f)
    font.draw(batch, "3. 有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。", modalRect.x + 50f, modalRect.y + 325f)
    font.draw(batch, "4. 洞察识别: 标记牌进入对手手牌时可识别牌面，打入牌河时精准定位标识。", modalRect.x + 50f, modalRect.y + 285f)
    font.draw(batch, "5. 袖里藏花: 摸牌后出牌前消耗 10 额度，翻看第二候选牌二选一保留；", modalRect.x + 50f, modalRect.y + 245f)
    font.draw(batch, "   第二候选为牌山次张或杠后王牌替补；未选牌留在或退回对应位置；", modalRect.x + 50f, modalRect.y + 219f)
    font.draw(batch, "   超时保留原摸牌并摸切，费用不退。", modalRect.x + 50f, modalRect.y + 193f)

    font.color = Color(0.95f, 0.45f, 0.45f, 1f)
    font.draw(batch, "注意: 立直后、非本人回合或思考超时禁止施展落汗与袖里藏花，请选准时机！", modalRect.x + 50f, modalRect.y + 150f)
    ```
  - 每行文本宽度均控制在 550px 以内（右边界在 860px 左右，远小于 `x=1020`），且距离底部关闭按钮（`y=135..177`）留有大于 60px 的清晰物理安全距离，完全无重叠。

---

## 3. 严格验证与测试结果数据

所有验证均在本地干净环境中执行，并查验真实新鲜 XML 报告与 stdout：

### 3.1 单元测试全量验证 (`.\gradlew.bat test --rerun-tasks --no-build-cache --console=plain`)

全仓 5 个模块共 **1110** 项测试全部执行并 **100% 通过**，0 失败，0 错误，0 跳过：

| 模块 | 测试总数 | 失败 (Failures) | 错误 (Errors) | 跳过 (Skipped) | 状态 |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **engine** | 407 | 0 | 0 | 0 | PASSED |
| **protocol** | 158 | 0 | 0 | 0 | PASSED |
| **server** | 141 | 0 | 0 | 0 | PASSED |
| **client** | 136 | 0 | 0 | 0 | PASSED |
| **presentation** | 268 | 0 | 0 | 0 | PASSED |
| **总计** | **1110** | **0** | **0** | **0** | **ALL PASSED** |

- `presentation` 模块关键测试 XML：
  - `TEST-com.majiang.screen.GameScreenTest.xml`：4 tests, 0 failures, 0 errors（理牌高度验证恢复通过）
  - `TEST-com.majiang.screen.SleeveBloomScreenContractTest.xml`：8 tests, 0 failures, 0 errors（包含 `stale-after-deadline` 与 authority timeout 提示核验，全部通过）
  - `TEST-com.majiang.screen.SleeveBloomChoiceModelContractTest.xml`：6 tests, 0 failures, 0 errors

### 3.2 平台编译与集成验证 (`.\gradlew.bat :lwjgl3:classes :android:assembleDebug --console=plain`)
- `:lwjgl3:classes`：BUILD SUCCESSFUL
- `:android:assembleDebug`：BUILD SUCCESSFUL

### 3.3 真实 GL 冒烟与像素断言验证
1. **1280 视口冒烟**：
   ```powershell
   .\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke --console=plain
   ```
   - 结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=564`
   - 包含通过：落汗模态开启期间藏花按钮灰色像素断言、藏花二选一期间落汗按钮灰色像素断言、两次真实二选一（SECOND 与 FIRST）操作、额度与 deadline 扣除核验、正常返回菜单无泄漏。
2. **960 视口冒烟**：
   ```powershell
   .\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeWidth=960 --console=plain
   ```
   - 结果：`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=583`
   - 两套分辨率的截图均已重新生成并保存在 `build/sleeve-bloom/screenshots-1280` 与 `build/sleeve-bloom/screenshots-960`。

---

## 4. 交付文件清单

| 文件路径 | 说明 |
| :--- | :--- |
| `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt` | 修正 `btnSortRect` 高度、完善超时摸切判定、接入模态互斥渲染与输入拦截 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt` | 渲染入口增加 `sleeveBloomChoosing` 状态联动 |
| `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomPanel.kt` | 渲染入口增加 `tileMarkSelecting` 状态联动；修正来源文字 y 坐标至 286f |
| `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt` | 秘籍弹窗第 5 项文本换行与垂直间距排版优化 |
| `doc/sleeve-bloom/PROGRESS-ANTI-E3FIX1.md` | E3FIX1 进度追踪与问题修复记录 |
| `doc/sleeve-bloom/DELIVERY-ANTI-E3FIX1.md` | 本交付报告 |
| `doc/sleeve-bloom/CALLBACK-ANTI-E3FIX1.json` | 机器可读回调结构数据 |

---

## 5. 结论

E3FIX1 的 5 项要求已全量高质量完成，所有单元测试与图形冒烟测试均已真实全绿通过。现在提交回调请求 Codex 进行独立复审与验收。
