# MR-02 第一批独立检视

日期：2026-10-14。**当前结论：MR-02 第一批 R2 通过独立检视与验收，无本批阻断项。MR-02 第二批（结构化错误与重连分类）尚未开始，MR-02 整体未完成。**
基线及本轮结束时 HEAD 均为 `35a87c6fcd22139c21e91316d393350b4bf24000`。本轮仅更新本检视记录，未修改生产源码、测试、保护路径，未 commit/push，未反向调用等待中的 Anti。

## 第一批 R2 本次独立复验（当前有效结论）

### 源码检视与问题关闭

1. `ScheduledExecutorUdpScheduler` 已无 `companion object`、`lastCreatedInstance` 或对应 `init` 赋值；检索 `client/src` 无该全局引用，未引入替代性的全局可变观测钩子或弱引用。保留的 `isShutdown` 只读属性直接读取执行器状态。
2. 公共 `UdpClient.connect` 不含工厂参数，委托同一实现；新增工厂重载及 `connectInternal` 均声明为 Kotlin `internal`。工厂在本次调用的 try 块内按需执行，外部 scheduler 非空时不调用工厂；创建结果仅存于局部变量。工厂异常经同一 catch 转为失败 Future。
3. 内部创建时 `shouldCloseScheduler` 始终为 true，显式 false 不会放弃内部资源所有权；外部注入默认保留调用方所有权，仅显式 true 时转交。失败路径关闭已创建 socket 和应释放的调度器，成功路径使用实际 `activeScheduler` 装配 binding。
4. 正常关闭保留约 2 秒终态 ACK 窗口、调度器与 delayedExecutor 双重收尾及原子幂等保护；接收循环仍捕获 Exception。只读检查 Session 调用方确认其继续传入共享调度器及 `closeSchedulerOnShutdown = false`，底层连接不会提前关闭该会话资源。
5. 两个内部调度器失败用例通过局部 factory 闭包捕获本次实例，在 finally 清理前验证 shutdown 状态及拒绝新任务，finally 再安全释放；正常关闭用例从 `binding.scheduler` 观察实际实例。基线三项有效失败测试仍保留，未放宽断言或删除有效用例。
6. 既有独立用例保持原样，覆盖内部调度器显式 false 后释放、外部共享调度器保留、转交后重复关闭、启动后拒绝关闭调度的兜底和启动拒绝时释放。本轮不另增重复用例。
7. 交付报告已纠正 MR-01 文件名称与未运行 Gradle 的协调原因，并明确第二批尚未实施；此前全局测试状态与报告收敛问题关闭。

本批未发现需返修的问题。编译有非阻断警告，包括本次失败测试中两处多余的 `!!`、其他文件已有 Kotlin 警告及 Gradle 弃用提示；不影响本次构建和测试结果。

### 本次实际串行测试

执行日期为 2026-10-14，时间为 Asia/Shanghai。启动前进程检查仅见 Gradle/Kotlin 守护进程，未见其他 Gradle 客户端或测试 worker；本轮先等待标准测试退出，再启动独立专项。

| 顺序 | 命令 | 实际结果 |
| --- | --- | --- |
| 1 | `gradlew.bat :client:test --console=plain` | 退出码 0，BUILD SUCCESSFUL in 48s；158/158，失败 0、错误 0、跳过 0 |
| 2 | `gradlew.bat -I tools/architecture/v4-improvement.init.gradle :client:test --console=plain` | 退出码 0，BUILD SUCCESSFUL in 33s；18/18，失败 0、错误 0、跳过 0 |

两轮 `:client:test` 均实际执行，并非 UP-TO-DATE 或 FROM-CACHE。已核对本次生成的 JUnit XML，而非复用前轮统计：

- 标准结果：`client/build/test-results/test/TEST-*.xml`，9 个测试类，158 项；`UdpClientFailureTest` 15/15。测试类起始时间为 22:43:35–22:43:36（XML 记录为 UTC 14:43:35–14:43:36）。
- 独立结果：`build/v4-improvement/client/test-results/test/TEST-*.xml`，`PlayerBindingIsolationReviewTest` 13/13、`SchedulerOwnershipReviewTest` 5/5，起始时间为 22:44:28（XML UTC 14:44:28）。
- init 脚本替换独立测试源集及构建目录，因此第二轮是额外的 18 项专项，不是重新运行标准 158 项；本次共验证 176 项客户端测试。没有执行 protocol 测试任务。

### 本次源码与独立测试 SHA-256

以下为当前工作区文件字节的 SHA-256（不做换行归一化），测试前取样、两轮结束后复核一致；旧报告中的指纹仅属于历史版本。

| 文件 | SHA-256 |
| --- | --- |
| `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt` | `98A86EB79E7EFE6FA7F3594430F4FE09FCBE216B13286D9AEB0E19D82FA6AB24` |
| `client/src/main/kotlin/com/majiang/client/transport/udp/ScheduledExecutorUdpScheduler.kt` | `BAB6B0DDBD51C3861FD11E239411C87F0840AB72B106078F66979B8BCD734DCC` |
| `client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientFailureTest.kt` | `CA8C3ABFFA4B833197D044F3211958C9F3E55C4F6726C44EF6668442A768FB76` |
| `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt` | `46EF306D92266D534782A3A974E13A1DD0FA14D9DC2724FD31B341628BD8E449` |
| `client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt` | `4C1C7D970C65C741E873109A7D4C0C7DD248B5AF23DB93878D36961FFB72A523` |
| `tools/architecture/v4-improvement/client/SchedulerOwnershipReviewTest.kt` | `FB9AAD17A0F4E924A59EF403984863793EC24BD2965E4EA66BCE0F71D3D775D1` |
| `tools/architecture/v4-improvement.init.gradle` | `F6E8E3C2983A8B282FD643BFC02F1CE8BB12C07CE9A9DD7B294F63393F6852BB` |

同时复核本批三个源码/测试文件、两个 MR-01 保护文件、lwjgl3 构建与截图入口、`tools/architecture/` 全部现有文件，共 81 个文件，前后 SHA-256 均一致。两个 MR-01 保护文件相对 `35a87c6` 的 git diff 为空；原有工作区修改保留。

### 验收边界

第一批 R2 通过，之前第一批未通过的状态已由本节取代。**第二批结构化错误类型、两种传输映射和重连分类尚未开始，不据此宣称 MR-02 整体完成。** 本轮未执行全模块测试、server 独立审查、双端构建、GL 冒烟或重连资源压力测试；结论限定于上述源码检视与实际测试范围。

下文保留先前验收轨迹，其中测试数据、源码指纹与未通过结论均为 R2 之前的历史记录，不代表当前状态。

## 首次返修后的复验（R2 之前的历史记录）

Codex 已实际串行执行 `gradlew.bat :client:test --console=plain` 和 `gradlew.bat -I tools/architecture/v4-improvement.init.gradle :client:test --console=plain`，两次构建均成功：标准客户端 158/158；独立专项 18/18（MR-01 回归 13 项、调度器所有权 5 项）。新增独立用例通过 binding.scheduler 的调度拒绝行为，确认内部创建调度器即使显式 false 也在关闭窗口后释放。原编译失败、内部资源所有权和拒绝调度阶段问题已修复；接收循环已恢复 Exception 捕获。

本次验证源码 SHA-256：UdpClient.kt 为 `054F3DDE8C8C2CD8BD1CF4EB3700F77B98733BF68DF491954D1536AE99E3B307`；ScheduledExecutorUdpScheduler.kt 为 `C105FA61FE1EA39EBCB230196ECC16A6CA153F58249AE05B5AEFE78AC8126D69`。

尚待收敛：返修新增 `ScheduledExecutorUdpScheduler.lastCreatedInstance` 全局强引用，仅供失败路径测试查找实例，会保留最近执行器且可能被并发连接覆盖。要求改为调用级 internal 工厂注入，由各测试局部捕获本次创建资源，不在生产类保存全局测试状态。已准备第二轮任务 `tools/architecture/v4-improvement/ANTI-MR02-R2-TASK.txt`。交付报告还误写 MR-01 文件名称，需一并修正。

因此第一批功能测试通过，但最终检视尚未关闭；第二批结构化错误和重连分类未实施，MR-02 整体未完成、未提交或推送。本轮未执行完整 server 独立审查、双端构建、GL 冒烟或多次重连资源负载验证。

## 首次交付问题（历史记录）

1. **测试编译失败**：`UdpClientFailureTest.kt:216` 引用不存在的 `binding.isClosed`。实际执行标准客户端专项命令失败于 `:client:compileTestKotlin`，没有任何测试开始运行。应通过公开行为/资源释放断言验证重复关闭，不为测试添加无必要的生产 getter。
2. **内部调度器仍可能无所有者**：`UdpClient.kt` 的 `closeSchedulerOnShutdown ?: (scheduler == null)` 允许未注入 scheduler 却传 `false` 时，内部创建的调度器永不关闭。调用方拿不到它，无法代为释放。内部创建必须始终由连接释放；外部注入时才允许选择保留或明确移交。
3. **拒绝调度测试触发阶段错误**：Anti 的 RejectingScheduler 从第一次 schedule 就拒绝，连接会在启动心跳时失败，不能取得 binding 后验证关闭兜底。Codex 独立用例已确认启动拒绝导致 ExecutionException，原因是 RejectedExecutionException。关闭兜底用例应在连接成功后才切换为拒绝调度。
4. **正常关闭测试过早断言**：仅 sleep 100 ms，尚未到约 2 秒关闭窗口，无法证明关闭任务不会关闭外部调度器。必须实际触发或等到关闭任务执行；异常路径使用 finally 释放资源。默认调度器失败用例仅断言 Future 异常，不能据此宣称资源不泄漏，应补充可观察的资源释放验证。

另：接收循环由 Exception 扩大为 Throwable 与所有权改造无关，会静默吞掉严重 JVM Error 后继续循环，要求撤回该无关扩张。清理路径如需 finally，应明确设计，不能笼统吞掉所有 Throwable。

## 首次交付时已完成验证（历史记录）

```powershell
.\gradlew.bat :client:test --tests 'com.majiang.client.transport.udp.UdpClientFailureTest' --console=plain
```

结果：编译失败，见第 1 项。

```powershell
.\gradlew.bat -I tools/architecture/v4-improvement.init.gradle :client:test --tests 'com.majiang.review.SchedulerOwnershipReviewTest' --console=plain
```

结果：4/4 通过。独立测试位于 `tools/architecture/v4-improvement/client/SchedulerOwnershipReviewTest.kt`，验证实际执行关闭任务后外部调度器仍保留、明确移交后的幂等关闭、成功启动后拒绝关闭调度时的延迟兜底，以及启动即拒绝时的失败释放。这四项不覆盖内部调度器显式 false 的漏洞，不代表 MR-02 验收通过。

下一步：Anti 修复上述问题并补足模块测试，Codex 串行执行标准客户端、独立专项及必要回归。第一批通过后再实施共享结构化错误类型、两种传输映射和重连分类；MR-02 尚未提交或推送。
