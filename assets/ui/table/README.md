# 正式牌桌美术素材

2026-10-22 由 Codex 使用内置 imagegen 生成并逐张检查。采用已确认的麻将馆、翡翠锦纹、朱红漆木、暖金云纹风格。全部为正式 APP 可用的独立图层；没有烘焙文字、按钮文案、分数、倒计时或实体牌。

## 文件与拼接

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| club-environment.png | 1672×941 | 无人物、无麻将牌、无桌子的环境层；全屏居中 cover |
| jade-table-board.png | 1536×1024 | 透明独立桌面，锦纹桌布、金云纹与漆木桌沿一次拼好 |
| jade-panel-skin.png | 1254×1254 | 深翡翠金饰面板，九宫格用于准备栏/抽屉/回执/席位名牌 |
| button-skins.png | 1536×1024 | 上红下绿两张带金饰的按钮底板；分别取区再九宫格 |
| animal-medallions.png | 1254×1254 | 2×2：章鱼、狐狸 / 熊猫、喜鹊；每格627×627，含完整头像金框 |
| target-card-scenes.png | 1536×1024 | 2×3：红樱/竹林、青山/金龙、雪峰/月云；六种目标背景 |
| round-compass.png | 1254×1254 | 中心圆盘和四向珐琅空牌，局次/方向用代码绘制 |

环境层为不透明图。其余六张均含真实 Alpha；已检查 PNG 尺寸、角像素和图像内容。图像工具显示的深色透明区域不是需要去掉的底板。使用 straight alpha 正常混合，不要按黑色抠图。

`manifest.json` 是精确的源切片、目标坐标、边框宽度、字体与动画参数表，坐标原点统一为左上角。转 libGDX 时 `y = 720 - top - height`，不能将源 PNG 坐标也翻一次。源切片使用 TextureRegion，不需要重新导出或裁切 PNG。

按钮区域是红 `[40,96,1456,384]`、绿 `[40,544,1456,384]`。九宫格源 split 为左右248、上下120，目标保留左右32、上下18。面板源 split 为340，主面板目标四边44；名牌12、卡片18～22、提示条18，不能让340个源像素直接变成340个逻辑单位的边框。

六张目标背景的下方空条用于动态名称，上方用当前 `TileSkin.appendRotatedTileDraw` 绘制真实牌面样式的装饰示例。示例牌只存在于卡片绘制缓冲，不进入牌桌实体/权威/玩家手牌。禁止用三个小矩形加单字代替插画和牌面。

动物头像按公开绝对席位 0/1/2/3 装饰映射章鱼/喜鹊/熊猫/狐狸，不是选角或动物能力功能。名字、分数、庄家与方向以现有公开快照为准。

## 设计和验收入口

- [详细图层与交互交付](../../../doc/ui/round-preparation/VISUAL-COMPOSITION.md)
- [默认准备拼接图](../../../doc/ui/round-preparation/ASSEMBLY-01-entry.png)
- [六种目标拼接图](../../../doc/ui/round-preparation/ASSEMBLY-02-targets.png)
- [确认回执拼接图](../../../doc/ui/round-preparation/ASSEMBLY-03-receipt.png)
- [发牌后衔接拼接图](../../../doc/ui/round-preparation/ASSEMBLY-04-playing.png)

四张 ASSEMBLY 图由实际素材与 SVG 文字/布局渲染，属于生产设计交付图，**不是运行截图**。SVG 是可检查的拼接源文件，不是浏览器 Demo。不要把整张拼接图加载为游戏背景。

源提示词与参考路径见 `generation-prompts.json`。`node tools/ui-assets/build-table-design.cjs` 可重建清单与四张 SVG，不修改任何 PNG 像素；PNG 预览由浏览器渲染对应 SVG 得到。七张源素材 SHA256 和纹理内存估算在清单里。资源由共享 UiAssets 根持有、一次加载，Screen 只借用。
