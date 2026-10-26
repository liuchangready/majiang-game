# ENG-016 目标感知策略：Caller 独立验收与 PR19 集成

日期：2026-10-25。Bridge：`thread_20261025_005116_ae52`，第 1 轮。

结论：**三个目标策略组件通过，已调用 approve_task 归档。完整 P1.6、G1/G5 与平台端到端不在本次通过范围；V4 仍是唯一生产基线。**

## 范围与代码复核

- 交付：`dac4db37ded2fd03c1b2cabe0ddf95e67ca1d757`，冻结基线 `78441944283ca111cc344f19d152514ed577574c`。
- 交付工作树干净，相对基线只新增约定的 Context、Strategies、Tests 和交付文档四文件；没有修改 V4、冻结 golden、共享观察模型或兄弟任务。
- 逐函数对照真实 `TargetAwareAIStrategies.kt`，确认 Ron 先于 Tsumo、精确物理摸切、三种整数评分、最大 type/copy 排序与全等保留首个、碰吃优先级、Basic 完整候选选择后再过滤的 fallback。
- 确认清一色吃碰移除实际参与牌并加入完整副露、目标字牌缺口条件、国士额外副本罚分与禁止动作。
- 确认完整候选身份与 session/DecisionKey/controlEpoch/stateVersion 绑定、防御复制、非法事实拒绝；新增策略不读取对手暗牌、牌山或权威状态。
- 未发现需返修的组件缺陷。实际 actor 投影与策略装配仍属后续接线。

## 交付树独立执行

工作目录 `D:/majiang-wt/target-aware`，使用 `D:/Unity/dotnet/dotnet.exe`。

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-build
```

全部 exit 0；构建 0 警告、0 错误。Ai224、Domain322、Contracts104、Client132，共 **782 passed / 0 failed / 0 skipped**，保留基线 Ai172。

Caller 另执行 `gradlew.bat :engine:jar --offline --console=plain`，再执行交付 TEMP 中已审阅的真实 Kotlin 探针和 C# 比较器。Kotlin 直接调用真实 engine.jar 策略，无手抄参考算法。seed `20261025`，121 case、363 策略结果，**363 matches / 0 mismatches / 0 errors**，28 个 null。

独立产物目录：`C:/Users/sncdc/AppData/Local/Temp/codex-eng016-review-20261025/`。

| 产物 | SHA-256 |
|---|---|
| kotlin-cases.txt | `2a9176fb7b15534f5b9b2f94dc4c3a9c2c60a0c1bce8c426d08fb701abecbbe8` |
| csharp-probe.log | `b735fe7cf2a327461131991493edc6c8ae29a676b3939ac3ac1b674de783ffa3` |
| csharp-diff.txt（0 bytes） | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |

临时差分样本不加入冻结 golden 分母。调用命令和原始输出同时保存在 Caller 工具记录；交付者原始日志位置见交付文档。

## 统一 PR19 集成验证

在隔离树 `D:/majiang-wt/codex-target-aware-integration-20261025`，从已发布 `3c2e7f5e735faeb1f73eb4699b7e9551105ee897` 合并交付，得到 `b8fe44ce36f032f25a5b75a5f1f25a478917e2d5`。其后仅新增本验收记录。

同一组 Release 非增量构建和四个测试命令全部 exit 0，0 警告、0 错误；Ai245（已接受 Ai193 + 本次52）、Domain322、Contracts104、Client132，共 **803 passed / 0 failed / 0 skipped**。

原始日志位于上面的独立产物目录，`integration-sha256.json` 保存清单：

| 日志 | SHA-256 |
|---|---|
| integration-build.log | `85b5a19661418ea80ad39a0514e90e8a1be8363c3ad53b62bc6f3350bb37fd48` |
| integration-Ai.log | `ce12c330b7c7f8fd443c1106458efd345df6e6796e5ebf7ce8ae83d88abb4316` |
| integration-Domain.log | `fcb2d6dc156868b4e24f001dc34b855ced7682667293bb0c3223f3b660405575` |
| integration-Contracts.log | `0f7cfc3efd8046b244ad015035f33040da6065dd567ac18969a1761f05c4791c` |
| integration-Client.log | `5fb0f05b13b5cb50cf89763134cf4e613349f54aea043dc494e6b83fe3887801` |

本批统一使用既有 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，源分支 `feat/v5-1-integration`、目标 `v5`。未验收的黄金目录与候选模型任务未纳入本次推送。主树用户未提交文件单独保存 SHA-256，用于集成回写后确认保留。

本次未运行 TableRuntime、PG/WSS/Lobby、Unity/IL2CPP、真机、物理网络及 V4 全量测试；本次发布头的远端 CI 尚无验收结论。历史头的 CI 通过记录不能替代本次头验证。实际 actor 接线、完整 P1.6 与 G1/G5 保持未完成。
