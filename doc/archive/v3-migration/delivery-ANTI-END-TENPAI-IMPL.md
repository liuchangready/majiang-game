# 交付报告：ANTI-END-TENPAI-IMPL (击飞终局与听牌提示生产代码实现)

- **任务编号**：ANTI-END-TENPAI-IMPL
- **开发角色**：Antigravity (Production Developer)
- **验收/测试角色**：Codex (Design, Tests, Verification & Review)
- **交付时间**：2026-10-02
- **实施原则**：严格基于文件读写工具实现，不修改任何测试文件或构建脚本，保留工作区所有既有未提交变更。

---

## 一、新增与修改文件清单

1. **`engine/src/main/kotlin/com/majiang/session/TenpaiPreviewProvider.kt`**（新建）
   - 固定接口与单例对象：`object TenpaiPreviewProvider { fun waits(view: PlayerView): List<Tile> }`
   - 纯函数式只读视图计算，复用 `FuritenRules.waits`，不复制手牌拆解算法；
   - 严格约束有效等待形为 `closedTiles.size + melds.size * 3 == 13`；
   - 摸牌后 (`myDrawnTile != null`) 自动推导摸牌前有效闭手等待形；打牌后推导新等待形；吃碰后未弃牌、其余 14 张等效形及准备/终局阶段返回空列表；
   - 结果按 `typeIndex` 去重升序排序，转换为普通非赤展示牌并包装为不可变集合。

2. **`core/src/main/kotlin/com/majiang/screen/FinalResultRenderer.kt`**（新建）
   - 专门负责整场对局总成绩模态弹窗的几何、文字与多玩家排名展示；
   - 权威读取 `session.finalResult`（终局原因如击飞 BANKRUPTCY、四人权威名次、最终得分与出千点数）；
   - 兼容模式下缺失 `finalResult` 时仅展示“比赛已结束”与公开分数，绝不伪造终局原因；
   - 提供“返回主菜单”按钮（对齐 `520, 145, 240, 46`）及“本局明细”按钮（`250, 145, 240, 46`）。

3. **`core/src/main/kotlin/com/majiang/screen/TenpaiPanelRenderer.kt`**（新建）
   - 专门负责手牌上方常驻听牌小面板的几何与牌面渲染；
   - 几何定位：`x = 220f, y = 195f`，对齐手牌左侧，垂直方向严格位于手牌（`40..120`）与操作按钮（`145..189`）之上，水平方向位于理牌按钮（`1080`）之左且不触碰牌河；
   - 标注文案：“听牌”或“原听牌”、理论待牌张数、“牌型待牌，和牌仍需满足役种与振听规则”；
   - 振听时醒目提示“振听：不能荣和”并切换警告边框；
   - 待牌列表通过现有 `TileSkin` 渲染，支持国士无双 13 面等多行自适应换行展示。

4. **`core/src/main/kotlin/com/majiang/screen/SettlementDetailRenderer.kt`**（修改）
   - 在终局状态 (`isMatchEnd == true`) 下，底部按钮清晰展示高亮背景与白色文字“查看总成绩”或“MATCH ENDED”。

5. **`core/src/main/kotlin/com/majiang/screen/GameScreen.kt`**（修改）
   - 暴露只读验收契约属性：`isMatchFinished`、`isFinalResultShown`、`tenpaiWaits`、`tenpaiPanelRect`、`btnReturnToMenuRect`、`btnRoundDetailsRect`；
   - 统一整场终局权威判定：`isMatchFinished` 综合检查 `currentView.phase == MATCH_END`、`session.lifecycle == "FINISHED"` 及 `session.finalResult != null`；
   - 消除点击终局按钮错误请求下一局的缺陷：终局状态下点击结算按钮直接流转至总成绩弹窗，不调用 `nextRound`、`start` 或游戏动作；
   - 终局总成绩交互接线：支持点击“返回主菜单”调用 `exitToMenu()`，支持点击“本局明细”切回末局结算详情，且不重置游戏状态；
   - 解决下一局异步拒绝丢失结算的问题：增加 `isNextRoundSubmitting` 提交门控，点击下一局不再立即关闭结算，失败或拒绝时保留原结算详情并弹出 Toast，仅在权威新局快照到达后清理；
   - 终局表现状态锁死：终局无倒计时（`--+--`）、状态盒显示 `[FINISHED]`、所有 `canHuman*` 动作禁用；
   - 常驻听牌提示集成：随快照与视图更新缓存至 `cachedTenpaiWaits`，禁止逐帧重复拆解，在立直选牌、副露选择、结算、总成绩、洗牌准备时自动隐藏。

---

## 二、验收契约达成说明

| 契约项 | 实现位置 | 表现与行为说明 |
| :--- | :--- | :--- |
| **统一权威终局判定** | `GameScreen.isMatchFinished` | 综合检查 `session.lifecycle == "FINISHED"`、`session.finalResult != null` 与 `currentView.phase == MATCH_END`，UI 不以分数自行判定权威终局。 |
| **总成绩展示状态暴露** | `GameScreen.isFinalResultShown` | 提供公开只读布尔属性，准确反映总成绩弹窗是否处于显示中。 |
| **常驻听牌待牌暴露** | `GameScreen.tenpaiWaits` | 提供不可修改待牌 Tile 列表，经 `TenpaiPreviewProvider.waits` 缓存，随快照更新重算，禁止逐帧重复分解。 |
| **终局按钮与明细流转** | `SettlementDetailRenderer` & `GameScreen` | 最后一局和牌/流局结算底部按钮明确显示“查看总成绩”；点击进入总成绩模态并保留本局明细；总成绩页可点击“本局明细”再次查看。 |
| **总成绩内容与返回菜单** | `FinalResultRenderer` | 展示终局原因（击飞等）、四人名次/最终得分/出千点数；提供“返回主菜单”按钮（`520,145,240,46`），复用 `exitToMenu()` 关闭宿主回到 `MenuScreen`。 |
| **快照重连与同局恢复** | `GameScreen.onSnapshotAndEvents` | FINISHED 状态下快照不清除结算与总成绩；无 live win / settlementEvents 时自动展示总成绩；同局重复快照不强制隐藏已展开的总成绩。 |
| **异步下一局失败保留** | `GameScreen.touchDown` | `isNextRoundSubmitting` 防重点击；异步请求被拒绝或网络失败时不清理结算弹窗，保留供重新尝试；成功时等待新局快照权威清理。 |
| **听牌形状与等待形契约** | `TenpaiPreviewProvider.waits` | 暗牌数 + 副露数 * 3 == 13；摸牌非空时取 `myClosedTiles`（原听牌）；吃碰后未打牌（14等效张）及准备/终局阶段返回空列表；杠排除第5张。 |
| **听牌面板防遮挡几何** | `TenpaiPanelRenderer` | 固定在 `x=220, y=195`，位于手牌（40..120）与操作按钮（145..189）上方，位于理牌按钮（1080）左侧，待牌多行自适应折行。 |
| **说明与振听清晰提示** | `TenpaiPanelRenderer` | 严格说明“牌型待牌，和牌仍需满足役种与振听规则”；处于振听时明确标示“振听：不能荣和”并使用醒目警告边框。 |

---

## 三、交由 Codex 执行的独立验证建议

1. **V3 宿主击飞终局全链路验证**：
   - 自摸击飞、放铳击飞、流局罚符击飞，确认 `MatchSession` 转移至 `FINISHED`，`finalResult.reason == "BANKRUPTCY"`，四人名次与点数准确无误；
   - 0 分临界点阈值对照（默认小于 0 击飞，等于 0 不击飞）；
   - 终局状态下调用 `nextRound()` 验证被拒绝且 `roundId` 保持不变。
2. **表现层终局与恢复验证**：
   - `ROUND_END + FINISHED` 状态下点击终局按钮不调用 `nextRound`，正确打开总成绩模态；
   - 重连到已结束对局（缺失 settlementEvents）验证直接展示总成绩；
   - 未终局对局模拟 `nextRound()` 失败，验证结算弹窗完好保留且支持重新点击；
   - 检查总成绩界面中点击“返回主菜单”释放 TableHost 宿主并切回 MenuScreen。
3. **听牌提示算法全覆盖验证**：
   - 默听、立直、单副露、多副露、七对子、国士无双 13 面全牌型测试；
   - 门清与副露中已有 4 张的牌，确认已在待牌列表中彻底排除（四张上限）；
   - 摸牌前后视图验证：摸牌时等待形为“原听牌”，弃牌后刷新为新等待形；吃碰后未打牌阶段待牌为空；
   - 非 0 号观察者座位（P1..P3）及不变量（视图未受篡改、返回列表不可修改）验证；
   - 真实 GL 环境下截图检视常驻听牌面板与总成绩弹窗的排版。
