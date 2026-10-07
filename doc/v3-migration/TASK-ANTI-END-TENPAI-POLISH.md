# 验收返修

本次仅修 core/screen/TenpaiPanelRenderer.kt、FinalResultRenderer.kt、GameScreen.kt，禁止重查全项目，禁止读历史计划，禁止测试/构建/终端。读取这三文件相关段落立即编辑，写 delivery-ANTI-END-TENPAI-POLISH.md 返回。

Codex 已验证 8 个新增 engine 测试、6 个新增 UI 测试通过，真实 OpenGL 也运行通过。但截图有下列确定缺陷，必须修复：

1. TenpaiPanelRenderer 的 240px 宽面板，说明文字超出右边框，标题/说明/牌面挤在一起，24x34 的牌太小。采用约 420px 宽，牌面 32x44、横间隔36，每行7张；单行面板高度126、双行172，保留 x220,y195 底边在操作按钮之上。标题距顶12px，说明距顶40px（字号至少约15px），牌面底行距底14px、第二行高50px；不得裁掉说明文字，可按 glyphLayout 实测换行或扩大宽度。特别检查普通2面、国士13面、原听牌+振听长标题不越界。
2. FinalResultRenderer 未开启 alpha blend，rank row 背景渲染为高亮纯金/纯银/纯红，文字对比度很低。明确开启混合或直接用不透明深色行底，保证金色/白色/浅红文字清晰可读。
3. 总成绩不要把其他座位全部标成 AI（网络真人也走此界面）：只显示“座位1/2/3/4”和“你”的标记；没有权威控制身份则不猜。中文终局原因使用“击飞终局 / 正常终局 / 延长战结束 / 庄家终了”，对应 BANKRUPTCY、NORMAL_COMPLETION、WEST_COMPLETION、AGARI_YAME，不展示括号中的枚举代码。缺 finalResult 不能伪造原因。不要按 score<0 加“击飞”座位标签，因为配置支持零分及其他阈值，现有 DTO 没有逐席击飞信息；负分可以着色。
4. GameScreen touchDown 中 final modal 处理晚于断线门控和 takeover 处理。导致总成绩弹窗可点击穿透请求托管，且断线后总成绩“返回菜单/本局明细”失效。把这些纯本地模态操作放在断线门控和托管处理之前；已终局的末局明细也不得发托管请求。新增测试 final modal swallows underlying takeover and gameplay controls 当前应失败。
5. 总成绩上仍绘制底部计时/托管按钮。整场终局时隐藏托管按钮、底部操作状态盒；总成绩模态遮罩正确挡住底层 UI。保留顶部返回入口、排名和两个实际按钮。

其他已通过行为保持：finished authority判断、明细/总成绩切换与重连恢复、失败下一局保留明细及防重入、待牌缓存从快照更新。engine 不需要再修改。
