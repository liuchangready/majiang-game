# 保留的正式牌桌自适应设计绘制示例

此目录由 Codex 编写，用于验证多分辨率设计并指导后续页面开发。**不属于正式启动入口，也不代表已接入正式 APP。** 使用现有正式美术素材与 libGDX/LWJGL 真正绘制，姓名、手牌、点数为固定布局样例。

## 代码职责

| 文件 | 职责 |
| --- | --- |
| ResponsiveTableLayout.java | 纯几何：安全区、16:9等比内容区、每页矩形、输入逆变换、按下/抬起与缩放取消示例 |
| ResponsiveTableRenderer.java | 实际纹理、九宫格、原生字号、桌面/头像/目标卡的GL绘制，边界标注和GPU ID拾取通道 |
| ResponsiveTableDesignTest.java | 创建真实窗口、变换分辨率、截图、几何/字形/GPU点击/图像Alpha边界断言、连续缩放状态检查 |
| responsive-design.init.gradle | 独立编译运行上述Java，复用桌面依赖，不改正式sourceSet和入口 |
| build-review-gallery.cjs | 基于通过的运行结果生成静态截图检视页、位置CSV、设计JSON和报告；不修改PNG |

运行：

```powershell
.\gradlew.bat -I tools/ui-layout/responsive-design.init.gradle :lwjgl3:responsiveDesign --console=plain
node tools/ui-layout/build-review-gallery.cjs
```

默认使用隐藏的真实 GLFW 窗口与其帧缓冲；如要看窗口绘制，加 `-PdesignVisible=true`。隐藏窗口与FBO是不同的：原生窗口捕获和离屏捕获在结果中分别标记。

截图保存在 `doc/ui/round-preparation/responsive-validation/`。详细规范见 `doc/ui/round-preparation/RESPONSIVE-DESIGN.md`。

## 后续页面可以复用的模式

1. 把布局作为纯数据：所有矩形由同一 `View` 变换，绘制和命中不各算一次。
2. 区分环境层、16:9内容层、系统弹窗层；不要让按钮跟窗口右沿漂离桌面。
3. 稳定的页面草稿与窗口几何分开，`resize`只更新视图并取消当前按压；不重置选择和提交状态。
4. 九宫格源切片是纹理像素，目标边框是设计单位，经过同一矩阵缩放。
5. 原生字体根据最终输出像素生成，避免把小字库放大成标题；此示例为画面检查缓存字号，正式 APP 应按需要收敛缓存档位。
6. 先画原图，再保存带边界图；GPU拾取与Alpha读回作为独立位置证据。
7. `Draft`只是交互示例，正式页面仍必须用真实client.api状态和请求回执，不能将示例提交逻辑替换到权威链。

本轮按用户要求保留代码和截图，未向 MiniMax 或其他代理派发，未提交 Git。
