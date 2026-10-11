# 落汗接口与 API 独立检视

Engine E1–E3/E3FIX1 已通过，详见 REVIEW-ENGINE.md。本文件记录后续接口阶段，不能据此宣称客户端或 UI 已可用。

## A1 线路契约（已验收）

Anti 通过 agy 接收 TASK-ANTI-A1.md，调用包含 `--dangerously-skip-permissions`。Codex 已移入并维护 10 项 `ProtocolTileMarkContractTest`。当前仍待生产交付、测试通过与回调。

源码初看发现组视图需防止 Int 局次加法回绕，且 futureRoundsRemaining 只能 0..3、最后有效局标识必须与剩余 0 局一致。Codex 已把这些断言并入线路畸形载荷用例；不增加测试数量、不修改生产代码。

首次 A1 XML（21:39:52）为 10 项、9 通过、1 失败：畸形组载荷未抛出 JsonParseException。Codex 另补充嵌套 tile/group 为 JSON null 的畸形载荷，要求统一 JsonParseException，不能漏成 NullPointerException；随后给此用例增加载荷编号与内容的失败说明，未降低断言。当前 A1 测试 SHA-256：58D8F496F45382DBE0CA704A58F79B3416C5B3E05194547ACEB00466991B8922。仍为 10 项，等待修复和完整 protocol 测试，不宣称通过。

## 后续准备

2026-10-11 21:46：收到 CODEX_CALLBACK A1 ready_for_review。Anti 完整 protocol 报告 16 套件、150 项全部通过；Codex 随后独立实际执行 10 项契约测试，全部通过，退出码 0。上列测试 SHA-256 保持一致。已检视严格载荷、组有效期、防御性集合、快照牌河定位、配置/能力/快照的手写复制与映射保留。先前失败已修复；A1 接受。Anti 另报全工程 Gradle 成功，此处不把缓存任务计为重新运行测试。

A2 现派发，5 项 Codex 服务端测试移入正常测试目录；后续客户端与 UI 尚未完成。

### A1FIX1 补充审查（已验收）

Codex 新增第 11 项无当前局快照携带牌河位置的回归，独立执行失败，确认构造器和反序列化仅在 roundId != null 时比较的漏洞。A1 原验收保留为历史，当前须完成 A1FIX1 才最终关闭。另派范围严格为 protocol 的修复，A2 继续 server 接线，测试依然由 Codex 维护。

22:01：收到 CALLBACK-ANTI-A1FIX1.json，复核仅把 DISCARD 当前局校验收紧为非空且相等。Codex 独立复跑 11 项全部通过、退出码 0；修复方完整 protocol 的 16 套件 /151 项 XML 已核对，零失败、错误、跳过。测试当前 SHA-256 为 2066D360644ED83D760B5BC7D10089DAF44B02FB0B93C558E32BF042FA561270。A1/A1FIX1 接受；A2 尚在开发，不宣称完整 API 已交付。

## A2 服务端（生产已验收，交付文档待更正）

agy 会话 e7bf99fe-30bf-46bc-81f7-b94342a0ea71，TASK-ANTI-A2.md，调用包含危险权限跳过参数并强制测试/回调。Codex 维护 TileMarkServerContractTest 4 项和 TileMarkUdpContractTest 1 项。前者新增“截止后重放保持原回执、不二次收费，新请求仍拒绝”断言；数量不变。

当前测试 SHA-256：
- TileMarkServerContractTest.kt：3B6209A6BC35BB43F53B803100725954AE718E7F77A8F28A62058FB2AE51B5D2
- TileMarkUdpContractTest.kt：25CC362FE8EDD5A697896387D45E8E455F34B2418820CE57442FFBAA5DA2EC10（另补实际未绑定 UDP 能力查询断言）

A3 的 6 项客户端契约与 1 项完整 UDP 门面贯通测试仍为 doc/tile-mark/tests 下草稿，尚未编译验收。P1 选择模型和位置/期限展示测试也仅为草稿；不能计作通过测试。

22:13：收到 A2 ready_for_review。测试哈希与上列一致。源码复核覆盖映射、绑定身份及队列内校验、原回执复用、时钟保留、完整私有快照变化感知、观战 API 白名单与未绑定 UDP 能力查询。Codex 初次全量命中 FROM-CACHE 后，强制执行 `:server:test --rerun-tasks --console=plain`，实际运行 14 套件、135 项全部通过，退出码 0（26 秒）。A2 生产接受。交付文档若干 helper/错误码名称不符合源码，已写入 A3 任务要求 Anti 更正，不据这些伪名称推断实现。

A3 现在派发；6+1 项测试已移入正常 client/server 源集，尚待实现与编译。P1 仍是草稿。

## A3 客户端（生产已验收，交付文档退回更正）

agy 会话 67c819fd-b020-45ad-b280-db95cb78960c，TASK-ANTI-A3.md。Codex 另补自动重连及旧连接晚到回执用例，现为 7 项客户端契约 + 1 项实际客户端 UDP 集成；不修改生产源码。

- TileMarkClientContractTest.kt：63406A0C6F1B859AE888E57BD0519BC650AFBAF35C0ADDFC63D0152C7088F0B3（含完整深复制保留断言）
- ServerConnectionSessionTest.kt（复用夹具新增延迟/异常/自定义重连）：AD80E75C94C7243A4BE981DDDA62911E3DF8D77CE1361B8A00D054D795327E5D
- TileMarkClientUdpIntegrationTest.kt：37E7E6FC366504689DFE26BC50EAE36977DC013802C54DE9A4D6C2174239C255

Codex 同步维护既有 tools/architecture/V4SmokeSession.java：新增可空 tileMark 构造参数传 null，原有无落汗的 GL 夹具语义不变；待客户端交付后编译校验。

22:32：A3 回调完成。完整 client 132 项、server 136 项 XML 已核对，零失败/错误/跳过。Codex 强制独立复跑新增客户端 7 项和真实 UDP 集成 1 项，并编译 `compileDiscardInputGateSmoke` 的既有 Java GL 夹具，19 个 Gradle 任务实际执行，10 秒成功。上列测试哈希保持一致。API 生产链路接受，表现层现在才开始。

DELIVERY-ANTI-A3.md 的 ownerSeat/colorId/markTurn/durationRounds/HAND/0费用等描述不符合实际代码，不能作为新契约；A2 也残留虚构 helper 表述。已在 TASK-ANTI-P1.md 明确要求 Anti 先按源码更正文档，再接 UI；正确代码无需迎合错误文档。实际接口说明已由 Codex 更新到 doc/SERVER_CLIENT_API.md。

A2 任务卡及服务端测试草稿已准备：真实本地绑定/推送、权威队列身份及 deadline、私有恢复、原生 UDP 三组标记/分片/观战拒绝/牌河位置。SharedRoom 当前 join 只允许 LOBBY，不存在对局中离房换人继承标记的路径；后续需保持这一边界。
