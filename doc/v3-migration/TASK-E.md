# E：模块迁移脚本与构建边界

当前 D/C2/B2 正在各自范围修改生产。你不能移动或改写它们的源码。任务只输出工具脚本与模块Gradle结构，Codex检视后在他们完成时执行机械迁移。

允许写：engine/build.gradle、client/build.gradle、server/build.gradle、settings.gradle、core/build.gradle、tools/architecture/migrate-v3.ps1、doc/v3-migration/delivery-E.md。不要运行任何shell，不修改测试内容或其他生产代码，不升依赖版本。新module Gradle固定复用root kotlinVersion/javaVersion/JUnit现有版本。

## Gradle布局

- engine应用java-library、java-test-fixtures、kotlin.jvm。仅Kotlin stdlib生产依赖，无libGDX。测试JUnit5；testFixtures实现TestTableBuilder等需要testFixturesApi JUnit或合理隔离。
- client应用java-library/kotlin.jvm，api project(':engine')；测试JUnit5与testFixtures(engine)。
- core保留libGDX与资源，api project(':engine')、api project(':client')；测试加testFixtures(engine)。
- server应用java-library/kotlin.jvm，依赖engine/client（后续网络codec会在client/transport包），无UI依赖；JUnit测试，testFixtures(engine)。暂不写不存在mainClass运行任务。
- settings加入engine/client/server。

## 迁移脚本

PowerShell唯一操作体系。默认dry-run，显式-Apply执行。根路径固定脚本../..解析为D:/majiang，使用GetFullPath/Resolve-Path确保所有源/目标都在该根下；单文件Move-Item -LiteralPath，不使用递归删除/跨shell拼命令。禁止覆盖不同内容目标，源已移动时可幂等跳过。生成文件清单与迁移前后hash到build/v3-migration，核验字节不变。空目录不必清除。

1. 核心原有package路径保持不变，只有module物理迁移：
 engine/src/main/kotlin/com/majiang 下迁移core的model/state/round/match/rules/settlement/event/ai/cheat/application/session/economy/timing/control/host/access所有文件；以及client目录中旧PlayerEndpoint.kt/LocalPlayerEndpoint.kt（兼容路径暂在engine，避免application.LocalTableRuntime循环依赖）。
 client/src/main/kotlin/com/majiang 下迁移sync/**与client/GameClient.kt；后续网络/transport代码直接写client模块不靠该脚本移动。
 core仅剩MahjongGame.kt、screen/**、render/**和后续UI adapter。
2. 测试fixtures：core/src/test/kotlin/com/majiang/testing/**迁到engine/src/testFixtures/kotlin/com/majiang/testing，供全部模块测试共用；确认这些fixture无libGDX/UI imports。
3. 测试自动归属（仅机械移动，不改断言）：
 - 引用com.badlogic、GdxHeadlessTestRunner、com.majiang.screen、com.majiang.render、com.majiang.MahjongGame的测试留core；GdxHeadlessTestRunner.kt本身也留core。
 - 引用com.majiang.client.GameClient、com.majiang.sync的测试移client。
 - 其余纯引擎、领域、host测试移engine（现有反射边界test若importScreen也必须留core）。
 - 所有源码相对package路径不变；ArchitectureDependencyAcceptanceTest移engine后src/main路径自然有效。
4. 脚本扫描源目录时当时不存在host也不能静态漏掉后续D文件；Apply执行时重新生成清单。禁止把所有测试重复拷到多个module来掩盖依赖错误。
5. 原系统安全/规则测试不得删除，迁移后应可运行 `gradlew test :lwjgl3:classes :android:assembleDebug`。

交付报告记录模块依赖与脚本预期，明确脚本尚未由你执行。后续兼容代码清理另一个任务完成，不在此波擅自删除。
