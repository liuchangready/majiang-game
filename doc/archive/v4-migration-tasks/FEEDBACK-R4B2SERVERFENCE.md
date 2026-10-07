# Codex 只读复核补充

原失败耗时 5.018 秒，`SharedRoom.resumeSpectator().thenApply { notifySpectatorDisplaced(...) }` 在 TableHost 单线程权威 Future 完成时同步执行 listener。gen2 listener 挂 5 秒，gen3 因此无法推进，待 gen2 完成绑定后才开始，导致旧请求错误成功。`InProcessBinding.tryClaimSpectatorBinding` 已有 member.generation 检查，关键是不能让外部顶替通知阻塞权威队列。异步派发必须保留代次身份校验，防止乱序旧通知关闭 gen3 新绑定。

验收保留原门闩：gen2 通知被挂起时 gen3 应在小于5秒内完成；放行后 gen2 请求以 `DISPLACED_BY_NEW_CONNECTION` 拒绝；gen3 可查公开快照；旧连接及时终止。不能改短/延长等待或删断言规避。

注意：只把通知改成 fire-and-forget 仍可能让 gen2 的 resume future 在通知挂起时先成功，测试依然失败且旧连接顺序未受控。需要把“通知、绑定认领、该次 Future 完成”的链条整体移出单线程权威队列，使 gen3 完成链可并行；在 gen2 认领前复核最新代次，失败不得覆盖/解绑 gen3。迟到 gen2 通知也不能误关 gen3。
