# DeepSeek 正式 APP UI 续做交付要求

2026-10-22，用户最新授权：“以此作为补充基础设计依据下发给deepseek，让其在当前的基础下完成ui开发，你来检视结果”。

因此，先前设计文件中“暂不交代理”的表述属于上一阶段记录，已由本次明确授权取代。由 DeepSeek 在当前工作区继续完成正式 UI，Codex 独立检视。暂不提交或推送 Git。

## 设计依据顺序

1. `RESPONSIVE-DESIGN.md` 与 `responsive-validation/responsive-spec.json`：多分辨率、窗口缩放、内容区、输入变换、HUD避让和纹理过滤的最新规则。
2. `responsive-validation/` 内38张原始绘制截图、`overview.png`、`positions.csv`、`alpha-bounds.csv`：已验证的位置和效果参照，全部保留，不覆盖。
3. `tools/ui-layout/ResponsiveTableLayout.java`、`ResponsiveTableRenderer.java`、`ResponsiveTableDesignTest.java`：可执行设计参考。不能把其中示例牌、分数、Draft或提交计数移入正式权威链。
4. `VISUAL-COMPOSITION.md`、四张ASSEMBLY、七张`assets/ui/table/`素材：美术与切片依据；其中旧的扩展世界相机、面板锚物理窗口右沿、无mipmap等建议已被新适配规范覆盖。
5. `DESIGN.md`：0/10/30/40点、按需抽屉、失败恢复、旧round回包隔离与即时发牌等行为仍有效。

## 当前代码的继承与实现重点

- 当前未提交的 TableVisualAssets/TableStageRenderer/TableStageLayout/TableHudView、UiAssets、GameScreen、准备栏和二级面板代码是既有开发成果，继续修整，不回滚重做，不清理未跟踪设计素材。
- 将已验证的**中央16:9内容区等比fit，环境cover整个窗口**接入正式GameScreen及SpectatorScreen。准备、抽屉、回执与发牌后都用相同stage；桌子不能被抽屉压窄。
- 绘制、scissor、所有鼠标/触控/键盘焦点与命中共用stage变换。背景环境区没有牌桌点击。审查所有`camera.unproject`调用，不能保留默认全窗口反投影而仅修绘制viewport；牌河提示的局部缩放还需正确组合逆变换。
- 既有落汗/袖里藏花/偷梁换柱二级面板、结算、暂停、退出和观战相关交互也必须在新坐标链中正确工作；不要求重新设计不相关菜单。保留主菜单“被点按钮向右滑、相机背景不动”的已验收行为。
- 采用新HUD宽度和间距，保留真实公开名字/席位、分数、庄家、立直、当前轮次与时钟，不用0.68字号挤进72px名牌。卡图、标题和金边效果对照新绘制截图，不以测试日志代替视觉还原。
- resize仅更新几何并取消正在按住的提交，不重置草稿、看破、权威截止、回执或动画进度；零宽高安全暂停。高DPI区分窗口输入与帧缓冲，不重复乘像素比。
- 原生字号与美术缩小时的mipmap/过滤遵循新方案，生产字体缓存使用有限档位或有界LRU，不能每次拖动窗口新增字体永不释放。共享纹理/字体仍由UiAssets根管理，Screen只借用。
- 单机0点立即确认、电脑及时确认、权威截止与136实体守恒保持，不为动画/回执等待10或15秒。禁止修改引擎/协议/服务端规则来让UI测试通过。

## 真实APP交付与独立复验

先运行保留的设计测试作为参照；再在正式 `MahjongGame → 主菜单 → 单机 → GameScreen` 路径重新截图与点击验证。正式截图保存到新的 `responsive-app-runtime/`，与设计测试截图分开，截图使用真实快照。

最低矩阵：960×540、1280×720、1920×1080、1024×768、1280×800、1600×600、2560×1080。包括准备默认、目标未选/已选、40点、回执、发牌后、动画中段；连续`1280×720→1160×700→1024×768→960×540→1440×810→1600×600→1280×720`，每步真实点击并确认草稿/费用保持，resize中按压不得误提交。每种尺寸的截图必须标记窗口/帧缓冲实际尺寸；原生3440/OS高DPI/最小化恢复如环境不具备，记录缺口，不将离屏模拟称为通过。

编译与验证至少包括：presentation相关用例、桌面classes、Android compileDebugKotlin、target和zero正式GL冒烟；修改共享渲染时验证观战。新增必要的viewport/反投影/取消按压回归，不能只写硬编码坐标与自身相等的镜像测试。报告固定画布绘制测试和真实APP验证各自的结果。

写 `RESPONSIVE-APP-DELIVERY.md`，列出实际变更、命令、测试计数、真实截图、输入命中证据、与新参照图的差异及环境缺口。完成后由Codex独立读diff、复跑、看图；不自行宣称最终验收通过。

源素材、原ASSEMBLY、全部设计绘制代码与截图均保留供用户检查；不改ui_demo，不处理snapshot/table-design-edge缓存，不提交Git。
