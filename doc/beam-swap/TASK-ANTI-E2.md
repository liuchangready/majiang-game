# BS-E2：线路、服务端与客户端

仅在Codex明确验收E1并下发后开始。阅读AGENTS、DESIGN、INTERFACES、E1交付。Anti负责生产，Codex负责测试；不得自行改测试、启动其他代理、提交或推送。

E1已由Codex独立471项验收，见ACCEPTANCE-E1.md。本阶段可开始。E1额外写出的根目录CALLBACK-ANTI-E1.json是你自己的重复产物，请移除该重复文件，仅保留doc/beam-swap中的正式回调；以后不在仓库根目录生成回调副本。

## 实现范围

- protocol独立同构模型、配置beamSwapConfig与capabilities.beamSwapSupported（缺失false）、PlayerSnapshot.beamSwap、严格TypeAdapter以及SUBMIT_BEAM_SWAP/CHOICE。候选名称固定ProtocolBeamSwapCandidateView；client对应BeamSwapCandidateView。隐藏实体ID不得加入未标记条目。
- 分支SWAP必含handTileId和targetSlot，KEEP_ORIGINAL省略二者且显式null拒绝。拒绝未知字段、目标实体ID、非整数/负索引、未知kind/action、互斥载荷错配。更新所有API标签互斥矩阵及已知标签列表。
- server双向EngineProtocolMapper、配置能力、ServerTableHost串行提交及状态变化推送、SharedRoom、InProcessBinding、UdpRoom、UdpTableServer。绑定校验先于权威重放；普通/公开快照不得泄露他人offer；观战显式禁止两API；保持已执行回执重放和拒绝身份行为。
- client.api独立模型配置能力、GameSession/ServerConnection门面及映射、ServerConnectionSession两新入口共用全部动作的在途锁、失连/关闭/重绑定future处理、有序完整快照缓存恢复、旧服务能力默认关闭。
- 不改UI，不打开产品入口；保留engine默认关闭以及现有SB-KAN-DORA改动。如发现engine缺陷可以修正并说明。

## 测试与交付

Codex-owned新测试：ProtocolBeamSwapContractTest、BeamSwapClientContractTest、BeamSwapHostContractTest；另需Codex提供UDP链路测试。等待build/beam-swap-e2-tests-ready.json后运行对应测试，再不筛选运行:protocol:test :client:test :server:test :engine:test。Gradle只由Anti运行直到回调；测试源码/夹具问题写FEEDBACK-ANTI-E2.md交给Codex，不自行降低断言。

完成实现并修复失败后写DELIVERY-ANTI-E2.md、CALLBACK-ANTI-E2.json（recipient=codex,taskId=BS-E2,status=ready_for_review，真实命令结果/数量/边界），stdout输出CODEX_CALLBACK BS-E2 ready_for_review后退出。Codex独立检视不通过则返工再回调，不能直接进入E3。
