# TCP 返修第八轮独立复验：R14 关闭

日期：2026-10-16。Anti 在原会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R14 ready_for_review` 后，Codex 独立复验。

- `InProcessBinding.executeRequest` 现在于 `ApiMessageCodec.encodeRequest(req)` 失败时直接返回结构化 `MALFORMED_REQUEST`，发生在 `receiptStore.acquireAdmission` 之前；不再回退为 API 名称。
- 新增正式测试先断言畸形请求确实导致编码器抛错，再检查错误响应及同 requestId 合法请求成功；另测同 API 不同合法载荷得到 `CONFLICT`。定向套件强制重跑通过，日志 `build/tcp-current-review8-r14-target.log`。
- 标准全仓强制重跑 **1,239/1,239 通过**，日志 `build/tcp-current-review8-all-forced.log`；Codex 10/10 独立用例强制重跑通过，日志 `build/tcp-current-review8-independent-forced.log`；Android 调试包与桌面 jar 构建通过，日志 `build/tcp-current-review8-dual-build.log`。

结论：**R14 关闭**。R07、R09、R10 仍为设计阻断，R01–R16 整体与 T4/T5 仍待验收。下一批沿用同一 Anti 会话处理 R09。双端构建不替代真机网络、弱网和 GL 冒烟。
