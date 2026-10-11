# A1FIX1：没有当前局的快照不能携带牌河位置

Codex 追加 ProtocolTileMarkContractTest 的第 11 项 `discard position cannot belong to a snapshot without a current round`，已独立执行并复现失败。ProtocolPlayerSnapshot 构造与 ApiMessage 的快照适配器只在 roundId != null 时比较位置局号，导致当前局缺失却包含 DISCARD 定位的畸形快照被接受。

范围仅 protocol 生产中上述两处边界。DISCARD 位置要求非空当前 roundId 且严格相等；不改变合法 lobby/准备快照、无标记及 NOT_VISIBLE 的可空局号规则。不要修改测试或 server/client/engine，不要提交 Git 或启动代理。

运行 11 项 ProtocolTileMarkContractTest 和完整 :protocol:test；修复到全部通过。写 DELIVERY-ANTI-A1FIX1.md、CALLBACK-ANTI-A1FIX1.json（recipient codex,status ready_for_review），输出 CODEX_CALLBACK A1FIX1 ready_for_review 后退出。A2 服务端由另一个 agy 会话独立执行，不修改其任务或 PROGRESS-ANTI.md，避免交叉覆盖。
