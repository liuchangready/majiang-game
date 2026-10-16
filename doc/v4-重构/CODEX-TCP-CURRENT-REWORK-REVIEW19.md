# TCP 返修第十九轮独立复验：自动化代码阻断转绿，T4 物理环境验收仍待完成

日期：2026-10-17。Anti 原会话 `a449f5ef-f624-49c4-a741-f703bbb39968` 返回 `CODEX_CALLBACK TCP_R07_FIX5 ready_for_review` 后，Codex 独立复验。

## 本轮通过的证据

- 22 项独立探针（客户端 21 项、服务端部分帧预算 1 项）强制重跑通过，包括工作池满载不无限建线程、双完成队列满载后动作最终交付；日志 `build/tcp-current-review19-independent-forced.log`。
- 标准全量 **1,275/1,275** 项强制重跑通过，日志 `build/tcp-current-review19-all-forced.log`。其中三大绝技均有真实 TCP 集成用例通过，R01/R02/R06/R10/R11/R15 服务端定向测试通过。
- Android Debug APK 与桌面 jar 强制重建通过，日志 `build/tcp-current-review19-dual-build.log`。
- `:lwjgl3:snapshot` 实际创建 GL 上下文、运行菜单与单机牌桌，输出 6 张 1280×720 快照并正常退出；日志 `build/tcp-current-review19-gl-snapshot.log`，图像位于 `snapshot/`，已人工查看主菜单与牌桌图像。
- 活跃模块 `src/main` 与 `src/test` 的 Kotlin/Java 源码无 UDP、`connectUdp` 或旧 TCP/V3 引用；历史独立审查工具目录仍存档 UDP 探针，不属于发布源集。T5“删除 UDP 运行链”的源码引用审计与回归门槛可关闭。

## 当前边界

- R07/R09/R10 此前的可复现阻断均已在当前自动化探针与标准回归中转绿；R04/R14 在前轮已关闭。R01–R16 的代码返修可按本轮证据进入自动化验收结论。
- 设计 T4 还要求跨物理网络长连/弱网、多次玩家及观战真实中断资源回落、Android 真机或模拟器连接与后台恢复。当前机器 `adb devices -l` 为空，Android emulator 可执行文件不存在；这些实测不能用回环测试、APK 构建或桌面 GL 快照替代。T4 仍待有设备与网络环境时补验。
- `TcpClientBinding.executeOnWorker` 双执行器满载后使用最多 256 项有界溢出缓冲；现有第 22 项探针证明容量恢复后单个终态动作交付。若以后扩展 Selector 线程上的派发种类，需复核 256 上限相对于最大在途动作数，防止上限触顶时的静默丢弃分支。

## 文档待纠偏

- `AGENTS.md` 模块说明仍称 protocol 含 UDP 分片/可靠性窗口、server 为“本地/UDP”两种绑定，与当前唯一远端 TCP 矛盾。
- 历史 `ANTI-TCP-T5-DELIVERY.md` 多处仍写 16 字节 TCP 帧头与 1,658 项当前测试；设计和源码为 12 字节帧头，本轮标准 XML 为 1,275 项。T5 是删除阶段，Android/GL 实测归设计 T4；交付文档的 T4/T5 口径需统一。

结论：自动化范围内未发现新的代码阻断；T5 删除门槛可关闭，T4 物理环境验收仍待补。文档问题继续交原 Anti 会话纠偏并复核。
