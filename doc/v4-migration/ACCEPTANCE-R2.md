# R2 模型边界验收记录

开发 Anti，验收 Codex。2026-10-05 16:53 派发；尚未最终交付/验收。

最近通过的完整构建是 R1 基线，R2 编辑中的文件不能沿用其通过结论。

## 开发前边界基线

递归门面测试确认客户端接口暴露领域 ActionReceipt、MatchConfig、ActionChoice、ShufflePlanRequest，以及 Any 载荷。测试源码 tools/architecture/v4-review/V4FinalArchitectureTest.kt；日志 build/v4-migration/R2-facade-baseline.log。该失败属于 R2 预期修复项，不覆盖 R1 已归档报告。

## 开发期间反馈

[FEEDBACK-R2.md](FEEDBACK-R2.md) 记录集合别名、物理牌身份、参数类型丢失、Gson 校验、兼容桥及客户端字段凭空新增等问题。

17:17 提前编译：protocol 主代码可编译，client/server 不通过。诊断集中在 ProtocolViewMapper 引用了实际协议中不存在的字段/枚举（274 条）；UdpClientBinding 两处、服务端两处类型接线问题。日志 build/v4-migration/R2-interim-compile.log。这是开发期间反馈，不是最终验收结果。

Codex 已把 R1 的独立用例改接协议 DTO 和 claimControl/releaseControl，保留行为断言；新增 V4ModelAcceptanceTest 验证所有物理牌、输入集合别名、配置与参数保真、严格解码及可空 roundId。领域 ActionChoice.roundId 本来允许为空，审查用例已按真实契约修正，不强加新限制。

17:30 第二次提前编译：错误减少到 3 条。EngineProtocolMapper.kt:144 和 ProtocolViewMapper.kt:147 的 ProtocolMeld 重载构造调用有歧义；ServerConnectionSession.kt:40 缺少 mapRoomSummaries。日志 build/v4-migration/R2-interim-compile-2.log。编译错误减少不代表行为、不可变性和边界要求已经通过。

用户于 17:34 左右要求暂停，Codex 已停止当前 R2 Anti 进程，保留修改，尚无最终交付。未继续运行测试或修改业务代码。

恢复后先检查在途修改、修复编译，再执行 R1 回归、R2 模型/边界测试、模块测试与平台构建；失败继续返修。

17:38 用户恢复任务，继续由 Anti 开发。R2 交付说明已生成，但 17:45 的实际编译仍失败：ApiMessage.kt 对 StartStacking/DrawTile 使用不存在的构造参数，尚未进入行为验收。已派发小范围 R2FIX1 修复编译和模块测试调用点。Anti 调用同时报告服务 503；交付说明中的“全部解决”不作为验收依据。独立模型用例补充空 roundId 线路往返、空嵌套对象、字符串伪装数值、嵌套集合不可变及物理牌身份验证。

R2FIX1 期间三层主代码均已编译；模块测试仍有旧接口接线错误。独立用例隔离执行（模块测试仍须后续全量运行）：R1 回归 21/21 通过，模型用例 13/17 通过，失败为暗杠 -1 来源座位、字符串 epoch、session.scores 可变和线路牌身份矛盾。日志 R2-first-behavior.log。门面反射审查先误将 Function 参数 `? super T` 的 Object 上界判为未类型化载荷；修正为读取具体下界后，单独重跑门面类型隔离通过，日志 R2-facade-check.log。不把该误报计为产品问题。

17:56 开始 R2FIX2，由 Anti 负责生产修复，Codex 负责既有模块测试接线，明确避免同文件冲突。Codex 将旧 EngineDtoMapperTest 的 6 项有效测试迁移到 ProtocolViewMapper，保留牌属性、14 动作、算分、完整快照、结算和不可变集合断言；更新 server 控制/回执类型及两个 IntArray 事件测试数据。实际模块测试：server 40/40，client 57/59（千术参数被字符串化、settlementDetail 常量 null），protocol 89/91（缺失 api/requestId 异常类型错误）。日志 R2-client-server-first-test.log、R2-protocol-first-test.log。全部问题交给 Anti，未放行 R2。

18:14 R2FIX2 实测客户端 59/59，通过参数与结算回归；独立组合 41/43（R1 21 项仍全绿），剩余客户端发布集合可变和未知嵌套字段接受。协议 90/91，剩余 yakuLines Int 被读为 Double。日志 R2FIX2-behavior-interim.log、R2FIX2-protocol-interim-2.log。

18:15 派发 R2FIX3。扩展模型基线 19/24：再确认必填 cursor 默认 0、分数小数截断、client MatchConfig 调用方集合别名三个问题，均属既定严格校验/不可变契约。日志 R2FIX3-model-baseline.log。审查同时要求移除 ClientParamValue 对原始数值的非对称 equals 兼容写法；Codex 已将原测试改为 IntValue.value 显式取值，不降低数值保真要求。

18:32 R2FIX3 当前代码通过 46 项独立检查、91 项协议测试；全量模块合计 713 项零失败零跳过（engine 340、protocol 91、client 59、server 40、presentation 183），桌面编译及 Android APK 构建成功。日志 R2FIX3-protocol-interim-2.log、R2-final-checks.log，模块计数 R2-current-test-counts.json。Anti 进程因服务 503 退出，尚未写该轮交付。人工审查发现严格 DTO 图校验仍有遗漏，暂不放行。

18:34 派发 R2FIX4（新 Anti 会话）。扩展模型 25/29：缺失回执 requestId 被接受、未知控制角色被接受、动作缺失 player 被当作 0、标量分数未知字段被接受；时钟小数用例已通过，无需虚报失败。日志 R2FIX4-baseline.log。仍属原定完整解码校验要求，未增加玩法范围。

18:36 R2FIX4 调用因 RESOURCE_EXHAUSTED / 429 额度限制退出，未产生新代码。工具当时提示约 10 分 39 秒后恢复；日志 R2FIX4-1791196462116.stderr.log。18:38 用户要求保存进度并暂停，已停止后续重试/开发/验收。无本轮开发进程遗留，保留用户 Antigravity 应用。继续任务须等待用户明确恢复；从 R2FIX4 已知 4 项失败接续，不重做已完成迁移。

19:42 用户明确继续。校验暂停记录的 299 个源码哈希无变化后，恢复 R2FIX4；Anti 20:00 返回成功交付。Codex 同期加强 EngineProtocolMapperTest：14 种动作比较完整往返对象，23 种事件和 ScoreBreakdown 比较完整递归字段，8 项映射测试通过（R2-mapping-full-fields.log）。

20:02 R2FIX4 独立验收 33/52；协议当次 90/91（Anti 后续追加的 8 个测试未包含在该次结果）。日志 R2FIX4-resumed-interim-2.log。主要回归是适配器新加 roundNumber >= 1，错误拒绝正常零基局数；另一项是强制 ApiResponse.requestId 非空。实际该响应字段为 String? = null，Codex 此前对此字段的非空要求错误，已更正独立用例和新增模块用例为“可空但非字符串类型拒绝”；ApiRequest.requestId 仍必填。缺玩家字段、未知控制角色及未知分数字段用例已通过，未放行 R2。

20:05 派发 R2FIX5，只修 ApiMessage.kt 的上述回归和响应可空字符串类型校验，不重写适配器、不修改领域与现有测试。待重新执行独立检查和完整构建。

## 最终验收：通过（2026-10-05 20:13）

R2FIX5 已交付并经 Codex 实测。R2FIX5-review.log：52 项独立验收、99 项协议测试全部通过，零跳过。R2FIX5-final-checks.log：全量 Gradle test、桌面 compileJava、Android assembleDebug 成功；模块 XML 合计 721 项（engine 340、protocol 99、client 59、server 40、presentation 183），零失败、零错误、零跳过。engine 测试复用 UP-TO-DATE，protocol 在独立检查时已执行，client/server/presentation 测试在最后命令中执行；不声称所有测试均在最后命令中重新执行。

接受 V4 独立协议模型、server 显式映射、client 自有门面类型及当前解码/不可变性修复。独立测试同时保留 R1 UDP/本地生命周期和丢包回归。旧 TCP 协议依赖 engine、旧展示链及无实现旧映射文件留待 R5/R6 删除；当前产品入口尚未切换，不以阶段通过宣称 V4 全部完成。

计数：build/v4-migration/R2-accepted-test-counts.json；源码哈希：build/v4-migration/R2-accepted-source-hashes.json。下一阶段 R3 允许开始。
