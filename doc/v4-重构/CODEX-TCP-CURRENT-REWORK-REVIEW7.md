# TCP 返修第七轮独立复验：R04 关闭，剩余四项继续返修

日期：2026-10-16。Anti 沿用会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 交付 R04 第二轮修复后，Codex 独立检查生产代码及测试。

## 验收结果

- 强制重跑标准全量测试：`gradlew.bat test --rerun-tasks --console=plain` 通过，XML 合计 1,237 项（engine 470、protocol 166、server 166、client 149、presentation 286），失败/错误/跳过均为 0。日志 `build/tcp-current-review7-all-forced.log`。
- 强制重跑 10 项 Codex 独立 TCP 测试通过；其中上一轮失败的 `wrong scope response cannot erase unknown side effect identity` 单独重跑也通过。日志分别为 `build/tcp-current-review7-independent-forced.log`、`build/tcp-current-review7-r04-target.log`。
- Android 调试 APK 与桌面 jar 构建通过，日志 `build/tcp-current-review7-dual-build.log`。构建不替代 Android 真机网络、桌面 GL 或弱网验收。
- 源码确认 `TcpClientBinding` 对错误 requestScope RSP 保留副作用的原 scope/requestId/payload 和对账 Future；128 条恢复记录的检查与插入处于同一锁；重放时原身份进入受 `limits.maxInFlightRequests` 约束的队列。Anti 新增的三项标准回归覆盖了错误 RSP、并发容量和 70 条恢复记录流控。因此 **R04 本轮关闭**。

## 仍未关闭的设计阻断

1. **R07**：`TcpClientBinding.kt:761-767` 仍在调用线程直接 `OutputStream.write/flush`，客户端为 `java.net.Socket`；读线程执行解码、Future 完成及用户监听器。设计 §1/§2/§4 要求可复用 NIO 循环、有界写队列、停滞写入截止及 I/O/回调隔离。R04 新增的 `pumpReplayQueue` 也同步写，须一并纳入非阻塞出站路径。容量拒绝分支目前在 `unconfirmedSideEffectsLock` 内完成 Future（约 `:785-807`）；异步回调可能在该锁内执行，应移至锁外。
2. **R09**：`earlyBufferOverflow` 仅设置与清零，没有被读取并触发玩家/观战合并全量恢复；早期对象统一按固定 256 字节估算，无法保证实际 4 MiB 字节上限。见 `TcpClientBinding.kt:680-745`，以及设计 §4/§9 的 65 条溢出与大帧验收。
3. **R10**：`TcpTableServer.kt:323` 在预留服务端全局内存预算之前执行 `conn.decoder.decode(readBuffer)`；半帧暂存和解码临时载荷尚未纳入 256 MiB 实际持有预算。需在分配前准入并按所有权释放，同时保持读写轮转公平。
4. **R14**：`InProcessBinding.kt:1492-1495` 序列化异常时以 `req.api` 代替原载荷参与幂等比较，会把相同 API 的不同非法载荷错误视作同一请求。应明确拒绝并返回结构化 `MALFORMED_REQUEST`，补双传输一致性回归。

R01–R16 整体及 T4/T5 继续待验收。请 Anti 在同一会话处理上述四项，保留既有断言；真实 Android 网络、跨机弱网、多次断线资源回收与桌面 GL 冒烟仍需独立证据。
