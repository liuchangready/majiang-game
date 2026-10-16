# TCP 迁移返修最终文档复核与阶段验收

日期：2026-10-17。Anti 始终使用原会话 `a449f5ef-f624-49c4-a741-f703bbb39968`，以 `--dangerously-skip-permissions` 接收统一问题与后续独立复验退回项。最新回调为 `CODEX_CALLBACK TCP_DOC_STATUS ready_for_review`。

## 独立验收结果

- R01–R16 当前自动化代码阻断已转绿：Codex 22 项独立探针、标准全量 1,275 项、Android Debug APK 与桌面 jar 强制构建均通过。三大绝技真实 TCP 集成测试通过。证据见 `build/tcp-current-review19-independent-forced.log`、`build/tcp-current-review19-all-forced.log`、`build/tcp-current-review19-dual-build.log`。
- 真实桌面 GL 冒烟通过：`:lwjgl3:snapshot` 正常退出，生成 6 张 1280×720 的主菜单/牌桌快照；主菜单与牌桌图像已人工查看。证据见 `build/tcp-current-review19-gl-snapshot.log` 与 `snapshot/`。
- T5 删除门槛通过：活跃模块 `src/main`、`src/test` 无 UDP、`connectUdp` 或旧 TCP/V3 引用；历史独立审查工具里的 UDP 探针为存档，不参与发布源集。
- Anti 文档纠偏已复核：`AGENTS.md` 模块说明改为 MJ-TCP/1 与 InProcess/TCP；`ANTI-TCP-T5-DELIVERY.md` 帧头为 12 字节、当前标准基线为 1,275 项；最新进度与交付记录把 T5 删除和 T4 环境验收区分开。此轮只改文档，生产源码未变，故未重复运行已通过的代码测试。

## 尚未取得的 T4 证据

设计 T4 仍需跨物理网络长连与弱网测试、玩家和观战各至少 3 次真实中断后的应用资源回落、Android 真机或模拟器建连和后台恢复。本机 `adb devices -l` 无设备，Android emulator 不存在，也没有第二物理网络节点。现有回环集成、APK 构建及桌面 GL 成绩不能替代上述实测；**T4 整体不予签收**。

另记一项后续维护约束：客户端终态派发有两级有界执行器与最多 256 项有界溢出缓冲；当前独立满载探针验证单次双满载后最终交付。若未来增加 Selector 线程上的终态动作类别，必须重新核算溢出上限对最大在途动作数的覆盖。

结论：当前可在自动化范围内验收 TCP 代码返修和 T5 删除阶段；T4 保持部分通过，待具备设备与物理网络环境时补齐实测。
