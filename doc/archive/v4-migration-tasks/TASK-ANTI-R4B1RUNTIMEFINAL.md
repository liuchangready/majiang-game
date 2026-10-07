# R4B1 runtime 缓存提交竞态返修

沿用 TASK-ANTI-R4B1RUNTIME.md 的独占范围：ServerConnectionSession.kt、对应模块测试，可新增 RoomRuntimeTest。其他任务同时改 client transport/udp 与 server UDP，禁止跨改，不改公共API/protocol/reviewer/build。只文件工具，不 shell/构建/提交/再委派。

阅读 FEEDBACK-R4B1RUNTIMEFINAL.md，修复独立回归已证实的问题并补模块覆盖。Anti开发，Codex测试验收，额度耗尽自动转GLM。不进入B2/R5/R6。交付 delivery-ANTI-R4B1RUNTIMEFINAL.md，结尾重读反馈，明确没运行测试。
