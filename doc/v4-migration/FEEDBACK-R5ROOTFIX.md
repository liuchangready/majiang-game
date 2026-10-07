# Codex 独立诊断（2026-10-07 12:41，GLM额度退出后）

Anti ROOT初版交付尚未接受。Anti ROOTFIX及其自动GLM接续均明确429额度退出，未交付；部分改动保留。无任何写入进程时Codex实测：`build/v4-migration/R5-quota-partial-presentation-compile.log` 失败，`R5-quota-partial-client-test-fix.log` client147项145通过2失败。Codex只修新增`UdpClientFailureTest.TrackableScheduler.schedule`的测试fixture签名 `Runnable` 以便实际运行，没有改生产或有效断言。`R5-quota-partial-server-test.log` server模块通过。

ROOT自有明确编译错误：`MahjongGame.kt` 行165/175/243/281 `postRunnable`收`()->Unit`但推断lambda返回Boolean/Unit?，应明确以Unit结尾；`MenuScreen.kt` 缺`SpriteBatch` import引出大量`projectionMatrix/begin/end`级联；`ConnectionState.DISCONNECTED`不存在（实际CONNECTING/CONNECTED/RECONNECTING/CLOSED）；列表`RoomSummaryView`只有`matchStarted`、`configSummary`，没有`lifecycle/ruleSummary`。`GameScreen/SpectatorScreen`新包未生成属GAME范围，勿用反射或旧包壳规避。

新增真实客户端失败：`UdpClientFailureTest` 未解析域名的两项均期望connect Future异常，实际成功（XML显示isCompletedExceptionally false）；`DatagramSocket.connect(InetSocketAddress(unresolvable))`在此环境没有即时抛错。应在初次connect装配/启动接收线程前验证地址已解析并以一致的异常结束，同时关闭已创建socket和自有scheduler；测试的未知主机样例是有效的，不能放宽为成功。两项路径分别覆盖`UdpClient.connect`和`ServerConnectionSession.connectUdp`。初版ROOT对failure测试补的重载签名错误已由Codex测试接线修正。

此外仍必须逐项完成 `TASK-ANTI-R5ROOTFIX.md` 的开赛、非0席位/观战角色、失败离房不导航、旧兼容构造删除、赤五配置真实或固定、dispose/代次回调守卫和大厅断开语义。完成前不得宣布ROOT接受；不能运行Gradle由Codex验收。

## Codex 续检（2026-10-07 16:52）

15:36 接续的 ROOTFIX 在15:58写出 `delivery-ANTI-R5ROOTFIX.md`，随后桥接返回 `Individual quota reached`（估计约19:03额度重置），真实 writer 已退出。**交付不代表验收**。`build/v4-migration/R5-after-anti-presentation-compile.log` 的 ROOT 自有剩余错误仅 `MahjongGame.kt:233,287`：使用 `ReceiptStatus.APPLIED` 却未 import `com.majiang.client.api.ReceiptStatus`；请修并自审相关路径。GAME 另有3处编译错误，属 GAME 范围，不要改其文件。

`build/v4-migration/R5-after-anti-client-server-test.log` 中 client147项145通过2失败，原因已查明：本机DNS将测试固定的 `.invalid` 域名合成为 `198.18.0.92`，故该主机名并不满足测试前提，并非 `UdpClient` 未调用 `InetAddress.getByName`。Codex 仅将测试主机名改为含空格的确定无效格式（仍断言连接失败、`UnknownHostException`、自有scheduler关闭），复跑 `build/v4-migration/R5-after-anti-client-test-dns-fixture.log` **147/147全过**；server **111/111全过**。不要删除生产地址解析或弱化清理语义。

待表现层编译恢复后，Codex 还须运行 presentation 模块测试及真实菜单/桌面入口烟测，故 ROOT 目前未接受。
