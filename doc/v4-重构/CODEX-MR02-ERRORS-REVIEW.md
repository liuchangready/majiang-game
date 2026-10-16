# MR-02 第二批独立验收：结构化错误与重连分类

补充（2026-10-15 01:08）：下文记录的重连资源基线缺口已由独立实测关闭，见 [MR-02 资源验收](ACCEPTANCE-MR02-RESOURCES.md)。新增 1 项场景通过，玩家/观战各 3 次真实断网恢复，全部关闭后 UDP 端点回到 0、存活 Java 线程回到 13 且无新增线程 ID 残留。MR-02 两批功能及计划内资源验收现已完成；未提交、未推送。下文 1185 项仍指先前功能验收，不将本次补验冒充整套重跑。

## 第三轮最终独立验收（2026-10-15，Asia/Shanghai）

**当前结论：MR-02 第二批通过本轮源码检视与测试验收；R1–R3 及第二轮新增编译阻断均已关闭。本轮最终有效结果为 1185/1185，通过率 100%，失败、错误、跳过均为 0。** 此结论取代下方前两轮的未通过状态；前两轮全文保留为历史。第一批报告 `CODEX-MR02-REVIEW.md` 不作修改，其历史通过结论继续有效。MR-02 整体资源基线验收的证据边界见本节末尾，不能将第二批通过扩写为所有计划验收项均已完成。

验收者：Codex。HEAD 为 `35a87c6fcd22139c21e91316d393350b4bf24000`，受检对象为该提交加当前未提交工作区。此次独立阅读生产实现、标准测试与独立探针，重新解析磁盘上的 JUnit XML，核对日志中的实际测试任务、结束标记和退出码，并计算原始文件 SHA-256；未仅采纳交接摘要。本次报告阶段复核既有五组完整运行证据，并亲自串行补跑服务端独立验收，未将此前所有执行归为本次亲自运行。

### 证据纠正与补验

交接摘要所列六组最终凭据中，服务端独立验收存在一处实质性证据缺口：`majiang-mr02-r3-08-server-review-final.log` 停在 `closingRuntimeEventuallyReleasesItsUdpReceiver() PASSED`，仅有 196 条 PASSED，没有 `BUILD SUCCESSFUL` 或退出码；核查时相关 Gradle 客户端/测试 worker 已不存在。该文件 SHA-256 为 `DD2BEF409B7DC471474E9BD4F41B7BF072D490843B3A43B6689E15938DA79022`。当时磁盘上的 197 项 XML 起始时间为 UTC 16:43:13.475–16:43:52.957，对应较早的 `05-server-review.log`，不能冒充 `08` 的最终结果；也不能据截断日志断言最后一个测试失败或推定进程退出原因。

为补齐当前源码对应的完整证据，在确认没有其他 Gradle 客户端或测试 worker 后，Codex 单独执行：

```powershell
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true -PreviewStage=mr02-errors :server:test --rerun-tasks --console=plain
```

结果为 **BUILD SUCCESSFUL in 2m 25s，进程退出码 0，12 个任务实际执行，197/197**。完整日志为 `%TEMP%/majiang-mr02-r3-09-codex-server-review-final.log`，末尾另记录捕获的 `CODEX_EXIT_CODE=0`。下表及总数使用此次补跑的 XML，替代 `08`，不重复累计 `05` 或补跑次数。

本轮其他历史执行也保留明确区分：

- `02-client.log` 的 `:client:test` 为 UP-TO-DATE，不作为实际执行；使用 `02b-client-rerun.log` 的强制重跑结果。
- `03-server.log` 为 156 项、154 通过、2 失败，失败项是 `UdpRoomTest.testResumeSessionDisplacesOldConnection` 和 `testSpectatorJoinResumeAndCheckpointResend`，均是恢复失败消息未满足既有 `INVALID_TOKEN` 文本断言。当前 `UdpRoom.resume/resumeSpectator` 对 typed INVALID_TOKEN 重新包装为同 code、兼容消息并保留 cause；测试文件未修改。`07-server-final.log` 已对修复后标准 server 完整运行并通过，`UdpRoomTest` 为 18/18。
- `05-server-review.log` 是较早一次完整的 197/197（BUILD SUCCESSFUL，1m28s），不是截断的 `08` 的结束凭据。其历史 XML 与本次新 XML 不混用。

### 源码独立检视与问题关闭

| 项目 | 本轮核对结果 | 结论 |
| --- | --- | --- |
| R1：标准测试编译与有效断言 | 客户端桩已按真实接口返回 `CompletableFuture<ProtocolAccessCapabilities>`，使用现有 DTO 构造并补齐 `ProtocolPlayerView/ProtocolMeld` 导入；不再引用其他模块的测试桩。服务端测试使用真实 `CreateRoomRequest/ResumeRequest/ApiRequest`，UDP 测试收包支持 Fragmenter 重组。标准 client/server 均实际编译并执行。 | 关闭 |
| R2：服务端防止文案误判 | `UdpRoom.mapError` 逐层解包，IOException 映射 INTERNAL_ERROR，typed code 与 frame.reasonCode 优先于文本；兼容文本只作完整等值匹配。两个恢复包装均保护 IOException，typed INVALID_TOKEN 保留 cause；大厅 create/join/resume 使用共享映射。IOException 导入已补齐，不存在的 `ApiErrors.SESSION_TIMEOUT` 引用已移除，历史字面原因映射为 INTERNAL_ERROR，未新增协议错误码。 | 关闭 |
| R3：异步业务错误结构化 | `ServerTableHost` 七个查询的 validator 失效均携带 INVALID_TOKEN/STALE_SESSION；进程内七个查询出口接入 `mapInProcessErrors`，保留已有 typed 异常及 IOException，普通异常转换保留 root cause。观战绑定竞争及 UDP 六个查询入口的失效分支已结构化；validator、代次比较、权威队列和关闭顺序保留。 | 关闭 |
| 共享错误类型与客户端分类 | `ServerApiException` 位于 protocol，继承 IllegalStateException，直接保留 message/cause；client 虽引用 `com.majiang.server.api` 包名，实际依赖仍来自 protocol，未增加 server/engine 反向依赖。客户端分类先解包，再按 IOException、code、reasonCode 和完整历史原因判断，无子串永久终止判定。UDP 无 receipt 的业务拒绝使用共享类型，动作 receipt 路径保留。 | 通过 |
| 有界重连与第一批保护 | 玩家及观战 resume 失败分支接入分类器；第 10 次失败后不再调度，终止原因为 RECONNECT_ATTEMPTS_EXHAUSTED。标准集成测试及独立生产 UDP binding 探针验证 500/1000/2000/4000/8000×6 ms、10 次尝试、失败绑定关闭和任务清空。第一批所有权实现及独立断言保留。 | 通过 |

独立失败探针没有被删除或放宽：`Mr02ErrorMappingReviewTest` 六项本次全部通过，覆盖 nested typed code、frame reason、IOException 文案、未知错误子串、历史完整原因及排队 snapshot 在 close 后的 typed STALE_SESSION。两份 MR-02 独立测试源码哈希与首轮一致。领域 engine 无本批源码修改；未通过修改规则、136 张物理守恒或模块依赖规避验收。

### 最终有效测试结果

统计同时核对 testsuite 属性与实际 testcase 节点数；每组失败/错误/跳过均为 **0/0/0**。六组共 135 个测试类、1185 项：标准四模块 967 项，独立专项 218 项。独立 init 脚本替换测试源集及输出目录，218 项是额外专项，不能把其通过替代标准模块测试。

| 组别 | 测试类 | 实际用例 | 通过 | 完整日志后缀（均以 `majiang-mr02-r3-` 开头） | BUILD SUCCESSFUL 耗时 / 退出码 |
| --- | ---: | ---: | ---: | --- | --- |
| protocol | 18 | 171 | 171 | `01-protocol.log` | 35s / 0 |
| client | 11 | 170 | 170 | `02b-client-rerun.log` | 1m10s / 0 |
| server | 23 | 156 | 156 | `07-server-final.log` | 1m / 0 |
| client review | 3 | 21 | 21 | `04-client-review.log` | 48s / 0 |
| server review（Codex 补验） | 22 | 197 | 197 | `09-codex-server-review-final.log` | 2m25s / 0 |
| engine | 58 | 470 | 470 | `06-engine.log` | 1m4s / 0 |
| 合计（每组最终运行计一次） | 135 | **1185** | **1185** | | |

日志内命令已核对：protocol、client 重跑和 engine 均使用 `--rerun-tasks --console=plain`；标准 server 为 `:server:test --console=plain`；client review 使用 `-I tools/architecture/v4-improvement.init.gradle :client:test --console=plain`。最终六组的目标 test 任务均实际执行，无 UP-TO-DATE/FROM-CACHE 冒充运行。

专项拆分：client 包含分类器 **8/8** 与会话集成 **4/4**；server 包含双传输一致性 **5/5** 与 UdpRoom **18/18**；client review 为 MR-01 隔离 **13/13**、scheduler 所有权 **5/5**、MR-02 重连 **3/3**；server review 为 MR-02 映射 **6/6** 加既有 V4 专项 **191/191**，包含真实 UDP 两次恢复和接收线程退出。以上子集已计入表中，不额外加总。

JUnit XML 的 `timestamp` 为各测试类起始时刻，以下均为 **2026-10-14 UTC**，不是测试结束时间；换算本地为 **2026-10-15 Asia/Shanghai**：

| 组别 | 实际 XML 目录（相对仓库根） | 类起始时间范围（UTC） |
| --- | --- | --- |
| protocol | `protocol/build/test-results/test/` | 16:39:15.346–16:39:16.290 |
| client | `client/build/test-results/test/` | 16:40:31.575–16:40:32.237 |
| server | `server/build/test-results/test/` | 16:46:30.857–16:46:46.898 |
| client review | `build/v4-improvement/client/test-results/test/` | 16:42:29.492–16:42:30.128 |
| server review（补验） | `build/v4-migration/review-mr02-errors/server/test-results/test/` | 16:52:58.628–16:53:38.274 |
| engine | `engine/build/test-results/test/` | 16:45:32.767–16:45:36.459 |

### 凭据 SHA-256

日志均位于 `%TEMP%`。六组最终日志和 XML 已另存到 `%TEMP%/majiang-mr02-r3-codex-final-evidence/` 各组子目录，统计保存为该目录下 `majiang-mr02-r3-codex-verified.json`，避免后续 Gradle 执行覆盖当前凭据。文件哈希按原始字节计算，不归一化换行。XML 集合指纹算法：取目录内全部 `TEST-*.xml`，以 PowerShell `Sort-Object Name` 排序，每个文件生成 `文件名<TAB>大写SHA256<LF>`，拼接后按 UTF-8 无 BOM 计算 SHA-256；不包含 binary 子目录。

| 组别 | 完整日志 SHA-256 | XML 集合 SHA-256 |
| --- | --- | --- |
| protocol | `A93B7BCB79C9E5349E9A508858C098FB9C3029934142B6274FF22BFA11B284A0` | `3B0A7BE7816FD1594760FF6F691160EACEF297D46680527AB4D8398DF1324271` |
| client | `6D0C4AA3B71E344B61B00B951212979626550719E8F6D1D05A260E7DB45DC3E3` | `594716D295027F357CA1B1CAE418A73E0E422D1CCEA8976781BF4F5F1DFC7162` |
| server | `002E4BE9F6369823018D3D3A7CD21BA5461D6226401A51C81D426358770A9A8E` | `353841D575A9BF9B655CE4656F1E8A11507C18306556FEFC7FEEB5142E442353` |
| client review | `9B311C2C722544CFF8D7FCC75CF97E86A0756DDFFC3389C3937B47B95C7927C1` | `523E576C384D2FC2CF1779A7506A7CC50C47C209D2CA4CD2F39D9AAC45D20F92` |
| server review（补验） | `70C6A5401CE55ECE16A6D4C2DCAED56AD66DE514231ED792FD24E26974EABC66` | `8976EED1159D1506BAD640419EA75E5F971D1595FA6F3759F8DB21224834C0B7` |
| engine | `4FD5425DEB1A6DC94AC841B86AFDDC95FBDF5D1F1C7606F0C97ED950C8D03183` | `1F2D87C1313DE5FFA9F98849297D364D9A41081072EA57DB3AA0BE893E80BDE3` |

关键受检源码与测试指纹（文件名按所属模块定位）：

| 文件 | SHA-256 |
| --- | --- |
| protocol `ServerApiException.kt` | `B5EB80399E11EF04A8D39EEB62BC8615253278C22EBF0B113573105DA627E0C0` |
| client `ReconnectErrorClassifier.kt` | `B26B1D66D4655767438E1566C64F990EA9AAF371CC9D7A49EBC433A3AFC7ED37` |
| client `ServerConnectionSession.kt` | `1EFA0E6B3F2D122FA2A8C8311FBB47AAF8B7110FEB6EEB0EC3A6A6A10E7ADC24` |
| client `UdpClientBinding.kt` | `7129AB9CBC27158F48DBED655B484F5F149E1F54E1A43C2A2B58F0BDDD3014E5` |
| server `SharedRoom.kt` | `E47AE96D5F6A77C51385B78E1B7D3153DCC04B140B40706D290D80E8FB081AFA` |
| server `RoomRegistry.kt` | `BDA5A6DC04A13F63E12A3145584BF28927C807771076B82F40DB4F1826B67E59` |
| server `ServerTableHost.kt` | `8D0B9FEA0ADD28242430792DD1D0993F5C92163DB0DB62E740530330ABBB4D61` |
| server `InProcessBinding.kt` | `20D6679701BE0A7B41D5103415160A90FBDD4262373E69022FD8353D9963B1F1` |
| server `UdpRoom.kt` | `B860A4B54436A3A2C61A129E8A4D4E9660E4F26F4EF584F746B83DF4D1ACE20F` |
| server `UdpTableServer.kt` | `C3AA0B23B27E6AE9DB277B93FAC622DFBEBC8425F64891D14B1E02007AECDDD6` |
| client `ReconnectErrorClassifierTest.kt` | `2819F6964FD55B44CD6FA20E44AB43F34229D35E98C6643EABACD28390BE5CF8` |
| client `ServerConnectionSessionReconnectClassificationTest.kt` | `B4577D73A02D45F5ADF8386E2FC0E483A57E956D128DD08D858E7AB2698F9564` |
| server `DualTransportErrorCodeConsistencyTest.kt` | `54B31C61FD04C71CEB6224B20E6AA2C393AD48E7552E03BB54359433FCF393E1` |
| server `UdpRoomTest.kt`（原断言保留） | `5B1C14630C572310835D520A6122FFB6C61CFE9DAB022C575C3D22CBF699B2E7` |
| 独立 `Mr02ReconnectReviewTest.kt` | `AB2980BEE958F8F82143E6806B1DEB9BA02573A280357F0D6229AB9035ACAB8D` |
| 独立 `Mr02ErrorMappingReviewTest.kt` | `D2185C170B5B15F85081301EC9CD5E09431862E130DC21641317CA0D04022C3E` |

### 工作区保护与结论边界

第三轮最初的 761 文件清单 `%TEMP%/majiang-mr02-r3-baseline.json` 与本次接手状态相比，已有 `UdpRoom.kt` 和 `ANTI-PROGRESS.md` 两处差异，不能声称第三轮全过程所有源码都未变。当前 UdpRoom 为上述最终哈希；标准 server 最终运行及 Codex 补验均针对修复后状态。本次补验及报告阶段重新记录 761 个既有文件哈希，只更新本报告，不代修生产或测试源码，不 commit/push；第一批报告、MR-01 保护逻辑、第一批调度器、独立测试及 lwjgl3 既有修改均保留。第一批报告 SHA-256 保持 `EA4B47D8097D0C72C2AF05FD2EF7AB49B2B9540CDC608DA9AACD1772B91AEC69`。

**第二批最终结论为通过，无本批待返修阻断项。** 本轮没有执行 Android/桌面打包或 GL 冒烟，不能据此声明双端构建通过。真实 UDP 两次恢复及实际接收线程退出已有回归证据，但未测量多次重连后完整线程/Socket 数回落到基线；`IMPROVEMENT-PLAN.md` MR-02 中该项整体资源验收仍缺专门凭据。本报告只关闭第二批已列问题及六组测试验收，不据 1185 项通过自动签署该额外资源基线条件。

---

以下为第二轮及首轮历史原文；其中“最新”“当前”均指当轮时点，当前有效结论以第三轮为准。

## 第二轮独立复验（2026-10-15，Asia/Shanghai）

**最新结论：未通过。R1 仍有编译阻断，R2 返修又引入服务端生产源码编译错误；R2/R3 只能确认源码修改，不能认定运行验收通过。MR-02 第二批及整体仍不得标记完成。** 第一批验收报告不变；下方首轮记录保留为历史，不作为本轮测试结果。

本轮基线仍为 `35a87c6fcd22139c21e91316d393350b4bf24000` 加交接时已有工作区。Codex 独立核对源码、真实协议接口、测试和日志，按交接顺序串行执行六条命令，并对命中 UP-TO-DATE 的客户端独立专项补做强制重跑。未代修生产或测试源码，未 commit/push，未反向调用 Anti。

### 本轮阻断项

1. **R1 / P1，客户端标准测试仍无法编译。** `client/src/test/kotlin/com/majiang/client/session/ServerConnectionSessionReconnectClassificationTest.kt:181–182` 的新桩声明 `capabilities(): ProtocolAccessCapabilities`，而 `protocol/.../SessionApi.kt:38` 要求 `CompletableFuture<ProtocolAccessCapabilities>`；构造参数 `reconnectSupported` 不存在，真实字段为 `ProtocolAccessCapabilities.resumption`（`ProtocolModels.kt:378`）。第 247 行使用 `ProtocolPlayerView` 却没有导入，其后 260/261 行集合类型推导也连带报错。实际 `:client:compileTestKotlin` 失败，分类器 8 项和会话集成 4 项本轮均未进入标准模块测试执行。应对齐 Future 返回签名、使用真实 DTO 字段并补齐导入，然后运行整个标准 client 测试；不得用独立源集通过替代。

2. **R2 / P1，服务端生产源码无法编译。** `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt:380,630,1180` 使用 `IOException`，但缺少 `java.io.IOException` 导入；第 1196 行引用 `ApiErrors.SESSION_TIMEOUT`，当前 `protocol/.../ApiErrors.kt` 并无该常量（左右两处引用均报错）。标准 `:server:test` 与隔离独立验收均失败于 `:server:compileKotlin`，尚未到测试源码编译或执行。应补齐导入，按现有错误码契约处理该不存在的分支，不应仅为消除编译错误擅自增加协议码。当前不能声明 `DualTransportErrorCodeConsistencyTest` 已编译通过，更不能声明两个映射探针及队列 STALE_SESSION 探针“确定性通过”。

### 返修逐项核对

| 首轮问题 | 本轮源码核对 | 验收状态 |
| --- | --- | --- |
| R1：桩、DTO 与断言 | 已移除跨模块测试桩导入，改为三个文件内桩；第 4 项显式断言 500/1000/2000/4000/8000×6 ms、10 次替换连接、全部关闭、耗尽原因及任务清空。服务端测试已改用 `playerName`、正确 `ResumeRequest` 与 `ApiRequest` 参数。交付说明确为 8 + 4 + 5 个测试方法。 | 未关闭；上述新编译错误仍阻断运行。 |
| R2：服务端防误判 | `mapError` 逐层解包、保护 IOException、提取 typed code/reasonCode，并对 trim 后的完整历史原因做等值匹配；两个 resume 包装也已保护 IOException，未再按子串匹配。六个查询入口的 STALE_SESSION 已结构化。 | 源码修改已确认；生产编译失败，6 项独立映射测试未运行。 |
| R3：进程内异步结构化 | `ServerTableHost` 七个查询的 validator 失败均使用 INVALID_TOKEN；`mapInProcessErrors` 接到七个查询出口，保留已有结构化异常与 IOException，普通异常映射保留 root cause；观战绑定竞争的 DISPLACED/ALREADY_IN_ROOM 分支已结构化。 | 源码修改已确认；队列关闭探针及其他服务端回归被编译阻断，尚不能签署通过。 |

检查的修改保留了原有 validator 调用、队列调度、绑定代次比较和关闭流程；本轮没有通过移除校验或放宽独立断言规避问题。客户端生产重连分类的实际复跑结果见下表。

### 本轮串行执行记录

启动前仅见 Gradle/Kotlin 守护进程，没有其他 Gradle 客户端或测试 worker。每条命令均等待上一条退出后启动。日志位于 `%TEMP%/majiang-mr02-r2-*.log`；耗时取 Gradle 输出。

| 顺序 | 命令 | 实际结果 |
| --- | --- | --- |
| 1 | `gradlew.bat :protocol:test --rerun-tasks --console=plain` | 退出 0；33s；171/171，18 类，失败/错误/跳过均 0。日志 `01-protocol`。 |
| 2 | `gradlew.bat :client:test --console=plain` | 退出 1；11s；`compileTestKotlin` 失败，未执行测试。日志 `02-client`。 |
| 3 | `gradlew.bat :server:test --console=plain` | 退出 1；7s；生产 `compileKotlin` 失败，未执行测试。日志 `03-server`。 |
| 4 | `gradlew.bat -I tools/architecture/v4-improvement.init.gradle :client:test --console=plain` | 退出 0；40s；测试任务 UP-TO-DATE，不计作本轮实际运行；由第 7 步补验。日志 `04-client-review`。 |
| 5 | `gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true -PreviewStage=mr02-errors :server:test --console=plain` | 退出 1；11s；同一生产 `compileKotlin` 失败，未执行独立服务端测试。日志 `05-server-review`。 |
| 6 | `gradlew.bat :engine:test --rerun-tasks --console=plain` | 退出 0；1m1s；470/470，58 类，失败/错误/跳过均 0。日志 `06-engine`。 |
| 7 | `gradlew.bat -I tools/architecture/v4-improvement.init.gradle :client:test --rerun-tasks --console=plain` | 退出 0；55s；21/21，3 类，失败/错误/跳过均 0：MR-01 隔离 13 项、scheduler 所有权 5 项、重连 3 项。日志 `07-client-review-rerun`。 |

本轮 JUnit XML 核对（时间为 UTC；本地为 2026-10-15 凌晨）：

- `protocol/build/test-results/test/TEST-*.xml`：2026-10-14 16:17:14.355–16:17:15.462。
- `engine/build/test-results/test/TEST-*.xml`：2026-10-14 16:20:43.786–16:20:46.857。
- `build/v4-improvement/client/test-results/test/TEST-*.xml`：2026-10-14 16:21:47.684–16:21:48.336，三类均为本轮强制重跑结果。
- 标准 client/server 目录残留 XML 起始时间分别为 14:43:35.470、11:19:09.812；服务端独立目录仍为首轮 16:03:25.967–16:04:05.460 的 197 项、3 失败。它们不是本轮执行结果；既不据此宣称本轮通过，也不把首轮 3 项失败冒充本轮重新失败。

补核首轮末尾 engine 记录：原 `%TEMP%/majiang-mr02-engine.log` 为 BUILD SUCCESSFUL、58s、退出 0；补跑前 XML 为 470/470、58 类，失败/错误/跳过均 0，UTC 16:05:59.906–16:06:03.058。此处补全历史“待末尾核实”，与本轮第 6 步分开记录。

### 工作区保护与边界

交接时对 git 跟踪及未忽略的 761 个既有文件建立 SHA-256 清单（`%TEMP%/majiang-mr02-r2-baseline.json`）。本轮仅更新本报告，其余既有文件字节保持不变，未新增验收源码；MR-01、第一批调度器文件、Anti 交付源码、已有独立测试和第一批报告均保留。

两份独立测试的哈希与首轮完全一致：

- `Mr02ReconnectReviewTest.kt`：`AB2980BEE958F8F82143E6806B1DEB9BA02573A280357F0D6229AB9035ACAB8D`。
- `Mr02ErrorMappingReviewTest.kt`：`D2185C170B5B15F85081301EC9CD5E09431862E130DC21641317CA0D04022C3E`。

本轮受检返修版本：

| 文件 | SHA-256 |
| --- | --- |
| server `UdpRoom.kt` | `0D1F988179FED8FD90755C262F0D24541932B6410D9D6EB846DC1025A4135BEA` |
| server `InProcessBinding.kt` | `20D6679701BE0A7B41D5103415160A90FBDD4262373E69022FD8353D9963B1F1` |
| server `ServerTableHost.kt` | `8D0B9FEA0ADD28242430792DD1D0993F5C92163DB0DB62E740530330ABBB4D61` |
| client `ServerConnectionSessionReconnectClassificationTest.kt` | `75C7348AFC7B4B1415EEB24995F36CDDBCD222C7F91D70CCDEB6EA5066E81EE5` |
| server `DualTransportErrorCodeConsistencyTest.kt` | `314A56E9546145BD2733641514B27C052B4CF77190602934A8B1ADA0AB6B48C1` |

本轮未执行 Android/桌面打包、GL 冒烟或长时资源压力测试；未取得本轮服务端运行回归结果。返修后必须重新运行标准 client/server 测试及原有独立服务端验收，保留现有断言与模块边界。

---

## 首轮验收记录（历史，最新结论见上）


**结论：未通过，退回 Anti 返修。MR-02 第二批及整体不得标记完成。** 第一批 R2 的历史验收结论保留，不覆盖 `CODEX-MR02-REVIEW.md`。

- 验收者：Codex；执行时间：2026-10-14 深夜至 2026-10-15 凌晨（Asia/Shanghai）。
- 基线：`35a87c6fcd22139c21e91316d393350b4bf24000`，加交接时已有的未提交修改。
- 依据：`AGENTS.md`、`IMPROVEMENT-PLAN.md` 的 MR-02 契约、`ANTI-CODEX-HANDOFF.md`、本批交付报告和进度文件。
- 本轮独立阅读本批生产源码、测试、实际 DTO 与上下游调用链；没有仅采纳开发者自报。未修改生产实现或 Anti 的测试，未 commit/push，未反向调用等待中的 Anti。

## 需要返修的问题

### R1 / P1：两个标准模块测试均无法编译

实际运行 `:client:test` 和 `:server:test`，分别失败于 `compileTestKotlin`；两轮均没有进入测试执行，不能引用旧 XML 宣称标准模块通过。

1. `client/src/test/kotlin/com/majiang/client/session/ServerConnectionSessionReconnectClassificationTest.kt:5,10,11,29,33` 引用不可见或不存在的 `com.majiang.server.api.FakeServerConnection`、`com.majiang.transport.udp.FakeUdpClock`、`FakeUdpScheduler`。protocol 的测试源码不会由正常模块依赖导出；现有 client 测试桩在 `ServerConnectionSessionTest` 内，也不是本文件导入的类型。不得把测试桩搬入生产包或破坏模块依赖来消除编译错误。
2. `server/src/test/kotlin/com/majiang/server/inprocess/DualTransportErrorCodeConsistencyTest.kt:161,187,210` 使用不存在的 `seat` 参数；第 177/226/277 行使用不存在的 `CreateRoomRequest.hostPlayerName`；第 178/227/278 行将领域 `MatchConfig` 传入要求 `ProtocolMatchConfig?` 的接口。当前 `CreateRoomRequest` 参数是 `playerName`，`ResumeRequest` 不接受 seat，线路 `ApiRequest` 也没有这些调用中的 seat 参数。应按当前 V4 DTO 修正测试。
3. 会话测试即使修复编译，`:222` 仍断言耗尽原因为 `SERVER_BUSY`，而生产 `scheduleReconnect` 在 attempt 10 入口终止为 `RECONNECT_ATTEMPTS_EXHAUSTED`。本轮独立 UDP 用例已验证后者；需对齐交付时声明的终止契约，并显式断言实际次数、500/1000/2000/4000/8000ms 封顶退避及失败连接释放，不能只循环调度任务。

交付说明称分类器 7 项，实际源码为 8 个 `@Test`；会话测试的方法名、最终原因也与交付说明不完全一致。返修后同步纠正文档与真实执行结果。

### R2 / P1：服务端仍按文案子串制造永久错误码

位置：`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt:1170–1189`；同类兜底也存在于 `resume` 第 380 行、`resumeSpectator` 第 627–631 行。

`mapError` 虽优先读取结构化 code/reasonCode，但随后仍对任何普通异常（包括 IOException）执行 `contains`。客户端改成按 code 分类之后，这个上游误分类会成为可信的永久业务拒绝，客户端无法恢复原始的临时故障身份。

独立失败用例（已实际运行）：

| 输入（外层另有 CompletionException/ExecutionException） | 期望 | 实际 |
| --- | --- | --- |
| `IOException("temporary upstream failure while checking INVALID_TOKEN")` | `INTERNAL_ERROR`，不得因文案变成永久 token 拒绝 | `INVALID_TOKEN` |
| `IllegalStateException("temporary failure while processing ROOM_CLOSED notification")` | `INTERNAL_ERROR`，未知错误可有界重试 | `ROOM_NOT_FOUND` |

这是映射函数的确定性故障注入复现，不声称已经发生真实线上故障。实际生产大厅及会话错误路径会调用该函数，且客户端把这两个输出码列为永久错误；因此只修客户端 contains 不足以满足端到端防误判契约。

返修要求：保留结构化类型优先与逐层解包；普通异常兼容仅接受明确的完整历史原因；IOException 不得依赖文本映射成永久业务码。同步审计上述 resume 两处包装，避免先把临时异常转换为 `ServerApiException(INVALID_TOKEN)` 后再绕过安全兜底。未知异常使用非永久的默认映射，不添加新的模糊关键字判断。

### R3 / P2：进程内异步错误没有统一为结构化业务异常

位置：`InProcessBinding.kt:849–851`、`ServerTableHost.kt:791–818`；类似直传还存在于 decision/resume/public 查询及绑定竞争分支。

本批主要替换了绑定方法入口的 `completeExceptionally(IllegalStateException(...))`，但异步房间 Future 的错误直接透传。确定性复现步骤：创建进程内房间 → 用 latch 暂停权威队列 → 提交 `connection.snapshot()` → 关闭连接使 validator 失效 → 释放队列。实际解包得到 `java.lang.IllegalStateException: STALE_SESSION`，不是携带 `INVALID_TOKEN` 的 `ServerApiException`。

UDP 的同一历史原因由 `UdpRoom.mapError` 转为 `INVALID_TOKEN`，而进程内调用者拿不到稳定 code。交付报告“所有失败 CompletableFuture 统一包装”不成立，也不满足双传输一致性目标。

返修要求：在合适的共享业务抛错点或进程内 API 出口统一异步错误转换；保留 cause，已有结构化错误保持原 code，IOException 保留传输身份。检查绑定竞争中 `DISPLACED_BY_NEW_CONNECTION`、`ALREADY_IN_ROOM` 以及查询的 STALE_SESSION 分支；不得修改 MR-01 的代次校验、关闭顺序和权威队列语义来绕过异常。

## 已确认有效的实现

- `ServerApiException` 位于 protocol，携带 code/cause，继承 IllegalStateException；CompletionException/ExecutionException 可以逐层解包，没有增加 engine/client/server 的反向模块依赖。
- 客户端 `ReconnectErrorClassifier` 区分 IOException、结构化业务异常和 UdpFrameException.reasonCode；未知文本不使用 contains。typed 非永久码优先于误导文案。
- `UdpClientBinding` 将无 receipt 的业务拒绝转换为 ServerApiException；动作 receipt 处理保持原路径。
- 独立客户端用例通过生产 `UdpClientBinding` 与编码后的 RSP 验证：INVALID_TOKEN 首次恢复失败即终止；未知 `UNKNOWN_CLOSED_ERROR` 恰好 10 次尝试后终止，指数退避封顶 8000ms，失败的替换绑定全部 closed，最终没有有效重试任务。这里使用确定性调度及内存报文，不冒充真实 socket 负载测试。
- 本轮既有 MR-01 隔离专项 13 项、第一批 scheduler 所有权专项 5 项均通过。
- 服务端独立回归中的既有 191 项均通过，包括真实 UDP 两次恢复、接收线程实际退出、公开流/玩家流隔离、抢占与控制代次、权威计时、房间生命周期等。新加 6 项映射探针中 3 项通过、3 项失败。

## 实际串行执行记录

启动前进程检查仅见 Gradle/Kotlin 守护进程，没有其他 Gradle 客户端或测试 worker；每条 Gradle 命令均等上一条退出后启动，没有并行构建。日志存于 `%TEMP%/majiang-mr02-*.log`。protocol 与 engine 使用 `--rerun-tasks` 强制重新执行；独立测试任务本轮也实际执行，未用旧测试结果替代。

| 顺序 | 命令 | 结果 |
| --- | --- | --- |
| 1 | `gradlew.bat :protocol:test --rerun-tasks --console=plain` | 退出 0；35s；171/171 通过，18 类，失败/错误/跳过均 0 |
| 2 | `gradlew.bat :client:test --console=plain` | 退出 1；31s；新增测试编译失败，未执行模块测试 |
| 3 | `gradlew.bat :server:test --console=plain` | 退出 1；26s；新增测试编译失败，未执行模块测试 |
| 4 | `gradlew.bat -I tools/architecture/v4-improvement.init.gradle :client:test --console=plain` | 退出 0；55s；既有独立专项 18/18 通过 |
| 5 | 同上，加入本轮新增独立用例后执行 | 退出 0；10s；21/21 通过，3 类，失败/错误/跳过均 0；包含上一步的 18 项，不重复计数 |
| 6 | `gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true -PreviewStage=mr02-errors :server:test --console=plain` | 退出 1；1m28s；197 项，194 通过，3 失败，错误/跳过 0；22 类 |
| 7 | `gradlew.bat :engine:test --rerun-tasks --console=plain` | 结果待末尾核实 |

核对的本轮 JUnit XML：

- `protocol/build/test-results/test/TEST-*.xml`，UTC 起始时间 2026-10-14 15:58:59。
- `build/v4-improvement/client/test-results/test/TEST-*.xml`，UTC 起始时间 2026-10-14 16:02:34。
- `build/v4-migration/review-mr02-errors/server/test-results/test/TEST-*.xml`，UTC 各类起始时间 2026-10-14 16:03:25–16:04:05。
- 独立 init 脚本替换测试源集和输出目录，因此独立专项不会被 Anti 新增测试的编译失败阻断；这不等于标准 client/server 测试通过，没有删除、跳过或放松交付测试。

本轮新增并保留的独立验收源码：

- `tools/architecture/v4-improvement/client/Mr02ReconnectReviewTest.kt`：3 项，全部通过。
- `tools/architecture/v4-review/Mr02ErrorMappingReviewTest.kt`：6 项，3 通过、3 失败；失败断言保留，供返修后复验。

## 工作区保护与验收边界

交接开始时对 git 跟踪及未忽略的既有文件建立 SHA-256 清单，共 758 个。完成主要检视与专项后复核，既有文件变化数为 0；包括第一批三个调度器相关文件、MR-01 源码、第一批报告、`tools/architecture/` 既有文件，以及 lwjgl3 配置和截图入口。本轮只新增两个独立测试文件和本报告；Anti 交付源码未被代修，原工作区修改全部保留。

关键验收版本 SHA-256（原文件字节，不归一化换行）：

| 文件 | SHA-256 |
| --- | --- |
| protocol `ServerApiException.kt` | `53C739BF34CAE4A3A0A1ECCF5C8A70BAD9784BD826E4684C7DD9ABA280335106` |
| client `ReconnectErrorClassifier.kt` | `B26B1D66D4655767438E1566C64F990EA9AAF371CC9D7A49EBC433A3AFC7ED37` |
| client `ServerConnectionSession.kt` | `1EFA0E6B3F2D122FA2A8C8311FBB47AAF8B7110FEB6EEB0EC3A6A6A10E7ADC24` |
| server `UdpRoom.kt` | `A6DD72D899F8C79CC7CD5B14079B52A566113FFB886670B506759FFA7BFDDE06` |
| server `InProcessBinding.kt` | `144B4601EB4FA7B6870927A2A48BDEDF0F7D92F5A8DB1A6453A808840447D2A4` |
| 独立 `Mr02ReconnectReviewTest.kt` | `AB2980BEE958F8F82143E6806B1DEB9BA02573A280357F0D6229AB9035ACAB8D` |
| 独立 `Mr02ErrorMappingReviewTest.kt` | `D2185C170B5B15F85081301EC9CD5E09431862E130DC21641317CA0D04022C3E` |

本轮未执行 Android/桌面打包、GL 冒烟或长时资源压力测试；真实 UDP 两次恢复和线程退出回归不能替代全面的线程/socket 数量基线测量。由于两个标准模块编译失败，不宣称全量构建通过。Anti 应先修复 R1–R3、同步交付说明，再主动交接 Codex 串行复验；不得放宽本轮失败探针的断言来获得通过。
