# T3 交付记录：UDP 自封装协议栈

日期：2026-10-05。开发：Anti。检视/测试：主会话。

## 交付物（18 个新文件，均在独占路径 `client/**/transport/udp/`）

主源码 10 个：`UdpProtocol`（常量集中：魔数 0x4D47/版本 4/头 22B/报文 1200B/分片 1090B/RTO 200ms×1.5 上限 3s/5 次判死/窗口 256/心跳 3s/入站判死 10s/EWMA 0.875）、`UdpPacketType`（9 种）、`UdpFrame`（22B 大端编解码 + CRC32 全校验）、`UdpClock/UdpScheduler/Cancellable`（时间全注入）、`ReliableRequestor`（重传直至 RSP、RTO 退避、DUPLICATE_REQUEST_ID、收 RSP 自动回 ACK）、`PushSequencer`（游标缺口→RESEND、窗口外→全量自愈回调）、`PushRetransmitWindow`（ArrayDeque 环形 256、窗口内按序重发）、`Fragmenter`（>1090B 拆片、9B 子头、3s 未齐丢弃、并发 8 条上限）、`HeartbeatPacer`（PING/PONG、EWMA RTT、3 丢判死、10s 入站判死）、`ReplayGuard`（单调 seq 防重放 + connectionId 校验）。

测试 8 个：`FakeClockAndScheduler`（虚拟时钟确定性调度）+ 7 个组件测试，共 39 项，全部假时钟/注入传输，零真实 socket、零真实等待。

## 验收结论：通过

- 隔离检查：无越界改动。
- 检视结论：参数与 SERVER_CLIENT_API §7 逐项一致；禁真实时间/线程的约束落实（时间全走注入）；协议常量无散写；零依赖 server 实现包。
- 测试：client 73 项全过（34 既有 + 39 新增）；全量回归 **632 项 0 失败**。

## 主会话修正（1 处，测试预期错误而非实现缺陷）

- `HeartbeatPacerTest.connection dead after 10s`：原用例在静默满 10000ms 的瞬间断言 `isDead()==true`，但 READ_TIMEOUT 检测随 3s 心跳 tick 执行（实现 [HeartbeatPacer.kt:146](../../client/src/main/kotlin/com/majiang/transport/udp/HeartbeatPacer.kt)），检测粒度为一个心跳周期。已改为：14000ms 仍未检出（断言 not dead）→ 推进至 16000ms 下一个 tick 检出判死（断言 READ_TIMEOUT）。
- 设计确认：判死延迟 ≤1 个心跳周期（3s）符合 §7.6 参数表的工程语义，不改实现。

## 遗留观察（转 T4）

1. 载荷上限 1178B（1200−22）与分片阈值 1090B 留有子头余量，一致。
2. `PushRetransmitWindow` 窗口外生成全量 PUSH 需注入快照供应者——T4 接线时提供。
