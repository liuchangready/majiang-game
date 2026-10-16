# TCP 返修第十一轮独立复验：R09 排水与在线派发仍乱序

日期：2026-10-16。Anti 原会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R09_FIX3 ready_for_review` 后，Codex 独立复验。

## 已通过

- 上轮第 13 项独立探针“房间信息溢出后挂载即收到最新版本”已转绿；源码确认 `onRoomUpdate` 排水时补入 `latestRoomInfo`，并按版本去重。
- 重试耗尽后调用 `handleFailure(HEARTBEAT_TIMEOUT, ...)`，该路径关闭当前 Socket 并向监听器派发断线通知；正式测试覆盖了玩家路径。
- R09 标准定向套件强制重跑通过，日志 `build/tcp-current-review11-r09-target.log`；标准全仓强制重跑 **1,247/1,247** 通过，日志 `build/tcp-current-review11-all-forced.log`；原 13 项独立用例强制重跑通过，日志 `build/tcp-current-review11-independent-forced.log`；Android 调试 APK 与桌面 jar 构建通过，日志 `build/tcp-current-review11-dual-build.log`。

## R09 新发现的顺序阻断（独立复现）

`onRoomUpdate`、`onPublicUpdate` 与 `onUpdate` 在 `stateLock` 内把早期队列取到局部列表并注册监听器，然后在锁外逐条回调。此时新在线更新可由入站线程看到已注册监听器、直接回调；若旧队列第 1 条的监听器稍慢，新在线第 3 条会先于旧队列第 2 条交付。

- 新增独立用例 `room listener drain and live update preserve version order`：真实观战绑定下缓存房间版本 1、2，在版本 1 回调中阻塞，再派发版本 3；实测回调顺序为 **1、3、2**，期望 **1、2、3**。日志 `build/tcp-current-review11-r09-order.log`。
- 新增独立用例 `public listener drain and live update preserve cursor order`：同样交错公开流游标 1、2、3，实测 **1、3、2**。日志 `build/tcp-current-review11-r09-public-order.log`。

这违反设计 §6 “先采纳绑定基线再排水早到消息”“早到数据与在线派发有序”。请让注册排水与后续在线更新共用有界串行派发门控；仍在锁外调用用户回调，保留 close/rebind 代次检查，并同样覆盖玩家流。不要只在测试中增加等待，两个独立失败探针不得修改。

结论：R09 **仍退回**。R04/R14 已关闭，R07/R10 仍待修，R01–R16 整体与 T4/T5 仍待验收。
