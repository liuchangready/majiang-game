# 架构清理验收记录（ARCH-CLEANUP）

2026-10-13。开发者 Anti（简报 [TASK-ANTI-ARCH-CLEANUP.md](TASK-ANTI-ARCH-CLEANUP.md)，交付 [DELIVERY-ANTI-ARCH-CLEANUP.md](DELIVERY-ANTI-ARCH-CLEANUP.md)），Codex 检视验收。范围：2026-10-13 架构检视结论中的卫生清理项，无生产行为变化。

## 验收结论：通过

## 执行内容

1. **测试包名对齐**（Codex 先以 git mv 完成 38 个文件的机械搬移并删除全部空目录骨架，Anti 完成全部 package/import 内容编辑）：
   - `com.majiang.screen`(20)、`com.majiang.v3`(4)、散落旧包(5) → `com.majiang.presentation.game`；`com.majiang.testing`(presentation 侧 5 文件) → `com.majiang.presentation.testing`；`GdxHeadlessTestRunner` → `com.majiang.presentation`；`MenuScreenTest` → `com.majiang.presentation.menu`；architecture/state/session 旧包 5 文件 → `com.majiang.presentation.architecture|game`。
   - 同包隐式引用补显式 import：MenuScreenTest+`MahjongGame`、GameplayLoopRegressionTest+`com.majiang.state.*`、RiichiPreviewContractTest+`com.majiang.session.*`。
   - engine testFixtures 包 `com.majiang.testing`（TestTableBuilder/MeldChoiceFixtures）**保持不动**，presentation 与 engine 的同名包重叠就此消除。
   - `tools/architecture/BeamSwapLayoutSmoke.java` 三处 import 同步更新；`common`/`common.render` 下 9 个用例引用同步更新。
2. **TsumogiriAIStrategy 迁包** `com.majiang.host.ai` → `com.majiang.ai`（engine/host 空壳随之消除），ServerTableHost import 同步。
3. **client/build.gradle** 删除与 protocol `api` 传递重复的 gson 行。
4. **MahjongGame.render()** `server.tick(busy)` 处补注释：单机权威节奏由 GL 渲染循环驱动、动画回放期间暂停推进属单机 UX 决策，远端不受影响。仅注释。
5. **ArchitectureBoundaryContractTest 加强**：新增源码扫描测试，遍历 `src/main/kotlin/com/majiang/presentation` 全部 .kt，禁止 import `com.majiang.client.`（`client.api.` 除外）、`com.majiang.server./transport./network.` 及 19 个 engine 领域包；扫描根缺失或空集必须失败。

## 验证证据（Codex 独立执行）

- **纪律检查（归一化对比）**：38 个 rename 对剥除 `package`/`import` 行后与 HEAD 逐字节比较，仅 2 文件存在差异且均已核实：ArchitectureBoundaryContractTest 为新增扫描测试（原 3 个反射测试逐字保留），SettlementDetailPresentationAcceptanceTest 为原全限定名 `com.majiang.GdxHeadlessTestRunner()` 改 import 引用（类迁移后的必需等价修正）。其余全部文件断言体零改动，无测试被削弱。
- **残留扫描**：工程内无 `com.majiang.host.ai`、`import com.majiang.screen.`、presentation 侧 `com.majiang.v3.`/`com.majiang.testing.`（Fake/TestUi/TestView）、旧 GdxHeadlessTestRunner 引用残留。engine 自身测试树 `com.majiang.v3`（5 个验收测试）为历史命名，不属本任务范围，列为后续观察项。
- **全量测试 + 桌面编译**：`./gradlew test :lwjgl3:classes` BUILD SUCCESSFUL（EXIT=0），**1054 项 PASSED，0 失败/跳过**（含新增边界扫描测试，实测执行通过）；日志 build/v4-migration/acceptance-arch-cleanup-test.log。
- **Android**：`:android:assembleDebug` BUILD SUCCESSFUL（EXIT=0）；日志 build/v4-migration/acceptance-arch-cleanup-android.log。
- **空目录复查**：七模块源码树 `find -type d -empty` 零残留。

## 遗留事项（后续任务，不在本次范围）

1. protocol 中 `com.majiang.server.api` 与 server 模块包名重叠（契约知情保留），待协议冻结期结束后统一迁入 transport 域。
2. presentation 测试对 engine 的直接依赖（4 个 engine 直连验收测试 + testFixtures），长期应改为经 InProcessServer+ServerConnectionSession 驱动，使 presentation 彻底不依赖 engine。
3. engine 测试树 `com.majiang.v3` 包名（5 文件）与若干测试包命名可后续对齐。
4. 工作区约 40+ 文件的 sleeve-bloom 进行中改动与本任务改动均未提交，建议尽快分层提交。
