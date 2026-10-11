# Anti：P1FIX2 表现层最终显示修正交付报告 (DELIVERY-ANTI-P1FIX2.md)

**交付日期**: 2026-10-11\
**执行角色**: Anti\
**复核角色**: Codex\
**交付状态**: ready_for_review\

---

## 1. 修正概述与范围

按照 [`doc/tile-mark/TASK-ANTI-P1FIX2.md`](TASK-ANTI-P1FIX2.md) 的要求，本次仅进行最后两处小范围表现层显示修正：
1. **对手识别真正小麻将牌面**：复用现有 [`TileSkin.appendTileDraw`](../../presentation/src/main/kotlin/com/majiang/presentation/common/render/TileSkin.kt) 与 [`TileDrawBuffer`](../../presentation/src/main/kotlin/com/majiang/presentation/common/render/TileDrawBuffer.kt)，从 [`GameScreen`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 传入参数，渲染 4 列 3 行最多 12 张真实麻将牌面（26×36 像素、横间距 28、纵间距 38），保持赤五图案，不使用文本替代牌面；
2. **秘籍弹窗第 3 行收窄**：在 [`MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt) 中将第 3 条精简为“`3. 有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。`”，使所有文字完全容纳在 760 宽弹窗背景内。

范围严格限制在 [`TileMarkPanel.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt)、[`GameScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 与 [`MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt)。未改动任何测试/GL工具/用户文件，未引入新图片资源，未改动领域与网络 API。

---

## 2. 修正明细

### 2.1 对手区域真正小麻将牌面渲染
- **接口扩展**：[`TileMarkPanel.render`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt) 增加 `tileSkin: TileSkin? = null` 与 `tileDrawBuffer: TileDrawBuffer? = null` 可选参数，[`GameScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt) 传入当前激活的 `tileSkin` 与成员实例 `tileDrawBuffer`；
- **几何排布与尺寸**：
  - 牌面尺寸：`26f × 36f`；横向列间隔 `28f`（4 列，跨度 110px），纵向行间隔 `38f`（最多 3 行，跨度 112px）；
  - 下家（右侧）：基准点 `(1140f, 345f)`，牌面向下排布在 `y=289f..213f`，严格位于 `y=194f` 操作按钮之上，右侧留有 30px 屏幕边距；
  - 对家（顶部）：基准点 `(500f, 670f)`，牌面位于 `x=500f..610f`，左侧距离追踪栏（`x<=340f`）留白 160px，右侧距离 P2 座位标签（`x=640f`）留白 30px，与右侧副露（`x>=846f`）互不重叠；
  - 上家（左侧）：基准点 `(40f, 345f)`，牌面位于 `x=40f..150f`，下方与 P0 座位标签留白充分，不侵入中央牌桌与追踪栏；
- **授权列表与赤五保持**：仅渲染 [`TileMarkPresentation.opponentMarks`](../../presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPresentation.kt) 返回的 `OPPONENT_HAND` 授权列表，不映射对手真实手牌槽位；通过 `tileSkin.appendTileDraw` 原生支持赤五纹理/着色。

### 2.2 秘籍弹窗文案收窄
- [`MenuScreen.kt`](../../presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt) 第 3 行精简为：“`3. 有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。`”；
- 截图人工复核确认：右侧留白超过 60px，所有文案完全在弹窗背景内。

---

## 3. 验证与人工截图核对

### 3.1 自动化测试与真实 GL 运行结果
- **Presentation 完整测试**：
  - 命令：`./gradlew.bat :presentation:test --console=plain`
  - 结果：**PASSED**（204 项测试全部通过，退出码 0）；
- **GL 运行链路冒烟测试**：
  - 命令：`./gradlew.bat :lwjgl3:tileMarkSmoke --init-script tools/architecture/tile-mark-smoke.init.gradle --console=plain`
  - 结果：**PASSED**（`TILE_MARK_GL_PASS frames=365 groups=3 fee=30 riverIndex=0`，退出码 0）；
- **GL 视觉场景冒烟测试**：
  - 命令：`./gradlew.bat :lwjgl3:tileMarkVisibilitySmoke --init-script tools/architecture/tile-mark-visibility-smoke.init.gradle --console=plain`
  - 结果：**PASSED**（`TILE_MARK_VISIBILITY_GL_PASS scenarios=7`，退出码 0）。

### 3.2 实际截图人工视觉核查
已使用图像查看工具逐一核对生成的截图：
1. `build/tile-mark/screenshots/technique-rules.png`：第 3 行“有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。”右端距弹窗边框约 60px，文字完整居中于安全区；
2. `build/tile-mark/screenshots/visibility-0.png`：下家（右侧）展示 4列×3行共 12 张真实 Wikipedia 牌面（一万×4、二万×4、三万×4），底部与“落汗 · 10”按钮之间间隔充足；
3. `build/tile-mark/screenshots/visibility-1.png`：对家（顶部）展示 12 张真实牌面，左避开追踪栏、右避开 P2 标签、下避开牌河；
4. `build/tile-mark/screenshots/visibility-2.png`：上家（左侧）展示 12 张真实牌面，排布整齐；
5. `build/tile-mark/screenshots/visibility-5.png`：对家展示 9 张真实牌面（2 行 4 张 + 1 行 1 张），与右侧碰牌副露（一万×3）完全隔离，无重叠无遮挡。

---

## 4. 修改文件清单

1. `presentation/src/main/kotlin/com/majiang/presentation/game/TileMarkPanel.kt`
2. `presentation/src/main/kotlin/com/majiang/presentation/game/GameScreen.kt`
3. `presentation/src/main/kotlin/com/majiang/presentation/menu/MenuScreen.kt`
