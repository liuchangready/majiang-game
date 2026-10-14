# MR-02 重连资源基线验收

结论：**通过**。2026-10-15 01:06–01:08（Asia/Shanghai），Codex 独立新增并执行真实 UDP 重连资源测试，补齐 MR-02 计划中的“多次重连线程/Socket 数回落到基线”验收凭据。未修改生产代码，未提交或推送。

此前功能验收为 1185/1185；本轮另执行 1 项资源场景测试，内部包含玩家 3 次、观战 3 次真实断网恢复。两次验收分开记录，不声称本轮重跑全部功能测试。第一批、第二批及本项均已通过，MR-02 开发与计划内验收完成。

## 方法与实际结果

测试源码：[Mr02ReconnectResourceBaselineTest.kt](../../tools/architecture/v4-review/Mr02ReconnectResourceBaselineTest.kt)。

- 使用真实 UdpTableServer、UdpClient、ServerConnectionSession 和 UDP 转发代理；代理双向丢弃全部流量，由生产心跳超时触发自动重连，进入 RECONNECTING 后恢复流量。未直接调用私有重连方法或伪造掉线通知。
- 预热 JVM 延迟执行设施和一次完整会话生命周期，然后测初始基线；使用 Windows `netstat.exe -ano -p udp` 按测试 JVM PID 34544 过滤，记录操作系统可见的 UDP 本地端点集合。
- 使用 `Thread.getAllStackTraces` 记录存活 Java 线程 ID。每次恢复均检查旧接收线程对象已退出、旧绑定 closed、新绑定实例不同、会话 sessionId 不变，以及同一个会话调度器仍可实际执行任务。
- 每轮关闭后检查端点集合精确恢复到该角色启动前集合；全部关闭后检查端点集合精确恢复初始集合，且没有任何初始基线之外的 Java 线程 ID 仍存活。不是仅根据线程名推断释放。
- 每次重连清理等待上限 8 秒，恢复等待上限 18 秒，保留生产约 2 秒终态 ACK 窗口。进程内服务器与房间在角色关闭时仍运行，最终再整体关闭。

| 采样点 | 进程 UDP 端点数 | 存活 Java 线程数 |
|---|---:|---:|
| 预热后初始基线 | 0 | 13 |
| 玩家已连接 | 4 | 36 |
| 玩家第 1 / 2 / 3 次恢复并清理 | 4 / 4 / 4 | 37 / 37 / 37 |
| 玩家会话与代理关闭，服务端及房主仍存活 | 2 | 34 |
| 观战已连接 | 4 | 37 |
| 观战第 1 / 2 / 3 次恢复并清理 | 4 / 4 / 4 | 37 / 37 / 37 |
| 观战会话与代理关闭，服务端及房主仍存活 | 2 | 34 |
| 服务端、房主及全部资源关闭 | **0** | **13** |

两种角色的共享调度器均在会话存活时保留、关闭后进入 shutdown 状态。每次旧接收线程均已退出，六次恢复的端点数未累计增长；最终线程 ID 检查与端点集合检查均通过，无本次场景资源残留。

## 执行与凭据

```powershell
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true -PreviewStage=mr02-resources :server:test --tests 'com.majiang.v4runtime.Mr02ReconnectResourceBaselineTest' --console=plain
```

实际 test 任务执行：**1/1 通过，失败/错误/跳过均 0**；JUnit 用时 88.490 秒；构建 `BUILD SUCCESSFUL in 1m 50s`，退出码 0。测试类开始时间 UTC `2026-10-14T17:06:53.060Z`。构建中存在 Thread.id 弃用警告，无编译或运行失败。

| 凭据 | SHA-256 |
|---|---|
| 测试源码 | `A57ACC16378D388E97FB86BD195857FC1857FD51501A4A86DD50604A2A07F719` |
| `build/mr02-resources-test.log` | `9071FAC3B8639BD1F9066EBC452BAC5CCA6C2085AE25DFA0BF48D05C3B84F2F7` |
| `build/v4-migration/review-mr02-resources/server/test-results/test/TEST-com.majiang.v4runtime.Mr02ReconnectResourceBaselineTest.xml` | `11349C5B7F30F2C6D56C4B9EF83AB81AD835DCB14833862D24AD8C332BAEF0DD` |

范围：Windows 单测试 JVM、局域回环 UDP、玩家/观战各 3 次恢复及最终关闭。线程数是可通过 Java API 枚举的线程，Socket 测量为 OS UDP 端点，不代表所有原生 JVM 线程、TCP 或系统句柄。未进行长时压力、1/10/100 房间负载、Android/桌面打包或 GL 冒烟；这些不应从本次结果推导为已通过。MR-00/MR-07 的多房间资源预算仍为后续独立工作。
