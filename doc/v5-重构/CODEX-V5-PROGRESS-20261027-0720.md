# V5 定时检视：P4.4 复验与并发审查协调

日期：2026-10-27 07:20，Asia/Shanghai。调用者 Codex。

## 当前结论与版本

G1 APPROVED；G2–G5 未批准，V4 唯一生产。origin/v5 仍为
8fd7c6cb701dfbb41c28938de4186749bdd1d6c7，PR19 已合并、不重开。
本轮没有批准或集成新的实现。本批仍仅使用 draft PR20，报告前源头
 d2e74fe75bfd752233c2a63f62e3facd26fef859。

全局 recent tasks 仅读取一次，70 条。开始时 P4.4 waiting_review R5，
P4.1 failed R36、P5.1 failed R4，P4.3-A completed/MR 已记录。
提交前一次详情核对发现另一调用链已把同一 P4.4 线程推进至 R6/reworking；
接收方正在原树返修。本轮没有重复发起返修、批准、派单或抢占此范围。

## 分配与依赖

| 线程 | 当前实际路由/状态 | 本轮处理 |
| --- | --- | --- |
| thread_20261027_022047_f12b P4.4 | Anti / gemini-3.8-flash-high，R6 reworking | 保留当前唯一线程与文件预约，待 R6 正式交付再审 |
| thread_20261026_163026_eac6 P4.1 | DeepSeek Harness / deepseek/deepseek-v4.1-flash，failed | RATE_LIMITED，不重复提交 |
| thread_20261026_163027_db9e P5.1 | DeepSeek Harness / z-ai/glm-5.3-flash，failed | 保留用户改过的现有路由，不计作 Claude Code/GLM 活动名额 |
| thread_20261027_032015_62d3 P4.3-A | DeepSeek Flash，completed | 已收录 PR20，不重复批准/分派 |

活动：DeepSeek 指定 Flash **0/2**，Claude Code/GLM **0/1**，Anti **1/1**；
总计 **1/4**。模型目录已核对：DeepSeek 与 Claude Code 指定默认匹配，Anti 默认不变。
两条失败回合均有明确配额重置时间 2026-10-27T11:38:15.851Z，
即北京时间今天 19:38:15.851；未在已知耗尽通道重复重试或替换模型。

P4.3-B 依已接受 P4.3-A 交接仍等待 P4.4 释放 App/注册/asmdef/边界范围；
P4.2-B 等 P4.1 工厂；P4.5 等 P4.1、完整 P4.3 和 P4.4；P5.4b 等 P5.1。
P6.1/P6.2 等 G2+G3，P7 等 G4。没有为填槽另造工作，也没有恢复手动暂停线程。

## 本轮实际独立验证

执行树：D:/majiang-wt/p44-spectator-anti-20261027。
证据根 E：build/p44-caller-r5-review-20261027-0715/。

| 检查 | 实际结果 |
| --- | --- |
| tools/unity/test-p44-views.ps1 -OutputDir E/run | wrapper native0，162.01s；EditMode56/56，PlayMode49/49，0 failed/skipped/inconclusive |
| 静态/StrictCompiled 边界 | PASS；负例13/13，7张新PNG完整且不同hash |
| E/run-independent.ps1，最终运行 | Unity native0，23/23 passed，0 failed/skipped；旧17隐私/结算+旧2布局+4实际分辨率探针 |
| 实际渲染分辨率 | PlayModeWindow.SetCustomRenderingResolution 实际变更 Screen 为1280x720/2560x1080；直接读取帧缓冲，各场景两张新PNG；四席/按钮/局况及部分结算控件边界、生产fitter比例通过 |

四张实际分辨率PNG已逐图可视核验。它们是满屏安全区的 Editor 实跑，
不是带刘海真机；没有覆盖所有非空文本的 preferredHeight。
最终23项是额外探针，不是正式49项的一部分，不扩大正式覆盖口径。

XML SHA256：
- E/run/editmode-results.xml：D22E331564586EB5A1257EF91E4F19D82787C439E55D12016F716EB9676BECD7
- E/run/playmode-results.xml：764FFADEB6A2C67AC7109C412B48B22D44FD98F06BE7059644E0C9A30C2EA4B5
- E/independent-final/results.xml：49AD0E88AF02A716DF46B57FE81EA1A81C68A9CC45929EE04FEBD9E4058303A1

第一次额外探针因当前Unity工程未提供ScreenCapture模块而编译失败，无XML；
原始 E/independent/unity.log 保留。仅改ignored探针为Texture2D.ReadPixels后重跑；
没有改生产程序集或放宽边界。E/evidence-sha256.csv 覆盖本轮独占原始证据。

## 为什么没有批准 P4.4

R6原调用者已实跑更完整的供数文本矩阵并交付同线程返修。
本轮读取其既有 build/p44-caller-r5-review-viewport-matrix/results.xml：
实际 total2/passed0/failed2/skipped0；SHA256 精确匹配
DF437262CA5EB10CD13E26BF367D47620A5C8F60D2209D21B1FAFDF4C36CFB5E。

原 D-1-04 遗留：两种比例均存在 SessionInfoText 43.44>34、四个SeatName
24.62>24、ScoreBreakdownText 43.44>40 的文本高度溢出。
本轮23项没有检查这些全部文本，不能以23/23覆盖2项真实失败。
该缺陷已由原调用者完整报告并派回同线程，不重复返修。
D-1-06 的真实Client重连/旧binding拒绝、旧委托、账本撤权清空及暂停测试
已有接受证据；不要求重写这些已通过功能。

## 并发与工作区保全

本调用者误将审查快照暂存/提交到接收方分支时，发现 R6 已在相同目录继续修改。
没有push该实现，没有approve或record_merge_request。仅撤回自己创建的提交对原分支的影响：
确认HEAD为自己的9122a5c、index为空后，保留快照引用
wt/codex-p44-unaccepted-snapshot-20261027-0718，再mixed reset回原冻结42953c9；
工作文件保留，包括R6新修改。9122a5c为**未接受的混合时间快照**，不得派生任务或集成。

此前清理自动生成项的PowerShell数组包装错误暂时恢复了7个原修改文件；
已经从事先保存的完整patch恢复，逐文件git blob前缀7/7相同，
E/source-restoration-audit.json记录。没有丢弃这些原修改。
新场景/meta的尾空白曾由caller规范化，不改变YAML值；不据此宣称R6已通过。
检测到R6在途后不再清理接收方字体/Resources/slnx/其他生成变化，留给当前owner按其运行归属处理。

本轮报告所在独占树 D:/majiang-wt/codex-p44-integration-20261027-0718：
从最新fetched origin/v5创建，再ff-only到已接受PR20源头d2e74fe；
未合入9122a5c或任何接收方未提交实现。仅本报告沿用PR20发布。

报告前PR20的40415034803、40415027331由GitHub API显示SUCCESS；
本轮未下载其raw产物，故不新增测试计数或PG/Unity通过声明。新报告SHA的CI须另查。
调度任务 Running/PT30M/IgnoreNew，complete.flag不存在。
181 classified未覆盖、Android FAIL、iOS BLOCKED、真机/跨物理网络NOT_RUN保留。
