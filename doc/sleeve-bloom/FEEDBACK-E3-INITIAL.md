# Codex E3 初检反馈：保持原理牌触控契约

2026-10-12。新增10项通过，但完整presentation264项中GameScreenTest的auto sort starts off and toggles on each click失败。原因是Codex下发的理牌高36与既有高至少44契约冲突；测试正确，不改测试。

Codex已更正TASK-ANTI-E3.md、VISUAL-DESIGN.md、TESTS-READY-E3.md：理牌改为(1080,50,140,44)，其上沿94与袖里藏花下沿100间隔6；与托管上沿42间隔8。落汗(1080,150,140,44)、袖里藏花(1080,100,140,44)、托管(1080,6,140,36)不变。请Anti按修正后的任务改生产，再跑presentation及后续验证。

另有源码初检项，需修复并纳入交付：

- 禁用入口点击目前仅返回true，没有按设计显示中文disabledReason提示。
- 权威超时清除offer时缺少“已保留第一张、额度不退”提示；不能在本地倒计时归零时提前关闭offer，也不能把手动选择/终局/身份切换误报为超时。
- SleeveBloomPanel入口绘制未考虑落汗选牌模态，与GameScreen输入条件不一致；保持所有其他模态下的禁用视觉与点击一致。
- 秘籍新增整句很长，需在真实GL核对换行与边界；候选来源文字y274与按钮上沿269仅5像素，也需按实际字形核对重叠。Codex会在回调后运行GL并提供具体截图。

这些问题不授权修改测试。若当前执行已结束，请将待修项如实写回调，由Codex正式续发返修；不得声称全量通过。

Codex补充：已新增3项屏幕回归（禁用入口提示、权威超时后提示、手动/新局不误报），E3现共13项。旧理牌高度测试继续保留。新增测试当前尚未由Codex执行，不能宣称通过；请实现上述反馈后运行。

Codex进一步检视：不能只以oldOffer非空/newOffer为空/未手动提交判超时；正常终局、失效清理、session/syncEpoch变更均会清offer。已增加第14项专项terminal stale or binding reset offer clearing never masquerades as timeout（当前尚未执行）。需要同一有效绑定/同局、真正到期与原第一张默认摸切等可靠证据后提示；单纯offer消失不代表超时。请一并修正。

超时正例已贴近真实推送：上一次offer快照尚未到期（serverNow=1000/deadline=24000）；下一份直接清offer并将原第一张摸切，响应者时钟serverNow=24001。权威不会保证先推送“offer仍在且serverNow已过期”的中间态，因此不能仅依赖activeBloomOfferExpired曾收到expired offer；请结合清除快照的权威时间及第一张摸切证据判断。该正例测试已更新，仍是14项。

截至13:47，14项袖里藏花专项测试接口已稳定，不再增加本轮需求；按最新测试及本文修复。Codex未运行Gradle，当前仍由Anti独占。旧GameScreenTest高度断言保留，任务中的理牌高44是Codex对原高36错误指令的更正，不需要用户批准。

Codex已修正VISUAL-DESIGN来源文字坐标口径：BitmapFont.draw的y不是字形底部基线；y274画出的字向下延伸会进入按钮顶269。初始参考draw y286，实际字形须落在269与290之间，待GL确认。生产代码由Anti改。
