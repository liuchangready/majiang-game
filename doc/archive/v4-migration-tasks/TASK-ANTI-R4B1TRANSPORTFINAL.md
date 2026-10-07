# R4B1 UDP 客户端通知收尾

沿用 TASK-ANTI-R4B1UDPCLIENT.md：独占 client transport/udp 与对应模块测试，不改 ServerConnectionSession、API、protocol、server、reviewer、build。其他两个任务并行。只文件工具，不 shell/构建/提交/再委派。Anti优先，额度耗尽自动交GLM。

修复 FEEDBACK-R4B1TRANSPORTFINAL.md 中并发通知/绑定提交边界，保持稳定seq重复ACK和连接身份检查。不要把外部listener移回状态锁。交付 delivery-ANTI-R4B1TRANSPORTFINAL.md，最终重读反馈，测试由Codex执行。不进入B2/R5/R6。
