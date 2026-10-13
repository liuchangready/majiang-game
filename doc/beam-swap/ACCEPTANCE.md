# 偷梁换柱最终验收

2026-10-13，Codex独立验收通过。Anti负责生产实现；Codex负责新增测试、GL脚本、检视和验收。E1、E2分别回调后独立验收；E3首轮检视退回修复，Anti以`reviewRound=1`再次回调并退出后，Codex完成最终复验。未提交或推送代码。

## 验收结论

偷梁换柱已贯通engine、protocol、server、client、presentation及产品入口。每次激活扣10，仅原有暗手牌与候选槽位交换；当前摸牌／杠补牌及全部副露不可交换。普通窗口提供接下来的至多8槽，杠后提供有效死山暗槽与替补；已取岭上槽、已翻宝牌排除。未标记候选保持牌背，仅本人有效落汗命中显示牌面。136张物理守恒、取牌指针与摸牌来源保持，延后杠宝时序沿用既有雀魂规则。

激活与选择具有独立强类型请求、共享幂等命名空间和在途锁；首次权威拒绝回执也保留原cursor重放。保持原样、交换及陈旧窗口清理即使没有领域事件，也通过完整快照更新。取消和超时不退款、不延长截止；与袖里藏花同次实际取牌互斥。正常旧服务缺失beam视图时不出现可用入口，底层配置默认关闭，产品三入口显式启用并配置10点，用户显式禁用仍保留。

## Codex独立测试与构建

```text
./gradlew test :lwjgl3:classes :android:assembleDebug --rerun-tasks --no-build-cache
```

退出码0，BUILD SUCCESSFUL，86项Gradle任务全部执行。结果来自本次XML统计，而非沿用Anti报告：

| 模块 | 测试数 | 失败／错误／跳过 |
|---|---:|---|
| engine | 471 | 0／0／0 |
| protocol | 172 | 0／0／0 |
| server | 151 | 0／0／0 |
| client | 146 | 0／0／0 |
| presentation | 285 | 0／0／0 |
| 合计 | **1225** | **0／0／0** |

相较本次开发前1131项基线，新增94项均由Codex编写：领域44、协议14、宿主及真实UDP9、客户端10、表现层17。包含普通／杠后候选、第四杠、标记与隐私、实体守恒、算分／合法动作刷新、费用及同取牌互斥、幂等冲突、超时／暂停／接管／恢复、真实UDP替换绑定，以及UI双选／重复点击／推送先于回执／模态输入／缺时钟禁提交等。已有SleeveBloom几何测试仅按统一布局将y=100改为106，其余既有断言保留。

证据：[完整日志](../../build/beam-swap-codex-full.log)、[统计](../../build/beam-swap-codex-full-results.json)。Android调试APK已生成，桌面classes已编译。测试源码哈希复核未发现Anti更改Codex测试；`git diff --check`通过。

## Codex独立真实GL验收

```text
./gradlew -I tools/architecture/beam-swap-smoke.init.gradle beamSwapSmoke beamSwapLayoutSmoke --no-parallel
./gradlew -I tools/architecture/beam-swap-smoke.init.gradle beamSwapSmoke beamSwapLayoutSmoke -PsmokeWidth=960 --no-parallel
```

两条命令均退出码0。1280×720产品流程输出`BEAM_SWAP_GL_PASS choices=2 timeouts=1 fee=30 frames=1365`，960×540输出相同通过标记、frames=1369。两种尺寸的最大布局均输出`BEAM_SWAP_LAYOUT_GL_PASS candidates=13 hand=13 marked=1 frames=6`。

- 产品流程通过真实菜单、进程内服务端、客户端私有推送和指针点击，完成SWAP、手动KEEP、等待真实权威超时KEEP及默认弃牌；核对三次各扣10、无重复提交、摸牌未被交换、原deadline未延长、返回菜单资源释放。
- 最大布局使用明确的客户端快照夹具，在真实OpenGL下显示13候选＋13暗手牌＋1张标记亮面，验证最后一张双选、手牌与理牌输入隔离及提交去重。它验证死山最大布局，不冒充真实杠动作端到端GL；杠的领域及宿主链路由测试覆盖。
- Codex已查看两种尺寸截图：候选/手牌/按钮未越界，普通候选显示相对顺序，汗标在牌体之后绘制，五个按钮使用共享布局。截图共20张，位于`build/beam-swap/screenshots-{1280,960}`及`layout-screenshots-{1280,960}`。

证据：[1280日志](../../build/beam-swap-codex-gl-1280.log)、[960日志](../../build/beam-swap-codex-gl-960.log)、[普通双选](../../build/beam-swap/screenshots-1280/offer-0.png)、[1280最大布局](../../build/beam-swap/layout-screenshots-1280/maximum-selected.png)、[960最大布局](../../build/beam-swap/layout-screenshots-960/maximum-selected.png)。build下为本地验收产物，可用上述脚本再生成。

## 检视修复闭环

E1/E2已处理实体与候选基线漂移、串行校验、拒绝回执重放、空候选JSON绕过、能力声明遗漏和无事件推送，见[领域验收](ACCEPTANCE-E1.md)、[线路验收](ACCEPTANCE-E2.md)。E3测试先拦截缺时钟激活、快照到达后模型未刷新及新入口吞掉旧鸣牌／立直取消点击；旧用例全部恢复通过。

[E3第一轮检视](FEEDBACK-CODEX-E3-REVIEW1.md)的四项问题均已修复：默认单调时钟、普通候选相对序号、汗标覆盖层顺序、集中按钮布局；旧局错误toast也按session/round/syncEpoch过滤。交付描述已按实际实现修正。最终独立源码检视未发现剩余阻断项，V4单向模块依赖保持。

## 验证边界

Android仅完成APK构建，未做物理真机触摸验证。真实产品GL覆盖普通摸牌路径；最大死山布局采用客户端夹具，不能当作真实杠GL证据。公网账号、撮合、持久化和加密传输仍属项目既有未实现范围，本功能不改变这些边界。
