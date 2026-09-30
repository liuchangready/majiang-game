# 结算明细验收记录（Codex）

## 原实现评估

已有役种、三类宝牌、符数和基本点解释，领域分层方向合理；但初版单行文本无法稳定容纳完整信息，默认字体缺少中文，指示牌与命中牌缺少对应关系。高番基本点用 Int 位移会溢出，新结算字段也未纳入事件深拷贝。

## 分工和实现

- Codex：审计、`DESIGN.md`、独立预期值验收测试、OpenGL 截图、代码检视与构建。
- Antigravity：通过本地 Agent Mesh MCP bridge 的 `invoke_antigravity` 开发全部生产改动。
- R：逐指示牌命中快照、里宝牌资格及庄闲/荣和自摸上下文、BigInteger 精确原始基本点、嵌套事件隔离。
- U/F：三个分页区域、Noto 中文字体、真实牌面、逐项符数、逐笔支付进位、本场和供托、役满独立说明。
- G/H/I/J：复查发现的换行边界与30符保底说明、旧记录分页及字体访问编译修正、旧分页按钮间距。

## 可复现验证

```powershell
$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat test :lwjgl3:classes :android:assembleDebug --console=plain
.\gradlew.bat -I tools/architecture/settlement-detail-smoke.init.gradle :lwjgl3:settlementDetailSmoke --console=plain
```

新增测试在 `SettlementDetailAcceptanceTest.kt`、`SettlementDetailPaymentAcceptanceTest.kt`、`SettlementDetailPresentationAcceptanceTest.kt`。渲染检查在 `tools/architecture/SettlementDetailRenderSmoke.java`，使用隐藏的真实 LWJGL/OpenGL 窗口，不依赖无头 mock 来宣称画面正确。

检查内容：30符1番荣和1000、平和自摸20符分别取整700/400、七对25、互斥拆解不混加、4番40符与满贯阶梯、80番精确公式、重复/零命中/字牌循环指示、赤五叠加、杠四张、荣和牌只计一次、未立直里牌不泄漏、无役不能凭宝牌和、多个役满不加宝牌番、深层快照隔离、真实引擎本场与供托收支守恒、分页完整性及不误提交动作。

额外检视确实复现了仅最后一字超出宽度时的 StringIndexOutOfBoundsException；保留 `smoke-review.log` 作为修复前证据。初轮手算用例把11223344556677万最优拆解漏算平和，已核对“雀头11、234×2、567×2、荣和7两面”后更正为10番；不修改领域代码去迎合错误测试。

## 最终状态

最终全量 **364 tests，0 failures，0 errors，0 skipped**（343基线 + 21新增）；桌面编译、Android debug APK 构建通过。日志：`build/settlement-detail/final-validation.log`。

真实 OpenGL 检查通过：`SETTLEMENT_DETAIL_RENDER_PASS screenshots=25 nextRounds=1`，日志 `build/settlement-detail/smoke-final.log`。验证所有标签分页、五组表/五组里/赤牌、役满、4番40符满贯推导、零宝牌、旧14条役种三页均可到达、导航零 GameAction 提交、最后一字超宽边界及下一局按钮。全部页面检查文字字形/行高度，代表页面另做人工截图检视。

示例截图：`build/settlement-detail/real-dora.png`、`crowded-dora-2.png`、`mangan-ron-calc-1.png`、`mangan-ron-calc-2.png`、`yakuman-yaku.png`、`legacy-long-2.png`。crowded/legacy长列表为容量压力样例，规则正确性由真实手牌与引擎测试独立核对；不把压力样例当作可达实战牌型。

保留既有本项目规则配置（赤五、累计役满、每项役满一倍、连风雀头2符等），本次不修改各规则流派差异。结算展示最高得点的同一合法解释，不能把互斥拆解役种混加；役满宝牌仅展示命中，不加入番数与分数。

Android只验证构建及包内包含中文TTF与四种ABI的freetype库，未做真机交互验证。
