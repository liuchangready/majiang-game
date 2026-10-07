# V2 清理修复检视（验收通过）

2026-10-05 11:04（Asia/Shanghai），Anti 负责开发，Codex 独立复现、拆分、审查、增加验收测试并实跑。当前 core 批1清理造成的编译和行为断点已修复，验收通过；engine 批2/批3兼容入口仍按计划保留。

## 最终结论

| 检查 | 结果 |
|---|---|
| engine / client / core / server | 340 / 34 / 183 / 4，共 **561测试全部通过，0失败、0跳过** |
| 桌面 classes、Android assembleDebug | 通过；Android未做真机验证 |
| 真实 OpenGL 验收工具 | **11个全部通过** |
| 测试保留 | 原core的178个@Test保留，新增5个独立验收；未禁用测试 |
| 旧工具接口扫描 | RenderSmoke可执行代码不再调用GameSession/FakeGameSession/LocalGameSession/getEndpoint/getSession |

最终证据：`build/v3-migration/v2-final-validation.log`、`build/v3-migration/v2-final-gl-validation.log`；JUnit XML位于各模块 `build/test-results/test`。GL联跑包括 Desktop、RoundSession、Meld、Furiten、DiscardInputGate、SettlementDetail、MeldChoice、RiichiChoice、MeldDisplay、V3、EndTenpai，全部输出PASS。结算25张截图、输入门控4次动画与2次提交、立直确认、副露3次确认、击飞排名及听牌展示均保留，已检视结算与牌桌关键截图。

## 根因与最终修改

1. **编译遗漏**：GameScreenTest迁移后缺导入。迁移为真实TableHost→GameClient→V3适配器后恢复编译。
2. **旧同步测试语义**：UI回调转postRunnable后，夹具在错误位置等待、可能UI自锁，测试提交后立即检查旧状态。改为UI线程识别与有界FIFO等待，超时直接失败；未使用sleep轮询或吞异常。
3. **V3快照不完整**：fake固定身份/游标且丢结算事件。修复游标演进、终局身份/恢复事件保留及防御性复制；下一局请求不等于权威已切局。
4. **真实覆盖被替换为假提交**：GameScreen和两种皮肤测试原来fake.choose只计数。改为真实Screen提交并验证13张闭手、本人牌河增加和完整物理Tile身份。RECORDED是响应窗口待决的合法回执，结合权威状态断言，不强制仅APPLIED。
5. **节奏用例装配不完整**：传给TableHost的外部coordinator未初始化指定controllers，宿主实际取了coordinator默认AI策略，破坏确定性两圈场景。初始化同一controllers列表后原顺序、队列及动画门控断言通过，未增加步数上限。
6. **实际UI回归**：删除旧polling后，立直/副露确认仍读屏幕旧currentView。现在确认读取client.snapshot中最新合法候选，失效则取消选择并刷新；旧候选不调用choose。保留Meld原零提交断言，新增Riichi同类验收。产品代码修改限定GameScreen这两处分支，服务端仍最终验证。
7. **验收工具漏迁移**：9个旧GL脚本不在默认test编译链中。迁移为真实V3入口或纯展示GameScreenClient，跨帧处理回执，保留全部原截图和行为约束。共享helper的泛型错误经审查返修。

分工与过程见 [修复分工](V2-CLEANUP-FIX-PLAN.md)。A系列处理共享夹具，B/F处理真实集成与皮肤/结算覆盖，C/D/E处理图形工具，G处理最新候选校验。下文为历史失败与返修记录，不代表当前结果。既有D8 Kotlin metadata警告仍存在，但Android任务成功；本轮未升级工具链。

## 已验证证据

- 全模块基线构建在 `:core:compileTestKotlin` 失败：`GameScreenTest` 遗漏 `PlayerView` / `GameState` 导入。日志 `build/v3-migration/v2-cleanup-baseline-20261005.log`。
- engine 340、client 34、server 4 个测试通过。core 当次未执行，不能把旧 XML 中的 178 个结果计入本轮通过数。
- 桌面产品源码编译与 Android assembleDebug 通过；D8 Kotlin metadata 警告属于工具链输出，不是本次任务失败根因。
- 默认 V3 真实 OpenGL 验收通过，日志 `build/v3-migration/v2-default-v3-smoke.log` 含 `V3_DEFAULT_ASYNC_CLIENT_RENDER_PASS`：准备阶段选择并确认千术、扣点、14张起手、实体点击弃牌、AI 接续和返回菜单释放宿主。
- 单独编译全部旧 GL 工具发现9个失败任务，日志 `build/v3-migration/v2-tools-baseline.log`：Desktop、RoundSession、Meld、Furiten、DiscardInputGate、SettlementDetail、MeldChoice、RiichiChoice、MeldDisplay，均引用已删构造器或 session/endpoint 属性。

## 审查重点

1. 缺导入是第一层阻塞；不能据此认定补导入即修复完毕。
2. 迁移后 Screen 始终通过 `postRunnable` 消费更新和回执。旧测试在动作后立即断言、Fake 在自身 choose 中 drain、在 UI 线程等待 UI 自身，均不符合实际异步顺序。
3. Fake 快照固定游标/身份且忽略 events、缺失终局恢复字段，会在无事件刷新时丢结算；应按 V3 快照契约维护。
4. `GameScreenTest` 和 `GameScreenSkinSmokeTest` 的打牌测试曾直接调用 fake.choose，绕过装饰器内真实 coordinator 提交。必须证明实体牌进入牌河，不能只断言 APPLIED。
5. 真实牌局测试保留136张物理守恒、立直/杠/振听和动画门控等原有约束；view-only fake 不应伪装成完整 authority。
6. 不恢复已删V2入口，不把批1扩大到 engine 批2/批3，不删测试或削弱断言。

## 派发状态

首轮 A/B/C 三个 Anti CLI 任务超时，无文件修改与交付，不能计作完成。已按 A1（共享夹具）、B1（真实 GameScreenTest）、C1（两个真实GL入口）小批重发；后续依据实际编译、测试和文件审查继续分派。分工总纲见 `V2-CLEANUP-FIX-PLAN.md`。

### 第一批交付检视

- C1 首版因调用不存在的 `Tile.getId()` 未通过编译，退回改用 data class 的完整 `equals`（包含copyIndex）。返修后 Desktop 与 RoundSession 均真实GL通过，日志 `v2-c1-smoke-pass2.log`，保留实体弃牌、AI后续和释放宿主断言。
- A1 首版存在错误的 PlayerDecision 参数名与 GamePhase 枚举，编译不通过；另有吞掉UI等待超时的 catch，审查拒绝。A2 正返修。
- B1 改为真实 TableHost→GameClient→Screen 提交，但加入 sleep 轮询；已退回 B2 改为确定性等待，并迁移同样绕过真实动作的皮肤测试。
- Codex 新增 `V2CleanupFixtureAcceptanceTest`，独立检查 UI线程不自锁、空刷新/未确认下一局请求保留结算、发布终局身份延续和可变事件数组隔离。待夹具编译修复后运行。

### 第二批实际结果

- A2 后一个nullable赋值编译错误经A3修复；夹具4个独立验收全部通过。
- `v2-core-pass3.log` 当时执行182个core测试，179通过、3失败：真实GameScreen回执为RECORDED（响应窗口待决，原断言仅APPLIED）、MeldChoice“silently stale”用例错误主动广播更新、RiichiPacing最后E2E未走满两圈。B3处理。
- B2/C2再次CLI超时，仅有部分文件修改；交付不计完成。B2部分改动还引入不存在的addSnapshotListener及吞异常等待，已明确退回。
- Meld图形工具已编译并真实运行通过，`v2-meld-smoke.log`。剩余工具按每批两文件分发C3/D3/E3。F3单独修正皮肤测试的假提交覆盖并补真实自摸UI结算断言。
