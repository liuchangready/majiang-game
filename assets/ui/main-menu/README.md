# 主菜单素材与历史实现说明

本目录保留主菜单视觉素材及其 V4/libGDX 设计记录，不能据此推断旧 libGDX APP 仍可构建或运行。当前产品为 V5 Unity + C#/.NET；Unity 当前 UI 原型导入资源位于 `unity/Mahjong.Unity/Assets/Art/UI/MainMenu/`，由 `unity/Mahjong.Unity/Assets/Editor/V4UiPrototypeBuilder.cs` 和相关 Unity 测试引用。原始方案与交付记录仅描述历史 V4 接入过程：[主菜单交付记录](../../../doc/ui/main-menu/DELIVERY.md)、[主菜单方案](../../../doc/ui/main-menu/DESIGN.md)。

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| `club-background.png` | 1672×941 | 夜间麻将馆背景，RGB 内容、不承载交互 |
| `octopus-hero.png` | 1284×1225 | 有透明通道的章鱼吉祥物，无独立角色能力含义 |
| `button-primary.png` | 96×96 | 朱红主按钮底板，18px 四边 NinePatch |
| `button-secondary.png` | 96×96 | 翡翠次按钮底板，18px 四边 NinePatch |
| `button-focus.png` | 96×96 | 透明金色焦点边框，18px 四边 NinePatch |
| `menu-icons.png` | 384×64 | 六格 64px 图标，顺序：单机、联机、皮肤、秘籍、设置、退出 |
| `manifest.json` | — | libGDX 左下角原点坐标、裁切区域、动画参数和菜单映射 |

底板是普通 PNG，使用 `NinePatch(texture, 18, 18, 18, 18)`，没有 Android `.9.png` 标记边。图标区域使用图片左上角裁切坐标（单行图集的 y 均为 0），菜单位置使用 libGDX 左下角原点。文字由共享中文字体动态绘制，不能贴在底板上。

背景与章鱼为此前内置 imagegen 创建的美术素材，复制后的字节独立存放于本目录。控件和图标为程序绘制的新 UI 图元，可由 [生成脚本](../../../tools/ui-assets/generate-main-menu-controls.ps1) 重建；未修改已有图片。生成方式：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ui-assets/generate-main-menu-controls.ps1`。

Linear 过滤、ClampToEdge、不生成 mipmap；所有纹理边长不超过 2048，兼顾 Android GLES2。以 RGBA8888 保守计算，全部纹理约 12.2 MiB，未计字体/驱动开销。`UiAssets` 已统一持有并释放主菜单资源，Screen/View 借用；禁止每帧创建 Texture。
