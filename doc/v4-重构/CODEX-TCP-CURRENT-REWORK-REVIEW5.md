# TCP 返修第五轮独立复验：标准测试全绿，设计阻断仍未关闭

日期：2026-10-16。对 Anti 在会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 中写入的“剩余六项”修复独立复验。会话日志末尾出现连续 `ERROR_MESSAGE`，因此仅依据工作区代码与实测结果，不把 CLI 中断视为已验收回调。

## 已核实结果

- `gradlew.bat test --rerun-tasks --console=plain`：**BUILD SUCCESSFUL**。标准测试 XML 共 1,232 项，engine 470、protocol 166、server 166、client 144、presentation 286；失败、错误、跳过均为 0。日志 `build/tcp-current-review5-all-forced.log`。
- Codex 独立 9 项：`gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :client:test --tests com.majiang.review.TcpCurrentReviewTest --rerun-tasks --console=plain`，9/9 通过。日志 `build/tcp-current-review5-independent-forced.log`。
- `gradlew.bat :android:assembleDebug --console=plain` 与 `:lwjgl3:jar` 构建通过，日志分别为 `build/tcp-current-review5-android.log`、`build/tcp-current-review5-desktop.log`。这不替代 Android 真机/模拟器网络与桌面 GL 实际运行验收。
- 六项原标准失败本轮均已转绿。R07 测试桩改为合法 `ProtocolPlayerUpdate` 帧，原“回调异常不关闭绑定”断言仍在。

## 仍为 P1 的设计阻断

1. **R04 未知结果对账身份丢失**：`TcpClientBinding.kt:783-789,802-809` 请求超时或写失败时移除 `unconfirmedSideEffects`；超时也让 `reconciliationFuture` 失败。副作用仍可能已到服务端，原 scope/requestId/payload 不能可靠恢复。
2. **R07 客户端 I/O 仍可阻塞调用线程**：同文件 `:271-276,724-731` 使用 `java.net.Socket` 与直接 `OutputStream.write/flush`；与设计要求的可复用 NIO 循环、有界写队列和慢写截止不符。回调异常测试通过只证明回调捕获，不证明慢端安全。
3. **R09 早期推送溢出不恢复**：`earlyBufferOverflow` 仅赋值/清零，未读取该标志发起玩家/观战分流的合并全量查询；缓冲达到 64 条或 4 MiB 后仍可能永久停在旧视图。
4. **R10 解码暂存未计全局预算**：`TcpTableServer.kt:323` 在没有预留服务端全局字节预算时直接 `conn.decoder.decode(readBuffer)`；完整帧入邮箱后才记账，未完成帧和解码临时载荷不受 256 MiB 实际持有预算约束。
5. **R14 InProcess 载荷编码失败仍退化为 API 名称**：`InProcessBinding.kt:1492-1495` catch 后取 `req.api`，同 API 不同载荷可被误判为相同重放；应拒绝编码失败并保留明确错误语义，补回归覆盖。

结论：六项标准失败修复可接受为**阶段性进展**，R01–R16 整体、T4/T5 **仍不可验收**。请 Anti 在同一会话按上述五项继续返修，保留原业务断言；返修完成后 Codex 再做独立静态审查和针对性测试。真机/模拟器、跨机弱网与 GL 冒烟仍待实施。
