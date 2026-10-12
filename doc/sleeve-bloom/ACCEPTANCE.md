# 袖里藏花最终独立验收

2026-10-12，Codex检视通过。设计文档已修正，Anti/agy实现生产代码，Codex编写60项新增测试并独立复验。未发现阻塞本次交付的问题；Android真机触控未验证，构建结果与设备验证分开记录。

## 最终版本的独立验证

Anti的SB-E3FIX2回调退出后，Codex实际执行，未以开发者报告或缓存代替复测：

```powershell
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle test :lwjgl3:classes :android:assembleDebug :lwjgl3:sleeveBloomSmoke --rerun-tasks --no-build-cache --console=plain
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle -I tools/architecture/tile-mark-visibility-smoke.init.gradle -I tools/architecture/tile-mark-hint-smoke.init.gradle :lwjgl3:compileTileMarkVisibilitySmoke :lwjgl3:compileTileMarkHintSmoke :lwjgl3:sleeveBloomSmoke -PsmokeWidth=960 --console=plain
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeDisconnect=true --console=plain
```

三条命令均退出0。第一条88个任务全部实际执行，35秒；包含完整测试、桌面classes、Android调试APK与1280常规GL。第二条也验证了本次适配的旧Java冒烟夹具能够编译。

| 模块 | JUnit XML测试数 | 失败/错误/跳过 |
|---|---:|---|
| engine | 407 | 0 / 0 / 0 |
| protocol | 158 | 0 / 0 / 0 |
| server | 141 | 0 / 0 / 0 |
| client | 136 | 0 / 0 / 0 |
| presentation | 268 | 0 / 0 / 0 |
| 合计 | **1110** | **0 / 0 / 0** |

新增60项：领域30、协议/宿主/客户端/本地/真实UDP16、表现层14。覆盖136实体守恒、正常及三种杠后候选/回退、实际四杠上限、费用/幂等/隐私、超时与控制权恢复、换牌后动作和役种、严格线路与观战伪造、在途锁/乱序快照、模态交互/身份失效/提示边界。完整回归保留原规则、算分、计时、码牌和出千测试。

真实GL结果：1280×720输出`SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=563`；960×540输出同标记、frames=572；关闭真实会话输出`SLEEVE_BLOOM_DISCONNECT_GL_PASS fee=0 frames=432`。常规流程使用生产菜单、服务端、客户端和牌桌输入，验证SECOND/FIRST、只扣两次费用、deadline不变、摸牌绘制槽与权威实体一致、双向模态禁用像素和返回菜单释放会话/服务端。断线流程验证模型与入口显示同时禁用。Codex查看了两种尺寸的秘籍、候选与落点截图，未见本次修复涉及的越界或遮挡。

日志：[全量与1280](../../build/sleeve-bloom-final-codex.log)、[960与旧夹具编译](../../build/sleeve-bloom-final-gl960-codex.log)、[断线GL](../../build/sleeve-bloom-final-disconnect-codex.log)。截图：[1280候选](../../build/sleeve-bloom/screenshots-1280/offer-0.png)、[960候选](../../build/sleeve-bloom/screenshots-960/offer-0.png)、[960换牌落点](../../build/sleeve-bloom/screenshots-960/kept-0.png)、[秘籍](../../build/sleeve-bloom/screenshots-960/technique-rules.png)、[断线入口](../../build/sleeve-bloom/screenshots-1280-disconnect/disconnected-entry.png)。构建产物：[Android调试APK](../../android/build/outputs/apk/debug/android-debug.apk)。这些构建产物位于工作区，未作为源码提交。

## 检视、返修及回调记录

- E1：首次领域提交后，Codex增加反例并退回暂停拒绝幂等、岭上来源门禁等问题；修复后领域30项及完整engine407项独立通过，见[领域验收](ACCEPTANCE-E1.md)。
- E2：16项新增契约通过，Codex强制复测protocol/server/client435项通过，见[线路验收](ACCEPTANCE-E2.md)。独立审查包括串行队列、绑定身份、私有投影、无事件推送及客户端防重入，维持V4依赖边界。
- E3首轮未验收：原下发的理牌高度36是Codex指令错误，已明确修正为44，保留旧测试；另补禁用/超时提示及误报反例。独立268项曾有2失败，真实GL发现秘籍越界、来源文字遮挡和模态入口仍高亮，按[E3FIX1](TASK-ANTI-E3FIX1.md)返修。
- E3FIX1通过测试及双尺寸GL后，Codex用真实关闭连接场景复现入口仍高亮，按[E3FIX2](TASK-ANTI-E3FIX2.md)退回。最终绘制和输入复用模型可用状态，新增断线GL通过。

所有开发任务使用`agy.exe --mode accept-edits --dangerously-skip-permissions --print`，续接使用`--conversation`。阶段完成均写交付报告、CALLBACK JSON并以`CODEX_CALLBACK`标准输出通知当前Codex；回调只是提交检视，不等于验收。最终回调见[SB-E3FIX2](CALLBACK-ANTI-E3FIX2.json)。早期报告中“理牌测试夹具缺陷”的判断已撤销，以本报告和更正后的设计为准。

文档同步了物理回退、四杠门禁、幂等及线路游标元数据边界、恢复时钟、布局坐标、真实接口和共享字体所有权；中央[接口文档](../SERVER_CLIENT_API.md)已补齐两类请求。`git diff --check`通过。本次提交包含袖里藏花相关源码、测试和文档；用户原有lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/等改动保留在工作区，不纳入提交。
