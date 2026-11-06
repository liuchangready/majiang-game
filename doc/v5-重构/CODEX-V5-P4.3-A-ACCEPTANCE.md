# V5 P4.3-A 独立验收（2026-10-27）

结论：**APPROVED，仅限 P4.3-A 玩家牌桌纯模型范围**。
同一任务 `thread_20261027_032015_62d3` 第 6 轮已由 caller 实际复验，
并通过桥接 `approve_task` 置为 COMPLETED。完整 P4.3、G2–G5 未批准。

## 范围与版本

- 冻结基线：`929e6e7fea1d2a609b274c118da1b6f904291a13`。
- 接受候选：`8d1d367a9ae8f80254da3ead9375d6aa1e47b2bb`；最终代码提交 `1b7518e`。
- 候选净增 23 文件：五个 Table 生产 C# 文件及五个 meta、独立测试工程、runner 与交付报告。
- 未改 Contracts/Wire、App/场景/asmdef、解决方案注册或其他 owner 源码。
- 原任务树 tracked clean，基线到候选 `git diff --check` exit 0。

五个模型分别处理已确认快照的玩家投影、不可变展示数据、候选选择与 pending、
保留原 request/scope 的 canonical 命令意图，以及有界确认动画队列。
动画不影响确认状态、输入或服务端 deadline；本席与公开内容分开授权。
真实 Client 集成测试经过 TableSession 的 HELLO/WELCOME/全量基线与
TableSessionOperationQueue.SubmitAsync，校验业务字节、原 request 和 outbox 去重。
它使用测试 channel，不证明真实服务端或网络端到端交互。

## 本轮缺陷关闭

`D-R5-01`（MAJOR）已关闭。R4 的 `_meldActions` 关联记录现在与
`_items/_seenKeys/_seenOrder` 一起在 `ClearItems()` 清除。
调用链覆盖接受新基线、重连/确认上下文切换、显式 Clear 与 RejectAndClear。
旧记录不再跨 generation/binding/round 吞掉新上下文首个确认副露。

正式回归增加双输入顺序 × 四种 reset 共八条，以及显式 Clear 一条。
新上下文首来源 `Enqueued=1/PendingCount=1`，第二来源 `Enqueued=0`；
既有授权身份、旧 previous、游标、batch 双向关联、Pon→AddedKan 独立播放回归保留。
本轮检视未发现尚未关闭的本范围编译、测试或功能阻断。

## Caller 实际验证

执行位置：`D:/majiang-wt/p43-models-ds-20261027`，接受候选原始生产源码。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-p43-models.ps1 -OutputDir build/p43a-models/codex-review-r6-20261027
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/run-codex-r6-probes.ps1
git diff --check 929e6e7..HEAD
```

| 检查 | 实际结果 |
| --- | --- |
| Fresh Release build，真实五文件 Compile Link | native exit 0 |
| Fresh DLL direct xUnit v3，XML/console 一致 | 134/134 passed，failed/skipped/errors/not-run 全 0 |
| 现有 Unity 静态边界 | native exit 0，PASS |
| 原强度边界负例矩阵 | native exit 0，13/13 |
| 原 R1/R2/R3/R4/R5 独立探针，fresh build/run | 12/12、7/7、4/4、8/8、8/8，合计 39/39；全部 native exit 0 |
| Unity Editor/PlayMode/Player/IL2CPP、本轮设备/跨物理网络 | NOT_RUN |

Runner 原始证据：`build/p43a-models/codex-review-r6-20261027/`，含命令、
native exit、原始日志、XML、`p43a-evidence.json` 与 `evidence-sha256.csv`。
探针证据：`build/p43a-models/codex-review-r6-probes/` 的 R1–R5 build/run 日志。
这些 ignored 产物保留在上述任务树，不提交二进制或伪造远端证据。

| 文件 | SHA256 |
| --- | --- |
| results.xml | `79BB01F2F1DB7E87393A3940A623127E0FD07EA42784CD4406067D0C5A79DD3F` |
| Mahjong.Unity.TableModel.Tests.dll | `D2F5598F5A6EB11B22936AF8F650B66543ECED7CEB57EE3E9C3D4DC3978A622A` |
| evidence-sha256.csv | `F8BCAD782C0F64462060AC1D0DB6D918288A903377E2ABA113E998C108101F55` |
| ConfirmedAnimationQueue.cs | `8B1865405E7832CE918BFE4937DB57413139B4142C4708FDDBBA7DCFCC7803C5` |
| test-p43-models.ps1 | `450E5094BCF6581BDEA56989F59ACF3DC1B5E59EA42FDEBEC928F17B89DC4EC4` |

## 批次集成与后续边界

Caller 从 origin 的既有 PR20 源头 `929e6e7` 创建独占集成树
`D:/majiang-wt/codex-p43a-accepted-20261027`，fast-forward 保留全部候选提交，
另增本验收文档；生产、测试与 runner 字节与接受候选完全相同。
统一沿用 `feat/v5-post-g1-batch -> v5` 的 draft PR20，普通 push 一次，
不创建组件 MR、不 merge 到 v5。新头远端 CI 尚待单独复核。

查询任务账本时，同批已接受 P5.2、伴随校验接入及 P4.2-core 均已同步 PR20；
P4.1、P5.1、P4.4 尚在途，没有其他已批准且未同步的交付需要并入本次 push。
不纳入其他 receiver 的在途代码，不因其尚未交付否决本范围。

P4.3-B 仍需等待 P4.4 释放共享注册/装配范围，真实本地接线等 P4.1/P4.2-B；
后续负责场景、MonoBehaviour、typed visible event 供给、真实 Unity 导入和视觉交互。
模型对上游合同无法表达字段的缺口继续遵循交付报告，不推导隐藏事实。
G1 APPROVED；G2–G5 未批准；V4 仍为唯一生产。181 classified 未覆盖、
Android FAIL、iOS BLOCKED、真机/跨物理网络 NOT_RUN 保留，不创建 complete.flag。
