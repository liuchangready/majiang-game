# G2-C01 独立验收：执行边界与 terminal UTC 一致性

日期：2026-11-01。线程：`thread_20261031_214650_17ee`。结论：**接受本任务组件**；不批准 G2/P4.5 整体阶段，不合并、部署或改变 V4 生产状态。

接收方 final：`5d103e3614e09fc7d4632b352c7944b13b6b1ce7`，代码提交 `aec0c9a52c353b9220e06ed74343cfcb734b3749`，冻结 base `c3c95af6023e13915656acd686269475d5dcceb8`。调用方在隔离分支将完整 successor 及其驱动依赖合入现有 PR20 的 `a8d58ffb81957e3f2afb7281d1d009b834e0ee5d`，候选 merge 为 `4b743e6964d2b643e484d4071830d5e3def86d32`。没有建立第二个 MR，也没有纳入未接受的 C02。

## 接受依据

- 共享生产执行边界处理提交、精确原请求 Applied、可见事件与 terminal commit；未决请求阻止提交/开下一局，Dispose 保留 unresolved 身份。Draw 的 RoundEnded 从 observer 不可变历史按原身份与 commandVersion 配对，后续 publication 不再抹去确认。
- 内部 timeout/AI 耗墙与外部 Pass 路径保留真实 reservation 正反对照、终态不可复活和136张守恒。历史 r15 与 D-1-05(b) 保持认可，不用旧成功样本替代本轮证据。
- `TableActor.BuildCheckpoint` 复用 `timingOverride.CapturedAtUtcMs`；无 override 时只读一次 UTC。普通与作弊 durable terminal 共用这一入口。validator 的 UTC/monotonic/BootId 校验保持严格；没有冻结生产时钟、扩大等待界、重发新 ID 或跳过终局确认。
- R2 范围外 SessionCoordinator 扩展与额外报告已从最终树撤回。R4 指令允许在既有 Runtime 测试文件补 advancing-clock 回归；调用方认可使用现有 N2 settlement fixture 所在 `N2HostSettlementTests.cs` 的最小测试扩展。接收方最终11文件为原9文件、原已允许的 TableActor 与该测试文件；没有新生产层扩展。

## 调用方实际验证

在 `D:/majiang-wt/caller-c01-r4-closeout-05750bbd` 强制 Release Rebuild 并执行原生 DLL runner，构建和测试 native 均0，零 fail/error/skip/not-run：

| Suite | Pass/total | 原生 XML SHA256 |
| --- | ---: | --- |
| Unity.TableModel | 235/235 | `D9FCC5B56751A9B305DD2062A2184C6B330A50FBED4FAE90D9EB7CECDB29E9A6` |
| TableRuntime | 483/483 | `7B0EE975FC0A415E55EA1D5DE1CA4203BDA676A0822877868F8D63D96C1EA0B0` |
| LocalHost | 127/127 | `1F1C281CB66886BADE0F047EAAB1200A61BCD06177CD95CB8F606BD719DF3263` |
| TableService，集成适配后 | 203/203 | `2AF5E22778426A1C6867A4DA8F610068AE4C0F293F470BCC2AF07FF82FE1BF52` |

合计 **1,048/1,048**。原 `VerifyP45FullMatchDriver.Tests.ps1` 实跑 **32/32、native0**。

原始第一次 TableService 为202/203、native1：B2 历史诊断测试明确要求 UTC 跨毫秒时返回 CheckpointInvalid/无 outbox；修复后真实返回 Accepted，因此这个旧故障预期已过时。调用方仅在集成分支把该测试改为 advancing UTC 的成功回归，保留真实 host/actor/clock，增加检查点三项时间身份一致、真实 RoundEnd、唯一 outbox 解码及原请求重放 Replayed/版本不变/仍一条 outbox。定向1/1与完整203/203均通过，没有改生产代码、跳过测试或刷新旧结果。首次失败 XML、日志和后续结果分别保留。

独立真实 Host/Client 探针从新局version0通过71条真实合法 Actor 提交自然耗墙到 wall0/seat0 Draw/136张，再以原 canonical Draw 经 TableSession→ITableChannel→LocalTableRuntime→TableActor 提交。冻结 UTC 和每次读取+1ms均得到**一次原 ID Applied、Ready、pending0、真实 RoundEnd**；继续读取独立排队的 terminal push，未用仅收到 receipt 冒称终局。未改状态/原件/墙、伪造回执或转派。该探针是受控本地 durable 流程，不是 PostgreSQL 或设备证据。

探针结果 SHA256 `70E9B796FD2FC21DB425EC93BB8C26F82CC6E3FC885F86F53491518E2C64BF63`；断言汇总 SHA256 `2B84A37B2CD1E8DC84A84C30315AB0716B34B7B15186D6360EBA66A1EE8AD400`。

## 独立核对接收方新 Unity/Player 原件

调用方重验原件，没有声称另跑一轮 Unity/Player：

- PlayMode attempt14：**7/7**、记录 native0；XML SHA256 `3777ED8FDBE7CF1887FBEC44777009E3BFCD626C951BDD436820F4FF376A1F4F`。
- Windows Player `p45-20261101-032455-f08dfc06`：**12个不同 round、12次 terminal、12次 match commit、WestCompletion、matchFinished=true**，native0/tool0，分数 `[25000,25000,25000,25000]`，排名 `[0,1,2,3]`。269次动作均有原 Applied；其中56次普通 response Pass 合法只需 receipt，其他动作有可见事件确认。无 CheckpointInvalid。
- 完整205文件长度/SHA匹配；共享插件149个源码输入与最终源吻合，7个自有 DLL 与 Player Managed 包逐项吻合。source.json 记录 f8ed9090 加构建时 dirty R4 patch；该补丁摘要核验通过，两项改动的新 blob 与最终代码一致，其余 App 源码在 f8ed→final 未变。没有把构建时 dirty 状态隐藏为 clean HEAD 构建。
- Mutation F 原件为483项中仅新 UTC 回归失败：旧双读 UTC 确实被 CheckpointInvalid 拒绝；不是放宽 validator 得到绿色。

Player summary SHA256 `575BF95B811B5DEE316FBB82F9A02350D5458EC3F5E5FA0BB8A2D266A84C3539`；调用方原件审计 SHA256 `75769F249C56099DB4021ADC4E41AC2CAD53C4AEA8DC9CE5FC13E60BC6E3DBB5`。

## 证据与剩余边界

调用方原生日志/XML、原始失败、source-results、最终 TableService XML、独立探针、原件审计与出处均在隔离树 `build/caller-review/`。接收方首失败、R2/R3失败、原成功和 mutation 记录保留。最终补充提交只包含上述服务测试适配与本验收文档；已测生产源码不变。

这次修复了此前 B2 所记录的 Runtime UTC 原因，但**没有重跑真实 PostgreSQL 自然终局、outbox/重启验收**，不能据此宣布它们通过。B2 cold-win calculation persistence 的既有缺口仍保留。C02、C03统一候选、C04恢复后对账/零重复、C05平台/视觉及 C06/G2阶段裁决各自独立；本组件接受不替代这些工作。新发布头 CI 也不能继承 f99227a 的历史双 CI 结果。PR20保持 Draft，不操作 PR24，不合并/发布/部署，不改 scheduler 或 complete.flag。
