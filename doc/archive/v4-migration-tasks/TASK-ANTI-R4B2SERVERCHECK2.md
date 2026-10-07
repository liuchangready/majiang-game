# R4B2 SERVER CHECK2：服务端实际编译返修，文件工具直接落盘

用户指定 Anti 开发、Codex 测试；本批独占 server/src/main 与相关 server/src/test。上一 SERVERCHECK bridge `R4B2SERVERCHECK-1791342263338` 已 exit0/PID20836消失，但 Anti 返回空 response，未写交付文件，UdpRoom:541 `full = true` 仍在，新增测试仍未修，bridge 记录一次被拒的 RunCommand。它不构成交付或quota失败；保留此前 SERVERRESUME 代码，重新从实际文件继续。并行 CLIENTFENCE 独占client，protocol/engine/presentation/平台/build/reviewer均冻结。

请用你自己的内置文件读取、编辑/写入工具直接修改源文件。**不要尝试 RunCommand、shell、Gradle、测试、提交、其他agent或设置修改**。若文件编辑工具不可用，明确报告真实错误，不能写“已完成”。

先读 `FEEDBACK-R4B2SERVERRESUME.md`、`TASK-ANTI-R4B2SERVERCHECK.md` 和 `build/v4-migration/R4B2-ANTIRESUME-modules.log`（只读，最后日志若太长按需读编译行），并完成其中全部服务端修复，重点：

1. `UdpRoom.kt:541` 冻结协议 `ProtocolPublicUpdate` 构造字段为 `isFullSnapshot = true`，不是 `full`。真实编译错误必须落盘修复。
2. 新增 `UdpRoomTest` 的 PublicCheckpoint 负数/超前/Long.MAX/错流拒绝，要在真实构造器边界或服务端边界按合法协议路径断言；不能先非法构造抛出使后面断言不可达。
3. 真实UDP异步加入和角色非法请求必须观察实际RSP，不把错误请求塞进成功缓存，也不在异步完成前立即读取 spectators。保留完整权限、隐私与离房断言。
4. SERVERRESUME 已补的注册后PUBLIC基线、关房不复活与InProcess排他机制要保留。请核对并修复必要测试/生产缺口，不能规避独立 reviewer 2 个真实UDP注册窗口测试。

交付前读本任务全文与若存在的 `FEEDBACK-R4B2SERVERCHECK2.md`。写 `doc/v4-migration/delivery-ANTI-R4B2SERVERCHECK2.md`，列每个改动文件、未完成项、未运行测试，然后结束通知Codex；绝不声称构建已过。不得进入R5。
