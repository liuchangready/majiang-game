# SB-E3FIX1：Codex 独立检视退回，修复后全部通过再回调

2026-10-12。E3首轮交付不予验收。Codex编写测试、检视与验收；Anti只修改presentation/src/main及本次进度/交付/回调文档。所有测试、GL脚本、其他模块、Gradle和用户截图文件只读，不委派、不Git提交。

## 真实复测证据

Codex执行 `-I tools/architecture/sleeve-bloom-smoke.init.gradle :presentation:test :lwjgl3:sleeveBloomSmoke --continue --rerun-tasks --no-build-cache`：268项中2失败、0错误、0跳过。日志build/sleeve-bloom-e3-review-initial.log。失败为旧理牌高至少44，以及新增失效候选在deadline后清除不得误报超时（后者是原14项测试内新增反例，仍14项）。

1280及960真实生产会话GL均跑通两次施术、SECOND和FIRST、总扣20、不赠送deadline、返回菜单释放会话；但截图确认秘籍溢出与来源文字遮挡，因此交互闭环通过不等于视觉验收通过。初始截图build/sleeve-bloom/review-initial-1280；960截图build/sleeve-bloom/screenshots-960。Codex随后给GL脚本增加双向模态禁用颜色断言，脚本仍由Codex维护。

## 必须修复

1. **覆盖原CLI指令的尺寸更正**：理牌必须为`Rectangle(1080f,50f,140f,44f)`。原CLI中的h36是Codex的错误指令，在此明确撤销；不是测试夹具缺陷，不改旧测试。落汗y150/h44、藏花y100/h44、托管y6/h36保持，上下间距6/8。TASK-ANTI-E3和VISUAL-DESIGN均已修正，不需要用户审批。
2. **超时提示不能仅比较时间**：当前只按第一条新clock.serverNow>=oldOffer.deadline并且offer消失就提示，会把deadline后失效清理误报为“保留第一张”。须核对同session/syncEpoch/round/有效控制上下文、原第一张发生权威默认摸切等证据；新snapshot其他座位clock.serverNow也可提供权威时间，不能依赖事先收到expired offer快照。终局、身份替换、候选失效、手动完成不误报。保留正例与所有反例；不要改测试。
3. **双向模态禁用显示**：落汗选牌时袖里藏花入口必须灰色；袖里藏花二选一时落汗入口也必须灰色。现在输入拦截正确但绘制仍橙色。让渲染/输入共享一致条件，包含连接、暂停、pending、忙碌、其他模态；保留两个功能。GL已检查两个实际按钮填充色，并输出other-modal与offer截图。必要时可改TileMarkPanel的可选渲染参数，不能改engine。
4. **来源文字被按钮盖住**：两种尺寸的offer-0.png中“已摸到/牌山次张”仅露出顶部。卡底y290、按钮顶y269，BitmapFont.draw y274不能视为字形底部基线。最新视觉稿建议draw y286，按实际字形界限验证完整显示。不要挪到卡内遮牌。
5. **秘籍溢出**：technique-rules.png第5行越过面板右边界x1020。采用适当宽度换行/分行并调整各行间距，保持可读字号。完整准确说明“第二候选为牌山次张或杠后王牌替补；未选牌留在或退回对应位置；超时保留原摸牌并摸切，费用不退”，不得靠删掉规则绕过溢出。不要让警告、关闭按钮与正文重叠。

## 验证与回调

Codex当前无Gradle进程，执行权交Anti。先通过`:presentation:test`全部268项，再真实执行以下验证并核对XML：

```powershell
.\gradlew.bat test --rerun-tasks --no-build-cache --console=plain
.\gradlew.bat :lwjgl3:classes :android:assembleDebug --console=plain
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke --console=plain
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeWidth=960 --console=plain
```

全仓应1110项（engine407、protocol158、server141、client136、presentation268），以新鲜XML实际结果为准。失败必须修生产后重新验证，不得将非零失败标ready_for_review，也不得把旧高度测试称作夹具缺陷。脚本若有真实夹具错误给Codex证据，不自行改脚本。

写PROGRESS-ANTI-E3FIX1.md、DELIVERY-ANTI-E3FIX1.md、CALLBACK-ANTI-E3FIX1.json（taskId=SB-E3FIX1，recipient=codex，命令、实际计数、构建/GL结果、剩余问题）。全部通过后标准输出`CODEX_CALLBACK SB-E3FIX1 ready_for_review`并退出回调当前Codex。Codex会独立复测和查看截图。不得递归另启Codex或自行Git提交。
