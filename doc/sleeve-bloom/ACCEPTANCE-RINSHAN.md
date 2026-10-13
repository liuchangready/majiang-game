# 袖里藏花岭上候选规则修订验收

2026-10-13。状态：Anti已完成开发并回调，Codex独立检视、全量复测与双端构建通过，未发现阻塞交付的问题。

## 最新规则

杠后第一候选为刚摸到的岭上牌，第二候选仅为下一张未取岭上牌。选第二张时，将原摸牌写回下一岭上槽，下一次合法杠补可取回它。普通牌山、王牌替补和岭上指针均不因选择变化；实际杠次数、宝牌、摸牌来源与计时不变。第四杠后禁止施术，不扣费；不能将宝牌指示牌当作第五张岭上牌。

此规则替代初版“王牌替补为第二候选、选择后回退岭上指针”的规则。对话中间讨论过的“普通牌山头端两张”方案已被最新指令替代，未实施。

## Codex 测试范围

更新 `SleeveBloomWallAndRulesTest` 原9项中的相关测试，新增5项，共14项：

- 原语仅交换两张实体，其他牌序、普通牌山、替补列表和指针不动；下一次补牌取回未选原牌。
- 暗杠、加杠、大明杠均覆盖 FIRST/SECOND；连续四次真实杠检验换牌后的补牌顺序与第四杠拒绝。
- 岭上换牌后的真实自摸计分保留岭上开花，正常摸牌和地和规则不变。
- 激活无物理移动、扣费一次、候选仅本人可见、重放幂等、deadline不变、超时保留并摸切原牌。
- 下一岭上槽改变后拒绝旧选择，清理offer但不移动牌、不退费、不加时。
- 普通活牌山耗尽时，若下一岭上牌仍存在且未到第四杠，仍可选择。
- 第四杠即使指针显示有余量，也在投影、权威、协调器和物理原语拒绝，不扣费。
- 第三杠可选择第四岭上牌；后续第四杠不能再选，宝牌槽完全不动。

## 验证结果

Anti的[交付报告](DELIVERY-ANTI-RINSHAN.md)与[回调](CALLBACK-ANTI-RINSHAN.json)收到后，Codex独立执行：

```powershell
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle test :lwjgl3:classes :android:assembleDebug :lwjgl3:sleeveBloomSmoke --rerun-tasks --no-build-cache --console=plain
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeWidth=960 --console=plain
```

第一条命令退出0，88项任务全部执行；第二条退出0。JUnit XML实际汇总如下，所有模块均0失败、0错误、0跳过：

| 模块 | 测试数 |
|---|---:|
| engine | 412 |
| protocol | 158 |
| server | 141 |
| client | 136 |
| presentation | 268 |
| 合计 | 1115 |

袖里藏花领域专项35项通过，其中墙体与规则14项包含本次新增5项及更新的原测试。桌面classes与Android调试APK构建通过。

真实桌面GL结果：1280视口 `SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=576`，960视口 `SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=571`。两次均验证正常摸牌二选一、实际输入、扣费及回到菜单释放资源；Codex已查看两尺寸秘籍截图，第四杠提示完整、文字无溢出。真实GL场景未专门制造杠局面，杠后候选、连续四杠及宝牌边界由领域测试覆盖，标签分支由代码检视确认。Android实体机触控未验证。

日志：[全量与1280](../../build/sleeve-bloom-rinshan-codex-full.log)、[960](../../build/sleeve-bloom-rinshan-codex-gl960.log)。截图：[1280秘籍](../../build/sleeve-bloom/screenshots-1280/technique-rules.png)、[960秘籍](../../build/sleeve-bloom/screenshots-960/technique-rules.png)。构建产物为工作区本地产物，不纳入源码提交。

## 独立检视与交付记录

Codex核对了物理原语、权威激活与旧offer校验、协调器、私有投影四处规则一致，均限定下一岭上槽和实际杠次数；选择不影响其他实体、指针、时钟或算分来源。界面标签、提示及秘籍已同步，设计、图形设计与线路接口文档已清理旧替补候选和回退指针语义。`git diff --check`通过。

首次专项出现一项测试夹具的时钟同步遗漏，Codex补齐同步后35项全过，未放宽生产门禁或断言，详见[反馈](FEEDBACK-RINSHAN-INITIAL.md)。Anti网络错误后按用户要求重试同一会话，完成全量验证并输出 `CODEX_CALLBACK SB-RINSHAN ready_for_review`；下发持续使用 `--mode accept-edits --dangerously-skip-permissions`。根目录重复交付文件已清理，正式记录保留在本目录。

本次提交包含岭上候选规则修订的代码、测试和文档；用户原有lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/改动保留在工作区，不纳入提交。
