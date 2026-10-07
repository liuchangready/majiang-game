# 任务 T3：UDP 自封装协议栈（纯协议库）

你是 Anti，负责本任务的纯代码开发。**只用文件读写工具，禁止运行任何命令、构建、测试**——由主会话执行。完成后列出你创建/修改的全部文件路径清单。

必读：
- `doc/SERVER_CLIENT_API.md` §7 全部（数据报格式/包类型/可靠性/会话/参数——逐条实现）
- 参考风格：`client/src/main/kotlin/com/majiang/network/protocol/NetworkFrameCodec.kt`（现有编解码的写法）

## 目标

实现 UDP 协议栈的**纯逻辑库**：编解码、可靠性状态机、分片、心跳。不含真实 socket 收发（T4/T5 接线），单测用注入的传输函数与假时钟。

## 文件所有权（独占）

```
client/src/main/kotlin/com/majiang/transport/udp/
client/src/test/kotlin/com/majiang/transport/udp/
```

其余文件只读（server 模块已依赖 client 模块，协议栈放此处双方共用）。

## 交付物

### 1. 编解码 `UdpFrame.kt` + `UdpPacketType.kt`
- 22 字节头：magic(2)=0x4D47、version(1)=4、packetType(1)（高 2 位 flags：bit7=zlib、bit6=加密预留）、connectionId(4)、seq(4)、ackSeq(4)、payloadLen(2)、crc32(4，计算时本字段清零)。大端序。
- 9 种包类型常量：REQ/RSP/PUSH/ACK/RESEND/PING/PONG/FRAG/KICK（§7.3 表）。
- `encode(...): ByteArray` / `decode(bytes: ByteArray): UdpFrame`；数据报总长上限 1200，超长抛 `IllegalArgumentException`；magic/version/CRC/payloadLen 不符抛异常（异常消息含稳定原因码，供上层判恶意包）。
- KDoc 写明字段语义与 §7.2 完全一致。

### 2. 可靠请求-响应 `ReliableRequestor.kt`
- 状态机：`send(requestId, payload)` → 重传直至 RSP 或放弃；RTO 初始 200ms、×1.5 退避、上限 3s；累计 5 次无应答判死。
- **时间全部走注入的 `Clock`/`Scheduler` 接口**（你自己定义最小接口，测试注入假实现），禁止 `System.currentTimeMillis`/`Thread.sleep` 散落。
- 重复 `requestId` 在途时拒绝新请求（对齐现有 ClientTransport 的 DUPLICATE_REQUEST_ID 行为）。

### 3. 推送游标自愈 `PushSequencer.kt`（接收端）+ `PushRetransmitWindow.kt`（发送端）
- 接收端：`offer(update(cursor, fromCursor))`——`fromCursor > 本端游标+1` 判缺口 → 产出 RESEND(fromCursor=本端游标+1) 动作；`cursor ≤ 本端游标` 丢弃；窗口外缺口 → 请求方直接采用全量快照（暴露回调）。
- 发送端：按 connectionId 保留最近 256 个已发推送包，响应 RESEND：缺口在窗口内按序重发，窗口外发全量 PUSH。
- 数据结构用 `ArrayDeque` 环形窗口，内存有界。

### 4. 分片 `Fragmenter.kt`
- payload > 1090 → FRAG(msgId, fragIndex, fragCount, bytes)；3s（注入时钟）未集齐整条丢弃并上报回调。
- 重组缓冲有界（同时最多 8 条消息在途）。

### 5. 心跳 `HeartbeatPacer.kt`
- 3s 间隔 PING（携带 clientMillis）、PONG 回填 RTT 估计（EWMA）；连续 3 次未收到 PONG 或 10s 无任何入站帧 → 判死回调。

### 6. 防重放 `ReplayGuard.kt`
- 每方向单调 seq：`accept(seq)` 返回 false 当 `seq ≤ maxSeen`；connectionId 不匹配的包由上层静默丢弃（本组件只提供校验函数）。

### 7. 单元测试（你写文件，主会话跑；全部用假时钟/注入传输，零真实 socket、零真实等待）
- 编解码往返、截断/篡改 CRC/错 magic/超长 拒绝。
- ReliableRequestor：无丢失成功、丢首包重传成功、5 次失败判死、重复 requestId 拒绝、RTO 退避序列断言。
- PushSequencer：连续应用、缺口触发 RESEND、乱序丢弃、窗口外全量自愈；RetransmitWindow：窗口内补发、窗口外全量、窗口淘汰。
- Fragmenter：往返、缺片超时丢弃、并发 8 条上限。
- HeartbeatPacer：间隔触发、RTT 计算、判死。
- ReplayGuard：重放拒绝、乱序接受。

## 陷阱清单

1. **禁止**真实网络/线程/睡眠：一切并发原语只许 `java.util.concurrent` 的纯数据结构；定时行为全部依赖注入的 Scheduler。
2. 禁止修改 `client/network/` 任何现有 TCP 文件；T7 前旧栈必须原样可用。
3. 协议常量（版本 4、1200、1090、RTO 参数、窗口 256、心跳 3s/判死 10s）集中在一个 `UdpProtocol.kt` 常量对象，散写视为缺陷。
4. Kotlin/JVM 17，无新依赖（kotlin-stdlib、junit-jupiter、kotlin-test 已有；CRC32 用 JDK `java.util.zip.CRC32`）。
5. 纯逻辑库不 import `com.majiang.server.*`（协议栈不得反向依赖 server 实现；可以依赖 T1 的 api 常量）。

## 完成标准

主会话执行 `:client:compileKotlin :client:test` 与全量 `test`。除独占路径外 `git status` 无其它改动。
