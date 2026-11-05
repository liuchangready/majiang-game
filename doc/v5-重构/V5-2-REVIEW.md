# V5-2 / G2-C06 预审草稿：待最终平台证据

日期：2026-11-05（Asia/Shanghai）
预审结论：`PRE_REVIEW_COMPLETE`
G2 阶段结论：`G2_NOT_ACCEPTED`

本文件只整理已有证据、已接受范围和当前缺口；不是最终平台复核或 G2 签署。当前生产版本仍为 V4。预审所依据的业务源码候选为 `59e8bd741385b5d7b39ccdbc47573dde552067d1`；本树派发 HEAD `40229800432d444a70acbae05894e4a7dddae4c2` 与该候选分开记录。本次任务最终提交仅包含本文件、证据索引、缺口清单及任务卡，不改变受测代码、构建包或历史证据。

## P4.1–P4.5 / G2 门槛映射

状态解释：历史组件/候选 `ACCEPTED` 仅适用于对应 source、运行包和复核范围；当前应用包及 G2 未因此自动通过。证据路径均为原报告/原件路径，摘要文件哈希绑定见 [证据索引](V5-G2-C06-EVIDENCE-INDEX-20261105.json)。

| ID | 门槛断言与实现范围 | 已接受证据、源码身份与真实执行 | 当前状态 / 缺口 |
|---|---|---|---|
| P4.1 | LocalHost 复用 Client 命令/回执和 TableRuntime；LOCAL 不触网、不产生线上 token/钱包/排名；存档原子保存、损坏/旧版明确拒绝；真实整局/整场、恢复和原请求对账保持一致。 | `6850fcc1913301c9d58d473a447dfc3c5dbb7856`；最终 CI 独立复核原件：21/21 project/helper 行 PASS，其中14个测试工程 2872/2872，0 fail/error/skip/not-run；LocalHost 184/184、Client 182/182、TableRuntime 483/483、Lobby 89/89；真实 PG crash recovery、backup/restore、清理及环境恢复通过。CI run `41995915276` 实际 checkout source `59e8bd74…`，PR run `41995924338` 实际 checkout merge `0794af7f…`，不得混为同一源码 checkout。`V5-G2-FINAL-CI-GREEN-DELIVERY-20261105.md` 的专属 reviewer 追加接受 `6850fcc` 本地范围。 | 已接受本地服务/测试范围；不单独证明 Unity UI、Windows/Android 当前包或设备。保留 `FU-02 UNKNOWN`、`SYNC-03` 间歇风险。 |
| P4.2 | Unity 装配/导航和平台壳只依赖 Client 门面；静态程序集边界、实际启动/场景、字体/输入/屏幕适配与平台构建分别举证。 | C03 统一候选 `b33f0aad21c962a6f147dbfdb8a63fd98942d4a2` 的候选 Player/插件闭包及调用方复核有记录；C05 Android 工具接受不等于应用运行接受。D05 R6 应用候选 `3aec0c3a92bb87e954163ad1847c78dcb08eba3a` 有历史 PlayMode/Windows 包记录。 | 旧候选证据保持原身份。LocalHost DLL 在 `6850fcc` 改变后，旧 Unity/Windows 包不覆盖当前候选；Windows 当前 owner thread `thread_20261105_180011_21e1` 正在 R3。见 G2-C06-PLATFORM-WIN。 |
| P4.3 | 真实 Client 视图驱动四席牌桌、摸打鸣杠立直/三绝技与动画；乱序/重复/重连/到期/快照重置不破坏权威状态，UI 不伪造实体或私有候选。 | C03 统一候选保留真实 Player 样本：12 个不同 round、12 次 terminal、12 次 match commit、WestCompletion、matchFinished=true；269 个动作按真实 Applied/合法 receipt 语义核验。D05 R6 PlayMode 7/7 与同一 Windows 包 7 个场景/P2 属于应用候选 `3aec0c3a`；八个共享 DLL 与两个应用来源清单匹配。 | 历史候选范围已接受，但非 `6850fcc` 当前包。Windows R3 未完成；Android 当前设备/玩法证据未完成。黑帧、exit 0、菜单装饰桌与 fixture 不构成此门槛通过。 |
| P4.4 | 观战公共流与四席私有投影隔离；角色切换清除身份/游标/旧 binding；观战不可发玩家命令；结算来自结构化字段。 | D05 R7 独立复核实跑真实 WireCodec 与绑定/可见性验证：LocalHost 180/180、真实九帧 Windows capture 1/1；七个 canonical-hash 变体逐项拒绝；runner 33/33、Player source contract 47/47。D05 R6 的 PlayMode 7/7 与 Windows 同包七案/P2 被 reviewer 以原件复核并沿用。 | D05 组件及指定历史应用候选已接受；没有新候选同包的 UI/平台覆盖。历史 FU-02 原因未知，不作为无变化返修循环。 |
| P4.5 | 无人工改状态完成菜单→本地房→码牌→完整一局/整场/连庄或下一局；AI 托管/收回、流局、断网本地继续、强杀/重启/坏存档恢复；每节点对照 Domain；LOCAL 假线上资产入口拒绝；实际平台矩阵。 | 当前本地完整 CI 候选源码 `6850fcc`：21 行 PASS / 14 测试工程 2872/2872；Table 42/42、定向 13/13；真实 PG 证据通过。远端既有 push/PR 两 run SUCCESS，独立原件审计 4,782 assertions。C01、C02、C03、D03、D04、D05 组件分别保留自己的真实 source/run/package 身份和接受段。 | 代码/服务和远端 CI 证据可接受，但平台矩阵仍不完整；Windows 当前包未接受、Android 当前 APK/设备未接受。P4.5/G2 尚未接受。 |
| G2 | 独立复核 P4.1–P4.5 的测试/场景证据，记录桌面、Android、iOS 实际构建/设备、LOCAL 隔离、恢复、隐私投影和门面依赖；V4 保持可回退生产。 | P4 来源如上；远端当前身份快照确认 PR20 `OPEN/Draft`、base `v5`、head `59e8bd74…`，与受测 source 一致。查询原件 SHA `65E59A6C…`；此前 CI reviewer 报告记载的 09:26 UTC TLS 失败仍是历史事实，此次成功查询是后续补记。 | `G2_NOT_ACCEPTED`。Windows/Android 原 owner 续验及最终同候选包接受、独立 G2 签署仍待；本预审不等待平台线程结束，也不代其验收。 |

## 证据分类与已关闭范围

- **C01/C02/C03/D03/D04/D05 已接受部分保持接受。** C01 为执行边界/terminal UTC 组件；C02 为 controlled collection/verifier；C03 的统一候选保持 132/147 的真实对照结果（15 个失败仍留存，不剔除）；D03、D04 各自接受范围不扩写；D05 R7 专属 reviewer 已接受其任务范围。旧报告开头的 `PENDING`、`NOT_ACCEPTED` 由后续接受追加更新，不得截断历史或把旧暂态状态当最终状态。
- **真实应用结果按候选分开。** 保留真实 12-round 全场、四类动作、三绝技六行、D05 PlayMode 7/7、普通 Windows 同包七案/P2，以及各自源码、8 DLL、完整资源清单和 hash。它们不绑定到 `6850fcc` 新 LocalHost DLL 或当前 final C03 APK。
- **CI 分母不混用。** 21 行中 14 行是真实测试工程 2872/2872，7 行是 helper/build-only；Table 42/42 和 targeted 13/13 独立记录。预期 crash writer 非零退出是故障注入预期；skip/error/not-run 均按工程原始 XML/metadata 计算，不将 helper 行并入测试分母。PG 清理/恢复有原始过程证据。
- **结构与行为证据分开。** 门面/程序集方向用静态引用与项目结构核验；权限、私有/公开投影、切换清理、deadline/controlEpoch、幂等/对账、136 实体守恒用相应结构化测试或真实应用证据。静态检查不称运行验证，测试绿也不自动证明结构边界。
- **远端 CI 与 PR 身份。** `41995915276` push 真实 checkout `59e8bd74…`；`41995924338` pull_request 真实 checkout 合成 merge `0794af7…`。两者 API head 都指 source，但不可用 API head 冒充 PR job checkout。caller 于本任务派发前成功查询 PR20 与 `git ls-remote`，结果记录在 `build/caller-preflight-20261105/query-results.json`。原审计过去发生的 TLS 失败不删除、不改写，新的查询只补足查询时点身份。

## 平台矩阵与阶段边界

| 平台/入口 | 已有证据 | 当前判定 |
|---|---|---|
| Desktop Unity Editor / PlayMode | D05 R6 历史候选 7/7；不是新 LocalHost 候选包。 | 历史候选 PASS；当前候选同包覆盖 BLOCKED/待补。 |
| 普通 Windows Player | D05 R6 历史同包七案/P2、8 DLL 与包清单闭合；LocalHost DLL 后续变化后需重绑。当前 Windows 原线程 R3 reworking。 | 当前最终包未接受。 |
| Android 完整 ARM64 APK | C05 历史 development APK 52,142,409 bytes、SHA `0506AEEA…`，APK 结构/aapt2 与工具门禁已核；非当前 C03 候选。实际设备枚举此前无设备，玩法/触控/视觉未跑；当前 Android 原线程 R8 reworking。 | 当前候选 APK 与实际设备/模拟器运行未接受。工具 fixture 不计游戏运行。 |
| iOS 真机 | 无本阶段真实构建/设备原件。P4.5 按计划列为后续 V5-4 硬缺口；G2 不把 G4 的 iOS 真机覆盖提前为前置。 | `NOT_RUN`，明确保留为 G4/V5-4 边界。 |

真实远端 WSS/HTTPS/PG 整场、弱网、发布/回滚和 iOS 属于后续 G3–G5 或明确平台边界，不新增为 G2 前置。G2 仅放行本地体验；V4 仍可回退生产。

## 残余风险与正式裁决进入条件

当前明确缺口 ID 为 `G2-C06-PLATFORM-WIN`、`G2-C06-PLATFORM-ANDROID`、`G2-C06-FINAL-SIGNOFF`，详情见 [G2-C06 缺口清单](V5-G2-C06-GAPS-20261105.md)：Windows 当前应用候选包闭包/运行、Android 当前候选 APK 与真实设备覆盖、以及最终 G2 独立签署。历史 `FU-02 UNKNOWN` 首败原因未解释，`SYNC-03` 为间歇风险记录；未声称根因修复，也不无变化重开已接受实现。

正式 C06 裁决需由原 owner 完成当前候选平台证据并绑定准确 source/package/hash；原 owner/reviewer 核验上述缺口退出断言后，专属 reviewer 才能签署 G2。平台线程继续运行不影响本次预审结论。此草稿没有批准阶段切换、合并、推送、部署或 V4 下线。
