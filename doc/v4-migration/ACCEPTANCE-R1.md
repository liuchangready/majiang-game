# R1 独立验收记录

执行人 Codex；开发 Anti；日期 2026-10-05。交付说明不等于测试通过。

## 第一轮：拒收

交付：delivery-ANTI-R1.md。桥接日志 R1-1791186517251，16:08 完成。

| 检查 | 结果 | 证据 |
|---|---|---|
| 独立验收编译 | 失败，未执行用例 | build/v4-migration/R1-review.log；UdpTableServer:591 调用 Unit.whenComplete |
| protocol 模块测试 | 72 项，5 失败 | build/v4-migration/R1-module-tests.log |
| client 模块测试 | 48 项，2 失败 | 同上 |
| 桌面与 Android | 本轮未重跑 | 服务端编译失败后暂不作为新交付通过依据 |

protocol 失败包括四个编解码异常类型断言及旧快照回退。client 失败包括重复 RSP 未再次 ACK、心跳断连原因与测试预期不一致。

代码检视另发现：乱序独立请求仍被最高序号门槛丢弃；大厅幂等缓存没有比较载荷；已绑定客户端仍接受 connectionId=0 的响应；终止 KICK 尚无重传/确认生命周期。已派发 TASK-ANTI-R1FIX1.md。

独立回归套件已扩展至 21 项：V4ReviewAcceptanceTest.kt 的 20 项行为用例，加 V4DatagramLossAcceptanceTest.kt 的真实停服通知/确认丢包用例，均在 tools/architecture/v4-review。最终架构门禁另放在 com.majiang.v4final 包，避免把尚未迁移阶段误计为当前协议回归。当前新增用例尚未执行，不计为通过。

## 第二轮：R1FIX1 仍未通过

16:33 Anti 写出交付，但 CLI 最终附带 503 服务不可用错误。以实际文件与测试为准：生产编译通过；测试源码原先仍有 Fragmenter 构造缺参，Codex 修正测试接线后执行独立回归。

- 独立回归 21 项，16 通过、5 失败；日志 build/v4-migration/R1FIX1-review.log。
- protocol 75 项 / 5 失败，client 48 项全部通过，server 32 项 / 6 失败；日志 build/v4-migration/R1FIX1-modules.log。
- 独立失败根因：PUSH 标记字段被传入严格的 PlayerUpdate 解码器而遭拒绝；停服提前置 isClosed/关闭调度器，阻断 KICK 发送和重试。
- 另外检视发现 KICK ACK 未匹配 ackSeq，ReplayGuard 重置基线保护不足，在途缓存未实现硬上限；已于 16:37 派发 R1FIX2，沿用 Anti 上一轮会话。
- Codex 修正测试辅助器分片接收、合法信封缺协议版本、手写快照结构、错误码集合；乱序测试改为新序接受且重复拒绝，保留防重放断言。没有改生产代码或删除有效规则断言。

## 后续验收

R1FIX2 于 16:49 写出交付，仍附带 CLI 503 错误。独立编译发现 UdpClientSession 缺 ApiErrors import、ApiErrors.SERVER_BUSY 未定义、ApiErrors.SERVER_SHUTDOWN 应为 ApiProtocol 常量。日志 build/v4-migration/R1FIX2-review.log；16:50 派发仅三处修改的 R1FIX3。

## 最终结果：通过

R1FIX3 的三处编译修正完成后，2026-10-05 16:52 独立验收 21 项全部通过，无跳过。日志 build/v4-migration/R1FIX3-review.log。

随后执行 `gradlew.bat test :lwjgl3:compileJava :android:assembleDebug --continue --console=plain` 成功，日志 build/v4-migration/R1-final-checks.log。各模块 XML 合计 678 项、零失败、零跳过，统计保存为 build/v4-migration/R1-test-counts.json；engine 的 340 项沿用 Gradle UP-TO-DATE 结果，其余模块本轮执行。Android 为构建验证，尚未真机验收。

验收源文件哈希保存为 build/v4-migration/R1-accepted-source-hashes.json。通过只代表本阶段可靠性修复，产品仍未切换 V4，模型分层、真正异步、重连、展示迁移和 V3 清理在后续阶段验收。
