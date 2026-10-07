# 击飞终局与听牌提示验收

2026-10-02。已完成。Codex 负责设计、独立测试、构建和截图审查；Antigravity 负责全部生产代码实现及返修。设计见 [END-TENPAI-DESIGN.md](END-TENPAI-DESIGN.md)。

## 结果与根因

权威规则原本能正确执行击飞终局。V3 宿主的自摸击飞、流局罚符击飞、零分阈值与终局供托一次结算均通过实测。直接的界面问题是 MATCH ENDED 点击只关闭末局明细，让玩家留在已结束的牌桌；另有 ROUND_END 与 Session FINISHED 判断不一致的恢复边界，以及下一局请求失败时提前丢失明细的问题。

现在整场终局统一采用权威会话结果，末局明细可以进入总成绩，展示终局原因、排名与最终积分，并可返回菜单或查看本局明细。终局不能推进下一局或穿透请求托管。断线时仍支持本地结果导航与返回菜单；下一局失败保留结算，成功后由新局快照清理。

默听、立直、副露听牌都会显示本人待牌。计算复用 FuritenRules，支持两面、七对子、国士十三面和杠后四张上限；摸牌时注明“原听牌”，打牌后刷新。牌型待牌不代替役种和振听合法性判断，不展示未知牌山剩余数。面板使用当前牌面皮肤，准备、终局及选择模态期间隐藏。

默认击飞规则仍为小于0分；等于0分是否击飞由原 bankruptcyInclusive 配置决定。本次未改动领域终局算法、网络协议或规则默认值。

## 最终验证

| 模块 | 测试 | 失败 | 跳过 |
|---|---:|---:|---:|
| engine | 340 | 0 | 0 |
| client | 34 | 0 | 0 |
| core | 178 | 0 | 0 |
| server | 4 | 0 | 0 |
| 合计 | 556 | 0 | 0 |

相对539项基线新增17项测试。两项旧UI测试更新为“权威新局快照到达后关闭结算”，保留并加强其动画队列清理、明细页签及分页重置断言，没有删除测试。

最终执行并通过：

```powershell
.\gradlew.bat -I tools/architecture/end-tenpai-smoke.init.gradle :engine:test :client:test :core:test :server:test :lwjgl3:classes :android:assembleDebug :lwjgl3:endTenpaiSmoke --console=plain
```

完整日志：[end-tenpai-final-validation.log](../../build/v3-migration/end-tenpai-final-validation.log)。真实 GL 输出 `END_TENPAI_RENDER_PASS`，包含断线情况下“总成绩→本局明细→总成绩→返回菜单”，并断言客户端关闭、进入 MenuScreen。真实 TableHost→GameClient→GameScreen 的击飞结算与排名切换另有集成测试覆盖。

已查看最终截图，说明文字、长振听标题、全部十三张牌均在面板内，手牌和操作按钮未遮挡，排名行颜色对比清晰：

- [默听待牌](../../build/v3-migration/tenpai-silent.png)
- [国士十三面](../../build/v3-migration/tenpai-thirteen.png)
- [摸牌前待牌与振听提示](../../build/v3-migration/tenpai-original-furiten.png)
- [击飞总成绩](../../build/v3-migration/bankruptcy-final-ranking.png)
- [终局恢复](../../build/v3-migration/bankruptcy-recovery.png)

Android 已验证构建，未做真机触控验收。所有改动保留在当前工作区，未提交 Git。

## 开发追溯

生产实现见 [IMPL交付](delivery-ANTI-END-TENPAI-IMPL.md)、[界面返修交付](delivery-ANTI-END-TENPAI-POLISH.md)、[断线导航交付](delivery-ANTI-END-TENPAI-OFFLINE.md)。首个委派包含过多历史迁移上下文，中止后改为限定本次设计的任务；随后所有实现/返修均经 Antigravity 完成。桥接记录保留在 build/v3-migration/bridge-ANTI-END-TENPAI*。
