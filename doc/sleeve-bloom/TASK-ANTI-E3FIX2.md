# SB-E3FIX2：补齐断线入口禁用显示

E3FIX1的1110项和双尺寸GL通过，Codex已看截图确认排版/模态修复有效。本次仅一个剩余接线问题，不重新设计。

SleeveBloomPanel.render入口`canOpen`仍读取旧快照`bloom.canApply`，忽略模型中的连接/控制权/余额门禁。断线后模型canActivate=false，但入口仍橙色，显示与输入不一致。请将入口绘制条件接到`model.canActivate`并保留现有忙碌、暂停、pending和其他模态条件；不改模型、不改测试。通常只需一行生产修正。

Codex新增真实GL断线场景：通过生产牌局进入canApply=true窗口，关闭真实GameSession，等待两帧；断线模型已禁止激活，但像素仍rgb216,114,38。独立执行失败：

```powershell
.\gradlew.bat -I tools/architecture/sleeve-bloom-smoke.init.gradle :lwjgl3:sleeveBloomSmoke -PsmokeDisconnect=true --console=plain
```

日志build/sleeve-bloom-disconnect-initial.log，截图build/sleeve-bloom/screenshots-1280-disconnect/disconnected-entry.png。不是夹具缺陷。默认GL双选择流程保持不变。

只允许presentation/src/main必要修正及本次交付/回调文档。测试、脚本、其他模块、Gradle、用户文件只读。Codex已无Gradle进程。修复后跑完整1110项（test --rerun-tasks --no-build-cache）、桌面classes/AndroidassembleDebug、原1280及960GL、上述新增断线GL，全部0失败再交付。无需重复架构规划或用户确认。

写DELIVERY-ANTI-E3FIX2.md、CALLBACK-ANTI-E3FIX2.json（taskId=SB-E3FIX2、recipient=codex、真实命令/计数/构建和三次GL结果），输出`CODEX_CALLBACK SB-E3FIX2 ready_for_review`并退出回调。不要改/删测试，不委派，不Git提交，不递归另启Codex。Codex负责最后独立复测。
