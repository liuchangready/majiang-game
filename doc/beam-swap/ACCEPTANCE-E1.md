# 偷梁换柱 E1 独立验收

2026-10-13，Codex。领域阶段通过；整个功能仍需E2网络客户端、E3表现层和最终联合验收，不代表已可在产品界面使用。

Anti按TASK-ANTI-E1交付并通过CALLBACK-ANTI-E1.json与stdout `CODEX_CALLBACK BS-E1 ready_for_review`回调。Codex编写测试并独立审查，期间修复准备阶段空死山越界、请求分支缺少校验、直接普通动作绕过挂起护栏、摸牌来源/阶段漂移未清offer、协调器锁内未复核完整候选等问题。

## 独立验证

- `./gradlew :engine:test --rerun-tasks --no-build-cache`：BUILD SUCCESSFUL，6项构建任务全部实际执行。
- XML报告合计471项，0失败、0错误、0跳过。其中BeamSwapContractTest 20项、BeamSwapWallAndRulesTest 24项；其余427项既有engine测试全部保留通过。
- 日志：`build/beam-swap-codex-e1.log`。Anti另完成131项千术专项及`classes`；这里不把Anti运行冒充Codex独立运行。
- `git diff --check`通过；现有SB-KAN-DORA生产变更及测试仍保留。

## 检视范围

正常后续8张截断、三类真实杠及第四杠、有效死山含替补与历史岭上引用、136物理守恒、禁止drawn/全部副露作为换出源；真实弃牌翻待翻指示、未来杠翻深层指示、立直自摸里宝与岭上算分；本人有效标记限定亮面及其他观察者无offer。

权威与协调器复核原子扣费、同取牌两术互斥、完整请求指纹和共用ID空间、非法选择保持有效offer/真实漂移清offer、锁内复核、时钟rekey、超时保持原样后摸切、暂停恢复、接管与收回及AI等待截止、旧快照与状态副本隔离、终局与新局清理。

E2继续验证线路严格校验、服务端绑定与队列、私有/公共推送、在途锁及真实UDP恢复；E3继续验证实际图形与输入。尚未进行完整功能验收、桌面GL或Android此次构建。没有Git提交或推送。
