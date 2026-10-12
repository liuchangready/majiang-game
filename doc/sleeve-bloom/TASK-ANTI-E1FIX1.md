# SB-E1FIX1：Codex 独立检视退回

2026-10-12。已收到 SB-E1 ready_for_review 回调，但未验收。继续 E1 文件所有权：仅 engine/src/main 和进度/交付/回调文档；测试只读，不提交 Git，不委派，不修改其他模块。

Codex 独立执行 `:engine:test --tests 'com.majiang.cheat.bloom.*' --console=plain`，退出 1；30 项、3 失败、0 错误、0 跳过。新增反例是设计要求，不得删改绕过：

1. activation rejected during pause remains rejected after resume：同 requestId 的 HOST_PAUSED 首次领域拒绝未缓存，恢复后变 APPLIED 并扣费。
2. choice rejected during pause cannot execute later under the same request id：同样漏缓存，恢复后意外换牌。设计要求作用域通过后的首次领域成功/拒绝都入账；错误绑定/作用域不得污染。请同时核对功能禁用等提前返回分支。成功请求重放仍优先于暂停/新身份判断。
3. rinshan source with mismatched original slot rejects before charging：实体守恒仍有效但来源已失效，projection.canApply=true。投影、权威入口与协调器锁内均须验证 consumed rinshan slot == drawnTile，替补非空且属 DEAD_WALL，原摸牌属本人 HAND，pointer in 1..4；失败无扣费/offer/交换。正常来源也核对候选归属。不能只在选择 SECOND 时才发现原槽错误。

静态检视必须一并修复（此前 FEEDBACK-E1-INITIAL.md 第4/5项未处理）：

4. TableAuthority 两个新提交入口缺少 @Synchronized，应与同类既有入口保持相同同步边界，保证幂等、状态及 rekey 原子性。
5. ApplicationCoordinator.submitSleeveBloom 仍 val cost = 10。从权威经验证的 CheatEconomyConfig 传入/保存并读 costs["SLEEVE_BLOOM"]，不以默认参数10伪装修复。不要顺带改落汗。协调器锁内复核开关/经济/费用、身份和候选状态；选择在锁内重新核对 offer/当前轮次/候选，避免仅靠外侧旧读副本。

测试已就绪（无需等待新标记），按序运行专项30项与完整engine（预期407项，以实际XML为准），所有失败先修复。若夹具存在真实问题，只报证据由Codex修正。

更新 DELIVERY-ANTI-E1.md、PROGRESS-ANTI-E1.md、CALLBACK-ANTI-E1.json，写本次 taskId=SB-E1FIX1，真实命令/退出码/计数、处理各项和剩余项。全部通过才 ready_for_review，最后输出 CODEX_CALLBACK SB-E1FIX1 ready_for_review 并退出，由当前Codex再次检视。不得声明最终验收。
