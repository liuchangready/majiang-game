# R6 旧链测试保留与替代清单（准备审计，非验收结果）

**最终处理：** 本文件是 R6 前的准备审计和风险清单；完成后的实际迁移、例外与 978 项全量/188 项独立运行证据见 [R6 验收](ACCEPTANCE-R6.md)。下文“尚未”只代表准备审计时点。

用户授权继续 R5/R6，但当前 R4 尚未接受。本清单由 Codex 只读审查形成，未改生产、未构建。下述“已有覆盖”仅表示找到对应测试源码，最终须逐项核对断言并记录实际执行结果，不能以测试名称相同代替验证。

路径缩写：E=`engine/src/test/kotlin/com/majiang/`，C=`client/src/test/kotlin/com/majiang/`，S=`server/src/test/kotlin/com/majiang/`，P=`presentation/src/test/kotlin/com/majiang/`，R=`tools/architecture/v4-review/`。

## 领域与旧宿主

| 旧测试 | 必须保留的语义 | 处理与替代证据 |
| --- | --- | --- |
| E/v3/TableHostAcceptanceTest、HostRhythmAcceptanceTest、HostAiIsolationAcceptanceTest、BankruptcyHostAcceptanceTest、ShufflePreparationAcceptanceTest | 私有发牌、幂等、超时一次、暂停不赠时、AI不阻塞、破产及供托、码牌隐私/预算/截止 | 五文件20方法在 S/server/host/ServerTableHostCoverageTest.kt 有同名迁移，另有 disabled shuffle；逐方法核对，保留既存20方法哈希基线。 |
| E/v3/TimeControlAcceptanceTest、ShuffleAlgorithmAcceptanceTest、ShuffleAtomicDealAcceptanceTest、MatchEconomyAcceptanceTest、CheatLedgerIntegrationAcceptanceTest | 20+3、旧计时器隔离、136物理ID/死牌山/可复现、失败不扣费不安装部分状态、跨局幂等 | 有效领域测试原地保留，不因v3目录删除。尤其 ShuffleAtomicDeal 的重复物理牌/余额不足需同时断言version、费用、牌山、手牌未变化。 |
| E/session/SessionSnapshotContractTest、FuritenViewContractTest、MeldChoiceProviderContractTest、TenpaiPreviewContractTest、PlayerPresentationPreviewProviderTest | 深拷贝、摸牌/岭上/里宝牌隐私、振听/七对/国士、赤五物理ID/全部鸣牌组成/optionId | 模型和算法仍为V4使用，保留。R/V4PreviewAcceptanceTest 只补新链路，不能替代规则样例。 |
| E/session/LocalGameSessionContractTest | 回执与订阅分别保证私有事件隐私、越权拒绝、活动局不能nextRound、退订/坏listener隔离、关闭后拒绝 | 改接InProcess/runtime。R/V4AuthorityAcceptanceTest、V4ServerHostAcceptanceTest、V4RuntimeAcceptanceTest只有部分等价，逐项补齐。 |
| E/session/SeatControllerSessionAcceptanceTest | 非0座位、AI填充策略、显式控制不被覆盖、托管无可提交候选、旧输入拒绝且守恒 | 移到V4装配测试。仍需指定viewerSeat、控制列表防御复制、显式AI strategy不重置的具体断言。 |
| E/architecture/ClientBoundaryAcceptanceTest | 同桌隔离、幂等冲突、controlEpoch、伪造scope、不可变回执/候选、他座响应不误废合法候选、跨局旧选择无效 | 迁到server集成测试，不让engine依赖server；原110/135/152/174行边界不能按旧Endpoint整体删除。 |
| E/architecture/CoordinatorConcurrencyAcceptanceTest | 查询只看完整提交、竞争仅成功一次 | 原18行用150ms超时证明被AI锁阻塞，阻塞形状不适用于V4。以V4Authority/ServerHost并发测试替代，同时保留一致提交断言。 |
| E/state/MatchIntegrationContractTest | 多观察者结算数组独立及役番、赤宝牌、轮次、本场、南四、供托一次结算 | 仅117行直接实例化LocalGameSession的用例迁新边界，不整文件删除。 |

E/architecture/{RoundBoundaryAcceptanceTest,SessionBoundaryAcceptanceTest,ExternalValidationAcceptanceTest,ArchitectureDependencyAcceptanceTest}.kt 仍验证重用领域边界、冻结局、候选原子性和供托，保留并更新确有变化的架构断言。

## 客户端与真实网络

| 旧测试 | 已找到部分替代 | 必须核对/补齐 |
| --- | --- | --- |
| C/v3/AsyncClientAcceptanceTest | R/V4RuntimeAcceptanceTest 的早期推送、关闭、恢复缺口、同步异常Future、pending所有权/晚回包 | 原60行物理牌ID精确匹配，不能按牌型代替；选择失败后仍可提交。 |
| C/v3/ClientSynchronizationAcceptanceTest | runtime去重/隔离；ServerConnectionSessionTest相同内容不同cursor | 明确跨roundId的相同事件仍是两个事实；一个listener修改支付数组不能污染另一个，不能只用CollectionIsolation名称声称覆盖。 |
| C/v3/TableLifecycleAcceptanceTest | V4 lobby/reconnect/Host开局、掉线接管、reclaim、超时 | 旧局候选/请求不能跨局生效，旧结算仍可查；配置资源进入私有快照；可信账本扣款发布但不刷新时间；动画忙仍执行权威超时。 |
| C/v3/RecoveryProjectionAcceptanceTest | bankruptcy及模型/PUBLIC部分终局字段 | 建议四场景整体迁server集成：晚加入终局排名/资源/完整breakdown、私有扣款reason不上线/分页拒绝；下一局清terminal保history；控制主动发布；暂停fresh快照serverNow冻结且恢复deadline不赠时。DTO往返不等价。 |
| C/v3/NetworkCodecAcceptanceTest、ShuffleProtocolAcceptanceTest | V4ModelAcceptanceTest、ProtocolViewMapperTest、typed codec | scoring/furiten/clock/资源/private preparation全部字段；未知目标/未知字段/非整数或溢出epoch拒绝；码牌异步/重复点击gate/关闭pending。 |
| C/v3/ClientTransportAcceptanceTest | ReliableRequestorTest有界5次失败、UdpFrameCodecTest、runtime关闭 | TCP EOF/长度前缀形状可撤销；UDP坏包不能永久挂请求，关闭结束全部在途请求。坏UDP可丢弃，不照搬必断线。 |
| S/v3/NetworkAcceptanceTest | V4ReviewAcceptanceTest、V4RuntimeUdpAcceptanceTest | 原30行真实四客户端14/13/13/13、53唯一ID、订阅本座13条TileDealt且无他座私牌、越权拒绝、重复命令只弃一牌；73行旧连接close不影响替代连接。加入smoke不等价。 |
| S/v3/ShuffleNetworkAcceptanceTest | 新Host coverage、V4LobbyConfigurationAcceptanceTest部分预算 | 尚未找到完整真实UDP等价：确认方案幂等、别座不可见selection/手牌、resume保同deadline与40 reservedPoints、被替代连接不能提交。将整场景迁UDP+client门面。 |

优先补足完整替代：四客户端真实UDP私密发牌、真实UDP码牌恢复、资源/结算/暂停时钟的端到端恢复。

## 表现层（R5迁移时就要保留）

- P/v3/{AsyncScreenAcceptanceTest,ScreenRecoveryAcceptanceTest,EndTenpaiScreenAcceptanceTest,V2CleanupFixtureAcceptanceTest}.kt：非阻塞/pending单次提交、终局恢复不重播、拒绝续局保结算、成功请求等待权威新局才清空、断网仍查历史、旧选择/计时失效。runtime测试不能证明画面正确。
- P/screen/{RiichiChoiceScreenContractTest,MeldChoiceScreenContractTest,FuritenScreenContractTest,RiichiScreenIntegrationTest,GameScreenSessionContractTest}.kt 与 P/architecture/ClientSelectionAcceptanceTest.kt：物理牌/optionId、选牌不提前提交、取消不算过荣、碰后振听、各杠只一张岭上、非0 viewer、无合法动作无按钮、振听仍可自摸。Preview数据测试不能替代点击提交。
- P/screen/{DiscardAnimationIntegrationTest,DiscardInputGateAcceptanceTest,RiichiDiscardOrderAcceptanceTest}.kt 与 P/architecture/RiichiPacingAcceptanceTest.kt：事件FIFO、吃碰移走不复活、全队列结束解锁、结算/新局清动画、每步最多一动作且不吞他人荣和。legacy eager mode演示可替换成V4直接节奏断言，权威timeout仍有效。
- P/screen/SettlementDetailPresentationAcceptanceTest.kt、P/ShufflePreparationUiAcceptanceTest.kt：全部役/宝牌分页、符与各付款人本场展示、里宝牌隐私、倒计时不重置、重连不重扣保留额、超时不提交草稿、旧回执不解锁新请求。
- P/state/GameplayLoopRegressionTest.kt：无役荣和/自摸拒绝、PASS不吞AI荣和、摸切标记；领域断言可归engine，画面与权威一致性留集成。
- P/render/{OpponentMeldDisplayAcceptanceTest,MeldDisplayContractTest,GameScreenSkinSmokeTest}.kt及其它理牌/皮肤测试：迁client DTO，保留实体牌/赤五/方位副露/资源生命周期，模型往返不代替渲染。
- P/state/ArchitectureBoundaryContractTest.kt：更新V4 client.api边界，保留不公开mutable authority、不在画面构造第二规则权威。

`engine/src/testFixtures/kotlin/com/majiang/testing/FakeGameSession.kt` 实现旧GameSession；用presentation自有client.api fake替代，不往engine testFixtures引server。P/testing/FakeScreenClient.kt同样迁移。

最终交付每项格式：旧文件/方法 → 保留或新测试路径/方法 → 关键断言 → 实际执行结果。未完成替代的有效断言不能随旧链删除。
